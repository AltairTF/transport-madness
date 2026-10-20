using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class RailBehaviourTests
{
    /// <summary>The lowest speed limit among the tracks any part of the train is on, and among the turns it is in the middle of.</summary>
    static float LimitUnder(Simulation sim, Train train)
    {
        float limit = float.PositiveInfinity;
        float rear = train.Head - train.Length;
        for (int k = 0; k < train.Steps.Count; k++)
        {
            var s = train.Steps[k];
            if (s.End < rear || s.Start > train.Head) continue;
            limit = MathF.Min(limit, sim.Rail.GetTrack(s.Track).SpeedLimit);
            if (k + 1 < train.Steps.Count && s.End >= rear && s.End <= train.Head)
            {
                var n = train.Steps[k + 1];
                var t = sim.Rail.GetTrack(s.Track);
                int node = s.Forward ? t.To : t.From;
                limit = MathF.Min(limit, sim.Rail.TurnSpeed(sim.Rail.TurnAngle(s.Track, node, n.Track)));
            }
        }
        return limit;
    }

    [Fact]
    public void ATrain_KeepsToTheLimitOfEveryPieceItIsOn_OnCurvesAndThroughJunctions()
    {
        // Two stations joined by an S of gentle arcs and a bend at a junction.
        var sim = RailWorlds.Flat();
        Assert.True(sim.Execute(new BuildTrackCommand(0, 0, 400, 0)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(400, 0, 800, 120, 0.5f)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(800, 120, 1200, 240, -0.5f)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(1200, 240, 1600, 240)) > 0);
        int a = sim.Execute(new BuildRailStationCommand(60, 0)), b = sim.Execute(new BuildRailStationCommand(1540, 240));
        int depot = sim.Execute(new AddRailDepotCommand(0, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int id = sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", 4, line));
        Assert.True(id >= 0);
        var train = sim.Trains[id];
        float worst = float.NegativeInfinity;
        bool slowedForCurve = false;

        for (int i = 0; i < 18000; i++)
        {
            sim.Step();
            float over = train.Speed - LimitUnder(sim, train);
            worst = MathF.Max(worst, over);
            if (LimitUnder(sim, train) < sim.Rail.Rail.SpeedLimit - 1f && train.Speed > 3f) slowedForCurve = true;
        }

        Assert.True(worst <= 0.35f, $"exceeded a limit by {worst} m/s");
        Assert.True(slowedForCurve, "there was a limit to keep to");
    }

    [Fact]
    public void AClimb_SlowsATrain_AndItStillGetsThere()
    {
        float Top(Func<float, float> ground)
        {
            var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (x, _) => ground(x))), 1);
            sim.Execute(new BuildTrackCommand(-1200, 0, -900, 0));
            Assert.True(sim.Execute(new BuildTrackCommand(-900, 0, 1200, 0)) > 0);
            int a = sim.Execute(new BuildRailStationCommand(-850, 0)), b = sim.Execute(new BuildRailStationCommand(1100, 0));
            int depot = sim.Execute(new AddRailDepotCommand(-900, 0));
            int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
            int id = sim.Execute(new BuyTrainCommand(depot, "diesel", "freight", 8, line));
            Assert.True(id >= 0);
            var train = sim.Trains[id];
            float top = 0f;
            for (int i = 0; i < 9000 && !(train.StopIndex == 1 && train.Status == TrainStatus.AtStation); i++)
            {
                sim.Step();
                if (train.StopIndex == 1 && train.Speed > 0) top = MathF.Max(top, train.Speed);
            }
            Assert.Equal(1, train.StopIndex);
            return top;
        }

        float flat = Top(_ => 60f);
        float hill = Top(x => 60f + 0.034f * x);   // 3.4 % all the way: a halved limit and a heavy train

        Assert.True(hill < flat - 3f, $"flat top {flat}, climbing top {hill}");
    }

    [Fact]
    public void ABranchLine_IsReachedThroughTheJunction_ByTheLineThatNeedsIt()
    {
        // A main line A-B and a branch to C leaving it at a junction; the same network with a train on the line to B, and with one on the line to C.
        HashSet<int> Stops(bool toBranch)
        {
            var sim = RailWorlds.Flat();
            sim.Execute(new BuildTrackCommand(0, 0, 600, 0));
            sim.Execute(new BuildTrackCommand(600, 0, 1400, 0));
            sim.Execute(new BuildTrackCommand(600, 0, 900, 300));    // 45 degrees off
            int a = sim.Execute(new BuildRailStationCommand(100, 0));
            int b = sim.Execute(new BuildRailStationCommand(1300, 0));
            int c = sim.Execute(new BuildRailStationCommand(900, 300));
            int depot = sim.Execute(new AddRailDepotCommand(0, 0));
            int line = sim.Execute(new CreateLineCommand(new[] { a, toBranch ? c : b }));
            int id = sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", 2, line));
            Assert.True(id >= 0);
            var seen = new HashSet<int>();
            for (int i = 0; i < 40000; i++)
            {
                sim.Step();
                if (sim.Trains[id].Status == TrainStatus.AtStation) seen.Add(sim.TargetStationOf(sim.Trains[id]));
            }
            return seen;
        }

        Assert.Equal(new[] { 0, 1 }, Stops(false).OrderBy(x => x));   // A and B
        Assert.Equal(new[] { 0, 2 }, Stops(true).OrderBy(x => x));    // A and C: it takes the branch
    }

    [Fact]
    public void BuildingAStationOnTheTrackATrainIsRunningOn_DoesNotStopOrLoseTheTrain()
    {
        var w = RailWorlds.StraightLine(2000f);
        var sim = w.Sim;
        var train = sim.Trains[0];
        for (int i = 0; i < 4000 && !(train.StopIndex == 1 && train.Speed > 15f); i++) sim.Step();
        Assert.Equal(1, train.StopIndex);
        var (x, _, _, _, _) = sim.RoutePoint(train, train.Head);

        // A signal and a station on the stretch ahead of it and one under its tail.
        Assert.True(sim.Execute(new BuildSignalCommand(x + 250f, 0, SignalKind.Block)) >= 0);
        Assert.True(sim.Execute(new BuildRailStationCommand(x + 600f, 0)) >= 0);
        Assert.True(sim.Execute(new BuildSignalCommand(x - 40f, 0, SignalKind.Block)) >= 0);
        long income = train.Income;
        RailWorlds.Run(sim, 900);

        Assert.True(train.Active);
        Assert.True(train.Income > income, "the train kept earning");
        for (int t = 0; t < sim.Rail.TrackCount; t++)
        {
            int owner = sim.TrackHeldBy(new TrackId(t));
            Assert.True(owner == -1 || owner == 0);
        }
    }

    [Fact]
    public void TearingUpTheTrackUnderATrain_LosesIt_AndTearingUpTheTrackAheadOnlyStopsIt()
    {
        var w = RailWorlds.StraightLine(2000f);
        var sim = w.Sim;
        var train = sim.Trains[0];
        for (int i = 0; i < 4000 && !(train.StopIndex == 1 && train.Speed > 12f); i++) sim.Step();
        var (x, _, _, _, _) = sim.RoutePoint(train, train.Head);
        // The piece ahead.
        var ahead = sim.Rail.TrackAt(x + 400f, 0, 5f)!.Value;
        Assert.Equal(1, sim.Execute(new RemoveTrackCommand(ahead.Value)));
        RailWorlds.Run(sim, 200);

        Assert.True(train.Active);
        Assert.NotEqual(TrainStatus.AtStation, train.Status);
        Assert.True(train.Speed < 0.5f, "it stopped short of the gap");

        var under = sim.Rail.TrackAt(x, 0, 60f)!.Value;
        sim.Execute(new RemoveTrackCommand(under.Value));
        sim.Step();

        Assert.False(train.Active, "the track under it went, and the train with it");
    }

    [Fact]
    public void ATrainWhoseWayIsCut_GoesOnWhenTheTrackIsBuiltAgain()
    {
        var w = RailWorlds.StraightLine(1800f);
        var sim = w.Sim;
        var train = sim.Trains[0];
        for (int i = 0; i < 4000 && !(train.StopIndex == 1 && train.Speed > 12f); i++) sim.Step();
        var (x, _, _, _, _) = sim.RoutePoint(train, train.Head);
        var gap = sim.Rail.TrackAt(x + 500f, 0, 5f)!.Value;
        var g = sim.Rail.GetTrack(gap);
        var (gx0, gz0) = sim.Rail.GetNode(g.From);
        var (gx1, gz1) = sim.Rail.GetNode(g.To);
        sim.Execute(new RemoveTrackCommand(gap.Value));
        RailWorlds.Run(sim, 120);
        Assert.True(train.Speed < 0.5f);

        Assert.True(sim.Execute(new BuildTrackCommand(gx0, gz0, gx1, gz1)) > 0);
        RailWorlds.Run(sim, 400);

        Assert.True(train.Active);
        Assert.Equal(1, train.StopIndex);
        long income = train.Income;
        RailWorlds.Run(sim, 900);
        Assert.True(train.Income > income, "it is running the line again");
    }

    [Fact]
    public void EditingARailLine_SendsTheTrainToTheNewStop_AndClosingAStationSkipsIt()
    {
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildTrackCommand(0, 0, 300, 0));
        sim.Execute(new BuildTrackCommand(300, 0, 2000, 0));
        int a = sim.Execute(new BuildRailStationCommand(100, 0));
        int b = sim.Execute(new BuildRailStationCommand(900, 0));
        int c = sim.Execute(new BuildRailStationCommand(1900, 0));
        int depot = sim.Execute(new AddRailDepotCommand(300, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int id = sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", 3, line));
        var train = sim.Trains[id];
        var seen = new HashSet<int>();

        Assert.Equal(1, sim.Execute(new EditLineCommand(line, new[] { a, b, c })));
        for (int i = 0; i < 40000; i++)
        {
            sim.Step();
            if (train.Status == TrainStatus.AtStation) seen.Add(sim.TargetStationOf(train));
        }
        Assert.Contains(c, seen);

        Assert.Equal(1, sim.Execute(new CloseStationCommand(b)));
        RailWorlds.Run(sim, 60);   // a train standing at it finishes loading and leaves
        seen.Clear();
        for (int i = 0; i < 40000; i++)
        {
            sim.Step();
            if (train.Status == TrainStatus.AtStation) seen.Add(sim.TargetStationOf(train));
        }
        Assert.DoesNotContain(b, seen);
        Assert.True(seen.Contains(a) && seen.Contains(c), "the line carries on between the stations that are left");
        Assert.Equal(-1, sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", 3, sim.Execute(new CreateLineCommand(new[] { a, b })))));
    }

    [Fact]
    public void FreightWagons_CarryTheCargoOfAMapWithoutTowns_AndAreSoldWithTheirLine()
    {
        var w = RailWorlds.StraightLine(wagon: "freight", wagons: 4);
        var train = w.Sim.Trains[0];

        RailWorlds.Run(w.Sim, 1500);

        Assert.True(train.Income > 0);
        Assert.Equal(1, w.Sim.Execute(new DeleteLineCommand(w.Line)));
        Assert.False(train.Active, "a train goes with its line");
    }

    [Fact]
    public void ATrainOnALine_PaysUpkeepEveryDay_AndAStationCostsItsOwn()
    {
        var w = RailWorlds.StraightLine();
        long before = w.Sim.Company.Total(LedgerEntry.Upkeep);

        RailWorlds.Run(w.Sim, 600);   // one day

        long upkeep = before - w.Sim.Company.Total(LedgerEntry.Upkeep);
        var loco = w.Sim.Locomotives[0];
        var wagon = w.Sim.Wagons[0];
        long expected = (long)Math.Ceiling(loco.UpkeepPerDay + 3 * wagon.UpkeepPerDay) + 2 * (long)Math.Ceiling(w.Sim.Rail.Rail.StationUpkeepPerDay);
        Assert.Equal(expected, upkeep);
        Assert.Equal((long)Math.Ceiling(loco.UpkeepPerDay + 3 * wagon.UpkeepPerDay), w.Sim.Trains[0].UpkeepPaid);
    }
}
