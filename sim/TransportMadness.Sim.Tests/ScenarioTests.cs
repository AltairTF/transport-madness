using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>M0 exit criteria 3 and 4 on a whole road network rather than a hand-built scene.</summary>
public class ScenarioTests
{
    static RoadNetwork TwoLaneGrid(int size = 8, float spacing = 300f)
    {
        var net = new RoadNetwork();
        var nodes = new int[size, size];
        for (int x = 0; x < size; x++)
            for (int z = 0; z < size; z++)
                nodes[x, z] = net.AddNode(x * spacing, z * spacing);
        for (int x = 0; x < size; x++)
            for (int z = 0; z < size; z++)
            {
                if (x + 1 < size) net.AddRoad(nodes[x, z], nodes[x + 1, z], RoadType.TwoLane, 14f);
                if (z + 1 < size) net.AddRoad(nodes[x, z], nodes[x, z + 1], RoadType.TwoLane, 14f);
            }
        return net;
    }

    [Fact]
    public void LongRun_ShowsOvertakingAndAtLeastOneFailureThatBecomesAnAccident_ThatCrewsThenClear()
    {
        var net = TwoLaneGrid();
        var sim = new Simulation(net, seed: 3);
        sim.AddDepot(net.NodeCount / 2, DepotLevel.Workshop);
        sim.SeedTraffic(300);

        int overtakes = 0;
        var wasOut = new bool[1024];
        for (int step = 0; step < 20 * 60 * 25; step++)
        {
            sim.Step();
            for (int i = 0; i < sim.VehicleCount; i++)
            {
                bool out_ = sim.GetVehicle(i).Overtake != OvertakePhase.None;
                if (out_ && !wasOut[i]) overtakes++;
                wasOut[i] = out_;
            }
        }

        Assert.True(overtakes >= 20, $"{overtakes} overtakes");
        Assert.NotEmpty(sim.Incidents);
        Assert.Contains(sim.Incidents, i => i.Kind is IncidentKind.HeadOn or IncidentKind.Sideswipe);
        Assert.Contains(sim.Incidents, i => i.State == IncidentState.Cleared);
        // Whatever is still wrecked belongs to an incident that is not cleared yet.
        for (int i = 0; i < sim.VehicleCount; i++)
        {
            var v = sim.GetVehicle(i);
            if (v.Kind is VehicleKind.Wreck or VehicleKind.Debris)
                Assert.Contains(sim.Incidents, inc => inc.State != IncidentState.Cleared && inc.VehicleIds.Contains(i));
        }
    }
}
