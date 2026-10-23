extends "res://game/ui/page.gd"
## Region > Power: what the industries ask for, what the grid, the plants and the power bought from outside give, and the buttons to buy more.

var _short: Control
var _demand: Dictionary
var _supply: Dictionary
var _balance: Dictionary
var _grid_note: Label
var _purchase: Label
var _purchase_note: Label
var _less: Button
var _more: Button
var _purchased := 0
var _step := 5
var _max := 0


func _build() -> void:
	add_child(Kit.note("Every industry draws power. A shortage slows all of them by the same share. Power plants burn coal; the rest can be bought from outside by the month. The Power map view shows who gets what."))
	_short = Kit.notice("Short of power", "")
	add_child(_short)
	var figures := Kit.row()
	_demand = Kit.stat("Demand")
	_supply = Kit.stat("Supply")
	figures.add_child(_demand["box"])
	figures.add_child(_supply["box"])
	add_child(figures)
	_balance = Kit.meter("Supply against demand", UiTheme.GOOD)
	add_child(_balance["box"])
	_grid_note = Kit.note("")
	add_child(_grid_note)
	add_child(Kit.section("Bought from outside"))
	var stepper := Kit.row()
	_less = Kit.button("−", func() -> void: hud.power_purchase_set.emit(maxi(0, _purchased - _step)))
	_less.custom_minimum_size = Vector2(48, 0)
	stepper.add_child(_less)
	_purchase = Label.new()
	_purchase.theme_type_variation = "Stat"
	_purchase.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_purchase.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	stepper.add_child(_purchase)
	_more = Kit.button("+", func() -> void: hud.power_purchase_set.emit(mini(_max, _purchased + _step)))
	_more.custom_minimum_size = Vector2(48, 0)
	stepper.add_child(_more)
	add_child(stepper)
	_purchase_note = Kit.note("")
	add_child(_purchase_note)


func show_power(power: Dictionary) -> void:
	_purchased = power["purchased"]
	_step = power["step"]
	_max = power["max_purchase"]
	var factor: float = power["factor"]
	_short.visible = factor < 0.995
	if _short.visible:
		_short.get_child(0).get_child(1).text = "The industries make %d%% of their output." % roundi(factor * 100.0)
	_demand["value"].text = "%d MW" % roundi(power["demand"])
	_supply["value"].text = "%d MW" % roundi(power["supply"])
	var share := 100.0 if power["demand"] <= 0.0 else minf(100.0, 100.0 * power["supply"] / power["demand"])
	_balance["value"].text = "%d%%" % roundi(share)
	_balance["bar"].value = share
	_balance["fill"].bg_color = UiTheme.WARN if factor < 0.995 else UiTheme.GOOD
	_grid_note.text = "Free grid %d MW · %d of %d power plants burning" % [roundi(power["free_grid"]), power["plants_burning"], power["plants"]]
	_purchase.text = "%d MW a month" % _purchased
	_purchase_note.text = "%s a megawatt and month · cost %s · up to %d MW" % [Kit.money(power["price"]), Kit.money(power["cost"]), _max]
	_less.tooltip_text = "Buy %d MW less" % _step
	_more.tooltip_text = "Buy %d MW more" % _step
	_less.disabled = _purchased <= 0
	_more.disabled = _purchased >= _max
