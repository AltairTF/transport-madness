# M6g: whiteboxes that work the way the final game will, and the rest of the tool review

Status: slices A, B and C built 2026-10-22 (see `docs/project-status.md` 5.51 to 5.53 for what differs from this plan: rail depots and stations have no pad, undo was left out, parallel roads are for straight roads and tracks only). Third part of the tool usability review (M6e: roads stay where put; M6f: depots, stops at industries, line preview). M7 (art and audio) follows. The aim stated by the developer: before the final 3D art there is a whitebox version of everything that works the way the final game will, only without the models.

## Decided with the developer, 2026-10-22

- **Buildings the player places** (depot, rail depot, rail station, anti-aircraft battery) can be **rotated** and **raised or lowered**. The terrain under them is levelled.
- **Terrain model.** A flat pad under the footprint, at the average ground height under it by default; PageUp / PageDown raise and lower it in 0.5 m steps. The ground is carved to the pad with a shoulder, the way roads already carve it (`Terrain.CarvePath`). The cost of the earthwork is in the price and the preview shows cut and fill and the highest of each. The pad is refused above a slope limit (a maximum cut or fill in metres, in tuning).
- **Snap off.** A key (Alt held, or G to switch it on; both rebindable in `game/keys.gd`) turns off the snap to the road: the building stands exactly where it is put, and its access road (the Path of M6e/M6f) is still built to the nearest road within reach. Without the key a building near a road is pulled against it (the "Beside" connection of M6f).
- **Bus stop (road station).** It stands on the road, so its box is replaced by a marker lying on the road: a thin slab on the lane side and a sign post, no blue building beside the road. Size and colour stay in `data/whitebox.json`.
- **Mouse in the build tools.** Left click places. Right click cancels the draft in progress (a road, a line); with no draft it leaves the tool for the pointer. Esc keeps doing the same. Applies to every tool that has a draft, not only roads.
- **Included from the road tool review:** a road in one drag (press, drag, release builds; click-click still works), parallel roads (a second road at a fixed offset beside the one being drawn), and the camera following a vehicle. **Left for later:** undo.

## Slices

### A. Input and the bus stop (game only)
- Right click as above in `build_tool.gd` (`_unhandled_input`), sharing the code of Esc.
- The road station marker in `SimHost` and `data/whitebox.json` (`station`), and its preview in `_hover_station`.
- A hold-to-disable key for the snap in `keys.gd`, read by the tools (used by slice B).

### B. Placement: rotation, height, snap off, terrain
- Sim: `PlanDepot` and the plans of the other buildings take an angle, a height offset and a snap flag; the commands carry them (`BuildDepotCommand`, `BuildRailStationCommand`, `AddRailDepotCommand`, `BuildBatteryCommand`). A footprint pad is levelled by a new `Terrain` helper next to `CarvePath`; its cost and its cut and fill are read by the plan.
- Game: R / Shift+R (or the scroll wheel with a key held) turn in 15 degree steps; PageUp / PageDown height; the preview draws the box turned and raised, the pad as a green and orange fill, and the Path.
- Tests: the same plan gives the same price; the pad levels the sample heights it should and leaves the rest; refused above the slope limit; snap off keeps the position and still gets the Path; a replay from the command log gives the same hash; the golden hashes of worlds with no such building do not move.

### C. Road tool: one drag, parallel roads, follow camera
- One drag in `_click_road` and `_unhandled_input` (button down sets the start, motion previews, button up builds).
- Parallel roads: a key toggles a second preview offset to one side by a set gap (tuning), built by two `BuildCurve` calls in one step; refused as a whole when either part cannot be built.
- Camera follow: click a vehicle in the pointer tool to follow it; any camera move or Esc releases it (`camera_rig.gd`, `picking.gd`).

## Verification
`dotnet test` on `sim/` after every slice that touches it (environment in the project's test notes), the Balance category once at the end, and the running .NET editor through the godot-ai bridge: place a depot rotated and raised on a slope, with and without the snap key, a station on a road, a road drawn in one drag and in parallel, a vehicle followed. Say what was not played.

## Documentation to update when building
`docs/project-status.md` (new sections), `CONTEXT.md` if a term is added (Pad).

Follow-up items raised while playing M6g (interface scale, coverage along the streets, a coverage preview, a ghost road) are in `docs/m6h-plan.md`.
