using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// Map overlays (M6b): one at a time, the rest of the map dimmed, a legend for the HUD. Roads are coloured by a strip drawn just over them,
/// circles and the raid approaches are lines on the ground, and buildings and industries take the overlay's colours. Everything here only
/// reads the simulation; the traffic average is kept by this node from what it draws.
/// </summary>
public partial class SimHost
{
    /// <summary>The overlays, in the order of the HUD's bar (0 is none).</summary>
    public enum MapOverlay { None, Traffic, Risk, Condition, Coverage, AntiAircraft, Power, Staffing, Connectivity }

    static readonly string[] OverlayNames = { "Off", "Traffic", "Incident risk", "Road condition", "Coverage", "Anti-aircraft", "Power", "Staffing", "Connectivity" };

    static readonly Color Good = new(0.20f, 0.80f, 0.30f), Fair = new(0.95f, 0.80f, 0.20f), Bad = new(0.92f, 0.22f, 0.20f);
    static readonly Color Neutral = new(0.55f, 0.75f, 0.95f), Faint = new(0.32f, 0.34f, 0.38f);
    const float OverlayRefreshSeconds = 1f;
    const float RiskDays = 10f; // incidents counted for the risk overlay
    const float UncontrolledJunctionRisk = 0.25f; // added for each end of a road at a junction of three or more roads with no lights or signs (an incident counts 1)
    const float TrafficLowDensity = 12f, TrafficHighDensity = 40f; // vehicles per km of lane: green below the first, red from the second

    MapOverlay _overlay;
    MeshInstance3D _overlayMesh = null!;
    StandardMaterial3D _overlayMaterial = null!;
    double _overlayTimer;
    Godot.Collections.Dictionary _legend = new();
    readonly Dictionary<Material, Color> _litColours = new();

    // The traffic overlay's average: per road, vehicles on it, averaged over about an hour of the game (an exponential average in game time).
    double[] _trafficAverage = Array.Empty<double>();
    long _trafficSampledAt = -1;
    Simulation? _trafficSim;

    // What the building colours are worked out from for the overlays that colour them.
    HashSet<int> _coveredBuildings = new();
    HashSet<int> _coveredIndustries = new();

    /// <summary>The names of the overlays, for the HUD's bar.</summary>
    public string[] GetOverlayNames() => OverlayNames;

    public int Overlay => (int)_overlay;

    /// <summary>Shows overlay <paramref name="overlay"/> (see <see cref="MapOverlay"/>), 0 for none.</summary>
    public void SetOverlay(int overlay)
    {
        var next = (MapOverlay)Math.Clamp(overlay, 0, OverlayNames.Length - 1);
        if (next == _overlay) return;
        _overlay = next;
        EnsureOverlayNodes();
        DimMap(_overlay != MapOverlay.None);
        _overlayTimer = OverlayRefreshSeconds; // draw it now
        if (_overlay == MapOverlay.None)
        {
            _overlayMesh.Mesh = null;
            _legend = new Godot.Collections.Dictionary();
        }
        _buildingsVersion = -1;
    }

    /// <summary>The legend of the overlay on show: {title, rows: [{colour, text}], note}; empty with no overlay.</summary>
    public Godot.Collections.Dictionary GetOverlayLegend() => _legend;

    void EnsureOverlayNodes()
    {
        if (_overlayMesh != null) return;
        _overlayMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _overlayMesh = new MeshInstance3D { Name = "Overlay", MaterialOverride = _overlayMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_overlayMesh);
    }

    /// <summary>Darkens the ground, the water and the roads under an overlay (and lights them again).</summary>
    void DimMap(bool dim)
    {
        var materials = new List<StandardMaterial3D> { _terrainMaterial, _roadMaterial, _medianMaterial, _lineMaterial, _deckMaterial, _waterMaterial, _depotMaterial, _stationMaterial };
        if (GetNodeOrNull<MeshInstance3D>("Ground")?.MaterialOverride is StandardMaterial3D ground) materials.Add(ground);
        foreach (var m in materials)
        {
            if (!_litColours.ContainsKey(m)) _litColours[m] = m.AlbedoColor;
            var lit = _litColours[m];
            m.AlbedoColor = dim ? new Color(lit.R * 0.45f, lit.G * 0.45f, lit.B * 0.45f, lit.A) : lit;
        }
    }

    /// <summary>Called every frame: keeps the traffic average and redraws the overlay once a second.</summary>
    void UpdateOverlay(double delta)
    {
        SampleTraffic();
        if (_overlay == MapOverlay.None) return;
        _overlayTimer += delta;
        if (_overlayTimer < OverlayRefreshSeconds) return;
        _overlayTimer = 0;
        RebuildOverlay();
        if (_overlay is MapOverlay.Coverage or MapOverlay.Power or MapOverlay.Staffing or MapOverlay.Connectivity or MapOverlay.AntiAircraft)
            _buildingsVersion = -1;
    }

    void SampleTraffic()
    {
        var net = Sim.Network;
        if (_trafficSim != Sim || _trafficAverage.Length != net.RoadCount)
        {
            if (_trafficSim != Sim) _trafficAverage = new double[net.RoadCount];
            else Array.Resize(ref _trafficAverage, net.RoadCount);
            _trafficSim = Sim;
            _trafficSampledAt = Sim.Tick;
        }
        double seconds = (Sim.Tick - _trafficSampledAt) * Simulation.StepSeconds;
        if (seconds < 2.0) return;
        _trafficSampledAt = Sim.Tick;
        var counts = new int[net.RoadCount];
        for (int i = 0; i < Sim.VehicleCount; i++)
        {
            var v = Sim.GetVehicle(i);
            if (v.Kind == VehicleKind.Removed || v.Kind == VehicleKind.Debris) continue;
            int road = net.GetLane(v.Lane).Road.Value;
            if (road >= 0 && road < counts.Length) counts[road]++;
        }
        double hour = Sim.DayLengthSeconds / 24.0;
        double keep = Math.Exp(-seconds / hour);
        for (int r = 0; r < counts.Length; r++) _trafficAverage[r] = _trafficAverage[r] * keep + counts[r] * (1.0 - keep);
    }

    // ---- Drawing --------------------------------------------------------------------------------------------

    void RebuildOverlay()
    {
        EnsureOverlayNodes();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int quads = 0;
        var rows = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        string note = "";
        void Row(Color c, string text) => rows.Add(new Godot.Collections.Dictionary { ["colour"] = c, ["text"] = text });

        switch (_overlay)
        {
            case MapOverlay.Traffic:
            {
                var net = Sim.Network;
                for (int r = 0; r < net.RoadCount && r < _trafficAverage.Length; r++)
                {
                    var road = net.GetRoad(new RoadId(r));
                    if (road.Retired) continue;
                    int lanes = road.ForwardLanes.Length + road.BackwardLanes.Length;
                    float density = (float)(_trafficAverage[r] / Math.Max(0.001, road.Length / 1000.0 * Math.Max(1, lanes)));
                    quads += RoadStrip(st, r, Ramp(density, TrafficLowDensity, TrafficHighDensity));
                }
                Row(Good, $"Under {TrafficLowDensity:0} vehicles a km of lane");
                Row(Fair, "Busy");
                Row(Bad, $"{TrafficHighDensity:0} or more: queues");
                note = "Averaged over the last hour of the game.";
                break;
            }
            case MapOverlay.Risk:
            {
                var risk = RoadRisk();
                for (int r = 0; r < risk.Length; r++)
                    if (risk[r] >= 0f) quads += RoadStrip(st, r, Ramp(risk[r], 0.5f, 2f));
                Row(Good, "Low");
                Row(Fair, "Some risk");
                Row(Bad, "High");
                note = $"Incidents on the road in the last {RiskDays:0} days, a worn surface (Condition under 60) and junctions of three or more roads with no lights or signs.";
                break;
            }
            case MapOverlay.Condition:
            {
                var net = Sim.Network;
                for (int r = 0; r < net.RoadCount; r++)
                {
                    if (net.GetRoad(new RoadId(r)).Retired) continue;
                    float c = Sim.RoadConditionOf(r);
                    quads += RoadStrip(st, r, Ramp(100f - c, 40f, 80f));
                }
                Row(Good, "Condition 60 or more");
                Row(Fair, "Worn");
                Row(Bad, "20 or less: slow and risky");
                note = "Resurface a road with the Resurface tool (Modify).";
                break;
            }
            case MapOverlay.Coverage:
            {
                RefreshCovered();
                float radius = Sim.Network.Tuning.Stations.CoverageRadius;
                foreach (var station in Sim.Stations)
                {
                    if (!station.Active || station.DockKind != 0) continue; // the stop at an industry covers that industry only
                    var p = StationPosition(station.Id);
                    quads += Ring(st, p.X, p.Z, radius, 2.5f, Neutral);
                }
                Row(Neutral, $"What a station covers ({radius:0} m)");
                Row(Good, "Covered building or industry");
                Row(Bad, "Not covered");
                break;
            }
            case MapOverlay.AntiAircraft:
            {
                var a = Sim.AntiAircraft;
                foreach (var industry in Sim.Industries)
                    if (Sim.IsBattery(industry))
                        quads += Ring(st, industry.X, industry.Z, a.Range, 4f, industry.DamagedUntil > Sim.Tick ? Faint : Neutral);
                var (ex, ez) = Sim.RaidEntryPoint();
                float release = Sim.BombReleaseDistance;
                var targets = Sim.RaidTargets()
                    .Where(t => t.Kind is RaidTargetKind.Industry or RaidTargetKind.Gateway or RaidTargetKind.Town)
                    .OrderByDescending(t => t.Weight).Take(12).ToList();
                foreach (var t in targets)
                {
                    float dx = ex - t.X, dz = ez - t.Z, len = MathF.Sqrt(dx * dx + dz * dz);
                    if (len < 1f) continue;
                    dx /= len; dz /= len;
                    float runIn = Math.Min(len, release + a.Range);
                    quads += Segment(st, t.X, t.Z, t.X + dx * runIn, t.Z + dz * runIn, 3f, new Color(1f, 1f, 1f, 0.55f));
                    quads += Segment(st, t.X + dx * (release - 60f), t.Z + dz * (release - 60f), t.X + dx * (release + 60f), t.Z + dz * (release + 60f), 9f, Bad);
                }
                string side = Simulation.DirectionName(Sim.RaidDirection);
                Row(Neutral, $"Gun range ({a.Range:0} m around each battery)");
                Row(new Color(1f, 1f, 1f), $"The run in to the likeliest targets, from the {side}");
                Row(Bad, $"Where the bombs are let go (about {release:0} m before the target)");
                note = targets.Count == 0 ? "Nothing on the map is worth a raid yet." : "Guns that reach the run in before the red mark fire before the bombs fall.";
                break;
            }
            case MapOverlay.Power:
            {
                Row(Good, "Gets all its power");
                Row(Fair, "Short of power");
                Row(Bad, "Plant without coal");
                note = $"Supply {Sim.PowerSupply:0} MW, demand {Sim.PowerDemand:0} MW: every industry runs at {Sim.PowerFactor * 100f:0}%.";
                break;
            }
            case MapOverlay.Staffing:
            {
                Row(Good, "All jobs filled");
                Row(Fair, "Half filled");
                Row(Bad, "Nobody came");
                note = "Industries with jobs, by the share of their jobs filled by residents who reached them yesterday.";
                break;
            }
            case MapOverlay.Connectivity:
            {
                Row(ReachedColour, "Home whose people reached work or leisure last month");
                Row(CutOffColour, "Home cut off");
                break;
            }
        }
        _overlayMesh.Mesh = quads > 0 ? st.Commit() : null;
        _legend = new Godot.Collections.Dictionary { ["title"] = OverlayNames[(int)_overlay], ["rows"] = rows, ["note"] = note };
    }

    /// <summary>Green below <paramref name="low"/>, yellow between, red from <paramref name="high"/>.</summary>
    static Color Ramp(float value, float low, float high)
    {
        if (value <= low) return Good.Lerp(Fair, Math.Clamp(value / Math.Max(low, 1e-3f), 0f, 1f) * 0.35f);
        if (value >= high) return Bad;
        float f = (value - low) / (high - low);
        return f < 0.5f ? Good.Lerp(Fair, 0.35f + f * 1.3f) : Fair.Lerp(Bad, (f - 0.5f) * 2f);
    }

    /// <summary>Per road: the risk score, -1 for a road that is gone.</summary>
    float[] RoadRisk()
    {
        var net = Sim.Network;
        var risk = new float[net.RoadCount];
        long since = Sim.Tick - (long)(RiskDays * Sim.DayLengthSeconds / Simulation.StepSeconds);
        foreach (var incident in Sim.Incidents)
        {
            if (incident.StartTick < since) continue;
            int road = net.GetLane(incident.Lane).Road.Value;
            if (road >= 0 && road < risk.Length) risk[road] += 1f;
        }
        var signed = new HashSet<int>(Sim.RoadSigns.Where(s => s.Active).Select(s => s.Node));
        for (int r = 0; r < risk.Length; r++)
        {
            var road = net.GetRoad(new RoadId(r));
            if (road.Retired) { risk[r] = -1f; continue; }
            risk[r] += Math.Max(0f, (60f - Sim.RoadConditionOf(r)) / 40f);
            foreach (int node in new[] { road.From, road.To })
                if (net.RoadsAt(node).Count >= 3 && Sim.LightAt(node) == null && !signed.Contains(node)) risk[r] += UncontrolledJunctionRisk;
        }
        return risk;
    }

    void RefreshCovered()
    {
        _coveredBuildings = new HashSet<int>();
        _coveredIndustries = new HashSet<int>();
        foreach (var station in Sim.Stations)
        {
            if (!station.Active) continue;
            foreach (int b in Sim.CoveredBy(station)) _coveredBuildings.Add(b);
            foreach (int i in Sim.IndustriesCoveredBy(station)) _coveredIndustries.Add(i);
        }
    }

    /// <summary>A strip over a road, slightly narrower than it, in <paramref name="colour"/>. Returns the quads added.</summary>
    int RoadStrip(SurfaceTool st, int roadId, Color colour)
    {
        var net = Sim.Network;
        var id = new RoadId(roadId);
        var road = net.GetRoad(id);
        var g = road.Geometry;
        bool avenue = road.Type == RoadType.Avenue2x2;
        float half = road.Type == RoadType.Path ? RoadNetwork.HalfWidth(RoadType.Path) * 0.9f : ((avenue ? 4 : 2) * Simulation.LaneWidth + (avenue ? RoadNetwork.MedianWidth : 0f)) * 0.45f;
        int n = Math.Max(1, (int)MathF.Ceiling(g.Length / 8f));
        var left = new Vector3[n + 1];
        var right = new Vector3[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float s = g.Length * i / n;
            var (x, z) = g.PointAt(s);
            float s2 = Math.Min(g.Length, s + 0.5f), s1 = Math.Max(0f, s - 0.5f);
            var (ax, az) = g.PointAt(s1);
            var (bx, bz) = g.PointAt(s2);
            float dx = bx - ax, dz = bz - az, len = MathF.Max(1e-4f, MathF.Sqrt(dx * dx + dz * dz));
            float nx = -dz / len, nz = dx / len;
            float y = net.RoadHeightAt(id, s) + 0.35f;
            left[i] = new Vector3(x + nx * half, y, z + nz * half);
            right[i] = new Vector3(x - nx * half, y, z - nz * half);
        }
        var c = new Color(colour.R, colour.G, colour.B, 0.85f);
        for (int i = 0; i < n; i++) Quad(st, left[i], right[i], right[i + 1], left[i + 1], c);
        return n;
    }

    /// <summary>A ring on the ground (radius <paramref name="radius"/>, <paramref name="width"/> wide). Returns the quads added.</summary>
    int Ring(SurfaceTool st, float cx, float cz, float radius, float width, Color colour)
    {
        int n = Math.Clamp((int)(radius / 6f), 32, 128);
        var c = new Color(colour.R, colour.G, colour.B, 0.8f);
        for (int i = 0; i < n; i++)
        {
            float a0 = MathF.Tau * i / n, a1 = MathF.Tau * (i + 1) / n;
            Vector3 P(float a, float r)
            {
                float x = cx + MathF.Cos(a) * r, z = cz + MathF.Sin(a) * r;
                return new Vector3(x, HeightAt(x, z) + 1.5f, z);
            }
            Quad(st, P(a0, radius - width * 0.5f), P(a0, radius + width * 0.5f), P(a1, radius + width * 0.5f), P(a1, radius - width * 0.5f), c);
        }
        return n;
    }

    /// <summary>A line on the ground from one point to another. Returns the quads added.</summary>
    int Segment(SurfaceTool st, float x0, float z0, float x1, float z1, float width, Color colour)
    {
        float dx = x1 - x0, dz = z1 - z0, len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1f) return 0;
        int n = Math.Max(1, (int)MathF.Ceiling(len / 20f));
        float nx = -dz / len * width * 0.5f, nz = dx / len * width * 0.5f;
        Vector3 P(float f, float side)
        {
            float x = x0 + dx * f + nx * side, z = z0 + dz * f + nz * side;
            return new Vector3(x, HeightAt(x, z) + 2f, z);
        }
        for (int i = 0; i < n; i++)
        {
            float f0 = (float)i / n, f1 = (float)(i + 1) / n;
            Quad(st, P(f0, 1f), P(f0, -1f), P(f1, -1f), P(f1, 1f), colour);
        }
        return n;
    }

    static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color colour)
    {
        foreach (var v in new[] { a, b, c, a, c, d })
        {
            st.SetColor(colour);
            st.AddVertex(v);
        }
    }

    // ---- Colours of buildings and industries ------------------------------------------------------------------

    /// <summary>The colour of an industry under the overlay (its own colour with none).</summary>
    Color OverlayIndustryColour(Industry industry, Color own)
    {
        var type = Sim.IndustryTuning.Types[industry.Type];
        switch (_overlay)
        {
            case MapOverlay.None:
                return own;
            case MapOverlay.Coverage:
                return _coveredIndustries.Contains(industry.Id) ? Good : Bad;
            case MapOverlay.Power:
                if (type.Supply > 0f)
                {
                    var status = Sim.StatusOf(industry);
                    return status.MissingInputs.Count == 0 && !status.Damaged ? Neutral : Bad;
                }
                return type.Demand > 0f ? (Sim.PowerFactor >= 0.999f ? Good : Fair.Lerp(Bad, 1f - Sim.PowerFactor)) : own.Lerp(DimmedColour, 0.75f);
            case MapOverlay.Staffing:
                return Sim.JobsOf(industry) > 0 ? Ramp((1f - Sim.StaffingOf(industry)) * 2f, 0.1f, 1.9f) : own.Lerp(DimmedColour, 0.75f);
            case MapOverlay.AntiAircraft:
                return Sim.IsBattery(industry) ? Neutral : own.Lerp(DimmedColour, 0.5f);
            default:
                return own.Lerp(DimmedColour, 0.7f);
        }
    }

    /// <summary>The colour of a building under the overlay (<paramref name="own"/>, its colour by kind and state, with none).</summary>
    Color OverlayBuildingColour(Building b, Color own) => _overlay switch
    {
        MapOverlay.None => own,
        MapOverlay.Connectivity => b.Type == BuildingType.Home && b.State == BuildingState.Active ? (b.Reached ? ReachedColour : CutOffColour) : own.Lerp(DimmedColour, 0.7f),
        MapOverlay.Coverage => b.State == BuildingState.Active ? (_coveredBuildings.Contains(b.Id) ? Good : Bad) : own.Lerp(DimmedColour, 0.7f),
        _ => own.Lerp(DimmedColour, 0.7f),
    };
}
