# Transport Madness

3D transport management game, see `docs/game-plan.md` for the plan, `docs/project-status.md` for what exists, how it works and what comes next, `CONTEXT.md` for vocabulary and `docs/adr/` for decisions.

## Repository

Hosted at https://github.com/AltairTF/transport-madness (private), branch `main`, created on 2026-10-19 with the GitHub CLI after the M2 commits. The `origin` remote is HTTPS, not SSH: the SSH key on the development machine authenticates as the other GitHub account (`AltairTF96`, the git user name), which has no access to a repository owned by `AltairTF`. `gh auth setup-git` makes git use the `gh` login for HTTPS. To work from a fresh checkout:

```sh
gh auth login                                   # as AltairTF
git clone https://github.com/AltairTF/transport-madness.git
cd transport-madness/sim && dotnet test         # 332 tests
```

`.godot/`, `bin/` and `obj/` are ignored, so Godot imports the project on first open.

## Layout

- `sim/` the simulation: a pure C# library with no Godot types (ADR 0001) and its xUnit tests. Godot ignores this folder (`.gdignore`).
- `data/` tuning and prices (`tuning.json`), embedded in the simulation assembly; edit and rebuild to retune.
- `game/` the Godot side: `SimHost.cs` steps and draws the simulation, GDScript does camera, HUD and the build tool.
- `transport-madness.csproj` the Godot .NET project; it references `sim/`.

## Run

Needs the Godot 4.7 .NET editor build and the .NET 8 SDK.

```sh
cd sim && dotnet test                                   # the whole simulation test suite, headless
godot --path .                                          # play: build roads, +100 cars, Crash!
godot --path . -- --bench                               # frame rate with 1000 vehicles at 1x and 8x
godot --path . -- --selftest-build --shot=build.png     # builds roads with synthetic mouse clicks
godot --path . -- --towns=5                             # generated hills with five towns, a calendar and station coverage
godot --path . -- --hills                              # empty generated-hills map with lakes and rivers: roads show grade, cut/fill cost, bridges and tunnels
godot --path . -- --selftest-crash                      # crashes a car and follows the rescue crew
godot --path . -- --selftest-lines                      # builds stations and a bus and a truck line, runs 30 days, prints the books
```

Controls: right mouse orbits, middle mouse or WASD pans, wheel zooms. Pick a road tool in the toolbar, click to start and click again to build; ending on or crossing another road makes a junction. **Curve** and **Curved avenue** take three clicks: start, a point on the curve, end. **Upgrade** widens a two-lane road to an avenue, **Downgrade** narrows an avenue, **Faster** and **Slower** change a road's speed limit by 10 km/h, **Remove** tears a road piece down (junction to junction); all show the cost when you hover. **Move** picks up a junction with one click and drops it with the next (its roads are rebuilt with the same bend, may cross other roads, and the vehicles on them carry on). Esc stops.

The company: money, loan, the date and the time of day are at the top (a day is 10 minutes), **Borrow 100k** and **Repay 100k** change the loan, and every tool above now costs money (hover shows the price). **Station** builds on the road under the mouse (the road is split there; vehicles stop on the lanes that run into it). **New line** takes stations one click at a time, Enter finishes the line. The Lines panel on the right has, for each line, buttons to buy a bus or a truck (they appear at the first depot with room, so build a depot first) or to delete the line and sell its fleet. Buses carry passengers, trucks carry goods.

## M0 status

| Exit criterion | State |
|---|---|
| 1. Flat terrain, build 2-lane and 2x2 roads with junctions | Done: crossings, T junctions and shared nodes, in the running game |
| 2. 1000 ambient vehicles at 60 fps at 1x; at least 10 fps at 8x | Met on the development machine (Apple M4): 60 fps at 1x, 65 fps at 8x with the simulation at full 8x; one step costs about 0.5 ms |
| 3. Visible overtaking with occasional failures becoming accidents | Done: roughly one head-on in 70 overtakes |
| 4. An accident blocks the road, a crew clears it, traffic reorganizes | Done: wrecks block their lane, crews come from the nearest free depot, drivers avoid blocked roads |
| 5. Headless deterministic test | Done: same seed, same hash, and a command log that replays a whole run with building, crashes and crews (`TrafficTests`, `DeterminismTests`) |

M1 (terrain, water, bridges and tunnels, road editing) and M2 (company, stations, lines, bus and truck, money) are done and described in `docs/project-status.md`.

Towns (M3a): the Towns map has 2 to 10 towns (a slider in the new-game screen) of buildings along their streets; a station serves the buildings in its radius, a road built through buildings demolishes them for a price (shown in the preview) and costs the town satisfaction, and buildings cut off from every road are abandoned after 3 months and demolished a month later. The Towns window lists them. Industries (M3b): forests, quarries and farms produce wood, stone and grain; sawmills, cement works and food plants turn them into planks, building materials and food; a factory makes goods from planks. A station only accepts what the buildings and industries in its radius use; a truck takes the cargo waiting at a stop that the next stop accepts, so a line from a forest to a sawmill carries wood by itself. Towns use food, goods and planks every month and keep building materials and planks for growing; a town whose needs go unmet loses satisfaction. The Industries and Stations windows show stocks and what each station accepts. Trip groups (M3c): the people of each home travel in groups on a daily schedule (to work in the morning, home in the evening, out for leisure at night), on foot when the place is close, otherwise by ambient car or by the company's buses, whichever costs less in time and money; at a station they queue for the station they are going to, and a bus takes only those whose stop is on its line and pays by the distance each rides. The Towns window shows yesterday's trips by mode and the Stations window who is waiting for where. Growth (M3d): once a month a town that is satisfied and connected (60% of its homes can reach work or leisure) spends the building materials and planks delivered to its buildings: a building with a full stock goes up a size, one already biggest founds a new building on a free lot, and a town with no free lot builds a new block of streets. The Towns window shows connectivity and why a town is not growing. This closes M3 (`docs/m3-plan.md`).

Known gaps: no tier 1 and 2 ambient traffic; the few cars that are not trip cars are a random walk over the network; incident causes are limited to overtaking, rear-ending and meeting inside a junction; vehicles are boxes.
