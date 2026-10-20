namespace TransportMadness.Sim;

/// <summary>
/// Route finding for company vehicles. For each station there is a field over the lane graph, the time to drive from the
/// start of a lane to the station's stop, found backwards from the stop lanes; a vehicle at a junction takes the turn
/// with the lowest field. Fields are made when first needed and again after the network changes.
/// </summary>
public sealed partial class Simulation
{
    const float BlockedLanePenalty = 600f; // seconds a lane with a wreck in it counts as longer

    readonly List<float[]> _fields = new();
    readonly List<int> _fieldVersion = new();
    readonly List<float[]> _nodeFields = new(); // by node: the field of the lanes into it, for trip cars
    readonly List<int> _nodeFieldVersion = new();
    // Reverse of _nextLanes: the lanes (and moves) that lead into each lane.
    int[] _predStart = new int[1];
    int[] _preds = Array.Empty<int>();
    int[] _predMove = Array.Empty<int>();

    void SyncReverseLinks()
    {
        int lanes = _net.LaneCount;
        int total = _nextStart[lanes];
        if (_predStart.Length != lanes + 1) _predStart = new int[lanes + 1];
        Array.Clear(_predStart);
        if (_preds.Length != total) { _preds = new int[total]; _predMove = new int[total]; }
        for (int l = 0; l < lanes; l++)
        {
            if (_net.GetLane(new LaneId(l)).Retired) continue;
            for (int k = _nextStart[l]; k < _nextStart[l + 1]; k++) _predStart[_nextLanes[k] + 1]++;
        }
        for (int l = 0; l < lanes; l++) _predStart[l + 1] += _predStart[l];
        var cursor = new int[lanes];
        Array.Copy(_predStart, cursor, lanes);
        for (int l = 0; l < lanes; l++)
        {
            if (_net.GetLane(new LaneId(l)).Retired) continue;
            for (int k = _nextStart[l]; k < _nextStart[l + 1]; k++)
            {
                int slot = cursor[_nextLanes[k]]++;
                _preds[slot] = l;
                _predMove[slot] = k;
            }
        }
    }

    float LaneTime(int lane) => _laneLength[lane] / MathF.Max(_laneLimit[lane], 1f);
    float MoveTime(int lane, int move) => _moveLength[move] / MathF.Max(_laneLimit[lane], 1f);

    /// <summary>Seconds to drive from the start of each lane to the station's stop; infinity where it cannot be reached.</summary>
    float[] FieldTo(Station station)
    {
        if (_fieldVersion[station.Id] == _syncedNetworkVersion) return _fields[station.Id];
        var sources = new List<int>();
        for (int l = 0; l < _net.LaneCount; l++)
            if (!_net.GetLane(new LaneId(l)).Retired && IsStopLane(l, station)) sources.Add(l);
        _fields[station.Id] = RunField(sources);
        _fieldVersion[station.Id] = _syncedNetworkVersion;
        return _fields[station.Id];
    }

    /// <summary>Seconds to drive from the start of each lane to the end of a lane running into the node (where a trip car is at its destination); infinity where it cannot be reached.</summary>
    float[] FieldToNode(int node)
    {
        while (_nodeFields.Count <= node) { _nodeFields.Add(Array.Empty<float>()); _nodeFieldVersion.Add(-1); }
        if (_nodeFieldVersion[node] == _syncedNetworkVersion) return _nodeFields[node];
        var sources = new List<int>();
        foreach (var lane in _net.IncomingLanes(node)) sources.Add(lane.Value);
        _nodeFields[node] = RunField(sources);
        _nodeFieldVersion[node] = _syncedNetworkVersion;
        return _nodeFields[node];
    }

    /// <summary>Dijkstra backwards over the lane graph from lanes that count as being there.</summary>
    float[] RunField(List<int> sources)
    {
        int lanes = _net.LaneCount;
        var dist = new float[lanes];
        Array.Fill(dist, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        foreach (int l in sources)
        {
            dist[l] = LaneTime(l);
            queue.Enqueue(l, dist[l]);
        }
        while (queue.TryDequeue(out int lane, out float d))
        {
            if (d > dist[lane]) continue;
            for (int s = _predStart[lane]; s < _predStart[lane + 1]; s++)
            {
                int from = _preds[s];
                float candidate = d + LaneTime(from) + MoveTime(from, _predMove[s]);
                if (candidate >= dist[from]) continue;
                dist[from] = candidate;
                queue.Enqueue(from, candidate);
            }
        }
        return dist;
    }

    /// <summary>The turn at the end of <paramref name="lane"/> that leads soonest to the vehicle's next station, round wrecks where it can; random when none leads there.</summary>
    int RouteNextLane(int vehicle, int lane)
    {
        int first = _nextStart[lane], count = _nextStart[lane + 1] - first;
        if (count == 1) return _nextLanes[first];
        var station = TargetStation(vehicle);
        if (station == null || !station.Active) return ChooseNextLane(lane);

        var field = FieldTo(station);
        int best = -1;
        float bestCost = float.PositiveInfinity;
        for (int k = first; k < first + count; k++)
        {
            int next = _nextLanes[k];
            float cost = field[next] + MoveTime(lane, k) + (_blockedCount[next] > 0 ? BlockedLanePenalty : 0f);
            if (cost < bestCost) { bestCost = cost; best = k; }
        }
        return best >= 0 ? _nextLanes[best] : ChooseNextLane(lane);
    }

    /// <summary>The turn at the end of <paramref name="lane"/> that leads soonest to the node a trip car is bound for; random when none leads there.</summary>
    int RouteTripLane(int vehicle, int lane)
    {
        int first = _nextStart[lane], count = _nextStart[lane + 1] - first;
        if (count == 1) return _nextLanes[first];
        var field = FieldToNode(_tripDest[vehicle]);
        int best = -1;
        float bestCost = float.PositiveInfinity;
        for (int k = first; k < first + count; k++)
        {
            int next = _nextLanes[k];
            float cost = field[next] + MoveTime(lane, k) + (_blockedCount[next] > 0 ? BlockedLanePenalty : 0f);
            if (cost < bestCost) { bestCost = cost; best = k; }
        }
        return best >= 0 ? _nextLanes[best] : ChooseNextLane(lane);
    }

    /// <summary>The lane to take after <paramref name="lane"/>: routed for a company vehicle or a trip car, random for other ambient traffic.</summary>
    int NextLaneFor(int vehicle, int lane) =>
        _returnDepot[vehicle] >= 0 ? RouteTripLane(vehicle, lane)
        : _line[vehicle] >= 0 ? RouteNextLane(vehicle, lane)
        : _tripDest[vehicle] >= 0 ? RouteTripLane(vehicle, lane)
        : ChooseNextLane(lane);
}
