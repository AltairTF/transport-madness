extends Node
## What the next game should be. The main menu sets it, main.gd reads it when the game scene starts.

const MAPS := [
	{"id": "towns", "name": "Towns", "text": "Generated hills with towns and industries on the flat ground. Connect them and feed them.", "towns": true},
	{"id": "demo", "name": "Demo grid", "text": "A 10 x 10 street grid with 1000 cars, ready to watch."},
	{"id": "hills", "name": "Hills and water", "text": "Generated hills, rivers, lakes and a sea. Build your own roads."},
	{"id": "empty", "name": "Flat and empty", "text": "A level map with nothing on it."},
]

var map := "demo"
var towns := 5
var industries := 1 ## 0 few, 1 normal, 2 many
var size := 1 ## 0 small (3.1 km), 1 normal (4.1 km), 2 large (8.2 km)
var mountains := 0.5 ## how much of each the generated land has, 0 to 1
var water := 0.5
var forests := 0.5
var frequency := 2 ## how often random events happen: 0 off, 1 low, 2 normal, 3 high
var world_seed := 1
var load_path := "" ## a save the game scene loads instead of making a new world; "" for a new game
var menu_message := "" ## said by the main menu when it opens (why a save could not be loaded)
