extends "res://game/ui/page.gd"
## Network > Depots: for each depot its level and bays, what is being repaired and who waits, and the buttons that raise its level.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # "r3" / "d1" -> {"row", "separator", "label", "jobs", "upgrades", "upgrade_key", "go", "go_call"}


func _build() -> void:
	add_child(Kit.note("A Simple depot patches, a Workshop also services, a Complex also overhauls. Bays: how many vehicles it repairs at once; the rest wait inside. A depot is upgraded in place for the difference in price."))
	_box = Kit.column()
	add_child(_box)
	_empty = Kit.note("No depots yet.")
	add_child(_empty)


## What a row says of a depot (also the text of its own window).
static func text_of(depot: Dictionary) -> String:
	var text := "%s   %s, %d bays%s" % [depot["name"], depot["level_name"], depot["bays"],
		"   BOMBED: shut for %s more days, no repairs, no crews, nothing bought or sold" % str(snappedf(depot["shut_days"], 0.1)) if depot.get("shut_days", 0.0) > 0.0 else ""]
	var jobs := ""
	for job in depot["jobs"]:
		jobs += "\n%s: %s%s" % [job["label"], String(Kit.REPAIR_NAMES[job["kind"]]).to_lower(), (" (%d s left)" % ceili(job["seconds"])) if job["working"] else ", waiting for a bay"]
	return text + jobs


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
	var row := Kit.column()
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(label)
	var buttons := Kit.flow()
	row.add_child(buttons)
	var upgrades := Kit.row()
	buttons.add_child(upgrades)
	var go := Kit.button("Go to", Callable())
	var go_call := func() -> void: pass
	go.pressed.connect(go_call)
	buttons.add_child(go)
	var separator := add_row(_box, row)
	_rows[key] = {"row": row, "separator": separator, "label": label, "upgrades": upgrades, "upgrade_key": "?", "go": go, "go_call": go_call}
