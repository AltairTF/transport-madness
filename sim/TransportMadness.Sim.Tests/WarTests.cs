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
        return t with { Economy = t.Economy with { DayLength = 30f }, Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f } };
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
        c with { Waves = waves.Select(w => new CallUpWave { Month = w.Month, Share = w.Share }).ToList() };

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
