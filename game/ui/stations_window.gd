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


## The body of one station (the list and the object window share it): what it covers, what it accepts, today and yesterday.
## Returns {"box", ...refs} for fill_view.
static func make_view() -> Dictionary:
	var box := Kit.column()
	box.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	var refs := {"box": box}
	refs["shut"] = Kit.notice("Bombed", "")
	box.add_child(refs["shut"])
	box.add_child(Kit.section("Covers"))
	var covers := Kit.row()
	refs["covers"] = {}
	for entry in [["homes", "Homes"], ["shops", "Shops"], ["workplaces", "Workplaces"], ["industries", "Industries"]]:
		var stat := Kit.stat(entry[1])
		stat["value"].add_theme_font_size_override("font_size", 22)
		covers.add_child(stat["box"])
		refs["covers"][entry[0]] = stat
	box.add_child(covers)
	refs["reaches"] = Kit.note("")
	box.add_child(refs["reaches"])
	box.add_child(Kit.section("Accepts"))
	refs["accepts"] = Kit.flow()
	box.add_child(refs["accepts"])
	refs["accepts_key"] = "?"
	var activity := Kit.section("Activity")
	refs["waiting"] = Kit.note("")
	refs["waiting"].autowrap_mode = TextServer.AUTOWRAP_OFF
	activity.add_child(refs["waiting"])
	box.add_child(activity)
	var table := GridContainer.new()
	table.columns = 3
	table.add_theme_constant_override("h_separation", 16)
	table.add_theme_constant_override("v_separation", 6)
	refs["table"] = {}
	for entry in [["", "", ""], ["head", "Yesterday", "Today so far"], ["boarded", "", ""], ["alighted", "", ""], ["gave_up", "", ""]]:
		var name := Label.new()
		name.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		table.add_child(name)
		var yesterday := Label.new()
		yesterday.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
		var today := Label.new()
		today.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
		table.add_child(yesterday)
		table.add_child(today)
		if entry[0] == "":
			table.remove_child(name)
			table.remove_child(yesterday)
			table.remove_child(today)
			name.queue_free()
			yesterday.queue_free()
			today.queue_free()
			continue
		if entry[0] == "head":
			for label in [yesterday, today]:
				label.theme_type_variation = "Small"
			yesterday.text = entry[1]
			today.text = entry[2]
		else:
			name.text = {"boarded": "Boarded", "alighted": "Got off", "gave_up": "Gave up waiting"}[entry[0]]
			refs["table"][entry[0]] = [yesterday, today]
	box.add_child(table)
	refs["bound_for"] = Kit.note("")
	box.add_child(refs["bound_for"])
	return refs


static func fill_view(refs: Dictionary, station: Dictionary, cargo_names: Array) -> void:
	var shut: float = station.get("shut_days", 0.0)
	refs["shut"].visible = shut > 0.0
	if shut > 0.0:
		refs["shut"].get_child(0).get_child(1).text = "Shut for %s more days, nothing is loaded or unloaded." % str(snappedf(shut, 0.1))
	for key in refs["covers"]:
		refs["covers"][key]["value"].text = str(station[key])
	refs["reaches"].visible = station.get("reached", 0) > 0
	refs["reaches"].text = "Reaches %d more buildings along the streets (about %.0f at full coverage)." % [station.get("reached", 0), station.get("reached_share", 0.0)]
	var waiting := ""
	var accepts: Array[String] = []
	for c in cargo_names.size():
		if station["waiting"][c] > 0:
			waiting += ("" if waiting.is_empty() else ", ") + "%d %s" % [station["waiting"][c], String(cargo_names[c]).to_lower()]
		if station["accepts"][c]:
			accepts.append(String(cargo_names[c]))
	var accepts_key := ",".join(accepts)
	if accepts_key != refs["accepts_key"]:
		refs["accepts_key"] = accepts_key
		for child in refs["accepts"].get_children():
			child.queue_free()
		for name in accepts:
			refs["accepts"].add_child(Kit.chip(name))
		if accepts.is_empty():
			refs["accepts"].add_child(Kit.chip("Nothing"))
	refs["waiting"].text = "Waiting now: %s" % ("nothing" if waiting.is_empty() else waiting)
	refs["table"]["boarded"][0].text = str(station["boarded"])
	refs["table"]["boarded"][1].text = str(station["boarded_today"])
	refs["table"]["alighted"][0].text = str(station["alighted"])
	refs["table"]["alighted"][1].text = str(station["alighted_today"])
	refs["table"]["gave_up"][0].text = str(station["gave_up"])
	refs["table"]["gave_up"][1].text = str(station["gave_up_today"])
	var bound_for := ""
	for queue in station.get("waiting_for", []):
		bound_for += ("" if bound_for.is_empty() else ", ") + "%d to %s" % [queue["count"], queue["name"]]
	refs["bound_for"].visible = not bound_for.is_empty()
	refs["bound_for"].text = "People for %s" % bound_for


## The line under the name of a collapsed station.
static func summary_of(station: Dictionary) -> String:
	return "%d homes · %d shops · %d workplaces · %d got off today" % [station["homes"], station["shops"], station["workplaces"], station["alighted_today"]]


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
		bays_button.text = "Add bays %s" % Kit.money(station["bay_price"])
		entry["station_name"] = station["name"]
		fill_view(entry["view"], station, hud.cargo_names)
		entry["summary"].text = summary_of(station)
		entry["bays_note"].visible = station["bays"]
		entry["bays_note"].text = "Bays: %d in use" % station["in_bay"]
		for chip in entry["chips"].get_children():
			chip.queue_free()
		for line in station.get("lines", []):
			var chip := Kit.chip(line["name"])
			chip.get_child(0).add_theme_color_override("font_color", line["color"])
			entry["chips"].add_child(chip)
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
	var card := Kit.card()
	var body: VBoxContainer = card.get_meta("body")
	var head := Kit.row()
	var fold := Button.new()
	fold.theme_type_variation = "FlatButton"
	fold.text = "▸"
	head.add_child(fold)
	var title := Kit.column()
	title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var name_edit := LineEdit.new()
	name_edit.max_length = 24
	name_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	name_edit.tooltip_text = "Click to rename the station, Enter to keep the name."
	name_edit.text_submitted.connect(func(text: String) -> void:
		hud.station_action.emit("rename", id, text)
		name_edit.release_focus())
	name_edit.focus_exited.connect(func() -> void: hud.station_action.emit("rename", id, name_edit.text))
	title.add_child(name_edit)
	var summary := Kit.note("")
	title.add_child(summary)
	head.add_child(title)
	var chips := Kit.flow()
	chips.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	head.add_child(chips)
	body.add_child(head)
	var detail := Kit.column()
	detail.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	detail.visible = false
	body.add_child(detail)
	fold.pressed.connect(func() -> void:
		detail.visible = not detail.visible
		summary.visible = not detail.visible
		fold.text = "▾" if detail.visible else "▸")
	var view := make_view()
	detail.add_child(view["box"])
	var bays_note := Kit.note("")
	detail.add_child(bays_note)
	var buttons := Kit.flow()
	detail.add_child(buttons)
	buttons.add_child(go_button(station["x"], station["z"]))
	var bays_button := Kit.button("", func() -> void: hud.station_action.emit("bays", id, ""), "Build bays: loading vehicles pull out of the lane so they do not block it.")
	bays_button.theme_type_variation = "AccentButton"
	buttons.add_child(bays_button)
	buttons.add_child(Kit.spacer())
	var close := Kit.button("Close station", func() -> void: _confirm_close(id), "Close the station for good. Lines skip it; nothing is refunded.")
	close.theme_type_variation = "DangerButton"
	buttons.add_child(close)
	var separator := add_row(_box, card)
	separator.visible = false
	_rows[id] = {"row": card, "separator": separator, "name": name_edit, "bays_button": bays_button, "station_name": "", "view": view, "summary": summary, "chips": chips, "bays_note": bays_note}
