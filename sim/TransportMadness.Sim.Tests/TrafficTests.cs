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
            Assert.True(v.Speed >= 0f && v.Speed <= lane.SpeedLimit * 1.25f, $"speed {v.Speed}");
            // A vehicle driving through a junction is past the end of its lane until it comes out on the next one.
            Assert.True(v.Position >= 0f && v.Position < lane.Length + 40f, $"pos {v.Position}");
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

    [Fact]
    public void BusyGrid_KeepsFlowingInsteadOfLockingUp()
    {
        var sim = RunGrid(seed: 11, vehicles: 400, steps: 4000);

        float total = 0f;
        int crawling = 0;
        for (int i = 0; i < sim.VehicleCount; i++)
        {
            float v = sim.GetVehicle(i).Speed;
            total += v;
            if (v < 0.2f) crawling++;
        }
        Assert.True(total / sim.VehicleCount > 3f, $"mean speed {total / sim.VehicleCount:F2} m/s");
        Assert.True(crawling < sim.VehicleCount / 4, $"{crawling} of {sim.VehicleCount} stopped");
    }

    [Fact]
    public void DenseWorldWithAvenues_KeepsFlowingForTenMinutes_WithoutJunctionPileUps()
    {
        // 1000 cars on a 10x10 grid with avenues, the size the game starts with. A lane change right after a junction
        // used to cut in front of traffic still arriving from the feeding lanes and the wrecks jammed the whole map.
        var sim = new Simulation(Grid(10, 250f), seed: 1);
        sim.SeedTraffic(1000);
        for (int i = 0; i < 20 * 60 * 10; i++) sim.Step();

        int stopped = 0, cars = 0;
        for (int i = 0; i < sim.VehicleCount; i++)
        {
            var v = sim.GetVehicle(i);
            if (v.Kind != VehicleKind.Car) continue;
            cars++;
            if (v.Speed < 0.2f) stopped++;
        }
        Assert.True(stopped < cars / 10, $"{stopped} of {cars} stopped");
        Assert.True(sim.Incidents.Count <= 3, $"{sim.Incidents.Count} incidents");
    }
}
