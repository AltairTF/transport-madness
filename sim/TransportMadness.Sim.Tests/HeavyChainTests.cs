using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The heavy chain of the campaign (C2): scarce coal and iron ore, steel, fuel from imported oil, and the military truck's parts.</summary>
public class HeavyChainTests
{
    const int Sawmill = 3, CoalMine = 7, IronMine = 8, SteelMill = 9, Refinery = 10, EngineWorks = 11, BodyWorks = 12, TyreWorks = 13, TruckPlant = 14;
    const int Planks = 3, Coal = 8, Ore = 9, Steel = 10, Fuel = 11, Oil = 12, Rubber = 13, Engines = 14, Bodies = 15, Tyres = 16, Trucks = 17;

    static (Simulation Sim, int Station) World(params IndustryPlan[] industries)
    {
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f));
        var sim = new Simulation(net, 4, IncidentFrequency.Off);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1000, 0, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), industries));
        int station = sim.Execute(new BuildStationCommand(500, 0));
        Assert.True(station >= 0);
        return (sim, station);
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    // ---- Where the deposits go -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(IndustryAmount.Few)]
    [InlineData(IndustryAmount.Normal)]
    [InlineData(IndustryAmount.Many)]
    public void EveryMapHasOneCoalDepositAndOneIronDeposit(IndustryAmount amount)
    {
        var tuning = Tuning.Load();
        for (ulong seed = 1; seed <= 25; seed++)
        {
            if (Terrain.CoastOf(seed) == CoastKind.Island) continue; // not much land on an island, so one of the deposits can miss
            var net = new RoadNetwork(Terrain.Generate(seed), tuning);
            var plan = IndustryGenerator.Generate(net, seed, TownGenerator.Generate(net, seed, 4), amount);

            Assert.Equal(1, plan.Industries!.Count(i => i.Type == CoalMine));
            Assert.Equal(1, plan.Industries!.Count(i => i.Type == IronMine));
        }
    }

    [Fact]
    public void TheDeposits_AreWithinReachOfATown_AndApartFromEachOther_OnMostMaps()
    {
        var tuning = Tuning.Load();
        var g = tuning.Industries.Generation;
        int maps = 40, fitting = 0;
        for (ulong seed = 1; seed <= (ulong)maps; seed++)
        {
            var net = new RoadNetwork(Terrain.Generate(seed), tuning);
            var plan = IndustryGenerator.Generate(net, seed, TownGenerator.Generate(net, seed, 4), IndustryAmount.Normal);
            var coal = plan.Industries!.Single(i => i.Type == CoalMine);
            var ore = plan.Industries!.Single(i => i.Type == IronMine);
            float apart = MathF.Sqrt(MathF.Pow(coal.X - ore.X, 2) + MathF.Pow(coal.Z - ore.Z, 2));
            bool near(IndustryPlan d) => plan.Towns.Any(t => MathF.Sqrt(MathF.Pow(t.X - d.X, 2) + MathF.Pow(t.Z - d.Z, 2)) <= g.DepositReach);
            bool fits = near(coal) && near(ore) && apart >= g.DepositMinSeparation && apart <= g.DepositMaxSeparation;
            if (fits) fitting++;
            else
                Assert.True(plan.Unlinked!.Any(k => plan.Industries![k].Type is CoalMine or IronMine), $"seed {seed}: a deposit out of place was not reported");
        }
        Assert.True(fitting >= maps * 3 / 4, $"only {fitting} of {maps} maps keep the deposits as designed");
    }

    [Fact]
    public void TheMillIsPlacedWithinReachOfBothMines_OnMostMaps()
    {
        var tuning = Tuning.Load();
        float reach = tuning.Industries.Generation.ChainReach;
        int near = 0, maps = 40;
        for (ulong seed = 1; seed <= (ulong)maps; seed++)
        {
            var net = new RoadNetwork(Terrain.Generate(seed), tuning);
            var plan = IndustryGenerator.Generate(net, seed, TownGenerator.Generate(net, seed, 4), IndustryAmount.Normal);
            var mill = plan.Industries!.First(i => i.Type == SteelMill);
            bool Reaches(int type) => plan.Industries!.Any(i => i.Type == type && MathF.Sqrt(MathF.Pow(i.X - mill.X, 2) + MathF.Pow(i.Z - mill.Z, 2)) <= reach);
            if (Reaches(CoalMine) && Reaches(IronMine)) near++;
        }
        Assert.True(near >= maps * 3 / 4, $"the mill reaches both mines on only {near} of {maps} maps");
    }

    [Fact]
    public void ARefineryIsNotReportedForWantingOilThatNobodyMakes()
    {
        // With every reach unlimited the only thing that could still be reported is an input without a producer on the map.
        var shipped = Tuning.Load();
        var g = shipped.Industries.Generation with { ChainReach = 1e6f, DepositReach = 1e6f, DepositMinSeparation = 0f, DepositMaxSeparation = 1e6f };
        var tuning = shipped with { Industries = shipped.Industries with { Generation = g } };
        var net = new RoadNetwork(Terrain.Generate(7), tuning);

        var plan = IndustryGenerator.Generate(net, 7, TownGenerator.Generate(net, 7, 4), IndustryAmount.Normal);

        Assert.Contains(plan.Industries!, i => i.Type == Refinery);
        Assert.Empty(plan.Unlinked!);
    }

    [Fact]
    public void TheTrucksChainIsNotMadeWithTheMap()
    {
        var net = new RoadNetwork(Terrain.Generate(5));
        var plan = IndustryGenerator.Generate(net, 5, TownGenerator.Generate(net, 5, 4), IndustryAmount.Many);

        Assert.DoesNotContain(plan.Industries!, i => i.Type is EngineWorks or BodyWorks or TyreWorks or TruckPlant);
    }

    [Fact]
    public void OilAndRubberAreImported_AndNothingElseIs()
    {
        var (sim, _) = World(new IndustryPlan(Refinery, 300, 30, 0));

        Assert.True(sim.IsImported(Oil));
        Assert.True(sim.IsImported(Rubber));
        Assert.All(new[] { 0, 1, 2, 3, 4, 5, 6, 7, Coal, Ore, Steel, Fuel, Engines, Bodies, Tyres, Trucks }, c => Assert.False(sim.IsImported(c), sim.CargoTypes[c].Id));
    }

    // ---- What each one makes -------------------------------------------------------------------------------------

    [Fact]
    public void TheMinesMakeCoalAndOreFromNothing()
    {
        var (sim, station) = World(new IndustryPlan(CoalMine, 480, 30, 0), new IndustryPlan(IronMine, 520, 30, 0));

        RunDays(sim, 1);

        Assert.True(sim.Stations[station].Waiting[Coal] > 0f);
        Assert.True(sim.Stations[station].Waiting[Ore] > 0f);
    }

    [Fact]
    public void SteelNeedsBothOreAndCoal_AtTheTunedRatio_AndStopsWhenOneRunsOut()
    {
        var (sim, _) = World(new IndustryPlan(SteelMill, 500, 30, 0));
        var mill = sim.Industries[0];
        var type = sim.IndustryTuning.Types[SteelMill];
        float oreUse = type.Inputs.Single(i => i.Cargo == "ore").Amount, coalUse = type.Inputs.Single(i => i.Cargo == "coal").Amount;
        mill.Input[Ore] = 100f;
        mill.Input[Coal] = 10f; // the scarce one: 10 / 0.5 = 20 steel at most

        RunDays(sim, 20);

        Assert.Equal(10f / coalUse, (float)mill.Produced, 0.5f);
        Assert.Equal(100f - (float)mill.Produced * oreUse, mill.Input[Ore], 0.5f);
        Assert.Equal(0f, mill.Input[Coal], 0.5f);
    }

    [Fact]
    public void ARefineryWithNoOilMakesNothing_AndWithOilMakesFuelOneForOne()
    {
        var (sim, station) = World(new IndustryPlan(Refinery, 500, 30, 0));
        RunDays(sim, 2);
        Assert.Equal(0.0, sim.Industries[0].Produced);

        sim.Industries[0].Input[Oil] = 30f;
        RunDays(sim, 10);

        Assert.Equal(30.0, sim.Industries[0].Produced, 1);
        Assert.Equal(30f, sim.Stations[station].Waiting[Fuel], 1);
    }

    [Fact]
    public void AnEngineNeedsSteelAndFuel_ABodyNeedsSteelAndPlanks_AndTyresComeFromRubber()
    {
        var (sim, _) = World(new IndustryPlan(EngineWorks, 300, 30, 0), new IndustryPlan(BodyWorks, 500, 30, 0), new IndustryPlan(TyreWorks, 700, 30, 0));
        sim.Industries[0].Input[Steel] = 10f; // fuel 0.5 each: 4 fuel is enough for 8 engines
        sim.Industries[0].Input[Fuel] = 4f;
        sim.Industries[1].Input[Steel] = 10f; // planks 1 each: 3 is enough for 3 bodies
        sim.Industries[1].Input[Planks] = 3f;
        sim.Industries[2].Input[Rubber] = 5f;

        RunDays(sim, 30);

        Assert.Equal(8.0, sim.Industries[0].Produced, 0.5);
        Assert.Equal(3.0, sim.Industries[1].Produced, 0.5);
        Assert.Equal(5.0, sim.Industries[2].Produced, 0.5);
    }

    [Fact]
    public void ATruckNeedsAnEngineABodyAndTyres_AndTyresRunOutFirst()
    {
        var (sim, _) = World(new IndustryPlan(TruckPlant, 500, 30, 0));
        var plant = sim.Industries[0];
        var type = sim.IndustryTuning.Types[TruckPlant];
        float tyresPerTruck = type.Inputs.Single(i => i.Cargo == "tyres").Amount;
        plant.Input[Engines] = 10f;
        plant.Input[Bodies] = 10f;
        plant.Input[Tyres] = 8f * tyresPerTruck / 4f; // tyres for 2 trucks only

        RunDays(sim, 60);

        Assert.Equal(2.0, plant.Produced, 0.2);
        Assert.Equal(8f, plant.Input[Engines], 0.5f); // 2 of 10 used
    }

    // ---- What the Industries window says --------------------------------------------------------------------------

    [Fact]
    public void TheStatus_SaysWhatIsMissing_WhetherAStationCoversIt_AndWhetherItHasStopped()
    {
        var (sim, _) = World(new IndustryPlan(SteelMill, 500, 30, 0), new IndustryPlan(Sawmill, 900, 30, 0));
        var mill = sim.Industries[0];
        mill.Input[Ore] = 5f;

        var status = sim.StatusOf(mill);
        Assert.True(status.Covered);
        Assert.False(status.Working);
        Assert.Equal(new[] { Coal }, status.MissingInputs);

        mill.Input[Coal] = 5f;
        Assert.True(sim.StatusOf(mill).Working);

        Assert.False(sim.StatusOf(sim.Industries[1]).Covered); // no station within reach of the sawmill

        mill.Input[Ore] = 0f; mill.Input[Coal] = 0f;
        Assert.Equal(new[] { Ore, Coal }.OrderBy(c => c), sim.StatusOf(mill).MissingInputs.OrderBy(c => c));
    }

    [Fact]
    public void AnIndustryWhoseOutputIsFull_IsReportedAsStopped()
    {
        var (sim, _) = World(new IndustryPlan(CoalMine, 900, 30, 0)); // 400 m from the only station: not covered, so the output piles up
        RunDays(sim, 8);

        var status = sim.StatusOf(sim.Industries[0]);
        Assert.True(status.OutputFull);
        Assert.False(status.Covered);
        Assert.False(status.Working);
    }
}
