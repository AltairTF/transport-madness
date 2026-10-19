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
            if (_line[i] >= 0 && _kind[i] == VehicleKind.Car) _lineFleet[_line[i]]++;
        _lineFleetVersion = _transitVersion;
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
        var field = FieldTo(to);
        float best = float.PositiveInfinity;
        foreach (var lane in _net.IncomingLanes(from.Node))
            if (IsStopLane(lane.Value, from)) best = MathF.Min(best, field[lane.Value]);
        return best;
    }

    float WalkSeconds(Building b, Station station)
    {
        var (x, z) = _net.GetNode(station.Node);
        float d = MathF.Sqrt((b.X - x) * (b.X - x) + (b.Z - z) * (b.Z - z));
        return d * _trips.WalkDetour / _trips.WalkSpeed;
    }

    /// <summary>
    /// The quickest way between two buildings on the company's lines: a station that covers each, joined by a line that has
    /// vehicles. The time is the walk to the station, the wait (shorter with more vehicles), the ride and the walk from the
    /// other. Lines are direct; there are no changes.
    /// </summary>
    bool TransitSeconds(Building a, Building b, out int stationA, out int stationB, out float total, out float walkToStation)
    {
        stationA = stationB = -1;
        total = walkToStation = 0f;
        if (a.Id >= _buildingStations.Count || b.Id >= _buildingStations.Count) return false;
        var near = _buildingStations[a.Id];
        var far = _buildingStations[b.Id];
        if (near.Length == 0 || far.Length == 0) return false;
        EnsureLineFleet();
        float best = float.PositiveInfinity;
        foreach (int sa in near)
            foreach (int sb in far)
            {
                if (sa == sb) continue;
                int vehicles = 0;
                foreach (var line in _lines)
                    if (line.Active && LineHasStop(line, sa) && LineHasStop(line, sb)) vehicles += _lineFleet[line.Id];
                if (vehicles == 0) continue;
                float ride = RideSeconds(_stations[sa], _stations[sb]);
                if (float.IsPositiveInfinity(ride)) continue;
                float walkIn = WalkSeconds(a, _stations[sa]);
                float t = walkIn + _trips.TransitWaitSeconds / vehicles + ride * _trips.TransitRideFactor + WalkSeconds(b, _stations[sb]);
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
        var riders = _riders[vehicle];
        if (riders == null) return 0;
        int unloaded = 0;
        for (int k = 0; k < riders.Count; k++)
        {
            var lot = riders[k];
            bool arrived = lot.Dest == here.Id;
            if (!arrived && _stations[lot.Dest].Active) continue;
            riders.RemoveAt(k--);
            unloaded += lot.Count;
            if (arrived) Pay(vehicle, _passengerCargo, lot.Count, _stations[lot.From], here, lot.Boarded);
            var g = _groups[lot.Group];
            var (x, z) = _net.GetNode(here.Node);
            var to = _buildings[g.Destination];
            float d = MathF.Sqrt((to.X - x) * (to.X - x) + (to.Z - z) * (to.Z - z));
            Schedule(Tick + WalkTicks(d), new TripEvent(TripEventKind.Arrive, g.Id, lot.Count, -1, -1));
        }
        SetRiderLoad(vehicle);
        return unloaded;
    }

    /// <summary>Takes on, up to the bus's capacity, the people waiting here for a station on its line, longest waiting first.</summary>
    int BoardRiders(int vehicle, Station here)
    {
        int room = _models[_model[vehicle]].Capacity - _load[vehicle];
        if (room <= 0 || here.Queue.Count == 0) return 0;
        var line = _lines[_line[vehicle]];
        var riders = _riders[vehicle] ??= new List<PaxLot>();
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
        SetRiderLoad(vehicle);
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
