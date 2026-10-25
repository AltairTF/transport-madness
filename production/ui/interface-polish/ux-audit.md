# UX audit and polish list: Transport Madness interface

Date: 2026-10-24. Method: code reading only (ui_kit.gd, ui_theme.gd, hud.gd, window_manager.gd, settings_menu.gd, main_menu.gd, pause/save menus, parts of towns/standing/power windows). Nothing was run on screen, so every item is a code-derived finding to confirm in a screenshot. Pattern source: `game/ui/ui_kit.gd`, `game/ui_theme.gd`, `docs/style-guide.md` (not re-read).

**Assumed accessibility tier: Basic** (not committed; `design/accessibility-requirements.md` absent). Basic = scalable text, adequate contrast, no colour-only state, full mouse/keyboard play, rebindable keys. Confirm or change.

Priority: P1 visible defect or accessibility miss, P2 consistency, P3 nice-to-have. Cites are `file:line` under `game/`.

## A. Top strip, feed, banner, message, legend (hud.gd)

1. **P1 Colour-only state in the feed.** Severity (warn, bad) is only the font colour, no prefix or icon. `hud.gd:497-503`. Fix: add an icon or a "!" / "!!" lead, or a left stripe plus text label.
2. **P1 Money and standing state by colour only.** Negative money turns red (`hud.gd:782`) but shows no sign cue beyond the "-" from `Kit.money`. Standing red/green is backed by the arrow (`hud.gd:787-790`), but the red "low" state has no text. Fix: add a "LOW" word or warning glyph when `low`; money already has "-", keep that.
3. **P1 Text below the 16 px floor.** Captions at 14: `ui/maintenance_window.gd:22`, `ui/lines_window.gd:273`. Theme `Small` is 16 (`ui_theme.gd:151`). Fix: raise to 16 or use the `Small` variation.
4. **P1 Fixed offsets collide.** Raid banner (`hud.gd:442`, top 108, centred) and feed (`hud.gd:455-456`, top 108, 460 px wide, right) share a band; banner overlaps feed on narrow canvases or high interface scale. Legend sits at a fixed `offset_bottom = -150` (`hud.gd:656`) while the bottom stack grows by a group row plus message line, so the legend and an open group row can overlap. Fix: stack banner under the strip in a shared VBox, or move the feed below the banner; anchor the legend above the bottom stack's top, not a constant. Verify on screen at 1280x720 and 50% / 200% scale.
5. **P2 Duplicate date format.** The strip builds its own two-line date (`hud.gd:780`) and `Kit.date_text` (`ui_kit.gd:26-28`) gives a different one-line format. Fix: one helper, one format.
6. **P2 Feed fold tooltip is stale.** Always "Fold the news away" even when folded; glyph-only button. `hud.gd:466-468`. Fix: toggle tooltip ("Show the news"), add the word or a count ("News (3)") to the folded state.
7. **P2 Message line has no persistence or history.** One label, auto-hides (`hud.gd:532-537, 641-644`); errors ("Can't repair it: ...") vanish while the player looks at the world. Fix (P3 scope): keep error lines longer than info lines; optional last-5 history on hover.
8. **P2 Info-window and group buttons have no tooltips.** `Kit.button(entry[1], ...)` passes none (`hud.gd:550`); group buttons likewise (`hud.gd:588`). Tool buttons do have them (`hud.gd:542`). Fix: tooltip with the name, the hotkey and a few words on what the window answers.
9. **P3 Map-view buttons are a segmented control inside a plain panel.** They use `SegmentButton` (`hud.gd:684`) in a `_group_row` PanelContainer, not the `Segmented` well (`ui_theme.gd:92-93`), so they look unlike every other group row. Fix: use `Kit.segmented` or normal toggle buttons.

## B. Windows (window_manager.gd, ui_kit.gd, page scripts)

10. **P1 Close button is 40 high with a bare glyph; the Kit header one is 44.** Window frame uses `"✕"` (`ui/window_manager.gd:129`); `Kit.header` uses `"×"` 44x44 (`ui_kit.gd:92-94`). Two glyphs, two sizes; `Kit.header` may be unused. Fix: one close button in the Kit, used by the frame, with the same tooltip.
11. **P1 Window controls are mouse-only.** Drag handle is a Label and resize grip a Label with `gui_input` (`window_manager.gd:121-142`); neither is focusable. Close is a Button (reachable) but windows do not take focus when opened (`window_manager.gd:96-103`, no `grab_focus`), so Tab starts nowhere. Fix: on `_show`, focus the close button or first control; keep move/resize mouse-only (PC target) and say so in the accessibility notes.
12. **P2 Grip glyph is a bare "◢" Label.** Low-contrast affordance (`window_manager.gd:136-137`, default Label colour on panel). Fix: use `TEXT_DIM` and a minimum 24 px hit area.
13. **P2 Same actions, different shapes (inventory section 6).** Buy-vehicle and policy block appear in Lines, Vehicles and Maintenance. Do not redesign now. Cheap step: make one `Kit.buy_row` and one policy-summary string so labels and tooltips cannot drift (`ui_kit.gd:323-363` is already shared for the policy block; the buy row is not).
14. **P2 `CardMuted` is not visibly muted.** `_dashed_box` just calls `_box` (`ui_theme.gd:218-220`, used at `:157`); the only difference from `Card` is a fill and border tint. Players cannot tell "not available" from "available". Fix: lower the text alpha inside, or add a text word ("Not yet available" label already exists in Gateways; check Lines and Depots).
15. **P2 Condition bar carries its value only in the tooltip.** `Kit.set_condition_bar` sets tooltip only; the colour is the main signal (`ui_kit.gd:289-317`). Fix: print the number next to the bar in every place the bar is used, or add the band word (Good / Worn / Poor / Critical). Check `vehicles_window.gd` and `maintenance_window.gd`.
16. **P2 Standing history bars: red vs blue only.** `ui/standing_window.gd:120` (below dismissal line = red, else accent). Tooltip gives the number (`:122`), but the line itself is not drawn. Fix: draw a thin threshold line and label it "Dismissal below N".
17. **P2 Town trips split uses four hues** (`ui/towns_window.gd:121`); the legend exists (`refs["legend"]`) but check that blue (`3f78e0`) and light blue (`9cc2ff`) are distinguishable by labels and order, not hue. Fix: order plus labelled values on the segments or in the legend row.
18. **P2 Hard-coded meter and bar colours.** `Color("9cc2ff")` repeated (`standing_window.gd:29`, `vehicles_window.gd:102`, `towns_window.gd:71,77`, `finances_window.gd:31`, `industries_window.gd:98`), `Color(0.85,0.3,0.25)` (`standing_window.gd:120`), badge fill `Color("222a37")` (`ui_kit.gd:151`), condition colours (`ui_kit.gd:289-296`). The theme already has `ACCENT_TEXT`, `BAD`, `GOOD`, `WARN`. Fix: add `METER`, `BADGE_FILL`, and route condition colours through `GOOD/WARN/BAD`.
19. **P2 Font-size overrides outside the theme.** 20/22/26/14/16 appear as overrides in windows (`vehicles_window.gd:121`, `finances_window.gd:25`, `stations_window.gd:29` uses 22 where others use 20, `lines_window.gd:269`). Fix: one `Stat` size, one `Small` size, remove per-window overrides.
20. **P3 Power stepper `-` and `+` have no tooltip and a tiny hit area.** `ui/power_window.gd:34,42`. Fix: tooltip "Buy N MW less / more per month" and a 40 px square minimum.
21. **P3 Scroll area horizontal scroll is disabled** (`window_manager.gd:132`) and `MIN_SIZE` clamps the frame; a long unbreakable string in a `Label` without autowrap (for example `Kit.section` hint, `ui_kit.gd:83`) clips at small window widths. Fix: let hints wrap or elide with tooltip.

## C. Menus (main_menu, pause_menu, save_menu, settings_menu, credits_menu)

22. **P1 Fixed pixel panel widths may exceed the canvas at high interface scale.** New game 1180 (`main_menu.gd:210`), settings 980 (`settings_menu.gd:39`), save 820 (`save_menu.gd:27`), credits 820 (`credits_menu.gd:41`), side 660 (`main_menu.gd:93`). Interface scale goes to 200% (inventory 1.5); at 200% the logical canvas is about 960 wide, so these overflow with no scrolling. Verify; fix with a `min(width, viewport - margin)` clamp or a scroll wrapper.
23. **P2 Menus build their own panels and buttons** instead of `Kit` (`pause_menu.gd:86,159`, `main_menu.gd:484`, `settings_menu.gd:180,189`), and set sizes by hand: buttons 54 / 48 / 44 / 40 high across `main_menu.gd:125,140,144,218,350,353`, `save_menu.gd:49-54`. Fix: one `Kit.menu_button(text, variation)` at a single height (48), and `FIELD_HEIGHT` reused everywhere.
24. **P2 Rebinding allows a silent clash.** The new key is stored even when `clash != ""`; the old action keeps the same key until the player rebinds it, and only a note says so (`settings_menu.gd:220-223`). Two actions on one key is a bug trap. Fix: swap the keys, or refuse and ask. Also no per-row "reset" next to each key (only a global one at `:189`) and Escape cancels without feedback.
25. **P3 Disabled placeholders look like live controls.** Audio, Autosave, "Scroll at the screen edge" are disabled checkboxes (`settings_menu.gd:236-245`); disabled style is dim text on a near-identical fill (`ui_theme.gd:47,53`). Fix: label them "Coming later" or hide the tabs that are all placeholders (Audio, Gameplay) until built.
26. **P3 Keyboard focus in menus.** Only the save name field takes focus (`save_menu.gd:86`). Main, pause and settings menus do not set an initial focus, so arrow/Tab navigation starts from nothing. Fix: `grab_focus.call_deferred()` on Continue / Resume / first tab on show. Focus ring exists (`ui_theme.gd:48,209-215`, 2 px `FOCUS`), good.

## Not checked (need a screenshot pass)

Open tool and window rows, tooltips, feed and banner during a raid (as in inventory section 6); contrast of `TEXT_DIM` on `SURFACE_LOW` for disabled buttons; legibility at 50% interface scale; item 4 and 22 overlaps.

## Suggested order

1. Screenshot pass at 1280x720, 1920x1080, 50% and 200% scale (settles 4, 22, and what is really worth fixing).
2. P1 batch: 1, 2, 3, 10, 11, 22.
3. Consistency batch: 5, 6, 8, 14, 18, 19, 23, 24.
4. P3 as time allows.
