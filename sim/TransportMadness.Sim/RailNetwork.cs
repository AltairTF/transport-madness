namespace TransportMadness.Sim;

public readonly record struct TrackId(int Value)
{
    public static readonly TrackId None = new(-1);
    public bool IsNone => Value < 0;
}

public readonly record struct SignalId(int Value);

/// <summary>What a signal asks of a train before it may pass.</summary>
public enum SignalKind : byte
{
    /// <summary>The whole block beyond (every track reachable up to the next signal or station) must be free, whichever way the train goes.</summary>
    Block,
    /// <summary>Only the path the train will take, up to the next signal or station, must be free; trains whose paths do not meet pass together.</summary>
    Path,
}

/// <summary>One piece of single track between two nodes. Trains run on it both ways.</summary>
public sealed class Track
{
    internal Track(TrackId id, int from, int to, RoadGeometry geometry, float speedLimit, float grade, RoadStructure structure)
    {
        Id = id;
        From = from;
        To = to;
        Geometry = geometry;
        SpeedLimit = speedLimit;
        Grade = grade;
        Structure = structure;
    }

    public TrackId Id { get; }
    public int From { get; }
    public int To { get; }
    /// <summary>The centre line from <see cref="From"/> to <see cref="To"/>; positions on the track are distances along it.</summary>
    public RoadGeometry Geometry { get; }
    public float Length => Geometry.Length;
    /// <summary>The posted limit reduced for the curve and the grade.</summary>
    public float SpeedLimit { get; }
    /// <summary>Rise over run from <see cref="From"/> to <see cref="To"/>.</summary>
    public float Grade { get; }
    public RoadStructure Structure { get; }
    /// <summary>Replaced by shorter pieces when a node was made inside it, or torn down.</summary>
    public bool Retired { get; internal set; }
    /// <summary>When it was replaced by two pieces: the one from <see cref="From"/> to the new node, the one from there to <see cref="To"/>, and where it was cut. None when it was torn down.</summary>
    public TrackId SplitFirst { get; internal set; } = TrackId.None;
    public TrackId SplitSecond { get; internal set; } = TrackId.None;
    public float SplitDistance { get; internal set; }

    public int Other(int node) => node == From ? To : From;
}

/// <summary>A signal stands at a node, facing the trains that arrive along one track.</summary>
public sealed class RailSignal
{
    internal RailSignal(SignalId id, TrackId track, int node, SignalKind kind)
    {
        Id = id;
        Track = track;
        Node = node;
        Kind = kind;
    }

    public SignalId Id { get; }
    /// <summary>The track it faces: trains coming along it toward <see cref="Node"/> must stop here unless the track beyond is theirs.</summary>
    public TrackId Track { get; internal set; }
    public int Node { get; internal set; }
    public SignalKind Kind { get; internal set; }
    public bool Active { get; internal set; } = true;
}

/// <summary>What building a track would take.</summary>
public readonly record struct TrackCost(float Length, float CutVolume, float FillVolume, float Money, float BridgeLength = 0f, float TunnelLength = 0f);

/// <summary>A point on a track's centre line.</summary>
public readonly record struct TrackPoint(TrackId Track, float Along, float X, float Y, float Z);

/// <summary>
/// The rail graph: nodes, single tracks (straight or circular arcs), and signals. Trains have no lanes: they run on the
/// centre line both ways, and a node connects two of its tracks when the turn between them is gentle enough
/// (<see cref="RailTuning.MaxTurnAngle"/>). Building follows the road rules (cut and fill, bridges and tunnels, splitting
/// the tracks it ends on or crosses, grade and curve limits) with rail numbers from the tuning.
/// </summary>
public sealed class RailNetwork
{
    /// <summary>A node is on a track's end when it is this close.</summary>
    const float OnNode = 0.5f;
    const float SampleSpacing = 4f;
    const float StructureSampleSpacing = 4f;

    public RailNetwork(Terrain? terrain, Tuning tuning)
    {
        Terrain = terrain;
        Tuning = tuning;
        Rail = tuning.Rail;
    }

    public Terrain? Terrain { get; }
    public Tuning Tuning { get; }
    public RailTuning Rail { get; }

    readonly List<float> _nodeHeights = new();
    readonly List<(float X, float Z)> _nodes = new();
    readonly List<List<TrackId>> _nodeTracks = new();
    readonly List<Track> _tracks = new();
    readonly List<RailSignal> _signals = new();

    /// <summary>Increases whenever tracks or signals are added or removed.</summary>
    public int Version { get; private set; }

    public int NodeCount => _nodes.Count;
    public int TrackCount => _tracks.Count;
    public int SignalCount => _signals.Count;
    public int LiveTrackCount => _tracks.Count(t => !t.Retired);

    public int AddNode(float x, float z) => AddNode(x, z, Terrain?.HeightAt(x, z) ?? 0f);

    public int AddNode(float x, float z, float height)
    {
        _nodes.Add((x, z));
        _nodeHeights.Add(height);
        _nodeTracks.Add(new List<TrackId>());
        return _nodes.Count - 1;
    }

    public (float X, float Z) GetNode(int node) => _nodes[node];
    public float GetNodeHeight(int node) => _nodeHeights[node];
    public Track GetTrack(TrackId id) => _tracks[id.Value];
    public RailSignal GetSignal(SignalId id) => _signals[id.Value];
    public IReadOnlyList<RailSignal> Signals => _signals;
    public IReadOnlyList<Track> Tracks => _tracks;

    /// <summary>The live tracks that meet at a node.</summary>
    public IReadOnlyList<TrackId> TracksAt(int node) => _nodeTracks[node];

    public TrackId? FindTrack(int a, int b)
    {
        foreach (var id in _nodeTracks[a])
        {
            var t = _tracks[id.Value];
            if ((t.From == a && t.To == b) || (t.From == b && t.To == a)) return id;
        }
        return null;
    }

    public float TrackHeightAt(TrackId id, float along)
    {
        var t = _tracks[id.Value];
        float y0 = _nodeHeights[t.From], y1 = _nodeHeights[t.To];
        return y0 + (y1 - y0) * Math.Clamp(along / t.Length, 0f, 1f);
    }

    /// <summary>Ground-plane position and heading of the centre line at a distance along a track, travelling from <see cref="Track.From"/> to <see cref="Track.To"/>.</summary>
    public (float X, float Z, float Heading) PointAt(TrackId id, float along)
    {
        var g = _tracks[id.Value].Geometry;
        along = Math.Clamp(along, 0f, g.Length);
        var (x, z) = g.PointAt(along);
        return (x, z, g.HeadingAt(along));
    }

    // ---- Connections ---------------------------------------------------------------------------------------------

    /// <summary>The direction of travel (radians) of a train that arrives at <paramref name="node"/> along a track.</summary>
    public float HeadingInto(TrackId track, int node)
    {
        var t = _tracks[track.Value];
        return node == t.To ? t.Geometry.HeadingAt(t.Length) : t.Geometry.HeadingAt(0f) + MathF.PI;
    }

    /// <summary>The direction of travel of a train that leaves <paramref name="node"/> along a track.</summary>
    public float HeadingOutOf(TrackId track, int node)
    {
        var t = _tracks[track.Value];
        return node == t.From ? t.Geometry.HeadingAt(0f) : t.Geometry.HeadingAt(t.Length) + MathF.PI;
    }

    /// <summary>How much a train has to turn to get from one track onto another at a node, in radians (0 straight on, pi turning back).</summary>
    public float TurnAngle(TrackId from, int node, TrackId to)
    {
        float d = HeadingOutOf(to, node) - HeadingInto(from, node);
        d -= MathF.Tau * MathF.Floor((d + MathF.PI) / MathF.Tau);
        return MathF.Abs(d);
    }

    /// <summary>Whether a train can pass from one track onto another at a node.</summary>
    public bool CanPass(TrackId from, int node, TrackId to) =>
        from != to && TurnAngle(from, node, to) <= Rail.MaxTurnAngle;

    /// <summary>The tracks a train arriving along <paramref name="from"/> can leave <paramref name="node"/> by.</summary>
    public IEnumerable<TrackId> Exits(TrackId from, int node)
    {
        foreach (var id in _nodeTracks[node])
            if (CanPass(from, node, id)) yield return id;
    }

    /// <summary>The speed a train may have in a junction that bends it by this angle: unlimited (infinity) when straight.</summary>
    public float TurnSpeed(float angle)
    {
        if (angle < 0.05f) return float.PositiveInfinity;
        float share = Math.Clamp(angle / Rail.MaxTurnAngle, 0f, 1f);
        return Rail.TurnoutSpeed + (Rail.MinTurnoutSpeed - Rail.TurnoutSpeed) * share;
    }

    // ---- Signals --------------------------------------------------------------------------------------------------

    public RailSignal? SignalFor(TrackId track, int node)
    {
        foreach (var s in _signals)
            if (s.Active && s.Track == track && s.Node == node) return s;
        return null;
    }

    /// <summary>Whether any signal stands at the node (it then splits the network into blocks).</summary>
    public bool HasSignals(int node)
    {
        foreach (var s in _signals)
            if (s.Active && s.Node == node) return true;
        return false;
    }

    /// <summary>Adds a signal, or changes the kind of the one that is there. Returns null when the track does not end at the node.</summary>
    public RailSignal? SetSignal(TrackId track, int node, SignalKind kind)
    {
        var t = _tracks[track.Value];
        if (t.Retired || (t.From != node && t.To != node)) return null;
        var existing = SignalFor(track, node);
        if (existing != null)
        {
            if (existing.Kind == kind) return existing;
            existing.Kind = kind;
            Version++;
            return existing;
        }
        var signal = new RailSignal(new SignalId(_signals.Count), track, node, kind);
        _signals.Add(signal);
        Version++;
        return signal;
    }

    public bool RemoveSignal(SignalId id)
    {
        var s = _signals[id.Value];
        if (!s.Active) return false;
        s.Active = false;
        Version++;
        return true;
    }

    // ---- Queries --------------------------------------------------------------------------------------------------

    public int FindNodeNear(float x, float z, float radius, bool withTracksOnly = false)
    {
        int best = -1;
        float bestDistance = radius * radius;
        for (int n = 0; n < _nodes.Count; n++)
        {
            if (withTracksOnly && _nodeTracks[n].Count == 0) continue;
            float d = (_nodes[n].X - x) * (_nodes[n].X - x) + (_nodes[n].Z - z) * (_nodes[n].Z - z);
            if (d <= bestDistance) { best = n; bestDistance = d; }
        }
        return best;
    }

    /// <summary>The track whose centre line passes nearest to a point, within <paramref name="radius"/>.</summary>
    public TrackId? TrackAt(float x, float z, float radius)
    {
        float best = radius * radius;
        TrackId? found = null;
        foreach (var t in _tracks)
        {
            if (t.Retired) continue;
            var g = t.Geometry;
            var (px, pz) = g.PointAt(g.NearestAlong(x, z));
            float d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d <= best) { best = d; found = t.Id; }
        }
        return found;
    }

    /// <summary>
    /// The point on a track nearest to (x, z) within <paramref name="radius"/>, its distance along rounded to a multiple of
    /// <paramref name="step"/>; tracks where that is within <see cref="RailTuning.MinTrackLength"/> of either end are skipped.
    /// </summary>
    public TrackPoint? NearestOnTrack(float x, float z, float radius, float step)
    {
        float bestDistance = radius * radius;
        TrackPoint? best = null;
        foreach (var t in _tracks)
        {
            if (t.Retired) continue;
            var g = t.Geometry;
            float along = MathF.Round(g.NearestAlong(x, z) / step) * step;
            if (along < Rail.MinTrackLength || along > g.Length - Rail.MinTrackLength) continue;
            var (px, pz) = g.PointAt(along);
            float d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d <= bestDistance)
            {
                best = new TrackPoint(t.Id, along, px, TrackHeightAt(t.Id, along), pz);
                bestDistance = d;
            }
        }
        return best;
    }

    // ---- Planning and building ------------------------------------------------------------------------------------

    /// <summary>One place along a planned track where it meets the network: an existing node, or a point inside a track to split.</summary>
    readonly record struct Meeting(float Along, int Node, TrackId Track, float TrackDistance, float X, float Z, float Y, RoadStructure After = RoadStructure.None, bool Tight = false);

    /// <summary>The finished pieces a track would be built in: where each meets the network.</summary>
    public readonly record struct PlannedPoint(float Along, float X, float Z, bool Junction);

    /// <summary>
    /// Builds a track (straight, or bending by <paramref name="sweep"/> radians), joining the network wherever it touches it:
    /// at existing nodes, and by splitting any track it ends on or crosses. Returns the new pieces, none when it cannot be built
    /// (too short, too steep, too tight a curve, a duplicate, a junction too close to another, or an end in water).
    /// </summary>
    public IReadOnlyList<TrackId> BuildTrack(float x0, float z0, float x1, float z1, float sweep = 0f)
    {
        var plan = Plan(x0, z0, x1, z1, sweep);
        if (plan == null) return Array.Empty<TrackId>();
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);

        var nodes = new List<int>(plan.Count);
        var splits = new Dictionary<int, (TrackId First, TrackId Second, float At)>();
        foreach (var m in plan)
        {
            if (m.Node >= 0) nodes.Add(m.Node);
            else if (m.Track.Value >= 0)
            {
                var track = m.Track;
                float distance = m.TrackDistance;
                while (splits.TryGetValue(track.Value, out var split))
                {
                    if (distance < split.At) track = split.First;
                    else { distance -= split.At; track = split.Second; }
                }
                nodes.Add(SplitTrack(track, distance, out var first, out var second));
                splits[track.Value] = (first, second, _tracks[first.Value].Length);
            }
            else nodes.Add(AddNode(m.X, m.Z, m.Y));
        }
        var built = new List<TrackId>();
        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            float pieceSweep = sweep * (plan[i + 1].Along - plan[i].Along) / geometry.Length;
            built.Add(AddTrack(nodes[i], nodes[i + 1], pieceSweep, plan[i].After));
            if (plan[i].After == RoadStructure.None) Terrain?.CarvePath(Profile(geometry, plan[i], plan[i + 1]), Rail.HalfWidth, Rail.Shoulder);
        }
        return built;
    }

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

    float GroundHeight(float x, float z) => Terrain?.HeightAt(x, z) ?? 0f;

    /// <summary>What a track would cost, null if it cannot be built.</summary>
    public TrackCost? EstimateCost(float x0, float z0, float x1, float z1, float sweep = 0f)
    {
        var plan = Plan(x0, z0, x1, z1, sweep);
        if (plan == null) return null;
        return Price(plan, RoadGeometry.FromChord(x0, z0, x1, z1, sweep));
    }

    public bool CanBuildTrack(float x0, float z0, float x1, float z1, float sweep = 0f) => Plan(x0, z0, x1, z1, sweep) != null;

    /// <summary>Why a track cannot be built, in words for the player (the numbers that broke the rule included); null when it can.</summary>
    public string? WhyNotTrack(float x0, float z0, float x1, float z1, float sweep = 0f)
    {
        if (Plan(x0, z0, x1, z1, sweep) != null) return null;
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        if (geometry.Length < Rail.MinTrackLength) return $"too short ({geometry.Length:0} m, at least {Rail.MinTrackLength:0} m)";
        if (!geometry.IsStraight && MathF.Abs(sweep) > Rail.MaxSweep) return "the curve turns too far";
        if (!geometry.IsStraight && geometry.Radius < Rail.MinRadius) return $"curve too tight (radius {geometry.Radius:0} m, at least {Rail.MinRadius:0} m)";
        var meetings = PlanJunctions(x0, z0, x1, z1, sweep);
        if (meetings == null) return "a track already joins those two places";
        if (Terrain != null)
        {
            if (meetings[0].Node < 0 && meetings[0].Track.Value < 0 && Terrain.IsWater(meetings[0].X, meetings[0].Z)) return "the start is in water";
            var last = meetings[^1];
            if (last.Node < 0 && last.Track.Value < 0 && Terrain.IsWater(last.X, last.Z)) return "the end is in water";
        }
        if (meetings.Exists(m => m.Tight)) return $"it would join another track less than {Rail.MinTrackLength:0} m from a junction";
        for (int i = 0; i + 1 < meetings.Count; i++)
        {
            float run = meetings[i + 1].Along - meetings[i].Along;
            if (run < Rail.MinTrackLength) return $"a piece between two junctions would be only {run:0} m long (at least {Rail.MinTrackLength:0} m)";
            float grade = MathF.Abs(meetings[i + 1].Y - meetings[i].Y) / run;
            if (grade > Rail.MaxGrade) return $"too steep ({grade * 100f:0.0}% over {run:0} m, trains manage {Rail.MaxGrade * 100f:0.#}%): the ends are {MathF.Abs(meetings[i + 1].Y - meetings[i].Y):0.#} m apart in height, lay it in shorter hops along the ground or wind it up the slope";
        }
        return "a track already joins those two places";
    }

    /// <summary>Where the planned track would meet the network (its ends and every junction), for checks before building; null when it cannot be built.</summary>
    public IReadOnlyList<PlannedPoint>? PlannedPoints(float x0, float z0, float x1, float z1, float sweep = 0f)
    {
        var plan = Plan(x0, z0, x1, z1, sweep);
        if (plan == null) return null;
        var list = new List<PlannedPoint>();
        foreach (var m in plan)
            list.Add(new PlannedPoint(m.Along, m.X, m.Z, m.Node >= 0 || m.Track.Value >= 0));
        return list;
    }

    TrackCost Price(List<Meeting> plan, RoadGeometry geometry)
    {
        float width = Rail.HalfWidth * 2f;
        float cut = 0f, fill = 0f, length = 0f, bridge = 0f, tunnel = 0f, money = 0f;
        for (int i = 0; i + 1 < plan.Count; i++)
        {
            var a = plan[i];
            var b = plan[i + 1];
            float run = b.Along - a.Along;
            length += run;
            if (a.After != RoadStructure.None)
            {
                if (a.After == RoadStructure.Bridge) { bridge += run; money += run * Rail.BridgeCostPerMetre; }
                else { tunnel += run; money += run * Rail.TunnelCostPerMetre; }
                continue;
            }
            money += run * Rail.TrackCostPerMetre;
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
                float section = h * width + Rail.EarthworkSlope * h * h;
                if (height > 0f) cutHere += section * ds; else fillHere += section * ds;
            }
            cut += cutHere; fill += fillHere;
            money += cutHere * Rail.CutCostPerCubicMetre + fillHere * Rail.FillCostPerCubicMetre;
        }
        return new TrackCost(length, cut, fill, money, bridge, tunnel);
    }

    List<Meeting>? Plan(float x0, float z0, float x1, float z1, float sweep)
    {
        var meetings = PlanJunctions(x0, z0, x1, z1, sweep);
        if (meetings == null) return null;
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        if (Terrain != null)
        {
            if (meetings[0].Node < 0 && meetings[0].Track.Value < 0 && Terrain.IsWater(meetings[0].X, meetings[0].Z)) return null;
            var last = meetings[^1];
            if (last.Node < 0 && last.Track.Value < 0 && Terrain.IsWater(last.X, last.Z)) return null;
        }
        if (!PiecesAllowed(meetings)) return null;
        return Terrain == null ? meetings : InsertStructures(meetings, geometry);
    }

    bool PiecesAllowed(List<Meeting> meetings)
    {
        foreach (var m in meetings)
            if (m.Tight) return false;
        for (int i = 0; i + 1 < meetings.Count; i++)
        {
            float run = meetings[i + 1].Along - meetings[i].Along;
            if (run < Rail.MinTrackLength) return false;
            int na = meetings[i].Node, nb = meetings[i + 1].Node;
            if (na >= 0 && nb >= 0 && FindTrack(na, nb) != null) return false;
            if (meetings[i].Node < 0 && meetings[i + 1].Node < 0 && meetings[i].Track.Value >= 0 && meetings[i].Track == meetings[i + 1].Track
                && MathF.Abs(meetings[i + 1].TrackDistance - meetings[i].TrackDistance) < Rail.MinTrackLength) return false;
            if (MathF.Abs(meetings[i + 1].Y - meetings[i].Y) > Rail.MaxGrade * run) return false;
        }
        return true;
    }

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
            kind = Widen(kind, (int)MathF.Ceiling(Rail.StructureMargin / cell));
            kind = Cleanup(kind, cell);
            for (int k = 0; k < steps; k++)
            {
                if (k > 0 && kind[k] == kind[k - 1]) continue;
                if (k == 0) { result.Add(a with { After = kind[0] }); continue; }
                float along = a.Along + cell * k;
                var (x, z) = geometry.PointAt(along);
                result.Add(new Meeting(along, -1, TrackId.None, 0f, x, z, a.Y + (b.Y - a.Y) * (k / (float)steps), kind[k]));
            }
        }
        result.Add(meetings[^1]);
        return result;
    }

    RoadStructure StructureNeeded(float x, float z, float height)
    {
        if (Terrain!.IsWater(x, z)) return RoadStructure.Bridge;
        float ground = Terrain.HeightAt(x, z);
        if (height - ground > Rail.MaxFillDepth) return RoadStructure.Bridge;
        if (ground - height > Rail.MaxCutDepth) return RoadStructure.Tunnel;
        return RoadStructure.None;
    }

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

    RoadStructure[] Cleanup(RoadStructure[] kind, float cell)
    {
        int minSamples = (int)MathF.Ceiling(Rail.MinTrackLength / cell);
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

    List<Meeting>? PlanJunctions(float x0, float z0, float x1, float z1, float sweep)
    {
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        float length = geometry.Length;
        if (length < Rail.MinTrackLength) return null;
        if (!geometry.IsStraight && (MathF.Abs(sweep) > Rail.MaxSweep || geometry.Radius < Rail.MinRadius)) return null;

        var meetings = new List<Meeting>();
        int a = FindNodeNear(x0, z0, OnNode), b = FindNodeNear(x1, z1, OnNode);
        if (a >= 0 && b >= 0 && FindTrack(a, b) != null) return null;

        (float X, float Z)[]? samples = null;
        for (int r = 0; r < _tracks.Count; r++)
        {
            var track = _tracks[r];
            if (track.Retired) continue;
            var other = track.Geometry;
            float trackLength = other.Length;

            if (geometry.IsStraight && other.IsStraight)
            {
                var (rx0, rz0) = _nodes[track.From];
                var (rx1, rz1) = _nodes[track.To];
                float dx = x1 - x0, dz = z1 - z0;
                float ex = rx1 - rx0, ez = rz1 - rz0;
                float cross = dx * ez - dz * ex;
                if (MathF.Abs(cross) < 1e-3f * length * trackLength) continue;
                float s = ((rx0 - x0) * ez - (rz0 - z0) * ex) / cross;
                float u = ((rx0 - x0) * dz - (rz0 - z0) * dx) / cross;
                float slack = 0.5f / length, trackSlack = 0.5f / trackLength;
                if (s < -slack || s > 1f + slack || u < -trackSlack || u > 1f + trackSlack) continue;
                Touch(meetings, track, s * length, u * trackLength, x0 + dx * s, z0 + dz * s);
                continue;
            }

            samples ??= Sample(geometry);
            var otherSamples = Sample(other);
            if (!Overlap(samples, otherSamples)) continue;
            float stepNew = length / (samples.Length - 1), stepOld = trackLength / (otherSamples.Length - 1);
            float lastHit = float.NegativeInfinity;
            for (int i = 0; i + 1 < samples.Length; i++)
                for (int j = 0; j + 1 < otherSamples.Length; j++)
                {
                    if (!SegmentsIntersect(samples[i], samples[i + 1], otherSamples[j], otherSamples[j + 1], 1e-3f, out float t, out float u)) continue;
                    float along = (i + t) * stepNew;
                    if (MathF.Abs(along - lastHit) < 1f) continue;
                    lastHit = along;
                    float px = samples[i].X + (samples[i + 1].X - samples[i].X) * t, pz = samples[i].Z + (samples[i + 1].Z - samples[i].Z) * t;
                    Touch(meetings, track, along, (j + u) * stepOld, px, pz);
                }
        }

        if (!meetings.Exists(m => m.Along <= OnNode)) meetings.Add(new Meeting(0f, a, TrackId.None, 0f, x0, z0, a >= 0 ? _nodeHeights[a] : GroundHeight(x0, z0)));
        if (!meetings.Exists(m => m.Along >= length - OnNode)) meetings.Add(new Meeting(length, b, TrackId.None, 0f, x1, z1, b >= 0 ? _nodeHeights[b] : GroundHeight(x1, z1)));
        meetings.Sort((p, q) => p.Along.CompareTo(q.Along));
        return meetings;
    }

    void Touch(List<Meeting> meetings, Track track, float along, float trackAlong, float x, float z)
    {
        float trackLength = track.Length;
        if (trackAlong <= OnNode || trackAlong >= trackLength - OnNode)
        {
            int node = trackAlong <= OnNode ? track.From : track.To;
            if (!meetings.Exists(m => m.Node == node)) meetings.Add(new Meeting(along, node, TrackId.None, 0f, _nodes[node].X, _nodes[node].Z, _nodeHeights[node]));
            return;
        }
        bool tight = trackAlong < Rail.MinTrackLength || trackAlong > trackLength - Rail.MinTrackLength;
        meetings.Add(new Meeting(along, -1, track.Id, trackAlong, x, z, TrackHeightAt(track.Id, trackAlong), Tight: tight));
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

    /// <summary>Intersection of segments a0-a1 and b0-b1; the parameters are fractions along each.</summary>
    internal static bool SegmentsIntersect((float X, float Z) a0, (float X, float Z) a1, (float X, float Z) b0, (float X, float Z) b1,
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

    // ---- Removing and splitting -----------------------------------------------------------------------------------

    public float DemolitionCost(TrackId id) => _tracks[id.Value].Length * Rail.DemolitionCostPerMetre;

    /// <summary>Tears a track down; the signals facing along it go with it.</summary>
    public bool RemoveTrack(TrackId id)
    {
        var t = _tracks[id.Value];
        if (t.Retired) return false;
        Retire(t);
        foreach (var s in _signals)
            if (s.Active && s.Track == id) s.Active = false;
        Version++;
        return true;
    }

    void Retire(Track t)
    {
        t.Retired = true;
        _nodeTracks[t.From].Remove(t.Id);
        _nodeTracks[t.To].Remove(t.Id);
    }

    /// <summary>Replaces a track by two meeting at a new node <paramref name="distance"/> metres from its start. Returns the new node.</summary>
    public int SplitTrack(TrackId id, float distance) => SplitTrack(id, distance, out _, out _);

    public int SplitTrack(TrackId id, float distance, out TrackId firstId, out TrackId secondId)
    {
        var track = _tracks[id.Value];
        if (track.Retired) throw new InvalidOperationException("Track is already replaced.");
        float length = track.Length;
        if (distance < Rail.MinTrackLength || distance > length - Rail.MinTrackLength) throw new ArgumentOutOfRangeException(nameof(distance));

        var (mx, mz) = track.Geometry.PointAt(distance);
        int middle = AddNode(mx, mz, TrackHeightAt(id, distance));
        Retire(track);
        float sweepFirst = track.Geometry.Sweep * (distance / length);
        firstId = AddTrack(track.From, middle, sweepFirst, track.Structure);
        secondId = AddTrack(middle, track.To, track.Geometry.Sweep - sweepFirst, track.Structure);
        track.SplitFirst = firstId;
        track.SplitSecond = secondId;
        track.SplitDistance = distance;
        // Signals facing the old track move to the piece that still ends at their node.
        foreach (var s in _signals)
        {
            if (!s.Active || s.Track != id) continue;
            s.Track = s.Node == track.From ? firstId : secondId;
        }
        Version++;
        return middle;
    }

    Track AddTrackCore(int from, int to, float sweep, RoadStructure structure)
    {
        if (from == to) throw new ArgumentException("A track needs two distinct nodes.");
        var (x0, z0) = _nodes[from];
        var (x1, z1) = _nodes[to];
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        float length = geometry.Length;
        if (length < Rail.MinTrackLength) throw new ArgumentException($"Track is shorter than {Rail.MinTrackLength} m.");
        float grade = (_nodeHeights[to] - _nodeHeights[from]) / length;
        float curveLimit = geometry.IsStraight ? float.PositiveInfinity : MathF.Sqrt(Rail.CurveLateralAcceleration * geometry.Radius);
        float limit = MathF.Min(Rail.SpeedLimit * GradeFactor(grade), curveLimit);
        var track = new Track(new TrackId(_tracks.Count), from, to, geometry, limit, grade, structure);
        _tracks.Add(track);
        _nodeTracks[from].Add(track.Id);
        _nodeTracks[to].Add(track.Id);
        return track;
    }

    TrackId AddTrack(int from, int to, float sweep, RoadStructure structure)
    {
        var track = AddTrackCore(from, to, sweep, structure);
        Version++;
        return track.Id;
    }

    /// <summary>Adds a track between two existing nodes without planning (tests and scenarios).</summary>
    public TrackId AddTrack(int from, int to, float sweep = 0f) => AddTrack(from, to, sweep, RoadStructure.None);

    float GradeFactor(float grade)
    {
        float steep = (MathF.Abs(grade) - Rail.FreeGrade) / (Rail.MaxGrade - Rail.FreeGrade);
        return 1f - 0.5f * Math.Clamp(steep, 0f, 1f);
    }
}
