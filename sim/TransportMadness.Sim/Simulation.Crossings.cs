namespace TransportMadness.Sim;

public enum CrossingState : byte
{
    /// <summary>Road traffic passes.</summary>
    Open,
    /// <summary>A train is coming: lights flash and the barriers come down. Vehicles that can stop do.</summary>
    Lowering,
    /// <summary>Barriers down: trains may cross, road vehicles wait.</summary>
    Closed,
}

/// <summary>A place where a track and a road cross at the same height. Trains and road vehicles share it one at a time.</summary>
public sealed class LevelCrossing
{
    internal LevelCrossing(int id, float x, float z)
    {
        Id = id;
        X = x;
        Z = z;
    }

    public int Id { get; }
    public float X { get; }
    public float Z { get; }
    /// <summary>False once the road or the track is gone, or when they no longer meet at grade.</summary>
    public bool Active { get; internal set; } = true;
    public TrackId Track { get; internal set; }
    public float TrackAlong { get; internal set; }
    /// <summary>Half the length of the stretch of track the road covers.</summary>
    public float TrackHalf { get; internal set; }
    public RoadId Road { get; internal set; }
    public float RoadAlong { get; internal set; }
    /// <summary>Half the length of the stretch of road the rails cover.</summary>
    public float RoadHalf { get; internal set; }
    /// <summary>Angle between the track and the road, radians, in (0, pi/2].</summary>
    public float Angle { get; internal set; }
    public CrossingState State { get; internal set; }
    /// <summary>A wreck or a standing vehicle is in the crossing: trains stop before it.</summary>
    public bool Fouled { get; internal set; }
    /// <summary>Vehicles trains have hit here.</summary>
    public int Collisions { get; internal set; }
    internal float Timer;
}

/// <summary>Level crossings: found where tracks and roads meet, closed by approaching trains, observed by road vehicles, and where they collide.</summary>
public sealed partial class Simulation
{
    readonly List<LevelCrossing> _crossings = new();
    int _crossRoadsSeen, _crossTracksSeen;
    long _crossingsSyncedFor = -1;
    // Zones on road lanes, grouped by lane: _laneCrossFirst[l] .. _laneCrossFirst[l + 1].
    int[] _laneCrossFirst = Array.Empty<int>();
    int[] _zoneCrossing = Array.Empty<int>();
    float[] _zoneStart = Array.Empty<float>();
    float[] _zoneEnd = Array.Empty<float>();
    readonly Dictionary<int, List<int>> _trackCrossings = new();

    public IReadOnlyList<LevelCrossing> Crossings => _crossings;

    CrossingTuning CrossingRules => _railTuning.Crossings;

    // ---- Finding crossings --------------------------------------------------------------------------------------------

    static (float X0, float Z0, float X1, float Z1) BoundsOf(RoadGeometry g)
    {
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        int n = Math.Max(1, (int)MathF.Ceiling(g.Length / 20f));
        for (int i = 0; i <= n; i++)
        {
            var (x, z) = g.PointAt(g.Length * i / n);
            x0 = MathF.Min(x0, x); x1 = MathF.Max(x1, x);
            z0 = MathF.Min(z0, z); z1 = MathF.Max(z1, z);
        }
        const float pad = 6f;
        return (x0 - pad, z0 - pad, x1 + pad, z1 + pad);
    }

    static bool Overlaps((float X0, float Z0, float X1, float Z1) a, (float X0, float Z0, float X1, float Z1) b) =>
        a.X0 <= b.X1 && b.X0 <= a.X1 && a.Z0 <= b.Z1 && b.Z0 <= a.Z1;

    /// <summary>Where two centre lines cross: distance along each, and the point.</summary>
    static List<(float A, float B, float X, float Z)> Intersections(RoadGeometry a, RoadGeometry b)
    {
        var found = new List<(float, float, float, float)>();
        if (!Overlaps(BoundsOf(a), BoundsOf(b))) return found;
        if (a.IsStraight && b.IsStraight)
        {
            float dx = MathF.Cos(a.Heading0), dz = MathF.Sin(a.Heading0), ex = MathF.Cos(b.Heading0), ez = MathF.Sin(b.Heading0);
            float cross = dx * ez - dz * ex;
            if (MathF.Abs(cross) < 1e-4f) return found;
            float qx = b.X - a.X, qz = b.Z - a.Z;
            float s = (qx * ez - qz * ex) / cross, u = (qx * dz - qz * dx) / cross;
            if (s < 0f || s > a.Length || u < 0f || u > b.Length) return found;
            found.Add((s, u, a.X + dx * s, a.Z + dz * s));
            return found;
        }
        const float spacing = 3f;
        int na = Math.Max(1, (int)MathF.Ceiling(a.Length / spacing)), nb = Math.Max(1, (int)MathF.Ceiling(b.Length / spacing));
        var pa = new (float X, float Z)[na + 1];
        var pb = new (float X, float Z)[nb + 1];
        for (int i = 0; i <= na; i++) pa[i] = a.PointAt(a.Length * i / na);
        for (int j = 0; j <= nb; j++) pb[j] = b.PointAt(b.Length * j / nb);
        float stepA = a.Length / na, stepB = b.Length / nb, last = float.NegativeInfinity;
        for (int i = 0; i < na; i++)
            for (int j = 0; j < nb; j++)
            {
                if (!RailNetwork.SegmentsIntersect(pa[i], pa[i + 1], pb[j], pb[j + 1], 1e-3f, out float t, out float u)) continue;
                float along = (i + t) * stepA;
                if (MathF.Abs(along - last) < 1f) continue;
                last = along;
                found.Add((along, (j + u) * stepB, pa[i].X + (pa[i + 1].X - pa[i].X) * t, pa[i].Z + (pa[i + 1].Z - pa[i].Z) * t));
            }
        return found;
    }

    static float AngleBetween(float h0, float h1)
    {
        float d = h0 - h1;
        d -= MathF.Tau * MathF.Floor((d + MathF.PI) / MathF.Tau);
        d = MathF.Abs(d);
        return d > MathF.PI / 2f ? MathF.PI - d : d;
    }

    /// <summary>
    /// Whether a track about to be built can cross the roads it meets: each crossing at grade is far enough from the ends of the road
    /// and of the track, from other junctions and at a wide enough angle. <paramref name="count"/> is how many level crossings it makes.
    /// </summary>
    bool TrackCrossingsAllowed(RoadGeometry track, IReadOnlyList<RailNetwork.PlannedPoint> plan, out int count)
    {
        count = 0;
        var rules = CrossingRules;
        var trackBox = BoundsOf(track);
        for (int r = 0; r < _net.RoadCount; r++)
        {
            var road = _net.GetRoad(new RoadId(r));
            if (road.Retired) continue;
            foreach (var (at, along, x, z) in Intersections(track, road.Geometry))
            {
                float roadY = _net.RoadHeightAt(road.Id, along);
                float trackY = PlannedHeight(plan, at, x, z);
                if (MathF.Abs(roadY - trackY) >= rules.Clearance) continue; // one passes over the other
                if (AngleBetween(track.HeadingAt(at), road.Geometry.HeadingAt(along)) < rules.MinAngle) return false;
                if (along < rules.MinFromNode || along > road.Length - rules.MinFromNode) return false;
                foreach (var p in plan)
                    if (MathF.Abs(p.Along - at) < rules.MinFromNode) return false;
                foreach (var other in _crossings)
                    if (other.Active && (other.X - x) * (other.X - x) + (other.Z - z) * (other.Z - z) < rules.MinFromNode * rules.MinFromNode) return false;
                count++;
            }
        }
        return true;
    }

    float PlannedHeight(IReadOnlyList<RailNetwork.PlannedPoint> plan, float along, float x, float z)
    {
        // The plan only has the horizontal places; the rails follow the ground between them, which is also how the road was laid.
        return _rail.Terrain?.HeightAt(x, z) ?? 0f;
    }

    /// <summary>The same check for a road about to be built across tracks.</summary>
    bool RoadCrossingsAllowed(RoadGeometry road, IReadOnlyList<(float Along, float X, float Z, float Y, bool Junction)> plan, out int count)
    {
        count = 0;
        var rules = CrossingRules;
        foreach (var existing in _crossings)
        {
            if (!existing.Active) continue;
            foreach (var p in plan)
                if (p.Junction && (existing.X - p.X) * (existing.X - p.X) + (existing.Z - p.Z) * (existing.Z - p.Z) < rules.MinFromNode * rules.MinFromNode) return false;
        }
        if (_rail.TrackCount == 0) return true;
        for (int t = 0; t < _rail.TrackCount; t++)
        {
            var track = _rail.GetTrack(new TrackId(t));
            if (track.Retired) continue;
            foreach (var (at, along, x, z) in Intersections(road, track.Geometry))
            {
                float trackY = _rail.TrackHeightAt(track.Id, along);
                float roadY = HeightOfPlan(plan, at);
                if (MathF.Abs(roadY - trackY) >= rules.Clearance) continue;
                if (AngleBetween(road.HeadingAt(at), track.Geometry.HeadingAt(along)) < rules.MinAngle) return false;
                if (along < rules.MinFromNode || along > track.Length - rules.MinFromNode) return false;
                foreach (var p in plan)
                    if (MathF.Abs(p.Along - at) < rules.MinFromNode) return false;
                count++;
            }
        }
        return true;
    }

    static float HeightOfPlan(IReadOnlyList<(float Along, float X, float Z, float Y, bool Junction)> plan, float along)
    {
        int i = 0;
        while (i + 2 < plan.Count && plan[i + 1].Along < along) i++;
        float span = plan[i + 1].Along - plan[i].Along;
        float t = span > 1e-6f ? Math.Clamp((along - plan[i].Along) / span, 0f, 1f) : 0f;
        return plan[i].Y + (plan[i + 1].Y - plan[i].Y) * t;
    }

    /// <summary>Notes the crossings that new roads or tracks have made: every pair that was not checked before and meets at grade.</summary>
    void AddCrossingsFound()
    {
        int tracks = _rail.TrackCount, roads = _net.RoadCount;
        if (tracks == 0) { _crossTracksSeen = 0; _crossRoadsSeen = roads; return; }
        var rules = CrossingRules;
        for (int t = 0; t < tracks; t++)
        {
            var track = _rail.GetTrack(new TrackId(t));
            if (track.Retired) continue;
            for (int r = t < _crossTracksSeen ? _crossRoadsSeen : 0; r < roads; r++)
            {
                var road = _net.GetRoad(new RoadId(r));
                if (road.Retired) continue;
                foreach (var (at, along, x, z) in Intersections(track.Geometry, road.Geometry))
                {
                    if (MathF.Abs(_rail.TrackHeightAt(track.Id, at) - _net.RoadHeightAt(road.Id, along)) >= rules.Clearance) continue;
                    bool known = false;
                    foreach (var c in _crossings)
                        if (c.Active && (c.X - x) * (c.X - x) + (c.Z - z) * (c.Z - z) < 9f) { known = true; break; }
                    if (known) continue;
                    var crossing = new LevelCrossing(_crossings.Count, x, z);
                    _crossings.Add(crossing);
                    Charge(Price(rules.Price), LedgerEntry.Construction);
                }
            }
        }
        _crossTracksSeen = tracks;
        _crossRoadsSeen = roads;
        _crossingsSyncedFor = -1;
        SyncCrossings();
    }

    // ---- Resolving them against the current network -----------------------------------------------------------------

    /// <summary>Finds the track and road at each crossing again, and lays out the zones on the road lanes. Done when the road or rail network has changed.</summary>
    void SyncCrossings()
    {
        long stamp = ((long)_net.Version << 24) ^ (long)_rail.Version;
        if (stamp == _crossingsSyncedFor || _crossings.Count == 0) { _crossingsSyncedFor = stamp; return; }
        _crossingsSyncedFor = stamp;
        var rules = CrossingRules;
        _trackCrossings.Clear();
        var zones = new List<(int Lane, int Crossing, float Start, float End)>();
        foreach (var c in _crossings)
        {
            if (!c.Active) continue;
            var trackId = _rail.TrackAt(c.X, c.Z, 3f);
            var roadId = _net.RoadAt(c.X, c.Z, 3f);
            if (trackId == null || roadId == null) { Deactivate(c); continue; }
            var track = _rail.GetTrack(trackId.Value);
            var road = _net.GetRoad(roadId.Value);
            float trackAlong = track.Geometry.NearestAlong(c.X, c.Z), roadAlong = road.Geometry.NearestAlong(c.X, c.Z);
            if (MathF.Abs(_rail.TrackHeightAt(track.Id, trackAlong) - _net.RoadHeightAt(road.Id, roadAlong)) >= rules.Clearance) { Deactivate(c); continue; }
            float angle = AngleBetween(track.Geometry.HeadingAt(trackAlong), road.Geometry.HeadingAt(roadAlong));
            float sine = MathF.Max(MathF.Sin(angle), 0.35f);
            c.Track = track.Id;
            c.TrackAlong = trackAlong;
            c.TrackHalf = RoadNetwork.HalfWidth(road.Type) / sine + 2f;
            c.Road = road.Id;
            c.RoadAlong = roadAlong;
            c.RoadHalf = rules.BandHalfWidth / sine;
            c.Angle = angle;
            if (!_trackCrossings.TryGetValue(track.Id.Value, out var list)) _trackCrossings[track.Id.Value] = list = new List<int>();
            list.Add(c.Id);
            foreach (var lane in road.ForwardLanes) zones.Add((lane.Value, c.Id, roadAlong - c.RoadHalf, roadAlong + c.RoadHalf));
            foreach (var lane in road.BackwardLanes) zones.Add((lane.Value, c.Id, road.Length - roadAlong - c.RoadHalf, road.Length - roadAlong + c.RoadHalf));
        }
        zones.Sort((p, q) => p.Lane != q.Lane ? p.Lane.CompareTo(q.Lane) : p.Start.CompareTo(q.Start));
        int lanes = _net.LaneCount;
        _laneCrossFirst = new int[lanes + 1];
        _zoneCrossing = new int[zones.Count];
        _zoneStart = new float[zones.Count];
        _zoneEnd = new float[zones.Count];
        foreach (var z in zones) _laneCrossFirst[z.Lane + 1]++;
        for (int l = 0; l < lanes; l++) _laneCrossFirst[l + 1] += _laneCrossFirst[l];
        for (int k = 0; k < zones.Count; k++)
        {
            _zoneCrossing[k] = zones[k].Crossing;
            _zoneStart[k] = zones[k].Start;
            _zoneEnd[k] = zones[k].End;
        }
    }

    void Deactivate(LevelCrossing c)
    {
        c.Active = false;
        c.State = CrossingState.Open;
        c.Fouled = false;
    }

    // ---- Barriers ---------------------------------------------------------------------------------------------------

    /// <summary>The route coordinate of the middle of a crossing for a train, and the front edge and back edge of the band it covers; false when the route does not cross it.</summary>
    bool CrossingOnRoute(Train t, LevelCrossing c, out float bandStart, out float bandEnd)
    {
        bandStart = bandEnd = 0f;
        float rear = t.Head - t.Length;
        foreach (var s in t.Steps)
        {
            if (s.Track != c.Track || s.End < rear) continue;
            float at = s.Forward ? s.Start + c.TrackAlong : s.Start + s.Length - c.TrackAlong;
            bandStart = at - c.TrackHalf;
            bandEnd = at + c.TrackHalf;
            if (bandEnd < rear) continue;
            return true;
        }
        return false;
    }

    /// <summary>Opens and closes the barriers: down once a train that will cross is close, up a few seconds after the last one has cleared.</summary>
    void UpdateCrossings()
    {
        if (_crossings.Count == 0) return;
        SyncCrossings();
        var rules = CrossingRules;
        foreach (var c in _crossings)
        {
            if (!c.Active) continue;
            bool want = false, occupied = false;
            foreach (var t in _trains)
            {
                if (!t.Active || !CrossingOnRoute(t, c, out float bandStart, out float bandEnd)) continue;
                float rear = t.Head - t.Length;
                if (rear < bandEnd && t.Head > bandStart) { occupied = true; continue; }
                if (t.Head > bandStart || t.Approved < bandEnd || t.Status == TrainStatus.AtStation) continue;
                float d = bandStart - t.Head;
                if (d / MathF.Max(t.Speed, 4f) < rules.WarnSeconds + rules.LowerSeconds) want = true;
            }
            want |= occupied;
            switch (c.State)
            {
                case CrossingState.Open:
                    if (want) { c.State = CrossingState.Lowering; c.Timer = 0f; }
                    break;
                case CrossingState.Lowering:
                    c.Timer += StepSeconds;
                    if (c.Timer >= rules.LowerSeconds) { c.State = CrossingState.Closed; c.Timer = 0f; }
                    break;
                default:
                    if (want) c.Timer = 0f;
                    else
                    {
                        c.Timer += StepSeconds;
                        if (c.Timer >= rules.ClearSeconds) { c.State = CrossingState.Open; c.Timer = 0f; }
                    }
                    break;
            }
            c.Fouled = ZoneObstructed(c);
        }
    }

    IEnumerable<int> LanesOf(LevelCrossing c)
    {
        var road = _net.GetRoad(c.Road);
        foreach (var lane in road.ForwardLanes) yield return lane.Value;
        foreach (var lane in road.BackwardLanes) yield return lane.Value;
    }

    /// <summary>Whether a wreck, debris or a standing vehicle is in the crossing.</summary>
    bool ZoneObstructed(LevelCrossing c)
    {
        var road = _net.GetRoad(c.Road);
        foreach (int lane in LanesOf(c))
        {
            bool backward = _net.GetLane(new LaneId(lane)).StartNode == road.To;
            float centre = backward ? road.Length - c.RoadAlong : c.RoadAlong;
            for (int r = _laneStart[lane]; r < _laneStart[lane + 1]; r++)
            {
                int j = _order[r];
                if (_pos[j] < centre - c.RoadHalf || _pos[j] - _length[j] > centre + c.RoadHalf) continue;
                if (_kind[j] != VehicleKind.Car || _speed[j] < 1f) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// What stops a train at a crossing it has not reached: the front edge of the band of a crossing whose barriers are not down yet
    /// (<paramref name="emergencyStop"/>, kept to only by braking hard, so a train never slows for barriers that will be down in time), and
    /// of one with a vehicle or wreck in it (<paramref name="blockedStop"/>). Infinity for none.
    /// </summary>
    void CrossingStops(Train t, out float emergencyStop, out float blockedStop)
    {
        emergencyStop = blockedStop = float.PositiveInfinity;
        foreach (var s in t.Steps)
        {
            if (s.End < t.Head) continue;
            if (!_trackCrossings.TryGetValue(s.Track.Value, out var list)) continue;
            foreach (int id in list)
            {
                var c = _crossings[id];
                if (!c.Active) continue;
                float at = s.Forward ? s.Start + c.TrackAlong : s.Start + s.Length - c.TrackAlong;
                float bandStart = at - c.TrackHalf;
                if (t.Head > bandStart || bandStart > t.Approved) continue;
                if (c.Fouled) blockedStop = MathF.Min(blockedStop, bandStart);
                else if (c.State != CrossingState.Closed) emergencyStop = MathF.Min(emergencyStop, bandStart);
            }
        }
    }

    // ---- Road vehicles at a crossing --------------------------------------------------------------------------------

    /// <summary>
    /// The braking term for a road vehicle approaching a crossing: an obstacle at the stop line while the barriers are coming down or down,
    /// or while the road beyond the rails is queued up (a vehicle never stops on the rails). A vehicle that can no longer stop goes on.
    /// </summary>
    float CrossingInteraction(int i, float v)
    {
        int lane = _lane[i];
        if (lane + 1 >= _laneCrossFirst.Length) return 0f;
        float worst = 0f;
        for (int k = _laneCrossFirst[lane]; k < _laneCrossFirst[lane + 1]; k++)
        {
            float line = _zoneStart[k] - CrossingRules.RoadStopMargin;
            float gap = line - _pos[i];
            if (gap <= 0f) continue; // over the line: it clears the crossing
            var c = _crossings[_zoneCrossing[k]];
            if (c.State == CrossingState.Open && !QueuedBeyond(i, _zoneEnd[k])) continue;
            if (!CanStop(v, gap) && v > 1f) continue;
            worst = MathF.Max(worst, Interaction(v, MathF.Max(gap, 0.1f), 0f));
        }
        return worst;
    }

    /// <summary>Whether the vehicle ahead is standing so close beyond the crossing that this one would have to stop on it.</summary>
    bool QueuedBeyond(int i, float zoneEnd)
    {
        int lane = _lane[i];
        for (int r = _rankInOrder[i] + 1; r < _laneStart[lane + 1]; r++)
        {
            int j = _order[r];
            if (_lat[j] >= HomeLaneLat) continue;
            return _pos[j] - _length[j] < zoneEnd + _length[i] + _drive.MinGap && _speed[j] < _drive.ExitQueueSpeed;
        }
        return false;
    }

    // ---- Collisions -------------------------------------------------------------------------------------------------

    /// <summary>A train in a crossing and a car in it too: the car is wrecked where it stands and the train halts for a while.</summary>
    void DetectCrossingCollisions()
    {
        if (_crossings.Count == 0) return;
        foreach (var c in _crossings)
        {
            if (!c.Active) continue;
            Train? hitter = null;
            foreach (var t in _trains)
            {
                if (!t.Active || !CrossingOnRoute(t, c, out float bandStart, out float bandEnd)) continue;
                if (t.Head - t.Length < bandEnd && t.Head > bandStart) { hitter = t; break; }
            }
            if (hitter == null) continue;
            var road = _net.GetRoad(c.Road);
            foreach (int lane in LanesOf(c))
            {
                bool backward = _net.GetLane(new LaneId(lane)).StartNode == road.To;
                float centre = backward ? road.Length - c.RoadAlong : c.RoadAlong;
                for (int r = _laneStart[lane]; r < _laneStart[lane + 1]; r++)
                {
                    int j = _order[r];
                    if (_kind[j] != VehicleKind.Car) continue;
                    if (_pos[j] < centre - c.RoadHalf || _pos[j] - _length[j] > centre + c.RoadHalf) continue;
                    int incident = NewIncident(IncidentKind.LevelCrossing, j);
                    MakeWreck(j, incident);
                    if (_line[j] >= 0) OnCompanyCrash(j, incident);
                    OnTrainHit(hitter, incident);
                    c.Collisions++;
                    hitter.HaltSeconds = CrossingRules.CollisionHalt;
                    hitter.Speed = 0f;
                    hitter.Acceleration = 0f;
                }
            }
        }
    }

    ulong CrossingHash()
    {
        ulong h = (ulong)_crossings.Count;
        foreach (var c in _crossings)
        {
            h = h * 31 + (c.Active ? 1UL : 0UL);
            h = h * 31 + (ulong)c.State;
            h = h * 31 + (c.Fouled ? 5UL : 0UL);
            h = h * 31 + (ulong)c.Collisions;
        }
        return h;
    }
}
