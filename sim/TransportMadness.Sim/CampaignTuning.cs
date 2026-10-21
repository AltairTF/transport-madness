using System.Text.Json;

namespace TransportMadness.Sim;

/// <summary>The campaign: its length, how the standing moves, the phases of the war with their allocations, and the final grade.</summary>
public sealed record CampaignTuning
{
    public required string Country { get; init; }
    public required string Region { get; init; }
    public required int Months { get; init; }
    public required float StartStanding { get; init; }
    public required float DismissBelow { get; init; }
    public required int DismissMonths { get; init; }
    public required float AllocationBase { get; init; }
    public required float AllocationPerStanding { get; init; }
    public required float ExportScale { get; init; }
    public required float MaxExportSwing { get; init; }
    public required float DeficitPenalty { get; init; }
    public required float FinePenaltyUnit { get; init; }
    public required float MaxFinePenalty { get; init; }
    public required IReadOnlyList<PhaseTuning> Phases { get; init; }
    public required GradeTuning Grade { get; init; }

    /// <summary>The allocation factor at a standing.</summary>
    public float AllocationFactor(float standing) => AllocationBase + AllocationPerStanding * standing;

    /// <summary>The phase a month (index from January of the start year) belongs to: the last one that has started.</summary>
    public PhaseTuning PhaseOf(long month)
    {
        var phase = Phases[0];
        foreach (var p in Phases)
            if (p.FromMonth <= month) phase = p;
        return phase;
    }

    internal void Validate()
    {
        if (Months <= 0) throw new JsonException("campaign.months must be positive");
        if (Phases.Count == 0 || Phases[0].FromMonth != 0) throw new JsonException("campaign.phases must start at month 0");
        for (int i = 1; i < Phases.Count; i++)
            if (Phases[i].FromMonth <= Phases[i - 1].FromMonth) throw new JsonException("campaign.phases must be in order of start");
        if (Grade.Thresholds.Count + 1 != Grade.Names.Count) throw new JsonException("campaign.grade needs one name more than it has thresholds");
        for (int i = 1; i < Grade.Thresholds.Count; i++)
            if (Grade.Thresholds[i] >= Grade.Thresholds[i - 1]) throw new JsonException("campaign.grade.thresholds must fall");
    }
}

public sealed record PhaseTuning
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int FromMonth { get; init; }
    public required long Allocation { get; init; }
    public required float ExpectedValue { get; init; }
}

public sealed record GradeTuning
{
    public required float StandingWeight { get; init; }
    public required float ProductionWeight { get; init; }
    public required float SatisfactionWeight { get; init; }
    public required float ProductionTarget { get; init; }
    public required IReadOnlyList<float> Thresholds { get; init; }
    public required IReadOnlyList<string> Names { get; init; }
}
