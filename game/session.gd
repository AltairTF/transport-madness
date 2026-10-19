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
var world_seed := 1
