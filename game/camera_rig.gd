extends Node3D
## Free orbit camera: right mouse orbits, middle mouse or WASD pans, wheel zooms.

const MIN_DISTANCE := 20.0
const MAX_DISTANCE := 5000.0

var distance := 1100.0
var yaw := 0.0
var pitch := -55.0

@onready var camera: Camera3D = $Camera3D


func _ready() -> void:
	_apply()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_WHEEL_UP:
			distance = max(MIN_DISTANCE, distance * 0.9)
		elif event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			distance = min(MAX_DISTANCE, distance * 1.1)
	elif event is InputEventMouseMotion:
		if Input.is_mouse_button_pressed(MOUSE_BUTTON_RIGHT):
			yaw -= event.relative.x * 0.3
			pitch = clamp(pitch - event.relative.y * 0.3, -89.0, -10.0)
		elif Input.is_mouse_button_pressed(MOUSE_BUTTON_MIDDLE):
			_pan(Vector2(-event.relative.x, -event.relative.y) * distance * 0.0015)
	_apply()


func _process(delta: float) -> void:
	var move := Vector2(
		Input.get_axis("ui_left", "ui_right") + _key_axis(KEY_A, KEY_D),
		Input.get_axis("ui_up", "ui_down") + _key_axis(KEY_W, KEY_S))
	if move != Vector2.ZERO:
		_pan(move.normalized() * distance * 0.8 * delta)
		_apply()


func _key_axis(negative: Key, positive: Key) -> float:
	return float(Input.is_key_pressed(positive)) - float(Input.is_key_pressed(negative))


func _pan(offset: Vector2) -> void:
	var forward := Vector3(-sin(deg_to_rad(yaw)), 0.0, -cos(deg_to_rad(yaw)))
	var right := Vector3(cos(deg_to_rad(yaw)), 0.0, -sin(deg_to_rad(yaw)))
	position += right * offset.x - forward * offset.y


func _apply() -> void:
	rotation_degrees = Vector3(pitch, yaw, 0.0)
	camera.position = Vector3(0.0, 0.0, distance)
