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
    const int FloatsPerInstance = 16; // 3x4 transform + RGBA colour
    const float RoadHeight = 0.1f;
    const float MedianWidth = 2f;

    public Simulation Sim { get; private set; } = null!;

    float _speed = 1f;
    double _accumulator;
    readonly Stopwatch _clock = new();
    double _stepMsAverage;
    double _effectiveSpeed = 1;
    double _simSecondsThisSecond, _realSecondsThisSecond;

    MultiMesh _vehicles = null!;
    float[] _buffer = new float[InitialCapacity * FloatsPerInstance];
    int _capacity = InitialCapacity;
    Node3D _roadRoot = null!;
    StandardMaterial3D _roadMaterial = null!, _medianMaterial = null!, _depotMaterial = null!, _lineMaterial = null!;

    // Per-lane drawing data: start point, unit direction and sideways offset of the lane centre (to the driver's right).
    float[] _laneX = Array.Empty<float>(), _laneZ = Array.Empty<float>();
    float[] _laneDx = Array.Empty<float>(), _laneDz = Array.Empty<float>();
    float[] _laneSide = Array.Empty<float>(), _laneLength = Array.Empty<float>();

    public override void _Ready()
    {
        _roadRoot = new Node3D { Name = "Roads" };
        AddChild(_roadRoot);
        _roadMaterial = Flat(new Color(0.23f, 0.24f, 0.26f));
        _medianMaterial = Flat(new Color(0.35f, 0.5f, 0.3f));
        _depotMaterial = Flat(new Color(0.85f, 0.25f, 0.2f));
        _lineMaterial = Flat(new Color(0.8f, 0.78f, 0.6f));

        var ground = new MeshInstance3D { Name = "Ground", Mesh = new PlaneMesh { Size = new Vector2(20000, 20000) } };
        ground.MaterialOverride = Flat(new Color(0.42f, 0.58f, 0.34f));
        ground.Position = new Vector3(0, -0.05f, 0);
        AddChild(ground);

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
        AddChild(new MultiMeshInstance3D { Name = "Vehicles", Multimesh = _vehicles, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    static StandardMaterial3D Flat(Color color) => new() { AlbedoColor = color, Roughness = 1f };

    // ---- API for GDScript -----------------------------------------------------------------------------------

    /// <summary>Starts a fresh demo world with the given seed and number of ambient vehicles.</summary>
    public void NewDemoWorld(long seed, int vehicles)
    {
        var net = new RoadNetwork();
        DemoWorld.Build(net);
        Sim = new Simulation(net, (ulong)seed);
        Sim.AddDepot(net.NodeCount / 4, DepotLevel.Workshop);
        Sim.AddDepot(net.NodeCount * 3 / 4, DepotLevel.Simple);
        Sim.SeedTraffic(vehicles);
        RebuildRoads();
        _accumulator = 0;
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

        DrawVehicles(_speed > 0f ? (float)(_accumulator / Simulation.StepSeconds) : 0f);
    }

    // ---- Drawing --------------------------------------------------------------------------------------------

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
            float dx = _laneDx[lane], dz = _laneDz[lane];
            float side = _laneSide[lane] - Simulation.LaneWidth * v.Lateral;
            float x = _laneX[lane] + dx * pos - dz * side;
            float z = _laneZ[lane] + dz * pos + dx * side;

            float yaw = 0f;
            if (v.Kind != VehicleKind.Car) yaw = ((i * 2654435761u) % 1000) / 1000f * 0.8f - 0.4f;
            float cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
            float fx = dx * cos - dz * sin, fz = dx * sin + dz * cos; // forward, turned by the wreck's skew

            buf[o + 0] = fx;  buf[o + 1] = 0f; buf[o + 2] = -fz; buf[o + 3] = x;
            buf[o + 4] = 0f;  buf[o + 5] = 1f; buf[o + 6] = 0f;  buf[o + 7] = 0.8f;
            buf[o + 8] = fz;  buf[o + 9] = 0f; buf[o + 10] = fx; buf[o + 11] = z;

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

        int lanes = net.LaneCount;
        _laneX = new float[lanes]; _laneZ = new float[lanes];
        _laneDx = new float[lanes]; _laneDz = new float[lanes];
        _laneSide = new float[lanes]; _laneLength = new float[lanes];

        for (int r = 0; r < net.RoadCount; r++)
        {
            var road = net.GetRoad(new RoadId(r));
            var (x0, z0) = net.GetNode(road.From);
            var (x1, z1) = net.GetNode(road.To);
            float length = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            float dx = (x1 - x0) / length, dz = (z1 - z0) / length;
            bool avenue = road.Type == RoadType.Avenue2x2;
            float median = avenue ? MedianWidth : 0f;
            float width = (avenue ? 4 : 2) * Simulation.LaneWidth + median;

            AddRoadBox(new Vector3((x0 + x1) * 0.5f, 0f, (z0 + z1) * 0.5f), length, width, RoadHeight, MathF.Atan2(-dz, dx), _roadMaterial);
            AddRoadBox(new Vector3((x0 + x1) * 0.5f, RoadHeight * 0.5f + 0.01f, (z0 + z1) * 0.5f), length, avenue ? median : 0.25f, 0.04f, MathF.Atan2(-dz, dx), avenue ? _medianMaterial : _lineMaterial);

            SetLaneTable(net, road.ForwardLanes, x0, z0, dx, dz, length, median);
            SetLaneTable(net, road.BackwardLanes, x1, z1, -dx, -dz, length, median);
        }

        for (int n = 0; n < net.NodeCount; n++)
        {
            var (x, z) = net.GetNode(n);
            var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 8.5f, BottomRadius = 8.5f, Height = RoadHeight, RadialSegments = 24 }, MaterialOverride = _roadMaterial };
            disc.Position = new Vector3(x, 0f, z);
            _roadRoot.AddChild(disc);
        }

        foreach (var depot in Sim.Depots)
        {
            var (x, z) = net.GetNode(depot.Node);
            var building = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(22f, 6f, 22f) }, MaterialOverride = _depotMaterial };
            building.Position = new Vector3(x + 26f, 3f, z + 26f);
            _roadRoot.AddChild(building);
        }
    }

    void SetLaneTable(RoadNetwork net, LaneId[] lanes, float startX, float startZ, float dx, float dz, float length, float median)
    {
        foreach (var id in lanes)
        {
            var lane = net.GetLane(id);
            int i = id.Value;
            _laneX[i] = startX; _laneZ[i] = startZ;
            _laneDx[i] = dx; _laneDz[i] = dz;
            _laneLength[i] = length;
            _laneSide[i] = median * 0.5f + (lane.Index + 0.5f) * Simulation.LaneWidth; // to the driver's right of the centre line
        }
    }

    void AddRoadBox(Vector3 centre, float length, float width, float height, float yaw, Material material)
    {
        var mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(length, height, width) }, MaterialOverride = material };
        mesh.Position = centre;
        mesh.Rotation = new Vector3(0f, yaw, 0f);
        _roadRoot.AddChild(mesh);
    }
}
