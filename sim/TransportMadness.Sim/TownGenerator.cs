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
        for (int i = 0; i < places.Count; i++)
        {
            var p = places[i];
            BuildStreets(net, p.X, p.Z, p.Angle, p.Blocks);
            towns.Add(new TownPlan(names[i % names.Count] + (i >= names.Count ? $" {i / names.Count + 1}" : ""), p.X, p.Z, p.Angle, p.Blocks, new List<BuildingPlan>()));
        }
        // Buildings are planned once all the streets are in, so a building only goes where a road really is.
        var index = new RoadIndex(net);
        for (int i = 0; i < towns.Count; i++)
        {
            var p = places[i];
            towns[i] = towns[i] with { Buildings = PlanBuildings(net, index, rng, p.X, p.Z, p.Angle, p.Blocks) };
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
    static List<(float X, float Z, float Angle, int Blocks)> PickSites(RoadNetwork net, TownsTuning tuning, SimRandom rng, int townCount)
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
        var places = new List<(float X, float Z, float Angle, int Blocks)>();
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
                places.Add((site.X, site.Z, GridAngle(net, rng, site.X, site.Z), blocks));
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

    /// <summary>Half the diagonal of the grid: every part of the town lies within this of its centre.</summary>
    static float Extent(TownGenerationTuning g, int blocks) => blocks * g.BlockLength * 0.5f * 1.42f;

    static bool Suitable(RoadNetwork net, TownGenerationTuning g, List<(float X, float Z, float Angle, int Blocks)> others, float cx, float cz, int blocks)
    {
        float extent = Extent(g, blocks);
        foreach (var o in others)
        {
            float need = MathF.Max(g.MinSpacing, (extent + Extent(g, o.Blocks)) * 0.6f + g.Sizes.SpacingGap);
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

    /// <summary>Streets run along the contours of the slope; on level ground the angle is drawn.</summary>
    static float GridAngle(RoadNetwork net, SimRandom rng, float cx, float cz)
    {
        float drawn = rng.NextFloat() * MathF.PI * 0.5f;
        if (net.Terrain is not { } terrain) return drawn;
        const float d = 60f;
        float gx = terrain.HeightAt(cx + d, cz) - terrain.HeightAt(cx - d, cz);
        float gz = terrain.HeightAt(cx, cz + d) - terrain.HeightAt(cx, cz - d);
        if (MathF.Sqrt(gx * gx + gz * gz) < 0.5f) return drawn;
        return MathF.Atan2(gz, gx) + MathF.PI * 0.5f;
    }

    /// <summary>Turns a position in a town's own axes (u along its first street direction, v across) into the map's.</summary>
    public static (float X, float Z) ToWorld(float cx, float cz, float angle, float u, float v)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return (cx + u * c - v * s, cz + u * s + v * c);
    }

    static void BuildStreets(RoadNetwork net, float cx, float cz, float angle, int blocks)
    {
        var tuning = net.Tuning;
        float length = blocks * tuning.Towns.Generation.BlockLength;
        float half = length * 0.5f;
        int avenue = blocks / 2; // index of the grid line that is the main street, in each direction
        // The avenues first, so that the two-lane streets are cut by them.
        for (int pass = 0; pass < 2; pass++)
            for (int direction = 0; direction < 2; direction++)
                for (int line = 0; line <= blocks; line++)
                {
                    bool isAvenue = line == avenue && direction == 0;
                    if (isAvenue != (pass == 0)) continue;
                    float offset = -half + line * tuning.Towns.Generation.BlockLength;
                    var (x0, z0) = direction == 0 ? ToWorld(cx, cz, angle, -half, offset) : ToWorld(cx, cz, angle, offset, -half);
                    var (x1, z1) = direction == 0 ? ToWorld(cx, cz, angle, half, offset) : ToWorld(cx, cz, angle, offset, half);
                    var type = isAvenue ? RoadType.Avenue2x2 : RoadType.TwoLane;
                    if (net.Terrain is { } terrain && !TrimToDry(terrain, ref x0, ref z0, ref x1, ref z1)) continue;
                    net.BuildRoad(x0, z0, x1, z1, type, tuning.Roads.Of(type).SpeedLimit);
                }
    }

    /// <summary>Moves each end of a street inward until it is on dry ground (a road may cross water as a bridge but not end in it). False when too little is left.</summary>
    static bool TrimToDry(Terrain terrain, ref float x0, ref float z0, ref float x1, ref float z1)
    {
        const float step = 10f;
        float length = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        float dx = (x1 - x0) / length, dz = (z1 - z0) / length;
        float a = 0f, b = length;
        while (a < b && (terrain.IsWater(x0 + dx * a, z0 + dz * a) || MathF.Abs(x0 + dx * a) > terrain.HalfSize || MathF.Abs(z0 + dz * a) > terrain.HalfSize)) a += step;
        while (b > a && (terrain.IsWater(x0 + dx * b, z0 + dz * b) || MathF.Abs(x0 + dx * b) > terrain.HalfSize || MathF.Abs(z0 + dz * b) > terrain.HalfSize)) b -= step;
        if (b - a < 40f) return false;
        (x1, z1) = (x0 + dx * b, z0 + dz * b);
        (x0, z0) = (x0 + dx * a, z0 + dz * a);
        return true;
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

    static float MaxFootprint(TownsTuning tuning)
    {
        float widest = 0f;
        foreach (var type in Enum.GetValues<BuildingType>())
            widest = MathF.Max(widest, tuning.Of(type, TownsTuning.Tiers - 1).Footprint);
        return widest;
    }

    static List<BuildingPlan> PlanBuildings(RoadNetwork net, RoadIndex index, SimRandom rng, float cx, float cz, float angle, int blocks)
    {
        var tuning = net.Tuning.Towns;
        float half = blocks * tuning.Generation.BlockLength * 0.5f;
        var plans = new List<BuildingPlan>();
        foreach (var site in LotSites(tuning, cx, cz, angle, blocks, 0, blocks - 1, 0, blocks - 1))
        {
            // Drawn in a fixed order for every lot, built on or not, so one lot's fate never shifts another's.
            float fillRoll = rng.NextFloat();
            float typeRoll = rng.NextFloat(), tierRoll = rng.NextFloat();
            var ring = RingAt(tuning, site.U, site.V, half);
            if (fillRoll >= ring.Fill) continue;
            var type = (BuildingType)Pick(ring.Mix, typeRoll);
            int tier = Pick(ring.Tiers, tierRoll);
            if (!index.HasRoadWithin(site.X, site.Z, tuning.AccessRadius)) continue;
            if (net.Terrain is { } terrain && (MathF.Abs(site.X) > terrain.HalfSize || MathF.Abs(site.Z) > terrain.HalfSize)) continue;
            if (net.Terrain is { } land && land.HeightAt(site.X, site.Z) - land.WaterSurfaceAt(site.X, site.Z) < tuning.Generation.WaterMargin) continue;
            plans.Add(new BuildingPlan(type, tier, site.X, site.Z, site.Facing));
        }
        return plans;
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
