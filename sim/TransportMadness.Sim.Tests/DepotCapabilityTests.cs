using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>What a depot can repair and how many it takes at once depends on its level, which is raised in place by paying the difference.</summary>
public class DepotCapabilityTests
{
    [Fact]
    public void ADepotUpgradedInPlace_PaysTheDifference_AndGainsBaysAndCrews()
    {
        var w = Road(DepotLevel.Simple);
        var sim = w.Sim;
        var depot = sim.Depots[w.Depot];
        var price = sim.Network.Tuning.Economy.DepotPrice;
        long before = sim.Company.Money;
        Assert.Equal(1, sim.BaysOf(depot));
        Assert.Equal(sim.Network.Tuning.Crews.PerDepot.Simple, depot.Crews);

        Assert.Equal(1, sim.Execute(new UpgradeDepotCommand(false, w.Depot, DepotLevel.Workshop)));

        Assert.Equal(DepotLevel.Workshop, depot.Level);
        Assert.Equal(before - (price.Workshop - price.Simple), sim.Company.Money);
        Assert.Equal(2, sim.BaysOf(depot));
        Assert.Equal(sim.Network.Tuning.Crews.PerDepot.Workshop, depot.Crews);
        Assert.Equal(1, sim.Execute(new UpgradeDepotCommand(false, w.Depot, DepotLevel.Complex)));
        Assert.Equal(3, sim.BaysOf(depot));
        Assert.Equal(before - (price.Complex - price.Simple), sim.Company.Money);
    }

    [Fact]
    public void AnUpgradeIsRefused_WithAReason_ForTheSameLevelALowerOneAClosedDepotOrNoMoney()
    {
        var w = Road(DepotLevel.Workshop);
        var sim = w.Sim;

        Assert.Contains("already", sim.WhyNotUpgrade(false, w.Depot, DepotLevel.Workshop));
        Assert.Contains("already", sim.WhyNotUpgrade(false, w.Depot, DepotLevel.Simple));
        Assert.Contains("closed", sim.WhyNotUpgrade(false, 9, DepotLevel.Complex));
        Assert.Contains("closed", sim.WhyNotUpgrade(true, 0, DepotLevel.Complex));
        Assert.Null(sim.WhyNotUpgrade(false, w.Depot, DepotLevel.Complex));
        Assert.Equal(0, sim.Execute(new UpgradeDepotCommand(false, w.Depot, DepotLevel.Simple)));
        Assert.Equal(0, sim.Execute(new UpgradeDepotCommand(false, 9, DepotLevel.Complex)));
        var poor = Tuning.Load();
        var w2 = Road(DepotLevel.Workshop, poor with { Economy = poor.Economy with { DepotPrice = poor.Economy.DepotPrice with { Complex = 100000000 } } });
        Assert.Contains("money", w2.Sim.WhyNotUpgrade(false, w2.Depot, DepotLevel.Complex));
        Assert.Equal(0, w2.Sim.Execute(new UpgradeDepotCommand(false, w2.Depot, DepotLevel.Complex)));
        Assert.Equal(DepotLevel.Workshop, w2.Sim.Depots[w2.Depot].Level);
    }

    [Fact]
    public void ARailDepotIsUpgradedInPlaceToo_AndTheNewLevelsCostTheirPrices()
    {
        var w = Rail(DepotLevel.Simple);
        var sim = w.Sim;
        long before = sim.Company.Money;

        Assert.Equal(1, sim.Execute(new UpgradeDepotCommand(true, w.Depot, DepotLevel.Workshop)));

        Assert.Equal(DepotLevel.Workshop, sim.RailDepots[w.Depot].Level);
        Assert.Equal(before - (sim.RailDepotPrice(DepotLevel.Workshop) - sim.RailDepotPrice(DepotLevel.Simple)), sim.Company.Money);
        Assert.Equal(2, sim.BaysOf(sim.RailDepots[w.Depot]));
        Assert.Equal(0, sim.Execute(new UpgradeDepotCommand(true, w.Depot, DepotLevel.Workshop)));
    }

    [Fact]
    public void ABuiltRailDepotOfAHigherLevel_CostsThatLevelsPrice()
    {
        var sim = Rail(DepotLevel.Simple).Sim;
        sim.Execute(new BuildTrackCommand(0, 300, 300, 300));
        long before = sim.Company.Money;

        int id = sim.Execute(new AddRailDepotCommand(0, 300, DepotLevel.Complex));

        Assert.True(id >= 0);
        Assert.Equal(DepotLevel.Complex, sim.RailDepots[id].Level);
        Assert.Equal(before - sim.RailDepotPrice(DepotLevel.Complex), sim.Company.Money);
    }

    [Fact]
    public void ARepairWaitingForABay_StartsWhenTheDepotIsUpgraded()
    {
        var slow = Tuned(m => m with { Patch = m.Patch with { Seconds = 600f } });
        var w = Road(DepotLevel.Simple, slow); // one bay
        var sim = w.Sim;
        var buses = new[] { Bus(w), Bus(w) };
        Run(sim, 30); // out of the depot
        foreach (int bus in buses)
        {
            sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
            sim.Execute(new RequestRepairCommand(RepairSubject.Vehicle, bus, RepairKind.Patch));
        }
        Assert.True(RunUntil(sim, 600, () => sim.RepairJobs.Count == 2), "both never reached the depot");
        Assert.Equal(1, sim.RepairJobs.Count(j => j.Working));
        var waiting = sim.RepairJobs.Single(j => !j.Working);

        sim.Execute(new UpgradeDepotCommand(false, w.Depot, DepotLevel.Workshop));
        Run(sim, 0.2);

        Assert.True(waiting.Working, "the second bay stayed empty");
        Assert.Equal(2, sim.RepairJobs.Count(j => j.Working));
    }
}
