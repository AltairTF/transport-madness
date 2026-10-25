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


static func title_of(industry: Dictionary) -> String:
	var title: String = "%s %d" % [industry["name"], industry["id"] + 1]
	if industry.get("deposit", false):
		title += " (deposit)"
	return title


## The lines that have no place in the view: what a plant or battery does, staffing, who takes the output.
static func details_text(industry: Dictionary) -> String:
	var lines: Array[String] = []
	if industry.get("battery", false):
		lines.append("Fires at aircraft within %d m, ammunition for %d seconds of fire" % [roundi(industry["range"]), roundi(industry["fire_seconds"])])
	elif industry.get("supply", 0.0) > 0.0:
		lines.append("Gives %d MW to the region while it has coal (now %d MW), %d coal burned so far" % [roundi(industry["supply"]), roundi(industry["supply"] * industry["load"]), industry["produced"]])
	if industry.get("jobs", 0) > 0:
		lines.append("%d of %d jobs filled yesterday, staffing %d%%" % [industry["staff"], industry["jobs"], roundi(industry["staffing"] * 100.0)])
	var used_by: String = industry.get("used_by", "")
	if used_by != "":
		lines.append("Used by %s" % used_by)
	return "\n".join(lines)


## The panel of one industry (the list and the object window share it): its state, what it needs and makes, the figures.
static func make_view() -> Dictionary:
	var box := Kit.column()
	box.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	var refs := {"box": box}
	var notice := PanelContainer.new()
	notice.theme_type_variation = "Notice"
	var notice_body := Kit.column()
	notice.add_child(notice_body)
	refs["notice"] = notice
	refs["status"] = Label.new()
	notice_body.add_child(refs["status"])
	box.add_child(notice)
	var flow := Kit.row()
	var needs := Kit.card()
	refs["needs_card"] = needs
	refs["needs_body"] = needs.get_meta("body")
	needs.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var makes := Kit.card()
	makes.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var makes_body: VBoxContainer = makes.get_meta("body")
	makes_body.add_child(Kit.section("Makes"))
	refs["output"] = Label.new()
	makes_body.add_child(refs["output"])
	refs["output_meter"] = Kit.meter("", UiTheme.GOOD)
	refs["output_meter"]["caption"].visible = false
	makes_body.add_child(refs["output_meter"]["box"])
	flow.add_child(needs)
	var arrow := Label.new()
	arrow.text = "→"
	arrow.theme_type_variation = "Dim"
	arrow.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	flow.add_child(arrow)
	flow.add_child(makes)
	box.add_child(flow)
	refs["flow"] = flow
	var figures := Kit.row()
	refs["made"] = Kit.stat("Made so far")
	refs["power"] = Kit.stat("Power use")
	figures.add_child(refs["made"]["box"])
	figures.add_child(refs["power"]["box"])
	box.add_child(figures)
	refs["details"] = Kit.note("")
	box.add_child(refs["details"])
	return refs


static func fill_view(refs: Dictionary, industry: Dictionary) -> void:
	colour_status(refs["status"], industry)
	var kind: String = industry.get("status_kind", "ok")
	var notice: PanelContainer = refs["notice"]
	notice.theme_type_variation = "Notice" if kind == "warn" else "Card"
	var body: VBoxContainer = refs["needs_body"]
	for child in body.get_children():
		child.queue_free()
	body.add_child(Kit.section("Needs"))
	for input in industry["inputs"]:
		var name := Label.new()
		name.text = String(input["cargo"])
		body.add_child(name)
		var meter := Kit.meter("", UiTheme.METER)
		meter["caption"].visible = false
		meter["bar"].value = 100.0 * input["stock"] / maxf(1.0, input["max"])
		body.add_child(meter["box"])
		body.add_child(Kit.note("%d of %d in stock%s" % [input["stock"], input["max"], ", arrives through a gateway" if input.get("imported", false) else ""]))
	refs["needs_card"].visible = not industry["inputs"].is_empty()
	var has_output: bool = not industry.get("battery", false) and industry.get("supply", 0.0) <= 0.0
	refs["output"].text = String(industry["output"])
	refs["output_meter"]["bar"].value = 100.0 * industry["stock"] / maxf(1.0, industry["max_output"])
	refs["output_meter"]["value"].text = "%d of %d waiting for a truck" % [industry["stock"], industry["max_output"]]
	refs["flow"].get_child(2).visible = has_output
	refs["made"]["value"].text = str(int(industry["produced"]))
	refs["made"]["box"].visible = has_output
	refs["power"]["box"].visible = industry.get("demand", 0.0) > 0.0
	refs["power"]["value"].text = "%d MW" % roundi(industry.get("demand", 0.0))
	var details := details_text(industry)
	refs["details"].text = details
	refs["details"].visible = not details.is_empty()


static func colour_status(status: Label, industry: Dictionary) -> void:
	status.text = industry.get("status", "")
	match industry.get("status_kind", "ok"):
		"ok":
			status.add_theme_color_override("font_color", UiTheme.GOOD)
		"warn":
			status.add_theme_color_override("font_color", UiTheme.WARN_TEXT)
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
		fill_view(row["view"], industry)
	Kit.drop_unseen(_rows, seen)
	_empty.visible = _rows.is_empty()


func _add(id: int, industry: Dictionary) -> void:
	var row := Kit.column()
	var head := Kit.row()
	head.add_child(Kit.heading(title_of(industry)))
	head.add_child(Kit.spacer())
	head.add_child(go_button(industry["x"], industry["z"]))
	row.add_child(head)
	var view := make_view()
	row.add_child(view["box"])
	var separator := add_row(_box, row)
	_rows[id] = {"row": row, "view": view, "separator": separator}
