namespace TransportMadness.Sim;

public enum BuildingType { Home, Commerce, Industry }

/// <summary>
/// What a building is doing. A building with no road near it is Inactive; after a while it is Abandoned, and after
/// another while the town demolishes it. A road that comes back puts it right.
/// </summary>
public enum BuildingState { Active, Inactive, Abandoned, Demolished }

public sealed class Building
{
    internal Building(int id, int town, BuildingType type, int tier, float x, float y, float z, float facing)
    {
        Id = id;
        Town = town;
        Type = type;
        Tier = tier;
        X = x;
        Y = y;
        Z = z;
        Facing = facing;
    }

    public int Id { get; }
    public int Town { get; }
    public BuildingType Type { get; }
    /// <summary>Size, 0 for the smallest.</summary>
    public int Tier { get; internal set; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    /// <summary>Heading (atan2 of z over x) of the direction its front faces, toward its street.</summary>
    public float Facing { get; }
    /// <summary>What it holds of each cargo (index into the cargo list) for its monthly needs; used up at the turn of each month.</summary>
    public float[] Sustain { get; internal set; } = Array.Empty<float>();
    /// <summary>What it holds of each cargo for building and growing (spent from M3d).</summary>
    public float[] Construction { get; internal set; } = Array.Empty<float>();
    public BuildingState State { get; internal set; } = BuildingState.Active;
    /// <summary>For a home: whether a group living there made a trip to work or leisure within the time budget in the last month counted (true until the first month is over). Always true for other kinds.</summary>
    public bool Reached { get; internal set; } = true;
    /// <summary>The day it went into its current state.</summary>
    public long StateSince { get; internal set; }
    // The road node cars leave from and arrive at, found on demand for the network version it was last worked out for.
    internal int AccessNode = -1;
    internal int AccessVersion = -1;
    /// <summary>Index into its town's lots of the lot it stands on, -1 when it stands on none (placed by hand).</summary>
    internal int Lot = -1;
}

/// <summary>A place in a town where a building can stand; free while no building stands on it.</summary>
internal sealed class Lot
{
    internal Lot(TownGenerator.LotSite site) => Site = site;
    internal TownGenerator.LotSite Site;
    internal int Building = -1;
}

/// <summary>Why a town did not grow last month, or <see cref="None"/> when it did (or had nothing to spend).</summary>
public enum GrowthBlock { None, Satisfaction, Connectivity }

/// <summary>What a town did at the last turn of the month.</summary>
public readonly record struct GrowthReport(int Upgrades, int NewBuildings, int NewBlocks, GrowthBlock Blocked);

public sealed class Town
{
    internal Town(int id, string name, float x, float z)
    {
        Id = id;
        Name = name;
        X = x;
        Z = z;
    }

    public int Id { get; }
    public string Name { get; }
    /// <summary>Middle of the town.</summary>
    public float X { get; }
    public float Z { get; }
    /// <summary>0 to 100. Falls when buildings are demolished for roads and when they are abandoned; regained slowly.</summary>
    public float Satisfaction { get; internal set; }
    /// <summary>Share (0 to 1) of last month's needs of its buildings that were not met.</summary>
    public float UnmetShare { get; internal set; }
    /// <summary>People of this town called up so far (a number only).</summary>
    public int CalledUp { get; internal set; }
    /// <summary>People of this town killed or hurt in raids so far (a number only).</summary>
    public int Casualties { get; internal set; }
    /// <summary>Share (0 to 1) of its homes that were reached last month: a group living there made a trip to work or leisure within the time budget. 1 until the first month is over.</summary>
    public float Connectivity { get; internal set; } = 1f;
    public GrowthReport LastGrowth { get; internal set; }

    // The street grid: blocks of the first grid are 0..Blocks-1 either way; growth adds columns and rows on any side.
    internal float Angle;
    internal int Blocks;
    internal int IMin, IMax, JMin, JMax;
    internal List<Lot> Lots { get; } = new();
    /// <summary>Ids of every building ever in the town, demolished ones too (ids are never reused).</summary>
    public IReadOnlyList<int> Buildings => BuildingIds;
    internal List<int> BuildingIds { get; } = new();

    /// <summary>People who made a trip of each <see cref="TripMode"/> today (None: trips that could not be made), counted when the trip starts.</summary>
    public int[] TripsToday { get; } = new int[4];
    /// <summary>The same for the day before.</summary>
    public int[] TripsYesterday { get; } = new int[4];
}

/// <summary>A building as the generator lays it out.</summary>
public readonly record struct BuildingPlan(BuildingType Type, int Tier, float X, float Z, float Facing);

/// <summary>A town as the generator lays it out: where it is, how its street grid lies, and the buildings it starts with.</summary>
public sealed record TownPlan(string Name, float X, float Z, float Angle, int Blocks, IReadOnlyList<BuildingPlan> Buildings);

/// <summary>An industry as the generator lays it out: its type (index into <c>industries.types</c>) and place.</summary>
public readonly record struct IndustryPlan(int Type, float X, float Z, float Angle);

/// <summary>What the generator made: the towns that fit, out of the number asked for, and the industries.</summary>
/// <param name="Unlinked">Indices into <paramref name="Industries"/> of processors that could not be placed within reach of their inputs and a town, and of deposits that could not be placed within reach of a town and a fitting distance from the other deposits.</param>
public sealed record WorldPlan(int Requested, IReadOnlyList<TownPlan> Towns, IReadOnlyList<IndustryPlan>? Industries = null, IReadOnlyList<int>? Unlinked = null, IReadOnlyList<GatewayPlan>? Gateways = null);

/// <summary>A producer or processor in a cargo chain. It stands where it was generated and exchanges cargo with the stations that cover it.</summary>
public sealed class Industry
{
    internal Industry(int id, int type, float x, float y, float z, float angle, int cargoCount)
    {
        Id = id;
        Type = type;
        X = x;
        Y = y;
        Z = z;
        Angle = angle;
        Input = new float[cargoCount];
    }

    public int Id { get; }
    /// <summary>Index into <see cref="IndustriesTuning.Types"/>.</summary>
    public int Type { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float Angle { get; }
    /// <summary>Where it meets the road network: on the edge of its square that faces the nearest town (C10). A node stands there once a road has been built to it.</summary>
    public float AccessX { get; internal set; }
    public float AccessZ { get; internal set; }
    /// <summary>Delivered inputs waiting to be used, by cargo.</summary>
    public float[] Input { get; }
    /// <summary>The tick until which a raid keeps it from working, 0 when it is not damaged (C9d).</summary>
    public long DamagedUntil { get; internal set; }
    /// <summary>For a power plant: the share of full burn it managed in the last step, 0 to 1 (0 for any other industry).</summary>
    public float Load { get; internal set; }
    /// <summary>The share of its jobs the groups that reached it filled on the last day that closed (C11), 0 to 1; 1 before the first day closes.</summary>
    public float Staffing { get; internal set; } = 1f;
    /// <summary>People of trip groups who have reached it to work today so far, and on the last day that closed.</summary>
    public int WorkersToday { get; internal set; }
    public int WorkersYesterday { get; internal set; }
    /// <summary>What it has made that no station has taken yet.</summary>
    public float Output { get; internal set; }
    /// <summary>Everything it has made so far.</summary>
    public double Produced { get; internal set; }
}

/// <summary>Where a gateway stands, which network it serves and what it is called ("North Road Gate").</summary>
public sealed record GatewayPlan(GatewayKind Kind, string Name, float X, float Z, float Angle);

/// <summary>Which network a gateway is on: road vehicles reach a road gateway and trains a rail gateway.</summary>
public enum GatewayKind : byte { Road, Rail }

/// <summary>
/// A link with the rest of the world at the edge of the map. A station of its own mode that covers it takes the cargo it has imported
/// to haul inland, and what a vehicle unloads there is exported.
/// </summary>
public sealed class Gateway
{
    internal Gateway(int id, GatewayPlan plan, float y, int cargoCount)
    {
        Id = id;
        Kind = plan.Kind;
        Name = plan.Name;
        X = plan.X;
        Y = y;
        Z = plan.Z;
        Angle = plan.Angle;
        Stock = new float[cargoCount];
        Imported = new double[cargoCount];
        Exported = new double[cargoCount];
        ArrivesAt = new long[cargoCount];
        AvailableAt = new long[cargoCount];
        ShipmentUnits = new int[cargoCount];
        Array.Fill(ArrivesAt, -1L);
    }

    public int Id { get; }
    public GatewayKind Kind { get; }
    public string Name { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float Angle { get; }
    /// <summary>Where it meets the road network, as for an <see cref="Industry"/> (C10); only a road gateway is joined to roads.</summary>
    public float AccessX { get; internal set; }
    public float AccessZ { get; internal set; }
    /// <summary>Imported cargo waiting for a station to take it, by cargo.</summary>
    public float[] Stock { get; }
    /// <summary>Everything that has arrived and everything that has been delivered here so far, by cargo.</summary>
    public double[] Imported { get; }
    public double[] Exported { get; }
    /// <summary>The tick a shipment still on its way arrives at, -1 when none is, by cargo.</summary>
    public long[] ArrivesAt { get; }
    /// <summary>The tick from which the cargo can be asked for again.</summary>
    public long[] AvailableAt { get; }
    /// <summary>The units of the shipment on its way, fixed when it was asked for (rationing can change the size between requests).</summary>
    public int[] ShipmentUnits { get; }
    /// <summary>The tick until which a raid keeps the gateway shut, 0 when it is not (C9d).</summary>
    public long DamagedUntil { get; internal set; }
}
