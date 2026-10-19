using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class StructureTests
{
    const float Level = 10f;

    /// <summary>Level ground at 10 m with one feature centred on x = 0 (the roads run along z = 0).</summary>
    static Terrain WithFeature(Func<float, float> dip) => Terrain.Create(128, 8f, (x, z) => Level + dip(x));

    static float Smooth(float x, float halfWidth) => MathF.Abs(x) >= halfWidth ? 0f : 0.5f * (1f + MathF.Cos(MathF.PI * x / halfWidth));

    static Terrain Gorge() => WithFeature(x => -30f * Smooth(x, 70f));              // a 140 m wide bowl, 30 m deep
    static Terrain River() => WithFeature(x => MathF.Abs(x) < 30f ? -14f : 0f);   // a 60 m river, bed at -4 m, surface at 0
    static Terrain Ridge() => WithFeature(x => 45f * Smooth(x, 80f));              // a hill 45 m high

    static List<Road> Pieces(RoadNetwork net, IReadOnlyList<RoadId> built) => built.Select(id => net.GetRoad(id)).ToList();

    [Fact]
    public void RoadAcrossAGorge_IsGroundBridgeGround_AndTheGroundUnderTheBridgeIsUntouched()
    {
        var terrain = Gorge();
        var net = new RoadNetwork(terrain);
        float floor = terrain.HeightAt(0f, 0f);

        var pieces = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f));

        Assert.Equal(new[] { RoadStructure.None, RoadStructure.Bridge, RoadStructure.None }, pieces.Select(p => p.Structure));
        Assert.Equal(floor, terrain.HeightAt(0f, 0f), 3); // no earthworks under a bridge
        Assert.All(pieces, p => Assert.True(p.Length >= RoadNetwork.MinRoadLength));
        // The deck stays level with the banks; the bridge spans the part that needed more than 6 m of fill, plus margins.
        Assert.All(pieces, p => Assert.Equal(Level, net.GetNodeHeight(p.From), 2));
        Assert.True(pieces[1].Length > 100f, $"bridge is {pieces[1].Length} m");
        Assert.Equal(pieces[0].To, pieces[1].From);
        Assert.Equal(pieces[1].To, pieces[2].From);
    }

    [Fact]
    public void RoadOverARiver_IsABridge_AndAnEndInTheWaterIsRefused()
    {
        var net = new RoadNetwork(River());

        Assert.Empty(net.BuildRoad(0, 0, 200, 0, RoadType.TwoLane, 14f));     // starts in the water
        Assert.Empty(net.BuildRoad(-200, 0, 0, 0, RoadType.TwoLane, 14f));    // ends in the water
        Assert.False(net.CanBuildRoad(0, 100, 150, 100));

        var pieces = Pieces(net, net.BuildRoad(-200, 0, 200, 0, RoadType.TwoLane, 14f));
        var bridge = Assert.Single(pieces, p => p.Structure == RoadStructure.Bridge);
        Assert.True(bridge.Length >= 60f, "the bridge covers the whole river");
        var (bx0, _) = net.GetNode(bridge.From);
        var (bx1, _) = net.GetNode(bridge.To);
        Assert.True(bx0 <= -30f && bx1 >= 30f, $"bridge spans x {bx0} to {bx1}");
    }

    [Fact]
    public void RoadThroughAHill_IsATunnel_WithoutCuttingTheHill()
    {
        var terrain = Ridge();
        var net = new RoadNetwork(terrain);
        float top = terrain.HeightAt(0f, 0f);

        var pieces = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f));

        Assert.Equal(new[] { RoadStructure.None, RoadStructure.Tunnel, RoadStructure.None }, pieces.Select(p => p.Structure));
        Assert.Equal(top, terrain.HeightAt(0f, 0f), 3);
    }

    [Fact]
    public void ABridgeAndATunnelNextToEachOther_BothAppear()
    {
        var terrain = WithFeature(x => x < 0f ? -30f * Smooth(x + 150f, 70f) : 45f * Smooth(x - 150f, 80f));
        var net = new RoadNetwork(terrain);

        var kinds = Pieces(net, net.BuildRoad(-400, 0, 400, 0, RoadType.TwoLane, 14f)).Select(p => p.Structure).ToList();

        Assert.Contains(RoadStructure.Bridge, kinds);
        Assert.Contains(RoadStructure.Tunnel, kinds);
    }

    [Fact]
    public void ANarrowDitch_GetsABridgeNoShorterThanARoadPiece_AndNoPieceIsTooShort()
    {
        var net = new RoadNetwork(WithFeature(x => MathF.Abs(x) < 4f ? -20f : 0f));

        var pieces = Pieces(net, net.BuildRoad(-200, 0, 200, 0, RoadType.TwoLane, 14f));

        Assert.Contains(pieces, p => p.Structure == RoadStructure.Bridge);
        Assert.All(pieces, p => Assert.True(p.Length >= RoadNetwork.MinRoadLength, $"a {p.Length} m piece"));
    }

    [Fact]
    public void Structures_ArePricedPerMetre_InsteadOfRoadAndEarthworks()
    {
        var net = new RoadNetwork(Gorge());
        var prices = net.Tuning.Roads;

        var cost = net.EstimateCost(-300, 0, 300, 0, RoadType.TwoLane)!.Value;
        var built = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f));

        float bridge = built.Where(p => p.Structure == RoadStructure.Bridge).Sum(p => p.Length);
        Assert.Equal(bridge, cost.BridgeLength, 0.01f);
        Assert.Equal(0f, cost.TunnelLength);
        float ground = built.Where(p => p.Structure == RoadStructure.None).Sum(p => p.Length);
        float floor = bridge * prices.BridgeCostPerMetre + ground * prices.TwoLane.CostPerMetre;
        Assert.True(cost.Money >= floor - 1f, "at least the price of the road and the bridge");
        Assert.True(cost.Money < floor + 0.3f * cost.Money, "earthworks only for the short ground ends");
        Assert.Equal(cost.Length, built.Sum(p => p.Length), 0.01f);
    }

    [Fact]
    public void AnAvenueOnAStructure_CostsMoreThanATwoLaneRoad()
    {
        var net = new RoadNetwork(Gorge());

        var two = net.EstimateCost(-300, 0, 300, 0, RoadType.TwoLane)!.Value;
        var four = net.EstimateCost(-300, 0, 300, 0, RoadType.Avenue2x2)!.Value;

        Assert.True(four.Money > two.Money * 1.4f);
        Assert.Equal(two.BridgeLength, four.BridgeLength, 0.01f);
    }

    [Fact]
    public void FlatGround_AndAMapWithoutTerrain_NeverNeedStructures()
    {
        foreach (var net in new[] { new RoadNetwork(Terrain.Flat()), new RoadNetwork() })
            Assert.All(Pieces(net, net.BuildRoad(-200, 0, 200, 0, RoadType.TwoLane, 14f)), p => Assert.Equal(RoadStructure.None, p.Structure));
    }

    // Terrain for crossings: 40 m ground with a gorge down to 10 m along x = 0 (dry: the surface is at 0), or a hill.
    static Terrain DryGorge() => Terrain.Create(128, 8f, (x, z) => 40f - 30f * Smooth(x, 70f));
    static Terrain DryRidge() => Terrain.Create(128, 8f, (x, z) => 40f + 45f * Smooth(x, 80f));
    static Terrain Crater() => Terrain.Create(128, 8f, (x, z) => 40f - 30f * Smooth(MathF.Sqrt(x * x + z * z), 70f));

    [Fact]
    public void ARoadOnTheGroundPassesUnderABridge_WithoutAJunction()
    {
        var net = new RoadNetwork(DryGorge());
        var bridgeRoad = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f));
        Assert.Contains(bridgeRoad, p => p.Structure == RoadStructure.Bridge);
        int roads = net.RoadCount, nodes = net.NodeCount;

        var below = Pieces(net, net.BuildRoad(0, 150, 0, -150, RoadType.TwoLane, 14f));

        var piece = Assert.Single(below);
        Assert.Equal(RoadStructure.None, piece.Structure);
        Assert.Equal(roads + 1, net.RoadCount);   // nothing was split
        Assert.Equal(nodes + 2, net.NodeCount);   // two ends, no node where it passes under
        Assert.All(bridgeRoad, p => Assert.False(p.Retired));
        Assert.Equal(40f - 30f, net.GetNodeHeight(piece.From), 1);
    }

    [Fact]
    public void ABridgeBuiltOverAnExistingRoad_PassesOverItWithoutAJunction()
    {
        var net = new RoadNetwork(DryGorge());
        var below = net.BuildRoad(0, 150, 0, -150, RoadType.TwoLane, 14f);
        int roads = net.RoadCount;

        var over = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f));

        Assert.Equal(new[] { RoadStructure.None, RoadStructure.Bridge, RoadStructure.None }, over.Select(p => p.Structure));
        Assert.Equal(roads + 3, net.RoadCount);
        Assert.False(net.GetRoad(below[0]).Retired);
    }

    [Fact]
    public void ARoadOverAHill_PassesOverATunnelThroughIt()
    {
        var net = new RoadNetwork(DryRidge());
        Assert.Contains(Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f)), p => p.Structure == RoadStructure.Tunnel);

        var over = Pieces(net, net.BuildRoad(0, 150, 0, -150, RoadType.TwoLane, 14f));

        var top = Assert.Single(over);
        Assert.Equal(RoadStructure.None, top.Structure);
        Assert.Equal(85f, net.GetNodeHeight(top.From), 1);
    }

    [Fact]
    public void RoadsThatDoNotClearEachOther_StillMeetInAJunction_OrAreRefused()
    {
        // The ground road beside the gorge is at the bridge's height at its ends: crossing the bridge piece there is a junction.
        var net = new RoadNetwork(DryGorge());
        net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f);
        int roads = net.RoadCount;

        Assert.NotEmpty(net.BuildRoad(-250, 150, -250, -150, RoadType.TwoLane, 14f)); // across the ground part: a normal junction
        Assert.Equal(roads + 4, net.RoadCount); // the crossed road is replaced by two halves, and the new road is two pieces
    }

    [Fact]
    public void AJunctionCanBeBuiltInTheMiddleOfABridge()
    {
        var net = new RoadNetwork(Crater());
        var span = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f));
        Assert.Contains(span, p => p.Structure == RoadStructure.Bridge);

        var side = Pieces(net, net.BuildRoad(0, 250, 0, 0, RoadType.TwoLane, 14f));

        Assert.NotEmpty(side);
        Assert.Equal(RoadStructure.Bridge, side[^1].Structure); // it comes in over the crater on its own bridge
        int junction = side[^1].To;
        Assert.Equal(3, net.RoadsAt(junction).Count);
        var onDeck = net.RoadsAt(junction).Select(id => net.GetRoad(id)).Where(r => r.Structure == RoadStructure.Bridge).ToList();
        Assert.Equal(3, onDeck.Count);        // both halves of the split bridge and the side road
        Assert.Equal(40f, net.GetNodeHeight(junction), 1);
    }

    [Fact]
    public void ARoadMeetingATunnelInTheMiddle_SplitsItIntoTwoTunnels()
    {
        var net = new RoadNetwork(DryRidge());
        net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f);
        var tunnel = net.GetRoad(net.RoadAt(0f, 0f, 2f)!.Value);
        Assert.Equal(RoadStructure.Tunnel, tunnel.Structure);

        // From the hilltop down to the tunnel's level: a steep side road, so give it room.
        var side = net.BuildRoad(0, 400, 0, 0, RoadType.TwoLane, 14f);

        Assert.NotEmpty(side);
        Assert.True(tunnel.Retired);
        int junction = net.FindNodeNear(0f, 0f, 1f);
        Assert.True(junction >= 0);
        Assert.Equal(3, net.RoadsAt(junction).Count);
        Assert.Equal(2, net.RoadsAt(junction).Count(id => net.GetRoad(id).Structure == RoadStructure.Tunnel && id != side[^1]));
    }

    [Fact]
    public void TrafficUnderAndOverACrossing_NeverInteracts()
    {
        var sim = new Simulation(new RoadNetwork(DryGorge()), 12);
        sim.Execute(new BuildRoadCommand(-300, 0, 300, 0, RoadType.TwoLane, 14f));
        sim.Execute(new BuildRoadCommand(0, 150, 0, -150, RoadType.TwoLane, 14f));
        sim.Execute(new SeedTrafficCommand(24));
        for (int i = 0; i < 4000; i++) sim.Step();

        Assert.Empty(sim.Incidents);
    }

    [Fact]
    public void AJunctionCanTakeTheNodeAtTheEndOfABridge()
    {
        var terrain = Gorge();
        var net = new RoadNetwork(terrain);
        var pieces = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f));
        var (px, pz) = net.GetNode(pieces[1].From);

        var side = net.BuildRoad(px, pz, px, pz + 120f, RoadType.TwoLane, 14f);

        Assert.NotEmpty(side);
        Assert.Equal(3, net.RoadsAt(pieces[1].From).Count);
    }

    [Fact]
    public void UpgradingABridge_KeepsItABridge_PricedWithoutEarthworks_AndLeavesTheGround()
    {
        var terrain = Gorge();
        var net = new RoadNetwork(terrain);
        var bridge = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f)).Single(p => p.Structure == RoadStructure.Bridge);
        float floor = terrain.HeightAt(0f, 0f);
        var prices = net.Tuning.Roads;

        var cost = net.EstimateUpgrade(bridge.Id)!.Value;
        var upgraded = net.UpgradeRoad(bridge.Id, 19f);

        Assert.Equal(0f, cost.CutVolume + cost.FillVolume);
        float expected = bridge.Length * (prices.BridgeCostPerMetre * prices.AvenueStructureFactor - prices.UpgradeRefundShare * prices.BridgeCostPerMetre);
        Assert.Equal(expected, cost.Money, 1f);
        Assert.Equal(RoadStructure.Bridge, net.GetRoad(upgraded!.Value).Structure);
        Assert.Equal(RoadType.Avenue2x2, net.GetRoad(upgraded.Value).Type);
        Assert.Equal(floor, terrain.HeightAt(0f, 0f), 3);
        var narrowed = net.DowngradeRoad(upgraded.Value, 14f);
        Assert.Equal(RoadStructure.Bridge, net.GetRoad(narrowed!.Value).Structure);
        var resigned = net.SetSpeedLimit(narrowed.Value, 9f);
        Assert.Equal(RoadStructure.Bridge, net.GetRoad(resigned!.Value).Structure);
    }

    [Fact]
    public void ANodeAtTheEndOfABridgeCanBeMoved_AndTheRoadsAreWorkedOutAgain()
    {
        var net = new RoadNetwork(Gorge());
        var pieces = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f));
        var bridge = pieces[1];

        Assert.NotNull(net.EstimateMoveNode(bridge.From, -250f, 20f));
        Assert.True(net.MoveNode(bridge.From, -250f, 20f));

        Assert.True(net.GetRoad(bridge.Id).Retired);
        int moved = net.FindNodeNear(-250f, 20f, 0.5f);
        Assert.Equal(2, net.RoadsAt(moved).Count);
        var bridges = Enumerable.Range(0, net.RoadCount).Select(r => net.GetRoad(new RoadId(r))).Where(r => !r.Retired && r.Structure == RoadStructure.Bridge).ToList();
        var rebuilt = Assert.Single(bridges); // the bridge was found again for the longer road
        Assert.True(net.GetNode(rebuilt.From).X > -250f);
    }

    [Fact]
    public void TrafficCrossesABridgeAndATunnel_AndCommandsReplay()
    {
        var terrain = WithFeature(x => x < 0f ? -30f * Smooth(x + 150f, 70f) : 45f * Smooth(x - 150f, 80f));
        var sim = new Simulation(new RoadNetwork(terrain), 6);
        Assert.True(sim.Execute(new BuildRoadCommand(-400, 0, 400, 0, RoadType.TwoLane, 14f)) >= 3);
        sim.Execute(new SeedTrafficCommand(30));
        for (int i = 0; i < 2400; i++) sim.Step();

        var copy = new Simulation(new RoadNetwork(Terrain.Create(128, 8f, (x, z) => Level + (x < 0f ? -30f * Smooth(x + 150f, 70f) : 45f * Smooth(x - 150f, 80f)))), 6);
        copy.Replay(sim.CommandLog, sim.Tick);

        Assert.Equal(sim.StateHash(), copy.StateHash());
        Assert.Contains(Enumerable.Range(0, sim.Network.RoadCount).Select(r => sim.Network.GetRoad(new RoadId(r))), r => r.Structure == RoadStructure.Tunnel);
    }

    [Fact]
    public void GeneratedMapWithWater_StillAllowsRoadsOnLand_AndRefusesNodesInWater()
    {
        var terrain = Terrain.Generate(3);
        var net = new RoadNetwork(terrain);
        int tried = 0, built = 0, refusedInWater = 0;
        for (float x = -900; x < 900; x += 90)
            for (float z = -900; z < 900; z += 90)
            {
                tried++;
                var result = net.BuildRoad(x, z, x + 90f, z, RoadType.TwoLane, 14f);
                built += result.Count > 0 ? 1 : 0;
                if ((terrain.IsWater(x, z) || terrain.IsWater(x + 90f, z)) && result.Count == 0) refusedInWater++;
                if (terrain.IsWater(x, z) || terrain.IsWater(x + 90f, z)) Assert.Empty(result);
            }
        Assert.True(built > tried / 4, $"{built} of {tried} roads built");
        Assert.True(refusedInWater > 0, "no attempt touched water");
    }
}
