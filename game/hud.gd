extends CanvasLayer
## Heads-up display: speed buttons and live statistics. Emits intent, owns no game state.

signal speed_selected(speed: float)
signal tool_selected(tool_name: String)
signal action_requested(action: String)
## A line row button: kind is "buy" (with a vehicle model id) or "delete".
signal line_action(kind: String, line: int, model: String)

var _stats: Label
var _buttons: Dictionary = {}
var _tool_buttons: Dictionary = {}
var _message: Label
var _company: Label
var _lines_box: VBoxContainer
var _line_rows: Dictionary = {} # line id -> {"row": Control, "label": Label}
var _models: Array = []


func _ready() -> void:
	var panel := PanelContainer.new()
	panel.position = Vector2(12, 12)
	add_child(panel)

	var column := VBoxContainer.new()
	panel.add_child(column)

	var row := HBoxContainer.new()
	column.add_child(row)
	for entry in [["||", 0.0], ["1x", 1.0], ["2x", 2.0], ["4x", 4.0], ["8x", 8.0]]:
		var button := Button.new()
		button.text = entry[0]
		button.toggle_mode = true
		button.pressed.connect(_on_speed_pressed.bind(entry[1]))
		row.add_child(button)
		_buttons[entry[1]] = button

	var tools := HBoxContainer.new()
	column.add_child(tools)
	for entry in [["Pointer", "pointer"], ["2-lane road", "road2"], ["2x2 avenue", "road4"], ["Curve", "curve2"], ["Curved avenue", "curve4"], ["Upgrade", "upgrade"], ["Downgrade", "downgrade"], ["Faster", "faster"], ["Slower", "slower"], ["Move", "move"], ["Remove", "remove"], ["Depot", "depot"], ["Station", "station"], ["New line", "line"]]:
		var button := Button.new()
		button.text = entry[0]
		button.toggle_mode = true
		button.pressed.connect(_on_tool_pressed.bind(entry[1]))
		tools.add_child(button)
		_tool_buttons[entry[1]] = button
	select_tool("pointer")

	var actions := HBoxContainer.new()
	column.add_child(actions)
	for entry in [["+100 cars", "traffic"], ["Crash!", "crash"], ["Borrow 100k", "loan"], ["Repay 100k", "repay"], ["Empty map", "empty"], ["Hills map", "hills"], ["Demo map", "demo"]]:
		var button := Button.new()
		button.text = entry[0]
		button.pressed.connect(func() -> void: action_requested.emit(entry[1]))
		actions.add_child(button)

	_company = Label.new()
	column.add_child(_company)
	_stats = Label.new()
	column.add_child(_stats)
	_message = Label.new()
	column.add_child(_message)
	highlight_speed(1.0)

	var lines_panel := PanelContainer.new()
	lines_panel.anchor_left = 1.0
	lines_panel.anchor_right = 1.0
	lines_panel.offset_left = -330.0
	lines_panel.offset_right = -12.0
	lines_panel.offset_top = 12.0
	add_child(lines_panel)
	_lines_box = VBoxContainer.new()
	lines_panel.add_child(_lines_box)
	var title := Label.new()
	title.text = "Lines"
	_lines_box.add_child(title)


func select_tool(tool_name: String) -> void:
	for key in _tool_buttons:
		_tool_buttons[key].set_pressed_no_signal(key == tool_name)


func show_message(text: String) -> void:
	_message.text = text


func _on_tool_pressed(tool_name: String) -> void:
	select_tool(tool_name)
	tool_selected.emit(tool_name)


func highlight_speed(speed: float) -> void:
	for key in _buttons:
		_buttons[key].set_pressed_no_signal(is_equal_approx(key, speed))


func show_stats(stats: Dictionary) -> void:
	_stats.text = "%d fps\nvehicles %d   overtaking %d\nmean speed %.1f m/s\nsim speed x%.2f   step %.2f ms\nincidents %d (open %d)   wrecks %d" % [
		Engine.get_frames_per_second(),
		stats["vehicles"], stats["overtaking"],
		stats["mean_speed"],
		stats["effective_speed"], stats["step_ms"],
		stats["incidents"], stats["open_incidents"], stats["wrecks"]]


func _on_speed_pressed(speed: float) -> void:
	highlight_speed(speed)
	speed_selected.emit(speed)


## Company summary: money, loan, day, and how it earns.
func show_company(company: Dictionary) -> void:
	_company.text = "Money %s   Loan %s / %s   Day %d%s\nIncome %s   Upkeep %s   Interest %s" % [
		_money(company["money"]), _money(company["loan"]), _money(company["max_loan"]), company["day"],
		"   BANKRUPT" if company["bankrupt"] else "",
		_money(company["income"]), _money(-company["upkeep"]), _money(-company["interest"])]


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
			_money(line["income"]), _money(line["upkeep"]), _money(line["profit"])]
		label.add_theme_color_override("font_color", line["color"])
	for id in _line_rows.keys():
		if not seen.has(id):
			_remove_row(id)


func _add_row(id: int) -> void:
	var row := VBoxContainer.new()
	var label := Label.new()
	row.add_child(label)
	var buttons := HBoxContainer.new()
	row.add_child(buttons)
	for model in _models:
		var button := Button.new()
		button.text = "+ %s (%s)" % [model["name"], _money(model["price"])]
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


func _money(amount: Variant) -> String:
	var value := int(amount)
	var digits := str(absi(value))
	var out := ""
	for i in digits.length():
		if i > 0 and (digits.length() - i) % 3 == 0:
			out += ","
		out += digits[i]
	return ("-" if value < 0 else "") + out
