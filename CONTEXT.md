# Transport Madness

A 3D transport management game in the lineage of Transport Tycoon and Transport Fever. The player builds a road and rail network that connects towns and industries, runs vehicles on it, and watches the towns grow because of it. Its signature is a living traffic simulation where vehicles move dynamically and things can go wrong.

## Language

**Company**:
The player's transport business. Owns vehicles, stations and the money. In the campaign the Player chairs the development committee of the Region and the Company is the transport business that committee runs.

**Town**:
A settlement made of buildings along a grid of streets that generate and consume passengers and goods. Grows when well served. Has a name and a Satisfaction.
_Avoid_: City

**Building**:
A home, a commerce building or a light industry building on a lot beside a street of a Town. Has a size tier that sets how many people live or work in it and what it needs. Gets Trip groups from its people. Grows by upgrading its tier, using the Construction stock it has been delivered; at the top tier it founds a new Building on a free Lot instead. Becomes inactive when it loses all road access, is marked Abandoned after 3 months and demolished a month later.
_Avoid_: House (a Building is not always a home)

**Deposit**:
A scarce natural resource, a coal mine or an iron mine: one of each on every Region, within reach of a Town and a fitting distance from the other, so that mining both at home is an investment against importing. Marked in the tuning (`deposit`).

**Imported cargo**:
A Cargo that no Industry makes (oil, rubber, ammunition): it can only arrive as an Import shipment through a Gateway, so the Industries that use it idle until there is one.

**Industry**:
A producer or processor of cargo that sits in a chain. Primary industries (forest, quarry, farm, and the two Deposits) produce at a fixed rate; processors (sawmill, cement works, food plant, factory, steel mill, refinery and the military truck's works) turn inputs into outputs at a fixed ratio. Chains: wood → planks, stone → building materials, grain → food, planks → goods, iron ore and coal → steel, imported oil → fuel, and for the military truck steel and fuel → engines, steel and planks → bodies, imported rubber → tyres, then engines, bodies and tyres → trucks. Its output goes to the Stations that cover it, shared evenly; its inputs are filled by deliveries to those Stations. The generator places a processor within reach of a producer of its inputs and of a Town; one that did not fit is reported when a game starts.

**Access point**:
The place where an Industry, a road Gateway or a free-standing Depot meets the roads: a point just outside the edge of its square that faces the nearest Town. It becomes a node of the road network when a road reaches it. A road is built exactly where it is put and is never moved to an Access point; a road that passes within 60 m of one that no road reaches yet (a Depot's too) gets a **Path** from the nearest point of the road to it (from the nearest junction or end when that is within 10 m of one); the build preview shows the Path, and the price includes it. Stations and Depots already stand on nodes, and what a Station covers does not change: the Access point is where vehicles can drive to the Industry's door, and the road node that residents driving to work at the Industry go to.

**Pad**:
The level ground under a Depot or an anti-aircraft battery: its square, at one height (the mean ground under it, or the road at its door for a Depot against a road, plus what the Player raised or lowered it with Page Up and Page Down), blended back to the old ground over 10 m. The ground under a road is left alone. Levelling is paid for by the earth moved (a share of a road's price per cubic metre) and refused where it would cut or fill more than 6 m. A Depot or battery can be turned in steps of 15 degrees (R); a Depot that is turned, or placed with Alt held, stands exactly where it is put and its door is joined to a road within reach by a Path instead of being drawn against the road.

**Path**:
The narrow, slow, cheap stone road that joins an Access point to the roads, made together with the road that passes it. One lane each way, never chosen from the build bar and not upgradable, torn down like any road. An Access point already reached by a road gets no second Path, and a Path that cannot be built (too steep, blocked, not affordable) is left out while the road is still built.

**Power**:
The region's electricity, one number of megawatts and not a cargo or a network. Every Industry asks for some; the supply is a free grid sized for the industries of the start, the Power plants that are burning coal, and power bought from outside by the month. When the demand is higher, every Industry makes less in proportion.

**Staffing**:
How many of its Jobs an Industry has filled, 0 to 1: the share filled by the Trip groups that reached it on the last day that closed (worked out at the turn of the day; 1 before the first day closes, and on a map without Towns). It makes its output times this. A Trip group goes to an Industry with Jobs like to any workplace: on foot, by car to its Access point or by a Line that ends at a Station covering it; a Town with no road or Line to it fills none of its Jobs.

**Call-up**:
A wave of the war in which a share of the people of every Town leave for good, at fixed months. The workforce is then short by a gap that closes slowly, and every Industry makes less in proportion to it. Each wave also lowers the Capacity factor of every Town for good.

**Capacity factor**:
The share (0 to 1) of the nominal people and jobs of its Buildings that a Town still has room for after the Call-up (C15c): each wave multiplies it by one less the wave's share, and it never goes back up. It scales what the Town reports as its people and jobs, the monthly needs of its Buildings, the jobs of its Buildings as workplaces and the people a Building brings when it is founded or goes up a size, so that Growth cannot refill what the Call-up took. The jobs of Industries are not scaled.

**Requisition**:
At fixed months the army buys a share of the Company's vehicles and trains (freight first, the biggest first, the garage too) at a table price, whatever their Condition. The Player is told a month ahead and cannot refuse, but in that month of notice chooses the **Requisition rule** of the wave.

**Requisition rule**:
The order in which the army picks the vehicles of one Requisition wave (C15b): freight first and the biggest (the default), the oldest model first, or passengers first. It is chosen in the month of notice, with a command and the buttons of the Company window, cannot be changed once the wave has fired, and goes back to the default after each wave. The price (80 % of new) does not depend on it.

**Raid**:
From 1942 the enemy bombs the Region at random moments. Each raid is a Flight of bombers that comes in from one side of the map, picks a target by value and drops Bombs on it; what a Bomb does is decided where it lands. Industries stop for days, Gateways, Stations and Depots are shut for a time, Vehicles become wrecks, a rail Signal fails, buildings fall and the road gets craters, which the crews clear like wrecks. People are hurt too: see Casualty, Siren and Cover. The frequency option scales how often.

**Flight**:
A group of 1 to 6 bomber aircraft of a Raid. It enters the map from the side drawn for the game, flies straight at a fixed speed and height over its target (chosen by the value the enemy gives it, less when it is down, idle or defended by Batteries) and out of the map. Each aircraft drops a stick of Bombs on the way unless a Battery has brought it down. A flight is a simulated thing: it is stepped, kept in the state hash and drawn from the simulation.

**Bomb**:
Dropped by an aircraft of a Flight, it falls for a few seconds and lands at an exact point. Its effect is worked out by geometry when it lands: everything inside its Blast radius suffers.

**Blast radius**:
How far a Bomb reaches from where it lands (35 m). A building inside it is destroyed with a chance that falls with the distance; an industry inside it stays down longer with every bomb; a gateway, station or depot inside it is shut; every vehicle inside it is a wreck; the nearest signal fails.

**Battery**:
An anti-aircraft battery: an Industry the Player places by a command (45 000, 3 000 a month) on free level dry ground, from month 20 (September 1939, the Mobilisation phase). It makes nothing; it has Jobs that residents fill (its Staffing) and a stock of Ammunition that trucks deliver like any input. Once a second it fires a salvo at every aircraft of a Flight within its range (700 m) if it has ammunition and is not damaged, and each salvo has a chance, the base chance times its Staffing, to bring the aircraft down. Several batteries add up as one minus the product of what each misses. A bombed battery is damaged like any Industry and stops. The enemy wants a target less the more working batteries cover it (about half at strong cover).

**Ammunition**:
A Cargo that only a Gateway imports (from month 20; priced and rationed like fuel) and that only a Battery uses. A salvo spends a fixed amount of it. A battery that finds its stock empty with an aircraft in range says so once, until it has some again. Nothing in the Region makes it.

**Fire pressure**:
The Staffing of the Batteries that are firing at a point of the sky, added up. A Flight under fire has its Bombs scatter more (up to a cap) and let go early, so that they fall short of the target; an aircraft the fire brings down drops no more Bombs and falls, leaving a flash and a crater where it lands.

**Air raid warning**:
The notice that a Flight has entered the map: how many aircraft, from which side, and how many seconds until they arrive. The target is told only in the last ten seconds.

**Siren**:
Sounds on the whole map when a Flight is launched, and stays on until the last Flight in the air is over (the all-clear). People do not know where the Bombs will fall, so everybody outdoors reacts. A Group that is to leave on foot while it is on stays in.

**Cover** (also **Shelter**):
Where the people on foot and the people waiting in a Station queue go when the Siren sounds: the nearest standing building they can walk to before the first Bomb could land (the Flight's lead time less five seconds to start running). They stay inside until the all-clear and then go on, so their walk is late by the time they spent there. People in cars and buses keep driving, people already in a building stay in it, and people who find no building in reach are caught in the open.

**Casualty**:
A person killed or hurt in a raid, and gone for good from the Group they belonged to (until C14 only a number). A destroyed building takes 35 % of the people really inside it (those at home, at work or at leisure there, and those who took Cover in it), a Bomb takes the same share of the people on foot and of a Station queue within its Blast radius, a wrecked car takes two of its people and a wrecked bus all its riders. People in Cover outside the blast lose nothing. The people of a destroyed home who were elsewhere are not casualties; they are homeless and leave the Region.

**Cargo**:
Anything that is not people and is moved between industries and towns: wood, planks, stone, building materials, grain, food, goods, coal, iron ore, steel, fuel, engines, truck bodies, tyres, military trucks, the Imported oil and rubber, and ammunition. A Station accepts a Cargo only when a Building or Industry in its radius uses it.

**Satisfaction**:
A number from 0 to 100 for a Town. Falls when buildings are demolished for the Company's roads, when buildings are abandoned, and when the Sustain needs of its buildings go unmet. Below a threshold (50) the Town stops growing. It has no direct effect on money; it stops growth below the threshold, civil Orders raise or lower it, and the average of the Towns counts for 20 % of the final Grade.

**Sustain stock / Construction stock**:
What a Building holds of delivered Cargo. Sustain (food, goods, planks) is used up every month to meet its needs. Construction (building materials, planks) is spent to upgrade the Building or to found a new one next to it. Deliveries go to the Buildings within the coverage radius of the unloading Station, split by need.

**Connectivity**:
The share of a Town's homes that were reached in a month: a Trip group living there made a trip to work or leisure within a time budget, by any mode. Below a threshold the Town does not grow.

**Lot**:
A place beside a street of a Town where a Building can stand. A Town has the lots of its blocks and gets more when it builds a new block of streets.

**Growth**:
Once a month a Town whose Satisfaction and Connectivity are high enough lets each Building spend a full Construction stock: it goes up a size, or if it is already the biggest founds a new Building on the nearest free Lot, or on a new block of streets when no Lot is free. A Town never shrinks except by abandonment.

**Rebuilding**:
How a Town recovers from a raid (C15a): a Building a Bomb destroys frees its Lot, and Growth founds new Buildings on free Lots from the Construction stock the player delivers. There is no command to rebuild and the state pays nothing; without Construction stock the Town stays destroyed. A raid never destroys the last standing Building of a Town (standing: not demolished, whether it has a road or not), nor its last Building with a road, so a Town can always be rebuilt from something.

**Passenger**:
A person moving between a home and a place of work or leisure, here one who has chosen the company's Lines. Waits at a Station in a queue for a destination Station; a bus takes only those whose destination is on its Line and they leave it there.

**Stop**:
A place a road Line's vehicles stop at: a road Station, or a stop at an Industry or a road Gateway, which stands at its Access point and covers only that Industry or Gateway (what it makes is loaded there, what it uses is unloaded there, the people who work there get off and on, and the Line is paid by the distance between the Access points); it needs a road to reach the Access point and opens again when one does. Each stop of a Line has options the Player sets: **load** (on or off, and which cargo when the place makes more than one), **unload** (on or off) and an **unload share**, the percentage of what the vehicle took on at the last loading stop that it unloads here (unset: everything the place takes; with a share, what the place has no room for stays aboard). _Avoid_: "dock" for what the Player sees; the code calls the stop at an Industry a dock.

**Reach**:
How far a road Station's coverage of buildings spreads along the streets beyond its circle (150 m). The walk along the streets from the Station's node to the building's access node, plus the straight walk from there to the door, gives a coverage from 1 (up to 150 m) falling in a line to 0 at 300 m. People at a building with coverage under 1 take the Station with that chance (each Trip group draws once for each Station and place, from its own seed) and walk the street distance to it; inside the circle coverage is full, as it always was. Only buildings and only road Stations: Industries, gateways, the cargo a Station accepts and delivers, and rail Stations keep the circle. _Avoid_: "partial coverage" for what the circle gives.

**Station**:
A place on the road network (a road Station) or the rail network (a rail Station) where vehicles or trains load and unload, covering nearby buildings within a coverage radius. Stands at a road node (a road is split to make one) and stops vehicles on the lanes that run into it. On a map with towns, Passengers queue at it for the Station they are going to and Cargo comes from the Industries it covers; on a map without, it produces them at a fixed rate.

**Bay**:
A place beside the right-most lane of a Station with bays where a loading vehicle waits out of the lane, so the traffic behind it drives on. A Station with bays has room for a few vehicles at once (the next one loads in the lane as at any Station); a vehicle pulls back into the lane when there is a gap. Costs more to build and to keep.

**Line**:
A recurring route that a group of vehicles (a rail Line: trains) runs between stops, all of them on the road network or all rail Stations: they visit its stops in order and start over after the last. A road Line's stops are road Stations or Stops at an Industry or road Gateway. Vehicles find their way over the road network on their own. Has a name the Player can change, and its stations can be added, dropped and reordered while its vehicles run.

**Track**:
One piece of single railway between two nodes, straight or an arc, used by trains in both directions. Several meet at a node (a rail junction) and a train may pass from one to another there when the turn is gentle enough, slowing for it. Built with cut and fill, Bridges and Tunnels like a road, but steeper than 4 % or tighter than 90 m radius is refused.
_Avoid_: Rail (the network), Line (that is a route)

**Train**:
A locomotive and its wagons (a consist) running a rail Line. Has a real length, a mass that includes its load, traction that falls with speed and brakes that need a kilometre at speed; is held to the speed limit of every track and bend any part of it is on. Has a cab at each end, so it turns round in a Station without a loop. Cannot hit another train: it moves only on track it holds.
_Avoid_: Vehicle, in the sense of a Company vehicle (those are buses and trucks)

**Locomotive / Wagon**:
The two parts of a Train in the tuning roster: the locomotive has the power, a wagon (coach for people, freight wagon for cargo) has the capacity. A train has one locomotive and one to eight wagons of one kind.

**Rail depot**:
Where trains are bought and appear, at a rail node. Not a Depot (that sends Rescue crews).

**Signal**:
A post at a rail node facing the trains that arrive along one Track. A **block signal** makes a train wait unless every track beyond it, up to the next Boundary, is free; a **path signal** only the tracks the train will take. Where there is none, a train asks as for a block.

**Boundary**:
A rail node with a Signal, a Station or a Rail depot. Splits the network into the stretches (blocks) that trains hold one at a time. A train holds a stretch from when it passes the boundary where it starts until its rear passes each piece.

**Hold / reservation**:
The track and nodes a Train has been given and no other train may use. Released behind the rear. A train asks for the next stretch before it needs it, and waits at the boundary when it cannot have it.

**Deadlock**:
Trains that wait for each other for good: each has held a stretch the next one needs. Found when they have waited a long time, reported once, and marked on the trains. Sold, or relieved by a passing loop with path signals.

**Level crossing**:
A place where a Track and a road meet at the same height. Closes (barriers down, lights flashing) when a train that will cross is about to arrive; road vehicles stop at a line before it and do not enter when the road beyond is full; a train that finds a vehicle in it hits it (a Wreck that blocks the crossing too) and a train stops short of a wreck or a standing vehicle it can see. Not made where one passes over the other on a Bridge or in a Tunnel.

**Traffic light**:
Signals at a road junction of three or more roads: the approaches fall in two groups by the street they come along, and the groups take turns at green with a short red for both between. A vehicle that can still stop stops at the edge of the junction on red.

**Priority road, yield sign, stop sign**:
Signs on the approach of a road to a junction (never at a junction with traffic lights). Every approach has a rank: with a priority sign (which also goes on the approach across the junction) it goes first, without a sign it comes second, with a yield or stop sign it comes last. A vehicle does not wait for one of lower rank that has not entered the junction and waits for one of higher rank however the two arrive, unless it can be through first; the same rank takes the junction by arrival. A stop sign first holds the vehicle still at the line for a moment.

**Ghost**:
A Company vehicle leaving a Depot whose doorway is busy: it waits inside until there is room, drives out without cars braking for it (never into another vehicle), and becomes an ordinary vehicle once its whole length is out and the lane has room. A train bought while the depot is in use waits inside instead.

**Garage**:
The Company vehicles and trains standing in a Depot on no Line: bought without one, or sent back from a Line. They pay a tenth of their upkeep while they wait; each can be put on a Line of its kind, or sold.

**Order** (replaces the Contract of the first plan):
A request from the Government with a deadline: a production quota (deliver an amount of a Cargo to the state at a Gateway), a state work (a factory the state founds on the map, to be supplied and connected, with an output target) or a civil need. Offered at random times inside the Phase of the war, accepted or ignored by the Player, rewarded with Budget and Standing when met and penalised when failed.

**Model year** (replaces the Era of the first plan):
The year of the campaign from which a vehicle model or locomotive can be bought. The world is fictional, the period is 1938 to 1945. A model that has been on sale for more than three years is old: its repairs cost more, up to 60 % more, and it never wears for good.

**Incident**:
Anything that goes wrong on the network: an accident, a breakdown, a blockage. Includes incidents involving ambient traffic.

**Ambient traffic**:
Vehicles not owned by the Company (private cars, etc.) that share the roads and affect congestion and Incidents. In towns most of it is Trip cars: a car stands for five people of a Trip group, drives from the road node by its home to the node by its destination, and is taken off the road near it.

**Trip group**:
A bundle of at most ten people of one Building who travel together from home to a workplace (a Building with Jobs or an Industry with Jobs) and a leisure place and back, four trips a day at times drawn once from the seed inside the windows of the tuning file. Chooses how to travel by time and money: on foot when the place is close, otherwise by ambient car (if the group has one) or by the Company's Lines. Has no individual identity, but its people can be fewer than ten (or than its home's share): Casualties and the call-up take them from the Group for good. Ends (is retired) when its home is demolished or nobody is left, and the jobs it held go back to its workplace. The groups that reach an Industry are what give it its Staffing.

**Congestion estimate**:
How many times longer than on empty roads people expect a car trip to take, kept for each hour of the day and moved toward what each finished car trip really took. The car's cost in the choice of Mode uses it, so a jammed rush hour sends people to the Lines, or nowhere.

**Mode**:
How a Trip group goes: on foot, by car or by public transport. Cached per pair of Buildings and chosen again when the roads or the Lines change. A trip that no mode can make is not made (the group stays); the way home is always made, on foot if need be.

**Pedestrian**:
A purely visual figure shown on streets and platforms to make the flow of Trip groups visible. Carries no simulation of its own.

**Depot**:
A building where vehicles are serviced and from which a Rescue crew is dispatched. Comes in three levels of capability: a Simple Depot only does basic corrections (Patch) to keep the fleet running, a Workshop also does a Service, and a Complex can do an Overhaul. A Complex also keeps two Rescue crews, the other levels one.
A Depot goes anywhere the Player puts it. Within 24 m of a road it stands against it and its Access point is on the road (the road is split there); with a road within about 74 m a Path is built from the road to its Access point, charged as a road; with none near it stands alone. It starts as a Simple Depot and is raised by upgrades in place, which change its look. A Depot is **connected** while a road reaches its node (or, for one that stands alone, its Access point); a Depot that is not connected sends no crews, repairs nothing and takes no vehicles, and a crew already on its way is lost, until a road reaches it again (a road built within 60 m of its Access point gets a Path to it). When the node is moved, the Depot moves with it.
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
A vehicle whose model is old. It has no permanent wear limit and top speed does not fall, but its Patch, Service and Overhaul costs are higher than when new (a multiplier by the age of the model, in the campaign).

**Maintenance policy**:
What a Company vehicle or train does about its Condition: when it is due (days since its last repair, Condition below a floor, either or both) and which repair it gets (Patch, Service or Overhaul), or nothing. Set for the whole Company, for a Line or for one vehicle; the most specific one wins and "inherit" passes the choice up. A due vehicle finishes unloading at its next stop, loads no more and goes to the nearest Depot able to do that repair, waits for a bay, and returns to its Line; with no such Depot it keeps running and the game says so.

**Bay (depot)**:
A place in a Depot where one repair is done at a time. A Simple Depot has one, a Workshop two, a Complex three; vehicles that wait for one queue inside as in the Garage. Not the Bay of a Station.

**Breakdown**:
A Wreck made by wear instead of a crash: the risk per metre driven is none while Condition is above 60 and grows as it falls. Cleared by a Rescue crew like any Wreck; the cargo is lost, and the vehicle goes to the Garage of the nearest Depot with minimal Condition. Only Company vehicles and trains wear.

**Signal failure**:
A Signal that shows red for some minutes whatever the track beyond it, until a Rescue crew repairs it. Trains still cannot hit each other; they wait, and a Deadlock may follow.

**Frequency option**:
Off, low, normal or high: scales the random breakdowns, signal failures and, in the campaign, random events and raids. It does not scale the incidents that come out of the driving (failed overtaking, junction crashes).

**Campaign** (ADR 0006):
The game's one mode for now: the Player chairs the development committee of a Region of a fictional country at war, from January 1938 to December 1945, managing its military and civil industrialisation.

**Country**:
Veldmar, the fictional republic that goes to war. Its neighbours are never named; the Gateways lead to "the rest of the world". The names are made up in a Central European manner and chosen not to be real places.

**Region**:
The map: a small part of the country (the Marren Valley), with Towns and Industries, joined to the rest of the world only by Gateways.

**Phase**:
A stretch of the campaign (Rearmament to August 1939, Mobilisation to December 1941, Total war 1942 to 1944, The end 1945) that sets the size of the Allocation and what the Government expects to be delivered, and the mix of Orders.

**Budget / Allocation / Advance**:
The money the Player spends. Fares and cargo pay earn it; at the turn of every Month the Government adds an Allocation, the Phase's amount times a factor of the Standing (half at a Standing of 0, 1.0 at 50, one and a half at 100); the Advance (the old Loan) is borrowed from the ministry. Imports are paid from it, and it can go below zero, which costs Standing.

**Standing**:
A number from 0 to 100 for how the Government sees the Player, 60 at the start. Each Month it moves by what was delivered to the Gateways against what the Phase expects, falls for a deficit and for fines (and rises with Orders met and falls with Orders failed). Under 20 for six Months in a row the Player is **dismissed** and the game ends. Competes with the Satisfaction of the Towns.

**Grade**:
The end of the campaign in December 1945 gives one of four grades (Distinguished, Creditable, Adequate or Unsatisfactory service) from a score: half the Standing, 30 % the value delivered to the Gateways and 20 % the average Satisfaction of the Towns. Dismissal gives none.

**Gateway**:
A road or rail connection on the edge of the Region where goods come in and go out, one of each kind per map. Behaves like an Industry that a Station of its own mode covers: an Import shipment arrives there as stock to be carried inland, and Cargo a vehicle unloads there is exported (it pays like any delivery, and counts toward Orders).

**Import shipment**:
Cargo the Player asks the Government to send from outside the Region (coal, ore, oil, rubber, fuel, and from month 20 ammunition), paid at once (from the Budget in the campaign) at a higher price per unit than the same Cargo produced locally. One at a time per Cargo and Gateway, it arrives after a delay as stock at the Gateway.

**Command**:
A Player action applied to the simulation between steps (build, remove, upgrade, downgrade, re-sign or move a road, place a Depot or a Station, rename, close or add bays to a Station, create, edit, rename, colour or delete a Line, buy or sell a vehicle or train, park a vehicle or put it on a Line, set a Maintenance policy or ask for a repair, ask for an Import shipment, accept or decline an Order, set the power purchase, take or repay a loan, add traffic, cause a crash; `SimCommand.cs` has the full list). Each is recorded with the tick it ran at in the command log, so a run can be replayed from its seed and that log.

**Save**:
A snapshot of the whole simulation (the tuning it was started with included) in a file, with the camera and the news feed of the game layer, so that a game can be taken up again exactly where it was: a loaded game runs on as the saved one would have. Named by the Player, or one of the three Autosaves written at the start of each month, or the quick save (F5, loaded with F9). A save of another version of the game is refused, not converted. The command log of a loaded game starts again from the save.
_Avoid_: "savegame slot" for the file (a slot is only the named kind)

**Overlay**:
A view of the map that colours one thing and dims the rest, chosen from the Map view bar or with V: Traffic (vehicles per kilometre of lane over the last hour), Incident risk, Road condition, Coverage (what the Stations cover), Anti-aircraft (gun range and the run in of the Raids), Power, Staffing and Connectivity. One at a time, with a legend. It only shows; it changes nothing in the simulation.

**Company vehicle**:
A bus or truck the Company owns, bought at a Depot for a Line. A bus takes passengers; a heavy truck and a large bus come on sale later in the campaign; a truck takes whichever freight Cargo is waiting at a stop and wanted at the next one. Unlike Ambient traffic it is routed to its stations, loads and unloads Cargo or Passengers, costs upkeep and earns pay. Comes from the vehicle roster in the tuning data, with its own length.
_Avoid_: "player vehicle" (ambiguous with the camera), "fleet car"

**Pay**:
Money earned for cargo or passengers delivered: quantity times straight-line distance between the stations times the cargo's rate, reduced when the trip took longer than the cargo tolerates.

**Day**:
1200 seconds of simulated time (twenty minutes), the cycle of people's lives: a commute is a small part of it. Upkeep and loan interest are paid at the turn of a day. Three days make a **Month** (the calendar, 3600 s) and twelve months a year, counted from January 1938 and shown on the HUD with the time of day; the campaign of 96 months is 288 days. Monthly rates (production, needs, growth) are per month of 3600 s.

**Loan**:
Money borrowed, up to a limit, that costs interest every day. In the campaign it is the Advance from the ministry, and bankruptcy is replaced by dismissal.

**Bridge / Tunnel**:
A stretch of road carried over water or a deep valley (Bridge), or through a hill (Tunnel), instead of being cut or filled into the ground. Chosen automatically when a road is planned, priced per metre, and leaves the ground as it was. Other roads can join one in the middle or pass over or under it.

**Grade-separated crossing**:
A place where one road passes over or under another with no junction between them. Built automatically when the two surfaces are far enough apart in height and there is a Bridge above or a Tunnel below.

**Water surface**:
The height water stands at. It falls gently toward a Sea along one side of the map, so Rivers run downhill. Ground below it is water (lake, river, sea). There are no tides and no boats.

**Junction path**:
The line a vehicle follows from one lane through a junction to the next. Vehicles drive along it, slowing for tight turns. Two paths that cross, merge or run closer than a vehicle is wide conflict; the vehicle that would reach the junction second waits at its edge until the other has passed the place where they meet, and nobody enters a junction whose exit lane is full. Two vehicles that do meet inside a junction crash (a Junction Incident).

**Whitebox**:
The plain stand-in the game draws for something that has no art yet: a box (or a few) of the right footprint, height, colour and Access point, read from `data/whitebox.json` and the tuning. The look of a thing can change from Whitebox to Model without its footprint, its door or any rule changing.

**Model**:
A 3D asset drawn for a game object (a Vehicle, a Building, an Industry, a Depot, a Station) in place of its Whitebox. It is scaled uniformly (never stretched) to the footprint or the length the tuning gives the object, keeps the proportions it was made with, and is chosen by the object's type and level and, for Buildings, by one of a few variants drawn from the object's id. Purely visual: the simulation never sees it. Chosen from existing CC0 packs first; each one is recorded in `CREDITS.md`.

**Line marker**:
The small mark in the colour of its Line on the roof of a Vehicle that has a Model, so a Line can be told apart from above; the Model keeps its own colours.

## Relationships

- A **Town** is served by **Stations**; a **Station** belongs to one or more **Lines**.
- A **Town** needs **Cargo** deliveries to grow, and its **Trip groups** need to reach work and leisure.
- An **Order** names an **Industry**, **Town** or **Gateway** as source and destination.
- A **Line** is run by **Company vehicles**, bought at a **Depot**; they earn **Pay** and cost upkeep every **Day**.
- A **Battery** is staffed by **Trip groups**, supplied with **Ammunition** from a **Gateway** through a **Station** that covers it, and lowers the weight the enemy gives the targets it covers.
- A **Siren** sends the **Trip groups** on foot and the people in **Station** queues to **Cover**; **Casualties** of a raid are taken from the **Trip groups** inside the destroyed building, in the blast or aboard the wrecked vehicle.
- A **Model year** gates which vehicles the **Company** may buy.
- A **Maintenance policy** sends a due vehicle to a **Depot** whose level can do the repair; a **Breakdown** is a **Wreck** made by low **Condition**.
- The **Budget** is fed by **Pay** and the **Allocation**; **Orders** met raise **Standing**, which sets the **Allocation**; **Import shipments** come in at a **Gateway** and are paid from the **Budget**.
