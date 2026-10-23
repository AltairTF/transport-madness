extends Control
## The floating windows of the HUD (M6d): opened, raised, dragged by the title, resized by the corner and closed; the position and
## size of each kind are kept in the settings. A list window (Lines, Towns, ...) is one of its kind and keeps its page when closed,
## so its numbers are current when it opens again; an object window (one vehicle, one station) is made when opened and freed when
## closed, and at most MAX_OBJECT_WINDOWS are open: the oldest goes.

signal window_opened(key: String)
signal window_closed(key: String)

const Kit := preload("res://game/ui/ui_kit.gd")
const MAX_OBJECT_WINDOWS := 6
const MIN_SIZE := Vector2(280, 160)
const EDGE := 12.0 ## windows keep this far inside the screen
const TOP := 100.0 ## below the top strip
const BOTTOM := 150.0 ## above the bottom bar

var _windows: Dictionary = {} ## key -> {"frame", "content", "object", "kind", "title"}
var _order: Array[String] = [] ## open windows, topmost last


func _ready() -> void:
	set_anchors_preset(Control.PRESET_FULL_RECT)
	mouse_filter = Control.MOUSE_FILTER_IGNORE


func is_open(key: String) -> bool:
	return _windows.has(key) and _windows[key]["frame"].visible


## Opens a list window around <content> (kept when it closes), or raises it when it is open.
func open_list(key: String, title: String, content: Control, size_wanted := Vector2(540, 780)) -> void:
	if not _windows.has(key):
		_windows[key] = {"frame": _frame(key, title, content, size_wanted, key), "content": content, "object": false, "kind": key, "title": title}
	_show(key)


func toggle_list(key: String, title: String, content: Control) -> void:
	if is_open(key):
		close(key)
	else:
		open_list(key, title, content)


## Opens a window for one object (<kind> sets where it goes and how big it is), or raises it when it is open already.
func open_object(key: String, kind: String, title: String, content: Control) -> void:
	if _windows.has(key):
		content.queue_free()
		_show(key)
		return
	var objects := _order.filter(func(k: String) -> bool: return _windows[k]["object"])
	if objects.size() >= MAX_OBJECT_WINDOWS:
		close(objects[0])
	_windows[key] = {"frame": _frame(key, title, content, Vector2(440, 680), "object:" + kind), "content": content, "object": true, "kind": "object:" + kind, "title": title}
	_show(key)


func set_title(key: String, title: String) -> void:
	if _windows.has(key):
		_windows[key]["frame"].get_meta("title_label").text = title


func content_of(key: String) -> Control:
	return _windows[key]["content"] if _windows.has(key) else null


## The keys of the object windows open now.
func object_keys() -> Array[String]:
	var out: Array[String] = []
	for key in _order:
		if _windows[key]["object"]:
			out.append(key)
	return out


func close(key: String) -> void:
	if not _windows.has(key):
		return
	var entry: Dictionary = _windows[key]
	_order.erase(key)
	if entry["object"]:
		entry["frame"].queue_free()
		_windows.erase(key)
	else:
		entry["frame"].visible = false
	window_closed.emit(key)


## Closes the topmost window; false when none is open (Esc then opens the menu).
func close_top() -> bool:
	if _order.is_empty():
		return false
	close(_order[-1])
	return true


func _show(key: String) -> void:
	var frame: Control = _windows[key]["frame"]
	frame.visible = true
	frame.move_to_front()
	_order.erase(key)
	_order.append(key)
	_keep_inside(frame)
	window_opened.emit(key)


func _raise(key: String) -> void:
	if _order.has(key) and _order[-1] != key:
		_windows[key]["frame"].move_to_front()
		_order.erase(key)
		_order.append(key)


func _frame(key: String, title: String, content: Control, size_wanted: Vector2, rect_key: String) -> PanelContainer:
	var frame := PanelContainer.new()
	frame.mouse_filter = Control.MOUSE_FILTER_STOP
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", Kit.GAP)
	frame.add_child(column)
	var head := Kit.row()
	column.add_child(head)
	var label := Kit.heading(title)
	label.autowrap_mode = TextServer.AUTOWRAP_OFF
	label.clip_text = true
	label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	label.mouse_filter = Control.MOUSE_FILTER_STOP
	label.mouse_default_cursor_shape = Control.CURSOR_MOVE
	label.tooltip_text = "Drag to move"
	head.add_child(label)
	head.add_child(Kit.button("✕", close.bind(key), "Close (Esc closes the topmost window)"))
	var scroll := ScrollContainer.new()
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	content.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(content)
	column.add_child(scroll)
	var grip := Label.new()
	grip.text = "◢"
	grip.size_flags_horizontal = Control.SIZE_SHRINK_END
	grip.mouse_filter = Control.MOUSE_FILTER_STOP
	grip.mouse_default_cursor_shape = Control.CURSOR_FDIAGSIZE
	grip.tooltip_text = "Drag to resize"
	column.add_child(grip)
	add_child(frame)

	var saved: Dictionary = Settings.window_rects
	if saved.has(rect_key):
		var r: Rect2 = saved[rect_key]
		frame.position = r.position + Vector2(24, 24) * _same_kind_open(rect_key)
		frame.size = r.size
	else:
		frame.size = size_wanted
		# Lists open down the left, objects in the middle, each a step down and right of the one before.
		var n := _order.filter(func(k: String) -> bool: return _windows[k]["object"] == rect_key.begins_with("object:")).size()
		var screen := get_viewport_rect().size
		var origin := Vector2(screen.x * 0.5 - size_wanted.x * 0.5, TOP + 40.0) if rect_key.begins_with("object:") else Vector2(EDGE + 20.0, TOP + 20.0)
		frame.position = origin + Vector2(36, 32) * n
	frame.set_meta("title_label", label)

	label.gui_input.connect(func(event: InputEvent) -> void:
		if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
			if event.pressed:
				_raise(key)
			else:
				_remember(rect_key, frame)
		elif event is InputEventMouseMotion and event.button_mask & MOUSE_BUTTON_MASK_LEFT:
			frame.position += event.relative
			_keep_inside(frame))
	grip.gui_input.connect(func(event: InputEvent) -> void:
		if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT and not event.pressed:
			_remember(rect_key, frame)
		elif event is InputEventMouseMotion and event.button_mask & MOUSE_BUTTON_MASK_LEFT:
			frame.size = (frame.size + event.relative).max(MIN_SIZE)
			_keep_inside(frame))
	frame.gui_input.connect(func(event: InputEvent) -> void:
		if event is InputEventMouseButton and event.pressed:
			_raise(key))
	return frame


func _same_kind_open(rect_key: String) -> int:
	var n := 0
	for k in _order:
		if _windows[k]["kind"] == rect_key:
			n += 1
	return n


func _keep_inside(frame: Control) -> void:
	var screen := get_viewport_rect().size
	frame.size = frame.size.min(screen - Vector2(2 * EDGE, TOP + BOTTOM)).max(MIN_SIZE)
	frame.position = frame.position.clamp(Vector2(EDGE, TOP), (screen - frame.size - Vector2(EDGE, BOTTOM)).max(Vector2(EDGE, TOP)))


func _remember(rect_key: String, frame: Control) -> void:
	Settings.window_rects[rect_key] = Rect2(frame.position, frame.size)
	Settings.save()
