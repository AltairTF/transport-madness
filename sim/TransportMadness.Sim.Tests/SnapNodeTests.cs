using TransportMadness.Sim;
using Xunit;

namespace TransportMadness.Sim.Tests;

/// <summary>Town upgrade, C: a click snaps to junctions and road ends only, not to a bend inside a street.</summary>
public class SnapNodeTests
{
    static RoadNetwork Flat() => new(Terrain.Create(512, 8f, (_, _) => 5f), Worlds.Late());

    static void Build(RoadNetwork net, float x0, float z0, float x1, float z1)
        => Assert.NotEmpty(net.BuildRoad(x0, z0, x1, z1, RoadType.TwoLane, 14f));

    [Fact]
    public void APassThroughNode_IsNotATarget_UntilAThirdRoadJoinsIt()
    {
        var net = Flat();
        Build(net, -100, 0, 0, 0);
        Build(net, 0, 0, 100, 0);
        Assert.Equal(-1, net.FindSnapNode(0, 0, 10f));

        Build(net, 0, 0, 0, 100);
        int node = net.FindSnapNode(0, 0, 10f);
        Assert.True(node >= 0);
        Assert.Equal(3, net.RoadsAt(node).Count);
    }

    [Fact]
    public void ARoadEnd_AndACorner_AreTargets_ButASlightBendIsNot()
    {
        var net = Flat();
        Build(net, -100, 0, 0, 0);
        Build(net, 0, 0, 100, 17); // about 10 degrees: a bend inside one street
        Build(net, 100, 17, 100, 117); // a corner
        Assert.True(net.FindSnapNode(-100, 0, 5f) >= 0, "the end of a road");
        Assert.Equal(-1, net.FindSnapNode(0, 0, 5f));
        Assert.True(net.FindSnapNode(100, 17, 5f) >= 0, "a corner of a street");
    }

    [Fact]
    public void ACloserPassThroughNode_IsSkipped_ForAFartherJunction()
    {
        var net = Flat();
        Build(net, -100, 0, 0, 0);
        Build(net, 0, 0, 100, 0);
        Build(net, 100, 0, 100, 100);
        Build(net, 100, 0, 200, 0);
        // At (0,0) only a bend; the nearest target within 120 m of the click is the junction at (100, 0).
        int node = net.FindSnapNode(2, 0, 120f);
        Assert.True(node >= 0);
        var (x, _) = net.GetNode(node);
        Assert.NotEqual(0f, x);
    }
}
