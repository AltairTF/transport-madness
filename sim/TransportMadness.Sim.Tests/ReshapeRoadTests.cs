using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class ReshapeRoadTests
{
    static (RoadNetwork Net, Simulation Sim, RoadId Avenue) AvenueBetweenTwoJunctions()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(400, 0);
        var avenue = net.AddRoad(a, b, RoadType.Avenue2x2, 19f);
        return (net, new Simulation(net, 5), avenue);
    }

    [Fact]
    public void Downgrade_ReplacesAnAvenueByATwoLaneRoadOnTheSameNodes()
    {
        var (net, _, avenue) = AvenueBetweenTwoJunctions();
        var road = net.GetRoad(avenue);

        var narrowed = net.DowngradeRoad(avenue, 14f);

        Assert.NotNull(narrowed);
        Assert.True(road.Retired);
        var now = net.GetRoad(narrowed!.Value);
        Assert.Equal(RoadType.TwoLane, now.Type);
        Assert.Equal((road.From, road.To), (now.From, now.To));
        Assert.Equal(14f, now.SpeedLimit);
        Assert.Single(now.ForwardLanes);
        Assert.False(net.GetLane(now.ForwardLanes[0]).Oncoming.IsNone);
    }

    [Fact]
    public void Downgrade_RefusesTwoLaneRoadsRetiredRoadsAndBadLimits()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(300, 0), c = net.AddNode(600, 0);
        var twoLane = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        var avenue = net.AddRoad(b, c, RoadType.Avenue2x2, 19f);

        Assert.Null(net.DowngradeRoad(twoLane, 14f));
        Assert.Null(net.DowngradeRoad(avenue, 0f));
        Assert.Null(net.DowngradeRoad(avenue, float.NaN));
        Assert.False(net.GetRoad(avenue).Retired); // a refused limit must not leave the road half replaced
        Assert.NotNull(net.DowngradeRoad(avenue, 14f));
        Assert.Null(net.DowngradeRoad(avenue, 14f)); // already replaced
        Assert.Null(net.DowngradeCost(avenue));
    }

    [Fact]
    public void Downgrade_CostsByLength()
    {
        var (net, _, avenue) = AvenueBetweenTwoJunctions();

        Assert.Equal(400f * net.Tuning.Roads.DowngradeCostPerMetre, net.DowngradeCost(avenue));
    }

    [Fact]
    public void Downgrade_KeepsTrafficFlowing_WithoutVehiclesOnTopOfEachOther()
    {
        var (net, sim, avenue) = AvenueBetweenTwoJunctions();
        sim.SeedTraffic(40);
        for (int i = 0; i < 400; i++) sim.Step();
        int before = Alive(sim);

        net.DowngradeRoad(avenue, 14f);
        sim.Step();

        Assert.InRange(Alive(sim), 1, before);
        AssertNoOverlap(sim);
        for (int i = 0; i < 1200; i++) sim.Step();
        AssertNoOverlap(sim);
        Assert.Empty(sim.Incidents.Where(i => i.Kind == IncidentKind.Sideswipe)); // moving lanes must not read as a crash
    }

    [Fact]
    public void Downgrade_RemovesALeftLaneVehicleThatHasNoRoom()
    {
        var (net, sim, avenue) = AvenueBetweenTwoJunctions();
        var lanes = net.GetRoad(avenue).ForwardLanes;
        int right = sim.SpawnVehicle(lanes[0], 200f, 5f);
        int left = sim.SpawnVehicle(lanes[1], 201f, 5f); // would land 1 m from the other
        int clear = sim.SpawnVehicle(lanes[1], 300f, 5f);

        net.DowngradeRoad(avenue, 14f);
        sim.Step();

        Assert.NotEqual(VehicleKind.Removed, sim.GetVehicle(right).Kind);
        Assert.Equal(VehicleKind.Removed, sim.GetVehicle(left).Kind);
        Assert.NotEqual(VehicleKind.Removed, sim.GetVehicle(clear).Kind);
        Assert.False(net.GetLane(sim.GetVehicle(clear).Lane).Retired);
    }

    [Fact]
    public void SetSpeedLimit_ReplacesTheRoadAndLanesCarryTheNewLimit()
    {
        var (net, sim, avenue) = AvenueBetweenTwoJunctions();
        int car = sim.SpawnVehicle(net.GetRoad(avenue).ForwardLanes[1], 100f, 10f);

        var changed = net.SetSpeedLimit(avenue, 9f);
        sim.Step();

        Assert.NotNull(changed);
        var road = net.GetRoad(changed!.Value);
        Assert.Equal(RoadType.Avenue2x2, road.Type);
        Assert.Equal(9f, road.SpeedLimit);
        Assert.All(road.ForwardLanes.Concat(road.BackwardLanes), l => Assert.Equal(9f, net.GetLane(l).SpeedLimit));
        Assert.Contains(sim.GetVehicle(car).Lane, road.ForwardLanes); // it carries on in the new road, not a retired lane
    }

    [Fact]
    public void SetSpeedLimit_RefusesAnUnchangedBadOrRetiredRoad()
    {
        var (net, _, avenue) = AvenueBetweenTwoJunctions();

        Assert.Null(net.SetSpeedLimit(avenue, 19f));
        Assert.Null(net.SetSpeedLimit(avenue, -3f));
        Assert.Null(net.SetSpeedLimit(avenue, float.PositiveInfinity));
        var changed = net.SetSpeedLimit(avenue, 12f);
        Assert.Null(net.SetSpeedLimit(avenue, 8f)); // the old id is gone
        Assert.Null(net.SpeedLimitChangeCost(avenue));
        Assert.Equal(400f * net.Tuning.Roads.SpeedLimitChangeCostPerMetre, net.SpeedLimitChangeCost(changed!.Value));
    }

    [Fact]
    public void Commands_AreLoggedAndRefusedOnesReturnZero()
    {
        var (net, sim, avenue) = AvenueBetweenTwoJunctions();

        Assert.Equal(1, sim.Execute(new SetSpeedLimitCommand(avenue.Value, 12f)));
        Assert.Equal(0, sim.Execute(new DowngradeRoadCommand(avenue.Value, 14f))); // that road id was replaced
        int current = net.RoadCount - 1;
        Assert.Equal(1, sim.Execute(new DowngradeRoadCommand(current, 14f)));
        Assert.Equal(3, sim.CommandLog.Count);
    }

    static int Alive(Simulation sim) => Enumerable.Range(0, sim.VehicleCount).Count(i => sim.GetVehicle(i).Kind != VehicleKind.Removed);

    static void AssertNoOverlap(Simulation sim)
    {
        for (int i = 0; i < sim.VehicleCount; i++)
            for (int j = i + 1; j < sim.VehicleCount; j++)
            {
                var a = sim.GetVehicle(i);
                var b = sim.GetVehicle(j);
                if (a.Kind != VehicleKind.Car || b.Kind != VehicleKind.Car || a.Lane != b.Lane) continue; // wrecks from a crash overlap by design
                if (a.Lateral > 0f || b.Lateral > 0f) continue;
                Assert.True(MathF.Abs(a.Position - b.Position) >= Simulation.VehicleLength * 0.9f, $"vehicles {i} and {j} overlap: lane {a.Lane.Value} pos {a.Position} / {b.Position}, kinds {a.Kind} {b.Kind}, tick {sim.Tick}");
            }
    }
}

public class DepotClosureTests
{
    [Fact]
    public void ADepotWhoseRoadsAreAllTornDown_Closes_AndSendsNoMoreCrews()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(300, 0), c = net.AddNode(600, 0);
        var ab = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        var bc = net.AddRoad(b, c, RoadType.TwoLane, 14f);
        var sim = new Simulation(net, 2);
        sim.Execute(new AddDepotCommand(a, DepotLevel.Simple));
        sim.Execute(new AddDepotCommand(c, DepotLevel.Simple));

        sim.Execute(new RemoveRoadCommand(ab.Value));
        sim.Step();

        Assert.False(sim.Depots[0].Active);
        Assert.True(sim.Depots[1].Active);
        Assert.Equal(-1, sim.Execute(new AddDepotCommand(a, DepotLevel.Workshop))); // nothing to serve from a node without roads
        net.AddRoad(a, c, RoadType.TwoLane, 14f);
        Assert.True(sim.Execute(new AddDepotCommand(a, DepotLevel.Workshop)) >= 0); // the closed depot no longer blocks the node
    }

    [Fact]
    public void TheCrewOfAClosedDepot_IsLost_AndTheIncidentFallsBackToTheDistantService()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(2000, 0), c = net.AddNode(2300, 0);
        var far = net.AddRoad(a, b, RoadType.TwoLane, 14f);
        net.AddRoad(b, c, RoadType.TwoLane, 14f);
        var sim = new Simulation(net, 4);
        sim.Execute(new AddDepotCommand(a, DepotLevel.Simple));
        int car = sim.SpawnVehicle(net.GetRoad(new RoadId(1)).ForwardLanes[0], 100f, 8f);
        sim.Step();
        sim.Crash(car);
        for (int i = 0; i < 40 && sim.Incidents[0].State != IncidentState.CrewEnRoute; i++) sim.Step();
        Assert.Equal(0, sim.Incidents[0].Depot);
        Assert.Equal(1, sim.Depots[0].BusyCrews);

        net.RemoveRoad(far);
        sim.Step();

        Assert.False(sim.Depots[0].Active);
        Assert.Equal(0, sim.Depots[0].BusyCrews);
        Assert.Equal(-1, sim.Incidents[0].Depot);
        for (int i = 0; i < 20 * 700 && sim.OpenIncidentIds.Count > 0; i++) sim.Step();
        Assert.Empty(sim.OpenIncidentIds);
        Assert.Equal(0, sim.Depots[0].BusyCrews); // clearing the incident must not take the count below zero
    }
}
