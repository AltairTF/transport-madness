using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>
/// ADR 0003: the same seed and the same player actions give the same run. The scenario builds its whole world through
/// commands, so the command log alone (plus the seed) reproduces it, and it exercises building, upgrades, removal,
/// depots, crashes, crews and overtaking, not just seeded traffic.
/// </summary>
public class DeterminismTests
{
    const int Ticks = 12000; // ten minutes

    /// <summary>
    /// Pinned state hash of <see cref="Run"/> with seed 7. It changes whenever behaviour does (tuning, manoeuvre rules,
    /// a new field in the hash): after checking that the change was intended, copy the value from the failure message.
    /// It also guards against nondeterminism creeping in, such as iteration order or wall-clock use.
    /// </summary>
    const ulong GoldenHash = 0xA030F9330AAB6516UL;

    sealed record Result(Simulation Sim, int Overtakes);

    /// <summary>A 6 x 6 grid of 300 m two-lane roads built by command, then edited, with depots, traffic and crashes along the way.</summary>
    static Result Run(ulong seed, int ticks = Ticks)
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Flat()), seed);
        const int size = 6;
        const float spacing = 300f, origin = -750f;
        for (int x = 0; x < size; x++)
            for (int z = 0; z < size; z++)
            {
                float px = origin + x * spacing, pz = origin + z * spacing;
                if (x + 1 < size) Assert.Equal(1, sim.Execute(new BuildRoadCommand(px, pz, px + spacing, pz, RoadType.TwoLane, 14f)));
                if (z + 1 < size) Assert.Equal(1, sim.Execute(new BuildRoadCommand(px, pz, px, pz + spacing, RoadType.TwoLane, 14f)));
            }
        Assert.Equal(1, sim.Execute(new UpgradeRoadCommand(0, 19f)));
        Assert.Equal(1, sim.Execute(new RemoveRoadCommand(7)));
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(origin, origin, origin + 250f, origin - 250f, RoadType.TwoLane, 14f, 0.6f)));
        Assert.Equal(0, sim.Execute(new AddDepotCommand(9, DepotLevel.Workshop)));
        Assert.Equal(1, sim.Execute(new AddDepotCommand(25, DepotLevel.Simple)));
        Assert.Equal(-1, sim.Execute(new AddDepotCommand(9, DepotLevel.Complex))); // one depot per node
        Assert.True(sim.Execute(new SeedTrafficCommand(600)) > 500);

        int overtakes = 0;
        var wasOut = new bool[4096];
        while (sim.Tick < ticks)
        {
            if (sim.Tick == 1500 || sim.Tick == 7000) sim.Execute(new CrashSomeoneCommand());
            if (sim.Tick == 4000) sim.Execute(new SeedTrafficCommand(60));
            if (sim.Tick == 5000) sim.Execute(new BuildRoadCommand(origin + 150f, origin - 100f, origin + 150f, origin + 600f, RoadType.TwoLane, 14f));
            sim.Step();
            if (sim.VehicleCount > wasOut.Length) Array.Resize(ref wasOut, sim.VehicleCount * 2);
            for (int i = 0; i < sim.VehicleCount; i++)
            {
                bool out_ = sim.GetVehicle(i).Overtake != OvertakePhase.None;
                if (out_ && !wasOut[i]) overtakes++;
                wasOut[i] = out_;
            }
        }
        return new Result(sim, overtakes);
    }

    [Fact]
    public void TwoRuns_WithTheSameSeedAndActions_EndInIdenticalState()
    {
        var first = Run(seed: 7);
        var second = Run(seed: 7);

        Assert.Equal(first.Sim.StateHash(), second.Sim.StateHash());
        Assert.Equal(first.Overtakes, second.Overtakes);
    }

    [Fact]
    public void ReplayingTheCommandLogOnAFreshSimulation_ReproducesTheRun()
    {
        var original = Run(seed: 7).Sim;

        var replayed = new Simulation(new RoadNetwork(Terrain.Flat()), 7);
        replayed.Replay(original.CommandLog, original.Tick);

        Assert.Equal(original.Tick, replayed.Tick);
        Assert.Equal(original.CommandLog, replayed.CommandLog);
        Assert.Equal(original.StateHash(), replayed.StateHash());
    }

    [Fact]
    public void ScenarioActuallyExercisesCrashesCrewsAndOvertaking()
    {
        var result = Run(seed: 7);
        var sim = result.Sim;

        Assert.True(result.Overtakes >= 3, $"{result.Overtakes} overtakes");
        Assert.Contains(sim.Incidents, i => i.Kind == IncidentKind.Breakdown); // the commanded crash
        Assert.Contains(sim.Incidents, i => i.State == IncidentState.Cleared && i.Depot >= 0); // a depot crew cleared it
        Assert.True(sim.Network.LaneCount > 0 && sim.Depots.Count == 2);
    }

    [Fact]
    public void StateHash_CoversWhatCommandsChange()
    {
        var baseline = Run(seed: 7, ticks: 200).Sim.StateHash();

        var sim = new Simulation(new RoadNetwork(Terrain.Flat()), 7);
        sim.Replay(Run(seed: 7, ticks: 200).Sim.CommandLog.Where(c => c.Command is not AddDepotCommand).ToList(), 200);

        Assert.NotEqual(baseline, sim.StateHash()); // without its depots the world differs
    }

    [Fact]
    public void RunWithSeed7_MatchesThePinnedHash()
    {
        var hash = Run(seed: 7).Sim.StateHash();

        Assert.True(GoldenHash == hash, $"state hash changed; if intended, set GoldenHash = 0x{hash:X16}UL");
    }

    const ulong AvenueGoldenHash = 0x29979B015FEA3BF3UL;

    /// <summary>Two avenues crossing a two-lane road with dense traffic: lane changes, which <see cref="Run"/> never triggers.</summary>
    static (Simulation Sim, int ShiftingSteps) RunAvenues(ulong seed)
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Flat()), seed);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(-600, 0, 600, 0, RoadType.Avenue2x2, 16f)));
        Assert.True(sim.Execute(new BuildRoadCommand(0, -600, 0, 600, RoadType.Avenue2x2, 16f)) >= 1); // splits the first avenue
        Assert.True(sim.Execute(new BuildRoadCommand(-600, -300, 600, -300, RoadType.TwoLane, 14f)) >= 1);
        Assert.True(sim.Execute(new SeedTrafficCommand(150)) > 100);
        int shiftingSteps = 0;
        while (sim.Tick < 6000)
        {
            sim.Step();
            for (int i = 0; i < sim.VehicleCount; i++)
                if (sim.GetVehicle(i).Shift != 0f) shiftingSteps++; // a vehicle still settling after a lane change
        }
        return (sim, shiftingSteps);
    }

    [Fact]
    public void AvenueRun_ChangesLanesAndMatchesThePinnedHash()
    {
        var (sim, shiftingSteps) = RunAvenues(seed: 3);
        var hash = sim.StateHash();

        Assert.True(shiftingSteps >= 10, $"{shiftingSteps} vehicle-steps settling after a lane change");
        Assert.Equal(hash, RunAvenues(seed: 3).Sim.StateHash());
        Assert.True(AvenueGoldenHash == hash, $"state hash changed; if intended, set AvenueGoldenHash = 0x{hash:X16}UL");
    }

    [Fact]
    public void Execute_LogsEveryCommandWithTheTickItRanAt_RefusedOnesIncluded()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Flat()), 1);
        sim.Execute(new BuildRoadCommand(0, 0, 100, 0, RoadType.TwoLane, 14f));
        for (int i = 0; i < 5; i++) sim.Step();
        int refused = sim.Execute(new BuildRoadCommand(0, 0, 3, 0, RoadType.TwoLane, 14f)); // shorter than the minimum length

        Assert.Equal(0, refused);
        Assert.Equal(2, sim.CommandLog.Count);
        Assert.Equal(0, sim.CommandLog[0].Tick);
        Assert.Equal(5, sim.CommandLog[1].Tick);
    }

    [Fact]
    public void Replay_RejectsALogThatStartsBeforeTheCurrentTick()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Flat()), 1);
        for (int i = 0; i < 10; i++) sim.Step();
        var log = new[] { new LoggedCommand(3, new SeedTrafficCommand(1)) };

        Assert.Throws<ArgumentException>(() => sim.Replay(log, 20));
    }
}
