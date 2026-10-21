extends "res://game/ui/page.gd"
## Region > Gateways: one block per gateway: whether a station reaches it, one row per import with its price and a button to ask for a
## shipment, and what has been exported.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # gateway id -> {"row", "title", "offers": {cargo -> {"label", "button"}}, "exports", "separator"}


func _build() -> void:
	add_child(Kit.note("The Region's links with the rest of the world. Build a road to a gateway and a station beside it (a road station for a road gateway, a rail station for a rail gateway). Imported cargo arrives there to be hauled inland; what your vehicles unload there is exported."))
	_box = Kit.column()
	add_child(_box)
	_empty = Kit.note("There are no gateways on this map.")
	add_child(_empty)


func show_gateways(gateways: Array) -> void:
	var seen := {}
	for gateway in gateways:
		var id: int = gateway["id"]
		seen[id] = true
		if not _rows.has(id):
			_add(id, gateway)
		var row: Dictionary = _rows[id]
		row["title"].text = "%s   (%s)\n%s%s" % [gateway["name"], String(gateway["kind"]).to_lower(),
			"A station reaches it." if gateway["covered"] else "No station reaches it: build a %s station beside it." % String(gateway["kind"]).to_lower(),
			"\nBombed: shut for %s more days, no shipment lands and nothing is taken." % str(snappedf(gateway["shut_days"], 0.1)) if gateway.get("shut_days", 0.0) > 0.0 else ""]
		for offer in gateway["offers"]:
			var entry: Dictionary = row["offers"][offer["cargo"]]
			var text := "%s   %d at %s each%s   %d here   %d arrived so far\n%s" % [
				offer["name"], offer["units"], Kit.money(offer["price"]), "   (rationed)" if offer.get("rationed", false) else "", offer["stock"], offer["imported"], offer["status"]]
			if offer["seconds"] > 0.0:
				text += "   (%d s)" % ceili(offer["seconds"])
			entry["label"].text = text
			entry["button"].text = "Ask for %d %s: %s" % [offer["units"], String(offer["name"]).to_lower(), Kit.money(offer["units"] * offer["price"])]
			entry["button"].disabled = not offer["can"]
		var exported := ""
		for item in gateway["exports"]:
			exported += ("" if exported.is_empty() else ", ") + "%d %s" % [item["units"], String(item["name"]).to_lower()]
		row["exports"].text = "Exported so far: " + (exported if not exported.is_empty() else "nothing")
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


func _add(id: int, gateway: Dictionary) -> void:
	var row := Kit.column()
	var title := Kit.heading("")
	row.add_child(title)
	var go := go_button(gateway["x"], gateway["z"])
	go.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	row.add_child(go)
	var offers := {}
	for offer in gateway["offers"]:
		var label := Label.new()
		label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		row.add_child(label)
		var cargo: String = offer["cargo"]
		var b := Kit.button("", func() -> void: hud.import_requested.emit(id, cargo))
		b.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
		row.add_child(b)
		offers[cargo] = {"label": label, "button": b}
	var exports := Kit.note("")
	row.add_child(exports)
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "title": title, "offers": offers, "exports": exports, "separator": separator}
