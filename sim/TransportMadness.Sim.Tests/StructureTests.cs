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

    [Fact]
    public void NoJunctionCanBeBuiltOnABridge_NorCanARoadCrossOne()
    {
        var net = new RoadNetwork(Gorge());
        net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f);

        Assert.Empty(net.BuildRoad(0, 150, 0, 0, RoadType.TwoLane, 14f));   // ends on the bridge
        Assert.Empty(net.BuildRoad(0, 150, 0, -150, RoadType.TwoLane, 14f)); // crosses it
        Assert.False(net.CanBuildRoad(0, 150, 0, -150));
        Assert.NotEmpty(net.BuildRoad(-250, 150, -250, -150, RoadType.TwoLane, 14f)); // across the ground part: a normal junction
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
    public void ANodeWithABridgeCannotBeMoved()
    {
        var net = new RoadNetwork(Gorge());
        var pieces = Pieces(net, net.BuildRoad(-300, 0, 300, 0, RoadType.TwoLane, 14f));

        Assert.False(net.MoveNode(pieces[1].From, -250f, 20f));
        Assert.Null(net.EstimateMoveNode(pieces[1].To, 250f, 20f));
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
