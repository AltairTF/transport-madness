extends "res://game/ui/page.gd"
## Region > Gateways: a help fold, a switch between the gateways, and the chosen one as a panel: its kind, a warning when no station
## reaches it, the imports as cards with an Order button, and what has been exported.

const HELP := "The Region's links with the rest of the world. Build a road to a gateway and a station beside it (a road station for a road gateway, a rail station for a rail gateway). Imported cargo arrives there to be hauled inland; what your vehicles unload there is exported."

var _switch: HFlowContainer
var _body: VBoxContainer
var _help: VBoxContainer
var _empty: Label
var _selected := -1
var _shown_signature := ""


func _build() -> void:
	var fold := Kit.fold("How gateways work", Kit.note(HELP))
	add_child(fold)
	_switch = Kit.flow()
	add_child(_switch)
	_body = Kit.column()
	_body.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	add_child(_body)
	_empty = Kit.note("There are no gateways on this map.")
	add_child(_empty)


func show_gateways(gateways: Array) -> void:
	_empty.visible = gateways.is_empty()
	var ids: Array = gateways.map(func(g: Dictionary) -> int: return g["id"])
	if not ids.has(_selected):
		_selected = ids[0] if not ids.is_empty() else -1
	var signature := str(gateways) + str(_selected)
	if signature == _shown_signature:
		return
	_shown_signature = signature
	for child in _switch.get_children():
		child.queue_free()
	for child in _body.get_children():
		child.queue_free()
	_switch.visible = gateways.size() > 1
	for gateway in gateways:
		var id: int = gateway["id"]
		var tab := Kit.button(gateway["name"], func() -> void:
			_selected = id
			_shown_signature = ""
			show_gateways(hud.latest("gateway")))
		tab.toggle_mode = true
		tab.button_pressed = id == _selected
		_switch.add_child(tab)
		if id == _selected:
			_fill(gateway)


func _fill(gateway: Dictionary) -> void:
	var head := Kit.row()
	var title := Kit.heading(gateway["name"])
	head.add_child(title)
	title.autowrap_mode = TextServer.AUTOWRAP_OFF
	head.add_child(Kit.badge(String(gateway["kind"])))
	head.add_child(Kit.spacer())
	head.add_child(go_button(gateway["x"], gateway["z"]))
	_body.add_child(head)
	var kind := String(gateway["kind"]).to_lower()
	if gateway.get("shut_days", 0.0) > 0.0:
		_body.add_child(Kit.notice("Bombed", "Shut for %s more days: no shipment lands and nothing is taken." % str(snappedf(gateway["shut_days"], 0.1))))
	if not gateway["covered"]:
		_body.add_child(Kit.notice("Not connected", "No station reaches this gateway. Build a %s station beside it." % kind))

	var open: Array = gateway["offers"].filter(func(o: Dictionary) -> bool: return o["can"] or o["status"] == "On its way")
	var closed: Array = gateway["offers"].filter(func(o: Dictionary) -> bool: return not (o["can"] or o["status"] == "On its way"))
	if not open.is_empty():
		_body.add_child(Kit.section("Available imports", "Price is per order"))
		for offer in open:
			_body.add_child(_offer_card(gateway["id"], offer, false))
	if not closed.is_empty():
		_body.add_child(Kit.section("Not yet available"))
		for offer in closed:
			_body.add_child(_offer_card(gateway["id"], offer, true))

	var exported := ""
	for item in gateway["exports"]:
		exported += ("" if exported.is_empty() else ", ") + "%d %s" % [item["units"], String(item["name"]).to_lower()]
	var foot := Kit.row()
	var exported_label := Kit.note("Exported so far")
	exported_label.autowrap_mode = TextServer.AUTOWRAP_OFF
	foot.add_child(exported_label)
	foot.add_child(Kit.spacer())
	var value := Label.new()
	value.text = exported if not exported.is_empty() else "Nothing yet"
	foot.add_child(value)
	_body.add_child(foot)


func _offer_card(gateway_id: int, offer: Dictionary, muted: bool) -> Control:
	var card := PanelContainer.new()
	card.theme_type_variation = "CardMuted" if muted else "Card"
	var row := Kit.row()
	card.add_child(row)
	var text := Kit.column()
	text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var name_label := Label.new()
	name_label.text = offer["name"]
	text.add_child(name_label)
	text.add_child(Kit.note("%d per order · %s each%s" % [offer["units"], Kit.money(offer["price"]), " (rationed)" if offer.get("rationed", false) else ""]))
	if muted:
		var why := Kit.note(offer["status"])
		why.theme_type_variation = "Warn"
		text.add_child(why)
	else:
		var status := "%d here · %d arrived" % [offer["stock"], offer["imported"]]
		if offer["seconds"] > 0.0:
			status += " · %s (%d s)" % [offer["status"], ceili(offer["seconds"])]
		text.add_child(Kit.note(status))
	row.add_child(text)
	var cargo: String = offer["cargo"]
	var order := Kit.button("Order %s" % Kit.money(offer["units"] * offer["price"]), func() -> void: hud.import_requested.emit(gateway_id, cargo))
	order.disabled = not offer["can"]
	if not muted:
		order.theme_type_variation = "AccentButton"
	order.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	row.add_child(order)
	return card
