using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// The rail side of the host (M4): the tracks, signals, level crossings, stations, depots and trains drawn from the rail network and
/// the trains of the simulation, and the calls GDScript uses to build and read them. Static things are made again when the network
/// changes; signals, barriers and trains are updated each frame.
/// </summary>
public partial class SimHost
{
    const float RailLift = 0.12f;        // the ballast bed stands this far above the node height
    const float RailGauge = 0.75f;       // each rail is this far from the middle of the track
    const float BedWidth = 3.6f;
    const int TrainCarCapacity = 256;

    Node3D _railRoot = null!, _signalRoot = null!, _crossingRoot = null!;
    MultiMesh _trainCars = null!;
    MultiMesh _trainMarks = null!; // the Line marker on the roof of a locomotive
    ModelDraw _trainModels = null!;
    string[]? _locomotiveModelIds, _wagonModelIds;
    readonly float[] _trainMarkBuffer = new float[TrainCarCapacity * FloatsPerInstance];
    float[] _trainBuffer = new float[TrainCarCapacity * FloatsPerInstance];
    StandardMaterial3D _bedMaterial = null!, _railMaterial = null!, _railStationMaterial = null!, _railDepotMaterial = null!, _postMaterial = null!;
    Simulation? _railSim;
    long _railSignature = -1;
    double _signalClock;
    readonly List<(StandardMaterial3D Lamp, RailSignal RailSignal)> _signalLamps = new();
    readonly List<(Node3D ArmA, Node3D ArmB, StandardMaterial3D Light, LevelCrossing Crossing)> _crossingParts = new();
    int _warningsShown;

    void ReadyRail()
    {
        _railRoot = new Node3D { Name = "Rail" };
        AddChild(_railRoot);
        _signalRoot = new Node3D { Name = "Signals" };
        AddChild(_signalRoot);
        _crossingRoot = new Node3D { Name = "Crossings" };
        AddChild(_crossingRoot);
        _bedMaterial = Flat(new Color(0.36f, 0.33f, 0.30f));
        _railMaterial = Flat(new Color(0.78f, 0.78f, 0.82f));
        _railStationMaterial = Flat(new Color(0.25f, 0.62f, 0.78f));
        _railDepotMaterial = Flat(_whitebox.RailDepot.Color);
        _postMaterial = Flat(new Color(0.15f, 0.15f, 0.17f));
        _trainCars = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new BoxMesh { Size = new Vector3(1f, 1f, 2.8f) }, // unit length: each instance is stretched to its car
            InstanceCount = TrainCarCapacity,
            VisibleInstanceCount = 0,
        };
        var material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.7f };
        AddChild(new MultiMeshInstance3D { Name = "TrainCars", Multimesh = _trainCars, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _trainMarks = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new BoxMesh { Size = new Vector3(3.0f, 0.16f, 1.4f) },
            InstanceCount = TrainCarCapacity,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D { Name = "TrainMarkers", Multimesh = _trainMarks, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _trainModels = new ModelDraw(this);
    }

    // ---- Building and reading, for GDScript ------------------------------------------------------------------------------

    /// <summary>The rail node near a point, as {snapped, node, x, y, z}.</summary>
    public Godot.Collections.Dictionary SnapToRailNode(float x, float z, float radius)
    {
        int node = Sim.Rail.FindNodeNear(x, z, radius, withTracksOnly: true);
        if (node < 0) return new Godot.Collections.Dictionary { ["snapped"] = false };
        var (nx, nz) = Sim.Rail.GetNode(node);
        return new Godot.Collections.Dictionary { ["snapped"] = true, ["node"] = node, ["x"] = nx, ["y"] = Sim.Rail.GetNodeHeight(node), ["z"] = nz };
    }

    /// <summary>What a track from one point to another would cost: {valid, length, cut, fill, bridge, tunnel, money, affordable, crossings, demolished, demolition_price}.</summary>
    public Godot.Collections.Dictionary EstimateTrackCost(float x0, float z0, float x1, float z1, float sweep)
    {
        if (Sim.QuoteTrack(x0, z0, x1, z1, sweep) is not { } quote)
            return new Godot.Collections.Dictionary { ["valid"] = false, ["reason"] = Sim.WhyNotTrack(x0, z0, x1, z1, sweep) ?? "" };
        return new Godot.Collections.Dictionary
        {
            ["valid"] = true,
            ["length"] = quote.Cost.Length, ["cut"] = quote.Cost.CutVolume, ["fill"] = quote.Cost.FillVolume,
            ["bridge"] = quote.Cost.BridgeLength, ["tunnel"] = quote.Cost.TunnelLength,
            ["money"] = quote.Price, ["affordable"] = quote.Affordable,
            ["crossings"] = quote.Crossings, ["demolished"] = quote.Demolished, ["demolition_price"] = quote.DemolitionPrice,
        };
    }

    /// <summary>Builds a track. True when something was built.</summary>
    public bool BuildTrack(float x0, float z0, float x1, float z1, float sweep) =>
        Sim.Execute(new BuildTrackCommand(x0, z0, x1, z1, sweep)) > 0;

    /// <summary>The track under a point, as {found, track, length, demolition, x0, z0, x1, z1}.</summary>
    public Godot.Collections.Dictionary TrackAt(float x, float z)
    {
        if (Sim.Rail.TrackAt(x, z, 14f) is not { } id) return new Godot.Collections.Dictionary { ["found"] = false };
        var track = Sim.Rail.GetTrack(id);
        return new Godot.Collections.Dictionary { ["found"] = true, ["track"] = id.Value, ["length"] = track.Length, ["demolition"] = Sim.Rail.DemolitionCost(id) };
    }

    /// <summary>The centre line of a track as points about 6 m apart, for the hover preview.</summary>
    public Vector3[] TrackPoints(int track)
    {
        var t = Sim.Rail.GetTrack(new TrackId(track));
        int n = Math.Max(2, (int)MathF.Ceiling(t.Length / 6f));
        var points = new Vector3[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float along = t.Length * i / n;
            var (x, z) = t.Geometry.PointAt(along);
            points[i] = new Vector3(x, Sim.Rail.TrackHeightAt(t.Id, along), z);
        }
        return points;
    }

    public bool RemoveTrack(int track) => Sim.Execute(new RemoveTrackCommand(track)) == 1;

    /// <summary>Where a signal clicked for would stand: {valid, x, y, z, heading, price, affordable, node}.</summary>
    public Godot.Collections.Dictionary SignalPreview(float x, float z, int kind, bool forward)
    {
        long price = kind == 1 ? Sim.Rail.Rail.PathSignalPrice : Sim.Rail.Rail.SignalPrice;
        var rail = Sim.Rail;
        int node = rail.FindNodeNear(x, z, rail.Rail.SnapRadius, withTracksOnly: true);
        TrackId track;
        float along;
        if (node >= 0)
        {
            float best = float.MaxValue;
            track = TrackId.None;
            foreach (var id in rail.TracksAt(node))
            {
                float d = rail.GetTrack(id).Geometry.DistanceTo(x, z);
                if (d < best) { best = d; track = id; }
            }
            if (track.IsNone) return new Godot.Collections.Dictionary { ["valid"] = false };
            along = node == rail.GetTrack(track).To ? rail.GetTrack(track).Length : 0f;
        }
        else
        {
            if (rail.NearestOnTrack(x, z, rail.Rail.SnapRadius, rail.Rail.StationAlongStep) is not { } point) return new Godot.Collections.Dictionary { ["valid"] = false };
            track = point.Track;
            along = point.Along;
            node = forward ? rail.GetTrack(track).To : rail.GetTrack(track).From;
        }
        var t = rail.GetTrack(track);
        bool facingForward = node >= 0 && node == t.To;
        var (px, pz, heading) = rail.PointAt(track, along);
        if (!facingForward) heading += MathF.PI;
        // Stand a few metres short of the point, to the right of the direction the train comes from.
        float dx = MathF.Cos(heading), dz = MathF.Sin(heading);
        return new Godot.Collections.Dictionary
        {
            ["valid"] = true, ["x"] = px - dx * 3f - dz * 2.6f, ["y"] = rail.TrackHeightAt(track, along), ["z"] = pz - dz * 3f + dx * 2.6f,
            ["heading"] = heading, ["price"] = price, ["affordable"] = Sim.CanAfford(price),
        };
    }

    /// <summary>Builds a signal (kind 0 block, 1 path). True when built.</summary>
    public bool BuildSignal(float x, float z, int kind, bool forward) =>
        Sim.Execute(new BuildSignalCommand(x, z, kind == 1 ? SignalKind.Path : SignalKind.Block, forward)) >= 0;

    /// <summary>The signal nearest to a point within a radius, as {found, signal, kind, x, y, z}.</summary>
    public Godot.Collections.Dictionary SignalAt(float x, float z, float radius)
    {
        int best = -1;
        float bestD = radius * radius;
        foreach (var s in Sim.Rail.Signals)
        {
            if (!s.Active) continue;
            var (sx, sz) = Sim.Rail.GetNode(s.Node);
            float d = (sx - x) * (sx - x) + (sz - z) * (sz - z);
            if (d <= bestD) { best = s.Id.Value; bestD = d; }
        }
        if (best < 0) return new Godot.Collections.Dictionary { ["found"] = false };
        var signal = Sim.Rail.Signals[best];
        var (nx, nz) = Sim.Rail.GetNode(signal.Node);
        return new Godot.Collections.Dictionary { ["found"] = true, ["signal"] = best, ["kind"] = (int)signal.Kind, ["x"] = nx, ["y"] = Sim.Rail.GetNodeHeight(signal.Node), ["z"] = nz };
    }

    public bool RemoveSignal(int signal) => Sim.Execute(new RemoveSignalCommand(signal)) == 1;

    /// <summary>Where a rail station would stand: {valid, x, y, z, price, affordable, radius, covered, industries}.</summary>
    public Godot.Collections.Dictionary RailStationPreview(float x, float z)
    {
        if (Sim.QuoteRailStation(x, z) is not { } quote) return new Godot.Collections.Dictionary { ["valid"] = false };
        return new Godot.Collections.Dictionary
        {
            ["valid"] = true, ["x"] = quote.Site.X, ["y"] = quote.Site.Y, ["z"] = quote.Site.Z,
            ["price"] = quote.Price, ["affordable"] = quote.Affordable, ["radius"] = quote.Radius, ["covered"] = quote.Buildings, ["industries"] = quote.Industries,
        };
    }

    public int BuildRailStation(float x, float z) => Sim.Execute(new BuildRailStationCommand(x, z));

    public int Sim_RailDepotCount() => Sim.RailDepots.Count;

    /// <summary>Builds a rail depot at the track node near (x, z), its building turned by <paramref name="angle"/> (NaN: the free side of the node) and its floor <paramref name="raise"/> metres up.</summary>
    public bool BuildRailDepot(float x, float z, float angle, float raise)
    {
        if (Sim.Execute(new AddRailDepotCommand(x, z, DepotLevel.Simple, angle, raise)) < 0) return false;
        RebuildRoads(); // the pad moved the ground
        return true;
    }

    /// <summary>
    /// What a rail depot clicked for at (x, z) would be: {valid, why, x, y (the floor of its pad), z (the middle of its building), angle, size, node_x, node_z, price, pad_money, pad_cut, pad_fill,
    /// pad_max_cut, pad_max_fill, pad_step, pad_cells, pad_deltas, cell, affordable}.
    /// </summary>
    public Godot.Collections.Dictionary RailDepotPreview(float x, float z, float angle, float raise)
    {
        var plan = Sim.PlanRailDepot(x, z, DepotLevel.Simple, angle, raise);
        var rail = Sim.Network.Tuning.Rail;
        var preview = new Godot.Collections.Dictionary
        {
            ["valid"] = plan.Valid, ["why"] = plan.Reason, ["x"] = plan.X, ["z"] = plan.Z, ["angle"] = plan.Angle, ["node_x"] = plan.NodeX, ["node_z"] = plan.NodeZ,
            ["y"] = plan.Pad.Reason == null ? HeightAt(plan.X, plan.Z) : plan.Pad.Height,
            ["size"] = new Vector3(rail.DepotHalfLength * 2f, _whitebox.RailDepot.Size.Y, rail.DepotHalfWidth * 2f),
            ["price"] = plan.Price, ["pad_money"] = plan.Pad.Money, ["pad_cut"] = plan.Pad.Cut, ["pad_fill"] = plan.Pad.Fill, ["pad_max_cut"] = plan.Pad.MaxCut, ["pad_max_fill"] = plan.Pad.MaxFill,
            ["pad_step"] = Sim.PadRaiseStep, ["affordable"] = Sim.CanAfford(plan.Price + plan.Pad.Money),
        };
        AddPadCells(preview, plan.Pad);
        return preview;
    }

    /// <summary>What can be bought for a rail line: for each {id, label, price, upkeep, capacity, cargo}.</summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetTrainOptions()
    {
        var options = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var loco in Sim.AvailableLocomotives)
            foreach (var wagon in Sim.Wagons)
                foreach (int count in new[] { 4, 8 })
                {
                    if (count > loco.MaxWagons) continue;
                    options.Add(new Godot.Collections.Dictionary
                    {
                        ["id"] = $"{loco.Id}:{wagon.Id}:{count}",
                        ["label"] = $"{loco.Name} + {count} x {wagon.Name.ToLowerInvariant()}",
                        ["price"] = loco.Price + count * wagon.Price,
                        ["upkeep"] = loco.UpkeepPerDay + count * wagon.UpkeepPerDay,
                        ["capacity"] = count * wagon.Capacity,
                        ["cargo"] = wagon.Cargo,
                    });
                }
        return options;
    }

    /// <summary>Buys a train from an option id ("locomotive:wagon:count") for a rail line at the nearest depot. Returns the train id, -1 when refused.</summary>
    public int BuyTrain(string option, int line)
    {
        var parts = option.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[2], out int count)) return -1;
        return Sim.Execute(new BuyTrainCommand(BuyTrainCommand.NearestDepot, parts[0], parts[1], count, line));
    }

    public bool SellTrain(int train) => Sim.Execute(new SellTrainCommand(train)) >= 0;

    /// <summary>
    /// The company's trains: for each {id, model, line, line_name, color, load, capacity, cargo, income, upkeep, profit, state, wrecked
    /// (always false), deadlocked, x, y, z}. Their ids are negative (-1 - train) so that they share the Vehicles window with road vehicles.
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetTrains()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var train in Sim.Trains)
        {
            if (!train.Active) continue;
            var loco = Sim.Locomotives[train.Locomotive];
            var wagon = Sim.Wagons[train.Wagon];
            string where = StationName(Sim.TargetStationOf(train));
            var phase = Sim.RepairPhaseOf(train);
            string what = train.Broken ? "Broken down, waiting for the crew"
                : phase is RepairPhase.ToDepot or RepairPhase.Waiting or RepairPhase.Working ? PhaseText(phase, Sim.RepairOf(train))
                : train.ReturnDepot >= 0 ? "Going to a depot to wait there"
                : train.InDepot ? $"Waiting in the depot to leave for {where}"
                : train.Deadlocked ? $"DEADLOCK: waits for track held by train {train.BlockedBy + 1}"
                : train.Status switch
                {
                    TrainStatus.AtStation => $"Loading at {where}",
                    TrainStatus.WaitingForTrack => train.BlockedBy >= 0 ? $"Waiting at a signal for train {train.BlockedBy + 1}" : train.BlockedBy == -2 ? "Waiting at a signal that has failed" : "Waiting at a signal",
                    TrainStatus.Halted => "Halted after hitting a vehicle",
                    TrainStatus.NoRoute => $"No way to {where}",
                    _ => $"Heading for {where}",
                };
            var (x, y, z, _, _) = Sim.RoutePoint(train, train.Head);
            var entry = new Godot.Collections.Dictionary
            {
                ["id"] = -1 - train.Id, ["model"] = $"{loco.Name} + {train.Wagons} x {wagon.Name.ToLowerInvariant()}", ["line"] = train.Line,
                ["line_name"] = Sim.Lines[train.Line].Name, ["color"] = LineColor(train.Line),
                ["load"] = train.Load, ["capacity"] = train.Capacity, ["cargo"] = train.LoadCargo >= 0 ? Sim.CargoTypes[train.LoadCargo].Name : "",
                ["income"] = train.Income, ["upkeep"] = train.UpkeepPaid, ["profit"] = train.Income - train.UpkeepPaid,
                ["state"] = what, ["wrecked"] = false, ["deadlocked"] = train.Deadlocked, ["x"] = x, ["y"] = y, ["z"] = z,
            };
            AddMaintenance(entry, train);
            list.Add(entry);
        }
        return list;
    }

    /// <summary>New rail warnings since the last call, as text.</summary>
    public Godot.Collections.Array<string> TakeRailWarnings()
    {
        var texts = new Godot.Collections.Array<string>();
        while (_warningsShown < Sim.RailWarnings.Count)
        {
            var w = Sim.RailWarnings[_warningsShown++];
            texts.Add($"Deadlock: trains {string.Join(" and ", w.Trains.Select(t => (t + 1).ToString()))} wait for each other for good. Sell one, or add a passing loop with path signals.");
        }
        return texts;
    }

    // ---- Drawing --------------------------------------------------------------------------------------------------------

    void DrawRail(float stepFraction)
    {
        // Laying track levels the ground under it: redraw the chunks it changed (a no-op when the terrain is unchanged).
        RebuildTerrain();
        var rail = Sim.Rail;
        long signature = ((long)rail.Version << 32) ^ ((long)Sim.Stations.Count << 16) ^ ((long)Sim.RailDepots.Count << 8) ^ (long)Sim.Crossings.Count ^ ((long)StationActiveCount() << 40);
        if (!ReferenceEquals(_railSim, Sim) || signature != _railSignature)
        {
            if (!ReferenceEquals(_railSim, Sim)) _warningsShown = 0;
            _railSim = Sim;
            _railSignature = signature;
            RebuildRail();
        }
        UpdateCrossingsAndSignals(delta: 0.0);
        DrawTrains(stepFraction);
        DrawLights();
        DrawSigns();
    }

    int StationActiveCount()
    {
        int n = 0;
        foreach (var s in Sim.Stations) if (s.Active && s.Mode == StationMode.Rail) n++;
        return n;
    }

    /// <summary>Draws the tracks, bridges, tunnels, platforms and depots again, and makes the signal and crossing figures.</summary>
    void RebuildRail()
    {
        foreach (var child in _railRoot.GetChildren()) child.QueueFree();
        foreach (var child in _signalRoot.GetChildren()) child.QueueFree();
        foreach (var child in _crossingRoot.GetChildren()) child.QueueFree();
        _signalLamps.Clear();
        _crossingParts.Clear();
        var rail = Sim.Rail;
        var terrain = rail.Terrain ?? _terrain;

        var bed = new List<Vector3>();
        var bedIndex = new List<int>();
        var rails = new List<Vector3>();
        var railIndex = new List<int>();
        foreach (var track in rail.Tracks)
        {
            if (track.Retired) continue;
            var g = track.Geometry;
            int segments = Math.Max(1, (int)MathF.Ceiling(g.Length / 4f));
            int bedFirst = bed.Count;
            int railFirst = rails.Count;
            for (int i = 0; i <= segments; i++)
            {
                float along = g.Length * i / segments;
                var (x, z) = g.PointAt(along);
                float h = g.HeadingAt(along);
                float rx = -MathF.Sin(h), rz = MathF.Cos(h);
                float y = rail.TrackHeightAt(track.Id, along) + RailLift;
                bed.Add(new Vector3(x - rx * BedWidth * 0.5f, y, z - rz * BedWidth * 0.5f));
                bed.Add(new Vector3(x + rx * BedWidth * 0.5f, y, z + rz * BedWidth * 0.5f));
                foreach (float side in new[] { -RailGauge, RailGauge })
                {
                    rails.Add(new Vector3(x + rx * (side - 0.09f), y + 0.12f, z + rz * (side - 0.09f)));
                    rails.Add(new Vector3(x + rx * (side + 0.09f), y + 0.12f, z + rz * (side + 0.09f)));
                }
            }
            for (int i = 0; i < segments; i++)
            {
                int l = bedFirst + i * 2;
                bedIndex.AddRange(new[] { l, l + 2, l + 1, l + 2, l + 3, l + 1 });
                int r = railFirst + i * 4;
                foreach (int rail0 in new[] { 0, 2 })
                {
                    int a = r + rail0;
                    railIndex.AddRange(new[] { a, a + 4, a + 1, a + 4, a + 5, a + 1 });
                }
            }
            if (track.Structure == RoadStructure.Bridge) AddRailBridge(rail, track);
            else if (track.Structure == RoadStructure.Tunnel) AddRailTunnel(rail, track, terrain);
        }
        var mesh = new ArrayMesh();
        AddSurface(mesh, bed, bedIndex);
        AddSurface(mesh, rails, railIndex);
        var instance = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        if (mesh.GetSurfaceCount() > 0) instance.SetSurfaceOverrideMaterial(0, _bedMaterial);
        if (mesh.GetSurfaceCount() > 1) instance.SetSurfaceOverrideMaterial(1, _railMaterial);
        _railRoot.AddChild(instance);

        foreach (var station in Sim.Stations)
        {
            if (!station.Active || station.Mode != StationMode.Rail) continue;
            AddPlatform(rail, station.RailNode);
        }
        foreach (var depot in Sim.RailDepots)
        {
            if (!depot.Active) continue;
            var (x, z) = rail.GetNode(depot.Node);
            var block = _whitebox.RailDepot;
            if (depot.Placed)
            {
                // Built since M6g: where its pad is, turned the way the player chose.
                var tuning = Sim.Network.Tuning.Rail;
                if (PlaceModel(_railRoot, "raildepot", new Vector3(depot.X, HeightAt(depot.X, depot.Z), depot.Z), depot.Angle)) continue;
                var placed = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(tuning.DepotHalfLength * 2f, block.Size.Y, tuning.DepotHalfWidth * 2f) }, MaterialOverride = _railDepotMaterial };
                placed.Position = new Vector3(depot.X, HeightAt(depot.X, depot.Z) + block.Size.Y * 0.5f, depot.Z);
                placed.Rotation = new Vector3(0f, -depot.Angle, 0f);
                _railRoot.AddChild(placed);
                continue;
            }
            if (PlaceModel(_railRoot, "raildepot", new Vector3(x + block.Offset.X, rail.GetNodeHeight(depot.Node), z + block.Offset.Y), MathF.Atan2(-block.Offset.Y, -block.Offset.X))) continue;
            var building = new MeshInstance3D { Mesh = new BoxMesh { Size = block.Size }, MaterialOverride = _railDepotMaterial };
            building.Position = new Vector3(x + block.Offset.X, rail.GetNodeHeight(depot.Node) + block.Size.Y * 0.5f, z + block.Offset.Y);
            _railRoot.AddChild(building);
        }
        foreach (var signal in rail.Signals)
        {
            if (signal.Active) AddSignalFigure(rail, signal);
        }
        foreach (var crossing in Sim.Crossings)
        {
            if (crossing.Active) AddCrossingFigure(crossing);
        }
    }

    static void AddSurface(ArrayMesh mesh, List<Vector3> vertices, List<int> indices)
    {
        if (vertices.Count == 0 || indices.Count == 0) return;
        var normals = new Vector3[vertices.Count];
        Array.Fill(normals, Vector3.Up);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    void AddBoxAt(Node3D parent, Vector3 centre, float heading, Vector3 size, Material material)
    {
        var box = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = material };
        box.Transform = new Transform3D(new Basis(Vector3.Up, -heading), centre);
        parent.AddChild(box);
    }

    /// <summary>A slab under the track and piers down to the ground, as for a road bridge.</summary>
    void AddRailBridge(RailNetwork rail, Track track)
    {
        var g = track.Geometry;
        int slabs = Math.Max(1, (int)MathF.Ceiling(g.Length / 8f));
        for (int k = 0; k < slabs; k++)
        {
            float a = g.Length * k / slabs, b = g.Length * (k + 1) / slabs, mid = (a + b) * 0.5f;
            var (x, z) = g.PointAt(mid);
            float y = rail.TrackHeightAt(track.Id, mid);
            AddBoxAt(_railRoot, new Vector3(x, y - 0.4f, z), g.HeadingAt(mid), new Vector3(b - a + 0.2f, 0.7f, 5f), _deckMaterial);
        }
        int piers = Math.Max(1, (int)MathF.Floor(g.Length / 25f));
        for (int k = 1; k < piers; k++)
        {
            float along = g.Length * k / piers;
            var (x, z) = g.PointAt(along);
            float deck = rail.TrackHeightAt(track.Id, along);
            float ground = _terrain.HeightAt(x, z);
            if (deck - ground < 1.5f) continue;
            float h = deck - ground;
            AddBoxAt(_railRoot, new Vector3(x, ground + h * 0.5f, z), g.HeadingAt(along), new Vector3(2.2f, h, 3f), _deckMaterial);
        }
    }

    /// <summary>A dark portal at each end of a tunnel.</summary>
    void AddRailTunnel(RailNetwork rail, Track track, Terrain terrain)
    {
        var g = track.Geometry;
        foreach (float along in new[] { 0f, g.Length })
        {
            var (x, z) = g.PointAt(along);
            float y = rail.TrackHeightAt(track.Id, along);
            AddBoxAt(_railRoot, new Vector3(x, y + 3f, z), g.HeadingAt(along), new Vector3(2.4f, 6f, 7f), _tunnelMaterial);
        }
    }

    /// <summary>A platform beside the first track at a station's node, and a roof sign.</summary>
    void AddPlatform(RailNetwork rail, int node)
    {
        var tracks = rail.TracksAt(node);
        if (tracks.Count == 0) return;
        float heading = rail.HeadingOutOf(tracks[0], node);
        var (x, z) = rail.GetNode(node);
        float dx = MathF.Cos(heading), dz = MathF.Sin(heading);
        float y = rail.GetNodeHeight(node);
        // Along the track on the side the trains stand (behind the node as the first track leaves it), to the right of it.
        foreach (float sign in tracks.Count > 1 ? new[] { 1f, -1f } : new[] { 1f })
        {
            float cx = x + dx * 18f * sign - dz * 3.6f, cz = z + dz * 18f * sign + dx * 3.6f;
            AddBoxAt(_railRoot, new Vector3(cx, y + 0.45f, cz), heading, new Vector3(36f, 0.9f, 3.2f), _railStationMaterial);
        }
        AddBoxAt(_railRoot, new Vector3(x - dz * 3.6f, y + 3.6f, z + dx * 3.6f), heading, new Vector3(10f, 0.3f, 3.6f), _railStationMaterial);
    }

    void AddSignalFigure(RailNetwork rail, RailSignal signal)
    {
        float heading = rail.HeadingInto(signal.Track, signal.Node);
        var (nx, nz) = rail.GetNode(signal.Node);
        float dx = MathF.Cos(heading), dz = MathF.Sin(heading);
        float y = rail.GetNodeHeight(signal.Node);
        var foot = new Vector3(nx - dx * 5f - dz * 2.7f, y, nz - dz * 5f + dx * 2.7f);
        bool path = signal.Kind == SignalKind.Path;
        AddBoxAt(_signalRoot, foot + new Vector3(0, 2.2f, 0), heading, new Vector3(0.25f, 4.4f, 0.25f), _postMaterial);
        var lamp = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.8f, 0.2f), EmissionEnabled = true, Emission = new Color(0.1f, 0.8f, 0.2f), Roughness = 0.4f };
        var head = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, path ? 1.4f : 0.9f, 0.9f) }, MaterialOverride = lamp };
        head.Transform = new Transform3D(new Basis(Vector3.Up, -heading), foot + new Vector3(0, 4.3f, 0));
        _signalRoot.AddChild(head);
        if (path) AddBoxAt(_signalRoot, foot + new Vector3(0, 3.35f, 0), heading, new Vector3(0.6f, 0.15f, 1.0f), _railMaterial); // a bar under the lamp marks a path signal
        _signalLamps.Add((lamp, signal));
    }

    void AddCrossingFigure(LevelCrossing crossing)
    {
        var net = Sim.Network;
        var road = net.GetRoad(crossing.Road);
        float h = road.Geometry.HeadingAt(crossing.RoadAlong);
        float dx = MathF.Cos(h), dz = MathF.Sin(h);
        float y = net.RoadHeightAt(crossing.Road, crossing.RoadAlong);
        float half = RoadNetwork.HalfWidth(road.Type);
        var light = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.1f, 0.1f), EmissionEnabled = true, Emission = new Color(0.9f, 0.1f, 0.1f) };
        var arms = new Node3D[2];
        for (int k = 0; k < 2; k++)
        {
            // Traffic moving along s * (dx, dz) meets the barrier before the rails, on its right-hand verge; the arm points across to the middle.
            float s = k == 0 ? 1f : -1f;
            float rightX = s * -dz, rightZ = s * dx;
            float distance = crossing.RoadHalf + 2.2f;
            var verge = new Vector3(crossing.X - s * dx * distance + rightX * (half + 1f), y + 1.1f, crossing.Z - s * dz * distance + rightZ * (half + 1f));
            var pivot = new Node3D { Position = verge, Rotation = new Vector3(0, MathF.Atan2(rightZ, -rightX), 0) };
            pivot.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.3f, 2.2f, 0.3f) }, MaterialOverride = _postMaterial, Position = new Vector3(0, -0.55f, 0) });
            var armPivot = new Node3D();
            armPivot.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(half + 1f, 0.18f, 0.18f) }, MaterialOverride = light, Position = new Vector3((half + 1f) * 0.5f, 0, 0) });
            pivot.AddChild(armPivot);
            _crossingRoot.AddChild(pivot);
            arms[k] = armPivot;
        }
        _crossingParts.Add((arms[0], arms[1], light, crossing));
    }

    /// <summary>RailSignal lamps (a few times a second) and barrier arms (each frame) follow the simulation.</summary>
    void UpdateCrossingsAndSignals(double delta)
    {
        foreach (var (arm0, arm1, light, crossing) in _crossingParts)
        {
            float angle = crossing.State == CrossingState.Open ? 80f : crossing.State == CrossingState.Lowering ? 40f : 0f;
            arm0.RotationDegrees = new Vector3(0, 0, angle);
            arm1.RotationDegrees = new Vector3(0, 0, angle);
            bool on = crossing.State != CrossingState.Open && ((int)(Sim.Tick / 8) % 2 == 0 || crossing.State == CrossingState.Closed);
            light.EmissionEnabled = on;
            light.AlbedoColor = on ? new Color(0.95f, 0.1f, 0.1f) : new Color(0.35f, 0.1f, 0.1f);
        }
        if (Sim.Tick % 10 != 0) return;
        foreach (var (lamp, signal) in _signalLamps)
        {
            bool clear = Sim.IsSignalClear(signal);
            var colour = clear ? new Color(0.1f, 0.85f, 0.25f) : new Color(0.95f, 0.1f, 0.1f);
            lamp.AlbedoColor = colour;
            lamp.Emission = colour;
        }
    }

    void DrawTrains(float stepFraction)
    {
        var buf = _trainBuffer;
        int n = 0;
        var marks = _trainMarkBuffer;
        int markCount = 0;
        _locomotiveModelIds ??= Sim.Locomotives.Select(l => "rail." + l.Id).ToArray();
        _wagonModelIds ??= Sim.Wagons.Select(w => "rail." + w.Id).ToArray();
        _trainModels.Begin();
        foreach (var train in Sim.Trains)
        {
            if (!train.Active) continue;
            float lead = train.Speed * Simulation.StepSeconds * stepFraction;
            var cars = Sim.TrainCars(train, lead);
            var line = LineColor(train.Line);
            foreach (var car in cars)
            {
                if (!car.Visible) continue;
                if (n >= TrainCarCapacity) break;
                int o = n * FloatsPerInstance;
                float fx = MathF.Cos(car.Heading), fz = MathF.Sin(car.Heading);
                string modelId = car.IsLocomotive ? _locomotiveModelIds[train.Locomotive] : _wagonModelIds[train.Wagon];
                if (_trainModels.Has(modelId))
                {
                    _trainModels.Add(modelId, car.X, car.Y + RailLift + 0.35f, car.Z, fx, fz, 0f);
                    n++; // keeps the box's slot (zeroed below) so the counts stay one to one
                    Array.Clear(buf, o, FloatsPerInstance);
                    if (car.IsLocomotive && markCount < TrainCarCapacity)
                    {
                        int m = markCount++ * FloatsPerInstance;
                        marks[m + 0] = fx;  marks[m + 1] = 0f; marks[m + 2] = -fz; marks[m + 3] = car.X;
                        marks[m + 4] = 0f;  marks[m + 5] = 1f; marks[m + 6] = 0f;  marks[m + 7] = car.Y + RailLift + 0.35f + _trainModels.HeightOf(modelId) + 0.1f;
                        marks[m + 8] = fz;  marks[m + 9] = 0f; marks[m + 10] = fx; marks[m + 11] = car.Z;
                        Color mark = train.Deadlocked ? new Color(0.95f, 0.15f, 0.12f) : ConditionTint(line, train.Condition);
                        marks[m + 12] = mark.R; marks[m + 13] = mark.G; marks[m + 14] = mark.B; marks[m + 15] = 1f;
                    }
                    continue;
                }
                float length = car.Length - 0.6f, height = car.IsLocomotive ? 3.8f : 3.4f;
                buf[o + 0] = fx * length; buf[o + 1] = 0f;     buf[o + 2] = -fz; buf[o + 3] = car.X;
                buf[o + 4] = 0f;          buf[o + 5] = height; buf[o + 6] = 0f;  buf[o + 7] = car.Y + RailLift + 0.35f + height * 0.5f;
                buf[o + 8] = fz * length; buf[o + 9] = 0f;     buf[o + 10] = fx; buf[o + 11] = car.Z;
                Color c = train.Deadlocked ? new Color(0.95f, 0.15f, 0.12f)
                    : ConditionTint(car.IsLocomotive ? line : line.Lerp(new Color(0.85f, 0.85f, 0.85f), 0.55f), train.Condition);
                buf[o + 12] = c.R; buf[o + 13] = c.G; buf[o + 14] = c.B; buf[o + 15] = 1f;
                n++;
            }
        }
        _trainModels.End();
        _trainCars.Buffer = buf;
        _trainCars.VisibleInstanceCount = n;
        _trainMarks.Buffer = marks;
        _trainMarks.VisibleInstanceCount = markCount;
    }
}
