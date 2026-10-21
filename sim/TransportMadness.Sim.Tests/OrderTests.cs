using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The government's orders (C5): offers, acceptance, progress, rewards and failure, civil needs and state works.</summary>
public class OrderTests
{
    const int Goods = 1, Wood = 2, Food = 7, Trucks = 17;
    const int Forest = 0, FoodPlant = 5;

    static Terrain DryLevelMap() => Terrain.Create(512, 8f, (_, _) => 5f);

    /// <summary>A short day (60 s, so 3 days make a month of 180 s) keeps the runs quick; the tutorial is made small.</summary>
    static Tuning Quick(Func<OrderTuning, OrderTuning>? orders = null)
    {
        var t = Tuning.Load();
        t = t with { Economy = t.Economy with { DayLength = 60f }, Campaign = t.Campaign with { MaxExportSwing = 0f } }; // the monthly turn leaves the standing to the orders
        t = t with { Orders = t.Orders with { Tutorial = t.Orders.Tutorial with { Units = 40, Days = 20 } } };
        return orders == null ? t : t with { Orders = orders(t.Orders) };
    }

    static GatewayPlan RoadGate(float x) => new(GatewayKind.Road, "Test Road Gate", x, -30, 0);

    class World
    {
        public required Simulation Sim { get; init; }
        public int Depot { get; init; }
        public int ForestStation { get; set; } = -1;
        public int GateStation { get; set; } = -1;
        public int TownStation { get; set; } = -1;
    }

    /// <summary>A block of streets round (0, 0) that is a town, a road east from its corner to a road gateway at x = 1200 (with a forest at x = 500 and a food plant at x = 800 beside it). Stations are built by the tests.</summary>
    static World Build(Tuning? tuning = null, bool gateway = true, ulong seed = 5)
    {
        tuning ??= Quick();
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, seed, IncidentFrequency.Off);
        foreach (var (x0, z0, x1, z1) in new[] { (-60f, -60f, 60f, -60f), (60f, -60f, 60f, 60f), (60f, 60f, -60f, 60f), (-60f, 60f, -60f, -60f) })
            sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadType.TwoLane, 14f));
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(60, -60, 1300, -60, RoadType.TwoLane, 14f)));
        var lots = TownGenerator.LotSites(tuning.Towns, 0, 0, 0, 1, 0, 0, 0, 0).ToList();
        var buildings = new[] { new BuildingPlan(BuildingType.Home, 0, lots[0].X, lots[0].Z, lots[0].Facing), new BuildingPlan(BuildingType.Commerce, 0, lots[1].X, lots[1].Z, lots[1].Facing) };
        var industries = new[] { new IndustryPlan(Forest, 500, -30, 0), new IndustryPlan(FoodPlant, 800, -30, 0) };
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", 0, 0, 0, 1, buildings) }, industries, null, gateway ? new[] { RoadGate(1200) } : Array.Empty<GatewayPlan>()));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(60, -60, 1f), DepotLevel.Simple));
        Assert.True(depot >= 0);
        return new World { Sim = sim, Depot = depot };
    }

    static int Station(Simulation sim, float x, float z)
    {
        int id = sim.Execute(new BuildStationCommand(x, z));
        Assert.True(id >= 0, $"no station at {x}, {z}");
        return id;
    }

    static void StationsForQuota(World w)
    {
        w.ForestStation = Station(w.Sim, 500, -60);
        w.GateStation = Station(w.Sim, 1200, -60);
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static Order Tutorial(Simulation sim)
    {
        sim.Step();
        return sim.Orders.Single(o => o.Template == "tutorial");
    }

    // ---- Offers ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutGateways_NoOrdersAreOffered()
    {
        var w = Build(gateway: false);

        RunDays(w.Sim, 40);

        Assert.Empty(w.Sim.Orders);
    }

    [Fact]
    public void TheFirstOrder_IsTheTutorial_AWoodQuotaToTheGatewayOfferedAtTheStart()
    {
        var w = Build();

        var o = Tutorial(w.Sim);

        Assert.Equal(OrderState.Offered, o.State);
        Assert.Equal(OrderKind.Quota, o.Kind);
        Assert.Equal(Wood, o.Cargo);
        Assert.Equal(0, o.Gateway);
        Assert.Equal(40, o.Units);
        Assert.Equal(w.Sim.OrderTuning.Tutorial.Reward, o.Reward);
        Assert.Contains("wood", o.Title);
        Assert.Contains("Test Road Gate", o.Title);
        Assert.Single(w.Sim.Events, e => e.Kind == SimEventKind.Order && e.Text.StartsWith("New order"));
    }

    [Fact]
    public void TheTutorial_FallsBackToTheNextCargoSomethingMakes()
    {
        var tuning = Quick(o => o with { Tutorial = o.Tutorial with { Cargo = new[] { "steel", "food" } } });
        var w = Build(tuning);

        var o = Tutorial(w.Sim);

        Assert.Equal(Food, o.Cargo); // no steel mill on this map, but a food plant
    }

    [Fact]
    public void OffersComeAtRandomTimes_NotOnAMonthlyBeat_AndNeverMoreThanTheOpenLimit()
    {
        var w = Build();
        var sim = w.Sim;
        var ticks = new List<long>();
        int seen = 0;
        for (long t = 0; t < 70L * 1200; t++)
        {
            sim.Step();
            Assert.True(sim.OpenOffers <= sim.OrderTuning.MaxOpen);
            if (sim.Orders.Count != seen) { ticks.Add(sim.Tick); seen = sim.Orders.Count; }
        }

        Assert.True(ticks.Count >= 6, $"only {ticks.Count} offers in 70 days");
        long month = 3 * 1200;
        Assert.True(ticks.Skip(1).Select(t => t % month).Distinct().Count() > 3, "offers should not come at the same moment of the month");
    }

    [Fact]
    public void AnOfferNobodyAccepts_LapsesAfterTwoMonths_WithoutAPenalty()
    {
        var w = Build();
        var sim = w.Sim;
        var o = Tutorial(sim);
        var standing = sim.Company.Standing;

        RunDays(sim, sim.OrderTuning.Tutorial.ExpiryDays + 1);

        Assert.Equal(OrderState.Expired, o.State);
        Assert.Equal(standing, sim.Company.Standing);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Order && e.Text.StartsWith("Offer lapsed"));
    }

    [Fact]
    public void AnOfferCanBeDeclined_OnceOnly()
    {
        var w = Build();
        var o = Tutorial(w.Sim);

        Assert.Equal(1, w.Sim.Execute(new DeclineOrderCommand(o.Id)));
        Assert.Equal(OrderState.Declined, o.State);
        Assert.Equal(0, w.Sim.Execute(new DeclineOrderCommand(o.Id)));
        Assert.Equal(0, w.Sim.Execute(new DeclineOrderCommand(99)));
        Assert.Equal(0, w.Sim.OpenOffers);
    }

    [Fact]
    public void TheSameSeedOffersTheSameOrders_AnotherSeedOther()
    {
        string Run(ulong seed)
        {
            var w = Build(seed: seed);
            RunDays(w.Sim, 50);
            return string.Join("|", w.Sim.Orders.Select(o => $"{o.Template}/{o.Units}/{o.Days}/{o.Reward}/{o.OfferedAt}"));
        }

        Assert.Equal(Run(5), Run(5));
        Assert.NotEqual(Run(5), Run(6));
    }

    [Fact]
    public void ATemplateIsOnlyDrawnInThePhasesItNames_AndTheSameOrderDoesNotComeTwiceInARow()
    {
        var w = Build();
        var sim = w.Sim;
        RunDays(sim, 25 * 3); // 75 days: Rearmament (months 0 to 19) into Mobilisation

        var templates = sim.OrderTuning.Templates.ToDictionary(t => t.Id);
        foreach (var o in sim.Orders.Where(o => o.Template != "tutorial"))
        {
            string phase = sim.CampaignTuning.PhaseOf(o.OfferedAt / (3 * 1200)).Id;
            Assert.True(templates[o.Template].Weights.ContainsKey(phase), $"{o.Template} offered in {phase}");
        }
        Assert.Contains(sim.Orders, o => sim.CampaignTuning.PhaseOf(o.OfferedAt / (3 * 1200)).Id == "mobilisation");
        var list = sim.Orders.ToList();
        for (int i = 1; i < list.Count; i++)
            Assert.False(list[i].Template == list[i - 1].Template && list[i].Cargo == list[i - 1].Cargo && list[i].Gateway == list[i - 1].Gateway && list[i].Town == list[i - 1].Town && list[i - 1].Template != "tutorial" && list.Count > 12, "repeated");
    }

    // ---- Acceptance ------------------------------------------------------------------------------------------------

    [Fact]
    public void AnOrderNeedsStationsAtTheGatewayAndAtAProducer_BeforeItCanBeAccepted()
    {
        var w = Build();
        var o = Tutorial(w.Sim);

        Assert.Equal("Build a road station beside the Test Road Gate first", w.Sim.WhyNotAccept(o.Id));
        Assert.Equal(0, w.Sim.Execute(new AcceptOrderCommand(o.Id)));

        w.GateStation = Station(w.Sim, 1200, -60);
        Assert.Equal("Build a station at a forest first", w.Sim.WhyNotAccept(o.Id));

        w.ForestStation = Station(w.Sim, 500, -60);
        Assert.Null(w.Sim.WhyNotAccept(o.Id));
        Assert.Equal(1, w.Sim.Execute(new AcceptOrderCommand(o.Id)));
        Assert.Equal(OrderState.Active, o.State);
        Assert.Equal(o.AcceptedAt + 20L * 1200, o.Deadline);
    }

    [Fact]
    public void AnOrderCannotBeAcceptedTwice_NorAfterItLapsed_AndOnlyThreeAreActiveAtOnce()
    {
        var tuning = Quick(o => o with { Gaps = new[] { new OfferGapTuning { Phase = "rearmament", MinDays = 1, MaxDays = 1 }, new OfferGapTuning { Phase = "mobilisation", MinDays = 1, MaxDays = 1 } } });
        var w = Build(tuning);
        StationsForQuota(w);
        var sim = w.Sim;
        var first = Tutorial(sim);
        Assert.Equal(1, sim.Execute(new AcceptOrderCommand(first.Id)));
        Assert.Equal("It is no longer on offer", sim.WhyNotAccept(first.Id));

        RunDays(sim, 8);
        int accepted = 1;
        foreach (var o in sim.Orders.Where(o => o.State == OrderState.Offered && sim.WhyNotAccept(o.Id) == null).ToList())
            if (sim.Execute(new AcceptOrderCommand(o.Id)) == 1) accepted++;

        Assert.Equal(3, accepted);
        Assert.Equal(3, sim.ActiveOrders);
        var another = sim.Orders.First(o => o.State == OrderState.Offered);
        Assert.Equal("3 orders are already under way", sim.WhyNotAccept(another.Id));
    }

    // ---- Meeting and failing ---------------------------------------------------------------------------------------

    [Fact]
    public void DeliveringTheCargoToTheGateway_MeetsTheOrder_PaysTheRewardAndRaisesTheStanding()
    {
        var w = Build();
        StationsForQuota(w);
        var sim = w.Sim;
        var o = Tutorial(sim);
        sim.Execute(new AcceptOrderCommand(o.Id));
        int line = sim.Execute(new CreateLineCommand(new[] { w.ForestStation, w.GateStation }));
        Assert.True(sim.Execute(new BuyVehicleCommand(w.Depot, "truck", line)) >= 0);
        long money = sim.Company.Money;
        float standing = sim.Company.Standing;

        for (long t = 0; t < 18L * 1200 && o.State == OrderState.Active; t++) sim.Step();

        Assert.Equal(OrderState.Met, o.State);
        Assert.True(o.Progress >= o.Units);
        Assert.Equal(1, sim.Company.OrdersMet);
        Assert.Equal(sim.OrderTuning.Tutorial.Reward, sim.Company.Total(LedgerEntry.Orders));
        Assert.Equal(sim.OrderTuning.Tutorial.Reward, o.Paid);
        Assert.Equal(standing + sim.OrderTuning.Tutorial.Standing, sim.Company.Standing, 0.01f);
        Assert.True(sim.Company.Money > money + sim.OrderTuning.Tutorial.Reward - 1, "reward and haulage pay both arrived");
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Order && e.Text.StartsWith("Order met"));
        Assert.Equal(0, sim.ActiveOrders);
    }

    [Fact]
    public void ADeliveryToAnotherCargoOrGateway_DoesNotCountTowardAQuota()
    {
        var w = Build();
        StationsForQuota(w);
        var sim = w.Sim;
        var o = Tutorial(sim);
        sim.Execute(new AcceptOrderCommand(o.Id));

        sim.Distribute(sim.Stations[w.GateStation], Food, 10); // food is not wood
        Assert.Equal(0.0, o.Progress);
        sim.Distribute(sim.Stations[w.GateStation], Wood, 10);
        Assert.Equal(10.0, o.Progress, 3);
    }

    [Fact]
    public void AtTheDeadlineWithLittleDelivered_TheOrderFails_ItCostsAFineAndStanding()
    {
        var w = Build();
        StationsForQuota(w);
        var sim = w.Sim;
        var o = Tutorial(sim);
        sim.Execute(new AcceptOrderCommand(o.Id));
        sim.Distribute(sim.Stations[w.GateStation], Wood, 4);
        long money = sim.Company.Money;
        float standing = sim.Company.Standing;

        RunDays(sim, o.Days + 1);

        Assert.Equal(OrderState.Failed, o.State);
        Assert.Equal(1, sim.Company.OrdersFailed);
        long fine = (long)Math.Round(o.Reward * sim.OrderTuning.FailedFineShare);
        long partial = (long)Math.Round(o.Reward * 4.0 / o.Units);
        Assert.Equal(fine, -sim.Company.Total(LedgerEntry.Fines));
        Assert.Equal(partial - fine, o.Paid);
        Assert.True(sim.Company.Standing < standing - o.Standing * 1.4f, "standing fell by about 1.5 times the gain (and the monthly turn may have moved it too)");
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Order && e.Severity == EventSeverity.Serious && e.Text.StartsWith("Order failed"));
    }

    [Fact]
    public void AtTheDeadlineWithMostDelivered_TheOrderIsLate_PaidInProportionWithNoFine()
    {
        var w = Build();
        StationsForQuota(w);
        var sim = w.Sim;
        var o = Tutorial(sim);
        sim.Execute(new AcceptOrderCommand(o.Id));
        sim.Distribute(sim.Stations[w.GateStation], Wood, 30); // 30 of 40
        var fines = sim.Company.Total(LedgerEntry.Fines);

        RunDays(sim, o.Days + 1);

        Assert.Equal(OrderState.Late, o.State);
        Assert.Equal(1, sim.Company.OrdersLate);
        Assert.Equal((long)Math.Round(o.Reward * 0.75), sim.Company.Total(LedgerEntry.Orders));
        Assert.Equal(fines, sim.Company.Total(LedgerEntry.Fines));
    }

    [Fact]
    public void TheOrdersAreInTheStateHash()
    {
        ulong Run(bool accept)
        {
            var w = Build();
            StationsForQuota(w);
            var o = Tutorial(w.Sim);
            if (accept) w.Sim.Execute(new AcceptOrderCommand(o.Id));
            RunDays(w.Sim, 2);
            return w.Sim.StateHash();
        }

        Assert.Equal(Run(true), Run(true));
        Assert.NotEqual(Run(true), Run(false));
    }

    // ---- Civil needs -----------------------------------------------------------------------------------------------

    static Tuning OnlyCivil() => Quick(o => o with
    {
        Tutorial = o.Tutorial with { Cargo = new[] { "steel" } },
        Templates = o.Templates.Where(t => t.Id == "town-food").Select(t => t with { Weights = new Dictionary<string, int> { ["rearmament"] = 1 }, MinUnits = 5, MaxUnits = 5, MinDays = 10, MaxDays = 10 }).ToList(),
    });

    [Fact]
    public void ACivilNeed_AsksForCargoAtATown_AndIsMetByDeliveriesToItsBuildings()
    {
        var w = Build(OnlyCivil());
        var sim = w.Sim;
        RunDays(sim, 9);
        var o = Assert.Single(sim.Orders, o => o.Kind == OrderKind.Civil);
        Assert.Equal(0, o.Town);
        Assert.Contains("Testville", o.Title);

        Assert.Equal("Build a station in Testville first", sim.WhyNotAccept(o.Id));
        w.TownStation = Station(sim, -60, -60);
        Assert.Equal("Build a station at a food plant first", sim.WhyNotAccept(o.Id));
        Station(sim, 800, -60);
        Assert.Null(sim.WhyNotAccept(o.Id));
        Assert.Equal(1, sim.Execute(new AcceptOrderCommand(o.Id)));
        float satisfaction = sim.Towns[0].Satisfaction;
        sim.Towns[0].Satisfaction = 50f;

        sim.Distribute(sim.Stations[w.TownStation], Wood, 50); // not wanted by the town
        Assert.Equal(0.0, o.Progress);
        for (int k = 0; k < 10 && o.State == OrderState.Active; k++)
        {
            sim.Distribute(sim.Stations[w.TownStation], Food, 7);
            sim.Step();
        }

        Assert.Equal(OrderState.Met, o.State);
        Assert.True(sim.Towns[0].Satisfaction > 50f, "the town is pleased");
    }

    // ---- State works -----------------------------------------------------------------------------------------------

    static Tuning OnlyWorks() => Quick(o => o with
    {
        Tutorial = o.Tutorial with { Cargo = new[] { "steel" } },
        Templates = o.Templates.Where(t => t.Id == "truck-works").Select(t => t with { Weights = new Dictionary<string, int> { ["rearmament"] = 1 } }).ToList(),
    });

    [Fact]
    public void AStateWork_FoundsItsIndustriesWhenAccepted_NearATown_ApartFromEachOther()
    {
        var w = Build(OnlyWorks());
        var sim = w.Sim;
        RunDays(sim, 9);
        var o = Assert.Single(sim.Orders, o => o.Kind == OrderKind.StateWork);
        Assert.Equal(Trucks, o.Cargo);
        Assert.Equal(4, o.Works.Count);
        int industries = sim.Industries.Count;
        Assert.Equal("Build a station beside a gateway first", sim.WhyNotAccept(o.Id));
        Station(sim, 1200, -60);
        int version = sim.IndustriesVersion;

        Assert.Equal(1, sim.Execute(new AcceptOrderCommand(o.Id)));

        Assert.Equal(industries + 4, sim.Industries.Count);
        Assert.NotEqual(version, sim.IndustriesVersion);
        Assert.Equal(new[] { "truckplant", "engineworks", "bodyworks", "tyreworks" }, o.WorkIds.Select(id => sim.IndustryTuning.Types[sim.Industries[id].Type].Id));
        var founded = o.WorkIds.Select(id => sim.Industries[id]).ToList();
        for (int i = 0; i < founded.Count; i++)
            for (int j = i + 1; j < founded.Count; j++)
                Assert.True(MathF.Sqrt(MathF.Pow(founded[i].X - founded[j].X, 2) + MathF.Pow(founded[i].Z - founded[j].Z, 2)) >= sim.OrderTuning.Sites.MinSpacing - 0.01f);
        var tuning = sim.OrderTuning.Sites;
        Assert.All(founded.Skip(1), f => Assert.True(MathF.Sqrt(MathF.Pow(f.X - founded[0].X, 2) + MathF.Pow(f.Z - founded[0].Z, 2)) <= tuning.ClusterReach + 0.01f));
        Assert.Contains(sim.Events, e => e.Text.StartsWith("The state has founded"));

        // Trucks delivered to a gateway count, whichever it is.
        sim.Distribute(sim.Stations.Last(), Trucks, 5);
        Assert.Equal(5.0, o.Progress, 3);
    }

    [Fact]
    public void AStateWork_IsOfferedOncePerGame_AndNotWhileTheMapHasNoRoomForIt()
    {
        var w = Build(OnlyWorks());
        var sim = w.Sim;
        Station(sim, 1200, -60);
        RunDays(sim, 9);
        var first = Assert.Single(sim.Orders, o => o.Kind == OrderKind.StateWork);
        sim.Execute(new AcceptOrderCommand(first.Id));

        RunDays(sim, 30);

        Assert.Single(sim.Orders, o => o.Kind == OrderKind.StateWork);
    }

    // ---- The data --------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryTemplate_NamesKnownCargoAndPhases_AndEveryPhaseHasOffers()
    {
        var t = Tuning.Load();
        var cargo = t.Cargo.Select(c => c.Id).ToHashSet();

        Assert.All(t.Orders.Templates, template => Assert.Contains(template.Cargo, cargo));
        foreach (var phase in t.Campaign.Phases)
        {
            Assert.NotEmpty(t.Orders.Templates.Where(x => x.Weights.ContainsKey(phase.Id)));
            Assert.Contains(t.Orders.Gaps, g => g.Phase == phase.Id);
        }
    }

    [Fact]
    public void ABadTemplate_IsRefusedWhenTheTuningLoads()
    {
        var t = Tuning.Load();
        var bad = t.Orders with { Templates = t.Orders.Templates.Select(x => x.Id == "timber" ? x with { Cargo = "gold" } : x).ToList() };

        Assert.Throws<System.Text.Json.JsonException>(() => bad.Validate(t));
    }
}
