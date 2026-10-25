namespace TransportMadness.Sim;

/// <summary>The size class of a town: how many people it starts with (docs/town-redo-plan.md).</summary>
public enum TownSize : byte { Hamlet, Village, Town }

/// <summary>
/// Lays out towns on a map: picks flat dry ground, builds each town's streets in the network (free, and before the
/// simulation exists, so it is not part of the command log) and plans the buildings along them (<see cref="VillageGenerator"/>).
/// It draws from its own random generator seeded from the world seed, so the same seed always gives the same towns and nothing
/// else in the simulation depends on it (ADR 0003). Tuning is <c>towns.generation</c>, <c>towns.rings</c> and <c>towns.buildingTypes</c>.
/// </summary>
public static class TownGenerator
{
    const ulong SeedSalt = 0x544F574E53554C45UL; // keeps this stream apart from the simulation's own

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
        for (int i = 0; i < places.Count; i++)
        {
            var p = places[i];
            var village = VillageGenerator.Generate(net, p.Size, p.X, p.Z, seed ^ (ulong)(i + 1) * 0x9E3779B97F4A7C15UL);
            towns.Add(new TownPlan(names[i % names.Count] + (i >= names.Count ? $" {i / names.Count + 1}" : ""), p.X, p.Z, village.Buildings, village.Radius));
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
    /// Scores <c>sizes.candidates</c> random sites (flat dry ground around them, a bonus for water close by), then takes towns best
    /// site first: the best gets the Town class (when <c>townMinTowns</c> or more are asked for), <c>villageShare</c> of the rest the
    /// Village class, the others Hamlets. A town is made smaller when its site cannot hold its class, and skipped when it cannot hold
    /// even a hamlet.
    /// </summary>
    static List<(float X, float Z, TownSize Size)> PickSites(RoadNetwork net, TownsTuning tuning, SimRandom rng, int townCount)
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
            sites.Add((SiteScore(net, g, x, z) + 0.05f * rng.NextFloat(), x, z)); // the jitter spreads towns over equally good ground, not along the lowest x
        }
        sites.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.X.CompareTo(b.X));

        bool big = townCount >= s.TownMinTowns;
        int villages = (int)MathF.Round(Math.Max(0, townCount - 1) * s.VillageShare);
        var places = new List<(float X, float Z, TownSize Size)>();
        foreach (var site in sites)
        {
            if (places.Count == townCount) break;
            int rank = places.Count;
            var wanted = rank == 0 ? (big ? TownSize.Town : TownSize.Village)
                : rank <= villages + (big ? 0 : 1) ? TownSize.Village : TownSize.Hamlet;
            // Smaller until the ground holds it and no other town is too near for its size.
            for (var size = wanted; ; size--)
            {
                if (Suitable(net, g, places, site.X, site.Z, size))
                {
                    places.Add((site.X, site.Z, size));
                    break;
                }
                if (size == TownSize.Hamlet) break;
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

    /// <summary>How far the roads and houses of a town of a class reach from its middle.</summary>
    internal static float Extent(TownGenerationTuning g, TownSize size) => MathF.Max(g.Of(size).Radius, g.Of(size).ArmLength + g.Streets.CountryReach);

    static bool Suitable(RoadNetwork net, TownGenerationTuning g, List<(float X, float Z, TownSize Size)> others, float cx, float cz, TownSize size)
    {
        float extent = Extent(g, size);
        foreach (var o in others)
        {
            float need = MathF.Max(g.MinSpacing, (extent + Extent(g, o.Size)) * g.Sizes.SpacingShare + g.Sizes.SpacingGap);
            if ((o.X - cx) * (o.X - cx) + (o.Z - cz) * (o.Z - cz) < need * need) return false;
        }
        if (net.Terrain is not { } terrain) return true;
        // The houses stand within the radius: that disc is mostly dry, and not too steep. The through road goes on beyond, on land.
        float radius = g.Of(size).Radius;
        if (MathF.Abs(cx) + extent > terrain.HalfSize - g.EdgeMargin * 0.5f || MathF.Abs(cz) + extent > terrain.HalfSize - g.EdgeMargin * 0.5f) return false;
        float lowest = float.MaxValue, highest = float.MinValue;
        int wet = 0, samples = 0;
        const int steps = 9;
        for (int i = 0; i < steps; i++)
            for (int j = 0; j < steps; j++)
            {
                float x = (i / (steps - 1f) * 2f - 1f) * radius, z = (j / (steps - 1f) * 2f - 1f) * radius;
                if (x * x + z * z > radius * radius) continue;
                x += cx; z += cz;
                samples++;
                float h = terrain.HeightAt(x, z);
                if (h - terrain.WaterSurfaceAt(x, z) < g.WaterMargin) { wet++; continue; }
                lowest = MathF.Min(lowest, h);
                highest = MathF.Max(highest, h);
            }
        if (terrain.HeightAt(cx, cz) - terrain.WaterSurfaceAt(cx, cz) < g.WaterMargin) return false;
        if (wet > g.Sizes.WaterShare * samples) return false;
        return highest - lowest <= g.MaxHeightRange;
    }

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
