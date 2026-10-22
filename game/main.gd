extends Node3D
## Wires the world together: environment, HUD, input. Command line (after `--`):
##   --bench                      measure frame rate with 1000 vehicles at 1x and 8x, then quit
##   --shot=<file> [--shot-after=<seconds>]   save a screenshot and quit
##   --selftest-build             build roads with simulated mouse clicks, run, print stats, quit
##   --hills                      start on an empty generated-hills map instead of the default map
##   --towns[=<n>]                start on the generated map with n towns (default 5)
##   --selftest-hills             build roads over hills, print grade and cost, quit
##   --selftest-crash             crash a car, follow the crew until the road is clear, print the timeline, quit
##   --selftest-place             right click, drag, parallel roads, a turned and raised depot and the camera following, with simulated input on the hills map, print, quit
##   --selftest-lines             build a road, two stations, a bus line and a truck line, run 30 days, print the books, quit
##   --distance=<m> --pitch=<deg> --focus=<x>,<z>   place the camera (useful with --shot)

const VEHICLES := 1000

@onready var host: Node3D = $SimHost
@onready var rig: Node3D = $CameraRig

var hud: CanvasLayer
var build_tool: Node
var pause_menu: CanvasLayer
var _elapsed := 0.0
var _stats_timer := 0.0
var _args := {}
var _bench: Array = []
var _bench_index := 0
var _bench_phase_time := 0.0
var _frame_ms: Array = []
var _world_note := "" ## said once the HUD exists, e.g. that fewer towns fit than were asked for
var _slots: RefCounted ## the save files (SaveSlots.cs)
var _saved_mark := [-1, -1] ## the tick and the command count at the last save or load: progress since is unsaved
var _last_month := -1 ## for the autosave at the start of each month
var _loaded := false ## the game came from a save
var picking: Node ## what a click with no tool opens (picking.gd)
const Keys := preload("res://game/keys.gd")


func _ready() -> void:
	for arg in OS.get_cmdline_user_args():
		var parts: PackedStringArray = arg.split("=", true, 1)
		_args[parts[0]] = parts[1] if parts.size() > 1 else true

	_build_environment()
	rig.terrain_host = host
	if _args.has("--distance"):
		rig.distance = float(_args["--distance"])
	if _args.has("--pitch"):
		rig.pitch = float(_args["--pitch"])
	if _args.has("--focus"):
		var focus: PackedStringArray = _args["--focus"].split(",")
		rig.position = Vector3(float(focus[0]), 0.0, float(focus[1]))
	rig._apply()
	_slots = load("res://game/SaveSlots.cs").new()
	if Session.load_path != "":
		var path := Session.load_path
		Session.load_path = ""
		var why: String = host.LoadGameFrom(path)
		if why != "":
			Session.menu_message = why
			get_tree().change_scene_to_file.call_deferred("res://game/main_menu.tscn")
			return
		_loaded = true
	var map: String = Session.map
	if _args.has("--hills") or _args.has("--selftest-hills") or _args.has("--selftest-place"):
		map = "hills"
	if _args.has("--towns"):
		map = "towns"
		if _args["--towns"] is String:
			Session.towns = int(_args["--towns"])
	if not _loaded:
		_new_world(map)
	if _args.has("--focus"):
		rig.focus_on(rig.position.x, rig.position.z) # now that the terrain exists, stand on it

	hud = load("res://game/hud.gd").new()
	add_child(hud)
	hud.speed_selected.connect(func(speed: float) -> void: host.SetSpeed(speed))
	hud.set_models(host.GetVehicleModels())
	hud.set_train_options(host.GetTrainOptions())
	hud.set_cargo_names(host.GetCargoNames())
	hud.line_action.connect(_on_line_action)
	hud.line_edit.connect(_on_line_edit)
	hud.line_rename.connect(_on_line_rename)
	hud.line_stop_options.connect(func(line: int, index: int, load: bool, cargo: int, unload: bool, share: int) -> void: host.SetStopOptions(line, index, load, cargo, unload, share))
	hud.line_color.connect(func(line: int, color: Color) -> void: host.SetLineColor(line, color))
	hud.station_action.connect(_on_station_action)
	hud.vehicle_action.connect(_on_vehicle_action)
	hud.garage_buy.connect(_on_garage_buy)
	hud.garage_assign.connect(_on_garage_assign)
	hud.policy_set.connect(func(scope: int, target: int, mode: int, days: int, below: int, kind: int) -> void:
		hud.show_message("Maintenance policy set" if host.SetPolicy(scope, target, mode, days, below, kind) else "That policy can't be set")
	)
	hud.repair_requested.connect(func(vehicle: int, kind: int) -> void:
		var why: String = host.RequestRepair(vehicle, kind)
		hud.show_message("It will be repaired at its next stop" if why == "" else "Can't ask for that repair: " + why)
	)
	hud.parked_repair.connect(func(parked: int, kind: int) -> void:
		var why: String = host.RepairParked(parked, kind)
		hud.show_message("Taken into a bay" if why == "" else "Can't repair it: " + why)
	)
	hud.depot_upgrade.connect(func(rail: bool, depot: int, level: int) -> void:
		var why: String = host.UpgradeDepot(rail, depot, level)
		hud.show_message("Depot upgraded" if why == "" else "Can't upgrade it: " + why)
	)
	hud.frequency_selected.connect(func(frequency: int) -> void: host.SetFrequency(frequency))
	hud.resurface_requested.connect(func(road: int) -> void:
		var why: String = host.Resurface(road)
		hud.show_message("Resurfacing started: the road is closed until the crew is done" if why == "" else "Can't resurface it: " + why)
	)
	hud.garage_sell.connect(func(parked: int) -> void: hud.show_message("Sold" if host.SellParked(parked) else "That can't be sold now"))
	hud.import_requested.connect(func(gateway: int, cargo: String) -> void:
		if host.RequestImport(gateway, cargo):
			hud.show_message("Shipment asked for")
		else:
			hud.show_message(host.WhyNotImport(gateway, cargo)))
	hud.order_answered.connect(func(order: int, accept: bool) -> void:
		if not accept:
			host.DeclineOrder(order)
			hud.show_message("Order declined")
		elif host.AcceptOrder(order):
			hud.show_message("Order accepted: the clock is running")
		else:
			hud.show_message(host.WhyNotAccept(order)))
	hud.power_purchase_set.connect(func(megawatts: int) -> void:
		hud.show_message("Power bought from outside: %d MW a month" % megawatts if host.SetPowerPurchase(megawatts) else "That can't be set"))
	hud.requisition_rule_set.connect(func(rule: int) -> void:
		hud.show_message("The army will take: %s" % ["freight first, the biggest first", "the oldest first", "passengers first"][rule] if host.SetRequisitionRule(rule) else "That can't be set now"))
	hud.campaign_left.connect(_leave_to_menu)
	hud.town_focus.connect(rig.focus_on)
	hud.connectivity_view_toggled.connect(func(on: bool) -> void:
		host.SetConnectivityView(on)
		hud.show_overlay(host.Overlay, host.GetOverlayLegend()))
	hud.set_overlay_names(Array(host.GetOverlayNames()))
	hud.overlay_selected.connect(func(index: int) -> void:
		host.SetOverlay(index)
		hud.show_overlay(host.Overlay, host.GetOverlayLegend()))
	if _world_note != "":
		hud.show_message(_world_note)

	build_tool = load("res://game/build_tool.gd").new()
	add_child(build_tool)
	build_tool.setup(host, rig.get_node("Camera3D"))
	build_tool.message.connect(hud.show_message)
	if _args.has("--selftest-build"):
		build_tool.message.connect(func(text: String) -> void: print("SELFTEST message: ", text))
	hud.tool_selected.connect(build_tool.set_tool)
	picking = load("res://game/picking.gd").new()
	add_child(picking)
	picking.setup(host, rig.get_node("Camera3D"), hud, build_tool, rig)
	picking.picked.connect(hud.open_object)
	build_tool.tool_changed.connect(hud.select_tool)
	hud.action_requested.connect(_on_action)

	pause_menu = load("res://game/pause_menu.gd").new()
	add_child(pause_menu)
	pause_menu.main_menu_requested.connect(_leave_to_menu)
	pause_menu.quit_requested.connect(_quit)
	pause_menu.save_requested.connect(func(save_name: String) -> void:
		await _save(_slots.SlotPath(save_name), save_name, true)
		pause_menu.close())
	pause_menu.load_requested.connect(_load_save)
	pause_menu.unsaved = _has_unsaved_progress
	pause_menu.name_for_save = _default_save_name
	hud.menu_requested.connect(pause_menu.open)
	get_tree().auto_accept_quit = false # closing the window asks first when there is progress to lose
	if _loaded:
		_restore_host_state(host.LoadedHostBlob)
		host.SetSpeed(0.0)
		hud.highlight_speed(0.0)
		hud.show_message("Game loaded. It is paused: pick a speed to go on.")
	_mark_saved()
	_last_month = host.MonthIndex

	if _args.has("--selftest-build"):
		_selftest_build()
	if _args.has("--selftest-crash"):
		_selftest_crash()
	if _args.has("--selftest-hills"):
		_selftest_hills()
	if _args.has("--selftest-lines"):
		_selftest_lines()
	if _args.has("--selftest-place"):
		_selftest_place()

	if _args.has("--bench"):
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
		_bench = [{"speed": 1.0, "warmup": 3.0, "measure": 8.0}, {"speed": 8.0, "warmup": 3.0, "measure": 8.0}]
		_start_bench_phase()


var _campaign_over_seen := false
var _model_year := 0


func _process(delta: float) -> void:
	if hud == null:
		return # a save that could not be loaded: on the way back to the menu
	_elapsed += delta
	_stats_timer += delta
	if _stats_timer > 0.25:
		_stats_timer = 0.0
		_autosave_if_new_month()
		var save_error: String = host.TakeSaveError()
		if save_error != "":
			hud.show_message(save_error)
		hud.show_stats(host.GetStats())
		var company: Dictionary = host.GetCompany()
		hud.show_company(company)
		if company["year"] != _model_year:
			# A new year: the models on sale change, and the news says which are new.
			var first: bool = _model_year == 0
			_model_year = company["year"]
			hud.set_models(host.GetVehicleModels())
			hud.set_train_options(host.GetTrainOptions())
			var news: Array = host.GetNewModels(_model_year)
			if not first and not news.is_empty():
				hud.show_message("New on sale in %d: %s" % [_model_year, ", ".join(news)])
		if company["campaign_over"] and not _campaign_over_seen:
			_campaign_over_seen = true
			host.SetSpeed(0.0)
			hud.highlight_speed(0.0)
		elif not company["campaign_over"]:
			_campaign_over_seen = false
		hud.show_places(host.GetStopPlaces())
		hud.show_lines(host.GetLines())
		hud.show_towns(host.GetTowns())
		hud.show_industries(host.GetIndustries())
		hud.show_power(host.GetPower())
		hud.show_gateways(host.GetGateways())
		hud.show_orders(host.GetOrders(), company["orders_open"])
		hud.show_stations(host.GetStations())
		var vehicles: Array = host.GetVehicles()
		hud.show_vehicles(vehicles)
		var depots: Array = host.GetDepots()
		hud.show_garage(depots, host.GetParked(), host.GetLines())
		hud.show_depots(depots)
		hud.show_maintenance(host.GetMaintenance(), vehicles)
		hud.show_worn_roads(host.GetWornRoads(6))
		hud.show_events(host.TakeEvents())
		hud.show_raid_warnings(host.GetRaidWarnings())
		if host.Overlay != 0:
			hud.show_overlay(host.Overlay, host.GetOverlayLegend())
		for warning in host.TakeRailWarnings():
			hud.show_message(warning)

	if _args.has("--shot") and not _args.has("--selftest-build") and not _args.has("--selftest-crash") and _elapsed > float(_args.get("--shot-after", 6.0)):
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
		get_tree().quit()

	if _bench_index < _bench.size():
		_run_bench(delta)


## Esc with no tool in hand opens the pause menu. (A tool takes Esc itself, and marks it handled.)
func _unhandled_input(event: InputEvent) -> void:
	if pause_menu == null:
		return
	if event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_ESCAPE:
			# Esc closes the topmost window first; with none open it is the menu. (A tool in hand takes Esc before this.)
			get_viewport().set_input_as_handled()
			if not hud.close_top_window():
				pause_menu.open()
		elif Keys.pressed(event, "quick_save"):
			get_viewport().set_input_as_handled()
			_save(_slots.QuickPath(), "Quick save", false)
		elif Keys.pressed(event, "quick_load"):
			get_viewport().set_input_as_handled()
			if not _slots.Exists(_slots.QuickPath()):
				hud.show_message("No quick save yet: %s makes one" % Keys.text("quick_save", Settings.key_bindings))
			elif _has_unsaved_progress():
				pause_menu.open()
				pause_menu._ask("Load the quick save? Progress since your last save will be lost.", _load_save.bind(_slots.QuickPath()))
			else:
				_load_save(_slots.QuickPath())


func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_CLOSE_REQUEST and pause_menu != null:
		pause_menu.ask_quit()


func _leave_to_menu() -> void:
	pause_menu.close()
	host.FinishSaving()
	get_tree().auto_accept_quit = true
	get_tree().change_scene_to_file("res://game/main_menu.tscn")


func _quit() -> void:
	host.FinishSaving()
	get_tree().quit()


# --- save and load (M6a) ---

func _has_unsaved_progress() -> bool:
	return _saved_mark != [host.CurrentTick, host.CommandCount]


func _mark_saved() -> void:
	_saved_mark = [host.CurrentTick, host.CommandCount]


func _default_save_name() -> String:
	var company: Dictionary = host.GetCompany()
	const MONTHS := ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"]
	return "%s %d" % [MONTHS[clampi(int(company["month"]) - 1, 0, 11)], int(company["year"])]


## Saves the game: the screenshot first (without the menus when <hide_ui>), then the snapshot; the file is written in the background.
func _save(path: String, save_name: String, hide_ui: bool) -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("user://saves"))
	if hide_ui:
		pause_menu.visible = false
		hud.visible = false
		await get_tree().process_frame
		await get_tree().process_frame
	var image := get_viewport().get_texture().get_image()
	if hide_ui:
		hud.visible = true
		pause_menu.visible = true
	if image != null and not image.is_empty():
		image.resize(320, maxi(1, roundi(320.0 * image.get_height() / image.get_width())), Image.INTERPOLATE_BILINEAR)
		image.save_png(ProjectSettings.globalize_path(_slots.ShotPath(path)))
	var why: String = host.SaveGameTo(path, save_name, var_to_bytes(_host_state()))
	if why != "":
		hud.show_message(why)
		return
	_mark_saved()
	hud.show_message("Saved: %s" % save_name)


## At the start of each month, one of the three autosaves (the oldest) is written over.
func _autosave_if_new_month() -> void:
	var month: int = host.MonthIndex
	if month == _last_month:
		return
	_last_month = month
	if not _args.is_empty() or host.GetCompany()["campaign_over"]:
		return
	_save(_slots.NextAutosavePath(), "Autosave", false)


func _load_save(path: String) -> void:
	host.FinishSaving()
	pause_menu.close()
	Session.load_path = path
	get_tree().change_scene_to_file("res://game/main.tscn")


## What the game layer keeps in a save besides the simulation: the camera and the news feed.
func _host_state() -> Dictionary:
	return {
		"camera": {"x": rig.position.x, "y": rig.position.y, "z": rig.position.z, "distance": rig.distance, "yaw": rig.yaw, "pitch": rig.pitch},
		"feed": hud.feed_snapshot(),
	}


func _restore_host_state(blob: PackedByteArray) -> void:
	if blob.is_empty():
		return
	var state = bytes_to_var(blob)
	if not state is Dictionary:
		return
	var camera: Dictionary = state.get("camera", {})
	if not camera.is_empty():
		rig.position = Vector3(camera["x"], camera["y"], camera["z"])
		rig.distance = camera["distance"]
		rig.yaw = camera["yaw"]
		rig.pitch = camera["pitch"]
		rig._apply()
	hud.restore_feed(state.get("feed", []))


## Replaces the world: "towns" (hills with towns), "demo" (street grid with traffic), "hills" or "empty".
func _new_world(map: String) -> void:
	host.StartFrequency = Session.frequency
	match map:
		"towns":
			var placed: int = host.NewTownsWorld(Session.world_seed, Session.towns, Session.industries)
			_world_note = "" if placed >= Session.towns else "Only %d of %d towns fit on this map" % [placed, Session.towns]
			var unlinked: int = host.UnlinkedIndustries
			if unlinked > 0:
				_world_note += ("" if _world_note == "" else ". ") + "%d industries are out of reach of their suppliers, a town or the other deposits on this map" % unlinked
			if host.GatewaysPlaced < 2:
				_world_note += ("" if _world_note == "" else ". ") + "Only %d of the 2 gateways fit on this map" % host.GatewaysPlaced
			if hud != null and _world_note != "":
				hud.show_message(_world_note)
			var towns: Array = host.GetTowns()
			if not towns.is_empty() and not _args.has("--focus"):
				rig.focus_on(towns[0]["x"], towns[0]["z"])
		"hills":
			host.NewHillsWorld(Session.world_seed)
		"empty":
			host.NewEmptyWorld(Session.world_seed)
		_:
			host.NewDemoWorld(Session.world_seed, VEHICLES)


func _on_action(action: String) -> void:
	match action:
		"traffic":
			hud.show_message("Added %d cars" % host.SpawnTraffic(100))
		"crash":
			var victim: int = host.CrashSomeone()
			hud.show_message("Vehicle %d crashed" % victim if victim >= 0 else "No moving car to crash")
		"loan":
			hud.show_message("Advance of 100,000 granted" if host.TakeLoan(100000) else "The ministry will not advance more")
		"repay":
			hud.show_message("Repaid 100,000" if host.RepayLoan(100000) else "Nothing to repay, or not enough money")
		"empty", "hills", "demo", "towns":
			_new_world(action)


## Buys a vehicle for a line, or deletes the line (selling its vehicles): the buttons of the Lines panel.
func _on_line_action(kind: String, line: int, model: String) -> void:
	match kind:
		"buy":
			var why: String = host.BuyForLine(false, model, line)
			hud.show_message("Bought a %s for line %d" % [model, line + 1] if why == "" else "Can't buy that: " + why)
		"buy_train":
			var train_why: String = host.BuyForLine(true, model, line)
			hud.show_message("Bought a train for line %d" % (line + 1) if train_why == "" else "Can't buy that: " + train_why)
		"delete":
			hud.show_message("Line deleted, its vehicles sold" if host.DeleteLine(line) else "No such line")


## The Lines window changed a line's stops: the new list of station ids.
func _on_line_edit(line: int, stops: Array) -> void:
	var ids := PackedInt32Array()
	for stop in stops:
		var id: int = host.StopFromCode(int(stop)) # a place offered as a stop (an industry) gets its stop now
		if id < 0:
			hud.show_message("No road reaches that place yet")
			return
		ids.append(id)
	if not host.EditLine(line, ids):
		hud.show_message("Can't change the line that way: it needs at least two stops, and the same station can't follow itself")


func _on_line_rename(line: int, new_name: String) -> void:
	host.RenameLine(line, new_name)


## A button or the name box of a row in the Stations window.
func _on_station_action(kind: String, station: int, text: String) -> void:
	match kind:
		"rename":
			if not host.RenameStation(station, text):
				hud.show_message("A station's name is one to 24 characters")
		"close":
			hud.show_message("Station closed" if host.CloseStation(station) else "That station is already closed")
		"bays":
			hud.show_message("Bays built" if host.AddStationBays(station) else "Can't build bays: not enough money, or the station has them already")


## A button of a vehicle row in the Vehicles window: sell it, or send it to a depot.
func _on_vehicle_action(kind: String, vehicle: int) -> void:
	match kind:
		"follow":
			rig.follow(_vehicle_spot.bind(vehicle))
			hud.show_message("Following: move the camera to let go")
		"sell":
			# Trains share the window with road vehicles under negative ids.
			var sold: bool = host.SellTrain(-1 - vehicle) if vehicle < 0 else host.SellVehicle(vehicle)
			hud.show_message("Sold" if sold else "That can't be sold now")
		"depot":
			var why: String = host.SendToDepot(vehicle)
			hud.show_message("On its way to a depot" if why == "" else "Can't send it to a depot: " + why)


## Where a vehicle is now for the camera that follows it (trains share the list under negative ids), null when it is gone.
func _vehicle_spot(vehicle: int) -> Variant:
	if vehicle >= 0:
		var spot: Dictionary = host.VehicleSpot(vehicle)
		return Vector3(spot["x"], spot["y"], spot["z"]) if spot["found"] else null
	for item in hud.latest("vehicle"):
		if item["id"] == vehicle:
			return Vector3(item["x"], item["y"], item["z"])
	return null


## A vehicle bought for a depot's garage, from the Vehicles window.
func _on_garage_buy(rail: bool, depot: int, option: String) -> void:
	var id: int = host.BuyParked(rail, depot, option)
	hud.show_message("Bought, waiting in the depot: put it on a line when you are ready" if id >= 0 else "Can't buy that: not enough money")


## A parked vehicle put on a line, from the Vehicles window.
func _on_garage_assign(parked: int, line: int) -> void:
	var why: String = host.AssignParked(parked, line)
	hud.show_message("Out of the depot and on line %d" % (line + 1) if why == "" else "Can't put it on that line: " + why)


## Drives the real tool with synthetic mouse events: two chained roads plus an avenue, a depot, then traffic.
func _selftest_build() -> void:
	host.NewEmptyWorld(Session.world_seed)
	rig.distance = float(_args.get("--distance", 400.0))
	rig.pitch = -89.0
	rig.position = Vector3.ZERO
	rig._apply()
	await get_tree().process_frame
	await get_tree().process_frame
	var camera: Camera3D = rig.get_node("Camera3D")

	# World points on the ground, converted to the screen positions a player would click.
	var clicks := [
		["road2", [Vector3(-150, 0, 0), Vector3(0, 0, 0), Vector3(0, 0, 100)]],
		["road4", [Vector3(0, 0, 0), Vector3(150, 0, 0)]],
		["road2", [Vector3(0, 0, 0), Vector3(-5, 0, -110)]],
		["road2", [Vector3(-80, 0, 60), Vector3(-80, 0, -60)]], # crosses the first road: a junction forms
		["road2", [Vector3(75, 0, 0), Vector3(75, 0, 90)]],     # starts on the middle of the avenue: a T junction
		["curve2", [Vector3(150, 0, 0), Vector3(195, 0, 45), Vector3(150, 0, 110)]],       # a bend off the avenue's end
		["curve4", [Vector3(-150, 0, 0), Vector3(-195, 0, 45), Vector3(-150, 0, 110)]],  # an avenue bend off the west road
		["curve2", [Vector3(180, 0, 100), Vector3(60, 0, 60), Vector3(0, 0, 100)]],        # arc that meets the first bend and ends on the south road
		["upgrade", [Vector3(-115, 0, 0)]],                                                 # widen the west road to an avenue
		["remove", [Vector3(75, 0, 45)]],                                                   # tear down the T road off the avenue
		["downgrade", [Vector3(30, 0, 0)]],                                                 # narrow the first half of the east avenue
		["faster", [Vector3(-115, 0, 0)]],                                                  # raise the limit on the west avenue
		["slower", [Vector3(-115, 0, 0), Vector3(-115, 0, 0)]],                             # ... and lower it twice
		["move", [Vector3(0, 0, 0), Vector3(10, 0, 10)]],                                   # drag the hub a little south-east
		["depot", [Vector3(0, 0, 0)]],
	]
	for step in clicks:
		hud.select_tool(step[0])
		build_tool.set_tool(step[0])
		for point in step[1]:
			await _click_world(camera, point)
		build_tool.set_tool("pointer")
	var stats: Dictionary = host.GetStats()
	print("SELFTEST roads=%d depots=%d" % [host.RoadCount(), host.DepotCount()])
	print("SELFTEST hud sweep check %.3f" % host.SweepThrough(150, 0, 195, 45, 150, 110))
	host.SpawnTraffic(60)
	host.SetSpeed(8.0)
	await get_tree().create_timer(10.0).timeout
	stats = host.GetStats()
	print("SELFTEST vehicles=%d mean_speed=%.1f tick=%d incidents=%d" % [stats["vehicles"], stats["mean_speed"], stats["tick"], stats["incidents"]])
	if _args.has("--shot"):
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
	get_tree().quit()


## Builds a road, a depot, two stations, a bus line and a truck line through the host API, runs them for 30 days and prints the books.
func _selftest_lines() -> void:
	host.NewEmptyWorld(Session.world_seed)
	await get_tree().process_frame
	print("SELFTEST lines: road built=%s depot=%s" % [host.BuildRoad(0, 0, 1200, 0, 0), host.BuildDepot(0, 0, 0, false, NAN, 0.0)])
	var a: int = host.BuildStation(150, 0, false)
	var b: int = host.BuildStation(1050, 0, true)
	var bus_line: int = host.CreateLine(PackedInt32Array([a, b]))
	var truck_line: int = host.CreateLine(PackedInt32Array([b, a]))
	print("SELFTEST lines: stations %d %d, lines %d %d" % [a, b, bus_line, truck_line])
	print("SELFTEST lines: bus %d" % host.BuyVehicle("bus", bus_line))
	host.StepFor(20.0)
	print("SELFTEST lines: truck %d" % host.BuyVehicle("truck", truck_line))
	host.StepFor(30.0 * 60.0)
	var company: Dictionary = host.GetCompany()
	print("SELFTEST lines: day %d money %d loan %d income %d upkeep %d" % [company["day"], company["money"], company["loan"], company["income"], company["upkeep"]])
	for line in host.GetLines():
		print("SELFTEST lines: line %d vehicles %d delivered %d income %d upkeep %d profit %d" % [line["id"], line["vehicles"], line["delivered"], line["income"], line["upkeep"], line["profit"]])
	hud.show_company(company)
	hud.show_lines(host.GetLines())
	await get_tree().process_frame
	if _args.has("--shot"):
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
	get_tree().quit()


## Builds a few roads over generated hills through the host API and reports what they cost.
func _selftest_hills() -> void:
	await get_tree().process_frame
	var attempts := [[-400, -300, 0, -300], [0, -300, 300, 0], [-400, 200, 100, 200], [-400, -300, -400, 200]]
	var curves := [[-300, -100, 0, -100, 0.9], [-300, 100, -100, 250, -0.7]]
	for c in curves:
		var cost: Dictionary = host.EstimateCurveCost(c[0], c[1], c[2], c[3], 0, c[4])
		if not cost["valid"]:
			print("HILLS curve %s refused" % [c])
			continue
		print("HILLS curve %s built=%s length=%.0f cut=%.0f fill=%.0f cost=%.0f" % [c, host.BuildCurve(c[0], c[1], c[2], c[3], 0, c[4]), cost["length"], cost["cut"], cost["fill"], cost["money"]])
	for a in attempts:
		var cost: Dictionary = host.EstimateRoadCost(a[0], a[1], a[2], a[3], 0)
		if not cost["valid"]:
			print("HILLS %s refused" % [a])
			continue
		var built: bool = host.BuildRoad(a[0], a[1], a[2], a[3], 0)
		print("HILLS %s built=%s length=%.0f cut=%.0f fill=%.0f cost=%.0f" % [a, built, cost["length"], cost["cut"], cost["fill"], cost["money"]])
	await _selftest_structures()
	host.SpawnTraffic(0)
	await get_tree().create_timer(1.0).timeout
	if _args.has("--shot"):
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
	get_tree().quit()


## Looks over the generated map for the 300 m road that needs the longest bridge and the one that needs the longest
## tunnel, builds them, and points the camera at the bridge.
func _selftest_structures() -> void:
	var best := {"bridge": [-1.0, 0, 0, {}], "tunnel": [-1.0, 0, 0, {}]}
	for z in range(-1900, 1700, 100):
		for x in range(-1900, 1500, 100):
			var cost: Dictionary = host.EstimateRoadCost(x, z, x + 300, z, 0)
			if not cost["valid"]:
				continue
			for kind in ["bridge", "tunnel"]:
				if cost[kind] > best[kind][0]:
					best[kind] = [cost[kind], x, z, cost]
	for kind in ["bridge", "tunnel"]:
		var f: Array = best[kind]
		if f[0] <= 0.0:
			print("HILLS no %s found" % kind)
			continue
		var built: bool = host.BuildRoad(f[1], f[2], f[1] + 300, f[2], 0)
		print("HILLS %s road at (%d, %d): built=%s bridge=%.0f m tunnel=%.0f m cost=%.0f" % [kind, f[1], f[2], built, f[3]["bridge"], f[3]["tunnel"], f[3]["money"]])
	if best["bridge"][0] > 0.0 and not _args.has("--focus"):
		rig.focus_on(best["bridge"][1] + 150.0, best["bridge"][2])
		rig.distance = 220.0
		rig.pitch = -40.0
		rig._apply()
	await get_tree().process_frame


## Crashes a car in the demo world and follows the incident from wreck to cleared road at 8x.
func _selftest_crash() -> void:
	await get_tree().create_timer(2.0).timeout
	host.SetSpeed(8.0)
	var victim: int = host.CrashSomeone()
	print("CRASH victim=%d" % victim)
	var last_state := ""
	var shot_taken := false
	var waited := 0.0
	while waited < 120.0:
		await get_tree().create_timer(0.25).timeout
		waited += 0.25
		var state: String = host.FirstOpenIncidentState()
		if state != last_state:
			var stats: Dictionary = host.GetStats()
			print("CRASH state=%s tick=%d incidents=%d open=%d" % [state, stats["tick"], stats["incidents"], stats["open_incidents"]])
			last_state = state
		if state == "Clearing" and not shot_taken and _args.has("--shot"):
			var at: Vector3 = host.FirstOpenIncidentPosition()
			rig.position = at
			rig.distance = 60.0
			rig.pitch = -60.0
			rig._apply()
			await get_tree().process_frame
			await get_tree().process_frame
			get_viewport().get_texture().get_image().save_png(_args["--shot"])
			shot_taken = true
		if state == "none":
			break
	print("CRASH done cleared=%s" % (last_state == "none"))
	get_tree().quit()


## Simulated input for --selftest-place: a mouse button pressed or released at a world point, and a key.
func _mouse_at(camera: Camera3D, point: Vector3, button: int, pressed: bool) -> void:
	var screen: Vector2 = get_viewport().get_final_transform() * camera.unproject_position(point)
	var motion := InputEventMouseMotion.new()
	motion.position = screen
	Input.parse_input_event(motion)
	await get_tree().process_frame
	var event := InputEventMouseButton.new()
	event.position = screen
	event.button_index = button
	event.pressed = pressed
	Input.parse_input_event(event)
	await get_tree().process_frame


func _key(keycode: int, shift := false) -> void:
	var event := InputEventKey.new()
	event.keycode = keycode
	event.shift_pressed = shift
	event.pressed = true
	Input.parse_input_event(event)
	await get_tree().process_frame


func _selftest_place() -> void:
	rig.distance = 500.0
	rig.pitch = -89.0
	rig.position = Vector3.ZERO
	rig._apply()
	await get_tree().process_frame
	await get_tree().process_frame
	var camera: Camera3D = rig.get_node("Camera3D")
	var say := func(text: String) -> void: print("PLACE ", text)
	build_tool.message.connect(func(text: String) -> void: print("PLACE message: ", text))

	# Right click: cancels the draft, then leaves the tool.
	build_tool.set_tool("road2")
	await _mouse_at(camera, Vector3(-200, 0, -100), MOUSE_BUTTON_LEFT, true)
	say.call("start set: %s" % (build_tool._start != null))
	await _mouse_at(camera, Vector3(-150, 0, -100), MOUSE_BUTTON_RIGHT, true)
	await _mouse_at(camera, Vector3(-150, 0, -100), MOUSE_BUTTON_RIGHT, false)
	say.call("after one right click: start=%s tool=%s" % [build_tool._start, build_tool.tool])
	await _mouse_at(camera, Vector3(-150, 0, -100), MOUSE_BUTTON_RIGHT, true)
	await _mouse_at(camera, Vector3(-150, 0, -100), MOUSE_BUTTON_RIGHT, false)
	say.call("after two: tool=%s" % build_tool.tool)

	# One drag: press at the start, move, let go.
	build_tool.set_tool("road2")
	var roads: int = host.RoadCount()
	await _mouse_at(camera, Vector3(-200, 0, -100), MOUSE_BUTTON_LEFT, true)
	await _mouse_at(camera, Vector3(-100, 0, -100), MOUSE_BUTTON_LEFT, false)
	say.call("drag built %d road(s)" % (host.RoadCount() - roads))
	build_tool.set_tool("pointer")

	# A click and a click still work, and no road is built by a short click-release.
	build_tool.set_tool("road2")
	roads = host.RoadCount()
	await _mouse_at(camera, Vector3(-200, 0, 0), MOUSE_BUTTON_LEFT, true)
	await _mouse_at(camera, Vector3(-200, 0, 0), MOUSE_BUTTON_LEFT, false)
	say.call("click-release built %d" % (host.RoadCount() - roads))
	await _mouse_at(camera, Vector3(-100, 0, 0), MOUSE_BUTTON_LEFT, true)
	await _mouse_at(camera, Vector3(-100, 0, 0), MOUSE_BUTTON_LEFT, false)
	say.call("click-click built %d" % (host.RoadCount() - roads))
	build_tool.set_tool("pointer")

	# Parallel road: P once, two clicks build two roads.
	build_tool.set_tool("road2")
	await _key(KEY_P)
	roads = host.RoadCount()
	await _mouse_at(camera, Vector3(0, 0, 100), MOUSE_BUTTON_LEFT, true)
	await _mouse_at(camera, Vector3(100, 0, 100), MOUSE_BUTTON_LEFT, true)
	say.call("parallel built %d roads (side %d)" % [host.RoadCount() - roads, build_tool._parallel])
	build_tool.set_tool("pointer")

	# A depot turned and raised, placed exactly where put.
	build_tool.set_tool("depot")
	var spot := Vector3(200, 0, 150)
	var depots: int = host.DepotCount()
	var motion := InputEventMouseMotion.new()
	motion.position = get_viewport().get_final_transform() * camera.unproject_position(spot)
	Input.parse_input_event(motion)
	await get_tree().process_frame
	await _key(KEY_R)
	await _key(KEY_R)
	await _key(KEY_PAGEUP)
	await _key(KEY_PAGEUP)
	say.call("turned=%s angle=%.1f deg raise=%.1f" % [build_tool._turned, rad_to_deg(build_tool._angle), build_tool._raise])
	await _mouse_at(camera, spot, MOUSE_BUTTON_LEFT, true)
	say.call("depots built %d" % (host.DepotCount() - depots))
	for depot in host.GetDepots():
		say.call("depot %s at %.0f, %.0f" % [depot["name"], depot["x"], depot["z"]])
	build_tool.set_tool("pointer")

	# A station on the dragged road and a look from above, for --shot.
	say.call("station %d" % host.BuildStation(-150, -100, false))
	if _args.has("--shot"):
		rig.stop_following()
		rig.position = Vector3(-140, host.HeightAt(-140, -90), -90)
		rig.distance = 140.0
		rig.pitch = -50.0
		rig._apply()
		for i in 6:
			await get_tree().process_frame
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
		rig.position = Vector3(190, host.HeightAt(190, 140), 140)
		rig.distance = 120.0
		rig._apply()
		for i in 6:
			await get_tree().process_frame
		get_viewport().get_texture().get_image().save_png(_args["--shot"].replace(".png", "-depot.png"))

	# The camera follows a point until it is moved by hand.
	var target := [Vector3(300, 0, 300)]
	rig.follow(func() -> Variant: return target[0])
	await get_tree().create_timer(1.5).timeout
	say.call("camera at %.0f, %.0f following (%s)" % [rig.position.x, rig.position.z, rig._following.is_valid()])
	rig.stop_following()
	get_tree().quit()


func _click_world(camera: Camera3D, point: Vector3) -> void:
	# Input events carry window coordinates; unproject_position gives viewport coordinates (the stretch mode differs).
	var screen: Vector2 = get_viewport().get_final_transform() * camera.unproject_position(point)
	var motion := InputEventMouseMotion.new()
	motion.position = screen
	Input.parse_input_event(motion)
	await get_tree().process_frame
	var press := InputEventMouseButton.new()
	press.position = screen
	press.button_index = MOUSE_BUTTON_LEFT
	press.pressed = true
	Input.parse_input_event(press)
	await get_tree().process_frame


func _start_bench_phase() -> void:
	var phase: Dictionary = _bench[_bench_index]
	host.SetSpeed(phase["speed"])
	hud.highlight_speed(phase["speed"])
	_bench_phase_time = 0.0
	_frame_ms.clear()


func _run_bench(delta: float) -> void:
	var phase: Dictionary = _bench[_bench_index]
	_bench_phase_time += delta
	if _bench_phase_time > phase["warmup"]:
		_frame_ms.append(delta * 1000.0)
	if _bench_phase_time < phase["warmup"] + phase["measure"]:
		return

	_frame_ms.sort()
	var total := 0.0
	for ms in _frame_ms:
		total += ms
	var stats: Dictionary = host.GetStats()
	var worst_1pct: float = _frame_ms[int(_frame_ms.size() * 0.99)]
	print("BENCH speed=%dx vehicles=%d avg_fps=%.1f worst1pct_fps=%.1f max_frame_ms=%.1f sim_speed=x%.2f step_ms=%.2f incidents=%d" % [
		phase["speed"], stats["vehicles"], 1000.0 * _frame_ms.size() / total, 1000.0 / worst_1pct,
		_frame_ms[_frame_ms.size() - 1], stats["effective_speed"], stats["step_ms"], stats["incidents"]])
	_bench_index += 1
	if _bench_index < _bench.size():
		_start_bench_phase()
	else:
		get_tree().quit()


func _build_environment() -> void:
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.6, 0.75, 0.9)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.75, 0.78, 0.85)
	environment.ambient_light_energy = 0.7
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	add_child(world_environment)

	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-55, -35, 0)
	sun.light_energy = 1.1
	add_child(sun)
