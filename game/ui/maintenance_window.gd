extends "res://game/ui/page.gd"
## Company > Maintenance: the company policy, the option for random events, the totals, the repairs, the fleet from the worst
## Condition up, and the worn roads.

var _policy: Dictionary
var _frequency: OptionButton
var _totals: Dictionary
var _rules: RichTextLabel
var _fleet: VBoxContainer
var _fleet_empty: Label
var _fleet_rows: Dictionary = {} # vehicle id -> {"row", "label", "bar", "go", "go_call"}
var _roads: VBoxContainer
var _road_rows: Dictionary = {} # road id -> {"row", "label", "bar", "go", "go_call", "resurface"}
var _roads_empty: Label


func _build() -> void:
	var totals := Kit.row()
	for entry in [["maintenance", "Spent on maintenance"], ["compensation", "Compensation paid"], ["fines", "Fines"]]:
		var stat := Kit.stat(entry[1])
		stat["value"].add_theme_font_size_override("font_size", 20)
		stat["caption"].add_theme_font_size_override("font_size", 14)
		totals.add_child(stat["box"])
		_totals[entry[0]] = stat
	add_child(totals)
	_totals["injured"] = Kit.note("")
	add_child(_totals["injured"])

	add_child(Kit.section("Company policy"))
	add_child(Kit.note("When a vehicle or train is due it unloads at its next stop, loads nothing and drives to the nearest depot that can do the repair. Lines, vehicles and trains can have a policy of their own; the most specific one that does not inherit wins. Signals follow the company policy."))
	var policy_card := Kit.card()
	_policy = Kit.make_policy_block(0, 0, hud.policy_set.emit)
	policy_card.get_meta("body").add_child(_policy["box"])
	add_child(policy_card)
	_rules = Kit.rich("")
	add_child(_rules)

	add_child(Kit.section("Random events"))
	_frequency = OptionButton.new()
	for i in Kit.FREQUENCY_NAMES.size():
		_frequency.add_item(Kit.FREQUENCY_NAMES[i], i)
	_frequency.tooltip_text = "How often breakdowns and signal failures happen."
	_frequency.item_selected.connect(func(index: int) -> void: hud.frequency_selected.emit(_frequency.get_item_id(index)))
	add_child(_frequency)
	add_child(Kit.note("Accidents that come out of the driving (an overtaking gone wrong, a crash at a junction) are not scaled, so Off does not remove all accidents."))

	add_child(Kit.section("Worst condition first"))
	_fleet = Kit.column()
	add_child(_fleet)
	_fleet_empty = Kit.note("No vehicles yet.")
	add_child(_fleet_empty)

	add_child(Kit.section("Worn roads"))
	add_child(Kit.note("Traffic wears roads. Below Condition 80 drivers go slower and company vehicles break down more often. Resurfacing costs a price per metre and closes the road while the crew works."))
	_roads = Kit.column()
	add_child(_roads)
	_roads_empty = Kit.note("No road is worn.")
	add_child(_roads_empty)


func show_maintenance(maint: Dictionary, vehicles: Array) -> void:
	Kit.show_policy(_policy, maint["company"], "Now: %s" % maint["company_text"])
	if _frequency.get_selected_id() != maint["frequency"] and not _frequency.get_popup().visible:
		_frequency.select(_frequency.get_item_index(maint["frequency"]))
	_totals["maintenance"]["value"].text = Kit.money(maint["maintenance"])
	_totals["compensation"]["value"].text = Kit.money(maint["compensation"])
	_totals["fines"]["value"].text = Kit.money(maint["fines"])
	_totals["injured"].text = "%d people injured" % maint["injured"]
	var rules := ""
	for repair in maint["repairs"]:
		rules += "%s +%d Condition (up to %d), %d s in a bay, %d%% of the price, from a %s\n" % [
			Kit.lead(repair["name"] + ":"), roundi(repair["gain"]), roundi(repair["ceiling"]), roundi(repair["seconds"]), roundi(100.0 * repair["share"]), String(repair["level_name"]).to_lower() if repair["level"] > 0 else "depot of any level"]
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
		entry["resurface"].text = "Resurface %s" % Kit.money(road["price"])
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
	var row := Kit.card()
	var body: VBoxContainer = row.get_meta("body")
	var top := Kit.row()
	body.add_child(top)
	var label := Label.new()
	label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	top.add_child(label)
	var go := Kit.button("Go to", Callable())
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	top.add_child(go)
	var bar := Kit.make_condition_bar()
	bar["bar"].custom_minimum_size = Vector2(0, 8)
	body.add_child(bar["bar"])
	var resurface := Kit.button("", func() -> void: hud.resurface_requested.emit(id))
	resurface.theme_type_variation = "AccentButton"
	body.add_child(resurface)
	_roads.add_child(row)
	_road_rows[id] = {"row": row, "label": label, "bar": bar, "go": go, "go_call": go_call, "resurface": resurface}


func _add_fleet_row(id: int) -> void:
	var row := Kit.card()
	var body: VBoxContainer = row.get_meta("body")
	var top := Kit.row()
	body.add_child(top)
	var label := Label.new()
	label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	label.clip_text = true
	top.add_child(label)
	var go := Kit.button("Go to", Callable())
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	top.add_child(go)
	var bar := Kit.make_condition_bar()
	bar["bar"].custom_minimum_size = Vector2(0, 8)
	body.add_child(bar["bar"])
	_fleet.add_child(row)
	_fleet_rows[id] = {"row": row, "label": label, "bar": bar, "go": go, "go_call": go_call}
