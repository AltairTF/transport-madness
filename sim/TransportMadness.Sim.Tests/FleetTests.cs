using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Stations, lines and company vehicles: routes, stops, loading, pay and upkeep.</summary>
public class FleetTests
{
    /// <summary>A straight road 1200 m long with a depot at its west end and stations at x = 150 and x = 1050.</summary>
    static (Simulation Sim, int Depot, int A, int B) StraightLine(Tuning? tuning = null)
    {
        var net = new RoadNetwork(Terrain.Flat(), tuning);
        var sim = new Simulation(net, 5);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f)));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(150, 0));
        int b = sim.Execute(new BuildStationCommand(1050, 0));
        Assert.True(depot >= 0 && a >= 0 && b >= 0);
        return (sim, depot, a, b);
    }

    static void Run(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    [Fact]
    public void Station_SplitsTheRoadItStandsOn_AndCostsMoney()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 1);
        sim.Execute(new BuildRoadCommand(0, 0, 600, 0, RoadType.TwoLane, 14f));
        long before = sim.Company.Money;

        int id = sim.Execute(new BuildStationCommand(300, 0));

        Assert.Equal(0, id);
        Assert.Equal(2, net.RoadsAt(sim.Stations[0].Node).Count);
        Assert.Equal(before - sim.Network.Tuning.Stations.Price, sim.Company.Money);
        Assert.Equal(-sim.Network.Tuning.Stations.Price, sim.Company.Total(LedgerEntry.Stations));
    }

    [Fact]
    public void Station_IsRefusedWhereThereIsNoRoom_OrAStationAlready()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 1);
        sim.Execute(new BuildRoadCommand(0, 0, 600, 0, RoadType.TwoLane, 14f));

        Assert.Equal(-1, sim.Execute(new BuildStationCommand(300, 200)));  // no road there
        Assert.Equal(-1, sim.Execute(new BuildStationCommand(20, 0)));     // too close to the end of the road to leave a stop lane
        Assert.Equal(0, sim.Execute(new BuildStationCommand(300, 0)));
        Assert.Equal(-1, sim.Execute(new BuildStationCommand(302, 0)));    // snaps to the node of the first
    }

    [Fact]
    public void Line_NeedsTwoDifferentOpenStations()
    {
        var (sim, _, a, b) = StraightLine();

        Assert.Equal(-1, sim.Execute(new CreateLineCommand(new[] { a })));
        Assert.Equal(-1, sim.Execute(new CreateLineCommand(new[] { a, a })));
        Assert.Equal(-1, sim.Execute(new CreateLineCommand(new[] { a, 9 })));
        Assert.Equal(0, sim.Execute(new CreateLineCommand(new[] { a, b })));
    }

    [Fact]
    public void BuyingAVehicle_PaysThePriceAndPutsItOnTheRoadAtTheDepot()
    {
        var (sim, depot, a, b) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        long before = sim.Company.Money;

        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));

        Assert.True(bus >= 0);
        Assert.True(sim.IsCompanyVehicle(bus));
        Assert.Equal(before - 24000, sim.Company.Money);
        Assert.Equal(VehicleKind.Car, sim.GetVehicle(bus).Kind);
        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(depot, "tank", line)));
        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(7, "bus", line)));
        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(depot, "bus", 3)));
    }

    [Fact]
    public void BuyingIsRefused_WhenTheCompanyCannotPay()
    {
        var poor = Tuning.Load();
        poor = poor with { Economy = poor.Economy with { StartingMoney = 80000 } };
        var (sim, depot, a, b) = StraightLine(poor);
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        long money = sim.Company.Money;

        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(depot, "bus", line)));
        Assert.Equal(money, sim.Company.Money);
    }

    [Fact]
    public void ABusRunsBetweenTheStations_LoadsAtEach_AndGetsPaid()
    {
        var (sim, depot, a, b) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));

        Run(sim, 12);

        Assert.True(sim.Lines[line].Delivered > 0, "nothing was delivered");
        Assert.True(sim.Lines[line].Income > 0);
        Assert.True(sim.IncomeOf(bus) > 0);
        Assert.Equal(sim.Lines[line].Income, sim.Company.Total(LedgerEntry.Income));
        Assert.Equal(VehicleKind.Car, sim.GetVehicle(bus).Kind);
    }

    [Fact]
    public void ABusStopsAtTheStation_ForTheTimeItTakesToLoad()
    {
        var (sim, depot, a, b) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));

        long loadingTicks = 0;
        bool sawStopAtA = false;
        for (int t = 0; t < 40000 && !sawStopAtA; t++)
        {
            sim.Step();
            var info = sim.GetFleetInfo(bus);
            if (!info.Loading) continue;
            sawStopAtA = true;
            Assert.Equal(0f, sim.GetVehicle(bus).Speed);
            var lane = sim.Network.GetLane(sim.GetVehicle(bus).Lane);
            Assert.Equal(sim.Stations[a].Node, lane.EndNode);
            while (sim.GetFleetInfo(bus).Loading) { sim.Step(); loadingTicks++; }
        }

        Assert.True(sawStopAtA);
        Assert.True(loadingTicks * Simulation.StepSeconds >= 6f, "dwell is at least the base time");
    }

    [Fact]
    public void ATruckCarriesGoods_NotPassengers()
    {
        var (sim, depot, a, b) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        sim.Execute(new BuyVehicleCommand(depot, "truck", line));

        Run(sim, 12);

        Assert.True(sim.Lines[line].Delivered > 0);
        int passengers = sim.CargoTypes.ToList().FindIndex(c => c.Id == "passengers");
        Assert.True(sim.Stations[a].Waiting[passengers] > 20f, "nobody took the passengers");
    }

    [Fact]
    public void ABusLine_AndATruckLine_PayForThemselves()
    {
        // The M2 exit criterion: a profitable bus line and truck line.
        var (sim, depot, a, b) = StraightLine();
        int busLine = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int truckLine = sim.Execute(new CreateLineCommand(new[] { b, a }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", busLine));
        int truck = sim.Execute(new BuyVehicleCommand(depot, "truck", truckLine));
        Assert.True(bus >= 0 && truck >= 0);
        Assert.False(sim.IsGhost(bus));
        Assert.True(sim.IsGhost(truck)); // the bus still stands in the doorway: the truck comes out behind it as a ghost, not refused

        Run(sim, 40);

        Assert.True(sim.Lines[busLine].Profit > 0, $"bus line profit {sim.Lines[busLine].Profit}");
        Assert.True(sim.Lines[truckLine].Profit > 0, $"truck line profit {sim.Lines[truckLine].Profit}");
        Assert.True(sim.Lines[busLine].Upkeep > 0);
    }

    [Fact]
    public void Upkeep_IsChargedEveryDay()
    {
        var (sim, depot, a, b) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        long upkeepBefore = sim.Company.Total(LedgerEntry.Upkeep);

        Run(sim, 3);

        long perDay = 400 + 2 * 150; // the bus and the two stations
        Assert.Equal(upkeepBefore - 3 * perDay, sim.Company.Total(LedgerEntry.Upkeep));
        Assert.Equal(3, sim.Day);
    }

    [Fact]
    public void AVehicleIsNotBoughtAtADepotThatHasNoRoadToTheLine_AndTheNearerConnectedDepotComesFirst()
    {
        var (sim, depot, a, b) = StraightLine();
        // A second depot on the same road, just short of the line's first station, and one on a road of its own.
        Assert.True(sim.Execute(new BuildStationCommand(100, 0)) >= 0);
        int near = sim.Execute(new AddDepotCommand(sim.Network.FindNodeNear(100, 0, 1f), DepotLevel.Simple));
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 600, 300, 600, RoadType.TwoLane, 14f)));
        int island = sim.Execute(new AddDepotCommand(sim.Network.FindNodeNear(0, 600, 1f), DepotLevel.Simple));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.True(near >= 0 && island >= 0);

        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(island, "bus", line)));
        Assert.Equal(new[] { near, depot }, sim.DepotsByDistance(line));
        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(island, "bus", line)));
    }

    [Fact]
    public void ARouteGoesRoundTheNetwork_ToTheStationItNeeds()
    {
        // A T junction: the depot is on the stem, the stations on the two arms; the bus must choose the right turn each time.
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 2);
        sim.Execute(new BuildRoadCommand(-600, 0, 600, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(0, 0, 0, 500, RoadType.TwoLane, 14f));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 500, 1f), DepotLevel.Simple));
        int west = sim.Execute(new BuildStationCommand(-400, 0));
        int east = sim.Execute(new BuildStationCommand(400, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { west, east }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Assert.True(bus >= 0);

        Run(sim, 15);

        Assert.True(sim.Lines[line].Delivered > 0);
        Assert.Equal(VehicleKind.Car, sim.GetVehicle(bus).Kind);
    }

    [Fact]
    public void SellingAVehicle_RefundsAShare_AndDeletingALineSellsItsFleet()
    {
        var (sim, depot, a, b) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        long before = sim.Company.Money;

        Assert.Equal(bus, sim.Execute(new SellVehicleCommand(bus)));
        Assert.Equal(before + 14400, sim.Company.Money);
        Assert.False(sim.IsCompanyVehicle(bus));
        Assert.Equal(-1, sim.Execute(new SellVehicleCommand(bus)));

        int second = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Assert.Equal(1, sim.Execute(new DeleteLineCommand(line)));
        Assert.False(sim.IsCompanyVehicle(second));
        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(depot, "bus", line)));
    }

    [Fact]
    public void ClosedStations_AreSkippedByTheLine()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 3);
        sim.Execute(new BuildRoadCommand(0, 0, 1500, 0, RoadType.TwoLane, 14f));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(200, 0));
        int b = sim.Execute(new BuildStationCommand(700, 0));
        int c = sim.Execute(new BuildStationCommand(1200, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b, c }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Run(sim, 4);

        // Tear down both roads at the middle station: it loses every road and closes, and the road is cut in two.
        foreach (var road in net.RoadsAt(sim.Stations[b].Node).ToArray()) sim.Execute(new RemoveRoadCommand(road.Value));
        Run(sim, 1);

        Assert.False(sim.Stations[b].Active);
        Assert.True(sim.IsCompanyVehicle(bus) || sim.Lines[line].Active);
    }

    [Fact]
    public void SameSeedAndCommands_GiveTheSameRun_AndTheLogReplaysIt()
    {
        ulong Hash(out IReadOnlyList<LoggedCommand> log)
        {
            var (sim, depot, a, b) = StraightLine();
            int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
            sim.Execute(new SeedTrafficCommand(20));
            sim.Execute(new BuyVehicleCommand(depot, "bus", line));
            Run(sim, 2);
            sim.Execute(new BuyVehicleCommand(depot, "truck", line));
            Run(sim, 6);
            log = sim.CommandLog.ToList();
            return sim.StateHash();
        }

        ulong first = Hash(out var log);
        ulong second = Hash(out _);

        var net = new RoadNetwork(Terrain.Flat());
        var replay = new Simulation(net, 5);
        replay.Replay(log, (long)Math.Round(8 * replay.DayLengthSeconds / Simulation.StepSeconds));

        Assert.Equal(first, second);
        Assert.Equal(first, replay.StateHash());
    }

    [Fact]
    public void AFleetOnAGridWithAmbientTraffic_KeepsRunning_AndEveryLineEarns()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 11);
        const int size = 5;
        const float spacing = 300f;
        for (int x = 0; x < size; x++)
            for (int z = 0; z < size; z++)
            {
                float px = x * spacing, pz = z * spacing;
                if (x + 1 < size) Assert.Equal(1, sim.Execute(new BuildRoadCommand(px, pz, px + spacing, pz, RoadType.TwoLane, 14f)));
                if (z + 1 < size) Assert.Equal(1, sim.Execute(new BuildRoadCommand(px, pz, px, pz + spacing, RoadType.TwoLane, 14f)));
            }
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(600, 600, 1f), DepotLevel.Simple));
        sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        Assert.True(sim.Execute(new SeedTrafficCommand(150)) > 100);

        int s0 = sim.Execute(new BuildStationCommand(150, 0));     // on an edge of the grid
        int s1 = sim.Execute(new BuildStationCommand(1050, 0));
        int s2 = sim.Execute(new BuildStationCommand(1200, 750));
        int s3 = sim.Execute(new BuildStationCommand(300, 900));
        int s4 = sim.Execute(new BuildStationCommand(750, 1200));
        Assert.All(new[] { s0, s1, s2, s3, s4 }, s => Assert.True(s >= 0));
        int busA = sim.Execute(new CreateLineCommand(new[] { s0, s2, s4 }));
        int busB = sim.Execute(new CreateLineCommand(new[] { s1, s3 }));
        int truckA = sim.Execute(new CreateLineCommand(new[] { s3, s0 }));
        Assert.All(new[] { busA, busB, truckA }, l => Assert.True(l >= 0));

        // The depot is at a busy junction: a vehicle is only put on the road when nothing is about to arrive there.
        int bought = 0;
        foreach (var (model, line) in new[] { ("bus", busA), ("bus", busB), ("truck", truckA), ("bus", busA), ("bus", busB), ("truck", truckA), ("bus", busA), ("bus", busB) })
            for (int attempt = 0; attempt < 60; attempt++)
            {
                if (sim.Execute(new BuyVehicleCommand(depot, model, line)) >= 0) { bought++; break; }
                Run(sim, 1 / 30.0);
            }
        Assert.Equal(8, bought);

        Run(sim, 25);

        foreach (var line in new[] { busA, busB, truckA })
            Assert.True(sim.Lines[line].Delivered > 0, $"line {line} delivered nothing");
        int running = 0, wrecked = 0;
        for (int v = 0; v < sim.VehicleCount; v++)
        {
            if (!sim.IsCompanyVehicle(v)) continue;
            if (sim.GetVehicle(v).Kind == VehicleKind.Car) running++; else wrecked++;
        }
        Assert.True(running == bought, $"{running} running, {wrecked} wrecked of {bought}");
        Assert.Empty(sim.Incidents);
    }

    [Fact]
    public void RoomToSpawn_CountsTheRealLengthOfTheVehicleAlreadyOnTheLane()
    {
        // A 10 m bus reaches back 10 m from its front: a car put on the road 11 m behind that front used to count as clear
        // (only the distance between fronts was compared) and was then rear-ended by the bus pulling away over it.
        var (sim, depot, a, b) = StraightLine();
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        Assert.True(bus >= 0);
        for (int t = 0; t < 40; t++) sim.Step();
        var state = sim.GetVehicle(bus);
        float front = state.Position;
        Assert.Equal(10f, sim.LengthOf(bus));

        Assert.False(sim.IsClearFor(state.Lane, front - 11f, Simulation.VehicleLength, 3f)); // inside the bus's body
        Assert.False(sim.IsClearFor(state.Lane, front - 12.4f, Simulation.VehicleLength, 3f)); // closer than the margin to its rear
        Assert.True(sim.IsClearFor(state.Lane, front - 13.5f, Simulation.VehicleLength, 3f));
        Assert.False(sim.IsClearFor(state.Lane, front + 6f, Simulation.VehicleLength, 3f)); // too close in front of it
        Assert.True(sim.IsClearFor(state.Lane, front + 8f, Simulation.VehicleLength, 3f));
    }

    [Fact]
    public void ALongVehicleThatMergedInFromAnotherStreet_IsNotRearEnded_ByACarWaitingAtTheEdge()
    {
        // A car waits at the junction edge on the south arm to turn into the west arm. A 10 m bus that came from the east has
        // just entered that exit lane. Measured along the car's own (short) turn, the bus's body would reach back to the car,
        // but the bus is on another street: it was counted as a rear-end collision and wrecked.
        for (ulong seed = 1; seed < 60; seed++)
        {
            var net = new RoadNetwork(Terrain.Flat());
            var sim = new Simulation(net, seed);
            Assert.Equal(1, sim.Execute(new BuildRoadCommand(-600, 0, 600, 0, RoadType.TwoLane, 14f)));
            Assert.True(sim.Execute(new BuildRoadCommand(0, -600, 0, 600, RoadType.TwoLane, 14f)) > 0);
            int centre = net.FindNodeNear(0, 0, 1f);
            var south = net.IncomingLanes(centre).First(l => net.GetNode(net.GetLane(l).StartNode).Z < -1f);
            float edge = net.GetLane(south).Length - RoadNetwork.JunctionReach(net.GetLane(south).Length);
            int car = sim.SpawnVehicle(south, edge, 0f);
            sim.SetHalted(car, true);
            var exit = sim.NextLaneOf(car);
            if (net.GetNode(net.GetLane(exit).EndNode).X > -1f) continue; // this seed sends it somewhere else

            int bus = sim.SpawnVehicle(exit, RoadNetwork.JunctionReach(net.GetLane(exit).Length) + 0.1f, 0f);
            sim.SetLength(bus, 10f);
            sim.SetHalted(bus, true);
            Assert.True(net.JunctionLength(south, exit) + 0.1f < 10f, "the car's turn must be shorter than the bus for this to be the case in question");

            sim.Step();

            Assert.Empty(sim.Incidents);
            return;
        }
        Assert.Fail("no seed sent the car into the west arm");
    }
}
