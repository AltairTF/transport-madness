namespace TransportMadness.Sim;

/// <summary>
/// Square heightmap centred on the origin: <see cref="Cells"/> x <see cref="Cells"/> cells of <see cref="CellSize"/> metres,
/// with <c>Cells + 1</c> height samples per side. Heights are generated from a seed with integer-hashed value noise, so
/// the same seed gives the same map everywhere. Building a road carves the terrain (cut and fill), which bumps
/// <see cref="Version"/> so the renderer knows to rebuild.
/// </summary>
public sealed class Terrain
{
    public int Cells { get; }
    public float CellSize { get; }
    /// <summary>Distance from the origin to each edge of the map.</summary>
    public float HalfSize => Cells * CellSize * 0.5f;
    /// <summary>Height samples per side.</summary>
    public int Samples => Cells + 1;
    /// <summary>Changes whenever any height changes.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Height of the water surface at the middle of the map: ground below the surface is under water (lakes, rivers,
    /// sea). The surface falls steadily toward one side of the map (<see cref="WaterSlopeX"/>, <see cref="WaterSlopeZ"/>),
    /// so rivers run downhill to the sea and the water is not at one height everywhere; see <see cref="WaterSurfaceAt"/>.
    /// </summary>
    public const float WaterLevel = 0f;

    /// <summary>Change of the water surface height per metre along x (negative: it falls toward +x).</summary>
    public float WaterSlopeX { get; }
    /// <summary>Change of the water surface height per metre along z.</summary>
    public float WaterSlopeZ { get; }

    /// <summary>Side of a chunk, in cells: a renderer can rebuild the meshes of just the chunks that changed.</summary>
    public const int ChunkCells = 32;
    /// <summary>Chunks per side of the map.</summary>
    public int ChunksPerSide => (Cells + ChunkCells - 1) / ChunkCells;

    readonly float[] _heights;
    readonly HashSet<int> _changedChunks = new();

    Terrain(int cells, float cellSize, float waterSlopeX = 0f, float waterSlopeZ = 0f)
    {
        if (cells < 1) throw new ArgumentOutOfRangeException(nameof(cells));
        if (!(cellSize > 0f)) throw new ArgumentOutOfRangeException(nameof(cellSize));
        Cells = cells;
        CellSize = cellSize;
        WaterSlopeX = waterSlopeX;
        WaterSlopeZ = waterSlopeZ;
        _heights = new float[(cells + 1) * (cells + 1)];
    }

    /// <summary>A level map at height zero.</summary>
    public static Terrain Flat(int cells = 512, float cellSize = 8f) => new(cells, cellSize);

    /// <summary>A map whose height at each sample is <paramref name="shape"/>(x, z), for scenarios and tests; the water surface is level unless a slope is given.</summary>
    public static Terrain Create(int cells, float cellSize, Func<float, float, float> shape, float waterSlopeX = 0f, float waterSlopeZ = 0f)
    {
        var terrain = new Terrain(cells, cellSize, waterSlopeX, waterSlopeZ);
        for (int j = 0; j <= cells; j++)
            for (int i = 0; i <= cells; i++)
                terrain._heights[j * terrain.Samples + i] = shape((i - cells * 0.5f) * cellSize, (j - cells * 0.5f) * cellSize);
        return terrain;
    }

    /// <summary>Height of the water surface above a point (where there is water, and where there would be if the ground were lower).</summary>
    public float WaterSurfaceAt(float x, float z) => WaterLevel + WaterSlopeX * x + WaterSlopeZ * z;

    /// <summary>Height of the water surface at a height sample.</summary>
    public float WaterSurfaceAtSample(int i, int j) => WaterSurfaceAt((i - Cells * 0.5f) * CellSize, (j - Cells * 0.5f) * CellSize);

    /// <summary>Whether the ground at a point lies under water.</summary>
    public bool IsWater(float x, float z) => HeightAt(x, z) < WaterSurfaceAt(x, z);

    /// <summary>
    /// A generated map: mountain belts, hills and plains, rivers that follow the drainage of the land down to the sea or
    /// out of the lowlands, lakes, a ragged coast and a biome for each height sample (see <see cref="TerrainGenerator"/>).
    /// With <paramref name="water"/>, the water surface falls toward one side of the map (a direction drawn from the seed)
    /// and ground below the surface is under water; the plains themselves sit a little above it. The same seed and settings
    /// give the same land at any <paramref name="cellSize"/>, only the detail differs, so a coarse map can preview a fine one.
    /// </summary>
    public static Terrain Generate(ulong seed, int cells = 512, float cellSize = 8f, float amplitude = 70f, bool water = true, TerrainSettings? settings = null)
    {
        float downhill = Lattice(seed, 20, 0, 0) * MathF.Tau; // direction in which the water surface falls
        float dx = MathF.Cos(downhill), dz = MathF.Sin(downhill);
        var terrain = water ? new Terrain(cells, cellSize, -WaterFall * dx, -WaterFall * dz) : new Terrain(cells, cellSize);
        TerrainGenerator.Fill(terrain, seed, amplitude, water, settings ?? TerrainSettings.Default, dx, dz);
        return terrain;
    }

    /// <summary>The height samples, for the generator to fill.</summary>
    internal float[] Heights => _heights;
    byte[]? _biomes;
    internal void SetBiomes(byte[] biomes) => _biomes = biomes;

    /// <summary>What covers the ground at a height sample (<see cref="Biome.Field"/> on a map that was not generated).</summary>
    public Biome BiomeAt(int i, int j) => _biomes == null ? Biome.Field : (Biome)_biomes[Clamp(j) * Samples + Clamp(i)];

    /// <summary>The biome at the height sample nearest to a world position.</summary>
    public Biome BiomeAtPoint(float x, float z) => BiomeAt((int)MathF.Round(x / CellSize + Cells * 0.5f), (int)MathF.Round(z / CellSize + Cells * 0.5f));

    const float WaterFall = 0.0012f;   // metres the water surface falls per metre toward the sea

    public float GetSample(int i, int j) => _heights[Clamp(j) * Samples + Clamp(i)];

    int Clamp(int index) => Math.Clamp(index, 0, Cells);

    /// <summary>Bilinear height at a world position; outside the map the nearest edge height.</summary>
    public float HeightAt(float x, float z)
    {
        float gx = x / CellSize + Cells * 0.5f, gz = z / CellSize + Cells * 0.5f;
        gx = Math.Clamp(gx, 0f, Cells);
        gz = Math.Clamp(gz, 0f, Cells);
        int i = Math.Min((int)gx, Cells - 1), j = Math.Min((int)gz, Cells - 1);
        float fx = gx - i, fz = gz - j;
        float h00 = _heights[j * Samples + i], h10 = _heights[j * Samples + i + 1];
        float h01 = _heights[(j + 1) * Samples + i], h11 = _heights[(j + 1) * Samples + i + 1];
        return (h00 * (1f - fx) + h10 * fx) * (1f - fz) + (h01 * (1f - fx) + h11 * fx) * fz;
    }

    /// <summary>
    /// Levels the ground under a straight road whose centre line runs from (x0,z0) at height y0 to (x1,z1) at height y1.
    /// See <see cref="CarvePath"/>.
    /// </summary>
    public void CarveRoad(float x0, float z0, float y0, float x1, float z1, float y1, float halfWidth, float shoulder) =>
        CarvePath(new[] { (x0, z0, y0), (x1, z1, y1) }, halfWidth, shoulder);

    /// <summary>
    /// Levels the ground under a road whose centre line is the given polyline (x, z, road height): the carriageway
    /// becomes the road surface and the ground blends back to its old height over <paramref name="shoulder"/> metres.
    /// </summary>
    public void CarvePath(IReadOnlyList<(float X, float Z, float Y)> path, float halfWidth, float shoulder)
    {
        if (path.Count < 2) return;
        // Every height sample that bilinear interpolation reads under the carriageway must be level, so the flat zone
        // is never narrower than about one cell diagonal.
        halfWidth = MathF.Max(halfWidth, CellSize * 1.42f);
        float reach = halfWidth + shoulder;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var (x, z, _) in path)
        {
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
        }
        int iMin = Clamp((int)MathF.Floor((minX - reach) / CellSize + Cells * 0.5f));
        int iMax = Clamp((int)MathF.Ceiling((maxX + reach) / CellSize + Cells * 0.5f));
        int jMin = Clamp((int)MathF.Floor((minZ - reach) / CellSize + Cells * 0.5f));
        int jMax = Clamp((int)MathF.Ceiling((maxZ + reach) / CellSize + Cells * 0.5f));
        bool changed = false;
        for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float px = (i - Cells * 0.5f) * CellSize, pz = (j - Cells * 0.5f) * CellSize;
                float best = float.MaxValue, road = 0f;
                for (int k = 0; k + 1 < path.Count; k++)
                {
                    var (x0, z0, y0) = path[k];
                    var (x1, z1, y1) = path[k + 1];
                    float ex = x1 - x0, ez = z1 - z0;
                    float length2 = ex * ex + ez * ez;
                    if (length2 < 1e-9f) continue;
                    float t = Math.Clamp(((px - x0) * ex + (pz - z0) * ez) / length2, 0f, 1f);
                    float dx = px - (x0 + ex * t), dz = pz - (z0 + ez * t);
                    float d2 = dx * dx + dz * dz;
                    if (d2 < best) { best = d2; road = y0 + (y1 - y0) * t; }
                }
                float distance = MathF.Sqrt(best);
                if (distance >= reach) continue;
                float blend = distance <= halfWidth ? 1f : 1f - (distance - halfWidth) / shoulder;
                blend = blend * blend * (3f - 2f * blend);
                ref float h = ref _heights[j * Samples + i];
                float target = h + (road - h) * blend;
                if (target != h) { h = target; changed = true; }
            }
        if (changed)
        {
            Version++;
            MarkChanged(iMin, iMax, jMin, jMax);
        }
    }

    /// <summary>What levelling a pad changes: the volumes of ground taken away (cut) and put on (fill) in cubic metres, and the largest drop and rise of any height sample in metres.</summary>
    public readonly record struct PadChange(float Cut, float Fill, float MaxCut, float MaxFill);

    /// <summary>
    /// Levels the ground under a square of half-side <paramref name="half"/> centred at (x, z) and turned by <paramref name="angle"/> to <paramref name="height"/>, blending back
    /// to the old ground over <paramref name="shoulder"/> metres (as <see cref="CarvePath"/> does for a road). Samples for which <paramref name="skip"/> is true (under a road)
    /// are left alone. With <paramref name="apply"/> false nothing changes and the result is what it would change.
    /// </summary>
    public PadChange LevelPad(float x, float z, float angle, float half, float height, float shoulder, bool apply, Func<float, float, bool>? skip = null) =>
        LevelPad(x, z, angle, half, half, height, shoulder, apply, skip);

    /// <summary>The same for a rectangle: <paramref name="halfAlong"/> along the direction <paramref name="angle"/> and <paramref name="halfAcross"/> across it.</summary>
    public PadChange LevelPad(float x, float z, float angle, float halfAlong, float halfAcross, float height, float shoulder, bool apply, Func<float, float, bool>? skip = null)
    {
        var change = default(PadChange);
        foreach (var (_, _, delta, i, j) in PadSamples(x, z, angle, halfAlong, halfAcross, height, shoulder, skip))
        {
            float area = CellSize * CellSize;
            if (delta < 0f) change = change with { Cut = change.Cut - delta * area, MaxCut = MathF.Max(change.MaxCut, -delta) };
            else change = change with { Fill = change.Fill + delta * area, MaxFill = MathF.Max(change.MaxFill, delta) };
            if (apply) _heights[j * Samples + i] += delta;
        }
        if (apply && (change.Cut > 0f || change.Fill > 0f))
        {
            Version++;
            float reach = (MathF.Max(halfAlong, halfAcross) + CellSize) * 1.4143f + shoulder;
            MarkChanged(Clamp((int)MathF.Floor((x - reach) / CellSize + Cells * 0.5f)), Clamp((int)MathF.Ceiling((x + reach) / CellSize + Cells * 0.5f)),
                Clamp((int)MathF.Floor((z - reach) / CellSize + Cells * 0.5f)), Clamp((int)MathF.Ceiling((z + reach) / CellSize + Cells * 0.5f)));
        }
        return change;
    }

    /// <summary>
    /// The height samples a pad changes: the world place of each, how much it would change (negative: cut) and its indices. Every sample that bilinear interpolation reads under the
    /// footprint is level (the footprint is a cell larger), and the ground blends back over <paramref name="shoulder"/> metres. A sample for which <paramref name="skip"/> is true is left out.
    /// </summary>
    public IEnumerable<(float X, float Z, float Delta, int I, int J)> PadSamples(float x, float z, float angle, float halfAlong, float halfAcross, float height, float shoulder, Func<float, float, bool>? skip = null)
    {
        halfAlong += CellSize;
        halfAcross += CellSize;
        float reach = MathF.Max(halfAlong, halfAcross) * 1.4143f + shoulder;
        int iMin = Clamp((int)MathF.Floor((x - reach) / CellSize + Cells * 0.5f));
        int iMax = Clamp((int)MathF.Ceiling((x + reach) / CellSize + Cells * 0.5f));
        int jMin = Clamp((int)MathF.Floor((z - reach) / CellSize + Cells * 0.5f));
        int jMax = Clamp((int)MathF.Ceiling((z + reach) / CellSize + Cells * 0.5f));
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        for (int j = jMin; j <= jMax; j++)
            for (int i = iMin; i <= iMax; i++)
            {
                float px = (i - Cells * 0.5f) * CellSize, pz = (j - Cells * 0.5f) * CellSize;
                float dx = px - x, dz = pz - z;
                float lx = MathF.Abs(dx * cos + dz * sin), lz = MathF.Abs(-dx * sin + dz * cos);
                float ox = MathF.Max(lx - halfAlong, 0f), oz = MathF.Max(lz - halfAcross, 0f);
                float distance = MathF.Sqrt(ox * ox + oz * oz);
                if (distance >= shoulder) continue;
                if (skip != null && skip(px, pz)) continue;
                float blend = distance <= 0f ? 1f : 1f - distance / shoulder;
                blend = blend * blend * (3f - 2f * blend);
                float delta = (height - _heights[j * Samples + i]) * blend;
                if (delta == 0f) continue;
                yield return (px, pz, delta, i, j);
            }
    }

    /// <summary>The mean height of the ground under a square (a grid of points across it), the pad height when the player has not raised or lowered it.</summary>
    public float MeanHeight(float x, float z, float angle, float half) => MeanHeight(x, z, angle, half, half);

    /// <summary>The same for a rectangle.</summary>
    public float MeanHeight(float x, float z, float angle, float halfAlong, float halfAcross)
    {
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle), sum = 0f;
        const int n = 5;
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                float lx = (a / (n - 1f) * 2f - 1f) * halfAlong, lz = (b / (n - 1f) * 2f - 1f) * halfAcross;
                sum += HeightAt(x + lx * cos - lz * sin, z + lx * sin + lz * cos);
            }
        return sum / (n * n);
    }

    /// <summary>
    /// Chunks (index = row * <see cref="ChunksPerSide"/> + column) whose mesh is out of date since the last call, in
    /// ascending order. A mesh depends on the samples one step beyond its edge (for normals), so neighbours are included.
    /// </summary>
    public List<int> TakeChangedChunks()
    {
        var chunks = _changedChunks.ToList();
        chunks.Sort();
        _changedChunks.Clear();
        return chunks;
    }

    void MarkChanged(int iMin, int iMax, int jMin, int jMax)
    {
        int last = ChunksPerSide - 1;
        int c0 = Math.Clamp((iMin - 2) / ChunkCells, 0, last), c1 = Math.Clamp((iMax + 1) / ChunkCells, 0, last);
        int r0 = Math.Clamp((jMin - 2) / ChunkCells, 0, last), r1 = Math.Clamp((jMax + 1) / ChunkCells, 0, last);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++) _changedChunks.Add(r * ChunksPerSide + c);
    }

    /// <summary>Order-independent hash of every height; used by tests to compare two maps.</summary>
    public ulong Hash()
    {
        ulong h = 14695981039346656037UL;
        foreach (float height in _heights)
        {
            h ^= BitConverter.SingleToUInt32Bits(height);
            h *= 1099511628211UL;
        }
        return h;
    }

    internal static float ValueNoise(ulong seed, int octave, float x, float z)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float fx = x - ix, fz = z - iz;
        float u = fx * fx * (3f - 2f * fx), v = fz * fz * (3f - 2f * fz);
        float a = Lattice(seed, octave, ix, iz), b = Lattice(seed, octave, ix + 1, iz);
        float c = Lattice(seed, octave, ix, iz + 1), d = Lattice(seed, octave, ix + 1, iz + 1);
        return (a * (1f - u) + b * u) * (1f - v) + (c * (1f - u) + d * u) * v;
    }

    /// <summary>Pseudo-random value in [0, 1) for a lattice corner.</summary>
    internal static float Lattice(ulong seed, int octave, int ix, int iz)
    {
        ulong h = seed + 0x9E3779B97F4A7C15UL * (ulong)(octave + 1);
        h ^= (ulong)(uint)ix * 0xBF58476D1CE4E5B9UL;
        h ^= (ulong)(uint)iz * 0x94D049BB133111EBUL;
        h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27; h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;
        return (h >> 40) * (1f / (1UL << 24));
    }
}
