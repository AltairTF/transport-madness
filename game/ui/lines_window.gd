extends "res://game/ui/page.gd"
## Network > Lines: one row per line: its colour and name, stops (with buttons to reorder, drop or add one), fleet and money, its
## maintenance policy, and buttons to buy vehicles for it or delete it.

var _box: VBoxContainer
var _tabs: HFlowContainer
const GAP_BUY := 10
var _selected := -1
var _empty: Label
var _rows: Dictionary = {} # line id -> {"row", "label", "separator", "swatch", "name", "stops", "choice", "stop_ids", "stop_names", "color", "rail", "policy"}


func _build() -> void:
	_tabs = Kit.flow()
	add_child(_tabs)
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
		var stats: Dictionary = entry["stats"]
		stats["vehicles"]["value"].text = str(line["vehicles"])
		stats["vehicles"]["loading"].text = "%d loading" % line["loading"]
		stats["delivered"]["value"].text = str(line["delivered"])
		stats["income"]["value"].text = Kit.money(line["income"])
		stats["upkeep"]["value"].text = Kit.money(line["upkeep"])
		stats["profit"]["value"].text = Kit.money(line["profit"])
		entry["no_vehicles"].visible = line["vehicles"] == 0
		Kit.show_policy(entry["policy"], line["policy"], "Maintenance of its vehicles: %s" % line["policy_text"])
		_show_stops(id, line["stop_ids"], line["stations"])
		if not line.get("rail", false):
			_show_options(id, line["stop_options"])
		_fill_stop_choices(id)
	for id in _rows.keys():
		if not seen.has(id):
			_remove(id)
	if not _rows.has(_selected):
		_selected = _rows.keys()[0] if not _rows.is_empty() else -1
	_show_tabs(lines)
	for id in _rows:
		_rows[id]["row"].visible = id == _selected
		_rows[id]["separator"].visible = false
	_empty.visible = _rows.is_empty()


## One pill per line to choose which line the panel shows; made again only when the lines or their names or colours change.
func _show_tabs(lines: Array) -> void:
	var signature := str(_selected)
	for line in lines:
		signature += "|%d%s%s" % [line["id"], line["name"], line["color"]]
	if _tabs.get_meta("signature", "") == signature:
		return
	_tabs.set_meta("signature", signature)
	for child in _tabs.get_children():
		_tabs.remove_child(child)
		child.queue_free()
	_tabs.visible = lines.size() > 1
	for line in lines:
		var id: int = line["id"]
		var pill := Kit.button("● " + String(line["name"]), func() -> void:
			_selected = id
			show_lines(hud.latest("lines")))
		pill.toggle_mode = true
		pill.button_pressed = id == _selected
		pill.add_theme_color_override("font_color", line["color"])
		_tabs.add_child(pill)


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
	entry["controls"] = []
	for i in stop_ids.size():
		var card := Kit.card()
		var body: VBoxContainer = card.get_meta("body")
		var stop := Kit.row()
		var number := Kit.badge(str(i + 1))
		number.size_flags_vertical = Control.SIZE_SHRINK_CENTER
		stop.add_child(number)
		var caption := Label.new()
		caption.text = stop_names[i]
		caption.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		caption.clip_text = true
		stop.add_child(caption)
		for action in [["▲", -1], ["▼", 1], ["✕", 0]]:
			var b := Kit.button(action[0], _edit_stops.bind(id, i, action[1]),
				{"▲": "Move this stop earlier", "▼": "Move this stop later", "✕": "Take this stop off the line"}[action[0]])
			b.disabled = (action[1] == -1 and i == 0) or (action[1] == 1 and i == stop_ids.size() - 1)
			if action[1] == 0:
				b.theme_type_variation = "DangerButton"
			stop.add_child(b)
		body.add_child(stop)
		box.add_child(card)
		if not entry["rail"]:
			var options := _make_options(id, i)
			body.add_child(options["row"])
			entry["controls"].append(options)
	entry["route_note"].text = "%d stops, then back to stop 1" % stop_ids.size() if stop_ids.size() > 1 else ""


## What a road vehicle does at one stop (M6f): load (and which cargo), unload, and how much of what it took on at the last loading stop it unloads here.
func _make_options(id: int, index: int) -> Dictionary:
	var row := Kit.flow()
	var load_box := CheckBox.new()
	load_box.text = "Load"
	load_box.tooltip_text = "Take cargo on at this stop. Off: the vehicle takes nothing here."
	row.add_child(load_box)
	var cargo := OptionButton.new()
	cargo.add_item("any cargo")
	for name in hud.cargo_names:
		cargo.add_item(String(name).to_lower())
	cargo.tooltip_text = "Which cargo it takes on here, when the place makes more than one."
	row.add_child(cargo)
	var unload_box := CheckBox.new()
	unload_box.text = "Unload"
	unload_box.tooltip_text = "Put cargo down at this stop. Off: what it carries stays aboard."
	row.add_child(unload_box)
	var share_box := CheckBox.new()
	share_box.text = "Share load"
	share_box.tooltip_text = "Unload only this percentage of what the vehicle took on at the last loading stop (the rest goes on to the next stop). Off: unload everything the place takes."
	row.add_child(share_box)
	var share := SpinBox.new()
	share.min_value = 0
	share.max_value = 100
	share.step = 5
	share.value = 100
	var percent := Label.new()
	percent.text = "%"
	row.add_child(share)
	row.add_child(percent)
	var controls := {"row": row, "load": load_box, "cargo": cargo, "unload": unload_box, "share_on": share_box, "share": share}
	var changed := func(_value: Variant = null) -> void: _send_options(id, index)
	load_box.toggled.connect(changed)
	cargo.item_selected.connect(changed)
	unload_box.toggled.connect(changed)
	share_box.toggled.connect(changed)
	share.value_changed.connect(changed)
	return controls


func _send_options(id: int, index: int) -> void:
	var c: Dictionary = _rows[id]["controls"][index]
	hud.line_stop_options.emit(id, index, c["load"].button_pressed, c["cargo"].selected - 1, c["unload"].button_pressed, int(c["share"].value) if c["share_on"].button_pressed else -1)


## Shows what the line does at its stops in the controls, without sending it back (and not under the hand that is changing them).
func _show_options(id: int, options: Array) -> void:
	var controls: Array = _rows[id]["controls"]
	for i in mini(controls.size(), options.size()):
		var c: Dictionary = controls[i]
		var o: Dictionary = options[i]
		if c["share"].get_line_edit().has_focus():
			continue
		c["load"].set_pressed_no_signal(o["load"])
		c["cargo"].select(o["cargo"] + 1)
		c["cargo"].disabled = not o["load"]
		c["unload"].set_pressed_no_signal(o["unload"])
		c["share_on"].set_pressed_no_signal(o["share"] >= 0)
		c["share_on"].disabled = not o["unload"]
		c["share"].editable = o["share"] >= 0 and o["unload"]
		if o["share"] >= 0:
			c["share"].set_value_no_signal(o["share"])


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
	if not rail:
		for place in hud.places_known: # industries and gateways a road reaches, as stops (M6f)
			offered.append({"id": place["code"], "name": place["name"]})
	var same := choice.item_count == offered.size()
	if same:
		for i in offered.size():
			if choice.get_item_id(i) != offered[i]["id"] or choice.get_item_text(i) != offered[i]["name"]:
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
	row.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	var main := Kit.card()
	var main_body: VBoxContainer = main.get_meta("body")
	main_body.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	row.add_child(main)
	var header := Kit.row()
	main_body.add_child(header)
	var swatch := ColorPickerButton.new()
	swatch.custom_minimum_size = Vector2(40, 40)
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
	var delete := Kit.button("Delete", func() -> void: hud.line_action.emit("delete", id, ""), "Close the line and sell its vehicles.")
	delete.theme_type_variation = "DangerButton"
	header.add_child(delete)
	var no_vehicles := Kit.notice("No vehicles on this line yet", "Add one at the bottom to start running.")
	main_body.add_child(no_vehicles)
	var stats_row := Kit.row()
	main_body.add_child(stats_row)
	var stats := {}
	for entry in [["vehicles", "Vehicles"], ["delivered", "Delivered"], ["income", "Income"], ["upkeep", "Upkeep"], ["profit", "Profit"]]:
		var stat := Kit.stat(entry[1], "Figure")
		if entry[0] == "vehicles":
			var loading := Label.new()
			loading.theme_type_variation = "Small"
			stat["box"].get_child(0).add_child(loading)
			stat["loading"] = loading
		stats_row.add_child(stat["box"])
		stats[entry[0]] = stat
	var maint := Kit.card()
	var maint_body: VBoxContainer = maint.get_meta("body")
	maint_body.add_child(Kit.section("Maintenance"))
	var policy := Kit.make_policy_block(1, id, hud.policy_set.emit)
	maint_body.add_child(policy["box"])
	row.add_child(maint)
	var route_head := Kit.section("Route")
	var route_note := Kit.note("")
	route_note.autowrap_mode = TextServer.AUTOWRAP_OFF
	route_head.add_child(route_note)
	row.add_child(route_head)
	var stops := Kit.column()
	row.add_child(stops)
	var add := Kit.row()
	row.add_child(add)
	var choice := OptionButton.new()
	choice.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add.add_child(choice)
	var add_stop := Kit.button("Add stop", func() -> void:
		if choice.item_count == 0:
			return
		var stop_ids: Array = _rows[id]["stop_ids"].duplicate()
		stop_ids.append(choice.get_selected_id())
		hud.line_edit.emit(id, stop_ids), "Add the chosen station at the end of the line.")
	add_stop.theme_type_variation = "PrimaryButton"
	add.add_child(add_stop)
	row.add_child(Kit.section("Add a vehicle"))
	var buttons := HFlowContainer.new()
	buttons.add_theme_constant_override("h_separation", GAP_BUY)
	buttons.add_theme_constant_override("v_separation", GAP_BUY)
	row.add_child(buttons)
	if rail:
		for option in hud.train_options:
			var buy := Kit.button("%s\n+ %s" % [option["label"], Kit.money(option["price"])], func() -> void: hud.line_action.emit("buy_train", id, option["id"]),
				"Carries %d, costs %s a day to run." % [option["capacity"], Kit.money(option["upkeep"])])
			buy.theme_type_variation = "AccentButton"
			buttons.add_child(buy)
	else:
		for model in hud.models:
			var buy := Kit.button("%s\n+ %s" % [model["name"], Kit.money(model["price"])], func() -> void: hud.line_action.emit("buy", id, model["id"]))
			buy.theme_type_variation = "AccentButton"
			buttons.add_child(buy)
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "separator": separator, "swatch": swatch, "name": name_edit, "stops": stops, "stats": stats, "no_vehicles": no_vehicles, "route_note": route_note,
		"choice": choice, "stop_ids": [], "stop_names": [], "color": swatch.color, "rail": rail, "policy": policy, "controls": []}


func _remove(id: int) -> void:
	_rows[id]["row"].queue_free()
	_rows[id]["separator"].queue_free()
	_rows.erase(id)
