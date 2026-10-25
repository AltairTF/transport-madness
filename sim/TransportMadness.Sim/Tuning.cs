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
    public required AccessTuning Access { get; init; }
    public required TownsTuning Towns { get; init; }
    public required IndustriesTuning Industries { get; init; }
    public required GatewaysTuning Gateways { get; init; }
    public required CampaignTuning Campaign { get; init; }
    public required OrderTuning Orders { get; init; }
    public required PowerTuning Power { get; init; }
    public required WarTuning War { get; init; }
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
        tuning.War.Validate(tuning);
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
    public required float DepotHalfSize { get; init; }
    public required float DepotBesideRadius { get; init; }
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
    public required RoadTypeTuning Path { get; init; }
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

    public RoadTypeTuning Of(RoadType type) => type switch { RoadType.Avenue2x2 => Avenue, RoadType.Path => Path, _ => TwoLane };
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

/// <summary>Access points (C10): where an industry or a gateway meets the road network, and how far a road is drawn to it.</summary>
public sealed record AccessTuning
{
    public required float SnapRadius { get; init; }
    public required float EdgeMargin { get; init; }
    /// <summary>The ground under a depot or battery is levelled (M6g): it blends back to the old ground over this many metres.</summary>
    public required float PadShoulder { get; init; }
    /// <summary>A pad is refused when it would cut or fill more than this many metres at any point.</summary>
    public required float PadMaxChange { get; init; }
    /// <summary>The share of a road's price per cubic metre that levelling a pad costs: it is rough work, not a road bed.</summary>
    public required float PadCostShare { get; init; }
    /// <summary>How far a pad is raised or lowered by one step of the player's keys, in metres.</summary>
    public required float PadRaiseStep { get; init; }
}

public sealed record StationTuning
{
    public required long Price { get; init; }
    public required float UpkeepPerDay { get; init; }
    public required float CoverageRadius { get; init; }
    /// <summary>The walk along the streets at which a station's coverage of buildings outside its circle has fallen to nothing (M6h).</summary>
    public required float ReachRadius { get; init; }
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
    /// <summary>The year of the campaign from which the model can be bought.</summary>
    public required int FromYear { get; init; }
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
        foreach (var (name, c) in new[] { ("hamlet", Generation.Hamlet), ("village", Generation.Village), ("town", Generation.Town) })
            if (c.PeopleMin < 1 || c.PeopleMax < c.PeopleMin || c.PeopleMax > 1000 || c.MaxTier is < 0 or >= Tiers)
                throw new JsonException($"towns.generation.{name} has people or maxTier out of range");
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
    // The grid of the old towns: only the lot and growth code that slice 2 of docs/town-redo-plan.md removes, and test fixtures, still read these.
    public required float BlockLength { get; init; }
    public required float LotWidth { get; init; }
    public required float SetbackGap { get; init; }
    public required TownSizesTuning Sizes { get; init; }
    public required StreetsTuning Streets { get; init; }
    public required TownClassTuning Hamlet { get; init; }
    public required TownClassTuning Village { get; init; }
    public required TownClassTuning Town { get; init; }

    public TownClassTuning Of(TownSize size) => size switch { TownSize.Hamlet => Hamlet, TownSize.Village => Village, _ => Town };
}

/// <summary>One size class of town (docs/town-redo-plan.md): how many people it starts with and how far its houses reach.</summary>
public sealed record TownClassTuning
{
    public required int PeopleMin { get; init; }
    public required int PeopleMax { get; init; }
    /// <summary>How far from the middle the houses may stand.</summary>
    public required float Radius { get; init; }
    /// <summary>Length of each arm of the through road from the middle, before the country part.</summary>
    public required float ArmLength { get; init; }
    public required int LanesMin { get; init; }
    public required int LanesMax { get; init; }
    /// <summary>Chance of a second road across the middle.</summary>
    public required float CrossingChance { get; init; }
    /// <summary>Chance of a farm or factory compound apart from the houses.</summary>
    public required float CompoundChance { get; init; }
    /// <summary>The biggest size (tier index) a building may have.</summary>
    public required int MaxTier { get; init; }
}

/// <summary>How the streets and houses of a town are laid out (<see cref="VillageGenerator"/>).</summary>
public sealed record StreetsTuning
{
    /// <summary>The length of one piece of a street, and the most it may turn over one piece (radians).</summary>
    public required float PieceMin { get; init; }
    public required float PieceMax { get; init; }
    public required float MaxTurn { get; init; }
    /// <summary>Rise over run a street piece may climb; the ground under a house may be this steep, too.</summary>
    public required float MaxSlope { get; init; }
    public required float LaneMin { get; init; }
    public required float LaneMax { get; init; }
    /// <summary>A lane at most this long ends in a loop back to the through road when one is near enough.</summary>
    public required float LoopChance { get; init; }
    /// <summary>The through road goes on this far beyond the houses, as a country road.</summary>
    public required float CountryReach { get; init; }
    /// <summary>Metres between the middles of two houses along a street (at least and at most).</summary>
    public required float SpacingMin { get; init; }
    public required float SpacingMax { get; init; }
    /// <summary>Clear ground between a road's edge and a house's footprint (at least and at most): the yard.</summary>
    public required float YardMin { get; init; }
    public required float YardMax { get; init; }
    public required float Gap { get; init; }
    public required float YawJitter { get; init; }
    public required float PadShoulder { get; init; }
    public required float CompoundRadius { get; init; }
}

/// <summary>Where towns go and how many of each class (docs/town-redo-plan.md).</summary>
public sealed record TownSizesTuning
{
    public required int Candidates { get; init; }
    public required float SiteRadius { get; init; }
    public required float WaterShare { get; init; }
    public required float WaterBonus { get; init; }
    public required float WaterBonusReach { get; init; }
    /// <summary>A map with fewer towns than this has no big Town; its best site gets a Village.</summary>
    public required int TownMinTowns { get; init; }
    /// <summary>Of the towns after the first, this share are Villages and the rest Hamlets.</summary>
    public required float VillageShare { get; init; }
    public required float SpacingGap { get; init; }
    public required float SpacingShare { get; init; }
}

public sealed record RingTuning
{
    public required float Until { get; init; }
    public required float Fill { get; init; }
    public required IReadOnlyList<float> Mix { get; init; }
    public required IReadOnlyList<float> Tiers { get; init; }
    /// <summary>Metres of street a lot takes along it in this zone, and the biggest size (tier index) a building may grow to there.</summary>
    public required float Frontage { get; init; }
    public required int MaxTier { get; init; }
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
    /// <summary>The month (0 is the first January) from which the government sends it.</summary>
    public required int FromMonth { get; init; }
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
    /// <summary>Jobs it has (C7): it makes its output times its staffing. 0 for an industry that needs nobody.</summary>
    public required int Jobs { get; init; }
    /// <summary>Megawatts this industry uses whether it works or not (C6).</summary>
    public required float Demand { get; init; }
    /// <summary>Megawatts a power plant adds while it burns its coal; 0 for everything else. A plant makes no cargo (its <see cref="Output"/> is empty).</summary>
    public required float Supply { get; init; }
    public required IReadOnlyList<float> Color { get; init; }
}

/// <summary>Electricity (C6): the grid the map started with, and power bought from outside.</summary>
public sealed record PowerTuning
{
    /// <summary>The free grid supplies the demand of the industries of the start times this.</summary>
    public required float FreeGridMargin { get; init; }
    public required long PricePerMegawattMonth { get; init; }
    public required int MaxPurchase { get; init; }
    public required int PurchaseStep { get; init; }
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
