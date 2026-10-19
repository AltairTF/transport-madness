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

    public int Id { get; }
    /// <summary>The node it stands at; follows the node when that is moved.</summary>
    public int Node { get; internal set; }
    /// <summary>False once every road at its node has been torn down.</summary>
    public bool Active { get; internal set; } = true;
    /// <summary>Cargo waiting to be picked up, by cargo type (index into <see cref="Tuning.Cargo"/>). Fractions build up between whole units.</summary>
    public float[] Waiting { get; }
}

/// <summary>A recurring route: the company's vehicles on it visit the stations in order, and start over after the last.</summary>
public sealed class Line
{
    internal Line(int id, int[] stops)
    {
        Id = id;
        Stops = stops;
    }

    public int Id { get; }
    public IReadOnlyList<int> Stops { get; }
    /// <summary>False once deleted; its vehicles are sold.</summary>
    public bool Active { get; internal set; } = true;
    /// <summary>Paid for cargo delivered by this line's vehicles.</summary>
    public long Income { get; internal set; }
    /// <summary>Upkeep paid for this line's vehicles.</summary>
    public long Upkeep { get; internal set; }
    /// <summary>Units of cargo delivered.</summary>
    public long Delivered { get; internal set; }
    public long Profit => Income - Upkeep;
}

/// <summary>What a company vehicle is doing, for drawing and for the vehicle window.</summary>
/// <param name="Line">The line it runs, -1 for ambient traffic.</param>
/// <param name="Model">Index into <see cref="Tuning.Vehicles"/>.</param>
/// <param name="NextStop">Index into the line's stops of the station it is heading for (or standing at).</param>
/// <param name="Loading">Standing at a station.</param>
/// <param name="Load">Units of cargo on board.</param>
/// <param name="Cargo">What it is (index into the cargo list), -1 when empty.</param>
public readonly record struct FleetInfo(int Line, int Model, int NextStop, bool Loading, int Load, int Cargo = -1);
