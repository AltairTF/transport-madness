using System;
using System.Collections.Generic;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// Trees (M7e): visual only. They are not in the simulation, the save or the hash, and no random stream is touched. A tree stands at a fixed jittered spot of a grid
/// with a fixed random number; where it is drawn depends on the ground (sparse on grass, patches of forest, none on steep ground, in water or above the tree line)
/// and on what has been built (none on a road, a track, a building, an industry, a gateway or a depot). Whenever something is built the set is worked out again.
/// </summary>
public partial class SimHost
{
    const float TreeCell = 16f;
    const float TreeRebuildSeconds = 3f;
    const string TreeModel = "tree";
    const float TreeChunk = 512f; // trees are drawn by a MultiMesh for each square of this size, so the camera culls the squares it does not see
    const float TreeRangeEnd = 2800f; // beyond this distance from the camera a square is not drawn (it fades out over the last 280 m)

    sealed record TreeSpot(float X, float Z, float Pick, float Yaw, float Size);

    readonly Dictionary<(int, int), ModelDraw> _treeChunks = new();
    Node3D? _treeRoot;
    TreeSpot[] _treeSpots = Array.Empty<TreeSpot>();
    Terrain? _treeTerrain;
    int _treeSignature = int.MinValue;
    double _treeAge = 1e9;
    ulong _treeSeed;
    int _treeCount;

    /// <summary>How many trees are drawn now.</summary>
    public int TreeCount => _treeCount;

    void UpdateTrees()
    {
        if (_terrain == null) return;
        _treeRoot ??= new Node3D { Name = "Trees" };
        if (_treeRoot.GetParent() == null) AddChild(_treeRoot);
        _treeRoot.Visible = _overlay == MapOverlay.None && !_layersOff.Contains("trees"); // under a map view they would stay bright while the rest goes dark
        _treeAge += GetProcessDeltaTime();
        if (_treeTerrain != _terrain) { _treeSpots = Array.Empty<TreeSpot>(); _treeTerrain = _terrain; _treeSignature = int.MinValue; }
        int signature = HashCode.Combine(Sim.Network.Version, Sim.Rail.Version, _terrain.Version, Sim.BuildingsVersion, Sim.IndustriesVersion, Sim.GatewaysVersion, Sim.Depots.Count, Sim.RailDepots.Count);
        if (signature == _treeSignature || _treeAge < TreeRebuildSeconds) return;
        _treeSignature = signature;
        _treeAge = 0;
        RebuildTrees();
    }

    void RebuildTrees()
    {
        if (_treeSpots.Length == 0) MakeTreeSpots();
        var net = Sim.Network;
        var obstacles = new List<(float X, float Z, float R)>();
        float industryRadius = 40f;
        foreach (var industry in Sim.Industries) obstacles.Add((industry.X, industry.Z, industryRadius));
        foreach (var gateway in Sim.Gateways) obstacles.Add((gateway.X, gateway.Z, 40f));
        foreach (var depot in Sim.Depots)
        {
            if (depot.FreeStanding) obstacles.Add((depot.X, depot.Z, 26f));
            else if (depot.Node >= 0) { var (x, z) = net.GetNode(depot.Node); obstacles.Add((x, z, 26f)); }
        }
        foreach (var depot in Sim.RailDepots) { var (x, z) = Sim.Rail.GetNode(depot.Node); obstacles.Add((x, z, 28f)); }
        var forests = new List<(float X, float Z)>();
        foreach (var industry in Sim.Industries)
            if (Sim.IndustryTuning.Types[industry.Type].Id == "forest") forests.Add((industry.X, industry.Z));

        int variants = Math.Max(1, ChunkOfTrees(0, 0).Variants(TreeModel));
        foreach (var chunk in _treeChunks.Values) chunk.Begin();
        _treeCount = 0;
        foreach (var spot in _treeSpots)
        {
            float p = TreeDensity(spot.X, spot.Z, forests);
            if (spot.Pick >= p) continue;
            float h = _terrain.HeightAt(spot.X, spot.Z);
            if (h < _terrain.WaterSurfaceAt(spot.X, spot.Z) + 1.2f) continue;
            float treeLine = 55f + 30f * spot.Pick; // the tree line is ragged
            if (h > treeLine) continue;
            float gx = _terrain.HeightAt(spot.X + 3f, spot.Z) - _terrain.HeightAt(spot.X - 3f, spot.Z);
            float gz = _terrain.HeightAt(spot.X, spot.Z + 3f) - _terrain.HeightAt(spot.X, spot.Z - 3f);
            if (MathF.Abs(gx) > 3.4f || MathF.Abs(gz) > 3.4f) continue; // steeper than about 30 degrees
            if (Blocked(spot.X, spot.Z, obstacles)) continue;
            int variant = (int)(spot.Pick * 9973f) % variants;
            ChunkOfTrees(spot.X, spot.Z).Add(TreeModel, spot.X, h - TerrainSink, spot.Z, MathF.Cos(spot.Yaw), MathF.Sin(spot.Yaw), 0f, variant, null, spot.Size);
            _treeCount++;
        }
        foreach (var chunk in _treeChunks.Values) chunk.End();
    }

    ModelDraw ChunkOfTrees(float x, float z)
    {
        var key = ((int)MathF.Floor(x / TreeChunk), (int)MathF.Floor(z / TreeChunk));
        if (!_treeChunks.TryGetValue(key, out var chunk))
            _treeChunks[key] = chunk = new ModelDraw(_treeRoot!, rangeEnd: TreeRangeEnd, rangeMargin: TreeRangeEnd / 10f);
        return chunk;
    }

    bool Blocked(float x, float z, List<(float X, float Z, float R)> obstacles)
    {
        foreach (var (ox, oz, r) in obstacles)
            if ((ox - x) * (ox - x) + (oz - z) * (oz - z) < r * r) return true;
        if (Sim.Network.RoadAt(x, z, 11f) != null) return true;
        if (Sim.Rail.TrackAt(x, z, 10f) != null) return true;
        return Sim.BuildingsNear(x, z, 20f).Count > 0;
    }

    /// <summary>The chance (0 to 1) that a spot has a tree: a few on open ground, patches of wood, and a thick wood around a Forest industry.</summary>
    float TreeDensity(float x, float z, List<(float X, float Z)> forests)
    {
        // The land says where the woods are (its forest biome); elsewhere a few trees stand on open ground.
        float p = _terrain.BiomeAtPoint(x, z) switch
        {
            Biome.Forest => 0.72f,
            Biome.Field => 0.025f,
            Biome.Swamp => 0.12f,
            _ => 0f,
        };
        foreach (var (fx, fz) in forests)
        {
            float d = MathF.Sqrt((fx - x) * (fx - x) + (fz - z) * (fz - z));
            p = MathF.Max(p, 0.9f * Mathf.SmoothStep(130f, 55f, d));
        }
        return p;
    }

    void MakeTreeSpots()
    {
        _treeSeed = TreeSeed();
        int side = (int)(_terrain.Cells * _terrain.CellSize / TreeCell);
        float half = _terrain.HalfSize;
        var spots = new List<TreeSpot>(side * side / 4);
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                ulong h = Mix(_treeSeed ^ ((ulong)i << 32 | (uint)j));
                float x = (i + Unit(ref h)) * TreeCell - half, z = (j + Unit(ref h)) * TreeCell - half;
                float pick = Unit(ref h), yaw = Unit(ref h) * MathF.Tau, size = 0.65f + 0.5f * Unit(ref h);
                if (pick < TreeDensityFloor) spots.Add(new TreeSpot(x, z, pick / TreeDensityFloor, yaw, size));
            }
        _treeSpots = spots.ToArray();
    }

    // Spots whose number is above this never get a tree wherever they are: the thickest wood has a chance of 0.9 at most.
    const float TreeDensityFloor = 0.9f;

    /// <summary>Differs from map to map: made from the towns, which the player cannot move.</summary>
    ulong TreeSeed()
    {
        ulong seed = 0x9E3779B97F4A7C15UL;
        foreach (var town in Sim.Towns) seed = Mix(seed ^ (ulong)BitConverter.SingleToInt32Bits(town.X) ^ ((ulong)BitConverter.SingleToInt32Bits(town.Z) << 21));
        return seed;
    }

    float ValueNoise(float x, float z, int octave)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float fx = Mathf.SmoothStep(0f, 1f, x - ix), fz = Mathf.SmoothStep(0f, 1f, z - iz);
        float a = Lattice(ix, iz, octave), b = Lattice(ix + 1, iz, octave), c = Lattice(ix, iz + 1, octave), d = Lattice(ix + 1, iz + 1, octave);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
    }

    float Lattice(int ix, int iz, int octave) => (Mix(_treeSeed ^ (ulong)octave * 0x100000001B3UL ^ ((ulong)(uint)ix << 32 | (uint)iz)) >> 40) / (float)(1UL << 24);

    static ulong Mix(ulong v)
    {
        v += 0x9E3779B97F4A7C15UL;
        v = (v ^ (v >> 30)) * 0xBF58476D1CE4E5B9UL;
        v = (v ^ (v >> 27)) * 0x94D049BB133111EBUL;
        return v ^ (v >> 31);
    }

    static float Unit(ref ulong state)
    {
        state = Mix(state);
        return (state >> 40) / (float)(1UL << 24);
    }
}
