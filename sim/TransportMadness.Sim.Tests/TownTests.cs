using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Towns: generation, road access and abandonment, demolition for roads, and what stations collect.</summary>
public class TownTests
{
    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static WorldPlan Generate(RoadNetwork net, ulong seed, int towns) => TownGenerator.Generate(net, seed, towns);

    /// <summary>A flat map, a 400 m road along x with a town of three hand-placed buildings beside it.</summary>
    static (Simulation Sim, int Home, int Shop, int Far) SmallTown(Tuning? tuning = null)
    {
        var net = new RoadNetwork(Terrain.Flat(), tuning);
        var sim = new Simulation(net, 3);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f)));
        var plan = new WorldPlan(1, new[]
        {
            new TownPlan("Testville", 200, 0, 0, 1, new[]
            {
                new BuildingPlan(BuildingType.Home, 0, 100, 25, 0),
                new BuildingPlan(BuildingType.Commerce, 1, 300, -25, 0),
                new BuildingPlan(BuildingType.Home, 0, 200, 600, 0), // no road near it
            }),
        });
        sim.AddTowns(plan);
        return (sim, 0, 1, 2);
    }

    // ---- Generation ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheSameSeed_GivesTheSameTowns()
    {
        WorldPlan Make(ulong seed)
        {
            var net = new RoadNetwork(Terrain.Generate(seed));
            return Generate(net, seed, 4);
        }

        var a = Make(11);
        var b = Make(11);
        var c = Make(12);

        Assert.Equal(a.Towns.Select(t => (t.Name, t.X, t.Z, t.Buildings.Count)), b.Towns.Select(t => (t.Name, t.X, t.Z, t.Buildings.Count)));
        Assert.Equal(a.Towns.SelectMany(t => t.Buildings), b.Towns.SelectMany(t => t.Buildings));
        Assert.NotEqual(a.Towns.Select(t => (t.X, t.Z)), c.Towns.Select(t => (t.X, t.Z)));
    }

    [Fact]
    public void Towns_StandOnFlatDryGround_FarApart_WithABuildingOnARoadEach()
    {
        var terrain = Terrain.Generate(5);
        var net = new RoadNetwork(terrain);
        var tuning = net.Tuning.Towns;

        var plan = Generate(net, 5, 5);

        Assert.True(plan.Towns.Count >= 2, $"only {plan.Towns.Count} towns fit");
        Assert.Equal(5, plan.Requested);
        for (int i = 0; i < plan.Towns.Count; i++)
        {
            var t = plan.Towns[i];
            Assert.False(terrain.IsWater(t.X, t.Z));
            Assert.True(t.Buildings.Count >= 20, $"{t.Name} has {t.Buildings.Count} buildings");
            for (int j = i + 1; j < plan.Towns.Count; j++)
            {
                float d = MathF.Sqrt(MathF.Pow(t.X - plan.Towns[j].X, 2) + MathF.Pow(t.Z - plan.Towns[j].Z, 2));
                Assert.True(d >= tuning.Generation.MinSpacing, $"{t.Name} and {plan.Towns[j].Name} are {d} m apart");
            }
        }
        Assert.Equal(plan.Towns.Count, plan.Towns.Select(t => t.Name).Distinct().Count());

        var index = new RoadIndex(net);
        foreach (var b in plan.Towns.SelectMany(t => t.Buildings))
        {
            Assert.True(index.HasRoadWithin(b.X, b.Z, tuning.AccessRadius), "a building without a road");
            Assert.False(terrain.IsWater(b.X, b.Z), "a building in the water");
        }
    }

    [Fact]
    public void BuildingsInATown_DoNotOverlap_AndNoStreetRunsThroughOne()
    {
        var net = new RoadNetwork(Terrain.Generate(9));
        var plan = Generate(net, 9, 3);
        var tuning = net.Tuning.Towns;
        var all = plan.Towns.SelectMany(t => t.Buildings).ToList();

        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
            {
                float d = MathF.Sqrt(MathF.Pow(all[i].X - all[j].X, 2) + MathF.Pow(all[i].Z - all[j].Z, 2));
                float needed = tuning.Of(all[i].Type, all[i].Tier).Footprint + tuning.Of(all[j].Type, all[j].Tier).Footprint;
                Assert.True(d >= needed, $"buildings {i} and {j} overlap ({d} m apart, {needed} needed)");
            }
        var sim = new Simulation(net, 9);
        sim.AddTowns(plan);
        Assert.All(sim.Buildings, b => Assert.Equal(BuildingState.Active, b.State));
        for (int r = 0; r < net.RoadCount; r++)
        {
            var road = net.GetRoad(new RoadId(r));
            if (road.Retired) continue;
            foreach (var b in sim.Buildings)
                Assert.True(road.Geometry.DistanceTo(b.X, b.Z) >= RoadNetwork.HalfWidth(road.Type) + tuning.Of(b.Type, b.Tier).Footprint, "a street runs through a building");
        }
    }

    [Fact]
    public void WhenTheLandRunsOut_FewerTownsAreMade()
    {
        var net = new RoadNetwork(Terrain.Create(160, 8f, (_, _) => 5f)); // 1280 m square, dry and level

        var plan = Generate(net, 4, 10);

        Assert.Equal(10, plan.Requested);
        Assert.InRange(plan.Towns.Count, 1, 4);
    }

    [Fact]
    public void AMapWithTowns_RunsTheSameEveryTime()
    {
        ulong Run()
        {
            var net = new RoadNetwork(Terrain.Generate(21));
            var plan = Generate(net, 21, 3);
            var sim = new Simulation(net, 21);
            sim.AddTowns(plan);
            sim.SeedTraffic(60);
            for (int t = 0; t < 600; t++) sim.Step();
            return sim.StateHash();
        }

        Assert.Equal(Run(), Run());
    }

    // ---- Access and abandonment ------------------------------------------------------------------------------------

    [Fact]
    public void ABuildingWithNoRoadNearIt_StartsInactive()
    {
        var (sim, home, shop, far) = SmallTown();

        Assert.Equal(BuildingState.Active, sim.Buildings[home].State);
        Assert.Equal(BuildingState.Active, sim.Buildings[shop].State);
        Assert.Equal(BuildingState.Inactive, sim.Buildings[far].State);
    }

    [Fact]
    public void WithoutARoad_ABuildingGoesInactive_ThenAbandoned_ThenIsDemolished()
    {
        var (sim, home, _, _) = SmallTown();
        var t = sim.TownTuning;
        float before = sim.Towns[0].Satisfaction;
        for (int r = 0; r < sim.Network.RoadCount; r++)
            if (!sim.Network.GetRoad(new RoadId(r)).Retired) sim.Execute(new RemoveRoadCommand(r));
        sim.Step();

        Assert.Equal(BuildingState.Inactive, sim.Buildings[home].State);

        RunDays(sim, t.InactiveMonths * sim.Network.Tuning.Economy.DaysPerMonth - 1);
        Assert.Equal(BuildingState.Inactive, sim.Buildings[home].State);
        RunDays(sim, 2);
        Assert.Equal(BuildingState.Abandoned, sim.Buildings[home].State);
        Assert.True(sim.Towns[0].Satisfaction < before, "abandoning costs satisfaction");

        RunDays(sim, t.AbandonedMonths * sim.Network.Tuning.Economy.DaysPerMonth + 1);
        Assert.Equal(BuildingState.Demolished, sim.Buildings[home].State);
        Assert.Empty(sim.BuildingsNear(100, 25, 5));
    }

    [Fact]
    public void ARoadThatComesBack_BringsTheBuildingBack()
    {
        var (sim, home, _, _) = SmallTown();
        for (int r = 0; r < sim.Network.RoadCount; r++)
            if (!sim.Network.GetRoad(new RoadId(r)).Retired) sim.Execute(new RemoveRoadCommand(r));
        RunDays(sim, 3.5 * sim.Network.Tuning.Economy.DaysPerMonth);
        Assert.Equal(BuildingState.Abandoned, sim.Buildings[home].State);

        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f)));
        sim.Step();

        Assert.Equal(BuildingState.Active, sim.Buildings[home].State);
    }

    [Fact]
    public void Satisfaction_RecoversEveryMonth_UpToTheMaximum()
    {
        var shipped = Tuning.Load();
        var (sim, _, _, _) = SmallTown(shipped with { Towns = shipped.Towns with { UnmetPenalty = 0f } });
        var t = sim.TownTuning;
        int daysPerMonth = sim.Network.Tuning.Economy.DaysPerMonth;
        float start = sim.Towns[0].Satisfaction;
        Assert.Equal(t.InitialSatisfaction, start);

        RunDays(sim, 2 * daysPerMonth);
        Assert.Equal(start + 2 * t.RecoveryPerMonth, sim.Towns[0].Satisfaction, 3);

        RunDays(sim, 60 * daysPerMonth);
        Assert.Equal(t.MaxSatisfaction, sim.Towns[0].Satisfaction);
    }

    // ---- Roads through buildings -----------------------------------------------------------------------------------

    [Fact]
    public void ARoadThroughABuilding_DemolishesIt_ForAPrice_AndTheTownMindsIt()
    {
        var (sim, home, shop, _) = SmallTown();
        var estimate = sim.EstimateRoadDemolition(100, -60, 100, 60, RoadType.TwoLane);
        long roadPrice = (long)Math.Ceiling(sim.Network.EstimateCost(100, -60, 100, 60, RoadType.TwoLane)!.Value.Money);
        long price = sim.TownTuning.Of(BuildingType.Home, 0).DemolitionPrice;
        long money = sim.Company.Money;
        float satisfaction = sim.Towns[0].Satisfaction;

        Assert.Equal(new Simulation.Demolition(1, price), estimate);
        Assert.True(sim.Execute(new BuildRoadCommand(100, -60, 100, 60, RoadType.TwoLane, 14f)) > 0);

        Assert.Equal(BuildingState.Demolished, sim.Buildings[home].State);
        Assert.Equal(BuildingState.Active, sim.Buildings[shop].State);
        Assert.Equal(money - roadPrice - price, sim.Company.Money);
        Assert.Equal(-price, sim.Company.Total(LedgerEntry.Demolition));
        Assert.Equal(satisfaction - sim.TownTuning.DemolishedPenalty, sim.Towns[0].Satisfaction, 3);
    }

    [Fact]
    public void ARoadBesideABuilding_LeavesItStanding()
    {
        var (sim, home, _, _) = SmallTown();

        Assert.Equal(Simulation.Demolition.None, sim.EstimateRoadDemolition(100, 80, 100, 160, RoadType.TwoLane));
        sim.Execute(new BuildRoadCommand(100, 80, 100, 160, RoadType.TwoLane, 14f));

        Assert.Equal(BuildingState.Active, sim.Buildings[home].State);
    }

    [Fact]
    public void WhenTheDemolitionCannotBePaid_TheRoadIsRefused()
    {
        var shipped = Tuning.Load();
        var (sim, home, _, _) = SmallTown(shipped with { Economy = shipped.Economy with { StartingMoney = 40000 } });
        long road = (long)Math.Ceiling(sim.Network.EstimateCost(100, -60, 100, 60, RoadType.TwoLane)!.Value.Money);
        Assert.True(sim.Company.Money > road, "the road alone would be affordable");
        // Spend until the road is affordable but the road and the home's demolition are not.
        sim.Execute(new TakeLoanCommand(0));
        long spare = sim.Company.Money - road - sim.TownTuning.Of(BuildingType.Home, 0).DemolitionPrice + 1;
        var net = sim.Network;
        // Run the money down with a road elsewhere.
        long spend = spare;
        float length = spend / net.Tuning.Roads.TwoLane.CostPerMetre;
        sim.Execute(new BuildRoadCommand(0, 200, length, 200, RoadType.TwoLane, 14f));
        long before = sim.Company.Money;

        int result = sim.Execute(new BuildRoadCommand(100, -60, 100, 60, RoadType.TwoLane, 14f));

        Assert.Equal(0, result);
        Assert.Equal(before, sim.Company.Money);
        Assert.Equal(BuildingState.Active, sim.Buildings[home].State);
    }

    // ---- Stations --------------------------------------------------------------------------------------------------

    [Fact]
    public void AStation_CoversTheActiveBuildingsInRange_AndCollectsFromThem()
    {
        var (sim, home, shop, far) = SmallTown();
        int a = sim.Execute(new BuildStationCommand(100, 0));
        int b = sim.Execute(new BuildStationCommand(330, 0));
        int passengers = 0;
        float radius = sim.Network.Tuning.Stations.CoverageRadius;

        Assert.Equal(new[] { home }, sim.CoveredBy(sim.Stations[a]));
        Assert.Equal(new[] { shop }, sim.CoveredBy(sim.Stations[b]));
        Assert.DoesNotContain(far, sim.CoveredBy(sim.Stations[a]));
        Assert.True(radius > 25f);

        sim.SeedTraffic(0);
        RunDays(sim, 0.5);

        float expectedHome = sim.TownTuning.Of(BuildingType.Home, 0).PassengersPerDay * 0.5f;
        Assert.Equal(expectedHome, sim.Stations[a].Waiting[passengers], 1);
        Assert.Equal(0f, sim.Stations[a].Waiting[1], 3); // goods come from factories, not from homes
    }

    [Fact]
    public void AStationFarFromTheTown_CollectsNothing_ButOnAMapWithoutTownsItCollectsTheFixedAmount()
    {
        var (sim, _, _, _) = SmallTown();
        // A second road far from every building.
        sim.Execute(new BuildRoadCommand(-1000, 1000, -400, 1000, RoadType.TwoLane, 14f));
        int lonely = sim.Execute(new BuildStationCommand(-700, 1000));
        RunDays(sim, 0.5);
        Assert.Equal(0f, sim.Stations[lonely].Waiting[0]);

        var net = new RoadNetwork(Terrain.Flat());
        var bare = new Simulation(net, 1);
        bare.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        int s = bare.Execute(new BuildStationCommand(200, 0));
        RunDays(bare, 0.1);
        Assert.True(bare.Stations[s].Waiting[0] > 0f);
    }

    [Fact]
    public void ABuildingThatGoesInactive_StopsFeedingItsStation()
    {
        var (sim, _, _, _) = SmallTown();
        int a = sim.Execute(new BuildStationCommand(100, 0));
        RunDays(sim, 0.1);
        float first = sim.Stations[a].Waiting[0];
        Assert.True(first > 0f);

        // Cut the road to the west of the station: the home is still within 40 m of the road by the station, so remove that as well.
        for (int r = 0; r < sim.Network.RoadCount; r++)
            if (!sim.Network.GetRoad(new RoadId(r)).Retired) sim.Execute(new RemoveRoadCommand(r));
        RunDays(sim, 0.1);

        Assert.Equal(first, sim.Stations[a].Waiting[0], 3);
    }

    [Fact]
    public void TownStats_CountPeopleJobsAndBuildings()
    {
        var (sim, _, _, _) = SmallTown();

        var stats = sim.StatsOf(0);

        Assert.Equal(sim.TownTuning.Of(BuildingType.Home, 0).People, stats.Population);
        Assert.Equal(sim.TownTuning.Of(BuildingType.Commerce, 1).Jobs, stats.Jobs);
        Assert.Equal((1, 1, 0, 1, 0), (stats.Homes, stats.Commerce, stats.Industry, stats.Inactive, stats.Abandoned));
    }

    [Fact]
    public void Towns_AreAddedOnce_BeforeTheSimulationRuns()
    {
        var (sim, _, _, _) = SmallTown();

        Assert.Throws<InvalidOperationException>(() => sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>())));
    }
}
