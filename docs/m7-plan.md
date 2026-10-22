# M7: art (models, materials, light and interface icons)

Status: planned 2026-10-22 in a grilling interview with the developer; M7a, M7b, M7c and M7d built 2026-10-22 (project-status 5.60 to 5.65; M7d with the Models the packs lacked made from plain shapes, `tools/make_models.gd`), M7e to M7g not. Replaces the M7 paragraph of `m6-plan.md`. **Audio is not in this plan**: the developer postponed it to after the art (sound effects, ambience and music will get a plan of their own).

The aim of the milestone: the whitebox game becomes a game with models, materials and a lit scene, **working end to end with existing assets**, even where they do not match the period. Period-correct art is a later milestone (M8).

## Decided with the developer

### Sources and licences
- **Kenney** (CC0) first: Car Kit, Train Kit, City Kit (Roads, Commercial, Suburban, Industrial), Factory Kit, Fantasy Town Kit, Mini Forest and the like. Where Kenney has nothing (aircraft, bombs, an anti-aircraft gun, more kinds of tree) other **CC0** sources are allowed (Quaternius, OpenGameArt filtered by CC0, and the like). CC-BY only with the developer's approval asset by asset.
- **No AI-generated models in M7.** The developer wants to see everything working with existing assets first.
- Every asset in the game is recorded in `CREDITS.md` (name, author, licence, link, where it is used); a Credits screen in the main menu is made from that file so it cannot go out of date.
- Only the files the game uses are committed (`game/assets/<category>/`, GLB with its textures); whole packs stay out of git in a local downloads folder.
- Textures for the ground and the roads: CC0 tileable textures of 512 px (ambientCG).

### Look
- The reference images the developer gave (Empires: Dawn of the Modern World, Commandos and Sudden Strike style screenshots) are a **target for the finished game**: painted, textured, earthy and slightly desaturated, warm low light with long shadows, cobblestones and varied grass, a stylised late 1930s and 1940s. The Kenney models are flat-coloured and low-poly, so **M7 does not reach that look by the models**; it gets close by the scene (light, shadow, grading, ground textures) and leaves painted textures and period models to M8. A style guide (`docs/style-guide.md`) states the rules for any later asset: simple faceted forms, a palette per category, saturation about 15 % lower than a plain game palette, warm earthy tones, no photographic textures on models.
- **Real scale** is kept: a lane is 3.5 m, a car 4.5 x 1.9 m, a bus 10 m, a house 16 to 24 m a side. A Model is scaled **uniformly** (never stretched) to the footprint or the length the tuning gives, and keeps its own proportions; the tuning does not change to suit a Model.

### How things are drawn
- **Vehicles:** one MultiMesh per Model (6 road models, the two locomotives, the wagons, 3 ambient cars, the repair crews), scaled to the length in the tuning. Wagons differ by the colour of their cargo only. The Line colour is a **Line marker**, a small flat mark on the roof in a second MultiMesh; the Model keeps its colours. No wheel or other animation yet. A wrecked vehicle stays debris.
- **Town buildings:** 2 or 3 variants for each type and level (home, commerce, industry, 3 levels), drawn from the building's id (the simulation's random streams are not touched), scaled uniformly to the footprint, the height being the Model's own. One MultiMesh per variant. Upgrades will one day add parts to the same building (with our own 3D); for now a building is one Model per level.
- **Industries (17, the anti-aircraft battery included):** one composite scene each (a `.tscn` assembled from kit pieces, trees and props) with the footprint of the tuning, instanced one by one (there are few). A bombed industry is darkened with smoke, as now; real ruins are M8.
- **Depots (road and rail, 3 levels), stations (road stop, rail platform), gateways, the aircraft and the bombs:** Models in the whitebox slots (`data/whitebox.json` keeps the footprints, the offsets and the Access point marks). The pad, the rotation and the raising of M6g/M6i stay as they are.
- **Road furniture:** the traffic lights, the three signs (priority, yield, stop), the rail signals and the level crossing barriers become Models. No street lamps or other new props.
- **Roads, tracks, bridges, the ground:** the geometry stays generated (it follows the terrain and the curves); the **materials** become textures (asphalt, stone path, track bed, grass, dirt, rock, sand), the ground blended by height and slope.
- **Trees:** visual only, not in the simulation state, the save or the hash. Scattered from the world seed and the terrain: sparse on grass, dense around a Forest industry, never on a road, track or building, and gone where something is built. One MultiMesh per species.
- **People on the streets:** not drawn (casualties stay numbers). A full state for people (NPCs) is a later piece of work.
- **Light:** the sun follows the time of day (a day is 1200 s): warm low sun at dawn and dusk with long shadows, bright noon, a dark but readable night (never black), shadows and SSAO that the player can turn off in Settings, mild fog and colour grading.
- **Interface icons:** a CC0 icon pack on the buttons of the bars, **beside the text, never in place of it**; the dark theme of `ui_theme.gd` stays.

### Not in M7
Audio, AI-generated models, period-correct models and painted textures (M8), ruins of destroyed buildings (M8), upgrade parts on buildings, people on the streets, street lamps and other new props, wheel or door animation, weather.

## Slices

Each slice is playable on its own and ends with screenshots from the real game (`--selftest-*` with `--shot`), the frame times of `--bench` compared with the whitebox, and the developer's look before the next one.

1. **M7a, pipeline.** `game/assets/`, `data/models.json` (for each Model: scene, how it is scaled and offset, its variants), a loader in `game/`, `CREDITS.md` and the Credits screen, `docs/style-guide.md`, and the tool that renders previews of candidate GLBs into sheets so the developer can choose: for each category I propose 2 or 3 candidates on a page (an Artifact) with a preview and the licence, the developer picks, and only then is anything imported.
2. **M7b, vehicles and trains.** The road models, the locomotives and wagons, the ambient cars, the repair crews, the Line marker, the debris.
3. **M7c, buildings and industries.** The town building variants, the 17 composite industries.
4. **M7d, depots, stations, gateways, aircraft, bombs and road furniture.** Aircraft and bombs probably need a source other than Kenney.
5. **M7e, ground and trees.** Textures for the terrain and the roads, the water, the trees.
6. **M7f, the lit scene.** Sun by time of day, shadows, SSAO, fog and grading, the Settings switch.
7. **M7g, interface icons.**

Documentation to update as each slice is built: `docs/project-status.md` (a section each), `CREDITS.md`, `data/models.json`, `docs/game-plan.md` ("Art and audio"), `CONTEXT.md` if a term is added.

## Verification and risks
- No one can hear or see for the developer: say plainly what was only looked at in screenshots and what was not played.
- Performance: a MultiMesh per Model with thousands of vehicles; compare `--bench` with the whitebox before and after each slice. Shadows are the likely cost; the switch in Settings is for that.
- Kenney models have their own proportions (a car is not 4.5 m by 1.9 m exactly); scaling to the tuning length makes the width differ a little from the lane: accepted.
- The look will not match the references until M8: the developer knows and accepted it.
