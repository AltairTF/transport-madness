# Consistency part B

## Files changed
game/hud.gd, game/settings_menu.gd

## Items
- 5: hud.gd date label now `Kit.date_text(company, "\n")` (needs part A's separator param).
- 6: `_feed_title` kept; fold tooltip toggles "Fold the news away" / "Show the news"; `_refresh_feed_title()` writes `News (n)` while folded, `News` otherwise; called from fold handler, show_events and the expiry loop in _process.
- 8: INFO_GROUPS entries get a 4th element (tooltip), passed to Kit.button in _build_bottom. `_group_button` tooltip "<name> (<key>)" via Keys.text, set at build and refreshed on mouse_entered.
- 19 (hud.gd only): `_legend_note` 16 override -> `theme_type_variation = "Small"`.
- 24: settings_menu.gd _input swaps: clashing action takes the old binding; note "<A> now on <key>.  <B> moved to <old key>."

## Tooltip phrases (13 windows exist in INFO_GROUPS, not 17; Map group has no windows)
Lines: Routes, stops and timetables
Vehicles: Every vehicle and its condition
Depots: Where vehicles are kept and bought
Stations: Stations and the cargo waiting
Towns: Population, needs and connections
Industries: What each industry makes and needs
Gateways: Where cargo enters and leaves
Power: Electricity supply and demand
Finances: Money in, money out
Maintenance: Wear, repairs and upkeep
Requisition: Goods the government requisitions
Orders: What the government demands of you
Standing: How the government judges you

## Ran
- Godot --check-only on hud.gd and settings_menu.gd: both report "Identifier not found: Settings" (autoload, not resolvable standalone; settings_menu.gd already used it before my edit). Not a real check.
- Real-game selftest --selftest-build (150 s alarm): ran to "SELFTEST vehicles=0 ... incidents=0"; grep for SCRIPT ERROR|^ERROR|Parse Error found nothing.
- No visual checks done.

## Concerns
- Plan says 17 phrases; the data has 13 windows. Phrases for Requisition/Power/Gateways are guesses from names; designer should read.
- Selftest passing implies Kit.date_text(company, "\n") compiled (part A's param present or HUD path not hit); not independently confirmed.
