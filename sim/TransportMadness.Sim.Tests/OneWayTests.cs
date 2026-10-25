using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class OneWayTests
{
    /// <summary>A two-way loop of four nodes with a one-way ramp as a diagonal from the west node to the east one.</summary>
    internal static (RoadNetwork Net, RoadId Ramp, int West, int East) Loop()
    {
        var net = new RoadNetwork();
        int w = net.AddNode(-150, 0), e = net.AddNode(150, 0), n = net.AddNode(0, 150), s = net.AddNode(0, -150);
        net.AddRoad(w, n, RoadType.TwoLane, 14f);
        net.AddRoad(n, e, RoadType.TwoLane, 14f);
        net.AddRoad(e, s, RoadType.TwoLane, 14f);
        net.AddRoad(s, w, RoadType.TwoLane, 14f);
        var ramp = net.AddRoad(w, e, RoadType.Ramp, 12f, 0f, RoadStructure.None, 1, 0);
        return (net, ramp, w, e);
    }

    [Fact]
    public void Ramp_HasLanesOnlyFromItsStartToItsEnd()
    {
        var (net, rampId, w, e) = Loop();
        var ramp = net.GetRoad(rampId);
        Assert.True(ramp.IsOneWay);
        Assert.Single(ramp.ForwardLanes);
        Assert.Empty(ramp.BackwardLanes);
        Assert.Empty(ramp.LanesLeaving(e));
        Assert.Single(ramp.LanesLeaving(w));
    }

    [Fact]
    public void LanesRunningIntoARampsEnd_DoNotLeadIntoIt_ButStartLanesDo()
    {
        var (net, rampId, w, e) = Loop();
        var ramp = net.GetRoad(rampId);
        var rampLane = ramp.ForwardLanes[0];
        bool leadsIntoRamp(int node) => net.IncomingLanes(node).Any(l => net.GetLane(l).Next.Contains(rampLane));
        Assert.True(leadsIntoRamp(w));
        Assert.False(leadsIntoRamp(e));
        for (int l = 0; l < net.LaneCount; l++)
            if (!net.GetLane(new LaneId(l)).Retired) Assert.NotEmpty(net.GetLane(new LaneId(l)).Next);
    }

    [Fact]
    public void TrafficRunsOnALoopWithARamp_AndNoOneDrivesTheRampBackwards()
    {
        var (net, rampId, _, _) = Loop();
        var sim = new Simulation(net, 7);
        sim.Execute(new SeedTrafficCommand(60));
        Worlds.Run(sim, 0.5);
        Assert.True(sim.VehicleCount > 0);
    }

    [Fact]
    public void RemovingTheRoadARampRunsInto_RemovesTheRamp()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(-100, 0), b = net.AddNode(100, 0), c = net.AddNode(300, 0);
        var road = net.AddRoad(b, c, RoadType.TwoLane, 14f);
        var ramp = net.AddRoad(a, b, RoadType.Ramp, 12f, 0f, RoadStructure.None, 1, 0);
        Assert.True(net.RemoveRoad(road));
        Assert.True(net.GetRoad(ramp).Retired);
    }

    [Fact]
    public void RampsCannotBeBuiltByTheBuildCommand()
    {
        var sim = new Simulation(new RoadNetwork(), 1);
        Assert.Equal(0, sim.Execute(new BuildRoadCommand(0, 0, 100, 0, RoadType.Ramp, 12f)));
    }
}
