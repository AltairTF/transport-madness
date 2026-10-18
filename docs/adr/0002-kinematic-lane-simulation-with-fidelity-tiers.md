# Kinematic lane-based microsimulation with fidelity tiers

Vehicles are simulated kinematically on a lane graph (position along lane, lateral offset, speed; car-following, lane changes, overtaking, box-overlap collisions), not as rigid bodies. Jolt physics is used only to animate the scene of an accident after the simulation decides it happened. Full-rigid-body physics for hundreds of vehicles is expensive and unstable, and the kinematic model already gives all the dynamics we want.

To avoid a visible vehicle cap, fidelity drops with relevance: the player's own vehicles and anything near the camera or near them are fully simulated (tier 0), general ambient traffic uses cheaper rules (tier 1), and distant ambient traffic is aggregated into per-road flow and density (tier 2). If the CPU budget is exceeded, effective game speed degrades instead of vehicles being capped. The lane graph is lane-level from day one even though the MVP ships few road types.
