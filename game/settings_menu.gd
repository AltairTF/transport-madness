extends PanelContainer
## The settings screen, shared by the main menu and the pause menu. Every change applies and is saved at once.
## Tabs that have nothing to set yet say so rather than being hidden.

signal closed

const Keys := preload("res://game/keys.gd")
## What the mouse and Esc do: these are not rebound.
const FIXED_CONTROLS := [
	["Orbit the camera", "Right mouse button + drag"],
	["Pan", "Middle mouse + drag, or the arrow keys"],
	["Zoom", "Mouse wheel"],
	["Place, pick, open what is clicked", "Left mouse button"],
	["Cancel the tool, close a window, the menu", "Esc"],
]
const MSAA_NAMES := ["Off", "2x", "4x", "8x"]

var _resolution: OptionButton
var _resolutions: Array[Vector2i] = []
var _key_buttons: Dictionary = {} ## action -> its Button in the Controls tab
var _listening := "" ## the action waiting for its new key
var _key_note: Label


func _ready() -> void:
	custom_minimum_size = Vector2(760, 0)
	var column := VBoxContainer.new()
	add_child(column)

	var title := Label.new()
	title.text = "Settings"
	title.theme_type_variation = "Title"
	column.add_child(title)

	var tabs := TabContainer.new()
	tabs.custom_minimum_size = Vector2(0, 360)
	column.add_child(tabs)
	tabs.add_child(_display_tab())
	tabs.add_child(_audio_tab())
	tabs.add_child(_controls_tab())
	tabs.add_child(_gameplay_tab())

	var back := Button.new()
	back.text = "Back"
	back.custom_minimum_size = Vector2(160, 0)
	back.size_flags_horizontal = Control.SIZE_SHRINK_END
	back.pressed.connect(func() -> void: closed.emit())
	column.add_child(back)


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

	var vsync := CheckBox.new()
	vsync.button_pressed = Settings.vsync
	vsync.text = "On"
	vsync.toggled.connect(func(on: bool) -> void:
		Settings.vsync = on
		_commit())
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
		render.add_item("%d %%" % roundi(step * 100.0))
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

	var ssao := CheckBox.new()
	ssao.button_pressed = Settings.ssao
	ssao.text = "On"
	ssao.tooltip_text = "Darkens creases and the foot of buildings. Costs frame time on a large window."
	ssao.toggled.connect(func(on: bool) -> void:
		Settings.ssao = on
		_commit())
	_row(grid, "Ambient occlusion", ssao)
	return page


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
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
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
		Settings.key_bindings[action] = {"key": key, "shift": event.shift_pressed}
		_key_note.text = "%s now on %s.%s" % [Keys.label(action), Keys.text(action, Settings.key_bindings),
			"  It was on %s too: bind that one again." % Keys.label(clash) if clash != "" else ""]
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
	return page


## A two-column grid of label and control rows, added to a page.
func _grid(page: VBoxContainer) -> GridContainer:
	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_top", 12)
	var grid := GridContainer.new()
	grid.columns = 2
	grid.add_theme_constant_override("h_separation", 24)
	grid.add_theme_constant_override("v_separation", 12)
	margin.add_child(grid)
	page.add_child(margin)
	return grid


func _row(grid: GridContainer, text: String, control: Control) -> void:
	var label := Label.new()
	label.text = text
	label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	grid.add_child(label)
	control.custom_minimum_size.x = maxf(control.custom_minimum_size.x, 280.0)
	grid.add_child(control)


func _slider(grid: GridContainer, text: String, value: float, setter: Callable) -> void:
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
