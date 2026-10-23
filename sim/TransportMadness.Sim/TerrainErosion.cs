namespace TransportMadness.Sim;

static partial class TerrainGenerator
{
    static readonly (int Di, int Dj)[] Neighbours = { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };

    /// <summary>
    /// Finds where the water of every cell of a grid goes. The land is filled from the edge inward, lowest first, so that every cell
    /// has a path down to the edge (the sea, or the side of the map); the cell it was reached from is where its water goes. Fills
    /// <paramref name="order"/> with the cells so that the one a cell drains to comes before it, and <paramref name="flow"/> with the cells
    /// of catchment of each one (itself counted).
    /// </summary>
    static void Drain(float[] dem, int hn, float[] filled, int[] receiver, int[] order, float[] flow)
    {
        var closed = new bool[hn * hn];
        var queue = new PriorityQueue<int, (float, int)>(hn * 4);
        for (int j = 0; j < hn; j++)
            for (int i = 0; i < hn; i++)
            {
                int k = j * hn + i;
                receiver[k] = -1;
                if (i != 0 && j != 0 && i != hn - 1 && j != hn - 1) continue;
                filled[k] = dem[k];
                closed[k] = true;
                queue.Enqueue(k, (filled[k], k));
            }
        int count = 0;
        while (queue.TryDequeue(out int cell, out _))
        {
            order[count++] = cell;
            int ci = cell % hn, cj = cell / hn;
            foreach (var (di, dj) in Neighbours)
            {
                int ni = ci + di, nj = cj + dj;
                if (ni < 0 || nj < 0 || ni >= hn || nj >= hn) continue;
                int nk = nj * hn + ni;
                if (closed[nk]) continue;
                closed[nk] = true;
                filled[nk] = MathF.Max(dem[nk], filled[cell] + Epsilon);
                receiver[nk] = cell;
                queue.Enqueue(nk, (filled[nk], nk));
            }
        }
        Array.Fill(flow, 1f);
        for (int o = count - 1; o >= 0; o--)
        {
            int cell = order[o];
            if (receiver[cell] >= 0) flow[receiver[cell]] += flow[cell];
        }
    }

    /// <summary>
    /// Erodes the relief on the 16 m drainage grid and returns what it changed there (metres, mostly below zero). Each round finds the
    /// drainage of the land as it now is; every stream then lowers its bed toward the cell downstream in proportion to the square
    /// root of its catchment (stream power, solved from the mouth upward so it is stable), and slopes steeper than <see cref="Talus"/>
    /// slump into the lower cells beside them. Water, and the ground that is not above the plains, is left alone.
    /// </summary>
    static float[] Erode(Context c, int rounds)
    {
        var terrain = c.Terrain;
        int hn = c.DrainCells, cells = hn * hn;
        float origin = -hn * DrainCell * 0.5f;
        var h = new float[cells];
        var floor = new float[cells];
        var wobble = new float[cells];
        var dry = new bool[cells];
        Parallel.For(0, hn, j =>
        {
            for (int i = 0; i < hn; i++)
            {
                int k = j * hn + i;
                float x = origin + (i + 0.5f) * DrainCell, z = origin + (j + 0.5f) * DrainCell;
                h[k] = GroundFrom(c, x, z, Relief(c, x, z));
                float surface = terrain.WaterSurfaceAt(x, z);
                floor[k] = c.Water ? surface + PlainsHeight + Tilt(c, x, z) : Tilt(c, x, z) + 0.5f;
                dry[k] = !c.Water || h[k] > surface + 0.3f;
                // A little roughness in the routing only, so the streams wander instead of running along the grid and its diagonals.
                wobble[k] = (Terrain.ValueNoise(c.Seed, 80, x / 61f, z / 61f) - 0.5f) * 5f + (Terrain.ValueNoise(c.Seed, 81, x / 23f, z / 23f) - 0.5f) * 2.5f;
            }
        });
        var before = (float[])h.Clone();

        var filled = new float[cells];
        var receiver = new int[cells];
        var order = new int[cells];
        var flow = new float[cells];
        var next = new float[cells];
        var route = new float[cells];
        var shift = new float[cells];
        float perCell = StreamPower / 60f; // 60 = sqrt(3600), the catchment the constant is stated for
        for (int round = 0; round < rounds; round++)
        {
            for (int k = 0; k < cells; k++) route[k] = h[k] + wobble[k];
            Drain(route, hn, filled, receiver, order, flow);
            Array.Copy(h, next, cells);
            foreach (int k in order)
            {
                int p = receiver[k];
                if (p < 0 || !dry[k] || filled[k] > route[k] + 1e-4f) continue;
                float run = (k % hn != p % hn && k / hn != p / hn) ? DrainCell * 1.4142f : DrainCell;
                float f = perCell * MathF.Sqrt(flow[k]) * DrainCell / run;
                float lowered = (h[k] + f * next[p]) / (1f + f);
                if (lowered < h[k]) next[k] = MathF.Max(lowered, floor[k]);
            }
            (h, next) = (next, h);
            for (int pass = 0; pass < 2; pass++) Slump(h, dry, floor, shift, hn);
        }
        var delta = new float[cells];
        for (int k = 0; k < cells; k++) delta[k] = dry[k] ? h[k] - before[k] : 0f;
        // One blur takes the last of the grid out of what the streams cut.
        var soft = (float[])delta.Clone();
        for (int j = 1; j < hn - 1; j++)
            for (int i = 1; i < hn - 1; i++)
            {
                int k = j * hn + i;
                if (!dry[k]) continue;
                soft[k] = (4f * delta[k] + 2f * (delta[k - 1] + delta[k + 1] + delta[k - hn] + delta[k + hn]) + delta[k - hn - 1] + delta[k - hn + 1] + delta[k + hn - 1] + delta[k + hn + 1]) / 16f;
            }
        return soft;
    }

    /// <summary>One pass of slope collapse: a cell steeper than the talus over a lower neighbour hands part of the excess to it.</summary>
    static void Slump(float[] h, bool[] dry, float[] floor, float[] shift, int hn)
    {
        Array.Clear(shift);
        for (int j = 1; j < hn - 1; j++)
            for (int i = 1; i < hn - 1; i++)
            {
                int k = j * hn + i;
                if (!dry[k]) continue;
                foreach (var (di, dj) in Neighbours)
                {
                    int nk = k + dj * hn + di;
                    if (!dry[nk]) continue;
                    float run = di != 0 && dj != 0 ? DrainCell * 1.4142f : DrainCell;
                    float excess = h[k] - h[nk] - Talus * run;
                    if (excess <= 0f) continue;
                    shift[k] -= 0.05f * excess;
                    shift[nk] += 0.05f * excess;
                }
            }
        for (int k = 0; k < h.Length; k++)
            if (dry[k]) h[k] = MathF.Max(floor[k], h[k] + shift[k]);
    }

    /// <summary>The erosion change at a point, interpolated between the cells of the drainage grid.</summary>
    static float DeltaAt(Context c, float x, float z)
    {
        int hn = c.DrainCells;
        float gx = Math.Clamp(x / DrainCell + hn * 0.5f - 0.5f, 0f, hn - 1f), gz = Math.Clamp(z / DrainCell + hn * 0.5f - 0.5f, 0f, hn - 1f);
        int i = Math.Min((int)gx, hn - 2), j = Math.Min((int)gz, hn - 2);
        float fx = gx - i, fz = gz - j;
        var d = c.Delta!;
        return (d[j * hn + i] * (1f - fx) + d[j * hn + i + 1] * fx) * (1f - fz) + (d[(j + 1) * hn + i] * (1f - fx) + d[(j + 1) * hn + i + 1] * fx) * fz;
    }
}
