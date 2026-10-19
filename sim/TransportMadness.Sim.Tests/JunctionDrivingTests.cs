using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Vehicles drive through junctions along their paths instead of jumping across the node.</summary>
public class JunctionDrivingTests
{
    /// <summary>Four 300 m two-lane arms meeting at the origin.</summary>
    static RoadNetwork Cross(float arm = 300f)
    {
        var net = new RoadNetwork();
        int c = net.AddNode(0, 0);
        foreach (var (x, z) in new[] { (-arm, 0f), (arm, 0f), (0f, -arm), (0f, arm) })
            net.AddRoad(net.AddNode(x, z), c, RoadType.TwoLane, 14f);
        return net;
    }

    /// <summary>World position and heading of the middle of a vehicle, in a junction or on the open road.</summary>
    static (float X, float Z, float Heading) Pose(Simulation sim, int i)
    {
        var v = sim.GetVehicle(i);
        float centre = v.Position - Simulation.VehicleLength * 0.5f;
        if (sim.JunctionPoseOf(i, centre) is { } pose) return (pose.X, pose.Z, pose.Heading);
        return sim.Network.LanePoint(v.Lane, centre);
    }

    static float Angle(float a, float b)
    {
        float d = MathF.Abs(a - b) % MathF.Tau;
        return MathF.Min(d, MathF.Tau - d);
    }

    [Fact]
    public void ACarDrivingThroughAJunction_MovesSmoothly_WhateverWayItGoes()
    {
        var kinds = new HashSet<string>();
        for (ulong seed = 1; seed <= 24; seed++)
        {
            var net = Cross();
            var sim = new Simulation(net, seed);
            var lane = net.IncomingLanes(0)[(int)(seed % 4)]; // each of the four arms in turn
            int car = sim.SpawnVehicle(lane, 250f, 10f);
            sim.SetKeepLane(car, true);
            var next = sim.NextLaneOf(car);
            float turn = net.JunctionTurn(lane, next);
            kinds.Add(turn < 0.3f ? "straight" : "turn");

            var (px, pz, ph) = Pose(sim, car);
            int crossed = 0;
            for (int step = 0; step < 20 * 20; step++)
            {
                sim.Step();
                var (x, z, h) = Pose(sim, car);
                float moved = MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                Assert.True(moved < 0.9f, $"seed {seed}: jumped {moved:F2} m at step {step}");
                Assert.True(Angle(h, ph) < 0.12f, $"seed {seed}: heading jumped {Angle(h, ph):F2} rad at step {step}");
                (px, pz, ph) = (x, z, h);
                if (sim.JunctionPoseOf(car, sim.GetVehicle(car).Position - Simulation.VehicleLength * 0.5f) != null) crossed++;
            }
            Assert.True(crossed > 20, $"seed {seed}: only {crossed} steps in the junction");
        }
        Assert.Equal(2, kinds.Count); // both straight and turning crossings were exercised
    }

    [Fact]
    public void TurningCars_SlowDownForTheTurn_StraightOnesDoNot()
    {
        float slowestStraight = float.MaxValue, fastestTurn = 0f;
        int turns = 0, straights = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var net = Cross();
            var sim = new Simulation(net, seed);
            var lane = net.IncomingLanes(0)[0];
            int car = sim.SpawnVehicle(lane, 150f, 14f);
            sim.SetKeepLane(car, true);
            var next = sim.NextLaneOf(car);
            float turn = net.JunctionTurn(lane, next);
            float limit = MathF.Sqrt(net.Tuning.Driving.TurnLateralAcceleration * net.JunctionLength(lane, next) / MathF.Max(turn, 1e-3f));
            float inJunction = 0f;
            for (int step = 0; step < 20 * 25; step++)
            {
                sim.Step();
                var v = sim.GetVehicle(car);
                if (sim.JunctionPoseOf(car, v.Position - Simulation.VehicleLength * 0.5f) != null) inJunction = MathF.Max(inJunction, v.Speed);
            }
            if (turn > 1f) { turns++; fastestTurn = MathF.Max(fastestTurn, inJunction / limit); }
            else if (turn < 0.1f) { straights++; slowestStraight = MathF.Min(slowestStraight, inJunction); }
        }
        Assert.True(turns > 5 && straights > 5, $"{turns} turns, {straights} straight");
        Assert.True(fastestTurn < 1.3f, $"a turn was taken at {fastestTurn:F2} times its limit");
        Assert.True(slowestStraight > 8f, $"a straight crossing was taken at {slowestStraight:F1} m/s");
    }

    [Fact]
    public void CrossingTraffic_YieldsInsideTheJunction_SoNoTwoCarsOverlapAndNothingCrashes()
    {
        int worstPairs = 0;
        for (ulong seed = 1; seed <= 4; seed++)
        {
            var net = Cross(200f);
            var sim = new Simulation(net, seed);
            sim.SeedTraffic(60);
            for (int i = 0; i < sim.VehicleCount; i++) sim.SetKeepLane(i, true);
            for (int step = 0; step < 20 * 600; step++)
            {
                sim.Step();
                var inside = new List<(int Id, float X, float Z)>();
                for (int i = 0; i < sim.VehicleCount; i++)
                {
                    var v = sim.GetVehicle(i);
                    if (v.Kind != VehicleKind.Car) continue;
                    if (sim.JunctionPoseOf(i, v.Position - Simulation.VehicleLength * 0.5f) is { } p) inside.Add((i, p.X, p.Z));
                }
                int close = 0;
                for (int a = 0; a < inside.Count; a++)
                    for (int b = a + 1; b < inside.Count; b++)
                        if ((inside[a].X - inside[b].X) * (inside[a].X - inside[b].X) + (inside[a].Z - inside[b].Z) * (inside[a].Z - inside[b].Z) < Simulation.VehicleWidth * Simulation.VehicleWidth)
                            close++;
                worstPairs = Math.Max(worstPairs, close);
            }
            Assert.Empty(sim.Incidents);
        }
        Assert.Equal(0, worstPairs);
    }
}
