#!/usr/bin/env python3
"""Convert the macOS Quick Look SVG render into transparent app icon assets."""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw


CANVAS_SIZE = 1024
SVG_UNITS = 64
TILE_LEFT = 3
TILE_TOP = 3
TILE_RIGHT = 61
TILE_BOTTOM = 61
TILE_RADIUS = 15
MACOS_ARTWORK_SIZE = 910
SUPERSAMPLE = 4


def build_tile_mask() -> Image.Image:
    scale = CANVAS_SIZE * SUPERSAMPLE / SVG_UNITS
    mask = Image.new("L", (CANVAS_SIZE * SUPERSAMPLE,) * 2, 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle(
        (
            round(TILE_LEFT * scale),
            round(TILE_TOP * scale),
            round(TILE_RIGHT * scale),
            round(TILE_BOTTOM * scale),
        ),
        radius=round(TILE_RADIUS * scale),
        fill=255,
    )
    return mask.resize((CANVAS_SIZE, CANVAS_SIZE), Image.Resampling.LANCZOS)


def remove_white_matte(render: Image.Image, mask: Image.Image) -> Image.Image:
    source = render.convert("RGB")
    output = Image.new("RGBA", source.size, (0, 0, 0, 0))
    source_pixels = source.load()
    mask_pixels = mask.load()
    output_pixels = output.load()

    for y in range(CANVAS_SIZE):
        for x in range(CANVAS_SIZE):
            alpha = mask_pixels[x, y]
            if alpha == 0:
                continue

            red, green, blue = source_pixels[x, y]
            if alpha < 255:
                # Quick Look composites SVG thumbnails onto white. Reverse that
                # compositing at the rounded edge to avoid a light fringe.
                red = max(0, min(255, round(255 * (red - 255 + alpha) / alpha)))
                green = max(0, min(255, round(255 * (green - 255 + alpha) / alpha)))
                blue = max(0, min(255, round(255 * (blue - 255 + alpha) / alpha)))
            output_pixels[x, y] = (red, green, blue, alpha)

    return output


def add_macos_safe_margin(icon: Image.Image) -> Image.Image:
    resized = icon.resize((MACOS_ARTWORK_SIZE, MACOS_ARTWORK_SIZE), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    offset = (CANVAS_SIZE - MACOS_ARTWORK_SIZE) // 2
    canvas.alpha_composite(resized, (offset, offset))
    return canvas


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit("usage: generate-icons.py <quick-look-png> <asset-directory>")

    rendered_path = Path(sys.argv[1])
    asset_directory = Path(sys.argv[2])
    render = Image.open(rendered_path)
    if render.size != (CANVAS_SIZE, CANVAS_SIZE):
        raise SystemExit(f"expected a 1024x1024 render, got {render.size}")

    transparent = remove_white_matte(render, build_tile_mask())
    icon = add_macos_safe_margin(transparent)
    edges = (0, CANVAS_SIZE - 1)
    if any(icon.getpixel((x, y))[3] != 0 for x in edges for y in edges):
        raise SystemExit("app icon corners must be transparent")
    asset_directory.mkdir(parents=True, exist_ok=True)
    icon.save(asset_directory / "app-icon.png", format="PNG", optimize=True)
    icon.save(
        asset_directory / "app-icon.ico",
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    icon.save(asset_directory / "app-icon.icns", format="ICNS")


if __name__ == "__main__":
    main()
