using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Coverage along the streets (M6h): beyond a station's circle it spreads along the roads and falls to nothing, and people there take the station with that chance.</summary>
public class ReachTests
{
    /// <summary>A flat map and a 700 m road along x in pieces of 100 m, a station at x = 100, and homes at known places.</summary>
    static (Simulation Sim, Station Station, int Near, int Out, int Far, int Lonely) World()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 3);
        sim.Company.Money = 10_000_000;
        for (int x = 0; x < 700; x += 100) Assert.Equal(1, sim.Execute(new BuildRoadCommand(x, 0, x + 100, 0, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(1, new[]
        {
            new TownPlan("Testville", 300, 0, 0, 1, new[]
            {
                new BuildingPlan(BuildingType.Home, 0, 150, 20, 0),  // 50 m from the station: inside its circle
                new BuildingPlan(BuildingType.Home, 0, 320, 20, 0),  // 220 m in a straight line, about 228 m along the street
                new BuildingPlan(BuildingType.Home, 0, 540, 20, 0),  // about 440 m along the street
                new BuildingPlan(BuildingType.Home, 0, 200, 900, 0), // no road near it
                new BuildingPlan(BuildingType.Commerce, 1, 620, 20, 0), // a shop next to the road's far end
            }),
        }));
        int id = sim.Execute(new BuildStationCommand(100, 0, false));
        Assert.True(id >= 0);
        return (sim, sim.Stations[id], 0, 1, 2, 3);
    }

    [Fact]
    public void CoverageFallsInALine_FromFullAtTheCircleToNothingAtTheReach()
    {
        var (sim, _, _, _, _, _) = World();
        Assert.Equal(1f, sim.CoverageAtWalk(0f));
        Assert.Equal(1f, sim.CoverageAtWalk(150f));
        Assert.Equal(0.5f, sim.CoverageAtWalk(225f), 0.001f);
        Assert.Equal(0f, sim.CoverageAtWalk(300f));
        Assert.Equal(0f, sim.CoverageAtWalk(500f));
    }

    [Fact]
    public void AStation_ReachesAlongTheStreetsToABuildingOutsideItsCircle()
    {
        var (sim, station, near, outside, far, lonely) = World();
        Assert.Contains(near, sim.CoveredBy(station)); // the circle is as it was
        Assert.DoesNotContain(outside, sim.CoveredBy(station));
        var reach = sim.ReachOf(station);
        var reached = Assert.Single(reach);
        Assert.Equal(outside, reached.Building);
        Assert.InRange(reached.Distance, 220f, 240f);
        Assert.Equal(sim.CoverageAtWalk(reached.Distance), reached.Coverage, 0.001f);
        Assert.InRange(reached.Coverage, 0.4f, 0.6f);
        Assert.DoesNotContain(reach, r => r.Building == far || r.Building == lonely || r.Building == near);
        Assert.Equal(station.Id, Assert.Single(sim.ReachingStationsOf(outside)).Station);
        Assert.Empty(sim.ReachingStationsOf(near));
    }

    [Fact]
    public void ABuildingTheCircleCoversHasNoReach_AndTheCircleStillCountsAsFullCoverage()
    {
        var (sim, station, near, _, _, _) = World();
        Assert.DoesNotContain(sim.ReachOf(station), r => r.Building == near);
        Assert.Equal(new[] { station.Id }, sim.PlaceStations(near));
    }

    [Fact]
    public void AGroupTakesAReachedStation_WithTheChanceOfTheCoverage_AndAlwaysTheSameWay()
    {
        var (sim, station, near, outside, _, _) = World();
        float coverage = sim.ReachOf(station).Single().Coverage;
        int taken = 0;
        for (int i = 0; i < 2000; i++)
        {
            var group = new TripGroup(i, outside, 4, (ulong)(i + 1) * 7919UL, false);
            var once = sim.StationsFor(group, outside);
            Assert.Equal(once, sim.StationsFor(group, outside)); // the same draw every time
            if (once.Contains(station.Id)) taken++;
            Assert.Contains(station.Id, sim.StationsFor(group, near)); // full coverage is never drawn
        }
        Assert.InRange(taken / 2000f, coverage - 0.04f, coverage + 0.04f);
    }

    [Fact]
    public void ThePeopleWalkTheStreetDistance_ToAReachedStation()
    {
        var (sim, station, near, outside, _, _) = World();
        var reached = sim.ReachOf(station).Single();
        var (x, z) = (sim.Buildings[outside].X, sim.Buildings[outside].Z);
        float straight = MathF.Sqrt((x - 100) * (x - 100) + z * z);
        Assert.True(reached.Distance > straight);
        float walk = sim.WalkMetres(outside, station);
        Assert.Equal(reached.Distance, walk * sim.Network.Tuning.Trips.WalkDetour, 1f); // the callers add the detour
        Assert.InRange(sim.WalkMetres(near, station), 53f, 55f);                       // inside the circle it is the straight line
    }

    [Fact]
    public void ARoadTornUp_TakesTheReachBeyondItAway()
    {
        var (sim, station, _, outside, _, _) = World();
        Assert.Contains(sim.ReachOf(station), r => r.Building == outside);
        var piece = sim.Network.RoadAt(250, 0, 5f)!.Value;
        Assert.True(sim.Execute(new RemoveRoadCommand(piece.Value)) >= 0);
        Assert.DoesNotContain(sim.ReachOf(station), r => r.Building == outside);
    }

    [Fact]
    public void AClosedStation_ReachesNothing()
    {
        var (sim, station, _, _, _, _) = World();
        Assert.NotEmpty(sim.ReachOf(station));
        sim.Execute(new CloseStationCommand(station.Id));
        Assert.Empty(sim.ReachOf(station));
    }

    [Fact]
    public void PeopleOutsideTheCircle_RideALine_OnlyAsOftenAsTheirCoverage()
    {
        var (sim, a, _, outside, _, _) = World();
        const int shop = 4;
        float coverage = sim.ReachOf(a).Single().Coverage;
        int b = sim.Execute(new BuildStationCommand(600, 0, false));
        int depot = sim.Execute(new AddDepotCommand(sim.Network.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int line = sim.Execute(new CreateLineCommand(new[] { a.Id, b }));
        Assert.True(sim.Execute(new BuyVehicleCommand(depot, "bus", line)) >= 0);
        Assert.Equal(TripMode.None, sim.ModeFor(outside, shop, hasCar: false).Mode); // no group, no draw: only the circle counts

        int rode = 0;
        for (int i = 0; i < 400; i++)
        {
            var group = new TripGroup(i, outside, 4, (ulong)(i + 1) * 104729UL, false);
            var mode = sim.ChooseMode(outside, shop, false, group).Mode;
            bool drew = sim.StationsFor(group, outside).Contains(a.Id);
            Assert.Equal(drew ? TripMode.Transit : TripMode.None, mode);
            if (mode == TripMode.Transit) rode++;
        }
        Assert.InRange(rode / 400f, coverage - 0.08f, coverage + 0.08f);
    }
}
