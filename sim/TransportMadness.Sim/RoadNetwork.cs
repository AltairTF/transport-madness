namespace TransportMadness.Sim;

public enum RoadType
{
    /// <summary>One lane per direction; opposing lanes may be used to overtake.</summary>
    TwoLane,
    /// <summary>Two lanes per direction on a separated carriageway; free lane choice, no overtaking via the oncoming side.</summary>
    Avenue2x2,
}

public readonly record struct LaneId(int Value)
{
    public static readonly LaneId None = new(-1);
    public bool IsNone => Value < 0;
}

public readonly record struct RoadId(int Value);

public sealed class Lane
{
    internal Lane(LaneId id, RoadId road, int index, float length, float speedLimit,
        int startNode, int endNode, float angleAtStart, float angleAtEnd)
    {
        StartNode = startNode;
        EndNode = endNode;
        AngleAtStart = angleAtStart;
        AngleAtEnd = angleAtEnd;
        Id = id;
        Road = road;
        Index = index;
        Length = length;
        SpeedLimit = speedLimit;
    }

    public LaneId Id { get; }
    public RoadId Road { get; }
    /// <summary>0 is the right-most lane of its carriageway.</summary>
    public int Index { get; }
    public float Length { get; }
    public float SpeedLimit { get; }
    public int StartNode { get; }
    public int EndNode { get; }
    /// <summary>Heading (radians, atan2 of z over x) from the start node into the road.</summary>
    public float AngleAtStart { get; }
    /// <summary>Heading from the end node back along the road, i.e. the direction the traffic arrives from.</summary>
    public float AngleAtEnd { get; }
    public LaneId Left { get; internal set; } = LaneId.None;
    public LaneId Right { get; internal set; } = LaneId.None;
    public LaneId Oncoming { get; internal set; } = LaneId.None;
    public LaneId[] Next { get; internal set; } = Array.Empty<LaneId>();
}

public sealed class Road
{
    internal Road(RoadId id, int from, int to, RoadType type, LaneId[] forward, LaneId[] backward)
    {
        Id = id;
        From = from;
        To = to;
        Type = type;
        ForwardLanes = forward;
        BackwardLanes = backward;
    }

    public RoadId Id { get; }
    public int From { get; }
    public int To { get; }
    public RoadType Type { get; }
    /// <summary>Lanes running From to To, right-most first.</summary>
    public LaneId[] ForwardLanes { get; }
    /// <summary>Lanes running To to From, right-most first.</summary>
    public LaneId[] BackwardLanes { get; }
}

/// <summary>Lane-level road graph on flat terrain. Straight segments only for now.</summary>
public sealed class RoadNetwork
{
    /// <summary>Keeps a lane longer than a vehicle so a vehicle never spans more than two lanes.</summary>
    public const float MinRoadLength = 10f;

    readonly List<(float X, float Z)> _nodes = new();
    readonly List<List<RoadId>> _nodeRoads = new();
    readonly List<Road> _roads = new();
    readonly List<Lane> _lanes = new();
    readonly List<List<LaneId>> _incoming = new();

    public int NodeCount => _nodes.Count;
    public int RoadCount => _roads.Count;
    public int LaneCount => _lanes.Count;

    public int AddNode(float x, float z)
    {
        _nodes.Add((x, z));
        _nodeRoads.Add(new List<RoadId>());
        _incoming.Add(new List<LaneId>());
        return _nodes.Count - 1;
    }

    public (float X, float Z) GetNode(int node) => _nodes[node];
    /// <summary>The road joining two nodes, in either direction, or null.</summary>
    public RoadId? FindRoad(int a, int b)
    {
        foreach (var id in _nodeRoads[a])
        {
            var road = _roads[id.Value];
            if ((road.From == a && road.To == b) || (road.From == b && road.To == a)) return id;
        }
        return null;
    }

    public Road GetRoad(RoadId id) => _roads[id.Value];
    public Lane GetLane(LaneId id) => _lanes[id.Value];

    /// <summary>Lanes whose traffic arrives at <paramref name="node"/>.</summary>
    public IReadOnlyList<LaneId> IncomingLanes(int node) => _incoming[node];

    /// <summary>
    /// True when a vehicle going <paramref name="inA"/> to <paramref name="outA"/> through a junction can meet one going
    /// <paramref name="inB"/> to <paramref name="outB"/>: they merge into the same lane, or their paths cross.
    /// Paths are approximated as chords between the arms they use, so they cross when the chords interleave.
    /// </summary>
    public bool MovesConflict(LaneId inA, LaneId outA, LaneId inB, LaneId outB)
    {
        if (outA == outB) return inA != inB;
        if (inA == inB) return false;
        const float eps = 1e-3f;
        float a0 = GetLane(inA).AngleAtEnd, a1 = GetLane(outA).AngleAtStart;
        float b0 = GetLane(inB).AngleAtEnd, b1 = GetLane(outB).AngleAtStart;
        float arc = Wrap(a1 - a0);
        if (arc < eps || arc > MathF.Tau - eps) return false; // U-turn
        float p = Wrap(b0 - a0), q = Wrap(b1 - a0);
        if (p < eps || q < eps || MathF.Abs(p - arc) < eps || MathF.Abs(q - arc) < eps) return false; // shared arm
        return (p < arc) != (q < arc);
    }

    static float Wrap(float angle)
    {
        angle %= MathF.Tau;
        return angle < 0f ? angle + MathF.Tau : angle;
    }

    public RoadId AddRoad(int from, int to, RoadType type, float speedLimit)
    {
        if (from == to) throw new ArgumentException("A road needs two distinct nodes.");
        var (x0, z0) = _nodes[from];
        var (x1, z1) = _nodes[to];
        float length = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        if (length < MinRoadLength) throw new ArgumentException($"Road is shorter than {MinRoadLength} m.");
        if (!(speedLimit > 0f)) throw new ArgumentOutOfRangeException(nameof(speedLimit), "Speed limit must be positive.");

        var id = new RoadId(_roads.Count);
        int perDirection = type == RoadType.TwoLane ? 1 : 2;
        float heading = MathF.Atan2(z1 - z0, x1 - x0);
        float reverse = MathF.Atan2(z0 - z1, x0 - x1);
        var forward = NewCarriageway(id, perDirection, length, speedLimit, from, to, heading, reverse);
        var backward = NewCarriageway(id, perDirection, length, speedLimit, to, from, reverse, heading);
        if (type == RoadType.TwoLane)
        {
            _lanes[forward[0].Value].Oncoming = backward[0];
            _lanes[backward[0].Value].Oncoming = forward[0];
        }

        _roads.Add(new Road(id, from, to, type, forward, backward));
        _nodeRoads[from].Add(id);
        _nodeRoads[to].Add(id);
        RebuildJunction(from);
        RebuildJunction(to);
        return id;
    }

    LaneId[] NewCarriageway(RoadId road, int count, float length, float speedLimit,
        int startNode, int endNode, float angleAtStart, float angleAtEnd)
    {
        var ids = new LaneId[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = new LaneId(_lanes.Count);
            _lanes.Add(new Lane(ids[i], road, i, length, speedLimit, startNode, endNode, angleAtStart, angleAtEnd));
            _incoming[endNode].Add(ids[i]);
        }
        for (int i = 0; i + 1 < count; i++)
        {
            _lanes[ids[i].Value].Left = ids[i + 1];
            _lanes[ids[i + 1].Value].Right = ids[i];
        }
        return ids;
    }

    void RebuildJunction(int node)
    {
        var roads = _nodeRoads[node];
        foreach (var roadId in roads)
        {
            var road = _roads[roadId.Value];
            var incoming = road.To == node ? road.ForwardLanes : road.BackwardLanes;
            foreach (var laneId in incoming)
            {
                var lane = _lanes[laneId.Value];
                var next = new List<LaneId>();
                foreach (var otherId in roads)
                {
                    if (otherId == roadId) continue;
                    next.Add(Outgoing(_roads[otherId.Value], node, lane.Index));
                }
                if (next.Count == 0) // dead end: turn around
                    next.Add(Outgoing(road, node, lane.Index));
                lane.Next = next.ToArray();
            }
        }
    }

    static LaneId Outgoing(Road road, int node, int preferredIndex)
    {
        var outgoing = road.From == node ? road.ForwardLanes : road.BackwardLanes;
        return outgoing[Math.Min(preferredIndex, outgoing.Length - 1)];
    }
}
