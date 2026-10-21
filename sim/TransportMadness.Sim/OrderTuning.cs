using System.Text.Json;

namespace TransportMadness.Sim;

/// <summary>The government's orders (C5): how many can be open, how often they are offered, what failing costs and the templates they are drawn from.</summary>
public sealed record OrderTuning
{
    public required int MaxActive { get; init; }
    public required int MaxOpen { get; init; }
    /// <summary>Days an unaccepted offer stays open.</summary>
    public required int OfferExpiryDays { get; init; }
    /// <summary>A failed order costs this share of its reward as a fine.</summary>
    public required float FailedFineShare { get; init; }
    /// <summary>At the deadline, an order with at least this share delivered counts as late (paid in proportion, no penalty); under it, it has failed.</summary>
    public required float LateShare { get; init; }
    /// <summary>Satisfaction a town gains when a civil need is met, and loses when it is failed.</summary>
    public required float TownGain { get; init; }
    public required float TownLoss { get; init; }
    /// <summary>Failing costs this multiple of the standing that meeting the order would have given.</summary>
    public required float StandingLossFactor { get; init; }
    /// <summary>Offers drawn again when they repeat the one before (same template, cargo and place).</summary>
    public required int RepeatRedraws { get; init; }
    public required TutorialOrderTuning Tutorial { get; init; }
    public required IReadOnlyList<OfferGapTuning> Gaps { get; init; }
    public required IReadOnlyList<OrderTemplate> Templates { get; init; }
    public required StateWorkSiteTuning Sites { get; init; }

    /// <summary>Days between offers in a phase of the war.</summary>
    public OfferGapTuning GapOf(string phase) => Gaps.FirstOrDefault(g => g.Phase == phase) ?? Gaps[^1];

    internal void Validate(Tuning tuning)
    {
        if (MaxActive <= 0 || MaxOpen <= 0) throw new JsonException("orders.maxActive and maxOpen must be positive");
        foreach (var g in Gaps)
            if (g.MinDays <= 0 || g.MaxDays < g.MinDays) throw new JsonException($"orders.gaps: {g.Phase} needs 0 < minDays <= maxDays");
        if (Gaps.Count == 0) throw new JsonException("orders.gaps is empty");
        var cargo = tuning.Cargo.Select(c => c.Id).ToHashSet();
        var industries = tuning.Industries.Types.Select(t => t.Id).ToHashSet();
        var phases = tuning.Campaign.Phases.Select(p => p.Id).ToHashSet();
        foreach (var c in Tutorial.Cargo)
            if (!cargo.Contains(c)) throw new JsonException($"orders.tutorial: unknown cargo '{c}'");
        foreach (var t in Templates)
        {
            if (!cargo.Contains(t.Cargo)) throw new JsonException($"orders.templates: {t.Id} has unknown cargo '{t.Cargo}'");
            if (t.Weights.Count == 0) throw new JsonException($"orders.templates: {t.Id} has no phase weights");
            foreach (var p in t.Weights.Keys)
                if (!phases.Contains(p)) throw new JsonException($"orders.templates: {t.Id} names an unknown phase '{p}'");
            if (t.MinUnits <= 0 || t.MaxUnits < t.MinUnits || t.MinDays <= 0 || t.MaxDays < t.MinDays) throw new JsonException($"orders.templates: {t.Id} has bad ranges");
            if (t.Kind == "statework")
            {
                if (t.Works.Count == 0) throw new JsonException($"orders.templates: {t.Id} founds nothing");
                foreach (var w in t.Works)
                    if (!industries.Contains(w)) throw new JsonException($"orders.templates: {t.Id} founds unknown industry '{w}'");
            }
            else if (t.Kind != "quota" && t.Kind != "civil") throw new JsonException($"orders.templates: {t.Id} has unknown kind '{t.Kind}'");
        }
    }
}

/// <summary>The first order, month 1: easy, and a tutorial for stations, lines and the gateway.</summary>
public sealed record TutorialOrderTuning
{
    /// <summary>Cargo to try, in order: the first that some industry on the map makes.</summary>
    public required IReadOnlyList<string> Cargo { get; init; }
    public required int Units { get; init; }
    public required int Days { get; init; }
    public required int ExpiryDays { get; init; }
    public required long Reward { get; init; }
    public required float Standing { get; init; }
}

public sealed record OfferGapTuning
{
    public required string Phase { get; init; }
    public required int MinDays { get; init; }
    public required int MaxDays { get; init; }
}

/// <summary>One kind of order the government can draw. <see cref="Kind"/>: quota (deliver a cargo to a gateway), civil (deliver it to a town) or statework (the state founds <see cref="Works"/> and wants <see cref="Cargo"/> delivered to a gateway).</summary>
public sealed record OrderTemplate
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Cargo { get; init; }
    /// <summary>Weight of the template in each phase (by phase id); absent from a phase means it is not drawn there.</summary>
    public required IReadOnlyDictionary<string, int> Weights { get; init; }
    public required int MinUnits { get; init; }
    public required int MaxUnits { get; init; }
    public required int MinDays { get; init; }
    public required int MaxDays { get; init; }
    public required long RewardPerUnit { get; init; }
    public required float MinStanding { get; init; }
    public required float MaxStanding { get; init; }
    /// <summary>For a state work: the industries it founds (ids), the first near a town and the rest near it.</summary>
    public required IReadOnlyList<string> Works { get; init; }
    /// <summary>A state work is offered once per game.</summary>
    public required bool Once { get; init; }
}

/// <summary>Where a state work founds its industries.</summary>
public sealed record StateWorkSiteTuning
{
    public required int Attempts { get; init; }
    /// <summary>The first work stands within this of a town's middle ...</summary>
    public required float TownReach { get; init; }
    /// <summary>... and the others within this of the first.</summary>
    public required float ClusterReach { get; init; }
    public required float MinSpacing { get; init; }
    public required float TownClearance { get; init; }
    public required float MaxHeightRange { get; init; }
}
