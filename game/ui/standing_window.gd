extends "res://game/ui/page.gd"
## Government > Standing: how the government judges the committee, what last month did to it, its history, and the war's toll.

const HISTORY_MONTHS := 24

var _details: Label
var _history: HBoxContainer
var _history_key := ""


func _build() -> void:
	_details = Label.new()
	_details.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(_details)
	add_child(Kit.heading("The last %d months" % HISTORY_MONTHS))
	_history = HBoxContainer.new()
	_history.add_theme_constant_override("separation", 2)
	_history.custom_minimum_size = Vector2(0, 90)
	add_child(_history)


func show_company(company: Dictionary) -> void:
	var standing: float = company["standing"]
	var text := "Standing   %d of 100   (allocation factor x%.2f)" % [roundi(standing), company["allocation_factor"]]
	if company["months_to_dismissal"] > 0:
		text += "\nUnder %d: %d more months and the committee is dismissed" % [roundi(company["dismiss_below"]), company["months_to_dismissal"]]
	text += "\nDelivered to the gateways this month   %s  (the phase expects %s)" % [Kit.money(company["export_value"]), Kit.money(company["expected_value"])]
	text += "\nOrders   %d met, %d late, %d failed   (%d under way, %d on offer)   rewards so far %s" % [company["orders_met"], company["orders_late"], company["orders_failed"], company["orders_active"], company["orders_open"], Kit.money(company["orders_income"])]
	var last: Dictionary = company["last"]
	if not last.is_empty():
		text += "\n\nLast month   deliveries %+.1f   deficit %+.1f   fines %+.1f   standing %d to %d   allocation %s" % [
			last["export_term"], last["deficit_term"], last["fine_term"], roundi(last["standing_before"]), roundi(last["standing_after"]), Kit.money(last["allocation"])]
	if company["called_up"] > 0 or company["next_call_month"] >= 0:
		text += "\n\nCall-up   %d people called up so far   workforce %d%% short (industry at %d%%)%s" % [company["called_up"], roundi(company["workforce_gap"] * 100.0), roundi(company["workforce_factor"] * 100.0),
			"   next call-up in %s %d" % [Kit.MONTHS[company["next_call_month"] % 12], company["year"] - floori(company["phase_month"] / 12.0) + floori(company["next_call_month"] / 12.0)] if company["next_call_month"] >= 0 else ""]
	if company["raids"] > 0:
		text += "\nRaids   %d flights so far, %d bombs, %d casualties" % [company["raids"], company["bombs"], company["casualties"]]
	_details.text = text
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
		_history.add_child(Kit.note("No month has ended yet."))
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
