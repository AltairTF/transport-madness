using Xunit;

namespace TransportMadness.Sim.Tests;

public class RoadGroundTests
{
    /// <summary>The ground under the carriageway of a chain of bent roads on rolling land never stands above the road surface by more than a hand (it was a metre where roads meet).</summary>
    [Fact]
    public void GroundNeverStandsHighAboveTheRoad_WhereBentRoadsMeet()
    {
        var terrain = Terrain.Create(512, 8f, (x, z) => 20f + 6f * MathF.Sin(x * 0.02f) + 5f * MathF.Cos(z * 0.025f));
        var net = new RoadNetwork(terrain);
        var points = new (float X, float Z)[] { (0, 0), (120, 40), (200, 140), (160, 260), (60, 300), (-40, 220), (-20, 100) };
        for (int i = 0; i + 1 < points.Length; i++)
            net.BuildRoad(points[i].X, points[i].Z, points[i + 1].X, points[i + 1].Z, RoadType.Dirt, 9f, i % 2 == 0 ? 0.3f : -0.2f);

        float highest = 0f;
        for (int r = 0; r < net.RoadCount; r++)
        {
            var road = net.GetRoad(new RoadId(r));
            if (road.Retired) continue;
            float y0 = net.GetNodeHeight(road.From), y1 = net.GetNodeHeight(road.To);
            for (float s = 0f; s <= road.Length; s += 1f)
            {
                var (x, z) = road.Geometry.PointAt(s);
                float heading = road.Geometry.HeadingAt(s), y = y0 + (y1 - y0) * s / road.Length;
                foreach (float offset in new[] { -2.9f, 0f, 2.9f })
                    highest = MathF.Max(highest, terrain.HeightAt(x - MathF.Sin(heading) * offset, z + MathF.Cos(heading) * offset) - y);
            }
        }

        Assert.True(highest < 0.3f, $"the ground stands {highest:F2} m above a road");
    }
}
