# M4 review: what was built, decisions, weak points, what is left

Status on 2026-10-21 (open points applied the same day: sections 3.1, 3.2, 3.9 and 4 say what was done and what remains). Covers all of M4 (rail, signals, level crossings, traffic lights), the terrain and depot follow-ups from the first play-test, and the garage (5.21). Committed in 551eedc (M4) and 5de050a (follow-ups, signs, benchmark job). Part descriptions are in `project-status.md` sections 5.19 to 5.21, decisions in `m4-plan.md` and ADR 0004, vocabulary in `CONTEXT.md`. This file is the single place for what is still open.

## 1. What was built

| Area | What exists | Where |
|---|---|---|
| Rail network | Single two-way tracks (straight or arc) between nodes; cut and fill; bridges over water; tunnels through hills; 4 % grade limit; 90 m minimum radius; junction turns up to 0.9 rad | `RailNetwork.cs`, `RailTuning.cs` |
| Ground under track | Building levels the ground (`CarvePath`). The game redraws changed terrain chunks every frame (`DrawRail` calls `RebuildTerrain`). | `SimHostRail.cs` |
| Hover checks | `WhyNotTrack` names the refusing rule with its numbers (too short, too steep, too tight, in water, junction too close, crossing too near a road end) | `RailNetwork.cs`, `Simulation.Rail.cs` |
| Trains | Locomotive plus 1 to 8 wagons; traction, rolling, air and grade resistance; service and emergency brakes; limits from curve and grade; push-pull reversal in stations | `Simulation.Trains.cs`, `Simulation.Rail.cs` |
| Signals and reservation | Block and path signals. Trains hold tracks and nodes and ask for the next stretch before reaching it. After 1.5 s waiting, re-route around track others hold. | `Simulation.Trains.cs` (`ExtendAuthority`, `TryReserveNext`, `TryReroute`) |
| Deadlocks | Wait-for graph every 40 ticks; a ring is recorded once and shown as a message; trains painted red | `FindDeadlocks` |
| Stations, depots, lines | Rail stations at nodes; rail depots; lines are all road or all rail; passengers and freight paid by distance | `Simulation.Rail.cs`, `Station.cs` |
| Level crossings | Found where a track and a road meet at the same height. Barriers close for approaching trains. Road vehicles stop at a line. A train that cannot stop hits a car in the band. | `Simulation.Crossings.cs` |
| Traffic lights | Two axis groups at junctions of three or more roads, timed by tick, no state | `Simulation.Lights.cs` |
| Garage (5.21) | Vehicles and trains bought for a depot with no line, put on a line later, sent back to a depot, or sold | `Simulation.Garage.cs` |
| Ghosts (5.21) | A vehicle leaving a busy depot drives out without being held, then becomes ordinary. Trains bought together wait inside the depot. | `Simulation.Fleet.cs` (`StepGhosts`), `Simulation.Trains.cs` |
| Game layer | Track, curved track, block and path signal, remove signal, remove track, rail station, rail depot tools; traffic light tools; drawing of all of it; Vehicles window with the garage; Lines window buy buttons | `SimHostRail.cs`, `SimHostLights.cs`, `SimHostGarage.cs`, `build_tool.gd`, `hud.gd`, `main.gd` |

Tests: 426 xUnit tests passed at the time on macOS and in the Linux container (Release, x64); the suite has 692 on 2026-10-21. The rail golden hash `0x24A1F669E8A6208C` was unchanged then (the current value is `0x179999302AAEC508`, see 5.37 of the status) on both systems, so none of the garage or ghost changes moved an existing run.

## 2. Decisions taken

These come from the review and the play-test. They are settled unless the user changes them.

1. **Ghosts only matter at a depot exit.** Cars do not need to stop for a ghost. The requirement is that a ghost enters the road correctly: never placed inside another vehicle, and never driving into one. Done (4.1).
2. **Parked vehicles pay 10 % of their upkeep.** A vehicle or train waiting in the garage still counts as kept in working order, so it pays upkeep, but at a tenth of its daily rate (road model upkeep, or locomotive plus wagons for a train). Done: `economy.parkedUpkeepShare` (0.1), charged by `ChargeGarageUpkeep`. See 4.2.
3. **Minimum block between level crossings stays at 48 m** (24 m from each junction or road end). Accepted; the player will meet it.
4. **One commit for all of M4**, when the user asks for it. Done: 551eedc.

## 3. Weak points, by area

### 3.1 Ghosts and the depot exit

- **Fixed: a ghost never goes inside another vehicle.** It waits inside the depot (not on the road, not drawn) until nobody is within the spawn clearance of its place, nothing is about to arrive at the node and no earlier ghost is ahead of it; once out it stops short of any vehicle ahead on its lane; it becomes ordinary only when its whole length is out and the lane has room. Tested by `ANewVehicle_IsNeverPutInsideAnother_WhateverTheTrafficAtTheDepot` (six vehicles, two bought later, 150 s, every pair checked each tick) and `AGhost_WaitsInsideTheDepot_...`.
- **Accepted: cars do not stop for a ghost.** A ghost is left out of the lane ordering, so a car that reaches the doorway while a ghost is out can drive through it without an incident. The ghost phase is a few seconds at a depot exit; the decision is that this needs no rule.
- **Accepted: level crossings ignore ghosts.** Both `DetectCrossingCollisions` and `ZoneObstructed` read the lane ordering, which a ghost is not in. A ghost can only be near a crossing when a depot is within about 35 m of one; not worth a rule.
- **Open: ghost speed and acceleration are guesses** (6 m/s, 2.5 m/s²), and how ghosts look in 3D is unverified.

### 3.2 Garage and parked vehicles

- **Fixed: parked vehicles pay 10 % of their upkeep** (`economy.parkedUpkeepShare`, vehicles and trains), charged to the company and on no line's books, so the company's upkeep total moves in a way the Lines window does not show. Tested for a bus and a train, and a sold parked vehicle stops costing.
- **Fixed and tested: a returning vehicle that crashes** becomes a wreck, is cleared like any wreck and is never parked (`ABusThatCrashesOnItsWayToTheDepot_...`).
- **Fixed and tested: returning trains contend for the depot's node** without trouble: two trains sent back both reach the depot, one after the other, free the node and leave no deadlock (`TwoTrainsSentBack_...`).
- **Open: returning vehicles in a crowded junction** use the trip-car routing and follow the normal junction rules; nothing specific has been tested there.
- **Open: no hard cap on the garage.** The upkeep is the only brake on buying many and leaving them.

### 3.3 Trains and the reservation model

- **No train-to-train collision and no derailment.** Reservation makes overlap impossible, so this is by design. Any bug in reservation would show as a silent overlap, not an incident; the tests check overlap, not the reason.
- **Deadlocks are reported, not resolved.** Two trains turning round at the ends of one single track meet head on and stop for good. The game says so and paints them red. The player must sell one or add a passing loop.
- **Re-routing is local.** A waiting train only changes the part of its route it has not been given. It looks at what other trains hold, not at where they are heading. Two trains can still choose routes that meet, and the detector then has to catch it.
- **All trains are push-pull.** A train reverses in a station whenever that is quicker. There is no run-round loop and no timetable. A train longer than the track behind its station cannot reverse until it is fully on its route; it carries on instead.
- **Stations only at nodes.** A rail station splits the track, so it needs a node. A station that ends a line needs a dead-end node. Platform length is not modelled: a train longer than its platform hangs back along the track.
- **Signals face one way.** A signal faces the direction of the track it was built on. It can only be turned by taking it down. The R key flips it before placing.
- **No one-way signal.** Only block and path signals exist.
- **Speed through junctions.** Turns are limited by `turnoutSpeed` and `minTurnoutSpeed`; the values are first guesses.
- **Trains have no wear, breakdown or servicing.** That is M5.

### 3.4 Level crossings

- **Refused near junctions and road ends (24 m).** A railway cannot cross a town block shorter than 48 m between junctions.
- **Moving a road junction is refused** when the rebuilt roads would cross a track. It is refused rather than priced.
- **Towns do not grow within 14 m of track.** A railway through a town stops it on that side.
- **Road vehicle stop line and queue** are tuned by guess (`roadStopMargin`, `QueuedBeyond`). Not played.
- **Barrier timing** (`warnSeconds`, `lowerSeconds`, `clearSeconds`) is a first guess.
- **Emergency braking for closed barriers** only bites when the train would otherwise arrive before the barrier. Checked by test, not by play.

### 3.5 Traffic lights

- **Fixed timing.** Green is 18 s and clear is 4 s, the same for every junction. A long queue on one axis gets no extra green.
- **No state.** Timing is a function of tick and node id, so lights cannot be out of step with a replay, but they cannot respond to traffic either.
- **No stop or yield signs and no priority roads.** Junctions use lights or nothing. Dropped from the M4 plan.

### 3.6 Rail economy

- **Balance is a first pass.** A four-coach line between two towns made about 4000 a day against 1800 upkeep in the test. A two-wagon freight train made 1.2 to 1.5 times its upkeep. Track price (160 per m), locomotive and wagon roster, upkeep and pay rates are first numbers.
- **No long play.** Nothing has been run for hours of game time on a real town map.
- **Rail stations share road pay rules.** Passengers and freight use the same pay code, so a rail line can outearn a road line on distance alone. That may be too strong or too weak; it needs playing.

### 3.7 Game layer and 3D

- **Nothing in M4 has been played in 3D.** The C# compiles, the GDScript parses and the HUD runs headless with made-up data. The Vehicles and Lines windows, the build tools and the drawing are all unverified.
- **Drawing** of barrier arms, signal posts, platforms, depots, trains and ghosts has not been checked by eye.
- **The `--bench` harness covers road vehicles only.** Frame rate with trains and crossings is unmeasured.
- **Garage UI.** The choice of line is rebuilt when the list of lines changes; an open choice list is left alone. Not played.
- **The HUD smoke test** (scratchpad `hud_garage_test.gd`) and the earlier `hud_rail_test.gd` were run headless only. They are not part of the repo.

### 3.8 Engineering and tests

- **The hash is pinned on two systems.** The garage and ghost state are mixed in only when they exist, so the existing golden values hold. A test that rebuilds the rail scenario with a garage vehicle has not been pinned.
- **Tests check behaviour, not the causes.** E.g. no overlap, but not why overlap is absent.
- **Some numbers are asserted loosely** (e.g. "income greater than zero" after 600 s) to avoid tuning-sensitive tests. A balance change can pass them while the line is unprofitable.
- **`SimHostRail.cs` and `SimHostGarage.cs` are partials of `SimHost`**, which keeps growing. Splitting further is debt.
- **`RailNetwork` and `RoadNetwork` duplicate planning code** (`PlanJunctions`, `PiecesAllowed`, `InsertStructures`). Shared extraction is possible; deferred to keep M4 safe.

### 3.9 Documentation

- Done: `project-status.md` 5.21, `CONTEXT.md`, `README.md` and `m4-plan.md` now describe the garage, the 10 % upkeep and the ghost rule, and the test counts are 426.

## 4. What needs to be done

### 4.1 Ghost placement (decision 1): done

`StepGhosts` rewritten (`Simulation.Fleet.cs`): inside the depot until `IsClearFor` the doorway, no `TrafficNear` the node and no earlier ghost ahead; then out, stopping short of any vehicle ahead; ordinary when out and clear. `IsInDepot` hides a waiting ghost from the drawing (`SimHost.DrawVehicles`) and the Vehicles window says "Waiting in the depot". Two tests added (see 3.1); the old "ghost drives through the bus in the doorway" test was replaced.

### 4.2 Parked upkeep (decision 2): done

`parkedUpkeepShare` 0.1 in `data/tuning.json` and `EconomyTuning`; `ChargeGarageUpkeep` in `Simulation.Garage.cs`, called from `CloseDay`. Tests: `AParkedBus_PaysAShareOfItsUpkeep_OnNoLinesBooks`, `AParkedTrain_PaysAShareOfItsConsistsUpkeep_AndASoldOneStopsPaying`. Worlds with no garage are unchanged, so no golden hash moved.

### 4.3 Other fixes before committing

1. Done: whole suite (426) on macOS and in the Linux container; the rail hash is unchanged.
2. Done (decided, not coded): the crossing check ignores ghosts (3.1).
3. Done: test for a returning vehicle that crashes (3.2).
4. Done: test for returning trains contending for the depot (3.2).
5. Done: the docs (3.9).

### 4.4 Commit (when the user asks)

One commit for all of M4 (decision 4), with a `Co-Authored-By` line. Push only when asked. The working tree held the rail, traffic light, garage and game-layer files, the tests and the docs. Committed in 551eedc and 5de050a.

## What is still open

The 3D play-test (section 5), the rail balance and the items in section 6.

## 5. Play-test checklist (the user, in 3D)

- Track is cut into the hill and follows the ground after building.
- A steep or short track shows the refusing reason and numbers when hovered.
- Buying a bus for a busy depot: it comes out one after another, never in the air or inside another vehicle.
- A bus waiting in a busy depot is not drawn, then comes out behind the one before and never sits inside another.
- Trains bought together leave the depot one at a time; a waiting train is not drawn in the depot.
- Level crossing: barriers come down before a train; road traffic stops at the line; a collision wrecks the car and blocks the band.
- Signals: a train waits at a red signal; a passing loop with path signals lets two trains pass; two trains on one single track deadlock and the game says so.
- Traffic lights: one axis at a time; cars stop at the edge on red.
- Vehicles window: the garage section, Put on line, Sell, To depot. A bus sent to a depot drives there and disappears into the garage.
- Upkeep: a parked vehicle costs a tenth of a running one (company upkeep goes up by that, no line's books change).
- Frame rate with a few trains and a crossing on a town map.

## 6. After M4 is accepted (not blocking)

- Rail balance pass: track price, roster, upkeep, pay rates, a month of real income on a town map.
- Re-routing that considers where other trains are heading.
- Turn a signal round after placing it.
- Adaptive traffic lights.
- Stop and yield signs, priority roads.
- Platform length, and trains longer than a station.
- Split the shared planning code of the road and rail networks.
- Split `SimHost` further.

Note, 2026-10-21: this review is history. Since it was written the signs (5.22), maintenance, trains' wear and signal failures (M5, 5.24) and the whole campaign (C1 to C9) were built, and the developer played the 3D side, which works; the current state and the open points are in `project-status.md` and `campaign-plan.md`.
