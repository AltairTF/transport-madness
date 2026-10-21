using System.Text.Json;

namespace TransportMadness.Sim;

/// <summary>The war (C9): rationing of what the Region imports, the call-up, the army's requisitions and the raids.</summary>
public sealed record WarTuning
{
    public required RationingTuning Rationing { get; init; }

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
