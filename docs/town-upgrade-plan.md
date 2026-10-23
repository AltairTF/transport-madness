# Town upgrade: compact towns, streets that make sense, and what stands along them

Status: built 2026-10-23 (see "Built" at the end for what differs); agreed with the developer in two rounds of questions. Numbers marked first values are tuned by looking at the layouts. It is built in one go (one branch of work, one commit per step is fine) and replaces the R4 growth loop; saves are broken on purpose (`SaveGame.FormatVersion` goes up).

## Why

The R4 towns (docs/r4-plan.md) were looked at in the 3D build on 2026-10-23 and are wrong in four ways:
- Too sparse: about 25 buildings within 150 m of the centre of a medium town, because each lot is 30 m wide and 20 to 25 m from the street, and many lots are refused near other streets.
- Round: the outline is the generation radius times `outerReach`, not the result of blocks of buildings.
- Streets that make no sense: random pieces of 40 to 90 m, dead ends, odd angles, nothing like a hierarchy.
- The street itself looks wrong: a dark disc at every node (also in the middle of a straight street), no kerb or pavement in town, the same cross-section in the country, and a node can be snapped to even when it lies under or over other roads.

## Decided

1. **Street hierarchy.** Two or three main streets cross the town (the seed roads, which leave it as country roads). Collector streets leave them at about 90 degrees, 60 to 90 m apart. Local streets are short and close rectangular, slightly irregular blocks. Streets follow the contours of a slope. The outline of a town is what the blocks leave; there is no radius.
2. **Density and frontage.** Buildings face the street and stand in a nearly continuous row in the centre; in the edge houses are spaced and have a yard. Lot frontage 18 to 24 m (by zone), buildings close to the street.
3. **Population drives the buildings.** Every town gets a target population by size class (first values: village 400 to 800, medium 3 000 to 6 000, metropolis 15 000 or more). The generator adds buildings and tiers until the people add up; tall buildings only where many people live. The size of the town follows from the population.
4. **Nodes.** Real nodes only at junctions and street ends. A bend inside a street is not a junction: it is drawn as part of the street and costs traffic nothing. Snapping accepts only junctions and ends, never a point over another street. No disc at plain pass-through points.
5. **Cross-section.** An urban street has a kerb and a pavement on both sides; a country road has an earth shoulder and no pavement. The change is at a junction at the edge of the town. Pavements are visual only for now (cars keep their lanes).
6. **What appears with the town.** Street lamps on urban streets, ground (paving or lawn) under the blocks, trees along the streets and in yards. Plazas, parks and walls come later.
7. **Saves.** Broken on purpose; everything in one go.

## What the code does today (facts found 2026-10-23)

- One road is one straight line or one circular arc (`RoadGeometry`, S/RoadGeometry.cs); there are no polylines. A bend is several roads joined at nodes, and every joint is a junction with a 12-segment Bezier of 8 m reach in and out (`MovePath`, `RebuildJunction`) and a 10 m minimum piece. `StreetGenerator` keeps pieces of at least 30 m for that reason.
- The disc at every node is `CylinderMesh` of radius 8.5 in `SimHost.RebuildRoads` (G/SimHost.cs:1474-1481) for every node with incoming lanes. Hiding it where `RoadsAt(n).Count < 3` is one line.
- Snap: `SnapToNode` (G/SimHostPicking.cs:44) is a plain 2D nearest-node search (`FindNodeNear`, `NODE_SNAP_RADIUS` 20) and then `NearestOnRoad`. Nodes at bridge and tunnel ends and where a road passes over or under another (`InsertStructures`, `SeparateCrossings`) are snappable although other roads lie there.
- Cross-section: TwoLane 7 m, Avenue2x2 16 m with a 2 m grass median, Path 5 m; asphalt texture, one cream centre line; no kerb, pavement, edge line or shoulder mesh. The 6 m `Shoulder` is only a terrain-blend constant. No urban/rural switch exists.
- Towns: buildings are Kenney models in MultiMeshes (G/ModelDraw.cs), footprint fitted to 16, 20 or 24 m by tier; trees are visual only (G/SimHostTrees.cs, biome density, kept 11 m from roads and 20 m from buildings); the only lamps are traffic lights; no ground tint under towns.
- Sim: lots are fixed at generation (`plan.Lots`); `FoundBuilding` uses `RingAt(U)`; `ExtendGrid` does nothing for organic towns, so a town only grows into its pre-laid lots. People per building: home 8 / 20 / 50, jobs per commerce 6 / 16 / 40.

## Work, in the order it is built

### A. Street generator (S/StreetGenerator.cs, replaced)
- Seed roads as now (through roads, along contours), then collectors from them at about 90 degrees every 60 to 90 m, alternating sides, only while the town needs more buildings for its population; then local streets closing blocks. Pieces are long and straight (80 to 200 m); small bends are made by turning at a pass-through node by at most about 10 degrees.
- Blocks are what the streets enclose; a block gets lots on all its sides, deep enough for one or two rows by zone.
- Pure function as before (seed, centre, population target, terrain in; streets, lots and buildings out), so it can be drawn as SVG and tested alone.

### B. Lots and buildings
- Lot frontage by zone: centre 20 to 24 m, middle 22 m, edge 28 m with a yard; building footprint by tier capped by the lot (tier 2 only where the lot allows it).
- Buildings face the street, are set back by the pavement plus a small gap, and are packed along the frontage (no gaps larger than a lot).
- Population target (Decided 3) closes the loop: tiers and mix by ring and by lot, until the people add up; jobs follow the existing mix.

### C. Road network and snap
- A pass-through node (two roads, nearly tangent) is "free": the lanes join with a direct link, no Bezier of 8 m reach, and its minimum piece is not 10 m. Junction logic stays for three or more roads.
- Snap candidates: junctions and ends only; skip nodes at bridge, tunnel and over/under-crossings; a click on a street gives the clean junction on its centre line (`NearestOnRoad`).
- Tests: crossing, junction and save tests keep passing; new tests for tangent joins and for snap refusing a node under another road.

### D. Visuals (game side)
- Disc only at junctions and dead ends (`RoadsAt(n).Count != 2`), and joins of two roads drawn as one ribbon.
- Urban flag per road (set by the generator; for a road the player builds, by being inside the built-up area), with kerb and pavement meshes on both sides; country roads keep the shoulder look; the switch is at a junction at the town edge.
- Street lamps along urban streets, ground under the blocks, trees along streets and in yards (extend `SimHostTrees` rules, which today keep 11 m from roads and 20 m from buildings).

### E. Plumbing and checks
- `TownPlan` and `Town`: radius becomes a result, not an input; `Radius` stays for the clearance formulas.
- `SaveGame.FormatVersion` up; golden hashes regenerated and noted; `StreetGeneratorTests`, `StreetLotTests`, balance, rail and trip tests moved to the new layouts.
- Acceptance numbers (to be set with the answers below): buildings within 150 m of the centre, share of street length with a building facing it, longest dead end, generation time on the Large map.
- Look at it: SVG of a village, a medium town and a metropolis; screenshots in the .NET build, day and night; `--bench` before and after.

## Decided in the second round

- **Bends:** every road stays one straight line or one arc; no polylines. A pass-through node (two roads, nearly tangent) is free: the lanes join directly, there is no Bezier of 8 m reach and no 10 m minimum piece. Streets are long and straight (80 to 200 m) with turns of at most about 10 degrees at pass-through nodes.
- **Snap:** only junctions and street ends are targets; pass-through nodes and nodes at bridge, tunnel and over/under-crossings are not. A click on a street still makes a clean junction on its centre line.
- **Urban:** a per-road flag, no new `RoadType`. The generator sets it for the streets of the town; a road the player builds counts as urban inside the built-up area (near lots of the town). The cross-section changes at a junction at the edge of the town. Price and lanes do not change.
- **Lots by zone (first values):** centre 24 m frontage, up to tier 2; middle 22 m, up to tier 1; edge 28 m, small houses with a yard. Buildings stand at the pavement with a small setback.
- **Growth in play (R5) is not in this round.** A reserve of free lots stays in the plan.
- **Acceptance (first values):** a medium town has at least 80 buildings within 150 m of its centre; at least 70 % of the length of urban streets has a building facing it; no dead end longer than 80 m; generation on the Large map under 5 s. Plus an SVG of a village, a medium town and a metropolis, and screenshots in the .NET build, day and night.

## Built (2026-10-23)

Sim side done and tested (909 tests); the game side compiles and the headless self-test starts, but the 3D look (pavements, lamps, street trees, ground tint, no disc at bends) was **not seen**.

- **Generator** (`StreetGenerator`): seed roads with a node every block step (64 to 70 m); two collectors (both sides) at each straight node; each step takes the nearest of "a collector goes one step further" (only after a local street closed a block at its end) or "a local street joins the ends of two neighbouring collectors at one depth". Streets may cross others at 40 degrees or more (new junction) or end at them. A gap-filling pass (`Infill`) runs twice (at 70 % and 100 % of the target people). Dead ends longer than `maxDeadEnd` are pruned.
- **Lots**: frontage, biggest tier (`LotSite.MaxTier`) from the ring (`frontage`, `maxTier` in `towns.rings`); the main street gets one tier more. Growth upgrades stop at the lot's `MaxTier`.
- **Snap**: `RoadNetwork.FindSnapNode` / `IsSnapTarget` (junctions, ends, corners from 20 degrees; not bends, bridge/tunnel ends, nodes under another road). `SnapToNode` uses it.
- **Urban**: `Simulation.IsUrbanRoad` (road middle inside a town's radius) drives kerb, pavement and lamps in `game/SimHostTown.cs`; `StreetEdge.Urban` is for the SVG only. Disc only where `NeedsDisc`.
- **Saves**: `FormatVersion` 4.

**Differences from the plan**
- Buildings within 150 m of a medium town's centre: about 45 to 55, not 80 (threshold in the test: 40). With 20 to 24 m buildings, 7 m streets plus 2.5 m pavements and a building clear of the pavement of a crossing street, a block holds about four buildings: at best one to 850 m2. 70 % of urban street length is faced (measured 92 to 96 %).
- People by size class: 400, 2500 and 8000 (plan: 400 to 800, 3000 to 6000, 15000+). Higher targets run into `outerReach` and leave no room for industries on some maps. Medium towns come out at 350 to 420 buildings, a metropolis at about 1200.
- Pass-through nodes in the sim are **not** free: `JunctionReach` is a static function of lane length used in many places and tests and changes when a third road joins. The generator makes few such nodes; only the drawing treats them as one ribbon.
- Not built: yard trees (only trees on the pavement), growth in play (R5).
- Test thresholds moved: station coverage share 0.12 to 0.08, maintenance share to 0.18, bus speed 4.5 to 3.5 m/s, mixed network level crossings 3 to 2; `Worlds.Connect` and the rail scenario cross road adapted to bigger towns.
- Generation of 14 towns on the Large map: 1.3 s.
