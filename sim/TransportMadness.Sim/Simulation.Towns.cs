namespace TransportMadness.Sim;

/// <summary>Towns and their buildings: access to roads, abandonment, demolition for roads, and what a station collects from the buildings it covers.</summary>
public sealed partial class Simulation
{
    const float BuildingCellSize = 128f;

    readonly TownsTuning _townTuning;
    readonly List<Town> _towns = new();
    readonly List<Building> _buildings = new();
    readonly Dictionary<long, List<int>> _buildingCells = new();
    readonly int _passengerCargo, _goodsCargo; // indices into the cargo list, -1 when the roster has none
    RoadIndex? _roadIndex;
    // What each station collects per second from the buildings in its coverage, by cargo; refreshed when buildings or stations change.
    readonly List<float[]> _stationRate = new();
    bool _coverageDirty = true;

    public IReadOnlyList<Town> Towns => _towns;
    /// <summary>Every building ever made, in id order; demolished ones stay in the list in state <see cref="BuildingState.Demolished"/>.</summary>
    public IReadOnlyList<Building> Buildings => _buildings;
    public TownsTuning TownTuning => _townTuning;

    /// <summary>People and jobs in a town, and its buildings by state.</summary>
    public readonly record struct TownStats(int Population, int Jobs, int Homes, int Commerce, int Industry, int Inactive, int Abandoned);

    static int FindCargo(Tuning tuning, string id)
    {
        for (int c = 0; c < tuning.Cargo.Count; c++)
            if (tuning.Cargo[c].Id == id) return c;
        return -1;
    }

    /// <summary>
    /// Puts the generated towns into the world. Part of setting the world up, like the first roads: it is done once,
    /// before the first step, and not recorded in the command log (a replay generates the same towns from the same seed).
    /// </summary>
    public void AddTowns(WorldPlan plan)
    {
        if (Tick != 0 || _towns.Count != 0) throw new InvalidOperationException("Towns are added once, before the simulation runs.");
        SyncNetwork();
        foreach (var townPlan in plan.Towns)
        {
            var town = new Town(_towns.Count, townPlan.Name, townPlan.X, townPlan.Z) { Satisfaction = _townTuning.InitialSatisfaction };
            _towns.Add(town);
            foreach (var b in townPlan.Buildings)
            {
                float y = _net.Terrain?.HeightAt(b.X, b.Z) ?? 0f;
                var building = new Building(_buildings.Count, town.Id, b.Type, b.Tier, b.X, y, b.Z, b.Facing);
                _buildings.Add(building);
                town.BuildingIds.Add(building.Id);
                CellList(b.X, b.Z, create: true)!.Add(building.Id);
            }
        }
        _coverageDirty = true;
        EvaluateAccess();
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
        return new TownStats(people, jobs, homes, commerce, industry, inactive, abandoned);
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
            foreach (var town in _towns)
                town.Satisfaction = MathF.Min(_townTuning.MaxSatisfaction, town.Satisfaction + _townTuning.RecoveryPerMonth);
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

    // ---- What stations collect -------------------------------------------------------------------------------

    /// <summary>On a map with towns, a station collects passengers and goods from the buildings within its coverage radius.</summary>
    void RefreshCoverage()
    {
        _coverageDirty = false;
        while (_stationRate.Count < _stations.Count) _stationRate.Add(new float[_cargo.Count]);
        for (int s = 0; s < _stations.Count; s++)
        {
            var rate = _stationRate[s];
            Array.Clear(rate);
            var station = _stations[s];
            if (!station.Active) continue;
            var (x, z) = _net.GetNode(station.Node);
            foreach (int id in BuildingsNear(x, z, _stationTuning.CoverageRadius))
            {
                var b = _buildings[id];
                if (b.State != BuildingState.Active) continue;
                var tier = _townTuning.Of(b.Type, b.Tier);
                if (_passengerCargo >= 0) rate[_passengerCargo] += tier.PassengersPerDay / _economy.DayLength;
                if (_goodsCargo >= 0) rate[_goodsCargo] += tier.GoodsPerDay / _economy.DayLength;
            }
        }
    }

    /// <summary>The buildings an active station covers (active ones only), in id order.</summary>
    public List<int> CoveredBy(Station station)
    {
        var (x, z) = _net.GetNode(station.Node);
        return BuildingsNear(x, z, _stationTuning.CoverageRadius).Where(id => _buildings[id].State == BuildingState.Active).ToList();
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
        foreach (var t in _towns) h = h * 31 + BitConverter.SingleToUInt32Bits(t.Satisfaction);
        return h;
    }
}
