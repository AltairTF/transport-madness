namespace TransportMadness.Sim;

/// <summary>The size class of a town: how many seed roads, how many people and how it is laid out.</summary>
public enum TownSize : byte { Village, Medium, Metropolis }

/// <summary>A street between two nodes of a <see cref="StreetGraph"/>. <c>Seed</c>: part of the frame (a through road), which runs out of the town and may end free. <c>Urban</c>: inside the built-up area (kerb and pavement), as against a country road.</summary>
public readonly record struct StreetEdge(int A, int B, bool Main, bool Bridge, bool Seed, bool Urban = true);

/// <summary>The streets of one town as the generator lays them out: nodes in the map's axes and the streets between them.</summary>
public sealed record StreetGraph(IReadOnlyList<(float X, float Z)> Nodes, IReadOnlyList<StreetEdge> Edges);

/// <summary>A town as the generator leaves it: its streets, every lot along them (the buildings stand on some), and how far from its centre they reach.</summary>
public sealed record OrganicTown(StreetGraph Graph, IReadOnlyList<TownGenerator.LotSite> Lots, IReadOnlyList<BuildingPlan> Buildings, float Radius);

/// <summary>
/// A town built up street by street (docs/town-upgrade-plan.md). Two or three seed roads cross at the square and run out of the town; a collector
/// street leaves each of them at right angles at every block step, on both sides. Then, from the middle out, the generator takes the nearest
/// of two kinds of step until the homes hold the size class's people: a collector takes one more step away from its seed road (only once a local
/// street has closed a block at its end), or a local street joins the ends of two neighbouring collectors at the same depth and closes a block.
/// A street is refused over water, on ground that is too steep, off the map, across or too near another street. The lots along each street
/// stand in a row facing it; their width and the biggest building on them come from the ring they lie in. Pure: it does not touch the road
/// network, and the same inputs give the same town.
/// </summary>
public static class StreetGenerator
{
    /// <summary>No piece of street is shorter: two junctions closer than this (a junction reaches 8 m into each road) leave cars no room between them.</summary>
    const float ShortestPiece = 30f;
    const ulong SeedSalt = 0x5354524545545234UL;
    const float LotCell = 32f;      // side of the squares the lots are filed in
    const float LotTolerance = 0.97f; // a lot may be this share of the way it needs from a street

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
        public int A, B;
        public bool Main, Bridge, Seed, Dead;
    }

    sealed class Lot
    {
        public TownGenerator.LotSite Site;
        public BuildingPlan? Plan;
        public bool Dead;
        public int Edge;   // the street it faces
        public float Half; // half the side of the biggest building the lot takes
    }

    /// <summary>A street that leaves a seed road at a right angle: the nodes along it, one per step.</summary>
    sealed class Collector
    {
        public readonly List<int> Nodes = new();
        public float Dir, NextDir, NextLen, Bias = 1f;
        public bool Ended, HasNext;
        public int Linked; // the deepest step with a local street across its end
        public int Depth => Nodes.Count - 1;
    }

    /// <summary>Two neighbouring collectors on the same side of a seed road: a local street joins their ends at each depth, one after another.</summary>
    sealed class Pair
    {
        public Collector A = null!, B = null!;
        public int Next = 1;
    }

    sealed class Layout
    {
        readonly Terrain? _terrain;
        readonly TownsTuning _tuning;
        readonly TownGenerationTuning _g;
        readonly StreetsTuning _t;
        readonly TownSize _size;
        readonly float _cx, _cz, _r;
        readonly SimRandom _rng;
        readonly float[] _half = new float[TownsTuning.Tiers]; // half the side of the biggest building of a size

        List<(float X, float Z)> _nodes = new();
        List<Edge> _edges = new();
        List<List<int>> _adj = new();
        readonly List<Lot> _lots = new();
        readonly Dictionary<(int, int), List<int>> _cells = new();
        readonly HashSet<int> _slotNodes = new();
        readonly HashSet<int> _active = new(); // seed edges that have their lots
        readonly List<Collector> _collectors = new();
        readonly List<Pair> _pairs = new();
        int _people;

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
            for (int tier = 0; tier < _half.Length; tier++)
                foreach (var type in Enum.GetValues<BuildingType>())
                    _half[tier] = MathF.Max(_half[tier], tuning.Of(type, tier).Size);
        }

        int Target => _size switch { TownSize.Village => _t.PopulationVillage, TownSize.Medium => _t.PopulationMedium, _ => _t.PopulationMetropolis };

        public OrganicTown Run()
        {
            BuildFrame();
            MakeCollectors();
            // The middle of the frame is built along from the start; the rest as collectors leave it.
            for (int e = 0; e < _edges.Count; e++)
                if (Dist((_nodes[_edges[e].A].X + _nodes[_edges[e].B].X) * 0.5f, (_nodes[_edges[e].A].Z + _nodes[_edges[e].B].Z) * 0.5f, _cx, _cz) < 0.2f * _r) Activate(e);
            // Grown in two goes with the gaps filled in between, so the filling does not overshoot the people by much.
            foreach (float share in new[] { 0.7f, 1f })
            {
                for (int guard = 0; _people < Target * share && guard < 50000; guard++)
                    if (!Step()) break;
                PruneDeadEnds();
                Infill();
            }

            var lots = new List<TownGenerator.LotSite>();
            var buildings = new List<BuildingPlan>();
            var distances = new List<float>();
            foreach (var lot in _lots)
            {
                if (lot.Dead || _edges[lot.Edge].Dead) continue;
                lots.Add(lot.Site);
                if (lot.Plan is { } plan) buildings.Add(plan);
                distances.Add(Dist(lot.Site.X, lot.Site.Z, _cx, _cz));
            }
            // How far the town reaches: where all but a few of its lots lie (a few houses stand along the country roads further out).
            distances.Sort();
            float reach = distances.Count > 0 ? MathF.Max(0.3f * _r, distances[(int)((distances.Count - 1) * 0.98f)]) + 15f : 0.3f * _r;
            var map = new int[_nodes.Count];
            var kept = new List<(float X, float Z)>();
            Array.Fill(map, -1);
            var edges = new List<StreetEdge>();
            foreach (var e in _edges)
            {
                if (e.Dead) continue;
                foreach (int n in new[] { e.A, e.B })
                    if (map[n] < 0) { map[n] = kept.Count; kept.Add(_nodes[n]); }
                edges.Add(new StreetEdge(map[e.A], map[e.B], e.Main, e.Bridge, e.Seed,
                    !e.Seed || Dist((_nodes[e.A].X + _nodes[e.B].X) * 0.5f, (_nodes[e.A].Z + _nodes[e.B].Z) * 0.5f, _cx, _cz) <= reach));
            }
            return new OrganicTown(new StreetGraph(kept, edges), lots, buildings, reach);
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
            int second = AddEdge(node, b, e.Main, e.Bridge, e.Seed);
            if (_active.Contains(edge)) _active.Add(second);
        }

        /// <summary>A seed road gets its lots once the town reaches it (a collector leaves it); other streets get theirs as they are laid.</summary>
        void Activate(int edge)
        {
            if (_edges[edge].Seed && _active.Add(edge)) AddLots(edge);
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
        /// Whether a street from a to b may stand: on good ground with no water, crossing no street and keeping <c>streetClearance</c> from every street
        /// that does not touch one of <paramref name="joined"/> (the nodes it joins).
        /// </summary>
        bool Fits(float ax, float az, float bx, float bz, params int[] joined)
        {
            if (!Probe(ax, az, bx, bz, out bool bridge) || bridge) return false;
            for (int f = 0; f < _edges.Count; f++)
            {
                var e = _edges[f];
                if (e.Dead || Array.IndexOf(joined, e.A) >= 0 || Array.IndexOf(joined, e.B) >= 0) continue;
                float x0 = _nodes[e.A].X, z0 = _nodes[e.A].Z, x1 = _nodes[e.B].X, z1 = _nodes[e.B].Z;
                if (SegmentSegment(ax, az, bx, bz, x0, z0, x1, z1) < _t.StreetClearance) return false;
            }
            return true;
        }

        // ---- The frame: seed roads -----------------------------------------------------------------------------------

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
                        // One block step: a node every block, so a collector can leave at each. A turn at a node is small (about 7 degrees).
                        float length = _t.BlockMin + _rng.NextFloat() * (_t.BlockMax - _t.BlockMin);
                        drift = Math.Clamp(drift + (_rng.NextFloat() - 0.5f) * 0.24f, -0.26f, 0.26f);
                        float bx = x + MathF.Cos(a0 + drift) * length, bz = z + MathF.Sin(a0 + drift) * length;
                        if (!Probe(x, z, bx, bz, out bool bridge)) break;
                        int next = AddNode(bx, bz);
                        bool main = k == 0 && Dist((x + bx) * 0.5f, (z + bz) * 0.5f, _cx, _cz) <= _r * _t.MainReach;
                        AddEdge(node, next, main, bridge, seed: true);
                        node = next;
                        x = bx;
                        z = bz;
                        walked += length;
                    }
                }
            }
            SplitCrossings();
            CollapseShort();
            KeepConnected();
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
            var group = Enumerable.Range(0, _nodes.Count).ToArray();
            int Find(int n) { while (group[n] != n) n = group[n] = group[group[n]]; return n; }
            bool any = false;
            for (int e = 0; e < _edges.Count; e++)
                if (Length(e) < ShortestPiece) { group[Find(_edges[e].A)] = Find(_edges[e].B); any = true; }
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

        /// <summary>Drops the parts of the frame (a road cut off by water) that do not join the first seed road (or the first street there is), and nodes nothing uses.</summary>
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

        // ---- Collectors ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Two collectors (one each side) at every node of a seed road that only goes straight on and is not near a junction of it, and a pair of
        /// collectors for every two such nodes next to each other along the road, on each side.
        /// </summary>
        void MakeCollectors()
        {
            var junctions = Enumerable.Range(0, _nodes.Count).Where(n => _adj[n].Count >= 3).ToList();
            var slotOf = new Dictionary<int, (Collector[] Sides, float Tx, float Tz)>();
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_adj[n].Count != 2 || !_edges[_adj[n][0]].Seed || !_edges[_adj[n][1]].Seed) continue;
                if (Dist(_nodes[n].X, _nodes[n].Z, _cx, _cz) > _r * _t.OuterReach) continue;
                if (junctions.Any(j => Dist(_nodes[j].X, _nodes[j].Z, _nodes[n].X, _nodes[n].Z) < _t.SlotClearance)) continue;
                var o1 = _nodes[Other(_adj[n][0], n)];
                var o2 = _nodes[Other(_adj[n][1], n)];
                float ix = _nodes[n].X - o1.X, iz = _nodes[n].Z - o1.Z, jx = o2.X - _nodes[n].X, jz = o2.Z - _nodes[n].Z;
                float il = MathF.Sqrt(ix * ix + iz * iz), jl = MathF.Sqrt(jx * jx + jz * jz);
                if ((ix * jx + iz * jz) / (il * jl) < 0.8f) continue;
                float tx = o2.X - o1.X, tz = o2.Z - o1.Z, tl = MathF.Sqrt(tx * tx + tz * tz);
                tx /= tl;
                tz /= tl;
                float normal = MathF.Atan2(tx, -tz); // the left normal (-tz, tx)
                var sides = new Collector[2];
                for (int s = 0; s < 2; s++)
                {
                    var c = new Collector { Dir = normal + s * MathF.PI + (_rng.NextFloat() - 0.5f) * 0.1f, Bias = 0.96f + 0.08f * _rng.NextFloat() };
                    c.Nodes.Add(n);
                    sides[s] = c;
                    _collectors.Add(c);
                }
                slotOf[n] = (sides, tx, tz);
                _slotNodes.Add(n);
            }

            // Neighbours: walk along the road from a slot to the next, as long as nothing joins in between.
            foreach (var (node, slot) in slotOf.OrderBy(kv => kv.Key))
                foreach (int first in _adj[node])
                {
                    int via = first, at = Other(first, node);
                    float walked = Length(first);
                    while (!slotOf.ContainsKey(at) && _adj[at].Count == 2 && walked <= _t.BlockMax * 1.8f)
                    {
                        via = _adj[at][0] == via ? _adj[at][1] : _adj[at][0];
                        walked += Length(via);
                        at = Other(via, at);
                    }
                    if (!slotOf.TryGetValue(at, out var other) || at < node || walked > _t.BlockMax * 1.8f) continue;
                    float wx = _nodes[at].X - _nodes[node].X, wz = _nodes[at].Z - _nodes[node].Z;
                    foreach (float side in new[] { 1f, -1f })
                    {
                        float nx = -wz * side, nz = wx * side;
                        _pairs.Add(new Pair { A = Facing(slot.Sides, nx, nz), B = Facing(other.Sides, nx, nz) });
                    }
                }
        }

        static Collector Facing(Collector[] sides, float nx, float nz)
            => MathF.Cos(sides[0].Dir) * nx + MathF.Sin(sides[0].Dir) * nz > 0f ? sides[0] : sides[1];

        // ---- Growth -------------------------------------------------------------------------------------------------

        /// <summary>Takes the step nearest the middle of the town; whether there was one.</summary>
        bool Step()
        {
            float best = float.MaxValue;
            Collector? pickC = null;
            Pair? pickP = null;
            foreach (var c in _collectors)
            {
                if (c.Ended || c.Depth >= _t.MaxDepth || (c.Depth > 0 && c.Linked < c.Depth)) continue;
                if (!c.HasNext)
                {
                    c.NextLen = _t.DepthMin + _rng.NextFloat() * (_t.DepthMax - _t.DepthMin);
                    c.NextDir = c.Dir + (_rng.NextFloat() - 0.5f) * 0.08f;
                    c.HasNext = true;
                }
                var (lx, lz) = _nodes[c.Nodes[^1]];
                float d = Dist(lx + MathF.Cos(c.NextDir) * c.NextLen * 0.5f, lz + MathF.Sin(c.NextDir) * c.NextLen * 0.5f, _cx, _cz) * c.Bias;
                if (d < best) { best = d; pickC = c; pickP = null; }
            }
            foreach (var p in _pairs)
            {
                int n = p.Next;
                if (n > _t.MaxDepth) continue;
                if (p.A.Depth < n || p.B.Depth < n)
                {
                    if ((p.A.Ended && p.A.Depth < n) || (p.B.Ended && p.B.Depth < n)) p.Next = int.MaxValue;
                    continue;
                }
                var (ax, az) = _nodes[p.A.Nodes[n]];
                var (bx, bz) = _nodes[p.B.Nodes[n]];
                float d = Dist((ax + bx) * 0.5f, (az + bz) * 0.5f, _cx, _cz) - 8f; // a block is closed as soon as it can be
                if (d < best) { best = d; pickC = null; pickP = p; }
            }
            if (pickC != null) Extend(pickC);
            else if (pickP != null) Close(pickP);
            else return false;
            return true;
        }

        /// <summary>One more step of a collector, straight on with a slight turn (a few turns are tried: a crossing too near a junction of the street it crosses is dodged that way). Where none fits the collector ends, joining the street in its way if it reaches one at a fair angle.</summary>
        void Extend(Collector c)
        {
            int last = c.Nodes[^1];
            var (lx, lz) = _nodes[last];
            if (c.Depth == 0)
                foreach (int e in _adj[last].ToArray()) Activate(e);
            foreach (float turn in new[] { 0f, 0.07f, -0.07f, 0.14f, -0.14f })
            {
                float dir = c.NextDir + turn;
                float ex = lx + MathF.Cos(dir) * c.NextLen, ez = lz + MathF.Sin(dir) * c.NextLen;
                if (Dist(ex, ez, _cx, _cz) > _r * _t.OuterReach || !Lay(last, ex, ez, -1, out int end)) continue;
                c.Nodes.Add(end);
                c.Dir = dir;
                c.HasNext = false;
                return;
            }
            c.Ended = true;
            float jx = lx + MathF.Cos(c.NextDir) * c.NextLen, jz = lz + MathF.Sin(c.NextDir) * c.NextLen;
            if (Dist(jx, jz, _cx, _cz) <= _r * _t.OuterReach) Join(last, lx, lz, jx, jz);
        }

        /// <summary>
        /// Lays a straight street from node <paramref name="from"/> to (ex, ez), which is the node <paramref name="to"/> or a new one (-1). It runs through
        /// the streets it crosses (each crossing is a new junction: at 40 degrees or more, a short piece from the street's own junctions and from
        /// the other crossings and the ends) and keeps clear of the others. False, and nothing changed, when it does not fit.
        /// </summary>
        bool Lay(int from, float ex, float ez, int to, out int end)
        {
            end = -1;
            var (lx, lz) = _nodes[from];
            float len = Dist(lx, lz, ex, ez);
            var joined = new List<int> { from };
            if (to >= 0) joined.Add(to);
            var cross = new List<(float T, int Edge)>();
            for (int f = 0; f < _edges.Count; f++)
            {
                var e = _edges[f];
                if (e.Dead || e.A == from || e.B == from || e.A == to || e.B == to) continue;
                if (!Crossing(lx, lz, ex, ez, _nodes[e.A].X, _nodes[e.A].Z, _nodes[e.B].X, _nodes[e.B].Z, out float t, out _)) continue;
                float sx = _nodes[e.B].X - _nodes[e.A].X, sz = _nodes[e.B].Z - _nodes[e.A].Z;
                if (MathF.Abs((sx * (ex - lx) + sz * (ez - lz)) / (MathF.Sqrt(sx * sx + sz * sz) * len)) > 0.77f) return false;
                float px = lx + (ex - lx) * t, pz = lz + (ez - lz) * t;
                if (Dist(px, pz, _nodes[e.A].X, _nodes[e.A].Z) < ShortestPiece || Dist(px, pz, _nodes[e.B].X, _nodes[e.B].Z) < ShortestPiece) return false;
                cross.Add((t, f));
                joined.Add(e.A);
                joined.Add(e.B);
            }
            cross.Sort((a, b) => a.T.CompareTo(b.T));
            float previous = 0f;
            foreach (var (t, _) in cross)
            {
                if ((t - previous) * len < ShortestPiece) return false;
                previous = t;
            }
            if (cross.Count > 0 && (1f - previous) * len < ShortestPiece) return false;
            if (!Fits(lx, lz, ex, ez, joined.ToArray())) return false;

            int node = from;
            var made = new List<int>();
            foreach (var (t, f) in cross)
            {
                int x = AddNode(lx + (ex - lx) * t, lz + (ez - lz) * t);
                SplitEdge(f, x);
                made.Add(AddEdge(node, x, false, false, false));
                node = x;
            }
            end = to >= 0 ? to : AddNode(ex, ez);
            made.Add(AddEdge(node, end, false, false, false));
            foreach (int edge in made) Commit(edge);
            return true;
        }

        /// <summary>Ends the piece from (lx, lz) toward (ex, ez) at the first street it crosses (at a node of it when one is near), when that is at least a short piece away, crosses at 40 degrees or more and fits.</summary>
        void Join(int last, float lx, float lz, float ex, float ez)
        {
            float best = 2f;
            int hit = -1;
            for (int f = 0; f < _edges.Count; f++)
            {
                var e = _edges[f];
                if (e.Dead || e.A == last || e.B == last) continue;
                if (Crossing(lx, lz, ex, ez, _nodes[e.A].X, _nodes[e.A].Z, _nodes[e.B].X, _nodes[e.B].Z, out float t, out _) && t < best) { best = t; hit = f; }
            }
            if (hit < 0) return;
            var edge = _edges[hit];
            float len = Dist(lx, lz, ex, ez), px = lx + (ex - lx) * best, pz = lz + (ez - lz) * best;
            float sx = _nodes[edge.B].X - _nodes[edge.A].X, sz = _nodes[edge.B].Z - _nodes[edge.A].Z;
            if (MathF.Abs((sx * (ex - lx) + sz * (ez - lz)) / (MathF.Sqrt(sx * sx + sz * sz) * len)) > 0.77f) return;
            int target = Dist(px, pz, _nodes[edge.A].X, _nodes[edge.A].Z) < ShortestPiece ? edge.A : Dist(px, pz, _nodes[edge.B].X, _nodes[edge.B].Z) < ShortestPiece ? edge.B : -1;
            if (target >= 0) (px, pz) = _nodes[target];
            if (Dist(lx, lz, px, pz) < ShortestPiece + 10f || !Fits(lx, lz, px, pz, last, edge.A, edge.B)) return;
            if (target < 0)
            {
                target = AddNode(px, pz);
                SplitEdge(hit, target);
            }
            Commit(AddEdge(last, target, false, false, false));
        }

        /// <summary>A local street across the ends of two collectors at the same depth, which closes a block; refused when it is too short or long, meets either collector at a bad angle or does not fit.</summary>
        void Close(Pair p)
        {
            int n = p.Next++;
            int a = p.A.Nodes[n], b = p.B.Nodes[n];
            var (ax, az) = _nodes[a];
            var (bx, bz) = _nodes[b];
            float len = Dist(ax, az, bx, bz);
            if (len < 0.6f * _t.BlockMin || len > 1.5f * _t.BlockMax) return;
            if (!Square(p.A, n, ax, az, bx, bz, len) || !Square(p.B, n, bx, bz, ax, az, len)) return;
            if (!Lay(a, bx, bz, b, out _)) return;
            p.A.Linked = Math.Max(p.A.Linked, n);
            p.B.Linked = Math.Max(p.B.Linked, n);
        }

        /// <summary>Whether a street from a collector's node at depth <paramref name="n"/> toward (bx, bz) leaves its last piece at between 55 and 125 degrees.</summary>
        bool Square(Collector c, int n, float ax, float az, float bx, float bz, float len)
        {
            var (px, pz) = _nodes[c.Nodes[n - 1]];
            float ix = ax - px, iz = az - pz, il = MathF.Sqrt(ix * ix + iz * iz);
            return MathF.Abs((ix * (bx - ax) + iz * (bz - az)) / (il * len)) <= 0.57f;
        }

        /// <summary>Drops the lots a new street runs too near, and lays and builds the lots along it.</summary>
        void Commit(int edge)
        {
            var e = _edges[edge];
            var (ax, az) = _nodes[e.A];
            var (bx, bz) = _nodes[e.B];
            float hw = RoadNetwork.HalfWidth(e.Main ? RoadType.Avenue2x2 : RoadType.TwoLane) + _t.Pavement + _g.SetbackGap;
            foreach (var lot in _lots)
            {
                if (lot.Dead || PointSegment(lot.Site.X, lot.Site.Z, ax, az, bx, bz) >= (hw + lot.Half) * LotTolerance) continue;
                Kill(lot);
            }
            AddLots(edge);
        }

        void Kill(Lot lot)
        {
            lot.Dead = true;
            if (lot.Plan is { Type: BuildingType.Home } home) _people -= _tuning.Of(BuildingType.Home, home.Tier).People;
        }

        /// <summary>Takes the last piece off every street end that joins nothing and is further from its junction than <c>maxDeadEnd</c> (and its lots), until none is.</summary>
        void PruneDeadEnds()
        {
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int n = 0; n < _nodes.Count; n++)
                {
                    var live = _adj[n].Where(e => !_edges[e].Dead).ToList();
                    if (live.Count != 1 || _edges[live[0]].Seed) continue;
                    float run = 0f;
                    int node = n, via = live[0];
                    while (true)
                    {
                        run += Length(via);
                        node = Other(via, node);
                        var next = _adj[node].Where(e => !_edges[e].Dead).ToList();
                        if (next.Count != 2) break;
                        via = next[0] == via ? next[1] : next[0];
                    }
                    if (run <= _t.MaxDeadEnd) continue;
                    _edges[live[0]].Dead = true;
                    foreach (var lot in _lots)
                        if (!lot.Dead && lot.Edge == live[0]) Kill(lot);
                    changed = true;
                }
            }
        }

        // ---- Lots ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// The lots on both sides of an edge: a row facing the street, from the corner of the street at one end to the corner of the one at the other
        /// (a building stands clear of the pavement of a street that crosses), as many as the frontage of the ring allows, spread evenly. Each lot is
        /// built on or left free by the chances of its ring.
        /// </summary>
        void AddLots(int edge)
        {
            var e = _edges[edge];
            if (e.Bridge) return;
            var (x0, z0) = _nodes[e.A];
            var (x1, z1) = _nodes[e.B];
            float len = Dist(x0, z0, x1, z1);
            if (len < 1f) return;
            float mx = (x0 + x1) * 0.5f, mz = (z0 + z1) * 0.5f;
            if (e.Seed && Dist(mx, mz, _cx, _cz) > _r * _t.OuterReach) return; // the country road beyond the town has no houses
            var mid = TownGenerator.RingAt(_tuning, Dist(mx, mz, _cx, _cz) / _r);
            float cross = RoadNetwork.HalfWidth(RoadType.TwoLane) + _t.Pavement + _g.SetbackGap + _half[mid.MaxTier];
            float m0 = EndMargin(e.A, cross), m1 = EndMargin(e.B, cross);
            float span = len - m0 - m1;
            if (span < 0f) return;
            float frontage = mid.Frontage + (Promoted(e, mid) - mid.MaxTier) * 2f;
            int count = (int)MathF.Floor(span / frontage) + 1;
            float dx = (x1 - x0) / len, dz = (z1 - z0) / len;
            for (int k = 0; k < count; k++)
            {
                float along = count == 1 ? m0 + span * 0.5f : m0 + span * k / (count - 1);
                for (int side = 0; side < 2; side++) PlaceLot(edge, x0 + dx * along, z0 + dz * along, dx, dz, side, k);
            }
        }

        /// <summary>
        /// A lot on one side of the street at a point along it, when a building can stand there: on the map and dry, clear of every other street and
        /// of the lots already laid. It is built on or left free by the chances of its ring.
        /// </summary>
        void PlaceLot(int edge, float px, float pz, float dx, float dz, int side, int k)
        {
            var e = _edges[edge];
            float roadHalf = RoadNetwork.HalfWidth(e.Main ? RoadType.Avenue2x2 : RoadType.TwoLane) + _t.Pavement + _g.SetbackGap;
            var zone = TownGenerator.RingAt(_tuning, Dist(px, pz, _cx, _cz) / _r);
            int maxTier = Promoted(e, zone);
            float half = _half[maxTier];
            float nx = side == 0 ? -dz : dz, nz = side == 0 ? dx : -dx;
            float x = px + nx * (roadHalf + half), z = pz + nz * (roadHalf + half);
            if (_terrain is { } terrain && (MathF.Abs(x) > terrain.HalfSize - 8f || MathF.Abs(z) > terrain.HalfSize - 8f
                || terrain.HeightAt(x, z) - terrain.WaterSurfaceAt(x, z) < _g.WaterMargin)) return;
            if (NearOtherStreet(edge, x, z, half) || Crowded(x, z, half)) return;
            float ratio = Dist(x, z, _cx, _cz) / _r;
            var site = new TownGenerator.LotSite(-1, -1, side, k, x, z, MathF.Atan2(-nz, -nx), ratio, 0f, maxTier);
            // Drawn in a fixed order for every lot, built on or not, so one lot's fate never shifts another's.
            float fillRoll = _rng.NextFloat(), typeRoll = _rng.NextFloat(), tierRoll = _rng.NextFloat();
            float noise = (_rng.NextFloat() - 0.5f) * TownGenerator.RingNoise;
            var ring = TownGenerator.RingAt(_tuning, ratio + noise);
            var lot = new Lot { Site = site, Half = half, Edge = edge };
            if (fillRoll < ring.Fill)
            {
                int tier = Math.Min(TownGenerator.Pick(ring.Tiers, tierRoll), maxTier);
                var type = (BuildingType)TownGenerator.Pick(ring.Mix, typeRoll);
                lot.Plan = new BuildingPlan(type, tier, x, z, site.Facing);
                if (type == BuildingType.Home) _people += _tuning.Of(BuildingType.Home, tier).People;
            }
            var cell = ((int)MathF.Floor(x / LotCell), (int)MathF.Floor(z / LotCell));
            if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = new List<int>();
            list.Add(_lots.Count);
            _lots.Add(lot);
        }

        /// <summary>
        /// Fills the gaps along every street built so far: a street's lots were laid as if a street crossed at each end, and what is left once the
        /// streets are known (a pass-through node, a free end, a street that never came) takes more lots, every few metres along it wherever one fits.
        /// </summary>
        void Infill()
        {
            const float step = 4f;
            int count = _edges.Count;
            for (int edge = 0; edge < count; edge++)
            {
                var e = _edges[edge];
                if (e.Dead || e.Bridge || (e.Seed && !_active.Contains(edge))) continue;
                var (x0, z0) = _nodes[e.A];
                var (x1, z1) = _nodes[e.B];
                float len = Dist(x0, z0, x1, z1);
                if (len < 1f) continue;
                float dx = (x1 - x0) / len, dz = (z1 - z0) / len;
                if (e.Seed && Dist((x0 + x1) * 0.5f, (z0 + z1) * 0.5f, _cx, _cz) > _r * _t.OuterReach) continue;
                for (float along = 0f; along <= len; along += step)
                    for (int side = 0; side < 2; side++) PlaceLot(edge, x0 + dx * along, z0 + dz * along, dx, dz, side, -1);
            }
        }

        /// <summary>The biggest size on a lot of a street in a zone: the zone's, one more on the main street (the avenue has the tall buildings) unless the zone is all small houses.</summary>
        static int Promoted(Edge e, RingTuning zone) => e.Main && zone.MaxTier > 0 ? Math.Min(zone.MaxTier + 1, TownsTuning.Tiers - 1) : zone.MaxTier;

        /// <summary>How far in from the end of a street its first lot stands: clear of a street that crosses there, none at a free end.</summary>
        float EndMargin(int node, float cross) => _adj[node].Count >= 2 || _slotNodes.Contains(node) ? cross : 0f;

        /// <summary>Whether a street other than <paramref name="except"/> runs closer than a building of this half-size needs to stand from it.</summary>
        bool NearOtherStreet(int except, float x, float z, float half)
        {
            for (int i = 0; i < _edges.Count; i++)
            {
                if (i == except || _edges[i].Dead) continue;
                float need = RoadNetwork.HalfWidth(_edges[i].Main ? RoadType.Avenue2x2 : RoadType.TwoLane) + _t.Pavement + _g.SetbackGap + half;
                if (EdgeDistance(i, x, z) < need * LotTolerance) return true;
            }
            return false;
        }

        /// <summary>Whether a lot already stands so near that the two buildings (each as wide as the biggest its lot takes) would touch.</summary>
        bool Crowded(float x, float z, float half)
        {
            int cx0 = (int)MathF.Floor(x / LotCell), cz0 = (int)MathF.Floor(z / LotCell);
            for (int a = cx0 - 1; a <= cx0 + 1; a++)
                for (int b = cz0 - 1; b <= cz0 + 1; b++)
                    if (_cells.TryGetValue((a, b), out var near))
                        foreach (int l in near)
                        {
                            var o = _lots[l];
                            float need = (half + o.Half) * LotTolerance;
                            if (!o.Dead && (o.Site.X - x) * (o.Site.X - x) + (o.Site.Z - z) * (o.Site.Z - z) < need * need) return true;
                        }
            return false;
        }
    }
}
