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

public class DirtRoadTests
{
    [Fact]
    public void DirtRoad_IsPricedFromItsRowAndHasNoOvertaking()
    {
        var net = new RoadNetwork();
        var cost = net.EstimateCost(0, 0, 200, 0, RoadType.Dirt)!.Value;
        Assert.Equal(200f * net.Tuning.Roads.Dirt.CostPerMetre, cost.Money, 1);
        int a = net.AddNode(0, 0), b = net.AddNode(200, 0);
        var road = net.GetRoad(net.AddRoad(a, b, RoadType.Dirt, net.Tuning.Roads.Dirt.SpeedLimit));
        Assert.True(net.GetLane(road.ForwardLanes[0]).Oncoming.IsNone);
        Assert.Equal(3f, RoadNetwork.HalfWidth(RoadType.Dirt));
    }

    [Fact]
    public void DirtRoad_UpgradesToTwoLaneForTheDifferenceMinusRefund()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(200, 0);
        var id = net.AddRoad(a, b, RoadType.Dirt, 9f);
        var roads = net.Tuning.Roads;
        var cost = net.EstimateUpgrade(id)!.Value;
        Assert.Equal(200f * (roads.TwoLane.CostPerMetre - roads.UpgradeRefundShare * roads.Dirt.CostPerMetre), cost.Money, 1);

        var upgraded = net.UpgradeRoad(id, roads.TwoLane.SpeedLimit)!.Value;
        Assert.Equal(RoadType.TwoLane, net.GetRoad(upgraded).Type);
        Assert.False(net.GetLane(net.GetRoad(upgraded).ForwardLanes[0]).Oncoming.IsNone);
    }
}

public class AsymmetricLaneTests
{
    static (RoadNetwork Net, Road Road) Build(RoadType type, int forward, int back)
    {
        var net = new RoadNetwork();
        net.BuildRoad(0, 0, 200, 0, type, 14f, 0f, forward, back);
        return (net, net.GetRoad(new RoadId(0)));
    }

    [Fact]
    public void TwoLane_WithTwoForwardAndOneBack_HasThoseLanesAndNoOvertaking()
    {
        var (net, road) = Build(RoadType.TwoLane, 2, 1);
        Assert.Equal(2, road.ForwardLanes.Length);
        Assert.Single(road.BackwardLanes);
        Assert.True(net.GetLane(road.ForwardLanes[0]).Oncoming.IsNone);
        Assert.True(net.GetLane(road.BackwardLanes[0]).Oncoming.IsNone);
        Assert.Equal(5.25f, road.HalfWidth, 3);
    }

    [Fact]
    public void ExtraLane_CostsAShareOfTheTwoLanePrice()
    {
        var net = new RoadNetwork();
        var roads = net.Tuning.Roads;
        var plain = net.EstimateCost(0, 0, 200, 0, RoadType.TwoLane)!.Value.Money;
        var wide = net.EstimateCost(0, 0, 200, 0, RoadType.TwoLane, 0f, 2, 1)!.Value.Money;
        Assert.Equal(200f * roads.ExtraLaneShare * roads.TwoLane.CostPerMetre, wide - plain, 1);
    }

    [Theory]
    [InlineData(RoadType.TwoLane, 3, 2)]
    [InlineData(RoadType.TwoLane, 4, 0)]
    [InlineData(RoadType.TwoLane, 2, 0)]
    [InlineData(RoadType.Dirt, 2, 1)]
    [InlineData(RoadType.Path, 2, 2)]
    public void InvalidLaneCounts_AreRefused(RoadType type, int forward, int back)
    {
        var net = new RoadNetwork();
        Assert.Null(net.EstimateCost(0, 0, 200, 0, type, 0f, forward, back));
        Assert.Empty(net.BuildRoad(0, 0, 200, 0, type, 14f, 0f, forward, back));
    }

    [Fact]
    public void LanesAreCentredOnTheCentreLine()
    {
        var (net, road) = Build(RoadType.TwoLane, 2, 1);
        // Outer edge of each side equals the half width.
        float f = net.GetLane(road.ForwardLanes[0]).SideOffset + RoadNetwork.LaneWidth * 0.5f;
        float b = net.GetLane(road.BackwardLanes[0]).SideOffset + RoadNetwork.LaneWidth * 0.5f;
        Assert.Equal(road.HalfWidth, f, 3);
        Assert.Equal(road.HalfWidth, b, 3);
    }

    [Fact]
    public void SplittingAndSpeedChange_KeepTheLanes_AndUpgradeResetsThem()
    {
        var (net, road) = Build(RoadType.TwoLane, 2, 1);
        int middle = net.SplitRoad(road.Id, 100f);
        foreach (var r in net.RoadsAt(middle)) Assert.Equal(3, net.GetRoad(r).LanesForward + net.GetRoad(r).LanesBack);
        var first = net.RoadsAt(middle).Select(r => net.GetRoad(r)).First();
        var slower = net.GetRoad(net.SetSpeedLimit(first.Id, 8f)!.Value);
        Assert.Equal((first.LanesForward, first.LanesBack), (slower.LanesForward, slower.LanesBack));
        var upgraded = net.GetRoad(net.UpgradeRoad(slower.Id, 19f)!.Value);
        Assert.Equal((2, 2), (upgraded.LanesForward, upgraded.LanesBack));
    }
}
