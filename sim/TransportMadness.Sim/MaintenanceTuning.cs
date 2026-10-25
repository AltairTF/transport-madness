using System.Text.Json;

namespace TransportMadness.Sim;

/// <summary>What a repair does: it adds <see cref="Gain"/> points (never above <see cref="Ceiling"/>), takes a bay for <see cref="Seconds"/> and costs a share of the price of what is repaired.</summary>
public sealed record RepairTuning
{
    public required float Gain { get; init; }
    public required float Ceiling { get; init; }
    public required float Seconds { get; init; }
    public required float PriceShare { get; init; }
}

/// <summary>How many vehicles a depot can repair at once, by its level.</summary>
public sealed record BaysPerLevel
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

/// <summary>The company-wide maintenance policy a game starts with (the player changes it with a command).</summary>
public sealed record PolicyTuning
{
    /// <summary>Days since the last repair after which a vehicle is due, 0 for never.</summary>
    public required int EveryDays { get; init; }
    /// <summary>Condition below which a vehicle is due, 0 for never.</summary>
    public required int BelowCondition { get; init; }
    /// <summary>"patch", "service" or "overhaul".</summary>
    public required string Repair { get; init; }
}

/// <summary>How much the option for random events scales them: breakdowns, signal failures, and later the campaign's.</summary>
public sealed record FrequencyScales
{
    public required float Off { get; init; }
    public required float Low { get; init; }
    public required float Normal { get; init; }
    public required float High { get; init; }

    public float For(IncidentFrequency frequency) => frequency switch
    {
        IncidentFrequency.Off => Off,
        IncidentFrequency.Low => Low,
        IncidentFrequency.Normal => Normal,
        _ => High,
    };
}

/// <summary>Rail signals wear with the trains that pass and sometimes fail; a crew repairs them.</summary>
public sealed record SignalTuning
{
    /// <summary>Points of Condition lost each time a train is let through.</summary>
    public required float WearPerPassage { get; init; }
    /// <summary>The metres of driving one passage counts for in the breakdown risk (the same curve as for vehicles).</summary>
    public required float PassageMetres { get; init; }
    /// <summary>The Condition a signal has after a crew has repaired it after a failure.</summary>
    public required float RepairedCondition { get; init; }
    /// <summary>What a crew's repair of a failed signal costs.</summary>
    public required long RepairPrice { get; init; }
    /// <summary>A failure that lasts longer than this was left unrepaired for long (no depot near): negligence, fined.</summary>
    public required float FineAfterSeconds { get; init; }
}

/// <summary>Wear of road sections by the traffic that passes, and resurfacing.</summary>
public sealed record RoadWearTuning
{
    /// <summary>Vehicles driving the whole length of a section (ambient ones count too) for one point of Condition to be lost.</summary>
    public required float PassesPerPoint { get; init; }
    /// <summary>The same for a dirt road, which wears faster.</summary>
    public required float DirtPassesPerPoint { get; init; }
    /// <summary>At or above this Condition a section has no effect on speed or risk.</summary>
    public required float NoEffectAbove { get; init; }
    /// <summary>The share of the speed limit drivers keep on a section of Condition 0 (it falls evenly from <see cref="NoEffectAbove"/>).</summary>
    public required float WorstSpeedShare { get; init; }
    /// <summary>How many times the breakdown risk of a company vehicle is on a section of Condition 0.</summary>
    public required float WorstRiskFactor { get; init; }
    public required float ResurfacePricePerMetre { get; init; }
    /// <summary>Metres of road a crew resurfaces each second; the road is closed for that long.</summary>
    public required float ResurfaceMetresPerSecond { get; init; }
}

/// <summary>What an incident costs the company: compensation to the injured, fines for negligence, the goodwill of the town it happens in.</summary>
public sealed record ConsequenceTuning
{
    /// <summary>Share of the passengers aboard a company vehicle that are injured when it crashes.</summary>
    public required float InjuredShare { get; init; }
    public required long CompensationPerInjured { get; init; }
    /// <summary>The fine for a breakdown of a vehicle below the critical Condition, or a signal failure left unrepaired for long.</summary>
    public required long NegligenceFine { get; init; }
    /// <summary>Satisfaction a town loses when an incident of a company vehicle happens inside it.</summary>
    public required float TownIncidentPenalty { get; init; }
    /// <summary>Satisfaction a town loses when such an incident has blocked a road in it for <see cref="BlockedSeconds"/>.</summary>
    public required float TownBlockedPenalty { get; init; }
    public required float BlockedSeconds { get; init; }
    /// <summary>How far from the middle of a town, beyond its blocks, an incident still counts as inside it.</summary>
    public required float TownMargin { get; init; }
}

/// <summary>Condition, wear, breakdowns and repairs of the company's fleet (ADR 0005, <c>docs/m5-plan.md</c>).</summary>
public sealed record MaintenanceTuning
{
    /// <summary>Metres driven for one point of Condition to be lost.</summary>
    public required float MetresPerPoint { get; init; }
    /// <summary>Extra wear at a full load, as a share (a vehicle half full wears half of it more).</summary>
    public required float LoadedWear { get; init; }
    /// <summary>Wear of a vehicle that was patched, until its next Service or Overhaul, as a multiple.</summary>
    public required float PatchedWearFactor { get; init; }
    /// <summary>No risk of a breakdown at or above this Condition.</summary>
    public required float NoRiskAbove { get; init; }
    /// <summary>The risk reaches <see cref="HazardPerKmAtFloor"/> here and rises faster below it.</summary>
    public required float HighRiskBelow { get; init; }
    /// <summary>Breakdowns per kilometre at <see cref="HighRiskBelow"/>.</summary>
    public required float HazardPerKmAtFloor { get; init; }
    /// <summary>Breakdowns per kilometre at Condition 0.</summary>
    public required float HazardPerKmAtZero { get; init; }
    /// <summary>Below this Condition the brakes fade, down to <see cref="WorstBrakeShare"/> of the braking at 0.</summary>
    public required float BrakeFadeBelow { get; init; }
    public required float WorstBrakeShare { get; init; }
    /// <summary>The Condition a vehicle has after it broke down.</summary>
    public required float WreckCondition { get; init; }
    /// <summary>A breakdown below this Condition counts as negligence (a fine, in 5c).</summary>
    public required float CriticalCondition { get; init; }
    /// <summary>Seconds a broken-down train stands before the crew has cleared it.</summary>
    public required float TrainBreakdownSeconds { get; init; }
    /// <summary>What a repair costs more for a model that has been on offer for years (C8).</summary>
    public required OldVehicleTuning OldVehicle { get; init; }
    public required RepairTuning Patch { get; init; }
    public required RepairTuning Service { get; init; }
    public required RepairTuning Overhaul { get; init; }
    public required BaysPerLevel Bays { get; init; }
    public required PolicyTuning DefaultPolicy { get; init; }
    public required FrequencyScales Frequency { get; init; }
    public required SignalTuning Signals { get; init; }
    public required RoadWearTuning Roads { get; init; }
    public required ConsequenceTuning Consequences { get; init; }

    public RepairTuning Of(RepairKind kind) => kind switch
    {
        RepairKind.Patch => Patch,
        RepairKind.Service => Service,
        _ => Overhaul,
    };

    internal void Validate()
    {
        if (MetresPerPoint <= 0f) throw new JsonException("maintenance.metresPerPoint must be positive");
        if (HighRiskBelow <= 0f || NoRiskAbove <= HighRiskBelow || NoRiskAbove > 100f) throw new JsonException("maintenance.noRiskAbove and highRiskBelow are out of order");
        if (BrakeFadeBelow <= 0f || BrakeFadeBelow > 100f || WorstBrakeShare <= 0f || WorstBrakeShare > 1f) throw new JsonException("maintenance brake fade is out of range");
        if (!Enum.TryParse<RepairKind>(DefaultPolicy.Repair, ignoreCase: true, out _)) throw new JsonException("maintenance.defaultPolicy.repair must be patch, service or overhaul");
        foreach (var repair in new[] { Patch, Service, Overhaul })
            if (repair.Seconds <= 0f || repair.Gain <= 0f || repair.Ceiling <= 0f || repair.Ceiling > 100f) throw new JsonException("maintenance repair is out of range");
        if (Roads.PassesPerPoint <= 0f || Roads.DirtPassesPerPoint <= 0f || Roads.NoEffectAbove <= 0f || Roads.NoEffectAbove > 100f || Roads.WorstSpeedShare <= 0f || Roads.WorstSpeedShare > 1f || Roads.ResurfaceMetresPerSecond <= 0f)
            throw new JsonException("maintenance.roads is out of range");
        if (Bays.Simple < 1 || Bays.Workshop < 1 || Bays.Complex < 1) throw new JsonException("maintenance.bays must be at least 1");
    }
}

/// <summary>A model is old after <see cref="AfterYears"/> years since its year; each further year adds <see cref="ExtraPerYear"/> to the price of a repair, up to <see cref="MaxExtra"/>.</summary>
public sealed record OldVehicleTuning
{
    public required int AfterYears { get; init; }
    public required float ExtraPerYear { get; init; }
    public required float MaxExtra { get; init; }

    /// <summary>The multiple of the repair price for a model of <paramref name="fromYear"/> in <paramref name="year"/>.</summary>
    public float Factor(int fromYear, int year) => 1f + MathF.Min(MaxExtra, ExtraPerYear * MathF.Max(0, year - fromYear - AfterYears));
}
