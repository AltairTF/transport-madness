namespace TransportMadness.Sim;

public readonly record struct VehicleState(LaneId Lane, float Position, float Speed);

/// <summary>
/// Deterministic fixed-step traffic simulation on a <see cref="RoadNetwork"/> (ADR 0002, 0003).
/// Position is the front bumper's distance along its lane.
/// </summary>
public sealed class Simulation
{
    public const float StepSeconds = 0.05f;
    public const float VehicleLength = 4.5f;

    // Intelligent Driver Model parameters (shared for now; per-driver traits come with overtaking).
    const float MaxAccel = 1.5f;
    const float ComfortDecel = 2.0f;
    const float MinGap = 2.0f;
    const float TimeHeadway = 1.5f;
    const float MaxBraking = 9.0f; // physical limit; keeps lane-change speed mismatches from producing absurd decelerations

    readonly RoadNetwork _net;
    readonly SimRandom _rng;

    int _count;
    int[] _lane = new int[64];
    float[] _pos = new float[64];
    float[] _speed = new float[64];
    float[] _desired = new float[64]; // cap on top of the lane's speed limit
    float[] _accel = new float[64];
    bool[] _halted = new bool[64];
    int[] _nextLane = new int[64]; // lane this vehicle will enter at the end of its current one

    // Vehicles grouped by lane, ascending position within a lane. Rebuilt each step.
    int[] _order = new int[64];
    int[] _laneStart = Array.Empty<int>();
    int[] _rankInOrder = new int[64];
    int[] _cursor = Array.Empty<int>();

    /// <summary>The network must be complete: lanes added after construction are not simulated.</summary>
    public Simulation(RoadNetwork net, ulong seed)
    {
        _net = net;
        _rng = new SimRandom(seed);
        _laneStart = new int[net.LaneCount + 1];
        _cursor = new int[net.LaneCount];
    }

    public RoadNetwork Network => _net;
    public int VehicleCount => _count;
    public long Tick { get; private set; }

    /// <summary>
    /// Scatters up to <paramref name="count"/> vehicles on random lanes at random free spots, drawing from the
    /// simulation's RNG. Returns how many were placed (fewer when the network is full).
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
            SpawnVehicle(lane.Id, pos, lane.SpeedLimit * 0.5f);
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
        Mix((ulong)Tick);
        Mix((ulong)_count);
        for (int i = 0; i < _count; i++)
        {
            Mix((ulong)_lane[i]);
            Mix((ulong)_nextLane[i]);
            Mix(BitConverter.SingleToUInt32Bits(_pos[i]));
            Mix(BitConverter.SingleToUInt32Bits(_speed[i]));
            Mix(BitConverter.SingleToUInt32Bits(_desired[i]));
            Mix(_halted[i] ? 1UL : 0UL);
        }
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
        _halted[id] = false;
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

    public VehicleState GetVehicle(int id) => new(new LaneId(_lane[id]), _pos[id], _speed[id]);

    public void Step()
    {
        for (int i = 0; i < _count; i++)
        {
            if (_halted[i]) continue;
            float v0 = MathF.Min(_net.GetLane(new LaneId(_lane[i])).SpeedLimit, _desired[i]);
            float v = _speed[i];
            float ratio = v / v0;
            float free = 1f - ratio * ratio * ratio * ratio;

            float interaction = 0f;
            int rank = _rankInOrder[i];
            int lane = _lane[i];
            int lead = -1;
            float gap = 0f;
            if (rank + 1 < _laneStart[lane + 1])
            {
                lead = _order[rank + 1];
                gap = _pos[lead] - VehicleLength - _pos[i];
            }
            else
            {
                // Last in its lane: the leader is the rearmost vehicle on the lane it is about to enter.
                int next = _nextLane[i];
                if (_laneStart[next + 1] > _laneStart[next])
                {
                    lead = _order[_laneStart[next]];
                    gap = _net.GetLane(new LaneId(lane)).Length - _pos[i] + _pos[lead] - VehicleLength;
                }
            }
            if (lead >= 0)
            {
                gap = MathF.Max(0.01f, gap);
                float dv = v - _speed[lead];
                float desiredGap = MinGap + MathF.Max(0f, v * TimeHeadway + v * dv / (2f * MathF.Sqrt(MaxAccel * ComfortDecel)));
                float g = desiredGap / gap;
                interaction = g * g;
            }
            _accel[i] = MathF.Max(-MaxBraking, MaxAccel * (free - interaction));
        }
        for (int i = 0; i < _count; i++)
        {
            if (_halted[i]) continue;
            _speed[i] = MathF.Max(0f, _speed[i] + _accel[i] * StepSeconds);
            _pos[i] += _speed[i] * StepSeconds;
            AdvanceLane(i);
        }
        RebuildOrder();
        Tick++;
    }

    LaneId ChooseNextLane(LaneId lane)
    {
        var next = _net.GetLane(lane).Next;
        return next.Length == 1 ? next[0] : next[_rng.NextInt(next.Length)];
    }

    void AdvanceLane(int i)
    {
        while (true)
        {
            float length = _net.GetLane(new LaneId(_lane[i])).Length;
            if (_pos[i] < length) return;
            _pos[i] -= length;
            _lane[i] = _nextLane[i];
            _nextLane[i] = ChooseNextLane(new LaneId(_lane[i])).Value;
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
        Array.Resize(ref _order, n);
        Array.Resize(ref _rankInOrder, n);
    }
}
