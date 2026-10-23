extends CanvasLayer
## The frame of the interface (M6d, docs/m6-plan.md): the top strip (date, speed, money, standing, the menu), the bottom bar
## (building groups on the left, information groups on the right; a group opens a row of buttons above the bar), the news feed on
## the right, the messages, the legend of the map view and the floating windows. Each window's content is a page in game/ui/, kept
## and updated by the show_* calls below; main.gd talks to the HUD only through these calls and its signals.

signal speed_selected(speed: float)
signal tool_selected(tool_name: String)
signal action_requested(action: String)
## Buy a vehicle for a line ("buy", with the model's id), or delete the line ("delete").
signal line_action(kind: String, line: int, model: String)
## The stops of a line changed in the Lines window: the full new list of station ids.
signal line_edit(line: int, stops: Array)
signal line_rename(line: int, new_name: String)
## What vehicles do at one stop of a line (M6f): load, which cargo (-1 any), unload, and the unload share in percent (-1 all).
signal line_stop_options(line: int, index: int, load: bool, cargo: int, unload: bool, share: int)
## A colour picked for a line in the Lines window.
signal line_color(line: int, color: Color)
## A station's button or name box: kind is "rename" (with the text), "close" or "bays".
signal station_action(kind: String, station: int, text: String)
## A vehicle's button: kind is "sell" or "depot" (take it off its line to wait in a depot).
signal vehicle_action(kind: String, vehicle: int)
## Buy a vehicle (or a train, by option id) to wait in a depot.
signal garage_buy(rail: bool, depot: int, option: String)
## Put a waiting vehicle on a line.
signal garage_assign(parked: int, line: int)
signal garage_sell(parked: int)
## A maintenance policy set from a block: scope 0 company, 1 line, 2 vehicle; mode 0 inherit, 1 off, 2 on; kind 0 patch, 1 service, 2 overhaul.
signal policy_set(scope: int, target: int, mode: int, days: int, below: int, kind: int)
## Ask for a repair at the next stop of a vehicle on a line (negative ids are trains, as in the Vehicles window).
signal repair_requested(vehicle: int, kind: int)
signal parked_repair(parked: int, kind: int)
## Raise a depot to a level (1 Workshop, 2 Complex).
signal depot_upgrade(rail: bool, depot: int, level: int)
## The random-events option: 0 off, 1 low, 2 normal, 3 high.
signal frequency_selected(frequency: int)
signal resurface_requested(road: int)
signal menu_requested
## The panel at the end of the campaign: back to the main menu.
signal campaign_left
## Ask for a shipment of a cargo (its id) at a gateway.
signal import_requested(gateway: int, cargo: String)
## Accept (true) or decline an order the government offered.
signal order_answered(order: int, accept: bool)
## The megawatts to buy from outside each month.
signal power_purchase_set(megawatts: int)
## The rule for the army's next requisition: 0 freight first, 1 the oldest first, 2 passengers first.
signal requisition_rule_set(rule: int)
signal town_focus(x: float, z: float)
## The Towns window turns the connectivity view of the map on or off.
signal connectivity_view_toggled(on: bool)
## A map view picked from the Map group or with V (0 for none; the order of SimHost.GetOverlayNames).
signal overlay_selected(index: int)

const UiTheme := preload("res://game/ui_theme.gd")
const Kit := preload("res://game/ui/ui_kit.gd")
const Keys := preload("res://game/keys.gd")
const WindowManager := preload("res://game/ui/window_manager.gd")
const IndustriesWindow := preload("res://game/ui/industries_window.gd")
const StationsWindow := preload("res://game/ui/stations_window.gd")
const TownsWindow := preload("res://game/ui/towns_window.gd")
const VehiclesWindow := preload("res://game/ui/vehicles_window.gd")
const DepotsWindow := preload("res://game/ui/depots_window.gd")
const Icons := preload("res://game/ui/icons.gd")

const SPEEDS := [["||", 0.0], ["1x", 1.0], ["2x", 2.0], ["4x", 4.0], ["8x", 8.0]]
const TOOL_CATEGORIES := [
	{"name": "Roads", "tools": [
		["2-lane road", "road2", "Build a road with one lane each way."],
		["2x2 avenue", "road4", "Build a road with two lanes each way."],
		["Curve", "curve2", "Build a curved 2-lane road: start, bend, end."],
		["Curved avenue", "curve4", "Build a curved avenue: start, bend, end."]]},
	{"name": "Modify", "tools": [
		["Upgrade", "upgrade", "Widen a road to an avenue."],
		["Downgrade", "downgrade", "Narrow an avenue to a road."],
		["Faster", "faster", "Raise the speed limit by 10 km/h."],
		["Slower", "slower", "Lower the speed limit by 10 km/h."],
		["Move", "move", "Pick a junction, then click where it goes."],
		["Resurface", "resurface", "Have a crew resurface a worn road: it is closed while they work and costs a price per metre."],
		["Remove", "remove", "Demolish a piece of road."],
		["Traffic light", "light", "Put traffic lights at a junction: the two crossing streets take turns."],
		["Remove light", "light_remove", "Take traffic lights down."],
		["Priority road", "sign_priority", "Priority sign on a road and the one across the junction: the other roads give way to them."],
		["Yield sign", "sign_yield", "The traffic of this road gives way to the roads without a sign."],
		["Stop sign", "sign_stop", "The traffic of this road stops, then gives way to the roads without a sign."],
		["Remove sign", "sign_remove", "Take a sign down."]]},
	{"name": "Rail", "tools": [
		["Track", "track", "Lay a straight track. Click to start, click again to build; it joins tracks it meets, and crosses roads with level crossings."],
		["Curved track", "track_curve", "Lay a curved track: start, a point on the curve, end."],
		["Block signal", "signal_block", "A train waits here unless the whole block beyond is free. R (see Settings > Controls) turns the signal round."],
		["Path signal", "signal_path", "A train waits here unless the path it will take, up to the next signal, is free. Lets trains pass at loops. R turns it round."],
		["Remove signal", "signal_remove", "Take a signal down."],
		["Remove track", "track_remove", "Tear a piece of track down. A train on it is lost."],
		["Rail station", "rail_station", "Build a station on a track."],
		["Rail depot", "rail_depot", "Build a depot at a track junction or end: trains are bought there."]]},
	{"name": "Transport", "tools": [
		["Depot", "depot", "Build a depot at a junction: vehicles are bought and repaired there."],
		["Station", "station", "Build a station on a road."],
		["Station with bays", "station_bays", "Build a station where loading vehicles pull out of the lane, so they do not hold up the road. Costs more to build and keep."],
		["New line", "line", "Click stations in order, then press Enter."]]},
	{"name": "Defence", "tools": [
		["Anti-aircraft", "battery", "Build an anti-aircraft battery on free level ground. It needs people to man it and ammunition from a gateway, and fires at aircraft within its range. From month 20."]]},
]
## The information groups: [name, [[window key, title, page script], ...]]; the Map group holds the map views instead.
const INFO_GROUPS := [
	["Network", [["lines", "Lines", "lines"], ["vehicles", "Vehicles", "vehicles"], ["depots", "Depots", "depots"], ["stations", "Stations", "stations"]]],
	["Region", [["towns", "Towns", "towns"], ["industries", "Industries", "industries"], ["gateways", "Gateways", "gateways"], ["power", "Power", "power"]]],
	["Company", [["finances", "Finances", "finances"], ["maintenance", "Maintenance", "maintenance"], ["requisition", "Requisition", "requisition"]]],
	["Government", [["orders", "Orders", "orders"], ["standing", "Standing", "standing"]]],
	["Map", []],
]
const MESSAGE_SECONDS := 8.0
const FEED_MAX := 6

## Shared with the pages.
var models: Array = []
var train_options: Array = [] ## what can be bought for a rail line: {id, label, price, upkeep, capacity, cargo}
var cargo_names: Array = []
var stations_known: Array = [] ## [{id, name, rail}] of the open stations, for the "add a stop" choices of the lines
var places_known: Array = [] ## [{code, name}] of the industries and road gateways a road reaches that have no stop yet, offered as stops too (M6f)

var _root: Control
var _windows: Control
var _pages: Dictionary = {} ## window key -> page
var _titles: Dictionary = {} ## window key -> title
var _latest: Dictionary = {"vehicles": [], "stations": [], "depots": [], "industries": [], "gateways": [], "towns": []}
var _company: Dictionary = {}

var _date: Label
var _money: Label
var _standing: Label
var _dismissal: Label
var _speed_buttons: Dictionary = {}
var _speed := 1.0
var _speed_before_pause := 1.0
var _stats: Label
var _debug_panel: Control
var _message: Label
var _message_age := 0.0
var _raid_banner: Label
var _feed: VBoxContainer
var _feed_panel: PanelContainer
var _feed_fold: Button
var _feed_items: Array = [] ## the entries of the event feed: {"node": Button, "age": float, "lifetime": float, "event": Dictionary}
var _campaign_end: Control
var _campaign_end_title: Label
var _campaign_end_text: Label

var _group_rows: Dictionary = {} ## group name -> its row of buttons above the bar
var _group_buttons: Dictionary = {} ## group name -> its button in the bar
var _open_group := ""
var _window_buttons: Dictionary = {} ## window key -> its button in a group row
var _tool_buttons: Dictionary = {}
var _pointer_button: Button
var _overlay_names: Array = []
var _overlay_buttons: Array[Button] = []
var _overlay_index := 0
var _legend: PanelContainer
var _legend_title: Label
var _legend_rows: VBoxContainer
var _legend_note: Label


func _ready() -> void:
	_root = Control.new()
	_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_root.theme = UiTheme.build()
	add_child(_root)
	_windows = WindowManager.new()
	_root.add_child(_windows)
	_windows.window_opened.connect(_on_window_changed)
	_windows.window_closed.connect(_on_window_changed)
	_build_pages()
	_build_top_strip()
	_build_raid_banner()
	_build_feed()
	_build_legend()
	_build_bottom()
	_build_debug_panel()
	_build_campaign_end()
	select_tool("pointer")
	highlight_speed(1.0)


## Pages that were never opened are not in the tree: free them with the HUD.
func _exit_tree() -> void:
	for key in _pages:
		if is_instance_valid(_pages[key]) and not _pages[key].is_inside_tree():
			_pages[key].free()


func _process(delta: float) -> void:
	if _message.visible:
		_message_age += delta
		if _message_age > MESSAGE_SECONDS:
			_message.visible = false
	for i in range(_feed_items.size() - 1, -1, -1):
		var item: Dictionary = _feed_items[i]
		item["age"] += delta
		if item["age"] > item["lifetime"]:
			item["node"].queue_free()
			_feed_items.remove_at(i)


## The keys of the HUD, read before the buttons so that Space does not press the button that has the focus; nothing while a name is typed.
func _input(event: InputEvent) -> void:
	if not (event is InputEventKey and event.pressed and not event.echo):
		return
	var focus := get_viewport().gui_get_focus_owner()
	if focus is LineEdit or focus is TextEdit:
		return
	if Keys.pressed(event, "speed_pause"):
		_set_speed(_speed_before_pause if _speed == 0.0 else 0.0)
	elif Keys.pressed(event, "speed_1"):
		_set_speed(1.0)
	elif Keys.pressed(event, "speed_2"):
		_set_speed(2.0)
	elif Keys.pressed(event, "speed_3"):
		_set_speed(4.0)
	elif Keys.pressed(event, "speed_4"):
		_set_speed(8.0)
	elif Keys.pressed(event, "overlay_next") and not _overlay_names.is_empty():
		overlay_selected.emit(posmod(_overlay_index + 1, _overlay_names.size()))
	elif Keys.pressed(event, "overlay_previous") and not _overlay_names.is_empty():
		overlay_selected.emit(posmod(_overlay_index - 1, _overlay_names.size()))
	elif Keys.pressed(event, "debug"):
		_debug_panel.visible = not _debug_panel.visible
	else:
		for i in TOOL_CATEGORIES.size():
			if Keys.pressed(event, "build_%d" % (i + 1)):
				_toggle_group(TOOL_CATEGORIES[i]["name"])
				get_viewport().set_input_as_handled()
				return
		for i in INFO_GROUPS.size():
			if Keys.pressed(event, "info_%d" % (i + 1)):
				_toggle_group(INFO_GROUPS[i][0])
				get_viewport().set_input_as_handled()
				return
		return
	get_viewport().set_input_as_handled()


# --- pages and windows ---

func _build_pages() -> void:
	for group in INFO_GROUPS:
		for entry in group[1]:
			var page: VBoxContainer = load("res://game/ui/%s_window.gd" % entry[2]).new()
			page.setup(self)
			_pages[entry[0]] = page
			_titles[entry[0]] = entry[1]


## Opens a list window, or closes it when it is open.
func toggle_window(key: String) -> void:
	if _pages.has(key):
		_windows.toggle_list(key, _titles[key], _pages[key])


func open_window(key: String) -> void:
	if _pages.has(key):
		_windows.open_list(key, _titles[key], _pages[key])


## Closes the topmost window; false when there was none.
func close_top_window() -> bool:
	return _windows.close_top()


func _on_window_changed(_key: String) -> void:
	for key in _window_buttons:
		_window_buttons[key].set_pressed_no_signal(_windows.is_open(key))


## What the host last sent of a kind of object (for picking and the object windows): "vehicles", "stations", ... or the singular.
func latest(kind: String) -> Array:
	const PLURAL := {"vehicle": "vehicles", "station": "stations", "depot": "depots", "industry": "industries", "gateway": "gateways", "town": "towns"}
	return _latest.get(PLURAL.get(kind, kind), [])


## Opens the window of one thing on the map (picking.gd): a vehicle, station, depot ("d1", "r3"), industry or town; a gateway opens the Gateways list.
func open_object(kind: String, id: Variant) -> void:
	if kind == "gateway":
		open_window("gateways")
		return
	var item := _find(kind, id)
	if item.is_empty():
		return
	var key := "%s:%s" % [kind, str(id)]
	var content := Kit.column()
	content.add_theme_constant_override("separation", Kit.GAP)
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	content.add_child(label)
	var extra := Kit.column()
	content.add_child(extra)
	var buttons := Kit.flow()
	content.add_child(buttons)
	buttons.add_child(Kit.button("Go to", func() -> void:
		var now := _find(kind, id)
		if not now.is_empty():
			town_focus.emit(now["x"], now["z"])))
	var refs := {"label": label, "extra": extra}
	match kind:
		"vehicle":
			var view := VehiclesWindow.make_view()
			extra.add_child(view["box"])
			refs["view"] = view
			label.visible = false
			buttons.add_child(Kit.button("Follow", func() -> void: vehicle_action.emit("follow", id), "The camera follows it until you move the camera."))
			buttons.add_child(Kit.button("To depot", func() -> void: vehicle_action.emit("depot", id), "Take it off its line to wait in the nearest depot."))
			buttons.add_child(Kit.button("Sell", func() -> void: vehicle_action.emit("sell", id), "Sell it for part of its price."))
			buttons.add_child(Kit.button("Vehicles", open_window.bind("vehicles"), "The Vehicles window: repairs and its maintenance policy."))
		"station":
			var view := StationsWindow.make_view()
			extra.add_child(view["box"])
			refs["view"] = view
			label.visible = false
			buttons.add_child(Kit.button("Stations", open_window.bind("stations"), "The Stations window: rename, bays, close."))
		"industry":
			var view := IndustriesWindow.make_view()
			extra.add_child(view["box"])
			refs["view"] = view
			label.visible = false
		"town":
			var view := TownsWindow.make_view()
			extra.add_child(view["box"])
			refs["view"] = view
			label.visible = false
		"depot":
			var upgrades := Kit.row()
			buttons.add_child(upgrades)
			refs["upgrades"] = upgrades
			refs["upgrade_key"] = "?"
	content.set_meta("refs", refs)
	_windows.open_object(key, kind, _object_title(kind, item), content)
	_refresh_object(key, kind, id)


func _find(kind: String, id: Variant) -> Dictionary:
	for item in latest(kind):
		if kind == "depot":
			if DepotsWindow.key_of(item) == id:
				return item
		elif item["id"] == id:
			return item
	return {}


func _object_title(kind: String, item: Dictionary) -> String:
	match kind:
		"vehicle":
			return VehiclesWindow.title_of(item)
		"station", "town", "depot":
			return item["name"]
		"industry":
			return "%s %d" % [item["name"], item["id"] + 1]
	return kind


## Brings the open windows of one kind of object up to date (after each show_*), and closes those whose object is gone.
func _refresh_objects(kind: String) -> void:
	for key in _windows.object_keys():
		var parts: PackedStringArray = key.split(":", true, 1)
		if parts[0] != kind:
			continue
		var id: Variant = parts[1] if kind == "depot" else int(parts[1])
		_refresh_object(key, kind, id)


func _refresh_object(key: String, kind: String, id: Variant) -> void:
	var content: Control = _windows.content_of(key)
	if content == null:
		return
	var item := _find(kind, id)
	if item.is_empty():
		_windows.close(key)
		return
	var refs: Dictionary = content.get_meta("refs")
	var label: Label = refs["label"]
	match kind:
		"vehicle":
			VehiclesWindow.fill_view(refs["view"], item)
		"station":
			StationsWindow.fill_view(refs["view"], item, cargo_names)
		"industry":
			IndustriesWindow.fill_view(refs["view"], item)
		"town":
			TownsWindow.fill_view(refs["view"], item, cargo_names)
		"depot":
			label.text = DepotsWindow.text_of(item)
			_pages["depots"].fill_upgrades(refs, item)


# --- the top strip ---

func _build_top_strip() -> void:
	var bar := PanelContainer.new()
	bar.set_anchors_preset(Control.PRESET_TOP_WIDE)
	bar.offset_left = 12.0
	bar.offset_right = -12.0
	bar.offset_top = 8.0
	_root.add_child(bar)
	var row := Kit.row()
	row.add_theme_constant_override("separation", 16)
	bar.add_child(row)
	_date = Label.new()
	_date.custom_minimum_size = Vector2(170, 0)
	row.add_child(_date)
	var speeds := HBoxContainer.new()
	speeds.add_theme_constant_override("separation", 4)
	row.add_child(speeds)
	for entry in SPEEDS:
		var b := Kit.button(entry[0], _set_speed.bind(entry[1]), "Pause (Space)" if entry[1] == 0.0 else "Speed %s" % entry[0])
		b.toggle_mode = true
		b.custom_minimum_size = Vector2(52, 0)
		speeds.add_child(b)
		_speed_buttons[entry[1]] = b
	row.add_child(Kit.spacer())
	_money = Label.new()
	row.add_child(_money)
	_standing = Label.new()
	_standing.mouse_filter = Control.MOUSE_FILTER_PASS
	row.add_child(_standing)
	_dismissal = Label.new()
	_dismissal.add_theme_color_override("font_color", UiTheme.WARN_TEXT)
	_dismissal.add_theme_stylebox_override("normal", UiTheme.plate(Color("f2b84b1f"), Color("f2b84b59"), 8))
	_dismissal.visible = false
	row.add_child(_dismissal)
	row.add_child(Kit.button("Menu", func() -> void: menu_requested.emit(), "Pause menu (Esc)"))


func _build_raid_banner() -> void:
	_raid_banner = Label.new()
	_raid_banner.visible = false
	_raid_banner.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_raid_banner.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_raid_banner.set_anchors_preset(Control.PRESET_CENTER_TOP)
	_raid_banner.grow_horizontal = Control.GROW_DIRECTION_BOTH
	_raid_banner.offset_top = 108.0
	_raid_banner.add_theme_color_override("font_color", UiTheme.WARN_TEXT)
	_raid_banner.add_theme_stylebox_override("normal", UiTheme.plate(Color("2a2314"), Color("f2b84b59"), 14))
	_root.add_child(_raid_banner)


# --- the news feed, on the right ---

func _build_feed() -> void:
	_feed_panel = PanelContainer.new()
	_feed_panel.set_anchors_preset(Control.PRESET_TOP_RIGHT)
	_feed_panel.grow_horizontal = Control.GROW_DIRECTION_BEGIN
	_feed_panel.offset_left = -460.0
	_feed_panel.offset_right = -12.0
	_feed_panel.offset_top = 108.0
	_feed_panel.visible = false
	_root.add_child(_feed_panel)
	var column := Kit.column()
	_feed_panel.add_child(column)
	var head := Kit.row()
	column.add_child(head)
	var title := Kit.heading("News")
	title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	head.add_child(title)
	_feed_fold = Kit.button("▾", func() -> void:
		_feed.visible = not _feed.visible
		_feed_fold.text = "▾" if _feed.visible else "▸", "Fold the news away")
	head.add_child(_feed_fold)
	_feed = Kit.column()
	column.add_child(_feed)


## The entries of the feed as they are now, for a save: [{"event": the event's dictionary, "age": seconds shown so far}].
func feed_snapshot() -> Array:
	var out: Array = []
	for item in _feed_items:
		out.append({"event": item["event"], "age": item["age"]})
	return out


## Puts back the feed of a loaded save, each entry as old as it was (no message pops up again).
func restore_feed(entries: Array) -> void:
	for entry in entries:
		show_events([entry["event"]], true)
		_feed_items[-1]["age"] = float(entry["age"])


## New entries of the event feed (from the host): serious ones also show as a message; a click on one moves the camera to the place.
func show_events(events: Array, quiet := false) -> void:
	for event in events:
		var b := Button.new()
		b.text = event["text"]
		b.alignment = HORIZONTAL_ALIGNMENT_LEFT
		b.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		b.custom_minimum_size = Vector2(400, 0)
		var colour := UiTheme.TEXT
		if event["severity"] == 2:
			colour = UiTheme.BAD
		elif event["severity"] == 1:
			colour = UiTheme.WARN_TEXT
		b.add_theme_color_override("font_color", colour)
		b.add_theme_color_override("font_hover_color", colour.lightened(0.3))
		# News without a place (a call-up, the standing, power) has nowhere to take the camera.
		if event["place"]:
			var x: float = event["x"]
			var z: float = event["z"]
			b.tooltip_text = "Click to go there."
			b.pressed.connect(func() -> void: town_focus.emit(x, z))
		_feed.add_child(b)
		_feed_items.append({"node": b, "age": 0.0, "lifetime": 90.0 if event["severity"] == 2 else 45.0, "event": event})
		if event["severity"] == 2 and not quiet:
			show_message(event["text"])
	while _feed_items.size() > FEED_MAX:
		_feed_items[0]["node"].queue_free()
		_feed_items.remove_at(0)
	_feed_panel.visible = not _feed_items.is_empty()


# --- the bottom bar ---

func _build_bottom() -> void:
	var stack := VBoxContainer.new()
	stack.mouse_filter = Control.MOUSE_FILTER_IGNORE
	stack.set_anchors_preset(Control.PRESET_CENTER_BOTTOM)
	stack.grow_horizontal = Control.GROW_DIRECTION_BOTH
	stack.grow_vertical = Control.GROW_DIRECTION_BEGIN
	stack.offset_bottom = -12.0
	stack.add_theme_constant_override("separation", Kit.GAP)
	_root.add_child(stack)

	_message = Label.new()
	_message.visible = false
	_message.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_message.add_theme_stylebox_override("normal", UiTheme.plate(UiTheme.SURFACE, UiTheme.BORDER_HOVER))
	_message.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	stack.add_child(_message)

	for category in TOOL_CATEGORIES:
		var r := _group_row(stack, category["name"])
		for entry in category["tools"]:
			var b := Kit.button(entry[0], _on_tool_pressed.bind(entry[1]), entry[2])
			b.toggle_mode = true
			Icons.put(b, entry[1])
			r.add_child(b)
			_tool_buttons[entry[1]] = b
	for group in INFO_GROUPS:
		var r := _group_row(stack, group[0])
		for entry in group[1]:
			var b := Kit.button(entry[1], toggle_window.bind(entry[0]))
			b.toggle_mode = true
			Icons.put(b, entry[0])
			r.add_child(b)
			_window_buttons[entry[0]] = b

	var bar := PanelContainer.new()
	bar.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	stack.add_child(bar)
	var row := Kit.row()
	row.add_theme_constant_override("separation", 6)
	bar.add_child(row)
	_pointer_button = Kit.button("Pointer", _on_tool_pressed.bind("pointer"), "No tool: look around, click something to open its window (Esc)")
	_pointer_button.toggle_mode = true
	Icons.put(_pointer_button, "pointer")
	_stack_icon(_pointer_button)
	row.add_child(_pointer_button)
	row.add_child(VSeparator.new())
	for i in TOOL_CATEGORIES.size():
		row.add_child(_group_button(TOOL_CATEGORIES[i]["name"], "build_%d" % (i + 1)))
	row.add_child(VSeparator.new())
	for i in INFO_GROUPS.size():
		row.add_child(_group_button(INFO_GROUPS[i][0], "info_%d" % (i + 1)))


func _group_row(stack: VBoxContainer, group: String) -> HBoxContainer:
	var panel := PanelContainer.new()
	panel.visible = false
	panel.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	stack.add_child(panel)
	var r := Kit.row()
	r.add_theme_constant_override("separation", 6)
	panel.add_child(r)
	_group_rows[group] = panel
	return r


func _group_button(group: String, action: String) -> Button:
	var b := Kit.button(group, _toggle_group.bind(group))
	b.toggle_mode = true
	b.set_meta("action", action)
	Icons.put(b, group)
	_stack_icon(b)
	_group_buttons[group] = b
	return b


## The icon over the label, a taller button, as on the bottom bar of the design.
func _stack_icon(b: Button) -> void:
	b.vertical_icon_alignment = VERTICAL_ALIGNMENT_TOP
	b.icon_alignment = HORIZONTAL_ALIGNMENT_CENTER
	b.custom_minimum_size = Vector2(0, 56)


## Shows the row of one group; the same group again closes it.
func _toggle_group(group: String) -> void:
	_open_group = "" if _open_group == group else group
	for g in _group_rows:
		_group_rows[g].visible = g == _open_group
		_group_buttons[g].set_pressed_no_signal(g == _open_group)


func select_tool(tool_name: String) -> void:
	_pointer_button.set_pressed_no_signal(tool_name == "pointer")
	for key in _tool_buttons:
		_tool_buttons[key].set_pressed_no_signal(key == tool_name)
	# A tool picked from outside (the self-test) opens its group so the highlight is visible.
	for category in TOOL_CATEGORIES:
		for entry in category["tools"]:
			if entry[1] == tool_name and _open_group != category["name"]:
				_toggle_group(category["name"])


func _on_tool_pressed(tool_name: String) -> void:
	select_tool(tool_name)
	tool_selected.emit(tool_name)


func highlight_speed(speed: float) -> void:
	_speed = speed
	if speed > 0.0:
		_speed_before_pause = speed
	for key in _speed_buttons:
		_speed_buttons[key].set_pressed_no_signal(is_equal_approx(key, speed))


func _set_speed(speed: float) -> void:
	highlight_speed(speed)
	speed_selected.emit(speed)


func show_message(text: String) -> void:
	_message.text = text
	_message_age = 0.0
	_message.visible = text != ""


# --- the map views (M6b), in the Map group, with the legend in the bottom-left corner ---

func _build_legend() -> void:
	_legend = PanelContainer.new()
	_legend.visible = false
	_legend.custom_minimum_size = Vector2(320, 0)
	_legend.set_anchors_preset(Control.PRESET_BOTTOM_LEFT)
	_legend.grow_vertical = Control.GROW_DIRECTION_BEGIN
	_legend.offset_left = 12.0
	_legend.offset_bottom = -150.0
	_root.add_child(_legend)
	var column := Kit.column()
	_legend.add_child(column)
	_legend_title = Kit.heading("")
	column.add_child(_legend_title)
	_legend_rows = Kit.column()
	column.add_child(_legend_rows)
	_legend_note = Kit.note("")
	_legend_note.custom_minimum_size = Vector2(300, 0)
	column.add_child(_legend_note)


## The map views (SimHost.GetOverlayNames), the first being none: the buttons of the Map group.
func set_overlay_names(names: Array) -> void:
	_overlay_names = names
	for b in _overlay_buttons:
		b.queue_free()
	_overlay_buttons.clear()
	var r: HBoxContainer = _group_rows["Map"].get_child(0)
	for i in names.size():
		var b := Kit.button(names[i], func() -> void: overlay_selected.emit(i), "V shows the next map view, Shift+V the one before")
		b.toggle_mode = true
		b.button_pressed = i == _overlay_index
		r.add_child(b)
		_overlay_buttons.append(b)


## The map view on show and its legend ({title, rows: [{colour, text}], note}; empty with none).
func show_overlay(index: int, legend: Dictionary) -> void:
	_overlay_index = index
	for i in _overlay_buttons.size():
		_overlay_buttons[i].set_pressed_no_signal(i == index)
	_pages["towns"].connectivity_check.set_pressed_no_signal(index == _overlay_names.find("Connectivity"))
	_legend.visible = index != 0 and not legend.is_empty()
	if not _legend.visible:
		return
	_legend_title.text = legend["title"]
	for child in _legend_rows.get_children():
		child.queue_free()
	for row in legend["rows"]:
		var line := Kit.row()
		var swatch := ColorRect.new()
		swatch.color = row["colour"]
		swatch.custom_minimum_size = Vector2(18, 18)
		swatch.size_flags_vertical = Control.SIZE_SHRINK_BEGIN
		line.add_child(swatch)
		var text := Label.new()
		text.text = row["text"]
		text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		text.custom_minimum_size = Vector2(260, 0)
		text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		line.add_child(text)
		_legend_rows.add_child(line)
	_legend_note.text = legend["note"]
	_legend_note.visible = legend["note"] != ""


# --- debug and the end of the campaign ---

func _build_debug_panel() -> void:
	var panel := PanelContainer.new()
	panel.position = Vector2(12, 72)
	panel.visible = false
	_root.add_child(panel)
	_debug_panel = panel
	var column := Kit.column()
	panel.add_child(column)
	column.add_child(Kit.heading("Debug (%s)" % Keys.text("debug", Settings.key_bindings)))
	_stats = Label.new()
	column.add_child(_stats)
	var r := Kit.flow()
	r.custom_minimum_size = Vector2(440, 0)
	column.add_child(r)
	for entry in [["+100 cars", "traffic"], ["Crash!", "crash"], ["Empty map", "empty"], ["Hills map", "hills"], ["Towns map", "towns"], ["Demo map", "demo"]]:
		r.add_child(Kit.button(entry[0], func() -> void: action_requested.emit(entry[1])))


## A panel in the middle of the screen, hidden until the campaign is over.
func _build_campaign_end() -> void:
	var center := CenterContainer.new()
	center.set_anchors_preset(Control.PRESET_FULL_RECT)
	center.visible = false
	_root.add_child(center)
	_campaign_end = center
	var panel := PanelContainer.new()
	panel.custom_minimum_size = Vector2(520, 0)
	center.add_child(panel)
	var column := Kit.column()
	panel.add_child(column)
	_campaign_end_title = Kit.heading("")
	column.add_child(_campaign_end_title)
	_campaign_end_text = Label.new()
	_campaign_end_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(_campaign_end_text)
	column.add_child(Kit.button("Main menu", func() -> void: campaign_left.emit()))


# --- what the host sends, each refresh ---

func show_stats(stats: Dictionary) -> void:
	_stats.text = "%d fps\nvehicles %d   overtaking %d\nmean speed %.1f m/s\nsim speed x%.2f   step %.2f ms\nincidents %d (open %d)   wrecks %d" % [
		Engine.get_frames_per_second(), stats["vehicles"], stats["overtaking"], stats["mean_speed"],
		stats["effective_speed"], stats["step_ms"], stats["incidents"], stats["open_incidents"], stats["wrecks"]]


## The company: money, standing and the date in the top strip, and the Finances, Requisition and Standing windows.
func show_company(company: Dictionary) -> void:
	_company = company
	var standing: float = company["standing"]
	var low: bool = standing < company["dismiss_below"] + 10.0
	var hour: float = company["hour"]
	_date.text = "%s %d\nDay %d · %02d:%02d" % [Kit.MONTHS[(company["month"] - 1) % 12], company["year"], company["day_of_month"], int(hour), int(fmod(hour, 1.0) * 60.0)]
	_money.text = "Money\n%s%s" % [Kit.money(company["money"]), "  DISMISSED" if company["dismissed"] else ("  CAMPAIGN OVER" if company["finished"] else "")]
	_money.add_theme_color_override("font_color", UiTheme.BAD if company["money"] < 0 else UiTheme.TEXT)
	var history: Array = company.get("standing_history", [])
	var trend := ""
	if history.size() >= 1:
		var before: float = history[-2] if history.size() >= 2 else company.get("start_standing", history[-1])
		trend = " ▲" if history[-1] > before + 0.05 else (" ▼" if history[-1] < before - 0.05 else " ▬")
	_standing.text = "Standing\n%d%s" % [roundi(standing), trend]
	_standing.tooltip_text = "How the government judges the committee, 0 to 100: Government > Standing tells why."
	_standing.add_theme_color_override("font_color", UiTheme.BAD if low else (UiTheme.GOOD if trend == " ▲" else UiTheme.TEXT))
	_dismissal.visible = company["months_to_dismissal"] > 0
	_dismissal.text = "%d months to dismissal" % company["months_to_dismissal"]
	for key in ["finances", "requisition", "standing"]:
		_pages[key].show_company(company)
	if company["campaign_over"]:
		if not _campaign_end.visible:
			_show_campaign_end(company)
	else:
		_campaign_end.visible = false


## The panel that ends the game: dismissal, or the grade at the end of December 1945.
func _show_campaign_end(company: Dictionary) -> void:
	_campaign_end.visible = true
	if company["dismissed"]:
		_campaign_end_title.text = "Dismissed"
		_campaign_end_text.text = "The standing of the committee stayed under %d for %d months and the ministry has replaced it.\n\nStanding %d   delivered to the gateways %s   towns' satisfaction %d" % [
			roundi(company["dismiss_below"]), company["dismiss_months"], roundi(company["standing"]), Kit.money(company["delivered_value"]), roundi(company["average_satisfaction"])]
	else:
		_campaign_end_title.text = company["grade_name"]
		_campaign_end_text.text = "The campaign is over: the %s, from 1938 to 1945.\n\nScore %d of 100\nStanding %d   delivered to the gateways %s   towns' satisfaction %d" % [
			company["region"], roundi(company["score"]), roundi(company["standing"]), Kit.money(company["delivered_value"]), roundi(company["average_satisfaction"])]


## The flights in the air (from the host): the warning with the time left, the target once it is told, and "bombs falling" over the target.
func show_raid_warnings(flights: Array) -> void:
	var text := ""
	for flight in flights:
		var line := "Air raid warning: %d aircraft from the %s" % [flight["aircraft"], flight["direction"]]
		if flight["dropping"]:
			line += ", over the target now"
		else:
			line += ", arriving in %d s" % ceili(flight["seconds"])
		if flight["revealed"]:
			line += "   Target: %s" % String(flight["target"]).to_lower()
		if flight.get("sheltered", 0) > 0:
			line += "   %d people are taking cover" % flight["sheltered"]
		text += ("" if text.is_empty() else "\n") + line
	_raid_banner.text = text
	_raid_banner.visible = text != ""


func set_cargo_names(names: Array) -> void:
	cargo_names = names


## Models the company can buy, for the buy buttons of the line rows (call before show_lines).
func set_models(list: Array) -> void:
	models = list
	_pages["lines"].reset_rows()
	_pages["vehicles"].rebuild_buy_buttons()


## The trains the company can buy, for the buy buttons of the rail line rows (call before show_lines).
func set_train_options(options: Array) -> void:
	train_options = options
	_pages["lines"].reset_rows()
	_pages["vehicles"].rebuild_buy_buttons()


func show_places(places: Array) -> void:
	places_known = places


func show_lines(lines: Array) -> void:
	_latest["lines"] = lines
	_pages["lines"].show_lines(lines)


func show_vehicles(vehicles: Array) -> void:
	_latest["vehicles"] = vehicles
	_pages["vehicles"].show_vehicles(vehicles)
	_refresh_objects("vehicle")


func show_garage(depots: Array, parked: Array, lines: Array) -> void:
	_pages["vehicles"].show_garage(depots, parked, lines)


func show_depots(depots: Array) -> void:
	_latest["depots"] = depots
	_pages["depots"].show_depots(depots)
	_refresh_objects("depot")


func show_stations(stations: Array) -> void:
	_latest["stations"] = stations
	var known: Array = []
	for station in stations:
		known.append({"id": station["id"], "name": station["name"], "rail": station.get("rail", false)})
	stations_known = known
	_pages["stations"].show_stations(stations)
	_refresh_objects("station")


func show_towns(towns: Array) -> void:
	_latest["towns"] = towns
	_pages["towns"].show_towns(towns)
	_refresh_objects("town")


func show_industries(industries: Array) -> void:
	_latest["industries"] = industries
	_pages["industries"].show_industries(industries)
	_refresh_objects("industry")


func show_gateways(gateways: Array) -> void:
	_latest["gateways"] = gateways
	_pages["gateways"].show_gateways(gateways)


func show_power(power: Dictionary) -> void:
	_pages["power"].show_power(power)


func show_orders(orders: Array, open_offers: int) -> void:
	_pages["orders"].show_orders(orders)
	_window_buttons["orders"].text = "Orders (%d)" % open_offers if open_offers > 0 else "Orders"
	_group_buttons["Government"].text = "Government (%d)" % open_offers if open_offers > 0 else "Government"


func show_maintenance(maint: Dictionary, vehicles: Array) -> void:
	_pages["maintenance"].show_maintenance(maint, vehicles)


func show_worn_roads(roads: Array) -> void:
	_pages["maintenance"].show_worn_roads(roads)
