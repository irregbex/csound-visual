"""Renders the Csound Visual app icon at every Windows icon size and writes app.ico + app.png.

Usage:  python tools/make_icon.py [<output folder> [<preview sheet .png>]]
        (normally: python tools/make_icon.py src/CsoundVisual/Assets icon_sheet.png)

Design: a rounded deep-navy tile with a thin electric-blue rim and the oscilloscope grid, a
glowing light-blue -> blue -> cyan sine wave and a bright dot on its highest crest (the "drag a
value" feature). The colours follow the app's default «Azul deportivo» preset
(src/CsoundVisual/Theming/AppTheme.cs), hard-coded here as RGB tuples. Requires Pillow.

Each size is drawn on its own 8x supersampled canvas (then downscaled) so strokes stay crisp;
small sizes use a thicker wave and drop the decorative details that would turn into mush.

Outputs:
  <output folder>/app.png   the 256 px image (window icon, docs favicon/logo via build_docs.py)
  <output folder>/app.ico   multi-resolution icon with every size in SIZES (the exe icon)
  <preview sheet .png>      every size side by side on the dark app background and on white,
                            for visual review only (not used by the build)

Run by hand when the icon design changes; the generated files are committed. Output paths are
joined with os.path.join, so the script runs on any OS. Both arguments are optional: the output
folder defaults to src/CsoundVisual/Assets of this repository, and without a preview path no
preview sheet is written (BUILDING.md documents the plain `python tools\\make_icon.py`).
"""
import math, os, sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops

# Output folder for app.png / app.ico (default: the app's Assets folder, found from this file).
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "CsoundVisual", "Assets")
PREVIEW = sys.argv[2] if len(sys.argv) > 2 else None  # optional preview sheet .png
# Icon sizes Windows asks for (taskbar, title bar, Explorer views at 100-200 % scaling).
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 8  # supersampling factor: each size is drawn at size*SS pixels, then downscaled


def lerp(a, b, t):
    """Linearly interpolate two colour tuples component-wise.

    Args:
        a: start colour (any length, e.g. RGBA).
        b: end colour (same length).
        t: position, 0.0 -> ``a`` and 1.0 -> ``b``.

    Returns:
        Tuple of ints (truncated, not rounded).
    """
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def vertical_gradient(w, h, top, bottom):
    """Build a ``w`` x ``h`` RGBA image fading from ``top`` to ``bottom`` colour.

    Only a 1-pixel-wide column is computed and then stretched, which is much faster than
    filling every pixel.
    """
    g = Image.new("RGBA", (1, h))
    for y in range(h):
        g.putpixel((0, y), lerp(top, bottom, y / max(1, h - 1)))
    return g.resize((w, h))


def horizontal_gradient(w, h, stops):
    """Build a ``w`` x ``h`` RGBA image with a multi-stop left-to-right gradient.

    Args:
        w, h: image size in pixels.
        stops: ``[(t, colour), ...]`` with ``t`` from 0.0 to 1.0 in increasing order; the first
            stop must be at 0.0 and the last at 1.0, otherwise edge pixels stay transparent.

    Returns:
        The gradient image (computed as one row, then stretched vertically).
    """
    g = Image.new("RGBA", (w, 1))
    for x in range(w):
        t = x / max(1, w - 1)
        for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
            if t0 <= t <= t1:
                g.putpixel((x, 0), lerp(c0, c1, (t - t0) / (t1 - t0)))
                break
    return g.resize((w, h))


def render(size):
    """Draw the icon for one target size.

    Everything is drawn on an ``S = size * SS`` canvas and composed in layers: tile (gradient
    clipped by a rounded-rectangle mask), top highlight, rim, oscilloscope grid, wave glow, wave,
    crest dot. All coordinates are fractions of ``S`` so the design scales. Which details are
    drawn depends on the size: ``small`` (<= 24 px) gets a tighter margin, a bigger and slower
    wave and no highlight/glow; the grid needs >= 48 px and the dot >= 24 px.

    Args:
        size: final edge length in pixels.

    Returns:
        A ``size`` x ``size`` RGBA image (Lanczos-downscaled from the supersampled canvas).
    """
    S = size * SS
    small = size <= 24
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Tile: rounded square with a deep navy gradient and a thin electric-blue rim («Azul deportivo»).
    margin = round(S * (0.04 if small else 0.06))
    radius = round(S * 0.22)
    box = [margin, margin, S - margin - 1, S - margin - 1]
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius, fill=255)
    tile = vertical_gradient(S, S, (18, 52, 120, 255), (6, 11, 22, 255))
    img.paste(tile, (0, 0), mask)

    # Soft top highlight for depth (large sizes only).
    if not small:
        # Alpha column that fades quadratically from 46/255 at the tile top to 0 at 55 % of its
        # height, clipped to the tile, then used to blend a light blue over the tile.
        hl = Image.new("L", (1, S), 0)
        for y in range(S):
            t = (y - box[1]) / ((box[3] - box[1]) * 0.55)
            hl.putpixel((0, y), int(46 * max(0.0, 1 - t) ** 2) if t >= 0 else 0)
        hl = ImageChops.multiply(hl.resize((S, S)), mask)
        img = Image.composite(Image.new("RGBA", (S, S), (168, 204, 255, 255)), img, hl)

    # Rim: more opaque on small sizes so it still separates the tile from the taskbar.
    rim = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle(box, radius, outline=(30, 107, 255, 180 if small else 130),
                                          width=max(SS, round(S * 0.012)))
    img = Image.alpha_composite(img, rim)

    # Grid lines like the in-app oscilloscope (large sizes only).
    if size >= 48:
        grid = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        gd = ImageDraw.Draw(grid)
        # Three faint vertical divisions and one horizontal centre line, clipped to the tile.
        for i in (1, 2, 3):
            x = box[0] + (box[2] - box[0]) * i / 4
            gd.line([(x, box[1] + S * 0.16), (x, box[3] - S * 0.16)], fill=(90, 160, 255, 30), width=max(SS, round(S * 0.008)))
        y = S / 2
        gd.line([(box[0] + S * 0.1, y), (box[2] - S * 0.1, y)], fill=(90, 160, 255, 36), width=max(SS, round(S * 0.008)))
        img = Image.alpha_composite(img, Image.composite(grid, Image.new("RGBA", (S, S)), mask))

    # The wave: a sine with a gentle envelope, drawn as a thick round polyline.
    # The envelope sin(pi*t)^0.6 is 0 at both ends and flat-topped in the middle, so the wave
    # starts and ends on the centre line. Small sizes use fewer cycles so the peaks stay apart.
    x0, x1 = S * 0.17, S * 0.83
    amp = S * (0.21 if small else 0.18)
    cycles = 1.5 if small else 2.0
    pts = []
    n = 2400
    for i in range(n + 1):
        t = i / n
        env = math.sin(math.pi * t) ** 0.6
        pts.append((x0 + (x1 - x0) * t, S / 2 - amp * env * math.sin(2 * math.pi * cycles * t)))
    stroke = round(S * (0.11 if size <= 20 else 0.085 if small else 0.062))

    # Stroke = a disc stamped at each of the 2401 points: gives round caps and joins and a
    # uniform width, which ImageDraw.line does not guarantee for thick polylines.
    wave_mask = Image.new("L", (S, S), 0)
    wd = ImageDraw.Draw(wave_mask)
    r = stroke / 2
    for p in pts:
        wd.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=255)

    # Wave colour along x: light blue -> accent blue (#1E6BFF) -> cyan.
    colors = horizontal_gradient(S, S, [(0.0, (168, 204, 255, 255)), (0.55, (30, 107, 255, 255)), (1.0, (0, 212, 255, 255))])

    # Glow: the wave mask blurred and dimmed to 75 %, filled with the accent blue under the wave.
    if not small:
        glow = wave_mask.filter(ImageFilter.GaussianBlur(S * 0.035))
        glow = glow.point(lambda v: int(v * 0.75))
        img = Image.alpha_composite(img, Image.composite(Image.new("RGBA", (S, S), (30, 107, 255, 255)), Image.new("RGBA", (S, S)), glow))

    img = Image.alpha_composite(img, Image.composite(colors, Image.new("RGBA", (S, S)), wave_mask))

    # Scrub handle on the highest crest (smallest y; the first crest when several tie): a bright
    # dot (the "drag a value" feature), drawn over a slightly larger dark disc that cuts it
    # out of the wave.
    if size >= 24:
        best = min(pts, key=lambda p: p[1])  # highest crest
        rr = stroke * (0.95 if size >= 48 else 0.85)
        dot = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        dd = ImageDraw.Draw(dot)
        dd.ellipse([best[0] - rr - stroke * 0.35, best[1] - rr - stroke * 0.35, best[0] + rr + stroke * 0.35, best[1] + rr + stroke * 0.35], fill=(6, 11, 22, 255))
        dd.ellipse([best[0] - rr, best[1] - rr, best[0] + rr, best[1] + rr], fill=(240, 246, 255, 255))
        img = Image.alpha_composite(img, dot)

    return img.resize((size, size), Image.LANCZOS)


# --- script body ---------------------------------------------------------------------------------
images = [render(s) for s in SIZES]
# os.path.join instead of OUT + r"\app.png": the hard-coded backslash only worked on Windows
# (elsewhere it produced a file literally named "Assets\app.png" in the parent folder).
images[-1].save(os.path.join(OUT, "app.png"))
# The ICO is saved from the 256 px image with the hand-drawn smaller renders passed in
# append_images, so each ICO entry is its own render rather than a downscale of the 256 px one.
images[-1].save(os.path.join(OUT, "app.ico"), format="ICO", sizes=[(s, s) for s in SIZES],
                append_images=images[:-1])

# Preview sheet for review: every size on the app background and on white.
# Top band: icons bottom-aligned on the navy background; bottom band (from y = 286): on light grey.
sheet = Image.new("RGBA", (sum(SIZES) + 20 * len(SIZES) + 20, 2 * 256 + 60), (6, 11, 22, 255))
ImageDraw.Draw(sheet).rectangle([0, 256 + 30, sheet.width, sheet.height], fill=(240, 240, 240, 255))
x = 20
for s, im in zip(SIZES, images):
    sheet.alpha_composite(im, (x, 20 + 256 - s))
    sheet.alpha_composite(im, (x, 256 + 40 + 256 - s))
    x += s + 20
if PREVIEW:  # only when a preview path was given (see the module docstring)
    sheet.save(PREVIEW)
print("ok", [im.size for im in images])
