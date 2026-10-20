using System;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>Priority, yield and stop signs at road junctions (M4): a plate on a post beside the right-most lane of the approach.</summary>
public partial class SimHost
{
    Node3D? _signRoot;
    long _signSignature = -1;
    Simulation? _signSim;
    StandardMaterial3D? _stopMaterial, _yieldMaterial, _priorityMaterial;

    static RoadSignKind KindOf(int kind) => kind switch { 0 => RoadSignKind.Priority, 1 => RoadSignKind.Yield, _ => RoadSignKind.Stop };

    /// <summary>Where the post of a sign on an approach stands: a little before the junction, at the right of the right-most lane.</summary>
    (Vector3 Foot, float Heading) PostOf(Lane lane)
    {
        var net = Sim.Network;
        var (nx, nz) = net.GetNode(lane.EndNode);
        float y = net.GetNodeHeight(lane.EndNode);
        float heading = lane.Path.HeadingAt(lane.Length);
        float dx = MathF.Cos(heading), dz = MathF.Sin(heading);
        float side = lane.SideOffset + 2.4f;
        return (new Vector3(nx - dx * 13f - dz * side, y, nz - dz * 13f + dx * side), heading);
    }

    /// <summary>Where a sign would go for a click: {valid, x, y, z, price, affordable}. <paramref name="kind"/> is 0 priority, 1 yield, 2 stop.</summary>
    public Godot.Collections.Dictionary SignPreview(float x, float z, int kind)
    {
        var approach = Sim.ApproachNear(x, z);
        if (approach.IsNone || Sim.LightAt(Sim.Network.GetLane(approach).EndNode) != null) return new Godot.Collections.Dictionary { ["valid"] = false };
        var lane = Sim.Network.GetLane(approach);
        var (foot, _) = PostOf(lane);
        long price = Sim.RoadSignPrice(KindOf(kind));
        if (Sim.SignOnApproach(approach) == KindOf(kind)) return new Godot.Collections.Dictionary { ["valid"] = false };
        return new Godot.Collections.Dictionary { ["valid"] = true, ["x"] = foot.X, ["y"] = foot.Y, ["z"] = foot.Z, ["price"] = price, ["affordable"] = Sim.CanAfford(price) };
    }

    /// <summary>The sign on the approach nearest a point, as {found, x, y, z}.</summary>
    public Godot.Collections.Dictionary SignAt(float x, float z)
    {
        var approach = Sim.ApproachNear(x, z);
        if (approach.IsNone || Sim.SignOnApproach(approach) == null) return new Godot.Collections.Dictionary { ["found"] = false };
        var (foot, _) = PostOf(Sim.Network.GetLane(approach));
        return new Godot.Collections.Dictionary { ["found"] = true, ["x"] = foot.X, ["y"] = foot.Y, ["z"] = foot.Z };
    }

    public bool BuildRoadSign(float x, float z, int kind) => Sim.Execute(new BuildRoadSignCommand(x, z, KindOf(kind))) > 0;

    public bool RemoveRoadSign(float x, float z) => Sim.Execute(new RemoveRoadSignCommand(x, z)) > 0;

    void DrawSigns()
    {
        _signRoot ??= new Node3D { Name = "RoadSigns" };
        if (_signRoot.GetParent() == null) AddChild(_signRoot);
        long signature = ((long)Sim.Network.Version << 20) ^ (long)Sim.RoadSigns.Count(s => s.Active) ^ ((long)Sim.RoadSigns.Count << 10) ^ ((long)Sim.RoadSigns.Where(s => s.Active).Sum(s => (int)s.Kind * (s.Id + 1)) << 40)
            ^ ((long)Sim.TrafficLights.Count(l => l.Active) << 52);
        if (ReferenceEquals(_signSim, Sim) && signature == _signSignature) return;
        _signSim = Sim;
        _signSignature = signature;
        foreach (var child in _signRoot.GetChildren()) child.QueueFree();
        _stopMaterial ??= Flat(new Color(0.78f, 0.08f, 0.08f));
        _yieldMaterial ??= Flat(new Color(0.95f, 0.95f, 0.95f));
        _priorityMaterial ??= Flat(new Color(0.98f, 0.8f, 0.1f));
                var net = Sim.Network;
        foreach (var sign in Sim.RoadSigns)
        {
            if (!sign.Active || Sim.LightAt(sign.Node) != null) continue;
            foreach (var laneId in Sim.LanesOf(sign))
            {
                var lane = net.GetLane(laneId);
                if (lane.Index != 0) continue; // one post for each road, at the right-most lane
                var (foot, heading) = PostOf(lane);
                AddSignBox(foot + new Vector3(0, 1.4f, 0), heading, new Vector3(0.12f, 2.8f, 0.12f), _postMaterial);
                var top = foot + new Vector3(0, 3.1f, 0);
                // Flat boxes, thin along the road: a red plate for stop, a white one with a red border for yield, a yellow diamond for priority.
                switch (sign.Kind)
                {
                    case RoadSignKind.Stop:
                        AddSignBox(top, heading, new Vector3(0.08f, 0.9f, 0.9f), _stopMaterial);
                        AddSignBox(top + SignForward(heading, 0.06f), heading, new Vector3(0.05f, 0.2f, 0.7f), _yieldMaterial);
                        break;
                    case RoadSignKind.Yield:
                        AddSignBox(top, heading, new Vector3(0.08f, 1.0f, 1.0f), _stopMaterial);
                        AddSignBox(top + SignForward(heading, 0.06f), heading, new Vector3(0.05f, 0.62f, 0.62f), _yieldMaterial);
                        break;
                    default:
                        AddSignBox(top, heading, new Vector3(0.08f, 0.95f, 0.95f), _yieldMaterial, Mathf.Pi / 4f);
                        AddSignBox(top + SignForward(heading, 0.06f), heading, new Vector3(0.05f, 0.75f, 0.75f), _priorityMaterial, Mathf.Pi / 4f);
                        break;
                }
            }
        }
    }

    void AddSignBox(Vector3 centre, float heading, Vector3 size, Material material, float roll = 0f)
    {
        var box = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = material };
        box.Transform = new Transform3D(new Basis(Vector3.Up, -heading) * new Basis(Vector3.Right, roll), centre);
        _signRoot!.AddChild(box);
    }

    /// <summary>A step of <paramref name="distance"/> against the direction of travel, towards the vehicles that read the plate.</summary>
    static Vector3 SignForward(float heading, float distance) => new(-MathF.Cos(heading) * distance, 0f, -MathF.Sin(heading) * distance);
}
