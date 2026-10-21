using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The war (C9): rationing of imports, the call-up, requisitions and raids.</summary>
public class WarTests
{
    const int Fuel = 11, Coal = 8;

    static Terrain DryLevelMap() => Terrain.Create(512, 8f, (_, _) => 5f);

    /// <summary>A short day (30 s) so that the phases of the war come quickly (the phase of the war follows the month, 3 days).</summary>
    static Tuning Quick(IncidentFrequency? unused = null)
    {
        var t = Tuning.Load();
        return t with { Economy = t.Economy with { DayLength = 30f }, Campaign = t.Campaign with { MaxExportSwing = 0f, DeficitPenalty = 0f } };
    }

    static Simulation World(Tuning tuning, IncidentFrequency frequency = IncidentFrequency.Normal)
    {
        var net = new RoadNetwork(DryLevelMap(), tuning);
        var sim = new Simulation(net, 4, frequency);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1000, 0, RoadType.TwoLane, 14f)));
        sim.AddTowns(new WorldPlan(0, Array.Empty<TownPlan>(), Array.Empty<IndustryPlan>(), null, new[] { new GatewayPlan(GatewayKind.Road, "Test Road Gate", 500, 30, 0) }));
        Assert.True(sim.Execute(new AddDepotCommand(net.FindNodeNear(0, 0, 1f), DepotLevel.Simple)) >= 0);
        Assert.True(sim.Execute(new BuildStationCommand(500, 0)) >= 0);
        return sim;
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static void RunToMonth(Simulation sim, long month) => RunDays(sim, month * 3 - sim.Day + 0.05);

    // ---- Rationing -------------------------------------------------------------------------------------------------

    [Fact]
    public void InRearmament_NothingIsRationed()
    {
        var sim = World(Quick());
        var fuel = sim.ImportOffers.Single(o => o.Cargo == "fuel");

        Assert.Equal(fuel.PricePerUnit, sim.ImportPrice(fuel));
        Assert.Equal(fuel.Units, sim.ImportUnits(fuel));
    }

    [Theory]
    [InlineData(25, 1.25f, 0.85f)]   // mobilisation
    [InlineData(50, 1.7f, 0.65f)]    // total war
    [InlineData(90, 2.0f, 0.5f)]     // the end
    public void InTheLaterPhases_FuelAndRubberAreDearerAndComeInSmallerShipments(int month, float price, float units)
    {
        var sim = World(Quick());
        RunToMonth(sim, month);
        var fuel = sim.ImportOffers.Single(o => o.Cargo == "fuel");
        var rubber = sim.ImportOffers.Single(o => o.Cargo == "rubber");
        var coal = sim.ImportOffers.Single(o => o.Cargo == "coal");

        Assert.Equal((int)Math.Round(fuel.PricePerUnit * price), sim.ImportPrice(fuel));
        Assert.Equal((int)Math.Round(fuel.Units * units), sim.ImportUnits(fuel));
        Assert.Equal((int)Math.Round(rubber.PricePerUnit * price), sim.ImportPrice(rubber));
        Assert.Equal(coal.PricePerUnit, sim.ImportPrice(coal)); // coal is not rationed
        Assert.Equal(coal.Units, sim.ImportUnits(coal));
    }

    [Fact]
    public void AnImportIsChargedAndDeliveredAtThePhasesPrice_EvenIfThePhaseChangesWhileItIsOnItsWay()
    {
        var tuning = Quick();
        var sim = World(tuning);
        RunToMonth(sim, 47);
        var fuel = sim.ImportOffers.Single(o => o.Cargo == "fuel");
        long money = sim.Company.Money;
        int units = sim.ImportUnits(fuel), price = sim.ImportPrice(fuel);
        Assert.Equal(1, sim.Execute(new RequestImportCommand(0, "fuel")));
        Assert.Equal(money - (long)units * price, sim.Company.Money);

        RunDays(sim, sim.GatewayTuning.ShipmentSeconds / sim.DayLengthSeconds + 1); // the shipment lands in total war, the request was made in mobilisation
        Assert.Equal("totalwar", sim.Phase.Id);

        Assert.Equal((double)units, sim.Gateways[0].Imported[Fuel]);
        Assert.True(units < fuel.Units);
    }

    [Fact]
    public void TheFrequencyOption_ScalesRationing_OffMeansNone_HighMeansTwiceTheChange()
    {
        double Price(IncidentFrequency f)
        {
            var sim = World(Quick(), f);
            RunToMonth(sim, 50);
            return sim.ImportPrice(sim.ImportOffers.Single(o => o.Cargo == "fuel")) / (double)sim.ImportOffers.Single(o => o.Cargo == "fuel").PricePerUnit;
        }

        Assert.Equal(1.0, Price(IncidentFrequency.Off), 3);
        Assert.Equal(1.35, Price(IncidentFrequency.Low), 3);
        Assert.Equal(1.7, Price(IncidentFrequency.Normal), 3);
        Assert.Equal(2.4, Price(IncidentFrequency.High), 3);
    }

    [Fact]
    public void WhenThePhaseChanges_TheNewsSaysRationingBegins()
    {
        var sim = World(Quick());

        RunToMonth(sim, 21);

        var news = Assert.Single(sim.Events, e => e.Kind == SimEventKind.War);
        Assert.Contains("Mobilisation", news.Text);
        Assert.Contains("rationing", news.Text);
    }

    [Fact]
    public void WithRationingOff_ThereIsNoNews()
    {
        var sim = World(Quick(), IncidentFrequency.Off);

        RunToMonth(sim, 50);

        Assert.DoesNotContain(sim.Events, e => e.Kind == SimEventKind.War);
    }

    [Fact]
    public void ABadRationingTuning_IsRefused()
    {
        var t = Tuning.Load();
        var bad = t.War with { Rationing = t.War.Rationing with { Cargo = new[] { "gold" } } };

        Assert.Throws<System.Text.Json.JsonException>(() => bad.Validate(t));
    }
}
