using System;
using System.Diagnostics;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// Owns the simulation, advances it in fixed steps against the frame clock, and draws it: roads as boxes and every
/// vehicle as one instance of a single MultiMesh filled in bulk each frame. The simulation never sees Godot (ADR 0001);
/// this node only reads its state. GDScript talks to it through the PascalCase methods below.
/// </summary>
public partial class SimHost : Node3D
{
    const int InitialCapacity = 2048;
    const int CrewCapacity = 256;
    const int FloatsPerInstance = 16; // 3x4 transform + RGBA colour
    const float RoadHeight = 0.1f;
    const float RoadSnapRadius = 12f;
    const float TerrainSink = 0.05f; // terrain is drawn this far below the road surface so roads never z-fight with it

    public Simulation Sim { get; private set; } = null!;

    float _speed = 1f;
    double _accumulator;
    readonly Stopwatch _clock = new();
    double _stepMsAverage;
    double _effectiveSpeed = 1;
    double _simSecondsThisSecond, _realSecondsThisSecond;

    MultiMesh _vehicles = null!;
    MultiMesh _crews = null!;
    readonly float[] _crewBuffer = new float[CrewCapacity * FloatsPerInstance];
    float[] _buffer = new float[InitialCapacity * FloatsPerInstance];
    int _capacity = InitialCapacity;
    Node3D _roadRoot = null!;
    MeshInstance3D _terrainMesh = null!;
    int _terrainVersion = -1;
    Terrain _terrain = null!, _terrainSource = null!;
    StandardMaterial3D _roadMaterial = null!, _medianMaterial = null!, _depotMaterial = null!, _lineMaterial = null!;

    // Per-lane drawing data: start point, unit direction and sideways offset of the lane centre (to the driver's right).
    float[] _laneX = Array.Empty<float>(), _laneZ = Array.Empty<float>();
    float[] _laneDx = Array.Empty<float>(), _laneDz = Array.Empty<float>();
    float[] _laneSide = Array.Empty<float>(), _laneLength = Array.Empty<float>();
    float[] _laneSweep = Array.Empty<float>(), _laneH0 = Array.Empty<float>(), _laneSinH0 = Array.Empty<float>(), _laneCosH0 = Array.Empty<float>();
    float[] _laneY = Array.Empty<float>(), _laneGrade = Array.Empty<float>();

    public override void _Ready()
    {
        _roadRoot = new Node3D { Name = "Roads" };
        AddChild(_roadRoot);
        _roadMaterial = Flat(new Color(0.23f, 0.24f, 0.26f));
        _medianMaterial = Flat(new Color(0.35f, 0.5f, 0.3f));
        _depotMaterial = Flat(new Color(0.85f, 0.25f, 0.2f));
        _lineMaterial = Flat(new Color(0.8f, 0.78f, 0.6f));

        // Beyond the edge of the terrain the world is just flat grass.
        var ground = new MeshInstance3D { Name = "Ground", Mesh = new PlaneMesh { Size = new Vector2(20000, 20000) } };
        ground.MaterialOverride = Flat(new Color(0.42f, 0.58f, 0.34f));
        ground.Position = new Vector3(0, -0.5f, 0);
        AddChild(ground);
        _terrainMesh = new MeshInstance3D { Name = "Terrain", MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f } };
        _terrainMesh.Position = new Vector3(0, -TerrainSink, 0);
        AddChild(_terrainMesh);

        var box = new BoxMesh { Size = new Vector3(Simulation.VehicleLength, 1.5f, Simulation.VehicleWidth) };
        _vehicles = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = box,
            InstanceCount = InitialCapacity,
            VisibleInstanceCount = 0,
        };
        var material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.8f };
        _crews = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new BoxMesh { Size = new Vector3(5.5f, 2.2f, 2.2f) },
            InstanceCount = CrewCapacity,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Name = "Crews", Multimesh = _crews, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(new MultiMeshInstance3D { Name = "Vehicles", Multimesh = _vehicles, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    static StandardMaterial3D Flat(Color color) => new() { AlbedoColor = color, Roughness = 1f };

    // ---- API for GDScript -----------------------------------------------------------------------------------

    /// <summary>Starts a fresh demo world with the given seed and number of ambient vehicles.</summary>
    public void NewDemoWorld(long seed, int vehicles)
    {
        var net = new RoadNetwork(Terrain.Flat());
        DemoWorld.Build(net);
        Sim = new Simulation(net, (ulong)seed);
        // The grid itself is the initial world; everything after it goes through commands so it is logged.
        foreach (int node in new[] { net.NodeCount / 5, net.NodeCount * 2 / 5, net.NodeCount * 3 / 5, net.NodeCount * 4 / 5 })
            Sim.Execute(new AddDepotCommand(node, DepotLevel.Simple));
        Sim.Execute(new SeedTrafficCommand(vehicles));
        RebuildRoads();
        _accumulator = 0;
    }

    /// <summary>Starts an empty flat map.</summary>
    public void NewEmptyWorld(long seed)
    {
        Sim = new Simulation(new RoadNetwork(Terrain.Flat()), (ulong)seed);
        RebuildRoads();
        _accumulator = 0;
    }

    /// <summary>Starts an empty map of generated hills.</summary>
    public void NewHillsWorld(long seed)
    {
        Sim = new Simulation(new RoadNetwork(Terrain.Generate((ulong)seed)), (ulong)seed);
        RebuildRoads();
        _accumulator = 0;
    }

    /// <summary>Terrain height under a point.</summary>
    public float HeightAt(float x, float z) => _terrain.HeightAt(x, z);

    /// <summary>
    /// Where a camera ray first meets the terrain: {hit, x, y, z}. Marches in 4 m steps, then bisects the last step.
    /// </summary>
    public Godot.Collections.Dictionary RayToGround(Vector3 origin, Vector3 direction)
    {
        const float step = 4f, maxDistance = 12000f;
        float previous = 0f;
        for (float t = 0f; t < maxDistance; t += step)
        {
            var p = origin + direction * t;
            if (p.Y <= _terrain.HeightAt(p.X, p.Z))
            {
                float lo = previous, hi = t;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    var q = origin + direction * mid;
                    if (q.Y <= _terrain.HeightAt(q.X, q.Z)) hi = mid; else lo = mid;
                }
                var hit = origin + direction * hi;
                return new Godot.Collections.Dictionary { ["hit"] = true, ["x"] = hit.X, ["y"] = hit.Y, ["z"] = hit.Z };
            }
            previous = t;
        }
        return new Godot.Collections.Dictionary { ["hit"] = false };
    }

    /// <summary>
    /// Snaps a point to the nearest node within <paramref name="radius"/>, else to the nearest road's centre line
    /// (a junction will be built there), else leaves it alone.
    /// </summary>
    public Godot.Collections.Dictionary SnapToNode(float x, float z, float radius)
    {
        var net = Sim.Network;
        int node = net.FindNodeNear(x, z, radius);
        if (node >= 0)
        {
            var (nx, nz) = net.GetNode(node);
            return new Godot.Collections.Dictionary { ["snapped"] = true, ["x"] = nx, ["y"] = net.GetNodeHeight(node), ["z"] = nz };
        }

        if (net.NearestOnRoad(x, z, RoadSnapRadius, 5f) is { } hit)
            return new Godot.Collections.Dictionary { ["snapped"] = true, ["x"] = hit.X, ["y"] = hit.Y, ["z"] = hit.Z };
        return new Godot.Collections.Dictionary { ["snapped"] = false, ["x"] = x, ["y"] = _terrain.HeightAt(x, z), ["z"] = z };
    }

    /// <summary>Whether a straight road can be built between the points: long enough, not too steep, not crossing too close to a junction.</summary>
    public bool CanBuildRoad(float x0, float z0, float x1, float z1) => Sim.Network.CanBuildRoad(x0, z0, x1, z1);

    /// <summary>The sweep (total turn in radians, signed) of the arc from the first point through the second to the third.</summary>
    public float SweepThrough(float x0, float z0, float xm, float zm, float x1, float z1) =>
        RoadGeometry.SweepThrough(x0, z0, xm, zm, x1, z1);

    /// <summary>What a straight road would cost: {valid, length, cut, fill, money}; only {valid} when it cannot be built.</summary>
    public Godot.Collections.Dictionary EstimateRoadCost(float x0, float z0, float x1, float z1, int type) =>
        EstimateCurveCost(x0, z0, x1, z1, type, 0f);

    /// <summary>What a road bending by <paramref name="sweep"/> radians would cost; see <see cref="EstimateRoadCost"/>.</summary>
    public Godot.Collections.Dictionary EstimateCurveCost(float x0, float z0, float x1, float z1, int type, float sweep)
    {
        var cost = Sim.Network.EstimateCost(x0, z0, x1, z1, RoadTypeOf(type), sweep);
        if (cost is not { } c) return new Godot.Collections.Dictionary { ["valid"] = false };
        return new Godot.Collections.Dictionary { ["valid"] = true, ["length"] = c.Length, ["cut"] = c.CutVolume, ["fill"] = c.FillVolume, ["money"] = c.Money };
    }

    /// <summary>Points along a road bending by <paramref name="sweep"/> radians, lying on the ground (for the build preview).</summary>
    public Vector3[] CurvePoints(float x0, float z0, float x1, float z1, float sweep)
    {
        var g = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        int n = Math.Max(2, (int)MathF.Ceiling(g.Length / 4f));
        var points = new Vector3[n + 1];
        for (int i = 0; i <= n; i++)
        {
            var (x, z) = g.PointAt(g.Length * i / n);
            points[i] = new Vector3(x, _terrain.HeightAt(x, z), z);
        }
        return points;
    }

    static RoadType RoadTypeOf(int type) => type == 1 ? RoadType.Avenue2x2 : RoadType.TwoLane;

    /// <summary>Builds a straight road. Type 0 is 2-lane, 1 is the 2x2 avenue.</summary>
    public bool BuildRoad(float x0, float z0, float x1, float z1, int type) => BuildCurve(x0, z0, x1, z1, type, 0f);

    /// <summary>
    /// Builds a road bending by <paramref name="sweep"/> radians. It joins existing nodes at its ends and makes a junction
    /// wherever it ends on or crosses another road.
    /// </summary>
    public bool BuildCurve(float x0, float z0, float x1, float z1, int type, float sweep)
    {
        int built = Sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadTypeOf(type), Sim.Network.Tuning.Roads.Of(RoadTypeOf(type)).SpeedLimit, sweep));
        if (built == 0) return false;
        RebuildRoads();
        return true;
    }

    /// <summary>The road piece nearest to a point within 14 m: {found, road, type, length}.</summary>
    public Godot.Collections.Dictionary RoadAt(float x, float z)
    {
        if (Sim.Network.RoadAt(x, z, 14f) is not { } id) return new Godot.Collections.Dictionary { ["found"] = false };
        var road = Sim.Network.GetRoad(id);
        return new Godot.Collections.Dictionary { ["found"] = true, ["road"] = id.Value, ["type"] = road.Type == RoadType.Avenue2x2 ? 1 : 0, ["length"] = road.Length };
    }

    /// <summary>Points along a road's centre line at road height (for highlighting it).</summary>
    public Vector3[] RoadPoints(int road)
    {
        var id = new RoadId(road);
        var g = Sim.Network.GetRoad(id).Geometry;
        int n = Math.Max(2, (int)MathF.Ceiling(g.Length / 4f));
        var points = new Vector3[n + 1];
        for (int i = 0; i <= n; i++)
        {
            var (x, z) = g.PointAt(g.Length * i / n);
            points[i] = new Vector3(x, Sim.Network.RoadHeightAt(id, g.Length * i / n), z);
        }
        return points;
    }

    /// <summary>What tearing a road down costs.</summary>
    public float DemolitionCost(int road) => Sim.Network.DemolitionCost(new RoadId(road));

    /// <summary>Tears a road piece down; vehicles on it disappear.</summary>
    public bool RemoveRoad(int road)
    {
        if (Sim.Execute(new RemoveRoadCommand(road)) == 0) return false;
        RebuildRoads();
        return true;
    }

    /// <summary>What widening a two-lane road to an avenue costs: {valid, cut, fill, money}.</summary>
    public Godot.Collections.Dictionary EstimateUpgrade(int road)
    {
        if (Sim.Network.EstimateUpgrade(new RoadId(road)) is not { } c) return new Godot.Collections.Dictionary { ["valid"] = false };
        return new Godot.Collections.Dictionary { ["valid"] = true, ["cut"] = c.CutVolume, ["fill"] = c.FillVolume, ["money"] = c.Money };
    }

    /// <summary>Widens a two-lane road to an avenue.</summary>
    public bool UpgradeRoad(int road)
    {
        if (Sim.Execute(new UpgradeRoadCommand(road, Sim.Network.Tuning.Roads.Avenue.SpeedLimit)) == 0) return false;
        RebuildRoads();
        return true;
    }

    /// <summary>Places a depot at the node nearest the point (within 60 m) that has none yet.</summary>
    public bool BuildDepot(float x, float z, int level)
    {
        int node = Sim.Network.FindNodeNear(x, z, 60f);
        if (node < 0) return false;
        if (Sim.Execute(new AddDepotCommand(node, (DepotLevel)Math.Clamp(level, 0, 2))) < 0) return false;
        RebuildRoads();
        return true;
    }

    /// <summary>Adds up to <paramref name="count"/> ambient vehicles; returns how many fit.</summary>
    public int SpawnTraffic(int count) => Sim.Execute(new SeedTrafficCommand(count));

    /// <summary>Breaks down a moving car, picked deterministically from the current state. Returns its id or -1.</summary>
    public int CrashSomeone() => Sim.Execute(new CrashSomeoneCommand());

    /// <summary>World position of the oldest open incident, or the origin when there is none.</summary>
    public Vector3 FirstOpenIncidentPosition()
    {
        if (Sim.OpenIncidentIds.Count == 0) return Vector3.Zero;
        var incident = Sim.Incidents[Sim.OpenIncidentIds[0]];
        int lane = incident.Lane.Value;
        LaneFrame(lane, incident.Position, out float x, out float z, out _, out _);
        return new Vector3(x, _laneY[lane] + _laneGrade[lane] * incident.Position, z);
    }

    public string FirstOpenIncidentState() => Sim.OpenIncidentIds.Count == 0 ? "none" : Sim.Incidents[Sim.OpenIncidentIds[0]].State.ToString();

    public int DepotCount() => Sim.Depots.Count;

    /// <summary>Roads currently in use (a road split by a junction counts as its two halves).</summary>
    public int RoadCount()
    {
        int n = 0;
        for (int r = 0; r < Sim.Network.RoadCount; r++) if (!Sim.Network.GetRoad(new RoadId(r)).Retired) n++;
        return n;
    }

    /// <summary>Game speed multiplier: 0 pauses, then 1, 2, 4 or 8.</summary>
    public void SetSpeed(float speed) => _speed = Mathf.Max(0f, speed);

    public float GetSpeed() => _speed;

    public Godot.Collections.Dictionary GetStats()
    {
        int cars = 0, wrecks = 0, overtaking = 0;
        double speed = 0;
        for (int i = 0; i < Sim.VehicleCount; i++)
        {
            var v = Sim.GetVehicle(i);
            if (v.Kind == VehicleKind.Car) { cars++; speed += v.Speed; if (v.Overtake != OvertakePhase.None) overtaking++; }
            else if (v.Kind != VehicleKind.Removed) wrecks++;
        }
        int open = 0;
        foreach (var incident in Sim.Incidents) if (incident.State != IncidentState.Cleared) open++;
        return new Godot.Collections.Dictionary
        {
            ["vehicles"] = cars,
            ["wrecks"] = wrecks,
            ["overtaking"] = overtaking,
            ["mean_speed"] = cars > 0 ? speed / cars : 0.0,
            ["incidents"] = Sim.Incidents.Count,
            ["open_incidents"] = open,
            ["step_ms"] = _stepMsAverage,
            ["effective_speed"] = _effectiveSpeed,
            ["tick"] = Sim.Tick,
            ["hash"] = Sim.StateHash().ToString("X16"),
        };
    }

    // ---- Stepping -------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (Sim == null) return;

        double before = Sim.Tick;
        if (_speed > 0f)
        {
            _accumulator += delta * _speed;
            // If the CPU cannot keep up, effective speed degrades rather than the frame rate collapsing (ADR 0002).
            double budgetMs = _speed > 1f ? 70.0 : 10.0;
            _clock.Restart();
            int steps = 0;
            while (_accumulator >= Simulation.StepSeconds && _clock.Elapsed.TotalMilliseconds < budgetMs)
            {
                Sim.Step();
                _accumulator -= Simulation.StepSeconds;
                steps++;
            }
            if (_accumulator >= Simulation.StepSeconds) _accumulator = Simulation.StepSeconds * 0.5; // drop the backlog
            if (steps > 0) _stepMsAverage = Mathf.Lerp((float)_stepMsAverage, (float)(_clock.Elapsed.TotalMilliseconds / steps), 0.1f);
        }

        _simSecondsThisSecond += (Sim.Tick - before) * Simulation.StepSeconds;
        _realSecondsThisSecond += delta;
        if (_realSecondsThisSecond >= 1.0)
        {
            _effectiveSpeed = _simSecondsThisSecond / _realSecondsThisSecond;
            _simSecondsThisSecond = _realSecondsThisSecond = 0;
        }

        float fraction = _speed > 0f ? (float)(_accumulator / Simulation.StepSeconds) : 0f;
        DrawVehicles(fraction);
        DrawCrews(fraction);
    }

    // ---- Drawing --------------------------------------------------------------------------------------------

    /// <summary>Centre-line point of a lane's road at a distance along it, and the unit direction of travel there.</summary>
    void LaneFrame(int lane, float s, out float x, out float z, out float dx, out float dz)
    {
        float sweep = _laneSweep[lane];
        if (sweep == 0f)
        {
            dx = _laneDx[lane]; dz = _laneDz[lane];
            x = _laneX[lane] + dx * s; z = _laneZ[lane] + dz * s;
            return;
        }
        float h = _laneH0[lane] + sweep * (s / _laneLength[lane]);
        float k = _laneLength[lane] / sweep;
        dx = MathF.Cos(h); dz = MathF.Sin(h);
        x = _laneX[lane] + k * (MathF.Sin(h) - _laneSinH0[lane]);
        z = _laneZ[lane] - k * (MathF.Cos(h) - _laneCosH0[lane]);
    }

    void DrawVehicles(float stepFraction)
    {
        int count = Sim.VehicleCount;
        if (count > _capacity)
        {
            // Doubling keeps the bulk upload small in the common case while allowing wrecks and debris on top.
            while (_capacity < count) _capacity *= 2;
            _buffer = new float[_capacity * FloatsPerInstance];
            _vehicles.InstanceCount = _capacity;
        }
        var buf = _buffer;
        for (int i = 0; i < count; i++)
        {
            int o = i * FloatsPerInstance;
            var v = Sim.GetVehicle(i);
            if (v.Kind == VehicleKind.Removed)
            {
                Array.Clear(buf, o, FloatsPerInstance);
                continue;
            }

            int lane = v.Lane.Value;
            // Look a fraction of a step ahead so motion is smooth between the 20 Hz simulation steps.
            float pos = Math.Min(v.Position + v.Speed * Simulation.StepSeconds * stepFraction, _laneLength[lane]) - Simulation.VehicleLength * 0.5f;
            LaneFrame(lane, pos, out float cx, out float cz, out float dx, out float dz);
            float side = _laneSide[lane] - Simulation.LaneWidth * v.Lateral + v.Shift;
            float x = cx - dz * side;
            float z = cz + dx * side;

            float yaw = 0f;
            if (v.Kind != VehicleKind.Car) yaw = ((i * 2654435761u) % 1000) / 1000f * 0.8f - 0.4f;
            float cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
            float fx = dx * cos - dz * sin, fz = dx * sin + dz * cos; // forward, turned by the wreck's skew

            // Pitched to follow the grade of the lane.
            float grade = _laneGrade[lane], tilt = 1f / MathF.Sqrt(1f + grade * grade), rise = grade * tilt;
            float y = _laneY[lane] + grade * pos + 0.8f;
            buf[o + 0] = fx * tilt;  buf[o + 1] = -fx * rise; buf[o + 2] = -fz; buf[o + 3] = x;
            buf[o + 4] = rise;       buf[o + 5] = tilt;       buf[o + 6] = 0f;  buf[o + 7] = y;
            buf[o + 8] = fz * tilt;  buf[o + 9] = -fz * rise; buf[o + 10] = fx; buf[o + 11] = z;

            Color c = v.Kind switch
            {
                VehicleKind.Wreck => new Color(0.8f, 0.1f, 0.08f),
                VehicleKind.Debris => new Color(0.9f, 0.55f, 0.1f),
                _ => v.Overtake != OvertakePhase.None ? new Color(1f, 0.9f, 0.2f) : CarColor(i),
            };
            buf[o + 12] = c.R; buf[o + 13] = c.G; buf[o + 14] = c.B; buf[o + 15] = 1f;
        }
        _vehicles.Buffer = buf;
        _vehicles.VisibleInstanceCount = count;
    }

    /// <summary>Rescue crews as blue boxes: driving from their depot to the site, then parked beside the wreck while they work.</summary>
    void DrawCrews(float stepFraction)
    {
        var buf = _crewBuffer;
        int n = 0;
        var net = Sim.Network;
        foreach (int id in Sim.OpenIncidentIds)
        {
            if (n >= CrewCapacity) break;
            var incident = Sim.Incidents[id];
            if (incident.State is not (IncidentState.CrewEnRoute or IncidentState.Clearing)) continue;

            int lane = incident.Lane.Value;
            LaneFrame(lane, incident.Position, out float lx, out float lz, out float dx, out float dz);
            float sx = lx + dz * 5f;
            float sz = lz - dx * 5f;

            float x = sx, z = sz;
            if (incident.State == IncidentState.CrewEnRoute && incident.Depot >= 0)
            {
                var (fx, fz) = net.GetNode(Sim.Depots[incident.Depot].Node);
                float span = Math.Max(1f, incident.CrewArrivalTick - incident.DispatchTick);
                float t = Math.Clamp((Sim.Tick + stepFraction - incident.DispatchTick) / span, 0f, 1f);
                x = fx + (sx - fx) * t;
                z = fz + (sz - fz) * t;
                float len = MathF.Sqrt((sx - fx) * (sx - fx) + (sz - fz) * (sz - fz));
                if (len > 1f) { dx = (sx - fx) / len; dz = (sz - fz) / len; }
            }
            else if (incident.State == IncidentState.CrewEnRoute)
            {
                continue; // the distant service is not on the map yet
            }

            int o = n * FloatsPerInstance;
            buf[o + 0] = dx;  buf[o + 1] = 0f; buf[o + 2] = -dz; buf[o + 3] = x;
            buf[o + 4] = 0f;  buf[o + 5] = 1f; buf[o + 6] = 0f;  buf[o + 7] = _terrain.HeightAt(x, z) + 1.1f;
            buf[o + 8] = dz;  buf[o + 9] = 0f; buf[o + 10] = dx; buf[o + 11] = z;
            bool working = incident.State == IncidentState.Clearing;
            buf[o + 12] = working ? 1f : 0.1f; buf[o + 13] = working ? 0.85f : 0.4f; buf[o + 14] = working ? 0.1f : 1f; buf[o + 15] = 1f;
            n++;
        }
        _crews.Buffer = buf;
        _crews.VisibleInstanceCount = n;
    }

    static Color CarColor(int id)
    {
        float hue = ((id * 2654435761u) % 997) / 997f;
        return Color.FromHsv(hue, 0.45f, 0.85f);
    }

    /// <summary>Rebuilds road meshes and the per-lane drawing tables from the simulation's network.</summary>
    public void RebuildRoads()
    {
        foreach (var child in _roadRoot.GetChildren()) child.QueueFree();
        var net = Sim.Network;
        _terrain = net.Terrain!;
        RebuildTerrain();

        int lanes = net.LaneCount;
        _laneX = new float[lanes]; _laneZ = new float[lanes];
        _laneDx = new float[lanes]; _laneDz = new float[lanes];
        _laneSide = new float[lanes]; _laneLength = new float[lanes];
        _laneSweep = new float[lanes]; _laneH0 = new float[lanes]; _laneSinH0 = new float[lanes]; _laneCosH0 = new float[lanes];
        _laneY = new float[lanes]; _laneGrade = new float[lanes];

        for (int r = 0; r < net.RoadCount; r++)
        {
            var road = net.GetRoad(new RoadId(r));
            var g = road.Geometry;
            float y0 = net.GetNodeHeight(road.From), y1 = net.GetNodeHeight(road.To);
            bool avenue = road.Type == RoadType.Avenue2x2;
            float median = avenue ? RoadNetwork.MedianWidth : 0f;
            float width = (avenue ? 4 : 2) * Simulation.LaneWidth + median;

            // Tables for every road: retired lanes may still be referred to by finished incidents.
            SetLaneTable(net, road.ForwardLanes, y0);
            SetLaneTable(net, road.BackwardLanes, y1);
            if (road.Retired) continue;

            if (g.IsStraight)
            {
                var (x0, z0) = net.GetNode(road.From);
                var (x1, z1) = net.GetNode(road.To);
                float dx = (x1 - x0) / g.Length, dz = (z1 - z0) / g.Length;
                var middle = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
                AddRoadBox(middle, g.Length, y1 - y0, dx, dz, width, RoadHeight, 0f, _roadMaterial);
                AddRoadBox(middle, g.Length, y1 - y0, dx, dz, avenue ? median : 0.25f, 0.04f, RoadHeight * 0.5f + 0.01f, avenue ? _medianMaterial : _lineMaterial);
            }
            else
            {
                AddRoadRibbon(g, y0, y1, width, RoadHeight * 0.5f, _roadMaterial);
                AddRoadRibbon(g, y0, y1, avenue ? median : 0.25f, RoadHeight * 0.5f + 0.05f, avenue ? _medianMaterial : _lineMaterial);
            }
        }

        for (int n = 0; n < net.NodeCount; n++)
        {
            if (net.IncomingLanes(n).Count == 0) continue; // a node whose roads were all replaced
            var (x, z) = net.GetNode(n);
            var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 8.5f, BottomRadius = 8.5f, Height = RoadHeight, RadialSegments = 24 }, MaterialOverride = _roadMaterial };
            disc.Position = new Vector3(x, net.GetNodeHeight(n), z);
            _roadRoot.AddChild(disc);
        }

        foreach (var depot in Sim.Depots)
        {
            var (x, z) = net.GetNode(depot.Node);
            var building = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(22f, 6f, 22f) }, MaterialOverride = _depotMaterial };
            building.Position = new Vector3(x + 26f, _terrain.HeightAt(x + 26f, z + 26f) + 3f, z + 26f);
            _roadRoot.AddChild(building);
        }
    }

    void SetLaneTable(RoadNetwork net, LaneId[] lanes, float startY)
    {
        foreach (var id in lanes)
        {
            var lane = net.GetLane(id);
            var path = lane.Path;
            int i = id.Value;
            _laneX[i] = path.X; _laneZ[i] = path.Z;
            _laneH0[i] = path.Heading0;
            _laneSinH0[i] = MathF.Sin(path.Heading0); _laneCosH0[i] = MathF.Cos(path.Heading0);
            _laneDx[i] = _laneCosH0[i]; _laneDz[i] = _laneSinH0[i];
            _laneSweep[i] = path.IsStraight ? 0f : path.Sweep;
            _laneLength[i] = path.Length;
            _laneY[i] = startY;
            _laneGrade[i] = lane.Grade;
            _laneSide[i] = lane.SideOffset;
        }
    }

    /// <summary>A flat strip following a curved road, <paramref name="lift"/> above its centre line.</summary>
    void AddRoadRibbon(RoadGeometry g, float y0, float y1, float width, float lift, Material material)
    {
        int segments = Math.Max(2, (int)MathF.Ceiling(g.Length / 4f));
        var vertices = new Vector3[(segments + 1) * 2];
        var normals = new Vector3[vertices.Length];
        var indices = new int[segments * 6];
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            var (x, z) = g.PointAt(g.Length * t);
            float h = g.HeadingAt(g.Length * t);
            float rx = -MathF.Sin(h) * width * 0.5f, rz = MathF.Cos(h) * width * 0.5f;
            float y = y0 + (y1 - y0) * t + lift;
            vertices[i * 2] = new Vector3(x - rx, y, z - rz);     // left edge
            vertices[i * 2 + 1] = new Vector3(x + rx, y, z + rz); // right edge
            normals[i * 2] = normals[i * 2 + 1] = Vector3.Up;
        }
        for (int i = 0; i < segments; i++)
        {
            int l = i * 2, r = l + 1, nl = l + 2, nr = l + 3;
            int o = i * 6;
            indices[o] = l; indices[o + 1] = nl; indices[o + 2] = r;
            indices[o + 3] = nl; indices[o + 4] = nr; indices[o + 5] = r;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _roadRoot.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = material });
    }

    /// <summary>A flat slab lying along a road that climbs <paramref name="rise"/> over <paramref name="run"/> metres.</summary>
    void AddRoadBox(Vector3 centre, float run, float rise, float dx, float dz, float width, float height, float lift, Material material)
    {
        float length = MathF.Sqrt(run * run + rise * rise);
        float c = run / length, s = rise / length;
        var along = new Vector3(dx * c, s, dz * c);
        var across = new Vector3(-dz, 0f, dx);
        var up = across.Cross(along);
        var mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(length, height, width) }, MaterialOverride = material };
        mesh.Transform = new Transform3D(new Basis(along, up, across), centre + up * lift);
        _roadRoot.AddChild(mesh);
    }

    /// <summary>Rebuilds the ground mesh when the terrain changed (a road was carved into it).</summary>
    void RebuildTerrain()
    {
        if (_terrainVersion == _terrain.Version && _terrainMesh.Mesh != null && _terrainSource == _terrain) return;
        _terrainVersion = _terrain.Version;
        _terrainSource = _terrain;

        int n = _terrain.Samples;
        var vertices = new Vector3[n * n];
        var normals = new Vector3[n * n];
        var colors = new Color[n * n];
        float cell = _terrain.CellSize, half = _terrain.HalfSize;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float h = _terrain.GetSample(i, j);
                int k = j * n + i;
                vertices[k] = new Vector3(i * cell - half, h, j * cell - half);
                float gx = _terrain.GetSample(i + 1, j) - _terrain.GetSample(i - 1, j);
                float gz = _terrain.GetSample(i, j + 1) - _terrain.GetSample(i, j - 1);
                normals[k] = new Vector3(-gx, 2f * cell, -gz).Normalized();
                colors[k] = TerrainColor(h, normals[k].Y).SrgbToLinear(); // vertex colours are read as linear
            }
        var indices = new int[_terrain.Cells * _terrain.Cells * 6];
        int t = 0;
        for (int j = 0; j < _terrain.Cells; j++)
            for (int i = 0; i < _terrain.Cells; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                indices[t++] = a; indices[t++] = b; indices[t++] = c;
                indices[t++] = b; indices[t++] = d; indices[t++] = c;
            }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _terrainMesh.Mesh = mesh;
    }

    static Color TerrainColor(float height, float normalY)
    {
        var grass = new Color(0.42f, 0.58f, 0.34f);
        var upland = new Color(0.5f, 0.55f, 0.32f);
        var rock = new Color(0.52f, 0.48f, 0.43f);
        var snow = new Color(0.9f, 0.92f, 0.95f);
        Color c = height < 40f ? grass.Lerp(upland, height / 40f) : upland.Lerp(rock, Mathf.Clamp((height - 40f) / 40f, 0f, 1f));
        if (height > 85f) c = c.Lerp(snow, Mathf.Clamp((height - 85f) / 25f, 0f, 1f));
        return c.Lerp(rock, Mathf.Clamp((0.9f - normalY) * 4f, 0f, 0.8f)); // steep ground shows rock
    }
}
