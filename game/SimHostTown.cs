using System;
using System.Collections.Generic;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// What a street looks like inside a town (docs/town-upgrade-plan.md, D): kerb and pavement on both sides of an urban road, street lamps along it,
/// trees on the pavement, the ground under the blocks tinted, and a junction disc only where roads really meet. All of it is visual: it is not in the
/// simulation, the save or the hash. Whether a road is urban comes from the simulation (<see cref="Simulation.IsUrbanRoad"/>).
/// </summary>
public partial class SimHost
{
    const float PavementWidth = 2.5f;     // matches streets.pavement in tuning.json
    const float KerbHeight = 0.12f;       // the pavement stands this far above the carriageway
    const float JunctionCut = 8.5f;       // the pavement stops this far from a junction (the radius of its disc)
    const float LampSpacing = 34f;
    const float LampHeight = 6f;
    const float TreeSpacing = 24f;
    const float BendAngle = 0.2f;         // two roads that turn more than this at a node (about 11 degrees) make a corner: it gets its disc
    const float UrbanCell = 32f;          // side of the squares the buildings are filed in for the ground tint
    const float UrbanFull = 14f, UrbanEnd = 32f; // the tint is full this near a building and gone this far

    StandardMaterial3D _pavementMaterial = null!, _lampPoleMaterial = null!, _lampHeadMaterial = null!;
    readonly List<Transform3D> _lampPoles = new(), _lampHeads = new();
    readonly Dictionary<(int, int), List<(float X, float Z)>> _urbanCells = new();
    Color _townGround = new Color(0.52f, 0.56f, 0.42f);

    void InitTownMaterials()
    {
        _pavementMaterial = Textured(new Color(0.62f, 0.61f, 0.58f), "paving", 3f);
        _lampPoleMaterial = Flat(new Color(0.22f, 0.23f, 0.25f));
        _lampHeadMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.9f, 0.7f), EmissionEnabled = true, Emission = new Color(1f, 0.82f, 0.45f), EmissionEnergyMultiplier = 0f };
    }

    /// <summary>The lamps come on at night: <paramref name="night"/> is 0 by day to 1 in the dark (lighting.gd works it out from the hour).</summary>
    public void SetNight(float night) => _lampHeadMaterial.EmissionEnergyMultiplier = Math.Clamp(night, 0f, 1f) * 4f;

    /// <summary>Whether a node needs its disc: where roads meet (not two roads that go on from each other) or one ends.</summary>
    static bool NeedsDisc(RoadNetwork net, int node)
    {
        var roads = net.RoadsAt(node);
        if (roads.Count != 2) return true;
        var a = net.GetRoad(roads[0]); var b = net.GetRoad(roads[1]);
        float ha = a.From == node ? a.Geometry.HeadingAt(0f) : a.Geometry.HeadingAt(a.Length) + MathF.PI;
        float hb = b.From == node ? b.Geometry.HeadingAt(0f) : b.Geometry.HeadingAt(b.Length) + MathF.PI;
        return MathF.Abs(MathF.IEEERemainder(ha - hb - MathF.PI, MathF.Tau)) > BendAngle;
    }

    /// <summary>
    /// A kerb and pavement along each side of an urban road, and its lamps (collected for <see cref="BuildLamps"/>), stopping short of a junction
    /// where the side streets open. Not on a bridge or a tunnel or a path.
    /// </summary>
    void AddStreetFurniture(RoadNetwork net, Road road, float y0, float y1, float width)
    {
        if (road.Structure != RoadStructure.None || road.Type == RoadType.Path || road.Type == RoadType.Dirt || !Sim.IsUrbanRoad(road.Id)) return;
        var g = road.Geometry;
        float from = NeedsDisc(net, road.From) ? JunctionCut : 0f, to = g.Length - (NeedsDisc(net, road.To) ? JunctionCut : 0f);
        if (to - from < 4f) return;
        float offset = (width + PavementWidth) * 0.5f;
        foreach (float side in new[] { -1f, 1f })
        {
            if (g.IsStraight)
            {
                var (x0, z0) = net.GetNode(road.From);
                var (x1, z1) = net.GetNode(road.To);
                float dx = (x1 - x0) / g.Length, dz = (z1 - z0) / g.Length;
                float mid = (from + to) * 0.5f, t = mid / g.Length;
                var centre = new Vector3(x0 + dx * mid - dz * offset * side, y0 + (y1 - y0) * t, z0 + dz * mid + dx * offset * side);
                AddRoadBox(centre, to - from, (y1 - y0) * (to - from) / g.Length, dx, dz, PavementWidth, 0.5f, KerbHeight + RoadHeight * 0.5f - 0.25f, _pavementMaterial);
            }
            else AddRoadRibbon(g, y0, y1, PavementWidth, RoadHeight * 0.5f + KerbHeight, _pavementMaterial, offset * side, from, to);
        }

        // Lamps: one every LampSpacing along the road, on alternate sides, at the kerb.
        int count = (int)((to - from - 12f) / LampSpacing);
        for (int k = 0; k <= count; k++)
        {
            float s = from + 6f + k * LampSpacing + ((to - from - 12f) - count * LampSpacing) * 0.5f;
            float sideOfLamp = (k + road.Id.Value) % 2 == 0 ? 1f : -1f;
            var (x, z) = g.PointAt(s);
            float h = g.HeadingAt(s);
            float lateral = (width * 0.5f + 0.6f) * sideOfLamp;
            float lx = x - MathF.Sin(h) * lateral, lz = z + MathF.Cos(h) * lateral;
            float y = y0 + (y1 - y0) * (s / g.Length) + RoadHeight * 0.5f + KerbHeight;
            _lampPoles.Add(new Transform3D(Basis.Identity, new Vector3(lx, y + LampHeight * 0.5f, lz)));
            _lampHeads.Add(new Transform3D(Basis.Identity, new Vector3(lx + MathF.Sin(h) * 0.5f * sideOfLamp, y + LampHeight, lz - MathF.Cos(h) * 0.5f * sideOfLamp)));
        }
    }

    /// <summary>Puts the lamps collected while the roads were drawn into two MultiMeshes (poles and heads).</summary>
    void BuildLamps()
    {
        AddInstances(new BoxMesh { Size = new Vector3(0.14f, LampHeight, 0.14f) }, _lampPoleMaterial, _lampPoles, shadows: true);
        AddInstances(new BoxMesh { Size = new Vector3(0.7f, 0.16f, 0.5f) }, _lampHeadMaterial, _lampHeads, shadows: false);
        _lampPoles.Clear();
        _lampHeads.Clear();
    }

    void AddInstances(Mesh mesh, Material material, List<Transform3D> where, bool shadows)
    {
        if (where.Count == 0) return;
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = where.Count };
        for (int i = 0; i < where.Count; i++) multi.SetInstanceTransform(i, where[i]);
        _roadRoot.AddChild(new MultiMeshInstance3D
        {
            Multimesh = multi,
            MaterialOverride = material,
            CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    // ---- Ground under the blocks ----------------------------------------------------------------------------------

    /// <summary>Files the buildings in squares so the terrain can tell how near a vertex is to one.</summary>
    void RefreshUrban()
    {
        _urbanCells.Clear();
        foreach (var b in Sim.Buildings)
        {
            if (b.State == BuildingState.Demolished) continue;
            var key = ((int)MathF.Floor(b.X / UrbanCell), (int)MathF.Floor(b.Z / UrbanCell));
            if (!_urbanCells.TryGetValue(key, out var list)) _urbanCells[key] = list = new List<(float, float)>();
            list.Add((b.X, b.Z));
        }
    }

    /// <summary>The colour of the ground at a point, tinted toward the town ground (lawn and paving) near buildings.</summary>
    Color UrbanTint(Color ground, float x, float z)
    {
        if (_urbanCells.Count == 0) return ground;
        int cx = (int)MathF.Floor(x / UrbanCell), cz = (int)MathF.Floor(z / UrbanCell);
        float best = UrbanEnd * UrbanEnd;
        for (int a = cx - 1; a <= cx + 1; a++)
            for (int b = cz - 1; b <= cz + 1; b++)
                if (_urbanCells.TryGetValue((a, b), out var list))
                    foreach (var (bx, bz) in list) best = MathF.Min(best, (bx - x) * (bx - x) + (bz - z) * (bz - z));
        float d = MathF.Sqrt(best);
        float w = 1f - Math.Clamp((d - UrbanFull) / (UrbanEnd - UrbanFull), 0f, 1f);
        return ground.Lerp(_townGround.SrgbToLinear(), w * 0.7f);
    }

    // ---- Trees on the pavement ------------------------------------------------------------------------------------

    /// <summary>A tree every <see cref="TreeSpacing"/> metres along each urban road, on alternate sides, on the pavement, clear of junctions and of buildings.</summary>
    void AddStreetTrees(int variants)
    {
        var net = Sim.Network;
        for (int r = 0; r < net.RoadCount; r++)
        {
            var road = net.GetRoad(new RoadId(r));
            if (road.Retired || road.Structure != RoadStructure.None || road.Type == RoadType.Path || road.Type == RoadType.Dirt || !Sim.IsUrbanRoad(road.Id)) continue;
            var g = road.Geometry;
            float width = (road.LanesForward + road.LanesBack) * Simulation.LaneWidth + (road.Type == RoadType.Avenue2x2 ? RoadNetwork.MedianWidth : 0f);
            float from = NeedsDisc(net, road.From) ? JunctionCut + 4f : 4f, to = g.Length - (NeedsDisc(net, road.To) ? JunctionCut + 4f : 4f);
            for (float s = from + 4f, k = 0; s < to; s += TreeSpacing, k++)
            {
                float side = ((int)k + r) % 2 == 0 ? 1f : -1f;
                var (x, z) = g.PointAt(s);
                float h = g.HeadingAt(s);
                float lateral = (width * 0.5f + PavementWidth * 0.6f) * side;
                float tx = x - MathF.Sin(h) * lateral, tz = z + MathF.Cos(h) * lateral;
                if (Sim.BuildingsNear(tx, tz, 9f).Count > 0) continue;
                uint hash = (uint)(r * 73856093) ^ (uint)((int)k * 19349663);
                float yaw = (hash % 628) / 100f;
                float size = 0.55f + (hash / 7 % 30) / 100f; // street trees are young and small
                ChunkOfTrees(tx, tz).Add(TreeModel, tx, _terrain.HeightAt(tx, tz) - TerrainSink, tz, MathF.Cos(yaw), MathF.Sin(yaw), 0f, (int)(hash % (uint)variants), null, size);
                _treeCount++;
            }
        }
    }
}
