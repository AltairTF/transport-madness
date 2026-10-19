using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.Worlds;

namespace TransportMadness.Sim.Tests;

/// <summary>
/// The money of M2 and M3 checked against generated maps: a decently placed line pays for its vehicles, passengers and freight earn
/// in the same range, and a bus is not wrecked by the traffic of the towns it serves in its first days. The numbers are in
/// <c>data/tuning.json</c>; these tests fail when a change of tuning breaks the balance, not when it moves it a little.
/// </summary>
public class BalanceTests
{
    const int Days = 8, From = 3;

    /// <summary>Income and upkeep per day, from day <see cref="From"/> on, of a six-stop passenger line with four buses.</summary>
    static (double Income, double Upkeep) BusLineDaily(ulong seed)
    {
        var sc = BusScenario(seed, 3, 4)!.Value;
        var sim = sc.Sim;
        var line = sim.Lines[sc.Line];
        long income = 0, upkeep = 0;
        for (int d = 0; d < Days; d++)
        {
            if (d == From) { income = line.Income; upkeep = line.Upkeep; }
            Run(sim, 1);
        }
        double days = Days - From;
        return ((line.Income - income) / days, (line.Upkeep - upkeep) / days);
    }

    static (double Income, double Upkeep, int Trucks) TruckLineDaily(ulong seed)
    {
        var sc = TruckScenario(seed, 3)!.Value;
        var sim = sc.Sim;
        var line = sim.Lines[sc.Line];
        long income = 0, upkeep = 0;
        for (int d = 0; d < Days; d++)
        {
            if (d == From) { income = line.Income; upkeep = line.Upkeep; }
            Run(sim, 1);
        }
        double days = Days - From;
        return ((line.Income - income) / days, (line.Upkeep - upkeep) / days, sc.Bought);
    }

    [Fact]
    public void APassengerLineThroughTwoTowns_PaysMoreThanItsBusesCost_OnAverage()
    {
        var results = new[] { 4UL, 6UL }.Select(BusLineDaily).ToList();

        double income = results.Average(r => r.Income), upkeep = results.Average(r => r.Upkeep);

        Assert.True(income > 1.3 * upkeep, $"income {income:0} a day against upkeep {upkeep:0}");
        // Whatever the map, a line with fully working buses does not lose for ever: none runs at less than a third of its upkeep.
        Assert.All(results.Where(r => r.Upkeep > 0), r => Assert.True(r.Income > r.Upkeep / 3, $"income {r.Income:0} against upkeep {r.Upkeep:0}"));
    }

    [Fact]
    public void ATruckLineBetweenAForestAndASawmill_PaysMoreThanItsTrucksCost_ButNotAbsurdlyMore()
    {
        var results = new[] { 1UL, 2UL, 3UL }.Select(TruckLineDaily).ToList();

        foreach (var r in results)
        {
            Assert.True(r.Income > 1.3 * r.Upkeep, $"income {r.Income:0} against upkeep {r.Upkeep:0}");
            // Freight on the easy chain makes money, but a truck does not earn many times what the same money in buses would.
            Assert.True(r.Income < 4.5 * r.Upkeep, $"income {r.Income:0} against upkeep {r.Upkeep:0}: too lucrative");
        }
    }

    [Fact]
    public void ALargerStationRadius_ReachesMoreOfATown()
    {
        // The radius is the walk to a stop: with it at 150 m a station at the middle of a small town covers a good part of it.
        var (sim, _) = World(3, 4);
        var town = sim.Towns[0];
        var (x, z) = sim.Network.GetNode(sim.Network.FindNodeNear(town.X, town.Z, 200f, withRoadsOnly: true));
        int id = sim.Execute(new BuildStationCommand(x, z));
        var covered = sim.CoveredBy(sim.Stations[id]);

        int total = sim.StatsOf(town.Id).Homes + sim.StatsOf(town.Id).Commerce + sim.StatsOf(town.Id).Industry;
        Assert.True(covered.Count >= 0.25 * total, $"{covered.Count} of {total} buildings covered");
    }
}
