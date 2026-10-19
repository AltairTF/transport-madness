using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class BuildRoadTests
{
    static RoadNetwork WithEastWestRoad(out RoadId road)
    {
        var net = new RoadNetwork();
        int w = net.AddNode(-100, 0);
        int e = net.AddNode(100, 0);
        road = net.AddRoad(w, e, RoadType.TwoLane, 14f);
        return net;
    }

    static int ActiveRoads(RoadNetwork net)
    {
        int n = 0;
        for (int r = 0; r < net.RoadCount; r++) if (!net.GetRoad(new RoadId(r)).Retired) n++;
        return n;
    }

    [Fact]
    public void RoadCrossingAnExistingOne_MakesAFourWayJunction()
    {
        var net = WithEastWestRoad(out var original);

        var built = net.BuildRoad(0, -100, 0, 100, RoadType.TwoLane, 14f);

        Assert.Equal(2, built.Count);
        Assert.True(net.GetRoad(original).Retired);
        Assert.Equal(4, ActiveRoads(net));
        int centre = net.FindNodeNear(0, 0, 0.5f);
        Assert.True(centre >= 0);
        Assert.Equal(4, net.IncomingLanes(centre).Count);
    }

    [Fact]
    public void RoadEndingOnTheMiddleOfAnExistingOne_MakesATJunction()
    {
        var net = WithEastWestRoad(out _);

        var built = net.BuildRoad(0, 80, 0, 0, RoadType.TwoLane, 14f);

        Assert.Single(built);
        int junction = net.FindNodeNear(0, 0, 0.5f);
        Assert.Equal(3, net.IncomingLanes(junction).Count);
        Assert.Equal(3, ActiveRoads(net));
    }

    [Fact]
    public void ReplacementHalves_KeepTheOriginalRoadTypeAndSpeedLimit()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(-100, 0), b = net.AddNode(100, 0);
        net.AddRoad(a, b, RoadType.Avenue2x2, 19f);

        net.BuildRoad(0, -100, 0, 100, RoadType.TwoLane, 14f);

        int centre = net.FindNodeNear(0, 0, 0.5f);
        bool sawAvenue = false;
        foreach (var lane in net.IncomingLanes(centre))
        {
            var road = net.GetRoad(net.GetLane(lane).Road);
            if (road.Type == RoadType.Avenue2x2) { sawAvenue = true; Assert.Equal(19f, road.SpeedLimit); }
        }
        Assert.True(sawAvenue);
    }

    [Fact]
    public void CrossingTooCloseToAJunctionNode_IsRefused_AndChangesNothing()
    {
        var net = WithEastWestRoad(out _);
        int nodes = net.NodeCount, lanes = net.LaneCount;

        Assert.False(net.CanBuildRoad(-95, -100, -95, 100));
        Assert.Empty(net.BuildRoad(-95, -100, -95, 100, RoadType.TwoLane, 14f));

        Assert.Equal(nodes, net.NodeCount);
        Assert.Equal(lanes, net.LaneCount);
    }

    [Fact]
    public void BuildRoad_RefusesDuplicatesAndStubs()
    {
        var net = WithEastWestRoad(out _);
        net.BuildRoad(0, -100, 0, 100, RoadType.TwoLane, 14f);

        Assert.False(net.CanBuildRoad(0, -100, 0, 0)); // already there (as half of the road just built)
        Assert.False(net.CanBuildRoad(300, 300, 303, 300)); // shorter than the minimum
    }

    [Fact]
    public void BuildRoad_ReusesNodesAtItsEndPoints()
    {
        var net = WithEastWestRoad(out _);
        int before = net.NodeCount;

        net.BuildRoad(100, 0, 100, 150, RoadType.TwoLane, 14f);

        Assert.Equal(before + 1, net.NodeCount);
    }

    [Fact]
    public void VehiclesOnASplitRoad_KeepTheirWorldPosition()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(300, 0);
        var road = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        var sim = new Simulation(net, seed: 1);
        int eastbound = sim.SpawnVehicle(net.GetRoad(road).ForwardLanes[0], 150f, 10f);
        int westbound = sim.SpawnVehicle(net.GetRoad(road).BackwardLanes[0], 100f, 10f); // at x = 200

        net.BuildRoad(100, 0, 100, 120, RoadType.TwoLane, 14f); // splits the road at x = 100
        sim.Step();

        var e = sim.GetVehicle(eastbound);
        var w = sim.GetVehicle(westbound);
        Assert.False(net.GetLane(e.Lane).Retired);
        Assert.False(net.GetLane(w.Lane).Retired);
        Assert.Equal(150.5f, WorldX(net, e), 0.2f);
        Assert.Equal(199.5f, WorldX(net, w), 0.2f);
        for (int i = 0; i < 20 * 30; i++) sim.Step();
        Assert.False(net.GetLane(sim.GetVehicle(eastbound).Lane).Retired);
    }

    static float WorldX(RoadNetwork net, VehicleState v)
    {
        var lane = net.GetLane(v.Lane);
        var (x0, _) = net.GetNode(lane.StartNode);
        var (x1, _) = net.GetNode(lane.EndNode);
        return x0 + MathF.Sign(x1 - x0) * v.Position;
    }
}
