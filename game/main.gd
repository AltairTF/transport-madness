extends Node3D
## Wires the world together: environment, HUD, input. Command line (after `--`):
##   --bench                      measure frame rate with 1000 vehicles at 1x and 8x, then quit
##   --shot=<file> [--shot-after=<seconds>]   save a screenshot and quit
##   --selftest-build             build roads with simulated mouse clicks, run, print stats, quit
##   --selftest-crash             crash a car, follow the crew until the road is clear, print the timeline, quit
##   --distance=<m> --pitch=<deg> --focus=<x>,<z>   place the camera (useful with --shot)

const VEHICLES := 1000
const SEED := 1

@onready var host: Node3D = $SimHost
@onready var rig: Node3D = $CameraRig

var hud: CanvasLayer
var build_tool: Node
var _elapsed := 0.0
var _stats_timer := 0.0
var _args := {}
var _bench: Array = []
var _bench_index := 0
var _bench_phase_time := 0.0
var _frame_ms: Array = []


func _ready() -> void:
	for arg in OS.get_cmdline_user_args():
		var parts: PackedStringArray = arg.split("=", true, 1)
		_args[parts[0]] = parts[1] if parts.size() > 1 else true

	_build_environment()
	if _args.has("--distance"):
		rig.distance = float(_args["--distance"])
	if _args.has("--pitch"):
		rig.pitch = float(_args["--pitch"])
	if _args.has("--focus"):
		var focus: PackedStringArray = _args["--focus"].split(",")
		rig.position = Vector3(float(focus[0]), 0.0, float(focus[1]))
	rig._apply()
	host.NewDemoWorld(SEED, VEHICLES)

	hud = load("res://game/hud.gd").new()
	add_child(hud)
	hud.speed_selected.connect(func(speed: float) -> void: host.SetSpeed(speed))

	build_tool = load("res://game/build_tool.gd").new()
	add_child(build_tool)
	build_tool.setup(host, rig.get_node("Camera3D"))
	build_tool.message.connect(hud.show_message)
	if _args.has("--selftest-build"):
		build_tool.message.connect(func(text: String) -> void: print("SELFTEST message: ", text))
	hud.tool_selected.connect(build_tool.set_tool)
	build_tool.tool_changed.connect(hud.select_tool)
	hud.action_requested.connect(_on_action)

	if _args.has("--selftest-build"):
		_selftest_build()
	if _args.has("--selftest-crash"):
		_selftest_crash()

	if _args.has("--bench"):
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
		_bench = [{"speed": 1.0, "warmup": 3.0, "measure": 8.0}, {"speed": 8.0, "warmup": 3.0, "measure": 8.0}]
		_start_bench_phase()


func _process(delta: float) -> void:
	_elapsed += delta
	_stats_timer += delta
	if _stats_timer > 0.25:
		_stats_timer = 0.0
		hud.show_stats(host.GetStats())

	if _args.has("--shot") and not _args.has("--selftest-build") and not _args.has("--selftest-crash") and _elapsed > float(_args.get("--shot-after", 6.0)):
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
		get_tree().quit()

	if _bench_index < _bench.size():
		_run_bench(delta)


func _on_action(action: String) -> void:
	match action:
		"traffic":
			hud.show_message("Added %d cars" % host.SpawnTraffic(100))
		"crash":
			var victim: int = host.CrashSomeone()
			hud.show_message("Vehicle %d crashed" % victim if victim >= 0 else "No moving car to crash")
		"empty":
			host.NewEmptyWorld(SEED)
		"demo":
			host.NewDemoWorld(SEED, VEHICLES)


## Drives the real tool with synthetic mouse events: two chained roads plus an avenue, a depot, then traffic.
func _selftest_build() -> void:
	host.NewEmptyWorld(SEED)
	rig.distance = 400.0
	rig.pitch = -89.0
	rig.position = Vector3.ZERO
	rig._apply()
	await get_tree().process_frame
	await get_tree().process_frame
	var camera: Camera3D = rig.get_node("Camera3D")

	# World points on the ground, converted to the screen positions a player would click.
	var clicks := [
		["road2", [Vector3(-150, 0, 0), Vector3(0, 0, 0), Vector3(0, 0, 100)]],
		["road4", [Vector3(0, 0, 0), Vector3(150, 0, 0)]],
		["road2", [Vector3(0, 0, 0), Vector3(-5, 0, -110)]],
		["road2", [Vector3(-80, 0, 60), Vector3(-80, 0, -60)]], # crosses the first road: a junction forms
		["road2", [Vector3(75, 0, 0), Vector3(75, 0, 90)]],     # starts on the middle of the avenue: a T junction
		["depot", [Vector3(0, 0, 0)]],
	]
	for step in clicks:
		hud.select_tool(step[0])
		build_tool.set_tool(step[0])
		for point in step[1]:
			await _click_world(camera, point)
		build_tool.set_tool("pointer")
	var stats: Dictionary = host.GetStats()
	print("SELFTEST roads=%d depots=%d" % [host.RoadCount(), host.DepotCount()])
	host.SpawnTraffic(60)
	host.SetSpeed(8.0)
	await get_tree().create_timer(10.0).timeout
	stats = host.GetStats()
	print("SELFTEST vehicles=%d mean_speed=%.1f tick=%d incidents=%d" % [stats["vehicles"], stats["mean_speed"], stats["tick"], stats["incidents"]])
	if _args.has("--shot"):
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
	get_tree().quit()


## Crashes a car in the demo world and follows the incident from wreck to cleared road at 8x.
func _selftest_crash() -> void:
	await get_tree().create_timer(2.0).timeout
	host.SetSpeed(8.0)
	var victim: int = host.CrashSomeone()
	print("CRASH victim=%d" % victim)
	var last_state := ""
	var shot_taken := false
	var waited := 0.0
	while waited < 120.0:
		await get_tree().create_timer(0.25).timeout
		waited += 0.25
		var state: String = host.FirstOpenIncidentState()
		if state != last_state:
			var stats: Dictionary = host.GetStats()
			print("CRASH state=%s tick=%d incidents=%d open=%d" % [state, stats["tick"], stats["incidents"], stats["open_incidents"]])
			last_state = state
		if state == "Clearing" and not shot_taken and _args.has("--shot"):
			var at: Vector3 = host.FirstOpenIncidentPosition()
			rig.position = at
			rig.distance = 60.0
			rig.pitch = -60.0
			rig._apply()
			await get_tree().process_frame
			await get_tree().process_frame
			get_viewport().get_texture().get_image().save_png(_args["--shot"])
			shot_taken = true
		if state == "none":
			break
	print("CRASH done cleared=%s" % (last_state == "none"))
	get_tree().quit()


func _click_world(camera: Camera3D, point: Vector3) -> void:
	# Input events carry window coordinates; unproject_position gives viewport coordinates (the stretch mode differs).
	var screen: Vector2 = get_viewport().get_final_transform() * camera.unproject_position(point)
	var motion := InputEventMouseMotion.new()
	motion.position = screen
	Input.parse_input_event(motion)
	await get_tree().process_frame
	var press := InputEventMouseButton.new()
	press.position = screen
	press.button_index = MOUSE_BUTTON_LEFT
	press.pressed = true
	Input.parse_input_event(press)
	await get_tree().process_frame


func _start_bench_phase() -> void:
	var phase: Dictionary = _bench[_bench_index]
	host.SetSpeed(phase["speed"])
	hud.highlight_speed(phase["speed"])
	_bench_phase_time = 0.0
	_frame_ms.clear()


func _run_bench(delta: float) -> void:
	var phase: Dictionary = _bench[_bench_index]
	_bench_phase_time += delta
	if _bench_phase_time > phase["warmup"]:
		_frame_ms.append(delta * 1000.0)
	if _bench_phase_time < phase["warmup"] + phase["measure"]:
		return

	_frame_ms.sort()
	var total := 0.0
	for ms in _frame_ms:
		total += ms
	var stats: Dictionary = host.GetStats()
	var worst_1pct: float = _frame_ms[int(_frame_ms.size() * 0.99)]
	print("BENCH speed=%dx vehicles=%d avg_fps=%.1f worst1pct_fps=%.1f max_frame_ms=%.1f sim_speed=x%.2f step_ms=%.2f incidents=%d" % [
		phase["speed"], stats["vehicles"], 1000.0 * _frame_ms.size() / total, 1000.0 / worst_1pct,
		_frame_ms[_frame_ms.size() - 1], stats["effective_speed"], stats["step_ms"], stats["incidents"]])
	_bench_index += 1
	if _bench_index < _bench.size():
		_start_bench_phase()
	else:
		get_tree().quit()


func _build_environment() -> void:
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.6, 0.75, 0.9)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.75, 0.78, 0.85)
	environment.ambient_light_energy = 0.7
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	add_child(world_environment)

	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-55, -35, 0)
	sun.light_energy = 1.1
	add_child(sun)
