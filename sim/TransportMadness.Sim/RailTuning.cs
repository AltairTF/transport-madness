namespace TransportMadness.Sim;

/// <summary>Rail: building prices and limits, how trains move and reserve track, level crossings, and the locomotive and wagon roster (M4).</summary>
public sealed record RailTuning
{
    public required float TrackCostPerMetre { get; init; }
    public required float CutCostPerCubicMetre { get; init; }
    public required float FillCostPerCubicMetre { get; init; }
    public required float EarthworkSlope { get; init; }
    public required float DemolitionCostPerMetre { get; init; }
    public required float BridgeCostPerMetre { get; init; }
    public required float TunnelCostPerMetre { get; init; }
    public required float MaxGrade { get; init; }
    public required float FreeGrade { get; init; }
    public required float MinRadius { get; init; }
    public required float MaxSweep { get; init; }
    public required float MinTrackLength { get; init; }
    public required float SpeedLimit { get; init; }
    public required float CurveLateralAcceleration { get; init; }
    public required float HalfWidth { get; init; }
    public required float Shoulder { get; init; }
    public required float MaxFillDepth { get; init; }
    public required float MaxCutDepth { get; init; }
    public required float StructureMargin { get; init; }
    public required float MaxTurnAngle { get; init; }
    public required float TurnoutSpeed { get; init; }
    public required float MinTurnoutSpeed { get; init; }
    public required long SignalPrice { get; init; }
    public required long PathSignalPrice { get; init; }
    public required long StationPrice { get; init; }
    public required float StationUpkeepPerDay { get; init; }
    public required long DepotPrice { get; init; }
    /// <summary>A rail depot's building (M6g): half its length, in the direction away from its node, and half its width, and how far its middle stands from the node; its ground is levelled as a pad.</summary>
    public required float DepotHalfLength { get; init; }
    public required float DepotHalfWidth { get; init; }
    public required float DepotReach { get; init; }
    public required long DepotWorkshopPrice { get; init; }
    public required long DepotComplexPrice { get; init; }
    public required float SnapRadius { get; init; }
    public required float StationAlongStep { get; init; }
    public required RailMovementTuning Movement { get; init; }
    public required CrossingTuning Crossings { get; init; }
    public required IReadOnlyList<LocomotiveTuning> Locomotives { get; init; }
    public required IReadOnlyList<WagonTuning> Wagons { get; init; }
}

/// <summary>How a train moves: brakes, resistance, how far ahead it asks for track, and when a wait counts as a deadlock.</summary>
public sealed record RailMovementTuning
{
    /// <summary>Service braking, m/s². Speed profiles toward stops and limits use this.</summary>
    public required float ServiceBrake { get; init; }
    /// <summary>The hardest a train ever brakes, m/s², when it finds itself going too fast.</summary>
    public required float EmergencyBrake { get; init; }
    /// <summary>Share of the service brake the speed profile assumes, so there is room to be early.</summary>
    public required float BrakePlanShare { get; init; }
    /// <summary>Gain, 1/s, of the speed controller.</summary>
    public required float ControlGain { get; init; }
    /// <summary>Metres short of a stop the head halts.</summary>
    public required float StopMargin { get; init; }
    /// <summary>A train asks for the track beyond a signal this far (plus its braking distance) before it.</summary>
    public required float ReserveLookahead { get; init; }
    /// <summary>Rolling resistance as a share of weight.</summary>
    public required float RollingResistance { get; init; }
    /// <summary>Air resistance, newtons per (m/s)².</summary>
    public required float AirResistance { get; init; }
    public required float Gravity { get; init; }
    /// <summary>Seconds a train must have waited for track before it counts as blocked (and a ring of trains waiting for each other as a deadlock).</summary>
    public required float BlockedSeconds { get; init; }
    /// <summary>How far ahead (metres) speed limits are looked at.</summary>
    public required float Horizon { get; init; }
    /// <summary>How far in front of a depot node the train appears: it stands in the depot with the whole body behind the node.</summary>
    public required float DepotSpawnClearance { get; init; }
}

/// <summary>Level crossings: where a track and a road meet at the same height.</summary>
public sealed record CrossingTuning
{
    /// <summary>Half the width of the band the rails take along the track, metres, plus clearance. Road vehicles stop outside it.</summary>
    public required float BandHalfWidth { get; init; }
    /// <summary>Seconds ahead of a train reaching the crossing that the barriers start to come down.</summary>
    public required float WarnSeconds { get; init; }
    /// <summary>Seconds the barriers take to come down; vehicles that can stop do, and those already past the line drive on.</summary>
    public required float LowerSeconds { get; init; }
    /// <summary>Seconds the barriers stay down after the train has cleared the crossing.</summary>
    public required float ClearSeconds { get; init; }
    /// <summary>A crossing must be this far from the end of the road and of the track it is on, and from any junction.</summary>
    public required float MinFromNode { get; init; }
    /// <summary>Tightest angle (radians) between a track and a road that can cross.</summary>
    public required float MinAngle { get; init; }
    /// <summary>Metres before the crossing band a road vehicle stops.</summary>
    public required float RoadStopMargin { get; init; }
    /// <summary>Seconds a train that hit a vehicle stands before it may go on.</summary>
    public required float CollisionHalt { get; init; }
    /// <summary>Price of a level crossing, charged to whoever builds the second of the two ways.</summary>
    public required long Price { get; init; }
    /// <summary>Height difference below which a track and a road meet at grade; above it one passes over the other.</summary>
    public required float Clearance { get; init; }
}

/// <summary>A locomotive: all trains in this game have a cab at each end, so they reverse in a station without running round.</summary>
public sealed record LocomotiveTuning
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The year of the campaign from which the locomotive can be bought.</summary>
    public required int FromYear { get; init; }
    public required float Length { get; init; }
    /// <summary>Kilograms.</summary>
    public required float Mass { get; init; }
    /// <summary>Watts at the rails.</summary>
    public required float Power { get; init; }
    /// <summary>Newtons at standstill.</summary>
    public required float TractiveEffort { get; init; }
    public required float MaxSpeed { get; init; }
    public required int MaxWagons { get; init; }
    public required long Price { get; init; }
    public required float UpkeepPerDay { get; init; }
}

/// <summary>A wagon (or coach) of a train.</summary>
public sealed record WagonTuning
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>A cargo id, or "freight" for any freight cargo.</summary>
    public required string Cargo { get; init; }
    public required int Capacity { get; init; }
    public required float Length { get; init; }
    /// <summary>Kilograms, empty.</summary>
    public required float Mass { get; init; }
    /// <summary>Kilograms per unit of cargo carried.</summary>
    public required float UnitMass { get; init; }
    public required long Price { get; init; }
    public required float UpkeepPerDay { get; init; }
    public required float DwellSeconds { get; init; }
    public required float SecondsPerUnit { get; init; }
}
