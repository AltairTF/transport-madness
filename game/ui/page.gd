extends VBoxContainer
## The content of one window of the HUD (M6d). A page is built once and kept: its show_* calls update it every refresh, open or
## not, so a window opens with its numbers already there. The HUD's signals are the page's way out to main.gd.

const Kit := preload("res://game/ui/ui_kit.gd")
const UiTheme := preload("res://game/ui_theme.gd")

var hud ## the HUD (hud.gd)


func setup(owner_hud) -> void:
	hud = owner_hud
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	add_theme_constant_override("separation", Kit.GAP)
	_build()


## Builds the page; each window overrides it.
func _build() -> void:
	pass


## A list row: added with a separator under it.
func add_row(box: Control, row: Control) -> HSeparator:
	var separator := HSeparator.new()
	box.add_child(row)
	box.add_child(separator)
	return separator


func go_button(x: float, z: float) -> Button:
	return Kit.button("Go to", func() -> void: hud.town_focus.emit(x, z))
