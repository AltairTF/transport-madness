extends Node
## What is under the mouse (M6d): a left click with no tool in hand (and no drag) finds the nearest vehicle, station, depot,
## industry, gateway or town at the point of the ground it hits, and asks the HUD to open its window. It reads the positions the
## HUD already has from the host; snapping to the road and rail networks, which the tools need, stays in the host (SimHostPicking.cs).

signal picked(kind: String, id: Variant)

const DRAG := 6.0 ## pixels the mouse may move between press and release for it to be a click
## Kinds in the order they win when two are near, with the radius (in metres, at a camera 400 m away) a click may miss them by.
const KINDS := [["vehicle", 10.0], ["station", 22.0], ["depot", 26.0], ["industry", 40.0], ["gateway", 40.0], ["town", 260.0]]

var host: Node3D
var camera: Camera3D
var hud ## the HUD (hud.gd)
var build_tool: Node
var rig: Node3D
var _press_at := Vector2.INF


func setup(owner_host: Node3D, owner_camera: Camera3D, owner_hud, tool: Node, camera_rig: Node3D) -> void:
	host = owner_host
	camera = owner_camera
	hud = owner_hud
	build_tool = tool
	rig = camera_rig


func _unhandled_input(event: InputEvent) -> void:
	if build_tool == null or build_tool.tool != "pointer":
		return
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		if event.pressed:
			_press_at = event.position
		elif _press_at != Vector2.INF and _press_at.distance_to(event.position) < DRAG:
			_press_at = Vector2.INF
			var found := pick_screen(event.position)
			if not found.is_empty():
				picked.emit(found["kind"], found["id"])


## The object under a point of the screen: {kind, id}, empty when there is none.
func pick_screen(screen: Vector2) -> Dictionary:
	var hit: Dictionary = host.RayToGround(camera.project_ray_origin(screen), camera.project_ray_normal(screen))
	if not hit["hit"]:
		return {}
	return pick_at(hit["x"], hit["z"], maxf(1.0, rig.distance / 400.0))


## The nearest object of the first kind (in KINDS order) that has one within its radius of (x, z), radii times <scale>.
func pick_at(x: float, z: float, scale: float) -> Dictionary:
	for entry in KINDS:
		var kind: String = entry[0]
		var radius: float = entry[1] * scale
		var best_id: Variant = null
		var best := radius * radius
		for item in hud.latest(kind):
			for p in _points(kind, item):
				var d: float = (p.x - x) * (p.x - x) + (p.y - z) * (p.y - z)
				if d <= best:
					best = d
					best_id = item["id"] if kind != "depot" else "%s%d" % ["r" if item["rail"] else "d", item["id"]]
		if best_id != null:
			return {"kind": kind, "id": best_id}
	return {}


## Where an object can be clicked: its point, and for a station or a depot also the box beside its node.
func _points(kind: String, item: Dictionary) -> Array[Vector2]:
	var p := Vector2(item["x"], item["z"])
	match kind:
		"station":
			return [p, p + Vector2(-24, -24)]
		"depot":
			return [p, p + Vector2(26, 26)]
	return [p]
