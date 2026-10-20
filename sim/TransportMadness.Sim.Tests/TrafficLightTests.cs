using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class TrafficLightTests
{
    /// <summary>A plus-shaped junction with four arms 300 m long on flat ground, and its middle node.</summary>
    static (Simulation Sim, int Node) Cross(ulong seed = 3)
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Create(256, 8f, (_, _) => 5f)), seed);
        sim.Execute(new BuildRoadCommand(-300, 0, 300, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(0, -300, 0, 300, RoadType.TwoLane, 14f));
        int node = sim.Network.FindNodeNear(0, 0, 1f);
        return (sim, node);
    }

    [Fact]
    public void ALightGoesAtAJunctionOfThreeRoadsOrMore_OnceAndForMoney()
    {
        var (sim, node) = Cross();
        long before = sim.Company.Money;

        Assert.Equal(1, sim.Execute(new BuildTrafficLightCommand(node)));

        Assert.Equal(before - sim.TrafficLightPrice, sim.Company.Money);
        Assert.Equal(0, sim.Execute(new BuildTrafficLightCommand(node)));                       // one to a junction
        Assert.Equal(0, sim.Execute(new BuildTrafficLightCommand(sim.Network.FindNodeNear(300, 0, 1f))));  // the end of a road is no junction
        Assert.NotNull(sim.LightAt(node));
        Assert.Equal(1, sim.Execute(new RemoveTrafficLightCommand(node)));
        Assert.Null(sim.LightAt(node));
        Assert.Equal(0, sim.Execute(new RemoveTrafficLightCommand(node)));
    }

    [Fact]
    public void TheTwoAxesTakeTurns_WithAShortTimeOfRedForBoth()
    {
        var (sim, node) = Cross();
        sim.Execute(new BuildTrafficLightCommand(node));
        var lanes = sim.Network.IncomingLanes(node);
        var ew = lanes.Where(l => MathF.Abs(MathF.Cos(sim.Network.GetLane(l).AngleAtEnd)) > 0.9f).ToList();
        var ns = lanes.Where(l => MathF.Abs(MathF.Sin(sim.Network.GetLane(l).AngleAtEnd)) > 0.9f).ToList();
        Assert.Equal(2, ew.Count);
        Assert.Equal(2, ns.Count);
        int onlyEw = 0, onlyNs = 0, both = 0, neither = 0;

        for (int i = 0; i < 4000; i++)
        {
            sim.Step();
            bool e = ew.All(l => sim.LaneHasGreen(l)), n = ns.All(l => sim.LaneHasGreen(l));
            Assert.True(ew.All(l => sim.LaneHasGreen(l) == sim.LaneHasGreen(ew[0])), "opposite approaches go together");
            if (e && n) both++; else if (e) onlyEw++; else if (n) onlyNs++; else neither++;
        }

        Assert.Equal(0, both);
        Assert.True(onlyEw > 800 && onlyNs > 800, $"each axis got its green: {onlyEw} and {onlyNs}");
        Assert.True(neither > 100, "both are red for a moment between");
    }

    [Fact]
    public void ACarArrivingOnRed_StopsAtTheEdge_AndGoesOnAtGreen()
    {
        var (sim, node) = Cross();
        sim.Execute(new BuildTrafficLightCommand(node));
        var lane = sim.Network.IncomingLanes(node).First(l => MathF.Abs(MathF.Cos(sim.Network.GetLane(l).AngleAtEnd)) > 0.9f);
        // Wait for red on that axis, then put a car on its way.
        while (sim.LaneHasGreen(lane)) sim.Step();
        int car = sim.SpawnVehicle(lane, sim.Network.GetLane(lane).Length - 120f, 12f);
        sim.SetKeepLane(car, true);
        float edge = sim.Network.GetLane(lane).Length - RoadNetwork.JunctionReach(sim.Network.GetLane(lane).Length);
        float stoppedAt = -1f;
        bool crossed = false;

        for (int i = 0; i < 2400; i++)
        {
            sim.Step();
            var s = sim.GetVehicle(car);
            if (s.Lane == lane && s.Speed < 0.1f && !sim.LaneHasGreen(lane)) stoppedAt = s.Position;
            if (s.Lane != lane) { crossed = true; break; }
        }

        Assert.True(stoppedAt > 0f && stoppedAt <= edge, $"it stood at {stoppedAt}, the edge being at {edge}");
        Assert.True(crossed, "it went on when its light turned green");
    }

    [Fact]
    public void BusyJunction_HasNoCollisionsInsideItWithLights_AndTheCarsKeepMoving()
    {
        var (sim, node) = Cross(9);
        sim.Execute(new BuildTrafficLightCommand(node));
        for (int k = 0; k < 4; k++)
        {
            foreach (var lane in sim.Network.IncomingLanes(node))
            {
                int car = sim.SpawnVehicle(lane, sim.Network.GetLane(lane).Length - 30f - 22f * k, 10f);
                sim.SetKeepLane(car, true);
            }
        }
        double travelled = 0;

        for (int i = 0; i < 24000; i++)
        {
            sim.Step();
            if (i % 20 == 0)
                for (int v = 0; v < sim.VehicleCount; v++)
                    if (sim.GetVehicle(v).Kind == VehicleKind.Car) travelled += sim.GetVehicle(v).Speed;
        }

        Assert.DoesNotContain(sim.Incidents, inc => inc.Kind == IncidentKind.Junction);
        Assert.True(travelled > 1000, "the cars kept going");
    }

    [Fact]
    public void LightsCostUpkeepEveryDay_AndTheRunReplays()
    {
        var (sim, node) = Cross(5);
        sim.Execute(new BuildTrafficLightCommand(node));
        sim.Execute(new SeedTrafficCommand(20));
        long upkeep = sim.Company.Total(LedgerEntry.Upkeep);
        RailWorlds.Run(sim, 600);

        Assert.Equal(upkeep - 120, sim.Company.Total(LedgerEntry.Upkeep));

        var (fresh, _) = Cross(5);
        fresh.Replay(sim.CommandLog, sim.Tick);
        Assert.Equal(sim.StateHash(), fresh.StateHash());
    }
}
