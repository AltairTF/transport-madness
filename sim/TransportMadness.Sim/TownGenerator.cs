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
        var places = new List<(float X, float Z, float Angle, int Blocks)>();
        var names = ShuffledNames(tuning, rng);

        for (int t = 0; t < townCount; t++)
        {
            bool found = false;
            for (int attempt = 0; attempt < tuning.Generation.Attempts && !found; attempt++)
            {
                int blocks = tuning.Generation.BlocksMin + rng.NextInt(tuning.Generation.BlocksMax - tuning.Generation.BlocksMin + 1);
                var (cx, cz) = RandomSpot(net, tuning.Generation, rng);
                if (!Suitable(net, tuning.Generation, places, cx, cz, blocks)) continue;
                places.Add((cx, cz, GridAngle(net, rng, cx, cz), blocks));
                found = true;
            }
            if (!found) break; // no room for this one, so none for the next either
        }

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

    static (float X, float Z) RandomSpot(RoadNetwork net, TownGenerationTuning g, SimRandom rng)
    {
        float half = net.Terrain?.HalfSize ?? 2048f;
        float range = half - g.EdgeMargin;
        return ((rng.NextFloat() * 2f - 1f) * range, (rng.NextFloat() * 2f - 1f) * range);
    }

    /// <summary>Half the diagonal of the grid: every part of the town lies within this of its centre.</summary>
    static float Extent(TownGenerationTuning g, int blocks) => blocks * g.BlockLength * 0.5f * 1.42f;

    static bool Suitable(RoadNetwork net, TownGenerationTuning g, List<(float X, float Z, float Angle, int Blocks)> others, float cx, float cz, int blocks)
    {
        foreach (var o in others)
            if ((o.X - cx) * (o.X - cx) + (o.Z - cz) * (o.Z - cz) < g.MinSpacing * g.MinSpacing) return false;
        if (net.Terrain is not { } terrain) return true;
        // The grid may lie at any angle, so test the whole disc around it: dry ground, and not too much height.
        float extent = Extent(g, blocks);
        float lowest = float.MaxValue, highest = float.MinValue;
        const int steps = 9;
        for (int i = 0; i < steps; i++)
            for (int j = 0; j < steps; j++)
            {
                float x = cx + (i / (steps - 1f) * 2f - 1f) * extent, z = cz + (j / (steps - 1f) * 2f - 1f) * extent;
                if (MathF.Abs(x) > terrain.HalfSize || MathF.Abs(z) > terrain.HalfSize) return false;
                float h = terrain.HeightAt(x, z);
                if (h - terrain.WaterSurfaceAt(x, z) < g.WaterMargin) return false;
                lowest = MathF.Min(lowest, h);
                highest = MathF.Max(highest, h);
            }
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
    static (float X, float Z) ToWorld(float cx, float cz, float angle, float u, float v)
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
                    net.BuildRoad(x0, z0, x1, z1, type, tuning.Roads.Of(type).SpeedLimit);
                }
    }

    static List<BuildingPlan> PlanBuildings(RoadNetwork net, RoadIndex index, SimRandom rng, float cx, float cz, float angle, int blocks)
    {
        var tuning = net.Tuning.Towns;
        var g = tuning.Generation;
        float half = blocks * g.BlockLength * 0.5f;
        float avenueHalf = RoadNetwork.HalfWidth(RoadType.Avenue2x2);
        var plans = new List<BuildingPlan>();
        // Each block has lots on its four sides, facing the street. Along a side they stand a lot width apart, centred, so
        // that the ones on neighbouring sides keep clear of each other at the corner. A building's middle is as far
        // from its street as its own size needs (and an avenue's width, in case the street is widened later).
        for (int i = 0; i < blocks; i++)
            for (int j = 0; j < blocks; j++)
                for (int side = 0; side < 4; side++)
                    for (int lot = 0; lot < LotsPerSide; lot++)
                    {
                        float u0 = -half + i * g.BlockLength, v0 = -half + j * g.BlockLength;
                        float along = g.BlockLength * 0.5f + (lot - (LotsPerSide - 1) * 0.5f) * g.LotWidth;
                        // Drawn in a fixed order for every lot, built on or not, so one lot's fate never shifts another's.
                        float fillRoll = rng.NextFloat();
                        float typeRoll = rng.NextFloat(), tierRoll = rng.NextFloat();
                        var ring = RingAt(tuning, u0 + g.BlockLength * 0.5f, v0 + g.BlockLength * 0.5f, half);
                        if (fillRoll >= ring.Fill) continue;
                        var type = (BuildingType)Pick(ring.Mix, typeRoll);
                        int tier = Pick(ring.Tiers, tierRoll);
                        float setback = avenueHalf + g.SetbackGap + tuning.Of(type, tier).Footprint;
                        // Local axes: u along the first street direction, v across. Sides: bottom, top, left, right.
                        (float u, float v, float facing) = side switch
                        {
                            0 => (u0 + along, v0 + setback, -MathF.PI * 0.5f),
                            1 => (u0 + along, v0 + g.BlockLength - setback, MathF.PI * 0.5f),
                            2 => (u0 + setback, v0 + along, MathF.PI),
                            _ => (u0 + g.BlockLength - setback, v0 + along, 0f),
                        };
                        var (wx, wz) = ToWorld(cx, cz, angle, u, v);
                        if (!index.HasRoadWithin(wx, wz, tuning.AccessRadius)) continue;
                        if (net.Terrain is { } terrain && (MathF.Abs(wx) > terrain.HalfSize || MathF.Abs(wz) > terrain.HalfSize)) continue;
                        plans.Add(new BuildingPlan(type, tier, wx, wz, facing + angle));
                    }
        return plans;
    }

    static RingTuning RingAt(TownsTuning tuning, float u, float v, float half)
    {
        float d = MathF.Max(MathF.Abs(u), MathF.Abs(v)) / half;
        foreach (var ring in tuning.Rings)
            if (d <= ring.Until) return ring;
        return tuning.Rings[^1];
    }

    static int Pick(IReadOnlyList<float> weights, float unit)
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
