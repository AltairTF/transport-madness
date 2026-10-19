extends Node
## Turns mouse input into build commands on the simulation host. Straight roads snap to existing nodes
## (that is how junctions are made) and otherwise to 15 degree steps and 5 m lengths. Curves take three
## clicks: start, a point on the curve, end.

const NODE_SNAP_RADIUS := 20.0
const ANGLE_STEP := deg_to_rad(15.0)
const LENGTH_STEP := 5.0
const ROAD_COLORS := {"valid": Color(0.3, 0.9, 0.4, 0.7), "invalid": Color(0.95, 0.3, 0.3, 0.7)}

signal message(text: String)
signal tool_changed(tool_name: String)

var host: Node3D
var camera: Camera3D
var tool := "pointer" # pointer, road2, road4, curve2, curve4, upgrade, remove, depot

var _start: Variant = null # Vector3 or null
var _mid: Variant = null   # Vector3 or null, curves only
var _preview: MeshInstance3D
var _arc_preview: MeshInstance3D
var _hover_road := -1
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
	_arc_preview = MeshInstance3D.new()
	_arc_preview.mesh = ImmediateMesh.new()
	_arc_preview.material_override = _material
	_arc_preview.visible = false
	add_child(_arc_preview)


func set_tool(tool_name: String) -> void:
	tool = tool_name
	_reset()
	match tool:
		"road2": message.emit("2-lane road: click to start, click again to build (Esc stops)")
		"road4": message.emit("2x2 avenue: click to start, click again to build (Esc stops)")
		"curve2": message.emit("Curved 2-lane road: click the start, a point on the curve, then the end (Esc stops)")
		"curve4": message.emit("Curved avenue: click the start, a point on the curve, then the end (Esc stops)")
		"upgrade": message.emit("Upgrade: click a 2-lane road to widen it to an avenue")
		"remove": message.emit("Remove: click a road piece (junction to junction) to tear it down")
		"depot": message.emit("Depot: click near a junction")
		_: message.emit("")


func _is_curve() -> bool:
	return tool == "curve2" or tool == "curve4"


func _road_type() -> int:
	return 1 if tool == "road4" or tool == "curve4" else 0


func _reset() -> void:
	_hover_road = -1
	_start = null
	_mid = null
	_preview.visible = false
	_arc_preview.visible = false


func _unhandled_input(event: InputEvent) -> void:
	if tool == "pointer":
		return
	if event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		if _start == null:
			set_tool("pointer")
			tool_changed.emit("pointer")
		else:
			_reset()
	elif event is InputEventMouseMotion:
		_update_preview(event.position)
	elif event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		_click(event.position)


func ground_point(screen: Vector2) -> Variant:
	var hit: Dictionary = host.RayToGround(camera.project_ray_origin(screen), camera.project_ray_normal(screen))
	if not hit["hit"]:
		return null
	return Vector3(hit["x"], hit["y"], hit["z"])


func _target_point(screen: Vector2) -> Variant:
	var p: Variant = ground_point(screen)
	if p == null:
		return null
	var snap: Dictionary = host.SnapToNode(p.x, p.z, NODE_SNAP_RADIUS)
	if snap["snapped"]:
		return Vector3(snap["x"], snap["y"], snap["z"])
	if _start != null and not _is_curve():
		var offset: Vector3 = p - _start
		offset.y = 0.0
		var angle := roundf(atan2(offset.z, offset.x) / ANGLE_STEP) * ANGLE_STEP
		var length := maxf(LENGTH_STEP * 2.0, roundf(offset.length() / LENGTH_STEP) * LENGTH_STEP)
		return _ground(_start.x + cos(angle) * length, _start.z + sin(angle) * length)
	return _ground(roundf(p.x / LENGTH_STEP) * LENGTH_STEP, roundf(p.z / LENGTH_STEP) * LENGTH_STEP)


func _ground(x: float, z: float) -> Vector3:
	return Vector3(x, host.HeightAt(x, z), z)


func _click(screen: Vector2) -> void:
	var target: Variant = _target_point(screen)
	if target == null:
		return
	if tool == "upgrade" or tool == "remove":
		_click_road(screen)
		return
	if tool == "depot":
		var level := 1
		message.emit("Depot built" if host.BuildDepot(target.x, target.z, level) else "No free junction nearby")
		return

	if _start == null:
		_start = target
		return
	if _is_curve() and _mid == null:
		_mid = target
		return
	var sweep := 0.0
	if _is_curve():
		sweep = host.SweepThrough(_start.x, _start.z, _mid.x, _mid.z, target.x, target.z)
	if host.BuildCurve(_start.x, _start.z, target.x, target.z, _road_type(), sweep):
		message.emit("Road built")
		_start = target # keep drawing from the new end
		_mid = null
		_arc_preview.visible = false
	else:
		message.emit("Can't build there")


func _hovered_road(screen: Vector2) -> Dictionary:
	var p: Variant = ground_point(screen)
	if p == null:
		return {"found": false}
	return host.RoadAt(p.x, p.z)


func _click_road(screen: Vector2) -> void:
	var hit := _hovered_road(screen)
	if not hit["found"]:
		message.emit("No road there")
		return
	var road: int = hit["road"]
	if tool == "remove":
		var cost: float = host.DemolitionCost(road)
		message.emit("Road removed (demolition %s)" % _money(cost) if host.RemoveRoad(road) else "Can't remove that")
	elif host.UpgradeRoad(road):
		message.emit("Upgraded to an avenue")
	else:
		message.emit("Only 2-lane roads can be upgraded")
	_hover_road = -1
	_arc_preview.visible = false
	_update_preview(screen)


func _hover(screen: Vector2) -> void:
	_preview.visible = false
	var hit := _hovered_road(screen)
	if not hit["found"]:
		_hover_road = -1
		_arc_preview.visible = false
		message.emit("")
		return
	_hover_road = hit["road"]
	var width := (16.0 if hit["type"] == 1 else 7.0) + 2.0
	var color: Color = ROAD_COLORS["invalid"]
	if tool == "remove":
		message.emit("Remove %d m of road: demolition %s" % [hit["length"], _money(host.DemolitionCost(_hover_road))])
	else:
		var cost: Dictionary = host.EstimateUpgrade(_hover_road)
		if cost["valid"]:
			color = ROAD_COLORS["valid"]
			message.emit("Upgrade %d m to an avenue: cut %d m3   fill %d m3   cost %s" % [hit["length"], cost["cut"], cost["fill"], _money(cost["money"])])
		else:
			message.emit("Already an avenue")
	_material.albedo_color = color
	_draw_arc(host.RoadPoints(_hover_road), width)


func _update_preview(screen: Vector2) -> void:
	if tool == "upgrade" or tool == "remove":
		_hover(screen)
		return
	var target: Variant = _target_point(screen)
	if target == null or tool == "depot":
		_preview.visible = tool == "depot" and target != null
		_arc_preview.visible = false
		if _preview.visible:
			_preview.mesh.size = Vector3(22, 6, 22)
			_preview.transform = Transform3D(Basis.IDENTITY, target + Vector3(26, 3, 26))
			_material.albedo_color = ROAD_COLORS["valid"]
		return
	if _start == null:
		_preview.visible = true
		_arc_preview.visible = false
		_preview.mesh.size = Vector3(4, 4, 4)
		_preview.transform = Transform3D(Basis.IDENTITY, target + Vector3(0, 2, 0))
		_material.albedo_color = ROAD_COLORS["valid"]
		return

	var sweep := 0.0
	var offset: Vector3 = target - _start
	if _is_curve() and _mid != null:
		sweep = host.SweepThrough(_start.x, _start.z, _mid.x, _mid.z, target.x, target.z)
	var cost: Dictionary = host.EstimateCurveCost(_start.x, _start.z, target.x, target.z, _road_type(), sweep)
	var valid: bool = cost["valid"]
	var width := 16.0 if _road_type() == 1 else 7.0
	_material.albedo_color = ROAD_COLORS["valid"] if valid else ROAD_COLORS["invalid"]

	if absf(sweep) > 0.0001:
		_preview.visible = false
		_draw_arc(host.CurvePoints(_start.x, _start.z, target.x, target.z, sweep), width)
	else:
		_arc_preview.visible = false
		_preview.visible = true
		_preview.mesh.size = Vector3(maxf(offset.length(), 0.1), 0.4, width)
		# Lies along the planned road, which may climb: x runs start to end, z stays horizontal.
		var along := offset.normalized() if offset.length() > 0.001 else Vector3.RIGHT
		var across := Vector3(-along.z, 0.0, along.x).normalized()
		_preview.transform = Transform3D(Basis(along, across.cross(along), across), (_start + target) * 0.5 + Vector3(0, 0.3, 0))

	if valid:
		message.emit("%d m   grade %.1f%%   cut %d m3   fill %d m3   cost %s" % [
			cost["length"], 100.0 * offset.y / maxf(cost["length"], 0.1), cost["cut"], cost["fill"], _money(cost["money"])])
	else:
		message.emit("Can't build there: too short, too steep (max 12%), curve too tight or too close to a junction")


## A flat strip on the ground following the planned curve.
func _draw_arc(points: PackedVector3Array, width: float) -> void:
	var mesh: ImmediateMesh = _arc_preview.mesh
	mesh.clear_surfaces()
	_arc_preview.visible = true
	if points.size() < 2:
		return
	mesh.surface_begin(Mesh.PRIMITIVE_TRIANGLE_STRIP)
	for i in points.size():
		var a := points[maxi(i - 1, 0)]
		var b := points[mini(i + 1, points.size() - 1)]
		var forward := (b - a)
		forward.y = 0.0
		forward = forward.normalized()
		var right := Vector3(-forward.z, 0.0, forward.x) * width * 0.5
		var lift := Vector3(0, 0.4, 0)
		mesh.surface_add_vertex(points[i] - right + lift)
		mesh.surface_add_vertex(points[i] + right + lift)
	mesh.surface_end()


func _money(amount: float) -> String:
	var digits := str(int(round(amount)))
	var out := ""
	for i in digits.length():
		if i > 0 and (digits.length() - i) % 3 == 0:
			out += ","
		out += digits[i]
	return out
