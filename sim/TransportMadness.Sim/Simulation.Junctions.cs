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

        var myLane = new LaneId(lane);
        var myNext = new LaneId(_nextLane[i]);
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
                if (!_net.MovesConflict(myLane, myNext, incoming[k], new LaneId(_nextLane[j]))) continue;
                worst = MathF.Max(worst, Interaction(v, distance - theirs - (VehicleLength + _drive.JunctionMargin), _speed[j]));
            }
        }
        return worst;
    }
}
