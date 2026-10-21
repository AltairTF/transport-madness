extends "res://game/ui/page.gd"
## Region > Power: what the industries ask for, what the grid, the plants and the power bought from outside give, and the buttons to buy more.

var _label: Label
var _less: Button
var _more: Button
var _purchased := 0
var _step := 5
var _max := 0


func _build() -> void:
	_label = Label.new()
	_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(_label)
	var buttons := Kit.flow()
	add_child(buttons)
	_less = Kit.button("", func() -> void: hud.power_purchase_set.emit(maxi(0, _purchased - _step)))
	buttons.add_child(_less)
	_more = Kit.button("", func() -> void: hud.power_purchase_set.emit(mini(_max, _purchased + _step)))
	buttons.add_child(_more)
	add_child(Kit.note("A shortage slows every industry of the region by the same share. Power plants burn coal; the Power map view shows who gets what."))


func show_power(power: Dictionary) -> void:
	_purchased = power["purchased"]
	_step = power["step"]
	_max = power["max_purchase"]
	var text := "Supply %d MW   demand %d MW" % [roundi(power["supply"]), roundi(power["demand"])]
	if power["factor"] < 0.995:
		text += "\nSHORT: the industries make %d%% of their output" % roundi(power["factor"] * 100.0)
	text += "\nFree grid %d MW\n%d of %d power plants burning\nBought from outside %d MW (%s a month)" % [
		roundi(power["free_grid"]), power["plants_burning"], power["plants"], power["purchased"], Kit.money(power["cost"])]
	_label.text = text
	_label.add_theme_color_override("font_color", Color(1.0, 0.7, 0.35) if power["factor"] < 0.995 else UiTheme.TEXT)
	_less.text = "Buy %d MW less" % _step
	_less.disabled = _purchased <= 0
	_more.text = "Buy %d MW more (%s a month each)" % [_step, Kit.money(power["price"])]
	_more.disabled = _purchased >= _max
