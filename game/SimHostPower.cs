using System;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>The region's electricity (C6) for the Industries window.</summary>
public partial class SimHost
{
    /// <summary>
    /// The grid: {supply, demand (megawatts), factor (the share of their output the industries manage, 0 to 1), free_grid, purchased, max_purchase,
    /// step, price (per megawatt and month), cost (of what is bought now, per month), plants, plants_burning}.
    /// </summary>
    public Godot.Collections.Dictionary GetPower()
    {
        var plants = Sim.Industries.Where(i => Sim.IndustryTuning.Types[i.Type].Supply > 0f).ToList();
        var t = Sim.PowerTuning;
        return new Godot.Collections.Dictionary
        {
            ["supply"] = Sim.PowerSupply, ["demand"] = Sim.PowerDemand, ["factor"] = Sim.PowerFactor, ["free_grid"] = Sim.FreeGrid,
            ["purchased"] = Sim.Company.PowerPurchase, ["max_purchase"] = t.MaxPurchase, ["step"] = t.PurchaseStep,
            ["price"] = t.PricePerMegawattMonth, ["cost"] = Sim.PowerCostPerMonth,
            ["plants"] = plants.Count, ["plants_burning"] = plants.Count(p => p.Load > 0.5f),
        };
    }

    /// <summary>Sets the megawatts bought from outside for each month; false when out of range.</summary>
    public bool SetPowerPurchase(int megawatts) => Sim.Execute(new SetPowerPurchaseCommand(megawatts)) == 1;

    /// <summary>Chooses the rule by which the army takes vehicles in the announced wave (0 freight first, 1 the oldest first, 2 passengers first); false outside the month of notice (C15b).</summary>
    public bool SetRequisitionRule(int rule) => Sim.Execute(new SetRequisitionRuleCommand((RequisitionRule)rule)) == 1;
}
