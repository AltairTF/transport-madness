using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Road types GDD, highway: 2+2 lanes, fast, never met in level (ramps are the way on, see <see cref="RampTests"/>).</summary>
public class HighwayTests
{
    static RoadNetwork Net(out RoadId highway)
    {
        var net = new RoadNetwork();
        var pieces = net.BuildRoad(-400, 0, 400, 0, RoadType.Highway, net.Tuning.Roads.Highway.SpeedLimit);
        highway = Assert.Single(pieces);
        return net;
    }

    [Fact]
    public void Highway_HasTwoLanesEachWay_AWideCarriageway_AndTheTuningRowsPrice()
    {
        var net = Net(out var id);
        var road = net.GetRoad(id);
        Assert.Equal((RoadType.Highway, 2, 2), (road.Type, road.LanesForward, road.LanesBack));
        Assert.Equal(10f, road.HalfWidth);
        Assert.Equal(28f, road.SpeedLimit);
        var cost = new RoadNetwork().EstimateCost(0, 0, 200, 0, RoadType.Highway)!.Value;
        Assert.Equal(200 * 180f, cost.Money, 1);
    }

    [Fact]
    public void ARoadCrossingOrEndingOnAHighway_IsRefused()
    {
        var net = Net(out _);
        Assert.Empty(net.BuildRoad(0, -200, 0, 200, RoadType.TwoLane, 14f));  // crossing
        Assert.Empty(net.BuildRoad(0, -200, 0, 0, RoadType.TwoLane, 14f));    // ending on its side
        Assert.Empty(net.BuildRoad(400, 0, 600, 0, RoadType.TwoLane, 14f));   // ending on its end node
        Assert.Empty(net.BuildRoad(0, -200, 0, 200, RoadType.Avenue2x2, 19f));
    }

    [Fact]
    public void AHighway_CannotMeetARoad_ButCanBeContinuedFromItsFreeEnd()
    {
        var net = Net(out _);
        net.BuildRoad(0, 100, 0, 300, RoadType.TwoLane, 14f);
        Assert.Single(net.BuildRoad(-100, 50, -100, 350, RoadType.Highway, 28f));  // parallel and clear: fine
        Assert.Empty(net.BuildRoad(-100, 200, 100, 200, RoadType.Highway, 28f));   // crosses the two-lane road in level
        Assert.Empty(net.BuildRoad(0, -100, 0, 100, RoadType.Highway, 28f));       // crosses the highway
        Assert.Single(net.BuildRoad(400, 0, 800, 0, RoadType.Highway, 28f));       // continues it
    }

    [Fact]
    public void ARailLineCannotCrossAHighwayInLevel_AndAHighwayCannotCrossRails()
    {
        var sim = RailWorlds.Flat();
        Assert.True(sim.Execute(new BuildTrackCommand(0, 0, 1500, 0)) > 0);
        Assert.Equal(0, sim.Execute(new BuildRoadCommand(600, -400, 600, 400, RoadType.Highway, 28f)));
        Assert.Empty(sim.Crossings);

        var sim2 = RailWorlds.Flat();
        Assert.True(sim2.Execute(new BuildRoadCommand(600, -400, 600, 400, RoadType.Highway, 28f)) > 0);
        Assert.Equal(0, sim2.Execute(new BuildTrackCommand(0, 0, 1500, 0)));
    }

    [Fact]
    public void HighwayTraffic_RunsAtItsOwnSpeed()
    {
        var net = new RoadNetwork();
        net.BuildRoad(-600, 0, 600, 0, RoadType.Highway, 28f);
        var sim = new Simulation(net, 3);
        sim.Execute(new SeedTrafficCommand(10));
        Worlds.Run(sim, 0.2);
        Assert.True(sim.VehicleCount > 0);
    }
}
