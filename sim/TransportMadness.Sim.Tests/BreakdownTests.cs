using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>Breakdowns from low Condition: a wreck on the road, a crew, the garage; the frequency option; trains.</summary>
public class BreakdownTests
{
    [Fact]
    public void AWornBus_BreaksDown_BlocksItsLane_AndIsTowedToTheGarageWithTheWreckCondition()
    {
        var w = Road(frequency: IncidentFrequency.Normal);
        var sim = w.Sim;
        int bus = Bus(w);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
        sim.SetCondition(bus, 0f);

        Assert.True(RunUntil(sim, 600, () => sim.IncidentCount > 0), "it never broke down");

        var incident = sim.GetIncident(0);
        Assert.Equal(IncidentKind.Breakdown, incident.Kind);
        Assert.True(incident.FromCondition);
        Assert.True(incident.Negligent, "Condition 0 is below the critical mark");
        Assert.Equal(VehicleKind.Wreck, sim.GetVehicle(bus).Kind);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.Breakdown && e.Severity == EventSeverity.Serious && e.Subject == bus);

        Assert.True(RunUntil(sim, 1200, () => !sim.IsCompanyVehicle(bus)), "the wreck was never cleared");
        var parked = Assert.Single(sim.Garage, g => g.Active);
        Assert.Equal(sim.MaintenanceRules.WreckCondition, parked.Condition);
        Assert.False(parked.Rail);
        Assert.Equal(w.Depot, parked.Depot);
    }

    [Fact]
    public void ABreakdownAboveTheCriticalMark_IsNotNegligence()
    {
        // The risk is pulled up so that it happens at Condition 40 in a reasonable time.
        var tuned = Tuned(m => m with { NoRiskAbove = 60f, HazardPerKmAtFloor = 40f, HazardPerKmAtZero = 80f });
        var w = Road(tuning: tuned, frequency: IncidentFrequency.High);
        int bus = Bus(w);
        w.Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
        w.Sim.SetCondition(bus, 40f);

        Assert.True(RunUntil(w.Sim, 600, () => w.Sim.IncidentCount > 0));

        Assert.True(w.Sim.GetIncident(0).FromCondition);
        Assert.False(w.Sim.GetIncident(0).Negligent);
    }

    [Fact]
    public void AVehicleAbove60NeverBreaksDown_WhateverTheFrequency()
    {
        var w = Road(frequency: IncidentFrequency.High);
        int bus = Bus(w);
        w.Sim.SetCondition(bus, 100f);

        Run(w.Sim, 1500);

        Assert.Equal(0, w.Sim.IncidentCount);
        Assert.True(w.Sim.ConditionOf(bus) > 60f);
    }

    [Fact]
    public void WithTheFrequencyOff_NothingBreaksDown_EvenAtConditionZero()
    {
        var w = Road(frequency: IncidentFrequency.Off);
        int bus = Bus(w);
        w.Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
        w.Sim.SetCondition(bus, 0f);

        Run(w.Sim, 1500);

        Assert.Equal(0, w.Sim.IncidentCount);
        Assert.Equal(1, w.Sim.Execute(new SetFrequencyCommand(IncidentFrequency.High)));
        Assert.Equal(IncidentFrequency.High, w.Sim.Frequency);
        Assert.True(RunUntil(w.Sim, 1500, () => w.Sim.IncidentCount > 0), "turning it on had no effect");
    }

    [Fact]
    public void ABiggerFrequency_MeansMoreBreakdowns()
    {
        int Breakdowns(IncidentFrequency frequency)
        {
            int count = 0;
            for (ulong seed = 1; seed <= 12; seed++)
            {
                var net = new RoadNetwork(Terrain.Flat(), Worlds.Late());
                var sim = new Simulation(net, seed, frequency);
                sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f));
                int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Workshop));
                int a = sim.Execute(new BuildStationCommand(150, 0)), b = sim.Execute(new BuildStationCommand(1050, 0));
                int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
                int bus = sim.Execute(new BuyVehicleCommand(depot, "bus", line));
                sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
                sim.SetCondition(bus, 10f);
                Run(sim, 300);
                count += sim.IncidentCount;
            }
            return count;
        }

        int off = Breakdowns(IncidentFrequency.Off), low = Breakdowns(IncidentFrequency.Low), normal = Breakdowns(IncidentFrequency.Normal), high = Breakdowns(IncidentFrequency.High);

        Assert.Equal(0, off);
        Assert.True(low <= normal && normal < high, $"low {low}, normal {normal}, high {high}");
        Assert.True(high > 0);
    }

    [Fact]
    public void ABreakdownCostsTheLoad_AndThePeopleOnBoardMakeTheirOwnWay()
    {
        var w = Road(frequency: IncidentFrequency.Normal);
        var sim = w.Sim;
        int bus = Bus(w);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Vehicle, bus, MaintenancePolicy.Off));
        sim.SetCondition(bus, 0f);

        Assert.True(RunUntil(sim, 1800, () => !sim.IsCompanyVehicle(bus) && sim.IncidentCount > 0));

        Assert.Equal(0, sim.Garage.Count(g => g.Active && g.Condition > sim.MaintenanceRules.WreckCondition));
        Assert.Equal(0, sim.GetFleetInfo(bus).Load);
    }

    [Fact]
    public void ATrainThatBreaksDown_StandsHoldingItsTrack_ThenIsTowedToTheDepot()
    {
        var w = Rail(frequency: IncidentFrequency.Normal);
        var sim = w.Sim;
        var train = Train(w);
        sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Train, train.Id, MaintenancePolicy.Off));
        train.Condition = 0f;

        Assert.True(RunUntil(sim, 300, () => train.Broken), "the train never broke down");

        Assert.Equal(TrainStatus.Halted, train.Status);
        Assert.Equal(sim.MaintenanceRules.WreckCondition, train.Condition);
        Assert.Equal(0, train.Load);
        Assert.Contains(sim.Events, e => e.Kind == SimEventKind.TrainBreakdown && e.Subject == train.Id);
        Run(sim, sim.MaintenanceRules.TrainBreakdownSeconds - 20);
        Assert.Equal(TrainStatus.Halted, train.Status);

        Assert.True(RunUntil(sim, 600, () => !train.Active), "the train was never towed");
        var parked = Assert.Single(sim.Garage, g => g.Active);
        Assert.True(parked.Rail);
        Assert.InRange(parked.Condition, sim.MaintenanceRules.WreckCondition - 1f, sim.MaintenanceRules.WreckCondition); // it wore a little on the way
    }
}
