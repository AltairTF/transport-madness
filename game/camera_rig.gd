extends Node3D
## Free orbit camera: right mouse orbits, middle mouse or WASD pans, wheel zooms. The point it orbits follows the
## height of the ground below it once a host that knows the terrain is set.

const FOLLOW_RATE := 6.0 # per second: how quickly the focus settles on a new ground height

const TURN_RATE := 90.0 # degrees a second that Q and E turn the camera
const MIN_DISTANCE := 20.0
const MAX_DISTANCE := 5000.0

var distance := 1100.0
var yaw := 0.0
var pitch := -55.0
var terrain_host: Node3D ## anything with HeightAt(x, z), set by main.gd

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
	_follow_ground(delta)
	if get_viewport().gui_get_focus_owner() is LineEdit:
		return # typing a name, not moving
	var move := Vector2(
		Input.get_axis("ui_left", "ui_right") + _action_axis("camera_left", "camera_right"),
		Input.get_axis("ui_up", "ui_down") + _action_axis("camera_forward", "camera_back"))
	if move != Vector2.ZERO:
		_pan(move.normalized() * distance * 0.8 * delta)
		_apply()
	var turn := _action_axis("camera_turn_left", "camera_turn_right")
	if turn != 0.0:
		yaw -= turn * TURN_RATE * delta
		_apply()


## Eases the focus height toward the terrain under it, so panning over hills keeps the view level with the ground.
func _follow_ground(delta: float) -> void:
	if terrain_host == null:
		return
	var target: float = terrain_host.HeightAt(position.x, position.z)
	if absf(target - position.y) < 0.01:
		return
	position.y = lerpf(position.y, target, minf(1.0, FOLLOW_RATE * delta))
	_apply()


## Puts the focus on a ground point at once.
func focus_on(x: float, z: float) -> void:
	position = Vector3(x, terrain_host.HeightAt(x, z) if terrain_host != null else 0.0, z)
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


## -1, 0 or 1 from two of the actions of keys.gd (0 before they are registered).
func _action_axis(negative: String, positive: String) -> float:
	if not InputMap.has_action(negative) or not InputMap.has_action(positive):
		return 0.0
	return Input.get_action_strength(positive) - Input.get_action_strength(negative)
