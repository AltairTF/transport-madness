using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class SignalTests
{
    /// <summary>
    /// A single track from x = 0 to 2400 with a passing loop in the middle (a second track from x = 900 to 1500, 60 m to the side), a
    /// station at each end and a depot at each end beyond the stations.
    /// </summary>
    static (Simulation Sim, int A, int B, int Line, int DepotA, int DepotB) WithLoop(bool signals, SignalKind kind = SignalKind.Block, ulong seed = 3)
    {
        var sim = RailWorlds.Flat(seed);
        sim.Execute(new TakeLoanCommand(1000000));
        Assert.True(sim.Execute(new BuildTrackCommand(0, 0, 900, 0)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(900, 0, 1500, 0)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(1500, 0, 2400, 0)) > 0);
        // the loop
        Assert.True(sim.Execute(new BuildTrackCommand(900, 0, 960, 60)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(960, 60, 1440, 60)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(1440, 60, 1500, 0)) > 0);
        int a = sim.Execute(new BuildRailStationCommand(150, 0));
        int b = sim.Execute(new BuildRailStationCommand(2250, 0));
        int depotA = sim.Execute(new AddRailDepotCommand(0, 0));
        int depotB = sim.Execute(new AddRailDepotCommand(2400, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        if (signals)
        {
            // A signal facing each of the tracks that arrive at the points at either end of the loop.
            foreach (var (x, z) in new[] { (890f, 0f), (910f, 0f), (907f, 7f), (1510f, 0f), (1490f, 0f), (1493f, 7f) })
                Assert.True(sim.Execute(new BuildSignalCommand(x, z, kind)) >= 0);
        }
        return (sim, a, b, line, depotA, depotB);
    }

    static bool Overlap(Simulation sim, Train a, Train b)
    {
        var ca = sim.TrainCars(a);
        var cb = sim.TrainCars(b);
        foreach (var p in ca)
            foreach (var q in cb)
            {
                if (!p.Visible || !q.Visible) continue;
                float d = MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z));
                if (d < (p.Length + q.Length) * 0.5f - 1f) return true;
            }
        return false;
    }

    [Fact]
    public void ASignalSplitsTheTrack_AndIsAThingOnTheNode()
    {
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildTrackCommand(0, 0, 800, 0));
        long before = sim.Company.Money;

        int id = sim.Execute(new BuildSignalCommand(400, 0, SignalKind.Block, Forward: true));

        Assert.True(id >= 0);
        var signal = sim.Rail.GetSignal(new SignalId(id));
        Assert.Equal(2, sim.Rail.TracksAt(signal.Node).Count);
        Assert.Equal(before - sim.Rail.Rail.SignalPrice, sim.Company.Money);
        Assert.Equal(sim.Rail.GetTrack(signal.Track).To, signal.Node); // faces trains travelling from the start toward the end
        int back = sim.Execute(new BuildSignalCommand(410, 0, SignalKind.Path, Forward: false));
        Assert.NotEqual(signal.Track, sim.Rail.GetSignal(new SignalId(back)).Track);
        Assert.Equal(1, sim.Execute(new RemoveSignalCommand(id)));
        Assert.Equal(0, sim.Execute(new RemoveSignalCommand(id)));
    }

    [Fact]
    public void TwoTrainsOnOneTrack_NeverOverlap_AndAHeadOnMeetingIsReportedAsADeadlock()
    {
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildTrackCommand(0, 0, 300, 0));
        sim.Execute(new BuildTrackCommand(300, 0, 1500, 0));
        int a = sim.Execute(new BuildRailStationCommand(100, 0)), b = sim.Execute(new BuildRailStationCommand(1400, 0));
        int depot = sim.Execute(new AddRailDepotCommand(300, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int t0 = sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", 3, line));
        int t1 = sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", 3, line));
        Assert.True(t1 >= 0 && sim.Trains[t1].InDepot, "the second train waits inside the depot until the first has driven out");
        RailWorlds.Run(sim, 40);
        Assert.False(sim.Trains[t1].InDepot);

        for (int i = 0; i < 24000; i++)
        {
            sim.Step();
            Assert.False(Overlap(sim, sim.Trains[t0], sim.Trains[t1]), $"trains overlap at tick {i}");
        }

        // Two trains turning round at the ends of one single track meet head on sooner or later: nothing can be done by the trains themselves.
        bool moving = sim.Trains[t0].Income > 0 && sim.Trains[t1].Income > 0;
        Assert.True(moving || sim.RailWarnings.Count > 0, "either both trains ran or the deadlock was reported");
    }

    /// <summary>An eight-sided ring 400 m across with a station on three corners, a block signal at every corner and a depot at one.</summary>
    static (Simulation Sim, int Line, int Depot) Ring(SignalKind kind)
    {
        var sim = RailWorlds.Flat(5);
        sim.Execute(new TakeLoanCommand(1000000));
        var v = Enumerable.Range(0, 8).Select(k => (X: 400f * MathF.Cos(k * MathF.PI / 4f), Z: 400f * MathF.Sin(k * MathF.PI / 4f))).ToArray();
        for (int k = 0; k < 8; k++)
            Assert.True(sim.Execute(new BuildTrackCommand(v[k].X, v[k].Z, v[(k + 1) % 8].X, v[(k + 1) % 8].Z)) > 0, $"side {k}");
        for (int k = 0; k < 8; k++) Assert.True(sim.Execute(new BuildSignalCommand(v[k].X, v[k].Z, kind)) >= 0);
        var stations = new[] { 0, 3, 5 }.Select(k => sim.Execute(new BuildRailStationCommand(v[k].X, v[k].Z))).ToArray();
        Assert.All(stations, id => Assert.True(id >= 0));
        int depot = sim.Execute(new AddRailDepotCommand(v[7].X, v[7].Z)); // so that the first leg runs the way round that the others do
        int line = sim.Execute(new CreateLineCommand(stations));
        return (sim, line, depot);
    }

    [Theory]
    [InlineData(SignalKind.Block)]
    [InlineData(SignalKind.Path)]
    public void ThreeTrainsOnARing_FollowEachOtherWithoutOverlapping_AndKeepRunning(SignalKind kind)
    {
        var (sim, line, depot) = Ring(kind);
        var ids = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            int id = -1;
            for (int tries = 0; tries < 80 && id < 0; tries++)
            {
                id = sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", 3, line));
                if (id < 0) RailWorlds.Run(sim, 2);
            }
            Assert.True(id >= 0, $"train {i} bought");
            ids.Add(id);
        }
        var stops = new int[3];
        var prev = new TrainStatus[3];

        for (int i = 0; i < 48000; i++)
        {
            sim.Step();
            for (int a = 0; a < 3; a++)
            {
                var t = sim.Trains[ids[a]];
                if (t.Status == TrainStatus.AtStation && prev[a] != TrainStatus.AtStation) stops[a]++;
                prev[a] = t.Status;
                for (int b = a + 1; b < 3; b++) Assert.False(Overlap(sim, t, sim.Trains[ids[b]]), $"trains {a} and {b} overlap at tick {i}");
            }
        }

        Assert.Empty(sim.RailWarnings);
        Assert.All(stops, n => Assert.True(n >= 5, $"every train kept stopping: {string.Join(",", stops)}"));
    }

    [Fact]
    public void TwoTrainsMeetingOnASingleTrackWithAPassingLoop_AndPathSignals_PassWithoutDeadlock()
    {
        var w = WithLoop(signals: true, SignalKind.Path);
        int t0 = w.Sim.Execute(new BuyTrainCommand(w.DepotA, "diesel", "coach", 3, w.Line));
        int t1 = w.Sim.Execute(new BuyTrainCommand(w.DepotB, "diesel", "coach", 3, w.Line));
        Assert.True(t0 >= 0 && t1 >= 0);
        long arrivals = 0;
        var prev = new[] { TrainStatus.Running, TrainStatus.Running };

        for (int i = 0; i < 60000; i++)
        {
            w.Sim.Step();
            Assert.False(Overlap(w.Sim, w.Sim.Trains[t0], w.Sim.Trains[t1]), $"trains overlap at tick {i}");
            for (int k = 0; k < 2; k++)
            {
                var s = w.Sim.Trains[k].Status;
                if (s == TrainStatus.AtStation && prev[k] != TrainStatus.AtStation) arrivals++;
                prev[k] = s;
            }
        }

        Assert.Empty(w.Sim.RailWarnings);
        Assert.True(arrivals >= 8, $"the trains kept running: {arrivals} stops");
    }

    [Fact]
    public void TwoTrainsMeetingOnSingleTrackWithNoLoop_AreFoundDeadlocked_AndTheWarningIsRecordedOnce()
    {
        // One track, a train from each end, signals everywhere so that each takes a block toward the other: nobody can get through.
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildTrackCommand(0, 0, 1200, 0));
        foreach (float x in new[] { 400f, 800f })
            foreach (bool fwd in new[] { true, false })
                sim.Execute(new BuildSignalCommand(x, 0, SignalKind.Block, fwd));
        int a = sim.Execute(new BuildRailStationCommand(100, 0)), b = sim.Execute(new BuildRailStationCommand(1100, 0));
        int d0 = sim.Execute(new AddRailDepotCommand(0, 0)), d1 = sim.Execute(new AddRailDepotCommand(1200, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { b, a }));   // trains must pass the whole track
        int line2 = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int t0 = sim.Execute(new BuyTrainCommand(d0, "diesel", "coach", 3, line));
        int t1 = sim.Execute(new BuyTrainCommand(d1, "diesel", "coach", 3, line2));
        Assert.True(t0 >= 0 && t1 >= 0);

        RailWorlds.Run(sim, 400);

        var warning = Assert.Single(sim.RailWarnings);
        Assert.Equal("deadlock", warning.Kind);
        Assert.Equal(new[] { t0, t1 }, warning.Trains.OrderBy(x => x));
        Assert.Equal(new[] { t0, t1 }, sim.DeadlockedTrains.OrderBy(x => x));
        RailWorlds.Run(sim, 400);
        Assert.Single(sim.RailWarnings); // the same ring is not reported again
        Assert.False(Overlap(sim, sim.Trains[t0], sim.Trains[t1]), "stuck, but not through each other");
    }

    [Fact]
    public void SellingOneOfTheDeadlockedTrains_ClearsTheDeadlock()
    {
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildTrackCommand(0, 0, 1200, 0));
        foreach (float x in new[] { 400f, 800f })
            foreach (bool fwd in new[] { true, false })
                sim.Execute(new BuildSignalCommand(x, 0, SignalKind.Block, fwd));
        int a = sim.Execute(new BuildRailStationCommand(100, 0)), b = sim.Execute(new BuildRailStationCommand(1100, 0));
        int d0 = sim.Execute(new AddRailDepotCommand(0, 0)), d1 = sim.Execute(new AddRailDepotCommand(1200, 0));
        int t0 = sim.Execute(new BuyTrainCommand(d0, "diesel", "coach", 3, sim.Execute(new CreateLineCommand(new[] { b, a }))));
        int t1 = sim.Execute(new BuyTrainCommand(d1, "diesel", "coach", 3, sim.Execute(new CreateLineCommand(new[] { a, b }))));
        RailWorlds.Run(sim, 400);
        Assert.Equal(2, sim.DeadlockedTrains.Count);

        sim.Execute(new SellTrainCommand(t0));
        RailWorlds.Run(sim, 120);

        Assert.Empty(sim.DeadlockedTrains);
        Assert.True(sim.Trains[t1].Speed > 0f || sim.Trains[t1].Status == TrainStatus.AtStation);
    }

    [Fact]
    public void ASignalShowsRed_WhileATrainHoldsTheBlockBeyondIt()
    {
        var w = WithLoop(signals: true);
        // The signal at the west end of the loop that faces trains coming from the single track.
        var signal = w.Sim.Rail.Signals.First(s => MathF.Abs(w.Sim.Rail.GetNode(s.Node).X - 900f) < 1f && w.Sim.Rail.GetNode(w.Sim.Rail.GetTrack(s.Track).From).X < 800f);
        Assert.True(w.Sim.IsSignalClear(signal));
        int t0 = w.Sim.Execute(new BuyTrainCommand(w.DepotA, "diesel", "coach", 3, w.Line));

        bool sawRed = false;
        for (int i = 0; i < 12000 && !sawRed; i++)
        {
            w.Sim.Step();
            sawRed = !w.Sim.IsSignalClear(signal);
        }

        Assert.True(sawRed, "the signal is red while the train is in the block beyond it");
        Assert.True(t0 >= 0);
    }
}
