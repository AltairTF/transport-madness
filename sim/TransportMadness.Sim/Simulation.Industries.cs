namespace TransportMadness.Sim;

/// <summary>
/// Industries, and the cargo that moves between them, stations and the buildings of towns: production, what a station
/// accepts, where a delivery goes, and what buildings use up each month.
/// </summary>
public sealed partial class Simulation
{
    const int FreightAny = -2; // a vehicle model that carries whichever freight cargo is waiting and wanted

    readonly IndustriesTuning _industryTuning;
    readonly List<Industry> _industries = new();
    // Resolved once from the tuning: indices into the cargo list.
    readonly int[] _industryOutput;
    readonly (int Cargo, float Amount)[][] _industryInputs;
    // Per building type and tier: for every cargo, the monthly need and the most that is kept in stock / for construction.
    readonly float[][][] _sustainNeed;
    readonly float[][][] _constructionCap;
    // Cached with the coverage: what each station reaches and accepts, and which stations reach each industry.
    readonly List<int[]> _stationBuildings = new();
    readonly List<int[]> _stationIndustries = new();
    readonly List<bool[]> _stationAccepts = new();
    List<int[]> _industryStations = new();
    double _productionPerTick; // 1 / ticks in a month

    public IReadOnlyList<Industry> Industries => _industries;
    public IndustriesTuning IndustryTuning => _industryTuning;
    /// <summary>The cargo (index into the cargo list) an industry type makes.</summary>
    public int OutputOf(int industryType) => _industryOutput[industryType];
    /// <summary>Whether no industry type makes a cargo, so that it can only come from outside the region (oil, rubber): it needs a gateway.</summary>
    public bool IsImported(int cargo) => cargo != _passengerCargo && IndustryGenerator.IsImported(_industryTuning, _cargo[cargo].Id);

    /// <summary>
    /// What an industry is doing now, for the Industries window: whether a station covers it (without one its output piles up
    /// and nothing reaches it), whether its output is full, and which inputs it has less than a unit of.
    /// </summary>
    public IndustryStatus StatusOf(Industry industry)
    {
        if (_coverageDirty) RefreshCoverage();
        var type = _industryTuning.Types[industry.Type];
        var missing = new List<int>();
        foreach (var (cargo, amount) in _industryInputs[industry.Type])
            if (industry.Input[cargo] < amount) missing.Add(cargo);
        return new IndustryStatus(_industryStations[industry.Id].Length > 0, type.Supply <= 0f && industry.Output >= type.MaxOutput - 0.5f, missing, Math.Max(0L, industry.DamagedUntil - Tick));
    }

    /// <summary>Changes whenever industries are added (for a renderer).</summary>
    public int IndustriesVersion { get; private set; }

    /// <summary>Whether there is anything on the map to deliver to: towns or industries. Without, every station takes everything and produces a fixed amount.</summary>
    bool Settled => _buildings.Count > 0 || _industries.Count > 0 || _gateways.Count > 0;

    static (int Cargo, float Amount)[][] ResolveIndustryInputs(Tuning tuning)
    {
        var all = new (int, float)[tuning.Industries.Types.Count][];
        for (int t = 0; t < all.Length; t++)
            all[t] = tuning.Industries.Types[t].Inputs.Select(a => (CargoIndex(tuning, a.Cargo), a.Amount)).ToArray();
        return all;
    }

    static int CargoIndex(Tuning tuning, string id)
    {
        int c = FindCargo(tuning, id);
        if (c < 0) throw new ArgumentException($"Unknown cargo '{id}' in the towns or industries tuning.");
        return c;
    }

    static float[][][] ResolveNeeds(Tuning tuning, Func<TierTuning, IReadOnlyList<CargoAmount>> pick, float scale)
    {
        var types = tuning.Towns.BuildingTypes;
        var all = new float[types.Count][][];
        for (int t = 0; t < all.Length; t++)
        {
            all[t] = new float[types[t].Tiers.Count][];
            for (int k = 0; k < all[t].Length; k++)
            {
                all[t][k] = new float[tuning.Cargo.Count];
                foreach (var a in pick(types[t].Tiers[k])) all[t][k][CargoIndex(tuning, a.Cargo)] += a.Amount * scale;
            }
        }
        return all;
    }

    /// <summary>Puts the planned industries into the world (called by <see cref="AddTowns"/>).</summary>
    void AddIndustries(IReadOnlyList<IndustryPlan> plans)
    {
        foreach (var plan in plans)
        {
            float y = _net.Terrain?.HeightAt(plan.X, plan.Z) ?? 0f;
            var industry = new Industry(_industries.Count, plan.Type, plan.X, y, plan.Z, plan.Angle, _cargo.Count);
            _industries.Add(industry);
            SetAccessPoint(industry);
            SetOwnStaff(industry);
        }
        IndustriesVersion++;
        _freeGrid = _industries.Sum(i => _industryTuning.Types[i.Type].Demand) * _power.FreeGridMargin;
    }

    /// <summary>Gives every building its starting stock (called by <see cref="AddTowns"/>).</summary>
    void StockBuilding(Building b)
    {
        b.Sustain = new float[_cargo.Count];
        b.Construction = new float[_cargo.Count];
        var need = _sustainNeed[(int)b.Type][b.Tier];
        for (int c = 0; c < need.Length; c++) b.Sustain[c] = need[c] * _townTuning.InitialStockMonths;
    }

    // ---- Production --------------------------------------------------------------------------------------------

    /// <summary>One step of every industry: make cargo from what it has, then hand its output to the stations that cover it.</summary>
    void UpdateIndustries()
    {
        if (_industries.Count == 0) return;
        if (_coverageDirty) RefreshCoverage();
        UpdatePowerBalance();
        foreach (var industry in _industries)
        {
            var type = _industryTuning.Types[industry.Type];
            bool plant = type.Supply > 0f;
            float full = type.PerMonth * (float)_productionPerTick;
            float make = plant ? full : MathF.Min(full, type.MaxOutput - industry.Output);
            var inputs = _industryInputs[industry.Type];
            foreach (var (cargo, amount) in inputs) make = MathF.Min(make, industry.Input[cargo] / amount);
            if (!plant) make *= _powerFactor;
            if (industry.DamagedUntil > Tick) make = 0f; // a raid has stopped it
            if (type.Jobs > 0) make *= StaffingOf(industry);
            if (_workforceGap > 0f) make *= WorkforceFactor;
            if (make > 0f)
            {
                foreach (var (cargo, amount) in inputs) industry.Input[cargo] -= make * amount;
                if (!plant) industry.Output += make;
                industry.Produced += make;
            }
            if (plant) industry.Load = make > 0f ? make / full : 0f;
            if (type.Jobs > 0 && industry.Input[_workerCargo] > 0f) industry.Input[_workerCargo] *= _staffKeepPerTick;
            if (industry.Output > 0f) ShipOutput(industry);
        }
    }

    /// <summary>Divides an industry's output evenly between the stations that cover it, as far as they have room; the rest stays.</summary>
    void ShipOutput(Industry industry)
    {
        var stations = _industryStations[industry.Id];
        if (stations.Length == 0) return;
        int cargo = _industryOutput[industry.Type];
        float share = industry.Output / stations.Length;
        float limit = _cargo[cargo].MaxWaiting;
        foreach (int s in stations)
        {
            var station = _stations[s];
            float take = MathF.Min(share, limit - station.Waiting[cargo]);
            if (take <= 0f) continue;
            station.Waiting[cargo] += take;
            industry.Output -= take;
        }
    }

    // ---- What a station accepts, and where a delivery goes ---------------------------------------------------

    /// <summary>Whether a station takes a cargo: on a map with towns or industries, only when something it covers uses it.</summary>
    public bool Accepts(Station station, int cargo)
    {
        if (!Settled) return true;
        if (_coverageDirty || _stationAccepts.Count != _stations.Count) RefreshCoverage();
        return _stationAccepts[station.Id][cargo];
    }

    /// <summary>
    /// Delivers up to <paramref name="units"/> of a cargo to the buildings and industries a station covers that have
    /// room for it, split by how much room each has. Returns how many units were taken.
    /// </summary>
    internal int Distribute(Station station, int cargo, int units)
    {
        if (_coverageDirty || _stationAccepts.Count != _stations.Count) RefreshCoverage();
        var buildings = _stationBuildings[station.Id];
        var industries = _stationIndustries[station.Id];
        var roomB = new float[buildings.Length];
        var roomI = new float[industries.Length];
        float total = 0f;
        for (int k = 0; k < buildings.Length; k++) total += roomB[k] = BuildingRoom(_buildings[buildings[k]], cargo);
        for (int k = 0; k < industries.Length; k++) total += roomI[k] = IndustryRoom(_industries[industries[k]], cargo);
        // A vehicle's units are whole: when they cover all the room every taker is filled to the brim, so the last fraction of
        // a unit of room (and the float dust of splitting by room) never leaves a stock stuck just short of full.
        int given = 0;
        if (total > 0f)
        {
            given = (int)MathF.Min(units, MathF.Ceiling(total));
            float share = given >= total ? 1f : given / total;
            for (int k = 0; k < buildings.Length; k++)
                if (roomB[k] > 0f)
                {
                    GiveToBuilding(_buildings[buildings[k]], cargo, roomB[k] * share);
                    if (_liveOrders.Count > 0) NoteOrderCivil(cargo, _buildings[buildings[k]].Town, roomB[k] * share);
                }
            for (int k = 0; k < industries.Length; k++)
                if (roomI[k] > 0f) _industries[industries[k]].Input[cargo] += roomI[k] * share;
        }
        // What the buildings and industries here cannot use goes out of the Region when a gateway is covered.
        return given + Export(station, cargo, units - given);
    }

    float BuildingRoom(Building b, int cargo)
    {
        var need = _sustainNeed[(int)b.Type][b.Tier][cargo];
        float room = need > 0f ? need * _townTuning.SustainStockMonths - b.Sustain[cargo] : 0f;
        room += _constructionCap[(int)b.Type][b.Tier][cargo] - b.Construction[cargo];
        return MathF.Max(0f, room);
    }

    float IndustryRoom(Industry industry, int cargo)
    {
        if (cargo == _workerCargo) return MathF.Max(0f, _industryTuning.Types[industry.Type].Jobs - industry.Input[cargo]);
        foreach (var (input, _) in _industryInputs[industry.Type])
            if (input == cargo) return MathF.Max(0f, _industryTuning.Types[industry.Type].MaxInput - industry.Input[cargo]);
        return 0f;
    }

    /// <summary>The monthly needs fill first, then the construction stock.</summary>
    void GiveToBuilding(Building b, int cargo, float amount)
    {
        var need = _sustainNeed[(int)b.Type][b.Tier][cargo];
        if (need > 0f)
        {
            float toSustain = MathF.Min(amount, need * _townTuning.SustainStockMonths - b.Sustain[cargo]);
            if (toSustain > 0f) { b.Sustain[cargo] += toSustain; amount -= toSustain; }
        }
        b.Construction[cargo] += amount;
    }

    /// <summary>At the turn of a month every standing building uses up its needs; the town minds what was missing.</summary>
    void ConsumeNeeds()
    {
        var needed = new float[_towns.Count];
        var unmet = new float[_towns.Count];
        foreach (var b in _buildings)
        {
            if (b.State != BuildingState.Active) continue;
            var need = _sustainNeed[(int)b.Type][b.Tier];
            for (int c = 0; c < need.Length; c++)
            {
                if (need[c] <= 0f) continue;
                float used = MathF.Min(b.Sustain[c], need[c]);
                b.Sustain[c] -= used;
                needed[b.Town] += need[c];
                unmet[b.Town] += need[c] - used;
            }
        }
        foreach (var town in _towns)
        {
            town.UnmetShare = needed[town.Id] > 0f ? unmet[town.Id] / needed[town.Id] : 0f;
            Dissatisfy(town.Id, _townTuning.UnmetPenalty * town.UnmetShare);
        }
    }

    ulong IndustryHash()
    {
        ulong h = (ulong)_industries.Count;
        foreach (var industry in _industries)
        {
            h = h * 31 + BitConverter.SingleToUInt32Bits(industry.Output);
            h = h * 31 + BitConverter.SingleToUInt32Bits(industry.Load);
            foreach (float f in industry.Input) h = h * 31 + BitConverter.SingleToUInt32Bits(f);
        }
        h = h * 31 + BitConverter.SingleToUInt32Bits(_freeGrid) + (ulong)Company.PowerPurchase;
        foreach (var b in _buildings)
        {
            foreach (float f in b.Sustain) h = h * 31 + BitConverter.SingleToUInt32Bits(f);
            foreach (float f in b.Construction) h = h * 31 + BitConverter.SingleToUInt32Bits(f);
        }
        return h;
    }
}

/// <summary>What an industry is doing: <paramref name="Covered"/> by a station, <paramref name="OutputFull"/> (it has stopped), and the cargo (indices) it has too little of to make a unit.</summary>
public readonly record struct IndustryStatus(bool Covered, bool OutputFull, IReadOnlyList<int> MissingInputs, long DamagedTicksLeft = 0)
{
    public bool Damaged => DamagedTicksLeft > 0;
    public bool Working => !OutputFull && MissingInputs.Count == 0 && !Damaged;
}
