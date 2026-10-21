namespace TransportMadness.Sim;

/// <summary>Where a vehicle is in an overtaking manoeuvre on the oncoming lane.</summary>
public enum OvertakePhase : byte { None, MovingOut, Passing, Returning }

public enum VehicleKind : byte
{
    Car,
    /// <summary>Crashed or broken-down vehicle: stationary, blocks the road until a rescue crew clears it.</summary>
    Wreck,
    /// <summary>Wreckage spread into the lane next to a head-on collision.</summary>
    Debris,
    /// <summary>Slot of a vehicle that no longer exists (ids are reused).</summary>
    Removed,
}

/// <param name="Lateral">0 in the vehicle's own lane, 1 fully in the oncoming lane.</param>
/// <param name="Shift">Metres to the right of its lane's centre while settling after a lane change; fades to 0.</param>
public readonly record struct VehicleState(LaneId Lane, float Position, float Speed, float Lateral, OvertakePhase Overtake, VehicleKind Kind, float Shift);

/// <summary>
/// Deterministic fixed-step traffic simulation on a <see cref="RoadNetwork"/> (ADR 0002, 0003).
/// Position is the front bumper's distance along the vehicle's lane.
/// </summary>
public sealed partial class Simulation
{
    public const float StepSeconds = 0.05f;
    public const float VehicleLength = 4.5f;
    public const float VehicleWidth = 1.9f;
    public const float LaneWidth = RoadNetwork.LaneWidth;
    const float TurnLookahead = 150f; // metres before a junction at which a vehicle starts to consider the turn

    readonly RoadNetwork _net;
    // Tuning comes from the network (data/tuning.json); the sections are cached because the hot loops read them.
    readonly DrivingTuning _drive;
    readonly TrafficTuning _traffic;
    readonly ManoeuvreTuning _manoeuvre;
    readonly LaneChangeTuning _laneChange;
    readonly OvertakeTuning _overtake;
    readonly CrewTuning _crew;
    readonly float _twoSqrtAB; // 2 * sqrt(maxAccel * comfortDecel), the IDM braking term's denominator
    readonly float _laneChangeSeconds; // how long the sideways move of a lane change takes
    int _syncedNetworkVersion = -1;
    readonly SimRandom _rng;

    // Per-lane constants copied out of the network so the hot loop stays on flat arrays.
    float[] _laneLength = Array.Empty<float>();
    float[] _laneLimit = Array.Empty<float>();
    int[] _laneEndNode = Array.Empty<int>();
    int[] _laneRoad = Array.Empty<int>();
    // Junctions are driven through: a lane has a tail past its end, the path through the junction, that starts
    // _laneZoneStart metres along it and is _moveLength long for the chosen next lane (see ZoneEnd).
    float[] _laneZoneStart = Array.Empty<float>();
    float[] _laneReach = Array.Empty<float>(); // how far into this lane a vehicle comes out of a junction
    float[] _moveLength = Array.Empty<float>(); // per move (see _nextLanes): length of its junction path
    float[] _moveLimit = Array.Empty<float>(); // per move: the speed its turn allows, infinite when it is straight
    // Neighbouring lanes, -1 for none.
    int[] _laneOncoming = Array.Empty<int>();
    int[] _laneLeft = Array.Empty<int>();
    int[] _laneRight = Array.Empty<int>();
    // Lanes reachable at the end of each lane: _nextLanes[_nextStart[l] .. _nextStart[l + 1]).
    int[] _nextStart = new int[1];
    int[] _nextLanes = Array.Empty<int>();
    // Conflicts between the moves at each node; see SyncConflictTables.
    int[] _moveLocal = Array.Empty<int>(); // per move: its number among the moves at its node, -1 for a retired lane
    int[] _nodeMoveCount = Array.Empty<int>();
    int[] _nodeMatrixStart = new int[1];
    byte[] _conflictTable = Array.Empty<byte>(); // 0 unknown, 1 no conflict, 2 conflict
    float[] _zoneIn = Array.Empty<float>();      // for a conflict: the stretch of the first move's path where it meets the second's
    float[] _zoneOut = Array.Empty<float>();
    // The same for pairs in which one vehicle is longer than a car.
    byte[] _wideConflictTable = Array.Empty<byte>();
    float[] _wideZoneIn = Array.Empty<float>();
    float[] _wideZoneOut = Array.Empty<float>();

    int _count; // slots in use, including removed ones
    int _active; // vehicles that currently exist
    int _orderCount; // vehicles in the lane ordering: all but those standing in a station's bay
    readonly Stack<int> _freeSlots = new();
    int[] _lane = new int[64];
    float[] _pos = new float[64];
    float[] _speed = new float[64];
    float[] _desired = new float[64]; // absolute speed cap on top of the lane's speed limit
    float[] _accel = new float[64];
    bool[] _halted = new bool[64];
    int[] _nextLane = new int[64]; // lane this vehicle will enter at the end of its current one
    int[] _prevLane = new int[64]; // lane it came from at the last junction, -1 if none; only used to draw the turn, so not part of the state hash
    float[] _factor = new float[64]; // personal fraction of the lane's speed limit
    float[] _patience = new float[64];
    float[] _aggression = new float[64];
    float[] _attention = new float[64];
    int[] _cooldown = new int[64]; // ticks before the next lane change or overtake
    float[] _stuck = new float[64]; // seconds held up by a slower leader
    float[] _stall = new float[64]; // seconds an overtaker has stood still out in the oncoming lane
    float[] _stopHold = new float[64]; // seconds stood still at the line of a stop sign on this lane; 0 again on the next lane
    float[] _lat = new float[64]; // 0 in own lane .. 1 fully in the oncoming lane
    float[] _shift = new float[64]; // visual sideways offset left over from a lane change, metres to the right
    int[] _blockLane = new int[64]; // lane a wreck physically blocks, or -1
    int[] _blockedCount = Array.Empty<int>(); // per lane: wrecks and debris in it
    byte[] _phase = new byte[64];
    int[] _target = new int[64]; // vehicle being overtaken, or -1
    bool[] _aborting = new bool[64];
    bool[] _keepLane = new bool[64];
    VehicleKind[] _kind = new VehicleKind[64];
    int[] _incident = new int[64]; // incident this wreck belongs to, or -1
    float[] _extraGap = new float[64]; // closing obstacle formed by an oncoming overtaker: gap ...
    float[] _extraSpeed = new float[64]; // ... and its speed as seen by us (negative: coming at us)
    float[] _oncGap = new float[64]; // nearest oncoming vehicle ahead of an overtaker
    float[] _oncSpeed = new float[64];
    float[] _length = new float[64]; // vehicle length: the roster's for company vehicles, VehicleLength for ambient traffic

    // Vehicles grouped by lane, ascending position within a lane. Rebuilt each step.
    int[] _order = new int[64];
    int[] _laneStart = new int[1];
    int[] _rankInOrder = new int[64];
    int[] _cursor = Array.Empty<int>();

    readonly List<int> _overtakers = new();

    /// <summary>The network may keep growing: roads added later are picked up at the start of the next step or spawn.</summary>
    public Simulation(RoadNetwork net, ulong seed, IncidentFrequency frequency = IncidentFrequency.Normal)
    {
        _net = net;
        var tuning = net.Tuning;
        _drive = tuning.Driving;
        _traffic = tuning.Traffic;
        _manoeuvre = tuning.Manoeuvres;
        _laneChange = tuning.LaneChange;
        _overtake = tuning.Overtake;
        _crew = tuning.Crews;
        _economy = tuning.Economy;
        _stationTuning = tuning.Stations;
        _cargo = tuning.Cargo;
        _townTuning = tuning.Towns;
        _trips = tuning.Trips;
        _growth = tuning.Towns.Growth;
        _industryTuning = tuning.Industries;
        _industryOutput = tuning.Industries.Types.Select(t => CargoIndex(tuning, t.Output)).ToArray();
        _industryInputs = ResolveIndustryInputs(tuning);
        _gatewayTuning = tuning.Gateways;
        _importCargo = tuning.Gateways.Imports.Select(i => CargoIndex(tuning, i.Cargo)).ToArray();
        _exportable = new bool[tuning.Cargo.Count];
        foreach (var id in tuning.Gateways.Exports) _exportable[CargoIndex(tuning, id)] = true;
        _shipmentTicks = (long)MathF.Ceiling(tuning.Gateways.ShipmentSeconds / StepSeconds);
        _intervalTicks = (long)MathF.Ceiling(tuning.Gateways.IntervalSeconds / StepSeconds);
        _sustainNeed = ResolveNeeds(tuning, t => t.Sustain, 1f);
        _constructionCap = ResolveNeeds(tuning, t => t.Construction, 1f);
        _passengerCargo = FindCargo(tuning, "passengers");
        _rail = new RailNetwork(net.Terrain, tuning);
        _railTuning = tuning.Rail;
        _maint = tuning.Maintenance;
        _companyPolicy = new MaintenancePolicy(PolicyMode.On, _maint.DefaultPolicy.EveryDays, _maint.DefaultPolicy.BelowCondition, Enum.Parse<RepairKind>(_maint.DefaultPolicy.Repair, ignoreCase: true));
        _frequency = frequency;
        _frequencyScale = _maint.Frequency.For(frequency);
        ResolveWagonCargo();
        _models = tuning.Vehicles;
        _modelCargo = ResolveModelCargo(tuning);
        _ticksPerDay = (long)MathF.Round(_economy.DayLength / StepSeconds);
        _productionPerTick = 1.0 / (_ticksPerDay * _economy.DaysPerMonth);
        _campaign = tuning.Campaign;
        _orderTuning = tuning.Orders;
        _orderRng = new SimRandom(seed ^ OrderSeedMix);
        Company = new Company(_economy.StartingMoney) { Standing = tuning.Campaign.StartStanding };
        _twoSqrtAB = 2f * MathF.Sqrt(_drive.MaxAccel * _drive.ComfortDecel);
        _laneChangeSeconds = 1f / _manoeuvre.LateralRate;
        _rng = new SimRandom(seed);
        _tripRng = new SimRandom(seed ^ TripSeedMix);
        _growRng = new SimRandom(seed ^ GrowthSeedMix);
        SyncNetwork();
    }

    public RoadNetwork Network => _net;
    /// <summary>Number of vehicle slots ever used; valid ids are below this. Check <see cref="VehicleKind.Removed"/>.</summary>
    public int VehicleCount => _count;
    public long Tick { get; private set; }

    /// <summary>
    /// Scatters up to <paramref name="count"/> vehicles on random lanes at random free spots, with random driver
    /// traits, drawing from the simulation's RNG. Returns how many were placed (fewer when the network is full).
    /// </summary>
    public int SeedTraffic(int count)
    {
        SyncNetwork();
        if (_net.LaneCount == 0) return 0;
        float clearance = _traffic.SpawnClearance;
        int placed = 0;
        for (int attempt = 0; placed < count && attempt < count * _traffic.SpawnAttemptsPerVehicle; attempt++)
        {
            var lane = _net.GetLane(new LaneId(_rng.NextInt(_net.LaneCount)));
            if (lane.Retired || lane.Length < 2 * clearance) continue;
            float pos = clearance + _rng.NextFloat() * (lane.Length - 2 * clearance);
            if (!IsClear(lane.Id, pos, clearance)) continue;
            int id = SpawnVehicle(lane.Id, pos, lane.SpeedLimit * _traffic.SpawnSpeedFactor);
            _factor[id] = _traffic.DriverSpeedFactorMin + _traffic.DriverSpeedFactorRange * _rng.NextFloat();
            _patience[id] = _rng.NextFloat();
            _aggression[id] = _rng.NextFloat();
            _attention[id] = _rng.NextFloat();
            placed++;
        }
        return placed;
    }

    bool IsClear(LaneId lane, float pos, float clearance)
    {
        for (int r = _laneStart[lane.Value]; r < _laneStart[lane.Value + 1]; r++)
            if (MathF.Abs(_pos[_order[r]] - pos) < clearance) return false;
        return true;
    }

    /// <summary>
    /// Whether a vehicle of <paramref name="length"/> whose front would be at <paramref name="pos"/> has <paramref name="margin"/> metres
    /// free all round on the lane, counting the real length of the vehicles already there (a bus in front reaches back 10 m).
    /// </summary>
    internal bool IsClearFor(LaneId lane, float pos, float length, float margin)
    {
        for (int r = _laneStart[lane.Value]; r < _laneStart[lane.Value + 1]; r++)
        {
            int j = _order[r];
            if (_pos[j] > pos - length - margin && _pos[j] - _length[j] < pos + margin) return false;
        }
        return true;
    }

    /// <summary>FNV-1a over the complete simulation state; equal hashes mean identical runs.</summary>
    public ulong StateHash()
    {
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) { h = (h ^ v) * 1099511628211UL; }
        void MixF(float v) => Mix(BitConverter.SingleToUInt32Bits(v));
        Mix((ulong)Tick);
        Mix((ulong)_count);
        for (int i = 0; i < _count; i++)
        {
            Mix((ulong)_lane[i]);
            Mix((ulong)_kind[i]);
            Mix((ulong)(_incident[i] + 1));
            Mix((ulong)_nextLane[i]);
            MixF(_pos[i]);
            MixF(_speed[i]);
            MixF(_desired[i]);
            MixF(_factor[i]);
            MixF(_patience[i]);
            MixF(_aggression[i]);
            MixF(_attention[i]);
            MixF(_stuck[i]);
            MixF(_stall[i]);
            if (_roadSigns.Count > 0) MixF(_stopHold[i]);
            MixF(_accel[i]);
            MixF(_lat[i]);
            MixF(_shift[i]);
            Mix((ulong)_cooldown[i]);
            Mix((ulong)_phase[i]);
            Mix((ulong)(_target[i] + 1));
            Mix((_halted[i] ? 1UL : 0UL) | (_aborting[i] ? 2UL : 0UL) | (_keepLane[i] ? 4UL : 0UL) | (_ghost[i] ? 8UL : 0UL) | (_ghostOut[i] ? 16UL : 0UL));
        }
        Mix(IncidentHash());
        Mix(StationHash());
        if (_buildings.Count > 0) Mix(TownHash());
        if (_industries.Count > 0 || _buildings.Count > 0) Mix(IndustryHash());
        if (_gateways.Count > 0) Mix(GatewayHash());
        if (_gateways.Count > 0) Mix(OrderHash());
        if (_groups.Count > 0) Mix(TripHash());
        Mix(FleetHash());
        if (_lines.Count > 0 || _roadWorks.Count > 0 || _resurfaced > 0) Mix(RoadWearHash());
        if (_lights.Count > 0) Mix(LightHash());
        if (_roadSigns.Count > 0) Mix(SignHash());
        if (RailInUse) Mix(RailHash());
        Mix((ulong)Company.Money);
        Mix((ulong)Company.Loan);
        Mix(CampaignHash());
        Mix((ulong)_net.Version);
        Mix((ulong)_net.LaneCount);
        foreach (var depot in _depots)
        {
            Mix((ulong)depot.Node);
            Mix((ulong)depot.Level);
        }
        var (a, b, c, d) = _rng.State;
        Mix(a); Mix(b); Mix(c); Mix(d);
        return h;
    }

    public int SpawnVehicle(LaneId lane, float position, float speed)
    {
        SyncNetwork();
        int id;
        if (_freeSlots.Count > 0) id = _freeSlots.Pop();
        else
        {
            if (_count == _lane.Length) Grow();
            id = _count++;
        }
        _active++;
        _lane[id] = lane.Value;
        _pos[id] = position;
        _speed[id] = speed;
        _desired[id] = float.MaxValue;
        _factor[id] = 1f;
        _patience[id] = 0.5f;
        _aggression[id] = 0.5f;
        _attention[id] = 1f;
        _halted[id] = false;
        _accel[id] = 0f;
        _cooldown[id] = 0;
        _stuck[id] = 0f;
        _stall[id] = 0f;
        _stopHold[id] = 0f;
        _lat[id] = 0f;
        _shift[id] = 0f;
        _blockLane[id] = -1;
        _phase[id] = 0;
        _target[id] = -1;
        _aborting[id] = false;
        _keepLane[id] = false;
        _kind[id] = VehicleKind.Car;
        _incident[id] = -1;
        _prevLane[id] = -1;
        ResetFleetSlot(id);
        ResetConditionSlot(id);
        _nextLane[id] = ChooseNextLane(lane.Value);
        RebuildOrder();
        return id;
    }

    /// <summary>A halted vehicle (wreck, breakdown) stays put and acts as an obstacle.</summary>
    public void SetHalted(int vehicle, bool halted)
    {
        _halted[vehicle] = halted;
        if (halted) _speed[vehicle] = 0f;
    }

    public void SetDesiredSpeed(int vehicle, float speed)
    {
        if (!(speed > 0f)) throw new ArgumentOutOfRangeException(nameof(speed), "Desired speed must be positive; use SetHalted to stop a vehicle.");
        _desired[vehicle] = speed;
    }

    /// <summary>Driver traits in [0, 1]: patience (waits longer before overtaking), aggression (accepts tighter gaps), attention (sees further, misjudges less).</summary>
    public void SetDriver(int vehicle, float patience, float aggression, float attention)
    {
        _patience[vehicle] = Math.Clamp(patience, 0f, 1f);
        _aggression[vehicle] = Math.Clamp(aggression, 0f, 1f);
        _attention[vehicle] = Math.Clamp(attention, 0f, 1f);
    }

    /// <summary>A vehicle that keeps its lane never changes lanes or overtakes (scripted traffic, tests).</summary>
    public void SetKeepLane(int vehicle, bool keepLane) => _keepLane[vehicle] = keepLane;

    /// <summary>Lane the vehicle will enter at the end of its current one (for drawing the turn).</summary>
    public LaneId NextLaneOf(int id) => new(_nextLane[id]);

    /// <summary>Lane the vehicle came from at the junction it last crossed, or none (for drawing the turn).</summary>
    public LaneId PreviousLaneOf(int id) => new(_prevLane[id]);

    /// <summary>Where a vehicle is while it drives through a junction: its place on the path between two lanes.</summary>
    /// <param name="Distance">Metres along the path from where it starts, <see cref="RoadNetwork.JunctionReach"/> before the end of <paramref name="From"/>.</param>
    public readonly record struct JunctionPose(float X, float Z, float Heading, LaneId From, LaneId To, float Distance, float Length);

    /// <summary>
    /// The pose of the middle of a vehicle (<paramref name="centre"/> metres along its lane, which can be a little off
    /// the front bumper position) when that is inside a junction: on the path to its next lane once past the start of the
    /// junction, or still on the path it came by while it is less than one reach into its lane. Null on the open road.
    /// </summary>
    public JunctionPose? JunctionPoseOf(int vehicle, float centre)
    {
        int lane = _lane[vehicle];
        int from, to;
        float distance;
        if (centre > _laneZoneStart[lane])
        {
            from = lane;
            to = _nextLane[vehicle];
            distance = centre - _laneZoneStart[lane];
        }
        else if (centre < _laneReach[lane] && _prevLane[vehicle] >= 0)
        {
            from = _prevLane[vehicle];
            to = lane;
            distance = _net.JunctionLength(new LaneId(from), new LaneId(to)) - (_laneReach[lane] - centre);
        }
        else return null;
        var (x, z, heading) = _net.JunctionPointAt(new LaneId(from), new LaneId(to), distance);
        return new JunctionPose(x, z, heading, new LaneId(from), new LaneId(to), distance, _net.JunctionLength(new LaneId(from), new LaneId(to)));
    }

    public VehicleState GetVehicle(int id) =>
        new(new LaneId(_lane[id]), _pos[id], _speed[id], _lat[id], (OvertakePhase)_phase[id], _kind[id], _shift[id]);

    public void Step()
    {
        SyncNetwork();
        if (RailInUse)
        {
            SyncRail();
            UpdateCrossings();
        }
        if (_lightCount > 0) SyncLights();
        if (_signCount > 0) SyncSigns();
        DecideManoeuvres();
        PrepareOvertakes();
        GenerateCargo();
        UpdateIndustries();
        UpdateGateways();
        StepOrders();
        UpdateTrips();
        UpdateFleet();
        StepRepairs();
        StepRoadWorks();
        StepSignals();
        StepGhosts();
        for (int i = 0; i < _count; i++)
        {
            if (_halted[i] || _ghost[i]) continue;
            float v = _speed[i];
            float v0 = FreeSpeed(i);
            float ratio = v / v0;
            float interaction = 0f;

            if (FindLeader(i, out _, out float gap, out float leadSpeed))
                interaction = Interaction(v, gap, leadSpeed);
            if (_phase[i] != 0 && _lat[i] >= OncomingSightLat && _oncGap[i] < float.MaxValue)
                interaction = MathF.Max(interaction, Interaction(v, _oncGap[i], -_oncSpeed[i]));
            if (_extraGap[i] < float.MaxValue)
                interaction = MathF.Max(interaction, Interaction(v, _extraGap[i], _extraSpeed[i]));
            interaction = MathF.Max(interaction, JunctionInteraction(i, v));
            if (_line[i] >= 0) interaction = MathF.Max(interaction, StopInteraction(i, v));
            if (_zoneStart.Length > 0) interaction = MathF.Max(interaction, CrossingInteraction(i, v));

            float maxBraking = _line[i] >= 0 ? _drive.MaxBraking * BrakeShare(_condition[i]) : _drive.MaxBraking;
            float wanted = MathF.Max(-maxBraking, _drive.MaxAccel * (1f - ratio * ratio * ratio * ratio - interaction));
            _accel[i] = MathF.Max(wanted, _accel[i] - _drive.BrakeJerk * StepSeconds);
        }
        for (int i = 0; i < _count; i++)
        {
            if (_halted[i] || _ghost[i]) continue;
            _speed[i] = MathF.Max(0f, _speed[i] + _accel[i] * StepSeconds);
            _pos[i] += _speed[i] * StepSeconds;
            if (_shift[i] != 0f) _shift[i] = MathF.Sign(_shift[i]) * MathF.Max(0f, MathF.Abs(_shift[i]) - LaneWidth / _laneChangeSeconds * StepSeconds);
            AdvanceLane(i);
        }
        RebuildOrder();
        if (_trains.Count > 0)
        {
            StepTrains();
            DetectCrossingCollisions();
        }
        StepCondition();
        DetectIncidents();
        if ((Tick + 1) % _ticksPerDay == 0) CloseDay();
        Tick++;
    }

    /// <summary>Speed a turn of the given path length and total heading change allows, from its mean radius.</summary>
    float TurnLimit(float length, float turn)
    {
        if (turn < 0.05f) return float.PositiveInfinity;
        return MathF.Max(_drive.MinTurnSpeed, MathF.Sqrt(_drive.TurnLateralAcceleration * length / turn));
    }

    /// <summary>The speed a driver aims for when nothing is in the way.</summary>
    float FreeSpeed(int i)
    {
        float v0 = _laneLimit[_lane[i]] * _factor[i] * RoadSpeedShare(_lane[i]);
        // Slow down for the turn at the end of the lane: the speed it allows, rising with the distance left to brake in.
        float toTurn = _laneZoneStart[_lane[i]] - _pos[i];
        if (toTurn < TurnLookahead)
        {
            int move = MoveIndex(_lane[i], _nextLane[i]);
            if (move >= 0 && _moveLimit[move] < v0)
                v0 = MathF.Min(v0, MathF.Sqrt(_moveLimit[move] * _moveLimit[move] + 2f * _drive.ComfortDecel * MathF.Max(0f, toTurn)));
        }
        if (_phase[i] != 0) v0 *= OvertakeBoost(i);
        return MathF.Min(v0, _desired[i]);
    }

    float OvertakeBoost(int i) => _overtake.Boost + _overtake.BoostAggression * _aggression[i];

    /// <summary>IDM braking term for a leader <paramref name="gap"/> metres ahead moving at <paramref name="leadSpeed"/>.</summary>
    float Interaction(float v, float gap, float leadSpeed)
    {
        gap = MathF.Max(_drive.MinInteractionGap, gap);
        float dv = v - leadSpeed;
        float desiredGap = _drive.MinGap + MathF.Max(0f, v * _drive.TimeHeadway + v * dv / _twoSqrtAB);
        float g = desiredGap / gap;
        return g * g;
    }

    /// <summary>
    /// The vehicle directly ahead in the same lane, or failing that the rearmost one on the lane about to be entered.
    /// Vehicles that are out in the oncoming lane do not count as being in this one; a vehicle that is itself out
    /// there follows the others out there (and watches oncoming traffic separately).
    /// </summary>
    bool FindLeader(int i, out int leader, out float gap, out float leadSpeed)
    {
        leader = -1;
        gap = 0f;
        leadSpeed = 0f;
        int lane = _lane[i];

        if (_lat[i] >= HomeLaneLat)
        {
            // Out in the oncoming lane: only others out there in the same direction are in the way ...
            bool found = false;
            for (int r = _rankInOrder[i] + 1; r < _laneStart[lane + 1]; r++)
            {
                int j = _order[r];
                if (_lat[j] < HomeLaneLat) continue;
                leader = j;
                gap = _pos[j] - _length[j] - _pos[i];
                leadSpeed = _speed[j];
                found = true;
                break;
            }
            // ... and an aborting overtaker is on its way back in behind its target.
            int t = _target[i];
            if (_aborting[i] && t >= 0 && _lane[t] == lane)
            {
                float g = _pos[t] - _length[t] - _pos[i];
                if (!found || g < gap)
                {
                    leader = t;
                    gap = g;
                    leadSpeed = _speed[t];
                    found = true;
                }
            }
            return found;
        }

        int end = _laneStart[lane + 1];
        for (int r = _rankInOrder[i] + 1; r < end; r++)
        {
            int j = _order[r];
            if (_lat[j] >= HomeLaneLat) continue;
            leader = j;
            gap = _pos[j] - _length[j] - _pos[i];
            leadSpeed = _speed[j];
            return true;
        }

        int next = _nextLane[i];
        for (int r = _laneStart[next]; r < _laneStart[next + 1]; r++)
        {
            int j = _order[r];
            if (_lat[j] >= HomeLaneLat) continue;
            leader = j;
            gap = GapAcrossJunction(lane, next, _pos[i], _pos[j], _length[j]);
            leadSpeed = _speed[j];
            return true;
        }
        return false;
    }

    /// <summary>Where on <paramref name="lane"/> a vehicle bound for <paramref name="next"/> has finished crossing the junction and moves on to <paramref name="next"/>.</summary>
    float ZoneEnd(int lane, int next)
    {
        int move = MoveIndex(lane, next);
        return _laneZoneStart[lane] + (move >= 0 ? _moveLength[move] : _laneReach[lane] + _laneReach[next]);
    }

    /// <summary>Bumper to bumper distance from a vehicle at <paramref name="followerPos"/> on <paramref name="lane"/> to one at <paramref name="leaderPos"/> on <paramref name="next"/>, going through the junction.</summary>
    float GapAcrossJunction(int lane, int next, float followerPos, float leaderPos, float leaderLength) =>
        ZoneEnd(lane, next) - followerPos + leaderPos - _laneReach[next] - leaderLength;

    /// <summary>
    /// Picks up roads built since the last call. Vehicles whose planned turn no longer exists (a dead end that
    /// became a junction, say) choose again.
    /// </summary>
    void SyncNetwork()
    {
        if (_net.Version == _syncedNetworkVersion) return;
        _syncedNetworkVersion = _net.Version;

        int lanes = _net.LaneCount;
        int old = _laneLength.Length;
        Array.Resize(ref _laneLength, lanes);
        Array.Resize(ref _laneLimit, lanes);
        Array.Resize(ref _laneEndNode, lanes);
        Array.Resize(ref _laneRoad, lanes);
        Array.Resize(ref _laneZoneStart, lanes);
        Array.Resize(ref _laneReach, lanes);
        Array.Resize(ref _laneOncoming, lanes);
        Array.Resize(ref _laneLeft, lanes);
        Array.Resize(ref _laneRight, lanes);
        Array.Resize(ref _blockedCount, lanes);
        _laneStart = new int[lanes + 1];
        _cursor = new int[lanes];
        for (int l = old; l < lanes; l++)
        {
            var lane = _net.GetLane(new LaneId(l));
            _laneLength[l] = lane.Length;
            _laneLimit[l] = lane.SpeedLimit;
            _laneEndNode[l] = lane.EndNode;
            _laneRoad[l] = lane.Road.Value;
            _laneReach[l] = RoadNetwork.JunctionReach(lane.Length);
            _laneZoneStart[l] = lane.Length - _laneReach[l];
        }
        SyncLinks();
        SyncRoadWear();
        EvaluateAccess();

        for (int i = 0; i < _count; i++)
        {
            if (_lane[i] < 0) continue;
            if (OnDemolishedLane(i))
            {
                // The road under it was torn down: the vehicle goes with it, and so does any wreck it was part of.
                if (_incident[i] >= 0) GetIncident(_incident[i]).Vehicles.Remove(i);
                RemoveVehicle(i);
                continue;
            }
            bool merged = MoveOffRetiredLane(i);
            if (merged && LandedOnAnother(i))
            {
                // An avenue narrowed to two lanes: this vehicle had no room in the lane it was moved into.
                if (_incident[i] >= 0) GetIncident(_incident[i]).Vehicles.Remove(i);
                RemoveVehicle(i);
                continue;
            }
            if (!CanEnter(_lane[i], _nextLane[i])) _nextLane[i] = NextLaneFor(i, _lane[i]);
        }
        CloseOrphanedDepots();
        CloseOrphanedStations();
        RebuildOrder();
    }

    /// <summary>Copies the links between lanes, which junction rebuilds change on existing lanes, into flat arrays.</summary>
    void SyncLinks()
    {
        int lanes = _net.LaneCount;
        if (_nextStart.Length != lanes + 1) _nextStart = new int[lanes + 1];
        int total = 0;
        for (int l = 0; l < lanes; l++) total += _net.GetLane(new LaneId(l)).Next.Length;
        if (_nextLanes.Length != total) _nextLanes = new int[total];
        if (_moveLength.Length != total) { _moveLength = new float[total]; _moveLimit = new float[total]; }
        int n = 0;
        for (int l = 0; l < lanes; l++)
        {
            var lane = _net.GetLane(new LaneId(l));
            _laneOncoming[l] = lane.Oncoming.Value;
            _laneLeft[l] = lane.Left.Value;
            _laneRight[l] = lane.Right.Value;
            _nextStart[l] = n;
            foreach (var next in lane.Next)
            {
                _moveLength[n] = _net.JunctionLength(lane.Id, next);
                _moveLimit[n] = TurnLimit(_moveLength[n], _net.JunctionTurn(lane.Id, next));
                _nextLanes[n++] = next.Value;
            }
        }
        _nextStart[lanes] = n;
        SyncConflictTables();
        SyncReverseLinks();
    }

    bool CanEnter(int lane, int next)
    {
        for (int k = _nextStart[lane]; k < _nextStart[lane + 1]; k++)
            if (_nextLanes[k] == next) return true;
        return false;
    }

    /// <summary>Whether the lane has been retired without anything to take its vehicles.</summary>
    bool OnDemolishedLane(int i)
    {
        var lane = _net.GetLane(new LaneId(_lane[i]));
        float pos = _pos[i];
        while (lane.Retired)
        {
            var next = Successor(lane, ref pos);
            if (next.IsNone) return true;
            lane = _net.GetLane(next);
        }
        return false;
    }

    /// <summary>The lane that takes over a retired lane's vehicle at <paramref name="position"/>, which becomes its position on that lane. None when the road was torn down.</summary>
    static LaneId Successor(Lane lane, ref float position)
    {
        if (lane.Remap is { } remap)
        {
            var (next, along) = remap.Resolve(position);
            position = along;
            return next;
        }
        if (position < lane.SplitDistance) return lane.SplitFirst;
        position -= lane.SplitDistance;
        return lane.SplitSecond;
    }

    /// <summary>A junction was built in the middle of the road this vehicle is on, or the road was rebuilt: carry on from the same spot on the new piece.</summary>
    bool MoveOffRetiredLane(int i)
    {
        var lane = _net.GetLane(new LaneId(_lane[i]));
        if (!lane.Retired) return false;
        if (_blockLane[i] >= 0) _blockedCount[_blockLane[i]]--;
        bool merged = false;
        while (lane.Retired)
        {
            merged |= lane.SplitMerges;
            _lane[i] = Successor(lane, ref _pos[i]).Value;
            lane = _net.GetLane(new LaneId(_lane[i]));
        }
        // Any manoeuvre was planned against the old geometry.
        _phase[i] = 0;
        _lat[i] = 0f;
        _target[i] = -1;
        _aborting[i] = false;
        if (_blockLane[i] >= 0) Block(i);
        return merged;
    }

    /// <summary>Whether another vehicle already stands where this one has just been placed (a rare, one-off check, so it scans every vehicle).</summary>
    bool LandedOnAnother(int i)
    {
        for (int j = 0; j < _count; j++)
            if (j != i && _lane[j] == _lane[i] && MathF.Abs(_pos[j] - _pos[i]) < (_pos[j] > _pos[i] ? _length[j] : _length[i])) return true;
        return false;
    }

    /// <summary>Picks the turn for the next junction at random, preferring roads not blocked by a wreck.</summary>
    int ChooseNextLane(int lane)
    {
        int first = _nextStart[lane], count = _nextStart[lane + 1] - first;
        if (count == 1) return _nextLanes[first];

        int open = 0;
        for (int k = 0; k < count; k++)
            if (_blockedCount[_nextLanes[first + k]] == 0) open++;
        if (open == 0 || open == count) return _nextLanes[first + _rng.NextInt(count)];

        int pick = open == 1 ? 0 : _rng.NextInt(open);
        for (int k = 0; k < count; k++)
            if (_blockedCount[_nextLanes[first + k]] == 0 && pick-- == 0) return _nextLanes[first + k];
        return _nextLanes[first];
    }

    void AdvanceLane(int i)
    {
        // A vehicle is on its lane until it has driven through the junction; then it is _laneReach metres into the next one.
        while (_pos[i] >= ZoneEnd(_lane[i], _nextLane[i]))
        {
            _pos[i] += _laneReach[_nextLane[i]] - ZoneEnd(_lane[i], _nextLane[i]);
            _prevLane[i] = _lane[i];
            _lane[i] = _nextLane[i];
            _nextLane[i] = NextLaneFor(i, _lane[i]);
            _stopHold[i] = 0f;
            // A manoeuvre cannot span a junction; the vehicle is simply back in its lane.
            _phase[i] = 0;
            _lat[i] = 0f;
            _target[i] = -1;
            _aborting[i] = false;
        }
    }

    void RebuildOrder()
    {
        Array.Clear(_laneStart);
        for (int i = 0; i < _count; i++)
            if (_lane[i] >= 0 && !_bay[i] && !_ghost[i]) _laneStart[_lane[i] + 1]++;
        for (int l = 0; l < _net.LaneCount; l++) _laneStart[l + 1] += _laneStart[l];

        // Counting sort by lane, in vehicle-id order (stable, deterministic).
        for (int l = 0; l < _cursor.Length; l++) _cursor[l] = _laneStart[l];
        for (int i = 0; i < _count; i++)
            if (_lane[i] >= 0 && !_bay[i] && !_ghost[i]) _order[_cursor[_lane[i]]++] = i;

        // Insertion sort by position within each lane; input is nearly sorted between steps.
        for (int l = 0; l < _net.LaneCount; l++)
        {
            for (int a = _laneStart[l] + 1; a < _laneStart[l + 1]; a++)
            {
                int v = _order[a];
                int b = a - 1;
                while (b >= _laneStart[l] && Precedes(v, _order[b]))
                {
                    _order[b + 1] = _order[b];
                    b--;
                }
                _order[b + 1] = v;
            }
        }
        _orderCount = _laneStart[_net.LaneCount];
        for (int r = 0; r < _orderCount; r++) _rankInOrder[_order[r]] = r;
    }

    /// <summary>
    /// Re-files one vehicle that changed lane without moving along it: shifts only the slice of <see cref="_order"/>
    /// between its old and new place, which gives the same order as <see cref="RebuildOrder"/>.
    /// </summary>
    void MoveInOrder(int vehicle, int oldLane)
    {
        int newLane = _lane[vehicle];
        if (newLane == oldLane) return;
        int from = _rankInOrder[vehicle];
        int at = _laneStart[newLane + 1]; // first vehicle in the new lane that comes after this one, else the end
        for (int r = _laneStart[newLane]; r < _laneStart[newLane + 1]; r++)
            if (Precedes(vehicle, _order[r])) { at = r; break; }

        if (newLane > oldLane)
        {
            for (int r = from + 1; r < at; r++) { _order[r - 1] = _order[r]; _rankInOrder[_order[r - 1]] = r - 1; }
            _order[at - 1] = vehicle;
            _rankInOrder[vehicle] = at - 1;
            for (int l = oldLane + 1; l <= newLane; l++) _laneStart[l]--;
        }
        else
        {
            for (int r = from; r > at; r--) { _order[r] = _order[r - 1]; _rankInOrder[_order[r]] = r; }
            _order[at] = vehicle;
            _rankInOrder[vehicle] = at;
            for (int l = newLane + 1; l <= oldLane; l++) _laneStart[l]++;
        }
    }

    // Position ascending, ties broken by id so ordering never depends on sort internals.
    bool Precedes(int a, int b) => _pos[a] < _pos[b] || (_pos[a] == _pos[b] && a < b);

    void Grow()
    {
        int n = _lane.Length * 2;
        Array.Resize(ref _lane, n);
        Array.Resize(ref _pos, n);
        Array.Resize(ref _speed, n);
        Array.Resize(ref _desired, n);
        Array.Resize(ref _accel, n);
        Array.Resize(ref _halted, n);
        Array.Resize(ref _nextLane, n);
        Array.Resize(ref _prevLane, n);
        Array.Resize(ref _factor, n);
        Array.Resize(ref _patience, n);
        Array.Resize(ref _aggression, n);
        Array.Resize(ref _attention, n);
        Array.Resize(ref _cooldown, n);
        Array.Resize(ref _stuck, n);
        Array.Resize(ref _stall, n);
        Array.Resize(ref _stopHold, n);
        Array.Resize(ref _lat, n);
        Array.Resize(ref _shift, n);
        Array.Resize(ref _blockLane, n);
        Array.Resize(ref _phase, n);
        Array.Resize(ref _target, n);
        Array.Resize(ref _aborting, n);
        Array.Resize(ref _keepLane, n);
        Array.Resize(ref _kind, n);
        Array.Resize(ref _incident, n);
        Array.Resize(ref _extraGap, n);
        Array.Resize(ref _extraSpeed, n);
        Array.Resize(ref _oncGap, n);
        Array.Resize(ref _oncSpeed, n);
        GrowFleetArrays(n);
        GrowConditionArrays(n);
        Array.Resize(ref _order, n);
        Array.Resize(ref _rankInOrder, n);
    }
}
