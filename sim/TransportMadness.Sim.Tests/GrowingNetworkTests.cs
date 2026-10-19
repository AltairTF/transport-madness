using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class GrowingNetworkTests
{
    [Fact]
    public void RoadBuiltAfterTheSimulationStarted_CanBeDrivenOn()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(200, 0);
        var ab = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        var sim = new Simulation(net, seed: 1);
        sim.Step();

        int c = net.AddNode(200, 200);
        var bc = net.AddRoad(b, c, RoadType.TwoLane, 14f);
        int car = sim.SpawnVehicle(net.GetRoad(bc).ForwardLanes[0], position: 20f, speed: 10f);
        for (int i = 0; i < 20 * 10; i++) sim.Step();

        Assert.NotEqual(LaneId.None, sim.GetVehicle(car).Lane);
        Assert.True(sim.GetVehicle(car).Speed > 5f);
    }

    [Fact]
    public void VehicleHeadingForADeadEnd_TakesAnExtensionBuiltBeforeItArrives()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(400, 0);
        var ab = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        var sim = new Simulation(net, seed: 1);
        int car = sim.SpawnVehicle(net.GetRoad(ab).ForwardLanes[0], position: 10f, speed: 12f);
        sim.SetKeepLane(car, true);
        for (int i = 0; i < 20 * 5; i++) sim.Step();

        int c = net.AddNode(800, 0);
        var bc = net.AddRoad(b, c, RoadType.TwoLane, 14f);
        // The only turns at b now are the extension and the U-turn; run enough times for the choice to land on the extension.
        bool reachedExtension = false;
        for (int i = 0; i < 20 * 60 && !reachedExtension; i++)
        {
            sim.Step();
            reachedExtension = net.GetLane(sim.GetVehicle(car).Lane).Road == bc;
        }

        Assert.True(reachedExtension, "vehicle never used the new road");
    }

    [Fact]
    public void SeedTraffic_OnAnEmptyNetwork_PlacesNothing()
    {
        var sim = new Simulation(new RoadNetwork(), seed: 1);

        Assert.Equal(0, sim.SeedTraffic(10));
        sim.Step();
    }

    [Fact]
    public void FindRoad_ReturnsTheRoadBetweenTwoNodesInEitherOrder()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0);
        int b = net.AddNode(100, 0);
        int c = net.AddNode(0, 100);
        var ab = net.AddRoad(a, b, RoadType.TwoLane, 14f);

        Assert.Equal(ab, net.FindRoad(a, b));
        Assert.Equal(ab, net.FindRoad(b, a));
        Assert.Null(net.FindRoad(a, c));
    }
}
