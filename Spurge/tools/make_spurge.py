"""Generates the Spurge plant's seven .timbermesh models into NaturalResources/.

The spurge is a Euphorbia candelabra (the succulent's corrupted sibling):
one stout three-wing trunk with beaded segment joints that continues as the
central leader, plus three arms forking steeply off the trunk's own wing
planes. Every column is one watertight solid: a closed star ring per band,
side quads sharing edges, a per-tri crown cap on top, a buried cap below;
the build asserts manifold edges (every edge used by exactly two triangles)
on every model, so there are no cracks. All detail is painted per facet as
one flat Succulent-atlas texel (constant uv0, vanilla crispness) times a
vertex tint: green lobed body with light crests and shaded valleys, maroon
contamination blush rooted at the base and climbing higher in the valleys,
olive collars, green crowns, pale-grey dead husks, cream dried-latex cut
faces. Crown faces carry the uv2 droplet-blob detail the GatherableModel
toggles. DESIGN.md#the-plant holds the design; docs/plant-models.md the
format facts. python3, stdlib only; deterministic.
"""

import math
import struct
import zlib
from pathlib import Path

MOD_ROOT = Path(__file__).resolve().parent.parent
MESH_DIR = MOD_ROOT / "NaturalResources" / "Trees" / "Spurge" / "Mesh"
MATERIAL = "Succulent"

# ---------------------------------------------------------------- palette
# uv points on the game's Succulent_D atlas with their sampled rgb, baked.
# The body paints from the leaf cell's greens; the blush rides the leaf
# cell's red rim; crowns mix the light greens; dead husks and cut faces
# sample the washed greys and browns. Tints darken only.

ATLAS = {
    "dark_edge": ((0.501, 0.816), (33, 16, 8)),
    "tan_mid": ((0.627, 0.30), (138, 112, 92)),
    "scale_f": ((0.61, 0.80), (139, 113, 92)),
    "scale_c": ((0.76, 0.62), (112, 90, 70)),
    "scale_a": ((0.56, 0.60), (110, 94, 86)),
    "scale_b": ((0.66, 0.71), (85, 66, 56)),
    "scale_d": ((0.86, 0.70), (84, 64, 58)),
    "groove_c": ((0.83, 0.66), (78, 62, 56)),
    "groove_d": ((0.90, 0.76), (85, 57, 40)),
    "hi_c": ((0.97, 0.86), (65, 39, 21)),
    "dry_scale_a": ((0.56, 0.10), (106, 83, 75)),
    "dry_scale_f": ((0.61, 0.30), (130, 105, 86)),
    "dry_scale_b": ((0.66, 0.21), (80, 58, 50)),
    "dry_groove_c": ((0.83, 0.16), (84, 62, 54)),
    "dry_groove_d": ((0.90, 0.26), (83, 58, 42)),
    "blush_a": ((0.457, 0.832), (121, 71, 57)),
    "blush_b": ((0.45, 0.70), (119, 67, 59)),
    "blush_c": ((0.44, 0.75), (118, 80, 62)),
    "blush_low": ((0.43, 0.88), (124, 73, 59)),
    "blush_deep2": ((0.45, 0.86), (117, 67, 53)),
    "green_core": ((0.256, 0.756), (115, 129, 95)),
    "green_light": ((0.113, 0.794), (110, 119, 72)),
    "green_hi": ((0.30, 0.88), (112, 122, 93)),
    "green_deep": ((0.20, 0.68), (109, 123, 90)),
    "green_shadow": ((0.38, 0.72), (107, 119, 81)),
    "green_core_d": ((0.256, 0.256), (124, 124, 94)),
    "green_light_d": ((0.113, 0.294), (126, 114, 76)),
    "green_hi_d": ((0.30, 0.38), (118, 115, 92)),
    "green_deep_d": ((0.20, 0.18), (121, 121, 93)),
    "green_shadow_d": ((0.38, 0.22), (129, 124, 94)),
    "tip_olive_d": ((0.36, 0.30), (102, 101, 74)),
    "blush_d": ((0.457, 0.332), (120, 62, 57)),
    "blush_edge_d": ((0.40, 0.18), (113, 84, 64)),
    "dead_tan": ((0.724, 0.388), (180, 169, 160)),
    "dead_tan2": ((0.85, 0.25), (77, 47, 28)),
    "dead_dark": ((0.988, 0.327), (43, 23, 13)),
}

GREENS = ("green_deep", "green_core", "green_hi", "green_light")
GREENS_D = ("green_deep_d", "green_core_d", "green_hi_d", "green_light_d")
BLUSHES = ("blush_low", "blush_a", "blush_c", "blush_deep2")
BLUSHES_D = ("blush_d", "blush_d", "blush_d", "blush_d")

# uv2 for the detail layer: the amber droplet art in Maple_Detail (the
# Succulent material's _DetailMap) and a guaranteed-transparent corner.
BLOB_UVS = [(0.504, 0.468), (0.504, 0.495), (0.504, 0.515)]
NULL_UV2 = (0.03, 0.03)

WIND_U = 0.47          # succulent parity: phase ~0.47, tiny bend growing with y
WIND_BEND_MAX = 0.012

# Preview color tables: every uv point keyed with its rgb (tints multiply).
ALIVE_TEXELS = {k: (uv, rgb, (1.0, 1.0, 1.0)) for k, (uv, rgb) in ATLAS.items()}
DRY_TEXELS = dict(ALIVE_TEXELS)
DEAD_TEXELS = dict(ALIVE_TEXELS)


def mottle(*key):
    """Deterministic 0..1 hash; drives every per-face color variation."""
    h = 0
    for k in key:
        h = (h * 1000003 + int(k * 83492791)) & 0xFFFFFFFF
    return ((h ^ (h >> 13)) * 1274126177 & 0xFFFFFFFF) / 0xFFFFFFFF


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


def packed_ints(field: int, values: list) -> bytes:
    body = b"".join(varint(v) for v in values)
    return tag(field, 2) + varint(len(body)) + body


def _vec3(v):
    return f_float(1, v[0]) + f_float(2, v[1]) + f_float(3, v[2])


# ---------------------------------------------------------------- mesh kit
# Flat-shaded like the vanilla plants; quads share their four vertices
# between the two triangles, so vertex counts stay near the vanilla scale.


class Node:
    def __init__(self, name):
        self.name = name
        self.pos = []
        self.nrm = []
        self.tan = []
        self.col = []
        self.uv0 = []
        self.uv1 = []
        self.uv2 = []
        self.indices = []

    def _face(self, pts, uv, tint, uv2, normal):
        # The game shows faces whose winding cross points outward (verified
        # against the vanilla succulent mesh); honor the authored normal.
        if _dot(_normal(pts[0], pts[1], pts[2]), normal) < 0:
            pts = [pts[0]] + pts[1:][::-1]
            if len(uv) > 2:
                uv = [uv[0]] + uv[1:][::-1]
        base = len(self.pos)
        tangent = _tangent(pts, normal)
        per_vertex = len(uv) > 2
        uvs = uv if per_vertex else [uv] * len(pts)
        for k, p in enumerate(pts):
            self.pos.append(p)
            self.nrm.append(normal)
            self.tan.append(tangent)
            self.col.append((tint[0], tint[1], tint[2], 1.0))
            self.uv0.append(uvs[k])
            self.uv1.append((WIND_U, max(0.0, p[1]) / 1.35 * WIND_BEND_MAX + 0.0005))
            self.uv2.append(uv2)
        if len(pts) == 3:
            self.indices += [base, base + 1, base + 2]
        else:
            self.indices += [base, base + 1, base + 2, base, base + 2, base + 3]

    def quad(self, p0, p1, p2, p3, uv, tint, uv2=NULL_UV2, outward=None):
        n = _normal(p0, p1, p2)
        if outward is not None and _dot(n, outward) < 0:
            n = (-n[0], -n[1], -n[2])
            p1, p3 = p3, p1
            if len(uv) > 2:
                uv = [uv[0], uv[3], uv[2], uv[1]]
        self._face([p0, p1, p2, p3], uv, tint, uv2, n)

    def tri(self, p0, p1, p2, uv, tint, uv2=NULL_UV2, outward=None):
        n = _normal(p0, p1, p2)
        if outward is not None and _dot(n, outward) < 0:
            n = (-n[0], -n[1], -n[2])
        self._face([p0, p1, p2], uv, tint, uv2, n)

    def stats(self):
        xs = [p[0] for p in self.pos]
        ys = [p[1] for p in self.pos]
        zs = [p[2] for p in self.pos]
        return {"verts": len(self.pos), "tris": len(self.indices) // 3,
                "min": (min(xs), min(ys), min(zs)), "max": (max(xs), max(ys), max(zs))}


def _normal(a, b, c):
    u = (b[0] - a[0], b[1] - a[1], b[2] - a[2])
    v = (c[0] - a[0], c[1] - a[1], c[2] - a[2])
    n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
    l = math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2) or 1.0
    return (n[0] / l, n[1] / l, n[2] / l)


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _tangent(pts, normal):
    # Unit tangent along the face's first edge, orthogonalized against the
    # normal, w=-1 like the vanilla plants ship.
    ex = (pts[1][0] - pts[0][0], pts[1][1] - pts[0][1], pts[1][2] - pts[0][2])
    d = _dot(ex, normal)
    t = (ex[0] - d * normal[0], ex[1] - d * normal[1], ex[2] - d * normal[2])
    l = math.sqrt(t[0] ** 2 + t[1] ** 2 + t[2] ** 2) or 1.0
    return (t[0] / l, t[1] / l, t[2] / l, -1.0)


# ---------------------------------------------------------------- paint

class Paint:
    """Per-column paint state: zone heights, stage tint, atlas table.

    Zones top to bottom: crown (light greens), collar (olive), body (green
    lobed: light crests, shaded valleys, dark wing edges), fade, blush and
    root (the maroon contamination creeping from the ground, higher in the
    valleys than on the crests). Dead columns skip the zones entirely.
    """

    def __init__(self, texels, crown_y, blush, col_bias=0.0, tint=(1.0, 1.0, 1.0),
                 dry=False, dead=False, blob_tell=False):
        self.t = texels
        self.crown_y = crown_y
        self.blush = blush + col_bias
        self.tint = tint
        self.dry = dry
        self.dead = dead
        self.blob_tell = blob_tell

    def uv(self, name):
        return self.t[name][0]

    def zone(self, y, m, margin):
        crown_line = self.crown_y + 0.09 * (m - 0.5)
        if y > crown_line:
            return "crown"
        if crown_line - y < 0.07:
            return "collar"
        # blush boundary: valleys stay blushed higher than crests
        line = self.blush + 0.10 * (m - 0.5) + (0.05 if margin else -0.02)
        if y < 0.12:
            return "root"
        if y < line:
            return "blush"
        if y < line + 0.12:
            return "fade"
        return "body"

    def facet(self, col_id, band, s, y0, y1, s_class, near_crown):
        """One texel point per face (vanilla's constant-uv0 crispness); tints
        do the shading. s_class 0 = wing edge, 1/2 = broad faces."""
        m = mottle(col_id, band, s)
        m2 = mottle(col_id * 7 + 3, band, s)
        margin = (s_class == 0)
        if self.dead:
            # bleached husk: warm grey body, brown patches, dark cracks
            if m2 > 0.8:
                pt = "dead_tan"
            elif s % 3 == 2:
                pt = "dead_dark"
            else:
                pt = "dead_tan2"
            return [self.uv(pt)] * 4, (0.84, 0.80, 0.76)
        zone = self.zone((y0 + y1) / 2, m, margin)
        if zone == "body" and near_crown:
            zone = "crown"
        j = 0.92 + 0.08 * m2
        u = self.uv
        if zone == "crown":
            g = GREENS_D if self.dry else GREENS
            pt = g[3] if m2 > 0.85 else g[int(m2 * 3) % 3]
            tint = (0.74 * j + 0.06, 0.80 * j + 0.04, 0.66 * j + 0.10)
        elif zone == "collar":
            pt = "green_shadow"
            tint = (0.70 * j, 0.76 * j, 0.62 * j)
        elif zone == "root":
            pt = ("dark_edge", "blush_deep2")[int(m2 * 2) % 2]
            tint = (0.9, 0.78, 0.75)
        elif zone == "blush":
            names = BLUSHES_D if self.dry else BLUSHES
            pt = names[3] if m2 > 0.55 else names[int(m2 * 3) % 3]
            tint = (0.72, 0.52, 0.50)
        elif zone == "fade":
            if margin:
                pt, tint = "blush_b", (0.74, 0.54, 0.52)
            else:
                pt = "groove_c"
                tint = (0.76, 0.70, 0.66)
        else:
            y_mid = (y0 + y1) / 2
            green_line = self.blush + 0.40
            on_crest = (s_class == 1)
            if y_mid > green_line or (on_crest and y_mid > green_line - 0.16):
                # upper stem: the leaf greens take over (fresh growth)
                if margin:
                    pt = "green_shadow_d" if self.dry else "green_shadow"
                    tint = (0.70 * j, 0.75 * j, 0.62 * j)
                else:
                    if on_crest and m2 > 0.8:
                        pt = "green_light_d" if self.dry else "green_light"
                        tint = (0.82, 0.82, 0.66)
                    else:
                        pt = ("green_core_d" if self.dry else "green_core") \
                            if on_crest else ("green_deep_d" if self.dry
                                              else "green_deep")
                        tint = ((0.72, 0.78, 0.60) if on_crest
                                else (0.58, 0.64, 0.54))
                    tint = tuple(t * k for t, k in zip(tint, (j, j, j)))
            else:
                # lower body: the succulent's scale browns (its own language)
                if margin:
                    pt = "groove_d"
                    tint = (0.70 * j, 0.70 * j, 0.70 * j)
                elif m2 > 0.82:
                    pt = "scale_f"
                    tint = (0.82 * j, 0.78 * j, 0.72 * j)
                elif m2 > 0.30:
                    pt = "scale_c" if s_class == 1 else "scale_a"
                    tint = (0.80 * j, 0.78 * j, 0.74 * j)
                elif m2 > 0.12:
                    pt = "scale_b" if s_class == 1 else "scale_d"
                    tint = (0.72 * j, 0.72 * j, 0.72 * j)
                else:
                    pt = "groove_c"
                    tint = (0.62 * j, 0.60 * j, 0.60 * j)
                if self.dry:
                    pt = {"scale_f": "dry_scale_f", "scale_c": "dry_scale_a",
                          "scale_a": "dry_scale_a", "scale_b": "dry_scale_b",
                          "scale_d": "dry_scale_b", "groove_c": "dry_groove_c",
                          "groove_d": "dry_groove_d"}[pt]
        return [u(pt)] * 4, tint


# ---------------------------------------------------------------- winged columns

WINGS = 3                  # trigona-true
# Valley radius must stay outside the chord between adjacent fin tips, or the
# cross section turns concave at the valleys and the game's backface culling
# opens see-through notches from the steep camera. With FIN_HALF=0.24 the
# chord sits at 0.692 R, so depth stays under 0.308; 0.26 keeps margin.
WING_DEPTH = 0.26          # valley radius = 0.74 R: convex lobes
FIN_HALF = 0.24            # fin angular half-width (rad)


def star_ring(cx, cz, r, y, phase):
    """Closed 9-point cross section: per wing a [tip, tip] blade edge at
    full radius, then one valley vertex between wings at 0.74 R. Facet
    classes: s % 3 == 0 is the wing edge, 1/2 the broad faces. The ring is
    convex (see the WING_DEPTH note), so no side face can turn back-facing."""
    pts = []
    for k in range(WINGS):
        base = phase + k * 2 * math.pi / WINGS
        for ang, rad in ((base - FIN_HALF, r), (base + FIN_HALF, r),
                         (base + math.pi / WINGS, r * (1.0 - WING_DEPTH))):
            pts.append((cx + rad * math.cos(ang), y, cz - rad * math.sin(ang)))
    return pts


def check_convex(node, name):
    """Build guard: every horizontal ring turn must keep the same sign, so
    the cross section stays convex and the game's culling cannot open holes."""
    rings = {}
    for p in node.pos:
        rings.setdefault(round(p[1], 4), []).append((p[0], p[2]))
    for y, pts in rings.items():
        seen = set()
        poly = []
        for x, z in pts:
            if (x, z) not in seen:
                seen.add((x, z))
                poly.append((x, z))
        if len(poly) < 3:
            continue
        cx = sum(p[0] for p in poly) / len(poly)
        cz = sum(p[1] for p in poly) / len(poly)
        poly.sort(key=lambda p: math.atan2(p[1] - cz, p[0] - cx))
        sign = 0
        n = len(poly)
        for i in range(n):
            ax, az = poly[i][0] - cx, poly[i][1] - cz
            bx, bz = poly[(i + 1) % n][0] - cx, poly[(i + 1) % n][1] - cz
            cross = ax * bz - az * bx
            if cross:
                s = 1 if cross > 0 else -1
                if sign and s != sign:
                    raise AssertionError(f"{name}: concave ring at y={y}")
                sign = s


def solid_column(node, paint, col_id, spine, phase, top_jag=False,
                 jag_dip=0.0, cut=False, dome=0.45):
    """One watertight winged column: closed star rings extruded per band,
    side quads sharing edges, crown cap on top, buried cap at the base.
    Returns (top ring, top spine point)."""
    jag = (1.0, 0.78, 0.55) if top_jag else None
    rings = [star_ring(c[2], c[3], c[1], c[0], phase + 0.02 * b)
             for b, c in enumerate(spine[:-1])]
    rings.append(star_ring(spine[-1][2], spine[-1][3], spine[-1][1],
                           spine[-1][0], phase + 0.02 * (len(spine) - 1)))
    n = len(rings[0])
    for band in range(len(spine) - 1):
        r0, r1 = rings[band], rings[band + 1]
        y0, y1 = spine[band][0], spine[band + 1][0]
        near = abs(y1 - paint.crown_y) < 0.26 or abs(y0 - paint.crown_y) < 0.26
        for s in range(n):
            uvs, tint = paint.facet(col_id, band, s, y0, y1, s % 3, near)
            node.quad(r0[s], r0[(s + 1) % n], r1[(s + 1) % n], r1[s],
                      uvs, tint, NULL_UV2)
    if top_jag:
        broken_cap(node, paint, col_id, rings[-1], spine[-1], jag_dip, cut)
    else:
        crown_cap(node, paint, col_id, rings[-1], spine[-1], dome)
    base_cap(node, paint, rings[0], spine[0])
    return rings[-1], spine[-1]


def crown_cap(node, paint, col_id, ring, top, dome=0.45):
    """Two-tier crown dome: a green inner dome (apex to a mid ring) ringed by
    an outer band of facets. On the mature alive stage the outer band carries
    the amber latex beads (uv2 into the material's detail blob) so a ready
    crown shows a halo of droplets beading around its rim against a green
    center; every other crown stays detail-free, because the game only forces
    _EnableDetail on Mature-stage renderers."""
    cx, cz, r, y = top[2], top[3], top[1], top[0]
    n = len(ring)
    mid = [(cx + (p[0] - cx) * 0.5, y + 0.22 * r, cz + (p[2] - cz) * 0.5)
           for p in ring]
    apex = (cx, y + dome * r, cz)
    tell = paint.blob_tell and not paint.dry
    for s in range(n):
        m = mottle(col_id, 88, s)
        if paint.dry:
            uv = paint.uv("green_light_d" if m > 0.8 else
                          ("tip_olive_d" if s % 2 else "green_core_d"))
        else:
            uv = paint.uv("green_light" if m > 0.75 else
                          ("green_hi" if s % 2 else "green_core"))
        node.tri(apex, mid[s], mid[(s + 1) % n], uv, (0.95, 0.98, 0.90),
                 NULL_UV2, outward=(0.0, 1.0, 0.0))
        bead = BLOB_UVS[1] if tell and s % 3 != 2 else NULL_UV2
        node.quad(mid[s], ring[s], ring[(s + 1) % n], mid[(s + 1) % n],
                  [uv] * 4, (0.9, 0.95, 0.86), bead)


def broken_cap(node, paint, col_id, ring, top, dip, cut=False):
    """Jagged broken top for dead stages; the stump's cut face reads as
    dried latex: pale crust with a few dark flecks."""
    apex = (top[2], top[0] - dip, top[3])
    n = len(ring)
    for s in range(n):
        if cut:
            uv = paint.uv("dead_tan" if s % 3 else "dead_tan2")
            tint = (1.0, 1.0, 1.0)
        else:
            m = mottle(col_id, 99, s)
            uv = paint.uv("dead_tan" if m > 0.6 else
                          ("dead_tan2", "dead_dark")[s % 2])
            tint = (0.85, 0.82, 0.80)
        node.tri(apex, ring[s], ring[(s + 1) % n], uv, tint,
                 NULL_UV2, outward=(0.0, 1.0, 0.0))


def base_cap(node, paint, ring, base):
    """Buried cap closing the column below ground; hidden, painted dark."""
    tip = (base[2], base[0] - 0.05, base[3])
    n = len(ring)
    for s in range(n):
        node.tri(tip, ring[s], ring[(s + 1) % n], paint.uv("dark_edge"),
                 (0.03, 0.03, 0.03), NULL_UV2, outward=(0.0, -1.0, 0.0))


# ---------------------------------------------------------------- models
# Tile is 1x1 with origin at the corner; the base sits a little below y=0.
# One stout beaded trunk that continues as the central leader, three arms
# forking off the trunk's wing planes at 45-60 degrees and upturning.
# Branch roots start buried inside the trunk so the crotches read merged.

TRUNK = [(-0.15, 0.165, 0.5, 0.5), (0.05, 0.215, 0.5, 0.5),
         (0.30, 0.165, 0.5, 0.5), (0.42, 0.225, 0.5, 0.5),
         (0.68, 0.225, 0.5, 0.5), (0.90, 0.150, 0.5, 0.5),
         (0.98, 0.175, 0.5, 0.5), (1.24, 0.085, 0.5, 0.5)]
TRUNK_CROWN = 1.00
PHASE = 0.31

TIP_ANGLES = [PHASE + k * 2 * math.pi / WINGS for k in range(WINGS)]
BRANCHES = [
    dict(y0=0.44, reach=0.44, tip_y=1.10, r0=0.170, crown=0.70),
    dict(y0=0.56, reach=0.40, tip_y=1.18, r0=0.160, crown=0.78),
    dict(y0=0.50, reach=0.46, tip_y=1.04, r0=0.175, crown=0.64),
]
R_TIP = 0.115


def arm_spine(y0, ang, reach, tip_y, r0):
    """Arm leaving a trunk wing plane: kicks out at 45-60 deg, one bead
    joint mid-arm (the cooperi/ingens segmentation), then upturns."""
    a = math.radians(ang)
    dx, dz = math.cos(a), -math.sin(a)
    pts = []
    steps = 4
    for i in range(steps + 1):
        t = i / steps
        off = (t ** 1.15) * reach
        h = y0 + (tip_y - y0) * (t ** 1.35)
        r = r0 + (R_TIP - r0) * t
        if i == 2:
            r *= 0.82
        pts.append((h, r, 0.5 + dx * off, 0.5 + dz * off))
    return pts


def wilt(spine, amount=1.0):
    """Droop, thin and fold a spine for the dry stage; ribs shrivel."""
    top = max(c[0] for c in spine)

    def ease(t):
        return t * t * (3.0 - 2.0 * t)

    out = []
    for (y, r, cx, cz) in spine:
        t = ease(max(0.0, y - 0.35) / max(0.01, top - 0.35))
        dx, dz = cx - 0.5, cz - 0.5
        l = math.sqrt(dx * dx + dz * dz) or 1.0
        out.append((y - 0.075 * t * amount, r * (1.0 - 0.13 * t * amount),
                    cx + dx / l * 0.06 * t * amount,
                    cz + dz / l * 0.06 * t * amount))
    return out


def build_mature(dry=False):
    texels = DRY_TEXELS if dry else ALIVE_TEXELS
    n = Node("SpurgeMature" + ("Dry" if dry else ""))
    stage_tint = (0.94, 0.90, 0.86) if dry else (1.0, 1.0, 1.0)
    trunk = wilt(TRUNK, 0.5) if dry else TRUNK
    paint = Paint(texels, TRUNK_CROWN, 0.30, 0.0, stage_tint, dry,
                  blob_tell=True)
    solid_column(n, paint, 0, trunk, PHASE)
    for i, br in enumerate(BRANCHES):
        spine = arm_spine(br["y0"], math.degrees(TIP_ANGLES[i]), br["reach"],
                          br["tip_y"], br["r0"])
        if dry:
            spine = wilt(spine, 0.9)
        bpaint = Paint(texels, br["crown"], 0.30, 0.0, stage_tint, dry,
                       blob_tell=True)
        solid_column(n, bpaint, i + 1, spine, PHASE)
    return n


DEAD_TRUNK = [(-0.14, 0.105, 0.500, 0.500), (0.20, 0.135, 0.432, 0.476),
              (0.50, 0.105, 0.372, 0.456), (0.78, 0.070, 0.340, 0.444)]
DEAD_ARMS = [
    dict(spine=[(0.40, 0.075, 0.428, 0.576), (0.62, 0.045, 0.368, 0.620)],
         phase=1.1),
    dict(spine=[(0.16, 0.065, 0.620, 0.424), (0.28, 0.040, 0.692, 0.388)],
         phase=3.7),
]


def build_mature_dead():
    n = Node("SpurgeMatureDead")
    paint = Paint(DEAD_TEXELS, 99.0, 0.0, 0.0, (1.0, 1.0, 1.0), dead=True)
    solid_column(n, paint, 0, DEAD_TRUNK, PHASE, top_jag=True, jag_dip=0.10)
    for i, arm in enumerate(DEAD_ARMS):
        solid_column(n, paint, i + 1, arm["spine"], arm["phase"],
                     top_jag=True, jag_dip=0.06)
    return n


SEEDLING = [(-0.10, 0.080, 0.5, 0.5), (0.02, 0.105, 0.5, 0.5),
            (0.22, 0.080, 0.5, 0.5), (0.30, 0.098, 0.5, 0.5),
            (0.58, 0.052, 0.5, 0.5)]
SEEDLING_CROWN = 0.42
SEEDLING_BUDS = [
    dict(spine=[(0.16, 0.055, 0.44, 0.52), (0.32, 0.048, 0.39, 0.545),
                (0.46, 0.030, 0.365, 0.555)], crown=0.30, phase=PHASE - 1.4),
    dict(spine=[(0.12, 0.048, 0.565, 0.45), (0.26, 0.042, 0.62, 0.425),
                (0.38, 0.026, 0.645, 0.415)], crown=0.22, phase=PHASE + 1.4),
]


def build_seedling(dry=False):
    texels = DRY_TEXELS if dry else ALIVE_TEXELS
    n = Node("SpurgeSeedling" + ("Dry" if dry else ""))
    stage_tint = (0.94, 0.90, 0.86) if dry else (1.0, 1.0, 1.0)
    spine = wilt(SEEDLING, 0.9) if dry else SEEDLING
    paint = Paint(texels, SEEDLING_CROWN, 0.24, 0.0, stage_tint, dry)
    solid_column(n, paint, 0, spine, PHASE)
    for i, bud in enumerate(SEEDLING_BUDS):
        bspine = wilt(bud["spine"], 0.9) if dry else bud["spine"]
        bpaint = Paint(texels, bud["crown"], 0.24, 0.0, stage_tint, dry)
        solid_column(n, bpaint, 10 + i, bspine, bud["phase"])
    return n


SEEDLING_DEAD = [(-0.10, 0.060, 0.500, 0.500), (0.00, 0.092, 0.486, 0.494),
                 (0.16, 0.070, 0.460, 0.484)]


def build_seedling_dead():
    n = Node("SpurgeSeedlingDead")
    paint = Paint(DEAD_TEXELS, 99.0, 0.0, 0.0, (1.0, 1.0, 1.0), dead=True)
    solid_column(n, paint, 0, SEEDLING_DEAD, PHASE,
                 top_jag=True, jag_dip=0.045)
    return n


STUMP = [(-0.14, 0.160, 0.500, 0.500), (0.22, 0.140, 0.505, 0.501)]


def build_mature_stump():
    n = Node("SpurgeMatureStump")
    paint = Paint(DEAD_TEXELS, 99.0, 0.0, 0.0, (1.0, 1.0, 1.0), dead=True)
    solid_column(n, paint, 0, STUMP, PHASE,
                 top_jag=True, jag_dip=0.035, cut=True)
    return n


# ---------------------------------------------------------------- output

def check_manifold(node, name):
    """Build guard: every geometric edge of the model must be used by
    exactly two triangles - the mesh is a watertight solid, no cracks."""
    ids = {}

    def vid(p):
        key = (round(p[0], 4), round(p[1], 4), round(p[2], 4))
        return ids.setdefault(key, len(ids))

    edges = {}
    for i in range(0, len(node.indices), 3):
        a, b, c = (vid(node.pos[node.indices[i + k]]) for k in range(3))
        for e in ((a, b), (b, c), (c, a)):
            e = tuple(sorted(e))
            edges[e] = edges.get(e, 0) + 1
    bad = [e for e, count in edges.items() if count != 2]
    assert not bad, f"{name}: {len(bad)} non-manifold edges, e.g. {bad[:3]}"


def serialize(node):
    def prop(pname, dim, values):
        data = b"".join(struct.pack("<" + "f" * dim, *v[:dim]) for v in values)
        return f_string(1, pname) + f_varint(2, 4) + f_varint(3, dim) + f_bytes(4, data)

    body = (f_varint(1, -1) + f_string(2, node.name)
            + f_message(3, _vec3((0.0, 0.0, 0.0)))
            + f_varint(6, len(node.pos))
            + f_message(7, prop("position", 3, node.pos))
            + f_message(7, prop("normal", 3, node.nrm))
            + f_message(7, prop("tangent", 4, node.tan))
            + f_message(7, prop("color", 4, node.col))
            + f_message(7, prop("uv0", 2, node.uv0))
            + f_message(7, prop("uv1", 2, node.uv1))
            + f_message(7, prop("uv2", 2, node.uv2))
            + f_message(8, packed_ints(1, node.indices) + f_string(2, MATERIAL))
            + f_message(4, f_float(4, 1.0))
            + f_message(5, f_float(1, 1.0) + f_float(2, 1.0) + f_float(3, 1.0)))
    return zlib.compress(f_varint(1, 1) + f_string(2, node.name)
                         + f_message(3, body), 6)


def _parse_fields(data: bytes) -> dict:
    """Generic protobuf wire reader: {field: [(wire, value), ...]}."""
    out = {}
    p = 0
    while p < len(data):
        shift, key = 0, 0
        while True:
            b = data[p]; p += 1
            key |= (b & 0x7F) << shift
            if not b & 0x80:
                break
            shift += 7
        fnum, wire = key >> 3, key & 7
        if wire == 0:
            shift, val = 0, 0
            while True:
                b = data[p]; p += 1
                val |= (b & 0x7F) << shift
                if not b & 0x80:
                    break
                shift += 7
            if val >= 1 << 63:
                val -= 1 << 64
            out.setdefault(fnum, []).append((0, val))
        elif wire == 5:
            out.setdefault(fnum, []).append((5, data[p:p + 4])); p += 4
        elif wire == 2:
            shift, ln = 0, 0
            while True:
                b = data[p]; p += 1
                ln |= (b & 0x7F) << shift
                if not b & 0x80:
                    break
                shift += 7
            out.setdefault(fnum, []).append((2, data[p:p + ln])); p += ln
        else:
            raise AssertionError(f"unexpected wire type {wire}")
    return out


def validate(blob: bytes) -> None:
    """Full structural round-trip of an emitted model, the same properties the
    game's TimbermeshReader will exercise."""
    assert blob[0] == 0x78 and blob[1] == 0x9C, "missing zlib header"
    model = _parse_fields(zlib.decompress(blob))
    assert model[1][0][1] == 1, "version must be 1"
    name = model[2][0][1]
    assert isinstance(name, bytes) and name, "model name"
    nodes = model.get(3, [])
    assert nodes, "no nodes"
    for node_raw in nodes:
        nf = _parse_fields(node_raw[1])
        parent = nf[1][0][1]
        assert -1 <= parent < len(nodes), "parent index"
        vcount = nf[6][0][1]
        assert vcount > 0
        sizes = {"position": 12, "normal": 12, "tangent": 16, "color": 16,
                 "uv0": 8, "uv1": 8, "uv2": 8}
        for _, pdata in nf.get(7, []):
            pf = _parse_fields(pdata)
            pname = pf[1][0][1].decode()
            assert pname in sizes, f"unknown prop {pname}"
            assert pf[2][0][1] == 4 and pf[3][0][1] * 4 == sizes[pname], \
                f"prop {pname} type/dimension"
            assert len(pf[4][0][1]) == vcount * sizes[pname], f"prop {pname} bytes"
        assert nf.get(8), "no submesh"
        for _, mdata in nf.get(8, []):
            mf = _parse_fields(mdata)
            idx_bytes = mf[1][0][1]
            q = 0
            count = 0
            while q < len(idx_bytes):
                shift, v = 0, 0
                while True:
                    b = idx_bytes[q]; q += 1
                    v |= (b & 0x7F) << shift
                    if not b & 0x80:
                        break
                    shift += 7
                assert v < vcount, "index out of range"
                count += 1
            assert count % 3 == 0 and count > 0, "triangle count"
            assert mf[2][0][1].decode() == MATERIAL, "material name"


def main():
    MESH_DIR.mkdir(parents=True, exist_ok=True)
    models = [
        ("SpurgeSeedling", build_seedling(False)),
        ("SpurgeSeedlingDry", build_seedling(True)),
        ("SpurgeSeedlingDead", build_seedling_dead()),
        ("SpurgeMature", build_mature(False)),
        ("SpurgeMatureDry", build_mature(True)),
        ("SpurgeMatureDead", build_mature_dead()),
        ("SpurgeMatureStump", build_mature_stump()),
    ]
    for name, node in models:
        check_manifold(node, name)
        check_convex(node, name)
        for t in range(0, len(node.indices), 3):
            a, b, c = (node.pos[node.indices[t + k]] for k in range(3))
            assert len({a, b, c}) == 3, f"degenerate triangle in {name}"
            _n = _normal(a, b, c)
            assert _n != (0.0, 0.0, 0.0)
        blob = serialize(node)
        validate(blob)
        (MESH_DIR / f"{name}.timbermesh").write_bytes(blob)
        s = node.stats()
        r = max(s["max"][0] - s["min"][0], s["max"][2] - s["min"][2]) / 2
        h = s["max"][1] - s["min"][1]
        print(f"{name:22s} verts={s['verts']:4d} tris={s['tris']:4d} "
              f"y {s['min'][1]:.2f}..{s['max'][1]:.2f} | capsule r={r:.3f} "
              f"h={h:.3f} cy={(s['max'][1]+s['min'][1])/2:.3f}")
    print(f"wrote {len(models)} models to {MESH_DIR}")


if __name__ == "__main__":
    main()
