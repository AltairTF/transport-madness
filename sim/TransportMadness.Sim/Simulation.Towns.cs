namespace TransportMadness.Sim;

/// <summary>Towns and their buildings: access to roads, abandonment, demolition for roads, and what a station collects from the buildings it covers.</summary>
public sealed partial class Simulation
{
    const float BuildingCellSize = 128f;

    readonly TownsTuning _townTuning;
    readonly List<Town> _towns = new();
    readonly List<Building> _buildings = new();
    readonly Dictionary<long, List<int>> _buildingCells = new();
    readonly int _passengerCargo; // index into the cargo list, -1 when the roster has none
    bool _worldAdded;
    RoadIndex? _roadIndex;
    bool _coverageDirty = true;

    public IReadOnlyList<Town> Towns => _towns;
    /// <summary>Every building ever made, in id order; demolished ones stay in the list in state <see cref="BuildingState.Demolished"/>.</summary>
    public IReadOnlyList<Building> Buildings => _buildings;
    public TownsTuning TownTuning => _townTuning;
    /// <summary>Changes whenever any building changes state (for a renderer to know when to redraw).</summary>
    public int BuildingsVersion { get; private set; }

    /// <summary>People and jobs in a town, and its buildings by state.</summary>
    public readonly record struct TownStats(int Population, int Jobs, int Homes, int Commerce, int Industry, int Inactive, int Abandoned);

    static int FindCargo(Tuning tuning, string id)
    {
        for (int c = 0; c < tuning.Cargo.Count; c++)
            if (tuning.Cargo[c].Id == id) return c;
        return -1;
    }

    /// <summary>
    /// Puts the generated towns and industries into the world. Part of setting the world up, like the first roads: it is
    /// done once, before the first step, and not recorded in the command log (a replay generates the same ones from the
    /// same seed).
    /// </summary>
    public void AddTowns(WorldPlan plan)
    {
        if (Tick != 0 || _worldAdded) throw new InvalidOperationException("Towns are added once, before the simulation runs.");
        _worldAdded = true;
        SyncNetwork();
        foreach (var townPlan in plan.Towns)
        {
            var town = new Town(_towns.Count, townPlan.Name, townPlan.X, townPlan.Z) { Satisfaction = _townTuning.InitialSatisfaction };
            _towns.Add(town);
            foreach (var b in townPlan.Buildings)
            {
                float y = _net.Terrain?.HeightAt(b.X, b.Z) ?? 0f;
                var building = new Building(_buildings.Count, town.Id, b.Type, b.Tier, b.X, y, b.Z, b.Facing);
                StockBuilding(building);
                _buildings.Add(building);
                town.BuildingIds.Add(building.Id);
                CellList(b.X, b.Z, create: true)!.Add(building.Id);
            }
            InitLots(town, townPlan);
        }
        if (plan.Industries != null) AddIndustries(plan.Industries);
        if (plan.Gateways != null) AddGateways(plan.Gateways);
        _coverageDirty = true;
        BuildingsVersion++;
        EvaluateAccess();
        CreateTripGroups();
    }

    static long CellKey(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
    static int CellOf(float v) => (int)MathF.Floor(v / BuildingCellSize);

    List<int>? CellList(float x, float z, bool create)
    {
        long key = CellKey(CellOf(x), CellOf(z));
        if (_buildingCells.TryGetValue(key, out var list)) return list;
        if (!create) return null;
        return _buildingCells[key] = new List<int>();
    }

    /// <summary>Buildings that stand (not demolished) with their middle within <paramref name="radius"/> of a point, in id order.</summary>
    public List<int> BuildingsNear(float x, float z, float radius)
    {
        var found = new List<int>();
        for (int cx = CellOf(x - radius); cx <= CellOf(x + radius); cx++)
            for (int cz = CellOf(z - radius); cz <= CellOf(z + radius); cz++)
            {
                if (!_buildingCells.TryGetValue(CellKey(cx, cz), out var list)) continue;
                foreach (int id in list)
                {
                    var b = _buildings[id];
                    if (b.State != BuildingState.Demolished && (b.X - x) * (b.X - x) + (b.Z - z) * (b.Z - z) <= radius * radius) found.Add(id);
                }
            }
        found.Sort();
        return found;
    }

    /// <summary>What the active buildings of a town lack of one cargo: how many are short of this month's need, the units that need lacks, and the units of construction stock missing for growth.</summary>
    public readonly record struct CargoNeed(int Cargo, int ShortBuildings, float MissingMonthly, float MissingConstruction);

    /// <summary>What a town still needs and how much of it a station of the company reaches: its active buildings, and how many of them some active station covers.</summary>
    public readonly record struct TownNeeds(IReadOnlyList<CargoNeed> Cargo, int Buildings, int Covered);

    /// <summary>The cargo a town's buildings are short of (this month's needs and the stock for growing) and how much of the town the company's stations cover.</summary>
    public TownNeeds NeedsOf(int town)
    {
        if (_coverageDirty || _buildingStations.Count != _buildings.Count) RefreshCoverage();
        var short_ = new int[_cargo.Count];
        var monthly = new float[_cargo.Count];
        var construction = new float[_cargo.Count];
        var wanted = new bool[_cargo.Count];
        int active = 0, covered = 0;
        foreach (int id in _towns[town].BuildingIds)
        {
            var b = _buildings[id];
            if (b.State != BuildingState.Active) continue;
            active++;
            if (id < _buildingStations.Count && _buildingStations[id].Length > 0) covered++;
            var need = _sustainNeed[(int)b.Type][b.Tier];
            var cap = _constructionCap[(int)b.Type][b.Tier];
            float scale = _towns[town].CapacityFactor;
            for (int c = 0; c < _cargo.Count; c++)
            {
                if (need[c] > 0f)
                {
                    wanted[c] = true;
                    float lack = need[c] * scale - b.Sustain[c];
                    if (lack > 0f) { short_[c]++; monthly[c] += lack; }
                }
                if (cap[c] > 0f)
                {
                    wanted[c] = true;
                    construction[c] += Math.Max(0f, cap[c] - b.Construction[c]);
                }
            }
        }
        var list = new List<CargoNeed>();
        for (int c = 0; c < _cargo.Count; c++)
            if (wanted[c]) list.Add(new CargoNeed(c, short_[c], monthly[c], construction[c]));
        return new TownNeeds(list, active, covered);
    }

    /// <summary>A number of people or jobs of the nominal capacity, as much as a town with this capacity factor has room for.</summary>
    static int ScaledBy(int amount, float factor) => factor >= 1f ? amount : (int)MathF.Round(amount * factor);

    /// <summary>The people (or jobs) of a size of a building of this town, lowered by what the call-up has taken for good (C15c).</summary>
    int Scaled(Building b, int amount) => ScaledBy(amount, _towns[b.Town].CapacityFactor);

    public TownStats StatsOf(int town)
    {
        int people = 0, jobs = 0, homes = 0, commerce = 0, industry = 0, inactive = 0, abandoned = 0;
        foreach (int id in _towns[town].BuildingIds)
        {
            var b = _buildings[id];
            switch (b.State)
            {
                case BuildingState.Demolished: continue;
                case BuildingState.Inactive: inactive++; continue;
                case BuildingState.Abandoned: abandoned++; continue;
            }
            var tier = _townTuning.Of(b.Type, b.Tier);
            people += tier.People;
            jobs += tier.Jobs;
            switch (b.Type)
            {
                case BuildingType.Home: homes++; break;
                case BuildingType.Commerce: commerce++; break;
                default: industry++; break;
            }
        }
        float capacity = _towns[town].CapacityFactor; // what the call-up has left of the nominal room (C15c)
        return new TownStats(ScaledBy(people, capacity), ScaledBy(jobs, capacity), homes, commerce, industry, inactive, abandoned);
    }

    // ---- Road access, abandonment ------------------------------------------------------------------------------

    /// <summary>
    /// Sets each building's state from whether a road is near it. Run whenever the network changes: a building that
    /// loses its road goes inactive, one that gets a road back (in time) is active again.
    /// </summary>
    void EvaluateAccess()
    {
        if (_buildings.Count == 0) return;
        _roadIndex = new RoadIndex(_net);
        foreach (var b in _buildings)
        {
            if (b.State == BuildingState.Demolished) continue;
            bool access = _roadIndex.HasRoadWithin(b.X, b.Z, _townTuning.AccessRadius);
            if (access && b.State != BuildingState.Active) SetState(b, BuildingState.Active);
            else if (!access && b.State == BuildingState.Active) SetState(b, BuildingState.Inactive);
        }
    }

    void SetState(Building b, BuildingState state)
    {
        b.State = state;
        b.StateSince = Day;
        _coverageDirty = true;
        BuildingsVersion++;
    }

    /// <summary>The turn of a day for the towns: buildings without a road age, and each month the towns recover some satisfaction.</summary>
    void CloseDayForTowns()
    {
        if (_buildings.Count == 0) return;
        long inactiveDays = (long)_townTuning.InactiveMonths * _economy.DaysPerMonth;
        long abandonedDays = (long)_townTuning.AbandonedMonths * _economy.DaysPerMonth;
        foreach (var b in _buildings)
        {
            long age = Day - b.StateSince;
            if (b.State == BuildingState.Inactive && age >= inactiveDays)
            {
                SetState(b, BuildingState.Abandoned);
                Dissatisfy(b.Town, _townTuning.AbandonedPenalty);
            }
            else if (b.State == BuildingState.Abandoned && age >= abandonedDays)
                Demolish(b);
        }
        if ((Day + 1) % _economy.DaysPerMonth == 0)
        {
            ConsumeNeeds();
            EvaluateConnectivity();
            GrowTowns();
            foreach (var town in _towns)
                town.Satisfaction = MathF.Min(_townTuning.MaxSatisfaction, town.Satisfaction + _townTuning.RecoveryPerMonth);
        }
    }

    void Dissatisfy(int town, float amount)
    {
        var t = _towns[town];
        t.Satisfaction = MathF.Max(0f, t.Satisfaction - amount);
    }

    void Demolish(Building b)
    {
        SetState(b, BuildingState.Demolished);
        CellList(b.X, b.Z, create: false)?.Remove(b.Id);
        if (b.Lot >= 0) _towns[b.Town].Lots[b.Lot].Building = -1;
    }

    // ---- Roads through buildings -------------------------------------------------------------------------------

    /// <summary>What it would cost the player to make room: the buildings a road would run through and the price of demolishing them.</summary>
    public readonly record struct Demolition(int Count, long Price)
    {
        public static readonly Demolition None = new(0, 0);
    }

    /// <summary>Buildings under a road's paved width (and its own footprint), by id.</summary>
    List<int> BuildingsInTheWay(IEnumerable<(RoadGeometry Geometry, RoadType Type)> roads)
    {
        var hit = new SortedSet<int>();
        if (_buildings.Count == 0) return new List<int>();
        const float step = 20f;
        float widest = _townTuning.Of(BuildingType.Home, TownsTuning.Tiers - 1).Footprint;
        foreach (var type in new[] { BuildingType.Commerce, BuildingType.Industry })
            widest = MathF.Max(widest, _townTuning.Of(type, TownsTuning.Tiers - 1).Footprint);
        foreach (var (g, type) in roads)
        {
            float reach = RoadNetwork.HalfWidth(type) + widest;
            int steps = Math.Max(1, (int)MathF.Ceiling(g.Length / step));
            for (int k = 0; k <= steps; k++)
            {
                var (x, z) = g.PointAt(g.Length * k / steps);
                foreach (int id in BuildingsNear(x, z, reach + step))
                {
                    var b = _buildings[id];
                    if (g.DistanceTo(b.X, b.Z) < RoadNetwork.HalfWidth(type) + _townTuning.Of(b.Type, b.Tier).Footprint) hit.Add(id);
                }
            }
        }
        return hit.ToList();
    }

    Demolition DemolitionOf(List<int> ids)
    {
        long price = 0;
        foreach (int id in ids) price += _townTuning.Of(_buildings[id].Type, _buildings[id].Tier).DemolitionPrice;
        return new Demolition(ids.Count, price);
    }

    /// <summary>The buildings a new road from (x0, z0) to (x1, z1) would demolish and what that costs, on top of the road's own price.</summary>
    public Demolition EstimateRoadDemolition(float x0, float z0, float x1, float z1, RoadType type, float sweep = 0f) =>
        DemolitionOf(BuildingsInTheWay(new[] { (RoadGeometry.FromChord(x0, z0, x1, z1, sweep), type) }));

    /// <summary>The same for dragging a node to a new place (the roads at it are rebuilt).</summary>
    public Demolition EstimateMoveDemolition(int node, float x, float z) =>
        _net.PlannedMoveRoads(node, x, z) is { } roads ? DemolitionOf(BuildingsInTheWay(roads)) : Demolition.None;

    /// <summary>Takes the buildings down, charges the price and lowers the satisfaction of their towns.</summary>
    void DemolishForRoad(List<int> ids)
    {
        if (ids.Count == 0) return;
        Charge(DemolitionOf(ids).Price, LedgerEntry.Demolition);
        foreach (int id in ids)
        {
            var b = _buildings[id];
            Demolish(b);
            Dissatisfy(b.Town, _townTuning.DemolishedPenalty);
        }
    }

    // ---- What stations cover, collect and accept -------------------------------------------------------------

    /// <summary>
    /// Works out, for every station, the standing buildings and the industries within its coverage radius and the cargo it
    /// accepts, and for every building the stations that cover it (where its people can catch a bus).
    /// </summary>
    void RefreshCoverage()
    {
        _coverageDirty = false;
        _transitVersion++;
        var perBuilding = new List<int>?[_buildings.Count];
        while (_stationBuildings.Count < _stations.Count)
        {
            _stationBuildings.Add(Array.Empty<int>());
            _stationIndustries.Add(Array.Empty<int>());
            _stationGateways.Add(Array.Empty<int>());
            _stationAccepts.Add(new bool[_cargo.Count]);
        }
        var perIndustry = new List<int>[_industries.Count];
        for (int i = 0; i < perIndustry.Length; i++) perIndustry[i] = new List<int>();
        var perGateway = new List<int>[_gateways.Count];
        for (int i = 0; i < perGateway.Length; i++) perGateway[i] = new List<int>();
        float radius = _stationTuning.CoverageRadius;
        for (int s = 0; s < _stations.Count; s++)
        {
            var accepts = _stationAccepts[s];
            Array.Clear(accepts);
            _stationBuildings[s] = Array.Empty<int>();
            _stationIndustries[s] = Array.Empty<int>();
            _stationGateways[s] = Array.Empty<int>();
            var station = _stations[s];
            if (!station.Active) continue;
            var (x, z) = PointOf(station);

            if (station.DockKind != 0)
            {
                // A stop at an industry or a gateway covers that one place, and nothing around it.
                var covered = new List<int>();
                if (station.DockKind == 1)
                {
                    var industry = _industries[station.DockId];
                    covered.Add(industry.Id);
                    perIndustry[industry.Id].Add(s);
                    foreach (var (cargo, _) in _industryInputs[industry.Type]) accepts[cargo] = true;
                    if (_passengerCargo >= 0 && _industryTuning.Types[industry.Type].Jobs > 0) accepts[_passengerCargo] = true;
                    _stationIndustries[s] = covered.ToArray();
                }
                else
                {
                    var gateway = _gateways[station.DockId];
                    covered.Add(gateway.Id);
                    perGateway[gateway.Id].Add(s);
                    if (gateway.DamagedUntil <= Tick)
                        for (int c = 0; c < accepts.Length; c++)
                            if (_exportable[c]) accepts[c] = true;
                    _stationGateways[s] = covered.ToArray();
                }
                continue;
            }

            var buildings = new List<int>();
            foreach (int id in BuildingsNear(x, z, radius))
            {
                var b = _buildings[id];
                if (b.State != BuildingState.Active) continue;
                buildings.Add(id);
                (perBuilding[id] ??= new List<int>()).Add(s);
                if (_passengerCargo >= 0) accepts[_passengerCargo] = true;
                var sustain = _sustainNeed[(int)b.Type][b.Tier];
                var construction = _constructionCap[(int)b.Type][b.Tier];
                for (int c = 0; c < accepts.Length; c++)
                    if (sustain[c] > 0f || construction[c] > 0f) accepts[c] = true;
            }
            var industries = new List<int>();
            foreach (var industry in _industries)
            {
                if ((industry.X - x) * (industry.X - x) + (industry.Z - z) * (industry.Z - z) > radius * radius) continue;
                industries.Add(industry.Id);
                perIndustry[industry.Id].Add(s);
                foreach (var (cargo, _) in _industryInputs[industry.Type]) accepts[cargo] = true;
                if (_passengerCargo >= 0 && _industryTuning.Types[industry.Type].Jobs > 0) accepts[_passengerCargo] = true; // its people come by the lines
            }
            var gateways = new List<int>();
            var mode = station.Mode == StationMode.Rail ? GatewayKind.Rail : GatewayKind.Road;
            foreach (var gateway in _gateways)
            {
                if (gateway.Kind != mode || (gateway.X - x) * (gateway.X - x) + (gateway.Z - z) * (gateway.Z - z) > radius * radius) continue;
                gateways.Add(gateway.Id);
                perGateway[gateway.Id].Add(s);
                if (gateway.DamagedUntil > Tick) continue; // shut by a raid: it takes nothing
                for (int c = 0; c < accepts.Length; c++)
                    if (_exportable[c]) accepts[c] = true;
            }
            _stationBuildings[s] = buildings.ToArray();
            _stationIndustries[s] = industries.ToArray();
            _stationGateways[s] = gateways.ToArray();
        }
        _gatewayStations = perGateway.Select(l => l.ToArray()).ToList();
        _industryStations = perIndustry.Select(l => l.ToArray()).ToList();
        _buildingStations = perBuilding.Select(l => l?.ToArray() ?? Array.Empty<int>()).ToList();
        RefreshReach();
    }

    /// <summary>The buildings an active station covers (active ones only), in id order.</summary>
    public List<int> CoveredBy(Station station)
    {
        if (station.DockKind != 0) return new List<int>(); // the stop at an industry covers that industry, not the buildings around
        var (x, z) = PointOf(station);
        return BuildingsNear(x, z, _stationTuning.CoverageRadius).Where(id => _buildings[id].State == BuildingState.Active).ToList();
    }

    /// <summary>The industries whose middle lies within <paramref name="radius"/> of a point, by id.</summary>
    public List<int> IndustriesNear(float x, float z, float radius) =>
        _industries.Where(i => (i.X - x) * (i.X - x) + (i.Z - z) * (i.Z - z) <= radius * radius).Select(i => i.Id).ToList();

    /// <summary>The industries within the coverage radius of a station, by id.</summary>
    public IReadOnlyList<int> IndustriesCoveredBy(Station station)
    {
        if (_coverageDirty || _stationBuildings.Count != _stations.Count) RefreshCoverage();
        return _stationIndustries[station.Id];
    }

    ulong TownHash()
    {
        ulong h = (ulong)_buildings.Count;
        foreach (var b in _buildings)
        {
            h = h * 31 + (ulong)b.State;
            h = h * 31 + (ulong)b.Tier;
            h = h * 31 + (ulong)b.StateSince;
        }
        foreach (var t in _towns)
        {
            h = h * 31 + BitConverter.SingleToUInt32Bits(t.Satisfaction);
            h = h * 31 + BitConverter.SingleToUInt32Bits(t.Connectivity);
        }
        return h;
    }

    /// <summary>
    /// Whether a road lies in a built-up area: its middle is within the reach of a town. Such a street has a kerb and pavements; a road outside
    /// has an earth shoulder. The change comes at a junction at the edge of the town, as streets are a block long.
    /// </summary>
    public bool IsUrbanRoad(RoadId id)
    {
        var road = _net.GetRoad(id);
        var (x, z) = road.Geometry.PointAt(road.Length * 0.5f);
        foreach (var town in _towns)
            if (town.Radius > 0f && (town.X - x) * (town.X - x) + (town.Z - z) * (town.Z - z) <= town.Radius * town.Radius) return true;
        return false;
    }
}
