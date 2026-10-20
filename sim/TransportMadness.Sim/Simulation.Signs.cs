namespace TransportMadness.Sim;

/// <summary>What a sign on the approach to a road junction asks of the vehicles that come that way.</summary>
public enum RoadSignKind : byte
{
    /// <summary>Approaches without a sign give way to this one (to the vehicles that have not entered the junction yet).</summary>
    Priority = 1,
    /// <summary>This approach gives way to every approach without a sign, and to every priority one.</summary>
    Yield = 2,
    /// <summary>A yield sign, after a full stop at the line.</summary>
    Stop = 3,
}

/// <summary>A priority, yield or stop sign on one road's approach to a junction, found again by its direction when the roads are edited.</summary>
public sealed class RoadSign
{
    internal RoadSign(int id, int node, float angle, RoadSignKind kind)
    {
        Id = id;
        Node = node;
        Angle = angle;
        Kind = kind;
    }

    public int Id { get; }
    public int Node { get; }
    /// <summary>Heading of the approach seen from the junction (the lane's <see cref="Lane.AngleAtEnd"/>); how the sign finds its road again after the road is split or reshaped.</summary>
    public float Angle { get; }
    public RoadSignKind Kind { get; internal set; }
    /// <summary>False once taken down, or when no road comes that way any more.</summary>
    public bool Active { get; internal set; } = true;
}

/// <summary>
/// Signs at the approaches of road junctions. Every lane running into a junction has a rank: a priority sign makes it 0, no sign 1,
/// a yield or stop sign 2. A vehicle that has not entered a junction ignores the vehicles of lower rank (greater number) that have
/// not entered either, waits for those of higher rank whatever the order of arrival unless it can be through before they get there,
/// and takes those of its own rank by arrival, as at a junction without signs. The ranks form an order, so no ring of vehicles waits
/// for each other because of signs. At a stop sign the vehicle first stands still at the line for <see cref="RoadSignTuning.StopSeconds"/>.
/// Signs at a junction with traffic lights are dormant: the lights decide.
/// </summary>
public sealed partial class Simulation
{
    const float SignTolerance = 0.3f; // radians: how far a road's direction may turn and still be the sign's road

    readonly List<RoadSign> _roadSigns = new();
    int _signCount;
    int _signsSyncedFor = -1;
    byte[] _laneSign = Array.Empty<byte>(); // per lane: the sign of the approach it runs into the junction by, 0 for none

    public IReadOnlyList<RoadSign> RoadSigns => _roadSigns;

    RoadSignTuning SignRules => _net.Tuning.Roads.Signs;

    /// <summary>What it costs to put a sign of this kind up.</summary>
    public long RoadSignPrice(RoadSignKind kind) => kind switch
    {
        RoadSignKind.Priority => SignRules.PriorityPrice,
        RoadSignKind.Yield => SignRules.YieldPrice,
        _ => SignRules.StopPrice,
    };

    /// <summary>The approach a click at (<paramref name="x"/>, <paramref name="z"/>) points at: the lane running into the junction near it that lies nearest the click, or none.</summary>
    public LaneId ApproachNear(float x, float z)
    {
        int node = _net.FindNodeNear(x, z, SignRules.PickRadius, withRoadsOnly: true);
        if (node < 0 || _net.RoadsAt(node).Count < 3) return LaneId.None;
        var best = LaneId.None;
        float bestDistance = float.MaxValue;
        foreach (var laneId in _net.IncomingLanes(node))
        {
            float d = _net.GetLane(laneId).Path.DistanceTo(x, z);
            if (d < bestDistance) { bestDistance = d; best = laneId; }
        }
        return best;
    }

    /// <summary>The kind of sign on the approach of a lane running into a junction, or null.</summary>
    public RoadSignKind? SignOnApproach(LaneId lane)
    {
        var l = _net.GetLane(lane);
        return FindSign(l.EndNode, l.AngleAtEnd)?.Kind;
    }

    /// <summary>The lanes running into the junction by the road a sign stands on (empty when no road comes that way any more).</summary>
    public IReadOnlyList<LaneId> LanesOf(RoadSign sign)
    {
        var lanes = new List<LaneId>();
        if (sign.Node >= _net.NodeCount) return lanes;
        var lane = ResolveApproach(sign.Node, sign.Angle);
        if (lane.IsNone) return lanes;
        var road = _net.GetLane(lane).Road;
        foreach (var other in _net.IncomingLanes(sign.Node))
            if (_net.GetLane(other).Road == road) lanes.Add(other);
        return lanes;
    }

    static float AngleGap(float a, float b)
    {
        float d = MathF.Abs(a - b) % (2f * MathF.PI);
        return d > MathF.PI ? 2f * MathF.PI - d : d;
    }

    /// <summary>The incoming lane of a node whose approach points the way of <paramref name="angle"/>, nearest first, or none when no road comes that way.</summary>
    LaneId ResolveApproach(int node, float angle)
    {
        var best = LaneId.None;
        float bestGap = SignTolerance;
        foreach (var laneId in _net.IncomingLanes(node))
        {
            float gap = AngleGap(_net.GetLane(laneId).AngleAtEnd, angle);
            if (gap < bestGap) { bestGap = gap; best = laneId; }
        }
        return best;
    }

    RoadSign? FindSign(int node, float angle)
    {
        RoadSign? best = null;
        float bestGap = SignTolerance;
        foreach (var sign in _roadSigns)
        {
            if (!sign.Active || sign.Node != node) continue;
            float gap = AngleGap(sign.Angle, angle);
            if (gap < bestGap) { bestGap = gap; best = sign; }
        }
        return best;
    }

    /// <summary>The approach on the other side of the junction from <paramref name="approach"/>, within a quarter turn of straight across, or none.</summary>
    LaneId OppositeApproach(LaneId approach)
    {
        var from = _net.GetLane(approach);
        var best = LaneId.None;
        float bestGap = MathF.PI / 4f;
        foreach (var laneId in _net.IncomingLanes(from.EndNode))
        {
            var lane = _net.GetLane(laneId);
            if (lane.Road == from.Road) continue;
            float gap = AngleGap(lane.AngleAtEnd, from.AngleAtEnd + MathF.PI);
            if (gap < bestGap) { bestGap = gap; best = laneId; }
        }
        return best;
    }

    int BuildRoadSign(float x, float z, RoadSignKind kind)
    {
        var approach = ApproachNear(x, z);
        if (approach.IsNone || LightAt(_net.GetLane(approach).EndNode) != null) return 0;
        var targets = new List<LaneId> { approach };
        if (kind == RoadSignKind.Priority)
        {
            var opposite = OppositeApproach(approach);
            if (!opposite.IsNone) targets.Add(opposite);
        }
        targets.RemoveAll(t => SignOnApproach(t) == kind);
        if (targets.Count == 0) return 0;
        long price = RoadSignPrice(kind) * targets.Count;
        if (!TrySpend(price, LedgerEntry.Construction)) return 0;
        foreach (var target in targets)
        {
            var lane = _net.GetLane(target);
            var existing = FindSign(lane.EndNode, lane.AngleAtEnd);
            if (existing != null) existing.Kind = kind;
            else
            {
                _roadSigns.Add(new RoadSign(_roadSigns.Count, lane.EndNode, lane.AngleAtEnd, kind));
                _signCount++;
            }
        }
        _signsSyncedFor = -1;
        return targets.Count;
    }

    int RemoveRoadSign(float x, float z)
    {
        var approach = ApproachNear(x, z);
        if (approach.IsNone) return 0;
        var lane = _net.GetLane(approach);
        var sign = FindSign(lane.EndNode, lane.AngleAtEnd);
        if (sign == null) return 0;
        var kind = sign.Kind;
        int removed = TakeDown(sign);
        if (kind == RoadSignKind.Priority)
        {
            var opposite = OppositeApproach(approach);
            if (!opposite.IsNone)
            {
                var other = _net.GetLane(opposite);
                if (FindSign(other.EndNode, other.AngleAtEnd) is { Kind: RoadSignKind.Priority } twin) removed += TakeDown(twin);
            }
        }
        _signsSyncedFor = -1;
        return removed;
    }

    int TakeDown(RoadSign sign)
    {
        sign.Active = false;
        _signCount--;
        return 1;
    }

    /// <summary>Works out which sign each lane running into a junction answers to. Done again when the roads or the signs change.</summary>
    void SyncSigns()
    {
        int stamp = _net.Version * 31 + _roadSigns.Count * 7 + _signCount + _lightCount * 13;
        if (stamp == _signsSyncedFor && _laneSign.Length == _net.LaneCount) return;
        _signsSyncedFor = stamp;
        _laneSign = new byte[_net.LaneCount];
        foreach (var sign in _roadSigns)
        {
            if (!sign.Active) continue;
            if (sign.Node >= _net.NodeCount || _net.RoadsAt(sign.Node).Count < 3) continue; // not a junction at the moment
            var lane = ResolveApproach(sign.Node, sign.Angle);
            if (lane.IsNone) { TakeDown(sign); _signsSyncedFor = -1; continue; }   // no road comes that way any more
            if (_lightCount > 0 && LightAt(sign.Node) != null) continue;    // the lights decide
            var road = _net.GetLane(lane).Road;
            foreach (var other in _net.IncomingLanes(sign.Node))
                if (_net.GetLane(other).Road == road) _laneSign[other.Value] = (byte)sign.Kind;
        }
    }

    byte SignOf(int lane) => _signCount > 0 && lane < _laneSign.Length ? _laneSign[lane] : (byte)0;

    /// <summary>0 for the approaches with priority, 1 for those without a sign, 2 for those that give way.</summary>
    static int SignRank(byte sign) => sign == (byte)RoadSignKind.Priority ? 0 : sign == 0 ? 1 : 2;

    ulong SignHash()
    {
        ulong h = (ulong)_roadSigns.Count;
        foreach (var sign in _roadSigns)
            h = h * 31 + (ulong)sign.Node * 7 + (ulong)sign.Kind + (sign.Active ? 1UL : 0UL) + (ulong)(uint)BitConverter.SingleToInt32Bits(sign.Angle);
        return h;
    }
}
