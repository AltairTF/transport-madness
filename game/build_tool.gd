extends Node
## Turns mouse input into build commands on the simulation host. Straight roads snap to existing nodes
## (that is how junctions are made); otherwise the road follows the mouse in any direction, pulled onto a 15 degree
## step when within 1.5 degrees of one (Shift forces the step), with 5 m lengths. Curves take three
## clicks: start, a point on the curve, end. Road tools (upgrade, downgrade, remove, speed limit) act on the road
## piece under the mouse; Move takes two clicks: the junction to pick up, then where to put it. Station builds on the
## road under the mouse (splitting it); New line takes stations one click at a time and Enter finishes the line.

const NODE_SNAP_RADIUS := 20.0
const ANGLE_STEP := deg_to_rad(15.0)
const ANGLE_PULL := deg_to_rad(1.5) ## a road within this of a 15 degree step is pulled onto it; Shift forces the step
const LENGTH_STEP := 5.0
const PICK_RADIUS := 24.0
const STATION_PICK_RADIUS := 40.0
const SPEED_STEP := 10.0 / 3.6 # one click on a speed tool changes the limit by 10 km/h
const ROAD_COLORS := {"valid": Color(0.3, 0.9, 0.4, 0.7), "invalid": Color(0.95, 0.3, 0.3, 0.7)}

signal message(text: String)
signal tool_changed(tool_name: String)

var host: Node3D
var camera: Camera3D
var tool := "pointer" # pointer, road2, road4, curve2, curve4, upgrade, downgrade, faster, slower, move, remove, depot, station, line, and the rail tools
const LIGHT_TOOLS := ["light", "light_remove"]
const SIGN_TOOLS := {"sign_priority": 0, "sign_yield": 1, "sign_stop": 2} ## the kind each sign tool puts up, as SimHost.BuildRoadSign takes it
const RAIL_TOOLS := ["track", "track_curve", "track_remove", "signal_block", "signal_path", "signal_remove", "rail_station", "rail_depot"]
const RAIL_WIDTH := 4.2
var _signal_forward := true ## signals face trains going from a track's start toward its end; R turns them round

var _start: Variant = null # Vector3 or null
var _mid: Variant = null   # Vector3 or null, curves only
var _picked_node := -1     # move tool: the node being dragged, with _start at its place
var _line_stations: Array = [] # line tool: station ids picked so far, in order
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
		"downgrade": message.emit("Downgrade: click an avenue to narrow it to a 2-lane road")
		"faster": message.emit("Faster: click a road to raise its speed limit by 10 km/h")
		"slower": message.emit("Slower: click a road to lower its speed limit by 10 km/h")
		"move": message.emit("Move: click a junction to pick it up, then click where it goes (roads keep their bend; vehicles carry on)")
		"remove": message.emit("Remove: click a road piece (junction to junction) to tear it down")
		"depot": message.emit("Depot: click near a junction")
		"station": message.emit("Station: click a road to build one there (the road is split); vehicles stop on the lanes that run into it")
		"station_bays": message.emit("Station with bays: like a station, but loading vehicles pull out of the lane, so a bus loading does not hold up the road")
		"line": message.emit("New line: click the stations in order, Enter finishes the line, Esc cancels")
		"light": message.emit("Traffic light: click a junction of three roads or more; the streets that cross take turns at green")
		"light_remove": message.emit("Remove light: click a junction with traffic lights")
		"sign_priority": message.emit("Priority road: click a road next to a junction; the road across it gets the sign too and every other road gives way to them")
		"sign_yield": message.emit("Yield sign: click a road next to a junction; its traffic gives way to the roads without a sign")
		"sign_stop": message.emit("Stop sign: click a road next to a junction; its traffic stops, then gives way to the roads without a sign")
		"sign_remove": message.emit("Remove sign: click a road next to a junction with a sign")
		"track": message.emit("Track: click to start, click again to build (Esc stops). Tracks join where they meet; a road it crosses gets a level crossing")
		"track_curve": message.emit("Curved track: click the start, a point on the curve, then the end (Esc stops)")
		"track_remove": message.emit("Remove track: click a piece of track (node to node); a train on it is lost")
		"signal_block": message.emit("Block signal: click a track (R turns the signal round). A train waits unless the whole block beyond is free")
		"signal_path": message.emit("Path signal: click a track (R turns the signal round). A train waits unless its path up to the next signal is free; use them at passing loops")
		"signal_remove": message.emit("Remove signal: click a signal")
		"rail_station": message.emit("Rail station: click a track to build one there; trains stop with their front at the node")
		"rail_depot": message.emit("Rail depot: click a track junction or end; trains are bought there for a line")
		_: message.emit("")


func _is_rail_tool() -> bool:
	return tool in RAIL_TOOLS


func _is_curve() -> bool:
	return tool == "curve2" or tool == "curve4" or tool == "track_curve"


func _road_type() -> int:
	return 1 if tool == "road4" or tool == "curve4" else 0


func _is_road_tool() -> bool:
	return tool in ["upgrade", "downgrade", "remove", "faster", "slower"]


func _reset() -> void:
	_picked_node = -1
	_line_stations.clear()
	_hover_road = -1
	_start = null
	_mid = null
	_preview.visible = false
	_arc_preview.visible = false


func _unhandled_input(event: InputEvent) -> void:
	if tool == "pointer":
		return
	if event is InputEventKey and event.pressed and (event.keycode == KEY_ENTER or event.keycode == KEY_KP_ENTER):
		if tool == "line":
			_finish_line()
	elif event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_R and tool.begins_with("signal_"):
		_signal_forward = not _signal_forward
		_update_preview(get_viewport().get_mouse_position())
	elif event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		get_viewport().set_input_as_handled()
		if _start == null and _line_stations.is_empty():
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
	var snap: Dictionary = host.SnapToRailNode(p.x, p.z, NODE_SNAP_RADIUS) if _is_rail_tool() else host.SnapToNode(p.x, p.z, NODE_SNAP_RADIUS)
	if snap["snapped"]:
		return Vector3(snap["x"], snap["y"], snap["z"])
	if _start != null and not _is_curve():
		var offset: Vector3 = p - _start
		offset.y = 0.0
		var angle := atan2(offset.z, offset.x)
		var stepped := roundf(angle / ANGLE_STEP) * ANGLE_STEP
		if Input.is_key_pressed(KEY_SHIFT) or absf(angle - stepped) <= ANGLE_PULL:
			angle = stepped
		var length := maxf(LENGTH_STEP * 2.0, roundf(offset.length() / LENGTH_STEP) * LENGTH_STEP)
		return _ground(_start.x + cos(angle) * length, _start.z + sin(angle) * length)
	return _ground(roundf(p.x / LENGTH_STEP) * LENGTH_STEP, roundf(p.z / LENGTH_STEP) * LENGTH_STEP)


func _ground(x: float, z: float) -> Vector3:
	return Vector3(x, host.HeightAt(x, z), z)


func _click(screen: Vector2) -> void:
	var target: Variant = _target_point(screen)
	if target == null:
		return
	if _is_road_tool():
		_click_road(screen)
		return
	if tool in LIGHT_TOOLS:
		_click_light(screen)
		return
	if tool in SIGN_TOOLS or tool == "sign_remove":
		_click_sign(screen)
		return
	if tool in ["track_remove", "signal_block", "signal_path", "signal_remove", "rail_station", "rail_depot"]:
		_click_rail(screen, target)
		return
	if tool == "move":
		_click_move(screen)
		return
	if tool == "station" or tool == "station_bays":
		var id: int = host.BuildStation(target.x, target.z, tool == "station_bays")
		message.emit("Station %s built" % host.StationName(id) if id >= 0 else "Can't build a station there: it needs a road with room on both sides, no station already, and enough money")
		_update_preview(screen)
		return
	if tool == "line":
		_click_line(target)
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
	var built: bool = host.BuildTrack(_start.x, _start.z, target.x, target.z, sweep) if _is_rail_tool() else host.BuildCurve(_start.x, _start.z, target.x, target.z, _road_type(), sweep)
	if built:
		message.emit("Track laid" if _is_rail_tool() else "Road built")
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
	match tool:
		"remove":
			var cost: float = host.DemolitionCost(road)
			message.emit("Road removed (demolition %s)" % _money(cost) if host.RemoveRoad(road) else "Can't remove that")
		"upgrade":
			message.emit("Upgraded to an avenue" if host.UpgradeRoad(road) else "Only 2-lane roads can be upgraded")
		"downgrade":
			message.emit("Narrowed to a 2-lane road" if host.DowngradeRoad(road) else "Only avenues can be narrowed")
		"faster", "slower":
			var delta := SPEED_STEP if tool == "faster" else -SPEED_STEP
			var limit_kmh: float = host.SteppedSpeedLimit(road, delta) * 3.6
			message.emit("Speed limit now %d km/h" % roundi(limit_kmh) if host.ChangeSpeedLimit(road, delta) else "The limit can't go any further")
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
	match tool:
		"remove":
			color = ROAD_COLORS["valid"]
			message.emit("Remove %d m of road: demolition %s" % [hit["length"], _money(host.DemolitionCost(_hover_road))])
		"upgrade":
			var cost: Dictionary = host.EstimateUpgrade(_hover_road)
			if cost["valid"]:
				color = ROAD_COLORS["valid"]
				message.emit("Upgrade %d m to an avenue: cut %d m3   fill %d m3   cost %s" % [hit["length"], cost["cut"], cost["fill"], _money(cost["money"])])
			else:
				message.emit("Already an avenue")
		"downgrade":
			var cost: Dictionary = host.EstimateDowngrade(_hover_road)
			if cost["valid"]:
				color = ROAD_COLORS["valid"]
				message.emit("Narrow %d m to a 2-lane road: cost %s" % [hit["length"], _money(cost["money"])])
			else:
				message.emit("Already a 2-lane road")
		_:
			var delta := SPEED_STEP if tool == "faster" else -SPEED_STEP
			var now_kmh: float = host.SpeedLimitOf(_hover_road) * 3.6
			var cost: Dictionary = host.EstimateSpeedLimit(_hover_road, delta)
			if cost["valid"]:
				color = ROAD_COLORS["valid"]
				message.emit("Speed limit %d to %d km/h: cost %s" % [roundi(now_kmh), roundi(cost["limit"] * 3.6), _money(cost["money"])])
			else:
				message.emit("Speed limit stays at %d km/h" % roundi(now_kmh))
	_material.albedo_color = color
	_draw_arc(host.RoadPoints(_hover_road), width)


func _update_preview(screen: Vector2) -> void:
	if tool in LIGHT_TOOLS:
		_hover_light(screen)
		return
	if tool in SIGN_TOOLS or tool == "sign_remove":
		_hover_sign(screen)
		return
	if tool in ["track_remove", "signal_block", "signal_path", "signal_remove", "rail_station", "rail_depot"]:
		_hover_rail(screen)
		return
	if _is_road_tool():
		_hover(screen)
		return
	if tool == "station" or tool == "station_bays":
		_hover_station(screen)
		return
	if tool == "line":
		_hover_line(screen)
		return
	if tool == "move":
		_hover_move(screen)
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
	var cost: Dictionary = host.EstimateTrackCost(_start.x, _start.z, target.x, target.z, sweep) if _is_rail_tool() else host.EstimateCurveCost(_start.x, _start.z, target.x, target.z, _road_type(), sweep)
	var valid: bool = cost["valid"]
	var width := RAIL_WIDTH if _is_rail_tool() else (16.0 if _road_type() == 1 else 7.0)
	_material.albedo_color = ROAD_COLORS["valid"] if valid and cost["affordable"] else ROAD_COLORS["invalid"]

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
		var text := "%d m   grade %.1f%%   cut %d m3   fill %d m3   cost %s" % [
			cost["length"], 100.0 * offset.y / maxf(cost["length"], 0.1), cost["cut"], cost["fill"], _money(cost["money"])]
		if cost["bridge"] > 0.0:
			text += "   bridge %d m" % cost["bridge"]
		if cost["tunnel"] > 0.0:
			text += "   tunnel %d m" % cost["tunnel"]
		if _is_rail_tool() and cost["crossings"] > 0:
			text += "   %d level %s" % [cost["crossings"], "crossing" if cost["crossings"] == 1 else "crossings"]
		text += _demolition_text(cost)
		message.emit(text)
	elif _is_rail_tool():
		var reason: String = cost.get("reason", "")
		if reason == "":
			reason = "too short, too steep (max 4%), curve too tight (radius 90 m), in water, too close to another junction, or it would cross a road too near its end or at too sharp an angle"
		message.emit("Can't lay track there: " + reason)
	else:
		message.emit("Can't build there: too short, too steep (max 12%), curve too tight, in water, or too close to a junction")


## Station tool: a marker where the station would stand, and what it costs.
func _hover_station(screen: Vector2) -> void:
	_arc_preview.visible = false
	var target: Variant = _target_point(screen)
	if target == null:
		_preview.visible = false
		return
	var site: Dictionary = host.StationPreview(target.x, target.z, tool == "station_bays")
	if not site["valid"]:
		_preview.visible = false
		message.emit("No room for a station here: click a road, away from its ends and from other stations")
		return
	_preview.visible = true
	_preview.mesh.size = Vector3(14, 4, 10)
	_preview.transform = Transform3D(Basis.IDENTITY, Vector3(site["x"] - 24.0, site["y"] + 2.0, site["z"] - 24.0))
	_material.albedo_color = ROAD_COLORS["valid"] if site["affordable"] else ROAD_COLORS["invalid"]
	message.emit("Station: cost %s   serves %d buildings and %d industries within %d m" % [_money(site["price"]), site["covered"], site["industries"], site["radius"]] + ("" if site["affordable"] else "   (not enough money)"))


## Line tool: the station under the mouse is added to the line being drawn.
func _click_line(target: Vector3) -> void:
	var id: int = host.StationNear(target.x, target.z, STATION_PICK_RADIUS)
	if id < 0:
		message.emit("Click a station to add it to the line")
		return
	if not _line_stations.is_empty() and _line_stations[-1] == id:
		message.emit("That station is already the last stop")
		return
	_line_stations.append(id)
	_show_line_draft()


func _finish_line() -> void:
	if _line_stations.size() < 2:
		message.emit("A line needs at least two stations")
		return
	var line: int = host.CreateLine(PackedInt32Array(_line_stations))
	if line >= 0:
		message.emit("Line %d created: buy vehicles for it in the Lines window, and rename it there" % (line + 1))
		_reset()
	else:
		message.emit("Can't make that line: the last station may not be the same as the first")


func _show_line_draft() -> void:
	var names := ""
	var points := PackedVector3Array()
	for id in _line_stations:
		names += ("" if names.is_empty() else " - ") + host.StationName(id)
		points.append(host.StationPosition(id))
	message.emit("Line: %s   (click more stations, Enter finishes, Esc cancels)" % names)
	_material.albedo_color = ROAD_COLORS["valid"]
	_draw_arc(points, 5.0)


func _hover_line(screen: Vector2) -> void:
	_preview.visible = false
	var p: Variant = ground_point(screen)
	if p == null:
		return
	var id: int = host.StationNear(p.x, p.z, STATION_PICK_RADIUS)
	if id < 0:
		return
	var at: Vector3 = host.StationPosition(id)
	_preview.visible = true
	_preview.mesh.size = Vector3(10, 10, 10)
	_preview.transform = Transform3D(Basis.IDENTITY, at + Vector3(0, 5, 0))
	_material.albedo_color = ROAD_COLORS["valid"]


## Move tool: the first click picks up the junction under the mouse, the second drops it.
func _click_move(screen: Vector2) -> void:
	var p: Variant = ground_point(screen)
	if p == null:
		return
	if _picked_node < 0:
		var found: Dictionary = host.NodeAt(p.x, p.z, PICK_RADIUS)
		if not found["found"]:
			message.emit("No junction there")
			return
		_picked_node = found["node"]
		_start = Vector3(found["x"], found["y"], found["z"])
		message.emit("Junction picked up: click where it goes (Esc cancels)")
		return
	var target := _move_target(p)
	if host.MoveNode(_picked_node, target.x, target.z):
		message.emit("Junction moved")
		_reset()
	else:
		message.emit("Can't move it there")


func _move_target(p: Vector3) -> Vector3:
	return _ground(roundf(p.x / LENGTH_STEP) * LENGTH_STEP, roundf(p.z / LENGTH_STEP) * LENGTH_STEP)


func _hover_move(screen: Vector2) -> void:
	_preview.visible = false
	var p: Variant = ground_point(screen)
	if p == null:
		return
	if _picked_node < 0:
		var found: Dictionary = host.NodeAt(p.x, p.z, PICK_RADIUS)
		_arc_preview.visible = false
		if found["found"]:
			_preview.visible = true
			_preview.mesh.size = Vector3(10, 10, 10)
			_preview.transform = Transform3D(Basis.IDENTITY, Vector3(found["x"], found["y"] + 5.0, found["z"]))
			_material.albedo_color = ROAD_COLORS["valid"]
			message.emit("Click to pick up this junction")
		else:
			message.emit("Click a junction to pick it up")
		return
	var target := _move_target(p)
	var cost: Dictionary = host.EstimateMoveNode(_picked_node, target.x, target.z)
	_material.albedo_color = ROAD_COLORS["valid"] if cost["valid"] and cost["affordable"] else ROAD_COLORS["invalid"]
	# Strips from the new place to the other end of each road that will be rebuilt, with the road's bend.
	var first := true
	for shape in host.NodeRoadShapes(_picked_node):
		var end: Vector3 = shape["end"]
		var points: PackedVector3Array
		if shape["starts_here"]:
			points = host.CurvePoints(target.x, target.z, end.x, end.z, shape["sweep"])
		else:
			points = host.CurvePoints(end.x, end.z, target.x, target.z, shape["sweep"])
		_draw_arc(points, 9.0, first)
		first = false
	if cost["valid"]:
		var text := "Move here: %d m of road rebuilt, cut %d m3   fill %d m3   cost %s" % [cost["length"], cost["cut"], cost["fill"], _money(cost["money"])]
		if cost["bridge"] > 0.0:
			text += "   bridge %d m" % cost["bridge"]
		if cost["tunnel"] > 0.0:
			text += "   tunnel %d m" % cost["tunnel"]
		text += _demolition_text(cost)
		message.emit(text)
	else:
		message.emit("Can't move it there: roads too short, too steep or too tightly curved, an end in water, or on another junction")


## A flat strip on the ground following the planned curve. Several strips can share the mesh: pass clear = false to add one.
func _draw_arc(points: PackedVector3Array, width: float, clear := true) -> void:
	var mesh: ImmediateMesh = _arc_preview.mesh
	if clear:
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


## What a road would tear down, as the end of the cost line: "   demolishes 3 buildings for 12,000", or "   (not enough money)".
func _demolition_text(cost: Dictionary) -> String:
	var text := ""
	if cost["demolished"] > 0:
		text += "   demolishes %d %s for %s" % [cost["demolished"], "building" if cost["demolished"] == 1 else "buildings", _money(cost["demolition_price"])]
	if not cost["affordable"]:
		text += "   (not enough money)"
	return text


func _money(amount: float) -> String:
	var digits := str(int(round(amount)))
	var out := ""
	for i in digits.length():
		if i > 0 and (digits.length() - i) % 3 == 0:
			out += ","
		out += digits[i]
	return out


## Rail tools that act on one place: a marker where the signal, station or depot would stand, or the track to tear down.
func _hover_rail(screen: Vector2) -> void:
	_preview.visible = false
	_arc_preview.visible = false
	var p: Variant = ground_point(screen)
	if p == null:
		return
	match tool:
		"track_remove":
			var hit: Dictionary = host.TrackAt(p.x, p.z)
			if not hit["found"]:
				message.emit("No track there")
				return
			_material.albedo_color = ROAD_COLORS["valid"]
			_draw_arc(host.TrackPoints(hit["track"]), RAIL_WIDTH + 2.0)
			message.emit("Remove %d m of track: demolition %s" % [hit["length"], _money(hit["demolition"])])
		"signal_block", "signal_path":
			var site: Dictionary = host.SignalPreview(p.x, p.z, 1 if tool == "signal_path" else 0, _signal_forward)
			if not site["valid"]:
				message.emit("Click a track to put a signal there (R turns it round)")
				return
			_preview.visible = true
			_preview.mesh.size = Vector3(1.0, 5.0, 1.0)
			_preview.transform = Transform3D(Basis(Vector3.UP, -site["heading"]), Vector3(site["x"], site["y"] + 2.5, site["z"]))
			_material.albedo_color = ROAD_COLORS["valid"] if site["affordable"] else ROAD_COLORS["invalid"]
			message.emit("%s signal: cost %s   faces trains going the way the marker points (R turns it round)" % ["Path" if tool == "signal_path" else "Block", _money(site["price"])])
		"signal_remove":
			var found: Dictionary = host.SignalAt(p.x, p.z, 16.0)
			if found["found"]:
				_preview.visible = true
				_preview.mesh.size = Vector3(5, 6, 5)
				_preview.transform = Transform3D(Basis.IDENTITY, Vector3(found["x"], found["y"] + 3.0, found["z"]))
				_material.albedo_color = ROAD_COLORS["valid"]
				message.emit("Click to take this signal down")
			else:
				message.emit("Click a signal")
		"rail_station":
			var site: Dictionary = host.RailStationPreview(p.x, p.z)
			if not site["valid"]:
				message.emit("No room for a station here: click a track, away from its ends, not on another station")
				return
			_preview.visible = true
			_preview.mesh.size = Vector3(36, 1, 4)
			_preview.transform = Transform3D(Basis.IDENTITY, Vector3(site["x"], site["y"] + 0.6, site["z"]))
			_material.albedo_color = ROAD_COLORS["valid"] if site["affordable"] else ROAD_COLORS["invalid"]
			message.emit("Rail station: cost %s   serves %d buildings and %d industries within %d m" % [_money(site["price"]), site["covered"], site["industries"], site["radius"]] + ("" if site["affordable"] else "   (not enough money)"))
		"rail_depot":
			var node: Dictionary = host.SnapToRailNode(p.x, p.z, 16.0)
			if node["snapped"]:
				_preview.visible = true
				_preview.mesh.size = Vector3(16, 5, 12)
				_preview.transform = Transform3D(Basis.IDENTITY, Vector3(node["x"] + 16.0, node["y"] + 2.5, node["z"] + 16.0))
				_material.albedo_color = ROAD_COLORS["valid"]
				message.emit("Click to build a rail depot here")
			else:
				message.emit("Click a track junction or end")


func _click_rail(screen: Vector2, _target: Vector3) -> void:
	var p: Variant = ground_point(screen)
	if p == null:
		return
	match tool:
		"track_remove":
			var hit: Dictionary = host.TrackAt(p.x, p.z)
			if not hit["found"]:
				message.emit("No track there")
				return
			message.emit("Track removed (demolition %s)" % _money(hit["demolition"]) if host.RemoveTrack(hit["track"]) else "Can't remove that")
		"signal_block", "signal_path":
			var kind := 1 if tool == "signal_path" else 0
			message.emit("Signal built" if host.BuildSignal(p.x, p.z, kind, _signal_forward) else "Can't put a signal there: click a track, with enough money")
		"signal_remove":
			var found: Dictionary = host.SignalAt(p.x, p.z, 16.0)
			message.emit("Signal taken down" if found["found"] and host.RemoveSignal(found["signal"]) else "No signal there")
		"rail_station":
			var id: int = host.BuildRailStation(p.x, p.z)
			message.emit("Station %s built" % host.StationName(id) if id >= 0 else "Can't build a station there: it needs a track with room on both sides, no station already, and enough money")
		"rail_depot":
			message.emit("Rail depot built" if host.BuildRailDepot(p.x, p.z) else "No free track junction or end nearby")
	_hover_rail(screen)


## Traffic light tools: a marker on the junction under the mouse.
func _hover_light(screen: Vector2) -> void:
	_preview.visible = false
	_arc_preview.visible = false
	var p: Variant = ground_point(screen)
	if p == null:
		return
	if tool == "light":
		var site: Dictionary = host.LightPreview(p.x, p.z)
		if not site["valid"]:
			message.emit("Click a junction of three roads or more without lights")
			return
		_preview.visible = true
		_preview.mesh.size = Vector3(8, 6, 8)
		_preview.transform = Transform3D(Basis.IDENTITY, Vector3(site["x"], site["y"] + 3.0, site["z"]))
		_material.albedo_color = ROAD_COLORS["valid"] if site["affordable"] else ROAD_COLORS["invalid"]
		message.emit("Traffic lights: cost %s" % _money(site["price"]) + ("" if site["affordable"] else "   (not enough money)"))
	else:
		var found: Dictionary = host.LightAt(p.x, p.z)
		if found["found"]:
			_preview.visible = true
			_preview.mesh.size = Vector3(8, 6, 8)
			_preview.transform = Transform3D(Basis.IDENTITY, Vector3(found["x"], found["y"] + 3.0, found["z"]))
			_material.albedo_color = ROAD_COLORS["valid"]
			message.emit("Click to take the lights down")
		else:
			message.emit("Click a junction with traffic lights")


func _click_light(screen: Vector2) -> void:
	var p: Variant = ground_point(screen)
	if p == null:
		return
	if tool == "light":
		message.emit("Traffic lights built" if host.BuildTrafficLight(p.x, p.z) else "Can't put lights there: it needs a junction of three roads or more, without lights, and enough money")
	else:
		message.emit("Lights taken down" if host.RemoveTrafficLight(p.x, p.z) else "No lights there")
	_hover_light(screen)


## Sign tools: a marker on the post of the sign for the approach under the mouse.
func _hover_sign(screen: Vector2) -> void:
	_preview.visible = false
	_arc_preview.visible = false
	var p: Variant = ground_point(screen)
	if p == null:
		return
	if tool in SIGN_TOOLS:
		var site: Dictionary = host.SignPreview(p.x, p.z, SIGN_TOOLS[tool])
		if not site["valid"]:
			message.emit("Click a road next to a junction of three roads or more, without lights and without this sign")
			return
		_preview.visible = true
		_preview.mesh.size = Vector3(1.6, 4.0, 1.6)
		_preview.transform = Transform3D(Basis.IDENTITY, Vector3(site["x"], site["y"] + 2.0, site["z"]))
		_material.albedo_color = ROAD_COLORS["valid"] if site["affordable"] else ROAD_COLORS["invalid"]
		message.emit("Sign: cost %s" % _money(site["price"]) + ("" if site["affordable"] else "   (not enough money)"))
	else:
		var found: Dictionary = host.SignAt(p.x, p.z)
		if found["found"]:
			_preview.visible = true
			_preview.mesh.size = Vector3(1.6, 4.0, 1.6)
			_preview.transform = Transform3D(Basis.IDENTITY, Vector3(found["x"], found["y"] + 2.0, found["z"]))
			_material.albedo_color = ROAD_COLORS["valid"]
			message.emit("Click to take the sign down")
		else:
			message.emit("Click a road next to a junction with a sign")


func _click_sign(screen: Vector2) -> void:
	var p: Variant = ground_point(screen)
	if p == null:
		return
	if tool in SIGN_TOOLS:
		message.emit("Sign put up" if host.BuildRoadSign(p.x, p.z, SIGN_TOOLS[tool]) else "Can't put a sign there: it needs a road next to a junction of three roads or more, without lights or the same sign, and enough money")
	else:
		message.emit("Sign taken down" if host.RemoveRoadSign(p.x, p.z) else "No sign there")
	_hover_sign(screen)
