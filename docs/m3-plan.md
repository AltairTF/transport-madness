# M3 plan: towns, industries, cargo chains, trip flows

Status: designed (grilled and agreed 2026-10-19). M3a and M3b are built (see `project-status.md` sections 5.11 and 5.12); M3c and M3d are not started. Done when (game-plan.md): a town grows only when it is supplied and connected.

M3 is built in four vertical slices; each leaves a playable, tested game. Numbers named here live in `data/tuning.json` (every key required), not in code. Vocabulary is in `/CONTEXT.md`.

## Slices

### M3a: towns, buildings, coverage, calendar (built)
- `dayLength` goes from 60 s to 600 s so that a commute fits inside a day. Per-day amounts (upkeep, interest, pay timings that are expressed per day) are rescaled so the economy stays the same per second. Golden hashes change once, deliberately.
- Calendar: 30-day months, 12-month years. HUD only, plus the clock of the day (needed by M3c). The sim keeps the integer day counter and derives month and year from it.
- Towns and buildings in the sim: `Town`, `Building` (type home, commerce or light industry; size tier; lot; road access point). Stored in flat arrays like vehicles, with a spatial grid for coverage queries.
- Generation: `NewTownsWorld(seed, options)` and a "Towns" map in the new-game screen next to Demo, Hills and Empty (those stay for tests). The generator draws from its own RNG derived from the seed, so existing seeds and golden hashes are unaffected. Town streets are built with `RoadNetwork.BuildRoad` before `Simulation` exists: free, outside the command log, regenerated identically on replay.
- Placement: flat land (slope below a limit, clear of water by a margin), minimum spacing between town centres. Layout: a grid of two-lane streets aligned to the flattest direction, one avenue as the main street through the centre, lots of about 30 m, bigger buildings near the centre. The generator builds no roads between towns or to industries.
- New-game options: a towns slider (2 to 10, default 5; fewer are placed if land runs out, and the screen says so) and an industries option (few, normal, many; used from M3b).
- Station coverage: buildings within `stations.coverageRadius` are served. Until M3c a station's passengers and goods come from its covered buildings (type and size tier) instead of the fixed `supplyPerDay`. This is a stopgap that M3c replaces.
- Player roads: streets are ordinary roads, fully editable (upgrade, speed limit, remove, move). A road that would run through buildings demolishes them for a price (per building, scaled by size tier); the cost is part of the build preview's total and the build is refused if unaffordable. Satisfaction drops a little per building demolished.
- Abandonment: a building that loses all road access becomes inactive (no trips, no supply, no needs). After 3 months it is marked abandoned (drawn dark, satisfaction drops more). After 1 more month the town demolishes it. If a road gives it access again before then, it becomes active.
- Satisfaction: a per-town number 0 to 100. In M3 it only gates growth (M3d) and shows in the Town window; money effects, fines and incidents come with M5.
- UI: Town window replaces the HUD placeholder (name, population, buildings by type, satisfaction). Greybox buildings. Coverage circle on the station preview.
- Tests: generation is deterministic per seed and leaves old seeds untouched; towns are on flat land and respect spacing; station coverage picks the right buildings; demolition cost and refusal; abandonment timeline and recovery; calendar maths.

### M3b: industries, chains, cargo acceptance (built)
- Eight cargo types: passengers, wood, planks, stone, building materials, grain, food, goods. Chains:
  - wood (forest) to planks (sawmill)
  - stone (quarry) to building materials (cement works)
  - grain (farm) to food (food plant)
  - planks to goods (factory)
- `Industry` (primary, processor), placed by the generator on suitable land; count from the industries option. Primary industries produce a fixed amount per month into a capped output stock. Processors turn inputs into outputs at a fixed ratio, limited by input on hand. No service-based boost in M3.
- Output goes to every station that covers the industry, split evenly, and waits there (capped like other cargo). A station accepts a cargo only when some covered building or industry uses it; processors receive inputs the same way.
- Town needs (per building tier, in tuning). Sustain, monthly: homes need food and goods; commerce needs goods; light industry needs planks. Construction (for growth in M3d): building materials and planks.
- Delivery: units are split among the covered buildings that need that cargo, weighted by need, within the unloading station's `coverageRadius`. Each building holds its own sustain stock and construction stock; no town-level stock. A sustain shortfall lowers satisfaction.
- Industry window replaces the placeholder (built, with a Stations window showing waiting cargo and acceptance). Trucks became one `freight` model that takes the cargo the next stop accepts, so no model per cargo is needed.
- Tests: a chain runs end to end with a truck line at each hop; acceptance follows coverage; delivery splits by need; shortfall lowers satisfaction.

### M3c: trip groups
- A trip group is a number of people (fixed by the building's size tier, so population shows in the trips) with a home, a fixed workplace and a leisure destination.
  - Workplace chosen at creation: weighted random by free jobs and nearness, seeded, so trips are stable and deterministic.
  - Leisure destination: weighted-random commerce building.
- Daily schedule (windows in tuning, a day is 600 s): work out 07:00 to 09:00 and back 16:00 to 18:00; leisure 18:00 to 21:00, home by 23:00; each group jittered by a seeded amount up to 45 minutes. Almost no travel at night.
- Mode choice: each group has a seeded car-available flag (share from tuning). Walk if the destination is inside the walk radius. Otherwise the lowest generalised cost (time plus a money weight) among the modes available to it. Public transport only counts if a line links a station near the origin to one near the destination. The choice is cached per building pair and redone when the network version changes.
  - Walking is a timer (straight-line distance over walk speed, no pathfinding); pedestrians are a visual derived later.
  - Car: one ambient vehicle stands for 2 people, spawned at the origin's access point and driving to the destination's. A cap on concurrent trip vehicles (inside the 1000-vehicle budget) makes extra trips wait.
  - Public transport: direct lines only (no transfers in M3). Passengers wait at the station in queues per destination station, and a bus loads only those whose destination is on its line. This replaces the single float per cargo for passengers; freight stays one count per cargo type per station.
- Ambient traffic: trips replace the random walk inside towns. A small seeded baseline of through-traffic stays so roads outside towns are not dead.
- Passenger supply by coverage (the M3a stopgap) is removed.
- Performance gate: no entity per person; profile at a realistic town size before and after, and keep the 1000-vehicle step-time test.
- Tests: determinism with trips running; schedule respects the windows; a connected town produces measurable ridership; removing a link moves trips to cars or walking; passengers unload only at their destination.

### M3d: growth and the exit test
- Connectivity (town level): a building is reached in a month when its trip groups finished their work and leisure trips within a time budget by any mode; connectivity is the share of reached buildings.
- Growth is local to where cargo is delivered. A building upgrades through size tiers from its own construction stock. A building at its top tier with construction stock to spare spawns a new building on a free lot beside it; when there is no free lot, the street grid extends by one block. More buildings and bigger buildings mean more people.
- Gates: construction stock covers the step, town satisfaction is above its threshold, connectivity is above its threshold. The town stalls when a gate fails; it never declines in M3 (only the abandonment above removes buildings).
- Growth is evaluated once a month.
- Tests (the exit test): an unserved town keeps its size over N years; a served and connected town grows; the same town served but disconnected does not; growth appears around the station that delivers.

## Cross-cutting
- Slice order is fixed: real trips (M3c) come before growth (M3d) so growth is built on real connectivity.
- Update `project-status.md` and `CONTEXT.md` at the end of each slice; keep the golden hashes honest (record why each change).
- Not in M3: town decline, transfers between lines, service-based production boosts, generated roads between towns, money effects of satisfaction, pedestrians drawn (visual only, later).
