# Transport Madness

3D transport management game set as a campaign: the player chairs the development committee of a small region of a fictional country at war, from January 1938 to December 1945, and manages its military and civil industrialisation with roads, rail, vehicles, towns and industries. See `docs/game-plan.md` for the plan, `docs/project-status.md` for what exists, how it works and what comes next, `CONTEXT.md` for vocabulary and `docs/adr/` for decisions.

## Repository

Hosted at https://github.com/AltairTF/transport-madness (private), branch `main`, created on 2026-10-19 with the GitHub CLI after the M2 commits. The `origin` remote is HTTPS, not SSH: the SSH key on the development machine authenticates as the other GitHub account (`AltairTF96`, the git user name), which has no access to a repository owned by `AltairTF`. `gh auth setup-git` makes git use the `gh` login for HTTPS. To work from a fresh checkout:

```sh
gh auth login                                   # as AltairTF
git clone https://github.com/AltairTF/transport-madness.git
cd transport-madness/sim && dotnet test         # 692 tests, about 50 s
BENCH=1 dotnet test -c Release --filter StepBenchmarks --logger "console;verbosity=detailed"   # step time, what the CI benchmark job runs
```

`.godot/`, `bin/` and `obj/` are ignored, so Godot imports the project on first open.

## Layout

- `sim/` the simulation: a pure C# library with no Godot types (ADR 0001) and its xUnit tests. Godot ignores this folder (`.gdignore`).
- `data/` tuning and prices (`tuning.json`), embedded in the simulation assembly; edit and rebuild to retune.
- `game/` the Godot side: `SimHost.cs` steps and draws the simulation, GDScript does camera, HUD and the build tool.
- `transport-madness.csproj` the Godot .NET project; it references `sim/`.

## Run

Needs the Godot 4.7 .NET editor build and the .NET 8 SDK (a newer SDK runs the tests with `DOTNET_ROLL_FORWARD=Major`).

```sh
cd sim && dotnet test                                   # the whole simulation test suite, headless
godot --path .                                          # play: build roads, +100 cars, Crash!
godot --path . -- --bench                               # frame rate with 1000 vehicles at 1x and 8x
godot --path . -- --selftest-build --shot=build.png     # builds roads with synthetic mouse clicks
godot --path . -- --towns=5                             # generated hills with five towns, a calendar and station coverage
godot --path . -- --hills                              # empty generated-hills map with lakes and rivers: roads show grade, cut/fill cost, bridges and tunnels
godot --path . -- --selftest-hills                      # builds roads over hills, prints grade and cost, quits
godot --path . -- --selftest-crash                      # crashes a car and follows the rescue crew
godot --path . -- --selftest-lines                      # builds stations and a bus and a truck line, runs 30 days, prints the books
```

Controls: right mouse orbits, middle mouse or WASD pans, wheel zooms. Pick a road tool in the toolbar, click to start and click again to build; ending on or crossing another road makes a junction. **Curve** and **Curved avenue** take three clicks: start, a point on the curve, end. **Upgrade** widens a two-lane road to an avenue, **Downgrade** narrows an avenue, **Faster** and **Slower** change a road's speed limit by 10 km/h, **Remove** tears a road piece down (junction to junction); all show the cost when you hover. **Move** picks up a junction with one click and drops it with the next (its roads are rebuilt with the same bend, may cross other roads, and the vehicles on them carry on). Esc stops.

The company: money, the ministry's advance, the date and the time of day are at the top (a day is 20 minutes of game time and a month is 3 days), the Company window has the advance (ask for 100k, repay 100k), the standing and the allocation, and every tool above now costs money (hover shows the price). **Station** builds on the road under the mouse (the road is split there; vehicles stop on the lanes that run into it). **New line** takes stations one click at a time, Enter finishes the line. The Lines panel on the right has, for each line, buttons to buy a bus or a truck (they come out of the depot with the quickest way to the line, one after another, so build a depot first; one parked in a depot pays a tenth of its upkeep) or to delete the line and sell its fleet. The Vehicles window lets you buy a vehicle for a depot with no line (pick the depot, then the model), keeps those waiting in depots with a choice of line and **Put on line**, and has **To depot** on every vehicle on the road to take it off its line and park it again. Buses carry passengers, trucks carry goods, and from the years in the roster a workers' bus, a heavy truck and a large bus can be bought.

Rail (M4): the **Rail** category of the toolbar has **Track** and **Curved track** (the same clicks as roads; tracks join where they meet, cost cut and fill, and a road they cross gets a level crossing), **Block signal** and **Path signal** (R turns a signal round; a train waits at a block signal unless the whole block beyond is free, at a path signal unless the path it will take is), **Remove signal**, **Remove track**, **Rail station** (on a track) and **Rail depot** (at a track junction or end). **New line** takes rail stations too (a line is all road or all rail); the Lines window then has buttons to buy a locomotive with four or eight coaches or freight wagons, which appear at the rail depot with the quickest way to the line's first station. The Vehicles window lists trains with the road vehicles. A single track with trains turning round at both ends deadlocks when they meet (the game says so and paints the trains red): add a passing loop with path signals. Traffic lights (**Roads → Modify**: Traffic light, Remove light) make the crossing streets of a junction take turns.

## Campaign (C1 to C9, M5)

The game is a campaign of 96 months (288 days). The government gives the committee a monthly allocation and judges it by its standing, 0 to 100; under 20 for six months in a row the committee is dismissed, and in December 1945 one of four grades is given. Two gateways on the map edge (one road, one rail) bring in coal, ore, oil, rubber and fuel and take out what the state asks for. Orders (production quotas, state works, civil needs) come at random times inside the phases of the war. Coal and iron ore are scarce on the map, there are 16 kinds of industry and 19 cargo types, electricity is a regional capacity, big industries need workers carried by workers' buses, and the fleet is gated by model year. From 1938 the war reaches the game in four ways: rationing, call-up waves, requisition of part of the fleet and, from 1942, air raids. Vehicles and trains wear and break down, and a maintenance policy and depots of three levels keep them running (M5). The design is in `docs/campaign-plan.md` and ADR 0006; the milestone table is in `docs/game-plan.md`. Only M6 is left (save and load, overlays, balancing, art, audio).

## M0 status

| Exit criterion | State |
|---|---|
| 1. Flat terrain, build 2-lane and 2x2 roads with junctions | Done: crossings, T junctions and shared nodes, in the running game |
| 2. 1000 ambient vehicles at 60 fps at 1x; at least 10 fps at 8x | Met on the development machine (Apple M4): 60 fps at 1x, 65 fps at 8x with the simulation at full 8x; one step costs about 1 ms or less depending on the world, see `docs/project-status.md` for the measurements |
| 3. Visible overtaking with occasional failures becoming accidents | Done: roughly one head-on in 70 overtakes |
| 4. An accident blocks the road, a crew clears it, traffic reorganizes | Done: wrecks block their lane, crews come from the nearest free depot, drivers avoid blocked roads |
| 5. Headless deterministic test | Done: same seed, same hash, and a command log that replays a whole run with building, crashes and crews (`TrafficTests`, `DeterminismTests`) |

M1 (terrain, water, bridges and tunnels, road editing), M2 (company, stations, lines, bus and truck, money), M3 (towns, industries, trips, growth), M4 (rail, signals, level crossings, traffic lights, road signs) and M5 (maintenance, depot capability, full incidents), and the campaign slices C1 to C9, are done and described in `docs/project-status.md`.

Towns (M3a): the Towns map has 2 to 10 towns (a slider in the new-game screen) of buildings along their streets; a station serves the buildings in its radius, a road built through buildings demolishes them for a price (shown in the preview) and costs the town satisfaction, and buildings cut off from every road are abandoned after 3 months and demolished a month later. The Towns window lists them. Industries (M3b): forests, quarries and farms produce wood, stone and grain; sawmills, cement works and food plants turn them into planks, building materials and food; a factory makes goods from planks. A station only accepts what the buildings and industries in its radius use; a truck takes the cargo waiting at a stop that the next stop accepts, so a line from a forest to a sawmill carries wood by itself. Towns use food, goods and planks every month and keep building materials and planks for growing; a town whose needs go unmet loses satisfaction. The Industries and Stations windows show stocks and what each station accepts. Trip groups (M3c): the people of each home travel in groups on a daily schedule (to work in the morning, home in the evening, out for leisure at night), on foot when the place is close, otherwise by ambient car or by the company's buses, whichever costs less in time and money; at a station they queue for the station they are going to, and a bus takes only those whose stop is on its line and pays by the distance each rides. The Towns window shows yesterday's trips by mode and the Stations window who is waiting for where. Growth (M3d): once a month a town that is satisfied and connected (60% of its homes can reach work or leisure) spends the building materials and planks delivered to its buildings: a building with a full stock goes up a size, one already biggest founds a new building on a free lot, and a town with no free lot builds a new block of streets. The Towns window shows connectivity and why a town is not growing. This closes M3 (`docs/m3-plan.md`).

Known gaps: the 3D side of M5 and the campaign was played by the developer and works, but the interface is awkward and hard to reach and is to be dealt with after the features; no tier 1 and 2 ambient traffic; trains cannot collide or derail, and on the rail side incidents are trains hitting vehicles at crossings and failed signals; the few cars that are not trip cars are a random walk over the network; raids cannot be defended against and nothing they destroy is rebuilt; no save and load, no weather, no art or audio (M6); vehicles are boxes.
