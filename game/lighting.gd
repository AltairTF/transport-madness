extends Node
## The lit scene (M7f, docs/m7-plan.md): a sun that follows the time of day (a day is 1200 s at normal speed), a dim moon so the night is dark but readable,
## the sky and ambient colours, mild fog, tone mapping and grading, and the shadow and SSAO switches of Settings.
## main.gd creates it and calls set_hour() with the hour of the simulation (0 to 24); it never touches the simulation.

const SUNRISE := 6.0
const SUNSET := 19.0
const NOON_ELEVATION := 62.0 ## degrees; there are no seasons yet
const SUN_AZIMUTH_RISE := -105.0 ## degrees from the south, towards the east; the sun climbs in the east, is south at noon, sets in the west
const SUN_AZIMUTH_SET := 105.0
const SHADOW_LOW := {"atlas": 2048, "distance": 450.0, "splits": DirectionalLight3D.SHADOW_PARALLEL_2_SPLITS}
const SHADOW_HIGH := {"atlas": 4096, "distance": 900.0, "splits": DirectionalLight3D.SHADOW_PARALLEL_4_SPLITS}

# Light colours by the height of the sun: x is the elevation in degrees.
const SUN_COLORS: Array = [
	[0.0, Color(1.0, 0.50, 0.28)], [8.0, Color(1.0, 0.66, 0.40)], [20.0, Color(1.0, 0.84, 0.66)], [45.0, Color(1.0, 0.94, 0.84)]]
# Sky (the background and the ambient light) by the height of the sun; below the horizon it falls to the night colour.
const SKY_COLORS: Array = [
	[-8.0, Color(0.05, 0.07, 0.14)], [0.0, Color(0.42, 0.34, 0.40)], [8.0, Color(0.66, 0.66, 0.72)], [25.0, Color(0.62, 0.74, 0.86)], [60.0, Color(0.58, 0.72, 0.88)]]
const AMBIENT_ENERGY_NIGHT := 0.42
const AMBIENT_ENERGY_DAY := 0.5

var hour := 12.0
var environment: Environment
var sun: DirectionalLight3D
var moon: DirectionalLight3D


func _ready() -> void:
	environment = Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.tonemap_mode = Environment.TONE_MAPPER_ACES
	environment.tonemap_exposure = 1.0
	environment.tonemap_white = 6.0
	# Slightly desaturated and a little contrast: the target look is earthy, not a plain game palette (docs/style-guide.md).
	environment.adjustment_enabled = true
	environment.adjustment_saturation = 0.92
	environment.adjustment_contrast = 1.04
	environment.fog_enabled = true
	environment.fog_density = 0.00006 ## about 10 % of the colour at 1900 m, the farthest the camera sits
	environment.fog_aerial_perspective = 0.0
	environment.fog_sky_affect = 0.0
	environment.ssao_radius = 6.0
	environment.ssao_intensity = 1.3
	environment.ssao_power = 1.6
	environment.ssao_detail = 0.4
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	add_child(world_environment)

	sun = DirectionalLight3D.new()
	sun.name = "Sun"
	sun.shadow_normal_bias = 1.5
	sun.shadow_bias = 0.08
	sun.shadow_blur = 1.2
	sun.directional_shadow_blend_splits = true
	add_child(sun)

	moon = DirectionalLight3D.new()
	moon.name = "Moon"
	moon.light_color = Color(0.55, 0.65, 1.0)
	moon.rotation_degrees = Vector3(-48, 35, 0)
	add_child(moon)

	Settings.changed.connect(apply_quality)
	apply_quality()
	set_hour(hour)


## The shadow and SSAO choices of Settings (shadows 0 off, 1 low, 2 high).
func apply_quality() -> void:
	var level: int = Settings.shadows
	sun.shadow_enabled = level > 0
	if level > 0:
		var quality: Dictionary = SHADOW_HIGH if level >= 2 else SHADOW_LOW
		RenderingServer.directional_shadow_atlas_set_size(quality["atlas"], true)
		RenderingServer.directional_soft_shadow_filter_set_quality(
			RenderingServer.SHADOW_QUALITY_SOFT_HIGH if level >= 2 else RenderingServer.SHADOW_QUALITY_SOFT_LOW)
		sun.directional_shadow_max_distance = quality["distance"]
		sun.directional_shadow_mode = quality["splits"]
		sun.directional_shadow_split_1 = 0.08
		sun.directional_shadow_split_2 = 0.25
		sun.directional_shadow_split_3 = 0.6
	environment.ssao_enabled = Settings.ssao


## Puts the sun, the moon, the sky and the fog where `h` (hours since midnight) says.
func set_hour(h: float) -> void:
	hour = fposmod(h, 24.0)
	var elevation := sun_elevation(hour)
	var aim := maxf(elevation, 4.0) # the light never points along the ground: the longest shadows are those of 4 degrees
	var azimuth := lerpf(SUN_AZIMUTH_RISE, SUN_AZIMUTH_SET, clampf((hour - SUNRISE) / (SUNSET - SUNRISE), 0.0, 1.0))
	var toward_sun := Vector3(
		cos(deg_to_rad(aim)) * sin(deg_to_rad(azimuth)),
		sin(deg_to_rad(aim)),
		cos(deg_to_rad(aim)) * cos(deg_to_rad(azimuth)))
	# The sun is a light only above the horizon.
	var lit := elevation > 0.0
	sun.visible = lit
	if lit:
		sun.basis = Basis.looking_at(-toward_sun.normalized(), Vector3.UP)
		sun.light_color = _ramp(SUN_COLORS, elevation)
		sun.light_energy = lerpf(0.15, 1.0, smoothstep(0.0, 28.0, elevation))
	var night := 1.0 - smoothstep(-6.0, 4.0, elevation)
	moon.visible = night > 0.02
	moon.light_energy = 0.28 * night
	var sky: Color = _ramp(SKY_COLORS, elevation)
	environment.background_color = sky
	environment.fog_light_color = sky.lerp(Color(0.8, 0.78, 0.74), 0.25)
	environment.ambient_light_color = sky.lerp(Color(1, 1, 1), 0.2)
	environment.ambient_light_energy = lerpf(AMBIENT_ENERGY_NIGHT, AMBIENT_ENERGY_DAY, smoothstep(-4.0, 20.0, elevation))


## The height of the sun above the horizon in degrees: a half sine between sunrise and sunset, negative (down to -30) at night.
static func sun_elevation(h: float) -> float:
	if h >= SUNRISE and h <= SUNSET:
		return NOON_ELEVATION * sin(PI * (h - SUNRISE) / (SUNSET - SUNRISE))
	var since := (h - SUNSET) if h > SUNSET else (h + 24.0 - SUNSET)
	return -30.0 * sin(PI * since / (24.0 - (SUNSET - SUNRISE)))


## A colour read from a list of [elevation, colour] steps, blended between the two steps around `x`.
static func _ramp(steps: Array, x: float) -> Color:
	if x <= steps[0][0]:
		return steps[0][1]
	for i in range(1, steps.size()):
		if x <= steps[i][0]:
			var a: Array = steps[i - 1]
			var b: Array = steps[i]
			return (a[1] as Color).lerp(b[1], inverse_lerp(a[0], b[0], x))
	return steps[steps.size() - 1][1]
