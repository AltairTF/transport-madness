using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The ground under a depot or a battery is levelled (M6g), and a depot can be rotated, raised and placed exactly where it is put.</summary>
public class PadTests
{
    /// <summary>The ground rises 10 % toward +x: 5 m at x = 0.</summary>
    static Terrain Slope(float rise = 0.1f) => Terrain.Create(512, 8f, (x, _) => 5f + rise * x);

    static Simulation World(Terrain terrain, Tuning? tuning = null)
    {
        var sim = new Simulation(new RoadNetwork(terrain, tuning ?? Tuning.Load()), 7, IncidentFrequency.Off);
        sim.Company.Money = 10_000_000;
        return sim;
    }

    static int Build(Simulation sim, float x0, float z0, float x1, float z1) => sim.Execute(new BuildRoadCommand(x0, z0, x1, z1, RoadType.TwoLane, 14f));

    [Fact]
    public void LevellingAPad_MakesTheFootprintFlat_AndLeavesTheFarGroundAlone()
    {
        var terrain = Slope();
        float far = terrain.HeightAt(400, 0), farBefore = terrain.HeightAt(0, 0);
        int version = terrain.Version;
        var measured = terrain.LevelPad(200, 0, 0.3f, 11f, 25f, 10f, apply: false);
        Assert.Equal(version, terrain.Version); // measuring changes nothing
        Assert.True(measured.Cut > 0 && measured.Fill > 0);

        var applied = terrain.LevelPad(200, 0, 0.3f, 11f, 25f, 10f, apply: true);
        Assert.Equal(measured, applied);
        Assert.True(terrain.Version > version);
        foreach (var (dx, dz) in new[] { (0f, 0f), (10f, 10f), (-10f, 10f), (10f, -10f), (-10f, -10f) })
        {
            float c = MathF.Cos(0.3f), s = MathF.Sin(0.3f);
            Assert.Equal(25f, terrain.HeightAt(200 + dx * c - dz * s, dx * s + dz * c), 0.001f);
        }
        Assert.Equal(far, terrain.HeightAt(400, 0), 0.001f);
        Assert.Equal(farBefore, terrain.HeightAt(0, 0), 0.001f);
    }

    [Fact]
    public void ADepotOnASlope_IsLevelled_AndThePadIsPaidFor()
    {
        var sim = World(Slope());
        float mean = sim.Network.Terrain!.MeanHeight(300, 0, 0f, 11f);
        var plan = sim.PlanDepot(300, 0, free: true, angle: 0f);
        Assert.True(plan.Valid, plan.Reason);
        Assert.Equal(mean, plan.Pad.Height, 0.01f);
        Assert.True(plan.PadMoney > 0);

        long before = sim.Company.Money;
        int id = sim.Execute(new BuildDepotCommand(300, 0, DepotLevel.Simple, true, 0f));
        Assert.Equal(0, id);
        Assert.Equal(before - sim.DepotPrice(false, DepotLevel.Simple) - plan.PadMoney, sim.Company.Money);
        var terrain = sim.Network.Terrain!;
        foreach (var (dx, dz) in new[] { (0f, 0f), (10f, 10f), (-10f, -10f), (10f, -10f), (-10f, 10f) })
            Assert.Equal(plan.Pad.Height, terrain.HeightAt(300 + dx, dz), 0.001f);
    }

    [Fact]
    public void RaisingAPad_LiftsTheFloor_AndCostsMore()
    {
        var sim = World(Slope());
        var level = sim.PlanDepot(300, 0, free: true, angle: 0f);
        var raised = sim.PlanDepot(300, 0, free: true, angle: 0f, raise: 2f);
        Assert.Equal(level.Pad.Height + 2f, raised.Pad.Height, 0.001f);
        Assert.NotEqual(level.PadMoney, raised.PadMoney);
        sim.Execute(new BuildDepotCommand(300, 0, DepotLevel.Simple, true, 0f, 2f));
        Assert.Equal(raised.Pad.Height, sim.Network.Terrain!.HeightAt(300, 0), 0.001f);
    }

    [Fact]
    public void APadThatWouldCutOrFillTooMuch_IsRefused_AndNothingIsSpent()
    {
        var sim = World(Slope(0.5f));
        var plan = sim.PlanDepot(300, 0, free: true, angle: 0f);
        Assert.False(plan.Valid);
        Assert.Contains("at most", plan.Reason);
        long before = sim.Company.Money;
        Assert.Equal(-1, sim.Execute(new BuildDepotCommand(300, 0, DepotLevel.Simple, true, 0f)));
        Assert.Equal(before, sim.Company.Money);
        Assert.Empty(sim.Depots);
    }

    [Fact]
    public void WithTheSnapOff_ADepotStandsWhereItIsPut_AndKeepsItsAngle()
    {
        var sim = World(Terrain.Create(512, 8f, (_, _) => 5f));
        Assert.Equal(1, Build(sim, 0, 100, 1000, 100));
        // 12 m from the road the snap would draw it against the road (see FreeDepotTests); with the snap off it stays, door to the north.
        var snapped = sim.PlanDepot(500, 112);
        Assert.Equal(DepotConnection.Beside, snapped.Connection);
        float north = -MathF.PI / 2f;
        var free = sim.PlanDepot(500, 140, free: true, angle: north);
        Assert.Equal(500f, free.X);
        Assert.Equal(140f, free.Z);
        Assert.Equal(north, free.Angle, 0.001f);
        Assert.Equal(DepotConnection.Path, free.Connection); // door at z = 140 - 14 = 126, 26 m from the road: a Path, not a split
    }

    [Fact]
    public void WithTheSnapOff_TheDoorIsStillJoinedToTheRoadByAPath()
    {
        var sim = World(Terrain.Create(512, 8f, (_, _) => 5f));
        Assert.Equal(1, Build(sim, 0, 100, 1000, 100));
        float north = -MathF.PI / 2f;
        var free = sim.PlanDepot(500, 140, free: true, angle: north);
        Assert.True(free.Valid, free.Reason);
        Assert.Equal(DepotConnection.Path, free.Connection);
        Assert.Equal(126f, free.AccessZ, 0.01f);
        long before = sim.Company.Money;
        int id = sim.Execute(new BuildDepotCommand(500, 140, DepotLevel.Simple, true, north));
        Assert.Equal(0, id);
        Assert.True(sim.Depots[id].Active);
        Assert.Equal(140f, sim.Depots[id].Z);
        Assert.True(before - sim.Company.Money > sim.DepotPrice(false, DepotLevel.Simple)); // the Path was paid for too
    }

    [Fact]
    public void WithTheSnapOff_ADoorOnTheRoadSplitsIt_AndADepotOnTheRoadIsRefused()
    {
        var sim = World(Terrain.Create(512, 8f, (_, _) => 5f));
        Assert.Equal(1, Build(sim, 0, 100, 1000, 100));
        var onRoad = sim.PlanDepot(500, 114, free: true, angle: -MathF.PI / 2f); // the door at z = 100
        Assert.Equal(DepotConnection.Beside, onRoad.Connection);
        Assert.False(sim.PlanDepot(500, 104, free: true, angle: 0f).Valid);
    }

    [Fact]
    public void APadLeavesTheGroundUnderARoadAlone()
    {
        var sim = World(Slope());
        Assert.Equal(1, Build(sim, 300, -200, 300, 200));
        var terrain = sim.Network.Terrain!;
        var onRoad = Enumerable.Range(0, 13).Select(i => (X: 300f, Z: 10f + i * 5f)).ToArray();
        var before = onRoad.Select(p => terrain.HeightAt(p.X, p.Z)).ToArray();
        // 30 m east of the road: the pad's shoulder reaches the road, its samples are skipped. The door faces north so the Path joins the road north of the points looked at.
        Assert.True(sim.Execute(new BuildDepotCommand(330, 0, DepotLevel.Simple, true, -MathF.PI / 2f)) >= 0);
        Assert.NotEqual(before[0], terrain.HeightAt(320, 0)); // the pad was made
        for (int i = 0; i < onRoad.Length; i++)
            Assert.True(MathF.Abs(before[i] - terrain.HeightAt(onRoad[i].X, onRoad[i].Z)) < 0.001f, $"z = {onRoad[i].Z}: {before[i]} became {terrain.HeightAt(onRoad[i].X, onRoad[i].Z)}");
    }

    [Fact]
    public void TheSameCommands_GiveTheSameGround()
    {
        static ulong Run()
        {
            var sim = World(Slope());
            sim.Execute(new BuildDepotCommand(300, 0, DepotLevel.Simple, true, 0.5f, 1f));
            sim.Execute(new BuildDepotCommand(300, 200, DepotLevel.Workshop));
            return sim.Network.Terrain!.Hash();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ABattery_StandsTurnedAndRaised_OnALevelledPad()
    {
        var tuning = Worlds.Late(Tuning.Load());
        tuning = tuning with { War = tuning.War with { AntiAircraft = tuning.War.AntiAircraft with { FromMonth = 0 } } };
        var sim = World(Terrain.Create(512, 8f, (_, _) => 5f), tuning);
        var (pad, why) = sim.PlanBattery(600, 400, 0.7f, 2f);
        Assert.Null(why);
        Assert.Equal(7f, pad.Height, 0.001f);
        long before = sim.Company.Money;
        int id = sim.Execute(new BuildBatteryCommand(600, 400, 0.7f, 2f));
        Assert.True(id >= 0);
        var battery = sim.Industries[id];
        Assert.Equal(0.7f, battery.Angle, 0.0001f);
        Assert.Equal(7f, battery.Y, 0.001f);
        Assert.Equal(7f, sim.Network.Terrain!.HeightAt(600, 400), 0.001f);
        Assert.Equal(before - sim.AntiAircraft.Price - pad.Money, sim.Company.Money);
    }

    [Fact]
    public void ATurnWithTheSnapOn_KeepsTheSnappedPlace_AndOnlyChangesTheWayItFaces()
    {
        var sim = World(Terrain.Create(512, 8f, (_, _) => 5f));
        Assert.Equal(1, Build(sim, 0, 100, 1000, 100));
        var snapped = sim.PlanDepot(500, 112);
        Assert.Equal(DepotConnection.Beside, snapped.Connection);
        var turned = sim.PlanDepot(500, 112, free: false, angle: 0f); // facing east, away from the road
        Assert.True(turned.Valid, turned.Reason);
        Assert.Equal(snapped.X, turned.X, 0.01f);
        Assert.Equal(snapped.Z, turned.Z, 0.01f);
        Assert.Equal(0f, turned.Angle, 0.001f);
        Assert.NotEqual(DepotConnection.Beside, turned.Connection);
        Assert.Equal(snapped.Z, sim.PlanDepot(500, 112, free: true, angle: 0f).Z + 2f, 0.01f); // a free one stands where the mouse is (z = 112), the snapped one at 114
    }
}
