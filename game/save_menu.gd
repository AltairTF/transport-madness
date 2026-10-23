extends PanelContainer
## The list of saves (M6a), as a page of the main menu and of the pause menu. In "load" mode every save can be loaded or
## deleted; in "save" mode the player types a name (or picks a named save to overwrite). Each row shows the screenshot, the
## name, the month of the game, the money, the standing and when it was saved.

const UiTheme := preload("res://game/ui_theme.gd")
const Kit := preload("res://game/ui/ui_kit.gd")

signal closed
signal load_requested(path: String)
signal save_requested(save_name: String)

var _slots: RefCounted
var _mode := "load"
var _scroll: Control
var _tab_box: Control
var _tab_buttons: Array = []
var _name_row: Control
var _name_edit: LineEdit
var _rows: VBoxContainer
var _empty: Label
var _pending_delete := ""


func _init() -> void:
	_slots = load("res://game/SaveSlots.cs").new() # C#: loaded at run time, as the menus need it
	custom_minimum_size = Vector2(820, 0)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	add_child(column)
	column.add_child(Kit.header("Save and load", func() -> void: closed.emit()))

	# Save needs a running game: where nothing listens for it (the main menu) only Load is offered.
	var tabs := Kit.segmented(["Save", "Load"], func(index: int) -> void:
		if index == 0:
			show_save(_name_edit.text)
		else:
			show_load())
	_tab_box = tabs["box"]
	_tab_buttons = tabs["buttons"]
	column.add_child(_tab_box)

	_name_row = Kit.row()
	column.add_child(_name_row)
	_name_edit = LineEdit.new()
	_name_edit.placeholder_text = "Name of the save"
	_name_edit.max_length = 40
	_name_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_name_edit.custom_minimum_size = Vector2(0, 48)
	_name_edit.text_submitted.connect(func(_text: String) -> void: _save_named(_name_edit.text))
	_name_row.add_child(_name_edit)
	var save_button := Kit.button("Save", func() -> void: _save_named(_name_edit.text))
	save_button.theme_type_variation = "PrimaryButton"
	save_button.custom_minimum_size = Vector2(100, 48)
	_name_row.add_child(save_button)

	column.add_child(Kit.section("Saved games", "Newest first"))
	var scroll := ScrollContainer.new()
	_scroll = scroll
	scroll.custom_minimum_size = Vector2(0, 420)
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	column.add_child(scroll)
	_rows = VBoxContainer.new()
	_rows.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_rows.add_theme_constant_override("separation", 8)
	scroll.add_child(_rows)
	_empty = Kit.note("No saves yet.")
	column.add_child(_empty)
	column.add_child(HSeparator.new())
	column.add_child(Kit.note("A save carries the whole game, including the tuning it was started with."))


func show_load() -> void:
	_mode = "load"
	_open_tab(1)
	_name_row.visible = false
	refresh()


func show_save(default_name: String) -> void:
	_mode = "save"
	_open_tab(0)
	_name_row.visible = true
	_name_edit.text = default_name
	refresh()
	_name_edit.grab_focus.call_deferred()
	_name_edit.select_all.call_deferred()


func _open_tab(index: int) -> void:
	_tab_box.visible = not save_requested.get_connections().is_empty()
	Kit.select_segment(_tab_buttons, index)


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
	_scroll.visible = shown > 0
	_empty.text = "No saves yet." if _mode == "load" else "No named saves yet: type a name above."


func _save_named(save_name: String) -> void:
	if save_name.strip_edges() == "":
		_name_edit.placeholder_text = "Type a name first"
		return
	save_requested.emit(save_name.strip_edges())


func _build_row(row: Dictionary) -> Control:
	var card := Kit.card()
	var line := Kit.row()
	card.get_meta("body").add_child(line)
	var picture := TextureRect.new()
	picture.custom_minimum_size = Vector2(96, 54)
	picture.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	picture.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	picture.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
	if row["shot"] != "":
		var image := Image.load_from_file(ProjectSettings.globalize_path(row["shot"]))
		if image != null and not image.is_empty():
			picture.texture = ImageTexture.create_from_image(image)
	line.add_child(picture)

	var text := VBoxContainer.new()
	text.add_theme_constant_override("separation", 2)
	text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	text.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	var kind: String = {"auto": " (autosave)", "quick": " (quick save)"}.get(row["kind"], "")
	var title := Label.new()
	title.text = row["name"] + kind
	title.clip_text = true
	text.add_child(title)
	var detail := Kit.note("")
	detail.add_theme_font_size_override("font_size", 16)
	if row["error"] != "":
		detail.text = "%s Saved %s" % [row["error"], row["saved"]]
		detail.add_theme_color_override("font_color", UiTheme.BAD)
	else:
		detail.text = "%s · money %s · standing %d · %s" % [row["date"], Kit.money(row["money"]), roundi(row["standing"]), row["saved"]]
	text.add_child(detail)
	line.add_child(text)

	if _mode == "load":
		var delete := Kit.button("Delete", Callable())
		delete.pressed.connect(func() -> void:
			if _pending_delete == row["path"]:
				_slots.Delete(row["path"])
				refresh()
			else:
				_pending_delete = row["path"]
				delete.text = "Delete? Click again")
		line.add_child(delete)
		var load_button := Kit.button("Load", func() -> void: load_requested.emit(row["path"]))
		load_button.theme_type_variation = "AccentButton"
		load_button.disabled = row["error"] != ""
		line.add_child(load_button)
	else:
		line.add_child(Kit.button("Overwrite", func() -> void: save_requested.emit(row["name"])))
	return card
