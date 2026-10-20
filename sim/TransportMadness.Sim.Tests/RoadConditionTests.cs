using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>Road Condition: wear by traffic, the slowing and the risk it brings, and what happens to it when a road is split.</summary>
public class RoadConditionTests
{
    static Tuning Worn(float passesPerPoint = 20f) => Tuned(m => m with { Roads = m.Roads with { PassesPerPoint = passesPerPoint } });

    [Fact]
    public void ANewRoadIsInPerfectCondition_AndTrafficWearsIt_AmbientCarsIncluded()
    {
        var net = new RoadNetwork(Terrain.Flat(), Worn());
        var sim = new Simulation(net, 1, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        Assert.Equal(100f, sim.RoadConditionOf(0));
        sim.SpawnVehicle(new LaneId(0), 20f, 14f);

        for (int i = 0; i < 20 * 20; i++) sim.Step();

        Assert.True(sim.RoadConditionOf(0) < 100f);
    }

    [Fact]
    public void WearFollowsPassesNotLength_OnePassOverTheWholeRoadIsOneOverPassesPerPoint()
    {
        var net = new RoadNetwork(Terrain.Flat(), Worn(passesPerPoint: 10f));
        var sim = new Simulation(net, 1, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        int car = sim.SpawnVehicle(new LaneId(0), 5f, 14f);
        sim.SetKeepLane(car, true);
        double driven = 0;
        for (int i = 0; i < 20 * 15; i++)
        {
            sim.Step();
            driven += sim.GetVehicle(car).Speed * Simulation.StepSeconds;
        }

        float expected = 100f - (float)(driven / 400.0 / 10.0);
        Assert.Equal(expected, sim.RoadConditionOf(0), 0.05f);
    }

    [Fact]
    public void ARoadAbove80HasNoEffectOnSpeed_BelowItDriversSlowDownToTheWorstShareAtZero()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Flat(), Worn(passesPerPoint: 1f)), 1, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(0, 0, 2000, 0, RoadType.TwoLane, 14f));
        int fresh = sim.SpawnVehicle(new LaneId(0), 10f, 0f);
        sim.SetKeepLane(fresh, true);
        for (int i = 0; i < 20 * 8; i++) sim.Step();
        float freshSpeed = sim.GetVehicle(fresh).Speed;
        Assert.True(sim.RoadConditionOf(0) > 80f, $"the test road wore out too soon ({sim.RoadConditionOf(0)})");

        // Wear the road out by hand-driving: a worn copy of the same road.
        var worn = new Simulation(new RoadNetwork(Terrain.Flat(), Worn(passesPerPoint: 0.0001f)), 1, IncidentFrequency.Off);
        worn.Execute(new BuildRoadCommand(0, 0, 2000, 0, RoadType.TwoLane, 14f));
        int car = worn.SpawnVehicle(new LaneId(0), 10f, 14f);
        worn.SetKeepLane(car, true);
        for (int i = 0; i < 20 * 20; i++) worn.Step();

        Assert.Equal(0f, worn.RoadConditionOf(0));
        float wornSpeed = worn.GetVehicle(car).Speed;
        Assert.True(freshSpeed > 8f);
        Assert.True(wornSpeed <= 14f * 0.7f + 0.5f, $"a worn road should hold drivers to 70 % of the limit ({wornSpeed})");
    }

    [Fact]
    public void ABreakdownIsMoreLikelyOnAWornRoad()
    {
        int Breakdowns(float passesPerPoint)
        {
            int count = 0;
            for (ulong seed = 1; seed <= 10; seed++)
            {
                var tuning = Tuned(m => m with { Roads = m.Roads with { PassesPerPoint = passesPerPoint } });
                var net = new RoadNetwork(Terrain.Flat(), tuning);
                var sim = new Simulation(net, seed, IncidentFrequency.Normal);
                sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f));
                int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Workshop));
                int a = sim.Execute(new BuildStationCommand(150, 0)), b = sim.Execute(new BuildStationCommand(1050, 0));
                int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
                int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
                sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
                sim.SetCondition(bus, 30f);
                Run(sim, 300);
                count += sim.IncidentCount;
            }
            return count;
        }

        int good = Breakdowns(1000000f), bad = Breakdowns(0.001f);

        Assert.True(bad > good, $"good road {good}, worn road {bad}");
    }

    [Fact]
    public void ARoadSplitByAJunction_PassesItsWearOnToBothPieces()
    {
        var net = new RoadNetwork(Terrain.Flat(), Worn(passesPerPoint: 5f));
        var sim = new Simulation(net, 1, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        int car = sim.SpawnVehicle(new LaneId(0), 5f, 14f);
        sim.SetKeepLane(car, true);
        for (int i = 0; i < 20 * 10; i++) sim.Step();
        float before = sim.RoadConditionOf(0);
        Assert.True(before < 100f);
        sim.RemoveVehicleForTest(car);

        sim.Execute(new BuildRoadCommand(200, -200, 200, 0, RoadType.TwoLane, 14f)); // meets the road in its middle and splits it

        var roads = Enumerable.Range(0, net.RoadCount).Where(r => !net.GetRoad(new RoadId(r)).Retired).ToList();
        Assert.Equal(3, roads.Count);
        var halves = roads.Where(r => MathF.Abs(net.GetRoad(new RoadId(r)).Length - 200f) < 1f && net.GetRoad(new RoadId(r)).From != net.GetRoad(new RoadId(r)).To).ToList();
        Assert.True(halves.Count >= 2);
        int fresh = roads.Max();
        Assert.Equal(100f, sim.RoadConditionOf(fresh));
        foreach (int r in halves.Where(r => r != fresh && net.GetRoad(new RoadId(r)).Length > 150f)) Assert.Equal(before, sim.RoadConditionOf(r), 0.01f);
    }

    [Fact]
    public void TheWornRoads_AreListedWorstFirst_AndOnlyThoseBelowTheLevelThatMatters()
    {
        var net = new RoadNetwork(Terrain.Flat(), Worn(passesPerPoint: 0.01f));
        var sim = new Simulation(net, 1, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(0, 300, 400, 300, RoadType.TwoLane, 14f));
        int car = sim.SpawnVehicle(new LaneId(0), 5f, 14f);
        sim.SetKeepLane(car, true);
        Assert.Empty(sim.WornRoads(5));

        for (int i = 0; i < 20 * 30; i++) sim.Step();

        var worn = sim.WornRoads(5);
        Assert.Single(worn); // only the road the car drove on
        Assert.Equal(0, worn[0].Road);
        Assert.True(worn[0].Condition < 80f);
        Assert.Empty(sim.WornRoads(0));
    }
}
