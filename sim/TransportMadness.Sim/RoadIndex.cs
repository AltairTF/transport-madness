namespace TransportMadness.Sim;

/// <summary>
/// Which roads are near a point, for the questions towns ask (does this building still have a road?). A coarse grid
/// of the roads' centre lines, built for one state of the network; rebuild it when <see cref="RoadNetwork.Version"/> changes.
/// </summary>
public sealed class RoadIndex
{
    const float CellSize = 64f;
    const float SampleSpacing = 24f; // roads are registered at points this far apart, so a query looks this much further

    readonly RoadNetwork _net;
    readonly Dictionary<long, List<int>> _cells = new();

    public RoadIndex(RoadNetwork net)
    {
        _net = net;
        for (int r = 0; r < net.RoadCount; r++)
        {
            var road = net.GetRoad(new RoadId(r));
            if (road.Retired) continue;
            var g = road.Geometry;
            int steps = Math.Max(1, (int)MathF.Ceiling(g.Length / SampleSpacing));
            long last = long.MinValue;
            for (int k = 0; k <= steps; k++)
            {
                var (x, z) = g.PointAt(g.Length * k / steps);
                long key = Key(Cell(x), Cell(z));
                if (key == last) continue;
                last = key;
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
                if (list.Count == 0 || list[^1] != r) list.Add(r);
            }
        }
    }

    static int Cell(float v) => (int)MathF.Floor(v / CellSize);
    static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    /// <summary>Distance to the nearest road's centre line if that is within <paramref name="radius"/>, else infinity.</summary>
    public float DistanceToNearest(float x, float z, float radius)
    {
        float reach = radius + SampleSpacing * 0.5f;
        int x0 = Cell(x - reach), x1 = Cell(x + reach), z0 = Cell(z - reach), z1 = Cell(z + reach);
        float best = float.PositiveInfinity;
        for (int cx = x0; cx <= x1; cx++)
            for (int cz = z0; cz <= z1; cz++)
            {
                if (!_cells.TryGetValue(Key(cx, cz), out var list)) continue;
                foreach (int r in list)
                {
                    float d = _net.GetRoad(new RoadId(r)).Geometry.DistanceTo(x, z);
                    if (d < best) best = d;
                }
            }
        return best <= radius ? best : float.PositiveInfinity;
    }

    public bool HasRoadWithin(float x, float z, float radius) => DistanceToNearest(x, z, radius) <= radius;
}
