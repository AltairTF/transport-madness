using TransportMadness.Sim;

namespace TransportMadness.Sim.Tests;

/// <summary>What a bot's campaign came to.</summary>
/// <param name="DismissedAt">The month index of the turn that dismissed the committee, -1 when it was not dismissed.</param>
/// <param name="Grade">0 (the best) to 3, -1 when the campaign did not finish.</param>
internal sealed record CampaignResult(string Bot, ulong Seed, int DismissedAt, int Grade, float Score, float Standing, long Money, double Delivered, int OrdersMet, int OrdersLate, int OrdersFailed,
    int Lines, int Vehicles, IReadOnlyList<float> StandingByMonth, IReadOnlyList<string> Log)
{
    public override string ToString() =>
        $"{Bot} seed {Seed}: {(DismissedAt >= 0 ? $"dismissed at month {DismissedAt}" : $"grade {Grade} score {Score:F1}")}, standing {Standing:F1}, money {Money}, delivered {Delivered:F0}, " +
        $"orders met/late/failed {OrdersMet}/{OrdersLate}/{OrdersFailed}, {Lines} lines, {Vehicles} vehicles; standing by year: " +
        string.Join(" ", StandingByMonth.Where((_, i) => i % 12 == 11).Select(s => s.ToString("F0")));
}

/// <summary>
/// Players that play a whole campaign headless through commands (M6c), so that the balance can be measured: an idle committee that builds nothing,
/// a company that runs only buses and no maintenance, and a competent one that builds truck lines along the chains to the road gateway, buses between
/// the towns, roads from its industries with jobs to the nearest town, keeps the default maintenance, takes the quota orders it can meet and adds
/// trucks where cargo waits.
/// </summary>
internal static class CampaignBots
{
    internal enum Bot { Idle, BusesNoMaintenance, Competent }

    const float Speed = 14f;

    internal static CampaignResult Play(Bot bot, ulong seed, Tuning? tuning = null, int towns = 5, IndustryAmount industries = IndustryAmount.Normal, int months = 96)
    {
        var (sim, _) = WorldBuilder.Towns(seed, towns, industries, tuning);
        var log = new List<string>();
        var player = bot switch
        {
            Bot.Idle => (IPlayer)new IdlePlayer(),
            Bot.BusesNoMaintenance => new BusPlayer(sim, log),
            _ => new CompetentPlayer(sim, log),
        };
        player.Start();
        long ticksPerMonth = (long)Math.Round(sim.DayLengthSeconds * sim.Network.Tuning.Economy.DaysPerMonth / Simulation.StepSeconds);
        int dismissedAt = -1, eventsSeen = 0;
        for (int m = 0; m < months && !sim.Company.CampaignOver; m++)
        {
            for (long t = 0; t < ticksPerMonth && !sim.Company.CampaignOver; t++)
            {
                sim.Step();
                if (t % 2400 == 0) player.Tick(); // every two minutes of the game
            }
            if (sim.Company.Dismissed && dismissedAt < 0) dismissedAt = (int)sim.MonthIndex - 1;
            for (int id = Math.Max(eventsSeen, sim.EventsDropped); id < sim.EventCount; id++)
            {
                var e = sim.Events[id - sim.EventsDropped];
                if (e.Severity != EventSeverity.Info) log.Add($"  [{e.Kind}] {e.Text}");
            }
            eventsSeen = sim.EventCount;
            if (sim.LastMonth is { } r)
            {
                int fleet = 0;
                for (int v = 0; v < sim.VehicleCount; v++) if (sim.IsCompanyVehicle(v)) fleet++;
                log.Add($"m{r.Month}: export {r.ExportValue:0} ({r.ExportTerm:+0.0;-0.0}) deficit {r.DeficitTerm:0.0} fines {r.FineTerm:0.0} standing {r.StandingAfter:0.0} money {sim.Company.Money} loan {sim.Company.Loan} fleet {fleet} parked {sim.Garage.Count}");
            }
        }
        if (Environment.GetEnvironmentVariable("BOT_DIAG") != null)
        {
            int stopped = 0, company = 0;
            for (int v = 0; v < sim.VehicleCount; v++)
            {
                if (!sim.IsCompanyVehicle(v)) continue;
                company++;
                if (sim.GetVehicle(v).Speed < 0.5f) stopped++;
            }
            log.Add($"diag: company {company}, stopped {stopped}, open incidents {sim.Incidents.Count(i => i.State != IncidentState.Cleared)}, road works {sim.RoadWorks.Count}, vehicles {sim.VehicleCount}");
            foreach (var g in sim.Gateways) log.Add($"diag: gateway {g.Name} damaged until {g.DamagedUntil} (tick {sim.Tick})");
            foreach (var st in sim.Stations) log.Add($"diag: station {st.Id} active {st.Active} shut {st.ShutUntil} waiting {string.Join(",", st.Waiting.Select((w, i) => (w, i)).Where(x => x.w > 0).Select(x => $"{x.i}:{x.w:0}"))}");
            for (int v = 0; v < sim.VehicleCount; v++)
                if (sim.IsCompanyVehicle(v)) { var f = sim.GetFleetInfo(v); var st = sim.GetVehicle(v); log.Add($"diag: v{v} line {f.Line} next {f.NextStop} load {f.Load} cargo {f.Cargo} speed {st.Speed:0.0} lane {st.Lane.Value} pos {st.Position:0} kind {st.Kind} ghost {sim.IsGhost(v)}"); }
            var lanes = new SortedSet<int>();
            for (int v = 0; v < sim.VehicleCount; v++)
            {
                if (!sim.IsCompanyVehicle(v)) continue;
                lanes.Add(sim.GetVehicle(v).Lane.Value);
                lanes.Add(sim.NextLaneOf(v).Value);
            }
            foreach (int l in lanes)
            {
                if (l < 0) continue;
                var lane = sim.Network.GetLane(new LaneId(l));
                var on = Enumerable.Range(0, sim.VehicleCount).Where(v => sim.GetVehicle(v).Kind != VehicleKind.Removed && sim.GetVehicle(v).Lane.Value == l)
                    .Select(v => $"v{v}@{sim.GetVehicle(v).Position:0}->{sim.NextLaneOf(v).Value}{(sim.IsCompanyVehicle(v) ? "c" : "")}");
                log.Add($"diag: lane {l} road {lane.Road.Value} {lane.StartNode}->{lane.EndNode} len {lane.Length:0} next [{string.Join(",", lane.Next.Select(n => n.Value))}] roadsAtEnd {sim.Network.RoadsAt(lane.EndNode).Count} station {sim.Stations.FirstOrDefault(st => st.Node == lane.EndNode)?.Id} light {sim.LightAt(lane.EndNode) != null}: {string.Join(" ", on)}");
            }
        }
        var c = sim.Company;
        int vehicles = 0;
        for (int v = 0; v < sim.VehicleCount; v++) if (sim.IsCompanyVehicle(v)) vehicles++;
        return new CampaignResult(bot.ToString(), seed, dismissedAt, c.Grade, c.Score, c.Standing, c.Money, c.DeliveredValue, c.OrdersMet, c.OrdersLate, c.OrdersFailed,
            sim.Lines.Count(l => l.Active), vehicles, sim.StandingHistory.ToList(), log);
    }

    interface IPlayer
    {
        void Start();
        void Tick();
    }

    sealed class IdlePlayer : IPlayer
    {
        public void Start() { }
        public void Tick() { }
    }

    /// <summary>Common moves: roads that bend round a hill when the straight one is refused, stations by an industry's door, depots, lines and vehicles.</summary>
    abstract class Builder(Simulation sim, List<string> log, DepotLevel depotLevel)
    {
        protected readonly DepotLevel DepotLevel = depotLevel;
        protected readonly Simulation Sim = sim;
        protected readonly List<string> Log = log;
        protected readonly Dictionary<int, int> DepotOfLine = new();
        /// <summary>Money the bot does not spend on roads that are not its freight lines (the competent one keeps it for trucks).</summary>
        protected virtual float Keep => 0f;

        /// <summary>
        /// The cheapest way the bot sees from one point to another: straight, or bent at one point off the line (a human would follow the valleys;
        /// this is the poor man's version). Null when every try is refused.
        /// </summary>
        protected (float Cost, List<(float X, float Z)> Points)? BestRoute(float ax, float az, float bx, float bz)
        {
            var net = Sim.Network;
            (float, List<(float, float)>)? best = null;
            void Try(List<(float X, float Z)> points)
            {
                float cost = 0f;
                for (int k = 0; k + 1 < points.Count; k++)
                {
                    if (net.EstimateCost(points[k].X, points[k].Z, points[k + 1].X, points[k + 1].Z, RoadType.TwoLane) is not { } c) return;
                    cost += c.Money;
                }
                if (best == null || cost < best.Value.Item1) best = (cost, points);
            }
            Try(new() { (ax, az), (bx, bz) });
            float dx = bx - ax, dz = bz - az, len = MathF.Sqrt(dx * dx + dz * dz);
            if (len > 60f)
            {
                float nx = -dz / len, nz = dx / len;
                foreach (float along in new[] { 0.3f, 0.5f, 0.7f })
                    foreach (float side in new[] { -0.45f, -0.3f, -0.15f, 0.15f, 0.3f, 0.45f })
                        Try(new() { (ax, az), (ax + dx * along + nx * len * side, az + dz * along + nz * len * side), (bx, bz) });
            }
            return best;
        }

        protected bool Road(float ax, float az, float bx, float bz, float maxCost = float.MaxValue)
        {
            if (BestRoute(ax, az, bx, bz) is not { } route || route.Cost > maxCost) return false;
            for (int k = 0; k + 1 < route.Points.Count; k++)
                if (Sim.Execute(new BuildRoadCommand(route.Points[k].X, route.Points[k].Z, route.Points[k + 1].X, route.Points[k + 1].Z, RoadType.TwoLane, Speed)) <= 0) return false;
            return true;
        }

        /// <summary>A station on the road near a point (an industry's door), reusing one already within 100 m.</summary>
        protected int StationAt(float x, float z, float towardX, float towardZ)
        {
            int near = Sim.NearestStation(x, z, 100f);
            if (near >= 0) return near;
            float dx = towardX - x, dz = towardZ - z, len = MathF.Max(1f, MathF.Sqrt(dx * dx + dz * dz));
            foreach (float along in new[] { 50f, 35f, 70f, 0f })
            {
                int id = Sim.Execute(new BuildStationCommand(x + dx / len * along, z + dz / len * along));
                if (id >= 0) return id;
            }
            return -1;
        }

        protected int DepotNear(float x, float z)
        {
            foreach (var d in Sim.Depots)
            {
                if (!d.Active) continue;
                var (dx, dz) = Sim.Network.GetNode(d.Node);
                if ((dx - x) * (dx - x) + (dz - z) * (dz - z) < 400f * 400f) return d.Id;
            }
            int node = Sim.Network.FindNodeNear(x, z, 200f, withRoadsOnly: true);
            return node < 0 ? -1 : Sim.Execute(new AddDepotCommand(node, DepotLevel));
        }

        protected int Line(int a, int b, float depotX, float depotZ)
        {
            if (a < 0 || b < 0 || a == b) return -1;
            int line = Sim.Execute(new CreateLineCommand(new[] { a, b }));
            if (line < 0) return -1;
            int depot = DepotNear(depotX, depotZ);
            if (depot < 0) return -1;
            DepotOfLine[line] = depot;
            return line;
        }

        protected int Buy(int line, string model)
        {
            if (!DepotOfLine.TryGetValue(line, out int depot)) return -1;
            return Sim.Execute(new BuyVehicleCommand(depot, model, line));
        }

        protected int VehiclesOn(int line)
        {
            int n = 0;
            for (int v = 0; v < Sim.VehicleCount; v++) if (Sim.IsCompanyVehicle(v) && Sim.GetFleetInfo(v).Line == line) n++;
            return n;
        }

        /// <summary>A road between the streets of two towns, from the edge of each that faces the other, if it costs less than 150 000.</summary>
        bool ConnectTowns(Town a, Town b)
        {
            var net = Sim.Network;
            float dx = b.X - a.X, dz = b.Z - a.Z, len = MathF.Sqrt(dx * dx + dz * dz); dx /= len; dz /= len;
            int na = net.FindNodeNear(a.X + dx * 250f, a.Z + dz * 250f, 400f, withRoadsOnly: true);
            int nb = net.FindNodeNear(b.X - dx * 250f, b.Z - dz * 250f, 400f, withRoadsOnly: true);
            if (na < 0 || nb < 0 || na == nb) return false;
            var (ax, az) = net.GetNode(na);
            var (bx, bz) = net.GetNode(nb);
            return Road(ax, az, bx, bz, Math.Min(150000f, Sim.Company.Money - Keep));
        }

        /// <summary>Bus lines between each town and the nearest other one (a road joins them first).</summary>
        protected void TownBuses(int buses)
        {
            var towns = Sim.Towns.ToList();
            var done = new HashSet<(int, int)>();
            foreach (var a in towns)
            {
                var b = towns.Where(t => t != a).OrderBy(t => (t.X - a.X) * (t.X - a.X) + (t.Z - a.Z) * (t.Z - a.Z)).FirstOrDefault();
                if (b == null) continue;
                var key = (Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id));
                if (!done.Add(key)) continue;
                if (!ConnectTowns(a, b)) { Log.Add($"no road {a.Name}-{b.Name}"); continue; }
                var sa = Worlds.StationsNear(Sim, a.X, a.Z, 2);
                var sb = Worlds.StationsNear(Sim, b.X, b.Z, 2);
                if (sa.Count == 0 || sb.Count == 0) continue;
                int line = Sim.Execute(new CreateLineCommand(sa.Concat(Enumerable.Reverse(sb)).ToArray()));
                if (line < 0) continue;
                int depot = DepotNear(a.X, a.Z);
                if (depot < 0) continue;
                DepotOfLine[line] = depot;
                for (int k = 0; k < buses; k++) Buy(line, "bus");
                Log.Add($"bus line {a.Name}-{b.Name}");
            }
        }
    }

    sealed class BusPlayer(Simulation sim, List<string> log) : Builder(sim, log, DepotLevel.Simple), IPlayer
    {
        public void Start()
        {
            Sim.Execute(new SetMaintenancePolicyCommand(PolicyScope.Company, 0, MaintenancePolicy.Off));
            TownBuses(4);
        }

        public void Tick() { }
    }

    sealed class CompetentPlayer(Simulation sim, List<string> log) : Builder(sim, log, DepotLevel.Workshop), IPlayer
    {
        readonly List<(int Line, int From, int Cargo)> _freight = new();
        readonly HashSet<int> _exported = new(); // cargo sent to the road gateway
        Gateway? _gate;
        long _lastMonth = -1;

        int TypeIndex(string id) => Sim.IndustryTuning.Types.ToList().FindIndex(t => t.Id == id);
        int CargoIndex(string id) => Sim.Network.Tuning.Cargo.ToList().FindIndex(c => c.Id == id);
        static float Dist(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

        // What the bot spends on roads at the start, and the most one link may cost.
        const float RoadBudget = 900000f, MaxLinkCost = 300000f;
        float _spent;
        protected override float Keep => _freight.Count * 3 * 28000f + 100000f;

        public void Start()
        {
            _gate = Sim.Gateways.FirstOrDefault(g => g.Kind == GatewayKind.Road);
            Sim.Execute(new TakeLoanCommand(400000));
            if (_gate != null)
            {
                // Every primary industry whose output the gateway takes, cheapest road first, while the budget lasts.
                foreach (var (industry, route) in RawSources())
                {
                    // Keep enough for three trucks on every line built so far and this one, and a little over.
                    float keep = (_freight.Count + 1) * 3 * 28000f + 100000f;
                    if (_spent + route.Cost > RoadBudget || route.Cost > MaxLinkCost || Sim.Company.Money - route.Cost < keep) continue;
                    LinkToGate(industry);
                }
            }
            StaffRoads();
            TownBuses(3);
            BuyTrucks();
            Log.Add($"start: {_freight.Count} freight lines, roads {_spent:0}, money {Sim.Company.Money}");
        }

        readonly HashSet<int> _linked = new(); // industries with a line to the gateway
        readonly HashSet<int> _fed = new();    // processors with a line from a producer

        List<(Industry Industry, (float Cost, List<(float X, float Z)> Points) Route)> RawSources()
        {
            var exports = Sim.Network.Tuning.Gateways.Exports.Select(CargoIndex).ToHashSet();
            return Sim.Industries
                .Where(i => !_linked.Contains(i.Id) && Sim.OutputOf(i.Type) >= 0 && exports.Contains(Sim.OutputOf(i.Type)) && Sim.IndustryTuning.Types[i.Type].Inputs.Count == 0)
                .Select(i => (Industry: i, Route: BestRoute(i.AccessX, i.AccessZ, _gate!.AccessX, _gate.AccessZ)))
                .Where(x => x.Route != null).Select(x => (x.Industry, x.Route!.Value)).OrderBy(x => x.Value.Cost).ToList();
        }

        bool LinkToGate(Industry industry)
        {
            int cargo = Sim.OutputOf(industry.Type);
            if (!Link(industry.AccessX, industry.AccessZ, _gate!.AccessX, _gate.AccessZ, cargo, $"{Sim.IndustryTuning.Types[industry.Type].Id}->gateway")) return false;
            _exported.Add(cargo);
            _linked.Add(industry.Id);
            return true;
        }

        /// <summary>
        /// A step of growth, when the money allows: the cheapest of a raw source not yet sent to the gateway, or a processor with one input
        /// fed from the nearest producer and sent to the gateway (both roads), so that the exports keep up with what the phases ask.
        /// </summary>
        void Expand()
        {
            if (_gate == null) return;
            float reserve = 250000f;
            var options = new List<(float Cost, Action Build, string Name)>();
            foreach (var (industry, route) in RawSources().Take(3))
                options.Add((route.Cost, () => { if (LinkToGate(industry)) BuyTrucks(); }, Sim.IndustryTuning.Types[industry.Type].Id));
            foreach (var processor in Sim.Industries.Where(i => !_fed.Contains(i.Id) && Sim.IndustryTuning.Types[i.Type].Inputs.Count == 1 && Sim.OutputOf(i.Type) >= 0))
            {
                int input = CargoIndex(Sim.IndustryTuning.Types[processor.Type].Inputs[0].Cargo);
                var producer = Sim.Industries.Where(i => Sim.OutputOf(i.Type) == input).OrderBy(i => Dist(i.X, i.Z, processor.X, processor.Z)).FirstOrDefault();
                if (producer == null) continue;
                var feed = BestRoute(producer.AccessX, producer.AccessZ, processor.AccessX, processor.AccessZ);
                var out_ = _linked.Contains(processor.Id) ? (0f, new List<(float, float)>()) : BestRoute(processor.AccessX, processor.AccessZ, _gate.AccessX, _gate.AccessZ);
                if (feed == null || out_ == null) continue;
                options.Add((feed.Value.Cost + out_.Value.Item1, () =>
                {
                    if (!Link(producer.AccessX, producer.AccessZ, processor.AccessX, processor.AccessZ, input, $"{Sim.IndustryTuning.Types[producer.Type].Id}->{Sim.IndustryTuning.Types[processor.Type].Id}")) return;
                    _fed.Add(processor.Id);
                    if (!_linked.Contains(processor.Id)) LinkToGate(processor);
                    BuyTrucks();
                }, Sim.IndustryTuning.Types[processor.Type].Id));
            }
            if (options.Count == 0) return;
            var best = options.OrderBy(o => o.Cost).First();
            if (Sim.Company.Money - best.Cost < reserve) return;
            Log.Add($"month {Sim.MonthIndex}: expand {best.Name} for {best.Cost:0}");
            best.Build();
        }

        int _trucksBoughtFor;
        void BuyTrucks()
        {
            string model = Sim.Today.Year >= 1940 ? "heavytruck" : "truck";
            for (; _trucksBoughtFor < _freight.Count; _trucksBoughtFor++)
                for (int k = 0; k < 3; k++) Buy(_freight[_trucksBoughtFor].Line, model);
        }

        bool Link(float ax, float az, float bx, float bz, int cargo, string name)
        {
            float before = Sim.Company.Money;
            bool built = Road(ax, az, bx, bz, MaxLinkCost);
            _spent += before - Sim.Company.Money;
            if (!built) { Log.Add($"no road {name}"); return false; }
            int sa = StationAt(ax, az, bx, bz), sb = StationAt(bx, bz, ax, az);
            int line = Line(sa, sb, ax, az);
            if (line < 0) { Log.Add($"no line {name}"); return false; }
            _freight.Add((line, sa, cargo));
            Log.Add($"line {name} ({Dist(ax, az, bx, bz):0} m), money {Sim.Company.Money}");
            return true;
        }

        /// <summary>Industries with jobs get a road from their door to the nearest town's streets, so that residents can reach them.</summary>
        void StaffRoads()
        {
            foreach (var industry in Sim.Industries)
            {
                if (Sim.JobsOf(industry) == 0) continue;
                var town = Sim.Towns.OrderBy(t => Dist(t.X, t.Z, industry.X, industry.Z)).FirstOrDefault();
                if (town == null) continue;
                int node = Sim.Network.FindNodeNear(town.X, town.Z, 400f, withRoadsOnly: true);
                if (node < 0) continue;
                var (nx, nz) = Sim.Network.GetNode(node);
                Log.Add(Road(industry.AccessX, industry.AccessZ, nx, nz, Math.Min(150000f, Sim.Company.Money - Keep)) ? $"staff road to {town.Name}" : "no staff road");
            }
        }

        public void Tick()
        {
            // Orders: the quotas of a cargo already sent to the gateway are taken; others are declined.
            foreach (var order in Sim.Orders.Where(o => o.State == OrderState.Offered).ToList())
            {
                bool can = order.Kind == OrderKind.Quota && _exported.Contains(order.Cargo) && _gate != null && order.Gateway == _gate.Id;
                if (can && Sim.Execute(new AcceptOrderCommand(order.Id)) == 1) Log.Add($"month {Sim.MonthIndex}: took {order.Title}");
                else Sim.Execute(new DeclineOrderCommand(order.Id));
            }
            if (Sim.MonthIndex == _lastMonth) return;
            _lastMonth = Sim.MonthIndex;
            if (Sim.Company.Loan > 0 && Sim.Company.Money > 600000) Sim.Execute(new RepayLoanCommand(Math.Min(Sim.Company.Loan, Sim.Company.Money - 400000)));
            Expand();
            // Vehicles towed in after a breakdown are serviced, and those fit to run go back on the line of their kind with the fewest.
            foreach (var parked in Sim.Garage.Where(p => p.Active && !p.Rail).ToList())
            {
                if (parked.Condition < 60f) { Sim.Execute(new SellParkedCommand(parked.Id)); continue; }
                bool bus = Sim.VehicleModels[parked.Model].Cargo == "passengers";
                var lines = bus ? DepotOfLine.Keys.Where(l => !_freight.Any(f => f.Line == l)) : _freight.Select(f => f.Line);
                int line = lines.Where(l => Sim.Lines[l].Active).OrderBy(VehiclesOn).DefaultIfEmpty(-1).First();
                if (line >= 0) Sim.Execute(new AssignParkedCommand(parked.Id, line));
            }
            // Once a month: a truck more where cargo waits, while the money lasts.
            string model = Sim.Today.Year >= 1940 ? "heavytruck" : "truck";
            foreach (var (line, from, cargo) in _freight)
            {
                if (Sim.Company.Money < 60000) break;
                var station = Sim.Stations[from];
                int on = VehiclesOn(line);
                if (on < 3 || (station.Waiting[cargo] > 60 && on < 10)) Buy(line, model);
            }
        }
    }
}
