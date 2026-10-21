extends PanelContainer
## The list of saves (M6a), as a page of the main menu and of the pause menu. In "load" mode every save can be loaded or
## deleted; in "save" mode the player types a name (or picks a named save to overwrite). Each row shows the screenshot, the
## name, the month of the game, the money, the standing and when it was saved.

signal closed
signal load_requested(path: String)
signal save_requested(save_name: String)

var _slots: RefCounted
var _mode := "load"
var _title: Label
var _name_row: Control
var _name_edit: LineEdit
var _rows: VBoxContainer
var _empty: Label
var _pending_delete := ""


func _init() -> void:
	_slots = load("res://game/SaveSlots.cs").new() # C#: loaded at run time, as the menus need it
	custom_minimum_size = Vector2(760, 0)
	var column := VBoxContainer.new()
	add_child(column)
	_title = Label.new()
	_title.theme_type_variation = "Heading"
	column.add_child(_title)

	_name_row = HBoxContainer.new()
	column.add_child(_name_row)
	_name_edit = LineEdit.new()
	_name_edit.placeholder_text = "Name of the save"
	_name_edit.max_length = 40
	_name_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_name_edit.text_submitted.connect(func(_text: String) -> void: _save_named(_name_edit.text))
	_name_row.add_child(_name_edit)
	var save_button := Button.new()
	save_button.text = "Save"
	save_button.pressed.connect(func() -> void: _save_named(_name_edit.text))
	_name_row.add_child(save_button)

	var scroll := ScrollContainer.new()
	scroll.custom_minimum_size = Vector2(740, 420)
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	column.add_child(scroll)
	_rows = VBoxContainer.new()
	_rows.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(_rows)
	_empty = Label.new()
	_empty.theme_type_variation = "Dim"
	_empty.text = "No saves yet."
	column.add_child(_empty)

	var back := Button.new()
	back.text = "Back"
	back.pressed.connect(func() -> void: closed.emit())
	column.add_child(back)


func show_load() -> void:
	_mode = "load"
	_title.text = "Load game"
	_name_row.visible = false
	refresh()


func show_save(default_name: String) -> void:
	_mode = "save"
	_title.text = "Save game"
	_name_row.visible = true
	_name_edit.text = default_name
	refresh()
	_name_edit.grab_focus.call_deferred()
	_name_edit.select_all.call_deferred()


func has_saves() -> bool:
	return _slots.Latest() != ""


func latest() -> String:
	return _slots.Latest()


func refresh() -> void:
	_pending_delete = ""
	for child in _rows.get_children():
		child.queue_free()
	var shown := 0
	for row in _slots.List():
		if _mode == "save" and row["kind"] != "slot":
			continue
		_rows.add_child(_build_row(row))
		shown += 1
	_empty.visible = shown == 0
	_empty.text = "No saves yet." if _mode == "load" else "No named saves yet: type a name above."


func _save_named(save_name: String) -> void:
	if save_name.strip_edges() == "":
		_name_edit.placeholder_text = "Type a name first"
		return
	save_requested.emit(save_name.strip_edges())


func _build_row(row: Dictionary) -> Control:
	var line := HBoxContainer.new()
	line.add_theme_constant_override("separation", 12)
	var picture := TextureRect.new()
	picture.custom_minimum_size = Vector2(160, 90)
	picture.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	picture.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
	if row["shot"] != "":
		var image := Image.load_from_file(ProjectSettings.globalize_path(row["shot"]))
		if image != null and not image.is_empty():
			picture.texture = ImageTexture.create_from_image(image)
	line.add_child(picture)

	var text := Label.new()
	text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	var kind: String = {"auto": "  (autosave)", "quick": "  (quick save)"}.get(row["kind"], "")
	if row["error"] != "":
		text.text = "%s%s\n%s\nSaved %s" % [row["name"], kind, row["error"], row["saved"]]
		text.add_theme_color_override("font_color", Color(1.0, 0.6, 0.5))
	else:
		text.text = "%s%s\n%s   money %s   standing %d\nSaved %s" % [row["name"], kind, row["date"], _money(row["money"]), roundi(row["standing"]), row["saved"]]
	line.add_child(text)

	var buttons := VBoxContainer.new()
	line.add_child(buttons)
	if _mode == "load":
		var load_button := Button.new()
		load_button.text = "Load"
		load_button.disabled = row["error"] != ""
		load_button.pressed.connect(func() -> void: load_requested.emit(row["path"]))
		buttons.add_child(load_button)
		var delete := Button.new()
		delete.text = "Delete"
		delete.pressed.connect(func() -> void:
			if _pending_delete == row["path"]:
				_slots.Delete(row["path"])
				refresh()
			else:
				_pending_delete = row["path"]
				delete.text = "Delete? Click again")
		buttons.add_child(delete)
	else:
		var overwrite := Button.new()
		overwrite.text = "Overwrite"
		overwrite.pressed.connect(func() -> void: save_requested.emit(row["name"]))
		buttons.add_child(overwrite)
	return line


static func _money(amount: Variant) -> String:
	var value := int(amount)
	var digits := str(absi(value))
	var out := ""
	for i in digits.length():
		if i > 0 and (digits.length() - i) % 3 == 0:
			out += ","
		out += digits[i]
	return ("-" if value < 0 else "") + out
