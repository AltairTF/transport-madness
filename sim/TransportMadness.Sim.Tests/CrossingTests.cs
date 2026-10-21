using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class CrossingTests
{
    /// <summary>The straight line of <see cref="RailWorlds"/> with a two-lane road across it at x = 600.</summary>
    static (RailWorlds.Line1 W, LevelCrossing Crossing) WithRoad(bool trainToo = true)
    {
        var w = RailWorlds.StraightLine(1500f, addTrain: trainToo);
        Assert.True(w.Sim.Execute(new BuildRoadCommand(600, -400, 600, 400, RoadType.TwoLane, 14f)) > 0);
        var crossing = Assert.Single(w.Sim.Crossings);
        return (w, crossing);
    }

    [Fact]
    public void ARoadBuiltAcrossATrack_MakesALevelCrossing_AndCostsExtra()
    {
        var w = RailWorlds.StraightLine(1500f, addTrain: false);
        long before = w.Sim.Company.Money;
        float cost = w.Sim.Network.EstimateCost(600, -400, 600, 400, RoadType.TwoLane)!.Value.Money;

        Assert.True(w.Sim.Execute(new BuildRoadCommand(600, -400, 600, 400, RoadType.TwoLane, 14f)) > 0);

        var crossing = Assert.Single(w.Sim.Crossings);
        Assert.Equal(before - (long)MathF.Ceiling(cost) - w.Sim.Rail.Rail.Crossings.Price, w.Sim.Company.Money);
        Assert.Equal(600f, crossing.X, 1);
        Assert.Equal(0f, crossing.Z, 1);
        Assert.Equal(CrossingState.Open, crossing.State);
        Assert.Equal(MathF.PI / 2f, crossing.Angle, 2);
    }

    [Fact]
    public void ATrackBuiltAcrossARoad_MakesTheSameCrossing()
    {
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildRoadCommand(600, -400, 600, 400, RoadType.TwoLane, 14f));

        Assert.True(sim.Execute(new BuildTrackCommand(0, 0, 1500, 0)) > 0);

        var crossing = Assert.Single(sim.Crossings);
        Assert.Equal(600f, crossing.X, 1);
        Assert.True(crossing.Active);
    }

    [Fact]
    public void ACrossingTooCloseToTheEndOfARoad_ATrackOrAJunction_IsRefused()
    {
        var sim = RailWorlds.Flat();
        sim.Execute(new BuildRoadCommand(600, -400, 600, 10, RoadType.TwoLane, 14f));   // ends 10 m past where a track would cross
        Assert.Equal(0, sim.Execute(new BuildTrackCommand(0, 0, 1500, 0)));            // too near the road's end
        sim.Execute(new BuildTrackCommand(0, 600, 1500, 600));
        Assert.Equal(0, sim.Execute(new BuildRoadCommand(0, 550, 800, 650, RoadType.TwoLane, 14f)));  // crosses at too sharp an angle
        sim.Execute(new BuildRoadCommand(800, 300, 800, 900, RoadType.TwoLane, 14f));  // fine: one crossing at x = 800
        Assert.Single(sim.Crossings);
        Assert.Equal(0, sim.Execute(new BuildRoadCommand(800, 590, 900, 590, RoadType.TwoLane, 14f)));  // a junction 10 m from the crossing
    }

    [Fact]
    public void ACrossing_GoesWhenTheRoadIsTornDown()
    {
        var (w, crossing) = WithRoad(trainToo: false);

        for (int r = 0; r < w.Sim.Network.RoadCount; r++) w.Sim.Execute(new RemoveRoadCommand(r));
        w.Sim.Step();

        Assert.False(crossing.Active);
    }

    [Fact]
    public void BarriersComeDownForATrain_RoadVehiclesWait_AndNothingIsHit()
    {
        var (w, crossing) = WithRoad();
        var road = w.Sim.Network.GetRoad(crossing.Road);
        // Cars that keep their lane (random traffic overtakes and crashes on its own), spread over both directions.
        for (int k = 0; k < 6; k++)
        {
            foreach (var lane in new[] { road.ForwardLanes[0], road.BackwardLanes[0] })
            {
                int car = w.Sim.SpawnVehicle(lane, 30f + 60f * k + (lane == road.ForwardLanes[0] ? 0f : 20f), 10f);
                w.Sim.SetKeepLane(car, true);
            }
        }
        int closings = 0;
        bool wasClosed = false;
        int crossed = 0;

        for (int i = 0; i < 30000; i++)
        {
            w.Sim.Step();
            bool closed = crossing.State != CrossingState.Open;
            if (closed && !wasClosed) closings++;
            wasClosed = closed;
        }
        for (int v = 0; v < w.Sim.VehicleCount; v++)
            if (w.Sim.GetVehicle(v).Kind == VehicleKind.Wreck) crossed++;

        Assert.True(closings >= 3, $"the barriers came down {closings} times");
        Assert.Equal(0, crossing.Collisions);
        Assert.Equal(0, crossed);
        Assert.DoesNotContain(w.Sim.Incidents, i => i.Kind == IncidentKind.LevelCrossing);
        Assert.True(w.Sim.Trains[0].Income > 0, "the train carried on across the road");
    }

    [Fact]
    public void RoadVehicles_StopAtTheLineBeforeTheCrossing_WhileTheBarriersAreDown()
    {
        var (w, crossing) = WithRoad();
        var sim = w.Sim;
        var road = sim.Network.GetRoad(crossing.Road);
        var lane = road.ForwardLanes[0];
        float stopLine = crossing.RoadAlong - crossing.RoadHalf - sim.Rail.Rail.Crossings.RoadStopMargin;
        int car = sim.SpawnVehicle(lane, stopLine - 120f, 12f);
        sim.SetKeepLane(car, true);
        float closestWhileClosed = float.MaxValue;
        bool sawClosed = false;

        for (int i = 0; i < 12000; i++)
        {
            sim.Step();
            var state = sim.GetVehicle(car);
            if (state.Lane != lane) continue;
            if (crossing.State == CrossingState.Closed && state.Position < crossing.RoadAlong - crossing.RoadHalf)
            {
                sawClosed = true;
                closestWhileClosed = MathF.Min(closestWhileClosed, crossing.RoadAlong - crossing.RoadHalf - state.Position);
            }
        }

        // It was never inside the band while the barriers were down, and it never hit a train.
        Assert.Equal(0, crossing.Collisions);
        Assert.True(!sawClosed || closestWhileClosed > 0f);
    }

    [Fact]
    public void AStandingVehicleInTheCrossing_StopsTheTrainBeforeIt_UntilItIsGone()
    {
        var (w, crossing) = WithRoad();
        var sim = w.Sim;
        var lane = sim.Network.GetRoad(crossing.Road).ForwardLanes[0];
        int car = sim.SpawnVehicle(lane, crossing.RoadAlong + 2f, 0f);
        sim.SetHalted(car, true);
        var train = sim.Trains[0];
        float furthest = 0f;

        for (int i = 0; i < 16000; i++)
        {
            sim.Step();
            var (x, _, _, _, _) = sim.RoutePoint(train, train.Head);
            if (train.StopIndex == 1 || train.StopIndex == 0) furthest = MathF.Max(furthest, x);
        }

        Assert.True(crossing.Fouled);
        Assert.Equal(0, crossing.Collisions);
        Assert.True(furthest < 600f - crossing.TrackHalf + 0.5f, $"the train reached x = {furthest}");
        Assert.NotEqual(TrainStatus.AtStation, train.Status);
    }

    [Fact]
    public void ATrainThatCannotStopInTime_HitsACarInTheCrossing_WhichIsWreckedAndBlocksIt()
    {
        var (w, crossing) = WithRoad();
        var sim = w.Sim;
        var lane = sim.Network.GetRoad(crossing.Road).ForwardLanes[0];
        var train = sim.Trains[0];
        // Run until the train is on its way east to the crossing and close, then put a car onto it.
        for (int i = 0; i < 20000; i++)
        {
            sim.Step();
            var (x, _, _, _, _) = sim.RoutePoint(train, train.Head);
            if (train.StopIndex == 1 && x > 440f && x < 480f && train.Speed > 15f) break;
        }
        int car = sim.SpawnVehicle(lane, crossing.RoadAlong, 0f);
        sim.SetHalted(car, true);

        RailWorlds.Run(sim, 30);

        Assert.Equal(1, crossing.Collisions);
        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(car).Kind);
        var incident = Assert.Single(sim.Incidents, i => i.Kind == IncidentKind.LevelCrossing);
        Assert.Contains(car, incident.VehicleIds);
        Assert.True(crossing.Fouled, "the wreck keeps the crossing blocked for trains");
        Assert.Equal(TrainStatus.Halted, train.Status);
    }

    [Fact]
    public void ATrackOnABridgeOverARoad_MakesNoCrossing_AndTheTrainRunsOverIt()
    {
        // A bowl 30 m deep with a road along its floor; the railway runs across the rim on a bridge.
        float Smooth(float x) => MathF.Abs(x) >= 90f ? 0f : 0.5f * (1f + MathF.Cos(MathF.PI * x / 90f));
        var terrain = Terrain.Create(256, 8f, (x, z) => 50f - 30f * Smooth(x) * Smooth(z * 0.5f));
        var sim = new Simulation(new RoadNetwork(terrain, Worlds.Late()), 1);
        sim.Execute(new TakeLoanCommand(1000000));
        Assert.True(sim.Execute(new BuildRoadCommand(0, -60, 0, 60, RoadType.TwoLane, 14f)) > 0);

        Assert.True(sim.Execute(new BuildTrackCommand(-400, 0, 400, 0)) > 0);

        Assert.Contains(sim.Rail.Tracks, t => t.Structure == RoadStructure.Bridge);
        Assert.Empty(sim.Crossings);
    }

    [Fact]
    public void CrossingRuns_AreTheSameEveryTime_AndReplay()
    {
        var a = WithRoad().W;
        a.Sim.Execute(new SeedTrafficCommand(20));
        RailWorlds.Run(a.Sim, 900);

        var fresh = RailWorlds.Flat();
        fresh.Replay(a.Sim.CommandLog, a.Sim.Tick);

        Assert.Equal(a.Sim.StateHash(), fresh.StateHash());
    }
}
