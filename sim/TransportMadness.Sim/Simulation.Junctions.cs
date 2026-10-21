namespace TransportMadness.Sim;

public sealed partial class Simulation
{
    /// <summary>
    /// Uncontrolled junctions: a vehicle that has not entered the junction waits at its edge for any vehicle with a
    /// path that meets its own which would get into the junction sooner (time to arrive from distance, speed and
    /// acceleration, ties to the lower id) or is in it, until that vehicle has left the stretch of its path where the
    /// two can touch (<see cref="RoadNetwork.GetConflictZone"/>), unless it can be through that stretch, with a margin,
    /// before the other gets to its own. Arrival order is the same for every pair, so no ring
    /// of vehicles waits for each other. A vehicle that cannot stop in time goes on, and the check in
    /// <see cref="DetectJunctionCollisions"/> sees what comes of it. The box is kept clear too: a vehicle does not
    /// enter a junction whose exit lane has no room for it. Returns the IDM braking term for the most restrictive
    /// of these, each a stopped obstacle at the edge. With signs (<see cref="RoadSignKind"/>) approaches have a rank: a vehicle
    /// does not wait for one that has not entered and ranks lower, and waits for one that ranks higher however the two arrive.
    /// </summary>
    float JunctionInteraction(int i, float v)
    {
        int lane = _lane[i];
        float distance = _laneLength[lane] - _pos[i];
        byte sign = SignOf(lane);
        int myRank = SignRank(sign);
        float range = myRank == 2 ? MathF.Max(_drive.JunctionRange, SignRules.ApproachRange) : _drive.JunctionRange;
        if (distance > range) return 0f;

        float toEdge = _laneZoneStart[lane] - _pos[i]; // front bumper to the edge of the junction
        if (toEdge <= 0f || !CanStop(v, toEdge)) return 0f; // in it already, or too late to wait
        int myNext = _nextLane[i];
        int myMove = MoveIndex(lane, myNext);
        float worst = ExitBlocked(myNext, _length[i]) ? Interaction(v, toEdge, 0f) : 0f;
        if (_lightCount > 0 && LaneIsRed(lane)) return MathF.Max(worst, Interaction(v, toEdge, 0f)); // a red light: nothing else matters
        if (sign == (byte)RoadSignKind.Stop && _stopHold[i] < SignRules.StopSeconds)
        {
            // Stand still at the line first; the others only matter once the vehicle has.
            if (toEdge <= SignRules.StopLine && v < SignRules.StopSpeed) _stopHold[i] += StepSeconds;
            return MathF.Max(worst, Interaction(v, toEdge, 0f));
        }
        float myArrival = TimeToCover(toEdge, v);
        float centre = _pos[i] - _length[i] * 0.5f - _laneZoneStart[lane]; // along the path through the junction, negative before it

        int node = _laneEndNode[lane];
        var incoming = _net.IncomingLanes(node);
        for (int k = 0; k < incoming.Count; k++)
        {
            int other = incoming[k].Value;
            if (other == lane) continue;
            int theirRank = _signCount > 0 ? SignRank(SignOf(other)) : 1;
            // Nearest to the junction first; the rest are further away.
            for (int r = _laneStart[other + 1] - 1; r >= _laneStart[other]; r--)
            {
                int j = _order[r];
                if (_laneLength[other] - _pos[j] > range) break;
                if (!Conflict(lane, myNext, myMove, other, _nextLane[j], Wide(i, j), out var zone)) continue;
                float theirCentre = _pos[j] - _length[j] * 0.5f - _laneZoneStart[other];
                if (theirCentre > zone.OutB) continue; // it has left the stretch where we meet
                float theirToEdge = _laneZoneStart[other] - _pos[j];
                if (theirToEdge > 0f && _halted[j]) continue; // standing still outside: it is not coming
                // Waiting outside for room beyond the junction: it is not coming either, and waiting for it can close a ring of queues (M6c found one).
                if (theirToEdge > 0f && _speed[j] < _drive.ExitQueueSpeed && ExitBlocked(_nextLane[j], _length[j])) continue;
                if (myRank < theirRank && theirToEdge > 0f) continue; // we have priority and it has not got into the junction
                float theirArrival = theirToEdge > 0f ? TimeToCover(theirToEdge, _speed[j]) : 0f;
                if (myRank == theirRank && (myArrival < theirArrival || (myArrival == theirArrival && i < j))) continue;
                // It would get there first, but it is still far from the stretch where we meet: if we are through before it arrives, we go.
                if (theirCentre < zone.InB && TimeToCover(zone.OutA - centre, v) + _drive.CrossingSafetyTime < TimeToCover(zone.InB - theirCentre, _speed[j])) continue;
                worst = MathF.Max(worst, Interaction(v, toEdge, 0f));
            }
        }

        // Vehicles that have driven out onto the other lanes of the junction with their tails still inside it.
        var roads = _net.RoadsAt(node);
        for (int k = 0; k < roads.Count; k++)
        {
            var road = _net.GetRoad(roads[k]);
            var leaving = road.From == node ? road.ForwardLanes : road.BackwardLanes;
            foreach (var laneId in leaving)
            {
                int outLane = laneId.Value;
                for (int r = _laneStart[outLane]; r < _laneStart[outLane + 1]; r++)
                {
                    int j = _order[r];
                    if (_pos[j] - _length[j] >= _laneReach[outLane]) break; // clear of the junction
                    int from = _prevLane[j];
                    if (from < 0 || from == lane || _laneEndNode[from] != node) continue;
                    if (!Conflict(lane, myNext, myMove, from, outLane, Wide(i, j), out var zone)) continue;
                    int theirMove = MoveIndex(from, outLane);
                    float theirCentre = (theirMove >= 0 ? _moveLength[theirMove] : _laneReach[from] + _laneReach[outLane])
                        + _pos[j] - _laneReach[outLane] - _length[j] * 0.5f;
                    if (theirCentre > zone.OutB) continue;
                    worst = MathF.Max(worst, Interaction(v, toEdge, 0f));
                }
            }
        }
        return worst;
    }

    /// <summary>Whether a pair involving these vehicles needs the conflict tables for long vehicles.</summary>
    bool Wide(int a, int b) => _length[a] > VehicleLength || _length[b] > VehicleLength;

    /// <summary>Whether a vehicle at speed <paramref name="v"/> can stop within <paramref name="distance"/> metres by braking as hard as it ever does.</summary>
    bool CanStop(float v, float distance) => distance > 0f && v * v < 2f * _drive.MaxBraking * distance;

    /// <summary>Seconds to cover <paramref name="distance"/> metres from speed <paramref name="speed"/> while accelerating.</summary>
    float TimeToCover(float distance, float speed) =>
        distance <= 0f ? 0f : (MathF.Sqrt(speed * speed + 2f * _drive.MaxAccel * distance) - speed) / _drive.MaxAccel;

    /// <summary>Whether a vehicle entering <paramref name="exit"/> would have to stop inside the junction: a queue fills the lane up to its start.</summary>
    bool ExitBlocked(int exit, float length)
    {
        for (int r = _laneStart[exit]; r < _laneStart[exit + 1]; r++)
        {
            int j = _order[r];
            if (_lat[j] >= HomeLaneLat) continue;
            float room = _pos[j] - _length[j] - _laneReach[exit];
            return room < length + _drive.MinGap && _speed[j] < _drive.ExitQueueSpeed;
        }
        return false;
    }

    // ---- Conflict table -------------------------------------------------------------------------------------------
    // Whether two moves through a junction conflict, and where, never changes (lane geometry is fixed), but asking the
    // network costs a dictionary lookup with a four-part key and some geometry. Here each node gets a small square table
    // over its moves (an incoming lane together with one of its next lanes), filled on first use from the network. The
    // entry for (a, b) holds the stretch of a's path where it meets b's.

    /// <summary>Index of the move lane-to-next in the flat next-lane array, or -1 when the lane cannot enter it.</summary>
    int MoveIndex(int lane, int next)
    {
        for (int k = _nextStart[lane]; k < _nextStart[lane + 1]; k++)
            if (_nextLanes[k] == next) return k;
        return -1;
    }

    /// <summary>
    /// Whether the move inA-to-outA meets inB-to-outB (both at the end node of <paramref name="inA"/>), and the stretches of
    /// both paths where they do; <paramref name="moveA"/> is the index of inA-to-outA, found once per vehicle.
    /// </summary>
    bool Conflict(int inA, int outA, int moveA, int inB, int outB, bool wide, out ConflictZone zone)
    {
        zone = default;
        if (inA == inB) return false;
        int moveB = MoveIndex(inB, outB);
        int a = moveA >= 0 ? _moveLocal[moveA] : -1, b = moveB >= 0 ? _moveLocal[moveB] : -1;
        if (a < 0 || b < 0)
        {
            var found = _net.GetConflictZone(new LaneId(inA), new LaneId(outA), new LaneId(inB), new LaneId(outB), wide ? _drive.WideConflictDistance : _drive.ConflictDistance, wide);
            if (found is not { } z) return false;
            zone = z;
            return true;
        }

        int node = _laneEndNode[inA];
        int count = _nodeMoveCount[node];
        int ab = _nodeMatrixStart[node] + a * count + b, ba = _nodeMatrixStart[node] + b * count + a;
        var table = wide ? _wideConflictTable : _conflictTable;
        var zoneIn = wide ? _wideZoneIn : _zoneIn;
        var zoneOut = wide ? _wideZoneOut : _zoneOut;
        if (table[ab] == 0)
        {
            var found = _net.GetConflictZone(new LaneId(inA), new LaneId(outA), new LaneId(inB), new LaneId(outB), wide ? _drive.WideConflictDistance : _drive.ConflictDistance, wide);
            if (found is { } z)
            {
                table[ab] = table[ba] = 2;
                zoneIn[ab] = z.InA; zoneOut[ab] = z.OutA;
                zoneIn[ba] = z.InB; zoneOut[ba] = z.OutB;
            }
            else table[ab] = table[ba] = 1;
        }
        if (table[ab] != 2) return false;
        zone = new ConflictZone(zoneIn[ab], zoneOut[ab], zoneIn[ba], zoneOut[ba]);
        return true;
    }

    /// <summary>Lays out one empty table per node; called whenever the links between lanes change.</summary>
    void SyncConflictTables()
    {
        int nodes = _net.NodeCount;
        _moveLocal = new int[_nextLanes.Length];
        Array.Fill(_moveLocal, -1);
        _nodeMoveCount = new int[nodes];
        _nodeMatrixStart = new int[nodes + 1];
        for (int n = 0; n < nodes; n++)
        {
            int count = 0;
            foreach (var lane in _net.IncomingLanes(n))
                for (int m = _nextStart[lane.Value]; m < _nextStart[lane.Value + 1]; m++) _moveLocal[m] = count++;
            _nodeMoveCount[n] = count;
            _nodeMatrixStart[n + 1] = _nodeMatrixStart[n] + count * count;
        }
        _conflictTable = new byte[_nodeMatrixStart[nodes]];
        _zoneIn = new float[_conflictTable.Length];
        _zoneOut = new float[_conflictTable.Length];
        _wideConflictTable = new byte[_conflictTable.Length];
        _wideZoneIn = new float[_conflictTable.Length];
        _wideZoneOut = new float[_conflictTable.Length];
    }
}
