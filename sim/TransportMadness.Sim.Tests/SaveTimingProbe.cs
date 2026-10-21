using System.Diagnostics;
using TransportMadness.Sim;
using Xunit.Abstractions;

namespace TransportMadness.Sim.Tests;

/// <summary>
/// How long a save takes on a big world (M6a target: the part on the main thread, <see cref="SaveGame.Capture"/>, under 0.3 s). Runs only
/// with SAVE_PROBE set: <c>SAVE_PROBE=1 dotnet test -c Release --filter SaveTimingProbe --logger "console;verbosity=detailed"</c>.
/// SAVE_TOWNS (8), SAVE_CARS (1000) and SAVE_MONTHS (2) change the world.
/// </summary>
public class SaveTimingProbe(ITestOutputHelper output)
{
    static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    [Fact]
    public void Probe()
    {
        if (Environment.GetEnvironmentVariable("SAVE_PROBE") == null) return;
        var t = Worlds.Late(Tuning.Load());
        var (sim, _) = WorldBuilder.Towns(5, Env("SAVE_TOWNS", 8), IndustryAmount.Many, t);
        sim.Execute(new TakeLoanCommand(5000000));
        var towns = sim.Towns.ToList();
        int lines = 0, vehicles = 0;
        for (int k = 0; k + 1 < towns.Count; k++)
        {
            if (!Worlds.Connect(sim, towns[k], towns[k + 1])) continue;
            var sa = Worlds.StationsNear(sim, towns[k].X, towns[k].Z, 3);
            var sb = Worlds.StationsNear(sim, towns[k + 1].X, towns[k + 1].Z, 3);
            int depot = Worlds.Depot(sim, towns[k]);
            int line = sim.Execute(new CreateLineCommand(sa.Concat(Enumerable.Reverse(sb)).ToArray()));
            if (line < 0 || depot < 0) continue;
            lines++;
            for (int b = 0; b < 6; b++) if (Worlds.Buy(sim, depot, "bus", line) >= 0) vehicles++;
        }
        sim.Execute(new SeedTrafficCommand(Env("SAVE_CARS", 1000)));
        var run = Stopwatch.StartNew();
        Worlds.Run(sim, Env("SAVE_MONTHS", 2) * t.Economy.DaysPerMonth);
        output.WriteLine($"world: {sim.Towns.Count} towns, {sim.Buildings.Count} buildings, {sim.Industries.Count} industries, {sim.TripGroups.Count} groups, {lines} lines, {vehicles} buses, {sim.VehicleCount} vehicle slots; ran in {run.Elapsed.TotalSeconds:F0} s");

        SaveGame.Capture(sim); // warm the layouts
        var watch = Stopwatch.StartNew();
        byte[] snapshot = Array.Empty<byte>();
        for (int k = 0; k < 3; k++) snapshot = SaveGame.Capture(sim);
        double capture = watch.Elapsed.TotalMilliseconds / 3;
        watch.Restart();
        using var file = new MemoryStream();
        SaveGame.Write(file, SaveGame.InfoOf(sim, "probe", DateTime.UtcNow), Array.Empty<byte>(), snapshot);
        double write = watch.Elapsed.TotalMilliseconds;
        file.Position = 0;
        watch.Restart();
        var loaded = SaveGame.Load(file).Sim;
        double load = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"capture {capture:F0} ms (main thread), compress and write {write:F0} ms, load {load:F0} ms; snapshot {snapshot.Length / 1024} KiB, file {file.Length / 1024} KiB");
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }
}
