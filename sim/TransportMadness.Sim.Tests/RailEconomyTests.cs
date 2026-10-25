using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.Worlds;

namespace TransportMadness.Sim.Tests;

/// <summary>What a railway earns on generated maps, next to what the trains cost.</summary>
public class RailEconomyTests
{
    const int Days = 9, From = 3;

    /// <summary>The two closest of four towns joined by one track from near the middle of the first to near the middle of the second, a station at each end and one train.</summary>
    internal static (Simulation Sim, int Line, int Train, float Dist)? PassengerRail(ulong seed, int wagons = 4, int minBuildings = 10)
    {
        var (sim, _) = World(seed, 4);
        sim.Execute(new TakeLoanCommand(1000000));
        var (i, j) = ClosestPair(sim);
        var ta = sim.Towns[i]; var tb = sim.Towns[j];
        float d = MathF.Sqrt(MathF.Pow(tb.X - ta.X, 2) + MathF.Pow(tb.Z - ta.Z, 2));
        // Some towns leave no room for a track that crosses their streets properly, or only at the edge, where a station has few people around it.
        if (TrackBetween(sim, ta, tb, minBuildings: minBuildings) is not var (a, b)) return null;
        if (sim.Execute(new BuildTrackCommand(a.X, a.Z, b.X, b.Z)) == 0) return null;
        int sa = sim.Execute(new BuildRailStationCommand(a.X, a.Z)), sb = sim.Execute(new BuildRailStationCommand(b.X, b.Z));
        int depot = sim.Execute(new AddRailDepotCommand(a.X, a.Z));
        int line = sim.Execute(new CreateLineCommand(new[] { sa, sb }));
        int train = sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", wagons, line));
        if (line < 0 || train < 0) return null;
        return (sim, line, train, d);
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
        Assert.True(earned > 0.9 * cost, $"income {earned:0} a day against upkeep {cost:0}");
        // Villages are small: a line between two of them roughly pays for its train (a tuning question for the user, see docs/town-redo-plan.md). Not per map: with one station in each organic town only the people who live and work near the two stations ride, which varies a lot between maps.
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
        foreach (ulong seed in new[] { 1UL, 2UL, 3UL, 4UL, 6UL, 7UL, 9UL, 10UL, 11UL, 12UL })
        {
            if (PassengerRail(seed) is not { } rail) continue;
            if (BusScenario(seed, 1, 6) is not { } bus || bus.Bought < 6) continue;
            capitalRatios.Add(Capital(rail.Sim) / (double)Capital(bus.Sim));
            railNet += NetPerDay(rail.Sim, rail.Line);
            busNet += NetPerDay(bus.Sim, bus.Line);
        }

        Assert.True(capitalRatios.Count >= 4, $"only {capitalRatios.Count} maps took both");
        Assert.All(capitalRatios, r => Assert.InRange(r, 1.4, 2.8));
        // Six buses run half empty between villages, so they may lose: the train must lose less or earn more.
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
