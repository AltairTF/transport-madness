using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The company's money: what commands cost, the loan, the daily accounts.</summary>
public class EconomyTests
{
    static Simulation NewSim(Tuning? tuning = null) => new(new RoadNetwork(Terrain.Flat(), tuning), 1);

    static void RunDays(Simulation sim, int days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    [Fact]
    public void TheCompanyStartsWithTheTunedMoney_AndNoLoan()
    {
        var sim = NewSim();

        Assert.Equal(sim.Network.Tuning.Economy.StartingMoney, sim.Company.Money);
        Assert.Equal(0, sim.Company.Loan);
        Assert.False(sim.Company.Bankrupt);
    }

    [Fact]
    public void BuildingARoad_CostsWhatTheEstimateSays()
    {
        var sim = NewSim();
        var estimate = sim.Network.EstimateCost(0, 0, 500, 0, RoadType.TwoLane)!.Value;
        long before = sim.Company.Money;

        sim.Execute(new BuildRoadCommand(0, 0, 500, 0, RoadType.TwoLane, 14f));

        Assert.Equal(before - (long)Math.Ceiling(estimate.Money), sim.Company.Money);
        Assert.Equal(sim.Company.Money - before, sim.Company.Total(LedgerEntry.Construction));
    }

    [Fact]
    public void ACommandThatCostsMoreThanTheCompanyHas_IsRefusedAndChangesNothing()
    {
        var shipped = Tuning.Load();
        var sim = NewSim(shipped with { Economy = shipped.Economy with { StartingMoney = 10000 } });

        int built = sim.Execute(new BuildRoadCommand(0, 0, 500, 0, RoadType.TwoLane, 14f)); // 20000

        Assert.Equal(0, built);
        Assert.Equal(0, sim.Network.RoadCount);
        Assert.Equal(10000, sim.Company.Money);
        Assert.Equal(-1, sim.Execute(new AddDepotCommand(0, DepotLevel.Simple)));
    }

    [Fact]
    public void EveryRoadCommand_ChargesItsPrice()
    {
        var sim = NewSim();
        sim.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(400, 0, 800, 0, RoadType.TwoLane, 14f));
        var net = sim.Network;

        long m = sim.Company.Money;
        Assert.Equal(1, sim.Execute(new UpgradeRoadCommand(0, 19f)));
        Assert.True(sim.Company.Money < m);

        m = sim.Company.Money;
        Assert.Equal(1, sim.Execute(new SetSpeedLimitCommand(1, 10f)));
        Assert.True(sim.Company.Money < m);

        m = sim.Company.Money;
        int node = net.FindNodeNear(0, 0, 1f);
        Assert.Equal(1, sim.Execute(new MoveNodeCommand(node, 0, 60)));
        Assert.True(sim.Company.Money < m);

        m = sim.Company.Money;
        int removable = net.RoadsAt(net.FindNodeNear(800, 0, 1f))[0].Value;
        Assert.Equal(1, sim.Execute(new RemoveRoadCommand(removable)));
        Assert.True(sim.Company.Money < m);
        Assert.Equal(0, sim.Execute(new RemoveRoadCommand(removable))); // already gone: refused, and free
    }

    [Fact]
    public void ADepot_CostsByLevel()
    {
        var sim = NewSim();
        sim.Execute(new BuildRoadCommand(0, 0, 400, 0, RoadType.TwoLane, 14f));
        long before = sim.Company.Money;

        Assert.Equal(0, sim.Execute(new AddDepotCommand(0, DepotLevel.Complex)));

        Assert.Equal(before - sim.Network.Tuning.Economy.DepotPrice.Complex, sim.Company.Money);
    }

    [Fact]
    public void ALoan_AddsMoneyUpToTheLimit_AndIsPaidBack()
    {
        var sim = NewSim();
        long start = sim.Company.Money;
        long limit = sim.Network.Tuning.Economy.MaxLoan;

        Assert.Equal(1, sim.Execute(new TakeLoanCommand(limit / 2)));
        Assert.Equal(start + limit / 2, sim.Company.Money);
        Assert.Equal(0, sim.Execute(new TakeLoanCommand(limit)));            // over the limit
        Assert.Equal(0, sim.Execute(new RepayLoanCommand(limit)));           // more than is owed
        Assert.Equal(1, sim.Execute(new RepayLoanCommand(limit / 4)));
        Assert.Equal(limit / 4, sim.Company.Loan);
        Assert.Equal(start + limit / 4, sim.Company.Money);
        Assert.Equal(0, sim.Execute(new TakeLoanCommand(-5)));
    }

    [Fact]
    public void InterestIsChargedEveryDay_OnTheLoanOnly()
    {
        var sim = NewSim();
        RunDays(sim, 1);
        Assert.Equal(0, sim.Company.Total(LedgerEntry.Interest)); // no loan, no interest

        sim.Execute(new TakeLoanCommand(500000));
        RunDays(sim, 1);

        Assert.Equal(-(long)Math.Ceiling(500000 * sim.Network.Tuning.Economy.InterestPerDay), sim.Company.Total(LedgerEntry.Interest));
    }

    [Fact]
    public void AfterTooLongInDebt_TheCompanyGoesBankrupt_ButTheSimulationCarriesOn()
    {
        var shipped = Tuning.Load();
        var tuning = shipped with { Economy = shipped.Economy with { StartingMoney = 0, DebtLimit = 0, BankruptcyDays = 5, MaxLoan = 1000000 } };
        var sim = NewSim(tuning);
        sim.Execute(new TakeLoanCommand(100000));

        RunDays(sim, 4);
        Assert.False(sim.Company.Bankrupt);

        // Money 100000, loan 100000: net worth 0. Interest pushes it below zero every day.
        RunDays(sim, 3);

        Assert.True(sim.Company.Bankrupt);
        Assert.True(sim.Company.InsolventDays >= 5);
        sim.Step();
    }

    [Fact]
    public void AStationFollowsItsNodeWhenItIsMoved()
    {
        var sim = NewSim();
        sim.Execute(new BuildRoadCommand(0, 0, 800, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildStationCommand(0, 0));
        int node = sim.Stations[0].Node;

        Assert.Equal(1, sim.Execute(new MoveNodeCommand(node, 0, 80)));

        Assert.True(sim.Stations[0].Active);
        Assert.NotEqual(node, sim.Stations[0].Node);
        var (x, z) = sim.Network.GetNode(sim.Stations[0].Node);
        Assert.Equal((0f, 80f), (x, z));
    }

    [Theory]
    [InlineData(0, 1938, 1, 1)]
    [InlineData(2, 1938, 1, 3)]
    [InlineData(3, 1938, 2, 1)]
    [InlineData(35, 1938, 12, 3)]
    [InlineData(36, 1939, 1, 1)]
    [InlineData(287, 1945, 12, 3)]
    public void TheCalendar_IsDerivedFromTheDayCount(long day, int year, int month, int dayOfMonth)
    {
        var date = NewSim().DateOfDay(day);

        Assert.Equal(new Simulation.Date(year, month, dayOfMonth), date);
    }

    [Fact]
    public void TimeOfDay_RunsFromMidnightAndWrapsAtTheTurnOfTheDay()
    {
        var sim = NewSim();
        Assert.Equal(0f, sim.TimeOfDay);
        long ticksPerDay = (long)Math.Round(sim.DayLengthSeconds / Simulation.StepSeconds);

        for (long t = 0; t < ticksPerDay / 2; t++) sim.Step();
        Assert.Equal(0.5f, sim.TimeOfDay, 3);

        for (long t = 0; t < ticksPerDay / 2; t++) sim.Step();
        Assert.Equal(0f, sim.TimeOfDay);
        Assert.Equal(1, sim.Day);
    }
}
