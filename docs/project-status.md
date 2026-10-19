# Transport Madness: Project Status and Technical Overview

Last updated: M0 core complete. Vocabulary is in `/CONTEXT.md`, the plan in `/docs/game-plan.md`, decisions in `/docs/adr/`. This file records what exists, how it works, what was learned, and what comes next.

## 1. Where the project stands

| Milestone | State |
|---|---|
| M0 Dynamic traffic prototype | All five exit criteria met (see section 2) |
| M1 Procedural terrain, road building with cut and fill | Not started |
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
sim/                       the simulation (pure C#, no Godot types) and its tests; Godot ignores it (.gdignore)
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
- Straight segments only, flat terrain. Nodes have x/z; a `Road` joins two nodes and owns `ForwardLanes` and `BackwardLanes` (lane 0 is right-most).
- `TwoLane`: one lane per direction, opposing lanes linked as `Oncoming` (overtaking allowed). `Avenue2x2`: two lanes per direction, linked `Left`/`Right`, no `Oncoming`.
- Junction connections are rebuilt whenever a road is added: each incoming lane connects to the lane of every other road at the node, same lane index where possible; a dead end turns traffic around onto the opposite lane.
- `MovesConflict(inA, outA, inB, outB)`: two moves conflict if they enter the same lane (merge) or their paths cross. Paths are approximated as chords between the arms they use; interleaving chords cross.
- `BuildRoad` plans first (null means "cannot build"): snaps to nodes within 0.5 m at its ends, finds every road it ends on or crosses, splits those roads, then adds the pieces. Refused when shorter than `MinRoadLength` (10 m), when a piece duplicates an existing road, or when it would cross within 10 m of another junction.
- `SplitRoad` retires the old road and lanes and records for each old lane its two replacements and the split distance. Vehicles migrate in `Simulation.SyncNetwork`.

### 5.2 Vehicles and the step (`Simulation`, partial class)
Data layout: parallel arrays indexed by vehicle id (lane, position, speed, traits, manoeuvre state, kind...). Position is the front bumper's distance along its lane. Vehicles are grouped per lane in ascending position (counting sort by lane, insertion sort by position, ties broken by id), rebuilt each step. Ids are reused after a vehicle is removed.

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
One seeded RNG (`SimRandom`, xoshiro256** seeded through SplitMix64, own implementation so it never depends on the runtime), vehicles processed in id order, no wall-clock or Godot values inside a step. `StateHash()` is FNV-1a over all vehicle state, incident and depot state and the RNG state. Roads built while running are picked up at the next step or spawn.

## 6. Game layer (`game/`)

- `SimHost.cs`: world creation (`NewDemoWorld`, `NewEmptyWorld`), speed, stats, build commands (`BuildRoad`, `CanBuildRoad`, `SnapToNode`, `BuildDepot`), `SpawnTraffic`, `CrashSomeone` (picks a moving car deterministically from the tick), drawing.
- `build_tool.gd`: mouse to build commands. Snaps to nodes within 20 m, else to a road's centre line within 12 m (a junction forms there), else to 15 degree steps and 5 m lengths; green or red preview; roads chain from the last end point; Esc stops.
- `hud.gd`: speed buttons (pause, 1x to 8x), tool buttons, action buttons, statistics.
- `camera_rig.gd`: free orbit camera.
- `main.gd`: environment, wiring, and the command-line harness.

Command line (after `--`): `--bench`, `--selftest-build`, `--selftest-crash`, `--shot=<file>`, `--shot-after=<s>`, `--distance`, `--pitch`, `--focus=<x>,<z>`.

Demo world: 10 x 10 grid, 250 m spacing, an avenue on every third row, 4 simple depots, 1000 cars.

## 7. Tests

59 xUnit tests, run with `cd sim && dotnet test` (about 2 s). By seam:

| File | Covers |
|---|---|
| `RoadNetworkTests`, `BuildRoadTests`, `GrowingNetworkTests` | Lane graph, junction connections, conflict test, building, splitting, growing while running |
| `SimulationTests`, `LaneChangeTests`, `OvertakingTests`, `JunctionTests` | Following, junction crossing, lane changes, overtaking and aborting, driver traits, zipper yielding |
| `IncidentTests` | Wrecks, blocking, crews, response time, busy crews, head-on, avoiding blocked roads |
| `TrafficTests`, `ScenarioTests` | Determinism, sanity bounds, step-time budget, no lock-up on a dense map, long runs with overtaking, accidents and clearing |

The suite is the main safeguard for tuning: the traffic model is sensitive (a small change to a margin once locked the whole demo map, found only by a 20-minute run with 1000 cars).

## 8. Tuning values (all in code for now)

Defined in `Simulation*.cs`: step 0.05 s; vehicle 4.5 x 1.9 m; lane width 3.5 m; IDM acceleration 1.5, comfortable deceleration 2.0, minimum gap 2 m, headway 1.5 s; braking cap 9, jerk 15; decisions every 10 ticks; lane-change cooldown 60 ticks, overtake cooldown 100; lateral rate 0.8 per second; lane margins 30 m (lane change), 15 m (overtake start), 30 m (abort near end); stall limit 4 s; junction range 40 m, conflict clearance 6.5 m; crew timings in 5.4. Road speed limits (14 and 19 m/s) are in `SimHost.BuildRoad` and `DemoWorld`. The game plan says these belong in data files.

## 9. Findings worth remembering

- Anything that lets a vehicle appear in front of traffic that cannot see it (lane change right after a junction) produces rear-end pile-ups; the fix is margins, not braking.
- A head-on standoff between an overtaker and stopped oncoming traffic is a permanent deadlock unless something forces a resolution; queues then spread across the grid.
- Strong braking hides failures: with instant full braking almost no overtake ended in a crash. Reaction delay (jerk limit) and attention are what make rare failures happen.
- A chord approximation of junction paths is enough to keep crossing traffic apart but ignores closing speed.
- `unproject_position` returns viewport coordinates and synthetic input events need window coordinates; with a stretch mode the two differ, so the self-tests convert through `get_final_transform()`.

## 10. Known limitations and technical debt

Behaviour:
- Ambient traffic is a random walk (no origins, destinations or routing); drivers only steer away from blocked roads.
- Only tier 0 fidelity exists (ADR 0002 tiers 1 and 2 are not built); 1000 vehicles are all fully simulated.
- Accident causes are limited to overtaking, rear-ending and junction merges; no curves, weather, signals, level crossings or condition.
- Crews are abstract (a straight line from the depot, no road travel, no return trip); depot level only sets crews (Complex has 2).
- Roads are straight, flat; no way to remove or upgrade a road; a depot sits on a node; no cost for anything.
- An overtaker alongside its target at a lane end can reappear overlapping it.
- Tiny dense networks (a few roads, dozens of cars) still produce occasional pile-ups at junctions.

Engineering:
- No command log: player actions (`BuildRoad`, `Crash`, `SpawnTraffic`) are applied at frame boundaries without a tick stamp, so a run with actions cannot be replayed (ADR 0003 promises "same seed and same actions").
- The determinism test only covers seeded traffic, not crashes, depots, crews or overtaking, and does not pin a golden hash across platforms.
- Hot loops still read lane data through `RoadNetwork` objects (`Oncoming`, `Left`, `Right`, `Next`); length, limit and end node are already flat arrays. `ChangeLane` rebuilds the whole ordering. Cleared incidents are kept forever (only the open list is scanned each step).
- Tuning constants are in code; some manoeuvre thresholds are unnamed literals.
- `SpawnDebris` goes through `SpawnVehicle` (draws RNG, rebuilds order) although debris never drives.
- `SimHost` holds some game rules (victim choice, depot snap radius, one depot per node) that belong in the simulation or a rules layer.
- Vocabulary drift: `Wreck`, `Debris` and `DepotLevel.Workshop`/`Complex` are not in `CONTEXT.md` ("complete Depot" there); `DepotLevel` and the crew count anticipate M5.
- `main.gd` carries the benchmark and self-test harness.
- Frame-rate numbers are manual, not part of CI.

## 11. What comes next

**Close out M0 (small)**
1. Add a command log with tick stamps for player actions and extend the determinism test to cover crashes, crews and overtaking (and a golden hash).
2. Move tuning to data files and name the remaining literals.
3. Flatten the remaining per-lane lookups; make lane change local instead of a full reorder.
4. Update `CONTEXT.md` with Wreck, Debris and the depot level names; reconcile "complete Depot" with `Complex`.

**M1: terrain and road building with cut and fill**
- Heightmap terrain from a seed; lane geometry gains elevation and grade (speed limits on grades).
- Roads as straight segments and circular arcs with angle and node snapping; the junction chord test then needs real path geometry.
- Cost model for cut and fill; the build tool shows the cost; road removal and upgrade.
- Camera and picking against terrain instead of the flat plane.

**M2: stations, lines, player vehicles, basic economy**
- Company, money, loan; stations with coverage radius; lines; bus and truck owned by the company (these are the tier 0 vehicles of ADR 0002).
- Give vehicles real routes (needed for lines); keep the random walk for ambient traffic until trip groups exist.
- Introduce tier 1 and tier 2 ambient traffic here, once the road network is large enough for 1000 fully simulated vehicles to stop being the norm.

**M3 to M6**
- Towns, buildings, trip groups, industries and cargo chains (M3); block and path signals, level crossings, deadlock detection (M4); condition, patch/service/overhaul, depot levels as capability, contracts, incident variety and frequency option (M5); eras, save and load (snapshot plus seed), overlays, balancing, art, audio (M6).

**Cross-cutting**
- Replace greybox boxes with art at M5/M6; introduce a CI job running `dotnet test` and a headless benchmark so performance regressions are caught.
- Revisit tick rate and the multithreading question (partitioned and ordered, per ADR 0003) only after profiling a realistic M2/M3 world.
