# M4 plan: rail, signals, level crossings

Status: built (2026-10-20). Done when (game-plan.md): a mixed rail and road network runs without deadlock. `MixedNetworkTests` runs towns with roads, buses and trains over eight days with a passing loop and level crossings and finds none. The architecture is ADR 0004; what exists and how it works is `project-status.md` section 5.19; the vocabulary is `CONTEXT.md`.

## What was decided

- **A separate rail network.** Single tracks (straight or arc) between nodes, running both ways, built with cut and fill, bridges and tunnels like roads; a junction is a node where tracks meet, a train may turn through it by up to 0.9 rad (about 51 degrees) and slows for the turn. Prices, limits (4 % grade, 90 m radius, 36 m/s) and the roster are in `data/tuning.json` under `rail`.
- **Reservation instead of a collision model.** A train moves only on track it holds. Boundaries (signals, stations, depots) split the network into blocks. A block signal needs the whole block beyond free, a path signal only the train's path up to the next boundary. Held track is released behind the tail. See ADR 0004.
- **Without signals the network is one block per stretch between stations and depots**: a second train waits until the first has left. Signals are what lets more trains run. This was chosen over "unsignalled track is free for all" so that the worst a careless player gets is a wait or a deadlock, never a pile of trains inside each other.
- **Deadlock detection and a warning**: trains that have waited for track more than `rail.movement.blockedSeconds` form a graph (each waits for the train that holds what it needs); a ring in it is a deadlock, reported once (`Simulation.RailWarnings`, a message in the game) and marked on the trains.
- **Re-routing around held track.** A train that waits at a signal for track held by another looks for a path to its station over track nobody holds; with path signals at a passing loop two trains pass.
- **Trains**: a locomotive and 1 to 8 wagons from the roster (`rail.locomotives`, `rail.wagons`). Length, mass, traction (force at standstill, power at speed), service and emergency braking, resistance (rolling, air, grade), curve and grade speed limits. All trains are push-pull, so a train turns round in a station.
- **Rail stations, depots and lines.** A rail station stands at a rail node, serves the same coverage radius as a road station, takes passengers and freight like one and pays by the straight-line distance. A line is all road or all rail. A depot on the first station of a line starts the train there. Trains are bought at the rail depot with the quickest way to the line's first station. Vehicles can also be bought for a depot with no line (the garage: 10 % upkeep while waiting), put on a line later or sent back; a vehicle leaving a busy depot waits inside until there is room and never comes out inside another (`project-status.md` 5.21, `m4-review.md`).
- **Level crossings** are found where a track and a road meet at the same height (within `rail.crossings.clearance`); a track on a bridge over a road, or the other way, makes none. They are priced, closed by approaching trains and observed by road vehicles. Rules for where one may be: at least `minFromNode` (24 m) from the ends of the road and of the track and from any junction, and at a wider angle than `minAngle`. A road, track or street growth that would break them is refused (towns do not grow over a railway).
- **Traffic lights** (the signals the road side was missing, listed under M4 in the status): a junction of three or more roads, two groups of approaches by axis taking turns.

## Slices (all built)

| Slice | Content | Tests |
|---|---|---|
| M4a | `RailNetwork`: tracks, nodes, signals, planning with terrain, junction turn rules | `RailNetworkTests` |
| M4b | Trains: consist, traction, speed profile, reservation, signals, deadlock detection, rerouting | `TrainTests`, `SignalTests`, `RailBehaviourTests` |
| M4c | Rail stations, depots, lines, loading and pay, passenger transit by rail | `TrainTests`, `RailEconomyTests` |
| M4d | Level crossings: detection, barriers, road vehicle gating, collisions | `CrossingTests` |
| M4e | Mixed network exit test, pinned rail hash, traffic lights | `MixedNetworkTests`, `RailDeterminismTests`, `TrafficLightTests` |
| M4f | Game layer: tracks, trains, signals, crossings, platforms, depots, lights drawn; rail and light tools; train purchase | not playable here (compiled, GDScript parsed, HUD run headless) |

## Left out on purpose

- Crashes between trains and derailments (reservation makes them impossible; incidents on the rail side are crossings only for now). Signal errors as a cause of incidents: M5.
- Priority roads, stop and yield signs at road junctions (built later, in the follow-up of 2026-10-21: `project-status.md` 5.22).
- Timetables (out of scope in the game plan), electrification, depots with servicing (M5), eras of locomotives (M6).
- Double-track as a single object: two parallel tracks with crossovers do it.
- Trains do not wear; no maintenance until M5.

Note, 2026-10-21: depots with servicing and trains' wear were built in M5 (`m5-plan.md`, status 5.24), signal errors as a cause of incidents too, and the eras of locomotives were dropped for model years (ADR 0006: the steam locomotive of 1938 and the diesel of 1942, C8).
