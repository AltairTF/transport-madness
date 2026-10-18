# Simulation core is a pure C# library with no Godot dependency

The traffic simulation (vehicles, lanes, signals, incidents, economy) is the performance bottleneck and the game's signature, so it is written in C# in its own assembly that never references Godot types. GDScript handles UI and scene glue; the scene only reads simulation state and draws it. We rejected GDScript (too slow for 1000+ vehicles) and C++ GDExtension (build and crash-debugging cost outweighs the extra 1.3-2x for a solo dev iterating on many rules). A pure library also runs headless in tests, and its hot loops can later be ported to C++ without touching the rest.

Consequence: development needs the Godot .NET editor build and the .NET SDK.
