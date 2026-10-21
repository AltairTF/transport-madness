using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Free-standing depots (M6f): a depot goes anywhere, stands against a road, gets a Path to one within reach, or waits unconnected until a road reaches it.</summary>
public class FreeDepotTests
{
    static Terrain Flat() => Terrain.Create(512, 8f, (_, _) => 5f);

    static Simulation World(Terrain? terrain = null)
    {
        var sim = new Simulation(new RoadNetwork(terrain ?? Flat()), 7, IncidentFrequency.Off);
        sim.Company.Money = 10_000_000;
        return sim;
    }

    static int Build(Simulation sim, float x0, float z0, float x1, float z1, RoadType type = RoadType.TwoLane) =>
        sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, type, 14f));

    static int LiveRoads(Simulation sim) => Enumerable.Range(0, sim.Network.RoadCount).Count(r => !sim.Network.GetRoad(new RoadId(r)).Retired);

    static int BuildDepot(Simulation sim, float x, float z, DepotLevel level = DepotLevel.Simple) => sim.Execute(new BuildDepotCommand(x, z, level));

    [Fact]
    public void ADepotFarFromAnyRoad_StandsAloneAndIsNotConnected()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 0, 1000, 0));
        long before = sim.Company.Money;
        int id = BuildDepot(sim, 500, 400);
        Assert.Equal(0, id);
        var depot = sim.Depots[id];
        Assert.True(depot.FreeStanding);
        Assert.False(depot.Active);
        Assert.Equal(-1, depot.Node);
        Assert.Equal(before - sim.DepotPrice(false, DepotLevel.Simple), sim.Company.Money);
        Assert.Equal(1, LiveRoads(sim));
        Assert.Equal(DepotConnection.None, sim.PlanDepot(500, 400).Connection);
    }

    [Fact]
    public void ARoadBuiltPastAnUnconnectedDepot_GivesItAPath_AndConnectsIt()
    {
        var sim = World();
        int id = BuildDepot(sim, 500, 400);
        var depot = sim.Depots[id];
        Assert.False(depot.Active);
        // The depot's access point is 14 m from its middle toward the nearest road or town; a road passing within reach joins it by a Path.
        Assert.Equal(1, Build(sim, 0, depot.AccessZ + 40, 1000, depot.AccessZ + 40));
        Assert.True(depot.Active);
        Assert.True(depot.Node >= 0);
        Assert.Equal((depot.AccessX, depot.AccessZ), sim.Network.GetNode(depot.Node));
        var path = sim.Network.GetRoad(Assert.Single(sim.Network.RoadsAt(depot.Node)));
        Assert.Equal(RoadType.Path, path.Type);
    }

    [Fact]
    public void ADepotNearARoad_StandsAgainstItAndSplitsIt()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 100, 1000, 100));
        long before = sim.Company.Money;
        var plan = sim.PlanDepot(500, 112); // 12 m from the road
        Assert.Equal(DepotConnection.Beside, plan.Connection);
        Assert.Equal(100f, plan.AccessZ, 0.01f);
        Assert.Equal(114f, plan.Z, 0.01f); // 11 m half side and the 3 m access margin from the road
        int id = BuildDepot(sim, 500, 112);
        var depot = sim.Depots[id];
        Assert.True(depot.Active);
        Assert.Equal((500f, 100f), sim.Network.GetNode(depot.Node));
        Assert.Equal(2, sim.Network.RoadsAt(depot.Node).Count);
        Assert.Equal(2, LiveRoads(sim)); // the road in two pieces
        Assert.Equal(before - sim.DepotPrice(false, DepotLevel.Simple), sim.Company.Money); // no Path to pay for
    }

    [Fact]
    public void ADepotWithARoadWithinReach_IsBuiltWithAPath_ChargedAsARoad()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 100, 1000, 100));
        var plan = sim.PlanDepot(500, 160); // 60 m from the road
        Assert.Equal(DepotConnection.Path, plan.Connection);
        Assert.True(plan.PathBuildable);
        Assert.Equal(46f * 15f, plan.PathMoney, 1f); // from the road to the access point 14 m from the middle
        long before = sim.Company.Money;
        int id = BuildDepot(sim, 500, 160);
        var depot = sim.Depots[id];
        Assert.True(depot.Active);
        Assert.Equal((double)(before - sim.DepotPrice(false, DepotLevel.Simple)), (double)sim.Company.Money + plan.PathMoney, 1.5);
        var path = sim.Network.GetRoad(Assert.Single(sim.Network.RoadsAt(depot.Node)));
        Assert.Equal(RoadType.Path, path.Type);
        Assert.Equal(3, LiveRoads(sim)); // the road in two pieces, and the Path
    }

    [Fact]
    public void ADepotInWater_IsRefused()
    {
        var sim = World(Terrain.Create(512, 8f, (x, z) => MathF.Abs(x - 500) < 40 && MathF.Abs(z - 400) < 40 ? -5f : 5f));
        Assert.Equal(-1, BuildDepot(sim, 500, 400));
        Assert.Empty(sim.Depots);
    }

    [Fact]
    public void ADepotThatCannotBePaidFor_IsRefused()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 100, 1000, 100));
        sim.Company.Money = sim.DepotPrice(false, DepotLevel.Simple) + 100; // the depot, but not its Path
        Assert.Equal(-1, BuildDepot(sim, 500, 160));
        Assert.Empty(sim.Depots);
    }

    [Fact]
    public void ADepotWhoseRoadIsTornDown_IsNotConnected_AndConnectsAgainWhenARoadReachesIt()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 100, 1000, 100));
        var depot = sim.Depots[BuildDepot(sim, 500, 160)];
        Assert.True(depot.Active);
        int path = Enumerable.Range(0, sim.Network.RoadCount).First(r => sim.Network.GetRoad(new RoadId(r)) is { Retired: false, Type: RoadType.Path });
        Assert.Equal(1, sim.Execute(new RemoveRoadCommand(path)));
        sim.Step();
        Assert.False(depot.Active);
        Assert.Equal(1, Build(sim, 300, depot.AccessZ - 40, 800, depot.AccessZ - 40)); // a road passing it again
        Assert.True(depot.Active);
    }

    [Fact]
    public void AFreeDepot_CanBeUpgradedWhileNotConnected_AndTheSameCommandsGiveTheSameWorld()
    {
        static Simulation Run()
        {
            var sim = World();
            Build(sim, 0, 100, 1000, 100);
            int id = BuildDepot(sim, 500, 400);
            sim.Execute(new UpgradeDepotCommand(false, id, DepotLevel.Workshop));
            BuildDepot(sim, 300, 112);
            for (int i = 0; i < 50; i++) sim.Step();
            return sim;
        }
        var a = Run();
        Assert.Equal(DepotLevel.Workshop, a.Depots[0].Level);
        Assert.False(a.Depots[0].Active);
        Assert.Equal(a.StateHash(), Run().StateHash());
    }
}
