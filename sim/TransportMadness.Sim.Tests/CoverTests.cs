using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Civilians in a raid (C14): the siren, people on foot and in station queues taking cover, and casualties that really lower the people of the trip groups.</summary>
public class CoverTests
{
    const int Sawmill = 3;
    const float VX = -1500f, VZ = 1500f; // the villages stand in the south-west corner, the sawmill the flights go for near the middle of the map

    /// <summary>Shipped tuning with the enemy wanting the sawmill only (a long way from the villages), the aim exact and a building at the centre of a blast always destroyed.</summary>
    static Tuning Raiding(Func<BombingTuning, BombingTuning>? change = null, Func<TripsTuning, TripsTuning>? trips = null)
    {
        var t = Worlds.Late(Tuning.Load());
        var b = t.War.Bombing with
        {
            IndustryValues = new[] { new RaidIndustryValue { Type = "sawmill", Value = 1 } },
            DefaultIndustryValue = 0,
            GatewayValue = 0,
            TownValue = 0,
            StationValue = 0,
            DepotValue = 0,
            SignalValue = 0,
            AimError = 0f,
            Scatter = 0f,
            ScatterAlong = 0f,
            BuildingChance = 1f,
        };
        if (change != null) b = change(b);
        return t with { Trips = trips != null ? trips(t.Trips) : t.Trips, War = t.War with { Bombing = b } };
    }

    /// <summary>
    /// A bomber whose speed gives the people <paramref name="seconds"/> between the siren and the first bomb (the target and the way in do not depend on the speed,
    /// so a flight of another world with the same seed shows how far it is).
    /// </summary>
    static Func<BombingTuning, BombingTuning> Lead(float seconds, ulong seed = 5)
    {
        var probe = Village(Raiding(), seed);
        var flight = Launch(probe);
        return b => b with { Speed = (flight.TargetDistance - (b.BombsPerAircraft - 1) * 0.5f * b.BombSpacing) / seconds };
    }

    /// <summary>Lead times used below: walking 0.3 of the 130 m of a walk puts a person 39 m from the building they left and 47 s from the one they are going to.</summary>
    const float Plenty = 35f, Little = 20f, Enough = 60f;

    /// <summary>
    /// A road along x with two homes of 20 people and two shops close enough for people to walk between them (about 130 m); nothing else near, so that somebody
    /// half way is more than 60 m from the nearest building. A sawmill stands far off.
    /// </summary>
    static Simulation Village(Tuning tuning, ulong seed = 5, int extraHomes = 0)
    {
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, seed, IncidentFrequency.Off);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(VX - 200, VZ, VX + 200, VZ, RoadType.TwoLane, 14f)));
        var buildings = new List<BuildingPlan>
        {
            new(BuildingType.Home, 1, VX - 60, VZ + 25, 0), new(BuildingType.Home, 1, VX - 70, VZ - 25, 0),
            new(BuildingType.Commerce, 2, VX + 60, VZ - 25, 0), new(BuildingType.Commerce, 2, VX + 70, VZ + 25, 0),
        };
        for (int k = 0; k < extraHomes; k++) buildings.Add(new BuildingPlan(BuildingType.Home, 1, VX - 150 + k * 30, VZ + 25, 0));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", VX, VZ, 0, 1, buildings) }, new[] { new IndustryPlan(Sawmill, 500, -500, 0) }));
        return sim;
    }

    /// <summary>
    /// A road of 1600 m with three homes near a station at its west end, a shop near a station at its east end, a depot, a line between the stations and, when
    /// <paramref name="buses"/> is more than 0, as many buses; people there go by line or car, not on foot.
    /// </summary>
    static (Simulation Sim, int A, int C, int Line, int Depot) Transit(Tuning tuning, int buses, ulong seed = 4)
    {
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, seed, IncidentFrequency.Off);
        float x0 = VX - 200f;
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(x0, VZ, x0 + 1600, VZ, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(1, new[]
        {
            new TownPlan("Testville", x0 + 800, VZ, 0, 1, new[]
            {
                new BuildingPlan(BuildingType.Home, 1, x0 + 170, VZ + 25, 0),
                new BuildingPlan(BuildingType.Home, 1, x0 + 200, VZ - 25, 0),
                new BuildingPlan(BuildingType.Home, 1, x0 + 230, VZ + 25, 0),
                new BuildingPlan(BuildingType.Industry, 2, x0 + 1420, VZ + 25, 0),
                new BuildingPlan(BuildingType.Industry, 2, x0 + 1470, VZ - 25, 0),
                new BuildingPlan(BuildingType.Commerce, 2, x0 + 1450, VZ + 60, 0),
            }),
        }, new[] { new IndustryPlan(Sawmill, 500, -500, 0) }));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(x0, VZ, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(x0 + 200, VZ)), c = sim.Execute(new BuildStationCommand(x0 + 1450, VZ));
        Assert.True(depot >= 0 && a >= 0 && c >= 0);
        int line = sim.Execute(new CreateLineCommand(new[] { a, c }));
        for (int k = 0; k < buses; k++) Assert.True(sim.Execute(new BuyVehicleCommand(depot, "bus", line)) >= 0);
        return (sim, a, c, line, depot);
    }

    static Func<TripsTuning, TripsTuning> Cars(float share) => t => t with { CarShare = share };

    static void Step(Simulation sim, double seconds)
    {
        long ticks = (long)Math.Round(seconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    /// <summary>How many ticks a walk of a group takes (the rule of the simulation: the straight line times the detour over the walking speed).</summary>
    static long WalkTicks(Simulation sim, TripGroup g)
    {
        var a = sim.Buildings[g.Origin];
        var b = sim.Buildings[g.Destination];
        float d = MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
        return Math.Max(1, (long)MathF.Ceiling(d * sim.TripTuning.WalkDetour / sim.TripTuning.WalkSpeed / Simulation.StepSeconds));
    }

    /// <summary>Where a group on foot is now, on the straight line between the two buildings.</summary>
    static (float X, float Z) WalkerPoint(Simulation sim, TripGroup g)
    {
        float f = Math.Clamp((sim.Tick - g.DepartTick) / (float)WalkTicks(sim, g), 0f, 1f);
        var a = sim.Buildings[g.Origin];
        var b = sim.Buildings[g.Destination];
        return (a.X + (b.X - a.X) * f, a.Z + (b.Z - a.Z) * f);
    }

    /// <summary>Steps until a group is on foot and has done at least <paramref name="share"/> of its walk, and returns it (just after the step in which it got there).</summary>
    static TripGroup WalkerAt(Simulation sim, float share)
    {
        for (long t = 0; t < 60000; t++)
        {
            sim.Step();
            foreach (var g in sim.TripGroups)
                if (g.Travelling && g.Mode == TripMode.Walk && (sim.Tick - g.DepartTick) / (float)WalkTicks(sim, g) >= share) return g;
        }
        Assert.Fail("nobody walked");
        return null!;
    }

    static float Distance(float x0, float z0, float x1, float z1) => MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));

    static Flight Launch(Simulation sim)
    {
        sim.LaunchFlightForTest();
        return sim.Flights[^1];
    }

    /// <summary>Steps until the siren is off; returns the tick of the step in which the all-clear came.</summary>
    static long UntilAllClear(Simulation sim)
    {
        long at = sim.Tick;
        for (long t = 0; t < 40000 && sim.SirenOn; t++)
        {
            at = sim.Tick;
            sim.Step();
        }
        Assert.False(sim.SirenOn, "the flight never ended");
        return at;
    }

    static int People(Simulation sim) => sim.TripGroups.Sum(g => g.People);

    // ---- The siren ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFirstBombCannotLandBeforeTheFlightsLeadTime_WhichIsTheTimeToTheTargetLessAHalfStick()
    {
        var sim = Village(Raiding());
        var flight = Launch(sim);

        var b = sim.WarTuning.Bombing;
        float firstAlong = flight.TargetDistance - (b.BombsPerAircraft - 1) * 0.5f * b.BombSpacing;
        float seconds = (flight.FirstImpactTick - sim.Tick) * Simulation.StepSeconds;
        Assert.Equal(firstAlong / b.Speed, seconds, 0.2f);
        // And the middle bomb of the leader does land when the warning said, later than the first.
        Assert.True(seconds < flight.TargetDistance / b.Speed);
        long firstLanding = long.MaxValue;
        for (long t = 0; t < 12000 && sim.Flights.Count > 0; t++)
        {
            sim.Step();
            if (sim.Impacts.Count > 0 && firstLanding == long.MaxValue) firstLanding = sim.Tick;
        }
        Assert.True(firstLanding >= flight.FirstImpactTick, $"{firstLanding} < {flight.FirstImpactTick}");
        Assert.True(firstLanding <= flight.FirstImpactTick + 4);
    }

    [Fact]
    public void PeopleOnFoot_RunToTheNearestBuildingTheyCanReachInTime_AndTheWalkIsHeldThere()
    {
        var sim = Village(Raiding(Lead(Plenty)));
        var g = WalkerAt(sim, 0.3f);
        var (x, z) = WalkerPoint(sim, g);
        int people = g.People;

        var flight = Launch(sim);

        var cover = Assert.Single(sim.ShelteredOf(g.Id));
        Assert.Equal(people, cover.People);
        Assert.False(cover.Queued);
        Assert.Equal(sim.Tick, cover.Since);
        float nearest = sim.Buildings.Where(b => b.State == BuildingState.Active).Min(b => Distance(b.X, b.Z, x, z));
        Assert.Equal(nearest, Distance(sim.Buildings[cover.Building].X, sim.Buildings[cover.Building].Z, x, z), 0.5f);
        // They could reach it in time: the walk there takes less than the time between the siren and the first bomb, less the time to start running.
        float walk = nearest * sim.TripTuning.WalkDetour / sim.TripTuning.WalkSpeed;
        Assert.True(walk <= (flight.FirstImpactTick - sim.Tick) * Simulation.StepSeconds - sim.WarTuning.Civilians.ReactionSeconds);
        Assert.True(g.Travelling); // still on their trip: they are late, not home
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.StartsWith("The sirens sound:") && e.Text.Contains("taking cover"));
        Assert.True(sim.PeopleSheltered >= people);
        Assert.True(sim.SirenOn);
    }

    [Fact]
    public void PeopleTooFarFromAnyBuilding_WalkOnInTheOpen_AndTheSirenTellsThePlayer()
    {
        var sim = Village(Raiding(Lead(Little)));
        var g = WalkerAt(sim, 0.3f);
        var (x, z) = WalkerPoint(sim, g);

        var flight = Launch(sim);

        // Twenty seconds, five of them before anybody starts to run: fifteen seconds is 29 m at the walking pace, and the nearest building is 39 m away.
        Assert.Equal(Little, (flight.FirstImpactTick - sim.Tick) * Simulation.StepSeconds, 0.5f);
        Assert.True(sim.Buildings.Min(b => Distance(b.X, b.Z, x, z)) > 15f * sim.TripTuning.WalkSpeed / sim.TripTuning.WalkDetour);
        Assert.Empty(sim.ShelteredOf(g.Id));
        Assert.True(g.Travelling);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.Contains("cannot reach a building in time"));
    }

    [Fact]
    public void PeopleWhoWouldBeIndoorsAtTheEndOfTheirWalkBeforeTheBombsCanFall_GoOnAndAreNotHeld()
    {
        var sim = Village(Raiding(Lead(Enough)));
        var g = WalkerAt(sim, 0.3f);
        long left = WalkTicks(sim, g) - (sim.Tick - g.DepartTick);

        var flight = Launch(sim);

        Assert.True(left * Simulation.StepSeconds < (flight.FirstImpactTick - sim.Tick) * Simulation.StepSeconds - 5f); // 47 s to go, 55 s to spare
        Assert.Empty(sim.ShelteredOf(g.Id));
        for (long t = 0; t <= left + 2; t++) sim.Step();
        Assert.False(g.Travelling);
        Assert.True(sim.SirenOn);
    }

    [Fact]
    public void PeopleWaitingInAStationQueue_TakeCoverAndRejoinTheQueueAtTheAllClear()
    {
        var (sim, a, _, _, _) = Transit(Raiding(Lead(Plenty), Cars(0f)), buses: 1);
        QueueUp(sim, a, 1);
        int queued = sim.Stations[a].Queue.Sum(l => l.Count);

        Launch(sim);

        Assert.Empty(sim.Stations[a].Queue);
        Assert.Equal(0f, sim.Stations[a].Waiting[0]);
        var lots = sim.Shelters.Where(s => s.Queued).ToList();
        Assert.Equal(queued, lots.Sum(s => s.People));
        Assert.All(lots, s => Assert.Equal(BuildingState.Active, sim.Buildings[s.Building].State));
        UntilAllClear(sim);
        Assert.Empty(sim.Shelters);
        Assert.Equal(queued, sim.Stations[a].Queue.Sum(l => l.Count));
        Assert.Equal(queued, (int)sim.Stations[a].Waiting[0]);
        Assert.Equal(0, sim.Stations[a].Today.GaveUp);
    }

    /// <summary>Steps until at least <paramref name="people"/> are waiting at a station for a bus that will not come (the one there was is sold once the first have joined the queue, after they chose to ride).</summary>
    static void QueueUp(Simulation sim, int station, int people)
    {
        bool sold = false;
        for (long t = 0; t < 60000 && sim.Stations[station].Queue.Sum(l => l.Count) < people; t++)
        {
            sim.Step();
            if (sold || sim.Stations[station].Queue.Count == 0) continue;
            sim.Execute(new SellVehicleCommand(Enumerable.Range(0, sim.VehicleCount).First(sim.IsCompanyVehicle)));
            sold = true;
        }
        Assert.True(sim.Stations[station].Queue.Sum(l => l.Count) >= people, "nobody queued");
    }

    [Fact]
    public void PeopleInCarsKeepDriving_AndGroupsThatAreToLeaveByCarDoSo()
    {
        var (sim, _, _, _, _) = Transit(Raiding(Lead(Plenty), Cars(1f)), buses: 0);
        for (long t = 0; t < 20000 && sim.TripVehicleCount == 0; t++) sim.Step();
        Assert.True(sim.TripVehicleCount > 0, "no car on the road");
        var driving = sim.TripGroups.Where(g => g.Travelling && g.Mode == TripMode.Car).Select(g => g.Id).ToHashSet();

        Launch(sim);

        Assert.All(sim.Shelters, s => Assert.DoesNotContain(s.Group, driving));
        Assert.Equal(0, sim.PeopleSheltered);
        // The cars go on, and the groups that were still at home in the morning set out by car with the siren on.
        for (long t = 0; t < 4000 && sim.TripGroups.Count(g => g.Travelling && g.Mode == TripMode.Car) <= driving.Count; t++) sim.Step();
        Assert.True(sim.SirenOn);
        Assert.True(sim.TripGroups.Count(g => g.Travelling && g.Mode == TripMode.Car) > driving.Count, "no other group set out by car");
        Assert.Equal(0, sim.PeopleSheltered);
    }

    [Fact]
    public void AGroupThatIsToLeaveOnFootWhileTheSirenIsOn_StaysInAndLooksAgain_UntilTheAllClear()
    {
        var sim = Village(Raiding(Lead(Plenty)));
        long first = sim.TripGroups.Min(g => g.NextTick);
        while (sim.Tick < first - 5) sim.Step();
        var due = sim.TripGroups.First(g => g.NextTick == first);

        Launch(sim);
        Step(sim, 1);
        Assert.False(due.Travelling);
        Assert.True(due.NextTick > sim.Tick);
        Step(sim, 20);
        Assert.False(due.Travelling); // the siren is still on
        Assert.True(sim.SirenOn);

        UntilAllClear(sim);
        for (long t = 0; t < 100 && !due.Travelling; t++) sim.Step(); // it looks again every few seconds

        Assert.True(due.Travelling, "the group stayed in after the all-clear");
    }

    // ---- The all-clear ---------------------------------------------------------------------------------------------

    [Fact]
    public void AtTheAllClear_ThePeopleInCoverGoOn_AndArriveLateByTheTimeTheySpentThere()
    {
        long Arrival(bool raid, out long sirenTick, out long clearTick, out Simulation sim)
        {
            sim = Village(Raiding(Lead(Plenty)));
            var g = WalkerAt(sim, 0.3f);
            sirenTick = sim.Tick;
            clearTick = sirenTick;
            if (raid)
            {
                Launch(sim);
                clearTick = UntilAllClear(sim);
                Assert.Empty(sim.Shelters);
                Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.StartsWith("All clear:"));
            }
            long safety = 0;
            while (g.Travelling && safety++ < 40000) sim.Step();
            Assert.False(g.Travelling);
            return sim.Tick;
        }

        long onTime = Arrival(false, out _, out _, out _);
        long late = Arrival(true, out long siren, out long clear, out var raided);

        Assert.True(clear > siren + 500, "the flight was over in no time");
        Assert.Equal(onTime + (clear - siren), late);
        Assert.Equal(0L, raided.Casualties);
    }

    [Fact]
    public void ThePeopleInCoverAreHashed_AndTheAllClearTakesThemOut()
    {
        var a = Village(Raiding(Lead(Plenty)));
        var b = Village(Raiding(Lead(Plenty)));
        var g = WalkerAt(a, 0.3f);
        WalkerAt(b, 0.3f);
        Assert.Equal(a.StateHash(), b.StateHash());
        ulong before = a.TripHash();

        Launch(a);
        Launch(b);

        Assert.NotEqual(before, a.TripHash());
        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.NotEmpty(a.ShelteredOf(g.Id));
    }

    // ---- Casualties ------------------------------------------------------------------------------------------------

    [Fact]
    public void PeopleInADestroyedHome_LoseTheShare_AndTheGroupsReallyShrink()
    {
        var sim = Village(Raiding());
        var home = sim.Buildings[0];
        var groups = sim.TripGroups.Where(g => g.Home == home.Id).ToList();
        Assert.Equal(2, groups.Count);
        int capacity = sim.TownTuning.Of(home.Type, home.Tier).People;
        Assert.Equal(capacity, groups.Sum(g => g.People));
        Assert.Equal(2 * capacity, People(sim));
        int residents = sim.ResidentsByTown()[0], population = sim.StatsOf(0).Population;
        Assert.Equal(population, residents);
        ulong hash = sim.TripHash();

        var flight = sim.BombForTest(home.X, home.Z);

        Assert.Equal(BuildingState.Demolished, home.State);
        int left = groups.Sum(g => g.People);
        int dead = capacity - left;
        Assert.InRange(dead, 6, 8);                // 35 % of 20, each group's 3.5 rounded by a draw
        Assert.All(groups, g => Assert.InRange(g.People, 6, 7));
        Assert.Equal(dead, sim.Casualties);
        Assert.Equal(dead, sim.Towns[0].Casualties);
        Assert.Equal(dead, flight.Casualties);
        Assert.Equal(residents - dead, sim.ResidentsByTown()[0]);
        Assert.Equal(population - capacity, sim.StatsOf(0).Population); // the buildings count what they have room for: the home is gone with the people who were still in it
        int workTaken = sim.TripGroups.Select(g => g.Work).Distinct().Sum(w => sim.JobsTakenAt(w));
        Assert.Equal(2 * capacity - dead, workTaken); // the jobs the dead held are free again
        Assert.NotEqual(hash, sim.TripHash());
    }

    [Fact]
    public void ABombOnAWorkplace_TakesTheShareOfThosePresent_AndNotOfThoseWhoWorkThereButAreAtHome()
    {
        var sim = Village(Raiding());
        TripGroup? present = null;
        for (long t = 0; t < 60000 && present == null; t++)
        {
            sim.Step();
            present = sim.TripGroups.FirstOrDefault(g => !g.Travelling && g.NextLeg == TripLeg.WorkBack && g.Work >= 0);
        }
        Assert.NotNull(present);
        var work = sim.Buildings[present!.Work];
        var snapshot = sim.TripGroups.ToDictionary(g => g.Id, g => g.People);
        var atWork = sim.TripGroups.Where(g => !g.Travelling && g.NextLeg == TripLeg.WorkBack && g.Work == work.Id).Select(g => g.Id).ToHashSet();
        var elsewhere = sim.TripGroups.Where(g => g.Work == work.Id && !g.Travelling && g.NextLeg != TripLeg.WorkBack).ToList();

        sim.BombForTest(work.X, work.Z);

        Assert.Equal(BuildingState.Demolished, work.State);
        Assert.All(atWork, id => Assert.InRange(snapshot[id] - sim.TripGroups[id].People, 3, 4)); // 35 % of 10
        Assert.All(elsewhere, g => Assert.Equal(snapshot[g.Id], g.People));
        Assert.Equal(sim.Casualties, snapshot.Sum(kv => kv.Value) - People(sim)); // people walking up to the building were in the blast too, and died with their share
        Assert.True(sim.Casualties >= atWork.Sum(id => snapshot[id] - sim.TripGroups[id].People));
    }

    [Fact]
    public void PeopleOnFootInsideTheBlast_LoseTheShare_AndTheSurvivorsArrive()
    {
        var sim = Village(Raiding());
        var g = WalkerAt(sim, 0.5f);
        var others = sim.TripGroups.Where(o => o != g).ToDictionary(o => o.Id, o => o.People);
        var (x, z) = WalkerPoint(sim, g);
        Assert.True(sim.Buildings.Min(b => Distance(b.X, b.Z, x, z)) > sim.WarTuning.Bombing.BlastRadius); // in the open: no building is hit
        int people = g.People;

        var flight = sim.BombForTest(x, z);

        int dead = people - g.People;
        Assert.InRange(dead, 3, 4);
        Assert.Equal(0, flight.Buildings);
        Assert.Equal(sim.Casualties, people + others.Sum(o => o.Value) - People(sim)); // whoever else was walking through the same blast lost people too
        Assert.True(sim.Casualties >= dead);
        Assert.Equal(sim.Casualties, sim.Towns[0].Casualties);
        Assert.Equal(g.People, g.Pending);
        Assert.True(g.Travelling);
        foreach (var o in sim.TripGroups.Where(o => o != g && !o.Travelling)) Assert.Equal(others[o.Id], o.People); // nobody else was there
        long safety = 0;
        while (g.Travelling && safety++ < 20000) sim.Step();
        Assert.False(g.Travelling);
        Assert.Equal(g.People, g.Arrived);
        Assert.Equal(people - dead, g.People);
    }

    [Fact]
    public void PeopleInCoverOutsideTheBlast_LoseNothing_WhereverTheBombsFall()
    {
        var sim = Village(Raiding(Lead(Plenty)));
        var g = WalkerAt(sim, 0.3f);
        var (x, z) = WalkerPoint(sim, g);
        Launch(sim);
        Assert.NotEmpty(sim.ShelteredOf(g.Id));
        int people = g.People;
        var cover = sim.Buildings[sim.ShelteredOf(g.Id)[0].Building];
        Assert.True(Distance(cover.X, cover.Z, x, z) > sim.WarTuning.Bombing.BlastRadius);

        var flight = sim.BombForTest(x, z); // on the very spot where they were walking

        Assert.Equal(0, flight.Buildings);
        Assert.Equal(0L, sim.Casualties);
        Assert.Equal(people, g.People);
        Assert.Equal(people, sim.ShelteredOf(g.Id).Sum(s => s.People));
    }

    [Fact]
    public void PeopleInCoverInABuildingThatIsDestroyed_LoseTheShareLikeThoseWhoLiveThere()
    {
        var sim = Village(Raiding(Lead(Plenty)));
        var g = WalkerAt(sim, 0.3f);
        Launch(sim);
        var cover = Assert.Single(sim.ShelteredOf(g.Id));
        var building = sim.Buildings[cover.Building];
        int people = g.People;
        var before = sim.TripGroups.ToDictionary(o => o.Id, o => o.People);

        sim.BombForTest(building.X, building.Z);

        Assert.Equal(BuildingState.Demolished, building.State);
        int dead = people - g.People;
        Assert.InRange(dead, 3, 4);
        Assert.Equal(g.People, sim.ShelteredOf(g.Id).Sum(s => s.People)); // the survivors stay where they are until the all-clear
        Assert.True(g.Travelling);
        Assert.Equal(sim.Casualties, sim.TripGroups.Sum(o => (long)(before[o.Id] - o.People)));
        Assert.True(sim.Casualties >= dead);
        UntilAllClear(sim);
        long safety = 0;
        while (g.Travelling && safety++ < 20000) sim.Step();
        Assert.False(g.Travelling);
        Assert.Equal(g.People, g.Arrived);
    }

    [Fact]
    public void PeopleWaitingAtAStationThatIsBombed_LoseTheShare_AndTheQueueIsShorter()
    {
        var (sim, a, _, _, _) = Transit(Raiding(), buses: 1);
        QueueUp(sim, a, 8);
        int queued = sim.Stations[a].Queue.Sum(l => l.Count);
        var (sx, sz) = sim.PointOf(sim.Stations[a]);
        int people = People(sim);

        sim.BombForTest(sx, sz);

        int left = sim.Stations[a].Queue.Sum(l => l.Count);
        Assert.InRange(queued - left, (int)(queued * 0.35f) - sim.Stations[a].Queue.Count - 3, (int)(queued * 0.35f) + sim.Stations[a].Queue.Count + 3);
        Assert.True(left < queued);
        Assert.Equal(left, (int)sim.Stations[a].Waiting[0]);
        Assert.True(sim.IsShut(sim.Stations[a]));
        Assert.True(sim.Casualties >= queued - left);
        Assert.Equal(people - People(sim), (int)sim.Casualties);
        Assert.All(sim.TripGroups.Where(g => g.Travelling), g => Assert.True(g.Pending >= 0 && g.Pending <= g.People));
    }

    [Fact]
    public void ACarThatIsBombed_TakesItsPeopleFromTheGroup_AndTheOthersInTheGroupArrive()
    {
        var (sim, _, _, _, _) = Transit(Raiding(trips: Cars(1f)), buses: 0);
        int car = -1;
        float x = 0, z = 0;
        for (long t = 0; t < 30000 && car < 0; t++)
        {
            sim.Step();
            for (int v = 0; v < sim.VehicleCount && car < 0; v++)
            {
                if (sim.TripGroupOf(v) < 0 || sim.GetVehicle(v).Kind != VehicleKind.Car) continue;
                var state = sim.GetVehicle(v);
                (x, z, _) = sim.Network.LanePoint(state.Lane, MathF.Max(0f, state.Position - 2f));
                if (FarFromBuildings(sim, x, z)) car = v; // out on the road, where only the car is in the blast
            }
        }
        Assert.True(car >= 0, "no trip car out on the road");
        var g = sim.TripGroups[sim.TripGroupOf(car)];
        int people = People(sim), before = g.People;

        var flight = sim.BombForTest(x, z);

        Assert.True(flight.Wrecks >= 1);
        Assert.Equal(0, flight.Buildings);
        int dead = people - People(sim);
        Assert.Equal(sim.Casualties, dead);
        Assert.Equal(2 * flight.Wrecks, dead);   // carPeople (2) of each car, the rest of the five in it arrive
        Assert.True(g.People <= before - 2);
        long safety = 0;
        while (g.Travelling && safety++ < 60000) sim.Step();
        Assert.False(g.Travelling);
        Assert.True(g.Arrived > 0);
    }

    static bool FarFromBuildings(Simulation sim, float x, float z) => sim.Buildings.All(b => Distance(b.X, b.Z, x, z) > sim.WarTuning.Bombing.BlastRadius + 15f);

    [Fact]
    public void ABusThatIsBombed_TakesItsRidersFromTheirGroups()
    {
        var (sim, _, _, _, _) = Transit(Raiding(trips: Cars(0f)), buses: 2);
        int bus = -1;
        float x = 0, z = 0;
        for (long t = 0; t < 60000 && bus < 0; t++)
        {
            sim.Step();
            for (int v = 0; v < sim.VehicleCount && bus < 0; v++)
            {
                if (!sim.IsCompanyVehicle(v) || sim.GetFleetInfo(v).Load <= 0 || sim.GetFleetInfo(v).Loading) continue;
                var state = sim.GetVehicle(v);
                (x, z, _) = sim.Network.LanePoint(state.Lane, MathF.Max(0f, state.Position - 4f));
                if (FarFromBuildings(sim, x, z)) bus = v;
            }
        }
        Assert.True(bus >= 0, "no bus carried anybody out on the road");
        int load = sim.GetFleetInfo(bus).Load;
        int people = People(sim);

        sim.BombForTest(x, z);

        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(bus).Kind);
        Assert.Equal(load, people - People(sim));
        Assert.Equal((long)load, sim.Casualties);
        Assert.All(sim.TripGroups, g => Assert.True(g.Pending >= 0 && g.Pending <= g.People));
        Step(sim, 400);
    }

    // ---- Whole raids -----------------------------------------------------------------------------------------------

    [Fact]
    public void ARaidOnATownWithTripsInProgress_KillsPeopleForReal_AndLeavesNobodyStuck()
    {
        var t = Raiding(b => b with
        {
            FromMonth = 0,
            Gaps = new[] { new RaidGapTuning { Phase = "rearmament", MinDays = 1, MaxDays = 1 } },
            Aircraft = new[] { new RaidAircraftTuning { Phase = "rearmament", Min = 1, Max = 1 } },
            TownValue = 1,
            IndustryValues = Array.Empty<RaidIndustryValue>(),
        });
        t = t with { Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f } };
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), t), 5, IncidentFrequency.Normal);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(VX - 200, VZ, VX + 200, VZ, RoadType.TwoLane, 14f)));
        var buildings = new List<BuildingPlan>();
        for (int k = 0; k < 6; k++) buildings.Add(new BuildingPlan(BuildingType.Home, 1, VX - 100 + k * 30, VZ + 25 + (k % 2) * 8, 0));
        for (int k = 0; k < 4; k++) buildings.Add(new BuildingPlan(BuildingType.Commerce, 2, VX - 80 + k * 40, VZ - 28 - (k % 2) * 8, 0));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", VX, VZ, 0, 1, buildings) }));
        int start = People(sim);
        Assert.Equal(sim.StatsOf(0).Population, start);

        bool sheltered = false;
        for (long tick = 0; tick < 3L * 24000 && sim.Raids < 3; tick++)
        {
            sim.Step();
            if (sim.PeopleSheltered > 0) sheltered = true;
            foreach (var g in sim.TripGroups)
                Assert.True(g.People >= 0 && g.Pending >= 0 && (!g.Travelling || g.Pending <= g.People), $"group {g.Id}: people {g.People} pending {g.Pending} at tick {sim.Tick}");
        }
        Assert.True(sim.Raids >= 1);
        while (sim.SirenOn || sim.BombsFalling.Count > 0) sim.Step();
        bool quiet = false;
        for (long tick = 0; tick < 24000 && !quiet; tick++) // everybody who was on the road gets there: some time in the next day nobody is out
        {
            sim.Step();
            quiet = sim.TripGroups.All(g => !g.Travelling);
        }

        Assert.True(quiet, "somebody never got where they were going");
        Assert.True(sheltered, "the siren sent nobody to cover");
        Assert.True(sim.Casualties > 0);
        Assert.Equal(sim.Casualties, sim.Towns[0].Casualties);
        Assert.True(People(sim) <= start - sim.Casualties);
        Assert.Equal(0, sim.PeopleSheltered);
        Assert.All(sim.TripGroups, g => Assert.True(g.People >= 0));
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.StartsWith("All clear"));
    }

    [Fact]
    public void ARaidWithTripsIsDeterministic_AndAnotherSeedGivesAnotherCourse()
    {
        ulong Course(ulong seed)
        {
            var sim = Village(Raiding(Lead(Plenty)), seed, extraHomes: 4);
            WalkerAt(sim, 0.3f);
            Launch(sim);
            UntilAllClear(sim);
            Step(sim, 60);
            return sim.StateHash();
        }

        Assert.Equal(Course(5), Course(5));
        Assert.NotEqual(Course(5), Course(6));
    }

    [Fact]
    public void NothingChangesBeforeTheFirstSiren_ThePeopleOfTheGroupsAreNotInTheTripHashYet()
    {
        var sim = Village(Raiding());
        ulong before = sim.TripHash();
        sim.TripGroups[0].People -= 1; // a lost person that nobody was told of
        Assert.Equal(before, sim.TripHash());
    }

    [Fact]
    public void ABadCiviliansTuningIsRefused()
    {
        var t = Tuning.Load();
        foreach (var bad in new[] { t.War.Civilians with { ReactionSeconds = -1f }, t.War.Civilians with { RecheckSeconds = 0f } })
        {
            var ex = Record.Exception(() => (t.War with { Civilians = bad }).Validate(t));
            Assert.IsType<System.Text.Json.JsonException>(ex);
        }
        Assert.Null(Record.Exception(() => t.War.Validate(t)));
    }
}
