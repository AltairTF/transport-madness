extends "res://game/ui/page.gd"
## Company > Maintenance: the company policy, the option for random events, the totals, the repairs, the fleet from the worst
## Condition up, and the worn roads.

var _policy: Dictionary
var _frequency: OptionButton
var _totals: Label
var _rules: Label
var _fleet: VBoxContainer
var _fleet_empty: Label
var _fleet_rows: Dictionary = {} # vehicle id -> {"row", "label", "bar", "go", "go_call"}
var _roads: VBoxContainer
var _road_rows: Dictionary = {} # road id -> {"row", "label", "bar", "go", "go_call", "resurface"}
var _roads_empty: Label


func _build() -> void:
	add_child(Kit.heading("Company policy"))
	add_child(Kit.note("When a vehicle or train is due it unloads at its next stop, loads nothing and drives to the nearest depot that can do the repair. Lines, vehicles and trains can have a policy of their own; the most specific one that does not inherit wins. Signals follow the company policy."))
	_policy = Kit.make_policy_block(0, 0, hud.policy_set.emit)
	add_child(_policy["box"])
	add_child(HSeparator.new())

	add_child(Kit.heading("Random events"))
	_frequency = OptionButton.new()
	for i in Kit.FREQUENCY_NAMES.size():
		_frequency.add_item(Kit.FREQUENCY_NAMES[i], i)
	_frequency.tooltip_text = "How often breakdowns and signal failures happen."
	_frequency.item_selected.connect(func(index: int) -> void: hud.frequency_selected.emit(_frequency.get_item_id(index)))
	_frequency.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	add_child(_frequency)
	add_child(Kit.note("Accidents that come out of the driving (an overtaking gone wrong, a crash at a junction) are not scaled, so Off does not remove all accidents."))
	add_child(HSeparator.new())

	_totals = Label.new()
	_totals.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(_totals)
	_rules = Kit.note("")
	add_child(_rules)
	add_child(HSeparator.new())

	add_child(Kit.heading("Fleet by Condition"))
	_fleet = Kit.column()
	add_child(_fleet)
	_fleet_empty = Kit.note("No vehicles yet.")
	add_child(_fleet_empty)
	add_child(HSeparator.new())

	add_child(Kit.heading("Worn roads"))
	add_child(Kit.note("Traffic wears roads. Below Condition 80 drivers go slower and company vehicles break down more often. Resurfacing costs a price per metre and closes the road while the crew works."))
	_roads = Kit.column()
	add_child(_roads)
	_roads_empty = Kit.note("No road is worn.")
	add_child(_roads_empty)


func show_maintenance(maint: Dictionary, vehicles: Array) -> void:
	Kit.show_policy(_policy, maint["company"], "Now: %s" % maint["company_text"])
	if _frequency.get_selected_id() != maint["frequency"] and not _frequency.get_popup().visible:
		_frequency.select(_frequency.get_item_index(maint["frequency"]))
	_totals.text = "Spent on maintenance   %s\nCompensation paid   %s   (%d people injured)\nFines   %s" % [
		Kit.money(maint["maintenance"]), Kit.money(maint["compensation"]), maint["injured"], Kit.money(maint["fines"])]
	var rules := ""
	for repair in maint["repairs"]:
		rules += "%s: +%d Condition (up to %d), %d s in a bay, %d%% of the price, from a %s\n" % [
			repair["name"], roundi(repair["gain"]), roundi(repair["ceiling"]), roundi(repair["seconds"]), roundi(100.0 * repair["share"]), String(repair["level_name"]).to_lower() if repair["level"] > 0 else "depot of any level"]
	rules += "A breakdown below Condition %d counts as negligence and is fined." % roundi(maint["critical"])
	_rules.text = rules

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
		Kit.set_condition_bar(entry["bar"], vehicle["condition"])
		entry["go"].pressed.disconnect(entry["go_call"])
		entry["go_call"] = func() -> void: hud.town_focus.emit(vehicle["x"], vehicle["z"])
		entry["go"].pressed.connect(entry["go_call"])
	Kit.drop_unseen(_fleet_rows, seen)
	for i in ordered.size():
		_fleet.move_child(_fleet_rows[ordered[i]["id"]]["row"], i)
	_fleet_empty.visible = _fleet_rows.is_empty()


## The worn roads, worst first (from the host's worn roads).
func show_worn_roads(roads: Array) -> void:
	var seen := {}
	for road in roads:
		var id: int = road["road"]
		seen[id] = true
		if not _road_rows.has(id):
			_add_road_row(id)
		var entry: Dictionary = _road_rows[id]
		entry["label"].text = "Road %d   %d m" % [id + 1, roundi(road["length"])]
		Kit.set_condition_bar(entry["bar"], road["condition"])
		entry["resurface"].text = "Resurface (%s)" % Kit.money(road["price"])
		entry["resurface"].disabled = road["why"] != ""
		entry["resurface"].tooltip_text = road["why"] if road["why"] != "" else "A crew restores the road; it is closed while they work."
		entry["go"].pressed.disconnect(entry["go_call"])
		entry["go_call"] = func() -> void: hud.town_focus.emit(road["x"], road["z"])
		entry["go"].pressed.connect(entry["go_call"])
	Kit.drop_unseen(_road_rows, seen)
	for i in roads.size():
		_roads.move_child(_road_rows[roads[i]["road"]]["row"], i)
	_roads_empty.visible = _road_rows.is_empty()


func _add_road_row(id: int) -> void:
	var row := Kit.column()
	var top := Kit.row()
	row.add_child(top)
	var label := Label.new()
	label.custom_minimum_size = Vector2(150, 0)
	top.add_child(label)
	var bar := Kit.make_condition_bar()
	top.add_child(bar["bar"])
	var buttons := Kit.row()
	row.add_child(buttons)
	var go := Kit.button("Go to", Callable())
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	buttons.add_child(go)
	var resurface := Kit.button("", func() -> void: hud.resurface_requested.emit(id))
	buttons.add_child(resurface)
	_roads.add_child(row)
	_road_rows[id] = {"row": row, "label": label, "bar": bar, "go": go, "go_call": go_call, "resurface": resurface}


func _add_fleet_row(id: int) -> void:
	var row := Kit.row()
	var label := Label.new()
	label.custom_minimum_size = Vector2(190, 0)
	row.add_child(label)
	var bar := Kit.make_condition_bar()
	row.add_child(bar["bar"])
	var go := Kit.button("Go to", Callable())
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	row.add_child(go)
	_fleet.add_child(row)
	_fleet_rows[id] = {"row": row, "label": label, "bar": bar, "go": go, "go_call": go_call}
