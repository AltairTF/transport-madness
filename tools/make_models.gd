extends SceneTree
## Builds the Models that no CC0 pack had (M7d) from plain shapes in flat, dull colours, and writes them as GLB into game/assets/made/.
## Run: Godot --headless --path . --script res://tools/make_models.gd   then   Godot --headless --path . --import
## These are the game's own work (CC0, see CREDITS.md). Real metres, the front is -Z, the base stands on y = 0, one flat colour per part.
## Edit a builder below and run again; the GLB is overwritten. Keep the colours in the palette (docs/style-guide.md): earthy, dull, no pure primaries.

const OUT := "res://game/assets/made/"

const OLIVE := Color(0.30, 0.33, 0.24)
const OLIVE_DARK := Color(0.21, 0.24, 0.17)
const KHAKI := Color(0.56, 0.52, 0.38)
const SAND := Color(0.70, 0.64, 0.50)
const CONCRETE := Color(0.55, 0.54, 0.50)
const STEEL := Color(0.33, 0.35, 0.37)
const IRON := Color(0.17, 0.18, 0.19)
const BRICK := Color(0.50, 0.25, 0.20)
const RED := Color(0.52, 0.15, 0.13)
const CREAM := Color(0.82, 0.78, 0.68)
const OCHRE := Color(0.72, 0.56, 0.20)
const SLATE_BLUE := Color(0.24, 0.33, 0.46)
const WOOD := Color(0.42, 0.32, 0.22)
const GLASS := Color(0.26, 0.34, 0.38)
const CHAR := Color(0.12, 0.11, 0.10)
const RUST := Color(0.40, 0.22, 0.14)

var _materials := {}


func _initialize() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
	var builders := {
		"bomber": _bomber, "bomb": _bomb, "flakgun": _flakgun, "sandbags": _sandbags, "ammo-crate": _ammo_crate,
		"sign-stop": _sign_stop, "sign-yield": _sign_yield, "sign-priority": _sign_priority,
		"traffic-light": _traffic_light, "rail-signal": func(r: Node3D) -> void: _rail_signal(r, false), "rail-signal-path": func(r: Node3D) -> void: _rail_signal(r, true),
		"crossing-post": _crossing_post, "rail-platform": _rail_platform, "rail-canopy": _rail_canopy, "bus-stop": _bus_stop,
		"wreck": _wreck, "debris": _debris,
	}
	for name: String in builders:
		var root := Node3D.new()
		root.name = name
		builders[name].call(root)
		var document := GLTFDocument.new()
		var state := GLTFState.new()
		document.append_from_scene(root, state)
		var path := ProjectSettings.globalize_path(OUT + name + ".glb")
		var error := document.write_to_filesystem(state, path)
		print("%s: %s (%s)" % [name, "written" if error == OK else "FAILED %d" % error, path.get_file()])
		root.free()
	quit()


# ---- helpers ----------------------------------------------------------------------------------------------

func _material(color: Color) -> StandardMaterial3D:
	var key := color.to_html()
	if not _materials.has(key):
		var material := StandardMaterial3D.new()
		material.albedo_color = color
		material.roughness = 1.0
		material.resource_name = "flat_" + key
		_materials[key] = material
	return _materials[key]


func _add(root: Node3D, mesh: Mesh, position: Vector3, color: Color, rotation_degrees := Vector3.ZERO, scale := Vector3.ONE) -> MeshInstance3D:
	var instance := MeshInstance3D.new()
	instance.mesh = mesh
	mesh.surface_set_material(0, _material(color))
	instance.position = position
	instance.rotation_degrees = rotation_degrees
	instance.scale = scale
	root.add_child(instance)
	return instance


func _box(root: Node3D, size: Vector3, position: Vector3, color: Color, rotation_degrees := Vector3.ZERO) -> MeshInstance3D:
	var mesh := BoxMesh.new()
	mesh.size = size
	return _add(root, mesh, position, color, rotation_degrees)


## A cylinder along an axis ("x", "y" or "z"); a cone when the radii differ.
func _cylinder(root: Node3D, radius_top: float, radius_bottom: float, length: float, position: Vector3, color: Color, axis := "y", segments := 14) -> MeshInstance3D:
	var mesh := CylinderMesh.new()
	mesh.top_radius = radius_top
	mesh.bottom_radius = radius_bottom
	mesh.height = length
	mesh.radial_segments = segments
	mesh.rings = 1
	var turn := Vector3.ZERO
	if axis == "z":
		turn = Vector3(90, 0, 0)
	elif axis == "x":
		turn = Vector3(0, 0, 90)
	return _add(root, mesh, position, color, turn)


func _sphere(root: Node3D, radius: float, position: Vector3, color: Color, scale := Vector3.ONE) -> MeshInstance3D:
	var mesh := SphereMesh.new()
	mesh.radius = radius
	mesh.height = radius * 2.0
	mesh.radial_segments = 14
	mesh.rings = 8
	return _add(root, mesh, position, color, Vector3.ZERO, scale)


## A flat plate: a regular polygon of the given number of sides standing in the XY plane (facing +Z and -Z), its first vertex turned by `turn` degrees from +X.
func _plate(root: Node3D, radius: float, sides: int, thickness: float, position: Vector3, color: Color, turn := 0.0) -> MeshInstance3D:
	var mesh := CylinderMesh.new()
	mesh.top_radius = radius
	mesh.bottom_radius = radius
	mesh.height = thickness
	mesh.radial_segments = sides
	mesh.rings = 1
	var basis := Basis(Vector3.BACK, deg_to_rad(turn)) * Basis(Vector3.RIGHT, deg_to_rad(90))
	var instance := _add(root, mesh, position, color)
	instance.basis = basis
	return instance


## A flat triangle plate in the XY plane pointing down (a yield sign), `radius` from its middle to a corner, `thickness` along Z.
func _triangle(root: Node3D, radius: float, thickness: float, position: Vector3, color: Color) -> MeshInstance3D:
	var corners := [Vector2(0, -radius), Vector2(radius * 0.866, radius * 0.5), Vector2(-radius * 0.866, radius * 0.5)]
	var half := thickness * 0.5
	var tool := SurfaceTool.new()
	tool.begin(Mesh.PRIMITIVE_TRIANGLES)
	for z in [half, -half]:
		var order := [0, 1, 2] if z > 0 else [0, 2, 1]
		for k in order:
			tool.set_normal(Vector3(0, 0, 1 if z > 0 else -1))
			tool.add_vertex(Vector3(corners[k].x, corners[k].y, z))
	for k in 3:
		var a: Vector2 = corners[k]
		var b: Vector2 = corners[(k + 1) % 3]
		var normal := Vector3(b.y - a.y, a.x - b.x, 0).normalized()
		for vertex in [Vector3(a.x, a.y, half), Vector3(b.x, b.y, half), Vector3(b.x, b.y, -half), Vector3(a.x, a.y, half), Vector3(b.x, b.y, -half), Vector3(a.x, a.y, -half)]:
			tool.set_normal(normal)
			tool.add_vertex(vertex)
	return _add(root, tool.commit(), position, color)


# ---- the aircraft and what it drops --------------------------------------------------------------------------

func _bomber(root: Node3D) -> void:
	# A twin-engined medium bomber, 16 m long and 22 m across; the nose is -Z.
	_cylinder(root, 1.1, 1.3, 11.0, Vector3(0, 0, 0), OLIVE, "z")
	_sphere(root, 1.25, Vector3(0, 0.05, -6.0), GLASS, Vector3(1, 0.95, 1.15))
	_cylinder(root, 0.25, 1.1, 5.5, Vector3(0, 0.25, 8.0), OLIVE, "z", 10)
	_box(root, Vector3(22.0, 0.4, 3.6), Vector3(0, -0.15, -0.5), OLIVE_DARK)
	_box(root, Vector3(7.5, 0.25, 1.9), Vector3(0, 0.35, 10.0), OLIVE_DARK)
	_box(root, Vector3(0.3, 3.0, 2.2), Vector3(0, 1.9, 10.2), OLIVE, Vector3(-12, 0, 0))
	for side in [-1.0, 1.0]:
		_cylinder(root, 0.75, 0.8, 3.6, Vector3(side * 4.8, -0.1, -2.2), OLIVE, "z", 10)
		_cylinder(root, 2.1, 2.1, 0.06, Vector3(side * 4.8, -0.1, -4.1), STEEL, "z", 16)
		_cylinder(root, 0.25, 0.25, 0.3, Vector3(side * 4.8, -0.1, -4.0), IRON, "z", 8)
	_box(root, Vector3(0.9, 0.5, 1.4), Vector3(0, 1.45, 0.5), GLASS)


func _bomb(root: Node3D) -> void:
	# 2.4 m long, hanging nose down when dropped: the long axis is Z, nose -Z.
	_cylinder(root, 0.55, 0.55, 1.6, Vector3(0, 0, -0.2), IRON, "z", 12)
	_sphere(root, 0.55, Vector3(0, 0, -1.0), IRON, Vector3(1, 1, 1.0))
	_cylinder(root, 0.12, 0.55, 0.8, Vector3(0, 0, 1.0), IRON, "z", 12)
	_box(root, Vector3(0.05, 1.4, 0.5), Vector3(0, 0, 1.2), STEEL)
	_box(root, Vector3(1.4, 0.05, 0.5), Vector3(0, 0, 1.2), STEEL)
	_cylinder(root, 0.56, 0.56, 0.12, Vector3(0, 0, -0.1), OCHRE, "z", 12)


func _flakgun(root: Node3D) -> void:
	# An 88 mm anti-aircraft gun on its cruciform mounting: the barrel points -Z, raised 55 degrees.
	_cylinder(root, 0.9, 1.0, 0.4, Vector3(0, 0.2, 0), STEEL, "y", 12)
	for angle in [0.0, 90.0]:
		_box(root, Vector3(0.45, 0.35, 5.6), Vector3(0, 0.3, 0), OLIVE_DARK, Vector3(0, angle, 0))
	_cylinder(root, 0.55, 0.65, 1.0, Vector3(0, 0.9, 0), OLIVE, "y", 12)
	_box(root, Vector3(2.0, 1.5, 0.35), Vector3(0, 2.0, -0.55), OLIVE_DARK) # the shield
	_box(root, Vector3(0.35, 0.35, 0.35), Vector3(-0.8, 1.55, 0), STEEL)
	_box(root, Vector3(0.35, 0.35, 0.35), Vector3(0.8, 1.55, 0), STEEL)
	var barrel := Node3D.new()
	barrel.position = Vector3(0, 1.7, 0)
	barrel.rotation_degrees = Vector3(55, 0, 0)
	root.add_child(barrel)
	_cylinder(barrel, 0.1, 0.14, 4.8, Vector3(0, 0, -2.0), IRON, "z", 8)
	_cylinder(barrel, 0.16, 0.16, 0.7, Vector3(0, 0, -4.3), STEEL, "z", 8)
	_box(barrel, Vector3(0.7, 0.7, 1.2), Vector3(0, 0, 0.3), STEEL)


func _sandbags(root: Node3D) -> void:
	# A ring of sandbags 6 m across with a gap at the back (+Z), as a low wall.
	var count := 16
	for k in count:
		var angle := deg_to_rad(40.0 + 280.0 * k / (count - 1))
		var position := Vector3(sin(angle) * 3.0, 0.5, cos(angle) * 3.0)
		var bag := _box(root, Vector3(1.3, 1.0, 0.8), position, SAND)
		bag.rotation.y = angle
	for k in count:
		var angle := deg_to_rad(48.0 + 264.0 * k / (count - 1))
		var position := Vector3(sin(angle) * 3.0, 1.35, cos(angle) * 3.0)
		var bag := _box(root, Vector3(1.2, 0.7, 0.75), position, KHAKI)
		bag.rotation.y = angle


func _ammo_crate(root: Node3D) -> void:
	_box(root, Vector3(1.4, 0.8, 0.9), Vector3(0, 0.4, 0), WOOD)
	_box(root, Vector3(1.45, 0.12, 0.2), Vector3(0, 0.55, 0), OCHRE)
	_box(root, Vector3(1.0, 0.7, 0.8), Vector3(1.3, 0.35, 0.3), OLIVE_DARK)


# ---- road furniture --------------------------------------------------------------------------------------------

func _post(root: Node3D, height: float) -> void:
	_cylinder(root, 0.07, 0.07, height, Vector3(0, height * 0.5, 0), STEEL, "y", 8)
	_cylinder(root, 0.16, 0.2, 0.25, Vector3(0, 0.125, 0), CONCRETE, "y", 8)


func _sign_stop(root: Node3D) -> void:
	_post(root, 3.2)
	_plate(root, 0.55, 8, 0.05, Vector3(0, 3.4, 0), CREAM, 22.5)
	_plate(root, 0.49, 8, 0.08, Vector3(0, 3.4, 0), RED, 22.5)
	_box(root, Vector3(0.62, 0.14, 0.1), Vector3(0, 3.4, 0), CREAM)


func _sign_yield(root: Node3D) -> void:
	_post(root, 3.0)
	_triangle(root, 0.8, 0.05, Vector3(0, 3.5, 0), RED)
	_triangle(root, 0.45, 0.08, Vector3(0, 3.45, 0), CREAM)


func _sign_priority(root: Node3D) -> void:
	_post(root, 3.0)
	_plate(root, 0.62, 4, 0.05, Vector3(0, 3.4, 0), CREAM)
	_plate(root, 0.46, 4, 0.08, Vector3(0, 3.4, 0), OCHRE)


func _traffic_light(root: Node3D) -> void:
	# A 4.6 m post with a dark housing and three hoods; the live lamp is added by the game in the middle of the housing (4.5 m up).
	_post(root, 4.6)
	_box(root, Vector3(0.5, 1.5, 0.5), Vector3(0, 4.5, 0), IRON)
	for y in [4.95, 4.5, 4.05]:
		_box(root, Vector3(0.62, 0.06, 0.2), Vector3(0, y + 0.22, 0.3), IRON)
		_box(root, Vector3(0.62, 0.06, 0.2), Vector3(0, y + 0.22, -0.3), IRON)


func _rail_signal(root: Node3D, path: bool) -> void:
	# A 4.4 m post with a lamp housing at 4.3 m (1.4 m tall for a path signal, which also has a bar under it); the live lamp is added by the game.
	_post(root, 4.4)
	var height := 1.4 if path else 0.9
	_box(root, Vector3(0.9, height, 0.5), Vector3(0, 4.3, 0), IRON)
	if path:
		_box(root, Vector3(1.0, 0.15, 0.6), Vector3(0, 3.35, 0), CREAM)


func _crossing_post(root: Node3D) -> void:
	# The post a level crossing barrier turns on: 1.1 m above the road at its top, where the arm is hung.
	_box(root, Vector3(0.5, 0.9, 0.5), Vector3(0, 0.45, 0), CONCRETE)
	_box(root, Vector3(0.3, 0.6, 0.3), Vector3(0, 1.2, 0), IRON)
	_cylinder(root, 0.22, 0.22, 0.35, Vector3(0, 1.1, 0), RED, "x", 10)


func _rail_platform(root: Node3D) -> void:
	# A platform 36 m long, 3.2 m wide and 0.9 m high, running along X; the track runs beside it on its -Z side, whose edge has a pale strip.
	_box(root, Vector3(36.0, 0.9, 3.2), Vector3(0, 0.45, 0), CONCRETE)
	_box(root, Vector3(36.0, 0.12, 0.35), Vector3(0, 0.96, -1.4), CREAM)
	_cylinder(root, 0.04, 0.04, 2.4, Vector3(16.0, 2.1, 1.0), STEEL, "y", 6)
	_plate(root, 0.35, 8, 0.05, Vector3(16.0, 3.2, 1.0), SLATE_BLUE, 22.5)


func _rail_canopy(root: Node3D) -> void:
	# The roof of a station: 10 m long along X, 3.6 m deep, on two posts, with a back wall and a bench; it stands on a platform (y = 0.9 is its floor) on the +Z side of the track.
	for x in [-4.4, 4.4]:
		_box(root, Vector3(0.25, 3.0, 0.25), Vector3(x, 2.4, 1.1), STEEL)
	_box(root, Vector3(10.0, 0.3, 3.6), Vector3(0, 3.7, 0.2), BRICK)
	_box(root, Vector3(9.6, 1.0, 0.2), Vector3(0, 1.9, 1.45), WOOD)
	_box(root, Vector3(2.4, 0.15, 0.6), Vector3(-2.0, 1.4, 1.1), WOOD)


func _bus_stop(root: Node3D) -> void:
	# A sign post with a round plate and a small shelter, 4.2 m long along X, open toward +Z (the road).
	_post(root, 3.4)
	_plate(root, 0.38, 14, 0.05, Vector3(0, 3.55, 0), SLATE_BLUE)
	_plate(root, 0.22, 14, 0.07, Vector3(0, 3.55, 0), CREAM)
	_box(root, Vector3(4.2, 0.18, 1.8), Vector3(0, 2.5, -1.2), BRICK)
	_box(root, Vector3(4.0, 2.4, 0.12), Vector3(0, 1.3, -2.0), WOOD)
	for x in [-1.95, 1.95]:
		_box(root, Vector3(0.14, 2.4, 0.14), Vector3(x, 1.2, -0.45), STEEL)
	_box(root, Vector3(2.6, 0.12, 0.5), Vector3(0, 0.5, -1.65), WOOD)


# ---- what is left on the road ----------------------------------------------------------------------------------

func _wreck(root: Node3D) -> void:
	# A burnt-out car, 4.5 m long, crumpled and leaning on a flat tyre.
	_box(root, Vector3(1.9, 0.9, 4.4), Vector3(0, 0.55, 0), CHAR, Vector3(0, 0, 4))
	_box(root, Vector3(1.6, 0.6, 2.0), Vector3(0.1, 1.2, 0.3), RUST, Vector3(0, 10, -6))
	_box(root, Vector3(1.8, 0.1, 1.2), Vector3(-0.6, 1.0, -1.8), RUST, Vector3(0, -14, 24))
	for z in [-1.4, 1.5]:
		for x in [-0.9, 0.9]:
			_cylinder(root, 0.36, 0.36, 0.25, Vector3(x, 0.36, z), IRON, "x", 10)


func _debris(root: Node3D) -> void:
	# A scatter of twisted metal and a wheel across 3 m.
	_box(root, Vector3(1.4, 0.12, 0.8), Vector3(-0.8, 0.12, 0.2), RUST, Vector3(0, 25, 8))
	_box(root, Vector3(0.9, 0.3, 0.6), Vector3(0.7, 0.2, -0.5), CHAR, Vector3(0, -40, 0))
	_box(root, Vector3(0.5, 0.1, 1.2), Vector3(0.1, 0.1, 0.9), STEEL, Vector3(0, 70, 12))
	_cylinder(root, 0.36, 0.36, 0.25, Vector3(1.2, 0.2, 0.8), IRON, "y", 10)
