using TransportMadness.Sim;
using Xunit;

namespace TransportMadness.Sim.Tests;

/// <summary>R4c: lots along the organic streets and the starting buildings on them.</summary>
public class StreetLotTests
{
    static WorldPlan Plan(ulong seed, out RoadNetwork net)
    {
        net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Worlds.Late());
        return TownGenerator.Generate(net, seed, 3);
    }

    [Fact]
    public void NoTwoBuildings_AreCloserThanTheirSizes()
    {
        var plan = Plan(11, out var net);
        var t = net.Tuning.Towns;
        foreach (var town in plan.Towns)
            for (int i = 0; i < town.Buildings.Count; i++)
                for (int j = i + 1; j < town.Buildings.Count; j++)
                {
                    var a = town.Buildings[i]; var b = town.Buildings[j];
                    float d = MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
                    float apart = (t.Of(a.Type, a.Tier).Size + t.Of(b.Type, b.Tier).Size) * 0.96f;
                    Assert.True(d >= apart, $"{town.Name}: {d:F1} m apart");
                }
    }

    [Fact]
    public void EveryStartingBuilding_HasARoadInReach_AndNoRoadThroughIt()
    {
        var plan = Plan(12, out var net);
        var index = new RoadIndex(net);
        var t = net.Tuning.Towns;
        Assert.All(plan.Towns, town => Assert.True(town.Buildings.Count > 40, $"{town.Name}: {town.Buildings.Count} buildings"));
        foreach (var town in plan.Towns)
            foreach (var b in town.Buildings)
            {
                float d = index.DistanceToNearest(b.X, b.Z, t.AccessRadius);
                Assert.True(d <= t.AccessRadius, $"{town.Name}: no road near {b.X:F0},{b.Z:F0}");
                Assert.True(d > 8f, $"{town.Name}: road {d:F1} m from a building");
            }
    }

    [Fact]
    public void TheSameSeed_GivesTheSameTown()
    {
        var a = Plan(13, out _);
        var b = Plan(13, out _);
        Assert.Equal(a.Towns.Select(t => t.Buildings.Count), b.Towns.Select(t => t.Buildings.Count));
        Assert.Equal(a.Towns[0].Buildings, b.Towns[0].Buildings);
    }

    [Fact]
    public void TheCentre_IsDenserAndMoreMixedThanTheEdge()
    {
        var plan = Plan(14, out var net);
        var town = plan.Towns[0];
        float radius = town.Radius;
        int inner = town.Buildings.Count(b => MathF.Sqrt((b.X - town.X) * (b.X - town.X) + (b.Z - town.Z) * (b.Z - town.Z)) < radius * 0.5f);
        int commerce = town.Buildings.Count(b => b.Type != BuildingType.Home);
        Assert.True(inner > 0 && commerce > 0);
    }
}
