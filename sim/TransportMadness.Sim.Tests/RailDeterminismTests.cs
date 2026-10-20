using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Rail runs are as repeatable as road ones: a pinned hash of a scenario built only from commands (like the road golden hash, one value per system).</summary>
public class RailDeterminismTests
{
    const ulong RailGoldenMac = 0xFC2176B37CC8EA48UL;
    const ulong RailGoldenLinux = 0xFC2176B37CC8EA48UL;

    /// <summary>
    /// A single railway with a passing loop and path signals, a road across it with a level crossing, two trains from opposite ends,
    /// a bus line and ambient traffic, built entirely by commands and run for twenty minutes.
    /// </summary>
    static Simulation Scenario()
    {
        var sim = RailWorlds.Flat(11);
        sim.Execute(new TakeLoanCommand(1000000));
        sim.Execute(new BuildTrackCommand(0, 0, 900, 0));
        sim.Execute(new BuildTrackCommand(900, 0, 1500, 0));
        sim.Execute(new BuildTrackCommand(1500, 0, 2400, 0));
        sim.Execute(new BuildTrackCommand(900, 0, 960, 60));
        sim.Execute(new BuildTrackCommand(960, 60, 1440, 60));
        sim.Execute(new BuildTrackCommand(1440, 60, 1500, 0));
        sim.Execute(new BuildRoadCommand(500, -300, 500, 300, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(-200, -300, 500, -300, RoadType.TwoLane, 14f));
        int a = sim.Execute(new BuildRailStationCommand(150, 0));
        int b = sim.Execute(new BuildRailStationCommand(2250, 0));
        int depotA = sim.Execute(new AddRailDepotCommand(0, 0));
        int depotB = sim.Execute(new AddRailDepotCommand(2400, 0));
        foreach (var (x, z) in new[] { (890f, 0f), (910f, 0f), (907f, 7f), (1510f, 0f), (1490f, 0f), (1493f, 7f) })
            sim.Execute(new BuildSignalCommand(x, z, SignalKind.Path));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        sim.Execute(new BuyTrainCommand(depotA, "diesel", "coach", 3, line));
        sim.Execute(new BuyTrainCommand(depotB, "diesel", "freight", 3, line));
        int busDepot = sim.Execute(new AddDepotCommand(sim.Network.FindNodeNear(500, -300, 10f, withRoadsOnly: true), DepotLevel.Simple));
        int s0 = sim.Execute(new BuildStationCommand(500, -150)), s1 = sim.Execute(new BuildStationCommand(500, 150));
        int busLine = sim.Execute(new CreateLineCommand(new[] { s0, s1 }));
        sim.Execute(new BuyVehicleCommand(busDepot, "bus", busLine));
        sim.Execute(new SeedTrafficCommand(25));
        RailWorlds.Run(sim, 1200);
        return sim;
    }

    [Fact]
    public void TwoRunsAgree_AndTheCommandLogReplaysTheRun()
    {
        var a = Scenario();
        var b = Scenario();
        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.NotEmpty(a.Crossings);
        Assert.All(a.Trains, t => Assert.True(t.Income > 0 || t.Load > 0 || t.Status != TrainStatus.NoRoute));

        var fresh = RailWorlds.Flat(11);
        fresh.Replay(a.CommandLog, a.Tick);
        Assert.Equal(a.StateHash(), fresh.StateHash());
    }

    [Fact]
    public void TheRailScenarioHashIsPinned()
    {
        ulong hash = Scenario().StateHash();

        ulong? pinned = OperatingSystem.IsMacOS() ? RailGoldenMac : OperatingSystem.IsLinux() ? RailGoldenLinux : null;
        if (pinned == null) return; // no pinned value for this system (Windows): the two-runs test above is the check
        Assert.True(pinned == hash, $"rail state hash changed; if intended, set {(OperatingSystem.IsMacOS() ? "RailGoldenMac" : "RailGoldenLinux")} = 0x{hash:X16}UL (the other system's value needs its own check)");
    }
}
