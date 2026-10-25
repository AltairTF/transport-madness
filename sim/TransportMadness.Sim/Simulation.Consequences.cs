namespace TransportMadness.Sim;

/// <summary>What the company pays for incidents: compensation to the injured, fines for negligence, and the goodwill of the town they happen in.</summary>
public sealed partial class Simulation
{
    /// <summary>The town a point is inside (its radius plus a margin), -1 when none.</summary>
    internal int TownAt(float x, float z)
    {
        int best = -1;
        float bestDistance = float.PositiveInfinity;
        foreach (var town in _towns)
        {
            float reach = town.Radius + _maint.Consequences.TownMargin;
            float d = MathF.Sqrt((town.X - x) * (town.X - x) + (town.Z - z) * (town.Z - z));
            if (d > reach || d >= bestDistance) continue;
            best = town.Id;
            bestDistance = d;
        }
        return best;
    }

    /// <summary>People hurt out of the passengers aboard, paid for at once. Returns how many.</summary>
    int Compensate(int passengers, out long paid)
    {
        int injured = (int)MathF.Round(passengers * _maint.Consequences.InjuredShare);
        paid = injured * _maint.Consequences.CompensationPerInjured;
        if (injured <= 0) return 0;
        Charge(paid, LedgerEntry.Compensation);
        Company.Injured += injured;
        return injured;
    }

    /// <summary>A town is unhappy that the company's vehicle had an incident in it (once for the incident).</summary>
    void TownMindsIncident(Incident incident, float x, float z)
    {
        incident.CompanyInvolved = true;
        if (incident.TownPenalised) return;
        incident.TownPenalised = true;
        int town = TownAt(x, z);
        if (town >= 0) Dissatisfy(town, _maint.Consequences.TownIncidentPenalty);
    }

    /// <summary>A company vehicle was wrecked in a collision: the people aboard are paid for, the town minds.</summary>
    void OnCompanyCrash(int vehicle, int incidentId)
    {
        var incident = GetIncident(incidentId);
        int passengers = _load[vehicle] > 0 && _loadCargo[vehicle] == _passengerCargo ? _load[vehicle] : 0;
        int injured = Compensate(passengers, out long paid);
        var (x, z, _) = _net.LanePoint(new LaneId(_lane[vehicle]), _pos[vehicle]);
        string text = $"{_models[_model[vehicle]].Name} on {_lines[_line[vehicle]].Name} crashed" + (injured > 0 ? $": {injured} injured, {paid:N0} paid in compensation" : "");
        AddEvent(SimEventKind.Crash, EventSeverity.Serious, x, z, vehicle, text);
        TownMindsIncident(incident, x, z);
    }

    /// <summary>A train hit something at a level crossing: the people aboard are paid for.</summary>
    void OnTrainHit(Train train, int incidentId)
    {
        int passengers = train.Load > 0 && train.LoadCargo == _passengerCargo ? train.Load : 0;
        int injured = Compensate(passengers, out long paid);
        var (x, z) = TrainPoint(train);
        AddEvent(SimEventKind.Crash, EventSeverity.Serious, x, z, train.Id,
            $"A train on {_lines[train.Line].Name} hit a vehicle at a level crossing" + (injured > 0 ? $": {injured} injured, {paid:N0} paid in compensation" : ""));
        TownMindsIncident(GetIncident(incidentId), x, z);
    }

    /// <summary>Incidents of the company's that have blocked a road for long make the town they are in unhappy once more.</summary>
    void CheckBlockedIncidents()
    {
        long limit = Ticks(_maint.Consequences.BlockedSeconds);
        foreach (int id in _openIncidents)
        {
            var incident = GetIncident(id);
            if (!incident.CompanyInvolved || incident.BlockedPenalised || Tick - incident.StartTick < limit) continue;
            incident.BlockedPenalised = true;
            var (x, z) = IncidentPoint(incident);
            int town = TownAt(x, z);
            if (town < 0) continue;
            Dissatisfy(town, _maint.Consequences.TownBlockedPenalty);
            AddEvent(SimEventKind.TownUnhappy, EventSeverity.Warning, x, z, town, $"{_towns[town].Name} is unhappy: a road has been blocked for over an hour");
        }
    }
}
