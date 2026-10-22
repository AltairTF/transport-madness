using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// Coverage along the streets on the map (M6h): the roads a station's walk reaches coloured by how well they are covered, the buildings above their roofs, and the preview of a station being placed.
/// The numbers come from the simulation (<c>Simulation.Reach.cs</c>); this draws them.
/// </summary>
public partial class SimHost
{
    MeshInstance3D? _reachMesh;
    int _reachPreviewKey = int.MinValue;
    Godot.Collections.Dictionary _reachSummary = new();

    /// <summary>Red where nothing reaches, through yellow, to green at full coverage.</summary>
    static Color CoverageColour(float coverage)
    {
        if (coverage <= 0f) return Bad;
        if (coverage >= 1f) return Good;
        return coverage > 0.5f ? Fair.Lerp(Good, (coverage - 0.5f) * 2f) : Bad.Lerp(Fair, coverage * 2f);
    }

    /// <summary>The roads that the walks of several stations reach, coloured along each by the best coverage any of them gives there. Returns the quads added.</summary>
    int ReachGradient(SurfaceTool st, IEnumerable<List<ReachRoad>> walks)
    {
        var byRoad = new Dictionary<int, List<ReachRoad>>();
        foreach (var walk in walks)
            foreach (var r in walk)
            {
                if (!byRoad.TryGetValue(r.Road.Value, out var list)) byRoad[r.Road.Value] = list = new List<ReachRoad>();
                list.Add(r);
            }
        int quads = 0;
        foreach (var (road, list) in byRoad)
            quads += RoadGradient(st, road, s =>
            {
                float best = 0f;
                foreach (var r in list) best = MathF.Max(best, Sim.CoverageAtWalk(r.WalkAt(s)));
                if (best <= 0.01f) return new Color(0, 0, 0, 0);
                var c = CoverageColour(best);
                return new Color(c.R, c.G, c.B, 0.4f + 0.5f * best);
            });
        return quads;
    }

    /// <summary>A flat square over the roof of each building, in the colour of its coverage. Returns the quads added.</summary>
    int ReachBuildingMarks(SurfaceTool st, Dictionary<int, float> coverage)
    {
        var tuning = Sim.TownTuning;
        int quads = 0;
        foreach (var (id, c) in coverage)
        {
            var b = Sim.Buildings[id];
            var tier = tuning.Of(b.Type, b.Tier);
            float half = tier.Size;
            float y = Math.Max(HeightAt(b.X, b.Z), _terrain.WaterSurfaceAt(b.X, b.Z)) + tier.Height + 1.2f;
            float fx = MathF.Cos(b.Facing), fz = MathF.Sin(b.Facing);
            Vector3 P(float u, float v) => new(b.X + fx * u * half - fz * v * half, y, b.Z + fz * u * half + fx * v * half);
            var colour = CoverageColour(c);
            Quad(st, P(-1, -1), P(1, -1), P(1, 1), P(-1, 1), new Color(colour.R, colour.G, colour.B, 0.9f));
            quads++;
        }
        return quads;
    }

    /// <summary>
    /// The coverage a station clicked for at (x, z) would give, drawn on the map and summed up: {valid, full (buildings in its circle), reached (outside it, along the streets), reached_share (their coverage
    /// added up, as buildings), already (of the buildings in its circle, those another station covers already), industries (in its circle), radius, reach}. Drawn again only when the place changes.
    /// </summary>
    public Godot.Collections.Dictionary StationReachPreview(float x, float z)
    {
        var plan = Sim.PlanStationReach(x, z);
        if (plan == null)
        {
            ClearReachPreview();
            return new Godot.Collections.Dictionary { ["valid"] = false };
        }
        int key = HashCode.Combine(plan.SourceRoad?.Value ?? -1, (int)MathF.Round(plan.SourceAlong * 2f), (int)MathF.Round(plan.X), (int)MathF.Round(plan.Z), Sim.Network.Version, Sim.Stations.Count, Sim.BuildingsVersion);
        if (key == _reachPreviewKey && _reachMesh?.Mesh != null) return _reachSummary;
        _reachPreviewKey = key;

        if (_reachMesh == null)
        {
            EnsureOverlayNodes();
            _reachMesh = new MeshInstance3D { Name = "ReachPreview", MaterialOverride = _overlayMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_reachMesh);
        }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int quads = Ring(st, plan.X, plan.Z, Sim.Network.Tuning.Stations.CoverageRadius, 2.5f, Neutral);
        quads += ReachGradient(st, new[] { Sim.ReachRoads(plan.Walk, plan.SourceRoad, plan.SourceAlong) });
        var coverage = new Dictionary<int, float>();
        foreach (int b in plan.Circle) coverage[b] = 1f;
        foreach (var r in plan.Reached) coverage[r.Building] = r.Coverage;
        quads += ReachBuildingMarks(st, coverage);
        _reachMesh.Mesh = quads > 0 ? st.Commit() : null;

        var already = new HashSet<int>();
        foreach (var station in Sim.Stations)
            if (station.Active) foreach (int b in Sim.CoveredBy(station)) already.Add(b);
        _reachSummary = new Godot.Collections.Dictionary
        {
            ["valid"] = true, ["full"] = plan.Circle.Count, ["reached"] = plan.Reached.Count, ["reached_share"] = plan.Reached.Sum(r => r.Coverage),
            ["already"] = plan.Circle.Count(already.Contains), ["industries"] = plan.Industries,
            ["radius"] = Sim.Network.Tuning.Stations.CoverageRadius, ["reach"] = Sim.Network.Tuning.Stations.ReachRadius,
        };
        return _reachSummary;
    }

    /// <summary>Takes the station coverage preview off the map.</summary>
    public void ClearReachPreview()
    {
        _reachPreviewKey = int.MinValue;
        if (_reachMesh != null) _reachMesh.Mesh = null;
    }
}
