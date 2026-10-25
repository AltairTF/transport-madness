namespace TransportMadness.Sim;

public sealed partial class Simulation
{
    /// <summary>
    /// A road built with an end on a highway (road-types GDD): the road stops short of it and an off-ramp and an on-ramp join it to the highway. Null when
    /// the road is an ordinary one; <paramref name="refusal"/> says why when it cannot be joined.
    /// </summary>
    public RoadNetwork.Interchange? PlanInterchange(float x0, float z0, float x1, float z1, RoadType type, float sweep, out string? refusal) =>
        _net.PlanInterchange(x0, z0, x1, z1, sweep, type, out refusal);

    /// <summary>The pieces of an interchange as (geometry, half width): the road and the two ramps, for finding the buildings in their way.</summary>
    List<(RoadGeometry Geometry, float Half)> InterchangeRoads(RoadNetwork.Interchange ic, RoadType type, int lanesForward, int lanesBack, float sweep) => new()
    {
        (RoadGeometry.FromChord(ic.X0, ic.Z0, ic.JX, ic.JZ, ic.SideSweep), RoadNetwork.HalfWidth(type, lanesForward, lanesBack)),
        (RoadGeometry.FromChord(ic.AX, ic.AZ, ic.JX, ic.JZ), RoadNetwork.HalfWidth(RoadType.Ramp)),
        (RoadGeometry.FromChord(ic.JX, ic.JZ, ic.BX, ic.BZ), RoadNetwork.HalfWidth(RoadType.Ramp)),
    };

    /// <summary>What the interchange of a road costs with the buildings it takes down, or null when it cannot be built.</summary>
    public (RoadCost Cost, Demolition Demolition)? EstimateInterchange(RoadNetwork.Interchange ic, RoadType type, int lanesForward = 0, int lanesBack = 0)
    {
        if (_net.EstimateInterchange(ic, type, lanesForward, lanesBack) is not { } cost) return null;
        return (cost, DemolitionOf(BuildingsInTheWay(InterchangeRoads(ic, type, lanesForward, lanesBack, 0f))));
    }

    /// <summary>
    /// <see cref="BuildRoadCommand"/> for a road that ends on a highway: the road, its off-ramp and its on-ramp, all or nothing. Returns the pieces built
    /// (0 when refused). The on-ramp gives way to the highway where it joins.
    /// </summary>
    int BuildInterchange(BuildRoadCommand c, RoadNetwork.Interchange ic)
    {
        if (_net.EstimateInterchange(ic, c.Type, c.LanesForward, c.LanesBack) is not { } cost) return 0;
        var inTheWay = BuildingsInTheWay(InterchangeRoads(ic, c.Type, c.LanesForward, c.LanesBack, c.Sweep));
        int crossings = 0;
        if (RailInUse)
        {
            var pieces = new[] { (ic.X0, ic.Z0, ic.JX, ic.JZ, ic.SideSweep, c.Type), (ic.AX, ic.AZ, ic.JX, ic.JZ, 0f, RoadType.Ramp), (ic.JX, ic.JZ, ic.BX, ic.BZ, 0f, RoadType.Ramp) };
            foreach (var (x0, z0, x1, z1, sweep, type) in pieces)
            {
                if (_net.PlannedPoints(x0, z0, x1, z1, sweep) is not { } plan) return 0;
                if (!RoadCrossingsAllowed(RoadGeometry.FromChord(x0, z0, x1, z1, sweep), plan, out int n)) return 0;
                crossings += n;
            }
        }
        if (!CanAfford(cost.Money + DemolitionOf(inTheWay).Price + crossings * _railTuning.Crossings.Price)) return 0;
        var (side, off, on) = _net.BuildInterchange(ic, c.Type, c.SpeedLimit, c.LanesForward, c.LanesBack);
        if (side.Count == 0 || off.Count == 0 || on.Count == 0) throw new InvalidOperationException("A planned interchange could not be built.");
        Charge(Price(cost.Money), LedgerEntry.Construction);
        DemolishForRoad(inTheWay);
        if (RailInUse) AddCrossingsFound();

        // The on-ramp gives way where it joins the highway.
        var lane = _net.GetLane(_net.GetRoad(on[^1]).ForwardLanes[0]);
        _roadSigns.Add(new RoadSign(_roadSigns.Count, lane.EndNode, lane.AngleAtEnd, RoadSignKind.Yield));
        _signCount++;
        _signsSyncedFor = -1;
        SyncNetwork();
        return side.Count + off.Count + on.Count;
    }
}
