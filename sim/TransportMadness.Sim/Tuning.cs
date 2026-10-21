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
    public required EconomyTuning Economy { get; init; }
    public required StationTuning Stations { get; init; }
    public required TownsTuning Towns { get; init; }
    public required IndustriesTuning Industries { get; init; }
    public required GatewaysTuning Gateways { get; init; }
    public required CampaignTuning Campaign { get; init; }
    public required OrderTuning Orders { get; init; }
    public required TripsTuning Trips { get; init; }
    public required RailTuning Rail { get; init; }
    public required MaintenanceTuning Maintenance { get; init; }
    public required IReadOnlyList<CargoTuning> Cargo { get; init; }
    public required IReadOnlyList<VehicleModelTuning> Vehicles { get; init; }

    /// <summary>The shipped tuning: a fresh copy of <c>data/tuning.json</c>.</summary>
    public static Tuning Load()
    {
        using var stream = typeof(Tuning).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    public static Tuning FromJson(string json)
    {
        var tuning = JsonSerializer.Deserialize<Tuning>(json, Options) ?? throw new JsonException("Tuning file is empty");
        tuning.Towns.Validate();
        tuning.Industries.Validate();
        tuning.Gateways.Validate();
        tuning.Campaign.Validate();
        tuning.Orders.Validate(tuning);
        tuning.Trips.Validate();
        tuning.Maintenance.Validate();
        return tuning;
    }
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
    public required float TurnLateralAcceleration { get; init; }
    public required float MinTurnSpeed { get; init; }
    public required float ConflictDistance { get; init; }
    public required float CrossingSafetyTime { get; init; }
    public required float ExitQueueSpeed { get; init; }
    public required float WideConflictDistance { get; init; }
    public required float WideBodyClearance { get; init; }
}

public sealed record TrafficTuning
{
    public required float SpawnClearance { get; init; }
    public required int SpawnAttemptsPerVehicle { get; init; }
    public required float SpawnSpeedFactor { get; init; }
    public required float DriverSpeedFactorMin { get; init; }
    public required float DriverSpeedFactorRange { get; init; }
    public required int AmbientPerTown { get; init; }
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
    public required int IncidentsKept { get; init; }
    public required float DepotSnapRadius { get; init; }
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
    public required float CrossingClearance { get; init; }
    public required float BridgeCostPerMetre { get; init; }
    public required float TunnelCostPerMetre { get; init; }
    public required float AvenueStructureFactor { get; init; }
    public required TrafficLightTuning TrafficLight { get; init; }
    public required RoadSignTuning Signs { get; init; }

    /// <summary>Price per metre of a bridge or tunnel of the given road type.</summary>
    public float StructureCostPerMetre(RoadStructure structure, RoadType type) =>
        (structure == RoadStructure.Tunnel ? TunnelCostPerMetre : BridgeCostPerMetre) * (type == RoadType.Avenue2x2 ? AvenueStructureFactor : 1f);

    public RoadTypeTuning Of(RoadType type) => type == RoadType.Avenue2x2 ? Avenue : TwoLane;
}

/// <summary>Traffic lights at road junctions: two groups of approaches take turns.</summary>
public sealed record TrafficLightTuning
{
    public required long Price { get; init; }
    public required float UpkeepPerDay { get; init; }
    /// <summary>Seconds one group of approaches has green.</summary>
    public required float GreenSeconds { get; init; }
    /// <summary>Seconds with red for everybody between the two greens.</summary>
    public required float ClearSeconds { get; init; }
}

/// <summary>Priority, yield and stop signs at the approaches of road junctions.</summary>
public sealed record RoadSignTuning
{
    public required long PriorityPrice { get; init; }
    public required long YieldPrice { get; init; }
    public required long StopPrice { get; init; }
    /// <summary>How near a click must be to a junction node to pick it, metres.</summary>
    public required float PickRadius { get; init; }
    /// <summary>How far before the junction a vehicle that must give way starts to watch for traffic, and a vehicle at a stop sign starts to brake, metres. The cross traffic is looked for this far back too.</summary>
    public required float ApproachRange { get; init; }
    /// <summary>Seconds a vehicle stands still at the line of a stop sign before it may go on.</summary>
    public required float StopSeconds { get; init; }
    /// <summary>Speed below which a vehicle counts as standing still at a stop sign, m/s.</summary>
    public required float StopSpeed { get; init; }
    /// <summary>How close to the junction a vehicle must be for its wait to count as the stop, metres.</summary>
    public required float StopLine { get; init; }
}

public sealed record EconomyTuning
{
    public required long StartingMoney { get; init; }
    public required long MaxLoan { get; init; }
    public required float DayLength { get; init; }
    public required int DaysPerMonth { get; init; }
    public required int MonthsPerYear { get; init; }
    /// <summary>The year of the first day of a game (the campaign starts in January of it).</summary>
    public required int StartYear { get; init; }
    public required float InterestPerDay { get; init; }
    public required float SellRefundShare { get; init; }
    public required float ParkedUpkeepShare { get; init; }
    public required DepotPrices DepotPrice { get; init; }
}

/// <summary>What a depot costs, by its level.</summary>
public sealed record DepotPrices
{
    public required long Simple { get; init; }
    public required long Workshop { get; init; }
    public required long Complex { get; init; }

    public long For(DepotLevel level) => level switch
    {
        DepotLevel.Simple => Simple,
        DepotLevel.Workshop => Workshop,
        _ => Complex,
    };
}

public sealed record StationTuning
{
    public required long Price { get; init; }
    public required float UpkeepPerDay { get; init; }
    public required float CoverageRadius { get; init; }
    public required float MinApproach { get; init; }
    public required float StopMargin { get; init; }
    public required float ArrivalGap { get; init; }
    public required float ArrivalSpeed { get; init; }
    public required long BayPrice { get; init; }
    public required float BayUpkeepPerDay { get; init; }
    public required int BayCapacity { get; init; }
    public required float BayExitGap { get; init; }
}

public sealed record CargoTuning
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required float PayPerUnitKm { get; init; }
    public required float FreeSeconds { get; init; }
    public required float ZeroSeconds { get; init; }
    public required float MinPayShare { get; init; }
    public required float SupplyPerDay { get; init; }
    public required float MaxWaiting { get; init; }
}

public sealed record VehicleModelTuning
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Cargo { get; init; }
    public required int Capacity { get; init; }
    public required float Length { get; init; }
    public required float MaxSpeed { get; init; }
    public required long Price { get; init; }
    public required float UpkeepPerDay { get; init; }
    public required float DwellSeconds { get; init; }
    public required float SecondsPerUnit { get; init; }
}

public sealed record TownsTuning
{
    public required IReadOnlyList<string> Names { get; init; }
    public required float InitialSatisfaction { get; init; }
    public required float MaxSatisfaction { get; init; }
    public required float RecoveryPerMonth { get; init; }
    public required float DemolishedPenalty { get; init; }
    public required float AbandonedPenalty { get; init; }
    public required float AccessRadius { get; init; }
    public required int InactiveMonths { get; init; }
    public required int AbandonedMonths { get; init; }
    public required float UnmetPenalty { get; init; }
    public required float InitialStockMonths { get; init; }
    public required float SustainStockMonths { get; init; }
    public required GrowthTuning Growth { get; init; }
    public required TownGenerationTuning Generation { get; init; }
    public required IReadOnlyList<RingTuning> Rings { get; init; }
    public required IReadOnlyList<BuildingTypeTuning> BuildingTypes { get; init; }

    /// <summary>Sizes every kind of building comes in.</summary>
    public const int Tiers = 3;

    public BuildingTypeTuning Of(BuildingType type) => BuildingTypes[(int)type];

    public TierTuning Of(BuildingType type, int tier) => BuildingTypes[(int)type].Tiers[tier];

    internal void Validate()
    {
        if (Names.Count == 0) throw new JsonException("towns.names is empty");
        var types = Enum.GetValues<BuildingType>();
        if (BuildingTypes.Count != types.Length) throw new JsonException($"towns.buildingTypes must list {types.Length} types");
        for (int i = 0; i < types.Length; i++)
        {
            if (!string.Equals(BuildingTypes[i].Id, types[i].ToString(), StringComparison.OrdinalIgnoreCase))
                throw new JsonException($"towns.buildingTypes[{i}] must be '{types[i].ToString().ToLowerInvariant()}'");
            if (BuildingTypes[i].Tiers.Count != Tiers) throw new JsonException($"towns.buildingTypes[{i}] must have {Tiers} tiers");
        }
        if (Rings.Count == 0) throw new JsonException("towns.rings is empty");
        foreach (var ring in Rings)
            if (ring.Mix.Count != types.Length || ring.Tiers.Count != Tiers)
                throw new JsonException("towns.rings need one mix weight per building type and one tier weight per size");
        if (Generation.BlocksMin < 1 || Generation.BlocksMax < Generation.BlocksMin) throw new JsonException("towns.generation blocks are out of order");
    }
}

public sealed record GrowthTuning
{
    public required float MinSatisfaction { get; init; }
    public required float MinConnectivity { get; init; }
    public required float TimeBudgetSeconds { get; init; }
    public required float LotClearance { get; init; }
    public required bool ExtendGrid { get; init; }
}

public sealed record TownGenerationTuning
{
    public required int Attempts { get; init; }
    public required float EdgeMargin { get; init; }
    public required float MinSpacing { get; init; }
    public required float MaxHeightRange { get; init; }
    public required float WaterMargin { get; init; }
    public required int BlocksMin { get; init; }
    public required int BlocksMax { get; init; }
    public required float BlockLength { get; init; }
    public required float LotWidth { get; init; }
    public required float SetbackGap { get; init; }
}

public sealed record RingTuning
{
    public required float Until { get; init; }
    public required float Fill { get; init; }
    public required IReadOnlyList<float> Mix { get; init; }
    public required IReadOnlyList<float> Tiers { get; init; }
}

public sealed record BuildingTypeTuning
{
    public required string Id { get; init; }
    public required IReadOnlyList<TierTuning> Tiers { get; init; }
}

public sealed record TierTuning
{
    public required float Size { get; init; }
    public required float Height { get; init; }
    public required int People { get; init; }
    public required int Jobs { get; init; }
    public required long DemolitionPrice { get; init; }
    public required IReadOnlyList<CargoAmount> Sustain { get; init; }
    public required IReadOnlyList<CargoAmount> Construction { get; init; }

    /// <summary>Radius of the ground a building of this size takes: a road closer than this to its middle (less the road's half width) runs through it.</summary>
    public float Footprint => Size * 1.2f;
}

/// <summary>An amount of a cargo, named by its id in the cargo list.</summary>
public sealed record CargoAmount
{
    public required string Cargo { get; init; }
    public required float Amount { get; init; }
}

public enum IndustryAmount { Few, Normal, Many }

/// <summary>The links with the rest of the world: where the gateways go, what can be imported and at what price, what is exported.</summary>
public sealed record GatewaysTuning
{
    public required float Size { get; init; }
    public required float Height { get; init; }
    public required GatewayGenerationTuning Generation { get; init; }
    public required float StockLimit { get; init; }
    public required float ShipmentSeconds { get; init; }
    public required float IntervalSeconds { get; init; }
    public required IReadOnlyList<ImportTuning> Imports { get; init; }
    public required IReadOnlyList<string> Exports { get; init; }

    internal void Validate()
    {
        if (Imports.Count == 0) throw new JsonException("gateways.imports is empty");
        foreach (var import in Imports)
            if (import.Units <= 0 || import.Units > StockLimit) throw new JsonException($"gateways.imports: a shipment of {import.Cargo} must be between 1 unit and the stock limit");
    }
}

public sealed record GatewayGenerationTuning
{
    public required int Attempts { get; init; }
    public required float EdgeInset { get; init; }
    public required float EdgeMargin { get; init; }
    public required float TownClearance { get; init; }
    public required float MinSpacing { get; init; }
    public required float MinSeparation { get; init; }
    public required float MaxHeightRange { get; init; }
    public required float TownReach { get; init; }
}

/// <summary>A cargo the government will send into the Region: the units of one shipment and the price of each.</summary>
public sealed record ImportTuning
{
    public required string Cargo { get; init; }
    public required int Units { get; init; }
    public required int PricePerUnit { get; init; }
}

public sealed record IndustriesTuning
{
    public required IndustryGenerationTuning Generation { get; init; }
    public required IndustryCounts Counts { get; init; }
    public required IReadOnlyList<IndustryTypeTuning> Types { get; init; }

    internal void Validate()
    {
        if (Types.Count == 0) throw new JsonException("industries.types is empty");
        foreach (var counts in new[] { Counts.Few, Counts.Normal, Counts.Many })
            if (counts.Count != Types.Count) throw new JsonException("industries.counts needs one number per industry type");
    }
}

public sealed record IndustryGenerationTuning
{
    public required int Attempts { get; init; }
    public required float EdgeMargin { get; init; }
    public required float TownClearance { get; init; }
    public required float MinSpacing { get; init; }
    public required float MaxHeightRange { get; init; }
    public required float ChainReach { get; init; }
    /// <summary>A deposit (mine) is placed within this of a town's middle, so that it can be reached by road.</summary>
    public required float DepositReach { get; init; }
    /// <summary>Two deposits of different types lie at least this far apart (so that mining both at home is a real investment) ...</summary>
    public required float DepositMinSeparation { get; init; }
    /// <summary>... and at most this far, so that a steel mill can still stand within <see cref="ChainReach"/> of both.</summary>
    public required float DepositMaxSeparation { get; init; }
}

public sealed record IndustryCounts
{
    public required IReadOnlyList<int> Few { get; init; }
    public required IReadOnlyList<int> Normal { get; init; }
    public required IReadOnlyList<int> Many { get; init; }

    public IReadOnlyList<int> Of(IndustryAmount amount) => amount switch
    {
        IndustryAmount.Few => Few,
        IndustryAmount.Normal => Normal,
        _ => Many,
    };
}

public sealed record IndustryTypeTuning
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required float Size { get; init; }
    public required float Height { get; init; }
    public required string Output { get; init; }
    /// <summary>A scarce natural resource (a mine): the generator keeps it within reach of a town and apart from the other deposits.</summary>
    public required bool Deposit { get; init; }
    public required float PerMonth { get; init; }
    public required float MaxOutput { get; init; }
    public required IReadOnlyList<CargoAmount> Inputs { get; init; }
    public required float MaxInput { get; init; }
    public required IReadOnlyList<float> Color { get; init; }
}

/// <summary>A stretch of the day, in hours (0 to 24), in which a kind of trip starts.</summary>
public sealed record TripWindow
{
    public required float From { get; init; }
    public required float To { get; init; }
}

public sealed record TripWindows
{
    public required TripWindow WorkOut { get; init; }
    public required TripWindow WorkBack { get; init; }
    public required TripWindow LeisureOut { get; init; }
    public required TripWindow LeisureBack { get; init; }

    public TripWindow Of(int leg) => leg switch { 0 => WorkOut, 1 => WorkBack, 2 => LeisureOut, _ => LeisureBack };
}

public sealed record TripsTuning
{
    public required int GroupSize { get; init; }
    public required float CarShare { get; init; }
    public required int PeoplePerCar { get; init; }
    public required float WalkRadius { get; init; }
    public required float WalkSpeed { get; init; }
    public required float WalkDetour { get; init; }
    public required float WorkRadius { get; init; }
    public required float Nearness { get; init; }
    public required float LeisureChance { get; init; }
    public required float JitterMinutes { get; init; }
    public required float LeisureStayMinutes { get; init; }
    public required TripWindows Windows { get; init; }
    public required float MoneyWeight { get; init; }
    public required float CarCostPerKm { get; init; }
    public required float CarOverheadSeconds { get; init; }
    public required float CarAccessRadius { get; init; }
    public required float TransitWaitShare { get; init; }
    public required float TransitRideFactor { get; init; }
    public required float TransitGiveUpSeconds { get; init; }
    public required int MaxCars { get; init; }
    public required float CarDepartGapSeconds { get; init; }
    public required float CarSpawnClearance { get; init; }
    public required int CarSpawnAttempts { get; init; }
    public required float CarArriveRange { get; init; }
    public required float CarTimeoutSeconds { get; init; }
    public required float CarStuckSeconds { get; init; }
    public required float CongestionSmoothing { get; init; }
    public required float MaxCongestion { get; init; }

    internal void Validate()
    {
        if (GroupSize < 1 || PeoplePerCar < 1 || MaxCars < 0) throw new JsonException("trips.groupSize, peoplePerCar and maxCars are out of range");
        if (WalkSpeed <= 0f || WalkDetour < 1f) throw new JsonException("trips.walkSpeed and walkDetour are out of range");
        if (CarStuckSeconds <= 0f) throw new JsonException("trips.carStuckSeconds must be positive");
        if (CongestionSmoothing < 0f || CongestionSmoothing > 1f || MaxCongestion < 1f) throw new JsonException("trips.congestionSmoothing and maxCongestion are out of range");
        for (int leg = 0; leg < 4; leg++)
        {
            var w = Windows.Of(leg);
            if (w.From < 0f || w.To < w.From || w.To > 24f) throw new JsonException($"trips.windows leg {leg} is not a stretch of the day");
        }
    }
}
