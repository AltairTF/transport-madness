using System.Diagnostics;
using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Trip groups: who they are, when they leave, how they travel, and what that does on the roads and on the company's lines.</summary>
public class TripTests
{
    const int Passengers = 0;

    static void Run(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static Tuning With(Func<TripsTuning, TripsTuning> change)
    {
        var tuning = Tuning.Load();
        return tuning with { Trips = change(tuning.Trips) };
    }

    static Terrain DryLevelMap() => Terrain.Create(512, 8f, (_, _) => 5f);

    /// <summary>
    /// A level dry map with one road along x from 0 to 1600. Three tall homes (60 people) stand beside it near x = 200, two
    /// factories and a shop near x = 1450. Stations at 200, 800 and 1450, a depot at the west end.
    /// </summary>
    static (Simulation Sim, int Depot, int A, int B, int C) Corridor(Tuning? tuning = null)
    {
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, 4);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1600, 0, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(1, new[]
        {
            new TownPlan("Testville", 800, 0, 0, 1, new[]
            {
                new BuildingPlan(BuildingType.Home, 1, 170, 25, 0),
                new BuildingPlan(BuildingType.Home, 1, 200, -25, 0),
                new BuildingPlan(BuildingType.Home, 1, 230, 25, 0),
                new BuildingPlan(BuildingType.Industry, 2, 1420, 25, 0),
                new BuildingPlan(BuildingType.Industry, 2, 1470, -25, 0),
                new BuildingPlan(BuildingType.Commerce, 2, 1450, 60, 0),
            }),
        }));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(200, 0));
        int b = sim.Execute(new BuildStationCommand(800, 0));
        int c = sim.Execute(new BuildStationCommand(1450, 0));
        Assert.True(depot >= 0 && a >= 0 && b >= 0 && c >= 0);
        return (sim, depot, a, b, c);
    }

    static Simulation GeneratedTowns(ulong seed, int towns, Tuning? tuning = null)
    {
        var net = new RoadNetwork(Terrain.Generate(seed), tuning);
        var plan = TownGenerator.Generate(net, seed, towns);
        var sim = new Simulation(net, seed);
        sim.AddTowns(plan);
        return sim;
    }

    // ---- The groups ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheGroupsOfAHome_AddUpToItsPeople_InGroupsNoBiggerThanTheTuningAllows()
    {
        var (sim, _, _, _, _) = Corridor();
        int size = sim.TripTuning.GroupSize;

        var groups = sim.TripGroups.Where(g => g.Home == 0).ToList();

        Assert.Equal(sim.TownTuning.Of(BuildingType.Home, 1).People, groups.Sum(g => g.People));
        Assert.All(groups, g => Assert.InRange(g.People, 1, size));
        Assert.Equal(sim.Towns.Sum(t => sim.StatsOf(t.Id).Population), sim.TripGroups.Sum(g => g.People));
    }

    [Fact]
    public void EveryGroup_HasAWorkplaceWithJobsAndACommerceBuildingToVisit()
    {
        var sim = GeneratedTowns(5, 4);

        Assert.NotEmpty(sim.TripGroups);
        foreach (var g in sim.TripGroups)
        {
            Assert.Equal(BuildingType.Home, sim.Buildings[g.Home].Type);
            Assert.True(g.Work >= 0 && g.Leisure >= 0, $"group {g.Id} has no place to go");
            var work = sim.Buildings[g.Work];
            Assert.True(sim.TownTuning.Of(work.Type, work.Tier).Jobs > 0);
            Assert.Equal(BuildingType.Commerce, sim.Buildings[g.Leisure].Type);
        }
    }

    [Fact]
    public void Workplaces_AreChosenByNearness_SoMostGroupsWorkInTheShopNextDoor()
    {
        var net = new RoadNetwork(DryLevelMap());
        var sim = new Simulation(net, 2);
        sim.Execute(new BuildRoadCommand(0, 0, 1600, 0, RoadType.TwoLane, 14f));
        var buildings = new List<BuildingPlan>();
        for (int k = 0; k < 10; k++) buildings.Add(new BuildingPlan(BuildingType.Home, 1, 100 + k * 5, 25, 0));
        buildings.Add(new BuildingPlan(BuildingType.Commerce, 2, 180, -25, 0)); // near
        buildings.Add(new BuildingPlan(BuildingType.Commerce, 2, 1500, -25, 0)); // far
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", 800, 0, 0, 1, buildings) }));

        int near = sim.TripGroups.Count(g => g.Work == 10);
        int far = sim.TripGroups.Count(g => g.Work == 11);

        Assert.Equal(sim.TripGroups.Count, near + far);
        Assert.True(near > far, $"{near} near, {far} far");
    }

    [Fact]
    public void ThePassengerSupplyOfStations_IsGone_NothingAppearsWithoutAGroupSettingOut()
    {
        var (sim, _, a, _, _) = Corridor();

        Run(sim, 0.2);

        Assert.Equal(0f, sim.Stations[a].Waiting[Passengers]);
    }

    // ---- The day ------------------------------------------------------------------------------------------------

    /// <summary>Steps through days and notes, for every group that sets out, the time of day and which trip it is.</summary>
    static List<(TripLeg Leg, float TimeOfDay, long Day, TripMode Mode)> Departures(Simulation sim, double days)
    {
        var found = new List<(TripLeg, float, long, TripMode)>();
        var was = new bool[sim.TripGroups.Count];
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++)
        {
            sim.Step();
            foreach (var g in sim.TripGroups)
            {
                if (g.Travelling && !was[g.Id]) found.Add((g.NextLeg, sim.TimeOfDay, sim.Day, g.Mode));
                was[g.Id] = g.Travelling;
            }
        }
        return found;
    }

    [Fact]
    public void TheTripsToWorkAndBack_StartInsideTheirWindows_AndNobodyTravelsAtNight()
    {
        var sim = GeneratedTowns(5, 3);
        var trips = sim.TripTuning;
        float slack = trips.JitterMinutes / 60f / 24f + 0.002f;

        var departures = Departures(sim, 2.0);

        Assert.NotEmpty(departures);
        foreach (var (leg, time, day, _) in departures)
        {
            // Later days can start late: a slow evening pushes the next morning back.
            if (day > 0 || (leg != TripLeg.WorkOut && leg != TripLeg.WorkBack)) continue;
            var w = trips.Windows.Of((int)leg);
            Assert.InRange(time, w.From / 24f - 0.002f, w.To / 24f + slack);
        }
        Assert.DoesNotContain(departures, d => d.Day == 0 && d.TimeOfDay < 6f / 24f);
        Assert.Contains(departures, d => d.Leg == TripLeg.WorkOut);
        Assert.Contains(departures, d => d.Leg == TripLeg.WorkBack);
        Assert.Contains(departures, d => d.Leg == TripLeg.LeisureOut);
        Assert.Contains(departures, d => d.Leg == TripLeg.LeisureBack);
    }

    [Fact]
    public void TheSchedule_IsTheSameEveryDayForAGroup_AndDiffersBetweenGroups()
    {
        var sim = GeneratedTowns(5, 3);

        var morning = Departures(sim, 1.0).Where(d => d.Leg == TripLeg.WorkOut).Select(d => Math.Round(d.TimeOfDay, 4)).ToList();

        Assert.True(morning.Distinct().Count() > morning.Count / 4, "groups should not all leave at the same moment");
    }

    // ---- How they travel ---------------------------------------------------------------------------------------

    [Fact]
    public void NeighboursWalk_AndArriveAfterTheWalkingTime()
    {
        var net = new RoadNetwork(DryLevelMap());
        var sim = new Simulation(net, 3);
        sim.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        sim.AddTowns(new WorldPlan(1, new[]
        {
            new TownPlan("Testville", 200, 0, 0, 1, new[]
            {
                new BuildingPlan(BuildingType.Home, 0, 100, 25, 0),
                new BuildingPlan(BuildingType.Commerce, 1, 200, -25, 0),
            }),
        }));
        var group = sim.TripGroups[0];
        Assert.Equal(1, group.Work);

        var (mode, seconds) = sim.ModeFor(0, 1, hasCar: true);

        Assert.Equal(TripMode.Walk, mode);
        float distance = MathF.Sqrt(100 * 100 + 50 * 50);
        Assert.Equal(distance * sim.TripTuning.WalkDetour / sim.TripTuning.WalkSpeed, seconds, 0.5f);

        Run(sim, 0.6); // morning trip is done by 12:00
        Assert.False(group.Travelling);
        Assert.Equal(TripMode.Walk, group.LastMode);
        Assert.Equal(seconds, group.LastTripSeconds, 0.5f);
    }

    [Fact]
    public void WithoutALine_APersonWithoutACarCannotTravelFar_AndOneWithACarDrives()
    {
        var (sim, _, _, _, _) = Corridor();

        Assert.Equal(TripMode.None, sim.ModeFor(0, 3, hasCar: false).Mode);
        var (mode, seconds) = sim.ModeFor(0, 3, hasCar: true);

        Assert.Equal(TripMode.Car, mode);
        Assert.InRange(seconds, 60f, 600f);
    }

    [Fact]
    public void ALineBetweenTwoStations_GivesPublicTransportToThosePeople_AndRemovingItTakesItAway()
    {
        var (sim, depot, a, b, c) = Corridor();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b, c }));
        // A line with no vehicle brings nobody.
        Assert.Equal(TripMode.None, sim.ModeFor(0, 3, hasCar: false).Mode);

        sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        var (mode, seconds) = sim.ModeFor(0, 3, hasCar: false);

        Assert.Equal(TripMode.Transit, mode);
        Assert.True(seconds > sim.TripTuning.TransitWaitSeconds / 2f);

        sim.Execute(new DeleteLineCommand(line));
        Assert.Equal(TripMode.None, sim.ModeFor(0, 3, hasCar: false).Mode);
        Assert.Equal(TripMode.Car, sim.ModeFor(0, 3, hasCar: true).Mode);
    }

    [Fact]
    public void CuttingTheRoad_TakesTheCarAway_Too()
    {
        var (sim, _, _, _, _) = Corridor();
        Assert.Equal(TripMode.Car, sim.ModeFor(0, 3, hasCar: true).Mode);

        // The road is in pieces at the stations; remove the one in the middle.
        for (int r = 0; r < sim.Network.RoadCount; r++)
        {
            var road = sim.Network.GetRoad(new RoadId(r));
            var (x, _) = sim.Network.GetNode(road.From);
            if (!road.Retired && x >= 799f && x <= 801f) sim.Execute(new RemoveRoadCommand(r));
        }

        Assert.Equal(TripMode.None, sim.ModeFor(0, 3, hasCar: true).Mode);
    }

    [Fact]
    public void TheCheaperMode_Wins_WhenBothAreOpen()
    {
        var tuning = With(t => t with { CarShare = 1f });
        var (sim, depot, a, b, c) = Corridor(tuning);
        int line = sim.Execute(new CreateLineCommand(new[] { a, c }));
        for (int k = 0; k < 4; k++) sim.Execute(new BuyVehicleCommand(depot, "bus", line));

        // A car is quicker and cheaper on an empty road than waiting at a stop and paying a fare.
        Assert.Equal(TripMode.Car, sim.ModeFor(0, 3, hasCar: true).Mode);

        // Make the car cost much more than the bus and the group takes the bus.
        var dear = With(t => t with { CarCostPerKm = 5000f });
        var (sim2, depot2, a2, _, c2) = Corridor(dear);
        int line2 = sim2.Execute(new CreateLineCommand(new[] { a2, c2 }));
        for (int k = 0; k < 4; k++) sim2.Execute(new BuyVehicleCommand(depot2, "bus", line2));
        Assert.Equal(TripMode.Transit, sim2.ModeFor(0, 3, hasCar: true).Mode);
    }

    // ---- On the road ----------------------------------------------------------------------------------------------

    [Fact]
    public void TripCars_AppearOnTheRoadInTheMorning_AndTheCapHolds()
    {
        var tuning = With(t => t with { CarShare = 1f, MaxCars = 12, WalkRadius = 10f });
        var sim = GeneratedTowns(5, 3, tuning);

        int most = 0;
        for (long t = 0; t < 12000; t++)
        {
            sim.Step();
            most = Math.Max(most, sim.TripVehicleCount);
            Assert.True(sim.TripVehicleCount <= 12);
        }

        Assert.True(most > 4, $"only {most} trip cars at once");
    }

    [Fact]
    public void CarsAreOffTheRoadAgain_WhenTheirPeopleHaveArrived()
    {
        // A short timeout: a car caught in a jam is taken off after it, so the evening's last cars are gone by the morning.
        var tuning = With(t => t with { CarShare = 1f, LeisureChance = 0f, CarTimeoutSeconds = 150f });
        var sim = GeneratedTowns(5, 3, tuning);

        for (long t = 0; t < 12000 + 3000; t++) sim.Step(); // to 06:00 the next morning, before anyone sets out again

        var local = sim.TripGroups.Where(g => sim.Buildings[g.Work].Town == sim.Buildings[g.Home].Town).ToList();
        Assert.NotEmpty(local);
        Assert.All(local, g => Assert.NotEqual(TripMode.None, g.LastMode));
        Assert.Contains(sim.TripGroups, g => g.LastMode == TripMode.Car);
        Assert.Equal(0, sim.TripVehicleCount);
        Assert.All(sim.TripGroups, g => Assert.False(g.Travelling));
    }

    [Fact]
    public void ACarThatIsStillOnTheRoadAfterTheTimeout_IsTakenOff_AndItsPeopleCountAsArrived()
    {
        var tuning = With(t => t with { CarShare = 1f, CarTimeoutSeconds = 4f, LeisureChance = 0f });
        var (sim, _, _, _, _) = Corridor(tuning);

        Run(sim, 1.0);

        Assert.Equal(0, sim.TripVehicleCount);
        Assert.All(sim.TripGroups, g => Assert.False(g.Travelling));
        Assert.All(sim.TripGroups, g => Assert.Equal(TripMode.Car, g.LastMode));
    }

    // ---- On the company's lines ------------------------------------------------------------------------------

    [Fact]
    public void ALineThroughTheTownsMakesAProfit_FromPeopleOnTheirWayToWork()
    {
        var tuning = With(t => t with { CarShare = 0f });
        var (sim, depot, a, b, c) = Corridor(tuning);
        int line = sim.Execute(new CreateLineCommand(new[] { a, c }));
        sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        sim.Execute(new BuyVehicleCommand(depot, "bus", line));

        Run(sim, 1.3);

        Assert.True(sim.Lines[line].Delivered > 0, "no one rode");
        Assert.True(sim.Lines[line].Income > 0);
        Assert.Contains(sim.TripGroups, g => g.LastMode == TripMode.Transit);
        Assert.All(sim.TripGroups, g => Assert.Equal(TripMode.Transit, g.LastMode));
    }

    [Fact]
    public void Passengers_GetOffOnlyAtTheirDestination()
    {
        var tuning = With(t => t with { CarShare = 0f });
        var (sim, depot, a, b, c) = Corridor(tuning);
        int line = sim.Execute(new CreateLineCommand(new[] { a, b, c }));
        sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        sim.Execute(new BuyVehicleCommand(depot, "bus", line));

        bool passedTheMiddleStopWithPeopleOnBoard = false;
        for (long t = 0; t < 16000; t++)
        {
            sim.Step();
            for (int v = 0; v < sim.VehicleCount; v++)
            {
                if (!sim.IsCompanyVehicle(v)) continue;
                var info = sim.GetFleetInfo(v);
                if (!info.Loading) continue;
                int here = sim.Lines[info.Line].Stops[info.NextStop];
                var riders = sim.RidersOf(v);
                // Nobody who is still on board is bound for the stop where the bus is standing.
                Assert.False(riders.ContainsKey(here), $"bus {v} standing at station {here} still carries people bound for it");
                if (here == b && riders.ContainsKey(c)) passedTheMiddleStopWithPeopleOnBoard = true;
            }
        }

        Assert.True(passedTheMiddleStopWithPeopleOnBoard, "no one rode through the middle stop");
        Assert.True(sim.Lines[line].Delivered > 0);
    }

    [Fact]
    public void People_GiveUpWaitingAtAStation_WhenNoBusComes()
    {
        var tuning = With(t => t with { CarShare = 0f, TransitGiveUpSeconds = 100f });
        var (sim, depot, a, b, c) = Corridor(tuning);
        int line = sim.Execute(new CreateLineCommand(new[] { a, c }));
        sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        // The bus is sold while the people are choosing; they have already decided to ride.
        for (long t = 0; t < 4300; t++) sim.Step(); // past the first departures (about 07:00)
        var bus = Enumerable.Range(0, sim.VehicleCount).First(sim.IsCompanyVehicle);
        sim.Execute(new SellVehicleCommand(bus));

        Run(sim, 0.7);

        Assert.Empty(sim.Stations[a].WaitingFor());
        Assert.All(sim.TripGroups, g => Assert.False(g.Travelling));
    }

    // ---- Determinism, load ----------------------------------------------------------------------------------

    [Fact]
    public void ATownWorldWithTrips_RunsTheSameEveryTime()
    {
        ulong Hash(ulong seed)
        {
            var sim = GeneratedTowns(seed, 3, With(t => t with { CarShare = 0.8f, WalkRadius = 80f }));
            sim.SeedTraffic(10);
            Run(sim, 1.3);
            return sim.StateHash();
        }

        Assert.Equal(Hash(21), Hash(21));
        Assert.NotEqual(Hash(21), Hash(22));
    }

    [Fact]
    public void TripsReplayFromTheCommandLog()
    {
        var tuning = With(t => t with { CarShare = 0f });
        var (sim, depot, a, b, c) = Corridor(tuning);
        int line = sim.Execute(new CreateLineCommand(new[] { a, c }));
        sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Run(sim, 0.8);
        long tick = sim.Tick;

        var (fresh, _, _, _, _) = Corridor(tuning);
        // The world (roads, stations) was built through commands before the first step; replaying the later ones needs the same start.
        var replay = fresh;
        foreach (var logged in sim.CommandLog.Where(l => l.Tick > 0 || l.Command is CreateLineCommand or BuyVehicleCommand))
        {
            while (replay.Tick < logged.Tick) replay.Step();
            if (logged.Command is CreateLineCommand or BuyVehicleCommand) replay.Execute(logged.Command);
        }
        while (replay.Tick < tick) replay.Step();

        Assert.Equal(sim.StateHash(), replay.StateHash());
    }

    [Fact]
    public void ARealisticTownMap_StepsFastEnoughWithTripsRunning()
    {
        var sim = GeneratedTowns(5, 5);
        sim.SeedTraffic(20);
        // Run into the morning rush, when the most cars are being put on the road.
        for (long t = 0; t < 3300; t++) sim.Step();

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 400; i++) sim.Step();
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms for 400 steps with {sim.TripVehicleCount} trip cars");
    }
}
