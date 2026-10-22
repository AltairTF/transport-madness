extends RefCounted
## The Models (M7): 3D assets drawn in place of a Whitebox, read from data/models.json.
## A Model is scaled uniformly (never stretched) by its "fit", turned by its "yaw" and set with the middle of its base on the origin,
## then moved by its "offset"; the object it stands for keeps its footprint and the Model adapts to it. Purely visual: nothing here is seen by the simulation.
##
## An entry of "models":
##   "variants": [ { "scene": "res://game/assets/..../x.glb", "yaw": 0 }, ... ]   one or more; a building picks one by its id, see pick()
##   "fit": { "mode": "length" | "width" | "height" | "footprint" | "scale", "value": number, or [width, length] for "footprint" }
##          length is the extent along Z once turned, width along X; footprint is the largest scale that fits both.
##          "maxWidth" (optional) caps the width in metres: a chunky Model is made shorter than its length rather than wider than its lane or gauge.
##   Forward is -Z (a Model made facing +Z, as the Kenney ones are, takes a "yaw" of 180).
##   "offset": [x, y, z]  optional, in metres after the fit
## A composite entry has "parts" in place of "variants" and "fit": [ { "model": id of a plain Model, "at": [x, z] from the middle in metres, "yaw": degrees, "scale": uniform, "raise": metres, "variant": n }, ... ];
## it is a few things standing together (an industry; a "scale" of its own sizes the whole) and only instance() builds it. Its parts keep their own fit.
## A scene that Godot has not imported (a file outside the project, as the sheet tool and the self-test use) is read straight from disk.

const PATH := "res://data/models.json"
const MODES := ["length", "width", "height", "footprint", "scale"]

var errors: Array[String] = [] ## what the file got wrong, one line each
var _entries: Dictionary = {} ## id -> entry
var _fitted: Dictionary = {} ## "id#variant" -> { "basis": Basis, "origin": Vector3, "aabb": AABB, "parts": Array }


static func load_file(path: String = PATH) -> RefCounted:
	var library: RefCounted = (load("res://game/model_library.gd") as GDScript).new()
	if not FileAccess.file_exists(path):
		library.errors.append("%s does not exist" % path)
		return library
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(path))
	if typeof(parsed) != TYPE_DICTIONARY or typeof(parsed.get("models")) != TYPE_DICTIONARY:
		library.errors.append("%s has no \"models\" object" % path)
		return library
	library.define(parsed["models"])
	return library


## Takes the "models" object (also used by the self-test with entries of its own); a bad entry is reported in errors and left out.
func define(models: Dictionary) -> void:
	for id in models:
		var entry: Variant = models[id]
		var problem := _problem(entry)
		if problem != "":
			errors.append("%s: %s" % [id, problem])
			continue
		_entries[str(id)] = entry


func is_composite(id: String) -> bool:
	return _entries.has(id) and _entries[id].has("parts")


func ids() -> Array:
	return _entries.keys()


func has(id: String) -> bool:
	return _entries.has(id)


func variant_count(id: String) -> int:
	if not _entries.has(id):
		return 0
	return 1 if is_composite(id) else _entries[id]["variants"].size()


## The variant a key (an id of a building, say) chooses; the same key always gives the same one and no random stream is touched.
func pick(id: String, key: int) -> int:
	var count := variant_count(id)
	return 0 if count == 0 else posmod(key * 2654435761 >> 7, count)


## A fresh Node3D holding the Model, ready to add to the scene; null if the id is unknown or the scene cannot be read.
func instance(id: String, variant: int = 0) -> Node3D:
	if is_composite(id):
		return _compose(id)
	var fit := _fit(id, variant)
	if fit.is_empty():
		return null
	var holder := Node3D.new()
	holder.name = id.replace(".", "_")
	var scene := _read_scene(scene_path(id, variant))
	scene.transform = Transform3D(fit["basis"], fit["origin"])
	holder.add_child(scene)
	return holder


## The Model as one Mesh with its fit applied, for a MultiMesh: { "mesh": ArrayMesh, "aabb": AABB }; empty if it cannot be read.
func baked(id: String, variant: int = 0) -> Dictionary:
	if is_composite(id):
		return {}
	var fit := _fit(id, variant)
	if fit.is_empty():
		return {}
	var mesh := ArrayMesh.new()
	var transform := Transform3D(fit["basis"], fit["origin"])
	for part: Dictionary in fit["parts"]:
		var source: Mesh = part["mesh"]
		var xf: Transform3D = transform * part["transform"]
		for surface in source.get_surface_count():
			var arrays := source.surface_get_arrays(surface)
			var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
			for i in vertices.size():
				vertices[i] = xf * vertices[i]
			arrays[Mesh.ARRAY_VERTEX] = vertices
			if arrays[Mesh.ARRAY_NORMAL] != null:
				var normals: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
				for i in normals.size():
					normals[i] = (xf.basis * normals[i]).normalized()
				arrays[Mesh.ARRAY_NORMAL] = normals
			arrays[Mesh.ARRAY_TANGENT] = null # the Models carry no normal maps
			mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
			var material: Material = part["override"] if part["override"] != null else source.surface_get_material(surface)
			mesh.surface_set_material(mesh.get_surface_count() - 1, material)
	return { "mesh": mesh, "aabb": mesh.get_aabb() }


## Where the Model ends up: the box it fills, with the middle of its base on the origin (before the offset).
func bounds(id: String, variant: int = 0) -> AABB:
	var fit := _fit(id, variant)
	return AABB() if fit.is_empty() else fit["aabb"]


## Every problem the file has, including scenes that cannot be read; empty when the whole library is usable.
func check() -> Array[String]:
	var problems: Array[String] = errors.duplicate()
	for id in _entries:
		if is_composite(id):
			for part: Dictionary in _entries[id]["parts"]:
				var part_id := str(part["model"])
				if not _entries.has(part_id) or is_composite(part_id):
					problems.append("%s: part %s is not a plain Model" % [id, part_id])
				elif int(part.get("variant", 0)) >= variant_count(part_id):
					problems.append("%s: part %s has no variant %d" % [id, part_id, int(part.get("variant", 0))])
			continue
		for variant in variant_count(id):
			if _fit(id, variant).is_empty():
				problems.append("%s variant %d: cannot read %s" % [id, variant, scene_path(id, variant)])
	return problems


## The size, in metres, a fresh Node3D scene occupies before any fit, with the turn applied; used by the sheet tool too.
static func extent_of(root: Node3D) -> AABB:
	var parts: Array = []
	_collect_children(root, parts)
	return _union(parts, Transform3D.IDENTITY)


static func _problem(entry: Variant) -> String:
	if typeof(entry) != TYPE_DICTIONARY:
		return "is not an object"
	if entry.has("parts"):
		if typeof(entry["parts"]) != TYPE_ARRAY or entry["parts"].is_empty():
			return "has no parts"
		for part: Variant in entry["parts"]:
			if typeof(part) != TYPE_DICTIONARY or typeof(part.get("model")) != TYPE_STRING:
				return "has a part without a model"
			var at: Variant = part.get("at", [0, 0])
			if typeof(at) != TYPE_ARRAY or at.size() != 2:
				return "has a part whose \"at\" is not [x, z]"
		return ""
	var variants: Variant = entry.get("variants")
	if typeof(variants) != TYPE_ARRAY or variants.is_empty():
		return "has no variants"
	for variant: Variant in variants:
		if typeof(variant) != TYPE_DICTIONARY or typeof(variant.get("scene")) != TYPE_STRING:
			return "has a variant without a scene"
	var fit: Variant = entry.get("fit")
	if typeof(fit) != TYPE_DICTIONARY or not MODES.has(fit.get("mode")):
		return "needs a fit with a mode of %s" % ", ".join(MODES)
	var value: Variant = fit.get("value")
	if fit["mode"] == "footprint":
		if typeof(value) != TYPE_ARRAY or value.size() != 2 or float(value[0]) <= 0.0 or float(value[1]) <= 0.0:
			return "fit footprint needs [width, length] above zero"
	elif not (typeof(value) in [TYPE_FLOAT, TYPE_INT]) or float(value) <= 0.0:
		return "fit %s needs a value above zero" % fit["mode"]
	return ""


## The scene file of a variant.
func scene_path(id: String, variant: int) -> String:
	if is_composite(id):
		return ""
	return _entries[id]["variants"][variant]["scene"]


## A composite: its parts, each an instance of a plain Model, placed on the ground around the middle.
func _compose(id: String) -> Node3D:
	var holder := Node3D.new()
	holder.name = id.replace(".", "_")
	holder.scale = Vector3.ONE * float(_entries[id].get("scale", 1.0))
	for part: Dictionary in _entries[id]["parts"]:
		var node := instance(str(part["model"]), int(part.get("variant", 0)))
		if node == null:
			continue
		var at: Array = part.get("at", [0, 0])
		node.position = Vector3(float(at[0]), float(part.get("raise", 0.0)), float(at[1]))
		node.rotation.y = deg_to_rad(float(part.get("yaw", 0.0)))
		node.scale = Vector3.ONE * float(part.get("scale", 1.0))
		holder.add_child(node)
	return holder


func _fit(id: String, variant: int) -> Dictionary:
	if is_composite(id):
		return {}
	var key := "%s#%d" % [id, variant]
	if _fitted.has(key):
		return _fitted[key]
	if not _entries.has(id) or variant < 0 or variant >= variant_count(id):
		return {}
	var entry: Dictionary = _entries[id]
	var scene := _read_scene(scene_path(id, variant))
	if scene == null:
		return {}
	var parts: Array = []
	_collect_children(scene, parts)
	scene.free()
	if parts.is_empty():
		return {}
	var yaw := Basis(Vector3.UP, deg_to_rad(float(entry["variants"][variant].get("yaw", 0.0))))
	var turned := _union(parts, Transform3D(yaw, Vector3.ZERO))
	var scale := _scale_for(entry["fit"], turned.size)
	if entry["fit"].has("maxWidth"):
		scale = minf(scale, float(entry["fit"]["maxWidth"]) / maxf(turned.size.x, 0.0001))
	var basis := yaw.scaled(Vector3.ONE * scale)
	var fitted := Transform3D(Basis.IDENTITY.scaled(Vector3.ONE * scale), Vector3.ZERO) * turned
	var offset: Variant = entry.get("offset")
	var shift := Vector3(-(fitted.position.x + fitted.size.x * 0.5), -fitted.position.y, -(fitted.position.z + fitted.size.z * 0.5))
	if typeof(offset) == TYPE_ARRAY and offset.size() == 3:
		shift += Vector3(float(offset[0]), float(offset[1]), float(offset[2]))
	var box := AABB(fitted.position + shift, fitted.size)
	var result := { "basis": basis, "origin": shift, "aabb": box, "parts": parts }
	_fitted[key] = result
	return result


static func _scale_for(fit: Dictionary, size: Vector3) -> float:
	var value: Variant = fit["value"]
	match fit["mode"]:
		"length": return float(value) / maxf(size.z, 0.0001)
		"width": return float(value) / maxf(size.x, 0.0001)
		"height": return float(value) / maxf(size.y, 0.0001)
		"footprint": return minf(float(value[0]) / maxf(size.x, 0.0001), float(value[1]) / maxf(size.z, 0.0001))
	return float(value)


## A scene Godot imported, or a .glb read from disk at run time. A new root every call; the caller owns it.
static func _read_scene(path: String) -> Node3D:
	if ResourceLoader.exists(path):
		var packed := load(path) as PackedScene
		return packed.instantiate() as Node3D if packed != null else null
	if not FileAccess.file_exists(path):
		return null
	var document := GLTFDocument.new()
	var state := GLTFState.new()
	if document.append_from_file(path, state) != OK:
		return null
	return document.generate_scene(state) as Node3D


## The meshes under a scene root with their transforms relative to the root (the root's own transform is left out: instance() sets it).
static func _collect_children(root: Node, out: Array) -> void:
	for child in root.get_children():
		_collect(child, Transform3D.IDENTITY, out)


static func _collect(node: Node, parent: Transform3D, out: Array) -> void:
	var transform: Transform3D = parent * node.transform if node is Node3D else parent
	if node is MeshInstance3D and node.mesh != null:
		out.append({ "mesh": node.mesh, "transform": transform, "override": node.material_override })
	for child in node.get_children():
		_collect(child, transform, out)


static func _union(parts: Array, by: Transform3D) -> AABB:
	var box := AABB()
	var first := true
	for part: Dictionary in parts:
		var piece: AABB = by * part["transform"] * (part["mesh"] as Mesh).get_aabb()
		box = piece if first else box.merge(piece)
		first = false
	return box
