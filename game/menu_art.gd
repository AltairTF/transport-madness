extends Control
## The picture beside the main menu: faint contour lines and a dotted route joining three stops.

const UiTheme := preload("res://game/ui_theme.gd")
const CONTOURS := 6
## Stops and route as fractions of the area: bottom left to top right.
const STOPS := [Vector2(0.12, 1.0), Vector2(0.40, 0.62), Vector2(0.78, 0.18)]
const STOP_COLORS := [Color("f2b84b"), Color("5fd4b0"), Color("86b4ff")]


func _draw() -> void:
	var w := size.x
	var h := size.y
	for k in CONTOURS:
		var points := PackedVector2Array()
		var base := h * (0.18 + 0.13 * k)
		for i in 49:
			var x := w * i / 48.0
			points.append(Vector2(x, base + sin(x / w * TAU * 0.9 + k * 0.8) * h * 0.05 + cos(x / w * TAU * 1.7 + k) * h * 0.02))
		draw_polyline(points, UiTheme.BORDER, 1.5, true)
	var a: Vector2 = STOPS[0] * size
	var c: Vector2 = STOPS[2] * size
	var length := a.distance_to(c)
	var step := 14.0
	for i in int(length / step):
		draw_circle(a.lerp(c, i * step / length), 2.0, Color("3b78ea"))
	for i in STOPS.size():
		var p: Vector2 = STOPS[i] * size
		draw_circle(p, 11.0, UiTheme.PANEL.darkened(0.2))
		draw_arc(p, 11.0, 0.0, TAU, 32, STOP_COLORS[i], 2.5, true)


func _notification(what: int) -> void:
	if what == NOTIFICATION_RESIZED:
		queue_redraw()
