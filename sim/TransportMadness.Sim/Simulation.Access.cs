namespace TransportMadness.Sim;

/// <summary>An industry or a road gateway, as the thing a road is joined to (C10).</summary>
public readonly record struct AccessRef(bool Gateway, int Id);

/// <summary>
/// The stone road of type <see cref="RoadType.Path"/> from a point of the road being built to an access point within reach (M6e), with what it is
/// estimated to cost. A connection that cannot be built (<see cref="Buildable"/> false) costs nothing and is shown as such in the preview;
/// <see cref="Cost"/> has its length and earthworks.
/// </summary>
public readonly record struct AccessSpur(AccessRef Target, float FromX, float FromZ, float ToX, float ToZ, float Money, int Demolished, long DemolitionPrice, bool Buildable = true, RoadCost? Cost = null);

/// <summary>
/// How a road being built is joined to the industries and gateways near it (M6e): the road itself is never moved; each access point within reach that
/// no road reaches yet gets a connection of its own. The ends are those given.
/// </summary>
public sealed record RoadJoin(float X0, float Z0, float X1, float Z1, IReadOnlyList<AccessSpur> Spurs)
{
    /// <summary>The price of the connections that can be built, their demolitions included.</summary>
    public float SpurMoney => Spurs.Where(s => s.Buildable).Sum(s => s.Money + s.DemolitionPrice);
}

/// <summary>
/// Access points (C10, M6e). An industry, and a gateway of the road network, meets the roads at a point on the edge of its square that faces the nearest
/// town. A road is built exactly where it is put; one that passes within <c>access.snapRadius</c> of a point no road reaches yet gets a connection of
/// type <see cref="RoadType.Path"/> from the nearest point of it to the access point. The point becomes a node of the road network when the connection
/// is built; nothing about it is kept apart from the network, so it is found again from the position. Stations and depots stand on nodes already; what a
/// station covers does not depend on any of this.
/// </summary>
public sealed partial class Simulation
{
    readonly AccessTuning _accessTuning;
    /// <summary>How far outside the wall of its square an access point is.</summary>
    public float AccessEdgeMargin => _accessTuning.EdgeMargin;
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
    /// the road is never moved, and every access point within <c>access.snapRadius</c> of it that no road reaches yet gets a connection of type
    /// <see cref="RoadType.Path"/> from where the road is nearest to the point. Where that is within a road piece's minimum length of a junction or an end
    /// of the road the connection starts there instead, and it never starts inside a bridge or a tunnel (it starts at the end of it nearest the point).
    /// An access point the road passes closer than a piece's minimum length gets none. A connection that cannot be built is listed as not buildable.
    /// The prices are estimates against the network as it is now.
    /// </summary>
    public RoadJoin JoinRoad(float x0, float z0, float x1, float z1, RoadType type, float sweep = 0f)
    {
        var spurs = SpursOf(x0, z0, x1, z1, sweep);
        return new RoadJoin(x0, z0, x1, z1, spurs);
    }

    static float Dist2(float ax, float az, float bx, float bz) => (ax - bx) * (ax - bx) + (az - bz) * (az - bz);

    List<AccessSpur> SpursOf(float x0, float z0, float x1, float z1, float sweep)
    {
        var spurs = new List<AccessSpur>();
        var points = AccessPoints().ToList();
        if (points.Count == 0) return spurs;
        var plan = _net.PlannedRuns(x0, z0, x1, z1, sweep);
        if (plan == null) return spurs;
        var geometry = RoadGeometry.FromChord(x0, z0, x1, z1, sweep);
        float minimum = RoadNetwork.MinRoadLength;
        foreach (var p in points)
        {
            if (AccessNodeOf(p.Ref) >= 0) continue; // a road reaches it already
            float along = geometry.NearestAlong(p.X, p.Z);
            var (fx, fz) = ConnectionStart(plan, geometry, along, p.X, p.Z, minimum);
            float distance = MathF.Sqrt(Dist2(fx, fz, p.X, p.Z));
            if (distance > _accessTuning.SnapRadius || distance < minimum) continue;
            const RoadType type = RoadType.Path;
            if (_net.EstimateCost(fx, fz, p.X, p.Z, type) is not { } cost)
            {
                spurs.Add(new AccessSpur(p.Ref, fx, fz, p.X, p.Z, 0f, 0, 0, Buildable: false));
                continue;
            }
            var demolition = EstimateRoadDemolition(fx, fz, p.X, p.Z, type);
            spurs.Add(new AccessSpur(p.Ref, fx, fz, p.X, p.Z, cost.Money, demolition.Count, demolition.Price, true, cost));
        }
        return spurs;
    }

    /// <summary>Where a connection to (px, pz) starts on a planned road: the nearest point of it, moved to the meeting beside it when that is closer than a piece's minimum length, and out of any structure.</summary>
    static (float X, float Z) ConnectionStart(IReadOnlyList<(float Along, float X, float Z, float Y, bool Junction, RoadStructure After)> plan, RoadGeometry geometry, float along, float px, float pz, float minimum)
    {
        int i = 0;
        while (i + 2 < plan.Count && plan[i + 1].Along < along) i++;
        var a = plan[i];
        var b = plan[Math.Min(i + 1, plan.Count - 1)];
        if (a.After != RoadStructure.None)
            return Dist2(a.X, a.Z, px, pz) <= Dist2(b.X, b.Z, px, pz) ? (a.X, a.Z) : (b.X, b.Z);
        if (along - a.Along < minimum) return (a.X, a.Z);
        if (b.Along - along < minimum) return (b.X, b.Z);
        return geometry.PointAt(along);
    }

    /// <summary>
    /// <see cref="BuildRoadCommand"/>: the road exactly as given, then the connections <see cref="JoinRoad"/> lists. The road must be affordable together
    /// with the connections as estimated; one that has become unaffordable or impossible by the time its turn comes is left out and the road stays. The
    /// result counts the pieces of the road itself.
    /// </summary>
    int BuildRoadWithAccess(BuildRoadCommand c)
    {
        var join = JoinRoad(c.X0, c.Z0, c.X1, c.Z1, c.Type, c.Sweep);
        int built = BuildRoadPiece(join.X0, join.Z0, join.X1, join.Z1, c.Type, c.SpeedLimit, c.Sweep, join.SpurMoney);
        if (built == 0) return 0;
        foreach (var spur in join.Spurs)
            if (spur.Buildable) BuildRoadPiece(spur.FromX, spur.FromZ, spur.ToX, spur.ToZ, RoadType.Path, _net.Tuning.Roads.Path.SpeedLimit, 0f, 0f);
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
