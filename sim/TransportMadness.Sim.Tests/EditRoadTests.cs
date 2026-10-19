using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class RemoveRoadTests
{
    static (RoadNetwork Net, Simulation Sim, RoadId A, RoadId B) TwoRoadsInARow()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(300, 0), c = net.AddNode(600, 0);
        var ab = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        var bc = net.AddRoad(b, c, RoadType.TwoLane, 14f);
        return (net, new Simulation(net, 3), ab, bc);
    }

    [Fact]
    public void RemovingARoad_RetiresItsLanes_AndTurnsTheNeighbourIntoADeadEnd()
    {
        var (net, _, ab, bc) = TwoRoadsInARow();
        Assert.True(net.RemoveRoad(bc));

        Assert.True(net.GetRoad(bc).Retired);
        foreach (var lane in net.GetRoad(bc).ForwardLanes.Concat(net.GetRoad(bc).BackwardLanes)) Assert.True(net.GetLane(lane).Retired);
        int b = net.GetRoad(ab).To;
        Assert.Single(net.RoadsAt(b));
        // Traffic arriving at the old junction now turns round onto the opposite lane.
        var arriving = net.GetRoad(ab).ForwardLanes[0];
        Assert.Equal(net.GetRoad(ab).BackwardLanes[0], Assert.Single(net.GetLane(arriving).Next));
        Assert.False(net.RemoveRoad(bc));
    }

    [Fact]
    public void VehiclesOnARemovedRoad_AreRemoved_AndOthersCarryOn()
    {
        var (net, sim, ab, bc) = TwoRoadsInARow();
        sim.SeedTraffic(30);
        for (int i = 0; i < 200; i++) sim.Step();

        net.RemoveRoad(bc);
        sim.Step();

        int onRemoved = 0, alive = 0;
        for (int i = 0; i < sim.VehicleCount; i++)
        {
            var v = sim.GetVehicle(i);
            if (v.Kind == VehicleKind.Removed) continue;
            alive++;
            if (net.GetLane(v.Lane).Retired) onRemoved++;
        }
        Assert.Equal(0, onRemoved);
        Assert.True(alive > 0);
        for (int i = 0; i < 600; i++) sim.Step(); // keeps running without trouble
        sim.SeedTraffic(10);
        for (int i = 0; i < sim.VehicleCount; i++)
        {
            var v = sim.GetVehicle(i);
            if (v.Kind != VehicleKind.Removed) Assert.False(net.GetLane(v.Lane).Retired);
        }
    }

    [Fact]
    public void RemovingTheRoadOfAnOpenIncident_LetsTheIncidentFinishWithoutCrashing()
    {
        var (net, sim, ab, bc) = TwoRoadsInARow();
        sim.SeedTraffic(20);
        for (int i = 0; i < 100; i++) sim.Step();
        int victim = -1;
        for (int i = 0; i < sim.VehicleCount && victim < 0; i++)
        {
            var v = sim.GetVehicle(i);
            if (v.Kind == VehicleKind.Car && v.Speed > 2f && v.Lane == net.GetRoad(bc).ForwardLanes[0]) victim = i;
        }
        if (victim < 0) victim = 0;
        sim.Crash(victim);
        sim.Step();
        net.RemoveRoad(bc);
        net.RemoveRoad(ab);
        for (int i = 0; i < 20 * 400; i++) sim.Step();
        Assert.Empty(sim.OpenIncidentIds);
    }
}

public class UpgradeRoadTests
{
    [Fact]
    public void Upgrade_ReplacesATwoLaneRoadByAnAvenueOnTheSameLine()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(200, 0);
        var road = net.AddRoad(a, b, RoadType.TwoLane, 14f, 0.6f);

        var upgraded = net.UpgradeRoad(road, 19f);

        Assert.NotNull(upgraded);
        Assert.True(net.GetRoad(road).Retired);
        var avenue = net.GetRoad(upgraded!.Value);
        Assert.Equal(RoadType.Avenue2x2, avenue.Type);
        Assert.Equal(2, avenue.ForwardLanes.Length);
        Assert.Equal(net.GetRoad(road).Length, avenue.Length, 3);
        Assert.Equal(net.GetRoad(road).Geometry.Sweep, avenue.Geometry.Sweep, 4);
        Assert.False(net.CanUpgrade(upgraded.Value));
        Assert.Null(net.UpgradeRoad(upgraded.Value, 19f));
        Assert.Equal(2, net.RoadsAt(a).Count + net.RoadsAt(b).Count);
    }

    [Fact]
    public void VehiclesCarryOnAfterAnUpgrade_InTheMatchingLane()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(300, 0), c = net.AddNode(600, 0);
        var ab = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        net.AddRoad(b, c, RoadType.TwoLane, 14f);
        var sim = new Simulation(net, 4);
        sim.SeedTraffic(30);
        for (int i = 0; i < 100; i++) sim.Step();
        int before = 0;
        for (int i = 0; i < sim.VehicleCount; i++) if (sim.GetVehicle(i).Kind == VehicleKind.Car) before++;

        var upgraded = net.UpgradeRoad(ab, 19f)!.Value;
        sim.Step();

        int after = 0, onNew = 0;
        for (int i = 0; i < sim.VehicleCount; i++)
        {
            var v = sim.GetVehicle(i);
            if (v.Kind != VehicleKind.Car) continue;
            after++;
            Assert.False(net.GetLane(v.Lane).Retired);
            if (net.GetLane(v.Lane).Road == upgraded) onNew++;
        }
        Assert.Equal(before, after);
        Assert.True(onNew > 0);
        for (int i = 0; i < 2000; i++) sim.Step();
    }

    [Fact]
    public void Upgrade_OnTerrain_CostsAndCarvesTheWiderRoad()
    {
        var terrain = Terrain.Generate(42);
        var net = new RoadNetwork(terrain);
        var built = net.BuildRoad(-300, -300, -100, -300, RoadType.TwoLane, 14f);
        if (built.Count == 0) return;
        var cost = net.EstimateUpgrade(built[0])!.Value;
        Assert.True(cost.Money > 0f);
        int version = terrain.Version;
        net.UpgradeRoad(built[0], 19f);
        Assert.True(terrain.Version >= version);
        Assert.Null(net.EstimateUpgrade(built[0]));
    }

    [Fact]
    public void RoadAt_FindsTheNearestRoadWithinReach()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(200, 0), c = net.AddNode(0, 100), d = net.AddNode(200, 100);
        var low = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        var curved = net.AddRoad(c, d, RoadType.TwoLane, 14f, 0.8f);
        Assert.Equal(low, net.RoadAt(100, 4, 10f));
        var (mx, mz) = net.GetRoad(curved).Geometry.PointAt(net.GetRoad(curved).Length * 0.5f);
        Assert.Equal(curved, net.RoadAt(mx, mz + 2, 10f));
        Assert.Null(net.RoadAt(100, 60, 5f));
    }
}
