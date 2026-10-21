using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Workers (C7): jobs in the big industries, staffing from the people's own movement and from the workers' bus.</summary>
public class WorkerTests
{
    const int Forest = 0, SteelMill = 9, PowerPlant = 15;
    const int Wood = 2, Coal = 8, Ore = 9, Steel = 10, Workers = 18;

    static Tuning Quick()
    {
        var t = Tuning.Load();
        return t with { Economy = t.Economy with { DayLength = 120f } };
    }

    /// <summary>A block of streets round (0, 0) that is a town, a road east from its corner, a steel mill at the given x beside the road.</summary>
    static (Simulation Sim, int Depot) Build(float millX = 700, Tuning? tuning = null, bool town = true)
    {
        tuning ??= Quick();
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, 5, IncidentFrequency.Off);
        foreach (var (x0, z0, x1, z1) in new[] { (-60f, -60f, 60f, -60f), (60f, -60f, 60f, 60f), (60f, 60f, -60f, 60f), (-60f, 60f, -60f, -60f) })
            sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadType.TwoLane, 14f));
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(60, -60, 1400, -60, RoadType.TwoLane, 14f)));
        var lots = TownGenerator.LotSites(tuning.Towns, 0, 0, 0, 1, 0, 0, 0, 0).ToList();
        var buildings = new List<BuildingPlan>();
        for (int k = 0; k < 6; k++) buildings.Add(new BuildingPlan(k % 3 == 2 ? BuildingType.Commerce : BuildingType.Home, 1, lots[k].X, lots[k].Z, lots[k].Facing));
        var towns = town ? new[] { new TownPlan("Testville", 0, 0, 0, 1, buildings) } : Array.Empty<TownPlan>();
        sim.AddTowns(new WorldPlan(1, towns, new[] { new IndustryPlan(SteelMill, millX, -30, 0) }));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(60, -60, 1f), DepotLevel.Simple));
        Assert.True(depot >= 0);
        return (sim, depot);
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static double MillOutput(Simulation sim, double days)
    {
        var mill = sim.Industries[0];
        mill.Input[Ore] = 600f;
        mill.Input[Coal] = 600f;
        RunDays(sim, days);
        return mill.Produced;
    }

    // ---- Staffing --------------------------------------------------------------------------------------------------

    [Fact]
    public void ABigIndustry_HasJobs_AndTheSmallOnesHaveNone()
    {
        var t = Tuning.Load().Industries.Types;

        Assert.Equal(new[] { "steelmill", "engineworks", "truckplant", "powerplant" }, t.Where(x => x.Jobs > 0).Select(x => x.Id));
        Assert.Equal(40, t.Single(x => x.Id == "steelmill").Jobs);
    }

    [Fact]
    public void OnAMapWithoutTowns_NobodyIsNeeded_AndStaffingIsFull()
    {
        var (sim, _) = Build(town: false);

        Assert.Equal(1f, sim.StaffingOf(sim.Industries[0]));
    }

    [Fact]
    public void TheOwnStaff_FallsWithTheDistanceToTheTown_AndIsNothingBeyondItsReach()
    {
        var w = Tuning.Load().Workers;
        float Own(float x, float reach)
        {
            var t = Quick();
            var (sim, _) = Build(millX: x, tuning: t with { Workers = t.Workers with { OwnReach = reach } });
            return sim.Industries[0].OwnStaff;
        }

        Assert.Equal(w.OwnShare * (1f - 700f / w.OwnReach), Own(700, w.OwnReach), 0.01f);
        Assert.True(Own(700, w.OwnReach) > Own(1200, w.OwnReach));
        Assert.Equal(0f, Own(1200, 1000f));
    }

    [Fact]
    public void WithoutWorkersBrought_AnIndustryMakesOnlyWhatItsOwnStaffAllows()
    {
        var (sim, _) = Build(millX: 700);
        float own = sim.Industries[0].OwnStaff;
        Assert.InRange(own, 0.2f, 0.5f);

        double made = MillOutput(sim, 1);
        var (full, _) = Build(millX: 700, town: false);
        double fullMade = MillOutput(full, 1);

        Assert.Equal(own, made / fullMade, 1);
    }

    [Fact]
    public void WorkersDelivered_AddToTheStaffing_AndLeaveAgainEachDay()
    {
        var (sim, _) = Build();
        var mill = sim.Industries[0];
        sim.Step();
        float own = sim.StaffingOf(mill);

        mill.Input[Workers] = 20f;
        sim.Step();
        Assert.Equal(MathF.Min(1f, own + 20f / 40f), sim.StaffingOf(mill), 0.01f);

        RunDays(sim, 1);
        Assert.True(mill.Input[Workers] < 20f * 0.45f, $"{mill.Input[Workers]} left after a day");
    }

    [Fact]
    public void AStationBesideTheIndustry_AcceptsWorkers_OnlyWhereThereAreJobs()
    {
        var (sim, _) = Build();
        int mill = sim.Execute(new BuildStationCommand(700, -60));
        int homes = sim.Execute(new BuildStationCommand(-60, -60));

        Assert.True(sim.Accepts(sim.Stations[mill], Workers));
        Assert.False(sim.Accepts(sim.Stations[homes], Workers));
    }

    // ---- Carrying them ---------------------------------------------------------------------------------------------

    [Fact]
    public void AStationBesideHomes_GetsWorkersWaiting_WhileTheIndustryIsShortOfStaff()
    {
        var (sim, _) = Build();
        int homes = sim.Execute(new BuildStationCommand(-60, -60));

        RunDays(sim, 1.2);

        float waiting = sim.Stations[homes].Waiting[Workers];
        Assert.True(waiting > 5f, $"{waiting} workers waiting");
        Assert.True(waiting <= sim.CargoTypes[Workers].MaxWaiting);
    }

    [Fact]
    public void ANoWorkersAreSent_ToAnIndustryThatIsFullyStaffed_OrOutOfReach()
    {
        var (sim, _) = Build();
        int homes = sim.Execute(new BuildStationCommand(-60, -60));
        sim.Industries[0].Input[Workers] = 100f;
        sim.Step();
        Assert.Equal(1f, sim.StaffingOf(sim.Industries[0]));

        RunDays(sim, 0.05);

        Assert.Equal(0f, sim.Stations[homes].Waiting[Workers]);
    }

    [Fact]
    public void AWorkersBus_CarriesThemToTheIndustry_StaffsItAndEarnsTheFare()
    {
        var (sim, depot) = Build();
        int homes = sim.Execute(new BuildStationCommand(-60, -60));
        int mill = sim.Execute(new BuildStationCommand(700, -60));
        int line = sim.Execute(new CreateLineCommand(new[] { homes, mill }));
        Assert.True(sim.Execute(new BuyVehicleCommand(depot, "workerbus", line)) >= 0);
        var industry = sim.Industries[0];
        sim.Step();
        float before = sim.StaffingOf(industry);

        float best = 0f;
        for (long t = 0; t < 4L * 2400; t++)
        {
            sim.Step();
            best = MathF.Max(best, sim.StaffOf(industry));
        }

        Assert.True(best > 10f, $"{best} workers at the mill at most");
        Assert.True(sim.Lines[line].Delivered > 20);
        Assert.True(sim.Lines[line].Income > 0);
        Assert.True(sim.StaffingOf(industry) > before || best > 10f);
    }

    [Fact]
    public void AFreightTruck_NeverTakesWorkers_AndAWorkersBusNeverTakesFreight()
    {
        var (sim, depot) = Build();
        int homes = sim.Execute(new BuildStationCommand(-60, -60));
        int mill = sim.Execute(new BuildStationCommand(700, -60));
        sim.Stations[homes].Waiting[Workers] = 50f;
        sim.Stations[homes].Waiting[Wood] = 50f;
        int truckLine = sim.Execute(new CreateLineCommand(new[] { homes, mill }));
        sim.Execute(new BuyVehicleCommand(depot, "truck", truckLine));

        RunDays(sim, 2);

        Assert.Equal(50f, sim.Stations[homes].Waiting[Workers]); // untouched by the truck
    }

    [Fact]
    public void StateWorksFoundedLater_NeedTheirOwnStaff()
    {
        var (sim, _) = Build();
        var plant = sim.Industries[0];

        Assert.True(sim.StaffingOf(plant) < 1f);
        Assert.Equal(0f, sim.StaffOf(plant));
    }

    [Fact]
    public void ThePowerPlant_WithoutStaff_BurnsCoalMoreSlowly_SoItGivesLessPower()
    {
        var tuning = Quick();
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, 5, IncidentFrequency.Off);
        var lots = TownGenerator.LotSites(tuning.Towns, 0, 0, 0, 1, 0, 0, 0, 0).ToList();
        var homes = new[] { new BuildingPlan(BuildingType.Home, 1, lots[0].X, lots[0].Z, lots[0].Facing) };
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", 0, 0, 0, 1, homes) }, new[] { new IndustryPlan(PowerPlant, 800, 0, 0) }));
        var plant = sim.Industries[0];
        plant.Input[Coal] = 400f;

        sim.Step();
        sim.Step();

        Assert.True(plant.Load < 0.5f, $"load {plant.Load}");
        plant.Input[Workers] = 60f;
        sim.Step();
        sim.Step();
        Assert.Equal(1f, plant.Load, 0.01f);
    }

    [Fact]
    public void WorkersAreInTheStateHash()
    {
        ulong Run(float workers)
        {
            var (sim, _) = Build();
            sim.Industries[0].Input[Workers] = workers;
            RunDays(sim, 0.1);
            return sim.StateHash();
        }

        Assert.Equal(Run(10f), Run(10f));
        Assert.NotEqual(Run(10f), Run(0f));
    }
}
