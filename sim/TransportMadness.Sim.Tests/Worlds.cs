using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>Generated towns on level dry ground with roads, stations, depots and lines put in by commands, for the balance probe and the balance tests.</summary>
internal static class Worlds
{
    internal static void Run(Simulation sim, double days)
    {
        long ticks = (long)Math.Round(days * sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < ticks; t++) sim.Step();
    }

    /// <summary>The 600 seconds that were a day before the campaign's 1200 s day: the balance tests measure in these periods, so their windows and thresholds keep their meaning.</summary>
    internal const double Period = 600.0;

    /// <summary>The tuning with every model of the roster on offer from the start (year 1938), so that the tests of rail and of the fleet that are not about model years can buy any, and old models cost nothing extra to repair.</summary>
    internal static Tuning Late(Tuning? tuning = null)
    {
        tuning ??= Tuning.Load();
        return tuning with
        {
            Vehicles = tuning.Vehicles.Select(v => v with { FromYear = 1938 }).ToList(),
            Rail = tuning.Rail with { Locomotives = tuning.Rail.Locomotives.Select(l => l with { FromYear = 1938 }).ToList() },
        };
    }

    internal static void RunPeriod(Simulation sim) => Run(sim, Period / sim.DayLengthSeconds);

    internal static (Simulation Sim, WorldPlan Plan) World(ulong seed, int towns, Tuning? tuning = null, IndustryAmount? industries = null)
    {
        var net = new RoadNetwork(Terrain.Create(512, 8f, (_, _) => 5f), Late(tuning));
        var plan = TownGenerator.Generate(net, seed, towns);
        if (industries != null) plan = IndustryGenerator.Generate(net, seed, plan, industries.Value);
        var sim = new Simulation(net, seed);
        sim.AddTowns(plan);
        return (sim, plan);
    }

    /// <summary>Up to <paramref name="count"/> stations at road junctions near a point, at least 110 m apart, nearest first.</summary>
    internal static List<int> StationsNear(Simulation sim, float x, float z, int count, float maxRadius = 300f)
    {
        var net = sim.Network;
        var nodes = new List<(float D, int Node)>();
        for (int n = 0; n < net.NodeCount; n++)
        {
            if (net.RoadsAt(n).Count < 3) continue;
            var (nx, nz) = net.GetNode(n);
            float d = MathF.Sqrt((nx - x) * (nx - x) + (nz - z) * (nz - z));
            if (d <= maxRadius) nodes.Add((d, n));
        }
        nodes.Sort((p, q) => p.D.CompareTo(q.D));
        var chosen = new List<int>();
        var at = new List<(float X, float Z)>();
        foreach (var (_, n) in nodes)
        {
            var (nx, nz) = net.GetNode(n);
            if (at.Any(p => MathF.Sqrt((p.X - nx) * (p.X - nx) + (p.Z - nz) * (p.Z - nz)) < 110f)) continue;
            int id = sim.Execute(new BuildStationCommand(nx, nz));
            if (id < 0) continue;
            chosen.Add(id);
            at.Add((nx, nz));
            if (chosen.Count == count) break;
        }
        return chosen;
    }

    /// <summary>Road nodes within <paramref name="reach"/> of a point, nearest first (the nearest ones can sit where a road cannot cross the streets, so a few are tried).</summary>
    static List<int> NodesNear(RoadNetwork net, float x, float z, float reach, int count)
    {
        var found = new List<(float D, int Node)>();
        for (int n = 0; n < net.NodeCount; n++)
        {
            if (net.RoadsAt(n).Count == 0) continue;
            var (nx, nz) = net.GetNode(n);
            float d = MathF.Sqrt((nx - x) * (nx - x) + (nz - z) * (nz - z));
            if (d <= reach) found.Add((d, n));
        }
        found.Sort((p, q) => p.D.CompareTo(q.D));
        return found.Take(count).Select(f => f.Node).ToList();
    }

    /// <summary>
    /// Two ends for a straight track between two towns, as close to their middles as the crossing rules allow: the streets of an organic town
    /// are many and short, so most lines through its core meet a junction too closely, and a few tries from the middle outward find one that does not.
    /// </summary>
    internal static ((float X, float Z) A, (float X, float Z) B)? TrackBetween(Simulation sim, Town ta, Town tb, int minBuildings = 0, float maxReach = 160f)
    {
        var ends = new List<(float R, float X, float Z, bool A)>();
        foreach (var (t, isA) in new[] { (ta, true), (tb, false) })
            foreach (float r in new[] { 0f, 40f, 80f, 120f, 160f })
            {
                if (r > maxReach) continue;
                for (int k = 0; k < (r == 0f ? 1 : 12); k++)
                {
                    float a = k * MathF.Tau / 12f;
                    ends.Add((r, t.X + MathF.Cos(a) * r, t.Z + MathF.Sin(a) * r, isA));
                }
            }
        var pairs = from a in ends.Where(e => e.A) from b in ends.Where(e => !e.A) orderby a.R + b.R select (a, b);
        // Of the first few that the rules allow, the one whose ends have the most buildings around them (a station there is worth having).
        int Around(float x, float z) => sim.Buildings.Count(b => (b.X - x) * (b.X - x) + (b.Z - z) * (b.Z - z) < 150f * 150f);
        ((float X, float Z) A, (float X, float Z) B)? best = null;
        int bestCover = -1, found = 0;
        foreach (var (a, b) in pairs)
        {
            if (sim.QuoteTrack(a.X, a.Z, b.X, b.Z) is not { Affordable: true }) continue;
            int cover = Math.Min(Around(a.X, a.Z), Around(b.X, b.Z));
            if (cover > bestCover) { bestCover = cover; best = ((a.X, a.Z), (b.X, b.Z)); }
            if (++found == 12) break;
        }
        return bestCover >= minBuildings ? best : null;
    }

    /// <summary>The nodes joined to a node by roads.</summary>
    static HashSet<int> Component(RoadNetwork net, int start)
    {
        var seen = new HashSet<int>();
        if (start < 0) return seen;
        var queue = new Queue<int>();
        queue.Enqueue(start);
        seen.Add(start);
        while (queue.Count > 0)
        {
            int n = queue.Dequeue();
            foreach (var road in net.RoadsAt(n))
            {
                var r = net.GetRoad(road);
                int o = r.From == n ? r.To : r.From;
                if (seen.Add(o)) queue.Enqueue(o);
            }
        }
        return seen;
    }

    internal static bool Connect(Simulation sim, Town a, Town b)
    {
        var net = sim.Network;
        float dx = b.X - a.X, dz = b.Z - a.Z, len = MathF.Sqrt(dx * dx + dz * dz); dx /= len; dz /= len;
        // From the edge of each town (its radius, but never past 45 % of the way between them): the streets of a town reach far.
        float ra = MathF.Min(a.Radius, len * 0.45f), rb = MathF.Min(b.Radius, len * 0.45f);
        // Only nodes that belong to the town's own streets (a third town may lie between them, with streets of its own).
        var own = Component(net, net.FindNodeNear(a.X, a.Z, 300f, withRoadsOnly: true));
        var other = Component(net, net.FindNodeNear(b.X, b.Z, 300f, withRoadsOnly: true));
        var fromA = NodesNear(net, a.X + dx * ra, a.Z + dz * ra, 300f, 400).Where(own.Contains).Take(10).ToList();
        var fromB = NodesNear(net, b.X - dx * rb, b.Z - dz * rb, 300f, 400).Where(other.Contains).Take(10).ToList();
        foreach (int na in fromA)
            foreach (int nb in fromB)
            {
                if (na == nb) continue;
                var (ax, az) = net.GetNode(na); var (bx, bz) = net.GetNode(nb);
                if (sim.Execute(new BuildRoadCommand(ax, az, bx, bz, RoadType.TwoLane, 14f)) > 0) return true;
            }
        return false;
    }

    internal static int Depot(Simulation sim, Town t)
    {
        var net = sim.Network;
        int n = net.FindNodeNear(t.X, t.Z, 300f, withRoadsOnly: true);
        var (x, z) = net.GetNode(n);
        return sim.Execute(new AddDepotCommand(n, DepotLevel.Simple));
    }

    internal static int Buy(Simulation sim, int depot, string model, int line)
    {
        for (int tries = 0; tries < 40; tries++)
        {
            int v = sim.Execute(new BuyVehicleCommand(depot, model, line));
            if (v >= 0) return v;
            Run(sim, 0.005);
        }
        return -1;
    }

    internal static (int A, int B) ClosestPair(Simulation sim)
    {
        int bi = 0, bj = 1; float best = float.MaxValue;
        for (int i = 0; i < sim.Towns.Count; i++)
            for (int j = i + 1; j < sim.Towns.Count; j++)
            {
                float d = MathF.Sqrt(MathF.Pow(sim.Towns[i].X - sim.Towns[j].X, 2) + MathF.Pow(sim.Towns[i].Z - sim.Towns[j].Z, 2));
                if (d < best) { best = d; bi = i; bj = j; }
            }
        return (bi, bj);
    }

    /// <summary>The two closest of four towns joined by a road, with <paramref name="stops"/> stations in each, one line through them all and buses bought for it.</summary>
    internal static (Simulation Sim, int Line, Town A, Town B, int Bought, float Dist)? BusScenario(ulong seed, int stops, int buses, Tuning? tuning = null)
    {
        var (sim, _) = World(seed, 4, tuning);
        sim.Execute(new TakeLoanCommand(1000000));
        var (i, j) = ClosestPair(sim);
        var ta = sim.Towns[i]; var tb = sim.Towns[j];
        float dist = MathF.Sqrt(MathF.Pow(ta.X - tb.X, 2) + MathF.Pow(ta.Z - tb.Z, 2));
        if (!Connect(sim, ta, tb)) return null;
        var sa = StationsNear(sim, ta.X, ta.Z, stops);
        var sb = StationsNear(sim, tb.X, tb.Z, stops);
        int depot = Depot(sim, ta);
        var order = sa.Concat(Enumerable.Reverse(sb)).ToArray();
        if (order.Length < 2 || depot < 0) return null;
        int line = sim.Execute(new CreateLineCommand(order));
        int bought = 0;
        for (int k = 0; k < buses; k++) if (Buy(sim, depot, "bus", line) >= 0) bought++;
        return (sim, line, ta, tb, bought, dist);
    }

    /// <summary>A forest (or farm) and the nearest processor joined by a road, a station beside each, a depot at the first and one line with trucks.</summary>
    internal static (Simulation Sim, int Line, int Bought, float Dist)? TruckScenario(ulong seed, int trucks, int primaryType = 0, int processorType = 3, Tuning? tuning = null, float minDistance = 0f)
    {
        var (sim, _) = World(seed, 3, tuning, IndustryAmount.Normal);
        sim.Execute(new TakeLoanCommand(1000000));
        var primaries = sim.Industries.Where(i => i.Type == primaryType).ToList();
        var processors = sim.Industries.Where(i => i.Type == processorType).ToList();
        if (primaries.Count == 0 || processors.Count == 0) return null;
        static float Gap(Industry a, Industry b) => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Z - b.Z, 2));
        var pairs = primaries.SelectMany(x => processors.Select(y => (x, y))).Where(t => Gap(t.x, t.y) >= minDistance).OrderBy(t => Gap(t.x, t.y)).ToList();
        if (pairs.Count == 0) return null;
        var (pa, pb) = pairs[0];
        float dist = Gap(pa, pb);
        // The road joins the industries' access points (a road ending near one is drawn to it); the stations stand 50 m in from them (a road is not split nearer its end than 30 m), about 80 m from the middles.
        if (sim.Execute(new BuildRoadCommand(pa.AccessX, pa.AccessZ, pb.AccessX, pb.AccessZ, RoadType.TwoLane, 14f)) <= 0) return null;
        var net = sim.Network;
        int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(pa.AccessX, pa.AccessZ, 5f, withRoadsOnly: true), DepotLevel.Simple));
        float length = MathF.Sqrt(MathF.Pow(pb.AccessX - pa.AccessX, 2) + MathF.Pow(pb.AccessZ - pa.AccessZ, 2));
        float dx = (pb.AccessX - pa.AccessX) / length, dz = (pb.AccessZ - pa.AccessZ) / length;
        int sa = sim.Execute(new BuildStationCommand(pa.AccessX + dx * 50f, pa.AccessZ + dz * 50f));
        int sb = sim.Execute(new BuildStationCommand(pb.AccessX - dx * 50f, pb.AccessZ - dz * 50f));
        if (depot < 0 || sa < 0 || sb < 0) return null;
        int line = sim.Execute(new CreateLineCommand(new[] { sa, sb }));
        int bought = 0;
        for (int k = 0; k < trucks; k++) if (Buy(sim, depot, "truck", line) >= 0) bought++;
        return (sim, line, bought, dist);
    }
}
