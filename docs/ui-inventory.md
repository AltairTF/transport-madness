# UI inventory

What each screen of the game shows, where its data comes from, and what the player can do from it. Written for the panel redesign (the artifact canvas "Transport Madness: panel redesign"). It records the state of the code on 2026-10-23; the field names are the keys the scripts read, so a redesign can map each one to a visible label.

Terms follow `/CONTEXT.md`. "Host" is `SimHost` (the C# bridge, `game/SimHost*.cs`); "sim" is the pure C# core it reads from.

## How the data flows

- `game/main.gd` calls the host getters and passes the results to `hud.gd` (`show_*` functions). Each getter returns Godot dictionaries and arrays built from the sim.
- `hud.gd` forwards each list to its window page: `_pages[key]` for the 14 list windows, `show_company` to the ones that show company numbers, and `open_object` for the object windows.
- Every action goes back the other way as a signal (`hud.*.emit`) that `main.gd` connects to a host call (`Sim.Execute` through a command, or a `Set*`/`Request*` method). Results come back as a line on the message line (`show_message`).
- Refresh: the lists are rebuilt from the host on the main refresh in `main.gd`; the window pages keep the last list they got, which `picking.gd` and the object windows read.

## 1. Menus (full screen)

### 1.1 Main menu
- File: `game/main_menu.gd` (`_build_main_page`).
- Buttons: Continue, New game, Load game, Settings, Credits, Quit.
- Data: the newest save file (its name for "Continue"); "No save yet." when there is none.
- Actions: opens the new game page, the load list (`save_menu.gd`), settings, credits; Quit exits.

### 1.2 New game
- File: `game/main_menu.gd` (`_build_new_game_page`, `_build_land_box`).
- Choices: map (Towns, Demo, Hills, Empty), size (Small, Normal, Large), land sliders (mountains, water, forests), industries amount, towns count, traffic frequency, seed.
- Data: the preview is `WorldBuilder.Preview` (same seed and settings, a 128 x 128 grid) coloured by `game/MapPreview.cs`; the towns slider tops out at 4 on Small, 10 otherwise.
- Actions: Start builds the world with the chosen settings; Back returns to the main menu.

### 1.3 Pause menu
- File: `game/pause_menu.gd`. Opened with Esc (no tool in hand, no window open) or the Menu button.
- Buttons: Resume, Save game, Load game, Settings, Main menu, Quit to desktop. Main menu and Quit ask first when there is progress since the last save.
- Data: none from the sim; it pauses the scene tree.

### 1.4 Save and load
- File: `game/save_menu.gd`. Used from the pause menu and the main menu.
- Shows: a list of saves (name, date, size) with Save or Load per row; a name field when saving.
- Data: save files in `user://` via `game/SimHostSaves.cs` (`TakeSaveError` reports a failed write).

### 1.5 Settings
- File: `game/settings_menu.gd` (tabs: Display, Audio, Controls, Gameplay). Stored in `game/settings.gd` (autoload `Settings`, `user://settings.cfg`).
- Display: window mode, window size (32 sizes filtered to the screen), interface scale (50 to 200 %, on top of the display scale), vsync, anti-aliasing.
- Audio: volumes (the audio is still postponed, so these are placeholders).
- Controls: every key of `game/keys.gd`, rebindable; listens for the next key press.
- Gameplay: placeholders.

### 1.6 Credits
- File: `game/credits_menu.gd`. Lists the rows of `CREDITS.md` (asset packs, licences).

### 1.7 Campaign end
- File: `game/hud.gd` (`_build_campaign_end`, `_show_campaign_end`). Shown when the campaign is over or the committee is dismissed.
- Shows: title ("Dismissed" or the campaign outcome), the score out of 100, standing, deliveries.
- Data: `GetCompany()` (`campaign_over`, `dismissed`, score, standing).
- Actions: Main menu.

## 2. HUD, always on in game

All in `game/hud.gd` unless stated.

### 2.1 Top strip
- Shows: date and time, pause and speed buttons (the current speed highlighted), money, standing with a trend arrow, "N months to dismissal" when it applies, Menu.
- Data: `GetCompany()` (`money`, `standing`, `months_to_dismissal`, `dismissed`, `campaign`).
- Actions: speed (`speed_selected`), pause, Menu.

### 2.2 Raid banner
- Shows: a banner under the top strip while bomber flights are on their way (target, time to arrival).
- Data: `GetRaidWarnings()`.

### 2.3 News feed
- Shows: a list on the right with the latest news and maintenance events; folds away with a button. Click a row to go to its place.
- Data: `TakeEvents()` (maintenance and incidents), news of new models (`GetNewModels(year)`), and the raid and call-up news.
- The feed is kept across a save (`feed_snapshot`, `restore_feed`).

### 2.4 Message line
- Shows: one line of feedback after an action ("Maintenance policy set", "Can't repair it: ...", "Order accepted: the clock is running").
- Data: the return value of the host call behind the action.

### 2.5 Map legend
- Shows: the legend of the current map view (bottom left): title and colour keys.
- Data: `GetOverlayLegend()`; the view is chosen from the Map group or with V.

### 2.6 Bottom bar
- Shows: the pointer, the build groups (Roads, Modify, Rail, Transport, Defence), and the information groups (Network, Region, Company, Government, Map). Each group opens a row of buttons above the bar. Buttons with an icon from `data/icons.json` (`game/ui/icons.gd`).
- Window buttons show a count when there is something to answer: Orders shows the open offers.
- Data: the static lists in `hud.gd` (`INFO_GROUPS` and the tool groups); the Map group is `GetOverlayNames()`.

### 2.7 Build tools (world space, no panel)
- File: `game/build_tool.gd`. Tools: road build (curve, one-drag, parallel), road upgrade, downgrade, remove, speed limit faster and slower, Move (junction), Resurface, Station (with radius circle and reached streets), New line, depot, bays, rail track, rail signals (block, path, remove), level crossing, traffic light, road signs, anti-aircraft battery.
- Preview shows: length, grade, cut, fill, cost; green or red. Station preview shows the circle, the streets it reaches and the buildings it covers with counts.
- Data: `EstimateRoadCost`, the planners in the host (`PlanStation`, `PlanStationReach`, `PlanDepot`, the pads), `SnapToNode`.
- Keys: Esc stops; Shift forces the 15 degree step; Alt (hold) or G places a building exactly under the mouse; R and Shift+R turn a depot or battery by 15 degrees; PageUp and PageDown raise it 0.5 m.

### 2.8 Debug panel
- Shows: fps, vehicles, overtaking, mean speed, sim speed and step time, incidents (open and total), wrecks; map buttons (Demo, Towns, Hills, Empty) and Crash.
- Data: `GetStats()`.
- Opened with the debug key (shown in its title).

### 2.9 Performance overlay
- File: `game/perf_overlay.gd`. A panel with the switches for shadows, ambient occlusion, fog and grading, and a log of each toggle. Data: render and engine numbers, not the sim.

## 3. Object windows (click on the world)

- Picking: `game/picking.gd`. A left click with no tool in hand opens the nearest object within its radius. Kinds in order: vehicle (10 m), station (22 m), depot (26 m), industry (40 m), gateway (40 m), town (260 m).
- Window: `hud.open_object` makes a window with a title, a text block, a "Go to" button, and kind-specific extras. Gateways open the Gateways list window.
- Data: the last list sent for that kind (`hud.latest(kind)`), so the object window shows what the list shows.
- Each object window keeps its numbers up to date while open (`_refresh_object`).

## 4. List windows (14)

Each is one page script in `game/ui/` (base `page.gd`, shared pieces `ui_kit.gd`). The window frame and dragging are in `window_manager.gd`.

### 4.1 Network

**Lines** (`lines_window.gd`)
- Shows: each line with its colour, name (editable), Delete, vehicles count, delivered, income, upkeep, profit; the maintenance policy of the line; the route as numbered stops, each with Load, a cargo choice, Unload, Share load (percent); Add stop (choose a station); buy buttons for a vehicle or train of the line's kind; a "no vehicles yet" hint.
- Data: `GetLines()` (`id`, `name`, `color`, `stops`, `stop_names`, `stop_options`, `vehicles`, `income`, `upkeep`, `profit`, `delivered`, `policy`, `rail`), `GetStopPlaces()`, `GetVehicleModels()`, `GetTrainOptions()`, `GetCargoNames()`.
- Actions: `line_action` (delete, buy, buy train), `line_edit` (stops), `line_rename`, `line_color`, `line_stop_options`, `policy_set`.

**Vehicles** (`vehicles_window.gd`)
- Shows: each vehicle: model, line, state, cargo and load, condition bar, repair (kind and price, or "repair at next stop"), policy, income, upkeep, profit, days since the last repair; the parked vehicles of each depot (assign to a line, repair, sell); garage buy buttons per depot.
- Data: `GetVehicles()` (`id`, `model`, `line_name`, `state`, `condition`, `policy_text`, `income`, `upkeep`, `profit`, `repair_phase`, `repair_price`, `can_repair`, `x`, `z`), `GetDepots()`, `GetParked()`, `GetLines()`, `GetVehicleModels()`, `GetTrainOptions()`.
- Actions: `vehicle_action` (To depot, Sell), `repair_requested`, `parked_repair`, `garage_buy`, `garage_assign`, `garage_sell`, `policy_set`, `town_focus` (Go to).

**Depots** (`depots_window.gd`)
- Shows: each depot (road or rail): name, Go to, level name, bays and working bays, jobs, shut days; the upgrade buttons with price and level name; a note that repairs wait inside.
- Data: `GetDepots()` (`id`, `rail`, `level`, `level_name`, `bays`, `working`, `jobs`, `upgrades` (`name`, `price`, `upgrade_key`), `shut_days`, `x`, `z`).
- Actions: `depot_upgrade`, `town_focus`.

**Stations** (`stations_window.gd`)
- Shows: each station: name (editable), line count, bays and the price to add bays, Close; Covers (homes, shops, workplaces, industries, the buildings reached along the streets); Accepts (cargo list); Activity today and yesterday (boarded, got off, gave up waiting); the number waiting now; Go to.
- Data: `GetStations()` (`id`, `name`, `lines`, `homes`, `shops`, `workplaces`, `industries`, `reached`, `reached_share`, `accepts`, `boarded`, `alighted`, `gave_up`, `waiting`, `bays`, `bay_price`, `shut_days`, `x`, `z`).
- Actions: `station_action` (rename, bays, close).

### 4.2 Region

**Towns** (`towns_window.gd`)
- Shows: each town: population, jobs, homes, shops and commerce, industries; last month's needs met and unmet; homes reached; satisfaction bar (with its own explanation); trips yesterday by mode (on foot, by bus, by car, could not go); the company's stations in it; the "Show connectivity on the map" toggle.
- Data: `GetTowns()` (`id`, `name`, `population`, `homes`, `jobs`, `buildings`, `commerce`, `industry`, `needs`, `satisfaction`, `walked`, `rode`, `drove`, `stayed`, `unmet`, `connectivity`, `casualties`, `abandoned`, `x`, `z`), plus the growth numbers (`grew_buildings`, `grew_blocks`, `grew_sizes`).
- Actions: `connectivity_view_toggled`, `town_focus`.

**Industries** (`industries_window.gd`)
- Shows: each industry: name and status (idle, waiting for a cargo, producing, shut), inputs and output with stock, staffing (share of jobs filled), output this month against the maximum, supply and demand; for a power plant the MW given and coal burned; for an anti-aircraft battery the range and ammunition left; a shut note from raids.
- Data: `GetIndustries()` (`id`, `name`, `status`, `inputs`, `output`, `stock`, `staff`, `staffing`, `jobs`, `max_output`, `produced`, `supply`, `demand`, `fire_seconds`, `range`, `x`, `z`).
- Actions: `town_focus`.

**Gateways** (`gateways_window.gd`)
- Shows: each gateway (road and rail): its kind and name; a "not connected" warning when no station reaches it, with the advice to build a station beside it; Available imports (each cargo with the units per order, price per order, the amount here and arrived, an Order button with the price); Not yet available (cargo the government sends from a later month); Exported so far.
- Data: `GetGateways()` (`id`, `name`, `kind`, `covered`, `status`, `stock`, `offers` (`name`, `units`, `price`, `can`, `why`), `exports`, `imported`, `shut_days`, `x`, `z`).
- Actions: `import_requested` (gateway, cargo). Import failure text comes from `WhyNotImport`.

**Power** (`power_window.gd`)
- Shows: the grid's demand and supply, the plants (burning or not), the power bought from outside by the month (a stepper, with the price and the maximum), the free grid size.
- Data: `GetPower()` (`demand`, `supply`, `plants`, `plants_burning`, `free_grid`, `purchased`, `max_purchase`, `price`, `cost`, `factor`, `step`).
- Actions: `power_purchase_set` (megawatts).

### 4.3 Company

**Finances** (`finances_window.gd`)
- Shows: money; the loan (taken, maximum) and the advance from the ministry; the month and phase; income, upkeep, interest and the allocation by phase (country, region, imports); the next allocation.
- Data: `GetCompany()` (`money`, `income`, `upkeep`, `interest`, `loan`, `max_loan`, `allocations`, `next_allocation`, `phase`, `phase_month`, `imports`, `region`, `country`).
- Actions: `action_requested` ("loan", "repay"); the loan itself is a fixed 100k step.

**Maintenance** (`maintenance_window.gd`)
- Shows: the company policy (mode, level, days, the condition below which to repair); the frequency of random events (an option); totals of repairs and fines; the repairs with who is waiting; the vehicles from the worst condition up; worn roads with a Resurface button and the cost; the compensation and the injured.
- Data: `GetMaintenance()` (`company`, `frequency`, `repairs`, `fines`, `compensation`, `injured`, `critical`), `GetVehicles()` (`condition`, `repair_phase`), `GetWornRoads(6)` (`condition`, `resurface`, `length`, `price`).
- Actions: `policy_set` (scope company), `frequency_selected`, `resurface_requested`, `town_focus`.

**Requisition** (`requisition_window.gd`)
- Shows: the next requisition month and the expected share; this wave's rule choice (freight first and the biggest, oldest first, passengers first) enabled only in the month of notice; the income from the last requisition and the vehicles taken.
- Data: `GetCompany()` (`next_requisition_month`, `next_requisition_share`, `expected_requisition`, `requisition_rule`, `can_choose_rule`, `requisition_income`, `requisitioned`).
- Actions: `requisition_rule_set`.

### 4.4 Government

**Orders** (`orders_window.gd`)
- Shows: open offers (title, units, reward, standing, days left, place, Accept or Decline, and why an offer cannot be accepted yet); accepted orders with their progress, days left and state (met, late, failed); the paid total.
- Data: `GetOrders()` (`id`, `title`, `units`, `reward`, `standing`, `days_left`, `progress`, `state`, `status`, `can`, `why`, `place`).
- Actions: `order_answered` (accept, decline). Failure text from `WhyNotAccept`.

**Standing** (`standing_window.gd`)
- Shows: the standing now and before the last month, the trend, the dismissal threshold and the months left; the terms that made it up (exports, deliveries, orders, workforce, casualties, bombs, fines); the next call-up; the workforce gap; the allocation factor.
- Data: `GetCompany()` (`standing`, `standing_before`, `standing_after`, `export_term`, `deficit_term`, `orders_met`, `orders_late`, `orders_failed`, `workforce_gap`, `workforce_factor`, `allocation_factor`, `dismiss_below`, `months_to_dismissal`, `next_call_month`, `called_up`, `raids`, `bombs`, `casualties`).

## 5. Object windows, field summary

The object windows reuse the list data above for one object:

| Kind | Fields shown | Source list |
|---|---|---|
| Vehicle | model, line, state, load, condition, policy, income, upkeep | Vehicles |
| Station | name, lines, covers, accepts, activity | Stations |
| Depot | level, bays, jobs, upgrades | Depots |
| Industry | status, inputs, output, staffing | Industries |
| Town | population, jobs, satisfaction, trips | Towns |
| Gateway | status, offers | Gateways (opens its list window) |

## 6. Observations for the redesign

- The same object appears in a list and in an object window; actions are repeated (buying a vehicle is in Lines and in Vehicles; the policy block is in Lines, Vehicles and Maintenance).
- Each window mixes reading and acting. Read-only summaries (Towns, Industries, Standing) and action panels (Lines, Gateways) have different needs.
- Several texts are long sentences in the scripts (the satisfaction bar, the coverage notes, the power and raid notes). They sit in code, not in a string table.
- The menus use their own panel code, not `ui_kit.gd` used by the windows; a redesign can use one set of components for both.
- Not seen on screen here: the look of the open tool and window rows, the tooltips, and the feed and banners during a raid. Those were not checked by hand (project-status 5.69 and 5.70 say the same).
