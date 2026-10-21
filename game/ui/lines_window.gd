extends "res://game/ui/page.gd"
## Network > Lines: one row per line: its colour and name, stops (with buttons to reorder, drop or add one), fleet and money, its
## maintenance policy, and buttons to buy vehicles for it or delete it.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # line id -> {"row", "label", "separator", "swatch", "name", "stops", "choice", "stop_ids", "stop_names", "color", "rail", "policy"}


func _build() -> void:
	_box = Kit.column()
	add_child(_box)
	_empty = Kit.note("No lines yet. Build a depot and two stations, then use Transport > New line.")
	add_child(_empty)


## The models or trains on offer changed: rows are made again with their new buy buttons.
func reset_rows() -> void:
	for id in _rows.keys():
		_remove(id)


func show_lines(lines: Array) -> void:
	var seen := {}
	for line in lines:
		var id: int = line["id"]
		seen[id] = true
		if not _rows.has(id):
			_add(id, line.get("rail", false))
		var entry: Dictionary = _rows[id]
		var colour: Color = line["color"]
		entry["color"] = colour
		if not entry["swatch"].get_popup().visible: # not while the player is choosing
			entry["swatch"].color = colour
		var name_edit: LineEdit = entry["name"]
		if not name_edit.has_focus() and name_edit.text != line["name"]:
			name_edit.text = line["name"]
		var label: Label = entry["label"]
		label.text = "%d vehicles (%d loading)   delivered %d\nincome %s   upkeep %s   profit %s" % [
			line["vehicles"], line["loading"], line["delivered"], Kit.money(line["income"]), Kit.money(line["upkeep"]), Kit.money(line["profit"])]
		label.add_theme_color_override("font_color", colour)
		Kit.show_policy(entry["policy"], line["policy"], "Maintenance of its vehicles: %s" % line["policy_text"])
		_show_stops(id, line["stop_ids"], line["stations"])
		_fill_stop_choices(id)
	for id in _rows.keys():
		if not seen.has(id):
			_remove(id)
	_empty.visible = _rows.is_empty()


## The stops of a line as rows with up, down and remove buttons; made again only when the stops change.
func _show_stops(id: int, stop_ids: Array, stop_names: Array) -> void:
	var entry: Dictionary = _rows[id]
	if entry["stop_ids"] == stop_ids and entry["stop_names"] == stop_names:
		return
	entry["stop_ids"] = stop_ids.duplicate()
	entry["stop_names"] = stop_names.duplicate()
	var box: VBoxContainer = entry["stops"]
	for child in box.get_children():
		box.remove_child(child)
		child.queue_free()
	for i in stop_ids.size():
		var stop := Kit.row()
		var caption := Label.new()
		caption.text = "%d  %s" % [i + 1, stop_names[i]]
		caption.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		stop.add_child(caption)
		for action in [["Up", -1], ["Down", 1], ["X", 0]]:
			var b := Kit.button(action[0], _edit_stops.bind(id, i, action[1]),
				{"Up": "Move this stop earlier", "Down": "Move this stop later", "X": "Take this stop off the line"}[action[0]])
			b.disabled = (action[1] == -1 and i == 0) or (action[1] == 1 and i == stop_ids.size() - 1)
			stop.add_child(b)
		box.add_child(stop)


## Moves a stop one place (-1 earlier, 1 later) or takes it off (0) and asks for the line to be changed.
func _edit_stops(id: int, index: int, move: int) -> void:
	var stops: Array = _rows[id]["stop_ids"].duplicate()
	if move == 0:
		stops.remove_at(index)
	else:
		var other := index + move
		var kept: int = stops[index]
		stops[index] = stops[other]
		stops[other] = kept
	hud.line_edit.emit(id, stops)


## Fills the "add a stop" choice of a line with the open stations (again only when they have changed).
func _fill_stop_choices(id: int) -> void:
	var choice: OptionButton = _rows[id]["choice"]
	var rail: bool = _rows[id]["rail"]
	var offered: Array = hud.stations_known.filter(func(station: Dictionary) -> bool: return station["rail"] == rail) # a line is all road or all rail
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


func _add(id: int, rail := false) -> void:
	var row := Kit.column()
	var header := Kit.row()
	row.add_child(header)
	var swatch := ColorPickerButton.new()
	swatch.custom_minimum_size = Vector2(30, 20)
	swatch.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	swatch.edit_alpha = false
	swatch.tooltip_text = "Pick a colour for this line and its vehicles."
	swatch.popup_closed.connect(func() -> void:
		var shown: Color = _rows[id]["color"]
		if not swatch.color.is_equal_approx(shown):
			hud.line_color.emit(id, swatch.color))
	header.add_child(swatch)
	var name_edit := LineEdit.new()
	name_edit.max_length = 24
	name_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	name_edit.tooltip_text = "Click to rename the line, Enter to keep the name."
	name_edit.text_submitted.connect(func(text: String) -> void:
		hud.line_rename.emit(id, text)
		name_edit.release_focus())
	name_edit.focus_exited.connect(func() -> void: hud.line_rename.emit(id, name_edit.text))
	header.add_child(name_edit)
	header.add_child(Kit.button("Delete", func() -> void: hud.line_action.emit("delete", id, ""), "Close the line and sell its vehicles."))
	var label := Label.new()
	row.add_child(label)
	var policy := Kit.make_policy_block(1, id, hud.policy_set.emit)
	row.add_child(policy["box"])
	var stops := Kit.column()
	row.add_child(stops)
	var add := Kit.row()
	row.add_child(add)
	var choice := OptionButton.new()
	choice.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add.add_child(choice)
	add.add_child(Kit.button("Add stop", func() -> void:
		if choice.item_count == 0:
			return
		var stop_ids: Array = _rows[id]["stop_ids"].duplicate()
		stop_ids.append(choice.get_selected_id())
		hud.line_edit.emit(id, stop_ids), "Add the chosen station at the end of the line."))
	var buttons := Kit.flow()
	row.add_child(buttons)
	if rail:
		for option in hud.train_options:
			buttons.add_child(Kit.button("+ %s (%s)" % [option["label"], Kit.money(option["price"])], func() -> void: hud.line_action.emit("buy_train", id, option["id"]),
				"Carries %d, costs %s a day to run." % [option["capacity"], Kit.money(option["upkeep"])]))
	else:
		for model in hud.models:
			buttons.add_child(Kit.button("+ %s (%s)" % [model["name"], Kit.money(model["price"])], func() -> void: hud.line_action.emit("buy", id, model["id"])))
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "label": label, "separator": separator, "swatch": swatch, "name": name_edit, "stops": stops,
		"choice": choice, "stop_ids": [], "stop_names": [], "color": swatch.color, "rail": rail, "policy": policy}


func _remove(id: int) -> void:
	_rows[id]["row"].queue_free()
	_rows[id]["separator"].queue_free()
	_rows.erase(id)
