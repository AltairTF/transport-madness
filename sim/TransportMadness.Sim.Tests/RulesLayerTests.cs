using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Rules the game layer used to carry itself: where a depot or station can go, which depot sells a vehicle, how a new map is made.</summary>
public class RulesLayerTests
{
    /// <summary>A straight road 1200 m long with a depot at its west end and stations at x = 150 and x = 1050.</summary>
    static (Simulation Sim, int DepotWest, int A, int B) Road()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 5);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f)));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(150, 0));
        int b = sim.Execute(new BuildStationCommand(1050, 0));
        Assert.True(depot >= 0 && a >= 0 && b >= 0);
        sim.Execute(new TakeLoanCommand(1000000));
        return (sim, depot, a, b);
    }

    [Fact]
    public void ADepotGoesOnTheNearestNodeWithinTheSnapRadius_AndNotWhereOneStands()
    {
        var (sim, _, _, _) = Road();
        float radius = sim.Network.Tuning.Crews.DepotSnapRadius;

        int near = sim.FindDepotNode(radius * 0.5f, 5f);
        Assert.Equal(sim.Network.FindNodeNear(0, 0, 1f), near);          // the west end is the nearest node
        Assert.Equal(-1, sim.FindDepotNode(600f, radius + 100f));        // nothing within reach
        Assert.Equal(-1, sim.Execute(new AddDepotCommand(near, DepotLevel.Simple))); // it has a depot already
        int east = sim.FindDepotNode(1200f - 10f, 0f);
        Assert.True(sim.Execute(new AddDepotCommand(east, DepotLevel.Simple)) >= 0);
    }

    [Fact]
    public void AStationQuote_SaysWhereItStands_WhatItCosts_AndWhetherTheCompanyCanPay()
    {
        var (sim, _, _, _) = Road();

        var quote = sim.QuoteStation(600f, 0f, bays: true)!.Value;
        var plain = sim.QuoteStation(600f, 0f, bays: false)!.Value;

        Assert.Equal(sim.StationPrice + sim.StationBayPrice, quote.Price);
        Assert.Equal(sim.StationPrice, plain.Price);
        Assert.True(quote.Affordable);
        Assert.Equal(sim.Network.Tuning.Stations.CoverageRadius, quote.Radius);
        Assert.Equal(0, quote.Buildings);
        Assert.Null(sim.QuoteStation(150f, 0f, bays: false)); // a station stands there already
        Assert.Null(sim.QuoteStation(600f, 900f, bays: false)); // nothing to build on

        sim.Company.Money = 0;
        Assert.False(sim.QuoteStation(600f, 0f, bays: false)!.Value.Affordable);
    }

    [Fact]
    public void TheNearestStation_IsFoundWithinTheRadius_AndNotWhenClosed()
    {
        var (sim, _, a, b) = Road();

        Assert.Equal(a, sim.NearestStation(200f, 30f, 100f));
        Assert.Equal(b, sim.NearestStation(1000f, 0f, 100f));
        Assert.Equal(-1, sim.NearestStation(600f, 0f, 100f));
        sim.Execute(new CloseStationCommand(a));
        Assert.Equal(-1, sim.NearestStation(200f, 30f, 100f));
    }

    [Fact]
    public void ABuyWithNoDepotNamed_UsesTheNearestOneConnectedToTheLine()
    {
        var (sim, west, a, b) = Road();
        int east = sim.Execute(new AddDepotCommand(sim.FindDepotNode(1195f, 0f), DepotLevel.Simple));
        int lineFromEast = sim.Execute(new CreateLineCommand(new[] { b, a }));
        int lineFromWest = sim.Execute(new CreateLineCommand(new[] { a, b }));

        int fromEast = sim.Execute(new BuyVehicleCommand(BuyVehicleCommand.NearestDepot, "bus", lineFromEast));
        int fromWest = sim.Execute(new BuyVehicleCommand(BuyVehicleCommand.NearestDepot, "bus", lineFromWest));

        Assert.True(fromEast >= 0 && fromWest >= 0);
        int StartNode(int vehicle) => sim.Network.GetLane(sim.GetVehicle(vehicle).Lane).StartNode;
        Assert.Equal(sim.Depots[east].Node, StartNode(fromEast));
        Assert.Equal(sim.Depots[west].Node, StartNode(fromWest));
    }

    [Fact]
    public void ABuyWithNoDepotNamed_IsRefusedOnceWhenNoDepotCanTakeItOrTheCompanyCannotPay()
    {
        var (sim, _, a, b) = Road();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        sim.Company.Money = 0;
        int logged = sim.CommandLog.Count;

        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(BuyVehicleCommand.NearestDepot, "bus", line)));
        Assert.Equal(logged + 1, sim.CommandLog.Count); // one command in the log, not one per depot tried
        Assert.Equal(0, Enumerable.Range(0, sim.VehicleCount).Count(sim.IsCompanyVehicle));
    }

    [Fact]
    public void ANewTownsWorld_HasItsTownsIndustriesAndAmbientCars_AndIsTheSameEveryTime()
    {
        var (sim, plan) = WorldBuilder.Towns(3, 3, IndustryAmount.Normal);
        var (again, _) = WorldBuilder.Towns(3, 3, IndustryAmount.Normal);

        Assert.Equal(plan.Towns.Count, sim.Towns.Count);
        Assert.Equal(plan.Industries!.Count, sim.Industries.Count);
        int wanted = sim.Network.Tuning.Traffic.AmbientPerTown * plan.Towns.Count;
        Assert.InRange(sim.VehicleCount, wanted / 2, wanted); // a few may not find room
        Assert.Single(sim.CommandLog);
        Assert.IsType<SeedTrafficCommand>(sim.CommandLog[0].Command);
        Assert.Equal(sim.StateHash(), again.StateHash());
    }
}
