"""
Generate the CC Director setup wizard's brand assets.

Outputs (relative to this script):
- cc-logo.png         512x512 transparent. White "CC" + blue underline accent. Used by
                      the Welcome screen <Image>; lets WPF render the mark crisp at any
                      display size.
- ../setup.ico        Multi-frame ICO (16, 32, 48, 64, 128, 256). Same CC mark with a
                      small download-arrow badge in the bottom-right corner so the
                      installer is visually distinct from CC Director in taskbar/Alt-Tab.

Re-run after any visual tweak. The outputs are checked into the repo; the script does
not run as part of the build.
"""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ACCENT = (0, 122, 204, 255)   # #007ACC, matches App.xaml AccentBrush
WHITE = (255, 255, 255, 255)
TRANSPARENT = (0, 0, 0, 0)

HERE = Path(__file__).parent
SETUP_ROOT = HERE.parent


def _load_font(size: int) -> ImageFont.FreeTypeFont:
    """Pick a heavy sans-serif. Segoe UI is on every modern Windows; Arial is the fallback."""
    for candidate in (r"C:\Windows\Fonts\segoeuib.ttf", r"C:\Windows\Fonts\arialbd.ttf"):
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, size)
    return ImageFont.load_default()


def _draw_cc_mark(canvas: Image.Image, *, with_badge: bool) -> None:
    """Render the CC wordmark + blue underline onto canvas, centred. canvas is modified in-place."""
    w, h = canvas.size
    draw = ImageDraw.Draw(canvas)

    # The CC glyphs occupy ~58% of the canvas height; tuned by eye against the screenshot,
    # leaving room above for headroom and below for the underline accent.
    font_size = int(h * 0.58)
    font = _load_font(font_size)
    text = "CC"

    # Pillow's textbbox returns canvas-absolute (left, top, right, bottom) when given (0,0)
    # as the anchor. Subtracting `top`/`left` from the draw position aligns the visible
    # glyph bounding box to a known canvas point (text_y0/text_x0).
    left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
    text_w = right - left
    # Centre the glyphs horizontally; bias slightly above centre vertically so the underline
    # and a small bottom margin both fit.
    text_x0 = (w - text_w) // 2
    text_y0 = int(h * 0.12)
    text_x = text_x0 - left
    text_y = text_y0 - top

    draw.text((text_x, text_y), text, font=font, fill=WHITE)

    # Underline placement: anchor to the actual glyph bottom in canvas coords
    # (= text_y + bottom). Using a bbox-height shortcut here was off by `top` and
    # placed the underline inside the glyphs instead of below them.
    glyph_bottom = text_y + bottom
    underline_w = int(text_w * 0.48)
    underline_h = max(4, int(h * 0.045))
    ux = (w - underline_w) // 2
    uy = glyph_bottom + max(4, int(h * 0.025))
    draw.rectangle((ux, uy, ux + underline_w, uy + underline_h), fill=ACCENT)

    if with_badge:
        _draw_download_badge(canvas)


def _draw_download_badge(canvas: Image.Image) -> None:
    """Bottom-right corner badge: filled circle + downward arrow. Signals 'installer'."""
    w, h = canvas.size
    draw = ImageDraw.Draw(canvas)

    badge_d = int(min(w, h) * 0.42)
    pad = int(min(w, h) * 0.02)
    bx0 = w - badge_d - pad
    by0 = h - badge_d - pad
    bx1, by1 = bx0 + badge_d, by0 + badge_d

    # Thin white outline gives separation when the badge sits over a dark CC glyph.
    ring = max(2, badge_d // 24)
    draw.ellipse((bx0 - ring, by0 - ring, bx1 + ring, by1 + ring), fill=WHITE)
    draw.ellipse((bx0, by0, bx1, by1), fill=ACCENT)

    # Down-arrow: vertical shaft + triangular head, both white, sized off the badge.
    cx = (bx0 + bx1) // 2
    cy = (by0 + by1) // 2
    shaft_w = max(2, badge_d // 8)
    shaft_h = int(badge_d * 0.36)
    shaft_top = cy - shaft_h // 2 - int(badge_d * 0.05)
    draw.rectangle((cx - shaft_w // 2, shaft_top, cx + shaft_w // 2, shaft_top + shaft_h), fill=WHITE)

    head_h = int(badge_d * 0.26)
    head_w = int(badge_d * 0.42)
    head_top = shaft_top + shaft_h - max(1, head_h // 6)
    draw.polygon(
        [
            (cx - head_w // 2, head_top),
            (cx + head_w // 2, head_top),
            (cx, head_top + head_h),
        ],
        fill=WHITE,
    )


def build_logo(size: int = 512) -> Image.Image:
    img = Image.new("RGBA", (size, size), TRANSPARENT)
    _draw_cc_mark(img, with_badge=False)
    return img


def build_setup_icon_master(size: int = 512) -> Image.Image:
    img = Image.new("RGBA", (size, size), TRANSPARENT)
    _draw_cc_mark(img, with_badge=True)
    return img


def main() -> None:
    logo = build_logo(512)
    logo_out = HERE / "cc-logo.png"
    logo.save(logo_out, format="PNG", optimize=True)
    print(f"wrote {logo_out}")

    # Build the .ico from a high-res master so every frame is supersampled-then-resized
    # rather than each frame being drawn at its own (low) resolution.
    master = build_setup_icon_master(512)
    ico_out = SETUP_ROOT / "setup.ico"
    sizes = [(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    master.save(ico_out, format="ICO", sizes=sizes)
    print(f"wrote {ico_out} (frames: {', '.join(f'{w}x{h}' for w, h in sizes)})")


if __name__ == "__main__":
    main()
