namespace TransportMadness.Sim;

/// <summary>Lines and the company's vehicles: buying, selling, driving the route, loading and getting paid.</summary>
public sealed partial class Simulation
{
    const float SpawnGap = 1f; // free road between a new vehicle and the junction behind it
    const float ArrivalWatch = 40f; // a new vehicle is only put on the road when nothing is within this distance of the junction it leaves

    readonly IReadOnlyList<VehicleModelTuning> _models;
    readonly int[] _modelCargo; // per model: index of the cargo it carries
    readonly List<Line> _lines = new();

    // Per vehicle slot; -1 line means ambient traffic.
    int[] _line = FilledWith(64, -1);
    int[] _model = new int[64];
    int[] _stopIndex = new int[64]; // index into the line's stops of the station it is heading for or standing at
    int[] _dwell = new int[64];     // ticks left of loading, 0 when driving
    int[] _load = new int[64];
    int[] _loadFrom = new int[64];  // station the cargo on board was picked up at
    long[] _loadTick = new long[64];
    long[] _income = new long[64];

    public IReadOnlyList<Line> Lines => _lines;
    public IReadOnlyList<VehicleModelTuning> VehicleModels => _models;

    static int[] ResolveModelCargo(Tuning tuning)
    {
        var cargo = new int[tuning.Vehicles.Count];
        for (int m = 0; m < cargo.Length; m++)
        {
            cargo[m] = -1;
            for (int c = 0; c < tuning.Cargo.Count; c++)
                if (tuning.Cargo[c].Id == tuning.Vehicles[m].Cargo) cargo[m] = c;
            if (cargo[m] < 0) throw new ArgumentException($"Vehicle '{tuning.Vehicles[m].Id}' carries unknown cargo '{tuning.Vehicles[m].Cargo}'.");
        }
        return cargo;
    }

    void GrowFleetArrays(int n)
    {
        Array.Resize(ref _length, n);
        Array.Resize(ref _line, n);
        Array.Resize(ref _model, n);
        Array.Resize(ref _stopIndex, n);
        Array.Resize(ref _dwell, n);
        Array.Resize(ref _load, n);
        Array.Resize(ref _loadFrom, n);
        Array.Resize(ref _loadTick, n);
        Array.Resize(ref _income, n);
    }

    static int[] FilledWith(int length, int value)
    {
        var array = new int[length];
        Array.Fill(array, value);
        return array;
    }

    /// <summary>The index of a vehicle model by its id in the tuning file, or -1.</summary>
    public int ModelIndex(string id)
    {
        for (int m = 0; m < _models.Count; m++)
            if (_models[m].Id == id) return m;
        return -1;
    }

    /// <summary>Creates a line through these stations in order. Returns its id, -1 when refused: fewer than two stations, one that does not exist or is closed, or the same station twice in a row.</summary>
    int CreateLine(int[] stations)
    {
        if (stations.Length < 2) return -1;
        for (int k = 0; k < stations.Length; k++)
        {
            int s = stations[k];
            if (s < 0 || s >= _stations.Count || !_stations[s].Active) return -1;
            if (s == stations[(k + 1) % stations.Length]) return -1;
        }
        _lines.Add(new Line(_lines.Count, (int[])stations.Clone()));
        return _lines.Count - 1;
    }

    /// <summary>Closes a line and sells its vehicles. Returns 1, 0 when there is no such line.</summary>
    int DeleteLine(int line)
    {
        if (line < 0 || line >= _lines.Count || !_lines[line].Active) return 0;
        _lines[line].Active = false;
        for (int i = 0; i < _count; i++)
            if (_line[i] == line && _kind[i] == VehicleKind.Car) SellVehicle(i);
        return 1;
    }

    /// <summary>
    /// Buys a vehicle of a model at a depot and sets it on a line. It appears on a road leaving the depot's node, on the
    /// lane that is the quicker way to the line's first station. Returns the vehicle id, -1 when refused: no such depot
    /// (or it is closed), model or line, not enough money, or no room on the road at the depot.
    /// </summary>
    int BuyVehicle(int depot, string modelId, int line)
    {
        SyncNetwork();
        int model = ModelIndex(modelId);
        if (model < 0 || depot < 0 || depot >= _depots.Count || !_depots[depot].Active) return -1;
        if (line < 0 || line >= _lines.Count || !_lines[line].Active) return -1;
        var spec = _models[model];
        if (!CanAfford(spec.Price) || TrafficNear(_depots[depot].Node)) return -1;

        var first = _stations[_lines[line].Stops[0]];
        var field = first.Active ? FieldTo(first) : null;
        int bestLane = -1;
        float bestCost = float.PositiveInfinity;
        foreach (var roadId in _net.RoadsAt(_depots[depot].Node))
        {
            var road = _net.GetRoad(roadId);
            int lane = (road.From == _depots[depot].Node ? road.ForwardLanes : road.BackwardLanes)[0].Value;
            float position = _laneReach[lane] + spec.Length + SpawnGap;
            if (position > _laneLength[lane] || !IsClear(new LaneId(lane), position, spec.Length + _traffic.SpawnClearance)) continue;
            float cost = field?[lane] ?? 0f;
            if (bestLane < 0 || cost < bestCost) { bestLane = lane; bestCost = cost; }
        }
        if (bestLane < 0) return -1;

        TrySpend(spec.Price, LedgerEntry.Vehicles);
        int id = SpawnVehicle(new LaneId(bestLane), _laneReach[bestLane] + spec.Length + SpawnGap, 0f);
        _line[id] = line;
        _model[id] = model;
        _stopIndex[id] = 0;
        _desired[id] = spec.MaxSpeed;
        _length[id] = spec.Length;
        _nextLane[id] = RouteNextLane(id, bestLane);
        return id;
    }

    /// <summary>Whether a vehicle is about to arrive at the node (within <see cref="ArrivalWatch"/> metres of the end of a lane running into it) and would run into one just put on the road there.</summary>
    bool TrafficNear(int node)
    {
        foreach (var lane in _net.IncomingLanes(node))
            for (int r = _laneStart[lane.Value]; r < _laneStart[lane.Value + 1]; r++)
                if (_laneLength[lane.Value] - _pos[_order[r]] < ArrivalWatch) return true;
        return false;
    }

    /// <summary>Sells a company vehicle for a share of its price; whatever it carries is lost. Returns the vehicle id, -1 when it is not a company vehicle in working order.</summary>
    int SellVehicle(int vehicle)
    {
        if (vehicle < 0 || vehicle >= _count || _line[vehicle] < 0 || _kind[vehicle] != VehicleKind.Car) return -1;
        Earn((long)MathF.Round(_models[_model[vehicle]].Price * _economy.SellRefundShare), LedgerEntry.Vehicles);
        RemoveVehicle(vehicle);
        RebuildOrder();
        return vehicle;
    }

    public bool IsCompanyVehicle(int vehicle) => vehicle >= 0 && vehicle < _count && _line[vehicle] >= 0 && _kind[vehicle] != VehicleKind.Removed;

    /// <summary>Length of a vehicle in metres: that of its model for the company's vehicles, a car's for ambient traffic.</summary>
    public float LengthOf(int vehicle) => _length[vehicle];

    public FleetInfo GetFleetInfo(int vehicle) => new(_line[vehicle], _model[vehicle], _stopIndex[vehicle], _dwell[vehicle] > 0, _load[vehicle]);

    /// <summary>Money this vehicle has earned so far.</summary>
    public long IncomeOf(int vehicle) => _income[vehicle];

    /// <summary>The station a company vehicle is heading for or standing at, null for ambient traffic.</summary>
    Station? TargetStation(int vehicle)
    {
        int line = _line[vehicle];
        return line < 0 ? null : _stations[_lines[line].Stops[_stopIndex[vehicle]]];
    }

    void ResetFleetSlot(int id)
    {
        _line[id] = -1;
        _model[id] = 0;
        _stopIndex[id] = 0;
        _dwell[id] = 0;
        _load[id] = 0;
        _loadFrom[id] = -1;
        _loadTick[id] = 0;
        _income[id] = 0;
        _length[id] = VehicleLength;
    }

    /// <summary>Starts loading for company vehicles that have pulled up at their stop, and sends off those that have finished.</summary>
    void UpdateFleet()
    {
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] < 0 || _kind[i] != VehicleKind.Car) continue;
            if (_dwell[i] > 0)
            {
                if (--_dwell[i] == 0) Depart(i);
            }
            else if (!TargetStation(i)!.Active) Retarget(i);
            else if (ArrivedAtStop(i)) BeginStop(i);
        }
    }

    bool ArrivedAtStop(int i)
    {
        var station = TargetStation(i);
        if (station == null) return false;
        int lane = _lane[i];
        if (!IsStopLane(lane, station) || _pos[i] >= _laneZoneStart[lane]) return false;
        return StopPosition(lane) - _pos[i] <= _stationTuning.ArrivalGap && _speed[i] <= _stationTuning.ArrivalSpeed;
    }

    /// <summary>The braking term for the stop a company vehicle is heading for: an obstacle standing at the stop position.</summary>
    float StopInteraction(int i, float v)
    {
        var station = TargetStation(i);
        if (station == null) return 0f;
        int lane = _lane[i];
        if (!IsStopLane(lane, station) || _pos[i] >= _laneZoneStart[lane]) return 0f;
        return Interaction(v, StopPosition(lane) - _pos[i], 0f);
    }

    void BeginStop(int i)
    {
        var station = TargetStation(i)!;
        var spec = _models[_model[i]];
        int cargo = _modelCargo[_model[i]];
        int unloaded = _load[i];
        if (unloaded > 0) Deliver(i, station);

        int loaded = Math.Min(spec.Capacity, (int)MathF.Floor(station.Waiting[cargo]));
        station.Waiting[cargo] -= loaded;
        _load[i] = loaded;
        _loadFrom[i] = loaded > 0 ? station.Id : -1;
        _loadTick[i] = Tick;

        _speed[i] = 0f;
        _accel[i] = 0f;
        _halted[i] = true;
        _dwell[i] = (int)Math.Max(1, Ticks(spec.DwellSeconds + spec.SecondsPerUnit * (unloaded + loaded)));
    }

    /// <summary>Pays for the cargo on board at the station it has reached: quantity times distance times the rate, less for a slow trip.</summary>
    void Deliver(int i, Station station)
    {
        int cargo = _modelCargo[_model[i]];
        var type = _cargo[cargo];
        var (x0, z0) = _net.GetNode(_stations[_loadFrom[i]].Node);
        var (x1, z1) = _net.GetNode(station.Node);
        float km = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / 1000f;
        float seconds = (Tick - _loadTick[i]) * StepSeconds;
        float share = seconds <= type.FreeSeconds ? 1f
            : seconds >= type.ZeroSeconds ? type.MinPayShare
            : 1f - (1f - type.MinPayShare) * (seconds - type.FreeSeconds) / (type.ZeroSeconds - type.FreeSeconds);
        long pay = (long)MathF.Round(_load[i] * km * type.PayPerUnitKm * share);

        Earn(pay, LedgerEntry.Income);
        var line = _lines[_line[i]];
        line.Income += pay;
        line.Delivered += _load[i];
        _income[i] += pay;
        _load[i] = 0;
        _loadFrom[i] = -1;
    }

    /// <summary>Loading is done: head for the next open station on the line.</summary>
    void Depart(int i)
    {
        _halted[i] = false;
        Retarget(i);
    }

    /// <summary>Picks the next open station on the line after the current one and turns toward it at the next junction.</summary>
    void Retarget(int i)
    {
        var line = _lines[_line[i]];
        for (int tried = 0; tried < line.Stops.Count; tried++)
        {
            _stopIndex[i] = (_stopIndex[i] + 1) % line.Stops.Count;
            if (_stations[line.Stops[_stopIndex[i]]].Active) break;
        }
        _nextLane[i] = RouteNextLane(i, _lane[i]);
    }

    void ChargeVehicleUpkeep()
    {
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] < 0 || _kind[i] != VehicleKind.Car) continue;
            long upkeep = Price(_models[_model[i]].UpkeepPerDay);
            Charge(upkeep, LedgerEntry.Upkeep);
            _lines[_line[i]].Upkeep += upkeep;
        }
    }

    ulong FleetHash()
    {
        ulong h = (ulong)_lines.Count;
        foreach (var line in _lines)
        {
            h = h * 31 + (line.Active ? 1UL : 0UL);
            foreach (int stop in line.Stops) h = h * 31 + (ulong)stop;
            h = h * 31 + (ulong)line.Income;
            h = h * 31 + (ulong)line.Upkeep;
            h = h * 31 + (ulong)line.Delivered;
        }
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] < 0) continue;
            h = h * 31 + (ulong)i;
            h = h * 31 + (ulong)_line[i];
            h = h * 31 + (ulong)_model[i];
            h = h * 31 + (ulong)_stopIndex[i];
            h = h * 31 + (ulong)_dwell[i];
            h = h * 31 + (ulong)_load[i];
            h = h * 31 + (ulong)_loadTick[i];
            h = h * 31 + (ulong)_income[i];
        }
        return h;
    }
}
