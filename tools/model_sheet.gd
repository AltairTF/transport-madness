extends SceneTree
## Renders one preview PNG for each GLB, so candidate Models can be looked at side by side before any is imported.
## Run (needs a window, so not --headless):
##   Godot --path . --script res://tools/model_sheet.gd -- <out folder> <a.glb> [<b.glb> ...]
## Writes <out folder>/<name of the glb>.png at 360 x 270: three-quarter view from the front right, a soft key light, the ground as a grey plate.
## An argument "model:<id>" renders that Model of data/models.json (a composite industry, a variant 0 plain Model) instead of a file.
## GLBs are read from disk as they are (no import, no copy into the project). Slow models are not a concern: one frame each.

const Library := preload("res://game/model_library.gd")
const SIZE := Vector2i(360, 270)


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() < 2:
		printerr("usage: -- <out folder> <a.glb> [<b.glb> ...]")
		quit(2)
		return
	DirAccess.make_dir_recursive_absolute(args[0])
	_render_all(args)


func _render_all(args: PackedStringArray) -> void:
	var viewport := SubViewport.new()
	viewport.size = SIZE
	viewport.own_world_3d = true
	viewport.msaa_3d = Viewport.MSAA_4X
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	await process_frame # the viewport is in the tree from the next frame

	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.86, 0.88, 0.90)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.85, 0.87, 0.92)
	environment.ambient_light_energy = 0.7
	var world := WorldEnvironment.new()
	world.environment = environment
	viewport.add_child(world)
	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-50, 35, 0)
	sun.light_energy = 1.1
	viewport.add_child(sun)
	var camera := Camera3D.new()
	camera.fov = 30.0
	viewport.add_child(camera)

	var library: RefCounted = Library.load_file()
	for index in range(1, args.size()):
		var path := args[index]
		var scene: Node3D = library.instance(path.trim_prefix("model:")) if path.begins_with("model:") else Library._read_scene(path)
		if scene == null:
			printerr("cannot read " + path)
			continue
		viewport.add_child(scene)
		var box := Library.extent_of(scene)
		var middle := box.get_center()
		var radius := box.size.length() * 0.5
		var plate := MeshInstance3D.new()
		var mesh := BoxMesh.new()
		mesh.size = Vector3(box.size.x * 1.5 + 0.2, 0.02, box.size.z * 1.5 + 0.2)
		plate.mesh = mesh
		var plate_material := StandardMaterial3D.new()
		plate_material.albedo_color = Color(0.62, 0.64, 0.62)
		plate.material_override = plate_material
		plate.position = Vector3(middle.x, box.position.y - 0.011, middle.z)
		viewport.add_child(plate)
		var direction := Vector3(0.85, 0.6, 1.1).normalized()
		camera.position = middle + direction * (radius / sin(deg_to_rad(camera.fov) * 0.5)) * 1.05
		camera.look_at(middle)
		for frame in 3:
			await process_frame
		var image := viewport.get_texture().get_image()
		var name := path.trim_prefix("model:") if path.begins_with("model:") else path.get_file().get_basename()
		image.save_png("%s/%s.png" % [args[0], name])
		print("%s  %.2f x %.2f x %.2f m" % [name, box.size.x, box.size.y, box.size.z])
		scene.free()
		plate.free()
	quit()
