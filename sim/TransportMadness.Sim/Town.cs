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
    public BuildingState State { get; internal set; } = BuildingState.Active;
    /// <summary>The day it went into its current state.</summary>
    public long StateSince { get; internal set; }
}

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
    /// <summary>Ids of every building ever in the town, demolished ones too (ids are never reused).</summary>
    public IReadOnlyList<int> Buildings => BuildingIds;
    internal List<int> BuildingIds { get; } = new();
}

/// <summary>A building as the generator lays it out.</summary>
public readonly record struct BuildingPlan(BuildingType Type, int Tier, float X, float Z, float Facing);

/// <summary>A town as the generator lays it out: where it is, how its street grid lies, and the buildings it starts with.</summary>
public sealed record TownPlan(string Name, float X, float Z, float Angle, int Blocks, IReadOnlyList<BuildingPlan> Buildings);

/// <summary>What the generator made: the towns that fit, out of the number asked for.</summary>
public sealed record WorldPlan(int Requested, IReadOnlyList<TownPlan> Towns);
