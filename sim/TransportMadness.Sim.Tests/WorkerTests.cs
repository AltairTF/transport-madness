using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Residents as workers (C11): an industry with jobs is a workplace of the trip groups, and its staffing is the share of its jobs they fill.</summary>
public class WorkerTests
{
    const int SteelMill = 9, PowerPlant = 15;
    const int Coal = 8, Ore = 9;

    /// <summary>A 300 s day: with a very short one the trips of the groups last longer than the day.</summary>
    static Tuning Quick(float carShare = 1f)
    {
        var t = Tuning.Load();
        return t with { Economy = t.Economy with { DayLength = 300f }, Trips = t.Trips with { CarShare = carShare } };
    }

    /// <summary>
    /// A street along z = 0 from x = -200 to 0 with a town of homes beside it (<paramref name="homes"/> homes of 20 people, a home with no road would stand
    /// empty), and a steel mill at <paramref name="millX"/>, 50 m beside the line of the street. No commerce, so the mill is the only workplace there is.
    /// With <paramref name="road"/> the street goes on to the mill; it is built after the mill so that it gets a spur to its access point.
    /// </summary>
    static (Simulation Sim, int Depot) Build(int homes = 2, float millX = 1000, float millZ = 50, Tuning? tuning = null, bool town = true, bool road = true)
    {
        tuning ??= Quick();
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, 5, IncidentFrequency.Off);
        var buildings = new List<BuildingPlan>();
        for (int k = 0; k < homes; k++) buildings.Add(new BuildingPlan(BuildingType.Home, 1, -100 - k * 30, 25, 0));
        var towns = town ? new[] { new TownPlan("Testville", -150, 0, buildings) } : Array.Empty<TownPlan>();
        sim.AddTowns(new WorldPlan(1, towns, new[] { new IndustryPlan(SteelMill, millX, millZ, 0) }));
        sim.Company.Money = 10_000_000;
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(-200, 0, 0, 0, RoadType.TwoLane, 14f)));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(-200, 0, 1f), DepotLevel.Simple));
        Assert.True(depot >= 0);
        if (road)
        {
            Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, millX + 10, 0, RoadType.TwoLane, 14f)));
            Assert.True(sim.AccessNodeOf(sim.Industries[0]) >= 0, "the road reaches the mill");
        }
        return (sim, depot);
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    /// <summary>What the mill makes in a day, run after one day has closed (so that its staffing is the people's).</summary>
    static double MillOutput(Simulation sim)
    {
        var mill = sim.Industries[0];
        RunDays(sim, 1.05);
        mill.Input[Ore] = 600f;
        mill.Input[Coal] = 600f;
        double before = mill.Produced;
        RunDays(sim, 1);
        return mill.Produced - before;
    }

    // ---- Jobs and staffing -----------------------------------------------------------------------------------------

    [Fact]
    public void ABigIndustry_HasJobs_AndTheSmallOnesHaveNone()
    {
        var t = Tuning.Load().Industries.Types;

        Assert.Equal(new[] { "steelmill", "engineworks", "truckplant", "powerplant", "antiaircraft" }, t.Where(x => x.Jobs > 0).Select(x => x.Id));
        Assert.Equal(40, t.Single(x => x.Id == "steelmill").Jobs);
    }

    [Fact]
    public void OnAMapWithoutTowns_NobodyIsNeeded_AndStaffingIsFull()
    {
        var (sim, _) = Build(town: false, road: false);

        RunDays(sim, 2);

        Assert.Equal(1f, sim.StaffingOf(sim.Industries[0]));
    }

    [Fact]
    public void BeforeTheFirstDayCloses_AnIndustryIsFullyStaffed()
    {
        var (sim, _) = Build(road: false);

        Assert.Equal(1f, sim.StaffingOf(sim.Industries[0]));
        RunDays(sim, 0.5);
        Assert.Equal(1f, sim.StaffingOf(sim.Industries[0]));
    }

    [Fact]
    public void AnIndustryWithJobs_IsAWorkplace_OfTheGroupsWithinReach()
    {
        var (sim, _) = Build(homes: 3);
        int place = Simulation.IndustryPlace;

        Assert.All(sim.TripGroups, g => Assert.Equal(place, g.Work));
        Assert.Equal(42, sim.JobsTakenAt(place));
        Assert.All(sim.TripGroups, g => Assert.Equal(0, g.WorkIndustry));
    }

    [Fact]
    public void AnIndustryOutOfReach_IsNotAWorkplace()
    {
        var reach = Tuning.Load().Trips.WorkRadius;
        var (sim, _) = Build(millX: reach + 400, road: false);

        Assert.All(sim.TripGroups, g => Assert.Equal(-1, g.Work));
        Assert.Equal(0, sim.JobsTakenAt(Simulation.IndustryPlace));
    }

    [Fact]
    public void AnIndustryWithoutJobs_IsNeverAWorkplace()
    {
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Quick());
        var sim = new Simulation(net, 5, IncidentFrequency.Off);
        var homes = new[] { new BuildingPlan(BuildingType.Home, 1, -100, 25, 0) };
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", -150, 0, homes) }, new[] { new IndustryPlan(3, 400, 50, 0) })); // a sawmill

        Assert.All(sim.TripGroups, g => Assert.Equal(-1, g.Work));
    }

    [Fact]
    public void GroupsByCar_FillTheJobs_AndTheStaffingIsTheShareFilledWhenTheDayCloses()
    {
        var (sim, _) = Build(homes: 1); // 14 people for 40 jobs
        var mill = sim.Industries[0];

        RunDays(sim, 1.2);

        Assert.Equal(14, sim.StaffOf(mill));
        Assert.Equal(0.35f, sim.StaffingOf(mill));
        Assert.Contains(sim.TripGroups, g => g.LastMode == TripMode.Car);
    }

    [Fact]
    public void WhenThePeopleOutnumberTheJobs_TheIndustryIsFullyStaffed()
    {
        var (sim, _) = Build(homes: 3); // 42 people for 40 jobs

        RunDays(sim, 1.2);

        Assert.Equal(1f, sim.StaffingOf(sim.Industries[0]));
        Assert.True(sim.StaffOf(sim.Industries[0]) >= 40);
    }

    [Fact]
    public void WithNoRoadAndNoLine_NobodyReachesAnIndustryOutOfWalkingDistance_AndItStopsWorking()
    {
        var (sim, _) = Build(road: false);

        RunDays(sim, 1.2);

        Assert.Equal(0, sim.StaffOf(sim.Industries[0]));
        Assert.Equal(0f, sim.StaffingOf(sim.Industries[0]));
        Assert.Equal(0.0, MillOutput(sim));
    }

    [Fact]
    public void ThePeopleWhoAreNotCarOwners_StayHome_WithoutALine()
    {
        var (sim, _) = Build(tuning: Quick(carShare: 0f));

        RunDays(sim, 1.2);

        Assert.Equal(0f, sim.StaffingOf(sim.Industries[0]));
    }

    [Fact]
    public void ANeighbouringIndustry_IsReachedOnFoot()
    {
        var (sim, _) = Build(homes: 2, millX: -70, millZ: 120, road: false); // 28 people, a hundred metres away

        RunDays(sim, 1.2);

        Assert.Equal(28, sim.StaffOf(sim.Industries[0]));
        Assert.Equal(0.7f, sim.StaffingOf(sim.Industries[0]));
        Assert.Contains(sim.TripGroups, g => g.LastMode == TripMode.Walk);
    }

    [Fact]
    public void AnIndustryMakesItsOutputTimesItsStaffing()
    {
        var (half, _) = Build(homes: 1);
        var (full, _) = Build(town: false, road: false);

        double made = MillOutput(half), fullMade = MillOutput(full);

        Assert.True(fullMade > 0.0);
        Assert.Equal(0.35, made / fullMade, 2);
    }

    // ---- By a line -------------------------------------------------------------------------------------------------

    [Fact]
    public void AStationBesideTheIndustry_AcceptsPassengers_AndOneBesideNothingDoesNot()
    {
        var (sim, _) = Build();
        int mill = sim.Execute(new BuildStationCommand(1000, 0));
        int homes = sim.Execute(new BuildStationCommand(-100, 0));
        int nowhere = sim.Execute(new BuildStationCommand(500, 0));

        Assert.True(sim.Accepts(sim.Stations[mill], 0));
        Assert.True(sim.Accepts(sim.Stations[homes], 0));
        Assert.False(sim.Accepts(sim.Stations[nowhere], 0));
    }

    [Fact]
    public void ALineThatEndsAtTheIndustry_CarriesTheGroupsWithoutACar_AndStaffsIt()
    {
        var (sim, depot) = Build(homes: 2, tuning: Quick(carShare: 0f));
        int homes = sim.Execute(new BuildStationCommand(-100, 0));
        int atMill = sim.Execute(new BuildStationCommand(1000, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { homes, atMill }));
        for (int k = 0; k < 3; k++) Assert.True(sim.Execute(new BuyVehicleCommand(depot, "bus", line)) >= 0);
        var industry = sim.Industries[0];

        int best = 0;
        for (int day = 0; day < 4; day++)
        {
            RunDays(sim, 1);
            best = Math.Max(best, sim.StaffOf(industry));
        }

        Assert.True(best >= 20, $"{best} reached the mill on the best day");
        Assert.True(sim.Stations[atMill].Yesterday.Alighted + sim.Stations[atMill].Today.Alighted > 0 || sim.Lines[line].Delivered > 0);
        Assert.True(sim.Lines[line].Income > 0);
        Assert.Contains(sim.TripGroups, g => g.LastMode == TripMode.Transit);
    }

    [Fact]
    public void ALineThatEndsAtTheIndustrysOwnStop_CarriesTheGroupsToTheirWorkplace_AndStaffsIt()
    {
        var (sim, depot) = Build(homes: 2, tuning: Quick(carShare: 0f));
        int homes = sim.Execute(new BuildStationCommand(-100, 0));
        int atMill = sim.Execute(new AddDockCommand(false, 0)); // the stop at the mill's access point (M6f), no station beside it
        Assert.True(atMill >= 0);
        int line = sim.Execute(new CreateLineCommand(new[] { homes, atMill }));
        for (int k = 0; k < 3; k++) Assert.True(sim.Execute(new BuyVehicleCommand(depot, "bus", line)) >= 0);
        var industry = sim.Industries[0];

        int best = 0;
        for (int day = 0; day < 4; day++)
        {
            RunDays(sim, 1);
            best = Math.Max(best, sim.StaffOf(industry));
        }

        Assert.True(best >= 20, $"{best} reached the mill on the best day");
        Assert.True(sim.Lines[line].Income > 0);
        Assert.Contains(sim.TripGroups, g => g.LastMode == TripMode.Transit);
    }

    [Fact]
    public void TheModeToAnIndustry_IsChosenLikeAnyOther_ByTheQuickestWay()
    {
        var (sim, depot) = Build(homes: 1, tuning: Quick(carShare: 1f));
        int home = sim.TripGroups[0].Home;
        int place = Simulation.IndustryPlace;
        Assert.Equal(TripMode.Car, sim.ModeFor(home, place, hasCar: true).Mode);
        Assert.Equal(TripMode.None, sim.ModeFor(home, place, hasCar: false).Mode);

        int homes = sim.Execute(new BuildStationCommand(-100, 0));
        int mill = sim.Execute(new BuildStationCommand(1000, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { homes, mill }));
        Assert.True(sim.Execute(new BuyVehicleCommand(depot, "bus", line)) >= 0);

        Assert.Equal(TripMode.Transit, sim.ModeFor(home, place, hasCar: false).Mode);
    }

    // ---- Founding, determinism -------------------------------------------------------------------------------------

    [Fact]
    public void ThePowerPlant_WithoutStaff_BurnsCoalMoreSlowly_SoItGivesLessPower()
    {
        var tuning = Quick();
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, 5, IncidentFrequency.Off);
        var homes = new[] { new BuildingPlan(BuildingType.Home, 1, -100, 25, 0) };
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", -150, 0, homes) }, new[] { new IndustryPlan(PowerPlant, 800, 0, 0) }));
        var plant = sim.Industries[0];
        plant.Input[Coal] = 400f;

        RunDays(sim, 1.2); // nobody can get there: no road
        Assert.Equal(0f, sim.StaffingOf(plant));
        plant.Input[Coal] = 400f;
        sim.Step();
        sim.Step();

        Assert.True(plant.Load < 0.05f, $"load {plant.Load}");
    }

    [Fact]
    public void Staffing_IsInTheStateHash_AndTheRunIsDeterministic()
    {
        ulong Run(float carShare)
        {
            var (sim, _) = Build(tuning: Quick(carShare));
            RunDays(sim, 1.5);
            return sim.StateHash();
        }

        Assert.Equal(Run(1f), Run(1f));
        Assert.NotEqual(Run(1f), Run(0f));
    }
}
