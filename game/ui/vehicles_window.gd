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
var _parked_header: Label
var _parked_box: VBoxContainer
var _parked_rows: Dictionary = {} # garage id -> {"row", "separator", "label", "choice", "lines_key", "assign", "go", "go_call", "repair_kind", "repair_button"}


func _build() -> void:
	add_child(Kit.heading("Buy a vehicle"))
	add_child(Kit.note("It waits in the depot you pick until you put it on a line. Vehicles also come out of a depot at once when bought from the Lines window."))
	_depot_choice = OptionButton.new()
	_depot_choice.item_selected.connect(func(_index: int) -> void: rebuild_buy_buttons())
	add_child(_depot_choice)
	_buy_box = Kit.flow()
	add_child(_buy_box)
	_hint = Kit.note("No depot yet: build one under Transport > Depot, or under Rail > Rail depot for trains.")
	add_child(_hint)
	add_child(HSeparator.new())
	_parked_header = Kit.heading("Waiting in depots")
	_parked_header.visible = false
	add_child(_parked_header)
	_parked_box = Kit.column()
	add_child(_parked_box)
	_box = Kit.column()
	add_child(_box)
	_empty = Kit.note("No vehicles on the road yet. Buy one above and put it on a line.")
	add_child(_empty)


## What a row says of a vehicle (also the text of its own window).
static func text_of(vehicle: Dictionary) -> String:
	var id: int = vehicle["id"]
	var carrying := "empty"
	if vehicle["load"] > 0:
		carrying = "%d of %d %s" % [vehicle["load"], vehicle["capacity"], String(vehicle["cargo"]).to_lower()]
	return "%s %d   %s\n%s\ncarrying %s\nearned %s   upkeep %s   profit %s" % [
		vehicle["model"], id + 1 if id >= 0 else -id, vehicle["line_name"], vehicle["state"], carrying,
		Kit.money(vehicle["income"]), Kit.money(vehicle["upkeep"]), Kit.money(vehicle["profit"])]


static func repair_text(vehicle: Dictionary) -> String:
	var note := "last repaired %d days ago" % vehicle["days_since"]
	if vehicle["patched"]:
		note += "   patched: wears faster until a service"
	return note if vehicle["repair"] == "" else note + "\n" + vehicle["repair"]


func show_vehicles(vehicles: Array) -> void:
	var seen := {}
	for vehicle in vehicles:
		var id: int = vehicle["id"]
		seen[id] = true
		if not _rows.has(id):
			_add(id, vehicle)
		var entry: Dictionary = _rows[id]
		var label: Label = entry["label"]
		label.text = text_of(vehicle)
		label.add_theme_color_override("font_color", vehicle["color"])
		Kit.set_condition_bar(entry["condition"], vehicle["condition"])
		entry["repair"].text = repair_text(vehicle)
		entry["ask"].disabled = vehicle["repair_phase"] != 0 or vehicle["wrecked"]
		Kit.show_policy(entry["policy"], vehicle["policy"], "Maintenance: %s (from the %s)" % [vehicle["policy_text"], vehicle["policy_source"]])
		entry["go"].pressed.disconnect(entry["go_call"])
		entry["go_call"] = func() -> void: hud.town_focus.emit(vehicle["x"], vehicle["z"])
		entry["go"].pressed.connect(entry["go_call"])
		entry["sell"].disabled = vehicle["wrecked"]
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


func _add(id: int, vehicle: Dictionary) -> void:
	var row := Kit.column()
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(label)
	var condition_row := Kit.row()
	row.add_child(condition_row)
	var caption := Label.new()
	caption.theme_type_variation = "Dim"
	caption.text = "Condition"
	condition_row.add_child(caption)
	var condition := Kit.make_condition_bar()
	condition_row.add_child(condition["bar"])
	var repair_note := Kit.note("")
	row.add_child(repair_note)
	var buttons := Kit.flow()
	row.add_child(buttons)
	var go := Kit.button("Go to", Callable())
	var go_call := func() -> void: hud.town_focus.emit(vehicle["x"], vehicle["z"])
	go.pressed.connect(go_call)
	buttons.add_child(go)
	buttons.add_child(Kit.button("To depot", func() -> void: hud.vehicle_action.emit("depot", id),
		"Take it off its line: it drives to the nearest depot and waits there, ready for another line. What it carries is lost."))
	var sell := Kit.button("Sell", func() -> void: hud.vehicle_action.emit("sell", id), "Sell the vehicle for part of its price. What it carries is lost.")
	buttons.add_child(sell)
	var repairs := Kit.row()
	row.add_child(repairs)
	var kind := OptionButton.new()
	for i in Kit.REPAIR_NAMES.size():
		kind.add_item(Kit.REPAIR_NAMES[i], i)
	kind.select(1)
	kind.tooltip_text = "The repair to ask for."
	repairs.add_child(kind)
	var ask := Kit.button("Repair at next stop", func() -> void: hud.repair_requested.emit(id, kind.get_selected_id()),
		"At its next stop it unloads, loads nothing and goes to the nearest depot that can do this repair, then comes back to its line.")
	repairs.add_child(ask)
	var policy := Kit.make_policy_block(2, id, hud.policy_set.emit)
	row.add_child(policy["box"])
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "label": label, "separator": separator, "go": go, "go_call": go_call, "sell": sell,
		"condition": condition, "repair": repair_note, "ask": ask, "policy": policy}


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
			_buy_box.add_child(Kit.button("+ %s (%s)" % [option["label"], Kit.money(option["price"])], func() -> void: hud.garage_buy.emit(true, depot_id, option["id"]),
				"Carries %d, costs %s a day to run." % [option["capacity"], Kit.money(option["upkeep"])]))
	else:
		for model in hud.models:
			_buy_box.add_child(Kit.button("+ %s (%s)" % [model["name"], Kit.money(model["price"])], func() -> void: hud.garage_buy.emit(false, depot_id, model["id"]),
				"Carries %d, costs %s a day to run." % [model["capacity"], Kit.money(model["upkeep"])]))


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
		label.text = "%s   waiting in %s\nworth %s new   Condition %d" % [entry["model"], entry["depot"], Kit.money(entry["price"]), roundi(entry["condition"])]
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
	var row := Kit.column()
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(label)
	var buttons := Kit.row()
	row.add_child(buttons)
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
	row.add_child(repairs)
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
	_parked_rows[id] = {"row": row, "separator": separator, "label": label, "choice": choice, "lines_key": "", "assign": assign, "go": go, "go_call": go_call,
		"repair_kind": repair_kind, "repair_button": repair_button}
