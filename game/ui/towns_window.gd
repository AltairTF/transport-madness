extends "res://game/ui/page.gd"
## Region > Towns: one row per town: its name and numbers, a satisfaction bar and a button that takes the camera there.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # town id -> {"row", "label", "bar", "separator"}
var connectivity_check: CheckButton


func _build() -> void:
	connectivity_check = CheckButton.new()
	connectivity_check.text = "Show connectivity on the map"
	connectivity_check.tooltip_text = "Homes turn green when someone living there reached work or leisure within the time budget last month, red when nobody did. Everything else is dimmed."
	connectivity_check.toggled.connect(func(on: bool) -> void: hud.connectivity_view_toggled.emit(on))
	add_child(connectivity_check)
	_box = Kit.column()
	add_child(_box)
	_empty = Kit.note("There are no towns on this map. Start a new game on the Towns map.")
	add_child(_empty)


## What a row says of a town (also the text of its own window). <cargo_names> names the needs.
static func text_of(town: Dictionary, cargo_names: Array) -> String:
	var text := "%s\n%d people   %d jobs\n%d homes   %d shops and offices   %d industries" % [
		town["name"], town["residents"], town["jobs"], town["homes"], town["commerce"], town["industry"]]
	if town["residents"] < town["population"]:
		text += "\nThe homes have room for %d" % town["population"]
	if town["capacity"] < 0.995:
		text += "\nThe call-up has left its buildings room for %d%% of their people and jobs" % roundi(100.0 * town["capacity"])
	if town["casualties"] > 0:
		text += "\n%d killed in raids" % town["casualties"]
	if town["inactive"] > 0 or town["abandoned"] > 0:
		text += "\n%d cut off from the roads   %d abandoned" % [town["inactive"], town["abandoned"]]
	text += "\nNeeds met last month   %d%%" % roundi(100.0 * (1.0 - town["unmet"]))
	text += "\nConnected   %d%% of homes reached last month" % roundi(100.0 * town["connectivity"])
	if town["blocked"] == "Satisfaction":
		text += "\nNot growing: satisfaction is below %d" % roundi(town["min_satisfaction"])
	elif town["blocked"] == "Connectivity":
		text += "\nNot growing: fewer than %d%% of homes can reach work or leisure" % roundi(100.0 * town["min_connectivity"])
	elif town["grew_sizes"] + town["grew_buildings"] > 0:
		text += "\nGrew last month: %d bigger, %d new buildings" % [town["grew_sizes"], town["grew_buildings"]]
		if town["grew_blocks"] > 0:
			text += ", %d new block of streets" % town["grew_blocks"]
	text += _needs_text(town, cargo_names)
	var trips: int = town["walked"] + town["drove"] + town["rode"] + town["stayed"]
	if trips > 0:
		text += "\nTrips yesterday   %d on foot   %d by car   %d by bus" % [town["walked"], town["drove"], town["rode"]]
		if town["stayed"] > 0:
			text += "   %d could not go" % town["stayed"]
	text += "\nSatisfaction   %d of 100" % roundi(town["satisfaction"])
	return text


## What the town still needs: how much of it a station of yours reaches, what its buildings are short of this month, and what is
## missing from their stock for growing.
static func _needs_text(town: Dictionary, cargo_names: Array) -> String:
	var text := "\nYour stations reach   %d of %d buildings" % [town["covered"], town["buildings"]]
	if town["covered"] == 0:
		return text + "\nNo station of yours reaches this town, so nothing can be delivered to it"
	var short := ""
	var missing := ""
	for need in town["needs"]:
		var name: String = String(cargo_names[need["cargo"]]).to_lower() if need["cargo"] < cargo_names.size() else "cargo %d" % need["cargo"]
		if need["short"] > 0:
			short += ("" if short.is_empty() else ", ") + "%s (%d buildings)" % [name, need["short"]]
		if need["construction"] >= 1.0:
			missing += ("" if missing.is_empty() else ", ") + "%d %s" % [roundi(need["construction"]), name]
	if not short.is_empty():
		text += "\nShort of this month's needs   " + short
	if not missing.is_empty():
		text += "\nStill missing to grow   " + missing
	return text


func show_towns(towns: Array) -> void:
	var seen := {}
	for town in towns:
		var id: int = town["id"]
		seen[id] = true
		if not _rows.has(id):
			_add(id, town)
		var entry: Dictionary = _rows[id]
		entry["label"].text = text_of(town, hud.cargo_names)
		var bar: ProgressBar = entry["bar"]
		bar.value = town["satisfaction"]
		bar.tooltip_text = "Satisfaction %d of 100. It falls when buildings are demolished or abandoned, and slowly recovers." % roundi(town["satisfaction"])
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


func _add(id: int, town: Dictionary) -> void:
	var row := Kit.column()
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(label)
	var satisfaction := Kit.row()
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
	var go := go_button(town["x"], town["z"])
	go.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	row.add_child(go)
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "label": label, "bar": bar, "separator": separator}
