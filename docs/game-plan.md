# Transport Madness: Game Plan

Vocabulary is defined in `/CONTEXT.md`. Architectural decisions are in `/docs/adr/`.

## Pitch

A modern 3D transport management game in the lineage of Transport Tycoon and Transport Fever. The player builds road and rail networks that connect towns and industries, runs vehicles, and watches towns grow because of the network. The signature is a living traffic simulation: vehicles move dynamically (overtaking, lane changes, queues, level crossings) and things can go wrong (accidents, breakdowns, blocked roads). Management depth comes from maintenance, repair quality, town relations and contracts.

Player fantasy: network builder first, profit optimizer as feedback. Tone: light, with dynamic events. Not a parody.

## Project context

- Hobby / portfolio project, solo developer working with LLMs, no team. PC (macOS Apple Silicon, Windows later). Not aimed at sale initially.
- Engine: Godot 4.7.x. Simulation in C#, UI and glue in GDScript (ADR 0001). Needs the Godot .NET editor build and the .NET SDK.
- Visual style: stylized, PS3-era 3D. Not photorealistic, not flat low-poly. Free orbit camera.
- Setting: fictional temperate world, no real brands.

## Pillars

1. **Living traffic**: dynamic movement on a lane-level network, not fixed paths. Overtaking that can fail, 2x2 roads with free lane choice, level crossings, signals.
2. **Consequence**: incidents with real effects (blocked roads, destroyed vehicles, lost cargo, fines, town satisfaction).
3. **Maintenance as management**: repair quality tiers and depot levels. Cheap patches buy time; overhauls need better depots.
4. **Towns that need you**: towns grow only when supplied with resources and when people can reach work and leisure.

## Game systems

### Time
Real time with pause and speeds 1x, 2x, 4x, 8x. Fixed simulation step, deterministic, seeded (ADR 0003). If the CPU budget is exceeded, effective speed degrades; there is no visible vehicle cap.

### World
Procedural map from a seed: terrain, rivers, towns, industries. Small to medium fixed size at first. Cargo chain length varies by layout, as a gameplay factor only, never blocking.

### Construction
Roads and rail as straight segments and circular arcs with angle and node snapping on a graph. Terrain adjusts automatically with a cost for cut and fill. No manual terraforming in the MVP. The lane graph is lane-level from day one even though early content has few road types.

Road types: dirt, 2-lane paved, 2x2 avenue, with simple on-ramps. Later: highway, roundabout. Junction control: yield, stop, traffic light. Level crossings between rail and road are in the MVP. Per-road speed limits.

### Vehicles and movement (ADR 0002)
Kinematic microsimulation on the lane graph: car-following (IDM-style), lane changes, overtaking via the oncoming lane when it looks free, box-overlap collisions. Driver traits (patience, aggression, attention) make failed overtaking a natural outcome. Trains have consists with real length, acceleration and braking, curve and grade speed limits, block and path signals, and deadlock detection with a warning. No timetables in the MVP. Jolt is used only to animate accident scenes.

Fidelity tiers:
- Tier 0: the player's vehicles and anything near the camera or near them. Full behavior and incidents.
- Tier 1: general ambient traffic. Follows queues and obeys junctions, no costly maneuvers.
- Tier 2: distant ambient traffic, aggregated as road flow and density, materialized into vehicles near points of interest. Statistical incidents create real blockages.

Ambient traffic shares the roads, causes congestion, and takes part in incidents. Pedestrians are visual only, derived from trip flows.

### Towns
Buildings by type (homes, commerce, light industry) generate trips and demand. Trip groups (not individuals) go home to work and home to leisure at times of day, choosing walking, ambient car or public transport by time and cost over the real network and station coverage. Growth needs deliveries of resources (building materials, food, goods) scaled to building size and type. Stations cover nearby buildings within a radius.

### Industries and cargo
Chains such as forest to sawmill to shop. Cargo types have different delay tolerance. Contracts request specific deliveries with deadlines and a reward bonus.

### Economy
- Income: cargo and passengers pay by distance times quantity, reduced by delay according to the cargo's tolerance. Contracts add bonuses.
- Costs: construction (including cut and fill), vehicle purchase, vehicle upkeep, road wear, depots, fines, accident compensation.
- Loan with interest. Bankruptcy when debt stays above the limit for 2 years; ends the game in challenge mode, disabled in sandbox.
- Prices and tuning live in data files, not code.

### Maintenance and repair
Vehicles and road sections have Condition (0-100). Vehicles lose Condition by distance and load; roads by traffic and weather. Low Condition raises breakdown and accident risk (bad brakes, potholes). The player sets a service policy per line (visit the depot every X days) or risks it.

Repairs:
- Patch: cheap, fast, small gain, leaves a "patched" mark that wears faster. Any depot, or a road crew.
- Service: medium cost and time, big gain. Workshop or Complex depot.
- Overhaul: expensive and slow, nearly full restore, clears accumulated patches. Complex depot only.
- Resurfacing (roads): done by a road crew; closes or narrows the road while it runs, so traffic feels it.

Depot levels: Simple, Workshop, Complex. There is no permanent age wear. Old vehicles may be kept, but burn more fuel and cost more to Patch, Service and Overhaul. Eras gate which vehicles are on sale.

### Incidents
Causes: failed overtaking, uncontrolled junctions, fast sharp curves, wet or icy road, bad brakes from low Condition, level crossings, rail signal errors. Consequences: road blocked until a Rescue crew clears it (crew dispatched from a Depot; slower if none nearby), vehicle destroyed, cargo lost, injured passengers (numbers only, no graphic content), fines, town satisfaction drop. Frequency has a game option from off to high.

### Eras
Three eras in the MVP (for example 1900-1940 steam and carts, 1940-1980 diesel, 1980-2020 modern) with 2-3 vehicles per transport mode each. Game starts as sandbox with eras gating content. Scenario goals can be added later.

### UI
Transport Fever style: Godot Control UI with mouse and keyboard, free orbit camera. Build toolbar, line window (stations, vehicles, stats), vehicle window, event panel (incidents). Map overlays for traffic density, incident risk, road Condition and station coverage.

### Art and audio
Greybox with colors per category until M5, then asset packs plus AI-generated models for final visuals. Audio (engines, horns, crashes, ambience, music) at M6 from free libraries and/or generated audio, curated by the developer.

### Save and load
Snapshot of full simulation state plus seed.

## Technical principles

- The simulation assembly never references Godot types (ADR 0001). Rendering reads state and draws, using instanced meshes with bulk data transfer.
- Data-oriented layout in hot loops (arrays of structs or parallel arrays, spatial grid, no allocation per step).
- Multithreading only where it can be partitioned and ordered to stay deterministic.
- Headless tests: same seed, same actions, same result across runs.
- Content (vehicles, road types, prices, eras) in data files.

## Milestones

| | Milestone | Done when |
|---|---|---|
| M0 | Dynamic traffic prototype | Exit criteria below |
| M1 | Procedural terrain, road building with cut and fill | Build roads across hills on a generated map |
| M2 | Stations, lines, player vehicles (bus, truck), basic economy | A profitable bus line and truck line |
| M3 | Towns with buildings, industries, cargo chains, trip flows | Town grows only when supplied and connected |
| M4 | Full rail: block and path signals, level crossings | Mixed rail and road network runs without deadlock |
| M5 | Maintenance, depots, full incidents, contracts, events | The management layer is playable end to end |
| M6 | Eras, save/load, final overlays, balancing, art, audio | A complete, polished loop |

### M0 exit criteria
1. Flat terrain, build road segments (2-lane and 2x2) with junctions.
2. 1000 ambient vehicles at 60 fps at 1x; at 8x, at least 10 fps in the worst case (Apple M4).
3. Visible overtaking on 2-lane roads, with occasional failures becoming accidents.
4. An accident blocks the road, a crew clears it, traffic reorganizes.
5. Headless deterministic test: same seed gives the same result in two runs.

Only after M0 passes do terrain, towns and economy start.

## Out of scope for now
Multiplayer, AI competitors, employee management (drivers, strikes), ships and aircraft, manual terraforming, timetables, stocks, campaign.

## Open items to decide at their milestone
Starting money and difficulty presets, map size options, exact vehicle roster per era, scenario goals, tuning numbers (all in data files), tick rate fine-tuning after profiling.
