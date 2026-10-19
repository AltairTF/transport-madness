extends CanvasLayer
## Heads-up display. Top bar: menu, money, speed and the windows (Company, Lines, ...). Bottom: the tools, grouped
## in categories. Right: one window at a time. Left: the debug panel (F3). Emits intent, owns no game state.

signal speed_selected(speed: float)
signal tool_selected(tool_name: String)
signal action_requested(action: String)
## A line row button: kind is "buy" (with a vehicle model id) or "delete".
signal line_action(kind: String, line: int, model: String)
signal menu_requested

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
		["Remove", "remove", "Demolish a piece of road."]]},
	{"name": "Transport", "tools": [
		["Depot", "depot", "Build a depot at a junction: vehicles are bought and repaired there."],
		["Station", "station", "Build a station on a road."],
		["New line", "line", "Click stations in order, then press Enter."]]},
]
## Windows opened from the top bar. A page of null text is built by its own code (Company, Lines).
const WINDOWS := [
	["Company", "company", ""],
	["Lines", "lines", ""],
	["Vehicles", "vehicles", "The whole fleet in one list: condition, age, where each vehicle is, sell and replace. Planned."],
	["Stations", "stations", "Every station with its waiting cargo and ratings. Planned."],
	["Towns", "towns", "Towns, their growth and what they need. Arrives with M3."],
	["Industries", "industries", "Industries and the cargo chains between them. Arrives with M3."],
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
	for entry in [["+100 cars", "traffic"], ["Crash!", "crash"], ["Empty map", "empty"], ["Hills map", "hills"], ["Demo map", "demo"]]:
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
	_money.text = "Money %s   Day %d%s" % [_money_text(company["money"]), company["day"], "   BANKRUPT" if company["bankrupt"] else ""]
	_money.add_theme_color_override("font_color", Color(1.0, 0.45, 0.4) if company["bankrupt"] or company["money"] < 0 else UiTheme.TEXT)
	_company_details.text = "Money   %s\nLoan   %s of %s\nDay   %d\n\nIncome   %s\nUpkeep   %s\nInterest   %s" % [
		_money_text(company["money"]), _money_text(company["loan"]), _money_text(company["max_loan"]), company["day"],
		_money_text(company["income"]), _money_text(-company["upkeep"]), _money_text(-company["interest"])]


## Models the company can buy, for the buy buttons of the line rows (call before show_lines).
func set_models(models: Array) -> void:
	_models = models
	for id in _line_rows.keys():
		_remove_row(id)


## One row per line: its colour, stations, fleet and money, with buttons to buy vehicles for it or delete it.
func show_lines(lines: Array) -> void:
	var seen := {}
	for line in lines:
		var id: int = line["id"]
		seen[id] = true
		if not _line_rows.has(id):
			_add_row(id)
		var stations := ""
		for station_name in line["stations"]:
			stations += ("" if stations.is_empty() else " - ") + station_name
		var label: Label = _line_rows[id]["label"]
		label.text = "Line %d  %s\n%d vehicles (%d loading)   delivered %d\nincome %s   upkeep %s   profit %s" % [
			id + 1, stations, line["vehicles"], line["loading"], line["delivered"],
			_money_text(line["income"]), _money_text(line["upkeep"]), _money_text(line["profit"])]
		label.add_theme_color_override("font_color", line["color"])
	for id in _line_rows.keys():
		if not seen.has(id):
			_remove_row(id)
	_lines_empty.visible = _line_rows.is_empty()


func _add_row(id: int) -> void:
	var row := VBoxContainer.new()
	var label := Label.new()
	row.add_child(label)
	var buttons := HFlowContainer.new()
	row.add_child(buttons)
	for model in _models:
		var button := Button.new()
		button.text = "+ %s (%s)" % [model["name"], _money_text(model["price"])]
		button.pressed.connect(func() -> void: line_action.emit("buy", id, model["id"]))
		buttons.add_child(button)
	var remove_button := Button.new()
	remove_button.text = "Delete"
	remove_button.pressed.connect(func() -> void: line_action.emit("delete", id, ""))
	buttons.add_child(remove_button)
	var separator := HSeparator.new()
	_lines_box.add_child(row)
	_lines_box.add_child(separator)
	_line_rows[id] = {"row": row, "label": label, "separator": separator}


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
