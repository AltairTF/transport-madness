using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class TerrainTests
{
    [Fact]
    public void SameSeed_GivesTheSameMap_AndADifferentSeedDoesNot()
    {
        Assert.Equal(Terrain.Generate(7).Hash(), Terrain.Generate(7).Hash());
        Assert.NotEqual(Terrain.Generate(7).Hash(), Terrain.Generate(8).Hash());
    }

    [Fact]
    public void GeneratedMap_HasPlainsHillsAndWater()
    {
        var terrain = Terrain.Generate(42);
        float min = float.MaxValue, max = float.MinValue;
        int flat = 0, wet = 0, total = 0;
        for (int j = 0; j < terrain.Cells; j++)
            for (int i = 0; i < terrain.Cells; i++)
            {
                float h = terrain.GetSample(i, j);
                min = Math.Min(min, h); max = Math.Max(max, h);
                total++;
                if (h < Terrain.WaterLevel) wet++;
                else if (h < 4f) flat++;
            }
        Assert.True(max > 30f, $"highest point {max}");
        Assert.True(min < Terrain.WaterLevel - 2f, $"lowest point {min}");
        Assert.InRange(flat / (float)total, 0.1f, 0.8f);
        Assert.InRange(wet / (float)total, 0.02f, 0.45f);
    }

    [Fact]
    public void WithoutWater_NothingLiesBelowTheSurface()
    {
        var terrain = Terrain.Generate(42, water: false);
        for (int j = 0; j < terrain.Cells; j++)
            for (int i = 0; i < terrain.Cells; i++)
                Assert.True(terrain.GetSample(i, j) >= Terrain.WaterLevel);
    }

    [Fact]
    public void IsWater_FollowsTheGround_AndCreateShapesAMap()
    {
        var terrain = Terrain.Create(64, 10f, (x, z) => MathF.Abs(x) < 50f ? -3f : 5f); // a 100 m wide river along z

        Assert.True(terrain.IsWater(0f, 100f));
        Assert.False(terrain.IsWater(200f, 100f));
        Assert.Equal(5f, terrain.HeightAt(200f, 0f), 3);
    }

    [Fact]
    public void HeightAt_InterpolatesBetweenSamples_AndClampsOutsideTheMap()
    {
        var terrain = Terrain.Generate(3, cells: 16, cellSize: 10f);
        float x = -terrain.HalfSize + 10f * 5, z = -terrain.HalfSize + 10f * 6;
        Assert.Equal(terrain.GetSample(5, 6), terrain.HeightAt(x, z), 3);
        float mid = terrain.HeightAt(x + 5f, z);
        Assert.Equal((terrain.GetSample(5, 6) + terrain.GetSample(6, 6)) * 0.5f, mid, 3);
        Assert.Equal(terrain.GetSample(0, 0), terrain.HeightAt(-1e6f, -1e6f), 3);
    }

    [Fact]
    public void CarveRoad_LevelsTheCarriageway_AndBumpsTheVersion()
    {
        var terrain = Terrain.Generate(42);
        int before = terrain.Version;
        terrain.CarveRoad(-100f, 0f, 20f, 100f, 0f, 20f, 3.5f, 6f);
        Assert.NotEqual(before, terrain.Version);
        Assert.Equal(20f, terrain.HeightAt(0f, 0f), 0);
        Assert.Equal(20f, terrain.HeightAt(50f, 1f), 0);
    }
}

public class TerrainRoadTests
{
    static RoadNetwork OnSlope(float rise, out int a, out int b)
    {
        var net = new RoadNetwork();
        a = net.AddNode(0, 0, 0f);
        b = net.AddNode(100, 0, rise);
        return net;
    }

    [Fact]
    public void LaneGrade_IsRiseOverRun_AndOppositeInTheOtherDirection()
    {
        var net = OnSlope(8f, out int a, out int b);
        var road = net.GetRoad(net.AddRoad(a, b, RoadType.TwoLane, 14f));
        Assert.Equal(0.08f, net.GetLane(road.ForwardLanes[0]).Grade, 4);
        Assert.Equal(-0.08f, net.GetLane(road.BackwardLanes[0]).Grade, 4);
    }

    [Fact]
    public void SteepLanes_AreSlowerThanLevelOnes()
    {
        var level = OnSlope(2f, out int a, out int b);
        var steep = OnSlope(12f, out int c, out int d);
        float levelLimit = level.GetLane(level.GetRoad(level.AddRoad(a, b, RoadType.TwoLane, 14f)).ForwardLanes[0]).SpeedLimit;
        float steepLimit = steep.GetLane(steep.GetRoad(steep.AddRoad(c, d, RoadType.TwoLane, 14f)).ForwardLanes[0]).SpeedLimit;
        Assert.Equal(14f, levelLimit, 3);
        Assert.InRange(steepLimit, 7f, 13f);
    }

    [Fact]
    public void BuildRoad_RefusesAGradeAboveTheLimit()
    {
        var net = OnSlope(20f, out int a, out int b); // 20 %
        Assert.False(net.CanBuildRoad(0, 0, 100, 0));
        Assert.Empty(net.BuildRoad(0, 0, 100, 0, RoadType.TwoLane, 14f));
    }

    [Fact]
    public void SplittingARoad_PutsTheNewNodeOnTheRoadSurface()
    {
        var net = OnSlope(10f, out int a, out int b);
        net.AddRoad(a, b, RoadType.TwoLane, 14f);
        net.BuildRoad(50, -60, 50, 0, RoadType.TwoLane, 14f);
        int junction = net.FindNodeNear(50, 0, 0.5f);
        Assert.Equal(5f, net.GetNodeHeight(junction), 3);
    }

    [Fact]
    public void BuildRoad_OnTerrain_CarvesTheGroundAndNodesStandOnIt()
    {
        var terrain = Terrain.Generate(42);
        var net = new RoadNetwork(terrain);
        float y0 = terrain.HeightAt(-200, 0), y1 = terrain.HeightAt(0, 0);
        var built = net.BuildRoad(-200, 0, 0, 0, RoadType.TwoLane, 14f);
        if (built.Count == 0) { Assert.True(MathF.Abs(y1 - y0) > RoadNetwork.MaxGrade * 200f); return; }
        Assert.Equal(y0, net.GetNodeHeight(net.FindNodeNear(-200, 0, 0.5f)), 3);
        Assert.Equal((y0 + y1) * 0.5f, terrain.HeightAt(-100, 0), 1);
    }

    [Fact]
    public void Cost_RisesWithEarthworks_AndEqualsTheBasePriceOnLevelGround()
    {
        var flat = new RoadNetwork(Terrain.Flat());
        var level = flat.EstimateCost(0, 0, 200, 0, RoadType.TwoLane)!.Value;
        Assert.Equal(0f, level.CutVolume);
        Assert.Equal(0f, level.FillVolume);
        Assert.Equal(200f * 40f, level.Money, 1);

        var terrain = Terrain.Generate(42);
        var hilly = new RoadNetwork(terrain);
        // Find a straight 300 m stretch that is buildable but not level.
        RoadCost? best = null;
        for (float x = -600; x < 600 && best == null; x += 50)
        {
            var cost = hilly.EstimateCost(x, 300, x + 300, 300, RoadType.TwoLane);
            if (cost is { } c && (c.CutVolume + c.FillVolume) > 100f) best = c;
        }
        Assert.NotNull(best);
        Assert.True(best!.Value.Money > 300f * 40f);
    }

    [Fact]
    public void EstimateCost_IsNull_WhenTheRoadCannotBeBuilt()
    {
        Assert.Null(new RoadNetwork().EstimateCost(0, 0, 3, 0, RoadType.TwoLane));
    }

    [Fact]
    public void FlatWorld_IsUnchanged_ByHavingATerrain()
    {
        var net = new RoadNetwork(Terrain.Flat());
        var built = net.BuildRoad(0, 0, 200, 0, RoadType.TwoLane, 14f);
        Assert.Single(built);
        Assert.Equal(14f, net.GetLane(net.GetRoad(built[0]).ForwardLanes[0]).SpeedLimit, 3);
    }
}
