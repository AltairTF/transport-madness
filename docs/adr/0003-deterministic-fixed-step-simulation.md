# Deterministic fixed-step simulation driven by a seed

The simulation advances in a fixed step (about 20 per second at 1x; higher speeds run more steps per frame, up to 8x), uses a single seeded RNG, and processes entities in a stable order. The same seed and the same player actions always give the same result. This lets rare incidents be replayed and debugged, and lets headless tests assert exact outcomes. The cost is that any multithreading must be partitioned (for example by lane or road section) and ordered, so it cannot be added freely. A save game is a snapshot of the full state.
