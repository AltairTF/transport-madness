namespace TransportMadness.Sim;

/// <summary>
/// Repairs: a vehicle or train that is due unloads at its next stop, loads nothing and drives to the nearest depot able to do the repair; it
/// goes inside, waits for a bay, is repaired and comes out again on its line. The depot's level says what it can do and how many bays it has.
/// </summary>
public sealed partial class Simulation
{
    readonly List<RepairJob> _jobs = new();
    int _jobCount;

    /// <summary>The repairs waiting for a bay or under way, oldest first.</summary>
    public IReadOnlyList<RepairJob> RepairJobs => _jobs;

    public RepairJob? JobFor(RepairSubject subject, int target)
    {
        foreach (var job in _jobs)
            if (job.Subject == subject && job.Target == target) return job;
        return null;
    }

    /// <summary>Bays of a road depot.</summary>
    public int BaysOf(Depot depot) => _maint.Bays.For(depot.Level);

    /// <summary>Bays of a rail depot.</summary>
    public int BaysOf(RailDepot depot) => _maint.Bays.For(depot.Level);

    /// <summary>What a depot of this level costs to build, or to be upgraded to from a lower one (the difference).</summary>
    public long DepotPrice(bool rail, DepotLevel level) => rail ? RailDepotPrice(level) : _economy.DepotPrice.For(level);

    /// <summary>Why a depot cannot be upgraded to a level, in words for the player; null when it can.</summary>
    public string? WhyNotUpgrade(bool rail, int depot, DepotLevel level)
    {
        if (!Enum.IsDefined(level)) return "that is not a depot level";
        bool open = rail ? depot >= 0 && depot < _railDepots.Count && _railDepots[depot].Active : depot >= 0 && depot < _depots.Count && _depots[depot].Active;
        if (!open) return "that depot is closed";
        var now = rail ? _railDepots[depot].Level : _depots[depot].Level;
        if ((int)level <= (int)now) return "it is at that level already";
        return CanAfford(DepotPrice(rail, level) - DepotPrice(rail, now)) ? null : "not enough money";
    }

    int UpgradeDepot(UpgradeDepotCommand c)
    {
        if (WhyNotUpgrade(c.Rail, c.Depot, c.Level) != null) return 0;
        var now = c.Rail ? _railDepots[c.Depot].Level : _depots[c.Depot].Level;
        if (!TrySpend(DepotPrice(c.Rail, c.Level) - DepotPrice(c.Rail, now), LedgerEntry.Depots)) return 0;
        if (c.Rail) _railDepots[c.Depot].Level = c.Level;
        else
        {
            _depots[c.Depot].Level = c.Level;
            _depots[c.Depot].Crews = _crew.PerDepot.For(c.Level);
        }
        return 1;
    }

    RepairJob AddJob(bool rail, int depot, RepairSubject subject, int target, RepairKind kind)
    {
        var job = new RepairJob(_jobCount++, rail, depot, subject, target, kind, (int)Ticks(_maint.Of(kind).Seconds));
        _jobs.Add(job);
        return job;
    }

    void CancelJobsFor(RepairSubject subject, int target)
    {
        for (int k = 0; k < _jobs.Count; k++)
            if (_jobs[k].Subject == subject && _jobs[k].Target == target) _jobs.RemoveAt(k--);
    }

    /// <summary>Starts the repairs that have a bay free and works on those that are in one; finishes those that are done.</summary>
    void StepRepairs()
    {
        if (_jobs.Count == 0) return;
        var road = new int[_depots.Count];
        var rail = new int[_railDepots.Count];
        foreach (var job in _jobs)
            if (job.Working) (job.Rail ? rail : road)[job.Depot]++;
        for (int k = 0; k < _jobs.Count; k++)
        {
            var job = _jobs[k];
            bool open = job.Rail ? _railDepots[job.Depot].Active : _depots[job.Depot].Active;
            if (!open)
            {
                // The depot is gone with whatever was inside it waiting: it comes out as it went in.
                _jobs.RemoveAt(k--);
                Release(job, repaired: false);
                continue;
            }
            var counts = job.Rail ? rail : road;
            if (!job.Working)
            {
                int bays = job.Rail ? BaysOf(_railDepots[job.Depot]) : BaysOf(_depots[job.Depot]);
                if (counts[job.Depot] >= bays) continue;
                job.Working = true;
                counts[job.Depot]++;
            }
            if (--job.TicksLeft > 0) continue;
            counts[job.Depot]--;
            _jobs.RemoveAt(k--);
            Release(job, repaired: true);
        }
    }

    /// <summary>A job is over: the repair is done and paid for (or the depot was lost) and the vehicle, train or parked one is free to go.</summary>
    void Release(RepairJob job, bool repaired)
    {
        switch (job.Subject)
        {
            case RepairSubject.Vehicle:
            {
                int v = job.Target;
                if (v >= _count || !_servicing[v]) return;
                if (repaired)
                {
                    long price = RepairPrice(job.Kind, _models[_model[v]].Price, _models[_model[v]].FromYear);
                    (_condition[v], _patched[v]) = ApplyRepair(job.Kind, _condition[v], _patched[v]);
                    _lastRepair[v] = Tick;
                    PayRepair(price, _line[v]);
                    _upkeepPaid[v] += price;
                    _wantRepair[v] = -1;
                    _noDepotWarned[v] = false;
                }
                _servicing[v] = false;
                _returnDepot[v] = -1;
                _svcKind[v] = -1;
                _svcDepot[v] = -1;
                _tripDest[v] = -1;
                _nextLane[v] = RouteNextLane(v, _lane[v]);
                _transitVersion++;
                break;
            }
            case RepairSubject.Train:
            {
                var t = _trains[job.Target];
                if (!t.Active || !t.Servicing) return;
                if (repaired)
                {
                    long price = RepairPrice(job.Kind, _railTuning.Locomotives[t.Locomotive].Price + t.Wagons * _railTuning.Wagons[t.Wagon].Price, _railTuning.Locomotives[t.Locomotive].FromYear);
                    (t.Condition, t.Patched) = ApplyRepair(job.Kind, t.Condition, t.Patched);
                    t.LastRepairTick = Tick;
                    PayRepair(price, t.Line);
                    t.UpkeepPaid += price;
                    t.WantRepair = -1;
                    t.NoDepotWarned = false;
                }
                LeaveRailDepot(t, _railDepots[job.Depot]);
                break;
            }
            default:
            {
                var entry = _garage[job.Target];
                if (!entry.Active || !repaired) return;
                long price = RepairPrice(job.Kind, PriceOf(entry), FromYearOf(entry));
                (entry.Condition, entry.Patched) = ApplyRepair(job.Kind, entry.Condition, entry.Patched);
                entry.LastRepairTick = Tick;
                Charge(price, LedgerEntry.Maintenance);
                break;
            }
        }
    }

    (float, bool) ApplyRepair(RepairKind kind, float condition, bool patched)
    {
        var (after, mark) = After(kind, condition);
        return (after, kind == RepairKind.Patch ? mark || patched : false);
    }

    void PayRepair(long price, int line)
    {
        Charge(price, LedgerEntry.Maintenance);
        _lines[line].Upkeep += price;
    }

    // ---- Road vehicles --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A vehicle has reached a stop. When it is due for a repair and a depot can do it, it takes nothing more on: what it carries is put
    /// down here, and it will go to the depot when it leaves. Returns whether it is going.
    /// </summary>
    bool DecideService(int i)
    {
        if (DueRepair(i) is not { } kind) return false;
        int depot = ServiceDepotFor(i, kind);
        if (depot < 0)
        {
            if (!_noDepotWarned[i])
            {
                _noDepotWarned[i] = true;
                var (x, z, _) = _net.LanePoint(new LaneId(_lane[i]), _pos[i]);
                AddEvent(SimEventKind.NoDepot, EventSeverity.Warning, x, z, i,
                    $"{_models[_model[i]].Name} on {_lines[_line[i]].Name} is due for a {KindName(kind)} but no depot it can reach can do it");
            }
            return false;
        }
        _svcKind[i] = (sbyte)kind;
        _svcDepot[i] = depot;
        return true;
    }

    /// <summary>The nearest depot, by driving time from the vehicle, that can do the repair; -1 when none can.</summary>
    int ServiceDepotFor(int vehicle, RepairKind kind)
    {
        int lane = _lane[vehicle];
        int best = -1;
        float bestCost = float.PositiveInfinity;
        foreach (var depot in _depots)
        {
            if (!depot.Active || !CanRepair(depot.Level, kind)) continue;
            float cost = FieldToNode(depot.Node)[lane];
            if (float.IsPositiveInfinity(cost) || cost >= bestCost) continue;
            best = depot.Id;
            bestCost = cost;
        }
        return best;
    }

    /// <summary>The vehicle leaves its stop for the depot chosen for it.</summary>
    void StartServiceTrip(int i)
    {
        int depot = _svcDepot[i];
        if (depot < 0 || depot >= _depots.Count || !_depots[depot].Active)
        {
            _svcKind[i] = -1;
            _svcDepot[i] = -1;
            return;
        }
        _returnDepot[i] = depot;
        _tripDest[i] = _depots[depot].Node;
        _transitVersion++;
        int lane = _lane[i];
        if (!_bay[i] && _pos[i] < _laneZoneStart[lane]) _nextLane[i] = RouteTripLane(i, lane);
    }

    /// <summary>A vehicle has reached the depot it was sent to: it goes inside, out of the traffic, and waits for a bay.</summary>
    void EnterServiceBay(int i)
    {
        var depot = _depots[_returnDepot[i]];
        var kind = (RepairKind)_svcKind[i];
        var station = TargetStation(i);
        var field = station is { Active: true } ? FieldTo(station) : null;
        int lane = depot.Active && CanRepair(depot.Level, kind) ? ChooseExitLane(depot.Node, _length[i], field, out _) : -1;
        if (lane < 0 && depot.Active && CanRepair(depot.Level, kind)) lane = ChooseExitLane(depot.Node, _length[i], null, out _);
        if (lane < 0)
        {
            // The depot cannot take it after all (it was closed or changed on the way): it waits in the garage instead.
            _svcKind[i] = -1;
            _svcDepot[i] = -1;
            ParkAtDepot(i);
            return;
        }
        float position = _laneReach[lane] + _length[i] + SpawnGap;
        _lane[i] = lane;
        _pos[i] = position;
        _speed[i] = 0f;
        _accel[i] = 0f;
        _phase[i] = 0;
        _lat[i] = 0f;
        _shift[i] = 0f;
        _target[i] = -1;
        _aborting[i] = false;
        _prevLane[i] = -1;
        _ghost[i] = true;
        _ghostOut[i] = false;
        _ghostFrom[i] = position;
        _ghostNode[i] = depot.Node;
        _servicing[i] = true;
        if (_nextStart[lane] < _nextStart[lane + 1]) _nextLane[i] = _nextLanes[_nextStart[lane]];
        AddJob(false, depot.Id, RepairSubject.Vehicle, i, kind);
        RebuildOrder();
    }

    // ---- Trains ---------------------------------------------------------------------------------------------------------------

    /// <summary>A train has reached a stop and is due for a repair that a rail depot it can reach can do: it takes nothing more on. Returns whether it is going.</summary>
    bool DecideTrainService(Train t)
    {
        if (DueRepair(t) is not { } kind) return false;
        int depot = NearestRailDepotFor(t, kind);
        if (depot < 0)
        {
            if (!t.NoDepotWarned)
            {
                t.NoDepotWarned = true;
                var (x, z) = TrainPoint(t);
                AddEvent(SimEventKind.NoDepot, EventSeverity.Warning, x, z, t.Id,
                    $"A train on {_lines[t.Line].Name} is due for a {KindName(kind)} but no rail depot it can reach can do it");
            }
            return false;
        }
        t.SvcKind = (sbyte)kind;
        t.SvcDepot = depot;
        return true;
    }

    /// <summary>A train is leaving its stop for the depot chosen for it; it plans the way there instead of to its next station.</summary>
    void StartTrainServiceTrip(Train t)
    {
        if (t.SvcDepot < 0 || t.SvcDepot >= _railDepots.Count || !_railDepots[t.SvcDepot].Active)
        {
            t.SvcKind = -1;
            t.SvcDepot = -1;
            return;
        }
        t.ReturnDepot = t.SvcDepot;
        _transitVersion++;
    }

    /// <summary>A train has stopped at the depot it was sent to: it leaves the track and waits inside for a bay.</summary>
    void EnterTrainService(Train t)
    {
        var depot = _railDepots[t.ReturnDepot];
        var kind = (RepairKind)t.SvcKind;
        if (!depot.Active || !CanRepair(depot.Level, kind))
        {
            t.SvcKind = -1;
            t.SvcDepot = -1;
            ParkTrainAtDepot(t);
            return;
        }
        for (int h = t.Holds.Count - 1; h >= 0; h--) ReleaseHold(t, h);
        if (t.Riders != null)
        {
            foreach (var lot in t.Riders) GiveUp(_groups[lot.Group], lot.Count, lot.From);
            t.Riders = null;
        }
        t.Steps.Clear();
        t.Head = 0f;
        t.Approved = 0f;
        t.StopAt = float.PositiveInfinity;
        t.Speed = 0f;
        t.Acceleration = 0f;
        t.Load = 0;
        t.LoadCargo = -1;
        t.LoadFrom = -1;
        t.Dwell = 0;
        t.SpawnNode = depot.Node;
        t.Servicing = true;
        t.Status = TrainStatus.Servicing;
        _transitVersion++;
        AddJob(true, depot.Id, RepairSubject.Train, t.Id, kind);
    }

    /// <summary>The repair is over: the train comes out of the depot onto its line, to the stop it was to go to next.</summary>
    void LeaveRailDepot(Train t, RailDepot depot)
    {
        t.Servicing = false;
        t.ReturnDepot = -1;
        t.SvcKind = -1;
        t.SvcDepot = -1;
        t.SpawnNode = depot.Node;
        t.ReplanTick = 0;
        t.InDepot = true;
        t.Status = TrainStatus.InDepot;
        var target = _stations[_lines[t.Line].Stops[t.StopIndex]];
        // A depot on the station itself starts the train there, as a purchase does.
        if (target.Active && target.RailNode == depot.Node) t.StopAt = 0f;
        _transitVersion++;
    }

    /// <summary>A train that broke down has been cleared by the crew: it is towed to the nearest rail depot and waits in its garage.</summary>
    void TowTrain(Train t)
    {
        t.Broken = false;
        if (t.ReturnDepot >= 0) return;
        if (WhyNotParkTrain(t.Id) == null && ParkTrain(t.Id) == 1)
        {
            t.Towed = true;
            return;
        }
        var (x, z) = TrainPoint(t);
        AddEvent(SimEventKind.NoDepot, EventSeverity.Warning, x, z, t.Id, $"A train on {_lines[t.Line].Name} broke down and no rail depot can take it");
    }

    // ---- After a breakdown ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A company vehicle whose wreck the crew has cleared is not lost: it goes to the garage of the nearest depot with the Condition it broke
    /// down with. Without a depot it is gone, as other wrecks are.
    /// </summary>
    bool TowToGarage(int vehicle)
    {
        int depot = NearestDepotFor(vehicle);
        if (depot < 0)
        {
            var (x, z, _) = _net.LanePoint(new LaneId(_lane[vehicle]), _pos[vehicle]);
            float best = float.PositiveInfinity;
            foreach (var d in _depots)
            {
                if (!d.Active) continue;
                var (dx, dz) = _net.GetNode(d.Node);
                float distance = (dx - x) * (dx - x) + (dz - z) * (dz - z);
                if (distance >= best) continue;
                best = distance;
                depot = d.Id;
            }
        }
        if (depot < 0) return false;
        var entry = new ParkedVehicle(_garage.Count, false, depot)
        {
            Model = _model[vehicle],
            Condition = _condition[vehicle],
            LastRepairTick = _lastRepair[vehicle],
            Patched = _patched[vehicle],
        };
        RemoveVehicle(vehicle);
        AddToGarage(entry);
        return true;
    }

    /// <summary>
    /// At the turn of a day, the vehicles and trains waiting in a depot get the company's policy: one that is due goes into a bay of its depot when
    /// the depot can do the repair (a vehicle towed in after a breakdown is mended this way). One the depot cannot repair waits for the player.
    /// </summary>
    void CheckGarage()
    {
        if (_companyPolicy.Mode != PolicyMode.On) return;
        foreach (var entry in _garage)
        {
            if (!entry.Active || JobFor(RepairSubject.Parked, entry.Id) != null) continue;
            if (DueBy(_companyPolicy, entry.Condition, entry.LastRepairTick) is not { } kind) continue;
            bool open = entry.Rail ? _railDepots[entry.Depot].Active : _depots[entry.Depot].Active;
            var level = entry.Rail ? _railDepots[entry.Depot].Level : _depots[entry.Depot].Level;
            if (open && CanRepair(level, kind)) AddJob(entry.Rail, entry.Depot, RepairSubject.Parked, entry.Id, kind);
        }
    }

    ulong RepairHash()
    {
        ulong h = (ulong)_jobCount;
        foreach (var job in _jobs)
        {
            h = h * 31 + (ulong)job.Id;
            h = h * 31 + (ulong)job.Depot + (job.Rail ? 1UL << 40 : 0UL);
            h = h * 31 + (ulong)job.Subject * 1000003UL + (ulong)job.Target;
            h = h * 31 + (ulong)job.Kind;
            h = h * 31 + (ulong)job.TicksLeft + (job.Working ? 1UL << 41 : 0UL);
        }
        return h;
    }
}
