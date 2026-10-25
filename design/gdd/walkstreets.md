# Walkstreets

> **Status**: Designed
> **Author**: user + Claude
> **Last Updated**: 2026-10-25
> **Last Verified**: 2026-10-25
> **Implements Pillar**: none recorded (see docs/game-plan.md `## Pillars`)

## Summary

Walkstreets is a player-built walkway network for residents on foot. A footpath or walkstreet joins homes, workplaces and stops without using the road lane graph, shortens the walk, and stretches the reach of a stop beyond today's street walk. It gives the player a cheap way to feed a stop that roads cannot reach.

> **Quick reference** — Layer: `Feature (inferred from the brief, no systems index at this tier; confirm when one exists)` · Priority: `Post-MVP (inferred)` · Category: `Gameplay (inferred)` · Key deps: `stops-and-depots, road-types, Stations coverage, trips and groups, staffing, build tool, save/load`

## Overview

Residents already walk: a trip group walks in a straight line within `walkRadius` (150 m) at `walkSpeed` 2.5 m/s with a `walkDetour` of 1.3, and a Station serves buildings within `coverageRadius` (150 m) in full and along the streets out to `reachRadius` (300 m) with a falling chance. This GDD adds a second graph beside the road graph: **walkway pieces** the player places on open ground. Two kinds: **footpath** (cheap, narrow, slower) and **walkstreet** (paved, wide, lit, faster, the town-centre kind). Walkways carry people only; no vehicle uses them and they hold no lanes, so traffic, routing and junction right of way are untouched. A walkway changes two things for the people on it: the walk follows the real path and a walkstreet is faster than the street walk, and the coverage of a connected stop reaches farther (`walkwayReach`). Where a walkway ends at a bus plaza (stops-and-depots) the plaza reaches 50 m farther than any other stop (`plazaReachBonus`). The player feels it as the planner's fix: a new housing block sits 400 m from the bus plaza and nobody rides, the player lays a walkstreet from the block to the plaza, and the ridership and the factory staffing rise. A walkway is never needed for the game to work; it is a lever, not a gate.

## Player Fantasy

The player is a town planner who knits a place together with paving. Footfall follows the walkstreet to the plaza, and a quiet housing block starts to feed the line. The feeling is quiet pride in a connected centre, not management of a pedestrian simulator.

## Detailed Design

### Core Rules

> Values are proposals for `data/tuning.json`. Solo mode: `game-designer`, `systems-designer`, `ux-designer` not consulted. Review manually before production.

1. **A separate graph.** `WalkNetwork` holds nodes and segments on the ground. It shares no lane, node or id space with `RoadNetwork`. Segments are straight or a single-arc curve, like roads, so the build tool can reuse its preview.
2. **One definition table.** Each walkway type is a row in `data/tuning.json`, looked up by id: half-width, walk speed factor, price per metre, upkeep per metre per day, `lit`, `upgradesTo`. No type-specific code outside the lookup.

| Type | Half-width | Speed factor | Price/m | Upkeep/m/day | Lit | Upgrades to |
|---|---|---|---|---|---|---|
| footpath | 1.0 m | 0.9 | 6 | 0.02 | no | walkstreet |
| walkstreet | 3.0 m | 1.15 | 28 | 0.10 | yes | none |

3. **Where it may go.** On open ground clear of buildings, roads, rails and water. It may run beside a road (clearance 1 m from the road's edge) but never on it. Cut or fill beyond `maxWalkGrade` (12 %) is refused with a reason; there are no walkway bridges or tunnels in this GDD (see Open Questions).
4. **Crossing a road.** A walkway that meets a TwoLane, Dirt or Avenue2x2 road makes a **crossing node**. A walker always waits a fixed `crossingDelay` (6 s, 12 s on Avenue2x2), whatever the road's speed limit or traffic; the delay never grows with a queue. Cars are not slowed; the crossing is a walker-side cost only. A walkway never crosses a Highway (`separated-only`) or a rail line: refused with the reason.
5. **Attachment.** A building is attached when its entrance is within `attachRadius` (25 m) of a walkway segment or node; the walker joins at the nearest point (ties go to the lower segment id). A Station, Stop or plaza is attached the same way, by its stop node. A Path (road-types) is not a walkway.
6. **Walking on the network.** The walkway is used only for the walking leg of a trip: from home to a stop, from a stop to the workplace or leisure place, or from home to a place all the way on foot. It lifts the 150 m `walkRadius` limit to `walkwayTripMax` (450 m of path) when both ends are attached to the same component. A group uses the walkway path when its time is less than the straight walk (which still needs the straight distance within `walkRadius`; beyond it, the walkway is the only way to walk). Time is path length ÷ (`walkSpeed` × the slowest segment's speed factor on the path), plus crossing delays. The path length is the real length along the segments, so no detour factor applies on a walkway. Ambient walkers take the cheapest path by this time; ties go to the lower segment id.
7. **Coverage by walkway.** For a Station connected to the walkway network, a building attached to the same component has coverage by the walk length *d* along the network (not the straight line): 1 for *d* ≤ `coverageRadius`, falling linearly to 0 at `walkwayReach` (450 m). The larger of this and today's street coverage applies. A building not on the network is unchanged.
8. **Plaza bonus.** (Plaza layout: `design/gdd/stops-and-depots.md`.) A walkway attached to a bus plaza adds `plazaReachBonus` (+50 m) to `walkwayReach` for that plaza only. The bonus is data on the plaza layout row, not a branch in code.
9. **Staffing.** Workplaces with jobs (Industries) count as attached places too. A group that reaches an industry on a walkway counts as arrived on foot, as in `Simulation.Workers`; nothing else about staffing changes.
10. **Upgrade and demolish.** footpath → walkstreet pays the walkstreet price minus an `upgradeRefundShare` (0.5) of the footpath price and keeps the path and attachments. A walkway never downgrades; demolish it at `walkDemolishShare` (0.3) of its price per metre. No refund on sale.
11. **Lamps.** A `lit` type adds lamp props at `lampSpacing` (25 m), visual only; lamps carry no sim state. The village look (town pavement and lamps, commit 6c4ace7) owns lamps inside a Town-class centre: a walkstreet there adds a lamp only where the centre has none within 10 m, so no pair of lamps stands closer than that.
12. **Air raid.** Cover (`Simulation.Cover`) still picks the nearest standing building by straight line; a walkway changes neither the shelter choice nor the siren rules.
13. **Everything is data.** Prices, speeds, radii and delays come from the table and the `walk` block in `data/tuning.json`.
14. **Cost of the search.** The sim keeps each component's walk lengths to every attached stop, and rebuilds them only when a segment, a stop or an attachment changes (never per tick, never per group). A group's walk choice reads the cached length; a rebuild is a single bounded search per changed component. Coverage reads the same cache.

### States and Transitions

| From | To | Allowed when | Cost |
|---|---|---|---|
| (none) | footpath | open ground, grade ≤ 12 % | 6 × length |
| (none) | walkstreet | open ground, grade ≤ 12 % | 28 × length |
| footpath | walkstreet | always | 28 − 0.5 × 6 per metre |
| any | (none) | player demolishes | 0.3 × price per metre |

A walkway has no per-piece lifecycle beyond built and demolished. A group's walk state is the existing one (walking along an event with a start, end and ticks); only the path it follows changes.

### Formulas

Variables: *L* = path length along the walkway (m, at most `walkwayTripMax` 450), *v* = `walkSpeed` (2.5), *f* = slowest speed factor on the path, *c* = sum of crossing delays (s).

- **Walkway walk time:** `t = L / (v × f) + c` seconds.
- **Straight walk time (existing):** `t0 = (D / v) × walkDetour`, with *D* the straight-line distance and `walkDetour` 1.3. The detour only turns a straight-line distance into a path length, so it applies here and not on a walkway, whose *L* is already the path.
- **Choice:** the walkway is used when `t < t0`. Example: 120 m of walkstreet (f 1.15), no crossing: `t = 120/2.875 = 41.7 s`; a straight walk of 100 m is `100/2.5 × 1.3 = 52 s`. The walkway is used.
- **Coverage by walk length *d*:** `cov(d) = 1` for `d ≤ 150`; `cov(d) = (R − d) / (R − 150)` for `150 < d < R`; `0` for `d ≥ R`, where `R = walkwayReach` (450, or 500 at a plaza). Example: *d* = 300, R = 450: `cov = 150/300 = 0.5`.
- **Cost of a segment:** `price = priceM × length + earthwork`, earthwork by the existing terrain rule.

### Interactions with Other Systems

- **Trips and groups** (`Simulation.Trips*.cs`, `TripEvent`): the walk leg may follow the walkway path; the event's start, end and ticks work as today.
- **Stations coverage** (`Simulation.Stations.cs`, `StationTuning.ReachRadius`): rule 7 adds a second source of coverage and takes the larger.
- **Staffing** (`Simulation.Workers.cs`): reached-on-foot groups count as workers; no change to `CloseDayForStaffing`.
- **Stops and depots:** a plaza is the main attachment point; `plazaReachBonus` is a column on its layout row.
- **Road types:** crossing delay depends on type; Highway is refused.
- **Economy:** price, upkeep and demolition. **Save/load:** the graph and its types are saved by id; save format number goes up. **Build tool and UI:** a walkway mode, type picker, grade and crossing preview, and a coverage overlay.
- **Town generator:** may place walkstreets at world creation later; out of scope here.

## Edge Cases

> Solo mode: `systems-designer` not consulted. Review manually before production.

- **If a walkway would run through a building, a road, a rail line or water**: refused with the reason and nothing is charged. It is never rerouted.
- **If a segment's grade is above 12 %**: refused with the reason. The player must add a switchback.
- **If a walkway meets a Highway or a rail line**: refused. No level crossing exists for walkers on those.
- **If a road is built across an existing walkway**: a crossing node is made at the meeting point and the delay applies. If the road is a Highway, the road build is refused with the reason.
- **If a road is demolished at a crossing node**: the node stays as a plain walkway node; the delay goes.
- **If a building is demolished or destroyed in a raid**: its attachment goes; the walkway stays. A rebuilt building re-attaches by rule 5.
- **If a walkway is cut in two**: each part is its own component; coverage and walk use only the part the station sits on. Groups mid-walk finish the walk on the path they have, then re-plan.
- **If a station is demolished**: the walkway stays and keeps its upkeep; coverage reverts to the street rule.
- **If a building is within `attachRadius` of two segments**: it attaches to the nearest; ties go to the lower segment id.
- **If walker paths tie in time**: the lower segment id wins, so replays do not diverge.
- **If a Town-class centre already has lamps along the line of a walkstreet**: the walkstreet adds none within 10 m of an existing lamp (rule 11).
- **If a walkway has no attached building at either end**: it is allowed, it costs upkeep, and it is shown dimmed in the overlay.
- **If the plaza is downgraded or demolished**: `plazaReachBonus` goes with it. The stops-and-depots rule forbids downgrade, so it can only be demolished.
- **If the player builds a walkway loop**: allowed; the graph is a graph, not a tree.
- **If two types' rows share an id, or `upgradesTo` names a missing type**: the game refuses to start and names the row.
- **If a save from before this change is loaded**: no walkway exists; trips and coverage are unchanged; the state hash after 1000 steps equals the pre-change golden hash.

## Dependencies

**Upstream (this GDD needs):**
- **stops-and-depots** (`design/gdd/stops-and-depots.md`): soft. A plaza is where a walkstreet meets a bus stop; `plazaReachBonus` is a column on the plaza row.
- **road-types** (`design/gdd/road-types.md`): hard. Crossing rules read the type (`separated-only` is refused).
- **Stations coverage** (`Simulation.Stations.cs`, `StationTuning`): hard. Rule 7 extends coverage.
- **Trips and groups** (`TripEvent`, `Tuning.cs` `trips`): hard. The walk path comes from here.
- **Terrain and earthworks:** hard. Grade limit and cut/fill.
- **Tuning** (`data/tuning.json`): hard. The table and `walk` block live there.
- **Village look** (commit 6c4ace7, town-redo slice 3): soft. Owns lamps inside Town-class centres (rule 11).

**Downstream (needs this GDD):**
- **Staffing** (`Simulation.Workers.cs`): soft. Walkers count as workers.
- **Economy and upkeep:** hard. Price, upkeep, demolition.
- **Save/load:** hard. Graph saved by type id; save format number goes up.
- **Build tool and UI** (`game/build_tool.gd`, `SimHost.cs`): hard. Mode, preview, overlay.
- **Junction geometry** (`design/gdd/junction-geometry.md`): none. Walkways make no junction and use no lane.

## Tuning Knobs

| Knob | Value | Safe range | Affects |
|---|---|---|---|
| `walkwayReach` | 450 m | 300–700 | how far a stop pulls through a walkway; below 300 a walkway adds nothing |
| `plazaReachBonus` | +50 m | 0–150 | how much a plaza rewards a connected walkway |
| `walkwayTripMax` | 450 m | 150–700 | longest walkway walk a group will take; at 150 the walkway lifts no limit |
| `attachRadius` | 25 m | 10–50 | how easily buildings join the network |
| `maxWalkGrade` | 12 % | 6–20 | how much earthwork walkways need |
| `crossingDelay` | 6 s / 12 s | 2–20 | the cost of crossing a road |
| footpath / walkstreet price | 6 / 28 per m | ×0.5–×2 | build cost vs benefit |
| footpath / walkstreet speed factor | 0.9 / 1.15 | 0.7–1.5 | how much faster than the street walk a group moves |
| `lampSpacing` | 25 m | 15–50 | look only |

Balance rationale: a 300 m walkstreet costs 8400, about a third of a plaza (25000). Target for `BalanceProbe` (untested): a walkstreet of that length that brings 10 homes from coverage 0 to coverage above 0.5 of one Station must lift that Station's daily riders by at least 10 people within 5 days. If it does not, raise `walkwayReach` or cut the price.

## Acceptance Criteria

1. **GIVEN** the walkway table has the two rows, **WHEN** the sim starts, **THEN** half-width, speed factor, detour and price per type equal the table, and no `if (type == …)` outside the lookup remains. (Unit test.)
2. **GIVEN** two rows with one id, or an `upgradesTo` naming no row, **WHEN** the table loads, **THEN** loading fails and the message names the row.
3. **GIVEN** open flat ground, **WHEN** the player builds 100 m of footpath, **THEN** the bill is 100 × 6 plus earthwork, and a building within 25 m is attached.
4. **GIVEN** a segment with grade over 12 %, **WHEN** it is built, **THEN** it is refused with the grade as the reason and nothing is charged.
4a. **GIVEN** a segment through a building, **WHEN** it is built, **THEN** it is refused with the building as the reason and nothing is charged.
4b. **GIVEN** a segment through a rail line or across a Highway, **WHEN** it is built, **THEN** it is refused with the line or road as the reason and nothing is charged.
4c. **GIVEN** a segment through water or on a road lane, **WHEN** it is built, **THEN** it is refused with the reason and nothing is charged.
5. **GIVEN** a walkway across a TwoLane road, **WHEN** a group walks across, **THEN** it spends 6 s at the crossing node (12 s on Avenue2x2) and no vehicle is slowed.
6. **GIVEN** 120 m of walkstreet between a home and a stop 100 m away in a straight line, no crossing, **WHEN** the group plans its walk, **THEN** the walk takes 41.7 s ± 0.5 s, less than the straight walk of 52 s, and the group uses it.
6a. **GIVEN** a home 300 m from a stop in a straight line and 320 m by walkway, **WHEN** the group plans its walk, **THEN** it walks the walkway (the straight walk is past `walkRadius`); with a walkway of 500 m it does not walk at all.
7. **GIVEN** a home 400 m by walkway from a connected Station, **WHEN** coverage is worked out, **THEN** it is `(450 − 400)/300 = 0.17`; with a bus plaza as the station it is `(500 − 400)/350 = 0.29`.
8. **GIVEN** a building not on the walkway, **WHEN** coverage is worked out, **THEN** it equals the pre-change street coverage.
9. **GIVEN** a footpath, **WHEN** it is upgraded, **THEN** the bill is 28 − 0.5 × 6 per metre and attachments and walkers stay.
10. **GIVEN** a station that loses its walkway by demolition, **WHEN** a day passes, **THEN** coverage reverts to the street rule and no walker is lost.
11. **GIVEN** a raid, **WHEN** the siren sounds, **THEN** walkers on a walkway pick cover by straight line exactly as today.
11a. **GIVEN** a walkway component with 3 stops and 50 groups, **WHEN** 100 ticks pass with no change to the graph, **THEN** the walk-length search ran 0 times in those ticks, and ran once after a segment was added.
12. **GIVEN** the same seed and commands, **WHEN** run twice, **THEN** walk paths, ties and coverage give identical state hashes. (Determinism test.)
13. **GIVEN** a save from before this change, **WHEN** it is loaded, **THEN** there is no walkway, and the state hash after 1000 steps equals the pre-change golden hash.

A retained screenshot of each walkway type, a crossing and the coverage overlay in the 3D view, in `production/qa/evidence/`, is required before the story closes (coding standards).

## Open Questions

- Whether walkstreets should raise town growth or building value (footfall). Out of scope here; decide before the town-redo slice 2.
- Walkway bridges and tunnels over rails and Highways (rule 3 refuses these crossings until then).
- Era gating: all types are offered from day one.
- A shared overlay for coverage with Station coverage.
