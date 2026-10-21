namespace TransportMadness.Sim;

/// <summary>The army's requisitions (C9c): at fixed months it buys a share of the fleet at a table price, freight vehicles first.</summary>
public sealed partial class Simulation
{
    int _nextRequisition;

    /// <summary>The requisition that comes next, null when there are no more.</summary>
    public WarWave? NextRequisition => _nextRequisition < _war.Requisition.Waves.Count ? _war.Requisition.Waves[_nextRequisition] : null;

    /// <summary>A vehicle, train or parked entry the army may take: what it is, its capacity, whether it carries freight and what it cost new.</summary>
    readonly record struct Requisitionable(RepairSubject Subject, int Id, bool Freight, int Capacity, long Price);

    List<Requisitionable> RequisitionCandidates()
    {
        var all = new List<Requisitionable>();
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] < 0 || _kind[i] != VehicleKind.Car) continue;
            var model = _models[_model[i]];
            all.Add(new Requisitionable(RepairSubject.Vehicle, i, _modelCargo[_model[i]] != _passengerCargo, model.Capacity, model.Price));
        }
        foreach (var t in _trains)
        {
            if (!t.Active) continue;
            var loco = _railTuning.Locomotives[t.Locomotive];
            var wagon = _railTuning.Wagons[t.Wagon];
            all.Add(new Requisitionable(RepairSubject.Train, t.Id, _wagonCargo[t.Wagon] != _passengerCargo, t.Wagons * wagon.Capacity, loco.Price + t.Wagons * wagon.Price));
        }
        foreach (var p in _garage)
        {
            if (!p.Active) continue;
            if (p.Rail)
            {
                var wagon = _railTuning.Wagons[p.Wagon];
                all.Add(new Requisitionable(RepairSubject.Parked, p.Id, _wagonCargo[p.Wagon] != _passengerCargo, p.Wagons * wagon.Capacity, PriceOf(p)));
            }
            else all.Add(new Requisitionable(RepairSubject.Parked, p.Id, _modelCargo[p.Model] != _passengerCargo, _models[p.Model].Capacity, PriceOf(p)));
        }
        // Freight before people, then the biggest, then road before rail before the garage, then by id.
        all.Sort((a, b) =>
        {
            int c = b.Freight.CompareTo(a.Freight);
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
            AddEvent(SimEventKind.War, EventSeverity.Warning, 0f, 0f, -1,
                $"The army will requisition {wave.Share:P0} of the fleet (about {ExpectedRequisition} vehicles, freight first) at {r.PriceShare:P0} of their price in {MonthText(wave.Month)}");
    }

    void Requisition(WarWave wave)
    {
        var candidates = RequisitionCandidates();
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
        AddEvent(SimEventKind.War, EventSeverity.Serious, 0f, 0f, -1,
            take > 0 ? $"Requisition: the army has taken {take} of {candidates.Count} vehicles and trains and paid {paid}" : "Requisition: the army found nothing to take");
    }

    /// <summary>"August 1939" for a month index.</summary>
    string MonthText(long month) =>
        new DateTime(_economy.StartYear + (int)(month / _economy.MonthsPerYear), (int)(month % _economy.MonthsPerYear) + 1, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    ulong RequisitionHash() => (ulong)_nextRequisition * 31 + (ulong)Company.Requisitioned;
}
