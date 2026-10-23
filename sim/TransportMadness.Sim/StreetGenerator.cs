namespace TransportMadness.Sim;

/// <summary>The size class of a town: how many seed roads, how many buildings and how it is laid out.</summary>
public enum TownSize : byte { Village, Medium, Metropolis }

/// <summary>A street between two nodes of a <see cref="StreetGraph"/>. <c>Seed</c>: part of the frame (a through road or the ring), which runs out of the town and may end free.</summary>
public readonly record struct StreetEdge(int A, int B, bool Main, bool Bridge, bool Seed);

/// <summary>The streets of one town as the generator lays them out: nodes in the map's axes and the streets between them.</summary>
public sealed record StreetGraph(IReadOnlyList<(float X, float Z)> Nodes, IReadOnlyList<StreetEdge> Edges);

/// <summary>A town as the growth loop leaves it: its streets, every lot along them (the buildings stand on some), and how far from its centre they reach.</summary>
public sealed record OrganicTown(StreetGraph Graph, IReadOnlyList<TownGenerator.LotSite> Lots, IReadOnlyList<BuildingPlan> Buildings, float Radius);

/// <summary>
/// R4: a town that grows street by street (docs/r4-plan.md). Two to four seed roads cross at the square, each running past the edge of the town
/// (a metropolis has a ring street too). Then, until the size class has its buildings, a step adds one piece of street to a street end or a
/// point along a street, picked toward the centre: an extension (straight on), a branch (across) or a link (to a street within reach, which
/// closes a block), the shares depending on the ring of the pick. A piece is refused over water that is too wide to bridge, on too steep
/// ground, off the map, too near a street it is not joined to, or too far out. The lots on both sides of each piece are laid and built on
/// at once, by their ring. Pure: it does not touch the road network, and the same inputs give the same town.
/// </summary>
public static class StreetGenerator
{
    /// <summary>No piece of street is shorter: two junctions closer than this (a junction reaches 8 m into each road) leave cars no room between them.</summary>
    const float ShortestPiece = 30f;
    const ulong SeedSalt = 0x5354524545545234UL;

    public static OrganicTown Generate(Terrain? terrain, TownsTuning tuning, TownSize size, float cx, float cz, float radius, ulong seed)
        => new Layout(terrain, tuning, size, cx, cz, radius, seed).Run();

    /// <summary>
    /// The street ends of the graph that join nothing (outside the frame) with the length of street from each back to the junction it hangs off.
    /// </summary>
    public static List<(int Node, float Run)> DeadEnds(StreetGraph graph)
    {
        var adj = new List<int>[graph.Nodes.Count];
        for (int i = 0; i < adj.Length; i++) adj[i] = new();
        for (int e = 0; e < graph.Edges.Count; e++) { adj[graph.Edges[e].A].Add(e); adj[graph.Edges[e].B].Add(e); }
        var ends = new List<(int, float)>();
        for (int n = 0; n < adj.Length; n++)
        {
            if (adj[n].Count != 1 || graph.Edges[adj[n][0]].Seed) continue;
            float run = 0f;
            int node = n, via = adj[n][0];
            while (true)
            {
                var e = graph.Edges[via];
                var (x0, z0) = graph.Nodes[e.A];
                var (x1, z1) = graph.Nodes[e.B];
                run += MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                node = e.A == node ? e.B : e.A;
                if (adj[node].Count != 2) break;
                via = adj[node][0] == via ? adj[node][1] : adj[node][0];
            }
            ends.Add((n, run));
        }
        return ends;
    }

    sealed class Edge
    {
        public int A, B, Fails;
        public bool Main, Bridge, Seed;
    }

    sealed class Lot
    {
        public TownGenerator.LotSite Site;
        public BuildingPlan? Plan;
        public bool Dead;
    }

    /// <summary>Where a piece starts or ends: an existing node, or a point along an edge that is split when the piece is accepted.</summary>
    readonly record struct Anchor(int Node, int Edge, float X, float Z);

    sealed class Layout
    {
        readonly Terrain? _terrain;
        readonly TownsTuning _tuning;
        readonly TownGenerationTuning _g;
        readonly StreetsTuning _t;
        readonly TownSize _size;
        readonly float _cx, _cz, _r;
        readonly SimRandom _rng;
        readonly float _setback, _narrow, _apart;

        List<(float X, float Z)> _nodes = new();
        List<Edge> _edges = new();
        List<List<int>> _adj = new();
        readonly List<Lot> _lots = new();
        readonly Dictionary<(int, int), List<int>> _cells = new();
        int _built;

        public Layout(Terrain? terrain, TownsTuning tuning, TownSize size, float cx, float cz, float radius, ulong seed)
        {
            _terrain = terrain;
            _tuning = tuning;
            _g = tuning.Generation;
            _t = _g.Streets;
            _size = size;
            _cx = cx;
            _cz = cz;
            _r = radius;
            _rng = new SimRandom(seed ^ SeedSalt);
            float footprint = TownGenerator.MaxFootprint(tuning);
            _setback = RoadNetwork.HalfWidth(RoadType.Avenue2x2) + _g.SetbackGap + footprint;
            _narrow = RoadNetwork.HalfWidth(RoadType.TwoLane) + _g.SetbackGap + footprint;
            _apart = footprint * 2f;
        }

        int Target => _size switch { TownSize.Village => _t.TargetVillage, TownSize.Medium => _t.TargetMedium, _ => _t.TargetMetropolis };

        public OrganicTown Run()
        {
            BuildFrame();
            for (int e = 0; e < _edges.Count; e++) AddLots(e);
            int steps = 0, stall = 0, maxSteps = Target * 60;
            while (_built < Target && steps++ < maxSteps && stall < _t.StallLimit && _edges.Count > 0)
                stall = Step() ? 0 : stall + 1;

            var lots = new List<TownGenerator.LotSite>();
            var buildings = new List<BuildingPlan>();
            var distances = new List<float>();
            foreach (var lot in _lots)
            {
                if (lot.Dead) continue;
                lots.Add(lot.Site);
                if (lot.Plan is { } plan) buildings.Add(plan);
                distances.Add(Dist(lot.Site.X, lot.Site.Z, _cx, _cz));
            }
            // How far the town reaches: where nineteen in twenty of its lots lie (a few houses stand along the country roads further out).
            distances.Sort();
            float reach = distances.Count > 0 ? MathF.Max(0.5f * _r, distances[(int)(distances.Count * 0.95f)]) : 0.5f * _r;
            var edges = _edges.Select(e => new StreetEdge(e.A, e.B, e.Main, e.Bridge, e.Seed)).ToList();
            return new OrganicTown(new StreetGraph(_nodes, edges), lots, buildings, reach + 10f);
        }

        // ---- Graph ------------------------------------------------------------------------------------------------

        static float Dist(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

        int AddNode(float x, float z)
        {
            _nodes.Add((x, z));
            _adj.Add(new List<int>());
            return _nodes.Count - 1;
        }

        int AddEdge(int a, int b, bool main, bool bridge, bool seed)
        {
            _edges.Add(new Edge { A = a, B = b, Main = main, Bridge = bridge, Seed = seed });
            _adj[a].Add(_edges.Count - 1);
            _adj[b].Add(_edges.Count - 1);
            return _edges.Count - 1;
        }

        int Other(int edge, int node) => _edges[edge].A == node ? _edges[edge].B : _edges[edge].A;

        float Length(int edge)
        {
            var e = _edges[edge];
            return Dist(_nodes[e.A].X, _nodes[e.A].Z, _nodes[e.B].X, _nodes[e.B].Z);
        }

        /// <summary>Cuts an edge at a node (which stands on it): the edge keeps its first half, the second half is a new edge.</summary>
        void SplitEdge(int edge, int node)
        {
            var e = _edges[edge];
            int b = e.B;
            _adj[b].Remove(edge);
            e.B = node;
            _adj[node].Add(edge);
            int f = AddEdge(node, b, e.Main, e.Bridge, e.Seed);
            _edges[f].Fails = e.Fails;
        }

        int Resolve(Anchor a)
        {
            if (a.Node >= 0) return a.Node;
            int node = AddNode(a.X, a.Z);
            SplitEdge(a.Edge, node);
            return node;
        }

        // ---- Geometry ---------------------------------------------------------------------------------------------

        static float PointSegment(float px, float pz, float ax, float az, float bx, float bz)
        {
            float sx = bx - ax, sz = bz - az, l2 = sx * sx + sz * sz;
            float t = l2 < 1e-6f ? 0f : Math.Clamp(((px - ax) * sx + (pz - az) * sz) / l2, 0f, 1f);
            return Dist(px, pz, ax + sx * t, az + sz * t);
        }

        /// <summary>Where segment a-b crosses segment c-d (as shares of both), when it does inside both.</summary>
        static bool Crossing(float ax, float az, float bx, float bz, float cx, float cz, float dx, float dz, out float t, out float u)
        {
            float rx = bx - ax, rz = bz - az, sx = dx - cx, sz = dz - cz;
            float den = rx * sz - rz * sx;
            t = u = 0f;
            if (MathF.Abs(den) < 1e-6f) return false;
            t = ((cx - ax) * sz - (cz - az) * sx) / den;
            u = ((cx - ax) * rz - (cz - az) * rx) / den;
            return t > 0.001f && t < 0.999f && u > 0.001f && u < 0.999f;
        }

        static float SegmentSegment(float ax, float az, float bx, float bz, float cx, float cz, float dx, float dz)
        {
            if (Crossing(ax, az, bx, bz, cx, cz, dx, dz, out _, out _)) return 0f;
            return MathF.Min(MathF.Min(PointSegment(ax, az, cx, cz, dx, dz), PointSegment(bx, bz, cx, cz, dx, dz)),
                MathF.Min(PointSegment(cx, cz, ax, az, bx, bz), PointSegment(dx, dz, ax, az, bx, bz)));
        }

        float EdgeDistance(int edge, float x, float z)
        {
            var e = _edges[edge];
            return PointSegment(x, z, _nodes[e.A].X, _nodes[e.A].Z, _nodes[e.B].X, _nodes[e.B].Z);
        }

        /// <summary>Dry and on the map at every point, not too steep, and no more water than a bridge can span. <paramref name="bridge"/>: part of it is water.</summary>
        bool Probe(float ax, float az, float bx, float bz, out bool bridge)
        {
            bridge = false;
            if (_terrain is not { } terrain) return true;
            float len = Dist(ax, az, bx, bz);
            int n = Math.Max(2, (int)MathF.Ceiling(len / 6f));
            float step = len / n, wet = 0f, prev = float.NaN;
            for (int i = 0; i <= n; i++)
            {
                float x = ax + (bx - ax) * i / n, z = az + (bz - az) * i / n;
                if (MathF.Abs(x) > terrain.HalfSize - 12f || MathF.Abs(z) > terrain.HalfSize - 12f) return false;
                float h = terrain.HeightAt(x, z);
                if (h - terrain.WaterSurfaceAt(x, z) < _g.WaterMargin)
                {
                    if (i == 0 || i == n) return false;
                    wet += step;
                    bridge = true;
                    prev = float.NaN;
                    continue;
                }
                if (!float.IsNaN(prev) && MathF.Abs(h - prev) / step > _t.MaxSlope) return false;
                prev = h;
            }
            return wet <= _t.BridgeMax;
        }

        /// <summary>
        /// Whether a piece may stand: on good ground, clear of every street not in <paramref name="exempt"/> by <c>streetClearance</c> along its part from
        /// <paramref name="from"/> to <paramref name="to"/> (shares of its length: the part away from the street it hangs off, so a branch to the other side of a
        /// street may stand close to this one, but not parallel to it) and by 0.45 of that along the whole of it (so it does not cross or graze a street).
        /// </summary>
        bool Fits(float ax, float az, float bx, float bz, HashSet<int> exempt, out bool bridge, float from = 0.4f, float to = 1f)
        {
            if (!Probe(ax, az, bx, bz, out bridge)) return false;
            float px = ax + (bx - ax) * from, pz = az + (bz - az) * from, qx = ax + (bx - ax) * to, qz = az + (bz - az) * to;
            for (int f = 0; f < _edges.Count; f++)
            {
                if (exempt.Contains(f)) continue;
                var e = _edges[f];
                float x0 = _nodes[e.A].X, z0 = _nodes[e.A].Z, x1 = _nodes[e.B].X, z1 = _nodes[e.B].Z;
                if (SegmentSegment(ax, az, bx, bz, x0, z0, x1, z1) < _t.StreetClearance * 0.45f) return false;
                if (SegmentSegment(px, pz, qx, qz, x0, z0, x1, z1) < _t.StreetClearance) return false;
            }
            return true;
        }

        // ---- Exempt sets --------------------------------------------------------------------------------------------

        /// <summary>Follows a street away from <paramref name="node"/> over nodes where it only goes straight on (two edges), up to <paramref name="cap"/> metres. Returns the node it stops at.</summary>
        int Follow(int node, int via, HashSet<int> set, float cap, out float run)
        {
            run = 0f;
            while (_adj[node].Count == 2 && run < cap)
            {
                int next = _adj[node][0] == via ? _adj[node][1] : _adj[node][0];
                set.Add(next);
                run += Length(next);
                node = Other(next, node);
                via = next;
            }
            return node;
        }

        /// <summary>A street through an edge: the edge and what continues it at nodes with nothing else joining.</summary>
        HashSet<int> StreetOf(int edge)
        {
            var set = new HashSet<int> { edge };
            Follow(_edges[edge].A, edge, set, 180f, out _);
            Follow(_edges[edge].B, edge, set, 180f, out _);
            return set;
        }

        /// <summary>The streets at a node: each edge there and what continues it.</summary>
        HashSet<int> StreetsAt(int node)
        {
            var set = new HashSet<int>();
            foreach (int e in _adj[node])
            {
                set.Add(e);
                Follow(Other(e, node), e, set, 180f, out _);
            }
            return set;
        }

        /// <summary>A stub: its edges back to the junction it hangs off, and every edge at that junction (the new piece moves away from them). <paramref name="run"/> is the stub's length.</summary>
        HashSet<int> StubOf(int end, out float run)
        {
            int e0 = _adj[end][0];
            var set = new HashSet<int> { e0 };
            int stop = Follow(Other(e0, end), e0, set, 400f, out run);
            run += Length(e0);
            foreach (int e in _adj[stop]) set.Add(e);
            return set;
        }

        // ---- The frame: seed roads and the ring ---------------------------------------------------------------------

        void BuildFrame()
        {
            int roads = _size switch { TownSize.Village => _t.SeedRoadsVillage, TownSize.Medium => _t.SeedRoadsMedium, _ => _t.SeedRoadsMetropolis };
            float baseAngle = ContourAngle() ?? _rng.NextFloat() * MathF.PI;
            float limit = _r * _t.OuterReach + _t.SeedReach;
            for (int k = 0; k < roads; k++)
            {
                float angle = baseAngle + k * MathF.PI / roads + (_rng.NextFloat() - 0.5f) * 0.15f;
                float offset = k == 0 ? 0f : (_rng.NextFloat() < 0.5f ? -1f : 1f) * (0.06f + 0.18f * _rng.NextFloat()) * _r;
                float ox = _cx - MathF.Sin(angle) * offset, oz = _cz + MathF.Cos(angle) * offset;
                int origin = AddNode(ox, oz);
                for (int dir = 0; dir < 2; dir++)
                {
                    float a0 = angle + dir * MathF.PI;
                    int node = origin;
                    float x = ox, z = oz, walked = 0f, drift = 0f;
                    while (walked < limit)
                    {
                        float length = _t.PieceLengthMin + _rng.NextFloat() * (_t.PieceLengthMax - _t.PieceLengthMin);
                        drift = Math.Clamp(drift + (_rng.NextFloat() - 0.5f) * 0.2f, -0.26f, 0.26f);
                        float bx = x + MathF.Cos(a0 + drift) * length, bz = z + MathF.Sin(a0 + drift) * length;
                        if (!Probe(x, z, bx, bz, out bool bridge)) break;
                        int next = AddNode(bx, bz);
                        bool main = k == 0 && Dist((x + bx) * 0.5f, (z + bz) * 0.5f, _cx, _cz) <= _r;
                        AddEdge(node, next, main, bridge, seed: true);
                        node = next;
                        x = bx;
                        z = bz;
                        walked += length;
                    }
                }
            }
            if (_size == TownSize.Metropolis) BuildRing();
            SplitCrossings();
            CollapseShort();
            KeepConnected();
        }

        /// <summary>One street around the centre at <c>ringRadius</c>, in sides of about 70 m; a side that does not fit is left out.</summary>
        void BuildRing()
        {
            float ringR = _r * _t.RingRadius;
            int count = Math.Max(8, (int)MathF.Ceiling(MathF.Tau * ringR / 70f));
            float start = _rng.NextFloat() * MathF.Tau;
            var ring = new int[count];
            for (int i = 0; i < count; i++)
            {
                float a = start + i * MathF.Tau / count, rr = ringR * (1f + (_rng.NextFloat() - 0.5f) * 0.12f);
                ring[i] = AddNode(_cx + MathF.Cos(a) * rr, _cz + MathF.Sin(a) * rr);
            }
            for (int i = 0; i < count; i++)
            {
                var (ax, az) = _nodes[ring[i]];
                var (bx, bz) = _nodes[ring[(i + 1) % count]];
                if (Probe(ax, az, bx, bz, out bool bridge)) AddEdge(ring[i], ring[(i + 1) % count], false, bridge, seed: true);
            }
        }

        /// <summary>Puts a node where two frame edges cross.</summary>
        void SplitCrossings()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < _edges.Count && !changed; i++)
                    for (int j = i + 1; j < _edges.Count && !changed; j++)
                    {
                        var a = _edges[i]; var b = _edges[j];
                        if (a.A == b.A || a.A == b.B || a.B == b.A || a.B == b.B) continue;
                        var (ax, az) = _nodes[a.A]; var (bx, bz) = _nodes[a.B];
                        var (cx, cz) = _nodes[b.A]; var (dx, dz) = _nodes[b.B];
                        if (!Crossing(ax, az, bx, bz, cx, cz, dx, dz, out float t, out _)) continue;
                        int node = AddNode(ax + (bx - ax) * t, az + (bz - az) * t);
                        SplitEdge(i, node);
                        SplitEdge(j, node);
                        changed = true;
                    }
            }
        }

        /// <summary>Joins the nodes of every edge shorter than a road can be (a crossing next to a node leaves one) into one node at their middle.</summary>
        void CollapseShort()
        {
            float shortest = ShortestPiece;
            var group = Enumerable.Range(0, _nodes.Count).ToArray();
            int Find(int n) { while (group[n] != n) n = group[n] = group[group[n]]; return n; }
            bool any = false;
            for (int e = 0; e < _edges.Count; e++)
                if (Length(e) < shortest) { group[Find(_edges[e].A)] = Find(_edges[e].B); any = true; }
            if (!any) return;
            var sum = new (float X, float Z, int N)[_nodes.Count];
            for (int n = 0; n < _nodes.Count; n++)
            {
                int g = Find(n);
                sum[g] = (sum[g].X + _nodes[n].X, sum[g].Z + _nodes[n].Z, sum[g].N + 1);
            }
            var oldEdges = _edges;
            var map = new int[_nodes.Count];
            var oldNodes = _nodes;
            _nodes = new();
            _edges = new();
            _adj = new();
            for (int n = 0; n < oldNodes.Count; n++)
                if (Find(n) == n) map[n] = AddNode(sum[n].X / sum[n].N, sum[n].Z / sum[n].N);
            var seen = new HashSet<(int, int)>();
            foreach (var e in oldEdges)
            {
                int a = map[Find(e.A)], b = map[Find(e.B)];
                if (a == b || !seen.Add((Math.Min(a, b), Math.Max(a, b)))) continue;
                AddEdge(a, b, e.Main, e.Bridge, e.Seed);
            }
        }

        /// <summary>Drops the parts of the frame (a ring side cut off by water) that do not join the first seed road (or the first street there is), and nodes nothing uses.</summary>
        void KeepConnected()
        {
            var seen = new bool[_nodes.Count];
            var queue = new Queue<int>();
            int first = 0;
            for (int i = 0; i < _nodes.Count; i++)
                if (_adj[i].Count > 0) { first = i; break; }
            if (_nodes.Count > 0) { seen[first] = true; queue.Enqueue(first); }
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                foreach (int e in _adj[n])
                {
                    int o = Other(e, n);
                    if (!seen[o]) { seen[o] = true; queue.Enqueue(o); }
                }
            }
            var oldNodes = _nodes;
            var oldEdges = _edges;
            _nodes = new();
            _edges = new();
            _adj = new();
            var map = new int[oldNodes.Count];
            for (int i = 0; i < oldNodes.Count; i++) map[i] = seen[i] && HasEdge(oldEdges, i) ? AddNode(oldNodes[i].X, oldNodes[i].Z) : -1;
            foreach (var e in oldEdges)
                if (map[e.A] >= 0 && map[e.B] >= 0) AddEdge(map[e.A], map[e.B], e.Main, e.Bridge, e.Seed);
        }

        static bool HasEdge(List<Edge> edges, int node) => edges.Any(e => e.A == node || e.B == node);

        /// <summary>Along the contours of a slope; null on level ground.</summary>
        float? ContourAngle()
        {
            if (_terrain is not { } terrain) return null;
            const float d = 60f;
            float gx = terrain.HeightAt(_cx + d, _cz) - terrain.HeightAt(_cx - d, _cz);
            float gz = terrain.HeightAt(_cx, _cz + d) - terrain.HeightAt(_cx, _cz - d);
            if (MathF.Sqrt(gx * gx + gz * gz) < 0.5f) return null;
            return MathF.Atan2(gz, gx) + MathF.PI * 0.5f;
        }

        // ---- Growth -------------------------------------------------------------------------------------------------

        /// <summary>One try at one piece of street; whether it was added.</summary>
        bool Step()
        {
            // An edge, toward the centre, and less the more tries it has failed.
            var weights = new float[_edges.Count];
            float total = 0f;
            for (int e = 0; e < _edges.Count; e++)
            {
                if (_edges[e].Bridge) continue;
                var (ax, az) = _nodes[_edges[e].A];
                var (bx, bz) = _nodes[_edges[e].B];
                float d = Dist((ax + bx) * 0.5f, (az + bz) * 0.5f, _cx, _cz) / _r;
                float f = 1f + _edges[e].Fails;
                total += weights[e] = Length(e) * MathF.Exp(-d / 0.6f) / (f * f);
            }
            if (total <= 0f) return false;
            float roll = _rng.NextFloat() * total;
            int pick = 0;
            for (int e = 0; e < weights.Length; e++)
            {
                roll -= weights[e];
                if (roll < 0f) { pick = e; break; }
            }

            var mid = (X: (_nodes[_edges[pick].A].X + _nodes[_edges[pick].B].X) * 0.5f, Z: (_nodes[_edges[pick].A].Z + _nodes[_edges[pick].B].Z) * 0.5f);
            float ratio = Dist(mid.X, mid.Z, _cx, _cz) / _r;
            var w = _t.StepWeights.FirstOrDefault(s => ratio <= s.Until) ?? _t.StepWeights[^1];
            int kind = TownGenerator.Pick(new[] { w.Extend, w.Branch, w.Link }, _rng.NextFloat());
            bool ok = kind switch { 0 => Extend(pick, mid.X, mid.Z) ?? Branch(pick), 1 => Branch(pick), _ => Link(pick, mid.X, mid.Z) };
            if (!ok) _edges[pick].Fails++;
            return ok;
        }

        float RandomLength() => _t.PieceLengthMin + _rng.NextFloat() * (_t.PieceLengthMax - _t.PieceLengthMin);

        /// <summary>A small random bend, and a pull toward the contour on a slope.</summary>
        float Bend(float dir, float x, float z)
        {
            float bend = (_rng.NextFloat() - 0.5f) * 0.28f;
            if (_terrain is { } terrain)
            {
                const float d = 12f;
                float gx = terrain.HeightAt(x + d, z) - terrain.HeightAt(x - d, z);
                float gz = terrain.HeightAt(x, z + d) - terrain.HeightAt(x, z - d);
                float slope = MathF.Sqrt(gx * gx + gz * gz) / (2f * d);
                if (slope > 0.02f)
                {
                    float diff = MathF.Atan2(gz, gx) + MathF.PI * 0.5f - dir;
                    diff = MathF.IEEERemainder(diff, MathF.PI); // the contour runs both ways: the nearer one
                    bend += 0.5f * Math.Clamp(diff, -0.4f, 0.4f) * MathF.Min(1f, slope / 0.1f);
                }
            }
            return dir + bend;
        }

        /// <summary>The free end of a street that is not a frame road, nearest a point; -1 when there is none.</summary>
        int NearestFreeEnd(float x, float z)
        {
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_adj[n].Count != 1 || _edges[_adj[n][0]].Seed) continue;
                float d = Dist(_nodes[n].X, _nodes[n].Z, x, z);
                if (d < bestDistance) { best = n; bestDistance = d; }
            }
            return best;
        }

        /// <summary>A piece straight on from a free end near the pick; null when there is no end to go on from (the step becomes a branch).</summary>
        bool? Extend(int pick, float x, float z)
        {
            int end = NearestFreeEnd(x, z);
            if (end < 0) return null;
            int back = Other(_adj[end][0], end);
            var (ex, ez) = _nodes[end];
            float dir = Bend(MathF.Atan2(ez - _nodes[back].Z, ex - _nodes[back].X), ex, ez);
            float length = RandomLength();
            var exempt = StubOf(end, out float run);
            if (run + length > _t.MaxDeadEnd) return false;
            float bx = ex + MathF.Cos(dir) * length, bz = ez + MathF.Sin(dir) * length;
            if (Dist(bx, bz, _cx, _cz) > _r * _t.OuterReach) return false;
            if (!Fits(ex, ez, bx, bz, exempt, out bool bridge)) return false;
            Commit(new Anchor(end, -1, ex, ez), new Anchor(-1, -1, bx, bz), bridge);
            return true;
        }

        /// <summary>A piece across a street, from a node or a point along the picked edge, to one side or the other. Tries a few places along the edge.</summary>
        bool Branch(int pick)
        {
            var edge = _edges[pick];
            var (ax, az) = _nodes[edge.A];
            var (bx, bz) = _nodes[edge.B];
            float along = MathF.Atan2(bz - az, bx - ax);
            for (int place = 0; place < 4; place++)
            {
                Anchor from;
                HashSet<int> exempt;
                int endpoint = _rng.NextFloat() < 0.5f ? edge.A : edge.B;
                if (_adj[endpoint].Count == 2 && _rng.NextFloat() < 0.5f)
                {
                    from = new Anchor(endpoint, -1, _nodes[endpoint].X, _nodes[endpoint].Z);
                    exempt = StreetsAt(endpoint);
                }
                else
                {
                    if (!SplitRange(pick, out float t0, out float t1)) continue;
                    float t = t0 + (t1 - t0) * _rng.NextFloat();
                    from = new Anchor(-1, pick, ax + (bx - ax) * t, az + (bz - az) * t);
                    exempt = StreetOf(pick);
                }
                float side = _rng.NextFloat() < 0.5f ? 1f : -1f;
                float length = RandomLength();
                float jitter = (_rng.NextFloat() - 0.5f) * 0.52f;
                for (int attempt = 0; attempt < 2; attempt++, side = -side)
                {
                    float dir = Bend(along + side * (MathF.PI * 0.5f + jitter), from.X, from.Z);
                    float cx = from.X + MathF.Cos(dir) * length, cz = from.Z + MathF.Sin(dir) * length;
                    if (Dist(cx, cz, _cx, _cz) > _r * _t.OuterReach) continue;
                    if (!Fits(from.X, from.Z, cx, cz, exempt, out bool bridge)) continue;
                    Commit(from, new Anchor(-1, -1, cx, cz), bridge);
                    return true;
                }
            }
            return false;
        }

        /// <summary>A piece from a free end or a point of the picked edge to the nearest street within reach, which closes a block.</summary>
        bool Link(int pick, float x, float z)
        {
            Anchor from;
            HashSet<int> exempt;
            float outward = float.NaN; // for a free end: the way the street was going
            int end = _rng.NextFloat() < 0.5f ? NearestFreeEnd(x, z) : -1;
            if (end >= 0)
            {
                var (ex, ez) = _nodes[end];
                int back = Other(_adj[end][0], end);
                from = new Anchor(end, -1, ex, ez);
                exempt = StubOf(end, out _);
                outward = MathF.Atan2(ez - _nodes[back].Z, ex - _nodes[back].X);
            }
            else
            {
                var edge = _edges[pick];
                if (!SplitRange(pick, out float t0, out float t1)) return false;
                float t = t0 + (t1 - t0) * _rng.NextFloat();
                var (ax, az) = _nodes[edge.A];
                var (bx, bz) = _nodes[edge.B];
                from = new Anchor(-1, pick, ax + (bx - ax) * t, az + (bz - az) * t);
                exempt = StreetOf(pick);
            }

            // Candidates: nodes, and the nearest point of edges, by distance.
            float minLink = _t.PieceLengthMin * 0.7f;
            var candidates = new List<(float D, Anchor To)>();
            for (int n = 0; n < _nodes.Count; n++)
            {
                float d = Dist(_nodes[n].X, _nodes[n].Z, from.X, from.Z);
                if (d < minLink || d > _t.LinkReach || _adj[n].Any(exempt.Contains)) continue;
                candidates.Add((d, new Anchor(n, -1, _nodes[n].X, _nodes[n].Z)));
            }
            for (int f = 0; f < _edges.Count; f++)
            {
                var e = _edges[f];
                if (exempt.Contains(f) || e.Bridge) continue;
                var (ax, az) = _nodes[e.A];
                var (bx, bz) = _nodes[e.B];
                float sx = bx - ax, sz = bz - az, l2 = sx * sx + sz * sz;
                float t = ((from.X - ax) * sx + (from.Z - az) * sz) / l2;
                if (!SplitRange(f, out float t0, out float t1) || t < t0 || t > t1) continue;
                float qx = ax + sx * t, qz = az + sz * t, d = Dist(qx, qz, from.X, from.Z);
                if (d < minLink || d > _t.LinkReach) continue;
                candidates.Add((d, new Anchor(-1, f, qx, qz)));
            }
            candidates.Sort((a, b) => a.D.CompareTo(b.D));

            int tries = 0;
            foreach (var (_, to) in candidates)
            {
                if (tries++ == 6) break;
                float dir = MathF.Atan2(to.Z - from.Z, to.X - from.X);
                if (!float.IsNaN(outward) && MathF.Abs(MathF.IEEERemainder(dir - outward, MathF.Tau)) > 1.75f) continue; // not back on itself
                if (float.IsNaN(outward) && !AcrossEdge(pick, dir)) continue;
                if (!MeetsAtAngle(to, dir)) continue;
                var all = new HashSet<int>(exempt);
                all.UnionWith(to.Node >= 0 ? StreetsAt(to.Node) : StreetOf(to.Edge));
                if (!Fits(from.X, from.Z, to.X, to.Z, all, out bool bridge, 0.3f, 0.7f)) continue;
                Commit(from, to, bridge);
                return true;
            }
            return false;
        }

        /// <summary>The part of an edge (as fractions of its length) where a junction can stand: a road piece shorter than <see cref="RoadNetwork.MinRoadLength"/> cannot be built. False when the edge is too short for one.</summary>
        bool SplitRange(int edge, out float from, out float to)
        {
            float margin = ShortestPiece / Length(edge);
            from = MathF.Max(0.15f, margin);
            to = MathF.Min(0.85f, 1f - margin);
            return from < to;
        }

        /// <summary>Whether a direction is across an edge: 55 to 125 degrees to it.</summary>
        bool AcrossEdge(int edge, float dir)
        {
            var e = _edges[edge];
            float along = MathF.Atan2(_nodes[e.B].Z - _nodes[e.A].Z, _nodes[e.B].X - _nodes[e.A].X);
            return MathF.Abs(MathF.IEEERemainder(dir - along, MathF.PI)) >= 0.96f;
        }

        /// <summary>Whether a piece arriving at a target in direction <paramref name="dir"/> leaves at least 60 degrees to every street there.</summary>
        bool MeetsAtAngle(Anchor to, float dir)
        {
            const float minAngle = 1.05f;
            if (to.Node >= 0)
            {
                foreach (int e in _adj[to.Node])
                {
                    int o = Other(e, to.Node);
                    float away = MathF.Atan2(_nodes[o].Z - to.Z, _nodes[o].X - to.X);
                    if (MathF.Abs(MathF.IEEERemainder(away - (dir + MathF.PI), MathF.Tau)) < minAngle) return false;
                }
                return true;
            }
            var edge = _edges[to.Edge];
            float along = MathF.Atan2(_nodes[edge.B].Z - _nodes[edge.A].Z, _nodes[edge.B].X - _nodes[edge.A].X);
            return MathF.Abs(MathF.IEEERemainder(dir - along, MathF.PI)) > minAngle && MathF.Abs(MathF.IEEERemainder(dir - along, MathF.PI)) < MathF.PI - minAngle;
        }

        /// <summary>Adds the piece to the graph, drops the lots it runs near, and lays and builds the lots along it.</summary>
        void Commit(Anchor from, Anchor to, bool bridge)
        {
            int a = Resolve(from);
            int b = to.Node >= 0 || to.Edge >= 0 ? Resolve(to) : AddNode(to.X, to.Z);
            int edge = AddEdge(a, b, false, bridge, false);
            var (ax, az) = _nodes[a];
            var (bx, bz) = _nodes[b];
            foreach (var lot in _lots)
            {
                if (lot.Dead || PointSegment(lot.Site.X, lot.Site.Z, ax, az, bx, bz) >= _narrow) continue;
                lot.Dead = true;
                if (lot.Plan != null) _built--;
            }
            AddLots(edge);
        }

        // ---- Lots ---------------------------------------------------------------------------------------------------

        /// <summary>The lots on both sides of an edge, one every <c>lotWidth</c> at the setback; each is built on or left free by the chances of its ring.</summary>
        void AddLots(int edge)
        {
            var e = _edges[edge];
            if (e.Bridge) return;
            var (x0, z0) = _nodes[e.A];
            var (x1, z1) = _nodes[e.B];
            float len = Dist(x0, z0, x1, z1);
            if (len < 1f) return;
            if (e.Seed && Dist((x0 + x1) * 0.5f, (z0 + z1) * 0.5f, _cx, _cz) > _r * _t.OuterReach) return; // the country road beyond the town has no houses
            float setback = e.Main ? _setback : _narrow * 1.05f; // a lesser street is not widened, so its houses stand closer; the margin keeps a lot from failing the clearance of the next piece of the same street
            float dx = (x1 - x0) / len, dz = (z1 - z0) / len;
            int count = Math.Max(1, (int)MathF.Round(len / _g.LotWidth));
            for (int k = 0; k < count; k++)
            {
                float along = len * (k + 0.5f) / count;
                for (int side = 0; side < 2; side++)
                {
                    float nx = side == 0 ? -dz : dz, nz = side == 0 ? dx : -dx;
                    float x = x0 + dx * along + nx * setback, z = z0 + dz * along + nz * setback;
                    if (_terrain is { } terrain && (MathF.Abs(x) > terrain.HalfSize - 8f || MathF.Abs(z) > terrain.HalfSize - 8f
                        || terrain.HeightAt(x, z) - terrain.WaterSurfaceAt(x, z) < _g.WaterMargin)) continue;
                    if (NearOtherStreet(edge, x, z) || Crowded(x, z)) continue;
                    float ratio = Dist(x, z, _cx, _cz) / _r;
                    var site = new TownGenerator.LotSite(-1, -1, side, k, x, z, MathF.Atan2(-nz, -nx), ratio, 0f);
                    // Drawn in a fixed order for every lot, built on or not, so one lot's fate never shifts another's.
                    float fillRoll = _rng.NextFloat(), typeRoll = _rng.NextFloat(), tierRoll = _rng.NextFloat();
                    float noise = (_rng.NextFloat() - 0.5f) * TownGenerator.RingNoise;
                    var ring = TownGenerator.RingAt(_tuning, ratio + noise);
                    var lot = new Lot { Site = site };
                    if (fillRoll < ring.Fill)
                    {
                        lot.Plan = new BuildingPlan((BuildingType)TownGenerator.Pick(ring.Mix, typeRoll), TownGenerator.Pick(ring.Tiers, tierRoll), x, z, site.Facing);
                        _built++;
                    }
                    var cell = ((int)MathF.Floor(x / _apart), (int)MathF.Floor(z / _apart));
                    if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = new List<int>();
                    list.Add(_lots.Count);
                    _lots.Add(lot);
                }
            }
        }

        /// <summary>Whether a street other than <paramref name="except"/> runs closer than a building of the biggest size can stand.</summary>
        bool NearOtherStreet(int except, float x, float z)
        {
            for (int i = 0; i < _edges.Count; i++)
            {
                if (i == except) continue;
                float within = _edges[i].Main ? _setback : _narrow;
                if (EdgeDistance(i, x, z) < within) return true;
            }
            return false;
        }

        bool Crowded(float x, float z)
        {
            int cx0 = (int)MathF.Floor(x / _apart), cz0 = (int)MathF.Floor(z / _apart);
            for (int a = cx0 - 1; a <= cx0 + 1; a++)
                for (int b = cz0 - 1; b <= cz0 + 1; b++)
                    if (_cells.TryGetValue((a, b), out var near))
                        foreach (int l in near)
                            if (!_lots[l].Dead && (_lots[l].Site.X - x) * (_lots[l].Site.X - x) + (_lots[l].Site.Z - z) * (_lots[l].Site.Z - z) < _apart * _apart) return true;
            return false;
        }
    }
}
