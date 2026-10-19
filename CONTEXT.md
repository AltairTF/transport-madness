# Transport Madness

A 3D transport management game in the lineage of Transport Tycoon and Transport Fever. The player builds a road and rail network that connects towns and industries, runs vehicles on it, and watches the towns grow because of it. Its signature is a living traffic simulation where vehicles move dynamically and things can go wrong.

## Language

**Company**:
The player's transport business. Owns vehicles, stations and the money.

**Town**:
A settlement made of buildings along a grid of streets that generate and consume passengers and goods. Grows when well served. Has a name and a Satisfaction.
_Avoid_: City

**Building**:
A home, a commerce building or a light industry building on a lot beside a street of a Town. Has a size tier that sets how many people live or work in it and what it needs. Gets a Trip group from its people. Grows by upgrading its tier, using the Construction stock it has been delivered. Becomes inactive when it loses all road access, is marked Abandoned after 3 months and demolished a month later.
_Avoid_: House (a Building is not always a home)

**Industry**:
A producer or processor of cargo that sits in a chain. Primary industries (forest, quarry, farm) produce at a fixed rate; processors (sawmill, cement works, food plant, factory) turn inputs into outputs at a fixed ratio. Chains: wood → planks, stone → building materials, grain → food, planks → goods. Its output goes to the Stations that cover it, shared evenly; its inputs are filled by deliveries to those Stations.

**Cargo**:
Anything that is not people and is moved between industries and towns: wood, planks, stone, building materials, grain, food, goods. A Station accepts a Cargo only when a Building or Industry in its radius uses it.

**Satisfaction**:
A number from 0 to 100 for a Town. Falls when buildings are demolished for the Company's roads, when buildings are abandoned, and when the Sustain needs of its buildings go unmet. Below a threshold the Town stops growing. Has no effect on money before M5.

**Sustain stock / Construction stock**:
What a Building holds of delivered Cargo. Sustain (food, goods, planks) is used up every month to meet its needs. Construction (building materials, planks) is spent to upgrade the Building or to found a new one next to it. Deliveries go to the Buildings within the coverage radius of the unloading Station, split by need.

**Connectivity**:
The share of a Town's Buildings that were reached in a month: their Trip groups finished the work and leisure trips within a time budget, by any mode.

**Passenger**:
A person moving between a home and a place of work or leisure.

**Station**:
A place on the road network where vehicles load and unload, covering nearby buildings within a coverage radius. Stands at a road node (a road is split to make one) and stops vehicles on the lanes that run into it. On a map with towns it collects passengers and goods from the Buildings in its radius (a stopgap until trip groups); on a map without, it produces them at a fixed rate.

**Line**:
A recurring route that a group of vehicles runs between stations: they visit its stations in order and start over after the last. Vehicles find their way over the road network on their own.

**Contract**:
A time-limited request for specific cargo or passengers, with a reward.

**Era**:
A period of game time that decides which vehicles and road types are available. The world is fictional.

**Incident**:
Anything that goes wrong on the network: an accident, a breakdown, a blockage. Includes incidents involving ambient traffic.

**Ambient traffic**:
Vehicles not owned by the Company (private cars, etc.) that share the roads and affect congestion and Incidents.

**Trip group**:
A bundle of Passengers travelling together from a home to a work or leisure place. Chooses how to travel (on foot, ambient car, public transport) by time and cost. Has no individual identity.

**Pedestrian**:
A purely visual figure shown on streets and platforms to make the flow of Trip groups visible. Carries no simulation of its own.

**Depot**:
A building where vehicles are serviced and from which a Rescue crew is dispatched. Comes in three levels of capability: a Simple Depot only does basic corrections (Patch) to keep the fleet running, a Workshop also does a Service, and a Complex can do an Overhaul. A Complex also keeps two Rescue crews, the other levels one.
A Depot closes when every road at its node has been torn down; a crew already on its way is lost. When the node is moved, the Depot moves with it.
_Avoid_: "complete Depot" (the top level is called Complex)

**Rescue crew**:
A service that clears Incidents and reopens blocked roads. Response time depends on distance to the nearest Depot. When no Depot can respond in time, a distant fallback service comes instead, slowly.

**Wreck**:
A vehicle that crashed or broke down. It stays where it is and blocks its lane until a Rescue crew clears it.

**Debris**:
Wreckage spread into the neighbouring lane by a head-on collision, so both directions of the road are blocked. Cleared together with the Wrecks of the same Incident.

**Condition**:
How worn a vehicle or road is. Low Condition raises the risk of breakdowns and Incidents.

**Patch**:
A cheap, fast repair that restores some Condition but leaves the vehicle or road section "patched", so it wears faster than normal. Any Depot can do it.

**Service**:
A medium-cost repair that restores a lot of Condition. Needs a Workshop or a Complex.

**Overhaul**:
An expensive, slow repair that restores nearly all Condition and clears accumulated Patches. Needs a Complex.

**Resurfacing**:
The road equivalent of an Overhaul, done by a road crew. Closes or narrows the road while it runs, which affects traffic.

**Old vehicle**:
A vehicle kept past its Era. It has no permanent wear limit, but it burns more fuel and its Patch, Service and Overhaul costs are higher than when new.

**Command**:
A Player action applied to the simulation between steps (build, remove, upgrade, downgrade, re-sign or move a road, place a Depot or a Station, create or delete a Line, buy or sell a vehicle, take or repay a loan, add traffic, cause a crash). Each is recorded with the tick it ran at in the command log, so a run can be replayed from its seed and that log.

**Company vehicle**:
A bus or truck the Company owns, bought at a Depot for a Line. A bus takes passengers; a truck takes whichever freight Cargo is waiting at a stop and wanted at the next one. Unlike Ambient traffic it is routed to its stations, loads and unloads Cargo or Passengers, costs upkeep and earns pay. Comes from the vehicle roster in the tuning data, with its own length.
_Avoid_: "player vehicle" (ambiguous with the camera), "fleet car"

**Pay**:
Money earned for cargo or passengers delivered: quantity times straight-line distance between the stations times the cargo's rate, reduced when the trip took longer than the cargo tolerates.

**Day**:
600 seconds of simulated time (ten minutes), long enough for a commute. Upkeep and loan interest are paid at the turn of a day. Thirty days make a month and twelve months a year (shown on the HUD with the time of day).

**Loan**:
Money borrowed, up to a limit, that costs interest every day. A Company whose net worth stays below minus the debt limit for 73 days goes bankrupt (the sandbox carries on).

**Bridge / Tunnel**:
A stretch of road carried over water or a deep valley (Bridge), or through a hill (Tunnel), instead of being cut or filled into the ground. Chosen automatically when a road is planned, priced per metre, and leaves the ground as it was. Other roads can join one in the middle or pass over or under it.

**Grade-separated crossing**:
A place where one road passes over or under another with no junction between them. Built automatically when the two surfaces are far enough apart in height and there is a Bridge above or a Tunnel below.

**Water surface**:
The height water stands at. It falls gently toward a Sea along one side of the map, so Rivers run downhill. Ground below it is water (lake, river, sea). There are no tides and no boats.

**Junction path**:
The line a vehicle follows from one lane through a junction to the next. Vehicles drive along it, slowing for tight turns. Two paths that cross, merge or run closer than a vehicle is wide conflict; the vehicle that would reach the junction second waits at its edge until the other has passed the place where they meet, and nobody enters a junction whose exit lane is full. Two vehicles that do meet inside a junction crash (a Junction Incident).

## Relationships

- A **Town** is served by **Stations**; a **Station** belongs to one or more **Lines**.
- A **Town** needs **Cargo** deliveries to grow, and its **Passengers** need to reach work and leisure.
- A **Contract** names an **Industry** or **Town** as source and destination.
- A **Line** is run by **Company vehicles**, bought at a **Depot**; they earn **Pay** and cost upkeep every **Day**.
- An **Era** gates which vehicles and road types the **Company** may buy.
