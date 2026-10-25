# Town redo: small organic villages (DayZ / Chernarus style)

Status: agreed 2026-10-24 in three rounds of questions; slice 1 built 2026-10-24 (not committed), slices 2 and 3 open. Replaces `docs/town-upgrade-plan.md` and the R4 growth work. Saves break on purpose (`SaveGame.FormatVersion` goes up in slice 3).

## Why

The upgrade towns (7970 people in one town, a lattice of 60 m blocks) are far too big and look like a mesh. The region the player works in is small and under development: no town has more than 1000 people at the start. References are the villages of Chernarus: one curving through road, a few lanes, loose houses with yards, bigger buildings at the crossing, a farm or factory compound apart.

## Decided

1. **Size.** The cap of 1000 people is for generation only; in play a town can pass it. Three size classes: Hamlet 80 to 250, Village 250 to 600, Town 600 to 1000. No metropolis.
2. **Count.** Slider 1 to max, with max 3 (Small map), 5 (Normal), 7 (Large). The first town (best site) is a Town when two or more are asked for; half of the rest are Villages, the others Hamlets.
3. **Layout archetypes**, by class and terrain: *Cluster* (hamlet: a short road, up to one lane), *Linear* (houses both sides of one curving road, one to three lanes), *Crossing* (two roads cross in the middle, lanes in loops and dead ends; villages and towns). Villages and towns may also have a *compound* (farm or factory) apart: a spur, a ring road and industry buildings around it.
4. **Streets.** Curved, following the ground: the through road runs along the contour. Main roads are `TwoLane`, lanes are `Path` (earth). No grid, no avenue, no urban kerb or pavement (lamps and pavement only in the middle of the biggest towns, slice 3). No bridges inside a town.
5. **Buildings.** Loose points along the streets, facing the street, 16 to 34 m apart, yaw jitter, a yard in front. Bigger ones near the middle. Home 5, 14 or 34 people (house, big house, small block); commerce and industry keep their jobs. The people add up to the target of the class.
6. **Ground.** Gentle slopes only (about 12 %), dry, ground levelled under every building (`Terrain.LevelPad`), roads carve as before. Forest is kept off the middle of a town by the game side (slice 3).
7. **Growth in play (slice 2).** Monthly, gated by satisfaction and the share of demand delivered (the construction stock). A town that has no free spot grows a new street by itself, one at a time: it branches from a street of the town (or a road through it), curves with the ground, 60 to 150 m, ends in a dead end, a loop or a join; the houses come one by one along it. Houses can be founded beside any road within the town's reach, also the player's. Streets are free and not in the command log. Refused on steep ground, water, within 12 m of a building, across a railway or into another town; with no place the town stands still. A town never shrinks. 200 people to 1000 in about 8 to 12 years when well served. No lots, no reserve.
8. **War and rebuilding** stay as they are; a destroyed building is rebuilt on the same spot.

## Slices

1. **Generator** (this slice): `VillageGenerator`, `TownGenerator` (sites, sizes, names), tuning (`towns.generation`, `towns.rings`, building people), the slider, tests. Streets and buildings are built straight into the `RoadNetwork` and checked against what is really there. The old street generator is deleted. The grid `LotSites`, `Lot` and `ExtendGrid` stay for now only because the growth code and old test fixtures use them; the generator hands the sim its spare building spots as lots until slice 2 removes them.
2. **Organic growth**: street birth, building spots computed on the fly, `Lot` removed, growth tests rewritten, balance re-run.
3. **Look**: rural cross-section (no kerb), earth lanes, forest cleared, ground colour, preview, save format 5, screenshots.

## Checks

Tests with the real `RoadNetwork` and terrain: same seed same towns; population inside the class range and at most 1000; every building near a road, none on one, none in water, none on a steep slope, none overlapping; no bridge inside a town; a road connects the town; towns far apart; slider limits; generation time on the Large map. Look at the result with a drawn SVG and, in slice 3, screenshots.

## Slice 1 as built

- `VillageGenerator` (new), `TownGenerator` (sites and classes), `TownSize` is Hamlet/Village/Town, tuning `towns.generation` (`streets`, `hamlet`, `village`, `town`, `sizes`), `towns.rings` read as share of the town radius. `StreetGenerator` and its tests, `StreetLotTests` deleted; `VillageGeneratorTests` new; `WorldBuilder.MostTowns` (3, 5, 7) and the menu slider (1 to that).
- People per building were NOT changed to 5, 14, 34 as planned: many test fixtures hang on 8, 20, 50, so a house still holds 8. The class ranges are in people: hamlet 60 to 160, village 160 to 450, town 450 to 900 (the generator reaches roughly 40 to 100 % of the target; lanes are added, up to twelve, while short).
- Balance tests moved to small towns: bus line with two buses, served-town supply 30 a day with growth up to 3 times, maintenance share up to 0.25.
- Spare lots are still handed to the sim (few, since the streets fill up) until slice 2.
