extends CanvasLayer
## Heads-up display. Top bar: menu, money, speed and the windows (Company, Lines, ...). Bottom: the tools, grouped
## in categories. Right: one window at a time. Left: the debug panel (F3). Emits intent, owns no game state.

signal speed_selected(speed: float)
signal tool_selected(tool_name: String)
signal action_requested(action: String)
## A line row button: kind is "buy" (with a vehicle model id) or "delete".
signal line_action(kind: String, line: int, model: String)
## The Lines window changed a line's stations (the new list of station ids) or its name.
signal line_edit(line: int, stops: Array)
signal line_rename(line: int, new_name: String)
## The player picked a colour for a line.
signal line_color(line: int, color: Color)
## A station row: kind is "rename" (with the new name in text), "close" or "bays".
signal station_action(kind: String, station: int, text: String)
## A vehicle row button: kind is "sell".
signal vehicle_action(kind: String, vehicle: int)
## A vehicle bought for a depot's garage: whether it is a train, which depot (its id among road or rail depots), what (a model id, or "locomotive:wagon:count").
signal garage_buy(rail: bool, depot: int, option: String)
## A parked vehicle put on a line.
signal garage_assign(parked: int, line: int)
signal garage_sell(parked: int)
## The maintenance policy block of the company (scope 0), a line (1) or a vehicle or train (2, target a vehicle id) was set: mode 0 inherit, 1 off, 2 on; kind 0 patch, 1 service, 2 overhaul.
signal policy_set(scope: int, target: int, mode: int, days: int, below: int, kind: int)
## A repair asked for a vehicle (a train when the id is negative) or for a vehicle waiting in a depot.
signal repair_requested(vehicle: int, kind: int)
signal parked_repair(parked: int, kind: int)
## A depot is to be raised to a level.
signal depot_upgrade(rail: bool, depot: int, level: int)
## The option for how often random events happen: 0 off, 1 low, 2 normal, 3 high.
signal frequency_selected(frequency: int)
## A worn road is to be resurfaced.
signal resurface_requested(road: int)
signal menu_requested
## The "Main menu" button of the end-of-campaign panel.
signal campaign_left
## The Towns window asks the camera to go to a town.
signal import_requested(gateway: int, cargo: String)
## Accept (true) or decline an order the government offered.
signal order_answered(order: int, accept: bool)
## The megawatts to buy from outside each month.
signal power_purchase_set(megawatts: int)

signal town_focus(x: float, z: float)
## The Towns window turns the connectivity view of the map on or off.
signal connectivity_view_toggled(on: bool)

const UiTheme := preload("res://game/ui_theme.gd")

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
		["Block signal", "signal_block", "A train waits here unless the whole block beyond is free. R turns the signal round."],
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
]
## Windows opened from the top bar. A page of null text is built by its own code (Company, Lines).
const WINDOWS := [
	["Company", "company", ""],
	["Lines", "lines", ""],
	["Vehicles", "vehicles", ""],
	["Maintenance", "maintenance", ""],
	["Depots", "depots", ""],
	["Stations", "stations", ""],
	["Towns", "towns", ""],
	["Industries", "industries", ""],
	["Gateways", "gateways", ""],
	["Orders", "orders", ""],
]
const MESSAGE_SECONDS := 8.0

var _root: Control
var _stats: Label
var _speed_buttons: Dictionary = {}
var _tool_buttons: Dictionary = {}
var _category_buttons: Array[Button] = []
var _category_rows: Array[Control] = []
var _pointer_button: Button
var _message: Label
var _message_age := 0.0
var _money: Label
var _campaign_end: Control
var _campaign_end_title: Label
var _campaign_end_text: Label
var _company_details: Label
var _window_buttons: Dictionary = {}
var _window_panel: PanelContainer
var _window_title: Label
var _window_pages: Dictionary = {}
var _window := ""
var _debug_panel: Control
var _lines_box: VBoxContainer
var _lines_empty: Label
var _line_rows: Dictionary = {} # line id -> {"row": Control, "label": Label, "separator": Control}
var _models: Array = []
var _train_options: Array = [] ## what can be bought for a rail line: {id, label, price, upkeep, capacity, cargo}
var _cargo_names: Array = []
var _stations_known: Array = [] # [{id, name}] of the open stations, for the "add a stop" choices of the lines
var _vehicles_box: VBoxContainer
var _vehicles_empty: Label
var _garage_depot: OptionButton
var _garage_buy_box: HFlowContainer
var _garage_hint: Label
var _garage_depots: Array = []   ## the depots in the choice: {id, rail, name, x, z}
var _garage_depot_key := ""
var _parked_header: Label
var _parked_box: VBoxContainer
var _parked_rows: Dictionary = {} # garage id -> {"row", "separator", "label", "choice", "lines_key", "assign", "go", "go_call"}
var _vehicle_rows: Dictionary = {} # vehicle id -> {"row": Control, "label": Label, "separator": Control}
var _power_label: Label
var _power_less: Button
var _power_more: Button
var _power_purchased := 0
var _power_step := 5
var _power_max := 0
var _industries_box: VBoxContainer
var _industries_empty: Label
var _orders_box: VBoxContainer
var _orders_empty: Label
var _order_rows: Dictionary = {} # order id -> {"row", "title", "status", "buttons", "accept", "decline", "go", "separator", "state"}
var _gateways_box: VBoxContainer
var _gateways_empty: Label
var _gateway_rows: Dictionary = {} # gateway id -> {"row": Control, "title": Label, "offers": Dictionary cargo id -> {"label": Label, "button": Button}, "exports": Label, "separator": Control}
var _industry_rows: Dictionary = {} # industry id -> {"row": Control, "label": Label, "status": Label, "separator": Control}
var _stations_box: VBoxContainer
var _stations_empty: Label
var _station_rows: Dictionary = {} # station id -> {"row": Control, "label": Label, "separator": Control}
var _maintenance_policy: Dictionary ## the policy block of the Maintenance window
var _maintenance_frequency: OptionButton
var _maintenance_totals: Label
var _maintenance_rules: Label
var _maintenance_fleet: VBoxContainer
var _maintenance_empty: Label
var _fleet_rows: Dictionary = {} # vehicle id -> {"row", "label", "bar", "fill", "go", "go_call"}
var _roads_box: VBoxContainer
var _road_rows: Dictionary = {} # road id -> {"row", "label", "bar", "go", "go_call", "resurface"}
var _roads_empty: Label
var _depots_box: VBoxContainer
var _depots_empty: Label
var _depot_rows: Dictionary = {} # "r3" / "d1" -> {"row", "label", "jobs", "upgrades", "upgrade_key", "go", "go_call"}
var _feed: VBoxContainer
var _feed_items: Array = [] ## the entries of the event feed: {"node": Button, "age": float, "lifetime": float}
const FEED_MAX := 6
const REPAIR_NAMES := ["Patch", "Service", "Overhaul"]
const FREQUENCY_NAMES := ["Off", "Low", "Normal", "High"]
var _towns_box: VBoxContainer
var _towns_empty: Label
var _town_rows: Dictionary = {} # town id -> {"row": Control, "label": Label, "bar": ProgressBar}
const MONTHS := ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]


func _ready() -> void:
	_root = Control.new()
	_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_root.theme = UiTheme.build()
	add_child(_root)
	_build_top_bar()
	_build_window_panel()
	_build_feed()
	_build_campaign_end()
	_build_debug_panel()
	_build_bottom()
	select_tool("pointer")
	highlight_speed(1.0)


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


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_F3:
		_debug_panel.visible = not _debug_panel.visible


# --- building the layout ---

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
	var column := VBoxContainer.new()
	panel.add_child(column)
	_campaign_end_title = Label.new()
	_campaign_end_title.theme_type_variation = "Heading"
	column.add_child(_campaign_end_title)
	_campaign_end_text = Label.new()
	_campaign_end_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(_campaign_end_text)
	var back := Button.new()
	back.text = "Main menu"
	back.pressed.connect(func() -> void: campaign_left.emit())
	column.add_child(back)


func _build_top_bar() -> void:
	var bar := PanelContainer.new()
	bar.set_anchors_preset(Control.PRESET_TOP_WIDE)
	_root.add_child(bar)
	var row := HBoxContainer.new()
	bar.add_child(row)

	var menu := Button.new()
	menu.text = "Menu"
	menu.tooltip_text = "Pause menu (Esc)"
	menu.pressed.connect(func() -> void: menu_requested.emit())
	row.add_child(menu)
	_money = Label.new()
	_money.custom_minimum_size = Vector2(560, 0)
	_money.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	row.add_child(_money)

	row.add_child(_spacer())
	for entry in SPEEDS:
		var button := Button.new()
		button.text = entry[0]
		button.toggle_mode = true
		button.custom_minimum_size = Vector2(52, 0)
		button.pressed.connect(_on_speed_pressed.bind(entry[1]))
		row.add_child(button)
		_speed_buttons[entry[1]] = button
	row.add_child(_spacer())

	for entry in WINDOWS:
		var button := Button.new()
		button.text = entry[0]
		button.toggle_mode = true
		button.pressed.connect(toggle_window.bind(entry[1]))
		row.add_child(button)
		_window_buttons[entry[1]] = button
	var debug := Button.new()
	debug.text = "Debug"
	debug.tooltip_text = "Statistics and test actions (F3)"
	debug.pressed.connect(func() -> void: _debug_panel.visible = not _debug_panel.visible)
	row.add_child(debug)


## The right-hand window: a title with a close button over one page per entry of WINDOWS.
func _build_window_panel() -> void:
	_window_panel = PanelContainer.new()
	_window_panel.set_anchors_preset(Control.PRESET_RIGHT_WIDE)
	_window_panel.offset_left = -460.0
	_window_panel.offset_top = 76.0
	_window_panel.offset_right = -12.0
	_window_panel.offset_bottom = -12.0
	_window_panel.visible = false
	_root.add_child(_window_panel)
	var column := VBoxContainer.new()
	_window_panel.add_child(column)

	var header := HBoxContainer.new()
	column.add_child(header)
	_window_title = Label.new()
	_window_title.theme_type_variation = "Heading"
	_window_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	header.add_child(_window_title)
	var close := Button.new()
	close.text = "X"
	close.tooltip_text = "Close"
	close.pressed.connect(toggle_window.bind(""))
	header.add_child(close)

	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	column.add_child(scroll)
	var pages := VBoxContainer.new()
	pages.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(pages)

	for entry in WINDOWS:
		var page: Control
		match entry[1]:
			"company":
				page = _build_company_page()
			"lines":
				page = _build_lines_page()
			"towns":
				page = _build_towns_page()
			"industries":
				page = _build_industries_page()
			"gateways":
				page = _build_gateways_page()
			"orders":
				page = _build_orders_page()
			"stations":
				page = _build_stations_page()
			"vehicles":
				page = _build_vehicles_page()
			"maintenance":
				page = _build_maintenance_page()
			"depots":
				page = _build_depots_page()
			_:
				page = _placeholder(entry[2])
		page.visible = false
		pages.add_child(page)
		_window_pages[entry[1]] = page


func _build_company_page() -> Control:
	var column := VBoxContainer.new()
	_company_details = Label.new()
	column.add_child(_company_details)
	var buttons := HBoxContainer.new()
	column.add_child(buttons)
	for entry in [["Ask the ministry for an advance of 100k", "loan"], ["Repay 100k", "repay"]]:
		var button := Button.new()
		button.text = entry[0]
		button.pressed.connect(func() -> void: action_requested.emit(entry[1]))
		buttons.add_child(button)
	return column


func _build_lines_page() -> Control:
	_lines_box = VBoxContainer.new()
	_lines_empty = Label.new()
	_lines_empty.theme_type_variation = "Dim"
	_lines_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_lines_empty.text = "No lines yet. Build a depot and two stations, then use Transport > New line."
	_lines_box.add_child(_lines_empty)
	return _lines_box


func _build_towns_page() -> Control:
	_towns_box = VBoxContainer.new()
	var view := CheckButton.new()
	view.text = "Show connectivity on the map"
	view.tooltip_text = "Homes turn green when someone living there reached work or leisure within the time budget last month, red when nobody did. Everything else is dimmed."
	view.toggled.connect(func(on: bool) -> void: connectivity_view_toggled.emit(on))
	_towns_box.add_child(view)
	_towns_empty = Label.new()
	_towns_empty.theme_type_variation = "Dim"
	_towns_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_towns_empty.text = "There are no towns on this map. Start a new game on the Towns map."
	_towns_box.add_child(_towns_empty)
	return _towns_box


func _build_orders_page() -> Control:
	_orders_box = VBoxContainer.new()
	var note := Label.new()
	note.theme_type_variation = "Dim"
	note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	note.text = "What the government asks of the committee. An order can be accepted once stations already cover the place the cargo comes from and where it goes; the clock starts when you accept. Meeting it pays its reward and raises the standing; failing costs a fine and standing."
	_orders_box.add_child(note)
	_orders_empty = Label.new()
	_orders_empty.theme_type_variation = "Dim"
	_orders_empty.text = "The government has asked for nothing yet."
	_orders_box.add_child(_orders_empty)
	return _orders_box


func _build_gateways_page() -> Control:
	_gateways_box = VBoxContainer.new()
	var note := Label.new()
	note.theme_type_variation = "Dim"
	note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	note.text = "The Region's links with the rest of the world. Build a road to a gateway and a station beside it (a road station for a road gateway, a rail station for a rail gateway). Imported cargo arrives there to be hauled inland; what your vehicles unload there is exported."
	_gateways_box.add_child(note)
	_gateways_empty = Label.new()
	_gateways_empty.theme_type_variation = "Dim"
	_gateways_empty.text = "There are no gateways on this map."
	_gateways_box.add_child(_gateways_empty)
	return _gateways_box


func _build_industries_page() -> Control:
	_industries_box = VBoxContainer.new()
	_power_label = Label.new()
	_power_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_industries_box.add_child(_power_label)
	var power_buttons := HBoxContainer.new()
	_industries_box.add_child(power_buttons)
	_power_less = Button.new()
	_power_less.pressed.connect(func() -> void: power_purchase_set.emit(maxi(0, _power_purchased - _power_step)))
	power_buttons.add_child(_power_less)
	_power_more = Button.new()
	_power_more.pressed.connect(func() -> void: power_purchase_set.emit(mini(_power_max, _power_purchased + _power_step)))
	power_buttons.add_child(_power_more)
	_industries_box.add_child(HSeparator.new())
	_industries_empty = Label.new()
	_industries_empty.theme_type_variation = "Dim"
	_industries_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_industries_empty.text = "There are no industries on this map."
	_industries_box.add_child(_industries_empty)
	return _industries_box


func _build_stations_page() -> Control:
	_stations_box = VBoxContainer.new()
	_stations_empty = Label.new()
	_stations_empty.theme_type_variation = "Dim"
	_stations_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_stations_empty.text = "No stations yet. Build one on a road beside a town or an industry."
	_stations_box.add_child(_stations_empty)
	return _stations_box


func _build_vehicles_page() -> Control:
	_vehicles_box = VBoxContainer.new()
	var buy_title := Label.new()
	buy_title.text = "Buy a vehicle"
	_vehicles_box.add_child(buy_title)
	var buy_note := Label.new()
	buy_note.theme_type_variation = "Dim"
	buy_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	buy_note.text = "It waits in the depot you pick until you put it on a line. Vehicles also come out of a depot at once when bought from the Lines window."
	_vehicles_box.add_child(buy_note)
	_garage_depot = OptionButton.new()
	_garage_depot.item_selected.connect(func(_index: int) -> void: _rebuild_buy_buttons())
	_vehicles_box.add_child(_garage_depot)
	_garage_buy_box = HFlowContainer.new()
	_vehicles_box.add_child(_garage_buy_box)
	_garage_hint = Label.new()
	_garage_hint.theme_type_variation = "Dim"
	_garage_hint.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_garage_hint.text = "No depot yet: build one under Transport > Depot, or under Rail > Rail depot for trains."
	_vehicles_box.add_child(_garage_hint)
	_vehicles_box.add_child(HSeparator.new())
	_parked_header = Label.new()
	_parked_header.text = "Waiting in depots"
	_parked_header.visible = false
	_vehicles_box.add_child(_parked_header)
	_parked_box = VBoxContainer.new()
	_vehicles_box.add_child(_parked_box)
	_vehicles_empty = Label.new()
	_vehicles_empty.theme_type_variation = "Dim"
	_vehicles_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_vehicles_empty.text = "No vehicles on the road yet. Buy one above and put it on a line."
	_vehicles_box.add_child(_vehicles_empty)
	return _vehicles_box


## A row of controls that edits a maintenance policy: whether it applies (or inherits the level above's), when a vehicle is due (days
## since the last repair, Condition below a floor, either or both, 0 meaning not by that) and which repair it gets. Scope is 0 for the
## company, 1 for a line, 2 for a vehicle or train.
func _make_policy_block(scope: int, target: int) -> Dictionary:
	var box := VBoxContainer.new()
	var summary := Label.new()
	summary.theme_type_variation = "Dim"
	summary.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	box.add_child(summary)
	var row := HFlowContainer.new()
	box.add_child(row)
	var mode := OptionButton.new()
	if scope != 0:
		mode.add_item("Inherit", 0)
	mode.add_item("Off", 1)
	mode.add_item("On", 2)
	mode.tooltip_text = "Inherit: the level above decides. Off: no maintenance, the risk is yours. On: due by the triggers below."
	row.add_child(mode)
	var days := SpinBox.new()
	days.min_value = 0
	days.max_value = 3650
	days.step = 5
	days.prefix = "every "
	days.suffix = " days"
	days.tooltip_text = "Due this many days after the last repair. 0: not by days."
	row.add_child(days)
	var below := SpinBox.new()
	below.min_value = 0
	below.max_value = 100
	below.step = 5
	below.prefix = "below "
	below.tooltip_text = "Due when Condition is below this. 0: not by Condition."
	row.add_child(below)
	var kind := OptionButton.new()
	for i in REPAIR_NAMES.size():
		kind.add_item(REPAIR_NAMES[i], i)
	kind.tooltip_text = "Patch: quick and cheap, +15, leaves the vehicle patched (it wears faster). Service: +50. Overhaul: up to 95, clears patches. A depot of a higher level does the bigger repairs."
	row.add_child(kind)
	var apply := Button.new()
	apply.text = "Set"
	apply.tooltip_text = "Use this policy."
	apply.pressed.connect(func() -> void: policy_set.emit(scope, target, mode.get_selected_id(), int(days.value), int(below.value), kind.get_selected_id()))
	row.add_child(apply)
	return {"box": box, "summary": summary, "mode": mode, "days": days, "below": below, "kind": kind, "last": {}}


## Shows a policy in its block, unless the player is typing in it.
func _show_policy(block: Dictionary, policy: Dictionary, text: String) -> void:
	block["summary"].text = text
	if block["last"] == policy:
		return
	if block["days"].get_line_edit().has_focus() or block["below"].get_line_edit().has_focus():
		return
	block["last"] = policy.duplicate()
	var index: int = block["mode"].get_item_index(int(policy["mode"]))
	if index >= 0:
		block["mode"].select(index)
	block["days"].value = policy["days"]
	block["below"].value = policy["below"]
	block["kind"].select(int(policy["kind"]))


func _condition_color(value: float) -> Color:
	if value >= 60.0:
		return Color(0.25, 0.75, 0.35)
	if value >= 40.0:
		return Color(0.85, 0.75, 0.2)
	if value >= 20.0:
		return Color(0.9, 0.5, 0.15)
	return Color(0.85, 0.2, 0.15)


## A bar for a Condition from 0 to 100; returns the bar and the style of its fill (to colour by the value).
func _make_condition_bar() -> Dictionary:
	var bar := ProgressBar.new()
	bar.min_value = 0.0
	bar.max_value = 100.0
	bar.show_percentage = false
	bar.custom_minimum_size = Vector2(110, 16)
	bar.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	bar.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	var fill := StyleBoxFlat.new()
	fill.bg_color = _condition_color(100.0)
	bar.add_theme_stylebox_override("fill", fill)
	return {"bar": bar, "fill": fill}


func _set_condition_bar(entry: Dictionary, value: float) -> void:
	entry["bar"].value = value
	entry["fill"].bg_color = _condition_color(value)
	entry["bar"].tooltip_text = "Condition %d of 100. Below 60 a breakdown becomes possible, below 40 the vehicle is darker on the map, below 20 reddish and the brakes weaker." % roundi(value)


func _build_maintenance_page() -> Control:
	var column := VBoxContainer.new()
	var heading := Label.new()
	heading.text = "Company policy"
	column.add_child(heading)
	var note := Label.new()
	note.theme_type_variation = "Dim"
	note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	note.text = "When a vehicle or train is due it unloads at its next stop, loads nothing and drives to the nearest depot that can do the repair. Lines, vehicles and trains can have a policy of their own; the most specific one that does not inherit wins. Signals follow the company policy."
	column.add_child(note)
	_maintenance_policy = _make_policy_block(0, 0)
	column.add_child(_maintenance_policy["box"])
	column.add_child(HSeparator.new())

	var events := Label.new()
	events.text = "Random events"
	column.add_child(events)
	_maintenance_frequency = OptionButton.new()
	for i in FREQUENCY_NAMES.size():
		_maintenance_frequency.add_item(FREQUENCY_NAMES[i], i)
	_maintenance_frequency.tooltip_text = "How often breakdowns and signal failures happen."
	_maintenance_frequency.item_selected.connect(func(index: int) -> void: frequency_selected.emit(_maintenance_frequency.get_item_id(index)))
	column.add_child(_maintenance_frequency)
	var frequency_note := Label.new()
	frequency_note.theme_type_variation = "Dim"
	frequency_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	frequency_note.text = "Accidents that come out of the driving (an overtaking gone wrong, a crash at a junction) are not scaled, so Off does not remove all accidents."
	column.add_child(frequency_note)
	column.add_child(HSeparator.new())

	_maintenance_totals = Label.new()
	_maintenance_totals.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(_maintenance_totals)
	_maintenance_rules = Label.new()
	_maintenance_rules.theme_type_variation = "Dim"
	_maintenance_rules.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(_maintenance_rules)
	column.add_child(HSeparator.new())

	var fleet := Label.new()
	fleet.text = "Fleet by Condition"
	column.add_child(fleet)
	_maintenance_fleet = VBoxContainer.new()
	column.add_child(_maintenance_fleet)
	_maintenance_empty = Label.new()
	_maintenance_empty.theme_type_variation = "Dim"
	_maintenance_empty.text = "No vehicles yet."
	column.add_child(_maintenance_empty)
	column.add_child(HSeparator.new())

	var roads := Label.new()
	roads.text = "Worn roads"
	column.add_child(roads)
	var roads_note := Label.new()
	roads_note.theme_type_variation = "Dim"
	roads_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	roads_note.text = "Traffic wears roads. Below Condition 80 drivers go slower and company vehicles break down more often. Resurfacing costs a price per metre and closes the road while the crew works."
	column.add_child(roads_note)
	_roads_box = VBoxContainer.new()
	column.add_child(_roads_box)
	_roads_empty = Label.new()
	_roads_empty.theme_type_variation = "Dim"
	_roads_empty.text = "No road is worn."
	column.add_child(_roads_empty)
	return column


func _build_depots_page() -> Control:
	_depots_box = VBoxContainer.new()
	var note := Label.new()
	note.theme_type_variation = "Dim"
	note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	note.text = "A Simple depot patches, a Workshop also services, a Complex also overhauls. Bays: how many vehicles it repairs at once; the rest wait inside. A depot is upgraded in place for the difference in price."
	_depots_box.add_child(note)
	_depots_empty = Label.new()
	_depots_empty.theme_type_variation = "Dim"
	_depots_empty.text = "No depots yet."
	_depots_box.add_child(_depots_empty)
	return _depots_box


## The event feed: a few lines at the top left that fade out; a click moves the camera to the place.
func _build_feed() -> void:
	_feed = VBoxContainer.new()
	_feed.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_feed.offset_left = 12.0
	_feed.offset_top = 84.0
	_feed.custom_minimum_size = Vector2(440, 0)
	_root.add_child(_feed)


func _placeholder(text: String) -> Control:
	var label := Label.new()
	label.theme_type_variation = "Dim"
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	label.text = text
	return label


func _build_debug_panel() -> void:
	var panel := PanelContainer.new()
	panel.position = Vector2(12, 76)
	panel.visible = false
	_root.add_child(panel)
	_debug_panel = panel
	var column := VBoxContainer.new()
	panel.add_child(column)
	var title := Label.new()
	title.text = "Debug (F3)"
	title.theme_type_variation = "Heading"
	column.add_child(title)
	_stats = Label.new()
	column.add_child(_stats)
	var row := HFlowContainer.new()
	row.custom_minimum_size = Vector2(440, 0)
	column.add_child(row)
	for entry in [["+100 cars", "traffic"], ["Crash!", "crash"], ["Empty map", "empty"], ["Hills map", "hills"], ["Towns map", "towns"], ["Demo map", "demo"]]:
		var button := Button.new()
		button.text = entry[0]
		button.pressed.connect(func() -> void: action_requested.emit(entry[1]))
		row.add_child(button)


## Bottom centre: the message line, the tools of the open category, and the category bar with Pointer.
func _build_bottom() -> void:
	var stack := VBoxContainer.new()
	stack.mouse_filter = Control.MOUSE_FILTER_IGNORE
	stack.set_anchors_preset(Control.PRESET_CENTER_BOTTOM)
	stack.grow_horizontal = Control.GROW_DIRECTION_BOTH
	stack.grow_vertical = Control.GROW_DIRECTION_BEGIN
	stack.offset_bottom = -12.0
	_root.add_child(stack)

	_message = Label.new()
	_message.visible = false
	_message.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_message.add_theme_color_override("font_outline_color", Color.BLACK)
	_message.add_theme_constant_override("outline_size", 6)
	stack.add_child(_message)

	for category in TOOL_CATEGORIES:
		var panel := PanelContainer.new()
		panel.visible = false
		panel.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
		stack.add_child(panel)
		var tools := HBoxContainer.new()
		panel.add_child(tools)
		for entry in category["tools"]:
			var button := Button.new()
			button.text = entry[0]
			button.tooltip_text = entry[2]
			button.toggle_mode = true
			button.pressed.connect(_on_tool_pressed.bind(entry[1]))
			tools.add_child(button)
			_tool_buttons[entry[1]] = button
		_category_rows.append(panel)

	var bar := PanelContainer.new()
	bar.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	stack.add_child(bar)
	var row := HBoxContainer.new()
	bar.add_child(row)
	_pointer_button = Button.new()
	_pointer_button.text = "Pointer"
	_pointer_button.tooltip_text = "No tool: look around (Esc)"
	_pointer_button.toggle_mode = true
	_pointer_button.pressed.connect(_on_tool_pressed.bind("pointer"))
	row.add_child(_pointer_button)
	row.add_child(VSeparator.new())
	for i in TOOL_CATEGORIES.size():
		var button := Button.new()
		button.text = TOOL_CATEGORIES[i]["name"]
		button.toggle_mode = true
		button.pressed.connect(_open_category.bind(i))
		row.add_child(button)
		_category_buttons.append(button)


func _spacer() -> Control:
	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	return spacer


# --- tools, speed, windows ---

## Shows the tools of one category; picking its button again closes it.
func _open_category(index: int) -> void:
	var open := _category_buttons[index].button_pressed
	for i in _category_rows.size():
		_category_rows[i].visible = open and i == index
		_category_buttons[i].set_pressed_no_signal(open and i == index)


func select_tool(tool_name: String) -> void:
	_pointer_button.set_pressed_no_signal(tool_name == "pointer")
	for key in _tool_buttons:
		_tool_buttons[key].set_pressed_no_signal(key == tool_name)
	# A tool picked from outside (the self-test) opens its category so the highlight is visible.
	for i in TOOL_CATEGORIES.size():
		for entry in TOOL_CATEGORIES[i]["tools"]:
			if entry[1] == tool_name and not _category_rows[i].visible:
				_category_buttons[i].set_pressed_no_signal(true)
				_open_category(i)


func _on_tool_pressed(tool_name: String) -> void:
	select_tool(tool_name)
	tool_selected.emit(tool_name)


func highlight_speed(speed: float) -> void:
	for key in _speed_buttons:
		_speed_buttons[key].set_pressed_no_signal(is_equal_approx(key, speed))


func _on_speed_pressed(speed: float) -> void:
	highlight_speed(speed)
	speed_selected.emit(speed)


## Opens a window, or closes it when it is already open (an empty name closes whatever is open).
func toggle_window(window_name: String) -> void:
	_window = "" if window_name == _window else window_name
	_window_panel.visible = _window != ""
	for key in _window_pages:
		_window_pages[key].visible = key == _window
		_window_buttons[key].set_pressed_no_signal(key == _window)
	for entry in WINDOWS:
		if entry[1] == _window:
			_window_title.text = entry[0]


func show_message(text: String) -> void:
	_message.text = text
	_message_age = 0.0
	_message.visible = text != ""


# --- live numbers ---

func show_stats(stats: Dictionary) -> void:
	_stats.text = "%d fps\nvehicles %d   overtaking %d\nmean speed %.1f m/s\nsim speed x%.2f   step %.2f ms\nincidents %d (open %d)   wrecks %d" % [
		Engine.get_frames_per_second(),
		stats["vehicles"], stats["overtaking"],
		stats["mean_speed"],
		stats["effective_speed"], stats["step_ms"],
		stats["incidents"], stats["open_incidents"], stats["wrecks"]]


## Company summary: money, standing and day in the bar, the books and the budget in the Company window.
func show_company(company: Dictionary) -> void:
	var standing: float = company["standing"]
	var low: bool = standing < company["dismiss_below"] + 10.0
	_money.text = "Money %s   Standing %d   %s%s" % [_money_text(company["money"]), roundi(standing), _date_text(company),
		"   DISMISSED" if company["dismissed"] else ("   CAMPAIGN OVER" if company["finished"] else "")]
	_money.add_theme_color_override("font_color", Color(1.0, 0.45, 0.4) if company["money"] < 0 or low else UiTheme.TEXT)
	var text := "%s: %s\n%s, month %d of %d\n\nMoney   %s\nAdvance from the ministry   %s of %s\nDate   %s\n\nIncome   %s\nUpkeep   %s\nInterest   %s\nAllocations so far   %s\nImports so far   %s" % [
		company["country"], company["region"], company["phase"], mini(company["phase_month"] + 1, company["months"]), company["months"],
		_money_text(company["money"]), _money_text(company["loan"]), _money_text(company["max_loan"]), _date_text(company),
		_money_text(company["income"]), _money_text(-company["upkeep"]), _money_text(-company["interest"]),
		_money_text(company["allocations"]), _money_text(-company["imports"])]
	text += "\n\nStanding   %d of 100   (allocation factor x%.2f)" % [roundi(standing), company["allocation_factor"]]
	if company["months_to_dismissal"] > 0:
		text += "\nUnder %d: %d more months and the committee is dismissed" % [roundi(company["dismiss_below"]), company["months_to_dismissal"]]
	text += "\nNext allocation   %s" % _money_text(company["next_allocation"])
	text += "\nOrders   %d met, %d late, %d failed   (%d under way, %d on offer)   rewards so far %s" % [company["orders_met"], company["orders_late"], company["orders_failed"], company["orders_active"], company["orders_open"], _money_text(company["orders_income"])]
	if company["called_up"] > 0 or company["next_call_month"] >= 0:
		text += "\nCall-up   %d people called up so far   workforce %d%% short (industry at %d%%)%s" % [company["called_up"], roundi(company["workforce_gap"] * 100.0), roundi(company["workforce_factor"] * 100.0),
			"   next call-up in %s %d" % [MONTHS[company["next_call_month"] % 12], company["year"] - floori(company["phase_month"] / 12.0) + floori(company["next_call_month"] / 12.0)] if company["next_call_month"] >= 0 else ""]
	if company["raids"] > 0:
		text += "\nRaids   %d so far   %d casualties" % [company["raids"], company["casualties"]]
	if company["requisitioned"] > 0 or company["next_requisition_month"] >= 0:
		text += "\nRequisition   the army has taken %d vehicles and trains (paid %s)" % [company["requisitioned"], _money_text(company["requisition_income"])]
		if company["next_requisition_month"] >= 0:
			text += "   next in %s %d: %d%% of the fleet, about %d now" % [MONTHS[company["next_requisition_month"] % 12],
				company["year"] - floori(company["phase_month"] / 12.0) + floori(company["next_requisition_month"] / 12.0), roundi(company["next_requisition_share"] * 100.0), company["expected_requisition"]]
	text += "\nDelivered to the gateways this month   %s  (the phase expects %s)" % [_money_text(company["export_value"]), _money_text(company["expected_value"])]
	var last: Dictionary = company["last"]
	if not last.is_empty():
		text += "\n\nLast month   deliveries %+.1f   deficit %+.1f   fines %+.1f   standing %d to %d   allocation %s" % [
			last["export_term"], last["deficit_term"], last["fine_term"], roundi(last["standing_before"]), roundi(last["standing_after"]), _money_text(last["allocation"])]
	_company_details.text = text
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
			roundi(company["dismiss_below"]), company["dismiss_months"], roundi(company["standing"]), _money_text(company["delivered_value"]), roundi(company["average_satisfaction"])]
	else:
		_campaign_end_title.text = company["grade_name"]
		_campaign_end_text.text = "The campaign is over: the %s, from 1938 to 1945.\n\nScore %d of 100\nStanding %d   delivered to the gateways %s   towns' satisfaction %d" % [
			company["region"], roundi(company["score"]), roundi(company["standing"]), _money_text(company["delivered_value"]), roundi(company["average_satisfaction"])]


## "2 Mar 1938   14:30": the calendar day and the time of day (a day is 20 minutes long, a month three days).
func _date_text(company: Dictionary) -> String:
	var hour: float = company["hour"]
	return "%d %s %d   %02d:%02d" % [company["day_of_month"], MONTHS[(company["month"] - 1) % 12], company["year"], int(hour), int(fmod(hour, 1.0) * 60.0)]


func set_cargo_names(names: Array) -> void:
	_cargo_names = names


## One block per order on offer or under way, then the last few that were closed.
func show_orders(orders: Array, open_offers: int) -> void:
	var seen := {}
	for order in orders:
		var id: int = order["id"]
		seen[id] = true
		if not _order_rows.has(id):
			_add_order_row(id, order)
		var row: Dictionary = _order_rows[id]
		var state: String = order["state"]
		row["title"].text = "%s\n%s" % [order["title"], _order_terms(order)]
		var open: bool = state == "Offered" or state == "Active"
		var status := ""
		match state:
			"Offered":
				status = "Offered: %d days to accept it.   %s" % [ceili(order["days_left"]), "" if order["can"] else order["why"]]
			"Active":
				status = "Under way: %d of %d delivered, %d days left" % [order["progress"], order["units"], ceili(order["days_left"])]
			"Met":
				status = "Met. %s paid." % _money_text(order["paid"])
			"Late":
				status = "Late: %d of %d delivered, %s paid." % [order["progress"], order["units"], _money_text(order["paid"])]
			"Failed":
				status = "Failed: %d of %d delivered, net %s." % [order["progress"], order["units"], _money_text(order["paid"])]
			_:
				status = state
		row["status"].text = status
		row["status"].theme_type_variation = "" if open else "Dim"
		row["buttons"].visible = state == "Offered"
		row["accept"].disabled = not order["can"]
		row["state"] = state
	for id in _order_rows.keys():
		if not seen.has(id):
			_order_rows[id]["row"].queue_free()
			_order_rows[id]["separator"].queue_free()
			_order_rows.erase(id)
	_orders_empty.visible = _order_rows.is_empty()
	if _window_buttons.has("orders"):
		_window_buttons["orders"].text = "Orders (%d)" % open_offers if open_offers > 0 else "Orders"


func _order_terms(order: Dictionary) -> String:
	return "Reward %s   standing +%s   %d days to deliver" % [_money_text(order["reward"]), str(snappedf(order["standing"], 0.1)), order["days"]]


func _add_order_row(id: int, order: Dictionary) -> void:
	var row := VBoxContainer.new()
	var title := Label.new()
	title.theme_type_variation = "Heading"
	title.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(title)
	var status := Label.new()
	status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(status)
	var buttons := HBoxContainer.new()
	row.add_child(buttons)
	var accept := Button.new()
	accept.text = "Accept"
	accept.pressed.connect(func() -> void: order_answered.emit(id, true))
	buttons.add_child(accept)
	var decline := Button.new()
	decline.text = "Decline"
	decline.pressed.connect(func() -> void: order_answered.emit(id, false))
	buttons.add_child(decline)
	var go := Button.new()
	go.text = "Go to"
	go.pressed.connect(func() -> void: town_focus.emit(order["x"], order["z"]))
	buttons.add_child(go)
	var separator := HSeparator.new()
	_orders_box.add_child(row)
	_orders_box.add_child(separator)
	_order_rows[id] = {"row": row, "title": title, "status": status, "buttons": buttons, "accept": accept, "decline": decline, "go": go, "separator": separator, "state": ""}


## One block per gateway: whether a station reaches it, one row per import with its price and a button to ask for a shipment, and what has been exported.
func show_gateways(gateways: Array) -> void:
	var seen := {}
	for gateway in gateways:
		var id: int = gateway["id"]
		seen[id] = true
		if not _gateway_rows.has(id):
			_add_gateway_row(id, gateway)
		var row: Dictionary = _gateway_rows[id]
		row["title"].text = "%s   (%s)\n%s%s" % [gateway["name"], String(gateway["kind"]).to_lower(),
			"A station reaches it." if gateway["covered"] else "No station reaches it: build a %s station beside it." % String(gateway["kind"]).to_lower(),
			"\nBombed: shut for %s more days, no shipment lands and nothing is taken." % str(snappedf(gateway["shut_days"], 0.1)) if gateway.get("shut_days", 0.0) > 0.0 else ""]
		for offer in gateway["offers"]:
			var entry: Dictionary = row["offers"][offer["cargo"]]
			var text := "%s   %d at %s each%s   %d here   %d arrived so far\n%s" % [
				offer["name"], offer["units"], _money_text(offer["price"]), "   (rationed)" if offer.get("rationed", false) else "", offer["stock"], offer["imported"], offer["status"]]
			if offer["seconds"] > 0.0:
				text += "   (%d s)" % ceili(offer["seconds"])
			entry["label"].text = text
			entry["button"].text = "Ask for %d %s: %s" % [offer["units"], String(offer["name"]).to_lower(), _money_text(offer["units"] * offer["price"])]
			entry["button"].disabled = not offer["can"]
		var exported := ""
		for item in gateway["exports"]:
			exported += ("" if exported.is_empty() else ", ") + "%d %s" % [item["units"], String(item["name"]).to_lower()]
		row["exports"].text = "Exported so far: " + (exported if not exported.is_empty() else "nothing")
	for id in _gateway_rows.keys():
		if not seen.has(id):
			_gateway_rows[id]["row"].queue_free()
			_gateway_rows[id]["separator"].queue_free()
			_gateway_rows.erase(id)
	_gateways_empty.visible = _gateway_rows.is_empty()


func _add_gateway_row(id: int, gateway: Dictionary) -> void:
	var row := VBoxContainer.new()
	var title := Label.new()
	title.theme_type_variation = "Heading"
	title.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(title)
	var go := Button.new()
	go.text = "Go to"
	go.pressed.connect(func() -> void: town_focus.emit(gateway["x"], gateway["z"]))
	row.add_child(go)
	var offers := {}
	for offer in gateway["offers"]:
		var label := Label.new()
		label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		row.add_child(label)
		var button := Button.new()
		var cargo: String = offer["cargo"]
		button.pressed.connect(func() -> void: import_requested.emit(id, cargo))
		row.add_child(button)
		offers[cargo] = {"label": label, "button": button}
	var exports := Label.new()
	exports.theme_type_variation = "Dim"
	exports.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(exports)
	var separator := HSeparator.new()
	_gateways_box.add_child(row)
	_gateways_box.add_child(separator)
	_gateway_rows[id] = {"row": row, "title": title, "offers": offers, "exports": exports, "separator": separator}


## The electricity of the region: what the industries ask for, what the grid, the plants and the power bought from outside give, and the buttons to buy more.
func show_power(power: Dictionary) -> void:
	_power_purchased = power["purchased"]
	_power_step = power["step"]
	_power_max = power["max_purchase"]
	var text := "Power   supply %d MW   demand %d MW" % [roundi(power["supply"]), roundi(power["demand"])]
	if power["factor"] < 0.995:
		text += "   SHORT: the industries make %d%% of their output" % roundi(power["factor"] * 100.0)
	text += "\nFree grid %d MW   %d of %d power plants burning   bought from outside %d MW (%s a month)" % [
		roundi(power["free_grid"]), power["plants_burning"], power["plants"], power["purchased"], _money_text(power["cost"])]
	_power_label.text = text
	_power_label.add_theme_color_override("font_color", Color(1.0, 0.7, 0.35) if power["factor"] < 0.995 else UiTheme.TEXT)
	_power_less.text = "Buy %d MW less" % _power_step
	_power_less.disabled = _power_purchased <= 0
	_power_more.text = "Buy %d MW more (%s a month each)" % [_power_step, _money_text(power["price"])]
	_power_more.disabled = _power_purchased >= _power_max


## One row per industry: what it makes and holds, who takes it, what it is doing, with a button that takes the camera there.
func show_industries(industries: Array) -> void:
	var seen := {}
	for industry in industries:
		var id: int = industry["id"]
		seen[id] = true
		if not _industry_rows.has(id):
			_add_industry_row(id, industry)
		var title: String = "%s %d" % [industry["name"], id + 1]
		if industry.get("deposit", false):
			title += "   (deposit)"
		var text := "%s\nmakes %s   %d of %d waiting for a truck   %d made so far" % [
			title, industry["output"], industry["stock"], industry["max_output"], industry["produced"]]
		if industry.get("supply", 0.0) > 0.0:
			text = "%s\ngives %d MW to the region while it has coal (now %d MW)   %d coal burned so far" % [
				title, roundi(industry["supply"]), roundi(industry["supply"] * industry["load"]), industry["produced"]]
		elif industry.get("demand", 0.0) > 0.0:
			text += "   uses %d MW" % roundi(industry["demand"])
		if industry.get("jobs", 0) > 0:
			text += "\nStaff %d%% of %d jobs   (the town's own people %d%%, %d workers brought)" % [
				roundi(industry["staffing"] * 100.0), industry["jobs"], roundi(industry["own_staff"] * 100.0), roundi(industry["staff"])]
		var used_by: String = industry.get("used_by", "")
		if used_by != "":
			text += "\nused by %s" % used_by
		for input in industry["inputs"]:
			text += "\nneeds %s   %d of %d in stock%s" % [input["cargo"], input["stock"], input["max"], "   (imported: arrives through a gateway)" if input.get("imported", false) else ""]
		var row: Dictionary = _industry_rows[id]
		row["label"].text = text
		var status: Label = row["status"]
		status.text = industry.get("status", "")
		match industry.get("status_kind", "ok"):
			"ok":
				status.add_theme_color_override("font_color", Color(0.45, 0.85, 0.5))
			"warn":
				status.add_theme_color_override("font_color", Color(1.0, 0.7, 0.35))
			_:
				status.add_theme_color_override("font_color", UiTheme.TEXT_DIM)
	for id in _industry_rows.keys():
		if not seen.has(id):
			_industry_rows[id]["row"].queue_free()
			_industry_rows[id]["separator"].queue_free()
			_industry_rows.erase(id)
	_industries_empty.visible = _industry_rows.is_empty()


func _add_industry_row(id: int, industry: Dictionary) -> void:
	var row := VBoxContainer.new()
	var label := Label.new()
	row.add_child(label)
	var status := Label.new()
	row.add_child(status)
	var go := Button.new()
	go.text = "Go to"
	go.pressed.connect(func() -> void: town_focus.emit(industry["x"], industry["z"]))
	row.add_child(go)
	var separator := HSeparator.new()
	_industries_box.add_child(row)
	_industries_box.add_child(separator)
	_industry_rows[id] = {"row": row, "label": label, "status": status, "separator": separator}


## One row per station: the cargo waiting there and what it accepts.
func show_stations(stations: Array) -> void:
	var seen := {}
	var known: Array = []
	for station in stations:
		known.append({"id": station["id"], "name": station["name"], "rail": station.get("rail", false)})
	_stations_known = known
	for station in stations:
		var id: int = station["id"]
		seen[id] = true
		if not _station_rows.has(id):
			_add_station_row(id, station)
		var waiting := ""
		var accepts := ""
		for c in _cargo_names.size():
			if station["waiting"][c] > 0:
				waiting += ("" if waiting.is_empty() else ", ") + "%d %s" % [station["waiting"][c], _cargo_names[c].to_lower()]
			if station["accepts"][c]:
				accepts += ("" if accepts.is_empty() else ", ") + _cargo_names[c].to_lower()
		var entry: Dictionary = _station_rows[id]
		var label: Label = entry["label"]
		var name_edit: LineEdit = entry["name"]
		if not name_edit.has_focus() and name_edit.text != station["name"]:
			name_edit.text = station["name"]
		var bays_button: Button = entry["bays_button"]
		bays_button.visible = not station["bays"] and not station.get("rail", false)
		bays_button.text = "Add bays (%s)" % _money_text(station["bay_price"])
		entry["station_name"] = station["name"]
		var served := ""
		for line in station.get("lines", []):
			served += ("" if served.is_empty() else ", ") + line["name"]
		var text := "lines   %s" % ("none" if served.is_empty() else served)
		if station["bays"]:
			text = "bays   %d in use\n%s" % [station["in_bay"], text]
		text += "\ncovers   %d homes   %d shops   %d other workplaces   %d industries" % [
			station["homes"], station["shops"], station["workplaces"], station["industries"]]
		text += "\nwaiting   %s\naccepts   %s" % ["nothing" if waiting.is_empty() else waiting, "nothing" if accepts.is_empty() else accepts]
		var bound_for := ""
		for queue in station.get("waiting_for", []):
			bound_for += ("" if bound_for.is_empty() else ", ") + "%d to %s" % [queue["count"], queue["name"]]
		if not bound_for.is_empty():
			text += "\npeople for   %s" % bound_for
		text += "\nyesterday   %d boarded   %d got off   %d gave up waiting" % [station["boarded"], station["alighted"], station["gave_up"]]
		text += "\ntoday so far   %d boarded   %d got off   %d gave up waiting" % [station["boarded_today"], station["alighted_today"], station["gave_up_today"]]
		label.text = text
		if not served.is_empty():
			var colour: Color = station["lines"][0]["color"]
			label.add_theme_color_override("font_color", colour)
		else:
			label.remove_theme_color_override("font_color")
	for id in _station_rows.keys():
		if not seen.has(id):
			_station_rows[id]["row"].queue_free()
			_station_rows[id]["separator"].queue_free()
			_station_rows.erase(id)
	_stations_empty.visible = _station_rows.is_empty()


## Closing a station cannot be undone, so it asks first.
func _confirm_close_station(id: int) -> void:
	var dialog := ConfirmationDialog.new()
	dialog.dialog_text = "Close %s for good?\nLines will skip it and nothing is refunded." % _station_rows[id]["station_name"]
	dialog.ok_button_text = "Close it"
	dialog.confirmed.connect(func() -> void:
		station_action.emit("close", id, "")
		dialog.queue_free())
	dialog.canceled.connect(dialog.queue_free)
	add_child(dialog)
	dialog.popup_centered()


func _add_station_row(id: int, station: Dictionary) -> void:
	var row := VBoxContainer.new()
	var header := HBoxContainer.new()
	row.add_child(header)
	var name_edit := LineEdit.new()
	name_edit.max_length = 24
	name_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	name_edit.tooltip_text = "Click to rename the station, Enter to keep the name."
	name_edit.text_submitted.connect(func(text: String) -> void:
		station_action.emit("rename", id, text)
		name_edit.release_focus())
	name_edit.focus_exited.connect(func() -> void: station_action.emit("rename", id, name_edit.text))
	header.add_child(name_edit)
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(label)
	var buttons := HFlowContainer.new()
	row.add_child(buttons)
	var go := Button.new()
	go.text = "Go to"
	go.pressed.connect(func() -> void: town_focus.emit(station["x"], station["z"]))
	buttons.add_child(go)
	var bays_button := Button.new()
	bays_button.tooltip_text = "Build bays: loading vehicles pull out of the lane so they do not block it."
	bays_button.pressed.connect(func() -> void: station_action.emit("bays", id, ""))
	buttons.add_child(bays_button)
	var close := Button.new()
	close.text = "Close"
	close.tooltip_text = "Close the station for good. Lines skip it; nothing is refunded."
	close.pressed.connect(func() -> void: _confirm_close_station(id))
	buttons.add_child(close)
	var separator := HSeparator.new()
	_stations_box.add_child(row)
	_stations_box.add_child(separator)
	_station_rows[id] = {"row": row, "label": label, "separator": separator, "name": name_edit, "bays_button": bays_button, "station_name": ""}


## One row per town: its name and numbers, a satisfaction bar and a button that takes the camera there.
func show_towns(towns: Array) -> void:
	var seen := {}
	for town in towns:
		var id: int = town["id"]
		seen[id] = true
		if not _town_rows.has(id):
			_add_town_row(id, town)
		var entry: Dictionary = _town_rows[id]
		var label: Label = entry["label"]
		label.text = "%s\n%d people   %d jobs\n%d homes   %d shops and offices   %d industries" % [
			town["name"], town["population"], town["jobs"], town["homes"], town["commerce"], town["industry"]]
		if town["inactive"] > 0 or town["abandoned"] > 0:
			label.text += "\n%d cut off from the roads   %d abandoned" % [town["inactive"], town["abandoned"]]
		label.text += "\nNeeds met last month   %d%%" % roundi(100.0 * (1.0 - town["unmet"]))
		label.text += "\nConnected   %d%% of homes reached last month" % roundi(100.0 * town["connectivity"])
		if town["blocked"] == "Satisfaction":
			label.text += "\nNot growing: satisfaction is below %d" % roundi(town["min_satisfaction"])
		elif town["blocked"] == "Connectivity":
			label.text += "\nNot growing: fewer than %d%% of homes can reach work or leisure" % roundi(100.0 * town["min_connectivity"])
		elif town["grew_sizes"] + town["grew_buildings"] > 0:
			label.text += "\nGrew last month: %d bigger, %d new buildings" % [town["grew_sizes"], town["grew_buildings"]]
			if town["grew_blocks"] > 0:
				label.text += ", %d new block of streets" % town["grew_blocks"]
		label.text += _town_needs_text(town)
		var trips: int = town["walked"] + town["drove"] + town["rode"] + town["stayed"]
		if trips > 0:
			label.text += "\nTrips yesterday   %d on foot   %d by car   %d by bus" % [town["walked"], town["drove"], town["rode"]]
			if town["stayed"] > 0:
				label.text += "   %d could not go" % town["stayed"]
		var bar: ProgressBar = entry["bar"]
		bar.value = town["satisfaction"]
		bar.tooltip_text = "Satisfaction %d of 100. It falls when buildings are demolished or abandoned, and slowly recovers." % roundi(town["satisfaction"])
	for id in _town_rows.keys():
		if not seen.has(id):
			_town_rows[id]["row"].queue_free()
			_town_rows[id]["separator"].queue_free()
			_town_rows.erase(id)
	_towns_empty.visible = _town_rows.is_empty()


## What the town still needs: how much of it a station of yours reaches, what its buildings are short of this month, and what is
## missing from their stock for growing.
func _town_needs_text(town: Dictionary) -> String:
	var text := "\nYour stations reach   %d of %d buildings" % [town["covered"], town["buildings"]]
	if town["covered"] == 0:
		return text + "\nNo station of yours reaches this town, so nothing can be delivered to it"
	var short := ""
	var missing := ""
	for need in town["needs"]:
		var name: String = _cargo_names[need["cargo"]].to_lower() if need["cargo"] < _cargo_names.size() else "cargo %d" % need["cargo"]
		if need["short"] > 0:
			short += ("" if short.is_empty() else ", ") + "%s (%d buildings)" % [name, need["short"]]
		if need["construction"] >= 1.0:
			missing += ("" if missing.is_empty() else ", ") + "%d %s" % [roundi(need["construction"]), name]
	if not short.is_empty():
		text += "\nShort of this month's needs   " + short
	if not missing.is_empty():
		text += "\nStill missing to grow   " + missing
	return text


func _add_town_row(id: int, town: Dictionary) -> void:
	var row := VBoxContainer.new()
	var label := Label.new()
	row.add_child(label)
	var satisfaction := HBoxContainer.new()
	row.add_child(satisfaction)
	var caption := Label.new()
	caption.theme_type_variation = "Dim"
	caption.text = "Satisfaction"
	satisfaction.add_child(caption)
	var bar := ProgressBar.new()
	bar.min_value = 0.0
	bar.max_value = 100.0
	bar.show_percentage = false
	bar.custom_minimum_size = Vector2(0, 18)
	bar.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	bar.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	satisfaction.add_child(bar)
	var go := Button.new()
	go.text = "Go to"
	go.pressed.connect(func() -> void: town_focus.emit(town["x"], town["z"]))
	row.add_child(go)
	var separator := HSeparator.new()
	_towns_box.add_child(row)
	_towns_box.add_child(separator)
	_town_rows[id] = {"row": row, "label": label, "bar": bar, "separator": separator}


## The trains the company can buy, for the buy buttons of the rail line rows (call before show_lines).
func set_train_options(options: Array) -> void:
	_train_options = options
	for id in _line_rows.keys():
		_remove_row(id)


## Models the company can buy, for the buy buttons of the line rows (call before show_lines).
func set_models(models: Array) -> void:
	_models = models
	for id in _line_rows.keys():
		_remove_row(id)


## One row per line: its name, stops (with buttons to reorder, drop or add one), fleet and money, and buttons to buy vehicles for it or delete it.
func show_lines(lines: Array) -> void:
	var seen := {}
	for line in lines:
		var id: int = line["id"]
		seen[id] = true
		if not _line_rows.has(id):
			_add_row(id, line.get("rail", false))
		var entry: Dictionary = _line_rows[id]
		var colour: Color = line["color"]
		entry["color"] = colour
		if not entry["swatch"].get_popup().visible: # not while the player is choosing
			entry["swatch"].color = colour
		var name_edit: LineEdit = entry["name"]
		if not name_edit.has_focus() and name_edit.text != line["name"]:
			name_edit.text = line["name"]
		var label: Label = entry["label"]
		label.text = "%d vehicles (%d loading)   delivered %d\nincome %s   upkeep %s   profit %s" % [
			line["vehicles"], line["loading"], line["delivered"],
			_money_text(line["income"]), _money_text(line["upkeep"]), _money_text(line["profit"])]
		label.add_theme_color_override("font_color", colour)
		_show_policy(entry["policy"], line["policy"], "Maintenance of its vehicles: %s" % line["policy_text"])
		_show_stops(id, line["stop_ids"], line["stations"])
		_fill_stop_choices(id)
	for id in _line_rows.keys():
		if not seen.has(id):
			_remove_row(id)
	_lines_empty.visible = _line_rows.is_empty()


## The stops of a line as rows with up, down and remove buttons; made again only when the stops change.
func _show_stops(id: int, stop_ids: Array, stop_names: Array) -> void:
	var entry: Dictionary = _line_rows[id]
	if entry["stop_ids"] == stop_ids and entry["stop_names"] == stop_names:
		return
	entry["stop_ids"] = stop_ids.duplicate()
	entry["stop_names"] = stop_names.duplicate()
	var box: VBoxContainer = entry["stops"]
	for child in box.get_children():
		box.remove_child(child)
		child.queue_free()
	for i in stop_ids.size():
		var stop := HBoxContainer.new()
		var caption := Label.new()
		caption.text = "%d  %s" % [i + 1, stop_names[i]]
		caption.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		stop.add_child(caption)
		for action in [["Up", -1], ["Down", 1], ["X", 0]]:
			var button := Button.new()
			button.text = action[0]
			button.disabled = (action[1] == -1 and i == 0) or (action[1] == 1 and i == stop_ids.size() - 1)
			button.tooltip_text = {"Up": "Move this stop earlier", "Down": "Move this stop later", "X": "Take this stop off the line"}[action[0]]
			button.pressed.connect(_edit_stops.bind(id, i, action[1]))
			stop.add_child(button)
		box.add_child(stop)


## Moves a stop one place (-1 earlier, 1 later) or takes it off (0) and asks for the line to be changed.
func _edit_stops(id: int, index: int, move: int) -> void:
	var stops: Array = _line_rows[id]["stop_ids"].duplicate()
	if move == 0:
		stops.remove_at(index)
	else:
		var other := index + move
		var kept: int = stops[index]
		stops[index] = stops[other]
		stops[other] = kept
	line_edit.emit(id, stops)


## Fills the "add a stop" choice of a line with the open stations (again only when they have changed).
func _fill_stop_choices(id: int) -> void:
	var choice: OptionButton = _line_rows[id]["choice"]
	var rail: bool = _line_rows[id]["rail"]
	var offered: Array = _stations_known.filter(func(station: Dictionary) -> bool: return station["rail"] == rail) # a line is all road or all rail
	var same := choice.item_count == offered.size()
	if same:
		for i in offered.size():
			if choice.get_item_id(i) != offered[i]["id"]:
				same = false
	if same:
		return
	var selected := choice.get_selected_id()
	choice.clear()
	for station in offered:
		choice.add_item(station["name"], station["id"])
	if choice.item_count > 0:
		choice.select(maxi(0, choice.get_item_index(selected)))


func _add_row(id: int, rail := false) -> void:
	var row := VBoxContainer.new()
	var header := HBoxContainer.new()
	row.add_child(header)
	var swatch := ColorPickerButton.new()
	swatch.custom_minimum_size = Vector2(30, 20)
	swatch.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	swatch.edit_alpha = false
	swatch.tooltip_text = "Pick a colour for this line and its vehicles."
	swatch.popup_closed.connect(func() -> void:
		var shown: Color = _line_rows[id]["color"]
		if not swatch.color.is_equal_approx(shown):
			line_color.emit(id, swatch.color))
	header.add_child(swatch)
	var name_edit := LineEdit.new()
	name_edit.max_length = 24
	name_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	name_edit.tooltip_text = "Click to rename the line, Enter to keep the name."
	name_edit.text_submitted.connect(func(text: String) -> void:
		line_rename.emit(id, text)
		name_edit.release_focus())
	name_edit.focus_exited.connect(func() -> void: line_rename.emit(id, name_edit.text))
	header.add_child(name_edit)
	var remove_button := Button.new()
	remove_button.text = "Delete"
	remove_button.tooltip_text = "Close the line and sell its vehicles."
	remove_button.pressed.connect(func() -> void: line_action.emit("delete", id, ""))
	header.add_child(remove_button)
	var label := Label.new()
	row.add_child(label)
	var policy := _make_policy_block(1, id)
	row.add_child(policy["box"])
	var stops := VBoxContainer.new()
	row.add_child(stops)
	var add := HBoxContainer.new()
	row.add_child(add)
	var choice := OptionButton.new()
	choice.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add.add_child(choice)
	var add_button := Button.new()
	add_button.text = "Add stop"
	add_button.tooltip_text = "Add the chosen station at the end of the line."
	add_button.pressed.connect(func() -> void:
		if choice.item_count == 0:
			return
		var stop_ids: Array = _line_rows[id]["stop_ids"].duplicate()
		stop_ids.append(choice.get_selected_id())
		line_edit.emit(id, stop_ids))
	add.add_child(add_button)
	var buttons := HFlowContainer.new()
	row.add_child(buttons)
	if rail:
		for option in _train_options:
			var button := Button.new()
			button.text = "+ %s (%s)" % [option["label"], _money_text(option["price"])]
			button.tooltip_text = "Carries %d, costs %s a day to run." % [option["capacity"], _money_text(option["upkeep"])]
			button.pressed.connect(func() -> void: line_action.emit("buy_train", id, option["id"]))
			buttons.add_child(button)
	else:
		for model in _models:
			var button := Button.new()
			button.text = "+ %s (%s)" % [model["name"], _money_text(model["price"])]
			button.pressed.connect(func() -> void: line_action.emit("buy", id, model["id"]))
			buttons.add_child(button)
	var separator := HSeparator.new()
	_lines_box.add_child(row)
	_lines_box.add_child(separator)
	_line_rows[id] = {"row": row, "label": label, "separator": separator, "swatch": swatch, "name": name_edit, "stops": stops,
		"choice": choice, "stop_ids": [], "stop_names": [], "color": swatch.color, "rail": rail, "policy": policy}


## One row per company vehicle: what and where it is, what it carries and what it has earned, with buttons to go to it or sell it.
func show_vehicles(vehicles: Array) -> void:
	var seen := {}
	for vehicle in vehicles:
		var id: int = vehicle["id"]
		seen[id] = true
		if not _vehicle_rows.has(id):
			_add_vehicle_row(id, vehicle)
		var entry: Dictionary = _vehicle_rows[id]
		var carrying := "empty"
		if vehicle["load"] > 0:
			carrying = "%d of %d %s" % [vehicle["load"], vehicle["capacity"], String(vehicle["cargo"]).to_lower()]
		var label: Label = entry["label"]
		label.text = "%s %d   %s\n%s\ncarrying %s\nearned %s   upkeep %s   profit %s" % [
			vehicle["model"], id + 1 if id >= 0 else -id, vehicle["line_name"], vehicle["state"], carrying,
			_money_text(vehicle["income"]), _money_text(vehicle["upkeep"]), _money_text(vehicle["profit"])]
		label.add_theme_color_override("font_color", vehicle["color"])
		_set_condition_bar(entry["condition"], vehicle["condition"])
		var upkeep_note := "last repaired %d days ago" % vehicle["days_since"]
		if vehicle["patched"]:
			upkeep_note += "   patched: wears faster until a service"
		entry["repair"].text = upkeep_note if vehicle["repair"] == "" else upkeep_note + "\n" + vehicle["repair"]
		entry["ask"].disabled = vehicle["repair_phase"] != 0 or vehicle["wrecked"]
		_show_policy(entry["policy"], vehicle["policy"], "Maintenance: %s (from the %s)" % [vehicle["policy_text"], vehicle["policy_source"]])
		entry["go"].pressed.disconnect(entry["go_call"])
		entry["go_call"] = func() -> void: town_focus.emit(vehicle["x"], vehicle["z"])
		entry["go"].pressed.connect(entry["go_call"])
		entry["sell"].disabled = vehicle["wrecked"]
	for id in _vehicle_rows.keys():
		if not seen.has(id):
			_vehicle_rows[id]["row"].queue_free()
			_vehicle_rows[id]["separator"].queue_free()
			_vehicle_rows.erase(id)
	_vehicles_empty.visible = _vehicle_rows.is_empty()


func _add_vehicle_row(id: int, vehicle: Dictionary) -> void:
	var row := VBoxContainer.new()
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(label)
	var condition_row := HBoxContainer.new()
	row.add_child(condition_row)
	var caption := Label.new()
	caption.theme_type_variation = "Dim"
	caption.text = "Condition"
	condition_row.add_child(caption)
	var condition := _make_condition_bar()
	condition_row.add_child(condition["bar"])
	var repair_note := Label.new()
	repair_note.theme_type_variation = "Dim"
	repair_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(repair_note)
	var buttons := HBoxContainer.new()
	row.add_child(buttons)
	var go := Button.new()
	go.text = "Go to"
	var go_call := func() -> void: town_focus.emit(vehicle["x"], vehicle["z"])
	go.pressed.connect(go_call)
	buttons.add_child(go)
	var depot := Button.new()
	depot.text = "To depot"
	depot.tooltip_text = "Take it off its line: it drives to the nearest depot and waits there, ready for another line. What it carries is lost."
	depot.pressed.connect(func() -> void: vehicle_action.emit("depot", id))
	buttons.add_child(depot)
	var sell := Button.new()
	sell.text = "Sell"
	sell.tooltip_text = "Sell the vehicle for part of its price. What it carries is lost."
	sell.pressed.connect(func() -> void: vehicle_action.emit("sell", id))
	buttons.add_child(sell)
	var repairs := HBoxContainer.new()
	row.add_child(repairs)
	var kind := OptionButton.new()
	for i in REPAIR_NAMES.size():
		kind.add_item(REPAIR_NAMES[i], i)
	kind.select(1)
	kind.tooltip_text = "The repair to ask for."
	repairs.add_child(kind)
	var ask := Button.new()
	ask.text = "Repair at next stop"
	ask.tooltip_text = "At its next stop it unloads, loads nothing and goes to the nearest depot that can do this repair, then comes back to its line."
	ask.pressed.connect(func() -> void: repair_requested.emit(id, kind.get_selected_id()))
	repairs.add_child(ask)
	var policy := _make_policy_block(2, id)
	row.add_child(policy["box"])
	var separator := HSeparator.new()
	_vehicles_box.add_child(row)
	_vehicles_box.add_child(separator)
	_vehicle_rows[id] = {"row": row, "label": label, "separator": separator, "go": go, "go_call": go_call, "sell": sell,
		"condition": condition, "repair": repair_note, "ask": ask, "policy": policy}


func _rebuild_buy_buttons() -> void:
	for child in _garage_buy_box.get_children():
		child.queue_free()
	var index := _garage_depot.get_selected()
	if index < 0 or index >= _garage_depots.size():
		return
	var depot: Dictionary = _garage_depots[index]
	var depot_id: int = depot["id"]
	var rail: bool = depot["rail"]
	if rail:
		for option in _train_options:
			var button := Button.new()
			button.text = "+ %s (%s)" % [option["label"], _money_text(option["price"])]
			button.tooltip_text = "Carries %d, costs %s a day to run." % [option["capacity"], _money_text(option["upkeep"])]
			button.pressed.connect(func() -> void: garage_buy.emit(true, depot_id, option["id"]))
			_garage_buy_box.add_child(button)
	else:
		for model in _models:
			var button := Button.new()
			button.text = "+ %s (%s)" % [model["name"], _money_text(model["price"])]
			button.tooltip_text = "Carries %d, costs %s a day to run." % [model["capacity"], _money_text(model["upkeep"])]
			button.pressed.connect(func() -> void: garage_buy.emit(false, depot_id, model["id"]))
			_garage_buy_box.add_child(button)


## The depot choice and the vehicles waiting in depots (with a choice of line for each), from the host's depots, parked vehicles and lines.
func show_garage(depots: Array, parked: Array, lines: Array) -> void:
	var key := ""
	for depot in depots:
		key += "%s%d;" % ["r" if depot["rail"] else "d", depot["id"]]
	if key != _garage_depot_key:
		var previous := ""
		var shown := _garage_depot.get_selected()
		if shown >= 0 and shown < _garage_depots.size():
			previous = _garage_depots[shown]["name"]
		_garage_depot_key = key
		_garage_depots = depots
		_garage_depot.clear()
		var keep := 0
		for i in depots.size():
			_garage_depot.add_item(depots[i]["name"])
			if depots[i]["name"] == previous:
				keep = i
		if not depots.is_empty():
			_garage_depot.select(keep)
		_garage_depot.visible = not depots.is_empty()
		_garage_hint.visible = depots.is_empty()
		_rebuild_buy_buttons()

	var seen := {}
	for entry in parked:
		var id: int = entry["id"]
		seen[id] = true
		if not _parked_rows.has(id):
			_add_parked_row(id)
		var row: Dictionary = _parked_rows[id]
		var label: Label = row["label"]
		label.text = "%s   waiting in %s\nworth %s new   Condition %d" % [entry["model"], entry["depot"], _money_text(entry["price"]), roundi(entry["condition"])]
		if entry["repair"] != "":
			label.text += "\n" + entry["repair"]
		var repair_kind: OptionButton = row["repair_kind"]
		for i in REPAIR_NAMES.size():
			repair_kind.set_item_disabled(i, not entry["can_repair"][i])
			repair_kind.set_item_text(i, "%s (%s)" % [REPAIR_NAMES[i], _money_text(entry["repair_price"][i])])
		row["repair_button"].disabled = entry["repair"] != ""
		var rail: bool = entry["rail"]
		var offered := []
		var lines_key := ""
		for line in lines:
			if line["rail"] == rail:
				offered.append(line)
				lines_key += "%d:%s;" % [line["id"], line["name"]]
		var choice: OptionButton = row["choice"]
		if row["lines_key"] != lines_key and not choice.get_popup().visible:
			row["lines_key"] = lines_key
			var selected := choice.get_selected_id()
			choice.clear()
			for line in offered:
				choice.add_item(line["name"], line["id"])
			if choice.item_count > 0:
				choice.select(maxi(0, choice.get_item_index(selected)))
		row["assign"].disabled = choice.item_count == 0
		row["assign"].tooltip_text = "Send it out of its depot to work this line." if choice.item_count > 0 else "Make a %s line first." % ("rail" if rail else "road")
		row["go"].pressed.disconnect(row["go_call"])
		row["go_call"] = func() -> void: town_focus.emit(entry["x"], entry["z"])
		row["go"].pressed.connect(row["go_call"])
	for id in _parked_rows.keys():
		if not seen.has(id):
			_parked_rows[id]["row"].queue_free()
			_parked_rows[id]["separator"].queue_free()
			_parked_rows.erase(id)
	_parked_header.visible = not _parked_rows.is_empty()


func _add_parked_row(id: int) -> void:
	var row := VBoxContainer.new()
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(label)
	var buttons := HBoxContainer.new()
	row.add_child(buttons)
	var choice := OptionButton.new()
	choice.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	buttons.add_child(choice)
	var assign := Button.new()
	assign.text = "Put on line"
	assign.pressed.connect(func() -> void:
		if choice.item_count > 0:
			garage_assign.emit(id, choice.get_selected_id()))
	buttons.add_child(assign)
	var go := Button.new()
	go.text = "Go to"
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	buttons.add_child(go)
	var sell := Button.new()
	sell.text = "Sell"
	sell.tooltip_text = "Sell it for part of its price."
	sell.pressed.connect(func() -> void: garage_sell.emit(id))
	buttons.add_child(sell)
	var repairs := HBoxContainer.new()
	row.add_child(repairs)
	var repair_kind := OptionButton.new()
	for i in REPAIR_NAMES.size():
		repair_kind.add_item(REPAIR_NAMES[i], i)
	repair_kind.select(1)
	repair_kind.tooltip_text = "Greyed out repairs need a better depot (see the Depots window)."
	repair_kind.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	repairs.add_child(repair_kind)
	var repair_button := Button.new()
	repair_button.text = "Repair"
	repair_button.tooltip_text = "Take it into a bay of the depot it stands in."
	repair_button.pressed.connect(func() -> void: parked_repair.emit(id, repair_kind.get_selected_id()))
	repairs.add_child(repair_button)
	var separator := HSeparator.new()
	_parked_box.add_child(row)
	_parked_box.add_child(separator)
	_parked_rows[id] = {"row": row, "separator": separator, "label": label, "choice": choice, "lines_key": "", "assign": assign, "go": go, "go_call": go_call,
		"repair_kind": repair_kind, "repair_button": repair_button}


## The Maintenance window: the company policy, the option for random events, the totals, the repairs and the fleet from the worst Condition up.
func show_maintenance(maint: Dictionary, vehicles: Array) -> void:
	_show_policy(_maintenance_policy, maint["company"], "Now: %s" % maint["company_text"])
	if _maintenance_frequency.get_selected_id() != maint["frequency"] and not _maintenance_frequency.get_popup().visible:
		_maintenance_frequency.select(_maintenance_frequency.get_item_index(maint["frequency"]))
	_maintenance_totals.text = "Spent on maintenance   %s\nCompensation paid   %s   (%d people injured)\nFines   %s" % [
		_money_text(maint["maintenance"]), _money_text(maint["compensation"]), maint["injured"], _money_text(maint["fines"])]
	var rules := ""
	for repair in maint["repairs"]:
		rules += "%s: +%d Condition (up to %d), %d s in a bay, %d%% of the price, from a %s\n" % [
			repair["name"], roundi(repair["gain"]), roundi(repair["ceiling"]), roundi(repair["seconds"]), roundi(100.0 * repair["share"]), String(repair["level_name"]).to_lower() if repair["level"] > 0 else "depot of any level"]
	rules += "A breakdown below Condition %d counts as negligence and is fined." % roundi(maint["critical"])
	_maintenance_rules.text = rules

	var ordered: Array = vehicles.duplicate()
	ordered.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return a["condition"] < b["condition"])
	var seen := {}
	for vehicle in ordered:
		var id: int = vehicle["id"]
		seen[id] = true
		if not _fleet_rows.has(id):
			_add_fleet_row(id)
		var entry: Dictionary = _fleet_rows[id]
		entry["label"].text = "%s %d   %s" % [vehicle["model"], id + 1 if id >= 0 else -id, vehicle["line_name"]]
		entry["label"].add_theme_color_override("font_color", vehicle["color"])
		_set_condition_bar(entry["bar"], vehicle["condition"])
		entry["go"].pressed.disconnect(entry["go_call"])
		entry["go_call"] = func() -> void: town_focus.emit(vehicle["x"], vehicle["z"])
		entry["go"].pressed.connect(entry["go_call"])
	for id in _fleet_rows.keys():
		if not seen.has(id):
			_fleet_rows[id]["row"].queue_free()
			_fleet_rows.erase(id)
	for i in ordered.size():
		_maintenance_fleet.move_child(_fleet_rows[ordered[i]["id"]]["row"], i)
	_maintenance_empty.visible = _fleet_rows.is_empty()


## The worn roads of the Maintenance window, worst first (from the host's worn roads).
func show_worn_roads(roads: Array) -> void:
	var seen := {}
	for road in roads:
		var id: int = road["road"]
		seen[id] = true
		if not _road_rows.has(id):
			_add_road_row(id)
		var entry: Dictionary = _road_rows[id]
		entry["label"].text = "Road %d   %d m" % [id + 1, roundi(road["length"])]
		_set_condition_bar(entry["bar"], road["condition"])
		entry["resurface"].text = "Resurface (%s)" % _money_text(road["price"])
		entry["resurface"].disabled = road["why"] != ""
		entry["resurface"].tooltip_text = road["why"] if road["why"] != "" else "A crew restores the road; it is closed while they work."
		entry["go"].pressed.disconnect(entry["go_call"])
		entry["go_call"] = func() -> void: town_focus.emit(road["x"], road["z"])
		entry["go"].pressed.connect(entry["go_call"])
	for id in _road_rows.keys():
		if not seen.has(id):
			_road_rows[id]["row"].queue_free()
			_road_rows.erase(id)
	for i in roads.size():
		_roads_box.move_child(_road_rows[roads[i]["road"]]["row"], i)
	_roads_empty.visible = _road_rows.is_empty()


func _add_road_row(id: int) -> void:
	var row := VBoxContainer.new()
	var top := HBoxContainer.new()
	row.add_child(top)
	var label := Label.new()
	label.custom_minimum_size = Vector2(150, 0)
	top.add_child(label)
	var bar := _make_condition_bar()
	top.add_child(bar["bar"])
	var buttons := HBoxContainer.new()
	row.add_child(buttons)
	var go := Button.new()
	go.text = "Go to"
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	buttons.add_child(go)
	var resurface := Button.new()
	resurface.pressed.connect(func() -> void: resurface_requested.emit(id))
	buttons.add_child(resurface)
	_roads_box.add_child(row)
	_road_rows[id] = {"row": row, "label": label, "bar": bar, "go": go, "go_call": go_call, "resurface": resurface}


func _add_fleet_row(id: int) -> void:
	var row := HBoxContainer.new()
	var label := Label.new()
	label.custom_minimum_size = Vector2(190, 0)
	row.add_child(label)
	var bar := _make_condition_bar()
	row.add_child(bar["bar"])
	var go := Button.new()
	go.text = "Go to"
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	row.add_child(go)
	_maintenance_fleet.add_child(row)
	_fleet_rows[id] = {"row": row, "label": label, "bar": bar, "go": go, "go_call": go_call}


## The Depots window: for each depot its level and bays, what is being repaired and who waits, and the buttons that raise its level.
func show_depots(depots: Array) -> void:
	var seen := {}
	for depot in depots:
		var key := "%s%d" % ["r" if depot["rail"] else "d", depot["id"]]
		seen[key] = true
		if not _depot_rows.has(key):
			_add_depot_row(key)
		var entry: Dictionary = _depot_rows[key]
		entry["label"].text = "%s   %s, %d bays" % [depot["name"], depot["level_name"], depot["bays"]]
		var jobs := ""
		for job in depot["jobs"]:
			jobs += "%s: %s%s\n" % [job["label"], String(REPAIR_NAMES[job["kind"]]).to_lower(), (" (%d s left)" % ceili(job["seconds"])) if job["working"] else ", waiting for a bay"]
		entry["jobs"].text = jobs.strip_edges()
		entry["jobs"].visible = jobs != ""
		var upgrade_key := ""
		for upgrade in depot["upgrades"]:
			upgrade_key += "%d:%d;" % [upgrade["level"], upgrade["price"]]
		if upgrade_key != entry["upgrade_key"]:
			entry["upgrade_key"] = upgrade_key
			for child in entry["upgrades"].get_children():
				child.queue_free()
			for upgrade in depot["upgrades"]:
				var button := Button.new()
				button.text = "Upgrade to %s (%s)" % [upgrade["name"], _money_text(upgrade["price"])]
				button.tooltip_text = "Pay the difference in price; the depot keeps its place."
				var rail: bool = depot["rail"]
				var depot_id: int = depot["id"]
				var level: int = upgrade["level"]
				button.pressed.connect(func() -> void: depot_upgrade.emit(rail, depot_id, level))
				entry["upgrades"].add_child(button)
		entry["go"].pressed.disconnect(entry["go_call"])
		entry["go_call"] = func() -> void: town_focus.emit(depot["x"], depot["z"])
		entry["go"].pressed.connect(entry["go_call"])
	for key in _depot_rows.keys():
		if not seen.has(key):
			_depot_rows[key]["row"].queue_free()
			_depot_rows[key]["separator"].queue_free()
			_depot_rows.erase(key)
	_depots_empty.visible = _depot_rows.is_empty()


func _add_depot_row(key: String) -> void:
	var row := VBoxContainer.new()
	var label := Label.new()
	row.add_child(label)
	var jobs := Label.new()
	jobs.theme_type_variation = "Dim"
	jobs.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(jobs)
	var buttons := HFlowContainer.new()
	row.add_child(buttons)
	var upgrades := HBoxContainer.new()
	buttons.add_child(upgrades)
	var go := Button.new()
	go.text = "Go to"
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	buttons.add_child(go)
	var separator := HSeparator.new()
	_depots_box.add_child(row)
	_depots_box.add_child(separator)
	_depot_rows[key] = {"row": row, "separator": separator, "label": label, "jobs": jobs, "upgrades": upgrades, "upgrade_key": "?", "go": go, "go_call": go_call}


## New entries of the event feed (from the host): serious ones also show as a message; a click on one moves the camera to the place.
func show_events(events: Array) -> void:
	for event in events:
		var button := Button.new()
		button.text = event["text"]
		button.alignment = HORIZONTAL_ALIGNMENT_LEFT
		button.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		button.custom_minimum_size = Vector2(440, 0)
		button.tooltip_text = "Click to go there."
		var colour := UiTheme.TEXT
		if event["severity"] == 2:
			colour = Color(1.0, 0.5, 0.45)
		elif event["severity"] == 1:
			colour = Color(1.0, 0.85, 0.4)
		button.add_theme_color_override("font_color", colour)
		button.add_theme_color_override("font_hover_color", colour.lightened(0.3))
		var x: float = event["x"]
		var z: float = event["z"]
		button.pressed.connect(func() -> void: town_focus.emit(x, z))
		_feed.add_child(button)
		_feed_items.append({"node": button, "age": 0.0, "lifetime": 90.0 if event["severity"] == 2 else 45.0})
		if event["severity"] == 2:
			show_message(event["text"])
	while _feed_items.size() > FEED_MAX:
		_feed_items[0]["node"].queue_free()
		_feed_items.remove_at(0)


func _remove_row(id: int) -> void:
	var entry: Dictionary = _line_rows[id]
	entry["row"].queue_free()
	entry["separator"].queue_free()
	_line_rows.erase(id)


func _money_text(amount: Variant) -> String:
	var value := int(amount)
	var digits := str(absi(value))
	var out := ""
	for i in digits.length():
		if i > 0 and (digits.length() - i) % 3 == 0:
			out += ","
		out += digits[i]
	return ("-" if value < 0 else "") + out
