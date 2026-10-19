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
    MultiMesh _buildings = null!;
    Node3D _townLabels = null!;
    int _buildingsVersion = -1;
    readonly float[] _crewBuffer = new float[CrewCapacity * FloatsPerInstance];
    float[] _buffer = new float[InitialCapacity * FloatsPerInstance];
    int _capacity = InitialCapacity;
    Node3D _roadRoot = null!;
    Node3D _terrainRoot = null!;
    StandardMaterial3D _terrainMaterial = null!;
    MeshInstance3D?[] _terrainChunks = Array.Empty<MeshInstance3D?>();
    int _terrainVersion = -1;
    Terrain _terrain = null!, _terrainSource = null!;
    MeshInstance3D _water = null!;
    StandardMaterial3D _waterMaterial = null!, _deckMaterial = null!, _tunnelMaterial = null!, _tunnelHintMaterial = null!;
    StandardMaterial3D _roadMaterial = null!, _medianMaterial = null!, _depotMaterial = null!, _lineMaterial = null!, _stationMaterial = null!;

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
        _stationMaterial = Flat(new Color(0.2f, 0.45f, 0.85f));
        _lineMaterial = Flat(new Color(0.8f, 0.78f, 0.6f));
        _deckMaterial = Flat(new Color(0.55f, 0.55f, 0.58f));
        _tunnelMaterial = Flat(new Color(0.32f, 0.3f, 0.3f));
        _tunnelHintMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.1f, 0.1f, 0.12f, 0.35f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };

        // Beyond the edge of the terrain the world is just flat grass.
        var ground = new MeshInstance3D { Name = "Ground", Mesh = new PlaneMesh { Size = new Vector2(20000, 20000) } };
        ground.MaterialOverride = Flat(new Color(0.42f, 0.58f, 0.34f));
        ground.Position = new Vector3(0, -8f, 0); // below the deepest lake bed
        AddChild(ground);
        _waterMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.16f, 0.4f, 0.62f, 0.72f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.15f,
            Metallic = 0.2f,
        };
        _water = new MeshInstance3D { Name = "Water", MaterialOverride = _waterMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_water);
        _terrainMaterial = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f };
        _terrainRoot = new Node3D { Name = "Terrain", Position = new Vector3(0, -TerrainSink, 0) };
        AddChild(_terrainRoot);

        // A unit-length box: every instance is stretched to its vehicle's length.
        var box = new BoxMesh { Size = new Vector3(1f, 1.5f, Simulation.VehicleWidth) };
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
        _buildings = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new BoxMesh { Size = Vector3.One },
            InstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Name = "Buildings", Multimesh = _buildings, MaterialOverride = material });
        _townLabels = new Node3D { Name = "TownLabels" };
        AddChild(_townLabels);
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

    /// <summary>
    /// Starts a map of generated hills with towns and industries (<paramref name="industries"/>: 0 few, 1 normal, 2 many).
    /// Returns how many towns were placed; fewer than <paramref name="towns"/> when the land ran out. A few ambient cars
    /// are put on the streets.
    /// </summary>
    public int NewTownsWorld(long seed, int towns, int industries)
    {
        var net = new RoadNetwork(Terrain.Generate((ulong)seed));
        var plan = IndustryGenerator.Generate(net, (ulong)seed, TownGenerator.Generate(net, (ulong)seed, towns), (IndustryAmount)Math.Clamp(industries, 0, 2));
        Sim = new Simulation(net, (ulong)seed);
        Sim.AddTowns(plan);
        Sim.Execute(new SeedTrafficCommand(TownTrafficPerTown * Math.Max(1, plan.Towns.Count)));
        RebuildRoads();
        _accumulator = 0;
        return plan.Towns.Count;
    }

    const int TownTrafficPerTown = 30; // ambient cars put on a new map per town, until trip groups drive the roads (M3c)

    /// <summary>Height of what is seen at a point: the ground, or the water surface over it.</summary>
    public float HeightAt(float x, float z) => Math.Max(_terrain.HeightAt(x, z), _terrain.WaterSurfaceAt(x, z));

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
            if (p.Y <= HeightAt(p.X, p.Z))
            {
                float lo = previous, hi = t;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    var q = origin + direction * mid;
                    if (q.Y <= HeightAt(q.X, q.Z)) hi = mid; else lo = mid;
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
        int node = net.FindNodeNear(x, z, radius, withRoadsOnly: true);
        if (node >= 0)
        {
            var (nx, nz) = net.GetNode(node);
            return new Godot.Collections.Dictionary { ["snapped"] = true, ["x"] = nx, ["y"] = net.GetNodeHeight(node), ["z"] = nz };
        }

        if (net.NearestOnRoad(x, z, RoadSnapRadius, 5f) is { } hit)
            return new Godot.Collections.Dictionary { ["snapped"] = true, ["x"] = hit.X, ["y"] = hit.Y, ["z"] = hit.Z };
        return new Godot.Collections.Dictionary { ["snapped"] = false, ["x"] = x, ["y"] = HeightAt(x, z), ["z"] = z };
    }

    /// <summary>Whether a straight road can be built between the points: long enough, not too steep, not crossing too close to a junction.</summary>
    public bool CanBuildRoad(float x0, float z0, float x1, float z1) => Sim.Network.CanBuildRoad(x0, z0, x1, z1);

    /// <summary>The sweep (total turn in radians, signed) of the arc from the first point through the second to the third.</summary>
    public float SweepThrough(float x0, float z0, float xm, float zm, float x1, float z1) =>
        RoadGeometry.SweepThrough(x0, z0, xm, zm, x1, z1);

    /// <summary>What a straight road would cost: {valid, length, cut, fill, money, bridge, tunnel} (the last two are metres of structure); only {valid} when it cannot be built.</summary>
    public Godot.Collections.Dictionary EstimateRoadCost(float x0, float z0, float x1, float z1, int type) =>
        EstimateCurveCost(x0, z0, x1, z1, type, 0f);

    /// <summary>What a road bending by <paramref name="sweep"/> radians would cost; see <see cref="EstimateRoadCost"/>.</summary>
    public Godot.Collections.Dictionary EstimateCurveCost(float x0, float z0, float x1, float z1, int type, float sweep)
    {
        var cost = Sim.Network.EstimateCost(x0, z0, x1, z1, RoadTypeOf(type), sweep);
        if (cost is not { } c) return new Godot.Collections.Dictionary { ["valid"] = false };
        var demolition = Sim.EstimateRoadDemolition(x0, z0, x1, z1, RoadTypeOf(type), sweep);
        return new Godot.Collections.Dictionary
        {
            ["valid"] = true, ["length"] = c.Length, ["cut"] = c.CutVolume, ["fill"] = c.FillVolume, ["money"] = c.Money,
            ["bridge"] = c.BridgeLength, ["tunnel"] = c.TunnelLength,
            ["demolished"] = demolition.Count, ["demolition_price"] = demolition.Price,
            ["affordable"] = Sim.CanAfford(c.Money + demolition.Price),
        };
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
            points[i] = new Vector3(x, HeightAt(x, z), z);
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

    /// <summary>The road piece nearest to a point within 14 m: {found, road, type, length, structure} (structure: 0 ground, 1 bridge, 2 tunnel).</summary>
    public Godot.Collections.Dictionary RoadAt(float x, float z)
    {
        if (Sim.Network.RoadAt(x, z, 14f) is not { } id) return new Godot.Collections.Dictionary { ["found"] = false };
        var road = Sim.Network.GetRoad(id);
        return new Godot.Collections.Dictionary { ["found"] = true, ["road"] = id.Value, ["type"] = road.Type == RoadType.Avenue2x2 ? 1 : 0, ["length"] = road.Length, ["structure"] = (int)road.Structure };
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

    /// <summary>What narrowing an avenue to a two-lane road costs: {valid, money}.</summary>
    public Godot.Collections.Dictionary EstimateDowngrade(int road)
    {
        if (Sim.Network.DowngradeCost(new RoadId(road)) is not { } money) return new Godot.Collections.Dictionary { ["valid"] = false };
        return new Godot.Collections.Dictionary { ["valid"] = true, ["money"] = money };
    }

    /// <summary>Narrows an avenue to a two-lane road.</summary>
    public bool DowngradeRoad(int road)
    {
        if (Sim.Execute(new DowngradeRoadCommand(road, Sim.Network.Tuning.Roads.TwoLane.SpeedLimit)) == 0) return false;
        RebuildRoads();
        return true;
    }

    const float MinSpeedLimit = 5f, MaxSpeedLimit = 33f; // m/s, what the speed tools allow (18 to 120 km/h)

    /// <summary>A road's posted limit in m/s.</summary>
    public float SpeedLimitOf(int road) => Sim.Network.GetRoad(new RoadId(road)).SpeedLimit;

    /// <summary>The limit a road would get from <see cref="ChangeSpeedLimit"/>, in m/s.</summary>
    public float SteppedSpeedLimit(int road, float delta) => Math.Clamp(SpeedLimitOf(road) + delta, MinSpeedLimit, MaxSpeedLimit);

    /// <summary>What re-signing a road costs: {valid, money, limit}; invalid when the limit would not change.</summary>
    public Godot.Collections.Dictionary EstimateSpeedLimit(int road, float delta)
    {
        var id = new RoadId(road);
        float limit = SteppedSpeedLimit(road, delta);
        if (limit == SpeedLimitOf(road) || Sim.Network.SpeedLimitChangeCost(id) is not { } money)
            return new Godot.Collections.Dictionary { ["valid"] = false };
        return new Godot.Collections.Dictionary { ["valid"] = true, ["money"] = money, ["limit"] = limit };
    }

    /// <summary>Raises or lowers a road's posted limit by <paramref name="delta"/> m/s, within the allowed range.</summary>
    public bool ChangeSpeedLimit(int road, float delta)
    {
        if (Sim.Execute(new SetSpeedLimitCommand(road, SteppedSpeedLimit(road, delta))) == 0) return false;
        RebuildRoads();
        return true;
    }

    /// <summary>The junction or road end nearest a point within <paramref name="radius"/>: {found, node, x, y, z}.</summary>
    public Godot.Collections.Dictionary NodeAt(float x, float z, float radius)
    {
        var net = Sim.Network;
        int node = net.FindNodeNear(x, z, radius, withRoadsOnly: true);
        if (node < 0) return new Godot.Collections.Dictionary { ["found"] = false };
        var (nx, nz) = net.GetNode(node);
        return new Godot.Collections.Dictionary { ["found"] = true, ["node"] = node, ["x"] = nx, ["y"] = net.GetNodeHeight(node), ["z"] = nz };
    }

    /// <summary>What dragging a node to a point costs: {valid, length, cut, fill, money, bridge, tunnel}.</summary>
    public Godot.Collections.Dictionary EstimateMoveNode(int node, float x, float z)
    {
        if (Sim.Network.EstimateMoveNode(node, x, z) is not { } c) return new Godot.Collections.Dictionary { ["valid"] = false };
        var demolition = Sim.EstimateMoveDemolition(node, x, z);
        return new Godot.Collections.Dictionary
        {
            ["valid"] = true, ["length"] = c.Length, ["cut"] = c.CutVolume, ["fill"] = c.FillVolume, ["money"] = c.Money,
            ["bridge"] = c.BridgeLength, ["tunnel"] = c.TunnelLength,
            ["demolished"] = demolition.Count, ["demolition_price"] = demolition.Price,
            ["affordable"] = Sim.CanAfford(c.Money + demolition.Price),
        };
    }

    /// <summary>
    /// The roads at a node, to draw where they would run when it moves: for each {end, sweep, starts_here}, the other end
    /// on the ground, the bend of the road from its start to its end, and whether it starts at this node.
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> NodeRoadShapes(int node)
    {
        var net = Sim.Network;
        var shapes = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var id in net.RoadsAt(node))
        {
            var road = net.GetRoad(id);
            bool startsHere = road.From == node;
            int other = startsHere ? road.To : road.From;
            var (x, z) = net.GetNode(other);
            shapes.Add(new Godot.Collections.Dictionary
            {
                ["end"] = new Vector3(x, net.GetNodeHeight(other), z),
                ["sweep"] = road.Geometry.Sweep,
                ["starts_here"] = startsHere,
            });
        }
        return shapes;
    }

    /// <summary>Drags a node to a point; the roads at it are rebuilt there (same bend, bridges and tunnels worked out again) and vehicles on them carry on.</summary>
    public bool MoveNode(int node, float x, float z)
    {
        if (Sim.Execute(new MoveNodeCommand(node, x, z)) == 0) return false;
        RebuildRoads();
        return true;
    }

    /// <summary>Places a depot at the node nearest the point (within 60 m) that has none yet.</summary>
    public bool BuildDepot(float x, float z, int level)
    {
        int node = Sim.Network.FindNodeNear(x, z, 60f, withRoadsOnly: true);
        if (node < 0) return false;
        if (Sim.Execute(new AddDepotCommand(node, (DepotLevel)Math.Clamp(level, 0, 2))) < 0) return false;
        RebuildRoads();
        return true;
    }

    // ---- Company, stations and lines ----------------------------------------------------------------------

    /// <summary>The company: {money, loan, max_loan, net_worth, day, bankrupt, income, upkeep, interest} (the last three are totals so far, income positive, the others spending as negatives).</summary>
    public Godot.Collections.Dictionary GetCompany()
    {
        var c = Sim.Company;
        return new Godot.Collections.Dictionary
        {
            ["money"] = c.Money,
            ["loan"] = c.Loan,
            ["max_loan"] = Sim.Network.Tuning.Economy.MaxLoan,
            ["net_worth"] = c.NetWorth,
            ["day"] = Sim.Day,
            ["year"] = Sim.Today.Year,
            ["month"] = Sim.Today.Month,
            ["day_of_month"] = Sim.Today.DayOfMonth,
            ["hour"] = Sim.TimeOfDay * 24f,
            ["bankrupt"] = c.Bankrupt,
            ["income"] = c.Total(LedgerEntry.Income),
            ["upkeep"] = c.Total(LedgerEntry.Upkeep),
            ["interest"] = c.Total(LedgerEntry.Interest),
        };
    }

    public bool TakeLoan(long amount) => Sim.Execute(new TakeLoanCommand(amount)) == 1;

    public bool RepayLoan(long amount) => Sim.Execute(new RepayLoanCommand(amount)) == 1;

    /// <summary>Where a station clicked at (x, z) would stand: {valid, x, y, z, price, affordable, radius}.</summary>
    public Godot.Collections.Dictionary StationPreview(float x, float z)
    {
        if (Sim.FindStationSite(x, z) is not { } site) return new Godot.Collections.Dictionary { ["valid"] = false };
        return new Godot.Collections.Dictionary
        {
            ["valid"] = true,
            ["x"] = site.X, ["y"] = site.Y, ["z"] = site.Z,
            ["price"] = Sim.StationPrice,
            ["affordable"] = Sim.CanAfford(Sim.StationPrice),
            ["radius"] = Sim.Network.Tuning.Stations.CoverageRadius,
            ["covered"] = CoveredCount(site.X, site.Z),
            ["industries"] = Sim.IndustriesNear(site.X, site.Z, Sim.Network.Tuning.Stations.CoverageRadius).Count,
        };
    }

    /// <summary>Buildings a station at a point would serve: those standing with a road, within the coverage radius.</summary>
    int CoveredCount(float x, float z)
    {
        int n = 0;
        foreach (int id in Sim.BuildingsNear(x, z, Sim.Network.Tuning.Stations.CoverageRadius))
            if (Sim.Buildings[id].State == BuildingState.Active) n++;
        return n;
    }

    /// <summary>Builds a station at the node or road point near (x, z). Returns its id, -1 when refused.</summary>
    public int BuildStation(float x, float z)
    {
        int id = Sim.Execute(new BuildStationCommand(x, z));
        if (id >= 0) RebuildRoads();
        return id;
    }

    /// <summary>The open station nearest to a point within <paramref name="radius"/>, or -1.</summary>
    public int StationNear(float x, float z, float radius)
    {
        int best = -1;
        float bestDistance = radius * radius;
        foreach (var station in Sim.Stations)
        {
            if (!station.Active) continue;
            var (sx, sz) = Sim.Network.GetNode(station.Node);
            float d = (sx - x) * (sx - x) + (sz - z) * (sz - z);
            if (d <= bestDistance) { best = station.Id; bestDistance = d; }
        }
        return best;
    }

    /// <summary>A station's place on the ground.</summary>
    public Vector3 StationPosition(int station)
    {
        var node = Sim.Stations[station].Node;
        var (x, z) = Sim.Network.GetNode(node);
        return new Vector3(x, Sim.Network.GetNodeHeight(node), z);
    }

    /// <summary>Creates a line through the stations in order. Returns its id, -1 when refused.</summary>
    public int CreateLine(int[] stations) => Sim.Execute(new CreateLineCommand(stations));

    public bool DeleteLine(int line) => Sim.Execute(new DeleteLineCommand(line)) == 1;

    /// <summary>The vehicles the company can buy: for each {id, name, price, capacity, upkeep, cargo}.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetVehicleModels()
    {
        var models = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var m in Sim.VehicleModels)
            models.Add(new Godot.Collections.Dictionary { ["id"] = m.Id, ["name"] = m.Name, ["price"] = m.Price, ["capacity"] = m.Capacity, ["upkeep"] = m.UpkeepPerDay, ["cargo"] = m.Cargo });
        return models;
    }

    /// <summary>
    /// Buys a vehicle of a model for a line at the first depot with room for it. Returns the vehicle id, -1 when no
    /// depot can take it or the company cannot pay.
    /// </summary>
    public int BuyVehicle(string model, int line)
    {
        foreach (var depot in Sim.Depots)
        {
            if (!depot.Active) continue;
            int id = Sim.Execute(new BuyVehicleCommand(depot.Id, model, line));
            if (id >= 0) return id;
        }
        return -1;
    }

    /// <summary>The open lines: for each {id, color, stations (names), income, upkeep, profit, delivered, vehicles, loading}.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetLines()
    {
        var vehicles = new int[Sim.Lines.Count];
        var loading = new int[Sim.Lines.Count];
        for (int v = 0; v < Sim.VehicleCount; v++)
        {
            if (!Sim.IsCompanyVehicle(v)) continue;
            var info = Sim.GetFleetInfo(v);
            vehicles[info.Line]++;
            if (info.Loading) loading[info.Line]++;
        }
        var lines = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var line in Sim.Lines)
        {
            if (!line.Active) continue;
            var names = new Godot.Collections.Array<string>();
            foreach (int s in line.Stops) names.Add($"S{s + 1}");
            lines.Add(new Godot.Collections.Dictionary
            {
                ["id"] = line.Id,
                ["color"] = LineColor(line.Id),
                ["stations"] = names,
                ["income"] = line.Income,
                ["upkeep"] = line.Upkeep,
                ["profit"] = line.Profit,
                ["delivered"] = line.Delivered,
                ["vehicles"] = vehicles[line.Id],
                ["loading"] = loading[line.Id],
            });
        }
        return lines;
    }

    /// <summary>Open stations: for each {id, name, x, y, z, waiting, accepts} where waiting is the units of each cargo and accepts whether the station takes it, in the order of <see cref="GetCargoNames"/>.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetStations()
    {
        var stations = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var station in Sim.Stations)
        {
            if (!station.Active) continue;
            var waiting = new Godot.Collections.Array<int>();
            foreach (float w in station.Waiting) waiting.Add((int)w);
            var accepts = new Godot.Collections.Array<bool>();
            for (int c = 0; c < Sim.CargoTypes.Count; c++) accepts.Add(Sim.Accepts(station, c));
            var p = StationPosition(station.Id);
            stations.Add(new Godot.Collections.Dictionary { ["id"] = station.Id, ["name"] = $"S{station.Id + 1}", ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z, ["waiting"] = waiting, ["accepts"] = accepts });
        }
        return stations;
    }

    /// <summary>The names of the cargo types, in the order of the tuning file.</summary>
    public Godot.Collections.Array<string> GetCargoNames()
    {
        var names = new Godot.Collections.Array<string>();
        foreach (var c in Sim.CargoTypes) names.Add(c.Name);
        return names;
    }

    /// <summary>
    /// The industries: for each {id, name, x, y, z, output (cargo name), stock (waiting to be collected), produced, inputs}
    /// where inputs lists {cargo, stock, max} for the cargo it uses.
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetIndustries()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        var types = Sim.IndustryTuning.Types;
        foreach (var industry in Sim.Industries)
        {
            var type = types[industry.Type];
            var inputs = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            foreach (var input in type.Inputs)
            {
                int c = -1;
                for (int k = 0; k < Sim.CargoTypes.Count; k++) if (Sim.CargoTypes[k].Id == input.Cargo) c = k;
                inputs.Add(new Godot.Collections.Dictionary { ["cargo"] = Sim.CargoTypes[c].Name, ["stock"] = industry.Input[c], ["max"] = type.MaxInput });
            }
            list.Add(new Godot.Collections.Dictionary
            {
                ["id"] = industry.Id, ["name"] = type.Name, ["x"] = industry.X, ["y"] = HeightAt(industry.X, industry.Z), ["z"] = industry.Z,
                ["output"] = Sim.CargoTypes[Sim.OutputOf(industry.Type)].Name, ["stock"] = industry.Output, ["max_output"] = type.MaxOutput,
                ["produced"] = industry.Produced, ["inputs"] = inputs,
            });
        }
        return list;
    }

    /// <summary>
    /// The towns: for each {id, name, x, y, z, satisfaction, population, jobs, homes, commerce, industry, inactive,
    /// abandoned, unmet} (counts of buildings, inactive and abandoned ones apart; unmet is the share of last month's needs that were not met, 0 to 1).
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetTowns()
    {
        var towns = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var town in Sim.Towns)
        {
            var s = Sim.StatsOf(town.Id);
            towns.Add(new Godot.Collections.Dictionary
            {
                ["id"] = town.Id, ["name"] = town.Name, ["x"] = town.X, ["y"] = HeightAt(town.X, town.Z), ["z"] = town.Z,
                ["satisfaction"] = town.Satisfaction,
                ["population"] = s.Population, ["jobs"] = s.Jobs,
                ["homes"] = s.Homes, ["commerce"] = s.Commerce, ["industry"] = s.Industry,
                ["inactive"] = s.Inactive, ["abandoned"] = s.Abandoned, ["unmet"] = town.UnmetShare,
            });
        }
        return towns;
    }

    /// <summary>Runs the simulation for this many simulated seconds right now, without drawing (for the self-tests).</summary>
    public void StepFor(double seconds)
    {
        long steps = (long)Math.Ceiling(seconds / Simulation.StepSeconds);
        for (long i = 0; i < steps; i++) Sim.Step();
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

        if (Sim.BuildingsVersion != _buildingsVersion) RebuildBuildings();
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

    /// <summary>The pose of a vehicle inside a junction (see <see cref="Simulation.JunctionPoseOf"/>), with the road height there.</summary>
    bool TurnPose(int vehicle, float centre, out float x, out float z, out float heading, out float y)
    {
        x = z = heading = y = 0f;
        if (Sim.JunctionPoseOf(vehicle, centre) is not { } pose) return false;
        x = pose.X; z = pose.Z; heading = pose.Heading;
        int from = pose.From.Value, to = pose.To.Value;
        float yFrom = _laneY[from] + _laneGrade[from] * (_laneLength[from] - RoadNetwork.JunctionReach(_laneLength[from]));
        float yTo = _laneY[to] + _laneGrade[to] * RoadNetwork.JunctionReach(_laneLength[to]);
        y = yFrom + (yTo - yFrom) * Math.Clamp(pose.Distance / Math.Max(pose.Length, 0.01f), 0f, 1f);
        return true;
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
            float length = Sim.LengthOf(i);
            float pos = v.Position + v.Speed * Simulation.StepSeconds * stepFraction - length * 0.5f;
            float side = -Simulation.LaneWidth * v.Lateral + v.Shift;
            float cx, cz, dx, dz, baseY;
            if (TurnPose(i, pos, out cx, out cz, out float heading, out baseY))
            {
                // Inside a junction the vehicle follows the path through it; that path already includes the lane offset.
                dx = MathF.Cos(heading); dz = MathF.Sin(heading);
            }
            else
            {
                LaneFrame(lane, pos, out cx, out cz, out dx, out dz);
                side += _laneSide[lane];
                baseY = _laneY[lane] + _laneGrade[lane] * pos;
            }
            float x = cx - dz * side;
            float z = cz + dx * side;

            float yaw = 0f;
            if (v.Kind != VehicleKind.Car) yaw = ((i * 2654435761u) % 1000) / 1000f * 0.8f - 0.4f;
            float cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
            float fx = dx * cos - dz * sin, fz = dx * sin + dz * cos; // forward, turned by the wreck's skew

            // Pitched to follow the grade of the lane.
            float grade = _laneGrade[lane], tilt = 1f / MathF.Sqrt(1f + grade * grade), rise = grade * tilt;
            bool company = Sim.IsCompanyVehicle(i);
            float tall = company ? 1.6f : 1f; // buses and trucks stand taller
            float y = baseY + 0.8f * tall;
            buf[o + 0] = fx * tilt * length;  buf[o + 1] = -fx * rise * tall; buf[o + 2] = -fz; buf[o + 3] = x;
            buf[o + 4] = rise * length;       buf[o + 5] = tilt * tall;       buf[o + 6] = 0f;  buf[o + 7] = y;
            buf[o + 8] = fz * tilt * length;  buf[o + 9] = -fz * rise * tall; buf[o + 10] = fx; buf[o + 11] = z;

            Color c = v.Kind switch
            {
                VehicleKind.Wreck => new Color(0.8f, 0.1f, 0.08f),
                VehicleKind.Debris => new Color(0.9f, 0.55f, 0.1f),
                _ when company => LineColor(Sim.GetFleetInfo(i).Line),
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

    /// <summary>A strong colour per line, shared by its vehicles and the line list.</summary>
    static Color LineColor(int line) => Color.FromHsv(((line * 0.381966f) % 1f), 0.85f, 0.95f);

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

            if (road.Structure == RoadStructure.Bridge) AddBridge(net, road, y0, y1, width);
            else if (road.Structure == RoadStructure.Tunnel) AddTunnel(net, road, y0, y1, width);

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

        foreach (var station in Sim.Stations)
        {
            if (!station.Active) continue;
            var (x, z) = net.GetNode(station.Node);
            var platform = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(14f, 4f, 10f) }, MaterialOverride = _stationMaterial };
            platform.Position = new Vector3(x - 24f, _terrain.HeightAt(x - 24f, z - 24f) + 2f, z - 24f);
            _roadRoot.AddChild(platform);
        }

        foreach (var depot in Sim.Depots)
        {
            if (!depot.Active) continue;
            var (x, z) = net.GetNode(depot.Node);
            var building = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(22f, 6f, 22f) }, MaterialOverride = _depotMaterial };
            building.Position = new Vector3(x + 26f, _terrain.HeightAt(x + 26f, z + 26f) + 3f, z + 26f);
            _roadRoot.AddChild(building);
        }
        RebuildBuildings();
    }

    static readonly Color[] BuildingColors =
    {
        new(0.80f, 0.64f, 0.46f), // home
        new(0.38f, 0.58f, 0.86f), // commerce
        new(0.62f, 0.62f, 0.66f), // industry
    };

    /// <summary>Redraws every building that stands (one box each, coloured by kind and state) and the town names.</summary>
    void RebuildBuildings()
    {
        _buildingsVersion = Sim.BuildingsVersion;
        var tuning = Sim.TownTuning;
        int n = Sim.Industries.Count;
        foreach (var b in Sim.Buildings) if (b.State != BuildingState.Demolished) n++;
        _buildings.InstanceCount = n;
        var buf = new float[n * FloatsPerInstance];
        int k = 0;
        foreach (var industry in Sim.Industries)
        {
            var type = Sim.IndustryTuning.Types[industry.Type];
            float side = type.Size * 2f;
            float fx = MathF.Cos(industry.Angle), fz = MathF.Sin(industry.Angle);
            float y = Math.Max(_terrain.HeightAt(industry.X, industry.Z), _terrain.WaterSurfaceAt(industry.X, industry.Z)) - 0.3f + type.Height * 0.5f;
            int o = k++ * FloatsPerInstance;
            buf[o + 0] = fx * side;  buf[o + 1] = 0f;          buf[o + 2] = -fz * side; buf[o + 3] = industry.X;
            buf[o + 4] = 0f;         buf[o + 5] = type.Height; buf[o + 6] = 0f;         buf[o + 7] = y;
            buf[o + 8] = fz * side;  buf[o + 9] = 0f;          buf[o + 10] = fx * side; buf[o + 11] = industry.Z;
            buf[o + 12] = type.Color[0]; buf[o + 13] = type.Color[1]; buf[o + 14] = type.Color[2]; buf[o + 15] = 1f;
        }
        foreach (var b in Sim.Buildings)
        {
            if (b.State == BuildingState.Demolished) continue;
            var tier = tuning.Of(b.Type, b.Tier);
            float side = tier.Size * 2f;
            float fx = MathF.Cos(b.Facing), fz = MathF.Sin(b.Facing);
            float y = Math.Max(_terrain.HeightAt(b.X, b.Z), _terrain.WaterSurfaceAt(b.X, b.Z)) - 0.3f + tier.Height * 0.5f;
            int o = k++ * FloatsPerInstance;
            buf[o + 0] = fx * side;  buf[o + 1] = 0f;          buf[o + 2] = -fz * side; buf[o + 3] = b.X;
            buf[o + 4] = 0f;         buf[o + 5] = tier.Height; buf[o + 6] = 0f;         buf[o + 7] = y;
            buf[o + 8] = fz * side;  buf[o + 9] = 0f;          buf[o + 10] = fx * side; buf[o + 11] = b.Z;
            var colour = BuildingColors[(int)b.Type];
            colour = b.State switch
            {
                BuildingState.Inactive => colour.Lerp(new Color(0.45f, 0.45f, 0.45f), 0.65f),
                BuildingState.Abandoned => new Color(0.17f, 0.15f, 0.15f),
                _ => colour,
            };
            buf[o + 12] = colour.R; buf[o + 13] = colour.G; buf[o + 14] = colour.B; buf[o + 15] = 1f;
        }
        if (n > 0) _buildings.Buffer = buf;

        foreach (var child in _townLabels.GetChildren()) child.QueueFree();
        foreach (var town in Sim.Towns)
        {
            _townLabels.AddChild(new Label3D
            {
                Text = town.Name,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                PixelSize = 0.0014f,
                FontSize = 36,
                OutlineSize = 10,
                NoDepthTest = true,
                Position = new Vector3(town.X, HeightAt(town.X, town.Z) + 70f, town.Z),
            });
        }
        foreach (var industry in Sim.Industries)
        {
            _townLabels.AddChild(new Label3D
            {
                Text = Sim.IndustryTuning.Types[industry.Type].Name,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                PixelSize = 0.0011f,
                FontSize = 30,
                OutlineSize = 8,
                NoDepthTest = true,
                Modulate = new Color(0.9f, 0.95f, 0.8f),
                Position = new Vector3(industry.X, HeightAt(industry.X, industry.Z) + 40f, industry.Z),
            });
        }
    }

    /// <summary>Piers every 25 m from the deck down to the ground (or the bed of the river), and a slab under the road.</summary>
    void AddBridge(RoadNetwork net, Road road, float y0, float y1, float width)
    {
        var g = road.Geometry;
        int piers = Math.Max(1, (int)MathF.Floor(g.Length / 25f));
        for (int k = 0; k <= piers; k++)
        {
            float t = k / (float)piers;
            var (x, z) = g.PointAt(g.Length * t);
            float deck = y0 + (y1 - y0) * t;
            float ground = _terrain.HeightAt(x, z);
            if (deck - ground < 1.5f || k == 0 || k == piers) continue; // the ends rest on the ground
            float h = deck - ground;
            var pier = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.4f, h, width * 0.45f) }, MaterialOverride = _deckMaterial };
            float heading = g.HeadingAt(g.Length * t);
            pier.Transform = new Transform3D(new Basis(Vector3.Up, -heading), new Vector3(x, ground + h * 0.5f - TerrainSink, z));
            _roadRoot.AddChild(pier);
        }
        // The deck: a slab slightly thicker than the road, hanging below the surface.
        var (sx, sz) = net.GetNode(road.From);
        var (ex, ez) = net.GetNode(road.To);
        if (g.IsStraight)
        {
            float run = g.Length;
            float dx = (ex - sx) / run, dz = (ez - sz) / run;
            AddRoadBox(new Vector3((sx + ex) * 0.5f, (y0 + y1) * 0.5f, (sz + ez) * 0.5f), run, y1 - y0, dx, dz, width + 1.2f, 1.1f, -0.55f, _deckMaterial);
        }
        else AddRoadRibbon(g, y0, y1, width + 1.2f, -0.05f, _deckMaterial);
    }

    /// <summary>A dark frame at each end, and a faint strip over the ground where the road runs underneath.</summary>
    void AddTunnel(RoadNetwork net, Road road, float y0, float y1, float width)
    {
        var g = road.Geometry;
        foreach (float along in new[] { 0f, g.Length })
        {
            var (x, z) = g.PointAt(along);
            float heading = g.HeadingAt(along);
            float y = along == 0f ? y0 : y1;
            var portal = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.5f, 6.5f, width + 3f) }, MaterialOverride = _tunnelMaterial };
            portal.Transform = new Transform3D(new Basis(Vector3.Up, -heading), new Vector3(x, y + 3f, z));
            _roadRoot.AddChild(portal);
        }
        int segments = Math.Max(2, (int)MathF.Ceiling(g.Length / 6f));
        var vertices = new Vector3[(segments + 1) * 2];
        var normals = new Vector3[vertices.Length];
        var indices = new int[segments * 6];
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            var (x, z) = g.PointAt(g.Length * t);
            float h = g.HeadingAt(g.Length * t);
            float rx = -MathF.Sin(h) * width * 0.5f, rz = MathF.Cos(h) * width * 0.5f;
            float top = Math.Max(_terrain.HeightAt(x, z), y0 + (y1 - y0) * t) + 0.35f;
            vertices[i * 2] = new Vector3(x - rx, top, z - rz);
            vertices[i * 2 + 1] = new Vector3(x + rx, top, z + rz);
            normals[i * 2] = normals[i * 2 + 1] = Vector3.Up;
        }
        for (int i = 0; i < segments; i++)
        {
            int l = i * 2, r = l + 1, nl = l + 2, nr = l + 3, o = i * 6;
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
        _roadRoot.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = _tunnelHintMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
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

    /// <summary>
    /// Brings the ground meshes up to date. The map is drawn as chunks of <see cref="Terrain.ChunkCells"/> cells; a new
    /// terrain builds them all, and a carved road rebuilds only the chunks it touched.
    /// </summary>
    void RebuildTerrain()
    {
        if (_terrainVersion == _terrain.Version && _terrainSource == _terrain && _terrainChunks.Length > 0) return;
        _terrainVersion = _terrain.Version;

        int side = _terrain.ChunksPerSide;
        if (_terrainSource != _terrain || _terrainChunks.Length != side * side)
        {
            _terrainSource = _terrain;
            _water.Mesh = WaterMesh();
            _water.Position = Vector3.Zero;
            foreach (var child in _terrainRoot.GetChildren()) child.QueueFree();
            _terrainChunks = new MeshInstance3D?[side * side];
            _terrain.TakeChangedChunks(); // everything is built below
            for (int c = 0; c < side * side; c++) BuildTerrainChunk(c);
            return;
        }
        foreach (int c in _terrain.TakeChangedChunks()) BuildTerrainChunk(c);
    }

    /// <summary>One quad over the whole map at the height of the water surface, which tilts with the fall toward the sea.</summary>
    ArrayMesh WaterMesh()
    {
        float half = _terrain.HalfSize;
        var corners = new[] { (-half, -half), (half, -half), (-half, half), (half, half) };
        var vertices = new Vector3[4];
        for (int k = 0; k < 4; k++)
        {
            var (x, z) = corners[k];
            // just under level ground, which is drawn TerrainSink lower
            vertices[k] = new Vector3(x, _terrain.WaterSurfaceAt(x, z) - TerrainSink - 0.01f, z);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Up, Vector3.Up, Vector3.Up, Vector3.Up };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 1, 3, 2 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    void BuildTerrainChunk(int chunk)
    {
        int side = _terrain.ChunksPerSide;
        int i0 = chunk % side * Terrain.ChunkCells, j0 = chunk / side * Terrain.ChunkCells;
        int i1 = Math.Min(i0 + Terrain.ChunkCells, _terrain.Cells), j1 = Math.Min(j0 + Terrain.ChunkCells, _terrain.Cells);
        int w = i1 - i0 + 1, h = j1 - j0 + 1;
        var vertices = new Vector3[w * h];
        var normals = new Vector3[w * h];
        var colors = new Color[w * h];
        float cell = _terrain.CellSize, half = _terrain.HalfSize;
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                float height = _terrain.GetSample(i, j);
                int k = (j - j0) * w + (i - i0);
                vertices[k] = new Vector3(i * cell - half, height, j * cell - half);
                float gx = _terrain.GetSample(i + 1, j) - _terrain.GetSample(i - 1, j);
                float gz = _terrain.GetSample(i, j + 1) - _terrain.GetSample(i, j - 1);
                normals[k] = new Vector3(-gx, 2f * cell, -gz).Normalized();
                colors[k] = TerrainColor(height, _terrain.WaterSurfaceAtSample(i, j), normals[k].Y, NearWater(i, j)).SrgbToLinear(); // vertex colours are read as linear
            }
        var indices = new int[(w - 1) * (h - 1) * 6];
        int t = 0;
        for (int j = 0; j < h - 1; j++)
            for (int i = 0; i < w - 1; i++)
            {
                int a = j * w + i, b = a + 1, c = a + w, d = c + 1;
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

        var instance = _terrainChunks[chunk];
        if (instance == null)
        {
            instance = new MeshInstance3D { Name = $"Chunk{chunk}", MaterialOverride = _terrainMaterial };
            _terrainRoot.AddChild(instance);
            _terrainChunks[chunk] = instance;
        }
        instance.Mesh = mesh;
    }

    /// <summary>Whether a sample is dry but within two samples of water, which is where the shore is sand.</summary>
    bool NearWater(int i, int j)
    {
        if (Wet(i, j)) return false;
        for (int d = 1; d <= 2; d++)
            if (Wet(i + d, j) || Wet(i - d, j) || Wet(i, j + d) || Wet(i, j - d)) return true;
        return false;
    }

    bool Wet(int i, int j) => _terrain.GetSample(i, j) < _terrain.WaterSurfaceAtSample(i, j);

    static Color TerrainColor(float ground, float surface, float normalY, bool nearWater)
    {
        float height = ground; // above sea level, for the colour bands
        float depth = ground - surface; // above the water
        var grass = new Color(0.42f, 0.58f, 0.34f);
        var upland = new Color(0.5f, 0.55f, 0.32f);
        var rock = new Color(0.52f, 0.48f, 0.43f);
        var snow = new Color(0.9f, 0.92f, 0.95f);
        var bed = new Color(0.45f, 0.42f, 0.32f);
        var shore = new Color(0.74f, 0.69f, 0.5f);
        if (depth < 0f) return bed.Lerp(shore, Mathf.Clamp(1f + depth / 3f, 0f, 1f)); // sand at the edge, mud in the deep
        if (nearWater && depth < 1.5f) return shore.Lerp(grass, depth / 1.5f);
        Color c = height < 40f ? grass.Lerp(upland, height / 40f) : upland.Lerp(rock, Mathf.Clamp((height - 40f) / 40f, 0f, 1f));
        if (height > 85f) c = c.Lerp(snow, Mathf.Clamp((height - 85f) / 25f, 0f, 1f));
        return c.Lerp(rock, Mathf.Clamp((0.9f - normalY) * 4f, 0f, 0.8f)); // steep ground shows rock
    }
}
