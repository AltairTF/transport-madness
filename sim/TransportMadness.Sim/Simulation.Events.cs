namespace TransportMadness.Sim;

public enum SimEventKind : byte
{
    /// <summary>A company vehicle broke down on the road.</summary>
    Breakdown,
    /// <summary>A train broke down.</summary>
    TrainBreakdown,
    /// <summary>A vehicle is due for a repair that no depot it can reach can do.</summary>
    NoDepot,
    /// <summary>A rail signal failed.</summary>
    SignalFailure,
    /// <summary>A company vehicle or train crashed.</summary>
    Crash,
    /// <summary>A town is unhappy about an incident of the company's.</summary>
    TownUnhappy,
    /// <summary>An import shipment has arrived at a gateway.</summary>
    ShipmentArrived,
    /// <summary>The government's standing is low, the committee was dismissed, or the campaign is over.</summary>
    Standing,
    /// <summary>The government offered an order, or one was met, paid late, failed or lapsed.</summary>
    Order,
    /// <summary>The region's industries are short of power, or the shortage is over.</summary>
    Power,
    /// <summary>A development of the war: rationing, a call-up, a requisition, a raid.</summary>
    War,
    /// <summary>A raid: where the bombs fell and what they did.</summary>
    Raid,
}

public enum EventSeverity : byte { Info, Warning, Serious }

/// <summary>Something the player needs to see: an incident, a breakdown, a warning. <see cref="X"/> and <see cref="Z"/> are where it happened, for the camera; both are NaN for news without a place (a call-up, the standing, power).</summary>
public readonly record struct SimEvent(int Id, long Tick, SimEventKind Kind, EventSeverity Severity, float X, float Z, int Subject, string Text)
{
    /// <summary>False for news that happened nowhere in particular, so the camera has nowhere to go.</summary>
    public bool HasPlace => !float.IsNaN(X) && !float.IsNaN(Z);
}

/// <summary>The event feed: what the player should be told, newest last. Not part of the state hash (it is made from what the simulation does).</summary>
public sealed partial class Simulation
{
    const int EventsKept = 300;

    readonly List<SimEvent> _events = new();
    int _eventBase;

    /// <summary>The events still remembered, oldest first.</summary>
    public IReadOnlyList<SimEvent> Events => _events;
    /// <summary>How many events there have been in all; the one with id n is at <c>n - EventsDropped</c> in <see cref="Events"/>.</summary>
    public int EventCount => _eventBase + _events.Count;
    public int EventsDropped => _eventBase;

    void AddEvent(SimEventKind kind, EventSeverity severity, float x, float z, int subject, string text)
    {
        _events.Add(new SimEvent(EventCount, Tick, kind, severity, x, z, subject, text));
        if (_events.Count > EventsKept + 50)
        {
            _events.RemoveRange(0, 50);
            _eventBase += 50;
        }
    }

    /// <summary>News without a place on the map (the origin is a real place, the middle of the map).</summary>
    void AddEvent(SimEventKind kind, EventSeverity severity, int subject, string text) => AddEvent(kind, severity, float.NaN, float.NaN, subject, text);
}
