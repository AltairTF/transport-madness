extends "res://game/ui/page.gd"
## Company > Requisition: what the army has taken and will take, and in the month of notice the rule it takes by.

const RULE_NAMES := ["Freight first", "The oldest first", "Passengers first"]

var _details: Label
var _rule_row: HBoxContainer
var _rule_note: Label
var _rule_buttons: Array[Button] = []


func _build() -> void:
	_details = Label.new()
	_details.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(_details)
	_rule_row = Kit.row()
	add_child(_rule_row)
	var caption := Label.new()
	caption.text = "The army takes:"
	_rule_row.add_child(caption)
	for rule in RULE_NAMES.size():
		var b := Kit.button(RULE_NAMES[rule], func() -> void: hud.requisition_rule_set.emit(rule))
		b.toggle_mode = true
		_rule_row.add_child(b)
		_rule_buttons.append(b)
	_rule_note = Kit.note("The rule of a requisition can be chosen in the month before it, when the army gives notice.")
	add_child(_rule_note)


func show_company(company: Dictionary) -> void:
	var text := "The army has taken %d vehicles and trains (paid %s)." % [company["requisitioned"], Kit.money(company["requisition_income"])]
	if company["next_requisition_month"] >= 0:
		text += "\nNext: %s %d, %d%% of the fleet, about %d now, %s." % [Kit.MONTHS[company["next_requisition_month"] % 12],
			company["year"] - floori(company["phase_month"] / 12.0) + floori(company["next_requisition_month"] / 12.0), roundi(company["next_requisition_share"] * 100.0),
			company["expected_requisition"], company["requisition_rule_text"]]
	else:
		text += "\nNo more requisitions are planned."
	_details.text = text
	_rule_row.visible = company["can_choose_rule"]
	_rule_note.visible = not company["can_choose_rule"]
	for rule in _rule_buttons.size():
		_rule_buttons[rule].set_pressed_no_signal(rule == company["requisition_rule"])
