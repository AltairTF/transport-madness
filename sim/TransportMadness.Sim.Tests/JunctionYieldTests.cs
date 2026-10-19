using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Who waits for whom at an uncontrolled junction, and what happens when nobody can.</summary>
public class JunctionYieldTests
{
    const float Arm = 300f;

    static RoadNetwork Cross()
    {
        var net = new RoadNetwork();
        int c = net.AddNode(0, 0);
        foreach (var (x, z) in new[] { (-Arm, 0f), (Arm, 0f), (0f, -Arm), (0f, Arm) })
            net.AddRoad(net.AddNode(x, z), c, RoadType.TwoLane, 14f);
        return net;
    }

    /// <summary>The lane that arrives at the centre from the arm that starts at (x, z).</summary>
    static LaneId Approach(RoadNetwork net, float x, float z) =>
        net.IncomingLanes(0).First(l => net.GetLane(l).StartNode == net.FindNodeNear(x, z, 1f));

    static float Edge => Arm - RoadNetwork.JunctionReach(Arm);

    [Fact]
    public void ConflictZone_IsWhereThePathsMeet_AndMergesStayInConflictToTheEnd()
    {
        var net = new RoadNetwork();
        int c = net.AddNode(0, 0);
        foreach (var (x, z) in new[] { (-Arm, 0f), (Arm, 0f), (0f, -Arm), (0f, Arm) })
            net.AddRoad(net.AddNode(x, z), c, RoadType.TwoLane, 14f);
        var west = Approach(net, -Arm, 0f);
        var south = Approach(net, 0f, -Arm);
        var east = net.GetLane(Approach(net, Arm, 0f)).Id;
        LaneId Leaves(float x, float z) => net.GetRoad(net.FindRoad(c, net.FindNodeNear(x, z, 1f))!.Value).ForwardLanes
            .Concat(net.GetRoad(net.FindRoad(c, net.FindNodeNear(x, z, 1f))!.Value).BackwardLanes).First(l => net.GetLane(l).StartNode == c);
        var toEast = Leaves(Arm, 0f);
        var toNorth = Leaves(0f, Arm);

        var crossing = net.GetConflictZone(west, toEast, south, toNorth, 5f);

        Assert.NotNull(crossing);
        float lengthWE = net.JunctionLength(west, toEast), lengthSN = net.JunctionLength(south, toNorth);
        // Straight through, the paths meet in the middle of each: the stretch surrounds the middle and does not reach either end.
        Assert.InRange(crossing!.Value.InA, 0.05f * lengthWE, 0.5f * lengthWE);
        Assert.InRange(crossing.Value.OutA, 0.5f * lengthWE, 0.95f * lengthWE);
        Assert.InRange(crossing.Value.InB, 0.05f * lengthSN, 0.5f * lengthSN);
        Assert.InRange(crossing.Value.OutB, 0.5f * lengthSN, 0.95f * lengthSN);

        var merge = net.GetConflictZone(west, toNorth, south, toNorth, 5f);
        Assert.NotNull(merge);
        Assert.Equal(net.JunctionLength(west, toNorth), merge!.Value.OutA, 0.01f);
        Assert.Equal(net.JunctionLength(south, toNorth), merge.Value.OutB, 0.01f);

        Assert.Null(net.GetConflictZone(west, toEast, Approach(net, Arm, 0f), Leaves(-Arm, 0f), 5f)); // straight past each other
    }

    /// <summary>A seed for which both cars bound for the middle go straight on.</summary>
    static (Simulation Sim, int A, int B)? Setup(RoadNetwork net, ulong seed, float posA, float speedA, float posB, float speedB)
    {
        var sim = new Simulation(net, seed);
        var west = Approach(net, -Arm, 0f);
        var south = Approach(net, 0f, -Arm);
        int a = sim.SpawnVehicle(west, posA, speedA);
        int b = sim.SpawnVehicle(south, posB, speedB);
        sim.SetKeepLane(a, true);
        sim.SetKeepLane(b, true);
        bool straightA = net.JunctionTurn(west, sim.NextLaneOf(a)) < 0.3f, straightB = net.JunctionTurn(south, sim.NextLaneOf(b)) < 0.3f;
        return straightA && straightB ? (sim, a, b) : null;
    }

    [Fact]
    public void CarsArrivingTogether_TakeTurns_AndNeverTouch()
    {
        int tried = 0;
        for (ulong seed = 1; seed <= 60 && tried < 6; seed++)
        {
            var setup = Setup(Cross(), seed, 230f, 12f, 232f, 12f);
            if (setup is not { } s) continue;
            tried++;
            for (int step = 0; step < 20 * 40; step++) s.Sim.Step();
            Assert.Empty(s.Sim.Incidents);
            Assert.NotEqual(s.Sim.GetVehicle(s.A).Lane, Approach(s.Sim.Network, -Arm, 0f)); // both went through
            Assert.NotEqual(s.Sim.GetVehicle(s.B).Lane, Approach(s.Sim.Network, 0f, -Arm));
        }
        Assert.True(tried >= 3, "no seed sent both cars straight on");
    }

    [Fact]
    public void ACarWaitsAtTheEdge_WhileAnotherStandsInTheCrossing()
    {
        int tried = 0;
        for (ulong seed = 1; seed <= 60 && tried < 4; seed++)
        {
            var setup = Setup(Cross(), seed, Arm + 2.5f, 0f, 200f, 10f); // A stands where the paths cross, B comes in fast
            if (setup is not { } s) continue;
            tried++;
            s.Sim.SetHalted(s.A, true);
            for (int step = 0; step < 20 * 40; step++) s.Sim.Step();
            var b = s.Sim.GetVehicle(s.B);
            Assert.True(b.Position <= Edge, $"seed {seed}: the car drove into the junction ({b.Position:F1} of {Edge:F1})");
            Assert.True(b.Position > Edge - 8f, $"seed {seed}: stopped far from the edge ({b.Position:F1})");
            Assert.Equal(0f, b.Speed, 0.05f);
            Assert.Empty(s.Sim.Incidents);
        }
        Assert.True(tried >= 2, "no seed sent both cars straight on");
    }

    [Fact]
    public void ACarThatCannotStopInTime_GoesOn_AndTheCollisionInsideTheJunctionIsRecorded()
    {
        int tried = 0;
        for (ulong seed = 1; seed <= 60 && tried < 3; seed++)
        {
            // Both are already in the crossing, at the same place on their paths.
            var setup = Setup(Cross(), seed, Arm + 2.5f, 3f, Arm + 2.5f, 3f);
            if (setup is not { } s) continue;
            tried++;
            s.Sim.Step();
            var incident = Assert.Single(s.Sim.Incidents);
            Assert.Equal(IncidentKind.Junction, incident.Kind);
            Assert.Equal(VehicleKind.Wreck, s.Sim.GetVehicle(s.A).Kind);
            Assert.Equal(VehicleKind.Wreck, s.Sim.GetVehicle(s.B).Kind);
        }
        Assert.True(tried >= 2, "no seed sent both cars straight on");
    }

    [Fact]
    public void CarsFollowingEachOtherThroughTheJunction_AreNotCrashes()
    {
        var net = Cross();
        var sim = new Simulation(net, 4);
        var west = Approach(net, -Arm, 0f);
        for (int k = 0; k < 4; k++)
        {
            int v = sim.SpawnVehicle(west, 150f + 12f * k, 10f);
            sim.SetKeepLane(v, true);
        }
        for (int step = 0; step < 20 * 40; step++) sim.Step();

        Assert.Empty(sim.Incidents);
    }

    [Fact]
    public void AQueueOnTheExitLane_KeepsTheJunctionClear()
    {
        int tried = 0;
        for (ulong seed = 1; seed <= 60 && tried < 4; seed++)
        {
            var net = Cross();
            var sim = new Simulation(net, seed);
            var west = Approach(net, -Arm, 0f);
            int car = sim.SpawnVehicle(west, 200f, 10f);
            sim.SetKeepLane(car, true);
            var exit = sim.NextLaneOf(car);
            tried++;
            int stuck = sim.SpawnVehicle(exit, 10f, 0f); // a vehicle standing just beyond the junction: no room for another
            sim.SetHalted(stuck, true);
            for (int step = 0; step < 20 * 40; step++) sim.Step();
            var v = sim.GetVehicle(car);
            Assert.True(v.Lane == west && v.Position <= Edge, $"seed {seed}: the car is in the junction ({v.Position:F1})");
            Assert.Empty(sim.Incidents);
        }
        Assert.Equal(4, tried);
    }
}
