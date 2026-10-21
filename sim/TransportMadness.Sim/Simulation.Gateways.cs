namespace TransportMadness.Sim;

/// <summary>
/// The Region's links with the rest of the world (C3): imported cargo arrives at a gateway as stock for stations of its mode to
/// collect, and cargo a vehicle unloads at a gateway is exported.
/// </summary>
public sealed partial class Simulation
{
    readonly GatewaysTuning _gatewayTuning;
    readonly List<Gateway> _gateways = new();
    // Resolved once from the tuning: for every import offer the index of its cargo, and for every cargo whether a gateway takes it.
    readonly int[] _importCargo;
    readonly bool[] _exportable;
    readonly long _shipmentTicks, _intervalTicks;
    // Cached with the coverage: which gateways each station reaches (its own mode only) and which stations reach each gateway.
    readonly List<int[]> _stationGateways = new();
    List<int[]> _gatewayStations = new();

    public IReadOnlyList<Gateway> Gateways => _gateways;
    public GatewaysTuning GatewayTuning => _gatewayTuning;
    /// <summary>The import offers of the tuning, in order; <see cref="ImportCargoOf"/> gives the cargo of each.</summary>
    public IReadOnlyList<ImportTuning> ImportOffers => _gatewayTuning.Imports;
    /// <summary>The cargo (index into the cargo list) of the import offer at <paramref name="offer"/>.</summary>
    public int ImportCargoOf(int offer) => _importCargo[offer];
    /// <summary>Whether gateways take a cargo from vehicles (an export).</summary>
    public bool IsExportable(int cargo) => _exportable[cargo];
    /// <summary>Changes whenever gateways are added (for a renderer).</summary>
    public int GatewaysVersion { get; private set; }

    /// <summary>Puts the planned gateways into the world (called by <see cref="AddTowns"/>).</summary>
    void AddGateways(IReadOnlyList<GatewayPlan> plans)
    {
        foreach (var plan in plans)
        {
            float y = _net.Terrain?.HeightAt(plan.X, plan.Z) ?? 0f;
            var gateway = new Gateway(_gateways.Count, plan, y, _cargo.Count);
            (gateway.AccessX, gateway.AccessZ) = AccessPointOf(gateway.X, gateway.Z, gateway.Angle, _gatewayTuning.Size);
            _gateways.Add(gateway);
        }
        GatewaysVersion++;
    }

    /// <summary>The gateways of one kind whose middle lies within <paramref name="radius"/> of a point, by id.</summary>
    public List<int> GatewaysNear(float x, float z, float radius, GatewayKind kind) =>
        _gateways.Where(g => g.Kind == kind && (g.X - x) * (g.X - x) + (g.Z - z) * (g.Z - z) <= radius * radius).Select(g => g.Id).ToList();

    /// <summary>The gateways a station covers (those of its own mode within the coverage radius), by id.</summary>
    public IReadOnlyList<int> GatewaysCoveredBy(Station station)
    {
        if (_coverageDirty || _stationBuildings.Count != _stations.Count) RefreshCoverage();
        return _stationGateways[station.Id];
    }

    /// <summary>Whether a station of the gateway's own mode covers it, so that its stock can be collected and cargo delivered.</summary>
    public bool IsCovered(Gateway gateway)
    {
        if (_coverageDirty || _stationBuildings.Count != _stations.Count) RefreshCoverage();
        return _gatewayStations[gateway.Id].Length > 0;
    }

    // ---- Import shipments -------------------------------------------------------------------------------------

    /// <summary>
    /// Why a shipment of a cargo cannot be asked for at a gateway now, null when it can: no such gateway or offer, one is already on its way,
    /// it was asked for too recently, the gateway has no room, or the money is not there.
    /// </summary>
    public string? WhyNotImport(int gateway, string cargo)
    {
        if (gateway < 0 || gateway >= _gateways.Count) return "No such gateway";
        int c = FindCargo(_net.Tuning, cargo);
        var offer = c < 0 ? null : _gatewayTuning.Imports.FirstOrDefault(i => i.Cargo == cargo);
        if (offer == null) return "The government does not send that";
        var g = _gateways[gateway];
        if (g.ArrivesAt[c] >= 0) return "A shipment is already on its way";
        if (Tick < g.AvailableAt[c]) return "Asked for too recently";
        if (g.Stock[c] + ImportUnits(offer) > _gatewayTuning.StockLimit) return "The gateway has no room for it";
        if (!CanAfford((long)ImportUnits(offer) * ImportPrice(offer))) return "Not enough money";
        return null;
    }

    int RequestImport(int gateway, string cargo)
    {
        if (WhyNotImport(gateway, cargo) != null) return 0;
        var offer = _gatewayTuning.Imports.First(i => i.Cargo == cargo);
        int c = FindCargo(_net.Tuning, cargo);
        Charge((long)ImportUnits(offer) * ImportPrice(offer), LedgerEntry.Imports);
        _gateways[gateway].ArrivesAt[c] = Tick + _shipmentTicks;
        _gateways[gateway].ShipmentUnits[c] = ImportUnits(offer);
        return 1;
    }

    // ---- Each step ---------------------------------------------------------------------------------------------

    /// <summary>One step of every gateway: land the shipments that are due, then hand the stock to the stations that cover it.</summary>
    void UpdateGateways()
    {
        if (_gateways.Count == 0) return;
        if (_coverageDirty) RefreshCoverage();
        foreach (var g in _gateways)
        {
            foreach (var offer in _gatewayTuning.Imports)
            {
                int c = FindCargo(_net.Tuning, offer.Cargo);
                if (g.ArrivesAt[c] < 0 || Tick < g.ArrivesAt[c] || g.DamagedUntil > Tick) continue;
                int units = g.ShipmentUnits[c] > 0 ? g.ShipmentUnits[c] : offer.Units;
                g.Stock[c] += units;
                g.Imported[c] += units;
                g.ArrivesAt[c] = -1;
                g.ShipmentUnits[c] = 0;
                g.AvailableAt[c] = Tick + _intervalTicks;
                AddEvent(SimEventKind.ShipmentArrived, EventSeverity.Info, g.X, g.Z, g.Id, $"{units} {_cargo[c].Name.ToLowerInvariant()} arrived at the {g.Name}");
            }
            var stations = _gatewayStations[g.Id];
            if (stations.Length == 0) continue;
            for (int c = 0; c < g.Stock.Length; c++)
            {
                if (g.Stock[c] <= 0f) continue;
                float share = g.Stock[c] / stations.Length, limit = _cargo[c].MaxWaiting;
                foreach (int s in stations)
                {
                    var station = _stations[s];
                    float take = MathF.Min(share, limit - station.Waiting[c]);
                    if (take <= 0f) continue;
                    station.Waiting[c] += take;
                    g.Stock[c] -= take;
                }
            }
        }
    }

    /// <summary>Takes up to <paramref name="units"/> of a cargo a station has unloaded as exports, split between the gateways it covers. Returns how many.</summary>
    int Export(Station station, int cargo, int units)
    {
        var gateways = _stationGateways[station.Id];
        if (gateways.Length == 0 || !_exportable[cargo] || units <= 0 || gateways.All(id => _gateways[id].DamagedUntil > Tick)) return 0;
        for (int k = 0; k < gateways.Length; k++)
        {
            _gateways[gateways[k]].Exported[cargo] += (double)units / gateways.Length;
            NoteOrderExport(cargo, gateways[k], (double)units / gateways.Length);
        }
        NoteExport(cargo, units);
        return units;
    }

    ulong GatewayHash()
    {
        ulong h = (ulong)_gateways.Count;
        foreach (var g in _gateways)
        {
            foreach (float f in g.Stock) h = h * 31 + BitConverter.SingleToUInt32Bits(f);
            foreach (double d in g.Imported) h = h * 31 + BitConverter.DoubleToUInt64Bits(d);
            foreach (double d in g.Exported) h = h * 31 + BitConverter.DoubleToUInt64Bits(d);
            foreach (long t in g.ArrivesAt) h = h * 31 + (ulong)(t + 1);
            foreach (long t in g.AvailableAt) h = h * 31 + (ulong)t;
            foreach (int u in g.ShipmentUnits) if (u > 0) h = h * 31 + (ulong)u;
        }
        return h;
    }
}
