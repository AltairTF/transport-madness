using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Editing a line, naming it, what a station and a vehicle remember, and stations with bays.</summary>
public class LineEditAndBayTests
{
    /// <summary>A straight road 1200 m long, a depot at its west end, stations at x = 150 and x = 1050 (with bays if asked) and a third at x = 600.</summary>
    static (Simulation Sim, int Depot, int A, int B, int C) StraightLine(bool bays = false, Tuning? tuning = null)
    {
        var net = new RoadNetwork(Terrain.Flat(), tuning);
        var sim = new Simulation(net, 5);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f)));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(150, 0, bays));
        int b = sim.Execute(new BuildStationCommand(1050, 0, bays));
        int c = sim.Execute(new BuildStationCommand(600, 0, bays));
        Assert.True(depot >= 0 && a >= 0 && b >= 0 && c >= 0);
        return (sim, depot, a, b, c);
    }

    static void Run(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    // ---- Editing lines ----------------------------------------------------------------------------------------------

    [Fact]
    public void AddingAStop_SendsTheVehiclesThroughIt()
    {
        var (sim, depot, a, b, c) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Run(sim, 0.05);

        Assert.Equal(1, sim.Execute(new EditLineCommand(line, new[] { a, c, b })));

        Assert.Equal(new[] { a, c, b }, sim.Lines[line].Stops);
        bool stoppedAtC = false;
        for (long t = 0; t < 12000 && !stoppedAtC; t++)
        {
            sim.Step();
            var info = sim.GetFleetInfo(bus);
            stoppedAtC = info.Loading && sim.Lines[line].Stops[info.NextStop] == c;
        }
        Assert.True(stoppedAtC, "the bus never stopped at the new station");
    }

    [Fact]
    public void ABusKeepsHeadingForItsStation_WhenThePlaceItHasInTheListChanges()
    {
        var (sim, depot, a, b, c) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        // Drive until it has loaded at a and is heading for b.
        for (long t = 0; t < 20000 && !(sim.GetFleetInfo(bus).NextStop == 1 && !sim.GetFleetInfo(bus).Loading); t++) sim.Step();
        Assert.Equal(b, sim.TargetStationOf(bus));

        sim.Execute(new EditLineCommand(line, new[] { c, a, b }));

        Assert.Equal(b, sim.TargetStationOf(bus));
        Assert.Equal(2, sim.GetFleetInfo(bus).NextStop);
    }

    [Fact]
    public void ABusWhoseStationIsTakenOffTheLine_HeadsForTheStopInItsPlace()
    {
        var (sim, depot, a, b, c) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, c, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Assert.Equal(a, sim.TargetStationOf(bus));

        sim.Execute(new EditLineCommand(line, new[] { b, c }));

        Assert.Contains(sim.TargetStationOf(bus), new[] { b, c });
        Run(sim, 2);
        Assert.Equal(VehicleKind.Car, sim.GetVehicle(bus).Kind);
        Assert.Empty(sim.Incidents);
    }

    [Fact]
    public void EditingALine_IsRefusedForTheSameReasonsAsMakingOne()
    {
        var (sim, _, a, b, c) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));

        Assert.Equal(0, sim.Execute(new EditLineCommand(line, new[] { a })));
        Assert.Equal(0, sim.Execute(new EditLineCommand(line, new[] { a, a })));
        Assert.Equal(0, sim.Execute(new EditLineCommand(line, new[] { a, b, b })));
        Assert.Equal(0, sim.Execute(new EditLineCommand(line, new[] { a, b, a })));    // it would run a to a
        Assert.Equal(0, sim.Execute(new EditLineCommand(line, new[] { a, 9 })));
        Assert.Equal(0, sim.Execute(new EditLineCommand(5, new[] { a, c })));
        Assert.Equal(new[] { a, b }, sim.Lines[line].Stops);

        sim.Execute(new DeleteLineCommand(line));
        Assert.Equal(0, sim.Execute(new EditLineCommand(line, new[] { a, c })));
    }

    [Fact]
    public void ALine_CanBeNamed_WithAShortNonEmptyName()
    {
        var (sim, _, a, b, _) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.Equal("Line 1", sim.Lines[line].Name);

        Assert.Equal(1, sim.Execute(new RenameLineCommand(line, "  Harbour express ")));
        Assert.Equal("Harbour express", sim.Lines[line].Name);

        Assert.Equal(0, sim.Execute(new RenameLineCommand(line, "   ")));
        Assert.Equal(0, sim.Execute(new RenameLineCommand(line, new string('x', 25))));
        Assert.Equal(0, sim.Execute(new RenameLineCommand(7, "Nowhere")));
        Assert.Equal("Harbour express", sim.Lines[line].Name);
    }

    [Fact]
    public void AnEditedLine_ReplaysFromTheCommandLog()
    {
        var (sim, depot, a, b, c) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Run(sim, 0.2);
        sim.Execute(new EditLineCommand(line, new[] { a, c, b }));
        sim.Execute(new RenameLineCommand(line, "Coast"));
        Run(sim, 0.5);

        var (fresh, _, _, _, _) = StraightLine();
        // The stations were built through commands before the log of the run proper starts, so replay from the first line command on.
        foreach (var logged in sim.CommandLog.Where(l => l.Command is CreateLineCommand or BuyVehicleCommand or EditLineCommand or RenameLineCommand))
        {
            while (fresh.Tick < logged.Tick) fresh.Step();
            fresh.Execute(logged.Command);
        }
        while (fresh.Tick < sim.Tick) fresh.Step();

        Assert.Equal(sim.StateHash(), fresh.StateHash());
        Assert.Equal("Coast", fresh.Lines[line].Name);
    }

    // ---- What stations and vehicles remember ------------------------------------------------------------------------

    [Fact]
    public void AVehicleKeepsCountOfItsUpkeep_ForTheDaysItHasWorked()
    {
        var (sim, depot, a, b, _) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));

        Run(sim, 3.01);

        Assert.Equal(3 * (long)sim.Network.Tuning.Vehicles.First(v => v.Id == "bus").UpkeepPerDay, sim.UpkeepPaidFor(bus));
        Assert.Equal(sim.Lines[line].Upkeep, sim.UpkeepPaidFor(bus));
    }

    // ---- Bays -----------------------------------------------------------------------------------------------------

    [Fact]
    public void AStationWithBays_CostsAndCostsToKeepMore()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 1);
        sim.Execute(new BuildRoadCommand(0, 0, 900, 0, RoadType.TwoLane, 14f));
        var tuning = net.Tuning.Stations;
        long money = sim.Company.Money;

        int plain = sim.Execute(new BuildStationCommand(300, 0));
        int bays = sim.Execute(new BuildStationCommand(600, 0, true));

        Assert.False(sim.Stations[plain].HasBays);
        Assert.True(sim.Stations[bays].HasBays);
        Assert.Equal(money - 2 * tuning.Price - tuning.BayPrice, sim.Company.Money);
        long afterBuilding = sim.Company.Money;

        Run(sim, 1.01);

        Assert.Equal(afterBuilding - 2 * (long)tuning.UpkeepPerDay - (long)tuning.BayUpkeepPerDay, sim.Company.Money);
    }

    [Fact]
    public void AStationWithBays_IsRefusedWhenTheCompanyCannotPayTheExtra()
    {
        var poor = Tuning.Load();
        poor = poor with { Economy = poor.Economy with { StartingMoney = 46000 } };
        var net = new RoadNetwork(Terrain.Flat(), poor);
        var sim = new Simulation(net, 1);
        sim.Execute(new BuildRoadCommand(0, 0, 900, 0, RoadType.TwoLane, 14f)); // costs most of the money
        long left = sim.Company.Money;
        Assert.InRange(left, poor.Stations.Price, poor.Stations.Price + poor.Stations.BayPrice - 1); // enough for a station, not for its bays

        Assert.Equal(-1, sim.Execute(new BuildStationCommand(600, 0, true)));
        Assert.Equal(left, sim.Company.Money);
        Assert.True(sim.Execute(new BuildStationCommand(600, 0)) >= 0);
    }

    /// <summary>Where a car that drives up behind a loading bus is a few seconds after the bus started loading: 1 if it has driven past, 0 if it is still behind.</summary>
    static int CarPassesLoadingBus(bool bays)
    {
        var (sim, depot, a, b, _) = StraightLine(bays);
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        for (long t = 0; t < 20000 && !sim.GetFleetInfo(bus).Loading; t++) sim.Step();
        Assert.True(sim.GetFleetInfo(bus).Loading, "the bus never reached its first stop");
        Assert.Equal(bays, sim.InBay(bus));

        var lane = sim.GetVehicle(bus).Lane;
        float busPosition = sim.GetVehicle(bus).Position;
        int car = sim.SpawnVehicle(lane, busPosition - 40f, 14f);
        sim.SetKeepLane(car, true);
        for (int t = 0; t < 90 && sim.GetFleetInfo(bus).Loading; t++) sim.Step(); // 4.5 s, inside the time it takes to load

        Assert.True(sim.GetFleetInfo(bus).Loading, "the bus had finished loading before the car came by");
        var state = sim.GetVehicle(car);
        bool passed = state.Lane.Value != lane.Value || state.Position > busPosition;
        Assert.Empty(sim.Incidents);
        return passed ? 1 : 0;
    }

    [Fact]
    public void ACarDrivesPastABusLoadingInABay_ButWaitsBehindOneLoadingInTheLane()
    {
        Assert.Equal(0, CarPassesLoadingBus(bays: false));
        Assert.Equal(1, CarPassesLoadingBus(bays: true));
    }

    [Fact]
    public void ABusInABay_PullsBackIntoTheLaneAndCarriesOn()
    {
        var (sim, depot, a, b, _) = StraightLine(bays: true);
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));

        bool wasInBay = false, leftBay = false;
        for (long t = 0; t < 40000; t++)
        {
            sim.Step();
            bool inBay = sim.InBay(bus);
            wasInBay |= inBay;
            leftBay |= wasInBay && !inBay;
            // A bus in a bay is standing still and counted by its station.
            if (inBay)
            {
                Assert.Equal(0f, sim.GetVehicle(bus).Speed);
                Assert.Equal(1, sim.Stations.Sum(s => s.InBay));
            }
        }

        Assert.True(wasInBay, "the bus never used a bay");
        Assert.True(leftBay, "the bus never left it");
        Assert.True(sim.Lines[line].Delivered > 0);
        Assert.Empty(sim.Incidents);
    }

    [Fact]
    public void AStationHasRoomInItsBaysForNoMoreThanTheTuningAllows_ThenVehiclesLoadInTheLane()
    {
        var tuning = Tuning.Load();
        tuning = tuning with { Stations = tuning.Stations with { BayCapacity = 1 } };
        var (sim, depot, a, b, _) = StraightLine(bays: true, tuning);
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        for (int k = 0; k < 3; k++)
        {
            Assert.True(sim.Execute(new BuyVehicleCommand(depot, "bus", line)) >= 0);
            Run(sim, 0.02);
        }

        int most = 0, loadingInLane = 0;
        for (long t = 0; t < 24000; t++)
        {
            sim.Step();
            foreach (var station in sim.Stations) Assert.InRange(station.InBay, 0, 1);
            int counted = 0;
            for (int v = 0; v < sim.VehicleCount; v++)
                if (sim.IsCompanyVehicle(v) && sim.InBay(v)) counted++;
            Assert.Equal(sim.Stations.Sum(s => s.InBay), counted);
            most = Math.Max(most, counted);
            for (int v = 0; v < sim.VehicleCount; v++)
                if (sim.IsCompanyVehicle(v) && sim.GetFleetInfo(v).Loading && !sim.InBay(v)) { loadingInLane++; break; }
        }

        Assert.True(most is 0 or 1, $"{most} vehicles in the bays of a station with room for one");
        Assert.True(most == 1, "no bus ever used the bay");
        Assert.True(loadingInLane > 0, "a second bus never had to load in the lane");
    }

    [Fact]
    public void ABayedRunReplaysTheSame()
    {
        ulong Hash()
        {
            var (sim, depot, a, b, c) = StraightLine(bays: true);
            int line = sim.Execute(new CreateLineCommand(new[] { a, c, b }));
            for (int k = 0; k < 3; k++) { sim.Execute(new BuyVehicleCommand(depot, "bus", line)); Run(sim, 0.02); }
            sim.SeedTraffic(30);
            Run(sim, 2);
            return sim.StateHash();
        }

        Assert.Equal(Hash(), Hash());
    }

    // ---- Colour, names, closing and bays by hand ------------------------------------------------------------------

    [Fact]
    public void ALineTakesTheColourThePlayerGivesIt_AndRefusesAnythingElse()
    {
        var (sim, _, a, b, _) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.Equal(-1, sim.Lines[line].Color);

        Assert.Equal(1, sim.Execute(new SetLineColorCommand(line, 0x3366CC)));
        Assert.Equal(0x3366CC, sim.Lines[line].Color);
        Assert.Equal(0, sim.Execute(new SetLineColorCommand(line, 0x1000000)));
        Assert.Equal(0, sim.Execute(new SetLineColorCommand(line, -5)));
        Assert.Equal(0, sim.Execute(new SetLineColorCommand(99, 0xFFFFFF)));
        Assert.Equal(0x3366CC, sim.Lines[line].Color);
    }

    [Fact]
    public void AStationCanBeRenamed_AndTheColourAndNameDoNotChangeTheState()
    {
        var (sim, _, a, b, _) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        ulong before = sim.StateHash();

        Assert.Equal(1, sim.Execute(new RenameStationCommand(a, "  Central  ")));
        sim.Execute(new SetLineColorCommand(line, 0xAA0000));

        Assert.Equal("Central", sim.Stations[a].Name);
        Assert.Equal(before, sim.StateHash());
        Assert.Equal(0, sim.Execute(new RenameStationCommand(a, "")));
        Assert.Equal(0, sim.Execute(new RenameStationCommand(a, new string('x', 25))));
        Assert.Equal(0, sim.Execute(new RenameStationCommand(77, "Nowhere")));
        Assert.Equal("Central", sim.Stations[a].Name);
    }

    [Fact]
    public void ClosingAStation_MakesBusesSkipIt()
    {
        var (sim, depot, a, b, c) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, c, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Run(sim, 0.05);

        Assert.Equal(1, sim.Execute(new CloseStationCommand(c)));

        Assert.False(sim.Stations[c].Active);
        Assert.Equal(0, sim.Execute(new CloseStationCommand(c))); // already closed
        Assert.Equal(0, sim.Execute(new RenameStationCommand(c, "Gone")));
        var stoppedAt = new HashSet<int>();
        for (long t = 0; t < 24000; t++)
        {
            sim.Step();
            var info = sim.GetFleetInfo(bus);
            if (info.Loading) stoppedAt.Add(sim.Lines[line].Stops[info.NextStop]);
        }
        Assert.Contains(a, stoppedAt);
        Assert.Contains(b, stoppedAt);
        Assert.DoesNotContain(c, stoppedAt);
        Assert.Equal(VehicleKind.Car, sim.GetVehicle(bus).Kind);
    }

    [Fact]
    public void ANewStationCanStandWhereAClosedOneWas()
    {
        var (sim, _, _, _, c) = StraightLine();
        int node = sim.Stations[c].Node;
        Assert.NotNull(sim.StationAt(node));

        sim.Execute(new CloseStationCommand(c));

        Assert.Null(sim.StationAt(node));
        Assert.True(sim.Execute(new BuildStationCommand(600, 0)) >= 0);
    }

    [Fact]
    public void BaysCanBeAddedToAnExistingStation_ForTheirPrice_Once()
    {
        var (sim, _, a, _, _) = StraightLine();
        sim.Execute(new TakeLoanCommand(100000));
        Assert.False(sim.Stations[a].HasBays);
        long money = sim.Company.Money;
        ulong before = sim.StateHash();

        Assert.Equal(1, sim.Execute(new AddStationBaysCommand(a)));

        Assert.True(sim.Stations[a].HasBays);
        Assert.Equal(money - sim.StationBayPrice, sim.Company.Money);
        Assert.NotEqual(before, sim.StateHash());
        Assert.Equal(0, sim.Execute(new AddStationBaysCommand(a))); // it has them already
        Assert.Equal(money - sim.StationBayPrice, sim.Company.Money);
    }

    [Fact]
    public void BaysAreRefused_WhenTheCompanyCannotPayOrTheStationIsClosed()
    {
        var (sim, _, a, b, _) = StraightLine();
        sim.Execute(new CloseStationCommand(b));
        Assert.Equal(0, sim.Execute(new AddStationBaysCommand(b)));

        sim.Company.Money = 0;

        Assert.Equal(0, sim.Execute(new AddStationBaysCommand(a)));
        Assert.False(sim.Stations[a].HasBays);
    }

    [Fact]
    public void TheNewCommands_ReplayFromTheLog()
    {
        var (sim, depot, a, b, c) = StraightLine();
        sim.Execute(new TakeLoanCommand(100000));
        int line = sim.Execute(new CreateLineCommand(new[] { a, c, b }));
        sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Run(sim, 0.02);
        sim.Execute(new AddStationBaysCommand(a));
        sim.Execute(new RenameStationCommand(b, "Harbour"));
        sim.Execute(new SetLineColorCommand(line, 0x00AA55));
        Run(sim, 0.02);
        sim.Execute(new CloseStationCommand(c));
        Run(sim, 0.05);

        var (twin, _, _, _, _) = StraightLine();
        int setup = 5; // the road, the depot and three stations are built by StraightLine itself
        twin.Replay(sim.CommandLog.Skip(setup).ToList(), sim.Tick);

        Assert.Equal(sim.StateHash(), twin.StateHash());
        Assert.Equal("Harbour", twin.Stations[b].Name);
        Assert.Equal(0x00AA55, twin.Lines[line].Color);
    }
}
