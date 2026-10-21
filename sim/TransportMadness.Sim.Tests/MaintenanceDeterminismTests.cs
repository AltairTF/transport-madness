using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Wear, breakdowns and repairs are as repeatable as the rest: the same seed and commands give the same run, and the command log replays it.</summary>
public class MaintenanceDeterminismTests
{
    const ulong MaintenanceGoldenMac = 0x3BBFFDDB1FC34F61UL;
    const ulong MaintenanceGoldenLinux = 0x3BBFFDDB1FC34F61UL;

    static Tuning Worn() =>
        Tuning.Load() with { Maintenance = Tuning.Load().Maintenance with { MetresPerPoint = 300f, Roads = Tuning.Load().Maintenance.Roads with { PassesPerPoint = 40f } } };

    /// <summary>
    /// A bus line and a train line with wear fast enough that vehicles reach their floor, are serviced, patched and sometimes break
    /// down within the run; policies at every level and a manual repair, all by commands.
    /// </summary>
    static Simulation Scenario(Tuning? tuning = null)
    {
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Worlds.Late(tuning ?? Worn()));
        var sim = new Simulation(net, 17, IncidentFrequency.High);
        sim.Execute(new TakeLoanCommand(1000000));
        sim.Execute(new BuildRoadCommand(-200, 0, 1000, 0, RoadType.Avenue2x2, 19f));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(-200, 0, 5f, withRoadsOnly: true), DepotLevel.Complex));
        int a = sim.Execute(new BuildStationCommand(0, 0)), b = sim.Execute(new BuildStationCommand(800, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        int first = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        int neglected = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
        sim.Execute(new BuyVehicleCommand(depot, "truck", line));
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, neglected, MaintenancePolicy.Off));
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Line, line, MaintenancePolicy.When(5, 50, RepairKind.Service)));
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, first, MaintenancePolicy.When(0, 35, RepairKind.Patch)));
        sim.Execute(new BuildTrackCommand(0, 600, 300, 600));
        sim.Execute(new BuildTrackCommand(300, 600, 1500, 600));
        int rs = sim.Execute(new BuildRailStationCommand(100, 600)), re = sim.Execute(new BuildRailStationCommand(1400, 600));
        int railDepot = sim.Execute(new AddRailDepotCommand(300, 600, DepotLevel.Workshop));
        int railLine = sim.Execute(new CreateLineCommand(new[] { rs, re }));
        sim.Execute(new BuyTrainCommand(railDepot, "diesel", "coach", 3, railLine));
        sim.Execute(new BuildSignalCommand(700, 600, SignalKind.Block, Forward: true));
        sim.Execute(new BuyParkedVehicleCommand(depot, "bus"));
        sim.Execute(new RequestRepairCommand(RepairSubject.Parked, 0, RepairKind.Overhaul));
        MaintenanceWorlds.Run(sim, 900);
        sim.Execute(new RequestRepairCommand(RepairSubject.Vehicle, first, RepairKind.Service));
        sim.Execute(new SetFrequencyCommand(IncidentFrequency.Normal));
        MaintenanceWorlds.Run(sim, 2100);
        sim.Execute(new ResurfaceRoadCommand(0));
        MaintenanceWorlds.Run(sim, 1000);
        return sim;
    }

    [Fact]
    public void TwoRunsAgree_TheScenarioReallyRepairsAndBreaksThings_AndTheLogReplaysIt()
    {
        var a = Scenario();
        var b = Scenario();

        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.True(a.Company.Total(LedgerEntry.Maintenance) < 0, "nothing was ever repaired");
        Assert.True(a.Events.Any(e => e.Kind is SimEventKind.Breakdown or SimEventKind.TrainBreakdown), "nothing ever broke down");

        var fresh = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Worlds.Late(Worn())), 17, IncidentFrequency.High);
        fresh.Replay(a.CommandLog, a.Tick);
        Assert.Equal(a.StateHash(), fresh.StateHash());
    }

    [Fact]
    public void ADifferentFrequency_GivesADifferentRun()
    {
        var normal = Scenario();
        var off = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Worlds.Late(Worn())), 17, IncidentFrequency.Off);
        off.Replay(normal.CommandLog.Where(c => c.Command is not SetFrequencyCommand).ToList(), normal.Tick);

        Assert.NotEqual(normal.StateHash(), off.StateHash());
        Assert.DoesNotContain(off.Events, e => e.Kind is SimEventKind.Breakdown or SimEventKind.TrainBreakdown);
    }

    [Fact]
    public void TheMaintenanceScenarioHashIsPinned()
    {
        ulong hash = Scenario().StateHash();
        ulong? pinned = OperatingSystem.IsMacOS() ? MaintenanceGoldenMac : OperatingSystem.IsLinux() ? MaintenanceGoldenLinux : null;
        if (pinned == null) return; // no pinned value for this system (Windows): the two-runs test above is the check

        Assert.True(hash == pinned, $"maintenance state hash changed; if intended, set {(OperatingSystem.IsMacOS() ? "MaintenanceGoldenMac" : "MaintenanceGoldenLinux")} = 0x{hash:X16}UL (the other system's value needs its own check)");
    }
}
