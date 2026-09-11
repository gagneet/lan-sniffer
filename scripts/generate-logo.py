#!/usr/bin/env python3
"""Render the LanInspector logo and application icon from a single definition.

The mark is a magnifier over a small hub-and-spoke network: "inspect the LAN". Everything is
drawn at 4x and downsampled, which is what keeps the curves clean at icon sizes without shipping
a vector rasteriser. Re-run after editing PALETTE or the geometry below:

    python3 scripts/generate-logo.py
"""

from __future__ import annotations

import math
import pathlib

from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parent.parent
SUPERSAMPLE = 4
SIZE = 1024

PALETTE = {
    # Matches the WPF window chrome so the icon and the app read as one thing.
    "backdrop_top": (23, 33, 43),
    "backdrop_bottom": (26, 52, 78),
    "lens_glass": (16, 40, 62),
    "lens_rim": (79, 163, 240),
    "hub": (127, 212, 255),
    "leaf": (222, 233, 243),
    "link": (91, 135, 181),
}


def lerp(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def rounded_tile(size: int) -> Image.Image:
    """A vertical-gradient rounded square, used as the icon's backdrop."""
    gradient = Image.new("RGB", (1, size))
    for y in range(size):
        gradient.putpixel((0, y), lerp(PALETTE["backdrop_top"], PALETTE["backdrop_bottom"], y / max(size - 1, 1)))
    gradient = gradient.resize((size, size))

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=round(size * 0.195), fill=255)

    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    tile.paste(gradient, (0, 0), mask)
    return tile


def draw_mark(image: Image.Image, size: int) -> None:
    draw = ImageDraw.Draw(image)
    unit = size / 1024  # geometry below is authored against a 1024 canvas

    lens_x, lens_y, lens_r = 430 * unit, 410 * unit, 248 * unit
    rim = 54 * unit

    # Handle first, so the rim overlaps it rather than the other way round.
    angle = math.radians(45)
    handle_start = (lens_x + math.cos(angle) * (lens_r - rim * 0.2), lens_y + math.sin(angle) * (lens_r - rim * 0.2))
    handle_end = (lens_x + math.cos(angle) * (lens_r + 330 * unit), lens_y + math.sin(angle) * (lens_r + 330 * unit))
    draw.line([handle_start, handle_end], fill=PALETTE["lens_rim"], width=round(84 * unit))
    for point in (handle_start, handle_end):
        radius = 42 * unit
        draw.ellipse([point[0] - radius, point[1] - radius, point[0] + radius, point[1] + radius], fill=PALETTE["lens_rim"])

    # Lens glass, then the rim on top of it.
    draw.ellipse([lens_x - lens_r, lens_y - lens_r, lens_x + lens_r, lens_y + lens_r], fill=PALETTE["lens_glass"])
    draw.ellipse(
        [lens_x - lens_r, lens_y - lens_r, lens_x + lens_r, lens_y + lens_r],
        outline=PALETTE["lens_rim"],
        width=round(rim),
    )

    # The network under the glass: one hub, three leaves. Three is the most that stays legible
    # once the icon is scaled down to 16px.
    spoke = 132 * unit
    leaves = [
        (lens_x + math.cos(math.radians(a)) * spoke, lens_y + math.sin(math.radians(a)) * spoke)
        for a in (-90, 35, 160)
    ]

    for leaf in leaves:
        draw.line([(lens_x, lens_y), leaf], fill=PALETTE["link"], width=round(20 * unit))

    for leaf in leaves:
        radius = 33 * unit
        draw.ellipse([leaf[0] - radius, leaf[1] - radius, leaf[0] + radius, leaf[1] + radius], fill=PALETTE["leaf"])

    hub_r = 50 * unit
    draw.ellipse([lens_x - hub_r, lens_y - hub_r, lens_x + hub_r, lens_y + hub_r], fill=PALETTE["hub"])


def render(size: int, with_tile: bool = True) -> Image.Image:
    canvas = size * SUPERSAMPLE
    image = rounded_tile(canvas) if with_tile else Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw_mark(image, canvas)
    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    assets = ROOT / "assets"
    ui_assets = ROOT / "src" / "LanInspector.UI" / "Assets"
    assets.mkdir(exist_ok=True)
    ui_assets.mkdir(parents=True, exist_ok=True)

    master = render(SIZE)
    master.save(assets / "laninspector-logo.png")
    render(256).save(assets / "laninspector-logo-256.png")
    render(128).save(ui_assets / "laninspector-logo.png")

    # Windows picks the closest size out of the ICO, so ship the full ladder down to 16px.
    icon_sizes = [256, 128, 64, 48, 32, 24, 16]
    master.save(
        ui_assets / "laninspector.ico",
        format="ICO",
        sizes=[(s, s) for s in icon_sizes],
    )

    print(f"wrote {assets/'laninspector-logo.png'}")
    print(f"wrote {assets/'laninspector-logo-256.png'}")
    print(f"wrote {ui_assets/'laninspector-logo.png'}")
    print(f"wrote {ui_assets/'laninspector.ico'} ({', '.join(str(s) for s in icon_sizes)})")


if __name__ == "__main__":
    main()
