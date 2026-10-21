extends "res://game/ui/page.gd"
## Region > Industries: one row per industry: what it makes and holds, who takes it, what it is doing, with a button that takes the camera there.

var _box: VBoxContainer
var _empty: Label
var _rows: Dictionary = {} # industry id -> {"row", "label", "status", "separator"}


func _build() -> void:
	_box = Kit.column()
	add_child(_box)
	_empty = Kit.note("There are no industries on this map.")
	add_child(_empty)


## What a row says of an industry (also the text of its own window).
static func text_of(industry: Dictionary) -> String:
	var id: int = industry["id"]
	var title: String = "%s %d" % [industry["name"], id + 1]
	if industry.get("deposit", false):
		title += "   (deposit)"
	var text := "%s\nmakes %s   %d of %d waiting for a truck   %d made so far" % [
		title, industry["output"], industry["stock"], industry["max_output"], industry["produced"]]
	if industry.get("battery", false):
		text = "%s\nfires at aircraft within %d m   ammunition for %d seconds of fire" % [title, roundi(industry["range"]), roundi(industry["fire_seconds"])]
	elif industry.get("supply", 0.0) > 0.0:
		text = "%s\ngives %d MW to the region while it has coal (now %d MW)   %d coal burned so far" % [
			title, roundi(industry["supply"]), roundi(industry["supply"] * industry["load"]), industry["produced"]]
	elif industry.get("demand", 0.0) > 0.0:
		text += "   uses %d MW" % roundi(industry["demand"])
	if industry.get("jobs", 0) > 0:
		text += "\n%d of %d jobs filled yesterday   staffing %d%%" % [industry["staff"], industry["jobs"], roundi(industry["staffing"] * 100.0)]
	var used_by: String = industry.get("used_by", "")
	if used_by != "":
		text += "\nused by %s" % used_by
	for input in industry["inputs"]:
		text += "\nneeds %s   %d of %d in stock%s" % [input["cargo"], input["stock"], input["max"], "   (imported: arrives through a gateway)" if input.get("imported", false) else ""]
	return text


static func colour_status(status: Label, industry: Dictionary) -> void:
	status.text = industry.get("status", "")
	match industry.get("status_kind", "ok"):
		"ok":
			status.add_theme_color_override("font_color", Color(0.45, 0.85, 0.5))
		"warn":
			status.add_theme_color_override("font_color", Color(1.0, 0.7, 0.35))
		_:
			status.add_theme_color_override("font_color", UiTheme.TEXT_DIM)


func show_industries(industries: Array) -> void:
	var seen := {}
	for industry in industries:
		var id: int = industry["id"]
		seen[id] = true
		if not _rows.has(id):
			_add(id, industry)
		var row: Dictionary = _rows[id]
		row["label"].text = text_of(industry)
		colour_status(row["status"], industry)
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


func _add(id: int, industry: Dictionary) -> void:
	var row := Kit.column()
	var label := Label.new()
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(label)
	var status := Label.new()
	status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	row.add_child(status)
	var go := go_button(industry["x"], industry["z"])
	go.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	row.add_child(go)
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "label": label, "status": status, "separator": separator}
