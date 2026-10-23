using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Rail worlds put together by commands: a straight single track with stations, a depot and a line.</summary>
internal static class RailWorlds
{
    internal static Simulation Flat(ulong seed = 1, Tuning? tuning = null) =>
        new(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Worlds.Late(tuning)), seed);

    internal static void Run(Simulation sim, double seconds)
    {
        long ticks = (long)Math.Round(seconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    internal sealed record Line1(Simulation Sim, int A, int B, int Line, int Depot);

    /// <summary>Track from x = 0 to x = <paramref name="length"/> with a depot node at 300, stations 100 m in from each end, and a line between the stations.</summary>
    internal static Line1 StraightLine(float length = 1500f, ulong seed = 1, Tuning? tuning = null, bool addTrain = true, int wagons = 3, string wagon = "coach")
    {
        var sim = Flat(seed, tuning);
        Assert.True(sim.Execute(new BuildTrackCommand(0, 0, 300, 0)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(300, 0, length, 0)) > 0);
        int a = sim.Execute(new BuildRailStationCommand(100, 0));
        int b = sim.Execute(new BuildRailStationCommand(length - 100, 0));
        Assert.True(a >= 0 && b >= 0, "stations");
        int depot = sim.Execute(new AddRailDepotCommand(300, 0));
        Assert.True(depot >= 0, "depot");
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.True(line >= 0, "line");
        if (addTrain) Assert.True(sim.Execute(new BuyTrainCommand(depot, "diesel", wagon, wagons, line)) >= 0, "train");
        return new Line1(sim, a, b, line, depot);
    }
}

public class TrainTests
{
    [Fact]
    public void ARailStation_StandsAtANode_SplitsTheTrack_AndCostsMoney()
    {
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildTrackCommand(0, 0, 600, 0));
        long before = sim.Company.Money;

        int id = sim.Execute(new BuildRailStationCommand(300, 0));

        Assert.True(id >= 0);
        var station = sim.Stations[id];
        Assert.Equal(StationMode.Rail, station.Mode);
        Assert.Equal(2, sim.Rail.TracksAt(station.RailNode).Count);
        Assert.Equal(before - sim.Rail.Rail.StationPrice, sim.Company.Money);
        Assert.Equal(-1, sim.Execute(new BuildRailStationCommand(300, 0)));  // one to a node
    }

    [Fact]
    public void ALineMustKeepToOneMode()
    {
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildTrackCommand(0, 0, 600, 0));
        sim.Execute(new BuildRoadCommand(0, 200, 600, 200, RoadType.TwoLane, 14f));
        int rail = sim.Execute(new BuildRailStationCommand(300, 0));
        int rail2 = sim.Execute(new BuildRailStationCommand(500, 0));
        int road = sim.Execute(new BuildStationCommand(300, 200));

        Assert.True(sim.Execute(new CreateLineCommand(new[] { rail, rail2 })) >= 0);
        Assert.Equal(-1, sim.Execute(new CreateLineCommand(new[] { rail, road })));
        Assert.Equal(StationMode.Rail, sim.Lines[0].Mode);
    }

    [Fact]
    public void ATrainCannotBeBoughtWithoutAWayToTheFirstStation_OrForARoadLine()
    {
        var w = RailWorlds.StraightLine(addTrain: false);
        var sim = w.Sim;
        sim.Execute(new BuildTrackCommand(300, 400, 900, 400));
        int island = sim.Execute(new BuildRailStationCommand(500, 400));
        int island2 = sim.Execute(new BuildRailStationCommand(800, 400));
        int cut = sim.Execute(new CreateLineCommand(new[] { island, island2 }));

        Assert.Equal(-1, sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 3, cut)));   // not joined to the depot
        Assert.Equal(-1, sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 0, w.Line)));  // no wagons
        Assert.Equal(-1, sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 99, w.Line))); // too many
        Assert.Equal(-1, sim.Execute(new BuyTrainCommand(w.Depot, "monorail", "coach", 3, w.Line)));   // no such locomotive
        Assert.True(sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 3, w.Line)) >= 0);
    }

    [Fact]
    public void BuyingATrain_CostsTheLocomotiveAndTheWagons()
    {
        var w = RailWorlds.StraightLine(addTrain: false);
        long before = w.Sim.Company.Money;
        long price = w.Sim.TrainPrice("diesel", "coach", 4);

        int id = w.Sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 4, w.Line));

        Assert.True(id >= 0);
        Assert.Equal(before - price, w.Sim.Company.Money);
        Assert.Equal(18f + 4 * 20f, w.Sim.Trains[id].Length);
    }

    [Fact]
    public void ATrain_DrivesToTheFirstStation_StopsWithItsFrontAtTheNode_AndLoads()
    {
        var w = RailWorlds.StraightLine();
        var train = w.Sim.Trains[0];

        for (int i = 0; i < 4000 && train.Status != TrainStatus.AtStation; i++) w.Sim.Step();

        Assert.Equal(TrainStatus.AtStation, train.Status);
        Assert.Equal(w.A, w.Sim.TargetStationOf(train));
        var (x, _, _, _, _) = w.Sim.RoutePoint(train, train.Head);
        var (sx, _) = w.Sim.Rail.GetNode(w.Sim.Stations[w.A].RailNode);
        Assert.Equal(sx, x, 1);
        Assert.Equal(0f, train.Speed);
    }

    [Fact]
    public void ATrain_NeverExceedsItsLimit_NorAcceleratesFasterThanItsEngineAllows()
    {
        var w = RailWorlds.StraightLine(3000f);
        var train = w.Sim.Trains[0];
        var loco = w.Sim.Locomotives[0];
        float top = 0f, maxAccel = 0f;

        for (int i = 0; i < 4000; i++)
        {
            w.Sim.Step();
            top = MathF.Max(top, train.Speed);
            maxAccel = MathF.Max(maxAccel, train.Acceleration);
        }

        Assert.True(top <= w.Sim.Rail.Rail.SpeedLimit + 0.01f, $"top speed {top}");
        Assert.True(top > 20f, $"it should get up to speed, was {top}");
        float mass = w.Sim.MassOf(train);
        Assert.True(maxAccel <= loco.TractiveEffort / mass + 0.05f, $"acceleration {maxAccel} on {mass} kg");
    }

    [Fact]
    public void ATrain_StopsShortOfTheStation_WithoutOvershootingTheNode()
    {
        var w = RailWorlds.StraightLine();
        var train = w.Sim.Trains[0];
        var (stationX, _) = w.Sim.Rail.GetNode(w.Sim.Stations[w.B].RailNode);
        float furthest = 0f;

        for (int i = 0; i < 6000; i++)
        {
            w.Sim.Step();
            if (train.Status == TrainStatus.AtStation && train.StopIndex == 1) break;
            var (x, _, _, _, _) = w.Sim.RoutePoint(train, train.Head);
            if (train.StopIndex == 1) furthest = MathF.Max(furthest, x);
        }

        Assert.Equal(1, train.StopIndex);
        Assert.True(furthest <= stationX + 0.05f, $"front reached {furthest}, station at {stationX}");
        Assert.True(furthest >= stationX - 1.0f, $"front stopped {stationX - furthest} m short");
    }

    [Fact]
    public void ATrain_ReversesAtTheEndOfTheLine_AndGoesBack()
    {
        var w = RailWorlds.StraightLine();
        var train = w.Sim.Trains[0];
        var seen = new HashSet<int>();

        for (int i = 0; i < 20000; i++)
        {
            w.Sim.Step();
            if (train.Status == TrainStatus.AtStation) seen.Add(w.Sim.TargetStationOf(train));
        }

        Assert.Contains(w.A, seen);
        Assert.Contains(w.B, seen);
    }

    [Fact]
    public void ATrainOnALine_EarnsMoney_FromPassengersTheStationsMake()
    {
        var w = RailWorlds.StraightLine();
        long before = w.Sim.Company.Money;
        var train = w.Sim.Trains[0];

        RailWorlds.Run(w.Sim, 1800);

        Assert.True(train.Income > 0, "the train was paid");
        Assert.Equal(train.Income, w.Sim.Lines[w.Line].Income);
        Assert.True(w.Sim.Lines[w.Line].Delivered > 0);
    }

    [Fact]
    public void ASoldTrain_IsGone_PaysBackPartOfItsPrice_AndFreesTheTrack()
    {
        var w = RailWorlds.StraightLine();
        RailWorlds.Run(w.Sim, 60);
        long before = w.Sim.Company.Money;
        long price = w.Sim.TrainPrice("diesel", "coach", 3);

        Assert.Equal(0, w.Sim.Execute(new SellTrainCommand(0)));

        Assert.False(w.Sim.Trains[0].Active);
        Assert.Equal(before + (long)MathF.Round(price * 0.6f), w.Sim.Company.Money);
        for (int t = 0; t < w.Sim.Rail.TrackCount; t++) Assert.Equal(-1, w.Sim.TrackHeldBy(new TrackId(t)));
        Assert.Equal(-1, w.Sim.Execute(new SellTrainCommand(0)));
    }

    [Fact]
    public void RailRuns_AreTheSameEveryTime_AndReplayFromTheCommandLog()
    {
        var a = RailWorlds.StraightLine(seed: 7);
        var b = RailWorlds.StraightLine(seed: 7);
        RailWorlds.Run(a.Sim, 600);
        RailWorlds.Run(b.Sim, 600);
        Assert.Equal(a.Sim.StateHash(), b.Sim.StateHash());

        var fresh = RailWorlds.Flat(7);
        fresh.Replay(a.Sim.CommandLog, a.Sim.Tick);
        Assert.Equal(a.Sim.StateHash(), fresh.StateHash());
    }
}

internal static class RailTownWorlds
{
    internal sealed record Scenario(Simulation Sim, int Line, int DepotA, int DepotB, float Length, int Crossings, int Buses);

    /// <summary>
    /// The two closest towns of a generated map joined by a road with a bus line, and by a single railway between them with a passing
    /// loop in the middle (path signals on the approaches), a station at each end, a depot at each end and a second road across the
    /// middle of the railway. Null when the layout does not fit the towns at this offset (a crossing too close to a junction, say).
    /// </summary>
    internal static Scenario? Build(ulong seed, float sideOffset, int towns = 4, float margin = 90f)
    {
        var (sim, _) = Worlds.World(seed, towns);
        sim.Execute(new TakeLoanCommand(1000000));
        var (i, j) = Worlds.ClosestPair(sim);
        var ta = sim.Towns[i]; var tb = sim.Towns[j];
        float dx = tb.X - ta.X, dz = tb.Z - ta.Z, d = MathF.Sqrt(dx * dx + dz * dz); dx /= d; dz /= d;
        float nx = -dz, nz = dx;
        (float X, float Z) P(float along, float side = 0f) => (ta.X + dx * (margin + along) + nx * (sideOffset + side), ta.Z + dz * (margin + along) + nz * (sideOffset + side));
        float len = d - 2 * margin;
        if (len < 500f) return null;

        // Roads first: the bus road between the towns, and a cross road over the middle of the railway.
        bool busRoad = Worlds.Connect(sim, ta, tb);
        var m = P(len * 0.5f);
        sim.Execute(new BuildRoadCommand(m.X + nx * 260f, m.Z + nz * 260f, m.X - nx * 260f, m.Z - nz * 260f, RoadType.TwoLane, 14f));

        float j1 = len * 0.30f, j2 = len * 0.70f, bend = 45f;
        var pts = new (float, float)[]
        {
            P(0), P(j1), P(j2), P(len),
            P(j1 + bend, bend), P(j2 - bend, bend),
        };
        bool ok = sim.Execute(new BuildTrackCommand(pts[0].Item1, pts[0].Item2, pts[1].Item1, pts[1].Item2)) > 0
            && sim.Execute(new BuildTrackCommand(pts[1].Item1, pts[1].Item2, pts[2].Item1, pts[2].Item2)) > 0
            && sim.Execute(new BuildTrackCommand(pts[2].Item1, pts[2].Item2, pts[3].Item1, pts[3].Item2)) > 0
            && sim.Execute(new BuildTrackCommand(pts[1].Item1, pts[1].Item2, pts[4].Item1, pts[4].Item2)) > 0
            && sim.Execute(new BuildTrackCommand(pts[4].Item1, pts[4].Item2, pts[5].Item1, pts[5].Item2)) > 0
            && sim.Execute(new BuildTrackCommand(pts[5].Item1, pts[5].Item2, pts[2].Item1, pts[2].Item2)) > 0;
        if (!ok) return null;

        int a = sim.Execute(new BuildRailStationCommand(pts[0].Item1, pts[0].Item2));
        int b = sim.Execute(new BuildRailStationCommand(pts[3].Item1, pts[3].Item2));
        int dA = sim.Execute(new AddRailDepotCommand(pts[0].Item1, pts[0].Item2));
        int dB = sim.Execute(new AddRailDepotCommand(pts[3].Item1, pts[3].Item2));
        if (a < 0 || b < 0 || dA < 0 || dB < 0) return null;
        // Path signals facing every track that arrives at the points at either end of the loop.
        var rail = sim.Rail;
        foreach (var (x, z) in new[] { pts[1], pts[2] })
        {
            int node = rail.FindNodeNear(x, z, 1f);
            foreach (var id in rail.TracksAt(node).ToList())
            {
                var t = rail.GetTrack(id);
                var (ox, oz) = rail.GetNode(t.Other(node));
                float len2 = MathF.Sqrt((ox - x) * (ox - x) + (oz - z) * (oz - z));
                float cx = x + (ox - x) / len2 * 10f, cz = z + (oz - z) / len2 * 10f;
                if (sim.Execute(new BuildSignalCommand(cx, cz, SignalKind.Path)) < 0) return null;
            }
        }
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        if (line < 0) return null;
        return new Scenario(sim, line, dA, dB, len, sim.Crossings.Count, 0);
    }

    internal static Scenario Find(ulong seed)
    {
        // The layout starts at the middle of a town by default; an organic town has streets there, so it is tried further out as well.
        foreach (float margin in new[] { 90f, 200f, 320f })
            foreach (float offset in new[] { 0f, 40f, -40f, 80f, -80f, 120f, -120f, 160f, -160f, 25f, -25f, 60f, -60f })
                if (Build(seed, offset, margin: margin) is { } scenario) return scenario;
        throw new InvalidOperationException($"no rail layout fits seed {seed}");
    }
}
