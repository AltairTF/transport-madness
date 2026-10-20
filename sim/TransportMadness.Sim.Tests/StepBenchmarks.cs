using System.Diagnostics;
using TransportMadness.Sim;
using Xunit.Abstractions;
using static TransportMadness.Sim.Tests.Worlds;

namespace TransportMadness.Sim.Tests;

/// <summary>
/// Step time of the simulation in a Release build, per world: a plain grid with 1000 vehicles, a railway with several trains and a
/// generated map with towns, rush traffic, buses and trains. Only runs when BENCH is set (the CI benchmark job sets it, and writes
/// the lines to the job summary); each world must stay inside its budget so a slowdown fails the job. At 8x speed the game takes
/// 160 steps a second, so a step may cost 6.25 ms before the simulation falls behind.
/// </summary>
public class StepBenchmarks
{
    readonly ITestOutputHelper _out;
    public StepBenchmarks(ITestOutputHelper o) => _out = o;
    static bool On => Environment.GetEnvironmentVariable("BENCH") != null;

    /// <summary>The slowest a step may be one time in a hundred: at 8x speed the game takes 160 steps a second.</summary>
    const double BudgetWorstOneInHundredMs = 6.25;

    record Result(string Name, int Steps, double AvgMs, double P99Ms, double MaxMs);

    Result Measure(string name, Simulation sim, int warmup, int steps)
    {
        for (int i = 0; i < warmup; i++) sim.Step();
        var ms = new double[steps];
        for (int i = 0; i < steps; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            sim.Step();
            ms[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        _out.WriteLine($"  at the end: {sim.VehicleCount} road vehicles ({sim.TripVehicleCount} trip cars), trains {string.Join(' ', sim.Trains.GroupBy(t => t.Status).Select(g => $"{g.Key}={g.Count()}"))}");
        Array.Sort(ms);
        var r = new Result(name, steps, ms.Average(), ms[(int)(steps * 0.99)], ms[^1]);
        string line = $"BENCH {name}: avg {r.AvgMs:0.000} ms, p99 {r.P99Ms:0.000} ms, max {r.MaxMs:0.000} ms over {steps} steps";
        _out.WriteLine(line);
        // A job summary on GitHub shows the table; a run by hand just prints the lines.
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } path)
        {
            bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
            File.AppendAllText(path, (fresh ? "| world | steps | avg ms | p99 ms | max ms |\n|---|---|---|---|---|\n" : "") + FormattableString.Invariant($"| {name} | {steps} | {r.AvgMs:0.000} | {r.P99Ms:0.000} | {r.MaxMs:0.000} |\n"));
        }
        return r;
    }

    /// <summary>The average budget is about seven times what a 2026 laptop measures (grid 0.3 ms, trains 0.01 ms, mixed map 0.02 ms), so a shared runner passes and a real regression does not.</summary>
    static void InBudget(Result r, double budgetAvgMs)
    {
        Assert.True(r.AvgMs < budgetAvgMs, $"{r.Name}: average step {r.AvgMs:0.000} ms, budget {budgetAvgMs} ms");
        Assert.True(r.P99Ms < BudgetWorstOneInHundredMs, $"{r.Name}: 99th percentile step {r.P99Ms:0.000} ms, budget {BudgetWorstOneInHundredMs} ms");
    }

    /// <summary>The grid of <c>TrafficTests</c>: every other row is an avenue.</summary>
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
                if (x + 1 < size) net.AddRoad(nodes[x, z], nodes[x + 1, z], z % 2 == 0 ? RoadType.Avenue2x2 : RoadType.TwoLane, 14f);
                if (z + 1 < size) net.AddRoad(nodes[x, z], nodes[x, z + 1], RoadType.TwoLane, 14f);
            }
        return net;
    }

    static void BuyTrains(Simulation sim, RailTownWorlds.Scenario sc, int count)
    {
        sim.Company.Money += 20_000_000;   // the benchmark is about step time, not about what the player can afford
        for (int k = 0; k < count; k++)
        {
            int depot = k % 2 == 0 ? sc.DepotA : sc.DepotB, id = -1;
            for (int tries = 0; tries < 100 && id < 0; tries++)
            {
                id = sim.Execute(new BuyTrainCommand(depot, "diesel", k % 3 == 0 ? "freight" : "coach", 4, sc.Line));
                if (id < 0) RailWorlds.Run(sim, 0.5);
            }
            Assert.True(id >= 0, "train bought");
        }
    }

    [Fact]
    public void ThousandVehiclesOnAGrid()
    {
        if (!On) return;
        var sim = new Simulation(Grid(12, 200f), seed: 5);
        Assert.Equal(1000, sim.SeedTraffic(1000));

        InBudget(Measure("1000 vehicles on a grid", sim, warmup: 200, steps: 2000), 2.0);
    }

    [Fact]
    public void TwentyTrainsOnParallelLines()
    {
        if (!On) return;
        var sim = RailWorlds.Flat(3);
        sim.Company.Money += 50_000_000;
        for (int k = 0; k < 20; k++)
        {
            float z = k * 40f;
            Assert.True(sim.Execute(new BuildTrackCommand(0, z, 300, z)) > 0 && sim.Execute(new BuildTrackCommand(300, z, 1500, z)) > 0, "track");
            int a = sim.Execute(new BuildRailStationCommand(100, z)), b = sim.Execute(new BuildRailStationCommand(1400, z));
            int depot = sim.Execute(new AddRailDepotCommand(300, z));
            int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
            Assert.True(sim.Execute(new BuyTrainCommand(depot, "diesel", k % 3 == 0 ? "freight" : "coach", 8, line)) >= 0, "train");
        }
        RailWorlds.Run(sim, 120);   // all of them out of their depots and under way
        Assert.DoesNotContain(sim.Trains, t => t.Status == TrainStatus.InDepot);

        InBudget(Measure("20 trains of 8 wagons on separate lines", sim, warmup: 0, steps: 6000), 0.1);
    }

    [Fact]
    public void AMixedMapDuringARushDay()
    {
        if (!On) return;
        var sc = RailTownWorlds.Find(2);
        var sim = sc.Sim;
        BuyTrains(sim, sc, 6);
        var (i, j) = ClosestPair(sim);
        var sa = StationsNear(sim, sim.Towns[i].X, sim.Towns[i].Z, 3);
        var sb = StationsNear(sim, sim.Towns[j].X, sim.Towns[j].Z, 3);
        int busDepot = Depot(sim, sim.Towns[i]);
        int busLine = sim.Execute(new CreateLineCommand(sa.Concat(Enumerable.Reverse(sb)).ToArray()));
        for (int k = 0; k < 8; k++) Buy(sim, busDepot, "bus", busLine);
        Run(sim, 1.5);   // the second day: groups have their schedules, the rush has built up

        InBudget(Measure("towns, rush traffic, 8 buses and 6 trains (one day)", sim, warmup: 0, steps: (int)(sim.DayLengthSeconds / Simulation.StepSeconds)), 0.15);
    }
}
