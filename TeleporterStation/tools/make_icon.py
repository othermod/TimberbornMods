#!/usr/bin/env python3
"""Regenerates the Teleporter build-menu icon.

Measured vanilla style (icon_TubewayStationIcon.png, icon_PathIcon.png, ...):
112x112 RGBA containing ONLY muted-gold line art on full transparency: 54-74%
of pixels are alpha=0 and there are no semi-transparent background pixels at
all. The dark rounded tile behind every icon is drawn by the build-menu UI, so
any background baked into the PNG just covers the game's tile pattern.

Art: a teleporter gate, one bold ring, nine chevron locks as
filled studs on the band, top stud slightly larger, nothing in the center.
Drawn at 4x supersampling and downscaled for vanilla-matching anti-aliasing.
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw

MOD_ROOT = Path(__file__).resolve().parents[1]
OUT = MOD_ROOT / "Buildings" / "Paths" / "Teleporter" / "TeleporterIcon.png"

GOLD = (182, 157, 105, 255)      # measured vanilla line-art gold
SS = 4                           # supersample factor
SIZE = 112 * SS


def main() -> None:
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx = cy = SIZE / 2

    r_out, r_in = 34 * SS, 28.5 * SS   # ring band
    d.ellipse([cx - r_out, cy - r_out, cx + r_out, cy + r_out], fill=GOLD)
    hole = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(hole).ellipse([cx - r_in, cy - r_in, cx + r_in, cy + r_in], fill=255)
    img.paste((0, 0, 0, 0), (0, 0), hole)

    # nine chevron locks, one at the top; studs must clearly protrude past the
    # band edges or they merge into it invisibly (band half-width is ~2.75*SS)
    r_mid = (r_out + r_in) / 2
    for k in range(9):
        a = -math.pi / 2 + k * math.tau / 9       # start at 12 o'clock
        sx, sy = cx + r_mid * math.cos(a), cy + r_mid * math.sin(a)
        r = (6.4 if k == 0 else 5.4) * SS
        d.ellipse([sx - r, sy - r, sx + r, sy + r], fill=GOLD)

    img = img.resize((112, 112), Image.LANCZOS)
    img.save(OUT)
    a = img.getchannel("A").histogram()
    n = 112 * 112
    print(f"wrote {OUT}  transparent {sum(a[:16]) / n:.1%}  "
          f"opaque {sum(a[240:]) / n:.1%}")


if __name__ == "__main__":
    main()
