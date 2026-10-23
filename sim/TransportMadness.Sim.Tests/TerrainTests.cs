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
                float surface = terrain.WaterSurfaceAtSample(i, j);
                if (h < surface) wet++;
                else if (h < surface + 4f) flat++;
            }
        Assert.True(max > 30f, $"highest point {max}");
        Assert.True(min < Terrain.WaterLevel - 2f, $"lowest point {min}");
        Assert.InRange(flat / (float)total, 0.1f, 0.8f);
        Assert.InRange(wet / (float)total, 0.02f, 0.45f);
    }

    // Share of the strip along an edge (100 m deep) that is under water; edge 0 is +x, 1 is +z, 2 is -x, 3 is -z.
    static float EdgeWet(Terrain terrain, int edge)
    {
        float half = terrain.HalfSize;
        int wet = 0, total = 0;
        for (int j = 0; j <= terrain.Cells; j++)
            for (int i = 0; i <= terrain.Cells; i++)
            {
                float x = (i - terrain.Cells * 0.5f) * terrain.CellSize, z = (j - terrain.Cells * 0.5f) * terrain.CellSize;
                float inland = edge switch { 0 => half - x, 1 => half - z, 2 => half + x, _ => half + z };
                if (inland > 100f) continue;
                total++;
                if (terrain.GetSample(i, j) < terrain.WaterSurfaceAtSample(i, j)) wet++;
            }
        return wet / (float)total;
    }

    [Fact]
    public void TheSeedPicksTheCoast_AllFiveKindsHappen_AndTheSeaLiesAlongTheEdgesItHas()
    {
        var kinds = new HashSet<CoastKind>();
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var terrain = Terrain.Generate(seed, cells: 128, cellSize: 32f);
            kinds.Add(terrain.Coast);
            Assert.Equal(Terrain.CoastOf(seed), terrain.Coast);
            int seaEdges = Enumerable.Range(0, 4).Count(e => EdgeWet(terrain, e) > 0.95f);
            Assert.Equal(terrain.Coast switch { CoastKind.None => 0, CoastKind.OneSide => 1, CoastKind.TwoSides => 2, CoastKind.ThreeSides => 3, _ => 4 }, seaEdges);

            // The surface falls about 5 m across the map, toward the sea when there is one; nothing lies more than the water depth below it.
            float fall = MathF.Sqrt(terrain.WaterSlopeX * terrain.WaterSlopeX + terrain.WaterSlopeZ * terrain.WaterSlopeZ);
            Assert.InRange(fall, 0.0011f, 0.0013f);
            for (int j = 0; j <= terrain.Cells; j++)
                for (int i = 0; i <= terrain.Cells; i++)
                    Assert.True(terrain.GetSample(i, j) - terrain.WaterSurfaceAtSample(i, j) >= -4.01f, $"seed {seed}");
        }
        Assert.Equal(5, kinds.Count);
    }

    [Fact]
    public void TheSurfaceFallsTowardTheSea()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var terrain = Terrain.Generate(seed, cells: 128, cellSize: 32f);
            if (terrain.Coast is CoastKind.None or CoastKind.Island) continue;
            // The surface is lowest on the sea side: no sea edge is higher than the middle of the map, and one of them is lower
            // (the tip of a peninsula; two opposite sea edges are level with the middle).
            float half = terrain.HalfSize, middle = terrain.WaterSurfaceAt(0f, 0f);
            bool lower = false;
            for (int e = 0; e < 4; e++)
                if (EdgeWet(terrain, e) > 0.95f)
                {
                    var (x, z) = e switch { 0 => (half, 0f), 1 => (0f, half), 2 => (-half, 0f), _ => (0f, -half) };
                    Assert.True(terrain.WaterSurfaceAt(x, z) <= middle + 0.01f, $"seed {seed}, edge {e}");
                    lower |= terrain.WaterSurfaceAt(x, z) < middle - 1f;
                }
            Assert.True(lower, $"seed {seed}");
        }
    }

    [Fact]
    public void WithNoWaterSetting_ThereIsNoSeaAndNoLake()
    {
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var terrain = Terrain.Generate(seed, cells: 128, cellSize: 32f, settings: new TerrainSettings(0.5f, 0f, 0.5f));
            Assert.Equal(CoastKind.None, terrain.Coast);
            Assert.True(Share(terrain, Biome.Water) < 0.05f, $"seed {seed}: {Share(terrain, Biome.Water)}");
        }
    }

    [Fact]
    public void TheMountainsSliderMakesRealMountains_AndTheMapKeepsLevelGroundForTowns()
    {
        float Highest(Terrain t) { float max = float.MinValue; for (int j = 0; j < t.Samples; j++) for (int i = 0; i < t.Samples; i++) max = Math.Max(max, t.GetSample(i, j)); return max; }
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var most = Terrain.Generate(seed, settings: new TerrainSettings(1f, 0.5f, 0.5f));
            Assert.InRange(Highest(most), 150f, 650f);
            Assert.InRange(Highest(Terrain.Generate(seed, settings: new TerrainSettings(0f, 0.5f, 0.5f))), 5f, 70f);
            Assert.InRange(Highest(Terrain.Generate(seed, settings: new TerrainSettings(0.5f, 0.5f, 0.5f))), 60f, 320f);

            // Slope over 16 m: at least 30 % of the land is under 5 % even with the most mountains, and the mountains are steep somewhere.
            int land = 0, level = 0, steep = 0;
            for (int j = 1; j < most.Cells; j++)
                for (int i = 1; i < most.Cells; i++)
                {
                    if (most.BiomeAt(i, j) == Biome.Water) continue;
                    land++;
                    float gx = (most.GetSample(i + 1, j) - most.GetSample(i - 1, j)) / (2f * most.CellSize), gz = (most.GetSample(i, j + 1) - most.GetSample(i, j - 1)) / (2f * most.CellSize);
                    float slope = MathF.Sqrt(gx * gx + gz * gz);
                    if (slope < 0.05f) level++;
                    if (slope > 0.3f) steep++;
                }
            Assert.True(level >= 0.30f * land, $"seed {seed}: level {level / (float)land:0.00}");
            Assert.True(steep >= 0.10f * land, $"seed {seed}: steep {steep / (float)land:0.00}");
        }
    }

    static float Share(Terrain terrain, Biome biome)
    {
        int count = 0;
        for (int j = 0; j < terrain.Samples; j++)
            for (int i = 0; i < terrain.Samples; i++)
                if (terrain.BiomeAt(i, j) == biome) count++;
        return count / (float)(terrain.Samples * terrain.Samples);
    }

    [Fact]
    public void TheSettingsAreInTheMap_SameSettingsSameMap_AndTheSlidersMoveTheirFeature()
    {
        var more = new TerrainSettings(1f, 1f, 1f);
        var less = new TerrainSettings(0f, 0f, 0f);
        Assert.Equal(Terrain.Generate(5, settings: more).Hash(), Terrain.Generate(5, settings: more).Hash());
        Assert.NotEqual(Terrain.Generate(5, settings: more).Hash(), Terrain.Generate(5, settings: less).Hash());

        float Highest(Terrain t) { float max = float.MinValue; for (int j = 0; j < t.Samples; j++) for (int i = 0; i < t.Samples; i++) max = Math.Max(max, t.GetSample(i, j)); return max; }
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4 })
        {
            Assert.True(Highest(Terrain.Generate(seed, settings: new TerrainSettings(1f, 0.5f, 0.5f))) > Highest(Terrain.Generate(seed, settings: new TerrainSettings(0f, 0.5f, 0.5f))) + 20f, $"mountains, seed {seed}");
            Assert.True(Share(Terrain.Generate(seed, settings: new TerrainSettings(0.5f, 1f, 0.5f)), Biome.Water) > Share(Terrain.Generate(seed, settings: new TerrainSettings(0.5f, 0f, 0.5f)), Biome.Water), $"water, seed {seed}");
            Assert.True(Share(Terrain.Generate(seed, settings: new TerrainSettings(0.5f, 0.5f, 1f)), Biome.Forest) > Share(Terrain.Generate(seed, settings: new TerrainSettings(0.5f, 0.5f, 0f)), Biome.Forest) + 0.1f, $"forests, seed {seed}");
        }
    }

    [Fact]
    public void TheBiomesAreAllThere_OnTheDefaultSettings_AndWaterIsTheGroundUnderTheSurface()
    {
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4, 5, 6 })
        {
            var terrain = Terrain.Generate(seed);
            Assert.InRange(Share(terrain, Biome.Water), 0.005f, 0.85f);
            Assert.InRange(Share(terrain, Biome.Forest), 0.02f, 0.6f);
            Assert.True(Share(terrain, Biome.Field) > 0.1f, $"seed {seed}");
            // The biome Water is the ground under the water surface.
            for (int j = 0; j < terrain.Samples; j += 7)
                for (int i = 0; i < terrain.Samples; i += 7)
                    Assert.Equal(terrain.GetSample(i, j) < terrain.WaterSurfaceAtSample(i, j), terrain.BiomeAt(i, j) == Biome.Water);
        }
    }

    [Fact]
    public void ACoarseMap_ShowsTheSameLandAsAFineOne()
    {
        // The preview: the same seed on a 32 m grid has the same coast, lakes and rivers as the 8 m one, only less detail.
        foreach (ulong seed in new ulong[] { 1, 2, 3 })
        {
            var fine = Terrain.Generate(seed);
            var coarse = Terrain.Generate(seed, cells: 128, cellSize: 32f);
            Assert.Equal(fine.HalfSize, coarse.HalfSize);
            Assert.Equal(Share(fine, Biome.Water), Share(coarse, Biome.Water), 0.02f);
            int agree = 0, total = 0;
            for (int j = 0; j < coarse.Samples; j++)
                for (int i = 0; i < coarse.Samples; i++)
                {
                    total++;
                    if ((coarse.BiomeAt(i, j) == Biome.Water) == (fine.BiomeAt(i * 4, j * 4) == Biome.Water)) agree++;
                }
            Assert.True(agree > 0.97f * total, $"seed {seed}: {agree} of {total}");
        }
    }

    [Fact]
    public void ThePreview_CoversTheMapOfItsSizeAndShowsItsLand()
    {
        foreach (var size in new[] { MapSize.Small, MapSize.Normal })
        {
            var preview = WorldBuilder.Preview(5, size);
            var full = Terrain.Generate(5, WorldBuilder.CellsOf(size));
            Assert.Equal(full.HalfSize, preview.HalfSize, 1);
            Assert.Equal(Share(full, Biome.Water), Share(preview, Biome.Water), 0.03f);
        }
        Assert.True(WorldBuilder.CellsOf(MapSize.Small) < WorldBuilder.CellsOf(MapSize.Normal));
        Assert.True(WorldBuilder.CellsOf(MapSize.Normal) < WorldBuilder.CellsOf(MapSize.Large));
    }

    [Fact]
    public void ASmallMap_GetsItsTownsAndIndustries()
    {
        var (sim, plan) = WorldBuilder.Towns(5, 3, IndustryAmount.Normal, size: MapSize.Small);
        Assert.Equal(384, sim.Network.Terrain!.Cells);
        Assert.Equal(3, plan.Towns.Count);
    }

    [Fact]
    public void TheMaps_AreMadeFastEnoughForTheNewGameScreen()
    {
        // Seeds nobody asked for before, so the map is made and not copied from the cache.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Terrain.Generate(90001, cells: 512);
        Assert.True(clock.Elapsed.TotalSeconds < 1.5, $"normal map {clock.Elapsed.TotalSeconds} s");

        clock.Restart();
        var large = Terrain.Generate(90002, cells: 1024);
        Assert.True(clock.Elapsed.TotalSeconds < 4, $"large map {clock.Elapsed.TotalSeconds} s");
        Assert.Equal(1025, large.Samples);

        clock.Restart();
        WorldBuilder.Preview(90003, MapSize.Large);
        Assert.True(clock.Elapsed.TotalSeconds < 1.5, $"preview of a large map {clock.Elapsed.TotalSeconds} s");
    }

    [Fact]
    public void AMapAskedForAgain_IsACopy_SoRoadsCarvedInOneDoNotShowInTheNext()
    {
        var first = Terrain.Generate(90004, cells: 128, cellSize: 32f);
        var second = Terrain.Generate(90004, cells: 128, cellSize: 32f);
        Assert.Equal(first.Hash(), second.Hash());
        first.CarveRoad(-100f, 0f, 20f, 100f, 0f, 20f, 3.5f, 6f);
        Assert.NotEqual(first.Hash(), second.Hash());
        Assert.Equal(second.Hash(), Terrain.Generate(90004, cells: 128, cellSize: 32f).Hash());
    }

    [Fact]
    public void WaterIsMeasuredAgainstTheSurfaceWhereItIs()
    {
        // Level ground at height 0; the surface is 1 m above it at x = -100, level with it at x = 0 and 1 m below at x = 100.
        var terrain = Terrain.Create(64, 10f, (x, z) => 0f, waterSlopeX: -0.01f);

        Assert.Equal(1f, terrain.WaterSurfaceAt(-100f, 7f), 4);
        Assert.Equal(-1f, terrain.WaterSurfaceAt(100f, 7f), 4);
        Assert.True(terrain.IsWater(-100f, 0f));
        Assert.False(terrain.IsWater(100f, 0f));
        Assert.Equal(terrain.WaterSurfaceAt(-50f, 0f), terrain.WaterSurfaceAtSample(27, 32), 4);
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
        var terrain = Terrain.Generate(1);
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
