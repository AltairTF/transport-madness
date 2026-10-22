extends Node
## Turns mouse input into build commands on the simulation host. Straight roads snap to existing nodes
## (that is how junctions are made); otherwise the road follows the mouse in any direction, pulled onto a 15 degree
## step when within 1.5 degrees of one (Shift forces the step), with 5 m lengths. A road is built exactly where it is put: an industry or gateway
## within reach that no road reaches yet gets a stone path from the nearest point of the road (the preview shows it, the price includes it, and
## it is red when it cannot be built). The preview also marks where the road fills (green) or cuts (orange) the ground and where it is a bridge (blue)
## or a tunnel (purple), from the same plan the price uses. Curves take three
## clicks: start, a point on the curve, end. Road tools (upgrade, downgrade, remove, speed limit) act on the road
## piece under the mouse; Move takes two clicks: the junction to pick up, then where to put it. Station builds on the
## road under the mouse (splitting it); New line takes stations one click at a time and Enter finishes the line.

const NODE_SNAP_RADIUS := 20.0
const ANGLE_STEP := deg_to_rad(15.0)
const ANGLE_PULL := deg_to_rad(1.5) ## a road within this of a 15 degree step is pulled onto it; Shift forces the step
const LENGTH_STEP := 5.0
const STATION_PREVIEW_SIZE := 12.0
const RIGHT_CLICK_SLOP := 6.0 ## pixels the mouse may move with the right button down and still count as a click
const PICK_RADIUS := 24.0
const STATION_PICK_RADIUS := 40.0
const SPEED_STEP := 10.0 / 3.6 # one click on a speed tool changes the limit by 10 km/h
const ROAD_COLORS := {"valid": Color(0.3, 0.9, 0.4, 0.7), "invalid": Color(0.95, 0.3, 0.3, 0.7)}
const FILL_COLOR := Color(0.3, 0.85, 0.3)     ## embankment: the road is above the ground
const CUT_COLOR := Color(1.0, 0.55, 0.15)     ## cutting: the road is below the ground
const BRIDGE_COLOR := Color(0.35, 0.6, 1.0, 0.85)
const TUNNEL_COLOR := Color(0.7, 0.45, 1.0, 0.85)
const PATH_COLOR := Color(0.95, 0.78, 0.45, 0.85)   ## a stone path to an access point
const PATH_BLOCKED_COLOR := Color(0.95, 0.25, 0.25, 0.85)
const PATH_WIDTH := 5.0
const EARTH_FULL_AT := 2.0 ## metres of fill or cut at which the earthwork strip is at full strength

signal message(text: String)
signal tool_changed(tool_name: String)

var host: Node3D
var camera: Camera3D
const Keys := preload("res://game/keys.gd")

var tool := "pointer" # pointer, road2, road4, curve2, curve4, upgrade, downgrade, faster, slower, move, remove, depot, station, line, battery, and the rail tools
const LIGHT_TOOLS := ["light", "light_remove"]
const SIGN_TOOLS := {"sign_priority": 0, "sign_yield": 1, "sign_stop": 2} ## the kind each sign tool puts up, as SimHost.BuildRoadSign takes it
const RAIL_TOOLS := ["track", "track_curve", "track_remove", "signal_block", "signal_path", "signal_remove", "rail_station", "rail_depot"]
const RAIL_WIDTH := 4.2
const RANGE_SEGMENTS := 96 ## the circle of a battery's range is drawn with this many segments
const RANGE_WIDTH := 3.0
const PLACE_TOOLS := ["depot", "battery"] ## buildings put on the ground that can be turned and raised
const TURN_STEP := deg_to_rad(15.0)
var _turned := false      ## the player has turned the building, so it stands exactly where put, at _angle
var _angle := 0.0         ## radians the building is turned (the way its door faces)
var _raise := 0.0         ## metres the floor is lifted above the default height of its pad
var _site_angle := 0.0    ## the angle the last preview used (the place's own while the player has not turned it)
var _signal_forward := true ## signals face trains going from a track's start toward its end; R turns them round

var _start: Variant = null # Vector3 or null
var _mid: Variant = null   # Vector3 or null, curves only
var _picked_node := -1     # move tool: the node being dragged, with _start at its place
var _line_stations: Array[Dictionary] = [] # line tool: the stops picked so far, in order, each as host.PickStop gives it
var _preview: MeshInstance3D
var _arc_preview: MeshInstance3D
var _earth_preview: MeshInstance3D ## child of _arc_preview (shown with it): earthworks, structures and paths, in vertex colours
var _hover_road := -1
var _right_down_at := Vector2(-1.0, -1.0) ## where the right button went down, (-1, -1) once it has moved far enough to be a camera drag
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
	var earth_material := StandardMaterial3D.new()
	earth_material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	earth_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	earth_material.vertex_color_use_as_albedo = true
	earth_material.cull_mode = BaseMaterial3D.CULL_DISABLED
	_earth_preview = MeshInstance3D.new()
	_earth_preview.mesh = ImmediateMesh.new()
	_earth_preview.material_override = earth_material
	_arc_preview.add_child(_earth_preview)


func set_tool(tool_name: String) -> void:
	tool = tool_name
	_turned = false
	_raise = 0.0
	_reset()
	match tool:
		"road2": message.emit("2-lane road: click to start, click again to build (right click or Esc stops)")
		"road4": message.emit("2x2 avenue: click to start, click again to build (right click or Esc stops)")
		"curve2": message.emit("Curved 2-lane road: click the start, a point on the curve, then the end (right click or Esc stops)")
		"curve4": message.emit("Curved avenue: click the start, a point on the curve, then the end (right click or Esc stops)")
		"upgrade": message.emit("Upgrade: click a 2-lane road to widen it to an avenue")
		"downgrade": message.emit("Downgrade: click an avenue to narrow it to a 2-lane road")
		"faster": message.emit("Faster: click a road to raise its speed limit by 10 km/h")
		"slower": message.emit("Slower: click a road to lower its speed limit by 10 km/h")
		"move": message.emit("Move: click a junction to pick it up, then click where it goes (roads keep their bend; vehicles carry on)")
		"resurface": message.emit("Resurface: click a worn road piece; a crew restores it (the road is closed while they work)")
		"remove": message.emit("Remove: click a road piece (junction to junction) to tear it down")
		"depot": message.emit("Depot: click anywhere. Against a road it stands beside it, with a road within about 74 m it gets a stone path to it, else it waits until a road reaches its door (raise its level in its window). R turns it, Page Up and Page Down raise and lower its floor, Alt (or turning it) puts it exactly where the mouse is. The ground under it is levelled for a price")
		"station": message.emit("Station: click a road to build one there (the road is split); vehicles stop on the lanes that run into it")
		"station_bays": message.emit("Station with bays: like a station, but loading vehicles pull out of the lane, so a bus loading does not hold up the road")
		"line": message.emit("New line: click stations, or industries and gateways a road reaches, in order. The preview shows the route the vehicles would drive. Enter finishes the line, right click or Esc cancels")
		"battery": message.emit("Anti-aircraft battery: click level dry ground away from roads and buildings; the circle is its range. It needs people to man it and ammunition from a gateway (from month 20)")
		"light": message.emit("Traffic light: click a junction of three roads or more; the streets that cross take turns at green")
		"light_remove": message.emit("Remove light: click a junction with traffic lights")
		"sign_priority": message.emit("Priority road: click a road next to a junction; the road across it gets the sign too and every other road gives way to them")
		"sign_yield": message.emit("Yield sign: click a road next to a junction; its traffic gives way to the roads without a sign")
		"sign_stop": message.emit("Stop sign: click a road next to a junction; its traffic stops, then gives way to the roads without a sign")
		"sign_remove": message.emit("Remove sign: click a road next to a junction with a sign")
		"track": message.emit("Track: click to start, click again to build (right click or Esc stops). Tracks join where they meet; a road it crosses gets a level crossing")
		"track_curve": message.emit("Curved track: click the start, a point on the curve, then the end (right click or Esc stops)")
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
	return tool in ["upgrade", "downgrade", "remove", "faster", "slower", "resurface"]


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
	if event is InputEventKey and event.pressed and (Keys.pressed(event, "finish_line") or event.keycode == KEY_KP_ENTER):
		if tool == "line":
			_finish_line()
	elif event is InputEventKey and event.pressed and not event.echo and tool in PLACE_TOOLS and _place_key(event):
		get_viewport().set_input_as_handled()
		_update_preview(get_viewport().get_mouse_position())
	elif event is InputEventKey and event.pressed and not event.echo and Keys.pressed(event, "rotate_signal") and tool.begins_with("signal_"):
		_signal_forward = not _signal_forward
		_update_preview(get_viewport().get_mouse_position())
	elif event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		get_viewport().set_input_as_handled()
		_cancel()
	elif event is InputEventMouseMotion:
		if _right_down_at.x >= 0.0 and event.position.distance_to(_right_down_at) > RIGHT_CLICK_SLOP:
			_right_down_at = Vector2(-1.0, -1.0) # the button is orbiting the camera, not cancelling
		_update_preview(event.position)
	elif event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_RIGHT:
		# A click of the right button cancels; a drag of it orbits the camera and is left alone.
		if event.pressed:
			_right_down_at = event.position
		elif _right_down_at.x >= 0.0:
			_right_down_at = Vector2(-1.0, -1.0)
			_cancel()
	elif event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		_click(event.position)


## R and Shift+R turn a building by a step (the first turn starts from the angle the place gave it and makes it stand exactly where put), Page Up and Page Down lift
## and lower its floor. True when the key was one of them.
func _place_key(event: InputEvent) -> bool:
	var turn := 0.0
	if Keys.pressed(event, "rotate_signal"):
		turn = TURN_STEP
	elif Keys.pressed(event, "rotate_back"):
		turn = -TURN_STEP
	if turn != 0.0:
		if not _turned:
			_angle = roundf(_site_angle / TURN_STEP) * TURN_STEP
			_turned = true
		_angle = wrapf(_angle + turn, -PI, PI)
		return true
	var step: float = host.PadRaiseStep()
	if Keys.pressed(event, "raise_building"):
		_raise += step
		return true
	if Keys.pressed(event, "lower_building"):
		_raise -= step
		return true
	return false


## The building is put exactly where the mouse is (not drawn against a road) once it has been turned or the key to place freely is held.
func _free_placement() -> bool:
	return _turned or Input.is_action_pressed("place_free")


func _wanted_angle() -> float:
	return _angle if _turned else NAN


## " pad: ..." for the status line: what levelling the ground under the building costs, what it cuts and fills, and the raise in hand.
func _pad_text(site: Dictionary) -> String:
	var text := "   floor %+.1f m" % _raise if absf(_raise) > 0.01 else ""
	if site["pad_cut"] + site["pad_fill"] > 1.0:
		text += "   levelling %s (cut %d m3 up to %.1f m, fill %d m3 up to %.1f m)" % [_money(site["pad_money"]), roundi(site["pad_cut"]), site["pad_max_cut"], roundi(site["pad_fill"]), site["pad_max_fill"]]
	return text + "   [R turns, PgUp/PgDn raise, Alt places freely]"


## Esc and a click of the right button: drop the draft in progress (a road, a line), else leave the tool.
func _cancel() -> void:
	if _start == null and _line_stations.is_empty():
		set_tool("pointer")
		tool_changed.emit("pointer")
	else:
		_reset()


func ground_point(screen: Vector2) -> Variant:
	var hit: Dictionary = host.RayToGround(camera.project_ray_origin(screen), camera.project_ray_normal(screen))
	if not hit["hit"]:
		return null
	return Vector3(hit["x"], hit["y"], hit["z"])


func _target_point(screen: Vector2) -> Variant:
	var p: Variant = ground_point(screen)
	if p == null:
		return null
	if Input.is_action_pressed("place_free"):
		return _ground(p.x, p.z) # held: no snap to a node, a step or a length (the buildings read it too)
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
	if tool == "depot":
		_click_depot(screen)
		return
	if tool == "line":
		_click_line(screen)
		return
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
	if tool == "battery":
		_click_battery(screen)
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
	var join: Dictionary = {} # the access points the road gets a path to (M6e); the road itself is where it was put
	if not _is_rail_tool():
		join = host.RoadJoin(_start.x, _start.z, target.x, target.z, _road_type(), sweep)
	var built: bool = host.BuildTrack(_start.x, _start.z, target.x, target.z, sweep) if _is_rail_tool() else host.BuildCurve(_start.x, _start.z, target.x, target.z, _road_type(), sweep)
	if built:
		message.emit("Track laid" if _is_rail_tool() else "Road built" + _join_text(join))
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
		"resurface":
			var why: String = host.Resurface(road)
			message.emit("Resurfacing started: the road is closed until the crew is done" if why == "" else "Can't resurface it: " + why)
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
		"resurface":
			var info: Dictionary = host.ResurfaceInfo(_hover_road)
			if info["why"] == "":
				color = ROAD_COLORS["valid"]
				message.emit("Condition %d of 100. Resurface %d m: %s, closed for %d s" % [roundi(info["condition"]), hit["length"], _money(info["price"]), roundi(info["seconds"])])
			else:
				message.emit("Condition %d of 100: %s" % [roundi(info["condition"]), info["why"]])
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
	if tool == "battery":
		_hover_battery(screen)
		return
	if tool == "line":
		_hover_line(screen)
		return
	if tool == "move":
		_hover_move(screen)
		return
	if tool == "depot":
		_hover_depot(screen)
		return
	var target: Variant = _target_point(screen)
	if target == null:
		_preview.visible = false
		_arc_preview.visible = false
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
	var join: Dictionary = {} # the paths the simulation will add to the access points near the road (M6e)
	var origin: Vector3 = _start
	var runs: Array = []
	if not _is_rail_tool():
		join = host.RoadJoin(_start.x, _start.z, target.x, target.z, _road_type(), sweep)
		runs = host.RoadRuns(_start.x, _start.z, target.x, target.z, sweep, _road_type())
	var cost: Dictionary = host.EstimateTrackCost(_start.x, _start.z, target.x, target.z, sweep) if _is_rail_tool() else host.EstimateCurveCost(_start.x, _start.z, target.x, target.z, _road_type(), sweep)
	var valid: bool = cost["valid"]
	var width := RAIL_WIDTH if _is_rail_tool() else (16.0 if _road_type() == 1 else 7.0)
	_material.albedo_color = ROAD_COLORS["valid"] if valid and cost["affordable"] else ROAD_COLORS["invalid"]

	var spurs: Array = [] if join.is_empty() else join["spurs"]
	if not runs.is_empty():
		_preview.visible = false
		_draw_runs(runs, width)
		for spur in spurs:
			_draw_spur(spur)
	elif absf(sweep) > 0.0001:
		_preview.visible = false
		_draw_arc(host.CurvePoints(origin.x, origin.z, target.x, target.z, sweep), width)
	else:
		_arc_preview.visible = false
		_preview.visible = true
		_preview.mesh.size = Vector3(maxf(offset.length(), 0.1), 0.4, width)
		# Lies along the planned road, which may climb: x runs start to end, z stays horizontal.
		var along := offset.normalized() if offset.length() > 0.001 else Vector3.RIGHT
		var across := Vector3(-along.z, 0.0, along.x).normalized()
		_preview.transform = Transform3D(Basis(along, across.cross(along), across), (origin + target) * 0.5 + Vector3(0, 0.3, 0))

	if valid:
		var text := "%d m   grade %.1f%%   cut %d m3   fill %d m3   cost %s" % [
			cost["length"], 100.0 * offset.y / maxf(cost["length"], 0.1), cost["cut"], cost["fill"], _money(cost["money"])]
		if cost["bridge"] > 0.0:
			text += "   bridge %d m" % cost["bridge"]
		if cost["tunnel"] > 0.0:
			text += "   tunnel %d m" % cost["tunnel"]
		if not _is_rail_tool():
			if cost["max_fill"] >= 0.5:
				text += "   embankment up to %.1f m" % cost["max_fill"]
			if cost["max_cut"] >= 0.5:
				text += "   cutting up to %.1f m" % cost["max_cut"]
		if _is_rail_tool() and cost["crossings"] > 0:
			text += "   %d level %s" % [cost["crossings"], "crossing" if cost["crossings"] == 1 else "crossings"]
		text += _join_text(join, cost)
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
	_preview.mesh.size = Vector3(STATION_PREVIEW_SIZE, 0.6, STATION_PREVIEW_SIZE) # a slab on the road at the node; the stops show on the lanes once it is built
	_preview.transform = Transform3D(Basis.IDENTITY, Vector3(site["x"], site["y"] + 0.4, site["z"]))
	_material.albedo_color = ROAD_COLORS["valid"] if site["affordable"] else ROAD_COLORS["invalid"]
	message.emit("Station: cost %s   serves %d buildings and %d industries within %d m" % [_money(site["price"]), site["covered"], site["industries"], site["radius"]] + ("" if site["affordable"] else "   (not enough money)"))


## Line tool: the station under the mouse is added to the line being drawn.
const LINE_COLOR := Color(0.3, 0.8, 1.0, 0.85)       ## a leg of a line the vehicles can drive
const LINE_BLOCKED_COLOR := Color(0.95, 0.25, 0.25, 0.85) ## a leg with no road route
const LINE_BACK_COLOR := Color(0.3, 0.8, 1.0, 0.4)   ## the leg back from the last stop to the first
const LINE_WIDTH := 4.0
const LINE_PLACE_MARGIN := 30.0 ## how far from the square of an industry or gateway a click still picks it as a stop


func _click_line(screen: Vector2) -> void:
	var p: Variant = ground_point(screen)
	if p == null:
		return
	var stop: Dictionary = host.PickStop(p.x, p.z, STATION_PICK_RADIUS, LINE_PLACE_MARGIN)
	if not stop["found"]:
		message.emit("Click a station, or an industry or gateway a road reaches, to add it to the line")
		return
	if not stop["ready"]:
		message.emit("No road reaches the %s yet: build a road within 60 m of it first (it gets a stone path)" % stop["name"])
		return
	if not _line_stations.is_empty() and _line_stations[-1]["kind"] == stop["kind"] and _line_stations[-1]["id"] == stop["id"]:
		message.emit("That is already the last stop")
		return
	_line_stations.append(stop)
	_refresh_line_preview({})


func _finish_line() -> void:
	if _line_stations.size() < 2:
		message.emit("A line needs at least two stops")
		return
	var line: int = host.CreateLineFromStops(_line_stations)
	if line >= 0:
		message.emit("Line %d created: buy vehicles for it in the Lines window, and rename it and set what it loads and unloads at each stop there" % (line + 1))
		_reset()
	else:
		message.emit("Can't make that line: the last stop may not be the same as the first")


## Draws the legs between the stops picked so far (and the one under the mouse, when given) as the vehicles would drive them, blue where a road route runs and red where
## none does, and says what the line is: its stops, and for each leg its length, time and the cargo it would carry.
func _refresh_line_preview(hover: Dictionary) -> void:
	var stops: Array[Dictionary] = _line_stations.duplicate()
	if not hover.is_empty():
		stops.append(hover)
	_arc_preview.mesh.clear_surfaces()
	_earth_preview.mesh.clear_surfaces()
	_arc_preview.visible = true
	var names := ""
	var legs := ""
	var meters := 0.0
	var seconds := 0.0
	var blocked := false
	for i in stops.size():
		names += ("" if i == 0 else " - ") + String(stops[i]["name"])
		if i == 0:
			continue
		var leg: Dictionary = host.LineLeg(stops[i - 1]["kind"], stops[i - 1]["id"], stops[i]["kind"], stops[i]["id"])
		if not leg["valid"]:
			continue
		_draw_ribbon(leg["points"], LINE_WIDTH, LINE_COLOR if leg["reachable"] else LINE_BLOCKED_COLOR)
		meters += leg["length"]
		if leg["reachable"]:
			seconds += leg["seconds"]
			var cargo: Array = leg["cargo"]
			legs += "\n%s to %s: %d m, %d s%s" % [stops[i - 1]["name"], stops[i]["name"], leg["length"], ceili(leg["seconds"]),
				(", carries " + ", ".join(cargo).to_lower()) if not cargo.is_empty() else ", carries nothing it makes and the next takes"]
		else:
			blocked = true
			legs += "\n%s to %s: no road connection" % [stops[i - 1]["name"], stops[i]["name"]]
	if stops.size() >= 2 and not (stops[-1]["kind"] == stops[0]["kind"] and stops[-1]["id"] == stops[0]["id"]):
		# The line goes back from the last stop to the first, and over that leg too its vehicles carry what the stop makes.
		var back: Dictionary = host.LineLeg(stops[-1]["kind"], stops[-1]["id"], stops[0]["kind"], stops[0]["id"])
		if back["valid"]:
			if stops.size() >= 3:
				_draw_ribbon(back["points"], LINE_WIDTH * 0.6, LINE_BACK_COLOR if back["reachable"] else LINE_BLOCKED_COLOR)
			meters += back["length"]
			if back["reachable"]:
				seconds += back["seconds"]
				var cargo: Array = back["cargo"]
				legs += "\n%s back to %s: %d m, %d s%s" % [stops[-1]["name"], stops[0]["name"], back["length"], ceili(back["seconds"]),
					(", carries " + ", ".join(cargo).to_lower()) if not cargo.is_empty() else ", carries nothing it makes and the next takes"]
			else:
				blocked = true
				legs += "\n%s back to %s: no road connection" % [stops[-1]["name"], stops[0]["name"]]
	var text := "Line: %s" % names
	if stops.size() >= 2:
		text += "   %.1f km round trip" % (meters / 1000.0)
		if not blocked:
			text += ", about %d s" % ceili(seconds)
	text += "   (click more stops, Enter finishes, Esc cancels)" + legs
	message.emit(text)


## A flat ribbon on the ground in one colour, added to what the preview already shows (the earthwork mesh, with vertex colours).
func _draw_ribbon(points: PackedVector3Array, width: float, color: Color) -> void:
	if points.size() < 2:
		return
	var mesh: ImmediateMesh = _earth_preview.mesh
	mesh.surface_begin(Mesh.PRIMITIVE_TRIANGLE_STRIP)
	for i in points.size():
		var side := _side(points, i) * width * 0.5
		mesh.surface_set_color(color)
		mesh.surface_add_vertex(points[i] - side + Vector3(0, 0.6, 0))
		mesh.surface_set_color(color)
		mesh.surface_add_vertex(points[i] + side + Vector3(0, 0.6, 0))
	mesh.surface_end()


func _hover_line(screen: Vector2) -> void:
	_preview.visible = false
	var p: Variant = ground_point(screen)
	if p == null:
		return
	var stop: Dictionary = host.PickStop(p.x, p.z, STATION_PICK_RADIUS, LINE_PLACE_MARGIN)
	if not stop["found"] or not stop["ready"]:
		if not _line_stations.is_empty():
			_refresh_line_preview({})
		elif stop["found"]:
			message.emit("No road reaches the %s yet: build a road within 60 m of it first (it gets a stone path)" % stop["name"])
		return
	var same: bool = not _line_stations.is_empty() and _line_stations[-1]["kind"] == stop["kind"] and _line_stations[-1]["id"] == stop["id"]
	if not _line_stations.is_empty():
		_refresh_line_preview({} if same else stop)
	_preview.visible = true
	_preview.mesh.size = Vector3(10, 10, 10)
	_preview.transform = Transform3D(Basis.IDENTITY, Vector3(stop["x"], stop["y"] + 5.0, stop["z"]))
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
		_earth_preview.mesh.clear_surfaces()
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


## Depot tool (M6f): the box where the depot would stand (door toward its access point), the stone path that would join it to a road, and what it costs.
const DEPOT_ALONE_COLOR := Color(1.0, 0.8, 0.3, 0.7)


func _hover_depot(screen: Vector2) -> void:
	var p: Variant = ground_point(screen)
	if p == null:
		_preview.visible = false
		_arc_preview.visible = false
		return
	var site: Dictionary = host.DepotPreview(p.x, p.z, 0, _free_placement(), _wanted_angle(), _raise)
	_site_angle = site["angle"]
	_arc_preview.visible = false
	var size: Vector3 = site["size"]
	_preview.visible = true
	_preview.mesh.size = size
	_preview.transform = Transform3D(Basis(Vector3.UP, -float(site["angle"])), Vector3(site["x"], site["y"] + size.y * 0.5, site["z"]))
	if not site["valid"]:
		_material.albedo_color = ROAD_COLORS["invalid"]
		message.emit("Can't build a depot there: %s" % site["why"])
		return
	var connection: int = site["connection"]
	var runs: Array = site["path_runs"]
	if not runs.is_empty():
		_draw_runs(runs, PATH_WIDTH)
	elif site["path_points"].size() >= 2:
		_draw_arc(site["path_points"], PATH_WIDTH)
	var total: float = site["price"] + site["path_money"] + site["pad_money"]
	var ok: bool = site["affordable"]
	_material.albedo_color = ROAD_COLORS["invalid"] if not ok else (DEPOT_ALONE_COLOR if connection == 0 or (connection == 2 and not site["path_buildable"]) else ROAD_COLORS["valid"])
	var text := "Depot: %s" % _money(total)
	match connection:
		1:
			text += "   stands against the road (it is split there)"
		2:
			if site["path_buildable"]:
				text += "   stone path %d m (%s) joins it to the road" % [site["path_length"], _money(site["path_money"])]
			else:
				text += "   NOT connected: no path can be built to the road from here, so it waits for a road to reach its door"
		_:
			text += "   NOT connected: no road within reach, so it waits until a road is built within 60 m of its door"
	if not ok:
		text += "   (not enough money)"
	message.emit(text + _pad_text(site))


func _click_depot(screen: Vector2) -> void:
	var p: Variant = ground_point(screen)
	if p == null:
		return
	var site: Dictionary = host.DepotPreview(p.x, p.z, 0, _free_placement(), _wanted_angle(), _raise)
	if not site["valid"]:
		message.emit("Can't build a depot there: %s" % site["why"])
		return
	if host.BuildDepot(p.x, p.z, 0, _free_placement(), _wanted_angle(), _raise):
		var connected: bool = site["connection"] != 0 and (site["connection"] == 1 or site["path_buildable"])
		message.emit("Depot built" if connected else "Depot built, not connected yet: build a road within 60 m of its door")
	else:
		message.emit("Can't build the depot: not enough money")
	_hover_depot(screen)


## A planned road from the simulation's stretches (RoadRuns): the road surface in the usual colour, with the earthworks, bridges and tunnels marked.
func _draw_runs(runs: Array, width: float) -> void:
	var surface := PackedVector3Array()
	for run in runs:
		var road: PackedVector3Array = run["road"]
		for i in road.size():
			if i == 0 and not surface.is_empty():
				continue
			surface.append(road[i])
	_draw_arc(surface, width)
	var mesh: ImmediateMesh = _earth_preview.mesh
	for run in runs:
		var road: PackedVector3Array = run["road"]
		var ground: PackedVector3Array = run["ground"]
		var half: PackedFloat32Array = run["half"]
		var height: PackedFloat32Array = run["height"]
		if run["kind"] == 0:
			mesh.surface_begin(Mesh.PRIMITIVE_TRIANGLE_STRIP)
			for i in ground.size():
				var h: float = height[i]
				var color := (FILL_COLOR if h > 0.0 else CUT_COLOR)
				color.a = 0.55 * clampf(absf(h) / EARTH_FULL_AT, 0.0, 1.0)
				var side := _side(ground, i) * half[i]
				mesh.surface_set_color(color)
				mesh.surface_add_vertex(ground[i] - side + Vector3(0, 0.3, 0))
				mesh.surface_set_color(color)
				mesh.surface_add_vertex(ground[i] + side + Vector3(0, 0.3, 0))
			mesh.surface_end()
		else:
			var bridge: bool = run["kind"] == 1
			mesh.surface_begin(Mesh.PRIMITIVE_TRIANGLE_STRIP)
			for i in road.size():
				var side := _side(road, i) * (width * 0.5 + 1.5)
				var base: Vector3 = road[i] if bridge else ground[i] # a tunnel is marked on the ground above it
				var color := BRIDGE_COLOR if bridge else TUNNEL_COLOR
				mesh.surface_set_color(color)
				mesh.surface_add_vertex(base - side + Vector3(0, 0.2 if bridge else 0.5, 0))
				mesh.surface_set_color(color)
				mesh.surface_add_vertex(base + side + Vector3(0, 0.2 if bridge else 0.5, 0))
			mesh.surface_end()


## An access path (M6e) on top of what is drawn: stone coloured, and red when it cannot be built.
func _draw_spur(spur: Dictionary) -> void:
	var points := PackedVector3Array()
	var color := PATH_COLOR
	if spur["buildable"]:
		for run in spur["runs"]:
			var road: PackedVector3Array = run["road"]
			for i in road.size():
				if i == 0 and not points.is_empty():
					continue
				points.append(road[i])
	else:
		points = spur["points"]
		color = PATH_BLOCKED_COLOR
	var mesh: ImmediateMesh = _earth_preview.mesh
	mesh.surface_begin(Mesh.PRIMITIVE_TRIANGLE_STRIP)
	for i in points.size():
		var side := _side(points, i) * PATH_WIDTH * 0.5
		mesh.surface_set_color(color)
		mesh.surface_add_vertex(points[i] - side + Vector3(0, 0.5, 0))
		mesh.surface_set_color(color)
		mesh.surface_add_vertex(points[i] + side + Vector3(0, 0.5, 0))
	mesh.surface_end()


## Horizontal unit vector to the right of the line at point i.
func _side(points: PackedVector3Array, i: int) -> Vector3:
	var forward := points[mini(i + 1, points.size() - 1)] - points[maxi(i - 1, 0)]
	forward.y = 0.0
	forward = forward.normalized()
	return Vector3(-forward.z, 0.0, forward.x)


## The anti-aircraft tool (C13): a marker where the battery would stand and the circle of its range on the ground, green when it can be built there and red when not.
func _hover_battery(screen: Vector2) -> void:
	_preview.visible = false
	_arc_preview.visible = false
	var p: Variant = ground_point(screen)
	if p == null:
		return
	var site: Dictionary = host.BatteryPreview(p.x, p.z, _angle, _raise)
	_site_angle = _angle
	_material.albedo_color = ROAD_COLORS["valid"] if site["valid"] and site["affordable"] else ROAD_COLORS["invalid"]
	_preview.visible = true
	_preview.mesh.size = Vector3(site["size"] * 2.0, 6.0, site["size"] * 2.0)
	_preview.transform = Transform3D(Basis(Vector3.UP, -_angle), Vector3(p.x, site["y"] + 3.0, p.z))
	var ring := PackedVector3Array()
	for i in RANGE_SEGMENTS + 1:
		var angle := TAU * i / RANGE_SEGMENTS
		var x: float = p.x + cos(angle) * site["range"]
		var z: float = p.z + sin(angle) * site["range"]
		ring.append(Vector3(x, host.HeightAt(x, z), z))
	_draw_arc(ring, RANGE_WIDTH)
	if not site["available"]:
		message.emit("Anti-aircraft battery: the guns are available from month %d" % site["from_month"])
	elif site["why"] != "":
		message.emit("Anti-aircraft battery: %s" % site["why"])
	else:
		message.emit("Anti-aircraft battery: cost %s and %s a month   range %d m (the circle)   needs people to man it and ammunition from a gateway%s" % [_money(site["price"] + site["pad_money"]), _money(site["upkeep"]), roundi(site["range"]), _pad_text(site)])


func _click_battery(screen: Vector2) -> void:
	var target: Variant = ground_point(screen) # not snapped to a junction: a battery stands clear of the roads
	if target == null:
		return
	var site: Dictionary = host.BatteryPreview(target.x, target.z, _angle, _raise)
	if host.BuildBattery(target.x, target.z, _angle, _raise):
		message.emit("Anti-aircraft battery built: bring people to it by a road and ammunition by a station that covers it")
	elif not site["available"]:
		message.emit("Can't build one yet: the guns are available from month %d" % site["from_month"])
	else:
		message.emit("Can't build a battery there: %s" % site["why"])
	_hover_battery(screen)


## The paths a road gets to the industries and gateways near it (M6e), as part of a line of text: "   path to the Forest   (47 m, 705)". The cost, when given, tells their length and price.
func _join_text(join: Dictionary, cost := {}) -> String:
	if join.is_empty():
		return ""
	var text := ""
	for spur in join["spurs"]:
		if spur["buildable"]:
			text += "   path to the %s" % spur["name"]
		else:
			text += "   no path to the %s can be built (too steep or blocked)" % spur["name"]
	if not cost.is_empty() and cost["spurs"] > 0:
		text += " (%d m, %s)" % [cost["spur_length"], _money(cost["spur_money"])]
	return text


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
