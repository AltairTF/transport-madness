extends CanvasLayer
## The menu opened with Esc (or the HUD's Menu button): resume, settings, back to the main menu, quit.
## Opening it pauses the scene tree, which stops the simulation, the camera and the tools. Save and Load are
## placeholders until saving exists (M6).

signal main_menu_requested
signal quit_requested

const UiTheme := preload("res://game/ui_theme.gd")
const SettingsMenu := preload("res://game/settings_menu.gd")

var _root: Control
var _main_page: Control
var _settings_page: Control
var _confirm_page: Control
var _confirm_text: Label
var _confirmed: Callable


func _ready() -> void:
	layer = 20
	process_mode = Node.PROCESS_MODE_ALWAYS
	_root = Control.new()
	_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.theme = UiTheme.build()
	add_child(_root)

	var dim := ColorRect.new()
	dim.color = Color(0, 0, 0, 0.55)
	dim.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.add_child(dim)
	var center := CenterContainer.new()
	center.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.add_child(center)

	_main_page = _build_main_page()
	_settings_page = SettingsMenu.new()
	_settings_page.closed.connect(func() -> void: _show(_main_page))
	_confirm_page = _build_confirm_page()
	for page in [_main_page, _settings_page, _confirm_page]:
		center.add_child(page)
	visible = false


func is_open() -> bool:
	return visible


func open() -> void:
	_show(_main_page)
	visible = true
	get_tree().paused = true


func close() -> void:
	visible = false
	get_tree().paused = false


func _unhandled_input(event: InputEvent) -> void:
	if visible and event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_ESCAPE:
		get_viewport().set_input_as_handled()
		if _main_page.visible:
			close()
		else:
			_show(_main_page)


func _build_main_page() -> Control:
	var panel := PanelContainer.new()
	panel.custom_minimum_size = Vector2(420, 0)
	var column := VBoxContainer.new()
	panel.add_child(column)
	var title := Label.new()
	title.text = "Paused"
	title.theme_type_variation = "Title"
	title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	column.add_child(title)
	_button(column, "Resume", close)
	var save := _button(column, "Save game", Callable())
	save.disabled = true
	save.tooltip_text = "Saving and loading are not in the game yet."
	var load_button := _button(column, "Load game", Callable())
	load_button.disabled = true
	load_button.tooltip_text = "Saving and loading are not in the game yet."
	_button(column, "Settings", func() -> void: _show(_settings_page))
	_button(column, "Main menu", _ask.bind("Leave this game? It cannot be saved yet, so it will be lost.", main_menu_requested.emit))
	_button(column, "Quit to desktop", _ask.bind("Quit? This game cannot be saved yet, so it will be lost.", quit_requested.emit))
	return panel


func _build_confirm_page() -> Control:
	var panel := PanelContainer.new()
	panel.custom_minimum_size = Vector2(520, 0)
	var column := VBoxContainer.new()
	panel.add_child(column)
	_confirm_text = Label.new()
	_confirm_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(_confirm_text)
	var buttons := HBoxContainer.new()
	column.add_child(buttons)
	var no := _button(buttons, "Cancel", func() -> void: _show(_main_page))
	no.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var yes := _button(buttons, "Yes", func() -> void: _confirmed.call())
	yes.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	return panel


func _ask(text: String, action: Callable) -> void:
	_confirm_text.text = text
	_confirmed = action
	_show(_confirm_page)


func _show(page: Control) -> void:
	for candidate in [_main_page, _settings_page, _confirm_page]:
		candidate.visible = candidate == page


func _button(parent: Control, text: String, action: Callable) -> Button:
	var button := Button.new()
	button.text = text
	if action.is_valid():
		button.pressed.connect(action)
	parent.add_child(button)
	return button
