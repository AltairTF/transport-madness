using TransportMadness.Sim;
using Xunit.Abstractions;
using static TransportMadness.Sim.Tests.Worlds;

namespace TransportMadness.Sim.Tests;

/// <summary>Rail against road on the same pair of towns, for balancing; prints numbers and asserts nothing. Runs only when RAIL_PROBE is set.</summary>
public class RailBalanceProbe
{
    readonly ITestOutputHelper _out;
    public RailBalanceProbe(ITestOutputHelper o) => _out = o;
    static bool On => Environment.GetEnvironmentVariable("RAIL_PROBE") != null;
    static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    record Outcome(float Distance, double Income, double Upkeep, long Capital, int Capacity)
    {
        public double Net => Income - Upkeep;
        public double PaybackDays => Net > 0 ? Capital / Net : double.PositiveInfinity;
    }

    static long Spent(Simulation sim) => -(sim.Company.Total(LedgerEntry.Construction) + sim.Company.Total(LedgerEntry.Stations) + sim.Company.Total(LedgerEntry.Depots) + sim.Company.Total(LedgerEntry.Vehicles));

    static Outcome Measured(Simulation sim, int line, float dist, int capacity, int days)
    {
        var l = sim.Lines[line];
        long inc = 0, up = 0;
        for (int d = 0; d < days; d++)
        {
            if (d == 3) { inc = l.Income; up = l.Upkeep; }
            Run(sim, 1);
        }
        return new Outcome(dist, (l.Income - inc) / (double)(days - 3), (l.Upkeep - up) / (double)(days - 3), Spent(sim), capacity);
    }

    static Outcome? Rail(ulong seed, int a, int b, int wagons, int trains, int days)
    {
        var (sim, _) = World(seed, 4);
        sim.Execute(new TakeLoanCommand(1000000)); sim.Company.Money += 20_000_000;
        long start = sim.Company.Money;
        var ta = sim.Towns[a]; var tb = sim.Towns[b];
        float dx = tb.X - ta.X, dz = tb.Z - ta.Z, d = MathF.Sqrt(dx * dx + dz * dz); dx /= d; dz /= d;
        float nx = -dz, nz = dx;
        foreach (float side in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f })
        {
            float x0 = ta.X + dx * 100 + nx * side, z0 = ta.Z + dz * 100 + nz * side, x1 = tb.X - dx * 100 + nx * side, z1 = tb.Z - dz * 100 + nz * side;
            if (sim.Execute(new BuildTrackCommand(x0, z0, x1, z1)) == 0) continue;
            int sa = sim.Execute(new BuildRailStationCommand(x0, z0)), sb = sim.Execute(new BuildRailStationCommand(x1, z1));
            int depot = sim.Execute(new AddRailDepotCommand(x0, z0));
            int line = sim.Execute(new CreateLineCommand(new[] { sa, sb }));
            if (line < 0 || depot < 0) continue;
            int bought = 0;
            for (int k = 0; k < trains; k++)
                for (int tries = 0; tries < 60; tries++)
                {
                    if (sim.Execute(new BuyTrainCommand(depot, "diesel", "coach", wagons, line)) >= 0) { bought++; break; }
                    RailWorlds.Run(sim, 0.5);
                }
            if (bought == 0) continue;
            var o = Measured(sim, line, d, bought * wagons * 48, days);
            return o with { Capital = start - sim.Company.Money };
        }
        return null;
    }

    static Outcome? Bus(ulong seed, int a, int b, int buses, int days)
    {
        var (sim, _) = World(seed, 4);
        sim.Execute(new TakeLoanCommand(1000000)); sim.Company.Money += 20_000_000;
        long start = sim.Company.Money;
        var ta = sim.Towns[a]; var tb = sim.Towns[b];
        float d = MathF.Sqrt(MathF.Pow(ta.X - tb.X, 2) + MathF.Pow(ta.Z - tb.Z, 2));
        if (!Connect(sim, ta, tb)) return null;
        var sa = StationsNear(sim, ta.X, ta.Z, 1); var sb = StationsNear(sim, tb.X, tb.Z, 1);
        int depot = Depot(sim, ta);
        if (sa.Count == 0 || sb.Count == 0 || depot < 0) return null;
        int line = sim.Execute(new CreateLineCommand(new[] { sa[0], sb[0] }));
        int bought = 0;
        for (int k = 0; k < buses; k++) if (Buy(sim, depot, "bus", line) >= 0) bought++;
        if (bought == 0) return null;
        var o = Measured(sim, line, d, bought * 30, days);
        return o with { Capital = start - sim.Company.Money };
    }

    [Fact]
    public void PassengerRailAgainstBuses_ByDistance()
    {
        if (!On) return;
        int days = Env("DAYS", 12), wagons = Env("WAGONS", 4), trains = Env("TRAINS", 1);
        _out.WriteLine($"{trains} train(s) of {wagons} coaches against {trains * wagons * 48 / 30} buses; payback in days (capital / net a day)");
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var (probe, _) = World(seed, 4);
            for (int a = 0; a < probe.Towns.Count; a++)
                for (int b = a + 1; b < probe.Towns.Count; b++)
                {
                    float d = MathF.Sqrt(MathF.Pow(probe.Towns[a].X - probe.Towns[b].X, 2) + MathF.Pow(probe.Towns[a].Z - probe.Towns[b].Z, 2));
                    var r = Rail(seed, a, b, wagons, trains, days);
                    var u = Bus(seed, a, b, Math.Max(1, trains * wagons * 48 / 30), days);
                    string Fmt(Outcome? o) => o == null ? "no layout" : $"cap {o.Capital / 1000}k income {o.Income:0} upkeep {o.Upkeep:0} net {o.Net:0} payback {o.PaybackDays:0}d";
                    _out.WriteLine($"seed {seed} {a}-{b} {d:0} m | rail {Fmt(r)} | bus {Fmt(u)}");
                }
        }
    }

    static Outcome? FreightRail(ulong seed, int pairIndex, int wagons, int days, out float dist)
    {
        dist = 0;
        var (sim, _) = World(seed, 3, null, IndustryAmount.Normal);
        sim.Execute(new TakeLoanCommand(1000000)); sim.Company.Money += 20_000_000;
        long start = sim.Company.Money;
        var pairs = Pairs(sim);
        if (pairIndex >= pairs.Count) return null;
        var (x, y) = pairs[pairIndex];
        float d = Gap(x, y), dx = (y.X - x.X) / d, dz = (y.Z - x.Z) / d;
        dist = d;
        foreach (float side in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f })
        {
            float nx = -dz * side, nz = dx * side;
            float x0 = x.X + dx * 40 + nx, z0 = x.Z + dz * 40 + nz, x1 = y.X - dx * 40 + nx, z1 = y.Z - dz * 40 + nz;
            if (sim.Execute(new BuildTrackCommand(x0, z0, x1, z1)) == 0) continue;
            int sa = sim.Execute(new BuildRailStationCommand(x0, z0)), sb = sim.Execute(new BuildRailStationCommand(x1, z1));
            int depot = sim.Execute(new AddRailDepotCommand(x0, z0));
            if (sa < 0 || sb < 0 || depot < 0) continue;
            int line = sim.Execute(new CreateLineCommand(new[] { sa, sb }));
            if (sim.Execute(new BuyTrainCommand(depot, "diesel", "freight", wagons, line)) < 0) continue;
            return Measured(sim, line, d, wagons * 40, days) with { Capital = start - sim.Company.Money };
        }
        return null;
    }

    static Outcome? Trucks(ulong seed, int pairIndex, int trucks, int days)
    {
        var (sim, _) = World(seed, 3, null, IndustryAmount.Normal);
        sim.Execute(new TakeLoanCommand(1000000)); sim.Company.Money += 20_000_000;
        long start = sim.Company.Money;
        var pairs = Pairs(sim);
        if (pairIndex >= pairs.Count) return null;
        var (pa, pb) = pairs[pairIndex];
        float dist = Gap(pa, pb);
        if (sim.Execute(new BuildRoadCommand(pa.X, pa.Z, pb.X, pb.Z, RoadType.TwoLane, 14f)) <= 0) return null;
        var net = sim.Network;
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(pa.X, pa.Z, 5f, withRoadsOnly: true), DepotLevel.Simple));
        float dx = (pb.X - pa.X) / dist, dz = (pb.Z - pa.Z) / dist;
        int sa = sim.Execute(new BuildStationCommand(pa.X + dx * 60f, pa.Z + dz * 60f));
        int sb = sim.Execute(new BuildStationCommand(pb.X - dx * 60f, pb.Z - dz * 60f));
        if (depot < 0 || sa < 0 || sb < 0) return null;
        int line = sim.Execute(new CreateLineCommand(new[] { sa, sb }));
        int bought = 0;
        for (int k = 0; k < trucks; k++) if (Buy(sim, depot, "truck", line) >= 0) bought++;
        if (bought == 0) return null;
        return Measured(sim, line, dist, bought * 20, days) with { Capital = start - sim.Company.Money };
    }

    static float Gap(Industry a, Industry b) => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Z - b.Z, 2));

    /// <summary>Every primary industry with every processor of its chain within 2.5 km, nearest first.</summary>
    static List<(Industry, Industry)> Pairs(Simulation sim) =>
        sim.Industries.Where(i => i.Type == 0).SelectMany(x => sim.Industries.Where(i => i.Type == 3).Select(y => (x, y))).Where(t => Gap(t.x, t.y) < 2500f).OrderBy(t => Gap(t.x, t.y)).ToList();

    [Fact]
    public void FreightRailAgainstTrucks_ByDistance()
    {
        if (!On) return;
        int days = Env("DAYS", 12), wagons = Env("WAGONS", 2);
        _out.WriteLine($"{wagons} freight wagons against {wagons * 2} trucks, forest to sawmill");
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4, 5, 6, 7, 8 })
            for (int p = 0; p < 3; p++)
            {
                var r = FreightRail(seed, p, wagons, days, out float d);
                var u = Trucks(seed, p, wagons * 2, days);
                if (d == 0) continue;
                string Fmt(Outcome? o) => o == null ? "no layout" : $"cap {o.Capital / 1000}k income {o.Income:0} upkeep {o.Upkeep:0} net {o.Net:0} payback {o.PaybackDays:0}d";
                _out.WriteLine($"seed {seed} pair {p} {d:0} m | rail {Fmt(r)} | trucks {Fmt(u)}");
            }
    }
}
