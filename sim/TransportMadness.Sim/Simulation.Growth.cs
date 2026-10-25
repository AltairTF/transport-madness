namespace TransportMadness.Sim;

/// <summary>
/// Growth of towns (M3d). At the turn of each month the share of a town's homes that were reached is worked out
/// (connectivity); a town with enough satisfaction and connectivity then spends the construction stock of its buildings:
/// a building with a full stock goes up a size, or if already the biggest founds a new building on the nearest free spot beside a road of
/// its town (or where a destroyed one stood), or on a new street of earth when there is none. A town never shrinks by itself.
/// </summary>
public sealed partial class Simulation
{
    const ulong GrowthSeedMix = 0x47524F57544821A7UL;
    const float SampleSpacing = 30f; // ground is checked this far apart when a town wants a new block
    const float StockSlack = 0.01f;  // a construction stock this close to its cost counts as full (float dust)
    const float StripClearance = 12f; // a new block is refused when a building stands this close to a point of it

    readonly GrowthTuning _growth;
    readonly SimRandom _growRng;

    // ---- Spots ---------------------------------------------------------------------------------------------------
    // A town has no list of lots: where a house may stand is worked out from the roads and buildings that are there when it wants room.

    /// <summary>A place for a building: its middle and the way it faces (toward its street).</summary>
    internal readonly record struct Spot(float X, float Z, float Facing);

    /// <summary>
    /// Whether a building of a type and size could stand at a place now, apart from ground that is wet or steep: on the map, no building closer than
    /// the two sizes need (<paramref name="ignore"/> is the building itself, when it grows in place), no road through it or closer than its yard, a road
    /// near enough to reach it, and clear of industries.
    /// </summary>
    internal bool RoomFor(float x, float z, BuildingType type, int tier, int ignore = -1)
    {
        if (_roadIndex == null) return false;
        var me = _townTuning.Of(type, tier);
        if (_net.Terrain is { } terrain && (MathF.Abs(x) > terrain.HalfSize - 8f || MathF.Abs(z) > terrain.HalfSize - 8f)) return false;
        float clear = RoadNetwork.HalfWidth(RoadType.TwoLane) + me.Footprint + 1f;
        if (_roadIndex.DistanceToNearest(x, z, clear + 2f) < clear) return false;
        float road = _roadIndex.DistanceToNearest(x, z, _townTuning.AccessRadius);
        if (float.IsPositiveInfinity(road) || road < _growth.SpotClearance) return false;
        float widest = _townTuning.Of(BuildingType.Home, TownsTuning.Tiers - 1).Size;
        foreach (int id in BuildingsNear(x, z, (me.Size + widest) * 1.15f + _townTuning.Generation.Streets.Gap))
        {
            if (id == ignore) continue;
            var o = _buildings[id];
            float need = (me.Size + _townTuning.Of(o.Type, o.Tier).Size) * 1.15f + _townTuning.Generation.Streets.Gap;
            if ((o.X - x) * (o.X - x) + (o.Z - z) * (o.Z - z) < need * need) return false;
        }
        foreach (var industry in _industries)
        {
            float reach = _industryTuning.Types[industry.Type].Size + me.Size + _townTuning.Generation.Streets.Gap;
            if ((industry.X - x) * (industry.X - x) + (industry.Z - z) * (industry.Z - z) < reach * reach) return false;
        }
        return true;
    }

    /// <summary>Whether the ground at a place is dry and gentle enough for a new building of the smallest size, and no railway or other town is in the way.</summary>
    bool GroundFor(Town town, float x, float z)
    {
        foreach (var t in _towns)
            if (t.Id != town.Id && (t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z) < t.Radius * t.Radius) return false;
        if (_rail.TrackCount > 0 && _rail.TrackAt(x, z, RailClearance) != null) return false;
        if (_net.Terrain is not { } terrain) return true;
        float size = _townTuning.Of(BuildingType.Home, 0).Size, lowest = float.MaxValue, highest = float.MinValue;
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                float px = x + i * size, pz = z + j * size;
                if (terrain.HeightAt(px, pz) - terrain.WaterSurfaceAt(px, pz) < _townTuning.Generation.WaterMargin) return false;
                float h = terrain.HeightAt(px, pz);
                lowest = MathF.Min(lowest, h);
                highest = MathF.Max(highest, h);
            }
        return highest - lowest <= _townTuning.Generation.Streets.MaxSlope * size * 2.8f;
    }

    /// <summary>Levels the ground under a building of a type and size, as the generator does for the first ones (a road is left alone).</summary>
    void LevelUnder(float x, float z, float facing, BuildingType type, int tier)
    {
        if (_net.Terrain is not { } terrain) return;
        float half = _townTuning.Of(type, tier).Size * 1.1f;
        terrain.LevelPad(x, z, facing, half, terrain.MeanHeight(x, z, facing, half), _townTuning.Generation.Streets.PadShoulder, true, SkipRoadSample);
    }

    const float SpotYardMin = 3f, SpotYardMax = 9f; // clear ground between a road's edge and a new house's front

    /// <summary>
    /// The free spot nearest to a point: first where a building of the town was destroyed (it is rebuilt on the same spot), else beside a road within the
    /// town's reach (the player's too), 15 to 32 m apart on both sides. <paramref name="only"/> limits the roads. Null when there is none.
    /// </summary>
    internal Spot? FindSpot(Town town, float fromX, float fromZ, IReadOnlyCollection<RoadId>? only = null)
    {
        Spot? best = null;
        float bestDistance = float.MaxValue;
        void Consider(float x, float z, float facing)
        {
            float d = (x - fromX) * (x - fromX) + (z - fromZ) * (z - fromZ);
            if (d >= bestDistance || !GroundFor(town, x, z) || !RoomFor(x, z, BuildingType.Home, 0)) return;
            best = new Spot(x, z, facing);
            bestDistance = d;
        }
        if (only == null)
            foreach (int id in town.BuildingIds)
            {
                var b = _buildings[id];
                if (b.State == BuildingState.Demolished) Consider(b.X, b.Z, b.Facing);
            }
        if (best != null) return best; // a destroyed building is rebuilt on the same spot before any new one is founded
        float reach = town.Radius + _growth.SpotReach;
        for (int r = 0; r < _net.RoadCount; r++)
        {
            var id = new RoadId(r);
            if (only != null && !only.Contains(id)) continue;
            var road = _net.GetRoad(id);
            if (road.Retired || road.Structure != RoadStructure.None) continue;
            var g = road.Geometry;
            float footprint = _townTuning.Of(BuildingType.Home, 0).Footprint;
            for (float at = _growRng.NextFloat() * 10f + 8f; at < g.Length; at += 15f + _growRng.NextFloat() * 17f)
            {
                var (px, pz) = g.PointAt(at);
                if ((px - town.X) * (px - town.X) + (pz - town.Z) * (pz - town.Z) > reach * reach) continue;
                float h = g.HeadingAt(at);
                for (int side = 1; side >= -1; side -= 2)
                {
                    float nx = -MathF.Sin(h) * side, nz = MathF.Cos(h) * side;
                    float offset = RoadNetwork.HalfWidth(road.Type) + SpotYardMin + _growRng.NextFloat() * (SpotYardMax - SpotYardMin) + footprint;
                    Consider(px + nx * offset, pz + nz * offset, MathF.Atan2(-nz, -nx));
                }
            }
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

            int upgrades = 0, founded = 0, streets = 0;
            bool streetTried = false;
            foreach (int id in town.BuildingIds.ToArray()) // buildings founded this month wait for the next
            {
                var b = _buildings[id];
                if (b.State != BuildingState.Active || !HasFullConstructionStock(b)) continue;
                if (b.Tier < TownsTuning.Tiers - 1 && RoomFor(b.X, b.Z, b.Type, b.Tier + 1, b.Id))
                {
                    SpendConstruction(b);
                    GrowBuilding(b);
                    upgrades++;
                    continue;
                }
                var spot = FindSpot(town, b.X, b.Z);
                if (spot == null && !streetTried) // no spot beside any road: the town grows a street of earth (once a month)
                {
                    streetTried = true;
                    if (GrowStreet(town, b.X, b.Z)) { streets++; spot = FindSpot(town, b.X, b.Z); }
                }
                if (spot == null) continue;
                SpendConstruction(b);
                FoundBuilding(town, spot.Value);
                founded++;
            }
            town.LastGrowth = new GrowthReport(upgrades, founded, streets, GrowthBlock.None);
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
        LevelUnder(b.X, b.Z, b.Facing, b.Type, b.Tier);
        if (b.Type == BuildingType.Home) // the people it brings are those of the new size less those of the old, both lowered by the call-up (C15c)
            CreateTripGroups(b, Scaled(b, _townTuning.Of(b.Type, b.Tier).People) - people, Day + 1);
        _coverageDirty = true;
        BuildingsVersion++;
    }

    /// <summary>Puts a new, smallest building on a spot (levelling the ground under it); what kind is drawn from the mix of the ring the spot lies in.</summary>
    void FoundBuilding(Town town, Spot s)
    {
        float ratio = MathF.Sqrt((s.X - town.X) * (s.X - town.X) + (s.Z - town.Z) * (s.Z - town.Z)) / MathF.Max(town.Radius, 1f);
        var type = (BuildingType)TownGenerator.Pick(TownGenerator.RingAt(_townTuning, ratio).Mix, _growRng.NextFloat());
        LevelUnder(s.X, s.Z, s.Facing, type, 0);
        town.Radius = MathF.Max(town.Radius, ratio * town.Radius + 10f); // a town reaches as far out as its houses do
        float y = _net.Terrain?.HeightAt(s.X, s.Z) ?? 0f;
        var b = new Building(_buildings.Count, town.Id, type, 0, s.X, y, s.Z, s.Facing) { StateSince = Day };
        StockBuilding(b);
        _buildings.Add(b);
        town.BuildingIds.Add(b.Id);
        CellList(b.X, b.Z, create: true)!.Add(b.Id);
        if (type == BuildingType.Home) CreateTripGroups(b, Scaled(b, _townTuning.Of(type, 0).People), Day + 1);
        _coverageDirty = true;
        BuildingsVersion++;
    }

    // ---- New streets ------------------------------------------------------------------------------------------------

        const float RailClearance = 14f;  // new streets keep this far from a track
    const float StreetClearance = 16f; // a new street keeps this far from other roads, apart from where it leaves one
    const int BranchTries = 16;

    /// <summary>
    /// The town grows a street of earth (free, and not in the command log: a replay grows the same way): it leaves a road of the town near a
    /// point at about a right angle, bends with the ground in pieces and ends in a dead end or joins a road of the town, 60 to 150 m on, then gets spots along it. Tries
    /// several branch points, nearest first; false when none will do (steep or wet ground, a building, a railway or another town in the way).
    /// </summary>
    internal bool GrowStreet(Town town, float x, float z)
    {
        var st = _townTuning.Generation.Streets;
        var index = new RoadIndex(_net);
        var branches = new List<(float D, float X, float Z, float H)>();
        for (int r = 0; r < _net.RoadCount; r++)
        {
            var road = _net.GetRoad(new RoadId(r));
            if (road.Retired || road.Structure != RoadStructure.None) continue;
            var g = road.Geometry;
            for (float at = 20f; at < g.Length - 15f; at += 30f)
            {
                var (px, pz) = g.PointAt(at);
                if ((px - town.X) * (px - town.X) + (pz - town.Z) * (pz - town.Z) > town.Radius * town.Radius) continue;
                branches.Add(((px - x) * (px - x) + (pz - z) * (pz - z), px, pz, g.HeadingAt(at)));
            }
        }
        branches.Sort((a, b) => a.D.CompareTo(b.D));
        for (int tries = 0; tries < Math.Min(BranchTries, branches.Count); tries++)
        {
            var (_, bx, bz, bh) = branches[tries];
            int side = _growRng.NextFloat() < 0.5f ? 1 : -1;
            for (int s = 0; s < 2; s++, side = -side)
            {
                float heading = bh + side * (MathF.PI * 0.5f + (_growRng.NextFloat() - 0.5f) * 0.4f);
                float length = _growth.StreetMin + _growRng.NextFloat() * (_growth.StreetMax - _growth.StreetMin);
                List<(float X0, float Z0, float X1, float Z1, float Turn)> pieces = new();
                if (_growRng.NextFloat() < st.LoopChance) // a street that bends back to join a road of the town 90 to 170 m along, else a dead end
                {
                    var join = branches.Where(c => { float d = MathF.Sqrt((c.X - bx) * (c.X - bx) + (c.Z - bz) * (c.Z - bz)); return d >= 90f && d <= 170f; }).ToList();
                    if (join.Count > 0)
                    {
                        var t = join[_growRng.NextInt(join.Count)];
                        pieces = PlanStreet(town, index, bx, bz, heading, length, st, (t.X, t.Z));
                    }
                }
                if (pieces.Count < 2) pieces = PlanStreet(town, index, bx, bz, heading, length, st, null);
                if (pieces.Count < 2) continue;
                var built = new List<RoadId>();
                float speed = _net.Tuning.Roads.Path.SpeedLimit;
                foreach (var (x0, z0, x1, z1, turn) in pieces)
                    built.AddRange(_net.BuildRoad(x0, z0, x1, z1, RoadType.Path, speed, turn));
                if (built.Count == 0) continue;
                SyncNetwork(); // the new street gives the new spots their road access
                if (FindSpot(town, x, z, built) != null) return true;
            }
        }
        return false;
    }

    /// <summary>The pieces (start, end, bend) of a street that leaves a point to the given heading, as far as the ground and what stands about allow; fewer than two when it cannot get 60 m.</summary>
    List<(float X0, float Z0, float X1, float Z1, float Turn)> PlanStreet(Town town, RoadIndex index, float x, float z, float heading, float length, StreetsTuning st, (float X, float Z)? join)
    {
        if (join is { } target) return PlanJoin(town, index, x, z, heading, target, st);
        var pieces = new List<(float, float, float, float, float)>();
        float done = 0f, curve = (_growRng.NextFloat() - 0.5f) * st.MaxTurn;
        while (done < length)
        {
            float len = st.PieceMin + _growRng.NextFloat() * (st.PieceMax - st.PieceMin);
            curve = Math.Clamp(curve * 0.7f + (_growRng.NextFloat() - 0.5f) * st.MaxTurn * 1.2f, -st.MaxTurn, st.MaxTurn);
            bool ok = false;
            foreach (float turn in new[] { curve, curve * 0.5f, 0f, -curve * 0.5f, -curve })
            {
                float mid = heading + turn * 0.5f;
                float ex = x + MathF.Cos(mid) * len, ez = z + MathF.Sin(mid) * len;
                if (!StreetPieceOk(town, index, x, z, ex, ez, st, first: pieces.Count == 0)) continue;
                pieces.Add((x, z, ex, ez, turn));
                x = ex;
                z = ez;
                heading += turn;
                done += len;
                ok = true;
                break;
            }
            if (!ok) break;
        }
        return done >= _growth.StreetMin * 0.9f ? pieces : new();
    }

    /// <summary>The pieces of a street that leaves a point and bends toward a road point, each turning up to 0.45 rad toward it, the last running straight to it; empty when it cannot get there.</summary>
    List<(float X0, float Z0, float X1, float Z1, float Turn)> PlanJoin(Town town, RoadIndex index, float x, float z, float heading, (float X, float Z) t, StreetsTuning st)
    {
        var pieces = new List<(float, float, float, float, float)>();
        for (int piece = 0; piece < 14; piece++)
        {
            float remaining = MathF.Sqrt((t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z));
            if (remaining <= st.PieceMax * 1.2f && piece > 1)
            {
                if (!StreetPieceOk(town, index, x, z, t.X, t.Z, st, first: false, joining: true)) return new();
                pieces.Add((x, z, t.X, t.Z, 0f));
                return pieces;
            }
            float len = Math.Min(st.PieceMin + _growRng.NextFloat() * (st.PieceMax - st.PieceMin), remaining * 0.6f);
            if (len < RoadNetwork.MinRoadLength * 1.5f) return new();
            float diff = MathF.Atan2(t.Z - z, t.X - x) - heading;
            diff -= MathF.Tau * MathF.Floor((diff + MathF.PI) / MathF.Tau);
            float turn = Math.Clamp(diff * 0.5f, -0.45f, 0.45f);
            bool done = false;
            foreach (float attempt in new[] { turn, turn * 0.5f, 0f, -turn * 0.5f })
            {
                float mid = heading + attempt * 0.5f;
                float ex = x + MathF.Cos(mid) * len, ez = z + MathF.Sin(mid) * len;
                if (!StreetPieceOk(town, index, x, z, ex, ez, st, first: piece == 0)) continue;
                pieces.Add((x, z, ex, ez, attempt));
                x = ex;
                z = ez;
                heading += attempt;
                done = true;
                break;
            }
            if (!done) return new();
        }
        return new();
    }

    bool StreetPieceOk(Town town, RoadIndex index, float x0, float z0, float x1, float z1, StreetsTuning st, bool first, bool joining = false)
    {
        if (_net.Terrain is { } terrain)
        {
            if (MathF.Abs(x1) > terrain.HalfSize - 60f || MathF.Abs(z1) > terrain.HalfSize - 60f) return false;
            float last = terrain.HeightAt(x0, z0);
            for (int i = 1; i <= 6; i++)
            {
                float px = x0 + (x1 - x0) * i / 6f, pz = z0 + (z1 - z0) * i / 6f, h = terrain.HeightAt(px, pz);
                if (h - terrain.WaterSurfaceAt(px, pz) < _townTuning.Generation.WaterMargin) return false;
                if (MathF.Abs(h - last) > st.MaxSlope * MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / 6f) return false;
                last = h;
            }
        }
        float mx = (x0 + x1) * 0.5f, mz = (z0 + z1) * 0.5f;
        if (_rail.TrackCount > 0) // a town does not grow over a railway: checked along the whole piece, not only at its ends
        {
            float length = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            int steps = Math.Max(1, (int)MathF.Ceiling(length / 8f));
            for (int i = 0; i <= steps; i++)
                if (_rail.TrackAt(x0 + (x1 - x0) * i / steps, z0 + (z1 - z0) * i / steps, RailClearance) != null) return false;
        }
        foreach (var (px, pz) in new[] { (x1, z1), (mx, mz) })
        {
            if (BuildingsNear(px, pz, StripClearance).Count > 0) return false;
            foreach (var t in _towns)
                if (t.Id != town.Id && (t.X - px) * (t.X - px) + (t.Z - pz) * (t.Z - pz) < t.Radius * t.Radius) return false;
            foreach (var industry in _industries)
            {
                float reach = _industryTuning.Types[industry.Type].Size + SampleSpacing;
                if ((industry.X - px) * (industry.X - px) + (industry.Z - pz) * (industry.Z - pz) < reach * reach) return false;
            }
        }
        if (joining) return true; // it ends on a road
        if (index.HasRoadWithin(x1, z1, StreetClearance)) return false;
        return first || !index.HasRoadWithin(mx, mz, StreetClearance);
    }
}
