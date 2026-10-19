# Transport Madness

3D transport management game, see `docs/game-plan.md` for the plan, `docs/project-status.md` for what exists, how it works and what comes next, `CONTEXT.md` for vocabulary and `docs/adr/` for decisions.

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
godot --path . -- --hills                              # empty generated-hills map with lakes and rivers: roads show grade, cut/fill cost, bridges and tunnels
godot --path . -- --selftest-crash                      # crashes a car and follows the rescue crew
godot --path . -- --selftest-lines                      # builds stations and a bus and a truck line, runs 30 days, prints the books
```

Controls: right mouse orbits, middle mouse or WASD pans, wheel zooms. Pick a road tool in the toolbar, click to start and click again to build; ending on or crossing another road makes a junction. **Curve** and **Curved avenue** take three clicks: start, a point on the curve, end. **Upgrade** widens a two-lane road to an avenue, **Downgrade** narrows an avenue, **Faster** and **Slower** change a road's speed limit by 10 km/h, **Remove** tears a road piece down (junction to junction); all show the cost when you hover. **Move** picks up a junction with one click and drops it with the next (its roads are rebuilt with the same bend, may cross other roads, and the vehicles on them carry on). Esc stops.

The company: money, loan and day are at the top, **Borrow 100k** and **Repay 100k** change the loan, and every tool above now costs money (hover shows the price). **Station** builds on the road under the mouse (the road is split there; vehicles stop on the lanes that run into it). **New line** takes stations one click at a time, Enter finishes the line. The Lines panel on the right has, for each line, buttons to buy a bus or a truck (they appear at the first depot with room, so build a depot first) or to delete the line and sell its fleet. Buses carry passengers, trucks carry goods; each station produces both at a fixed rate until towns exist.

## M0 status

| Exit criterion | State |
|---|---|
| 1. Flat terrain, build 2-lane and 2x2 roads with junctions | Done: crossings, T junctions and shared nodes, in the running game |
| 2. 1000 ambient vehicles at 60 fps at 1x; at least 10 fps at 8x | Met on the development machine (Apple M4): 60 fps at 1x, 65 fps at 8x with the simulation at full 8x; one step costs about 0.5 ms |
| 3. Visible overtaking with occasional failures becoming accidents | Done: roughly one head-on in 70 overtakes |
| 4. An accident blocks the road, a crew clears it, traffic reorganizes | Done: wrecks block their lane, crews come from the nearest free depot, drivers avoid blocked roads |
| 5. Headless deterministic test | Done: same seed, same hash, and a command log that replays a whole run with building, crashes and crews (`TrafficTests`, `DeterminismTests`) |

M1 (terrain, water, bridges and tunnels, road editing) and M2 (company, stations, lines, bus and truck, money) are done and described in `docs/project-status.md`.

Known gaps: no towns, industries or cargo chains (M3), so stations produce fixed amounts; no tier 1 and 2 ambient traffic; ambient traffic is a random walk over the network; incident causes are limited to overtaking, rear-ending and meeting inside a junction; vehicles are boxes.
