using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>What an incident costs the company: compensation to the injured, a fine for negligence, and the goodwill of the town it happens in.</summary>
public class IncidentConsequenceTests
{
    [Fact]
    public void ACrashOfALoadedBus_PaysCompensationForAShareOfThePassengers_AndIsReported()
    {
        var w = Road(frequency: IncidentFrequency.Off);
        var sim = w.Sim;
        int bus = Bus(w);
        Assert.True(RunUntil(sim, 600, () => sim.GetFleetInfo(bus).Load > 0 && !sim.GetFleetInfo(bus).Loading), "the bus never carried anybody");
        int load = sim.GetFleetInfo(bus).Load;
        int other = sim.SpawnVehicle(sim.GetVehicle(bus).Lane, sim.GetVehicle(bus).Position + 20f, 0f);
        long before = sim.Company.Total(LedgerEntry.Compensation);
        var rules = sim.MaintenanceRules.Consequences;
        int expected = (int)Math.Round(load * rules.InjuredShare);

        sim.Collide(bus, other, IncidentKind.RearEnd);

        Assert.True(expected > 0);
        Assert.Equal(before - expected * rules.CompensationPerInjured, sim.Company.Total(LedgerEntry.Compensation));
        Assert.Equal(expected, sim.Company.Injured);
        var crash = Assert.Single(sim.Events, e => e.Kind == SimEventKind.Crash);
        Assert.Contains($"{expected} injured", crash.Text);
        Assert.True(sim.GetIncident(0).CompanyInvolved);
    }

    [Fact]
    public void ACrashOfAnAmbientCar_CostsTheCompanyNothing()
    {
        var w = Road(frequency: IncidentFrequency.Off);
        var sim = w.Sim;
        int a = sim.SpawnVehicle(new LaneId(0), 400f, 0f), b = sim.SpawnVehicle(new LaneId(0), 410f, 0f);
        long money = sim.Company.Money;

        sim.Collide(a, b, IncidentKind.RearEnd);

        Assert.Equal(money, sim.Company.Money);
        Assert.Equal(0, sim.Company.Injured);
        Assert.DoesNotContain(sim.Events, e => e.Kind == SimEventKind.Crash);
        Assert.False(sim.GetIncident(0).CompanyInvolved);
    }

    [Fact]
    public void ABreakdownBelowTheCriticalMark_IsFined_OneAboveItIsNot()
    {
        long Fines(float condition)
        {
            var tuned = Tuned(m => m with { HazardPerKmAtFloor = 5000f, HazardPerKmAtZero = 5000f, NoRiskAbove = 80f });
            var w = Road(tuning: tuned, frequency: IncidentFrequency.Normal);
            int bus = Bus(w);
            w.Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
            w.Sim.SetCondition(bus, condition);
            Assert.True(RunUntil(w.Sim, 300, () => w.Sim.IncidentCount > 0));
            return w.Sim.Company.Total(LedgerEntry.Fines);
        }

        var fine = Tuning.Load().Maintenance.Consequences.NegligenceFine;

        Assert.Equal(-fine, Fines(10f));
        Assert.Equal(0, Fines(50f));
    }

    static (Simulation Sim, int Bus) BusInATown(Tuning? tuning = null)
    {
        var sc = Worlds.BusScenario(7, 2, 1, tuning)!.Value;
        var sim = sc.Sim;
        int bus = Enumerable.Range(0, sim.VehicleCount).First(sim.IsCompanyVehicle);
        bool inTown = false;
        for (int i = 0; i < 40 * 600 && !inTown; i++)
        {
            sim.Step();
            var v = sim.GetVehicle(bus);
            var (x, z, _) = sim.Network.LanePoint(v.Lane, v.Position);
            inTown = v.Speed > 1f && sim.TownAt(x, z) >= 0;
        }
        Assert.True(inTown, "the bus never drove through a town");
        return (sim, bus);
    }

    [Fact]
    public void AnIncidentOfACompanyVehicleInsideATown_CostsThatTownSatisfaction_Once()
    {
        var (sim, bus) = BusInATown();
        var v = sim.GetVehicle(bus);
        var (x, z, _) = sim.Network.LanePoint(v.Lane, v.Position);
        int town = sim.TownAt(x, z);
        float before = sim.Towns[town].Satisfaction;
        var penalty = sim.MaintenanceRules.Consequences.TownIncidentPenalty;
        int other = sim.SpawnVehicle(v.Lane, v.Position + 15f, 0f);

        sim.Collide(bus, other, IncidentKind.RearEnd);

        Assert.Equal(before - penalty, sim.Towns[town].Satisfaction, 0.01f);
        sim.Collide(bus, sim.SpawnVehicle(v.Lane, Math.Max(2f, v.Position - 25f), 0f), IncidentKind.RearEnd); // the same incident: no second penalty
        Assert.Equal(before - penalty, sim.Towns[town].Satisfaction, 0.01f);
    }

    [Fact]
    public void ARoadBlockedByTheCompanysWreckForAnHour_CostsTheTownMoreAndTheGameSaysSo()
    {
        var tuned = Tuning.Load();
        tuned = tuned with
        {
            Crews = tuned.Crews with { FallbackResponse = 5000f, PerDepot = new CrewsPerDepot { Simple = 0, Workshop = 0, Complex = 0 } },
            Maintenance = tuned.Maintenance with { Consequences = tuned.Maintenance.Consequences with { BlockedSeconds = 300f } },
        };
        var (sim, bus) = BusInATown(tuned);
        var v = sim.GetVehicle(bus);
        var (x, z, _) = sim.Network.LanePoint(v.Lane, v.Position);
        int town = sim.TownAt(x, z);
        sim.Collide(bus, sim.SpawnVehicle(v.Lane, v.Position + 15f, 0f), IncidentKind.RearEnd);
        float after = sim.Towns[town].Satisfaction;

        for (int i = 0; i < 20 * 200; i++) sim.Step();
        Assert.Equal(after, sim.Towns[town].Satisfaction, 0.01f);
        for (int i = 0; i < 20 * 200; i++) sim.Step();

        Assert.Equal(after - sim.MaintenanceRules.Consequences.TownBlockedPenalty, sim.Towns[town].Satisfaction, 0.6f);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.TownUnhappy);
    }
}
