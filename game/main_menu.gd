extends Control
## The first screen: continue the newest save, new game, load a save, settings, quit.
## A run with command line arguments (the self-tests, the benchmark) skips it and goes straight to the game.

const GAME := "res://game/main.tscn"
const UiTheme := preload("res://game/ui_theme.gd")
const SettingsMenu := preload("res://game/settings_menu.gd")
const SaveMenu := preload("res://game/save_menu.gd")
const CreditsMenu := preload("res://game/credits_menu.gd")

var _main_page: Control
var _new_game_page: Control
var _settings_page: Control
var _load_page: Control
var _credits_page: Control
var _notice: Label
var _map_buttons: Dictionary = {}
var _map_text: Label
var _seed: SpinBox
var _towns_row: Control
var _towns_value: Label
var _towns_slider: HSlider
var _industries_row: Control
var _industries_buttons: Array[Button] = []
var _frequency_buttons: Array[Button] = []
var _land_box: Control
var _preview: TextureRect
var _preview_scale: Label
var _preview_timer: Timer
var _size_buttons: Array[Button] = []
var _land_sliders: Dictionary = {}
var _preview_maker: RefCounted


func _ready() -> void:
	if not OS.get_cmdline_user_args().is_empty():
		get_tree().change_scene_to_file.call_deferred(GAME)
		return
	theme = UiTheme.build()
	Session.map = "towns" # what a new game offers first; the self-tests keep Session's own default

	var backdrop := ColorRect.new()
	backdrop.color = UiTheme.PANEL.darkened(0.1)
	backdrop.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(backdrop)

	var center := CenterContainer.new()
	center.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(center)
	var stack := VBoxContainer.new()
	stack.add_theme_constant_override("separation", 28)
	center.add_child(stack)

	var title := Label.new()
	title.text = "Transport Madness"
	title.theme_type_variation = "Title"
	title.add_theme_font_size_override("font_size", 72)
	title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	stack.add_child(title)
	var tagline := Label.new()
	tagline.text = "A region at war, 1938 to 1945"
	tagline.theme_type_variation = "Dim"
	tagline.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	stack.add_child(tagline)

	_main_page = _build_main_page()
	_new_game_page = _build_new_game_page()
	_settings_page = SettingsMenu.new()
	_settings_page.closed.connect(func() -> void: _show(_main_page))
	_load_page = SaveMenu.new()
	_load_page.closed.connect(func() -> void: _show(_main_page))
	_load_page.load_requested.connect(_load)
	_credits_page = CreditsMenu.new()
	_credits_page.closed.connect(func() -> void: _show(_main_page))
	for page in [_main_page, _new_game_page, _settings_page, _load_page, _credits_page]:
		stack.add_child(page)
	_notice = Label.new()
	_notice.add_theme_color_override("font_color", UiTheme.BAD)
	_notice.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_notice.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_notice.custom_minimum_size = Vector2(620, 0)
	_notice.text = Session.menu_message # why the last load failed, if it did
	_notice.visible = Session.menu_message != ""
	Session.menu_message = ""
	stack.add_child(_notice)

	var footer := Label.new()
	footer.text = "Whitebox build"
	footer.theme_type_variation = "Dim"
	footer.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_LEFT, Control.PRESET_MODE_MINSIZE, 16)
	add_child(footer)
	_show(_main_page)


## Esc goes back to the first page.
func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_ESCAPE and not _main_page.visible:
		get_viewport().set_input_as_handled()
		_show(_main_page)


func _build_main_page() -> Control:
	var column := VBoxContainer.new()
	column.custom_minimum_size = Vector2(380, 0)
	column.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	var slots: RefCounted = load("res://game/SaveSlots.cs").new()
	var latest: String = slots.Latest()
	column.add_theme_constant_override("separation", 12)
	var continue_button := _button(column, "Continue", func() -> void: _load(latest))
	continue_button.theme_type_variation = "PrimaryButton"
	continue_button.disabled = latest == ""
	continue_button.tooltip_text = "Load the newest save." if latest != "" else "No save yet."
	_button(column, "New game", func() -> void: _show(_new_game_page))
	var load_button := _button(column, "Load game", func() -> void:
		_load_page.show_load()
		_show(_load_page))
	load_button.disabled = slots.List().is_empty()
	_button(column, "Settings", func() -> void: _show(_settings_page))
	_button(column, "Credits", func() -> void: _show(_credits_page))
	column.add_child(HSeparator.new())
	_button(column, "Quit", get_tree().quit)
	return column


func _build_new_game_page() -> Control:
	var panel := PanelContainer.new()
	panel.custom_minimum_size = Vector2(620, 0)
	panel.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	var column := VBoxContainer.new()
	panel.add_child(column)

	var heading := Label.new()
	heading.text = "New game"
	heading.theme_type_variation = "Heading"
	column.add_child(heading)

	var maps := HBoxContainer.new()
	column.add_child(maps)
	for map in Session.MAPS:
		var button := Button.new()
		button.text = map["name"]
		button.toggle_mode = true
		button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		button.pressed.connect(_select_map.bind(map["id"]))
		maps.add_child(button)
		_map_buttons[map["id"]] = button
	_map_text = Label.new()
	_map_text.theme_type_variation = "Dim"
	_map_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(_map_text)

	var seed_row := HBoxContainer.new()
	column.add_child(seed_row)
	var seed_label := Label.new()
	seed_label.text = "Map seed"
	seed_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	seed_row.add_child(seed_label)
	_seed = SpinBox.new()
	_seed.min_value = 1
	_seed.max_value = 999999
	_seed.value = Session.world_seed
	_seed.custom_minimum_size = Vector2(160, 0)
	seed_row.add_child(_seed)
	var random := Button.new()
	random.text = "Random"
	random.pressed.connect(func() -> void: _seed.value = randi_range(1, 999999))
	seed_row.add_child(random)
	_seed.value_changed.connect(func(_v: float) -> void: _queue_preview())

	column.add_child(_build_land_box())

	_towns_row = HBoxContainer.new()
	column.add_child(_towns_row)
	var towns_label := Label.new()
	towns_label.text = "Towns"
	towns_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_towns_row.add_child(towns_label)
	_towns_slider = HSlider.new()
	_towns_slider.min_value = 2
	_towns_slider.max_value = 10
	_towns_slider.step = 1
	_towns_slider.value = Session.towns
	_towns_slider.custom_minimum_size = Vector2(240, 0)
	_towns_slider.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	_towns_slider.value_changed.connect(_on_towns_changed)
	_towns_row.add_child(_towns_slider)
	_towns_value = Label.new()
	_towns_value.custom_minimum_size = Vector2(40, 0)
	_towns_value.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	_towns_row.add_child(_towns_value)
	_on_towns_changed(Session.towns)
	_on_size_changed(Session.size)

	_industries_row = HBoxContainer.new()
	column.add_child(_industries_row)
	var industries_label := Label.new()
	industries_label.text = "Industries"
	industries_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_industries_row.add_child(industries_label)
	for i in 3:
		var button := Button.new()
		button.text = ["Few", "Normal", "Many"][i]
		button.toggle_mode = true
		button.custom_minimum_size = Vector2(90, 0)
		button.pressed.connect(_on_industries_changed.bind(i))
		_industries_row.add_child(button)
		_industries_buttons.append(button)
	_on_industries_changed(Session.industries)

	var frequency_row := HBoxContainer.new()
	column.add_child(frequency_row)
	var frequency_label := Label.new()
	frequency_label.text = "Random events"
	frequency_label.tooltip_text = "How often breakdowns and signal failures happen. It can be changed in the Maintenance window during the game."
	frequency_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	frequency_row.add_child(frequency_label)
	for i in 4:
		var button := Button.new()
		button.text = ["Off", "Low", "Normal", "High"][i]
		button.toggle_mode = true
		button.custom_minimum_size = Vector2(90, 0)
		button.pressed.connect(_on_frequency_changed.bind(i))
		frequency_row.add_child(button)
		_frequency_buttons.append(button)
	_on_frequency_changed(Session.frequency)
	var frequency_note := Label.new()
	frequency_note.theme_type_variation = "Dim"
	frequency_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	frequency_note.text = "Accidents that come out of the driving (overtaking gone wrong, crashes at junctions) are not scaled, so Off does not remove all accidents."
	column.add_child(frequency_note)

	var buttons := HBoxContainer.new()
	column.add_child(buttons)
	var back := _button(buttons, "Back", func() -> void: _show(_main_page))
	back.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var start := _button(buttons, "Start", _start)
	start.theme_type_variation = "PrimaryButton"
	start.size_flags_horizontal = Control.SIZE_EXPAND_FILL

	_select_map(Session.map)
	return panel


## The land: a picture of the map the seed and the sliders will make, its size and how much mountain, water and forest it has.
func _build_land_box() -> Control:
	_preview_maker = load("res://game/MapPreview.cs").new()
	_preview_timer = Timer.new()
	_preview_timer.one_shot = true
	_preview_timer.wait_time = 0.25
	_preview_timer.timeout.connect(_refresh_preview)
	add_child(_preview_timer)

	_land_box = HBoxContainer.new()
	_land_box.add_theme_constant_override("separation", 16)
	_preview = TextureRect.new()
	_preview.custom_minimum_size = Vector2(290, 290)
	_preview.size_flags_vertical = Control.SIZE_SHRINK_BEGIN
	_preview.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	_preview.stretch_mode = TextureRect.STRETCH_SCALE
	_preview.texture_filter = CanvasItem.TEXTURE_FILTER_LINEAR
	_land_box.add_child(_preview)

	var side := VBoxContainer.new()
	side.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_land_box.add_child(side)
	var size_label := Label.new()
	size_label.text = "Map size"
	side.add_child(size_label)
	var sizes := HBoxContainer.new()
	side.add_child(sizes)
	for i in 3:
		var button := Button.new()
		button.text = ["Small", "Normal", "Large"][i]
		button.toggle_mode = true
		button.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		button.pressed.connect(_on_size_changed.bind(i))
		sizes.add_child(button)
		_size_buttons.append(button)
	_preview_scale = Label.new()
	_preview_scale.theme_type_variation = "Dim"
	side.add_child(_preview_scale)
	for entry in [["mountains", "Mountains"], ["water", "Water"], ["forests", "Forests"]]:
		var label := Label.new()
		label.text = entry[1]
		side.add_child(label)
		var slider := HSlider.new()
		slider.min_value = 0.0
		slider.max_value = 1.0
		slider.step = 0.05
		slider.value = Session.get(entry[0])
		slider.value_changed.connect(_on_land_changed.bind(entry[0]))
		side.add_child(slider)
		_land_sliders[entry[0]] = slider
	return _land_box


func _on_size_changed(size: int) -> void:
	Session.size = size
	for i in _size_buttons.size():
		_size_buttons[i].set_pressed_no_signal(i == size)
	var most: int = _preview_maker.MostTowns(size)
	_towns_slider.max_value = most
	_towns_slider.value = mini(int(_towns_slider.value), most)
	_preview_scale.text = "%.1f km on a side" % (_preview_maker.SideMetres(size) / 1000.0)
	_queue_preview()


func _on_land_changed(value: float, key: String) -> void:
	Session.set(key, value)
	_queue_preview()


func _queue_preview() -> void:
	if _preview_timer != null:
		_preview_timer.start()


func _refresh_preview() -> void:
	if not _land_box.visible:
		return
	var image: Image = _preview_maker.Render(int(_seed.value), Session.size, Session.mountains, Session.water, Session.forests)
	_preview.texture = ImageTexture.create_from_image(image)


func _select_map(id: String) -> void:
	Session.map = id
	for key in _map_buttons:
		_map_buttons[key].set_pressed_no_signal(key == id)
	for map in Session.MAPS:
		if map["id"] == id:
			_map_text.text = map["text"]
			_towns_row.visible = map.get("towns", false)
			_industries_row.visible = map.get("towns", false)
			_land_box.visible = map.get("towns", false)
			if _land_box.visible:
				_queue_preview()


func _on_industries_changed(setting: int) -> void:
	Session.industries = setting
	for i in _industries_buttons.size():
		_industries_buttons[i].set_pressed_no_signal(i == setting)


func _on_frequency_changed(setting: int) -> void:
	Session.frequency = setting
	for i in _frequency_buttons.size():
		_frequency_buttons[i].set_pressed_no_signal(i == setting)


func _on_towns_changed(value: float) -> void:
	Session.towns = int(value)
	_towns_value.text = str(Session.towns)


func _start() -> void:
	Session.world_seed = int(_seed.value)
	Session.load_path = ""
	get_tree().change_scene_to_file(GAME)


## Starts the game scene on a save; main.gd loads it (and comes back here with Session.menu_message when it cannot).
func _load(path: String) -> void:
	Session.load_path = path
	get_tree().change_scene_to_file(GAME)


func _show(page: Control) -> void:
	if _notice != null and page != _main_page:
		_notice.visible = false
	for candidate in [_main_page, _new_game_page, _settings_page, _load_page, _credits_page]:
		candidate.visible = candidate == page


func _button(parent: Control, text: String, action: Callable) -> Button:
	var button := Button.new()
	button.text = text
	if action.is_valid():
		button.pressed.connect(action)
	parent.add_child(button)
	return button
