using System.Diagnostics;
using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class TrafficTests
{
    /// <summary>size x size grid of nodes, 'spacing' metres apart, joined by 2-lane roads; every other row is an avenue.</summary>
    static RoadNetwork Grid(int size, float spacing)
    {
        var net = new RoadNetwork();
        var nodes = new int[size, size];
        for (int x = 0; x < size; x++)
            for (int z = 0; z < size; z++)
                nodes[x, z] = net.AddNode(x * spacing, z * spacing);
        for (int x = 0; x < size; x++)
            for (int z = 0; z < size; z++)
            {
                if (x + 1 < size)
                    net.AddRoad(nodes[x, z], nodes[x + 1, z], z % 2 == 0 ? RoadType.Avenue2x2 : RoadType.TwoLane, 14f);
                if (z + 1 < size)
                    net.AddRoad(nodes[x, z], nodes[x, z + 1], RoadType.TwoLane, 14f);
            }
        return net;
    }

    static Simulation RunGrid(ulong seed, int vehicles, int steps)
    {
        var sim = new Simulation(Grid(8, 200f), seed);
        sim.SeedTraffic(vehicles);
        for (int i = 0; i < steps; i++) sim.Step();
        return sim;
    }

    [Fact]
    public void SeedTraffic_PlacesRequestedVehiclesWhenThereIsRoom()
    {
        var sim = new Simulation(Grid(8, 200f), seed: 7);

        int placed = sim.SeedTraffic(300);

        Assert.Equal(300, placed);
        Assert.Equal(300, sim.VehicleCount);
    }

    [Fact]
    public void SameSeed_GivesIdenticalStateAfterManySteps()
    {
        var first = RunGrid(seed: 42, vehicles: 300, steps: 2000);
        var second = RunGrid(seed: 42, vehicles: 300, steps: 2000);

        Assert.Equal(first.StateHash(), second.StateHash());
    }

    [Fact]
    public void DifferentSeeds_GiveDifferentState()
    {
        var first = RunGrid(seed: 1, vehicles: 300, steps: 500);
        var second = RunGrid(seed: 2, vehicles: 300, steps: 500);

        Assert.NotEqual(first.StateHash(), second.StateHash());
    }

    [Fact]
    public void Traffic_KeepsSpeedsAndPositionsSane()
    {
        var sim = RunGrid(seed: 3, vehicles: 400, steps: 1500);
        var net = sim.Network;

        for (int i = 0; i < sim.VehicleCount; i++)
        {
            var v = sim.GetVehicle(i);
            var lane = net.GetLane(v.Lane);
            Assert.True(v.Speed >= 0f && v.Speed <= lane.SpeedLimit * 1.05f, $"speed {v.Speed}");
            Assert.True(v.Position >= 0f && v.Position < lane.Length, $"pos {v.Position}");
        }
    }

    [Fact]
    public void ThousandVehicles_StepWellInsideTheEightTimesSpeedBudget()
    {
        // 8x speed = 160 steps/s. Debug build, CI-grade machine: generous bound of one second per 160 steps.
        var sim = new Simulation(Grid(12, 200f), seed: 5);
        Assert.Equal(1000, sim.SeedTraffic(1000));
        for (int i = 0; i < 40; i++) sim.Step(); // warm up

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 160; i++) sim.Step();
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 1000, $"{clock.ElapsedMilliseconds} ms");
    }
}
