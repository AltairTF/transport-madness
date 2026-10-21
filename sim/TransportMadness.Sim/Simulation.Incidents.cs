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
    /// <summary>Two vehicles on crossing or merging paths met inside a junction.</summary>
    Junction,
    /// <summary>A train ran into a vehicle on a level crossing.</summary>
    LevelCrossing,
    /// <summary>A bomb left a crater in the road.</summary>
    Bombing,
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
    /// <summary>When the crew set off (for drawing it on its way).</summary>
    public long DispatchTick { get; internal set; }
    public long CrewArrivalTick { get; internal set; }
    public long ClearedTick { get; internal set; }
    /// <summary>The wreck is a company vehicle that broke down from low Condition: after clearing it is towed to a depot's garage instead of being scrapped.</summary>
    public bool FromCondition { get; internal set; }
    /// <summary>A breakdown of a vehicle that was not maintained (below the critical Condition): negligence, which is fined.</summary>
    public bool Negligent { get; internal set; }
    /// <summary>A vehicle or train of the company is part of it.</summary>
    public bool CompanyInvolved { get; internal set; }
    internal bool TownPenalised;
    internal bool BlockedPenalised;
    /// <summary>Wrecks and debris that still need clearing, by vehicle id.</summary>
    public IReadOnlyList<int> VehicleIds => Vehicles;
}

public sealed class Depot
{
    internal Depot(int id, int node, DepotLevel level, int crews)
    {
        Id = id;
        Node = node;
        Level = level;
        Crews = crews;
    }

    public int Id { get; }
    /// <summary>The node it stands at, or the last one it stood at; -1 for a free-standing depot no road has reached yet. Follows the node when that is moved.</summary>
    public int Node { get; internal set; }
    /// <summary>A depot built at a point with an access point of its own (M6f) rather than on a node: it is connected, and active, while a road reaches that point.</summary>
    public bool FreeStanding { get; internal set; }
    /// <summary>Where a free-standing depot's middle is, which way its door faces (radians), and its access point.</summary>
    public float X { get; internal set; }
    public float Z { get; internal set; }
    public float Angle { get; internal set; }
    public float AccessX { get; internal set; }
    public float AccessZ { get; internal set; }
    /// <summary>What the depot can repair and how many bays it has; raised in place by an upgrade.</summary>
    public DepotLevel Level { get; internal set; }
    public int Crews { get; internal set; }
    public int BusyCrews { get; internal set; }
    /// <summary>False while no road reaches its node (it was torn down, or a free-standing depot has none yet): the depot sends no more crews and takes no vehicles. It is active again when a road reaches the node.</summary>
    public bool Active { get; internal set; } = true;
    /// <summary>The tick until which a raid keeps it shut, 0 when it is not (C12): it sends no crew, repairs nothing and sells and buys nothing meanwhile.</summary>
    public long ShutUntil { get; internal set; }
}

public sealed partial class Simulation
{
    // The incidents still remembered, in id order; ids count from the first ever made, so one is at id - _incidentBase. Cleared
    // incidents are dropped from the front, in batches, once more than crews.incidentsKept (plus a few) are held (never one that is still open, so a
    // very old open incident holds back the ones after it); what was dropped stays in the state hash through _droppedHash.
    readonly List<Incident> _incidents = new();
    int _incidentBase;
    ulong _droppedHash;
    readonly List<int> _openIncidents = new(); // not yet cleared, in id order
    readonly List<(int Vehicle, JunctionPose Pose)> _inJunction = new();
    readonly List<Depot> _depots = new();
    readonly List<(int Lane, float Position, int Incident)> _pendingDebris = new();

    /// <summary>The incidents still remembered, oldest first: every open one and the last cleared ones (<c>crews.incidentsKept</c>).</summary>
    public IReadOnlyList<Incident> Incidents => _incidents;
    /// <summary>How many incidents there have been in all, dropped ones included.</summary>
    public int IncidentCount => _incidentBase + _incidents.Count;
    /// <summary>The incident with this id; it must be open or among the last cleared ones.</summary>
    public Incident GetIncident(int id) => _incidents[id - _incidentBase];
    /// <summary>Ids of incidents that are not cleared yet, oldest first.</summary>
    public IReadOnlyList<int> OpenIncidentIds => _openIncidents;
    public IReadOnlyList<Depot> Depots => _depots;

    /// <summary>The node a depot clicked for at (x, z) would stand on: the nearest with a road within <c>crews.depotSnapRadius</c>, -1 when there is none. (<see cref="AddDepotCommand"/> refuses a node that has a depot.)</summary>
    public int FindDepotNode(float x, float z) => _net.FindNodeNear(x, z, _crew.DepotSnapRadius, withRoadsOnly: true);

    /// <summary>Places a depot at a node. Its crews respond to incidents; closer depots respond faster.</summary>
    public int AddDepot(int node, DepotLevel level)
    {
        _depots.Add(new Depot(_depots.Count, node, level, _crew.PerDepot.For(level)));
        return _depots.Count - 1;
    }

    /// <summary>
    /// Finds out which depots are connected: one has a road at its node (a free-standing one at its access point). A depot that lost every road is
    /// not connected: a crew already on its way is lost with it, and the incident falls back to the distant service, counted from now. It is connected
    /// again, and active, when a road reaches the node.
    /// </summary>
    void UpdateDepotConnections()
    {
        foreach (var depot in _depots)
        {
            int node = depot.FreeStanding
                ? _net.FindNodeNear(depot.AccessX, depot.AccessZ, AccessOnNode, withRoadsOnly: true)
                : depot.Node >= 0 && _net.RoadsAt(depot.Node).Count > 0 ? depot.Node : -1;
            if (node >= 0)
            {
                depot.Node = node;
                depot.Active = true;
                continue;
            }
            if (!depot.Active) continue;
            depot.Active = false;
            depot.BusyCrews = 0;
            foreach (int k in _openIncidents)
            {
                var incident = GetIncident(k);
                if (incident.Depot != depot.Id || incident.State != IncidentState.CrewEnRoute) continue;
                incident.Depot = -1;
                incident.CrewArrivalTick = Tick + Ticks(_crew.FallbackResponse);
            }
        }
    }

    /// <summary>Makes a vehicle break down or crash where it stands.</summary>
    public void Crash(int vehicle)
    {
        if (_kind[vehicle] != VehicleKind.Car) return;
        int incident = NewIncident(IncidentKind.Breakdown, vehicle);
        MakeWreck(vehicle, incident);
    }

    static ulong MixIncident(ulong h, Incident incident)
    {
        h = h * 31 + (ulong)incident.Kind;
        h = h * 31 + BitConverter.SingleToUInt32Bits(incident.Position);
        h = h * 31 + (ulong)(incident.Depot + 1);
        h = h * 31 + (ulong)incident.State;
        h = h * 31 + (ulong)incident.CrewArrivalTick;
        h = h * 31 + (ulong)incident.ClearedTick;
        return h;
    }

    ulong IncidentHash()
    {
        ulong h = (ulong)IncidentCount;
        if (_incidentBase > 0) h = h * 31 + _droppedHash; // runs that never dropped one keep their hash
        foreach (var incident in _incidents) h = MixIncident(h, incident);
        foreach (var depot in _depots)
        {
            h = h * 31 + (ulong)depot.BusyCrews;
            if (!depot.Active) h = h * 31 + 1; // active depots add nothing, so runs without closures keep their hash
        }
        return h;
    }

    int NewIncident(IncidentKind kind, int vehicle)
    {
        int id = IncidentCount;
        _incidents.Add(new Incident(id, kind, Tick, new LaneId(_lane[vehicle]), _pos[vehicle]));
        _openIncidents.Add(id);
        return id;
    }

    /// <summary>An incident with no vehicle of its own at a place on a lane: debris is put there and a crew will clear it.</summary>
    int NewIncidentAt(IncidentKind kind, int lane, float position)
    {
        int id = IncidentCount;
        _incidents.Add(new Incident(id, kind, Tick, new LaneId(lane), position));
        _openIncidents.Add(id);
        _pendingDebris.Add((lane, position, id));
        return id;
    }

    void MakeWreck(int vehicle, int incident)
    {
        _kind[vehicle] = VehicleKind.Wreck;
        _halted[vehicle] = true;
        _speed[vehicle] = 0f;
        _accel[vehicle] = 0f;
        _incident[vehicle] = incident;
        GetIncident(incident).Vehicles.Add(vehicle);
        Block(vehicle);
    }

    /// <summary>Counts the wreck against the lane it physically blocks, so drivers can steer clear of the road.</summary>
    void Block(int vehicle)
    {
        int lane = _lane[vehicle];
        if (_lat[vehicle] >= HomeLaneLat)
        {
            if (_laneOncoming[lane] >= 0) lane = _laneOncoming[lane];
        }
        _blockLane[vehicle] = lane;
        _blockedCount[lane]++;
    }

    /// <summary>Takes a vehicle out of the world (for tests that need an empty road after driving on it).</summary>
    internal void RemoveVehicleForTest(int vehicle)
    {
        RemoveVehicle(vehicle);
        RebuildOrder();
    }

    void RemoveVehicle(int vehicle)
    {
        if (_servicing[vehicle]) CancelJobsFor(RepairSubject.Vehicle, vehicle);
        LeaveBay(vehicle);
        ReleaseTripSlot(vehicle);
        if (_blockLane[vehicle] >= 0) _blockedCount[_blockLane[vehicle]]--;
        _blockLane[vehicle] = -1;
        for (int i = 0; i < _count; i++)
            if (_target[i] == vehicle) _target[i] = -1; // the slot will be reused
        _kind[vehicle] = VehicleKind.Removed;
        _line[vehicle] = -1;
        _dwell[vehicle] = 0;
        _load[vehicle] = 0;
        _lane[vehicle] = -1;
        _halted[vehicle] = true;
        _ghost[vehicle] = false;
        _ghostOut[vehicle] = false;
        _returnDepot[vehicle] = -1;
        _servicing[vehicle] = false;
        _svcKind[vehicle] = -1;
        _svcDepot[vehicle] = -1;
        _wantRepair[vehicle] = -1;
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
        DetectJunctionCollisions();
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
                if (follower >= 0 && _pos[j] - _length[j] < _pos[follower])
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
                // Only a vehicle that came by the follower's own lane is in front of it: one that merged in from another
                // street is beside it, and a long body there is no overlap (the junction check covers real ones).
                if (_prevLane[j] == lane && GapAcrossJunction(lane, next, _pos[follower], _pos[j], _length[j]) < 0f)
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
            float rear = _pos[o] - _length[o], front = _pos[o];

            for (int r = _laneStart[lane]; r < _laneStart[lane + 1]; r++)
            {
                int j = _order[r];
                if (j == o) continue;
                if (BoxesOverlap(rear, front, y, _pos[j] - _length[j], _pos[j], LaneWidth * _lat[j]))
                    Collide(o, j, IncidentKind.Sideswipe);
            }

            int oncoming = _laneOncoming[lane];
            if (oncoming < 0) continue;
            float length = _laneLength[lane];
            for (int r = _laneStart[oncoming]; r < _laneStart[oncoming + 1]; r++)
            {
                int j = _order[r];
                float jRear = length - _pos[j], jFront = jRear + _length[j];
                if (BoxesOverlap(rear, front, y, jRear, jFront, LaneWidth * (1f - _lat[j])))
                    Collide(o, j, IncidentKind.HeadOn);
            }
        }
    }

    /// <summary>
    /// Vehicles inside a junction are checked as rotated boxes against each other, on the paths they are driving. Two on
    /// the same path (one behind the other) are the following check's business.
    /// </summary>
    void DetectJunctionCollisions()
    {
        _inJunction.Clear();
        for (int i = 0; i < _count; i++)
        {
            int lane = _lane[i];
            if (lane < 0 || _ghost[i] || _lat[i] >= HomeLaneLat) continue;
            float centre = _pos[i] - _length[i] * 0.5f;
            bool inTail = centre > _laneZoneStart[lane];
            if (!inTail && !(centre < _laneReach[lane] && _prevLane[i] >= 0)) continue;
            if (JunctionPoseOf(i, centre) is not { } pose) continue;
            _inJunction.Add((i, pose));
        }
        for (int a = 0; a < _inJunction.Count; a++)
            for (int b = a + 1; b < _inJunction.Count; b++)
            {
                var (i, p) = _inJunction[a];
                var (j, q) = _inJunction[b];
                if (p.From == q.From && p.To == q.To) continue;
                if (_kind[i] != VehicleKind.Car && _kind[j] != VehicleKind.Car) continue;
                float dx = q.X - p.X, dz = q.Z - p.Z;
                float reach = 0.5f * (_length[i] + _length[j]);
                if (dx * dx + dz * dz > reach * reach) continue;
                if (OrientedBoxesOverlap(p.X, p.Z, p.Heading, _length[i], q.X, q.Z, q.Heading, _length[j])) Collide(i, j, IncidentKind.Junction);
            }
    }

    /// <summary>Separating axis test for two vehicle-sized boxes given by centre and heading.</summary>
    static bool OrientedBoxesOverlap(float ax, float az, float ha, float la, float bx, float bz, float hb, float lb)
    {
        float dx = bx - ax, dz = bz - az;
        Span<float> axes = stackalloc float[4] { ha, ha + MathF.PI * 0.5f, hb, hb + MathF.PI * 0.5f };
        for (int k = 0; k < 4; k++)
        {
            float ux = MathF.Cos(axes[k]), uz = MathF.Sin(axes[k]);
            float ra = Extent(ha, la, ux, uz), rb = Extent(hb, lb, ux, uz);
            if (MathF.Abs(dx * ux + dz * uz) > ra + rb) return false;
        }
        return true;
    }

    /// <summary>Half the width of a vehicle box, heading <paramref name="heading"/>, projected on the axis (ux, uz).</summary>
    static float Extent(float heading, float length, float ux, float uz)
    {
        float fx = MathF.Cos(heading), fz = MathF.Sin(heading);
        return MathF.Abs(fx * ux + fz * uz) * length * 0.5f + MathF.Abs(-fz * ux + fx * uz) * VehicleWidth * 0.5f;
    }

    static bool BoxesOverlap(float rearA, float frontA, float yA, float rearB, float frontB, float yB) =>
        rearA < frontB && rearB < frontA && MathF.Abs(yA - yB) < VehicleWidth;

    /// <summary>Wrecks every moving vehicle involved; one that runs into an existing wreck joins that incident.</summary>
    internal void Collide(int a, int b, IncidentKind kind)
    {
        bool aMoving = _kind[a] == VehicleKind.Car, bMoving = _kind[b] == VehicleKind.Car;
        if (!aMoving && !bMoving) return;

        int incident = _incident[a] >= 0 ? _incident[a] : _incident[b];
        if (incident < 0) incident = NewIncident(kind, a);
        // A car piling into a wreck the crew is already working on adds to the job.
        if (GetIncident(incident).State == IncidentState.Clearing) GetIncident(incident).CrewArrivalTick = Tick;
        if (aMoving) MakeWreck(a, incident);
        if (bMoving) MakeWreck(b, incident);
        if (aMoving && _line[a] >= 0) OnCompanyCrash(a, incident);
        if (bMoving && _line[b] >= 0) OnCompanyCrash(b, incident);

        // An overtaker wrecked out in the oncoming lane also leaves wreckage across its own lane.
        if (kind == IncidentKind.HeadOn && _lat[a] >= HomeLaneLat && aMoving)
            _pendingDebris.Add((_lane[a], _pos[a], incident));
    }

    void SpawnDebris(int lane, float position, int incident)
    {
        for (int r = _laneStart[lane]; r < _laneStart[lane + 1]; r++)
        {
            int j = _order[r];
            if (_lat[j] < HomeLaneLat && MathF.Abs(_pos[j] - position) < _length[j]) return; // already blocked by a vehicle
        }
        int id = SpawnVehicle(new LaneId(lane), position, 0f);
        _kind[id] = VehicleKind.Debris;
        _halted[id] = true;
        _incident[id] = incident;
        GetIncident(incident).Vehicles.Add(id);
        Block(id);
    }

    // ---- Rescue crews -------------------------------------------------------------------------------------------

    void UpdateCrews()
    {
        if (_openIncidents.Count > 0) CheckBlockedIncidents();
        for (int k = 0; k < _openIncidents.Count; k++)
        {
            var incident = GetIncident(_openIncidents[k]);
            switch (incident.State)
            {
                case IncidentState.AwaitingCrew:
                    Dispatch(incident);
                    break;
                case IncidentState.CrewEnRoute when Tick >= incident.CrewArrivalTick:
                    incident.State = IncidentState.Clearing;
                    break;
                case IncidentState.Clearing when Tick >= incident.CrewArrivalTick + Ticks(_crew.ClearDuration):
                    Clear(incident);
                    _openIncidents.RemoveAt(k--);
                    break;
            }
        }
    }

    void Dispatch(Incident incident)
    {
        if (_depots.Count == 0)
        {
            incident.State = IncidentState.CrewEnRoute;
            incident.DispatchTick = Tick;
            incident.CrewArrivalTick = Tick + Ticks(_crew.FallbackResponse);
            return;
        }

        var (x, z) = IncidentPoint(incident);
        Depot? best = null;
        float bestDistance = float.MaxValue;
        foreach (var depot in _depots)
        {
            if (!depot.Active || IsShut(depot) || depot.BusyCrews >= depot.Crews) continue;
            var (dx, dz) = _net.GetNode(depot.Node);
            float distance = MathF.Sqrt((dx - x) * (dx - x) + (dz - z) * (dz - z));
            if (distance < bestDistance) { best = depot; bestDistance = distance; }
        }
        if (best == null)
        {
            // Every crew is busy. Keep waiting, but the distant service will not leave the road blocked forever.
            if (Tick - incident.StartTick >= Ticks(_crew.FallbackResponse))
            {
                incident.State = IncidentState.CrewEnRoute;
                incident.DispatchTick = Tick;
                incident.CrewArrivalTick = Tick;
            }
            return;
        }

        float response = _crew.DispatchDelay + bestDistance * _crew.RoadDetour / _crew.Speed;
        incident.State = IncidentState.CrewEnRoute;
        incident.DispatchTick = Tick;
        if (response >= _crew.FallbackResponse)
        {
            incident.CrewArrivalTick = Tick + Ticks(_crew.FallbackResponse); // the distant service gets there first
            return;
        }
        best.BusyCrews++;
        incident.Depot = best.Id;
        incident.CrewArrivalTick = Tick + Ticks(response);
    }

    (float X, float Z) IncidentPoint(Incident incident)
    {
        var (x, z, _) = _net.LanePoint(incident.Lane, incident.Position);
        return (x, z);
    }

    void Clear(Incident incident)
    {
        foreach (int v in incident.Vehicles)
        {
            // A company vehicle that broke down is towed to a depot's garage; other wrecks are scrapped.
            if (incident.FromCondition && _kind[v] == VehicleKind.Wreck && _line[v] >= 0 && TowToGarage(v)) continue;
            RemoveVehicle(v);
        }
        incident.Vehicles.Clear();
        incident.State = IncidentState.Cleared;
        incident.ClearedTick = Tick;
        if (incident.Depot >= 0 && _depots[incident.Depot].Active) _depots[incident.Depot].BusyCrews--;
        RebuildOrder();
        DropOldIncidents();
    }

    /// <summary>Forgets the oldest cleared incidents beyond <c>crews.incidentsKept</c>, folding them into the state hash. Done in batches so the list is not shifted for each one.</summary>
    void DropOldIncidents()
    {
        int keep = _crew.IncidentsKept;
        if (_incidents.Count <= keep + Math.Max(16, keep / 4)) return;
        int drop = 0;
        while (_incidents.Count - drop > keep && _incidents[drop].State == IncidentState.Cleared)
            _droppedHash = MixIncident(_droppedHash, _incidents[drop++]);
        if (drop == 0) return;
        _incidents.RemoveRange(0, drop);
        _incidentBase += drop;
    }

    static long Ticks(float seconds) => (long)MathF.Ceiling(seconds / StepSeconds);
}
