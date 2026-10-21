using System.Text.Json;

namespace TransportMadness.Sim;

/// <summary>The war (C9): rationing of what the Region imports, the call-up, the army's requisitions and the raids.</summary>
public sealed record WarTuning
{
    public required RationingTuning Rationing { get; init; }
    public required ConscriptionTuning Conscription { get; init; }

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
    public required IReadOnlyList<CallUpWave> Waves { get; init; }
}

/// <summary>A wave of the call-up: at the start of <see cref="Month"/> (0 is the first January) <see cref="Share"/> of the people go.</summary>
public sealed record CallUpWave
{
    public required int Month { get; init; }
    public required float Share { get; init; }
}
