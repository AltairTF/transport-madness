using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Road types GDD: a road that ends on a highway gets an off-ramp and an on-ramp on the carriageway on its side.</summary>
public class RampTests
{
    /// <summary>A highway along x from -800 to 800, and a two-lane road from the south (z = -300) ending on it at x = 0.</summary>
    static Simulation WithInterchange(out int built)
    {
        var sim = new Simulation(new RoadNetwork(), 11);
        Assert.True(sim.Execute(new BuildRoadCommand(-800, 0, 800, 0, RoadType.Highway, 28f)) > 0);
        built = sim.Execute(new BuildRoadCommand(0, -300, 0, 0, RoadType.TwoLane, 14f));
        return sim;
    }

    static List<Road> Live(Simulation sim, RoadType type) =>
        Enumerable.Range(0, sim.Network.RoadCount).Select(r => sim.Network.GetRoad(new RoadId(r))).Where(r => !r.Retired && r.Type == type).ToList();

    static HashSet<int> Reachable(RoadNetwork net, LaneId start)
    {
        var seen = new HashSet<int> { start.Value };
        var todo = new Stack<LaneId>();
        todo.Push(start);
        while (todo.Count > 0)
            foreach (var next in net.GetLane(todo.Pop()).Next)
                if (seen.Add(next.Value)) todo.Push(next);
        return seen;
    }

    [Fact]
    public void ARoadEndingOnAHighway_GetsAnOffRampAndAnOnRamp_AndStopsShortOfIt()
    {
        var sim = WithInterchange(out int built);
        Assert.True(built >= 3);
        Assert.Equal(2, Live(sim, RoadType.Ramp).Count);
        var side = Live(sim, RoadType.TwoLane).Single();
        var (jx, jz) = sim.Network.GetNode(side.To);
        Assert.Equal(0f, jx, 1);
        Assert.Equal(-45f, jz, 1); // the standoff
        Assert.All(Live(sim, RoadType.Ramp), r => Assert.True(r.IsOneWay));
        // no junction of the road with the highway: the node on the highway at x = 0 does not exist
        Assert.True(sim.Network.FindNodeNear(0, 0, 0.5f) < 0);
    }

    [Fact]
    public void TheRamps_ServeOnlyTheCarriagewayOnTheRoadsSide()
    {
        var sim = WithInterchange(out _);
        var net = sim.Network;
        var side = Live(sim, RoadType.TwoLane).Single();
        var onRamp = Live(sim, RoadType.Ramp).Single(r => net.GetNode(r.From).Z < -1f);
        // the road comes from the south, which is to the left of the +x direction: its carriageway is the westbound one
        var merged = net.GetLane(onRamp.ForwardLanes[0]).Next;
        Assert.NotEmpty(merged);
        Assert.All(merged, l => Assert.True(MathF.Cos(net.GetLane(l).Path.Heading0 - MathF.PI) > 0.9f));
        // and a westbound vehicle upstream of the ramps can leave by the off-ramp, which leads on to the road
        var offRamp = Live(sim, RoadType.Ramp).Single(r => r != onRamp);
        var leaves = Enumerable.Range(0, net.LaneCount).Select(l => net.GetLane(new LaneId(l)))
            .Where(l => !l.Retired && l.Next.Contains(offRamp.ForwardLanes[0])).ToList();
        Assert.NotEmpty(leaves);
        Assert.All(leaves, l => Assert.True(MathF.Cos(l.Path.Heading0 - MathF.PI) > 0.9f));
        Assert.Contains(side.BackwardLanes[0].Value, Reachable(net, offRamp.ForwardLanes[0]));
    }

    [Fact]
    public void TheEastboundCarriageway_CannotTurnIntoTheRamps()
    {
        var sim = WithInterchange(out _);
        var net = sim.Network;
        var ramps = Live(sim, RoadType.Ramp).SelectMany(r => r.ForwardLanes).Select(l => l.Value).ToHashSet();
        for (int l = 0; l < net.LaneCount; l++)
        {
            var lane = net.GetLane(new LaneId(l));
            if (lane.Retired || net.GetRoad(lane.Road).Type != RoadType.Highway) continue;
            bool east = MathF.Cos(lane.Path.Heading0) > 0.9f;
            if (east) Assert.DoesNotContain(lane.Next, n => ramps.Contains(n.Value));
        }
    }

    [Fact]
    public void TheOnRampGivesWay_WhereItJoinsTheHighway()
    {
        var sim = WithInterchange(out _);
        var on = Live(sim, RoadType.Ramp).Single(r => sim.Network.GetNode(r.From).Z < -1f); // the on-ramp starts at the road's end node
        Assert.Equal(RoadSignKind.Yield, sim.SignOnApproach(on.ForwardLanes[0]));
    }

    [Fact]
    public void RampsTooCloseToAnotherRamp_OrAtAShallowAngle_AreRefused()
    {
        var sim = WithInterchange(out _);
        Assert.Equal(0, sim.Execute(new BuildRoadCommand(100, -300, 100, 0, RoadType.TwoLane, 14f)));          // 100 m along: too close
        Assert.Equal(0, sim.Execute(new BuildRoadCommand(500, -60, 300, 0, RoadType.TwoLane, 14f)));          // nearly parallel
        Assert.True(sim.Execute(new BuildRoadCommand(500, 300, 500, 0, RoadType.TwoLane, 14f)) > 0);          // 500 m along, from the north: fine
        Assert.Equal(4, Live(sim, RoadType.Ramp).Count);
    }

    [Fact]
    public void TrafficRunsOverAnInterchange_AndTheRampsCarryIt()
    {
        var sim = WithInterchange(out _);
        sim.Execute(new BuildRoadCommand(500, 300, 500, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(-800, -300, -800, 0, RoadType.TwoLane, 14f)); // a road to the highway's end node is refused; this one ends on it
        sim.Execute(new SeedTrafficCommand(120));
        Worlds.Run(sim, 0.5);
        Assert.True(sim.VehicleCount > 50);
    }

    [Fact]
    public void Vehicles_EnterTheHighwayByTheOnRamp_AndLeaveItByTheOffRamp()
    {
        var sim = WithInterchange(out _);
        sim.Execute(new BuildRoadCommand(500, 300, 500, 0, RoadType.TwoLane, 14f));
        var net = sim.Network;
        sim.Execute(new SeedTrafficCommand(150));
        var rampLanes = Live(sim, RoadType.Ramp).SelectMany(r => r.ForwardLanes).Select(l => l.Value).ToHashSet();
        var highwayLanes = Live(sim, RoadType.Highway).SelectMany(r => r.ForwardLanes.Concat(r.BackwardLanes)).Select(l => l.Value).ToHashSet();
        bool onRamp = false, onHighwayAfterRamp = false;
        var fromRamp = new HashSet<int>();
        long steps = (long)(0.6 * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < steps; t++)
        {
            sim.Step();
            for (int v = 0; v < sim.VehicleCount; v++)
            {
                int lane = sim.GetVehicle(v).Lane.Value;
                if (rampLanes.Contains(lane)) onRamp = true;
                if (highwayLanes.Contains(lane) && rampLanes.Contains(sim.PreviousLaneOf(v).Value)) onHighwayAfterRamp = true;
            }
        }
        Assert.True(onRamp, "no vehicle drove a ramp");
        Assert.True(onHighwayAfterRamp, "no vehicle got from a ramp onto the highway");
    }

    [Fact]
    public void TwoInterchanges_FitOnAShortHighway_OneFromEachSide()
    {
        var sim = new Simulation(new RoadNetwork(), 11);
        Assert.True(sim.Execute(new BuildRoadCommand(-520, 0, 520, 0, RoadType.Highway, 28f)) > 0);
        Assert.True(sim.Execute(new BuildRoadCommand(0, -240, 0, 0, RoadType.TwoLane, 14f)) > 0);
        Assert.Equal(0, sim.Execute(new BuildRoadCommand(260, 240, 260, 0, RoadType.TwoLane, 14f))); // 260 m from the first: ramps too close
        Assert.True(sim.Execute(new BuildRoadCommand(330, 240, 330, 0, RoadType.TwoLane, 14f)) > 0);
        Assert.Equal(4, Live(sim, RoadType.Ramp).Count);
    }

    [Fact]
    public void DemolishingTheHighway_TakesTheRampsWithIt()
    {
        var sim = WithInterchange(out _);
        var net = sim.Network;
        foreach (var h in Live(sim, RoadType.Highway)) sim.Execute(new RemoveRoadCommand(h.Id.Value));
        Assert.Empty(Live(sim, RoadType.Ramp));
    }

    [Fact]
    public void AnInterchange_RoundTripsThroughSaveAndLoad_AndRunsOnTheSame()
    {
        var sim = WithInterchange(out _);
        sim.Execute(new SeedTrafficCommand(60));
        Worlds.Run(sim, 0.05);
        using var stream = new MemoryStream();
        SaveGame.Save(sim, stream, SaveGame.InfoOf(sim, "t", new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc)), null);
        stream.Position = 0;
        var loaded = SaveGame.Load(stream).Sim;
        Assert.Equal(sim.StateHash(), loaded.StateHash());
        Worlds.Run(sim, 0.1);
        Worlds.Run(loaded, 0.1);
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }
}
