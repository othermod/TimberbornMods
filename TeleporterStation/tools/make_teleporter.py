#!/usr/bin/env python3
"""Generates the TeleporterStation teleporter models as loose .timbermesh files.

Timberborn's Timbermesh format (decompiled from Timberborn.Timbermesh[DTO].dll, 1.1.2.7):
  zlib stream (0x78 0x9C) -> protobuf `Model`
    Model { int32 version=1; string name=2; repeated Node nodes=3; }
    Node  { int32 parent=1; string name=2; Vector3Float position=3; QuaternionFloat rotation=4;
            Vector3Float scale=5; int32 vertexCount=6; repeated VertexProperty vertexProperties=7;
            repeated Mesh meshes=8; }
    VertexProperty { string name=1; ScalarType scalarType=2; int32 dimension=3; bytes data=4; }
    Mesh { repeated int32 indices=1 (packed); string material=2; }
  Vertex properties consumed by StaticMeshBuilder by name: "position"(float3), "normal"(float3),
  "uv0"(float2), "color"(float4). Mesh.Material is resolved by MaterialRepository against the
  Unity material's .name: only materials from the Common + active faction MaterialCollection
  are available. Loose files live at the mod root; the asset key is the file name minus
  .timbermesh (lowercased), referenced from TimbermeshSpec.Model.

Look strategy: the faction atlas textures (PaintedMetal.*, BaseWood.*, Details.*) are
addressed by authored UVs, so every part maps UVs into a hand-picked clean region:
ring = one corrugation band of the faction painted metal wrapping the tube,
chevrons = the dark grunge band, ramps = the faction wood plank block,
event horizon = a constant dark texel on the painted metal ("void" look; the water
materials' shader only works through the game's own water rendering, not on meshes).
"""

import math
import struct
import zlib
from pathlib import Path

MOD_ROOT = Path(__file__).resolve().parents[1]

# Tile is 1x1 world units with the origin at its corner; X/Z horizontal, Y up.
R_MAJOR, R_MINOR = 0.44, 0.075   # ring outer radius 0.515 ~= fills the 1x1 tile
CX, CY, CZ = 0.5, R_MAJOR + R_MINOR, 0.5  # ring center: bottom touches the tile floor
U_SEG, V_SEG = 64, 16
HORIZON_R = R_MAJOR - R_MINOR    # horizon fills the ring bore exactly (no gap)
HORIZON_SEG = 48
# With the ring grounded and the horizon at bore radius, the horizon's lower
# edge sits exactly 2*R_MINOR (the toroid thickness) above the floor; the
# ramps rise to meet it there.
RAMP_H = 2 * R_MINOR
RAMP_RUN = 0.32                  # run of each slope; the plateau between the
                                 # two slopes spans 1 - 2*RAMP_RUN, and a
                                 # longer run keeps the taper gradual
RAMP_CHAMFER = 0.06              # side inset: adjacent ramps form a V-groove
RAIL_H = 0.32                    # hand-rail height above the walking surface
RAIL_HALF = 0.017                # rail bar / post cross-section half-width
RAIL_X = 0.14                    # rail offset from the tile edge
# Event-horizon swirl: a dark void disc plus bright spiral arm strips in child
# nodes carrying a "Spin" node animation. The game lerps node rotations with
# Quaternion.Lerp, which does NOT take the shortest path across the ±180°
# quaternion hemisphere: a full 360° spin would visibly kick backward once per
# revolution, so the animation is a gentle sway plus a radial pulse instead,
# which loops cleanly and reads as inward flow over the void.
SPIN_FRAMES, SPIN_FRAMERATE = 12, 3     # 12 frames @ 3 fps = 4 s loop
SWAY_DEG, PULSE = 16.0, 0.05            # ±16° arm sway, ±5% radial breathing
SPIRAL_ARMS, SPIRAL_TWIST = 4, 1.6      # arm count / rim-to-eye curl (radians)
ARM_INNER, ARM_OUTER, ARM_SEG = 0.14, 0.94, 8   # arm radial span (× HORIZON_R)
ARM_HALF_WIDTH = 0.10                   # arm angular half-width at the eye (rad)

VARIANTS = {
    "IronTeeth": {
        # PaintedMetal.IronTeeth: blue corrugated metal, 8 panels per atlas width
        "ring_material": "PaintedMetal.IronTeeth",
        "tread_material": "BaseMetal.IronTeeth",
        "rail_u": 0.9697,        # bright panel texel for the hand rails
        # The game's own dark planks: IrregularPlanks_D_Grey atlas under a warm
        # dark-brown tint (0.31, 0.267, 0.208), a proper dark wood rather than
        # the bright orange PlanksWithMetal the pedestal shipped with. The rect
        # spans exactly two plank cells, edges on measured gap centers, so the
        # walkway tiling seams land on real texture gaps.
        "plinth_material": "IrregularPlanks_DarkBrown.IronTeeth",
        "wood_uv_rect": (0.2681, 0.40, 0.519, 0.58),  # u0, v0, u1, v1
    },
    "Folktails": {
        # PaintedMetal.Folktails: yellow corrugated metal, 8 panels per atlas width
        "ring_material": "PaintedMetal.Folktails",
        "tread_material": "BaseMetal.Folktails",
        "rail_u": 0.5830,
        # Same logic: LightBrown's tinted khaki reads as straw; this atlas is
        # baked walnut planks at full brightness. Gap-centered two-cell rect
        # (measured), matching the IronTeeth tiling scheme.
        "plinth_material": "IrregularPlanks_Brown.Folktails",
        "wood_uv_rect": (0.2261, 0.42, 0.4658, 0.61),   # two plank cells
    },
}

# Flat near-white texel in the Plaster_* atlas (brightest flat 16px block). The
# horizon samples this constant texel so the runtime per-district _Color tint
# lands as a pure hue: tinting multiplies the texture, which would muddy any
# colored base.
HORIZON_MATERIAL = "Plaster_White"
PLASTER_UV = (0.4141, 0.3516)


# ---------------------------------------------------------------- proto wire

def varint(value: int) -> bytes:
    if value < 0:
        value &= (1 << 64) - 1          # negative int32 -> sign-extended 64-bit
    out = bytearray()
    while True:
        b = value & 0x7F
        value >>= 7
        out.append(b | (0x80 if value else 0))
        if not value:
            return bytes(out)


def tag(field: int, wire: int) -> bytes:
    return varint((field << 3) | wire)


def f_string(field: int, value: str) -> bytes:
    data = value.encode("utf-8")
    return tag(field, 2) + varint(len(data)) + data


def f_bytes(field: int, value: bytes) -> bytes:
    return tag(field, 2) + varint(len(value)) + value


def f_message(field: int, value: bytes) -> bytes:
    return f_bytes(field, value)


def f_float(field: int, value: float) -> bytes:
    return tag(field, 5) + struct.pack("<f", value)


def f_varint(field: int, value: int) -> bytes:
    return tag(field, 0) + varint(value)


def packed_ints(field: int, values: list[int]) -> bytes:
    body = b"".join(varint(v) for v in values)
    return tag(field, 2) + varint(len(body)) + body


# ---------------------------------------------------------------- geometry

def torus(variant: dict):
    """Ring torus in the X-Y plane (walk through it along Z).

    UVs: hexagonal tread plate from the BaseMetal atlas. The zone at
    u 0.762-0.997, v 0.248-0.429 holds a 4x2 hexagon cell block; a 4-segment
    mirror wrap turns it into ~15 near-square columns around the ring with 2
    rows along the tube. The grout lines land on every mirror crease and on
    the u wrap, so the mapping is seamless. The rails stay on the faction
    PaintedMetal panel texel for contrast."""
    verts, norms, uvs = [], [], []
    for i in range(U_SEG):
        u = 2 * math.pi * i / U_SEG
        cu, su = math.cos(u), math.sin(u)
        m = abs((i / U_SEG * 4) % 2 - 1)
        tu = 0.762 + m * 0.235
        for j in range(V_SEG):
            v = 2 * math.pi * j / V_SEG
            cv, sv = math.cos(v), math.sin(v)
            verts.append((CX + (R_MAJOR + R_MINOR * cv) * cu,
                          CY + (R_MAJOR + R_MINOR * cv) * su,
                          CZ + R_MINOR * sv))
            norms.append((cu * cv, su * cv, sv))
            vt = (j / V_SEG + 0.5) % 1.0
            uvs.append((tu, 0.248 + vt * 0.181))
    idx = []
    for i in range(U_SEG):
        i2 = (i + 1) % U_SEG
        for j in range(V_SEG):
            j2 = (j + 1) % V_SEG
            a = i * V_SEG + j
            b = i2 * V_SEG + j
            c = i2 * V_SEG + j2
            d = i * V_SEG + j2
            idx += [a, b, c, a, c, d]
    return verts, norms, uvs, [(idx, variant["tread_material"])]


def horizon_disc(variant: dict):
    """Static double-sided "void" disc filling the ring, in its own child node's
    local space (the node sits at the ring center). Flat plaster-white texel:
    water shaders only render through the game's water system and collapse on
    building meshes (verified in game twice), and a neutral base lets the game
    tint the disc to the district's color at runtime (unpaired nodes stay
    hidden entirely)."""
    uv = PLASTER_UV
    verts, norms, uvs = [], [], []
    center_front = len(verts)
    verts.append((0.0, 0.0, 0.004)); norms.append((0.0, 0.0, 1.0)); uvs.append(uv)
    center_back = len(verts)
    verts.append((0.0, 0.0, -0.004)); norms.append((0.0, 0.0, -1.0)); uvs.append(uv)
    for k in range(HORIZON_SEG):
        a = math.tau * k / HORIZON_SEG
        x, y = HORIZON_R * math.cos(a), HORIZON_R * math.sin(a)
        verts.append((x, y, 0.004)); norms.append((0.0, 0.0, 1.0)); uvs.append(uv)
        verts.append((x, y, -0.004)); norms.append((0.0, 0.0, -1.0)); uvs.append(uv)
    idx_front, idx_back = [], []
    for k in range(HORIZON_SEG):
        f0 = 2 + 2 * k
        f1 = 2 + 2 * ((k + 1) % HORIZON_SEG)
        idx_front += [center_front, f0, f1]
        idx_back += [center_back, f1 + 1, f0 + 1]
    material = f"{HORIZON_MATERIAL}.{variant['faction']}"
    return verts, norms, uvs, [(idx_front, material), (idx_back, material)]


def horizon_arms(variant: dict):
    """Four spiral arm strips floating just off the void disc, in an animated
    sibling node. Each arm follows phi(r) = base + SPIRAL_TWIST*(r/R), widening
    slightly toward the eye; constant UVs pin them to the same flat plaster
    texel as the disc so the runtime district tint renders them as a pure
    bright hue against the darkened disc. Double-sided like the rest of the
    model so the back never disappears."""
    uv = PLASTER_UV
    verts, norms, uvs, indices = [], [], [], []

    def add_quad(corners, normal):
        base = len(verts)
        for p in corners:
            verts.append(p); norms.append(normal); uvs.append(uv)
        indices.extend([base, base + 1, base + 2, base, base + 2, base + 3])
        base = len(verts)
        for p in reversed(corners):
            verts.append(p); norms.append(tuple(-c for c in normal)); uvs.append(uv)
        indices.extend([base, base + 1, base + 2, base, base + 2, base + 3])

    for z in (0.010, -0.010):            # arms on both faces of the void disc
        for k in range(SPIRAL_ARMS):
            base_azimuth = math.tau * k / SPIRAL_ARMS

            def azimuth(r: float, side: int) -> float:
                t = r / HORIZON_R
                return base_azimuth + SPIRAL_TWIST * t + side * ARM_HALF_WIDTH * (1.0 - 0.55 * t)

            for s in range(ARM_SEG):
                r0 = HORIZON_R * (ARM_INNER + (ARM_OUTER - ARM_INNER) * s / ARM_SEG)
                r1 = HORIZON_R * (ARM_INNER + (ARM_OUTER - ARM_INNER) * (s + 1) / ARM_SEG)
                corners = []
                for r, side in ((r0, -1), (r0, 1), (r1, 1), (r1, -1)):
                    phi = azimuth(r, side)
                    corners.append((r * math.cos(phi), r * math.sin(phi), z))
                add_quad(corners, (0.0, 0.0, 1.0))
    return verts, norms, uvs, [(indices, f"{HORIZON_MATERIAL}.{variant['faction']}")]


def plinth(variant: dict):
    """Single hip-roof solid under the ring, open bottom, one winding per
    face, nothing coplanar with the terrain or itself. The front/back slopes
    are eased tapers: a 3-segment smoothstep strip with row-averaged normals,
    shallow where it leaves the ground and steepening into the plateau, so
    the profile shades as a curve instead of one 40-degree slab. The walk
    surfaces carry per-plank UV strips (see plank_uv), the flanks sample the
    rect once over their height."""
    u0, v0, u1, v1 = variant["wood_uv_rect"]
    c, H, run = RAMP_CHAMFER, RAMP_H, RAMP_RUN
    seg = 3
    center = (0.5, H / 2, 0.5)
    verts, norms, uvs, idx = [], [], [], []

    def sub(a, b):
        return tuple(p - q for p, q in zip(a, b))

    def cross(a, b):
        return (a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0])

    def dot(a, b):
        return sum(p * q for p, q in zip(a, b))

    def unit(a):
        ln = dot(a, a) ** 0.5
        return tuple(p / ln for p in a)

    def slope_rows(z_ground):
        """Profile rows in increasing z for the slope whose ground edge sits
        at z_ground: (z, y) pairs with a smoothstep ease. The front slope runs
        ground -> plateau, the back one plateau -> ground, so both strips emit
        with the same winding rule."""
        sgn = 1.0 if z_ground < 0.5 else -1.0
        def ease(s):
            return s * s * (3.0 - 2.0 * s)
        return [(z_ground + sgn * run * t, H * ease(t))
                for t in [i / seg if sgn > 0 else 1.0 - i / seg
                          for i in range(seg + 1)]]

    def plank_uv(t, along):
        """UV for the walk surfaces: planks run along `along` (0..1), the
        across direction is measured in flank heights (t = x / H), tiling the
        two-cell rect with a mirror fold every traversal so seams always land
        on the gap-centered rect edges. Each plank takes a shifted window of
        the plank-length grain so neighbours don't repeat identically."""
        m = t % 2.0
        if m > 1.0:
            m = 2.0 - m
        off = (int(t * 2.0) % 3) * 0.15
        return (u0 + m * (u1 - u0), v0 + (off + along * 0.55) * (v1 - v0))

    def emit_slope(rows):
        """Tapered strip, two vertices per profile row (the width inset
        follows height), normals averaged across neighbouring segments so the
        ease shades smooth. The strip is subdivided into per-plank columns
        (seams every half-height in x) because the mirrored tiling and the
        per-plank grain shift both need vertices on every plank seam. Planks
        run up the slope; rows run in increasing z and the quad order
        R,L,L,R keeps the winding CCW outward on both slopes."""
        ins = [c * y / H for _, y in rows]
        pts = [[(i_, y, z), (1.0 - i_, y, z)] for (z, y), i_ in zip(rows, ins)]
        qn = []
        for i in range(len(rows) - 1):
            n = unit(cross(sub(pts[i][0], pts[i][1]),
                           sub(pts[i + 1][0], pts[i][1])))
            mid = [(pts[i][0][k] + pts[i][1][k] + pts[i + 1][0][k]
                    + pts[i + 1][1][k]) / 4 for k in range(3)]
            if dot(n, sub(mid, center)) < 0:
                raise AssertionError("slope strip wound inward")
            qn.append(n)
        rn = [unit(tuple(sum(qn[j][k] for j in (i - 1, i) if 0 <= j < len(qn))
                         for k in range(3))) for i in range(len(rows))]
        arc = [0.0]
        for i in range(1, len(rows)):
            dz = rows[i][0] - rows[i - 1][0]
            dy = rows[i][1] - rows[i - 1][1]
            arc.append(arc[-1] + (dz * dz + dy * dy) ** 0.5)
        arc_max = arc[-1] or 1.0
        seams = [j * H for j in range(int(2.0 / H) + 2)]
        for i in range(len(rows) - 1):
            b_lo, b_hi = ins[i], 1.0 - ins[i]          # visible x, row i
            t_lo, t_hi = ins[i + 1], 1.0 - ins[i + 1]  # visible x, row i+1
            xs_b = [b_lo] + [min(max(e, b_lo), b_hi) for e in seams] + [b_hi]
            xs_t = [t_lo] + [min(max(e, t_lo), t_hi) for e in seams] + [t_hi]
            for j in range(len(xs_b) - 1):
                if xs_b[j + 1] - xs_b[j] < 1e-9 or xs_t[j + 1] - xs_t[j] < 1e-9:
                    continue                     # sliver clipped away by the inset
                corners = [(xs_b[j + 1], rows[i][1], rows[i][0], rn[i], arc[i]),
                           (xs_b[j], rows[i][1], rows[i][0], rn[i], arc[i]),
                           (xs_t[j], rows[i + 1][1], rows[i + 1][0],
                            rn[i + 1], arc[i + 1]),
                           (xs_t[j + 1], rows[i + 1][1], rows[i + 1][0],
                            rn[i + 1], arc[i + 1])]
                base = len(verts)
                for x, y, z, n, a in corners:
                    verts.append((x, y, z)); norms.append(n)
                    uvs.append(plank_uv(x / (2.0 * H), a / arc_max))
                idx.extend([base, base + 1, base + 2, base, base + 2, base + 3])

    def emit_flank(x_side):
        """Planar flank wall: ground-edge bottom, top edge following the
        eased profile (inset tracks height), fan-triangulated with a flat
        normal. Planks run along z as horizontal bands stacked up the wall:
        u spans the wood rect over the wall height, v follows z."""
        step = 1.0 - 2.0 * x_side
        back = list(reversed(slope_rows(1.0)))[1:]    # z descending, no ground row
        front = list(reversed(slope_rows(0.0)))[:-1]  # z descending, no ground row
        poly = [(x_side, 0.0, 0.0), (x_side, 0.0, 1.0)]
        poly += [(x_side + step * (c * y / H), y, z) for z, y in back + front]
        n = unit(cross(sub(poly[1], poly[0]), sub(poly[2], poly[0])))
        cen = [sum(p[k] for p in poly) / len(poly) for k in range(3)]
        if dot(n, sub(cen, center)) < 0:
            n = tuple(-p for p in n)
            poly = poly[::-1]
        for i in range(1, len(poly) - 1):
            base = len(verts)
            for p in (poly[0], poly[i], poly[i + 1]):
                verts.append(p); norms.append(n)
                uvs.append((u0 + (p[1] / H) * (u1 - u0),
                            v0 + p[2] * (v1 - v0)))
            idx.extend([base, base + 1, base + 2])

    emit_slope(slope_rows(0.0))
    emit_slope(slope_rows(1.0))
    emit_flank(0.0)
    emit_flank(1.0)
    # plateau between the two slope tops, subdivided into the same per-plank
    # columns (seams align with the walkway's at the shared edge); planks keep
    # running along z, continuous with the walkway's
    z_lo, z_hi = run, 1.0 - run
    xs = [c] + [e for e in [j * H for j in range(int(2.0 / H) + 2)]
                if c < e < 1.0 - c] + [1.0 - c]
    base = len(verts)
    for j in range(len(xs) - 1):
        for x_side, z_side in ((xs[j], z_lo), (xs[j], z_hi),
                               (xs[j + 1], z_hi), (xs[j + 1], z_lo)):
            verts.append((x_side, H, z_side)); norms.append((0.0, 1.0, 0.0))
            uvs.append(plank_uv(x_side / (2.0 * H),
                                (z_side - run) / (1.0 - 2.0 * run)))
        idx.extend([base, base + 1, base + 2, base, base + 2, base + 3])
        base = len(verts)
    return verts, norms, uvs, [(idx, variant["plinth_material"])]


def rails(variant: dict):
    """Metal hand rails flanking the front entrance only: a sloped bar
    following the ramp plus a short landing bar, each on two posts. The back
    ramp gets none: it is the exit face, not an entrance. Lives in its own
    node so the mesh viewer's ring-pattern override can retune the torus UVs
    without touching the rail texels. Pinned to a bright panel texel of the
    ring material so the rails match whatever the torus samples."""
    verts, norms, uvs, idx = [], [], [], []
    rail_uv = (variant["rail_u"], 0.5)
    # z=0 is the building's front in game (the side the placement camera
    # presents); the z=1 ramp is the exit face and stays bare.
    for z_far in (0.0,):
        sgn = 1.0 if z_far < CZ else -1.0
        z_top = CZ - sgn * RAMP_RUN
        for sx in (RAIL_X, 1.0 - RAIL_X):
            z_edge = z_far + sgn * 0.05
            _rail_box(verts, norms, uvs, idx,
                      (sx, RAIL_H, z_edge), (sx, RAIL_H + RAMP_H, z_top),
                      RAIL_HALF, rail_uv)
            _rail_box(verts, norms, uvs, idx,
                      (sx, RAIL_H + RAMP_H, z_top),
                      (sx, RAIL_H + RAMP_H, CZ - sgn * 0.03),
                      RAIL_HALF, rail_uv)
            # post bases are buried 2 cm into what they stand on: a cap left
            # exactly at y=0 or y=RAMP_H would be coplanar with the terrain /
            # plateau plane and shimmer against it from grazing angles
            _rail_box(verts, norms, uvs, idx,
                      (sx, -0.02, z_edge), (sx, RAIL_H, z_edge),
                      RAIL_HALF, rail_uv)
            _rail_box(verts, norms, uvs, idx,
                      (sx, RAMP_H - 0.02, z_top), (sx, RAIL_H + RAMP_H, z_top),
                      RAIL_HALF, rail_uv)
    return verts, norms, uvs, [(idx, variant["ring_material"])]


def _rail_box(verts, norms, uvs, idx, p0, p1, half, uv):
    """Square-profile bar from p0 to p1 (rails slope only in the Y-Z plane),
    every face pinned to the single metal texel `uv`."""
    dx, dy, dz = (b - a for a, b in zip(p0, p1))
    ln = (dx * dx + dy * dy + dz * dz) ** 0.5
    d = (dx / ln, dy / ln, dz / ln)
    side = (1.0, 0.0, 0.0)
    up = (0.0, d[2], -d[1])            # perpendicular to d, in the Y-Z plane
    corners = []
    for p in (p0, p1):
        for s in (-1.0, 1.0):
            for u in (-1.0, 1.0):
                corners.append((p[0] + s * half * side[0] + u * half * up[0],
                                p[1] + s * half * side[1] + u * half * up[1],
                                p[2] + s * half * side[2] + u * half * up[2]))
    faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 2, 6, 4),
             (1, 5, 7, 3), (0, 4, 5, 1), (2, 3, 7, 6)]
    for a, b, cc, dd in faces:
        base = len(verts)
        quad_pts = (corners[a], corners[b], corners[cc], corners[dd])
        e1 = tuple(q2 - q1 for q1, q2 in zip(quad_pts[0], quad_pts[1]))
        e2 = tuple(q2 - q1 for q1, q2 in zip(quad_pts[0], quad_pts[3]))
        n = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2],
             e1[0] * e2[1] - e1[1] * e2[0])
        ln2 = (n[0] ** 2 + n[1] ** 2 + n[2] ** 2) ** 0.5 or 1.0
        n = (n[0] / ln2, n[1] / ln2, n[2] / ln2)
        for p in quad_pts:
            verts.append(p); norms.append(n); uvs.append(uv)
        idx.extend([base, base + 1, base + 2, base, base + 2, base + 3])


def _vec3(v: tuple[float, float, float]) -> bytes:
    return f_float(1, v[0]) + f_float(2, v[1]) + f_float(3, v[2])


def _quat(q: tuple[float, float, float, float]) -> bytes:
    return (f_float(1, q[0]) + f_float(2, q[1]) + f_float(3, q[2]) + f_float(4, q[3]))


def spin_animation(position: tuple[float, float, float],
                   frames: int = SPIN_FRAMES,
                   framerate: float = SPIN_FRAMERATE) -> bytes:
    """Node.NodeAnimations entry (Node field 10), "Spin": the arm node sways
    ±SWAY_DEG around local Z while pulsing radially ±PULSE, out of phase, so
    the arms read as flowing toward the eye. All keyframes stay inside the
    quaternion hemisphere (see the constants comment for why not a full spin)."""
    out = f_string(1, "Spin") + f_float(2, framerate)
    for k in range(frames):
        phase = math.tau * k / frames
        half_angle = math.radians(SWAY_DEG) * math.sin(phase) / 2.0
        q = (0.0, 0.0, math.sin(half_angle), math.cos(half_angle))
        scale = 1.0 + PULSE * math.sin(phase - math.pi / 2.0)
        out += f_message(3, f_message(1, _vec3(position))      # Position (Vector3Float)
                         + f_message(2, _quat(q))              # Rotation (QuaternionFloat)
                         + f_message(3, _vec3((scale, scale, 1.0))))  # Scale
    return f_message(10, out)


def build_node(name: str, parts, parent: int = -1,
               position: tuple[float, float, float] = (0.0, 0.0, 0.0),
               extra: bytes = b"") -> bytes:
    """parts: callables returning (verts, norms, uvs, submeshes) with submeshes as
    (indices, material) tuples. `extra` is appended raw (used for animations).
    Vertices are in this node's local space; the importer applies parent,
    position, rotation (identity) and scale (one) as transform values."""
    verts: list = []
    norms: list = []
    uvs: list = []
    # One submesh per material per node; every extra submesh is an extra draw
    # call, so index lists sharing a material are merged (chevron bands into the
    # ring, the six pedestal boxes into one frame).
    by_material: dict[str, list[int]] = {}
    for make in parts:
        p_verts, p_norms, p_uvs, submeshes = make()
        base = len(verts)
        verts += p_verts
        norms += p_norms
        uvs += p_uvs
        for indices, material in submeshes:
            _assert_sound_triangles(indices, verts)
            by_material.setdefault(material, []).extend(base + i for i in indices)
    meshes = [f_message(8, packed_ints(1, idxs) + f_string(2, mat))
              for mat, idxs in by_material.items()]

    def prop(pname: str, dim: int, values: list) -> bytes:
        data = b"".join(struct.pack("<" + "f" * dim, *v[-dim:]) for v in values)
        return (f_string(1, pname) + f_varint(2, 4) + f_varint(3, dim)
                + f_bytes(4, data))

    node = (f_varint(1, parent)
            + f_string(2, name)
            + f_message(3, _vec3(position))
            + f_varint(6, len(verts))
            + f_message(7, prop("position", 3, verts))
            + f_message(7, prop("normal", 3, norms))
            + f_message(7, prop("uv0", 2, uvs)))
    node += b"".join(meshes)
    node += (f_message(4, f_float(4, 1.0))      # Rotation identity (0,0,0,1)
             + f_message(5, f_float(1, 1.0) + f_float(2, 1.0) + f_float(3, 1.0)))  # Scale
    return node + extra


def _assert_sound_triangles(indices: list[int], verts: list) -> None:
    """Zero-area triangles render nothing but pass structural checks; this guard
    exists because exactly that bug shipped an invisible pedestal once."""
    for t in range(0, len(indices), 3):
        a, b, c = (verts[indices[t + k]] for k in range(3))
        if a == b or b == c or a == c:
            raise AssertionError(f"degenerate triangle at output triangle {t // 3}")


def make_model(name: str, nodes: list[bytes]) -> bytes:
    return f_varint(1, 1) + f_string(2, name) + b"".join(f_message(3, n) for n in nodes)


# ---------------------------------------------------------------- validation

def validate(blob: bytes, name: str) -> None:
    assert blob[0] == 0x78 and blob[1] == 0x9C, "missing zlib header"
    raw = zlib.decompress(blob)

    pos = 0
    def rd_varint() -> int:
        nonlocal pos
        shift, out = 0, 0
        while True:
            b = raw[pos]; pos += 1
            out |= (b & 0x7F) << shift
            if not b & 0x80:
                return out
            shift += 7
    def rd_field():
        nonlocal pos
        key = rd_varint()
        field, wire = key >> 3, key & 7
        if wire == 0: return field, rd_varint()
        if wire == 5: pos += 4; return field, None
        if wire == 2:
            n = rd_varint()
            data = raw[pos:pos + n]; pos += n
            return field, data
        raise AssertionError(f"unexpected wire type {wire}")

    model_fields = {}
    while pos < len(raw):
        f, v = rd_field()
        model_fields.setdefault(f, []).append(v)
    assert 1 in model_fields and model_fields[1][0] == 1, "version"
    nodes = model_fields.get(3, [])
    assert nodes, "no nodes"

    total_verts = total_meshes = total_anims = 0
    for node_idx, node in enumerate(nodes):
        raw = node; pos = 0
        node_fields = {}
        while pos < len(raw):
            f, v = rd_field()
            node_fields.setdefault(f, []).append(v)
        parent = node_fields.get(1, [-1])[0]
        if parent >= 1 << 63:                     # sign-extended int32 varint
            parent -= 1 << 64
        assert -1 <= parent < len(nodes), "parent index out of range"
        vcount = node_fields[6][0]
        props = {}
        for data in node_fields.get(7, []):
            p = 0
            n_raw = data
            def rd2():
                nonlocal p
                shift, out = 0, 0
                while True:
                    b = n_raw[p]; p += 1
                    out |= (b & 0x7F) << shift
                    if not b & 0x80: return out
                    shift += 7
            fields = {}
            while p < len(n_raw):
                key = rd2(); f, wire = key >> 3, key & 7
                if wire == 0: fields[f] = rd2()
                elif wire == 5: p += 4
                elif wire == 2:
                    ln = rd2(); fields[f] = n_raw[p:p + ln]; p += ln
                else: raise AssertionError("bad wire")
            props[fields[1].decode()] = fields[4]
        assert len(props["position"]) == vcount * 12, "position size"
        assert len(props["normal"]) == vcount * 12, "normal size"
        assert len(props["uv0"]) == vcount * 8, "uv0 size"
        max_index = 0
        mesh_count = 0
        for data in node_fields.get(8, []):
            mesh_count += 1
            p = 0; n_raw = data
            def rd3():
                nonlocal p
                shift, out = 0, 0
                while True:
                    b = n_raw[p]; p += 1
                    out |= (b & 0x7F) << shift
                    if not b & 0x80: return out
                    shift += 7
            fields = {}
            while p < len(n_raw):
                key = rd3(); f, wire = key >> 3, key & 7
                if wire == 0: fields[f] = rd3()
                elif wire == 2:
                    ln = rd3()
                    if f == 1:
                        chunk = n_raw[p:p + ln]
                        q = 0
                        idxs = []
                        while q < len(chunk):
                            shift, out = 0, 0
                            while True:
                                b = chunk[q]; q += 1
                                out |= (b & 0x7F) << shift
                                if not b & 0x80: break
                                shift += 7
                            idxs.append(out)
                        assert all(i < vcount for i in idxs), "index out of range"
                        max_index = max(max_index, max(idxs)) if idxs else max_index
                    else:
                        fields[f] = n_raw[p:p + ln]
                    p += ln
                else: raise AssertionError("bad wire")
            assert 2 in fields and fields[2], "mesh without material"
        for anim in node_fields.get(10, []):          # NodeAnimations
            total_anims += 1
            p = 0; n_raw = anim

            def rd4() -> int:
                nonlocal p
                shift, out = 0, 0
                while True:
                    b = n_raw[p]; p += 1
                    out |= (b & 0x7F) << shift
                    if not b & 0x80: return out
                    shift += 7

            frame_count = 0
            while p < len(n_raw):
                key = rd4(); f, wire = key >> 3, key & 7
                if wire == 0: rd4()
                elif wire == 5: p += 4
                elif wire == 2:
                    ln = rd4()
                    if f == 3: frame_count += 1        # repeated NodeAnimationFrame
                    p += ln
                else: raise AssertionError("bad wire")
            assert frame_count > 0, "node animation without frames (game throws)"
        total_verts += vcount
        total_meshes += mesh_count
    print(f"  {name}: {len(nodes)} nodes, {total_verts} verts, {total_meshes} submeshes, "
          f"{total_anims} node animations, OK")


def main() -> None:
    for faction, variant in VARIANTS.items():
        name = f"Teleporter.{faction}"
        variant.setdefault("faction", faction)
        root = build_node(name, [
            lambda: torus(variant),
            lambda: plinth(variant),
        ])
        rails_node = build_node("Rails", [lambda: rails(variant)], parent=0)
        horizon_pos = (CX, CY, CZ)
        disc = build_node("Horizon", [lambda: horizon_disc(variant)],
                          parent=0, position=horizon_pos)
        arms = build_node("Arms", [lambda: horizon_arms(variant)],
                          parent=0, position=horizon_pos,
                          extra=spin_animation(horizon_pos))
        blob = zlib.compress(make_model(name, [root, rails_node, disc, arms]), 6)
        validate(blob, name)
        out = MOD_ROOT / f"{name}.timbermesh"
        out.write_bytes(blob)
        print(f"wrote {out}")


if __name__ == "__main__":
    main()
