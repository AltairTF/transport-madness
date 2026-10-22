# Style guide for art

Rules for any asset the game draws, so that models from different sources read as one game. M7 uses existing CC0 assets that only partly follow these rules (`m7-plan.md`); period-correct art (M8) follows them from the start. Written 2026-10-22 with M7a.

## Target look
Painted, earthy and slightly desaturated: a stylised late 1930s and 1940s, warm low light with long shadows, cobblestones and varied grass (the reference images of the developer: Empires, Commandos, Sudden Strike). The target is reached by the scene (light, shadow, grading, ground textures) as much as by the models.

## Models
- **Forms:** simple and faceted, readable from the game camera at 50 to 300 m. Small details (handles, wipers, bolts) are left out; a silhouette must tell a bus from a truck and a home from a shop.
- **Scale:** real. A lane is 3.5 m, a car 4.5 x 1.9 m, a bus 10 m, a house 16 to 24 m a side. The loader scales a model uniformly to the footprint or length of the tuning (`data/models.json`, "fit"); never stretch.
- **Orientation and pivot:** any, the entry's `yaw` turns it; the loader puts the middle of the base on the origin. Forward is -Z in the game.
- **Budget:** a vehicle up to 1,500 triangles, a town building up to 3,000, an industry piece up to 5,000; thousands of vehicles are drawn at once (a MultiMesh each). One material per model where possible.
- **Format:** GLB with embedded or sibling textures, in `game/assets/<category>/`. Only the files the game uses are committed.

## Colour
- A palette per category (vehicles, homes, commerce, industry, nature), earthy and warm: ochre, brick, olive, slate, dull red, cream.
- Saturation about 15 % lower than a plain game palette; no pure primaries, no neon, no pure black or white (dark grey and off-white).
- A vehicle keeps its own colours. The Line colour is the Line marker on the roof, not the paint.
- No photographic textures on models; painted or flat colour only. Ground and road textures are CC0 tileable 512 px (ambientCG).

## Licence and records
- CC0 first. CC-BY only with the developer's approval asset by asset. No AI-generated models in M7.
- Every asset is a row in `CREDITS.md` (name, author, licence, link, folder); the Credits screen and `tools/selftest_models.gd` read that file.
- Whole packs stay out of git, in a local downloads folder (`../transport-madness-packs/`).

## Choosing a model: the flow
1. `Godot --path . --script res://tools/model_sheet.gd -- <out folder> <a.glb> ...` renders a preview of each candidate straight from the file (a window opens briefly).
2. The candidates go on a page for the developer with the licence of each; the developer picks.
3. Only then: copy the GLBs into `game/assets/<category>/`, add the row to `CREDITS.md` and the entry to `data/models.json`, and run `tools/selftest_models.gd`.
