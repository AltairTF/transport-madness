using TransportMadness.Sim;
using Xunit.Abstractions;
using static TransportMadness.Sim.Tests.Worlds;

namespace TransportMadness.Sim.Tests;

/// <summary>Exploratory runs that print numbers for balancing; they assert nothing and only run when BALANCE_PROBE is set.</summary>
public class BalanceProbe
{
    readonly ITestOutputHelper _out;
    public BalanceProbe(ITestOutputHelper o) => _out = o;
    static bool On => Environment.GetEnvironmentVariable("BALANCE_PROBE") != null;
    static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
    static float EnvF(string name, float fallback) => float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Overrides from the environment: PAYPAX (passenger pay per unit-km), BUSUPKEEP, TRUCKUPKEEP, WAITSEC, PEOPLEPERCAR, CARSHARE.</summary>
    static Tuning Override(Tuning t)
    {
        float pay = EnvF("PAYPAX", -1f), bu = EnvF("BUSUPKEEP", -1f), tu = EnvF("TRUCKUPKEEP", -1f);
        if (pay > 0) t = t with { Cargo = t.Cargo.Select(c => c.Id == "passengers" ? c with { PayPerUnitKm = pay } : c).ToList() };
        if (bu > 0 || tu > 0) t = t with { Vehicles = t.Vehicles.Select(v => v.Id == "bus" && bu > 0 ? v with { UpkeepPerDay = bu } : v.Id == "truck" && tu > 0 ? v with { UpkeepPerDay = tu } : v).ToList() };
        float ppc = EnvF("PEOPLEPERCAR", -1f); if (ppc > 0) t = t with { Trips = t.Trips with { PeoplePerCar = (int)ppc } };
        float stk = EnvF("CARSTUCK", -1f); if (stk > 0) t = t with { Trips = t.Trips with { CarStuckSeconds = stk } };
        float cto = EnvF("CARTIMEOUT", -1f); if (cto > 0) t = t with { Trips = t.Trips with { CarTimeoutSeconds = cto } };
        float mxc = EnvF("MAXCARS", -1f); if (mxc > 0) t = t with { Trips = t.Trips with { MaxCars = (int)mxc } };
        float cs = EnvF("CONGSMOOTH", -1f); if (cs >= 0) t = t with { Trips = t.Trips with { CongestionSmoothing = cs } };
        float ts = EnvF("TRANSITWAIT", -1f); if (ts > 0) t = t with { Trips = t.Trips with { TransitWaitSeconds = ts } };
        return t;
    }

    [Fact]
    public void Towns_Overview()
    {
        if (!On) return;
        foreach (ulong seed in new ulong[] { 1, 2, 3 })
        {
            var (sim, plan) = World(seed, 4);
            _out.WriteLine($"seed {seed}: {plan.Towns.Count} towns");
            foreach (var t in sim.Towns)
            {
                var s = sim.StatsOf(t.Id);
                _out.WriteLine($"  {t.Name}: pop {s.Population} jobs {s.Jobs} homes {s.Homes} commerce {s.Commerce} industry {s.Industry}");
            }
        }
    }

    /// <summary>A passenger line through several stations in each of the two closest towns, joined by a road; N buses.</summary>
    [Fact]
    public void BusLine_ThroughTwoTowns()
    {
        if (!On) return;
        int stops = Env("STOPS", 3), buses = Env("BUSES", 4), days = Env("DAYS", 10), from = 3;
        float carShare = EnvF("CARSHARE", -1f), radius = EnvF("RADIUS", -1f);
        _out.WriteLine($"stops/town {stops}, buses {buses}, carShare {carShare}, radius {radius}");
        double totalIncome = 0, totalUpkeep = 0; int lostTotal = 0, seeds = 0;
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4, 5, 6 })
        {
            var tuning = Tuning.Load();
            if (carShare >= 0) tuning = tuning with { Trips = tuning.Trips with { CarShare = carShare } };
            if (radius > 0) tuning = tuning with { Stations = tuning.Stations with { CoverageRadius = radius } };
            tuning = Override(tuning);
            var (sim, _) = World(seed, 4, tuning);
            sim.Execute(new TakeLoanCommand(1000000));
            var (i, j) = ClosestPair(sim);
            var ta = sim.Towns[i]; var tb = sim.Towns[j];
            float dist = MathF.Sqrt(MathF.Pow(ta.X - tb.X, 2) + MathF.Pow(ta.Z - tb.Z, 2));
            if (!Connect(sim, ta, tb)) { _out.WriteLine($"seed {seed}: no link"); continue; }
            var sa = StationsNear(sim, ta.X, ta.Z, stops);
            var sb = StationsNear(sim, tb.X, tb.Z, stops);
            int depot = Depot(sim, ta);
            var order = sa.Concat(Enumerable.Reverse(sb)).ToArray();
            if (order.Length < 2 || depot < 0) { _out.WriteLine($"seed {seed}: no stations/depot ({sa.Count}+{sb.Count}, depot {depot})"); continue; }
            int line = sim.Execute(new CreateLineCommand(order));
            int bought = 0;
            for (int k = 0; k < buses; k++) if (Buy(sim, depot, "bus", line) >= 0) bought++;
            var ln = sim.Lines[line];
            long inc0 = 0, up0 = 0, del0 = 0; var modes = new int[4];
            var wasWreck = new HashSet<int>();
            long sampleN = 0, queueSum = 0, queueMax = 0, loadSum = 0, loadN = 0, busTicks = 0, loadingTicks = 0, moveTicks = 0, stopEvents = 0; double speedSum = 0; var wasLoading = new HashSet<int>();
            var causes = new Dictionary<string, int>();
            for (int d = 0; d < days; d++)
            {
                if (d == from) { inc0 = ln.Income; up0 = ln.Upkeep; del0 = ln.Delivered; }
                long dayTicks = (long)Math.Round(sim.DayLengthSeconds / Simulation.StepSeconds);
                for (long t = 0; t < dayTicks; t++)
                {
                    sim.Step();
                    if (d >= from && t % 100 == 0)
                    {
                        sampleN++;
                        int q = 0; foreach (int st in order) q += (int)sim.Stations[st].Waiting[0];
                        queueSum += q; queueMax = Math.Max(queueMax, q);
                        for (int v = 0; v < sim.VehicleCount; v++) if (sim.IsCompanyVehicle(v) && sim.GetVehicle(v).Kind == VehicleKind.Car) { loadSum += sim.GetFleetInfo(v).Load; loadN++; }
                    }
                    if (d >= from)
                        for (int v = 0; v < sim.VehicleCount; v++)
                        {
                            if (!sim.IsCompanyVehicle(v) || sim.GetVehicle(v).Kind != VehicleKind.Car) continue;
                            var fi = sim.GetFleetInfo(v); var vs = sim.GetVehicle(v);
                            busTicks++;
                            if (fi.Loading) { loadingTicks++; if (!wasLoading.Contains(v)) { stopEvents++; wasLoading.Add(v); } }
                            else { wasLoading.Remove(v); moveTicks += vs.Speed > 0.5f ? 1 : 0; speedSum += vs.Speed; }
                        }
                    for (int v = 0; v < sim.VehicleCount; v++)
                    {
                        if (!sim.IsCompanyVehicle(v) || sim.GetVehicle(v).Kind != VehicleKind.Wreck || !wasWreck.Add(v)) continue;
                        var hit = sim.Incidents.First(x => x.VehicleIds.Contains(v));
                        string key = hit.Kind.ToString();
                        causes[key] = causes.GetValueOrDefault(key) + 1;
                        if (Environment.GetEnvironmentVariable("WRECKS") != null) _out.WriteLine($"   wreck: bus {v} {hit.Kind} day {d} {sim.TimeOfDay * 24:0.0}h lane {hit.Lane.Value} pos {hit.Position:0}");
                    }
                }
                if (d >= from) foreach (var t in new[] { ta, tb }) for (int m = 0; m < 4; m++) modes[m] += t.TripsYesterday[m];
            }
            int alive = Enumerable.Range(0, sim.VehicleCount).Count(v => sim.IsCompanyVehicle(v) && sim.GetVehicle(v).Kind == VehicleKind.Car);
            double n = days - from;
            double inc = (ln.Income - inc0) / n, up = (ln.Upkeep - up0) / n;
            totalIncome += inc; totalUpkeep += up; lostTotal += bought - alive; seeds++;
            _out.WriteLine($"seed {seed} {dist:0} m, {order.Length} stops, {alive}/{bought} buses: income/day {inc:0} upkeep/day {up:0} delivered/day {(ln.Delivered - del0) / n:0} | trips/day none {modes[0] / n:0} walk {modes[1] / n:0} car {modes[2] / n:0} transit {modes[3] / n:0} incidents {sim.Incidents.Count} {string.Join(",", sim.Incidents.GroupBy(x => x.Kind).Select(g => g.Key + "=" + g.Count()))} | queue avg {queueSum / Math.Max(1.0, sampleN):0} max {queueMax} bus load avg {loadSum / Math.Max(1.0, loadN):0.0} | bus time: loading {100.0 * loadingTicks / Math.Max(1, busTicks):0}% moving {100.0 * moveTicks / Math.Max(1, busTicks):0}% mean speed {speedSum / Math.Max(1, busTicks - loadingTicks):0.0} stops/bus/day {stopEvents / (double)Math.Max(1, alive) / (days - from):0.0} | bus wrecks {string.Join(",", causes.Select(c => c.Key + "=" + c.Value))}");
        }
        _out.WriteLine($"MEAN income/day {totalIncome / Math.Max(1, seeds):0} upkeep/day {totalUpkeep / Math.Max(1, seeds):0} lost buses {lostTotal}");
    }

    [Fact]
    public void TraceFirstBusWreck()
    {
        if (!On) return;
        ulong seed = (ulong)Env("SEED", 2);
        int stops = Env("STOPS", 3), buses = Env("BUSES", 4);
        long firstWreck = -1; int victim = -1; Incident? inc = null;
        {
            var sc = BusScenario(seed, stops, buses)!.Value; var sim = sc.Sim;
            for (long t = 0; t < 10 * 12000 && firstWreck < 0; t++)
            {
                sim.Step();
                for (int v = 0; v < sim.VehicleCount; v++)
                    if (sim.IsCompanyVehicle(v) && sim.GetVehicle(v).Kind == VehicleKind.Wreck) { firstWreck = t; victim = v; inc = sim.Incidents.First(x => x.VehicleIds.Contains(v)); break; }
            }
        }
        if (firstWreck < 0) { _out.WriteLine("no wreck"); return; }
        _out.WriteLine($"first wreck at tick {firstWreck}: bus {victim}, incident {inc!.Kind} lane {inc.Lane.Value} pos {inc.Position:0.0}, vehicles {string.Join("/", inc.VehicleIds)}");
        var sc2 = BusScenario(seed, stops, buses)!.Value; var s2 = sc2.Sim;
        var net = s2.Network;
        for (long t = 0; t <= firstWreck; t++)
        {
            s2.Step();
            if (t < firstWreck - 40) continue;
            var sb = new System.Text.StringBuilder($"  t{t}:");
            foreach (int v in inc.VehicleIds.Append(victim).Distinct())
            {
                var st = s2.GetVehicle(v);
                sb.Append($" v{v}[{(s2.IsCompanyVehicle(v) ? "bus" : "car")} L{st.Lane.Value} len{s2.LengthOf(v):0} pos {st.Position:0.0} spd {st.Speed:0.0} prev {s2.PreviousLaneOf(v).Value} next {s2.NextLaneOf(v).Value} {st.Kind}]");
            }
            _out.WriteLine(sb.ToString());
        }
        // Also everyone on the incident's lane and the lanes around it at the wreck moment
        var lane = inc.Lane;
        _out.WriteLine($"lane {lane.Value} length {net.GetLane(lane).Length:0.0}");
        for (int v = 0; v < s2.VehicleCount; v++)
        {
            var st = s2.GetVehicle(v);
            if (st.Kind == VehicleKind.Removed) continue;
            if (st.Lane.Value == lane.Value || s2.NextLaneOf(v).Value == lane.Value || s2.PreviousLaneOf(v).Value == lane.Value)
                _out.WriteLine($"  near: v{v} [{(s2.IsCompanyVehicle(v) ? "bus" : "car")} L{st.Lane.Value} len{s2.LengthOf(v):0} pos {st.Position:0.0} spd {st.Speed:0.0} prev {s2.PreviousLaneOf(v).Value} next {s2.NextLaneOf(v).Value} {st.Kind}]");
        }
    }

    /// <summary>Five towns, no company lines: how jammed the morning rush is.</summary>
    [Fact]
    public void Rush_FiveTowns()
    {
        if (!On) return;
        int days = Env("DAYS", 5);
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4 })
        {
            var (sim, plan) = World(seed, 5, Override(Tuning.Load()));
            sim.SeedTraffic(20);
            int maxCars = 0; long carTicks = 0, samples = 0;
            var ratios = new List<float>();
            long dayTicks = (long)Math.Round(sim.DayLengthSeconds / Simulation.StepSeconds);
            var byHour = new double[24]; var byHourN = new int[24];
            int people = sim.Towns.Sum(t => sim.StatsOf(t.Id).Population);
            var modes = new int[4];
            for (int d = 0; d < days; d++)
            {
                for (long t = 0; t < dayTicks; t++)
                {
                    sim.Step();
                    if (t % 20 == 0) { maxCars = Math.Max(maxCars, sim.TripVehicleCount); carTicks += sim.TripVehicleCount; samples++; if (d >= 1) { int hr = (int)(sim.TimeOfDay * 24); byHour[hr] += sim.TripVehicleCount; byHourN[hr]++; } }
                }
                if (d >= 1)
                {
                    foreach (var tn in sim.Towns) for (int m = 0; m < 4; m++) modes[m] += tn.TripsYesterday[m];
                    foreach (var g in sim.TripGroups)
                        if (g.LastMode == TripMode.Car && g.LastTripSeconds > 0 && g.Origin >= 0 && g.Destination >= 0)
                        {
                            var (mode, secs) = sim.ModeFor(g.Origin, g.Destination, g.HasCar);
                            if (mode == TripMode.Car && secs > 0) ratios.Add(g.LastTripSeconds / secs);
                        }
                }
            }
            ratios.Sort();
            float Q(double p) => ratios.Count == 0 ? 0 : ratios[(int)Math.Min(ratios.Count - 1, p * ratios.Count)];
            double n = days - 1;
            _out.WriteLine($"seed {seed}: {plan.Towns.Count} towns, {people} people | trip cars max {maxCars} mean {carTicks / Math.Max(1.0, samples):0} | trips/day none {modes[0] / n:0} walk {modes[1] / n:0} car {modes[2] / n:0} | car trip time / predicted: median {Q(0.5):0.0} p90 {Q(0.9):0.0} max {Q(0.999):0.0} | incidents {sim.Incidents.Count} ({sim.Incidents.Count / (double)days:0.0}/day) cars by hour [{string.Join(" ", Enumerable.Range(0, 24).Select(h => (byHour[h] / Math.Max(1, byHourN[h])).ToString("0")))}]");
        }
    }

    [Fact]
    public void WhyAreBusesStanding()
    {
        if (!On) return;
        ulong seed = (ulong)Env("SEED", 5);
        int stops = Env("STOPS", 3), buses = Env("BUSES", 4), days = Env("DAYS", 4);
        var tuning = Override(Tuning.Load());
        var radius = EnvF("RADIUS", 150f); tuning = tuning with { Stations = tuning.Stations with { CoverageRadius = radius } };
        var sc = BusScenario(seed, stops, buses, tuning)!.Value; var sim = sc.Sim; var net = sim.Network;
        long dayTicks = (long)Math.Round(sim.DayLengthSeconds / Simulation.StepSeconds);
        for (long t = 0; t < days * dayTicks; t++)
        {
            sim.Step();
            // sample at 3 times of day on the last day
            if (t >= (days - 1) * dayTicks && (t % (dayTicks / 6) == 0))
            {
                _out.WriteLine($"--- t {t} time {sim.TimeOfDay * 24:0.0}h  tripcars {sim.TripVehicleCount} open incidents {sim.OpenIncidentIds.Count}");
                for (int v = 0; v < sim.VehicleCount; v++)
                {
                    if (!sim.IsCompanyVehicle(v)) continue;
                    var st = sim.GetVehicle(v); var fi = sim.GetFleetInfo(v);
                    var lane = net.GetLane(st.Lane);
                    // vehicle immediately ahead in the lane
                    int ahead = -1; float gap = float.MaxValue;
                    for (int j = 0; j < sim.VehicleCount; j++)
                    {
                        var sj = sim.GetVehicle(j);
                        if (j == v || sj.Kind == VehicleKind.Removed || sj.Lane.Value != st.Lane.Value || sj.Position <= st.Position) continue;
                        float g = sj.Position - sim.LengthOf(j) - st.Position;
                        if (g < gap) { gap = g; ahead = j; }
                    }
                    string aheadText = ahead < 0 ? "none ahead" : $"ahead v{ahead} {(sim.IsCompanyVehicle(ahead) ? "bus" : "car")} {sim.GetVehicle(ahead).Kind} gap {gap:0.0} spd {sim.GetVehicle(ahead).Speed:0.0}";
                    int zoneStartLeft = (int)(lane.Length - st.Position);
                    _out.WriteLine($"  bus {v} {st.Kind} lane {st.Lane.Value} len {lane.Length:0} pos {st.Position:0.0} ({zoneStartLeft} m to end) spd {st.Speed:0.0} loading {fi.Loading} load {fi.Load} target S{sim.TargetStationOf(v)} next {sim.NextLaneOf(v).Value} | {aheadText}");
                }
            }
        }
    }

    /// <summary>A truck line from a forest to a sawmill (or a farm to a food plant): income per truck.</summary>
    [Fact]
    public void TruckLine_PrimaryToProcessor()
    {
        if (!On) return;
        int trucks = Env("TRUCKS", 3), days = Env("DAYS", 10), from = 3, primaryType = Env("PRIMARY", 0), processorType = Env("PROCESSOR", 3);
        double totalIncome = 0, totalUpkeep = 0; int n = 0;
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4, 5, 6 })
        {
            var (sim, plan) = World(seed, 3, Override(Tuning.Load()), IndustryAmount.Normal);
            sim.Execute(new TakeLoanCommand(1000000));
            var a = sim.Industries.Where(i => i.Type == primaryType).ToList();
            var b = sim.Industries.Where(i => i.Type == processorType).ToList();
            if (a.Count == 0 || b.Count == 0) { _out.WriteLine($"seed {seed}: no pair ({a.Count} primary, {b.Count} processor)"); continue; }
            var (pa, pb) = a.SelectMany(x => b.Select(y => (x, y))).OrderBy(t => MathF.Sqrt(MathF.Pow(t.x.X - t.y.X, 2) + MathF.Pow(t.x.Z - t.y.Z, 2))).First();
            float dist = MathF.Sqrt(MathF.Pow(pa.X - pb.X, 2) + MathF.Pow(pa.Z - pb.Z, 2));
            if (sim.Execute(new BuildRoadCommand(pa.X, pa.Z, pb.X, pb.Z, RoadType.TwoLane, 14f)) <= 0) { _out.WriteLine($"seed {seed}: road refused"); continue; }
            var net = sim.Network;
            int depot = sim.Execute(new AddDepotCommand(net.FindNodeNear(pa.X, pa.Z, 5f, withRoadsOnly: true), DepotLevel.Simple));
            // stations a little inside the road, near each industry
            float dx = (pb.X - pa.X) / dist, dz = (pb.Z - pa.Z) / dist;
            int sa = sim.Execute(new BuildStationCommand(pa.X + dx * 60f, pa.Z + dz * 60f));
            int sb = sim.Execute(new BuildStationCommand(pb.X - dx * 60f, pb.Z - dz * 60f));
            if (depot < 0 || sa < 0 || sb < 0) { _out.WriteLine($"seed {seed}: depot {depot} sa {sa} sb {sb}"); continue; }
            int line = sim.Execute(new CreateLineCommand(new[] { sa, sb }));
            int bought = 0;
            for (int k = 0; k < trucks; k++) if (Buy(sim, depot, "truck", line) >= 0) bought++;
            var ln = sim.Lines[line];
            long inc0 = 0, up0 = 0, del0 = 0;
            for (int d = 0; d < days; d++)
            {
                if (d == from) { inc0 = ln.Income; up0 = ln.Upkeep; del0 = ln.Delivered; }
                Run(sim, 1);
            }
            double nd = days - from;
            double inc = (ln.Income - inc0) / nd, up = (ln.Upkeep - up0) / nd;
            totalIncome += inc; totalUpkeep += up; n++;
            _out.WriteLine($"seed {seed} {dist:0} m, {bought} trucks: income/day {inc:0} upkeep/day {up:0} delivered/day {(ln.Delivered - del0) / nd:0} | income per truck/day {inc / Math.Max(1, bought):0} | forest waiting at station {sim.Stations[sa].Waiting.Max():0} industry stock {pa.Output:0} processor input {pb.Input.Max():0}");
        }
        _out.WriteLine($"MEAN income/day {totalIncome / Math.Max(1, n):0} upkeep/day {totalUpkeep / Math.Max(1, n):0}");
    }
}
