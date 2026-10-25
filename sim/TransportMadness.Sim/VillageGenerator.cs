namespace TransportMadness.Sim;

/// <summary>How a town's streets are arranged (docs/town-redo-plan.md): a short road with houses clustered on it, houses both sides of one curving road, or two roads crossing in the middle.</summary>
public enum TownLayout : byte { Cluster, Linear, Crossing }

/// <summary>A town as <see cref="VillageGenerator"/> made it: its buildings, and how far its houses reach.</summary>
public sealed record VillagePlan(IReadOnlyList<BuildingPlan> Buildings, float Radius, TownLayout Layout, int People);

/// <summary>
/// A small town in the style of the villages of Chernarus: one road curves through it along the contour, lanes of earth leave it (some
/// ending in a loop back to it), houses stand loose beside the streets with a yard, facing them, and bigger buildings stand near the
/// middle. A farm or factory compound may stand apart on a spur with a ring road. The streets are built straight into the
/// <see cref="RoadNetwork"/> (free, before the simulation exists) and the houses are checked against the roads that really came out.
/// A function of the network, the class of town, its place and a seed.
/// </summary>
public static class VillageGenerator
{
    readonly record struct Spot(float X, float Z, float Heading, float Along);

    public static VillagePlan Generate(RoadNetwork net, TownSize size, float cx, float cz, ulong seed) => new Layout(net, size, cx, cz, seed).Run();

    sealed class Layout
    {
        readonly RoadNetwork _net;
        readonly Terrain? _terrain;
        readonly TownsTuning _tuning;
        readonly TownGenerationTuning _g;
        readonly StreetsTuning _st;
        readonly TownClassTuning _class;
        readonly TownSize _size;
        readonly float _cx, _cz;
        readonly SimRandom _rng;
        readonly List<Spot> _mainSpots = new(); // the through road and the cross road: lanes may end in a loop back to them
        readonly List<Spot> _spots = new();     // every point a lane may leave from
        readonly List<(float X, float Z)> _used = new(); // where lanes already leave
        readonly List<(float X, float Z)> _compounds = new(); // middles of ring roads
        readonly List<Placed> _placed = new();
        RoadIndex _index = null!;

        sealed record Placed(BuildingType Type, int Tier, float X, float Z, float Facing, float Size);

        public Layout(RoadNetwork net, TownSize size, float cx, float cz, ulong seed)
        {
            _net = net;
            _terrain = net.Terrain;
            _tuning = net.Tuning.Towns;
            _g = _tuning.Generation;
            _st = _g.Streets;
            _class = _g.Of(size);
            _size = size;
            _cx = cx;
            _cz = cz;
            _rng = new SimRandom(seed);
        }

        float Range(float a, float b) => a + (b - a) * _rng.NextFloat();
        float Signed() => _rng.NextFloat() * 2f - 1f;
        float Dist(float x, float z) => MathF.Sqrt((x - _cx) * (x - _cx) + (z - _cz) * (z - _cz));

        bool Dry(float x, float z) => _terrain == null || _terrain.HeightAt(x, z) - _terrain.WaterSurfaceAt(x, z) >= _g.WaterMargin;

        bool OnMap(float x, float z) => _terrain == null || (MathF.Abs(x) < _terrain.HalfSize - 60f && MathF.Abs(z) < _terrain.HalfSize - 60f);

        // ---- Streets --------------------------------------------------------------------------------------------

        public VillagePlan Run()
        {
            var layout = BuildStreets();
            _index = new RoadIndex(_net);
            int target = _class.PeopleMin + _rng.NextInt(_class.PeopleMax - _class.PeopleMin + 1);
            int people = PlaceBuildings(target);
            // Short of its people, a town lays another lane (up to forty) and houses it.
            for (int extra = 0; extra < 40 && people < target; extra++)
            {
                int roads = _net.RoadCount;
                BuildLane(_rng.NextFloat() < 0.5f ? 1 : -1, _used);
                if (_net.RoadCount == roads) continue;
                _index = new RoadIndex(_net);
                people += PlaceAlong(target - people);
            }
            float radius = _class.Radius;
            foreach (var b in _placed) radius = MathF.Max(radius, Dist(b.X, b.Z) + 10f);
            var buildings = _placed.Select(b => new BuildingPlan(b.Type, b.Tier, b.X, b.Z, b.Facing)).ToList();
            return new VillagePlan(buildings, radius, layout, people);
        }

        TownLayout BuildStreets()
        {
            // The through road runs along the contour: across the line of steepest ascent, a little off.
            float heading = _rng.NextFloat() * MathF.Tau;
            if (_terrain != null)
            {
                const float d = 20f;
                float gx = _terrain.HeightAt(_cx + d, _cz) - _terrain.HeightAt(_cx - d, _cz);
                float gz = _terrain.HeightAt(_cx, _cz + d) - _terrain.HeightAt(_cx, _cz - d);
                if (gx * gx + gz * gz > 0.5f) heading = MathF.Atan2(gx, -gz) + Signed() * 0.35f;
            }
            float armLength = _class.ArmLength + _st.CountryReach;
            List<Spot> forward = new(), back = new();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                forward = Walk(_cx, _cz, heading, armLength, RoadType.TwoLane);
                if (forward.Count > 1) break;
                heading += 0.6f;
            }
            if (forward.Count < 2) return TownLayout.Cluster; // no road could be built here
            back = Walk(_cx, _cz, heading + MathF.PI, armLength, RoadType.TwoLane);
            _mainSpots.AddRange(forward.Skip(1));
            _mainSpots.AddRange(back.Skip(1));
            _spots.AddRange(_mainSpots);

            bool crossing = _rng.NextFloat() < _class.CrossingChance;
            if (crossing)
            {
                float across = heading + MathF.PI * 0.5f + Signed() * 0.3f;
                float cross = _class.ArmLength * 0.75f + _st.CountryReach;
                var a = Walk(_cx, _cz, across, cross, RoadType.TwoLane);
                var b = Walk(_cx, _cz, across + MathF.PI, cross, RoadType.TwoLane);
                _mainSpots.AddRange(a.Skip(1));
                _mainSpots.AddRange(b.Skip(1));
                _spots.AddRange(a.Skip(1));
                _spots.AddRange(b.Skip(1));
            }

            int lanes = _class.LanesMin + _rng.NextInt(_class.LanesMax - _class.LanesMin + 1);
            for (int k = 0; k < lanes; k++) BuildLane(k % 2 == 0 ? 1 : -1, _used);
            if (_rng.NextFloat() < _class.CompoundChance) BuildCompound(_used);
            return crossing ? TownLayout.Crossing : lanes == 0 ? TownLayout.Cluster : TownLayout.Linear;
        }

        /// <summary>
        /// Builds a street from a point as pieces that bend a little each, so the street curves smoothly (a piece's end heading is the next one's start).
        /// A piece is dropped for another turn when it would cross water, climb too steeply, leave the map or the network refuses it; the street stops
        /// when none will do. Returns the point it started at and the end of every piece it built, with the heading there.
        /// </summary>
        List<Spot> Walk(float x, float z, float heading, float length, RoadType type, float clearance = 0f)
        {
            var spots = new List<Spot> { new(x, z, heading, 0f) };
            float speed = _net.Tuning.Roads.Of(type).SpeedLimit;
            float done = 0f, curve = Signed() * _st.MaxTurn * 0.5f;
            while (done < length)
            {
                float len = Range(_st.PieceMin, _st.PieceMax);
                curve = Math.Clamp(curve * 0.7f + Signed() * _st.MaxTurn * 0.6f, -_st.MaxTurn, _st.MaxTurn);
                bool built = false;
                foreach (float turn in new[] { curve, curve * 0.5f, 0f, -curve * 0.5f, -curve, _st.MaxTurn, -_st.MaxTurn, 2f * _st.MaxTurn, -2f * _st.MaxTurn })
                {
                    float mid = heading + turn * 0.5f;
                    float ex = x + MathF.Cos(mid) * len, ez = z + MathF.Sin(mid) * len;
                    if (!PieceOk(x, z, ex, ez) || !Clear(ex, ez, clearance) || !Clear((x + ex) * 0.5f, (z + ez) * 0.5f, clearance)) continue;
                    if (_net.BuildRoad(x, z, ex, ez, type, speed, turn).Count == 0) continue;
                    x = ex;
                    z = ez;
                    heading += turn;
                    done += len;
                    spots.Add(new Spot(x, z, heading, done));
                    built = true;
                    break;
                }
                if (!built) break;
            }
            return spots;
        }

        /// <summary>Whether no road already built lies within <paramref name="clearance"/> of a point (always true for none).</summary>
        bool Clear(float x, float z, float clearance)
        {
            if (clearance <= 0f) return true;
            if (_clearIndex == null || _clearRoads != _net.RoadCount) { _clearIndex = new RoadIndex(_net); _clearRoads = _net.RoadCount; }
            return !_clearIndex.HasRoadWithin(x, z, clearance);
        }

        RoadIndex? _clearIndex;
        int _clearRoads;

        /// <summary>Whether a piece of street from one point to another stays on the map over dry ground and climbs no more than a street may.</summary>
        bool PieceOk(float x0, float z0, float x1, float z1)
        {
            if (!OnMap(x1, z1)) return false;
            const int steps = 6;
            float run = MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / steps;
            float last = _terrain?.HeightAt(x0, z0) ?? 0f;
            foreach (var b in _placed) // a street laid after the houses keeps clear of them (the chord, and a bend's bulge)
            {
                float need = b.Size * 1.2f + RoadNetwork.HalfWidth(RoadType.TwoLane) + 5f;
                float dx = x1 - x0, dz = z1 - z0, t = Math.Clamp(((b.X - x0) * dx + (b.Z - z0) * dz) / (dx * dx + dz * dz), 0f, 1f);
                float px = x0 + dx * t - b.X, pz = z0 + dz * t - b.Z;
                if (px * px + pz * pz < need * need) return false;
            }
            for (int i = 1; i <= steps; i++)
            {
                float x = x0 + (x1 - x0) * i / steps, z = z0 + (z1 - z0) * i / steps;
                if (_terrain == null) continue;
                if (!Dry(x, z)) return false;
                float h = _terrain.HeightAt(x, z);
                if (MathF.Abs(h - last) > _st.MaxSlope * run) return false;
                last = h;
            }
            return true;
        }

        const float LaneClearance = 16f; // a lane's pieces keep this far from the other roads, apart from where it joins one

        /// <summary>A lane of earth that leaves the through road (or the cross road) at about a right angle, away from the lanes that are there, and ends in a dead end or in a loop back to the road.</summary>
        void BuildLane(int side, List<(float X, float Z)> used)
        {
            var from = PickSpot(used, 0.15f, 0.9f);
            if (from == null) return;
            var spot = from.Value;
            used.Add((spot.X, spot.Z));
            if (_rng.NextFloat() < _st.LoopChance && BuildLoop(spot, side)) return;
            float heading = spot.Heading + side * (MathF.PI * 0.5f + Signed() * 0.3f);
            _spots.AddRange(Walk(spot.X, spot.Z, heading, Range(_st.LaneMin, _st.LaneMax), RoadType.Path, LaneClearance).Skip(1));
        }

        /// <summary>
        /// A lane that leaves a point of the road to one side and bends back to join a point of it 90 to 170 m along: each piece turns toward that point
        /// (more than a street's usual bend), and the last runs straight to it. Nothing is built when it cannot get there, and the lane is a dead end instead.
        /// </summary>
        bool BuildLoop(Spot start, int side)
        {
            Spot? target = null;
            float want = Range(90f, 170f);
            foreach (var m in _mainSpots)
            {
                float d = MathF.Sqrt((m.X - start.X) * (m.X - start.X) + (m.Z - start.Z) * (m.Z - start.Z));
                if (d < 70f || d > 190f) continue;
                if (target == null || MathF.Abs(d - want) < MathF.Abs(Dist2(target.Value, start) - want)) target = m;
            }
            if (target == null) return false;
            var t = target.Value;
            float x = start.X, z = start.Z, heading = start.Heading + side * (MathF.PI * 0.5f + Signed() * 0.15f);
            float speed = _net.Tuning.Roads.Path.SpeedLimit;
            var built = new List<RoadId>();
            for (int piece = 0; piece < 14; piece++)
            {
                float remaining = MathF.Sqrt((t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z));
                if (remaining <= _st.PieceMax * 1.2f && piece > 1)
                {
                    if (!PieceOk(x, z, t.X, t.Z)) return false;
                    return _net.BuildRoad(x, z, t.X, t.Z, RoadType.Path, speed).Count > 0;
                }
                float len = Math.Min(Range(_st.PieceMin, _st.PieceMax), remaining * 0.6f);
                if (len < RoadNetwork.MinRoadLength * 1.5f) return false;
                float desired = MathF.Atan2(t.Z - z, t.X - x);
                float diff = desired - heading;
                diff -= MathF.Tau * MathF.Floor((diff + MathF.PI) / MathF.Tau);
                float turn = Math.Clamp(diff * 0.5f, -0.45f, 0.45f);
                bool done = false;
                foreach (float attempt in new[] { turn, turn * 0.5f, 0f, -turn * 0.5f })
                {
                    float mid = heading + attempt * 0.5f;
                    float ex = x + MathF.Cos(mid) * len, ez = z + MathF.Sin(mid) * len;
                    if (!PieceOk(x, z, ex, ez) || !Clear(ex, ez, LaneClearance) || !Clear((x + ex) * 0.5f, (z + ez) * 0.5f, LaneClearance)) continue;
                    if (_net.BuildRoad(x, z, ex, ez, RoadType.Path, speed, attempt).Count == 0) continue;
                    x = ex;
                    z = ez;
                    heading += attempt;
                    done = true;
                    break;
                }
                if (!done) return piece > 1; // a lane that got part of the way stays as a dead end
            }
            return true;
        }

        static float Dist2(Spot a, Spot b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        /// <summary>A point of the main streets between two shares of the town radius, at least a lane's width from the places already used.</summary>
        Spot? PickSpot(List<(float X, float Z)> used, float from, float to)
        {
            var candidates = new List<Spot>();
            foreach (var s in _spots)
            {
                float d = Dist(s.X, s.Z);
                if (d < _class.Radius * from || d > _class.Radius * to) continue;
                bool free = true;
                foreach (var u in used)
                    if ((u.X - s.X) * (u.X - s.X) + (u.Z - s.Z) * (u.Z - s.Z) < 60f * 60f) { free = false; break; }
                if (free) candidates.Add(s);
            }
            return candidates.Count == 0 ? null : candidates[_rng.NextInt(candidates.Count)];
        }

        /// <summary>A farm or factory: a spur of earth off the through road ending in a ring road (four quarter circles), with industry buildings around it.</summary>
        void BuildCompound(List<(float X, float Z)> used)
        {
            // Look for a spot where a straight spur and a ring at its end would stand clear of every road already built.
            Spot spot = default;
            int side = 1;
            float heading = 0f, spurLength = 0f;
            bool found = false;
            for (int attempt = 0; attempt < 8 && !found; attempt++)
            {
                var from = PickSpot(used, 0.3f, 0.8f);
                if (from == null) return;
                spot = from.Value;
                side = _rng.NextFloat() < 0.5f ? 1 : -1;
                heading = spot.Heading + side * (MathF.PI * 0.5f + Signed() * 0.2f);
                spurLength = Range(45f, 75f);
                float sx = spot.X + MathF.Cos(heading) * spurLength, sz = spot.Z + MathF.Sin(heading) * spurLength;
                float cx = sx + MathF.Cos(heading) * _st.CompoundRadius, cz = sz + MathF.Sin(heading) * _st.CompoundRadius;
                found = PieceOk(spot.X, spot.Z, cx, cz) && Clear(cx, cz, _st.CompoundRadius + 26f) && Clear(sx, sz, 18f);
            }
            if (!found) return;
            used.Add((spot.X, spot.Z));
            var spur = Walk(spot.X, spot.Z, heading, spurLength, RoadType.Path);
            if (spur.Count < 2) return;
            var s = spur[^1];
            float r = _st.CompoundRadius, chord = r * MathF.Sqrt(2f);
            float turn = -side * MathF.PI * 0.5f; // the ring bends away from the through road's side
            float h = s.Heading - side * MathF.PI / 3f; // it leaves the spur's end at an angle, so the ring's two ends do not lie on each other
            float x = s.X, z = s.Z;
            float speed = _net.Tuning.Roads.Path.SpeedLimit;
            // Four quarter circles of the same radius come back to where they began; the middle of the circle is r to the side they bend toward.
            float sideAngle = h + MathF.Sign(turn) * MathF.PI * 0.5f;
            float centreX = x + MathF.Cos(sideAngle) * r, centreZ = z + MathF.Sin(sideAngle) * r;
            int built = 0;
            for (int i = 0; i < 4; i++)
            {
                float mid = h + turn * 0.5f;
                float ex = x + MathF.Cos(mid) * chord, ez = z + MathF.Sin(mid) * chord;
                if (!PieceOk(x, z, ex, ez) || (i > 0 && i < 3 && !Clear(ex, ez, 14f)) || _net.BuildRoad(x, z, ex, ez, RoadType.Path, speed, turn).Count == 0) break;
                x = ex;
                z = ez;
                h += turn;
                built++;
            }
            if (built > 0) _compounds.Add((centreX, centreZ));
        }

        // ---- Buildings ------------------------------------------------------------------------------------------

        /// <summary>A place along a street where a house may stand: its point on the street, the street's heading there and half its width.</summary>
        readonly record struct Slot(float X, float Z, float Heading, float HalfWidth, float Order, bool Compound);

        List<Slot> Slots(float spacing = 1f)
        {
            var slots = new List<Slot>();
            for (int r = 0; r < _net.RoadCount; r++)
            {
                var road = _net.GetRoad(new RoadId(r));
                if (road.Retired || road.Structure != RoadStructure.None) continue;
                var g = road.Geometry;
                var (mx, mz) = g.PointAt(g.Length * 0.5f);
                if (Dist(mx, mz) > _class.Radius * 1.6f + g.Length) continue;
                float hw = RoadNetwork.HalfWidth(road.Type);
                for (float s = Range(0f, _st.SpacingMin) * spacing; s <= g.Length; s += Range(_st.SpacingMin, _st.SpacingMax) * spacing)
                {
                    var (x, z) = g.PointAt(s);
                    bool compound = InCompound(x, z);
                    if (!compound && Dist(x, z) > _class.Radius) continue;
                    slots.Add(new Slot(x, z, g.HeadingAt(s), hw, Dist(x, z) * (1f + 0.4f * _rng.NextFloat()), compound));
                }
            }
            slots.Sort((a, b) => a.Order.CompareTo(b.Order));
            return slots;
        }

        bool InCompound(float x, float z)
        {
            foreach (var c in _compounds)
                if ((c.X - x) * (c.X - x) + (c.Z - z) * (c.Z - z) < MathF.Pow(_st.CompoundRadius * 2.2f, 2)) return true;
            return false;
        }

        int PlaceBuildings(int target)
        {
            // Compounds first: an industry building in the middle of each ring road, more round it as the slots along the ring come up.
            foreach (var c in _compounds)
                PlaceAt(c.X, c.Z, BuildingType.Industry, Math.Min(1, _class.MaxTier), _rng.NextFloat() * MathF.Tau);
            return PlaceAlong(target);
        }

        /// <summary>Puts houses (and the shops and workshops of the ring they lie in) beside the streets, middle first, until the homes hold <paramref name="target"/> people. Returns the people added.</summary>
        int PlaceAlong(int target)
        {
            int people = 0;
            foreach (var slot in Slots())
            {
                if (people >= target) break;
                int side = _rng.NextFloat() < 0.5f ? 1 : -1;
                for (int k = 0; k < 2; k++, side = -side)
                {
                    float ratio = Dist(slot.X, slot.Z) / _class.Radius;
                    var (type, tier) = slot.Compound ? (BuildingType.Industry, Math.Min(1, _class.MaxTier)) : Roll(ratio);
                    // A town always has its shop and its workshop: people need somewhere to go to (and growth needs trips).
                    if (type == BuildingType.Home && !slot.Compound)
                    {
                        int homes = _placed.Count(p => p.Type == BuildingType.Home);
                        if (_placed.Count(p => p.Type == BuildingType.Commerce) * 6 < homes) (type, tier) = (BuildingType.Commerce, Math.Min(tier, 1));
                        else if (_placed.Count(p => p.Type == BuildingType.Industry) * 12 < homes) (type, tier) = (BuildingType.Industry, 0);
                    }
                    if (!Fits(slot, side, type, tier, out var place)) continue;
                    Commit(place, type, tier);
                    if (type == BuildingType.Home) people += _tuning.Of(type, tier).People;
                    break;
                }
            }
            return people;
        }

        (BuildingType, int) Roll(float ratio)
        {
            var ring = TownGenerator.RingAt(_tuning, ratio);
            var type = (BuildingType)TownGenerator.Pick(ring.Mix, _rng.NextFloat());
            var tiers = ring.Tiers.Select((w, i) => i <= _class.MaxTier ? w : 0f).ToList();
            if (tiers.Sum() <= 0f) return (type, 0);
            return (type, TownGenerator.Pick(tiers, _rng.NextFloat()));
        }

        /// <summary>Where a building would stand on one side of a slot: set back from the street by its yard and its footprint, facing the street, a little turned.</summary>
        (float X, float Z, float Facing) Place(Slot slot, int side, BuildingType type, int tier)
        {
            var tuning = _tuning.Of(type, tier);
            float yard = Range(_st.YardMin, _st.YardMax);
            float offset = slot.HalfWidth + yard + tuning.Footprint;
            float nx = -MathF.Sin(slot.Heading) * side, nz = MathF.Cos(slot.Heading) * side;
            float facing = MathF.Atan2(-nz, -nx) + Signed() * _st.YawJitter;
            return (slot.X + nx * offset, slot.Z + nz * offset, facing);
        }

        bool Fits(Slot slot, int side, BuildingType type, int tier, out (float X, float Z, float Facing) place)
        {
            place = Place(slot, side, type, tier);
            return Valid(place.X, place.Z, type, tier);
        }

        bool Valid(float x, float z, BuildingType type, int tier)
        {
            var tuning = _tuning.Of(type, tier);
            float size = tuning.Size;
            if (!OnMap(x, z) || !Dry(x, z)) return false;
            if (_terrain != null)
            {
                float lowest = float.MaxValue, highest = float.MinValue;
                for (int i = -1; i <= 1; i++)
                    for (int j = -1; j <= 1; j++)
                    {
                        float px = x + i * size, pz = z + j * size;
                        if (!Dry(px, pz)) return false;
                        float h = _terrain.HeightAt(px, pz);
                        lowest = MathF.Min(lowest, h);
                        highest = MathF.Max(highest, h);
                    }
                if (highest - lowest > _st.MaxSlope * size * 2.8f) return false;
            }
            // No road runs through it, nor close enough to touch its yard.
            float clear = RoadNetwork.HalfWidth(RoadType.TwoLane) + tuning.Footprint + 1f;
            if (_index.DistanceToNearest(x, z, clear + 2f) < clear) return false;
            if (!_index.HasRoadWithin(x, z, _tuning.AccessRadius - 4f)) return false; // a building with no road near it would be abandoned
            foreach (var p in _placed)
            {
                float need = (p.Size + size) * 1.15f + _st.Gap;
                if ((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z) < need * need) return false;
            }
            return true;
        }

        void PlaceAt(float x, float z, BuildingType type, int tier, float facing)
        {
            if (Valid(x, z, type, tier)) Commit((x, z, facing), type, tier);
        }

        /// <summary>Puts a building down and levels the ground under it (not under a street), so it stands on a flat pad with the ground blending back around it.</summary>
        void Commit((float X, float Z, float Facing) place, BuildingType type, int tier)
        {
            float size = _tuning.Of(type, tier).Size;
            _placed.Add(new Placed(type, tier, place.X, place.Z, place.Facing, size));
            if (_terrain == null) return;
            float half = size * 1.1f;
            float height = _terrain.MeanHeight(place.X, place.Z, place.Facing, half);
            _terrain.LevelPad(place.X, place.Z, place.Facing, half, height, _st.PadShoulder, apply: true, (px, pz) => _index.DistanceToNearest(px, pz, 5f) <= 5f);
        }
    }
}
