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

    /// <summary>Income and upkeep per period of 600 s, from period <see cref="From"/> on, of a six-stop passenger line with four buses.</summary>
    static (double Income, double Upkeep) BusLineDaily(ulong seed)
    {
        var sc = BusScenario(seed, 3, 4)!.Value;
        var sim = sc.Sim;
        var line = sim.Lines[sc.Line];
        long income = 0, upkeep = 0;
        for (int d = 0; d < Days; d++)
        {
            if (d == From) { income = line.Income; upkeep = line.Upkeep; }
            RunPeriod(sim);
        }
        double days = Days - From;
        return ((line.Income - income) / days, (line.Upkeep - upkeep) / days);
    }

    static (double Income, double Upkeep, int Trucks) TruckLineDaily(ulong seed)
    {
        // Two trucks: a forest makes about 150 units a day, which is what two trucks carry; a third would only wait. The generator
        // keeps a sawmill within 1500 m of a forest, so a line of under 800 m is a short hop and pays little: not the case measured here.
        var sc = TruckScenario(seed, 2, minDistance: 800f)!.Value;
        var sim = sc.Sim;
        var line = sim.Lines[sc.Line];
        long income = 0, upkeep = 0;
        for (int d = 0; d < Days; d++)
        {
            if (d == From) { income = line.Income; upkeep = line.Upkeep; }
            RunPeriod(sim);
        }
        double days = Days - From;
        return ((line.Income - income) / days, (line.Upkeep - upkeep) / days, sc.Bought);
    }

    [Fact]
    public void APassengerLineThroughTwoTowns_PaysMoreThanItsBusesCost_OnAverage()
    {
        var results = new[] { 4UL, 6UL }.Select(BusLineDaily).ToList();

        double income = results.Average(r => r.Income), upkeep = results.Average(r => r.Upkeep);

        Assert.True(income > 1.3 * upkeep, $"income {income:0} a period against upkeep {upkeep:0}");
        // Whatever the map, a line with fully working buses does not lose for ever: none runs at less than a third of its upkeep.
        Assert.All(results.Where(r => r.Upkeep > 0), r => Assert.True(r.Income > r.Upkeep / 3, $"income {r.Income:0} against upkeep {r.Upkeep:0}"));
    }

    [Fact]
    public void ATruckLineBetweenAForestAndASawmill_PaysMoreThanItsTrucksCost_ButNotAbsurdlyMore()
    {
        var results = new[] { 1UL, 3UL, 4UL }.Select(TruckLineDaily).ToList();

        foreach (var r in results)
        {
            Assert.True(r.Income > 1.3 * r.Upkeep, $"income {r.Income:0} against upkeep {r.Upkeep:0}");
            // Freight on the easy chain makes money, but a truck does not earn many times what the same money in buses would.
            Assert.True(r.Income < 4.5 * r.Upkeep, $"income {r.Income:0} against upkeep {r.Upkeep:0}: too lucrative");
        }
    }

    [Fact]
    public void BusesOfALine_KeepMovingThroughTheRush_AndTheCarCapIsNotLivedAt()
    {
        // The rush used to fill the road with the cap of trip cars and buses crawled at 1 to 8 m/s; with a car standing for several
        // people the road is busy but moving. Mean speed of the moving-or-waiting buses, sampled from day From on.
        foreach (ulong seed in new[] { 1UL, 4UL })
        {
            var sc = BusScenario(seed, 3, 4)!.Value;
            var sim = sc.Sim;
            double speed = 0; long n = 0; int peak = 0;
            long dayTicks = (long)Math.Round(Period / Simulation.StepSeconds);
            for (long t = 0; t < Days * dayTicks; t++)
            {
                sim.Step();
                if (t < From * dayTicks || t % 20 != 0) continue;
                peak = Math.Max(peak, sim.TripVehicleCount);
                for (int v = 0; v < sim.VehicleCount; v++)
                    if (sim.IsCompanyVehicle(v) && sim.GetVehicle(v).Kind == VehicleKind.Car && !sim.GetFleetInfo(v).Loading) { speed += sim.GetVehicle(v).Speed; n++; }
            }
            Assert.True(n > 0 && speed / n > 4.5, $"seed {seed}: buses averaged {speed / Math.Max(1, n):0.0} m/s");
            Assert.True(peak < sim.TripTuning.MaxCars, $"seed {seed}: the rush reached the cap of {sim.TripTuning.MaxCars} trip cars");
        }
    }

    /// <summary>
    /// A station in the middle of town 0 of a three-town map, fed every day with <paramref name="perDay"/> units each of building
    /// materials and planks (a truck line of about three trucks at 60) and, when <paramref name="needsMet"/>, the town's monthly
    /// needs of food and goods. Returns the month of the first growth (0 when none) and the population before and after.
    /// </summary>
    static (int FirstGrowth, int Before, int After, float Satisfaction) ServedTown(ulong seed, int perDay, bool needsMet, int months)
    {
        var (sim, _) = World(seed, 1);
        var town = sim.Towns[0];
        var (x, z) = sim.Network.GetNode(sim.Network.FindNodeNear(town.X, town.Z, 200f, withRoadsOnly: true));
        sim.Execute(new TakeLoanCommand(1000000));
        var station = sim.Stations[sim.Execute(new BuildStationCommand(x, z))];
        int Cargo(string id) => Enumerable.Range(0, sim.CargoTypes.Count).First(c => sim.CargoTypes[c].Id == id);
        int materials = Cargo("materials"), planks = Cargo("planks"), food = Cargo("food"), goods = Cargo("goods");
        var covered = sim.CoveredBy(station);
        int Need(int cargo) => (int)covered.Sum(id => sim.TownTuning.Of(sim.Buildings[id].Type, sim.Buildings[id].Tier).Sustain.Where(a => a.Cargo == sim.CargoTypes[cargo].Id).Sum(a => a.Amount));
        int daysPerMonth = sim.Network.Tuning.Economy.DaysPerMonth;
        int before = sim.StatsOf(town.Id).Population, first = 0;
        for (int m = 1; m <= months; m++)
        {
            for (int d = 0; d < daysPerMonth; d++)
            {
                if (perDay > 0) { sim.Distribute(station, materials, perDay); sim.Distribute(station, planks, perDay); }
                if (needsMet) { sim.Distribute(station, food, Need(food) / daysPerMonth); sim.Distribute(station, goods, Need(goods) / daysPerMonth); }
                Run(sim, 1);
            }
            if (first == 0 && town.LastGrowth.Upgrades + town.LastGrowth.NewBuildings > 0) first = m;
        }
        return (first, before, sim.StatsOf(town.Id).Population, town.Satisfaction);
    }

    [Fact]
    public void AServedTownWithAFewTrucksOfBuildingSupplies_StartsGrowingWithinAFewMonths_AndDoesNotExplode()
    {
        // Measured with the probe (Growth_OfAServedTown): first growth in month 2 or 3 and the population 1.5 to 2.2 times its size after a year.
        var r = ServedTown(4, perDay: 120, needsMet: true, months: 4);

        Assert.InRange(r.FirstGrowth, 1, 4);
        Assert.InRange((double)r.After / r.Before, 1.1, 2.0);
    }

    [Fact]
    public void ATownThatGetsItsNeedsButNoBuildingSupplies_StaysTheSameSize()
    {
        var r = ServedTown(4, perDay: 0, needsMet: true, months: 4);

        Assert.Equal(0, r.FirstGrowth);
        Assert.Equal(r.Before, r.After);
    }

    [Fact]
    public void LessSupplyMeansSlowerGrowth()
    {
        var little = ServedTown(4, perDay: 60, needsMet: true, months: 4);
        var more = ServedTown(4, perDay: 120, needsMet: true, months: 4);

        Assert.True(more.After > little.After, $"{more.After} people with 120 a period against {little.After} with 60");
        Assert.True(little.FirstGrowth == 0 || little.FirstGrowth >= more.FirstGrowth);
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
