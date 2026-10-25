using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>
/// Save and load (M6a): a loaded game is the same game. Each world is saved, loaded, and then both copies are run on side by side:
/// the snapshot of the loaded copy has the same bytes as the original's (anything read back wrong shows), and after running on
/// both have the same state hash and again the same snapshot bytes (anything that was not saved and changes the future shows).
/// </summary>
public class SaveLoadTests
{
    static Simulation SaveAndLoad(Simulation sim, byte[]? host = null)
    {
        using var stream = new MemoryStream();
        SaveGame.Save(sim, stream, SaveGame.InfoOf(sim, "test", new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc)), host);
        stream.Position = 0;
        return SaveGame.Load(stream).Sim;
    }

    static void Step(Simulation sim, double seconds)
    {
        long ticks = (long)Math.Round(seconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static void AssertLoadedGameRunsOnTheSame(Simulation sim, double seconds)
    {
        var loaded = SaveAndLoad(sim);
        Assert.Equal(sim.StateHash(), loaded.StateHash());
        Assert.True(SaveGame.Capture(sim).AsSpan().SequenceEqual(SaveGame.Capture(loaded)), "the snapshot of the loaded game differs from the original's");
        Step(sim, seconds);
        Step(loaded, seconds);
        Assert.Equal(sim.StateHash(), loaded.StateHash());
        Assert.True(SaveGame.Capture(sim).AsSpan().SequenceEqual(SaveGame.Capture(loaded)), "the two games drifted apart after the load");
    }

    [Fact]
    public void OneWayRamp_RoundTrips_AndRunsOnTheSame()
    {
        var sim = new Simulation(OneWayTests.Loop().Net, 7);
        sim.Execute(new SeedTrafficCommand(30));
        Step(sim, 5);
        var loaded = SaveAndLoad(sim);
        Assert.True(loaded.Network.GetRoad(new RoadId(4)).IsOneWay);
        AssertLoadedGameRunsOnTheSame(sim, 60);
    }

    [Fact]
    public void DirtAndAsymmetricRoads_RoundTrip_AndRunOnTheSame()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Flat()), 7);
        sim.Execute(new BuildRoadCommand(0, 0, 300, 0, RoadType.Dirt, 9f));
        sim.Execute(new BuildRoadCommand(300, 0, 600, 0, RoadType.TwoLane, 14f, 0f, 2, 1));
        sim.Execute(new SeedTrafficCommand(20));
        Step(sim, 5);
        var loaded = SaveAndLoad(sim);
        var road = loaded.Network.GetRoad(new RoadId(1));
        Assert.Equal((RoadType.TwoLane, 2, 1), (road.Type, road.LanesForward, road.LanesBack));
        Assert.Equal(RoadType.Dirt, loaded.Network.GetRoad(new RoadId(0)).Type);
        AssertLoadedGameRunsOnTheSame(sim, 60);
    }

    [Fact]
    public void AnEmptyMap_RoundTrips()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Flat()), 7);
        AssertLoadedGameRunsOnTheSame(sim, 10);
    }

    [Fact]
    public void BusesAndAmbientTraffic_InGeneratedTowns_RunOnTheSameAfterALoad()
    {
        var w = Worlds.BusScenario(3, stops: 2, buses: 3) ?? throw new InvalidOperationException("no bus scenario");
        w.Sim.Execute(new SeedTrafficCommand(60));
        Worlds.Run(w.Sim, 0.5);
        AssertLoadedGameRunsOnTheSame(w.Sim, 600);
    }

    [Fact]
    public void StopsAtIndustriesWithOptions_AndAFreeStandingDepot_RunOnTheSameAfterALoad()
    {
        var (sim, depot, docks) = DockStopTests.World();
        int line = sim.Execute(new CreateLineCommand(new[] { docks[0], docks[1], docks[2] }));
        sim.Execute(new SetStopOptionsCommand(line, 1, false, -1, true, 50));
        sim.Execute(new BuyVehicleCommand(depot, "truck", line));
        sim.Execute(new BuildDepotCommand(900, 300, DepotLevel.Simple)); // alone, 300 m from the road
        Step(sim, 3 * sim.DayLengthSeconds);
        AssertLoadedGameRunsOnTheSame(sim, 900);
    }

    [Fact]
    public void TrucksOnAChain_WithIncidentsDrawn_RunOnTheSameAfterALoad()
    {
        var tuning = Tuning.Load();
        var w = Worlds.TruckScenario(2, trucks: 4, tuning: tuning) ?? throw new InvalidOperationException("no truck scenario");
        w.Sim.Execute(new SetFrequencyCommand(IncidentFrequency.High));
        w.Sim.Execute(new SeedTrafficCommand(40));
        Worlds.Run(w.Sim, 0.5);
        AssertLoadedGameRunsOnTheSame(w.Sim, 900);
    }

    [Fact]
    public void ATrainOnItsLine_RunsOnTheSameAfterALoad()
    {
        var w = MaintenanceWorlds.Rail();
        MaintenanceWorlds.Train(w);
        MaintenanceWorlds.Run(w.Sim, 300);
        AssertLoadedGameRunsOnTheSame(w.Sim, 600);
    }

    /// <summary>
    /// A generated campaign map with its gateways, a bus line and a battery, where the call-up and a requisition come in the first months and a flight is
    /// saved while its bombs are in the air and the siren is on.
    /// </summary>
    [Fact]
    public void AWarWorld_SavedWithBombsFallingAndTheSirenOn_RunsOnTheSameAfterALoad()
    {
        var t = Worlds.Late(Tuning.Load());
        var war = t.War with
        {
            Conscription = t.War.Conscription with { Waves = new[] { new WarWave { Month = 1, Share = 0.1f } } },
            Requisition = t.War.Requisition with { Waves = new[] { new WarWave { Month = 1, Share = 0.3f } } },
            Bombing = t.War.Bombing with { FromMonth = 1000 },
            AntiAircraft = t.War.AntiAircraft with { FromMonth = 0 },
        };
        var (sim, _) = WorldBuilder.Towns(11, 4, IndustryAmount.Normal, t with { War = war });
        sim.Execute(new TakeLoanCommand(1000000));
        var (i, j) = Worlds.ClosestPair(sim);
        Assert.True(Worlds.Connect(sim, sim.Towns[i], sim.Towns[j]));
        var sa = Worlds.StationsNear(sim, sim.Towns[i].X, sim.Towns[i].Z, 2);
        var sb = Worlds.StationsNear(sim, sim.Towns[j].X, sim.Towns[j].Z, 2);
        int depot = Worlds.Depot(sim, sim.Towns[i]);
        int line = sim.Execute(new CreateLineCommand(sa.Concat(Enumerable.Reverse(sb)).ToArray()));
        Assert.True(line >= 0 && depot >= 0);
        for (int k = 0; k < 4; k++) Worlds.Buy(sim, depot, "bus", line);
        var town = sim.Towns[i];
        int industries = sim.Industries.Count;
        for (int k = 0; k < 12 && sim.Industries.Count == industries; k++)
            sim.Execute(new BuildBatteryCommand(town.X + 200f + 60f * k, town.Z - 200f - 40f * k));

        // Into month 1: the call-up wave and the requisition come at its start.
        Worlds.Run(sim, t.Economy.DaysPerMonth + 0.2);
        Assert.True(sim.CalledUp > 0, "the call-up did not come");
        AssertLoadedGameRunsOnTheSame(sim, 60);

        // A flight, saved once its first bombs are falling.
        sim.LaunchFlightForTest();
        Assert.True(sim.SirenOn);
        for (int k = 0; k < 200000 && sim.BombsFalling.Count == 0; k++) sim.Step();
        Assert.True(sim.BombsFalling.Count > 0, "no bomb fell");
        AssertLoadedGameRunsOnTheSame(sim, 400);
    }

    [Fact]
    public void TheCommandLogOfALoadedGameStartsEmpty_AndTheInfoAndTheGameLayersBlockComeBack()
    {
        var w = Worlds.BusScenario(3, stops: 1, buses: 1) ?? throw new InvalidOperationException("no bus scenario");
        Assert.NotEmpty(w.Sim.CommandLog);
        using var stream = new MemoryStream();
        var info = SaveGame.InfoOf(w.Sim, "Slot one", new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc));
        SaveGame.Save(w.Sim, stream, info, new byte[] { 1, 2, 3 });
        stream.Position = 0;
        var header = SaveGame.ReadHeader(stream);
        Assert.Equal(info, header.Info);
        Assert.Equal(new byte[] { 1, 2, 3 }, header.Host);
        stream.Position = 0;
        var (loaded, loadedInfo, host) = SaveGame.Load(stream);
        Assert.Empty(loaded.CommandLog);
        Assert.Equal(info, loadedInfo);
        Assert.Equal(new byte[] { 1, 2, 3 }, host);
        Assert.Equal(w.Sim.Company.Money, loaded.Company.Money);
    }

    [Fact]
    public void ALoadedGameKeepsTheTuningItWasStartedWith()
    {
        var t = Tuning.Load();
        var tuning = t with { Economy = t.Economy with { StartingMoney = 1234567 } };
        var sim = new Simulation(new RoadNetwork(Terrain.Flat(), tuning), 7);
        var loaded = SaveAndLoad(sim);
        Assert.Equal(1234567, loaded.Network.Tuning.Economy.StartingMoney);
        Assert.Equal(1234567, loaded.Company.Money);
    }

    [Fact]
    public void ASaveOfAnotherFormatOrDamaged_IsRefusedWithAMessage()
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Flat()), 7);
        using var stream = new MemoryStream();
        SaveGame.Save(sim, stream, SaveGame.InfoOf(sim, "x", DateTime.UnixEpoch));
        var bytes = stream.ToArray();

        var other = (byte[])bytes.Clone();
        other[8] = 99; // the format version
        var e = Assert.Throws<SaveFormatException>(() => SaveGame.Load(new MemoryStream(other)));
        Assert.Contains("another version", e.Message);

        Assert.Throws<SaveFormatException>(() => SaveGame.Load(new MemoryStream(bytes[..(bytes.Length / 2)])));
        Assert.Throws<SaveFormatException>(() => SaveGame.Load(new MemoryStream("not a save at all"u8.ToArray())));
    }

    [Fact]
    public void ASavedGeneratedWorld_KeepsItsBiomesAndItsLand()
    {
        var (sim, _) = WorldBuilder.Towns(4, 3, IndustryAmount.Few, terrain: new TerrainSettings(0.8f, 0.2f, 1f));
        var loaded = SaveAndLoad(sim);
        var a = sim.Network.Terrain!;
        var b = loaded.Network.Terrain!;
        Assert.Equal(a.Hash(), b.Hash());
        for (int j = 0; j < a.Samples; j += 11)
            for (int i = 0; i < a.Samples; i += 11)
                Assert.Equal(a.BiomeAt(i, j), b.BiomeAt(i, j));
    }

    [Fact]
    public void TheTerrainSettings_ChangeTheWorldBuilt()
    {
        var plain = WorldBuilder.Towns(4, 3, IndustryAmount.Few).Sim.Network.Terrain!.Hash();
        var other = WorldBuilder.Towns(4, 3, IndustryAmount.Few, terrain: new TerrainSettings(1f, 1f, 1f)).Sim.Network.Terrain!.Hash();
        Assert.NotEqual(plain, other);
        Assert.Equal(plain, WorldBuilder.Towns(4, 3, IndustryAmount.Few, terrain: TerrainSettings.Default).Sim.Network.Terrain!.Hash());
    }
}
