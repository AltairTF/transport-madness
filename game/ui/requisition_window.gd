extends "res://game/ui/page.gd"
## Company > Requisition: what the army has taken and will take, and in the month of notice the rule it takes by.

const RULE_NAMES := ["Freight first", "The oldest first", "Passengers first"]

var _taken: Dictionary
var _paid: Dictionary
var _next: Label
var _rule_box: VBoxContainer
var _rule_note: Label
var _rule_buttons: Array[Button] = []


func _build() -> void:
	var figures := Kit.row()
	_taken = Kit.stat("Taken so far")
	_paid = Kit.stat("Paid")
	figures.add_child(_taken["box"])
	figures.add_child(_paid["box"])
	add_child(figures)
	_next = Label.new()
	_next.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(_next)
	_rule_box = Kit.column()
	_rule_box.add_child(Kit.section("The army takes"))
	for rule in RULE_NAMES.size():
		var b := Kit.button(RULE_NAMES[rule], func() -> void: hud.requisition_rule_set.emit(rule))
		b.toggle_mode = true
		_rule_box.add_child(b)
		_rule_buttons.append(b)
	add_child(_rule_box)
	_rule_note = Kit.note("The rule of a requisition can be chosen in the month before it, when the army gives notice.")
	add_child(_rule_note)


func show_company(company: Dictionary) -> void:
	_taken["value"].text = "%d vehicles" % company["requisitioned"]
	_paid["value"].text = Kit.money(company["requisition_income"])
	if company["next_requisition_month"] >= 0:
		_next.text = "Next: %s %d, %d%% of the fleet, about %d now, %s." % [Kit.MONTHS[company["next_requisition_month"] % 12],
			company["year"] - floori(company["phase_month"] / 12.0) + floori(company["next_requisition_month"] / 12.0), roundi(company["next_requisition_share"] * 100.0),
			company["expected_requisition"], company["requisition_rule_text"]]
	else:
		_next.text = "No more requisitions are planned."
	_rule_box.visible = company["can_choose_rule"]
	_rule_note.visible = not company["can_choose_rule"]
	for rule in _rule_buttons.size():
		_rule_buttons[rule].set_pressed_no_signal(rule == company["requisition_rule"])
