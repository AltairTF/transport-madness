using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Growth of towns: connectivity, the gates, what a full construction stock buys, and where growth appears.</summary>
public class GrowthTests
{
    const int Materials = 5, Planks = 3;

    static Terrain DryLevelMap() => Terrain.Create(512, 8f, (_, _) => 5f);

    /// <summary>A short day (60 s) so that months go by quickly; growth does not depend on the length of the day.</summary>
    static Tuning Quick(float minConnectivity = 0f)
    {
        var t = Tuning.Load();
        return t with
        {
            Economy = t.Economy with { DayLength = 60f },
            Towns = t.Towns with { Growth = t.Towns.Growth with { MinConnectivity = minConnectivity } },
        };
    }

    static void RunMonths(Simulation sim, double months)
    {
        long ticks = (long)Math.Round(months * sim.Network.Tuning.Economy.DaysPerMonth * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static void Fill(Simulation sim, int building)
    {
        var b = sim.Buildings[building];
        foreach (var a in sim.TownTuning.Of(b.Type, b.Tier).Construction)
            for (int c = 0; c < sim.CargoTypes.Count; c++)
                if (sim.CargoTypes[c].Id == a.Cargo) b.Construction[c] = a.Amount;
    }

    static bool HasStock(Simulation sim, int building) => sim.Buildings[building].Construction.Any(x => x > 0f);

    /// <summary>
    /// One square block of streets, 120 m a side, round (0, 0), with its eight lots. <paramref name="occupants"/> puts a
    /// building (type, size) on the lots in order; null leaves a lot free.
    /// </summary>
    static (Simulation Sim, IReadOnlyList<TownGenerator.LotSite> Lots) BlockTown(Tuning tuning, params (BuildingType Type, int Tier)?[] occupants)
    {
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, 7);
        foreach (var (x0, z0, x1, z1) in new[] { (-60f, -60f, 60f, -60f), (60f, -60f, 60f, 60f), (60f, 60f, -60f, 60f), (-60f, 60f, -60f, -60f) })
            sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadType.TwoLane, 14f));
        var lots = TownGenerator.LotSites(tuning.Towns, 0, 0, 0, 1, 0, 0, 0, 0).ToList();
        var plans = new List<BuildingPlan>();
        for (int k = 0; k < occupants.Length; k++)
            if (occupants[k] is { } o) plans.Add(new BuildingPlan(o.Type, o.Tier, lots[k].X, lots[k].Z, lots[k].Facing));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", 0, 0, 0, 1, plans) }));
        return (sim, lots);
    }

    // ---- What a full stock buys ----------------------------------------------------------------------------------

    [Fact]
    public void ABuildingWithAFullConstructionStock_GoesUpASize_AtTheTurnOfTheMonth_AndSpendsTheStock()
    {
        var (sim, _) = BlockTown(Quick(), (BuildingType.Home, 0));
        Fill(sim, 0);
        int before = sim.StatsOf(0).Population;

        RunMonths(sim, 0.9);
        Assert.Equal(0, sim.Buildings[0].Tier); // nothing happens in the middle of a month
        RunMonths(sim, 0.2);

        Assert.Equal(1, sim.Buildings[0].Tier);
        Assert.False(HasStock(sim, 0));
        Assert.Equal(1, sim.Towns[0].LastGrowth.Upgrades);
        Assert.Equal(sim.TownTuning.Of(BuildingType.Home, 1).People, sim.StatsOf(0).Population);
        Assert.True(sim.StatsOf(0).Population > before);
    }

    [Fact]
    public void ABuildingWithAPartialStock_WaitsForTheRest()
    {
        var (sim, _) = BlockTown(Quick(), (BuildingType.Home, 0));
        Fill(sim, 0);
        var b = sim.Buildings[0];
        for (int c = 0; c < b.Construction.Length; c++) b.Construction[c] *= 0.9f;

        RunMonths(sim, 1.1);

        Assert.Equal(0, b.Tier);
        Assert.True(HasStock(sim, 0));
    }

    [Fact]
    public void TheNewPeopleOfAGrownHome_AreGroupsOfTheirOwn()
    {
        var (sim, _) = BlockTown(Quick(), (BuildingType.Home, 0), (BuildingType.Commerce, 1));
        Fill(sim, 0);

        RunMonths(sim, 1.1);

        Assert.Equal(sim.StatsOf(0).Population, sim.TripGroups.Where(g => g.Home == 0).Sum(g => g.People));
        Assert.All(sim.TripGroups, g => Assert.InRange(g.People, 1, sim.TripTuning.GroupSize));
    }

    [Fact]
    public void ABuildingAtTheTopSize_FoundsANewOneOnTheNearestFreeLot()
    {
        var (sim, lots) = BlockTown(Quick(), (BuildingType.Home, 2), null, (BuildingType.Home, 2), (BuildingType.Home, 2), (BuildingType.Home, 2), (BuildingType.Home, 2), (BuildingType.Home, 2), null);
        Fill(sim, 0);
        int before = sim.Buildings.Count;

        RunMonths(sim, 1.1);

        Assert.Equal(before + 1, sim.Buildings.Count);
        var born = sim.Buildings[^1];
        Assert.Equal(0, born.Tier);
        Assert.Equal(lots[1].X, born.X, 0.01f); // the lot next to the parent, not the far one
        Assert.Equal(lots[1].Z, born.Z, 0.01f);
        Assert.False(HasStock(sim, 0));
        Assert.Equal(1, sim.Towns[0].LastGrowth.NewBuildings);
        Assert.Equal(BuildingState.Active, born.State);
        Assert.Contains(born.Id, sim.Towns[0].Buildings);
    }

    [Fact]
    public void ATownWithNoFreeLot_BuildsANewBlockOfStreets_AndFoundsTheBuildingThere()
    {
        var all = Enumerable.Repeat<(BuildingType, int)?>((BuildingType.Home, 2), 8).ToArray();
        var (sim, _) = BlockTown(Quick(), all);
        Fill(sim, 0);
        int roads = Enumerable.Range(0, sim.Network.RoadCount).Count(r => !sim.Network.GetRoad(new RoadId(r)).Retired);
        int before = sim.Buildings.Count;

        RunMonths(sim, 1.1);

        int after = Enumerable.Range(0, sim.Network.RoadCount).Count(r => !sim.Network.GetRoad(new RoadId(r)).Retired);
        Assert.True(after > roads, $"{after} roads against {roads}");
        Assert.Equal(1, sim.Towns[0].LastGrowth.NewBlocks);
        Assert.Equal(before + 1, sim.Buildings.Count);
        var born = sim.Buildings[^1];
        Assert.True(MathF.Abs(born.X) > 60f || MathF.Abs(born.Z) > 60f, $"({born.X}, {born.Z}) is inside the first block");
        Assert.Equal(BuildingState.Active, born.State);
        // The new streets are real: the building has a road to reach it.
        Assert.True(new RoadIndex(sim.Network).HasRoadWithin(born.X, born.Z, sim.TownTuning.AccessRadius));
    }

    [Fact]
    public void ATownDoesNotBuildBlocksIntoWaterOrOtherBuildings()
    {
        var all = Enumerable.Repeat<(BuildingType, int)?>((BuildingType.Home, 2), 8).ToArray();
        // Water on every side but a thin strip: no side can grow.
        var tuning = Quick();
        var net = new RoadNetwork(Terrain.Create(512, 8f, (x, z) => MathF.Abs(x) < 100 && MathF.Abs(z) < 100 ? 5f : -5f), tuning);
        var sim = new Simulation(net, 7);
        foreach (var (x0, z0, x1, z1) in new[] { (-60f, -60f, 60f, -60f), (60f, -60f, 60f, 60f), (60f, 60f, -60f, 60f), (-60f, 60f, -60f, -60f) })
            sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadType.TwoLane, 14f));
        var lots = TownGenerator.LotSites(tuning.Towns, 0, 0, 0, 1, 0, 0, 0, 0).ToList();
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Islet", 0, 0, 0, 1, lots.Select(l => new BuildingPlan(BuildingType.Home, 2, l.X, l.Z, l.Facing)).ToList()) }));
        Fill(sim, 0);
        int roads = sim.Network.RoadCount;

        RunMonths(sim, 1.1);

        Assert.Equal(roads, sim.Network.RoadCount);
        Assert.Equal(8, sim.Buildings.Count);
        Assert.True(HasStock(sim, 0)); // nothing was spent
    }

    // ---- The gates ----------------------------------------------------------------------------------------------

    [Fact]
    public void NothingGrows_WhileTheTownIsUnsatisfied()
    {
        var (sim, _) = BlockTown(Quick(), (BuildingType.Home, 0));
        Fill(sim, 0);
        sim.Towns[0].Satisfaction = sim.TownTuning.Growth.MinSatisfaction - 5f;

        RunMonths(sim, 1.1);

        Assert.Equal(0, sim.Buildings[0].Tier);
        Assert.Equal(GrowthBlock.Satisfaction, sim.Towns[0].LastGrowth.Blocked);
        Assert.True(HasStock(sim, 0));
    }

    [Fact]
    public void NothingGrows_WhileTheTownIsNotConnected()
    {
        // Nothing ever completes a trip within the budget when the budget is nothing.
        var t = Quick(minConnectivity: 0.6f);
        t = t with { Towns = t.Towns with { Growth = t.Towns.Growth with { TimeBudgetSeconds = 0f } } };
        var (sim, _) = BlockTown(t, (BuildingType.Home, 0), (BuildingType.Commerce, 0));
        Fill(sim, 0);

        RunMonths(sim, 1.1);

        Assert.Equal(0f, sim.Towns[0].Connectivity);
        Assert.Equal(GrowthBlock.Connectivity, sim.Towns[0].LastGrowth.Blocked);
        Assert.Equal(0, sim.Buildings[0].Tier);
    }

    [Fact]
    public void ATownWithNothingDelivered_KeepsItsSize_ForYears()
    {
        var (sim, _) = BlockTown(Quick(), (BuildingType.Home, 1), (BuildingType.Home, 2), (BuildingType.Commerce, 1), (BuildingType.Industry, 0));
        var tiers = sim.Buildings.Select(b => b.Tier).ToList();
        int people = sim.StatsOf(0).Population;

        RunMonths(sim, 24);

        Assert.Equal(4, sim.Buildings.Count);
        Assert.Equal(tiers, sim.Buildings.Select(b => b.Tier).ToList());
        Assert.Equal(people, sim.StatsOf(0).Population);
        Assert.Equal(0, sim.Towns[0].LastGrowth.NewBuildings + sim.Towns[0].LastGrowth.Upgrades);
    }

    // ---- Where it grows -----------------------------------------------------------------------------------------

    [Fact]
    public void Growth_AppearsAroundTheStationThatDelivers()
    {
        var homes = Enumerable.Repeat<(BuildingType, int)?>((BuildingType.Home, 0), 8).ToArray();
        var narrow = Quick();
        narrow = narrow with { Stations = narrow.Stations with { CoverageRadius = 90f } }; // a station that covers only some of the homes
        var (sim, lots) = BlockTown(narrow, homes);
        int station = sim.Execute(new BuildStationCommand(60, -60));
        Assert.True(station >= 0);
        var covered = sim.CoveredBy(sim.Stations[station]);
        Assert.InRange(covered.Count, 2, 7);

        sim.Distribute(sim.Stations[station], Materials, 5000);
        sim.Distribute(sim.Stations[station], Planks, 5000);
        RunMonths(sim, 1.1);

        for (int id = 0; id < 8; id++)
            Assert.Equal(covered.Contains(id) ? 1 : 0, sim.Buildings[id].Tier);
    }

    // ---- Connectivity ---------------------------------------------------------------------------------------------

    /// <summary>Homes at x = 100 to 220 on a road of their own, jobs and shops 1.3 km away on another, a day of 600 s. Optionally a road between them.</summary>
    static Simulation TwoClusters(bool joined)
    {
        var t = Tuning.Load();
        t = t with { Trips = t.Trips with { CarShare = 1f } };
        var net = new RoadNetwork(DryLevelMap(), t);
        var sim = new Simulation(net, 3);
        sim.Execute(new BuildRoadCommand(0, 0, 300, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(1400, 0, 1700, 0, RoadType.TwoLane, 14f));
        if (joined) sim.Execute(new BuildRoadCommand(300, 0, 1400, 0, RoadType.TwoLane, 14f));
        sim.AddTowns(new WorldPlan(1, new[]
        {
            new TownPlan("Testville", 800, 0, 0, 1, new[]
            {
                new BuildingPlan(BuildingType.Home, 0, 100, 25, 0), new BuildingPlan(BuildingType.Home, 0, 160, -25, 0),
                new BuildingPlan(BuildingType.Home, 0, 220, 25, 0), new BuildingPlan(BuildingType.Home, 0, 40, -25, 0),
                new BuildingPlan(BuildingType.Industry, 2, 1500, 25, 0), new BuildingPlan(BuildingType.Commerce, 2, 1600, -25, 0),
            }),
        }));
        for (int id = 0; id < 4; id++) Fill(sim, id);
        return sim;
    }

    [Fact]
    public void AServedTown_WhosePeopleCannotReachWorkOrLeisure_DoesNotGrow_UntilARoadJoinsThem()
    {
        var cut = TwoClusters(joined: false);
        var open = TwoClusters(joined: true);

        RunMonths(cut, 1.05);
        RunMonths(open, 1.05);

        Assert.Equal(0f, cut.Towns[0].Connectivity);
        Assert.Equal(GrowthBlock.Connectivity, cut.Towns[0].LastGrowth.Blocked);
        Assert.All(Enumerable.Range(0, 4), id => Assert.Equal(0, cut.Buildings[id].Tier));

        Assert.True(open.Towns[0].Connectivity >= open.TownTuning.Growth.MinConnectivity, $"connectivity {open.Towns[0].Connectivity}");
        Assert.Equal(GrowthBlock.None, open.Towns[0].LastGrowth.Blocked);
        Assert.All(Enumerable.Range(0, 4), id => Assert.Equal(1, open.Buildings[id].Tier));
    }

    // ---- Determinism ----------------------------------------------------------------------------------------------

    [Fact]
    public void AGrowingTown_RunsTheSameEveryTime_AndReplaysFromTheSeed()
    {
        ulong Run()
        {
            var all = Enumerable.Repeat<(BuildingType, int)?>((BuildingType.Home, 2), 8).ToArray();
            var (sim, _) = BlockTown(Quick(), all);
            Fill(sim, 0);
            Fill(sim, 3);
            RunMonths(sim, 3.2);
            return sim.StateHash();
        }

        Assert.Equal(Run(), Run());
    }
}
