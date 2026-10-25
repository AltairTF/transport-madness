extends "res://game/ui/page.gd"
## Region > Towns: one row per town: its name and numbers, a satisfaction bar and a button that takes the camera there.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # town id -> {"row", "view", "separator"}
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


## The lines of a town that have no place of their own in its view: why it does not grow, what it lost, what it is short of.
static func details_text(town: Dictionary, cargo_names: Array) -> String:
	var lines: Array[String] = []
	if town["residents"] < town["population"]:
		lines.append("The homes have room for %d" % town["population"])
	if town["capacity"] < 0.995:
		lines.append("The call-up has left its buildings room for %d%% of their people and jobs" % roundi(100.0 * town["capacity"]))
	if town["casualties"] > 0:
		lines.append("%d killed in raids" % town["casualties"])
	if town["inactive"] > 0 or town["abandoned"] > 0:
		lines.append("%d cut off from the roads   %d abandoned" % [town["inactive"], town["abandoned"]])
	if town["blocked"] == "Satisfaction":
		lines.append("Not growing: satisfaction is below %d" % roundi(town["min_satisfaction"]))
	elif town["blocked"] == "Connectivity":
		lines.append("Not growing: fewer than %d%% of homes can reach work or leisure" % roundi(100.0 * town["min_connectivity"]))
	elif town["grew_sizes"] + town["grew_buildings"] > 0:
		var grew := "Grew last month: %d bigger, %d new buildings" % [town["grew_sizes"], town["grew_buildings"]]
		if town["grew_streets"] > 0:
			grew += ", %d new streets" % town["grew_streets"]
		lines.append(grew)
	var short := _needs_text(town, cargo_names).split("\n")
	for line in short:
		if not line.is_empty() and not line.begins_with("Your stations reach") and not line.begins_with("No station of yours"):
			lines.append(line)
	return "\n".join(lines)


## The panel of one town (the list shows one per town, the object window shows one): figures, last month's meters, the company's
## stations, the trips. Returns {"box", ...refs} for fill_view.
static func make_view() -> Dictionary:
	var box := Kit.column()
	box.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	var refs := {"box": box}
	var big := Kit.row()
	refs["people"] = Kit.stat("People", "BigNumber")
	refs["jobs"] = Kit.stat("Jobs", "BigNumber")
	big.add_child(refs["people"]["box"])
	big.add_child(refs["jobs"]["box"])
	box.add_child(big)
	var small := Kit.row()
	refs["homes"] = Kit.stat("Homes")
	refs["shops"] = Kit.stat("Shops & offices")
	refs["industries"] = Kit.stat("Industries")
	for key in ["homes", "shops", "industries"]:
		small.add_child(refs[key]["box"])
	box.add_child(small)
	box.add_child(Kit.section("Last month"))
	refs["needs"] = Kit.meter("Needs met", UiTheme.GOOD)
	refs["reached"] = Kit.meter("Homes reached", UiTheme.GOOD)
	refs["satisfaction"] = Kit.meter("Satisfaction", Color("9cc2ff"))
	for key in ["needs", "reached", "satisfaction"]:
		box.add_child(refs[key]["box"])
	refs["satisfaction"]["bar"].tooltip_text = "Satisfaction falls when buildings are demolished or abandoned, and slowly recovers."
	box.add_child(Kit.section("Your stations"))
	var stations := Kit.card()
	refs["covered"] = Kit.meter("Buildings reached", Color("9cc2ff"))
	stations.get_meta("body").add_child(refs["covered"]["box"])
	refs["no_station"] = Kit.note("No station of yours reaches this town, so nothing can be delivered to it.")
	refs["no_station"].theme_type_variation = "Warn"
	stations.get_meta("body").add_child(refs["no_station"])
	box.add_child(stations)
	refs["details"] = Kit.note("")
	box.add_child(refs["details"])
	var trips_head := Kit.section("Trips yesterday")
	refs["trips_head"] = trips_head
	box.add_child(trips_head)
	refs["trips_total"] = Kit.note("")
	box.add_child(refs["trips_total"])
	var split := HBoxContainer.new()
	split.add_theme_constant_override("separation", 2)
	split.custom_minimum_size = Vector2(0, 14)
	refs["split"] = split
	box.add_child(split)
	var legend := GridContainer.new()
	legend.columns = 2
	legend.add_theme_constant_override("h_separation", 16)
	legend.add_theme_constant_override("v_separation", 6)
	box.add_child(legend)
	refs["legend"] = legend
	refs["trip_values"] = {}
	for entry in TRIP_KINDS:
		var cell := Kit.row()
		var swatch := ColorRect.new()
		swatch.color = entry[2]
		swatch.custom_minimum_size = Vector2(10, 10)
		swatch.size_flags_vertical = Control.SIZE_SHRINK_CENTER
		cell.add_child(swatch)
		var name := Kit.note(entry[1])
		name.autowrap_mode = TextServer.AUTOWRAP_OFF
		name.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		cell.add_child(name)
		var value := Label.new()
		cell.add_child(value)
		cell.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		legend.add_child(cell)
		refs["trip_values"][entry[0]] = value
	return refs


const TRIP_KINDS := [["walked", "On foot", Color("9cc2ff")], ["drove", "By car", Color("3f78e0")], ["rode", "By bus", Color("5fd4b0")], ["stayed", "Could not go", Color("f2b84b")]]


static func fill_view(refs: Dictionary, town: Dictionary, cargo_names: Array) -> void:
	refs["people"]["value"].text = str(town["residents"])
	refs["jobs"]["value"].text = str(town["jobs"])
	refs["homes"]["value"].text = str(town["homes"])
	refs["shops"]["value"].text = str(town["commerce"])
	refs["industries"]["value"].text = str(town["industry"])
	var met := roundi(100.0 * (1.0 - town["unmet"]))
	refs["needs"]["value"].text = "%d%%" % met
	refs["needs"]["bar"].value = met
	var reached := roundi(100.0 * town["connectivity"])
	refs["reached"]["value"].text = "%d%%" % reached
	refs["reached"]["bar"].value = reached
	refs["satisfaction"]["value"].text = "%d of 100" % roundi(town["satisfaction"])
	refs["satisfaction"]["bar"].value = town["satisfaction"]
	refs["covered"]["value"].text = "%d of %d" % [town["covered"], town["buildings"]]
	refs["covered"]["bar"].value = 100.0 * town["covered"] / maxf(1.0, town["buildings"])
	refs["no_station"].visible = town["covered"] == 0
	var details := details_text(town, cargo_names)
	refs["details"].text = details
	refs["details"].visible = not details.is_empty()
	var trips: int = town["walked"] + town["drove"] + town["rode"] + town["stayed"]
	for node in [refs["trips_head"], refs["trips_total"], refs["split"], refs["legend"]]:
		node.visible = trips > 0
	refs["trips_total"].text = "%d wanted to go" % trips
	for child in refs["split"].get_children():
		child.queue_free()
	for entry in TRIP_KINDS:
		var count: int = town[entry[0]]
		refs["trip_values"][entry[0]].text = str(count)
		if count > 0:
			var seg := ColorRect.new()
			seg.color = entry[2]
			seg.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			seg.size_flags_stretch_ratio = count
			refs["split"].add_child(seg)


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
		fill_view(_rows[id]["view"], town, hud.cargo_names)
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


func _add(id: int, town: Dictionary) -> void:
	var row := Kit.column()
	var head := Kit.row()
	head.add_child(Kit.heading(town["name"]))
	head.add_child(Kit.spacer())
	head.add_child(go_button(town["x"], town["z"]))
	row.add_child(head)
	var view := make_view()
	row.add_child(view["box"])
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "view": view, "separator": separator}
