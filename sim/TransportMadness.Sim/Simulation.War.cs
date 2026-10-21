namespace TransportMadness.Sim;

/// <summary>The war (C9): rationing of imports now; the call-up, the requisitions and the raids in their own files.</summary>
public sealed partial class Simulation
{
    readonly WarTuning _war;

    public WarTuning WarTuning => _war;

    /// <summary>The share of a phase's change in prices and shipments that applies: 0 with the frequency option off, 1 at normal, 2 at high.</summary>
    float RationingScale => _frequencyScale;

    bool IsRationed(string cargo) => _war.Rationing.Cargo.Contains(cargo);

    /// <summary>The multiple of the offer's price now (1 for cargo that is not rationed, and in a phase with no rationing).</summary>
    public float RationingPriceFactor(string cargo) =>
        IsRationed(cargo) ? 1f + (_war.Rationing.Of(Phase.Id).PriceFactor - 1f) * RationingScale : 1f;

    /// <summary>The multiple of the offer's units now, never more than 1 in the phases of the war.</summary>
    public float RationingUnitsFactor(string cargo) =>
        IsRationed(cargo) ? MathF.Max(0.05f, 1f + (_war.Rationing.Of(Phase.Id).UnitsFactor - 1f) * RationingScale) : 1f;

    /// <summary>What a unit of an import costs now.</summary>
    public int ImportPrice(ImportTuning offer) => (int)MathF.Round(offer.PricePerUnit * RationingPriceFactor(offer.Cargo));

    /// <summary>The size of a shipment of an import now.</summary>
    public int ImportUnits(ImportTuning offer) => Math.Max(1, (int)MathF.Round(offer.Units * RationingUnitsFactor(offer.Cargo)));

    /// <summary>At the turn of a month: the news when the phase changes the cost of the imports.</summary>
    void WarNewsForMonth(long nextMonth)
    {
        var before = _campaign.PhaseOf(nextMonth - 1);
        var after = _campaign.PhaseOf(nextMonth);
        if (before.Id == after.Id || _frequencyScale <= 0f) return;
        var offers = _gatewayTuning.Imports.Where(i => IsRationed(i.Cargo)).ToList();
        if (offers.Count == 0) return;
        var f = _war.Rationing.Of(after.Id);
        AddEvent(SimEventKind.War, EventSeverity.Warning, 0f, 0f, -1,
            $"{after.Name}: rationing. {string.Join(", ", offers.Select(o => _cargo[CargoIndex(_net.Tuning, o.Cargo)].Name.ToLowerInvariant()))} cost {RationingPriceFactor(offers[0].Cargo):0.##} times as much and shipments are {RationingUnitsFactor(offers[0].Cargo):P0} of the size");
    }
}
