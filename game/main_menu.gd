extends Control
## The first screen: new game, settings, quit. Continue and Load are placeholders until saving exists (M6).
## A run with command line arguments (the self-tests, the benchmark) skips it and goes straight to the game.

const GAME := "res://game/main.tscn"
const UiTheme := preload("res://game/ui_theme.gd")
const SettingsMenu := preload("res://game/settings_menu.gd")

var _main_page: Control
var _new_game_page: Control
var _settings_page: Control
var _map_buttons: Dictionary = {}
var _map_text: Label
var _seed: SpinBox


func _ready() -> void:
	if not OS.get_cmdline_user_args().is_empty():
		get_tree().change_scene_to_file.call_deferred(GAME)
		return
	theme = UiTheme.build()

	var backdrop := ColorRect.new()
	backdrop.color = Color(0.07, 0.10, 0.14)
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
	tagline.text = "Build the roads. Run the lines. Survive the traffic."
	tagline.theme_type_variation = "Dim"
	tagline.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	stack.add_child(tagline)

	_main_page = _build_main_page()
	_new_game_page = _build_new_game_page()
	_settings_page = SettingsMenu.new()
	_settings_page.closed.connect(func() -> void: _show(_main_page))
	for page in [_main_page, _new_game_page, _settings_page]:
		stack.add_child(page)

	var footer := Label.new()
	footer.text = "Prototype build"
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
	var continue_button := _button(column, "Continue", Callable())
	continue_button.disabled = true
	continue_button.tooltip_text = "Saving and loading are not in the game yet."
	_button(column, "New game", func() -> void: _show(_new_game_page))
	var load_button := _button(column, "Load game", Callable())
	load_button.disabled = true
	load_button.tooltip_text = "Saving and loading are not in the game yet."
	_button(column, "Settings", func() -> void: _show(_settings_page))
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

	var buttons := HBoxContainer.new()
	column.add_child(buttons)
	var back := _button(buttons, "Back", func() -> void: _show(_main_page))
	back.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var start := _button(buttons, "Start", _start)
	start.size_flags_horizontal = Control.SIZE_EXPAND_FILL

	_select_map(Session.map)
	return panel


func _select_map(id: String) -> void:
	Session.map = id
	for key in _map_buttons:
		_map_buttons[key].set_pressed_no_signal(key == id)
	for map in Session.MAPS:
		if map["id"] == id:
			_map_text.text = map["text"]


func _start() -> void:
	Session.world_seed = int(_seed.value)
	get_tree().change_scene_to_file(GAME)


func _show(page: Control) -> void:
	for candidate in [_main_page, _new_game_page, _settings_page]:
		candidate.visible = candidate == page


func _button(parent: Control, text: String, action: Callable) -> Button:
	var button := Button.new()
	button.text = text
	if action.is_valid():
		button.pressed.connect(action)
	parent.add_child(button)
	return button
