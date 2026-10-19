extends Node
## User settings: read from user://settings.cfg at startup, applied to the window and the audio, saved when changed.
## Sound is not in the game yet; the music and effects volumes are stored for when it is.

signal changed

const PATH := "user://settings.cfg"
const RESOLUTIONS: Array[Vector2i] = [
	Vector2i(1024, 576), Vector2i(1152, 648), Vector2i(1280, 720), Vector2i(1280, 800), Vector2i(1366, 768),
	Vector2i(1440, 900), Vector2i(1600, 900), Vector2i(1680, 1050), Vector2i(1920, 1080), Vector2i(1920, 1200),
	Vector2i(2048, 1152), Vector2i(2560, 1080), Vector2i(2560, 1440), Vector2i(2560, 1600), Vector2i(3440, 1440),
	Vector2i(3840, 2160)]
const MIN_WINDOW := Vector2i(960, 540)
const UI_SCALES: Array[float] = [0.75, 1.0, 1.25, 1.5]
const WINDOW_FILL := 0.92 ## share of the free screen area a first-run window may take (the title bar needs room)

var fullscreen := false
var resolution := Vector2i.ZERO ## windowed size in logical points (a pixel size on a screen with no scaling); zero until chosen on first run
var vsync := true
var ui_scale := 1.0
var msaa := Viewport.MSAA_4X
var master_volume := 1.0
var music_volume := 0.8
var effects_volume := 0.8


func _ready() -> void:
	_load()
	if resolution == Vector2i.ZERO:
		resolution = default_resolution()
	# A command line run (self-tests, benchmark) keeps the project's own window so its numbers stay comparable.
	if OS.get_cmdline_user_args().is_empty():
		apply()


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
	window.content_scale_factor = ui_scale
	window.msaa_3d = msaa as Viewport.MSAA
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


func save() -> void:
	var file := ConfigFile.new()
	file.set_value("display", "fullscreen", fullscreen)
	file.set_value("display", "resolution", resolution)
	file.set_value("display", "vsync", vsync)
	file.set_value("display", "ui_scale", ui_scale)
	file.set_value("display", "msaa", msaa)
	file.set_value("audio", "master", master_volume)
	file.set_value("audio", "music", music_volume)
	file.set_value("audio", "effects", effects_volume)
	file.save(PATH)


func _load() -> void:
	var file := ConfigFile.new()
	if file.load(PATH) != OK:
		return
	fullscreen = file.get_value("display", "fullscreen", fullscreen)
	resolution = file.get_value("display", "resolution", resolution)
	vsync = file.get_value("display", "vsync", vsync)
	ui_scale = file.get_value("display", "ui_scale", ui_scale)
	msaa = file.get_value("display", "msaa", msaa)
	master_volume = file.get_value("audio", "master", master_volume)
	music_volume = file.get_value("audio", "music", music_volume)
	effects_volume = file.get_value("audio", "effects", effects_volume)
	if resolution.x < MIN_WINDOW.x or resolution.y < MIN_WINDOW.y:
		resolution = Vector2i.ZERO


## The free area of the screen in logical points: on a scaled (retina) screen the window size is in pixels,
## so sizes are kept in points and multiplied by the scale when applied.
func _usable_area() -> Rect2i:
	return Rect2i(Vector2i(Vector2(_usable_pixels().position) / _screen_scale()), Vector2i(Vector2(_usable_pixels().size) / _screen_scale()))


func _usable_pixels() -> Rect2i:
	return DisplayServer.screen_get_usable_rect(DisplayServer.window_get_current_screen())


func _screen_scale() -> float:
	return maxf(1.0, DisplayServer.screen_get_scale(DisplayServer.window_get_current_screen()))
