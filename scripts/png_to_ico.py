#!/usr/bin/env python3
"""Convert a PNG image into a multi-resolution Windows .ico file.

Transparency (the source alpha channel) is preserved. The standard .ico format
encodes each frame's width/height in a single byte (0 == 256), so 256x256 is the
maximum size a compatible .ico can represent -- larger sizes are silently skipped.

Usage:
    python scripts/png_to_ico.py <source.png> <output.ico> [--sizes 16,32,48,256]

Example (the icon shipped in this repo):
    python scripts/png_to_ico.py "input.png" assets/icon.ico

Requires: Pillow  (pip install pillow)
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image

DEFAULT_SIZES = [16, 24, 32, 48, 64, 128, 256]
MAX_ICO_SIZE = 256  # .ico directory entry stores dimensions in a single byte (0 == 256)


def parse_sizes(raw: str) -> list[int]:
    sizes = sorted({int(s.strip()) for s in raw.split(",") if s.strip()})
    if not sizes:
        raise argparse.ArgumentTypeError("no valid sizes provided")
    return sizes


def convert(source: Path, output: Path, sizes: list[int]) -> list[int]:
    usable = [s for s in sizes if s <= MAX_ICO_SIZE]
    dropped = [s for s in sizes if s > MAX_ICO_SIZE]
    if dropped:
        print(
            f"warning: sizes {dropped} exceed the .ico maximum of {MAX_ICO_SIZE} "
            "and will be skipped (single-byte dimension limit).",
            file=sys.stderr,
        )
    if not usable:
        raise ValueError("no sizes <= 256 to write")

    img = Image.open(source).convert("RGBA")
    # Downscale every frame from a high-quality full-resolution base.
    base = img.resize((MAX_ICO_SIZE, MAX_ICO_SIZE), Image.LANCZOS)

    output.parent.mkdir(parents=True, exist_ok=True)
    base.save(output, format="ICO", sizes=[(s, s) for s in usable])
    return usable


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("source", type=Path, help="source PNG path")
    parser.add_argument("output", type=Path, help="destination .ico path")
    parser.add_argument(
        "--sizes",
        type=parse_sizes,
        default=DEFAULT_SIZES,
        help="comma-separated square sizes (default: %s)" % ",".join(map(str, DEFAULT_SIZES)),
    )
    args = parser.parse_args(argv)

    if not args.source.is_file():
        parser.error(f"source not found: {args.source}")

    written = convert(args.source, args.output, args.sizes)
    print(f"Wrote {args.output} with sizes {written}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
