namespace TransportMadness.Sim;

/// <summary>How a free-standing depot is joined to the roads (M6f).</summary>
public enum DepotConnection
{
    /// <summary>No road within reach: the depot stands alone and is not connected until a road reaches its access point.</summary>
    None,
    /// <summary>A road is near: the depot stands against it and its access point is on the road (split there, or at the node nearest).</summary>
    Beside,
    /// <summary>A road is within reach: a Path joins the road to the depot's access point.</summary>
    Path,
}

/// <summary>
/// Where a depot clicked for would stand and how it would be joined to the roads, with what that costs. <see cref="X"/>, <see cref="Z"/> is its middle,
/// <see cref="Angle"/> the way its door faces. For <see cref="DepotConnection.Beside"/> the road is split at (<see cref="SplitRoad"/>, <see cref="SplitAlong"/>)
/// unless <see cref="Node"/> is a node already. For <see cref="DepotConnection.Path"/> the Path runs from (<see cref="PathFromX"/>, <see cref="PathFromZ"/>) to the
/// access point; <see cref="PathBuildable"/> is false when it cannot be built (the depot can still be built, not connected).
/// </summary>
public readonly record struct DepotPlan(
    bool Valid, string Reason, DepotConnection Connection, float X, float Z, float Angle, float AccessX, float AccessZ,
    int Node, RoadId SplitRoad, float SplitAlong, float PathFromX, float PathFromZ, bool PathBuildable, RoadCost? PathCost, int Demolished, long DemolitionPrice)
{
    /// <summary>The price of the Path, its demolitions included; 0 when there is none to build.</summary>
    public float PathMoney => Connection == DepotConnection.Path && PathBuildable ? (PathCost?.Money ?? 0f) + DemolitionPrice : 0f;
}

public sealed partial class Simulation
{
    readonly record struct RoadSpot(RoadId Road, float Along, int Node, float X, float Z, float Distance);

    /// <summary>
    /// The nearest point of a road within <paramref name="radius"/> of (x, z) where something can join it: a node when the nearest point is within a road
    /// piece's minimum length of an end of its road (a junction cannot be built nearer), else the point on the road.
    /// </summary>
    RoadSpot? NearestRoadSpot(float x, float z, float radius)
    {
        RoadSpot? best = null;
        float bestDistance = radius;
        float minimum = RoadNetwork.MinRoadLength;
        for (int r = 0; r < _net.RoadCount; r++)
        {
            var road = _net.GetRoad(new RoadId(r));
            if (road.Retired) continue;
            float along = road.Geometry.NearestAlong(x, z);
            int node = -1;
            if (along < minimum) { along = 0f; node = road.From; }
            else if (along > road.Length - minimum) { along = road.Length; node = road.To; }
            var (px, pz) = node >= 0 ? _net.GetNode(node) : road.Geometry.PointAt(along);
            float d = MathF.Sqrt(Dist2(px, pz, x, z));
            if (d < bestDistance) { best = new RoadSpot(road.Id, along, node, px, pz, d); bestDistance = d; }
        }
        return best;
    }

    /// <summary>
    /// How a depot clicked for at (x, z) would be placed (see <see cref="DepotPlan"/>): within <c>crews.depotBesideRadius</c> of a road it stands against it,
    /// else with a road within reach of its access point (<c>access.snapRadius</c>) it gets a Path from the nearest point of that road, else it stands alone
    /// with its access point toward the nearest town. A depot cannot stand in water or where an open depot already has its access node.
    /// </summary>
    public DepotPlan PlanDepot(float x, float z)
    {
        float reach = _crew.DepotHalfSize + _accessTuning.EdgeMargin; // from the middle to the access point
        DepotPlan Invalid(string why) => new(false, why, DepotConnection.None, x, z, 0f, x, z, -1, default, 0f, 0f, 0f, false, null, 0, 0);

        if (_net.Terrain is { } terrain && terrain.IsWater(x, z)) return Invalid("it cannot stand in water");
        var spot = NearestRoadSpot(x, z, _accessTuning.SnapRadius + reach);
        if (spot is { } s && s.Distance <= _crew.DepotBesideRadius)
        {
            var (dx, dz) = s.Distance > 1e-3f ? ((s.X - x) / s.Distance, (s.Z - z) / s.Distance) : RoadNormal(s);
            float cx = s.X - dx * reach, cz = s.Z - dz * reach;
            if (_net.Terrain is { } t2 && t2.IsWater(cx, cz)) return Invalid("it cannot stand in water");
            if (s.Node >= 0)
                foreach (var d in _depots)
                    if (d.Active && d.Node == s.Node) return Invalid("a depot has this node already");
            return new DepotPlan(true, "", DepotConnection.Beside, cx, cz, MathF.Atan2(dz, dx), s.X, s.Z, s.Node, s.Road, s.Along, 0f, 0f, false, null, 0, 0);
        }
        if (spot is { } p)
        {
            float ddx = (p.X - x) / p.Distance, ddz = (p.Z - z) / p.Distance;
            float ax = x + ddx * reach, az = z + ddz * reach;
            RoadCost? cost = _net.EstimateCost(p.X, p.Z, ax, az, RoadType.Path);
            var demolition = cost == null ? default : EstimateRoadDemolition(p.X, p.Z, ax, az, RoadType.Path);
            return new DepotPlan(true, "", DepotConnection.Path, x, z, MathF.Atan2(ddz, ddx), ax, az, -1, default, 0f, p.X, p.Z, cost != null, cost, demolition.Count, demolition.Price);
        }
        var (tx, tz) = AccessPointOf(x, z, 0f, _crew.DepotHalfSize);
        return new DepotPlan(true, "", DepotConnection.None, x, z, MathF.Atan2(tz - z, tx - x), tx, tz, -1, default, 0f, 0f, 0f, false, null, 0, 0);
    }

    (float X, float Z) RoadNormal(RoadSpot s)
    {
        var g = _net.GetRoad(s.Road).Geometry;
        var (ax, az) = g.PointAt(MathF.Max(0f, s.Along - 1f));
        var (bx, bz) = g.PointAt(MathF.Min(g.Length, s.Along + 1f));
        float tx = bx - ax, tz = bz - az, length = MathF.Max(1e-4f, MathF.Sqrt(tx * tx + tz * tz));
        return (-tz / length, tx / length);
    }

    /// <summary>
    /// <see cref="BuildDepotCommand"/>: the depot where <see cref="PlanDepot"/> puts it, with its Path or its road split, for the depot's price (and the
    /// Path's, which is charged as a road). Refused when it cannot be paid for together.
    /// </summary>
    int BuildFreeDepot(BuildDepotCommand c)
    {
        SyncNetwork();
        var plan = PlanDepot(c.X, c.Z);
        if (!plan.Valid || !Enum.IsDefined(c.Level)) return -1;
        long price = _economy.DepotPrice.For(c.Level);
        if (!CanAfford(price + plan.PathMoney)) return -1;

        int node = -1;
        if (plan.Connection == DepotConnection.Beside)
        {
            node = plan.Node >= 0 ? plan.Node : _net.SplitRoad(plan.SplitRoad, plan.SplitAlong);
            SyncNetwork();
        }
        if (!TrySpend(price, LedgerEntry.Depots)) return -1;
        var depot = new Depot(_depots.Count, node, c.Level, _crew.PerDepot.For(c.Level))
        {
            FreeStanding = true, X = plan.X, Z = plan.Z, Angle = plan.Angle, AccessX = plan.AccessX, AccessZ = plan.AccessZ, Active = false,
        };
        _depots.Add(depot);
        if (plan.Connection == DepotConnection.Path && plan.PathBuildable)
            BuildRoadPiece(plan.PathFromX, plan.PathFromZ, plan.AccessX, plan.AccessZ, RoadType.Path, _net.Tuning.Roads.Path.SpeedLimit, 0f, 0f);
        SyncNetwork();
        UpdateDepotConnections();
        return depot.Id;
    }
}
