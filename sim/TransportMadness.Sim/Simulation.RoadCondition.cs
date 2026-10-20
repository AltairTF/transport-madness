namespace TransportMadness.Sim;

/// <summary>A road closed while a crew resurfaces it: debris stands in each of its lanes until the work is done.</summary>
public sealed class RoadWork
{
    internal RoadWork(int road, long endTick)
    {
        Road = road;
        EndTick = endTick;
    }

    public int Road { get; }
    public long EndTick { get; }
    internal List<int> Vehicles { get; } = new();
}

/// <summary>
/// The Condition of road sections: it falls with the traffic that passes (ambient cars too), slows drivers and raises the breakdown risk of
/// the company's vehicles when low, and is restored by resurfacing, which closes the road like a wreck for a while.
/// </summary>
public sealed partial class Simulation
{
    // Per road: vehicles that have driven its whole length (as a fraction each step), a double so that a step's small share is not lost.
    double[] _roadPasses = Array.Empty<double>();
    float[] _roadInvLength = Array.Empty<float>();
    bool[] _roadSeeded = Array.Empty<bool>();
    bool[] _laneWearDone = Array.Empty<bool>();
    readonly List<RoadWork> _roadWorks = new();
    int _resurfaced; // roads resurfaced so far
    double _roadFreePasses; // passes below which a road has no effect on speed or risk

    public IReadOnlyList<RoadWork> RoadWorks => _roadWorks;

    /// <summary>Condition of a road, 0 to 100.</summary>
    public float RoadConditionOf(int road)
    {
        SyncNetwork();
        return road >= 0 && road < _roadPasses.Length ? (float)Math.Max(0.0, 100.0 - _roadPasses[road] / _maint.Roads.PassesPerPoint) : 100f;
    }

    /// <summary>The share of the speed limit drivers keep on this lane's road: 1 above <c>noEffectAbove</c>, falling evenly to <c>worstSpeedShare</c> at Condition 0.</summary>
    float RoadSpeedShare(int lane)
    {
        double passes = _roadPasses[_laneRoad[lane]];
        if (passes <= _roadFreePasses) return 1f;
        float condition = (float)Math.Max(0.0, 100.0 - passes / _maint.Roads.PassesPerPoint);
        return _maint.Roads.WorstSpeedShare + (1f - _maint.Roads.WorstSpeedShare) * condition / _maint.Roads.NoEffectAbove;
    }

    /// <summary>How many times the breakdown risk of a company vehicle is on this lane's road.</summary>
    float RoadRiskFactor(int lane)
    {
        double passes = _roadPasses[_laneRoad[lane]];
        if (passes <= _roadFreePasses) return 1f;
        float condition = (float)Math.Max(0.0, 100.0 - passes / _maint.Roads.PassesPerPoint);
        return 1f + (_maint.Roads.WorstRiskFactor - 1f) * (1f - condition / _maint.Roads.NoEffectAbove);
    }

    /// <summary>Wears the road each moving vehicle is on by the share of its length it drove this step.</summary>
    void WearRoads()
    {
        for (int i = 0; i < _count; i++)
        {
            if (_kind[i] != VehicleKind.Car || _ghost[i] || _lane[i] < 0) continue;
            float distance = _speed[i] * StepSeconds;
            if (distance <= 0f) continue;
            int road = _laneRoad[_lane[i]];
            _roadPasses[road] += distance * _roadInvLength[road];
        }
    }

    /// <summary>Makes room for the roads that have been built and gives roads that replaced others (split by a junction, rebuilt when a node moved) the wear of the ones they came from.</summary>
    void SyncRoadWear()
    {
        _roadFreePasses = (100.0 - _maint.Roads.NoEffectAbove) * _maint.Roads.PassesPerPoint;
        int roads = _net.RoadCount, lanes = _net.LaneCount;
        int oldRoads = _roadPasses.Length;
        Array.Resize(ref _roadPasses, roads);
        Array.Resize(ref _roadInvLength, roads);
        Array.Resize(ref _roadSeeded, roads);
        Array.Resize(ref _laneWearDone, lanes);
        for (int r = oldRoads; r < roads; r++) _roadInvLength[r] = 1f / MathF.Max(1f, _net.GetRoad(new RoadId(r)).Length);
        for (int l = 0; l < lanes; l++)
        {
            var lane = _net.GetLane(new LaneId(l));
            if (!lane.Retired || _laneWearDone[l]) continue;
            _laneWearDone[l] = true;
            double passes = _roadPasses[_laneRoad[l]];
            foreach (float at in new[] { 0f, lane.Length * 0.5f, lane.Length })
            {
                float position = at;
                var next = Successor(lane, ref position);
                var current = lane;
                while (!next.IsNone && _net.GetLane(next).Retired)
                {
                    current = _net.GetLane(next);
                    next = Successor(current, ref position);
                }
                if (next.IsNone) continue;
                int road = _laneRoad[next.Value];
                if (_roadSeeded[road]) continue;
                _roadSeeded[road] = true;
                _roadPasses[road] = passes;
            }
        }
        for (int r = oldRoads; r < roads; r++) _roadSeeded[r] = true; // roads nothing came from start new
    }

    /// <summary>The roads whose Condition is below the level at which it starts to matter, the worst first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<(int Road, float Condition)> WornRoads(int limit)
    {
        SyncNetwork();
        var worn = new List<(int Road, float Condition)>();
        for (int r = 0; r < _roadPasses.Length; r++)
        {
            if (_net.GetRoad(new RoadId(r)).Retired) continue;
            float condition = RoadConditionOf(r);
            if (condition < _maint.Roads.NoEffectAbove) worn.Add((r, condition));
        }
        worn.Sort((a, b) => a.Condition != b.Condition ? a.Condition.CompareTo(b.Condition) : a.Road.CompareTo(b.Road));
        return worn.Take(limit).ToList();
    }

    // ---- Resurfacing ----------------------------------------------------------------------------------------------------------

    /// <summary>What resurfacing a road costs.</summary>
    public long ResurfacePrice(int road) => Price(_net.GetRoad(new RoadId(road)).Length * _maint.Roads.ResurfacePricePerMetre);

    /// <summary>How long a road is closed for resurfacing, in seconds.</summary>
    public float ResurfaceSeconds(int road) => _net.GetRoad(new RoadId(road)).Length / _maint.Roads.ResurfaceMetresPerSecond;

    /// <summary>Why a road cannot be resurfaced, in words for the player; null when it can.</summary>
    public string? WhyNotResurface(int road)
    {
        if (!IsLiveRoad(road)) return "that road does not exist";
        foreach (var work in _roadWorks)
            if (work.Road == road) return "it is being resurfaced";
        if (RoadConditionOf(road) >= 99.5f) return "it needs no work";
        return CanAfford(ResurfacePrice(road)) ? null : "not enough money";
    }

    int ResurfaceRoad(int road)
    {
        if (WhyNotResurface(road) != null) return 0;
        TrySpend(ResurfacePrice(road), LedgerEntry.Maintenance);
        var work = new RoadWork(road, Tick + Ticks(ResurfaceSeconds(road)));
        var r = _net.GetRoad(new RoadId(road));
        foreach (var lane in r.ForwardLanes.Concat(r.BackwardLanes))
        {
            float length = _laneLength[lane.Value];
            float first = _laneReach[lane.Value] + VehicleLength, last = _laneZoneStart[lane.Value] - VehicleLength;
            for (int k = 0; k < 40; k++)
            {
                float at = length * 0.5f + ((k & 1) == 0 ? 1 : -1) * ((k + 1) / 2) * 6f;
                if (at < first || at > last) continue;
                if (!IsClearFor(lane, at, VehicleLength, 2f)) continue;
                int id = SpawnVehicle(lane, at, 0f);
                _kind[id] = VehicleKind.Debris;
                _halted[id] = true;
                Block(id);
                work.Vehicles.Add(id);
                break;
            }
        }
        _roadWorks.Add(work);
        RebuildOrder();
        return 1;
    }

    /// <summary>Finishes the resurfacing that is done: the debris goes, the road is as good as new.</summary>
    void StepRoadWorks()
    {
        if (_roadWorks.Count == 0) return;
        for (int k = 0; k < _roadWorks.Count; k++)
        {
            var work = _roadWorks[k];
            if (Tick < work.EndTick) continue;
            foreach (int v in work.Vehicles)
                if (_kind[v] == VehicleKind.Debris && _incident[v] < 0 && _lane[v] >= 0 && _laneRoad[_lane[v]] == work.Road) RemoveVehicle(v);
            RebuildOrder();
            if (work.Road < _roadPasses.Length) _roadPasses[work.Road] = 0.0;
            _resurfaced++;
            _roadWorks.RemoveAt(k--);
        }
    }

    ulong RoadWearHash()
    {
        ulong h = (ulong)_resurfaced;
        foreach (double passes in _roadPasses) h = h * 31 + BitConverter.DoubleToUInt64Bits(passes);
        foreach (var work in _roadWorks)
        {
            h = h * 31 + (ulong)work.Road;
            h = h * 31 + (ulong)work.EndTick;
        }
        return h;
    }
}
