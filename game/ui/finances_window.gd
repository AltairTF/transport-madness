extends "res://game/ui/page.gd"
## Company > Finances: the books of the committee and the ministry's advance.

var _when: Label
var _money: Label
var _flows: Dictionary = {}
var _loan: Dictionary
var _next: Label
var _shares: Dictionary = {}


func _build() -> void:
	_when = Kit.note("")
	add_child(_when)
	var summary := Kit.card()
	var body: VBoxContainer = summary.get_meta("body")
	var caption := Kit.note("Money")
	body.add_child(caption)
	_money = Label.new()
	_money.theme_type_variation = "BigNumber"
	body.add_child(_money)
	var flows := Kit.row()
	for entry in [["income", "Income"], ["upkeep", "Upkeep"], ["interest", "Interest"]]:
		var stat := Kit.stat(entry[1])
		stat["value"].add_theme_font_size_override("font_size", 20)
		flows.add_child(stat["box"])
		_flows[entry[0]] = stat
	body.add_child(flows)
	add_child(summary)
	add_child(Kit.section("Advance from the ministry"))
	_loan = Kit.meter("Owed", Color("9cc2ff"))
	add_child(_loan["box"])
	var buttons := Kit.row()
	var ask := Kit.button("Ask for 100,000", func() -> void: hud.action_requested.emit("loan"))
	ask.theme_type_variation = "AccentButton"
	ask.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	buttons.add_child(ask)
	var repay := Kit.button("Repay 100,000", func() -> void: hud.action_requested.emit("repay"))
	repay.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	buttons.add_child(repay)
	add_child(buttons)
	var head := Kit.section("Monthly allocation")
	_next = Kit.note("")
	_next.autowrap_mode = TextServer.AUTOWRAP_OFF
	head.add_child(_next)
	add_child(head)
	var shares := Kit.card()
	var shares_body: VBoxContainer = shares.get_meta("body")
	for entry in [["allocations", "Allocations so far"], ["imports", "Imports so far"], ["next", "Next payment"]]:
		var row := Kit.row()
		var name := Label.new()
		name.text = entry[1]
		name.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		row.add_child(name)
		var value := Label.new()
		row.add_child(value)
		shares_body.add_child(row)
		_shares[entry[0]] = value
	add_child(shares)
	add_child(Kit.note("The allocation depends on the phase and on your standing with the government."))


func show_company(company: Dictionary) -> void:
	_when.text = "%s, %s · %s · month %d of %d" % [company["country"], company["region"], company["phase"], mini(company["phase_month"] + 1, company["months"]), company["months"]]
	_money.text = Kit.money(company["money"])
	_flows["income"]["value"].text = "+ " + Kit.money(company["income"])
	_flows["upkeep"]["value"].text = "− " + Kit.money(company["upkeep"])
	_flows["interest"]["value"].text = "− " + Kit.money(company["interest"])
	_loan["value"].text = "%s of %s" % [Kit.money(company["loan"]), Kit.money(company["max_loan"])]
	_loan["bar"].value = 100.0 * company["loan"] / maxf(1.0, company["max_loan"])
	_next.text = Kit.date_text(company)
	_shares["allocations"].text = Kit.money(company["allocations"])
	_shares["imports"].text = "− " + Kit.money(company["imports"])
	_shares["next"].text = Kit.money(company["next_allocation"])
