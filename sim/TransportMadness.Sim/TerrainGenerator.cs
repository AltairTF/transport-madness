namespace TransportMadness.Sim;

/// <summary>What covers the ground at a height sample.</summary>
public enum Biome : byte { Field, Forest, Rock, Sand, Swamp, Snow, Water }

/// <summary>
/// How much of each feature a generated map has, each from 0 (little) to 1 (a lot); 0.5 is the usual map.
/// </summary>
public sealed record TerrainSettings(float Mountains = 0.5f, float Water = 0.5f, float Forests = 0.5f)
{
    public static readonly TerrainSettings Default = new();
}

/// <summary>
/// Builds the heights and biomes of a generated map. The land is a function of the position (mountain belts along ridge lines,
/// hills with the slope-damped noise that looks eroded, plains, a rise toward the far edge, a ragged coast and lakes), so a coarse
/// grid and a fine one show the same land. The rivers come from a drainage pass on a fixed 16 m grid: the land is filled so every
/// cell drains to the sea or the edge, the flow of every cell is summed down its path, and the cells that carry enough water are
/// cut into the land as channels with a flat bed and valley walls, graded to the water surface where the land is low. Big rivers
/// cut deeper and wider, so they stay wet farther inland and leave a wide flat floodplain.
/// </summary>
static class TerrainGenerator
{
    const float PlainsHeight = 2.5f;   // land starts this far above the water
    const float WaterDepth = 4f;       // how far the middle of a lake or river lies below the surface
    const float SeaWidth = 400f;       // the sea reaches this far in from the edge of the map
    const float SeaShore = 120f;       // and its shore slopes over this distance
    const float SeaMinimumApproach = 0.3f; // an edge the water runs toward at a shallower angle than this has no sea
    const float RiseInland = 3.5f;      // how much higher the far edge is than the sea edge, besides the hills
    const float DrainCell = 16f;       // metres per cell of the drainage grid
    const float Epsilon = 0.01f;       // fall forced on flat ground so water drains

    sealed class Context
    {
        public ulong Seed;
        public Terrain Terrain = null!;
        public float Half, Amplitude, Dx, Dz, Range;
        public bool Water;
        public TerrainSettings Settings = null!;
    }

    public static void Fill(Terrain terrain, ulong seed, float amplitude, bool water, TerrainSettings settings, float dx, float dz)
    {
        var c = new Context
        {
            Seed = seed, Terrain = terrain, Half = terrain.HalfSize, Amplitude = amplitude, Dx = dx, Dz = dz, Water = water,
            Range = terrain.HalfSize * (MathF.Abs(dx) + MathF.Abs(dz)),
            Settings = new TerrainSettings(Math.Clamp(settings.Mountains, 0f, 1f), Math.Clamp(settings.Water, 0f, 1f), Math.Clamp(settings.Forests, 0f, 1f)),
        };
        int n = terrain.Samples;
        var heights = terrain.Heights;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                heights[j * n + i] = Ground(c, (i - terrain.Cells * 0.5f) * terrain.CellSize, (j - terrain.Cells * 0.5f) * terrain.CellSize);

        var nearRiver = new float[n * n];
        if (water) CutRivers(c, heights, nearRiver);
        terrain.SetBiomes(Biomes(c, heights, nearRiver));
    }

    // ---- the land ----

    /// <summary>Height of the ground at a point before the rivers are cut: the sea and the lakes are in it.</summary>
    static float Ground(Context c, float x, float z)
    {
        var s = c.Settings;
        float proj = (x * c.Dx + z * c.Dz) / Math.Max(c.Range, 1f);
        float inland = 0.5f - 0.5f * Math.Clamp(proj, -1f, 1f); // 0 at the sea side, 1 at the far side
        // Bend the coordinates so that ridges and valleys do not run straight.
        float wx = x + (Terrain.ValueNoise(c.Seed, 30, x / 900f, z / 900f) - 0.5f) * 420f;
        float wz = z + (Terrain.ValueNoise(c.Seed, 31, x / 900f + 17f, z / 900f) - 0.5f) * 420f;

        float scale = c.Amplitude / 70f;
        // Rolling hills, damped where the ground is already steep (what erosion leaves: gentle slopes with sharp detail on the tops).
        float n = Fbm(c.Seed, 0, wx, wz, 1000f, 5);
        float shaped = MathF.Max(0f, n - 0.47f) / 0.53f;
        // Highland and lowland: big stretches of the map are plain, with only gentle swells; hills rise in the rest.
        float upland = Smooth(0.58f - 0.2f * s.Mountains, 0.74f - 0.2f * s.Mountains, Terrain.ValueNoise(c.Seed, 11, wx / 2000f, wz / 2000f));
        float hills = scale * (30f + 30f * s.Mountains) * MathF.Pow(shaped, 2.4f) * 1.8f * upland + 2.2f * shaped;
        // Mountain belts: a few long ridge lines, with ridged noise on them.
        float belt = Smooth(0.80f - 0.08f * s.Mountains, 0.98f, 1f - MathF.Abs(2f * Terrain.ValueNoise(c.Seed, 12, wx / 2800f, wz / 2800f) - 1f));
        float ridged = Smooth(0.55f, 0.95f, 1f - MathF.Abs(2f * Fbm(c.Seed, 13, wx, wz, 1100f, 5) - 1f));
        float mountains = scale * (14f + 85f * MathF.Pow(s.Mountains, 1.3f)) * belt * ridged * ridged * (0.55f + 0.45f * inland);
        float rel = PlainsHeight + RiseInland * MathF.Pow(inland, 1.1f) + hills + mountains
            + (Terrain.ValueNoise(c.Seed, 14, x / 45f, z / 45f) - 0.5f) * 0.7f * Smooth(0f, 6f, hills + mountains);

        if (!c.Water) return rel - PlainsHeight + 0.5f;
        float surface = c.Terrain.WaterSurfaceAt(x, z);
        float land = surface + rel;
        // Lakes sit in the lowlands, in the highs of a noise field; the more water the map has, the more of the field is a lake.
        float lakeEdge = 0.80f - 0.12f * s.Water;
        float lake = Smooth(lakeEdge, lakeEdge + 0.07f, Terrain.ValueNoise(c.Seed, 9, wx / 800f, wz / 800f) * 0.75f + Terrain.ValueNoise(c.Seed, 10, wx / 300f, wz / 300f) * 0.25f);
        float lowland = 1f - Smooth(PlainsHeight + 3f, PlainsHeight + 10f, rel);
        float mask = lake * lowland;
        // The sea takes the stretch along each edge the water runs toward, with a ragged coast that is straight at the very edge.
        float seaWidth = SeaWidth * (0.7f + 0.9f * s.Water);
        float coast = (Terrain.ValueNoise(c.Seed, 15, x / 520f, z / 520f) - 0.5f) * 150f + (Terrain.ValueNoise(c.Seed, 16, x / 170f, z / 170f) - 0.5f) * 40f;
        mask = MathF.Max(mask, Sea(c.Half - x, c.Dx, seaWidth, coast));
        mask = MathF.Max(mask, Sea(c.Half + x, -c.Dx, seaWidth, coast));
        mask = MathF.Max(mask, Sea(c.Half - z, c.Dz, seaWidth, coast));
        mask = MathF.Max(mask, Sea(c.Half + z, -c.Dz, seaWidth, coast));
        return land + (surface - WaterDepth - land) * mask;
    }

    static float Sea(float inland, float toward, float seaWidth, float coast)
    {
        if (toward < SeaMinimumApproach) return 0f;
        float width = seaWidth * toward;
        return 1f - Smooth(width - SeaShore, width, inland + coast * Smooth(60f, 260f, inland));
    }

    /// <summary>Noise of several octaves, each lessened where the slope of the ones before is steep; in [0, 1], centred near 0.5.</summary>
    static float Fbm(ulong seed, int octave0, float x, float z, float wavelength, int octaves)
    {
        float sum = 0f, total = 0f, a = 1f, f = 1f / wavelength, gx = 0f, gz = 0f;
        for (int o = 0; o < octaves; o++)
        {
            var (v, dx, dz) = Noise(seed, octave0 + o, x * f, z * f);
            gx += a * dx; gz += a * dz;
            sum += a * v / (1f + 1.4f * (gx * gx + gz * gz));
            total += a;
            a *= 0.5f;
            f *= 2f;
        }
        return sum / total;
    }

    /// <summary>Value noise and its gradient, in lattice units.</summary>
    static (float V, float Dx, float Dz) Noise(ulong seed, int octave, float x, float z)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float fx = x - ix, fz = z - iz;
        float u = fx * fx * (3f - 2f * fx), v = fz * fz * (3f - 2f * fz);
        float du = 6f * fx * (1f - fx), dv = 6f * fz * (1f - fz);
        float a = Terrain.Lattice(seed, octave, ix, iz), b = Terrain.Lattice(seed, octave, ix + 1, iz);
        float cc = Terrain.Lattice(seed, octave, ix, iz + 1), d = Terrain.Lattice(seed, octave, ix + 1, iz + 1);
        float value = (a * (1f - u) + b * u) * (1f - v) + (cc * (1f - u) + d * u) * v;
        return (value, ((b - a) * (1f - v) + (d - cc) * v) * du, ((cc - a) * (1f - u) + (d - b) * u) * dv);
    }

    static float Smooth(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // ---- rivers ----

    readonly record struct Reach(float X, float Z, float Bed, float Radius, float BedHalfWidth);

    static void CutRivers(Context c, float[] heights, float[] nearRiver)
    {
        var terrain = c.Terrain;
        int hn = (int)MathF.Round(terrain.Cells * terrain.CellSize / DrainCell);
        float origin = -hn * DrainCell * 0.5f;
        var dem = new float[hn * hn];
        for (int j = 0; j < hn; j++)
            for (int i = 0; i < hn; i++)
            {
                float x = origin + (i + 0.5f) * DrainCell, z = origin + (j + 0.5f) * DrainCell;
                // A little roughness that the cut will smooth away makes the water wander instead of running straight down the slope.
                dem[j * hn + i] = Ground(c, x, z) + (Terrain.ValueNoise(c.Seed, 43, x / 75f, z / 75f) - 0.5f) * 3.5f + (Terrain.ValueNoise(c.Seed, 44, x / 28f, z / 28f) - 0.5f) * 1.6f;
            }

        // Fill the land from the edge inward, lowest first, so that every cell has a path down to the edge (the sea, or the side of
        // the map); the cell it was reached from is where its water goes.
        var filled = new float[hn * hn];
        var receiver = new int[hn * hn];
        var order = new int[hn * hn];
        var closed = new bool[hn * hn];
        var queue = new PriorityQueue<int, (float, int)>();
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
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int ni = ci + di, nj = cj + dj;
                    if ((di == 0 && dj == 0) || ni < 0 || nj < 0 || ni >= hn || nj >= hn) continue;
                    int nk = nj * hn + ni;
                    if (closed[nk]) continue;
                    closed[nk] = true;
                    filled[nk] = MathF.Max(dem[nk], filled[cell] + Epsilon);
                    receiver[nk] = cell;
                    queue.Enqueue(nk, (filled[nk], nk));
                }
        }
        var flow = new float[hn * hn];
        for (int k = 0; k < hn * hn; k++) flow[k] = 1f;
        for (int o = count - 1; o >= 0; o--)
        {
            int cell = order[o];
            if (receiver[cell] >= 0) flow[receiver[cell]] += flow[cell];
        }

        // The cells that carry enough water are river; the more water the map has, the smaller the streams that count.
        float threshold = 3600f - 2200f * c.Settings.Water;
        var reaches = new List<Reach>();
        for (int k = 0; k < hn * hn; k++)
        {
            if (flow[k] < threshold || receiver[k] < 0) continue;
            int i = k % hn, j = k / hn;
            float x = origin + (i + 0.5f) * DrainCell, z = origin + (j + 0.5f) * DrainCell;
            if (dem[k] < terrain.WaterSurfaceAt(x, z) - 0.5f) continue; // already sea or lake
            // Meander: the whole river is shifted by a slow field, so its course is not the straight steps of the grid.
            float mx = (Terrain.ValueNoise(c.Seed, 40, x / 330f, z / 330f) - 0.5f) * 110f + (Terrain.ValueNoise(c.Seed, 45, x / 110f, z / 110f) - 0.5f) * 36f;
            float mz = (Terrain.ValueNoise(c.Seed, 41, x / 330f + 9f, z / 330f) - 0.5f) * 110f + (Terrain.ValueNoise(c.Seed, 46, x / 110f + 5f, z / 110f) - 0.5f) * 36f;
            x += mx; z += mz;
            float size = MathF.Sqrt(flow[k] / threshold);
            float depth = MathF.Min(WaterDepth, 1.4f + 0.9f * size);
            float surfaceHere = terrain.WaterSurfaceAt(x, z);
            // A big river is graded to the water surface for as long as the land is not too high; a small one cuts a channel of its own depth.
            float bed = MathF.Max(surfaceHere - depth, filled[k] - (3f + 4f * size));
            if (size >= 1.7f && filled[k] - surfaceHere < 34f) bed = surfaceHere - depth;
            float drop = Math.Max(0f, filled[k] - bed);
            reaches.Add(new Reach(x, z, bed, Math.Min(150f, 18f + 14f * size + 2.2f * drop), Math.Min(11f, 6f + 0.8f * size)));
        }

        // Cut each reach into the grid, always from the ground as it was so the order does not matter.
        var original = (float[])heights.Clone();
        int n = terrain.Samples;
        float cell8 = terrain.CellSize, half = terrain.Cells * 0.5f;
        foreach (var r in reaches)
        {
            float reach = r.Radius * 1.3f;
            int i0 = Math.Max(0, (int)MathF.Floor((r.X - reach) / cell8 + half)), i1 = Math.Min(n - 1, (int)MathF.Ceiling((r.X + reach) / cell8 + half));
            int j0 = Math.Max(0, (int)MathF.Floor((r.Z - reach) / cell8 + half)), j1 = Math.Min(n - 1, (int)MathF.Ceiling((r.Z + reach) / cell8 + half));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float px = (i - half) * cell8, pz = (j - half) * cell8;
                    float ddx = px - r.X, ddz = pz - r.Z;
                    float d = MathF.Sqrt(ddx * ddx + ddz * ddz) * (0.8f + 0.4f * Terrain.ValueNoise(c.Seed, 42, px / 55f, pz / 55f));
                    int idx = j * n + i;
                    nearRiver[idx] = MathF.Max(nearRiver[idx], d <= r.BedHalfWidth ? 1f : MathF.Max(0f, 1f - (d - r.BedHalfWidth) / (r.Radius + 60f)));
                    if (d >= r.Radius) continue;
                    float w = d <= r.BedHalfWidth ? 1f : 1f - Smooth(r.BedHalfWidth, r.Radius, d);
                    float h = original[idx];
                    if (r.Bed >= h) continue;
                    heights[idx] = MathF.Max(MathF.Min(heights[idx], h + (r.Bed - h) * w), terrain.WaterSurfaceAtSample(i, j) - WaterDepth);
                }
        }
    }

    // ---- biomes ----

    static byte[] Biomes(Context c, float[] heights, float[] nearRiver)
    {
        var terrain = c.Terrain;
        int n = terrain.Samples;
        var biomes = new byte[n * n];
        int stride = Math.Max(1, (int)MathF.Round(DrainCell / terrain.CellSize));
        float treeLine = Math.Max(0f, 0.78f - 0.30f * c.Settings.Forests);
        float half = terrain.Cells * 0.5f;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                float x = (i - half) * terrain.CellSize, z = (j - half) * terrain.CellSize;
                float h = heights[k], surface = terrain.WaterSurfaceAtSample(i, j), rel = h - surface;
                if (rel < 0f) { biomes[k] = (byte)Biome.Water; continue; }
                float gx = (heights[j * n + Math.Min(n - 1, i + stride)] - heights[j * n + Math.Max(0, i - stride)]) / (2f * stride * terrain.CellSize);
                float gz = (heights[Math.Min(n - 1, j + stride) * n + i] - heights[Math.Max(0, j - stride) * n + i]) / (2f * stride * terrain.CellSize);
                float slope = MathF.Sqrt(gx * gx + gz * gz);
                Biome biome = Biome.Field;
                if (rel > 105f) biome = Biome.Snow;
                else if (slope > 0.7f || rel > 72f) biome = Biome.Rock;
                else if ((rel < 1.2f && NearWater(heights, terrain, i, j, stride)) || nearRiver[k] >= 1f) biome = Biome.Sand;
                else if (rel < 2.4f && nearRiver[k] > 0.3f && Terrain.ValueNoise(c.Seed, 17, x / 90f, z / 90f) > 0.5f) biome = Biome.Swamp;
                else if (slope < 0.55f && rel < 78f)
                {
                    float f = 0.62f * Terrain.ValueNoise(c.Seed, 18, x / 260f, z / 260f) + 0.38f * Terrain.ValueNoise(c.Seed, 19, x / 70f, z / 70f);
                    if (f + 0.1f * nearRiver[k] > treeLine + 0.0f) biome = Biome.Forest;
                }
                biomes[k] = (byte)biome;
            }
        return biomes;
    }

    static bool NearWater(float[] heights, Terrain terrain, int i, int j, int stride)
    {
        int n = terrain.Samples;
        for (int d = 1; d <= 2; d++)
            foreach (var (di, dj) in new[] { (d * stride, 0), (-d * stride, 0), (0, d * stride), (0, -d * stride) })
            {
                int ni = Math.Clamp(i + di, 0, n - 1), nj = Math.Clamp(j + dj, 0, n - 1);
                if (heights[nj * n + ni] < terrain.WaterSurfaceAtSample(ni, nj)) return true;
            }
        return false;
    }
}
