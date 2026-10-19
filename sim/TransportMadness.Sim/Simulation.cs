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

    // Intelligent Driver Model parameters (shared; driver traits only change when to overtake).
    const float MaxAccel = 1.5f;
    const float ComfortDecel = 2.0f;
    const float MinGap = 2.0f;
    const float TimeHeadway = 1.5f;
    const float MaxBraking = 9.0f; // physical limit; keeps lane-change speed mismatches from producing absurd decelerations
    const float BrakeJerk = 15f; // braking builds up at most this fast (m/s^3): drivers need a moment to react
    static readonly float TwoSqrtAB = 2f * MathF.Sqrt(MaxAccel * ComfortDecel);

    readonly RoadNetwork _net;
    int _syncedNetworkVersion = -1;
    readonly SimRandom _rng;

    // Per-lane constants copied out of the network so the hot loop stays on flat arrays.
    float[] _laneLength = Array.Empty<float>();
    float[] _laneLimit = Array.Empty<float>();
    int[] _laneEndNode = Array.Empty<int>();

    int _count; // slots in use, including removed ones
    int _active; // vehicles that currently exist
    readonly Stack<int> _freeSlots = new();
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
    float[] _stall = new float[64]; // seconds an overtaker has stood still out in the oncoming lane
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

    // Vehicles grouped by lane, ascending position within a lane. Rebuilt each step.
    int[] _order = new int[64];
    int[] _laneStart = new int[1];
    int[] _rankInOrder = new int[64];
    int[] _cursor = Array.Empty<int>();

    readonly List<int> _overtakers = new();

    /// <summary>The network may keep growing: roads added later are picked up at the start of the next step or spawn.</summary>
    public Simulation(RoadNetwork net, ulong seed)
    {
        _net = net;
        _rng = new SimRandom(seed);
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
        const float clearance = 12f;
        int placed = 0;
        for (int attempt = 0; placed < count && attempt < count * 30; attempt++)
        {
            var lane = _net.GetLane(new LaneId(_rng.NextInt(_net.LaneCount)));
            if (lane.Retired || lane.Length < 2 * clearance) continue;
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
            MixF(_accel[i]);
            MixF(_lat[i]);
            MixF(_shift[i]);
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
        _lat[id] = 0f;
        _shift[id] = 0f;
        _blockLane[id] = -1;
        _phase[id] = 0;
        _target[id] = -1;
        _aborting[id] = false;
        _keepLane[id] = false;
        _kind[id] = VehicleKind.Car;
        _incident[id] = -1;
        _nextLane[id] = ChooseNextLane(lane).Value;
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
        new(new LaneId(_lane[id]), _pos[id], _speed[id], _lat[id], (OvertakePhase)_phase[id], _kind[id], _shift[id]);

    public void Step()
    {
        SyncNetwork();
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

            float wanted = MathF.Max(-MaxBraking, MaxAccel * (1f - ratio * ratio * ratio * ratio - interaction));
            _accel[i] = MathF.Max(wanted, _accel[i] - BrakeJerk * StepSeconds);
        }
        for (int i = 0; i < _count; i++)
        {
            if (_halted[i]) continue;
            _speed[i] = MathF.Max(0f, _speed[i] + _accel[i] * StepSeconds);
            _pos[i] += _speed[i] * StepSeconds;
            if (_shift[i] != 0f) _shift[i] = MathF.Sign(_shift[i]) * MathF.Max(0f, MathF.Abs(_shift[i]) - LaneWidth / LaneChangeSeconds * StepSeconds);
            AdvanceLane(i);
        }
        RebuildOrder();
        DetectIncidents();
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
                gap = _pos[j] - VehicleLength - _pos[i];
                leadSpeed = _speed[j];
                found = true;
                break;
            }
            // ... and an aborting overtaker is on its way back in behind its target.
            int t = _target[i];
            if (_aborting[i] && t >= 0 && _lane[t] == lane)
            {
                float g = _pos[t] - VehicleLength - _pos[i];
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
        Array.Resize(ref _blockedCount, lanes);
        _laneStart = new int[lanes + 1];
        _cursor = new int[lanes];
        for (int l = old; l < lanes; l++)
        {
            var lane = _net.GetLane(new LaneId(l));
            _laneLength[l] = lane.Length;
            _laneLimit[l] = lane.SpeedLimit;
            _laneEndNode[l] = lane.EndNode;
        }

        for (int i = 0; i < _count; i++)
        {
            if (_lane[i] < 0) continue;
            if (OnDemolishedLane(i))
            {
                // The road under it was torn down: the vehicle goes with it, and so does any wreck it was part of.
                if (_incident[i] >= 0) _incidents[_incident[i]].Vehicles.Remove(i);
                RemoveVehicle(i);
                continue;
            }
            MoveOffRetiredLane(i);
            var next = _net.GetLane(new LaneId(_lane[i])).Next;
            if (Array.IndexOf(next, new LaneId(_nextLane[i])) < 0) _nextLane[i] = ChooseNextLane(new LaneId(_lane[i])).Value;
        }
        RebuildOrder();
    }

    /// <summary>Whether the lane has been retired without anything to take its vehicles.</summary>
    bool OnDemolishedLane(int i)
    {
        var lane = _net.GetLane(new LaneId(_lane[i]));
        while (lane.Retired)
        {
            var next = _pos[i] < lane.SplitDistance ? lane.SplitFirst : lane.SplitSecond;
            if (next.IsNone) return true;
            lane = _net.GetLane(next);
        }
        return false;
    }

    /// <summary>A junction was built in the middle of the road this vehicle is on: carry on from the same spot on the new piece.</summary>
    void MoveOffRetiredLane(int i)
    {
        var lane = _net.GetLane(new LaneId(_lane[i]));
        if (!lane.Retired) return;
        if (_blockLane[i] >= 0) _blockedCount[_blockLane[i]]--;
        while (lane.Retired)
        {
            if (_pos[i] < lane.SplitDistance) _lane[i] = lane.SplitFirst.Value;
            else
            {
                _pos[i] -= lane.SplitDistance;
                _lane[i] = lane.SplitSecond.Value;
            }
            lane = _net.GetLane(new LaneId(_lane[i]));
        }
        // Any manoeuvre was planned against the old geometry.
        _phase[i] = 0;
        _lat[i] = 0f;
        _target[i] = -1;
        _aborting[i] = false;
        if (_blockLane[i] >= 0) Block(i);
    }

    /// <summary>Picks the turn for the next junction at random, preferring roads not blocked by a wreck.</summary>
    LaneId ChooseNextLane(LaneId lane)
    {
        var next = _net.GetLane(lane).Next;
        if (next.Length == 1) return next[0];

        int open = 0;
        for (int k = 0; k < next.Length; k++)
            if (_blockedCount[next[k].Value] == 0) open++;
        if (open == 0 || open == next.Length) return next[_rng.NextInt(next.Length)];

        int pick = open == 1 ? 0 : _rng.NextInt(open);
        for (int k = 0; k < next.Length; k++)
            if (_blockedCount[next[k].Value] == 0 && pick-- == 0) return next[k];
        return next[0];
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
        for (int i = 0; i < _count; i++)
            if (_lane[i] >= 0) _laneStart[_lane[i] + 1]++;
        for (int l = 0; l < _net.LaneCount; l++) _laneStart[l + 1] += _laneStart[l];

        // Counting sort by lane, in vehicle-id order (stable, deterministic).
        for (int l = 0; l < _cursor.Length; l++) _cursor[l] = _laneStart[l];
        for (int i = 0; i < _count; i++)
            if (_lane[i] >= 0) _order[_cursor[_lane[i]]++] = i;

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
        for (int r = 0; r < _active; r++) _rankInOrder[_order[r]] = r;
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
        Array.Resize(ref _stall, n);
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
        Array.Resize(ref _order, n);
        Array.Resize(ref _rankInOrder, n);
    }
}
