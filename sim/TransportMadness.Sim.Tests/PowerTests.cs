using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Electricity (C6): the free grid, plants that burn coal, power bought from outside, and a shortage that slows the industries.</summary>
public class PowerTests
{
    const int Forest = 0, Sawmill = 3, SteelMill = 9, PowerPlant = 15;
    const int Wood = 2, Planks = 3, Coal = 8;

    static Tuning WithMargin(float margin, Func<Tuning, Tuning>? more = null)
    {
        var t = Tuning.Load();
        t = t with { Power = t.Power with { FreeGridMargin = margin } };
        return more == null ? t : more(t);
    }

    static Simulation World(Tuning tuning, params IndustryPlan[] industries)
    {
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, 4, IncidentFrequency.Off);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1000, 0, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), industries));
        return sim;
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static IndustryPlan At(int type, float x) => new(type, x, 40, 0);

    [Fact]
    public void TheFreeGrid_CoversWhatTheIndustriesOfTheStartAsk_SoTheFirstGameRunsAtFullPower()
    {
        var sim = World(Tuning.Load(), At(Sawmill, 200), At(SteelMill, 500), At(Forest, 800));
        var types = sim.IndustryTuning.Types;

        sim.Step();

        float demand = types[Sawmill].Demand + types[SteelMill].Demand;
        Assert.Equal(demand, sim.PowerDemand, 0.001f);
        Assert.Equal(demand * sim.PowerTuning.FreeGridMargin, sim.FreeGrid, 0.001f);
        Assert.Equal(1f, sim.PowerFactor);
    }

    [Fact]
    public void AShortage_SlowsEveryIndustryInProportion()
    {
        double Produced(float margin)
        {
            var sim = World(WithMargin(margin), At(Forest, 200), At(Sawmill, 400));
            sim.Industries[1].Input[Wood] = 500f;
            RunDays(sim, 1.5);
            return sim.Industries[1].Produced;
        }

        double full = Produced(1.1f), half = Produced(0.5f);

        // The sawmill is the only demand (1 MW): a margin of 0.5 leaves half of it.
        Assert.Equal(0.5, half / full, 2);
    }

    [Fact]
    public void AShortage_ThenItsEnd_AreOnTheEventFeed()
    {
        var sim = World(WithMargin(0.5f), At(Sawmill, 400));
        sim.Step();
        Assert.Equal(0.5f, sim.PowerFactor, 3);
        var shortage = Assert.Single(sim.Events, e => e.Kind == SimEventKind.Power);
        Assert.Contains("shortage", shortage.Text);

        Assert.Equal(1, sim.Execute(new SetPowerPurchaseCommand(5)));
        sim.Step();

        Assert.Equal(1f, sim.PowerFactor);
        Assert.Equal(2, sim.Events.Count(e => e.Kind == SimEventKind.Power));
        Assert.Contains("full supply", sim.Events.Last(e => e.Kind == SimEventKind.Power).Text);
    }

    [Fact]
    public void APlantWithCoal_AddsItsMegawatts_AndBurnsCoalAtItsRate()
    {
        var sim = World(WithMargin(0.5f), At(Sawmill, 200), At(SteelMill, 500), At(PowerPlant, 800));
        var plant = sim.Industries[2];
        var type = sim.IndustryTuning.Types[PowerPlant];
        sim.Step();
        float without = sim.PowerSupply;
        Assert.Equal(0f, plant.Load);
        Assert.True(sim.PowerFactor < 1f);

        plant.Input[Coal] = 400f;
        sim.Step();
        sim.Step();

        Assert.Equal(1f, plant.Load, 0.001f);
        Assert.Equal(without + type.Supply, sim.PowerSupply, 0.01f);
        RunDays(sim, 3); // a month
        Assert.Equal(400f - type.PerMonth, plant.Input[Coal], 5f);
        Assert.Equal(0f, plant.Output);
    }

    [Fact]
    public void APlantThatRunsOutOfCoal_StopsGivingPower()
    {
        var sim = World(WithMargin(0.5f), At(SteelMill, 500), At(PowerPlant, 800));
        sim.Industries[1].Input[Coal] = 100f;
        RunDays(sim, 0.5);
        Assert.True(sim.PowerSupply > sim.FreeGrid + 15f);

        RunDays(sim, 3);

        Assert.Equal(0f, sim.Industries[1].Load);
        Assert.Equal(sim.FreeGrid, sim.PowerSupply, 0.001f);
    }

    [Fact]
    public void ThePlant_IsNeverReportedAsStopped_ForAFullOutput()
    {
        var sim = World(Tuning.Load(), At(PowerPlant, 800));
        sim.Industries[0].Input[Coal] = 100f;
        sim.Step();

        var status = sim.StatusOf(sim.Industries[0]);

        Assert.False(status.OutputFull);
        Assert.True(status.Working);
        Assert.Equal(-1, sim.OutputOf(PowerPlant));
    }

    [Fact]
    public void PowerBoughtFromOutside_IsChargedAtTheTurnOfEachMonth_AndCountsInTheSupply()
    {
        var sim = World(WithMargin(0.5f), At(SteelMill, 500));
        Assert.Equal(1, sim.Execute(new SetPowerPurchaseCommand(10)));
        long money = sim.Company.Money;
        sim.Step();

        Assert.Equal(sim.FreeGrid + 10f, sim.PowerSupply, 0.001f);
        Assert.Equal(money, sim.Company.Money);

        RunDays(sim, 3);

        long cost = 10 * sim.PowerTuning.PricePerMegawattMonth;
        Assert.Equal(-cost, sim.Company.Total(LedgerEntry.Power));
        RunDays(sim, 3);
        Assert.Equal(-2 * cost, sim.Company.Total(LedgerEntry.Power));
    }

    [Fact]
    public void ThePurchase_HasALimit_AndCanBeSetBackToNothing()
    {
        var sim = World(Tuning.Load(), At(SteelMill, 500));
        var t = sim.PowerTuning;

        Assert.Equal(0, sim.Execute(new SetPowerPurchaseCommand(t.MaxPurchase + 1)));
        Assert.Equal(0, sim.Execute(new SetPowerPurchaseCommand(-1)));
        Assert.Equal(1, sim.Execute(new SetPowerPurchaseCommand(t.MaxPurchase)));
        Assert.Equal(t.MaxPurchase * t.PricePerMegawattMonth, sim.PowerCostPerMonth);
        Assert.Equal(1, sim.Execute(new SetPowerPurchaseCommand(0)));
        Assert.Equal(0, sim.PowerCostPerMonth);
    }

    [Fact]
    public void WithoutIndustries_ThereIsNoDemandAndNoShortage()
    {
        var sim = World(Tuning.Load());
        sim.Step();

        Assert.Equal(0f, sim.PowerDemand);
        Assert.Equal(1f, sim.PowerFactor);
    }

    [Fact]
    public void ThePurchaseIsInTheStateHash()
    {
        ulong Run(int mw)
        {
            var sim = World(Tuning.Load(), At(SteelMill, 500));
            sim.Execute(new SetPowerPurchaseCommand(mw));
            RunDays(sim, 0.2);
            return sim.StateHash();
        }

        Assert.Equal(Run(5), Run(5));
        Assert.NotEqual(Run(5), Run(0));
    }

    [Theory]
    [InlineData(IndustryAmount.Few)]
    [InlineData(IndustryAmount.Normal)]
    [InlineData(IndustryAmount.Many)]
    public void EveryMapHasAPowerPlant_WithinReachOfTheCoalMineAndATown_OnMostMaps(IndustryAmount amount)
    {
        var tuning = Tuning.Load();
        var reach = tuning.Industries.Generation.ChainReach;
        int maps = 30, fitting = 0;
        for (ulong seed = 1; seed <= (ulong)maps; seed++)
        {
            var net = new RoadNetwork(Terrain.Generate(seed), tuning);
            var plan = IndustryGenerator.Generate(net, seed, TownGenerator.Generate(net, seed, 4), amount);
            var plant = plan.Industries!.Single(i => i.Type == PowerPlant);
            var coal = plan.Industries!.Single(i => i.Type == 7);
            float d = MathF.Sqrt(MathF.Pow(plant.X - coal.X, 2) + MathF.Pow(plant.Z - coal.Z, 2));
            if (d <= reach) fitting++;
        }

        Assert.True(fitting >= maps * 9 / 10, $"{fitting} of {maps}");
    }
}
