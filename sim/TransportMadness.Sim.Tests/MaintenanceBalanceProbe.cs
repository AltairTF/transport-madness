using TransportMadness.Sim;
using Xunit.Abstractions;
using static TransportMadness.Sim.Tests.Worlds;

namespace TransportMadness.Sim.Tests;

/// <summary>
/// Exploratory runs that print the numbers behind the maintenance balance (what a line earns, how far a vehicle drives a day, what the default policy
/// costs, what breakdowns cost without it). They assert nothing and only run when MAINT_PROBE is set; DAYS and SEEDS choose the length and the maps.
/// </summary>
public class MaintenanceBalanceProbe
{
    readonly ITestOutputHelper _out;
    public MaintenanceBalanceProbe(ITestOutputHelper o) => _out = o;
    static bool On => Environment.GetEnvironmentVariable("MAINT_PROBE") != null;
    static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
    static float EnvF(string name, float fallback) => float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    static IEnumerable<bool> Kinds() => Environment.GetEnvironmentVariable("KINDS") switch { "bus" => new[] { true }, "truck" => new[] { false }, _ => new[] { true, false } };
    static IEnumerable<ulong> Seeds() => (Environment.GetEnvironmentVariable("SEEDS") ?? "1,3,4,6").Split(',').Select(ulong.Parse);

    static Tuning Override(Tuning t)
    {
        float mpp = EnvF("MPP", -1f);
        if (mpp > 0) t = t with { Maintenance = t.Maintenance with { MetresPerPoint = mpp } };
        float service = EnvF("SERVICE", -1f);
        if (service > 0) t = t with { Maintenance = t.Maintenance with { Service = t.Maintenance.Service with { PriceShare = service } } };
        return t;
    }

    static (double Income, double Upkeep, double Maintenance, double KmPerVehicleDay, int Breakdowns, long Fines, long Compensation) Measure(
        ulong seed, int days, bool policyOn, bool bus, IncidentFrequency frequency)
    {
        var tuning = Override(Tuning.Load());
        if (bus)
        {
            var sc = BusScenario(seed, 3, 4, tuning)!.Value;
            return Run(sc.Sim, sc.Line, days, policyOn, frequency);
        }
        var tr = TruckScenario(seed, 2, minDistance: 800f, tuning: tuning)!.Value;
        return Run(tr.Sim, tr.Line, days, policyOn, frequency);
    }

    static (double, double, double, double, int, long, long) Run(Simulation sim, int line, int days, bool policyOn, IncidentFrequency frequency)
    {
        sim.Execute(new SetFrequencyCommand(frequency));
        if (!policyOn) sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
        // Depots of the scenarios are Simple; a Workshop is what the default policy (a Service) needs.
        for (int d = 0; d < sim.Depots.Count; d++) sim.Execute(new UpgradeDepotCommand(false, d, DepotLevel.Workshop));
        double metres = 0;
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        long from = ticks / 3, income0 = 0, upkeep0 = 0, maintenance0 = 0;
        for (long t = 0; t < ticks; t++)
        {
            if (t == from)
            {
                income0 = sim.Lines[line].Income;
                upkeep0 = sim.Lines[line].Upkeep;
                maintenance0 = -sim.Company.Total(LedgerEntry.Maintenance);
                metres = 0;
            }
            sim.Step();
            for (int v = 0; v < sim.VehicleCount; v++)
                if (sim.IsCompanyVehicle(v)) metres += sim.GetVehicle(v).Speed * Simulation.StepSeconds;
        }
        double window = days - days / 3;
        var l = sim.Lines[line];
        int vehicles = Math.Max(1, sim.Garage.Count + Enumerable.Range(0, sim.VehicleCount).Count(sim.IsCompanyVehicle));
        int breakdowns = sim.Events.Count(e => e.Kind == SimEventKind.Breakdown) + sim.EventsDropped;
        double maintenance = -sim.Company.Total(LedgerEntry.Maintenance) - maintenance0;
        return ((l.Income - income0) / window, (l.Upkeep - upkeep0 - maintenance) / window, maintenance / window, metres / 1000.0 / window / vehicles, breakdowns,
            -sim.Company.Total(LedgerEntry.Fines), -sim.Company.Total(LedgerEntry.Compensation));
    }

    [Fact]
    public void WhatTheDefaultPolicyCosts()
    {
        if (!On) return;
        int days = Env("DAYS", 20);
        foreach (bool bus in Kinds())
            foreach (ulong seed in Seeds())
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var on = Measure(seed, days, true, bus, IncidentFrequency.Normal);
                _out.WriteLine($"{(bus ? "bus  " : "truck")} seed {seed}, {days} days, policy on: income/day {on.Item1:0}, upkeep/day {on.Item2:0}, maintenance/day {on.Item3:0} ({100 * on.Item3 / Math.Max(1, on.Item1):0.0} % of income), {on.Item4:0.0} km/vehicle/day, breakdowns {on.Item5}, fines {on.Item6}, compensation {on.Item7}   [{watch.Elapsed.TotalSeconds:0} s]");
            }
    }

    [Fact]
    public void WhatBreakdownsCostWithoutMaintenance()
    {
        if (!On) return;
        int days = Env("DAYS", 20);
        foreach (bool bus in Kinds())
            foreach (ulong seed in Seeds())
            {
                var off = Measure(seed, days, false, bus, IncidentFrequency.Normal);
                var on = Measure(seed, days, true, bus, IncidentFrequency.Normal);
                _out.WriteLine($"{(bus ? "bus  " : "truck")} seed {seed}, {days} days: income/day without maintenance {off.Item1:0} ({off.Item5} breakdowns, fines {off.Item6}) against with {on.Item1:0} (maintenance {on.Item3:0}/day, {on.Item5} breakdowns)");
            }
    }
}
