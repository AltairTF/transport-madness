# Transport Madness

A 3D transport management game in the lineage of Transport Tycoon and Transport Fever. The player builds a road and rail network that connects towns and industries, runs vehicles on it, and watches the towns grow because of it. Its signature is a living traffic simulation where vehicles move dynamically and things can go wrong.

## Language

**Company**:
The player's transport business. Owns vehicles, stations and the money.

**Town**:
A settlement made of buildings that generate and consume passengers and goods. Grows when well served.
_Avoid_: City

**Industry**:
A producer or consumer of cargo that sits in a chain (e.g. forest → sawmill → shop).

**Cargo**:
Anything that is not people and is moved between industries and towns.

**Passenger**:
A person moving between a home and a place of work or leisure.

**Station**:
A place where vehicles load and unload, covering nearby buildings within a coverage radius.

**Line**:
A recurring route that a group of vehicles runs between stations.

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
A Depot closes when every road at its node has been torn down or moved away; a crew already on its way is lost.
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
A Player action applied to the simulation between steps (build, remove, upgrade, downgrade, re-sign or move a road, place a Depot, add traffic, cause a crash). Each is recorded with the tick it ran at in the command log, so a run can be replayed from its seed and that log.

## Relationships

- A **Town** is served by **Stations**; a **Station** belongs to one or more **Lines**.
- A **Town** needs **Cargo** deliveries to grow, and its **Passengers** need to reach work and leisure.
- A **Contract** names an **Industry** or **Town** as source and destination.
- An **Era** gates which vehicles and road types the **Company** may buy.
