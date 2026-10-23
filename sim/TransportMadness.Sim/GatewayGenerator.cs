namespace TransportMadness.Sim;

/// <summary>
/// Places the Region's two gateways, one for roads and one for rail, on level dry ground at the edge of the map, on two different
/// sides, clear of the towns and industries and apart from each other. Like the towns and industries it draws from its own random
/// generator, so adding gateways moves nothing else. Connecting them is the player's job.
/// </summary>
public static class GatewayGenerator
{
    const ulong SeedSalt = 0x4741544557415953UL; // keeps this stream apart from the others'
    static readonly string[] SideNames = { "North", "East", "South", "West" };

    /// <summary>
    /// <paramref name="plan"/> with its gateways added. A gateway that finds no place within reach of a town takes any place that
    /// will do; one that finds none on any side is left out.
    /// </summary>
    public static WorldPlan Generate(RoadNetwork net, ulong seed, WorldPlan plan)
    {
        var tuning = net.Tuning.Gateways;
        var g = tuning.Generation;
        var rng = new SimRandom(seed ^ SeedSalt);
        float half = net.Terrain?.HalfSize ?? 2048f;
        var placed = new List<GatewayPlan>();
        int firstSide = (int)(rng.NextFloat() * 4f) % 4;
        int secondSide = (firstSide + 1 + (int)(rng.NextFloat() * 3f) % 3) % 4;
        foreach (var (kind, preferred) in new[] { (GatewayKind.Road, firstSide), (GatewayKind.Rail, secondSide) })
        {
            // The preferred side first, then the others in turn; a side already holding the other gateway comes last.
            for (int pass = 0; pass < 2 && placed.Count(p => p.Kind == kind) == 0; pass++)
                for (int k = 0; k < 4 && placed.Count(p => p.Kind == kind) == 0; k++)
                {
                    int side = (preferred + k) % 4;
                    if (pass == 0 && placed.Any(p => SideOf(p, half) == side)) continue;
                    for (int attempt = 0; attempt < g.Attempts; attempt++)
                    {
                        float along = (rng.NextFloat() * 2f - 1f) * (half - g.EdgeMargin);
                        var (x, z) = side switch
                        {
                            0 => (along, -(half - g.EdgeInset)),
                            1 => (half - g.EdgeInset, along),
                            2 => (along, half - g.EdgeInset),
                            _ => (-(half - g.EdgeInset), along),
                        };
                        if (!Suitable(net, plan, placed, x, z)) continue;
                        if (pass == 0 && !plan.Towns.Any(t => (t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z) <= g.TownReach * g.TownReach)) continue;
                        float angle = side % 2 == 0 ? 0f : MathF.PI * 0.5f;
                        placed.Add(new GatewayPlan(kind, $"{SideNames[side]} {(kind == GatewayKind.Road ? "Road" : "Rail")} Gate", x, z, angle));
                        break;
                    }
                }
        }
        return plan with { Gateways = placed };
    }

    static int SideOf(GatewayPlan p, float half) =>
        MathF.Abs(p.Z) >= MathF.Abs(p.X) ? (p.Z < 0 ? 0 : 2) : (p.X > 0 ? 1 : 3);

    static bool Suitable(RoadNetwork net, WorldPlan plan, List<GatewayPlan> others, float x, float z)
    {
        var tuning = net.Tuning.Gateways;
        var g = tuning.Generation;
        float reach = tuning.Size * 1.42f;
        foreach (var town in plan.Towns)
        {
            float clear = town.Reach(net.Tuning.Towns.Generation) + g.TownClearance + reach;
            if ((town.X - x) * (town.X - x) + (town.Z - z) * (town.Z - z) < clear * clear) return false;
        }
        foreach (var o in plan.Industries ?? Array.Empty<IndustryPlan>())
            if ((o.X - x) * (o.X - x) + (o.Z - z) * (o.Z - z) < g.MinSpacing * g.MinSpacing) return false;
        foreach (var o in others)
            if ((o.X - x) * (o.X - x) + (o.Z - z) * (o.Z - z) < g.MinSeparation * g.MinSeparation) return false;
        return IndustryGenerator.LevelAndDry(net, net.Tuning.Towns, x, z, reach, g.MaxHeightRange);
    }
}
