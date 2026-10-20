using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>Signals wear with the trains let through, fail sometimes, and are put right by a crew; a failed signal shows red to everybody.</summary>
public class SignalFailureTests
{
    /// <summary>The straight rail line with a block signal at x = 700 facing trains that go from the first station to the second.</summary>
    static (RailLine W, RailSignal Signal) WithSignal(Tuning? tuning = null, IncidentFrequency frequency = IncidentFrequency.Off)
    {
        var w = Rail(DepotLevel.Workshop, tuning, frequency);
        int id = w.Sim.Execute(new BuildSignalCommand(700, 0, SignalKind.Block, Forward: true));
        Assert.True(id >= 0);
        return (w, w.Sim.Rail.GetSignal(new SignalId(id)));
    }

    static Tuning Fragile(Func<SignalTuning, SignalTuning>? signals = null) => Tuned(m => m with
    {
        HazardPerKmAtFloor = 5000f,
        HazardPerKmAtZero = 5000f,
        Signals = (signals ?? (s => s))(m.Signals),
    });

    [Fact]
    public void ASignalWearsWithEachTrainLetThrough_AndStartsAt100()
    {
        var (w, signal) = WithSignal();
        var train = Train(w);
        Assert.Equal(100f, signal.Condition);

        Assert.True(RunUntil(w.Sim, 600, () => signal.Condition < 100f), "no train was let through");

        Assert.Equal(100f - w.Sim.MaintenanceRules.Signals.WearPerPassage, signal.Condition, 3);
        Assert.False(signal.Failed);
        Assert.True(train.Active);
    }

    [Fact]
    public void AWornSignal_FailsWhenATrainIsLetThrough_AndShowsRedUntilACrewHasRepairedIt()
    {
        var (w, signal) = WithSignal(Fragile(), IncidentFrequency.Normal);
        var sim = w.Sim;
        var train = Train(w);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        signal.SetConditionForTest(10f);

        Assert.True(RunUntil(sim, 600, () => signal.Failed), "the signal never failed");

        Assert.False(sim.IsSignalClear(signal));
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.SignalFailure && e.Severity == EventSeverity.Serious);
        Assert.True(RunUntil(sim, 120, () => train.Status == TrainStatus.WaitingForTrack), "the train did not stop at the red signal");
        Run(sim, 10);
        double headAtRed = train.Head;
        Run(sim, 5);
        Assert.Equal(headAtRed, train.Head, 0.05);

        long before = sim.Company.Total(LedgerEntry.Maintenance);
        Assert.True(RunUntil(sim, 600, () => !signal.Failed), "the crew never came");
        Assert.Equal(sim.MaintenanceRules.Signals.RepairedCondition, signal.Condition, 1f);
        Assert.Equal(before - sim.MaintenanceRules.Signals.RepairPrice, sim.Company.Total(LedgerEntry.Maintenance));
        Assert.Equal(0, sim.Company.Total(LedgerEntry.Fines)); // a depot is near: no negligence
        Assert.True(RunUntil(sim, 600, () => train.Head > headAtRed + 50), "the train never went on");
    }

    [Fact]
    public void WithTheFrequencyOff_ASignalNeverFails_HoweverWornItIs()
    {
        var (w, signal) = WithSignal(Fragile(), IncidentFrequency.Off);
        Train(w);
        w.Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        signal.SetConditionForTest(0f);

        Run(w.Sim, 900);

        Assert.False(signal.Failed);
        Assert.DoesNotContain(w.Sim.Events, e => e.Kind == SimEventKind.SignalFailure);
    }

    [Fact]
    public void AFailureThatTakesTooLongToRepair_IsNegligence_AndFined()
    {
        var tuning = Fragile(s => s with { FineAfterSeconds = 30f });
        var (w, signal) = WithSignal(tuning, IncidentFrequency.Normal);
        var sim = w.Sim;
        Train(w);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        signal.SetConditionForTest(10f);

        Assert.True(RunUntil(sim, 600, () => signal.Failed));
        Assert.True(RunUntil(sim, 600, () => !signal.Failed));

        Assert.Equal(-sim.MaintenanceRules.Consequences.NegligenceFine, sim.Company.Total(LedgerEntry.Fines));
    }

    [Fact]
    public void WithTheCompanyPolicyOn_ACrewServicesASignalUnderTheFloor_AndWithItOffNoneComes()
    {
        long Run1(MaintenancePolicy policy, out float condition)
        {
            var (w, signal) = WithSignal();
            Train(w);
            w.Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, policy));
            signal.SetConditionForTest(35f);
            Run(w.Sim, 700);
            condition = signal.Condition;
            return w.Sim.Company.Total(LedgerEntry.Maintenance);
        }

        long on = Run1(MaintenancePolicy.When(0, 40, RepairKind.Service), out float serviced);
        long off = Run1(MaintenancePolicy.Off, out float neglected);

        Assert.True(serviced > 80f, $"it was not serviced ({serviced})");
        Assert.True(on < 0);
        Assert.True(neglected < 36f);
        Assert.Equal(0, off);
    }

    [Fact]
    public void ASignalPendingACrew_IsForgotten_WhenItIsRemoved()
    {
        var (w, signal) = WithSignal(Fragile(), IncidentFrequency.Normal);
        var sim = w.Sim;
        Train(w);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        signal.SetConditionForTest(10f);
        Assert.True(RunUntil(sim, 600, () => signal.Failed));

        Assert.Equal(1, sim.Execute(new RemoveSignalCommand(signal.Id.Value)));
        Run(sim, 400);

        Assert.False(signal.Failed);
    }
}
