extends "res://game/ui/page.gd"
## Network > Depots: for each depot its level and bays, what is being repaired and who waits, and the buttons that raise its level.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # "r3" / "d1" -> {"row", "separator", "label", "jobs", "upgrades", "upgrade_key", "go", "go_call"}


func _build() -> void:
	add_child(Kit.note("Depots repair your vehicles. Bays are how many vehicles are repaired at once; the rest wait inside. A Simple depot patches, a Workshop also services, a Complex also overhauls."))
	_box = Kit.column()
	add_child(_box)
	_empty = Kit.note("No depots yet.")
	add_child(_empty)


## The state of a depot as lines: its level and bays, what stops it working, the repairs in its bays (also in its own window).
static func text_of(depot: Dictionary) -> String:
	var text := "%s, %d bays" % [depot["level_name"], depot["bays"]]
	if depot.get("shut_days", 0.0) > 0.0:
		text += "\nBombed: shut for %s more days, no repairs, no crews, nothing bought or sold" % str(snappedf(depot["shut_days"], 0.1))
	if not depot.get("connected", true):
		text += "\nNot connected: no road reaches its door yet (a road built within 60 m of it gets a path to it). No vehicles, service or crews until then."
	for job in depot["jobs"]:
		text += "\n%s: %s%s" % [job["label"], String(Kit.REPAIR_NAMES[job["kind"]]).to_lower(), (" (%d s left)" % ceili(job["seconds"])) if job["working"] else ", waiting for a bay"]
	return text


static func key_of(depot: Dictionary) -> String:
	return "%s%d" % ["r" if depot["rail"] else "d", depot["id"]]


func show_depots(depots: Array) -> void:
	var seen := {}
	for depot in depots:
		var key := key_of(depot)
		seen[key] = true
		if not _rows.has(key):
			_add(key)
		var entry: Dictionary = _rows[key]
		entry["label"].text = text_of(depot)
		entry["title"].text = depot["name"]
		fill_upgrades(entry, depot)
		entry["go"].pressed.disconnect(entry["go_call"])
		entry["go_call"] = func() -> void: hud.town_focus.emit(depot["x"], depot["z"])
		entry["go"].pressed.connect(entry["go_call"])
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


## The upgrade buttons of a depot, made again only when what is on offer changes (<entry> holds "upgrades" and "upgrade_key").
func fill_upgrades(entry: Dictionary, depot: Dictionary) -> void:
	var upgrade_key := ""
	for upgrade in depot["upgrades"]:
		upgrade_key += "%d:%d;" % [upgrade["level"], upgrade["price"]]
	if upgrade_key == entry["upgrade_key"]:
		return
	entry["upgrade_key"] = upgrade_key
	for child in entry["upgrades"].get_children():
		child.queue_free()
	for upgrade in depot["upgrades"]:
		var rail: bool = depot["rail"]
		var depot_id: int = depot["id"]
		var level: int = upgrade["level"]
		entry["upgrades"].add_child(Kit.button("Upgrade to %s (%s)" % [upgrade["name"], Kit.money(upgrade["price"])], func() -> void: hud.depot_upgrade.emit(rail, depot_id, level),
			"Pay the difference in price; the depot keeps its place."))


func _add(key: String) -> void:
	var row := Kit.card()
	var body: VBoxContainer = row.get_meta("body")
	var head := Kit.row()
	var title := Kit.heading("")
	title.autowrap_mode = TextServer.AUTOWRAP_OFF
	head.add_child(title)
	head.add_child(Kit.spacer())
	var go := Kit.button("Go to", Callable())
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	head.add_child(go)
	body.add_child(head)
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	body.add_child(label)
	var upgrades := Kit.flow()
	body.add_child(upgrades)
	body.add_child(Kit.note("Upgrading happens in place and costs only the difference in price."))
	var separator := add_row(_box, row)
	separator.visible = false
	_rows[key] = {"row": row, "separator": separator, "label": label, "title": title, "upgrades": upgrades, "upgrade_key": "?", "go": go, "go_call": go_call}


func _upgrade_button(text: String, action: Callable) -> Button:
	var b := Kit.button(text, action, "Pay the difference in price; the depot keeps its place.")
	b.theme_type_variation = "AccentButton"
	return b
