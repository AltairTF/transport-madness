using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Anti-aircraft (C13): the battery the player builds, the ammunition it fires, the aircraft it brings down and what its fire does to a raid.</summary>
public class DefenceTests
{
    const int Sawmill = 3;
    const float StepSeconds = Simulation.StepSeconds;

    // ---- The world ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The shipped tuning with the guns available from the first month, and the raids those of a flight of <paramref name="aircraft"/> every day on the one industry
    /// that is worth anything (a sawmill), with the aim exact, so that a test can say where every bomb lands. A day of a minute, so that one flight is over before the next.
    /// </summary>
    static Tuning Tuned(Func<AntiAircraftTuning, AntiAircraftTuning>? defence = null, Func<BombingTuning, BombingTuning>? bombing = null, int aircraft = 1, float dayLength = 60f)
    {
        var t = Worlds.Late(Tuning.Load());
        var a = t.War.AntiAircraft with { FromMonth = 0 };
        if (defence != null) a = defence(a);
        var b = t.War.Bombing with
        {
            FromMonth = 0,
            Gaps = new[] { new RaidGapTuning { Phase = "rearmament", MinDays = 1, MaxDays = 1 } },
            Aircraft = new[] { new RaidAircraftTuning { Phase = "rearmament", Min = aircraft, Max = aircraft } },
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
            EntryArc = 0f,
            BuildingChance = 1f,
        };
        if (bombing != null) b = bombing(b);
        return t with
        {
            Economy = t.Economy with { DayLength = dayLength },
            Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f },
            War = t.War with { AntiAircraft = a, Bombing = b },
        };
    }

    /// <summary>The shipped tuning (the guns from month 20, the raids from month 48) with the standing left alone and a day of a minute.</summary>
    static Tuning Shipped()
    {
        var t = Worlds.Late(Tuning.Load());
        return t with { Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f }, Economy = t.Economy with { DayLength = 60f } };
    }

    /// <summary>
    /// A block of streets, a road east past a sawmill (x = 600) to a road gateway (x = 1100), a depot, and the industries given. With <paramref name="town"/> the block is
    /// a town with homes; without, nobody lives there and so a battery is always fully staffed (a day closing does not take its staff away).
    /// </summary>
    static Simulation Region(Tuning tuning, IncidentFrequency frequency = IncidentFrequency.Off, ulong seed = 5, Terrain? terrain = null, bool town = false, params IndustryPlan[] more)
    {
        var net = new RoadNetwork(terrain ?? Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, seed, frequency);
        foreach (var (x0, z0, x1, z1) in new[] { (-60f, -60f, 60f, -60f), (60f, -60f, 60f, 60f), (60f, 60f, -60f, 60f), (-60f, 60f, -60f, -60f) })
            sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadType.TwoLane, 14f));
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(60, -60, 1300, -60, RoadType.TwoLane, 14f)));
        var buildings = new List<BuildingPlan>();
        if (town)
        {
            var lots = TownGenerator.LotSites(tuning.Towns, 0, 0, 0, 1, 0, 0, 0, 0).ToList();
            for (int k = 0; k < 8; k++) buildings.Add(new BuildingPlan(k % 4 == 3 ? BuildingType.Commerce : BuildingType.Home, 1, lots[k].X, lots[k].Z, lots[k].Facing));
        }
        var industries = new List<IndustryPlan> { new(Sawmill, 600, -30, 0) };
        industries.AddRange(more);
        sim.AddTowns(new WorldPlan(1, town ? new[] { new TownPlan("Testville", 0, 0, 0, 1, buildings) } : Array.Empty<TownPlan>(), industries, null,
            new[] { new GatewayPlan(GatewayKind.Road, "Test Road Gate", 1100, -30, 0) }));
        Assert.True(sim.Execute(new AddDepotCommand(net.FindNodeNear(60, -60, 1f), DepotLevel.Simple)) >= 0);
        return sim;
    }

    static Industry Build(Simulation sim, float x, float z, float ammunition = 100f, float staffing = 1f)
    {
        int id = sim.Execute(new BuildBatteryCommand(x, z));
        Assert.True(id >= 0, $"the battery was refused: {sim.WhyNotBattery(x, z)}");
        var battery = sim.Industries[id];
        battery.Input[sim.AmmunitionCargo] = ammunition;
        battery.Staffing = staffing;
        return battery;
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static bool StepUntil(Simulation sim, double seconds, Func<bool> done)
    {
        long ticks = (long)Math.Round(seconds / StepSeconds);
        for (long t = 0; t < ticks; t++)
        {
            if (done()) return true;
            sim.Step();
        }
        return done();
    }

    static void Fly(Simulation sim) =>
        Assert.True(StepUntil(sim, 600, () => sim.Flights.Count == 0 && sim.BombsFalling.Count == 0 && sim.FallingAircraft.Count == 0), "the flight never ended");

    static float Distance(float x0, float z0, float x1, float z1) => MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));

    /// <summary>Launches a flight and puts its leader over its target (the sawmill), so that the aircraft are where a test wants them without stepping.</summary>
    static Flight Over(Simulation sim)
    {
        sim.LaunchFlightForTest();
        var flight = sim.Flights[^1];
        flight.Travelled = flight.TargetDistance;
        return flight;
    }

    const string OutOfAmmunition = "Anti-aircraft battery out of ammunition";

    // ---- Building one ------------------------------------------------------------------------------------------

    [Fact]
    public void TheShippedNumbers_AreABatteryFromMonth20_NewsAtMonth20And40_AndAmmunitionOnlyFromTheGateways()
    {
        var sim = Region(Worlds.Late(Tuning.Load()));
        var a = sim.AntiAircraft;

        Assert.Equal((20, 40), (a.FromMonth, a.ExpectMonth));
        Assert.Equal("Mobilisation", sim.Network.Tuning.Campaign.PhaseOf(a.FromMonth).Name);
        Assert.Equal("antiaircraft", sim.IndustryTuning.Types[sim.BatteryType].Id);
        Assert.True(sim.IndustryTuning.Types[sim.BatteryType].Jobs > 0);
        Assert.Equal("ammunition", sim.CargoTypes[sim.AmmunitionCargo].Id);
        Assert.True(sim.IsImported(sim.AmmunitionCargo));
        Assert.False(sim.IsExportable(sim.AmmunitionCargo));
        var offer = sim.ImportOffers.Single(o => o.Cargo == "ammunition");
        Assert.Equal(20, offer.FromMonth);
        Assert.Contains("ammunition", sim.WarTuning.Rationing.Cargo);
        Assert.True(sim.WarTuning.Bombing.ValueOf("antiaircraft") > 0, "the enemy wants to destroy the guns too");
    }

    [Fact]
    public void ABattery_IsRefusedBeforeMonth20_AndBuiltFromThen()
    {
        var sim = Region(Shipped());
        long before = sim.Company.Money;

        Assert.Equal(-1, sim.Execute(new BuildBatteryCommand(600, 400)));
        Assert.Contains("month 20", sim.WhyNotBattery(600, 400));
        Assert.Equal(before, sim.Company.Money);
        Assert.Single(sim.Industries);

        RunDays(sim, 3 * 20 - 0.5);
        Assert.Equal(19, sim.MonthIndex);
        Assert.NotNull(sim.WhyNotBattery(600, 400));
        RunDays(sim, 1);
        Assert.Equal(20, sim.MonthIndex);

        Assert.Null(sim.WhyNotBattery(600, 400));
        int id = sim.Execute(new BuildBatteryCommand(600, 400));
        Assert.Equal(1, id);
        Assert.True(sim.IsBattery(sim.Industries[id]));
    }

    [Fact]
    public void ABattery_IsPaidFor_AndRefusedWithoutTheMoney()
    {
        var sim = Region(Tuned());
        long price = sim.AntiAircraft.Price, built = sim.Company.Total(LedgerEntry.Construction);
        sim.Company.Money = price - 1;

        Assert.Equal(-1, sim.Execute(new BuildBatteryCommand(600, 400)));
        Assert.Equal("Not enough money", sim.WhyNotBattery(600, 400));
        Assert.Equal(price - 1, sim.Company.Money);

        sim.Company.Money = price;
        int id = sim.Execute(new BuildBatteryCommand(600, 400));

        Assert.True(id >= 0);
        Assert.Equal(0L, sim.Company.Money);
        Assert.Equal(-price, sim.Company.Total(LedgerEntry.Construction) - built);
        var battery = sim.Industries[id];
        Assert.Equal((600f, 400f), (battery.X, battery.Z));
        Assert.Equal(0f, battery.Input[sim.AmmunitionCargo]);
        Assert.NotEqual(0f, battery.AccessX + battery.AccessZ); // it meets the roads like any industry (C10)
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.War && e.Subject == id && e.Text.StartsWith("An anti-aircraft battery was built"));
    }

    [Fact]
    public void ABattery_CostsItsUpkeepEveryMonth()
    {
        var with = Region(Tuned());
        var without = Region(Tuned());
        Build(with, 600, 400);

        RunDays(with, 6);
        RunDays(without, 6);

        Assert.Equal(2, with.MonthIndex);
        long upkeep = with.AntiAircraft.UpkeepPerMonth;
        Assert.True(upkeep > 0);
        Assert.Equal(-2 * upkeep, with.Company.Total(LedgerEntry.Upkeep) - without.Company.Total(LedgerEntry.Upkeep));
        Assert.Equal(upkeep, with.BatteryUpkeepPerMonth);
        Assert.Equal(0L, without.BatteryUpkeepPerMonth);
    }

    [Fact]
    public void ABattery_StandsOnlyOnFreeLevelDryGround_AwayFromOthers()
    {
        var sim = Region(Tuned(), town: true);
        var a = sim.AntiAircraft;

        Assert.Equal("Too near another industry", sim.WhyNotBattery(600 + a.Clearance - 1, -30));        // the sawmill
        Assert.Equal("Too near a gateway", sim.WhyNotBattery(1100, -30 + a.Clearance - 1));
        Assert.Equal("Too near a building", sim.WhyNotBattery(sim.Buildings[0].X, sim.Buildings[0].Z));
        Assert.Equal("A road is in the way", sim.WhyNotBattery(300, -60));
        Assert.Contains("level and dry", sim.WhyNotBattery(5000, 0));                                       // off the map
        Assert.Null(sim.WhyNotBattery(600 + a.Clearance + 1, -30 + a.Clearance));
        Build(sim, 600, 400);
        Assert.Equal("Too near another industry", sim.WhyNotBattery(600, 400 + a.Clearance - 1));          // another battery
        Assert.Null(sim.WhyNotBattery(600, 400 + a.Clearance + 1));
        Assert.Equal(-1, sim.Execute(new BuildBatteryCommand(300, -60)));
    }

    [Fact]
    public void ABattery_IsNotBuiltOnASlope_OrInTheWater()
    {
        var slope = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (x, _) => 5f + 0.3f * x), Tuned()), 5);
        var lake = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (x, _) => x > 1500f ? -4f : 5f), Tuned()), 5);

        Assert.Contains("level and dry", slope.WhyNotBattery(600, 400));
        Assert.Equal(-1, slope.Execute(new BuildBatteryCommand(600, 400)));
        Assert.Contains("level and dry", lake.WhyNotBattery(1700, 400));
        Assert.Null(lake.WhyNotBattery(1300, 400));
    }

    [Fact]
    public void ABattery_HasJobsFilledByResidents_AndStaffingIsTheShareFilled()
    {
        var sim = Region(Tuned(dayLength: 300f), town: true);
        sim.Company.Money = 10_000_000;
        var battery = Build(sim, 600, 400);
        int jobs = sim.JobsOf(battery);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(60, 60, battery.AccessX, battery.AccessZ, RoadType.TwoLane, 14f))); // the town's road to its access point
        Assert.True(sim.AccessNodeOf(battery) >= 0);

        Assert.True(jobs > 0);
        Assert.Equal(1f, sim.StaffingOf(battery)); // until a day closes

        RunDays(sim, 3);

        Assert.True(sim.StaffOf(battery) > 0, "the groups of the town went to work at the battery");
        Assert.Equal(MathF.Min(1f, sim.StaffOf(battery) / (float)jobs), sim.StaffingOf(battery), 0.0001f);
        Assert.InRange(sim.StaffingOf(battery), 0.01f, 1f);
    }

    // ---- Ammunition --------------------------------------------------------------------------------------------

    [Fact]
    public void Ammunition_IsAskedForAtAGateway_FromMonth20_AtTheRationedPriceAndSize()
    {
        var early = Region(Worlds.Late(Tuning.Load()));
        Assert.Contains("month 20", early.WhyNotImport(0, "ammunition"));
        Assert.Equal(0, early.Execute(new RequestImportCommand(0, "ammunition")));

        var sim = Region(Shipped(), IncidentFrequency.Normal);
        RunDays(sim, 3 * 20 + 1);
        var offer = sim.ImportOffers.Single(o => o.Cargo == "ammunition");
        int c = sim.AmmunitionCargo;

        Assert.Null(sim.WhyNotImport(0, "ammunition"));
        Assert.True(sim.RationingPriceFactor("ammunition") > 1f, "Mobilisation rations it as it rations fuel");
        Assert.True(sim.ImportUnits(offer) < offer.Units);
        long money = sim.Company.Money;
        Assert.Equal(1, sim.Execute(new RequestImportCommand(0, "ammunition")));
        Assert.Equal((long)sim.ImportUnits(offer) * sim.ImportPrice(offer), money - sim.Company.Money);
        Assert.Equal("A shipment is already on its way", sim.WhyNotImport(0, "ammunition"));

        Assert.True(StepUntil(sim, sim.GatewayTuning.ShipmentSeconds + 5, () => sim.Gateways[0].Stock[c] > 0f));
        Assert.Equal(sim.ImportUnits(offer), sim.Gateways[0].Stock[c], 0.01f);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.ShipmentArrived && e.Text.EndsWith("ammunition arrived at the Test Road Gate"));
    }

    [Fact]
    public void AStationThatCoversTheBattery_AcceptsAmmunition_AndDeliversItUpToTheStockLimit()
    {
        var sim = Region(Tuned());
        int ammunition = sim.AmmunitionCargo;
        int station = sim.Execute(new BuildStationCommand(500, -60));
        Assert.True(station >= 0);
        Assert.False(sim.Accepts(sim.Stations[station], ammunition), "nothing uses it yet");

        var battery = Build(sim, 500, -200, ammunition: 0f);

        Assert.True(sim.Accepts(sim.Stations[station], ammunition));
        Assert.Contains(ammunition, sim.StatusOf(battery).MissingInputs);
        Assert.Equal(50, sim.Distribute(sim.Stations[station], ammunition, 50));
        Assert.Equal(50f, battery.Input[ammunition], 0.01f);
        float room = sim.IndustryTuning.Types[sim.BatteryType].MaxInput - 50f;
        sim.Distribute(sim.Stations[station], ammunition, 500);
        Assert.Equal(room + 50f, battery.Input[ammunition], 0.01f);
        Assert.DoesNotContain(ammunition, sim.StatusOf(battery).MissingInputs);
        Assert.True(sim.StatusOf(battery).Working, "a battery makes nothing, so its output is never full");
        Assert.True(sim.StatusOf(battery).Covered);
    }

    // ---- Firing ------------------------------------------------------------------------------------------------

    [Fact]
    public void ASalvo_CostsAmmunition_ForEveryAircraftInRange_OnlyWhileTheyAre()
    {
        var sim = Region(Tuned(a => a with { HitChance = 0f }, aircraft: 3));
        var battery = Build(sim, 600, 400);
        int c = sim.AmmunitionCargo;
        float perSecond = sim.AntiAircraft.AmmoPerSalvo, step = perSecond * StepSeconds;
        sim.LaunchFlightForTest();
        var flight = sim.Flights[0];

        sim.DefendForTest(flight); // far outside the map, out of range
        Assert.Equal(100f, battery.Input[c]);

        flight.Travelled = flight.TargetDistance;
        sim.DefendForTest(flight);
        Assert.Equal(100f - 3 * step, battery.Input[c], 0.0001f);

        for (int k = 0; k < 19; k++) sim.DefendForTest(flight); // a second of it
        Assert.Equal(100f - 3 * perSecond, battery.Input[c], 0.001f);

        flight.Travelled = flight.TargetDistance + 3000f; // past it, out of range again
        sim.DefendForTest(flight);
        Assert.Equal(100f - 3 * perSecond, battery.Input[c], 0.001f);
    }

    /// <summary>The share of the aircraft of many flights, each over the target for a second, that were shot down.</summary>
    static double Share(Simulation sim, int flights, Action<Flight>? prepare = null, int ticks = 20)
    {
        int down = 0, total = 0;
        for (int n = 0; n < flights; n++)
        {
            foreach (var industry in sim.Industries)
                if (sim.IsBattery(industry)) industry.Input[sim.AmmunitionCargo] = 100f;
            var flight = Over(sim);
            prepare?.Invoke(flight);
            for (int t = 0; t < ticks; t++) sim.DefendForTest(flight);
            down += flight.ShotDown;
            total += flight.Aircraft;
        }
        return down / (double)total;
    }

    static Simulation Firing(int batteries = 1, float hit = 0.3f)
    {
        var sim = Region(Tuned(a => a with { HitChance = hit, Range = 3000f }, aircraft: 12), town: true); // with a town, so that a battery's staffing is its own
        for (int k = 0; k < batteries; k++) Build(sim, 300f * k, 400);
        return sim;
    }

    [Fact]
    public void AnAircraftInRange_IsShotDownWithTheChanceOfTheBattery_PerSecond()
    {
        double share = Share(Firing(), 40);

        Assert.InRange(share, 0.30 - 0.08, 0.30 + 0.08); // 30 % a second, for one second
        // Twice as long, one minus the miss twice over.
        Assert.InRange(Share(Firing(), 40, ticks: 40), 0.51 - 0.08, 0.51 + 0.08);
    }

    [Fact]
    public void WithoutAmmunition_OrWithNobodyToMan_AGunBringsNothingDown()
    {
        var empty = Firing();
        var unmanned = Firing();
        unmanned.Industries.Single(i => unmanned.IsBattery(i)).Staffing = 0f;

        double dry = 0;
        for (int n = 0; n < 20; n++)
        {
            empty.Industries.Single(i => empty.IsBattery(i)).Input[empty.AmmunitionCargo] = 0f;
            var flight = Over(empty);
            for (int t = 0; t < 20; t++) empty.DefendForTest(flight);
            dry += flight.ShotDown;
        }
        double idle = 0;
        for (int n = 0; n < 20; n++)
        {
            var flight = Over(unmanned);
            for (int t = 0; t < 20; t++) unmanned.DefendForTest(flight);
            idle += flight.ShotDown;
        }

        Assert.Equal(0, dry);
        Assert.Equal(0, idle);
        Assert.Equal(0, empty.AircraftShotDown + unmanned.AircraftShotDown);
        Assert.Equal(100f, unmanned.Industries.Single(i => unmanned.IsBattery(i)).Input[unmanned.AmmunitionCargo]); // nobody to fire it, nothing spent
    }

    [Fact]
    public void TheChance_IsTheBaseTimesTheStaffing()
    {
        var half = Firing();
        half.Industries.Single(i => half.IsBattery(i)).Staffing = 0.5f;

        // 0.3 * 0.5 = 15 % a second.
        Assert.InRange(Share(half, 40), 0.15 - 0.07, 0.15 + 0.07);
    }

    [Fact]
    public void SeveralBatteries_AddUp_AsOneMinusTheProductOfWhatEachMisses()
    {
        double two = Share(Firing(2), 40), three = Share(Firing(3), 40);

        Assert.InRange(two, 0.51 - 0.08, 0.51 + 0.08);   // 1 - 0.7 * 0.7
        Assert.InRange(three, 0.657 - 0.08, 0.657 + 0.08); // 1 - 0.7 ^ 3
        Assert.True(three > two);
    }

    [Fact]
    public void AGunOutOfRange_Misses_EvenWhenItNeverFails()
    {
        var sim = Region(Tuned(a => a with { HitChance = 1f, Range = 100f }, aircraft: 4));
        var battery = Build(sim, 600, 400);

        var flight = Over(sim); // 430 m from the battery with a range of 100
        for (int t = 0; t < 40; t++) sim.DefendForTest(flight);

        Assert.Equal(0, flight.ShotDown);
        Assert.Equal(100f, battery.Input[sim.AmmunitionCargo]);
        Assert.Equal(0, sim.AircraftShotDown);
    }

    [Fact]
    public void ADownedAircraft_FallsAndLeavesAFlashAndACraterWhereItLands_AndTheShotIsReported()
    {
        var sim = Region(Tuned(a => a with { HitChance = 1f, Range = 3000f, FallCarry = 0f }, aircraft: 2));
        Build(sim, 600, 400);
        var flight = Over(sim);
        // Over the road: the leader above (x, -60) on the road, if the path crosses it.
        float s = (-60f - flight.EntryZ) / flight.DirZ;
        Assert.InRange(flight.EntryX + flight.DirX * s, 100f, 1200f);
        flight.Travelled = s;
        var (hx, hz) = sim.AircraftPoint(flight, 0);
        int impacts = sim.ImpactCount;

        sim.DefendForTest(flight);

        Assert.Equal(2, flight.ShotDown);
        Assert.Equal(2, sim.AircraftShotDown);
        Assert.Equal(2, sim.FallingAircraft.Count);
        Assert.True(flight.IsDown(0) && flight.IsDown(1));
        var shot = sim.Events.Where(e => e.Text == "Anti-aircraft shot down an aircraft").ToList();
        Assert.Equal(2, shot.Count);
        Assert.Equal((hx, hz), (shot[0].X, shot[0].Z));
        Assert.All(shot, e => Assert.Equal(EventSeverity.Warning, e.Severity));
        var fall = sim.FallingAircraft[0];
        var (fx, height, fz) = sim.FallPoint(fall);
        Assert.Equal((hx, flight.Altitude, hz), (fx, height, fz));

        long fallTicks = fall.LandTick - fall.HitTick;
        Assert.InRange(fallTicks * StepSeconds, 6f, 10f); // as long as a bomb takes from 320 m
        for (long t = 0; t <= fallTicks; t++) sim.Step(); // it lands in the step after the one that reaches the tick

        Assert.Empty(sim.FallingAircraft);
        Assert.Equal(impacts + 2, sim.ImpactCount);
        var crash = sim.Impacts[impacts - sim.ImpactsDropped];
        Assert.Equal((hx, hz), (crash.X, crash.Z));
        Assert.Contains(sim.Incidents, i => i.Kind == IncidentKind.Bombing); // the road under it is cratered
    }

    [Fact]
    public void ADownedAircraft_DropsNoMoreBombs_AndTheFlightEndsWithWhatWasDropped()
    {
        // A battery on the approach, 585 m before the target, with a range of 12 m: the leader is over it between the third bomb of the stick (let go 607 m before the
        // target) and the fourth (577 m), and is hit with two bombs of five still in the aircraft.
        Simulation? sim = null;
        Industry? battery = null;
        Flight? flight = null;
        for (ulong seed = 1; seed < 40 && battery == null; seed++)
        {
            sim = Region(Tuned(a => a with { HitChance = 1f, Range = 12f }), seed: seed);
            sim.LaunchFlightForTest();
            flight = sim.Flights[0];
            float px = flight.EntryX + flight.DirX * (flight.TargetDistance - 585f), pz = flight.EntryZ + flight.DirZ * (flight.TargetDistance - 585f);
            if (sim.WhyNotBattery(px, pz) is null) battery = Build(sim, px, pz);
        }
        Assert.NotNull(battery);
        var f = flight!;
        int planned = f.BombsPlanned;
        Assert.Equal(5, planned);

        Fly(sim!);

        Assert.Equal(1, f.ShotDown);
        Assert.Equal(3, f.BombsDropped); // the bombs let go before it was hit
        Assert.Equal(3, f.BombsPlanned);
        Assert.Equal(3, sim!.Bombs);
        Assert.Equal(1, sim.AircraftShotDown);
        Assert.Contains(sim.Events, e => e.Text.StartsWith("Air raid over: 3 bombs dropped") && e.Text.Contains("1 aircraft shot down"));
    }

    [Fact]
    public void AFlightWhoseAircraftAreAllDownBeforeTheyDrop_DropsNothing_AndEnds()
    {
        var sim = Region(Tuned(a => a with { HitChance = 1f, Range = 5000f }, aircraft: 3));
        Build(sim, 600, 400);
        sim.LaunchFlightForTest();
        var flight = sim.Flights[0];

        Fly(sim);

        Assert.Equal(3, flight.ShotDown);
        Assert.Equal(0, sim.Bombs);
        Assert.Equal(0, flight.BombsPlanned);
        Assert.Equal(3, sim.Impacts.Count(i => i.Flight == flight.Id)); // the three that came down, no bomb
        Assert.Equal(3, sim.Events.Count(e => e.Text == "Anti-aircraft shot down an aircraft"));
        Assert.Contains(sim.Events, e => e.Text.StartsWith("Air raid over: 0 bombs dropped") && e.Text.Contains("3 aircraft shot down"));
        Assert.False(sim.StatusOf(sim.Industries[0]).Damaged, "the sawmill was not touched");
    }

    // ---- Under fire --------------------------------------------------------------------------------------------

    /// <summary>The impacts of one flight of a lone aircraft with a long stick, as (metres along the path from the target, metres across it).</summary>
    static List<(float Along, float Across)> Stick(Simulation sim)
    {
        sim.LaunchFlightForTest();
        var flight = sim.Flights[0];
        int first = sim.ImpactCount;
        Fly(sim);
        var list = new List<(float, float)>();
        for (int id = first; id < sim.ImpactCount; id++)
        {
            var i = sim.Impacts[id - sim.ImpactsDropped];
            if (i.Flight != flight.Id) continue;
            float dx = i.X - flight.EntryX, dz = i.Z - flight.EntryZ;
            list.Add((dx * flight.DirX + dz * flight.DirZ - flight.TargetDistance, -dx * flight.DirZ + dz * flight.DirX));
        }
        return list;
    }

    static Tuning Scattering(Func<AntiAircraftTuning, AntiAircraftTuning>? defence = null) =>
        Tuned(a => (defence == null ? a : defence(a)) with { HitChance = 0f, Range = 4000f },
            b => b with { BombsPerAircraft = 20, Scatter = 12f, ScatterAlong = 8f });

    [Fact]
    public void UnderFire_TheBombsScatterMore_AndAreLetGoEarly_SoTheyFallShort()
    {
        var calm = Region(Scattering());
        var fired = Region(Scattering());
        var battery = Build(fired, 600, 400);
        var a = fired.AntiAircraft;

        var plain = Stick(calm);
        var under = Stick(fired);

        Assert.Equal(20, plain.Count);
        Assert.Equal(20, under.Count);
        Assert.True(battery.Input[fired.AmmunitionCargo] < 100f, "the battery fired all the while");
        Assert.InRange(plain.Select(p => p.Along).Average(), -10f, 10f);
        Assert.True(plain.Max(p => MathF.Abs(p.Across)) <= 12.01f);
        // A pressure of 1 (one battery manned in full): everything is let go the early distance short, and the scatter is 1 + growth times as much.
        float factor = 1f + a.ScatterGrowth;
        Assert.InRange(under.Select(p => p.Along).Average(), -a.EarlyRelease - 10f * factor, -a.EarlyRelease + 10f * factor);
        Assert.True(under.Max(p => MathF.Abs(p.Across)) > 12.5f, "wider than the calm stick can be");
        Assert.True(under.Max(p => MathF.Abs(p.Across)) <= 12.01f * factor);
    }

    [Fact]
    public void ThePressure_IsTheStaffingOfTheBatteriesFiring_AndTheScatterHasACap()
    {
        var sim = Region(Scattering(a => a with { ScatterCap = 2f, Range = 4000f }), town: true);
        var one = Build(sim, 600, 400);
        var two = Build(sim, 900, 400);
        var tuning = sim.AntiAircraft;
        var (x, z) = sim.AircraftPoint(Over(sim), 0);

        Assert.Equal(2f, sim.FirePressure(x, z));
        one.Staffing = 0.25f;
        Assert.Equal(1.25f, sim.FirePressure(x, z));
        two.Input[sim.AmmunitionCargo] = 0f;
        Assert.Equal(0.25f, sim.FirePressure(x, z));
        one.Input[sim.AmmunitionCargo] = 0f;
        Assert.Equal(0f, sim.FirePressure(x, z));
        Assert.Equal(1f, sim.ScatterFactor(0f));
        Assert.Equal(1f + tuning.ScatterGrowth * 0.25f, sim.ScatterFactor(0.25f), 0.0001f);
        Assert.Equal(2f, sim.ScatterFactor(5f)); // the cap
        Assert.Equal(tuning.EarlyRelease * 0.25f, sim.EarlyRelease(0.25f), 0.0001f);
        Assert.Equal(tuning.EarlyRelease, sim.EarlyRelease(5f));
    }

    [Fact]
    public void ABatteryWithoutAmmunition_ChangesNothingAboutTheRaid_NotEvenTheBombsThatFall()
    {
        var control = Region(Scattering());
        var dry = Region(Scattering());
        Build(dry, 600, 400, ammunition: 0f);

        Assert.Equal(Stick(control), Stick(dry));
    }

    // ---- The enemy weighs its targets --------------------------------------------------------------------------

    static Simulation TwoSawmills(Tuning t, out Industry covered, out Industry bare)
    {
        var sim = Region(t, IncidentFrequency.Off, 5, null, true, new IndustryPlan(Sawmill, 700, 1500, 0));
        bare = sim.Industries[0];
        covered = sim.Industries[1];
        return sim;
    }

    static float WeightOf(Simulation sim, Industry industry) => sim.CandidatesForTest().Single(c => c.Kind == RaidTargetKind.Industry && c.Id == industry.Id).Weight;

    [Fact]
    public void ATargetUnderCover_IsWantedLess_AboutHalfAsMuchAtStrongCover()
    {
        var sim = TwoSawmills(Tuned(), out var covered, out var bare);
        Assert.Equal(WeightOf(sim, bare), WeightOf(sim, covered));
        var a = sim.AntiAircraft;
        var one = Build(sim, 700, 1700);          // 200 m from the sawmill, 1700 from the other
        Assert.True(Distance(one.X, one.Z, bare.X, bare.Z) > a.Range);

        Assert.Equal(1f - (1f - a.StrongCover) * (1f / a.CoverSaturation), WeightOf(sim, covered) / WeightOf(sim, bare), 0.0001f);
        var two = Build(sim, 900, 1700);
        Assert.Equal(a.StrongCover, WeightOf(sim, covered) / WeightOf(sim, bare), 0.0001f); // full cover: "about half"
        Build(sim, 500, 1700);
        Assert.Equal(a.StrongCover, WeightOf(sim, covered) / WeightOf(sim, bare), 0.0001f); // and it saturates there
        one.Staffing = 0f;
        two.Staffing = 0.5f;
        Assert.True(WeightOf(sim, covered) > WeightOf(sim, bare) * a.StrongCover);
    }

    [Fact]
    public void TheCover_IsOfTheBatteriesThatWork_NotOfTheOnesBombedOrOutOfReach()
    {
        var sim = TwoSawmills(Tuned(), out var covered, out var bare);
        var battery = Build(sim, 700, 1700, ammunition: 0f);

        Assert.True(WeightOf(sim, covered) < WeightOf(sim, bare), "the enemy cannot see the stock of ammunition");
        battery.DamagedUntil = sim.Tick + 1000;
        Assert.Equal(WeightOf(sim, bare), WeightOf(sim, covered));
        battery.DamagedUntil = 0;
        Assert.True(WeightOf(sim, covered) < WeightOf(sim, bare));
        Assert.DoesNotContain(sim.CandidatesForTest(), c => c.Kind == RaidTargetKind.Industry && c.Id == battery.Id); // the enemy wants sawmills only here
    }

    [Fact]
    public void Defence_MovesTheFlightsToTheWeakerTarget_OverManyLaunches()
    {
        int Picked(Simulation sim, Industry industry, int launches = 600)
        {
            int picked = 0;
            for (int n = 0; n < launches; n++)
            {
                sim.LaunchFlightForTest();
                if (sim.Flights[^1].TargetId == industry.Id) picked++;
            }
            return picked;
        }

        var open = TwoSawmills(Tuned(), out var coveredOpen, out _);
        var strong = TwoSawmills(Tuned(), out var coveredStrong, out _);
        Build(strong, 700, 1700);
        Build(strong, 900, 1700);
        var light = TwoSawmills(Tuned(), out var coveredLight, out _);
        Build(light, 700, 1700);

        double without = Picked(open, coveredOpen) / 600.0, half = Picked(light, coveredLight) / 600.0, full = Picked(strong, coveredStrong) / 600.0;

        Assert.InRange(without, 0.5 - 0.07, 0.5 + 0.07);
        Assert.InRange(half, 0.75 / 1.75 - 0.07, 0.75 / 1.75 + 0.07);   // 0.43
        Assert.InRange(full, 0.5 / 1.5 - 0.07, 0.5 / 1.5 + 0.07);       // 0.33
        Assert.True(full < half && half < without);
    }

    // ---- Events ------------------------------------------------------------------------------------------------

    [Fact]
    public void ABatteryOutOfAmmunition_SaysSoOnceUntilItHasSomeAgain()
    {
        var sim = Region(Tuned(a => a with { HitChance = 0f, Range = 3000f }));
        var battery = Build(sim, 600, 400);
        var flight = Over(sim);
        int c = sim.AmmunitionCargo;
        float step = sim.AntiAircraft.AmmoPerSalvo * StepSeconds;
        battery.Input[c] = step * 2.5f; // two steps of firing and a bit

        for (int t = 0; t < 10; t++) sim.DefendForTest(flight);

        Assert.Single(sim.Events, e => e.Text == OutOfAmmunition);
        Assert.Equal(step * 0.5f, battery.Input[c], 0.0001f);
        var said = sim.Events.Single(e => e.Text == OutOfAmmunition);
        Assert.Equal((battery.X, battery.Z, EventSeverity.Warning, battery.Id), (said.X, said.Z, said.Severity, said.Subject));

        battery.Input[c] = 50f; // trucks have brought some
        for (int t = 0; t < 10; t++) sim.DefendForTest(flight);
        Assert.Single(sim.Events, e => e.Text == OutOfAmmunition);
        battery.Input[c] = 0f;
        for (int t = 0; t < 10; t++) sim.DefendForTest(flight);

        Assert.Equal(2, sim.Events.Count(e => e.Text == OutOfAmmunition));
    }

    [Fact]
    public void TheNews_TellsOfTheGunsAtMonth20_AndOfRaidsAtMonth40()
    {
        var sim = Region(Shipped(), IncidentFrequency.Normal);
        bool Has(string text) => sim.Events.Any(e => e.Kind == SimEventKind.War && e.Text.StartsWith(text));

        RunDays(sim, 3 * 20 - 1);
        Assert.False(Has("Anti-aircraft guns can now be built"));
        RunDays(sim, 1);
        Assert.True(Has("Anti-aircraft guns can now be built"));
        Assert.Contains("45000 each", sim.Events.Single(e => e.Text.StartsWith("Anti-aircraft guns can now")).Text);

        RunDays(sim, 3 * 20 - 1);
        Assert.False(Has("Enemy raids are expected"));
        RunDays(sim, 1);
        Assert.True(Has("Enemy raids are expected"));
        Assert.Equal(EventSeverity.Serious, sim.Events.Single(e => e.Text.StartsWith("Enemy raids are expected")).Severity);
    }

    [Fact]
    public void WithTheFrequencyOff_TheNewsOfRaidsIsNotGiven()
    {
        var sim = Region(Shipped(), IncidentFrequency.Off);

        RunDays(sim, 3 * 41);

        Assert.Contains(sim.Events, e => e.Text.StartsWith("Anti-aircraft guns can now be built"));
        Assert.DoesNotContain(sim.Events, e => e.Text.StartsWith("Enemy raids are expected"));
    }

    // ---- A battery that is bombed ------------------------------------------------------------------------------

    [Fact]
    public void ABombedBattery_IsDamagedLikeAnyIndustry_AndStopsFiringUntilItIsWorkingAgain()
    {
        var sim = Region(Tuned(a => a with { HitChance = 1f, Range = 3000f }, aircraft: 3));
        var battery = Build(sim, 600, 400);

        var bomb = sim.BombForTest(600, 400);

        Assert.Equal(1, bomb.IndustriesHit);
        Assert.True(battery.DamagedUntil > sim.Tick);
        Assert.True(sim.StatusOf(battery).Damaged);
        Assert.True(battery.Input[sim.AmmunitionCargo] < 100f, "the bomb took some of the stock too");
        float stock = battery.Input[sim.AmmunitionCargo];
        var flight = Over(sim);
        for (int t = 0; t < 40; t++) sim.DefendForTest(flight);
        Assert.Equal(0, flight.ShotDown);
        Assert.Equal(stock, battery.Input[sim.AmmunitionCargo]);
        Assert.DoesNotContain(sim.Events, e => e.Text == OutOfAmmunition);

        RunDays(sim, sim.WarTuning.Bombing.IndustryDownDays + 1);
        Assert.False(sim.StatusOf(battery).Damaged);
        Assert.Contains(sim.Events, e => e.Text == "The anti-aircraft battery is working again");
        var next = Over(sim);
        for (int t = 0; t < 40; t++) sim.DefendForTest(next);
        Assert.Equal(3, next.ShotDown);
    }

    // ---- Determinism -------------------------------------------------------------------------------------------

    static (ulong Hash, int Down, int Bombs) Scenario(ulong seed, float ammunition)
    {
        var sim = Region(Tuned(a => a with { HitChance = 0.35f, Range = 900f }, aircraft: 4), IncidentFrequency.Normal, seed);
        Build(sim, 600, 400, ammunition);
        Build(sim, 600, -400, ammunition, 0.8f);
        RunDays(sim, 6);
        return (sim.StateHash(), sim.AircraftShotDown, sim.Bombs);
    }

    [Fact]
    public void TheShots_ReplayExactly_FromTheSameSeed_AndDifferWithAnother()
    {
        var a = Scenario(7, 100f);
        var b = Scenario(7, 100f);
        var other = Scenario(8, 100f);
        var dry = Scenario(7, 0f);

        Assert.Equal(a, b);
        Assert.True(a.Down > 0, "something was shot down in the scenario");
        Assert.NotEqual(a.Hash, other.Hash);
        Assert.NotEqual(a.Hash, dry.Hash);
        Assert.Equal(0, dry.Down);
        Assert.True(dry.Bombs > a.Bombs, "the guns saved some bombs from being dropped");
    }

    [Fact]
    public void TheAmmunitionStockTheOutOfAmmunitionFlagAndTheFallingAircraft_AreInTheStateHash()
    {
        var sim = Region(Tuned(a => a with { HitChance = 1f, Range = 3000f }, aircraft: 2), IncidentFrequency.Normal);
        var battery = Build(sim, 600, 400);
        sim.Step(); // the raids have begun, and so are in the hash
        ulong start = sim.StateHash();

        battery.Input[sim.AmmunitionCargo] -= 1f;
        ulong stock = sim.StateHash();
        battery.OutOfAmmo = true;
        ulong flag = sim.StateHash();
        battery.OutOfAmmo = false;
        battery.Input[sim.AmmunitionCargo] += 1f;
        Assert.Equal(start, sim.StateHash());
        var flight = Over(sim);
        ulong flying = sim.StateHash();
        sim.DefendForTest(flight);
        ulong falling = sim.StateHash();

        Assert.Equal(5, new[] { start, stock, flag, flying, falling }.Distinct().Count());
    }

    [Fact]
    public void ABattery_AddsToTheCommandLog_AndTheLogReplaysIt()
    {
        var sim = Region(Tuned(), IncidentFrequency.Normal);
        RunDays(sim, 1);
        Build(sim, 600, 400, ammunition: 0f);
        RunDays(sim, 2);
        Assert.Equal(-1, sim.Execute(new BuildBatteryCommand(300, -60)));
        RunDays(sim, 1);

        var again = Region(Tuned(), IncidentFrequency.Normal);
        again.Replay(sim.CommandLog.Skip(again.CommandLog.Count).ToList(), sim.Tick);

        Assert.Equal(sim.StateHash(), again.StateHash());
    }
}
