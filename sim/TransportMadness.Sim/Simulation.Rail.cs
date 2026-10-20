namespace TransportMadness.Sim;

public enum TrainStatus : byte
{
    /// <summary>Under way, or standing for a reason that is not the track.</summary>
    Running,
    /// <summary>Standing at a station, loading.</summary>
    AtStation,
    /// <summary>Standing in front of track that another train holds (a red signal).</summary>
    WaitingForTrack,
    /// <summary>Standing after hitting a vehicle at a level crossing.</summary>
    Halted,
    /// <summary>No way to the next station over the tracks as they are now.</summary>
    NoRoute,
    /// <summary>Inside its depot, waiting for the depot's node to be free before it drives out.</summary>
    InDepot,
}

/// <summary>A depot on the rail network: trains are bought here and appear here.</summary>
public sealed class RailDepot
{
    internal RailDepot(int id, int node)
    {
        Id = id;
        Node = node;
    }

    public int Id { get; }
    public int Node { get; internal set; }
    /// <summary>False once every track at its node has been torn down.</summary>
    public bool Active { get; internal set; } = true;
}

/// <summary>One track travelled in one direction as part of a train's route.</summary>
internal struct RouteStep
{
    public TrackId Track;
    public bool Forward;   // From to To
    public float Start;    // route coordinate where the step begins
    public float Length;
    public float End => Start + Length;
}

/// <summary>A piece of the network a train holds (a track or a node): released when the rear has passed <see cref="Hi"/>.</summary>
internal struct Hold
{
    public bool IsNode;
    public int Id;
    public float Lo, Hi;
}

/// <summary>A consist: a locomotive and wagons running a rail line. Position is the route coordinate of its front.</summary>
public sealed class Train
{
    internal Train(int id, int line, int locomotive, int wagon, int wagons, float length, float emptyMass, int capacity)
    {
        Id = id;
        Line = line;
        Locomotive = locomotive;
        Wagon = wagon;
        Wagons = wagons;
        Length = length;
        EmptyMass = emptyMass;
        Capacity = capacity;
    }

    public int Id { get; }
    public int Line { get; internal set; }
    /// <summary>Indices into <see cref="RailTuning.Locomotives"/> and <see cref="RailTuning.Wagons"/>.</summary>
    public int Locomotive { get; }
    public int Wagon { get; }
    public int Wagons { get; }
    public bool Active { get; internal set; } = true;
    /// <summary>Metres from the front of the locomotive to the back of the last wagon.</summary>
    public float Length { get; }
    public float EmptyMass { get; }
    public int Capacity { get; }
    public float Speed { get; internal set; }
    public float Acceleration { get; internal set; }
    /// <summary>Distance along the route of the front of the train.</summary>
    public float Head { get; internal set; }
    /// <summary>Index into the line's stops of the station it is heading for or standing at.</summary>
    public int StopIndex { get; internal set; }
    public TrainStatus Status { get; internal set; } = TrainStatus.Running;
    /// <summary>Units of cargo (or people) on board and what it is (a cargo index, -1 when empty).</summary>
    public int Load { get; internal set; }
    public int LoadCargo { get; internal set; } = -1;
    public long Income { get; internal set; }
    public long UpkeepPaid { get; internal set; }
    /// <summary>The train whose hold on the track keeps this one standing, -1 when none.</summary>
    public int BlockedBy { get; internal set; } = -1;
    /// <summary>Seconds it has stood waiting for track.</summary>
    public float WaitedSeconds { get; internal set; }
    /// <summary>In a ring of trains that wait for each other (see <see cref="Simulation.RailWarnings"/>).</summary>
    public bool Deadlocked { get; internal set; }
    /// <summary>Standing in its depot and waiting for the depot's node to be free; it holds nothing yet and the trains in a depot do not block one another.</summary>
    public bool InDepot { get; internal set; }
    /// <summary>The rail depot it is driving back to, to be parked there; -1 when it is on its line.</summary>
    public int ReturnDepot { get; internal set; } = -1;

    internal List<RouteStep> Steps { get; } = new();
    internal List<Hold> Holds { get; } = new();
    internal float Approved;       // the head may go this far (a boundary node, or the end of the route)
    internal float StopAt = float.PositiveInfinity; // where the leg's station is, once the route reaches it
    internal int Dwell;
    internal int LoadFrom = -1;
    internal long LoadTick;
    internal List<PaxLot>? Riders;
    internal int SpawnNode = -1;   // while the route is empty: the depot node it stands at
    internal long ReplanTick;
    internal float HaltSeconds;
    internal float EndOfRoute => Steps.Count == 0 ? 0f : Steps[^1].End;
    internal float EndOfRoutePublic => EndOfRoute;
}

/// <summary>A warning about the trains: a ring of trains each waiting for track another holds.</summary>
public readonly record struct RailWarning(long Tick, string Kind, IReadOnlyList<int> Trains);

public readonly record struct CarPose(float X, float Y, float Z, float Heading, float Length, bool IsLocomotive, bool Visible);

/// <summary>Rail: stations, depots, trains and the track they hold.</summary>
public sealed partial class Simulation
{
    readonly RailNetwork _rail;
    readonly RailTuning _railTuning;
    readonly List<RailDepot> _railDepots = new();
    readonly List<Train> _trains = new();
    int _railSynced = -1;
    int[] _trackOwner = Array.Empty<int>();
    int[] _nodeOwner = Array.Empty<int>();
    bool[] _railBoundary = Array.Empty<bool>();
    bool _railBoundaryDirty = true;
    readonly List<RailWarning> _railWarnings = new();
    readonly List<(int Train, int Waits)> _scratchWaits = new();

    public RailNetwork Rail => _rail;
    public IReadOnlyList<RailDepot> RailDepots => _railDepots;
    public IReadOnlyList<Train> Trains => _trains;
    public IReadOnlyList<LocomotiveTuning> Locomotives => _railTuning.Locomotives;
    public IReadOnlyList<WagonTuning> Wagons => _railTuning.Wagons;
    /// <summary>Deadlocks found so far, oldest first.</summary>
    public IReadOnlyList<RailWarning> RailWarnings => _railWarnings;

    bool RailInUse => _rail.TrackCount > 0 || _trains.Count > 0 || _railDepots.Count > 0;

    // ---- Syncing with the network ---------------------------------------------------------------------------------

    /// <summary>Picks up tracks and signals built since the last call, and moves trains and holds over tracks that were split.</summary>
    void SyncRail()
    {
        if (_rail.Version == _railSynced && !_railBoundaryDirty) return;
        int tracks = _rail.TrackCount, nodes = _rail.NodeCount;
        int oldTracks = _trackOwner.Length, oldNodes = _nodeOwner.Length;
        Array.Resize(ref _trackOwner, tracks);
        Array.Resize(ref _nodeOwner, nodes);
        for (int i = oldTracks; i < tracks; i++) _trackOwner[i] = -1;
        for (int i = oldNodes; i < nodes; i++) _nodeOwner[i] = -1;

        if (_rail.Version != _railSynced)
        {
            _railSynced = _rail.Version;
            foreach (var train in _trains)
                if (train.Active) RemapTrain(train);
            for (int t = 0; t < tracks; t++)
                if (_rail.GetTrack(new TrackId(t)).Retired) _trackOwner[t] = -1;
            ClosedOrphanedRailStations();
            SyncCrossings();
        }

        if (_railBoundary.Length != nodes) _railBoundary = new bool[nodes];
        else Array.Clear(_railBoundary);
        foreach (var s in _rail.Signals)
            if (s.Active) _railBoundary[s.Node] = true;
        foreach (var station in _stations)
            if (station.Active && station.Mode == StationMode.Rail) _railBoundary[station.RailNode] = true;
        foreach (var depot in _railDepots)
            if (depot.Active) _railBoundary[depot.Node] = true;
        _railBoundaryDirty = false;
    }

    /// <summary>Whether a node splits the network into blocks: a signal, a station or a depot stands there.</summary>
    internal bool IsBoundary(int node) => node < _railBoundary.Length && _railBoundary[node];

    /// <summary>Puts a train's route and holds onto the pieces that replaced a split track, and removes the train if track under it was torn down.</summary>
    void RemapTrain(Train train)
    {
        bool lost = false;
        // Route steps: a split track becomes its two pieces, in the order the train drives them.
        for (int k = 0; k < train.Steps.Count; k++)
        {
            var step = train.Steps[k];
            var track = _rail.GetTrack(step.Track);
            if (!track.Retired) continue;
            if (track.SplitFirst.IsNone)
            {
                // Torn down. A train that is on it is lost; one whose route runs over it only loses the rest of the route.
                if (train.Head - train.Length < step.End && train.Head > step.Start) lost = true;
                else TruncateRoute(train, k);
                break;
            }
            var first = _rail.GetTrack(track.SplitFirst);
            var second = _rail.GetTrack(track.SplitSecond);
            var a = new RouteStep { Track = track.SplitFirst, Forward = step.Forward, Length = first.Length };
            var b = new RouteStep { Track = track.SplitSecond, Forward = step.Forward, Length = second.Length };
            if (!step.Forward) (a, b) = (b, a);
            a.Start = step.Start;
            b.Start = step.Start + a.Length;
            train.Steps[k] = a;
            train.Steps.Insert(k + 1, b);
            // The new node in the middle belongs to a train that holds both pieces.
            for (int h = 0; h < train.Holds.Count; h++)
            {
                var hold = train.Holds[h];
                if (hold.IsNode || hold.Id != step.Track.Value) continue;
                train.Holds[h] = hold with { Id = track.SplitFirst.Value };
                train.Holds.Add(hold with { Id = track.SplitSecond.Value });
                train.Holds.Add(new Hold { IsNode = true, Id = _rail.GetTrack(track.SplitFirst).To == _rail.GetTrack(track.SplitSecond).From ? _rail.GetTrack(track.SplitFirst).To : _rail.GetTrack(track.SplitFirst).From, Lo = b.Start, Hi = b.Start });
                break;
            }
            k--; // look at the first piece again (it is current)
        }
        // Holds on retired tracks that were not split vanish with them.
        for (int h = train.Holds.Count - 1; h >= 0; h--)
        {
            var hold = train.Holds[h];
            if (!hold.IsNode && _rail.GetTrack(new TrackId(hold.Id)).Retired) train.Holds.RemoveAt(h);
        }
        foreach (var hold in train.Holds)
        {
            if (hold.IsNode) _nodeOwner[hold.Id] = train.Id;
            else _trackOwner[hold.Id] = train.Id;
        }
        if (lost) RemoveTrain(train, refund: false);
    }

    /// <summary>Cuts a route short at step <paramref name="from"/> (that step and all after it go): the train stops at the end of what is left and plans again.</summary>
    void TruncateRoute(Train train, int from)
    {
        train.Steps.RemoveRange(from, train.Steps.Count - from);
        float end = train.EndOfRoute;
        train.Approved = MathF.Min(train.Approved, end);
        train.StopAt = float.PositiveInfinity;
        if (train.Status == TrainStatus.NoRoute) train.Status = TrainStatus.Running;
        for (int h = train.Holds.Count - 1; h >= 0; h--)
        {
            var hold = train.Holds[h];
            if (hold.Lo >= end && hold.Lo > train.Head) ReleaseHold(train, h);
        }
    }

    void ReleaseHold(Train train, int index)
    {
        var hold = train.Holds[index];
        if (hold.IsNode) { if (_nodeOwner[hold.Id] == train.Id) _nodeOwner[hold.Id] = -1; }
        else if (_trackOwner[hold.Id] == train.Id) _trackOwner[hold.Id] = -1;
        train.Holds.RemoveAt(index);
    }

    // ---- Positions ------------------------------------------------------------------------------------------------

    /// <summary>The map position, height and heading (direction of travel) at a route coordinate; the heading is that of the train going forward along its route.</summary>
    public (float X, float Y, float Z, float Heading, bool OnRoute) RoutePoint(Train train, float coordinate)
    {
        if (train.Steps.Count == 0)
        {
            // Standing in a depot, not on track yet: at the depot's node.
            if (train.SpawnNode < 0) return (0f, 0f, 0f, 0f, false);
            var (dx, dz) = _rail.GetNode(train.SpawnNode);
            return (dx, _rail.GetNodeHeight(train.SpawnNode), dz, 0f, false);
        }
        bool before = coordinate < train.Steps[0].Start;
        bool after = coordinate > train.Steps[^1].End;
        coordinate = Math.Clamp(coordinate, train.Steps[0].Start, train.Steps[^1].End);
        int k = 0;
        while (k < train.Steps.Count - 1 && coordinate > train.Steps[k].End) k++;
        var step = train.Steps[k];
        float distance = Math.Clamp(coordinate - step.Start, 0f, step.Length);
        float along = step.Forward ? distance : step.Length - distance;
        var (x, z, heading) = _rail.PointAt(step.Track, along);
        float y = _rail.TrackHeightAt(step.Track, along);
        if (!step.Forward) heading += MathF.PI;
        return (x, y, z, heading, !before && !after);
    }

    /// <summary>The cars of a train, locomotive first, with where each one's middle stands; <paramref name="lead"/> metres further along its route (to draw between two steps).</summary>
    public IReadOnlyList<CarPose> TrainCars(Train train, float lead = 0f)
    {
        var loco = _railTuning.Locomotives[train.Locomotive];
        var wagon = _railTuning.Wagons[train.Wagon];
        var cars = new List<CarPose>(train.Wagons + 1);
        float front = train.Head + lead;
        for (int k = 0; k <= train.Wagons; k++)
        {
            float length = k == 0 ? loco.Length : wagon.Length;
            float centre = front - length * 0.5f;
            var (x, y, z, heading, onRoute) = RoutePoint(train, centre);
            cars.Add(new CarPose(x, y, z, heading, length, k == 0, onRoute));
            front -= length;
        }
        return cars;
    }

    /// <summary>Total mass of the train with what it carries, kilograms.</summary>
    public float MassOf(Train train)
    {
        var wagon = _railTuning.Wagons[train.Wagon];
        return train.EmptyMass + train.Load * wagon.UnitMass;
    }

    // ---- Building -------------------------------------------------------------------------------------------------

    int BuildTrack(BuildTrackCommand c)
    {
        var plan = _rail.PlannedPoints(c.X0, c.Z0, c.X1, c.Z1, c.Sweep);
        var cost = _rail.EstimateCost(c.X0, c.Z0, c.X1, c.Z1, c.Sweep);
        if (plan == null || cost == null) return 0;
        var geometry = RoadGeometry.FromChord(c.X0, c.Z0, c.X1, c.Z1, c.Sweep);
        if (!TrackCrossingsAllowed(geometry, plan, out int crossings)) return 0;
        var inTheWay = BuildingsInTheWay(new[] { (geometry, RoadType.TwoLane) });
        if (!CanAfford(cost.Value.Money + crossings * _railTuning.Crossings.Price + DemolitionOf(inTheWay).Price)) return 0;
        int built = _rail.BuildTrack(c.X0, c.Z0, c.X1, c.Z1, c.Sweep).Count;
        if (built == 0) return 0;
        Charge(Price(cost.Value.Money), LedgerEntry.Construction);
        DemolishForRoad(inTheWay);
        SyncRail();
        AddCrossingsFound();
        return built;
    }

    int RemoveTrack(int track)
    {
        if (track < 0 || track >= _rail.TrackCount || _rail.GetTrack(new TrackId(track)).Retired) return 0;
        float cost = _rail.DemolitionCost(new TrackId(track));
        if (!CanAfford(cost)) return 0;
        _rail.RemoveTrack(new TrackId(track));
        Charge(Price(cost), LedgerEntry.Construction);
        SyncRail();
        return 1;
    }

    /// <summary>
    /// Puts a signal where a click at (x, z) points. On an existing node it faces the track nearest to the click; on open track the
    /// track is split there and the signal faces trains going <see cref="BuildSignalCommand.Forward"/> (from the track's start toward
    /// its end) or the other way. Returns the signal id, -1 when refused.
    /// </summary>
    int BuildSignal(BuildSignalCommand c)
    {
        long price = c.Kind == SignalKind.Path ? _railTuning.PathSignalPrice : _railTuning.SignalPrice;
        if (!CanAfford(price)) return -1;
        SyncRail();
        int node = _rail.FindNodeNear(c.X, c.Z, _railTuning.SnapRadius, withTracksOnly: true);
        TrackId track;
        if (node >= 0)
        {
            if (_rail.TracksAt(node).Count == 0) return -1;
            // The track whose centre line is nearest to the click.
            float best = float.MaxValue;
            track = TrackId.None;
            foreach (var id in _rail.TracksAt(node))
            {
                var g = _rail.GetTrack(id).Geometry;
                float d = g.DistanceTo(c.X, c.Z);
                if (d < best) { best = d; track = id; }
            }
        }
        else
        {
            if (_rail.NearestOnTrack(c.X, c.Z, _railTuning.SnapRadius, _railTuning.StationAlongStep) is not { } point) return -1;
            node = _rail.SplitTrack(point.Track, point.Along, out var first, out var second);
            track = c.Forward ? first : second;
            SyncRail();
        }
        var signal = _rail.SetSignal(track, node, c.Kind);
        if (signal == null) return -1;
        Charge(price, LedgerEntry.Construction);
        _railBoundaryDirty = true;
        SyncRail();
        return signal.Id.Value;
    }

    int RemoveSignal(int signal)
    {
        if (signal < 0 || signal >= _rail.SignalCount) return 0;
        bool done = _rail.RemoveSignal(new SignalId(signal));
        if (done) { _railBoundaryDirty = true; SyncRail(); }
        return done ? 1 : 0;
    }

    /// <summary>The node and track a rail station clicked for at (x, z) would stand on; null when none can.</summary>
    public readonly record struct RailStationSite(int Node, TrackId Track, float Along, float X, float Y, float Z);

    public RailStationSite? FindRailStationSite(float x, float z)
    {
        int node = _rail.FindNodeNear(x, z, _railTuning.SnapRadius, withTracksOnly: true);
        if (node >= 0)
        {
            if (RailStationAt(node) != null) return null;
            var (nx, nz) = _rail.GetNode(node);
            return new RailStationSite(node, TrackId.None, 0f, nx, _rail.GetNodeHeight(node), nz);
        }
        if (_rail.NearestOnTrack(x, z, _railTuning.SnapRadius, _railTuning.StationAlongStep) is not { } point) return null;
        return new RailStationSite(-1, point.Track, point.Along, point.X, point.Y, point.Z);
    }

    /// <summary>The open rail station at a rail node, if any.</summary>
    public Station? RailStationAt(int node)
    {
        foreach (var station in _stations)
            if (station.Active && station.Mode == StationMode.Rail && station.RailNode == node) return station;
        return null;
    }

    /// <summary>What a rail station would cost and cover at a place.</summary>
    public StationQuote? QuoteRailStation(float x, float z)
    {
        if (FindRailStationSite(x, z) is not { } site) return null;
        var asRoad = new StationSite(site.Node, default, site.Along, site.X, site.Y, site.Z);
        float radius = _stationTuning.CoverageRadius;
        int buildings = BuildingsNear(site.X, site.Z, radius).Count(id => _buildings[id].State == BuildingState.Active);
        long price = _railTuning.StationPrice;
        return new StationQuote(asRoad, price, CanAfford(price), radius, buildings, IndustriesNear(site.X, site.Z, radius).Count);
    }

    int BuildRailStation(BuildRailStationCommand c)
    {
        SyncRail();
        long price = _railTuning.StationPrice;
        if (!CanAfford(price) || FindRailStationSite(c.X, c.Z) is not { } site) return -1;
        int node = site.Node;
        if (node < 0)
        {
            node = _rail.SplitTrack(site.Track, site.Along);
            SyncRail();
        }
        TrySpend(price, LedgerEntry.Stations);
        var (x, z) = _rail.GetNode(node);
        _stations.Add(new Station(_stations.Count, node, _cargo.Count, x, z));
        _coverageDirty = true;
        _fields.Add(Array.Empty<float>());
        _fieldVersion.Add(-1);
        _railBoundaryDirty = true;
        SyncRail();
        return _stations.Count - 1;
    }

    /// <summary>Rail stations whose node has lost every track close, as road stations do.</summary>
    void ClosedOrphanedRailStations()
    {
        foreach (var station in _stations)
            if (station.Active && station.Mode == StationMode.Rail && _rail.TracksAt(station.RailNode).Count == 0)
            {
                station.Active = false;
                _coverageDirty = true;
                _transitVersion++;
            }
        foreach (var depot in _railDepots)
            if (depot.Active && _rail.TracksAt(depot.Node).Count == 0) depot.Active = false;
    }

    int BuildRailDepot(AddRailDepotCommand c)
    {
        SyncRail();
        int node = _rail.FindNodeNear(c.X, c.Z, _railTuning.SnapRadius, withTracksOnly: true);
        if (node < 0) return -1;
        foreach (var depot in _railDepots)
            if (depot.Active && depot.Node == node) return -1;
        if (!TrySpend(_railTuning.DepotPrice, LedgerEntry.Depots)) return -1;
        _railDepots.Add(new RailDepot(_railDepots.Count, node));
        _railBoundaryDirty = true;
        SyncRail();
        return _railDepots.Count - 1;
    }

    // ---- Path finding ---------------------------------------------------------------------------------------------

    float StepSecondsOf(Track t, float maxSpeed) => t.Length / MathF.Max(1f, MathF.Min(t.SpeedLimit, maxSpeed));

    /// <summary>The steps leaving a node by each track that a train arriving along <paramref name="arrival"/> can take (every track, when it has no arrival).</summary>
    List<RouteStep> LeavingSteps(int node, TrackId arrival)
    {
        var steps = new List<RouteStep>();
        foreach (var id in _rail.TracksAt(node))
        {
            if (!arrival.IsNone && !_rail.CanPass(arrival, node, id)) continue;
            var t = _rail.GetTrack(id);
            steps.Add(new RouteStep { Track = id, Forward = t.From == node, Length = t.Length });
        }
        return steps;
    }

    /// <summary>
    /// The quickest sequence of steps from one of <paramref name="seeds"/> (the first step of the route) to <paramref name="targetNode"/>,
    /// by travel time at the posted speeds, ties settled by track number; false when there is none.
    /// </summary>
    bool FindRailPath(List<RouteStep> seeds, int targetNode, float maxSpeed, out List<RouteStep> path, out float seconds, Func<TrackId, bool>? skip = null)
    {
        path = new List<RouteStep>();
        seconds = 0f;
        int states = _rail.TrackCount * 2;
        var dist = new float[states];
        var prev = new int[states];
        Array.Fill(dist, float.PositiveInfinity);
        Array.Fill(prev, -1);
        var queue = new PriorityQueue<int, (float, int)>();
        foreach (var seed in seeds)
        {
            if (skip != null && skip(seed.Track)) continue;
            int s = seed.Track.Value * 2 + (seed.Forward ? 1 : 0);
            float c = StepSecondsOf(_rail.GetTrack(seed.Track), maxSpeed);
            if (c >= dist[s]) continue;
            dist[s] = c;
            queue.Enqueue(s, (c, s));
        }
        while (queue.TryDequeue(out int state, out var priority))
        {
            if (priority.Item1 > dist[state]) continue;
            var track = _rail.GetTrack(new TrackId(state >> 1));
            bool forward = (state & 1) != 0;
            int node = forward ? track.To : track.From;
            if (node == targetNode)
            {
                var reversed = new List<RouteStep>();
                for (int s = state; s >= 0; s = prev[s])
                {
                    var t = _rail.GetTrack(new TrackId(s >> 1));
                    reversed.Add(new RouteStep { Track = t.Id, Forward = (s & 1) != 0, Length = t.Length });
                }
                reversed.Reverse();
                path = reversed;
                seconds = dist[state];
                return true;
            }
            foreach (var next in _rail.Exits(track.Id, node))
            {
                if (skip != null && skip(next)) continue;
                var nt = _rail.GetTrack(next);
                int ns = next.Value * 2 + (nt.From == node ? 1 : 0);
                float c = dist[state] + StepSecondsOf(nt, maxSpeed);
                if (c >= dist[ns]) continue;
                dist[ns] = c;
                prev[ns] = state;
                queue.Enqueue(ns, (c, ns));
            }
        }
        return false;
    }

    /// <summary>Travel time between two rail stations by the quickest way over the tracks, infinity when they are not joined (either way round).</summary>
    float RailRideSeconds(Station from, Station to)
    {
        if (!from.Active || !to.Active || from.Mode != StationMode.Rail || to.Mode != StationMode.Rail) return float.PositiveInfinity;
        float best = float.PositiveInfinity;
        float vmax = _railTuning.Locomotives.Count > 0 ? _railTuning.Locomotives[0].MaxSpeed : _railTuning.SpeedLimit;
        if (FindRailPath(LeavingSteps(from.RailNode, TrackId.None), to.RailNode, vmax, out _, out float s)) best = s;
        return best;
    }

    // ---- Depots, buying and selling -------------------------------------------------------------------------------

    int LocomotiveIndex(string id)
    {
        for (int k = 0; k < _railTuning.Locomotives.Count; k++)
            if (_railTuning.Locomotives[k].Id == id) return k;
        return -1;
    }

    int WagonIndex(string id)
    {
        for (int k = 0; k < _railTuning.Wagons.Count; k++)
            if (_railTuning.Wagons[k].Id == id) return k;
        return -1;
    }

    /// <summary>What a train of a locomotive and some wagons costs to buy.</summary>
    public long TrainPrice(string locomotive, string wagon, int wagons)
    {
        int l = LocomotiveIndex(locomotive), w = WagonIndex(wagon);
        return l < 0 || w < 0 ? -1 : _railTuning.Locomotives[l].Price + wagons * _railTuning.Wagons[w].Price;
    }

    /// <summary>
    /// Buys a train at a rail depot for a rail line. It stands in the depot (its length behind the depot's node) and drives out toward the
    /// first station once the depot's node is free: trains bought together wait inside and leave one after another. Returns the train id,
    /// -1 when refused: no such depot, locomotive, wagon or line, too many or no wagons, no way to the first station, or no money.
    /// </summary>
    int BuyTrain(BuyTrainCommand c)
    {
        if (c.Depot == BuyTrainCommand.NearestDepot) return BuyTrainAtNearestDepot(c);
        SyncRail();
        int l = LocomotiveIndex(c.Locomotive), w = WagonIndex(c.Wagon);
        if (l < 0 || w < 0 || c.Depot < 0 || c.Depot >= _railDepots.Count || !_railDepots[c.Depot].Active) return -1;
        if (c.Line < 0 || c.Line >= _lines.Count || !_lines[c.Line].Active || _lines[c.Line].Mode != StationMode.Rail) return -1;
        var loco = _railTuning.Locomotives[l];
        var wagon = _railTuning.Wagons[w];
        if (c.Wagons < 1 || c.Wagons > loco.MaxWagons) return -1;
        long price = loco.Price + c.Wagons * wagon.Price;
        if (!CanAfford(price)) return -1;
        var depot = _railDepots[c.Depot];
        if (!DepotRoute(depot, c.Line, l, out var path)) return -1;

        TrySpend(price, LedgerEntry.Vehicles);
        return StartTrain(depot, l, w, c.Wagons, c.Line, path).Id;
    }

    /// <summary>The way out of a depot to the first station of a line. A depot on that station starts the train there, loading; anywhere else it drives out to it. False when there is none.</summary>
    bool DepotRoute(RailDepot depot, int line, int locomotive, out List<RouteStep> path)
    {
        path = new List<RouteStep>();
        var first = _stations[_lines[line].Stops[0]];
        if (!first.Active) return false;
        float vmax = _railTuning.Locomotives[locomotive].MaxSpeed;
        if (depot.Node == first.RailNode) return _rail.TracksAt(depot.Node).Count > 0;
        return FindRailPath(LeavingSteps(depot.Node, TrackId.None), first.RailNode, vmax, out path, out _);
    }

    /// <summary>Puts a train in a depot, the money already settled, with the route out to the first station of its line.</summary>
    Train StartTrain(RailDepot depot, int l, int w, int wagons, int line, List<RouteStep> path)
    {
        var loco = _railTuning.Locomotives[l];
        var wagon = _railTuning.Wagons[w];
        float length = loco.Length + wagons * wagon.Length;
        float mass = loco.Mass + wagons * wagon.Mass;
        var train = new Train(_trains.Count, line, l, w, wagons, length, mass, wagons * wagon.Capacity);
        float start = 0f;
        foreach (var step in path)
        {
            var s = step;
            s.Start = start;
            start += s.Length;
            train.Steps.Add(s);
        }
        train.StopAt = start;
        train.SpawnNode = depot.Node;
        train.Approved = 0f;
        train.Head = 0f;
        train.StopIndex = 0;
        if (_nodeOwner[depot.Node] == -1) AddHold(train, isNode: true, depot.Node, 0f, 0f);
        else
        {
            train.InDepot = true;
            train.Status = TrainStatus.InDepot;
        }
        _trains.Add(train);
        _transitVersion++;
        return train;
    }

    /// <summary>Tries the rail depots in the order of their travel time to the line's first station and buys at the first that takes the train.</summary>
    int BuyTrainAtNearestDepot(BuyTrainCommand c)
    {
        SyncRail();
        if (c.Line < 0 || c.Line >= _lines.Count || !_lines[c.Line].Active || _lines[c.Line].Mode != StationMode.Rail) return -1;
        var first = _stations[_lines[c.Line].Stops[0]];
        if (!first.Active) return -1;
        int l = LocomotiveIndex(c.Locomotive);
        float vmax = l >= 0 ? _railTuning.Locomotives[l].MaxSpeed : _railTuning.SpeedLimit;
        var order = new List<(float Seconds, int Depot)>();
        foreach (var depot in _railDepots)
        {
            if (!depot.Active) continue;
            if (depot.Node == first.RailNode) { order.Add((0f, depot.Id)); continue; }
            if (FindRailPath(LeavingSteps(depot.Node, TrackId.None), first.RailNode, vmax, out _, out float seconds)) order.Add((seconds, depot.Id));
        }
        order.Sort((a, b) => a.Seconds != b.Seconds ? a.Seconds.CompareTo(b.Seconds) : a.Depot.CompareTo(b.Depot));
        foreach (var (_, depot) in order)
        {
            int id = BuyTrain(c with { Depot = depot });
            if (id >= 0) return id;
        }
        return -1;
    }

    /// <summary>What a track would cost and tear down, null when it cannot be built (the track rules, or a level crossing that the roads do not allow).</summary>
    public readonly record struct TrackQuote(TrackCost Cost, long Price, bool Affordable, int Crossings, int Demolished, long DemolitionPrice);

    public TrackQuote? QuoteTrack(float x0, float z0, float x1, float z1, float sweep = 0f)
    {
        SyncRail();
        var plan = _rail.PlannedPoints(x0, z0, x1, z1, sweep);
        var cost = _rail.EstimateCost(x0, z0, x1, z1, sweep);
        if (plan == null || cost == null) return null;
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        if (!TrackCrossingsAllowed(geometry, plan, out int crossings)) return null;
        var inTheWay = BuildingsInTheWay(new[] { (geometry, RoadType.TwoLane) });
        var demolition = DemolitionOf(inTheWay);
        long price = Price(cost.Value.Money) + crossings * _railTuning.Crossings.Price + demolition.Price;
        return new TrackQuote(cost.Value, price, CanAfford(price), crossings, inTheWay.Count, demolition.Price);
    }

    /// <summary>Why a track cannot be built there, null when it can: the terrain and track rules first, then the road crossings.</summary>
    public string? WhyNotTrack(float x0, float z0, float x1, float z1, float sweep = 0f)
    {
        SyncRail();
        if (_rail.WhyNotTrack(x0, z0, x1, z1, sweep) is { } why) return why;
        var plan = _rail.PlannedPoints(x0, z0, x1, z1, sweep);
        if (plan == null) return "it cannot be laid there";
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        if (!TrackCrossingsAllowed(geometry, plan, out _))
            return $"it would cross a road too near the road's end or a junction (at least {CrossingRules.MinFromNode:0} m) or at too sharp an angle";
        return null;
    }

    int SellTrain(int id)
    {
        if (id < 0 || id >= _trains.Count || !_trains[id].Active) return -1;
        RemoveTrain(_trains[id], refund: true);
        return id;
    }

    void RemoveTrain(Train train, bool refund)
    {
        if (refund)
        {
            var loco = _railTuning.Locomotives[train.Locomotive];
            var wagon = _railTuning.Wagons[train.Wagon];
            Earn((long)MathF.Round((loco.Price + train.Wagons * wagon.Price) * _economy.SellRefundShare), LedgerEntry.Vehicles);
        }
        for (int h = train.Holds.Count - 1; h >= 0; h--) ReleaseHold(train, h);
        if (train.Riders != null)
        {
            foreach (var lot in train.Riders) GiveUp(_groups[lot.Group], lot.Count, lot.From);
            train.Riders = null;
        }
        train.Active = false;
        train.Speed = 0f;
        train.Load = 0;
        train.LoadCargo = -1;
        _transitVersion++;
    }

    void AddHold(Train train, bool isNode, int id, float lo, float hi)
    {
        for (int h = 0; h < train.Holds.Count; h++)
        {
            var hold = train.Holds[h];
            if (hold.IsNode != isNode || hold.Id != id) continue;
            hold.Lo = MathF.Min(hold.Lo, lo);
            hold.Hi = MathF.Max(hold.Hi, hi);
            train.Holds[h] = hold;
            return;
        }
        train.Holds.Add(new Hold { IsNode = isNode, Id = id, Lo = lo, Hi = hi });
        if (isNode) _nodeOwner[id] = train.Id;
        else _trackOwner[id] = train.Id;
    }

    ulong RailHash()
    {
        ulong h = (ulong)_rail.Version;
        h = h * 31 + (ulong)_rail.TrackCount;
        foreach (var depot in _railDepots) h = h * 31 + (ulong)depot.Node + (depot.Active ? 1UL : 0UL);
        foreach (var train in _trains)
        {
            h = h * 31 + (ulong)train.Id + (train.Active ? 7UL : 0UL);
            h = h * 31 + (ulong)train.Line;
            h = h * 31 + (ulong)train.StopIndex;
            h = h * 31 + (ulong)train.Dwell;
            h = h * 31 + (ulong)train.Load;
            h = h * 31 + (ulong)train.Status;
            h = h * 31 + BitConverter.SingleToUInt32Bits(train.Head);
            h = h * 31 + BitConverter.SingleToUInt32Bits(train.Speed);
            h = h * 31 + BitConverter.SingleToUInt32Bits(train.Approved);
            h = h * 31 + (ulong)train.Steps.Count;
            h = h * 31 + (ulong)train.Income;
            h = h * 31 + (ulong)train.UpkeepPaid;
            if (train.InDepot) h = h * 31 + 3UL;
            if (train.ReturnDepot >= 0) h = h * 31 + (ulong)(train.ReturnDepot + 5);
        }
        foreach (var o in _trackOwner) h = h * 31 + (ulong)(o + 1);
        h = h * 31 + CrossingHash();
        return h;
    }
}
