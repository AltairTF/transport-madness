namespace TransportMadness.Sim;

public sealed partial class Simulation
{
    /// <summary>
    /// Uncontrolled junctions are zipper-style: a vehicle yields to any vehicle on another approach that would
    /// reach the junction first and whose path meets its own (it merges into the same lane or crosses it).
    /// The yielded-to vehicle is treated as a virtual leader sitting ahead of us by the difference in distance
    /// to the junction. Returns the IDM braking term for the most restrictive one.
    /// </summary>
    float JunctionInteraction(int i, float v)
    {
        int lane = _lane[i];
        float distance = _laneLength[lane] - _pos[i];
        if (distance > _drive.JunctionRange) return 0f;

        int myNext = _nextLane[i];
        int myMove = MoveIndex(lane, myNext);
        var incoming = _net.IncomingLanes(_laneEndNode[lane]);
        float worst = 0f;
        for (int k = 0; k < incoming.Count; k++)
        {
            int other = incoming[k].Value;
            if (other == lane) continue;
            // Nearest to the junction first; once one is further than us, the rest are too.
            for (int r = _laneStart[other + 1] - 1; r >= _laneStart[other]; r--)
            {
                int j = _order[r];
                float theirs = _laneLength[other] - _pos[j];
                if (theirs > distance || (theirs == distance && j > i)) break;
                if (!MovesConflict(lane, myNext, myMove, other, _nextLane[j])) continue;
                worst = MathF.Max(worst, Interaction(v, distance - theirs - (VehicleLength + _drive.JunctionMargin), _speed[j]));
            }
        }
        return worst;
    }

    // ---- Conflict table -------------------------------------------------------------------------------------------
    // Whether two moves through a junction conflict never changes (lane geometry is fixed), but asking the network
    // costs a dictionary lookup with a four-part key. Here each node gets a small square table over its moves
    // (an incoming lane together with one of its next lanes), filled on first use from the network.

    /// <summary>Index of the move lane-to-next in the flat next-lane array, or -1 when the lane cannot enter it.</summary>
    int MoveIndex(int lane, int next)
    {
        for (int k = _nextStart[lane]; k < _nextStart[lane + 1]; k++)
            if (_nextLanes[k] == next) return k;
        return -1;
    }

    /// <summary>Same answer as <see cref="RoadNetwork.MovesConflict"/> for two moves at the end node of <paramref name="inA"/>; <paramref name="moveA"/> is the index of inA-to-outA, found once per vehicle.</summary>
    bool MovesConflict(int inA, int outA, int moveA, int inB, int outB)
    {
        if (outA == outB) return inA != inB;
        if (inA == inB) return false;
        int moveB = MoveIndex(inB, outB);
        int a = moveA >= 0 ? _moveLocal[moveA] : -1, b = moveB >= 0 ? _moveLocal[moveB] : -1;
        if (a < 0 || b < 0) return _net.MovesConflict(new LaneId(inA), new LaneId(outA), new LaneId(inB), new LaneId(outB));

        int node = _laneEndNode[inA];
        int slot = _nodeMatrixStart[node] + a * _nodeMoveCount[node] + b;
        byte known = _conflictTable[slot];
        if (known != 0) return known == 2;
        bool result = _net.MovesConflict(new LaneId(inA), new LaneId(outA), new LaneId(inB), new LaneId(outB));
        _conflictTable[slot] = (byte)(result ? 2 : 1);
        return result;
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
    }
}
