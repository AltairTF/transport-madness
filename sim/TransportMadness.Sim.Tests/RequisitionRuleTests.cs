using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The rule by which the army picks (C15b): chosen in the month of notice, one wave at a time.</summary>
public class RequisitionRuleTests
{
    /// <summary>
    /// The shipped tuning with every model on offer from the start and the models given years of their own, so that some are older than others:
    /// the bus 1930, the light truck 1932, the heavy truck and the large bus 1938, the steam locomotive 1930 and the diesel 1938.
    /// </summary>
    static Tuning Quick(params (int Month, float Share)[] waves) => Aged(RequisitionTests.Quick(r => RequisitionTests.Waves(r, waves)), bus: 1930, truck: 1932);

    static Tuning Aged(Tuning t, int bus, int truck) => t with
    {
        Vehicles = t.Vehicles.Select(v => v with { FromYear = v.Id switch { "bus" => bus, "truck" => truck, _ => 1938 } }).ToList(),
        Rail = t.Rail with { Locomotives = t.Rail.Locomotives.Select(l => l with { FromYear = l.Id == "steam" ? 1930 : 1938 }).ToList() },
    };

    const double Notice = 3 * 2 + 0.5;  // month 2 has begun: a wave in month 3 is announced
    const double Taken = 3 * 3 + 0.5;   // month 3 has begun: it has come

    /// <summary>Four vehicles in a depot's garage: a heavy truck (1940), a light truck (1938), a bus (1938) and a large bus (1941); the army takes half.</summary>
    static (Simulation Sim, int Depot) Garaged(Tuning tuning)
    {
        var (sim, depot, _) = RequisitionTests.Fleet(tuning, buses: 0, trucks: 0);
        Assert.Equal(0, sim.Execute(new BuyParkedVehicleCommand(depot, "heavytruck")));
        Assert.Equal(1, sim.Execute(new BuyParkedVehicleCommand(depot, "truck")));
        Assert.Equal(2, sim.Execute(new BuyParkedVehicleCommand(depot, "bus")));
        Assert.Equal(3, sim.Execute(new BuyParkedVehicleCommand(depot, "bigbus")));
        return (sim, depot);
    }

    static int[] Left(Simulation sim) => sim.Garage.Where(p => p.Active).Select(p => p.Id).ToArray();

    // ---- The rules ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultRule_IsFreightFirstAndTheBiggest_AsBefore()
    {
        var (sim, _) = Garaged(Quick((3, 0.5f)));
        Assert.Equal(RequisitionRule.FreightFirst, sim.RequisitionRule);

        RequisitionTests.RunDays(sim, Taken);

        Assert.Equal(new[] { 2, 3 }, Left(sim)); // the heavy truck and the light truck went
    }

    [Fact]
    public void TheOldestFirst_TakesTheOldestModels()
    {
        var (sim, _) = Garaged(Quick((3, 0.5f)));
        RequisitionTests.RunDays(sim, Notice);
        Assert.Equal(1, sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.OldestFirst)));

        RequisitionTests.RunDays(sim, Taken - Notice);

        Assert.Equal(new[] { 0, 3 }, Left(sim)); // the bus (1930) and the light truck (1932) went; the newer ones stay
        Assert.Equal((long)Math.Round((28000 + 24000) * 0.8), sim.Company.Total(LedgerEntry.Requisition));
    }

    [Fact]
    public void TheOldestFirst_TakesFreightBeforePeopleAmongModelsOfTheSameYear()
    {
        var (sim, _) = Garaged(Aged(RequisitionTests.Quick(r => RequisitionTests.Waves(r, (3, 0.25f))), bus: 1932, truck: 1932));
        RequisitionTests.RunDays(sim, Notice);
        sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.OldestFirst));

        RequisitionTests.RunDays(sim, Taken - Notice);

        Assert.Equal(new[] { 0, 2, 3 }, Left(sim)); // the light truck, not the bus of the same year
    }

    [Fact]
    public void PassengersFirst_TakesThePeopleCarriersBeforeFreight_TheBiggestFirst()
    {
        var (sim, _) = Garaged(Quick((3, 0.5f)));
        RequisitionTests.RunDays(sim, Notice);
        Assert.Equal(1, sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.PassengersFirst)));

        RequisitionTests.RunDays(sim, Taken - Notice);

        Assert.Equal(new[] { 0, 1 }, Left(sim)); // the large bus and the bus went
        Assert.Equal((long)Math.Round((36000 + 24000) * 0.8), sim.Company.Total(LedgerEntry.Requisition)); // the price is still 80 % of new
    }

    [Fact]
    public void PassengersFirst_OnTheLines_TakesTheBusesAndLeavesTheTrucks()
    {
        var (sim, _, _) = RequisitionTests.Fleet(Quick((3, 0.5f)));
        var trucks = Enumerable.Range(0, sim.VehicleCount).Where(i => sim.IsCompanyVehicle(i) && sim.VehicleModels[sim.GetFleetInfo(i).Model].Id == "truck").ToList();
        RequisitionTests.RunDays(sim, Notice);
        sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.PassengersFirst));

        RequisitionTests.RunDays(sim, Taken - Notice);

        Assert.Equal(5, sim.Company.Requisitioned);
        Assert.All(trucks, t => Assert.True(sim.IsCompanyVehicle(t))); // the default would have taken all four
        Assert.Equal(5, RequisitionTests.Count(sim));
        Assert.Equal((long)(5 * Math.Round(24000 * 0.8)), sim.Company.Total(LedgerEntry.Requisition)); // six buses, five of them
    }

    [Fact]
    public void ATrainOfAnOlderLocomotive_GoesFirstWhenTheOldestAreTaken()
    {
        var tuning = Quick((3, 0.5f));
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning), 5, IncidentFrequency.Off);
        Assert.True(sim.Execute(new BuildTrackCommand(0, 300, 300, 300)) > 0);
        int rdepot = sim.Execute(new AddRailDepotCommand(300, 300));
        sim.Execute(new TakeLoanCommand(1000000));
        int diesel = sim.Execute(new BuyParkedTrainCommand(rdepot, "diesel", "coach", 2));
        int steam = sim.Execute(new BuyParkedTrainCommand(rdepot, "steam", "coach", 2));
        Assert.True(diesel >= 0 && steam >= 0);
        RequisitionTests.RunDays(sim, Notice);
        sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.OldestFirst));

        RequisitionTests.RunDays(sim, Taken - Notice);

        Assert.False(sim.Garage[steam].Active);
        Assert.True(sim.Garage[diesel].Active);
    }

    // ---- When it can be chosen -------------------------------------------------------------------------------------

    [Fact]
    public void TheRule_CannotBeChosenBeforeTheMonthOfNotice_OrWithNoWaveAnnounced()
    {
        var (sim, _) = Garaged(Quick((3, 0.5f)));

        Assert.False(sim.CanChooseRequisitionRule);
        Assert.Equal(0, sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.OldestFirst)));
        Assert.Equal(RequisitionRule.FreightFirst, sim.RequisitionRule);
        RequisitionTests.RunDays(sim, 3 * 1 + 0.5); // month 1: still two months ahead
        Assert.Equal(0, sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.PassengersFirst)));

        var (none, _) = Garaged(Quick());
        RequisitionTests.RunDays(none, Notice);
        Assert.False(none.CanChooseRequisitionRule);
        Assert.Equal(0, none.Execute(new SetRequisitionRuleCommand(RequisitionRule.OldestFirst)));
    }

    [Fact]
    public void TheRule_CannotBeChangedOnceTheWaveHasFired()
    {
        var (sim, _) = Garaged(Quick((3, 0.5f)));
        RequisitionTests.RunDays(sim, Notice);
        Assert.True(sim.CanChooseRequisitionRule);
        RequisitionTests.RunDays(sim, Taken - Notice);

        Assert.False(sim.CanChooseRequisitionRule);
        Assert.Equal(0, sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.OldestFirst)));
        Assert.Equal(RequisitionRule.FreightFirst, sim.RequisitionRule);
    }

    [Fact]
    public void TheLastChoiceInTheNoticeMonthCounts_AndAnUnknownRuleIsRefused()
    {
        var (sim, _) = Garaged(Quick((3, 0.5f)));
        RequisitionTests.RunDays(sim, Notice);

        Assert.Equal(1, sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.OldestFirst)));
        Assert.Equal(1, sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.PassengersFirst)));
        Assert.Equal(0, sim.Execute(new SetRequisitionRuleCommand((RequisitionRule)9)));
        Assert.Equal(RequisitionRule.PassengersFirst, sim.RequisitionRule);

        RequisitionTests.RunDays(sim, Taken - Notice);
        Assert.Equal(new[] { 0, 1 }, Left(sim));
    }

    [Fact]
    public void TheChoice_IsForOneWaveOnly_AndTheNextWaveStartsAtTheDefault()
    {
        var (sim, depot) = Garaged(Quick((3, 0.25f), (5, 0.25f)));
        RequisitionTests.RunDays(sim, Notice);
        sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.PassengersFirst));
        RequisitionTests.RunDays(sim, Taken - Notice);
        Assert.Equal(new[] { 0, 1, 2 }, Left(sim)); // the large bus
        Assert.Equal(RequisitionRule.FreightFirst, sim.RequisitionRule);

        RequisitionTests.RunDays(sim, 3 * 2 - 0.0); // month 5: the second wave comes with the default
        Assert.Equal(new[] { 1, 2 }, Left(sim)); // the heavy truck (the default)
        Assert.Null(sim.NextRequisition);
        Assert.Equal(2, sim.Company.Requisitioned);
    }

    [Fact]
    public void ALaterWave_CanBeGivenItsOwnRule()
    {
        var (sim, _) = Garaged(Quick((3, 0.25f), (5, 0.25f)));
        RequisitionTests.RunDays(sim, Taken); // the default for the first: the heavy truck
        RequisitionTests.RunDays(sim, 3 * 1); // month 4 has begun: the second is announced
        Assert.True(sim.CanChooseRequisitionRule);
        Assert.Equal(1, sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.OldestFirst)));

        RequisitionTests.RunDays(sim, 3 * 1);

        Assert.Equal(new[] { 1, 3 }, Left(sim)); // the bus (1930), the oldest of the three that were left
    }

    [Fact]
    public void TheNotice_SaysWhichRuleItIsUnder()
    {
        var (sim, _) = Garaged(Quick((3, 0.5f)));
        RequisitionTests.RunDays(sim, Notice);

        var notice = Assert.Single(sim.Events, e => e.Kind == SimEventKind.War && e.Text.StartsWith("The army will requisition"));
        Assert.Contains("freight first", notice.Text);
        Assert.Equal("the oldest first", Simulation.RuleText(RequisitionRule.OldestFirst));
        Assert.Equal("passengers first", Simulation.RuleText(RequisitionRule.PassengersFirst));
    }

    // ---- The state -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRule_IsInTheStateHash_AndTheSameChoicesGiveTheSameRun()
    {
        ulong Run(RequisitionRule? rule, double days)
        {
            var (sim, _) = Garaged(Quick((3, 0.5f)));
            RequisitionTests.RunDays(sim, Notice);
            if (rule is { } r) sim.Execute(new SetRequisitionRuleCommand(r));
            RequisitionTests.RunDays(sim, days);
            return sim.StateHash();
        }

        // Chosen, not yet fired: the hash differs from the default's.
        Assert.Equal(Run(RequisitionRule.OldestFirst, 1), Run(RequisitionRule.OldestFirst, 1));
        Assert.NotEqual(Run(null, 1), Run(RequisitionRule.OldestFirst, 1));
        Assert.NotEqual(Run(RequisitionRule.OldestFirst, 1), Run(RequisitionRule.PassengersFirst, 1));
        // After the wave: a different fleet is left.
        Assert.NotEqual(Run(null, Taken - Notice), Run(RequisitionRule.PassengersFirst, Taken - Notice));
        Assert.Equal(Run(RequisitionRule.PassengersFirst, Taken - Notice), Run(RequisitionRule.PassengersFirst, Taken - Notice));
    }

    [Fact]
    public void TheChoice_ReplaysFromTheCommandLog()
    {
        var (sim, _) = Garaged(Quick((3, 0.5f)));
        RequisitionTests.RunDays(sim, Notice);
        sim.Execute(new SetRequisitionRuleCommand(RequisitionRule.OldestFirst));
        RequisitionTests.RunDays(sim, Taken - Notice);

        var (fresh, _) = Garaged(Quick((3, 0.5f)));
        fresh.Replay(sim.CommandLog.Skip(fresh.CommandLog.Count).ToList(), sim.Tick);

        Assert.Equal(sim.StateHash(), fresh.StateHash());
    }
}
