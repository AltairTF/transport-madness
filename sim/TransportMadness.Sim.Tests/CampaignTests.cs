using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The campaign's books (C4): the monthly allocation, the standing, dismissal and the final grade.</summary>
public class CampaignTests
{
    const int Steel = 10;

    static Simulation NewSim(Func<Tuning, Tuning>? change = null)
    {
        var tuning = Tuning.Load();
        if (change != null) tuning = change(tuning);
        return new Simulation(new RoadNetwork(Terrain.Flat(), tuning), 1);
    }

    static Tuning With(Tuning t, Func<CampaignTuning, CampaignTuning> change) => t with { Campaign = change(t.Campaign) };

    static void RunDays(Simulation sim, int days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static void RunMonths(Simulation sim, int months) => RunDays(sim, months * sim.Network.Tuning.Economy.DaysPerMonth);

    // ---- The phases ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "rearmament")]
    [InlineData(19, "rearmament")]
    [InlineData(20, "mobilisation")]
    [InlineData(47, "mobilisation")]
    [InlineData(48, "totalwar")]
    [InlineData(83, "totalwar")]
    [InlineData(84, "end")]
    [InlineData(95, "end")]
    public void ThePhasesFollowTheCalendar(int month, string expected) =>
        Assert.Equal(expected, Tuning.Load().Campaign.PhaseOf(month).Id);

    [Fact]
    public void TheCampaignRunsNinetySixMonths_FromJanuary1938()
    {
        var t = Tuning.Load();

        Assert.Equal(96, t.Campaign.Months);
        Assert.Equal(96, t.Economy.MonthsPerYear * 8);
        Assert.Equal(1938, t.Economy.StartYear);
        Assert.Equal("Veldmar", t.Campaign.Country);
    }

    // ---- The allocation --------------------------------------------------------------------------------------------

    [Fact]
    public void TheAllocationIsPaidAtTheTurnOfEachMonth_NotBefore()
    {
        var sim = NewSim();
        long start = sim.Company.Money;

        RunDays(sim, 2);
        Assert.Equal(start, sim.Company.Money);

        RunDays(sim, 1);

        var report = sim.LastMonth!.Value;
        Assert.Equal(0, report.Month);
        Assert.True(report.Allocation > 0);
        Assert.Equal(start + report.Allocation, sim.Company.Money);
        Assert.Equal(report.Allocation, sim.Company.Total(LedgerEntry.Allocation));
    }

    [Fact]
    public void TheAllocation_IsThePhasesAmountTimesTheFactorOfTheStanding()
    {
        var sim = NewSim();

        RunMonths(sim, 1);

        var report = sim.LastMonth!.Value;
        var campaign = sim.CampaignTuning;
        Assert.Equal((long)MathF.Round(campaign.PhaseOf(1).Allocation * campaign.AllocationFactor(report.StandingAfter)), report.Allocation);
    }

    [Fact]
    public void AHigherStandingBringsABiggerAllocation()
    {
        var low = NewSim(t => With(t, c => c with { StartStanding = 10f }));
        var high = NewSim(t => With(t, c => c with { StartStanding = 90f }));

        Assert.True(high.NextAllocation > low.NextAllocation * 2);
        Assert.Equal(1.0f, high.CampaignTuning.AllocationFactor(50f), 3);
    }

    [Fact]
    public void ThePhaseOfTheNextMonthSetsTheAllocation()
    {
        var sim = NewSim(t => With(t, c => c with { Phases = new[] { c.Phases[0], c.Phases[1] with { FromMonth = 1 } } }));

        RunMonths(sim, 1);

        var report = sim.LastMonth!.Value;
        Assert.Equal((long)MathF.Round(sim.CampaignTuning.Phases[1].Allocation * sim.CampaignTuning.AllocationFactor(report.StandingAfter)), report.Allocation);
    }

    // ---- The standing ----------------------------------------------------------------------------------------------

    [Fact]
    public void WithNothingDelivered_TheStandingSlipsBySomeOfThePhasesExpectation()
    {
        var sim = NewSim();
        float start = sim.Company.Standing;
        float expected = sim.CampaignTuning.PhaseOf(0).ExpectedValue / sim.CampaignTuning.ExportScale;

        RunMonths(sim, 1);

        Assert.Equal(start - expected, sim.Company.Standing, 3);
        Assert.Equal(-expected, sim.LastMonth!.Value.ExportTerm, 3);
        Assert.Equal(new[] { sim.Company.Standing }, sim.StandingHistory);
    }

    [Fact]
    public void AMonthThatEndsInDeficit_CostsStanding()
    {
        var sim = NewSim(t => With(t, c => c with { Phases = new[] { c.Phases[0] with { ExpectedValue = 0f } } }));
        sim.Company.Money = -1000;

        RunMonths(sim, 1);

        var report = sim.LastMonth!.Value;
        Assert.Equal(-sim.CampaignTuning.DeficitPenalty, report.DeficitTerm);
        Assert.Equal(sim.CampaignTuning.StartStanding - sim.CampaignTuning.DeficitPenalty, sim.Company.Standing, 3);
    }

    [Fact]
    public void TheStandingStaysBetweenZeroAndAHundred()
    {
        var high = NewSim(t => With(t, c => c with { StartStanding = 100f, Phases = new[] { c.Phases[0] with { ExpectedValue = -1e6f } } }));
        var low = NewSim(t => With(t, c => c with { StartStanding = 0f, DismissMonths = 100 }));

        RunMonths(high, 2);
        RunMonths(low, 2);

        Assert.Equal(100f, high.Company.Standing);
        Assert.Equal(0f, low.Company.Standing);
    }

    [Fact]
    public void WhatIsDeliveredToAGatewayRaisesTheStanding()
    {
        var tuning = With(Tuning.Load(), c => c with { ExportScale = 100f, Phases = new[] { c.Phases[0] with { ExpectedValue = 0f } } });
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var sim = new Simulation(net, 4, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(0, 0, 1000, 0, RoadType.TwoLane, 14f));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), Array.Empty<IndustryPlan>(), null, new[] { new GatewayPlan(GatewayKind.Road, "Test Road Gate", 700, 30, 0) }));
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(300, 0)), b = sim.Execute(new BuildStationCommand(700, 0));
        sim.Stations[a].Waiting[Steel] = 100f;
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        sim.Execute(new BuyVehicleCommand(depot, "truck", line));

        RunMonths(sim, 1);

        var report = sim.LastMonth!.Value;
        double steelValue = tuning.Cargo.Single(c => c.Id == "steel").PayPerUnitKm;
        Assert.True(report.ExportValue >= 20 * steelValue, $"delivered value {report.ExportValue}");
        Assert.Equal(report.ExportValue, sim.Company.DeliveredValue, 3);
        Assert.True(report.ExportTerm > 0f);
        Assert.True(sim.Company.Standing > tuning.Campaign.StartStanding);
        Assert.Equal(0.0, sim.ExportValueThisMonth, 3); // a new month starts at nothing
    }

    // ---- Dismissal -------------------------------------------------------------------------------------------------

    [Fact]
    public void SixMonthsUnderTheLine_DismissTheCommittee_AndTheAllocationStops()
    {
        var sim = NewSim(t => With(t, c => c with { StartStanding = 10f }));

        RunMonths(sim, 5);
        Assert.False(sim.Company.Dismissed);
        Assert.Equal(5, sim.Company.LowStandingMonths);
        Assert.Equal(1, sim.MonthsToDismissal);
        long allocated = sim.Company.Total(LedgerEntry.Allocation);

        RunMonths(sim, 1);

        Assert.True(sim.Company.Dismissed);
        Assert.True(sim.Company.CampaignOver);
        Assert.False(sim.Company.Finished);
        Assert.Equal(-1, sim.Company.Grade);
        Assert.Equal(allocated, sim.Company.Total(LedgerEntry.Allocation));
        Assert.Equal(0, sim.LastMonth!.Value.Allocation);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Standing && e.Severity == EventSeverity.Serious && e.Text.Contains("dismissed"));

        long money = sim.Company.Money;
        RunMonths(sim, 2);
        Assert.Equal(money, sim.Company.Money); // nothing more is granted
        Assert.True(sim.Company.Dismissed);
    }

    [Fact]
    public void AMonthAboveTheLine_ClearsTheCount()
    {
        var sim = NewSim(t => With(t, c => c with { StartStanding = 10f }));
        RunMonths(sim, 2);
        Assert.Equal(2, sim.Company.LowStandingMonths);

        sim.Company.Standing = 80f;
        RunMonths(sim, 1);

        Assert.Equal(0, sim.Company.LowStandingMonths);
        Assert.Equal(0, sim.MonthsToDismissal);
    }

    [Fact]
    public void ALowStanding_IsWarnedAboutOnTheEventFeed()
    {
        var sim = NewSim(t => With(t, c => c with { StartStanding = 25f }));

        RunMonths(sim, 1);

        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Standing && e.Severity == EventSeverity.Warning);
    }

    // ---- The grade -------------------------------------------------------------------------------------------------

    [Fact]
    public void AfterTheLastMonth_TheCampaignIsFinished_WithAGradeFromTheScore()
    {
        var sim = NewSim(t => With(t, c => c with { Months = 3 }));
        var grade = sim.CampaignTuning.Grade;

        RunMonths(sim, 2);
        Assert.False(sim.Company.Finished);
        sim.Company.DeliveredValue = grade.ProductionTarget * 2; // more than full marks counts as full marks
        RunMonths(sim, 1);

        Assert.True(sim.Company.Finished);
        Assert.True(sim.Company.CampaignOver);
        Assert.False(sim.Company.Dismissed);
        float expected = grade.StandingWeight * sim.Company.Standing + grade.ProductionWeight * 100f;
        Assert.Equal(expected, sim.Company.Score, 2);
        Assert.Equal(1, sim.Company.Grade); // about 60: above 55, under 75
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Standing && e.Text.Contains(grade.Names[1]));
        Assert.Equal(0, sim.LastMonth!.Value.Allocation);
    }

    [Theory]
    [InlineData(0.0, 3)]
    [InlineData(1.0, 1)]
    public void TheProductionDeliveredMovesTheGrade(double shareOfTarget, int expectedGrade)
    {
        var sim = NewSim(t => With(t, c => c with { Months = 1 }));
        sim.Company.DeliveredValue = sim.CampaignTuning.Grade.ProductionTarget * shareOfTarget;

        RunMonths(sim, 1);

        Assert.Equal(expectedGrade, sim.Company.Grade);
    }

    [Fact]
    public void TheTownsSatisfactionCountsTowardTheScore()
    {
        float Score(float satisfaction)
        {
            var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), With(Tuning.Load(), c => c with { Months = 1 }));
            var sim = new Simulation(net, 1);
            sim.AddTowns(new WorldPlan(1, new[] { new TownPlan("Testville", 100, 0, 0, 1, Array.Empty<BuildingPlan>()) }));
            sim.Towns[0].Satisfaction = satisfaction;
            RunMonths(sim, 1);
            return sim.Company.Score;
        }

        Assert.True(Score(100f) > Score(0f) + 15f);
    }

    // ---- State -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStanding_IsPartOfTheHash_AndTwoRunsAgree()
    {
        ulong Run(float standing)
        {
            var sim = NewSim(t => With(t, c => c with { StartStanding = standing }));
            RunMonths(sim, 2);
            return sim.StateHash();
        }

        Assert.Equal(Run(60f), Run(60f));
        Assert.NotEqual(Run(60f), Run(50f));
    }
}
