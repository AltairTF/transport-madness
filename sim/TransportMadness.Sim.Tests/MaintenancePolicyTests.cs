using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>The three-level maintenance policy and what it makes a vehicle do: go to a depot at its stop, wait for a bay, be repaired, come back.</summary>
public class MaintenancePolicyTests
{
    [Fact]
    public void TheCompanyStartsWithAServiceEvery60Days_OrBelow40()
    {
        var sim = Road().Sim;

        Assert.Equal(MaintenancePolicy.When(60, 40, RepairKind.Service), sim.CompanyPolicy);
    }

    [Fact]
    public void TheMostSpecificPolicyThatDoesNotInheritWins_AndInheritPassesTheChoiceUp()
    {
        var w = Road();
        var sim = w.Sim;
        int bus = Bus(w);
        var line = MaintenancePolicy.When(30, 0, RepairKind.Patch);
        var vehicle = MaintenancePolicy.Off;

        Assert.Equal((sim.CompanyPolicy, PolicyScope.Company), sim.EffectivePolicyOf(bus));
        Assert.Equal(1, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Line, w.Line, line)));
        Assert.Equal((line, PolicyScope.Line), sim.EffectivePolicyOf(bus));
        Assert.Equal(1, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, vehicle)));
        Assert.Equal((vehicle, PolicyScope.Vehicle), sim.EffectivePolicyOf(bus));
        Assert.Equal(1, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Inherit)));
        Assert.Equal((line, PolicyScope.Line), sim.EffectivePolicyOf(bus));
        Assert.Equal(1, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Line, w.Line, MaintenancePolicy.Inherit)));
        Assert.Equal((sim.CompanyPolicy, PolicyScope.Company), sim.EffectivePolicyOf(bus));
        Assert.Equal(1, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.When(10, 50, RepairKind.Overhaul))));
        Assert.Equal(MaintenancePolicy.When(10, 50, RepairKind.Overhaul), sim.EffectivePolicyOf(bus).Policy);
    }

    [Fact]
    public void APolicyIsRefused_ForNothingToApplyItTo_ValuesOutOfRange_OrACompanyThatInherits()
    {
        var w = Road();
        var sim = w.Sim;
        int bus = Bus(w);
        var ok = MaintenancePolicy.When(30, 30, RepairKind.Service);

        Assert.Equal(0, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Inherit)));
        Assert.Equal(0, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Line, 9, ok)));
        Assert.Equal(0, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, 99, ok)));
        Assert.Equal(0, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Train, 0, ok)));
        Assert.Equal(0, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.When(-1, 30, RepairKind.Patch))));
        Assert.Equal(0, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.When(30, 101, RepairKind.Patch))));
        Assert.Equal(0, sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.When(30, 30, (RepairKind)9))));
        Assert.Equal(PolicyMode.Inherit, sim.PolicyOf(bus).Mode);
    }

    [Fact]
    public void ADueBus_FinishesUnloadingAtItsStop_GoesToTheDepot_IsServiced_AndCarriesOnOnItsLine()
    {
        var w = Road(DepotLevel.Workshop);
        var sim = w.Sim;
        int bus = Bus(w);
        sim.SetCondition(bus, 30f); // below the company's floor of 40
        var seen = new HashSet<RepairPhase>();
        long price = sim.VehicleModels[sim.ModelIndex("bus")].Price;

        Assert.True(RunUntil(sim, 900, () =>
        {
            seen.Add(sim.RepairPhaseOf(bus));
            return sim.ConditionOf(bus) > 60f;
        }), "it was never serviced");

        Assert.Contains(RepairPhase.ToDepot, seen);
        Assert.Contains(RepairPhase.Working, seen);
        Assert.Equal(80f, sim.ConditionOf(bus), 0.5f);
        Assert.Equal(-sim.RepairPrice(RepairKind.Service, price), sim.Company.Total(LedgerEntry.Maintenance));
        Assert.Equal(1440, sim.RepairPrice(RepairKind.Service, price)); // 6 % of 24 000
        Assert.True(sim.Lines[w.Line].Upkeep >= sim.RepairPrice(RepairKind.Service, price), "the repair is on the line's books");
        Assert.True(sim.UpkeepPaidFor(bus) >= sim.RepairPrice(RepairKind.Service, price));
        Assert.True(sim.IsCompanyVehicle(bus));
        Assert.Equal(w.Line, sim.GetFleetInfo(bus).Line);
        Assert.Equal(0, sim.DaysSinceRepair(bus));
        Assert.False(sim.IsPatched(bus));

        long income = sim.IncomeOf(bus);
        Run(sim, 400);
        Assert.True(sim.IsCompanyVehicle(bus));
        Assert.Equal(RepairPhase.None, sim.RepairPhaseOf(bus));
        Assert.True(sim.IncomeOf(bus) > income, "it never went back to work");
    }

    [Fact]
    public void ADueBus_LoadsNothingMoreAtTheStopWhereItBecomesDue()
    {
        var w = Road(DepotLevel.Workshop);
        var sim = w.Sim;
        int bus = Bus(w);
        sim.SetCondition(bus, 30f);

        Assert.True(RunUntil(sim, 300, () => sim.RepairPhaseOf(bus) is RepairPhase.ToDepot or RepairPhase.Working));

        Assert.Equal(0, sim.GetFleetInfo(bus).Load);
        Assert.Empty(sim.RidersOf(bus));
    }

    [Fact]
    public void WithThePolicyOff_ALowBusIsLeftToRun()
    {
        var w = Road(DepotLevel.Workshop);
        var sim = w.Sim;
        int bus = Bus(w);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
        sim.SetCondition(bus, 30f);

        Run(sim, 600);

        Assert.True(sim.ConditionOf(bus) < 30f);
        Assert.Empty(sim.RepairJobs);
        Assert.Equal(0, sim.Company.Total(LedgerEntry.Maintenance));
    }

    [Fact]
    public void ThePolicyCanFireByDays_AtFullCondition()
    {
        var w = Road(DepotLevel.Workshop);
        var sim = w.Sim;
        int bus = Bus(w);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.When(2, 0, RepairKind.Patch)));

        Assert.False(RunUntil(sim, sim.DayLengthSeconds * 1.5, () => sim.Company.Total(LedgerEntry.Maintenance) != 0));
        Assert.True(RunUntil(sim, sim.DayLengthSeconds, () => sim.Company.Total(LedgerEntry.Maintenance) != 0), "two days passed and nothing was done");
        Assert.True(RunUntil(sim, 200, () => sim.IsPatched(bus)), "a Patch leaves the patched mark");
    }

    [Fact]
    public void APatchRaisesConditionBy15_AndMakesTheVehicleWearFaster_UntilAServiceClearsIt()
    {
        var w = Road(DepotLevel.Simple);
        var sim = w.Sim;
        int bus = Bus(w);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.When(0, 40, RepairKind.Patch)));
        sim.SetCondition(bus, 30f);

        Assert.True(RunUntil(sim, 900, () => sim.IsPatched(bus)));

        Assert.True(sim.ConditionOf(bus) > 42f && sim.ConditionOf(bus) <= 45f);
        Assert.Equal(-sim.RepairPrice(RepairKind.Patch, 24000), sim.Company.Total(LedgerEntry.Maintenance));
        Assert.Equal(480, sim.RepairPrice(RepairKind.Patch, 24000)); // 2 %

        var (after, patched) = sim.After(RepairKind.Service, 45f);
        Assert.Equal(95f, after);
        Assert.False(patched);
    }

    [Fact]
    public void AnOverhaulRestoresTo95_AndNeverLowersAHigherCondition()
    {
        var sim = Road(DepotLevel.Complex).Sim;

        Assert.Equal(95f, sim.After(RepairKind.Overhaul, 20f).Condition);
        Assert.Equal(98f, sim.After(RepairKind.Overhaul, 98f).Condition);
        Assert.Equal(100f, sim.After(RepairKind.Patch, 90f).Condition);
        Assert.Equal(100f, sim.After(RepairKind.Service, 70f).Condition);
    }

    [Fact]
    public void WhatADepotCanDo_DependsOnItsLevel()
    {
        Assert.True(Simulation.CanRepair(DepotLevel.Simple, RepairKind.Patch));
        Assert.False(Simulation.CanRepair(DepotLevel.Simple, RepairKind.Service));
        Assert.True(Simulation.CanRepair(DepotLevel.Workshop, RepairKind.Service));
        Assert.False(Simulation.CanRepair(DepotLevel.Workshop, RepairKind.Overhaul));
        Assert.True(Simulation.CanRepair(DepotLevel.Complex, RepairKind.Overhaul));
    }

    [Fact]
    public void WhenNoDepotCanDoTheRepair_TheVehicleKeepsRunning_AndTheGameSaysSoOnce()
    {
        var w = Road(DepotLevel.Simple); // a Service needs a Workshop
        var sim = w.Sim;
        int bus = Bus(w);
        sim.SetCondition(bus, 30f);
        long income = sim.IncomeOf(bus);

        Run(sim, 900);

        Assert.Empty(sim.RepairJobs);
        Assert.True(sim.IncomeOf(bus) > income, "it stopped working");
        Assert.True(sim.ConditionOf(bus) < 30f);
        var warnings = sim.Events.Where(e => e.Kind == SimEventKind.NoDepot).ToList();
        Assert.Single(warnings);
        Assert.Equal(EventSeverity.Warning, warnings[0].Severity);
        Assert.Equal(bus, warnings[0].Subject);
    }

    [Fact]
    public void ADepotWorksOnAsManyVehiclesAsItHasBays_TheRestWait_AndAllAreRepaired()
    {
        var slow = Tuned(m => m with { Service = m.Service with { Seconds = 400f } });
        var w = Road(DepotLevel.Workshop, slow); // two bays
        var sim = w.Sim;
        var buses = new[] { Bus(w), Bus(w), Bus(w) };
        foreach (int bus in buses) sim.SetCondition(bus, 30f);
        int mostWorking = 0, mostJobs = 0;

        Assert.True(RunUntil(sim, 2400, () =>
        {
            mostWorking = Math.Max(mostWorking, sim.RepairJobs.Count(j => j.Working));
            mostJobs = Math.Max(mostJobs, sim.RepairJobs.Count);
            return buses.All(b => sim.ConditionOf(b) > 60f);
        }), "not all of them were repaired");

        Assert.Equal(2, mostWorking);
        Assert.Equal(3, mostJobs);
        Assert.Equal(3 * -sim.RepairPrice(RepairKind.Service, 24000), sim.Company.Total(LedgerEntry.Maintenance));
    }

    [Fact]
    public void ARepairTheyAskedFor_IsDoneAtTheNextStop_AndRefusedWithAReasonWhenItCannotBe()
    {
        var w = Road(DepotLevel.Workshop);
        var sim = w.Sim;
        int bus = Bus(w);

        Assert.Null(sim.WhyNotRepair(RepairSubject.Vehicle, bus, RepairKind.Service));
        Assert.Contains("no depot", sim.WhyNotRepair(RepairSubject.Vehicle, bus, RepairKind.Overhaul));
        Assert.Equal(0, sim.Execute(new RequestRepairCommand(RepairSubject.Vehicle, bus, RepairKind.Overhaul)));
        Assert.Equal(0, sim.Execute(new RequestRepairCommand(RepairSubject.Vehicle, 99, RepairKind.Patch)));
        Assert.Equal(1, sim.Execute(new RequestRepairCommand(RepairSubject.Vehicle, bus, RepairKind.Service)));
        Assert.Equal(RepairPhase.Requested, sim.RepairPhaseOf(bus));
        Assert.Equal(0, sim.Execute(new RequestRepairCommand(RepairSubject.Vehicle, bus, RepairKind.Patch))); // one at a time

        Assert.True(RunUntil(sim, 900, () => sim.Company.Total(LedgerEntry.Maintenance) < 0), "it was never repaired");
        Assert.Equal(RepairPhase.None, sim.RepairPhaseOf(bus));
        Run(sim, 30); // out of the depot again
        Assert.Equal(1, sim.Execute(new RequestRepairCommand(RepairSubject.Vehicle, bus, RepairKind.Patch))); // free to ask again
    }

    [Fact]
    public void AParkedVehicle_CanBeRepairedInItsDepot_ButNotPutOnALineWhileItIs()
    {
        var w = Road(DepotLevel.Workshop);
        var sim = w.Sim;
        int parked = sim.Execute(new BuyParkedVehicleCommand(w.Depot, "bus"));
        sim.Garage[parked].Condition = 5f;

        Assert.Equal(0, sim.Execute(new RequestRepairCommand(RepairSubject.Parked, parked, RepairKind.Overhaul))); // a Workshop cannot
        Assert.Equal(1, sim.Execute(new RequestRepairCommand(RepairSubject.Parked, parked, RepairKind.Service)));
        Assert.Equal(0, sim.Execute(new RequestRepairCommand(RepairSubject.Parked, parked, RepairKind.Patch)));
        Assert.Contains("being repaired", sim.WhyNotAssign(parked, w.Line));
        Assert.Equal(-1, sim.Execute(new AssignParkedCommand(parked, w.Line)));

        Run(sim, 65);

        Assert.Empty(sim.RepairJobs);
        Assert.Equal(55f, sim.Garage[parked].Condition);
        Assert.Null(sim.WhyNotAssign(parked, w.Line));
        int bus = sim.Execute(new AssignParkedCommand(parked, w.Line));
        Assert.True(bus >= 0);
        Assert.Equal(55f, sim.ConditionOf(bus)); // it keeps what it has
    }

    [Fact]
    public void ParkingAVehicle_KeepsItsCondition_AndSellingOneInTheDepotEndsTheRepair()
    {
        var w = Road(DepotLevel.Workshop, Tuned(m => m with { Service = m.Service with { Seconds = 600f } }));
        var sim = w.Sim;
        int bus = Bus(w);
        sim.SetCondition(bus, 33f);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
        Run(sim, 20);
        Assert.Equal(1, sim.Execute(new ParkVehicleCommand(bus)));
        Assert.True(RunUntil(sim, 900, () => !sim.IsCompanyVehicle(bus)));
        var entry = Assert.Single(sim.Garage, g => g.Active);
        Assert.InRange(entry.Condition, 32f, 33f);

        // And a vehicle that is inside the depot for a service is sold with its job.
        var w2 = Road(DepotLevel.Workshop, Tuned(m => m with { Service = m.Service with { Seconds = 600f } }));
        int bus2 = Bus(w2);
        w2.Sim.SetCondition(bus2, 30f);
        Assert.True(RunUntil(w2.Sim, 900, () => w2.Sim.RepairPhaseOf(bus2) == RepairPhase.Working));
        Assert.Equal(bus2, w2.Sim.Execute(new SellVehicleCommand(bus2)));
        Assert.Empty(w2.Sim.RepairJobs);
        Run(w2.Sim, 5);
    }

    [Fact]
    public void DeletingTheLine_SellsAVehicleInTheDepotForARepair()
    {
        var w = Road(DepotLevel.Workshop, Tuned(m => m with { Service = m.Service with { Seconds = 600f } }));
        int bus = Bus(w);
        w.Sim.SetCondition(bus, 30f);
        Assert.True(RunUntil(w.Sim, 900, () => w.Sim.RepairPhaseOf(bus) == RepairPhase.Working));

        Assert.Equal(1, w.Sim.Execute(new DeleteLineCommand(w.Line)));

        Assert.False(w.Sim.IsCompanyVehicle(bus));
        Assert.Empty(w.Sim.RepairJobs);
        Run(w.Sim, 30);
    }

    [Fact]
    public void AtTheTurnOfADay_AParkedVehicleThatIsDue_IsRepairedByTheCompanyPolicy_IfItsDepotCanDoIt()
    {
        var w = Road(DepotLevel.Workshop);
        var sim = w.Sim;
        int parked = sim.Execute(new BuyParkedVehicleCommand(w.Depot, "bus"));
        sim.Garage[parked].Condition = 5f;
        var tooSimple = Road(DepotLevel.Simple);
        int stuck = tooSimple.Sim.Execute(new BuyParkedVehicleCommand(tooSimple.Depot, "bus"));
        tooSimple.Sim.Garage[stuck].Condition = 5f;

        Run(sim, sim.DayLengthSeconds + 70);
        Run(tooSimple.Sim, tooSimple.Sim.DayLengthSeconds + 70);

        Assert.Equal(55f, sim.Garage[parked].Condition);   // a Service, +50
        Assert.Equal(5f, tooSimple.Sim.Garage[stuck].Condition); // a Simple depot cannot Service: it waits for the player

        var off = Road(DepotLevel.Workshop);
        off.Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        int idle = off.Sim.Execute(new BuyParkedVehicleCommand(off.Depot, "bus"));
        off.Sim.Garage[idle].Condition = 5f;
        Run(off.Sim, off.Sim.DayLengthSeconds + 70);
        Assert.Equal(5f, off.Sim.Garage[idle].Condition);
    }

    // ---- Trains ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ADueTrain_GoesToTheRailDepotAtItsStop_IsServiced_AndRunsAgain()
    {
        var w = Rail(DepotLevel.Workshop);
        var sim = w.Sim;
        var train = Train(w);
        train.Condition = 30f;
        var seen = new HashSet<RepairPhase>();
        var statuses = new HashSet<TrainStatus>();

        Assert.True(RunUntil(sim, 1200, () =>
        {
            seen.Add(sim.RepairPhaseOf(train));
            statuses.Add(train.Status);
            return train.Condition > 60f;
        }), "it was never serviced");

        Assert.Contains(RepairPhase.ToDepot, seen);
        Assert.Contains(RepairPhase.Working, seen);
        Assert.Contains(TrainStatus.Servicing, statuses);
        Assert.Equal(80f, train.Condition, 0.5f);
        long price = sim.Locomotives[0].Price + 3 * sim.Wagons[0].Price;
        Assert.Equal(-sim.RepairPrice(RepairKind.Service, price), sim.Company.Total(LedgerEntry.Maintenance));

        long income = train.Income;
        RailWorlds.Run(sim, 600);
        Assert.True(train.Active);
        Assert.Equal(RepairPhase.None, sim.RepairPhaseOf(train));
        Assert.True(train.Income > income, "it never went back to work");
    }

    [Fact]
    public void ARailDepotThatIsOnlySimple_CannotServiceATrain_ButItCanBeUpgradedByBuyingABetterOne()
    {
        var w = Rail(DepotLevel.Simple);
        var sim = w.Sim;
        var train = Train(w);
        train.Condition = 30f;

        Run(sim, 600);

        Assert.Empty(sim.RepairJobs);
        Assert.Single(sim.Events, e => e.Kind == SimEventKind.NoDepot);
        Assert.Equal(sim.Rail.Rail.DepotPrice, sim.RailDepotPrice(DepotLevel.Simple));
        Assert.True(sim.RailDepotPrice(DepotLevel.Workshop) > sim.RailDepotPrice(DepotLevel.Simple));
        Assert.True(sim.RailDepotPrice(DepotLevel.Complex) > sim.RailDepotPrice(DepotLevel.Workshop));
    }

    [Fact]
    public void ATrainIsSoldWithItsRepair_WhenItIsInTheDepot()
    {
        var w = Rail(DepotLevel.Workshop, Tuned(m => m with { Service = m.Service with { Seconds = 900f } }));
        var sim = w.Sim;
        var train = Train(w);
        train.Condition = 30f;
        Assert.True(RunUntil(sim, 1200, () => sim.RepairPhaseOf(train) == RepairPhase.Working));

        Assert.Equal(train.Id, sim.Execute(new SellTrainCommand(train.Id)));

        Assert.Empty(sim.RepairJobs);
        Assert.False(train.Active);
        RailWorlds.Run(sim, 30);
    }
}
