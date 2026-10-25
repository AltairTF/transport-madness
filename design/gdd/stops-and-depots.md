# Stops and Depots

> **Status**: Designed
> **Author**: user + Claude
> **Last Updated**: 2026-10-25
> **Last Verified**: 2026-10-25
> **Implements Pillar**: none recorded (see docs/game-plan.md `## Pillars`)

## Summary

Stops and depots extends the bay pocket that road Stations have today with five larger layouts: bus bays, bus plazas, truck queue lots, multi-lane entrances and rail-served factories. They let the player fix a stop that backs traffic onto the road. Each layout is data on the existing lane graph.

> **Quick reference** — Layer: `Feature (inferred from the brief, no systems index at this tier)` · Priority: `Post-MVP (inferred)` · Category: `Gameplay (inferred)` · Key deps: `road-types, junction-geometry, Stations, Depots and Garage, Lines, traffic and routing, rail, save/load, build tool`

## Overview

Stops and depots decide where vehicles stand to load, unload and be repaired, and how many fit before they block the road. Today a road Station is one node with an optional bay pocket (2 vehicles), a Stop at an Industry or Gateway is one point at its Access point, and a Depot has 1 to 3 repair bays. When the pocket is full, the next vehicle stops in the lane. This GDD adds five larger forms: **bus bays** (a pull-in pocket per direction, sized by the lane count), **bus plazas** (a station with several bays and a shared turn-around, for a town centre), **truck queue lots** (an off-road waiting area before an Industry or Depot so trucks queue outside the lane), **multi-lane entrances** (a wider Access point with 2 or 3 lanes in, so loading and unloading run side by side), and **rail-served factories** (an Industry whose Access point also takes a rail siding, so trains and trucks load at one door). Each is a data row and a layout rule on the same lane graph, so routing, junction right of way and upkeep treat them as ordinary roads and nodes. The player feels it as the engineer's fix: a queue backs onto the main road, the player gives the stop a bay, a lot or a second lane, and the queue moves into the pocket and the road clears. A dwelling vehicle holds the road only when the stop is too small for the traffic it serves. Lane sim rules come from ADR 0002 (`docs/adr/0002-kinematic-lane-simulation-with-fidelity-tiers.md`); a stops ADR will probably be needed for the layout and queue rules.

## Detailed Design

### Core Rules

> Values are proposals for `data/tuning.json`. Solo mode: `game-designer`, `systems-designer`, `ai-programmer`, `ux-designer` not consulted. Review manually before production.

1. **One layout row per stop.** A Station, a Stop at an Industry or Gateway, and a Depot each hold a `layout` id into a table in `data/tuning.json`. `HasBays` becomes `layout = bay`. The row sets: what it applies to, loading places, price, upkeep per day, footprint, what it needs, and `upgradesTo`. Looked up by id, never by position.

| Layout | Applies to | Loading places | Footprint (m) | Price | Upkeep/day | Upgrades to |
|---|---|---|---|---|---|---|
| none | all | 1 per approach lane (in the lane) | none | 0 | 0 | bay, wide, lot |
| bay | road Station | 2 per direction, off the lane (today's `bayCapacity`) | 2 × (vehicle length + 2) beside the lane | 6000 | 200 | plaza |
| plaza | road Station | 6 (3 per direction) + turn-around | 40 × 30 | 25000 | 600 | none |
| wide | Industry/Gateway Stop, Depot | 2 per Access point, side by side | 12 wide × 30 deep | 12000 | 250 | lot |
| lot | Industry/Gateway Stop, Depot | 1 loading place + 8 waiting slots | 40 × 30 | 9000 | 150 | none |
| railDock | Industry | 1 train, own siding | rail-length 120 | 15000 | 400 | none |

2. **Bus bay.** The pocket is the existing one: the right-most lane of each approach, `bayCapacity` vehicles, `bayExitGap` 30 m to leave. Bays are offered on TwoLane and Avenue2x2 only. A third vehicle stops in the lane as today.
3. **Bus plaza.** A bay Station upgraded in place. It adds a pocket of 3 per direction and a one-way **turn-around** at the node's far side, so a line that ends at the plaza turns without entering the junction. Offered only where the Station has a free 40 × 30 m of ground clear of buildings. Vehicles pick the least busy pocket; ties go to the lower pocket index. Offered on TwoLane and Avenue2x2, never on dirt or highway.
4. **Multi-lane entrance (`wide`).** The Access point gets a 12 m wide approach with 2 loading places side by side, fed by a Path of 2 lanes. A truck takes whichever place is free; if both are, the left one. Offered when the Path is at least 20 m long.
5. **Truck queue lot (`lot`).** A lot holds 8 waiting slots, joined to the Access point by a Path. A truck that finds the loading place taken drives into the lot and takes the lowest free slot. When the loading place frees, the truck longest in the lot is called out first (arrival order, ties by vehicle id). A full lot sends the truck to wait in the lane as today. A lot also works for a Depot garage: vehicles due for repair wait in the lot instead of the road.
6. **Rail-served factory (`railDock`).** An Industry can take a rail siding that ends at its Access point. The siding makes a rail Stop covering only that Industry, like the road Stop: the train loads what the Industry makes, unloads what it uses, and the Line is paid by the distance between the Access points. A Line's stops stay all rail or all road, so a rail Line never uses the road Stop of the same Industry.
7. **Prices and refunds.** Upgrade price is the target layout's price minus an `upgradeRefundShare` (0.5) of the old layout's price. Downgrade is refused: demolish the layout, with a demolition bill of `layoutDemolishShare` (0.3) of its price. Layouts never refund on sale of the stop.
8. **Connection.** A layout needs its stop connected, as Depots do: a stop that loses its road is shut, its layout stays, and it opens again when a road reaches it.
9. **Plaza walkway reach.** The plaza row also carries `plazaReachBonus` (50 m): a walkway attached to the plaza reaches that much farther (walkstreets rule 8). Other layouts carry 0.
10. **Everything is data.** Capacity, footprint, price and upkeep come from the table. No `if (layout == ...)` appears in the sim outside the lookup.

### States and Transitions

| From | To | Allowed when | Cost |
|---|---|---|---|
| none | bay | road Station, TwoLane/Avenue | 6000 |
| bay | plaza | 40 × 30 m free ground | 25000 − 3000 |
| none | wide | Path ≥ 20 m | 12000 |
| wide | lot | 40 × 30 m free ground | 9000 − 6000 |
| none | lot | 40 × 30 m free ground | 9000 |
| none | railDock | rail reaches the Access point | 15000 |
| any | none | player demolishes it | 0.3 × price |

Vehicle states at a stop: **Approaching → Waiting (lane or lot slot) → Called → Loading → Leaving**. Only Waiting and Called are new.

### Interactions with Other Systems

- **Stations and Lines** (`Station.cs`, `Simulation.Stations.cs`): `HasBays` becomes `Layout`; `BayLanes` and `StopLanes` read the row.
- **Fleet** (`Simulation.Fleet.cs`): bay entry and exit (`LeaveBay`, `BayExitClear`) generalise to slots; the lot adds one more `_bay`-like state.
- **Garage and Depots** (`Simulation.Garage.cs`, `DepotSites`): lot and wide apply to a Depot door; the 1/2/3 repair bays stay separate.
- **Junction geometry:** a plaza's turn-around is a one-way ring piece, so it needs the same one-way road support. Turn pockets and a bay on one approach cannot both claim the right-most lane (see Edge Cases).
- **Road types:** layout offered by type. Highway never gets one.
- **Rail** (`RailNetwork`, `Simulation.Rail`): `railDock` is a rail Stop at an Industry.
- **Economy:** price, upkeep, demolition. **Save/load:** layout id, save format number goes up. **Build tool:** layout picker and footprint preview.

## Edge Cases

> Solo mode: `systems-designer` not consulted. Review manually before production.

- **If a bay and a turn pocket both want the right-most lane of one approach**: the turn pocket (junction-geometry) keeps the lane and the bay is refused with a reason. A bay needs 2 × (length + 2) m of straight lane before the stop margin, outside every pocket.
- **If a layout footprint overlaps a building, another layout, a road or a rail line**: refused with the reason and nothing is charged. Ground inside the footprint is levelled as Depots are, and the 6 m cut-or-fill limit applies.
- **If a lot is full**: a new truck waits in the lane, as today. It never circles and never leaves its Line.
- **If the loading place frees while the lot is empty**: the next truck on its way takes it directly.
- **If a called truck is blocked by lane traffic leaving the lot**: it waits for the existing `bayExitGap` rule. The loading place is held for it for 20 s, then offered to the next truck in the lot. The late truck re-enters the lot.
- **If a lot truck's Line is deleted or the truck is sold**: it leaves its slot at once and the slot is free. Calling order skips empty slots.
- **If the Industry closes (raid, strike) while trucks wait in the lot**: they stay. A truck gives up after `lotPatienceDays` (3) and returns to its Line's next stop.
- **If the stop's road is demolished**: the stop shuts, the layout stays (rule 8), vehicles in the lot or pockets are re-routed to the Depot Garage. Cargo aboard is lost only if the vehicle itself is lost.
- **If a plaza's turn-around is blocked by a wreck**: vehicles in the plaza load and leave through the junction as at a bay. The wreck is cleared like any other.
- **If a Line has only 1 vehicle at a plaza**: the plaza still works; the extra pockets stay empty and upkeep is still charged.
- **If a Gateway Stop gets `wide`**: allowed. Both places are used by through freight; no passenger rule changes.
- **If a `railDock` siding is cut**: the rail Stop shuts as a road Stop does; a train already inside is held by the existing signal rules and leaves when the block clears.
- **If a Depot with a lot is upgraded Simple → Workshop → Complex**: the lot stays; the repair-bay count follows the Depot level. The two are independent counts.
- **If a layout row names an unknown `upgradesTo`, or two rows share an id**: the game refuses to start and names the row.
- **If a save from before this change is loaded**: `HasBays` maps to `layout = bay`, `false` to `none`, by name not by position. The state hash after 1000 steps must equal the pre-change golden hash.

## Dependencies

**Upstream (this GDD needs):**
- **road-types** (`design/gdd/road-types.md`): hard. Layout is offered by road type (never on `ramps-only`). The plaza turn-around needs the same one-way road support as ramps.
- **junction-geometry** (`design/gdd/junction-geometry.md`): hard. A bay and a turn pocket share the right-most lane. The plaza ring piece is a one-way road.
- **Stations and Lines** (`Station.cs`, `Simulation.Stations.cs`): hard. `HasBays` becomes `Layout`.
- **Fleet and Garage** (`Simulation.Fleet.cs`, `Simulation.Garage.cs`): hard. Bay entry/exit generalise to slots; a lot waits in the Depot Garage too.
- **Depot sites and Pads** (`Simulation.DepotSites.cs`, `Simulation.Pads.cs`): hard. Footprint levelling and the 6 m cut/fill limit.
- **Access points and Paths** (`Simulation.Access.cs`): hard. `wide` and `lot` hang off the Path.
- **Rail** (`RailNetwork.cs`, `Simulation.Rail.cs`): hard for `railDock` only.
- **Tuning** (`data/tuning.json`): hard. The layout table lives there.

**Downstream (needs this GDD):**
- **Routing and traffic:** hard. Edge cost and queue spill-back change with pockets and lots.
- **Economy and upkeep:** hard. Price, upkeep, demolition bill.
- **Save/load:** hard. Layout id per stop; save format number goes up.
- **Build tool and UI** (`game/build_tool.gd`, `SimHost.cs`): hard. Layout picker, footprint preview.
- **Walkstreets** (`design/gdd/walkstreets.md`): soft. A plaza is where a walkstreet meets a bus stop; the plaza row carries `plazaReachBonus` (+50 m of walkway reach).

## Acceptance Criteria

No Formulas section at `minimal`; criteria come from Core Rules. Solo mode: `qa-lead` not consulted.

1. **GIVEN** the layout table has the six rows, **WHEN** the sim starts, **THEN** loading places, price and upkeep per layout equal the table, and no `if (layout == …)` outside the lookup remains in `Simulation.*.cs`. (Unit test.)
2. **GIVEN** two rows with one id, or an `upgradesTo` naming no row, **WHEN** the table loads, **THEN** loading fails and the message names the row.
3. **GIVEN** a TwoLane Station with a bay and 3 buses arriving together, **WHEN** they load, **THEN** 2 stand in the pocket, the third stops in the lane, and a car behind it is held only by that one bus.
4. **GIVEN** a straight approach with a turn pocket on its right-most lane, **WHEN** the player adds a bay, **THEN** the command is refused with a reason and nothing is charged.
5. **GIVEN** a bay Station with a free 40 × 30 m, **WHEN** it is upgraded to plaza, **THEN** the bill is 25000 − 0.5 × 6000, there are 3 pockets per direction and a turn-around, and a Line ending there turns without entering the junction.
6. **GIVEN** a plaza on a dirt road or a highway, **WHEN** the upgrade is issued, **THEN** it is refused with the road type as the reason.
7. **GIVEN** an Industry with `wide` and 2 trucks arriving together, **WHEN** they load, **THEN** both load at once, the left place first.
8. **GIVEN** a `lot` and 10 trucks arriving at one loading place, **WHEN** they queue, **THEN** 8 wait in slots, 1 loads, the 10th waits in the lane; and when the place frees, the truck longest in the lot goes next (ties by vehicle id).
9. **GIVEN** a called truck blocked for over 20 s, **WHEN** the hold ends, **THEN** the next lot truck is called and the late one returns to a slot.
10. **GIVEN** a truck in a lot whose Line is deleted, **WHEN** the command runs, **THEN** its slot is free the same step.
11. **GIVEN** an Industry with a rail siding at its Access point, **WHEN** a rail Line stops there, **THEN** the train loads what the Industry makes, and pay is by the distance between Access points; a road Line cannot use the rail Stop.
12. **GIVEN** a stop with a layout whose road is demolished, **WHEN** a day passes, **THEN** the stop is shut, its layout and upkeep remain, vehicles go to a Garage, and it reopens when a road reaches it.
13. **GIVEN** a footprint over a building or a rail line, **WHEN** the layout is placed, **THEN** it is refused with the reason and nothing is charged.
14. **GIVEN** a save from before this change, **WHEN** it is loaded, **THEN** `HasBays` stations read as `bay`, the rest as `none`, and the state hash after 1000 steps equals the pre-change golden hash.
15. **GIVEN** the same seed and commands, **WHEN** run twice, **THEN** lot call order and plaza pocket choice give identical state hashes. (Determinism test.)

A retained screenshot of each layout in the 3D view, in `production/qa/evidence/`, is required before the story closes (coding standards).
