namespace TransportMadness.Sim;

/// <summary>Stations: where they can stand, what they cost, and the cargo they produce.</summary>
public sealed partial class Simulation
{
    const float StationSnapRadius = 12f; // a click this close to a node or a road's centre line picks it
    const float StationAlongStep = 5f;   // a station on the open road stands at a multiple of this along it

    readonly StationTuning _stationTuning;
    readonly IReadOnlyList<CargoTuning> _cargo;
    readonly List<Station> _stations = new();

    public IReadOnlyList<Station> Stations => _stations;
    public IReadOnlyList<CargoTuning> CargoTypes => _cargo;

    /// <summary>The active station standing at a node, if any.</summary>
    public Station? StationAt(int node)
    {
        foreach (var station in _stations)
            if (station.Active && station.Node == node) return station;
        return null;
    }

    /// <summary>Where a station would stand: at an existing node, or on a road (<see cref="Road"/>, <see cref="Along"/>) that is split to make one.</summary>
    public readonly record struct StationSite(int Node, RoadId Road, float Along, float X, float Y, float Z);

    /// <summary>
    /// Where a station built at (x, z) would stand, null when none can: nothing to build on, a station there already,
    /// no lane long enough to stop on, or a road too short to split.
    /// </summary>
    public StationSite? FindStationSite(float x, float z)
    {
        int node = _net.FindNodeNear(x, z, StationSnapRadius, withRoadsOnly: true);
        if (node >= 0)
        {
            if (StationAt(node) != null || !HasStopLane(node)) return null;
            var (nx, nz) = _net.GetNode(node);
            return new StationSite(node, default, 0f, nx, _net.GetNodeHeight(node), nz);
        }
        if (_net.NearestOnRoad(x, z, StationSnapRadius, StationAlongStep) is not { } point) return null;
        float length = _net.GetRoad(point.Road).Length;
        if (point.Along < _stationTuning.MinApproach || point.Along > length - _stationTuning.MinApproach) return null;
        return new StationSite(-1, point.Road, point.Along, point.X, point.Y, point.Z);
    }

    public long StationPrice => _stationTuning.Price;

    /// <summary>
    /// Builds a station at the node nearest to (x, z), or on the road under that point, which is split there so the
    /// station has a node of its own (<see cref="FindStationSite"/>). Returns the station id, -1 when refused: no site,
    /// or not enough money.
    /// </summary>
    int BuildStation(float x, float z)
    {
        SyncNetwork();
        if (!CanAfford(_stationTuning.Price) || FindStationSite(x, z) is not { } site) return -1;

        int node = site.Node;
        if (node < 0)
        {
            node = _net.SplitRoad(site.Road, site.Along);
            SyncNetwork();
        }

        TrySpend(_stationTuning.Price, LedgerEntry.Stations);
        _stations.Add(new Station(_stations.Count, node, _cargo.Count));
        _fields.Add(Array.Empty<float>());
        _fieldVersion.Add(-1);
        return _stations.Count - 1;
    }

    bool HasStopLane(int node)
    {
        foreach (var lane in _net.IncomingLanes(node))
            if (_net.GetLane(lane).Length >= _stationTuning.MinApproach) return true;
        return false;
    }

    /// <summary>Whether vehicles bound for the station stop on this lane: it runs into the station's node and is long enough.</summary>
    bool IsStopLane(int lane, Station station) =>
        station.Active && _laneEndNode[lane] == station.Node && _laneLength[lane] >= _stationTuning.MinApproach;

    /// <summary>Where on a stop lane the front of a vehicle halts: a little before the junction.</summary>
    float StopPosition(int lane) => _laneZoneStart[lane] - _stationTuning.StopMargin;

    /// <summary>Stations whose node has lost every road close, as depots do; lines skip them.</summary>
    void CloseOrphanedStations()
    {
        foreach (var station in _stations)
            if (station.Active && _net.RoadsAt(station.Node).Count == 0) station.Active = false;
    }

    /// <summary>Until towns exist, every active station produces each cargo at its fixed rate, up to a limit.</summary>
    void GenerateCargo()
    {
        foreach (var station in _stations)
        {
            if (!station.Active) continue;
            for (int c = 0; c < _cargo.Count; c++)
            {
                float next = station.Waiting[c] + _cargo[c].SupplyPerDay * StepSeconds / _economy.DayLength;
                station.Waiting[c] = MathF.Min(next, _cargo[c].MaxWaiting);
            }
        }
    }

    ulong StationHash()
    {
        ulong h = (ulong)_stations.Count;
        foreach (var station in _stations)
        {
            h = h * 31 + (ulong)station.Node;
            h = h * 31 + (station.Active ? 1UL : 0UL);
            foreach (float w in station.Waiting) h = h * 31 + BitConverter.SingleToUInt32Bits(w);
        }
        return h;
    }
}
