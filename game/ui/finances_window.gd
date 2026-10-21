extends "res://game/ui/page.gd"
## Company > Finances: the books of the committee and the ministry's advance.

var _details: Label


func _build() -> void:
	_details = Label.new()
	_details.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(_details)
	var buttons := Kit.flow()
	add_child(buttons)
	buttons.add_child(Kit.button("Ask the ministry for an advance of 100k", func() -> void: hud.action_requested.emit("loan")))
	buttons.add_child(Kit.button("Repay 100k", func() -> void: hud.action_requested.emit("repay")))


func show_company(company: Dictionary) -> void:
	_details.text = "%s: %s\n%s, month %d of %d\n\nMoney   %s\nAdvance from the ministry   %s of %s\nDate   %s\n\nIncome   %s\nUpkeep   %s\nInterest   %s\nAllocations so far   %s\nImports so far   %s\nNext allocation   %s" % [
		company["country"], company["region"], company["phase"], mini(company["phase_month"] + 1, company["months"]), company["months"],
		Kit.money(company["money"]), Kit.money(company["loan"]), Kit.money(company["max_loan"]), Kit.date_text(company),
		Kit.money(company["income"]), Kit.money(-company["upkeep"]), Kit.money(-company["interest"]),
		Kit.money(company["allocations"]), Kit.money(-company["imports"]), Kit.money(company["next_allocation"])]
