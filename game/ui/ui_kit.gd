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


## A paragraph like note() but with BBCode, for a bold, brighter lead on a dim body ("[b]Patch:[/b] +15 ...").
static func rich(text := "") -> RichTextLabel:
	var label := RichTextLabel.new()
	label.bbcode_enabled = true
	label.fit_content = true
	label.scroll_active = false
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.text = text
	return label


## Wraps a lead in the BBCode that the game's rich paragraphs use for emphasis (bold and full-brightness text).
static func lead(text: String) -> String:
	return "[color=#%s][b]%s[/b][/color]" % [UiTheme.TEXT.to_html(false), text]


static func heading(text: String) -> Label:
	var label := Label.new()
	label.theme_type_variation = "Heading"
	# A title is one line: autowrap in a row next to a spacer would collapse its width and break the name letter by letter.
	label.autowrap_mode = TextServer.AUTOWRAP_OFF
	label.clip_text = true
	label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	label.text = text
	return label


## The small spaced caps over a block, with an optional hint at the right ("AVAILABLE IMPORTS    Price is per order").
static func section(text: String, hint := "") -> Control:
	var r := row()
	var label := Label.new()
	label.theme_type_variation = "Section"
	label.text = text.to_upper()
	r.add_child(label)
	r.add_child(spacer())
	if not hint.is_empty():
		var dim := note(hint)
		dim.autowrap_mode = TextServer.AUTOWRAP_OFF
		r.add_child(dim)
	return r


## A window's top line: the title and a close button at the right.
static func header(text: String, on_close: Callable) -> Control:
	var r := row()
	r.add_child(heading(text))
	var close := button("×", on_close, "Close")
	close.custom_minimum_size = Vector2(44, 44)
	r.add_child(close)
	return r


## A row of toggle buttons in one rounded well; <pick> gets the index of the pressed one. Returns {box, buttons}.
static func segmented(labels: Array, pick: Callable) -> Dictionary:
	var box := PanelContainer.new()
	box.theme_type_variation = "Segmented"
	var line := HBoxContainer.new()
	line.add_theme_constant_override("separation", 4)
	box.add_child(line)
	var buttons: Array[Button] = []
	for i in labels.size():
		var b := Button.new()
		b.text = labels[i]
		b.theme_type_variation = "SegmentButton"
		b.toggle_mode = true
		b.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		b.pressed.connect(pick.bind(i))
		line.add_child(b)
		buttons.append(b)
	return {"box": box, "buttons": buttons}


## Presses one button of a segmented() and releases the others, without a signal.
static func select_segment(buttons: Array, index: int) -> void:
	for i in buttons.size():
		buttons[i].set_pressed_no_signal(i == index)


## A line of a settings card: a title with a dim sentence under it, the control at the right.
static func setting_row(parent: Control, title: String, hint: String, control: Control) -> void:
	if parent.get_child_count() > 0:
		parent.add_child(HSeparator.new())
	var r := row()
	var text := VBoxContainer.new()
	text.add_theme_constant_override("separation", 2)
	text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	text.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	var caption := Label.new()
	caption.text = title
	text.add_child(caption)
	if hint != "":
		var dim := note(hint)
		dim.add_theme_font_size_override("font_size", 16)
		text.add_child(dim)
	r.add_child(text)
	control.custom_minimum_size.x = maxf(control.custom_minimum_size.x, 300.0)
	control.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	r.add_child(control)
	parent.add_child(r)


## A small rounded tag next to a title ("Road").
static func badge(text: String) -> Control:
	var panel := PanelContainer.new()
	var box := StyleBoxFlat.new()
	box.bg_color = Color("222a37")
	box.set_corner_radius_all(11)
	box.content_margin_left = 8
	box.content_margin_right = 8
	box.content_margin_top = 1
	box.content_margin_bottom = 1
	panel.add_theme_stylebox_override("panel", box)
	panel.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	var label := Label.new()
	label.theme_type_variation = "Dim"
	label.text = text
	label.add_theme_font_size_override("font_size", 16)
	panel.add_child(label)
	return panel


## A warning strip: a bold line and a sentence under it.
static func notice(title: String, text: String) -> Control:
	var panel := PanelContainer.new()
	panel.theme_type_variation = "Notice"
	var box := column()
	var head := Label.new()
	head.theme_type_variation = "Warn"
	head.text = title
	box.add_child(head)
	box.add_child(note(text))
	panel.add_child(box)
	return panel


## A block of help that opens and closes under its title.
static func fold(title: String, content: Control) -> Control:
	var panel := PanelContainer.new()
	panel.theme_type_variation = "Card"
	var box := column()
	var head := Button.new()
	head.text = "▸  " + title
	head.flat = true
	head.alignment = HORIZONTAL_ALIGNMENT_LEFT
	content.visible = false
	head.pressed.connect(func() -> void:
		content.visible = not content.visible
		head.text = ("▾  " if content.visible else "▸  ") + title)
	box.add_child(head)
	box.add_child(content)
	panel.add_child(box)
	return panel


## A card holding a column; returns the card (add it to a container) with the column in meta "body".
static func card(muted := false) -> PanelContainer:
	var panel := PanelContainer.new()
	panel.theme_type_variation = "CardMuted" if muted else "Card"
	var body := column()
	panel.add_child(body)
	panel.set_meta("body", body)
	return panel


## A figure with its caption over it: {"box", "value"}; set value.text. <size> is "BigNumber" or "Stat".
static func stat(caption: String, size := "Stat") -> Dictionary:
	var panel := PanelContainer.new()
	panel.theme_type_variation = "Card"
	panel.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var body := VBoxContainer.new()
	body.add_theme_constant_override("separation", 0)
	panel.add_child(body)
	var cap := Label.new()
	cap.theme_type_variation = "Small"
	cap.text = caption
	cap.clip_text = true
	body.add_child(cap)
	var value := Label.new()
	value.theme_type_variation = size
	body.add_child(value)
	return {"box": panel, "value": value, "caption": cap}


## A bar under a caption and a figure at the right: {"box", "value", "bar"}; colour is the fill.
static func meter(caption: String, color: Color) -> Dictionary:
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 6)
	var head := row()
	var cap := Label.new()
	cap.text = caption
	head.add_child(cap)
	head.add_child(spacer())
	var value := Label.new()
	head.add_child(value)
	box.add_child(head)
	var bar := ProgressBar.new()
	bar.min_value = 0.0
	bar.max_value = 100.0
	bar.show_percentage = false
	bar.custom_minimum_size = Vector2(0, 8)
	bar.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	var fill := StyleBoxFlat.new()
	fill.bg_color = color
	fill.set_corner_radius_all(4)
	bar.add_theme_stylebox_override("fill", fill)
	box.add_child(bar)
	return {"box": box, "value": value, "bar": bar, "caption": cap, "fill": fill}


## A small rounded tag in a flow (a cargo accepted).
static func chip(text: String) -> Control:
	return badge(text)


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
	var grid := GridContainer.new()
	grid.columns = 2
	grid.add_theme_constant_override("h_separation", GAP)
	grid.add_theme_constant_override("v_separation", GAP / 2)
	box.add_child(grid)
	var mode := OptionButton.new()
	if scope != 0:
		mode.add_item("Inherit", 0)
	mode.add_item("Off", 1)
	mode.add_item("On", 2)
	mode.tooltip_text = "Inherit: the level above decides. Off: no maintenance, the risk is yours. On: due by the triggers below."
	var kind := OptionButton.new()
	for i in REPAIR_NAMES.size():
		kind.add_item(REPAIR_NAMES[i], i)
	kind.tooltip_text = "Patch: quick and cheap, +15, leaves the vehicle patched (it wears faster). Service: +50. Overhaul: up to 95, clears patches. A depot of a higher level does the bigger repairs."
	var days := SpinBox.new()
	days.min_value = 0
	days.max_value = 3650
	days.step = 5
	days.tooltip_text = "Due this many days after the last repair. 0: not by days."
	var below := SpinBox.new()
	below.min_value = 0
	below.max_value = 100
	below.step = 5
	below.tooltip_text = "Due when Condition is below this. 0: not by Condition."
	for entry in [["Policy", mode], ["Level", kind], ["Every (days)", days], ["Or when condition below", below]]:
		var field := column()
		field.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		var caption := Label.new()
		caption.theme_type_variation = "Small"
		caption.text = entry[0]
		field.add_child(caption)
		entry[1].size_flags_horizontal = Control.SIZE_EXPAND_FILL
		field.add_child(entry[1])
		grid.add_child(field)
	box.add_child(button("Set maintenance", func() -> void: on_set.call(scope, target, mode.get_selected_id(), int(days.value), int(below.value), kind.get_selected_id()), "Use this policy."))
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
