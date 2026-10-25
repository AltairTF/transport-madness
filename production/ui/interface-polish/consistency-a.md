# Consistency batch, part A

Files changed: game/ui_theme.gd, game/ui/ui_kit.gd, game/main_menu.gd, game/pause_menu.gd, game/save_menu.gd, game/ui/{maintenance,lines,stations,vehicles,finances,towns,standing,industries}_window.gd

- 5: `Kit.date_text(company, separator := "   ")`; default output unchanged. hud.gd call left to part B.
- 14: `_dashed_box` renamed `_muted_box`; CardMuted uses a transparent fill with the dim border.
- 18: `UiTheme.METER/METER_WELL/POOR` added; ProgressBar styles, badge fill, towns/standing/vehicles/finances/industries literals and `condition_color` use palette tokens (GOOD/WARN/POOR/BAD); standing red bar uses BAD; towns trip kinds use METER/GOOD/WARN.
- 19: `Figure` variation (20); maintenance, lines, vehicles, finances, stations use `Kit.stat(..., "Figure")` with overrides removed (stations 22 becomes 20); 16 overrides become `Small` in ui_kit (2), main_menu (2), save_menu (1). hud.gd:670 skipped. Title 84 and glyph 22 kept.
- 23: `Kit.menu_button`, `UiTheme.MENU_BUTTON_HEIGHT/FIELD_HEIGHT` added; main/pause `_button` use it; per-call heights removed (Back/Start/Continue lose their min width/height 90/44/54); save name field uses FIELD_HEIGHT, Save button uses menu_button (width 100 kept); back arrow uses ICON_BUTTON.

Ran: `--check-only --script` on all 13 files with the non-.NET Godot: only "Identifier not found: Settings/Session" (autoloads, unavailable in script-only mode) cascades; no other parse errors seen. Real-game `--selftest-build` (mono Godot, headless): finished, grep for `SCRIPT ERROR|^ERROR|Parse Error` found nothing. No visual check done.

Concerns: Back/Start in new-game footer no longer have min width 90; no screenshots in production/qa/evidence/ yet.
