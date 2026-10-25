namespace TransportMadness.Sim;

/// <summary>A vehicle or train the company owns that is standing in a depot on no line: bought without a line, or sent back from one.</summary>
public sealed class ParkedVehicle
{
    internal ParkedVehicle(int id, bool rail, int depot)
    {
        Id = id;
        Rail = rail;
        Depot = depot;
    }

    public int Id { get; }
    /// <summary>A train in a rail depot, else a road vehicle in a road depot.</summary>
    public bool Rail { get; }
    /// <summary>Id of the road depot or the rail depot it stands in.</summary>
    public int Depot { get; internal set; }
    /// <summary>The road vehicle model (an index into <see cref="Simulation.VehicleModels"/>).</summary>
    public int Model { get; internal init; }
    /// <summary>The train's locomotive and wagon (indices into <see cref="Simulation.Locomotives"/> and <see cref="Simulation.Wagons"/>) and how many wagons it has.</summary>
    public int Locomotive { get; internal init; }
    public int Wagon { get; internal init; }
    public int Wagons { get; internal init; }
    /// <summary>False once it has been put on a line or sold.</summary>
    public bool Active { get; internal set; } = true;
    /// <summary>Its Condition, 0 to 100 (a vehicle that was towed in after a breakdown has little); kept while it waits.</summary>
    public float Condition { get; internal set; } = 100f;
    public long LastRepairTick { get; internal set; }
    /// <summary>Patched since its last Service or Overhaul, so that it wears faster.</summary>
    public bool Patched { get; internal set; }
}

/// <summary>
/// The company's garage: vehicles bought for no line in particular wait in a depot until they are put on one, and a vehicle can be sent
/// back from its line to wait there again.
/// </summary>
public sealed partial class Simulation
{
    readonly List<ParkedVehicle> _garage = new();

    /// <summary>Every vehicle that has been parked, oldest first; check <see cref="ParkedVehicle.Active"/>.</summary>
    public IReadOnlyList<ParkedVehicle> Garage => _garage;

    ParkedVehicle AddToGarage(ParkedVehicle entry)
    {
        _garage.Add(entry);
        _transitVersion++;
        return entry;
    }

    /// <summary>What a parked vehicle cost new.</summary>
    /// <summary>The year the model of a parked vehicle or train was first sold.</summary>
    public int FromYearOf(ParkedVehicle parked) => parked.Rail ? _railTuning.Locomotives[parked.Locomotive].FromYear : _models[parked.Model].FromYear;

    public long PriceOf(ParkedVehicle parked) =>
        parked.Rail
            ? _railTuning.Locomotives[parked.Locomotive].Price + parked.Wagons * _railTuning.Wagons[parked.Wagon].Price
            : _models[parked.Model].Price;

    // ---- Buying and selling -----------------------------------------------------------------------------------------

    /// <summary>Buys a road vehicle into a road depot's garage. With <see cref="BuyParkedVehicleCommand.AnyDepot"/> the first open depot is used. Returns the garage id, -1 when refused.</summary>
    int BuyParkedVehicle(int depot, string modelId)
    {
        int model = ModelIndex(modelId);
        if (depot == BuyParkedVehicleCommand.AnyDepot) depot = FirstOpen(_depots, d => d.Active && !IsShut(d), d => d.Id);
        if (model < 0 || depot < 0 || depot >= _depots.Count || !_depots[depot].Active || IsShut(_depots[depot]) || !IsAvailable(_models[model])) return -1;
        long price = _models[model].Price;
        if (!CanAfford(price)) return -1;
        TrySpend(price, LedgerEntry.Vehicles);
        return AddToGarage(new ParkedVehicle(_garage.Count, false, depot) { Model = model, LastRepairTick = Tick }).Id;
    }

    /// <summary>Buys a train into a rail depot's garage. Returns the garage id, -1 when refused.</summary>
    int BuyParkedTrain(int depot, string locomotive, string wagon, int wagons)
    {
        SyncRail();
        int l = LocomotiveIndex(locomotive), w = WagonIndex(wagon);
        if (depot == BuyParkedVehicleCommand.AnyDepot) depot = FirstOpen(_railDepots, d => d.Active && !IsShut(d), d => d.Id);
        if (l < 0 || w < 0 || depot < 0 || depot >= _railDepots.Count || !_railDepots[depot].Active || IsShut(_railDepots[depot]) || !IsAvailable(_railTuning.Locomotives[l])) return -1;
        if (wagons < 1 || wagons > _railTuning.Locomotives[l].MaxWagons) return -1;
        long price = _railTuning.Locomotives[l].Price + wagons * _railTuning.Wagons[w].Price;
        if (!CanAfford(price)) return -1;
        TrySpend(price, LedgerEntry.Vehicles);
        return AddToGarage(new ParkedVehicle(_garage.Count, true, depot) { Locomotive = l, Wagon = w, Wagons = wagons, LastRepairTick = Tick }).Id;
    }

    static int FirstOpen<T>(IReadOnlyList<T> items, Func<T, bool> open, Func<T, int> id)
    {
        foreach (var item in items)
            if (open(item)) return id(item);
        return -1;
    }

    /// <summary>Sells a parked vehicle for a share of its price. Returns its id, -1 when there is none.</summary>
    int SellParked(int parked)
    {
        if (parked < 0 || parked >= _garage.Count || !_garage[parked].Active) return -1;
        var entry = _garage[parked];
        if (entry.Rail ? IsShut(_railDepots[entry.Depot]) : IsShut(_depots[entry.Depot])) return -1; // a raid has shut the depot
        Earn((long)MathF.Round(PriceOf(entry) * _economy.SellRefundShare), LedgerEntry.Vehicles);
        CancelJobsFor(RepairSubject.Parked, parked);
        entry.Active = false;
        _transitVersion++;
        return parked;
    }

    // ---- Putting on a line --------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts a parked vehicle on a line: it comes out of the depot it stands in (any open depot connected to the line when that one is closed).
    /// Returns the vehicle id (for a train, the train id), -1 when refused; see <see cref="WhyNotAssign"/>.
    /// </summary>
    int AssignParked(int parked, int line)
    {
        if (WhyNotAssign(parked, line) != null) return -1;
        var entry = _garage[parked];
        int id;
        if (entry.Rail)
        {
            var depot = _railDepots[entry.Depot].Active ? _railDepots[entry.Depot] : RailDepotFor(entry, line)!;
            if (!DepotRoute(depot, line, entry.Locomotive, out var path)) return -1;
            var train = StartTrain(depot, entry.Locomotive, entry.Wagon, entry.Wagons, line, path);
            train.Condition = entry.Condition;
            train.LastRepairTick = entry.LastRepairTick;
            train.Patched = entry.Patched;
            id = train.Id;
        }
        else
        {
            int depot = _depots[entry.Depot].Active ? entry.Depot : DepotsByDistance(line).FirstOrDefault(-1);
            id = depot < 0 ? -1 : PlaceAtDepot(depot, entry.Model, line, entry.Condition, entry.LastRepairTick, entry.Patched);
        }
        if (id < 0) return -1;
        entry.Active = false;
        _transitVersion++;
        return id;
    }

    RailDepot? RailDepotFor(ParkedVehicle entry, int line)
    {
        foreach (var depot in _railDepots)
            if (depot.Active && DepotRoute(depot, line, entry.Locomotive, out _)) return depot;
        return null;
    }

    /// <summary>Why a parked vehicle cannot be put on a line, in words for the player; null when it can.</summary>
    public string? WhyNotAssign(int parked, int line)
    {
        SyncNetwork();
        if (parked < 0 || parked >= _garage.Count || !_garage[parked].Active) return "that vehicle is not in a depot";
        if (JobFor(RepairSubject.Parked, parked) != null) return "it is being repaired";
        if (line < 0 || line >= _lines.Count || !_lines[line].Active) return "that line does not exist";
        var entry = _garage[parked];
        var mode = _lines[line].Mode;
        if (entry.Rail != (mode == StationMode.Rail)) return entry.Rail ? "a train runs only on a rail line" : "a road vehicle runs only on a road line";
        var first = _stations[_lines[line].Stops[0]];
        if (!first.Active) return "the line's first station is closed";
        if (entry.Rail)
        {
            SyncRail();
            if (_railDepots[entry.Depot].Active)
            {
                if (!DepotRoute(_railDepots[entry.Depot], line, entry.Locomotive, out _))
                    return "no track leads from its rail depot to the line's first station (build track from the depot, or join it to the line)";
                return null;
            }
            return RailDepotFor(entry, line) == null ? "its rail depot is closed and no other depot has a track to the line's first station" : null;
        }
        if (_depots[entry.Depot].Active)
        {
            var field = FieldTo(first);
            bool reaches = false;
            foreach (var roadId in _net.RoadsAt(_depots[entry.Depot].Node))
            {
                var road = _net.GetRoad(roadId);
                int lane = road.RightLaneLeaving(_depots[entry.Depot].Node);
                if (lane < 0) continue;
                reaches |= !float.IsPositiveInfinity(field[lane]) && _laneReach[lane] + _models[entry.Model].Length + SpawnGap <= _laneLength[lane];
            }
            return reaches ? null : "no road leads from its depot to the line's first station";
        }
        return DepotsByDistance(line).Count == 0 ? "its depot is closed and no other depot has a road to the line's first station" : null;
    }

    // ---- Sending back -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes a road vehicle off its line: it stops loading, drives to the nearest depot it can reach and waits there. What it carries is lost
    /// and the people on board get off at the depot. Returns 1, 0 when refused (see <see cref="WhyNotPark"/>).
    /// </summary>
    int ParkVehicle(int vehicle)
    {
        if (WhyNotPark(vehicle) != null) return 0;
        int depot = NearestDepotFor(vehicle);
        _returnDepot[vehicle] = depot;
        _svcKind[vehicle] = -1;
        _svcDepot[vehicle] = -1;
        _tripDest[vehicle] = _depots[depot].Node;
        _dwell[vehicle] = 0;
        _load[vehicle] = 0;
        _loadCargo[vehicle] = -1;
        _loadFrom[vehicle] = -1;
        _transitVersion++;
        int lane = _lane[vehicle];
        if (!_bay[vehicle] && _pos[vehicle] < _laneZoneStart[lane]) _nextLane[vehicle] = RouteTripLane(vehicle, lane);
        return 1;
    }

    /// <summary>Why a vehicle cannot be sent to a depot; null when it can.</summary>
    public string? WhyNotPark(int vehicle)
    {
        SyncNetwork();
        if (vehicle < 0 || vehicle >= _count || _line[vehicle] < 0 || _kind[vehicle] != VehicleKind.Car) return "that is not a vehicle in working order";
        if (_ghost[vehicle]) return "it is still coming out of the depot";
        if (_returnDepot[vehicle] >= 0) return "it is on its way to a depot already";
        return NearestDepotFor(vehicle) < 0 ? "no road leads from where it is to a depot" : null;
    }

    /// <summary>Whether a company vehicle is driving to a depot to be parked.</summary>
    public bool IsReturning(int vehicle) => vehicle >= 0 && vehicle < _count && _returnDepot[vehicle] >= 0;

    int NearestDepotFor(int vehicle)
    {
        int lane = _lane[vehicle];
        int best = -1;
        float bestCost = float.PositiveInfinity;
        foreach (var depot in _depots)
        {
            if (!depot.Active) continue;
            float cost = FieldToNode(depot.Node)[lane];
            if (float.IsPositiveInfinity(cost) || cost >= bestCost) continue;
            best = depot.Id;
            bestCost = cost;
        }
        return best;
    }

    bool ReachedDepot(int vehicle)
    {
        int lane = _lane[vehicle];
        var depot = _depots[_returnDepot[vehicle]];
        if (!depot.Active) { _returnDepot[vehicle] = NearestDepotFor(vehicle); return false; }
        return lane >= 0 && _laneEndNode[lane] == depot.Node && _laneLength[lane] - _pos[vehicle] <= _trips.CarArriveRange;
    }

    /// <summary>A returning vehicle has reached its depot: it goes into the garage.</summary>
    void ParkAtDepot(int vehicle)
    {
        var entry = new ParkedVehicle(_garage.Count, false, _returnDepot[vehicle])
        {
            Model = _model[vehicle],
            Condition = _condition[vehicle],
            LastRepairTick = _lastRepair[vehicle],
            Patched = _patched[vehicle],
        };
        RemoveVehicle(vehicle);
        RebuildOrder();
        AddToGarage(entry);
    }

    /// <summary>
    /// Takes a train off its line: it stops, finds its way to the nearest rail depot it can reach and waits there. What it carries is lost.
    /// Returns 1, 0 when refused (see <see cref="WhyNotParkTrain"/>).
    /// </summary>
    int ParkTrain(int train)
    {
        if (WhyNotParkTrain(train) != null) return 0;
        var t = _trains[train];
        if (t.InDepot || t.Steps.Count == 0)
        {
            // Never left: it simply stays.
            var home = _railDepots.First(d => d.Active && d.Node == t.SpawnNode);
            t.ReturnDepot = home.Id;
            ParkTrainAtDepot(t);
            return 1;
        }
        CutRouteAfterApproved(t);
        int depot = NearestRailDepotFor(t);
        if (depot < 0) return 0;
        t.ReturnDepot = depot;
        t.SvcKind = -1;
        t.SvcDepot = -1;
        t.Dwell = 0;
        t.Load = 0;
        t.LoadCargo = -1;
        t.LoadFrom = -1;
        _transitVersion++;
        return 1;
    }

    /// <summary>Why a train cannot be sent to a depot; null when it can.</summary>
    public string? WhyNotParkTrain(int train)
    {
        SyncRail();
        if (train < 0 || train >= _trains.Count || !_trains[train].Active) return "that is not a train";
        var t = _trains[train];
        if (t.ReturnDepot >= 0) return "it is on its way to a depot already";
        if (t.HaltSeconds > 0f) return "it is halted after an accident";
        if (t.InDepot || t.Steps.Count == 0) return _railDepots.Any(d => d.Active && d.Node == t.SpawnNode) ? null : "its rail depot is closed";
        return null;
    }

    int NearestRailDepotFor(Train t, RepairKind? kind = null)
    {
        int best = -1;
        float bestSeconds = float.PositiveInfinity;
        int was = t.ReturnDepot;
        foreach (var depot in _railDepots)
        {
            if (!depot.Active || (kind is { } needed && !CanRepair(depot.Level, needed))) continue;
            t.ReturnDepot = depot.Id;
            if (PlanLeg(t, apply: false, out float seconds, willStop: true) && seconds < bestSeconds) { best = depot.Id; bestSeconds = seconds; }
        }
        t.ReturnDepot = was;
        return best;
    }

    /// <summary>A returning train has stopped at its depot: it goes into the garage.</summary>
    void ParkTrainAtDepot(Train t)
    {
        var entry = new ParkedVehicle(_garage.Count, true, t.ReturnDepot)
        {
            Locomotive = t.Locomotive,
            Wagon = t.Wagon,
            Wagons = t.Wagons,
            Condition = t.Condition,
            LastRepairTick = t.LastRepairTick,
            Patched = t.Patched,
        };
        RemoveTrain(t, refund: false);
        AddToGarage(entry);
    }

    /// <summary>The daily upkeep of what waits in the garage: <see cref="EconomyTuning.ParkedUpkeepShare"/> of the running upkeep, on no line's books.</summary>
    void ChargeGarageUpkeep()
    {
        foreach (var entry in _garage)
        {
            if (!entry.Active) continue;
            float daily = entry.Rail
                ? _railTuning.Locomotives[entry.Locomotive].UpkeepPerDay + entry.Wagons * _railTuning.Wagons[entry.Wagon].UpkeepPerDay
                : _models[entry.Model].UpkeepPerDay;
            Charge(Price(daily * _economy.ParkedUpkeepShare), LedgerEntry.Upkeep);
        }
    }

    ulong GarageHash()
    {
        ulong h = (ulong)_garage.Count;
        foreach (var entry in _garage)
        {
            h = h * 31 + (entry.Active ? 1UL : 0UL) + (entry.Rail ? 2UL : 0UL);
            h = h * 31 + (ulong)entry.Depot;
            h = h * 31 + (ulong)(entry.Rail ? entry.Locomotive * 97 + entry.Wagon * 13 + entry.Wagons : entry.Model);
            h = h * 31 + BitConverter.SingleToUInt32Bits(entry.Condition);
            h = h * 31 + (ulong)entry.LastRepairTick + (entry.Patched ? 1UL << 50 : 0UL);
        }
        return h;
    }
}
