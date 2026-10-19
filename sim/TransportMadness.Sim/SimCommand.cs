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

/// <summary>Narrows an avenue to a two-lane road. Result: 1 when downgraded, 0 when refused.</summary>
public sealed record DowngradeRoadCommand(int Road, float SpeedLimit) : SimCommand;

/// <summary>Changes a road's posted speed limit. Result: 1 when changed, 0 when refused.</summary>
public sealed record SetSpeedLimitCommand(int Road, float SpeedLimit) : SimCommand;

/// <summary>Drags a node to a new place, rebuilding the roads at it. Result: 1 when moved, 0 when refused.</summary>
public sealed record MoveNodeCommand(int Node, float X, float Z) : SimCommand;

/// <summary>Places a depot at a node. Result: the depot id, -1 when refused (the node has no road, it already has a depot, or not enough money).</summary>
public sealed record AddDepotCommand(int Node, DepotLevel Level) : SimCommand;

/// <summary>Adds ambient vehicles at random free spots. Result: how many were placed.</summary>
public sealed record SeedTrafficCommand(int Count) : SimCommand;

/// <summary>Breaks down a moving car, picked from the current tick so the choice replays. Result: the vehicle id, -1 when none qualifies.</summary>
public sealed record CrashSomeoneCommand : SimCommand;

/// <summary>Builds a station at the node near (X, Z), or on the road there, with bays for loading vehicles if asked (it costs more). Result: the station id, -1 when refused.</summary>
public sealed record BuildStationCommand(float X, float Z, bool Bays = false) : SimCommand;

/// <summary>Creates a line through stations (ids, in order; the line returns to the first after the last). Result: the line id, -1 when refused.</summary>
public sealed record CreateLineCommand(int[] Stations) : SimCommand;

/// <summary>Changes the stations a line serves (the same rules as creating one). Vehicles carry on toward the station they were heading for when it is still on the line, else toward the stop at the same place in the new list. Result: 1, 0 when refused.</summary>
public sealed record EditLineCommand(int Line, int[] Stations) : SimCommand;

/// <summary>Gives a line a name of one to 24 characters. Result: 1, 0 when refused.</summary>
public sealed record RenameLineCommand(int Line, string Name) : SimCommand;

/// <summary>Gives a line a colour (0xRRGGBB) for its vehicles and its name in the windows. Result: 1, 0 when refused (no such line, or not a colour).</summary>
public sealed record SetLineColorCommand(int Line, int Rgb) : SimCommand;

/// <summary>Names a station, one to 24 characters, in place of the name made from its town. Result: 1, 0 when refused.</summary>
public sealed record RenameStationCommand(int Station, string Name) : SimCommand;

/// <summary>Closes a station for good: lines skip it, vehicles head for the next stop and people waiting there give up. Nothing is refunded. Result: 1, 0 when it is already closed or does not exist.</summary>
public sealed record CloseStationCommand(int Station) : SimCommand;

/// <summary>Adds bays to a station that has none, for the bay price. Result: 1, 0 when refused (closed, already has bays, or not enough money).</summary>
public sealed record AddStationBaysCommand(int Station) : SimCommand;

/// <summary>Closes a line and sells its vehicles. Result: 1, 0 when there is no such line.</summary>
public sealed record DeleteLineCommand(int Line) : SimCommand;

/// <summary>Buys a vehicle (by its id in the tuning file, for example "bus") at a depot and puts it on a line. Result: the vehicle id, -1 when refused.</summary>
/// <remarks>With <see cref="NearestDepot"/> for the depot, the nearest depot connected to the line by road that has room is used.</remarks>
public sealed record BuyVehicleCommand(int Depot, string Model, int Line) : SimCommand
{
    public const int NearestDepot = -1;
}

/// <summary>Sells a company vehicle for part of its price. Result: the vehicle id, -1 when refused.</summary>
public sealed record SellVehicleCommand(int Vehicle) : SimCommand;

/// <summary>Borrows money, up to the loan limit. Result: 1, 0 when refused.</summary>
public sealed record TakeLoanCommand(long Amount) : SimCommand;

/// <summary>Pays back part of the loan. Result: 1, 0 when refused (more than is owed or than the company has).</summary>
public sealed record RepayLoanCommand(long Amount) : SimCommand;

/// <summary>A command and the tick it was applied at, i.e. the number of steps that had run before it.</summary>
public readonly record struct LoggedCommand(long Tick, SimCommand Command);
