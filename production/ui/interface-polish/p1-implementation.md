# P1 interface polish: implementation (items 1, 2, 3, 4, 10, 11, 22)

## Files changed (all under game/)
ui_theme.gd, ui/ui_kit.gd, ui/window_manager.gd, hud.gd, main_menu.gd, settings_menu.gd, save_menu.gd, credits_menu.gd, ui/maintenance_window.gd, ui/lines_window.gd

## Summary
- 1: `hud.gd` const `SEVERITY_MARK` ("", "! ", "!! ") prefixes feed button text; event dictionaries untouched.
- 2: `show_company` appends "  LOW" to the standing text when `low`.
- 3: removed the 14 px overrides in maintenance_window.gd (caption) and lines_window.gd (loading label).
- 4: raid banner and feed share one right-anchored `alerts` VBox (top 108, 448 wide, autowrap banner); legend `offset_bottom` follows the bottom stack's `resized` (the -150 stays only as the start value).
- 10: `UiTheme.ICON_BUTTON` = 44, `Kit.close_button`; `Kit.header` and the window frame use it.
- 11: window frame `FOCUS_ALL`, `grab_focus.call_deferred()` in `_show` (close button deliberately not focused).
- 22: `Kit.fit_width(panel, wanted)` (on enter tree, visibility, deferred `Settings.changed`, disconnect on exit) used by main_menu (1180), settings (980), save (820), credits (820).

## Verification
Ran `Godot --headless --path . --check-only --script res://game/<f>.gd` for each of the 10 files: no errors or parse messages printed. Not run: the game, selftests, screenshots. No visual check done (50%/200% scale, banner/feed stacking, LOW width, Tab focus, menu widths at 200%).

## Concerns
- Close glyph in header changed from "×" to "✕" (unified, per plan).
- Check-only per file does not resolve autoload `Settings` in `Kit.fit_width`; unverified at runtime.
- Heights of settings menu and `window_manager` TOP/BOTTOM constants not clamped (out of scope).
- `perf_overlay.gd:506` font 12 left alone (out of set).
