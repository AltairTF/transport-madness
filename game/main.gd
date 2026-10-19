extends Node3D
## Wires the world together: environment, HUD, input. Command line (after `--`):
##   --bench                      measure frame rate with 1000 vehicles at 1x and 8x, then quit
##   --shot=<file> [--shot-after=<seconds>]   save a screenshot and quit
##   --selftest-build             build roads with simulated mouse clicks, run, print stats, quit
##   --hills                      start on an empty generated-hills map instead of the demo grid
##   --selftest-hills             build roads over hills, print grade and cost, quit
##   --selftest-crash             crash a car, follow the crew until the road is clear, print the timeline, quit
##   --selftest-lines             build a road, two stations, a bus line and a truck line, run 30 days, print the books, quit
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
	rig.terrain_host = host
	if _args.has("--distance"):
		rig.distance = float(_args["--distance"])
	if _args.has("--pitch"):
		rig.pitch = float(_args["--pitch"])
	if _args.has("--focus"):
		var focus: PackedStringArray = _args["--focus"].split(",")
		rig.position = Vector3(float(focus[0]), 0.0, float(focus[1]))
	rig._apply()
	if _args.has("--hills") or _args.has("--selftest-hills"):
		host.NewHillsWorld(SEED)
	else:
		host.NewDemoWorld(SEED, VEHICLES)
	if _args.has("--focus"):
		rig.focus_on(rig.position.x, rig.position.z) # now that the terrain exists, stand on it

	hud = load("res://game/hud.gd").new()
	add_child(hud)
	hud.speed_selected.connect(func(speed: float) -> void: host.SetSpeed(speed))
	hud.set_models(host.GetVehicleModels())
	hud.line_action.connect(_on_line_action)

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
	if _args.has("--selftest-hills"):
		_selftest_hills()
	if _args.has("--selftest-lines"):
		_selftest_lines()

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
		hud.show_company(host.GetCompany())
		hud.show_lines(host.GetLines())

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
		"loan":
			hud.show_message("Borrowed 100,000" if host.TakeLoan(100000) else "The loan limit is reached")
		"repay":
			hud.show_message("Repaid 100,000" if host.RepayLoan(100000) else "Nothing to repay, or not enough money")
		"empty":
			host.NewEmptyWorld(SEED)
		"hills":
			host.NewHillsWorld(SEED)
		"demo":
			host.NewDemoWorld(SEED, VEHICLES)


## Buys a vehicle for a line, or deletes the line (selling its vehicles): the buttons of the Lines panel.
func _on_line_action(kind: String, line: int, model: String) -> void:
	match kind:
		"buy":
			var id: int = host.BuyVehicle(model, line)
			hud.show_message("Bought a %s for line %d" % [model, line + 1] if id >= 0 else "Can't buy that: not enough money, or no depot with room (is the road at a depot busy?)")
		"delete":
			hud.show_message("Line %d deleted, its vehicles sold" % (line + 1) if host.DeleteLine(line) else "No such line")


## Drives the real tool with synthetic mouse events: two chained roads plus an avenue, a depot, then traffic.
func _selftest_build() -> void:
	host.NewEmptyWorld(SEED)
	rig.distance = float(_args.get("--distance", 400.0))
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
		["curve2", [Vector3(150, 0, 0), Vector3(195, 0, 45), Vector3(150, 0, 110)]],       # a bend off the avenue's end
		["curve4", [Vector3(-150, 0, 0), Vector3(-195, 0, 45), Vector3(-150, 0, 110)]],  # an avenue bend off the west road
		["curve2", [Vector3(180, 0, 100), Vector3(60, 0, 60), Vector3(0, 0, 100)]],        # arc that meets the first bend and ends on the south road
		["upgrade", [Vector3(-115, 0, 0)]],                                                 # widen the west road to an avenue
		["remove", [Vector3(75, 0, 45)]],                                                   # tear down the T road off the avenue
		["downgrade", [Vector3(30, 0, 0)]],                                                 # narrow the first half of the east avenue
		["faster", [Vector3(-115, 0, 0)]],                                                  # raise the limit on the west avenue
		["slower", [Vector3(-115, 0, 0), Vector3(-115, 0, 0)]],                             # ... and lower it twice
		["move", [Vector3(0, 0, 0), Vector3(10, 0, 10)]],                                   # drag the hub a little south-east
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
	print("SELFTEST hud sweep check %.3f" % host.SweepThrough(150, 0, 195, 45, 150, 110))
	host.SpawnTraffic(60)
	host.SetSpeed(8.0)
	await get_tree().create_timer(10.0).timeout
	stats = host.GetStats()
	print("SELFTEST vehicles=%d mean_speed=%.1f tick=%d incidents=%d" % [stats["vehicles"], stats["mean_speed"], stats["tick"], stats["incidents"]])
	if _args.has("--shot"):
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
	get_tree().quit()


## Builds a road, a depot, two stations, a bus line and a truck line through the host API, runs them for 30 days and prints the books.
func _selftest_lines() -> void:
	host.NewEmptyWorld(SEED)
	await get_tree().process_frame
	print("SELFTEST lines: road built=%s depot=%s" % [host.BuildRoad(0, 0, 1200, 0, 0), host.BuildDepot(0, 0, 0)])
	var a: int = host.BuildStation(150, 0)
	var b: int = host.BuildStation(1050, 0)
	var bus_line: int = host.CreateLine(PackedInt32Array([a, b]))
	var truck_line: int = host.CreateLine(PackedInt32Array([b, a]))
	print("SELFTEST lines: stations %d %d, lines %d %d" % [a, b, bus_line, truck_line])
	print("SELFTEST lines: bus %d" % host.BuyVehicle("bus", bus_line))
	host.StepFor(20.0)
	print("SELFTEST lines: truck %d" % host.BuyVehicle("truck", truck_line))
	host.StepFor(30.0 * 60.0)
	var company: Dictionary = host.GetCompany()
	print("SELFTEST lines: day %d money %d loan %d income %d upkeep %d" % [company["day"], company["money"], company["loan"], company["income"], company["upkeep"]])
	for line in host.GetLines():
		print("SELFTEST lines: line %d vehicles %d delivered %d income %d upkeep %d profit %d" % [line["id"], line["vehicles"], line["delivered"], line["income"], line["upkeep"], line["profit"]])
	hud.show_company(company)
	hud.show_lines(host.GetLines())
	await get_tree().process_frame
	if _args.has("--shot"):
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
	get_tree().quit()


## Builds a few roads over generated hills through the host API and reports what they cost.
func _selftest_hills() -> void:
	await get_tree().process_frame
	var attempts := [[-400, -300, 0, -300], [0, -300, 300, 0], [-400, 200, 100, 200], [-400, -300, -400, 200]]
	var curves := [[-300, -100, 0, -100, 0.9], [-300, 100, -100, 250, -0.7]]
	for c in curves:
		var cost: Dictionary = host.EstimateCurveCost(c[0], c[1], c[2], c[3], 0, c[4])
		if not cost["valid"]:
			print("HILLS curve %s refused" % [c])
			continue
		print("HILLS curve %s built=%s length=%.0f cut=%.0f fill=%.0f cost=%.0f" % [c, host.BuildCurve(c[0], c[1], c[2], c[3], 0, c[4]), cost["length"], cost["cut"], cost["fill"], cost["money"]])
	for a in attempts:
		var cost: Dictionary = host.EstimateRoadCost(a[0], a[1], a[2], a[3], 0)
		if not cost["valid"]:
			print("HILLS %s refused" % [a])
			continue
		var built: bool = host.BuildRoad(a[0], a[1], a[2], a[3], 0)
		print("HILLS %s built=%s length=%.0f cut=%.0f fill=%.0f cost=%.0f" % [a, built, cost["length"], cost["cut"], cost["fill"], cost["money"]])
	await _selftest_structures()
	host.SpawnTraffic(0)
	await get_tree().create_timer(1.0).timeout
	if _args.has("--shot"):
		get_viewport().get_texture().get_image().save_png(_args["--shot"])
	get_tree().quit()


## Looks over the generated map for the 300 m road that needs the longest bridge and the one that needs the longest
## tunnel, builds them, and points the camera at the bridge.
func _selftest_structures() -> void:
	var best := {"bridge": [-1.0, 0, 0, {}], "tunnel": [-1.0, 0, 0, {}]}
	for z in range(-1900, 1700, 100):
		for x in range(-1900, 1500, 100):
			var cost: Dictionary = host.EstimateRoadCost(x, z, x + 300, z, 0)
			if not cost["valid"]:
				continue
			for kind in ["bridge", "tunnel"]:
				if cost[kind] > best[kind][0]:
					best[kind] = [cost[kind], x, z, cost]
	for kind in ["bridge", "tunnel"]:
		var f: Array = best[kind]
		if f[0] <= 0.0:
			print("HILLS no %s found" % kind)
			continue
		var built: bool = host.BuildRoad(f[1], f[2], f[1] + 300, f[2], 0)
		print("HILLS %s road at (%d, %d): built=%s bridge=%.0f m tunnel=%.0f m cost=%.0f" % [kind, f[1], f[2], built, f[3]["bridge"], f[3]["tunnel"], f[3]["money"]])
	if best["bridge"][0] > 0.0 and not _args.has("--focus"):
		rig.focus_on(best["bridge"][1] + 150.0, best["bridge"][2])
		rig.distance = 220.0
		rig.pitch = -40.0
		rig._apply()
	await get_tree().process_frame


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
