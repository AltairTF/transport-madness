using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Priority, yield and stop signs at road junctions.</summary>
public class RoadSignTests
{
    const float Arm = 300f;

    static Simulation Cross(ulong seed = 3)
    {
        var net = new RoadNetwork(Terrain.Create(256, 8f, (_, _) => 5f));
        var sim = new Simulation(net, seed);
        sim.Execute(new BuildRoadCommand(-Arm, 0, Arm, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(0, -Arm, 0, Arm, RoadType.TwoLane, 14f));
        return sim;
    }

    static LaneId Approach(Simulation sim, float x, float z)
    {
        var net = sim.Network;
        int node = net.FindNodeNear(0, 0, 1f);
        int start = net.FindNodeNear(x, z, 1f);
        return net.IncomingLanes(node).First(l => net.GetLane(l).StartNode == start);
    }

    static LaneId West(Simulation sim) => Approach(sim, -Arm, 0f);
    static LaneId South(Simulation sim) => Approach(sim, 0f, -Arm);
    static LaneId East(Simulation sim) => Approach(sim, Arm, 0f);
    static LaneId North(Simulation sim) => Approach(sim, 0f, Arm);

    static float Edge(Simulation sim, LaneId lane) => sim.Network.GetLane(lane).Length - RoadNetwork.JunctionReach(sim.Network.GetLane(lane).Length);

    // ---- Building ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ASignGoesOnAnApproachOfAJunction_ForMoney_AndReplacesWhatWasThere()
    {
        var sim = Cross();
        long before = sim.Company.Money;

        Assert.Equal(1, sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Yield)));

        Assert.Equal(before - sim.RoadSignPrice(RoadSignKind.Yield), sim.Company.Money);
        Assert.Equal(RoadSignKind.Yield, sim.SignOnApproach(West(sim)));
        Assert.Null(sim.SignOnApproach(South(sim)));
        Assert.Equal(0, sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Yield)));     // already there
        Assert.Equal(1, sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Stop)));      // a stop sign takes its place
        Assert.Equal(RoadSignKind.Stop, sim.SignOnApproach(West(sim)));
        Assert.Single(sim.RoadSigns, s => s.Active);
        Assert.Equal(1, sim.Execute(new RemoveRoadSignCommand(-20, 0)));
        Assert.Null(sim.SignOnApproach(West(sim)));
        Assert.Equal(0, sim.Execute(new RemoveRoadSignCommand(-20, 0)));
    }

    [Fact]
    public void ThereAreNoSignsAtTheEndOfARoad_OrWithoutMoney_OrWhereTrafficLightsStand()
    {
        var sim = Cross();

        Assert.Equal(0, sim.Execute(new BuildRoadSignCommand(-290, 0, RoadSignKind.Stop)));   // a dead end is no junction
        Assert.Equal(0, sim.Execute(new BuildRoadSignCommand(100, 100, RoadSignKind.Stop)));  // nowhere near
        sim.Execute(new BuildTrafficLightCommand(sim.Network.FindNodeNear(0, 0, 1f)));
        Assert.Equal(0, sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Stop)));    // the lights decide

        var poor = Cross();
        poor.Company.Money = 100;
        Assert.Equal(0, poor.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Stop)));
        Assert.Equal(100, poor.Company.Money);
    }

    [Fact]
    public void APrioritySign_AlsoGoesOnTheOppositeApproach_AndComesDownWithIt()
    {
        var sim = Cross();
        long before = sim.Company.Money;

        Assert.Equal(2, sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Priority)));

        Assert.Equal(before - 2 * sim.RoadSignPrice(RoadSignKind.Priority), sim.Company.Money);
        Assert.Equal(RoadSignKind.Priority, sim.SignOnApproach(West(sim)));
        Assert.Equal(RoadSignKind.Priority, sim.SignOnApproach(East(sim)));
        Assert.Null(sim.SignOnApproach(South(sim)));
        Assert.Equal(2, sim.Execute(new RemoveRoadSignCommand(-20, 0)));
        Assert.Null(sim.SignOnApproach(East(sim)));
    }

    [Fact]
    public void ASignStaysOnItsRoad_WhenTheRoadIsSplit()
    {
        var sim = Cross();
        sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Stop));

        // A road across the west arm splits it: the piece that reaches the junction is a new road.
        Assert.True(sim.Execute(new BuildRoadCommand(-150, -200, -150, 200, RoadType.TwoLane, 14f)) > 0);
        sim.Step();

        var west = sim.Network.IncomingLanes(sim.Network.FindNodeNear(0, 0, 1f)).First(l => MathF.Abs(sim.Network.GetLane(l).AngleAtEnd - MathF.PI) < 0.1f);
        Assert.NotEqual(0, sim.Network.GetLane(west).StartNode - sim.Network.FindNodeNear(-Arm, 0, 1f));   // not the old arm any more
        Assert.Equal(RoadSignKind.Stop, sim.SignOnApproach(west));
        var sign = Assert.Single(sim.RoadSigns);
        Assert.True(sign.Active);
        Assert.NotEmpty(sim.LanesOf(sign));
    }

    // ---- Driving ----------------------------------------------------------------------------------------------------

    /// <summary>A seed for which two cars, one on the west approach and one on the south, both go straight on.</summary>
    static (Simulation Sim, int A, int B)? Race(ulong seed, Action<Simulation>? signs, float posA, float speedA, float posB, float speedB)
    {
        var sim = Cross(seed);
        signs?.Invoke(sim);
        var west = West(sim); var south = South(sim);
        int a = sim.SpawnVehicle(west, posA, speedA);
        int b = sim.SpawnVehicle(south, posB, speedB);
        sim.SetKeepLane(a, true);
        sim.SetKeepLane(b, true);
        bool straightA = sim.Network.JunctionTurn(west, sim.NextLaneOf(a)) < 0.3f, straightB = sim.Network.JunctionTurn(south, sim.NextLaneOf(b)) < 0.3f;
        return straightA && straightB ? (sim, a, b) : null;
    }

    /// <summary>Which car got through the junction first: -1 for the west one, 1 for the south one; no incident on the way.</summary>
    static int Winner(Simulation sim, int a, int b)
    {
        var west = West(sim); var south = South(sim);
        for (int step = 0; step < 20 * 40; step++)
        {
            sim.Step();
            bool aOut = sim.GetVehicle(a).Lane != west, bOut = sim.GetVehicle(b).Lane != south;
            if (aOut || bOut)
            {
                Assert.Empty(sim.Incidents);
                return aOut ? -1 : 1;
            }
        }
        throw new InvalidOperationException("nobody got through");
    }

    /// <summary>Runs the same race on the first few seeds where both cars go straight on.</summary>
    static void ForSomeSeeds(Action<Simulation>? signs, float posA, float posB, int expected, string why)
    {
        int tried = 0;
        for (ulong seed = 1; seed <= 60 && tried < 4; seed++)
        {
            if (Race(seed, signs, posA, 12f, posB, 12f) is not { } r) continue;
            tried++;
            Assert.True(expected == Winner(r.Sim, r.A, r.B), $"seed {seed}: {why}");
        }
        Assert.True(tried >= 3, "no seed sent both cars straight on");
    }

    [Fact]
    public void WithoutSigns_TheCarThatArrivesFirstGoesFirst()
    {
        ForSomeSeeds(null, 234f, 230f, -1, "the west car is closer to the junction");
        ForSomeSeeds(null, 230f, 234f, 1, "the south car is closer to the junction");
    }

    [Fact]
    public void AYieldSign_MakesTheCarWaitForOneThatArrivesLater()
    {
        ForSomeSeeds(s => s.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Yield)), 234f, 230f, 1, "the west car yields");
    }

    [Fact]
    public void APrioritySign_MakesTheOtherRoadWait_EvenForACarThatArrivesFirst()
    {
        // The south car has priority: the west car, closer to the junction, waits.
        ForSomeSeeds(s => s.Execute(new BuildRoadSignCommand(0, -20, RoadSignKind.Priority)), 234f, 230f, 1, "the south road has priority");
        // The west road has priority: the west car goes first even though it arrives second.
        ForSomeSeeds(s => s.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Priority)), 230f, 234f, -1, "the west road has priority");
    }

    [Fact]
    public void ACarWithPriority_DoesNotSlowDownForTheOtherRoad()
    {
        int tried = 0;
        for (ulong seed = 1; seed <= 60 && tried < 3; seed++)
        {
            if (Race(seed, s => s.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Priority)), 234f, 12f, 234f, 12f) is not { } r) continue;
            tried++;
            float slowest = float.MaxValue;
            var west = West(r.Sim);
            for (int step = 0; step < 20 * 12 && r.Sim.GetVehicle(r.A).Lane == west; step++)
            {
                r.Sim.Step();
                slowest = MathF.Min(slowest, r.Sim.GetVehicle(r.A).Speed);
            }
            Assert.True(slowest > 9f, $"seed {seed}: the car with priority dropped to {slowest:0.0} m/s");
        }
        Assert.True(tried >= 2, "no seed sent both cars straight on");
    }

    [Fact]
    public void AStopSign_StopsACarAtTheLineForTheStopTime_EvenWhenNothingIsComing_ThenItGoes()
    {
        int tried = 0;
        for (ulong seed = 1; seed <= 60 && tried < 3; seed++)
        {
            var sim = Cross(seed);
            sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Stop));
            var west = West(sim);
            int car = sim.SpawnVehicle(west, 150f, 13f);
            sim.SetKeepLane(car, true);
            if (sim.Network.JunctionTurn(west, sim.NextLaneOf(car)) >= 0.3f) continue;
            tried++;
            float edge = Edge(sim, west);
            var rules = sim.Network.Tuning.Roads.Signs;
            int stood = 0, longest = 0;
            bool crossed = false;
            for (int step = 0; step < 20 * 60 && !crossed; step++)
            {
                sim.Step();
                var s = sim.GetVehicle(car);
                if (s.Lane != west) { crossed = true; break; }
                stood = s.Speed < rules.StopSpeed ? stood + 1 : 0;
                longest = Math.Max(longest, stood);
                Assert.True(s.Position <= edge + 0.5f || longest * Simulation.StepSeconds >= rules.StopSeconds - 0.1f, $"seed {seed}: it went past the line without stopping");
            }

            Assert.True(crossed, $"seed {seed}: it never went on");
            Assert.True(longest * Simulation.StepSeconds >= rules.StopSeconds - 0.1f, $"seed {seed}: it stood still for only {longest * Simulation.StepSeconds:0.0} s");
        }
        Assert.True(tried >= 2, "no seed sent the car straight on");
    }

    [Fact]
    public void AYieldSign_ForACarWithNothingComing_DoesNotStopIt()
    {
        int tried = 0;
        for (ulong seed = 1; seed <= 60 && tried < 3; seed++)
        {
            var sim = Cross(seed);
            sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Yield));
            var west = West(sim);
            int car = sim.SpawnVehicle(west, 150f, 13f);
            sim.SetKeepLane(car, true);
            if (sim.Network.JunctionTurn(west, sim.NextLaneOf(car)) >= 0.3f) continue;
            tried++;
            float slowest = float.MaxValue;
            for (int step = 0; step < 20 * 30 && sim.GetVehicle(car).Lane == west; step++)
            {
                sim.Step();
                slowest = MathF.Min(slowest, sim.GetVehicle(car).Speed);
            }
            Assert.True(slowest > 4f, $"seed {seed}: it slowed to {slowest:0.0} m/s with nothing coming");
        }
        Assert.True(tried >= 2, "no seed sent the car straight on");
    }

    [Fact]
    public void AStopSignAtABusyJunction_LetsTheMainRoadRunAndTheSideRoadThroughInTheGaps_WithoutCollisions()
    {
        var sim = Cross(9);
        sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Stop));
        sim.Execute(new BuildRoadSignCommand(20, 0, RoadSignKind.Stop));
        var side = new[] { West(sim), East(sim) };
        var main = new[] { South(sim), North(sim) };
        var startLane = new Dictionary<int, LaneId>();
        foreach (var lane in side.Concat(main))
            for (int k = 0; k < 4; k++)
            {
                int car = sim.SpawnVehicle(lane, sim.Network.GetLane(lane).Length - 60f - 24f * k, 10f);
                sim.SetKeepLane(car, true);
                startLane[car] = lane;
            }
        var through = new HashSet<int>();

        for (int step = 0; step < 20 * 600; step++)
        {
            sim.Step();
            foreach (var (car, lane) in startLane)
                if (sim.GetVehicle(car).Kind == VehicleKind.Car && sim.GetVehicle(car).Lane != lane) through.Add(car);
        }

        Assert.DoesNotContain(sim.Incidents, inc => inc.Kind == IncidentKind.Junction);
        Assert.True(startLane.Where(c => side.Contains(c.Value)).All(c => through.Contains(c.Key)), "every car of the side road got through");
        Assert.True(startLane.Where(c => main.Contains(c.Value)).All(c => through.Contains(c.Key)), "every car of the main road got through");
    }

    [Fact]
    public void ASignedRoadNetworkWithTraffic_RunsTheSameEveryTime_AndTheCommandLogReplaysIt()
    {
        Simulation Run()
        {
            var sim = Cross(21);
            sim.Execute(new BuildRoadSignCommand(-20, 0, RoadSignKind.Stop));
            sim.Execute(new BuildRoadSignCommand(0, -20, RoadSignKind.Priority));
            sim.Execute(new SeedTrafficCommand(40));
            for (int i = 0; i < 20 * 120; i++) sim.Step();
            return sim;
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a.StateHash(), b.StateHash());

        var fresh = new Simulation(new RoadNetwork(Terrain.Create(256, 8f, (_, _) => 5f)), 21);   // the log has the roads in it too
        fresh.Replay(a.CommandLog, a.Tick);
        Assert.Equal(a.StateHash(), fresh.StateHash());
    }
}
