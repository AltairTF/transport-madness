extends CanvasLayer
## The performance panel (F10): frame time, GPU and CPU render time, draw calls, memory, the CPU time of each part of a frame, and switches that hide a layer
## so that its price shows. It can log everything to a JSON lines file (user://perf/), one line a second plus one for each switch, and it can run a sweep:
## each layer taken away alone and a few display settings changed, one after the other, measured with vsync off, with the table written to the log.
## Started with `--perf` (open) or `--perf-sweep` (run the sweep once the map is up, print the table and quit); `--perf-scale=1.2` renders the sweep at a higher 3D scale, to stand in for a bigger screen.

const Keys := preload("res://game/keys.gd")
const WARMUP_SECONDS := 1.5
const MEASURE_SECONDS := 3.0
const SCALES := [1.0, 0.85, 0.75, 0.66, 0.5]
const DEBUG_VIEWS := [["Normal", Viewport.DEBUG_DRAW_DISABLED], ["Overdraw", Viewport.DEBUG_DRAW_OVERDRAW], ["Unshaded", Viewport.DEBUG_DRAW_UNSHADED]]

var host: Node3D ## SimHost
var rig: Node3D ## the camera rig, for the log
var args := {}

var _panel: PanelContainer
var _stats: Label
var _sections: Label
var _checks := {}
var _log_check: CheckBox
var _vsync_check: CheckBox
var _msaa_option: OptionButton
var _scale_option: OptionButton
var _view_option: OptionButton
var _log_note: Label
var _status: Label
var _log: FileAccess
var _log_path := ""
var _acc := 0.0
var _window: Array = [] ## per frame: {ms, gpu, cpu, calls, prims}
var _sweep_window: Array = []
var _sweeping := false
var _proc_cpu_seconds := -1.0
var _proc_wall := 0.0
var _proc := {"cpu_pct": 0.0, "rss_mb": 0.0}
var _last := {}


func _ready() -> void:
	layer = 90
	_build()
	_panel.visible = args.has("--perf") or args.has("--perf-sweep")
	RenderingServer.viewport_set_measure_render_time(get_viewport().get_viewport_rid(), true)
	if args.has("--perf-sweep"):
		get_tree().create_timer(3.0).timeout.connect(run_sweep)


func _unhandled_input(event: InputEvent) -> void:
	if Keys.pressed(event, "perf_overlay"):
		_panel.visible = not _panel.visible
		get_viewport().set_input_as_handled()


func _process(delta: float) -> void:
	if not _panel.visible and _log == null and not _sweeping:
		return
	var vp := get_viewport().get_viewport_rid()
	var frame := {
		"ms": delta * 1000.0,
		"gpu": RenderingServer.viewport_get_measured_render_time_gpu(vp),
		"cpu": RenderingServer.viewport_get_measured_render_time_cpu(vp),
		"calls": Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME),
		"prims": Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME),
	}
	_window.append(frame)
	if _sweeping:
		_sweep_window.append(frame)
	_acc += delta
	if _acc >= 1.0:
		_sample(_acc)
		_acc = 0.0


# --- measuring ---

static func _summary(frames: Array) -> Dictionary:
	if frames.is_empty():
		return {}
	var times := PackedFloat64Array()
	var gpu := 0.0
	var cpu := 0.0
	var calls := 0.0
	var prims := 0.0
	var total := 0.0
	for f in frames:
		times.append(f["ms"])
		total += f["ms"]
		gpu += f["gpu"]
		cpu += f["cpu"]
		calls += f["calls"]
		prims += f["prims"]
	times.sort()
	var n := frames.size()
	var avg := total / n
	var p99: float = times[min(n - 1, int(n * 0.99))]
	return {
		"frames": n, "fps": 1000.0 / avg, "frame_ms": avg, "frame_ms_p99": p99, "fps_1pct_low": 1000.0 / p99, "frame_ms_max": times[n - 1],
		"gpu_ms": gpu / n, "render_cpu_ms": cpu / n, "draw_calls": calls / n, "primitives": prims / n,
	}


static func _section_table(timings: Dictionary) -> Dictionary:
	var sections := {}
	for key in timings:
		sections[key] = {"avg_ms": snappedf(timings[key][0], 0.001), "max_ms": snappedf(timings[key][1], 0.001)}
	return sections


## Bytes to MB; -1 when the driver gives a wrapped negative number (Metal reports texture memory as about 2^64 here).
static func _megabytes(bytes: float) -> float:
	return -1.0 if bytes < 0.0 or bytes >= 9.2e18 else bytes / 1048576.0


func _sample(seconds: float) -> void:
	var s := _summary(_window)
	_window = []
	_update_process_stats(seconds)
	var mem := {
		"video_mb": Performance.get_monitor(Performance.RENDER_VIDEO_MEM_USED) / 1048576.0,
		"texture_mb": _megabytes(Performance.get_monitor(Performance.RENDER_TEXTURE_MEM_USED)),
		"buffer_mb": Performance.get_monitor(Performance.RENDER_BUFFER_MEM_USED) / 1048576.0,
		"static_mb": Performance.get_monitor(Performance.MEMORY_STATIC) / 1048576.0,
		"objects": Performance.get_monitor(Performance.RENDER_TOTAL_OBJECTS_IN_FRAME),
		"nodes": Performance.get_monitor(Performance.OBJECT_NODE_COUNT),
		"orphan_nodes": Performance.get_monitor(Performance.OBJECT_ORPHAN_NODE_COUNT),
	}
	var timings: Dictionary = host.PerfTimings() if host != null else {}
	var counts: Dictionary = host.PerfCounts() if host != null else {}
	_last = {"stats": s, "mem": mem, "proc": _proc.duplicate(), "sections": timings, "counts": counts}
	_show(s, mem, timings, counts)
	if _log != null and not s.is_empty():
		_write({"type": "sample", "stats": _round(s), "mem": _round(mem), "process": _round(_proc), "sections": _section_table(timings), "counts": counts, "camera": _camera()})


func _update_process_stats(seconds: float) -> void:
	# CPU time and resident memory of this process from ps (macOS and Linux); the call itself costs a few milliseconds once a second.
	if OS.get_name() not in ["macOS", "Linux"]:
		return
	var out := []
	if OS.execute("ps", ["-o", "time=,rss=", "-p", str(OS.get_process_id())], out) != 0 or out.is_empty():
		return
	var parts: PackedStringArray = str(out[0]).strip_edges().split(" ", false)
	if parts.size() < 2:
		return
	var cpu := _cpu_seconds(parts[0])
	if _proc_cpu_seconds >= 0.0 and seconds > 0.0:
		_proc["cpu_pct"] = max(0.0, (cpu - _proc_cpu_seconds) / seconds * 100.0)
	_proc_cpu_seconds = cpu
	_proc["rss_mb"] = float(parts[1]) / 1024.0


static func _cpu_seconds(text: String) -> float:
	var seconds := 0.0
	for part in text.split(":"):
		seconds = seconds * 60.0 + float(part)
	return seconds


func _camera() -> Dictionary:
	if rig == null:
		return {}
	return {"distance": snappedf(rig.distance, 0.1), "pitch": snappedf(rig.pitch, 0.1), "yaw": snappedf(rig.yaw, 0.1), "x": snappedf(rig.position.x, 0.1), "z": snappedf(rig.position.z, 0.1)}


func _show(s: Dictionary, mem: Dictionary, timings: Dictionary, counts: Dictionary) -> void:
	if not _panel.visible or s.is_empty():
		return
	var lines := PackedStringArray()
	lines.append("FPS %.0f   frame %.1f ms   worst 1%% %.1f ms   max %.1f ms" % [s["fps"], s["frame_ms"], s["frame_ms_p99"], s["frame_ms_max"]])
	if s["gpu_ms"] > 0.0:
		lines.append("GPU %.1f ms   render CPU %.1f ms" % [s["gpu_ms"], s["render_cpu_ms"]])
	else:
		lines.append("render CPU %.1f ms   GPU time: Metal does not report it here (launch with MTL_HUD_ENABLED=1 for Apple's HUD)" % s["render_cpu_ms"])
	lines.append("draw calls %.0f   objects %.0f   triangles %.0fk" % [s["draw_calls"], mem["objects"], s["primitives"] / 1000.0])
	var textures := "textures n/a" if mem["texture_mb"] < 0.0 else "textures %.0f" % mem["texture_mb"]
	lines.append("video memory %.0f MB (%s, buffers %.0f)" % [mem["video_mb"], textures, mem["buffer_mb"]])
	lines.append("process CPU %.0f %%   resident %.0f MB   Godot static %.0f MB" % [_proc["cpu_pct"], _proc["rss_mb"], mem["static_mb"]])
	lines.append("nodes %.0f (orphans %.0f)" % [mem["nodes"], mem["orphan_nodes"]])
	var bits := PackedStringArray()
	for key in counts:
		bits.append("%s %s" % [key, str(counts[key])])
	lines.append(", ".join(bits))
	_stats.text = "\n".join(lines)
	var rows := []
	for key in timings:
		rows.append([key, timings[key][0], timings[key][1]])
	rows.sort_custom(func(a, b) -> bool: return a[1] > b[1])
	var text := PackedStringArray(["CPU in _Process (avg ms a frame, worst)"])
	for row in rows:
		text.append("%-30s %6.2f  %6.2f" % [row[0], row[1], row[2]])
	_sections.text = "\n".join(text)


# --- the log ---

func _start_log(name := "perf") -> void:
	DirAccess.make_dir_recursive_absolute("user://perf")
	var stamp := Time.get_datetime_string_from_system(false, true).replace(":", "").replace("-", "").replace("T", "-")
	_log_path = "user://perf/%s-%s.jsonl" % [name, stamp]
	_log = FileAccess.open(_log_path, FileAccess.WRITE)
	if _log == null:
		_log_note.text = "cannot write the log"
		return
	_write({"type": "session", "system": _system(), "toggles": _toggles(), "args": args, "camera": _camera(), "counts": host.PerfCounts() if host != null else {}})
	_log_note.text = ProjectSettings.globalize_path(_log_path)


func _stop_log() -> void:
	if _log != null:
		_write({"type": "end"})
		_log.close()
		_log = null
	_log_note.text = "log closed: " + ProjectSettings.globalize_path(_log_path) if _log_path != "" else ""


func _write(entry: Dictionary) -> void:
	if _log == null:
		return
	entry["time"] = Time.get_datetime_string_from_system(true, true)
	entry["unix"] = snappedf(Time.get_unix_time_from_system(), 0.001)
	entry["uptime_s"] = snappedf(Time.get_ticks_msec() / 1000.0, 0.01)
	_log.store_line(JSON.stringify(entry))
	_log.flush()


static func _round(d: Dictionary) -> Dictionary:
	var r := {}
	for key in d:
		r[key] = snappedf(d[key], 0.001) if d[key] is float else d[key]
	return r


func _system() -> Dictionary:
	var window := get_window()
	return {
		"godot": Engine.get_version_info()["string"],
		"os": OS.get_name() + " " + OS.get_version(),
		"cpu": OS.get_processor_name(), "cores": OS.get_processor_count(),
		"memory": OS.get_memory_info(),
		"gpu": RenderingServer.get_video_adapter_name(), "gpu_vendor": RenderingServer.get_video_adapter_vendor(),
		"driver": OS.get_video_adapter_driver_info(), "renderer": RenderingServer.get_current_rendering_method(),
		"rendering_driver": RenderingServer.get_current_rendering_driver_name(),
		"from_editor": OS.has_feature("editor"), "debug_build": OS.is_debug_build(),
		"window": window.size, "screen_scale": DisplayServer.screen_get_scale(), "refresh_hz": DisplayServer.screen_get_refresh_rate(),
		"content_scale": window.content_scale_factor,
	}


func _toggles() -> Dictionary:
	var t := {}
	if host != null:
		for name in host.PerfLayers():
			t[name] = host.PerfLayerOn(name)
	var viewport := get_viewport()
	t["vsync"] = DisplayServer.window_get_vsync_mode() != DisplayServer.VSYNC_DISABLED
	t["msaa_3d"] = viewport.msaa_3d
	t["scaling_3d_scale"] = viewport.scaling_3d_scale
	t["debug_draw"] = viewport.debug_draw
	t["shadows"] = Settings.shadows
	t["ssao"] = Settings.ssao
	var lighting = _lighting()
	if lighting != null:
		t["fog"] = lighting.environment.fog_enabled
		t["grading"] = lighting.environment.adjustment_enabled
		t["moon"] = lighting.moon.visible
		t["hour"] = lighting.hour
	return t


## The lit scene of main.gd (lighting.gd), or null where there is none.
func _lighting() -> Node:
	var scene := get_tree().current_scene
	return scene.get("lighting") if scene != null else null


func _event(what: String, value: Variant) -> void:
	_write({"type": "toggle", "name": what, "value": value, "toggles": _toggles()})


# --- the sweep ---

## Takes each layer away alone, then a few display settings, measuring each for a few seconds with vsync off; writes the table to the log and prints it.
func run_sweep() -> void:
	if _sweeping or host == null:
		return
	_sweeping = true
	_panel.visible = true
	var was_logging := _log != null
	if not was_logging:
		_start_log("sweep")
		_log_check.set_pressed_no_signal(true)
	var vsync_before := DisplayServer.window_get_vsync_mode()
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	DisplayServer.window_move_to_foreground() # a window behind others is paced by the system, which hides what the content costs
	var viewport := get_viewport()
	if args.has("--perf-scale"):
		viewport.scaling_3d_scale = float(args["--perf-scale"]) # to stand in for a bigger screen than this window
	var msaa_before := viewport.msaa_3d
	var scale_before := viewport.scaling_3d_scale
	var steps := [{"name": "baseline"}]
	for layer in host.PerfLayers():
		steps.append({"name": "without " + layer, "layer": layer})
	steps.append({"name": "msaa off", "msaa": Viewport.MSAA_DISABLED})
	steps.append({"name": "3D scale x0.75", "scale": scale_before * 0.75})
	steps.append({"name": "3D scale x0.5", "scale": scale_before * 0.5})
	steps.append({"name": "msaa off, 3D scale x0.75, no trees", "msaa": Viewport.MSAA_DISABLED, "scale": scale_before * 0.75, "layer": "trees"})
	var rows := []
	var index := 0
	var all_steps := []
	for step in steps:
		all_steps.append(step)
		if step["name"] != "baseline":
			all_steps.append({"name": "baseline", "again": true}) # between two steps, to show a drift of the machine
	steps = all_steps
	for step in steps:
		index += 1
		_status.text = "sweep %d/%d: %s" % [index, steps.size(), step["name"]]
		if step.has("layer"):
			host.PerfSetLayer(step["layer"], false)
		if step.has("msaa"):
			viewport.msaa_3d = step["msaa"]
		if step.has("scale"):
			viewport.scaling_3d_scale = step["scale"]
		await get_tree().create_timer(WARMUP_SECONDS).timeout
		_sweep_window = []
		await get_tree().create_timer(MEASURE_SECONDS).timeout
		var result := _summary(_sweep_window)
		result["name"] = step["name"]
		rows.append(result)
		_write({"type": "sweep_step", "step": step["name"], "stats": _round(result), "sections": _section_table(_last.get("sections", {})), "counts": host.PerfCounts(), "process": _round(_proc)})
		if step.has("layer"):
			host.PerfSetLayer(step["layer"], true)
		viewport.msaa_3d = msaa_before
		viewport.scaling_3d_scale = scale_before
	DisplayServer.window_set_vsync_mode(vsync_before)
	_sweeping = false
	var table := PackedStringArray(["SWEEP (vsync off; frame ms, change against the mean of the baselines on both sides, render CPU ms, draw calls)"])
	for i in rows.size():
		var r: Dictionary = rows[i]
		var near := []
		if i > 0:
			near.append(rows[i - 1]["frame_ms"])
		if i + 1 < rows.size():
			near.append(rows[i + 1]["frame_ms"])
		var base := 0.0
		for v in near:
			base += v
		base /= max(1, near.size())
		table.append("%-40s %6.1f ms  %6.0f fps  %+6.1f ms  render cpu %5.1f  calls %5.0f" % [r["name"], r["frame_ms"], r["fps"], r["frame_ms"] - base, r["render_cpu_ms"], r["draw_calls"]])
	_write({"type": "sweep_table", "table": Array(table)})
	print("\n".join(table))
	_status.text = "sweep done; log " + ProjectSettings.globalize_path(_log_path)
	if not was_logging:
		_stop_log()
		_log_check.set_pressed_no_signal(false)
	if args.has("--perf-sweep"):
		get_tree().quit()


# --- the panel ---

func _build() -> void:
	_panel = PanelContainer.new()
	_panel.position = Vector2(12, 96)
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.06, 0.07, 0.09, 0.88)
	style.set_content_margin_all(10)
	style.set_corner_radius_all(6)
	_panel.add_theme_stylebox_override("panel", style)
	add_child(_panel)
	var font := SystemFont.new()
	font.font_names = PackedStringArray(["Menlo", "Consolas", "DejaVu Sans Mono", "monospace"])
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 18)
	_panel.add_child(row)

	var left := VBoxContainer.new()
	row.add_child(left)
	var title := Label.new()
	title.text = "Performance (F10)"
	left.add_child(title)
	_stats = _mono(font)
	left.add_child(_stats)
	_sections = _mono(font)
	left.add_child(_sections)
	_status = _mono(font)
	left.add_child(_status)

	var right := VBoxContainer.new()
	row.add_child(right)
	var head := Label.new()
	head.text = "Switch off to see the cost"
	right.add_child(head)
	var grid := GridContainer.new()
	grid.columns = 2
	right.add_child(grid)
	if host != null:
		for name in host.PerfLayers():
			var check := CheckBox.new()
			check.text = name
			check.button_pressed = host.PerfLayerOn(name)
			check.toggled.connect(func(on: bool) -> void:
				host.PerfSetLayer(name, on)
				_event(name, on))
			grid.add_child(check)
			_checks[name] = check
	var all_row := HBoxContainer.new()
	right.add_child(all_row)
	for entry in [["All layers off", false], ["All on", true]]:
		var button := Button.new()
		button.text = entry[0]
		button.pressed.connect(func() -> void:
			for name in _checks:
				_checks[name].button_pressed = entry[1]) # each box logs its own switch
		all_row.add_child(button)
	_vsync_check = CheckBox.new()
	_vsync_check.text = "vsync"
	_vsync_check.button_pressed = DisplayServer.window_get_vsync_mode() != DisplayServer.VSYNC_DISABLED
	_vsync_check.toggled.connect(func(on: bool) -> void:
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED if on else DisplayServer.VSYNC_DISABLED)
		_event("vsync", on))
	right.add_child(_vsync_check)
	_msaa_option = OptionButton.new()
	for item in ["MSAA off", "MSAA 2x", "MSAA 4x", "MSAA 8x"]:
		_msaa_option.add_item(item)
	_msaa_option.select(get_viewport().msaa_3d)
	_msaa_option.item_selected.connect(func(i: int) -> void:
		get_viewport().msaa_3d = i as Viewport.MSAA
		_event("msaa_3d", i))
	right.add_child(_msaa_option)
	_scale_option = OptionButton.new()
	for item in SCALES:
		_scale_option.add_item("3D scale %d %%" % int(item * 100))
	_scale_option.select(SCALES.find(get_viewport().scaling_3d_scale) if SCALES.has(get_viewport().scaling_3d_scale) else 0)
	_scale_option.item_selected.connect(func(i: int) -> void:
		get_viewport().scaling_3d_scale = SCALES[i]
		_event("scaling_3d_scale", SCALES[i]))
	right.add_child(_scale_option)
	_view_option = OptionButton.new()
	for item in DEBUG_VIEWS:
		_view_option.add_item(item[0])
	_view_option.item_selected.connect(func(i: int) -> void:
		get_viewport().debug_draw = DEBUG_VIEWS[i][1] as Viewport.DebugDraw
		_event("debug_draw", DEBUG_VIEWS[i][0]))
	right.add_child(_view_option)
	var shadow_option := OptionButton.new()
	for item in ["Shadows off", "Shadows low", "Shadows high"]:
		shadow_option.add_item(item)
	shadow_option.select(Settings.shadows)
	shadow_option.item_selected.connect(func(i: int) -> void:
		Settings.shadows = i # not saved: Settings.save() is what writes the file
		Settings.changed.emit()
		_event("shadows", i))
	right.add_child(shadow_option)
	var ssao_check := CheckBox.new()
	ssao_check.text = "ambient occlusion"
	ssao_check.button_pressed = Settings.ssao
	ssao_check.toggled.connect(func(on: bool) -> void:
		Settings.ssao = on
		Settings.changed.emit()
		_event("ssao", on))
	right.add_child(ssao_check)
	for entry in [["fog", "fog_enabled"], ["grading", "adjustment_enabled"]]:
		var check := CheckBox.new()
		check.text = entry[0]
		check.button_pressed = true
		check.toggled.connect(func(on: bool) -> void:
			var lighting = _lighting()
			if lighting != null:
				lighting.environment.set(entry[1], on)
			_event(entry[0], on))
		right.add_child(check)

	_log_check = CheckBox.new()
	_log_check.text = "Log to file"
	_log_check.toggled.connect(func(on: bool) -> void:
		if on:
			_start_log()
		else:
			_stop_log())
	right.add_child(_log_check)
	var mark := Button.new()
	mark.text = "Mark in the log"
	mark.pressed.connect(func() -> void: _write({"type": "mark", "toggles": _toggles(), "camera": _camera()}))
	right.add_child(mark)
	var sweep := Button.new()
	sweep.text = "Run the sweep (3 min)"
	sweep.pressed.connect(run_sweep)
	right.add_child(sweep)
	var folder := Button.new()
	folder.text = "Show the log folder"
	folder.pressed.connect(func() -> void:
		DirAccess.make_dir_recursive_absolute("user://perf")
		OS.shell_show_in_file_manager(ProjectSettings.globalize_path("user://perf")))
	right.add_child(folder)
	_log_note = _mono(font)
	_log_note.custom_minimum_size.x = 260
	_log_note.autowrap_mode = TextServer.AUTOWRAP_ARBITRARY
	right.add_child(_log_note)


func _mono(font: Font) -> Label:
	var label := Label.new()
	label.add_theme_font_override("font", font)
	label.add_theme_font_size_override("font_size", 12)
	return label
