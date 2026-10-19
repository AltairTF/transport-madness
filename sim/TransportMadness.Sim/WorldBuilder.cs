namespace TransportMadness.Sim;

/// <summary>How a new game is put together, so that the game layer only chooses the options.</summary>
public static class WorldBuilder
{
    /// <summary>
    /// A map of generated hills with <paramref name="towns"/> towns (fewer when the land runs out; see <see cref="WorldPlan.Requested"/>) and
    /// industries, with <c>traffic.ambientPerTown</c> ambient cars per town put on the roads by a logged command. The plan says what was
    /// placed and which processors found no supplier within reach.
    /// </summary>
    public static (Simulation Sim, WorldPlan Plan) Towns(ulong seed, int towns, IndustryAmount industries, Tuning? tuning = null)
    {
        var net = new RoadNetwork(Terrain.Generate(seed), tuning);
        var plan = IndustryGenerator.Generate(net, seed, TownGenerator.Generate(net, seed, towns), industries);
        var sim = new Simulation(net, seed);
        sim.AddTowns(plan);
        sim.Execute(new SeedTrafficCommand(net.Tuning.Traffic.AmbientPerTown * Math.Max(1, plan.Towns.Count)));
        return (sim, plan);
    }
}
