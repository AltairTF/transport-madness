extends SceneTree
## Checks the Model loader (game/model_library.gd) and data/models.json.
## Run: Godot --headless --path . --script res://tools/selftest_models.gd [-- <folder holding car-kit/Models/GLB format/sedan.glb>]
## The folder is the local downloads folder of the packs (outside git); without it the checks that need a real GLB are skipped.

const Library := preload("res://game/model_library.gd")
const Credits := preload("res://game/credits_menu.gd")
var _failed := 0


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	var packs := args[0] if args.size() > 0 else ""

	var shipped := Library.load_file()
	_expect(shipped.check().is_empty(), "data/models.json is usable: %s" % str(shipped.check()))
	print("data/models.json: %d models" % shipped.ids().size())

	var rows := Credits.load_rows()
	for row in rows:
		_expect(row["asset"] != "" and row["licence"] != "" and row["link"].begins_with("http") and row["folder"].begins_with("game/assets/"), "a credits row is complete: %s" % str(row))
	for id in shipped.ids():
		for variant in shipped.variant_count(id):
			var scene: String = shipped.scene_path(id, variant)
			if scene == "":
				continue # a composite has no file of its own
			var listed := false
			for row in rows:
				listed = listed or scene.begins_with("res://" + row["folder"])
			_expect(listed, "%s is inside a folder listed in CREDITS.md (%s)" % [id, scene])
	var sample := Credits.parse("| Asset | Author | Licence | Link | Folder |\n| --- | --- | --- | --- | --- |\n| Car Kit | Kenney | CC0 | https://kenney.nl/assets/car-kit | game/assets/vehicles/ |\nnot a row\n")
	_expect(sample.size() == 1 and sample[0]["author"] == "Kenney" and sample[0]["folder"] == "game/assets/vehicles/", "the credits table is parsed, got %s" % str(sample))

	var bad := Library.new()
	bad.define({
		"no_variants": { "variants": [], "fit": { "mode": "length", "value": 4.5 } },
		"no_mode": { "variants": [{ "scene": "x.glb" }], "fit": { "mode": "stretch", "value": 1 } },
		"zero": { "variants": [{ "scene": "x.glb" }], "fit": { "mode": "length", "value": 0 } },
		"footprint": { "variants": [{ "scene": "x.glb" }], "fit": { "mode": "footprint", "value": [4.0] } },
		"missing": { "variants": [{ "scene": "res://game/assets/none.glb" }], "fit": { "mode": "length", "value": 4.5 } },
	})
	_expect(bad.errors.size() == 4, "four bad entries are refused, got %d: %s" % [bad.errors.size(), str(bad.errors)])
	_expect(bad.ids() == ["missing"], "the entry that is only missing its file is kept")
	_expect(bad.check().size() == 5 and bad.instance("missing") == null and bad.baked("missing").is_empty(), "a missing scene is reported and gives null")
	_expect(bad.pick("missing", 7) == 0 and bad.pick("nothing", 7) == 0, "pick on one variant or none gives 0")

	var car := packs + "/car-kit/Models/GLB format/sedan.glb"
	if packs != "" and FileAccess.file_exists(car):
		var library := Library.new()
		library.define({
			"car.length": { "variants": [{ "scene": car }], "fit": { "mode": "length", "value": 4.5 } },
			"car.turned": { "variants": [{ "scene": car, "yaw": 90 }], "fit": { "mode": "length", "value": 4.5 } },
			"car.footprint": { "variants": [{ "scene": car }, { "scene": car, "yaw": 180 }], "fit": { "mode": "footprint", "value": [2.0, 10.0] }, "offset": [0, 0.5, 0] },
		})
		_expect(library.check().is_empty(), "the sedan reads: %s" % str(library.check()))
		var box: AABB = library.bounds("car.length")
		_expect(is_equal_approx(box.size.z, 4.5), "fit length makes 4.5 m along Z, got %f" % box.size.z)
		_expect(absf(box.position.x + box.size.x * 0.5) < 0.001 and absf(box.position.z + box.size.z * 0.5) < 0.001 and absf(box.position.y) < 0.001, "the middle of the base is on the origin")
		var turned: AABB = library.bounds("car.turned")
		_expect(is_equal_approx(turned.size.z, 4.5) and turned.size.x > turned.size.z, "a yaw of 90 degrees is turned before the fit (the long side now lies along X and the fit takes the width), got %s" % str(turned.size))
		var foot: AABB = library.bounds("car.footprint")
		_expect(foot.size.x <= 2.0001 and foot.size.z <= 10.0001 and (is_equal_approx(foot.size.x, 2.0) or is_equal_approx(foot.size.z, 10.0)), "footprint is the largest uniform scale that fits, got %s" % str(foot.size))
		_expect(is_equal_approx(foot.position.y, 0.5), "the offset lifts it, got %f" % foot.position.y)
		_expect(library.variant_count("car.footprint") == 2 and library.pick("car.footprint", 5) == library.pick("car.footprint", 5), "variants are counted and pick is steady")
		var seen := {}
		for key in 200:
			seen[library.pick("car.footprint", key)] = true
		_expect(seen.size() == 2, "pick reaches both variants")
		var node: Node3D = library.instance("car.length")
		_expect(node != null and node.get_child_count() == 1, "instance gives a holder with the scene in it")
		var held := Library.extent_of(node)
		_expect(absf(held.size.z - 4.5) < 0.01, "the instance has the fitted size")
		node.free()
		var baked: Dictionary = library.baked("car.length")
		_expect(not baked.is_empty() and baked["mesh"].get_surface_count() > 0, "baked gives a mesh with surfaces")
		var mesh_box: AABB = baked["aabb"]
		_expect(absf(mesh_box.size.z - 4.5) < 0.01 and absf(mesh_box.position.y) < 0.01, "the baked mesh has the same size and base, got %s" % str(mesh_box))
	else:
		print("skipped the checks that read a real GLB (no packs folder given or %s missing)" % car)

	print("SELFTEST models: %s" % ("PASS" if _failed == 0 else "FAIL (%d)" % _failed))
	quit(0 if _failed == 0 else 1)


func _expect(ok: bool, what: String) -> void:
	if not ok:
		_failed += 1
		printerr("FAIL: " + what)
