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
signal menu_requested
## The Towns window asks the camera to go to a town.
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
		["Remove", "remove", "Demolish a piece of road."],
		["Traffic light", "light", "Put traffic lights at a junction: the two crossing streets take turns."],
		["Remove light", "light_remove", "Take traffic lights down."]]},
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
	["Stations", "stations", ""],
	["Towns", "towns", ""],
	["Industries", "industries", ""],
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
var _industries_box: VBoxContainer
var _industries_empty: Label
var _industry_rows: Dictionary = {} # industry id -> {"row": Control, "label": Label, "separator": Control}
var _stations_box: VBoxContainer
var _stations_empty: Label
var _station_rows: Dictionary = {} # station id -> {"row": Control, "label": Label, "separator": Control}
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
	_build_debug_panel()
	_build_bottom()
	select_tool("pointer")
	highlight_speed(1.0)


func _process(delta: float) -> void:
	if _message.visible:
		_message_age += delta
		if _message_age > MESSAGE_SECONDS:
			_message.visible = false


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_F3:
		_debug_panel.visible = not _debug_panel.visible


# --- building the layout ---

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
	_money.custom_minimum_size = Vector2(420, 0)
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
			"stations":
				page = _build_stations_page()
			"vehicles":
				page = _build_vehicles_page()
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
	for entry in [["Borrow 100k", "loan"], ["Repay 100k", "repay"]]:
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


func _build_industries_page() -> Control:
	_industries_box = VBoxContainer.new()
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


## Company summary: money and day in the bar, the books in the Company window.
func show_company(company: Dictionary) -> void:
	_money.text = "Money %s   %s%s" % [_money_text(company["money"]), _date_text(company), "   BANKRUPT" if company["bankrupt"] else ""]
	_money.add_theme_color_override("font_color", Color(1.0, 0.45, 0.4) if company["bankrupt"] or company["money"] < 0 else UiTheme.TEXT)
	_company_details.text = "Money   %s\nLoan   %s of %s\nDate   %s\n\nIncome   %s\nUpkeep   %s\nInterest   %s" % [
		_money_text(company["money"]), _money_text(company["loan"]), _money_text(company["max_loan"]), _date_text(company),
		_money_text(company["income"]), _money_text(-company["upkeep"]), _money_text(-company["interest"])]


## "12 Mar, year 2   14:30": the calendar day and the time of day (a day is 10 minutes long).
func _date_text(company: Dictionary) -> String:
	var hour: float = company["hour"]
	return "%d %s, year %d   %02d:%02d" % [company["day_of_month"], MONTHS[(company["month"] - 1) % 12], company["year"], int(hour), int(fmod(hour, 1.0) * 60.0)]


func set_cargo_names(names: Array) -> void:
	_cargo_names = names


## One row per industry: what it makes and holds, with a button that takes the camera there.
func show_industries(industries: Array) -> void:
	var seen := {}
	for industry in industries:
		var id: int = industry["id"]
		seen[id] = true
		if not _industry_rows.has(id):
			_add_industry_row(id, industry)
		var text := "%s %d\nmakes %s   %d of %d waiting for a truck   %d made so far" % [
			industry["name"], id + 1, industry["output"], industry["stock"], industry["max_output"], industry["produced"]]
		for input in industry["inputs"]:
			text += "\nneeds %s   %d of %d in stock" % [input["cargo"], input["stock"], input["max"]]
		_industry_rows[id]["label"].text = text
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
	var go := Button.new()
	go.text = "Go to"
	go.pressed.connect(func() -> void: town_focus.emit(industry["x"], industry["z"]))
	row.add_child(go)
	var separator := HSeparator.new()
	_industries_box.add_child(row)
	_industries_box.add_child(separator)
	_industry_rows[id] = {"row": row, "label": label, "separator": separator}


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
		"choice": choice, "stop_ids": [], "stop_names": [], "color": swatch.color, "rail": rail}


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
	var separator := HSeparator.new()
	_vehicles_box.add_child(row)
	_vehicles_box.add_child(separator)
	_vehicle_rows[id] = {"row": row, "label": label, "separator": separator, "go": go, "go_call": go_call, "sell": sell}


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
		label.text = "%s   waiting in %s\nworth %s new" % [entry["model"], entry["depot"], _money_text(entry["price"])]
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
	var separator := HSeparator.new()
	_parked_box.add_child(row)
	_parked_box.add_child(separator)
	_parked_rows[id] = {"row": row, "separator": separator, "label": label, "choice": choice, "lines_key": "", "assign": assign, "go": go, "go_call": go_call}


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
