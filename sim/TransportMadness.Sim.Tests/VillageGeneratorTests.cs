using TransportMadness.Sim;
using Xunit;

namespace TransportMadness.Sim.Tests;

/// <summary>The towns of docs/town-redo-plan.md: small villages with a curving road, lanes and loose houses, built into a real road network.</summary>
public class VillageGeneratorTests
{
    static int People(TownsTuning t, VillagePlan plan) => plan.Buildings.Where(b => b.Type == BuildingType.Home).Sum(b => t.Of(b.Type, b.Tier).People);

    static readonly TownSize[] Sizes = { TownSize.Hamlet, TownSize.Village, TownSize.Town };

    // A flat dry map: the layout alone, whatever the ground.
    static (RoadNetwork Net, VillagePlan Plan) Flat(TownSize size, ulong seed)
    {
        var net = new RoadNetwork(Terrain.Create(256, 8f, (_, _) => 5f));
        return (net, VillageGenerator.Generate(net, size, 0f, 0f, seed));
    }

    [Theory]
    [InlineData(TownSize.Hamlet)]
    [InlineData(TownSize.Village)]
    [InlineData(TownSize.Town)]
    public void TheSameSeed_GivesTheSameTown_AndAnotherSeedAnotherOne(TownSize size)
    {
        var (netA, a) = Flat(size, 3);
        var (netB, b) = Flat(size, 3);
        var (_, c) = Flat(size, 4);

        Assert.Equal(a.Buildings, b.Buildings);
        Assert.Equal(netA.RoadCount, netB.RoadCount);
        Assert.NotEqual(a.Buildings, c.Buildings);
    }

    [Fact]
    public void ABiggerClass_HasMorePeople_AndNoTownIsOverAThousand()
    {
        var t = Tuning.Load().Towns;
        var mean = new Dictionary<TownSize, double>();
        foreach (var size in Sizes)
        {
            var counts = new List<int>();
            for (ulong seed = 1; seed <= 8; seed++)
            {
                int people = People(t, Flat(size, seed).Plan);
                Assert.True(people <= 1000 && people >= Tuning.Load().Towns.Generation.Of(size).PeopleMin * 0.6, $"{size} {seed}: {people} people");
                counts.Add(people);
            }
            mean[size] = counts.Average();
        }

        Assert.True(mean[TownSize.Hamlet] < mean[TownSize.Village] && mean[TownSize.Village] < mean[TownSize.Town], string.Join(", ", mean));
        // A class holds the people the plan gives it on average; one town may come up a little short when its ground leaves little room.
        var c = Tuning.Load().Towns.Generation;
        foreach (var size in Sizes)
            Assert.InRange(mean[size], c.Of(size).PeopleMin, c.Of(size).PeopleMax);
    }

    /// <summary>The towns the generator picks for a map of three: a Town, a Village and a Hamlet, on real ground.</summary>
    static (RoadNetwork Net, IReadOnlyList<TownPlan> Towns) World(ulong seed)
    {
        var net = new RoadNetwork(Terrain.Generate(seed, 512));
        return (net, TownGenerator.Generate(net, seed, 3).Towns);
    }

    [Fact]
    public void EveryBuilding_HasARoadInReach_NoRoadThroughIt_AndNoneOverlapsAnother()
    {
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var (net, towns) = World(seed);
            var t = net.Tuning.Towns;
            var index = new RoadIndex(net);
            var size = "town";
            foreach (var plan in towns)
            for (int i = 0; i < plan.Buildings.Count; i++)
            {
                var a = plan.Buildings[i];
                var ta = t.Of(a.Type, a.Tier);
                float road = index.DistanceToNearest(a.X, a.Z, t.AccessRadius);
                Assert.True(road <= t.AccessRadius, $"{size} {seed}: no road near building {i}");
                Assert.True(road >= RoadNetwork.HalfWidth(RoadType.TwoLane) + ta.Footprint, $"{size} {seed}: a road {road:F1} m from building {i}");
                Assert.False(net.Terrain!.IsWater(a.X, a.Z), $"{size} {seed}: building {i} in the water");
                for (int j = i + 1; j < plan.Buildings.Count; j++)
                {
                    var b = plan.Buildings[j];
                    float d = MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
                    Assert.True(d >= (ta.Size + t.Of(b.Type, b.Tier).Size) * 0.96f, $"{size} {seed}: buildings {i} and {j} are {d:F1} m apart");
                }
            }
        }
    }

    [Fact]
    public void TheGroundUnderABuilding_IsLevel_AndNoStreetIsABridge()
    {
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var (net, towns) = World(seed);
            var t = net.Tuning.Towns;
            foreach (var b in towns.SelectMany(town => town.Buildings))
            {
                float size = t.Of(b.Type, b.Tier).Size, lowest = float.MaxValue, highest = float.MinValue;
                foreach (var (i, j) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    float h = net.Terrain!.HeightAt(b.X + i * size * 0.7f, b.Z + j * size * 0.7f);
                    lowest = MathF.Min(lowest, h);
                    highest = MathF.Max(highest, h);
                }
                Assert.True(highest - lowest < 1.2f, $"seed {seed}: {highest - lowest:F2} m of fall under a building");
            }
            for (int r = 0; r < net.RoadCount; r++)
            {
                var road = net.GetRoad(new RoadId(r));
                if (road.Retired) continue;
                Assert.Equal(RoadStructure.None, road.Structure);
            }
        }
    }

    [Fact]
    public void AHamletIsAClusterOrLinear_AndATownIsACrossingWithAMainRoadThroughIt()
    {
        for (ulong seed = 1; seed <= 6; seed++)
        {
            Assert.NotEqual(TownLayout.Crossing, Flat(TownSize.Hamlet, seed).Plan.Layout);
            var (net, town) = Flat(TownSize.Town, seed);
            Assert.Equal(TownLayout.Crossing, town.Layout);
            Assert.True(net.RoadCount >= 12, $"{net.RoadCount} road pieces");
            // The through road is made of pieces of 30 to 60 m and curves: more than two of them, and no piece turns sharply.
            var main = Enumerable.Range(0, net.RoadCount).Select(r => net.GetRoad(new RoadId(r))).Where(r => !r.Retired && r.Type == RoadType.TwoLane).ToList();
            Assert.All(main, r => Assert.True(MathF.Abs(r.Geometry.Sweep) <= 0.5f, $"a street piece turns {r.Geometry.Sweep:F2} rad"));
        }
    }

    [Fact]
    public void ABigTownIsRoomyEnough_ItsLanesAreEarthAndItsMainRoadIsPaved()
    {
        var (net, _) = Flat(TownSize.Town, 5);
        var types = Enumerable.Range(0, net.RoadCount).Select(r => net.GetRoad(new RoadId(r))).Where(r => !r.Retired).Select(r => r.Type).Distinct().ToList();

        Assert.Contains(RoadType.TwoLane, types);
        Assert.Contains(RoadType.Path, types);
        Assert.DoesNotContain(RoadType.Avenue2x2, types);
    }

    // ---- The map -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MapSize.Small, 3)]
    [InlineData(MapSize.Normal, 5)]
    [InlineData(MapSize.Large, 7)]
    public void TheMostTownsAMapAsksFor_IsThreeFiveSeven(MapSize size, int most)
    {
        Assert.Equal(most, WorldBuilder.MostTowns(size));
        var (_, plan) = WorldBuilder.Towns(11, 99, IndustryAmount.Few, size: size);
        Assert.True(plan.Towns.Count <= most);
        Assert.InRange(plan.Requested, 1, most);
    }

    [Fact]
    public void TheTownsOfAMap_AreVillages_TheBiggestFirst_AndApartFromEachOther()
    {
        var net = new RoadNetwork(Terrain.Generate(5, 512));
        var t = net.Tuning.Towns;
        var plan = TownGenerator.Generate(net, 5, 5);

        Assert.InRange(plan.Towns.Count, 3, 5);
        var people = plan.Towns.Select(town => town.Buildings.Where(b => b.Type == BuildingType.Home).Sum(b => t.Of(b.Type, b.Tier).People)).ToList();
        Assert.All(people, p => Assert.InRange(p, 20, 1000));
        Assert.True(people[0] >= people.Skip(1).Max() * 0.8, $"the first town has {people[0]} people, the others up to {people.Skip(1).Max()}");
        for (int i = 0; i < plan.Towns.Count; i++)
            for (int j = i + 1; j < plan.Towns.Count; j++)
                Assert.True(MathF.Sqrt(MathF.Pow(plan.Towns[i].X - plan.Towns[j].X, 2) + MathF.Pow(plan.Towns[i].Z - plan.Towns[j].Z, 2)) >= t.Generation.MinSpacing);
    }

    [Fact]
    public void ALargeMapWithSevenTowns_IsMadeInAFewSeconds()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var net = new RoadNetwork(Terrain.Generate(436212, 1024));
        var plan = TownGenerator.Generate(net, 436212, 7);

        Assert.InRange(plan.Towns.Count, 5, 7);
        Assert.True(clock.Elapsed.TotalSeconds < 8, $"{clock.Elapsed.TotalSeconds:F1} s");
    }
}
