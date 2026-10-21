# M6f: free-standing depots, industry stops with configurable loading, and a line preview

Status: slices A, B and C built 2026-10-21 (see `docs/project-status.md` 5.49 and 5.50 for what differs from this plan). Agreed with the developer in two grilling rounds; every recommendation was accepted, with the changes noted under Decisions. Second part of the tool usability review (the first is M6e, `docs/m6e-plan.md`); M7 (art and audio) follows.

## Context

Three problems of play came out of the developer's trial of M6e:

1. A Depot can only go on an existing road node within 60 m (`FindDepotNode`, `depotSnapRadius`); it should go anywhere, and be joined to a road, or put beside one.
2. A Line can only stop at Stations, and cargo only flows industry to `Station.Waiting[]` to vehicle (a Station covers industries within 150 m). The developer wants to build a line straight from one industry to another, with the loading and unloading of each stop under their control, so that industries can be linked directly.
3. The New line tool shows only a straight ribbon between the Stations already clicked: no route, no length, no warning.

What the code does today (from an exploration on 2026-10-21):

- `Depot` (`Simulation.Incidents.cs`) has only `Node`; no position or footprint. Spawn, return to depot, service bays, rescue dispatch, closing (`CloseOrphanedDepots`) and `MoveNode` all use `depot.Node`. The tool hard-codes the Workshop level and its preview is a fixed box that is always valid.
- A Line's stops are `int[]` Station ids (`Line.Stops`, `ValidStops`, `CreateLineCommand`); `TargetStation`, `RouteNextLane`/`FieldTo(Station)`, `BeginStop`, `Deliver`, `PayFor` all take Stations. There are no per-stop options.
- Loading takes from `station.Waiting[]` (`CargoToLoadFor`); unloading is `Deliver` then `Distribute` over the covered buildings and industries, and the rest is exported to covered gateways. `FieldToNode(node)` already routes a vehicle to any node (used for depots and ambient cars).
- Passengers: trip groups reach industries through a Station that covers them.

## Decisions

### Depot (slice A)

- A Depot is a building at the clicked point, with a position and an angle, and an **access point** like an Industry's (the M6e rule applies to it): a road that passes within 60 m of one that no road reaches yet gets a Path to it. Building the Depot with a road within 60 m builds the Path in the same command, charged with the Depot's price.
- Placed within 12 m of a road's centre line, the Depot stands beside the road and its access point is on the road itself (the road is split there, as a Station does); no Path.
- A Depot with no road at its access point is **not connected**: no vehicles are bought there, no rescue crew is dispatched from it, no service. It connects again by itself when a road reaches the point. Today a Depot with no roads at its node closes for good; that becomes "not connected" and is no longer final.
- One tool item, not one per level. A Depot is built as a Simple Depot and raised by the upgrade it already has (Simple, Workshop, Complex), from its window; the price of building is the Simple price, the upgrade charges the difference as it does today. The look changes with the level (the box grows and gains blocks), the sizes per level in `data/whitebox.json`, so art can replace them.
- The preview shows the box where it would stand with its door toward the access point, the Path, the price, and turns red with the reason when it cannot be built.
- Saves of an older version are refused already (the type table is checked); nothing is migrated.

### Industry and gateway stops (slice B)

- A Line may stop at an Industry or a road Gateway (road lines only), as well as at Stations, in any mix. The stop is the thing itself: the vehicle drives to its access node (`AccessNodeOf`), dead end included, and takes cargo from the Industry's output stock and puts cargo into its input stock directly; no Station is needed. At a Gateway, unloading exports and loading takes imported stock. Payment is by the distance between the two stops' points (`AccessPointOf` for these).
- Each stop, of any kind, has options the Player sets in the line's window (all on by default):
  - **Load**: on or off, and which cargo (any, or one) when the place makes more than one.
  - **Unload**: on or off, and a **share**: the percentage of what the vehicle took at the last loading stop of the line. The rest stays aboard for the next stop. A stop with unloading on and no share set takes 100% of what remains. It never takes more than the Industry's input stock still has room for; the surplus stays aboard.
  - Example (the developer's): a sawmill stop (the line's first) loads 100 wood; the second stop is a planks works with unload 50%, the third another with 100%, and loading is switched off at both, so the truck does not take planks. The truck leaves 50 at the first, the other 50 at the last, and starts again.
- Passengers: a stop at an Industry also moves people. A passenger vehicle there lets off the passengers whose destination is that workplace, and workers of that Industry may board for the stops that follow. Only that Industry's own workers, never people bound elsewhere.
- Station and direct stop compete for the same output stock if an Industry has both (the Station receives its split every tick as today; the vehicle takes what is there on arrival). No reservation: an Industry that should only feed the direct stop has no Station covering it.
- Rail lines are not touched.

### Line tool and preview (slice C)

- The New line tool picks Stations and, near them, Industries and road Gateways, as the Station is picked now (a radius that grows with the camera). The line's window gets the per-stop options above and can add or drop any kind of stop.
- The preview shows the route as the vehicle would drive it (the same lane route as `FieldTo`/`FieldToNode`) between the stops already placed, the stretch from the last stop to the cursor, the length of the whole line, and what each leg would carry (what a stop makes that the next accepts). A leg with no road connection is drawn red and the status line says "no road connection". It comes from a new read-only sim query, so that the preview and the vehicles cannot disagree.

## Slices

Each is built, tested, committed and pushed separately, as in M6e. Slice A touches the Depot only; slice B changes what a stop is; slice C is the game layer. A and B are independent.

### A. Free-standing depots
- `Depot` gets `X`, `Z`, `Angle` and an access point; `Node` is found from the access point (`AccessNodeOf`-like) in `SyncNetwork` and is -1 while not connected. Everything that reads `depot.Node` (`Simulation.Fleet.cs` spawn and `DepotsByDistance`, `Simulation.Garage.cs` return, `Simulation.Repairs.cs`, `Simulation.Incidents.cs` dispatch, `MoveNode` at `Simulation.Commands.cs:54`) treats -1 as not connected. `CloseOrphanedDepots` becomes the connected check.
- `AddDepotCommand` takes a position, not a node: it finds a road within 12 m (split, as `FindStationSite`/`BuildStation` do) or builds the Path with `SpursOf`/`BuildRoadPiece` (`Simulation.Access.cs`). `AccessPoints()` includes Depots so passing roads get their Path.
- Game: `SimHost.BuildDepot`, the depot tool and preview in `build_tool.gd`, the upgrade in the Depots window, level looks in `data/whitebox.json` and `SimHost` drawing, `picking.gd`, `SimHostGarage.GetDepots`.
- Tests that must change: `RulesLayerTests` (the node snap tests and the `Road()` helper), `MoveNodeTests` (a Depot follows its node), `Worlds.Depot` and the about 30 files that use `AddDepotCommand` (BalanceProbe, CampaignBots, DefenceTests, GatewayTests, WorkerTests and others), `DeterminismTests` golden hashes (the depot hash and command log change), `SaveLoadTests`. New tests: a Depot far from roads is not connected; a road built to it connects it; one beside a road splits it; the Path is charged; a not connected Depot sends no crew and takes no vehicles.

### B. Industry and gateway stops
- A stop becomes a reference (a Station, an Industry or a Gateway) instead of a Station id: `Line.Stops`, `ValidStops`, `CreateLineCommand`, `EditLineCommand`, `FleetHash`, `Transit.cs`. Per-stop options (load, cargo, unload, share) are saved with the line.
- Routing: `TargetStation` and the places that call it, `FieldToNode` for a stop that is not a Station, arrival and stop position at a dead end (`ArrivedAtStop`, `StopInteraction`), and `BeginStop` loading and unloading directly against `industry.Output` and `industry.Input` (`CargoToLoadFor`, `Deliver`/`Distribute`/`IndustryRoom`, `ShipOutput`), payment with `PayFor` using the access points, passengers through the Industry's workers (`Simulation.Trips.cs`, `Simulation.Transit.cs`).
- An implicit "Station at the access node" per Industry (so loading, passengers and payment reuse the Station code) is one way to do this and is to be weighed when building: a Station needs a stop lane at least `MinApproach` (30 m) long and a Path can be as short as 10 m.
- Tests: `FleetTests`, `LineEditAndBayTests`, `TripTests`, `IndustryTests`, `HeavyChainTests`, `CoverTests` and every test that builds a line stay green; new tests for the sawmill example (100 loaded, 50 and the rest unloaded, no planks taken), a stop with load off, unload limited by room, a Gateway stop, passengers let off at their workplace, and the command log replaying to the same hash.

### C. Line tool, line window and preview
- `build_tool.gd` (`_click_line`, `_finish_line`, `_show_line_draft`, `_hover_line`) picks any stop kind and draws the route preview; `SimHost` gets the preview query and the line API for stop options; `game/ui/lines_window.gd` gets the per-stop options (load, cargo, unload, share) and adds and drops any stop kind.
- Seen in the .NET editor through the bridge, like M6e.

## Documentation to update when building

`CONTEXT.md` (as each slice is built): **Depot** (a building with an access point, not connected until a road reaches it, built Simple and upgraded, no longer closing for good), **Line** (stops may be Stations, Industries or road Gateways), a new **Stop** (a place a Line's vehicles stop at, with load and unload options and a share), **Access point** (Depots too). `docs/project-status.md` sections 5.49 and on, and the index in `docs/README.md`. An ADR only if the stop-as-reference change proves hard to reverse.

## Verification

`dotnet test` on `sim/` (environment in the project's test notes) after every slice, the whole-campaign Balance category once at the end (the bots build depots and lines, so their numbers may move), and the game checked in the .NET editor through the godot-ai bridge: a depot far from roads and one beside a road, a line from a sawmill to two planks works with the shares above, the line preview with a leg that has no road.
