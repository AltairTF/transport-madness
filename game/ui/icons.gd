extends RefCounted
## Interface icons (M7g): game-icons.net shapes beside the text of the bar buttons, never in place of it.
## data/icons.json maps a button key (a tool, a window or a group name) to the icon; the files are in game/assets/icons/game-icons/.

const PATH := "res://data/icons.json"
const FOLDER := "res://game/assets/icons/game-icons/"
const SIZE := 22

static var _table: Dictionary = {}
static var _loaded := false
static var _textures: Dictionary = {}


## The icon of a key, or null when it has none (a button then stays text only).
static func of(key: String) -> Texture2D:
	if not _loaded:
		_loaded = true
		var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(PATH)) if FileAccess.file_exists(PATH) else null
		if typeof(parsed) == TYPE_DICTIONARY and typeof(parsed.get("icons")) == TYPE_DICTIONARY:
			_table = parsed["icons"]
	if not _table.has(key):
		return null
	if not _textures.has(key):
		var file: String = FOLDER + String(_table[key]).get_file() + ".svg"
		_textures[key] = load(file) if ResourceLoader.exists(file) else null
	return _textures[key]


## Puts the icon of `key` on a button; the theme sizes and tints it.
static func put(button: Button, key: String) -> Button:
	var icon := of(key)
	if icon:
		button.icon = icon
	return button
