using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class RoadNetworkTests
{
    [Fact]
    public void TwoLaneRoad_HasOneLanePerDirection_LinkedAsOncoming()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(100, 0);

        var road = net.AddRoad(a, b, RoadType.TwoLane, speedLimit: 13.9f);

        Assert.Equal(2, net.LaneCount);
        var forward = net.GetLane(net.GetRoad(road).ForwardLanes[0]);
        var backward = net.GetLane(net.GetRoad(road).BackwardLanes[0]);
        Assert.Equal(100f, forward.Length, 3);
        Assert.Equal(13.9f, forward.SpeedLimit);
        Assert.Equal(backward.Id, forward.Oncoming);
        Assert.Equal(forward.Id, backward.Oncoming);
    }

    [Fact]
    public void AvenueRoad_HasTwoLanesPerDirection_LinkedLeftAndRight()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(200, 0);

        var road = net.AddRoad(a, b, RoadType.Avenue2x2, speedLimit: 22f);

        Assert.Equal(4, net.LaneCount);
        var lanes = net.GetRoad(road).ForwardLanes;
        Assert.Equal(2, lanes.Length);
        var right = net.GetLane(lanes[0]);
        var left = net.GetLane(lanes[1]);
        Assert.Equal(left.Id, right.Left);
        Assert.Equal(right.Id, left.Right);
        Assert.Equal(LaneId.None, right.Right);
        Assert.Equal(LaneId.None, left.Left);
        // Avenues have no oncoming-lane overtaking: the opposite carriageway is separated.
        Assert.Equal(LaneId.None, left.Oncoming);
    }

    [Fact]
    public void Junction_ConnectsIncomingLaneToOtherRoadsButNotBackOntoItself()
    {
        var net = new RoadNetwork();
        int west = net.AddNode(-100, 0);
        int centre = net.AddNode(0, 0);
        int east = net.AddNode(100, 0);
        int north = net.AddNode(0, 100);
        var roadW = net.AddRoad(west, centre, RoadType.TwoLane, 13.9f);
        var roadE = net.AddRoad(centre, east, RoadType.TwoLane, 13.9f);
        var roadN = net.AddRoad(centre, north, RoadType.TwoLane, 13.9f);

        var incoming = net.GetRoad(roadW).ForwardLanes[0]; // west -> centre
        var next = net.GetLane(incoming).Next;

        Assert.Equal(2, next.Length);
        Assert.Contains(net.GetRoad(roadE).ForwardLanes[0], next);
        Assert.Contains(net.GetRoad(roadN).ForwardLanes[0], next);
        Assert.DoesNotContain(net.GetRoad(roadW).BackwardLanes[0], next);
    }

    [Fact]
    public void DeadEnd_TurnsLaneAroundOntoOppositeLane()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(100, 0);
        var road = net.AddRoad(a, b, RoadType.TwoLane, 13.9f);

        var forward = net.GetRoad(road).ForwardLanes[0];
        var backward = net.GetRoad(road).BackwardLanes[0];

        Assert.Equal(new[] { backward }, net.GetLane(forward).Next);
        Assert.Equal(new[] { forward }, net.GetLane(backward).Next);
    }

    [Fact]
    public void Road_RejectsNonPositiveSpeedLimitAndTooShortSegments()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int near = net.AddNode(3, 0);
        int far = net.AddNode(100, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => net.AddRoad(a, far, RoadType.TwoLane, 0f));
        Assert.Throws<ArgumentException>(() => net.AddRoad(a, near, RoadType.TwoLane, 14f));
    }
}
