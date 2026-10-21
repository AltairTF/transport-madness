using System.Linq;

namespace TransportMadness.Sim;

/// <summary>
/// A place where company vehicles load and unload, standing at a road node. Vehicles stop on the lanes that run into the
/// node, just short of its junction. Until towns exist (M3) it produces each cargo at a fixed rate.
/// </summary>
public sealed class Station
{
    internal Station(int id, int node, int cargoTypes)
    {
        Id = id;
        Node = node;
        Waiting = new float[cargoTypes];
    }

    /// <summary>A station on the rail network, standing at a rail node.</summary>
    internal Station(int id, int railNode, int cargoTypes, float x, float z)
    {
        Id = id;
        Node = -1;
        Mode = StationMode.Rail;
        RailNode = railNode;
        X = x;
        Z = z;
        Waiting = new float[cargoTypes];
    }

    public int Id { get; }
    /// <summary>Whether road vehicles or trains stop here.</summary>
    public StationMode Mode { get; } = StationMode.Road;
    /// <summary>What a stop at an industry or a gateway is (M6f): 0 for an ordinary station, 1 for the stop at an industry's access point, 2 for one at a road gateway's. It covers only that industry or gateway, not the buildings around, and stands at its access node.</summary>
    public int DockKind { get; internal set; }
    /// <summary>The industry or gateway id a stop (<see cref="DockKind"/> not 0) belongs to.</summary>
    public int DockId { get; internal set; }
    /// <summary>The rail node a rail station stands at, -1 for a road station.</summary>
    public int RailNode { get; internal set; } = -1;
    /// <summary>Where a rail station stands on the map (a road station's place is that of its node).</summary>
    public float X { get; internal set; }
    public float Z { get; internal set; }
    /// <summary>The name the player gave it, null while it has none (the game then names it for its town). Not part of the state hash.</summary>
    public string? Name { get; internal set; }
    /// <summary>The node it stands at; follows the node when that is moved.</summary>
    public int Node { get; internal set; }
    /// <summary>False once every road at its node has been torn down.</summary>
    public bool Active { get; internal set; } = true;
    /// <summary>The tick until which a raid keeps it shut, 0 when it is not (C12): nothing is loaded or unloaded and no cargo is put into it meanwhile.</summary>
    public long ShutUntil { get; internal set; }
    /// <summary>Whether loading vehicles pull out of the lane into bays (<see cref="StationTuning.BayCapacity"/> at a time), so they do not block it.</summary>
    public bool HasBays { get; internal set; }
    /// <summary>Vehicles in the bays now.</summary>
    public int InBay { get; internal set; }
    /// <summary>Cargo waiting to be picked up, by cargo type (index into <see cref="Tuning.Cargo"/>). Fractions build up between whole units.</summary>
    public float[] Waiting { get; }
    /// <summary>People who boarded a bus here, got off one here, or gave up waiting here: today so far and the whole day before. Not part of the state hash.</summary>
    public PeopleCounts Today { get; internal set; }
    public PeopleCounts Yesterday { get; internal set; }
    // Passengers waiting for a ride to another station (a map with towns); their total is mirrored in Waiting[passengers].
    internal List<PaxLot> Queue { get; } = new();

    /// <summary>People waiting here, by the station they are going to.</summary>
    public IReadOnlyDictionary<int, int> WaitingFor()
    {
        var by = new SortedDictionary<int, int>();
        foreach (var lot in Queue) by[lot.Dest] = by.GetValueOrDefault(lot.Dest) + lot.Count;
        return by;
    }
}

/// <summary>How many people boarded, got off and gave up at a station in a day.</summary>
public readonly record struct PeopleCounts(int Boarded, int Alighted, int GaveUp);

/// <summary>Which network a station or a line is on.</summary>
public enum StationMode : byte { Road, Rail }

/// <summary>
/// What a vehicle does at one stop of a line (M6f). By default it unloads everything the place takes and loads any cargo the next stop accepts.
/// <see cref="Share"/> is the percentage of what it took at the last loading stop that it unloads here (-1: unset, which unloads everything).
/// </summary>
public readonly record struct StopOptions(bool Load, int LoadCargo, bool Unload, int Share)
{
    public static readonly StopOptions Default = new(true, -1, true, -1);
    public bool IsDefault => this == Default;
}

/// <summary>A recurring route: the company's vehicles on it visit the stations in order, and start over after the last.</summary>
public sealed class Line
{
    internal Line(int id, int[] stops, StationMode mode = StationMode.Road)
    {
        Id = id;
        Stops = stops;
        Mode = mode;
        Name = $"Line {id + 1}";
        StopLoad = Enumerable.Repeat(true, stops.Length).ToArray();
        StopLoadCargo = Enumerable.Repeat(-1, stops.Length).ToArray();
        StopUnload = Enumerable.Repeat(true, stops.Length).ToArray();
        StopShare = Enumerable.Repeat(-1, stops.Length).ToArray();
    }

    // What a vehicle does at each stop (M6f), by the index of the stop in the line: load, which cargo (-1 any), unload, and the share (-1 unset).
    internal bool[] StopLoad;
    internal int[] StopLoadCargo;
    internal bool[] StopUnload;
    internal int[] StopShare;

    /// <summary>What a vehicle does at the stop at <paramref name="index"/>.</summary>
    public StopOptions OptionsAt(int index) => new(StopLoad[index], StopLoadCargo[index], StopUnload[index], StopShare[index]);

    /// <summary>Whether any stop has options other than the defaults.</summary>
    public bool HasOptions
    {
        get
        {
            for (int k = 0; k < StopLoad.Length; k++) if (!OptionsAt(k).IsDefault) return true;
            return false;
        }
    }

    public int Id { get; }
    /// <summary>Buses and trucks run road lines, trains rail lines; all its stations are of the same mode.</summary>
    public StationMode Mode { get; }
    /// <summary>Chosen by the player; not part of the state hash.</summary>
    public string Name { get; internal set; }
    /// <summary>The colour the player chose (0xRRGGBB), -1 while it has none (the game then picks one from its number). Not part of the state hash.</summary>
    public int Color { get; internal set; } = -1;
    /// <summary>The station ids in order; changes when the line is edited.</summary>
    public IReadOnlyList<int> Stops { get; internal set; }
    /// <summary>False once deleted; its vehicles are sold.</summary>
    public bool Active { get; internal set; } = true;
    /// <summary>Paid for cargo delivered by this line's vehicles.</summary>
    public long Income { get; internal set; }
    /// <summary>Upkeep paid for this line's vehicles.</summary>
    public long Upkeep { get; internal set; }
    /// <summary>Units of cargo delivered.</summary>
    public long Delivered { get; internal set; }
    public long Profit => Income - Upkeep;
    /// <summary>The maintenance policy set on this line; <see cref="PolicyMode.Inherit"/> leaves the choice to the company's.</summary>
    public MaintenancePolicy Policy { get; internal set; }
}

/// <summary>What a company vehicle is doing, for drawing and for the vehicle window.</summary>
/// <param name="Line">The line it runs, -1 for ambient traffic.</param>
/// <param name="Model">Index into <see cref="Tuning.Vehicles"/>.</param>
/// <param name="NextStop">Index into the line's stops of the station it is heading for (or standing at).</param>
/// <param name="Loading">Standing at a station.</param>
/// <param name="Load">Units of cargo on board.</param>
/// <param name="Cargo">What it is (index into the cargo list), -1 when empty.</param>
public readonly record struct FleetInfo(int Line, int Model, int NextStop, bool Loading, int Load, int Cargo = -1);
