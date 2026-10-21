using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The call-up lowers the capacity of the buildings of every town for good (C15c).</summary>
public class CapacityTests
{
    const int Materials = 5, Planks = 3;

    static Tuning Waves(Tuning t, params (int Month, float Share)[] waves) =>
        t with { War = t.War with { Conscription = ConscriptionTests.Waves(t.War.Conscription, waves) with { RecoveryPerMonth = 0f } } };

    /// <summary>The people and jobs the standing buildings of a town hold at their nominal size.</summary>
    static (int People, int Jobs) Nominal(Simulation sim, int town = 0)
    {
        int people = 0, jobs = 0;
        foreach (var b in sim.Buildings.Where(b => b.Town == town && b.State == BuildingState.Active))
        {
            var tier = sim.TownTuning.Of(b.Type, b.Tier);
            people += tier.People;
            jobs += tier.Jobs;
        }
        return (people, jobs);
    }

    static float Missing(Simulation sim, int town = 0) => sim.NeedsOf(town).Cargo.Sum(n => n.MissingMonthly);

    static void EmptyStocks(Simulation sim)
    {
        foreach (var b in sim.Buildings) Array.Clear(b.Sustain);
    }

    // ---- The factor ---------------------------------------------------------------------------------------------

    [Fact]
    public void EveryTownStartsWithItsWholeCapacity_AndEachWaveLowersItForGoodByTheWavesShare()
    {
        var sim = ConscriptionTests.Town(Waves(ConscriptionTests.Quick(), (1, 0.25f), (2, 0.20f)));
        Assert.Equal(1f, sim.Towns[0].CapacityFactor);
        var (people, jobs) = Nominal(sim);
        Assert.Equal(people, sim.StatsOf(0).Population);

        ConscriptionTests.RunDays(sim, 3 * 1 + 1);
        Assert.Equal(0.75f, sim.Towns[0].CapacityFactor, 0.0001f);
        Assert.Equal((int)Math.Round(people * 0.75), sim.StatsOf(0).Population);
        Assert.Equal((int)Math.Round(jobs * 0.75), sim.StatsOf(0).Jobs);

        ConscriptionTests.RunDays(sim, 3 * 1);
        Assert.Equal(0.75f * 0.80f, sim.Towns[0].CapacityFactor, 0.0001f);
        Assert.Equal((int)Math.Round(people * 0.6), sim.StatsOf(0).Population);
        Assert.Equal((int)Math.Round(jobs * 0.6), sim.StatsOf(0).Jobs);
    }

    [Fact]
    public void TheCapacity_NeverComesBack_EvenAsTheWorkforceRecovers()
    {
        var tuning = ConscriptionTests.Quick(c => ConscriptionTests.Waves(c, (1, 0.10f), (3, 0.06f)) with { RecoveryPerMonth = 0.05f });
        var sim = ConscriptionTests.Town(tuning);
        float last = 1f;
        for (int month = 0; month < 12; month++)
        {
            ConscriptionTests.RunDays(sim, 3);
            Assert.True(sim.Towns[0].CapacityFactor <= last);
            last = sim.Towns[0].CapacityFactor;
        }

        Assert.Equal(0.90f * 0.94f, last, 0.0001f);
        Assert.Equal(0f, sim.WorkforceGap, 0.2f); // the workforce gap closes; the buildings' room does not
    }

    [Fact]
    public void ThePeopleOfTheGroups_FallWithTheRoomOfTheBuildings_SoTheTownDoesNotShowEmptyRoom()
    {
        var sim = ConscriptionTests.Town(Waves(ConscriptionTests.Quick(), (1, 0.25f)));
        int before = ConscriptionTests.People(sim);

        ConscriptionTests.RunDays(sim, 3 * 1 + 2);

        int residents = sim.ResidentsByTown()[0];
        Assert.True(residents < before);
        Assert.InRange(residents, sim.StatsOf(0).Population - 8, sim.StatsOf(0).Population + 8); // the groups' draws are random, the capacity is exact
    }

    // ---- Needs, jobs, trips --------------------------------------------------------------------------------------

    [Fact]
    public void TheMonthlyNeedsOfTheBuildings_FallByTheFactor()
    {
        var sim = ConscriptionTests.Town(Waves(ConscriptionTests.Quick(), (1, 0.25f)));
        EmptyStocks(sim);
        float whole = Missing(sim);
        Assert.True(whole > 0f);

        ConscriptionTests.RunDays(sim, 3 * 1 + 0.5);
        EmptyStocks(sim);

        Assert.Equal(whole * 0.75f, Missing(sim), whole * 0.001f);
    }

    [Fact]
    public void ABuildingHoldingTheLoweredNeed_IsNotShort_AndTheMonthUsesOnlyThat()
    {
        float Unmet(bool called)
        {
            var sim = ConscriptionTests.Town(Waves(ConscriptionTests.Quick(), called ? new[] { (1, 0.5f) } : new[] { (40, 0.5f) }));
            ConscriptionTests.RunDays(sim, 3 * 1 + 0.5);
            EmptyStocks(sim);
            // Every building holds half of its nominal need.
            foreach (var b in sim.Buildings)
                foreach (var need in sim.TownTuning.Of(b.Type, b.Tier).Sustain)
                    b.Sustain[Enumerable.Range(0, sim.CargoTypes.Count).First(i => sim.CargoTypes[i].Id == need.Cargo)] = need.Amount * 0.5f;
            if (called) Assert.Equal(0f, Missing(sim), 0.01f); // nothing is short of the lowered need
            else Assert.True(Missing(sim) > 1f);
            ConscriptionTests.RunDays(sim, 3); // a month turns and the buildings use up their needs
            return sim.Towns[0].UnmetShare;
        }

        Assert.Equal(0f, Unmet(called: true), 0.001f);
        Assert.Equal(0.5f, Unmet(called: false), 0.05f);
    }

    [Fact]
    public void TheJobsOfABuilding_AreLessAttractiveAWorkplace_AfterTheCallUp()
    {
        // Two shops equally near a home, one in a town that lost half its room: the people choose the other more often.
        var tuning = GrowthTests.Quick();
        var net = new RoadNetwork(GrowthTests.DryLevelMap(), tuning);
        var sim = new Simulation(net, 5, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(-200, 0, 200, 0, RoadType.TwoLane, 14f));
        sim.AddTowns(new WorldPlan(2, new[]
        {
            new TownPlan("West", -100, 0, 0, 1, new[] { new BuildingPlan(BuildingType.Home, 0, 0, 25, 0), new BuildingPlan(BuildingType.Commerce, 2, -100, 25, 0) }),
            new TownPlan("East", 100, 0, 0, 1, new[] { new BuildingPlan(BuildingType.Commerce, 2, 100, 25, 0) }),
        }));
        var home = sim.Buildings[0];
        sim.Towns[1].CapacityFactor = 0.1f;

        int west = 0, east = 0;
        for (int k = 0; k < 16; k++)
        {
            int pick = sim.PickPlace(home, work: true, 1);
            if (sim.Buildings[pick].Town == 0) west++; else east++;
        }

        Assert.True(west >= 12, $"{west} to the whole town and {east} to the one with a tenth of its room");
    }

    [Fact]
    public void IndustryJobs_AreNotScaled()
    {
        var tuning = ConscriptionTests.Quick(c => ConscriptionTests.Waves(c, (1, 0.5f)));
        var sim = ConscriptionTests.Town(tuning);
        var sawmill = sim.Industries[0];
        int jobs = sim.JobsOf(sawmill);

        ConscriptionTests.RunDays(sim, 3 * 1 + 0.5);

        Assert.Equal(0.5f, sim.Towns[0].CapacityFactor, 0.0001f);
        Assert.Equal(jobs, sim.JobsOf(sawmill));
        Assert.Equal(jobs, sim.IndustryTuning.Types[sawmill.Type].Jobs);
    }

    // ---- Growth does not refill ----------------------------------------------------------------------------------

    [Fact]
    public void ABuildingThatGrows_BringsOnlyTheLoweredNumberOfNewPeople()
    {
        var (sim, _) = GrowthTests.BlockTown(GrowthTests.Quick(), (BuildingType.Home, 0));
        sim.Towns[0].CapacityFactor = 0.6f;
        int old = sim.TripGroups.Where(g => g.Home == 0).Sum(g => g.People);
        GrowthTests.Fill(sim, 0);

        GrowthTests.RunMonths(sim, 1.1);

        Assert.Equal(1, sim.Buildings[0].Tier);
        int p0 = sim.TownTuning.Of(BuildingType.Home, 0).People, p1 = sim.TownTuning.Of(BuildingType.Home, 1).People;
        Assert.Equal(old + (int)Math.Round(p1 * 0.6) - (int)Math.Round(p0 * 0.6), sim.TripGroups.Where(g => g.Home == 0).Sum(g => g.People));
        Assert.Equal((int)Math.Round(p1 * 0.6), sim.StatsOf(0).Population);
    }

    [Fact]
    public void ANewBuilding_BringsOnlyTheLoweredNumberOfPeople_AndNoMoreThanTheTownHasRoomFor()
    {
        int homes = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var (sim, _) = GrowthTests.BlockTown(GrowthTests.Quick(), seed, (BuildingType.Home, 2), null, (BuildingType.Home, 2), (BuildingType.Home, 2));
            sim.Towns[0].CapacityFactor = 0.5f;
            GrowthTests.Fill(sim, 0);

            GrowthTests.RunMonths(sim, 1.1);

            var born = sim.Buildings[^1];
            Assert.Equal(4, sim.Buildings.Count);
            var groups = sim.TripGroups.Where(g => g.Home == born.Id).ToList();
            if (born.Type == BuildingType.Home)
            {
                homes++;
                Assert.Equal((int)Math.Round(sim.TownTuning.Of(BuildingType.Home, 0).People * 0.5), groups.Sum(g => g.People));
            }
            else Assert.Empty(groups);
        }
        Assert.True(homes > 0);
    }

    [Fact]
    public void ATownThatLostPeopleToTheCallUp_DoesNotRegrowThemThroughGrowth()
    {
        // Eight homes, a wave of half, and then every building fed with construction stock every month: the town grows in buildings and sizes,
        // but its people stay at the share of what the buildings would hold that the call-up left.
        var tuning = Waves(GrowthTests.Quick(), (1, 0.5f));
        var (sim, _) = GrowthTests.BlockTown(tuning, Enumerable.Repeat<(BuildingType, int)?>((BuildingType.Home, 1), 8).ToArray());
        int before = sim.StatsOf(0).Population;
        GrowthTests.RunMonths(sim, 1.2);
        Assert.Equal(0.5f, sim.Towns[0].CapacityFactor, 0.0001f);
        Assert.InRange(sim.ResidentsByTown()[0], before * 0.5 - 12, before * 0.5 + 12);

        for (int month = 0; month < 4; month++)
        {
            foreach (var b in sim.Buildings.Where(b => b.State == BuildingState.Active).ToList()) GrowthTests.Fill(sim, b.Id);
            GrowthTests.RunMonths(sim, 1);
            var (nominal, _) = Nominal(sim);
            int residents = sim.ResidentsByTown()[0];
            Assert.InRange(residents, nominal * 0.5 - 14, nominal * 0.5 + 14);
            Assert.InRange(sim.StatsOf(0).Population, nominal * 0.5 - 1, nominal * 0.5 + 1);
        }
        Assert.True(sim.Buildings.Count > 8 || sim.Buildings.Any(b => b.Tier > 1), "the town did grow");
    }

    // ---- Raids ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ADestroyedHome_IsRebuiltAtTheLoweredCapacity_AndTheCasualtiesAreNotUndone()
    {
        int checkedHomes = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var tuning = Waves(GrowthTests.Quick(), (1, 0.5f));
            tuning = tuning with
            {
                Economy = tuning.Economy with { DayLength = 300f },
                War = tuning.War with { Bombing = tuning.War.Bombing with { BuildingChance = 1f } },
            };
            var (sim, lots) = GrowthTests.BlockTown(tuning, seed, Enumerable.Repeat<(BuildingType, int)?>((BuildingType.Home, 2), 8).ToArray());
            GrowthTests.RunMonths(sim, 1.2);
            Assert.Equal(0.5f, sim.Towns[0].CapacityFactor, 0.0001f);
            int residents = sim.ResidentsByTown()[0];

            var flight = sim.BombForTest(lots[0].X, lots[0].Z, seed);
            Assert.True(sim.Buildings[0].State == BuildingState.Demolished);
            long casualties = sim.Casualties;
            long day = sim.Day;
            while (sim.Day == day) sim.Step();
            Assert.True(sim.ResidentsByTown()[0] < residents);
            Assert.True(sim.StatsOf(0).Population <= (int)Math.Round(8 * 50 * 0.5)); // the room of the standing homes at half

            var survivor = sim.Buildings.Last(b => b.State == BuildingState.Active);
            GrowthTests.Fill(sim, survivor.Id);
            int now = sim.ResidentsByTown()[0];
            GrowthTests.RunMonths(sim, 1.1);

            var born = sim.Buildings[^1];
            Assert.Equal(casualties, sim.Casualties);
            if (born.Type != BuildingType.Home) continue;
            checkedHomes++;
            Assert.Equal(now + (int)Math.Round(sim.TownTuning.Of(BuildingType.Home, 0).People * 0.5), sim.ResidentsByTown()[0]);
        }
        Assert.True(checkedHomes > 0);
    }

    // ---- The state -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheCapacity_IsInTheStateHash_AndTheSameRunGivesTheSameHash()
    {
        ulong Run(float share)
        {
            var sim = ConscriptionTests.Town(Waves(ConscriptionTests.Quick(), (1, share)));
            ConscriptionTests.RunDays(sim, 3 * 1 + 2);
            return sim.StateHash();
        }

        Assert.Equal(Run(0.25f), Run(0.25f));
        Assert.NotEqual(Run(0.25f), Run(0.30f));
    }

    [Fact]
    public void WithNoWaveFired_TheCapacityDoesNotTouchTheHash_OrTheNumbers()
    {
        var sim = ConscriptionTests.Town(ConscriptionTests.Quick());
        ConscriptionTests.RunDays(sim, 3 * 3);

        Assert.Equal(1f, sim.Towns[0].CapacityFactor);
        Assert.Equal(Nominal(sim).People, sim.StatsOf(0).Population);
        Assert.Equal(Nominal(sim).Jobs, sim.StatsOf(0).Jobs);
    }

    [Fact]
    public void TheNewsOfTheCallUp_SaysWhatIsLeftOfTheRoom()
    {
        var sim = ConscriptionTests.Town(Waves(ConscriptionTests.Quick(), (1, 0.25f)));

        ConscriptionTests.RunDays(sim, 3 * 1 + 0.5);

        var news = Assert.Single(sim.Events, e => e.Kind == SimEventKind.War && e.Text.StartsWith("Call-up"));
        Assert.Contains("room for", news.Text);
        Assert.Contains("of the people and jobs they had", news.Text);
    }
}
