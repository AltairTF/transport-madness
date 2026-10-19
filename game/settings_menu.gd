extends PanelContainer
## The settings screen, shared by the main menu and the pause menu. Every change applies and is saved at once.
## Tabs that have nothing to set yet say so rather than being hidden.

signal closed

const CONTROLS := [
	["Orbit the camera", "Right mouse button + drag"],
	["Pan", "W A S D, arrow keys or middle mouse + drag"],
	["Zoom", "Mouse wheel"],
	["Place or pick", "Left mouse button"],
	["Finish a line", "Enter"],
	["Cancel the tool, open the menu", "Esc"],
	["Debug panel", "F3"],
]
const MSAA_NAMES := ["Off", "2x", "4x", "8x"]

var _resolution: OptionButton
var _resolutions: Array[Vector2i] = []


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


func _controls_tab() -> Control:
	var page := _page("Controls")
	var grid := _grid(page)
	for entry in CONTROLS:
		_row(grid, entry[0], _dim_label(entry[1]))
	var note := Label.new()
	note.theme_type_variation = "Dim"
	note.text = "Rebinding keys comes later."
	page.add_child(note)
	return page


func _gameplay_tab() -> Control:
	var page := _page("Gameplay")
	var grid := _grid(page)
	var autosave := CheckBox.new()
	autosave.text = "Autosave"
	autosave.disabled = true
	_row(grid, "Saving", autosave)
	var edge := CheckBox.new()
	edge.text = "Scroll at the screen edge"
	edge.disabled = true
	_row(grid, "Camera", edge)
	var note := Label.new()
	note.theme_type_variation = "Dim"
	note.text = "Placeholders: saving arrives with save and load, a later milestone."
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
