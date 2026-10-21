namespace TransportMadness.Sim;

/// <summary>Electricity (C6): a capacity of the whole region. Plants that burn coal and power bought from outside add to the grid the map started with; a shortage slows every industry in proportion.</summary>
public sealed partial class Simulation
{
    readonly PowerTuning _power;
    float _freeGrid;
    float _powerSupply, _powerDemand, _powerFactor = 1f;
    bool _inShortage;

    public PowerTuning PowerTuning => _power;
    /// <summary>The megawatts the grid the map started with supplies, free.</summary>
    public float FreeGrid => _freeGrid;
    /// <summary>Megawatts supplied now: the free grid, what is bought and the plants that are burning.</summary>
    public float PowerSupply => _powerSupply;
    /// <summary>Megawatts the industries ask for.</summary>
    public float PowerDemand => _powerDemand;
    /// <summary>The share of its output every industry manages: 1 while the supply covers the demand, else supply over demand.</summary>
    public float PowerFactor => _powerFactor;
    /// <summary>What the power bought from outside costs at the turn of a month.</summary>
    public long PowerCostPerMonth => _power.PricePerMegawattMonth * Company.PowerPurchase;

    int SetPowerPurchase(int megawatts)
    {
        if (megawatts < 0 || megawatts > _power.MaxPurchase) return 0;
        Company.PowerPurchase = megawatts;
        return 1;
    }

    /// <summary>The supply and demand of this step, from what the plants burned in the last one.</summary>
    void UpdatePowerBalance()
    {
        float supply = _freeGrid + Company.PowerPurchase, demand = 0f;
        foreach (var industry in _industries)
        {
            var type = _industryTuning.Types[industry.Type];
            demand += type.Demand;
            supply += industry.Load * type.Supply;
        }
        _powerSupply = supply;
        _powerDemand = demand;
        _powerFactor = demand <= 0f || supply >= demand ? 1f : supply / demand;
        bool short_ = _powerFactor < 0.995f;
        if (short_ == _inShortage) return;
        _inShortage = short_;
        AddEvent(SimEventKind.Power, short_ ? EventSeverity.Warning : EventSeverity.Info, -1,
            short_ ? $"Power shortage: the industries make {_powerFactor:P0} of their output" : "Power is back to full supply");
    }

    /// <summary>The power bought for the month is paid at its turn.</summary>
    void ChargePower()
    {
        long cost = PowerCostPerMonth;
        if (cost > 0) Charge(cost, LedgerEntry.Power);
    }
}
