# Campaign plan: a region at war, 1938 to 1945

Status: C1 to C5 done on 2026-10-21 (sections 5.23, 5.25, 5.26, 5.27 and 5.28 of `project-status.md`); the rest is planned (decisions taken 2026-10-21 in an interview with the developer). The reasons and the shape are in ADR 0006; this file is the list of decisions, the slices and what is still open. It sits between M5 and the old M6 in the milestone table of `game-plan.md`.

## What was decided

- **Frame.** The player chairs the development committee of a small region of a fictional country that goes to war and manages its military and civil industrialisation. Calendar: January 1938 to December 1945, 96 months. Fictional world, period technology, casualties as numbers only, bombing shown without graphic content (bombs falling, buildings, roads and track damaged). Only the campaign exists; a sandbox may come later and will be designed then.
- **Time.** Day 1200 s (the cycle of people's lives), month 3 days (the calendar), so the campaign is 288 days: 96 hours at 1x, about 12 at 8x, and in practice 20 to 30 hours of play. The step (0.05 s) does not change.
- **Budget.** Fares and cargo pay still earn money for the regional treasury; a monthly allocation from the government is added, set by the phase of the war and the standing. The loan becomes the ministry's advance (same mechanics, new name). Imports are paid from the budget. Bankruptcy is replaced by dismissal.
- **Two scores.** The government's standing (0 to 100) rises with orders met and falls with orders failed, fines and deficit. The towns' satisfaction is the existing number. They compete: what goes to the state is not available to the towns, and a discontented town loses workers. Standing under 20 for 6 months in a row is dismissal and the end of the game. The campaign ends in December 1945 with one of four grades, from the final standing, the production delivered and the average satisfaction of the towns.
- **Orders replace contracts.** Three kinds: a production quota (deliver N of a cargo to the state at a gateway by a date), a state work (the government founds a factory on the map, for instance a military truck plant, and wants it supplied and connected, with an output target) and a civil need (what the towns already ask for). Meeting one gives a bonus in the budget and standing; failing costs both. Scripted phases (rearmament 1938-39, mobilisation 1939-41, total war 1942-44, the end 1945) change the mix of orders and the size of the allocation; the concrete orders inside a phase are drawn by the seed at random intervals (not on a monthly beat), with the deadline and the reward drawn within a range, a cap of 3 active and 5 open, an unaccepted offer expiring after two months, and no repeat of the same cargo and pair right after. An order can only be accepted when the player already has stations that cover its source and destination. Cargo counts when loaded at a station covering the source and unloaded at one covering the destination, by any vehicle of the company on any line. Late delivery pays in proportion to what arrived; failing costs a quarter of the bonus and, when the destination is a Town, a little satisfaction. The first order comes in month 1, is easy (for example wood or food to a gateway) and doubles as the tutorial.
- **Gateways.** Two, on the map edge, one road and one rail, placed by the seed, to be connected by the player. Each is a station that is also an industry. Import: the player asks the government for a shipment (coal, ore, rubber, fuel), pays per unit from the budget (dearer than producing it locally), and it arrives as stock at the gateway, one shipment per request with size and interval from the tuning. Export: what the state asks for is delivered to a gateway and counts toward the orders.
- **Cargo chain.** About 14 cargo types and 13 industries, deep only where it pays. Raw: coal, iron ore, wood, stone, grain (rubber and oil only by import). Materials: steel (iron ore and coal), planks, building materials, fuel (from imported oil). Components, for the military truck only: engines (steel and fuel), bodies (steel and planks), tyres (imported rubber). Final: the military truck, from engines, bodies and tyres, exported to a gateway. Food and goods as they are. Coal and iron ore exist on every map but scarce (one deposit each, far from each other), so mining at home is an investment to weigh against importing. Rubber and oil never exist locally. State works appear by order, in the middle of the campaign, at a feasible place drawn by the seed.
- **Electricity** is a regional capacity, not a cargo and not a network. Power plants that are supplied with coal add megawatts to the whole region; each industry has a demand; a shortage lowers every output in proportion; the government sells power from outside by the month at a price in the tuning, with no transport.
- **Workers.** Big industries (steel mill, truck plant, engine works, power plant) have jobs. Residents fill them through the existing trip groups, on foot, by car or by the company's lines. Output is proportional to staffing. The people's own movement fills part of the jobs; the player's bus lines to the factories are what lets the rest be filled, a facilitator the player has to manage, not a replacement.
- **Fleet by year.** A model has the year from which it can be bought. Road: bus (1938) and a bigger bus (1941); light truck (1938) and heavy truck (1940). Rail: steam locomotive (1938, cheaper, slower, dearer to keep) and diesel (1942); coaches and freight wagons as now, and freight stays generic (one road truck or wagon carries any freight cargo). The old-vehicle rule of `CONTEXT.md` stays as a repair-cost multiplier by the age of the model, with no permanent wear. Later the player is to choose among many models, which is meant to be a selling point, so the roster stays in the tuning data and the code never names a model. The three eras of 1900 to 2020 in the first game plan are dropped.
- **Effects of the war**, four slices in this order: rationing (fuel and rubber dearer and scarcer in the phases of war), conscription (waves of residents leaving every town, which lowers the jobs and so the output), requisition (the army buys part of the fleet at fixed moments at a table price; the player cannot refuse and can prepare for it) and bombing (from 1942, on gateways, factories and railway: Wrecks, blocked track, damaged buildings, cleared by the crews; casualties as numbers; shown with bombs falling and the damage left on the map). Rationing and bombing are scaled by the frequency option of M5.
- **Start of a game.** Towns with streets and some industries in place, no links between them, as today; the first order in month 1; the budget and allocation from the tuning.

## Slices

| Slice | Content |
|---|---|
| C1 (done) | The new time scale: day 1200 s, month 3 days, calendar from January 1938, every per-day, per-month and per-year number re-derived (upkeep, interest, supply, town needs, growth budget, trip windows, pay decay), goldens set again and checked on macOS and Linux. Before M5. |
| M5 | Maintenance (`docs/m5-plan.md`): 5a, 5b, 5c, 5f. |
| C2 (done) | The chain: the new cargo types and industries, scarce coal and ore, generator rules, the Industries window (section 5.25 of `project-status.md`). The truck chain is founded by orders, so its works have count 0 in the tuning until C5. |
| C3 (done) | Gateways: placement, stock, import shipments with the price, export, the window (section 5.26 of `project-status.md`). |
| C4 (done) | The budget and standing: allocation, the advance, standing, dismissal, the four grades, the HUD (section 5.27 of `project-status.md`). |
| C5 (done) | Orders: the three kinds, the phases, the random schedule, acceptance, reward and failure, the Orders window and the first order (section 5.28 of `project-status.md`). |
| C6 | Electricity: plants, demand, shortage, the monthly purchase. |
| C7 | Workers: jobs in big industries, staffing, output by staffing, bus lines to factories. |
| C8 | The fleet by year, the new models, the old-vehicle repair multiplier. |
| C9 | The war: rationing, conscription, requisition, bombing, in that order, each its own slice with tests. |
| M6 | Save and load, overlays, balancing, art, audio (the old M6 without eras). |

Each slice has its tests and its own pinned hashes where the world changes, and is committed only when the user asks. The 3D side of each is compiled and its GDScript parsed here; the user plays it.

## Names (decided 2026-10-21)

- **Country:** the Republic of Veldmar. **Region:** the Marren Valley. The committee is the Marren Valley Development Committee. Neighbours, the enemy and the allies are never named: the world beyond the gateways is "the rest of the world", the war is "the war". This keeps ADR 0006's fictional world.
- **Towns:** 24 names in `towns.names` of `data/tuning.json`, invented in a Central European manner (-burg, -au, -feld, -moor, -holt, -hof) and checked not to be real places: Aldermark, Brennau, Corvenholt, Dravik, Eskerfeld, Falkenmoor, Grauwald, Harlenburg, Isenhaven, Jelenfeld, Karnbruck, Lorvenau, Marrenstadt, Norvik, Ostenhal, Pellenau, Quernheim, Rothmoor, Sarnfeld, Tavenburg, Ulvenhof, Varnholt, Wessenau, Zorvenburg. They replaced the English placeholders. There are still 24, shuffled by the seed, so no world changes and no golden hash moves (names are not in the hash).
- **Industries and gateways** are named for their town and type as now ("Aldermark Steel Mill"); the two gateways are named from the edge they sit on and their kind ("North Road Gate", "East Rail Gate"), see C3.
- The country and region names are not in code yet; the HUD and the Orders window will read them from a `campaign` section of the tuning when C4 and C5 need them.

## Deposits (decided in C2)

A deposit of coal or iron ore is one per map in every setting, within 2200 m of a town and 1500 to 2200 m from the other kind, so that a steel mill can stand within 1500 m of both. If the map has no such place it is put anywhere that will do and the new-game note says so. The numbers are `industries.generation.deposit*` in the tuning.

## Budget and standing (decided in C4)

Phases start at months 0, 20, 48 and 84 (Rearmament to August 1939, Mobilisation to December 1941, Total war 1942 to 1944, The end 1945). The allocation is 45 000, 60 000, 80 000 and 50 000 a month at a standing of 50, times 0.5 + standing/100. Standing starts at 60 and each month moves by deliveries to the gateways against what the phase expects (within 3 points), minus 2 for a deficit, minus a point per 2 000 of fines (at most 5). Under 20 for 6 months is dismissal. The grade score is half the standing, 30 % the value delivered against 600 000 and 20 % the towns' satisfaction, with thresholds 75, 55 and 35. All of it is in the `campaign` section of the tuning and is a first guess to be balanced once the orders (C5) exist.

## Open

- How import prices rise with rationing (the base prices, the shipment size and the interval are in the tuning since C3).
- The numbers of electricity (megawatts of a plant, demand of each industry) and of staffing, and how the people's own commuting is measured against the jobs.
- How conscription, requisition and raids are scheduled and how large they are; whether a raid can be hit by a defence the player builds.
- The Orders, Budget and Gateway windows and the war's look in 3D; the model choice for vehicles.
- What changes of the trip schedule in a day of 1200 s with shifts at factories.
