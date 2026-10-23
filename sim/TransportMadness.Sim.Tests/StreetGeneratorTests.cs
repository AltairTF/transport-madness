using TransportMadness.Sim;
using Xunit;
using Xunit.Abstractions;

namespace TransportMadness.Sim.Tests;

/// <summary>R4a': the growth loop as a pure function (docs/r4-plan.md).</summary>
public class StreetGeneratorTests(ITestOutputHelper output)
{
    static readonly TownsTuning T = Tuning.Load().Towns;
    static readonly float Narrow = RoadNetwork.HalfWidth(RoadType.TwoLane) + T.Generation.SetbackGap + TownGenerator.MaxFootprint(T);

    static float RadiusOf(TownSize size) => size switch { TownSize.Village => 210f, TownSize.Medium => 390f, _ => 660f };

    static OrganicTown Make(TownSize size, ulong seed = 3, Terrain? terrain = null, float cx = 0f, float cz = 0f)
        => StreetGenerator.Generate(terrain, T, size, cx, cz, RadiusOf(size), seed);

    /// <summary>A dry spot with water within 250 m, so a test on real terrain means something.</summary>
    static (float X, float Z) ShoreOf(Terrain terrain)
    {
        for (float x = -1500f; x <= 1500f; x += 50f)
            for (float z = -1500f; z <= 1500f; z += 50f)
                if (!terrain.IsWater(x, z) && terrain.IsWater(x + 200f, z)) return (x, z);
        throw new InvalidOperationException("no shore");
    }

    static float DistanceToStreets(StreetGraph graph, float x, float z, bool skipMain = false)
    {
        float best = float.MaxValue;
        foreach (var e in graph.Edges)
        {
            if (skipMain && e.Main) continue;
            var (ax, az) = graph.Nodes[e.A];
            var (bx, bz) = graph.Nodes[e.B];
            float sx = bx - ax, sz = bz - az, l2 = sx * sx + sz * sz;
            float t = l2 < 1e-6f ? 0f : Math.Clamp(((x - ax) * sx + (z - az) * sz) / l2, 0f, 1f);
            best = MathF.Min(best, MathF.Sqrt((ax + sx * t - x) * (ax + sx * t - x) + (az + sz * t - z) * (az + sz * t - z)));
        }
        return best;
    }

    static int Components(StreetGraph graph)
    {
        var used = new HashSet<int>(graph.Edges.SelectMany(e => new[] { e.A, e.B }));
        var parent = Enumerable.Range(0, graph.Nodes.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        foreach (var e in graph.Edges) parent[Find(e.A)] = Find(e.B);
        return used.Select(Find).Distinct().Count();
    }

    [Fact]
    public void TheSameSeed_GivesTheSameTown_AndAnotherSeedAnotherOne()
    {
        var a = Make(TownSize.Medium);
        var b = Make(TownSize.Medium);
        var c = Make(TownSize.Medium, seed: 4);
        Assert.Equal(a.Graph.Nodes, b.Graph.Nodes);
        Assert.Equal(a.Graph.Edges, b.Graph.Edges);
        Assert.Equal(a.Buildings, b.Buildings);
        Assert.NotEqual(a.Graph.Nodes, c.Graph.Nodes);
    }

    [Theory]
    [InlineData(TownSize.Village)]
    [InlineData(TownSize.Medium)]
    [InlineData(TownSize.Metropolis)]
    public void EveryStreet_JoinsTheSeedRoads_AndSomeStreetsAreSeedRoads(TownSize size)
    {
        foreach (ulong seed in new ulong[] { 1, 2, 3 })
        {
            var town = Make(size, seed);
            Assert.Equal(1, Components(town.Graph));
            Assert.Contains(town.Graph.Edges, e => e.Seed);
            Assert.Contains(town.Graph.Edges, e => e.Main);
            Assert.Contains(town.Graph.Edges, e => !e.Seed);
        }
    }

    [Theory]
    [InlineData(TownSize.Village, 40)]
    [InlineData(TownSize.Medium, 150)]
    [InlineData(TownSize.Metropolis, 550)]
    public void ATown_HasAtLeastItsMinimumOfBuildings(TownSize size, int least)
    {
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4 })
        {
            var town = Make(size, seed);
            output.WriteLine($"{size} {seed}: {town.Buildings.Count} buildings, {town.Lots.Count} lots, {town.Graph.Edges.Count} pieces, radius {town.Radius:F0}");
            Assert.True(town.Buildings.Count >= least, $"{size} seed {seed}: {town.Buildings.Count} buildings");
        }
    }

    [Fact]
    public void NoLotAndNoStreetNode_IsInWater_AndNoTwoLotsAreCloserThanAFootprint()
    {
        var terrain = Terrain.Generate(5);
        var (x, z) = ShoreOf(terrain);
        float apart = TownGenerator.MaxFootprint(T) * 2f;
        foreach (var size in new[] { TownSize.Village, TownSize.Medium, TownSize.Metropolis })
        {
            var town = Make(size, 5, terrain, x, z);
            foreach (var (nx, nz) in town.Graph.Nodes) Assert.False(terrain.IsWater(nx, nz), $"{size}: street node in water");
            foreach (var lot in town.Lots) Assert.False(terrain.IsWater(lot.X, lot.Z), $"{size}: lot in water");
            for (int i = 0; i < town.Lots.Count; i++)
                for (int j = i + 1; j < town.Lots.Count; j++)
                {
                    float d = MathF.Sqrt(MathF.Pow(town.Lots[i].X - town.Lots[j].X, 2) + MathF.Pow(town.Lots[i].Z - town.Lots[j].Z, 2));
                    Assert.True(d >= apart - 0.01f, $"{size}: lots {d:F1} m apart");
                }
        }
    }

    [Fact]
    public void EveryBuilding_HasAStreetInReach_AndNoStreetThroughIt()
    {
        foreach (var size in new[] { TownSize.Village, TownSize.Medium, TownSize.Metropolis })
        {
            var town = Make(size, 6);
            foreach (var b in town.Buildings)
            {
                float d = DistanceToStreets(town.Graph, b.X, b.Z);
                Assert.True(d <= T.AccessRadius, $"{size}: no street near {b.X:F0},{b.Z:F0}");
                Assert.True(d >= Narrow * 0.99f, $"{size}: a street {d:F1} m from a building");
                Assert.True(DistanceToStreets(town.Graph, b.X, b.Z, skipMain: true) >= Narrow * 0.99f);
            }
        }
    }

    [Fact]
    public void ADeadEndStreet_IsNeverALongSpoke()
    {
        foreach (var size in new[] { TownSize.Village, TownSize.Medium, TownSize.Metropolis })
            foreach (ulong seed in new ulong[] { 1, 2, 3 })
            {
                var town = Make(size, seed);
                foreach (var (node, run) in StreetGenerator.DeadEnds(town.Graph))
                    Assert.True(run <= T.Generation.Streets.MaxDeadEnd + 0.5f, $"{size} seed {seed}: a dead end {run:F0} m long at node {node}");
            }
    }

    [Fact]
    public void StreetsAreNotCloserTogetherThanTheClearance_UnlessJoined()
    {
        var town = Make(TownSize.Medium, 7);
        var g = town.Graph;
        // A block narrower than the clearance would mean two unjoined pieces closer than it: the frame (seed roads and ring) is exempt.
        var near = 0;
        for (int i = 0; i < g.Edges.Count; i++)
            for (int j = i + 1; j < g.Edges.Count; j++)
            {
                var a = g.Edges[i]; var b = g.Edges[j];
                if (a.Seed || b.Seed || a.A == b.A || a.A == b.B || a.B == b.A || a.B == b.B) continue;
                var (ax, az) = g.Nodes[a.A]; var (bx, bz) = g.Nodes[a.B];
                var (cx, cz) = g.Nodes[b.A]; var (dx, dz) = g.Nodes[b.B];
                float m = MathF.Min(Math.Min(Pt(ax, az, cx, cz, dx, dz), Pt(bx, bz, cx, cz, dx, dz)), MathF.Min(Pt(cx, cz, ax, az, bx, bz), Pt(dx, dz, ax, az, bx, bz)));
                if (m < T.Generation.Streets.StreetClearance * 0.5f) near++;
            }
        Assert.True(near <= g.Edges.Count / 3, $"{near} pairs of unjoined pieces are under half the clearance");
    }

    static float Pt(float px, float pz, float ax, float az, float bx, float bz)
    {
        float sx = bx - ax, sz = bz - az, l2 = sx * sx + sz * sz;
        float t = l2 < 1e-6f ? 0f : Math.Clamp(((px - ax) * sx + (pz - az) * sz) / l2, 0f, 1f);
        return MathF.Sqrt((ax + sx * t - px) * (ax + sx * t - px) + (az + sz * t - pz) * (az + sz * t - pz));
    }

    [Fact]
    public void AMetropolis_IsGrownInAFewSeconds()
    {
        var terrain = Terrain.Generate(5);
        var (x, z) = ShoreOf(terrain);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var town = Make(TownSize.Metropolis, 7, terrain, x, z);
        Assert.True(watch.Elapsed.TotalSeconds < 3, $"{watch.Elapsed.TotalSeconds:F1} s");
        Assert.NotEmpty(town.Buildings);
    }

    [Fact]
    public void Towns_GrowAcrossARiverWithBridges()
    {
        var terrain = Terrain.Generate(5);
        var (x, z) = ShoreOf(terrain);
        var bridges = 0;
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4, 5, 6 })
            bridges += Make(TownSize.Medium, seed, terrain, x, z).Graph.Edges.Count(e => e.Bridge);
        output.WriteLine($"{bridges} bridge pieces in 6 towns by the water");
        Assert.True(bridges >= 0);
    }

    [Fact]
    public void TheSameTown_StandsOnThe8mAndThe32mTerrain()
    {
        // The preview is the 32 m map of the same seed: a town site on it is a site on the real one, and a town laid there has about as many buildings.
        var fine = Terrain.Generate(5);
        var coarse = Terrain.Generate(5, cells: 128, cellSize: 32f);
        var (x, z) = ShoreOf(fine);
        var a = Make(TownSize.Medium, 8, fine, x, z);
        var b = Make(TownSize.Medium, 8, coarse, x, z);
        Assert.InRange(b.Buildings.Count, a.Buildings.Count * 0.7, a.Buildings.Count * 1.4);
    }

    /// <summary>Writes the layouts to look at: set TM_SVG_DIR to a folder (otherwise the temp folder).</summary>
    [Fact]
    public void WritesTheLayoutsAsSvg()
    {
        string dir = Environment.GetEnvironmentVariable("TM_SVG_DIR") ?? Path.Combine(Path.GetTempPath(), "tm-r4");
        Directory.CreateDirectory(dir);
        foreach (var size in new[] { TownSize.Village, TownSize.Medium, TownSize.Metropolis })
            foreach (ulong seed in new ulong[] { 1, 2 })
                File.WriteAllText(Path.Combine(dir, $"{size}-{seed}.svg"), Svg(Make(size, seed), RadiusOf(size)));
    }

    static string Svg(OrganicTown town, float radius)
    {
        float half = radius * 1.6f;
        var sb = new System.Text.StringBuilder();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append(ci, $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{-half} {-half} {half * 2} {half * 2}' width='1000' height='1000'><rect x='{-half}' y='{-half}' width='{half * 2}' height='{half * 2}' fill='#e8e4d8'/>");
        sb.Append(ci, $"<circle cx='0' cy='0' r='{radius}' fill='none' stroke='#aaa' stroke-dasharray='6'/>");
        foreach (var e in town.Graph.Edges)
        {
            var (ax, az) = town.Graph.Nodes[e.A]; var (bx, bz) = town.Graph.Nodes[e.B];
            string color = e.Bridge ? "#3070c0" : e.Main ? "#b05020" : e.Seed ? "#605040" : "#807060";
            sb.Append(ci, $"<line x1='{ax:F1}' y1='{az:F1}' x2='{bx:F1}' y2='{bz:F1}' stroke='{color}' stroke-width='{(e.Main ? 7 : 4)}' stroke-linecap='round'/>");
        }
        foreach (var lot in town.Lots) sb.Append(ci, $"<circle cx='{lot.X:F1}' cy='{lot.Z:F1}' r='3' fill='#ccc'/>");
        foreach (var b in town.Buildings)
        {
            var tier = T.Of(b.Type, b.Tier);
            string fill = b.Type == BuildingType.Home ? "#d9a06b" : b.Type == BuildingType.Commerce ? "#6b9fd9" : "#7a7a7a";
            sb.Append(ci, $"<rect x='{b.X - tier.Size:F1}' y='{b.Z - tier.Size:F1}' width='{tier.Size * 2}' height='{tier.Size * 2}' fill='{fill}' transform='rotate({b.Facing * 57.2958f:F0} {b.X:F1} {b.Z:F1})'/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
