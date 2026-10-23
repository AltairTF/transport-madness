# R4: organic towns that grow street by street

Status: replanned with the developer on 2026-10-23. The first R4 plan (the alpha-model of Molinero and Hernando, arXiv 2001.08180: Delaunay triangulation, gravity pairs, shortest paths) was built as R4a to R4c and **dropped** after the first layouts were seen in the 3D build: the streets were a spider web, long dead-end spokes ran out into empty ground, the cells between streets were 100 to 200 m across and mostly empty, and the buildings stood inside the cells instead of facing a street. A town should look like the villages and towns of Transport Fever 2. R4 is the fourth slice of the world revamp (R1 terrain, R2 preview and sizes, R3 town sites, R4 streets, R5 growth in play). Scope: the first layout; but the generator is the same loop that R5 will run in play.

## What the reference shows

Five screenshots of Transport Fever 2 towns (a village, two medium towns, a coastal town and a river town) were studied on 2026-10-23.

- **Village:** a crossing of two or three through roads with buildings packed along them. The side streets are short stubs. There are no loops and no empty cells.
- **Medium town:** a core of irregular quadrilateral blocks with tall buildings; the outskirts are rows of houses along streets that end in stubs. The blocks are 60 to 100 m across and almost all built on.
- **Through roads** leave the town as country roads. They are the first thing laid.
- A street is never empty: buildings appear as soon as a street exists.

## The generator

A pure function: seed, centre, size class, terrain (or none) in; a street graph and the lots with their starting buildings out. It does not touch `RoadNetwork`, so it can be tested and drawn alone. Streets and buildings grow together.

1. **Seed roads.** Two or three through roads cross at the square, each running past the edge of the town (these become the country roads that join the towns). Their directions follow the terrain: along the contours of a slope, along a river or the coast, drawn at random on level ground. A metropolis gets more seed roads and one ring street.
2. **Growth loop.** Repeat until the size class's target number of buildings is reached (or no step fits any more). Each step has two parts:
   - Pick an existing street end or a point along a street, weighted toward the centre. Add one street piece of 40 to 90 m from it. The piece is one of: an **extension** (same direction, small bend toward the contour), a **branch** (about 90 degrees, plus or minus 15) or a **link** (to a street end or street within reach, which closes a block). It is refused when it crosses water that cannot be bridged, runs over ground steeper than `maxSlope`, comes closer than a building needs to a street that is not its parent, or leaves the map.
   - Lay the lots on both sides of the new piece and build on them straight away, by the lot's ring.
3. **Density by weights.** The share of extension, branch and link steps depends on the ring of the pick: the core mostly branches and links (small blocks of 60 to 100 m), the edge mostly extends and ends in stubs. A street end that is not extended or linked stays a short stub with houses along it, never a long spoke.
4. **Lots.** On both sides of a piece, every `lotWidth`, at the setback used so far (`avenueHalf + setbackGap + max footprint`, so a building can grow in place and a street can be widened). A lot is refused in water, off the map, closer than a footprint to another lot, or when another street is as near as its own. This is the existing `TownGenerator.StreetLots` rule applied piece by piece.
5. **Rings.** How full a lot is, which building types and which sizes, come from the distance to the centre over the radius (`RingAt(tuning, ratio)` plus noise), as now. The centre is dense and mixed with tall buildings; the edge is sparse and mostly homes.
6. **Targets by size class.** Village: about 40 to 70 buildings, two or three seed roads. Medium town: about 150 to 300. Metropolis: 600 or more, with a ring street. Exact numbers go in `towns.generation.streets` and are tuned by looking at the layouts.

## What stays and what goes

- Stays: `TownGenerator.StreetLots` (spacing and clearance rules), `RingAt(tuning, ratio)`, `PlanBuildings`, the `BuildRoad` calls in `BuildStreets`, `StreetLotTests` (spacing, road in reach, same seed), the main street as `Avenue2x2` with the rest `TwoLane`.
- Goes: the Delaunay triangulation, the gravity pairs and the shortest-path walk in `StreetGenerator`, the straightening pass, and the tuning keys that belong to them (`alpha*`, `placeSpacing`, `nodeSpacing`, `nodeJitter`, `waterPenalty`, `distanceDecay`, `pairBudget`, `straightenRounds`). Their tests go with them.
- New in `towns.generation.streets`: `seedRoads` by size class, `pieceLengthMin` and `pieceLengthMax`, the step weights by ring, `maxSlope`, `targetBuildings` by size class.

## Plumbing (as in the first plan)

- `TownPlan` gets `Radius` and an explicit list of lot sites; `Blocks` and `Angle` stay for towns from older code paths and are 0 for organic ones. `LotSite` keeps its fields; `I`, `J` are -1 and `U` is the distance ratio.
- `Town.Radius` and a flag for an organic town. `Simulation.Growth.InitLots` copies the plan's sites. `ExtendGrid` does nothing for an organic town until R5, which replaces it with this growth loop; the free lots kept in the plan (about a third more than the starting buildings use) give the town room to grow until then.
- The clearance formulas that use `Blocks * BlockLength * 0.5 * 1.42` (`IndustryGenerator`, `GatewayGenerator`, `Simulation.Orders`) use `Radius`. `PickSites` and `Suitable` use the radius of the size class instead of the grid's diagonal.
- `Town` fields are saved by reflection (`StateGraph`); `SaveGame.FormatVersion` goes up since the layout of `Town` changes.

## Slices

- **R4a'.** The growth loop as a pure function (seed roads, pieces, lots and buildings together) plus a test that writes an SVG of a village, a medium town and a metropolis so the layouts can be looked at before anything else. Tests: same seed gives the same town; every street joins the seed roads; no street or lot in water; no two lots closer than a footprint; every building has a road within `AccessRadius`; a village gets at least 40 buildings, a medium town at least 150; no dead-end street longer than a set length; a metropolis is generated within a time budget; the 8 m and 32 m terrain give the same towns.
- **R4b'.** `TownGenerator.BuildStreets` builds the graph through `BuildRoad` (polylines in short segments, bridges where a piece crosses water). Remove the alpha-model code and its tuning and tests.
- **R4c'.** The plumbing above; tests that read `Blocks` move to `Radius`; golden hashes of worlds with towns are regenerated and noted; the 8 m and 32 m preview agree on town sites.
- **R4d'.** Look at it in the .NET build (screenshots of a village, a medium town and the metropolis, day and night), time the generation on the Large map, update `project-status.md` and the town section of the docs.

## Risks and open points

- **Density.** The last attempt gave 15 to 55 buildings a town against 60 to 110 on the grid, because the lot spacing and street clearance cap how many lots fit. The growth loop stops at a building target instead of a street layout, and the tests above set minimums, so this cannot pass unnoticed.
- **Growth stalls.** Until R5 an organic town cannot add streets. Mitigation: spare lots in the plan. If towns stop growing too early in play, R5 comes sooner.
- **Traffic and junctions.** Many T-junctions and short pieces cost the traffic simulation more nodes. Measure with `--bench` before and after; keep segments at the existing 5 m step and avoid junctions closer than a vehicle length.
- **Balance.** Street lengths change walking distances, coverage and trip times, so the `Balance` tests and the failing `ARailwayCostsAboutTwiceWhatBusesCostForTheSameSeats_AndEarnsMore` may move. Run them after R4c' and note the result.
- **Not in R4:** growth in play, a zone map, plazas as their own objects, walls or a town edge, different street widths beyond the main street.
