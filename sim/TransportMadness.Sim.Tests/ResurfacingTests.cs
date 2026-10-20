using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>Resurfacing: a crew restores a road, for a price per metre, and the road is closed like a wreck while it works.</summary>
public class ResurfacingTests
{
    static Simulation WornRoad(float length = 400f, ulong seed = 1)
    {
        var tuning = Tuned(m => m with { Roads = m.Roads with { PassesPerPoint = 0.01f } });
        var net = new RoadNetwork(Terrain.Flat(), tuning);
        var sim = new Simulation(net, seed, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(0, 0, length, 0, RoadType.TwoLane, 14f));
        int car = sim.SpawnVehicle(new LaneId(0), 5f, 14f);
        sim.SetKeepLane(car, true);
        for (int i = 0; i < 20 * 12; i++) sim.Step();
        sim.RemoveVehicleForTest(car);
        return sim;
    }

    [Fact]
    public void ResurfacingCostsAPricePerMetre_ClosesTheRoadWhileItTakes_AndLeavesItAsGoodAsNew()
    {
        var sim = WornRoad();
        float length = sim.Network.GetRoad(new RoadId(0)).Length;
        long money = sim.Company.Money;
        Assert.True(sim.RoadConditionOf(0) < 99f);
        long price = (long)Math.Ceiling(length * sim.MaintenanceRules.Roads.ResurfacePricePerMetre);

        Assert.Equal(price, sim.ResurfacePrice(0));
        Assert.Null(sim.WhyNotResurface(0));
        Assert.Equal(1, sim.Execute(new ResurfaceRoadCommand(0)));

        Assert.Equal(money - price, sim.Company.Money);
        Assert.Equal(-price, sim.Company.Total(LedgerEntry.Maintenance));
        var work = Assert.Single(sim.RoadWorks);
        Assert.Equal(0, work.Road);
        Assert.Contains("resurfaced", sim.WhyNotResurface(0));
        Assert.Equal(0, sim.Execute(new ResurfaceRoadCommand(0)));
        int debris = Enumerable.Range(0, sim.VehicleCount).Count(v => sim.GetVehicle(v).Kind == VehicleKind.Debris);
        Assert.Equal(2, debris); // one in each lane
        Assert.True(sim.RoadConditionOf(0) < 99f, "the road is not repaired until the work is done");

        float seconds = sim.ResurfaceSeconds(0);
        Run(sim, seconds * 0.5);
        Assert.Single(sim.RoadWorks);
        Run(sim, seconds * 0.5 + 1);

        Assert.Empty(sim.RoadWorks);
        Assert.Equal(100f, sim.RoadConditionOf(0));
        Assert.Equal(0, Enumerable.Range(0, sim.VehicleCount).Count(v => sim.GetVehicle(v).Kind == VehicleKind.Debris));
    }

    [Fact]
    public void ARoadWorkBlocksTheLane_LikeAWreck_SoTrafficQueuesBehindIt()
    {
        var sim = WornRoad(length: 800f);
        sim.Execute(new ResurfaceRoadCommand(0));
        int car = sim.SpawnVehicle(new LaneId(0), 20f, 12f);
        sim.SetKeepLane(car, true);

        Run(sim, 40);

        var v = sim.GetVehicle(car);
        Assert.True(v.Speed < 0.5f, "it should be held up behind the works");
        Assert.True(v.Position < 400f, "it drove through the works");
    }

    [Fact]
    public void ResurfacingIsRefused_WithAReason_ForNoRoadForAGoodRoadOrNoMoney()
    {
        var fresh = new Simulation(new RoadNetwork(Terrain.Flat()), 1, IncidentFrequency.Off);
        fresh.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        Assert.Contains("needs no work", fresh.WhyNotResurface(0));
        Assert.Contains("does not exist", fresh.WhyNotResurface(9));
        Assert.Equal(0, fresh.Execute(new ResurfaceRoadCommand(0)));
        Assert.Equal(0, fresh.Execute(new ResurfaceRoadCommand(9)));

        var tuning = Tuned(m => m with { Roads = m.Roads with { PassesPerPoint = 0.01f, ResurfacePricePerMetre = 1000000f } });
        var net = new RoadNetwork(Terrain.Flat(), tuning);
        var poor = new Simulation(net, 1, IncidentFrequency.Off);
        poor.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        int car = poor.SpawnVehicle(new LaneId(0), 5f, 14f);
        poor.SetKeepLane(car, true);
        for (int i = 0; i < 20 * 12; i++) poor.Step();
        Assert.Contains("money", poor.WhyNotResurface(0));
    }

    [Fact]
    public void ARoadWorkOnARoadThatIsTornDown_IsHarmless()
    {
        var sim = WornRoad();
        sim.Execute(new ResurfaceRoadCommand(0));
        Assert.Equal(1, sim.Execute(new RemoveRoadCommand(0)));

        Run(sim, sim.ResurfaceSeconds(0) + 5);

        Assert.Empty(sim.RoadWorks);
    }
}
