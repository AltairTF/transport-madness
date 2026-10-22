using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    MultiMesh _marks = null!; // the Line markers on the roofs of vehicles that have a Model
    ModelDraw _vehicleModels = null!, _crewModels = null!, _buildingModels = null!;
    Node3D _industryRoot = null!;
    readonly Dictionary<int, Node3D> _industryNodes = new();
    Simulation? _industryNodesOf; // the Simulation the nodes above stand for
    static readonly string[][] TownModelIds =
    {
        new[] { "town.home.0", "town.home.1", "town.home.2" },
        new[] { "town.commerce.0", "town.commerce.1", "town.commerce.2" },
        new[] { "town.industry.0", "town.industry.1", "town.industry.2" },
    };
    StandardMaterial3D? _smokeMaterial;
    float[] _markBuffer = new float[InitialCapacity * FloatsPerInstance];
    string[]? _roadModelIds;
    static readonly string[] CarModelIds = { "car.a", "car.b", "car.c" };
    MultiMesh _buildings = null!;
    Node3D _townLabels = null!;
    int _buildingsVersion = -1;
    int _networkVersion = -1; // of the roads as last drawn
    readonly float[] _crewBuffer = new float[CrewCapacity * FloatsPerInstance];
    float[] _buffer = new float[InitialCapacity * FloatsPerInstance];
    int _capacity = InitialCapacity;
    Node3D _roadRoot = null!, _stationRoot = null!;
    Node3D _terrainRoot = null!;
    ShaderMaterial _terrainMaterial = null!;
    MeshInstance3D?[] _terrainChunks = Array.Empty<MeshInstance3D?>();
    int _terrainVersion = -1;
    Terrain _terrain = null!, _terrainSource = null!;
    MeshInstance3D _water = null!;
    MeshInstance3D _cutaway = null!; // the four sides and the bottom of the slice of world
    StandardMaterial3D _waterMaterial = null!, _deckMaterial = null!, _tunnelMaterial = null!, _tunnelHintMaterial = null!;
    StandardMaterial3D _roadMaterial = null!, _medianMaterial = null!, _depotMaterial = null!, _lineMaterial = null!, _stationMaterial = null!, _pathMaterial = null!, _depotIdleMaterial = null!;
    readonly StandardMaterial3D[] _depotLevelMaterials = new StandardMaterial3D[3];

    // Per-lane drawing data: start point, unit direction and sideways offset of the lane centre (to the driver's right).
    float[] _laneX = Array.Empty<float>(), _laneZ = Array.Empty<float>();
    float[] _laneDx = Array.Empty<float>(), _laneDz = Array.Empty<float>();
    float[] _laneSide = Array.Empty<float>(), _laneLength = Array.Empty<float>();
    float[] _laneSweep = Array.Empty<float>(), _laneH0 = Array.Empty<float>(), _laneSinH0 = Array.Empty<float>(), _laneCosH0 = Array.Empty<float>();
    float[] _laneY = Array.Empty<float>(), _laneGrade = Array.Empty<float>();

    Whitebox _whitebox = new();
    StandardMaterial3D _padMaterial = null!, _doorMaterial = null!, _markerMaterial = null!;

    public override void _Ready()
    {
        _whitebox = Whitebox.Load();
        _roadRoot = new Node3D { Name = "Roads" };
        AddChild(_roadRoot);
        _stationRoot = new Node3D { Name = "Stations" };
        AddChild(_stationRoot);
        _roadMaterial = Textured(new Color(0.23f, 0.24f, 0.26f), "asphalt", 4f);
        _pathMaterial = Textured(new Color(0.5f, 0.44f, 0.34f), "paving", 3f); // the stone road of an access connection (M6e)
        _medianMaterial = Textured(new Color(0.35f, 0.5f, 0.3f), "grass", 6f);
        for (int level = 0; level < 3; level++) _depotLevelMaterials[level] = Flat(_whitebox.Depots[level].Color);
        _depotMaterial = _depotLevelMaterials[1];
        _depotIdleMaterial = Flat(_whitebox.DepotIdle);
        _stationMaterial = Flat(_whitebox.Station.Color);
        _padMaterial = Flat(_whitebox.PadColor);
        _doorMaterial = Flat(_whitebox.DoorColor);
        _markerMaterial = Flat(_whitebox.MarkerColor);
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
        _waterMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.16f, 0.4f, 0.62f, 0.72f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.15f,
            Metallic = 0.2f,
            NormalEnabled = true,
            NormalScale = 0.35f,
            NormalTexture = new NoiseTexture2D
            {
                Width = 512, Height = 512, Seamless = true, AsNormalMap = true, BumpStrength = 6f,
                Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.02f },
            },
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = Vector3.One / 40f, // one tile of ripples is 40 m
        };
        _water = new MeshInstance3D { Name = "Water", MaterialOverride = _waterMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_water);
        _terrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/terrain.gdshader") };
        _terrainMaterial.SetShaderParameter("grass_tex", GD.Load<Texture2D>("res://game/assets/ground/ambientcg/grass.png"));
        _terrainMaterial.SetShaderParameter("dirt_tex", GD.Load<Texture2D>("res://game/assets/ground/ambientcg/dirt.png"));
        _terrainMaterial.SetShaderParameter("rock_tex", GD.Load<Texture2D>("res://game/assets/ground/ambientcg/rock.png"));
        _cutaway = new MeshInstance3D { Name = "Cutaway", Position = new Vector3(0, -TerrainSink, 0) };
        AddChild(_cutaway);
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
        _marks = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.14f, 1.1f) },
            InstanceCount = InitialCapacity,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Name = "LineMarkers", Multimesh = _marks, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _vehicleModels = new ModelDraw(this);
        _crewModels = new ModelDraw(this);
        _buildingModels = new ModelDraw(this, tinted: true);
        _industryRoot = new Node3D { Name = "Industries" };
        AddChild(_industryRoot);
        AddChild(new MultiMeshInstance3D { Name = "Crews", Multimesh = _crews, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(new MultiMeshInstance3D { Name = "Vehicles", Multimesh = _vehicles, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        ReadyRail();
    }

    static StandardMaterial3D Flat(Color color) => new() { AlbedoColor = color, Roughness = 1f };

    /// <summary>A flat colour with the grain of a ground texture (M7e), projected from the world so that ribbons and boxes without UVs take it; <paramref name="metres"/> is the size of one tile.</summary>
    static StandardMaterial3D Textured(Color color, string texture, float metres) => new()
    {
        AlbedoColor = new Color(color.R * 2f, color.G * 2f, color.B * 2f), // the textures have a mean of 0.5
        AlbedoTexture = GD.Load<Texture2D>("res://game/assets/ground/ambientcg/" + texture + ".png"),
        Roughness = 1f,
        Uv1Triplanar = true,
        Uv1WorldTriplanar = true,
        Uv1Scale = Vector3.One / metres,
    };

    // ---- API for GDScript -----------------------------------------------------------------------------------

    /// <summary>How often random events happen in the next world that is started: 0 off, 1 low, 2 normal, 3 high (the Maintenance window changes it in the running game).</summary>
    public int StartFrequency { get; set; } = (int)IncidentFrequency.Normal;

    IncidentFrequency StartingFrequency => (IncidentFrequency)Math.Clamp(StartFrequency, 0, 3);

    /// <summary>Starts a fresh demo world with the given seed and number of ambient vehicles.</summary>
    public void NewDemoWorld(long seed, int vehicles)
    {
        var net = new RoadNetwork(Terrain.Flat());
        DemoWorld.Build(net);
        Sim = new Simulation(net, (ulong)seed, StartingFrequency);
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
        Sim = new Simulation(new RoadNetwork(Terrain.Flat()), (ulong)seed, StartingFrequency);
        RebuildRoads();
        _accumulator = 0;
    }

    /// <summary>Starts an empty map of generated hills.</summary>
    public void NewHillsWorld(long seed)
    {
        Sim = new Simulation(new RoadNetwork(Terrain.Generate((ulong)seed)), (ulong)seed, StartingFrequency);
        RebuildRoads();
        _accumulator = 0;
    }

    /// <summary>
    /// Starts a map of generated hills with towns and industries (<paramref name="industries"/>: 0 few, 1 normal, 2 many).
    /// Returns how many towns were placed; fewer than <paramref name="towns"/> when the land ran out. A few ambient cars
    /// are put on the streets. <see cref="UnlinkedIndustries"/> says how many processors found no place within reach of their inputs.
    /// </summary>
    public int NewTownsWorld(long seed, int towns, int industries)
    {
        var (sim, plan) = WorldBuilder.Towns((ulong)seed, towns, (IndustryAmount)Math.Clamp(industries, 0, 2), frequency: StartingFrequency);
        Sim = sim;
        UnlinkedIndustries = plan.Unlinked?.Count ?? 0;
        GatewaysPlaced = plan.Gateways?.Count ?? 0;
        RebuildRoads();
        _accumulator = 0;
        return plan.Towns.Count;
    }

    /// <summary>Processors of the last <see cref="NewTownsWorld"/> map that have no producer of an input, or no town, within reach.</summary>
    public int UnlinkedIndustries { get; private set; }
    /// <summary>How many of the two gateways found a place on the map.</summary>
    public int GatewaysPlaced { get; private set; }

    /// <summary>Height of what is seen at a point: the ground, or the water surface over it.</summary>
    public float HeightAt(float x, float z) => Math.Max(_terrain.HeightAt(x, z), _terrain.WaterSurfaceAt(x, z));

    /// <summary>Whether a straight road can be built between the points: long enough, not too steep, not crossing too close to a junction.</summary>
    public bool CanBuildRoad(float x0, float z0, float x1, float z1) => Sim.Network.CanBuildRoad(x0, z0, x1, z1);

    /// <summary>The sweep (total turn in radians, signed) of the arc from the first point through the second to the third.</summary>
    public float SweepThrough(float x0, float z0, float xm, float zm, float x1, float z1) =>
        RoadGeometry.SweepThrough(x0, z0, xm, zm, x1, z1);

    /// <summary>What a straight road would cost: {valid, length, cut, fill, money, bridge, tunnel} (the last two are metres of structure); only {valid} when it cannot be built.</summary>
    public Godot.Collections.Dictionary EstimateRoadCost(float x0, float z0, float x1, float z1, int type) =>
        EstimateCurveCost(x0, z0, x1, z1, type, 0f);

    /// <summary>
    /// What a road bending by <paramref name="sweep"/> radians would cost, as <see cref="BuildCurve"/> would build it: see <see cref="EstimateRoadCost"/>. The road is exactly
    /// as given (M6e); the money and the demolition include its access connections that can be built ({spurs}, with {spur_length} and {spur_money}), {spurs_blocked}
    /// counts those that cannot. {max_fill} and {max_cut} are the highest embankment and the deepest cutting along the road and the connections, in metres.
    /// </summary>
    public Godot.Collections.Dictionary EstimateCurveCost(float x0, float z0, float x1, float z1, int type, float sweep)
    {
        var join = Sim.JoinRoad(x0, z0, x1, z1, RoadTypeOf(type), sweep);
        var cost = Sim.Network.EstimateCost(x0, z0, x1, z1, RoadTypeOf(type), sweep);
        if (cost is not { } c) return new Godot.Collections.Dictionary { ["valid"] = false };
        var demolition = Sim.EstimateRoadDemolition(x0, z0, x1, z1, RoadTypeOf(type), sweep);
        var built = join.Spurs.Where(s => s.Buildable).ToList();
        int demolished = demolition.Count + built.Sum(s => s.Demolished);
        long demolitionPrice = demolition.Price + built.Sum(s => s.DemolitionPrice);
        float spurMoney = built.Sum(s => s.Money), spurLength = built.Sum(s => s.Cost?.Length ?? 0f);
        float maxFill = 0f, maxCut = 0f;
        Extremes(RoadRunData(x0, z0, x1, z1, sweep, RoadTypeOf(type)), ref maxFill, ref maxCut);
        foreach (var spur in built) Extremes(RoadRunData(spur.FromX, spur.FromZ, spur.ToX, spur.ToZ, 0f, RoadType.Path), ref maxFill, ref maxCut);
        return new Godot.Collections.Dictionary
        {
            ["valid"] = true, ["length"] = c.Length, ["cut"] = c.CutVolume, ["fill"] = c.FillVolume, ["money"] = c.Money + spurMoney,
            ["bridge"] = c.BridgeLength + built.Sum(s => s.Cost?.BridgeLength ?? 0f), ["tunnel"] = c.TunnelLength + built.Sum(s => s.Cost?.TunnelLength ?? 0f),
            ["max_fill"] = maxFill, ["max_cut"] = maxCut,
            ["demolished"] = demolished, ["demolition_price"] = demolitionPrice,
            ["spurs"] = built.Count, ["spurs_blocked"] = join.Spurs.Count - built.Count, ["spur_length"] = spurLength, ["spur_money"] = spurMoney,
            ["affordable"] = Sim.CanAfford(c.Money + spurMoney + demolitionPrice),
        };
    }

    /// <summary>
    /// What a road would be joined to, for the build preview (M6e): the road is never moved, and {spurs} lists the access connections it would get, each {name, points, money, buildable, runs}
    /// (points lie on the ground; runs are as <see cref="RoadRuns"/> gives them, empty when the connection cannot be built).
    /// </summary>
    public Godot.Collections.Dictionary RoadJoin(float x0, float z0, float x1, float z1, int type, float sweep)
    {
        var join = Sim.JoinRoad(x0, z0, x1, z1, RoadTypeOf(type), sweep);
        var spurs = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var spur in join.Spurs)
            spurs.Add(new Godot.Collections.Dictionary
            {
                ["name"] = AccessName(spur.Target), ["points"] = CurvePoints(spur.FromX, spur.FromZ, spur.ToX, spur.ToZ, 0f), ["money"] = spur.Money + spur.DemolitionPrice,
                ["buildable"] = spur.Buildable, ["runs"] = spur.Buildable ? RoadRuns(spur.FromX, spur.FromZ, spur.ToX, spur.ToZ, 0f, 2) : new Godot.Collections.Array<Godot.Collections.Dictionary>(),
            });
        return new Godot.Collections.Dictionary { ["x0"] = join.X0, ["z0"] = join.Z0, ["x1"] = join.X1, ["z1"] = join.Z1, ["spurs"] = spurs };
    }

    /// <summary>One stretch of a planned road for the preview: what carries it, the road surface and the ground under it at the same points, how far the earthworks reach from its middle at each, and the road height above the ground there (positive is fill).</summary>
    sealed record RunData(RoadStructure Kind, Vector3[] Road, Vector3[] Ground, float[] Half, float[] Height);

    List<RunData> RoadRunData(float x0, float z0, float x1, float z1, float sweep, RoadType type)
    {
        var runs = new List<RunData>();
        var plan = Sim.Network.PlannedRuns(x0, z0, x1, z1, sweep);
        if (plan == null) return runs;
        var g = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        float half = RoadNetwork.HalfWidth(type), slope = Sim.Network.Tuning.Roads.EarthworkSlope;
        for (int i = 0; i + 1 < plan.Count; i++)
        {
            var a = plan[i];
            var b = plan[i + 1];
            int steps = Math.Max(1, (int)MathF.Ceiling((b.Along - a.Along) / 4f));
            var road = new Vector3[steps + 1];
            var ground = new Vector3[steps + 1];
            var reach = new float[steps + 1];
            var height = new float[steps + 1];
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                var (x, z) = g.PointAt(a.Along + (b.Along - a.Along) * t);
                float y = a.Y + (b.Y - a.Y) * t, ground0 = HeightAt(x, z);
                road[k] = new Vector3(x, y, z);
                ground[k] = new Vector3(x, ground0, z);
                height[k] = y - ground0;
                reach[k] = half + (a.After == RoadStructure.None ? slope * MathF.Abs(height[k]) : 0f);
            }
            runs.Add(new RunData(a.After, road, ground, reach, height));
        }
        return runs;
    }

    static void Extremes(List<RunData> runs, ref float maxFill, ref float maxCut)
    {
        foreach (var run in runs)
        {
            if (run.Kind != RoadStructure.None) continue;
            foreach (float h in run.Height) { maxFill = MathF.Max(maxFill, h); maxCut = MathF.Max(maxCut, -h); }
        }
    }

    /// <summary>
    /// A planned road as stretches for the build preview (M6e): each {kind (0 ground, 1 bridge, 2 tunnel), road (the surface), ground (the terrain under it), half (how far the earthworks reach from the
    /// middle at each point), height (the road above the terrain there; positive is fill, negative is cut)}. Empty when it cannot be built. The same plan the price and the build use.
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> RoadRuns(float x0, float z0, float x1, float z1, float sweep, int type)
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var run in RoadRunData(x0, z0, x1, z1, sweep, RoadTypeOf(type)))
            list.Add(new Godot.Collections.Dictionary { ["kind"] = (int)run.Kind, ["road"] = run.Road, ["ground"] = run.Ground, ["half"] = run.Half, ["height"] = run.Height });
        return list;
    }

    /// <summary>What an access point belongs to, for the build preview: the industry's type or the gateway's name.</summary>
    string AccessName(AccessRef target) => target.Depot ? $"Depot {target.Id + 1}" : target.Gateway ? Sim.Gateways[target.Id].Name : Sim.IndustryTuning.Types[Sim.Industries[target.Id].Type].Name;

    /// <summary>Points along a road bending by <paramref name="sweep"/> radians, lying on the ground (for the build preview): at most 601, and just the two ends for an arc of more than 20 km.</summary>
    public Vector3[] CurvePoints(float x0, float z0, float x1, float z1, float sweep)
    {
        var g = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        // A third point almost on the line of the first two makes an arc of a nearly whole circle of a huge radius: its length in the hundreds of millions of metres would be as many points.
        if (!(g.Length < 20000f))
            return new[] { new Vector3(x0, HeightAt(x0, z0), z0), new Vector3(x1, HeightAt(x1, z1), z1) };
        int n = Math.Clamp((int)MathF.Ceiling(g.Length / 4f), 2, 600);
        var points = new Vector3[n + 1];
        for (int i = 0; i <= n; i++)
        {
            var (x, z) = g.PointAt(g.Length * i / n);
            points[i] = new Vector3(x, HeightAt(x, z), z);
        }
        return points;
    }

    static RoadType RoadTypeOf(int type) => type switch { 1 => RoadType.Avenue2x2, 2 => RoadType.Path, _ => RoadType.TwoLane };

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

    /// <summary>
    /// Builds a depot where it is put (<see cref="Simulation.PlanDepot"/>): against a road, with a Path to one within reach, or alone and not connected until a road
    /// reaches it. With <paramref name="free"/> (the snap off or the depot turned by hand) it stands exactly there at <paramref name="angle"/> (NaN: toward the nearest road or town);
    /// <paramref name="raise"/> lifts the floor of its levelled pad. False when it cannot be built or paid for.
    /// </summary>
    public bool BuildDepot(float x, float z, int level, bool free, float angle, float raise)
    {
        if (Sim.Execute(new BuildDepotCommand(x, z, (DepotLevel)Math.Clamp(level, 0, 2), free, angle, raise)) < 0) return false;
        RebuildRoads();
        return true;
    }

    /// <summary>Adds to a preview the cells its pad changes, for the green (fill) and orange (cut) squares: pad_cells (x, ground height, z), pad_deltas (metres, negative for a cut) and cell (the side of a square).</summary>
    void AddPadCells(Godot.Collections.Dictionary preview, PadPlan pad)
    {
        var cells = Sim.PadCells(pad);
        var position = new Vector3[cells.Count];
        var delta = new float[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            position[i] = new Vector3(cells[i].X, HeightAt(cells[i].X, cells[i].Z), cells[i].Z);
            delta[i] = cells[i].Delta;
        }
        preview["pad_cells"] = position;
        preview["pad_deltas"] = delta;
        preview["cell"] = _terrain.CellSize;
    }

    /// <summary>How many metres one press of the raise and lower keys moves the floor of a building being placed.</summary>
    public float PadRaiseStep() => Sim.PadRaiseStep;

    /// <summary>
    /// What a depot clicked for at (x, z) would be, for the build preview: {valid, why (when not), connection (0 alone, 1 against a road, 2 with a Path), x, z (its middle, on the
    /// ground), y (the floor of its pad), angle, size (its box), price (the depot's), path_money, path_length, path_buildable, path_runs (as RoadRuns), pad_money, pad_cut and pad_fill
    /// (cubic metres), pad_max_cut and pad_max_fill (metres), affordable}. A depot refused for its pad still has its place, angle, size and pad figures, to be drawn red.
    /// </summary>
    public Godot.Collections.Dictionary DepotPreview(float x, float z, int level, bool free, float angle, float raise)
    {
        var plan = Sim.PlanDepot(x, z, free, angle, raise);
        var lvl = (DepotLevel)Math.Clamp(level, 0, 2);
        var result = new Godot.Collections.Dictionary
        {
            ["valid"] = plan.Valid, ["why"] = plan.Reason, ["x"] = plan.X, ["z"] = plan.Z, ["angle"] = plan.Angle, ["size"] = _whitebox.Depots[(int)lvl].Size,
            ["y"] = plan.Pad.Reason == null ? HeightAt(plan.X, plan.Z) : plan.Pad.Height,
            ["pad_money"] = plan.PadMoney, ["pad_cut"] = plan.Pad.Cut, ["pad_fill"] = plan.Pad.Fill, ["pad_max_cut"] = plan.Pad.MaxCut, ["pad_max_fill"] = plan.Pad.MaxFill,
            ["pad_height"] = plan.Pad.Height, ["pad_step"] = Sim.PadRaiseStep,
        };
        AddPadCells(result, plan.Pad);
        if (!plan.Valid) return result;
        long price = Sim.DepotPrice(false, lvl);
        bool path = plan.Connection == DepotConnection.Path;
        result["connection"] = (int)plan.Connection;
        result["price"] = price;
        result["path_money"] = plan.PathMoney;
        result["path_length"] = plan.PathCost?.Length ?? 0f;
        result["path_buildable"] = plan.PathBuildable;
        result["path_runs"] = path && plan.PathBuildable ? RoadRuns(plan.PathFromX, plan.PathFromZ, plan.AccessX, plan.AccessZ, 0f, 2) : new Godot.Collections.Array<Godot.Collections.Dictionary>();
        result["path_points"] = path && !plan.PathBuildable ? CurvePoints(plan.PathFromX, plan.PathFromZ, plan.AccessX, plan.AccessZ, 0f) : System.Array.Empty<Vector3>();
        result["affordable"] = Sim.CanAfford(price + plan.PathMoney + plan.PadMoney);
        return result;
    }

    // ---- Company, stations and lines ----------------------------------------------------------------------

    /// <summary>
    /// The company: {money, loan (the ministry's advance), max_loan, net_worth, day, year, month, day_of_month, hour, income, upkeep, interest, allocations
    /// (total so far), imports (total spent), standing, phase, phase_month (months into the campaign), months, country, region, next_allocation,
    /// allocation_factor, months_to_dismissal, dismiss_below, dismiss_months, export_value (this month), expected_value (the phase's), delivered_value, average_satisfaction,
    /// campaign_over, dismissed, finished, grade_name, score, last} where last is the report of the month that ended, {month, export_value,
    /// export_term, deficit_term, fine_term, standing_before, standing_after, allocation}, empty before the first. Also raids and casualties (a number of people), requisitioned (vehicles and trains the army has taken), requisition_income, next_requisition_month (-1 when none), next_requisition_share, expected_requisition (how many it would take now), requisition_rule (0 freight first, 1 the oldest first, 2 passengers first), requisition_rule_text, can_choose_rule (the month of notice), called_up (people, all towns), workforce_gap and workforce_factor (0 to 1), next_call_month (-1 when no more), orders_met, orders_late, orders_failed,
    /// orders_active, orders_open (offers) and orders_income (rewards so far).
    /// </summary>
    public Godot.Collections.Dictionary GetCompany()
    {
        var c = Sim.Company;
        var campaign = Sim.CampaignTuning;
        var last = new Godot.Collections.Dictionary();
        if (Sim.LastMonth is { } m)
            last = new Godot.Collections.Dictionary
            {
                ["month"] = m.Month, ["export_value"] = m.ExportValue, ["export_term"] = m.ExportTerm, ["deficit_term"] = m.DeficitTerm,
                ["fine_term"] = m.FineTerm, ["standing_before"] = m.StandingBefore, ["standing_after"] = m.StandingAfter, ["allocation"] = m.Allocation,
            };
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
            ["income"] = c.Total(LedgerEntry.Income),
            ["upkeep"] = c.Total(LedgerEntry.Upkeep),
            ["interest"] = c.Total(LedgerEntry.Interest),
            ["allocations"] = c.Total(LedgerEntry.Allocation),
            ["imports"] = -c.Total(LedgerEntry.Imports),
            ["standing"] = c.Standing,
            ["standing_history"] = new Godot.Collections.Array(Sim.StandingHistory.Select(v => Variant.From(v))),
            ["start_standing"] = Sim.CampaignTuning.StartStanding,
            ["phase"] = Sim.Phase.Name,
            ["phase_month"] = Sim.MonthIndex,
            ["months"] = campaign.Months,
            ["country"] = campaign.Country,
            ["region"] = campaign.Region,
            ["next_allocation"] = Sim.NextAllocation,
            ["allocation_factor"] = campaign.AllocationFactor(c.Standing),
            ["months_to_dismissal"] = Sim.MonthsToDismissal,
            ["dismiss_below"] = campaign.DismissBelow,
            ["dismiss_months"] = campaign.DismissMonths,
            ["export_value"] = Sim.ExportValueThisMonth,
            ["expected_value"] = Sim.Phase.ExpectedValue,
            ["delivered_value"] = c.DeliveredValue,
            ["average_satisfaction"] = Sim.AverageSatisfaction,
            ["campaign_over"] = c.CampaignOver,
            ["dismissed"] = c.Dismissed,
            ["finished"] = c.Finished,
            ["grade_name"] = c.Grade >= 0 ? campaign.Grade.Names[c.Grade] : "",
            ["score"] = c.Score,
            ["raids"] = Sim.Raids, ["bombs"] = Sim.Bombs, ["casualties"] = Sim.Casualties,
            ["called_up"] = Sim.CalledUp, ["workforce_gap"] = Sim.WorkforceGap, ["workforce_factor"] = Sim.WorkforceFactor,
            ["next_call_month"] = Sim.NextWave is { } wave ? wave.Month : -1,
            ["requisitioned"] = c.Requisitioned, ["requisition_income"] = c.Total(LedgerEntry.Requisition),
            ["next_requisition_month"] = Sim.NextRequisition is { } req ? req.Month : -1, ["next_requisition_share"] = Sim.NextRequisition is { } req2 ? req2.Share : 0f,
            ["expected_requisition"] = Sim.ExpectedRequisition,
            ["requisition_rule"] = (int)Sim.RequisitionRule, ["requisition_rule_text"] = Simulation.RuleText(Sim.RequisitionRule), ["can_choose_rule"] = Sim.CanChooseRequisitionRule,
            ["orders_met"] = c.OrdersMet, ["orders_late"] = c.OrdersLate, ["orders_failed"] = c.OrdersFailed,
            ["orders_active"] = Sim.ActiveOrders, ["orders_open"] = Sim.OpenOffers, ["orders_income"] = c.Total(LedgerEntry.Orders),
            ["last"] = last,
        };
    }

    public bool TakeLoan(long amount) => Sim.Execute(new TakeLoanCommand(amount)) == 1;

    public bool RepayLoan(long amount) => Sim.Execute(new RepayLoanCommand(amount)) == 1;

    /// <summary>Where a station clicked at (x, z) would stand: {valid, x, y, z, price, affordable, radius, covered, industries}, the price with bays if asked.</summary>
    public Godot.Collections.Dictionary StationPreview(float x, float z, bool bays)
    {
        if (Sim.QuoteStation(x, z, bays) is not { } quote) return new Godot.Collections.Dictionary { ["valid"] = false };
        return new Godot.Collections.Dictionary
        {
            ["valid"] = true,
            ["x"] = quote.Site.X, ["y"] = quote.Site.Y, ["z"] = quote.Site.Z,
            ["price"] = quote.Price,
            ["affordable"] = quote.Affordable,
            ["radius"] = quote.Radius,
            ["covered"] = quote.Buildings,
            ["industries"] = quote.Industries,
        };
    }

    /// <summary>Builds a station at the node or road point near (x, z), with bays for loading vehicles if asked. Returns its id, -1 when refused.</summary>
    public int BuildStation(float x, float z, bool bays)
    {
        int id = Sim.Execute(new BuildStationCommand(x, z, bays));
        if (id >= 0) RebuildRoads();
        return id;
    }

    /// <summary>A station's place on the ground.</summary>
    public Vector3 StationPosition(int station)
    {
        var s = Sim.Stations[station];
        if (s.Mode == StationMode.Rail) return new Vector3(s.X, Sim.Rail.GetNodeHeight(s.RailNode), s.Z);
        var (x, z) = Sim.Network.GetNode(s.Node);
        return new Vector3(x, Sim.Network.GetNodeHeight(s.Node), z);
    }

    /// <summary>Creates a line through the stations in order. Returns its id, -1 when refused.</summary>
    public int CreateLine(int[] stations) => Sim.Execute(new CreateLineCommand(stations));

    public bool DeleteLine(int line) => Sim.Execute(new DeleteLineCommand(line)) == 1;

    /// <summary>Changes the stations a line serves. False when refused (fewer than two, a closed station, the same twice in a row).</summary>
    public bool EditLine(int line, int[] stations) => Sim.Execute(new EditLineCommand(line, stations)) == 1;

    public bool RenameLine(int line, string name) => Sim.Execute(new RenameLineCommand(line, name)) == 1;

    /// <summary>Gives a line the colour the player picked (alpha is ignored). False when refused.</summary>
    public bool SetLineColor(int line, Color color) =>
        Sim.Execute(new SetLineColorCommand(line, (Mathf.RoundToInt(color.R * 255f) << 16) | (Mathf.RoundToInt(color.G * 255f) << 8) | Mathf.RoundToInt(color.B * 255f))) == 1;

    /// <summary>Names a station; nothing happens when the name is the one it has. False when refused (empty, too long, closed).</summary>
    public bool RenameStation(int station, string name)
    {
        name = name.Trim();
        if (name == StationName(station)) return true;
        return Sim.Execute(new RenameStationCommand(station, name)) == 1;
    }

    /// <summary>Closes a station for good (nothing is refunded). False when it is already closed.</summary>
    public bool CloseStation(int station)
    {
        bool closed = Sim.Execute(new CloseStationCommand(station)) == 1;
        if (closed) RebuildStations();
        return closed;
    }

    /// <summary>Adds bays to a station that has none, for the bay price. False when refused.</summary>
    public bool AddStationBays(int station)
    {
        bool added = Sim.Execute(new AddStationBaysCommand(station)) == 1;
        if (added) RebuildStations();
        return added;
    }

    /// <summary>Sells a company vehicle. False when it is not one in working order.</summary>
    public bool SellVehicle(int vehicle) => Sim.Execute(new SellVehicleCommand(vehicle)) >= 0;

    // Stations are named for the town (or industry) they stand beside and their number there: "Aldermark 2". A name is made once.
    readonly Dictionary<int, string> _stationNames = new();

    /// <summary>The name of a station: the gateway of its mode within 300 m, else the nearest town within 500 m, else the nearest industry within 300 m, followed by its number among the stations named for it.</summary>
    public string StationName(int station)
    {
        if (Sim.Stations[station].Name is { } chosen) return chosen;
        if (Sim.Stations[station].DockKind != 0) return DockName(Sim.Stations[station]);
        for (int s = 0; s <= station; s++)
        {
            if (_stationNames.ContainsKey(s)) continue;
            var (x, z) = Sim.PointOf(Sim.Stations[s]);
            string place = Sim.Stations[s].Mode == StationMode.Rail ? "Rail station" : "Station";
            float best = float.MaxValue;
            var kind = Sim.Stations[s].Mode == StationMode.Rail ? GatewayKind.Rail : GatewayKind.Road;
            foreach (var gateway in Sim.Gateways)
            {
                float d = (gateway.X - x) * (gateway.X - x) + (gateway.Z - z) * (gateway.Z - z);
                if (gateway.Kind == kind && d < best && d < 300f * 300f) { best = d; place = gateway.Name; }
            }
            if (best == float.MaxValue)
                foreach (var town in Sim.Towns)
                {
                    float d = (town.X - x) * (town.X - x) + (town.Z - z) * (town.Z - z);
                    if (d < best && d < 500f * 500f) { best = d; place = town.Name; }
                }
            if (best == float.MaxValue)
                foreach (var industry in Sim.Industries)
                {
                    float d = (industry.X - x) * (industry.X - x) + (industry.Z - z) * (industry.Z - z);
                    if (d < best && d < 300f * 300f) { best = d; place = Sim.IndustryTuning.Types[industry.Type].Name; }
                }
            int number = 1;
            foreach (var (other, name) in _stationNames)
                if (name.StartsWith(place + " ") && int.TryParse(name.AsSpan(place.Length + 1), out _)) number++;
            _stationNames[s] = $"{place} {number}";
        }
        return _stationNames[station];
    }

    /// <summary>The vehicles the company can buy: for each {id, name, price, capacity, upkeep, cargo}.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetVehicleModels()
    {
        var models = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var m in Sim.AvailableModels)
            models.Add(new Godot.Collections.Dictionary { ["id"] = m.Id, ["name"] = m.Name, ["price"] = m.Price, ["capacity"] = m.Capacity, ["upkeep"] = m.UpkeepPerDay, ["cargo"] = m.Cargo, ["from_year"] = m.FromYear });
        return models;
    }

    /// <summary>The names of the models (vehicles and locomotives) that went on sale in a year, for the news.</summary>
    public Godot.Collections.Array<string> GetNewModels(int year)
    {
        var names = new Godot.Collections.Array<string>();
        foreach (var n in Sim.ModelsOfYear(year)) names.Add(n);
        return names;
    }

    /// <summary>
    /// Buys a vehicle of a model for a line at the nearest depot that is connected to the line by road and has room for
    /// it. Returns the vehicle id, -1 when no depot can take it or the company cannot pay.
    /// </summary>
    public int BuyVehicle(string model, int line) => Sim.Execute(new BuyVehicleCommand(BuyVehicleCommand.NearestDepot, model, line));

    /// <summary>The open lines: for each {id, name, color, stations (names), stop_ids, income, upkeep, profit, delivered, vehicles, loading}.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetLines()
    {
        var vehicles = new int[Sim.Lines.Count];
        var loading = new int[Sim.Lines.Count];
        for (int v = 0; v < Sim.VehicleCount; v++)
        {
            if (!Sim.IsCompanyVehicle(v)) continue;
            if (Sim.IsReturning(v)) continue;
            var info = Sim.GetFleetInfo(v);
            vehicles[info.Line]++;
            if (info.Loading) loading[info.Line]++;
        }
        foreach (var train in Sim.Trains)
        {
            if (!train.Active || train.ReturnDepot >= 0) continue;
            vehicles[train.Line]++;
            if (train.Status == TrainStatus.AtStation) loading[train.Line]++;
        }
        var lines = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var line in Sim.Lines)
        {
            if (!line.Active) continue;
            var names = new Godot.Collections.Array<string>();
            var ids = new Godot.Collections.Array<int>();
            foreach (int s in line.Stops) { names.Add(StationName(s)); ids.Add(s); }
            lines.Add(new Godot.Collections.Dictionary
            {
                ["id"] = line.Id,
                ["name"] = line.Name,
                ["color"] = LineColor(line.Id),
                ["stations"] = names,
                ["stop_ids"] = ids,
                ["stop_options"] = StopOptionsOf(line),
                ["rail"] = line.Mode == StationMode.Rail,
                ["income"] = line.Income,
                ["upkeep"] = line.Upkeep,
                ["profit"] = line.Profit,
                ["delivered"] = line.Delivered,
                ["vehicles"] = vehicles[line.Id],
                ["loading"] = loading[line.Id],
                ["policy"] = PolicyDictionary(line.Policy),
                ["policy_text"] = PolicyText(line.Policy),
            });
        }
        return lines;
    }

    /// <summary>Whether the company could pay this much now.</summary>
    public bool CanAfford(float amount) => Sim.CanAfford((long)Math.Ceiling(amount));

    /// <summary>Where a road vehicle is now, for the camera that follows it: {found, x, y, z}.</summary>
    public Godot.Collections.Dictionary VehicleSpot(int v)
    {
        if (v < 0 || v >= Sim.VehicleCount || !Sim.IsCompanyVehicle(v)) return new Godot.Collections.Dictionary { ["found"] = false };
        var state = Sim.GetVehicle(v);
        float pos = Math.Min(state.Position, _laneLength[state.Lane.Value]);
        LaneFrame(state.Lane.Value, pos, out float x, out float z, out _, out _);
        return new Godot.Collections.Dictionary { ["found"] = true, ["x"] = x, ["y"] = _laneY[state.Lane.Value] + _laneGrade[state.Lane.Value] * pos, ["z"] = z };
    }

    /// <summary>
    /// The company's vehicles: for each {id, model, line, line_name, color, load, capacity, cargo, income, upkeep, profit,
    /// state, x, y, z} where state is "Wrecked", "Loading at ..." or "Heading for ...".
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetVehicles()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        for (int v = 0; v < Sim.VehicleCount; v++)
        {
            if (!Sim.IsCompanyVehicle(v)) continue;
            var state = Sim.GetVehicle(v);
            var info = Sim.GetFleetInfo(v);
            var model = Sim.VehicleModels[info.Model];
            bool wrecked = state.Kind != VehicleKind.Car;
            int target = Sim.TargetStationOf(v);
            string where = target < 0 ? "" : StationName(target);
            var phase = Sim.RepairPhaseOf(v);
            string what = wrecked ? "Wrecked, waiting for a crew"
                : phase is RepairPhase.ToDepot or RepairPhase.Waiting or RepairPhase.Working ? PhaseText(phase, Sim.RepairOf(v))
                : Sim.IsReturning(v) ? "Going to a depot to wait there" : Sim.IsInDepot(v) ? $"Waiting in the depot to leave for {where}" : Sim.IsGhost(v) ? $"Leaving the depot for {where}" : info.Loading ? $"Loading at {where}" : $"Heading for {where}";
            float pos = Math.Min(state.Position, _laneLength[state.Lane.Value]);
            LaneFrame(state.Lane.Value, pos, out float x, out float z, out _, out _);
            var entry = new Godot.Collections.Dictionary
            {
                ["id"] = v, ["model"] = model.Name, ["line"] = info.Line, ["line_name"] = Sim.Lines[info.Line].Name, ["color"] = LineColor(info.Line),
                ["load"] = info.Load, ["capacity"] = model.Capacity, ["cargo"] = info.Cargo >= 0 ? Sim.CargoTypes[info.Cargo].Name : "",
                ["income"] = Sim.IncomeOf(v), ["upkeep"] = Sim.UpkeepPaidFor(v), ["profit"] = Sim.IncomeOf(v) - Sim.UpkeepPaidFor(v),
                ["state"] = what, ["wrecked"] = wrecked, ["x"] = x, ["y"] = _laneY[state.Lane.Value] + _laneGrade[state.Lane.Value] * pos, ["z"] = z,
            };
            AddMaintenance(entry, v);
            list.Add(entry);
        }
        foreach (var train in GetTrains()) list.Add(train);
        return list;
    }

    /// <summary>
    /// Open stations: for each {id, name, x, y, z, waiting, accepts, waiting_for, lines, homes, shops, workplaces, industries,
    /// boarded, alighted, gave_up, boarded_today, alighted_today, gave_up_today} where waiting is the units of each cargo and accepts
    /// whether the station takes it, in the order of <see cref="GetCargoNames"/>, waiting_for the people queueing for a ride as {name, count}
    /// by the station they are going to, lines the {name, color} of the lines that stop here, and the counts of homes, shops, other workplaces (and reached, share: the buildings outside its circle that its streets reach and their coverage added up)
    /// and industries it covers; the people counts are for the day before, and for today so far.
    /// </summary>
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
            var waitingFor = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            foreach (var (dest, count) in station.WaitingFor())
                waitingFor.Add(new Godot.Collections.Dictionary { ["name"] = StationName(dest), ["count"] = count });
            var lines = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            foreach (var line in Sim.Lines)
                if (line.Active && line.Stops.Contains(station.Id))
                    lines.Add(new Godot.Collections.Dictionary { ["name"] = line.Name, ["color"] = LineColor(line.Id) });
            int homes = 0, shops = 0, workplaces = 0;
            foreach (int b in Sim.CoveredBy(station))
                switch (Sim.Buildings[b].Type)
                {
                    case BuildingType.Home: homes++; break;
                    case BuildingType.Commerce: shops++; break;
                    default: workplaces++; break;
                }
            var p = StationPosition(station.Id);
            stations.Add(new Godot.Collections.Dictionary
            {
                ["id"] = station.Id, ["name"] = StationName(station.Id), ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z, ["rail"] = station.Mode == StationMode.Rail, ["bays"] = station.HasBays, ["in_bay"] = station.InBay, ["bay_price"] = Sim.StationBayPrice,
                ["waiting"] = waiting, ["accepts"] = accepts, ["waiting_for"] = waitingFor, ["lines"] = lines,
                ["homes"] = homes, ["shops"] = shops, ["workplaces"] = workplaces, ["industries"] = Sim.IndustriesCoveredBy(station).Count,
                ["reached"] = Sim.ReachOf(station).Count, ["reached_share"] = Sim.ReachOf(station).Sum(r => r.Coverage),
                ["shut_days"] = Math.Max(0f, (station.ShutUntil - Sim.Tick) * Simulation.StepSeconds / Sim.DayLengthSeconds), ["boarded"] = station.Yesterday.Boarded, ["alighted"] = station.Yesterday.Alighted, ["gave_up"] = station.Yesterday.GaveUp,
                ["boarded_today"] = station.Today.Boarded, ["alighted_today"] = station.Today.Alighted, ["gave_up_today"] = station.Today.GaveUp,
            });
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
    /// The industries: for each {id, name, x, y, z, output (cargo name), stock (waiting to be collected), max_output, produced,
    /// deposit (a scarce resource), demand and supply (megawatts), load (a plant's share of full burn), jobs, staffing (0 to 1), staff (people who reached it to work on the last day that closed), inputs, used_by, status, status_kind,
    /// battery (an anti-aircraft battery), range (metres, of a battery), fire_seconds (seconds of fire the ammunition in stock is good for, a battery's)} where inputs lists {cargo, stock, max, imported} for the
    /// cargo it uses (imported: no industry makes it, it comes through a gateway), used_by says who takes the output, status is a line
    /// of text and status_kind is "ok", "warn" or "idle".
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
                int c = CargoIndex(input.Cargo);
                inputs.Add(new Godot.Collections.Dictionary { ["cargo"] = Sim.CargoTypes[c].Name, ["stock"] = industry.Input[c], ["max"] = type.MaxInput, ["imported"] = Sim.IsImported(c) });
            }
            var (status, kind) = DescribeStatus(Sim.StatusOf(industry));
            float staffing = Sim.StaffingOf(industry);
            if (type.Jobs > 0 && staffing < 0.995f && kind == "ok") (status, kind) = ($"Working at {staffing:P0}: only {Sim.StaffOf(industry)} of {type.Jobs} jobs were filled yesterday (give the towns a road or a line to it)", "warn");
            list.Add(new Godot.Collections.Dictionary
            {
                ["id"] = industry.Id, ["name"] = type.Name, ["x"] = industry.X, ["y"] = HeightAt(industry.X, industry.Z), ["z"] = industry.Z,
                ["output"] = type.Supply > 0f ? "Electricity" : Sim.OutputOf(industry.Type) < 0 ? "Nothing" : Sim.CargoTypes[Sim.OutputOf(industry.Type)].Name, ["stock"] = industry.Output,
                ["demand"] = type.Demand, ["supply"] = type.Supply, ["load"] = industry.Load,
                ["jobs"] = type.Jobs, ["staffing"] = staffing, ["staff"] = Sim.StaffOf(industry), ["max_output"] = type.MaxOutput,
                ["produced"] = industry.Produced, ["deposit"] = type.Deposit, ["inputs"] = inputs, ["used_by"] = UsedBy(type),
                ["status"] = status, ["status_kind"] = kind,
                ["battery"] = Sim.IsBattery(industry), ["range"] = Sim.AntiAircraft.Range,
                ["fire_seconds"] = Sim.IsBattery(industry) ? industry.Input[Sim.AmmunitionCargo] / Sim.AntiAircraft.AmmoPerSalvo : 0f,
            });
        }
        return list;
    }

    int CargoIndex(string id)
    {
        for (int k = 0; k < Sim.CargoTypes.Count; k++) if (Sim.CargoTypes[k].Id == id) return k;
        return -1;
    }

    /// <summary>Who takes an industry type's output: the industry types that list it as an input, each marked when none stands on the map yet.</summary>
    string UsedBy(IndustryTypeTuning type)
    {
        var users = new List<string>();
        foreach (var other in Sim.IndustryTuning.Types)
        {
            if (!other.Inputs.Any(i => i.Cargo == type.Output)) continue;
            bool exists = Sim.Industries.Any(i => Sim.IndustryTuning.Types[i.Type].Id == other.Id);
            users.Add(exists ? other.Name : other.Name + " (none yet)");
        }
        return string.Join(", ", users);
    }

    (string Text, string Kind) DescribeStatus(IndustryStatus status)
    {
        if (status.Damaged) return ($"Bombed: out of work for {status.DamagedTicksLeft * Simulation.StepSeconds / Sim.DayLengthSeconds:0.#} more days", "warn");
        if (status.MissingInputs.Count > 0)
        {
            var names = status.MissingInputs.Select(c => Sim.CargoTypes[c].Name.ToLowerInvariant() + (Sim.IsImported(c) ? " (imported)" : ""));
            return ($"Idle: waiting for {string.Join(", ", names)}", "idle");
        }
        if (status.OutputFull) return ("Stopped: its output is full, collect it", "warn");
        if (!status.Covered) return ("No station covers it", "warn");
        return ("Working", "ok");
    }

    /// <summary>
    /// The gateways: for each {id, name, kind ("Road" or "Rail"), x, y, z, covered (a station of its mode reaches it), shut_days (days a raid keeps it shut, 0 when open), offers, exports}
    /// where offers lists {cargo (id), name, units, price, rationed (it costs more or comes in smaller shipments in this phase of the war), stock (waiting at the gateway), imported (so far), status, can (a shipment can be
    /// asked for now), seconds (until the shipment arrives, 0 when none is on its way)} for each import and exports lists {name, units}
    /// for each cargo delivered here so far.
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetGateways()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var gateway in Sim.Gateways)
        {
            var offers = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            foreach (var offer in Sim.ImportOffers)
            {
                int c = CargoIndex(offer.Cargo);
                string? why = Sim.WhyNotImport(gateway.Id, offer.Cargo);
                float seconds = gateway.ArrivesAt[c] >= 0 ? (gateway.ArrivesAt[c] - Sim.Tick) * Simulation.StepSeconds : 0f;
                offers.Add(new Godot.Collections.Dictionary
                {
                    ["cargo"] = offer.Cargo, ["name"] = Sim.CargoTypes[c].Name, ["units"] = Sim.ImportUnits(offer), ["price"] = Sim.ImportPrice(offer),
                    ["rationed"] = Sim.RationingPriceFactor(offer.Cargo) > 1.001f || Sim.RationingUnitsFactor(offer.Cargo) < 0.999f,
                    ["stock"] = gateway.Stock[c], ["imported"] = gateway.Imported[c], ["seconds"] = seconds,
                    ["status"] = gateway.ArrivesAt[c] >= 0 ? "On its way" : why ?? "Ready", ["can"] = why == null,
                });
            }
            var exports = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            for (int c = 0; c < Sim.CargoTypes.Count; c++)
                if (gateway.Exported[c] > 0.0)
                    exports.Add(new Godot.Collections.Dictionary { ["name"] = Sim.CargoTypes[c].Name, ["units"] = gateway.Exported[c] });
            list.Add(new Godot.Collections.Dictionary
            {
                ["id"] = gateway.Id, ["name"] = gateway.Name, ["kind"] = gateway.Kind.ToString(), ["x"] = gateway.X, ["y"] = HeightAt(gateway.X, gateway.Z), ["z"] = gateway.Z,
                ["covered"] = Sim.IsCovered(gateway), ["offers"] = offers, ["exports"] = exports,
                ["shut_days"] = Math.Max(0f, (gateway.DamagedUntil - Sim.Tick) * Simulation.StepSeconds / Sim.DayLengthSeconds),
            });
        }
        return list;
    }

    /// <summary>Asks the government for a shipment of a cargo (its id) at a gateway; false when refused, and <see cref="WhyNotImport"/> says why.</summary>
    public bool RequestImport(int gateway, string cargo) => Sim.Execute(new RequestImportCommand(gateway, cargo)) == 1;

    /// <summary>Why a shipment cannot be asked for now, an empty string when it can.</summary>
    public string WhyNotImport(int gateway, string cargo) => Sim.WhyNotImport(gateway, cargo) ?? "";

    /// <summary>
    /// The towns: for each {id, name, x, y, z, satisfaction, population (what its standing buildings have room for), residents (the people of its trip groups, fewer after raids), capacity (the share of its buildings' nominal room that the call-up has left, 0 to 1; population and jobs are already scaled by it), casualties (killed in raids so far), jobs, homes, commerce, industry, inactive,
    /// abandoned, unmet, walked, drove, rode, stayed, connectivity, min_connectivity, min_satisfaction, grew_sizes, grew_buildings, grew_blocks, blocked, buildings, covered, needs} (counts of buildings, inactive and abandoned ones apart; unmet is the share
    /// of last month's needs that were not met, 0 to 1; the last four are people-trips of the day before by foot, by car, on the
    /// company's lines, and trips that could not be made; buildings standing and covered is how many of those a station of the company
    /// covers; needs is a list of {cargo, short, monthly, construction}: for each cargo its buildings use or build with, how many buildings
    /// are short of this month's need, the units that need lacks, and the units of construction stock missing for growth).
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetTowns()
    {
        var towns = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        var residents = Sim.ResidentsByTown();
        foreach (var town in Sim.Towns)
        {
            var s = Sim.StatsOf(town.Id);
            var needs = Sim.NeedsOf(town.Id);
            var needList = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            foreach (var n in needs.Cargo)
                needList.Add(new Godot.Collections.Dictionary { ["cargo"] = n.Cargo, ["short"] = n.ShortBuildings, ["monthly"] = n.MissingMonthly, ["construction"] = n.MissingConstruction });
            towns.Add(new Godot.Collections.Dictionary
            {
                ["id"] = town.Id, ["name"] = town.Name, ["x"] = town.X, ["y"] = HeightAt(town.X, town.Z), ["z"] = town.Z,
                ["satisfaction"] = town.Satisfaction,
                ["population"] = s.Population, ["residents"] = residents[town.Id], ["capacity"] = town.CapacityFactor, ["casualties"] = town.Casualties, ["jobs"] = s.Jobs,
                ["homes"] = s.Homes, ["commerce"] = s.Commerce, ["industry"] = s.Industry,
                ["inactive"] = s.Inactive, ["abandoned"] = s.Abandoned, ["unmet"] = town.UnmetShare,
                ["walked"] = town.TripsYesterday[(int)TripMode.Walk], ["drove"] = town.TripsYesterday[(int)TripMode.Car],
                ["rode"] = town.TripsYesterday[(int)TripMode.Transit], ["stayed"] = town.TripsYesterday[(int)TripMode.None],
                ["connectivity"] = town.Connectivity, ["min_connectivity"] = Sim.TownTuning.Growth.MinConnectivity, ["min_satisfaction"] = Sim.TownTuning.Growth.MinSatisfaction,
                ["grew_sizes"] = town.LastGrowth.Upgrades, ["grew_buildings"] = town.LastGrowth.NewBuildings, ["grew_blocks"] = town.LastGrowth.NewBlocks,
                ["blocked"] = town.LastGrowth.Blocked.ToString(),
                ["buildings"] = needs.Buildings, ["covered"] = needs.Covered, ["needs"] = needList,
            });
        }
        return towns;
    }

    /// <summary>
    /// The connectivity view: homes are coloured green when a group living there reached work or leisure last month and red when
    /// none did; everything else is dimmed.
    /// </summary>
    public void SetConnectivityView(bool on)
    {
        if (on) SetOverlay((int)MapOverlay.Connectivity);
        else if (_overlay == MapOverlay.Connectivity) SetOverlay((int)MapOverlay.None);
    }

    /// <summary>Whether the connectivity overlay is the one on show (M6b made it one of the overlays).</summary>
    public bool ConnectivityView => _overlay == MapOverlay.Connectivity;

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
        var incident = Sim.GetIncident(Sim.OpenIncidentIds[0]);
        int lane = incident.Lane.Value;
        LaneFrame(lane, incident.Position, out float x, out float z, out _, out _);
        return new Vector3(x, _laneY[lane] + _laneGrade[lane] * incident.Position, z);
    }

    public string FirstOpenIncidentState() => Sim.OpenIncidentIds.Count == 0 ? "none" : Sim.GetIncident(Sim.OpenIncidentIds[0]).State.ToString();

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
        return new Godot.Collections.Dictionary
        {
            ["vehicles"] = cars,
            ["trains"] = Sim.Trains.Count(t => t.Active),
            ["wrecks"] = wrecks,
            ["overtaking"] = overtaking,
            ["mean_speed"] = cars > 0 ? speed / cars : 0.0,
            ["incidents"] = Sim.IncidentCount,
            ["open_incidents"] = Sim.OpenIncidentIds.Count,
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

        // Towns build streets as they grow, inside a step: draw the new roads before any vehicle on them.
        if (Sim.Network.Version != _networkVersion) RebuildRoads();
        else if (Sim.BuildingsVersion != _buildingsVersion) RebuildBuildings();
        float fraction = _speed > 0f ? (float)(_accumulator / Simulation.StepSeconds) : 0f;
        DrawVehicles(fraction);
        UpdateRaidEffects(delta, fraction);
        DrawCrews(fraction);
        DrawRail(fraction);
        UpdateOverlay(delta);
        _waterMaterial.Uv1Offset = new Vector3((float)(Time.GetTicksMsec() * 0.00002), 0f, (float)(Time.GetTicksMsec() * 0.000013)); // the ripples drift
        UpdateTrees();
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

    const float BayOffset = 3.4f; // how far to the right of its lane a vehicle in a station's bay is drawn

    void DrawVehicles(float stepFraction)
    {
        int count = Sim.VehicleCount;
        if (count > _capacity)
        {
            // Doubling keeps the bulk upload small in the common case while allowing wrecks and debris on top.
            while (_capacity < count) _capacity *= 2;
            _buffer = new float[_capacity * FloatsPerInstance];
            _vehicles.InstanceCount = _capacity;
            _markBuffer = new float[_capacity * FloatsPerInstance];
            _marks.InstanceCount = _capacity;
        }
        var buf = _buffer;
        var marks = _markBuffer;
        int markCount = 0;
        _roadModelIds ??= Sim.VehicleModels.Select(m => "road." + m.Id).ToArray();
        _vehicleModels.Begin();
        for (int i = 0; i < count; i++)
        {
            int o = i * FloatsPerInstance;
            var v = Sim.GetVehicle(i);
            if (v.Kind == VehicleKind.Removed || Sim.IsInDepot(i))
            {
                Array.Clear(buf, o, FloatsPerInstance);
                continue;
            }

            int lane = v.Lane.Value;
            // Look a fraction of a step ahead so motion is smooth between the 20 Hz simulation steps.
            float length = Sim.LengthOf(i);
            float pos = v.Position + v.Speed * Simulation.StepSeconds * stepFraction - length * 0.5f;
            float side = -Simulation.LaneWidth * v.Lateral + v.Shift;
            if (Sim.InBay(i)) side += BayOffset; // standing in the bay, to the right of the lane
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
            string? modelId = v.Kind == VehicleKind.Wreck ? "made.wreck" : v.Kind == VehicleKind.Debris ? "made.debris" : company ? _roadModelIds[Sim.GetFleetInfo(i).Model] : CarModelIds[(int)(((uint)i * 2654435761u >> 8) % (uint)CarModelIds.Length)];
            if (modelId != null && _vehicleModels.Has(modelId))
            {
                // A Model: it keeps its own colours; the Line colour is the marker on its roof (and a yellow one on a car that is overtaking).
                _vehicleModels.Add(modelId, x, baseY, z, fx, fz, grade);
                Array.Clear(buf, o, FloatsPerInstance);
                if (company || v.Overtake != OvertakePhase.None)
                {
                    int m = markCount++ * FloatsPerInstance;
                    float top = baseY + _vehicleModels.HeightOf(modelId) + 0.1f;
                    marks[m + 0] = fx * tilt;  marks[m + 1] = -fx * rise; marks[m + 2] = -fz; marks[m + 3] = x;
                    marks[m + 4] = rise;       marks[m + 5] = tilt;       marks[m + 6] = 0f;  marks[m + 7] = top;
                    marks[m + 8] = fz * tilt;  marks[m + 9] = -fz * rise; marks[m + 10] = fx; marks[m + 11] = z;
                    Color mark = company ? ConditionTint(LineColor(Sim.GetFleetInfo(i).Line), Sim.ConditionOf(i)) : new Color(1f, 0.9f, 0.2f);
                    marks[m + 12] = mark.R; marks[m + 13] = mark.G; marks[m + 14] = mark.B; marks[m + 15] = 1f;
                }
                continue;
            }
            float y = baseY + 0.8f * tall;
            buf[o + 0] = fx * tilt * length;  buf[o + 1] = -fx * rise * tall; buf[o + 2] = -fz; buf[o + 3] = x;
            buf[o + 4] = rise * length;       buf[o + 5] = tilt * tall;       buf[o + 6] = 0f;  buf[o + 7] = y;
            buf[o + 8] = fz * tilt * length;  buf[o + 9] = -fz * rise * tall; buf[o + 10] = fx; buf[o + 11] = z;

            Color c = v.Kind switch
            {
                VehicleKind.Wreck => new Color(0.8f, 0.1f, 0.08f),
                VehicleKind.Debris => new Color(0.9f, 0.55f, 0.1f),
                _ when company => ConditionTint(LineColor(Sim.GetFleetInfo(i).Line), Sim.ConditionOf(i)),
                _ => v.Overtake != OvertakePhase.None ? new Color(1f, 0.9f, 0.2f) : CarColor(i),
            };
            buf[o + 12] = c.R; buf[o + 13] = c.G; buf[o + 14] = c.B; buf[o + 15] = 1f;
        }
        _vehicles.Buffer = buf;
        _vehicles.VisibleInstanceCount = count;
        _vehicleModels.End();
        _marks.Buffer = marks;
        _marks.VisibleInstanceCount = markCount;
    }

    /// <summary>Rescue crews as blue boxes: driving from their depot to the site, then parked beside the wreck while they work.</summary>
    void DrawCrews(float stepFraction)
    {
        var buf = _crewBuffer;
        int n = 0;
        var net = Sim.Network;
        _crewModels.Begin();
        bool crewModel = _crewModels.Has("crew");
        foreach (int id in Sim.OpenIncidentIds)
        {
            if (n >= CrewCapacity) break;
            var incident = Sim.GetIncident(id);
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

            if (crewModel)
            {
                _crewModels.Add("crew", x, _terrain.HeightAt(x, z) + 0.1f, z, dx, dz, 0f);
                n++;
                continue;
            }
            int o = n * FloatsPerInstance;
            buf[o + 0] = dx;  buf[o + 1] = 0f; buf[o + 2] = -dz; buf[o + 3] = x;
            buf[o + 4] = 0f;  buf[o + 5] = 1f; buf[o + 6] = 0f;  buf[o + 7] = _terrain.HeightAt(x, z) + 1.1f;
            buf[o + 8] = dz;  buf[o + 9] = 0f; buf[o + 10] = dx; buf[o + 11] = z;
            bool working = incident.State == IncidentState.Clearing;
            buf[o + 12] = working ? 1f : 0.1f; buf[o + 13] = working ? 0.85f : 0.4f; buf[o + 14] = working ? 0.1f : 1f; buf[o + 15] = 1f;
            n++;
        }
        _crewModels.End();
        _crews.Buffer = buf;
        _crews.VisibleInstanceCount = crewModel ? 0 : n;
    }

    /// <summary>The colour of a line, shared by its vehicles and the line list: the player's choice, else a strong colour from its number.</summary>
    Color LineColor(int line) =>
        Sim.Lines[line].Color is >= 0 and var rgb ? new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f) : Color.FromHsv(((line * 0.381966f) % 1f), 0.85f, 0.95f);

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
        _networkVersion = net.Version;
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
            bool path = road.Type == RoadType.Path;
            float width = path ? RoadNetwork.HalfWidth(RoadType.Path) * 2f : (avenue ? 4 : 2) * Simulation.LaneWidth + median;
            var surface = path ? _pathMaterial : _roadMaterial;

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
                AddRoadBox(middle, g.Length, y1 - y0, dx, dz, width, RoadHeight, 0f, surface);
                if (!path) AddRoadBox(middle, g.Length, y1 - y0, dx, dz, avenue ? median : 0.25f, 0.04f, RoadHeight * 0.5f + 0.01f, avenue ? _medianMaterial : _lineMaterial);
            }
            else
            {
                AddRoadRibbon(g, y0, y1, width, RoadHeight * 0.5f, surface);
                if (!path) AddRoadRibbon(g, y0, y1, avenue ? median : 0.25f, RoadHeight * 0.5f + 0.05f, avenue ? _medianMaterial : _lineMaterial);
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

        RebuildStations();

        foreach (var depot in Sim.Depots)
        {
            if (!depot.Active && !depot.FreeStanding) continue;
            var block = _whitebox.Depots[(int)depot.Level];
            var material = depot.Active ? _depotLevelMaterials[(int)depot.Level] : _depotIdleMaterial; // grey while no road reaches it
            Color? idle = depot.Active ? null : new Color(0.5f, 0.5f, 0.52f, 0.6f);
            string depotModel = "depot." + (int)depot.Level;
            if (depot.FreeStanding && PlaceModel(_roadRoot, depotModel, new Vector3(depot.X, HeightAt(depot.X, depot.Z), depot.Z), depot.Angle, idle)) continue;
            if (!depot.FreeStanding)
            {
                var (nx, nz) = net.GetNode(depot.Node);
                float ox = block.Offset.X, oz = block.Offset.Y;
                if (PlaceModel(_roadRoot, depotModel, new Vector3(nx + ox, _terrain.HeightAt(nx + ox, nz + oz), nz + oz), MathF.Atan2(-oz, -ox), idle)) continue;
            }
            var building = new MeshInstance3D { Mesh = new BoxMesh { Size = block.Size }, MaterialOverride = material };
            if (depot.FreeStanding)
            {
                building.Position = new Vector3(depot.X, HeightAt(depot.X, depot.Z) + block.Size.Y * 0.5f, depot.Z);
                building.Rotation = new Vector3(0f, -depot.Angle, 0f);
            }
            else
            {
                var (x, z) = net.GetNode(depot.Node);
                building.Position = new Vector3(x + block.Offset.X, _terrain.HeightAt(x + block.Offset.X, z + block.Offset.Y) + block.Size.Y * 0.5f, z + block.Offset.Y);
            }
            _roadRoot.AddChild(building);
        }
        AddAccessMarks();
        RebuildBuildings();
    }

    /// <summary>Draws each open station: a slab and a post on the stop lane of each approach and, with bays, a pocket of paving beside the stop lane that the vehicles pull into.</summary>
    void RebuildStations()
    {
        foreach (var child in _stationRoot.GetChildren()) child.QueueFree();
        var net = Sim.Network;
        foreach (var station in Sim.Stations)
        {
            if (!station.Active || station.Mode != StationMode.Road || station.DockKind != 0) continue; // the stop at an industry has no platform of its own
            var block = _whitebox.Station;
            foreach (var (laneId, position) in Sim.StopLanes(station))
            {
                // The stop is on the road: a slab on the lane where the vehicles halt and a post at its right edge.
                int lane = laneId.Value;
                float s = Math.Max(0f, position - block.Size.X);
                LaneFrame(lane, s + block.Size.X * 0.5f, out float cx, out float cz, out float dx, out float dz);
                float tilt = 1f / MathF.Sqrt(1f + _laneGrade[lane] * _laneGrade[lane]);
                var along = new Vector3(dx * tilt, _laneGrade[lane] * tilt, dz * tilt);
                var across = new Vector3(-dz, 0f, dx);
                var up = across.Cross(along);
                var centre = new Vector3(cx - dz * _laneSide[lane], _laneY[lane] + _laneGrade[lane] * (s + block.Size.X * 0.5f) + RoadHeight + block.Size.Y * 0.5f, cz + dx * _laneSide[lane]);
                var slab = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(block.Size.X, block.Size.Y, block.Size.Z) }, MaterialOverride = _stationMaterial };
                slab.Transform = new Transform3D(new Basis(along, up, across), centre);
                _stationRoot.AddChild(slab);
                var stopSpot = new Vector3(centre.X - dz * (block.Size.Z * 0.5f + 0.6f), centre.Y - block.Size.Y * 0.5f, centre.Z + dx * (block.Size.Z * 0.5f + 0.6f));
                if (PlaceModelAlong(_stationRoot, "made.bus-stop", stopSpot, MathF.Atan2(dz, dx) + MathF.PI) != null) continue; // the shelter opens toward the lane, on the post's left
                var post = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, _whitebox.StationPostHeight, 0.5f) }, MaterialOverride = _stationMaterial };
                post.Position = new Vector3(centre.X - dz * (block.Size.Z * 0.5f + 0.6f), centre.Y + _whitebox.StationPostHeight * 0.5f, centre.Z + dx * (block.Size.Z * 0.5f + 0.6f));
                _stationRoot.AddChild(post);
            }
            foreach (var (laneId, position) in Sim.BayLanes(station))
            {
                int lane = laneId.Value;
                float length = Sim.BayLength;
                float s = Math.Max(0f, position - length * 0.5f);
                LaneFrame(lane, s, out float cx, out float cz, out float dx, out float dz);
                float side = _laneSide[lane] + BayOffset;
                var centre = new Vector3(cx - dz * side, _laneY[lane] + _laneGrade[lane] * s + 0.01f, cz + dx * side);
                float tilt = 1f / MathF.Sqrt(1f + _laneGrade[lane] * _laneGrade[lane]);
                var along = new Vector3(dx * tilt, _laneGrade[lane] * tilt, dz * tilt);
                var across = new Vector3(-dz, 0f, dx);
                var up = across.Cross(along);
                var pocket = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(length, RoadHeight, Simulation.LaneWidth) }, MaterialOverride = _roadMaterial };
                pocket.Transform = new Transform3D(new Basis(along, up, across), centre);
                _stationRoot.AddChild(pocket);
            }
        }
    }

    static readonly Color ReachedColour = new(0.25f, 0.85f, 0.35f), CutOffColour = new(0.92f, 0.22f, 0.20f), DimmedColour = new(0.30f, 0.32f, 0.36f);

    Color GatewayRoadColour => _whitebox.GatewayRoad;
    Color GatewayRailColour => _whitebox.GatewayRail;

    Color[] BuildingColors => _whitebox.Buildings; // home, commerce, industry

    /// <summary>
    /// The marks of every access point (M6e): a stone pad where the road reaches it, a post to find it by, and a dark door on the wall of the industry or gateway
    /// that faces it. They stand where the simulation puts the point, so the connection that leads to it ends at the pad.
    /// </summary>
    void AddAccessMarks()
    {
        foreach (var industry in Sim.Industries)
            AddAccessMark(industry.X, industry.Z, industry.AccessX, industry.AccessZ);
        foreach (var gateway in Sim.Gateways)
            if (gateway.Kind == GatewayKind.Road) AddAccessMark(gateway.X, gateway.Z, gateway.AccessX, gateway.AccessZ);
        foreach (var depot in Sim.Depots)
            if (depot.FreeStanding) AddAccessMark(depot.X, depot.Z, depot.AccessX, depot.AccessZ);
    }

    void AddAccessMark(float cx, float cz, float ax, float az)
    {
        float dx = ax - cx, dz = az - cz, length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 1e-3f) return;
        dx /= length; dz /= length;
        var pad = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(_whitebox.PadSize, 0.3f, _whitebox.PadSize) }, MaterialOverride = _padMaterial };
        pad.Position = new Vector3(ax, HeightAt(ax, az) + 0.15f, az);
        _roadRoot.AddChild(pad);
        var post = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.6f, _whitebox.MarkerHeight, 0.6f) }, MaterialOverride = _markerMaterial };
        post.Position = new Vector3(ax - dz * _whitebox.PadSize * 0.5f, HeightAt(ax, az) + _whitebox.MarkerHeight * 0.5f, az + dx * _whitebox.PadSize * 0.5f);
        _roadRoot.AddChild(post);
        float wallX = ax - dx * Sim.AccessEdgeMargin, wallZ = az - dz * Sim.AccessEdgeMargin;
        var door = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.8f, _whitebox.DoorHeight, _whitebox.DoorWidth) }, MaterialOverride = _doorMaterial };
        door.Position = new Vector3(wallX, HeightAt(wallX, wallZ) + _whitebox.DoorHeight * 0.5f, wallZ);
        door.Rotation = new Vector3(0f, MathF.Atan2(-dz, dx), 0f); // the box's x axis runs from the building toward the point
        _roadRoot.AddChild(door);
    }

    /// <summary>A composite Model (an industry, a gateway): made once under <paramref name="key"/>, then moved and tinted by an overlay colour (null: none). Models face -Z, turned to <paramref name="facing"/>.</summary>
    void PlaceCompositeNode(int key, float x, float z, float facing, string modelId, Color? overlay)
    {
        if (!_industryNodes.TryGetValue(key, out var node))
        {
            node = _buildingModels.Compose(modelId);
            if (node == null) return;
            _industryRoot.AddChild(node);
            _industryNodes[key] = node;
        }
        node.Position = new Vector3(x, Math.Max(_terrain.HeightAt(x, z), _terrain.WaterSurfaceAt(x, z)) - 0.3f, z);
        node.Rotation = new Vector3(0f, -facing, 0f);
        SetOverlay(node, overlay);
    }

    /// <summary>One industry as its composite Model: dark when bombed, the colour of the map view when one is shown.</summary>
    void PlaceIndustryModel(int index, Industry industry, string modelId, float shade)
    {
        Color? overlay = null;
        if (_overlay != MapOverlay.None) overlay = OverlayIndustryColour(industry, Colors.White) with { A = 0.8f };
        else if (shade < 1f) overlay = new Color(0.05f, 0.05f, 0.06f, 0.65f);
        PlaceCompositeNode(index, industry.X, industry.Z, industry.Angle, modelId, overlay);
    }

    /// <summary>Puts a plain Model of the library under a parent at a ground position, its front toward <paramref name="facing"/> (the direction a box would be turned to). False when there is no such Model.</summary>
    bool PlaceModel(Node3D parent, string modelId, Vector3 position, float facing, Color? overlay = null) => PlaceModelNode(parent, modelId, position, facing, overlay) != null;

    /// <summary>As <see cref="PlaceModel"/>, giving the node (to hang a live lamp on it).</summary>
    Node3D? PlaceModelNode(Node3D parent, string modelId, Vector3 position, float facing, Color? overlay = null)
    {
        if (!_buildingModels.Has(modelId)) return null;
        var node = _buildingModels.Compose(modelId);
        if (node == null) return null;
        node.Position = position;
        node.Rotation = new Vector3(0f, -(facing + MathF.PI * 0.5f), 0f);
        parent.AddChild(node);
        if (overlay != null) SetOverlay(node, overlay);
        return node;
    }

    /// <summary>A plain Model with its length (X) along a heading, for what runs along a track or a lane (a platform, a bus stop).</summary>
    Node3D? PlaceModelAlong(Node3D parent, string modelId, Vector3 position, float heading)
    {
        if (!_buildingModels.Has(modelId)) return null;
        var node = _buildingModels.Compose(modelId);
        if (node == null) return null;
        node.Position = position;
        node.Rotation = new Vector3(0f, -heading, 0f);
        parent.AddChild(node);
        return node;
    }

    /// <summary>A flat colour laid over every mesh below a node (null takes it away).</summary>
    void SetOverlay(Node node, Color? colour)
    {
        Material? material = null;
        if (colour is { } c)
        {
            _smokeMaterial ??= new StandardMaterial3D { Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            material = (Material)_smokeMaterial.Duplicate();
            ((StandardMaterial3D)material).AlbedoColor = c;
        }
        foreach (var child in node.GetChildren())
        {
            if (child is MeshInstance3D mesh) mesh.MaterialOverlay = material;
            SetOverlay(child, colour);
        }
    }

    /// <summary>Redraws every building that stands (one box each, coloured by kind and state) and the town names.</summary>
    void RebuildBuildings()
    {
        _buildingsVersion = Sim.BuildingsVersion;
        var tuning = Sim.TownTuning;
        int n = Sim.Industries.Count + Sim.Gateways.Count;
        foreach (var b in Sim.Buildings) if (b.State != BuildingState.Demolished) n++;
        var buf = new float[n * FloatsPerInstance];
        int k = 0;
        _buildingModels.Begin();
        if (_industryNodesOf != Sim)
        {
            foreach (var node in _industryNodes.Values) node.QueueFree();
            _industryNodes.Clear();
            _industryNodesOf = Sim;
        }
        for (int index = 0; index < Sim.Industries.Count; index++)
        {
            var industry = Sim.Industries[index];
            var type = Sim.IndustryTuning.Types[industry.Type];
            float shade = industry.DamagedUntil > Sim.Tick ? 0.3f : 1f; // bombed: dark until it works again
            string industryModel = "industry." + type.Id;
            if (_buildingModels.HasComposite(industryModel))
            {
                PlaceIndustryModel(index, industry, industryModel, shade);
                continue;
            }
            float side = type.Size * 2f;
            float fx = MathF.Cos(industry.Angle), fz = MathF.Sin(industry.Angle);
            float y = Math.Max(_terrain.HeightAt(industry.X, industry.Z), _terrain.WaterSurfaceAt(industry.X, industry.Z)) - 0.3f + type.Height * 0.5f;
            int o = k++ * FloatsPerInstance;
            buf[o + 0] = fx * side;  buf[o + 1] = 0f;          buf[o + 2] = -fz * side; buf[o + 3] = industry.X;
            buf[o + 4] = 0f;         buf[o + 5] = type.Height; buf[o + 6] = 0f;         buf[o + 7] = y;
            buf[o + 8] = fz * side;  buf[o + 9] = 0f;          buf[o + 10] = fx * side; buf[o + 11] = industry.Z;
            var own = OverlayIndustryColour(industry, new Color(type.Color[0] * shade, type.Color[1] * shade, type.Color[2] * shade));
            buf[o + 12] = own.R; buf[o + 13] = own.G; buf[o + 14] = own.B; buf[o + 15] = 1f;
        }
        for (int g = 0; g < Sim.Gateways.Count; g++)
        {
            var gateway = Sim.Gateways[g];
            string gatewayModel = gateway.Kind == GatewayKind.Road ? "gateway.road" : "gateway.rail";
            if (_buildingModels.HasComposite(gatewayModel))
            {
                Color? shade = _overlay != MapOverlay.None ? DimmedColour with { A = 0.6f } : gateway.DamagedUntil > Sim.Tick ? new Color(0.05f, 0.05f, 0.06f, 0.65f) : null;
                PlaceCompositeNode(-1 - g, gateway.X, gateway.Z, gateway.Angle, gatewayModel, shade);
                continue;
            }
            float side = Sim.GatewayTuning.Size * 2f, height = Sim.GatewayTuning.Height;
            float fx = MathF.Cos(gateway.Angle), fz = MathF.Sin(gateway.Angle);
            float y = Math.Max(_terrain.HeightAt(gateway.X, gateway.Z), _terrain.WaterSurfaceAt(gateway.X, gateway.Z)) - 0.3f + height * 0.5f;
            int o = k++ * FloatsPerInstance;
            buf[o + 0] = fx * side;  buf[o + 1] = 0f;     buf[o + 2] = -fz * side; buf[o + 3] = gateway.X;
            buf[o + 4] = 0f;         buf[o + 5] = height; buf[o + 6] = 0f;         buf[o + 7] = y;
            buf[o + 8] = fz * side;  buf[o + 9] = 0f;     buf[o + 10] = fx * side; buf[o + 11] = gateway.Z;
            var colour = gateway.Kind == GatewayKind.Road ? GatewayRoadColour : GatewayRailColour;
            if (gateway.DamagedUntil > Sim.Tick) colour = colour.Darkened(0.7f);
            if (_overlay != MapOverlay.None) colour = colour.Lerp(DimmedColour, 0.5f);
            buf[o + 12] = colour.R; buf[o + 13] = colour.G; buf[o + 14] = colour.B; buf[o + 15] = 1f;
        }
        foreach (var b in Sim.Buildings)
        {
            if (b.State == BuildingState.Demolished) continue;
            var tier = tuning.Of(b.Type, b.Tier);
            float fx = MathF.Cos(b.Facing), fz = MathF.Sin(b.Facing);
            float ground = Math.Max(_terrain.HeightAt(b.X, b.Z), _terrain.WaterSurfaceAt(b.X, b.Z)) - 0.3f;
            string townModel = TownModelIds[(int)b.Type][b.Tier];
            if (_buildingModels.Has(townModel))
            {
                // A Model keeps its own colours; the colour of the state multiplies them (white leaves them as they are).
                var tint = b.State switch
                {
                    BuildingState.Inactive => Colors.White.Lerp(new Color(0.45f, 0.45f, 0.45f), 0.65f),
                    BuildingState.Abandoned => new Color(0.3f, 0.27f, 0.27f),
                    _ => Colors.White,
                };
                _buildingModels.Add(townModel, b.X, ground, b.Z, fx, fz, 0f, _buildingModels.Pick(townModel, b.Id), OverlayBuildingColour(b, tint));
                continue;
            }
            float side = tier.Size * 2f;
            float y = ground + tier.Height * 0.5f;
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
            colour = OverlayBuildingColour(b, colour);
            buf[o + 12] = colour.R; buf[o + 13] = colour.G; buf[o + 14] = colour.B; buf[o + 15] = 1f;
        }
        _buildingModels.End();
        _buildings.InstanceCount = k;
        if (k > 0) _buildings.Buffer = k == n ? buf : buf[..(k * FloatsPerInstance)];

        foreach (var child in _townLabels.GetChildren()) child.QueueFree();
        foreach (var town in Sim.Towns)
        {
            _townLabels.AddChild(new Label3D
            {
                Text = town.Name,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                PixelSize = 0.0014f * LabelSize * _labelScale,
                FontSize = 36,
                VisibilityRangeEnd = 4500f, VisibilityRangeEndMargin = 450f, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
                OutlineSize = 10,
                NoDepthTest = true,
                Position = new Vector3(town.X, HeightAt(town.X, town.Z) + 70f, town.Z),
            });
        }
        foreach (var gateway in Sim.Gateways)
        {
            _townLabels.AddChild(new Label3D
            {
                Text = gateway.Name,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                PixelSize = 0.0013f * LabelSize * _labelScale,
                FontSize = 34,
                VisibilityRangeEnd = 3000f, VisibilityRangeEndMargin = 300f, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
                OutlineSize = 9,
                NoDepthTest = true,
                Modulate = gateway.Kind == GatewayKind.Road ? GatewayRoadColour.Lightened(0.4f) : GatewayRailColour.Lightened(0.4f),
                Position = new Vector3(gateway.X, HeightAt(gateway.X, gateway.Z) + 45f, gateway.Z),
            });
        }
        foreach (var industry in Sim.Industries)
        {
            _townLabels.AddChild(new Label3D
            {
                Text = Sim.IndustryTuning.Types[industry.Type].Name,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                PixelSize = 0.0011f * LabelSize * _labelScale,
                FontSize = 30,
                VisibilityRangeEnd = 1500f, VisibilityRangeEndMargin = 150f, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
                OutlineSize = 8,
                NoDepthTest = true,
                Modulate = new Color(0.9f, 0.95f, 0.8f),
                Position = new Vector3(industry.X, HeightAt(industry.X, industry.Z) + 40f, industry.Z),
            });
        }
    }

    /// <summary>How big the names in the world are drawn against the interface text: small, because they stay the same size on the screen however far the camera is and would cover the map.</summary>
    const float LabelSize = 0.5f; // and each kind fades out beyond a camera distance (towns 4.5 km, gateways 3 km, industries 1.5 km) so the whole map can be seen without names

    float _labelScale = 1f;

    /// <summary>
    /// The scale of the names in the world (Settings.label_scale): they are as big on the screen as the interface text, whatever the size of the window. Takes effect on the labels
    /// there are and on those made after.
    /// </summary>
    public void SetLabelScale(float scale)
    {
        if (_townLabels == null || Math.Abs(scale - _labelScale) < 1e-4f) { _labelScale = scale; return; }
        float ratio = scale / _labelScale;
        _labelScale = scale;
        foreach (var child in _townLabels.GetChildren())
            if (child is Label3D label) label.PixelSize *= ratio;
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
            _cutaway.Mesh = CutawayMesh();
            foreach (var child in _terrainRoot.GetChildren()) child.QueueFree();
            _terrainChunks = new MeshInstance3D?[side * side];
            _terrain.TakeChangedChunks(); // everything is built below
            for (int c = 0; c < side * side; c++) BuildTerrainChunk(c);
            return;
        }
        foreach (int c in _terrain.TakeChangedChunks()) BuildTerrainChunk(c);
    }

    const float CutawayDepth = 300f; // how far below the sea level the slice of world goes
    static readonly Color TopsoilColour = new Color(0.33f, 0.45f, 0.24f).SrgbToLinear(), SoilColour = new Color(0.42f, 0.31f, 0.21f).SrgbToLinear(),
        RockColour = new Color(0.40f, 0.37f, 0.34f).SrgbToLinear(), DeepRockColour = new Color(0.28f, 0.26f, 0.25f).SrgbToLinear(), CutWaterColour = new Color(0.16f, 0.4f, 0.62f, 0.72f).SrgbToLinear();

    /// <summary>
    /// The world as a slice cut out of the earth: a wall along each edge of the map from the ground down to <see cref="CutawayDepth"/> in layers (topsoil, soil, rock),
    /// the water seen in section where a lake or the sea reaches the edge, and a bottom. Nothing is drawn beyond it.
    /// </summary>
    ArrayMesh CutawayMesh()
    {
        int cells = _terrain.Cells;
        float cell = _terrain.CellSize, half = _terrain.HalfSize, bottom = -CutawayDepth;
        var earth = new SurfaceTool();
        earth.Begin(Mesh.PrimitiveType.Triangles);
        var water = new SurfaceTool();
        water.Begin(Mesh.PrimitiveType.Triangles);
        void Quad(SurfaceTool st, Vector3 a0, Vector3 a1, Vector3 b1, Vector3 b0, Vector3 normal, Color top, Color low)
        {
            // a0 and a1 are the top corners, b0 and b1 the ones below them
            Vector3[] v = { a0, a1, b1, a0, b1, b0 };
            Color[] c = { top, top, low, top, low, low };
            for (int k = 0; k < 6; k++) { st.SetNormal(normal); st.SetColor(c[k]); st.AddVertex(v[k]); }
        }
        // the four edges: along i or j, at the first or the last index, the outward normal
        for (int edge = 0; edge < 4; edge++)
        {
            Vector3 normal = edge switch { 0 => new Vector3(0, 0, -1), 1 => new Vector3(0, 0, 1), 2 => new Vector3(-1, 0, 0), _ => new Vector3(1, 0, 0) };
            for (int k = 0; k < cells; k++)
            {
                (int i, int j) p0 = edge switch { 0 => (k, 0), 1 => (k, cells), 2 => (0, k), _ => (cells, k) };
                (int i, int j) p1 = edge switch { 0 => (k + 1, 0), 1 => (k + 1, cells), 2 => (0, k + 1), _ => (cells, k + 1) };
                float h0 = _terrain.GetSample(p0.i, p0.j), h1 = _terrain.GetSample(p1.i, p1.j);
                float x0 = p0.i * cell - half, z0 = p0.j * cell - half, x1 = p1.i * cell - half, z1 = p1.j * cell - half;
                // layers follow the surface: the thickness of each is measured down from the ground at that end
                float[] depth = { 0f, 1.5f, 14f };
                Color[] shade = { TopsoilColour, SoilColour, RockColour };
                for (int layer = 0; layer < 3; layer++)
                {
                    float top0 = h0 - depth[layer], top1 = h1 - depth[layer];
                    float low0 = layer < 2 ? h0 - depth[layer + 1] : bottom, low1 = layer < 2 ? h1 - depth[layer + 1] : bottom;
                    var lowColour = layer < 2 ? shade[layer + 1].Lerp(shade[layer], 0.5f) : DeepRockColour;
                    Quad(earth, new Vector3(x0, top0, z0), new Vector3(x1, top1, z1), new Vector3(x1, low1, z1), new Vector3(x0, low0, z0), normal, shade[layer], layer == 0 ? shade[0] : lowColour.Lerp(shade[layer], 0.4f));
                }
                float w0 = _terrain.WaterSurfaceAtSample(p0.i, p0.j) - 0.01f + TerrainSink, w1 = _terrain.WaterSurfaceAtSample(p1.i, p1.j) - 0.01f + TerrainSink;
                if (w0 > h0 || w1 > h1)
                    Quad(water, new Vector3(x0, Math.Max(w0, h0), z0), new Vector3(x1, Math.Max(w1, h1), z1), new Vector3(x1, h1, z1), new Vector3(x0, h0, z0), normal, CutWaterColour, CutWaterColour);
            }
        }
        var floor = new[] { new Vector3(-half, bottom, -half), new Vector3(half, bottom, -half), new Vector3(half, bottom, half), new Vector3(-half, bottom, half) };
        foreach (int k in new[] { 0, 1, 2, 0, 2, 3 }) { earth.SetNormal(Vector3.Down); earth.SetColor(DeepRockColour); earth.AddVertex(floor[k]); }
        var mesh = earth.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled });
        var wallWater = water.Commit();
        if (wallWater.GetSurfaceCount() > 0)
        {
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, wallWater.SurfaceGetArrays(0));
            mesh.SurfaceSetMaterial(1, new StandardMaterial3D { VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.2f });
        }
        return mesh;
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
                float surface = _terrain.WaterSurfaceAtSample(i, j);
                bool shore = NearWater(i, j);
                var colour = TerrainColor(height, surface, normals[k].Y, shore).SrgbToLinear(); // vertex colours are read as linear
                float depth = height - surface;
                colour.A = depth < 0f ? 1f : shore && depth < 1.5f ? 1f - depth / 1.5f : 0f; // how much of the ground is sand (the shader takes its grain from it)
                colors[k] = colour;
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
