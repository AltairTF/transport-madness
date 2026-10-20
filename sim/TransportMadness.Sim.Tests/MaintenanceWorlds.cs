using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Small worlds for the maintenance tests: a bus line on a straight road, a train line on a straight track, and a depot of the level a test needs.</summary>
internal static class MaintenanceWorlds
{
    /// <summary>
    /// The shipped tuning with the maintenance numbers the unit tests were written against (slow wear, the first prices and risks of the plan), so that they
    /// do not move when the balance does; <see cref="MaintenanceEconomyTests"/> are the ones that use the shipped numbers.
    /// </summary>
    internal static Tuning Calm()
    {
        var tuning = Tuning.Load();
        var m = tuning.Maintenance;
        return tuning with
        {
            Maintenance = m with
            {
                MetresPerPoint = 12000f,
                HazardPerKmAtFloor = 0.05f,
                HazardPerKmAtZero = 0.5f,
                Patch = m.Patch with { PriceShare = 0.02f },
                Service = m.Service with { PriceShare = 0.06f },
                Overhaul = m.Overhaul with { PriceShare = 0.25f },
            },
        };
    }

    /// <summary>The calm tuning with the maintenance section changed by <paramref name="change"/>.</summary>
    internal static Tuning Tuned(Func<MaintenanceTuning, MaintenanceTuning> change)
    {
        var tuning = Calm();
        return tuning with { Maintenance = change(tuning.Maintenance) };
    }

    internal sealed record RoadLine(Simulation Sim, int Depot, int A, int B, int Line);

    /// <summary>A straight road 1200 m long with a depot of the given level at its west end, stations at x = 150 and x = 1050, and a line between them. No random breakdowns unless asked for.</summary>
    internal static RoadLine Road(DepotLevel level = DepotLevel.Workshop, Tuning? tuning = null, IncidentFrequency frequency = IncidentFrequency.Off)
    {
        var net = new RoadNetwork(Terrain.Flat(), tuning ?? Calm());
        var sim = new Simulation(net, 5, frequency);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f)));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), level));
        int a = sim.Execute(new BuildStationCommand(150, 0));
        int b = sim.Execute(new BuildStationCommand(1050, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.True(depot >= 0 && a >= 0 && b >= 0 && line >= 0);
        return new RoadLine(sim, depot, a, b, line);
    }

    internal static int Bus(RoadLine w)
    {
        int bus = w.Sim.Execute(new BuyVehicleCommand(w.Depot, "bus", w.Line));
        Assert.True(bus >= 0);
        return bus;
    }

    internal static void Run(Simulation sim, double seconds)
    {
        long ticks = (long)Math.Round(seconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    /// <summary>Steps until <paramref name="done"/> holds, at most <paramref name="seconds"/> seconds; returns whether it did.</summary>
    internal static bool RunUntil(Simulation sim, double seconds, Func<bool> done)
    {
        long ticks = (long)Math.Round(seconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++)
        {
            if (done()) return true;
            sim.Step();
        }
        return done();
    }

    internal sealed record RailLine(Simulation Sim, int A, int B, int Line, int Depot);

    /// <summary>Track from x = 0 to x = 1500 with a depot of the given level at 300, stations at 100 and 1400, and a line between the stations.</summary>
    internal static RailLine Rail(DepotLevel level = DepotLevel.Workshop, Tuning? tuning = null, IncidentFrequency frequency = IncidentFrequency.Off)
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning ?? Calm()), 1, frequency);
        Assert.True(sim.Execute(new BuildTrackCommand(0, 0, 300, 0)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(300, 0, 1500, 0)) > 0);
        int a = sim.Execute(new BuildRailStationCommand(100, 0));
        int b = sim.Execute(new BuildRailStationCommand(1400, 0));
        int depot = sim.Execute(new AddRailDepotCommand(300, 0, level));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.True(a >= 0 && b >= 0 && depot >= 0 && line >= 0);
        return new RailLine(sim, a, b, line, depot);
    }

    internal static Train Train(RailLine w)
    {
        int id = w.Sim.Execute(new BuyTrainCommand(w.Depot, "diesel", "coach", 3, w.Line));
        Assert.True(id >= 0);
        return w.Sim.Trains[id];
    }
}
