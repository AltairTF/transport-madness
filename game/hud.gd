extends CanvasLayer
## Heads-up display: speed buttons and live statistics. Emits intent, owns no game state.

signal speed_selected(speed: float)

var _stats: Label
var _buttons: Dictionary = {}


func _ready() -> void:
	var panel := PanelContainer.new()
	panel.position = Vector2(12, 12)
	add_child(panel)

	var column := VBoxContainer.new()
	panel.add_child(column)

	var row := HBoxContainer.new()
	column.add_child(row)
	for entry in [["||", 0.0], ["1x", 1.0], ["2x", 2.0], ["4x", 4.0], ["8x", 8.0]]:
		var button := Button.new()
		button.text = entry[0]
		button.toggle_mode = true
		button.pressed.connect(_on_speed_pressed.bind(entry[1]))
		row.add_child(button)
		_buttons[entry[1]] = button

	_stats = Label.new()
	column.add_child(_stats)
	highlight_speed(1.0)


func highlight_speed(speed: float) -> void:
	for key in _buttons:
		_buttons[key].set_pressed_no_signal(is_equal_approx(key, speed))


func show_stats(stats: Dictionary) -> void:
	_stats.text = "%d fps\nvehicles %d   overtaking %d\nmean speed %.1f m/s\nsim speed x%.2f   step %.2f ms\nincidents %d (open %d)   wrecks %d" % [
		Engine.get_frames_per_second(),
		stats["vehicles"], stats["overtaking"],
		stats["mean_speed"],
		stats["effective_speed"], stats["step_ms"],
		stats["incidents"], stats["open_incidents"], stats["wrecks"]]


func _on_speed_pressed(speed: float) -> void:
	highlight_speed(speed)
	speed_selected.emit(speed)
