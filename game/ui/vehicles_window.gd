extends "res://game/ui/page.gd"
## Network > Vehicles: buying a vehicle into a depot, the vehicles waiting in depots, and one row per company vehicle and train.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # vehicle id -> {"row", "label", "separator", "go", "go_call", "sell", "condition", "repair", "ask", "policy"}
var _depot_choice: OptionButton
var _buy_box: HFlowContainer
var _hint: Label
var _depots: Array = [] ## the depots in the choice: {id, rail, name, x, z}
var _depots_key := ""
var _parked_header: Control
var _parked_box: VBoxContainer
var _parked_rows: Dictionary = {} # garage id -> {"row", "separator", "label", "choice", "lines_key", "assign", "go", "go_call", "repair_kind", "repair_button"}


var _tab_buttons: Array[Button] = []
var _tab_pages: Array[Control] = []
var _fleet_tab: Button
var _parked_tab: Button


func _build() -> void:
	var tabs := Kit.row()
	add_child(tabs)
	var fleet_page := Kit.column()
	fleet_page.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	var parked_page := Kit.column()
	parked_page.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	var buy_page := Kit.column()
	buy_page.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	for entry in [["Fleet", fleet_page], ["Parked", parked_page], ["Buy", buy_page]]:
		var tab := Kit.button(entry[0], Callable())
		tab.toggle_mode = true
		tab.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		tabs.add_child(tab)
		_tab_buttons.append(tab)
		_tab_pages.append(entry[1])
		add_child(entry[1])
	_fleet_tab = _tab_buttons[0]
	_parked_tab = _tab_buttons[1]
	for k in _tab_buttons.size():
		_tab_buttons[k].pressed.connect(_show_tab.bind(k))
	_show_tab(0)

	_box = Kit.column()
	_box.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	fleet_page.add_child(_box)
	_empty = Kit.note("No vehicles on the road yet. Buy one in the Buy tab and put it on a line.")
	fleet_page.add_child(_empty)

	_parked_header = Kit.section("Waiting in depots")
	_parked_header.visible = false
	parked_page.add_child(_parked_header)
	_parked_box = Kit.column()
	_parked_box.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	parked_page.add_child(_parked_box)
	parked_page.add_child(Kit.note("Vehicles bought for a depot, or taken off a line, wait here until you put them on one."))

	buy_page.add_child(Kit.note("It waits in the depot you pick until you put it on a line. Vehicles also come out of a depot at once when bought from the Lines window."))
	_depot_choice = OptionButton.new()
	_depot_choice.item_selected.connect(func(_index: int) -> void: rebuild_buy_buttons())
	buy_page.add_child(_depot_choice)
	_buy_box = Kit.flow()
	buy_page.add_child(_buy_box)
	_hint = Kit.note("No depot yet: build one under Transport > Depot, or under Rail > Rail depot for trains.")
	buy_page.add_child(_hint)


func _show_tab(index: int) -> void:
	for k in _tab_buttons.size():
		_tab_buttons[k].set_pressed_no_signal(k == index)
		_tab_pages[k].visible = k == index


static func title_of(vehicle: Dictionary) -> String:
	return "%s %d" % [vehicle["model"], vehicle["id"] + 1 if vehicle["id"] >= 0 else -vehicle["id"]]


static func repair_text(vehicle: Dictionary) -> String:
	var note := "Last repaired %d days ago" % vehicle["days_since"]
	if vehicle["patched"]:
		note += ", patched: wears faster until a service"
	return note if vehicle["repair"] == "" else note + "\n" + vehicle["repair"]


## The body of one vehicle (the list and the object window share it): its state, load and condition, and the money.
## Returns {"box", ...refs} for fill_view.
static func make_view() -> Dictionary:
	var box := Kit.column()
	box.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	var refs := {"box": box}
	var head := Kit.row()
	var line := Kit.column()
	line.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	refs["line"] = Label.new()
	line.add_child(refs["line"])
	head.add_child(line)
	refs["state"] = Kit.badge("")
	head.add_child(refs["state"])
	box.add_child(head)
	refs["load"] = Kit.meter("Load", Color("9cc2ff"))
	box.add_child(refs["load"]["box"])
	var condition := Kit.make_condition_bar()
	refs["condition"] = condition
	var cond_head := Kit.row()
	var cond_caption := Kit.note("Condition")
	cond_caption.autowrap_mode = TextServer.AUTOWRAP_OFF
	cond_head.add_child(cond_caption)
	cond_head.add_child(Kit.spacer())
	refs["condition_value"] = Label.new()
	cond_head.add_child(refs["condition_value"])
	box.add_child(cond_head)
	condition["bar"].custom_minimum_size = Vector2(0, 8)
	box.add_child(condition["bar"])
	var money := Kit.row()
	refs["income"] = Kit.stat("Income")
	refs["upkeep"] = Kit.stat("Upkeep")
	refs["profit"] = Kit.stat("Profit")
	for key in ["income", "upkeep", "profit"]:
		refs[key]["value"].add_theme_font_size_override("font_size", 20)
		money.add_child(refs[key]["box"])
	box.add_child(money)
	refs["repair"] = Kit.note("")
	box.add_child(refs["repair"])
	return refs


static func fill_view(refs: Dictionary, vehicle: Dictionary) -> void:
	refs["line"].text = vehicle["line_name"]
	refs["line"].add_theme_color_override("font_color", vehicle["color"])
	refs["state"].get_child(0).text = vehicle["state"]
	var carrying: int = vehicle["load"]
	refs["load"]["value"].text = "%d of %d %s" % [carrying, vehicle["capacity"], String(vehicle["cargo"]).to_lower()] if carrying > 0 else "Empty"
	refs["load"]["bar"].value = 100.0 * carrying / maxf(1.0, vehicle["capacity"])
	Kit.set_condition_bar(refs["condition"], vehicle["condition"])
	refs["condition_value"].text = str(roundi(vehicle["condition"]))
	refs["income"]["value"].text = Kit.money(vehicle["income"])
	refs["upkeep"]["value"].text = Kit.money(vehicle["upkeep"])
	refs["profit"]["value"].text = Kit.money(vehicle["profit"])
	refs["repair"].text = repair_text(vehicle)


func show_vehicles(vehicles: Array) -> void:
	var seen := {}
	for vehicle in vehicles:
		var id: int = vehicle["id"]
		seen[id] = true
		if not _rows.has(id):
			_add(id, vehicle)
		var entry: Dictionary = _rows[id]
		entry["title"].text = title_of(vehicle)
		fill_view(entry["view"], vehicle)
		entry["ask"].disabled = vehicle["repair_phase"] != 0 or vehicle["wrecked"]
		Kit.show_policy(entry["policy"], vehicle["policy"], "Maintenance: %s (from the %s)" % [vehicle["policy_text"], vehicle["policy_source"]])
		entry["go"].pressed.disconnect(entry["go_call"])
		entry["go_call"] = func() -> void: hud.town_focus.emit(vehicle["x"], vehicle["z"])
		entry["go"].pressed.connect(entry["go_call"])
		entry["sell"].disabled = vehicle["wrecked"]
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


func _add(id: int, vehicle: Dictionary) -> void:
	var row := Kit.card()
	var body: VBoxContainer = row.get_meta("body")
	var title := Kit.heading("")
	body.add_child(title)
	var view := make_view()
	body.add_child(view["box"])
	var buttons := Kit.flow()
	body.add_child(buttons)
	var go := Kit.button("Go to", Callable())
	var go_call := func() -> void: hud.town_focus.emit(vehicle["x"], vehicle["z"])
	go.pressed.connect(go_call)
	buttons.add_child(go)
	buttons.add_child(Kit.button("To depot", func() -> void: hud.vehicle_action.emit("depot", id),
		"Take it off its line: it drives to the nearest depot and waits there, ready for another line. What it carries is lost."))
	var repairs := Kit.row()
	body.add_child(repairs)
	var kind := OptionButton.new()
	for i in Kit.REPAIR_NAMES.size():
		kind.add_item(Kit.REPAIR_NAMES[i], i)
	kind.select(1)
	kind.tooltip_text = "The repair to ask for."
	repairs.add_child(kind)
	var ask := Kit.button("Repair at next stop", func() -> void: hud.repair_requested.emit(id, kind.get_selected_id()),
		"At its next stop it unloads, loads nothing and goes to the nearest depot that can do this repair, then comes back to its line.")
	ask.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	repairs.add_child(ask)
	buttons.add_child(Kit.spacer())
	var sell := Kit.button("Sell", func() -> void: hud.vehicle_action.emit("sell", id), "Sell the vehicle for part of its price. What it carries is lost.")
	sell.theme_type_variation = "DangerButton"
	buttons.add_child(sell)
	var policy := Kit.make_policy_block(2, id, hud.policy_set.emit)
	body.add_child(Kit.fold("Maintenance policy", policy["box"]))
	var separator := add_row(_box, row)
	separator.visible = false
	_rows[id] = {"row": row, "title": title, "view": view, "separator": separator, "go": go, "go_call": go_call, "sell": sell, "ask": ask, "policy": policy}


func rebuild_buy_buttons() -> void:
	for child in _buy_box.get_children():
		child.queue_free()
	var index := _depot_choice.get_selected()
	if index < 0 or index >= _depots.size():
		return
	var depot: Dictionary = _depots[index]
	var depot_id: int = depot["id"]
	if depot["rail"]:
		for option in hud.train_options:
			var buy := Kit.button("%s\n+ %s" % [option["label"], Kit.money(option["price"])], func() -> void: hud.garage_buy.emit(true, depot_id, option["id"]),
				"Carries %d, costs %s a day to run." % [option["capacity"], Kit.money(option["upkeep"])])
			buy.theme_type_variation = "AccentButton"
			_buy_box.add_child(buy)
	else:
		for model in hud.models:
			var buy := Kit.button("%s\n+ %s" % [model["name"], Kit.money(model["price"])], func() -> void: hud.garage_buy.emit(false, depot_id, model["id"]),
				"Carries %d, costs %s a day to run." % [model["capacity"], Kit.money(model["upkeep"])])
			buy.theme_type_variation = "AccentButton"
			_buy_box.add_child(buy)


## The depot choice and the vehicles waiting in depots (with a choice of line for each), from the host's depots, parked vehicles and lines.
func show_garage(depots: Array, parked: Array, lines: Array) -> void:
	var key := ""
	for depot in depots:
		key += "%s%d;" % ["r" if depot["rail"] else "d", depot["id"]]
	if key != _depots_key:
		var previous := ""
		var shown := _depot_choice.get_selected()
		if shown >= 0 and shown < _depots.size():
			previous = _depots[shown]["name"]
		_depots_key = key
		_depots = depots
		_depot_choice.clear()
		var keep := 0
		for i in depots.size():
			_depot_choice.add_item(depots[i]["name"])
			if depots[i]["name"] == previous:
				keep = i
		if not depots.is_empty():
			_depot_choice.select(keep)
		_depot_choice.visible = not depots.is_empty()
		_hint.visible = depots.is_empty()
		rebuild_buy_buttons()

	var seen := {}
	for entry in parked:
		var id: int = entry["id"]
		seen[id] = true
		if not _parked_rows.has(id):
			_add_parked(id)
		var row: Dictionary = _parked_rows[id]
		var label: Label = row["label"]
		label.text = "%s, waiting in %s\nWorth %s new, condition %d" % [entry["model"], entry["depot"], Kit.money(entry["price"]), roundi(entry["condition"])]
		if entry["repair"] != "":
			label.text += "\n" + entry["repair"]
		var repair_kind: OptionButton = row["repair_kind"]
		for i in Kit.REPAIR_NAMES.size():
			repair_kind.set_item_disabled(i, not entry["can_repair"][i])
			repair_kind.set_item_text(i, "%s (%s)" % [Kit.REPAIR_NAMES[i], Kit.money(entry["repair_price"][i])])
		row["repair_button"].disabled = entry["repair"] != ""
		var rail: bool = entry["rail"]
		var offered := []
		var lines_key := ""
		for line in lines:
			if line["rail"] == rail:
				offered.append(line)
				lines_key += "%d:%s;" % [line["id"], line["name"]]
		var choice: OptionButton = row["choice"]
		if row["lines_key"] != lines_key and not choice.get_popup().visible:
			row["lines_key"] = lines_key
			var selected := choice.get_selected_id()
			choice.clear()
			for line in offered:
				choice.add_item(line["name"], line["id"])
			if choice.item_count > 0:
				choice.select(maxi(0, choice.get_item_index(selected)))
		row["assign"].disabled = choice.item_count == 0
		row["assign"].tooltip_text = "Send it out of its depot to work this line." if choice.item_count > 0 else "Make a %s line first." % ("rail" if rail else "road")
		row["go"].pressed.disconnect(row["go_call"])
		row["go_call"] = func() -> void: hud.town_focus.emit(entry["x"], entry["z"])
		row["go"].pressed.connect(row["go_call"])
	Kit.drop_unseen(_parked_rows, seen)
	_parked_header.visible = not _parked_rows.is_empty()


func _add_parked(id: int) -> void:
	var row := Kit.card()
	var body: VBoxContainer = row.get_meta("body")
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	body.add_child(label)
	var buttons := Kit.row()
	body.add_child(buttons)
	var choice := OptionButton.new()
	choice.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	buttons.add_child(choice)
	var assign := Kit.button("Put on line", func() -> void:
		if choice.item_count > 0:
			hud.garage_assign.emit(id, choice.get_selected_id()))
	buttons.add_child(assign)
	var go := Kit.button("Go to", Callable())
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	buttons.add_child(go)
	buttons.add_child(Kit.button("Sell", func() -> void: hud.garage_sell.emit(id), "Sell it for part of its price."))
	var repairs := Kit.row()
	body.add_child(repairs)
	var repair_kind := OptionButton.new()
	for i in Kit.REPAIR_NAMES.size():
		repair_kind.add_item(Kit.REPAIR_NAMES[i], i)
	repair_kind.select(1)
	repair_kind.tooltip_text = "Greyed out repairs need a better depot (see the Depots window)."
	repair_kind.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	repairs.add_child(repair_kind)
	var repair_button := Kit.button("Repair", func() -> void: hud.parked_repair.emit(id, repair_kind.get_selected_id()), "Take it into a bay of the depot it stands in.")
	repairs.add_child(repair_button)
	var separator := add_row(_parked_box, row)
	separator.visible = false
	_parked_rows[id] = {"row": row, "separator": separator, "label": label, "choice": choice, "lines_key": "", "assign": assign, "go": go, "go_call": go_call,
		"repair_kind": repair_kind, "repair_button": repair_button}
