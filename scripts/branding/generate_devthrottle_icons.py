"""
DevThrottle brand asset generator.

Produces the DevThrottle "DT" monogram icon (multi-size .ico) plus a logo
lockup (.png) and an .svg source. Run from the repo root:

    python scripts/branding/generate_devthrottle_icons.py

Design: dark slate rounded tile, bold white "DT", amber "throttle" accent bar.
Intentionally simple so it stays legible down to 16x16. ASCII-only output.
"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "out")
os.makedirs(OUT, exist_ok=True)

# Brand palette
SLATE_TOP = (32, 40, 58)      # #20283A
SLATE_BOT = (18, 23, 36)      # #121724
WHITE = (245, 247, 250)
AMBER = (255, 138, 24)        # #FF8A18 throttle accent
FONT_BOLD = "C:/Windows/Fonts/segoeuib.ttf"


def _gradient(size, top, bot):
    img = Image.new("RGB", (size, size), top)
    px = img.load()
    for y in range(size):
        t = y / max(1, size - 1)
        r = int(top[0] + (bot[0] - top[0]) * t)
        g = int(top[1] + (bot[1] - top[1]) * t)
        b = int(top[2] + (bot[2] - top[2]) * t)
        for x in range(size):
            px[x, y] = (r, g, b)
    return img


def _rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def make_tile(size):
    """A single square DT tile at the given pixel size (RGBA)."""
    # Render at 4x then downscale for crisp antialiased edges.
    ss = size * 4
    radius = int(ss * 0.22)
    bg = _gradient(ss, SLATE_TOP, SLATE_BOT).convert("RGBA")
    tile = Image.new("RGBA", (ss, ss), (0, 0, 0, 0))
    tile.paste(bg, (0, 0), _rounded_mask(ss, radius))

    d = ImageDraw.Draw(tile)

    # Amber throttle accent: a short thick angled bar lower-right (speed slash).
    bar_w = int(ss * 0.085)
    y0 = int(ss * 0.70)
    d.line([(int(ss * 0.30), int(ss * 0.82)), (int(ss * 0.70), int(ss * 0.70))],
           fill=AMBER, width=bar_w)

    # "DT" wordmark, bold, centered above the accent.
    font = ImageFont.truetype(FONT_BOLD, int(ss * 0.50))
    text = "DT"
    bb = d.textbbox((0, 0), text, font=font)
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    tx = (ss - tw) / 2 - bb[0]
    ty = (ss * 0.46 - th) / 2 - bb[1]
    d.text((tx, ty), text, font=font, fill=WHITE)

    return tile.resize((size, size), Image.LANCZOS)


def write_ico(path, sizes):
    master = make_tile(max(sizes))
    master.save(path, format="ICO", sizes=[(s, s) for s in sizes])
    print("WROTE", path, sizes)


def write_png(path, size):
    make_tile(size).save(path, format="PNG")
    print("WROTE", path, size)


def make_logo_lockup():
    """Horizontal lockup: DT tile + 'DevThrottle' wordmark (slate, for light bg)."""
    h = 320
    tile = make_tile(h - 40)
    pad = 20
    gap = 36
    font = ImageFont.truetype(FONT_BOLD, 150)
    tmp = ImageDraw.Draw(Image.new("RGBA", (10, 10)))
    bb = tmp.textbbox((0, 0), "DevThrottle", font=font)
    word_w = bb[2] - bb[0]
    w = pad + tile.width + gap + word_w + pad
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    img.paste(tile, (pad, (h - tile.height) // 2), tile)
    d = ImageDraw.Draw(img)
    tx = pad + tile.width + gap - bb[0]
    ty = (h - (bb[3] - bb[1])) // 2 - bb[1]
    # "Dev" in slate, "Throttle" in amber for a simple two-tone wordmark.
    d.text((tx, ty), "Dev", font=font, fill=(31, 41, 55))
    dev_w = d.textbbox((0, 0), "Dev", font=font)[2]
    d.text((tx + dev_w, ty), "Throttle", font=font, fill=AMBER)
    path = os.path.join(OUT, "devthrottle-logo.png")
    img.save(path, format="PNG")
    print("WROTE", path, img.size)


SVG = '''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#20283A"/>
      <stop offset="1" stop-color="#121724"/>
    </linearGradient>
  </defs>
  <rect width="256" height="256" rx="56" ry="56" fill="url(#bg)"/>
  <line x1="77" y1="210" x2="179" y2="179" stroke="#FF8A18" stroke-width="22" stroke-linecap="round"/>
  <text x="128" y="140" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif"
        font-weight="700" font-size="128" fill="#F5F7FA">DT</text>
</svg>
'''


def main():
    write_ico(os.path.join(OUT, "devthrottle.ico"), [256, 128, 64, 48, 32, 16])
    write_ico(os.path.join(OUT, "devthrottle-tray.ico"), [48, 32, 16])
    write_png(os.path.join(OUT, "devthrottle-256.png"), 256)
    make_logo_lockup()
    with open(os.path.join(OUT, "devthrottle-logo.svg"), "w", encoding="utf-8") as f:
        f.write(SVG)
    print("WROTE", os.path.join(OUT, "devthrottle-logo.svg"))
    print("DONE")


if __name__ == "__main__":
    main()
