"""Render the Muster mark to PNGs, a multi-resolution Windows .ico, and SVGs.

Two optical variants:
  full  - used at 48px and above
  small - thicker ring, wider gaps, used at 32px and below so the yard
          gate does not close up when the shell downscales it

Eight colourways, one per IconSet member in Muster.Core. The palettes are the
whole difference between them: the geometry never changes. Slugs match the file
names the build copies beside the exe, and the default set carries no slug at
all - it is muster.ico, not muster-slate.ico, because it was there first and
renaming it would break ApplicationIcon.

The mark SVGs are for print and the web. The shell only ever reads the .ico,
which is why one thing is allowed to differ between them: a set whose ground is
bone gets a hairline slate keyline in the SVG and not in the raster. A near-white
tile needs an edge on a white page, and already has one against every chrome
Windows puts an icon on.
"""

import io
import os

from PIL import Image, ImageDraw

SLATE = (0x1C, 0x2B, 0x31, 255)
BONE = (0xE8, 0xE4, 0xDA, 255)
OCHRE = (0xC9, 0x87, 0x1C, 255)
EUCALYPT = (0x0F, 0x8A, 0x7E, 255)
CLAY = (0xB4, 0x47, 0x2E, 255)
HIVIS = (0xE9, 0xC3, 0x3B, 255)

# slug -> (ground, ring, core, rim or None, svg keyline)
PALETTES = {
    "": (SLATE, BONE, OCHRE, None, False),
    "eucalypt": (EUCALYPT, BONE, OCHRE, None, False),
    "inverted": (BONE, SLATE, OCHRE, None, True),
    "ochre": (OCHRE, SLATE, BONE, None, False),
    "bone": (BONE, OCHRE, SLATE, None, True),
    "rimmed": (SLATE, BONE, OCHRE, BONE, False),
    "hivis": (HIVIS, SLATE, SLATE, None, False),
    "clay": (CLAY, BONE, OCHRE, None, False),
}

S = 8  # supersample factor
BASE = 256

FULL_ARCS = ((170, 250), (262, 350), (2, 100))
SMALL_ARCS = ((175, 245), (275, 345), (15, 95))

# The same three arcs, as the original mark SVGs spell their endpoints. Copied
# rather than recomputed: to two decimal places the two ways of arriving at them
# disagree in the last digit, and matching the sets already on disk matters more
# than matching the arithmetic.
FULL_PATHS = (
    "M 49.21 141.89 A 80 80 0 0 1 100.64 52.82",
    "M 116.87 48.78 A 80 80 0 0 1 206.79 114.11",
    "M 207.95 130.79 A 80 80 0 0 1 114.11 206.79",
)
SMALL_PATHS = (
    "M 50.30 134.80 A 78 78 0 0 1 95.03 57.31",
    "M 134.80 50.30 A 78 78 0 0 1 203.35 107.81",
    "M 203.35 148.19 A 78 78 0 0 1 121.20 205.70",
)

PNG_SIZES = (16, 32, 48, 64, 128, 256, 512, 1024)
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

HERE = os.path.dirname(os.path.abspath(__file__))


def hexof(colour):
    return "#%02X%02X%02X" % colour[:3]


# ---- raster ---------------------------------------------------------------


def draw_mark(palette, ring_r, ring_w, core_r, rim_w, arcs):
    ground, ring, core, rim, _ = palette

    n = BASE * S
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def sc(v):
        return v * S

    d.rounded_rectangle((sc(8), sc(8), sc(248), sc(248)), radius=sc(52), fill=ground)

    # Drawn inside the tile rather than around it, so a rimmed set is the same
    # size on screen as an unrimmed one and the two can be swapped without the
    # mark appearing to change scale.
    if rim is not None:
        d.rounded_rectangle(
            (sc(8), sc(8), sc(248), sc(248)),
            radius=sc(52),
            outline=rim,
            width=sc(rim_w),
        )

    c = 128
    bbox = (sc(c - ring_r), sc(c - ring_r), sc(c + ring_r), sc(c + ring_r))
    for start, end in arcs:
        d.arc(bbox, start, end, fill=ring, width=sc(ring_w))

    d.ellipse(
        (sc(c - core_r), sc(c - core_r), sc(c + core_r), sc(c + core_r)), fill=core
    )
    return img


def variant(palette):
    full = draw_mark(palette, ring_r=80, ring_w=28, core_r=34, rim_w=10, arcs=FULL_ARCS)

    # The rim thickens with the ring for the same reason the ring does: at 16px
    # a 10-unit rim is half a pixel of mud rather than an edge.
    small = draw_mark(
        palette, ring_r=78, ring_w=40, core_r=30, rim_w=14, arcs=SMALL_ARCS
    )
    return full, small


# ---- vector ---------------------------------------------------------------


def svg(palette, width, ring_w, core_r, rim_w, paths):
    ground, ring, core, rim, keyline = palette

    if keyline:
        # Inset by half the stroke, radius following it in, so the tile still
        # ends on 8 and the corners do not read as a change of size.
        tile = (
            '<rect x="10" y="10" width="236" height="236" rx="50" ry="50" '
            'fill="%s" stroke="%s" stroke-width="4"/>' % (hexof(ground), hexof(SLATE))
        )
    else:
        tile = (
            '<rect x="8" y="8" width="240" height="240" rx="52" ry="52" '
            'fill="%s"/>' % hexof(ground)
        )

    # A stroke straddles its path, so the band PIL draws inward from the tile
    # edge is, here, a rect inset by half the width with the radius pulled in to
    # match. The outer edge lands on 8 either way.
    if rim is not None:
        half = rim_w / 2
        tile += (
            '\n  <rect x="%g" y="%g" width="%g" height="%g" rx="%g" ry="%g" '
            'fill="none" stroke="%s" stroke-width="%g"/>'
            % (
                8 + half,
                8 + half,
                240 - rim_w,
                240 - rim_w,
                52 - half,
                52 - half,
                hexof(rim),
                rim_w,
            )
        )

    body = "\n    ".join('<path d="%s"/>' % d for d in paths)

    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" '
        'width="%d" height="%d" role="img" aria-label="Muster">\n'
        "  <title>Muster</title>\n"
        "  %s\n"
        '  <g fill="none" stroke="%s" stroke-width="%d" stroke-linecap="butt">\n'
        "    %s\n"
        "  </g>\n"
        '  <circle cx="128" cy="128" r="%d" fill="%s"/>\n'
        "</svg>\n"
        % (width, width, tile, hexof(ring), ring_w, body, core_r, hexof(core))
    )


# ---- output ---------------------------------------------------------------


def write(name, text):
    path = os.path.join(HERE, name)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def render(slug):
    palette = PALETTES[slug]
    full, small = variant(palette)

    def at(size):
        src = small if size <= 32 else full
        return src.resize((size, size), Image.LANCZOS)

    stem = "muster" if slug == "" else "muster-%s" % slug

    for size in PNG_SIZES:
        at(size).save(os.path.join(HERE, "%s-%d.png" % (stem, size)))

    frames = [at(s) for s in ICO_SIZES]
    frames[-1].save(
        os.path.join(HERE, "%s.ico" % stem),
        format="ICO",
        sizes=[(s, s) for s in ICO_SIZES],
        append_images=frames[:-1],
    )

    write("%s-mark.svg" % stem, svg(palette, 256, 28, 34, 10, FULL_PATHS))
    write("%s-mark-small.svg" % stem, svg(palette, 32, 40, 30, 14, SMALL_PATHS))


if __name__ == "__main__":
    import sys

    for slug in sys.argv[1:] or PALETTES:
        render(slug)
        print("rendered %s" % (slug or "(default)"))
