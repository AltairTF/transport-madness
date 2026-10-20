using TransportMadness.Sim;
using static TransportMadness.Sim.Tests.MaintenanceWorlds;

namespace TransportMadness.Sim.Tests;

/// <summary>The option for how often the random events happen: chosen when a game starts, changed by a logged command, and not touching the accidents that come out of the driving.</summary>
public class FrequencyOptionTests
{
    [Fact]
    public void AGameStartsOnNormal_UnlessToldOtherwise_AndTheScalesComeFromTheTuning()
    {
        var net = new RoadNetwork(Terrain.Flat());
        Assert.Equal(IncidentFrequency.Normal, new Simulation(net, 1).Frequency);
        Assert.Equal(IncidentFrequency.High, new Simulation(new RoadNetwork(Terrain.Flat()), 1, IncidentFrequency.High).Frequency);

        var scales = Tuning.Load().Maintenance.Frequency;
        Assert.Equal(0f, scales.For(IncidentFrequency.Off));
        Assert.Equal(1f, scales.For(IncidentFrequency.Normal));
        Assert.True(scales.For(IncidentFrequency.Low) < scales.For(IncidentFrequency.Normal));
        Assert.True(scales.For(IncidentFrequency.High) > scales.For(IncidentFrequency.Normal));
    }

    [Fact]
    public void TheCommandChangesIt_IsLogged_RefusesNonsense_AndReplays()
    {
        var w = Road(frequency: IncidentFrequency.Off);
        var sim = w.Sim;

        Assert.Equal(1, sim.Execute(new SetFrequencyCommand(IncidentFrequency.Low)));
        Assert.Equal(IncidentFrequency.Low, sim.Frequency);
        Assert.Equal(0, sim.Execute(new SetFrequencyCommand((IncidentFrequency)9)));
        Assert.Equal(IncidentFrequency.Low, sim.Frequency);
        Run(sim, 10);
        sim.Execute(new SetFrequencyCommand(IncidentFrequency.High));

        var fresh = Road(frequency: IncidentFrequency.Off).Sim;
        fresh.Replay(sim.CommandLog.Skip(4).ToList().Count == 0 ? sim.CommandLog : sim.CommandLog, sim.Tick);
        Assert.Equal(IncidentFrequency.High, fresh.Frequency);
    }

    [Fact]
    public void OffDoesNotRemoveTheAccidentsThatComeOutOfTheDriving()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var sim = new Simulation(net, 1, IncidentFrequency.Off);
        sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f));
        int a = sim.SpawnVehicle(new LaneId(0), 300f, 0f), b = sim.SpawnVehicle(new LaneId(0), 304f, 0f);

        sim.Collide(a, b, IncidentKind.RearEnd);

        Assert.Equal(1, sim.IncidentCount);
        Assert.Equal(IncidentFrequency.Off, sim.Frequency);
    }
}
