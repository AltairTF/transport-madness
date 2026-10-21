extends "res://game/ui/page.gd"
## Network > Stations: one row per station: its name (to rename), the cargo waiting there and what it accepts, bays and closing.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # station id -> {"row", "label", "separator", "name", "bays_button", "station_name"}


func _build() -> void:
	_box = Kit.column()
	add_child(_box)
	_empty = Kit.note("No stations yet. Build one on a road beside a town or an industry.")
	add_child(_empty)


## What a row says of a station (also the text of its own window).
static func text_of(station: Dictionary, cargo_names: Array) -> String:
	var waiting := ""
	var accepts := ""
	for c in cargo_names.size():
		if station["waiting"][c] > 0:
			waiting += ("" if waiting.is_empty() else ", ") + "%d %s" % [station["waiting"][c], String(cargo_names[c]).to_lower()]
		if station["accepts"][c]:
			accepts += ("" if accepts.is_empty() else ", ") + String(cargo_names[c]).to_lower()
	var served := ""
	for line in station.get("lines", []):
		served += ("" if served.is_empty() else ", ") + line["name"]
	var text := "lines   %s" % ("none" if served.is_empty() else served)
	if station["bays"]:
		text = "bays   %d in use\n%s" % [station["in_bay"], text]
	text += "\ncovers   %d homes   %d shops   %d other workplaces   %d industries" % [station["homes"], station["shops"], station["workplaces"], station["industries"]]
	text += "\nwaiting   %s\naccepts   %s" % ["nothing" if waiting.is_empty() else waiting, "nothing" if accepts.is_empty() else accepts]
	if station.get("shut_days", 0.0) > 0.0:
		text += "\nBombed: shut for %s more days, nothing is loaded or unloaded." % str(snappedf(station["shut_days"], 0.1))
	var bound_for := ""
	for queue in station.get("waiting_for", []):
		bound_for += ("" if bound_for.is_empty() else ", ") + "%d to %s" % [queue["count"], queue["name"]]
	if not bound_for.is_empty():
		text += "\npeople for   %s" % bound_for
	text += "\nyesterday   %d boarded   %d got off   %d gave up waiting" % [station["boarded"], station["alighted"], station["gave_up"]]
	text += "\ntoday so far   %d boarded   %d got off   %d gave up waiting" % [station["boarded_today"], station["alighted_today"], station["gave_up_today"]]
	return text


func show_stations(stations: Array) -> void:
	var seen := {}
	for station in stations:
		var id: int = station["id"]
		seen[id] = true
		if not _rows.has(id):
			_add(id, station)
		var entry: Dictionary = _rows[id]
		var name_edit: LineEdit = entry["name"]
		if not name_edit.has_focus() and name_edit.text != station["name"]:
			name_edit.text = station["name"]
		var bays_button: Button = entry["bays_button"]
		bays_button.visible = not station["bays"] and not station.get("rail", false)
		bays_button.text = "Add bays (%s)" % Kit.money(station["bay_price"])
		entry["station_name"] = station["name"]
		var label: Label = entry["label"]
		label.text = text_of(station, hud.cargo_names)
		if not station.get("lines", []).is_empty():
			label.add_theme_color_override("font_color", station["lines"][0]["color"])
		else:
			label.remove_theme_color_override("font_color")
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


## Closing a station cannot be undone, so it asks first.
func _confirm_close(id: int) -> void:
	var dialog := ConfirmationDialog.new()
	dialog.dialog_text = "Close %s for good?\nLines will skip it and nothing is refunded." % _rows[id]["station_name"]
	dialog.ok_button_text = "Close it"
	dialog.confirmed.connect(func() -> void:
		hud.station_action.emit("close", id, "")
		dialog.queue_free())
	dialog.canceled.connect(dialog.queue_free)
	add_child(dialog)
	dialog.popup_centered()


func _add(id: int, station: Dictionary) -> void:
	var row := Kit.column()
	var name_edit := LineEdit.new()
	name_edit.max_length = 24
	name_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	name_edit.tooltip_text = "Click to rename the station, Enter to keep the name."
	name_edit.text_submitted.connect(func(text: String) -> void:
		hud.station_action.emit("rename", id, text)
		name_edit.release_focus())
	name_edit.focus_exited.connect(func() -> void: hud.station_action.emit("rename", id, name_edit.text))
	row.add_child(name_edit)
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(label)
	var buttons := Kit.flow()
	row.add_child(buttons)
	buttons.add_child(go_button(station["x"], station["z"]))
	var bays_button := Kit.button("", func() -> void: hud.station_action.emit("bays", id, ""), "Build bays: loading vehicles pull out of the lane so they do not block it.")
	buttons.add_child(bays_button)
	buttons.add_child(Kit.button("Close", func() -> void: _confirm_close(id), "Close the station for good. Lines skip it; nothing is refunded."))
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "label": label, "separator": separator, "name": name_edit, "bays_button": bays_button, "station_name": ""}
