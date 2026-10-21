using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Line stops at industries (M6f): a vehicle loads and unloads at an industry's access point, with load, cargo, unload and share options per stop.</summary>
public class DockStopTests
{
    const int Forest = 0, Sawmill = 3;
    const int Wood = 2, Planks = 3;

    static Terrain Flat() => Terrain.Create(512, 8f, (_, _) => 5f);

    /// <summary>A forest at x 500 and two sawmills at x 1200 and 1900, 40 m off a road along x; their access points face east (no towns). The road is built after them, so each gets a Path.</summary>
    internal static (Simulation Sim, int Depot, int[] Docks) World(bool withRoad = true)
    {
        var sim = new Simulation(new RoadNetwork(Flat()), 4, IncidentFrequency.Off);
        sim.AddTowns(new WorldPlan(1, Array.Empty<TownPlan>(), new[]
        {
            new IndustryPlan(Forest, 500, 40, 0f), new IndustryPlan(Sawmill, 1200, 40, 0f), new IndustryPlan(Sawmill, 1900, 40, 0f),
        }));
        sim.Company.Money = 50_000_000;
        if (!withRoad) return (sim, -1, Array.Empty<int>());
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 2600, 0, RoadType.TwoLane, 14f)));
        int depot = sim.Execute(new AddDepotCommand(sim.Network.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        Assert.True(depot >= 0);
        var docks = new[] { sim.Execute(new AddDockCommand(false, 0)), sim.Execute(new AddDockCommand(false, 1)), sim.Execute(new AddDockCommand(false, 2)) };
        Assert.All(docks, d => Assert.True(d >= 0));
        return (sim, depot, docks);
    }

    static void Run(Simulation sim, int seconds)
    {
        for (long t = 0; t < seconds / Simulation.StepSeconds; t++) sim.Step();
    }

    [Fact]
    public void ADockNeedsARoadAtTheAccessPoint_AndIsFoundAgain()
    {
        var (sim, _, _) = World(withRoad: false);
        Assert.Equal(-1, sim.Execute(new AddDockCommand(false, 0)));
        Assert.Equal(-1, sim.Execute(new AddDockCommand(false, 9)));
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 2600, 0, RoadType.TwoLane, 14f)));
        int dock = sim.Execute(new AddDockCommand(false, 0));
        Assert.True(dock >= 0);
        Assert.Equal(dock, sim.Execute(new AddDockCommand(false, 0)));
        Assert.Equal(1, sim.Stations[dock].DockKind);
        Assert.Equal(sim.AccessNodeOf(sim.Industries[0]), sim.Stations[dock].Node);
        Assert.Equal(new[] { 0 }, sim.IndustriesCoveredBy(sim.Stations[dock]));
    }

    [Fact]
    public void ALineBetweenTwoIndustries_CarriesTheirCargoWithoutAnyStation()
    {
        var (sim, depot, docks) = World();
        int line = sim.Execute(new CreateLineCommand(new[] { docks[0], docks[1] }));
        Assert.True(line >= 0);
        Assert.True(sim.Execute(new BuyVehicleCommand(depot, "truck", line)) >= 0);
        Run(sim, 20 * (int)sim.DayLengthSeconds);
        Assert.True(sim.Industries[0].Produced > 0.0, "the forest made wood");
        Assert.True(sim.Lines[line].Delivered > 0, $"the line delivered {sim.Lines[line].Delivered}");
        Assert.True(sim.Industries[1].Produced > 0.0, "the sawmill made planks from the wood it was given");
        Assert.True(sim.Lines[line].Income > 0);
    }

    [Fact]
    public void AnUnloadShare_PutsDownThatPartOfWhatWasLoaded_AndTheRestGoesOn()
    {
        var (sim, depot, docks) = World();
        int line = sim.Execute(new CreateLineCommand(new[] { docks[0], docks[1], docks[2] }));
        Assert.Equal(1, sim.Execute(new SetStopOptionsCommand(line, 1, false, -1, true, 50)));
        Assert.Equal(1, sim.Execute(new SetStopOptionsCommand(line, 2, false, -1, true, -1)));
        int vehicle = sim.Execute(new BuyVehicleCommand(depot, "truck", line));
        Assert.True(vehicle >= 0);

        // What is on board at each stop once it has loaded or unloaded there, over the first round.
        int loadedAtForest = 0, afterFirst = -1, afterSecond = -1;
        for (long t = 0; t < 40 * sim.DayLengthSeconds / Simulation.StepSeconds && afterSecond < 0; t++)
        {
            sim.Step();
            var info = sim.GetFleetInfo(vehicle);
            if (!info.Loading) continue;
            if (info.NextStop == 0 && info.Load > 0 && loadedAtForest == 0) loadedAtForest = info.Load;
            if (info.NextStop == 1 && loadedAtForest > 0 && afterFirst < 0) afterFirst = info.Load;
            if (info.NextStop == 2 && afterFirst >= 0 && afterSecond < 0) afterSecond = info.Load;
        }
        Assert.True(loadedAtForest > 1, "it loaded wood at the forest");
        Assert.Equal(loadedAtForest - (int)MathF.Round(loadedAtForest * 0.5f), afterFirst); // half put down at the first sawmill
        Assert.Equal(0, afterSecond); // the rest put down at the second, and nothing loaded there
        Assert.True(sim.Industries[1].Input[Wood] + sim.Industries[1].Produced > 0.0);
        Assert.True(sim.Industries[2].Input[Wood] + sim.Industries[2].Produced > 0.0);
    }

    [Fact]
    public void WithLoadingSwitchedOffAtAStop_NothingIsTakenThere()
    {
        var (sim, depot, docks) = World();
        int line = sim.Execute(new CreateLineCommand(new[] { docks[0], docks[1] }));
        Assert.Equal(1, sim.Execute(new SetStopOptionsCommand(line, 0, false, -1, true, -1)));
        int vehicle = sim.Execute(new BuyVehicleCommand(depot, "truck", line));
        Run(sim, 10 * (int)sim.DayLengthSeconds);
        Assert.Equal(0, sim.Lines[line].Delivered);
        Assert.Equal(0, sim.GetFleetInfo(vehicle).Load);
    }

    [Fact]
    public void ALoadCargoOption_TakesOnlyThatCargo()
    {
        var (sim, depot, docks) = World();
        int line = sim.Execute(new CreateLineCommand(new[] { docks[0], docks[1] }));
        Assert.Equal(0, sim.Execute(new SetStopOptionsCommand(line, 0, true, 99, true, -1))); // no such cargo
        Assert.Equal(0, sim.Execute(new SetStopOptionsCommand(line, 5, true, -1, true, -1))); // no such stop
        Assert.Equal(0, sim.Execute(new SetStopOptionsCommand(line, 0, true, -1, true, 101)));
        Assert.Equal(1, sim.Execute(new SetStopOptionsCommand(line, 0, true, Planks, true, -1))); // the forest makes wood, not planks
        int vehicle = sim.Execute(new BuyVehicleCommand(depot, "truck", line));
        Run(sim, 10 * (int)sim.DayLengthSeconds);
        Assert.Equal(0, sim.Lines[line].Delivered);
        Assert.Equal(0, sim.GetFleetInfo(vehicle).Load);
    }

    [Fact]
    public void ALegBetweenTwoIndustries_IsTheRoadRouteWithItsLengthTimeAndCargo()
    {
        var (sim, _, _) = World();
        var leg = sim.RouteBetween(StopKind.Industry, 0, StopKind.Industry, 1);
        Assert.NotNull(leg);
        Assert.True(leg!.Reachable);
        Assert.InRange(leg.Length, 650f, 800f); // from the forest's access point (533) to the sawmill's (1225), by the road
        Assert.True(leg.Seconds > 30f);
        Assert.True(leg.Points.Count > 10);
        Assert.Equal(new[] { Wood }, leg.Cargo); // the forest makes wood and the sawmill takes it
        Assert.Empty(sim.RouteBetween(StopKind.Industry, 1, StopKind.Industry, 2)!.Cargo); // a sawmill makes planks, which another sawmill does not take
    }

    [Fact]
    public void ALegToAnIndustryNoRoadReaches_IsNotRouted()
    {
        var (sim, _, _) = World(withRoad: false);
        Assert.Null(sim.RouteBetween(StopKind.Industry, 0, StopKind.Industry, 1));
    }

    [Fact]
    public void EditingALine_KeepsTheOptionsOfTheStopsItKeeps()
    {
        var (sim, _, docks) = World();
        int line = sim.Execute(new CreateLineCommand(new[] { docks[0], docks[1], docks[2] }));
        Assert.Equal(1, sim.Execute(new SetStopOptionsCommand(line, 1, false, -1, true, 30)));
        Assert.Equal(1, sim.Execute(new EditLineCommand(line, new[] { docks[1], docks[2], docks[0] })));
        Assert.Equal(new StopOptions(false, -1, true, 30), sim.Lines[line].OptionsAt(0));
        Assert.True(sim.Lines[line].OptionsAt(1).IsDefault);
        Assert.True(sim.Lines[line].OptionsAt(2).IsDefault);
    }

    [Fact]
    public void TheSameCommandsWithOptions_GiveTheSameWorld_AndTheLogReplays()
    {
        static Simulation Run1()
        {
            var (sim, depot, docks) = World();
            int line = sim.Execute(new CreateLineCommand(new[] { docks[0], docks[1], docks[2] }));
            sim.Execute(new SetStopOptionsCommand(line, 1, false, -1, true, 50));
            sim.Execute(new BuyVehicleCommand(depot, "truck", line));
            Run(sim, 8 * (int)sim.DayLengthSeconds);
            return sim;
        }
        var a = Run1();
        Assert.Equal(a.StateHash(), Run1().StateHash());
        var (fresh, _, _) = World(withRoad: false);
        fresh.Company.Money = 50_000_000;
        fresh.Replay(a.CommandLog, a.Tick);
        Assert.Equal(a.StateHash(), fresh.StateHash());
    }
}
