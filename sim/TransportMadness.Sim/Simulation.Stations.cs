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

    /// <summary>Where a station stands on the map: its road node, or its rail node.</summary>
    public (float X, float Z) PointOf(Station station) => station.Mode == StationMode.Rail ? (station.X, station.Z) : _net.GetNode(station.Node);
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

    /// <summary>What a station would be like at a place: where it stands, what it costs, and how much of the town and industry around it it would cover.</summary>
    public readonly record struct StationQuote(StationSite Site, long Price, bool Affordable, float Radius, int Buildings, int Industries);

    /// <summary>The quote for a station clicked for at (x, z), null when none can stand there (<see cref="FindStationSite"/>).</summary>
    public StationQuote? QuoteStation(float x, float z, bool bays)
    {
        if (FindStationSite(x, z) is not { } site) return null;
        long price = _stationTuning.Price + (bays ? _stationTuning.BayPrice : 0);
        float radius = _stationTuning.CoverageRadius;
        int buildings = BuildingsNear(site.X, site.Z, radius).Count(id => _buildings[id].State == BuildingState.Active);
        return new StationQuote(site, price, CanAfford(price), radius, buildings, IndustriesNear(site.X, site.Z, radius).Count + GatewaysNear(site.X, site.Z, radius, GatewayKind.Road).Count);
    }

    /// <summary>The open station nearest to a point within <paramref name="radius"/>, or -1.</summary>
    public int NearestStation(float x, float z, float radius)
    {
        int best = -1;
        float bestDistance = radius * radius;
        foreach (var station in _stations)
        {
            if (!station.Active) continue;
            var (sx, sz) = PointOf(station);
            float d = (sx - x) * (sx - x) + (sz - z) * (sz - z);
            if (d <= bestDistance) { best = station.Id; bestDistance = d; }
        }
        return best;
    }

    public long StationPrice => _stationTuning.Price;
    public long StationBayPrice => _stationTuning.BayPrice;

    /// <summary>
    /// Builds a station at the node nearest to (x, z), or on the road under that point, which is split there so the
    /// station has a node of its own (<see cref="FindStationSite"/>). Returns the station id, -1 when refused: no site,
    /// or not enough money.
    /// </summary>
    int BuildStation(float x, float z, bool bays)
    {
        SyncNetwork();
        long price = _stationTuning.Price + (bays ? _stationTuning.BayPrice : 0);
        if (!CanAfford(price) || FindStationSite(x, z) is not { } site) return -1;

        int node = site.Node;
        if (node < 0)
        {
            node = _net.SplitRoad(site.Road, site.Along);
            SyncNetwork();
        }

        TrySpend(price, LedgerEntry.Stations);
        _stations.Add(new Station(_stations.Count, node, _cargo.Count) { HasBays = bays });
        _coverageDirty = true;
        _fields.Add(Array.Empty<float>());
        _fieldVersion.Add(-1);
        return _stations.Count - 1;
    }

    /// <summary>Names a station. Returns 1, 0 when it is closed or unknown or the name is empty or longer than 24 characters.</summary>
    int RenameStation(int station, string name)
    {
        name = name?.Trim() ?? "";
        if (station < 0 || station >= _stations.Count || !_stations[station].Active || name.Length == 0 || name.Length > 24) return 0;
        _stations[station].Name = name;
        return 1;
    }

    /// <summary>Closes a station by hand, as losing its roads does: people waiting give up, lines skip it, nothing is refunded. Returns 1, 0 when it is closed or unknown.</summary>
    int CloseStation(int station)
    {
        if (station < 0 || station >= _stations.Count || !_stations[station].Active) return 0;
        _stations[station].Active = false;
        _coverageDirty = true;
        _transitVersion++;
        return 1;
    }

    /// <summary>Adds bays to an open station without them, for the bay price. Returns 1, 0 when refused.</summary>
    int AddStationBays(int station)
    {
        if (station < 0 || station >= _stations.Count || !_stations[station].Active || _stations[station].HasBays || _stations[station].Mode != StationMode.Road) return 0;
        if (!TrySpend(_stationTuning.BayPrice, LedgerEntry.Stations)) return 0;
        _stations[station].HasBays = true;
        return 1;
    }

    bool HasStopLane(int node)
    {
        foreach (var lane in _net.IncomingLanes(node))
            if (_net.GetLane(lane).Length >= _stationTuning.MinApproach) return true;
        return false;
    }

    /// <summary>Vehicles per bay pocket and how long the pocket is drawn for each: <see cref="StationTuning.BayCapacity"/> vehicles of a bus's length and a gap.</summary>
    public float BayLength => _stationTuning.BayCapacity * (VehicleLength + 2f);

    /// <summary>
    /// The lanes where a vehicle can pull into a bay of the station (the right-most lane of each approach) and the position along
    /// each where the front of a halted vehicle stands, for drawing the bays. Empty for a station without bays.
    /// </summary>
    public IReadOnlyList<(LaneId Lane, float Position)> BayLanes(Station station) => station.HasBays ? StopLanes(station) : new List<(LaneId Lane, float Position)>();

    /// <summary>The right-most lane of each approach where vehicles stop at the station and the position along it where the front of a halted vehicle stands, for drawing the stop on the road.</summary>
    public IReadOnlyList<(LaneId Lane, float Position)> StopLanes(Station station)
    {
        var list = new List<(LaneId Lane, float Position)>();
        if (!station.Active) return list;
        SyncNetwork();
        foreach (var lane in _net.IncomingLanes(station.Node))
            if (IsStopLane(lane.Value, station) && _net.GetLane(lane).Index == 0) list.Add((lane, StopPosition(lane.Value)));
        return list;
    }

    /// <summary>Whether vehicles bound for the station stop on this lane: it runs into the station's node and is long enough.</summary>
    bool IsStopLane(int lane, Station station) =>
        station.Active && _laneEndNode[lane] == station.Node && _laneLength[lane] >= (station.DockKind != 0 ? RoadNetwork.MinRoadLength : _stationTuning.MinApproach);

    /// <summary>Where on a stop lane the front of a vehicle halts: a little before the junction.</summary>
    float StopPosition(int lane) => _laneZoneStart[lane] - _stationTuning.StopMargin;

    /// <summary>Stations whose node has lost every road close, as depots do; lines skip them. A stop at an industry or gateway opens again when a road reaches its access point.</summary>
    void CloseOrphanedStations()
    {
        foreach (var station in _stations)
        {
            if (station.Active && station.Mode == StationMode.Road && _net.RoadsAt(station.Node).Count == 0)
            {
                station.Active = false;
                _coverageDirty = true;
            }
            else if (!station.Active && station.DockKind != 0 && AccessNodeOf(new AccessRef(station.DockKind == 2, station.DockId)) is var node and >= 0)
            {
                station.Active = true;
                station.Node = node;
                _coverageDirty = true;
                _transitVersion++;
            }
        }
    }

    /// <summary>The stop at an industry's or a road gateway's access point, made when first asked for (<see cref="AddDockCommand"/>), found again after. -1 when there is none to make.</summary>
    int EnsureDock(bool gateway, int id)
    {
        SyncNetwork();
        if (gateway ? id < 0 || id >= _gateways.Count || _gateways[id].Kind != GatewayKind.Road : id < 0 || id >= _industries.Count) return -1;
        int kind = gateway ? 2 : 1;
        foreach (var station in _stations)
            if (station.DockKind == kind && station.DockId == id) return station.Active ? station.Id : -1;
        int node = AccessNodeOf(new AccessRef(gateway, id));
        if (node < 0) return -1;
        _stations.Add(new Station(_stations.Count, node, _cargo.Count) { DockKind = kind, DockId = id });
        _fields.Add(Array.Empty<float>());
        _fieldVersion.Add(-1);
        _coverageDirty = true;
        return _stations.Count - 1;
    }

    /// <summary>
    /// Cargo appears at stations: on a map without towns or industries every active station produces each cargo at its fixed
    /// rate, up to a limit. On one with, cargo comes from the industries (<see cref="UpdateIndustries"/>) and passengers from
    /// trip groups that queue at the station (<see cref="UpdateTrips"/>), so nothing is made here.
    /// </summary>
    void GenerateCargo()
    {
        if (Settled) return;
        for (int s = 0; s < _stations.Count; s++)
        {
            var station = _stations[s];
            if (!station.Active) continue;
            for (int c = 0; c < _cargo.Count; c++)
            {
                float perStep = _cargo[c].SupplyPerDay * StepSeconds / _economy.DayLength;
                if (perStep > 0f) station.Waiting[c] = MathF.Min(station.Waiting[c] + perStep, _cargo[c].MaxWaiting);
            }
        }
    }

    ulong StationHash()
    {
        ulong h = (ulong)_stations.Count;
        foreach (var station in _stations)
        {
            h = h * 31 + (ulong)station.Node;
            if (station.DockKind != 0) h = h * 31 + (ulong)station.DockKind * 977UL + (ulong)station.DockId;
            if (station.Mode == StationMode.Rail) h = h * 31 + 13UL + (ulong)station.RailNode;
            h = h * 31 + (station.Active ? 1UL : 0UL);
            if (station.HasBays) h = h * 31 + 7UL;
            foreach (float w in station.Waiting) h = h * 31 + BitConverter.SingleToUInt32Bits(w);
        }
        return h;
    }
}
