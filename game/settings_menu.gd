extends PanelContainer
## The settings screen, shared by the main menu and the pause menu. Every change applies and is saved at once.
## Tabs that have nothing to set yet say so rather than being hidden.

signal closed

const Keys := preload("res://game/keys.gd")
const UiTheme := preload("res://game/ui_theme.gd")
const Kit := preload("res://game/ui/ui_kit.gd")
## What the mouse and Esc do: these are not rebound.
const FIXED_CONTROLS := [
	["Orbit the camera", "Right mouse button + drag"],
	["Pan", "Middle mouse + drag, or the arrow keys"],
	["Zoom", "Mouse wheel"],
	["Place, pick, open what is clicked", "Left mouse button"],
	["Cancel the tool, close a window, the menu", "Esc"],
]
const HINTS := {
	"Window mode": "Windowed or fullscreen.",
	"Window size": "Only sizes that fit this screen are listed.",
	"Interface size": "On top of the display's own scale.",
	"Vertical sync": "Stops tearing; may lower the frame rate.",
	"Anti-aliasing": "Smooths the edges of shapes.",
	"3D resolution": "How big the 3D picture is drawn; the interface stays sharp.",
	"Shadows": "Of buildings, vehicles and hills.",
	"Ambient occlusion": "Darkens creases and the foot of buildings; costs frame time.",
}
const MSAA_NAMES := ["Off", "2x", "4x", "8x"]

var _resolution: OptionButton
var _resolutions: Array[Vector2i] = []
var _key_buttons: Dictionary = {} ## action -> its Button in the Controls tab
var _listening := "" ## the action waiting for its new key
var _key_note: Label
var tabs_buttons: Array = []


func _ready() -> void:
	Kit.fit_width(self, 980)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	add_child(column)
	column.add_child(Kit.header("Settings", func() -> void: closed.emit()))

	var pages: Array[Control] = [_display_tab(), _audio_tab(), _controls_tab(), _gameplay_tab()]
	var names: Array = []
	for page in pages:
		names.append(page.name)
	var tabs := Kit.segmented(names, func(index: int) -> void: _open_tab(pages, tabs_buttons, index))
	tabs_buttons = tabs["buttons"]
	column.add_child(tabs["box"])
	var card := Kit.card()
	card.custom_minimum_size = Vector2(0, 500)
	card.size_flags_vertical = Control.SIZE_EXPAND_FILL
	for page in pages:
		card.get_meta("body").add_child(page)
	column.add_child(card)
	_open_tab(pages, tabs_buttons, 0)
	column.add_child(HSeparator.new())
	column.add_child(Kit.note("Changes apply and are saved as you make them."))


func _open_tab(pages: Array[Control], buttons: Array, index: int) -> void:
	Kit.select_segment(buttons, index)
	for i in pages.size():
		pages[i].visible = i == index


func _display_tab() -> Control:
	var page := _page("Display")
	var grid := _grid(page)

	var mode := OptionButton.new()
	mode.add_item("Windowed")
	mode.add_item("Fullscreen")
	mode.selected = 1 if Settings.fullscreen else 0
	mode.item_selected.connect(func(index: int) -> void:
		Settings.fullscreen = index == 1
		_resolution.disabled = Settings.fullscreen
		_commit())
	_row(grid, "Window mode", mode)

	_resolution = OptionButton.new()
	_resolutions = Settings.available_resolutions()
	for dimension in _resolutions:
		_resolution.add_item("%d x %d" % [dimension.x, dimension.y])
	_resolution.selected = _resolutions.find(Settings.resolution)
	_resolution.disabled = Settings.fullscreen
	_resolution.item_selected.connect(func(index: int) -> void:
		Settings.resolution = _resolutions[index]
		_commit())
	_row(grid, "Window size", _resolution)

	var interface_size := OptionButton.new()
	for factor in Settings.UI_SCALES:
		interface_size.add_item("%d%%" % roundi(factor * 100.0))
	interface_size.selected = maxi(0, Settings.UI_SCALES.find(Settings.ui_scale))
	interface_size.tooltip_text = "100 % is the interface of a 1920 x 1080 screen; a bigger window shows more of the map, not bigger buttons."
	interface_size.item_selected.connect(func(index: int) -> void:
		Settings.ui_scale = Settings.UI_SCALES[index]
		_commit())
	_row(grid, "Interface size", interface_size)

	var vsync := _on_off(Settings.vsync, func(on: bool) -> void: Settings.vsync = on)
	_row(grid, "Vertical sync", vsync)

	var msaa := OptionButton.new()
	for label in MSAA_NAMES:
		msaa.add_item(label)
	msaa.selected = Settings.msaa
	msaa.item_selected.connect(func(index: int) -> void:
		Settings.msaa = index
		_commit())
	_row(grid, "Anti-aliasing", msaa)

	var render := OptionButton.new()
	render.add_item("Automatic")
	for step in Settings.RENDER_SCALES:
		render.add_item("%d%%" % roundi(step * 100.0))
	render.selected = 0 if Settings.render_scale <= 0.0 else 1 + maxi(0, Settings.RENDER_SCALES.find(Settings.render_scale))
	render.tooltip_text = "How big the 3D picture is drawn, against the window; the interface stays sharp. Automatic keeps it near 8 million pixels, which a very large window needs to hold 60 frames a second."
	render.item_selected.connect(func(index: int) -> void:
		Settings.render_scale = 0.0 if index == 0 else Settings.RENDER_SCALES[index - 1]
		_commit())
	_row(grid, "3D resolution", render)

	var shadows := OptionButton.new()
	for label in ["Off", "Low", "High"]:
		shadows.add_item(label)
	shadows.selected = Settings.shadows
	shadows.tooltip_text = "Shadows of buildings, vehicles and hills. Low reaches 450 m from the camera, High 900 m with sharper edges; off is the fastest."
	shadows.item_selected.connect(func(index: int) -> void:
		Settings.shadows = index
		_commit())
	_row(grid, "Shadows", shadows)

	var ssao := _on_off(Settings.ssao, func(on: bool) -> void: Settings.ssao = on)
	_row(grid, "Ambient occlusion", ssao)
	return page


## An Off / On dropdown that sets a setting and saves.
func _on_off(value: bool, setter: Callable) -> OptionButton:
	var option := OptionButton.new()
	option.add_item("Off")
	option.add_item("On")
	option.selected = 1 if value else 0
	option.item_selected.connect(func(index: int) -> void:
		setter.call(index == 1)
		_commit())
	return option


func _audio_tab() -> Control:
	var page := _page("Audio")
	var grid := _grid(page)
	_slider(grid, "Master volume", Settings.master_volume, func(value: float) -> void: Settings.master_volume = value)
	_slider(grid, "Music", Settings.music_volume, func(value: float) -> void: Settings.music_volume = value)
	_slider(grid, "Effects", Settings.effects_volume, func(value: float) -> void: Settings.effects_volume = value)
	var note := Label.new()
	note.theme_type_variation = "Dim"
	note.text = "There is no sound in the game yet; these are kept for when there is."
	page.add_child(note)
	return page


## Every key of the game can be bound again: click its button, then press the new key (with Shift if wanted; Esc leaves it as it was).
func _controls_tab() -> Control:
	var page := _page("Controls")
	var scroll := ScrollContainer.new()
	scroll.custom_minimum_size = Vector2(0, 460)
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	page.add_child(scroll)
	var inner := VBoxContainer.new()
	inner.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(inner)
	var grid := _grid(inner)
	for entry in Keys.ACTIONS:
		var action: String = entry[0]
		var button := Button.new()
		button.pressed.connect(func() -> void: _listen(action))
		_key_buttons[action] = button
		_row(grid, entry[1], button)
	for entry in FIXED_CONTROLS:
		_row(grid, entry[0], _dim_label(entry[1]))
	_key_note = _dim_label("Click a key to change it, then press the new one. Esc keeps the old key.")
	_key_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	inner.add_child(_key_note)
	var reset := Button.new()
	reset.text = "All keys back to their defaults"
	reset.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	reset.pressed.connect(func() -> void:
		Settings.key_bindings = {}
		_keys_changed())
	inner.add_child(reset)
	_show_keys()
	return page


func _show_keys() -> void:
	for action in _key_buttons:
		_key_buttons[action].text = "Press a key..." if action == _listening else Keys.text(action, Settings.key_bindings)


func _listen(action: String) -> void:
	_listening = action
	_show_keys()


func _input(event: InputEvent) -> void:
	if _listening == "" or not (event is InputEventKey and event.pressed and not event.echo):
		return
	get_viewport().set_input_as_handled()
	var key: int = event.keycode
	if key == KEY_SHIFT:
		return # wait for the key that goes with it
	var action := _listening
	_listening = ""
	if key != KEY_ESCAPE:
		var clash := Keys.action_on(key, event.shift_pressed, Settings.key_bindings, action)
		var old := Keys.binding(action, Settings.key_bindings)
		Settings.key_bindings[action] = {"key": key, "shift": event.shift_pressed}
		# The clashing action takes the old key (a swap), so no two actions share a key.
		if clash != "":
			Settings.key_bindings[clash] = {"key": old["key"], "shift": old["shift"]}
		_key_note.text = "%s now on %s.%s" % [Keys.label(action), Keys.text(action, Settings.key_bindings),
			"  %s moved to %s." % [Keys.label(clash), Keys.text(clash, Settings.key_bindings)] if clash != "" else ""]
	_keys_changed()


func _keys_changed() -> void:
	Keys.apply(Settings.key_bindings)
	Settings.save()
	_show_keys()


func _gameplay_tab() -> Control:
	var page := _page("Gameplay")
	var grid := _grid(page)
	var autosave := CheckBox.new()
	autosave.text = "Autosave"
	autosave.button_pressed = true
	autosave.disabled = true
	autosave.tooltip_text = "Always on: one of three autosaves is written at the start of each month."
	_row(grid, "Saving", autosave)
	var edge := CheckBox.new()
	edge.text = "Scroll at the screen edge"
	edge.disabled = true
	_row(grid, "Camera", edge)
	var note := Label.new()
	note.theme_type_variation = "Dim"
	note.text = "Scrolling at the screen edge comes later."
	page.add_child(note)
	return page


## A tab page; the tab shows its name.
func _page(tab_name: String) -> VBoxContainer:
	var page := VBoxContainer.new()
	page.name = tab_name
	page.add_theme_constant_override("separation", 12)
	return page


## What a page adds its rows to (the page itself: rows are Kit.setting_row lines).
func _grid(page: VBoxContainer) -> VBoxContainer:
	return page


func _row(grid: VBoxContainer, text: String, control: Control) -> void:
	Kit.setting_row(grid, text, HINTS.get(text, ""), control)


func _slider(grid: VBoxContainer, text: String, value: float, setter: Callable) -> void:
	var slider := HSlider.new()
	slider.max_value = 1.0
	slider.step = 0.05
	slider.value = value
	slider.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	slider.value_changed.connect(func(v: float) -> void:
		setter.call(v)
		_commit())
	_row(grid, text, slider)


func _dim_label(text: String) -> Label:
	var label := Label.new()
	label.text = text
	label.theme_type_variation = "Dim"
	return label


func _commit() -> void:
	Settings.apply()
	Settings.save()
