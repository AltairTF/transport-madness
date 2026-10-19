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

    /// <summary>Side of a chunk, in cells: a renderer can rebuild the meshes of just the chunks that changed.</summary>
    public const int ChunkCells = 32;
    /// <summary>Chunks per side of the map.</summary>
    public int ChunksPerSide => (Cells + ChunkCells - 1) / ChunkCells;

    readonly float[] _heights;
    readonly HashSet<int> _changedChunks = new();

    Terrain(int cells, float cellSize)
    {
        if (cells < 1) throw new ArgumentOutOfRangeException(nameof(cells));
        if (!(cellSize > 0f)) throw new ArgumentOutOfRangeException(nameof(cellSize));
        Cells = cells;
        CellSize = cellSize;
        _heights = new float[(cells + 1) * (cells + 1)];
    }

    /// <summary>A level map at height zero.</summary>
    public static Terrain Flat(int cells = 512, float cellSize = 8f) => new(cells, cellSize);

    /// <summary>Rolling hills with flat plains between them, up to about <paramref name="amplitude"/> metres high.</summary>
    public static Terrain Generate(ulong seed, int cells = 512, float cellSize = 8f, float amplitude = 70f)
    {
        var terrain = new Terrain(cells, cellSize);
        const float baseWavelength = 1400f;
        for (int j = 0; j <= cells; j++)
            for (int i = 0; i <= cells; i++)
            {
                float x = (i - cells * 0.5f) * cellSize, z = (j - cells * 0.5f) * cellSize;
                float n = 0f, a = 1f, total = 0f, f = 1f / baseWavelength;
                for (int octave = 0; octave < 5; octave++)
                {
                    n += a * ValueNoise(seed, octave, x * f, z * f);
                    total += a;
                    a *= 0.5f;
                    f *= 2f;
                }
                n /= total; // 0..1, centred near 0.5
                // Push the middle band down to plains and keep the tops hilly.
                float shaped = MathF.Max(0f, n - 0.38f) / 0.62f;
                terrain._heights[j * terrain.Samples + i] = amplitude * shaped * shaped * 1.6f;
            }
        return terrain;
    }

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

    static float ValueNoise(ulong seed, int octave, float x, float z)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float fx = x - ix, fz = z - iz;
        float u = fx * fx * (3f - 2f * fx), v = fz * fz * (3f - 2f * fz);
        float a = Lattice(seed, octave, ix, iz), b = Lattice(seed, octave, ix + 1, iz);
        float c = Lattice(seed, octave, ix, iz + 1), d = Lattice(seed, octave, ix + 1, iz + 1);
        return (a * (1f - u) + b * u) * (1f - v) + (c * (1f - u) + d * u) * v;
    }

    /// <summary>Pseudo-random value in [0, 1) for a lattice corner.</summary>
    static float Lattice(ulong seed, int octave, int ix, int iz)
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
