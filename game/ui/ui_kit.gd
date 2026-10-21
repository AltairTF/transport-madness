extends RefCounted
## Small pieces the windows of the HUD share (M6d): money as text, the date, the Condition bar, the block that edits a
## maintenance policy, and labels.

const UiTheme := preload("res://game/ui_theme.gd")

const MONTHS := ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
const REPAIR_NAMES := ["Patch", "Service", "Overhaul"]
const FREQUENCY_NAMES := ["Off", "Low", "Normal", "High"]
## Space between the items of a window, and between a window's edge and what is in it.
const GAP := 10


static func money(amount: Variant) -> String:
	var value := int(amount)
	var digits := str(absi(value))
	var out := ""
	for i in digits.length():
		if i > 0 and (digits.length() - i) % 3 == 0:
			out += ","
		out += digits[i]
	return ("-" if value < 0 else "") + out


## "2 Mar 1938   14:30": the calendar day and the time of day (a day is 20 minutes long, a month three days).
static func date_text(company: Dictionary) -> String:
	var hour: float = company["hour"]
	return "%d %s %d   %02d:%02d" % [company["day_of_month"], MONTHS[(company["month"] - 1) % 12], company["year"], int(hour), int(fmod(hour, 1.0) * 60.0)]


static func spacer() -> Control:
	var spacer := Control.new()
	spacer.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	return spacer


## A paragraph of dim, wrapping text.
static func note(text: String) -> Label:
	var label := Label.new()
	label.theme_type_variation = "Dim"
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.text = text
	return label


static func heading(text: String) -> Label:
	var label := Label.new()
	label.theme_type_variation = "Heading"
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.text = text
	return label


static func button(text: String, action: Callable, tooltip := "") -> Button:
	var b := Button.new()
	b.text = text
	b.tooltip_text = tooltip
	if action.is_valid():
		b.pressed.connect(action)
	return b


## A row container with the shared spacing.
static func row() -> HBoxContainer:
	var r := HBoxContainer.new()
	r.add_theme_constant_override("separation", GAP)
	return r


static func flow() -> HFlowContainer:
	var r := HFlowContainer.new()
	r.add_theme_constant_override("h_separation", GAP)
	r.add_theme_constant_override("v_separation", GAP / 2)
	return r


static func column() -> VBoxContainer:
	var c := VBoxContainer.new()
	c.add_theme_constant_override("separation", GAP / 2)
	return c


static func condition_color(value: float) -> Color:
	if value >= 60.0:
		return Color(0.25, 0.75, 0.35)
	if value >= 40.0:
		return Color(0.85, 0.75, 0.2)
	if value >= 20.0:
		return Color(0.9, 0.5, 0.15)
	return Color(0.85, 0.2, 0.15)


## A bar for a Condition from 0 to 100; returns the bar and the style of its fill (to colour by the value).
static func make_condition_bar() -> Dictionary:
	var bar := ProgressBar.new()
	bar.min_value = 0.0
	bar.max_value = 100.0
	bar.show_percentage = false
	bar.custom_minimum_size = Vector2(110, 16)
	bar.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	bar.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	var fill := StyleBoxFlat.new()
	fill.bg_color = condition_color(100.0)
	bar.add_theme_stylebox_override("fill", fill)
	return {"bar": bar, "fill": fill}


static func set_condition_bar(entry: Dictionary, value: float) -> void:
	entry["bar"].value = value
	entry["fill"].bg_color = condition_color(value)
	entry["bar"].tooltip_text = "Condition %d of 100. Below 60 a breakdown becomes possible, below 40 the vehicle is darker on the map, below 20 reddish and the brakes weaker." % roundi(value)


## A row of controls that edits a maintenance policy: whether it applies (or inherits the level above's), when a vehicle is due (days
## since the last repair, Condition below a floor, either or both, 0 meaning not by that) and which repair it gets. Scope is 0 for the
## company, 1 for a line, 2 for a vehicle or train. <on_set> gets (scope, target, mode, days, below, kind).
static func make_policy_block(scope: int, target: int, on_set: Callable) -> Dictionary:
	var box := column()
	var summary := note("")
	box.add_child(summary)
	var r := flow()
	box.add_child(r)
	var mode := OptionButton.new()
	if scope != 0:
		mode.add_item("Inherit", 0)
	mode.add_item("Off", 1)
	mode.add_item("On", 2)
	mode.tooltip_text = "Inherit: the level above decides. Off: no maintenance, the risk is yours. On: due by the triggers below."
	r.add_child(mode)
	var days := SpinBox.new()
	days.min_value = 0
	days.max_value = 3650
	days.step = 5
	days.prefix = "every "
	days.suffix = " days"
	days.tooltip_text = "Due this many days after the last repair. 0: not by days."
	r.add_child(days)
	var below := SpinBox.new()
	below.min_value = 0
	below.max_value = 100
	below.step = 5
	below.prefix = "below "
	below.tooltip_text = "Due when Condition is below this. 0: not by Condition."
	r.add_child(below)
	var kind := OptionButton.new()
	for i in REPAIR_NAMES.size():
		kind.add_item(REPAIR_NAMES[i], i)
	kind.tooltip_text = "Patch: quick and cheap, +15, leaves the vehicle patched (it wears faster). Service: +50. Overhaul: up to 95, clears patches. A depot of a higher level does the bigger repairs."
	r.add_child(kind)
	r.add_child(button("Set", func() -> void: on_set.call(scope, target, mode.get_selected_id(), int(days.value), int(below.value), kind.get_selected_id()), "Use this policy."))
	return {"box": box, "summary": summary, "mode": mode, "days": days, "below": below, "kind": kind, "last": {}}


## Shows a policy in its block, unless the player is typing in it.
static func show_policy(block: Dictionary, policy: Dictionary, text: String) -> void:
	block["summary"].text = text
	if block["last"] == policy:
		return
	if block["days"].get_line_edit().has_focus() or block["below"].get_line_edit().has_focus():
		return
	block["last"] = policy.duplicate()
	var index: int = block["mode"].get_item_index(int(policy["mode"]))
	if index >= 0:
		block["mode"].select(index)
	block["days"].value = policy["days"]
	block["below"].value = policy["below"]
	block["kind"].select(int(policy["kind"]))


## Removes the rows of a list that are no longer in <seen> (each entry holds "row" and, optionally, "separator").
static func drop_unseen(rows: Dictionary, seen: Dictionary) -> void:
	for id in rows.keys():
		if not seen.has(id):
			rows[id]["row"].queue_free()
			if rows[id].has("separator"):
				rows[id]["separator"].queue_free()
			rows.erase(id)
