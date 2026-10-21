# M6e: roads stay where clicked, access connections are automatic

Status: built 2026-10-21 (slices A, B and C; see `docs/project-status.md` 5.48). Agreed with the developer in two grilling rounds; every recommendation was accepted.

## Context

Today a road end within 60 m of an Access point (an Industry or a road Gateway) is moved onto it (`JoinRoad` and `NearestAccess`, `sim/TransportMadness.Sim/Simulation.Access.cs:91-127`), and a road that passes gets a spur of its own type. The developer wants the opposite. The road stays exactly where it is clicked. Each Access point in range gets its own separate stone-path connection to the nearest point of the road, made automatically. The build preview shows those connections, the terrain change and bridges or tunnels. Whiteboxes of the buildings follow as a separate slice.

This is the first part of the tool usability review the developer asked for after M6d; art and audio (M7) come after it.

## Decisions

- A road end never moves to an Access point. Snapping stays only to nodes and roads of the player's own network, within 20 m (`NODE_SNAP_RADIUS`, `game/build_tool.gd:10`).
- Connection range stays 60 m (`data/tuning.json:408`, `access.snapRadius`).
- New road type `Path`: half-width 2.5 m, speed limit 8, cost 15 per metre, the same terrain and structure rules as other roads, used by every vehicle, not offered in the build bar, demolishable like any road. The numbers live in `tuning.json`.
- The connection is made by the same command as the road, is a real piece of the network, and its cost (plus any demolition of buildings in its way) is in the preview price. The command is refused if the total cannot be paid.
- When the nearest point of the road is within 10 m (`MinRoadLength`) of a node or an end, the connection joins that node. In the middle of a road, the road is split there (existing `SplitRoad`). When the Access point already stands on a node, nothing new is made.
- An Access point already connected to the network by an older Path gets no second connection, and nothing is replaced automatically; the player demolishes the old one to change it.
- A connection that cannot be built (wide water, grade over `MaxGrade`, unaffordable) does not stop the road. The preview shows that connection in red with the reason, instead of dropping it silently as today.
- Preview of terrain: a coloured strip along the road and its connections, green for fill, orange for cut, with the highest value in metres; bridges and tunnels in their own colours with the span in metres. The terrain on screen is not deformed. The sim exposes one new read-only API (the road height profile and the structure runs, today private in `RoadNetwork.Plan` and `Profile`), used by both the preview and the cost so they cannot diverge.
- Whiteboxes of houses, industries, depots and stations are a separate slice after the connection rules.

## Slices

Each is built, tested, committed and pushed separately, as in M6.

### A. Sim: the Path type and the connections, no road moves

- `RoadNetwork.cs`: add `RoadType.Path`; extend `HalfWidth` (`:569`), the lane count in `AddRoad` (`:1285`), `NewCarriageway`, `CanUpgrade` and `CanDowngrade`. `Tuning.cs:195-218` (`RoadTuning.Of`) and the road block of `tuning.json` (`:107-125`) get the `path` entry.
- `Simulation.Access.cs`: remove end snapping (`NearestAccess`, the `tries` list, `AccessRef? Start` and `End` in `RoadJoin`). Rework `SpursOf` (`:131-154`): the connection type is always Path; it joins the nearest node when within 10 m of one; unbuildable connections are reported with a reason; one connection per Access point unless it has none yet (`AccessNodeOf`, `:71`).
- `BuildRoadWithAccess` (`:161-169`) keeps `reserve = SpurMoney`; the connection is built with Path.
- Other places that switch on the road type: `Simulation.Growth.cs:233`, `SimHost.cs:262` (`RoadTypeOf`), `SimHostOverlays.cs:313`, `DemoWorld.cs`, `SaveGame.cs` (types are checked on load; saves of other versions are already refused).
- Tests in `sim/TransportMadness.Sim.Tests/AccessPointTests.cs`: rewrite the seven that assert end snapping (`ARoadEndingWithinReach_IsDrawnToTheAccessPoint`, `ARoadEndingBeyondReach_IsLeftAlone`, `AnEndThatStandsOnANode_IsNotMoved`, `WhenBothEndsAreNearTheSamePoint_OnlyTheNearerIsDrawnToIt`, `AnEndIsNotMoved_WhenTheRoadWouldNoLongerBeBuildable`, `ThePreview_IsWhatTheCommandBuilds`, `ARoadGateway_IsJoinedLikeAnIndustry`) to say: the end never moves, the connection is Path, a connection near a road end joins the end node, no second connection. Adapt the spur tests (about nine; `TheSpurHasTheTypeAndSpeedOfTheRoad` becomes a Path test). Check `WorkerTests.cs:21-39`, `Worlds.cs:143-150`, `CampaignBots.cs:305-385` and `DefenceTests.cs:188, 247`.
- Run the whole suite and then the Balance category once (`BALANCE_BOTS=1`), since road costs change. Save-load-save byte-identity and the long-war `StateHash` test must still pass.

### B. Game: the preview

- `game/build_tool.gd`: stop replacing `origin` and `target` with the joined ends (`:366-369`, `:221-227`); the next road continues from the clicked end; draw connections with the Path look (`_draw_arc`, `:551-569`); red style with the reason for connections that cannot be built; extend `_join_text` (`:614`).
- `game/SimHost.cs`: `RoadJoin` (`:232`) and `EstimateCurveCost` (`:209`) return the status of each connection and the cut, fill and structure data from the new sim API; the strips are drawn as flat ribbons a little above the ground with new materials.
- Check in the running .NET editor through the bridge: a road passing an industry, a road ending near it, a bridge over water, a deep cut. Say what was not played in 3D.

### C. Whiteboxes (phase 2)

- Move footprint, height and colour into data where they are still in code (depot 22x6x22 at `SimHost.cs:1257-1264`, station 14x4x10 at `:1269-1297`, gateway colour at `:1301`).
- Draw the Access point (a marker, and its connection) on industries; boxes get door and yard marks so art can later replace the scene and keep footprint and access data.

## Documentation to update when building

- `CONTEXT.md`: rewrite **Access point** (no end is moved; an automatic Path connection within 60 m) and add **Path**.
- `docs/project-status.md`, as for earlier milestones.
- An ADR for the Path type only if the choice turns out hard to reverse; likely not needed.

## Verification

- `dotnet test` on `sim/` (path and roll-forward environment in the project's test notes), the full suite, then the Balance category once.
- Slices B and C: run the .NET editor through the godot-ai bridge, place roads near an industry and a gateway, take screenshots of the preview and the result.
