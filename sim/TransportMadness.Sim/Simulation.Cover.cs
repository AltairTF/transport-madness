namespace TransportMadness.Sim;

/// <summary>People of a trip group taking cover in a building until the all-clear (C14), as the simulation reports them.</summary>
/// <param name="Group">The trip group (<see cref="TripGroup.Id"/>).</param>
/// <param name="People">How many of its people are in this building.</param>
/// <param name="Building">The building they ran to.</param>
/// <param name="Since">The tick the siren sent them there.</param>
/// <param name="Queued">They were waiting at a station (and rejoin its queue at the all-clear) rather than on their way somewhere.</param>
public readonly record struct ShelterInfo(int Group, int People, int Building, long Since, bool Queued);

/// <summary>
/// Civilians in a raid (C14). When a flight is launched the siren sounds: people on foot and people waiting in a station queue run to the nearest
/// standing building they can reach before the first bomb could land, stay there until the flight is over and then go on, so that a walk is
/// late by the time they spent in cover. People in cars and buses keep driving and people already in a building stay in it. Casualties lower the
/// people of the trip groups: a destroyed building takes a share of those inside it (those who live there and are at home, those at work or at
/// leisure there, those who took cover in it), a bomb takes a share of the people on foot and in the queue of a station inside its blast, a wrecked
/// car or bus takes the people aboard.
/// </summary>
public sealed partial class Simulation
{
    /// <summary>What the siren did to one lot of people on foot.</summary>
    enum Cover : byte { None, Sheltered, Exposed }

    /// <summary>People who stopped what they were doing and took cover in a building: the walk or the wait to resume at the all-clear.</summary>
    sealed class Shelter
    {
        internal Shelter(int group, int people, int building, long since)
        {
            Group = group;
            People = people;
            Building = building;
            Since = since;
        }

        internal readonly int Group;
        internal int People;
        internal readonly int Building;
        internal readonly long Since;
        /// <summary>Walking: the walk they were on and the ticks it still had to go. Waiting at a station: the station and the one they were bound for.</summary>
        internal TripEvent Walk;
        internal long Remaining;
        internal bool Queued;
        internal int From, Dest;
    }

    readonly List<Shelter> _shelters = new();
    readonly SortedSet<int> _hurt = new(); // groups that lost people in the bomb being resolved

    /// <summary>People in cover now, in all the towns.</summary>
    public int PeopleSheltered => _shelters.Sum(s => s.People);

    /// <summary>The lots of people in cover now, in the order they took it.</summary>
    public IReadOnlyList<ShelterInfo> Shelters => _shelters.Select(s => new ShelterInfo(s.Group, s.People, s.Building, s.Since, s.Queued)).ToList();

    /// <summary>The people of a group that are in cover now (it can be several lots, in different buildings).</summary>
    public IReadOnlyList<ShelterInfo> ShelteredOf(int group) => Shelters.Where(s => s.Group == group).ToList();

    /// <summary>Whether the siren is on: a flight is in the air and has not yet been cleared.</summary>
    public bool SirenOn => _flights.Count > 0;

    /// <summary>The people living in each town now: those of its trip groups, fewer than its homes have room for after raids and call-ups.</summary>
    public int[] ResidentsByTown()
    {
        var residents = new int[_towns.Count];
        foreach (var g in _groups)
            if (!g.Retired) residents[_buildings[g.Home].Town] += g.People;
        return residents;
    }

    // ---- The siren ------------------------------------------------------------------------------------------------

    /// <summary>The seconds people have to reach a building: until the first bomb of the earliest flight could land, less the time to start running. Zero or less once bombs are falling.</summary>
    double CoverSeconds()
    {
        long first = long.MaxValue;
        foreach (var flight in _flights) first = Math.Min(first, flight.FirstImpactTick);
        return (first - Tick) * (double)StepSeconds - _civilians.ReactionSeconds;
    }

    /// <summary>The nearest standing building a person at a point can walk to in <paramref name="seconds"/> (the nearest by id when two are as near), -1 when there is none.</summary>
    int NearestCover(float x, float z, double seconds)
    {
        if (seconds <= 0.0) return -1;
        float reach = (float)(seconds * _trips.WalkSpeed / _trips.WalkDetour);
        int best = -1;
        float bestD = float.MaxValue;
        foreach (int id in BuildingsNear(x, z, reach))
        {
            var b = _buildings[id];
            if (b.State != BuildingState.Active) continue;
            float d = Dist2(b.X, b.Z, x, z);
            if (d >= bestD) continue; // the ids come in ascending order, so a tie keeps the lower
            best = id;
            bestD = d;
        }
        return best;
    }

    /// <summary>Where people on a walk are at this moment: on the straight line from where they set out to where they are bound.</summary>
    static (float X, float Z) WalkerPoint(TripEvent e, long at, long now)
    {
        float f = at <= e.Start ? 1f : Math.Clamp((now - e.Start) / (float)(at - e.Start), 0f, 1f);
        return (e.FromX + (e.ToX - e.FromX) * f, e.FromZ + (e.ToZ - e.FromZ) * f);
    }

    /// <summary>
    /// People on a walk that ends at tick <paramref name="at"/>, with the siren on: if they would be indoors at the end of it before the bombs can come they go on;
    /// otherwise they take cover in the nearest building they can reach in time, and the walk is held until the all-clear; with none in reach they walk on in the open.
    /// </summary>
    Cover TakeCover(TripEvent e, long at)
    {
        double seconds = CoverSeconds();
        var g = _groups[e.Group];
        if (e.Kind == TripEventKind.Arrive && IsStanding(g.Destination) && (at - Tick) * (double)StepSeconds <= seconds) return Cover.None; // there before the bombs
        var (x, z) = WalkerPoint(e, at, Tick);
        int building = NearestCover(x, z, seconds);
        if (building < 0) return Cover.Exposed;
        _shelters.Add(new Shelter(e.Group, e.People, building, Tick) { Walk = e, Remaining = Math.Max(0L, at - Tick) });
        return Cover.Sheltered;
    }

    /// <summary>The siren of a flight that has just been launched: everybody on foot or in a station queue takes cover if a building is within reach, and the player is told how many.</summary>
    void SoundSirens(float x, float z)
    {
        int took = 0, exposed = 0;
        double seconds = CoverSeconds();
        foreach (var (e, tick, order) in DrainTripEvents())
        {
            switch (TakeCover(e, tick))
            {
                case Cover.Sheltered: took += e.People; continue;
                case Cover.Exposed: exposed += e.People; break;
            }
            _tripEvents.Enqueue(e, (tick, order));
        }
        foreach (var station in _stations)
        {
            if (station.Queue.Count == 0) continue;
            var (sx, sz) = PointOf(station);
            int building = NearestCover(sx, sz, seconds);
            foreach (var lot in station.Queue)
            {
                if (building < 0) { exposed += lot.Count; continue; }
                took += lot.Count;
                _shelters.Add(new Shelter(lot.Group, lot.Count, building, Tick) { Queued = true, From = lot.From, Dest = lot.Dest, Walk = default });
            }
            if (building < 0) continue;
            station.Queue.Clear();
            RefreshWaiting(station);
        }
        if (took + exposed == 0) return;
        AddEvent(SimEventKind.Raid, EventSeverity.Info, x, z, -1,
            $"The sirens sound: {took} people on foot or waiting at stations are taking cover" + (exposed > 0 ? $", {exposed} cannot reach a building in time" : ""));
    }

    /// <summary>The last flight is over: everybody in cover comes out and goes on with the walk or the wait, late by the time spent inside.</summary>
    void AllClear(float x, float z)
    {
        int people = 0;
        foreach (var s in _shelters)
        {
            if (s.People <= 0) continue;
            people += s.People;
            var b = _buildings[s.Building];
            if (s.Queued)
            {
                var (sx, sz) = PointOf(_stations[s.From]);
                Schedule(Tick, new TripEvent(TripEventKind.JoinQueue, s.Group, s.People, s.From, s.Dest, Tick, sx, sz, sx, sz));
            }
            else
                Schedule(Tick + s.Remaining, s.Walk with { People = s.People, Start = Tick, FromX = b.X, FromZ = b.Z });
        }
        _shelters.Clear();
        if (people > 0)
            AddEvent(SimEventKind.Raid, EventSeverity.Info, x, z, -1, $"All clear: {people} people come out of cover");
    }

    /// <summary>Takes every pending walk out of the queue of events, in the order they would happen, to be looked at and put back (those that stay keep their place in the order).</summary>
    List<(TripEvent E, long Tick, long Order)> DrainTripEvents()
    {
        var list = new List<(TripEvent E, long Tick, long Order)>(_tripEvents.Count);
        foreach (var (e, p) in _tripEvents.UnorderedItems) list.Add((e, p.Tick, p.Order));
        list.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));
        _tripEvents.Clear();
        return list;
    }

    // ---- Casualties -----------------------------------------------------------------------------------------------

    /// <summary>How many of <paramref name="present"/> people a share takes: the share of them, rounded by a draw that is a function of the group, the bomb and the lot (no stream is used).</summary>
    int Killed(int present, float share, TripGroup g, int salt)
    {
        int impact = ImpactCount - 1;
        return Math.Min(present, (int)MathF.Floor(present * share + Unit(g.Seed, impact, 211 + salt)));
    }

    /// <summary>People of a group die: the group, its workplace's jobs and the casualties of the flight and of the town it lives in all lose them. The caller adjusts what was on the road.</summary>
    void Kill(Flight flight, TripGroup g, int dead)
    {
        g.People -= dead;
        if (g.Work >= 0) _jobsTaken[g.Work] = Math.Max(0, _jobsTaken.GetValueOrDefault(g.Work) - dead);
        AddCasualties(flight, _buildings[g.Home].Town, dead);
        _hurt.Add(g.Id);
    }

    /// <summary>People who were part of the trip under way die: they are no longer waited for.</summary>
    void KillOnTrip(Flight flight, TripGroup g, int dead)
    {
        Kill(flight, g, dead);
        g.Pending -= dead;
    }

    /// <summary>The bomb is resolved: the groups that lost people end (nobody left) or complete the trip their survivors were on.</summary>
    void FinishHurt()
    {
        foreach (int id in _hurt)
        {
            var g = _groups[id];
            if (g.People <= 0)
            {
                g.Travelling = false;
                g.Pending = 0;
                g.ToDispatch = 0;
                if (!g.Retired) Retire(g);
            }
            else if (g.Travelling && g.Pending <= 0) Complete(g);
        }
        _hurt.Clear();
    }

    /// <summary>
    /// A building is destroyed: <c>casualtyShare</c> of the people inside die. Inside are the groups at home, at work or at leisure there (what their next trip says), the people
    /// of a group about to set out by car from there, and those who took cover in it. The people that die are the casualties of the town each of them lives in.
    /// </summary>
    void KillInside(Flight flight, Building building)
    {
        float share = _war.Bombing.CasualtyShare;
        foreach (var g in _groups)
        {
            if (g.Retired || g.People <= 0) continue;
            int present = 0;
            bool waiting = false;
            if (!g.Travelling)
            {
                int at = g.NextLeg switch { TripLeg.WorkBack => g.Work, TripLeg.LeisureBack => g.Leisure, _ => g.Home };
                if (at == building.Id) present = g.People;
            }
            else if (g.Mode == TripMode.Car && g.ToDispatch > 0 && g.Origin == building.Id)
            {
                present = g.ToDispatch;
                waiting = true;
            }
            if (present <= 0) continue;
            int dead = Killed(present, share, g, 1);
            if (dead <= 0) continue;
            Kill(flight, g, dead);
            if (waiting)
            {
                g.ToDispatch -= dead;
                g.Pending -= dead;
            }
        }
        for (int k = 0; k < _shelters.Count; k++)
        {
            var s = _shelters[k];
            if (s.Building != building.Id) continue;
            var g = _groups[s.Group];
            int dead = Killed(s.People, share, g, 2 + k);
            if (dead <= 0) continue;
            KillOnTrip(flight, g, dead);
            s.People -= dead;
            if (s.People <= 0) _shelters.RemoveAt(k--);
        }
    }

    /// <summary>A bomb lands: people on foot and waiting at stations inside its blast lose the share, as those in a building it destroys do. People in cover are not outside.</summary>
    void BlastOutdoors(Flight flight, float x, float z, float radius)
    {
        float share = _war.Bombing.CasualtyShare, r2 = radius * radius;
        bool near = false;
        foreach (var (e, p) in _tripEvents.UnorderedItems)
        {
            var (wx, wz) = WalkerPoint(e, p.Tick, Tick);
            if (Dist2(wx, wz, x, z) > r2) continue;
            near = true;
            break;
        }
        if (near)
            foreach (var (e, tick, order) in DrainTripEvents())
            {
                var kept = e;
                var (wx, wz) = WalkerPoint(e, tick, Tick);
                if (Dist2(wx, wz, x, z) <= r2)
                {
                    var g = _groups[e.Group];
                    int dead = Killed(e.People, share, g, 40 + (int)(order & 0xFFFF));
                    if (dead > 0)
                    {
                        KillOnTrip(flight, g, dead);
                        kept = e with { People = e.People - dead };
                    }
                }
                if (kept.People > 0) _tripEvents.Enqueue(kept, (tick, order));
            }
        foreach (var station in _stations)
        {
            if (!station.Active || station.Queue.Count == 0) continue;
            var (sx, sz) = PointOf(station);
            if (Dist2(sx, sz, x, z) > r2) continue;
            for (int k = 0; k < station.Queue.Count; k++)
            {
                var lot = station.Queue[k];
                var g = _groups[lot.Group];
                int dead = Killed(lot.Count, share, g, 20 + k);
                if (dead <= 0) continue;
                KillOnTrip(flight, g, dead);
                lot.Count -= dead;
                if (lot.Count <= 0) station.Queue.RemoveAt(k--);
            }
            RefreshWaiting(station);
        }
    }

    ulong ShelterHash()
    {
        ulong h = (ulong)_shelters.Count;
        foreach (var s in _shelters)
        {
            h = h * 31 + (ulong)s.Group;
            h = h * 31 + (ulong)s.People;
            h = h * 31 + (ulong)s.Building;
            h = h * 31 + (ulong)s.Since;
            h = h * 31 + (ulong)s.Remaining;
            h = h * 31 + (s.Queued ? 1UL : 0UL);
            h = h * 31 + (ulong)(s.From + 1);
            h = h * 31 + (ulong)(s.Dest + 1);
        }
        return h;
    }
}
