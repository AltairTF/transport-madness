extends SceneTree
## Prints the fitted size of every Model of data/models.json: Godot --headless --path . --script res://tools/print_models.gd


func _initialize() -> void:
	var library: RefCounted = load("res://game/model_library.gd").load_file()
	for id in library.ids():
		var box: AABB = library.bounds(id)
		print("%-16s %.2f wide  %.2f high  %.2f long" % [id, box.size.x, box.size.y, box.size.z])
	print(library.check())
	quit()
