namespace TransportMadness.Sim;

public enum IncidentKind
{
    /// <summary>A vehicle broke down or crashed on its own.</summary>
    Breakdown,
    /// <summary>A vehicle ran into the back of another one.</summary>
    RearEnd,
    /// <summary>An overtaker met oncoming traffic in the oncoming lane.</summary>
    HeadOn,
    /// <summary>An overtaker hit a vehicle in its own lane while pulling out or in.</summary>
    Sideswipe,
}

public enum IncidentState
{
    /// <summary>No crew free yet.</summary>
    AwaitingCrew,
    CrewEnRoute,
    Clearing,
    Cleared,
}

public enum DepotLevel { Simple, Workshop, Complex }

/// <summary>A crash site: its wrecks block the road until a rescue crew has cleared them.</summary>
public sealed class Incident
{
    internal readonly List<int> Vehicles = new();

    internal Incident(int id, IncidentKind kind, long tick, LaneId lane, float position)
    {
        Id = id;
        Kind = kind;
        StartTick = tick;
        Lane = lane;
        Position = position;
    }

    public int Id { get; }
    public IncidentKind Kind { get; }
    public long StartTick { get; }
    public LaneId Lane { get; }
    public float Position { get; }
    public IncidentState State { get; internal set; } = IncidentState.AwaitingCrew;
    /// <summary>Depot the crew came from, or -1 for the distant fallback service.</summary>
    public int Depot { get; internal set; } = -1;
    public long CrewArrivalTick { get; internal set; }
    public long ClearedTick { get; internal set; }
    /// <summary>Wrecks and debris that still need clearing, by vehicle id.</summary>
    public IReadOnlyList<int> VehicleIds => Vehicles;
}

public sealed class Depot
{
    internal Depot(int id, int node, DepotLevel level)
    {
        Id = id;
        Node = node;
        Level = level;
        Crews = level == DepotLevel.Complex ? 2 : 1;
    }

    public int Id { get; }
    public int Node { get; }
    public DepotLevel Level { get; }
    public int Crews { get; }
    public int BusyCrews { get; internal set; }
}

public sealed partial class Simulation
{
    // Crew timings in seconds (tuning; will move to data files).
    const float DispatchDelay = 10f;
    const float CrewSpeed = 16f;          // metres per second, by road
    const float RoadDetour = 1.4f;        // road distance over straight-line distance
    const float FallbackResponse = 300f;  // no depot at all: help comes from far away
    const float ClearDuration = 45f;

    readonly List<Incident> _incidents = new();
    readonly List<Depot> _depots = new();
    readonly List<(int Lane, float Position, int Incident)> _pendingDebris = new();

    public IReadOnlyList<Incident> Incidents => _incidents;
    public IReadOnlyList<Depot> Depots => _depots;

    /// <summary>Places a depot at a node. Its crews respond to incidents; closer depots respond faster.</summary>
    public int AddDepot(int node, DepotLevel level)
    {
        _depots.Add(new Depot(_depots.Count, node, level));
        return _depots.Count - 1;
    }

    /// <summary>Makes a vehicle break down or crash where it stands.</summary>
    public void Crash(int vehicle)
    {
        if (_kind[vehicle] != VehicleKind.Car) return;
        int incident = NewIncident(IncidentKind.Breakdown, vehicle);
        MakeWreck(vehicle, incident);
    }

    ulong IncidentHash()
    {
        ulong h = (ulong)_incidents.Count;
        foreach (var incident in _incidents)
        {
            h = h * 31 + (ulong)incident.State;
            h = h * 31 + (ulong)incident.CrewArrivalTick;
            h = h * 31 + (ulong)incident.ClearedTick;
        }
        foreach (var depot in _depots) h = h * 31 + (ulong)depot.BusyCrews;
        return h;
    }

    int NewIncident(IncidentKind kind, int vehicle)
    {
        _incidents.Add(new Incident(_incidents.Count, kind, Tick, new LaneId(_lane[vehicle]), _pos[vehicle]));
        return _incidents.Count - 1;
    }

    void MakeWreck(int vehicle, int incident)
    {
        _kind[vehicle] = VehicleKind.Wreck;
        _halted[vehicle] = true;
        _speed[vehicle] = 0f;
        _accel[vehicle] = 0f;
        _incident[vehicle] = incident;
        _incidents[incident].Vehicles.Add(vehicle);
    }

    void RemoveVehicle(int vehicle)
    {
        _kind[vehicle] = VehicleKind.Removed;
        _lane[vehicle] = -1;
        _halted[vehicle] = true;
        _speed[vehicle] = 0f;
        _phase[vehicle] = 0;
        _lat[vehicle] = 0f;
        _target[vehicle] = -1;
        _incident[vehicle] = -1;
        _freeSlots.Push(vehicle);
        _active--;
    }

    // ---- Detection ------------------------------------------------------------------------------------------------

    void DetectIncidents()
    {
        DetectFollowingCollisions();
        DetectOvertakeCollisions();
        foreach (var (lane, position, incident) in _pendingDebris) SpawnDebris(lane, position, incident);
        _pendingDebris.Clear();
        UpdateCrews();
    }

    /// <summary>Box overlap between a vehicle and the one in front of it, in its lane or across the lane end.</summary>
    void DetectFollowingCollisions()
    {
        for (int lane = 0; lane < _net.LaneCount; lane++)
        {
            int start = _laneStart[lane], end = _laneStart[lane + 1];
            int follower = -1;
            for (int r = start; r < end; r++)
            {
                int j = _order[r];
                if (_lat[j] >= HomeLaneLat) continue;
                if (follower >= 0 && _pos[j] - VehicleLength < _pos[follower])
                    Collide(follower, j, IncidentKind.RearEnd);
                follower = j;
            }
            if (follower < 0) continue;

            // The last vehicle may be overlapping the first one of the lane it is about to enter.
            int next = _nextLane[follower];
            for (int r = _laneStart[next]; r < _laneStart[next + 1]; r++)
            {
                int j = _order[r];
                if (_lat[j] >= HomeLaneLat) continue;
                if (_laneLength[lane] - _pos[follower] + _pos[j] - VehicleLength < 0f)
                    Collide(follower, j, IncidentKind.RearEnd);
                break;
            }
        }
    }

    /// <summary>
    /// A vehicle swinging out is checked as a box against everything in its own lane and in the oncoming lane,
    /// the latter mapped into its frame of reference (positions there run the other way).
    /// </summary>
    void DetectOvertakeCollisions()
    {
        foreach (int o in _overtakers)
        {
            if (_kind[o] == VehicleKind.Removed) continue;
            int lane = _lane[o];
            float y = LaneWidth * _lat[o];
            float rear = _pos[o] - VehicleLength, front = _pos[o];

            for (int r = _laneStart[lane]; r < _laneStart[lane + 1]; r++)
            {
                int j = _order[r];
                if (j == o) continue;
                if (BoxesOverlap(rear, front, y, _pos[j] - VehicleLength, _pos[j], LaneWidth * _lat[j]))
                    Collide(o, j, IncidentKind.Sideswipe);
            }

            var oncoming = _net.GetLane(new LaneId(lane)).Oncoming;
            if (oncoming.IsNone) continue;
            float length = _laneLength[lane];
            for (int r = _laneStart[oncoming.Value]; r < _laneStart[oncoming.Value + 1]; r++)
            {
                int j = _order[r];
                float jRear = length - _pos[j], jFront = jRear + VehicleLength;
                if (BoxesOverlap(rear, front, y, jRear, jFront, LaneWidth * (1f - _lat[j])))
                    Collide(o, j, IncidentKind.HeadOn);
            }
        }
    }

    static bool BoxesOverlap(float rearA, float frontA, float yA, float rearB, float frontB, float yB) =>
        rearA < frontB && rearB < frontA && MathF.Abs(yA - yB) < VehicleWidth;

    /// <summary>Wrecks every moving vehicle involved; one that runs into an existing wreck joins that incident.</summary>
    void Collide(int a, int b, IncidentKind kind)
    {
        bool aMoving = _kind[a] == VehicleKind.Car, bMoving = _kind[b] == VehicleKind.Car;
        if (!aMoving && !bMoving) return;

        int incident = _incident[a] >= 0 ? _incident[a] : _incident[b];
        if (incident < 0) incident = NewIncident(kind, a);
        if (aMoving) MakeWreck(a, incident);
        if (bMoving) MakeWreck(b, incident);

        // An overtaker wrecked out in the oncoming lane also leaves wreckage across its own lane.
        if (kind == IncidentKind.HeadOn && _lat[a] >= HomeLaneLat && aMoving)
            _pendingDebris.Add((_lane[a], _pos[a], incident));
    }

    void SpawnDebris(int lane, float position, int incident)
    {
        for (int r = _laneStart[lane]; r < _laneStart[lane + 1]; r++)
        {
            int j = _order[r];
            if (_lat[j] < HomeLaneLat && MathF.Abs(_pos[j] - position) < VehicleLength) return; // already blocked by a vehicle
        }
        int id = SpawnVehicle(new LaneId(lane), position, 0f);
        _kind[id] = VehicleKind.Debris;
        _halted[id] = true;
        _incident[id] = incident;
        _incidents[incident].Vehicles.Add(id);
    }

    // ---- Rescue crews -------------------------------------------------------------------------------------------

    void UpdateCrews()
    {
        foreach (var incident in _incidents)
        {
            switch (incident.State)
            {
                case IncidentState.AwaitingCrew:
                    Dispatch(incident);
                    break;
                case IncidentState.CrewEnRoute when Tick >= incident.CrewArrivalTick:
                    incident.State = IncidentState.Clearing;
                    break;
                case IncidentState.Clearing when Tick >= incident.CrewArrivalTick + Ticks(ClearDuration):
                    Clear(incident);
                    break;
            }
        }
    }

    void Dispatch(Incident incident)
    {
        if (_depots.Count == 0)
        {
            incident.State = IncidentState.CrewEnRoute;
            incident.CrewArrivalTick = Tick + Ticks(FallbackResponse);
            return;
        }

        var (x, z) = IncidentPoint(incident);
        Depot? best = null;
        float bestDistance = float.MaxValue;
        foreach (var depot in _depots)
        {
            if (depot.BusyCrews >= depot.Crews) continue;
            var (dx, dz) = _net.GetNode(depot.Node);
            float distance = MathF.Sqrt((dx - x) * (dx - x) + (dz - z) * (dz - z));
            if (distance < bestDistance) { best = depot; bestDistance = distance; }
        }
        if (best == null) return; // every crew is busy; try again next step

        float response = DispatchDelay + bestDistance * RoadDetour / CrewSpeed;
        incident.State = IncidentState.CrewEnRoute;
        if (response >= FallbackResponse)
        {
            incident.CrewArrivalTick = Tick + Ticks(FallbackResponse); // the distant service gets there first
            return;
        }
        best.BusyCrews++;
        incident.Depot = best.Id;
        incident.CrewArrivalTick = Tick + Ticks(response);
    }

    (float X, float Z) IncidentPoint(Incident incident)
    {
        var lane = _net.GetLane(incident.Lane);
        var (x0, z0) = _net.GetNode(lane.StartNode);
        var (x1, z1) = _net.GetNode(lane.EndNode);
        float t = Math.Clamp(incident.Position / lane.Length, 0f, 1f);
        return (x0 + (x1 - x0) * t, z0 + (z1 - z0) * t);
    }

    void Clear(Incident incident)
    {
        foreach (int v in incident.Vehicles) RemoveVehicle(v);
        incident.Vehicles.Clear();
        incident.State = IncidentState.Cleared;
        incident.ClearedTick = Tick;
        if (incident.Depot >= 0) _depots[incident.Depot].BusyCrews--;
        RebuildOrder();
    }

    static long Ticks(float seconds) => (long)MathF.Ceiling(seconds / StepSeconds);
}
