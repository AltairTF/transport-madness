extends PanelContainer
## The credits screen of the main menu: the table of CREDITS.md, so that it cannot go out of date.

signal closed

const UiTheme := preload("res://game/ui_theme.gd")
const Kit := preload("res://game/ui/ui_kit.gd")
const PATH := "res://CREDITS.md"


## The rows of the table in a CREDITS.md text: { asset, author, licence, link, folder }.
static func parse(text: String) -> Array[Dictionary]:
	var rows: Array[Dictionary] = []
	var seen_header := false
	for line in text.split("\n"):
		var trimmed := line.strip_edges()
		if not trimmed.begins_with("|"):
			continue
		var cells := trimmed.trim_prefix("|").trim_suffix("|").split("|")
		if cells.size() != 5:
			continue
		if not seen_header:
			seen_header = true # the header row
			continue
		if cells[0].strip_edges().begins_with("---"):
			continue
		rows.append({
			"asset": cells[0].strip_edges(), "author": cells[1].strip_edges(), "licence": cells[2].strip_edges(),
			"link": cells[3].strip_edges(), "folder": cells[4].strip_edges(),
		})
	return rows


static func load_rows() -> Array[Dictionary]:
	if not FileAccess.file_exists(PATH):
		return []
	return parse(FileAccess.get_file_as_string(PATH))


func _ready() -> void:
	Kit.fit_width(self, 820)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", UiTheme.PANEL_MARGIN)
	add_child(column)
	column.add_child(Kit.header("Credits", func() -> void: closed.emit()))

	var rows := load_rows()
	var scroll := ScrollContainer.new()
	scroll.custom_minimum_size = Vector2(0, 460)
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	column.add_child(scroll)
	var list := VBoxContainer.new()
	list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	list.add_theme_constant_override("separation", 8)
	scroll.add_child(list)
	if rows.is_empty():
		list.add_child(Kit.note("The game does not use any outside asset yet."))
	for row in rows:
		var card := Kit.card()
		var body: VBoxContainer = card.get_meta("body")
		var name_label := Label.new()
		name_label.text = row["asset"]
		body.add_child(name_label)
		var detail := Kit.rich("%s, %s. [url]%s[/url]" % [row["author"], row["licence"], row["link"]])
		detail.meta_clicked.connect(func(meta: Variant) -> void: OS.shell_open(str(meta)))
		body.add_child(detail)
		list.add_child(card)
