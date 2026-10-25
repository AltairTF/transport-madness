using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Growth of towns: connectivity, the gates, what a full construction stock buys, and where growth appears.</summary>
public class GrowthTests
{
    const int Materials = 5, Planks = 3;

    /// <summary>A place for a building in a test town: its middle and the way it faces.</summary>
    internal readonly record struct BlockSpot(float X, float Z, float Facing);

    /// <summary>The eight places of the test block: two on each side of a square of streets 120 m a side round (0, 0), set back from the street so any size fits, facing it.</summary>
    internal static IReadOnlyList<BlockSpot> BlockSpots(Tuning tuning)
    {
        float widest = 0f;
        foreach (var type in Enum.GetValues<BuildingType>()) widest = MathF.Max(widest, tuning.Towns.Of(type, TownsTuning.Tiers - 1).Footprint);
        float setback = 8f + 1.5f + widest, half = 60f;
        var spots = new List<BlockSpot>();
        for (int side = 0; side < 4; side++)
            for (int lot = 0; lot < 2; lot++)
            {
                float along = (lot - 0.5f) * 32f;
                spots.Add(side switch
                {
                    0 => new BlockSpot(along, -half + setback, -MathF.PI * 0.5f),
                    1 => new BlockSpot(along, half - setback, MathF.PI * 0.5f),
                    2 => new BlockSpot(-half + setback, along, MathF.PI),
                    _ => new BlockSpot(half - setback, along, 0f),
                });
            }
        return spots;
    }

    internal static Terrain DryLevelMap() => Terrain.Create(512, 8f, (_, _) => 5f);

    /// <summary>A short day (60 s) so that months go by quickly; growth does not depend on the length of the day.</summary>
    internal static Tuning Quick(float minConnectivity = 0f)
    {
        var t = Tuning.Load();
        return t with
        {
            Economy = t.Economy with { DayLength = 60f },
            Towns = t.Towns with { Growth = t.Towns.Growth with { MinConnectivity = minConnectivity } },
        };
    }

    internal static void RunMonths(Simulation sim, double months)
    {
        long ticks = (long)Math.Round(months * sim.Network.Tuning.Economy.DaysPerMonth * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    internal static void Fill(Simulation sim, int building)
    {
        var b = sim.Buildings[building];
        foreach (var a in sim.TownTuning.Of(b.Type, b.Tier).Construction)
            for (int c = 0; c < sim.CargoTypes.Count; c++)
                if (sim.CargoTypes[c].Id == a.Cargo) b.Construction[c] = a.Amount;
    }

    internal static bool HasStock(Simulation sim, int building) => sim.Buildings[building].Construction.Any(x => x > 0f);

    /// <summary>
    /// One square block of streets, 120 m a side, round (0, 0), with its eight lots. <paramref name="occupants"/> puts a
    /// building (type, size) on the lots in order; null leaves a lot free.
    /// </summary>
    internal static (Simulation Sim, IReadOnlyList<BlockSpot> Lots) BlockTown(Tuning tuning, params (BuildingType Type, int Tier)?[] occupants) => BlockTown(tuning, 7, occupants);

    internal static (Simulation Sim, IReadOnlyList<BlockSpot> Lots) BlockTown(Tuning tuning, ulong seed, params (BuildingType Type, int Tier)?[] occupants)
    {
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, seed);
        foreach (var (x0, z0, x1, z1) in new[] { (-60f, -60f, 60f, -60f), (60f, -60f, 60f, 60f), (60f, 60f, -60f, 60f), (-60f, 60f, -60f, -60f) })
            sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadType.TwoLane, 14f));
        var lots = GrowthTests.BlockSpots(tuning);
        var plans = new List<BuildingPlan>();
        for (int k = 0; k < occupants.Length; k++)
            if (occupants[k] is { } o) plans.Add(new BuildingPlan(o.Type, o.Tier, lots[k].X, lots[k].Z, lots[k].Facing));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", 0, 0, plans) }));
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
    public void BuildingsFilledByDeliveries_ReachAFullStock_NotJustShortOfIt()
    {
        // Deliveries are split by room; the last fraction of a unit of room used to be left over for good (239.93 of 240).
        var (sim, _) = BlockTown(Quick(), (BuildingType.Home, 0), (BuildingType.Commerce, 0), (BuildingType.Home, 1), (BuildingType.Home, 0), (BuildingType.Commerce, 1), (BuildingType.Home, 0), (BuildingType.Home, 1), (BuildingType.Commerce, 0));
        var (x, z) = sim.Network.GetNode(sim.Network.FindNodeNear(-60f, -60f, 20f, withRoadsOnly: true));
        var station = sim.Stations[sim.Execute(new BuildStationCommand(x, z))];
        sim.Execute(new TakeLoanCommand(100000));
        foreach (int cargo in new[] { Materials, Planks })
            while (sim.Distribute(station, cargo, 7) > 0) { }

        RunMonths(sim, 1.1);

        Assert.Equal(8, sim.Towns[0].LastGrowth.Upgrades);
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
    public void ABuildingAtTheTopSize_FoundsANewOneOnTheNearestFreeSpotBesideARoad()
    {
        var (sim, lots) = BlockTown(Quick(), (BuildingType.Home, 2), null, (BuildingType.Home, 2), (BuildingType.Home, 2), (BuildingType.Home, 2), (BuildingType.Home, 2), (BuildingType.Home, 2), null);
        Fill(sim, 0);
        int before = sim.Buildings.Count;

        RunMonths(sim, 1.1);

        Assert.Equal(before + 1, sim.Buildings.Count);
        var born = sim.Buildings[^1];
        Assert.Equal(0, born.Tier);
        var parent = sim.Buildings[0];
        Assert.True(MathF.Sqrt((born.X - parent.X) * (born.X - parent.X) + (born.Z - parent.Z) * (born.Z - parent.Z)) < 60f, "next to the parent, not the far side of the block");
        Assert.True(new RoadIndex(sim.Network).HasRoadWithin(born.X, born.Z, sim.TownTuning.AccessRadius));
        Assert.False(HasStock(sim, 0));
        Assert.Equal(1, sim.Towns[0].LastGrowth.NewBuildings);
        Assert.Equal(BuildingState.Active, born.State);
        Assert.Contains(born.Id, sim.Towns[0].Buildings);
    }

    [Fact]
    public void ATownWithNoFreeSpotInTheBlock_FoundsTheBuildingBesideARoadOfItsReach()
    {
        var all = Enumerable.Repeat<(BuildingType, int)?>((BuildingType.Home, 2), 8).ToArray();
        var (sim, _) = BlockTown(Quick(), all);
        Fill(sim, 0);
        int before = sim.Buildings.Count;

        RunMonths(sim, 1.1);

        Assert.Equal(before + 1, sim.Buildings.Count);
        var born = sim.Buildings[^1];
        Assert.Equal(BuildingState.Active, born.State);
        Assert.True(new RoadIndex(sim.Network).HasRoadWithin(born.X, born.Z, sim.TownTuning.AccessRadius));
        Assert.Equal(0, sim.Towns[0].LastGrowth.NewStreets); // a road had room
    }

    [Fact]
    public void ATown_GrowsAStreetOfEarthFromItsRoad_WithSpotsAlongIt()
    {
        var tuning = Quick();
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, 7);
        sim.Execute(new BuildRoadCommand(-150f, 0f, 150f, 0f, RoadType.TwoLane, 14f));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Lane", 0, 0, new List<BuildingPlan> { new(BuildingType.Home, 0, 0f, 12f, 0f) }) }));
        int roads = Enumerable.Range(0, net.RoadCount).Count(r => !net.GetRoad(new RoadId(r)).Retired);

        Assert.True(sim.GrowStreet(sim.Towns[0], 0f, 12f));

        Assert.True(Enumerable.Range(0, net.RoadCount).Count(r => !net.GetRoad(new RoadId(r)).Retired) > roads);
        Assert.Contains(Enumerable.Range(0, net.RoadCount), r => net.GetRoad(new RoadId(r)).Type == RoadType.Path);
        Assert.NotNull(sim.FindSpot(sim.Towns[0], 0f, 12f)); // spots along the new street
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
        var lots = GrowthTests.BlockSpots(tuning);
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Islet", 0, 0, lots.Select(l => new BuildingPlan(BuildingType.Home, 2, l.X, l.Z, l.Facing)).ToList()) }));
        Fill(sim, 0);
        int roads = sim.Network.RoadCount;

        RunMonths(sim, 1.1);

        Assert.Equal(roads, sim.Network.RoadCount); // no street over water
        Assert.All(sim.Buildings, b => Assert.True(net.Terrain!.HeightAt(b.X, b.Z) > 0f)); // and no house in it
    }

    [Fact]
    public void AStreetThatIsToJoinARoad_BendsBackAndEndsOnIt()
    {
        var tuning = Quick();
        tuning = tuning with { Towns = tuning.Towns with { Generation = tuning.Towns.Generation with { Streets = tuning.Towns.Generation.Streets with { LoopChance = 1f } } } };
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, 7);
        sim.Execute(new BuildRoadCommand(-300f, 0f, 300f, 0f, RoadType.TwoLane, 14f));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Lane", 0, 0, new List<BuildingPlan> { new(BuildingType.Home, 0, 0f, 12f, 0f) }, 300f) }));

        Assert.True(sim.GrowStreet(sim.Towns[0], 0f, 12f));

        var roads = Enumerable.Range(0, net.RoadCount).Select(r => net.GetRoad(new RoadId(r))).Where(r => !r.Retired).ToList();
        var lanes = roads.Where(r => r.Type == RoadType.Path).ToList();
        Assert.NotEmpty(lanes);
        // A joined street leaves the main road and arrives on it again: its pieces touch the main road at both ends.
        var main = roads.Where(r => r.Type == RoadType.TwoLane).ToList();
        int touching = lanes.SelectMany(l => new[] { l.Geometry.PointAt(0f), l.Geometry.End }).Count(p => main.Any(m => m.Geometry.DistanceTo(p.X, p.Z) < 1f));
        Assert.True(touching >= 2, $"{touching} street ends on the main road");
    }

    [Fact]
    public void AStreet_IsNotLaidAcrossARailway()
    {
        var tuning = Quick();
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, 7);
        sim.Execute(new TakeLoanCommand(1000000));
        sim.Execute(new BuildRoadCommand(-150f, 0f, 150f, 0f, RoadType.TwoLane, 14f));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Lane", 0, 0, new List<BuildingPlan> { new(BuildingType.Home, 0, 0f, 12f, 0f) }, 200f) }));
        Assert.True(sim.Execute(new BuildTrackCommand(-150f, 60f, 150f, 60f)) > 0);

        Assert.True(sim.GrowStreet(sim.Towns[0], 0f, 12f));

        var lanes = Enumerable.Range(0, net.RoadCount).Select(r => net.GetRoad(new RoadId(r))).Where(r => !r.Retired && r.Type == RoadType.Path).ToList();
        Assert.NotEmpty(lanes);
        Assert.All(lanes, l => Assert.True(l.Geometry.PointAt(0f).Z < 45f && l.Geometry.End.Z < 45f && l.Geometry.PointAt(l.Geometry.Length * 0.5f).Z < 45f, "a street reaches the railway"));
    }

    [Fact]
    public void AWellServedVillage_GrowsStreetByStreetForYears_AndEveryNewHouseHasARoadAndRoom()
    {
        var (sim, _) = WorldBuilder.Towns(11, 3, IndustryAmount.Few, Quick());
        int start = sim.StatsOf(0).Population, roads = sim.Network.RoadCount;
        for (int month = 0; month < 60; month++) // five years of a town that gets everything it needs
        {
            foreach (var t in sim.Towns) t.Satisfaction = 100f;
            for (int id = 0; id < sim.Buildings.Count; id++) if (sim.Buildings[id].State == BuildingState.Active) Fill(sim, id);
            RunMonths(sim, 1);
        }
        int people = sim.StatsOf(0).Population;
        Assert.True(people > start, $"{start} people then, {people} now");
        Assert.True(sim.Network.RoadCount > roads, "no street was born");
        var index = new RoadIndex(sim.Network);
        var live = sim.Buildings.Where(b => b.State == BuildingState.Active).ToList();
        Assert.All(live, b => Assert.True(index.HasRoadWithin(b.X, b.Z, sim.TownTuning.AccessRadius)));
        for (int a = 0; a < live.Count; a++)
            for (int c = a + 1; c < live.Count; c++)
                Assert.True((live[a].X - live[c].X) * (live[a].X - live[c].X) + (live[a].Z - live[c].Z) * (live[a].Z - live[c].Z) > 100f, "two buildings on top of each other");
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
        // No call-up here: the first wave (month 22) lowers the room of the buildings for good, which CapacityTests covers.
        var tuning = Quick();
        tuning = tuning with { War = tuning.War with { Conscription = tuning.War.Conscription with { Waves = new List<WarWave>() } } };
        var (sim, _) = BlockTown(tuning, (BuildingType.Home, 1), (BuildingType.Home, 2), (BuildingType.Commerce, 1), (BuildingType.Industry, 0));
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
            new TownPlan("Testville", 800, 0, new[]
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

    // ---- What a town still needs, and what the map shows ---------------------------------------------------------

    [Fact]
    public void TheNeedsOfATown_ListWhatItIsShortOf_AndHowMuchOfItTheStationsCover()
    {
        var (sim, _) = BlockTown(Quick(), (BuildingType.Home, 0), (BuildingType.Commerce, 0));
        int Cargo(string id) => Enumerable.Range(0, sim.CargoTypes.Count).First(c => sim.CargoTypes[c].Id == id);
        int materials = Cargo("materials"), food = Cargo("food"), goods = Cargo("goods");

        var start = sim.NeedsOf(0);
        Assert.Equal(2, start.Buildings);
        Assert.Equal(0, start.Covered); // no station yet
        Assert.Equal(40f, start.Cargo.First(c => c.Cargo == materials).MissingConstruction); // 20 for each building
        Assert.All(start.Cargo, c => Assert.Equal(0, c.ShortBuildings)); // they start with some stock

        var (x, z) = sim.Network.GetNode(sim.Network.FindNodeNear(-60f, -60f, 20f, withRoadsOnly: true));
        sim.Execute(new TakeLoanCommand(100000));
        Assert.True(sim.Execute(new BuildStationCommand(x, z)) >= 0);
        RunMonths(sim, 3.1); // the starting stock is used up

        var later = sim.NeedsOf(0);
        Assert.Equal(2, later.Covered);
        Assert.Equal(1, later.Cargo.First(c => c.Cargo == food).ShortBuildings);  // the home eats
        Assert.Equal(2, later.Cargo.First(c => c.Cargo == goods).ShortBuildings); // home and shop
        Assert.True(later.Cargo.First(c => c.Cargo == goods).MissingMonthly > 0f);

        var station = sim.Stations[0];
        while (sim.Distribute(station, materials, 20) > 0) { }
        Assert.Equal(0f, sim.NeedsOf(0).Cargo.First(c => c.Cargo == materials).MissingConstruction, 0.01f);
    }

    [Fact]
    public void HomesAreMarkedReached_OrNot_ByTheTripsOfLastMonth()
    {
        var cut = TwoClusters(joined: false);
        var open = TwoClusters(joined: true);
        Assert.All(Enumerable.Range(0, 4), id => Assert.True(cut.Buildings[id].Reached)); // until a month is over

        RunMonths(cut, 1.05);
        RunMonths(open, 1.05);

        Assert.All(Enumerable.Range(0, 4), id => Assert.False(cut.Buildings[id].Reached));
        Assert.All(Enumerable.Range(0, 4), id => Assert.True(open.Buildings[id].Reached));
        Assert.True(open.Buildings[5].Reached); // other kinds are never marked
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
