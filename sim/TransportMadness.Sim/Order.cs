namespace TransportMadness.Sim;

public enum OrderKind : byte { Quota, Civil, StateWork }

/// <summary>Offered: waiting for the player. Active: accepted. Met, Late (paid in proportion) and Failed are closed by delivery or the deadline; Expired and Declined were never accepted.</summary>
public enum OrderState : byte { Offered, Active, Met, Late, Failed, Expired, Declined }

/// <summary>A request from the government (C5): deliver <see cref="Units"/> of a cargo to a gateway or a town by a deadline for a reward and standing.</summary>
public sealed class Order
{
    internal Order(int id, OrderKind kind, string template, int cargo, int units)
    {
        Id = id;
        Kind = kind;
        Template = template;
        Cargo = cargo;
        Units = units;
    }

    public int Id { get; }
    public OrderKind Kind { get; }
    /// <summary>The id of the template it was drawn from (or "tutorial").</summary>
    public string Template { get; }
    /// <summary>What the player is told; not part of the state hash.</summary>
    public string Title { get; internal set; } = "";
    /// <summary>Index into the cargo list.</summary>
    public int Cargo { get; }
    public int Units { get; }
    /// <summary>Units delivered since it was accepted.</summary>
    public double Progress { get; internal set; }
    /// <summary>The gateway cargo is to be delivered to, -1 for any (a state work).</summary>
    public int Gateway { get; internal set; } = -1;
    /// <summary>The town that is to receive the cargo (a civil need), -1 otherwise.</summary>
    public int Town { get; internal set; } = -1;
    /// <summary>For a state work: the industries it founds, with their places, and once accepted their ids.</summary>
    public IReadOnlyList<IndustryPlan> Works { get; internal set; } = Array.Empty<IndustryPlan>();
    public List<int> WorkIds { get; } = new();
    public long OfferedAt { get; internal set; }
    public long ExpiresAt { get; internal set; }
    public long AcceptedAt { get; internal set; } = -1;
    /// <summary>Days allowed from acceptance to the deadline.</summary>
    public int Days { get; internal set; }
    public long Deadline { get; internal set; } = -1;
    public long ClosedAt { get; internal set; } = -1;
    public long Reward { get; internal set; }
    /// <summary>Standing gained when it is met in full.</summary>
    public float Standing { get; internal set; }
    public OrderState State { get; internal set; }
    /// <summary>What the budget got from it (reward in proportion, less the fine of a failed one).</summary>
    public long Paid { get; internal set; }
    public bool IsOpen => State == OrderState.Offered || State == OrderState.Active;
}
