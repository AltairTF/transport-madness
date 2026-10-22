extends RefCounted
## The keys of the game (M6d): every action has a default key, the player can bind another in Settings > Controls, and the bindings
## are kept in the settings. They are registered as InputMap actions; a check asks for the exact key with its Shift, so F1 and
## Shift+F1 are different actions. Esc (the menu, closing a window, cancelling a tool) and the mouse are not rebound.

## [action, what it does, default key, with Shift]
const ACTIONS := [
	["speed_pause", "Pause, or go on", KEY_SPACE, false],
	["speed_1", "Speed 1x", KEY_1, false],
	["speed_2", "Speed 2x", KEY_2, false],
	["speed_3", "Speed 4x", KEY_3, false],
	["speed_4", "Speed 8x", KEY_4, false],
	["camera_forward", "Camera forward", KEY_W, false],
	["camera_back", "Camera back", KEY_S, false],
	["camera_left", "Camera left", KEY_A, false],
	["camera_right", "Camera right", KEY_D, false],
	["camera_turn_left", "Turn the camera left", KEY_Q, false],
	["camera_turn_right", "Turn the camera right", KEY_E, false],
	["overlay_next", "Next map view", KEY_V, false],
	["overlay_previous", "Map view before", KEY_V, true],
	["rotate_signal", "Turn a signal or a building round", KEY_R, false],
	["rotate_back", "Turn a building the other way", KEY_R, true],
	["raise_building", "Raise a building's floor", KEY_PAGEUP, false],
	["lower_building", "Lower a building's floor", KEY_PAGEDOWN, false],
	["place_free", "Hold to place without snapping", KEY_ALT, false],
	["place_free_toggle", "Switch placing without snapping on or off", KEY_G, false],
	["parallel_road", "Build a parallel road or track beside", KEY_P, false],
	["finish_line", "Finish a new line", KEY_ENTER, false],
	["quick_save", "Quick save", KEY_F5, false],
	["quick_load", "Load the quick save", KEY_F9, false],
	["debug", "Debug panel", KEY_F12, false],
	["perf_overlay", "Performance panel", KEY_F10, false],
	["build_1", "Roads", KEY_F1, false],
	["build_2", "Modify", KEY_F2, false],
	["build_3", "Rail", KEY_F3, false],
	["build_4", "Transport", KEY_F4, false],
	["build_5", "Defence", KEY_F6, false],
	["info_1", "Network", KEY_F1, true],
	["info_2", "Region", KEY_F2, true],
	["info_3", "Company", KEY_F3, true],
	["info_4", "Government", KEY_F4, true],
	["info_5", "Map", KEY_F6, true],
]


## The binding of an action: {"key": keycode, "shift": bool}, the player's or the default.
static func binding(action: String, bindings: Dictionary) -> Dictionary:
	if bindings.has(action):
		return bindings[action]
	for entry in ACTIONS:
		if entry[0] == action:
			return {"key": entry[2], "shift": entry[3]}
	return {"key": KEY_NONE, "shift": false}


## Registers every action with its key (call at start and after a change).
static func apply(bindings: Dictionary) -> void:
	for entry in ACTIONS:
		var action: String = entry[0]
		if InputMap.has_action(action):
			InputMap.erase_action(action)
		InputMap.add_action(action)
		var b := binding(action, bindings)
		var event := InputEventKey.new()
		event.keycode = b["key"]
		event.shift_pressed = b["shift"]
		InputMap.action_add_event(action, event)


## Whether <event> presses <action> with exactly its key and Shift.
static func pressed(event: InputEvent, action: String) -> bool:
	return InputMap.has_action(action) and event.is_action_pressed(action, false, true)


## "Shift+F1", "Space", "W".
static func text(action: String, bindings: Dictionary) -> String:
	var b := binding(action, bindings)
	return ("Shift+" if b["shift"] else "") + OS.get_keycode_string(b["key"])


## The action already bound to this key, "" when none (to warn about a clash).
static func action_on(key: int, shift: bool, bindings: Dictionary, except := "") -> String:
	for entry in ACTIONS:
		var b := binding(entry[0], bindings)
		if entry[0] != except and b["key"] == key and b["shift"] == shift:
			return entry[0]
	return ""


static func label(action: String) -> String:
	for entry in ACTIONS:
		if entry[0] == action:
			return entry[1]
	return action
