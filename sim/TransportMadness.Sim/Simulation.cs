namespace TransportMadness.Sim;

/// <summary>Where a vehicle is in an overtaking manoeuvre on the oncoming lane.</summary>
public enum OvertakePhase : byte { None, MovingOut, Passing, Returning }

/// <param name="Lateral">0 in the vehicle's own lane, 1 fully in the oncoming lane.</param>
public readonly record struct VehicleState(LaneId Lane, float Position, float Speed, float Lateral, OvertakePhase Overtake);

/// <summary>
/// Deterministic fixed-step traffic simulation on a <see cref="RoadNetwork"/> (ADR 0002, 0003).
/// Position is the front bumper's distance along the vehicle's lane.
/// </summary>
public sealed partial class Simulation
{
    public const float StepSeconds = 0.05f;
    public const float VehicleLength = 4.5f;
    public const float VehicleWidth = 1.9f;
    public const float LaneWidth = 3.5f;

    // Intelligent Driver Model parameters (shared; driver traits only change when to overtake).
    const float MaxAccel = 1.5f;
    const float ComfortDecel = 2.0f;
    const float MinGap = 2.0f;
    const float TimeHeadway = 1.5f;
    const float MaxBraking = 9.0f; // physical limit; keeps lane-change speed mismatches from producing absurd decelerations
    static readonly float TwoSqrtAB = 2f * MathF.Sqrt(MaxAccel * ComfortDecel);

    readonly RoadNetwork _net;
    readonly SimRandom _rng;

    // Per-lane constants copied out of the network so the hot loop stays on flat arrays.
    readonly float[] _laneLength;
    readonly float[] _laneLimit;
    readonly int[] _laneEndNode;

    int _count;
    int[] _lane = new int[64];
    float[] _pos = new float[64];
    float[] _speed = new float[64];
    float[] _desired = new float[64]; // absolute speed cap on top of the lane's speed limit
    float[] _accel = new float[64];
    bool[] _halted = new bool[64];
    int[] _nextLane = new int[64]; // lane this vehicle will enter at the end of its current one
    float[] _factor = new float[64]; // personal fraction of the lane's speed limit
    float[] _patience = new float[64];
    float[] _aggression = new float[64];
    float[] _attention = new float[64];
    int[] _cooldown = new int[64]; // ticks before the next lane change or overtake
    float[] _stuck = new float[64]; // seconds held up by a slower leader
    float[] _lat = new float[64]; // 0 in own lane .. 1 fully in the oncoming lane
    byte[] _phase = new byte[64];
    int[] _target = new int[64]; // vehicle being overtaken, or -1
    bool[] _aborting = new bool[64];
    bool[] _keepLane = new bool[64];
    float[] _extraGap = new float[64]; // closing obstacle formed by an oncoming overtaker: gap ...
    float[] _extraSpeed = new float[64]; // ... and its speed as seen by us (negative: coming at us)
    float[] _oncGap = new float[64]; // nearest oncoming vehicle ahead of an overtaker
    float[] _oncSpeed = new float[64];

    // Vehicles grouped by lane, ascending position within a lane. Rebuilt each step.
    int[] _order = new int[64];
    int[] _laneStart;
    int[] _rankInOrder = new int[64];
    readonly int[] _cursor;

    readonly List<int> _overtakers = new();

    /// <summary>The network must be complete: lanes added after construction are not simulated.</summary>
    public Simulation(RoadNetwork net, ulong seed)
    {
        _net = net;
        _rng = new SimRandom(seed);
        _laneStart = new int[net.LaneCount + 1];
        _cursor = new int[net.LaneCount];
        _laneLength = new float[net.LaneCount];
        _laneLimit = new float[net.LaneCount];
        _laneEndNode = new int[net.LaneCount];
        for (int l = 0; l < net.LaneCount; l++)
        {
            var lane = net.GetLane(new LaneId(l));
            _laneLength[l] = lane.Length;
            _laneLimit[l] = lane.SpeedLimit;
            _laneEndNode[l] = lane.EndNode;
        }
    }

    public RoadNetwork Network => _net;
    public int VehicleCount => _count;
    public long Tick { get; private set; }

    /// <summary>
    /// Scatters up to <paramref name="count"/> vehicles on random lanes at random free spots, with random driver
    /// traits, drawing from the simulation's RNG. Returns how many were placed (fewer when the network is full).
    /// </summary>
    public int SeedTraffic(int count)
    {
        const float clearance = 12f;
        int placed = 0;
        for (int attempt = 0; placed < count && attempt < count * 30; attempt++)
        {
            var lane = _net.GetLane(new LaneId(_rng.NextInt(_net.LaneCount)));
            if (lane.Length < 2 * clearance) continue;
            float pos = clearance + _rng.NextFloat() * (lane.Length - 2 * clearance);
            if (!IsClear(lane.Id, pos, clearance)) continue;
            int id = SpawnVehicle(lane.Id, pos, lane.SpeedLimit * 0.5f);
            _factor[id] = 0.65f + 0.35f * _rng.NextFloat();
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
            Mix((ulong)_nextLane[i]);
            MixF(_pos[i]);
            MixF(_speed[i]);
            MixF(_desired[i]);
            MixF(_factor[i]);
            MixF(_patience[i]);
            MixF(_aggression[i]);
            MixF(_attention[i]);
            MixF(_stuck[i]);
            MixF(_lat[i]);
            Mix((ulong)_cooldown[i]);
            Mix((ulong)_phase[i]);
            Mix((ulong)(_target[i] + 1));
            Mix((_halted[i] ? 1UL : 0UL) | (_aborting[i] ? 2UL : 0UL) | (_keepLane[i] ? 4UL : 0UL));
        }
        Mix(IncidentHash());
        var (a, b, c, d) = _rng.State;
        Mix(a); Mix(b); Mix(c); Mix(d);
        return h;
    }

    public int SpawnVehicle(LaneId lane, float position, float speed)
    {
        if (_count == _lane.Length) Grow();
        int id = _count++;
        _lane[id] = lane.Value;
        _pos[id] = position;
        _speed[id] = speed;
        _desired[id] = float.MaxValue;
        _factor[id] = 1f;
        _patience[id] = 0.5f;
        _aggression[id] = 0.5f;
        _attention[id] = 1f;
        _halted[id] = false;
        _cooldown[id] = 0;
        _stuck[id] = 0f;
        _lat[id] = 0f;
        _phase[id] = 0;
        _target[id] = -1;
        _aborting[id] = false;
        _keepLane[id] = false;
        _nextLane[id] = ChooseNextLane(lane).Value;
        OnVehicleSpawned(id);
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

    public VehicleState GetVehicle(int id) =>
        new(new LaneId(_lane[id]), _pos[id], _speed[id], _lat[id], (OvertakePhase)_phase[id]);

    public void Step()
    {
        DecideManoeuvres();
        PrepareOvertakes();
        for (int i = 0; i < _count; i++)
        {
            if (_halted[i]) continue;
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

            _accel[i] = MathF.Max(-MaxBraking, MaxAccel * (1f - ratio * ratio * ratio * ratio - interaction));
        }
        for (int i = 0; i < _count; i++)
        {
            if (_halted[i]) continue;
            _speed[i] = MathF.Max(0f, _speed[i] + _accel[i] * StepSeconds);
            _pos[i] += _speed[i] * StepSeconds;
            AdvanceLane(i);
        }
        DetectIncidents();
        RebuildOrder();
        Tick++;
    }

    /// <summary>The speed a driver aims for when nothing is in the way.</summary>
    float FreeSpeed(int i)
    {
        float v0 = _laneLimit[_lane[i]] * _factor[i];
        if (_phase[i] != 0) v0 *= OvertakeBoost(i);
        return MathF.Min(v0, _desired[i]);
    }

    float OvertakeBoost(int i) => 1.1f + 0.1f * _aggression[i];

    /// <summary>IDM braking term for a leader <paramref name="gap"/> metres ahead moving at <paramref name="leadSpeed"/>.</summary>
    static float Interaction(float v, float gap, float leadSpeed)
    {
        gap = MathF.Max(0.01f, gap);
        float dv = v - leadSpeed;
        float desiredGap = MinGap + MathF.Max(0f, v * TimeHeadway + v * dv / TwoSqrtAB);
        float g = desiredGap / gap;
        return g * g;
    }

    /// <summary>
    /// The vehicle directly ahead in the same lane, or failing that the rearmost one on the lane about to be entered.
    /// Vehicles that are out in the oncoming lane do not count as being in this one, and a vehicle that is itself out
    /// there has no leader (it watches oncoming traffic instead) unless it is aborting back behind its target.
    /// </summary>
    bool FindLeader(int i, out int leader, out float gap, out float leadSpeed)
    {
        leader = -1;
        gap = 0f;
        leadSpeed = 0f;
        int lane = _lane[i];

        if (_lat[i] >= HomeLaneLat)
        {
            int t = _target[i];
            if (!(_aborting[i] && t >= 0 && _lane[t] == lane)) return false;
            leader = t;
            gap = _pos[t] - VehicleLength - _pos[i];
            leadSpeed = _speed[t];
            return true;
        }

        int end = _laneStart[lane + 1];
        for (int r = _rankInOrder[i] + 1; r < end; r++)
        {
            int j = _order[r];
            if (_lat[j] >= HomeLaneLat) continue;
            leader = j;
            gap = _pos[j] - VehicleLength - _pos[i];
            leadSpeed = _speed[j];
            return true;
        }

        int next = _nextLane[i];
        for (int r = _laneStart[next]; r < _laneStart[next + 1]; r++)
        {
            int j = _order[r];
            if (_lat[j] >= HomeLaneLat) continue;
            leader = j;
            gap = _laneLength[lane] - _pos[i] + _pos[j] - VehicleLength;
            leadSpeed = _speed[j];
            return true;
        }
        return false;
    }

    LaneId ChooseNextLane(LaneId lane)
    {
        var next = _net.GetLane(lane).Next;
        return next.Length == 1 ? next[0] : next[_rng.NextInt(next.Length)];
    }

    void AdvanceLane(int i)
    {
        while (_pos[i] >= _laneLength[_lane[i]])
        {
            _pos[i] -= _laneLength[_lane[i]];
            _lane[i] = _nextLane[i];
            _nextLane[i] = ChooseNextLane(new LaneId(_lane[i])).Value;
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
        for (int i = 0; i < _count; i++) _laneStart[_lane[i] + 1]++;
        for (int l = 0; l < _net.LaneCount; l++) _laneStart[l + 1] += _laneStart[l];

        // Counting sort by lane, in vehicle-id order (stable, deterministic).
        for (int l = 0; l < _cursor.Length; l++) _cursor[l] = _laneStart[l];
        for (int i = 0; i < _count; i++) _order[_cursor[_lane[i]]++] = i;

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
        for (int r = 0; r < _count; r++) _rankInOrder[_order[r]] = r;
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
        Array.Resize(ref _factor, n);
        Array.Resize(ref _patience, n);
        Array.Resize(ref _aggression, n);
        Array.Resize(ref _attention, n);
        Array.Resize(ref _cooldown, n);
        Array.Resize(ref _stuck, n);
        Array.Resize(ref _lat, n);
        Array.Resize(ref _phase, n);
        Array.Resize(ref _target, n);
        Array.Resize(ref _aborting, n);
        Array.Resize(ref _keepLane, n);
        Array.Resize(ref _extraGap, n);
        Array.Resize(ref _extraSpeed, n);
        Array.Resize(ref _oncGap, n);
        Array.Resize(ref _oncSpeed, n);
        Array.Resize(ref _order, n);
        Array.Resize(ref _rankInOrder, n);
        GrowIncidentState(n);
    }
}
