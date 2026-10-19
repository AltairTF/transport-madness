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
    internal Lane(LaneId id, RoadId road, int index, float speedLimit, RoadGeometry path, float sideOffset,
        int startNode, int endNode, float grade)
    {
        Path = path;
        SideOffset = sideOffset;
        Grade = grade;
        StartNode = startNode;
        EndNode = endNode;
        Id = id;
        Road = road;
        Index = index;
        SpeedLimit = speedLimit;
    }

    public LaneId Id { get; }
    public RoadId Road { get; }
    /// <summary>0 is the right-most lane of its carriageway.</summary>
    public int Index { get; }
    /// <summary>Distance along the road's centre line, which every lane of the road shares for positions.</summary>
    public float Length => Path.Length;
    /// <summary>The road's centre line in the direction of travel. Positions on the lane are distances along it.</summary>
    public RoadGeometry Path { get; }
    /// <summary>How far the lane centre lies to the driver's right of <see cref="Path"/>.</summary>
    public float SideOffset { get; }
    /// <summary>Posted limit reduced for the grade and the curve radius.</summary>
    public float SpeedLimit { get; }
    /// <summary>Rise over run in the direction of travel; positive is uphill.</summary>
    public float Grade { get; }
    public int StartNode { get; }
    public int EndNode { get; }
    /// <summary>Heading (radians, atan2 of z over x) from the start node into the road.</summary>
    public float AngleAtStart => Path.Heading0;
    /// <summary>Heading from the end node back along the road, i.e. the direction the traffic arrives from.</summary>
    public float AngleAtEnd => Path.HeadingAt(Path.Length) + MathF.PI;
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
    /// <summary>The replacement is a lane that other lanes of the old road also move into (an avenue narrowed to two lanes), so vehicles may end up on top of each other.</summary>
    public bool SplitMerges { get; internal set; }
}

public sealed class Road
{
    internal Road(RoadId id, int from, int to, RoadType type, float speedLimit, RoadGeometry geometry, LaneId[] forward, LaneId[] backward)
    {
        SpeedLimit = speedLimit;
        Id = id;
        From = from;
        To = to;
        Type = type;
        Geometry = geometry;
        ForwardLanes = forward;
        BackwardLanes = backward;
    }

    public RoadId Id { get; }
    public int From { get; }
    public int To { get; }
    public RoadType Type { get; }
    public float SpeedLimit { get; }
    /// <summary>The centre line from <see cref="From"/> to <see cref="To"/>.</summary>
    public RoadGeometry Geometry { get; }
    public float Length => Geometry.Length;
    /// <summary>Replaced by two shorter roads when a junction was built in its middle.</summary>
    public bool Retired { get; internal set; }
    /// <summary>Lanes running From to To, right-most first.</summary>
    public LaneId[] ForwardLanes { get; }
    /// <summary>Lanes running To to From, right-most first.</summary>
    public LaneId[] BackwardLanes { get; }
}

/// <summary>What building a road would take: its length and how much earth it moves.</summary>
public readonly record struct RoadCost(float Length, float CutVolume, float FillVolume, float Money);

/// <summary>A point on a road's centre line.</summary>
public readonly record struct RoadPoint(RoadId Road, float Along, float X, float Y, float Z);

/// <summary>Lane-level road graph, optionally on a <see cref="Terrain"/>. Roads are straight or circular arcs.</summary>
public sealed class RoadNetwork
{
    /// <summary>Keeps a lane longer than a vehicle so a vehicle never spans more than two lanes.</summary>
    public const float MinRoadLength = 10f;
    /// <summary>Steepest road that can be built (rise over run).</summary>
    public const float MaxGrade = 0.12f;
    /// <summary>Up to this grade the speed limit is unaffected; at <see cref="MaxGrade"/> it is halved.</summary>
    public const float FreeGrade = 0.04f;
    /// <summary>Distance the ground blends back to its old height beyond the carriageway.</summary>
    public const float Shoulder = 6f;
    /// <summary>Tightest curve that can be built (radius of the centre line).</summary>
    public const float MinRadius = 25f;
    /// <summary>Largest total turn of one road, in radians.</summary>
    public const float MaxSweep = 2.8f;
    /// <summary>Sideways acceleration drivers accept in a curve; the curve speed limit follows from it.</summary>
    public const float ComfortLateralAcceleration = 3f;
    public const float LaneWidth = 3.5f;
    public const float MedianWidth = 2f;
    /// <summary>How far from the node a junction's paths begin and end, matching the drawn junction area.</summary>
    public const float JunctionRadius = 8f;

    const float SampleSpacing = 3f;    // for intersecting curved roads

    /// <param name="terrain">The ground the roads sit on, or null for a perfectly flat map.</param>
    /// <param name="tuning">Prices and simulation tuning; the shipped <c>data/tuning.json</c> when omitted.</param>
    public RoadNetwork(Terrain? terrain = null, Tuning? tuning = null)
    {
        Terrain = terrain;
        Tuning = tuning ?? Tuning.Load();
    }

    /// <summary>Prices and simulation tuning for this world. The simulation reads its tuning from here.</summary>
    public Tuning Tuning { get; }

    /// <summary>The ground the roads sit on, or null for a perfectly flat map.</summary>
    public Terrain? Terrain { get; }

    readonly List<float> _nodeHeights = new();
    readonly List<(float X, float Z)> _nodes = new();
    readonly List<List<RoadId>> _nodeRoads = new();
    readonly List<Road> _roads = new();
    readonly List<Lane> _lanes = new();
    readonly List<List<LaneId>> _incoming = new();
    // Lane geometry never changes, so what is derived from a pair of moves never goes stale.
    readonly Dictionary<(int, int), (float X, float Z)[]> _movePaths = new();
    readonly Dictionary<(int, int, int, int), bool> _conflicts = new();

    /// <summary>Increases whenever roads or lanes are added or removed, so a simulation knows to pick up the change.</summary>
    public int Version { get; private set; }

    public int NodeCount => _nodes.Count;
    public int RoadCount => _roads.Count;
    public int LaneCount => _lanes.Count;

    /// <summary>Adds a node standing on the ground (height zero on a flat map).</summary>
    public int AddNode(float x, float z) => AddNode(x, z, Terrain?.HeightAt(x, z) ?? 0f);

    /// <summary>Adds a node at an explicit road height.</summary>
    public int AddNode(float x, float z, float height)
    {
        _nodes.Add((x, z));
        _nodeHeights.Add(height);
        _nodeRoads.Add(new List<RoadId>());
        _incoming.Add(new List<LaneId>());
        return _nodes.Count - 1;
    }

    public (float X, float Z) GetNode(int node) => _nodes[node];
    /// <summary>Height of the road surface at a node.</summary>
    public float GetNodeHeight(int node) => _nodeHeights[node];
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

    /// <summary>Roads in use that meet at a node.</summary>
    public IReadOnlyList<RoadId> RoadsAt(int node) => _nodeRoads[node];

    /// <summary>Lanes whose traffic arrives at <paramref name="node"/>.</summary>
    public IReadOnlyList<LaneId> IncomingLanes(int node) => _incoming[node];

    /// <summary>Road surface height at a distance along a road.</summary>
    public float RoadHeightAt(RoadId id, float along)
    {
        var road = _roads[id.Value];
        float y0 = _nodeHeights[road.From], y1 = _nodeHeights[road.To];
        return y0 + (y1 - y0) * Math.Clamp(along / road.Length, 0f, 1f);
    }

    /// <summary>Ground-plane position and heading of the centre of a lane at a distance along it.</summary>
    public (float X, float Z, float Heading) LanePoint(LaneId id, float along)
    {
        var lane = _lanes[id.Value];
        along = Math.Clamp(along, 0f, lane.Length);
        var (x, z) = lane.Path.PointAt(along);
        float heading = lane.Path.HeadingAt(along);
        return (x - MathF.Sin(heading) * lane.SideOffset, z + MathF.Cos(heading) * lane.SideOffset, heading);
    }

    // ---- Junction geometry ----------------------------------------------------------------------------------

    /// <summary>
    /// True when a vehicle going <paramref name="inA"/> to <paramref name="outA"/> through a junction can meet one going
    /// <paramref name="inB"/> to <paramref name="outB"/>: they merge into the same lane, or their paths cross.
    /// A path runs from the end of the incoming lane to the start of the outgoing one along a smooth curve that
    /// keeps the heading of each lane at its end, so lane offsets and the width of the junction are taken into account.
    /// </summary>
    public bool MovesConflict(LaneId inA, LaneId outA, LaneId inB, LaneId outB)
    {
        if (outA == outB) return inA != inB;
        if (inA == inB) return false;
        var key = (inA.Value, outA.Value, inB.Value, outB.Value);
        if (_conflicts.TryGetValue(key, out bool known)) return known;
        bool result = PathsCross(MovePath(inA, outA), MovePath(inB, outB));
        _conflicts[key] = result;
        return result;
    }

    /// <summary>How far into a lane of the given length a junction path reaches (from its end, or from its start).</summary>
    public static float JunctionReach(float laneLength) => MathF.Min(JunctionRadius, laneLength * 0.5f);

    /// <summary>
    /// Where a vehicle is when it is a <paramref name="fraction"/> (0 to 1) of the way along the path from lane
    /// <paramref name="from"/> to lane <paramref name="to"/> through their junction, and which way it faces. The path
    /// starts <see cref="JunctionReach"/> before the end of <paramref name="from"/> and ends the same distance into
    /// <paramref name="to"/>. This is for drawing: the simulation itself carries a vehicle across the node in one step.
    /// </summary>
    public (float X, float Z, float Heading) JunctionPoint(LaneId from, LaneId to, float fraction)
    {
        var path = MovePath(from, to);
        float total = 0f;
        for (int i = 0; i + 1 < path.Length; i++) total += Distance(path[i], path[i + 1]);
        float target = Math.Clamp(fraction, 0f, 1f) * total;
        for (int i = 0; i + 1 < path.Length; i++)
        {
            float length = Distance(path[i], path[i + 1]);
            if (target > length && i + 2 < path.Length) { target -= length; continue; }
            float t = length > 1e-6f ? Math.Clamp(target / length, 0f, 1f) : 0f;
            float dx = path[i + 1].X - path[i].X, dz = path[i + 1].Z - path[i].Z;
            return (path[i].X + dx * t, path[i].Z + dz * t, MathF.Atan2(dz, dx));
        }
        return (path[0].X, path[0].Z, 0f);
    }

    static float Distance((float X, float Z) a, (float X, float Z) b) => MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));

    /// <summary>The line a vehicle follows through a junction: a cubic curve between the lane end and the next lane's start.</summary>
    (float X, float Z)[] MovePath(LaneId from, LaneId to)
    {
        if (_movePaths.TryGetValue((from.Value, to.Value), out var cached)) return cached;
        var a = _lanes[from.Value];
        var b = _lanes[to.Value];
        // The path starts where the incoming lane enters the junction area and ends where the outgoing lane leaves it.
        float inAlong = MathF.Max(0f, a.Length - JunctionReach(a.Length));
        float outAlong = JunctionReach(b.Length);
        float ha = a.Path.HeadingAt(inAlong), hb = b.Path.HeadingAt(outAlong);
        var (ax, az) = a.Path.PointAt(inAlong);
        var (bx, bz) = b.Path.PointAt(outAlong);
        ax -= MathF.Sin(ha) * a.SideOffset; az += MathF.Cos(ha) * a.SideOffset;
        bx -= MathF.Sin(hb) * b.SideOffset; bz += MathF.Cos(hb) * b.SideOffset;

        float distance = MathF.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        float handle = MathF.Max(3f, 0.4f * distance);
        float c1x = ax + MathF.Cos(ha) * handle, c1z = az + MathF.Sin(ha) * handle;
        float c2x = bx - MathF.Cos(hb) * handle, c2z = bz - MathF.Sin(hb) * handle;
        const int segments = 12;
        var path = new (float X, float Z)[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments, u = 1f - t;
            float w0 = u * u * u, w1 = 3f * u * u * t, w2 = 3f * u * t * t, w3 = t * t * t;
            path[i] = (w0 * ax + w1 * c1x + w2 * c2x + w3 * bx, w0 * az + w1 * c1z + w2 * c2z + w3 * bz);
        }
        _movePaths[(from.Value, to.Value)] = path;
        return path;
    }

    static bool PathsCross((float X, float Z)[] p, (float X, float Z)[] q)
    {
        for (int i = 0; i + 1 < p.Length; i++)
            for (int j = 0; j + 1 < q.Length; j++)
                if (SegmentsIntersect(p[i], p[i + 1], q[j], q[j + 1], 0f, out _, out _)) return true;
        return false;
    }

    /// <summary>Intersection of segments a0-a1 and b0-b1; the parameters are fractions along each.</summary>
    static bool SegmentsIntersect((float X, float Z) a0, (float X, float Z) a1, (float X, float Z) b0, (float X, float Z) b1,
        float tolerance, out float t, out float u)
    {
        float rx = a1.X - a0.X, rz = a1.Z - a0.Z, sx = b1.X - b0.X, sz = b1.Z - b0.Z;
        float denominator = rx * sz - rz * sx;
        t = u = 0f;
        if (MathF.Abs(denominator) < 1e-9f) return false;
        float qx = b0.X - a0.X, qz = b0.Z - a0.Z;
        t = (qx * sz - qz * sx) / denominator;
        u = (qx * rz - qz * rx) / denominator;
        return t >= -tolerance && t <= 1f + tolerance && u >= -tolerance && u <= 1f + tolerance;
    }

    /// <summary>
    /// The nearest node within <paramref name="radius"/> of a point, or -1. With <paramref name="withRoadsOnly"/> nodes
    /// that no road reaches any more (left behind by removing or moving roads) are skipped.
    /// </summary>
    public int FindNodeNear(float x, float z, float radius, bool withRoadsOnly = false)
    {
        int best = -1;
        float bestDistance = radius * radius;
        for (int n = 0; n < _nodes.Count; n++)
        {
            if (withRoadsOnly && _nodeRoads[n].Count == 0) continue;
            float d = (_nodes[n].X - x) * (_nodes[n].X - x) + (_nodes[n].Z - z) * (_nodes[n].Z - z);
            if (d <= bestDistance) { best = n; bestDistance = d; }
        }
        return best;
    }

    /// <summary>
    /// The point on a road's centre line nearest to (x, z) within <paramref name="radius"/>, with its distance along the
    /// road rounded to a multiple of <paramref name="step"/>; roads where that is within <see cref="MinRoadLength"/> of
    /// either end are skipped (a junction cannot be built there).
    /// </summary>
    public RoadPoint? NearestOnRoad(float x, float z, float radius, float step)
    {
        float bestDistance = radius * radius;
        RoadPoint? best = null;
        foreach (var road in _roads)
        {
            if (road.Retired) continue;
            var g = road.Geometry;
            float along = NearestAlong(g, x, z);
            along = MathF.Round(along / step) * step;
            if (along < MinRoadLength || along > g.Length - MinRoadLength) continue;
            var (px, pz) = g.PointAt(along);
            float d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d <= bestDistance)
            {
                best = new RoadPoint(road.Id, along, px, RoadHeightAt(road.Id, along), pz);
                bestDistance = d;
            }
        }
        return best;
    }

    static float NearestAlong(RoadGeometry g, float x, float z)
    {
        if (g.IsStraight)
            return Math.Clamp((x - g.X) * MathF.Cos(g.Heading0) + (z - g.Z) * MathF.Sin(g.Heading0), 0f, g.Length);
        // The centre of the arc lies k to the side the road turns toward, and a point at heading h is k * (sin h, -cos h) from it.
        float k = g.Length / g.Sweep; // signed radius
        float cx = g.X - k * MathF.Sin(g.Heading0), cz = g.Z + k * MathF.Cos(g.Heading0);
        float sign = MathF.Sign(k);
        float heading = MathF.Atan2((x - cx) * sign, -(z - cz) * sign);
        float delta = heading - g.Heading0;
        delta -= MathF.Tau * MathF.Floor((delta + MathF.PI) / MathF.Tau); // into [-pi, pi)
        return Math.Clamp(delta / g.Sweep * g.Length, 0f, g.Length);
    }

    // ---- Planning and building ------------------------------------------------------------------------------

    /// <summary>One place along a planned road where it meets the network: an existing node, or a point inside a road to split.</summary>
    readonly record struct Meeting(float Along, int Node, RoadId Road, float RoadDistance, float X, float Z, float Y);

    /// <summary>
    /// Builds a road, straight or bending by <paramref name="sweep"/> radians, joining the network wherever it touches
    /// it: at existing nodes, and by splitting any road it ends on or crosses so that a junction forms there. Returns
    /// the new road pieces, or none if it cannot be built (too short, too steep, too tight a curve, a duplicate, or
    /// crossing within <see cref="MinRoadLength"/> of another junction).
    /// </summary>
    public IReadOnlyList<RoadId> BuildRoad(float x0, float z0, float x1, float z1, RoadType type, float speedLimit, float sweep = 0f)
    {
        var plan = Plan(x0, z0, x1, z1, sweep);
        if (plan == null) return Array.Empty<RoadId>();
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);

        var nodes = new List<int>(plan.Count);
        foreach (var m in plan)
        {
            if (m.Node >= 0) nodes.Add(m.Node);
            else if (m.Road.Value >= 0) nodes.Add(SplitRoad(m.Road, m.RoadDistance));
            else nodes.Add(AddNode(m.X, m.Z, m.Y));
        }
        var built = new List<RoadId>();
        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            float pieceSweep = sweep * (plan[i + 1].Along - plan[i].Along) / geometry.Length;
            built.Add(AddRoad(nodes[i], nodes[i + 1], type, speedLimit, pieceSweep));
            Terrain?.CarvePath(Profile(geometry, plan[i], plan[i + 1]), HalfWidth(type), Shoulder);
        }
        return built;
    }

    /// <summary>Points along a piece of a planned road with the road surface height at each.</summary>
    static List<(float X, float Z, float Y)> Profile(RoadGeometry geometry, Meeting a, Meeting b)
    {
        float run = b.Along - a.Along;
        int steps = Math.Max(1, (int)MathF.Ceiling(run / 4f));
        var points = new List<(float, float, float)>(steps + 1) { (a.X, a.Z, a.Y) };
        for (int k = 1; k < steps; k++)
        {
            float t = k / (float)steps;
            var (x, z) = geometry.PointAt(a.Along + run * t);
            points.Add((x, z, a.Y + (b.Y - a.Y) * t));
        }
        points.Add((b.X, b.Z, b.Y));
        return points;
    }

    /// <summary>Terrain height under a point, zero on a flat map.</summary>
    float GroundHeight(float x, float z) => Terrain?.HeightAt(x, z) ?? 0f;

    /// <summary>Half the width of the paved road including any median.</summary>
    public static float HalfWidth(RoadType type) => type == RoadType.Avenue2x2 ? 8f : 3.5f;

    /// <summary>
    /// What a road would cost, or null if it cannot be built. Earthworks are the volume between the ground and the
    /// road surface along the way, with side slopes of 1 in 1.5.
    /// </summary>
    public RoadCost? EstimateCost(float x0, float z0, float x1, float z1, RoadType type, float sweep = 0f)
    {
        var plan = Plan(x0, z0, x1, z1, sweep);
        if (plan == null) return null;
        return Price(plan, RoadGeometry.FromChord(x0, z0, x1, z1, sweep), type);
    }

    /// <summary>Length, earthworks and money for building a planned road.</summary>
    RoadCost Price(List<Meeting> plan, RoadGeometry geometry, RoadType type)
    {
        float width = HalfWidth(type) * 2f;
        float cut = 0f, fill = 0f, length = 0f;
        for (int i = 0; i + 1 < plan.Count; i++)
        {
            var a = plan[i];
            var b = plan[i + 1];
            float run = b.Along - a.Along;
            length += run;
            if (Terrain == null) continue;
            int steps = Math.Max(1, (int)MathF.Ceiling(run / 4f));
            float ds = run / steps;
            for (int k = 0; k < steps; k++)
            {
                float t = (k + 0.5f) / steps;
                var (x, z) = geometry.PointAt(a.Along + run * t);
                float height = Terrain.HeightAt(x, z) - (a.Y + (b.Y - a.Y) * t);
                float h = MathF.Abs(height);
                float section = h * width + Tuning.Roads.EarthworkSlope * h * h;
                if (height > 0f) cut += section * ds; else fill += section * ds;
            }
        }
        var prices = Tuning.Roads;
        float perMetre = prices.Of(type).CostPerMetre;
        return new RoadCost(length, cut, fill, length * perMetre + cut * prices.CutCostPerCubicMetre + fill * prices.FillCostPerCubicMetre);
    }

    public bool CanBuildRoad(float x0, float z0, float x1, float z1, float sweep = 0f) => Plan(x0, z0, x1, z1, sweep) != null;

    /// <summary>The meetings along the planned road, start to end, or null when it cannot be built.</summary>
    List<Meeting>? Plan(float x0, float z0, float x1, float z1, float sweep)
    {
        const float onNode = 0.5f;
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        float length = geometry.Length;
        if (length < MinRoadLength) return null;
        if (!geometry.IsStraight && (MathF.Abs(sweep) > MaxSweep || geometry.Radius < MinRadius)) return null;

        var meetings = new List<Meeting>();
        int a = FindNodeNear(x0, z0, onNode), b = FindNodeNear(x1, z1, onNode);
        if (a >= 0 && b >= 0 && FindRoad(a, b) != null) return null;

        (float X, float Z)[]? samples = null;
        for (int r = 0; r < _roads.Count; r++)
        {
            var road = _roads[r];
            if (road.Retired) continue;
            var other = road.Geometry;
            float roadLength = other.Length;

            if (geometry.IsStraight && other.IsStraight)
            {
                var (rx0, rz0) = _nodes[road.From];
                var (rx1, rz1) = _nodes[road.To];
                float dx = x1 - x0, dz = z1 - z0;
                float ex = rx1 - rx0, ez = rz1 - rz0;
                float cross = dx * ez - dz * ex;
                if (MathF.Abs(cross) < 1e-3f * length * roadLength) continue; // parallel
                float s = ((rx0 - x0) * ez - (rz0 - z0) * ex) / cross; // along the new road, 0..1
                float u = ((rx0 - x0) * dz - (rz0 - z0) * dx) / cross; // along the existing road, 0..1
                float slack = 0.5f / length, roadSlack = 0.5f / roadLength;
                if (s < -slack || s > 1f + slack || u < -roadSlack || u > 1f + roadSlack) continue;
                if (!Touch(meetings, road, s * length, u * roadLength, x0 + dx * s, z0 + dz * s)) return null;
                continue;
            }

            samples ??= Sample(geometry);
            var otherSamples = Sample(other);
            if (!Overlap(samples, otherSamples)) continue;
            float stepNew = length / (samples.Length - 1), stepOld = roadLength / (otherSamples.Length - 1);
            float lastHit = float.NegativeInfinity;
            for (int i = 0; i + 1 < samples.Length; i++)
                for (int j = 0; j + 1 < otherSamples.Length; j++)
                {
                    if (!SegmentsIntersect(samples[i], samples[i + 1], otherSamples[j], otherSamples[j + 1], 1e-3f, out float t, out float u)) continue;
                    float along = (i + t) * stepNew;
                    if (MathF.Abs(along - lastHit) < 1f) continue; // the same crossing seen from the next segment
                    lastHit = along;
                    float px = samples[i].X + (samples[i + 1].X - samples[i].X) * t, pz = samples[i].Z + (samples[i + 1].Z - samples[i].Z) * t;
                    if (!Touch(meetings, road, along, (j + u) * stepOld, px, pz)) return null;
                }
        }

        // Make sure both ends are part of the plan.
        if (!meetings.Exists(m => m.Along <= onNode)) meetings.Add(new Meeting(0f, a, new RoadId(-1), 0f, x0, z0, a >= 0 ? _nodeHeights[a] : GroundHeight(x0, z0)));
        if (!meetings.Exists(m => m.Along >= length - onNode)) meetings.Add(new Meeting(length, b, new RoadId(-1), 0f, x1, z1, b >= 0 ? _nodeHeights[b] : GroundHeight(x1, z1)));
        meetings.Sort((p, q) => p.Along.CompareTo(q.Along));

        for (int i = 0; i + 1 < meetings.Count; i++)
        {
            if (meetings[i + 1].Along - meetings[i].Along < MinRoadLength) return null;
            int na = meetings[i].Node, nb = meetings[i + 1].Node;
            if (na >= 0 && nb >= 0 && FindRoad(na, nb) != null) return null;
            float run = meetings[i + 1].Along - meetings[i].Along;
            if (MathF.Abs(meetings[i + 1].Y - meetings[i].Y) > MaxGrade * run) return null; // too steep
        }
        return meetings;
    }

    /// <summary>
    /// Records where the planned road touches <paramref name="road"/>: at one of its end nodes, or inside it (a junction).
    /// False when it touches too close to the road's end to build a junction there.
    /// </summary>
    bool Touch(List<Meeting> meetings, Road road, float along, float roadAlong, float x, float z)
    {
        const float onNode = 0.5f;
        float roadLength = road.Length;
        if (roadAlong <= onNode || roadAlong >= roadLength - onNode)
        {
            int node = roadAlong <= onNode ? road.From : road.To;
            if (!meetings.Exists(m => m.Node == node)) meetings.Add(new Meeting(along, node, new RoadId(-1), 0f, _nodes[node].X, _nodes[node].Z, _nodeHeights[node]));
            return true;
        }
        if (roadAlong < MinRoadLength || roadAlong > roadLength - MinRoadLength) return false;
        meetings.Add(new Meeting(along, -1, road.Id, roadAlong, x, z, RoadHeightAt(road.Id, roadAlong)));
        return true;
    }

    static (float X, float Z)[] Sample(RoadGeometry g)
    {
        int n = Math.Max(1, (int)MathF.Ceiling(g.Length / SampleSpacing));
        var points = new (float, float)[n + 1];
        for (int i = 0; i <= n; i++) points[i] = g.PointAt(g.Length * i / n);
        return points;
    }

    static bool Overlap((float X, float Z)[] a, (float X, float Z)[] b)
    {
        static (float, float, float, float) Box((float X, float Z)[] p)
        {
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            foreach (var (x, z) in p) { x0 = MathF.Min(x0, x); x1 = MathF.Max(x1, x); z0 = MathF.Min(z0, z); z1 = MathF.Max(z1, z); }
            return (x0, z0, x1, z1);
        }
        var (ax0, az0, ax1, az1) = Box(a);
        var (bx0, bz0, bx1, bz1) = Box(b);
        return ax0 <= bx1 + 1f && bx0 <= ax1 + 1f && az0 <= bz1 + 1f && bz0 <= az1 + 1f;
    }


    // ---- Removing and upgrading ------------------------------------------------------------------------------

    /// <summary>The road piece whose centre line passes nearest to a point, within <paramref name="radius"/>.</summary>
    public RoadId? RoadAt(float x, float z, float radius)
    {
        float best = radius * radius;
        RoadId? found = null;
        foreach (var road in _roads)
        {
            if (road.Retired) continue;
            var g = road.Geometry;
            var (px, pz) = g.PointAt(NearestAlong(g, x, z));
            float d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d <= best) { best = d; found = road.Id; }
        }
        return found;
    }

    /// <summary>What it costs to tear a road down. Vehicles on it are removed; the ground keeps its cut and fill.</summary>
    public float DemolitionCost(RoadId id) => _roads[id.Value].Length * Tuning.Roads.DemolitionCostPerMetre;

    /// <summary>
    /// Tears a road piece down. Its lanes are retired with no replacement, so a simulation removes the vehicles on them;
    /// the junctions at both ends are rebuilt (a node left with one road becomes a dead end).
    /// </summary>
    public bool RemoveRoad(RoadId id)
    {
        var road = _roads[id.Value];
        if (road.Retired) return false;
        Retire(road);
        RebuildJunction(road.From);
        RebuildJunction(road.To);
        Version++;
        return true;
    }

    void Retire(Road road)
    {
        road.Retired = true;
        foreach (var lane in road.ForwardLanes.Concat(road.BackwardLanes))
        {
            _lanes[lane.Value].Retired = true;
            _incoming[_lanes[lane.Value].EndNode].Remove(lane);
        }
        _nodeRoads[road.From].Remove(road.Id);
        _nodeRoads[road.To].Remove(road.Id);
    }

    /// <summary>Whether a road can be widened to an avenue: only two-lane roads can.</summary>
    public bool CanUpgrade(RoadId id) => !_roads[id.Value].Retired && _roads[id.Value].Type == RoadType.TwoLane;

    static bool ValidLimit(float speedLimit) => speedLimit > 0f && float.IsFinite(speedLimit);

    /// <summary>The price of upgrading a two-lane road to an avenue, or null if it cannot be. Earthworks are for the extra width.</summary>
    public RoadCost? EstimateUpgrade(RoadId id)
    {
        if (!CanUpgrade(id)) return null;
        var road = _roads[id.Value];
        float width = HalfWidth(RoadType.Avenue2x2) * 2f;
        float cut = 0f, fill = 0f;
        if (Terrain != null)
        {
            float y0 = _nodeHeights[road.From], y1 = _nodeHeights[road.To];
            int steps = Math.Max(1, (int)MathF.Ceiling(road.Length / 4f));
            float ds = road.Length / steps;
            for (int k = 0; k < steps; k++)
            {
                float t = (k + 0.5f) / steps;
                var (x, z) = road.Geometry.PointAt(road.Length * t);
                // Ground right beside the existing carriageway, where the extra width goes.
                var (nx, nz) = (-MathF.Sin(road.Geometry.HeadingAt(road.Length * t)), MathF.Cos(road.Geometry.HeadingAt(road.Length * t)));
                float edge = (HalfWidth(RoadType.TwoLane) + HalfWidth(RoadType.Avenue2x2)) * 0.5f;
                float height = 0.5f * (Terrain.HeightAt(x + nx * edge, z + nz * edge) + Terrain.HeightAt(x - nx * edge, z - nz * edge)) - (y0 + (y1 - y0) * t);
                float h = MathF.Abs(height);
                float section = h * (width - HalfWidth(RoadType.TwoLane) * 2f) + Tuning.Roads.EarthworkSlope * h * h;
                if (height > 0f) cut += section * ds; else fill += section * ds;
            }
        }
        var prices = Tuning.Roads;
        float perMetre = prices.Avenue.CostPerMetre - prices.UpgradeRefundShare * prices.TwoLane.CostPerMetre;
        return new RoadCost(road.Length, cut, fill, road.Length * perMetre + cut * prices.CutCostPerCubicMetre + fill * prices.FillCostPerCubicMetre);
    }

    /// <summary>
    /// Replaces a two-lane road by an avenue on the same centre line. Vehicles carry on in the matching lane of the new
    /// road. Returns the new road, or null if the road cannot be upgraded.
    /// </summary>
    public RoadId? UpgradeRoad(RoadId id, float speedLimit)
    {
        if (!CanUpgrade(id) || !ValidLimit(speedLimit)) return null;
        var old = _roads[id.Value];
        var upgraded = Reshape(old, RoadType.Avenue2x2, speedLimit);

        if (Terrain != null)
        {
            var path = new List<(float, float, float)>();
            int steps = Math.Max(1, (int)MathF.Ceiling(old.Length / 4f));
            float y0 = _nodeHeights[old.From], y1 = _nodeHeights[old.To];
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                var (x, z) = old.Geometry.PointAt(old.Length * t);
                path.Add((x, z, y0 + (y1 - y0) * t));
            }
            Terrain.CarvePath(path, HalfWidth(RoadType.Avenue2x2), Shoulder);
        }
        return upgraded.Id;
    }

    // ---- Moving a node ----------------------------------------------------------------------------------------

    /// <summary>One road to rebuild when a node moves: the new road's ends, geometry and plan.</summary>
    sealed record MovedRoad(Road Old, int From, int To, float X0, float Z0, float X1, float Z1, RoadGeometry Geometry, List<Meeting> Plan);

    /// <summary>
    /// What it costs to drag a node to a new place, or null if it cannot be moved there. Every road at the node is torn
    /// down and rebuilt straight from the moved node to its other end, so the price is their demolition plus new
    /// construction. Refused when: the node has no roads, the target is within a metre of where it is or on another
    /// node, or any rebuilt road would be too short, too steep, or cross another road.
    /// </summary>
    public RoadCost? EstimateMoveNode(int node, float x, float z)
    {
        var moved = PlanMove(node, x, z);
        if (moved == null) return null;
        float length = 0f, cut = 0f, fill = 0f, money = 0f;
        foreach (var m in moved)
        {
            var cost = Price(m.Plan, m.Geometry, m.Old.Type);
            length += cost.Length; cut += cost.CutVolume; fill += cost.FillVolume;
            money += cost.Money + DemolitionCost(m.Old.Id);
        }
        return new RoadCost(length, cut, fill, money);
    }

    /// <summary>
    /// Drags a node to a new place (see <see cref="EstimateMoveNode"/>). Vehicles on the roads at the node disappear with
    /// them, and a depot at the old place closes. Returns false when the node cannot be moved there.
    /// </summary>
    public bool MoveNode(int node, float x, float z)
    {
        var moved = PlanMove(node, x, z);
        if (moved == null) return false;
        foreach (var m in moved) Retire(m.Old);
        int target = AddNode(x, z, GroundHeight(x, z));
        foreach (var m in moved)
        {
            int from = m.From == node ? target : m.From, to = m.To == node ? target : m.To;
            AddRoad(from, to, m.Old.Type, m.Old.SpeedLimit);
            Terrain?.CarvePath(Profile(m.Geometry, m.Plan[0], m.Plan[1]), HalfWidth(m.Old.Type), Shoulder);
        }
        RebuildJunction(node);
        Version++;
        return true;
    }

    const float MinAngleBetweenRoads = 0.05f; // radians

    /// <summary>Direction, as an angle, in which a rebuilt road leaves the moved node.</summary>
    float AngleFrom(MovedRoad m, int node, float x, float z)
    {
        var (fx, fz) = _nodes[m.From == node ? m.To : m.From];
        return MathF.Atan2(fz - z, fx - x);
    }

    List<MovedRoad>? PlanMove(int node, float x, float z)
    {
        var roads = _nodeRoads[node].Select(id => _roads[id.Value]).ToList();
        if (roads.Count == 0) return null;
        var (nx, nz) = _nodes[node];
        if ((nx - x) * (nx - x) + (nz - z) * (nz - z) < 1f) return null;
        int near = FindNodeNear(x, z, 0.5f);
        if (near >= 0) return null; // moving onto another node would merge them

        var moved = new List<MovedRoad>();
        foreach (var road in roads) road.Retired = true; // so the new roads do not see the ones they replace
        try
        {
            foreach (var road in roads)
            {
                bool startsHere = road.From == node;
                var (fx, fz) = _nodes[startsHere ? road.To : road.From];
                var (x0, z0, x1, z1) = startsHere ? (x, z, fx, fz) : (fx, fz, x, z);
                var plan = Plan(x0, z0, x1, z1, 0f);
                if (plan == null || plan.Count != 2) return null; // refused, or it would cross another road
                moved.Add(new MovedRoad(road, road.From, road.To, x0, z0, x1, z1, RoadGeometry.FromChord(x0, z0, x1, z1, 0f), plan));
            }
        }
        finally
        {
            foreach (var road in roads) road.Retired = false;
        }
        // Two roads leaving the node the same way would lie on top of each other.
        for (int i = 0; i < moved.Count; i++)
            for (int j = i + 1; j < moved.Count; j++)
            {
                float ai = AngleFrom(moved[i], node, x, z), aj = AngleFrom(moved[j], node, x, z);
                float between = MathF.Abs(ai - aj);
                if (MathF.Min(between, MathF.Tau - between) < MinAngleBetweenRoads) return null;
            }
        return moved;
    }

    /// <summary>Whether an avenue can be narrowed to a two-lane road.</summary>
    public bool CanDowngrade(RoadId id) => !_roads[id.Value].Retired && _roads[id.Value].Type == RoadType.Avenue2x2;

    /// <summary>The price of narrowing an avenue to a two-lane road, or null if it cannot be. The ground keeps its earthworks.</summary>
    public float? DowngradeCost(RoadId id) =>
        CanDowngrade(id) ? _roads[id.Value].Length * Tuning.Roads.DowngradeCostPerMetre : null;

    /// <summary>
    /// Replaces an avenue by a two-lane road on the same centre line. Vehicles in the right lane carry on in the new
    /// lane; those in the left lane move into it too, and are removed when they would land on another vehicle.
    /// Returns the new road, or null if the road cannot be downgraded.
    /// </summary>
    public RoadId? DowngradeRoad(RoadId id, float speedLimit)
    {
        if (!CanDowngrade(id) || !ValidLimit(speedLimit)) return null;
        return Reshape(_roads[id.Value], RoadType.TwoLane, speedLimit).Id;
    }

    /// <summary>The price of re-signing a road with a new speed limit, or null if the road is gone.</summary>
    public float? SpeedLimitChangeCost(RoadId id) =>
        _roads[id.Value].Retired ? null : _roads[id.Value].Length * Tuning.Roads.SpeedLimitChangeCostPerMetre;

    /// <summary>Changes a road's posted speed limit; vehicles carry on in the matching lane. Returns the new road, or null when the road is gone, the limit is not positive, or it is unchanged.</summary>
    public RoadId? SetSpeedLimit(RoadId id, float speedLimit)
    {
        var road = _roads[id.Value];
        if (road.Retired || !ValidLimit(speedLimit) || speedLimit == road.SpeedLimit) return null;
        return Reshape(road, road.Type, speedLimit).Id;
    }

    /// <summary>Retires a road and builds another of the given type and limit between the same nodes on the same centre line; each old lane hands its vehicles to the matching new lane (the last one when the new road has fewer).</summary>
    Road Reshape(Road old, RoadType type, float speedLimit)
    {
        Retire(old);
        var replacement = _roads[AddRoad(old.From, old.To, type, speedLimit, old.Geometry.Sweep).Value];
        HandOver(old.ForwardLanes, replacement.ForwardLanes);
        HandOver(old.BackwardLanes, replacement.BackwardLanes);
        return replacement;
    }

    void HandOver(LaneId[] from, LaneId[] to)
    {
        for (int i = 0; i < from.Length; i++)
        {
            Replace(from[i], to[Math.Min(i, to.Length - 1)], LaneId.None, float.MaxValue);
            _lanes[from[i].Value].SplitMerges = i >= to.Length;
        }
    }

    /// <summary>
    /// Replaces a road by two roads meeting at a new node <paramref name="distance"/> metres from its start.
    /// The old road and its lanes are retired; each old lane records its replacements so vehicles can move over.
    /// </summary>
    public int SplitRoad(RoadId id, float distance)
    {
        var road = _roads[id.Value];
        if (road.Retired) throw new InvalidOperationException("Road is already replaced.");
        float length = road.Length;
        if (distance < MinRoadLength || distance > length - MinRoadLength) throw new ArgumentOutOfRangeException(nameof(distance));

        var geometry = road.Geometry;
        var (mx, mz) = geometry.PointAt(distance);
        int middle = AddNode(mx, mz, RoadHeightAt(id, distance));

        road.Retired = true;
        foreach (var lane in road.ForwardLanes.Concat(road.BackwardLanes))
        {
            _lanes[lane.Value].Retired = true;
            _incoming[_lanes[lane.Value].EndNode].Remove(lane);
        }
        _nodeRoads[road.From].Remove(id);
        _nodeRoads[road.To].Remove(id);

        float sweepFirst = road.Geometry.Sweep * (distance / length);
        var first = _roads[AddRoad(road.From, middle, road.Type, road.SpeedLimit, sweepFirst).Value];
        var second = _roads[AddRoad(middle, road.To, road.Type, road.SpeedLimit, road.Geometry.Sweep - sweepFirst).Value];
        for (int i = 0; i < road.ForwardLanes.Length; i++)
            Replace(road.ForwardLanes[i], first.ForwardLanes[i], second.ForwardLanes[i], first.Length);
        for (int i = 0; i < road.BackwardLanes.Length; i++)
            Replace(road.BackwardLanes[i], second.BackwardLanes[i], first.BackwardLanes[i], second.Length);
        return middle;
    }

    void Replace(LaneId old, LaneId first, LaneId second, float firstLength)
    {
        var lane = _lanes[old.Value];
        lane.SplitFirst = first;
        lane.SplitSecond = second;
        lane.SplitDistance = firstLength;
    }

    public RoadId AddRoad(int from, int to, RoadType type, float speedLimit, float sweep = 0f)
    {
        if (from == to) throw new ArgumentException("A road needs two distinct nodes.");
        var (x0, z0) = _nodes[from];
        var (x1, z1) = _nodes[to];
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        float length = geometry.Length;
        if (length < MinRoadLength) throw new ArgumentException($"Road is shorter than {MinRoadLength} m.");
        if (!(speedLimit > 0f)) throw new ArgumentOutOfRangeException(nameof(speedLimit), "Speed limit must be positive.");

        var id = new RoadId(_roads.Count);
        int perDirection = type == RoadType.TwoLane ? 1 : 2;
        float rise = _nodeHeights[to] - _nodeHeights[from];
        float curveLimit = geometry.IsStraight ? float.PositiveInfinity : MathF.Sqrt(ComfortLateralAcceleration * geometry.Radius);
        var forward = NewCarriageway(id, type, perDirection, speedLimit, curveLimit, from, to, geometry, rise / length);
        var backward = NewCarriageway(id, type, perDirection, speedLimit, curveLimit, to, from, geometry.Reversed(), -rise / length);
        if (type == RoadType.TwoLane)
        {
            _lanes[forward[0].Value].Oncoming = backward[0];
            _lanes[backward[0].Value].Oncoming = forward[0];
        }

        _roads.Add(new Road(id, from, to, type, speedLimit, geometry, forward, backward));
        _nodeRoads[from].Add(id);
        _nodeRoads[to].Add(id);
        RebuildJunction(from);
        RebuildJunction(to);
        Version++;
        return id;
    }

    LaneId[] NewCarriageway(RoadId road, RoadType type, int count, float speedLimit, float curveLimit,
        int startNode, int endNode, RoadGeometry path, float grade)
    {
        float limit = MathF.Min(speedLimit * GradeFactor(grade), curveLimit);
        float median = type == RoadType.Avenue2x2 ? MedianWidth : 0f;
        var ids = new LaneId[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = new LaneId(_lanes.Count);
            // To the driver's right of the centre line. Lane 0 is the right-most, so it is furthest out.
            float side = median * 0.5f + (count - 1 - i + 0.5f) * LaneWidth;
            _lanes.Add(new Lane(ids[i], road, i, limit, path, side, startNode, endNode, grade));
            _incoming[endNode].Add(ids[i]);
        }
        for (int i = 0; i + 1 < count; i++)
        {
            _lanes[ids[i].Value].Left = ids[i + 1];
            _lanes[ids[i + 1].Value].Right = ids[i];
        }
        return ids;
    }

    /// <summary>1 on level ground, falling linearly to 0.5 at <see cref="MaxGrade"/>, either direction.</summary>
    static float GradeFactor(float grade)
    {
        float steep = (MathF.Abs(grade) - FreeGrade) / (MaxGrade - FreeGrade);
        return 1f - 0.5f * Math.Clamp(steep, 0f, 1f);
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
