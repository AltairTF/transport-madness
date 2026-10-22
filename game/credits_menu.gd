extends PanelContainer
## The credits screen of the main menu: the table of CREDITS.md, so that it cannot go out of date.

signal closed

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
	custom_minimum_size = Vector2(760, 0)
	var column := VBoxContainer.new()
	add_child(column)

	var title := Label.new()
	title.text = "Credits"
	title.theme_type_variation = "Title"
	column.add_child(title)

	var rows := load_rows()
	var scroll := ScrollContainer.new()
	scroll.custom_minimum_size = Vector2(0, 360)
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	column.add_child(scroll)
	var list := VBoxContainer.new()
	list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(list)
	if rows.is_empty():
		var none := Label.new()
		none.text = "The game does not use any outside asset yet."
		none.theme_type_variation = "Dim"
		list.add_child(none)
	for row in rows:
		var name_label := Label.new()
		name_label.text = row["asset"]
		name_label.theme_type_variation = "Heading"
		list.add_child(name_label)
		var detail := RichTextLabel.new()
		detail.bbcode_enabled = true
		detail.fit_content = true
		detail.scroll_active = false
		detail.text = "%s, %s. [url]%s[/url]" % [row["author"], row["licence"], row["link"]]
		detail.meta_clicked.connect(func(meta: Variant) -> void: OS.shell_open(str(meta)))
		detail.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		list.add_child(detail)

	var back := Button.new()
	back.text = "Back"
	back.custom_minimum_size = Vector2(160, 0)
	back.size_flags_horizontal = Control.SIZE_SHRINK_END
	back.pressed.connect(func() -> void: closed.emit())
	column.add_child(back)
