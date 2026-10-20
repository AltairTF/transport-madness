using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>Condition: where it starts, how it wears, what low Condition does to the risk of a breakdown and to braking.</summary>
public class ConditionTests
{
    [Fact]
    public void ANewVehicleAndANewTrainStartAt100_AndAmbientCarsHaveNoWear()
    {
        var w = Road();
        int bus = Bus(w);
        int car = w.Sim.SpawnVehicle(new LaneId(0), 400f, 8f);

        Assert.Equal(100f, w.Sim.ConditionOf(bus));
        Assert.False(w.Sim.IsPatched(bus));
        Run(w.Sim, 300);
        Assert.Equal(100f, w.Sim.ConditionOf(car));

        var r = Rail();
        Assert.Equal(100f, Train(r).Condition);
    }

    [Fact]
    public void ConditionFallsWithTheDistanceDriven_OnePointPerTheTuningsMetres()
    {
        var tuning = Tuned(m => m with { MetresPerPoint = 500f, LoadedWear = 0f });
        var w = Road(tuning: tuning);
        int bus = Bus(w);
        w.Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        double driven = 0;
        for (int i = 0; i < 2400; i++)
        {
            w.Sim.Step();
            driven += w.Sim.GetVehicle(bus).Speed * Simulation.StepSeconds;
        }

        Assert.True(driven > 400, "the bus hardly moved");
        Assert.Equal(100.0 - driven / 500.0, w.Sim.ConditionOf(bus), 0.2);
    }

    [Fact]
    public void WearIsFasterWithALoad_AndForAPatchedVehicle_AndNeverBelowZero()
    {
        var w = Road();
        var sim = w.Sim;
        float empty = sim.WearOf(1000f, 0f, false);

        Assert.Equal(1000f / 12000f, empty, 5);
        Assert.Equal(empty * 1.3f, sim.WearOf(1000f, 1f, false), 5);
        Assert.Equal(empty * 1.15f, sim.WearOf(1000f, 0.5f, false), 5);
        Assert.Equal(empty * 1.5f, sim.WearOf(1000f, 0f, true), 5);

        var worn = Tuned(m => m with { MetresPerPoint = 20f });
        var w2 = Road(tuning: worn);
        int bus = Bus(w2);
        w2.Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        Run(w2.Sim, 600);
        Assert.Equal(0f, w2.Sim.ConditionOf(bus));
    }

    [Fact]
    public void TheBreakdownRisk_IsNoneAbove60_RisesSmoothlyToThe20Mark_AndIsHighBelowIt()
    {
        var sim = Road().Sim;
        var m = sim.MaintenanceRules;

        Assert.Equal(0f, sim.HazardPerMetre(100f));
        Assert.Equal(0f, sim.HazardPerMetre(60f));
        Assert.Equal(m.HazardPerKmAtFloor / 1000f, sim.HazardPerMetre(20f), 9);
        Assert.Equal(m.HazardPerKmAtZero / 1000f, sim.HazardPerMetre(0f), 9);
        float last = 0f;
        for (float c = 59f; c >= 0f; c -= 1f)
        {
            float h = sim.HazardPerMetre(c);
            Assert.True(h > last, $"the risk should rise as Condition falls ({c})");
            Assert.True(h - last < 0.5f * m.HazardPerKmAtZero / 1000f, "a jump in the risk");
            last = h;
        }
        Assert.True(sim.HazardPerMetre(10f) > 3f * sim.HazardPerMetre(30f));
    }

    [Fact]
    public void Brakes_FadeBelowTheFadeMark_ToTheWorstShareAtZero()
    {
        var sim = Road().Sim;

        Assert.Equal(1f, sim.BrakeShare(100f));
        Assert.Equal(1f, sim.BrakeShare(60f));
        Assert.Equal(0.8f, sim.BrakeShare(30f), 5);
        Assert.Equal(0.6f, sim.BrakeShare(0f), 5);
    }

    [Fact]
    public void ConditionStaysWhereItIs_WhileAVehicleStandsAtAStation()
    {
        var w = Road();
        int bus = Bus(w);
        Assert.True(RunUntil(w.Sim, 300, () => w.Sim.GetFleetInfo(bus).Loading));
        float at = w.Sim.ConditionOf(bus);

        Run(w.Sim, 1);

        Assert.True(w.Sim.GetFleetInfo(bus).Loading);
        Assert.Equal(at, w.Sim.ConditionOf(bus));
    }
}
