extends Node
## User settings: read from user://settings.cfg at startup, applied to the window and the audio, saved when changed.
## Sound is not in the game yet; the music and effects volumes are stored for when it is.

signal changed

const PATH := "user://settings.cfg"
const RESOLUTIONS: Array[Vector2i] = [
	Vector2i(960, 540), Vector2i(1024, 576), Vector2i(1152, 648), Vector2i(1280, 720), Vector2i(1280, 800), Vector2i(1280, 960),
	Vector2i(1280, 1024), Vector2i(1360, 768), Vector2i(1366, 768), Vector2i(1400, 1050), Vector2i(1440, 900), Vector2i(1536, 864),
	Vector2i(1600, 900), Vector2i(1600, 1024), Vector2i(1600, 1200), Vector2i(1680, 1050), Vector2i(1792, 1008), Vector2i(1920, 1080),
	Vector2i(1920, 1200), Vector2i(1920, 1440), Vector2i(2048, 1152), Vector2i(2240, 1260), Vector2i(2304, 1440), Vector2i(2560, 1080),
	Vector2i(2560, 1440), Vector2i(2560, 1600), Vector2i(2880, 1620), Vector2i(3200, 1800), Vector2i(3440, 1440), Vector2i(3840, 1600),
	Vector2i(3840, 2160), Vector2i(5120, 1440)]
const MIN_WINDOW := Vector2i(960, 540)
const UI_SCALES: Array[float] = [0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.8, 2.0] ## the player's interface size: 100 % is the size of the interface of a 1920 x 1080 screen, whatever the window
const RENDER_SCALES: Array[float] = [1.0, 0.85, 0.75, 0.66, 0.5]
const AUTO_RENDER_PIXELS := 8.0e6 ## automatic 3D size: a window with more pixels than this is drawn smaller and scaled up (the performance log of 2026-10-22: 4471 x 2515 took 16.5 ms, 0.85 took 14.6)
const REFERENCE_HEIGHT := 1080.0
const WINDOW_FILL := 0.92 ## share of the free screen area a first-run window may take (the title bar needs room)

var fullscreen := false
var resolution := Vector2i.ZERO ## windowed size in logical points (a pixel size on a screen with no scaling); zero until chosen on first run
var vsync := true
var ui_scale := 1.0
var msaa := Viewport.MSAA_4X
var render_scale := 0.0 ## the 3D picture's size against the window: 0 is automatic (about 8 million pixels at most), or one of RENDER_SCALES
var shadows := 1 ## 0 off, 1 low, 2 high (lighting.gd)
var ssao := false ## ambient occlusion in the screen: dearer than it looks at this size, so off until chosen
var master_volume := 1.0
var music_volume := 0.8
var effects_volume := 0.8
var window_rects: Dictionary = {} ## where each kind of window of the HUD was left: kind -> Rect2 (M6d)
var key_bindings: Dictionary = {} ## keys the player bound: action -> {"key", "shift"}; the rest keep their defaults (keys.gd)


func _ready() -> void:
	_load()
	preload("res://game/keys.gd").apply(key_bindings)
	if resolution == Vector2i.ZERO:
		resolution = default_resolution()
	# A command line run (self-tests, benchmark) keeps the project's own window so its numbers stay comparable.
	if OS.get_cmdline_user_args().is_empty():
		apply()
		get_window().size_changed.connect(apply_render_scale)


## The scale of the interface in window pixels: the player's choice times the display's own scale (2 on a retina screen), so a bigger window shows more of the map instead of bigger buttons.
func interface_factor() -> float:
	return ui_scale * _screen_scale()


## How much a label in the world (a town's name) is scaled to be as big on the screen as the interface text of the same size: its size follows the height of the window, so it is
## brought back to a 1080 pixel high one and then by the interface factor.
func label_scale() -> float:
	var height := maxf(1.0, float(get_window().size.y))
	return REFERENCE_HEIGHT * get_window().content_scale_factor / height


## Windowed sizes that fit the free area of the screen the window is on, always including the current one.
func available_resolutions() -> Array[Vector2i]:
	var limit := _usable_area().size
	var out: Array[Vector2i] = []
	for candidate in RESOLUTIONS:
		if candidate.x <= limit.x and candidate.y <= limit.y:
			out.append(candidate)
	if not resolution in out:
		out.append(resolution)
		out.sort_custom(func(a: Vector2i, b: Vector2i) -> bool: return a.x < b.x or (a.x == b.x and a.y < b.y))
	return out


## The listed size with the most pixels that fits comfortably on this screen.
func default_resolution() -> Vector2i:
	var limit := Vector2(_usable_area().size) * WINDOW_FILL
	var best := RESOLUTIONS[0]
	for candidate in RESOLUTIONS:
		if candidate.x <= limit.x and candidate.y <= limit.y and candidate.x * candidate.y > best.x * best.y:
			best = candidate
	return best


func apply() -> void:
	var window := get_window()
	window.min_size = MIN_WINDOW
	window.content_scale_mode = Window.CONTENT_SCALE_MODE_DISABLED
	window.content_scale_factor = interface_factor()
	window.msaa_3d = msaa as Viewport.MSAA
	apply_render_scale()
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED if vsync else DisplayServer.VSYNC_DISABLED)
	AudioServer.set_bus_volume_db(0, linear_to_db(maxf(master_volume, 0.0001)))
	if fullscreen:
		window.mode = Window.MODE_FULLSCREEN
	else:
		window.mode = Window.MODE_WINDOWED
		var pixels := Vector2i(Vector2(resolution) * _screen_scale())
		window.size = pixels
		var area := _usable_pixels()
		window.position = area.position + (area.size - pixels) / 2
	changed.emit()


## The size the 3D picture is drawn at, as a share of the window: the chosen one, or for Automatic the largest that stays within AUTO_RENDER_PIXELS.
func effective_render_scale() -> float:
	if render_scale > 0.0:
		return render_scale
	var size := get_window().size
	return clampf(sqrt(AUTO_RENDER_PIXELS / maxf(1.0, float(size.x) * float(size.y))), 0.5, 1.0)


func apply_render_scale() -> void:
	var window := get_window()
	var scale := effective_render_scale()
	window.scaling_3d_mode = Viewport.SCALING_3D_MODE_FSR if scale < 0.99 else Viewport.SCALING_3D_MODE_BILINEAR
	window.scaling_3d_scale = scale


func save() -> void:
	var file := ConfigFile.new()
	file.set_value("display", "fullscreen", fullscreen)
	file.set_value("display", "resolution", resolution)
	file.set_value("display", "vsync", vsync)
	file.set_value("display", "ui_scale", ui_scale)
	file.set_value("display", "msaa", msaa)
	file.set_value("display", "render_scale", render_scale)
	file.set_value("display", "shadows", shadows)
	file.set_value("display", "ssao", ssao)
	file.set_value("audio", "master", master_volume)
	file.set_value("audio", "music", music_volume)
	file.set_value("audio", "effects", effects_volume)
	file.set_value("interface", "windows", window_rects)
	file.set_value("interface", "keys", key_bindings)
	file.save(PATH)


func _load() -> void:
	var file := ConfigFile.new()
	if file.load(PATH) != OK:
		return
	fullscreen = file.get_value("display", "fullscreen", fullscreen)
	resolution = file.get_value("display", "resolution", resolution)
	vsync = file.get_value("display", "vsync", vsync)
	ui_scale = _nearest_scale(float(file.get_value("display", "ui_scale", ui_scale)))
	msaa = file.get_value("display", "msaa", msaa)
	render_scale = float(file.get_value("display", "render_scale", render_scale))
	shadows = clampi(int(file.get_value("display", "shadows", shadows)), 0, 2)
	ssao = bool(file.get_value("display", "ssao", ssao))
	master_volume = file.get_value("audio", "master", master_volume)
	music_volume = file.get_value("audio", "music", music_volume)
	effects_volume = file.get_value("audio", "effects", effects_volume)
	window_rects = file.get_value("interface", "windows", window_rects)
	key_bindings = file.get_value("interface", "keys", key_bindings)
	if resolution.x < MIN_WINDOW.x or resolution.y < MIN_WINDOW.y:
		resolution = Vector2i.ZERO


func _nearest_scale(value: float) -> float:
	var best := 1.0
	for step in UI_SCALES:
		if absf(step - value) < absf(best - value):
			best = step
	return best


## The free area of the screen in logical points: on a scaled (retina) screen the window size is in pixels,
## so sizes are kept in points and multiplied by the scale when applied.
func _usable_area() -> Rect2i:
	return Rect2i(Vector2i(Vector2(_usable_pixels().position) / _screen_scale()), Vector2i(Vector2(_usable_pixels().size) / _screen_scale()))


func _usable_pixels() -> Rect2i:
	return DisplayServer.screen_get_usable_rect(DisplayServer.window_get_current_screen())


func _screen_scale() -> float:
	return maxf(1.0, DisplayServer.screen_get_scale(DisplayServer.window_get_current_screen()))
