namespace TransportMadness.Sim;

/// <summary>How a trip group gets from one place to another. <see cref="None"/> is a trip that could not be made.</summary>
public enum TripMode : byte { None, Walk, Car, Transit }

/// <summary>The four trips of a group's day, in order: out to work, back, out for leisure, back.</summary>
public enum TripLeg : byte { WorkOut, WorkBack, LeisureOut, LeisureBack }

/// <summary>
/// People who travel together from a home to a workplace and a leisure place and back, on a schedule drawn from the
/// seed. No identity beyond the group: the sim never follows a person.
/// </summary>
public sealed class TripGroup
{
    internal TripGroup(int id, int home, int people, ulong seed, bool hasCar)
    {
        Id = id;
        Home = home;
        People = people;
        Seed = seed;
        HasCar = hasCar;
    }

    public int Id { get; }
    /// <summary>Building the group lives in.</summary>
    public int Home { get; }
    public int People { get; internal set; }
    /// <summary>The home was demolished: the group is gone for good (its people and the jobs they held are released), and it never travels again.</summary>
    public bool Retired { get; internal set; }
    /// <summary>Workplace building, -1 when it has none.</summary>
    public int Work { get; internal set; } = -1;
    /// <summary>Commerce building it goes to in the evening, -1 when there is none.</summary>
    public int Leisure { get; internal set; } = -1;
    public bool HasCar { get; }
    internal ulong Seed { get; }
    /// <summary>The share of its people called up that has not been taken yet (it waits while the group is on a trip).</summary>
    internal float PendingCall;

    /// <summary>The trip it makes next (or is making).</summary>
    public TripLeg NextLeg { get; internal set; }
    /// <summary>The day the next trip belongs to.</summary>
    public long PlanDay { get; internal set; }
    /// <summary>Tick at which the next trip starts; <see cref="long.MaxValue"/> while it is on the way or has nowhere to go.</summary>
    public long NextTick { get; internal set; }
    public bool Travelling { get; internal set; }
    /// <summary>How the trip under way is made.</summary>
    public TripMode Mode { get; internal set; }

    // The trip under way.
    internal long DepartTick;
    internal int Pending;     // people not yet at the other end (or given up on the trip)
    internal int Arrived;
    internal int ToDispatch;  // by car: people still to be put on the road
    internal long NextSpawnTick;
    internal long ReachedMonth = -1; // the last month in which a trip out made within the time budget was completed
    internal float FreeRide;  // seconds the car trip would take on empty roads, to see how slow it really was
    internal int OriginNode = -1, DestNode = -1;
    internal int Origin = -1, Destination = -1; // buildings

    /// <summary>Seconds the last completed trip took, from leaving to the last of the group arriving.</summary>
    public float LastTripSeconds { get; internal set; }
    public TripMode LastMode { get; internal set; }
}

/// <summary>
/// People of one group waiting at a station for a ride to another, or on a bus bound for it. Passengers queue per
/// destination, so a bus only takes those whose destination is on its line.
/// </summary>
internal sealed class PaxLot
{
    internal PaxLot(int group, int count, int from, int dest, long since)
    {
        Group = group;
        Count = count;
        From = from;
        Dest = dest;
        Since = since;
    }

    internal int Group;
    internal int Count;
    internal int From;   // station they joined the queue at
    internal int Dest;   // station they are going to
    internal long Since; // tick they joined the queue
    internal long Boarded;
}
