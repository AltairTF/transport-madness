extends RefCounted
## The look shared by every menu and the HUD: dark panels, a blue accent, text sized for a 1080p canvas
## (the window scales it).

const FONT_SIZE := 20
## The palette follows the panel redesign (docs/ui-inventory.md and the artifact canvas "Transport Madness panels").
const PANEL := Color("14181fF0")
const SURFACE := Color("1b212b")
const SURFACE_LOW := Color("171c24")
const BORDER := Color("2b3341")
const BORDER_HOVER := Color("3d495c")
const BUTTON := SURFACE
const BUTTON_HOVER := Color("252d3b")
const ACCENT := Color("2f6bdc")
const ACCENT_SOFT := Color("2f6bdc29")
const ACCENT_TEXT := Color("c4d9ff")
const FOCUS := Color("86b4ff")
const TEXT := Color("e8ecf3")
const TEXT_DIM := Color("a3aec2")
const GOOD := Color("5fd4b0")
const WARN := Color("f2b84b")
const WARN_TEXT := Color("f6cd80")
const BAD := Color("ef6b6b")
const BUTTON_HEIGHT := 40
const PANEL_MARGIN := 16


static func build() -> Theme:
	var theme := Theme.new()
	theme.default_font_size = FONT_SIZE

	# Panels and windows keep their content well inside the border (M6d: the developer asked for more room).
	theme.set_stylebox("panel", "PanelContainer", _box(PANEL, BORDER, 10, PANEL_MARGIN))
	theme.set_stylebox("panel", "TooltipPanel", _box(PANEL, BORDER, 6, 6))
	theme.set_stylebox("panel", "TabContainer", _box(Color(0, 0, 0, 0), BORDER, 12, 0))
	theme.set_stylebox("tab_selected", "TabContainer", _box(BUTTON_HOVER, ACCENT, 8, 4))
	theme.set_stylebox("tab_unselected", "TabContainer", _box(BUTTON, BORDER, 8, 4))
	theme.set_stylebox("tab_hovered", "TabContainer", _box(BUTTON_HOVER, BORDER, 8, 4))
	theme.set_stylebox("tab_disabled", "TabContainer", _box(BUTTON, BORDER, 8, 4))

	for kind in ["Button", "OptionButton", "CheckBox"]:
		theme.set_stylebox("normal", kind, _box(BUTTON, BORDER, 8, 10))
		theme.set_stylebox("hover", kind, _box(BUTTON_HOVER, BORDER_HOVER, 8, 10))
		theme.set_stylebox("pressed", kind, _box(ACCENT, ACCENT, 8, 10))
		theme.set_stylebox("hover_pressed", kind, _box(ACCENT.lightened(0.15), ACCENT, 8, 10))
		theme.set_stylebox("disabled", kind, _box(SURFACE_LOW, BORDER, 8, 10))
		theme.set_stylebox("focus", kind, _focus_box(8))
		theme.set_color("font_color", kind, TEXT)
		theme.set_color("font_hover_color", kind, Color.WHITE)
		theme.set_color("font_pressed_color", kind, Color.WHITE)
		theme.set_color("font_hover_pressed_color", kind, Color.WHITE)
		theme.set_color("font_disabled_color", kind, TEXT_DIM)
		theme.set_color("icon_normal_color", kind, TEXT)
		theme.set_color("icon_hover_color", kind, Color.WHITE)
		theme.set_color("icon_pressed_color", kind, Color.WHITE)
		theme.set_color("icon_hover_pressed_color", kind, Color.WHITE)
		theme.set_color("icon_disabled_color", kind, TEXT_DIM)
		theme.set_constant("icon_max_width", kind, 22)
		theme.set_constant("h_separation", kind, 8)
	theme.set_stylebox("normal", "CheckBox", _box(Color(0, 0, 0, 0), Color(0, 0, 0, 0), 8, 8))
	theme.set_stylebox("hover", "CheckBox", _box(BUTTON_HOVER, Color(0, 0, 0, 0), 8, 8))
	theme.set_stylebox("pressed", "CheckBox", _box(Color(0, 0, 0, 0), Color(0, 0, 0, 0), 8, 8))
	theme.set_stylebox("hover_pressed", "CheckBox", _box(BUTTON_HOVER, Color(0, 0, 0, 0), 8, 8))
	theme.set_stylebox("disabled", "CheckBox", _box(Color(0, 0, 0, 0), Color(0, 0, 0, 0), 8, 8))
	for kind in ["Button", "OptionButton"]:
		theme.set_constant("h_separation", kind, 8)

	# A Button that does the main thing of a panel: a soft blue fill, like the Order button of the Gateways panel.
	theme.set_type_variation("AccentButton", "Button")
	theme.set_stylebox("normal", "AccentButton", _box(ACCENT_SOFT, Color("3b78ea"), 8, 10))
	theme.set_stylebox("hover", "AccentButton", _box(Color("2f6bdc4d"), FOCUS, 8, 10))
	theme.set_stylebox("pressed", "AccentButton", _box(ACCENT, ACCENT, 8, 10))
	theme.set_color("font_color", "AccentButton", ACCENT_TEXT)
	theme.set_type_variation("PrimaryButton", "Button")
	theme.set_stylebox("normal", "PrimaryButton", _box(ACCENT, ACCENT, 8, 10))
	theme.set_stylebox("hover", "PrimaryButton", _box(Color("3b78ea"), Color("3b78ea"), 8, 10))
	theme.set_stylebox("pressed", "PrimaryButton", _box(ACCENT.darkened(0.15), ACCENT, 8, 10))
	theme.set_stylebox("disabled", "PrimaryButton", _box(SURFACE_LOW, BORDER, 8, 10))
	theme.set_color("font_color", "PrimaryButton", Color.WHITE)
	theme.set_type_variation("DangerButton", "Button")
	theme.set_stylebox("normal", "DangerButton", _box(Color(0, 0, 0, 0), Color("6b4440"), 8, 10))
	theme.set_stylebox("hover", "DangerButton", _box(Color("ff8f8524"), Color("ff8f85"), 8, 10))
	theme.set_stylebox("pressed", "DangerButton", _box(Color("ff8f8540"), Color("ff8f85"), 8, 10))
	theme.set_color("font_color", "DangerButton", Color("ff8f85"))
	theme.set_color("font_hover_color", "DangerButton", Color("ff8f85"))
	theme.set_color("font_pressed_color", "DangerButton", Color("ff8f85"))
	theme.set_type_variation("FlatButton", "Button")
	for state in ["normal", "hover", "pressed", "hover_pressed", "disabled"]:
		theme.set_stylebox(state, "FlatButton", _box(Color(0, 0, 0, 0) if state != "hover" else BUTTON_HOVER, Color(0, 0, 0, 0), 8, 8))
	# Fields: a dark well inside a card.
	for kind in ["LineEdit", "SpinBox"]:
		theme.set_stylebox("normal", kind, _box(PANEL.darkened(0.0), BORDER, 8, 10))
		theme.set_stylebox("focus", kind, _focus_box(8))
		theme.set_stylebox("read_only", kind, _box(SURFACE_LOW, BORDER, 8, 10))
	theme.set_color("font_color", "LineEdit", TEXT)
	theme.set_stylebox("panel", "PopupMenu", _box(SURFACE, BORDER_HOVER, 8, 6))
	theme.set_stylebox("hover", "PopupMenu", _box(BUTTON_HOVER, BUTTON_HOVER, 6, 6))
	theme.set_color("font_color", "PopupMenu", TEXT)
	# Bars are thin, rounded and flat.
	var well := _box(Color("222a37"), Color(0, 0, 0, 0), 4, 0)
	well.set_border_width_all(0)
	var fill := _box(Color("9cc2ff"), Color(0, 0, 0, 0), 4, 0)
	fill.set_border_width_all(0)
	theme.set_stylebox("background", "ProgressBar", well)
	theme.set_stylebox("fill", "ProgressBar", fill)
	var track := _box(Color("222a37"), Color(0, 0, 0, 0), 4, 0)
	track.set_border_width_all(0)
	track.content_margin_top = 4
	track.content_margin_bottom = 4
	var grabber := _box(ACCENT, Color(0, 0, 0, 0), 4, 0)
	grabber.set_border_width_all(0)
	grabber.content_margin_top = 4
	grabber.content_margin_bottom = 4
	theme.set_stylebox("slider", "HSlider", track)
	theme.set_stylebox("grabber_area", "HSlider", grabber)
	theme.set_stylebox("grabber_area_highlight", "HSlider", grabber)
	theme.set_type_variation("BigNumber", "Label")
	theme.set_font_size("font_size", "BigNumber", 32)
	theme.set_type_variation("Stat", "Label")
	theme.set_font_size("font_size", "Stat", 26)
	theme.set_type_variation("Small", "Label")
	theme.set_font_size("font_size", "Small", 16)
	theme.set_color("font_color", "Small", TEXT_DIM)
	# A card: one object of a list (a line, a station, an offer) on a raised surface.
	theme.set_type_variation("Card", "PanelContainer")
	theme.set_stylebox("panel", "Card", _box(SURFACE, BORDER, 8, 12))
	theme.set_type_variation("CardMuted", "PanelContainer")
	theme.set_stylebox("panel", "CardMuted", _dashed_box(SURFACE_LOW, Color("3a4558"), 8, 12))
	# A notice: a warning strip.
	theme.set_type_variation("Notice", "PanelContainer")
	theme.set_stylebox("panel", "Notice", _box(Color("f2b84b1f"), Color("f2b84b59"), 8, 12))

	theme.set_color("font_color", "Label", TEXT)
	var line := StyleBoxLine.new()
	line.color = BORDER
	line.thickness = 1
	theme.set_stylebox("separator", "HSeparator", line)
	theme.set_constant("separation", "VBoxContainer", 10)
	theme.set_constant("separation", "HBoxContainer", 10)
	theme.set_constant("h_separation", "HFlowContainer", 10)
	theme.set_constant("v_separation", "HFlowContainer", 8)
	theme.set_constant("separation", "HSeparator", 18)

	theme.set_type_variation("Title", "Label")
	theme.set_font_size("font_size", "Title", 34)
	theme.set_type_variation("Heading", "Label")
	theme.set_font_size("font_size", "Heading", 24)
	theme.set_type_variation("Dim", "Label")
	theme.set_color("font_color", "Dim", TEXT_DIM)
	# The small spaced caps over a block of a panel ("AVAILABLE IMPORTS").
	theme.set_type_variation("Section", "Label")
	theme.set_font_size("font_size", "Section", 16)
	theme.set_color("font_color", "Section", TEXT_DIM)
	theme.set_type_variation("Warn", "Label")
	theme.set_color("font_color", "Warn", WARN_TEXT)
	return theme


## A rounded plate for a Label or a panel that is not part of the theme (the message line, the raid banner).
static func plate(fill: Color, border: Color, margin := 12) -> StyleBoxFlat:
	return _box(fill, border, 8, margin)


static func _focus_box(radius: int) -> StyleBoxFlat:
	var box := StyleBoxFlat.new()
	box.bg_color = Color(0, 0, 0, 0)
	box.border_color = FOCUS
	box.set_border_width_all(2)
	box.set_corner_radius_all(radius)
	return box


static func _dashed_box(fill: Color, border: Color, radius: int, margin: int) -> StyleBoxFlat:
	# StyleBoxFlat has no dash; a darker plain border says "not available" well enough.
	return _box(fill, border, radius, margin)


static func _box(fill: Color, border: Color, radius: int, margin: int) -> StyleBoxFlat:
	var box := StyleBoxFlat.new()
	box.bg_color = fill
	box.border_color = border
	box.set_border_width_all(1)
	box.set_corner_radius_all(radius)
	box.set_content_margin_all(margin)
	return box
