extends Node
## Turns mouse input into build commands on the simulation host. Straight roads snap to existing nodes
## (that is how junctions are made) and otherwise to 15 degree steps and 5 m lengths.

const NODE_SNAP_RADIUS := 20.0
const ANGLE_STEP := deg_to_rad(15.0)
const LENGTH_STEP := 5.0
const ROAD_COLORS := {"valid": Color(0.3, 0.9, 0.4, 0.7), "invalid": Color(0.95, 0.3, 0.3, 0.7)}

signal message(text: String)
signal tool_changed(tool_name: String)

var host: Node3D
var camera: Camera3D
var tool := "pointer" # pointer, road2, road4, depot

var _start: Variant = null # Vector3 or null
var _preview: MeshInstance3D
var _material := StandardMaterial3D.new()


func setup(sim_host: Node3D, cam: Camera3D) -> void:
	host = sim_host
	camera = cam
	_material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	_preview = MeshInstance3D.new()
	_preview.mesh = BoxMesh.new()
	_preview.material_override = _material
	_preview.visible = false
	add_child(_preview)


func set_tool(tool_name: String) -> void:
	tool = tool_name
	_start = null
	_preview.visible = false
	match tool:
		"road2": message.emit("2-lane road: click to start, click again to build (Esc stops)")
		"road4": message.emit("2x2 avenue: click to start, click again to build (Esc stops)")
		"depot": message.emit("Depot: click near a junction")
		_: message.emit("")


func _unhandled_input(event: InputEvent) -> void:
	if tool == "pointer":
		return
	if event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		if _start == null:
			set_tool("pointer")
			tool_changed.emit("pointer")
		else:
			_start = null
			_preview.visible = false
	elif event is InputEventMouseMotion:
		_update_preview(event.position)
	elif event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		_click(event.position)


func ground_point(screen: Vector2) -> Variant:
	var origin := camera.project_ray_origin(screen)
	var direction := camera.project_ray_normal(screen)
	if absf(direction.y) < 0.001:
		return null
	var t := -origin.y / direction.y
	if t < 0.0:
		return null
	return origin + direction * t


func _target_point(screen: Vector2) -> Variant:
	var p: Variant = ground_point(screen)
	if p == null:
		return null
	var snap: Dictionary = host.SnapToNode(p.x, p.z, NODE_SNAP_RADIUS)
	if snap["snapped"]:
		return Vector3(snap["x"], 0.0, snap["z"])
	if _start != null:
		var offset: Vector3 = p - _start
		var angle := roundf(atan2(offset.z, offset.x) / ANGLE_STEP) * ANGLE_STEP
		var length := maxf(LENGTH_STEP * 2.0, roundf(offset.length() / LENGTH_STEP) * LENGTH_STEP)
		return _start + Vector3(cos(angle), 0.0, sin(angle)) * length
	return Vector3(roundf(p.x / LENGTH_STEP) * LENGTH_STEP, 0.0, roundf(p.z / LENGTH_STEP) * LENGTH_STEP)


func _click(screen: Vector2) -> void:
	var target: Variant = _target_point(screen)
	if target == null:
		return
	if tool == "depot":
		var level := 1
		message.emit("Depot built" if host.BuildDepot(target.x, target.z, level) else "No free junction nearby")
		return

	if _start == null:
		_start = target
		return
	var type := 1 if tool == "road4" else 0
	if host.BuildRoad(_start.x, _start.z, target.x, target.z, type):
		message.emit("Road built")
		_start = target # keep drawing from the new end
	else:
		message.emit("Can't build there")


func _update_preview(screen: Vector2) -> void:
	var target: Variant = _target_point(screen)
	if target == null or tool == "depot":
		_preview.visible = tool == "depot" and target != null
		if _preview.visible:
			_preview.mesh.size = Vector3(22, 6, 22)
			_preview.position = target + Vector3(26, 3, 26)
			_material.albedo_color = ROAD_COLORS["valid"]
		return
	if _start == null:
		_preview.visible = true
		_preview.mesh.size = Vector3(4, 4, 4)
		_preview.position = target + Vector3(0, 2, 0)
		_material.albedo_color = ROAD_COLORS["valid"]
		return

	var offset: Vector3 = target - _start
	var valid: bool = host.CanBuildRoad(_start.x, _start.z, target.x, target.z)
	var width := 16.0 if tool == "road4" else 7.0
	_preview.visible = true
	_preview.mesh.size = Vector3(maxf(offset.length(), 0.1), 0.4, width)
	_preview.position = (_start + target) * 0.5 + Vector3(0, 0.3, 0)
	_preview.rotation = Vector3(0, atan2(-offset.z, offset.x), 0)
	_material.albedo_color = ROAD_COLORS["valid"] if valid else ROAD_COLORS["invalid"]
