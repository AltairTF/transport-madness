using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The war (C9): rationing of imports, the call-up, requisitions and raids.</summary>
public class WarTests
{
    const int Fuel = 11, Coal = 8;

    static Terrain DryLevelMap() => Terrain.Create(512, 8f, (_, _) => 5f);

    /// <summary>A short day (30 s) so that the phases of the war come quickly (the phase of the war follows the month, 3 days).</summary>
    static Tuning Quick(IncidentFrequency? unused = null)
    {
        var t = Tuning.Load();
        // No raids: these tests run into Total war and a raid could shut the gateway.
        return t with { Economy = t.Economy with { DayLength = 30f }, Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f }, War = t.War with { Bombing = t.War.Bombing with { FromMonth = 1000 } } };
    }

    static Simulation World(Tuning tuning, IncidentFrequency frequency = IncidentFrequency.Normal)
    {
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, 4, frequency);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1000, 0, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), Array.Empty<IndustryPlan>(), null, new[] { new GatewayPlan(GatewayKind.Road, "Test Road Gate", 500, 30, 0) }));
        Assert.True(sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple)) >= 0);
        Assert.True(sim.Execute(new BuildStationCommand(500, 0)) >= 0);
        return sim;
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static void RunToMonth(Simulation sim, long month) => RunDays(sim, month * 3 - sim.Day + 0.05);

    // ---- Rationing -------------------------------------------------------------------------------------------------

    [Fact]
    public void InRearmament_NothingIsRationed()
    {
        var sim = World(Quick());
        var fuel = sim.ImportOffers.Single(o => o.Cargo == "fuel");

        Assert.Equal(fuel.PricePerUnit, sim.ImportPrice(fuel));
        Assert.Equal(fuel.Units, sim.ImportUnits(fuel));
    }

    [Theory]
    [InlineData(25, 1.25f, 0.85f)]   // mobilisation
    [InlineData(50, 1.7f, 0.65f)]    // total war
    [InlineData(90, 2.0f, 0.5f)]     // the end
    public void InTheLaterPhases_FuelAndRubberAreDearerAndComeInSmallerShipments(int month, float price, float units)
    {
        var sim = World(Quick());
        RunToMonth(sim, month);
        var fuel = sim.ImportOffers.Single(o => o.Cargo == "fuel");
        var rubber = sim.ImportOffers.Single(o => o.Cargo == "rubber");
        var coal = sim.ImportOffers.Single(o => o.Cargo == "coal");

        Assert.Equal((int)Math.Round(fuel.PricePerUnit * price), sim.ImportPrice(fuel));
        Assert.Equal((int)Math.Round(fuel.Units * units), sim.ImportUnits(fuel));
        Assert.Equal((int)Math.Round(rubber.PricePerUnit * price), sim.ImportPrice(rubber));
        Assert.Equal(coal.PricePerUnit, sim.ImportPrice(coal)); // coal is not rationed
        Assert.Equal(coal.Units, sim.ImportUnits(coal));
    }

    [Fact]
    public void AnImportIsChargedAndDeliveredAtThePhasesPrice_EvenIfThePhaseChangesWhileItIsOnItsWay()
    {
        var tuning = Quick();
        var sim = World(tuning);
        RunToMonth(sim, 47);
        var fuel = sim.ImportOffers.Single(o => o.Cargo == "fuel");
        long money = sim.Company.Money;
        int units = sim.ImportUnits(fuel), price = sim.ImportPrice(fuel);
        Assert.Equal(1, sim.Execute(new RequestImportCommand(0, "fuel")));
        Assert.Equal(money - (long)units * price, sim.Company.Money);

        RunDays(sim, sim.GatewayTuning.ShipmentSeconds / sim.DayLengthSeconds + 1); // the shipment lands in total war, the request was made in mobilisation
        Assert.Equal("totalwar", sim.Phase.Id);

        Assert.Equal((double)units, sim.Gateways[0].Imported[Fuel]);
        Assert.True(units < fuel.Units);
    }

    [Fact]
    public void TheFrequencyOption_ScalesRationing_OffMeansNone_HighMeansTwiceTheChange()
    {
        double Price(IncidentFrequency f)
        {
            var sim = World(Quick(), f);
            RunToMonth(sim, 50);
            return sim.ImportPrice(sim.ImportOffers.Single(o => o.Cargo == "fuel")) / (double)sim.ImportOffers.Single(o => o.Cargo == "fuel").PricePerUnit;
        }

        Assert.Equal(1.0, Price(IncidentFrequency.Off), 3);
        Assert.Equal(1.35, Price(IncidentFrequency.Low), 3);
        Assert.Equal(1.7, Price(IncidentFrequency.Normal), 3);
        Assert.Equal(2.4, Price(IncidentFrequency.High), 3);
    }

    [Fact]
    public void WhenThePhaseChanges_TheNewsSaysRationingBegins()
    {
        var sim = World(Quick());

        RunToMonth(sim, 21);

        var news = Assert.Single(sim.Events, e => e.Kind == SimEventKind.War && e.Text.Contains("rationing"));
        Assert.Contains("Mobilisation", news.Text);
        Assert.Contains("rationing", news.Text);
    }

    [Fact]
    public void WithRationingOff_ThereIsNoNews()
    {
        var sim = World(Quick(), IncidentFrequency.Off);

        RunToMonth(sim, 50);

        Assert.DoesNotContain(sim.Events, e => e.Kind == SimEventKind.War && e.Text.Contains("rationing"));
    }

    [Fact]
    public void ABadRationingTuning_IsRefused()
    {
        var t = Tuning.Load();
        var bad = t.War with { Rationing = t.War.Rationing with { Cargo = new[] { "gold" } } };

        Assert.Throws<System.Text.Json.JsonException>(() => bad.Validate(t));
    }
}

/// <summary>The call-up (C9b).</summary>
public class ConscriptionTests
{
    const int Sawmill = 3, SteelMill = 9;
    const int Wood = 2, Planks = 3;

    /// <summary>A 300 s day: with a very short one the trips of the groups last longer than the day and they are always on the road.</summary>
    static Tuning Quick(Func<ConscriptionTuning, ConscriptionTuning>? change = null)
    {
        var t = Tuning.Load();
        t = t with { Economy = t.Economy with { DayLength = 300f }, Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f } };
        return change == null ? t : t with { War = t.War with { Conscription = change(t.War.Conscription) } };
    }

    static ConscriptionTuning Waves(ConscriptionTuning c, params (int Month, float Share)[] waves) =>
        c with { Waves = waves.Select(w => new WarWave { Month = w.Month, Share = w.Share }).ToList() };

    /// <summary>A block of streets that is a town with eight homes (so eight trip groups), and a sawmill beside it.</summary>
    static Simulation Town(Tuning tuning)
    {
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, 5, IncidentFrequency.Off);
        foreach (var (x0, z0, x1, z1) in new[] { (-60f, -60f, 60f, -60f), (60f, -60f, 60f, 60f), (60f, 60f, -60f, 60f), (-60f, 60f, -60f, -60f) })
            sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadType.TwoLane, 14f));
        var lots = TownGenerator.LotSites(tuning.Towns, 0, 0, 0, 1, 0, 0, 0, 0).ToList();
        var buildings = new List<BuildingPlan>();
        for (int k = 0; k < 8; k++) buildings.Add(new BuildingPlan(k % 4 == 3 ? BuildingType.Commerce : BuildingType.Home, 1, lots[k].X, lots[k].Z, lots[k].Facing));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", 0, 0, 0, 1, buildings) }, new[] { new IndustryPlan(Sawmill, 600, 0, 0) }));
        return sim;
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static int People(Simulation sim) => sim.TripGroups.Where(g => !g.Retired).Sum(g => g.People);

    [Fact]
    public void BeforeTheFirstWave_NobodyIsCalledUp_AndTheWorkforceIsWhole()
    {
        var sim = Town(Quick());

        RunDays(sim, 3 * 20);

        Assert.Equal(0L, sim.CalledUp);
        Assert.Equal(0f, sim.WorkforceGap);
        Assert.Equal(1f, sim.WorkforceFactor);
        Assert.Equal(22, sim.NextWave!.Month);
    }

    [Fact]
    public void AtTheStartOfAWaveMonth_AShareOfThePeopleLeave_AndTheyAreCountedPerTown()
    {
        var sim = Town(Quick(c => Waves(c, (2, 0.25f))));
        int before = People(sim);
        Assert.True(before >= 40);

        RunDays(sim, 3 * 2 + 3);

        long gone = sim.CalledUp;
        Assert.InRange(gone, before * 0.25 - 5, before * 0.25 + 5);
        Assert.Equal(before - gone, People(sim));
        Assert.Equal(gone, sim.Towns[0].CalledUp);
        Assert.Null(sim.NextWave);
    }

    [Fact]
    public void ThePeopleWhoLeave_FreeTheirJobs()
    {
        var sim = Town(Quick(c => Waves(c, (1, 0.5f))));
        int jobs = sim.Buildings.Where(b => b.State == BuildingState.Active).Sum(b => sim.JobsTakenAt(b.Id));
        Assert.Equal(People(sim), jobs);

        RunDays(sim, 3 * 1 + 3);

        int after = sim.Buildings.Sum(b => sim.JobsTakenAt(b.Id));
        Assert.Equal(People(sim), after);
    }

    [Fact]
    public void AGroupThatLosesEveryone_IsRetired()
    {
        var sim = Town(Quick(c => Waves(c, (1, 0.9999f))));

        RunDays(sim, 3 * 1 + 3);

        Assert.True(sim.TripGroups.Count(g => g.Retired) >= sim.TripGroups.Count / 2);
        Assert.All(sim.TripGroups.Where(g => g.Retired), g => Assert.Equal(0, g.People));
    }

    [Fact]
    public void TheGap_FollowsTheWaves_AndClosesALittleEachMonth()
    {
        var sim = Town(Quick(c => Waves(c, (1, 0.10f), (3, 0.10f)) with { RecoveryPerMonth = 0.01f }));

        RunDays(sim, 3 * 1 + 0.5);
        Assert.Equal(0.10f, sim.WorkforceGap, 0.001f);
        RunDays(sim, 3 * 1);
        Assert.Equal(0.09f, sim.WorkforceGap, 0.001f); // a month of recovery

        RunDays(sim, 3 * 1);
        // the second wave: 1 - (1 - 0.08) * 0.9, after another month of recovery
        Assert.Equal(1f - (1f - 0.08f) * 0.9f, sim.WorkforceGap, 0.003f);
        Assert.Equal(1f - sim.WorkforceGap * sim.WarTuning.Conscription.OutputLoss, sim.WorkforceFactor, 0.001f);
    }

    [Fact]
    public void ALowerWorkforce_MakesEveryIndustryProduceLess()
    {
        double Made(float share)
        {
            var sim = Town(Quick(c => Waves(c, (1, share)) with { RecoveryPerMonth = 0f }));
            RunDays(sim, 3 * 1 + 0.2);
            sim.Industries[0].Input[Wood] = 600f;
            RunDays(sim, 2);
            return sim.Industries[0].Produced;
        }

        double whole = Made(0.001f), cut = Made(0.5f);

        Assert.Equal(1 - 0.7 * 0.5, cut / whole, 2);
    }

    [Fact]
    public void ALowerWorkforce_CutsTheOwnStaffOfABigIndustry_AndTheWorkersSent()
    {
        var tuning = Quick(c => Waves(c, (1, 0.5f)) with { RecoveryPerMonth = 0f });
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, 5, IncidentFrequency.Off);
        var lots = TownGenerator.LotSites(tuning.Towns, 0, 0, 0, 1, 0, 0, 0, 0).ToList();
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", 0, 0, 0, 1, new[] { new BuildingPlan(BuildingType.Home, 1, lots[0].X, lots[0].Z, lots[0].Facing) }) }, new[] { new IndustryPlan(SteelMill, 700, 0, 0) }));
        sim.Step();
        float before = sim.StaffingOf(sim.Industries[0]);

        RunDays(sim, 3 * 1 + 0.5);

        Assert.Equal(before * 0.5f, sim.StaffingOf(sim.Industries[0]), 0.01f);
    }

    [Fact]
    public void EachTownLosesSatisfaction_AndTheCallUpIsOnTheFeed()
    {
        var sim = Town(Quick(c => Waves(c, (1, 0.1f))));
        float before = sim.Towns[0].Satisfaction;

        RunDays(sim, 3 * 1 + 0.5);

        Assert.True(sim.Towns[0].Satisfaction < before - 1f);
        var news = Assert.Single(sim.Events, e => e.Kind == SimEventKind.War && e.Text.StartsWith("Call-up"));
        Assert.Equal(EventSeverity.Serious, news.Severity);
    }

    [Fact]
    public void TheCallUpIsDeterministic_AndInTheStateHash()
    {
        ulong Run(float share)
        {
            var sim = Town(Quick(c => Waves(c, (1, share))));
            RunDays(sim, 3 * 1 + 1);
            return sim.StateHash();
        }

        Assert.Equal(Run(0.2f), Run(0.2f));
        Assert.NotEqual(Run(0.2f), Run(0.3f));
    }

    [Fact]
    public void ABadWaveTuning_IsRefused()
    {
        var t = Tuning.Load();
        var late = t.War with { Conscription = Waves(t.War.Conscription, (5, 0.1f), (3, 0.1f)) };
        var whole = t.War with { Conscription = Waves(t.War.Conscription, (5, 1.0f)) };

        Assert.Throws<System.Text.Json.JsonException>(() => late.Validate(t));
        Assert.Throws<System.Text.Json.JsonException>(() => whole.Validate(t));
    }
}

/// <summary>The army's requisitions (C9c).</summary>
public class RequisitionTests
{
    static Tuning Quick(Func<RequisitionTuning, RequisitionTuning>? change = null)
    {
        var t = Tuning.Load();
        t = Worlds.Late(t);
        t = t with { Economy = t.Economy with { DayLength = 30f }, Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f } };
        return change == null ? t : t with { War = t.War with { Requisition = change(t.War.Requisition) } };
    }

    static RequisitionTuning Waves(RequisitionTuning r, params (int Month, float Share)[] waves) =>
        r with { Waves = waves.Select(w => new WarWave { Month = w.Month, Share = w.Share }).ToList() };

    /// <summary>A road with a depot, two stations and a line, six buses and four trucks; a rail depot with a train; one parked bus.</summary>
    static (Simulation Sim, int Depot, int Line) Fleet(Tuning tuning, int buses = 6, int trucks = 4)
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning), 5, IncidentFrequency.Off);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f)));
        int depot = sim.Execute(new AddDepotCommand(sim.Network.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(150, 0)), b = sim.Execute(new BuildStationCommand(1050, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.True(depot >= 0 && line >= 0);
        sim.Execute(new TakeLoanCommand(1000000));
        for (int i = 0; i < buses; i++) Assert.True(sim.Execute(new BuyVehicleCommand(depot, "bus", line)) >= 0);
        for (int i = 0; i < trucks; i++) Assert.True(sim.Execute(new BuyVehicleCommand(depot, "truck", line)) >= 0);
        return (sim, depot, line);
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static int Count(Simulation sim) => Enumerable.Range(0, sim.VehicleCount).Count(sim.IsCompanyVehicle);

    [Fact]
    public void BeforeTheFirstWave_NothingIsTaken_AndItIsAnnouncedAMonthAhead()
    {
        var (sim, _, _) = Fleet(Quick(r => Waves(r, (3, 0.5f))));

        RunDays(sim, 3 * 2 + 0.5); // month 2 has begun: the notice
        var notice = Assert.Single(sim.Events, e => e.Kind == SimEventKind.War && e.Text.StartsWith("The army will requisition"));
        Assert.Contains("April 1938", notice.Text);
        Assert.Equal(10, Count(sim));
        Assert.Equal(5, sim.ExpectedRequisition);
        Assert.Equal(3, sim.NextRequisition!.Month);
    }

    [Fact]
    public void TheArmyTakesAShareOfTheFleet_FreightFirst_AndPaysTheTablePrice()
    {
        var (sim, _, _) = Fleet(Quick(r => Waves(r, (3, 0.5f))));
        long money = sim.Company.Money;
        var trucks = Enumerable.Range(0, sim.VehicleCount).Where(i => sim.IsCompanyVehicle(i) && sim.VehicleModels[sim.GetFleetInfo(i).Model].Id == "truck").ToList();
        float standing = sim.Company.Standing;

        RunDays(sim, 3 * 3 + 0.5);

        Assert.Equal(5, 10 - Count(sim));
        Assert.Equal(5, sim.Company.Requisitioned);
        // Four trucks and one bus: freight goes first.
        Assert.All(trucks, t => Assert.False(sim.IsCompanyVehicle(t)));
        long expected = (long)(4 * Math.Round(28000 * 0.8) + 1 * Math.Round(24000 * 0.8));
        Assert.Equal(expected, sim.Company.Total(LedgerEntry.Requisition));
        Assert.Equal(standing + sim.WarTuning.Requisition.Standing, sim.Company.Standing, 0.5f); // upkeep and the month's books move money, not standing here
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.War && e.Text.StartsWith("Requisition: the army has taken 5 of 10"));
        Assert.Null(sim.NextRequisition);
    }

    [Fact]
    public void ABiggerModelGoesBeforeASmallerOne_AndTheGarageCounts()
    {
        var (sim, depot, _) = Fleet(Quick(r => Waves(r, (3, 0.25f))), buses: 0, trucks: 0);
        sim.Execute(new BuyParkedVehicleCommand(depot, "heavytruck"));
        sim.Execute(new BuyParkedVehicleCommand(depot, "truck"));
        sim.Execute(new BuyParkedVehicleCommand(depot, "bus"));
        sim.Execute(new BuyParkedVehicleCommand(depot, "bigbus"));

        RunDays(sim, 3 * 3 + 0.5);

        Assert.Equal(1, sim.Company.Requisitioned);
        Assert.False(sim.Garage[0].Active, "the heavy truck is the biggest freight vehicle");
        Assert.True(sim.Garage[1].Active && sim.Garage[2].Active && sim.Garage[3].Active);
        Assert.Equal((long)Math.Round(46000 * 0.8), sim.Company.Total(LedgerEntry.Requisition));
    }

    [Fact]
    public void ATrainIsTakenToo_AndTheTablePriceIgnoresCondition()
    {
        var tuning = Quick(r => Waves(r, (3, 1.0f)));
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning), 5, IncidentFrequency.Off);
        Assert.True(sim.Execute(new BuildTrackCommand(0, 300, 300, 300)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(300, 300, 1500, 300)) > 0);
        int ra = sim.Execute(new BuildRailStationCommand(400, 300)), rb = sim.Execute(new BuildRailStationCommand(1400, 300));
        int rdepot = sim.Execute(new AddRailDepotCommand(300, 300));
        int rline = sim.Execute(new CreateLineCommand(new[] { ra, rb }));
        int train = sim.Execute(new BuyTrainCommand(rdepot, "steam", "coach", 3, rline));
        Assert.True(train >= 0);
        sim.Trains[train].Condition = 5f;

        RunDays(sim, 3 * 3 + 0.5);

        Assert.False(sim.Trains[train].Active);
        Assert.Equal((long)Math.Round((70000 + 3 * 36000) * 0.8), sim.Company.Total(LedgerEntry.Requisition));
    }

    [Fact]
    public void AnEmptyFleet_LosesNothing_AndTheArmyFindsNothing()
    {
        var (sim, _, _) = Fleet(Quick(r => Waves(r, (3, 0.5f))), buses: 0, trucks: 0);
        float standing = sim.Company.Standing;

        RunDays(sim, 3 * 3 + 0.5);

        Assert.Equal(0, sim.Company.Requisitioned);
        Assert.Equal(0L, sim.Company.Total(LedgerEntry.Requisition));
        Assert.Contains(sim.Events, e => e.Text == "Requisition: the army found nothing to take");
    }

    [Fact]
    public void ThePlayerCanPrepare_ByReplacingWhatWasTakenAfterwards_AndALaterWaveTakesAgain()
    {
        var (sim, depot, line) = Fleet(Quick(r => Waves(r, (3, 0.5f), (5, 0.5f))), buses: 4, trucks: 0);

        RunDays(sim, 3 * 3 + 0.5);
        Assert.Equal(2, Count(sim));
        for (int i = 0; i < 4; i++) sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        RunDays(sim, 3 * 2);

        Assert.Equal(3, Count(sim)); // half of six
        Assert.Equal(5, sim.Company.Requisitioned);
    }

    [Fact]
    public void TheRequisitionIsDeterministic_AndInTheStateHash()
    {
        ulong Run(float share)
        {
            var (sim, _, _) = Fleet(Quick(r => Waves(r, (3, share))));
            RunDays(sim, 3 * 3 + 1);
            return sim.StateHash();
        }

        Assert.Equal(Run(0.5f), Run(0.5f));
        Assert.NotEqual(Run(0.5f), Run(0.2f));
    }

    [Fact]
    public void ABadRequisitionTuning_IsRefused()
    {
        var t = Tuning.Load();
        var bad = t.War with { Requisition = Waves(t.War.Requisition, (5, 0.1f), (3, 0.1f)) };
        var free = t.War with { Requisition = t.War.Requisition with { PriceShare = 0f } };

        Assert.Throws<System.Text.Json.JsonException>(() => bad.Validate(t));
        Assert.Throws<System.Text.Json.JsonException>(() => free.Validate(t));
    }
}

/// <summary>The raids (C9d).</summary>
public class RaidTests
{
    const int Sawmill = 3, Wood = 2, Steel = 10;

    /// <summary>Raids from month 0, one a day, on the one kind of target; a short day so that repairs come quickly.</summary>
    static Tuning Raiding(string kind = "industry", Func<BombingTuning, BombingTuning>? change = null)
    {
        var t = Worlds.Late(Tuning.Load());
        var b = t.War.Bombing with
        {
            FromMonth = 0,
            Gaps = new[] { new RaidGapTuning { Phase = "rearmament", MinDays = 1, MaxDays = 1 } },
            Targets = new[] { new RaidTargetTuning { Kind = kind, Weight = 1 } },
        };
        if (change != null) b = change(b);
        return t with { Economy = t.Economy with { DayLength = 60f }, Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f }, War = t.War with { Bombing = b } };
    }

    /// <summary>A block of streets that is a town with homes, a road east past a sawmill (x = 600) to a road gateway (x = 1100), and a depot.</summary>
    static Simulation Region(Tuning tuning, IncidentFrequency frequency = IncidentFrequency.Normal, ulong seed = 5)
    {
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, seed, frequency);
        foreach (var (x0, z0, x1, z1) in new[] { (-60f, -60f, 60f, -60f), (60f, -60f, 60f, 60f), (60f, 60f, -60f, 60f), (-60f, 60f, -60f, -60f) })
            sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadType.TwoLane, 14f));
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(60, -60, 1300, -60, RoadType.TwoLane, 14f)));
        var lots = TownGenerator.LotSites(tuning.Towns, 0, 0, 0, 1, 0, 0, 0, 0).ToList();
        var buildings = new List<BuildingPlan>();
        for (int k = 0; k < 8; k++) buildings.Add(new BuildingPlan(k % 4 == 3 ? BuildingType.Commerce : BuildingType.Home, 1, lots[k].X, lots[k].Z, lots[k].Facing));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", 0, 0, 0, 1, buildings) }, new[] { new IndustryPlan(Sawmill, 600, -30, 0) }, null,
            new[] { new GatewayPlan(GatewayKind.Road, "Test Road Gate", 1100, -30, 0) }));
        Assert.True(sim.Execute(new AddDepotCommand(net.FindNodeNear(60, -60, 1f), DepotLevel.Simple)) >= 0);
        return sim;
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    // ---- When ------------------------------------------------------------------------------------------------------

    [Fact]
    public void NoRaidComesBeforeJanuary1942_OrWithTheFrequencyOff()
    {
        var shipped = Worlds.Late(Tuning.Load());
        var early = Region(shipped with { Economy = shipped.Economy with { DayLength = 60f } });
        RunDays(early, 3 * 47);
        Assert.Equal(0, early.Raids);
        Assert.Equal(-1L, early.NextRaidTick);

        var off = Region(Raiding(), IncidentFrequency.Off);
        RunDays(off, 30);
        Assert.Equal(0, off.Raids);
    }

    [Fact]
    public void TheRaidsComeAtTheGapOfThePhase_AndTheFrequencyOptionScalesIt()
    {
        int Count(IncidentFrequency f)
        {
            var sim = Region(Raiding("town", b => b with { BuildingsHit = 0, Craters = 0, Gaps = new[] { new RaidGapTuning { Phase = "rearmament", MinDays = 4, MaxDays = 4 } } }), f);
            RunDays(sim, 40);
            return sim.Raids;
        }

        int normal = Count(IncidentFrequency.Normal), low = Count(IncidentFrequency.Low), high = Count(IncidentFrequency.High);

        Assert.InRange(normal, 6, 10);   // about 40 / (4 + half a day)
        Assert.True(low < normal && normal < high, $"{low} {normal} {high}");
        Assert.InRange(low, 3, 6);
        Assert.InRange(high, 12, 20);
    }

    [Fact]
    public void TheShippedGaps_AreSixToTwelveDaysInTotalWar()
    {
        var sim = Region(Worlds.Late(Tuning.Load()));
        var gap = sim.WarTuning.Bombing.GapOf("totalwar");

        Assert.Equal((6, 12), (gap.MinDays, gap.MaxDays));
        Assert.Equal(48, sim.WarTuning.Bombing.FromMonth);
    }

    // ---- An industry -----------------------------------------------------------------------------------------------

    [Fact]
    public void AnIndustryThatIsBombed_StopsForDays_LosesHalfItsStock_AndWorksAgain()
    {
        var sim = Region(Raiding("industry"));
        var mill = sim.Industries[0];
        mill.Input[Wood] = 400f;
        mill.Output = 100f;
        float woodBefore = mill.Input[Wood];

        RunDays(sim, 2.2);
        Assert.Equal(1, sim.Raids);
        Assert.True(mill.DamagedUntil > sim.Tick);
        Assert.True(sim.StatusOf(mill).Damaged);
        Assert.False(sim.StatusOf(mill).Working);
        Assert.True(mill.Input[Wood] < woodBefore * 0.55f);
        double made = mill.Produced;
        RunDays(sim, 1);
        Assert.Equal(made, mill.Produced); // nothing made while it is down (the next raid may come: it is the only industry, damaged already)

        RunDays(sim, 3);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.Contains("working again"));
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Severity == EventSeverity.Serious && e.Text.StartsWith("Raid: the sawmill"));
    }

    // ---- A gateway -------------------------------------------------------------------------------------------------

    [Fact]
    public void AGatewayThatIsBombed_IsShut_TakesNothing_AndOpensAgain()
    {
        var sim = Region(Raiding("gateway"));
        int station = sim.Execute(new BuildStationCommand(1100, -60));
        Assert.True(station >= 0);
        Assert.True(sim.Accepts(sim.Stations[station], Steel));
        sim.Execute(new RequestImportCommand(0, "coal"));

        RunDays(sim, 2.2);

        Assert.True(sim.Gateways[0].DamagedUntil > sim.Tick);
        Assert.False(sim.Accepts(sim.Stations[station], Steel), "a shut gateway accepts no export");
        Assert.Equal(0.0, sim.Gateways[0].Imported[8]); // the shipment is held up

        RunDays(sim, sim.GatewayTuning.ShipmentSeconds / sim.DayLengthSeconds + 3);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.Contains("is open again"));
    }

    // ---- The railway -----------------------------------------------------------------------------------------------

    [Fact]
    public void ARaidOnTheRailway_FailsASignal_AndACrewMendsIt()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Raiding("rail")), 5, IncidentFrequency.Normal);
        Assert.True(sim.Execute(new BuildTrackCommand(0, 0, 1500, 0)) > 0);
        int id = sim.Execute(new BuildSignalCommand(700, 0, SignalKind.Block, Forward: true));
        Assert.True(id >= 0);
        sim.Execute(new AddRailDepotCommand(300, 0));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>()));
        var signal = sim.Rail.GetSignal(new SignalId(id));

        RunDays(sim, 2.2);

        Assert.True(signal.Failed);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.Contains("railway was bombed"));
        RunDays(sim, 12);
        Assert.True(sim.Company.Total(LedgerEntry.Maintenance) < 0, "a crew has been to mend it"); // a later raid may have failed it again
    }

    [Fact]
    public void WithoutASignal_ARailTargetIsNotPossible_AndNothingHappens()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Raiding("rail")), 5, IncidentFrequency.Normal);
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>()));

        RunDays(sim, 5);

        Assert.Equal(0, sim.Raids);
    }

    // ---- A town ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ATownThatIsBombed_LosesBuildings_CountsCasualties_AndIsUnhappy()
    {
        var sim = Region(Raiding("town"));
        int before = sim.Buildings.Count(b => b.State == BuildingState.Active);
        float satisfaction = sim.Towns[0].Satisfaction;

        RunDays(sim, 2.2);

        Assert.Equal(1, sim.Raids);
        Assert.Equal(before - 2, sim.Buildings.Count(b => b.State == BuildingState.Active));
        Assert.True(sim.Casualties > 0);
        Assert.Equal(sim.Casualties, sim.Towns[0].Casualties);
        Assert.True(sim.Towns[0].Satisfaction < satisfaction - 1f);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.StartsWith("Raid: Testville was bombed. 2 buildings destroyed"));
    }

    // ---- Craters ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ARaidLeavesCratersOnTheNearestRoads_ThatACrewClears()
    {
        var sim = Region(Raiding("industry", b => b with { Craters = 3, IndustryDownDays = 0, Gaps = new[] { new RaidGapTuning { Phase = "rearmament", MinDays = 5, MaxDays = 5 } } }));

        RunDays(sim, 6.2);

        var craters = sim.Incidents.Where(i => i.Kind == IncidentKind.Bombing).ToList();
        Assert.NotEmpty(craters);
        Assert.True(Enumerable.Range(0, sim.VehicleCount).Any(v => sim.GetVehicle(v).Kind == VehicleKind.Debris) || craters.Any(c => c.State == IncidentState.Cleared), "debris lies in the lane");

        RunDays(sim, 3);

        Assert.Contains(sim.Incidents, i => i.Kind == IncidentKind.Bombing && i.State == IncidentState.Cleared);
    }

    // ---- State -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRaidsAreDeterministic_AndInTheStateHash()
    {
        ulong Run(string kind, ulong seed)
        {
            var sim = Region(Raiding(kind), IncidentFrequency.Normal, seed);
            RunDays(sim, 4);
            return sim.StateHash();
        }

        Assert.Equal(Run("town", 5), Run("town", 5));
        Assert.NotEqual(Run("town", 5), Run("industry", 5));
        Assert.NotEqual(Run("town", 5), Run("town", 6));
    }

    [Fact]
    public void ABadBombingTuning_IsRefused()
    {
        var t = Tuning.Load();
        var kind = t.War with { Bombing = t.War.Bombing with { Targets = new[] { new RaidTargetTuning { Kind = "moon", Weight = 1 } } } };
        var gap = t.War with { Bombing = t.War.Bombing with { Gaps = new[] { new RaidGapTuning { Phase = "totalwar", MinDays = 9, MaxDays = 3 } } } };

        Assert.Throws<System.Text.Json.JsonException>(() => kind.Validate(t));
        Assert.Throws<System.Text.Json.JsonException>(() => gap.Validate(t));
    }
}
