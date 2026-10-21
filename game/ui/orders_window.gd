extends "res://game/ui/page.gd"
## Government > Orders: one block per order on offer or under way, then the last few that were closed.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # order id -> {"row", "title", "status", "buttons", "accept", "decline", "go", "separator", "state"}


func _build() -> void:
	add_child(Kit.note("What the government asks of the committee. An order can be accepted once stations already cover the place the cargo comes from and where it goes; the clock starts when you accept. Meeting it pays its reward and raises the standing; failing costs a fine and standing."))
	_box = Kit.column()
	add_child(_box)
	_empty = Kit.note("The government has asked for nothing yet.")
	add_child(_empty)


func show_orders(orders: Array) -> void:
	var seen := {}
	for order in orders:
		var id: int = order["id"]
		seen[id] = true
		if not _rows.has(id):
			_add(id, order)
		var row: Dictionary = _rows[id]
		var state: String = order["state"]
		row["title"].text = "%s\n%s" % [order["title"], _terms(order)]
		var open: bool = state == "Offered" or state == "Active"
		var status := ""
		match state:
			"Offered":
				status = "Offered: %d days to accept it.   %s" % [ceili(order["days_left"]), "" if order["can"] else order["why"]]
			"Active":
				status = "Under way: %d of %d delivered, %d days left" % [order["progress"], order["units"], ceili(order["days_left"])]
			"Met":
				status = "Met. %s paid." % Kit.money(order["paid"])
			"Late":
				status = "Late: %d of %d delivered, %s paid." % [order["progress"], order["units"], Kit.money(order["paid"])]
			"Failed":
				status = "Failed: %d of %d delivered, net %s." % [order["progress"], order["units"], Kit.money(order["paid"])]
			_:
				status = state
		row["status"].text = status
		row["status"].theme_type_variation = "" if open else "Dim"
		row["buttons"].visible = state == "Offered"
		row["accept"].disabled = not order["can"]
		row["go"].visible = order["place"]
		row["state"] = state
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


func _terms(order: Dictionary) -> String:
	return "Reward %s   standing +%s   %d days to deliver" % [Kit.money(order["reward"]), str(snappedf(order["standing"], 0.1)), order["days"]]


func _add(id: int, order: Dictionary) -> void:
	var row := Kit.column()
	var title := Kit.heading("")
	row.add_child(title)
	var status := Label.new()
	status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(status)
	var buttons := Kit.row()
	row.add_child(buttons)
	var accept := Kit.button("Accept", func() -> void: hud.order_answered.emit(id, true))
	buttons.add_child(accept)
	var decline := Kit.button("Decline", func() -> void: hud.order_answered.emit(id, false))
	buttons.add_child(decline)
	var go := go_button(order["x"], order["z"])
	buttons.add_child(go)
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "title": title, "status": status, "buttons": buttons, "accept": accept, "decline": decline, "go": go, "separator": separator, "state": ""}
