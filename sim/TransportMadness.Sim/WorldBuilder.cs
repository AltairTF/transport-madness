namespace TransportMadness.Sim;

/// <summary>How big a generated map is: 3.1 km, 4.1 km or 8.2 km on a side (cells of 8 m).</summary>
public enum MapSize { Small, Normal, Large }

/// <summary>How a new game is put together, so that the game layer only chooses the options.</summary>
public static class WorldBuilder
{
    /// <summary>
    /// A map of generated hills with <paramref name="towns"/> towns (fewer when the land runs out; see <see cref="WorldPlan.Requested"/>) and
    /// industries and the two gateways, with <c>traffic.ambientPerTown</c> ambient cars per town put on the roads by a logged command. The land follows <paramref name="terrain"/> (how many mountains, how much water and forest). The plan says what was
    /// placed and which processors found no supplier within reach.
    /// </summary>
    /// <summary>The most towns a map of this size is asked for (the new-game slider's top): the region is small and its towns are villages.</summary>
    public static int MostTowns(MapSize size) => size switch { MapSize.Small => 3, MapSize.Large => 7, _ => 5 };

    public static (Simulation Sim, WorldPlan Plan) Towns(ulong seed, int towns, IndustryAmount industries, Tuning? tuning = null, IncidentFrequency frequency = IncidentFrequency.Normal, TerrainSettings? terrain = null, MapSize size = MapSize.Normal)
    {
        towns = Math.Clamp(towns, 1, MostTowns(size));
        var net = new RoadNetwork(Terrain.Generate(seed, CellsOf(size), settings: terrain), tuning);
        var plan = GatewayGenerator.Generate(net, seed, IndustryGenerator.Generate(net, seed, TownGenerator.Generate(net, seed, towns), industries));
        var sim = new Simulation(net, seed, frequency);
        sim.AddTowns(plan);
        sim.Execute(new SeedTrafficCommand(net.Tuning.Traffic.AmbientPerTown * Math.Max(1, plan.Towns.Count)));
        return (sim, plan);
    }

    /// <summary>Cells of 8 m on a side of a map of this size.</summary>
    public static int CellsOf(MapSize size) => size switch { MapSize.Small => 384, MapSize.Large => 1024, _ => 512 };

    /// <summary>
    /// A coarse copy of the land of <see cref="Towns"/> for the new-game screen: the same seed and settings on a grid of
    /// <paramref name="cells"/> cells covering the whole map, so it shows the same lakes, rivers and mountains.
    /// </summary>
    public static Terrain Preview(ulong seed, MapSize size, TerrainSettings? terrain = null, int cells = 128) =>
        Terrain.Generate(seed, cells, CellsOf(size) * 8f / cells, settings: terrain);
}
