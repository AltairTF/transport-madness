using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>A bending road can meet the same road more than once.</summary>
public class CurveCrossingTests
{
    [Fact]
    public void ACurveCrossingARoadTwice_SplitsItIntoThreePieces_AndJoinsBothCrossings()
    {
        foreach (var (x, sweep) in new[] { (-30f, 2f), (30f, -2f) }) // the bulge is toward the other side of the road
        {
            var net = new RoadNetwork(Terrain.Flat());
            net.AddRoad(net.AddNode(0, -200), net.AddNode(0, 200), RoadType.TwoLane, 14f);

            var built = net.BuildRoad(x, -100, x, 100, RoadType.TwoLane, 14f, sweep);

            Assert.Equal(3, built.Count); // start to the first crossing, between the crossings, last crossing to the end
            int live = 0;
            for (int r = 0; r < net.RoadCount; r++) if (!net.GetRoad(new RoadId(r)).Retired) live++;
            Assert.Equal(6, live); // the straight road is in three pieces too
        }
    }

    [Fact]
    public void RandomBendingRoads_NeverThrow()
    {
        foreach (var terrain in new[] { Terrain.Flat(), Terrain.Generate(3) })
        {
            var net = new RoadNetwork(terrain);
            var rng = new Random(11);
            for (int i = 0; i < 200; i++)
            {
                float x0 = rng.Next(-400, 400), z0 = rng.Next(-400, 400);
                float a = (float)(rng.NextDouble() * Math.PI * 2), length = rng.Next(20, 300);
                float sweep = (float)(rng.NextDouble() * 4 - 2);
                net.BuildRoad(x0, z0, x0 + MathF.Cos(a) * length, z0 + MathF.Sin(a) * length, i % 2 == 0 ? RoadType.TwoLane : RoadType.Avenue2x2, 14f, sweep);
            }
        }
    }
}
