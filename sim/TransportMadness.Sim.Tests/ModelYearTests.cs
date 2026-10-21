using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>The fleet by year (C8): a model can be bought from its year on, and an old model costs more to repair.</summary>
public class ModelYearTests
{
    /// <summary>A short day so that years go by quickly (a year is 36 days).</summary>
    static Tuning Quick(int startYear = 1938)
    {
        var t = Tuning.Load();
        return t with { Economy = t.Economy with { DayLength = 20f, StartYear = startYear } };
    }

    /// <summary>A road with a depot, two stations and a line, and a rail line with a depot, for buying.</summary>
    static (Simulation Sim, int Depot, int Line, int RailDepot, int RailLine) World(Tuning tuning)
    {
        var sim = new Simulation(new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), tuning), 5, IncidentFrequency.Off);
        Assert.Equal(1, sim.Execute(new BuildRoadCommand(0, 0, 1200, 0, RoadType.TwoLane, 14f)));
        int depot = sim.Execute(new AddDepotCommand(sim.Network.FindNodeNear(0, 0, 1f), DepotLevel.Simple));
        int a = sim.Execute(new BuildStationCommand(150, 0)), b = sim.Execute(new BuildStationCommand(1050, 0));
        int line = sim.Execute(new CreateLineCommand(new[] { a, b }));
        Assert.True(sim.Execute(new BuildTrackCommand(0, 300, 300, 300)) > 0);
        Assert.True(sim.Execute(new BuildTrackCommand(300, 300, 1500, 300)) > 0);
        int ra = sim.Execute(new BuildRailStationCommand(400, 300)), rb = sim.Execute(new BuildRailStationCommand(1400, 300));
        int rdepot = sim.Execute(new AddRailDepotCommand(300, 300));
        int rline = sim.Execute(new CreateLineCommand(new[] { ra, rb }));
        Assert.True(depot >= 0 && line >= 0 && rdepot >= 0 && rline >= 0);
        return (sim, depot, line, rdepot, rline);
    }

    static void RunDays(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    static int YearsToDays(double years) => (int)Math.Round(years * 36);

    // ---- The roster ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRosterIsData_AModelHasItsYear_AndTheLaterOnesAreBiggerOrBetter()
    {
        var t = Tuning.Load();
        var byId = t.Vehicles.ToDictionary(v => v.Id);

        Assert.Equal(1938, byId["bus"].FromYear);
        Assert.Equal(1941, byId["bigbus"].FromYear);
        Assert.True(byId["bigbus"].Capacity > byId["bus"].Capacity);
        Assert.Equal(1940, byId["heavytruck"].FromYear);
        Assert.True(byId["heavytruck"].Capacity > byId["truck"].Capacity);
        Assert.All(t.Vehicles, v => Assert.InRange(v.FromYear, 1938, 1945));
        var steam = t.Rail.Locomotives.Single(l => l.Id == "steam");
        var diesel = t.Rail.Locomotives.Single(l => l.Id == "diesel");
        Assert.Equal(1938, steam.FromYear);
        Assert.Equal(1942, diesel.FromYear);
        Assert.True(steam.Price < diesel.Price, "steam is cheaper");
        Assert.True(steam.MaxSpeed < diesel.MaxSpeed, "and slower");
        Assert.True(steam.UpkeepPerDay > diesel.UpkeepPerDay, "and dearer to keep");
    }

    // ---- Buying ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ABusModelIsRefusedBeforeItsYear_AndBoughtAfterIt()
    {
        var (sim, depot, line, _, _) = World(Quick());
        Assert.False(sim.IsAvailable(sim.VehicleModels.Single(m => m.Id == "bigbus")));

        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(depot, "bigbus", line)));
        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(BuyVehicleCommand.NearestDepot, "bigbus", line)));
        Assert.Equal(-1, sim.Execute(new BuyParkedVehicleCommand(depot, "bigbus")));
        Assert.True(sim.Execute(new BuyVehicleCommand(depot, "bus", line)) >= 0);

        RunDays(sim, YearsToDays(3) - 1); // the last day of 1940
        Assert.Equal(1940, sim.Today.Year);
        Assert.Equal(-1, sim.Execute(new BuyVehicleCommand(depot, "bigbus", line)));
        Assert.True(sim.Execute(new BuyVehicleCommand(depot, "heavytruck", line)) >= 0, "the heavy truck is from 1940");
        RunDays(sim, 1);
        Assert.Equal(1941, sim.Today.Year);

        Assert.True(sim.Execute(new BuyVehicleCommand(depot, "bigbus", line)) >= 0);
        Assert.True(sim.Execute(new BuyParkedVehicleCommand(depot, "bigbus")) >= 0);
    }

    [Fact]
    public void ALocomotiveIsRefusedBeforeItsYear_ForALineAndForTheGarage_AndNewsNamesWhatIsNew()
    {
        var (sim, _, _, rdepot, rline) = World(Quick());

        Assert.Equal(-1, sim.Execute(new BuyTrainCommand(rdepot, "diesel", "coach", 3, rline)));
        Assert.Equal(-1, sim.Execute(new BuyTrainCommand(BuyTrainCommand.NearestDepot, "diesel", "coach", 3, rline)));
        Assert.Equal(-1, sim.Execute(new BuyParkedTrainCommand(rdepot, "diesel", "coach", 3)));
        Assert.True(sim.Execute(new BuyTrainCommand(rdepot, "steam", "coach", 3, rline)) >= 0);
        Assert.Equal(new[] { "steam" }, sim.AvailableLocomotives.Select(l => l.Id));

        RunDays(sim, YearsToDays(4));

        Assert.Equal(1942, sim.Today.Year);
        Assert.True(sim.Execute(new BuyTrainCommand(rdepot, "diesel", "coach", 3, rline)) >= 0);
        Assert.Equal(new[] { "diesel", "steam" }, sim.AvailableLocomotives.Select(l => l.Id)); // the order of the tuning
        Assert.Equal(new[] { "Diesel locomotive" }, sim.ModelsOfYear(1942));
        Assert.Equal(new[] { "Large bus" }, sim.ModelsOfYear(1941));
    }

    [Fact]
    public void TheAvailableModels_GrowWithTheYears()
    {
        var sim = World(Quick()).Sim;
        var ids = () => sim.AvailableModels.Select(m => m.Id).ToArray();

        Assert.Equal(new[] { "bus", "truck" }, ids());
        RunDays(sim, YearsToDays(2));
        Assert.Equal(new[] { "bus", "truck", "heavytruck" }, ids());
        RunDays(sim, YearsToDays(1));
        Assert.Equal(new[] { "bus", "truck", "heavytruck", "bigbus" }, ids());
    }

    // ---- Old models ------------------------------------------------------------------------------------------------

    [Fact]
    public void ARepair_CostsMore_TheOlderTheModel_UpToALimit()
    {
        var t = Tuning.Load().Maintenance.OldVehicle;

        Assert.Equal(1f, t.Factor(1938, 1938));
        Assert.Equal(1f, t.Factor(1938, 1941)); // three years: not yet old
        Assert.Equal(1f + t.ExtraPerYear, t.Factor(1938, 1942), 0.001f);
        Assert.Equal(1f + 2 * t.ExtraPerYear, t.Factor(1938, 1943), 0.001f);
        Assert.Equal(1f + t.MaxExtra, t.Factor(1938, 1960), 0.001f);
        Assert.True(t.Factor(1941, 1945) < t.Factor(1938, 1945), "a newer model of the same year is cheaper to keep");
    }

    [Fact]
    public void TheRepairPriceOfAnOldVehicle_UsesTheFactorOfItsModelAndTheYearNow()
    {
        var (sim, _, _, _, _) = World(Quick(startYear: 1943));
        long price = 24000;

        long plain = sim.RepairPrice(RepairKind.Service, price);
        long old = sim.RepairPrice(RepairKind.Service, price, 1938);
        long newer = sim.RepairPrice(RepairKind.Service, price, 1941);

        Assert.Equal(sim.MaintenanceRules.Of(RepairKind.Service).PriceShare * price, plain, 1.0);
        Assert.Equal(plain * 1.24, old, 1.0);
        Assert.Equal(plain * 1.0, newer, 1.0); // 1941 to 1943 is two years: not yet old
        Assert.Equal(1.24f, sim.RepairFactorOf(sim.VehicleModels.Single(m => m.Id == "bus")), 0.001f);
    }

    [Fact]
    public void ARepairInTheDepot_IsPaidAtTheModelsFactor()
    {
        var (sim, depot, line, _, _) = World(Quick(startYear: 1945));
        sim.Execute(new SetFrequencyCommand(IncidentFrequency.Off));
        int parked = sim.Execute(new BuyParkedVehicleCommand(depot, "bus"));
        sim.Garage[parked].Condition = 30f;
        long before = sim.Company.Total(LedgerEntry.Maintenance);

        Assert.Null(sim.WhyNotRepair(RepairSubject.Parked, parked, RepairKind.Patch));
        sim.Execute(new RequestRepairCommand(RepairSubject.Parked, parked, RepairKind.Patch));
        RunDays(sim, 1);

        long expected = sim.RepairPrice(RepairKind.Patch, 24000, 1938);
        Assert.Equal(-expected, sim.Company.Total(LedgerEntry.Maintenance) - before);
        Assert.True(expected > sim.RepairPrice(RepairKind.Patch, 24000));
    }
}
