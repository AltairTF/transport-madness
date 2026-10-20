namespace TransportMadness.Sim;

public enum RoadType
{
    /// <summary>One lane per direction; opposing lanes may be used to overtake.</summary>
    TwoLane,
    /// <summary>Two lanes per direction on a separated carriageway; free lane choice, no overtaking via the oncoming side.</summary>
    Avenue2x2,
}

/// <summary>How a road piece is carried over or through the ground.</summary>
public enum RoadStructure
{
    /// <summary>On the ground, which is cut or filled to the road's level.</summary>
    None,
    /// <summary>Raised on a deck over a valley or water; the ground is left alone.</summary>
    Bridge,
    /// <summary>Through a hill; the ground is left alone.</summary>
    Tunnel,
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
    /// <summary>Set when the road was rebuilt between other nodes (a node moved): vehicles move to the pieces of the rebuilt road, in proportion.</summary>
    public LaneRemap? Remap { get; internal set; }
}

/// <summary>Where the vehicles of a retired lane go when its road is rebuilt in another place.</summary>
/// <param name="Pieces">The lanes of the rebuilt road, in the direction of travel.</param>
/// <param name="Starts">Distance along the rebuilt road at which each piece starts.</param>
/// <param name="Scale">New length over old length: a position on the old lane is multiplied by it.</param>
public sealed record LaneRemap(LaneId[] Pieces, float[] Starts, float Scale)
{
    /// <summary>The piece a vehicle <paramref name="position"/> metres along the old lane moves to, and its position on it.</summary>
    public (LaneId Lane, float Position) Resolve(float position)
    {
        float along = position * Scale;
        int k = Pieces.Length - 1;
        while (k > 0 && along < Starts[k]) k--;
        return (Pieces[k], along - Starts[k]);
    }
}

public sealed class Road
{
    internal Road(RoadId id, int from, int to, RoadType type, float speedLimit, RoadGeometry geometry, LaneId[] forward, LaneId[] backward,
        RoadStructure structure = RoadStructure.None)
    {
        Structure = structure;
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
    public RoadStructure Structure { get; }
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
public readonly record struct RoadCost(float Length, float CutVolume, float FillVolume, float Money, float BridgeLength = 0f, float TunnelLength = 0f);

/// <summary>Stretches of two junction paths where vehicles on them can touch, as distances from where each path starts (see <see cref="RoadNetwork.GetConflictZone"/>).</summary>
public readonly record struct ConflictZone(float InA, float OutA, float InB, float OutB);

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
    readonly Dictionary<(int, int, int, int), bool> _wideConflicts = new(); // for pairs with a long vehicle (see DrivingTuning.WideBodyClearance)

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
    /// <paramref name="inB"/> to <paramref name="outB"/>: they merge into the same lane, their paths cross, or they run
    /// closer than a vehicle is wide.
    /// A path runs from the end of the incoming lane to the start of the outgoing one along a smooth curve that
    /// keeps the heading of each lane at its end, so lane offsets and the width of the junction are taken into account.
    /// </summary>
    public bool MovesConflict(LaneId inA, LaneId outA, LaneId inB, LaneId outB, bool wide = false)
    {
        if (outA == outB) return inA != inB;
        if (inA == inB) return false;
        var key = (inA.Value, outA.Value, inB.Value, outB.Value);
        var cache = wide ? _wideConflicts : _conflicts;
        if (cache.TryGetValue(key, out bool known)) return known;
        var pa = MovePath(inA, outA);
        var pb = MovePath(inB, outB);
        bool result = PathsCross(pa, pb) || PathsTouch(pa, pb, wide ? Tuning.Driving.WideBodyClearance : BodyClearance);
        cache[key] = result;
        return result;
    }

    /// <summary>
    /// Where two conflicting moves meet, as stretches of their paths: a vehicle whose centre is inside its stretch can be
    /// touching one whose centre is inside the other's. A stretch runs from the first to the last point of its path (as a
    /// distance from where the path starts) that lies within <paramref name="distance"/> of the other path; two moves
    /// into the same lane stay in conflict to the end of their paths. Null when the moves do not conflict.
    /// </summary>
    public ConflictZone? GetConflictZone(LaneId inA, LaneId outA, LaneId inB, LaneId outB, float distance, bool wide = false)
    {
        if (!MovesConflict(inA, outA, inB, outB, wide)) return null;
        var a = Resample(MovePath(inA, outA));
        var b = Resample(MovePath(inB, outB));
        float aIn = float.MaxValue, aOut = float.MinValue, bIn = float.MaxValue, bOut = float.MinValue;
        foreach (var (ax, az, sa) in a)
            foreach (var (bx, bz, sb) in b)
            {
                if ((ax - bx) * (ax - bx) + (az - bz) * (az - bz) >= distance * distance) continue;
                aIn = MathF.Min(aIn, sa); aOut = MathF.Max(aOut, sa);
                bIn = MathF.Min(bIn, sb); bOut = MathF.Max(bOut, sb);
            }
        float lengthA = a[^1].S, lengthB = b[^1].S;
        if (aOut < aIn)
        {
            // Paths cross between samples or merge without coming that close: take the last stretch.
            aIn = MathF.Max(0f, lengthA - 1f); aOut = lengthA;
            bIn = MathF.Max(0f, lengthB - 1f); bOut = lengthB;
        }
        if (outA == outB) { aOut = lengthA; bOut = lengthB; }
        return new ConflictZone(aIn, aOut, bIn, bOut);
    }

    /// <summary>A path cut into pieces about half a metre long, each point with its distance along the path.</summary>
    static List<(float X, float Z, float S)> Resample((float X, float Z)[] path)
    {
        var points = new List<(float, float, float)> { (path[0].X, path[0].Z, 0f) };
        float travelled = 0f;
        for (int i = 0; i + 1 < path.Length; i++)
        {
            float length = Distance(path[i], path[i + 1]);
            int pieces = Math.Max(1, (int)MathF.Ceiling(length / 0.5f));
            for (int k = 1; k <= pieces; k++)
            {
                float t = k / (float)pieces;
                points.Add((path[i].X + (path[i + 1].X - path[i].X) * t, path[i].Z + (path[i + 1].Z - path[i].Z) * t, travelled + length * t));
            }
            travelled += length;
        }
        return points;
    }

    /// <summary>How far into a lane of the given length a junction path reaches (from its end, or from its start).</summary>
    public static float JunctionReach(float laneLength) => MathF.Min(JunctionRadius, laneLength * 0.5f);

    /// <summary>Length of the path through a junction from the end of lane <paramref name="from"/> to the start of <paramref name="to"/>.</summary>
    public float JunctionLength(LaneId from, LaneId to)
    {
        var path = MovePath(from, to);
        float total = 0f;
        for (int i = 0; i + 1 < path.Length; i++) total += Distance(path[i], path[i + 1]);
        return total;
    }

    /// <summary>How far the heading turns along the path through a junction, as a positive angle in [0, pi].</summary>
    public float JunctionTurn(LaneId from, LaneId to)
    {
        float a = JunctionPointAt(from, to, 0f).Heading, b = JunctionPointAt(from, to, float.MaxValue).Heading;
        float d = MathF.Abs(b - a) % MathF.Tau;
        return MathF.Min(d, MathF.Tau - d);
    }

    /// <summary>
    /// Where a vehicle is when it has gone <paramref name="distance"/> metres along the path from lane <paramref name="from"/>
    /// to lane <paramref name="to"/> through their junction, and which way it faces. The path starts
    /// <see cref="JunctionReach"/> before the end of <paramref name="from"/> and ends the same distance into
    /// <paramref name="to"/>; distances outside it are clamped to its ends.
    /// </summary>
    public (float X, float Z, float Heading) JunctionPointAt(LaneId from, LaneId to, float distance)
    {
        var path = MovePath(from, to);
        float target = Math.Max(0f, distance);
        for (int i = 0; i + 1 < path.Length; i++)
        {
            float length = Distance(path[i], path[i + 1]);
            if (target > length && i + 2 < path.Length) { target -= length; continue; }
            float t = length > 1e-6f ? Math.Clamp(target / length, 0f, 1f) : 0f;
            float dx = path[i + 1].X - path[i].X, dz = path[i + 1].Z - path[i].Z;
            // The path is a polyline; blending the heading of a piece into its neighbours' (each piece's own heading
            // holds at its middle) makes the heading change smoothly instead of in steps at the corners.
            float heading = MathF.Atan2(dz, dx);
            if (t < 0.5f && i > 0) heading = BlendAngle(SegmentHeading(path, i - 1), heading, 0.5f + t);
            else if (t >= 0.5f && i + 2 < path.Length) heading = BlendAngle(heading, SegmentHeading(path, i + 1), t - 0.5f);
            return (path[i].X + dx * t, path[i].Z + dz * t, heading);
        }
        return (path[0].X, path[0].Z, 0f);
    }

    static float SegmentHeading((float X, float Z)[] path, int i) => MathF.Atan2(path[i + 1].Z - path[i].Z, path[i + 1].X - path[i].X);

    /// <summary>The angle <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/> by the shorter turn.</summary>
    static float BlendAngle(float a, float b, float t)
    {
        float d = b - a;
        d -= MathF.Tau * MathF.Floor((d + MathF.PI) / MathF.Tau);
        return a + d * t;
    }

    /// <summary>The same pose as <see cref="JunctionPointAt"/>, given as a <paramref name="fraction"/> (0 to 1) of the path.</summary>
    public (float X, float Z, float Heading) JunctionPoint(LaneId from, LaneId to, float fraction) =>
        JunctionPointAt(from, to, Math.Clamp(fraction, 0f, 1f) * JunctionLength(from, to));

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

    /// <summary>A vehicle reaches half its length ahead of its path and half its width to the side: paths that never cross but run closer than the two together (3.2 m) still bring vehicles together.</summary>
    const float BodyClearance = 3.3f;

    static bool PathsTouch((float X, float Z)[] p, (float X, float Z)[] q, float clearance)
    {
        var a = Resample(p);
        var b = Resample(q);
        foreach (var (ax, az, _) in a)
            foreach (var (bx, bz, _) in b)
                if ((ax - bx) * (ax - bx) + (az - bz) * (az - bz) < clearance * clearance) return true;
        return false;
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

    static float NearestAlong(RoadGeometry g, float x, float z) => g.NearestAlong(x, z);

    // ---- Planning and building ------------------------------------------------------------------------------

    /// <summary>One place along a planned road where it meets the network: an existing node, or a point inside a road to split.</summary>
    /// <param name="After">What the planned road is from here to the next meeting.</param>
    /// <param name="Tight">The meeting is inside a road too close to one of its ends for a junction: it only works if the planned road passes over or under that road instead.</param>
    readonly record struct Meeting(float Along, int Node, RoadId Road, float RoadDistance, float X, float Z, float Y, RoadStructure After = RoadStructure.None, bool Tight = false);

    /// <summary>
    /// Builds a road, straight or bending by <paramref name="sweep"/> radians, joining the network wherever it touches
    /// it: at existing nodes, and by splitting any road it ends on or crosses so that a junction forms there. Returns
    /// the new road pieces, or none if it cannot be built (too short, too steep, too tight a curve, a duplicate, or
    /// crossing within <see cref="MinRoadLength"/> of another junction, or an end in water).
    /// Stretches that would need a deep fill or cut, or that cross water, are built as bridges or tunnels (see <see cref="RoadStructure"/>).
    /// Where the new road meets another one at a different height (at least <c>crossingClearance</c> apart, with a bridge
    /// above or a tunnel below) it passes over or under it and no junction forms.
    /// </summary>
    public IReadOnlyList<RoadId> BuildRoad(float x0, float z0, float x1, float z1, RoadType type, float speedLimit, float sweep = 0f)
    {
        var plan = Plan(x0, z0, x1, z1, sweep);
        if (plan == null) return Array.Empty<RoadId>();
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);

        var nodes = new List<int>(plan.Count);
        var splits = new Dictionary<int, (RoadId First, RoadId Second, float At)>(); // a bending road can meet the same road more than once
        foreach (var m in plan)
        {
            if (m.Node >= 0) nodes.Add(m.Node);
            else if (m.Road.Value >= 0)
            {
                var road = m.Road;
                float distance = m.RoadDistance;
                while (splits.TryGetValue(road.Value, out var split))
                {
                    if (distance < split.At) road = split.First;
                    else { distance -= split.At; road = split.Second; }
                }
                nodes.Add(SplitRoad(road, distance, out var first, out var second));
                splits[road.Value] = (first, second, _roads[first.Value].Length);
            }
            else nodes.Add(AddNode(m.X, m.Z, m.Y));
        }
        var built = new List<RoadId>();
        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            float pieceSweep = sweep * (plan[i + 1].Along - plan[i].Along) / geometry.Length;
            built.Add(AddRoad(nodes[i], nodes[i + 1], type, speedLimit, pieceSweep, plan[i].After));
            if (plan[i].After == RoadStructure.None) Terrain?.CarvePath(Profile(geometry, plan[i], plan[i + 1]), HalfWidth(type), Shoulder);
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
        float cut = 0f, fill = 0f, length = 0f, bridge = 0f, tunnel = 0f, money = 0f;
        var prices = Tuning.Roads;
        for (int i = 0; i + 1 < plan.Count; i++)
        {
            var a = plan[i];
            var b = plan[i + 1];
            float run = b.Along - a.Along;
            length += run;
            if (a.After != RoadStructure.None)
            {
                // A structure replaces the road and its earthworks with one price.
                if (a.After == RoadStructure.Bridge) bridge += run; else tunnel += run;
                money += run * prices.StructureCostPerMetre(a.After, type);
                continue;
            }
            money += run * prices.Of(type).CostPerMetre;
            if (Terrain == null) continue;
            int steps = Math.Max(1, (int)MathF.Ceiling(run / 4f));
            float ds = run / steps;
            float cutHere = 0f, fillHere = 0f;
            for (int k = 0; k < steps; k++)
            {
                float t = (k + 0.5f) / steps;
                var (x, z) = geometry.PointAt(a.Along + run * t);
                float height = Terrain.HeightAt(x, z) - (a.Y + (b.Y - a.Y) * t);
                float h = MathF.Abs(height);
                float section = h * width + prices.EarthworkSlope * h * h;
                if (height > 0f) cutHere += section * ds; else fillHere += section * ds;
            }
            cut += cutHere; fill += fillHere;
            money += cutHere * prices.CutCostPerCubicMetre + fillHere * prices.FillCostPerCubicMetre;
        }
        return new RoadCost(length, cut, fill, money, bridge, tunnel);
    }

    public bool CanBuildRoad(float x0, float z0, float x1, float z1, float sweep = 0f) => Plan(x0, z0, x1, z1, sweep) != null;

    /// <summary>
    /// Where a planned road would meet the network, as distances along it with the map position and road height: its two ends, every
    /// junction, and each change between ground, bridge and tunnel. Null when it cannot be built. Used to check, before building, what
    /// else (a level crossing, say) the road would run into.
    /// </summary>
    internal IReadOnlyList<(float Along, float X, float Z, float Y, bool Junction)>? PlannedPoints(float x0, float z0, float x1, float z1, float sweep = 0f)
    {
        var plan = Plan(x0, z0, x1, z1, sweep);
        if (plan == null) return null;
        var list = new List<(float, float, float, float, bool)>(plan.Count);
        foreach (var m in plan) list.Add((m.Along, m.X, m.Z, m.Y, m.Node >= 0 || m.Road.Value >= 0));
        return list;
    }

    /// <summary>
    /// The meetings along the planned road, start to end, or null when it cannot be built. Besides the places where it
    /// meets the network there is one wherever it changes between ground, bridge and tunnel.
    /// </summary>
    List<Meeting>? Plan(float x0, float z0, float x1, float z1, float sweep)
    {
        var meetings = PlanJunctions(x0, z0, x1, z1, sweep);
        if (meetings == null) return null;
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        if (Terrain != null)
        {
            // Both ends stand on dry land.
            if (meetings[0].Node < 0 && meetings[0].Road.Value < 0 && Terrain.IsWater(meetings[0].X, meetings[0].Z)) return null;
            var last = meetings[^1];
            if (last.Node < 0 && last.Road.Value < 0 && Terrain.IsWater(last.X, last.Z)) return null;
            meetings = SeparateCrossings(meetings, geometry);
            if (meetings == null) return null;
        }
        if (!PiecesAllowed(meetings)) return null;
        return Terrain == null ? meetings : InsertStructures(meetings, geometry);
    }

    /// <summary>Whether every piece between two meetings can be a road: long enough, not a duplicate, not too steep, and no junction left in a road too close to its end.</summary>
    bool PiecesAllowed(List<Meeting> meetings)
    {
        foreach (var m in meetings)
            if (m.Tight) return false;
        for (int i = 0; i + 1 < meetings.Count; i++)
        {
            float run = meetings[i + 1].Along - meetings[i].Along;
            if (run < MinRoadLength) return false;
            int na = meetings[i].Node, nb = meetings[i + 1].Node;
            if (na >= 0 && nb >= 0 && FindRoad(na, nb) != null) return false;
            if (meetings[i].Node < 0 && meetings[i + 1].Node < 0 && meetings[i].Road.Value >= 0 && meetings[i].Road == meetings[i + 1].Road
                && MathF.Abs(meetings[i + 1].RoadDistance - meetings[i].RoadDistance) < MinRoadLength) return false; // two junctions too close on one road
            if (MathF.Abs(meetings[i + 1].Y - meetings[i].Y) > MaxGrade * run) return false; // too steep
        }
        return true;
    }

    /// <summary>
    /// Drops the junctions where the planned road would pass over or under the road it meets: they are at least
    /// <c>crossingClearance</c> apart in height, with a bridge above or a tunnel below. Null when the plan cannot be built.
    /// </summary>
    List<Meeting>? SeparateCrossings(List<Meeting> meetings, RoadGeometry geometry)
    {
        var kept = new List<Meeting>(meetings);
        var dropped = new List<Meeting>();
        for (int k = 0; k < kept.Count;)
        {
            var m = kept[k];
            if (m.Node >= 0 || m.Road.Value < 0 || k == 0 || k == kept.Count - 1) { k++; continue; } // the ends of the road stay
            var without = new List<Meeting>(kept);
            without.RemoveAt(k);
            if (!PassesOver(m, without, geometry)) { k++; continue; }
            kept = without;
            dropped.Add(m);
        }
        // Dropping one crossing moves the profile at the others, so check them all against the final plan.
        foreach (var m in dropped)
            if (!PassesOver(m, kept, geometry)) return null;
        return kept;
    }

    /// <summary>Whether a planned road with the given other meetings would clear the road at <paramref name="crossing"/> by enough, on a structure.</summary>
    bool PassesOver(Meeting crossing, List<Meeting> others, RoadGeometry geometry)
    {
        int i = 0;
        while (i + 2 < others.Count && others[i + 1].Along < crossing.Along) i++;
        float span = others[i + 1].Along - others[i].Along;
        float t = span > 1e-6f ? Math.Clamp((crossing.Along - others[i].Along) / span, 0f, 1f) : 0f;
        float height = others[i].Y + (others[i + 1].Y - others[i].Y) * t;
        float gap = height - crossing.Y;
        float clearance = Tuning.Roads.CrossingClearance;
        if (MathF.Abs(gap) < clearance) return false;

        var plan = InsertStructures(others, geometry);
        bool bridge = false, tunnel = false;
        for (int k = 0; k + 1 < plan.Count; k++)
        {
            if (plan[k].Along > crossing.Along || plan[k + 1].Along < crossing.Along) continue;
            bridge |= plan[k].After == RoadStructure.Bridge;
            tunnel |= plan[k].After == RoadStructure.Tunnel;
        }
        var old = _roads[crossing.Road.Value].Structure;
        return gap > 0f ? bridge || old == RoadStructure.Tunnel : old == RoadStructure.Bridge || tunnel;
    }

    const float StructureSampleSpacing = 4f;

    /// <summary>
    /// Walks each stretch between two meetings and marks where the road would need a bridge (over water, or a fill deeper
    /// than <c>MaxFillDepth</c>) or a tunnel (a cut deeper than <c>MaxCutDepth</c>), widened by the margin and cleaned up so
    /// that no piece is shorter than <see cref="MinRoadLength"/>. New meetings are added where the kind of road changes.
    /// </summary>
    List<Meeting> InsertStructures(List<Meeting> meetings, RoadGeometry geometry)
    {
        var result = new List<Meeting>();
        for (int s = 0; s + 1 < meetings.Count; s++)
        {
            var a = meetings[s];
            var b = meetings[s + 1];
            float run = b.Along - a.Along;
            int steps = Math.Max(1, (int)MathF.Ceiling(run / StructureSampleSpacing));
            var kind = new RoadStructure[steps];
            bool any = false;
            for (int k = 0; k < steps; k++)
            {
                float t = (k + 0.5f) / steps;
                var (x, z) = geometry.PointAt(a.Along + run * t);
                kind[k] = StructureNeeded(x, z, a.Y + (b.Y - a.Y) * t);
                any |= kind[k] != RoadStructure.None;
            }
            if (!any) { result.Add(a); continue; }

            float cell = run / steps;
            kind = Widen(kind, (int)MathF.Ceiling(Tuning.Roads.StructureMargin / cell));
            kind = Cleanup(kind, cell);
            // One meeting at the start of every run of the same kind; the first run starts at the existing meeting.
            for (int k = 0; k < steps; k++)
            {
                if (k > 0 && kind[k] == kind[k - 1]) continue;
                if (k == 0) { result.Add(a with { After = kind[0] }); continue; }
                float along = a.Along + cell * k;
                var (x, z) = geometry.PointAt(along);
                result.Add(new Meeting(along, -1, new RoadId(-1), 0f, x, z, a.Y + (b.Y - a.Y) * (k / (float)steps), kind[k]));
            }
        }
        result.Add(meetings[^1]);
        return result;
    }

    RoadStructure StructureNeeded(float x, float z, float roadHeight)
    {
        if (Terrain!.IsWater(x, z)) return RoadStructure.Bridge;
        float ground = Terrain.HeightAt(x, z);
        if (roadHeight - ground > Tuning.Roads.MaxFillDepth) return RoadStructure.Bridge;
        if (ground - roadHeight > Tuning.Roads.MaxCutDepth) return RoadStructure.Tunnel;
        return RoadStructure.None;
    }

    /// <summary>Extends every structure by <paramref name="margin"/> samples each way; a bridge wins over a tunnel where they meet.</summary>
    static RoadStructure[] Widen(RoadStructure[] kind, int margin)
    {
        var widened = (RoadStructure[])kind.Clone();
        for (int k = 0; k < kind.Length; k++)
        {
            if (kind[k] == RoadStructure.None) continue;
            for (int d = -margin; d <= margin; d++)
            {
                int q = k + d;
                if (q < 0 || q >= kind.Length) continue;
                if (widened[q] == RoadStructure.None || (kind[k] == RoadStructure.Bridge && widened[q] == RoadStructure.Tunnel)) widened[q] = kind[k];
            }
        }
        return widened;
    }

    /// <summary>Absorbs runs shorter than a road piece can be into the run before them (or after, at the start).</summary>
    static RoadStructure[] Cleanup(RoadStructure[] kind, float cell)
    {
        int minSamples = (int)MathF.Ceiling(MinRoadLength / cell);
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int start = 0; start < kind.Length;)
            {
                int end = start;
                while (end + 1 < kind.Length && kind[end + 1] == kind[start]) end++;
                bool whole = start == 0 && end == kind.Length - 1;
                if (!whole && end - start + 1 < minSamples)
                {
                    var into = start > 0 ? kind[start - 1] : kind[end + 1];
                    for (int q = start; q <= end; q++) kind[q] = into;
                    changed = true;
                    break;
                }
                start = end + 1;
            }
        }
        return kind;
    }

    /// <summary>The meetings with the network along the planned road, start to end, or null when it cannot be built.</summary>
    List<Meeting>? PlanJunctions(float x0, float z0, float x1, float z1, float sweep)
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
                Touch(meetings, road, s * length, u * roadLength, x0 + dx * s, z0 + dz * s);
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
                    Touch(meetings, road, along, (j + u) * stepOld, px, pz);
                }
        }

        // Make sure both ends are part of the plan.
        if (!meetings.Exists(m => m.Along <= onNode)) meetings.Add(new Meeting(0f, a, new RoadId(-1), 0f, x0, z0, a >= 0 ? _nodeHeights[a] : GroundHeight(x0, z0)));
        if (!meetings.Exists(m => m.Along >= length - onNode)) meetings.Add(new Meeting(length, b, new RoadId(-1), 0f, x1, z1, b >= 0 ? _nodeHeights[b] : GroundHeight(x1, z1)));
        meetings.Sort((p, q) => p.Along.CompareTo(q.Along));
        return meetings;
    }

    /// <summary>
    /// Records where the planned road touches <paramref name="road"/>: at one of its end nodes, or inside it (a junction,
    /// or a crossing where the planned road passes over or under it, see <see cref="SeparateCrossings"/>).
    /// </summary>
    void Touch(List<Meeting> meetings, Road road, float along, float roadAlong, float x, float z)
    {
        const float onNode = 0.5f;
        float roadLength = road.Length;
        if (roadAlong <= onNode || roadAlong >= roadLength - onNode)
        {
            int node = roadAlong <= onNode ? road.From : road.To;
            if (!meetings.Exists(m => m.Node == node)) meetings.Add(new Meeting(along, node, new RoadId(-1), 0f, _nodes[node].X, _nodes[node].Z, _nodeHeights[node]));
            return;
        }
        bool tight = roadAlong < MinRoadLength || roadAlong > roadLength - MinRoadLength;
        meetings.Add(new Meeting(along, -1, road.Id, roadAlong, x, z, RoadHeightAt(road.Id, roadAlong), Tight: tight));
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
        var prices = Tuning.Roads;
        if (road.Structure != RoadStructure.None)
        {
            // On a bridge or in a tunnel there are no earthworks: the deck or bore is simply wider.
            float structurePerMetre = prices.StructureCostPerMetre(road.Structure, RoadType.Avenue2x2) - prices.UpgradeRefundShare * prices.StructureCostPerMetre(road.Structure, RoadType.TwoLane);
            return new RoadCost(road.Length, 0f, 0f, road.Length * structurePerMetre,
                road.Structure == RoadStructure.Bridge ? road.Length : 0f, road.Structure == RoadStructure.Tunnel ? road.Length : 0f);
        }
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

        if (Terrain != null && old.Structure == RoadStructure.None)
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

    /// <summary>One road to rebuild when a node moves: the new road's ends, bend and plan.</summary>
    sealed record MovedRoad(Road Old, float X0, float Z0, float X1, float Z1, float Sweep, RoadGeometry Geometry, List<Meeting> Plan);

    /// <summary>
    /// What it costs to drag a node to a new place, or null if it cannot be moved there. Every road at the node is torn
    /// down and rebuilt from the moved node to its other end with the same bend (total turn), so the price is their
    /// demolition plus new construction. A rebuilt road joins the roads it ends on or crosses and becomes a bridge or
    /// tunnel where the ground calls for one, like any new road. Refused when: the node has no roads, the target is
    /// within a metre of where it is, on another node or in water, or any rebuilt road would be too short, too steep,
    /// too tightly curved, or cross another rebuilt road.
    /// </summary>
    public RoadCost? EstimateMoveNode(int node, float x, float z)
    {
        var moved = PlanMove(node, x, z);
        if (moved == null) return null;
        float length = 0f, cut = 0f, fill = 0f, money = 0f, bridge = 0f, tunnel = 0f;
        foreach (var m in moved)
        {
            var cost = Price(m.Plan, m.Geometry, m.Old.Type);
            length += cost.Length; cut += cost.CutVolume; fill += cost.FillVolume; bridge += cost.BridgeLength; tunnel += cost.TunnelLength;
            money += cost.Money + DemolitionCost(m.Old.Id);
        }
        return new RoadCost(length, cut, fill, money, bridge, tunnel);
    }

    /// <summary>The roads a move would build (centre line and type), null when the move is refused. For finding what stands in their way.</summary>
    public IReadOnlyList<(RoadGeometry Geometry, RoadType Type)>? PlannedMoveRoads(int node, float x, float z) =>
        PlanMove(node, x, z)?.Select(m => (m.Geometry, m.Old.Type)).ToList();

    /// <summary>
    /// Drags a node to a new place (see <see cref="EstimateMoveNode"/>). Vehicles on the roads at the node carry on, in
    /// proportion, on the rebuilt roads. Returns false when the node cannot be moved there.
    /// </summary>
    public bool MoveNode(int node, float x, float z) => MoveNode(node, x, z, out _);

    /// <summary>As <see cref="MoveNode(int, float, float)"/>; <paramref name="newNode"/> is the node now standing at the target (-1 when refused).</summary>
    public bool MoveNode(int node, float x, float z, out int newNode)
    {
        newNode = -1;
        var moved = PlanMove(node, x, z);
        if (moved == null) return false;
        foreach (var m in moved) Retire(m.Old);
        newNode = AddNode(x, z, GroundHeight(x, z));
        foreach (var m in moved)
        {
            // Planned again: an earlier rebuilt road may have split a road this one meets. It always works, because
            // PlanMove refused rebuilt roads that cross each other.
            var built = BuildRoad(m.X0, m.Z0, m.X1, m.Z1, m.Old.Type, m.Old.SpeedLimit, m.Sweep);
            if (built.Count == 0) throw new InvalidOperationException("A planned move could not be built.");
            HandOverMoved(m.Old, built);
        }
        Version++;
        return true;
    }

    /// <summary>Points every lane of a road that was torn down for a move at the matching lane(s) of its rebuilt pieces.</summary>
    void HandOverMoved(Road old, IReadOnlyList<RoadId> built)
    {
        var pieces = built.Select(id => _roads[id.Value]).ToList();
        float total = pieces.Sum(p => p.Length);
        float scale = total / old.Length;
        Remap(old.ForwardLanes, pieces.Select(p => p.ForwardLanes).ToList(), pieces.Select(p => p.Length).ToList(), scale);
        pieces.Reverse();
        Remap(old.BackwardLanes, pieces.Select(p => p.BackwardLanes).ToList(), pieces.Select(p => p.Length).ToList(), scale);
    }

    void Remap(LaneId[] oldLanes, List<LaneId[]> pieceLanes, List<float> lengths, float scale)
    {
        var starts = new float[lengths.Count];
        for (int k = 1; k < starts.Length; k++) starts[k] = starts[k - 1] + lengths[k - 1];
        for (int i = 0; i < oldLanes.Length; i++)
        {
            var lane = _lanes[oldLanes[i].Value];
            lane.Remap = new LaneRemap(pieceLanes.Select(p => p[Math.Min(i, p.Length - 1)]).ToArray(), starts, scale);
            lane.SplitMerges = scale < 1f; // a shorter road packs its vehicles closer together
        }
    }

    const float MinAngleBetweenRoads = 0.05f; // radians

    /// <summary>Direction, as an angle, in which a rebuilt road leaves the moved node.</summary>
    static float DepartureAngle(MovedRoad m, bool startsHere) =>
        startsHere ? m.Geometry.Heading0 : m.Geometry.HeadingAt(m.Geometry.Length) + MathF.PI;

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
                float sweep = road.Geometry.Sweep;
                var plan = Plan(x0, z0, x1, z1, sweep);
                if (plan == null) return null; // refused: too short, too steep, too tight, or an end in water
                moved.Add(new MovedRoad(road, x0, z0, x1, z1, sweep, RoadGeometry.FromChord(x0, z0, x1, z1, sweep), plan));
            }
        }
        finally
        {
            foreach (var road in roads) road.Retired = false;
        }
        // Two roads leaving the node the same way would lie on top of each other, and rebuilt roads must not cross.
        for (int i = 0; i < moved.Count; i++)
            for (int j = i + 1; j < moved.Count; j++)
            {
                float between = MathF.Abs(DepartureAngle(moved[i], moved[i].Old.From == node) - DepartureAngle(moved[j], moved[j].Old.From == node));
                between %= MathF.Tau;
                if (MathF.Min(between, MathF.Tau - between) < MinAngleBetweenRoads) return null;
                if (RebuiltRoadsCross(moved[i], moved[j], x, z)) return null;
            }
        return moved;
    }

    /// <summary>Whether two roads rebuilt from the same moved node meet anywhere but at it.</summary>
    static bool RebuiltRoadsCross(MovedRoad a, MovedRoad b, float nodeX, float nodeZ)
    {
        var p = Sample(a.Geometry);
        var q = Sample(b.Geometry);
        for (int i = 0; i + 1 < p.Length; i++)
            for (int j = 0; j + 1 < q.Length; j++)
            {
                if (!SegmentsIntersect(p[i], p[i + 1], q[j], q[j + 1], 0f, out float t, out _)) continue;
                float px = p[i].X + (p[i + 1].X - p[i].X) * t, pz = p[i].Z + (p[i + 1].Z - p[i].Z) * t;
                if ((px - nodeX) * (px - nodeX) + (pz - nodeZ) * (pz - nodeZ) > 1f) return true;
            }
        return false;
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
        var replacement = _roads[AddRoad(old.From, old.To, type, speedLimit, old.Geometry.Sweep, old.Structure).Value];
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
    public int SplitRoad(RoadId id, float distance) => SplitRoad(id, distance, out _, out _);

    int SplitRoad(RoadId id, float distance, out RoadId firstId, out RoadId secondId)
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
        firstId = AddRoad(road.From, middle, road.Type, road.SpeedLimit, sweepFirst, road.Structure);
        secondId = AddRoad(middle, road.To, road.Type, road.SpeedLimit, road.Geometry.Sweep - sweepFirst, road.Structure);
        var first = _roads[firstId.Value];
        var second = _roads[secondId.Value];
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

    public RoadId AddRoad(int from, int to, RoadType type, float speedLimit, float sweep = 0f, RoadStructure structure = RoadStructure.None)
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

        _roads.Add(new Road(id, from, to, type, speedLimit, geometry, forward, backward, structure));
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
