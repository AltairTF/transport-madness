using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.Worlds;

namespace TransportMadness.Sim.Tests;

/// <summary>What a railway earns on generated maps, next to what the trains cost.</summary>
public class RailEconomyTests
{
    const int Days = 9, From = 3;

    /// <summary>The two closest of four towns joined by one track from one edge of the first to one of the second, a station at each end and one train.</summary>
    static (Simulation Sim, int Line, int Train, float Dist)? PassengerRail(ulong seed, int wagons = 4)
    {
        foreach (float side in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f })
        {
            var (sim, _) = World(seed, 4);
            sim.Execute(new TakeLoanCommand(1000000));
            var (i, j) = ClosestPair(sim);
            var ta = sim.Towns[i]; var tb = sim.Towns[j];
            float dx = tb.X - ta.X, dz = tb.Z - ta.Z, d = MathF.Sqrt(dx * dx + dz * dz); dx /= d; dz /= d;
            float nx = -dz, nz = dx;
            float x0 = ta.X + dx * 100 + nx * side, z0 = ta.Z + dz * 100 + nz * side, x1 = tb.X - dx * 100 + nx * side, z1 = tb.Z - dz * 100 + nz * side;
            if (sim.Execute(new BuildTrackCommand(x0, z0, x1, z1)) == 0) continue;
            int sa = sim.Execute(new BuildRailStationCommand(x0, z0)), sb = sim.Execute(new BuildRailStationCommand(x1, z1));
            int depot = sim.Execute(new AddRailDepotCommand(x0, z0));
            int line = sim.Execute(new CreateLineCommand(new[] { sa, sb }));
            int train = sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", wagons, line));
            if (line < 0 || train < 0) continue;
            return (sim, line, train, d);
        }
        return null;
    }

    [Fact]
    public void ARailLineBetweenTwoTowns_EarnsMoreThanItsTrainCosts()
    {
        var results = new List<(double Income, double Upkeep)>();
        foreach (ulong seed in new[] { 1UL, 2UL, 3UL, 4UL, 5UL, 6UL, 7UL })
        {
            if (PassengerRail(seed) is not { } sc) continue;   // some towns leave no room for a track that crosses their streets properly
            if (results.Count == 3) break;
            var line = sc.Sim.Lines[sc.Line];
            long income = 0, upkeep = 0;
            for (int d = 0; d < Days; d++)
            {
                if (d == From) { income = line.Income; upkeep = line.Upkeep; }
                RunPeriod(sc.Sim);
            }
            results.Add(((line.Income - income) / (double)(Days - From), (line.Upkeep - upkeep) / (double)(Days - From)));
        }

        Assert.True(results.Count >= 3, $"only {results.Count} maps took the track");
        double earned = results.Average(r => r.Income), cost = results.Average(r => r.Upkeep);
        Assert.True(earned > 1.2 * cost, $"income {earned:0} a day against upkeep {cost:0}");
        Assert.All(results, r => Assert.True(r.Income > r.Upkeep * 0.6, $"income {r.Income:0} against upkeep {r.Upkeep:0}"));
    }

    static long Capital(Simulation sim) =>
        -(sim.Company.Total(LedgerEntry.Construction) + sim.Company.Total(LedgerEntry.Stations) + sim.Company.Total(LedgerEntry.Depots) + sim.Company.Total(LedgerEntry.Vehicles));

    static double NetPerDay(Simulation sim, int line)
    {
        var l = sim.Lines[line];
        long income = 0, upkeep = 0;
        for (int d = 0; d < Days; d++)
        {
            if (d == From) { income = l.Income; upkeep = l.Upkeep; }
            RunPeriod(sim);
        }
        return ((l.Income - income) - (l.Upkeep - upkeep)) / (double)(Days - From);
    }

    /// <summary>
    /// Rail against road for the same pair of towns and about the same seats (four coaches, 192, against six buses, 180): the railway costs
    /// roughly twice as much to build and buy (track, station, depot and a locomotive against a road and six buses), and earns more a day,
    /// because the buses fill up and the train does not. Measured on eight maps in the balance pass of 2026-10-21.
    /// </summary>
    [Fact]
    public void ARailwayCostsAboutTwiceWhatBusesCostForTheSameSeats_AndEarnsMore()
    {
        var capitalRatios = new List<double>();
        double railNet = 0, busNet = 0;
        foreach (ulong seed in new[] { 1UL, 2UL, 3UL, 4UL, 6UL, 7UL })
        {
            if (PassengerRail(seed) is not { } rail) continue;
            if (BusScenario(seed, 1, 6) is not { } bus || bus.Bought < 6) continue;
            capitalRatios.Add(Capital(rail.Sim) / (double)Capital(bus.Sim));
            railNet += NetPerDay(rail.Sim, rail.Line);
            busNet += NetPerDay(bus.Sim, bus.Line);
        }

        Assert.True(capitalRatios.Count >= 4, $"only {capitalRatios.Count} maps took both");
        Assert.All(capitalRatios, r => Assert.InRange(r, 1.4, 2.8));
        Assert.True(railNet > busNet, $"net a day over the maps: railway {railNet:0}, buses {busNet:0}");
    }

    [Fact]
    public void ATrainTakesPeopleOffTheStreets_TheTownsShowTripsByTransit()
    {
        var sc = PassengerRail(4) ?? PassengerRail(6)!.Value;
        Run(sc.Sim, 4);

        int byTransit = sc.Sim.Towns.Sum(t => t.TripsToday[(int)TripMode.Transit] + t.TripsYesterday[(int)TripMode.Transit]);

        Assert.True(byTransit > 0, "some groups ride the train");
        Assert.True(sc.Sim.Lines[sc.Line].Delivered > 0);
    }

    [Fact]
    public void AFreightTrainBetweenAForestAndASawmill_PaysMoreThanItCosts_ButNotAbsurdlyMore()
    {
        var results = new List<(double Income, double Upkeep)>();
        foreach (ulong seed in new[] { 1UL, 2UL, 3UL, 4UL, 5UL, 6UL, 7UL, 8UL })
        {
            if (results.Count == 3) break;
            var (sim, _) = World(seed, 3, null, IndustryAmount.Normal);
            sim.Execute(new TakeLoanCommand(1000000));
            var primaries = sim.Industries.Where(i => i.Type == 0).ToList();
            var processors = sim.Industries.Where(i => i.Type == 3).ToList();
            float Gap(Industry a, Industry b) => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Z - b.Z, 2));
            var pairs = primaries.SelectMany(x => processors.Select(y => (x, y))).Where(t => Gap(t.x, t.y) >= 800f).OrderBy(t => Gap(t.x, t.y)).ToList();
            if (pairs.Count == 0) continue;
            var pair = pairs[0];
            float d = Gap(pair.x, pair.y), dx = (pair.y.X - pair.x.X) / d, dz = (pair.y.Z - pair.x.Z) / d;
            int built = 0, sa = -1, sb = -1, depot = -1;
            foreach (float side in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f })
            {
                float nx = -dz * side, nz = dx * side;
                if (sim.Execute(new BuildTrackCommand(pair.x.X + dx * 40 + nx, pair.x.Z + dz * 40 + nz, pair.y.X - dx * 40 + nx, pair.y.Z - dz * 40 + nz)) == 0) continue;
                built = 1;
                sa = sim.Execute(new BuildRailStationCommand(pair.x.X + dx * 40 + nx, pair.x.Z + dz * 40 + nz));
                sb = sim.Execute(new BuildRailStationCommand(pair.y.X - dx * 40 + nx, pair.y.Z - dz * 40 + nz));
                depot = sim.Execute(new AddRailDepotCommand(pair.x.X + dx * 40 + nx, pair.x.Z + dz * 40 + nz));
                break;
            }
            if (built == 0 || sa < 0 || sb < 0 || depot < 0) continue;
            int line = sim.Execute(new CreateLineCommand(new[] { sa, sb }));
            if (sim.Execute(new BuyTrainCommand(depot, "diesel", "freight", 2, line)) < 0) continue;
            var l = sim.Lines[line];
            long income = 0, upkeep = 0;
            for (int day = 0; day < Days; day++)
            {
                if (day == From) { income = l.Income; upkeep = l.Upkeep; }
                RunPeriod(sim);
            }
            results.Add(((l.Income - income) / (double)(Days - From), (l.Upkeep - upkeep) / (double)(Days - From)));
        }

        Assert.True(results.Count >= 2, $"only {results.Count} maps took the track");
        foreach (var r in results)
        {
            Assert.True(r.Income > 1.2 * r.Upkeep, $"income {r.Income:0} against upkeep {r.Upkeep:0}");
            Assert.True(r.Income < 6 * r.Upkeep, $"income {r.Income:0} against upkeep {r.Upkeep:0}: too lucrative");
        }
    }
}
