using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Vehicles that come out of a depot freely (ghosts until clear), and the garage: buying with no line, putting on a line, sending back.</summary>
public class GarageTests
{
    /// <summary>A straight road 1200 m long with a depot at its west end, stations at x = 150 and x = 1050, and a line between them.</summary>
    static (Simulation Sim, int Depot, int A, int B, int Line) Road(Tuning? tuning = null)
    {
        var net = new RoadNetwork(Terrain.Flat(), Worlds.Late(tuning));
        var sim = new Simulation(net, 5);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f)));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(150, 0));
        int b = sim.Execute(new BuildStationCommand(1050, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.True(depot >= 0 && a >= 0 && b >= 0 && line >= 0);
        return (sim, depot, a, b, line);
    }

    static void RunSeconds(Simulation sim, double seconds)
    {
        long ticks = (long)Math.Round(seconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static IEnumerable<int> OnTheRoad(Simulation sim) => Enumerable.Range(0, sim.VehicleCount).Where(sim.IsCompanyVehicle);

    [Fact]
    public void ABusBoughtForNoLine_WaitsInTheDepot_AndCostsTheSameAsOneForALine()
    {
        var (sim, depot, _, _, _) = Road();
        long before = sim.Company.Money;

        int parked = sim.Execute(new BuyParkedVehicleCommand(depot, "bus"));

        Assert.Equal(0, parked);
        Assert.Equal(before - 24000, sim.Company.Money);
        Assert.Empty(OnTheRoad(sim));
        Assert.True(sim.Garage[parked].Active);
        Assert.False(sim.Garage[parked].Rail);
        Assert.Equal(-1, sim.Execute(new BuyParkedVehicleCommand(depot, "tank")));
        Assert.Equal(-1, sim.Execute(new BuyParkedVehicleCommand(9, "bus")));
        Assert.Equal(1, sim.Execute(new BuyParkedVehicleCommand(BuyParkedVehicleCommand.AnyDepot, "truck")));
    }

    [Fact]
    public void BuyingParked_IsRefused_WhenTheCompanyCannotPay()
    {
        var poor = Tuning.Load();
        poor = poor with { Economy = poor.Economy with { StartingMoney = 80000 } };
        var (sim, depot, _, _, _) = Road(poor);
        long money = sim.Company.Money;

        Assert.Equal(-1, sim.Execute(new BuyParkedVehicleCommand(depot, "bus")));
        Assert.Equal(money, sim.Company.Money);
    }

    [Fact]
    public void AParkedBus_PutOnALine_DrivesOutAndEarns_AndLeavesTheGarage()
    {
        var (sim, depot, _, _, line) = Road();
        int parked = sim.Execute(new BuyParkedVehicleCommand(depot, "bus"));
        long paid = sim.Company.Money;

        int bus = sim.Execute(new AssignParkedCommand(parked, line));

        Assert.True(bus >= 0 && sim.IsCompanyVehicle(bus));
        Assert.False(sim.Garage[parked].Active);
        Assert.Equal(paid, sim.Company.Money); // already paid for
        RunSeconds(sim, 6 * 60);
        Assert.True(sim.IncomeOf(bus) > 0, "it never delivered anything");
        Assert.Equal(-1, sim.Execute(new AssignParkedCommand(parked, line))); // it is on the road now
    }

    [Fact]
    public void AssigningIsRefusedWithAReason_ForTheWrongKindOfLineOrNoWayFromTheDepot()
    {
        var (sim, depot, a, b, line) = Road();
        // A second road with its stations, not joined to the depot's.
        sim.Execute(new BuildRoadCommand(0, 600, 800, 600, RoadType.TwoLane, 14f));
        int c = sim.Execute(new BuildStationCommand(100, 600)), d = sim.Execute(new BuildStationCommand(700, 600));
        int far = sim.Execute(new CreateLineCommand(new[] { c, d }));
        int parked = sim.Execute(new BuyParkedVehicleCommand(depot, "bus"));

        Assert.Null(sim.WhyNotAssign(parked, line));
        Assert.Contains("road", sim.WhyNotAssign(parked, far));
        Assert.Equal(-1, sim.Execute(new AssignParkedCommand(parked, far)));
        Assert.True(sim.Garage[parked].Active);
        Assert.NotNull(sim.WhyNotAssign(parked, 7));
        Assert.NotNull(sim.WhyNotAssign(7, line));
    }

    [Fact]
    public void BusesBoughtTogether_ComeOutOneAfterAnother_AreNeverRefused_AndNeverOverlapOnceReal()
    {
        var (sim, depot, _, _, line) = Road();
        var buses = new List<int>();
        for (int k = 0; k < 6; k++) buses.Add(sim.Execute(new BuyVehicleCommand(depot, "bus", line)));

        Assert.All(buses, id => Assert.True(id >= 0, "refused"));
        Assert.False(sim.IsGhost(buses[0]));
        Assert.All(buses.Skip(1), id => Assert.True(sim.IsGhost(id)));

        bool allReal = false;
        for (int i = 0; i < 40 * 90; i++)
        {
            sim.Step();
            allReal = buses.All(id => !sim.IsGhost(id));
            // Ordinary vehicles never overlap each other, ghosts are exempt.
            for (int x = 0; x < buses.Count; x++)
                for (int y = x + 1; y < buses.Count; y++)
                {
                    int p = buses[x], q = buses[y];
                    if (sim.IsGhost(p) || sim.IsGhost(q)) continue;
                    var sp = sim.GetVehicle(p); var sq = sim.GetVehicle(q);
                    if (sp.Lane != sq.Lane) continue;
                    float gap = MathF.Abs(sp.Position - sq.Position) - (sp.Position > sq.Position ? sim.LengthOf(p) : sim.LengthOf(q));
                    Assert.True(gap > -0.01f, $"buses {p} and {q} overlap by {-gap} m at tick {i}");
                }
        }
        Assert.True(allReal, "some bus never became an ordinary vehicle");
    }

    [Fact]
    public void AGhost_WaitsInsideTheDepot_UntilTheDoorwayHasRoom_ThenComesOutAndBecomesReal()
    {
        var (sim, depot, _, _, line) = Road();
        int first = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        int second = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        var start = sim.GetVehicle(second).Position;

        Assert.True(sim.IsGhost(second) && sim.IsInDepot(second)); // the first bus stands in the doorway
        sim.Step();
        Assert.True(sim.IsInDepot(second));
        Assert.Equal(start, sim.GetVehicle(second).Position, 3);

        bool cameOut = false;
        for (int i = 0; i < 40 * 60 && sim.IsGhost(second); i++)
        {
            sim.Step();
            cameOut |= sim.IsGhost(second) && !sim.IsInDepot(second);
        }
        Assert.True(cameOut, "it never came out as a ghost");
        Assert.False(sim.IsGhost(second));
        Assert.True(sim.GetVehicle(first).Position > 0f);
    }

    [Fact]
    public void ANewVehicle_IsNeverPutInsideAnother_WhateverTheTrafficAtTheDepot()
    {
        var (sim, depot, _, _, line) = Road();
        var fleet = new List<int>();
        for (int k = 0; k < 4; k++) fleet.Add(sim.Execute(new BuyVehicleCommand(depot, k % 2 == 0 ? "bus" : "truck", line)));
        for (int i = 0; i < 40 * 150; i++)
        {
            if (i == 40 * 20 || i == 40 * 21) fleet.Add(sim.Execute(new BuyVehicleCommand(depot, "bus", line)));
            sim.Step();
            // Neither a vehicle that is out nor a ghost may sit inside another one; vehicles still inside the depot are not on the road.
            var out_ = fleet.Where(id => id >= 0 && !sim.IsInDepot(id)).ToList();
            for (int x = 0; x < out_.Count; x++)
                for (int y = x + 1; y < out_.Count; y++)
                {
                    var p = sim.GetVehicle(out_[x]); var q = sim.GetVehicle(out_[y]);
                    if (p.Lane != q.Lane) continue;
                    int ahead = p.Position > q.Position ? out_[x] : out_[y];
                    float gap = MathF.Abs(p.Position - q.Position) - sim.LengthOf(ahead);
                    Assert.True(gap > -0.01f, $"vehicles {out_[x]} and {out_[y]} overlap by {-gap:0.0} m at tick {i}");
                }
        }
        Assert.All(fleet, id => Assert.False(sim.IsGhost(id)));
    }

    [Fact]
    public void ABusSentBack_StopsServingTheLine_DrivesToTheDepot_AndWaitsThere()
    {
        var (sim, depot, _, _, line) = Road();
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        RunSeconds(sim, 120);

        Assert.Equal(1, sim.Execute(new ParkVehicleCommand(bus)));
        Assert.Equal(0, sim.Execute(new ParkVehicleCommand(bus)));   // already on its way

        for (int i = 0; i < 40 * 600 && sim.IsCompanyVehicle(bus); i++) sim.Step();

        Assert.False(sim.IsCompanyVehicle(bus), "it never reached the depot");
        var entry = Assert.Single(sim.Garage, g => g.Active);
        Assert.Equal(depot, entry.Depot);
        Assert.False(entry.Rail);

        // And it can go back to work.
        int again = sim.Execute(new AssignParkedCommand(entry.Id, line));
        Assert.True(again >= 0);
        long income = sim.IncomeOf(again);
        RunSeconds(sim, 6 * 60);
        Assert.True(sim.IncomeOf(again) > income);
    }

    [Fact]
    public void AParkedVehicle_CanBeSold_ForPartOfItsPrice()
    {
        var (sim, depot, _, _, _) = Road();
        int parked = sim.Execute(new BuyParkedVehicleCommand(depot, "bus"));
        long before = sim.Company.Money;

        Assert.Equal(parked, sim.Execute(new SellParkedCommand(parked)));

        Assert.Equal(before + (long)MathF.Round(24000 * sim.Network.Tuning.Economy.SellRefundShare), sim.Company.Money);
        Assert.False(sim.Garage[parked].Active);
        Assert.Equal(-1, sim.Execute(new SellParkedCommand(parked)));
    }

    [Fact]
    public void TheGarage_IsPartOfTheReplay()
    {
        ulong Run()
        {
            var (sim, depot, _, _, line) = Road();
            int p = sim.Execute(new BuyParkedVehicleCommand(depot, "bus"));
            sim.Execute(new BuyVehicleCommand(depot, "truck", line));
            sim.Execute(new BuyVehicleCommand(depot, "bus", line));
            RunSeconds(sim, 30);
            int bus = sim.Execute(new AssignParkedCommand(p, line));
            RunSeconds(sim, 200);
            sim.Execute(new ParkVehicleCommand(bus));
            RunSeconds(sim, 600);
            return sim.StateHash();
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void AParkedBus_PaysAShareOfItsUpkeep_OnNoLinesBooks()
    {
        long Upkeep(bool parked)
        {
            var (sim, depot, _, _, _) = Road();
            if (parked) sim.Execute(new BuyParkedVehicleCommand(depot, "bus"));
            long before = sim.Company.Total(LedgerEntry.Upkeep);
            RunSeconds(sim, sim.DayLengthSeconds + 1);
            return sim.Company.Total(LedgerEntry.Upkeep) - before;
        }
        var (probe, _, _, _, _) = Road();
        float share = probe.Network.Tuning.Economy.ParkedUpkeepShare;
        float daily = probe.VehicleModels[probe.ModelIndex("bus")].UpkeepPerDay;

        long extra = Upkeep(false) - Upkeep(true); // expenses are negative in the ledger

        Assert.Equal(0.1f, share);
        Assert.InRange(extra, (long)Math.Round(daily * share) - 1, (long)Math.Round(daily * share) + 1);
        Assert.True(extra < daily / 2, "a parked bus must cost far less than a running one");
    }

    [Fact]
    public void AParkedTrain_PaysAShareOfItsConsistsUpkeep_AndASoldOneStopsPaying()
    {
        long Upkeep(bool parked, bool sell = false)
        {
            var w = RailWorlds.StraightLine(addTrain: false);
            var sim = w.Sim;
            if (parked)
            {
                int id = sim.Execute(new BuyParkedTrainCommand(w.Depot, "diesel", "coach", 3));
                if (sell) sim.Execute(new SellParkedCommand(id));
            }
            long before = sim.Company.Total(LedgerEntry.Upkeep);
            RailWorlds.Run(sim, sim.DayLengthSeconds + 1);
            return sim.Company.Total(LedgerEntry.Upkeep) - before;
        }
        var probe = RailWorlds.StraightLine(addTrain: false).Sim;
        float daily = probe.Locomotives[0].UpkeepPerDay + 3 * probe.Wagons[0].UpkeepPerDay;
        long expected = (long)Math.Round(daily * 0.1f);

        long extra = Upkeep(false) - Upkeep(true); // expenses are negative in the ledger

        Assert.InRange(extra, expected - 1, expected + 1);
        Assert.Equal(Upkeep(false), Upkeep(true, sell: true));
    }

    [Fact]
    public void ABusThatCrashesOnItsWayToTheDepot_IsClearedLikeAnyWreck_AndNeverParked()
    {
        var (sim, depot, _, _, line) = Road();
        int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        RunSeconds(sim, 120);
        Assert.Equal(1, sim.Execute(new ParkVehicleCommand(bus)));
        sim.Crash(bus);

        for (int i = 0; i < 40 * 1800 && sim.IsCompanyVehicle(bus); i++) sim.Step();

        Assert.False(sim.IsCompanyVehicle(bus), "the wreck was never cleared");
        Assert.DoesNotContain(sim.Garage, g => g.Active);
        Assert.False(sim.IsReturning(bus));
    }

    // ---- Rail -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ATrainBoughtForNoLine_WaitsInTheDepot_AndRunsWhenPutOnALine()
    {
        var w = RailWorlds.StraightLine(addTrain: false);
        var sim = w.Sim;
        long before = sim.Company.Money;

        int parked = sim.Execute(new BuyParkedTrainCommand(w.Depot, "diesel", "coach", 3));

        Assert.True(parked >= 0 && sim.Garage[parked].Rail);
        Assert.Equal(before - sim.TrainPrice("diesel", "coach", 3), sim.Company.Money);
        Assert.Empty(sim.Trains);

        int train = sim.Execute(new AssignParkedCommand(parked, w.Line));
        Assert.True(train >= 0);
        RailWorlds.Run(sim, 600);
        Assert.True(sim.Trains[train].Income > 0, "it never delivered anything");
        Assert.Single(sim.Trains);
    }

    [Fact]
    public void TrainsBoughtTogether_WaitInTheDepot_AndLeaveOneAfterAnother()
    {
        var w = RailWorlds.StraightLine(addTrain: false);
        var sim = w.Sim;
        var trains = new List<int>();
        for (int k = 0; k < 2; k++) trains.Add(sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 3, w.Line)));

        Assert.All(trains, id => Assert.True(id >= 0, "refused"));
        Assert.False(sim.Trains[trains[0]].InDepot);
        Assert.True(sim.Trains[trains[1]].InDepot);

        for (int i = 0; i < 40 * 240; i++) sim.Step();

        Assert.All(trains, id => Assert.False(sim.Trains[id].InDepot));
        Assert.All(trains, id => Assert.True(sim.Trains[id].Income > 0 || sim.Trains[id].Status != TrainStatus.NoRoute));
    }

    [Fact]
    public void ATrainWithNoTrackFromItsDepot_CanStillBeBought_AndTheReasonIsGivenWhenPuttingItOnALine()
    {
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildTrackCommand(0, 0, 800, 0));
        sim.Execute(new BuildTrackCommand(0, 400, 800, 400));
        int a = sim.Execute(new BuildRailStationCommand(100, 0)), b = sim.Execute(new BuildRailStationCommand(700, 0));
        int depot = sim.Execute(new AddRailDepotCommand(0, 400)); // on the other track
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int parked = sim.Execute(new BuyParkedTrainCommand(depot, "diesel", "coach", 3));

        Assert.True(parked >= 0);
        var why = sim.WhyNotAssign(parked, line);
        Assert.NotNull(why);
        Assert.Contains("track", why);
        Assert.Equal(-1, sim.Execute(new AssignParkedCommand(parked, line)));
        Assert.True(sim.Garage[parked].Active);
    }

    [Fact]
    public void ATrainSentBack_DrivesToItsDepot_IsParked_AndCanGoBackToWork()
    {
        var w = RailWorlds.StraightLine();
        var sim = w.Sim;
        RailWorlds.Run(sim, 300);
        Assert.Equal(1, sim.Execute(new ParkTrainCommand(0)));
        Assert.Equal(0, sim.Execute(new ParkTrainCommand(0))); // on its way already

        RailWorlds.Run(sim, 900);

        Assert.False(sim.Trains[0].Active, "it never reached the depot");
        var entry = Assert.Single(sim.Garage, g => g.Active);
        Assert.True(entry.Rail);
        Assert.Equal(w.Depot, entry.Depot);
        Assert.Equal(-1, sim.TrackHeldBy(new TrackId(0)));

        int again = sim.Execute(new AssignParkedCommand(entry.Id, w.Line));
        Assert.True(again >= 0 && again != 0);
        RailWorlds.Run(sim, 600);
        Assert.True(sim.Trains[again].Income > 0);
    }

    [Fact]
    public void ATrainStillInTheDepot_CanBeParkedAtOnce()
    {
        var w = RailWorlds.StraightLine(addTrain: false);
        var sim = w.Sim;
        int t0 = sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 3, w.Line));
        int t1 = sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 3, w.Line));
        Assert.True(sim.Trains[t1].InDepot);

        Assert.Equal(1, sim.Execute(new ParkTrainCommand(t1)));

        Assert.False(sim.Trains[t1].Active);
        Assert.Single(sim.Garage, g => g.Active && g.Rail);
        Assert.True(sim.Trains[t0].Active);
    }

    [Fact]
    public void TwoTrainsSentBack_BothReachTheDepot_OneAfterTheOther_AndFreeItsNode()
    {
        var w = RailWorlds.StraightLine(addTrain: false);
        var sim = w.Sim;
        int t0 = sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 3, w.Line));
        RailWorlds.Run(sim, 120);
        int t1 = sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 3, w.Line));
        RailWorlds.Run(sim, 400);
        Assert.Equal(1, sim.Execute(new ParkTrainCommand(t0)));
        Assert.Equal(1, sim.Execute(new ParkTrainCommand(t1)));

        RailWorlds.Run(sim, 1800);

        Assert.False(sim.Trains[t0].Active);
        Assert.False(sim.Trains[t1].Active);
        Assert.Equal(2, sim.Garage.Count(g => g.Active && g.Rail));
        int node = sim.RailDepots[w.Depot].Node;
        for (int t = 0; t < sim.Rail.TrackCount; t++) Assert.Equal(-1, sim.TrackHeldBy(new TrackId(t)));
        Assert.Empty(sim.DeadlockedTrains);
    }
}
