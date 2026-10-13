#!/usr/bin/env python3
"""Regenerates the Ring Station build-menu icon.

Same measured vanilla style as tools/make_icon.py (whose header notes the
reference icons): 112x112 RGBA containing ONLY muted-gold line art on full
transparency. The dark rounded tile behind every icon is drawn by the
build-menu UI, so no background is baked in.

Art: the transporter itself after its reference prop, four rings on one
vertical axis, close enough to overlap; where an upper ring's bottom
covers the ring below, a thin seam is cut out of the lower one so the
overlap reads. The teleporter icon is the studded gate; this one is the
ring stack, the pair's other silhouette.
"""

from pathlib import Path

from PIL import Image, ImageDraw

MOD_ROOT = Path(__file__).resolve().parents[1]
OUT = MOD_ROOT / "Buildings" / "Paths" / "Ring" / "RingIcon.png"

GOLD = (182, 157, 105, 255)      # measured vanilla line-art gold
SS = 4                           # supersample factor
SIZE = 112 * SS


def main() -> None:
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # the ring stack: (cy, rx, ry, inner scale), bands overlapping 3px
    cx = 56 * SS
    stack = ((31.0, 32.0, 9.5, 0.62), (45.0, 32.0, 9.5, 0.62),
             (59.0, 32.0, 9.5, 0.62), (73.0, 32.0, 9.5, 0.62))
    # drawn bottom-up so an upper ring paints over the one below and its
    # hole paste can only cut its own opening, never the ring above it
    for cy, rx, ry, inner in reversed(stack):
        cy *= SS
        d.ellipse([cx - rx * SS, cy - ry * SS, cx + rx * SS, cy + ry * SS],
                  fill=GOLD)
        hole = Image.new("L", (SIZE, SIZE), 0)
        ImageDraw.Draw(hole).ellipse(
            [cx - rx * inner * SS, cy - ry * inner * SS,
             cx + rx * inner * SS, cy + ry * inner * SS], fill=255)
        img.paste((0, 0, 0, 0), (0, 0), hole)

    # the seams: each upper ring knocks a 3px crescent out of the one below
    for (cy, rx, ry, _) in stack[:-1]:
        seam = Image.new("L", (SIZE, SIZE), 0)
        sd = ImageDraw.Draw(seam)
        lower = cy + 3.0
        sd.ellipse([cx - rx * SS, (lower - ry) * SS, cx + rx * SS, (lower + ry) * SS],
                   fill=255)
        sd.ellipse([cx - rx * SS, (cy - ry) * SS, cx + rx * SS, (cy + ry) * SS],
                   fill=0)
        img.paste((0, 0, 0, 0), (0, 0), seam)

    img = img.resize((112, 112), Image.LANCZOS)
    img.save(OUT)
    a = img.getchannel("A").histogram()
    n = 112 * 112
    print(f"wrote {OUT}  transparent {sum(a[:16]) / n:.1%}  "
          f"opaque {sum(a[240:]) / n:.1%}")


if __name__ == "__main__":
    main()
