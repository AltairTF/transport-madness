using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class JunctionTests
{
    [Fact]
    public void VehiclesWithMeetingPaths_NeverClearTheJunctionTogether()
    {
        var net = new RoadNetwork();
        int c = net.AddNode(0, 0);
        var arms = new[] { net.AddNode(-150, 0), net.AddNode(150, 0), net.AddNode(0, -150), net.AddNode(0, 150) };
        foreach (int arm in arms) net.AddRoad(arm, c, RoadType.TwoLane, 14f);
        var sim = new Simulation(net, seed: 9);

        // Three vehicles queued up on every approach, all heading for the middle.
        for (int a = 0; a < arms.Length; a++)
        {
            var lane = net.IncomingLanes(c).First(l => net.GetLane(l).StartNode == arms[a]);
            for (int k = 0; k < 3; k++)
            {
                int v = sim.SpawnVehicle(lane, position: 40f + 30f * k + a * 7f, speed: 10f);
                sim.SetKeepLane(v, true);
            }
        }

        var lastLane = new LaneId[sim.VehicleCount];
        for (int i = 0; i < sim.VehicleCount; i++) lastLane[i] = sim.GetVehicle(i).Lane;
        var passes = new List<(long Tick, LaneId In, LaneId Out)>();
        for (int step = 0; step < 150 * 20; step++)
        {
            sim.Step();
            for (int i = 0; i < sim.VehicleCount; i++)
            {
                var lane = sim.GetVehicle(i).Lane;
                if (lane == lastLane[i]) continue;
                if (net.GetLane(lane).StartNode == c) passes.Add((sim.Tick, lastLane[i], lane));
                lastLane[i] = lane;
            }
        }

        Assert.True(passes.Count >= 12, $"only {passes.Count} vehicles went through");
        for (int a = 0; a < passes.Count; a++)
            for (int b = a + 1; b < passes.Count; b++)
            {
                if (!net.MovesConflict(passes[a].In, passes[a].Out, passes[b].In, passes[b].Out)) continue;
                float apart = Math.Abs(passes[a].Tick - passes[b].Tick) * Simulation.StepSeconds;
                Assert.True(apart >= 0.4f, $"conflicting vehicles cleared the junction {apart:F2}s apart");
            }
    }
}
