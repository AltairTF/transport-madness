using System.Text.Json;
using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

public class TuningTests
{
    [Fact]
    public void Load_ReadsTheShippedFile()
    {
        var tuning = Tuning.Load();

        Assert.Equal(1.5f, tuning.Driving.MaxAccel);
        Assert.Equal(2, tuning.Crews.PerDepot.For(DepotLevel.Complex));
        Assert.Equal(1, tuning.Crews.PerDepot.For(DepotLevel.Simple));
        Assert.Equal(19f, tuning.Roads.Of(RoadType.Avenue2x2).SpeedLimit);
        Assert.Equal(14f, tuning.Roads.Of(RoadType.TwoLane).SpeedLimit);
    }

    [Fact]
    public void ShippedFile_HasTheEconomyAndTheVehicleRoster()
    {
        var t = Tuning.Load();

        Assert.Equal(600f, t.Economy.DayLength);
        Assert.Equal(30, t.Economy.DaysPerMonth);
        Assert.Equal(12, t.Economy.MonthsPerYear);
        Assert.Equal(1000000, t.Economy.StartingMoney);
        Assert.Equal(new[] { "bus", "truck" }, t.Vehicles.Select(v => v.Id));
        Assert.Equal(new[] { "passengers", "goods", "wood", "planks", "stone", "materials", "grain", "food" }, t.Cargo.Select(c => c.Id));
        Assert.All(t.Vehicles, v => Assert.True(v.Cargo == "freight" || t.Cargo.Any(c => c.Id == v.Cargo)));
        Assert.True(t.Economy.DepotPrice.For(DepotLevel.Complex) > t.Economy.DepotPrice.For(DepotLevel.Simple));
    }

    [Fact]
    public void AVehicleThatCarriesUnknownCargo_IsRejectedWhenTheSimulationStarts()
    {
        var shipped = Tuning.Load();
        var broken = shipped with { Vehicles = new[] { shipped.Vehicles[0] with { Cargo = "gold" } } };

        Assert.Throws<ArgumentException>(() => new Simulation(new RoadNetwork(Terrain.Flat(), broken), 1));
    }

    [Fact]
    public void FromJson_RejectsAMissingKey()
    {
        var text = ShippedText().Replace("\"maxAccel\"", "\"maxAccelX\"");

        Assert.Throws<JsonException>(() => Tuning.FromJson(text));
    }

    [Fact]
    public void FromJson_RejectsBuildingTypesOutOfOrder()
    {
        var text = ShippedText().Replace("\"id\": \"home\"", "\"id\": \"shed\"");

        Assert.Throws<JsonException>(() => Tuning.FromJson(text));
    }

    [Fact]
    public void ShippedFile_HasThreeSizesOfEveryBuilding_AndRingsThatMatch()
    {
        var t = Tuning.Load().Towns;

        Assert.All(t.BuildingTypes, type => Assert.Equal(TownsTuning.Tiers, type.Tiers.Count));
        Assert.All(t.Rings, ring => Assert.Equal(Enum.GetValues<BuildingType>().Length, ring.Mix.Count));
        Assert.True(t.Names.Count >= 10);
        Assert.True(t.Generation.LotWidth * 2f < t.Generation.BlockLength); // the two lots on a side fit, with the corners left free
    }

    [Fact]
    public void ShippedFile_HasTheFourChains()
    {
        var t = Tuning.Load().Industries;

        Assert.Equal(new[] { "forest", "quarry", "farm", "sawmill", "cementworks", "foodplant", "factory" }, t.Types.Select(x => x.Id));
        Assert.Equal(new[] { "wood", "stone", "grain", "planks", "materials", "food", "goods" }, t.Types.Select(x => x.Output));
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1, 1 }, t.Types.Select(x => x.Inputs.Count));
        Assert.All(new[] { t.Counts.Few, t.Counts.Normal, t.Counts.Many }, counts => Assert.Equal(t.Types.Count, counts.Count));
    }

    [Fact]
    public void ABuildingNeed_ForAnUnknownCargo_IsRejected()
    {
        var text = ShippedText().Replace("\"cargo\": \"food\", \"amount\": 20", "\"cargo\": \"gravy\", \"amount\": 20");

        Assert.Throws<ArgumentException>(() => new Simulation(new RoadNetwork(Terrain.Flat(), Tuning.FromJson(text)), 1));
    }

    [Fact]
    public void FromJson_RejectsAnUnknownKey()
    {
        var text = ShippedText().Replace("\"driving\": {", "\"driving\": { \"mystery\": 1,");

        Assert.Throws<JsonException>(() => Tuning.FromJson(text));
    }

    [Fact]
    public void ShippedFile_GivesTheNumbersTheSimulationWasTunedWith()
    {
        // The traffic model is sensitive (see project-status.md): a number changed in the file should be a deliberate act.
        var t = Tuning.Load();

        Assert.Equal((1.5f, 2f, 2f, 1.5f), (t.Driving.MaxAccel, t.Driving.ComfortDecel, t.Driving.MinGap, t.Driving.TimeHeadway));
        Assert.Equal((10, 0.8f), (t.Manoeuvres.DecisionInterval, t.Manoeuvres.LateralRate));
        Assert.Equal((60, 100), (t.LaneChange.Cooldown, t.Overtake.Cooldown));
    }

    [Fact]
    public void PricesComeFromTheTuningOfTheNetwork()
    {
        var shipped = Tuning.Load();
        var doubled = shipped with { Roads = shipped.Roads with { TwoLane = shipped.Roads.TwoLane with { CostPerMetre = shipped.Roads.TwoLane.CostPerMetre * 2f } } };

        var cheap = new RoadNetwork(Terrain.Flat(), shipped).EstimateCost(0, 0, 100, 0, RoadType.TwoLane)!.Value;
        var dear = new RoadNetwork(Terrain.Flat(), doubled).EstimateCost(0, 0, 100, 0, RoadType.TwoLane)!.Value;

        Assert.Equal(100 * 40f, cheap.Money);
        Assert.Equal(2f * cheap.Money, dear.Money);
    }

    [Fact]
    public void DepotCrewsFollowTheTuning()
    {
        var shipped = Tuning.Load();
        var tuning = shipped with { Crews = shipped.Crews with { PerDepot = new CrewsPerDepot { Simple = 3, Workshop = 1, Complex = 1 } } };
        var net = new RoadNetwork(Terrain.Flat(), tuning);
        net.AddNode(0, 0);
        var sim = new Simulation(net, 1);

        sim.AddDepot(0, DepotLevel.Simple);

        Assert.Equal(3, sim.Depots[0].Crews);
    }

    [Fact]
    public void DrivingTuning_ChangesHowTrafficBehaves()
    {
        static ulong Hash(Tuning tuning)
        {
            var net = new RoadNetwork(Terrain.Flat(), tuning);
            int a = net.AddNode(0, 0), b = net.AddNode(500, 0);
            net.AddRoad(a, b, RoadType.TwoLane, 14f);
            var sim = new Simulation(net, 3);
            sim.SeedTraffic(10);
            for (int i = 0; i < 400; i++) sim.Step();
            return sim.StateHash();
        }
        var shipped = Tuning.Load();

        Assert.NotEqual(Hash(shipped), Hash(shipped with { Driving = shipped.Driving with { MaxAccel = 3f } }));
    }

    static string ShippedText()
    {
        using var stream = typeof(Tuning).Assembly.GetManifestResourceStream("tuning.json")!;
        return new StreamReader(stream).ReadToEnd();
    }
}
