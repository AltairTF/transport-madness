namespace TransportMadness.Sim;

/// <summary>
/// Workers (C7): the big industries have jobs and make their output times their staffing. The people's own movement staffs part of them; the
/// rest are carried by the company's workers' buses from stations beside homes to a station beside the industry, and leave again at a rate.
/// </summary>
public sealed partial class Simulation
{
    readonly WorkersTuning _workers;
    readonly int _workerCargo; // index into the cargo list, -1 when the roster has none
    float _staffKeepPerTick => 1f - _workers.StaffLossPerDay / _ticksPerDay;

    public WorkersTuning WorkersTuning => _workers;
    /// <summary>The cargo workers are carried as (index into the cargo list).</summary>
    public int WorkerCargo => _workerCargo;

    /// <summary>
    /// How fully an industry is staffed, 0 to 1: its own staff plus the workers delivered, over its jobs. 1 for an industry without jobs
    /// and on a map without towns, where there is nobody to work.
    /// </summary>
    public float StaffingOf(Industry industry)
    {
        int jobs = _industryTuning.Types[industry.Type].Jobs;
        if (jobs <= 0 || _towns.Count == 0 || _workerCargo < 0) return 1f;
        return MathF.Min(1f, industry.OwnStaff * (1f - _workforceGap) + industry.Input[_workerCargo] / jobs);
    }

    /// <summary>Workers delivered and still at work in an industry.</summary>
    public float StaffOf(Industry industry) => _workerCargo < 0 ? 0f : industry.Input[_workerCargo];

    /// <summary>Sets the staff an industry has by the people's own movement, from its distance to the nearest town (called when it is founded).</summary>
    void SetOwnStaff(Industry industry)
    {
        if (_towns.Count == 0) return;
        float best = float.MaxValue;
        foreach (var town in _towns)
            best = MathF.Min(best, MathF.Sqrt((town.X - industry.X) * (town.X - industry.X) + (town.Z - industry.Z) * (town.Z - industry.Z)));
        industry.OwnStaff = _workers.OwnShare * MathF.Max(0f, 1f - best / _workers.OwnReach);
    }

    /// <summary>
    /// At the turn of a day, every road station beside homes gets workers to wait for a ride, up to the share of the residents it covers, when an
    /// industry with jobs within reach is not fully staffed.
    /// </summary>
    void SendWorkers()
    {
        if (_workerCargo < 0 || _towns.Count == 0 || _stations.Count == 0) return;
        var short_ = _industries.Where(i => _industryTuning.Types[i.Type].Jobs > 0 && StaffingOf(i) < 1f).ToList();
        if (short_.Count == 0) return;
        if (_coverageDirty || _stationBuildings.Count != _stations.Count) RefreshCoverage();
        float reach2 = _workers.Reach * _workers.Reach, limit = _cargo[_workerCargo].MaxWaiting;
        foreach (var station in _stations)
        {
            if (!station.Active || station.Mode != StationMode.Road) continue;
            int people = 0;
            foreach (int b in _stationBuildings[station.Id])
                if (_buildings[b].Type == BuildingType.Home) people += _townTuning.Of(_buildings[b].Type, _buildings[b].Tier).People;
            if (people == 0) continue;
            var (x, z) = PointOf(station);
            if (!short_.Any(i => (i.X - x) * (i.X - x) + (i.Z - z) * (i.Z - z) <= reach2)) continue;
            float target = MathF.Min(limit, people * _workers.HomeShare * (1f - _workforceGap));
            if (station.Waiting[_workerCargo] < target)
                station.Waiting[_workerCargo] = MathF.Min(target, station.Waiting[_workerCargo] + target * _workers.RefillPerDay);
        }
    }
}
