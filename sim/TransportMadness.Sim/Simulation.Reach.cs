namespace TransportMadness.Sim;

/// <summary>A building outside the coverage circle of a station that its streets still lead to (M6h): <see cref="Coverage"/> from 0 to 1 and the walk along the streets in metres.</summary>
public readonly record struct ReachedBuilding(int Building, float Coverage, float Distance);

/// <summary>A station that a building outside its circle is within walking reach of, along the streets (M6h).</summary>
public readonly record struct ReachingStation(int Station, float Coverage, float Distance);

/// <summary>
/// A road with the walking distance from a station to each of its ends, <see cref="float.PositiveInfinity"/> for an end not reached (M6h). For the road a station that is not built yet would
/// split, <see cref="SourceAlong"/> is where along it the station would stand (NaN for every other road): the walk at a point of it is the distance to there.
/// </summary>
public readonly record struct ReachRoad(RoadId Road, float FromDistance, float ToDistance, float Length, float SourceAlong = float.NaN)
{
    /// <summary>The walk in metres to the point <paramref name="s"/> metres along the road.</summary>
    public float WalkAt(float s)
    {
        float d = MathF.Min(FromDistance + s, ToDistance + Length - s);
        return float.IsNaN(SourceAlong) ? d : MathF.Min(d, MathF.Abs(s - SourceAlong));
    }
}

/// <summary>What a station at a place would reach (M6h): the walk over the roads, and the buildings in its circle and outside it that its streets reach, for a preview.</summary>
public sealed record StationReachPlan(Dictionary<int, float> Walk, RoadId? SourceRoad, float SourceAlong, float X, float Z, List<int> Circle, List<ReachedBuilding> Reached, int Industries);

/// <summary>
/// Coverage along the streets (M6h). Inside its circle (<c>stations.coverageRadius</c>) a station covers a building fully, as before. Beyond it coverage spreads along the roads: the
/// walk along the streets from the station's node to the building's access node, plus the straight walk from there to the door, gives a number from 1 (up to the radius) falling in a line to 0
/// at <c>stations.reachRadius</c>. People at a building with a number below 1 take the station with that chance (a draw made once for each group, station and place), and walk the
/// street distance to it. Only buildings and only road stations: industries, gateways, cargo and what a station accepts keep the circle.
/// </summary>
public sealed partial class Simulation
{
    List<ReachedBuilding[]> _stationReach = new();           // by station id: the buildings outside its circle that its streets reach
    List<ReachingStation[]> _buildingReach = new();           // by building id: the stations that reach it from outside their circles
    readonly Dictionary<(long Key, long Extra), ModeChoice> _reachChoices = new();
    long _reachChoicesVersion = -1;
    long _reachNetworkVersion = -1; // the network the reach was worked out on: a road built or torn up changes what the streets reach

    /// <summary>The coverage, 0 to 1, of a place at a walk of this many metres along the streets.</summary>
    public float CoverageAtWalk(float metres)
    {
        float full = _stationTuning.CoverageRadius, end = _stationTuning.ReachRadius;
        if (metres <= full) return 1f;
        if (metres >= end || end <= full) return 0f;
        return 1f - (metres - full) / (end - full);
    }

    /// <summary>The walking distance along the roads from a node to every node within <c>stations.reachRadius</c>.</summary>
    public Dictionary<int, float> WalkReach(int node) => WalkReach(node < 0 ? Array.Empty<(int, float)>() : new[] { (node, 0f) });

    /// <summary>The same from several starting nodes, each with the distance already walked to it (the two ends of a road that a station standing in the middle of it would split).</summary>
    public Dictionary<int, float> WalkReach(IEnumerable<(int Node, float Distance)> seeds)
    {
        var distance = new Dictionary<int, float>();
        SyncNetwork();
        float limit = _stationTuning.ReachRadius;
        var queue = new PriorityQueue<int, float>();
        foreach (var (node, d0) in seeds)
        {
            if (node < 0 || d0 > limit || (distance.TryGetValue(node, out float known) && known <= d0)) continue;
            distance[node] = d0;
            queue.Enqueue(node, d0);
        }
        while (queue.TryDequeue(out int n, out float d))
        {
            if (d > distance[n]) continue;
            foreach (var id in _net.RoadsAt(n))
            {
                var road = _net.GetRoad(id);
                if (road.Retired) continue;
                int other = road.From == n ? road.To : road.From;
                float nd = d + road.Length;
                if (nd > limit || (distance.TryGetValue(other, out float known) && known <= nd)) continue;
                distance[other] = nd;
                queue.Enqueue(other, nd);
            }
        }
        return distance;
    }

    /// <summary>The roads the walk reached, with the distance to each end (one entry for a road); <paramref name="sourceRoad"/> is the road a station not built yet would stand on.</summary>
    public List<ReachRoad> ReachRoads(Dictionary<int, float> distance, RoadId? sourceRoad = null, float sourceAlong = float.NaN)
    {
        var seen = new HashSet<int>();
        var list = new List<ReachRoad>();
        foreach (int node in distance.Keys)
            foreach (var id in _net.RoadsAt(node))
            {
                var road = _net.GetRoad(id);
                if (road.Retired || !seen.Add(id.Value)) continue;
                list.Add(new ReachRoad(id, distance.GetValueOrDefault(road.From, float.PositiveInfinity), distance.GetValueOrDefault(road.To, float.PositiveInfinity), road.Length,
                    sourceRoad is { } source && source == id ? sourceAlong : float.NaN));
            }
        list.Sort((a, b) => a.Road.Value.CompareTo(b.Road.Value));
        return list;
    }

    /// <summary>The standing buildings outside the circle of (x, z) that the walk <paramref name="distance"/> reaches, by building id.</summary>
    public List<ReachedBuilding> BuildingsReached(float x, float z, Dictionary<int, float> distance)
    {
        var list = new List<ReachedBuilding>();
        float circle2 = _stationTuning.CoverageRadius * _stationTuning.CoverageRadius;
        foreach (var b in _buildings)
        {
            if (b.State != BuildingState.Active || (b.X - x) * (b.X - x) + (b.Z - z) * (b.Z - z) <= circle2) continue;
            int node = AccessNodeOf(b);
            if (node < 0 || !distance.TryGetValue(node, out float d)) continue;
            var (nx, nz) = _net.GetNode(node);
            float walk = d + MathF.Sqrt((b.X - nx) * (b.X - nx) + (b.Z - nz) * (b.Z - nz));
            float coverage = CoverageAtWalk(walk);
            if (coverage > 0f) list.Add(new ReachedBuilding(b.Id, coverage, walk));
        }
        return list;
    }

    /// <summary>Works out what each road station reaches along the streets; called by <see cref="RefreshCoverage"/>.</summary>
    void RefreshReach()
    {
        SyncNetwork();
        _reachNetworkVersion = _syncedNetworkVersion;
        var perBuilding = new List<ReachingStation>?[_buildings.Count];
        _stationReach = new List<ReachedBuilding[]>();
        for (int s = 0; s < _stations.Count; s++)
        {
            var station = _stations[s];
            if (!station.Active || station.DockKind != 0 || station.Mode != StationMode.Road || _stationTuning.ReachRadius <= _stationTuning.CoverageRadius)
            {
                _stationReach.Add(Array.Empty<ReachedBuilding>());
                continue;
            }
            var (x, z) = PointOf(station);
            var reached = BuildingsReached(x, z, WalkReach(station.Node));
            foreach (var r in reached) (perBuilding[r.Building] ??= new List<ReachingStation>()).Add(new ReachingStation(s, r.Coverage, r.Distance));
            _stationReach.Add(reached.ToArray());
        }
        _buildingReach = perBuilding.Select(l => l?.ToArray() ?? Array.Empty<ReachingStation>()).ToList();
    }

    /// <summary>The buildings outside its circle that a station reaches along the streets, with their coverage (empty for a rail station, a stop at an industry or a closed station).</summary>
    public IReadOnlyList<ReachedBuilding> ReachOf(Station station)
    {
        SyncNetwork();
        if (_coverageDirty || _stationBuildings.Count != _stations.Count || _reachNetworkVersion != _syncedNetworkVersion) RefreshCoverage();
        return station.Id < _stationReach.Count ? _stationReach[station.Id] : Array.Empty<ReachedBuilding>();
    }

    /// <summary>The stations a building is reached by from outside their circles, with the coverage and the walk to each.</summary>
    public IReadOnlyList<ReachingStation> ReachingStationsOf(int building)
    {
        SyncNetwork();
        if (_coverageDirty || _buildingStations.Count != _buildings.Count || _reachNetworkVersion != _syncedNetworkVersion) RefreshCoverage();
        return building >= 0 && building < _buildingReach.Count ? _buildingReach[building] : Array.Empty<ReachingStation>();
    }

    /// <summary>
    /// The stations a trip group can use at a place: those that cover it, and those that reach it with a coverage under 1 for which the group's own draw (a pure function of its seed, the
    /// station and the place) is under that coverage. The same group always draws the same for the same pair.
    /// </summary>
    internal int[] StationsFor(TripGroup g, int place)
    {
        var full = PlaceStations(place);
        if (place >= IndustryPlace || place >= _buildingReach.Count || _buildingReach[place].Length == 0) return full;
        List<int>? list = null;
        foreach (var r in _buildingReach[place])
            if (Unit(g.Seed, (long)r.Station * 1_000_003L + place, 53) < r.Coverage) (list ??= new List<int>(full)).Add(r.Station);
        return list?.ToArray() ?? full;
    }

    /// <summary>
    /// The walk in metres to a station from a place as a straight line would give it: for a station that reaches the place from outside its circle the street distance, brought back by the
    /// detour the callers add to a straight line.
    /// </summary>
    internal float WalkMetres(int place, Station station)
    {
        if (place < IndustryPlace && place < _buildingReach.Count)
            foreach (var r in _buildingReach[place])
                if (r.Station == station.Id) return r.Distance / MathF.Max(1f, _trips.WalkDetour);
        var (x, z) = PointOf(station);
        var (px, pz) = PlacePoint(place);
        return MathF.Sqrt((px - x) * (px - x) + (pz - z) * (pz - z));
    }

    /// <summary>
    /// What a road station clicked for at (x, z) would reach, for the build preview: its walk over the roads (from its node, or from the two ends of the road it would split), the standing
    /// buildings in its circle, those outside it that its streets reach and the industries in its circle. Null where no station can stand (<see cref="FindStationSite"/>).
    /// </summary>
    public StationReachPlan? PlanStationReach(float x, float z)
    {
        if (FindStationSite(x, z) is not { } site) return null;
        Dictionary<int, float> walk;
        RoadId? source = null;
        if (site.Node >= 0) walk = WalkReach(site.Node);
        else
        {
            var road = _net.GetRoad(site.Road);
            walk = WalkReach(new[] { (road.From, site.Along), (road.To, road.Length - site.Along) });
            source = site.Road;
        }
        float radius = _stationTuning.CoverageRadius;
        var circle = BuildingsNear(site.X, site.Z, radius).Where(id => _buildings[id].State == BuildingState.Active).ToList();
        return new StationReachPlan(walk, source, site.Along, site.X, site.Z, circle, BuildingsReached(site.X, site.Z, walk), IndustriesNear(site.X, site.Z, radius).Count);
    }
}
