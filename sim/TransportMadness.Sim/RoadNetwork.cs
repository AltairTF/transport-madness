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

    /// <summary>The road was split by a junction; vehicles on it move to <see cref="SplitFirst"/> / <see cref="SplitSecond"/>.</summary>
    public bool Retired { get; internal set; }
    /// <summary>Replacement covering the first <see cref="SplitDistance"/> metres of this lane.</summary>
    public LaneId SplitFirst { get; internal set; } = LaneId.None;
    /// <summary>Replacement covering the rest.</summary>
    public LaneId SplitSecond { get; internal set; } = LaneId.None;
    public float SplitDistance { get; internal set; }
}

public sealed class Road
{
    internal Road(RoadId id, int from, int to, RoadType type, float speedLimit, LaneId[] forward, LaneId[] backward)
    {
        SpeedLimit = speedLimit;
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
    public float SpeedLimit { get; }
    /// <summary>Replaced by two shorter roads when a junction was built in its middle.</summary>
    public bool Retired { get; internal set; }
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

    /// <summary>The nearest node within <paramref name="radius"/> of a point, or -1.</summary>
    public int FindNodeNear(float x, float z, float radius)
    {
        int best = -1;
        float bestDistance = radius * radius;
        for (int n = 0; n < _nodes.Count; n++)
        {
            float d = (_nodes[n].X - x) * (_nodes[n].X - x) + (_nodes[n].Z - z) * (_nodes[n].Z - z);
            if (d <= bestDistance) { best = n; bestDistance = d; }
        }
        return best;
    }

    /// <summary>One place along a planned road where it meets the network: an existing node, or a point inside a road to split.</summary>
    readonly record struct Meeting(float Along, int Node, RoadId Road, float RoadDistance, float X, float Z);

    /// <summary>
    /// Builds a straight road, joining the network wherever it touches it: at existing nodes, and by splitting any road it
    /// ends on or crosses so that a junction forms there. Returns the new road pieces, or none if it cannot be built
    /// (too short, a duplicate, or crossing within <see cref="MinRoadLength"/> of another junction).
    /// </summary>
    public IReadOnlyList<RoadId> BuildRoad(float x0, float z0, float x1, float z1, RoadType type, float speedLimit)
    {
        var plan = Plan(x0, z0, x1, z1);
        if (plan == null) return Array.Empty<RoadId>();

        var nodes = new List<int>(plan.Count);
        foreach (var m in plan)
        {
            if (m.Node >= 0) nodes.Add(m.Node);
            else if (m.Road.Value >= 0) nodes.Add(SplitRoad(m.Road, m.RoadDistance));
            else nodes.Add(AddNode(m.X, m.Z));
        }
        var built = new List<RoadId>();
        for (int i = 0; i + 1 < nodes.Count; i++) built.Add(AddRoad(nodes[i], nodes[i + 1], type, speedLimit));
        return built;
    }

    public bool CanBuildRoad(float x0, float z0, float x1, float z1) => Plan(x0, z0, x1, z1) != null;

    /// <summary>The meetings along the planned road, start to end, or null when it cannot be built.</summary>
    List<Meeting>? Plan(float x0, float z0, float x1, float z1)
    {
        const float onNode = 0.5f;
        float dx = x1 - x0, dz = z1 - z0;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < MinRoadLength) return null;

        var meetings = new List<Meeting>();
        int a = FindNodeNear(x0, z0, onNode), b = FindNodeNear(x1, z1, onNode);
        if (a >= 0 && b >= 0 && FindRoad(a, b) != null) return null;

        for (int r = 0; r < _roads.Count; r++)
        {
            var road = _roads[r];
            if (road.Retired) continue;
            var (rx0, rz0) = _nodes[road.From];
            var (rx1, rz1) = _nodes[road.To];
            float ex = rx1 - rx0, ez = rz1 - rz0;
            float roadLength = MathF.Sqrt(ex * ex + ez * ez);

            float cross = dx * ez - dz * ex;
            if (MathF.Abs(cross) < 1e-3f * length * roadLength) continue; // parallel
            float s = ((rx0 - x0) * ez - (rz0 - z0) * ex) / cross; // along the new road, 0..1
            float u = ((rx0 - x0) * dz - (rz0 - z0) * dx) / cross; // along the existing road, 0..1
            float slack = 0.5f / length, roadSlack = 0.5f / roadLength;
            if (s < -slack || s > 1f + slack || u < -roadSlack || u > 1f + roadSlack) continue;

            float hx = x0 + dx * s, hz = z0 + dz * s;
            float along = u * roadLength;
            if (along <= 0.5f || along >= roadLength - 0.5f)
            {
                // Touches the road at one of its ends.
                int node = along <= 0.5f ? road.From : road.To;
                if (!meetings.Exists(m => m.Node == node)) meetings.Add(new Meeting(s * length, node, new RoadId(-1), 0f, _nodes[node].X, _nodes[node].Z));
                continue;
            }
            if (along < MinRoadLength || along > roadLength - MinRoadLength) return null; // too close to its junction
            meetings.Add(new Meeting(s * length, -1, road.Id, along, hx, hz));
        }

        // Make sure both ends are part of the plan.
        if (!meetings.Exists(m => m.Along <= onNode)) meetings.Add(new Meeting(0f, a, new RoadId(-1), 0f, x0, z0));
        if (!meetings.Exists(m => m.Along >= length - onNode)) meetings.Add(new Meeting(length, b, new RoadId(-1), 0f, x1, z1));
        meetings.Sort((p, q) => p.Along.CompareTo(q.Along));

        for (int i = 0; i + 1 < meetings.Count; i++)
        {
            if (meetings[i + 1].Along - meetings[i].Along < MinRoadLength) return null;
            int na = meetings[i].Node, nb = meetings[i + 1].Node;
            if (na >= 0 && nb >= 0 && FindRoad(na, nb) != null) return null;
        }
        return meetings;
    }

    /// <summary>
    /// Replaces a road by two roads meeting at a new node <paramref name="distance"/> metres from its start.
    /// The old road and its lanes are retired; each old lane records its replacements so vehicles can move over.
    /// </summary>
    public int SplitRoad(RoadId id, float distance)
    {
        var road = _roads[id.Value];
        if (road.Retired) throw new InvalidOperationException("Road is already replaced.");
        float length = _lanes[road.ForwardLanes[0].Value].Length;
        if (distance < MinRoadLength || distance > length - MinRoadLength) throw new ArgumentOutOfRangeException(nameof(distance));

        var (x0, z0) = _nodes[road.From];
        var (x1, z1) = _nodes[road.To];
        float t = distance / length;
        int middle = AddNode(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t);

        road.Retired = true;
        foreach (var lane in road.ForwardLanes.Concat(road.BackwardLanes))
        {
            _lanes[lane.Value].Retired = true;
            _incoming[_lanes[lane.Value].EndNode].Remove(lane);
        }
        _nodeRoads[road.From].Remove(id);
        _nodeRoads[road.To].Remove(id);

        var first = _roads[AddRoad(road.From, middle, road.Type, road.SpeedLimit).Value];
        var second = _roads[AddRoad(middle, road.To, road.Type, road.SpeedLimit).Value];
        for (int i = 0; i < road.ForwardLanes.Length; i++)
            Replace(road.ForwardLanes[i], first.ForwardLanes[i], second.ForwardLanes[i], distance);
        for (int i = 0; i < road.BackwardLanes.Length; i++)
            Replace(road.BackwardLanes[i], second.BackwardLanes[i], first.BackwardLanes[i], length - distance);
        return middle;
    }

    void Replace(LaneId old, LaneId first, LaneId second, float firstLength)
    {
        var lane = _lanes[old.Value];
        lane.SplitFirst = first;
        lane.SplitSecond = second;
        lane.SplitDistance = firstLength;
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

        _roads.Add(new Road(id, from, to, type, speedLimit, forward, backward));
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
