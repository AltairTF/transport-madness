extends CanvasLayer
## The menu opened with Esc (or the HUD's Menu button): resume, save, load, settings, back to the main menu, quit.
## Opening it pauses the scene tree, which stops the simulation, the camera and the tools. Leaving or loading with
## progress made since the last save asks first.

signal main_menu_requested
signal quit_requested
signal save_requested(save_name: String)
signal load_requested(path: String)

const UiTheme := preload("res://game/ui_theme.gd")
const SettingsMenu := preload("res://game/settings_menu.gd")
const SaveMenu := preload("res://game/save_menu.gd")

## Set by main.gd: whether there is progress since the last save, and the name a new save is offered.
var unsaved: Callable = func() -> bool: return false
var name_for_save: Callable = func() -> String: return "My game"

var _root: Control
var _main_page: Control
var _settings_page: Control
var _confirm_page: Control
var _save_page: Control
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
	_save_page = SaveMenu.new()
	_save_page.closed.connect(func() -> void: _show(_main_page))
	_save_page.save_requested.connect(func(save_name: String) -> void: save_requested.emit(save_name))
	_save_page.load_requested.connect(func(path: String) -> void:
		if unsaved.call():
			_ask("Load this save? Progress since your last save will be lost.", load_requested.emit.bind(path))
		else:
			load_requested.emit(path))
	for page in [_main_page, _settings_page, _confirm_page, _save_page]:
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
	column.add_theme_constant_override("separation", 12)
	_button(column, "Resume", close).theme_type_variation = "PrimaryButton"
	_button(column, "Save game", func() -> void:
		_save_page.show_save(name_for_save.call())
		_show(_save_page))
	_button(column, "Load game", func() -> void:
		_save_page.show_load()
		_show(_save_page))
	_button(column, "Settings", func() -> void: _show(_settings_page))
	column.add_child(HSeparator.new())
	_button(column, "Main menu", func() -> void: _leave("Leave this game? Progress since your last save will be lost.", main_menu_requested.emit))
	_button(column, "Quit to desktop", ask_quit).theme_type_variation = "DangerButton"
	var hint := Label.new()
	hint.text = "Esc resumes"
	hint.theme_type_variation = "Small"
	hint.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	column.add_child(hint)
	return panel


## Quit, asking first when there is progress since the last save (also what closing the window does).
func ask_quit() -> void:
	if not visible:
		open()
	_leave("Quit? Progress since your last save will be lost.", quit_requested.emit)


func _leave(question: String, action: Callable) -> void:
	if unsaved.call():
		_ask(question, action)
	else:
		action.call()


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
	var yes := _button(buttons, "Yes, leave", func() -> void: _confirmed.call())
	yes.theme_type_variation = "DangerButton"
	yes.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	return panel


func _ask(text: String, action: Callable) -> void:
	_confirm_text.text = text
	_confirmed = action
	_show(_confirm_page)


func _show(page: Control) -> void:
	for candidate in [_main_page, _settings_page, _confirm_page, _save_page]:
		candidate.visible = candidate == page


func _button(parent: Control, text: String, action: Callable) -> Button:
	var button := Button.new()
	button.text = text
	if action.is_valid():
		button.pressed.connect(action)
	parent.add_child(button)
	return button
