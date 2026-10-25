# Road Types

> **Status**: Designed
> **Author**: user + Claude
> **Last Updated**: 2026-10-25
> **Last Verified**: 2026-10-25
> **Implements Pillar**: none recorded (see docs/game-plan.md `## Pillars`)

## Summary

Road types is the data-driven catalogue of roads the player builds. It adds a cheap dirt road, a fast ramps-only highway, and per-road lane counts, on top of the existing two-lane and avenue roads. It gives the player a real choice between cost, speed and capacity, and a ladder to upgrade along as towns grow.

> **Quick reference** — Layer: `Foundation (inferred from the brief, no systems index at this tier)` · Priority: `Post-MVP (inferred)` · Category: `Gameplay (inferred)` · Key deps: `RoadNetwork, Tuning, junctions, traffic and routing, economy, rail crossings, save/load, build tool`

## Overview

Road types are the catalogue of roads the player can build, each defined by lane layout, speed limit, price per metre, and what it may connect to. Today there are two buildable types (`TwoLane`, `Avenue2x2`) plus the non-buildable `Path`. This GDD adds **dirt road** (cheap, slow, low capacity, the first road a young network can afford), **highway** (fast, high capacity, entered and left only by on-ramps, no level crossings or at-grade junctions), and **asymmetric roads** (a different lane count per direction). Each type feeds the same lane graph, so traffic, routing, junction right of way, upkeep and the build preview treat them alike; the types differ only through their data. The player feels road choice as a real trade-off: a dirt track connects a village for little money and slows every truck on it, while a highway moves the region's war and civil industry fast but costs a lot to build and can only be joined at ramps. Upgrading a road along that ladder should feel like the network growing up with the towns it serves. Implementation details of the lane simulation are in ADR 0002 (`docs/adr/0002-kinematic-lane-simulation-with-fidelity-tiers.md`); highway ramps and asymmetric lanes will probably need their own ADR.

## Detailed Design

### Core Rules

> Values below are proposals; they live in `data/tuning.json`, not in code. Solo mode: `game-designer`, `systems-designer`, `ai-programmer`, `art-director` not consulted. Review manually before production.

1. **One definition table.** Each road type is a row in `data/tuning.json`: half-width, lanes per direction, speed limit, price per metre, `overtaking`, `junctions` (`at-grade` | `ramps-only`), `crossings` (`level` | `separated-only`), `upgradesTo`, `buildable`. The `RoadType` switches (`HalfWidth`, `Tuning.Of`, lane count) read this table. No type-specific code remains.
2. **Types.**

| Type | Lanes each way | Half-width | Speed | Price/m | Overtaking | Junctions | Crossings | Buildable | Upgrades to |
|---|---|---|---|---|---|---|---|---|---|
| Path | 1 | 2.5 m | 8 | 15 | no | at-grade | level | no | none |
| Dirt | 1 | 3.0 m | 9 | 12 | no | at-grade | level | yes | TwoLane |
| TwoLane | 1 | 3.5 m | 14 | 40 | yes | at-grade | level | yes | Avenue2x2 |
| Avenue2x2 | 2 | 8.0 m | 19 | 90 | no (existing rule) | at-grade | level | yes | none |
| Highway | 2 | 10.0 m | 28 | 180 | no | ramps-only | separated-only | yes | none |

3. **Dirt.** Slows every vehicle to its limit (a heavy truck on dirt takes about 1.5 times as long as on TwoLane). Upgrading to TwoLane keeps the route and charges the TwoLane price minus an `upgradeRefundShare` of the dirt price, as the existing TwoLane→Avenue upgrade does.
4. **Highway.** Is built new; no road upgrades into one. A road ending on a highway side does not make a junction: the build tool inserts an on-ramp and an off-ramp, each a one-way, one-lane link (speed 12) with a merge or diverge lane. Ramps are at least 300 m apart; a closer one is refused with a reason. A highway that must cross a road or rail line passes over or under it, and the existing `crossingClearance` of 5.5 m applies.
5. **Asymmetric lanes.** A road stores `lanesForward` and `lanesBack` (1 to 3 each, total at most 4). It is allowed on TwoLane and Avenue2x2 only. Price per metre grows by 35 % of the TwoLane price for each lane above the type's normal count. Traffic lights and signs treat each lane as before.
6. **Access.** A type with `buildable: no` is never offered in the tool and is not picked by upgrade. Path keeps its access-point rule.
7. **Era gating.** Out of scope here. All buildable types are offered from day one; unlock by date or budget is in Open Questions.

### States and Transitions

| From | To | Allowed when | Cost |
|---|---|---|---|
| Dirt | TwoLane | always | TwoLane price minus refund share |
| TwoLane | Avenue2x2 | existing `CanUpgrade` | existing |
| Avenue2x2 | TwoLane | existing `CanDowngrade` | `downgradeCostPerMetre` |
| TwoLane | Dirt | never (no downgrade path) | none |
| any | Highway | never | none (build new) |
| Highway | any | never (demolish) | `demolitionCostPerMetre` |
| lane counts | other counts | type allows asymmetric | per-lane price difference |

### Interactions with Other Systems

- **RoadNetwork:** reads the table; `Reshape`, `CanUpgrade`, `CanDowngrade` follow `upgradesTo`.
- **Junctions and signs:** a `ramps-only` type refuses junction nodes; lights and signs exist only on `at-grade` types.
- **Traffic (lane sim):** speed, lane count and the `overtaking` flag come from the type; a ramp merge is a normal lane merge with `MovesConflict`.
- **Routing:** edge cost uses the type's speed limit, so vehicles prefer a highway when it saves time.
- **Economy:** price per metre, upkeep, demolition and structure factor (a highway on a bridge uses a factor like `avenueStructureFactor`).
- **Condition and resurfacing:** wear uses the same `passesPerPoint` rule; dirt wears faster (proposed `passesPerPoint` 2000).
- **Save/load:** road type becomes an id into the table, so the save format number goes up.
- **Build tool and UI:** list buildable types, show lanes and speed, and preview ramps.

## Edge Cases

> Solo mode: `systems-designer` not consulted. Review manually before production.

- **If a road built or upgraded to dirt, TwoLane or avenue meets a highway side**: the tool inserts the ramp pair. If a ramp cannot be placed (terrain, a building, a bridge or tunnel span, or under 300 m from another ramp), the whole build command is refused and listed as not buildable with the reason, as the Path rule does.
- **If the player builds a ramp on a bridge or tunnel section of a highway**: refused. Ramps start and end on ground, at least `structureMargin` (8 m) from a structure.
- **If a highway ends at a free node**: allowed while building, but routing never uses it as a destination or turn-around. Vehicles do not enter it.
- **If a rail line is built across a highway**: it must pass over or under (`crossingClearance` 5.5 m). If that cannot be done, the rail build is refused. A level crossing is never made on a `separated-only` type.
- **If a dirt road is upgraded to TwoLane while vehicles are on it**: vehicles keep their lane and position, as the existing upgrade does. Access Paths that joined it stay joined.
- **If a speed limit is set above the type's maximum**: refused. A limit below it is allowed, and is never raised by an upgrade.
- **If a road with asymmetric lanes is upgraded or downgraded**: lane counts reset to the new type's normal count. The refund is the price difference.
- **If lanes forward or back are more than 1 on a TwoLane**: overtaking through the oncoming side is off for that road. It is on only when both directions have 1 lane.
- **If two roads of different lane counts meet at a node**: the extra lane ends in a lane merge using the existing lane-end rules. It is never a dead end.
- **If a highway with ramps is demolished**: its ramps go with it, and the demolition bill includes them.
- **If a saved game from before this change is loaded**: road ids map to rows `twoLane`, `avenue`, `path` by name, not by enum order. Unknown ids refuse the load with a message.
- **If the table has two rows with the same id, or `upgradesTo` names a missing type**: the game refuses to start and names the row. It never falls back to TwoLane.
- **If the table's order changes**: results stay the same. Types are looked up by id, never by position, so golden hashes only move when values change.

## Dependencies

No other GDD exists yet, so entries point to code and docs.

**Upstream (road types needs):**
- **RoadNetwork** (`RoadNetwork.cs`): hard. The table replaces its type switches.
- **Tuning** (`Tuning.cs`, `data/tuning.json`): hard. The table lives here.
- **Terrain and earthworks**: hard. Cut, fill, bridges and tunnels use type width and price.
- **Junctions and right of way** (`Simulation.Junctions`): hard. `ramps-only` types must refuse junction nodes.

**Downstream (needs road types):**
- **Traffic and lane simulation:** hard. Lanes, speed and the overtaking flag.
- **Routing:** hard. Edge cost uses the type's speed.
- **Economy and upkeep:** hard. Price, demolition, structure factor.
- **Condition and resurfacing:** soft. Wear rate per type.
- **Rail crossings** (`Simulation.Crossings`): hard. `separated-only` blocks level crossings.
- **Village and town generators:** soft. They keep using TwoLane and Path by id.
- **Access points and Paths:** hard. Path stays non-buildable.
- **Save/load:** hard. Type becomes an id; save format number goes up.
- **Build tool and UI** (`game/build_tool.gd`, `SimHost.cs`): hard. Lists, previews and ramp preview.

- **Stops and depots** (`design/gdd/stops-and-depots.md`): hard. Stop layouts are offered by road type (never on `ramps-only`); the plaza turn-around needs the one-way road support defined here.
- **Walkstreets** (`design/gdd/walkstreets.md`): hard. Walkway crossings read the type: a `separated-only` type (Highway) is refused, and the crossing delay is 6 s on TwoLane and Dirt, 12 s on Avenue2x2.
- **Junction geometry** (`design/gdd/junction-geometry.md`): hard. Needs one-way roads (for ramps here and ring pieces there), per-road lane counts and the `at-grade` flag; `ramps-only` types never get roundabouts or turn lanes.

## Acceptance Criteria

No Formulas section at `minimal`; criteria come from Core Rules. Solo mode: `qa-lead` not consulted.

1. **GIVEN** `data/tuning.json` has the five rows, **WHEN** the sim starts, **THEN** `HalfWidth`, lanes per direction and speed for each type equal the table, and no `RoadType` switch remains in `RoadNetwork.cs` or `Tuning.cs`. (Unit test, `BuildRoadTests`.)
2. **GIVEN** two rows with the same id, **WHEN** the table loads, **THEN** loading fails and the message names the id.
3. **GIVEN** a 200 m flat dirt road, **WHEN** a truck drives it, **THEN** its speed never exceeds 9 m/s and the build price is 200 × 12 plus earthwork.
4. **GIVEN** a dirt road with a truck on it, **WHEN** the player upgrades it, **THEN** the road becomes TwoLane, the truck keeps its lane and position, the bill is the TwoLane price minus the refund share, and Paths joined to it stay joined.
5. **GIVEN** a highway and a TwoLane road ending on its side, **WHEN** the road is built, **THEN** an on-ramp and an off-ramp exist, no junction node is on the highway, and a car can route from the TwoLane road onto the highway.
6. **GIVEN** a planned ramp under 300 m from another, on a bridge, or blocked by a building, **WHEN** the road is built, **THEN** the command is refused with a reason and nothing is charged.
7. **GIVEN** a highway and a planned rail line crossing it, **WHEN** the rail is built, **THEN** it passes over or under with 5.5 m clearance, or is refused. No level crossing exists on the highway.
8. **GIVEN** a TwoLane road with 2 lanes forward and 1 back, **WHEN** a slow leader is ahead, **THEN** no vehicle overtakes through the oncoming side; with 1 and 1, one does.
9. **GIVEN** a speed limit above the type's maximum, **WHEN** the command is issued, **THEN** it is refused; below the maximum it is applied.
10. **GIVEN** a highway with ramps, **WHEN** it is demolished, **THEN** its ramps are removed and the bill includes them.
11. **GIVEN** a save from before this change, **WHEN** it is loaded, **THEN** roads keep their old types by name and the state hash after 1000 steps equals the pre-change golden hash. (Determinism test; fails if the table order leaks in.)
12. **GIVEN** a route where a highway saves time, **WHEN** a vehicle is routed, **THEN** it takes the highway.

A retained screenshot of each new type in the 3D view, in `production/qa/evidence/`, is required before the story closes (coding standards).
