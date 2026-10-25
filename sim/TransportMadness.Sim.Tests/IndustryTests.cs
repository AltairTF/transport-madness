using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Industries and the cargo chains: generation, production, what stations accept and where deliveries go.</summary>
public class IndustryTests
{
    const int Forest = 0, Quarry = 1, Farm = 2, Sawmill = 3, CementWorks = 4, FoodPlant = 5, Factory = 6;
    const int CoalMine = 7, IronMine = 8, SteelMill = 9, Refinery = 10, EngineWorks = 11, BodyWorks = 12, TyreWorks = 13, TruckPlant = 14;
    const int Passengers = 0, Goods = 1, Wood = 2, Planks = 3, Stone = 4, Materials = 5, Grain = 6, Food = 7;
    const int Coal = 8, Ore = 9, Steel = 10, Fuel = 11, Oil = 12, Rubber = 13, Engines = 14, Bodies = 15, Tyres = 16, Trucks = 17;

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static Terrain DryLevelMap() => Terrain.Create(512, 8f, (_, _) => 5f);

    /// <summary>A level dry map with one road along x from 0 to <paramref name="length"/>, a depot at its west end and the given industries and buildings beside it.</summary>
    static (Simulation Sim, int Depot) World(float length, IndustryPlan[] industries, BuildingPlan[]? buildings = null, Tuning? tuning = null)
    {
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, 4, IncidentFrequency.Off);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, length, 0, RoadType.TwoLane, 14f)));
        var towns = buildings == null ? Array.Empty<TownPlan>() : new[] { new TownPlan("Testville", length - 100, 0, buildings) };
        sim.AddTowns(new WorldPlan(1, towns, industries));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        Assert.True(depot >= 0);
        return (sim, depot);
    }

    static int Station(Simulation sim, float x)
    {
        int id = sim.Execute(new BuildStationCommand(x, 0));
        Assert.True(id >= 0, $"no station at {x}");
        return id;
    }

    static Tuning With(Func<Tuning, Tuning> change) => change(Tuning.Load());

    // ---- Generation ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(IndustryAmount.Few, 12)]
    [InlineData(IndustryAmount.Normal, 14)]
    [InlineData(IndustryAmount.Many, 21)]
    public void TheAmountSetting_DecidesHowManyIndustriesOfEachTypeAreMade(IndustryAmount amount, int expected)
    {
        var net = new RoadNetwork(DryLevelMap());
        var towns = TownGenerator.Generate(net, 3, 3);

        var plan = IndustryGenerator.Generate(net, 3, towns, amount);

        Assert.Equal(expected, plan.Industries!.Count);
        var counts = net.Tuning.Industries.Counts.Of(amount);
        for (int t = 0; t < counts.Count; t++) Assert.Equal(counts[t], plan.Industries!.Count(i => i.Type == t));
    }

    [Fact]
    public void Industries_AreDry_Level_ApartAndClearOfTheTowns()
    {
        var terrain = Terrain.Generate(8);
        var net = new RoadNetwork(terrain);
        var tuning = net.Tuning;
        var plan = IndustryGenerator.Generate(net, 8, TownGenerator.Generate(net, 8, 4), IndustryAmount.Many);

        Assert.True(plan.Industries!.Count >= 7, $"only {plan.Industries.Count} industries fit");
        foreach (var i in plan.Industries!)
        {
            Assert.False(terrain.IsWater(i.X, i.Z));
            foreach (var town in plan.Towns)
            {
                float d = MathF.Sqrt(MathF.Pow(town.X - i.X, 2) + MathF.Pow(town.Z - i.Z, 2));
                Assert.True(d > town.Radius, "an industry inside a town");
            }
        }
        foreach (var a in plan.Industries!)
            foreach (var b in plan.Industries!)
                if (!a.Equals(b))
                    Assert.True(MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Z - b.Z, 2)) >= tuning.Industries.Generation.MinSpacing);
    }

    [Fact]
    public void TheSameSeed_GivesTheSameIndustries_AndTheTownsAreUnchangedByThem()
    {
        (WorldPlan, WorldPlan) Make(ulong seed)
        {
            var net = new RoadNetwork(Terrain.Generate(seed));
            var towns = TownGenerator.Generate(net, seed, 3);
            return (towns, IndustryGenerator.Generate(net, seed, towns, IndustryAmount.Normal));
        }

        var (townsA, a) = Make(31);
        var (_, b) = Make(31);
        var (_, c) = Make(32);

        Assert.Equal(a.Industries, b.Industries);
        Assert.NotEqual(a.Industries, c.Industries);
        Assert.Equal(townsA.Towns.Select(t => (t.Name, t.X, t.Z)), a.Towns.Select(t => (t.Name, t.X, t.Z)));
        Assert.Null(townsA.Industries);
    }

    [Fact]
    public void WhenTheLandRunsOut_FewerIndustriesAreMade()
    {
        var net = new RoadNetwork(Terrain.Create(100, 8f, (_, _) => 5f)); // 800 m square

        var plan = IndustryGenerator.Generate(net, 5, new WorldPlan(0, Array.Empty<TownPlan>()), IndustryAmount.Many);

        Assert.InRange(plan.Industries!.Count, 1, 20);
    }

    /// <summary>
    /// The industries of a plan that break a placement rule: a processor without a producer of each input that is made on the map
    /// (oil and rubber are imported) and a town within <paramref name="reach"/>, and a deposit out of reach of a town or at the wrong distance from another deposit.
    /// </summary>
    static List<int> BrokenChains(Tuning tuning, WorldPlan plan, float reach)
    {
        var types = tuning.Industries.Types;
        var g = tuning.Industries.Generation;
        var broken = new List<int>();
        for (int k = 0; k < plan.Industries!.Count; k++)
        {
            var me = plan.Industries[k];
            var type = types[me.Type];
            float Dist(float x, float z) => MathF.Sqrt((x - me.X) * (x - me.X) + (z - me.Z) * (z - me.Z));
            bool Near(float x, float z) => Dist(x, z) <= reach;
            if (type.Deposit)
            {
                bool ok = plan.Towns.Any(t => Dist(t.X, t.Z) <= g.DepositReach)
                    && plan.Industries.Where(o => types[o.Type].Deposit && o.Type != me.Type).All(o => Dist(o.X, o.Z) >= g.DepositMinSeparation && Dist(o.X, o.Z) <= g.DepositMaxSeparation);
                if (!ok) broken.Add(k);
                continue;
            }
            if (type.Inputs.Count == 0) continue;
            bool linked = plan.Towns.Any(t => Near(t.X, t.Z)) && type.Inputs.All(i => IndustryGenerator.IsImported(tuning.Industries, i.Cargo) || plan.Industries.Any(o => types[o.Type].Output == i.Cargo && Near(o.X, o.Z)));
            if (!linked) broken.Add(k);
        }
        return broken;
    }

    [Theory]
    [InlineData(IndustryAmount.Few)]
    [InlineData(IndustryAmount.Normal)]
    [InlineData(IndustryAmount.Many)]
    public void EveryProcessor_HasItsInputsAndATownWithinReach_OnMostMaps_AndTheRestAreReported(IndustryAmount amount)
    {
        var tuning = Tuning.Load();
        float reach = tuning.Industries.Generation.ChainReach;
        int mapsWithAGap = 0, maps = 40;
        for (ulong seed = 1; seed <= (ulong)maps; seed++)
        {
            if (Terrain.CoastOf(seed) == CoastKind.Island) continue; // little land: a deposit or a processor can miss
            var net = new RoadNetwork(Terrain.Generate(seed), tuning);
            var plan = IndustryGenerator.Generate(net, seed, TownGenerator.Generate(net, seed, 5), amount);
            var broken = BrokenChains(tuning, plan, reach);
            Assert.Equal(broken.OrderBy(k => k), plan.Unlinked!.OrderBy(k => k)); // what is out of reach is exactly what is reported
            if (broken.Count > 0) mapsWithAGap++;
        }
        // The land is rugged now, so level sites are fewer; towns and industries are placed by the land in a later step.
        Assert.True(mapsWithAGap <= maps * 3 / 10, $"{mapsWithAGap} of {maps} maps have a chain out of reach");
    }

    [Fact]
    public void WhenAChainDoesNotFit_TheProcessorIsStillPlaced_AndReported()
    {
        // Reach of nothing: no processor can be placed next to anything, so all of them fall back and are listed.
        var tuning = With(t => t with { Industries = t.Industries with { Generation = t.Industries.Generation with { ChainReach = 1f } } });
        var net = new RoadNetwork(Terrain.Generate(3), tuning);

        var plan = IndustryGenerator.Generate(net, 3, TownGenerator.Generate(net, 3, 4), IndustryAmount.Normal);

        // Every processor is reported (even the refinery, which needs a town within reach), and so may a deposit be.
        var types = tuning.Industries.Types;
        var processors = Enumerable.Range(0, plan.Industries!.Count).Where(k => types[plan.Industries[k].Type].Inputs.Count > 0).ToList();
        Assert.NotEmpty(processors);
        Assert.All(processors, k => Assert.Contains(k, plan.Unlinked!));
        Assert.All(plan.Unlinked!, k => Assert.True(types[plan.Industries![k].Type].Inputs.Count > 0 || types[plan.Industries![k].Type].Deposit));
    }

    // ---- Production ----------------------------------------------------------------------------------------------

    [Fact]
    public void APrimaryIndustry_MakesItsMonthlyAmount_AndSharesItAmongTheStationsThatCoverIt()
    {
        var (sim, _) = World(1200, new[] { new IndustryPlan(Forest, 500, 30, 0) });
        int a = Station(sim, 450), b = Station(sim, 550);
        float perDay = sim.IndustryTuning.Types[Forest].PerMonth / sim.Network.Tuning.Economy.DaysPerMonth;

        RunDays(sim, 1);

        Assert.Equal(perDay, sim.Industries[0].Produced, 1);
        Assert.Equal(perDay / 2f, sim.Stations[a].Waiting[Wood], 1);
        Assert.Equal(perDay / 2f, sim.Stations[b].Waiting[Wood], 1);
        Assert.Equal(0f, sim.Industries[0].Output, 1);
    }

    [Fact]
    public void WithoutAStation_TheOutputPilesUpToTheLimit_ThenProductionStops()
    {
        var (sim, _) = World(1200, new[] { new IndustryPlan(Forest, 500, 30, 0) });
        float limit = sim.IndustryTuning.Types[Forest].MaxOutput;

        RunDays(sim, 8);

        Assert.InRange(sim.Industries[0].Output, limit - 1f, limit);
        Assert.InRange(sim.Industries[0].Produced, limit - 1f, limit);
    }

    [Fact]
    public void AStationThatIsFull_LeavesTheRestWithTheIndustry()
    {
        var (sim, _) = World(1200, new[] { new IndustryPlan(Forest, 500, 30, 0) });
        int a = Station(sim, 500);

        RunDays(sim, 3);

        Assert.Equal(sim.CargoTypes[Wood].MaxWaiting, sim.Stations[a].Waiting[Wood], 1);
        Assert.True(sim.Industries[0].Output > 0f);
    }

    [Fact]
    public void AProcessorWithNothingDelivered_MakesNothing()
    {
        var (sim, _) = World(1200, new[] { new IndustryPlan(Sawmill, 500, 30, 0) });
        Station(sim, 500);

        RunDays(sim, 2);

        Assert.Equal(0.0, sim.Industries[0].Produced);
    }

    [Fact]
    public void ThereIsNoFixedSupply_OnAMapWithIndustries()
    {
        var (sim, _) = World(1200, new[] { new IndustryPlan(Forest, 900, 30, 0) });
        int far = Station(sim, 200);

        RunDays(sim, 1);

        Assert.All(sim.Stations[far].Waiting, w => Assert.Equal(0f, w));
    }

    // ---- Acceptance ----------------------------------------------------------------------------------------------

    [Fact]
    public void AStation_AcceptsWhatTheIndustriesAndBuildingsItCoversUse()
    {
        var homes = new[] { new BuildingPlan(BuildingType.Home, 0, 1100, 25, 0), new BuildingPlan(BuildingType.Industry, 0, 1100, -25, 0) };
        var (sim, _) = World(1400, new[] { new IndustryPlan(Sawmill, 300, 30, 0), new IndustryPlan(Forest, 700, 30, 0) }, homes);
        var sawmill = sim.Stations[Station(sim, 300)];
        var forest = sim.Stations[Station(sim, 700)];
        var town = sim.Stations[Station(sim, 1100)];
        var nowhere = sim.Stations[Station(sim, 1300)];

        Assert.True(sim.Accepts(sawmill, Wood));
        Assert.False(sim.Accepts(sawmill, Planks));
        Assert.False(sim.Accepts(sawmill, Food));
        Assert.All(Enumerable.Range(0, sim.CargoTypes.Count), c => Assert.False(sim.Accepts(forest, c)));
        Assert.True(sim.Accepts(town, Food));
        Assert.True(sim.Accepts(town, Goods));
        Assert.True(sim.Accepts(town, Planks)); // a light industry building uses planks
        Assert.True(sim.Accepts(town, Materials)); // buildings keep building materials for growing
        Assert.True(sim.Accepts(town, Passengers));
        Assert.False(sim.Accepts(town, Wood));
        Assert.False(sim.Accepts(nowhere, Passengers));
    }

    [Fact]
    public void OnAMapWithNothingOnIt_EveryStationAcceptsEverything()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 1);
        sim.Execute(new BuildRoadCommand(0, 0, 600, 0, RoadType.TwoLane, 14f));
        var station = sim.Stations[sim.Execute(new BuildStationCommand(300, 0))];

        Assert.All(Enumerable.Range(0, sim.CargoTypes.Count), c => Assert.True(sim.Accepts(station, c)));
    }

    // ---- Freight and deliveries ----------------------------------------------------------------------------------

    [Fact]
    public void AFreightTruck_LoadsOnlyWhatTheNextStopAccepts()
    {
        var (sim, depot) = World(1400, new[] { new IndustryPlan(CementWorks, 1100, 30, 0) });
        int a = Station(sim, 300), b = Station(sim, 1100);
        sim.Stations[a].Waiting[Wood] = 150f; // more of this, but nobody downline wants it
        sim.Stations[a].Waiting[Stone] = 40f;
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int truck = sim.Execute(new BuyVehicleCommand(depot, "truck", line));
        Assert.True(truck >= 0);

        for (int t = 0; t < 20 * 400 && sim.GetFleetInfo(truck).Load == 0; t++) sim.Step();

        var info = sim.GetFleetInfo(truck);
        Assert.Equal(Stone, info.Cargo);
        Assert.Equal(20, info.Load);
        Assert.Equal(150f, sim.Stations[a].Waiting[Wood]);
    }

    [Fact]
    public void ADeliveryToAnIndustry_FillsItsInput_PaysForWhatIsTaken_AndLosesTheRest()
    {
        var tuning = With(t => t with { Industries = t.Industries with { Types = t.Industries.Types.Select(x => x.Id == "sawmill" ? x with { MaxInput = 5f } : x).ToList() } });
        var (sim, depot) = World(1400, new[] { new IndustryPlan(Sawmill, 1100, 30, 0) }, tuning: tuning);
        int a = Station(sim, 300), b = Station(sim, 1100);
        sim.Stations[a].Waiting[Wood] = 60f;
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        sim.Execute(new BuyVehicleCommand(depot, "truck", line));

        RunDays(sim, 0.25);

        Assert.Equal(5, sim.Lines[line].Delivered);
        Assert.True(sim.Lines[line].Income > 0);
        // The five units were used as they came in, so the sawmill has made planks from them.
        Assert.Equal(5.0, sim.Industries[0].Produced + sim.Industries[0].Input[Wood], 2);
    }

    [Fact]
    public void ADeliveryToABuildingSite_IsSharedByRoom_AndFillsTheMonthlyNeedsFirst()
    {
        var tuning = With(t => t with { Towns = t.Towns with { InitialStockMonths = 0f } });
        var buildings = new[]
        {
            new BuildingPlan(BuildingType.Home, 0, 1100, 25, 0),  // goods need 2 a month, room for 4
            new BuildingPlan(BuildingType.Home, 1, 1100, -25, 0), // goods need 5 a month, room for 10
        };
        var (sim, depot) = World(1400, Array.Empty<IndustryPlan>(), buildings, tuning);
        int a = Station(sim, 300), b = Station(sim, 1100);
        sim.Stations[a].Waiting[Goods] = 10f;
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        sim.Execute(new BuyVehicleCommand(depot, "truck", line));

        RunDays(sim, 0.25);

        var small = sim.Buildings[0].Sustain[Goods];
        var large = sim.Buildings[1].Sustain[Goods];
        Assert.Equal(10f, small + large, 2);
        Assert.Equal(2.5f, large / small, 2);
        Assert.Equal(0f, sim.Buildings[0].Construction[Goods]);
    }

    [Fact]
    public void ACargoABuildingKeepsForBuilding_IsStoredBesideItsMonthlyNeeds()
    {
        var tuning = With(t => t with { Towns = t.Towns with { InitialStockMonths = 0f } });
        var (sim, depot) = World(1400, Array.Empty<IndustryPlan>(), new[] { new BuildingPlan(BuildingType.Industry, 0, 1100, 25, 0) }, tuning);
        int a = Station(sim, 300), b = Station(sim, 1100);
        sim.Stations[a].Waiting[Planks] = 100f; // an industry building: 6 a month (room 12) plus 10 to keep for building
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        sim.Execute(new BuyVehicleCommand(depot, "truck", line));

        RunDays(sim, 2); // less than a month, so nothing has been used up yet

        Assert.Equal(12f, sim.Buildings[0].Sustain[Planks], 1);
        Assert.Equal(10f, sim.Buildings[0].Construction[Planks], 1);
    }

    // ---- The whole chain -----------------------------------------------------------------------------------------

    [Fact]
    public void WoodBecomesPlanks_BecomesGoods_AndReachesATown()
    {
        var tuning = With(t => t with { Towns = t.Towns with { InitialStockMonths = 0f } });
        var industries = new[] { new IndustryPlan(Forest, 300, 30, 0), new IndustryPlan(Sawmill, 1000, 30, 0), new IndustryPlan(Factory, 1700, 30, 0) };
        var town = new[]
        {
            new BuildingPlan(BuildingType.Home, 0, 2400, 25, 0), new BuildingPlan(BuildingType.Home, 0, 2400, -25, 0),
            new BuildingPlan(BuildingType.Commerce, 0, 2450, 25, 0), new BuildingPlan(BuildingType.Commerce, 0, 2450, -25, 0),
        };
        var (sim, depot) = World(2600, industries, town, tuning);
        int forest = Station(sim, 300), sawmill = Station(sim, 1000), factory = Station(sim, 1700), houses = Station(sim, 2420);
        int woodLine = sim.Execute(new CreateLineCommand(new[] { forest, sawmill }));
        int plankLine = sim.Execute(new CreateLineCommand(new[] { sawmill, factory }));
        int goodsLine = sim.Execute(new CreateLineCommand(new[] { factory, houses }));
        foreach (int line in new[] { woodLine, plankLine, goodsLine })
        {
            Assert.True(sim.Execute(new BuyVehicleCommand(depot, "truck", line)) >= 0);
            for (int t = 0; t < 400; t++) sim.Step(); // room for the next one at the depot
        }

        RunDays(sim, 15);

        Assert.True(sim.Industries[0].Produced > 0.0, "the forest made wood");
        Assert.True(sim.Industries[1].Produced > 0.0, "the sawmill made planks from it");
        Assert.True(sim.Industries[2].Produced > 0.0, "the factory made goods from them");
        Assert.True(sim.Lines[woodLine].Delivered > 0, $"wood line delivered {sim.Lines[woodLine].Delivered}");
        Assert.True(sim.Lines[plankLine].Delivered > 0, $"plank line delivered {sim.Lines[plankLine].Delivered}");
        Assert.True(sim.Lines[goodsLine].Delivered > 0, $"goods line delivered {sim.Lines[goodsLine].Delivered}");
        Assert.All(new[] { woodLine, plankLine, goodsLine }, line => Assert.True(sim.Lines[line].Income > 0));
        Assert.True(sim.Buildings.Sum(b => b.Sustain[Goods]) > 0f, "goods reached the buildings of the town");
        // Nothing appears from nowhere: the goods that were made are at the factory, in transit, or in the town.
        Assert.True(sim.Industries[2].Produced >= sim.Lines[goodsLine].Delivered);
    }

    // ---- Needs and satisfaction ------------------------------------------------------------------------------------

    [Fact]
    public void BuildingsStartWithSomeStock_AndANeglectedTownFeelsItOnceItRunsOut()
    {
        var buildings = new[] { new BuildingPlan(BuildingType.Home, 0, 200, 25, 0), new BuildingPlan(BuildingType.Commerce, 1, 300, -25, 0) };
        var (sim, _) = World(400, Array.Empty<IndustryPlan>(), buildings);
        int daysPerMonth = sim.Network.Tuning.Economy.DaysPerMonth;
        float start = sim.Towns[0].Satisfaction;
        Assert.True(sim.Buildings[0].Sustain[Food] > 0f);

        RunDays(sim, daysPerMonth);
        Assert.Equal(0f, sim.Towns[0].UnmetShare); // a month's worth was in stock
        RunDays(sim, 3 * daysPerMonth);

        Assert.Equal(1f, sim.Towns[0].UnmetShare, 3);
        Assert.True(sim.Towns[0].Satisfaction < start, $"{sim.Towns[0].Satisfaction} is not below {start}");
    }

    [Fact]
    public void ATownWhoseNeedsAreMet_LosesNothing()
    {
        var tuning = With(t => t with { Towns = t.Towns with { InitialStockMonths = 10f, SustainStockMonths = 10f } });
        var buildings = new[] { new BuildingPlan(BuildingType.Home, 0, 200, 25, 0) };
        var (sim, _) = World(400, Array.Empty<IndustryPlan>(), buildings, tuning);
        float start = sim.Towns[0].Satisfaction;
        int daysPerMonth = sim.Network.Tuning.Economy.DaysPerMonth;

        RunDays(sim, 4 * daysPerMonth);

        Assert.Equal(0f, sim.Towns[0].UnmetShare);
        Assert.Equal(start + 4 * sim.TownTuning.RecoveryPerMonth, sim.Towns[0].Satisfaction, 3);
    }

    [Fact]
    public void AMapWithTownsAndIndustries_RunsTheSameEveryTime()
    {
        ulong Run()
        {
            var net = new RoadNetwork(Terrain.Generate(41));
            var plan = IndustryGenerator.Generate(net, 41, TownGenerator.Generate(net, 41, 3), IndustryAmount.Normal);
            var sim = new Simulation(net, 41);
            sim.AddTowns(plan);
            sim.SeedTraffic(40);
            for (int t = 0; t < 1500; t++) sim.Step();
            return sim.StateHash();
        }

        Assert.Equal(Run(), Run());
    }
}
