namespace TransportMadness.Sim;

/// <summary>The kind of place a line stop is, as the new line tool names it before the stop exists (M6f).</summary>
public enum StopKind { Station, Industry, Gateway }

/// <summary>
/// One leg of a line as the vehicles would drive it: whether a road route runs between the two stops, how long it takes in seconds at the posted speeds and how
/// long it is in metres, the route as points on the centre line (a straight line between the stops when there is none), and the cargo the first stop makes that
/// the second takes.
/// </summary>
public sealed record RouteLeg(bool Reachable, float Seconds, float Length, IReadOnlyList<(float X, float Z)> Points, IReadOnlyList<int> Cargo);

public sealed partial class Simulation
{
    /// <summary>The road node a stop of the given kind would use: a station's node, or the node at the access point of an industry or road gateway; -1 when it has none (yet).</summary>
    public int StopNodeOf(StopKind kind, int id)
    {
        switch (kind)
        {
            case StopKind.Station:
                return id >= 0 && id < _stations.Count && _stations[id].Active && _stations[id].Mode == StationMode.Road ? _stations[id].Node : -1;
            case StopKind.Industry:
                return id >= 0 && id < _industries.Count ? AccessNodeOf(new AccessRef(false, id)) : -1;
            default:
                return id >= 0 && id < _gateways.Count && _gateways[id].Kind == GatewayKind.Road ? AccessNodeOf(new AccessRef(true, id)) : -1;
        }
    }

    (float X, float Z) StopPoint(StopKind kind, int id) =>
        kind == StopKind.Station ? PointOf(_stations[id]) : AccessPointOf(new AccessRef(kind == StopKind.Gateway, id));

    /// <summary>The cargo a stop makes (what the industries it covers produce) and, in <paramref name="takes"/>, what it takes.</summary>
    void StopCargo(StopKind kind, int id, List<int> makes, bool[] takes)
    {
        if (_coverageDirty || _stationAccepts.Count != _stations.Count) RefreshCoverage();
        switch (kind)
        {
            case StopKind.Station:
                foreach (int industry in _stationIndustries[id])
                {
                    int c = _industryOutput[_industries[industry].Type];
                    if (c >= 0 && !makes.Contains(c)) makes.Add(c);
                }
                for (int c = 0; c < takes.Length; c++) takes[c] = _stationAccepts[id][c] && c != _passengerCargo;
                break;
            case StopKind.Industry:
                {
                    int c = _industryOutput[_industries[id].Type];
                    if (c >= 0) makes.Add(c);
                    foreach (var (cargo, _) in _industryInputs[_industries[id].Type]) takes[cargo] = true;
                    break;
                }
            default:
                for (int c = 0; c < takes.Length; c++) takes[c] = _exportable[c] && c != _passengerCargo;
                break;
        }
    }

    /// <summary>
    /// The leg of a line between two stops (<see cref="RouteLeg"/>), found the way the vehicles find their way: the quickest chain of lanes from the first stop to
    /// the second over the road network as it is. Null when either stop has no road node (an industry no road reaches).
    /// </summary>
    public RouteLeg? RouteBetween(StopKind fromKind, int fromId, StopKind toKind, int toId)
    {
        SyncNetwork();
        int from = StopNodeOf(fromKind, fromId), to = StopNodeOf(toKind, toId);
        if (from < 0 || to < 0 || from == to) return null;
        var makes = new List<int>();
        var takes = new bool[_cargo.Count];
        StopCargo(fromKind, fromId, makes, new bool[_cargo.Count]);
        StopCargo(toKind, toId, new List<int>(), takes);
        var cargo = makes.Where(c => takes[c]).ToList();

        var field = FieldToNode(to);
        int start = -1;
        float best = float.PositiveInfinity;
        foreach (var roadId in _net.RoadsAt(from))
        {
            var road = _net.GetRoad(roadId);
            int lane = road.RightLaneLeaving(from);
            if (lane < 0) continue;
            if (field[lane] < best) { best = field[lane]; start = lane; }
        }
        var (fx, fz) = StopPoint(fromKind, fromId);
        var (tx, tz) = StopPoint(toKind, toId);
        if (start < 0 || float.IsPositiveInfinity(best))
            return new RouteLeg(false, 0f, MathF.Sqrt(Dist2(fx, fz, tx, tz)), new[] { (fx, fz), (tx, tz) }, cargo);

        var points = new List<(float X, float Z)>();
        float length = 0f;
        int lane2 = start;
        for (int guard = 0; guard < 4000; guard++)
        {
            var lane = _net.GetLane(new LaneId(lane2));
            float step = MathF.Max(2f, MathF.Min(8f, lane.Length / 2f));
            for (float d = 0f; d < lane.Length; d += step) points.Add(lane.Path.PointAt(d));
            length += lane.Length;
            if (_laneEndNode[lane2] == to) { points.Add(lane.Path.PointAt(lane.Length)); break; }
            int first = _nextStart[lane2], count = _nextStart[lane2 + 1] - first;
            int next = -1;
            float bestNext = float.PositiveInfinity;
            for (int k = first; k < first + count; k++)
            {
                float cost = field[_nextLanes[k]] + MoveTime(lane2, k);
                if (cost < bestNext) { bestNext = cost; next = _nextLanes[k]; }
            }
            if (next < 0) break;
            lane2 = next;
        }
        return new RouteLeg(true, best, length, points, cargo);
    }
}
