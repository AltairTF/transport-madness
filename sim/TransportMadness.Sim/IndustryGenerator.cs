namespace TransportMadness.Sim;

/// <summary>
/// Places industries on a map after its towns: on level dry ground, clear of every town's grid and of each other. Like
/// <see cref="TownGenerator"/> it draws from its own random generator, so the same seed gives the same industries and
/// nothing else depends on it. Roads to them are the player's job.
/// </summary>
public static class IndustryGenerator
{
    const ulong SeedSalt = 0x494E445553545259UL; // keeps this stream apart from the towns' and the simulation's

    /// <summary>
    /// The towns of <paramref name="towns"/> with industries added, <c>industries.counts</c> of each type for the chosen
    /// amount. Types are placed one after the other in the order of the tuning file, so a type that cannot find room does
    /// not take the place of the next; fewer come out when the land runs out.
    /// </summary>
    public static WorldPlan Generate(RoadNetwork net, ulong seed, WorldPlan towns, IndustryAmount amount)
    {
        var tuning = net.Tuning.Industries;
        var townTuning = net.Tuning.Towns;
        var rng = new SimRandom(seed ^ SeedSalt);
        var counts = tuning.Counts.Of(amount);
        var placed = new List<IndustryPlan>();
        float half = net.Terrain?.HalfSize ?? 2048f;

        for (int type = 0; type < tuning.Types.Count; type++)
            for (int n = 0; n < counts[type]; n++)
                for (int attempt = 0; attempt < tuning.Generation.Attempts; attempt++)
                {
                    float range = half - tuning.Generation.EdgeMargin;
                    float x = (rng.NextFloat() * 2f - 1f) * range, z = (rng.NextFloat() * 2f - 1f) * range;
                    float angle = rng.NextFloat() * MathF.PI * 0.5f;
                    if (!Suitable(net, tuning, townTuning, towns, placed, tuning.Types[type], x, z)) continue;
                    placed.Add(new IndustryPlan(type, x, z, angle));
                    break;
                }
        return towns with { Industries = placed };
    }

    static bool Suitable(RoadNetwork net, IndustriesTuning tuning, TownsTuning townTuning, WorldPlan towns, List<IndustryPlan> others, IndustryTypeTuning type, float x, float z)
    {
        float reach = type.Size * 1.42f;
        foreach (var town in towns.Towns)
        {
            float clear = town.Blocks * townTuning.Generation.BlockLength * 0.5f * 1.42f + tuning.Generation.TownClearance + reach;
            if ((town.X - x) * (town.X - x) + (town.Z - z) * (town.Z - z) < clear * clear) return false;
        }
        foreach (var o in others)
            if ((o.X - x) * (o.X - x) + (o.Z - z) * (o.Z - z) < tuning.Generation.MinSpacing * tuning.Generation.MinSpacing) return false;
        if (net.Terrain is not { } terrain) return true;
        float lowest = float.MaxValue, highest = float.MinValue;
        const int steps = 5;
        for (int i = 0; i < steps; i++)
            for (int j = 0; j < steps; j++)
            {
                float px = x + (i / (steps - 1f) * 2f - 1f) * reach, pz = z + (j / (steps - 1f) * 2f - 1f) * reach;
                if (MathF.Abs(px) > terrain.HalfSize || MathF.Abs(pz) > terrain.HalfSize) return false;
                float h = terrain.HeightAt(px, pz);
                if (h - terrain.WaterSurfaceAt(px, pz) < townTuning.Generation.WaterMargin) return false;
                lowest = MathF.Min(lowest, h);
                highest = MathF.Max(highest, h);
            }
        return highest - lowest <= tuning.Generation.MaxHeightRange;
    }
}
