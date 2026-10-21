using System;
using System.Collections.Generic;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// Raids (C12) in 3D: the bomber flights are drawn from the simulation's state every frame, the way the vehicles are, so pause and speed are
/// respected. Aircraft are small low-poly models moved along their straight path (<see cref="Simulation.AircraftPoint"/>), each bomb falls from the
/// flying height to its exact impact point over the simulation's own time (<see cref="Simulation.BombPoint"/>), and a bomb that has landed leaves a
/// flash and a dark scar. What the bomb did (a dark industry, a shut gateway, destroyed buildings, wrecks, craters) is drawn from the state as
/// before; this is the picture of the flight, with no people in it. An aircraft the anti-aircraft brings down (C13) is hidden from its formation, a puff
/// of smoke marks where it was hit, and it falls as a tumbling model along <see cref="Simulation.FallPoint"/> until it lands in a flash.
/// </summary>
public partial class SimHost
{
    const float FlashSeconds = 1.2f, ScarSeconds = 90f, PuffSeconds = 3f;

    sealed class FlightNodes
    {
        public readonly List<Node3D> Aircraft = new();
    }

    readonly Dictionary<int, FlightNodes> _flightNodes = new();
    readonly Dictionary<int, MeshInstance3D> _bombNodes = new();
    readonly Dictionary<int, Node3D> _fallNodes = new();
    readonly List<(MeshInstance3D Puff, double Age)> _puffs = new();
    readonly List<(MeshInstance3D Flash, double Age)> _flashes = new();
    readonly List<(MeshInstance3D Scar, double Age)> _scars = new();
    Node3D? _raidRoot;
    int _impactsSeen;
    Mesh? _fuselageMesh, _wingMesh, _tailMesh;
    SphereMesh? _bombMesh, _flashMesh, _puffMesh;
    CylinderMesh? _scarMesh;
    StandardMaterial3D? _aircraftMaterial, _bombMaterial, _flashMaterial, _scarMaterial, _puffMaterial;

    /// <summary>
    /// The flights in the air, for the HUD's warning: for each {id, aircraft, direction ("north-east"), seconds (until the first is over the target, 0 once
    /// it has passed), revealed (the target is told), target (its name, "" until revealed), x, z (the target's place), dropping (bombs are falling)}.
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetRaidWarnings()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var flight in Sim.Flights)
            list.Add(new Godot.Collections.Dictionary
            {
                ["id"] = flight.Id, ["aircraft"] = flight.Aircraft, ["direction"] = Simulation.DirectionName(Sim.RaidDirection),
                ["seconds"] = flight.SecondsToTarget, ["revealed"] = flight.TargetRevealed, ["target"] = flight.TargetRevealed ? flight.TargetName : "",
                ["x"] = flight.TargetX, ["z"] = flight.TargetZ, ["dropping"] = flight.Phase != FlightPhase.Approaching,
            });
        return list;
    }

    void MakeRaidResources()
    {
        if (_fuselageMesh != null) return;
        // An aircraft is built along +x: a long body, wings across it and a small tail fin.
        _fuselageMesh = new BoxMesh { Size = new Vector3(14f, 2.4f, 2.4f) };
        _wingMesh = new BoxMesh { Size = new Vector3(3.5f, 0.5f, 22f) };
        _tailMesh = new BoxMesh { Size = new Vector3(2.5f, 3.5f, 0.5f) };
        _bombMesh = new SphereMesh { Radius = 0.6f, Height = 2.4f };
        _flashMesh = new SphereMesh { Radius = 2.2f, Height = 4.4f };
        _aircraftMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.24f, 0.2f) };
        _bombMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.12f) };
        _flashMaterial = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.65f, 0.2f), EmissionEnabled = true, Emission = new Color(1f, 0.55f, 0.15f), EmissionEnergyMultiplier = 3f };
        _scarMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.07f, 0.06f) };
        _puffMesh = new SphereMesh { Radius = 3f, Height = 6f };
        _puffMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.35f, 0.37f, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
    }

    /// <summary>
    /// What a click would do with the anti-aircraft tool (C13): {valid, why (what is wrong, "" when nothing), x, y, z, size (half the side of the square it stands on), price, upkeep (a month), range (metres),
    /// affordable, available (the guns can be built yet), from_month}.
    /// </summary>
    public Godot.Collections.Dictionary BatteryPreview(float x, float z)
    {
        var a = Sim.AntiAircraft;
        string? why = Sim.WhyNotBattery(x, z);
        return new Godot.Collections.Dictionary
        {
            ["valid"] = why == null, ["why"] = why ?? "", ["x"] = x, ["y"] = HeightAt(x, z), ["z"] = z, ["size"] = Sim.IndustryTuning.Types[Sim.BatteryType].Size,
            ["price"] = a.Price, ["upkeep"] = a.UpkeepPerMonth, ["range"] = a.Range, ["affordable"] = Sim.CanAfford(a.Price),
            ["available"] = Sim.MonthIndex >= a.FromMonth, ["from_month"] = a.FromMonth,
        };
    }

    /// <summary>Builds an anti-aircraft battery at a place; false when refused, and <see cref="BatteryPreview"/> says why.</summary>
    public bool BuildBattery(float x, float z) => Sim.Execute(new BuildBatteryCommand(x, z)) >= 0;

    Node3D NewAircraft()
    {
        var plane = new Node3D();
        plane.AddChild(new MeshInstance3D { Mesh = _fuselageMesh, MaterialOverride = _aircraftMaterial });
        plane.AddChild(new MeshInstance3D { Mesh = _wingMesh, MaterialOverride = _aircraftMaterial, Position = new Vector3(1f, 0f, 0f) });
        plane.AddChild(new MeshInstance3D { Mesh = _tailMesh, MaterialOverride = _aircraftMaterial, Position = new Vector3(-6.5f, 2.2f, 0f) });
        return plane;
    }

    /// <summary>Height of the ground under a point of the sky, also outside the map (where the aircraft come in).</summary>
    float GroundAt(float x, float z)
    {
        float half = _terrain.HalfSize - 1f;
        return HeightAt(Math.Clamp(x, -half, half), Math.Clamp(z, -half, half));
    }

    void UpdateRaidEffects(double delta, float stepFraction)
    {
        _raidRoot ??= new Node3D { Name = "Raids" };
        if (_raidRoot.GetParent() == null) AddChild(_raidRoot);
        MakeRaidResources();
        double aging = delta * _speed; // a flash and a scar age with the simulation's time: a pause holds them

        // The aircraft.
        foreach (var flight in Sim.Flights)
        {
            if (!_flightNodes.TryGetValue(flight.Id, out var nodes))
            {
                nodes = new FlightNodes();
                _flightNodes[flight.Id] = nodes;
                for (int a = 0; a < flight.Aircraft; a++)
                {
                    var plane = NewAircraft();
                    _raidRoot.AddChild(plane);
                    nodes.Aircraft.Add(plane);
                }
            }
            for (int a = 0; a < nodes.Aircraft.Count; a++)
            {
                var (x, z) = Sim.AircraftPoint(flight, a, stepFraction);
                nodes.Aircraft[a].Position = new Vector3(x, GroundAt(x, z) + flight.Altitude, z);
                nodes.Aircraft[a].Rotation = new Vector3(0f, -flight.Heading, 0f);
                nodes.Aircraft[a].Visible = !flight.IsDown(a); // one the anti-aircraft brought down has left the formation
            }
        }
        if (_flightNodes.Count > Sim.Flights.Count)
        {
            var gone = new List<int>();
            foreach (var id in _flightNodes.Keys)
            {
                bool flying = false;
                foreach (var flight in Sim.Flights) if (flight.Id == id) flying = true;
                if (!flying) gone.Add(id);
            }
            foreach (int id in gone)
            {
                foreach (var plane in _flightNodes[id].Aircraft) plane.QueueFree();
                _flightNodes.Remove(id);
            }
        }

        // The aircraft the anti-aircraft has hit: a puff where it was, then it falls, tumbling, to the ground.
        var coming = new HashSet<int>();
        foreach (var fall in Sim.FallingAircraft)
        {
            coming.Add(fall.Id);
            if (!_fallNodes.TryGetValue(fall.Id, out var plane))
            {
                plane = NewAircraft();
                _raidRoot.AddChild(plane);
                _fallNodes[fall.Id] = plane;
                var puff = new MeshInstance3D { Mesh = _puffMesh, MaterialOverride = _puffMaterial, Position = new Vector3(fall.FromX, GroundAt(fall.FromX, fall.FromZ) + fall.Altitude, fall.FromZ) };
                _raidRoot.AddChild(puff);
                _puffs.Add((puff, 0));
            }
            var (fx, height, fz) = Sim.FallPoint(fall, stepFraction);
            float spent = (Sim.Tick + stepFraction - fall.HitTick) / (float)(fall.LandTick - fall.HitTick);
            plane.Position = new Vector3(fx, GroundAt(fx, fz) + height, fz);
            plane.Basis = new Basis(Vector3.Up, -fall.Heading) * new Basis(Vector3.Back, -0.2f - 0.9f * spent) * new Basis(Vector3.Right, spent * 9f);
        }
        if (_fallNodes.Count > coming.Count)
        {
            var landed = new List<int>();
            foreach (var id in _fallNodes.Keys) if (!coming.Contains(id)) landed.Add(id);
            foreach (int id in landed)
            {
                _fallNodes[id].QueueFree();
                _fallNodes.Remove(id);
            }
        }
        for (int k = _puffs.Count - 1; k >= 0; k--)
        {
            var (puff, age) = _puffs[k];
            age += aging;
            if (age >= PuffSeconds) { puff.QueueFree(); _puffs.RemoveAt(k); continue; }
            puff.Scale = Vector3.One * (1f + 5f * (float)(age / PuffSeconds));
            _puffs[k] = (puff, age);
        }

        // The bombs in the air: each from where it was let go to where it will land, on the simulation's time.
        var falling = new HashSet<int>();
        foreach (var bomb in Sim.BombsFalling)
        {
            falling.Add(bomb.Id);
            if (!_bombNodes.TryGetValue(bomb.Id, out var node))
            {
                node = new MeshInstance3D { Mesh = _bombMesh, MaterialOverride = _bombMaterial };
                _raidRoot.AddChild(node);
                _bombNodes[bomb.Id] = node;
            }
            var (x, height, z) = Sim.BombPoint(bomb, stepFraction);
            node.Position = new Vector3(x, GroundAt(bomb.ImpactX, bomb.ImpactZ) + height, z);
        }
        if (_bombNodes.Count > falling.Count)
        {
            var landed = new List<int>();
            foreach (var id in _bombNodes.Keys) if (!falling.Contains(id)) landed.Add(id);
            foreach (int id in landed)
            {
                _bombNodes[id].QueueFree();
                _bombNodes.Remove(id);
            }
        }

        // The bombs that have landed since the last frame: a flash and a scar, the size of the blast.
        if (Sim.ImpactCount < _impactsSeen) _impactsSeen = 0; // a new game
        for (int id = Math.Max(_impactsSeen, Sim.ImpactsDropped); id < Sim.ImpactCount; id++)
        {
            var impact = Sim.Impacts[id - Sim.ImpactsDropped];
            float radius = Sim.WarTuning.Bombing.BlastRadius;
            float ground = GroundAt(impact.X, impact.Z);
            var flash = new MeshInstance3D { Mesh = _flashMesh, MaterialOverride = _flashMaterial, Position = new Vector3(impact.X, ground + 2f, impact.Z) };
            _raidRoot.AddChild(flash);
            _flashes.Add((flash, 0));
            _scarMesh ??= new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 0.2f };
            var scar = new MeshInstance3D { Mesh = _scarMesh, MaterialOverride = _scarMaterial, Position = new Vector3(impact.X, ground + 0.15f, impact.Z), Scale = new Vector3(radius * 0.3f, 1f, radius * 0.3f) };
            _raidRoot.AddChild(scar);
            _scars.Add((scar, 0));
        }
        _impactsSeen = Sim.ImpactCount;

        for (int k = _flashes.Count - 1; k >= 0; k--)
        {
            var (flash, age) = _flashes[k];
            age += aging;
            if (age >= FlashSeconds) { flash.QueueFree(); _flashes.RemoveAt(k); continue; }
            flash.Scale = Vector3.One * (1f + 11f * (float)(age / FlashSeconds));
            _flashes[k] = (flash, age);
        }
        for (int k = _scars.Count - 1; k >= 0; k--)
        {
            var (scar, age) = _scars[k];
            age += aging;
            if (age >= ScarSeconds) { scar.QueueFree(); _scars.RemoveAt(k); continue; }
            _scars[k] = (scar, age);
        }
    }
}
