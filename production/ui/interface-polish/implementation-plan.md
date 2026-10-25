# Implementation plan: interface polish

Date: 2026-10-24. Source: `ux-audit.md` (approved items 1, 2, 3, 4, 10, 11, 22 and 5, 6, 8, 14, 18, 19, 23, 24). Every cite below was re-read against the current code. Paths are under `game/`. Nothing is written yet.

Tier assumed Basic: text floor 16, no colour-only state. Interface scale 50%..200% is kept: no new fixed pixel sizes except theme tokens; only item 22 changes how sizes react to the scale.

## Cross-cutting findings

- `Kit.header` is used (settings, save, credits menus), so the audit's "may be unused" is wrong. Two close buttons exist: header (`"×"`, 44x44, tooltip "Close") and window frame (`"✕"`, no min size, long tooltip).
- No selftest in `main.gd` or `tools/` touches a HUD private node, window text or menu node path. `main.gd` uses only public HUD API (`show_message`, `show_company`, `select_tool`, `open_window`, `restore_feed`, `feed_snapshot`, ...). All of it stays unchanged. No sim-facing path changes.
- Sim state is never read-write from these changes. The one input to settings is item 24 (`Settings.key_bindings`, UI-side settings, already written by the settings menu).

## New theme tokens and Kit helpers (shared by several items)

`ui_theme.gd`: add consts `METER` (9cc2ff), `METER_WELL` (222a37), `POOR` (orange tier for Condition), `ICON_BUTTON := 44`, `MENU_BUTTON_HEIGHT := 48`, `FIELD_HEIGHT := 52` (moved from `main_menu.gd`); add type variation `Figure` (Label, size 20) next to `Stat`/`Small`. The ProgressBar well/fill/track literals in `build()` use `METER_WELL`/`METER`.
`ui/ui_kit.gd`: add `close_button(action, tooltip := "Close") -> Button`, `menu_button(text, action, variation := "") -> Button`, `fit_width(panel, wanted)`. Change `condition_color`, `badge`, `date_text`, `stat` as listed below.

## P1

### 1. Feed severity by colour only (VALID, hud.gd:497-503)
- `hud.gd`: const `SEVERITY_MARK := ["", "! ", "!! "]`; in `show_events` prefix `b.text` with `SEVERITY_MARK[event["severity"]]`. Colour stays as a second cue. The saved event dictionary is untouched, so `feed_snapshot` / `restore_feed` round-trip is unchanged.
- Risk: none for saves. Text width grows by 2-3 chars; the button already wraps.

### 2. Money / standing state (PARTLY VALID)
- Money: dropped. `Kit.money` already prints "-"; the audit itself says keep.
- Standing low: `hud.gd` `show_company` (line 785-790): when `low`, append `"  LOW"` to the standing text, same convention as the existing `"  DISMISSED"` suffix on money. The dismissal label (`months_to_dismissal`) already adds text once the countdown runs; `low` is the 10-point warning band before it, which is what had no text.
- Risk: width of the standing label in the top strip; check at 50% scale.

### 3. Text under 16 (VALID, exact cites)
- `ui/maintenance_window.gd:22`: delete the `caption` 14 override (`Kit.stat` caption already uses `Small` = 16).
- `ui/lines_window.gd:273`: delete the 14 override (label already `Small`).
- Other font overrides below 16: only `perf_overlay.gd:506` (12, debug overlay); out of the approved set, left alone and noted.
- Risk: stat rows get slightly wider; check the Lines and Maintenance windows at default window width.

### 4. Fixed offsets collide (VALID)
- `hud.gd` `_build_raid_banner`, `_build_feed`: one right-anchored `VBoxContainer` (`_alerts`, top 108, 448 wide, `MOUSE_FILTER_IGNORE`) holds the raid banner (autowrap, expand fill) then the feed panel. Remove their own anchors/offsets. The banner is no longer centred; it sits above the feed, so they cannot overlap.
- `hud.gd` `_build_bottom` / `_build_legend`: keep the bottom `stack` in `_bottom_stack`; on its `resized` set `_legend.offset_bottom = -(_bottom_stack.size.y + 12 + Kit.GAP)`; replaces the constant `-150.0`. `_build_legend` already runs before `_build_bottom`, so set `_legend.offset_bottom` from a function called after both are built.
- Not changed: `window_manager.gd` `TOP`/`BOTTOM` (100/150) constants have the same staleness (a long group row is taller than 150). Out of the approved items; flagged below.
- Risk: moves the banner (visual change). Needs a screenshot at 1280x720 with 50% and 200% scale and an open group row plus message line.

### 10. Two close buttons (VALID; "header may be unused" is wrong)
- `ui/ui_kit.gd`: `close_button(action, tooltip := "Close")` returns a `Button` with `"✕"`, `custom_minimum_size = (ICON_BUTTON, ICON_BUTTON)`. `header()` uses it.
- `ui/window_manager.gd` `_frame`: replace the `Kit.button("✕", ...)` with `Kit.close_button(close.bind(key), "Close (Esc closes the topmost window)")`; keep the reference in frame meta `"close"`.
- Risk: window title row grows from 40 to 44 high. Tests: none.

### 11. Window controls mouse-only; no focus on open (VALID)
- `ui/window_manager.gd` `_frame`: `frame.focus_mode = Control.FOCUS_ALL`. `_show`: `frame.grab_focus.call_deferred()`. Tab then starts at the frame and the first stop is the close button.
- Deliberately NOT focusing the close button: Enter (finish line) or Space on a focused button would close the window. `Space` is safe (HUD reads it in `_input` first, see the comment at `hud.gd` `_input`), `Enter` is not.
- Drag and resize stay mouse-only (PC target). Say so in the accessibility notes; I will not write a new accessibility doc.
- Risk: a window opened from the world click takes UI focus from nothing; arrows still pan (camera polls the keys; unverified, check by running).

### 22. Fixed menu widths overflow at high scale (VALID in principle; unverified on screen)
- Math: logical canvas = window / (ui_scale x screen_scale). 1920x1080 at 200% gives 960 wide; 1180 / 980 / 820 overflow or fill it.
- `ui/ui_kit.gd` `fit_width(panel, wanted)`: sets `custom_minimum_size.x = minf(wanted, viewport_width - 2 * PANEL_MARGIN)` when shown, on `Settings.changed` (deferred), and disconnects on `tree_exiting`.
- Use in `main_menu.gd:210` (1180), `settings_menu.gd:39` (980), `save_menu.gd:27` (820), `credits_menu.gd:41` (820). Side 660 (`main_menu.gd:93`) and pause panels 420/520 fit at 960, so unchanged.
- Risk: children can still force a bigger minimum (the new-game body has a 380 px preview next to the fields). Verify on screen at 200%. Heights are not clamped: settings (card 500 plus scroll 460) is taller than a 540 logical canvas. Not in the approved item; flagged below.
- The `Settings.changed` signal is not emitted by an unrelated path, so this does not touch the selftests (the menu is skipped when user args are given).

## Consistency

### 5. Duplicate date format (VALID)
- `ui/ui_kit.gd` `date_text(company, separator := "   ")`. `hud.gd:780` calls `Kit.date_text(company, "\n")`: "2 Mar 1938 / 14:30". The "Day" wording goes. `finances_window.gd:71` unchanged (default separator).

### 6. Feed fold tooltip (VALID)
- `hud.gd` `_build_feed`: keep the title in `_feed_title`; the fold handler sets tooltip "Fold the news away" / "Show the news" and calls `_refresh_feed_title()` which writes `News (n)` while folded and `News` otherwise. Call it from `show_events` and the expiry loop in `_process`.

### 8. Missing tooltips (VALID)
- `hud.gd` `INFO_GROUPS`: add a fourth element per window, a short phrase of a few words (17 entries, data only, no paragraph). `_build_bottom` passes it as the tooltip of `Kit.button`.
- Group buttons (`_group_button`): tooltip is "<name> (<hotkey>)" using `Keys.text(action, Settings.key_bindings)`, refreshed on `mouse_entered` so a rebind shows. No global signal needed.
- Risk: wording needs a quick read from the designer. Tool buttons already have tooltips.

### 14. CardMuted not visibly muted (VALID, but the text cue exists)
- `Gateways` (`status` line, "Warn" variation) and `Orders` (status text for Met/Late/Failed) already print a word, so no text cue is added.
- `ui_theme.gd`: rename `_dashed_box` to `_muted_box`; transparent fill with the dim border, so a muted card reads as an outline against the filled `Card`. A real dash is not possible with `StyleBoxFlat`.
- Lines and Depots do not use `CardMuted` at all (grep). Nothing to change there.

### 18. Hard-coded colours (VALID)
- Replace `Color("9cc2ff")` with `UiTheme.METER` in `standing_window.gd:29`, `vehicles_window.gd:102`, `towns_window.gd:71,77,121`, `finances_window.gd:31`, `industries_window.gd:98`; `towns_window.gd:121` also takes `GOOD`/`WARN` for its two matching hues. Others in that array stay (item 17 is not approved).
- `standing_window.gd:120`: red becomes `UiTheme.BAD`.
- `ui_kit.gd:151` badge fill becomes `UiTheme.METER_WELL`; `condition_color` returns `GOOD` / `WARN` / `POOR` / `BAD`.
- Each window file gets a `const UiTheme := preload(...)` only where it is missing (checked at edit time).
- Risk: Condition colours shift slightly in value (the new ones are the palette's, brighter than the old literals). Screenshot check of Vehicles and Maintenance.

### 19. Font-size overrides outside the theme (VALID)
- `ui_theme.gd`: `Figure` variation (20). `Kit.stat(caption, size := "Stat")` already takes a variation name; pass `"Figure"` in `maintenance_window.gd:21`, `lines_window.gd:269`, `vehicles_window.gd:121`, `finances_window.gd:25`, `stations_window.gd:29` (22 becomes 20, matching the rest) and remove each override.
- 16 overrides become the `Small` variation: `ui_kit.gd:138` and `:162`, `hud.gd:670`, `main_menu.gd:73,132`, `save_menu.gd:150`. Title 84 and glyph 22 (random-seed button) stay: one-offs, noted.

### 23. Menus build their own buttons (VALID, narrowed)
- `Kit.menu_button` at `MENU_BUTTON_HEIGHT` (48). `pause_menu._button` and `main_menu._button` call it; `pause_menu.gd` gets a `Kit` preload. Remove the per-call heights (`main_menu.gd:125` 54, `:140`/`:144` 48, `:350`/`:353` 44, `save_menu.gd:54` 48).
- `FIELD_HEIGHT` moves to `UiTheme`; `main_menu.gd` and `save_menu.gd:49` use it (name field 48 becomes 52). Back arrow (`main_menu.gd:218`) uses `ICON_BUTTON`.
- Dropped: a shared panel factory. The menus' hand-made `PanelContainer`s only set a width, which item 22 now handles through `fit_width`.
- Settings tab buttons at `settings_menu.gd:180,189` are plain, unsized buttons: the reset button uses `Kit.menu_button`; the key buttons stay default (they are table cells).

### 24. Rebinding clash (VALID, narrowed)
- `settings_menu.gd` `_input`: before overwriting, keep the action's old binding; when `clash != ""`, assign that old binding to the clashing action (swap), so no two actions share a key. Note text: "<A> now on <key>. <B> moved to <old key>." Then `_keys_changed()` as today.
- Dropped: per-row reset button (extra row widget, global reset exists) and Esc feedback (the note under the table already says "Esc keeps the old key"). Both can follow if wanted.
- Risk: touches `Settings.key_bindings`, saved with the settings; same format, so no migration.

## Files to create or change

`game/ui_theme.gd`, `game/ui/ui_kit.gd`, `game/ui/window_manager.gd`, `game/hud.gd`, `game/main_menu.gd`, `game/pause_menu.gd`, `game/save_menu.gd`, `game/settings_menu.gd`, `game/credits_menu.gd`, `game/ui/maintenance_window.gd`, `game/ui/lines_window.gd`, `game/ui/stations_window.gd`, `game/ui/vehicles_window.gd`, `game/ui/finances_window.gd`, `game/ui/towns_window.gd`, `game/ui/standing_window.gd`, `game/ui/industries_window.gd`. No file is created. `production/qa/evidence/` screenshots are needed for each touched screen (project UI gate).

## Concerns

- Heights are not clamped at high scale (settings, new game): not an approved item.
- `window_manager.gd` `TOP`/`BOTTOM` constants (100/150) have the item-4 problem too; not approved.
- Whether arrow-key panning survives window focus (item 11) is unverified; confirm by running.
- Whether `Settings.changed` fires when the interface scale changes is read from `settings.gd:apply()` only; the 200% screenshot is the proof.
- Tooltip wording for item 8 (17 short phrases) needs a quick read from the designer.
