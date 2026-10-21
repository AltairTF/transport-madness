namespace TransportMadness.Sim;

/// <summary>
/// The order in which the army takes vehicles (C15b): which of the fleet go first. Whatever the rule, vehicles equal by it are taken in the order of the default rule,
/// so the choice is always complete and the same for the same fleet.
/// </summary>
public enum RequisitionRule : byte
{
    /// <summary>Freight vehicles before passenger ones, then the biggest by capacity (the rule of C9c and the default).</summary>
    FreightFirst,
    /// <summary>The oldest model first (the year it came on sale), then as the default.</summary>
    OldestFirst,
    /// <summary>Passenger vehicles before freight ones, then the biggest by capacity.</summary>
    PassengersFirst,
}

/// <summary>The army's requisitions (C9c): at fixed months it buys a share of the fleet at a table price; the player chooses the order in the month of notice (C15b).</summary>
public sealed partial class Simulation
{
    int _nextRequisition;
    RequisitionRule _requisitionRule;

    /// <summary>The requisition that comes next, null when there are no more.</summary>
    public WarWave? NextRequisition => _nextRequisition < _war.Requisition.Waves.Count ? _war.Requisition.Waves[_nextRequisition] : null;

    /// <summary>The rule by which the next wave will pick, the default until the player chooses in the month of notice; it goes back to the default after each wave.</summary>
    public RequisitionRule RequisitionRule => _requisitionRule;

    /// <summary>Whether the rule can be chosen now: a wave is announced (its month of notice has begun) and has not yet come.</summary>
    public bool CanChooseRequisitionRule =>
        NextRequisition is { } wave && _war.Requisition.NoticeMonths > 0 && MonthIndex >= wave.Month - _war.Requisition.NoticeMonths && MonthIndex < wave.Month;

    /// <summary>Chooses the rule for the announced wave. Result 1 (also when it is the rule already set), 0 when no wave is announced or the rule is unknown.</summary>
    int SetRequisitionRule(RequisitionRule rule)
    {
        if (!CanChooseRequisitionRule || !Enum.IsDefined(rule)) return 0;
        _requisitionRule = rule;
        return 1;
    }

    /// <summary>The words for a rule in the notice and the Company window.</summary>
    public static string RuleText(RequisitionRule rule) => rule switch
    {
        RequisitionRule.OldestFirst => "the oldest first",
        RequisitionRule.PassengersFirst => "passengers first",
        _ => "freight first, the biggest first",
    };

    /// <summary>A vehicle, train or parked entry the army may take: what it is, its capacity, whether it carries freight, what it cost new and the year its model came on sale.</summary>
    readonly record struct Requisitionable(RepairSubject Subject, int Id, bool Freight, int Capacity, long Price, int Year);

    List<Requisitionable> RequisitionCandidates()
    {
        var all = new List<Requisitionable>();
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] < 0 || _kind[i] != VehicleKind.Car) continue;
            var model = _models[_model[i]];
            all.Add(new Requisitionable(RepairSubject.Vehicle, i, _modelCargo[_model[i]] != _passengerCargo, model.Capacity, model.Price, model.FromYear));
        }
        foreach (var t in _trains)
        {
            if (!t.Active) continue;
            var loco = _railTuning.Locomotives[t.Locomotive];
            var wagon = _railTuning.Wagons[t.Wagon];
            all.Add(new Requisitionable(RepairSubject.Train, t.Id, _wagonCargo[t.Wagon] != _passengerCargo, t.Wagons * wagon.Capacity, loco.Price + t.Wagons * wagon.Price, loco.FromYear));
        }
        foreach (var p in _garage)
        {
            if (!p.Active) continue;
            if (p.Rail)
            {
                var wagon = _railTuning.Wagons[p.Wagon];
                all.Add(new Requisitionable(RepairSubject.Parked, p.Id, _wagonCargo[p.Wagon] != _passengerCargo, p.Wagons * wagon.Capacity, PriceOf(p), _railTuning.Locomotives[p.Locomotive].FromYear));
            }
            else all.Add(new Requisitionable(RepairSubject.Parked, p.Id, _modelCargo[p.Model] != _passengerCargo, _models[p.Model].Capacity, PriceOf(p), _models[p.Model].FromYear));
        }
        // By the rule: freight before people (or people before freight, or the oldest model first), then the biggest, then road before rail before the garage, then by id.
        var rule = _requisitionRule;
        all.Sort((a, b) =>
        {
            int c = rule == RequisitionRule.OldestFirst ? a.Year.CompareTo(b.Year) : rule == RequisitionRule.PassengersFirst ? a.Freight.CompareTo(b.Freight) : b.Freight.CompareTo(a.Freight);
            if (c == 0 && rule == RequisitionRule.OldestFirst) c = b.Freight.CompareTo(a.Freight);
            if (c == 0) c = b.Capacity.CompareTo(a.Capacity);
            if (c == 0) c = a.Subject.CompareTo(b.Subject);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        return all;
    }

    /// <summary>How many vehicles and trains the next requisition would take from the fleet as it is now.</summary>
    public int ExpectedRequisition => NextRequisition is { } wave ? (int)MathF.Round(RequisitionCandidates().Count * wave.Share) : 0;

    /// <summary>At the turn of a month: the notice a month ahead, and the requisition itself when its month has come.</summary>
    void RequisitionForMonth(long nextMonth)
    {
        var r = _war.Requisition;
        while (_nextRequisition < r.Waves.Count && r.Waves[_nextRequisition].Month <= nextMonth)
            Requisition(r.Waves[_nextRequisition++]);
        if (NextRequisition is { } wave && wave.Month - r.NoticeMonths == nextMonth && r.NoticeMonths > 0)
            AddEvent(SimEventKind.War, EventSeverity.Warning, -1,
                $"The army will requisition {wave.Share:P0} of the fleet (about {ExpectedRequisition} vehicles, {RuleText(_requisitionRule)}) at {r.PriceShare:P0} of their price in {MonthText(wave.Month)}; you can choose the order until then");
    }

    void Requisition(WarWave wave)
    {
        var candidates = RequisitionCandidates();
        _requisitionRule = RequisitionRule.FreightFirst; // the choice was for this wave only
        int take = (int)MathF.Round(candidates.Count * wave.Share);
        long paid = 0;
        for (int k = 0; k < take; k++)
        {
            var c = candidates[k];
            switch (c.Subject)
            {
                case RepairSubject.Vehicle:
                    RemoveVehicle(c.Id);
                    break;
                case RepairSubject.Train:
                    RemoveTrain(_trains[c.Id], refund: false);
                    break;
                default:
                    CancelJobsFor(RepairSubject.Parked, c.Id);
                    _garage[c.Id].Active = false;
                    break;
            }
            paid += (long)MathF.Round(c.Price * _war.Requisition.PriceShare);
        }
        if (take > 0)
        {
            RebuildOrder();
            _transitVersion++;
            Earn(paid, LedgerEntry.Requisition);
            Company.Requisitioned += take;
            Company.Standing = Math.Clamp(Company.Standing + _war.Requisition.Standing, 0f, 100f);
        }
        AddEvent(SimEventKind.War, EventSeverity.Serious, -1,
            take > 0 ? $"Requisition: the army has taken {take} of {candidates.Count} vehicles and trains and paid {paid}" : "Requisition: the army found nothing to take");
    }

    /// <summary>"August 1939" for a month index.</summary>
    string MonthText(long month) =>
        new DateTime(_economy.StartYear + (int)(month / _economy.MonthsPerYear), (int)(month % _economy.MonthsPerYear) + 1, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    ulong RequisitionHash() => (ulong)_nextRequisition * 31 + (ulong)Company.Requisitioned + (ulong)_requisitionRule * 1000003UL;
}
