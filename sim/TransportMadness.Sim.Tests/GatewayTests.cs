using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The gateways of the campaign (C3): where they stand, how imports arrive and how exports leave.</summary>
public class GatewayTests
{
    const int Passengers = 0, Steel = 10, Oil = 12, Coal = 8;

    static Terrain DryLevelMap() => Terrain.Create(512, 8f, (_, _) => 5f);

    /// <summary>A level dry map with a road along x from 0 to 1000, a depot at its west end and the given gateways beside it.</summary>
    static (Simulation Sim, int Depot) World(params GatewayPlan[] gateways)
    {
        var net = new RoadNetwork(DryLevelMap());
        var sim = new Simulation(net, 4, IncidentFrequency.Off);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1000, 0, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), Array.Empty<IndustryPlan>(), null, gateways));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        Assert.True(depot >= 0);
        return (sim, depot);
    }

    static GatewayPlan RoadGate(float x) => new(GatewayKind.Road, "Test Road Gate", x, 30, 0);
    static GatewayPlan RailGate(float x) => new(GatewayKind.Rail, "Test Rail Gate", x, 30, 0);

    static int Station(Simulation sim, float x)
    {
        int id = sim.Execute(new BuildStationCommand(x, 0));
        Assert.True(id >= 0, $"no station at {x}");
        return id;
    }

    static void RunSeconds(Simulation sim, double seconds)
    {
        long ticks = (long)Math.Round(seconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    // ---- Where they stand ------------------------------------------------------------------------------------------

    static (RoadNetwork Net, WorldPlan Plan) Make(ulong seed, IndustryAmount amount = IndustryAmount.Normal)
    {
        var net = new RoadNetwork(Terrain.Generate(seed));
        var plan = GatewayGenerator.Generate(net, seed, IndustryGenerator.Generate(net, seed, TownGenerator.Generate(net, seed, 4), amount));
        return (net, plan);
    }

    [Fact]
    public void EveryMapHasOneRoadGatewayAndOneRailGateway_OnMostSeeds()
    {
        int both = 0, maps = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            // An island has no land edge for a gateway, and a peninsula has one edge, so it has the one gateway of its mode at most.
            if (Terrain.CoastOf(seed) is CoastKind.Island or CoastKind.ThreeSides) continue;
            maps++;
            var (_, plan) = Make(seed);
            Assert.NotNull(plan.Gateways);
            Assert.True(plan.Gateways!.Count(g => g.Kind == GatewayKind.Road) <= 1);
            Assert.True(plan.Gateways!.Count(g => g.Kind == GatewayKind.Rail) <= 1);
            if (plan.Gateways!.Count == 2) both++;
        }
        Assert.True(both >= maps * 9 / 10, $"only {both} of {maps} maps have both gateways");
    }

    [Fact]
    public void TheGateways_StandAtTheEdge_OnLevelDryGround_ApartFromEachOther_AndClearOfTownsAndIndustries()
    {
        for (ulong seed = 1; seed <= 25; seed++)
        {
            var (net, plan) = Make(seed);
            var g = net.Tuning.Gateways;
            float half = net.Terrain!.HalfSize;
            foreach (var gate in plan.Gateways!)
            {
                Assert.Equal(half - g.Generation.EdgeInset, MathF.Max(MathF.Abs(gate.X), MathF.Abs(gate.Z)), 0.01f);
                Assert.False(net.Terrain.IsWater(gate.X, gate.Z));
                foreach (var town in plan.Towns)
                    Assert.True(MathF.Sqrt(MathF.Pow(town.X - gate.X, 2) + MathF.Pow(town.Z - gate.Z, 2)) > town.Blocks * net.Tuning.Towns.Generation.BlockLength * 0.5f);
                foreach (var industry in plan.Industries!)
                    Assert.True(MathF.Sqrt(MathF.Pow(industry.X - gate.X, 2) + MathF.Pow(industry.Z - gate.Z, 2)) >= g.Generation.MinSpacing);
            }
            if (plan.Gateways!.Count == 2)
                Assert.True(MathF.Sqrt(MathF.Pow(plan.Gateways[0].X - plan.Gateways[1].X, 2) + MathF.Pow(plan.Gateways[0].Z - plan.Gateways[1].Z, 2)) >= g.Generation.MinSeparation);
        }
    }

    [Fact]
    public void TheGateways_AreNamedForTheirSideAndKind_AndAreTheSameForTheSameSeed()
    {
        var (_, a) = Make(31);
        var (_, b) = Make(31);
        var (_, c) = Make(32);

        Assert.Equal(a.Gateways, b.Gateways);
        Assert.NotEqual(a.Gateways, c.Gateways);
        Assert.All(a.Gateways!, g => Assert.Matches(@"^(North|East|South|West) (Road|Rail) Gate$", g.Name));
        Assert.Equal(a.Gateways!.Count, a.Gateways!.Select(g => g.Name).Distinct().Count());
    }

    [Fact]
    public void AddingGateways_MovesNeitherTheTownsNorTheIndustries()
    {
        var net = new RoadNetwork(Terrain.Generate(9));
        var without = IndustryGenerator.Generate(net, 9, TownGenerator.Generate(net, 9, 4), IndustryAmount.Normal);

        var with = GatewayGenerator.Generate(net, 9, without);

        Assert.Equal(without.Industries, with.Industries);
        Assert.Equal(without.Towns, with.Towns);
        Assert.Equal(without.Unlinked, with.Unlinked);
    }

    [Fact]
    public void ANewTownsWorld_HasGateways()
    {
        var (sim, plan) = WorldBuilder.Towns(1, 3, IndustryAmount.Normal); // not an island: an island has no land edge for a gateway

        Assert.Equal(plan.Gateways!.Count, sim.Gateways.Count);
        Assert.NotEmpty(sim.Gateways);
    }

    // ---- Coverage and acceptance -----------------------------------------------------------------------------------

    [Fact]
    public void AStation_CoversAGatewayOfItsOwnModeOnly_AndAcceptsExportsThere()
    {
        var (sim, _) = World(RoadGate(500), RailGate(500));
        var station = sim.Stations[Station(sim, 500)];

        var covered = sim.GatewaysCoveredBy(station);

        Assert.Equal(new[] { 0 }, covered);
        Assert.True(sim.IsCovered(sim.Gateways[0]));
        Assert.False(sim.IsCovered(sim.Gateways[1]));
        Assert.True(sim.Accepts(station, Steel));
        Assert.False(sim.Accepts(station, Oil)); // oil only comes in
        Assert.False(sim.Accepts(station, Passengers));
    }

    [Fact]
    public void AStationFarFromTheGateway_AcceptsNothing()
    {
        var (sim, _) = World(RoadGate(900));
        var station = sim.Stations[Station(sim, 200)];

        Assert.Empty(sim.GatewaysCoveredBy(station));
        Assert.False(sim.Accepts(station, Steel));
    }

    [Fact]
    public void TheStationQuote_CountsAGatewayItWouldCover()
    {
        var (sim, _) = World(RoadGate(500));

        Assert.Equal(1, sim.QuoteStation(500, 0, false)!.Value.Industries);
        Assert.Equal(0, sim.QuoteStation(100, 0, false)!.Value.Industries);
    }

    // ---- Imports ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AnImport_IsPaidAtOnce_ArrivesAfterTheShipmentTime_AndIsHandedToTheStation()
    {
        var (sim, _) = World(RoadGate(500));
        int station = Station(sim, 500);
        var offer = sim.ImportOffers.Single(o => o.Cargo == "coal");
        long money = sim.Company.Money;

        Assert.Equal(1, sim.Execute(new RequestImportCommand(0, "coal")));

        Assert.Equal(money - (long)offer.Units * offer.PricePerUnit, sim.Company.Money);
        Assert.Equal(-(long)offer.Units * offer.PricePerUnit, sim.Company.Total(LedgerEntry.Imports));
        RunSeconds(sim, sim.GatewayTuning.ShipmentSeconds - 5);
        Assert.Equal(0f, sim.Stations[station].Waiting[Coal]);
        Assert.Equal(0.0, sim.Gateways[0].Imported[Coal]);

        RunSeconds(sim, 10);

        Assert.Equal((double)offer.Units, sim.Gateways[0].Imported[Coal]);
        float maxWaiting = sim.CargoTypes[Coal].MaxWaiting;
        Assert.Equal(MathF.Min(offer.Units, maxWaiting), sim.Stations[station].Waiting[Coal], 0.5f);
        Assert.Equal(offer.Units, sim.Stations[station].Waiting[Coal] + sim.Gateways[0].Stock[Coal], 0.5f); // nothing lost
    }

    [Fact]
    public void WithoutAStation_TheShipmentWaitsAtTheGateway()
    {
        var (sim, _) = World(RoadGate(500));
        var offer = sim.ImportOffers.Single(o => o.Cargo == "coal");
        sim.Execute(new RequestImportCommand(0, "coal"));

        RunSeconds(sim, sim.GatewayTuning.ShipmentSeconds + 10);

        Assert.Equal(offer.Units, sim.Gateways[0].Stock[Coal], 0.01f);
    }

    [Fact]
    public void AnArrivalIsOnTheEventFeed()
    {
        var (sim, _) = World(RoadGate(500));
        sim.Execute(new RequestImportCommand(0, "coal"));

        RunSeconds(sim, sim.GatewayTuning.ShipmentSeconds + 10);

        var arrival = Assert.Single(sim.Events, e => e.Kind == SimEventKind.ShipmentArrived);
        Assert.Contains("coal", arrival.Text);
        Assert.Contains("Test Road Gate", arrival.Text);
    }

    [Fact]
    public void ARailStation_CoversTheRailGateway_AndGetsItsStock_ButNotTheRoadGatewaysAtTheSameSpot()
    {
        var sim = RailWorlds.Flat();
        Assert.True(sim.Execute(new BuildTrackCommand(0, 0, 600, 0)) > 0);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 100, 600, 100, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), Array.Empty<IndustryPlan>(), null, new[] { RoadGate(300), RailGate(300) }));
        int rail = sim.Execute(new BuildRailStationCommand(300, 0));
        Assert.True(rail >= 0);

        Assert.Equal(new[] { 1 }, sim.GatewaysCoveredBy(sim.Stations[rail]));
        Assert.Equal(1, sim.Execute(new RequestImportCommand(1, "coal")));
        Assert.Equal(1, sim.Execute(new RequestImportCommand(0, "coal")));
        RailWorlds.Run(sim, sim.GatewayTuning.ShipmentSeconds + 10);

        Assert.True(sim.Stations[rail].Waiting[Coal] > 0f);
        Assert.True(sim.Gateways[0].Stock[Coal] > 0f, "the road gateway's stock stays where it is");
        Assert.Equal(0f, sim.Gateways[1].Stock[Coal] + sim.Stations[rail].Waiting[Coal] - sim.GatewayTuning.Imports.Single(o => o.Cargo == "coal").Units, 0.5f);
    }

    [Fact]
    public void AnImportIsRefused_WhileOneIsOnItsWay_ThenUntilTheIntervalHasPassed_ThenAllowedAgain()
    {
        var (sim, _) = World(RoadGate(500));
        Station(sim, 500);

        Assert.Equal(1, sim.Execute(new RequestImportCommand(0, "coal")));
        Assert.Equal("A shipment is already on its way", sim.WhyNotImport(0, "coal"));
        Assert.Equal(0, sim.Execute(new RequestImportCommand(0, "coal")));
        Assert.Null(sim.WhyNotImport(0, "ore")); // another cargo is free

        RunSeconds(sim, sim.GatewayTuning.ShipmentSeconds + 10);
        Assert.Equal("Asked for too recently", sim.WhyNotImport(0, "coal"));

        RunSeconds(sim, sim.GatewayTuning.IntervalSeconds);
        // Allowed in time, but the first shipment still sits there if nobody took it: the room check may be what stops it now.
        string? why = sim.WhyNotImport(0, "coal");
        Assert.True(why == null || why == "The gateway has no room for it", why);
    }

    [Fact]
    public void AnImportIsRefused_ForACargoNotOnOffer_AnUnknownGateway_NoRoom_OrNoMoney()
    {
        var (sim, _) = World(RoadGate(500));

        Assert.Equal("The government does not send that", sim.WhyNotImport(0, "steel"));
        Assert.Equal("The government does not send that", sim.WhyNotImport(0, "gold"));
        Assert.Equal("No such gateway", sim.WhyNotImport(3, "coal"));
        Assert.Equal(0, sim.Execute(new RequestImportCommand(0, "steel")));

        sim.Gateways[0].Stock[Coal] = sim.GatewayTuning.StockLimit - 10f;
        Assert.Equal("The gateway has no room for it", sim.WhyNotImport(0, "coal"));

        var poor = new Simulation(new RoadNetwork(DryLevelMap(), Tuning.Load() with { Economy = Tuning.Load().Economy with { StartingMoney = 100 } }), 1, IncidentFrequency.Off);
        poor.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), Array.Empty<IndustryPlan>(), null, new[] { RoadGate(500) }));
        Assert.Equal("Not enough money", poor.WhyNotImport(0, "coal"));
        Assert.Equal(0, poor.Execute(new RequestImportCommand(0, "coal")));
        Assert.Equal(100, poor.Company.Money);
    }

    [Fact]
    public void ImportingCostsMoreThanTheCargoPaysForAShortHaul()
    {
        var t = Tuning.Load();
        foreach (var offer in t.Gateways.Imports)
        {
            var cargo = t.Cargo.Single(c => c.Id == offer.Cargo);
            Assert.True(offer.PricePerUnit > cargo.PayPerUnitKm * 2f, $"{offer.Cargo} costs {offer.PricePerUnit} and pays {cargo.PayPerUnitKm} a km");
        }
    }

    // ---- Exports ---------------------------------------------------------------------------------------------------

    [Fact]
    public void WhatATruckUnloadsAtAGateway_IsExported_AndPaid()
    {
        var (sim, depot) = World(RoadGate(700));
        int a = Station(sim, 300), b = Station(sim, 700);
        sim.Stations[a].Waiting[Steel] = 40f;
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.True(sim.Execute(new BuyVehicleCommand(depot, "truck", line)) >= 0);

        RunSeconds(sim, 600);

        Assert.True(sim.Gateways[0].Exported[Steel] >= 20.0, $"exported {sim.Gateways[0].Exported[Steel]}");
        Assert.Equal(sim.Lines[line].Delivered, (long)Math.Round(sim.Gateways[0].Exported[Steel]));
        Assert.True(sim.Lines[line].Income > 0);
    }

    [Fact]
    public void ImportedStock_CanBeHauledInlandByTruck_ToAnIndustryThatUsesIt()
    {
        var net = new RoadNetwork(DryLevelMap());
        var sim = new Simulation(net, 4, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(0, 0, 1400, 0, RoadType.TwoLane, 14f));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), new[] { new IndustryPlan(9, 1100, 30, 0) }, null, new[] { RoadGate(300) }));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int gate = Station(sim, 300), mill = Station(sim, 1100);
        int line = sim.Execute(new CreateLineCommand(new[] { gate, mill }));
        sim.Execute(new BuyVehicleCommand(depot, "truck", line));
        sim.Execute(new RequestImportCommand(0, "coal"));

        RunSeconds(sim, sim.GatewayTuning.ShipmentSeconds + 900);

        Assert.True(sim.Industries[0].Input[Coal] > 0f, "the steel mill received coal from the gateway");
        Assert.True(sim.Lines[line].Delivered > 0);
    }

    // ---- State -----------------------------------------------------------------------------------------------------

    [Fact]
    public void GatewayState_IsPartOfTheHash_AndTwoRunsAgree()
    {
        ulong Run(bool import)
        {
            var (sim, _) = World(RoadGate(500));
            Station(sim, 500);
            if (import) sim.Execute(new RequestImportCommand(0, "coal"));
            RunSeconds(sim, sim.GatewayTuning.ShipmentSeconds + 60);
            return sim.StateHash();
        }

        Assert.Equal(Run(true), Run(true));
        Assert.NotEqual(Run(true), Run(false));
    }
}
