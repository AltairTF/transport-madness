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

/// <summary>
/// Builds a depot where it is put, not on a node (M6f): against a road when one is near (the road is split there), joined to the nearest road by a Path
/// when one is within reach, or alone, not connected until a road reaches it. See <see cref="Simulation.PlanDepot"/>. Result: the depot id, -1 when
/// refused (in water, a depot there already, or not enough money for it and its Path).
/// </summary>
public sealed record BuildDepotCommand(float X, float Z, DepotLevel Level) : SimCommand;

/// <summary>
/// Raises a depot to a higher level in place, paying the difference in price: a Workshop does a Service and has two bays, a Complex also does an
/// Overhaul and has three. A road depot's crews grow with its level. Result: 1, 0 when refused (no such open depot, not a higher level, or not enough money).
/// </summary>
public sealed record UpgradeDepotCommand(bool Rail, int Depot, DepotLevel Level) : SimCommand;

/// <summary>
/// Has a crew resurface a road: it is as good as new afterwards, costs a price per metre, and is closed (debris in its lanes) for as long as the work takes.
/// Result: 1, 0 when refused (see <see cref="Simulation.WhyNotResurface"/>).
/// </summary>
public sealed record ResurfaceRoadCommand(int Road) : SimCommand;

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

/// <summary>Puts traffic lights at a road junction (a node with at least three roads). Result: 1, 0 when refused (not a junction, one there already, or no money).</summary>
public sealed record BuildTrafficLightCommand(int Node) : SimCommand;

/// <summary>Takes the traffic lights down. Result: 1, 0 when there are none.</summary>
public sealed record RemoveTrafficLightCommand(int Node) : SimCommand;

/// <summary>Puts a priority, yield or stop sign on the approach to the road junction near (X, Z) that is nearest the click (a node with at least three roads and no traffic lights). A priority sign also goes on the approach opposite, if it has none. Replaces a sign already on the approach. Result: the number of signs placed, 0 when refused.</summary>
public sealed record BuildRoadSignCommand(float X, float Z, RoadSignKind Kind) : SimCommand;

/// <summary>Takes down the sign on the approach nearest (X, Z), and the opposite priority sign that went with a priority sign. Result: the number of signs removed, 0 when there is none.</summary>
public sealed record RemoveRoadSignCommand(float X, float Z) : SimCommand;

/// <summary>Builds a track (straight, or bending by <paramref name="Sweep"/> radians) on the rail network, joining it where it meets other tracks. Result: number of pieces built, 0 when refused.</summary>
public sealed record BuildTrackCommand(float X0, float Z0, float X1, float Z1, float Sweep = 0f) : SimCommand;

/// <summary>Tears a piece of track down; its signals go with it. Trains on it are lost. Result: 1, 0 when refused.</summary>
public sealed record RemoveTrackCommand(int Track) : SimCommand;

/// <summary>Puts a signal at the rail node near (X, Z), or on the track there (split to make a node). On a node it faces the track nearest the click; on open track it faces trains going from the track's start toward its end when <paramref name="Forward"/>, else the other way. Result: the signal id, -1 when refused.</summary>
public sealed record BuildSignalCommand(float X, float Z, SignalKind Kind, bool Forward = true) : SimCommand;

/// <summary>Takes a signal down. Result: 1, 0 when refused.</summary>
public sealed record RemoveSignalCommand(int Signal) : SimCommand;

/// <summary>Builds a rail station at the rail node near (X, Z), or on the track there. Result: the station id, -1 when refused.</summary>
public sealed record BuildRailStationCommand(float X, float Z) : SimCommand;

/// <summary>Places a rail depot at the rail node near (X, Z). Result: the depot id, -1 when refused.</summary>
public sealed record AddRailDepotCommand(float X, float Z, DepotLevel Level = DepotLevel.Simple) : SimCommand;

/// <summary>Buys a train (a locomotive and <paramref name="Wagons"/> wagons, by their ids in the tuning file) at a rail depot for a rail line. Result: the train id, -1 when refused.</summary>
/// <remarks>With <see cref="NearestDepot"/> for the depot, the rail depot with the quickest way to the line's first station that can take the train is used.</remarks>
public sealed record BuyTrainCommand(int Depot, string Locomotive, string Wagon, int Wagons, int Line) : SimCommand
{
    public const int NearestDepot = -1;
}

/// <summary>Sells a train for part of its price. Result: the train id, -1 when refused.</summary>
public sealed record SellTrainCommand(int Train) : SimCommand;

/// <summary>Buys a road vehicle (by its id in the tuning file) into a road depot's garage, on no line. Result: the garage id, -1 when refused (no such depot or model, or not enough money).</summary>
/// <remarks>With <see cref="AnyDepot"/> for the depot, the first open road depot is used.</remarks>
public sealed record BuyParkedVehicleCommand(int Depot, string Model) : SimCommand
{
    public const int AnyDepot = -1;
}

/// <summary>Buys a train (a locomotive and <paramref name="Wagons"/> wagons) into a rail depot's garage, on no line. Result: the garage id, -1 when refused. <see cref="BuyParkedVehicleCommand.AnyDepot"/> takes the first open rail depot.</summary>
public sealed record BuyParkedTrainCommand(int Depot, string Locomotive, string Wagon, int Wagons) : SimCommand;

/// <summary>Puts a parked vehicle or train on a line; it comes out of its depot. Result: the vehicle id (the train id for a train), -1 when refused.</summary>
public sealed record AssignParkedCommand(int Parked, int Line) : SimCommand;

/// <summary>Sells a parked vehicle or train for part of its price. Result: its garage id, -1 when refused.</summary>
public sealed record SellParkedCommand(int Parked) : SimCommand;

/// <summary>Takes a road vehicle off its line and sends it to the nearest depot to wait there. Result: 1, 0 when refused.</summary>
public sealed record ParkVehicleCommand(int Vehicle) : SimCommand;

/// <summary>Takes a train off its line and sends it to the nearest rail depot to wait there. Result: 1, 0 when refused.</summary>
public sealed record ParkTrainCommand(int Train) : SimCommand;

/// <summary>
/// Sets a maintenance policy at the company, a line, a road vehicle (by its slot id) or a train (by its id). The most specific policy that does not
/// inherit applies. Result: 1, 0 when refused (no such target, values out of range, or the company's policy set to inherit).
/// </summary>
public sealed record SetMaintenancePolicyCommand(PolicyScope Scope, int Target, MaintenancePolicy Policy) : SimCommand;

/// <summary>
/// Asks for a repair. A road vehicle or a train is repaired at its next stop (it goes to the nearest depot that can do it); a parked vehicle or
/// train, by its garage id, is taken into a bay of the depot it stands in. Result: 1, 0 when refused (see <see cref="Simulation.WhyNotRepair"/>).
/// </summary>
public sealed record RequestRepairCommand(RepairSubject Subject, int Target, RepairKind Kind) : SimCommand;

/// <summary>Changes how often the random events (breakdowns, later signal failures and the campaign's) happen. Result: 1, 0 when refused.</summary>
public sealed record SetFrequencyCommand(IncidentFrequency Frequency) : SimCommand;

/// <summary>A command and the tick it was applied at, i.e. the number of steps that had run before it.</summary>
public readonly record struct LoggedCommand(long Tick, SimCommand Command);

/// <summary>Asks the government for a shipment of an imported cargo (by its id) at a gateway, paying for it now; it arrives after the shipment time. Result: 1, 0 when refused (see <see cref="Simulation.WhyNotImport"/>).</summary>
public sealed record RequestImportCommand(int Gateway, string Cargo) : SimCommand;

/// <summary>Builds an anti-aircraft battery (C13) on the ground at (X, Z), from the month the guns are available. Result: the id of the industry that is the battery, -1 when refused (see <see cref="Simulation.WhyNotBattery"/>).</summary>
public sealed record BuildBatteryCommand(float X, float Z) : SimCommand;

/// <summary>Takes up an order the government has offered (C5); the clock to its deadline starts now. Result: 1, 0 when refused (see <see cref="Simulation.WhyNotAccept"/>).</summary>
public sealed record AcceptOrderCommand(int Order) : SimCommand;

/// <summary>Turns an order that has been offered down, with no penalty. Result: 1, 0 when it is not on offer.</summary>
public sealed record DeclineOrderCommand(int Order) : SimCommand;

/// <summary>Sets the megawatts bought from outside for every month from now on (0 to the limit in the tuning), charged at the turn of the month. Result: 1, 0 when refused.</summary>
public sealed record SetPowerPurchaseCommand(int Megawatts) : SimCommand;

/// <summary>Chooses the rule by which the army will pick vehicles in the requisition wave that is announced (C15b): possible only from the month of notice until the wave comes. Result: 1, 0 when refused.</summary>
public sealed record SetRequisitionRuleCommand(RequisitionRule Rule) : SimCommand;
