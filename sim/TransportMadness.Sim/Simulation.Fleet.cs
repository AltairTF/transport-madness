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
    bool[] _ghost = new bool[64];   // just out of the depot and not yet in the traffic: nothing blocks it and it blocks nothing (see StepGhosts)
    float[] _ghostFrom = new float[64]; // where on its lane it came out
    bool[] _ghostOut = new bool[64];    // false while it waits inside the depot for room to come out
    int[] _ghostNode = new int[64];     // the depot's node
    int[] _returnDepot = FilledWith(64, -1); // the depot it is driving back to, to be parked there, -1 when it is on its line

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
        Array.Resize(ref _ghost, n);
        Array.Resize(ref _ghostFrom, n);
        Array.Resize(ref _ghostOut, n);
        Array.Resize(ref _ghostNode, n);
        int oldReturn = _returnDepot.Length;
        Array.Resize(ref _returnDepot, n);
        for (int k = oldReturn; k < n; k++) _returnDepot[k] = -1;
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
            if (_stations[s].Mode != _stations[stations[0]].Mode) return false; // a line is all road or all rail
        }
        return true;
    }

    /// <summary>Creates a line through these stations in order. Returns its id, -1 when refused: fewer than two stations, one that does not exist or is closed, or the same station twice in a row.</summary>
    int CreateLine(int[] stations)
    {
        if (!ValidStops(stations)) return -1;
        _lines.Add(new Line(_lines.Count, (int[])stations.Clone(), _stations[stations[0]].Mode));
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
        if (_stations[stations[0]].Mode != current.Mode) return 0;
        var before = current.Stops;
        current.Stops = (int[])stations.Clone();
        _transitVersion++;
        if (current.Mode == StationMode.Rail) EditTrainsOfLine(line, before, stations);
        for (int i = 0; i < _count; i++)
        {
            // A vehicle on its way to a depot for a repair comes back to the line, so it keeps its stop in step too.
            if (_line[i] != line || _kind[i] != VehicleKind.Car || (_returnDepot[i] >= 0 && _svcKind[i] < 0)) continue;
            int heading = before[_stopIndex[i]];
            int at = Array.IndexOf(stations, heading);
            _stopIndex[i] = at >= 0 ? at : Math.Min(_stopIndex[i], stations.Length - 1);
            // Not while it is in the middle of a junction: its path to the next lane is already set.
            if (at < 0 && _returnDepot[i] < 0 && _dwell[i] == 0 && _lane[i] >= 0 && _pos[i] < _laneZoneStart[_lane[i]])
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
        {
            if (_line[i] != line || _kind[i] != VehicleKind.Car) continue;
            if (_returnDepot[i] < 0 || _servicing[i]) SellVehicle(i);
            else _svcKind[i] = -1; // on its way to a depot for a repair: it parks there instead
        }
        foreach (var train in _trains)
        {
            if (!train.Active || train.Line != line) continue;
            if (train.ReturnDepot < 0 || train.Servicing) SellTrain(train.Id);
            else train.SvcKind = -1;
        }
        return 1;
    }

    /// <summary>
    /// Buys a vehicle of a model at a depot and sets it on a line. It comes out of the depot onto a road leaving the depot's node, on the
    /// lane that is the quicker way to the line's first station; when other vehicles are in the way it comes out as a ghost (see
    /// <see cref="StepGhosts"/>) instead of waiting. Returns the vehicle id, -1 when refused: no such depot (or it is closed), model or
    /// line, not enough money, or no road from the depot to the line's first station.
    /// </summary>
    int BuyVehicle(int depot, string modelId, int line)
    {
        SyncNetwork();
        int model = ModelIndex(modelId);
        if (model < 0 || depot < 0 || depot >= _depots.Count || !_depots[depot].Active || !IsAvailable(_models[model])) return -1;
        if (!CanAfford(_models[model].Price)) return -1;
        int id = PlaceAtDepot(depot, model, line);
        if (id >= 0) TrySpend(_models[model].Price, LedgerEntry.Vehicles);
        return id;
    }

    /// <summary>
    /// Puts a vehicle out of a depot on a line, the money already settled. A ghost when the road at the depot is busy. Returns the vehicle
    /// id, -1 when the line is not an open road line or there is no road from the depot to its first station.
    /// </summary>
    int PlaceAtDepot(int depot, int model, int line, float condition = 100f, long lastRepair = -1, bool patched = false)
    {
        if (line < 0 || line >= _lines.Count || !_lines[line].Active || _lines[line].Mode != StationMode.Road) return -1;
        var spec = _models[model];
        var first = _stations[_lines[line].Stops[0]];
        var field = first.Active ? FieldTo(first) : null;
        int lane = ChooseExitLane(_depots[depot].Node, spec.Length, field, out bool clear);
        if (lane < 0) return -1;

        float position = _laneReach[lane] + spec.Length + SpawnGap;
        int id = SpawnVehicle(new LaneId(lane), position, 0f);
        _line[id] = line;
        _model[id] = model;
        _stopIndex[id] = 0;
        _desired[id] = spec.MaxSpeed;
        _length[id] = spec.Length;
        SetVehicleCondition(id, condition, lastRepair < 0 ? Tick : lastRepair, patched);
        _nextLane[id] = RouteNextLane(id, lane);
        if (!clear)
        {
            _ghost[id] = true;
            _ghostOut[id] = false; // waits inside the depot until there is room to come out
            _ghostFrom[id] = position;
            _ghostNode[id] = _depots[depot].Node;
            RebuildOrder();
        }
        _transitVersion++;
        return id;
    }

    /// <summary>
    /// The lane leaving a node a new vehicle of this length comes out on: the quickest way along <paramref name="field"/> (any when null),
    /// preferring a lane with room for it when nothing is about to arrive at the node. -1 when no road leaving the node leads where the field does.
    /// </summary>
    int ChooseExitLane(int node, float length, float[]? field, out bool clear)
    {
        clear = false;
        int bestLane = -1;
        float bestCost = float.PositiveInfinity;
        bool arriving = TrafficNear(node);
        foreach (var roadId in _net.RoadsAt(node))
        {
            var road = _net.GetRoad(roadId);
            int lane = (road.From == node ? road.ForwardLanes : road.BackwardLanes)[0].Value;
            float position = _laneReach[lane] + length + SpawnGap;
            if (position > _laneLength[lane]) continue;
            float cost = field?[lane] ?? 0f;
            if (float.IsPositiveInfinity(cost)) continue; // no way from here to where it is going
            bool free = !arriving && IsClearFor(new LaneId(lane), position, length, _traffic.SpawnClearance);
            if (bestLane < 0 || (free && !clear) || (free == clear && cost < bestCost)) { bestLane = lane; bestCost = cost; clear = free; }
        }
        return bestLane;
    }

    /// <summary>Whether a vehicle has just come out of a depot and is not yet part of the traffic (or is still inside, waiting for room to come out).</summary>
    public bool IsGhost(int vehicle) => vehicle >= 0 && vehicle < _count && _ghost[vehicle];

    /// <summary>Whether a vehicle is still inside its depot, waiting for room to come out: not on the road yet, so not drawn.</summary>
    public bool IsInDepot(int vehicle) => vehicle >= 0 && vehicle < _count && _ghost[vehicle] && !_ghostOut[vehicle];

    const float GhostSpeed = 6f;   // metres per second a vehicle comes out of a depot at
    const float GhostAccel = 2.5f;

    /// <summary>
    /// Moves the vehicles that have just come out of a depot. Nothing waits for a ghost on the road, but it never goes where another vehicle is:
    /// it stays inside the depot until the doorway has room (nobody within <see cref="TrafficTuning.SpawnClearance"/>, nothing about to arrive at the
    /// node, no ghost ahead of it in the queue), drives out at up to <see cref="GhostSpeed"/>, stops short of a vehicle ahead, and becomes an ordinary
    /// vehicle once its whole length is out and the lane has room. It stops short of the junction if there never is.
    /// </summary>
    void StepGhosts()
    {
        bool any = false;
        for (int i = 0; i < _count && !any; i++) any = _ghost[i];
        if (!any) return;
        for (int i = 0; i < _count; i++)
        {
            if (!_ghost[i] || _servicing[i] || _lane[i] < 0) continue;
            int lane = _lane[i];
            if (!_ghostOut[i])
            {
                // Inside the depot: comes out when the doorway is free, in the order of purchase.
                bool queued = false;
                for (int j = 0; j < _count && !queued; j++)
                {
                    if (j == i || !_ghost[j] || _servicing[j] || _lane[j] != lane) continue;
                    queued = _ghostOut[j] ? _pos[j] - _length[j] < _ghostFrom[i] + SpawnGap : j < i;
                }
                if (queued || TrafficNear(_ghostNode[i]) || !IsClearFor(new LaneId(lane), _ghostFrom[i], _length[i], _traffic.SpawnClearance)) continue;
                _ghostOut[i] = true;
            }

            // Out on the lane: a ghost ahead in the doorway, or a vehicle ahead, holds it back.
            float room = float.PositiveInfinity;
            for (int j = 0; j < _count; j++)
            {
                if (j == i || _lane[j] != lane || _kind[j] == VehicleKind.Removed) continue;
                if (_ghost[j] && !_ghostOut[j]) continue;
                bool ahead = _pos[j] > _pos[i] || (_pos[j] == _pos[i] && j < i);
                if (ahead) room = MathF.Min(room, _pos[j] - _length[j] - SpawnGap - _pos[i]);
            }
            if (room <= 0f) { _speed[i] = 0f; continue; }
            _speed[i] = MathF.Min(MathF.Min(GhostSpeed, _desired[i]), _speed[i] + GhostAccel * StepSeconds);
            _pos[i] += MathF.Min(_speed[i] * StepSeconds, room);
            if (_speed[i] * StepSeconds > room) _speed[i] = 0f;
            float limit = MathF.Max(_laneZoneStart[lane], _ghostFrom[i]);
            if (_pos[i] > limit) { _pos[i] = limit; _speed[i] = 0f; }
            float needed = MathF.Min(_length[i] + SpawnGap, limit - _ghostFrom[i]); // how far it has to go: all the way out, or as far as the lane lets it
            if (_pos[i] - _ghostFrom[i] >= needed - 1e-4f && IsClearFor(new LaneId(lane), _pos[i], _length[i], _traffic.SpawnClearance))
            {
                _ghost[i] = false;
                _ghostOut[i] = false;
                _accel[i] = 0f;
                RebuildOrder();
            }
        }
    }

    /// <summary>
    /// The active depots from which a vehicle could drive to the first station of a line, nearest (by driving time)
    /// first; empty when the line is not open or no depot is connected to it by road.
    /// </summary>
    public IReadOnlyList<int> DepotsByDistance(int line)
    {
        SyncNetwork();
        var result = new List<(float Cost, int Depot)>();
        if (line < 0 || line >= _lines.Count || !_lines[line].Active || _lines[line].Mode != StationMode.Road) return Array.Empty<int>();
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
        _ghost[id] = false;
        _ghostOut[id] = false;
        _returnDepot[id] = -1;
        _length[id] = VehicleLength;
        ResetTripSlot(id);
        _riders[id] = null;
    }

    /// <summary>Starts loading for company vehicles that have pulled up at their stop, and sends off those that have finished.</summary>
    void UpdateFleet()
    {
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] < 0 || _kind[i] != VehicleKind.Car || _ghost[i]) continue;
            if (_returnDepot[i] >= 0)
            {
                if (_bay[i])
                {
                    if (!BayExitClear(i)) continue; // waits in the bay for a gap in the traffic
                    LeaveBay(i);
                    RebuildOrder();
                }
                _halted[i] = false;
                if (ReachedDepot(i))
                {
                    if (_svcKind[i] >= 0) EnterServiceBay(i);
                    else ParkAtDepot(i);
                }
                continue;
            }
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
        if (station == null || _returnDepot[i] >= 0) return 0f;
        int lane = _lane[i];
        if (!IsStopLane(lane, station) || _pos[i] >= _laneZoneStart[lane]) return 0f;
        return Interaction(v, StopPosition(lane) - _pos[i], 0f);
    }

    void BeginStop(int i)
    {
        var station = TargetStation(i)!;
        var spec = _models[_model[i]];
        int unloaded = 0, loaded = 0;
        // Due for a repair that a depot can do: it unloads what this stop takes, puts the rest down here and loads nothing.
        bool service = DecideService(i);
        if (CarriesQueuedPassengers(i))
        {
            unloaded = UnloadRiders(i, station);
            if (service)
            {
                ReleaseRiders(i);
                SetRiderLoad(i);
            }
            else loaded = BoardRiders(i, station);
        }
        else if (_load[i] > 0 && Accepts(station, _loadCargo[i])) unloaded = Deliver(i, station);
        if (service && _load[i] > 0)
        {
            _load[i] = 0;
            _loadCargo[i] = -1;
            _loadFrom[i] = -1;
        }

        if (!service && _load[i] == 0 && !CarriesQueuedPassengers(i))
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
    int CargoToLoad(int i, Station station) => CargoToLoadFor(_modelCargo[_model[i]], _lines[_line[i]], _stopIndex[i], station);

    /// <summary>The cargo an empty vehicle (or train) takes on at a station when it carries <paramref name="fixedCargo"/> (or any freight), bound for the next stop of the line.</summary>
    int CargoToLoadFor(int fixedCargo, Line line, int stopIndex, Station station)
    {
        var next = _stations[line.Stops[(stopIndex + 1) % line.Stops.Count]];
        int best = -1;
        float bestWaiting = 1f; // at least one whole unit
        for (int c = 0; c < _cargo.Count; c++)
        {
            if (fixedCargo >= 0 ? c != fixedCargo : c == _passengerCargo || c == _workerCargo) continue;
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
        _income[vehicle] += PayFor(_lines[_line[vehicle]], cargo, units, from, to, since);
    }

    /// <summary>Pays the company for units delivered on a line (by a vehicle or a train) and returns the amount.</summary>
    long PayFor(Line line, int cargo, int units, Station from, Station to, long since)
    {
        var type = _cargo[cargo];
        var (x0, z0) = PointOf(from);
        var (x1, z1) = PointOf(to);
        float km = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / 1000f;
        float seconds = (Tick - since) * StepSeconds;
        float share = seconds <= type.FreeSeconds ? 1f
            : seconds >= type.ZeroSeconds ? type.MinPayShare
            : 1f - (1f - type.MinPayShare) * (seconds - type.FreeSeconds) / (type.ZeroSeconds - type.FreeSeconds);
        long pay = (long)MathF.Round(units * km * type.PayPerUnitKm * share);

        Earn(pay, LedgerEntry.Income);
        line.Income += pay;
        line.Delivered += units;
        return pay;
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
        if (_svcKind[i] >= 0) StartServiceTrip(i);
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
            h = h * 31 + line.Policy.Bits;
        }
        if (_lines.Count > 0 || _garage.Count > 0) h = h * 31 + MaintenanceHash();
        if (_jobs.Count > 0 || _jobCount > 0) h = h * 31 + RepairHash();
        if (_garage.Count > 0) h = h * 31 + GarageHash();
        for (int i = 0; i < _count; i++)
        {
            if (_line[i] < 0) continue;
            h = h * 31 + BitConverter.SingleToUInt32Bits(_condition[i]);
            h = h * 31 + (ulong)_lastRepair[i];
            h = h * 31 + (_patched[i] ? 1UL : 0UL) + (_servicing[i] ? 2UL : 0UL);
            h = h * 31 + _policy[i].Bits;
            h = h * 31 + (ulong)(_wantRepair[i] + 1) + ((ulong)(_svcKind[i] + 1) << 4);
            if (_ghost[i]) h = h * 31 + 13UL;
            if (_returnDepot[i] >= 0) h = h * 31 + (ulong)(_returnDepot[i] + 17);
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
