"""Draws the SpurgeIcon: muted-gold ink painting of the spurge on transparency.

Matches the vanilla plant-icon register (open tapered strokes, translucent
ink, dark second-tone accents, hand-drawn wobble), supersampled 4x and
LANCZOS-downscaled to 112. Geometry is hand-tuned in 112-space; pixels
appear only inside _poly().
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw

MOD_ROOT = Path(__file__).resolve().parent.parent
OUT = MOD_ROOT / "NaturalResources" / "Trees" / "Spurge" / "SpurgeIcon.png"

SIZE = 112   # final canvas
S = 4        # supersample factor

GOLD = (186, 160, 107)        # main ink
GOLD_DEEP = (146, 124, 82)    # dark second tone (maroon blush, depth marks)
GOLD_BRIGHT = (216, 190, 134) # bead highlight fleck


def bez(ctrl, n=32):
    """Samples a smooth path through the control points (Catmull-Rom)."""
    if len(ctrl) == 2:
        x0, y0 = ctrl[0]
        x1, y1 = ctrl[1]
        return [(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n)
                for i in range(n + 1)]
    ext = [ctrl[0]] + list(ctrl) + [ctrl[-1]]
    k = len(ext) - 3
    per = max(2, n // k)
    pts = []
    for i in range(k):
        p0, p1, p2, p3 = ext[i:i + 4]
        m = per + 1 if i == k - 1 else per
        for j in range(m):
            t = j / per
            mt = 1 - t
            x = mt**3 * p1[0] + 3 * mt**2 * t * (p1[0] + (p2[0] - p0[0]) / 6) \
                + 3 * mt * t**2 * (p2[0] - (p3[0] - p1[0]) / 6) + t**3 * p2[0]
            y = mt**3 * p1[1] + 3 * mt**2 * t * (p1[1] + (p2[1] - p0[1]) / 6) \
                + 3 * mt * t**2 * (p2[1] - (p3[1] - p1[1]) / 6) + t**3 * p2[1]
            pts.append((x, y))
    return pts


def wobble(pts, amp, cycles, phase):
    """Adds a fixed sinusoidal drift perpendicular to the path (112-space)."""
    out = []
    m = len(pts) - 1
    for i, (x, y) in enumerate(pts):
        t = i / m
        j = min(max(i, 1), m - 1)
        dx = pts[j + 1][0] - pts[j - 1][0]
        dy = pts[j + 1][1] - pts[j - 1][1]
        L = math.hypot(dx, dy) or 1.0
        o = amp * math.sin(2 * math.pi * cycles * t + phase)
        out.append((x - dy / L * o, y + dx / L * o))
    return out


def resample(pts, n):
    """Resamples a polyline to n points by arclength."""
    dists = [0.0]
    for a, b in zip(pts, pts[1:]):
        dists.append(dists[-1] + math.hypot(b[0] - a[0], b[1] - a[1]))
    total = dists[-1] or 1.0
    out = []
    k = 0
    for i in range(n):
        d = total * i / (n - 1)
        while k < len(dists) - 2 and dists[k + 1] < d:
            k += 1
        seg = dists[k + 1] - dists[k] or 1.0
        f = (d - dists[k]) / seg
        out.append((pts[k][0] + (pts[k + 1][0] - pts[k][0]) * f,
                    pts[k][1] + (pts[k + 1][1] - pts[k][1]) * f))
    return out


def edges(center, hws, n=48):
    """Offsets a 112-space centerline by half-widths: (left, right, mid)."""
    c = resample(center, n + 1)
    m = len(c) - 1
    left, right = [], []
    for i, (x, y) in enumerate(c):
        t = i / m * (len(hws) - 1)
        k = min(int(t), len(hws) - 2)
        f = t - k
        w = hws[k] + (hws[k + 1] - hws[k]) * f
        j = min(max(i, 1), m - 1)
        dx = c[j + 1][0] - c[j - 1][0]
        dy = c[j + 1][1] - c[j - 1][1]
        L = math.hypot(dx, dy) or 1.0
        left.append((x - dy / L * w, y + dx / L * w))
        right.append((x + dy / L * w, y - dx / L * w))
    return left, right, c


def ws_at(pts, hws):
    """Per-point widths: hws interpolated across the polyline."""
    m = len(pts) - 1
    out = []
    for i in range(len(pts)):
        t = i / m * (len(hws) - 1)
        k = min(int(t), len(hws) - 2)
        f = t - k
        out.append(hws[k] + (hws[k + 1] - hws[k]) * f)
    return out


def _px(pts):
    return [(x * S, y * S) for x, y in pts]


def ink(d, center, hws, color=GOLD, alpha=220, amp=0.0, cycles=2.0,
        phase=0.0, n=48):
    """One tapered ink stroke; center is 112-space, widths are final px."""
    c = bez(center, n) if len(center) > 2 else list(center)
    if amp:
        c = wobble(c, amp, cycles, phase)
    left, right, _ = edges(c, hws, n)
    d.polygon(_px(left) + _px(right)[::-1], fill=color + (alpha,))


def dots(d, specs, color=GOLD, alpha=235):
    """Small filled beads: (cx, cy, r) triples in 112-space."""
    for cx, cy, r in specs:
        rr = r * S
        d.ellipse([cx * S - rr, cy * S - rr, cx * S + rr, cy * S + rr],
                  fill=color + (alpha,))


def fleck(d, cx, cy, r):
    rr = r * S
    d.ellipse([cx * S - rr, cy * S - rr, cx * S + rr, cy * S + rr],
              fill=GOLD_BRIGHT + (255,))


def mass(d, spine, hws, bulge, alpha, phase, hw_base=0.95, hw_side=0.80,
         hw_apex=0.40, clip=None):
    """One bold contour loop around a column: thin base, swelling flanks,
    thin apex - the vanilla leaf construction. clip=(mid, y_base, widths)
    trims the base where the loop hides behind the trunk."""
    left, right, _ = edges(bez(spine, 60), hws, 60)
    ax = (left[-1][0] + right[-1][0]) / 2
    ay = (left[-1][1] + right[-1][1]) / 2
    tx = ax - (left[-6][0] + right[-6][0]) / 2
    ty = ay - (left[-6][1] + right[-6][1]) / 2
    L = math.hypot(tx, ty) or 1.0
    apex = (ax + tx / L * bulge, ay + ty / L * bulge)
    cap = bez([left[-1], apex, right[-1]], 14)
    loop = left + cap + right[::-1]
    if clip:
        keep = []
        y_top = clip[0][-1][1]
        for x, y in loop:
            f = min(max((clip[1] - y) / (clip[1] - y_top), 0.0), 1.0)
            k = f * (len(clip[0]) - 1)
            i = min(int(k), len(clip[0]) - 2)
            cx = clip[0][i][0] + (clip[0][i + 1][0] - clip[0][i][0]) * (k - i)
            w = clip[2][i] + (clip[2][i + 1] - clip[2][i]) * (k - i)
            if math.hypot(x - cx, 0) > w - 0.6:
                keep.append((x, y))
        loop = keep
    loop = resample(loop, 140)
    loop = wobble(loop, 0.32, 1.4, phase)
    ws = []
    n = len(loop) - 1
    for i, (x, y) in enumerate(loop):
        f = i / n
        w = (hw_base + hw_side * math.sin(math.pi * f) ** 0.9) \
            * (hw_apex + (1 - hw_apex) * abs(math.cos(math.pi * f)) ** 1.5)
        ws.append(w)
    l2, r2, _ = edges(loop, ws, 140)
    d.polygon(_px(l2) + _px(r2)[::-1], fill=GOLD + (alpha,))


def main():
    img = Image.new("RGBA", (SIZE * S, SIZE * S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img, "RGBA")

    # ground: a sagging dim bar with two fading ticks
    ink(d, [(44, 102.4), (56.5, 103.4), (69, 102.0)], (1.9, 2.5, 1.9),
        alpha=130, amp=0.3, cycles=1.0, phase=1.0)
    ink(d, [(39.5, 102.5), (43.5, 102.4)], (1.3, 0.7), alpha=80)
    ink(d, [(72.5, 102.1), (77, 101.8)], (1.3, 0.6), alpha=80)

    # central trunk: one bold loop, dome tip, flaring to the ground line
    t_spine = [(57.4, 100), (57, 86), (56, 70), (55, 54), (54, 38),
               (53.6, 24), (53.4, 14)]
    t_hws = (4.5, 10.0, 9.4, 8.8, 8.1, 7.4, 6.3)
    t_mid = bez(t_spine, 60)
    clip = (t_mid, 86, [w - 1.2 for w in ws_at(t_mid, t_hws)])

    mass(d, t_spine, t_hws, 5.0, 228, 0.0)
    # crown band and chevron texture in the trunk's gold reach
    ink(d, [(49.8, 29.5), (53.4, 27.2), (57.2, 29.2)], (1.2, 0.8, 1.2),
        alpha=145, amp=0.25, cycles=1.0, phase=5.1)
    ink(d, [(51.4, 31.8), (53.7, 34.9), (56.0, 31.8)], (1.2, 0.8, 1.2),
        alpha=140, amp=0.2, cycles=1.0, phase=0.6)
    ink(d, [(50.8, 41.0), (53.1, 44.1), (55.4, 41.0)], (1.2, 0.8, 1.2),
        alpha=126, amp=0.2, cycles=1.0, phase=2.2)

    # three arms: calm upward columns leaning off the trunk flanks
    s1 = [(47.8, 70), (42.5, 63.5), (37.2, 56.5), (32.2, 48.8),
          (28.2, 41), (26.2, 33.5)]
    s2 = [(64.8, 73), (70.8, 66.8), (76.0, 57.5), (81.6, 50.5), (84.8, 43.5),
          (86.6, 36.5)]
    s3 = [(63.8, 50), (68.2, 45.5), (72.2, 40.2), (75.0, 34.2),
          (76.2, 28.5)]
    h1 = (4.2, 6.0, 7.0, 7.4, 6.8, 4.6)
    h2 = (4.2, 6.0, 7.0, 7.4, 6.8, 4.6)
    h3 = (3.2, 4.8, 5.6, 5.8, 5.0, 3.0)
    mass(d, s1, h1, 4.5, 212, 1.1, clip=clip)
    mass(d, s2, h2, 4.5, 216, 2.6, clip=clip)
    mass(d, s3, h3, 4.0, 208, 4.2, clip=clip)
    # short rib dashes inside the big arms
    ink(d, [(36.5, 53.5), (34.8, 57.8)], (0.9, 0.5), alpha=110, amp=0.25,
        cycles=1.0, phase=1.8)
    ink(d, [(77.0, 57.0), (78.8, 61.0)], (0.9, 0.5), alpha=110, amp=0.25,
        cycles=1.0, phase=3.7)

    # maroon blush: a few dark diagonal ticks on the lower trunk (line art
    # only; the earlier edge strokes and contamination arc traced a rectangle
    # outline down the trunk, and a translucent fill read as a grey square)
    ink(d, [(52.4, 66.0), (50.2, 76.0)], (1.2, 0.6), color=GOLD_DEEP,
        alpha=185)
    ink(d, [(61.0, 66.0), (63.4, 76.0)], (1.2, 0.6), color=GOLD_DEEP,
        alpha=185)
    ink(d, [(54.4, 92.0), (56.3, 100.4)], (1.6, 1.0), color=GOLD_DEEP,
        alpha=210)

    # amber latex beads in tight bunches on the arm tips (ready-to-tap)
    clusters = [
        [(24.6, 26.8, 3.2), (21.9, 29.8, 2.3), (28.0, 29.4, 1.8)],
        [(87.9, 29.6, 3.2), (85.3, 32.7, 2.3), (90.9, 32.2, 1.8)],
        [(78.2, 22.0, 2.8), (75.8, 24.6, 2.0), (80.6, 24.2, 1.6)],
    ]
    for cl in clusters:
        dots(d, cl, alpha=250)
        x, y, r = cl[0]
        fleck(d, x - r * 0.30, y - r * 0.32, r * 0.38)

    img = img.resize((SIZE, SIZE), Image.LANCZOS)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    img.save(OUT)
    (OUT.parent / (OUT.name + ".meta.json")).write_text('{"isSprite": true}\n')
    print(f"wrote {OUT} (+ meta)")


if __name__ == "__main__":
    main()
