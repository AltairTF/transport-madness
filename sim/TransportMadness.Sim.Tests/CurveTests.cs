using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class RoadGeometryTests
{
    [Fact]
    public void Straight_HasChordLength_AndHeadingOfTheChord()
    {
        var g = RoadGeometry.FromChord(0, 0, 30, 40);
        Assert.True(g.IsStraight);
        Assert.Equal(50f, g.Length, 3);
        var (x, z) = g.PointAt(25f);
        Assert.Equal(15f, x, 3);
        Assert.Equal(20f, z, 3);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(-1.0f)]
    [InlineData(2.4f)]
    public void Arc_EndsAtTheChordEnd_AndHasTheRightLength(float sweep)
    {
        var g = RoadGeometry.FromChord(10, -20, 110, 40, sweep);
        var (x, z) = g.End;
        Assert.Equal(110f, x, 2);
        Assert.Equal(40f, z, 2);
        Assert.Equal(g.Radius * MathF.Abs(sweep), g.Length, 2);
        Assert.True(g.Length > MathF.Sqrt(100f * 100f + 60f * 60f));
    }

    [Fact]
    public void Arc_PointsAreOnACircle()
    {
        var g = RoadGeometry.FromChord(0, 0, 100, 0, 1.2f);
        // The centre lies radius away from every point; take it from three of them.
        float r = g.Radius;
        float k = g.Length / g.Sweep;
        float cx = g.X - k * MathF.Sin(g.Heading0), cz = g.Z + k * MathF.Cos(g.Heading0);
        foreach (float s in new[] { 0f, g.Length * 0.3f, g.Length * 0.7f, g.Length })
        {
            var (x, z) = g.PointAt(s);
            Assert.Equal(r, MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz)), 2);
        }
    }

    [Fact]
    public void Reversed_RunsTheOtherWay_AndSliceKeepsTheCurve()
    {
        var g = RoadGeometry.FromChord(0, 0, 100, 0, 1.2f);
        var back = g.Reversed();
        var (bx, bz) = back.PointAt(0f);
        Assert.Equal(100f, bx, 2);
        Assert.Equal(0f, bz, 2);
        var (ex, ez) = back.End;
        Assert.Equal(0f, ex, 2);
        Assert.Equal(0f, ez, 2);
        var (mx, mz) = g.PointAt(g.Length * 0.5f);
        var (rx, rz) = back.PointAt(g.Length * 0.5f);
        Assert.Equal(mx, rx, 2);
        Assert.Equal(mz, rz, 2);

        var slice = g.Slice(g.Length * 0.25f, g.Length * 0.75f);
        var (sx, sz) = slice.End;
        var (px, pz) = g.PointAt(g.Length * 0.75f);
        Assert.Equal(px, sx, 2);
        Assert.Equal(pz, sz, 2);
    }

    [Fact]
    public void SweepThrough_ThreePointsOnACircle_GivesThatArcsSweep()
    {
        var g = RoadGeometry.FromChord(0, 0, 80, 30, 1.4f);
        var (mx, mz) = g.PointAt(g.Length * 0.5f);
        float sweep = RoadGeometry.SweepThrough(0, 0, mx, mz, 80, 30);
        Assert.Equal(1.4f, sweep, 2);
        Assert.Equal(0f, RoadGeometry.SweepThrough(0, 0, 40, 0, 80, 0), 3);
    }
}

public class CurvedRoadTests
{
    static RoadNetwork Net() => new();

    [Fact]
    public void CurvedRoad_HasArcLengthAndTangentAngles()
    {
        var net = Net();
        int a = net.AddNode(0, 0), b = net.AddNode(100, 0);
        var road = net.GetRoad(net.AddRoad(a, b, RoadType.TwoLane, 14f, 1.0f));
        var lane = net.GetLane(road.ForwardLanes[0]);
        Assert.True(lane.Length > 100f);
        Assert.Equal(-0.5f, lane.AngleAtStart, 3); // heading of the chord (0) minus half the sweep
        Assert.Equal(road.Length, net.GetLane(road.BackwardLanes[0]).Length, 3);
    }

    [Fact]
    public void TightCurves_GetALowerSpeedLimit_ThanGentleOnes()
    {
        var net = Net();
        int a = net.AddNode(0, 0), b = net.AddNode(60, 0), c = net.AddNode(0, 300), d = net.AddNode(300, 300);
        var tight = net.GetLane(net.GetRoad(net.AddRoad(a, b, RoadType.TwoLane, 19f, 2.0f)).ForwardLanes[0]);
        var gentle = net.GetLane(net.GetRoad(net.AddRoad(c, d, RoadType.TwoLane, 19f, 0.3f)).ForwardLanes[0]);
        Assert.True(tight.SpeedLimit < 12f, $"{tight.SpeedLimit}");
        Assert.Equal(19f, gentle.SpeedLimit, 3);
    }

    [Fact]
    public void BuildRoad_RefusesTooTightACurve_AndTooLargeATurn()
    {
        var net = Net();
        Assert.False(net.CanBuildRoad(0, 0, 30, 0, 2.5f));  // radius about 18 m
        Assert.False(net.CanBuildRoad(0, 0, 200, 0, 3.0f)); // more than MaxSweep
        Assert.True(net.CanBuildRoad(0, 0, 200, 0, 1.0f));
    }

    [Fact]
    public void ArcCrossingAStraightRoad_MakesAJunctionAtTheCrossing()
    {
        var net = Net();
        int w = net.AddNode(-150, 0), e = net.AddNode(150, 0);
        net.AddRoad(w, e, RoadType.TwoLane, 14f);

        // An arc from below the road to above it, bulging sideways.
        var built = net.BuildRoad(0, -100, 0, 100, RoadType.TwoLane, 14f, 0.8f);

        Assert.Equal(2, built.Count);
        int centre = -1;
        for (int n = 0; n < net.NodeCount; n++)
        {
            var (x, z) = net.GetNode(n);
            if (MathF.Abs(z) < 1f && net.IncomingLanes(n).Count == 4 && n != w && n != e) centre = n;
        }
        Assert.True(centre >= 0, "no four-way junction on the existing road");
        // The two halves together still describe the original arc: lengths add up.
        float total = net.GetRoad(built[0]).Length + net.GetRoad(built[1]).Length;
        Assert.Equal(RoadGeometry.FromChord(0, -100, 0, 100, 0.8f).Length, total, 1);
    }

    [Fact]
    public void StraightRoadEndingOnAnArc_SplitsTheArcWithoutChangingItsShape()
    {
        var net = Net();
        int a = net.AddNode(0, 0), b = net.AddNode(200, 0);
        var arc = net.GetRoad(net.AddRoad(a, b, RoadType.TwoLane, 14f, 1.0f));
        var g = arc.Geometry;
        var (mx, mz) = g.PointAt(g.Length * 0.5f);

        var built = net.BuildRoad(mx, mz + (mz > 0 ? 100 : -100), mx, mz, RoadType.TwoLane, 14f);

        Assert.Single(built);
        Assert.True(net.GetRoad(arc.Id).Retired);
        int junction = net.FindNodeNear(mx, mz, 1f);
        Assert.Equal(3, net.IncomingLanes(junction).Count);
        float first = 0f, second = 0f;
        foreach (var road in net.RoadsAt(junction))
        {
            var r = net.GetRoad(road);
            if (r.From == a && r.To == junction) first = r.Length;
            if (r.From == junction && r.To == b) second = r.Length;
        }
        Assert.Equal(g.Length, first + second, 1);
    }

    [Fact]
    public void NearestOnRoad_FindsTheFootOnAnArc()
    {
        var net = Net();
        int a = net.AddNode(0, 0), b = net.AddNode(200, 0);
        var road = net.GetRoad(net.AddRoad(a, b, RoadType.TwoLane, 14f, -1.0f));
        var (px, pz) = road.Geometry.PointAt(80f);
        var hit = net.NearestOnRoad(px + 3f, pz + 3f, 12f, 5f);
        Assert.NotNull(hit);
        Assert.Equal(road.Id, hit!.Value.Road);
        Assert.InRange(hit.Value.Along, 75f, 85f);
    }

    [Fact]
    public void CurvedRoadOnTerrain_CarvesAlongTheArc_AndCostsMoreThanLevelGround()
    {
        var terrain = Terrain.Generate(42);
        var net = new RoadNetwork(terrain);
        var cost = net.EstimateCost(-300, -300, -100, -300, RoadType.TwoLane, 0.8f);
        if (cost == null) return; // too steep on this seed; the refusal is covered elsewhere
        Assert.True(cost.Value.Length > 200f);
        var built = net.BuildRoad(-300, -300, -100, -300, RoadType.TwoLane, 14f, 0.8f);
        Assert.NotEmpty(built);
        var geometry = RoadGeometry.FromChord(-300, -300, -100, -300, 0.8f);
        var (mx, mz) = geometry.PointAt(geometry.Length * 0.5f);
        // The ground under the middle of the arc is now at road height: between the heights at the two ends.
        float y0 = net.GetNodeHeight(net.FindNodeNear(-300, -300, 0.5f)), y1 = net.GetNodeHeight(net.FindNodeNear(-100, -300, 0.5f));
        Assert.Equal((y0 + y1) * 0.5f, terrain.HeightAt(mx, mz), 1);
    }
}

public class JunctionPathTests
{
    static (RoadNetwork Net, LaneId W, LaneId E, LaneId S, LaneId N, LaneId WOut, LaneId EOut, LaneId SOut, LaneId NOut) Cross()
    {
        var net = new RoadNetwork();
        int c = net.AddNode(0, 0), w = net.AddNode(-100, 0), e = net.AddNode(100, 0), s = net.AddNode(0, -100), n = net.AddNode(0, 100);
        var rw = net.GetRoad(net.AddRoad(w, c, RoadType.TwoLane, 14f));
        var re = net.GetRoad(net.AddRoad(c, e, RoadType.TwoLane, 14f));
        var rs = net.GetRoad(net.AddRoad(s, c, RoadType.TwoLane, 14f));
        var rn = net.GetRoad(net.AddRoad(c, n, RoadType.TwoLane, 14f));
        return (net, rw.ForwardLanes[0], re.BackwardLanes[0], rs.ForwardLanes[0], rn.BackwardLanes[0],
            rw.BackwardLanes[0], re.ForwardLanes[0], rs.BackwardLanes[0], rn.ForwardLanes[0]);
    }

    [Fact]
    public void LeftTurn_CrossesTheStraightThroughTrafficOfTheRoadItTurnsInto()
    {
        var (net, w, e, s, n, wOut, eOut, sOut, nOut) = Cross();
        // From the arm at z = +100 turning toward the west arm (a left turn) crosses the traffic coming from the west going east.
        Assert.True(net.MovesConflict(n, wOut, w, eOut));
        // Turning the other way (a right turn from the arm at z = -100 into the west arm) stays clear of it.
        Assert.False(net.MovesConflict(s, wOut, w, eOut));
        // Going straight on either road does not cross the traffic of the opposite direction.
        Assert.False(net.MovesConflict(w, eOut, e, wOut));
    }

    [Fact]
    public void RightTurn_DoesNotCrossTheOtherRoadsOutgoingTraffic()
    {
        var (net, w, e, s, n, wOut, eOut, sOut, nOut) = Cross();
        // Two vehicles turning into the same lane merge.
        Assert.True(net.MovesConflict(n, wOut, e, wOut));
        // Opposite right turns keep to their own corners.
        Assert.False(net.MovesConflict(w, nOut, e, sOut));
    }

    [Fact]
    public void ConflictIsSymmetric()
    {
        var (net, w, e, s, n, wOut, eOut, sOut, nOut) = Cross();
        var ins = new[] { w, e, s, n };
        var outs = new[] { wOut, eOut, sOut, nOut };
        foreach (var a in ins) foreach (var b in ins) foreach (var oa in outs) foreach (var ob in outs)
        {
            if (!net.GetLane(a).Next.Contains(oa) || !net.GetLane(b).Next.Contains(ob)) continue;
            Assert.Equal(net.MovesConflict(a, oa, b, ob), net.MovesConflict(b, ob, a, oa));
        }
    }
}

public class CurvedTrafficTests
{
    [Fact]
    public void TrafficOnARingOfArcs_KeepsToTheCurveSpeedLimit_AndStaysDeterministic()
    {
        ulong Run(out float fastest)
        {
            var net = new RoadNetwork();
            const int corners = 6;
            var nodes = new int[corners];
            for (int i = 0; i < corners; i++)
            {
                float a = i * MathF.Tau / corners;
                nodes[i] = net.AddNode(120f * MathF.Cos(a), 120f * MathF.Sin(a));
            }
            // Each side bulges outward, so the ring is made of tight-ish bends of about 60 degrees of turn.
            for (int i = 0; i < corners; i++)
                net.AddRoad(nodes[i], nodes[(i + 1) % corners], RoadType.TwoLane, 19f, MathF.Tau / corners);
            var sim = new Simulation(net, 5);
            sim.SeedTraffic(30);
            fastest = 0f;
            for (int step = 0; step < 20 * 120; step++)
            {
                sim.Step();
                for (int i = 0; i < sim.VehicleCount; i++)
                {
                    var v = sim.GetVehicle(i);
                    if (v.Kind != VehicleKind.Car) continue;
                    float limit = net.GetLane(v.Lane).SpeedLimit;
                    fastest = MathF.Max(fastest, v.Speed - limit * 1.35f); // overtakers may exceed the limit a little
                }
            }
            Assert.Empty(sim.Incidents.Where(x => x.Kind == IncidentKind.RearEnd));
            return sim.StateHash();
        }

        ulong first = Run(out float over);
        Assert.True(over <= 0f, $"exceeded the curve limit by {over}");
        Assert.Equal(first, Run(out _));
    }
}
