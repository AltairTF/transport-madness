using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace TransportMadness;

/// <summary>
/// What the performance panel (<c>perf_overlay.gd</c>) asks of the world: the time each part of a frame took on the CPU, counts of what is drawn, and switches that
/// hide a layer or a cost so that its price can be seen by taking it away. Nothing here touches the simulation.
/// </summary>
public partial class SimHost
{
    sealed class PerfSlot { public double Sum, Max; }

    readonly Dictionary<string, PerfSlot> _perf = new();
    readonly HashSet<string> _layersOff = new();
    int _perfFrames;

    static readonly string[] LayerList =
    {
        "terrain", "terrain grain", "water", "water ripples", "cutaway", "roads and tracks", "trees", "town buildings", "industries and gateways",
        "vehicles", "trains", "names",
    };

    static long PerfStart() => Stopwatch.GetTimestamp();

    /// <summary>Adds the time since <paramref name="since"/> to a section and returns the time now, for the next section.</summary>
    long PerfLap(string name, long since)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = (now - since) * 1000.0 / Stopwatch.Frequency;
        if (!_perf.TryGetValue(name, out var slot)) _perf[name] = slot = new PerfSlot();
        slot.Sum += ms;
        if (ms > slot.Max) slot.Max = ms;
        return now;
    }

    void PerfFrameDone(long frameStart)
    {
        PerfLap("process total", frameStart);
        _perfFrames++;
    }

    /// <summary>The CPU time of each section of <c>_Process</c> since the last call: {name: [average ms a frame, worst ms]}, and then it starts counting again.</summary>
    public Godot.Collections.Dictionary PerfTimings()
    {
        var result = new Godot.Collections.Dictionary();
        int frames = Math.Max(1, _perfFrames);
        foreach (var (name, slot) in _perf)
            result[name] = new Godot.Collections.Array { slot.Sum / frames, slot.Max };
        _perf.Clear();
        _perfFrames = 0;
        return result;
    }

    /// <summary>What is on the map now: counts that explain a cost.</summary>
    public Godot.Collections.Dictionary PerfCounts()
    {
        int buildings = 0;
        foreach (var b in Sim.Buildings) if (b.State != TransportMadness.Sim.BuildingState.Demolished) buildings++;
        return new Godot.Collections.Dictionary
        {
            ["trees"] = _treeCount,
            ["vehicles"] = Sim.VehicleCount,
            ["buildings"] = buildings,
            ["industries"] = Sim.Industries.Count,
            ["roads"] = Sim.Network.RoadCount,
            ["terrain chunks"] = _terrainChunks.Length,
            ["sim tick"] = Sim.Tick,
        };
    }

    public string[] PerfLayers() => LayerList;

    public bool PerfLayerOn(string name) => !_layersOff.Contains(name);

    /// <summary>Shows or hides a layer, or a cost inside one (the grain of the ground, the ripples of the water).</summary>
    public void PerfSetLayer(string name, bool on)
    {
        if (on) _layersOff.Remove(name); else _layersOff.Add(name);
        switch (name)
        {
            case "terrain": _terrainRoot.Visible = on; break;
            case "terrain grain": _terrainMaterial.SetShaderParameter("grain_on", on); break;
            case "water": _water.Visible = on; break;
            case "water ripples": _waterMaterial.NormalEnabled = on; break;
            case "cutaway": _cutaway.Visible = on; break;
            case "roads and tracks": _roadRoot.Visible = on; _railRoot.Visible = on; break;
            case "trees": if (_treeRoot != null) _treeRoot.Visible = on && _overlay == MapOverlay.None; break;
            case "town buildings": _buildingModels.Root.Visible = on; GetNodeOrNull<Node3D>("Buildings")?.SetVisible(on); break;
            case "industries and gateways": _industryRoot.Visible = on; break;
            case "vehicles":
                _vehicleModels.Root.Visible = on; _crewModels.Root.Visible = on;
                foreach (var n in new[] { "Vehicles", "Crews", "LineMarkers" }) GetNodeOrNull<Node3D>(n)?.SetVisible(on);
                break;
            case "trains":
                _trainModels.Root.Visible = on;
                foreach (var n in new[] { "TrainCars", "TrainMarkers" }) GetNodeOrNull<Node3D>(n)?.SetVisible(on);
                break;
            case "names": _townLabels.Visible = on; break;
        }
    }
}
