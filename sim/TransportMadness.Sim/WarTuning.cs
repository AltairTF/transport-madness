using System.Text.Json;

namespace TransportMadness.Sim;

/// <summary>The war (C9): rationing of what the Region imports, the call-up, the army's requisitions and the raids.</summary>
public sealed record WarTuning
{
    public required RationingTuning Rationing { get; init; }
    public required ConscriptionTuning Conscription { get; init; }
    public required RequisitionTuning Requisition { get; init; }
    public required BombingTuning Bombing { get; init; }

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
        var phasesSet = tuning.Campaign.Phases.Select(p => p.Id).ToHashSet();
        foreach (var g in b.Gaps)
        {
            if (!phasesSet.Contains(g.Phase)) throw new JsonException($"war.bombing: unknown phase '{g.Phase}'");
            if (g.MinDays <= 0 || g.MaxDays < g.MinDays) throw new JsonException($"war.bombing: {g.Phase} needs 0 < minDays <= maxDays");
        }
        if (b.Gaps.Count == 0 || b.Targets.Count == 0) throw new JsonException("war.bombing needs gaps and targets");
        foreach (var t in b.Targets)
            if (t.Kind is not ("industry" or "gateway" or "rail" or "town") || t.Weight <= 0) throw new JsonException($"war.bombing: bad target '{t.Kind}'");
        if (b.CasualtyShare < 0f || b.CasualtyShare > 1f || b.InputLoss < 0f || b.InputLoss > 1f) throw new JsonException("war.bombing: shares must be 0 to 1");
    }
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

/// <summary>The raids: when they come, what they hit and what they do.</summary>
public sealed record BombingTuning
{
    public required int FromMonth { get; init; }
    public required int IndustryDownDays { get; init; }
    public required int GatewayDownDays { get; init; }
    public required float InputLoss { get; init; }
    public required int Craters { get; init; }
    public required float CraterRadius { get; init; }
    public required int BuildingsHit { get; init; }
    public required float CasualtyShare { get; init; }
    public required float TownLoss { get; init; }
    public required IReadOnlyList<RaidGapTuning> Gaps { get; init; }
    public required IReadOnlyList<RaidTargetTuning> Targets { get; init; }

    /// <summary>Days between raids in a phase; the last entry holds for any phase after it.</summary>
    public RaidGapTuning GapOf(string phase) => Gaps.FirstOrDefault(g => g.Phase == phase) ?? Gaps[^1];
}

public sealed record RaidGapTuning
{
    public required string Phase { get; init; }
    public required int MinDays { get; init; }
    public required int MaxDays { get; init; }
}

/// <summary>A kind of target ("industry", "gateway", "rail" or "town") and how likely it is to be picked.</summary>
public sealed record RaidTargetTuning
{
    public required string Kind { get; init; }
    public required int Weight { get; init; }
}
