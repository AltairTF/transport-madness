using System;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// Picking against the world for the tools (M6d moved it out of SimHost.cs): where a camera ray meets the ground, and snapping a point
/// to the road network, a road or a station. What object a click opens is worked out in game/picking.gd from what the HUD shows.
/// </summary>
public partial class SimHost
{
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
    /// Snaps a point to the nearest junction or road end within <paramref name="radius"/> (not a bend inside a street, nor a bridge end, nor a node
    /// under another road), else to the nearest road's centre line (a junction will be built there), else leaves it alone.
    /// </summary>
    public Godot.Collections.Dictionary SnapToNode(float x, float z, float radius)
    {
        var net = Sim.Network;
        int node = net.FindSnapNode(x, z, radius);
        if (node >= 0)
        {
            var (nx, nz) = net.GetNode(node);
            return new Godot.Collections.Dictionary { ["snapped"] = true, ["x"] = nx, ["y"] = net.GetNodeHeight(node), ["z"] = nz };
        }

        if (net.NearestOnRoad(x, z, RoadSnapRadius, 5f) is { } hit)
            return new Godot.Collections.Dictionary { ["snapped"] = true, ["x"] = hit.X, ["y"] = hit.Y, ["z"] = hit.Z };
        return new Godot.Collections.Dictionary { ["snapped"] = false, ["x"] = x, ["y"] = HeightAt(x, z), ["z"] = z };
    }

    /// <summary>The road piece nearest to a point within 14 m: {found, road, type, length, structure, condition} (structure: 0 ground, 1 bridge, 2 tunnel).</summary>
    public Godot.Collections.Dictionary RoadAt(float x, float z)
    {
        if (Sim.Network.RoadAt(x, z, 14f) is not { } id) return new Godot.Collections.Dictionary { ["found"] = false };
        var road = Sim.Network.GetRoad(id);
        return new Godot.Collections.Dictionary { ["found"] = true, ["road"] = id.Value, ["type"] = road.Type switch { RoadType.Avenue2x2 => 1, RoadType.Path => 2, RoadType.Dirt => 3, RoadType.Highway => 4, _ => 0 }, ["length"] = road.Length, ["structure"] = (int)road.Structure, ["condition"] = Sim.RoadConditionOf(id.Value) };
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

    /// <summary>The open station nearest to a point within <paramref name="radius"/>, or -1.</summary>
    public int StationNear(float x, float z, float radius) => Sim.NearestStation(x, z, radius);
}
