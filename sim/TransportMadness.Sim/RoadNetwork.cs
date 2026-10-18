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
    internal Lane(LaneId id, RoadId road, int index, float length, float speedLimit)
    {
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

    public int NodeCount => _nodes.Count;
    public int RoadCount => _roads.Count;
    public int LaneCount => _lanes.Count;

    public int AddNode(float x, float z)
    {
        _nodes.Add((x, z));
        _nodeRoads.Add(new List<RoadId>());
        return _nodes.Count - 1;
    }

    public (float X, float Z) GetNode(int node) => _nodes[node];
    public Road GetRoad(RoadId id) => _roads[id.Value];
    public Lane GetLane(LaneId id) => _lanes[id.Value];

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
        var forward = NewCarriageway(id, perDirection, length, speedLimit);
        var backward = NewCarriageway(id, perDirection, length, speedLimit);
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

    LaneId[] NewCarriageway(RoadId road, int count, float length, float speedLimit)
    {
        var ids = new LaneId[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = new LaneId(_lanes.Count);
            _lanes.Add(new Lane(ids[i], road, i, length, speedLimit));
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
