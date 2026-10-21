namespace TransportMadness.Sim;

/// <summary>The three repairs a depot can do (see <see cref="MaintenanceTuning"/>); which it can do depends on its level.</summary>
public enum RepairKind : byte { Patch, Service, Overhaul }

/// <summary>Whether a policy decides (<see cref="On"/>), switches maintenance off (<see cref="Off"/>) or leaves the choice to the level above (<see cref="Inherit"/>).</summary>
public enum PolicyMode : byte { Inherit, Off, On }

/// <summary>Where a maintenance policy is set: the company, a line, one road vehicle or one train. The most specific one that does not inherit wins.</summary>
public enum PolicyScope : byte { Company, Line, Vehicle, Train }

/// <summary>What a repair is done to.</summary>
public enum RepairSubject : byte { Vehicle, Train, Parked }

/// <summary>How often the random events happen (breakdowns, signal failures, later the campaign's); the accidents that come out of the driving are not scaled.</summary>
public enum IncidentFrequency : byte { Off, Low, Normal, High }

/// <summary>Where a vehicle or a train is in getting a repair.</summary>
public enum RepairPhase : byte
{
    None,
    /// <summary>Asked for by the player or due by policy, to be done at the next stop.</summary>
    Requested,
    /// <summary>Driving to the depot that will do it.</summary>
    ToDepot,
    /// <summary>In the depot, waiting for a bay.</summary>
    Waiting,
    /// <summary>In a bay.</summary>
    Working,
}

/// <summary>
/// When a vehicle is due for a repair and which: after <see cref="EveryDays"/> days since the last one (0: not by days) and/or when its
/// Condition is below <see cref="BelowCondition"/> (0: not by Condition).
/// </summary>
public readonly record struct MaintenancePolicy(PolicyMode Mode, int EveryDays, int BelowCondition, RepairKind Kind)
{
    /// <summary>Leave it to the level above.</summary>
    public static MaintenancePolicy Inherit => default;
    /// <summary>No maintenance: the risk is the player's.</summary>
    public static MaintenancePolicy Off => new(PolicyMode.Off, 0, 0, RepairKind.Patch);
    public static MaintenancePolicy When(int everyDays, int belowCondition, RepairKind kind) => new(PolicyMode.On, everyDays, belowCondition, kind);

    internal MaintenancePolicy Normalised => Mode == PolicyMode.On ? this : this with { EveryDays = 0, BelowCondition = 0, Kind = RepairKind.Patch };
    internal ulong Bits => (ulong)Mode | ((ulong)Kind << 2) | ((ulong)EveryDays << 8) | ((ulong)BelowCondition << 32);
}

/// <summary>A repair under way or waiting for a bay in a depot.</summary>
public sealed class RepairJob
{
    internal RepairJob(int id, bool rail, int depot, RepairSubject subject, int target, RepairKind kind, int ticks)
    {
        Id = id;
        Rail = rail;
        Depot = depot;
        Subject = subject;
        Target = target;
        Kind = kind;
        TicksLeft = ticks;
    }

    public int Id { get; }
    /// <summary>In a rail depot, else a road depot.</summary>
    public bool Rail { get; }
    public int Depot { get; }
    public RepairSubject Subject { get; }
    /// <summary>The vehicle slot, the train id or the garage id.</summary>
    public int Target { get; }
    public RepairKind Kind { get; }
    /// <summary>In a bay; false while it waits for one.</summary>
    public bool Working { get; internal set; }
    /// <summary>Ticks of work left (the whole time while it still waits).</summary>
    public int TicksLeft { get; internal set; }
}

/// <summary>Condition, wear and breakdowns of the company's fleet, and the maintenance policy that says when it is repaired (ADR 0005).</summary>
public sealed partial class Simulation
{
    readonly MaintenanceTuning _maint;
    MaintenancePolicy _companyPolicy;
    IncidentFrequency _frequency;

    // Per vehicle slot. Only the company's vehicles use them; ambient cars have no Condition.
    float[] _condition = FilledWith(64, 100f);
    long[] _lastRepair = new long[64];            // tick of the last repair, or of the purchase
    bool[] _patched = new bool[64];               // patched since the last Service or Overhaul: wears faster
    MaintenancePolicy[] _policy = new MaintenancePolicy[64];
    sbyte[] _wantRepair = FilledWith(64, (sbyte)-1); // a repair the player asked for, done at the next stop; -1 for none
    sbyte[] _svcKind = FilledWith(64, (sbyte)-1);    // the repair it is going to a depot for (decided at its stop, started when it leaves it); -1 for none
    int[] _svcDepot = FilledWith(64, -1);            // the depot chosen for it
    bool[] _servicing = new bool[64];                // inside the depot, waiting for a bay or in one
    bool[] _noDepotWarned = new bool[64];            // the game has said that no depot can do the repair it is due for

    static T[] FilledWith<T>(int length, T value)
    {
        var array = new T[length];
        Array.Fill(array, value);
        return array;
    }

    /// <summary>The option for random events; change it with <see cref="SetFrequencyCommand"/>.</summary>
    public IncidentFrequency Frequency => _frequency;

    /// <summary>The company-wide maintenance policy; the most general level.</summary>
    public MaintenancePolicy CompanyPolicy => _companyPolicy;

    void GrowConditionArrays(int n)
    {
        int old = _condition.Length;
        Array.Resize(ref _condition, n);
        Array.Resize(ref _lastRepair, n);
        Array.Resize(ref _patched, n);
        Array.Resize(ref _policy, n);
        Array.Resize(ref _wantRepair, n);
        Array.Resize(ref _svcKind, n);
        Array.Resize(ref _svcDepot, n);
        Array.Resize(ref _servicing, n);
        Array.Resize(ref _noDepotWarned, n);
        for (int k = old; k < n; k++)
        {
            _condition[k] = 100f;
            _wantRepair[k] = -1;
            _svcKind[k] = -1;
            _svcDepot[k] = -1;
        }
    }

    void ResetConditionSlot(int id)
    {
        _condition[id] = 100f;
        _lastRepair[id] = Tick;
        _patched[id] = false;
        _policy[id] = default;
        _wantRepair[id] = -1;
        _svcKind[id] = -1;
        _svcDepot[id] = -1;
        _servicing[id] = false;
        _noDepotWarned[id] = false;
    }

    /// <summary>Sets a company vehicle's Condition (for tests that need a worn one without driving it for days).</summary>
    internal void SetCondition(int vehicle, float condition) => _condition[vehicle] = condition;

    /// <summary>Gives a company vehicle that has just been put on the road the state of the one it was (a parked vehicle taken out of the garage) or of a new one.</summary>
    void SetVehicleCondition(int id, float condition, long lastRepair, bool patched)
    {
        _condition[id] = condition;
        _lastRepair[id] = lastRepair;
        _patched[id] = patched;
    }

    // ---- Wear and breakdowns --------------------------------------------------------------------------------------------------

    /// <summary>Breakdowns per metre at this Condition: none at or above <c>noRiskAbove</c>, rising smoothly to <c>hazardPerKmAtFloor</c> at <c>highRiskBelow</c> and faster below it.</summary>
    internal float HazardPerMetre(float condition)
    {
        if (condition >= _maint.NoRiskAbove) return 0f;
        float perKm;
        if (condition >= _maint.HighRiskBelow)
        {
            float s = (_maint.NoRiskAbove - condition) / (_maint.NoRiskAbove - _maint.HighRiskBelow);
            perKm = _maint.HazardPerKmAtFloor * s * s;
        }
        else
            perKm = _maint.HazardPerKmAtFloor + (_maint.HazardPerKmAtZero - _maint.HazardPerKmAtFloor) * (_maint.HighRiskBelow - condition) / _maint.HighRiskBelow;
        return perKm / 1000f;
    }

    /// <summary>The share of full braking a vehicle of this Condition has: it fades below <c>brakeFadeBelow</c>.</summary>
    internal float BrakeShare(float condition) =>
        condition >= _maint.BrakeFadeBelow ? 1f : _maint.WorstBrakeShare + (1f - _maint.WorstBrakeShare) * (condition / _maint.BrakeFadeBelow);

    internal float WearOf(float distance, float loadShare, bool patched) =>
        distance / _maint.MetresPerPoint * (1f + _maint.LoadedWear * loadShare) * (patched ? _maint.PatchedWearFactor : 1f);

    /// <summary>Wears the company's vehicles and trains by what they drove this step and draws the breakdowns, vehicles first and each in id order.</summary>
    void StepCondition()
    {
        float scale = _frequencyScale;
        WearRoads();
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] < 0 || _kind[i] != VehicleKind.Car || _ghost[i]) continue;
            float distance = _speed[i] * StepSeconds;
            if (distance <= 0f) continue;
            _condition[i] = MathF.Max(0f, _condition[i] - WearOf(distance, (float)_load[i] / _models[_model[i]].Capacity, _patched[i]));
            if (scale > 0f && _condition[i] < _maint.NoRiskAbove && _rng.NextFloat() < HazardPerMetre(_condition[i]) * RoadRiskFactor(_lane[i]) * distance * scale)
                BreakDown(i);
        }
        foreach (var t in _trains)
        {
            if (!t.Active || t.InDepot || t.Servicing || t.Speed <= 0f) continue;
            float distance = t.Speed * StepSeconds;
            t.Condition = MathF.Max(0f, t.Condition - WearOf(distance, t.Capacity > 0 ? (float)t.Load / t.Capacity : 0f, t.Patched));
            if (scale > 0f && !t.Towed && t.Condition < _maint.NoRiskAbove && _rng.NextFloat() < HazardPerMetre(t.Condition) * distance * scale)
                BreakDownTrain(t);
        }
    }

    float _frequencyScale;

    /// <summary>A company vehicle breaks down where it is: a wreck that blocks its lane until a crew has cleared it, after which it goes to the garage of the nearest depot.</summary>
    void BreakDown(int vehicle)
    {
        bool negligent = _condition[vehicle] < _maint.CriticalCondition;
        _condition[vehicle] = _maint.WreckCondition;
        int incident = NewIncident(IncidentKind.Breakdown, vehicle);
        GetIncident(incident).FromCondition = true;
        GetIncident(incident).Negligent = negligent;
        MakeWreck(vehicle, incident);
        var (x, z, _) = _net.LanePoint(new LaneId(_lane[vehicle]), _pos[vehicle]);
        if (negligent) Charge(_maint.Consequences.NegligenceFine, LedgerEntry.Fines);
        AddEvent(SimEventKind.Breakdown, EventSeverity.Serious, x, z, vehicle,
            $"{_models[_model[vehicle]].Name} on {_lines[_line[vehicle]].Name} broke down" + (negligent ? $" (neglected: fined {_maint.Consequences.NegligenceFine:N0})" : ""));
        TownMindsIncident(GetIncident(incident), x, z);
    }

    /// <summary>A train breaks down: it stands where it is, holding its track, until the crew has cleared it, and is then towed to the nearest rail depot.</summary>
    void BreakDownTrain(Train t)
    {
        bool negligent = t.Condition < _maint.CriticalCondition;
        t.Condition = _maint.WreckCondition;
        t.HaltSeconds = _maint.TrainBreakdownSeconds;
        t.Broken = true;
        t.Speed = 0f;
        t.Acceleration = 0f;
        t.Status = TrainStatus.Halted;
        t.Load = 0;
        t.LoadCargo = -1;
        t.LoadFrom = -1;
        if (t.Riders != null)
        {
            foreach (var lot in t.Riders) GiveUp(_groups[lot.Group], lot.Count, lot.From);
            t.Riders = null;
        }
        var (x, z) = TrainPoint(t);
        if (negligent) Charge(_maint.Consequences.NegligenceFine, LedgerEntry.Fines);
        AddEvent(SimEventKind.TrainBreakdown, EventSeverity.Serious, x, z, t.Id,
            $"A train on {_lines[t.Line].Name} broke down" + (negligent ? $" (neglected: fined {_maint.Consequences.NegligenceFine:N0})" : ""));
    }

    (float X, float Z) TrainPoint(Train t)
    {
        var cars = TrainCars(t);
        return cars.Count > 0 ? (cars[0].X, cars[0].Z) : (0f, 0f);
    }

    // ---- Policy ---------------------------------------------------------------------------------------------------------------

    MaintenancePolicy Resolve(MaintenancePolicy own, int line)
    {
        if (own.Mode != PolicyMode.Inherit) return own;
        if (line >= 0 && _lines[line].Policy.Mode != PolicyMode.Inherit) return _lines[line].Policy;
        return _companyPolicy;
    }

    /// <summary>The policy that applies to a company vehicle, and the level it comes from.</summary>
    public (MaintenancePolicy Policy, PolicyScope Source) EffectivePolicyOf(int vehicle)
    {
        var own = _policy[vehicle];
        if (own.Mode != PolicyMode.Inherit) return (own, PolicyScope.Vehicle);
        int line = _line[vehicle];
        if (line >= 0 && _lines[line].Policy.Mode != PolicyMode.Inherit) return (_lines[line].Policy, PolicyScope.Line);
        return (_companyPolicy, PolicyScope.Company);
    }

    /// <summary>The policy that applies to a train, and the level it comes from.</summary>
    public (MaintenancePolicy Policy, PolicyScope Source) EffectivePolicyOf(Train train)
    {
        if (train.Policy.Mode != PolicyMode.Inherit) return (train.Policy, PolicyScope.Train);
        if (_lines[train.Line].Policy.Mode != PolicyMode.Inherit) return (_lines[train.Line].Policy, PolicyScope.Line);
        return (_companyPolicy, PolicyScope.Company);
    }

    /// <summary>The policy set on this vehicle itself (<see cref="MaintenancePolicy.Inherit"/> when none).</summary>
    public MaintenancePolicy PolicyOf(int vehicle) => _policy[vehicle];

    /// <summary>The repair a policy calls for, null when none is due.</summary>
    RepairKind? DueBy(MaintenancePolicy policy, float condition, long lastRepair)
    {
        if (policy.Mode != PolicyMode.On) return null;
        bool byDays = policy.EveryDays > 0 && Tick - lastRepair >= (long)policy.EveryDays * _ticksPerDay;
        bool byCondition = policy.BelowCondition > 0 && condition < policy.BelowCondition;
        return byDays || byCondition ? policy.Kind : null;
    }

    /// <summary>The repair a vehicle is due for at its stop: the one the player asked for, else the one its policy calls for.</summary>
    RepairKind? DueRepair(int vehicle)
    {
        if (_wantRepair[vehicle] >= 0) return (RepairKind)_wantRepair[vehicle];
        return DueBy(EffectivePolicyOf(vehicle).Policy, _condition[vehicle], _lastRepair[vehicle]);
    }

    RepairKind? DueRepair(Train t)
    {
        if (t.WantRepair >= 0) return (RepairKind)t.WantRepair;
        return DueBy(EffectivePolicyOf(t).Policy, t.Condition, t.LastRepairTick);
    }

    // ---- Queries --------------------------------------------------------------------------------------------------------------

    /// <summary>Condition of a company vehicle, 0 to 100 (100 for ambient traffic, which has none).</summary>
    public float ConditionOf(int vehicle) => _condition[vehicle];

    /// <summary>Whether the vehicle was patched since its last Service or Overhaul, so that it wears faster.</summary>
    public bool IsPatched(int vehicle) => _patched[vehicle];

    /// <summary>Whole days since the last repair of a company vehicle (or since it was bought).</summary>
    public long DaysSinceRepair(int vehicle) => (Tick - _lastRepair[vehicle]) / _ticksPerDay;

    /// <summary>Whole days since the last repair of a train (or since it was bought).</summary>
    public long DaysSinceRepair(Train train) => (Tick - train.LastRepairTick) / _ticksPerDay;

    /// <summary>The repair a company vehicle is waiting for, on its way to or in a depot for; null when none.</summary>
    public RepairKind? RepairOf(int vehicle) =>
        _svcKind[vehicle] >= 0 ? (RepairKind)_svcKind[vehicle] : _wantRepair[vehicle] >= 0 ? (RepairKind)_wantRepair[vehicle] : null;

    public RepairPhase RepairPhaseOf(int vehicle)
    {
        if (_servicing[vehicle]) return JobFor(RepairSubject.Vehicle, vehicle) is { Working: true } ? RepairPhase.Working : RepairPhase.Waiting;
        if (_svcKind[vehicle] >= 0 && _returnDepot[vehicle] >= 0) return RepairPhase.ToDepot;
        return _wantRepair[vehicle] >= 0 || _svcKind[vehicle] >= 0 ? RepairPhase.Requested : RepairPhase.None;
    }

    public RepairKind? RepairOf(Train train) =>
        train.SvcKind >= 0 ? (RepairKind)train.SvcKind : train.WantRepair >= 0 ? (RepairKind)train.WantRepair : null;

    public RepairPhase RepairPhaseOf(Train train)
    {
        if (train.Servicing) return JobFor(RepairSubject.Train, train.Id) is { Working: true } ? RepairPhase.Working : RepairPhase.Waiting;
        if (train.SvcKind >= 0 && train.ReturnDepot >= 0) return RepairPhase.ToDepot;
        return train.WantRepair >= 0 || train.SvcKind >= 0 ? RepairPhase.Requested : RepairPhase.None;
    }

    // ---- Commands -------------------------------------------------------------------------------------------------------------

    int SetMaintenancePolicy(SetMaintenancePolicyCommand c)
    {
        var p = c.Policy;
        if (!Enum.IsDefined(p.Mode) || !Enum.IsDefined(p.Kind) || p.EveryDays < 0 || p.EveryDays > 3650 || p.BelowCondition < 0 || p.BelowCondition > 100) return 0;
        p = p.Normalised;
        switch (c.Scope)
        {
            case PolicyScope.Company:
                if (p.Mode == PolicyMode.Inherit) return 0;
                _companyPolicy = p;
                return 1;
            case PolicyScope.Line:
                if (c.Target < 0 || c.Target >= _lines.Count || !_lines[c.Target].Active) return 0;
                _lines[c.Target].Policy = p;
                return 1;
            case PolicyScope.Vehicle:
                if (c.Target < 0 || c.Target >= _count || _line[c.Target] < 0 || _kind[c.Target] != VehicleKind.Car) return 0;
                _policy[c.Target] = p;
                return 1;
            case PolicyScope.Train:
                if (c.Target < 0 || c.Target >= _trains.Count || !_trains[c.Target].Active) return 0;
                _trains[c.Target].Policy = p;
                return 1;
            default:
                return 0;
        }
    }

    int SetFrequency(IncidentFrequency frequency)
    {
        if (!Enum.IsDefined(frequency)) return 0;
        _frequency = frequency;
        _frequencyScale = _maint.Frequency.For(frequency);
        return 1;
    }

    /// <summary>Asks for a repair. A vehicle or a train gets it at its next stop (it unloads, loads nothing and goes to the nearest depot that can do it); a parked one is taken into a bay of its depot.</summary>
    int RequestRepair(RequestRepairCommand c)
    {
        if (WhyNotRepair(c.Subject, c.Target, c.Kind) != null) return 0;
        switch (c.Subject)
        {
            case RepairSubject.Vehicle:
                _wantRepair[c.Target] = (sbyte)c.Kind;
                _noDepotWarned[c.Target] = false;
                break;
            case RepairSubject.Train:
                _trains[c.Target].WantRepair = (sbyte)c.Kind;
                _trains[c.Target].NoDepotWarned = false;
                break;
            default:
            {
                var entry = _garage[c.Target];
                AddJob(entry.Rail, entry.Depot, RepairSubject.Parked, c.Target, c.Kind);
                break;
            }
        }
        return 1;
    }

    /// <summary>Why a repair cannot be asked for, in words for the player; null when it can.</summary>
    public string? WhyNotRepair(RepairSubject subject, int target, RepairKind kind)
    {
        if (!Enum.IsDefined(kind)) return "that is not a repair";
        switch (subject)
        {
            case RepairSubject.Vehicle:
                if (target < 0 || target >= _count || _line[target] < 0 || _kind[target] != VehicleKind.Car) return "that is not a vehicle in working order";
                if (_ghost[target] || _returnDepot[target] >= 0) return "it is on its way to or in a depot already";
                if (_wantRepair[target] >= 0) return "a repair is asked for already";
                return AnyDepotCan(kind, rail: false) ? null : $"no depot can do a {KindName(kind)} (build a better one or upgrade one)";
            case RepairSubject.Train:
                if (target < 0 || target >= _trains.Count || !_trains[target].Active) return "that is not a train";
                var t = _trains[target];
                if (t.InDepot || t.Servicing || t.ReturnDepot >= 0) return "it is on its way to or in a depot already";
                if (t.Broken) return "it has broken down";
                if (t.WantRepair >= 0) return "a repair is asked for already";
                return AnyDepotCan(kind, rail: true) ? null : $"no rail depot can do a {KindName(kind)} (build a better one or upgrade one)";
            case RepairSubject.Parked:
                if (target < 0 || target >= _garage.Count || !_garage[target].Active) return "that vehicle is not in a depot";
                var entry = _garage[target];
                bool active = entry.Rail ? _railDepots[entry.Depot].Active : _depots[entry.Depot].Active;
                if (!active) return "its depot is closed";
                if (JobFor(RepairSubject.Parked, target) != null) return "it is being repaired already";
                if (!CanRepair(entry.Rail ? _railDepots[entry.Depot].Level : _depots[entry.Depot].Level, kind)) return $"its depot cannot do a {KindName(kind)}";
                return null;
            default:
                return "that cannot be repaired";
        }
    }

    static string KindName(RepairKind kind) => kind.ToString().ToLowerInvariant();

    bool AnyDepotCan(RepairKind kind, bool rail)
    {
        if (rail)
        {
            foreach (var depot in _railDepots)
                if (depot.Active && CanRepair(depot.Level, kind)) return true;
            return false;
        }
        foreach (var depot in _depots)
            if (depot.Active && CanRepair(depot.Level, kind)) return true;
        return false;
    }

    /// <summary>Whether a depot of this level can do a repair: a Patch anywhere, a Service from a Workshop, an Overhaul only at a Complex.</summary>
    public static bool CanRepair(DepotLevel level, RepairKind kind) => (int)level >= (int)kind;

    // ---- Repair arithmetic ----------------------------------------------------------------------------------------------------

    /// <summary>The Condition after a repair, and whether the vehicle is marked as patched.</summary>
    public (float Condition, bool Patched) After(RepairKind kind, float condition)
    {
        var repair = _maint.Of(kind);
        float after = MathF.Max(condition, MathF.Min(repair.Ceiling, condition + repair.Gain));
        return (after, kind == RepairKind.Patch);
    }

    /// <summary>What a repair costs for a vehicle or train that cost <paramref name="price"/> new.</summary>
    public long RepairPrice(RepairKind kind, long price) => Price(price * _maint.Of(kind).PriceShare);

    /// <summary>What a repair costs for a vehicle or train of a model first sold in <paramref name="fromYear"/>: dearer the older the model (C8).</summary>
    public long RepairPrice(RepairKind kind, long price, int fromYear) => Price(price * _maint.Of(kind).PriceShare * _maint.OldVehicle.Factor(fromYear, Today.Year));

    /// <summary>Seconds a repair takes in a bay.</summary>
    public float RepairSeconds(RepairKind kind) => _maint.Of(kind).Seconds;

    public MaintenanceTuning MaintenanceRules => _maint;

    ulong MaintenanceHash()
    {
        ulong h = _companyPolicy.Bits;
        h = h * 31 + (ulong)_frequency;
        return h;
    }
}
