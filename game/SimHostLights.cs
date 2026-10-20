using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>Traffic lights at road junctions (M4): a lamp on a post at each approach, green or red as the simulation says.</summary>
public partial class SimHost
{
    Node3D? _lightRoot;
    long _lightSignature = -1;
    Simulation? _lightSim;
    readonly List<(StandardMaterial3D Lamp, int Lane)> _lightLamps = new();

    /// <summary>Where lights would go for a click: {valid, x, y, z, price, affordable}.</summary>
    public Godot.Collections.Dictionary LightPreview(float x, float z)
    {
        var net = Sim.Network;
        int node = net.FindNodeNear(x, z, 20f, withRoadsOnly: true);
        if (node < 0 || net.RoadsAt(node).Count < 3 || Sim.LightAt(node) != null) return new Godot.Collections.Dictionary { ["valid"] = false };
        var (nx, nz) = net.GetNode(node);
        long price = Sim.TrafficLightPrice;
        return new Godot.Collections.Dictionary { ["valid"] = true, ["x"] = nx, ["y"] = net.GetNodeHeight(node), ["z"] = nz, ["price"] = price, ["affordable"] = Sim.CanAfford(price) };
    }

    /// <summary>The junction with lights near a point, as {found, x, y, z}.</summary>
    public Godot.Collections.Dictionary LightAt(float x, float z)
    {
        var net = Sim.Network;
        int node = net.FindNodeNear(x, z, 20f, withRoadsOnly: true);
        if (node < 0 || Sim.LightAt(node) == null) return new Godot.Collections.Dictionary { ["found"] = false };
        var (nx, nz) = net.GetNode(node);
        return new Godot.Collections.Dictionary { ["found"] = true, ["x"] = nx, ["y"] = net.GetNodeHeight(node), ["z"] = nz };
    }

    public bool BuildTrafficLight(float x, float z)
    {
        int node = Sim.Network.FindNodeNear(x, z, 20f, withRoadsOnly: true);
        return node >= 0 && Sim.Execute(new BuildTrafficLightCommand(node)) == 1;
    }

    public bool RemoveTrafficLight(float x, float z)
    {
        int node = Sim.Network.FindNodeNear(x, z, 20f, withRoadsOnly: true);
        return node >= 0 && Sim.Execute(new RemoveTrafficLightCommand(node)) == 1;
    }

    void DrawLights()
    {
        _lightRoot ??= new Node3D { Name = "TrafficLights" };
        if (_lightRoot.GetParent() == null) AddChild(_lightRoot);
        long signature = ((long)Sim.Network.Version << 20) ^ (long)Sim.TrafficLights.Count(l => l.Active) ^ ((long)Sim.TrafficLights.Count << 10);
        if (!ReferenceEquals(_lightSim, Sim) || signature != _lightSignature)
        {
            _lightSim = Sim;
            _lightSignature = signature;
            foreach (var child in _lightRoot.GetChildren()) child.QueueFree();
            _lightLamps.Clear();
            var net = Sim.Network;
            foreach (var light in Sim.TrafficLights)
            {
                if (!light.Active) continue;
                var (nx, nz) = net.GetNode(light.Node);
                float y = net.GetNodeHeight(light.Node);
                foreach (var laneId in net.IncomingLanes(light.Node))
                {
                    var lane = net.GetLane(laneId);
                    if (lane.Index != 0) continue; // one lamp for each road, at the right-most lane
                    float heading = lane.Path.HeadingAt(lane.Length);
                    float dx = MathF.Cos(heading), dz = MathF.Sin(heading);
                    float side = lane.SideOffset + 2.2f;
                    var foot = new Vector3(nx - dx * 11f - dz * side, y, nz - dz * 11f + dx * side);
                    var post = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.25f, 4.6f, 0.25f) }, MaterialOverride = _postMaterial, Position = foot + new Vector3(0, 2.3f, 0) };
                    _lightRoot.AddChild(post);
                    var lamp = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.85f, 0.25f), EmissionEnabled = true, Emission = new Color(0.1f, 0.85f, 0.25f) };
                    var head = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.6f, 0.9f, 0.6f) }, MaterialOverride = lamp, Position = foot + new Vector3(0, 4.5f, 0) };
                    _lightRoot.AddChild(head);
                    _lightLamps.Add((lamp, laneId.Value));
                }
            }
        }
        if (Sim.Tick % 5 != 0) return;
        foreach (var (lamp, lane) in _lightLamps)
        {
            var colour = Sim.LaneHasGreen(new LaneId(lane)) ? new Color(0.1f, 0.85f, 0.25f) : new Color(0.95f, 0.1f, 0.1f);
            lamp.AlbedoColor = colour;
            lamp.Emission = colour;
        }
    }
}
