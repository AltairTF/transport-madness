namespace TransportMadness.Sim;

/// <summary>Lines and the company's vehicles: buying, selling, driving the route, loading and getting paid.</summary>
public sealed partial class Simulation
{
    const float SpawnGap = 1f; // free road between a new vehicle and the junction behind it
    const float ArrivalWatch = 40f; // a new vehicle is only put on the road when nothing is within this distance of the junction it leaves

    readonly IReadOnlyList<VehicleModelTuning> _models;
    readonly int[] _modelCargo; // per model: index of the cargo it carries, or FreightAny
    readonly List<Line> _lines = new();

    // Per vehicle slot; -1 line means ambient traffic.
    int[] _line = FilledWith(64, -1);
    int[] _model = new int[64];
    int[] _stopIndex = new int[64]; // index into the line's stops of the station it is heading for or standing at
    int[] _dwell = new int[64];     // ticks left of loading, 0 when driving
    int[] _load = new int[64];
    int[] _loadCargo = FilledWith(64, -1); // the cargo on board, -1 when empty
    int[] _loadFrom = new int[64];  // station the cargo on board was picked up at
    long[] _loadTick = new long[64];
    long[] _income = new long[64];
    long[] _upkeepPaid = new long[64];
    bool[] _bay = new bool[64];     // standing in a station's bay: out of the lane, so out of its ordering
    int[] _bayStation = FilledWith(64, -1);

    public IReadOnlyList<Line> Lines => _lines;
    public IReadOnlyList<VehicleModelTuning> VehicleModels => _models;

    static int[] ResolveModelCargo(Tuning tuning)
    {
        var cargo = new int[tuning.Vehicles.Count];
        for (int m = 0; m < cargo.Length; m++)
        {
            cargo[m] = tuning.Vehicles[m].Cargo == "freight" ? FreightAny : -1;
            for (int c = 0; c < tuning.Cargo.Count; c++)
                if (tuning.Cargo[c].Id == tuning.Vehicles[m].Cargo) cargo[m] = c;
            if (cargo[m] == -1) throw new ArgumentException($"Vehicle '{tuning.Vehicles[m].Id}' carries unknown cargo '{tuning.Vehicles[m].Cargo}'.");
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
        Array.Resize(ref _loadCargo, n);
        Array.Resize(ref _loadFrom, n);
        Array.Resize(ref _loadTick, n);
        Array.Resize(ref _income, n);
        Array.Resize(ref _upkeepPaid, n);
        Array.Resize(ref _bay, n);
        int oldBay = _bayStation.Length;
        Array.Resize(ref _bayStation, n);
        for (int k = oldBay; k < n; k++) _bayStation[k] = -1;
        GrowTripArrays(n);
        GrowTransitArrays(n);
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

    bool ValidStops(int[] stations)
    {
        if (stations.Length < 2) return false;
        for (int k = 0; k < stations.Length; k++)
        {
            int s = stations[k];
            if (s < 0 || s >= _stations.Count || !_stations[s].Active) return false;
            if (s == stations[(k + 1) % stations.Length]) return false;
        }
        return true;
    }

    /// <summary>Creates a line through these stations in order. Returns its id, -1 when refused: fewer than two stations, one that does not exist or is closed, or the same station twice in a row.</summary>
    int CreateLine(int[] stations)
    {
        if (!ValidStops(stations)) return -1;
        _lines.Add(new Line(_lines.Count, (int[])stations.Clone()));
        _transitVersion++;
        return _lines.Count - 1;
    }

    /// <summary>
    /// Changes the stations of a line. A vehicle keeps heading for its station when the new list has it, otherwise for the stop at
    /// the same place in the list (the last when the list is shorter); it takes the new turn at the next junction it has not yet
    /// entered. People on board whose station is gone are put down at the next stop. Returns 1, 0 when refused.
    /// </summary>
    int EditLine(int line, int[] stations)
    {
        if (line < 0 || line >= _lines.Count || !_lines[line].Active || !ValidStops(stations)) return 0;
        var current = _lines[line];
        var before = current.Stops;
        current.Stops = (int[])stations.Clone();
        _transitVersion++;
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] != line || _kind[i] != VehicleKind.Car) continue;
            int heading = before[_stopIndex[i]];
            int at = Array.IndexOf(stations, heading);
            _stopIndex[i] = at >= 0 ? at : Math.Min(_stopIndex[i], stations.Length - 1);
            // Not while it is in the middle of a junction: its path to the next lane is already set.
            if (at < 0 && _dwell[i] == 0 && _lane[i] >= 0 && _pos[i] < _laneZoneStart[_lane[i]])
                _nextLane[i] = RouteNextLane(i, _lane[i]);
        }
        return 1;
    }

    /// <summary>Names a line. Returns 1, 0 when there is no such line or the name is empty or longer than 24 characters.</summary>
    int RenameLine(int line, string name)
    {
        name = name?.Trim() ?? "";
        if (line < 0 || line >= _lines.Count || !_lines[line].Active || name.Length == 0 || name.Length > 24) return 0;
        _lines[line].Name = name;
        return 1;
    }

    /// <summary>Gives a line a colour. Returns 1, 0 when there is no such line or the value is not 0xRRGGBB.</summary>
    int SetLineColor(int line, int rgb)
    {
        if (line < 0 || line >= _lines.Count || !_lines[line].Active || rgb < 0 || rgb > 0xFFFFFF) return 0;
        _lines[line].Color = rgb;
        return 1;
    }

    /// <summary>Buys a vehicle for a line at the nearest depot that is connected to it by road and takes it. Returns the vehicle id, -1 when no depot can or the company cannot pay.</summary>
    int BuyVehicleAtNearestDepot(string model, int line)
    {
        foreach (int depot in DepotsByDistance(line))
        {
            int id = BuyVehicle(depot, model, line);
            if (id >= 0) return id;
        }
        return -1;
    }

    /// <summary>Closes a line and sells its vehicles. Returns 1, 0 when there is no such line.</summary>
    int DeleteLine(int line)
    {
        if (line < 0 || line >= _lines.Count || !_lines[line].Active) return 0;
        _lines[line].Active = false;
        _transitVersion++;
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
            if (position > _laneLength[lane] || !IsClearFor(new LaneId(lane), position, spec.Length, _traffic.SpawnClearance)) continue;
            float cost = field?[lane] ?? 0f;
            if (float.IsPositiveInfinity(cost)) continue; // no way from here to the line's first station
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
        _transitVersion++;
        return id;
    }

    /// <summary>
    /// The active depots from which a vehicle could drive to the first station of a line, nearest (by driving time)
    /// first; empty when the line is not open or no depot is connected to it by road.
    /// </summary>
    public IReadOnlyList<int> DepotsByDistance(int line)
    {
        SyncNetwork();
        var result = new List<(float Cost, int Depot)>();
        if (line < 0 || line >= _lines.Count || !_lines[line].Active) return Array.Empty<int>();
        var first = _stations[_lines[line].Stops[0]];
        if (!first.Active) return Array.Empty<int>();
        var field = FieldTo(first);
        foreach (var depot in _depots)
        {
            if (!depot.Active) continue;
            float best = float.PositiveInfinity;
            foreach (var roadId in _net.RoadsAt(depot.Node))
            {
                var road = _net.GetRoad(roadId);
                int lane = (road.From == depot.Node ? road.ForwardLanes : road.BackwardLanes)[0].Value;
                best = MathF.Min(best, field[lane]);
            }
            if (!float.IsPositiveInfinity(best)) result.Add((best, depot.Id));
        }
        result.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost) : a.Depot.CompareTo(b.Depot));
        return result.Select(r => r.Depot).ToList();
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

    /// <summary>Gives a vehicle another length (for tests that need a long body without buying a bus).</summary>
    internal void SetLength(int vehicle, float length) => _length[vehicle] = length;

    public FleetInfo GetFleetInfo(int vehicle) => new(_line[vehicle], _model[vehicle], _stopIndex[vehicle], _dwell[vehicle] > 0, _load[vehicle], _loadCargo[vehicle]);

    /// <summary>Money this vehicle has earned so far.</summary>
    public long IncomeOf(int vehicle) => _income[vehicle];

    /// <summary>Upkeep the company has paid for this vehicle so far.</summary>
    public long UpkeepPaidFor(int vehicle) => _upkeepPaid[vehicle];

    /// <summary>The id of the station a company vehicle is heading for or standing at, -1 for ambient traffic.</summary>
    public int TargetStationOf(int vehicle) => TargetStation(vehicle)?.Id ?? -1;

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
        _loadCargo[id] = -1;
        _loadFrom[id] = -1;
        _loadTick[id] = 0;
        _income[id] = 0;
        _upkeepPaid[id] = 0;
        _bay[id] = false;
        _bayStation[id] = -1;
        _length[id] = VehicleLength;
        ResetTripSlot(id);
        _riders[id] = null;
    }

    /// <summary>Starts loading for company vehicles that have pulled up at their stop, and sends off those that have finished.</summary>
    void UpdateFleet()
    {
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] < 0 || _kind[i] != VehicleKind.Car) continue;
            if (_dwell[i] > 0)
            {
                if (--_dwell[i] == 0)
                {
                    if (_bay[i] && !BayExitClear(i)) _dwell[i] = 1; // waits in the bay for a gap in the traffic
                    else Depart(i);
                }
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
        int unloaded = 0, loaded = 0;
        if (CarriesQueuedPassengers(i))
        {
            unloaded = UnloadRiders(i, station);
            loaded = BoardRiders(i, station);
        }
        else if (_load[i] > 0 && Accepts(station, _loadCargo[i])) unloaded = Deliver(i, station);

        if (_load[i] == 0 && !CarriesQueuedPassengers(i))
        {
            int cargo = CargoToLoad(i, station);
            if (cargo >= 0)
            {
                loaded = Math.Min(spec.Capacity, (int)MathF.Floor(station.Waiting[cargo]));
                station.Waiting[cargo] -= loaded;
                _load[i] = loaded;
                _loadCargo[i] = cargo;
                _loadFrom[i] = station.Id;
                _loadTick[i] = Tick;
            }
        }

        _speed[i] = 0f;
        _accel[i] = 0f;
        _halted[i] = true;
        _dwell[i] = (int)Math.Max(1, Ticks(spec.DwellSeconds + spec.SecondsPerUnit * (unloaded + loaded)));

        // At a station with bays a vehicle on the right-most lane pulls out of the lane while it loads, if there is a free bay.
        if (station.HasBays && station.InBay < _stationTuning.BayCapacity && _net.GetLane(new LaneId(_lane[i])).Index == 0)
        {
            _bay[i] = true;
            _bayStation[i] = station.Id;
            station.InBay++;
            RebuildOrder();
        }
    }

    /// <summary>Whether a vehicle standing in a bay can pull back into the lane: nothing overlaps it or is close ahead, and nothing is within the exit gap behind.</summary>
    bool BayExitClear(int i)
    {
        int lane = _lane[i];
        float front = _pos[i], rear = front - _length[i], gap = _stationTuning.BayExitGap;
        for (int r = _laneStart[lane]; r < _laneStart[lane + 1]; r++)
        {
            int j = _order[r];
            if (_pos[j] > rear - gap && _pos[j] - _length[j] < front + _traffic.SpawnClearance) return false;
        }
        return true;
    }

    /// <summary>Takes a vehicle out of its bay (it is leaving, or going from the world); the caller puts the lane ordering right.</summary>
    void LeaveBay(int vehicle)
    {
        if (!_bay[vehicle]) return;
        _bay[vehicle] = false;
        if (_bayStation[vehicle] >= 0) _stations[_bayStation[vehicle]].InBay--;
        _bayStation[vehicle] = -1;
    }

    /// <summary>Whether a vehicle is standing in a station's bay, beside the lane.</summary>
    public bool InBay(int vehicle) => vehicle >= 0 && vehicle < _count && _bay[vehicle];

    /// <summary>
    /// The cargo an empty vehicle takes on at a station: what its model carries, or for a freight model whichever freight
    /// cargo has the most waiting; only cargo that the next stop on the line accepts. -1 when there is none.
    /// </summary>
    int CargoToLoad(int i, Station station)
    {
        int fixedCargo = _modelCargo[_model[i]];
        var line = _lines[_line[i]];
        var next = _stations[line.Stops[(_stopIndex[i] + 1) % line.Stops.Count]];
        int best = -1;
        float bestWaiting = 1f; // at least one whole unit
        for (int c = 0; c < _cargo.Count; c++)
        {
            if (fixedCargo >= 0 ? c != fixedCargo : c == _passengerCargo) continue;
            if (station.Waiting[c] < bestWaiting || !Accepts(next, c)) continue;
            best = c;
            bestWaiting = station.Waiting[c];
        }
        return best;
    }

    /// <summary>
    /// Unloads the cargo on board at the station it has reached and is paid for what is taken: quantity times distance times
    /// the rate, less for a slow trip. Where buildings or industries are what takes it, only what they have room for is taken
    /// and the rest is lost. Returns the units on board before unloading.
    /// </summary>
    int Deliver(int i, Station station)
    {
        int cargo = _loadCargo[i];
        int onBoard = _load[i];
        int taken = Settled && cargo != _passengerCargo ? Distribute(station, cargo, onBoard) : onBoard;
        Pay(i, cargo, taken, _stations[_loadFrom[i]], station, _loadTick[i]);
        _load[i] = 0;
        _loadCargo[i] = -1;
        _loadFrom[i] = -1;
        return onBoard;
    }

    /// <summary>
    /// Pays the company for units delivered by a vehicle: quantity times the straight-line distance between the stations
    /// times the cargo's rate, less for a slow trip since <paramref name="since"/>.
    /// </summary>
    void Pay(int vehicle, int cargo, int units, Station from, Station to, long since)
    {
        var type = _cargo[cargo];
        var (x0, z0) = _net.GetNode(from.Node);
        var (x1, z1) = _net.GetNode(to.Node);
        float km = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / 1000f;
        float seconds = (Tick - since) * StepSeconds;
        float share = seconds <= type.FreeSeconds ? 1f
            : seconds >= type.ZeroSeconds ? type.MinPayShare
            : 1f - (1f - type.MinPayShare) * (seconds - type.FreeSeconds) / (type.ZeroSeconds - type.FreeSeconds);
        long pay = (long)MathF.Round(units * km * type.PayPerUnitKm * share);

        Earn(pay, LedgerEntry.Income);
        var line = _lines[_line[vehicle]];
        line.Income += pay;
        line.Delivered += units;
        _income[vehicle] += pay;
    }

    /// <summary>Loading is done: head for the next open station on the line.</summary>
    void Depart(int i)
    {
        if (_bay[i])
        {
            LeaveBay(i);
            RebuildOrder();
        }
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
            _upkeepPaid[i] += upkeep;
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
            h = h * 31 + (ulong)(_loadCargo[i] + 1);
            h = h * 31 + (ulong)_loadTick[i];
            h = h * 31 + (ulong)_income[i];
            h = h * 31 + (ulong)_upkeepPaid[i];
            if (_bay[i]) h = h * 31 + 11UL;
        }
        return h;
    }
}
