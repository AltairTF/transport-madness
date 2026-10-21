# M6 plan: save and load, overlays, balance, the interface

Status: M6a built (project-status 5.44); M6b, M6c and M6d to come (decisions taken 2026-10-21 in an interview with the developer). M6 makes the loop complete before the look of the game changes: a campaign of 96 months can be saved and taken up again, the map can show what the player needs to see, the numbers are measured instead of guessed, and the interface is reworked. Art and audio, which the first plan put in M6, are moved to M7, so that everything is right before that overhaul. Weather stays out (after M6, with the visuals it needs). Vocabulary: `CONTEXT.md`. Determinism: ADR 0003.

## Slices

| Slice | Content | Done when |
|---|---|---|
| M6a | Save and load | A late-campaign world saved in the middle of a raid loads and continues to the same state hash as a run that was never interrupted |
| M6b | Overlays | Eight map overlays, one at a time, with a legend and a key |
| M6c | Balance | Headless bots play the campaign and the targets below are pinned |
| M6d | The interface | Starts with its own interview and layout prototypes; decided there |

M6a, M6b and M6c are built one after the other, each one commit and push; work stops before M6d for the interview. Each slice has its tests and hashes; the tuning numbers are chosen by Claude and listed afterwards; what was not played in 3D is said.

## M6a: save and load

- **A full snapshot.** A save is the whole state of the simulation: terrain, road and rail networks, towns and buildings, industries, the company and its fleet, trip groups, every random stream, orders, the war (flights in the air, the siren, ammunition of each battery, the side the aircraft come from, damage timers, the call-up and requisition indices, the requisition rule, the capacity factor of each town, the people lost by each group), power, incidents. Not the seed and the command log replayed: a whole campaign is about 6.9 million steps, which at 0.3 to 1 ms a step is half an hour or more of loading. Not the seed plus the changes: growth, raids and roads change the map, and a change to a generator would break every save.
- **Format.** Binary, written by hand per part of the state, compressed with gzip, with a header holding a format version. The save carries a copy of the tuning the game was started with, and a loaded game uses that copy, so a save plays as it was even after `data/tuning.json` changes. A save with another format version is refused with a clear message; there are no migrations while the game is in development.
- **The command log** starts again from the save: a bug can be replayed from a save and the commands after it.
- **Tests.** Save, load and save again gives the same bytes (whatever is read wrong shows). A long war scenario (flights in the air, the siren, a raid, a requisition, a call-up) saved midway continues to the same `StateHash` as the run that was not interrupted (whatever changes the future and was not saved shows). `StateHash` itself is not changed, so the goldens stay.
- **Slots.** Named saves made by the player, an autosave at the start of every month (three, rotating), F5 to save quickly and F9 to load the quick save. Continue in the main menu loads the most recent save of any kind. The end of the campaign (December 1945 or dismissal) does not delete saves. Files live in `user://saves/`.
- **The list** shows the slot's name, the month of the game, the standing, the money, the real date and time and a small screenshot; a save can be deleted or overwritten. Load is in the main menu and in the pause menu.
- **Presentation state** saved with the game: the camera, the history of the news feed and the state of the campaign kept by the game layer (the end of the game, the news of new models). Not saved: open windows, the overlay, the active tool. A loaded game starts paused.
- **Quitting** to the menu or closing the game with progress made since the last save (manual or automatic) asks for confirmation.
- **The hitch.** The state is written to memory on the main thread between steps; compressing and writing the file runs on another thread. Target: under 0.3 s on a late-campaign world, measured and written down.

## M6b: overlays

One overlay at a time, chosen from a single bar and cycled with one key; the rest of the map is dimmed and a legend sits in a corner.

- **Traffic:** vehicles per kilometre of lane, averaged over the last hour of the game; green, yellow, red.
- **Incident risk:** incidents on the road in the last 10 days, plus a low Condition, plus a junction without control.
- **Road Condition:** each road coloured by its Condition.
- **Coverage:** the 150 m circles of the stations, and the buildings and industries they cover marked.
- **Anti-aircraft:** the 700 m circle of each battery, the side the aircraft come from and the stretch, about 600 m before a target, where their bombs fall.
- **Power:** each industry coloured by the share of its power it gets.
- **Staffing:** each industry coloured by the share of its jobs that are filled.
- **Connectivity:** as now (homes green or red), moved into the same system.

## M6c: balance

- **Headless bots** play the campaign through commands: an idle committee, a company that runs only buses and does no maintenance, and a competent player (trucks on the chains, a railway, maintenance, orders taken and met). They report the standing, the grade, the money and the month of dismissal.
- **Targets:** the idle committee is dismissed between months 20 and 30 (about 55 now, too easy); the bus-only company without maintenance about month 28 (as now); the competent bot ends with the second-best grade. The best grade is for strong human play and no bot is pinned to it.
- **Where they run.** A whole campaign may take more than 30 minutes, so the full runs are a `Balance` test category outside the default CI run (run by hand, or by a manual GitHub workflow); short probes stay in CI.
- **One difficulty.** Presets (a bigger or smaller allocation) come later, with the sandbox.
- **What may change.** Numbers in `data/tuning.json` freely, listed at the end. A change of a rule or formula (for instance a standing that falls in idle months) is asked of the developer first, if the numbers alone do not reach a target.

## M6d: the interface

Starts with an interview of its own and layout prototypes for the developer to choose from. Scope: a toolbar by category, draggable windows, a key-binding menu, splitting `hud.gd`, and moving picking and drawing out of `SimHost`. The details are decided then.

## M7: art and audio (after M6)

- **Art:** low-poly and stylised, the late 1930s and 1940s, slightly desaturated colours, not realistic. CC0 packs first (Kenney, Quaternius, Poly Pizza), models generated by AI where nothing fits (vehicles of the period, particular industries); the developer curates. Scope: vehicles, trains, town buildings, the 16 industries, gateways, depots, stations, trees and terrain, aircraft and bombs. Casualties stay numbers only.
- **Audio:** CC0 sound effects (freesound CC0, Kenney): engines, horns, crashes, trains, the siren, bombs, anti-aircraft, town ambience. A few instrumental tracks of the period, public domain or CC0, or generated. Volume by category in the settings. The developer curates.

## Left out

Weather (wet or icy road), difficulty presets, and everything listed "after M6" in `campaign-plan.md` (a depot with its own stock, an ammunition factory, trains carrying workers, wages and shifts, a standing graph, sandbox mode, a wider choice of models).
