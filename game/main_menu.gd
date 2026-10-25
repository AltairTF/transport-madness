extends Control
## The first screen: continue the newest save, new game, load a save, settings, quit.
## A run with command line arguments (the self-tests, the benchmark) skips it and goes straight to the game.

const GAME := "res://game/main.tscn"
const UiTheme := preload("res://game/ui_theme.gd")
const Kit := preload("res://game/ui/ui_kit.gd")
const SettingsMenu := preload("res://game/settings_menu.gd")
const SaveMenu := preload("res://game/save_menu.gd")
const CreditsMenu := preload("res://game/credits_menu.gd")

var _main_page: Control
var _pages: Control
var _new_game_page: Control
var _settings_page: Control
var _load_page: Control
var _credits_page: Control
var _notice: Label
var _seed: SpinBox
var _towns_value: Label
var _towns_slider: HSlider
var _industries_option: OptionButton
var _frequency_option: OptionButton
var _summary: Label
var _preview_column: Control
var _preview: TextureRect
var _preview_loading: Control ## "Generating map" over the preview while a map is being made
var _preview_serial := 0 ## which request the picture on screen waits for; an older map that arrives late is dropped
var _preview_scale: Label
var _preview_timer: Timer
var _size_buttons: Array[Button] = []
var _land_sliders: Dictionary = {}
var _preview_maker: RefCounted

## Every field of the screen (segmented choice, seed, dropdown) is this tall.
const FIELD_HEIGHT := 52
const SIZE_NAMES := ["Small", "Normal", "Large"]
const INDUSTRY_NAMES := ["Few", "Normal", "Many"]
const LEGEND := [["Plains", Color(0.52, 0.62, 0.32)], ["Forest", Color(0.20, 0.42, 0.22)], ["Mountains", Color(0.46, 0.44, 0.42)], ["Water", Color(0.18, 0.36, 0.56)]]


func _ready() -> void:
	if not OS.get_cmdline_user_args().is_empty():
		get_tree().change_scene_to_file.call_deferred(GAME)
		return
	theme = UiTheme.build()

	var backdrop := ColorRect.new()
	backdrop.color = UiTheme.PANEL.darkened(0.1)
	backdrop.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(backdrop)

	_main_page = _build_main_page()
	add_child(_main_page)

	_pages = CenterContainer.new()
	_pages.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(_pages)
	_new_game_page = _build_new_game_page()
	_settings_page = SettingsMenu.new()
	_settings_page.closed.connect(func() -> void: _show(_main_page))
	_load_page = SaveMenu.new()
	_load_page.closed.connect(func() -> void: _show(_main_page))
	_load_page.load_requested.connect(_load)
	_credits_page = CreditsMenu.new()
	_credits_page.closed.connect(func() -> void: _show(_main_page))
	for page in [_new_game_page, _settings_page, _load_page, _credits_page]:
		_pages.add_child(page)

	var footer := Label.new()
	footer.text = "Whitebox build"
	footer.theme_type_variation = "Dim"
	footer.add_theme_font_size_override("font_size", 16)
	footer.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_LEFT, Control.PRESET_MODE_MINSIZE, 24)
	add_child(footer)
	_show(_main_page)


## Esc goes back to the first page.
func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_ESCAPE and not _main_page.visible:
		get_viewport().set_input_as_handled()
		_show(_main_page)


## The first page: the title and the buttons on a side panel, a picture of a route beside it.
func _build_main_page() -> Control:
	var page := HBoxContainer.new()
	page.set_anchors_preset(Control.PRESET_FULL_RECT)
	page.add_theme_constant_override("separation", 0)
	var side := PanelContainer.new()
	side.theme_type_variation = "Side"
	side.custom_minimum_size = Vector2(660, 0)
	page.add_child(side)
	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", 90)
	margin.add_theme_constant_override("margin_right", 90)
	side.add_child(margin)
	var column := VBoxContainer.new()
	column.alignment = BoxContainer.ALIGNMENT_CENTER
	column.add_theme_constant_override("separation", 12)
	margin.add_child(column)
	var art := preload("res://game/menu_art.gd").new()
	art.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	page.add_child(art)

	var eyebrow := Label.new()
	eyebrow.text = "A REGION AT WAR · 1938 TO 1945"
	eyebrow.theme_type_variation = "Section"
	eyebrow.add_theme_color_override("font_color", UiTheme.TEXT_DIM)
	column.add_child(eyebrow)
	var title := Label.new()
	title.text = "Transport\nMadness"
	title.theme_type_variation = "Title"
	title.add_theme_font_size_override("font_size", 84)
	column.add_child(title)
	var gap := Control.new()
	gap.custom_minimum_size = Vector2(0, 28)
	column.add_child(gap)

	var slots: RefCounted = load("res://game/SaveSlots.cs").new()
	var latest: String = slots.Latest()
	var continue_button := _button(column, "Continue", func() -> void: _load(latest))
	continue_button.theme_type_variation = "PrimaryButton"
	continue_button.custom_minimum_size = Vector2(0, 54)
	continue_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	continue_button.disabled = latest == ""
	continue_button.tooltip_text = "Load the newest save." if latest != "" else "No save yet."
	if latest != "":
		var hint := Label.new()
		hint.text = _save_hint(slots.List())
		hint.add_theme_font_size_override("font_size", 16)
		hint.add_theme_color_override("font_color", UiTheme.ACCENT_TEXT)
		hint.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
		hint.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
		hint.mouse_filter = Control.MOUSE_FILTER_IGNORE
		hint.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT, Control.PRESET_MODE_MINSIZE, 0)
		hint.offset_right = -18
		continue_button.add_child(hint)
	_button(column, "New game", _open_new_game).custom_minimum_size = Vector2(0, 48)
	var load_button := _button(column, "Load game", func() -> void:
		_load_page.show_load()
		_show(_load_page))
	load_button.custom_minimum_size = Vector2(0, 48)
	load_button.disabled = slots.List().is_empty()
	var small := Kit.row()
	column.add_child(small)
	for entry in [["Settings", func() -> void: _show(_settings_page)], ["Credits", func() -> void: _show(_credits_page)], ["Quit", get_tree().quit]]:
		var b := _button(small, entry[0], entry[1])
		b.size_flags_horizontal = Control.SIZE_EXPAND_FILL

	_notice = Label.new()
	_notice.add_theme_color_override("font_color", UiTheme.BAD)
	_notice.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_notice.text = Session.menu_message # why the last load failed, if it did
	_notice.visible = Session.menu_message != ""
	Session.menu_message = ""
	column.add_child(_notice)
	return page


## "autosave · March 1940": which save Continue loads.
func _save_hint(rows: Array) -> String:
	for row in rows:
		if row["error"] == "":
			var kind: String = {"auto": "autosave", "quick": "quick save"}.get(row["kind"], "save")
			return kind + " · " + row["date"]
	return ""


## A caption in small spaced caps over a control.
func _field(caption: String, control: Control) -> VBoxContainer:
	var field := VBoxContainer.new()
	field.add_theme_constant_override("separation", 8)
	field.add_child(Kit.section(caption))
	field.add_child(control)
	return field


## A label, a slider and its figure on one line of a card.
func _slider_row(parent: Control, caption: String, slider: HSlider) -> Label:
	var row := Kit.row()
	var label := Label.new()
	label.text = caption
	label.custom_minimum_size = Vector2(120, 0)
	row.add_child(label)
	slider.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	slider.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	row.add_child(slider)
	var value := Label.new()
	value.custom_minimum_size = Vector2(44, 0)
	value.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	row.add_child(value)
	parent.add_child(row)
	return value


func _option(names: Array, pick: Callable) -> OptionButton:
	var option := OptionButton.new()
	for text in names:
		option.add_item(text)
	option.item_selected.connect(pick)
	option.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	option.custom_minimum_size = Vector2(0, FIELD_HEIGHT)
	return option


func _build_new_game_page() -> Control:
	var panel := PanelContainer.new()
	panel.custom_minimum_size = Vector2(1180, 0)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	panel.add_child(column)

	var head := Kit.row()
	column.add_child(head)
	var back_arrow := _button(head, "‹", func() -> void: _show(_main_page))
	back_arrow.custom_minimum_size = Vector2(44, 44)
	back_arrow.tooltip_text = "Back"
	var heading := Kit.heading("New game")
	heading.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	head.add_child(heading)

	var body := HBoxContainer.new()
	body.add_theme_constant_override("separation", 28)
	column.add_child(body)
	var left := VBoxContainer.new()
	left.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	left.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	body.add_child(left)

	# The size of the map, and the seed.
	var size_seed := Kit.row()
	left.add_child(size_seed)
	var sizes := Kit.segmented(SIZE_NAMES, _on_size_changed)
	_size_buttons = sizes["buttons"]
	sizes["box"].custom_minimum_size = Vector2(0, FIELD_HEIGHT)
	var size_column := VBoxContainer.new()
	size_column.add_theme_constant_override("separation", 8)
	size_column.add_child(Kit.section("Size"))
	size_column.add_child(sizes["box"])
	_preview_scale = Kit.note("")
	size_column.add_child(_preview_scale)
	size_seed.add_child(size_column)
	var seed_row := Kit.row()
	_seed = SpinBox.new()
	_seed.min_value = 1
	_seed.max_value = 999999
	_seed.value = Session.world_seed
	_seed.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_seed.custom_minimum_size = Vector2(0, FIELD_HEIGHT)
	_seed.add_theme_icon_override("updown", ImageTexture.create_from_image(Image.create(1, 1, false, Image.FORMAT_RGBA8))) # no arrows: the seed is typed or rolled
	_seed.value_changed.connect(func(_v: float) -> void:
		_queue_preview()
		_update_summary())
	seed_row.add_child(_seed)
	var random := Kit.button("↻", func() -> void: _seed.value = randi_range(1, 999999), "Random seed")
	random.custom_minimum_size = Vector2(FIELD_HEIGHT, FIELD_HEIGHT)
	random.add_theme_font_size_override("font_size", 22)
	seed_row.add_child(random)
	var seed_column := _field("Map seed", seed_row)
	seed_column.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_seed.add_child(seed_column)

	# The land and the towns: sliders in one card.
	var land := VBoxContainer.new()
	land.add_theme_constant_override("separation", 8)
	land.add_child(Kit.section("The land"))
	var land_card := Kit.card()
	var land_body: VBoxContainer = land_card.get_meta("body")
	land.add_child(land_card)
	_preview_maker = load("res://game/MapPreview.cs").new()
	_preview_timer = Timer.new()
	_preview_timer.one_shot = true
	_preview_timer.wait_time = 0.25
	_preview_timer.timeout.connect(_refresh_preview)
	add_child(_preview_timer)
	for entry in [["mountains", "Mountains"], ["water", "Water"], ["forests", "Forests"]]:
		var slider := HSlider.new()
		slider.min_value = 0.0
		slider.max_value = 1.0
		slider.step = 0.05
		slider.value = Session.get(entry[0])
		var figure := _slider_row(land_body, entry[1], slider)
		slider.value_changed.connect(_on_land_changed.bind(entry[0], figure))
		figure.text = str(roundi(slider.value * 100.0))
		_land_sliders[entry[0]] = slider
		land_body.add_child(HSeparator.new())
	_towns_slider = HSlider.new()
	_towns_slider.min_value = 1
	_towns_slider.max_value = 5
	_towns_slider.step = 1
	_towns_slider.value = Session.towns
	_towns_value = _slider_row(land_body, "Towns", _towns_slider)
	_towns_slider.value_changed.connect(_on_towns_changed)
	left.add_child(land)

	# Industries and how often things break.
	var options := Kit.row()
	left.add_child(options)
	_industries_option = _option(INDUSTRY_NAMES, _on_industries_changed)
	var industries := _field("Industries", _industries_option)
	industries.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	options.add_child(industries)
	_frequency_option = _option(Kit.FREQUENCY_NAMES, _on_frequency_changed)
	_frequency_option.tooltip_text = "How often breakdowns and signal failures happen. It can be changed in the Maintenance window during the game. Accidents that come out of the driving (overtaking gone wrong, crashes at junctions) are not scaled, so Off does not remove all accidents."
	var frequency := _field("Breakdowns and incidents", _frequency_option)
	frequency.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	options.add_child(frequency)

	# The preview of the land, with what its colours mean.
	_preview_column = VBoxContainer.new()
	_preview_column.custom_minimum_size = Vector2(380, 0)
	_preview_column.add_theme_constant_override("separation", 8)
	_preview_column.add_child(Kit.section("Preview"))
	_preview = TextureRect.new()
	_preview.custom_minimum_size = Vector2(380, 380)
	_preview.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	_preview.stretch_mode = TextureRect.STRETCH_SCALE
	_preview.texture_filter = CanvasItem.TEXTURE_FILTER_LINEAR
	_preview_column.add_child(_preview)
	_preview_loading = _build_preview_loading()
	_preview.add_child(_preview_loading)
	var legend := Kit.flow()
	for entry in LEGEND:
		var key := Kit.row()
		key.add_theme_constant_override("separation", 6)
		var swatch := ColorRect.new()
		swatch.color = entry[1]
		swatch.custom_minimum_size = Vector2(10, 10)
		swatch.size_flags_vertical = Control.SIZE_SHRINK_CENTER
		key.add_child(swatch)
		var caption := Label.new()
		caption.text = entry[0]
		caption.theme_type_variation = "Small"
		key.add_child(caption)
		legend.add_child(key)
	_preview_column.add_child(legend)
	_preview_column.add_child(Kit.note("Updates a moment after you change a slider or the seed."))
	body.add_child(_preview_column)

	column.add_child(HSeparator.new())
	var footer := Kit.row()
	column.add_child(footer)
	_summary = Label.new()
	_summary.theme_type_variation = "Dim"
	_summary.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	footer.add_child(_summary)
	var back := _button(footer, "Back", func() -> void: _show(_main_page))
	back.custom_minimum_size = Vector2(90, 44)
	var start := _button(footer, "Start", _start)
	start.theme_type_variation = "PrimaryButton"
	start.custom_minimum_size = Vector2(90, 44)

	_on_towns_changed(Session.towns)
	_on_size_changed(Session.size)
	_on_industries_changed(Session.industries)
	_on_frequency_changed(Session.frequency)
	_update_summary()
	return panel


func _on_size_changed(size: int) -> void:
	Session.size = size
	for i in _size_buttons.size():
		_size_buttons[i].set_pressed_no_signal(i == size)
	var most: int = _preview_maker.MostTowns(size)
	_towns_slider.max_value = most
	_towns_slider.value = mini(int(_towns_slider.value), most)
	_preview_scale.text = "%.1f km across" % (_preview_maker.SideMetres(size) / 1000.0)
	_queue_preview()


func _on_land_changed(value: float, key: String, figure: Label) -> void:
	Session.set(key, value)
	figure.text = str(roundi(value * 100.0))
	_queue_preview()


func _queue_preview() -> void:
	if _preview_timer != null:
		_preview_loading.visible = true
		_preview_timer.start()


func _refresh_preview() -> void:
	if _preview_column == null or not _preview_column.visible:
		return
	# The map is made on a worker thread (the screen keeps answering); only the newest request puts its picture up.
	_preview_serial += 1
	var serial := _preview_serial
	var seed_value := int(_seed.value)
	var size: int = Session.size
	var mountains: float = Session.mountains
	var water: float = Session.water
	var forests: float = Session.forests
	WorkerThreadPool.add_task(func() -> void:
		var image: Image = _preview_maker.Render(seed_value, size, mountains, water, forests)
		_preview_ready.call_deferred(image, serial))


func _preview_ready(image: Image, serial: int) -> void:
	if serial != _preview_serial:
		return
	_preview.texture = ImageTexture.create_from_image(image)
	_preview_loading.visible = false


## A dark veil over the picture with "Generating map" and a bar that moves, so a map being made is plain to see.
func _build_preview_loading() -> Control:
	var veil := ColorRect.new()
	veil.color = Color(UiTheme.PANEL.darkened(0.3), 0.82)
	veil.set_anchors_preset(Control.PRESET_FULL_RECT)
	var center := CenterContainer.new()
	center.set_anchors_preset(Control.PRESET_FULL_RECT)
	veil.add_child(center)
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 12)
	center.add_child(box)
	var caption := Label.new()
	caption.text = "Generating map…"
	caption.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	box.add_child(caption)
	var bar := ProgressBar.new()
	bar.indeterminate = true
	bar.show_percentage = false
	bar.custom_minimum_size = Vector2(220, 8)
	box.add_child(bar)
	return veil


## Opens the new-game page on a map nobody has seen: the seed is drawn again each time (it can still be typed or rolled).
func _open_new_game() -> void:
	_seed.value = randi_range(1, 999999)
	_queue_preview()
	_show(_new_game_page)


func _on_industries_changed(setting: int) -> void:
	Session.industries = setting
	_industries_option.select(setting)
	_update_summary()


func _on_frequency_changed(setting: int) -> void:
	Session.frequency = setting
	_frequency_option.select(setting)


func _on_towns_changed(value: float) -> void:
	Session.towns = int(value)
	_towns_value.text = str(Session.towns)
	_update_summary()


func _update_summary() -> void:
	if _summary == null or _seed == null:
		return
	_summary.text = "%d towns · %s industries · seed %d" % [Session.towns, INDUSTRY_NAMES[Session.industries].to_lower(), int(_seed.value)]


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
	_main_page.visible = page == _main_page
	_pages.visible = page != _main_page
	for candidate in _pages.get_children():
		candidate.visible = candidate == page


func _button(parent: Control, text: String, action: Callable) -> Button:
	var button := Button.new()
	button.text = text
	if action.is_valid():
		button.pressed.connect(action)
	parent.add_child(button)
	return button
