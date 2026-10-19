namespace TransportMadness.Sim;

/// <summary>
/// A player action on the simulation. Commands are applied between steps through <see cref="Simulation.Execute"/>,
/// which stamps each with the tick it ran at, so a run can be replayed from its seed and its command log (ADR 0003).
/// </summary>
public abstract record SimCommand;

/// <summary>Builds a road (straight, or bending by <paramref name="Sweep"/> radians). Result: number of road pieces built, 0 when refused.</summary>
public sealed record BuildRoadCommand(float X0, float Z0, float X1, float Z1, RoadType Type, float SpeedLimit, float Sweep = 0f) : SimCommand;

/// <summary>Tears a road down. Result: 1 when removed, 0 when refused.</summary>
public sealed record RemoveRoadCommand(int Road) : SimCommand;

/// <summary>Widens a two-lane road to an avenue. Result: 1 when upgraded, 0 when refused.</summary>
public sealed record UpgradeRoadCommand(int Road, float SpeedLimit) : SimCommand;

/// <summary>Places a depot at a node. Result: the depot id, -1 when the node already has one.</summary>
public sealed record AddDepotCommand(int Node, DepotLevel Level) : SimCommand;

/// <summary>Adds ambient vehicles at random free spots. Result: how many were placed.</summary>
public sealed record SeedTrafficCommand(int Count) : SimCommand;

/// <summary>Breaks down a moving car, picked from the current tick so the choice replays. Result: the vehicle id, -1 when none qualifies.</summary>
public sealed record CrashSomeoneCommand : SimCommand;

/// <summary>A command and the tick it was applied at, i.e. the number of steps that had run before it.</summary>
public readonly record struct LoggedCommand(long Tick, SimCommand Command);
