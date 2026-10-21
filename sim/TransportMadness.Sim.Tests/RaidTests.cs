using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>The raids (C9d), made into flights of bombers with a stick of bombs each in C12.</summary>
public class RaidTests
{
    const int Sawmill = 3, SteelMill = 9, PowerPlant = 15;
    const int Wood = 2, Planks = 3, Steel = 10, Coal = 8;

    /// <summary>Values that make the enemy want one kind of target only (a sawmill, for an industry).</summary>
    static BombingTuning Wanting(BombingTuning b, RaidTargetKind kind) => b with
    {
        IndustryValues = kind == RaidTargetKind.Industry ? new[] { new RaidIndustryValue { Type = "sawmill", Value = 1 } } : Array.Empty<RaidIndustryValue>(),
        DefaultIndustryValue = 0,
        GatewayValue = kind == RaidTargetKind.Gateway ? 1 : 0,
        TownValue = kind == RaidTargetKind.Town ? 1 : 0,
        StationValue = kind == RaidTargetKind.Station ? 1 : 0,
        DepotValue = kind is RaidTargetKind.Depot or RaidTargetKind.RailDepot ? 1 : 0,
        SignalValue = kind == RaidTargetKind.Signal ? 1 : 0,
    };

    /// <summary>
    /// Flights from month 0, one aircraft every day, on the one kind of target, with the aim exact (no spread across or along, no error of the sight) so that a test
    /// can say where every bomb lands; a day of two minutes, so that one flight is over before the next comes.
    /// </summary>
    static Tuning Raiding(RaidTargetKind kind = RaidTargetKind.Industry, Func<BombingTuning, BombingTuning>? change = null, float dayLength = 120f)
    {
        var t = Worlds.Late(Tuning.Load());
        var b = Wanting(t.War.Bombing, kind) with
        {
            FromMonth = 0,
            Gaps = new[] { new RaidGapTuning { Phase = "rearmament", MinDays = 1, MaxDays = 1 } },
            Aircraft = new[] { new RaidAircraftTuning { Phase = "rearmament", Min = 1, Max = 1 } },
            AimError = 0f,
            Scatter = 0f,
            ScatterAlong = 0f,
            BuildingChance = 1f,
        };
        if (change != null) b = change(b);
        return t with { Economy = t.Economy with { DayLength = dayLength }, Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f }, War = t.War with { Bombing = b } };
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

    /// <summary>The same region with the shipped bomb, a building always destroyed at the centre of a blast and the frequency off, for tests that drop a bomb where they want it.</summary>
    static Simulation Bombed(Func<BombingTuning, BombingTuning>? change = null, ulong seed = 5)
    {
        var t = Worlds.Late(Tuning.Load());
        var b = t.War.Bombing with { BuildingChance = 1f };
        if (change != null) b = change(b);
        return Region(t with { Economy = t.Economy with { DayLength = 60f }, War = t.War with { Bombing = b } }, IncidentFrequency.Off, seed);
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static bool StepUntil(Simulation sim, double seconds, Func<bool> done)
    {
        long ticks = (long)Math.Round(seconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++)
        {
            if (done()) return true;
            sim.Step();
        }
        return done();
    }

    /// <summary>Steps until a flight has been launched and returns it.</summary>
    static Flight Launch(Simulation sim)
    {
        Assert.True(StepUntil(sim, 600, () => sim.Flights.Count > 0), "no flight came");
        return sim.Flights[0];
    }

    /// <summary>Steps until the flight is over (it has left the map and its last bomb has landed).</summary>
    static void Fly(Simulation sim)
    {
        Assert.True(StepUntil(sim, 600, () => sim.Flights.Count == 0 && sim.BombsFalling.Count == 0), "the flight never ended");
    }

    static float Distance(float x0, float z0, float x1, float z1) => MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));

    // ---- When ------------------------------------------------------------------------------------------------------

    [Fact]
    public void NoFlightComesBeforeJanuary1942_OrWithTheFrequencyOff()
    {
        var shipped = Worlds.Late(Tuning.Load());
        var early = Region(shipped with { Economy = shipped.Economy with { DayLength = 60f } });
        RunDays(early, 3 * 47);
        Assert.Equal(0, early.Raids);
        Assert.Equal(-1L, early.NextRaidTick);
        Assert.Empty(early.Flights);

        var off = Region(Raiding(), IncidentFrequency.Off);
        RunDays(off, 30);
        Assert.Equal(0, off.Raids);
        Assert.Empty(off.Flights);
    }

    [Fact]
    public void TheFlightsComeAtTheGapOfThePhase_AndTheFrequencyOptionScalesIt()
    {
        int Count(IncidentFrequency f)
        {
            var sim = Region(Raiding(RaidTargetKind.Town, b => b with { Gaps = new[] { new RaidGapTuning { Phase = "rearmament", MinDays = 4, MaxDays = 4 } } }, dayLength: 60f), f);
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
    public void TheShippedNumbers_AreSixToTwelveDaysInTotalWar_OneToFourAircraft_AndACrossingOfTheMapTakesAboutAMinute()
    {
        var sim = Region(Worlds.Late(Tuning.Load()));
        var b = sim.WarTuning.Bombing;

        Assert.Equal((6, 12), (b.GapOf("totalwar").MinDays, b.GapOf("totalwar").MaxDays));
        Assert.Equal(48, b.FromMonth);
        Assert.Equal((1, 4), (b.AircraftOf("totalwar").Min, b.AircraftOf("totalwar").Max));
        Assert.Equal((2, 6), (b.AircraftOf("end").Min, b.AircraftOf("end").Max));
        Assert.InRange(4096f / b.Speed, 40f, 90f);
        Assert.InRange(4096f * MathF.Sqrt(2f) / b.Speed, 40f, 90f);
    }

    // ---- The side they come from -----------------------------------------------------------------------------------

    [Fact]
    public void TheSideTheAircraftComeFrom_IsDrawnOncePerGame_FromItsOwnStream()
    {
        var sides = Enumerable.Range(1, 24).Select(seed => Region(Worlds.Late(Tuning.Load()), IncidentFrequency.Off, (ulong)seed).RaidDirection).ToList();

        Assert.All(sides, d => Assert.InRange(d, 0, 7));
        Assert.True(sides.Distinct().Count() >= 5, "the sides should differ between games");
        Assert.Equal(Region(Worlds.Late(Tuning.Load()), IncidentFrequency.Off, 7).RaidDirection, Region(Worlds.Late(Tuning.Load()), IncidentFrequency.Off, 7).RaidDirection);
        Assert.Equal("north-east", Simulation.DirectionName(1));
    }

    [Fact]
    public void EveryFlightComesInOnTheDrawnSide_JustOutsideTheEdge_AndIsToldInAWarning()
    {
        var sim = Region(Raiding(RaidTargetKind.Town), IncidentFrequency.Normal, 11);
        float arc = sim.WarTuning.Bombing.EntryArc, margin = sim.WarTuning.Bombing.EntryMargin, half = sim.Network.Terrain!.HalfSize;
        var seen = new List<Flight>();

        for (int n = 0; n < 3; n++)
        {
            var flight = Launch(sim);
            seen.Add(flight);
            float bearing = MathF.Atan2(flight.EntryX, -flight.EntryZ); // from north, clockwise
            float wanted = sim.RaidDirection * MathF.PI * 0.25f;
            float off = MathF.Abs(MathF.IEEERemainder(bearing - wanted, MathF.Tau));
            Assert.True(off <= arc + 0.001f, $"the entry lies {off} rad from the drawn side");
            Assert.Equal(half + margin, MathF.Max(MathF.Abs(flight.EntryX), MathF.Abs(flight.EntryZ)), 0.5f);
            Fly(sim);
        }

        var warnings = sim.Events.Where(e => e.Kind == SimEventKind.Raid && e.Text.StartsWith("Air raid warning")).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, w => Assert.Contains($"1 aircraft from the {Simulation.DirectionName(sim.RaidDirection)}, arriving in ", w.Text));
        Assert.Equal(seen.Select(f => (int)MathF.Round(f.TargetDistance / f.Speed, MidpointRounding.AwayFromZero)), warnings.Select(w => int.Parse(w.Text.Split("arriving in ")[1].Split(' ')[0])));
        Assert.Equal(3, sim.Raids);
    }

    // ---- The flight ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheAircraftFlyStraight_AtTheSpeedOfTheTuning_FromTheEntryOverTheTargetToTheExit()
    {
        var sim = Region(Raiding(RaidTargetKind.Industry), IncidentFrequency.Normal);
        var flight = Launch(sim);
        float speed = sim.WarTuning.Bombing.Speed, step = speed * Simulation.StepSeconds;
        var mill = sim.Industries[0];
        Assert.Equal(RaidTargetKind.Industry, flight.TargetKind);
        Assert.Equal((mill.X, mill.Z), (flight.TargetX, flight.TargetZ));

        // The target lies on the line from the entry to the exit.
        float cross = (mill.X - flight.EntryX) * flight.DirZ - (mill.Z - flight.EntryZ) * flight.DirX;
        Assert.True(MathF.Abs(cross) < 0.01f, $"the target is {cross} m off the path");
        Assert.Equal(Distance(flight.EntryX, flight.EntryZ, flight.ExitX, flight.ExitZ), flight.Length, 0.5f);
        Assert.Equal(Distance(flight.EntryX, flight.EntryZ, mill.X, mill.Z), flight.TargetDistance, 0.01f);

        long start = sim.Tick;
        var last = sim.AircraftPoint(flight, 0);
        float over = float.MaxValue;
        int steps = 0;
        while (sim.Flights.Count > 0)
        {
            sim.Step();
            if (sim.Flights.Count == 0) break;
            var p = sim.AircraftPoint(flight, 0);
            Assert.Equal(step, Distance(last.X, last.Z, p.X, p.Z), 0.01f);
            float off = (p.X - flight.EntryX) * flight.DirZ - (p.Z - flight.EntryZ) * flight.DirX;
            Assert.True(MathF.Abs(off) < 0.05f, $"{off} m off the line");
            over = MathF.Min(over, Distance(p.X, p.Z, mill.X, mill.Z));
            last = p;
            steps++;
        }

        Assert.True(over < step, $"the aircraft came {over} m from the target");
        Assert.InRange(sim.Tick - start, (long)(flight.Length / step) - 4, (long)(flight.Length / step) + 4); // one aircraft: out when it has flown the length
    }

    [Fact]
    public void TheTargetIsToldInTheLastTenSeconds_NotBefore()
    {
        var sim = Region(Raiding(RaidTargetKind.Industry), IncidentFrequency.Normal);
        var flight = Launch(sim);
        Assert.True(flight.SecondsToTarget > 10f, "the test needs a target further than ten seconds away");

        while (sim.Flights.Count > 0)
        {
            Assert.Equal(flight.SecondsToTarget <= 10f, flight.TargetRevealed);
            Assert.Equal(flight.TargetRevealed ? 1 : 0, sim.Events.Count(e => e.Text.Contains("heading for the sawmill")));
            sim.Step();
        }

        var reveal = Assert.Single(sim.Events, e => e.Text.Contains("heading for the sawmill"));
        Assert.Equal(SimEventKind.Raid, reveal.Kind);
        Assert.Equal((600f, -30f), (reveal.X, reveal.Z));
        Assert.Contains("over it in", reveal.Text);
    }

    [Fact]
    public void TheMiddleBombOfTheLeadAircraftLandsWhenItArrives_AsTheWarningSaid()
    {
        var sim = Region(Raiding(RaidTargetKind.Industry), IncidentFrequency.Normal);
        var flight = Launch(sim);
        long arrives = flight.LaunchTick + (long)MathF.Round(flight.TargetDistance / flight.Speed / Simulation.StepSeconds);
        Fly(sim);

        Assert.Equal(5, sim.Impacts.Count);
        Assert.InRange(sim.Impacts[2].Tick, arrives - 2, arrives + 2);
        Assert.Equal((600f, -30f), (MathF.Round(sim.Impacts[2].X), MathF.Round(sim.Impacts[2].Z)));
    }

    [Fact]
    public void AFlightHasTheNumberOfAircraftOfItsPhase_InAFormation_AndEachDropsAStick()
    {
        var sim = Region(Raiding(RaidTargetKind.Industry, b => b with { Aircraft = new[] { new RaidAircraftTuning { Phase = "rearmament", Min = 3, Max = 3 } } }), IncidentFrequency.Normal);
        var flight = Launch(sim);
        var b = sim.WarTuning.Bombing;

        Assert.Equal(3, flight.Aircraft);
        Assert.Equal(3 * b.BombsPerAircraft, flight.BombsPlanned);
        var lead = sim.AircraftPoint(flight, 0);
        var second = sim.AircraftPoint(flight, 1);
        var third = sim.AircraftPoint(flight, 2);
        Assert.Equal(MathF.Sqrt(b.TrailSpacing * b.TrailSpacing + b.WingSpacing * b.WingSpacing), Distance(lead.X, lead.Z, second.X, second.Z), 0.01f);
        Assert.Equal(MathF.Sqrt(4 * b.TrailSpacing * b.TrailSpacing + b.WingSpacing * b.WingSpacing), Distance(lead.X, lead.Z, third.X, third.Z), 0.01f);
        // The second is on one side of the leader and the third on the other.
        float Across(Flight f, (float X, float Z) p) => (p.X - lead.X) * -f.DirZ + (p.Z - lead.Z) * f.DirX;
        Assert.True(Across(flight, second) * Across(flight, third) < 0f);

        Fly(sim);

        Assert.Equal(3 * b.BombsPerAircraft, sim.Bombs);
        Assert.Equal(sim.Bombs, sim.ImpactCount);
        Assert.Equal(1, sim.Raids);
    }

    [Fact]
    public void AStickIsBombsPerAircraft_BombSpacingApart_AlongThePath_CentredOnTheTarget()
    {
        var sim = Region(Raiding(RaidTargetKind.Industry), IncidentFrequency.Normal);
        var flight = Launch(sim);
        var b = sim.WarTuning.Bombing;
        Fly(sim);

        Assert.Equal(b.BombsPerAircraft, sim.Impacts.Count);
        for (int k = 0; k < b.BombsPerAircraft; k++)
        {
            float place = (k - (b.BombsPerAircraft - 1) * 0.5f) * b.BombSpacing;
            float ex = 600f - flight.DirX * 0f, ez = -30f;
            Assert.Equal(ex + flight.DirX * place, sim.Impacts[k].X, 0.05f);
            Assert.Equal(ez + flight.DirZ * place, sim.Impacts[k].Z, 0.05f);
        }
        Assert.Equal(b.BombSpacing, Distance(sim.Impacts[0].X, sim.Impacts[0].Z, sim.Impacts[1].X, sim.Impacts[1].Z), 0.05f);
    }

    [Fact]
    public void ABombFallsFromTheFlyingHeightToItsImpactPointInTheTimeOfTheFall()
    {
        var sim = Region(Raiding(RaidTargetKind.Industry), IncidentFrequency.Normal);
        var flight = Launch(sim);
        var b = sim.WarTuning.Bombing;
        Assert.True(StepUntil(sim, 120, () => sim.BombsFalling.Count > 0));
        var bomb = sim.BombsFalling[0];
        int fall = (int)MathF.Round(MathF.Sqrt(2f * b.Altitude / 9.81f) / Simulation.StepSeconds);

        Assert.Equal(fall, bomb.ImpactTick - bomb.ReleaseTick);
        Assert.Equal(b.Altitude, bomb.Altitude);
        Assert.Equal(b.Altitude, sim.BombPoint(bomb).Height, 0.5f);
        var release = sim.AircraftPoint(flight, 0);
        Assert.Equal((bomb.FromX, bomb.FromZ), (release.X, release.Z));
        float before = float.MaxValue, last = b.Altitude;
        while (sim.BombsFalling.Count > 0 && sim.BombsFalling[0] == bomb)
        {
            var p = sim.BombPoint(bomb);
            Assert.True(p.Height <= last + 0.001f, "it only falls");
            last = p.Height;
            before = p.Height;
            sim.Step();
        }
        Assert.True(before < 0.2f * b.Altitude, "it was close to the ground on its last step");
        Assert.Equal(0f, sim.BombPoint(bomb).Height, 0.5f);
        Assert.Equal((bomb.ImpactX, bomb.ImpactZ), (sim.BombPoint(bomb).X, sim.BombPoint(bomb).Z));
        Assert.Contains(sim.Impacts, i => i.Tick == bomb.ImpactTick && i.X == bomb.ImpactX && i.Z == bomb.ImpactZ);
    }

    [Fact]
    public void TheScatterIsDrawnFromTheFlightsOwnStream_InAFixedOrder()
    {
        var wide = Raiding(RaidTargetKind.Industry, b => b with { Scatter = 20f, ScatterAlong = 15f, AimError = 40f, Aircraft = new[] { new RaidAircraftTuning { Phase = "rearmament", Min = 2, Max = 2 } } });
        List<(float, float)> Landings(ulong seed)
        {
            var sim = Region(wide, IncidentFrequency.Normal, seed);
            Launch(sim);
            Fly(sim);
            return sim.Impacts.Select(i => (i.X, i.Z)).ToList();
        }

        var a = Landings(5);
        Assert.Equal(a, Landings(5));
        Assert.NotEqual(a, Landings(6));
        // With a spread the bombs are not on the exact line any more.
        var sim = Region(wide, IncidentFrequency.Normal, 5);
        var flight = Launch(sim);
        Fly(sim);
        Assert.Contains(sim.Impacts, i => MathF.Abs((i.X - flight.EntryX) * flight.DirZ - (i.Z - flight.EntryZ) * flight.DirX) > 5f);
    }

    // ---- What the enemy wants --------------------------------------------------------------------------------------

    [Fact]
    public void TheTargetIsPickedByValue_AnIndustryThatWorksAndMattersWeighsMost_ADownOrIdleOneLess()
    {
        var t = Worlds.Late(Tuning.Load());
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), t);
        var sim = new Simulation(net, 5, IncidentFrequency.Off);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1000, 0, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), new[] { new IndustryPlan(SteelMill, 300, 60, 0), new IndustryPlan(Sawmill, 600, 60, 0) }, null,
            new[] { new GatewayPlan(GatewayKind.Road, "Test Road Gate", 900, 60, 0) }));
        var b = sim.WarTuning.Bombing;
        var mill = sim.Industries[0];
        var saw = sim.Industries[1];
        Array.Fill(mill.Input, 100f);
        Array.Fill(saw.Input, 100f);

        float Weight(RaidTargetKind kind, int id) => sim.CandidatesForTest().Single(c => c.Kind == kind && c.Id == id).Weight;

        Assert.Equal(b.ValueOf("steelmill"), Weight(RaidTargetKind.Industry, 0));
        Assert.Equal(b.ValueOf("sawmill"), Weight(RaidTargetKind.Industry, 1));
        Assert.True(Weight(RaidTargetKind.Industry, 0) > Weight(RaidTargetKind.Industry, 1));
        Assert.Equal(b.GatewayValue, Weight(RaidTargetKind.Gateway, 0));
        Assert.Equal(14, b.ValueOf("powerplant"));
        Assert.Equal(14, b.ValueOf("truckplant"));
        Assert.Equal(12, b.ValueOf("engineworks"));

        // Idle (an input lacking) weighs idleFactor, down weighs damagedFactor.
        Array.Clear(mill.Input);
        Assert.Equal(b.ValueOf("steelmill") * b.IdleFactor, Weight(RaidTargetKind.Industry, 0), 0.001f);
        Array.Fill(mill.Input, 100f);
        sim.BombForTest(300, 60);
        Assert.True(mill.DamagedUntil > sim.Tick);
        Assert.Equal(b.ValueOf("steelmill") * b.DamagedFactor, Weight(RaidTargetKind.Industry, 0), 0.001f);

        // Drawn over and over, the flights choose the better target more often.
        mill.DamagedUntil = 0;
        int steel = 0, sawmill = 0, gateway = 0;
        for (int n = 0; n < 400; n++)
        {
            sim.LaunchFlightForTest();
            var f = sim.Flights[^1];
            if (f.TargetKind == RaidTargetKind.Industry && f.TargetId == 0) steel++;
            else if (f.TargetKind == RaidTargetKind.Industry) sawmill++;
            else gateway++;
        }
        Assert.InRange(steel / 400f, 0.5f, 0.75f);   // 14 of 14 + 3 + 6
        Assert.InRange(sawmill / 400f, 0.06f, 0.22f); // 3 of 23
        Assert.InRange(gateway / 400f, 0.18f, 0.36f); // 6 of 23
    }

    [Fact]
    public void AFlightThatFindsNothingToBomb_IsNotLaunched()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Raiding(RaidTargetKind.Signal)), 5, IncidentFrequency.Normal);
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>()));

        RunDays(sim, 5);

        Assert.Equal(0, sim.Raids);
        Assert.Empty(sim.Flights);
    }

    // ---- What a bomb does ------------------------------------------------------------------------------------------

    [Fact]
    public void ABombDestroysTheBuildingsInItsBlastRadius_AndNoOthers_AndTakesAShareOfThePeopleInsideThem()
    {
        var sim = Bombed();
        var town = sim.Towns[0];
        var home = sim.Buildings.First(b => b.Type == BuildingType.Home);
        var b = sim.WarTuning.Bombing;
        Assert.All(sim.Buildings, x => Assert.Equal(BuildingState.Active, x.State));
        var outside = sim.Buildings.Where(x => Distance(x.X, x.Z, home.X, home.Z) > b.BlastRadius).ToList();
        Assert.NotEmpty(outside);
        float satisfaction = town.Satisfaction;
        var before = sim.TripGroups.ToDictionary(g => g.Id, g => g.People);

        var flight = sim.BombForTest(home.X, home.Z);

        var destroyed = sim.Buildings.Where(x => x.State == BuildingState.Demolished).ToList();
        Assert.Contains(home, destroyed); // at the centre the chance is certain
        Assert.All(destroyed, x => Assert.True(Distance(x.X, x.Z, home.X, home.Z) <= b.BlastRadius));
        Assert.All(outside, x => Assert.NotEqual(BuildingState.Demolished, x.State));
        Assert.Equal(destroyed.Count, flight.Buildings);
        // At the start of the game everybody is at home: the people who die are those of the groups that live in the destroyed homes (the shops have nobody in them yet).
        var lost = sim.TripGroups.Where(g => g.People < before[g.Id]).ToList();
        Assert.NotEmpty(lost);
        Assert.All(lost, g => Assert.Contains(sim.Buildings[g.Home], destroyed));
        long dead = lost.Sum(g => (long)(before[g.Id] - g.People));
        long capacity = destroyed.Where(x => x.Type == BuildingType.Home).Sum(x => sim.TownTuning.Of(x.Type, x.Tier).People);
        Assert.InRange(dead, (long)(capacity * b.CasualtyShare) - lost.Count, (long)(capacity * b.CasualtyShare) + lost.Count); // the share of each group, rounded by a draw
        Assert.Equal(dead, sim.Casualties);
        Assert.Equal(dead, town.Casualties);
        Assert.Equal(dead, flight.Casualties);
        Assert.Equal(satisfaction - destroyed.Count * b.TownLossPerBuilding, town.Satisfaction, 0.001f);
    }

    [Fact]
    public void TheChanceThatABuildingFalls_FallsWithItsDistanceFromTheCentreOfTheBlast()
    {
        int Destroyed(float fraction)
        {
            int count = 0;
            for (ulong seed = 1; seed <= 60; seed++)
            {
                var sim = Bombed();
                var home = sim.Buildings.First(x => x.Type == BuildingType.Home);
                sim.BombForTest(home.X + fraction * sim.WarTuning.Bombing.BlastRadius, home.Z, seed);
                if (home.State == BuildingState.Demolished) count++;
            }
            return count;
        }

        int near = Destroyed(0.2f), middle = Destroyed(0.5f), far = Destroyed(0.9f);

        Assert.True(near > middle && middle > far, $"{near} {middle} {far}");
        Assert.InRange(near, 38, 58);   // about 80 %
        Assert.InRange(middle, 20, 40); // about 50 %
        Assert.InRange(far, 0, 14);     // about 10 %
    }

    [Fact]
    public void AnIndustryAccumulatesDamage_EveryBombStopsItLongerAndTakesMoreOfItsStock_UpToACap()
    {
        var sim = Bombed();
        var mill = sim.Industries[0];
        var b = sim.WarTuning.Bombing;
        mill.Input[Wood] = 400f;
        mill.Output = 100f;
        long day = (long)(sim.DayLengthSeconds / Simulation.StepSeconds);

        var first = sim.BombForTest(600, -30);
        Assert.Equal(sim.Tick + b.IndustryDownDays * day, mill.DamagedUntil);
        Assert.Equal(400f * (1f - b.InputLoss), mill.Input[Wood], 0.01f);
        Assert.Equal(100f * (1f - b.InputLoss), mill.Output, 0.01f);
        Assert.True(sim.StatusOf(mill).Damaged);
        Assert.Single(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.StartsWith("The sawmill was bombed"));

        var second = sim.BombForTest(610, -30);
        Assert.Equal(sim.Tick + 2 * b.IndustryDownDays * day, mill.DamagedUntil);
        Assert.Equal(400f * MathF.Pow(1f - b.InputLoss, 2), mill.Input[Wood], 0.01f);
        Assert.Single(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.StartsWith("The sawmill was bombed")); // told once

        for (int n = 0; n < 20; n++) sim.BombForTest(600, -20);
        Assert.Equal(sim.Tick + b.IndustryMaxDownDays * day, mill.DamagedUntil);
        Assert.Equal(1, first.IndustriesHit);
        Assert.Equal(1, second.IndustriesHit);

        // It makes nothing meanwhile, and works again when the time is up.
        double made = mill.Produced;
        mill.Input[Wood] = 100f;
        RunDays(sim, b.IndustryMaxDownDays - 0.5);
        Assert.Equal(made, mill.Produced);
        RunDays(sim, 1);
        Assert.False(sim.StatusOf(mill).Damaged);
        Assert.Single(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text == "The sawmill is working again");
    }

    [Fact]
    public void AnIndustryOutsideTheBlastIsLeftAlone_AndABombBesideTheTargetStillHitsTheRoad()
    {
        var sim = Bombed(b => b with { CraterChance = 1f });
        var mill = sim.Industries[0];
        float reach = sim.WarTuning.Bombing.BlastRadius + sim.IndustryTuning.Types[Sawmill].Size * 0.5f;

        // 50 m from the sawmill and on the road (the road runs at z = -60): the mill is missed, the road is not.
        var flight = sim.BombForTest(640, -60);

        Assert.True(Distance(640, -60, mill.X, mill.Z) > reach);
        Assert.Equal(0L, mill.DamagedUntil);
        Assert.Equal(0, flight.IndustriesHit);
        Assert.Equal(1, flight.Craters);
        Assert.Contains(sim.Incidents, i => i.Kind == IncidentKind.Bombing);
    }

    [Fact]
    public void ABombedPowerPlantGivesNoPower_UntilItIsMended()
    {
        var t = Raiding(RaidTargetKind.Industry);
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), t), 4, IncidentFrequency.Off);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1000, 0, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), new[] { new IndustryPlan(PowerPlant, 400, 40, 0), new IndustryPlan(SteelMill, 800, 40, 0) }));
        var plant = sim.Industries[0];
        plant.Input[Coal] = 500f;
        sim.Step();
        sim.Step();
        float with = sim.PowerSupply;
        Assert.True(plant.Load > 0f);

        sim.BombForTest(400, 40);
        sim.Step();

        Assert.Equal(0f, plant.Load);
        Assert.True(sim.PowerSupply < with, $"{sim.PowerSupply} < {with}");
        Assert.Equal(sim.FreeGrid, sim.PowerSupply, 0.001f);
    }

    [Fact]
    public void AGatewayThatIsBombed_IsShut_TakesNothing_AndOpensAgain()
    {
        var sim = Bombed();
        int station = sim.Execute(new BuildStationCommand(1100, -60));
        Assert.True(station >= 0);
        Assert.True(sim.Accepts(sim.Stations[station], Steel));
        sim.Execute(new RequestImportCommand(0, "coal"));
        var b = sim.WarTuning.Bombing;

        var flight = sim.BombForTest(1100, -30);

        Assert.Equal(1, flight.GatewaysHit);
        Assert.True(sim.Gateways[0].DamagedUntil > sim.Tick);
        Assert.False(sim.Accepts(sim.Stations[station], Steel), "a shut gateway accepts no export");
        Assert.Single(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text == $"The Test Road Gate was bombed and is shut for {b.GatewayDownDays} days");
        RunDays(sim, 1.5);
        Assert.Equal(0.0, sim.Gateways[0].Imported[8]); // a shipment is held up while it is shut

        RunDays(sim, b.GatewayDownDays + 1);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.Contains("is open again"));
        Assert.True(sim.Accepts(sim.Stations[station], Steel));
    }

    [Fact]
    public void AStationThatIsBombed_IsShutForDays_AcceptsNothing_GetsNoCargo_AndOpensAgain()
    {
        var sim = Bombed();
        int station = sim.Execute(new BuildStationCommand(600, -60));
        Assert.True(station >= 0);
        var s = sim.Stations[station];
        var mill = sim.Industries[0];
        mill.Output = 40f;
        Assert.True(sim.Accepts(s, Wood));

        var flight = sim.BombForTest(600, -60);

        Assert.Equal(1, flight.StationsShut);
        Assert.True(sim.IsShut(s));
        Assert.False(sim.Accepts(s, Wood));
        Assert.Single(sim.Events, e => e.Text.EndsWith("was bombed and is shut for 2 days") && e.Text.StartsWith("Station "));
        float waiting = s.Waiting[Planks];
        mill.Input[Wood] = 0f;
        RunDays(sim, 0.5);
        Assert.Equal(waiting, s.Waiting[Planks]); // the mill's output is not put into a shut station

        RunDays(sim, 2);
        Assert.False(sim.IsShut(s));
        Assert.True(sim.Accepts(s, Wood));
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.EndsWith("is open again") && e.Text.StartsWith("Station "));
        RunDays(sim, 0.2);
        Assert.True(s.Waiting[Planks] > waiting);
    }

    [Fact]
    public void ABusDoesNotLoadOrUnloadAtAShutStation_ItGoesOnWithWhatItCarries()
    {
        long Income(bool bombed)
        {
            var w = Road(frequency: IncidentFrequency.Off);
            var sim = w.Sim;
            Bus(w);
            if (bombed) sim.BombForTest(1050, 0);
            Assert.Equal(bombed, sim.IsShut(sim.Stations[w.B]));
            Run(sim, 500);
            return sim.Lines[w.Line].Income;
        }

        Assert.True(Income(bombed: false) > 0, "the control line earns");
        Assert.Equal(0L, Income(bombed: true));
    }

    [Fact]
    public void ADepotThatIsBombed_IsShut_SellsAndBuysNothing_SendsNoCrew_AndOpensAgain()
    {
        var w = Road(DepotLevel.Workshop, frequency: IncidentFrequency.Off);
        var sim = w.Sim;
        int parked = sim.Execute(new BuyParkedVehicleCommand(w.Depot, "bus"));
        Assert.True(parked >= 0);
        var depot = sim.Depots[w.Depot];
        var (dx, dz) = sim.Network.GetNode(depot.Node);

        var flight = sim.BombForTest(dx, dz);

        Assert.Equal(1, flight.DepotsShut);
        Assert.True(sim.IsShut(depot));
        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(w.Depot, "bus", w.Line)));
        Assert.Equal(-1, sim.Execute(new BuyParkedVehicleCommand(w.Depot, "bus")));
        Assert.Equal(-1, sim.Execute(new SellParkedCommand(parked)));
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.StartsWith("Depot 1 was bombed and is shut for 2 days"));

        // No crew leaves a shut depot: the distant service has to come for a wreck.
        int a = sim.SpawnVehicle(new LaneId(0), 200f, 0f), b = sim.SpawnVehicle(new LaneId(0), 210f, 0f);
        sim.Collide(a, b, IncidentKind.RearEnd);
        Run(sim, 5);
        Assert.Equal(-1, sim.GetIncident(sim.IncidentCount - 1).Depot);

        RunDays(sim, 3);
        Assert.False(sim.IsShut(depot));
        Assert.True(sim.Execute(new SellParkedCommand(parked)) >= 0);
        Assert.True(sim.Execute(new BuyVehicleCommand(w.Depot, "bus", w.Line)) >= 0);
        Assert.Contains(sim.Events, e => e.Text == "Depot 1 is open again");
    }

    [Fact]
    public void AnOpenDepotSendsItsCrew_WhereAShutOneDoesNot()
    {
        var w = Road(DepotLevel.Workshop, frequency: IncidentFrequency.Off);
        var sim = w.Sim;
        int a = sim.SpawnVehicle(new LaneId(0), 200f, 0f), b = sim.SpawnVehicle(new LaneId(0), 210f, 0f);
        sim.Collide(a, b, IncidentKind.RearEnd);
        Run(sim, 5);

        Assert.Equal(w.Depot, sim.GetIncident(0).Depot);
    }

    [Fact]
    public void AVehicleInsideTheBlastBecomesAWreck_AndItsPeopleAreCasualties_OneOutsideIsLeftAlone()
    {
        var w = Road(frequency: IncidentFrequency.Off);
        var sim = w.Sim;
        int hit = sim.SpawnVehicle(new LaneId(0), 400f, 0f), also = sim.SpawnVehicle(new LaneId(0), 410f, 0f), clear = sim.SpawnVehicle(new LaneId(0), 600f, 0f);
        var (x, z, _) = sim.Network.LanePoint(new LaneId(0), 395f);

        var flight = sim.BombForTest(x, z);

        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(hit).Kind);
        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(also).Kind);
        Assert.Equal(VehicleKind.Car, sim.GetVehicle(clear).Kind);
        Assert.Equal(2, flight.Wrecks);
        Assert.Equal(2L * sim.WarTuning.Bombing.CarPeople, sim.Casualties);
        var incident = Assert.Single(sim.Incidents);
        Assert.Equal(IncidentKind.Bombing, incident.Kind);
        Assert.Equal(2, incident.VehicleIds.Count);
        Assert.False(incident.CompanyInvolved);

        Run(sim, 400);
        Assert.Equal(IncidentState.Cleared, sim.GetIncident(0).State);
    }

    [Fact]
    public void ABusThatIsBombed_IsLost_AndThePassengersAboardAreCasualties()
    {
        var w = Road(frequency: IncidentFrequency.Off);
        var sim = w.Sim;
        int bus = Bus(w);
        Assert.True(StepUntil(sim, 600, () => sim.GetFleetInfo(bus).Load > 0 && !sim.GetFleetInfo(bus).Loading), "the bus never carried anybody");
        int load = sim.GetFleetInfo(bus).Load;
        var v = sim.GetVehicle(bus);
        var (x, z, _) = sim.Network.LanePoint(v.Lane, v.Position - 4f);

        sim.BombForTest(x, z);

        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(bus).Kind);
        Assert.Equal((long)load, sim.Casualties);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Raid && e.Text.Contains("was destroyed in the raid") && e.Text.EndsWith($"{load} casualties"));
        Assert.Equal(0L, sim.Company.Total(LedgerEntry.Compensation)); // a bomb is nobody's fault: no compensation, no fine
        Run(sim, 400);
        Assert.Equal(IncidentState.Cleared, sim.GetIncident(0).State);
        Assert.Equal(VehicleKind.Removed, sim.GetVehicle(bus).Kind);
    }

    [Fact]
    public void ABombFailsTheNearestSignalInItsBlast_AndNothingHappensWithoutOne()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Raiding(RaidTargetKind.Signal)), 5, IncidentFrequency.Off);
        Assert.True(sim.Execute(new BuildTrackCommand(0, 0, 1500, 0)) > 0);
        int near = sim.Execute(new BuildSignalCommand(700, 0, SignalKind.Block, Forward: true)), other = sim.Execute(new BuildSignalCommand(725, 0, SignalKind.Block, Forward: true));
        Assert.True(near >= 0 && other >= 0);
        sim.Execute(new AddRailDepotCommand(300, 0));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>()));
        var a = sim.Rail.GetSignal(new SignalId(near));
        var b = sim.Rail.GetSignal(new SignalId(other));

        var miss = sim.BombForTest(100, 0);
        Assert.Equal(0, miss.SignalsFailed);
        Assert.False(a.Failed || b.Failed);

        var hit = sim.BombForTest(695, 0);
        Assert.Equal(1, hit.SignalsFailed);
        Assert.True(a.Failed);
        Assert.False(b.Failed, "only the nearest fails");
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.SignalFailure);

        sim.BombForTest(710, 0); // the nearest is failed already: the next one in the blast goes
        Assert.True(b.Failed);

        Run(sim, 720);
        Assert.True(sim.Company.Total(LedgerEntry.Maintenance) < 0, "a crew has been to mend them");
        Assert.False(a.Failed);
    }

    [Fact]
    public void ABombLeavesACraterOnTheNearestRoad_WithTheChanceOfTheTuning_AndACrewClearsIt()
    {
        var sure = Bombed(b => b with { CraterChance = 1f });
        var none = Bombed(b => b with { CraterChance = 0f });
        var far = Bombed(b => b with { CraterChance = 1f });

        var flight = sure.BombForTest(300, -60);
        none.BombForTest(300, -60);
        far.BombForTest(300, 400); // no road within reach

        Assert.Equal(1, flight.Craters);
        var crater = Assert.Single(sure.Incidents);
        Assert.Equal(IncidentKind.Bombing, crater.Kind);
        var (cx, cz, _) = sure.Network.LanePoint(crater.Lane, crater.Position);
        Assert.True(Distance(cx, cz, 300, -60) < sure.WarTuning.Bombing.CraterRadius, "the crater is near the impact");
        Assert.Empty(none.Incidents);
        Assert.Empty(far.Incidents);

        RunDays(sure, 3);
        Assert.Equal(IncidentState.Cleared, sure.GetIncident(0).State);
    }

    // ---- A flight against the world --------------------------------------------------------------------------------

    [Fact]
    public void AFlightThatIsAimedTrue_StopsTheIndustryForTheBombsThatHitIt_AndTheEndIsReported()
    {
        var sim = Region(Raiding(RaidTargetKind.Industry), IncidentFrequency.Normal);
        var mill = sim.Industries[0];
        var flight = Launch(sim);
        long day = (long)(sim.DayLengthSeconds / Simulation.StepSeconds);
        var b = sim.WarTuning.Bombing;
        // Bombs 30 m apart centred on the mill: the three nearest are within its reach (blast 35 + half the size of the sawmill).
        float reach = b.BlastRadius + sim.IndustryTuning.Types[Sawmill].Size * 0.5f;
        int expected = Enumerable.Range(0, b.BombsPerAircraft).Count(k => MathF.Abs((k - (b.BombsPerAircraft - 1) * 0.5f) * b.BombSpacing) <= reach);

        Fly(sim);

        Assert.Equal(expected, flight.IndustriesHit);
        Assert.True(expected >= 3);
        Assert.True(mill.DamagedUntil > sim.Tick - 1);
        var over = Assert.Single(sim.Events, e => e.Text.StartsWith("Air raid over"));
        Assert.Contains($"{b.BombsPerAircraft} bombs dropped on the sawmill", over.Text);
        Assert.Contains($"{expected} industries hit", over.Text);
        Assert.Equal(EventSeverity.Serious, over.Severity);
        Assert.Equal((600f, -30f), (over.X, over.Z));
        Assert.True(mill.DamagedUntil - flight.LaunchTick >= (expected - 1) * day);
    }

    [Fact]
    public void AFlightThatAimsOff_MissesTheTarget_ButItsBombsStillLandSomewhere()
    {
        var sim = Region(Raiding(RaidTargetKind.Industry), IncidentFrequency.Normal);
        var mill = sim.Industries[0];
        var flight = Launch(sim);
        flight.Aim[0] = 200f; // the sight is far off

        Fly(sim);

        Assert.Equal(0L, mill.DamagedUntil);
        Assert.Equal(sim.WarTuning.Bombing.BombsPerAircraft, sim.Bombs);
        Assert.All(sim.Impacts, i => Assert.True(Distance(i.X, i.Z, mill.X, mill.Z) > 150f));
        var over = Assert.Single(sim.Events, e => e.Text.StartsWith("Air raid over"));
        Assert.Contains("bombs dropped on the sawmill and around it", over.Text);
    }

    [Fact]
    public void AFlightIsOverWhenItsLastAircraftHasLeft_AndHasNoBombInTheAir()
    {
        var sim = Region(Raiding(RaidTargetKind.Town, b => b with { Aircraft = new[] { new RaidAircraftTuning { Phase = "rearmament", Min = 4, Max = 4 } } }), IncidentFrequency.Normal);
        var flight = Launch(sim);
        var b = sim.WarTuning.Bombing;
        long start = sim.Tick;

        Fly(sim);

        long took = sim.Tick - start;
        float expected = (flight.Length + 3 * b.TrailSpacing) / (flight.Speed * Simulation.StepSeconds);
        Assert.InRange(took, (long)expected - 3, (long)expected + 3);
        Assert.Empty(sim.Flights);
        Assert.Empty(sim.BombsFalling);
        Assert.Equal(4 * b.BombsPerAircraft, sim.Bombs);
        Assert.Equal(FlightPhase.Leaving, flight.Phase);
    }

    // ---- State -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFlightsAreDeterministic_AndInTheStateHash()
    {
        ulong Run(RaidTargetKind kind, ulong seed, double seconds)
        {
            var sim = Region(Raiding(kind, b => b with { Scatter = 10f, AimError = 30f, Aircraft = new[] { new RaidAircraftTuning { Phase = "rearmament", Min = 1, Max = 3 } } }), IncidentFrequency.Normal, seed);
            StepUntil(sim, seconds, () => false);
            return sim.StateHash();
        }

        Assert.Equal(Run(RaidTargetKind.Town, 5, 400), Run(RaidTargetKind.Town, 5, 400));
        Assert.NotEqual(Run(RaidTargetKind.Town, 5, 400), Run(RaidTargetKind.Industry, 5, 400));
        Assert.NotEqual(Run(RaidTargetKind.Town, 5, 400), Run(RaidTargetKind.Town, 6, 400));

        // In the middle of a flight the aircraft are in the hash too: the same run at the same tick agrees, one tick later it does not.
        var a = Region(Raiding(RaidTargetKind.Town), IncidentFrequency.Normal);
        var b = Region(Raiding(RaidTargetKind.Town), IncidentFrequency.Normal);
        var fa = Launch(a);
        Launch(b);
        for (int n = 0; n < 40; n++) { a.Step(); b.Step(); }
        Assert.Equal(a.StateHash(), b.StateHash());
        fa.Aim[0] += 1f;
        Assert.NotEqual(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void ABadBombingTuning_IsRefused()
    {
        var t = Tuning.Load();
        var B = t.War.Bombing;
        var cases = new Dictionary<string, BombingTuning>
        {
            ["gap"] = B with { Gaps = new[] { new RaidGapTuning { Phase = "totalwar", MinDays = 9, MaxDays = 3 } } },
            ["no gaps"] = B with { Gaps = Array.Empty<RaidGapTuning>() },
            ["no aircraft"] = B with { Aircraft = Array.Empty<RaidAircraftTuning>() },
            ["aircraft phase"] = B with { Aircraft = new[] { new RaidAircraftTuning { Phase = "moon", Min = 1, Max = 2 } } },
            ["aircraft range"] = B with { Aircraft = new[] { new RaidAircraftTuning { Phase = "totalwar", Min = 3, Max = 2 } } },
            ["no aircraft at all"] = B with { Aircraft = new[] { new RaidAircraftTuning { Phase = "totalwar", Min = 0, Max = 2 } } },
            ["industry type"] = B with { IndustryValues = new[] { new RaidIndustryValue { Type = "moonbase", Value = 3 } } },
            ["negative value"] = B with { GatewayValue = -1 },
            ["nothing worth bombing"] = Wanting(B, RaidTargetKind.Industry) with { IndustryValues = Array.Empty<RaidIndustryValue>() },
            ["speed"] = B with { Speed = 0f },
            ["altitude"] = B with { Altitude = 0f },
            ["blast"] = B with { BlastRadius = 0f },
            ["no bombs"] = B with { BombsPerAircraft = 0 },
            ["spacing"] = B with { BombSpacing = 0f },
            ["chance"] = B with { BuildingChance = 1.5f },
            ["crater chance"] = B with { CraterChance = -0.1f },
            ["casualty share"] = B with { CasualtyShare = 2f },
            ["cap below a bomb"] = B with { IndustryDownDays = 5, IndustryMaxDownDays = 2 },
            ["arc"] = B with { EntryArc = 3f },
            ["damaged factor"] = B with { DamagedFactor = 2f },
        };

        foreach (var (name, bad) in cases)
            Assert.True(ThrowsJson(() => (t.War with { Bombing = bad }).Validate(t)), name);
        Assert.False(ThrowsJson(() => t.War.Validate(t)), "the shipped tuning is valid");
    }

    static bool ThrowsJson(Action action)
    {
        try { action(); return false; }
        catch (System.Text.Json.JsonException) { return true; }
    }

    // ---- What the anti-aircraft overlay reads (M6b) ---------------------------------------------------------------

    [Fact]
    public void TheOverlaysTargets_AreTheEnemysCandidatesWithTheirWeights_AndReadingThemChangesNothing()
    {
        var sim = Bombed();
        ulong before = sim.StateHash();
        var targets = sim.RaidTargets();
        Assert.Equal(before, sim.StateHash());
        var candidates = sim.CandidatesForTest();
        Assert.Equal(candidates.Count, targets.Count);
        for (int k = 0; k < targets.Count; k++)
        {
            Assert.Equal(candidates[k].Kind, targets[k].Kind);
            Assert.Equal(candidates[k].Weight, targets[k].Weight);
        }
    }

    [Fact]
    public void AFlightStraightFromItsSide_EntersAtTheOverlaysEntryPoint_AndLetsItsFirstBombGoTheReleaseDistanceShortOfTheTarget()
    {
        var sim = Bombed(b => b with { EntryArc = 0f, AimError = 0f, Scatter = 0f, ScatterAlong = 0f });
        sim.LaunchFlightForTest();
        var flight = sim.Flights[^1];
        var (ex, ez) = sim.RaidEntryPoint();
        Assert.Equal(ex, flight.EntryX, 1f);
        Assert.Equal(ez, flight.EntryZ, 1f);
        Assert.True(StepUntil(sim, 600, () => sim.BombsFalling.Count > 0), "no bomb was let go");
        var bomb = sim.BombsFalling[0];
        float short_ = MathF.Sqrt((bomb.FromX - flight.TargetX) * (bomb.FromX - flight.TargetX) + (bomb.FromZ - flight.TargetZ) * (bomb.FromZ - flight.TargetZ));
        Assert.InRange(short_, sim.BombReleaseDistance - flight.Speed * Simulation.StepSeconds * 2f, sim.BombReleaseDistance + flight.Speed * Simulation.StepSeconds * 2f);
    }
}
