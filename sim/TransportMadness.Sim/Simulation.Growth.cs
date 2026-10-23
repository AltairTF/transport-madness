namespace TransportMadness.Sim;

/// <summary>
/// Growth of towns (M3d). At the turn of each month the share of a town's homes that were reached is worked out
/// (connectivity); a town with enough satisfaction and connectivity then spends the construction stock of its buildings:
/// a building with a full stock goes up a size, or if already the biggest founds a new building on the nearest free lot of
/// its town, or on a new block of streets when no lot is free. A town never shrinks by itself.
/// </summary>
public sealed partial class Simulation
{
    const ulong GrowthSeedMix = 0x47524F57544821A7UL;
    const float LotCrowding = 18f;  // a lot is not built on while a standing building is closer than this (lots are laid a building apart)
    const float SampleSpacing = 30f; // ground is checked this far apart when a town wants a new block
    const float StockSlack = 0.01f;  // a construction stock this close to its cost counts as full (float dust)
    const float StripClearance = 12f; // a new block is refused when a building stands this close to a point of it

    readonly GrowthTuning _growth;
    readonly SimRandom _growRng;

    // ---- Lots ----------------------------------------------------------------------------------------------------

    /// <summary>Gives a town its lots (those of its first grid) and marks the ones its buildings stand on.</summary>
    void InitLots(Town town, TownPlan plan)
    {
        town.Angle = plan.Angle;
        town.Blocks = plan.Blocks;
        town.IMin = town.JMin = 0;
        town.IMax = town.JMax = plan.Blocks - 1;
        town.Radius = plan.Reach(_townTuning.Generation);
        if (plan.Lots != null) // an organic town (R4): the lots the generator laid along its streets
        {
            town.Organic = true;
            foreach (var site in plan.Lots) town.Lots.Add(new Lot(site));
        }
        else
            foreach (var site in TownGenerator.LotSites(_townTuning, plan.X, plan.Z, plan.Angle, plan.Blocks, 0, plan.Blocks - 1, 0, plan.Blocks - 1))
                town.Lots.Add(new Lot(site));
        foreach (int id in town.BuildingIds)
        {
            var b = _buildings[id];
            for (int k = 0; k < town.Lots.Count; k++)
            {
                var site = town.Lots[k].Site;
                if (town.Lots[k].Building >= 0 || (site.X - b.X) * (site.X - b.X) + (site.Z - b.Z) * (site.Z - b.Z) > 1f) continue;
                town.Lots[k].Building = id;
                b.Lot = k;
                break;
            }
        }
    }

    /// <summary>Whether a lot could have a building put on it now: nobody stands there, there is a road to reach it and none through it.</summary>
    internal bool CanBuildOn(Lot lot)
    {
        if (lot.Building >= 0 || _roadIndex == null) return false;
        var s = lot.Site;
        if (_net.Terrain is { } terrain && (MathF.Abs(s.X) > terrain.HalfSize || MathF.Abs(s.Z) > terrain.HalfSize || terrain.IsWater(s.X, s.Z))) return false;
        float road = _roadIndex.DistanceToNearest(s.X, s.Z, _townTuning.AccessRadius);
        if (float.IsPositiveInfinity(road) || road < _growth.LotClearance) return false;
        if (BuildingsNear(s.X, s.Z, LotCrowding).Count > 0) return false;
        foreach (var industry in _industries)
        {
            float reach = _industryTuning.Types[industry.Type].Size + LotCrowding;
            if ((industry.X - s.X) * (industry.X - s.X) + (industry.Z - s.Z) * (industry.Z - s.Z) < reach * reach) return false;
        }
        return true;
    }

    /// <summary>The free lot of the town nearest to a point, or -1.</summary>
    int NearestFreeLot(Town town, float x, float z)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int k = 0; k < town.Lots.Count; k++)
        {
            var s = town.Lots[k].Site;
            float d = (s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z);
            if (d >= bestDistance || !CanBuildOn(town.Lots[k])) continue;
            best = k;
            bestDistance = d;
        }
        return best;
    }

    // ---- The monthly turn ----------------------------------------------------------------------------------------

    /// <summary>The share of each town's homes reached this month, from the trips its groups completed.</summary>
    void EvaluateConnectivity()
    {
        long month = MonthIndex;
        var reached = new HashSet<int>();
        foreach (var g in _groups)
            if (g.ReachedMonth == month) reached.Add(g.Home);
        var homes = new int[_towns.Count];
        var counted = new int[_towns.Count];
        foreach (var b in _buildings)
        {
            if (b.Type != BuildingType.Home || b.State != BuildingState.Active) continue;
            homes[b.Town]++;
            bool was = b.Reached;
            b.Reached = reached.Contains(b.Id);
            if (b.Reached) counted[b.Town]++;
            if (b.Reached != was) BuildingsVersion++; // the connectivity view colours homes by it
        }
        foreach (var town in _towns)
            town.Connectivity = homes[town.Id] > 0 ? (float)counted[town.Id] / homes[town.Id] : 1f;
    }

    void GrowTowns()
    {
        foreach (var town in _towns)
        {
            var blocked = town.Satisfaction < _growth.MinSatisfaction ? GrowthBlock.Satisfaction
                : town.Connectivity < _growth.MinConnectivity ? GrowthBlock.Connectivity
                : GrowthBlock.None;
            if (blocked != GrowthBlock.None) { town.LastGrowth = new GrowthReport(0, 0, 0, blocked); continue; }

            int upgrades = 0, founded = 0, blocks = 0;
            foreach (int id in town.BuildingIds.ToArray()) // buildings founded this month wait for the next
            {
                var b = _buildings[id];
                if (b.State != BuildingState.Active || !HasFullConstructionStock(b)) continue;
                int biggest = b.Lot >= 0 && b.Lot < town.Lots.Count ? town.Lots[b.Lot].Site.MaxTier : TownsTuning.Tiers - 1; // its lot is no wider
                if (b.Tier < biggest)
                {
                    SpendConstruction(b);
                    GrowBuilding(b);
                    upgrades++;
                    continue;
                }
                int lot = NearestFreeLot(town, b.X, b.Z);
                if (lot < 0 && _growth.ExtendGrid && !town.Organic && ExtendGrid(town, b.X, b.Z)) // R5 replaces this for organic towns
                {
                    blocks++;
                    lot = NearestFreeLot(town, b.X, b.Z);
                }
                if (lot < 0) continue;
                SpendConstruction(b);
                FoundBuilding(town, lot);
                founded++;
            }
            town.LastGrowth = new GrowthReport(upgrades, founded, blocks, GrowthBlock.None);
        }
    }

    bool HasFullConstructionStock(Building b)
    {
        var cost = _constructionCap[(int)b.Type][b.Tier];
        bool any = false;
        for (int c = 0; c < cost.Length; c++)
        {
            if (cost[c] <= 0f) continue;
            any = true;
            if (b.Construction[c] < cost[c] - StockSlack) return false;
        }
        return any;
    }

    void SpendConstruction(Building b)
    {
        var cost = _constructionCap[(int)b.Type][b.Tier];
        for (int c = 0; c < cost.Length; c++)
            if (cost[c] > 0f) b.Construction[c] = MathF.Max(0f, b.Construction[c] - cost[c]);
    }

    /// <summary>A building goes up one size; a home gets the people of the new size as new trip groups.</summary>
    void GrowBuilding(Building b)
    {
        int people = Scaled(b, _townTuning.Of(b.Type, b.Tier).People);
        b.Tier++;
        if (b.Type == BuildingType.Home) // the people it brings are those of the new size less those of the old, both lowered by the call-up (C15c)
            CreateTripGroups(b, Scaled(b, _townTuning.Of(b.Type, b.Tier).People) - people, Day + 1);
        _coverageDirty = true;
        BuildingsVersion++;
    }

    /// <summary>Puts a new, smallest building on a lot; what kind is drawn from the mix of the ring the lot lies in.</summary>
    void FoundBuilding(Town town, int lotIndex)
    {
        var lot = town.Lots[lotIndex];
        var s = lot.Site;
        var ring = town.Organic ? TownGenerator.RingAt(_townTuning, s.U) : TownGenerator.RingAt(_townTuning, s.U, s.V, town.Blocks * _townTuning.Generation.BlockLength * 0.5f);
        var type = (BuildingType)TownGenerator.Pick(ring.Mix, _growRng.NextFloat());
        float y = _net.Terrain?.HeightAt(s.X, s.Z) ?? 0f;
        var b = new Building(_buildings.Count, town.Id, type, 0, s.X, y, s.Z, s.Facing) { StateSince = Day, Lot = lotIndex };
        StockBuilding(b);
        _buildings.Add(b);
        town.BuildingIds.Add(b.Id);
        CellList(b.X, b.Z, create: true)!.Add(b.Id);
        lot.Building = b.Id;
        if (type == BuildingType.Home) CreateTripGroups(b, Scaled(b, _townTuning.Of(type, 0).People), Day + 1);
        _coverageDirty = true;
        BuildingsVersion++;
    }

    // ---- New blocks ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds a column or row of blocks to the side of the town nearest to a point, building its streets (the town pays
    /// nothing and the command log has no part in it: a replay grows the same way) and giving it lots. Tries the other
    /// sides in turn when the ground or what stands there does not allow it. False when no side can grow.
    /// </summary>
    bool ExtendGrid(Town town, float x, float z)
    {
        var g = _townTuning.Generation;
        float length = g.BlockLength;
        float half = town.Blocks * length * 0.5f;
        float c = MathF.Cos(town.Angle), s = MathF.Sin(town.Angle);
        float pu = (x - town.X) * c + (z - town.Z) * s, pv = -(x - town.X) * s + (z - town.Z) * c;
        float uMin = -half + town.IMin * length, uMax = -half + (town.IMax + 1) * length;
        float vMin = -half + town.JMin * length, vMax = -half + (town.JMax + 1) * length;
        var order = new[] { (uMax - pu, 0), (pu - uMin, 1), (vMax - pv, 2), (pv - vMin, 3) }.OrderBy(t => t.Item1).ThenBy(t => t.Item2);
        foreach (var (_, side) in order)
            if (TryExtend(town, side, half, uMin, uMax, vMin, vMax)) return true;
        return false;
    }

    bool TryExtend(Town town, int side, float half, float uMin, float uMax, float vMin, float vMax)
    {
        var g = _townTuning.Generation;
        float length = g.BlockLength;
        bool alongU = side < 2; // the new blocks lie beyond an end of the streets that run along u
        float sign = side % 2 == 0 ? 1f : -1f;
        // The new strip, in the town's own axes.
        float su0 = alongU ? (sign > 0 ? uMax : uMin - length) : uMin, su1 = alongU ? (sign > 0 ? uMax + length : uMin) : uMax;
        float sv0 = alongU ? vMin : (sign > 0 ? vMax : vMin - length), sv1 = alongU ? vMax : (sign > 0 ? vMax + length : vMin);
        if (!StripIsFree(town, su0, su1, sv0, sv1)) return false;

        int avenue = town.Blocks / 2; // the main street keeps its place in the rows whatever grows
        int built = 0;
        float edge = alongU ? (sign > 0 ? uMax : uMin) : (sign > 0 ? vMax : vMin);
        float far = edge + sign * length;
        // The streets that run through the new blocks continue the old ones, then the street along the far edge closes them.
        int lines = alongU ? town.JMax - town.JMin + 2 : town.IMax - town.IMin + 2;
        for (int k = 0; k < lines; k++)
        {
            int index = (alongU ? town.JMin : town.IMin) + k;
            float offset = -half + index * length;
            bool isAvenue = alongU && index == avenue;
            var type = isAvenue ? RoadType.Avenue2x2 : RoadType.TwoLane;
            var (x0, z0) = alongU ? TownGenerator.ToWorld(town.X, town.Z, town.Angle, edge, offset) : TownGenerator.ToWorld(town.X, town.Z, town.Angle, offset, edge);
            var (x1, z1) = alongU ? TownGenerator.ToWorld(town.X, town.Z, town.Angle, far, offset) : TownGenerator.ToWorld(town.X, town.Z, town.Angle, offset, far);
            built += _net.BuildRoad(x0, z0, x1, z1, type, _net.Tuning.Roads.Of(type).SpeedLimit).Count;
        }
        {
            float a = alongU ? vMin : uMin, b = alongU ? vMax : uMax;
            var (x0, z0) = alongU ? TownGenerator.ToWorld(town.X, town.Z, town.Angle, far, a) : TownGenerator.ToWorld(town.X, town.Z, town.Angle, a, far);
            var (x1, z1) = alongU ? TownGenerator.ToWorld(town.X, town.Z, town.Angle, far, b) : TownGenerator.ToWorld(town.X, town.Z, town.Angle, b, far);
            built += _net.BuildRoad(x0, z0, x1, z1, RoadType.TwoLane, _net.Tuning.Roads.TwoLane.SpeedLimit).Count;
        }
        if (built == 0) return false;

        int i0 = town.IMin, i1 = town.IMax, j0 = town.JMin, j1 = town.JMax;
        if (alongU) { if (sign > 0) i0 = i1 = ++town.IMax; else i0 = i1 = --town.IMin; }
        else { if (sign > 0) j0 = j1 = ++town.JMax; else j0 = j1 = --town.JMin; }
        foreach (var site in TownGenerator.LotSites(_townTuning, town.X, town.Z, town.Angle, town.Blocks, i0, i1, j0, j1))
            town.Lots.Add(new Lot(site));
        SyncNetwork(); // the new streets give the new lots their road access
        return true;
    }

    const float RailClearance = 14f; // new streets keep this far from a track

    /// <summary>Whether a strip of ground (in a town's own axes) is dry, on the map and clear of buildings, industries and railway.</summary>
    bool StripIsFree(Town town, float u0, float u1, float v0, float v1)
    {
        var g = _townTuning.Generation;
        for (float u = u0; u <= u1 + 0.1f; u += SampleSpacing)
            for (float v = v0; v <= v1 + 0.1f; v += SampleSpacing)
            {
                var (x, z) = TownGenerator.ToWorld(town.X, town.Z, town.Angle, u, v);
                if (_net.Terrain is { } terrain)
                {
                    if (MathF.Abs(x) > terrain.HalfSize - 8f || MathF.Abs(z) > terrain.HalfSize - 8f) return false;
                    if (terrain.HeightAt(x, z) - terrain.WaterSurfaceAt(x, z) < g.WaterMargin) return false;
                }
                if (BuildingsNear(x, z, StripClearance).Count > 0) return false;
                if (_rail.TrackCount > 0 && _rail.TrackAt(x, z, RailClearance) != null) return false; // a town does not grow over a railway
                foreach (var industry in _industries)
                {
                    float reach = _industryTuning.Types[industry.Type].Size + SampleSpacing;
                    if ((industry.X - x) * (industry.X - x) + (industry.Z - z) * (industry.Z - z) < reach * reach) return false;
                }
            }
        return true;
    }
}
