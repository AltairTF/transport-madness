namespace TransportMadness.Sim;

/// <summary>An industry or a road gateway, as the thing a road is joined to (C10).</summary>
public readonly record struct AccessRef(bool Gateway, int Id);

/// <summary>A short road of the type being built from a point of the new road to an access point it passes (C10), with what it is estimated to cost.</summary>
public readonly record struct AccessSpur(AccessRef Target, float FromX, float FromZ, float ToX, float ToZ, float Money, int Demolished, long DemolitionPrice);

/// <summary>
/// How a road being built is joined to the industries and gateways near it (C10): the road with its ends drawn to the access points within reach,
/// and the spurs to the access points it passes. <see cref="Start"/> and <see cref="End"/> say which access point an end was drawn to.
/// </summary>
public sealed record RoadJoin(float X0, float Z0, float X1, float Z1, AccessRef? Start, AccessRef? End, IReadOnlyList<AccessSpur> Spurs)
{
    /// <summary>The price of the spurs, their demolitions included.</summary>
    public float SpurMoney => Spurs.Sum(s => s.Money + s.DemolitionPrice);
}

/// <summary>
/// Access points (C10). An industry, and a gateway of the road network, meets the roads at a point on the edge of its square that faces the nearest town.
/// A road built with an end within <c>access.snapRadius</c> of the point is drawn to it, and a road that passes within that distance gets a spur of
/// its own type to it. The point becomes a node of the road network when a road reaches it; nothing about it is kept apart from the network, so it is
/// found again from the position. Stations and depots stand on nodes already; what a station covers does not depend on any of this.
/// </summary>
public sealed partial class Simulation
{
    readonly AccessTuning _accessTuning;
    const float AccessOnNode = 0.5f; // an end this close to a node is on it (the network's own tolerance)

    /// <summary>Works out where an industry meets the roads, once it stands.</summary>
    void SetAccessPoint(Industry industry) =>
        (industry.AccessX, industry.AccessZ) = AccessPointOf(industry.X, industry.Z, industry.Angle, _industryTuning.Types[industry.Type].Size);

    /// <summary>
    /// The point <c>edgeMargin</c> outside the edge of a square of half-side <paramref name="size"/> that faces the nearest town (the way it is
    /// heading when there is no town). The square turns with <paramref name="angle"/>, as it is drawn.
    /// </summary>
    (float X, float Z) AccessPointOf(float x, float z, float angle, float size)
    {
        float fx = MathF.Cos(angle), fz = MathF.Sin(angle);
        float dx = fx, dz = fz;
        float best = float.MaxValue;
        foreach (var town in _towns)
        {
            float d = (town.X - x) * (town.X - x) + (town.Z - z) * (town.Z - z);
            if (d >= best || d < 1e-6f) continue;
            best = d;
            float length = MathF.Sqrt(d);
            dx = (town.X - x) / length;
            dz = (town.Z - z) / length;
        }
        // Where the direction leaves the square: scale it until its larger part along or across the square reaches the half-side.
        float along = dx * fx + dz * fz, across = -dx * fz + dz * fx;
        float scale = size / MathF.Max(MathF.Abs(along), MathF.Abs(across));
        return (x + dx * (scale + _accessTuning.EdgeMargin), z + dz * (scale + _accessTuning.EdgeMargin));
    }

    /// <summary>The access points a road can be joined to, in order: industries, then road gateways (a rail gateway is not on the road network).</summary>
    IEnumerable<(AccessRef Ref, float X, float Z)> AccessPoints()
    {
        foreach (var i in _industries) yield return (new AccessRef(false, i.Id), i.AccessX, i.AccessZ);
        foreach (var g in _gateways)
            if (g.Kind == GatewayKind.Road) yield return (new AccessRef(true, g.Id), g.AccessX, g.AccessZ);
    }

    /// <summary>Where an industry or gateway meets the roads.</summary>
    public (float X, float Z) AccessPointOf(AccessRef target) =>
        target.Gateway ? (_gateways[target.Id].AccessX, _gateways[target.Id].AccessZ) : (_industries[target.Id].AccessX, _industries[target.Id].AccessZ);

    /// <summary>The node of the road network at the access point of an industry or gateway, or -1 while no road reaches it.</summary>
    public int AccessNodeOf(AccessRef target)
    {
        var (x, z) = AccessPointOf(target);
        return _net.FindNodeNear(x, z, AccessOnNode, withRoadsOnly: true);
    }

    /// <summary>The access node of an industry, or -1 (see <see cref="AccessNodeOf(AccessRef)"/>).</summary>
    public int AccessNodeOf(Industry industry) => AccessNodeOf(new AccessRef(false, industry.Id));

    /// <summary>The access node of a gateway, or -1.</summary>
    public int AccessNodeOf(Gateway gateway) => AccessNodeOf(new AccessRef(true, gateway.Id));

    /// <summary>
    /// How a road from (x0, z0) to (x1, z1) would be joined to the industries and gateways near it, by the rule <see cref="BuildRoadCommand"/> applies:
    /// an end that is not on a node and lies within <c>access.snapRadius</c> of an access point is moved onto it (the nearer end when both would go to the
    /// same point), and the road gets a spur of its type to every other access point within that distance of it. A spur starts where the road is nearest to
    /// the point, and is left out when that is within a road piece's minimum length of a junction or an end of the road, when the road already passes
    /// that close to the point, or when it cannot be built. An end is only moved when the road can still be built; the spur prices are estimates
    /// against the network as it is now.
    /// </summary>
    public RoadJoin JoinRoad(float x0, float z0, float x1, float z1, RoadType type, float sweep = 0f)
    {
        var points = AccessPoints().ToList();
        if (points.Count == 0) return new RoadJoin(x0, z0, x1, z1, null, null, Array.Empty<AccessSpur>());
        var start = NearestAccess(points, x0, z0);
        var end = NearestAccess(points, x1, z1);
        if (start is { } s && end is { } e && s.Ref == e.Ref)
        {
            if (Dist2(s.X, s.Z, x0, z0) <= Dist2(e.X, e.Z, x1, z1)) end = null; else start = null;
        }
        // The first of these that can be built; the plain road is the last.
        var tries = new List<((AccessRef Ref, float X, float Z)? Start, (AccessRef Ref, float X, float Z)? End)>();
        if (start != null && end != null) tries.Add((start, end));
        if (start != null) tries.Add((start, null));
        if (end != null) tries.Add((null, end));
        foreach (var (a, b) in tries)
        {
            float ax = a?.X ?? x0, az = a?.Z ?? z0, bx = b?.X ?? x1, bz = b?.Z ?? z1;
            if (_net.EstimateCost(ax, az, bx, bz, type, sweep) == null) continue;
            return new RoadJoin(ax, az, bx, bz, a?.Ref, b?.Ref, SpursOf(points, ax, az, bx, bz, type, sweep, a?.Ref, b?.Ref));
        }
        return new RoadJoin(x0, z0, x1, z1, null, null, SpursOf(points, x0, z0, x1, z1, type, sweep, null, null));
    }

    /// <summary>The access point within reach of an end of a road, unless the end is on a node already.</summary>
    (AccessRef Ref, float X, float Z)? NearestAccess(List<(AccessRef Ref, float X, float Z)> points, float x, float z)
    {
        if (_net.FindNodeNear(x, z, AccessOnNode) >= 0) return null;
        (AccessRef Ref, float X, float Z)? best = null;
        float bestDistance = _accessTuning.SnapRadius * _accessTuning.SnapRadius;
        foreach (var p in points)
        {
            float d = Dist2(p.X, p.Z, x, z);
            if (d <= bestDistance) { best = p; bestDistance = d; }
        }
        return best;
    }

    static float Dist2(float ax, float az, float bx, float bz) => (ax - bx) * (ax - bx) + (az - bz) * (az - bz);

    List<AccessSpur> SpursOf(List<(AccessRef Ref, float X, float Z)> points, float x0, float z0, float x1, float z1, RoadType type, float sweep, AccessRef? start, AccessRef? end)
    {
        var spurs = new List<AccessSpur>();
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        var meetings = _net.PlannedPoints(x0, z0, x1, z1, sweep);
        if (meetings == null) return spurs;
        float minimum = RoadNetwork.MinRoadLength;
        foreach (var p in points)
        {
            if (p.Ref == start || p.Ref == end) continue;
            float along = geometry.NearestAlong(p.X, p.Z);
            var (fx, fz) = geometry.PointAt(along);
            float distance = MathF.Sqrt(Dist2(fx, fz, p.X, p.Z));
            if (distance > _accessTuning.SnapRadius || distance < minimum) continue;
            if (along < minimum || along > geometry.Length - minimum) continue;
            bool nearMeeting = false;
            foreach (var m in meetings)
                if (MathF.Abs(m.Along - along) < minimum) nearMeeting = true;
            if (nearMeeting || _net.EstimateCost(fx, fz, p.X, p.Z, type) is not { } cost) continue;
            var demolition = EstimateRoadDemolition(fx, fz, p.X, p.Z, type);
            spurs.Add(new AccessSpur(p.Ref, fx, fz, p.X, p.Z, cost.Money, demolition.Count, demolition.Price));
        }
        return spurs;
    }

    /// <summary>
    /// <see cref="BuildRoadCommand"/>: the road as <see cref="JoinRoad"/> draws it, then its spurs. The road must be affordable together with the spurs
    /// as estimated; a spur that has become unaffordable or impossible by the time its turn comes is left out and the road stays. The result counts the
    /// pieces of the road itself.
    /// </summary>
    int BuildRoadWithAccess(BuildRoadCommand c)
    {
        var join = JoinRoad(c.X0, c.Z0, c.X1, c.Z1, c.Type, c.Sweep);
        int built = BuildRoadPiece(join.X0, join.Z0, join.X1, join.Z1, c.Type, c.SpeedLimit, c.Sweep, join.SpurMoney);
        if (built == 0) return 0;
        foreach (var spur in join.Spurs)
            BuildRoadPiece(spur.FromX, spur.FromZ, spur.ToX, spur.ToZ, c.Type, c.SpeedLimit, 0f, 0f);
        return built;
    }

    /// <summary>
    /// Builds one road piece sequence and charges it: refused when it cannot be built, would cross rails where that is not allowed, or cannot be
    /// afforded together with <paramref name="reserve"/> (the spurs to come). Returns the pieces built.
    /// </summary>
    int BuildRoadPiece(float x0, float z0, float x1, float z1, RoadType type, float speedLimit, float sweep, float reserve)
    {
        var cost = _net.EstimateCost(x0, z0, x1, z1, type, sweep);
        if (cost == null) return 0;
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        var inTheWay = BuildingsInTheWay(new[] { (geometry, type) });
        int crossings = 0;
        if (RailInUse && (_net.PlannedPoints(x0, z0, x1, z1, sweep) is not { } plan || !RoadCrossingsAllowed(geometry, plan, out crossings))) return 0;
        if (!CanAfford(cost.Value.Money + DemolitionOf(inTheWay).Price + crossings * _railTuning.Crossings.Price + reserve)) return 0;
        int built = _net.BuildRoad(x0, z0, x1, z1, type, speedLimit, sweep).Count;
        if (built > 0)
        {
            Charge(Price(cost.Value.Money), LedgerEntry.Construction);
            DemolishForRoad(inTheWay);
            if (RailInUse) AddCrossingsFound();
        }
        return built;
    }
}
