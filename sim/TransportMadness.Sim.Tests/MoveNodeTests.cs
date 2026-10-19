using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class MoveNodeTests
{
    /// <summary>Three arms from a centre node: west, east and north, 200 m each.</summary>
    static (RoadNetwork Net, int Centre, int West, int East, int North) Tee()
    {
        var net = new RoadNetwork();
        int centre = net.AddNode(0, 0), west = net.AddNode(-200, 0), east = net.AddNode(200, 0), north = net.AddNode(0, -200);
        net.AddRoad(west, centre, RoadType.TwoLane, 14f);
        net.AddRoad(centre, east, RoadType.TwoLane, 14f);
        net.AddRoad(centre, north, RoadType.Avenue2x2, 19f);
        return (net, centre, west, east, north);
    }

    [Fact]
    public void MovingTheCentre_RebuildsEveryRoadAtItsNewPlace_KeepingTypeAndLimit()
    {
        var (net, centre, west, east, north) = Tee();

        Assert.True(net.MoveNode(centre, 20f, 40f));

        Assert.Empty(net.RoadsAt(centre));
        int moved = net.FindNodeNear(20f, 40f, 0.5f);
        Assert.True(moved >= 0);
        Assert.Equal(3, net.RoadsAt(moved).Count);
        var toNorth = net.GetRoad(net.FindRoad(moved, north)!.Value);
        Assert.Equal(RoadType.Avenue2x2, toNorth.Type);
        Assert.Equal(19f, toNorth.SpeedLimit);
        Assert.Equal(14f, net.GetRoad(net.FindRoad(moved, west)!.Value).SpeedLimit);
        Assert.NotNull(net.FindRoad(moved, east));
        Assert.True(MathF.Abs(net.GetRoad(net.FindRoad(moved, east)!.Value).Length - MathF.Sqrt(180f * 180f + 40f * 40f)) < 0.1f);
    }

    [Fact]
    public void MoveNode_RefusesWhatCannotWork_AndChangesNothing()
    {
        var (net, centre, west, east, north) = Tee();
        int roads = net.RoadCount;

        Assert.False(net.MoveNode(centre, 0.4f, 0f));          // hardly moved
        Assert.False(net.MoveNode(centre, 200f, 0f));          // onto another node
        Assert.False(net.MoveNode(centre, -195f, 0f));         // roads of 5 m: too short
        Assert.False(net.MoveNode(centre, 400f, 0f));         // the west and east roads would both leave heading west, on top of each other
        Assert.False(net.MoveNode(north, 0f, -200.2f));        // hardly moved
        Assert.Null(net.EstimateMoveNode(centre, 200f, 0f));

        Assert.Equal(roads, net.RoadCount);
        Assert.Equal(3, net.RoadsAt(centre).Count);
        Assert.All(Enumerable.Range(0, roads), r => Assert.False(net.GetRoad(new RoadId(r)).Retired));
    }

    [Fact]
    public void MoveNode_CanDragAcrossAnotherRoad_WhichThenJoinsTheMovedRoad()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(-100, -200);
        net.AddRoad(a, b, RoadType.TwoLane, 14f);
        int c = net.AddNode(-300, 0), d = net.AddNode(300, 0);
        net.AddRoad(c, d, RoadType.TwoLane, 14f);
        int e = net.AddNode(-150, 100), f = net.AddNode(150, 100);
        var crossed = net.AddRoad(e, f, RoadType.TwoLane, 14f);

        Assert.NotNull(net.EstimateMoveNode(a, 0f, 250f));
        Assert.True(net.MoveNode(a, 0f, 250f)); // the road to b now crosses the road e-f

        Assert.True(net.GetRoad(crossed).Retired);
        int junction = net.FindNodeNear(-100f * 150f / 450f, 100f, 5f);
        Assert.True(junction >= 0, "a junction forms where the dragged road crosses");
        Assert.Equal(4, net.RoadsAt(junction).Count);
        Assert.True(net.FindNodeNear(-100f * 250f / 450f, 0f, 5f) >= 0, "and where it crosses the road c-d");
    }

    [Fact]
    public void MoveNode_StillRefusesAMoveIntoWater_OrOntoAnotherRoadsEnd()
    {
        var terrain = Terrain.Create(128, 8f, (x, z) => x > 100f ? -5f : 10f);
        var net = new RoadNetwork(terrain);
        int a = net.AddNode(0, 0), b = net.AddNode(-100, 0), c = net.AddNode(0, 100);
        net.AddRoad(a, b, RoadType.TwoLane, 14f);
        net.AddRoad(a, c, RoadType.TwoLane, 14f);

        Assert.False(net.MoveNode(a, 150f, 0f));
        Assert.Null(net.EstimateMoveNode(a, 150f, 0f));
        Assert.True(net.MoveNode(a, 40f, 30f));
    }

    [Fact]
    public void MovedCurves_KeepTheirBend()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(200, 0), c = net.AddNode(0, 200);
        net.AddRoad(a, b, RoadType.TwoLane, 14f, 0.8f);
        net.AddRoad(c, a, RoadType.TwoLane, 14f, -0.5f);

        Assert.True(net.MoveNode(a, 20f, 30f, out int moved));

        var toB = net.GetRoad(net.FindRoad(moved, b)!.Value);
        var fromC = net.GetRoad(net.FindRoad(c, moved)!.Value);
        Assert.Equal(0.8f, toB.Geometry.Sweep, 3);
        Assert.Equal(-0.5f, fromC.Geometry.Sweep, 3);
        Assert.Equal(moved, toB.From);
        Assert.Equal(moved, fromC.To);
    }

    [Fact]
    public void MovingANode_ThatMakesACurveTooTight_IsRefused()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(200, 0);
        net.AddRoad(a, b, RoadType.TwoLane, 14f, 2.0f); // radius about 119 m

        Assert.False(net.MoveNode(a, 170f, 0f)); // chord of 30 m with the same bend: radius 18 m
        Assert.True(net.MoveNode(a, 100f, 0f));
    }

    [Fact]
    public void EstimateMoveNode_IsDemolitionPlusNewConstruction_AndMatchesWhatMoveDoes()
    {
        var (net, centre, _, _, _) = Tee();
        var prices = net.Tuning.Roads;
        float demolition = (200f + 200f + 200f) * prices.DemolitionCostPerMetre;

        var cost = net.EstimateMoveNode(centre, 0f, 50f);

        Assert.NotNull(cost);
        float built = MathF.Sqrt(200f * 200f + 50f * 50f) * 2f * prices.TwoLane.CostPerMetre + 250f * prices.Avenue.CostPerMetre;
        Assert.InRange(cost!.Value.Money, demolition + built - 1f, demolition + built + 1f);
        Assert.InRange(cost.Value.Length, 2f * MathF.Sqrt(200f * 200f + 50f * 50f) + 250f - 0.1f, 2f * MathF.Sqrt(200f * 200f + 50f * 50f) + 250f + 0.1f);
    }

    [Fact]
    public void VehiclesOnTheMovedRoads_CarryOnInProportion_AndADepotFollowsItsNode()
    {
        var (net, centre, west, east, _) = Tee();
        var sim = new Simulation(net, 8);
        sim.Execute(new AddDepotCommand(centre, DepotLevel.Simple));
        var eastRoad = net.GetRoad(net.FindRoad(centre, east)!.Value);
        // On the east arm: 50 m along the way out, and 50 m along the way in.
        int outbound = sim.SpawnVehicle(eastRoad.ForwardLanes[0], 50f, 0f);
        int inbound = sim.SpawnVehicle(eastRoad.BackwardLanes[0], 50f, 0f);
        sim.SetHalted(outbound, true);
        sim.SetHalted(inbound, true);

        Assert.Equal(1, sim.Execute(new MoveNodeCommand(centre, 0f, 60f)));
        sim.Step();

        int moved = net.FindNodeNear(0f, 60f, 0.5f);
        Assert.True(sim.Depots[0].Active);
        Assert.Equal(moved, sim.Depots[0].Node);
        var newEast = net.GetRoad(net.FindRoad(moved, east)!.Value);
        float scale = newEast.Length / eastRoad.Length;
        var vo = sim.GetVehicle(outbound);
        var vi = sim.GetVehicle(inbound);
        Assert.Equal(newEast.ForwardLanes[0], vo.Lane);
        Assert.Equal(newEast.BackwardLanes[0], vi.Lane);
        Assert.Equal(50f * scale, vo.Position, 0.1f);
        Assert.Equal(50f * scale, vi.Position, 0.1f);
    }

    [Fact]
    public void TrafficSurvivesAMove_NothingStandsOnATornDownLane_AndRunsOn()
    {
        var (net, centre, west, east, _) = Tee();
        int far = net.AddNode(-600, 0);
        net.AddRoad(far, west, RoadType.TwoLane, 14f); // an untouched road beyond the west arm
        var sim = new Simulation(net, 8);
        sim.Execute(new AddDepotCommand(centre, DepotLevel.Simple));
        sim.SeedTraffic(40);
        for (int i = 0; i < 200; i++) sim.Step();
        int before = 0;
        for (int i = 0; i < sim.VehicleCount; i++) if (sim.GetVehicle(i).Kind != VehicleKind.Removed) before++;

        Assert.Equal(1, sim.Execute(new MoveNodeCommand(centre, 0f, 60f)));
        sim.Step();

        int alive = 0;
        for (int i = 0; i < sim.VehicleCount; i++)
        {
            var v = sim.GetVehicle(i);
            if (v.Kind == VehicleKind.Removed) continue;
            alive++;
            Assert.False(net.GetLane(v.Lane).Retired);
        }
        Assert.True(alive >= before - 4, $"{alive} of {before} vehicles kept"); // only those packed onto each other by a shorter road are lost
        for (int i = 0; i < 1000; i++) sim.Step();
    }

    [Fact]
    public void MoveNodeCommand_IsLogged_RefusedOnesToo_AndReplays()
    {
        var net = new RoadNetwork();
        var sim = new Simulation(net, 2);
        sim.Execute(new BuildRoadCommand(0, 0, 200, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(0, 0, 0, 200, RoadType.TwoLane, 14f));
        Assert.Equal(0, sim.Execute(new MoveNodeCommand(99, 0, 0)));
        Assert.Equal(0, sim.Execute(new MoveNodeCommand(0, 0, 0)));
        Assert.Equal(1, sim.Execute(new MoveNodeCommand(0, -30f, -30f)));
        sim.Execute(new SeedTrafficCommand(10));
        for (int i = 0; i < 100; i++) sim.Step();

        var copy = new Simulation(new RoadNetwork(), 2);
        copy.Replay(sim.CommandLog, sim.Tick);

        Assert.Equal(sim.StateHash(), copy.StateHash());
    }

    [Fact]
    public void OnHills_EveryAcceptedMoveKeepsEachRoadWithinTheGradeLimit_AndCarvesTheGround()
    {
        int accepted = 0;
        for (float z = -300f; z <= 300f; z += 50f)
        {
            var terrain = Terrain.Generate(5, water: false);
            var net = new RoadNetwork(terrain);
            int a = net.AddNode(-400, 0), b = net.AddNode(0, 0), c = net.AddNode(400, 0);
            net.AddRoad(a, b, RoadType.TwoLane, 14f);
            net.AddRoad(b, c, RoadType.TwoLane, 14f);
            terrain.TakeChangedChunks();

            bool estimated = net.EstimateMoveNode(b, 0f, z) != null;
            bool moved = net.MoveNode(b, 0f, z);

            Assert.Equal(estimated, moved);
            if (!moved) continue;
            accepted++;
            int node = net.FindNodeNear(0f, z, 0.5f);
            foreach (var id in net.RoadsAt(node))
            {
                var road = net.GetRoad(id);
                float rise = MathF.Abs(net.GetNodeHeight(road.To) - net.GetNodeHeight(road.From));
                Assert.True(rise <= RoadNetwork.MaxGrade * road.Length + 1e-3f, $"road to z={z} is steeper than allowed");
            }
        }
        Assert.True(accepted > 0, "no target on these hills was accepted");
    }
}

public class JunctionPointTests
{
    static (RoadNetwork Net, Road In, Road Out) Corner()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(-100, 0), b = net.AddNode(0, 0), c = net.AddNode(0, 100);
        var inRoad = net.GetRoad(net.AddRoad(a, b, RoadType.TwoLane, 14f));
        var outRoad = net.GetRoad(net.AddRoad(b, c, RoadType.TwoLane, 14f));
        return (net, inRoad, outRoad);
    }

    [Fact]
    public void ThePathRunsFromWhereTheIncomingLaneEntersTheJunctionToWhereTheOutgoingOneLeaves()
    {
        var (net, inRoad, outRoad) = Corner();
        var from = inRoad.ForwardLanes[0];
        var to = outRoad.ForwardLanes[0];

        var (x0, z0, h0) = net.JunctionPoint(from, to, 0f);
        var (x1, z1, h1) = net.JunctionPoint(from, to, 1f);

        var (sx, sz, sh) = net.LanePoint(from, net.GetLane(from).Length - RoadNetwork.JunctionReach(net.GetLane(from).Length));
        var (ex, ez, eh) = net.LanePoint(to, RoadNetwork.JunctionReach(net.GetLane(to).Length));
        Assert.True(Dist(x0, z0, sx, sz) < 0.01f);
        Assert.True(Dist(x1, z1, ex, ez) < 0.01f);
        Assert.True(AngleDifference(h0, sh) < 0.2f, "enters along the incoming lane");
        Assert.True(AngleDifference(h1, eh) < 0.2f, "leaves along the outgoing lane");
    }

    [Fact]
    public void TheTurnIsSmooth_NoJumpsInPositionOrHeading()
    {
        var (net, inRoad, outRoad) = Corner();
        var from = inRoad.ForwardLanes[0];
        var to = outRoad.ForwardLanes[0];

        var previous = net.JunctionPoint(from, to, 0f);
        for (int i = 1; i <= 40; i++)
        {
            var point = net.JunctionPoint(from, to, i / 40f);
            Assert.True(Dist(previous.X, previous.Z, point.X, point.Z) < 1.5f, $"jump at {i}");
            Assert.True(AngleDifference(previous.Heading, point.Heading) < 0.35f, $"heading jump at {i}");
            previous = point;
        }
        // A right-hand corner: the heading turns a quarter circle in total.
        Assert.InRange(AngleDifference(net.JunctionPoint(from, to, 0f).Heading, net.JunctionPoint(from, to, 1f).Heading), MathF.PI / 2 - 0.3f, MathF.PI / 2 + 0.3f);
    }

    [Fact]
    public void ADrawnVehicleCrossingTheNode_RemembersWhereItCameFrom()
    {
        var (net, inRoad, outRoad) = Corner();
        var sim = new Simulation(net, 1);
        int car = sim.SpawnVehicle(inRoad.ForwardLanes[0], 95f, 10f);
        Assert.True(sim.PreviousLaneOf(car).IsNone);

        for (int i = 0; i < 40; i++) sim.Step();

        Assert.Equal(inRoad.ForwardLanes[0], sim.PreviousLaneOf(car));
        Assert.Equal(outRoad.ForwardLanes[0], sim.GetVehicle(car).Lane);
    }

    static float Dist(float x0, float z0, float x1, float z1) => MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));

    static float AngleDifference(float a, float b)
    {
        float d = MathF.Abs(a - b) % MathF.Tau;
        return MathF.Min(d, MathF.Tau - d);
    }
}

public class TerrainChunkTests
{
    [Fact]
    public void CarvingARoad_MarksOnlyTheChunksNearIt_AndTakingThemClearsTheList()
    {
        var terrain = Terrain.Generate(3, water: false);
        var net = new RoadNetwork(terrain);
        Assert.Empty(terrain.TakeChangedChunks());

        net.BuildRoad(-200, -200, -100, -200, RoadType.TwoLane, 14f);
        var changed = terrain.TakeChangedChunks();

        Assert.NotEmpty(changed);
        Assert.True(changed.Count <= 4, $"{changed.Count} chunks for a 100 m road");
        Assert.Equal(changed.OrderBy(c => c), changed);
        Assert.All(changed, c => Assert.InRange(c, 0, terrain.ChunksPerSide * terrain.ChunksPerSide - 1));
        Assert.Empty(terrain.TakeChangedChunks());
    }

    [Fact]
    public void ARoadOnFlatGroundChangesNothing_SoNoChunkIsMarked()
    {
        var terrain = Terrain.Flat();
        new RoadNetwork(terrain).BuildRoad(0, 0, 100, 0, RoadType.TwoLane, 14f);

        Assert.Empty(terrain.TakeChangedChunks());
    }

    [Fact]
    public void ChunksCoverTheWholeMap()
    {
        var terrain = Terrain.Flat();

        Assert.Equal(16, terrain.ChunksPerSide);
        Assert.True(terrain.ChunksPerSide * Terrain.ChunkCells >= terrain.Cells);
    }
}

public class FindNodeTests
{
    [Fact]
    public void WithRoadsOnly_SkipsNodesNoRoadReachesAnyMore()
    {
        var net = new RoadNetwork();
        int a = net.AddNode(0, 0), b = net.AddNode(100, 0), c = net.AddNode(20, 0);
        var road = net.AddRoad(a, b, RoadType.TwoLane, 14f);

        Assert.Equal(c, net.FindNodeNear(21, 0, 30));              // c is nearest, but nothing uses it
        Assert.Equal(a, net.FindNodeNear(21, 0, 30, withRoadsOnly: true));
        Assert.Equal(-1, net.FindNodeNear(300, 0, 30, withRoadsOnly: true));
        net.RemoveRoad(road);
        Assert.Equal(-1, net.FindNodeNear(0, 0, 30, withRoadsOnly: true));
    }
}
