# Junction Geometry

> **Status**: Designed
> **Author**: user + Claude
> **Last Updated**: 2026-10-25
> **Last Verified**: 2026-10-25
> **Implements Pillar**: none recorded (see docs/game-plan.md `## Pillars`)

## Summary

Junction geometry sets how roads meet. It adds roundabouts built as real one-way rings, per-junction turn-lane pockets, cars that pick their lane early for the turn they will take, and left-turning cars that wait inside the junction for a gap. It lets the player fix bottlenecks by rebuilding a junction and watch queues clear.

> **Quick reference** — Layer: `Foundation (inferred from the brief, no systems index at this tier)` · Priority: `Post-MVP (inferred)` · Category: `Gameplay (inferred)` · Key deps: `road-types, RoadNetwork, conflict geometry, signals, routing, vehicle simulation, save/load, build tool`

## Overview

Junction geometry decides how roads meet: which lane connects to which, how many lanes an approach has, and who yields to whom. Today every junction is built automatically (each incoming lane joins the lane of every other road, same index where possible) and only signs and lights change who goes first. This GDD adds three things on top: **roundabouts** (a one-way ring that gives way on entry), **turn lanes** (a short extra lane before a junction so turning cars leave the through lane free), and **dynamic lane selection** (vehicles pick the lane that suits their next turn early, and change lane for it, instead of only reaching it by index). The player sees the change as flow: a jam at a busy crossing can be fixed by a roundabout or by turn pockets, and traffic visibly sorts itself into lanes before turns. The fantasy is the engineer's: spot a bottleneck on the map, rebuild the junction, and watch the queue dissolve. ADR 0002 (`docs/adr/0002-kinematic-lane-simulation-with-fidelity-tiers.md`) covers the lane simulation these rules extend; a junction ADR will probably be needed for the connection rules.

## Detailed Design

### Core Rules

> Values are proposals for `data/tuning.json`. Solo mode: `game-designer`, `systems-designer`, `ai-programmer`, `art-director` not consulted. Review before production.

1. **Roundabout.** A tool converts a junction node with 3 or more roads into a ring of one-way arc roads, radius 12 to 30 m (default 18), one lane, speed limit 8 m/s, driven counter-clockwise (lane 0 is right-most, so traffic keeps right). Each arm gets an entry and an exit node on the ring. Entries give way to ring traffic (the existing yield rule, applied to the entry approach). Price: ring length × the arm type's price per metre, plus a fee of 6000. Refused when an arm would be under 20 m after the ring, when the node is on a bridge or tunnel, or when a building is in the way. A traffic light or signs at the node are removed, and the bill shows it. A "remove roundabout" command restores a plain junction.
2. **Turn lanes.** The player turns turn lanes on or off per junction node. When on, each approach on TwoLane or Avenue2x2 whose road is at least 60 m long gets a pocket for each left or right turn (heading change over 25°): one extra lane, 30 m long, 3.0 m wide, on the turning side. The pocket connects only to that turn; the through lane no longer connects to it. Price: 1500 per pocket. Not offered on dirt, Path or highway. Turning it off retires the pockets and refunds nothing.
3. **Dynamic lane selection.** A vehicle fixes its next turn when it enters a lane: a company vehicle takes it from its route, ambient traffic from a seeded draw (replacing the random pick at the junction, same distribution). Within a 120 m lookahead it changes toward a lane that connects to that turn, where lanes are linked `Left`/`Right`. It changes only into a gap of at least `minGap` + its length behind and ahead. If it is blocked it slows and keeps trying until 20 m from the junction edge, then takes the best remaining connection (a turn it didn't plan, re-routed) rather than stop.
4. **Left-turn waiting in the box.** A vehicle turning left may enter the junction and wait at a holding point on its path (where the path first meets oncoming traffic's conflict stretch) when: the only conflicts left are with oncoming moves, its exit lane has room, and its holding point is outside every other approach's conflict stretch. At most one vehicle waits per left-turn move. It leaves when the gap is long enough by the existing arrival-time rule, or at once when a light clears the oncoming approach. If any condition fails, it waits at the edge as today.
5. **Connection rules.** Without these features, connections remain today's: incoming lane to every other road's lane, same index where possible. With turn lanes on, connections follow rule 2.

### States and Transitions

| Subject | State | To | When |
|---|---|---|---|
| Junction node | Plain | Roundabout | Tool, rule 1 refusals pass |
| Junction node | Roundabout | Plain | Remove command |
| Junction node | Plain | Plain + turn lanes | Toggle on, rule 2 |
| Junction node | Plain + turn lanes | Plain | Toggle off |
| Junction node | Roundabout | turn lanes | never |
| Vehicle | Approaching | WaitingAtEdge | existing right of way |
| Vehicle | Approaching | WaitingInBox | rule 4 conditions hold |
| Vehicle | WaitingInBox | Crossing | gap, or clear phase |
| Vehicle | Crossing | Exited | tail left the junction |
| Vehicle | Approaching | Changing lane | rule 3 gap exists |

### Interactions with Other Systems

- **road-types (GDD):** needs one-way roads (shared with ramps), lane counts per road, the `at-grade` junction flag. Ramps-only types never get roundabouts or turn lanes.
- **RoadNetwork:** junction connection rebuild reads the junction mode; ring roads are real roads, so `BuildRoad`, split and remove apply.
- **Signals and signs:** a roundabout removes them; turn-lane junctions keep them. A light's clear phase releases a box-waiting vehicle.
- **Conflicts (`MovesConflict`, `GetConflictZone`):** holding points come from the conflict zones; pocket and ring paths are ordinary paths.
- **Vehicle simulation and routing:** next turn is drawn on lane entry, so the lane-change and `FreeSpeed` rules read it; routing cost counts the lane-change cost implicitly through time.
- **Collisions:** `DetectJunctionCollisions` counts a waiting vehicle inside the box; a holding point is never inside another path.
- **Save/load and UI:** junction mode is saved per node; the tool shows ring radius, pockets and the price.

## Edge Cases

> Solo mode: `systems-designer` not consulted. Review manually before production.

- **If a roundabout is built where an arm is under 20 m after the ring, the node is on a structure, or a building is in the way**: refused with the reason; nothing is charged.
- **If a roundabout is built at a node with a traffic light or signs**: the light and signs are removed, and the bill lists the removal. Remove-roundabout does not bring them back.
- **If a road is joined to a roundabout ring later**: the tool adds an entry node on the ring at least 10 m from the next one; closer is refused.
- **If a roundabout arm is removed so fewer than 3 arms remain**: the ring stays but its entries to the missing arm close. If 1 arm remains, the node becomes a plain dead end.
- **If a vehicle is on a ring road when the roundabout is removed**: it moves to the nearest arm lane, and the pieces migrate as in `SplitRoad`. If no arm lane is free within 10 m, the vehicle is removed with no incident.
- **If an approach has less than 60 m of road**: no pocket on that approach; the others still get theirs.
- **If a pocket's turn is made impossible later** (a road removed): the pocket is retired and its refund is zero.
- **If a vehicle misses its planned lane** (blocked until 20 m from the edge): it takes the best remaining connection and re-routes. It never stops in the lane waiting for a gap that doesn't come.
- **If two vehicles want the same gap to change lane**: the one further ahead goes; a tie goes to the lower id.
- **If a vehicle waits in the box and the light turns red or its exit lane fills**: it clears the box when possible, else stays; `exitQueueSpeed` (3 m/s) applies as before. A box-waiter blocked longer than `blockedSeconds` is not an incident, but is shown in the junction's stats.
- **If a wreck is in the box**: no new vehicle enters the box for that move; the existing wreck rules apply.
- **If a turn-lane junction is switched on at a node with a station stop line** (`stopMargin` 3 m): the pocket must stay 3 m clear of the stop; otherwise that approach gets no pocket.
- **If a loaded save has a junction mode for a node that no longer exists**: the load drops that mode and logs it. It never fails the load.
- **If ambient traffic draws its next turn at lane entry and the lane has only one connection**: the draw is skipped, and no random number is used for it. This keeps seeded runs stable between lanes of different widths.

## Dependencies

**Upstream:**
- **road-types** (`design/gdd/road-types.md`): hard. One-way roads, per-road lane counts, the `at-grade` flag. *Provisional:* road-types defines one-way ramps only; ring pieces need the same one-way road support.
- **RoadNetwork** (`RoadNetwork.cs`): hard. Ring pieces are real roads; connection rebuild reads the junction mode.
- **Conflict geometry** (`MovesConflict`, `GetConflictZone`): hard. Holding points and pocket paths come from it.
- **Signals and signs** (`Simulation.Signals.cs`): hard. Removed at roundabouts; clear phase releases box-waiters.
- **Routing** (`Simulation.Routing.cs`): hard. The turn chosen on lane entry reads the route field.

**Downstream:**
- **Vehicle simulation** (`Simulation.Maneuvers.cs`, junction interaction): hard. New WaitingInBox state and lane change toward the planned turn.
- **Collision detection:** hard. Counts box-waiters.
- **Save/load:** hard. Junction mode per node; save format number goes up.
- **Build tool and UI** (`game/build_tool.gd`, `SimHost.cs`): hard. Roundabout tool, turn-lane toggle, preview and prices.
- **Stops and depots** (`design/gdd/stops-and-depots.md`): hard. A bay and a turn pocket share the right-most lane (the pocket wins); a plaza's turn-around is a one-way ring piece.

## Acceptance Criteria

No Formulas section at `minimal`; criteria come from Core Rules. Solo mode: `qa-lead` not consulted.

1. **GIVEN** a 4-arm junction with every arm at least 60 m, **WHEN** the roundabout tool runs at radius 18 m, **THEN** a one-way ring with 4 entries and 4 exits exists, traffic circulates counter-clockwise, and the bill equals ring length × arm price + 6000.
2. **GIVEN** an arm that would be 15 m long after the ring, a node on a bridge, or a building in the way, **WHEN** the tool runs, **THEN** it is refused with the reason and money is unchanged.
3. **GIVEN** a roundabout with 400 vehicles on a seeded grid, **WHEN** it runs 25 simulated minutes, **THEN** no `IncidentKind.Junction` occurs and no vehicle stands in the ring longer than 60 s.
4. **GIVEN** a roundabout that replaced a traffic light, **WHEN** it is removed, **THEN** the node is a plain junction, vehicles on the ring are moved or removed as in the edge cases, and no light returns.
5. **GIVEN** a junction with turn lanes toggled on and arms of at least 60 m, **WHEN** the toggle runs, **THEN** each left or right turn has a 30 m pocket connected only to that turn, the through lane has no connection to it, and the bill is 1500 per pocket.
6. **GIVEN** an approach under 60 m, **WHEN** turn lanes are toggled on, **THEN** that approach gets no pocket and the others do.
7. **GIVEN** a left-turning car with a free exit and oncoming traffic, **WHEN** it reaches the edge, **THEN** it enters and waits at the holding point, leaves in a gap, and no through vehicle is blocked by it.
8. **GIVEN** a light that clears the oncoming approach, **WHEN** the phase changes, **THEN** the box-waiting vehicle crosses at once.
9. **GIVEN** a car whose next turn is left and a road with 2 linked lanes, **WHEN** it is 120 m out, **THEN** it changes into the left lane before 20 m from the edge, and the lane change never overlaps another vehicle.
10. **GIVEN** a blocked lane change, **WHEN** the car is 20 m from the edge, **THEN** it takes another connection and re-routes without stopping.
11. **GIVEN** a seeded run, **WHEN** it runs twice, **THEN** the state hashes match. A run with no roundabouts or pockets differs from the golden hash only through the early turn draw, and the golden hashes are re-pinned once with that stated.
12. **GIVEN** a save with roundabout and turn-lane junctions, **WHEN** saved and loaded, **THEN** modes and pockets are the same, and a mode for a removed node is dropped with a log line.

A retained screenshot of a roundabout, a pocket junction, and a left-turn car in the box, in `production/qa/evidence/`, is required before the story closes.
