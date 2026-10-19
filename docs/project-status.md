# Transport Madness: Project Status and Technical Overview

Last updated: M0 closed out (flat per-lane lookups, local lane-change reordering) and the M1 cleanup done (downgrade, speed-limit change, move node, flat junction conflict tables, vehicles drawn along junction paths, camera that follows the ground, chunked terrain mesh); player actions are now commands in a replayable log, and tuning and prices live in `data/tuning.json`. Polish items listed in section 11. Vocabulary is in `/CONTEXT.md`, the plan in `/docs/game-plan.md`, decisions in `/docs/adr/`. This file records what exists, how it works, what was learned, and what comes next.

## 1. Where the project stands

| Milestone | State |
|---|---|
| M0 Dynamic traffic prototype | All five exit criteria met (see section 2); cleanup items closed (section 11) |
| M1 Procedural terrain, road building with cut and fill | Done: terrain, elevation, grades, cut and fill cost and carving, arcs, path-based junction conflicts, road removal, upgrade, downgrade, speed limits and moving a node (sections 5.1, 5.6, 5.7); what is left (bridges, water, vehicles driving the junction path) is in section 11 |
| M2 Stations, lines, player vehicles, basic economy | Not started |
| M3 Towns, industries, cargo chains, trip flows | Not started |
| M4 Full rail | Not started |
| M5 Maintenance, depots, full incidents, contracts | Partly seeded (incidents, depots, crews exist in a minimal form) |
| M6 Eras, save/load, overlays, art, audio | Not started |

## 2. M0 exit criteria

| # | Criterion | State | Evidence |
|---|---|---|---|
| 1 | Flat terrain, build 2-lane and 2x2 road segments with junctions | Done | `RoadNetwork.BuildRoad` splits roads it ends on or crosses; `BuildRoadTests`; `--selftest-build` drives the real tool with synthetic clicks |
| 2 | 1000 ambient vehicles at 60 fps at 1x; at least 10 fps at 8x | Met on the development machine (Apple M4) | `--bench`: 60 fps at 1x, 65 fps at 8x with the simulation at full 8x; one step costs about 0.5 ms |
| 3 | Visible overtaking on 2-lane roads, occasional failures become accidents | Done | `OvertakingTests`, `ScenarioTests`; about one head-on per 70 overtakes in long runs |
| 4 | An accident blocks the road, a crew clears it, traffic reorganizes | Done | `IncidentTests`; `--selftest-crash` (about 140 s to arrival from a far depot, 45 s to clear) |
| 5 | Headless deterministic test | Done | `TrafficTests.SameSeed_GivesIdenticalStateAfterManySteps` |

The frame-rate numbers were measured once on one machine and are not asserted by any automated test. The only automated performance check is a loose step-time bound (`ThousandVehicles_StepWellInsideTheEightTimesSpeedBudget`).

## 3. Repository layout

```
CONTEXT.md                 domain vocabulary
README.md                  how to run, M0 status table
docs/game-plan.md          pitch, pillars, systems, milestones
docs/adr/                  0001 pure C# core, 0002 kinematic lanes + fidelity tiers, 0003 deterministic fixed step
docs/project-status.md     this file
data/tuning.json           tuning and prices, embedded in the sim assembly (section 8)
sim/                       the simulation (pure C#, no Godot types) and its tests; Godot ignores it (.gdignore)
                             SimCommand.cs, Simulation.Commands.cs: player actions, command log, replay
                             Tuning.cs: typed view of data/tuning.json
  TransportMadness.Sim/      library
  TransportMadness.Sim.Tests/ xUnit tests
game/                      Godot side: SimHost.cs, DemoWorld.cs, GDScript (main, hud, camera_rig, build_tool), main.tscn
transport-madness.csproj   Godot .NET project; references sim/, excludes sim/** from its own compile
addons/                    godot_ai and yard (third party, untouched)
```

Tooling: Godot 4.7.2 .NET editor build, .NET 8 SDK (installed per user in `~/.dotnet` because the cask needs sudo). The root project and the simulation both target `net8.0`.

## 4. Architecture

Follows ADR 0001: the simulation assembly never references Godot. `SimHost` (C#, in the Godot project) owns a `Simulation`, advances it in fixed steps against the frame clock and draws it. GDScript only emits intent (speed, tool, action) and calls `SimHost`'s PascalCase methods.

Stepping (`SimHost._Process`): accumulates `delta * speed`, runs whole 0.05 s steps while under a wall-clock budget (10 ms at 1x, 70 ms above), and drops the backlog when over budget, so effective speed degrades instead of the frame rate (ADR 0002). The wall clock only decides how many steps run, never what a step computes.

Rendering: roads are boxes with a centre line (avenues get a median); every vehicle is one instance of a single `MultiMesh` filled in bulk each frame; rescue crews use a second small `MultiMesh`. Vehicle positions are extrapolated by a fraction of a step so motion is smooth between the 20 Hz steps. The buffer starts at 2048 instances and doubles when needed.

## 5. Simulation model

### 5.1 Road network (`RoadNetwork`)
- Roads are straight or circular arcs (`RoadGeometry`, section 5.7). Nodes have x/z and a height; a `Road` joins two nodes and owns `ForwardLanes` and `BackwardLanes` (lane 0 is right-most).
- `TwoLane`: one lane per direction, opposing lanes linked as `Oncoming` (overtaking allowed). `Avenue2x2`: two lanes per direction, linked `Left`/`Right`, no `Oncoming`.
- Junction connections are rebuilt whenever a road is added: each incoming lane connects to the lane of every other road at the node, same lane index where possible; a dead end turns traffic around onto the opposite lane.
- `MovesConflict(inA, outA, inB, outB)`: two moves conflict if they enter the same lane (merge) or their paths cross. Each move has a path: a cubic curve from where the incoming lane enters the junction area (8 m before the node, at the lane's own offset) to where the outgoing lane leaves it, keeping each lane's heading. Two moves conflict when their paths cross (segment intersection on 12-point polylines), so lane offsets, junction width and arcs count, and the left turn against oncoming straight traffic is a conflict. Results are cached per move pair (lane geometry never changes). The simulation keeps its own flat copy: each node has a small square table over its moves (an incoming lane with one of its next lanes), filled on first use and laid out again when the network changes (`SyncConflictTables`); a step with 1000 vehicles got about 7 percent faster.
- `JunctionPoint(from, to, fraction)` gives the pose along the same path; the game draws vehicles with it inside a junction (`JunctionReach`: 8 m, or half a short lane). The simulation still moves a vehicle across the node in one step.
- `BuildRoad` plans first (null means "cannot build"): snaps to nodes within 0.5 m at its ends, finds every road it ends on or crosses, splits those roads, then adds the pieces. Refused when shorter than `MinRoadLength` (10 m), when a piece duplicates an existing road, or when it would cross within 10 m of another junction.
- `SplitRoad` retires the old road and lanes and records for each old lane its two replacements and the split distance. Vehicles migrate in `Simulation.SyncNetwork`.

### 5.2 Vehicles and the step (`Simulation`, partial class)
Data layout: parallel arrays indexed by vehicle id (lane, position, speed, traits, manoeuvre state, kind...). Position is the front bumper's distance along its lane. Vehicles are grouped per lane in ascending position (counting sort by lane, insertion sort by position, ties broken by id), rebuilt each step (a lane change re-files just that vehicle, `MoveInOrder`). Lane links (`Oncoming`, `Left`, `Right`, `Next`, road) are flat arrays refreshed when the network version changes. Ids are reused after a vehicle is removed.

Step order: `SyncNetwork`; `DecideManoeuvres`; `PrepareOvertakes` (observe oncoming traffic, advance overtakes); acceleration for every vehicle from the start-of-step state; integrate and advance lanes; `RebuildOrder`; `DetectIncidents` (collisions, debris, crews); `Tick++`.

Car-following is the Intelligent Driver Model, taking the most restrictive of: the leader in the lane (or the rearmost vehicle on the lane about to be entered), an oncoming overtaker, and junction yielding. Braking is capped at 9 m/s^2 and builds up at most 15 m/s^3, so drivers need a moment to react; that delay is what makes some situations unrecoverable.

Junctions are uncontrolled and zipper-style: within 40 m of a junction a vehicle yields to any vehicle on another approach that is closer to it and whose move conflicts, treating it as a virtual leader.

### 5.3 Manoeuvres
- **Lane change** (avenues): evaluated every 0.5 s per vehicle (staggered by id) when held up by a slower leader; gap checks ahead and behind in the target lane; returns to the right lane once the way is clear. Not within 30 m of either end of a lane. The sim swaps lanes instantly; `VehicleState.Shift` carries a 3.5 m offset that fades over 1.25 s so the renderer can show a smooth move.
- **Overtaking** (2-lane roads): after being stuck behind a slower leader for `1.5 + 6 * patience` seconds, a driver pulls into the oncoming lane if it believes the pass fits. Phases: `MovingOut`, `Passing`, `Returning`. Attention limits how far the driver sees, makes it underestimate oncoming speed and how long the pass takes; aggression shrinks the safety margin. It never overtakes a halted vehicle, a queue with a halted vehicle within 80 m, or a leader slower than 1 m/s, and not within 15 m of the start of a lane or when the lane is too short.
- **Giving up:** an overtaker aborts when oncoming time-to-collision is too short, when something is within 15 m, or within 30 m of a junction: back in behind its target if still behind it, straight in if already clear. Alongside the target it cannot back out. An overtaker standing still nose to nose with oncoming traffic for 4 s cuts back in regardless (a sideswipe if the target is still beside it).
- Oncoming traffic brakes for an overtaker through a virtual leader; overtakers out in the oncoming lane follow each other.

### 5.4 Incidents, depots and crews
- **Detection** by box overlap: follower against the vehicle in front (also across a lane end), and each overtaker against everything in its own and the oncoming lane (oncoming positions mapped into its frame, lateral offset `3.5 m * lateral`, vehicle width 1.9 m). Kinds: `Breakdown`, `RearEnd`, `HeadOn`, `Sideswipe`.
- **Wrecks** are halted vehicles (`VehicleKind.Wreck`); a head-on with the overtaker out in the oncoming lane also spawns `Debris` in its own lane so both directions are blocked. A car that hits a wreck joins that incident. Wrecks count against the lane they physically block, and drivers prefer turns onto unblocked roads.
- **Crews**: `AddDepot(node, level)`. A new incident takes the nearest depot with a free crew: arrival after `10 s + straight distance * 1.4 / 16 m/s`; clearing takes 45 s, then wrecks are removed and the crew is free again (it does not drive back). If that response would take 300 s or more, or no depot exists, or every crew has been busy for 300 s, a distant fallback service arrives instead. A car piling into a wreck being cleared restarts the clearing timer.

### 5.5 Determinism (ADR 0003)
One seeded RNG (`SimRandom`, xoshiro256** seeded through SplitMix64, own implementation so it never depends on the runtime), vehicles processed in id order, no wall-clock or Godot values inside a step. `StateHash()` is FNV-1a over all vehicle state, incident and depot state, the network version and the RNG state. Roads built while running are picked up at the next step or spawn.

Player actions are commands (`SimCommand`: `BuildRoadCommand`, `RemoveRoadCommand`, `UpgradeRoadCommand`, `DowngradeRoadCommand`, `SetSpeedLimitCommand`, `MoveNodeCommand`, `AddDepotCommand`, `SeedTrafficCommand`, `CrashSomeoneCommand`). `Simulation.Execute` applies one between steps and appends it to `CommandLog` with the tick it ran at (refused commands too: they change nothing and are refused again on replay). `Simulation.Replay(log, untilTick)` runs a fresh simulation (same seed, same initial network) to a tick, executing the log at the recorded ticks. `DeterminismTests` builds its whole world through commands, so the log plus the seed alone reproduces the run. Anything that changes the network or the simulation without going through `Execute` is not replayable; the game's `DemoWorld` grid is the one such initial world (the demo's depots and traffic are commands).

### 5.7 Arcs, removal and upgrade (M1)
- `RoadGeometry` is a straight line or a circular arc: start point, start heading, signed sweep (total turn, positive is an increasing heading) and arc length. `PointAt(s)` and `HeadingAt(s)` take distances along the centre line; `Slice` and `Reversed` give the parts and the other direction. Positions of vehicles on every lane of a road are distances along the road's centre line, so parallel lanes share a length; the lane's sideways offset is only used for drawing, junction paths and incident points.
- `AddRoad(..., sweep)`; `BuildRoad(..., sweep)`. Limits: radius at least 25 m (`MinRadius`), total sweep at most 2.8 rad. Curve speed limit is `sqrt(3 * radius)` (about 8.7 m/s at 25 m, 17 m/s at 100 m), combined with the grade factor. Splitting an arc splits its sweep in proportion.
- `Plan` intersects straight against straight analytically as before; any arc involved uses 3 m polylines with a bounding box test, then the same rules (ends within 0.5 m snap to nodes, no junction within 10 m of an end).
- The tool draws a curve from three clicks: start, a point on it, end (`RoadGeometry.SweepThrough`). Terrain carving follows the polyline of the arc (`Terrain.CarvePath`).
- Terrain is never restored: removing or moving a road keeps its cut and fill, as a real demolition would.
- `RemoveRoad(id)` retires the road's lanes with no replacement; the simulation removes vehicles on them (wrecks included, taking them out of their incident) and rebuilds the junctions at both ends, so a node left with one road becomes a dead end. Cost is `10 per metre` demolition; the terrain keeps its cut and fill.
- `UpgradeRoad(id, speed)` turns a two-lane road into an avenue on the same centre line; vehicles move to the matching new lane. Cost is the avenue price minus half the two-lane price, plus earthworks for the extra width. Only two-lane roads can be upgraded.
- `DowngradeRoad(id, speed)` narrows an avenue to a two-lane road on the same centre line (`DowngradeCostPerMetre`; the ground keeps its earthworks). Right-lane vehicles carry on in the new lane; left-lane vehicles are moved into it as well, and one that lands within a vehicle length of another is removed (`Lane.SplitMerges`, checked in `SyncNetwork`).
- `SetSpeedLimit(id, limit)` re-signs a road (`SpeedLimitChangeCostPerMetre`; refused when unchanged or not a positive finite number). Upgrade, downgrade and speed change share `Reshape`: retire the old road, add a new one on the same nodes and sweep, hand every old lane to the matching new one. Lanes are immutable, so a limit change is a new road, not an edit.
- `MoveNode(node, x, z)` retires every road at a node and rebuilds each straight from the new place to its other end (curves become straight). The price is demolition plus new construction (`EstimateMoveNode`). Refused when the node has no roads, the target is within 1 m of the old place or on another node, a rebuilt road would be too short or too steep, it would cross another road, or two rebuilt roads would leave in the same direction (under 0.05 rad apart). Vehicles on those roads disappear; a depot at the old place closes.
- A depot whose node has lost every road is closed (`Depot.Active` false, checked in `SyncNetwork`): it sends no more crews, a crew on its way is lost and the incident falls back to the distant service, counted from that moment. `AddDepotCommand` is refused at a node with no road. The state hash only mixes in a closed depot, so runs without closures keep their hash.
- `RoadNetwork.Version` increases on every change; the simulation re-syncs when it differs, so a change that adds no lanes (removal) is noticed too.

### 5.6 Terrain and elevation (M1)
- `Terrain` is a heightmap centred on the origin: 512 x 512 cells of 8 m (4 km square), divided into 16 x 16 chunks of 32 cells. Carving a road records which chunks it touched (`TakeChangedChunks`) so a renderer rebuilds only those. `Terrain.Generate(seed)` sums five octaves of integer-hashed value noise and shapes it so there are plains between hills (up to about 110 m); `Terrain.Flat()` is level. Same seed, same map (`Hash()` in tests). `HeightAt` is bilinear.
- `RoadNetwork(Terrain?)`: nodes have a height (terrain height when created on the ground, interpolated along the road when a junction splits it). A road's profile is the straight line between its node heights. `Lane.Grade` is rise over run in the direction of travel; the lane's `SpeedLimit` falls linearly from the posted value at 4 percent to half of it at 12 percent. Lane length is still the horizontal distance.
- `Plan` refuses a piece steeper than `MaxGrade` (12 percent), so `CanBuildRoad` and `BuildRoad` check it.
- `EstimateCost` returns length, cut and fill volumes and money: the volume between ground and road surface sampled every 4 m, cross section `h * width + 1.5 * h^2`, plus a price per metre by road type. Prices are placeholders until the economy exists (M2).
- `BuildRoad` then carves the terrain (`Terrain.CarveRoad`): the carriageway becomes level with the road, blending back to the old ground over a 6 m shoulder; the flat zone is never narrower than 1.42 cells so every sample read under the road is level. This bumps `Terrain.Version`.

## 6. Game layer (`game/`)

- `SimHost.cs`: world creation (`NewDemoWorld`, `NewEmptyWorld`, `NewHillsWorld`), player actions as commands (`BuildCurve`, `RemoveRoad`, `UpgradeRoad`, `BuildDepot`, `SpawnTraffic`, `CrashSomeone` all go through `Sim.Execute`), `RayToGround` (picking against the terrain), `HeightAt`, `EstimateRoadCost`, terrain mesh (one `MeshInstance3D` per chunk; a new terrain builds all 256, a carved road rebuilds only the chunks it touched), roads and vehicles that follow elevation and grade, speed, stats, build commands (`BuildRoad`, `CanBuildRoad`, `SnapToNode`, `BuildDepot`), `SpawnTraffic`, `CrashSomeone` (picks a moving car deterministically from the tick), drawing.
- `build_tool.gd`: mouse to build commands. Road tools (upgrade, downgrade, remove, faster, slower) act on the road piece under the mouse and show their cost on hover; Move picks a junction with one click and drops it with the next, previewing the rebuilt roads and the price. Building and picking: picking on the terrain. While previewing it shows length, grade, cut, fill and cost. Snaps to nodes within 20 m, else to a road's centre line within 12 m (a junction forms there), else to 15 degree steps and 5 m lengths; green or red preview; roads chain from the last end point; Esc stops.
- `hud.gd`: speed buttons (pause, 1x to 8x), tool buttons, action buttons, statistics.
- `camera_rig.gd`: free orbit camera; the focus eases to the terrain height under it (`terrain_host`), so panning over hills keeps the view level with the ground.
- `main.gd`: environment, wiring, and the command-line harness.

Command line (after `--`): `--hills`, `--selftest-hills`, `--bench`, `--selftest-build`, `--selftest-crash`, `--shot=<file>`, `--shot-after=<s>`, `--distance`, `--pitch`, `--focus=<x>,<z>`.

Demo world: 10 x 10 grid, 250 m spacing, an avenue on every third row, 4 simple depots, 1000 cars.

## 7. Tests

135 xUnit tests, run with `cd sim && dotnet test` (about 2 s). By seam:

| File | Covers |
|---|---|
| `RoadNetworkTests`, `BuildRoadTests`, `GrowingNetworkTests` | Lane graph, junction connections, conflict test, building, splitting, growing while running |
| `SimulationTests`, `LaneChangeTests`, `OvertakingTests`, `JunctionTests` | Following, junction crossing, lane changes, overtaking and aborting, driver traits, zipper yielding |
| `CurveTests` (`RoadGeometryTests`, `CurvedRoadTests`, `JunctionPathTests`, `CurvedTrafficTests`), `EditRoadTests` | Arc geometry, curve limits, junctions on arcs, path-based conflicts, traffic on a ring of arcs, removal, upgrade, picking |
| `TerrainTests`, `TerrainRoadTests` | Seeded generation, interpolation, carving, grade, grade speed limit, grade refusal, cut and fill cost |
| `IncidentTests` | Wrecks, blocking, crews, response time, busy crews, head-on, avoiding blocked roads |
| `TrafficTests`, `ScenarioTests` | Seeded determinism, sanity bounds, step-time budget, no lock-up on a dense map, long runs with overtaking, accidents and clearing |
| `DeterminismTests` | Ten-minute scenario built entirely by commands (build, upgrade, remove, depots, traffic, crashes, crews, overtaking): two runs agree, replaying the command log reproduces the run, a pinned golden hash; a second pinned hash for an avenue scenario that exercises lane changes |
| `ReshapeRoadTests`, `DepotClosureTests` | Downgrade (lanes, refusals, cost, no overlap, no false crash, removal of a vehicle with no room), speed-limit change, the commands, depot closing and the lost crew |
| `MoveNodeTests`, `JunctionPointTests`, `TerrainChunkTests`, `FindNodeTests` | Moving a node (rebuild, refusals, crossing, price, vehicles, replay, hills and grade), the pose along a junction path, changed terrain chunks, nodes no road reaches |
| `TuningTests` | The shipped file loads, a missing or unknown key fails, prices and crew counts follow the tuning, driving tuning changes behaviour |

The suite is the main safeguard for tuning: the traffic model is sensitive (a small change to a margin once locked the whole demo map, found only by a 20-minute run with 1000 cars).

## 8. Tuning values

`data/tuning.json` is the only place the behaviour numbers and prices live. It is embedded in the simulation assembly (so the sim stays free of file paths and Godot) and read by `Tuning.Load()` into typed records; the network owns the `Tuning` (`RoadNetwork(terrain, tuning)`, default is the shipped file) and the simulation reads it from there. Every key is required and unknown keys are rejected, so a typo fails at load. Sections: `driving` (IDM, braking, junction yielding), `traffic` (spawning and driver speed spread), `manoeuvres` (decision interval, lateral rate, stall), `laneChange`, `overtake` (every threshold and driver-trait formula), `crews` (timings, crews per depot level), `roads` (speed limit and price per type, earthworks, demolition, upgrade refund, downgrade and re-signing prices). A change to a number is a behaviour change: the golden hash in `DeterminismTests` will move and the traffic tests must still pass (see section 7).

Still in code, on purpose: step 0.05 s (ADR 0003); vehicle 4.5 x 1.9 m and lane width 3.5 m (vehicle sizes move to the vehicle roster at M2); the lateral thresholds `HomeLaneLat` 0.5 and `OncomingSightLat` 0.3 (they define when a vehicle counts as being in the other lane); road geometry rules in `RoadNetwork` (minimum length, maximum grade, minimum radius, maximum sweep, curve comfort). Vehicle roster, eras and economy data come later as their own files.

## 9. Findings worth remembering

- Anything that lets a vehicle appear in front of traffic that cannot see it (lane change right after a junction) produces rear-end pile-ups; the fix is margins, not braking.
- A head-on standoff between an overtaker and stopped oncoming traffic is a permanent deadlock unless something forces a resolution; queues then spread across the grid.
- Strong braking hides failures: with instant full braking almost no overtake ended in a crash. Reaction delay (jerk limit) and attention are what make rare failures happen.
- The old chord approximation of junction paths ignored every pair of moves that shared an arm, including a left turn against the oncoming straight traffic. Real paths found those conflicts; the extra waiting built long crawling queues at junctions, and overtakers then tried to pass them and sideswiped oncoming cars. Fix: no overtake starts with a queue (vehicles under 2 m/s) within 100 m ahead of the target.
- A junction path needs a length: lanes meet at the node point, so paths start and end 8 m from it.
- `unproject_position` returns viewport coordinates and synthetic input events need window coordinates; with a stretch mode the two differ, so the self-tests convert through `get_final_transform()`.

## 10. Known limitations and technical debt

Behaviour:
- Ambient traffic is a random walk (no origins, destinations or routing); drivers only steer away from blocked roads.
- Only tier 0 fidelity exists (ADR 0002 tiers 1 and 2 are not built); 1000 vehicles are all fully simulated.
- Accident causes are limited to overtaking, rear-ending and junction merges; no curves, weather, signals, level crossings or condition.
- Crews are abstract (a straight line from the depot, no road travel, no return trip); depot level only sets crews (Complex has 2).
- Removal and moving leave the terrain as it was carved, on purpose: demolishing a road does not rebuild the hill (decision, not a gap); moving a node straightens curved roads and drops the vehicles on them; no money exists yet, costs are only numbers.
- Junction paths decide conflicts and are used to draw turns, but the simulation still moves vehicles across the node in one step (so conflict checks use lane speeds, not real closing speeds), and the lane length is the centre-line length for every lane of a curve.
- An overtaker alongside its target at a lane end can reappear overlapping it.
- Tiny dense networks (a few roads, dozens of cars) still produce occasional pile-ups at junctions.

Engineering:
- The golden hashes in `DeterminismTests` (`GoldenHash`, `AvenueGoldenHash`) were pinned on the development machine (macOS arm64). `MathF.Sin/Cos/Sqrt` come from the platform's maths library, so another OS or CPU may need its own value or a deliberate check.
- The command log is in memory only; saving it (and a snapshot, M6) is not done. `DemoWorld` builds its grid outside the log.
- Cleared incidents are kept forever (only the open list is scanned each step). Spawning a vehicle, clearing an incident and a network change still rebuild the whole per-lane ordering; only a lane change is local.
- Tuning is read through section records cached in `Simulation` fields rather than constants; step time at 1000 vehicles measured 0.8 to 1.1 ms in `--bench` after the change (it was about 0.9 ms), so nothing is gained by flattening it yet.
- `SpawnDebris` goes through `SpawnVehicle` (draws RNG, rebuilds order) although debris never drives.
- `SimHost` holds some game rules (victim choice, depot snap radius, one depot per node) that belong in the simulation or a rules layer.
- `DepotLevel` and the crew count anticipate M5; the depot level only sets the number of crews (in `data/tuning.json`) for now.
- `main.gd` carries the benchmark and self-test harness.
- Frame-rate numbers are manual, not part of CI.

## 11. What comes next

**Close out M0 (small)**
Done: command log with tick stamps and replay, determinism test over crashes, crews and overtaking with a golden hash, tuning and prices in `data/tuning.json`, `CONTEXT.md` brought in line (Wreck, Debris, Command, depot levels Simple, Workshop, Complex).
Also done: every per-lane lookup the hot loops use (length, limit, end node, road, `Oncoming`, `Left`, `Right`, `Next`) is a flat array in `Simulation`, refreshed when the network version changes; a lane change re-files only that vehicle (`MoveInOrder`) instead of rebuilding the ordering. A temporary check against `RebuildOrder` over 4533 lane changes found no difference, and `DeterminismTests` pins an avenue scenario (`AvenueGoldenHash`) that exercises lane changes, which the main golden run does not. Step time was not re-measured (`--bench` needs Godot).
Open: none.

**M1: terrain and road building with cut and fill**
Done: seeded heightmap, lane elevation and grade with speed limits, cut and fill cost and terrain carving, build tool shows the cost, picking and drawing on terrain.
Also done: circular arcs with curve speed limits, junction conflicts from real path geometry, road removal and upgrade.
Cleanup done: downgrade, speed-limit change and moving a node (commands, costs, tests, tools `Downgrade`, `Faster`, `Slower`, `Move`); a depot closes when its node loses every road; `MovesConflict` flattened into per-node tables; vehicles are drawn along the junction path; camera focus follows the ground; terrain drawn in chunks.
Open:
- Bridges and tunnels do not exist: a road crossing a valley is a long fill.
- Rivers and water (needed before towns in M3).
- Vehicles still cross the node in one step; following the junction path in the simulation (a position inside the junction) would let `MovesConflict` use real closing speeds.
- Moving a node keeps no curves and loses the vehicles on its roads; a node cannot be dragged across another road.

**M2: stations, lines, player vehicles, basic economy**
- Company, money, loan; stations with coverage radius; lines; bus and truck owned by the company (these are the tier 0 vehicles of ADR 0002).
- Give vehicles real routes (needed for lines); keep the random walk for ambient traffic until trip groups exist.
- Introduce tier 1 and tier 2 ambient traffic here, once the road network is large enough for 1000 fully simulated vehicles to stop being the norm.

**M3 to M6**
- Towns, buildings, trip groups, industries and cargo chains (M3); block and path signals, level crossings, deadlock detection (M4); condition, patch/service/overhaul, depot levels as capability, contracts, incident variety and frequency option (M5); eras, save and load (snapshot plus seed), overlays, balancing, art, audio (M6).

**Cross-cutting**
- Replace greybox boxes with art at M5/M6; introduce a CI job running `dotnet test` and a headless benchmark so performance regressions are caught.
- Revisit tick rate and the multithreading question (partitioned and ordered, per ADR 0003) only after profiling a realistic M2/M3 world.
