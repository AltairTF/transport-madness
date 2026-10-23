namespace TransportMadness.Sim;

/// <summary>The government's orders (C5): drawn at random intervals inside the phase of the war, accepted by the player, met by deliveries to a gateway or a town.</summary>
public sealed partial class Simulation
{
    const ulong OrderSeedMix = 0x4F5244455253C0DEUL; // the orders draw from their own stream, so maps without gateways run as before

    readonly OrderTuning _orderTuning;
    readonly SimRandom _orderRng;
    readonly List<Order> _orders = new();
    readonly List<Order> _liveOrders = new(); // offered or active, in id order
    long _nextOfferTick;
    bool _tutorialOffered;
    string _lastOfferKey = "";

    public OrderTuning OrderTuning => _orderTuning;
    /// <summary>Every order ever offered, in id order.</summary>
    public IReadOnlyList<Order> Orders => _orders;
    /// <summary>Changes whenever an order is offered, accepted, closed or makes progress by a whole unit (for a window).</summary>
    public int OrdersVersion { get; private set; }
    /// <summary>Orders accepted and not yet closed.</summary>
    public int ActiveOrders => _liveOrders.Count(o => o.State == OrderState.Active);
    /// <summary>Orders on offer.</summary>
    public int OpenOffers => _liveOrders.Count(o => o.State == OrderState.Offered);

    /// <summary>Where an order is on the map: its gateway, its town, or the first works the state planned when it offered it; null only for an order without any (the Go to of its row is hidden).</summary>
    public (float X, float Z)? PlaceOf(Order o) => o.Kind switch
    {
        OrderKind.Quota => (_gateways[o.Gateway].X, _gateways[o.Gateway].Z),
        OrderKind.Civil => (_towns[o.Town].X, _towns[o.Town].Z),
        _ => o.Works.Count > 0 ? (o.Works[0].X, o.Works[0].Z) : null,
    };

    void OrderEvent(Order o, EventSeverity severity, string text)
    {
        if (PlaceOf(o) is { } place) AddEvent(SimEventKind.Order, severity, place.X, place.Z, o.Id, text);
        else AddEvent(SimEventKind.Order, severity, o.Id, text);
    }

    // ---- The orders' commands ---------------------------------------------------------------------------------

    /// <summary>Why an offered order cannot be accepted now, null when it can: the stations that cover its source and its destination must already stand.</summary>
    public string? WhyNotAccept(int order)
    {
        if (order < 0 || order >= _orders.Count) return "No such order";
        var o = _orders[order];
        if (o.State != OrderState.Offered) return "It is no longer on offer";
        if (Company.CampaignOver) return "The campaign is over";
        if (ActiveOrders >= _orderTuning.MaxActive) return $"{_orderTuning.MaxActive} orders are already under way";
        if (_coverageDirty) RefreshCoverage();
        if (o.Kind == OrderKind.StateWork)
            return _gateways.Any(IsCovered) ? null : "Build a station beside a gateway first";
        if (o.Kind == OrderKind.Quota)
        {
            var g = _gateways[o.Gateway];
            if (!IsCovered(g)) return $"Build a {g.Kind.ToString().ToLowerInvariant()} station beside the {g.Name} first";
        }
        else if (!_towns[o.Town].Buildings.Any(b => _buildingStations.Count > b && _buildingStations[b].Length > 0 && _buildings[b].State == BuildingState.Active))
            return $"Build a station in {_towns[o.Town].Name} first";
        if (!ProducerCovered(o.Cargo)) return $"Build a station at {Producers(o.Cargo)} first";
        return null;
    }

    int AcceptOrder(int order)
    {
        if (WhyNotAccept(order) != null) return 0;
        var o = _orders[order];
        o.State = OrderState.Active;
        o.AcceptedAt = Tick;
        o.Deadline = Tick + (long)o.Days * _ticksPerDay;
        if (o.Kind == OrderKind.StateWork) FoundWorks(o);
        OrdersVersion++;
        OrderEvent(o, EventSeverity.Info, $"Order accepted: {o.Title} ({o.Days} days)");
        return 1;
    }

    int DeclineOrder(int order)
    {
        if (order < 0 || order >= _orders.Count || _orders[order].State != OrderState.Offered) return 0;
        Close(_orders[order], OrderState.Declined);
        return 1;
    }

    // ---- Each step --------------------------------------------------------------------------------------------

    /// <summary>One step of the orders: offer a new one when it is time, close the ones that are met or past their deadline, let offers lapse.</summary>
    void StepOrders()
    {
        if (_gateways.Count == 0 || Company.CampaignOver) return;
        if (!_tutorialOffered)
        {
            _tutorialOffered = true;
            OfferTutorial();
            _nextOfferTick = Tick + DrawGapTicks();
        }
        else if (Tick >= _nextOfferTick)
        {
            Offer();
            _nextOfferTick = Tick + DrawGapTicks();
        }
        for (int k = _liveOrders.Count - 1; k >= 0; k--)
        {
            var o = _liveOrders[k];
            if (o.State == OrderState.Offered)
            {
                if (Tick >= o.ExpiresAt) Close(o, OrderState.Expired);
            }
            else if (o.Progress >= o.Units - 1e-6) Close(o, OrderState.Met);
            else if (Tick >= o.Deadline) Close(o, o.Progress >= _orderTuning.LateShare * o.Units ? OrderState.Late : OrderState.Failed);
        }
    }

    long DrawGapTicks()
    {
        var gap = _orderTuning.GapOf(Phase.Id);
        int days = gap.MinDays + _orderRng.NextInt(gap.MaxDays - gap.MinDays + 1);
        // Not on a day boundary: the offer comes at some moment of the day.
        return (long)days * _ticksPerDay + _orderRng.NextInt((int)_ticksPerDay);
    }

    void Close(Order o, OrderState state)
    {
        bool wasActive = o.State == OrderState.Active;
        o.State = state;
        o.ClosedAt = Tick;
        _liveOrders.Remove(o);
        OrdersVersion++;
        if (!wasActive)
        {
            if (state == OrderState.Expired) OrderEvent(o, EventSeverity.Info, $"Offer lapsed: {o.Title}");
            return;
        }
        double share = Math.Min(1.0, o.Progress / o.Units);
        long pay = (long)Math.Round(o.Reward * share);
        float standing = o.Standing;
        string where = "";
        switch (state)
        {
            case OrderState.Met:
                Company.OrdersMet++;
                Company.Standing = Math.Clamp(Company.Standing + standing, 0f, 100f);
                if (o.Town >= 0) Satisfy(o.Town, _orderTuning.TownGain);
                where = $"Order met: {o.Title}. Reward {pay}, standing +{standing:0.#}";
                OrderEvent(o, EventSeverity.Info, where);
                break;
            case OrderState.Late:
                Company.OrdersLate++;
                Company.Standing = Math.Clamp(Company.Standing + standing * (float)share, 0f, 100f);
                where = $"Order late: {o.Title}. {share:P0} delivered, reward {pay}, standing +{standing * share:0.#}";
                OrderEvent(o, EventSeverity.Warning, where);
                break;
            default:
            {
                Company.OrdersFailed++;
                long fine = (long)Math.Round(o.Reward * _orderTuning.FailedFineShare);
                float loss = standing * _orderTuning.StandingLossFactor;
                Company.Standing = Math.Clamp(Company.Standing - loss, 0f, 100f);
                if (o.Town >= 0) Dissatisfy(o.Town, _orderTuning.TownLoss);
                if (fine > 0) Charge(fine, LedgerEntry.Fines);
                pay -= fine;
                where = $"Order failed: {o.Title}. Fine {fine}, standing -{loss:0.#}";
                OrderEvent(o, EventSeverity.Serious, where);
                break;
            }
        }
        long reward = (long)Math.Round(o.Reward * share);
        if (reward > 0) Earn(reward, LedgerEntry.Orders);
        o.Paid = pay;
    }

    void Satisfy(int town, float amount)
    {
        var t = _towns[town];
        t.Satisfaction = MathF.Min(_townTuning.MaxSatisfaction, t.Satisfaction + amount);
    }

    // ---- Progress ---------------------------------------------------------------------------------------------

    void NoteOrderExport(int cargo, int gateway, double units)
    {
        foreach (var o in _liveOrders)
        {
            if (o.State != OrderState.Active || o.Cargo != cargo) continue;
            if (o.Kind == OrderKind.StateWork || (o.Kind == OrderKind.Quota && o.Gateway == gateway)) AddProgress(o, units);
        }
    }

    void NoteOrderCivil(int cargo, int town, double units)
    {
        foreach (var o in _liveOrders)
            if (o.State == OrderState.Active && o.Kind == OrderKind.Civil && o.Cargo == cargo && o.Town == town) AddProgress(o, units);
    }

    void AddProgress(Order o, double units)
    {
        int before = (int)o.Progress;
        o.Progress += units;
        if ((int)o.Progress != before) OrdersVersion++;
    }

    // ---- Offers -----------------------------------------------------------------------------------------------

    void OfferTutorial()
    {
        var t = _orderTuning.Tutorial;
        foreach (var id in t.Cargo)
        {
            int cargo = FindCargo(_net.Tuning, id);
            if (cargo < 0 || !_exportable[cargo] || !Produced(cargo)) continue;
            var o = new Order(_orders.Count, OrderKind.Quota, "tutorial", cargo, t.Units)
            {
                Gateway = _gateways[0].Id, Days = t.Days, Reward = t.Reward, Standing = t.Standing,
                OfferedAt = Tick, ExpiresAt = Tick + (long)t.ExpiryDays * _ticksPerDay,
            };
            Publish(o);
            return;
        }
    }

    void Offer()
    {
        if (OpenOffers >= _orderTuning.MaxOpen) return;
        string phase = Phase.Id;
        var candidates = _orderTuning.Templates.Where(t => t.Weights.ContainsKey(phase) && Feasible(t)).ToList();
        Order? draft = null;
        for (int redraw = 0; redraw <= _orderTuning.RepeatRedraws && candidates.Count > 0; redraw++)
        {
            int total = candidates.Sum(t => t.Weights[phase]);
            int roll = _orderRng.NextInt(total);
            OrderTemplate? pick = null;
            foreach (var t in candidates)
            {
                roll -= t.Weights[phase];
                if (roll < 0) { pick = t; break; }
            }
            var made = Draw(pick!);
            if (made == null) { candidates.Remove(pick!); redraw--; continue; }
            draft = made;
            if (Key(made) != _lastOfferKey) break;
        }
        if (draft == null) return;
        _lastOfferKey = Key(draft);
        Publish(draft);
    }

    static string Key(Order o) => $"{o.Template}/{o.Cargo}/{o.Gateway}/{o.Town}";

    void Publish(Order o)
    {
        o.Title = Describe(o);
        _orders.Add(o);
        _liveOrders.Add(o);
        OrdersVersion++;
        OrderEvent(o, EventSeverity.Info, $"New order: {o.Title}");
    }

    /// <summary>Whether a template can be offered on this map now: something makes its cargo, a town needs it, or a state work has not been done.</summary>
    bool Feasible(OrderTemplate t)
    {
        int cargo = FindCargo(_net.Tuning, t.Cargo);
        if (cargo < 0) return false;
        switch (t.Kind)
        {
            case "quota":
                return _exportable[cargo] && Produced(cargo);
            case "civil":
                return TownsNeeding(cargo).Count > 0 && Produced(cargo);
            default:
                if (!_exportable[cargo] || _towns.Count == 0) return false; // the first work is founded near a town
                if (t.Once && _orders.Any(o => o.Template == t.Id && o.State is OrderState.Offered or OrderState.Active or OrderState.Met or OrderState.Late or OrderState.Failed)) return false;
                return _orders.All(o => o.Template != t.Id || !o.IsOpen);
        }
    }

    Order? Draw(OrderTemplate t)
    {
        int cargo = FindCargo(_net.Tuning, t.Cargo);
        int units = t.MinUnits + _orderRng.NextInt(t.MaxUnits - t.MinUnits + 1);
        int days = t.MinDays + _orderRng.NextInt(t.MaxDays - t.MinDays + 1);
        float standing = MathF.Round((t.MinStanding + _orderRng.NextFloat() * (t.MaxStanding - t.MinStanding)) * 2f) / 2f;
        var kind = t.Kind switch { "quota" => OrderKind.Quota, "civil" => OrderKind.Civil, _ => OrderKind.StateWork };
        var o = new Order(_orders.Count, kind, t.Id, cargo, units)
        {
            Days = days, Reward = units * t.RewardPerUnit, Standing = standing,
            OfferedAt = Tick, ExpiresAt = Tick + (long)_orderTuning.OfferExpiryDays * _ticksPerDay,
        };
        switch (kind)
        {
            case OrderKind.Quota:
                o.Gateway = _gateways[_orderRng.NextInt(_gateways.Count)].Id;
                break;
            case OrderKind.Civil:
                var towns = TownsNeeding(cargo);
                o.Town = towns[_orderRng.NextInt(towns.Count)];
                break;
            default:
                var works = FindWorkSites(t);
                if (works == null) return null;
                o.Works = works;
                break;
        }
        return o;
    }

    string Describe(Order o)
    {
        string cargo = _cargo[o.Cargo].Name.ToLowerInvariant();
        return o.Kind switch
        {
            OrderKind.Quota => $"deliver {o.Units} {cargo} to the {_gateways[o.Gateway].Name}",
            OrderKind.Civil => $"deliver {o.Units} {cargo} to {_towns[o.Town].Name}",
            _ => $"the state founds {string.Join(", ", o.Works.Select(w => _industryTuning.Types[w.Type].Name.ToLowerInvariant()))} and wants {o.Units} {cargo} delivered to a gateway",
        };
    }

    /// <summary>Whether some industry on the map makes a cargo.</summary>
    bool Produced(int cargo)
    {
        for (int t = 0; t < _industryOutput.Length; t++)
            if (_industryOutput[t] == cargo && _industries.Any(i => i.Type == t)) return true;
        return false;
    }

    /// <summary>Whether a station covers an industry that makes the cargo.</summary>
    bool ProducerCovered(int cargo)
    {
        foreach (var i in _industries)
            if (_industryOutput[i.Type] == cargo && _industryStations.Count > i.Id && _industryStations[i.Id].Length > 0) return true;
        return false;
    }

    string Producers(int cargo)
    {
        var names = _industries.Where(i => _industryOutput[i.Type] == cargo).Select(i => _industryTuning.Types[i.Type].Name.ToLowerInvariant()).Distinct().ToList();
        return names.Count == 0 ? "a producer" : "a " + string.Join(" or ", names);
    }

    /// <summary>Towns with a standing building that uses a cargo (for its monthly needs or for growing), by id.</summary>
    List<int> TownsNeeding(int cargo)
    {
        var found = new List<int>();
        foreach (var town in _towns)
            foreach (int id in town.Buildings)
            {
                var b = _buildings[id];
                if (b.State != BuildingState.Active) continue;
                if (_sustainNeed[(int)b.Type][b.Tier][cargo] > 0f || _constructionCap[(int)b.Type][b.Tier][cargo] > 0f) { found.Add(town.Id); break; }
            }
        return found;
    }

    // ---- State works ------------------------------------------------------------------------------------------

    /// <summary>Places the industries of a state work: the first within reach of a town, the others near it. Null when the map has no room.</summary>
    List<IndustryPlan>? FindWorkSites(OrderTemplate t)
    {
        var sites = _orderTuning.Sites;
        var plans = new List<IndustryPlan>();
        var types = t.Works.Select(w => _industryTuning.Types.ToList().FindIndex(x => x.Id == w)).ToList();
        for (int k = 0; k < types.Count; k++)
        {
            bool done = false;
            for (int attempt = 0; attempt < sites.Attempts && !done; attempt++)
            {
                float x, z;
                if (k == 0)
                {
                    var town = _towns[_orderRng.NextInt(_towns.Count)];
                    float angle = _orderRng.NextFloat() * MathF.PI * 2f, radius = 300f + _orderRng.NextFloat() * (sites.TownReach - 300f);
                    (x, z) = (town.X + MathF.Cos(angle) * radius, town.Z + MathF.Sin(angle) * radius);
                }
                else
                {
                    float angle = _orderRng.NextFloat() * MathF.PI * 2f, radius = sites.MinSpacing + _orderRng.NextFloat() * (sites.ClusterReach - sites.MinSpacing);
                    (x, z) = (plans[0].X + MathF.Cos(angle) * radius, plans[0].Z + MathF.Sin(angle) * radius);
                }
                float facing = _orderRng.NextFloat() * MathF.PI * 0.5f;
                if (!SiteFits(x, z, _industryTuning.Types[types[k]], plans)) continue;
                plans.Add(new IndustryPlan(types[k], x, z, facing));
                done = true;
            }
            if (!done) return null;
        }
        return plans;
    }

    bool SiteFits(float x, float z, IndustryTypeTuning type, List<IndustryPlan> planned)
    {
        var sites = _orderTuning.Sites;
        float reach = type.Size * 1.42f;
        foreach (var town in _towns)
        {
            float clear = (town.Organic ? town.Radius : town.Blocks * _townTuning.Generation.BlockLength * 0.5f * 1.42f) + sites.TownClearance + reach;
            if ((town.X - x) * (town.X - x) + (town.Z - z) * (town.Z - z) < clear * clear) return false;
        }
        float spacing2 = sites.MinSpacing * sites.MinSpacing;
        foreach (var i in _industries)
            if ((i.X - x) * (i.X - x) + (i.Z - z) * (i.Z - z) < spacing2) return false;
        foreach (var g in _gateways)
            if ((g.X - x) * (g.X - x) + (g.Z - z) * (g.Z - z) < spacing2) return false;
        foreach (var p in planned)
            if ((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z) < spacing2) return false;
        return IndustryGenerator.LevelAndDry(_net, _townTuning, x, z, reach, sites.MaxHeightRange);
    }

    /// <summary>The state founds a work's industries (at accept time).</summary>
    void FoundWorks(Order o)
    {
        foreach (var plan in o.Works)
        {
            float y = _net.Terrain?.HeightAt(plan.X, plan.Z) ?? 0f;
            var industry = new Industry(_industries.Count, plan.Type, plan.X, y, plan.Z, plan.Angle, _cargo.Count);
            _industries.Add(industry);
            SetAccessPoint(industry);
            StaffNewIndustry(industry);
            o.WorkIds.Add(industry.Id);
            AddEvent(SimEventKind.Order, EventSeverity.Info, plan.X, plan.Z, industry.Id, $"The state has founded a {_industryTuning.Types[plan.Type].Name.ToLowerInvariant()}");
        }
        IndustriesVersion++;
        BuildingsVersion++;
        _coverageDirty = true;
    }

    ulong OrderHash()
    {
        ulong h = (ulong)_orders.Count;
        foreach (var o in _orders)
        {
            h = h * 31 + (ulong)o.State + ((ulong)o.Kind << 8);
            h = h * 31 + BitConverter.DoubleToUInt64Bits(o.Progress);
            h = h * 31 + (ulong)(o.Deadline + 1);
            h = h * 31 + (ulong)(o.Gateway + 1) + ((ulong)(o.Town + 1) << 16) + ((ulong)o.Units << 32);
            h = h * 31 + (ulong)o.Reward;
            h = h * 31 + BitConverter.SingleToUInt32Bits(o.Standing);
        }
        h = h * 31 + (ulong)_nextOfferTick;
        h = h * 31 + (ulong)Company.OrdersMet + ((ulong)Company.OrdersLate << 16) + ((ulong)Company.OrdersFailed << 32);
        var (a, b, c, d) = _orderRng.State;
        return h ^ a ^ (b << 1) ^ (c << 2) ^ (d << 3);
    }
}
