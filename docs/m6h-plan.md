# M6h: interface scale, coverage along the streets, and a ghost road preview

Status: raised by the developer on 2026-10-22 while playing M6g; written down, not designed and not started. Each item below says what was seen, what is wanted, what the code does today, and what has to be decided with the developer before building (the habit of M6e to M6g: a short round of questions, then a plan with slices, then build). M7 (art and audio) still follows.

## Done already (from the same session)

- **Depot snap.** Holding Alt did not seem to turn the snap off, and turning a depot with R or Shift+R turned it off for good until the tool was left. Now turning a depot never changes the snap: a depot turned with the snap on stays where the snap puts it and only faces the way it was turned (its door is joined to the road by a Path, as a free depot's is); only the hold key (Alt) or the switch key (G, for when the hold key does not reach the game, as can happen in the editor's embedded window) places a building exactly under the mouse; the preview follows the key at once, not only when the mouse moves; the turn is forgotten once the depot is built. `PlanDepot` takes an angle without `free`. Tests in `PadTests`; project-status 5.52.

## 1. Interface scale

**Seen.** In the editor's game window (4471 x 2515) the HUD is far too big. The Settings window offers window sizes, and the developer expected 1920 x 1080 to be what the game is made for.

**Today.** `project.godot` has a 1920 x 1080 base viewport with `canvas_items` stretch and `expand` aspect, so the whole canvas is scaled by window height / 1080 (about 2.3 at 2515 px) and `Settings.ui_scale` (75, 100, 125, 150 %) is multiplied on top through `content_scale_factor`. A bigger window therefore only makes the interface bigger, not roomier. The window-size list is for a free-standing window; it does nothing in fullscreen or in the editor's embedded game.

**Wanted.** An interface scale the player controls, and a default that is sensible on any screen, so a big window shows more of the map instead of bigger buttons.

**To decide.** Whether the HUD stays at a fixed size in pixels (stretch off, scale from the display's own scale and the player's choice) or follows the window height but with a smaller reference than the current ratio; the range of the choice (50 to 200 %?), its steps and whether there is an automatic setting; whether a reference of 1920 x 1080 should be what 100 % means; whether the world labels (town and industry names) and the pause menu scale with it; how the saved window rectangles (`Settings.window_rects`) behave when the scale changes.

**Where.** `game/settings.gd` (`apply`, `UI_SCALES`), `game/settings_menu.gd`, `project.godot` (stretch), `game/ui/window_manager.gd` (window rectangles), `game/hud.gd`.

## 2. A preview of what a building will cover

**Seen.** A station is built blind: the only way to see what it covers is the Coverage map view afterwards (rings of 150 m, buildings in green and red).

**Wanted.** While placing anything that has coverage, show it, so the best place can be chosen: for a station (and a station with bays, and a rail station) the area and the buildings and industries that would be covered, green for covered and red for not, with the count in the status line as now; for the anti-aircraft battery the range circle it already draws; for a stop at an industry the industry it covers. Moving the mouse updates it; it uses the same rule as the real coverage (item 3 changes the rule, and the preview must follow it).

**To decide.** What else counts as coverage to be previewed (a depot's reach for its crews? a power plant's supply?); whether the preview also dims what is already covered by another station (so the gain is visible); whether the Coverage map view and the preview are one drawing.

**Where.** `game/build_tool.gd` (`_hover_station`, `_hover_battery`), `StationQuote` and `Simulation.QuoteStation` for the numbers, `game/SimHostOverlays.cs` for how the Coverage view colours buildings (`RefreshCovered`), reuse of its colours.

## 3. Coverage along the streets, with a falloff

**Seen** (the Cities: Skylines picture the developer shared): the service reaches along the streets and fades with distance, instead of a fixed circle. A house outside the circle of a bus stop but a reasonable walk away along a road can still use it, with a lower chance. More places are reached for a smaller share of use.

**Today.** A station covers every active building within `stations.coverageRadius` (150 m) in a straight line (`Simulation.BuildingsNear`, `CoveredBy`, `QuoteStation`); it is the same number as `trips.walkRadius` (150 m) for walking trips. Coverage is yes or no and feeds the trips (who can take the line), the needs, the connectivity view and the Coverage view.

**Wanted.** Coverage is a number from 0 to 1 for each building (and industry) for each station, worked out along the road network: full near the stop and falling to nothing at a maximum walking distance, spreading along streets and around corners, so a street that leads away from the stop is covered further than the ground behind a block. The chance that a person at that building chooses the station follows that number. The Coverage map view and the build preview colour the streets (and the buildings) by it, as in the reference: a bright street near the stop that fades toward its ends.

**To decide.**
- The distance: walking along the streets from the stop's node to the building's access node plus the straight walk from the road to the door, or the straight line as now with a falloff only; how far a building off the streets counts.
- The curve: full coverage out to what distance, then linear or smooth to zero at what maximum (the 150 m of today as the "full" part and about 300 m as the end?).
- How the number enters the trip choice (`Simulation.Trips.cs`, the mode choice): as a weight on the choice of public transport, as a chance of the group using the stop at all, or as extra walking time; how two stations in reach combine (the best, or a sum capped at 1); what it does to the people who walk (`walkRadius`).
- Cost: a search over the road graph from each station, redone when the network or the stations change (the `_coverageDirty` flag exists); a cap on the work per step.
- What it does to the balance (more covered people at the same price) and to the golden hashes (the coverage lists are in the state), and to the save format.
- Rail stations, which stand off the road network (a Path or a road must reach them?).

**Where.** `Simulation.Stations.cs`, `Simulation.Towns.cs` (`CoveredBy`, `RefreshCoverage`, `_stationBuildings`), `Simulation.Trips.cs`, `Simulation.Rail.cs`, `Simulation.LinePreview.cs` (what a stop carries), `game/SimHostOverlays.cs`, `data/tuning.json` (`stations`, `trips`), `CONTEXT.md` (Coverage is a defined term), tests in `StationTests`, `TownTests`, `TripTests` and the balance bots.

## 4. A ghost road for the build preview

**Built 2026-10-22** (project-status 5.54): the ghost reuses the planned stretches with its own translucent drawing rather than the real road mesh code; paths to access points and a depot's path keep their strips, and a rail track has a ghost with two rails. What follows is the original request.

**Seen.** While drawing a road the preview is a thin pale green box that is almost invisible on the grass (the picture of the avenue tool).

**Wanted.** The preview is the road itself in a ghost mode: the same width, the lane markings, the median of an avenue, the junction discs at its ends and where it meets a road, the bridge and tunnel stretches, the Paths to access points, the earthworks as now, drawn translucent and tinted green when it can be built and red when not. The same for tracks and for the parallel road or track beside it.

**Today.** `BuildTool._update_preview` draws a flat `BoxMesh` for a straight road (colour `ROAD_COLORS`, alpha 0.7, 0.4 m thick) or a ribbon for a curve (`_draw_arc`) and the planned stretches (`_draw_runs`); the real road is built by `SimHost.RebuildRoads` (`AddRoadRibbon`, bridges, tunnels, node discs) from the network.

**To decide.** Whether the ghost reuses the real road's mesh code on the planned runs (`RoadRuns`, which already gives the surface, structures and earthworks) with a ghost material, or is a lighter version; how it is lit (unshaded with a clear outline so it reads on any ground and from far away); whether a ghost junction shows where the road would split an existing road.

**Where.** `game/build_tool.gd`, `game/SimHost.cs` (`RebuildRoads`, `AddRoadRibbon`, `RoadRuns`), a ghost material next to `_roadMaterial`.

## Suggested order

1. Ghost road (item 4): contained in the game layer and makes every later check easier to see.
2. Interface scale (item 1): game layer only.
3. Coverage along the streets (item 3): the sim; the largest and the one that moves the balance. Design with a short round of questions first.
4. Coverage preview (item 2), built on the rule of item 3 so it is not done twice.

Each is checked in the real game through the self-tests (`--selftest-place` style, with and without `--shot`) and the sim through `dotnet test`; say what was not played by hand.
