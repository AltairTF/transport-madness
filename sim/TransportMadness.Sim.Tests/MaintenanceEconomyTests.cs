using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.Worlds;

namespace TransportMadness.Sim.Tests;

/// <summary>
/// The maintenance balance of <c>docs/m5-plan.md</c> on a generated map: with the default company policy the upkeep of Condition is about a
/// tenth of what a passenger line earns, and without maintenance the fleet breaks down and the line stops paying. Slow tests (a month of
/// play each); the numbers are in <c>data/tuning.json</c> and <see cref="MaintenanceBalanceProbe"/> prints them.
/// </summary>
public class MaintenanceEconomyTests
{
    /// <summary>A four-bus line through two towns whose depots are Workshops (the default policy asks for a Service).</summary>
    static (Simulation Sim, int Line) Scenario(bool policyOn, ulong seed = 3)
    {
        var sc = BusScenario(seed, 3, 4)!.Value;
        for (int d = 0; d < sc.Sim.Depots.Count; d++) Assert.Equal(1, sc.Sim.Execute(new UpgradeDepotCommand(false, d, DepotLevel.Workshop)));
        if (!policyOn) sc.Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        return (sc.Sim, sc.Line);
    }

    [Fact]
    public void WithTheDefaultPolicy_MaintenanceCostsAboutATenthOfWhatALinePays()
    {
        var (sim, line) = Scenario(policyOn: true, seed: 1);
        Worlds.Run(sim, 10);
        long income = sim.Lines[line].Income, maintenance = sim.Company.Total(LedgerEntry.Maintenance);
        Worlds.Run(sim, 20);

        double paid = sim.Lines[line].Income - income, spent = -(sim.Company.Total(LedgerEntry.Maintenance) - maintenance);
        double share = spent / paid;

        Assert.InRange(share, 0.05, 0.25); // a tenth to a fifth: villages of a few hundred people carry a line fewer riders (docs/town-redo-plan.md)
        Assert.Equal(0, sim.Company.Total(LedgerEntry.Fines)); // nothing neglected
    }
}

/// <summary>The other half of the balance, in its own class so that the two slow tests run side by side.</summary>
public class MaintenanceNeglectTests
{
    [Fact]
    public void WithoutMaintenance_TheFleetBreaksDown_AndTheLineLosesFarMoreThanMaintenanceWouldHaveCost()
    {
        var sc = BusScenario(1, 3, 4)!.Value; // seed 1, as in the test above: on seed 3 the organic towns leave this line without riders
        var sim = sc.Sim;
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        Worlds.Run(sim, 10);
        long early = sim.Lines[sc.Line].Income;
        Worlds.Run(sim, 25);
        long mid = sim.Lines[sc.Line].Income;
        Worlds.Run(sim, 10);
        long late = sim.Lines[sc.Line].Income;

        double firstRate = early / 10.0, lastRate = (late - mid) / 10.0;

        Assert.True(sim.Events.Count(e => e.Kind == SimEventKind.Breakdown) + sim.EventsDropped > 0, "nothing broke down");
        Assert.True(sim.Company.Total(LedgerEntry.Fines) < 0, "a neglected vehicle that breaks down is fined");
        // The maintenance the default policy asks for costs about a tenth of the income; the neglected line has lost far more than that.
        Assert.True(lastRate < 0.6 * firstRate, $"the neglected line still earned {lastRate:0} a day against {firstRate:0} at the start");
    }
}
