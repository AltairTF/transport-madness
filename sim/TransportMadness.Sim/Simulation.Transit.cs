namespace TransportMadness.Sim;

/// <summary>
/// People on the company's lines. A group that goes by public transport walks to a station near its home, queues there for a
/// ride to a station near its destination, is carried by a bus whose line has both, and walks the rest. Passengers queue per
/// destination: a bus only takes those whose destination is on its line, and they get off at that station.
/// </summary>
public sealed partial class Simulation
{
    List<PaxLot>?[] _riders = new List<PaxLot>?[64]; // by vehicle slot: the passengers on board
    int[] _lineFleet = Array.Empty<int>();
    int _lineFleetVersion = -1;
    float[] _lineCycle = Array.Empty<float>(); // seconds one vehicle takes to go round a line, infinity when a leg has no road
    long _lineCycleVersion = -1;

    /// <summary>Whether buses on this map take their passengers from the station queues (a map with towns) rather than from a fixed supply.</summary>
    bool CarriesQueuedPassengers(int vehicle) => Settled && _passengerCargo >= 0 && _modelCargo[_model[vehicle]] == _passengerCargo;

    /// <summary>The passengers on a bus, by the station they are going to: {destination station, people}.</summary>
    public IReadOnlyDictionary<int, int> RidersOf(int vehicle)
    {
        var by = new SortedDictionary<int, int>();
        if (_riders[vehicle] is { } riders)
            foreach (var lot in riders) by[lot.Dest] = by.GetValueOrDefault(lot.Dest) + lot.Count;
        return by;
    }

    void GrowTransitArrays(int n) => Array.Resize(ref _riders, n);

    /// <summary>The number of vehicles of each line, counted again when lines or fleets have changed.</summary>
    void EnsureLineFleet()
    {
        if (_lineFleetVersion == _transitVersion && _lineFleet.Length == _lines.Count) return;
        _lineFleet = new int[_lines.Count];
        for (int i = 0; i < _count; i++)
            if (_line[i] >= 0 && _kind[i] == VehicleKind.Car && _returnDepot[i] < 0) _lineFleet[_line[i]]++;
        foreach (var train in _trains)
            if (train.Active && train.ReturnDepot < 0) _lineFleet[train.Line]++;
        _lineFleetVersion = _transitVersion;
    }

    /// <summary>
    /// How long a vehicle takes to go once round each line, from the road time of every leg (as a car would drive it, times the
    /// ride factor) and the standing time at every stop of the models on it; counted again when lines, fleets or roads change.
    /// </summary>
    void EnsureLineCycles()
    {
        EnsureLineFleet();
        long version = ((long)_syncedNetworkVersion << 24) + _transitVersion;
        if (_lineCycleVersion == version && _lineCycle.Length == _lines.Count) return;
        _lineCycle = new float[_lines.Count];
        var dwell = new float[_lines.Count];
        for (int i = 0; i < _count; i++)
            if (_line[i] >= 0 && _kind[i] == VehicleKind.Car && _returnDepot[i] < 0) dwell[_line[i]] += _models[_model[i]].DwellSeconds;
        foreach (var train in _trains)
            if (train.Active && train.ReturnDepot < 0) dwell[train.Line] += _railTuning.Wagons[train.Wagon].DwellSeconds;
        foreach (var line in _lines)
        {
            float cycle = 0f;
            for (int k = 0; k < line.Stops.Count && !float.IsPositiveInfinity(cycle); k++)
                cycle += RideSeconds(_stations[line.Stops[k]], _stations[line.Stops[(k + 1) % line.Stops.Count]]) * _trips.TransitRideFactor;
            if (_lineFleet[line.Id] > 0) cycle += line.Stops.Count * dwell[line.Id] / _lineFleet[line.Id];
            _lineCycle[line.Id] = cycle;
        }
        _lineCycleVersion = version;
    }

    /// <summary>The seconds one vehicle takes to go once round a line (infinity when some leg has no road); 0 for an unknown line.</summary>
    public float LineCycleSeconds(int line)
    {
        SyncNetwork();
        EnsureLineCycles();
        return line >= 0 && line < _lineCycle.Length ? _lineCycle[line] : 0f;
    }

    static bool LineHasStop(Line line, int station)
    {
        for (int k = 0; k < line.Stops.Count; k++)
            if (line.Stops[k] == station) return true;
        return false;
    }

    /// <summary>Seconds on the road from a station to another, by the way a car would drive it; infinity when no road joins them.</summary>
    float RideSeconds(Station from, Station to)
    {
        if (from.Mode == StationMode.Rail) return RailRideSeconds(from, to);
        var field = FieldTo(to);
        float best = float.PositiveInfinity;
        foreach (var lane in _net.IncomingLanes(from.Node))
            if (IsStopLane(lane.Value, from)) best = MathF.Min(best, field[lane.Value]);
        return best;
    }

    float WalkSeconds(int place, Station station)
    {
        var (x, z) = PointOf(station);
        var (px, pz) = PlacePoint(place);
        float d = MathF.Sqrt((px - x) * (px - x) + (pz - z) * (pz - z));
        return d * _trips.WalkDetour / _trips.WalkSpeed;
    }

    /// <summary>
    /// The quickest way between two places (buildings or industries) on the company's lines: a station that covers each, joined by a line that has
    /// vehicles. The time is the walk to the station, the wait (shorter with more vehicles), the ride and the walk from the
    /// other. Lines are direct; there are no changes.
    /// </summary>
    bool TransitSeconds(int a, int b, out int stationA, out int stationB, out float total, out float walkToStation)
    {
        stationA = stationB = -1;
        total = walkToStation = 0f;
        var near = PlaceStations(a);
        var far = PlaceStations(b);
        if (near.Length == 0 || far.Length == 0) return false;
        EnsureLineCycles();
        float best = float.PositiveInfinity;
        foreach (int sa in near)
            foreach (int sb in far)
            {
                if (sa == sb) continue;
                // Vehicles come every cycle / fleet seconds on a line; with several lines the arrivals add up.
                float perSecond = 0f;
                foreach (var line in _lines)
                    if (line.Active && _lineFleet[line.Id] > 0 && LineHasStop(line, sa) && LineHasStop(line, sb) && !float.IsPositiveInfinity(_lineCycle[line.Id]))
                        perSecond += _lineFleet[line.Id] / MathF.Max(1f, _lineCycle[line.Id]);
                if (perSecond <= 0f) continue;
                float ride = RideSeconds(_stations[sa], _stations[sb]);
                if (float.IsPositiveInfinity(ride)) continue;
                float walkIn = WalkSeconds(a, _stations[sa]);
                float t = walkIn + _trips.TransitWaitShare / perSecond + ride * _trips.TransitRideFactor + WalkSeconds(b, _stations[sb]);
                if (t >= best) continue;
                best = t;
                stationA = sa;
                stationB = sb;
                walkToStation = walkIn;
            }
        total = best;
        return !float.IsPositiveInfinity(best);
    }

    // ---- Queues at stations ------------------------------------------------------------------------------------

    /// <summary>Keeps the passenger count shown in <see cref="Station.Waiting"/> equal to the people in its queue.</summary>
    void RefreshWaiting(Station station)
    {
        if (_passengerCargo < 0) return;
        int total = 0;
        foreach (var lot in station.Queue) total += lot.Count;
        station.Waiting[_passengerCargo] = total;
    }

    /// <summary>People who have waited too long, or whose station or destination has closed, give up.</summary>
    void GiveUpWaiting()
    {
        long limit = Ticks(_trips.TransitGiveUpSeconds);
        foreach (var station in _stations)
        {
            if (station.Queue.Count == 0) continue;
            bool changed = false;
            for (int k = 0; k < station.Queue.Count; k++)
            {
                var lot = station.Queue[k];
                if (station.Active && _stations[lot.Dest].Active && Tick - lot.Since < limit) continue;
                station.Queue.RemoveAt(k--);
                station.Today = station.Today with { GaveUp = station.Today.GaveUp + lot.Count };
                GiveUp(_groups[lot.Group], lot.Count, station.Id);
                changed = true;
            }
            if (changed) RefreshWaiting(station);
        }
    }

    // ---- Buses ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Lets off the passengers whose destination is this station, and any whose destination has closed. Each group is paid for
    /// by distance between the stations and the time on board, and walks from here to its destination. Returns the people let off.
    /// </summary>
    int UnloadRiders(int vehicle, Station here)
    {
        int unloaded = AlightRiders(_riders[vehicle], _lines[_line[vehicle]], here, out long income);
        _income[vehicle] += income;
        SetRiderLoad(vehicle);
        return unloaded;
    }

    /// <summary>Lets the riders in a list off at a station, as <see cref="UnloadRiders"/> does for a bus; <paramref name="income"/> is what they paid.</summary>
    int AlightRiders(List<PaxLot>? riders, Line line, Station here, out long income)
    {
        income = 0;
        if (riders == null) return 0;
        int unloaded = 0;
        for (int k = 0; k < riders.Count; k++)
        {
            var lot = riders[k];
            bool arrived = lot.Dest == here.Id;
            if (!arrived && _stations[lot.Dest].Active && LineHasStop(line, lot.Dest)) continue; // else the line no longer goes there
            riders.RemoveAt(k--);
            unloaded += lot.Count;
            if (arrived) income += PayFor(line, _passengerCargo, lot.Count, _stations[lot.From], here, lot.Boarded);
            here.Today = here.Today with { Alighted = here.Today.Alighted + lot.Count };
            var g = _groups[lot.Group];
            var (x, z) = PointOf(here);
            var (tx, tz) = PlacePoint(g.Destination);
            float d = MathF.Sqrt((tx - x) * (tx - x) + (tz - z) * (tz - z));
            ScheduleWalk(g, TripEventKind.Arrive, lot.Count, -1, -1, (x, z), (tx, tz), WalkTicks(d));
        }
        return unloaded;
    }

    /// <summary>Takes on, up to the bus's capacity, the people waiting here for a station on its line, longest waiting first.</summary>
    int BoardRiders(int vehicle, Station here)
    {
        int room = _models[_model[vehicle]].Capacity - _load[vehicle];
        if (room <= 0 || here.Queue.Count == 0) return 0;
        var line = _lines[_line[vehicle]];
        var riders = _riders[vehicle] ??= new List<PaxLot>();
        int boarded = BoardInto(riders, line, here, room);
        SetRiderLoad(vehicle);
        return boarded;
    }

    /// <summary>Takes up to <paramref name="room"/> people from the queue at a station into a list of riders: those bound for a station on the line, longest waiting first.</summary>
    int BoardInto(List<PaxLot> riders, Line line, Station here, int room)
    {
        int boarded = 0;
        for (int k = 0; k < here.Queue.Count && room > 0; k++)
        {
            var lot = here.Queue[k];
            if (lot.Dest == here.Id || !LineHasStop(line, lot.Dest) || !_stations[lot.Dest].Active) continue;
            int take = Math.Min(room, lot.Count);
            PaxLot on;
            if (take == lot.Count)
            {
                here.Queue.RemoveAt(k--);
                on = lot;
            }
            else
            {
                lot.Count -= take;
                on = new PaxLot(lot.Group, take, lot.From, lot.Dest, lot.Since);
            }
            on.Count = take;
            on.Boarded = Tick;
            riders.Add(on);
            room -= take;
            boarded += take;
        }
        RefreshWaiting(here);
        here.Today = here.Today with { Boarded = here.Today.Boarded + boarded };
        return boarded;
    }

    void SetRiderLoad(int vehicle)
    {
        int total = 0;
        if (_riders[vehicle] is { } riders)
            foreach (var lot in riders) total += lot.Count;
        _load[vehicle] = total;
        _loadCargo[vehicle] = total > 0 ? _passengerCargo : -1;
    }

    /// <summary>A bus is leaving the world (sold, wrecked and cleared): the people on board are put down where they set out from and make their own way.</summary>
    void ReleaseRiders(int vehicle)
    {
        var riders = _riders[vehicle];
        if (riders == null) return;
        _riders[vehicle] = null;
        foreach (var lot in riders) GiveUp(_groups[lot.Group], lot.Count, lot.From);
    }
}
