using System.Text.Json;

namespace TransportMadness.Sim;

/// <summary>
/// Simulation tuning and prices, read from <c>data/tuning.json</c> (embedded in this assembly). The file is the only
/// place the numbers live: every key is required and unknown keys are rejected, so a typo fails loudly instead of
/// silently running with a zero. A variant for a test is made with <c>with</c> expressions on a loaded copy.
/// </summary>
public sealed record Tuning
{
    const string ResourceName = "tuning.json";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public required DrivingTuning Driving { get; init; }
    public required TrafficTuning Traffic { get; init; }
    public required ManoeuvreTuning Manoeuvres { get; init; }
    public required LaneChangeTuning LaneChange { get; init; }
    public required OvertakeTuning Overtake { get; init; }
    public required CrewTuning Crews { get; init; }
    public required RoadTuning Roads { get; init; }

    /// <summary>The shipped tuning: a fresh copy of <c>data/tuning.json</c>.</summary>
    public static Tuning Load()
    {
        using var stream = typeof(Tuning).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    public static Tuning FromJson(string json) =>
        JsonSerializer.Deserialize<Tuning>(json, Options) ?? throw new JsonException("Tuning file is empty");
}

public sealed record DrivingTuning
{
    public required float MaxAccel { get; init; }
    public required float ComfortDecel { get; init; }
    public required float MinGap { get; init; }
    public required float TimeHeadway { get; init; }
    public required float MaxBraking { get; init; }
    public required float BrakeJerk { get; init; }
    public required float MinInteractionGap { get; init; }
    public required float JunctionRange { get; init; }
    public required float JunctionMargin { get; init; }
}

public sealed record TrafficTuning
{
    public required float SpawnClearance { get; init; }
    public required int SpawnAttemptsPerVehicle { get; init; }
    public required float SpawnSpeedFactor { get; init; }
    public required float DriverSpeedFactorMin { get; init; }
    public required float DriverSpeedFactorRange { get; init; }
}

public sealed record ManoeuvreTuning
{
    public required int DecisionInterval { get; init; }
    public required float LateralRate { get; init; }
    public required float StallSpeed { get; init; }
    public required float StallLimit { get; init; }
}

public sealed record LaneChangeTuning
{
    public required int Cooldown { get; init; }
    public required float EndMargin { get; init; }
    public required float HinderedGap { get; init; }
    public required float HinderedSpeedShare { get; init; }
    public required float HinderedSpeedDeficit { get; init; }
    public required float LeftGapAdvantage { get; init; }
    public required float LeftSpeedAdvantage { get; init; }
    public required float RightFreeGap { get; init; }
    public required float RightSpeedShare { get; init; }
    public required float FrontGapBase { get; init; }
    public required float FrontGapPerSpeed { get; init; }
    public required float ClosingGap { get; init; }
    public required float ClosingSpeedDelta { get; init; }
    public required float RearGapBase { get; init; }
    public required float RearGapPerSpeedDifference { get; init; }
}

public sealed record OvertakeTuning
{
    public required int Cooldown { get; init; }
    public required float MinStart { get; init; }
    public required float EndAbortMargin { get; init; }
    public required float EndRoom { get; init; }
    public required float StandingObstacle { get; init; }
    public required float MaxLeaderGap { get; init; }
    public required float MinLeaderSpeed { get; init; }
    public required float SlowLeaderShare { get; init; }
    public required float MinSpeedDeficit { get; init; }
    public required float QueueSpeed { get; init; }
    public required float QueueLookahead { get; init; }
    public required float HaltedLookahead { get; init; }
    public required float StuckBase { get; init; }
    public required float StuckPatience { get; init; }
    public required float Boost { get; init; }
    public required float BoostAggression { get; init; }
    public required float PassLengthPad { get; init; }
    public required float MinRelativeSpeed { get; init; }
    public required float SightBase { get; init; }
    public required float SightAttention { get; init; }
    public required float Misperception { get; init; }
    public required float BelievedTimeBase { get; init; }
    public required float BelievedTimeAttention { get; init; }
    public required float SafetyMargin { get; init; }
    public required float SafetyAggression { get; init; }
    public required float AbortTimeBase { get; init; }
    public required float AbortTimeAttention { get; init; }
    public required float ReturnBehindBase { get; init; }
    public required float ReturnBehindPerSpeed { get; init; }
    public required float ReturnAheadBase { get; init; }
    public required float ReturnAheadPerSpeed { get; init; }
}

public sealed record CrewTuning
{
    public required float DispatchDelay { get; init; }
    public required float Speed { get; init; }
    public required float RoadDetour { get; init; }
    public required float FallbackResponse { get; init; }
    public required float ClearDuration { get; init; }
    public required CrewsPerDepot PerDepot { get; init; }
}

/// <summary>How many rescue crews a depot keeps, by its level.</summary>
public sealed record CrewsPerDepot
{
    public required int Simple { get; init; }
    public required int Workshop { get; init; }
    public required int Complex { get; init; }

    public int For(DepotLevel level) => level switch
    {
        DepotLevel.Simple => Simple,
        DepotLevel.Workshop => Workshop,
        _ => Complex,
    };
}

public sealed record RoadTypeTuning
{
    public required float SpeedLimit { get; init; }
    public required float CostPerMetre { get; init; }
}

public sealed record RoadTuning
{
    public required RoadTypeTuning TwoLane { get; init; }
    public required RoadTypeTuning Avenue { get; init; }
    public required float CutCostPerCubicMetre { get; init; }
    public required float FillCostPerCubicMetre { get; init; }
    public required float EarthworkSlope { get; init; }
    public required float DemolitionCostPerMetre { get; init; }
    public required float UpgradeRefundShare { get; init; }
    public required float DowngradeCostPerMetre { get; init; }
    public required float SpeedLimitChangeCostPerMetre { get; init; }
    public required float MaxFillDepth { get; init; }
    public required float MaxCutDepth { get; init; }
    public required float StructureMargin { get; init; }
    public required float BridgeCostPerMetre { get; init; }
    public required float TunnelCostPerMetre { get; init; }
    public required float AvenueStructureFactor { get; init; }

    /// <summary>Price per metre of a bridge or tunnel of the given road type.</summary>
    public float StructureCostPerMetre(RoadStructure structure, RoadType type) =>
        (structure == RoadStructure.Tunnel ? TunnelCostPerMetre : BridgeCostPerMetre) * (type == RoadType.Avenue2x2 ? AvenueStructureFactor : 1f);

    public RoadTypeTuning Of(RoadType type) => type == RoadType.Avenue2x2 ? Avenue : TwoLane;
}
