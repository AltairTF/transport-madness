namespace TransportMadness.Sim;

/// <summary>
/// Lays out towns on a map: picks flat dry ground, builds each town's streets in the network (free, and before the
/// simulation exists, so it is not part of the command log) and plans the buildings along them. It draws from its own
/// random generator seeded from the world seed, so the same seed always gives the same towns and nothing else in the
/// simulation depends on it (ADR 0003). Tuning is <c>towns.generation</c>, <c>towns.rings</c> and <c>towns.buildingTypes</c>.
/// </summary>
public static class TownGenerator
{
    const ulong SeedSalt = 0x544F574E53554C45UL; // keeps this stream apart from the simulation's own
    const int LotsPerSide = 2;                   // lots along one side of a block

    /// <summary>
    /// Builds up to <paramref name="townCount"/> towns into <paramref name="net"/> and returns them. Fewer come out
    /// when the land runs out; <see cref="WorldPlan.Requested"/> keeps the number asked for.
    /// </summary>
    public static WorldPlan Generate(RoadNetwork net, ulong seed, int townCount)
    {
        var tuning = net.Tuning.Towns;
        var rng = new SimRandom(seed ^ SeedSalt);
        var places = PickSites(net, tuning, rng, townCount);
        var names = ShuffledNames(tuning, rng);

        var towns = new List<TownPlan>();
        var organic = new List<OrganicTown>();
        for (int i = 0; i < places.Count; i++)
        {
            var p = places[i];
            var g = tuning.Generation;
            var size = p.Blocks >= g.Sizes.MetropolisBlocks ? TownSize.Metropolis : p.Blocks >= g.Sizes.MediumBlocksMin ? TownSize.Medium : TownSize.Village;
            var town = StreetGenerator.Generate(net.Terrain, tuning, size, p.X, p.Z, p.Blocks * g.BlockLength * 0.5f, seed ^ (ulong)i * 0x9E3779B97F4A7C15UL);
            BuildStreets(net, town.Graph);
            organic.Add(town);
        }
        // A building only goes where a road really is, so the buildings are checked once all the streets are in.
        var index = new RoadIndex(net);
        for (int i = 0; i < places.Count; i++)
        {
            var p = places[i];
            towns.Add(new TownPlan(names[i % names.Count] + (i >= names.Count ? $" {i / names.Count + 1}" : ""), p.X, p.Z, 0f, 0, ReachableBuildings(net, index, organic[i]), organic[i].Radius, organic[i].Lots));
        }
        return new WorldPlan(townCount, towns);
    }

    static List<string> ShuffledNames(TownsTuning tuning, SimRandom rng)
    {
        var names = tuning.Names.ToList();
        for (int i = names.Count - 1; i > 0; i--)
        {
            int j = rng.NextInt(i + 1);
            (names[i], names[j]) = (names[j], names[i]);
        }
        return names;
    }

    /// <summary>
    /// R3: scores <c>sizes.candidates</c> random sites (flat dry ground around them, a bonus for water close by), then takes towns best
    /// site first: the best gets the metropolis size, <c>mediumShare</c> of the rest the medium size, the others the small one. A town
    /// is made smaller when its site cannot hold its size, and skipped when it cannot hold even the smallest. Rivers and bays may lie
    /// under a town (up to <c>waterShare</c> of it); the streets that cross them become bridges.
    /// </summary>
    static List<(float X, float Z, int Blocks)> PickSites(RoadNetwork net, TownsTuning tuning, SimRandom rng, int townCount)
    {
        var g = tuning.Generation;
        var s = g.Sizes;
        float half = net.Terrain?.HalfSize ?? 2048f;
        float range = half - g.EdgeMargin;
        var sites = new List<(float Score, float X, float Z)>();
        for (int i = 0; i < s.Candidates; i++)
        {
            float x = (rng.NextFloat() * 2f - 1f) * range, z = (rng.NextFloat() * 2f - 1f) * range;
            if (net.Terrain is { } terrain && terrain.HeightAt(x, z) - terrain.WaterSurfaceAt(x, z) < g.WaterMargin) continue;
            sites.Add((SiteScore(net, g, x, z), x, z));
        }
        sites.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.X.CompareTo(b.X));

        int mediums = (int)MathF.Round(Math.Max(0, townCount - 1) * s.MediumShare);
        var places = new List<(float X, float Z, int Blocks)>();
        foreach (var site in sites)
        {
            if (places.Count == townCount) break;
            int rank = places.Count;
            int wanted = rank == 0 && townCount >= s.MetropolisMinTowns ? s.MetropolisBlocks
                : rank < (townCount >= s.MetropolisMinTowns ? 1 : 0) + mediums ? s.MediumBlocksMin + rng.NextInt(s.MediumBlocksMax - s.MediumBlocksMin + 1)
                : g.BlocksMin + rng.NextInt(g.BlocksMax - g.BlocksMin + 1);
            // Smaller until the ground holds it and no other town is too near for its size.
            for (int blocks = wanted; blocks >= g.BlocksMin; blocks--)
            {
                if (!Suitable(net, g, places, site.X, site.Z, blocks)) continue;
                places.Add((site.X, site.Z, blocks));
                break;
            }
        }
        return places;
    }

    /// <summary>Flat dry ground in the square around a site, plus a bonus when water (a river, a lake, the coast) is close. Higher is better.</summary>
    static float SiteScore(RoadNetwork net, TownGenerationTuning g, float cx, float cz)
    {
        if (net.Terrain is not { } terrain) return 1f;
        var s = g.Sizes;
        float centre = terrain.HeightAt(cx, cz);
        int flat = 0, total = 0;
        const int steps = 9;
        for (int i = 0; i < steps; i++)
            for (int j = 0; j < steps; j++)
            {
                float x = cx + (i / (steps - 1f) * 2f - 1f) * s.SiteRadius, z = cz + (j / (steps - 1f) * 2f - 1f) * s.SiteRadius;
                total++;
                if (MathF.Abs(x) > terrain.HalfSize || MathF.Abs(z) > terrain.HalfSize || terrain.IsWater(x, z)) continue;
                if (MathF.Abs(terrain.HeightAt(x, z) - centre) <= g.MaxHeightRange * 0.5f) flat++;
            }
        float score = flat / (float)total;
        const int ring = 12;
        for (int k = 0; k < ring; k++)
        {
            float a = k * MathF.Tau / ring;
            float x = cx + MathF.Cos(a) * s.WaterBonusReach, z = cz + MathF.Sin(a) * s.WaterBonusReach;
            if (MathF.Abs(x) <= terrain.HalfSize && MathF.Abs(z) <= terrain.HalfSize && terrain.IsWater(x, z)) { score += s.WaterBonus; break; }
        }
        return score;
    }

    /// <summary>The radius of the town of a size class (its streets and lots reach further, by <c>outerReach</c>, but little is built there).</summary>
    static float Extent(TownGenerationTuning g, int blocks) => blocks * g.BlockLength * 0.5f;

    static bool Suitable(RoadNetwork net, TownGenerationTuning g, List<(float X, float Z, int Blocks)> others, float cx, float cz, int blocks)
    {
        float extent = Extent(g, blocks);
        foreach (var o in others)
        {
            float need = MathF.Max(g.MinSpacing, (extent + Extent(g, o.Blocks)) * g.Sizes.SpacingShare + g.Sizes.SpacingGap);
            if ((o.X - cx) * (o.X - cx) + (o.Z - cz) * (o.Z - cz) < need * need) return false;
        }
        if (net.Terrain is not { } terrain) return true;
        // The grid may lie at any angle, so test the whole disc around it: the middle is dry, little of the rest is water, and not too much height.
        float lowest = float.MaxValue, highest = float.MinValue;
        int wet = 0;
        const int steps = 9;
        for (int i = 0; i < steps; i++)
            for (int j = 0; j < steps; j++)
            {
                float x = cx + (i / (steps - 1f) * 2f - 1f) * extent, z = cz + (j / (steps - 1f) * 2f - 1f) * extent;
                if (MathF.Abs(x) > terrain.HalfSize || MathF.Abs(z) > terrain.HalfSize) return false;
                float h = terrain.HeightAt(x, z);
                if (h - terrain.WaterSurfaceAt(x, z) < g.WaterMargin) { wet++; continue; }
                lowest = MathF.Min(lowest, h);
                highest = MathF.Max(highest, h);
            }
        if (wet > g.Sizes.WaterShare * steps * steps) return false;
        return highest - lowest <= g.MaxHeightRange;
    }

    /// <summary>Turns a position in a town's own axes (u along its first street direction, v across) into the map's.</summary>
    public static (float X, float Z) ToWorld(float cx, float cz, float angle, float u, float v)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return (cx + u * c - v * s, cz + u * s + v * c);
    }

    /// <summary>
    /// R4b: the streets of a town as <see cref="StreetGenerator"/> grew them, built into the network piece by piece, the main street first so
    /// the lesser ones are cut by it.
    /// </summary>
    static void BuildStreets(RoadNetwork net, StreetGraph graph)
    {
        var roads = net.Tuning.Roads;
        foreach (bool main in new[] { true, false })
            foreach (var e in graph.Edges)
            {
                if (e.Main != main) continue;
                var (x0, z0) = graph.Nodes[e.A];
                var (x1, z1) = graph.Nodes[e.B];
                var type = main ? RoadType.Avenue2x2 : RoadType.TwoLane;
                net.BuildRoad(x0, z0, x1, z1, type, roads.Of(type).SpeedLimit);
            }
    }

    /// <summary>A place a building can stand: on one side of a block, facing its street. Where it is does not depend on what is built on it.</summary>
    /// <param name="I">Block column (can be negative or past the first grid when a town has grown).</param>
    /// <param name="J">Block row.</param>
    /// <param name="U">Middle of the block in the town's own axes, for the ring it lies in.</param>
    public readonly record struct LotSite(int I, int J, int Side, int Lot, float X, float Z, float Facing, float U, float V);

    /// <summary>
    /// The lots of the blocks <paramref name="i0"/>..<paramref name="i1"/> by <paramref name="j0"/>..<paramref name="j1"/> of a town
    /// whose first grid had <paramref name="blocks"/> blocks a side, in a fixed order (block columns, rows, sides, lots). A building's
    /// middle is as far from its street as the biggest size needs (and an avenue's width, in case the street is widened later), so
    /// a building can grow in place.
    /// </summary>
    public static IEnumerable<LotSite> LotSites(TownsTuning tuning, float cx, float cz, float angle, int blocks, int i0, int i1, int j0, int j1)
    {
        var g = tuning.Generation;
        float half = blocks * g.BlockLength * 0.5f;
        float avenueHalf = RoadNetwork.HalfWidth(RoadType.Avenue2x2);
        float setback = avenueHalf + g.SetbackGap + MaxFootprint(tuning);
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
                for (int side = 0; side < 4; side++)
                    for (int lot = 0; lot < LotsPerSide; lot++)
                    {
                        float u0 = -half + i * g.BlockLength, v0 = -half + j * g.BlockLength;
                        float along = g.BlockLength * 0.5f + (lot - (LotsPerSide - 1) * 0.5f) * g.LotWidth;
                        // Local axes: u along the first street direction, v across. Sides: bottom, top, left, right.
                        (float u, float v, float facing) = side switch
                        {
                            0 => (u0 + along, v0 + setback, -MathF.PI * 0.5f),
                            1 => (u0 + along, v0 + g.BlockLength - setback, MathF.PI * 0.5f),
                            2 => (u0 + setback, v0 + along, MathF.PI),
                            _ => (u0 + g.BlockLength - setback, v0 + along, 0f),
                        };
                        var (wx, wz) = ToWorld(cx, cz, angle, u, v);
                        yield return new LotSite(i, j, side, lot, wx, wz, facing + angle, u0 + g.BlockLength * 0.5f, v0 + g.BlockLength * 0.5f);
                    }
    }

    internal static float MaxFootprint(TownsTuning tuning)
    {
        float widest = 0f;
        foreach (var type in Enum.GetValues<BuildingType>())
            widest = MathF.Max(widest, tuning.Of(type, TownsTuning.Tiers - 1).Footprint);
        return widest;
    }

    /// <summary>The starting buildings of a town that have a road within reach and none through them (the streets are all in the network by now).</summary>
    static List<BuildingPlan> ReachableBuildings(RoadNetwork net, RoadIndex index, OrganicTown town)
    {
        var tuning = net.Tuning.Towns;
        float narrow = RoadNetwork.HalfWidth(RoadType.TwoLane) + tuning.Generation.SetbackGap + MaxFootprint(tuning);
        var plans = new List<BuildingPlan>();
        foreach (var b in town.Buildings)
        {
            float road = index.DistanceToNearest(b.X, b.Z, tuning.AccessRadius);
            if (road <= tuning.AccessRadius && road >= narrow * 0.9f) plans.Add(b);
        }
        return plans;
    }

    internal const float RingNoise = 0.2f; // how far, as a share of the radius, a lot's ring may be off its distance

    /// <summary>The ring at a distance from the middle of the town, as a share of its radius.</summary>
    public static RingTuning RingAt(TownsTuning tuning, float ratio)
    {
        foreach (var ring in tuning.Rings)
            if (ratio <= ring.Until) return ring;
        return tuning.Rings[^1];
    }

    /// <summary>The ring a point of the town (in its own axes) lies in: how much is built and what, by distance from the middle of the first grid.</summary>
    public static RingTuning RingAt(TownsTuning tuning, float u, float v, float half)
    {
        float d = MathF.Max(MathF.Abs(u), MathF.Abs(v)) / half;
        foreach (var ring in tuning.Rings)
            if (d <= ring.Until) return ring;
        return tuning.Rings[^1];
    }

    /// <summary>The index picked by a roll in [0, 1) among weights.</summary>
    public static int Pick(IReadOnlyList<float> weights, float unit)
    {
        float total = 0f;
        foreach (float w in weights) total += w;
        float roll = unit * total;
        for (int i = 0; i < weights.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0f) return i;
        }
        return weights.Count - 1;
    }
}
