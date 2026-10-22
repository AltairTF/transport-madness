#!/usr/bin/env python3
"""Copies the game-icons.net SVGs listed in data/icons.json into game/assets/icons/game-icons/.
The pack ships each icon as white on a black square; the square is dropped so the icon is a white shape on nothing and
the button's icon colour can tint it. Run: python3 tools/make_icons.py <path of the unzipped icons-master folder>"""
import json, re, sys, pathlib

root = pathlib.Path(__file__).resolve().parent.parent
source = pathlib.Path(sys.argv[1])
table = json.loads((root / "data" / "icons.json").read_text())
out = root / "game" / "assets" / "icons" / "game-icons"
out.mkdir(parents=True, exist_ok=True)
missing = []
for key, ref in table["icons"].items():
    src = source / (ref + ".svg")
    if not src.exists():
        missing.append(ref)
        continue
    svg = src.read_text()
    svg = re.sub(r'<path d="M0 0h512v512H0z"/>', "", svg, count=1)
    (out / (ref.split("/")[1] + ".svg")).write_text(svg)
print("copied", len(table["icons"]) - len(missing), "missing", missing)
sys.exit(1 if missing else 0)
