using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Access points (C10): where an industry or a gateway meets the roads, how a road is drawn to it or given a spur, and what that costs.</summary>
public class AccessPointTests
{
    const int Forest = 0; // size 30, so its access point is 33 m from its middle toward the town

    static Terrain Flat() => Terrain.Create(512, 8f, (_, _) => 5f);

    static TownPlan Town(float x, float z) => new("Test Town", x, z, 0f, 1, Array.Empty<BuildingPlan>());

    /// <summary>A flat map with a forest at (500, 300) and a town due north of it, so that the access point is (500, 333); no roads.</summary>
    static Simulation World(Tuning? tuning = null, params GatewayPlan[] gateways)
    {
        var net = new RoadNetwork(Flat(), tuning);
        var sim = new Simulation(net, 7, IncidentFrequency.Off);
        sim.AddTowns(new WorldPlan(1, new[] { Town(500, 1000) }, new[] { new IndustryPlan(Forest, 500, 300, 0f) }, null, gateways));
        sim.Company.Money = 10_000_000;
        return sim;
    }

    static int Build(Simulation sim, float x0, float z0, float x1, float z1, RoadType type = RoadType.TwoLane, float sweep = 0f) =>
        sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, type, 14f, sweep));

    static int LiveRoads(Simulation sim) => Enumerable.Range(0, sim.Network.RoadCount).Count(r => !sim.Network.GetRoad(new RoadId(r)).Retired);

    // ---- Where the point is -------------------------------------------------------------------------------------------

    [Fact]
    public void TheAccessPoint_IsOnTheEdgeFacingTheNearestTown()
    {
        var sim = new Simulation(new RoadNetwork(Flat()), 7, IncidentFrequency.Off);
        sim.AddTowns(new WorldPlan(2, new[] { Town(500, 1000), Town(-1500, 300) }, new[] { new IndustryPlan(Forest, 500, 300, 0f) }));
        var forest = sim.Industries[0];
        Assert.Equal(500f, forest.AccessX, 3);
        Assert.Equal(333f, forest.AccessZ, 3); // half the side (30) plus the edge margin (3) due north
    }

    [Fact]
    public void TheAccessPoint_FollowsTheTurnOfTheSquare()
    {
        var sim = new Simulation(new RoadNetwork(Flat()), 7, IncidentFrequency.Off);
        sim.AddTowns(new WorldPlan(1, new[] { Town(500, 1000) }, new[] { new IndustryPlan(Forest, 500, 300, MathF.PI / 4f) }));
        // Turned by 45 degrees the square reaches 30 * sqrt(2) due north.
        Assert.Equal(500f, sim.Industries[0].AccessX, 2);
        Assert.Equal(300f + 30f * MathF.Sqrt(2f) + 3f, sim.Industries[0].AccessZ, 2);
    }

    [Fact]
    public void WithoutATown_TheAccessPointIsAheadOfTheIndustry()
    {
        var sim = new Simulation(new RoadNetwork(Flat()), 7, IncidentFrequency.Off);
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), new[] { new IndustryPlan(Forest, 500, 300, 0f) }));
        Assert.Equal(533f, sim.Industries[0].AccessX, 3);
        Assert.Equal(300f, sim.Industries[0].AccessZ, 3);
    }

    [Fact]
    public void OnAMapWithoutRoads_IndustriesHaveAPointAndNoNode()
    {
        var sim = World();
        Assert.Equal(0, sim.Network.NodeCount);
        Assert.Equal(-1, sim.AccessNodeOf(sim.Industries[0]));
        Assert.Equal(sim.StateHash(), World().StateHash()); // nothing about it moves the hash
    }

    [Fact]
    public void EveryGeneratedIndustryAndGateway_HasAPointOutsideItsSquare()
    {
        var (sim, _) = WorldBuilder.Towns(11, 4, IndustryAmount.Normal);
        Assert.NotEmpty(sim.Industries);
        foreach (var i in sim.Industries)
        {
            float size = sim.IndustryTuning.Types[i.Type].Size;
            float d = MathF.Sqrt((i.AccessX - i.X) * (i.AccessX - i.X) + (i.AccessZ - i.Z) * (i.AccessZ - i.Z));
            Assert.InRange(d, size + 3f - 0.01f, size * 1.4143f + 3.01f);
        }
        foreach (var g in sim.Gateways)
        {
            float d = MathF.Sqrt((g.AccessX - g.X) * (g.AccessX - g.X) + (g.AccessZ - g.Z) * (g.AccessZ - g.Z));
            Assert.InRange(d, sim.GatewayTuning.Size + 3f - 0.01f, sim.GatewayTuning.Size * 1.4143f + 3.01f);
        }
    }

    // ---- A road that ends near the point --------------------------------------------------------------------------------

    [Fact]
    public void ARoadEndingWithinReach_IsDrawnToTheAccessPoint()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 340, 470, 340)); // ends 30 m short of the point, 7 m south of its line
        int node = sim.AccessNodeOf(sim.Industries[0]);
        Assert.True(node >= 0);
        var (x, z) = sim.Network.GetNode(node);
        Assert.Equal((500f, 333f), (x, z));
        Assert.Equal(1, LiveRoads(sim));
        Assert.Single(sim.Network.RoadsAt(node));
    }

    [Fact]
    public void ARoadEndingBeyondReach_IsLeftAlone()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 340, 430, 340)); // 70 m from the point
        Assert.Equal(-1, sim.AccessNodeOf(sim.Industries[0]));
        Assert.NotEqual(-1, sim.Network.FindNodeNear(430, 340, 0.5f));
    }

    [Fact]
    public void AnEndThatStandsOnANode_IsNotMoved()
    {
        // A node 51 m from the point, from a road that was there before the industry (a station or depot stands on such a node).
        var sim = new Simulation(new RoadNetwork(Flat()), 7, IncidentFrequency.Off);
        sim.Company.Money = 10_000_000;
        Assert.Equal(1, Build(sim, 450, 320, 450, 100));
        sim.AddTowns(new WorldPlan(1, new[] { Town(500, 1000) }, new[] { new IndustryPlan(Forest, 500, 300, 0f) }));
        Assert.Null(sim.JoinRoad(450, 320, 450, 600, RoadType.TwoLane).Start);
        int before = LiveRoads(sim);
        Assert.Equal(1, Build(sim, 450, 320, 450, 600)); // starts on that node: not drawn to the point, and it passes 50 m from it, so it gets a spur
        Assert.NotEqual(-1, sim.Network.FindNodeNear(450, 320, 0.5f, withRoadsOnly: true));
        Assert.Equal(before + 2 + 1, LiveRoads(sim)); // the road is cut where the spur leaves it
        Assert.True(sim.AccessNodeOf(sim.Industries[0]) >= 0);
    }

    [Fact]
    public void WhenBothEndsAreNearTheSamePoint_OnlyTheNearerIsDrawnToIt()
    {
        var sim = World();
        var join = sim.JoinRoad(480, 333, 520, 333, RoadType.TwoLane);
        Assert.Equal(new AccessRef(false, 0), join.Start);
        Assert.Null(join.End);
        Assert.Equal((500f, 333f), (join.X0, join.Z0));
        Assert.Equal((520f, 333f), (join.X1, join.Z1));
    }

    [Fact]
    public void AnEndIsNotMoved_WhenTheRoadWouldNoLongerBeBuildable()
    {
        // A wall of rock stands at the point: a road drawn up to it would climb far too steeply, so the road is built as it was asked.
        var net = new RoadNetwork(Terrain.Create(512, 8f, (x, z) => MathF.Abs(x - 500) < 20 && MathF.Abs(z - 333) < 20 ? 60f : 5f));
        var sim = new Simulation(net, 7, IncidentFrequency.Off);
        sim.AddTowns(new WorldPlan(1, new[] { Town(500, 1000) }, new[] { new IndustryPlan(Forest, 500, 300, 0f) }));
        sim.Company.Money = 10_000_000;
        var join = sim.JoinRoad(500, 450, 500, 385, RoadType.TwoLane);
        Assert.Null(join.End);
        Assert.Equal((500f, 385f), (join.X1, join.Z1));
        Assert.Equal(1, Build(sim, 500, 450, 500, 385));
        Assert.Equal(-1, sim.AccessNodeOf(sim.Industries[0]));
    }

    // ---- A road that passes the point ---------------------------------------------------------------------------------------

    [Fact]
    public void ARoadPassingWithinReach_GetsASpurToTheAccessPoint()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 380, 1000, 380)); // passes 47 m north of the point
        int node = sim.AccessNodeOf(sim.Industries[0]);
        Assert.True(node >= 0);
        Assert.Single(sim.Network.RoadsAt(node));
        var spur = sim.Network.GetRoad(sim.Network.RoadsAt(node)[0]);
        Assert.Equal(47f, spur.Length, 0.01f);
        int junction = spur.From == node ? spur.To : spur.From;
        Assert.Equal(3, sim.Network.RoadsAt(junction).Count); // the spur leaves the road at a junction
        Assert.Equal((500f, 380f), sim.Network.GetNode(junction));
        Assert.Equal(3, LiveRoads(sim)); // the road in two pieces and the spur
    }

    [Fact]
    public void TheSpurHasTheTypeAndSpeedOfTheRoad()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 380, 1000, 380, RoadType.Avenue2x2));
        var spur = sim.Network.GetRoad(sim.Network.RoadsAt(sim.AccessNodeOf(sim.Industries[0]))[0]);
        Assert.Equal(RoadType.Avenue2x2, spur.Type);
        Assert.Equal(14f, spur.SpeedLimit);
    }

    [Fact]
    public void ARoadPassingBeyondReach_GetsNoSpur()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 420, 1000, 420)); // 87 m away
        Assert.Equal(1, LiveRoads(sim));
        Assert.Equal(-1, sim.AccessNodeOf(sim.Industries[0]));
    }

    [Fact]
    public void ARoadAlreadyAtTheDoor_GetsNoSpur()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 340, 1000, 340)); // 7 m from the point: a spur would be shorter than a road piece
        Assert.Equal(1, LiveRoads(sim));
        Assert.Equal(-1, sim.AccessNodeOf(sim.Industries[0]));
    }

    [Fact]
    public void ARoadThatPassesTheEndOfItself_GetsNoSpurWhereNoJunctionFits()
    {
        var sim = World();
        // The nearest point of the road to the access point is 5 m from its end: too near for a junction.
        Assert.Equal(1, Build(sim, 495, 380, 900, 380));
        Assert.Equal(1, LiveRoads(sim));
    }

    [Fact]
    public void ACurvedRoad_GetsItsSpurToo()
    {
        var sim = World();
        float sweep = RoadGeometry.SweepThrough(0, 400, 500, 370, 1000, 400);
        Assert.Equal(1, Build(sim, 0, 400, 1000, 400, RoadType.TwoLane, sweep));
        int node = sim.AccessNodeOf(sim.Industries[0]);
        Assert.True(node >= 0, "the spur reached the access point");
        var spur = sim.Network.GetRoad(sim.Network.RoadsAt(node)[0]);
        int junction = spur.From == node ? spur.To : spur.From;
        Assert.Equal(3, sim.Network.RoadsAt(junction).Count); // and joined the curve
    }

    [Fact]
    public void ARoadBuiltLaterThroughAnExistingAccessNode_JoinsItInstead()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 340, 470, 340));
        int node = sim.AccessNodeOf(sim.Industries[0]);
        Assert.Equal(1, Build(sim, 500, 333, 800, 333)); // starts on the node
        Assert.Equal(2, sim.Network.RoadsAt(node).Count);
        Assert.Equal(node, sim.AccessNodeOf(sim.Industries[0]));
    }

    // ---- Price -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSpurIsInTheBill_AndTheEstimateAgreesWithTheCharge()
    {
        var sim = World();
        var join = sim.JoinRoad(0, 380, 1000, 380, RoadType.TwoLane);
        var spur = Assert.Single(join.Spurs);
        Assert.Equal(new AccessRef(false, 0), spur.Target);
        Assert.Equal(47f * 40f, spur.Money, 1f); // two-lane price per metre on level ground
        long before = sim.Company.Money;
        Assert.Equal(1, Build(sim, 0, 380, 1000, 380));
        Assert.Equal(1000 * 40 + 47 * 40, before - sim.Company.Money);
    }

    [Fact]
    public void TheRoadIsRefused_WhenItsSpurCannotBePaidFor()
    {
        var sim = World();
        sim.Company.Money = 1000 * 40 + 100; // the road alone, but not the spur
        long before = sim.Company.Money;
        Assert.Equal(0, Build(sim, 0, 380, 1000, 380));
        Assert.Equal(before, sim.Company.Money);
        Assert.Equal(0, LiveRoads(sim));
    }

    // ---- Gateways ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARoadGateway_IsJoinedLikeAnIndustry()
    {
        var gate = new GatewayPlan(GatewayKind.Road, "Test Road Gate", 1500, 600, 0f);
        var sim = World(null, gate);
        var g = sim.Gateways[0];
        // The town at (500, 1000) lies to the north-west; the square (half-side 22) is left where that direction leaves it, and the margin is added.
        float dx = -1000f / MathF.Sqrt(1000f * 1000f + 400f * 400f), dz = 400f / MathF.Sqrt(1000f * 1000f + 400f * 400f);
        float reach = 22f / MathF.Max(MathF.Abs(dx), MathF.Abs(dz)) + 3f;
        Assert.Equal(1500f + dx * reach, g.AccessX, 2);
        Assert.Equal(600f + dz * reach, g.AccessZ, 2);
        Assert.Equal(1, Build(sim, 1200, 900, g.AccessX + 20, g.AccessZ + 20)); // ends 28 m from the point
        Assert.True(sim.AccessNodeOf(g) >= 0);
    }

    [Fact]
    public void ARailGateway_IsNotOnTheRoadNetwork()
    {
        var gate = new GatewayPlan(GatewayKind.Rail, "Test Rail Gate", 1500, 600, 0f);
        var sim = World(null, gate);
        var g = sim.Gateways[0];
        Assert.Equal(1, Build(sim, 1000, 700, g.AccessX + 20, g.AccessZ));
        Assert.Equal(-1, sim.AccessNodeOf(g));
        Assert.Equal(1, LiveRoads(sim));
    }

    // ---- What does not change ----------------------------------------------------------------------------------------------

    [Fact]
    public void WhatAStationCovers_DoesNotDependOnAccessPoints()
    {
        var sim = World();
        Assert.Equal(1, Build(sim, 0, 340, 470, 340));
        int farStation = sim.Execute(new BuildStationCommand(300, 340)); // 200 m from the industry's middle: outside the 150 m radius
        Assert.True(farStation >= 0);
        Assert.Empty(sim.IndustriesCoveredBy(sim.Stations[farStation]));
        Assert.Equal(1, Build(sim, 300, 340, 300, 600)); // another road, away from the industry
        int nearStation = sim.Execute(new BuildStationCommand(400, 340)); // 100 m: covered, whether or not it is on the road to the point
        Assert.True(nearStation >= 0);
        Assert.Single(sim.IndustriesCoveredBy(sim.Stations[nearStation]));
    }

    // ---- Determinism -------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSameCommands_GiveTheSameWorld_AndTheLogReplays()
    {
        static Simulation Run()
        {
            var sim = World();
            Build(sim, 0, 380, 1000, 380);
            Build(sim, 0, 340, 470, 460, RoadType.Avenue2x2);
            for (int i = 0; i < 200; i++) sim.Step();
            return sim;
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a.StateHash(), b.StateHash());
        var replayed = World();
        replayed.Replay(a.CommandLog, a.Tick);
        Assert.Equal(a.StateHash(), replayed.StateHash());
        Assert.Equal(a.Network.NodeCount, replayed.Network.NodeCount);
    }

    [Fact]
    public void ThePreview_IsWhatTheCommandBuilds()
    {
        var sim = World();
        var join = sim.JoinRoad(0, 400, 480, 345, RoadType.TwoLane);
        Assert.Equal(new AccessRef(false, 0), join.End);
        Assert.Equal(1, Build(sim, 0, 400, 480, 345));
        Assert.NotEqual(-1, sim.Network.FindNodeNear(join.X1, join.Z1, 0.5f, withRoadsOnly: true));
        Assert.Equal(sim.AccessNodeOf(sim.Industries[0]), sim.Network.FindNodeNear(join.X1, join.Z1, 0.5f));
    }
}
