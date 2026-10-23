extends "res://game/ui/page.gd"
## Government > Standing: how the government judges the committee, what last month did to it, its history, and the war's toll.

const HISTORY_MONTHS := 24

var _now: Label
var _trend: Label
var _meter: Dictionary
var _dismissal: Control
var _terms: VBoxContainer
var _orders: Label
var _war: Label
var _history: HBoxContainer
var _history_key := ""


func _build() -> void:
	add_child(Kit.note("How the government judges the committee, 0 to 100. Too long under the line and the ministry replaces you."))
	var card := Kit.card()
	var body: VBoxContainer = card.get_meta("body")
	var head := Kit.row()
	_now = Label.new()
	_now.theme_type_variation = "BigNumber"
	head.add_child(_now)
	_trend = Kit.note("")
	_trend.size_flags_vertical = Control.SIZE_SHRINK_END
	head.add_child(_trend)
	body.add_child(head)
	_meter = Kit.meter("Allocation factor", Color("9cc2ff"))
	body.add_child(_meter["box"])
	_dismissal = Kit.notice("Dismissal", "")
	body.add_child(_dismissal)
	add_child(card)
	add_child(Kit.section("What moved it last month"))
	_terms = Kit.column()
	add_child(_terms)
	add_child(Kit.section("Orders"))
	_orders = Kit.note("")
	add_child(_orders)
	add_child(Kit.section("The war"))
	_war = Kit.note("")
	add_child(_war)
	add_child(Kit.section("The last %d months" % HISTORY_MONTHS))
	_history = HBoxContainer.new()
	_history.add_theme_constant_override("separation", 2)
	_history.custom_minimum_size = Vector2(0, 90)
	add_child(_history)


func _term(label: String, value: String, note := "") -> Control:
	var row := Kit.row()
	var text := Kit.column()
	text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var name := Label.new()
	name.text = label
	text.add_child(name)
	if note != "":
		text.add_child(Kit.note(note))
	row.add_child(text)
	var figure := Label.new()
	figure.text = value
	row.add_child(figure)
	return row


func show_company(company: Dictionary) -> void:
	var standing: float = company["standing"]
	_now.text = str(roundi(standing))
	var last: Dictionary = company["last"]
	_trend.text = "%+d since last month" % (roundi(last["standing_after"]) - roundi(last["standing_before"])) if not last.is_empty() else ""
	_meter["caption"].text = "Allocation factor x%.2f" % company["allocation_factor"]
	_meter["value"].text = "%d of 100" % roundi(standing)
	_meter["bar"].value = standing
	_dismissal.visible = company["months_to_dismissal"] > 0
	if _dismissal.visible:
		_dismissal.get_child(0).get_child(1).text = "Under %d: %d more months and the committee is dismissed." % [roundi(company["dismiss_below"]), company["months_to_dismissal"]]
	for child in _terms.get_children():
		child.queue_free()
	if last.is_empty():
		_terms.add_child(Kit.note("No month has ended yet."))
	else:
		_terms.add_child(_term("Deliveries", "%+.1f" % last["export_term"], "%s delivered to the gateways this month, the phase expects %s" % [Kit.money(company["export_value"]), Kit.money(company["expected_value"])]))
		_terms.add_child(_term("Deficit", "%+.1f" % last["deficit_term"]))
		_terms.add_child(_term("Fines", "%+.1f" % last["fine_term"]))
		_terms.add_child(_term("Allocation", Kit.money(last["allocation"])))
	_orders.text = "%d met, %d late, %d failed (%d under way, %d on offer). Rewards so far %s." % [company["orders_met"], company["orders_late"], company["orders_failed"], company["orders_active"], company["orders_open"], Kit.money(company["orders_income"])]
	var war := ""
	if company["raids"] > 0:
		war += "%d flights so far, %d bombs, %d casualties." % [company["raids"], company["bombs"], company["casualties"]]
	if company["called_up"] > 0 or company["next_call_month"] >= 0:
		war += ("\n" if war != "" else "") + "%d people called up so far, workforce %d%% short (industry at %d%%)." % [company["called_up"], roundi(company["workforce_gap"] * 100.0), roundi(company["workforce_factor"] * 100.0)]
		if company["next_call_month"] >= 0:
			war += " Next call-up in %s %d." % [Kit.MONTHS[company["next_call_month"] % 12], company["year"] - floori(company["phase_month"] / 12.0) + floori(company["next_call_month"] / 12.0)]
	_war.text = war if war != "" else "No raids, no call-ups yet."
	_show_history(company.get("standing_history", []), company["dismiss_below"])


## One bar a month, red under the line of dismissal.
func _show_history(history: Array, line: float) -> void:
	var shown: Array = history.slice(maxi(0, history.size() - HISTORY_MONTHS))
	var key := str(shown)
	if key == _history_key:
		return
	_history_key = key
	for child in _history.get_children():
		child.queue_free()
	if shown.is_empty():
		var none := Kit.note("No month has ended yet.")
		none.autowrap_mode = TextServer.AUTOWRAP_OFF
		_history.add_child(none)
		return
	for value in shown:
		var cell := VBoxContainer.new()
		cell.add_theme_constant_override("separation", 0)
		cell.custom_minimum_size = Vector2(12, 90)
		var gap := Control.new()
		gap.custom_minimum_size = Vector2(12, 90.0 * (1.0 - float(value) / 100.0))
		cell.add_child(gap)
		var bar := ColorRect.new()
		bar.color = Color(0.85, 0.3, 0.25) if value < line else UiTheme.ACCENT
		bar.custom_minimum_size = Vector2(12, maxf(1.0, 90.0 * float(value) / 100.0))
		bar.tooltip_text = "Standing %d" % roundi(value)
		cell.add_child(bar)
		_history.add_child(cell)
