using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Rebuilding (C15a): what a raid destroys becomes free lots that the town founds again through its growth, and a raid never destroys the last building of a town.</summary>
public class RebuildingTests
{
    const int Materials = 5, Planks = 3;

    /// <summary>Shipped tuning with a short day, no connectivity gate, and a building at the centre of a blast always destroyed (<paramref name="radius"/> metres wide).</summary>
    static Tuning Bombed(float radius = 35f)
    {
        var t = GrowthTests.Quick();
        return t with { War = t.War with { Bombing = t.War.Bombing with { BuildingChance = 1f, BlastRadius = radius } } };
    }

    static IEnumerable<Building> Standing(Simulation sim, int town = 0) =>
        sim.Buildings.Where(b => b.Town == town && b.State != BuildingState.Demolished);

    static (Simulation Sim, IReadOnlyList<GrowthTests.BlockSpot> Lots) Block(Tuning tuning, ulong seed, params (BuildingType Type, int Tier)?[] occupants) =>
        GrowthTests.BlockTown(tuning, seed, occupants);

    static (Simulation Sim, IReadOnlyList<GrowthTests.BlockSpot> Lots) EightHomes(Tuning tuning, ulong seed = 7) =>
        Block(tuning, seed, Enumerable.Repeat<(BuildingType, int)?>((BuildingType.Home, 2), 8).ToArray());

    /// <summary>The ids of the buildings a bomb on a lot destroys; the lot of each is free afterwards.</summary>
    static List<Building> Bomb(Simulation sim, GrowthTests.BlockSpot lot, ulong seed = 1)
    {
        var before = Standing(sim).ToList();
        sim.BombForTest(lot.X, lot.Z, seed);
        return before.Where(b => b.State == BuildingState.Demolished).ToList();
    }

    // ---- The lots ------------------------------------------------------------------------------------------------

    [Fact]
    public void ADestroyedBuilding_LeavesItsLotFree_AndTheLotCanBeBuiltOnAgain()
    {
        var (sim, lots) = EightHomes(Bombed());
        var home = sim.Buildings[0];
        Assert.False(sim.RoomFor(home.X, home.Z, BuildingType.Home, 0));

        var gone = Bomb(sim, lots[0]);

        Assert.Contains(home, gone);
        Assert.True(sim.RoomFor(home.X, home.Z, BuildingType.Home, 0));
    }

    [Fact]
    public void ALotWhoseRoadIsGone_IsNotBuiltOn_AndTheRoadAccessRulesStillApply()
    {
        var (sim, lots) = EightHomes(Bombed());
        var gone = Bomb(sim, lots[0]);
        var lot = gone[0];
        Assert.True(sim.RoomFor(lot.X, lot.Z, BuildingType.Home, 0));
        Assert.NotEqual(0, sim.Execute(new RemoveRoadCommand(sim.Network.RoadAt(lot.X, lot.Z, 60f)!.Value.Value)));
        sim.Step(); // the buildings' view of the roads follows at the next step
        Assert.False(sim.RoomFor(lot.X, lot.Z, BuildingType.Home, 0));

        // A survivor on the other side of the block has the stock; whatever it founds, it is not on the lot that lost its street.
        var survivor = Standing(sim).Where(b => b.State == BuildingState.Active).OrderByDescending(b => (b.X - lot.X) * (b.X - lot.X) + (b.Z - lot.Z) * (b.Z - lot.Z)).First();
        GrowthTests.Fill(sim, survivor.Id);
        int count = sim.Buildings.Count;

        GrowthTests.RunMonths(sim, 1.1);

        Assert.DoesNotContain(sim.Buildings.Skip(count), b => b.X == lot.X && b.Z == lot.Z);
        var index = new RoadIndex(sim.Network);
        Assert.All(sim.Buildings.Skip(count), b => Assert.True(index.HasRoadWithin(b.X, b.Z, sim.TownTuning.AccessRadius)));
    }

    // ---- Growing back --------------------------------------------------------------------------------------------

    [Fact]
    public void ABombedTown_FoundsBuildingsAgainOnTheFreedLots_WhenConstructionStockIsDelivered()
    {
        var (sim, lots) = EightHomes(Bombed());
        var gone = Bomb(sim, lots[0]);
        var freed = gone.Select(b => (b.X, b.Z)).ToHashSet();
        Assert.NotEmpty(gone);
        int survivors = Standing(sim).Count();
        var survivor = Standing(sim).Last();
        GrowthTests.Fill(sim, survivor.Id);
        int before = sim.Buildings.Count;

        GrowthTests.RunMonths(sim, 1.1);

        Assert.Equal(before + 1, sim.Buildings.Count);
        var born = sim.Buildings[^1];
        Assert.Contains((born.X, born.Z), freed); // on the spot a raid emptied, not on a new street
        Assert.Equal(0, born.Tier);
        Assert.Equal(BuildingState.Active, born.State);
        Assert.Equal(survivors + 1, Standing(sim).Count());
        Assert.Equal(0, sim.Towns[0].LastGrowth.NewStreets);
    }

    [Fact]
    public void ABombedTown_WithAStationDeliveringMaterials_GrowsBackToItsFormerNumberOfBuildings()
    {
        var (sim, lots) = EightHomes(Bombed());
        var (x, z) = sim.Network.GetNode(sim.Network.FindNodeNear(-60f, -60f, 20f, withRoadsOnly: true));
        var station = sim.Stations[sim.Execute(new BuildStationCommand(x, z))];
        sim.Execute(new TakeLoanCommand(100000));
        var gone = Bomb(sim, lots[3]);
        Assert.NotEmpty(gone);
        int survivors = Standing(sim).Count();
        foreach (int cargo in new[] { Materials, Planks })
            while (sim.Distribute(station, cargo, 7) > 0) { }

        GrowthTests.RunMonths(sim, 1.1);

        // Each survivor at the top size founds one building, on the nearest free lot, until the lots are used (then new blocks of streets are built for the rest).
        Assert.True(Standing(sim).Count() >= 8);
        Assert.All(gone, b => Assert.Contains(sim.Buildings.Skip(sim.Buildings.Count - sim.Towns[0].LastGrowth.NewBuildings), n => n.X == b.X && n.Z == b.Z));
        Assert.True(sim.Towns[0].LastGrowth.NewBuildings >= 8 - survivors);
    }

    [Fact]
    public void ABombedTown_StaysDestroyed_WithoutConstructionStock()
    {
        var (sim, lots) = EightHomes(Bombed());
        var gone = Bomb(sim, lots[0]);
        Assert.NotEmpty(gone);
        int standing = Standing(sim).Count(), count = sim.Buildings.Count;

        GrowthTests.RunMonths(sim, 6);

        Assert.Equal(count, sim.Buildings.Count);
        Assert.Equal(standing, Standing(sim).Count());
        Assert.All(gone, b => Assert.Equal(BuildingState.Demolished, b.State));
    }

    [Fact]
    public void TheStateDoesNotPayForRebuilding()
    {
        long Money(bool deliver)
        {
            var (sim, lots) = EightHomes(Bombed());
            Bomb(sim, lots[0]);
            if (deliver) GrowthTests.Fill(sim, Standing(sim).Last().Id);
            GrowthTests.RunMonths(sim, 1.1);
            Assert.Equal(deliver, sim.Buildings.Count > 8);
            return sim.Company.Money;
        }

        Assert.Equal(Money(deliver: false), Money(deliver: true));
    }

    // ---- The people of rebuilt homes -----------------------------------------------------------------------------

    [Fact]
    public void TheHomelessLeave_AndABuiltHomeBringsItsPeopleAsNewTripGroups()
    {
        // Several seeds, because what is founded is drawn: some of the new buildings are homes and some are not.
        int homes = 0, others = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var tuning = Bombed();
            tuning = tuning with { Economy = tuning.Economy with { DayLength = 300f } };
            var (sim, lots) = EightHomes(tuning, seed);
            int room = sim.StatsOf(0).Population;
            var gone = Bomb(sim, lots[0], seed);
            Assert.NotEmpty(gone);
            long day = sim.Day;
            while (sim.Day == day) sim.Step(); // the groups of the destroyed homes retire when the day closes
            int lost = gone.Sum(b => sim.TownTuning.Of(b.Type, b.Tier).People);
            Assert.Equal(room - lost, sim.ResidentsByTown()[0]);
            Assert.Equal(sim.StatsOf(0).Population, sim.ResidentsByTown()[0]); // nobody is left in the books who has no home

            GrowthTests.Fill(sim, Standing(sim).Last().Id);
            int residents = sim.ResidentsByTown()[0];
            GrowthTests.RunMonths(sim, 1.1);

            var born = sim.Buildings[^1];
            Assert.Equal(9, sim.Buildings.Count);
            var groups = sim.TripGroups.Where(g => g.Home == born.Id).ToList();
            if (born.Type == BuildingType.Home)
            {
                homes++;
                Assert.Equal(sim.TownTuning.Of(BuildingType.Home, 0).People, groups.Sum(g => g.People));
                Assert.All(groups, g => Assert.False(g.Retired));
                Assert.Equal(residents + groups.Sum(g => g.People), sim.ResidentsByTown()[0]);
            }
            else
            {
                others++;
                Assert.Empty(groups);
                Assert.Equal(residents, sim.ResidentsByTown()[0]);
            }
            Assert.Equal(sim.StatsOf(0).Population, sim.ResidentsByTown()[0]);
        }
        Assert.True(homes > 0, "no home was founded in any run");
    }

    [Fact]
    public void RaidCasualties_AndRebuilding_KeepTheBooksConsistent()
    {
        var tuning = Bombed();
        tuning = tuning with { Economy = tuning.Economy with { DayLength = 300f } };
        var (sim, lots) = EightHomes(tuning);
        var gone = Bomb(sim, lots[0]);
        Assert.True(sim.Casualties > 0);
        Assert.Equal((int)sim.Casualties, sim.Towns[0].Casualties);
        long casualties = sim.Casualties;

        GrowthTests.Fill(sim, Standing(sim).Last().Id);
        GrowthTests.RunMonths(sim, 1.1);

        Assert.Equal(casualties, sim.Casualties); // rebuilding never turns the dead back
        Assert.Equal(sim.StatsOf(0).Population, sim.ResidentsByTown()[0]);
    }

    // ---- The last building ---------------------------------------------------------------------------------------

    [Fact]
    public void ARaid_NeverDestroysTheLastStandingBuildingOfATown_WhateverTheSeed()
    {
        int spared = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var (sim, lots) = Block(Bombed(radius: 400f), seed, (BuildingType.Home, 1), (BuildingType.Commerce, 0), (BuildingType.Home, 2), null, (BuildingType.Home, 0));
            for (int bomb = 0; bomb < 30; bomb++)
            {
                spared += (int)SparedBy(sim, lots[bomb % 5].X, lots[bomb % 5].Z, seed * 100 + (ulong)bomb);
                Assert.True(Standing(sim).Any(), $"seed {seed}, bomb {bomb}: nothing stands");
            }
            Assert.Single(Standing(sim));
            Assert.Equal(BuildingState.Active, Standing(sim).Single().State);
            Assert.Equal(3, sim.Buildings.Count(b => b.State == BuildingState.Demolished));
        }
        Assert.True(spared > 0);
    }

    static int SparedBy(Simulation sim, float x, float z, ulong seed) => sim.BombForTest(x, z, seed).Spared;

    [Fact]
    public void ABuildingWithoutARoad_IsSpared_WhenItIsTheLastStandingOfItsTown()
    {
        var tuning = Bombed(radius: 400f);
        var net = new RoadNetwork(GrowthTests.DryLevelMap(), tuning);
        var sim = new Simulation(net, 3);
        sim.Execute(new BuildRoadCommand(-60, 0, 60, 0, RoadType.TwoLane, 14f));
        // Two buildings far from every street: standing, but inactive.
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Faraway", 0, 0, new[] { new BuildingPlan(BuildingType.Home, 0, 0, 200, 0), new BuildingPlan(BuildingType.Home, 0, 15, 200, 0) }) }));
        Assert.All(sim.Buildings, b => Assert.Equal(BuildingState.Inactive, b.State));

        for (ulong seed = 1; seed <= 20; seed++) sim.BombForTest(0, 200, seed);

        // One is lost and the last, with no road as it is, stays.
        Assert.Single(Standing(sim));
        Assert.Equal(BuildingState.Inactive, Standing(sim).Single().State);
    }

    [Fact]
    public void AnActiveBuilding_IsSparedAsTheLastWithARoad_EvenWhenInactiveOnesStandInTheTown()
    {
        var tuning = Bombed(radius: 400f);
        var net = new RoadNetwork(GrowthTests.DryLevelMap(), tuning);
        var sim = new Simulation(net, 5);
        sim.Execute(new BuildRoadCommand(-60, 0, 60, 0, RoadType.TwoLane, 14f));
        sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Split", 0, 0, new[]
        {
            new BuildingPlan(BuildingType.Home, 1, 0, 25, 0),    // on the road
            new BuildingPlan(BuildingType.Home, 1, 0, 200, 0),   // far from it: inactive
        }) }));
        Assert.Equal(BuildingState.Active, sim.Buildings[0].State);
        Assert.Equal(BuildingState.Inactive, sim.Buildings[1].State);

        for (ulong seed = 1; seed <= 20; seed++) sim.BombForTest(0, 100, seed);

        Assert.Equal(BuildingState.Active, sim.Buildings[0].State);
        Assert.Equal(BuildingState.Demolished, sim.Buildings[1].State);
    }

    [Fact]
    public void ATownReducedToOneBuilding_IsRebuiltFromIt()
    {
        var (sim, lots) = Block(Bombed(radius: 400f), 9, (BuildingType.Home, 2), (BuildingType.Home, 2), (BuildingType.Home, 2), (BuildingType.Home, 2));
        for (ulong seed = 1; seed <= 30; seed++) sim.BombForTest(0, 0, seed);
        var last = Standing(sim).Single();
        Assert.Equal(BuildingState.Active, last.State);
        GrowthTests.Fill(sim, last.Id);

        GrowthTests.RunMonths(sim, 1.1);

        Assert.Equal(2, Standing(sim).Count());
        Assert.Equal(sim.StatsOf(0).Population, sim.TripGroups.Where(g => !g.Retired).Sum(g => g.People));
    }

    // ---- Determinism ---------------------------------------------------------------------------------------------

    [Fact]
    public void ABombedAndRebuiltTown_IsDeterministic()
    {
        ulong Run()
        {
            var (sim, lots) = EightHomes(Bombed());
            Bomb(sim, lots[0], 4);
            GrowthTests.Fill(sim, Standing(sim).Last().Id);
            GrowthTests.RunMonths(sim, 2.1);
            return sim.StateHash();
        }
        Assert.Equal(Run(), Run());
    }
}
