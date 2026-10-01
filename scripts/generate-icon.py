#!/usr/bin/env python3
"""
Regenerates Assets/icon.ico from Assets/Icon.png, with every size Windows asks
for. Run this after swapping in a new source icon.

256x256 is the largest an .ico can hold - the classic ICONDIRENTRY format
stores each frame's width/height in a single byte (0 means 256), so there's
no way to address 512. That's a hard limit of the file format, not this
script. The window itself doesn't go through the .ico at all: it renders
Assets/Icon.png directly, at whatever resolution you give it, so the source
PNG can still be as large as you like.

Usage: python scripts/generate-icon.py [source.png] [dest.ico]
Requires Pillow: pip install pillow
"""
import sys
from pathlib import Path

from PIL import Image

SIZES = [16, 24, 32, 48, 64, 96, 128, 256]


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    src = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "Assets" / "Icon.png"
    dest = Path(sys.argv[2]) if len(sys.argv) > 2 else root / "Assets" / "icon.ico"

    if not src.exists():
        print(f"Source image not found: {src}", file=sys.stderr)
        return 1

    img = Image.open(src).convert("RGBA")

    if img.width != img.height:
        print(f"Warning: {src} is {img.width}x{img.height}, not square. Icon will be stretched.")

    # Pillow only ever down-scales for the ICO sizes list; upscale the source
    # first if it's smaller than our biggest requested size so every frame
    # (e.g. 512x512) is still generated instead of silently skipped.
    largest = max(SIZES)
    if img.width < largest or img.height < largest:
        img = img.resize((largest, largest), Image.LANCZOS)

    img.save(dest, format="ICO", sizes=[(s, s) for s in SIZES])
    print(f"Wrote {dest} with sizes: {', '.join(f'{s}x{s}' for s in SIZES)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
