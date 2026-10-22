extends RefCounted
## The look shared by every menu and the HUD: dark panels, a blue accent, text sized for a 1080p canvas
## (the window scales it).

const FONT_SIZE := 20
const PANEL := Color(0.09, 0.10, 0.12, 0.94)
const BORDER := Color(0.28, 0.31, 0.36)
const BUTTON := Color(0.17, 0.19, 0.23)
const BUTTON_HOVER := Color(0.23, 0.26, 0.31)
const ACCENT := Color(0.20, 0.46, 0.76)
const TEXT := Color(0.90, 0.92, 0.95)
const TEXT_DIM := Color(0.55, 0.58, 0.63)
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
		theme.set_stylebox("normal", kind, _box(BUTTON, BORDER, 6, 8))
		theme.set_stylebox("hover", kind, _box(BUTTON_HOVER, ACCENT, 6, 8))
		theme.set_stylebox("pressed", kind, _box(ACCENT, ACCENT, 6, 8))
		theme.set_stylebox("hover_pressed", kind, _box(ACCENT.lightened(0.15), ACCENT, 6, 8))
		theme.set_stylebox("disabled", kind, _box(BUTTON.darkened(0.3), BORDER.darkened(0.4), 6, 8))
		theme.set_stylebox("focus", kind, _box(Color(0, 0, 0, 0), ACCENT.lightened(0.3), 6, 8))
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
	theme.set_stylebox("normal", "CheckBox", _box(Color(0, 0, 0, 0), Color(0, 0, 0, 0), 6, 8))
	theme.set_stylebox("hover", "CheckBox", _box(BUTTON_HOVER, Color(0, 0, 0, 0), 6, 8))
	theme.set_stylebox("pressed", "CheckBox", _box(Color(0, 0, 0, 0), Color(0, 0, 0, 0), 6, 8))
	theme.set_stylebox("hover_pressed", "CheckBox", _box(BUTTON_HOVER, Color(0, 0, 0, 0), 6, 8))
	theme.set_stylebox("disabled", "CheckBox", _box(Color(0, 0, 0, 0), Color(0, 0, 0, 0), 6, 8))

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
	return theme


static func _box(fill: Color, border: Color, radius: int, margin: int) -> StyleBoxFlat:
	var box := StyleBoxFlat.new()
	box.bg_color = fill
	box.border_color = border
	box.set_border_width_all(1)
	box.set_corner_radius_all(radius)
	box.set_content_margin_all(margin)
	return box
