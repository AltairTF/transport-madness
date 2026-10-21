namespace TransportMadness.Sim;

/// <summary>
/// Trip groups: the people of each home travel to work and to a leisure place and back on a daily schedule, on foot, by
/// ambient car or by the company's lines. A group is created with its home, workplace and leisure place, a car or none, and
/// the times of its trips inside the windows of the tuning file, all drawn once from the seed.
/// </summary>
public sealed partial class Simulation
{
    const ulong TripSeedMix = 0x7121F5A93C4B8E6DUL; // the trips draw from their own stream, so maps without towns run as before
    const int GiveUpCheckTicks = 100;

    readonly TripsTuning _trips;
    readonly SimRandom _tripRng;
    readonly CiviliansTuning _civilians;
    readonly List<TripGroup> _groups = new();
    readonly Dictionary<int, int> _jobsTaken = new(); // workplace (see IndustryPlace) -> people assigned to it
    readonly PriorityQueue<TripEvent, (long Tick, long Order)> _tripEvents = new();
    long _eventOrder;
    readonly List<int> _carQueue = new(); // groups with people still to put on the road, oldest first
    int _tripVehicles;
    int _transitVersion; // changes when lines, their vehicles or what stations cover change: the mode choices are made again
    readonly Dictionary<long, ModeChoice> _choices = new();
    // How many times longer than on empty roads a car trip took, as people have come to expect it for each hour of the day.
    readonly float[] _congestion = Enumerable.Repeat(1f, 24).ToArray();
    List<int[]> _buildingStations = new(); // by building id: the active stations that cover it

    // Per vehicle slot: the trip group a car belongs to (-1 for other traffic), the node it is bound for, how many people it
    // stands for and when it is given up on.
    int[] _tripGroup = FilledWith(64, -1);
    int[] _tripDest = FilledWith(64, -1);
    int[] _tripPeople = new int[64];
    long[] _tripDeadline = new long[64];
    long[] _tripMoved = new long[64]; // the last tick the car was moving
    const float StuckSpeed = 0.3f;    // slower than this counts as standing still

    enum TripEventKind : byte { Arrive, JoinQueue }

    /// <summary>
    /// Something that happens to a group at a given tick: people reach the other end, or reach their station and join its queue. Both are people on foot,
    /// who walk in a straight line from where they set out (at <paramref name="Start"/>) to where they are bound, so that a bomb or a siren can find them.
    /// </summary>
    readonly record struct TripEvent(TripEventKind Kind, int Group, int People, int StationA, int StationB, long Start = 0, float FromX = 0f, float FromZ = 0f, float ToX = 0f, float ToZ = 0f);

    /// <summary>How a pair of buildings is travelled between, cached until the roads or the lines change.</summary>
    readonly record struct ModeChoice(TripMode Mode, float Seconds, int OriginNode, int DestNode, int StationA, int StationB, float WalkToStation, long Version, float FreeRide = 0f);

    /// <summary>
    /// A place a group goes to is an id: a building id below this, or from here up an industry (this plus the industry's id). Workplaces are
    /// either; leisure places and homes are always buildings. Building ids never get near it.
    /// </summary>
    public const int IndustryPlace = 1 << 24;

    public IReadOnlyList<TripGroup> TripGroups => _groups;
    /// <summary>People of trip groups who have a workplace (a building, or an industry as <see cref="IndustryPlace"/> plus its id) as their job.</summary>
    internal int JobsTakenAt(int place) => _jobsTaken.GetValueOrDefault(place);
    public TripsTuning TripTuning => _trips;
    /// <summary>Ambient cars on the road that carry a trip group.</summary>
    public int TripVehicleCount => _tripVehicles;
    /// <summary>The trip group a car carries, -1 when it is other traffic or a company vehicle.</summary>
    internal int TripGroupOf(int vehicle) => _tripGroup[vehicle];

    void GrowTripArrays(int n)
    {
        int old = _tripGroup.Length;
        Array.Resize(ref _tripGroup, n);
        Array.Resize(ref _tripDest, n);
        Array.Resize(ref _tripPeople, n);
        Array.Resize(ref _tripDeadline, n);
        Array.Resize(ref _tripMoved, n);
        for (int i = old; i < n; i++) { _tripGroup[i] = -1; _tripDest[i] = -1; }
    }

    void ResetTripSlot(int id)
    {
        _tripGroup[id] = -1;
        _tripDest[id] = -1;
        _tripPeople[id] = 0;
        _tripDeadline[id] = 0;
        _tripMoved[id] = 0;
    }

    // ---- Making the groups ---------------------------------------------------------------------------------------

    /// <summary>Makes the trip groups of the homes that exist; called once by <see cref="AddTowns"/>.</summary>
    void CreateTripGroups()
    {
        foreach (var b in _buildings)
            if (b.Type == BuildingType.Home) CreateTripGroups(b, _townTuning.Of(b.Type, b.Tier).People, 0);
    }

    /// <summary>
    /// <paramref name="people"/> more people living in a home split into groups of at most <c>groupSize</c>, each with a
    /// workplace and a leisure place, whose first trip is on <paramref name="firstDay"/> or later.
    /// </summary>
    void CreateTripGroups(Building home, int people, long firstDay)
    {
        if (people <= 0) return;
        int count = (people + _trips.GroupSize - 1) / _trips.GroupSize;
        for (int k = 0; k < count; k++)
        {
            int size = people / count + (k < people % count ? 1 : 0);
            var g = new TripGroup(_groups.Count, home.Id, size, _tripRng.NextUInt64(), _tripRng.NextFloat() < _trips.CarShare);
            _groups.Add(g);
            g.Work = PickPlace(home, work: true, size);
            g.Leisure = PickPlace(home, work: false, size);
            g.PlanDay = firstDay;
            Advance(g, -1, 0);
        }
    }

    float Nearness(float distance)
    {
        float r = distance / _trips.Nearness;
        return 1f / (1f + r * r);
    }

    /// <summary>
    /// A workplace (a building or an industry with jobs, weighted by the jobs not yet taken and by nearness) or a leisure place (a commerce
    /// building, weighted by size and nearness) within reach of a home; -1 when there is none. An industry is returned as <see cref="IndustryPlace"/> plus its id.
    /// </summary>
    int PickPlace(Building home, bool work, int people)
    {
        var near = BuildingsNear(home.X, home.Z, _trips.WorkRadius);
        var industries = work ? IndustriesNear(home.X, home.Z, _trips.WorkRadius) : new List<int>();
        int count = near.Count + industries.Count;
        var weight = new double[count];
        double total = 0, crowded = 0;
        var crowdedWeight = new double[count];
        for (int k = 0; k < near.Count; k++)
        {
            var b = _buildings[near[k]];
            if (b.State != BuildingState.Active) continue;
            var tier = _townTuning.Of(b.Type, b.Tier);
            float nearness = Nearness(MathF.Sqrt((b.X - home.X) * (b.X - home.X) + (b.Z - home.Z) * (b.Z - home.Z)));
            if (work)
            {
                if (tier.Jobs <= 0) continue;
                weight[k] = Math.Max(0, tier.Jobs - _jobsTaken.GetValueOrDefault(b.Id)) * nearness;
                crowdedWeight[k] = tier.Jobs * nearness;
            }
            else
            {
                if (b.Type != BuildingType.Commerce) continue;
                weight[k] = tier.Jobs * nearness;
            }
            total += weight[k];
            crowded += crowdedWeight[k];
        }
        for (int j = 0; j < industries.Count; j++)
        {
            var industry = _industries[industries[j]];
            int jobs = _industryTuning.Types[industry.Type].Jobs;
            if (jobs <= 0) continue;
            float nearness = Nearness(MathF.Sqrt((industry.X - home.X) * (industry.X - home.X) + (industry.Z - home.Z) * (industry.Z - home.Z)));
            int k = near.Count + j;
            weight[k] = Math.Max(0, jobs - _jobsTaken.GetValueOrDefault(IndustryPlace + industry.Id)) * nearness;
            crowdedWeight[k] = jobs * nearness;
            total += weight[k];
            crowded += crowdedWeight[k];
        }
        // Every job taken: the people crowd into the workplaces anyway rather than stay without one.
        if (work && total <= 0) { weight = crowdedWeight; total = crowded; }
        if (total <= 0) return -1;

        double r = _tripRng.NextFloat() * total;
        int pick = -1;
        for (int k = 0; k < count; k++)
        {
            if (weight[k] <= 0) continue;
            pick = k < near.Count ? near[k] : IndustryPlace + industries[k - near.Count];
            r -= weight[k];
            if (r < 0) break;
        }
        if (work && pick >= 0) _jobsTaken[pick] = _jobsTaken.GetValueOrDefault(pick) + people;
        return pick;
    }

    // ---- Places: buildings and industries ---------------------------------------------------------------------

    /// <summary>The middle of a building, or of an industry (see <see cref="IndustryPlace"/>).</summary>
    (float X, float Z) PlacePoint(int place)
    {
        if (place >= IndustryPlace) return (_industries[place - IndustryPlace].X, _industries[place - IndustryPlace].Z);
        return (_buildings[place].X, _buildings[place].Z);
    }

    /// <summary>The road node a car goes to for a place: the access node of a building, the node at the access point of an industry. -1 when there is none.</summary>
    int PlaceAccessNode(int place) =>
        place >= IndustryPlace ? AccessNodeOf(_industries[place - IndustryPlace]) : AccessNodeOf(_buildings[place]);

    /// <summary>The active stations that cover a place (where its people can catch a line, or get off one).</summary>
    int[] PlaceStations(int place)
    {
        if (place >= IndustryPlace) return place - IndustryPlace < _industryStations.Count ? _industryStations[place - IndustryPlace] : Array.Empty<int>();
        return place < _buildingStations.Count ? _buildingStations[place] : Array.Empty<int>();
    }

    float PlaceDistance(int a, int b)
    {
        var (ax, az) = PlacePoint(a);
        var (bx, bz) = PlacePoint(b);
        return MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
    }

    /// <summary>Whether a place can be gone to: a building that stands, or an industry (an industry is never pulled down).</summary>
    bool IsStanding(int place) => place >= IndustryPlace || (place >= 0 && _buildings[place].State == BuildingState.Active);

    // ---- The schedule -------------------------------------------------------------------------------------------

    /// <summary>Uniform in [0, 1), a pure function of a group's seed, a number (the day, say) and a salt.</summary>
    static float Unit(ulong seed, long a, int salt)
    {
        ulong z = seed + (ulong)a * 0x9E3779B97F4A7C15UL + (ulong)(salt + 1) * 0xD1B54A32D192ED03UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 40) * (1f / (1UL << 24));
    }

    bool LeisureToday(TripGroup g, long day) => Unit(g.Seed, day, 31) < _trips.LeisureChance;

    /// <summary>Ticks after midnight at which a group starts a trip: its own place inside the window plus its jitter.</summary>
    long LegOffsetTicks(TripGroup g, int leg)
    {
        var w = _trips.Windows.Of(leg);
        float hour = w.From + (w.To - w.From) * Unit(g.Seed, 0, leg) + _trips.JitterMinutes / 60f * Unit(g.Seed, 0, 8 + leg);
        return (long)MathF.Round(hour / 24f * _ticksPerDay);
    }

    /// <summary>Ticks after midnight at which the window of a trip ends, jitter included.</summary>
    long LegWindowEndTicks(TripGroup g, int leg)
    {
        float hour = _trips.Windows.Of(leg).To + _trips.JitterMinutes / 60f;
        return (long)MathF.Round(hour / 24f * _ticksPerDay);
    }

    /// <summary>
    /// Plans the trip after <paramref name="doneLeg"/> (-1 for the first of the day): skips the work trips of a group with no
    /// workplace and the leisure trips of a day it stays in, and never earlier than <paramref name="ready"/>.
    /// </summary>
    void Advance(TripGroup g, int doneLeg, long ready)
    {
        int leg = doneLeg + 1;
        long day = g.PlanDay;
        if (doneLeg == (int)TripLeg.LeisureOut) ready += Ticks(_trips.LeisureStayMinutes / 1440f * _economy.DayLength);
        for (int guard = 0; guard < 64; guard++)
        {
            if (leg > 3) { leg = 0; day++; }
            if (leg <= 1 && g.Work < 0) { leg = 2; continue; }
            if (leg >= 2 && (g.Leisure < 0 || !LeisureToday(g, day))) { leg = 4; continue; }
            // An evening that has slipped past its window (a slow day on the roads) is not made up for: the group stays in.
            if (leg == (int)TripLeg.LeisureOut && ready > day * _ticksPerDay + LegWindowEndTicks(g, leg)) { leg = 4; continue; }
            g.NextLeg = (TripLeg)leg;
            g.PlanDay = day;
            g.NextTick = Math.Max(ready, day * _ticksPerDay + LegOffsetTicks(g, leg));
            return;
        }
        g.NextTick = long.MaxValue; // nowhere to go: the daily check looks again
    }

    // ---- Leaving, arriving ---------------------------------------------------------------------------------------

    static bool IsOutbound(TripLeg leg) => leg == TripLeg.WorkOut || leg == TripLeg.LeisureOut;

    void UpdateTrips()
    {
        if (_groups.Count == 0) return;
        foreach (var g in _groups)
            if (!g.Travelling && Tick >= g.NextTick) Depart(g);
        while (_tripEvents.TryPeek(out var e, out var at) && at.Tick <= Tick)
        {
            _tripEvents.Dequeue();
            Handle(e);
        }
        DispatchCars();
        if (_tripVehicles > 0) CheckTripCars();
        if (Tick % GiveUpCheckTicks == 0) GiveUpWaiting();
    }

    void Schedule(long tick, TripEvent e) => _tripEvents.Enqueue(e, (tick, _eventOrder++));

    /// <summary>
    /// People of a group set out on foot from one point to another and will be there <paramref name="ticks"/> from now (they arrive, or join the queue of a
    /// station). With the siren on they may take cover on the way (see <see cref="TakeCover"/>), and then the walk is resumed at the all-clear.
    /// </summary>
    void ScheduleWalk(TripGroup g, TripEventKind kind, int people, int stationA, int stationB, (float X, float Z) from, (float X, float Z) to, long ticks)
    {
        var e = new TripEvent(kind, g.Id, people, stationA, stationB, Tick, from.X, from.Z, to.X, to.Z);
        long at = Tick + ticks;
        if (_flights.Count > 0 && TakeCover(e, at) == Cover.Sheltered) return;
        Schedule(at, e);
    }

    void Handle(TripEvent e)
    {
        var g = _groups[e.Group];
        if (e.Kind == TripEventKind.Arrive) { Arrive(g, e.People); return; }
        var station = _stations[e.StationA];
        if (!station.Active || !_stations[e.StationB].Active)
        {
            GiveUp(g, e.People, e.StationA);
            return;
        }
        station.Queue.Add(new PaxLot(g.Id, e.People, e.StationA, e.StationB, Tick));
        RefreshWaiting(station);
    }

    long WalkTicks(float distance) => Math.Max(1, Ticks(distance * _trips.WalkDetour / _trips.WalkSpeed));

    /// <summary>The group starts its next trip, or finds it cannot make it (a place is gone, nothing links the two).</summary>
    void Depart(TripGroup g)
    {
        var leg = g.NextLeg;
        bool outbound = IsOutbound(leg);
        int from = leg switch { TripLeg.WorkBack => g.Work, TripLeg.LeisureBack => g.Leisure, _ => g.Home };
        int to = leg switch { TripLeg.WorkOut => g.Work, TripLeg.LeisureOut => g.Leisure, _ => g.Home };
        var town = _towns[_buildings[g.Home].Town];
        if (from < 0 || to < 0) { Skip(g); return; }

        if (outbound && !(IsStanding(from) && IsStanding(to))) { Skip(g); return; } // a place with no road: nobody goes

        var choice = ChooseMode(from, to, g.HasCar);
        if (choice.Mode == TripMode.None && !outbound)
            choice = new ModeChoice(TripMode.Walk, 0f, -1, -1, -1, -1, 0f, 0); // people always get home, however far and slowly
        if (_flights.Count > 0 && choice.Mode is TripMode.Walk or TripMode.Transit)
        {
            g.NextTick = Tick + Math.Max(1, Ticks(_civilians.RecheckSeconds)); // the siren is on: nobody sets out on foot
            return;
        }
        town.TripsToday[(int)choice.Mode] += g.People;
        if (choice.Mode == TripMode.None) { Skip(g); return; }

        g.Travelling = true;
        g.NextTick = long.MaxValue;
        g.Mode = choice.Mode;
        g.DepartTick = Tick;
        g.Origin = from;
        g.Destination = to;
        g.Pending = g.People;
        g.Arrived = 0;
        switch (choice.Mode)
        {
            case TripMode.Walk:
                ScheduleWalk(g, TripEventKind.Arrive, g.People, -1, -1, PlacePoint(from), PlacePoint(to), WalkTicks(PlaceDistance(from, to)));
                break;
            case TripMode.Car:
                g.OriginNode = choice.OriginNode;
                g.DestNode = choice.DestNode;
                g.FreeRide = choice.FreeRide;
                g.ToDispatch = g.People;
                g.NextSpawnTick = Tick;
                _carQueue.Add(g.Id);
                break;
            default:
                ScheduleWalk(g, TripEventKind.JoinQueue, g.People, choice.StationA, choice.StationB, PlacePoint(from), PointOf(_stations[choice.StationA]),
                    Math.Max(1, Ticks(choice.WalkToStation)));
                break;
        }
    }

    /// <summary>The group stays where it is for this trip and its return; the next one is planned.</summary>
    void Skip(TripGroup g)
    {
        int leg = (int)g.NextLeg;
        Advance(g, IsOutbound(g.NextLeg) ? leg + 1 : leg, Tick);
    }

    void Arrive(TripGroup g, int people)
    {
        if (!g.Travelling) return;
        if (g.NextLeg == TripLeg.WorkOut && g.Work >= IndustryPlace) _industries[g.Work - IndustryPlace].WorkersToday += people;
        g.Pending -= people;
        g.Arrived += people;
        if (g.Pending <= 0) Complete(g);
    }

    /// <summary>People who gave up a trip they had not made (a ride that never came): they are where they started.</summary>
    void Drop(TripGroup g, int people)
    {
        if (!g.Travelling) return;
        g.Pending -= people;
        if (g.Pending <= 0) Complete(g);
    }

    void Complete(TripGroup g)
    {
        g.Travelling = false;
        int leg = (int)g.NextLeg;
        if (g.Arrived == 0 && IsOutbound(g.NextLeg))
        {
            Advance(g, leg + 1, Tick); // nobody got there: the group is still at home
            return;
        }
        g.LastTripSeconds = (Tick - g.DepartTick) * StepSeconds;
        g.LastMode = g.Mode;
        if (g.Mode == TripMode.Car && g.FreeRide > 0f) LearnCongestion(g);
        if (IsOutbound(g.NextLeg) && g.LastTripSeconds <= _growth.TimeBudgetSeconds) g.ReachedMonth = MonthIndex;
        Advance(g, leg, Tick);
    }

    /// <summary>People at a station (or on the road) who will not make the trip they set out on. On the way out they stay; on the way home they walk.</summary>
    void GiveUp(TripGroup g, int people, int station)
    {
        if (IsOutbound(g.NextLeg)) { Drop(g, people); return; }
        var (x, z) = PointOf(_stations[station]);
        var home = _buildings[g.Destination];
        float d = MathF.Sqrt((home.X - x) * (home.X - x) + (home.Z - z) * (home.Z - z));
        ScheduleWalk(g, TripEventKind.Arrive, people, -1, -1, (x, z), (home.X, home.Z), WalkTicks(d));
    }

    /// <summary>Daily upkeep of the groups: yesterday's trip counts, new places for groups whose workplace is gone, and a new look for groups with nothing to do.</summary>
    void CloseDayForTrips()
    {
        foreach (var station in _stations)
        {
            station.Yesterday = station.Today;
            station.Today = default;
        }
        if (_groups.Count == 0) return;
        foreach (var town in _towns)
        {
            Array.Copy(town.TripsToday, town.TripsYesterday, town.TripsToday.Length);
            Array.Clear(town.TripsToday);
        }
        foreach (var g in _groups)
        {
            if (g.Retired || g.Travelling) continue;
            if (_buildings[g.Home].State == BuildingState.Demolished) { Retire(g); continue; }
            if (g.NextLeg != TripLeg.WorkOut && g.NextLeg != TripLeg.LeisureOut) continue;
            var home = _buildings[g.Home];
            if (home.State != BuildingState.Active) continue;
            if (!IsStanding(g.Work))
            {
                if (g.Work >= 0) _jobsTaken[g.Work] = Math.Max(0, _jobsTaken.GetValueOrDefault(g.Work) - g.People);
                g.Work = PickPlace(home, work: true, g.People);
            }
            if (!IsStanding(g.Leisure)) g.Leisure = PickPlace(home, work: false, g.People);
            if (g.NextTick == long.MaxValue)
            {
                g.PlanDay = Day + 1;
                Advance(g, -1, Tick);
            }
        }
    }

    /// <summary>The home of a group is gone (demolished): the group ends, and the jobs it held go back to the workplace.</summary>
    void Retire(TripGroup g)
    {
        if (g.Work >= 0) _jobsTaken[g.Work] = Math.Max(0, _jobsTaken.GetValueOrDefault(g.Work) - g.People);
        g.Retired = true;
        g.People = 0;
        g.Work = g.Leisure = -1;
        g.NextTick = long.MaxValue;
    }

    // ---- Choosing how to travel -------------------------------------------------------------------------------

    int HourOf(long tick) => (int)(tick % _ticksPerDay * 24 / _ticksPerDay);

    /// <summary>A car trip has ended: the estimate for the hour it set out in moves toward how many times the empty-road time it took.</summary>
    internal void LearnCongestion(TripGroup g)
    {
        float ratio = Math.Clamp((Tick - g.DepartTick) * StepSeconds / g.FreeRide, 1f, _trips.MaxCongestion);
        int hour = HourOf(g.DepartTick);
        _congestion[hour] += (ratio - _congestion[hour]) * _trips.CongestionSmoothing;
    }

    /// <summary>How many times the empty-road time people expect a car trip to take, for trips setting out now.</summary>
    public float ExpectedCongestion => _congestion[HourOf(Tick)];

    /// <summary>The same estimate for an hour of the day (0 to 23).</summary>
    public float CongestionAt(int hour) => _congestion[hour];

    /// <summary>How people living at <paramref name="from"/> would go to <paramref name="to"/> now (places as for <see cref="IndustryPlace"/>): the mode and the seconds it takes.</summary>
    public (TripMode Mode, float Seconds) ModeFor(int from, int to, bool hasCar)
    {
        var c = ChooseMode(from, to, hasCar);
        return (c.Mode, c.Seconds);
    }

    ModeChoice ChooseMode(int from, int to, bool hasCar)
    {
        SyncNetwork();
        if (_coverageDirty || _buildingStations.Count != _buildings.Count) RefreshCoverage();
        int hour = HourOf(Tick);
        long key = ((long)hour << 58) | ((long)from << 33) | ((long)to << 1) | (hasCar ? 1L : 0L);
        // The estimate of the hour is part of the version, in quarters: a choice is made again when people's idea of the jam moves.
        long version = ((long)(_congestion[hour] * 4f) << 50) + ((long)_syncedNetworkVersion << 24) + _transitVersion;
        if (_choices.TryGetValue(key, out var cached) && cached.Version == version) return cached;
        var choice = ComputeMode(from, to, hasCar, _congestion[hour]) with { Version = version };
        _choices[key] = choice;
        return choice;
    }

    ModeChoice ComputeMode(int a, int b, bool hasCar, float congestion)
    {
        float d = PlaceDistance(a, b);
        if (d <= _trips.WalkRadius)
            return new ModeChoice(TripMode.Walk, WalkTicks(d) * StepSeconds, -1, -1, -1, -1, 0f, 0);

        // Two places that are reached from the same corner are next door whatever the straight line says.
        int nodeA = PlaceAccessNode(a);
        if (nodeA >= 0 && nodeA == PlaceAccessNode(b))
            return new ModeChoice(TripMode.Walk, WalkTicks(d) * StepSeconds, -1, -1, -1, -1, 0f, 0);

        var best = new ModeChoice(TripMode.None, 0f, -1, -1, -1, -1, 0f, 0);
        float bestCost = float.PositiveInfinity;
        if (hasCar && CarSeconds(a, b, out int on, out int dn, out float rideSeconds))
        {
            float seconds = rideSeconds * congestion + _trips.CarOverheadSeconds;
            bestCost = seconds + _trips.MoneyWeight * _trips.CarCostPerKm * d / 1000f;
            best = new ModeChoice(TripMode.Car, seconds, on, dn, -1, -1, 0f, 0, rideSeconds);
        }
        if (TransitSeconds(a, b, out int sa, out int sb, out float total, out float walk))
        {
            float fare = _passengerCargo >= 0 ? _cargo[_passengerCargo].PayPerUnitKm * d / 1000f : 0f;
            float cost = total + _trips.MoneyWeight * fare;
            if (cost < bestCost) best = new ModeChoice(TripMode.Transit, total, -1, -1, sa, sb, walk, 0);
        }
        return best;
    }

    int AccessNodeOf(Building b)
    {
        if (b.AccessVersion != _syncedNetworkVersion)
        {
            b.AccessNode = _net.FindNodeNear(b.X, b.Z, _trips.CarAccessRadius, withRoadsOnly: true);
            b.AccessVersion = _syncedNetworkVersion;
        }
        return b.AccessNode;
    }

    /// <summary>The lanes that lead out of a node on the right-hand side of each road (where a new car is put).</summary>
    IEnumerable<int> LanesLeaving(int node)
    {
        foreach (var roadId in _net.RoadsAt(node))
        {
            var road = _net.GetRoad(roadId);
            yield return (road.From == node ? road.ForwardLanes : road.BackwardLanes)[0].Value;
        }
    }

    /// <summary>The time a car takes from the access node of one place to that of the other, false when there is no way.</summary>
    bool CarSeconds(int a, int b, out int originNode, out int destNode, out float seconds)
    {
        seconds = 0f;
        originNode = PlaceAccessNode(a);
        destNode = PlaceAccessNode(b);
        if (originNode < 0 || destNode < 0 || originNode == destNode) return false;
        var field = FieldToNode(destNode);
        float best = float.PositiveInfinity;
        foreach (int lane in LanesLeaving(originNode)) best = MathF.Min(best, field[lane]);
        seconds = best;
        return !float.IsPositiveInfinity(best);
    }

    // ---- Cars --------------------------------------------------------------------------------------------------

    /// <summary>Puts the cars of groups that have set out on the road when there is room, at most a few tries per step.</summary>
    void DispatchCars()
    {
        if (_carQueue.Count == 0) return;
        long timeout = Ticks(_trips.CarTimeoutSeconds);
        int attempts = _trips.CarSpawnAttempts;
        int q = 0;
        while (q < _carQueue.Count)
        {
            var g = _groups[_carQueue[q]];
            if (g.ToDispatch <= 0) { _carQueue.RemoveAt(q); continue; }
            if (Tick - g.DepartTick > timeout)
            {
                // Never found room or a way: they count as having got there.
                int stuck = g.ToDispatch;
                g.ToDispatch = 0;
                _carQueue.RemoveAt(q);
                Arrive(g, stuck);
                continue;
            }
            if (Tick < g.NextSpawnTick || _tripVehicles >= _trips.MaxCars) { q++; continue; }
            if (attempts-- <= 0) return;
            if (!TrySpawnTripCar(g)) { _carQueue.RemoveAt(q); _carQueue.Add(g.Id); if (attempts <= 0) return; continue; }
            if (g.ToDispatch <= 0) _carQueue.RemoveAt(q); else q++;
        }
    }

    bool TrySpawnTripCar(TripGroup g)
    {
        int node = g.OriginNode;
        if (TrafficNear(node)) return false;
        var field = FieldToNode(g.DestNode);
        int bestLane = -1;
        float bestCost = float.PositiveInfinity;
        foreach (int lane in LanesLeaving(node))
        {
            float position = _laneReach[lane] + VehicleLength + SpawnGap;
            if (position > _laneLength[lane] || !IsClearFor(new LaneId(lane), position, VehicleLength, _trips.CarSpawnClearance)) continue;
            if (field[lane] < bestCost) { bestCost = field[lane]; bestLane = lane; }
        }
        if (bestLane < 0) return false;

        int id = SpawnVehicle(new LaneId(bestLane), _laneReach[bestLane] + VehicleLength + SpawnGap, 0f);
        int people = Math.Min(_trips.PeoplePerCar, g.ToDispatch);
        _tripGroup[id] = g.Id;
        _tripDest[id] = g.DestNode;
        _tripPeople[id] = people;
        _tripDeadline[id] = Tick + Ticks(_trips.CarTimeoutSeconds);
        _tripMoved[id] = Tick;
        _factor[id] = _traffic.DriverSpeedFactorMin + _traffic.DriverSpeedFactorRange * _tripRng.NextFloat();
        _patience[id] = _tripRng.NextFloat();
        _aggression[id] = _tripRng.NextFloat();
        _attention[id] = _tripRng.NextFloat();
        _nextLane[id] = RouteTripLane(id, bestLane);
        _tripVehicles++;
        g.ToDispatch -= people;
        g.NextSpawnTick = Tick + Ticks(_trips.CarDepartGapSeconds);
        return true;
    }

    /// <summary>Takes off the road the trip cars that have reached the node they were bound for (or have been out too long).</summary>
    void CheckTripCars()
    {
        bool removed = false;
        long stuck = Ticks(_trips.CarStuckSeconds);
        for (int i = 0; i < _count; i++)
        {
            if (_tripGroup[i] < 0 || _kind[i] != VehicleKind.Car || _lane[i] < 0) continue;
            int lane = _lane[i];
            bool there = _laneEndNode[lane] == _tripDest[i] && _laneLength[lane] - _pos[i] <= _trips.CarArriveRange;
            if (_speed[i] > StuckSpeed) _tripMoved[i] = Tick;
            bool jammed = Tick - _tripMoved[i] > stuck;
            if (!there && !jammed && Tick < _tripDeadline[i]) continue;
            RemoveVehicle(i);
            removed = true;
        }
        if (removed) RebuildOrder();
    }

    /// <summary>A vehicle is going: the people of a trip car arrive (it was either there or lost), those on a bus are let out.</summary>
    void ReleaseTripSlot(int vehicle)
    {
        if (_line[vehicle] >= 0) _transitVersion++;
        ReleaseRiders(vehicle);
        int group = _tripGroup[vehicle];
        if (group < 0) return;
        int people = _tripPeople[vehicle];
        _tripGroup[vehicle] = -1;
        _tripDest[vehicle] = -1;
        _tripVehicles--;
        Arrive(_groups[group], people);
    }

    internal ulong TripHash()
    {
        ulong h = (ulong)_groups.Count;
        foreach (var g in _groups)
        {
            if (_raids > 0 || _casualties > 0) h = h * 31 + (ulong)g.People; // lowered by casualties, hashed from the first siren or bomb so that nothing before it moves
            h = h * 31 + (ulong)g.NextTick;
            h = h * 31 + (ulong)g.NextLeg;
            h = h * 31 + (ulong)g.PlanDay;
            h = h * 31 + (g.Travelling ? 1UL : 0UL);
            h = h * 31 + (ulong)g.Mode;
            h = h * 31 + (ulong)g.Pending;
            h = h * 31 + (ulong)g.Arrived;
            h = h * 31 + (ulong)g.ToDispatch;
            h = h * 31 + (ulong)(g.Work + 1);
            h = h * 31 + (ulong)(g.Leisure + 1);
        }
        foreach (var station in _stations)
            foreach (var lot in station.Queue)
            {
                h = h * 31 + (ulong)lot.Group;
                h = h * 31 + (ulong)lot.Count;
                h = h * 31 + (ulong)lot.Dest;
                h = h * 31 + (ulong)lot.Since;
            }
        for (int i = 0; i < _riders.Length; i++)
            if (_riders[i] is { } riders)
                foreach (var lot in riders)
                {
                    h = h * 31 + (ulong)i;
                    h = h * 31 + (ulong)lot.Group;
                    h = h * 31 + (ulong)lot.Count;
                    h = h * 31 + (ulong)lot.Dest;
                }
        if (_shelters.Count > 0) h = h * 31 + ShelterHash();
        h = h * 31 + (ulong)_tripVehicles;
        foreach (float level in _congestion) h = h * 31 + BitConverter.SingleToUInt32Bits(level);
        h = h * 31 + (ulong)_tripEvents.Count;
        h = h * 31 + (ulong)_eventOrder;
        var (a, b, c, d) = _tripRng.State;
        return h ^ a ^ (b << 1) ^ (c << 2) ^ (d << 3);
    }
}
