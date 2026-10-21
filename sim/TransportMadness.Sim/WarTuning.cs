using System.Text.Json;

namespace TransportMadness.Sim;

/// <summary>The war (C9): rationing of what the Region imports, the call-up, the army's requisitions and the raids.</summary>
public sealed record WarTuning
{
    public required RationingTuning Rationing { get; init; }
    public required ConscriptionTuning Conscription { get; init; }
    public required RequisitionTuning Requisition { get; init; }
    public required BombingTuning Bombing { get; init; }
    public required AntiAircraftTuning AntiAircraft { get; init; }
    public required CiviliansTuning Civilians { get; init; }

    internal void Validate(Tuning tuning)
    {
        var cargo = tuning.Cargo.Select(c => c.Id).ToHashSet();
        var phases = tuning.Campaign.Phases.Select(p => p.Id).ToHashSet();
        foreach (var c in Rationing.Cargo)
            if (!cargo.Contains(c)) throw new JsonException($"war.rationing: unknown cargo '{c}'");
        foreach (var p in Rationing.Phases)
        {
            if (!phases.Contains(p.Phase)) throw new JsonException($"war.rationing: unknown phase '{p.Phase}'");
            if (p.PriceFactor <= 0f || p.UnitsFactor <= 0f) throw new JsonException($"war.rationing: {p.Phase} needs positive factors");
        }
        var call = Conscription;
        if (call.OutputLoss < 0f || call.OutputLoss > 1f || call.RecoveryPerMonth < 0f) throw new JsonException("war.conscription: outputLoss must be 0 to 1 and recoveryPerMonth not negative");
        for (int i = 0; i < call.Waves.Count; i++)
        {
            if (call.Waves[i].Share <= 0f || call.Waves[i].Share >= 1f) throw new JsonException("war.conscription: a wave's share must be between 0 and 1");
            if (i > 0 && call.Waves[i].Month <= call.Waves[i - 1].Month) throw new JsonException("war.conscription: the waves must be in order of month");
        }
        var r = Requisition;
        if (r.PriceShare <= 0f || r.NoticeMonths < 0) throw new JsonException("war.requisition: priceShare must be positive and noticeMonths not negative");
        for (int i = 0; i < r.Waves.Count; i++)
        {
            if (r.Waves[i].Share <= 0f || r.Waves[i].Share > 1f) throw new JsonException("war.requisition: a wave's share must be above 0 and at most 1");
            if (i > 0 && r.Waves[i].Month <= r.Waves[i - 1].Month) throw new JsonException("war.requisition: the waves must be in order of month");
        }
        var b = Bombing;
        foreach (var g in b.Gaps)
        {
            if (!phases.Contains(g.Phase)) throw new JsonException($"war.bombing: unknown phase '{g.Phase}'");
            if (g.MinDays <= 0 || g.MaxDays < g.MinDays) throw new JsonException($"war.bombing: {g.Phase} needs 0 < minDays <= maxDays");
        }
        if (b.Gaps.Count == 0 || b.Aircraft.Count == 0) throw new JsonException("war.bombing needs gaps and aircraft");
        foreach (var a in b.Aircraft)
        {
            if (!phases.Contains(a.Phase)) throw new JsonException($"war.bombing: unknown phase '{a.Phase}'");
            if (a.Min < 1 || a.Max < a.Min || a.Max > 12) throw new JsonException($"war.bombing: {a.Phase} needs 1 <= min <= max <= 12 aircraft");
        }
        var types = tuning.Industries.Types.Select(t => t.Id).ToHashSet();
        foreach (var v in b.IndustryValues)
            if (!types.Contains(v.Type) || v.Value < 0) throw new JsonException($"war.bombing: bad industry value '{v.Type}'");
        if (b.DefaultIndustryValue < 0 || b.GatewayValue < 0 || b.TownValue < 0 || b.StationValue < 0 || b.DepotValue < 0 || b.SignalValue < 0)
            throw new JsonException("war.bombing: values must not be negative");
        if (b.GatewayValue + b.TownValue + b.StationValue + b.DepotValue + b.SignalValue + b.DefaultIndustryValue + b.IndustryValues.Sum(v => v.Value) <= 0)
            throw new JsonException("war.bombing: something must be worth bombing");
        if (b.Speed <= 0f || b.Altitude <= 0f || b.BlastRadius <= 0f || b.RevealSeconds < 0f || b.EntryMargin < 0f || b.EntryArc < 0f || b.EntryArc > 1.5f)
            throw new JsonException("war.bombing: speed, altitude and blastRadius must be positive, the others not negative, entryArc at most 1.5");
        if (b.BombsPerAircraft < 1 || b.BombsPerAircraft > 40 || b.BombSpacing <= 0f || b.TrailSpacing < 0f || b.WingSpacing < 0f || b.AimError < 0f || b.Scatter < 0f || b.ScatterAlong < 0f)
            throw new JsonException("war.bombing: a stick needs 1 to 40 bombs and a positive spacing, the spreads must not be negative");
        if (b.CasualtyShare < 0f || b.CasualtyShare > 1f || b.InputLoss < 0f || b.InputLoss > 1f || b.BuildingChance < 0f || b.BuildingChance > 1f || b.CraterChance < 0f || b.CraterChance > 1f
            || b.DamagedFactor < 0f || b.DamagedFactor > 1f || b.IdleFactor < 0f || b.IdleFactor > 1f)
            throw new JsonException("war.bombing: shares and chances must be 0 to 1");
        if (b.IndustryDownDays < 0 || b.IndustryMaxDownDays < b.IndustryDownDays || b.GatewayDownDays < 0 || b.StationDownDays < 0 || b.DepotDownDays < 0 || b.CarPeople < 0 || b.TownLossPerBuilding < 0f || b.CraterRadius < 0f)
            throw new JsonException("war.bombing: days, people and radii must not be negative and the most an industry is down must be at least one bomb's worth");
        var aa = AntiAircraft;
        if (!types.Contains(aa.Type)) throw new JsonException($"war.antiAircraft: unknown industry type '{aa.Type}'");
        if (tuning.Industries.Types.First(t => t.Id == aa.Type).Inputs.Count == 0) throw new JsonException("war.antiAircraft: the battery type needs an input, the ammunition it fires");
        if (aa.FromMonth < 0 || aa.ExpectMonth < 0 || aa.Price <= 0 || aa.UpkeepPerMonth < 0)
            throw new JsonException("war.antiAircraft: the months must not be negative, the price positive");
        if (aa.Clearance < 0f || aa.BuildingClearance < 0f || aa.MaxHeightRange < 0f || aa.Range <= 0f || aa.AmmoPerSalvo <= 0f)
            throw new JsonException("war.antiAircraft: the clearances must not be negative, range and ammoPerSalvo positive");
        if (aa.HitChance < 0f || aa.HitChance > 1f || aa.FallCarry < 0f || aa.FallCraterChance < 0f || aa.FallCraterChance > 1f || aa.StrongCover < 0f || aa.StrongCover > 1f)
            throw new JsonException("war.antiAircraft: chances and shares must be 0 to 1");
        if (aa.ScatterGrowth < 0f || aa.ScatterCap < 1f || aa.EarlyRelease < 0f || aa.CoverSaturation <= 0f)
            throw new JsonException("war.antiAircraft: scatterGrowth and earlyRelease must not be negative, scatterCap at least 1, coverSaturation positive");
        if (Civilians.ReactionSeconds < 0f || Civilians.RecheckSeconds <= 0f)
            throw new JsonException("war.civilians: reactionSeconds must not be negative and recheckSeconds positive");
    }
}

/// <summary>Civilians in a raid (C14): how people on foot and at stations take cover when the siren sounds.</summary>
public sealed record CiviliansTuning
{
    /// <summary>Seconds after the siren before people start to run: they have this much less than the flight's lead time to reach a building.</summary>
    public required float ReactionSeconds { get; init; }
    /// <summary>A group that is to leave on foot or for a station while the siren is on stays in and looks again this many seconds later.</summary>
    public required float RecheckSeconds { get; init; }
}

/// <summary>The anti-aircraft batteries (C13): what one costs and where it may stand, how it fires, and what its fire does to the raids.</summary>
public sealed record AntiAircraftTuning
{
    /// <summary>The id of the industry type that is a battery.</summary>
    public required string Type { get; init; }
    /// <summary>The month (0 is the first January) from which a battery can be built, and the month the player is told that raids are expected.</summary>
    public required int FromMonth { get; init; }
    public required int ExpectMonth { get; init; }
    public required long Price { get; init; }
    public required long UpkeepPerMonth { get; init; }
    /// <summary>Metres a battery's middle must be from the middle of every other industry, gateway and battery, and from the middle of any building.</summary>
    public required float Clearance { get; init; }
    public required float BuildingClearance { get; init; }
    /// <summary>Highest minus lowest ground under it.</summary>
    public required float MaxHeightRange { get; init; }
    /// <summary>Metres from the battery within which it fires at an aircraft.</summary>
    public required float Range { get; init; }
    /// <summary>The chance per second that a battery with its jobs filled brings down an aircraft in range, and the ammunition a second of firing costs.</summary>
    public required float HitChance { get; init; }
    public required float AmmoPerSalvo { get; init; }
    /// <summary>A downed aircraft goes on this share of the way it would have flown while falling, and leaves a crater with this chance.</summary>
    public required float FallCarry { get; init; }
    public required float FallCraterChance { get; init; }
    /// <summary>Under fire a flight's bombs scatter 1 + <see cref="ScatterGrowth"/> times the fire pressure as much (at most <see cref="ScatterCap"/> times) and are let go <see cref="EarlyRelease"/> metres early at a pressure of 1.</summary>
    public required float ScatterGrowth { get; init; }
    public required float ScatterCap { get; init; }
    public required float EarlyRelease { get; init; }
    /// <summary>The cover of a place is the staffing of the working batteries that reach it over this, at most 1; the enemy wants a place with full cover <see cref="StrongCover"/> times as much.</summary>
    public required float CoverSaturation { get; init; }
    public required float StrongCover { get; init; }
}

/// <summary>What the Region's imports of some cargo cost and how big a shipment is, in each phase of the war.</summary>
public sealed record RationingTuning
{
    public required IReadOnlyList<string> Cargo { get; init; }
    public required IReadOnlyList<RationingPhaseTuning> Phases { get; init; }

    public RationingPhaseTuning Of(string phase) => Phases.FirstOrDefault(p => p.Phase == phase) ?? new RationingPhaseTuning { Phase = phase, PriceFactor = 1f, UnitsFactor = 1f };
}

public sealed record RationingPhaseTuning
{
    public required string Phase { get; init; }
    public required float PriceFactor { get; init; }
    public required float UnitsFactor { get; init; }
}

/// <summary>The call-up: waves in which a share of the people leave, and what that does to the output.</summary>
public sealed record ConscriptionTuning
{
    public required float OutputLoss { get; init; }
    public required float RecoveryPerMonth { get; init; }
    public required float SatisfactionLoss { get; init; }
    public required IReadOnlyList<WarWave> Waves { get; init; }
}

/// <summary>A wave of the war (the call-up, a requisition): at the start of <see cref="Month"/> (0 is the first January) <see cref="Share"/> of the people or the fleet go.</summary>
public sealed record WarWave
{
    public required int Month { get; init; }
    public required float Share { get; init; }
}

/// <summary>The army's requisitions: at fixed months it buys a share of the company's fleet at a table price.</summary>
public sealed record RequisitionTuning
{
    /// <summary>The player is told this many months ahead.</summary>
    public required int NoticeMonths { get; init; }
    /// <summary>The army pays this share of the price of a vehicle new, whatever its Condition.</summary>
    public required float PriceShare { get; init; }
    /// <summary>Standing the army's good opinion adds when a requisition has taken something.</summary>
    public required float Standing { get; init; }
    public required IReadOnlyList<WarWave> Waves { get; init; }
}

/// <summary>The raids (C12): when a flight comes, how it flies, what it aims at, and what each bomb does.</summary>
public sealed record BombingTuning
{
    public required int FromMonth { get; init; }
    public required IReadOnlyList<RaidGapTuning> Gaps { get; init; }
    /// <summary>How many aircraft a flight has in each phase (the last entry holds for any phase after it).</summary>
    public required IReadOnlyList<RaidAircraftTuning> Aircraft { get; init; }
    /// <summary>Metres per second an aircraft flies at, and the height it flies and drops from.</summary>
    public required float Speed { get; init; }
    public required float Altitude { get; init; }
    /// <summary>Aircraft come in this far outside the edge of the map, and leave this far outside it.</summary>
    public required float EntryMargin { get; init; }
    /// <summary>The entry point lies on the side of the map the game drew, spread by up to this angle (radians) either way.</summary>
    public required float EntryArc { get; init; }
    /// <summary>The target is told to the player this many seconds before the aircraft arrive.</summary>
    public required float RevealSeconds { get; init; }
    /// <summary>The aircraft of a flight fly in a formation: each trails the one before by <see cref="TrailSpacing"/> and the pairs are <see cref="WingSpacing"/> apart across the path.</summary>
    public required float TrailSpacing { get; init; }
    public required float WingSpacing { get; init; }
    /// <summary>Every aircraft drops a stick of this many bombs, this far apart along its path, centred on the target.</summary>
    public required int BombsPerAircraft { get; init; }
    public required float BombSpacing { get; init; }
    /// <summary>How far an aircraft's whole stick may be off the target across the path, and how far a single bomb may be off its place across and along it (metres, drawn uniformly either way).</summary>
    public required float AimError { get; init; }
    public required float Scatter { get; init; }
    public required float ScatterAlong { get; init; }
    /// <summary>What a bomb touches: everything whose middle (or, for a big structure, edge) is within this many metres of where it lands.</summary>
    public required float BlastRadius { get; init; }
    /// <summary>The chance a building at the very centre of a blast is destroyed; it falls to nothing at the edge of the radius.</summary>
    public required float BuildingChance { get; init; }
    /// <summary>Share of the people in a destroyed building who are counted as casualties, and the satisfaction a town loses for each building it loses.</summary>
    public required float CasualtyShare { get; init; }
    public required float TownLossPerBuilding { get; init; }
    /// <summary>People in an ambient car that is wrecked (the company's vehicles count the passengers they carry).</summary>
    public required int CarPeople { get; init; }
    /// <summary>Every bomb that hits an industry adds this many days to the time it stands idle (at most <see cref="IndustryMaxDownDays"/> from now) and takes <see cref="InputLoss"/> of the stock it holds.</summary>
    public required int IndustryDownDays { get; init; }
    public required int IndustryMaxDownDays { get; init; }
    public required float InputLoss { get; init; }
    /// <summary>A gateway, a station or a depot that a bomb hits is shut for this many days (not added to by the next bomb).</summary>
    public required int GatewayDownDays { get; init; }
    public required int StationDownDays { get; init; }
    public required int DepotDownDays { get; init; }
    /// <summary>The chance a bomb leaves a crater in the nearest road within <see cref="CraterRadius"/> of where it lands.</summary>
    public required float CraterChance { get; init; }
    public required float CraterRadius { get; init; }
    /// <summary>How much the enemy wants each target: the value of an industry by type, and of the other kinds of target.</summary>
    public required IReadOnlyList<RaidIndustryValue> IndustryValues { get; init; }
    public required int DefaultIndustryValue { get; init; }
    public required int GatewayValue { get; init; }
    public required int TownValue { get; init; }
    public required int StationValue { get; init; }
    public required int DepotValue { get; init; }
    public required int SignalValue { get; init; }
    /// <summary>A target that is already down (an industry stopped, a gateway, station or depot shut) weighs this share of its value; an industry that lacks an input weighs <see cref="IdleFactor"/>.</summary>
    public required float DamagedFactor { get; init; }
    public required float IdleFactor { get; init; }

    /// <summary>Days between flights in a phase; the last entry holds for any phase after it.</summary>
    public RaidGapTuning GapOf(string phase) => Gaps.FirstOrDefault(g => g.Phase == phase) ?? Gaps[^1];

    /// <summary>How many aircraft a flight has in a phase.</summary>
    public RaidAircraftTuning AircraftOf(string phase) => Aircraft.FirstOrDefault(g => g.Phase == phase) ?? Aircraft[^1];

    /// <summary>What an industry of a type is worth to the enemy.</summary>
    public int ValueOf(string industryType) => IndustryValues.FirstOrDefault(v => v.Type == industryType)?.Value ?? DefaultIndustryValue;
}

public sealed record RaidGapTuning
{
    public required string Phase { get; init; }
    public required int MinDays { get; init; }
    public required int MaxDays { get; init; }
}

/// <summary>A flight has between <see cref="Min"/> and <see cref="Max"/> aircraft in a phase.</summary>
public sealed record RaidAircraftTuning
{
    public required string Phase { get; init; }
    public required int Min { get; init; }
    public required int Max { get; init; }
}

public sealed record RaidIndustryValue
{
    public required string Type { get; init; }
    public required int Value { get; init; }
}
