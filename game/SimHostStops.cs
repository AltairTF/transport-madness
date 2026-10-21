using System;
using System.Linq;
using Godot;
using TransportMadness.Sim;

namespace TransportMadness;

/// <summary>
/// Line stops at industries and road gateways, and the route preview of the new line tool (M6f): what is under the mouse as a possible stop, the stop made for
/// an industry when a line is made through it, the leg between two stops as the vehicles would drive it, and the options of each stop of a line.
/// </summary>
public partial class SimHost
{
    /// <summary>The name of a stop at an industry or gateway: "Sawmill stop", with its number among the industries of the type when there are several ("Sawmill 2 stop").</summary>
    string DockName(Station station)
    {
        if (station.DockKind == 2) return $"{Sim.Gateways[station.DockId].Name} stop";
        var industry = Sim.Industries[station.DockId];
        string name = Sim.IndustryTuning.Types[industry.Type].Name;
        int ordinal = Sim.Industries.Count(i => i.Type == industry.Type && i.Id <= industry.Id);
        return ordinal > 1 ? $"{name} {ordinal} stop" : $"{name} stop";
    }

    string StopLabel(StopKind kind, int id) =>
        kind == StopKind.Station ? StationName(id) : kind == StopKind.Gateway ? $"{Sim.Gateways[id].Name}" : Sim.IndustryTuning.Types[Sim.Industries[id].Type].Name;

    /// <summary>
    /// The place a line stop could be near (x, z): an open road station within <paramref name="stationRadius"/> of the point, else an industry or road gateway whose
    /// square is within <paramref name="margin"/> of it, nearest first. {found, kind (0 station, 1 industry, 2 gateway), id, x, y, z (where vehicles stop: the station's node
    /// or the access point), name, ready (a road reaches it)}. The stop of an industry that has one already is the industry's.
    /// </summary>
    public Godot.Collections.Dictionary PickStop(float x, float z, float stationRadius, float margin)
    {
        StopKind kind = StopKind.Station;
        int id = -1;
        float best = float.MaxValue;
        foreach (var station in Sim.Stations)
        {
            if (!station.Active || station.Mode != StationMode.Road) continue;
            var (sx, sz) = Sim.PointOf(station);
            float d = MathF.Sqrt((sx - x) * (sx - x) + (sz - z) * (sz - z));
            if (d > stationRadius || d >= best) continue;
            best = d;
            (kind, id) = station.DockKind == 0 ? (StopKind.Station, station.Id) : station.DockKind == 2 ? (StopKind.Gateway, station.DockId) : (StopKind.Industry, station.DockId);
        }
        foreach (var industry in Sim.Industries)
        {
            float d = MathF.Sqrt((industry.X - x) * (industry.X - x) + (industry.Z - z) * (industry.Z - z)) - Sim.IndustryTuning.Types[industry.Type].Size;
            if (d > margin || d >= best) continue;
            best = d;
            (kind, id) = (StopKind.Industry, industry.Id);
        }
        foreach (var gateway in Sim.Gateways)
        {
            if (gateway.Kind != GatewayKind.Road) continue;
            float d = MathF.Sqrt((gateway.X - x) * (gateway.X - x) + (gateway.Z - z) * (gateway.Z - z)) - Sim.GatewayTuning.Size;
            if (d > margin || d >= best) continue;
            best = d;
            (kind, id) = (StopKind.Gateway, gateway.Id);
        }
        if (id < 0) return new Godot.Collections.Dictionary { ["found"] = false };
        var (px, pz) = kind == StopKind.Station ? Sim.PointOf(Sim.Stations[id]) : Sim.AccessPointOf(new AccessRef(kind == StopKind.Gateway, id));
        return new Godot.Collections.Dictionary
        {
            ["found"] = true, ["kind"] = (int)kind, ["id"] = id, ["x"] = px, ["y"] = HeightAt(px, pz), ["z"] = pz, ["name"] = StopLabel(kind, id), ["ready"] = Sim.StopNodeOf(kind, id) >= 0,
        };
    }

    /// <summary>Makes the stop of an industry or road gateway a line can use (free), or finds it again. Its station id, -1 when no road reaches it yet.</summary>
    public int AddDock(bool gateway, int id) => Sim.Execute(new AddDockCommand(gateway, id));

    /// <summary>Creates a line through stops, each {kind, id} as <see cref="PickStop"/> gives them; an industry or gateway gets its stop first. The line's id, -1 when refused.</summary>
    public int CreateLineFromStops(Godot.Collections.Array<Godot.Collections.Dictionary> stops)
    {
        var ids = new int[stops.Count];
        for (int k = 0; k < stops.Count; k++)
        {
            var kind = (StopKind)(int)stops[k]["kind"];
            int id = (int)stops[k]["id"];
            ids[k] = kind == StopKind.Station ? id : AddDock(kind == StopKind.Gateway, id);
            if (ids[k] < 0) return -1;
        }
        return CreateLine(ids);
    }

    /// <summary>
    /// The leg between two stops as the vehicles would drive it (<see cref="Simulation.RouteBetween"/>): {valid (false when a stop has no road), reachable, seconds, length, points (on the ground,
    /// the route, or a straight line when there is none), cargo (names of what the first stop makes that the second takes)}.
    /// </summary>
    public Godot.Collections.Dictionary LineLeg(int fromKind, int fromId, int toKind, int toId)
    {
        if (Sim.RouteBetween((StopKind)fromKind, fromId, (StopKind)toKind, toId) is not { } leg) return new Godot.Collections.Dictionary { ["valid"] = false };
        var points = new Vector3[leg.Points.Count];
        for (int k = 0; k < points.Length; k++) points[k] = new Vector3(leg.Points[k].X, HeightAt(leg.Points[k].X, leg.Points[k].Z), leg.Points[k].Z);
        var cargo = new Godot.Collections.Array<string>();
        foreach (int c in leg.Cargo) cargo.Add(Sim.CargoTypes[c].Name);
        return new Godot.Collections.Dictionary { ["valid"] = true, ["reachable"] = leg.Reachable, ["seconds"] = leg.Seconds, ["length"] = leg.Length, ["points"] = points, ["cargo"] = cargo };
    }

    /// <summary>Sets what vehicles do at one stop of a line: load (and which cargo, -1 for any), unload, and the share of what was taken on at the last loading stop that is unloaded (percent, -1 for all).</summary>
    public bool SetStopOptions(int line, int index, bool load, int cargo, bool unload, int share) =>
        Sim.Execute(new SetStopOptionsCommand(line, index, load, cargo, unload, share)) == 1;

    /// <summary>
    /// The industries and road gateways a road reaches and that have no stop yet, as places to add to a line: for each {code (1000000 + 100000 * kind + id), name}.
    /// A stop is made when the line is changed (<see cref="StopFromCode"/>).
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetStopPlaces()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var industry in Sim.Industries)
            if (Sim.StopNodeOf(StopKind.Industry, industry.Id) >= 0 && !HasDock(1, industry.Id))
                list.Add(new Godot.Collections.Dictionary { ["code"] = StopCode(StopKind.Industry, industry.Id), ["name"] = PlaceName(StopKind.Industry, industry.Id) });
        foreach (var gateway in Sim.Gateways)
            if (gateway.Kind == GatewayKind.Road && Sim.StopNodeOf(StopKind.Gateway, gateway.Id) >= 0 && !HasDock(2, gateway.Id))
                list.Add(new Godot.Collections.Dictionary { ["code"] = StopCode(StopKind.Gateway, gateway.Id), ["name"] = PlaceName(StopKind.Gateway, gateway.Id) });
        return list;
    }

    bool HasDock(int dockKind, int id) => Sim.Stations.Any(s => s.Active && s.DockKind == dockKind && s.DockId == id);

    static int StopCode(StopKind kind, int id) => 1_000_000 + 100_000 * (int)kind + id;

    string PlaceName(StopKind kind, int id)
    {
        if (kind == StopKind.Gateway) return $"{Sim.Gateways[id].Name} (gateway stop)";
        var type = Sim.Industries[id].Type;
        int ordinal = Sim.Industries.Count(i => i.Type == type && i.Id <= id);
        string name = Sim.IndustryTuning.Types[type].Name;
        return ordinal > 1 ? $"{name} {ordinal} (stop)" : $"{name} (stop)";
    }

    /// <summary>The station id a stop of a line stands for: a station id as it is, or for a code from <see cref="GetStopPlaces"/> the stop of that place, made now. -1 when none can be.</summary>
    public int StopFromCode(int code)
    {
        if (code < 1_000_000) return code;
        int kind = (code - 1_000_000) / 100_000, id = (code - 1_000_000) % 100_000;
        return AddDock(kind == (int)StopKind.Gateway, id);
    }

    /// <summary>What a line does at each of its stops: for each stop {load, cargo (-1 any), unload, share (-1 all), dock (the stop is at an industry or gateway)}.</summary>
    Godot.Collections.Array<Godot.Collections.Dictionary> StopOptionsOf(Line line)
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        for (int k = 0; k < line.Stops.Count; k++)
        {
            var o = line.OptionsAt(k);
            list.Add(new Godot.Collections.Dictionary { ["load"] = o.Load, ["cargo"] = o.LoadCargo, ["unload"] = o.Unload, ["share"] = o.Share, ["dock"] = Sim.Stations[line.Stops[k]].DockKind != 0 });
        }
        return list;
    }
}
