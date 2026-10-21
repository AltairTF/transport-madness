namespace TransportMadness.Sim;

/// <summary>What a flight of bombers has chosen to bomb.</summary>
public enum RaidTargetKind : byte { Industry, Gateway, Town, Station, Depot, RailDepot, Signal }

/// <summary>Where a flight is in its run.</summary>
public enum FlightPhase : byte
{
    /// <summary>Flying in, no bomb dropped yet.</summary>
    Approaching,
    /// <summary>Over the target, dropping.</summary>
    Over,
    /// <summary>Everything dropped, flying out of the map.</summary>
    Leaving,
}

/// <summary>
/// A flight of enemy bombers (C12): aircraft that come in from the side of the map the game drew, fly straight over the target they chose and out
/// of the map, and each drop a stick of bombs on the way. It is stepped, hashed and drawn from this state.
/// </summary>
public sealed class Flight
{
    internal Flight(int id, long launchTick, int aircraft, int bombsEach)
    {
        Id = id;
        LaunchTick = launchTick;
        Aircraft = aircraft;
        BombsEach = bombsEach;
        Next = new int[aircraft];
        Aim = new float[aircraft];
        Down = new bool[aircraft];
    }

    public int Id { get; }
    public long LaunchTick { get; }
    /// <summary>How many aircraft fly.</summary>
    public int Aircraft { get; }
    public int BombsEach { get; }
    /// <summary>Bombs the flight will drop in all (less the ones an aircraft shot down never let go of) and bombs it has dropped so far.</summary>
    public int BombsPlanned => Aircraft * BombsEach - BombsLost;
    /// <summary>Aircraft the anti-aircraft has brought down so far (C13), and whether the aircraft at an index is one of them.</summary>
    public int ShotDown { get; internal set; }
    public bool IsDown(int aircraft) => Down[aircraft];
    public int BombsDropped { get; internal set; }
    public FlightPhase Phase => BombsDropped == 0 ? FlightPhase.Approaching : BombsDropped < BombsPlanned ? FlightPhase.Over : FlightPhase.Leaving;
    /// <summary>Metres per second and metres above the ground.</summary>
    public float Speed { get; internal set; }
    public float Altitude { get; internal set; }
    /// <summary>Where it came in and where it leaves the map (outside the edge).</summary>
    public float EntryX { get; internal set; }
    public float EntryZ { get; internal set; }
    public float ExitX { get; internal set; }
    public float ExitZ { get; internal set; }
    /// <summary>The unit direction of flight on the ground, and its angle (radians, the way <c>atan2(z, x)</c> measures it).</summary>
    public float DirX { get; internal set; }
    public float DirZ { get; internal set; }
    public float Heading { get; internal set; }
    /// <summary>Metres from entry to exit, and from entry to the target.</summary>
    public float Length { get; internal set; }
    public float TargetDistance { get; internal set; }
    /// <summary>How far the leading aircraft has flown from the entry point.</summary>
    public float Travelled { get; internal set; }
    public RaidTargetKind TargetKind { get; internal set; }
    public int TargetId { get; internal set; }
    public string TargetName { get; internal set; } = "";
    public float TargetX { get; internal set; }
    public float TargetZ { get; internal set; }
    /// <summary>The tick the first bomb of the flight could land at the earliest, which is how long people have after the siren to reach cover (C14).</summary>
    public long FirstImpactTick { get; internal set; }
    /// <summary>Whether the player has been told what it is going for (in the last <c>revealSeconds</c> before it arrives).</summary>
    public bool TargetRevealed { get; internal set; }
    /// <summary>Seconds of simulation time left until the leading aircraft is over the target (0 once it has passed).</summary>
    public float SecondsToTarget => MathF.Max(0f, (TargetDistance - Travelled) / Speed);

    // The distance a bomb travels forward while it falls, and the places of the sticks.
    internal float Lead;
    internal readonly int[] Next;
    internal readonly float[] Aim;
    internal readonly bool[] Down;
    internal int BombsLost;
    internal SimRandom Rng = null!;
    internal int InFlight;
    internal int Buildings, Wrecks, Craters, IndustriesHit, GatewaysHit, StationsShut, DepotsShut, SignalsFailed;
    internal long Casualties;
}

/// <summary>A bomb that has been dropped and not yet landed.</summary>
public sealed class Bomb
{
    internal Bomb(int id, int flight, long releaseTick, long impactTick, float fromX, float fromZ, float impactX, float impactZ, float altitude)
    {
        Id = id;
        Flight = flight;
        ReleaseTick = releaseTick;
        ImpactTick = impactTick;
        FromX = fromX;
        FromZ = fromZ;
        ImpactX = impactX;
        ImpactZ = impactZ;
        Altitude = altitude;
    }

    public int Id { get; }
    public int Flight { get; }
    public long ReleaseTick { get; }
    public long ImpactTick { get; }
    /// <summary>Where the aircraft was when it let go, and where the bomb will land.</summary>
    public float FromX { get; }
    public float FromZ { get; }
    public float ImpactX { get; }
    public float ImpactZ { get; }
    /// <summary>The height it falls from.</summary>
    public float Altitude { get; }
}

/// <summary>A bomb that has landed (or an aircraft that has crashed), for the picture of the flash and the scar (the effect on the world is already in the state).</summary>
public readonly record struct Impact(int Id, long Tick, float X, float Z, int Flight);

/// <summary>An aircraft the anti-aircraft has brought down (C13), on its way to the ground.</summary>
public sealed class FallingAircraft
{
    internal FallingAircraft(int id, int flight, int aircraft, long hitTick, long landTick, float fromX, float fromZ, float toX, float toZ, float altitude, float heading)
    {
        Id = id;
        Flight = flight;
        Aircraft = aircraft;
        HitTick = hitTick;
        LandTick = landTick;
        FromX = fromX;
        FromZ = fromZ;
        ToX = toX;
        ToZ = toZ;
        Altitude = altitude;
        Heading = heading;
    }

    public int Id { get; }
    public int Flight { get; }
    /// <summary>Its index in the flight.</summary>
    public int Aircraft { get; }
    public long HitTick { get; }
    public long LandTick { get; }
    /// <summary>Where it was hit and where it will come down.</summary>
    public float FromX { get; }
    public float FromZ { get; }
    public float ToX { get; }
    public float ToZ { get; }
    /// <summary>The height it was hit at, and the way it was heading.</summary>
    public float Altitude { get; }
    public float Heading { get; }
}

/// <summary>
/// Raids (C9d, made into flights in C12): from the winter of 1942 enemy bombers come in from one side of the map at random moments. A flight picks
/// its target by value and flies straight over it; each aircraft drops a stick of bombs and each bomb does what is inside its blast radius when it
/// lands: buildings are destroyed, an industry is stopped for longer with every bomb, gateways, stations and depots are shut for a time, vehicles
/// become wrecks, a signal fails, the nearest road gets a crater. The crews clear the craters and the signals as they clear any wreck.
/// </summary>
public sealed partial class Simulation
{
    const ulong RaidSeedMix = 0x5241494453425F31UL;
    const ulong RaidSideMix = 0x464C4947485453A5UL; // the side the aircraft come from has a stream of its own
    const float Gravity = 9.81f;
    const int ImpactsKept = 300;

    static readonly string[] Directions = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };

    readonly SimRandom _raidRng;
    readonly int _raidDirection;
    long _nextRaid = -1;
    int _raids;
    int _bombsDropped;
    long _casualties;
    int _damaged; // industries, gateways, stations and depots that are down or shut
    readonly List<Flight> _flights = new();
    readonly List<Bomb> _bombs = new();
    readonly List<Impact> _impacts = new();
    int _impactBase;
    readonly List<FallingAircraft> _falling = new();

    /// <summary>Flights launched so far.</summary>
    public int Raids => _raids;
    /// <summary>Bombs dropped so far.</summary>
    public int Bombs => _bombsDropped;
    /// <summary>People killed or hurt in raids so far, in all the towns; since C14 they are also gone from the trip groups.</summary>
    public long Casualties => _casualties;
    /// <summary>The tick the next flight comes at, -1 before the raids have begun or with the frequency option off.</summary>
    public long NextRaidTick => _nextRaid;
    /// <summary>The flights in the air now, oldest first.</summary>
    public IReadOnlyList<Flight> Flights => _flights;
    /// <summary>The bombs falling now, in the order they were dropped.</summary>
    public IReadOnlyList<Bomb> BombsFalling => _bombs;
    /// <summary>The bombs that have landed and are still remembered, oldest first; the one with id n is at <c>n - ImpactsDropped</c>.</summary>
    public IReadOnlyList<Impact> Impacts => _impacts;
    public int ImpactCount => _impactBase + _impacts.Count;
    public int ImpactsDropped => _impactBase;
    /// <summary>The aircraft the anti-aircraft has brought down that are still falling, in the order they were hit.</summary>
    public IReadOnlyList<FallingAircraft> FallingAircraft => _falling;
    /// <summary>The side of the map the aircraft come from in this game, 0 (north) to 7 (north-west) clockwise, and its name. North is toward negative z, east toward positive x.</summary>
    public int RaidDirection => _raidDirection;
    public static string DirectionName(int direction) => Directions[direction];

    /// <summary>How many ticks a bomb takes to fall from the flying height.</summary>
    long FallTicks() => Math.Max(1L, (long)MathF.Round(MathF.Sqrt(2f * _war.Bombing.Altitude / Gravity) / StepSeconds));

    /// <summary>How far an aircraft is across its flight's path from the leading one, in metres (positive to the right): the leader flies in the middle and the others spread out in pairs.</summary>
    float WingOffset(int aircraft) => (aircraft % 2 == 1 ? 1f : -1f) * ((aircraft + 1) / 2) * _war.Bombing.WingSpacing;

    /// <summary>Where an aircraft of a flight is on the ground, <paramref name="tickFraction"/> of a step after the last step (for drawing between two steps).</summary>
    public (float X, float Z) AircraftPoint(Flight flight, int aircraft, float tickFraction = 0f)
    {
        float along = flight.Travelled + flight.Speed * StepSeconds * tickFraction - aircraft * _war.Bombing.TrailSpacing;
        float across = WingOffset(aircraft);
        return (flight.EntryX + flight.DirX * along - flight.DirZ * across, flight.EntryZ + flight.DirZ * along + flight.DirX * across);
    }

    /// <summary>Where a falling bomb is, and its height above the ground there, <paramref name="tickFraction"/> of a step after the last step.</summary>
    public (float X, float Height, float Z) BombPoint(Bomb bomb, float tickFraction = 0f)
    {
        float f = Math.Clamp((Tick + tickFraction - bomb.ReleaseTick) / (float)(bomb.ImpactTick - bomb.ReleaseTick), 0f, 1f);
        return (bomb.FromX + (bomb.ImpactX - bomb.FromX) * f, bomb.Altitude * (1f - f * f), bomb.FromZ + (bomb.ImpactZ - bomb.FromZ) * f);
    }

    long RaidGapTicks()
    {
        var gap = _war.Bombing.GapOf(Phase.Id);
        int days = gap.MinDays + _raidRng.NextInt(gap.MaxDays - gap.MinDays + 1);
        return (long)Math.Max(1f, days * _ticksPerDay / _frequencyScale) + _raidRng.NextInt((int)_ticksPerDay);
    }

    /// <summary>One step of the raids: the damage that has been repaired is cleared, and a flight comes when it is time.</summary>
    void StepRaids()
    {
        if (_damaged > 0) CheckRepaired();
        if (_frequencyScale <= 0f || Company.CampaignOver || MonthIndex < _war.Bombing.FromMonth) return;
        if (_nextRaid < 0)
        {
            _nextRaid = Tick + RaidGapTicks();
            return;
        }
        if (Tick < _nextRaid) return;
        Raid();
        _nextRaid = Tick + RaidGapTicks();
    }

    void CheckRepaired()
    {
        foreach (var industry in _industries)
        {
            if (industry.DamagedUntil == 0 || Tick < industry.DamagedUntil) continue;
            industry.DamagedUntil = 0;
            _damaged--;
            BuildingsVersion++;
            AddEvent(SimEventKind.Raid, EventSeverity.Info, industry.X, industry.Z, industry.Id, $"The {_industryTuning.Types[industry.Type].Name.ToLowerInvariant()} is working again");
        }
        foreach (var gateway in _gateways)
        {
            if (gateway.DamagedUntil == 0 || Tick < gateway.DamagedUntil) continue;
            gateway.DamagedUntil = 0;
            _damaged--;
            _coverageDirty = true;
            BuildingsVersion++;
            AddEvent(SimEventKind.Raid, EventSeverity.Info, gateway.X, gateway.Z, gateway.Id, $"The {gateway.Name} is open again");
        }
        foreach (var station in _stations)
        {
            if (station.ShutUntil == 0 || Tick < station.ShutUntil) continue;
            station.ShutUntil = 0;
            _damaged--;
            var (x, z) = PointOf(station);
            AddEvent(SimEventKind.Raid, EventSeverity.Info, x, z, station.Id, $"{StationName(station)} is open again");
        }
        foreach (var depot in _depots)
        {
            if (depot.ShutUntil == 0 || Tick < depot.ShutUntil) continue;
            depot.ShutUntil = 0;
            _damaged--;
            var (x, z) = _net.GetNode(depot.Node);
            AddEvent(SimEventKind.Raid, EventSeverity.Info, x, z, depot.Id, $"Depot {depot.Id + 1} is open again");
        }
        foreach (var depot in _railDepots)
        {
            if (depot.ShutUntil == 0 || Tick < depot.ShutUntil) continue;
            depot.ShutUntil = 0;
            _damaged--;
            var (x, z) = _rail.GetNode(depot.Node);
            AddEvent(SimEventKind.Raid, EventSeverity.Info, x, z, depot.Id, $"Rail depot {depot.Id + 1} is open again");
        }
    }

    /// <summary>Whether a raid has shut a station, a road depot or a rail depot (it neither loads nor unloads, sells nor repairs, sends no crew).</summary>
    public bool IsShut(Station station) => station.ShutUntil > Tick;
    public bool IsShut(Depot depot) => depot.ShutUntil > Tick;
    public bool IsShut(RailDepot depot) => depot.ShutUntil > Tick;

    static string StationName(Station station) => station.Name ?? $"Station {station.Id + 1}";

    // ---- The target -----------------------------------------------------------------------------------------------

    readonly record struct RaidCandidate(RaidTargetKind Kind, int Id, float X, float Z, float Weight, string Name);

    /// <summary>Everything a flight could go for, in a fixed order, with the weight the enemy gives it: its value, less when it is down already or idle.</summary>
    List<RaidCandidate> RaidCandidates()
    {
        var b = _war.Bombing;
        var list = new List<RaidCandidate>();
        void Add(RaidTargetKind kind, int id, float x, float z, int value, bool down, bool idle, string name)
        {
            if (value <= 0) return;
            float weight = value * (down ? b.DamagedFactor : idle ? b.IdleFactor : 1f) * DefenceFactor(x, z);
            if (weight > 0f) list.Add(new RaidCandidate(kind, id, x, z, weight, name));
        }
        foreach (var industry in _industries)
        {
            var type = _industryTuning.Types[industry.Type];
            bool idle = false; // a battery without ammunition is not idle to the enemy, which cannot see its stock
            if (industry.Type != _batteryType)
                foreach (var (cargo, amount) in _industryInputs[industry.Type])
                    if (industry.Input[cargo] < amount) idle = true;
            Add(RaidTargetKind.Industry, industry.Id, industry.X, industry.Z, b.ValueOf(type.Id), industry.DamagedUntil > Tick, idle, type.Name);
        }
        foreach (var gateway in _gateways)
            Add(RaidTargetKind.Gateway, gateway.Id, gateway.X, gateway.Z, b.GatewayValue, gateway.DamagedUntil > Tick, false, gateway.Name);
        foreach (var town in _towns)
            if (town.BuildingIds.Any(id => _buildings[id].State == BuildingState.Active))
                Add(RaidTargetKind.Town, town.Id, town.X, town.Z, b.TownValue, false, false, town.Name);
        foreach (var station in _stations)
        {
            if (!station.Active) continue;
            var (x, z) = PointOf(station);
            Add(RaidTargetKind.Station, station.Id, x, z, b.StationValue, station.ShutUntil > Tick, false, StationName(station));
        }
        foreach (var depot in _depots)
        {
            if (!depot.Active) continue;
            var (x, z) = _net.GetNode(depot.Node);
            Add(RaidTargetKind.Depot, depot.Id, x, z, b.DepotValue, depot.ShutUntil > Tick, false, $"Depot {depot.Id + 1}");
        }
        foreach (var depot in _railDepots)
        {
            if (!depot.Active) continue;
            var (x, z) = _rail.GetNode(depot.Node);
            Add(RaidTargetKind.RailDepot, depot.Id, x, z, b.DepotValue, depot.ShutUntil > Tick, false, $"Rail depot {depot.Id + 1}");
        }
        foreach (var signal in _rail.Signals)
        {
            if (!signal.Active || signal.Failed || signal.PendingTick >= 0) continue;
            var (x, z) = _rail.GetNode(signal.Node);
            Add(RaidTargetKind.Signal, signal.Id.Value, x, z, b.SignalValue, false, false, $"Signal {signal.Id.Value + 1}");
        }
        return list;
    }

    // ---- A flight -------------------------------------------------------------------------------------------------

    /// <summary>A flight is launched: it picks its target by weight and comes in on the side the game drew. A time with nothing worth bombing passes.</summary>
    void Raid()
    {
        var candidates = RaidCandidates();
        if (candidates.Count == 0) return;
        var b = _war.Bombing;
        var range = b.AircraftOf(Phase.Id);
        int aircraft = range.Min + _raidRng.NextInt(range.Max - range.Min + 1);
        float total = 0f;
        foreach (var c in candidates) total += c.Weight;
        float roll = _raidRng.NextFloat() * total;
        var pick = candidates[^1];
        foreach (var c in candidates)
        {
            roll -= c.Weight;
            if (roll < 0f) { pick = c; break; }
        }
        float arc = (_raidRng.NextFloat() * 2f - 1f) * b.EntryArc;
        var rng = new SimRandom(_raidRng.NextUInt64());

        float half = (_net.Terrain?.HalfSize ?? 2048f) + b.EntryMargin;
        float angle = _raidDirection * MathF.PI * 0.25f + arc;
        float ux = MathF.Sin(angle), uz = -MathF.Cos(angle);
        float reach = half / MathF.Max(MathF.Abs(ux), MathF.Abs(uz));
        float ex = ux * reach, ez = uz * reach;
        float dx = pick.X - ex, dz = pick.Z - ez;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        dx /= distance;
        dz /= distance;
        float run = float.PositiveInfinity;
        if (dx > 1e-6f) run = MathF.Min(run, (half - ex) / dx); else if (dx < -1e-6f) run = MathF.Min(run, (-half - ex) / dx);
        if (dz > 1e-6f) run = MathF.Min(run, (half - ez) / dz); else if (dz < -1e-6f) run = MathF.Min(run, (-half - ez) / dz);

        var flight = new Flight(_raids++, Tick, aircraft, b.BombsPerAircraft)
        {
            Speed = b.Speed,
            Altitude = b.Altitude,
            EntryX = ex,
            EntryZ = ez,
            DirX = dx,
            DirZ = dz,
            Heading = MathF.Atan2(dz, dx),
            Length = run,
            ExitX = ex + dx * run,
            ExitZ = ez + dz * run,
            TargetDistance = distance,
            TargetKind = pick.Kind,
            TargetId = pick.Id,
            TargetName = pick.Name,
            TargetX = pick.X,
            TargetZ = pick.Z,
            Lead = b.Speed * FallTicks() * StepSeconds,
            Rng = rng,
        };
        // The first bomb is let go when the leader is a stick's half and the early release short of the target less the lead, and lands a fall later: the
        // leader is then over the first place of its stick, unless the flight is already past it when it enters the map.
        float early = Batteries().Count > 0 ? _war.AntiAircraft.EarlyRelease : 0f;
        float firstAlong = distance - (b.BombsPerAircraft - 1) * 0.5f * b.BombSpacing - early;
        flight.FirstImpactTick = Tick - 1 + Math.Max(FallTicks(), Ticks(firstAlong / b.Speed)); // a flight launched in a step has already flown that step
        for (int a = 0; a < aircraft; a++) flight.Aim[a] = (rng.NextFloat() * 2f - 1f) * b.AimError;
        _flights.Add(flight);

        float edge = (_net.Terrain?.HalfSize ?? 2048f);
        float seconds = distance / b.Speed;
        AddEvent(SimEventKind.Raid, EventSeverity.Serious, Math.Clamp(ex, -edge, edge), Math.Clamp(ez, -edge, edge), flight.Id,
            $"Air raid warning: {aircraft} aircraft from the {DirectionName(_raidDirection)}, arriving in {seconds:0} s");
        SoundSirens(Math.Clamp(ex, -edge, edge), Math.Clamp(ez, -edge, edge));
    }

    /// <summary>One step of the bombing: the bombs that land now do what they do, then the aircraft fly on and let go of the bombs whose time it is.</summary>
    void StepFlights()
    {
        if (_flights.Count == 0 && _bombs.Count == 0 && _falling.Count == 0) return;
        while (_bombs.Count > 0 && _bombs[0].ImpactTick <= Tick)
        {
            var bomb = _bombs[0];
            _bombs.RemoveAt(0);
            var owner = _flights.First(f => f.Id == bomb.Flight);
            owner.InFlight--;
            Land(owner, bomb.ImpactX, bomb.ImpactZ);
        }
        while (_falling.Count > 0 && _falling[0].LandTick <= Tick) Crash(_falling[0]);
        var b = _war.Bombing;
        var batteries = Batteries();
        for (int f = 0; f < _flights.Count; f++)
        {
            var flight = _flights[f];
            flight.Travelled += flight.Speed * StepSeconds;
            if (!flight.TargetRevealed && flight.SecondsToTarget <= b.RevealSeconds)
            {
                flight.TargetRevealed = true;
                AddEvent(SimEventKind.Raid, EventSeverity.Serious, flight.TargetX, flight.TargetZ, flight.Id,
                    $"Air raid: the aircraft are heading for the {flight.TargetName.ToLowerInvariant()}, over it in {flight.SecondsToTarget:0} s");
            }
            if (batteries.Count > 0) Defend(flight, batteries);
            ReleaseBombs(flight);
            bool gone = flight.Travelled - (flight.Aircraft - 1) * b.TrailSpacing >= flight.Length || flight.ShotDown == flight.Aircraft;
            if (gone && flight.BombsDropped == flight.BombsPlanned && flight.InFlight == 0)
            {
                EndFlight(flight);
                _flights.RemoveAt(f--);
                if (_flights.Count == 0) AllClear(flight.TargetX, flight.TargetZ);
            }
        }
    }

    /// <summary>Each aircraft lets go of the next bomb of its stick when it is far enough from the place the bomb will land, as far as the bomb carries it while it falls.</summary>
    void ReleaseBombs(Flight flight)
    {
        var b = _war.Bombing;
        long fall = FallTicks();
        for (int a = 0; a < flight.Aircraft; a++)
        {
            if (flight.Down[a] || flight.Next[a] >= flight.BombsEach) continue;
            float along = flight.Travelled - a * b.TrailSpacing;
            var (fx, fz) = AircraftPoint(flight, a);
            // Under fire the bombs scatter more and are let go early, so that they fall short (C13); without batteries both are nothing.
            float pressure = FirePressure(fx, fz);
            float scatter = ScatterFactor(pressure), early = EarlyRelease(pressure);
            while (flight.Next[a] < flight.BombsEach)
            {
                int k = flight.Next[a];
                float place = (k - (flight.BombsEach - 1) * 0.5f) * b.BombSpacing;
                if (along < flight.TargetDistance + place - flight.Lead - early) break;
                flight.Next[a]++;
                float across = WingOffset(a) + flight.Aim[a] + (flight.Rng.NextFloat() * 2f - 1f) * b.Scatter * scatter;
                float ahead = flight.TargetDistance + place - early + (flight.Rng.NextFloat() * 2f - 1f) * b.ScatterAlong * scatter;
                var bomb = new Bomb(_bombsDropped++, flight.Id, Tick, Tick + fall, fx, fz,
                    flight.EntryX + flight.DirX * ahead - flight.DirZ * across, flight.EntryZ + flight.DirZ * ahead + flight.DirX * across, flight.Altitude);
                _bombs.Add(bomb);
                flight.BombsDropped++;
                flight.InFlight++;
            }
        }
    }

    void EndFlight(Flight flight)
    {
        var parts = new List<string>();
        if (flight.Buildings > 0) parts.Add($"{flight.Buildings} buildings destroyed");
        if (flight.Casualties > 0) parts.Add($"{flight.Casualties} casualties");
        if (flight.IndustriesHit > 0) parts.Add($"{flight.IndustriesHit} industries hit");
        if (flight.GatewaysHit > 0) parts.Add($"{flight.GatewaysHit} gateways hit");
        if (flight.StationsShut > 0) parts.Add($"{flight.StationsShut} stations shut");
        if (flight.DepotsShut > 0) parts.Add($"{flight.DepotsShut} depots shut");
        if (flight.ShotDown > 0) parts.Add($"{flight.ShotDown} aircraft shot down");
        if (flight.Wrecks > 0) parts.Add($"{flight.Wrecks} vehicles lost");
        if (flight.SignalsFailed > 0) parts.Add($"{flight.SignalsFailed} signals failed");
        if (flight.Craters > 0) parts.Add($"{flight.Craters} craters in the roads");
        AddEvent(SimEventKind.Raid, parts.Count > 0 ? EventSeverity.Serious : EventSeverity.Warning, flight.TargetX, flight.TargetZ, flight.Id,
            $"Air raid over: {flight.BombsDropped} bombs dropped on the {flight.TargetName.ToLowerInvariant()} and around it" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : ", and nothing was hit"));
    }

    // ---- A bomb ---------------------------------------------------------------------------------------------------

    /// <summary>A bomb lands: everything within its blast radius suffers, in a fixed order (buildings, industries, gateways, stations, depots, the signal, vehicles, the road).</summary>
    void Land(Flight flight, float x, float z)
    {
        float radius = _war.Bombing.BlastRadius;
        AddImpact(x, z, flight.Id);
        BlastBuildings(flight, x, z, radius);
        BlastOutdoors(flight, x, z, radius);
        BlastIndustries(flight, x, z, radius);
        BlastGateways(flight, x, z, radius);
        BlastStations(flight, x, z, radius);
        BlastDepots(flight, x, z, radius);
        BlastSignal(flight, x, z, radius);
        BlastVehicles(flight, x, z, radius);
        if (Crater(flight.Rng, x, z, _war.Bombing.CraterChance)) flight.Craters++;
        FinishHurt();
    }

    void AddImpact(float x, float z, int flight)
    {
        _impacts.Add(new Impact(ImpactCount, Tick, x, z, flight));
        if (_impacts.Count > ImpactsKept + 50)
        {
            _impacts.RemoveRange(0, 50);
            _impactBase += 50;
        }
    }

    /// <summary>For tests: a bomb of a flight of its own lands at a place at once. Returns the flight, which holds the tallies of what the bomb did.</summary>
    internal Flight BombForTest(float x, float z, ulong seed = 1)
    {
        var flight = new Flight(-1, Tick, 1, 1) { Rng = new SimRandom(seed), Altitude = _war.Bombing.Altitude };
        Land(flight, x, z);
        return flight;
    }

    /// <summary>For tests: launches a flight now.</summary>
    internal void LaunchFlightForTest() => Raid();

    internal IReadOnlyList<(RaidTargetKind Kind, int Id, float Weight)> CandidatesForTest() => RaidCandidates().Select(c => (c.Kind, c.Id, c.Weight)).ToList();

    void AddCasualties(Flight flight, int town, long dead)
    {
        if (dead <= 0) return;
        _casualties += dead;
        flight.Casualties += dead;
        if (town >= 0) _towns[town].Casualties += (int)dead;
    }

    /// <summary>Buildings are destroyed with a chance that falls with the distance from the centre of the blast; a share of the people inside them dies (see <see cref="KillInside"/>).</summary>
    void BlastBuildings(Flight flight, float x, float z, float radius)
    {
        var b = _war.Bombing;
        foreach (int id in BuildingsNear(x, z, radius))
        {
            var building = _buildings[id];
            float d = MathF.Sqrt((building.X - x) * (building.X - x) + (building.Z - z) * (building.Z - z));
            if (flight.Rng.NextFloat() >= b.BuildingChance * (1f - d / radius)) continue;
            if (building.State == BuildingState.Active) KillInside(flight, building);
            Demolish(building);
            flight.Buildings++;
            Dissatisfy(building.Town, b.TownLossPerBuilding);
        }
    }

    /// <summary>An industry stays down longer with every bomb (up to a limit) and loses a share of what it holds each time; a power plant gives no power meanwhile.</summary>
    void BlastIndustries(Flight flight, float x, float z, float radius)
    {
        var b = _war.Bombing;
        foreach (var industry in _industries)
        {
            var type = _industryTuning.Types[industry.Type];
            float reach = radius + type.Size * 0.5f;
            if ((industry.X - x) * (industry.X - x) + (industry.Z - z) * (industry.Z - z) > reach * reach) continue;
            bool first = industry.DamagedUntil == 0;
            if (first)
            {
                _damaged++;
                BuildingsVersion++;
            }
            long from = Math.Max(industry.DamagedUntil, Tick);
            industry.DamagedUntil = Math.Min(Tick + (long)b.IndustryMaxDownDays * _ticksPerDay, from + (long)b.IndustryDownDays * _ticksPerDay);
            for (int c = 0; c < industry.Input.Length; c++) industry.Input[c] *= 1f - b.InputLoss;
            industry.Output *= 1f - b.InputLoss;
            industry.Load = 0f;
            flight.IndustriesHit++;
            if (first)
                AddEvent(SimEventKind.Raid, EventSeverity.Serious, industry.X, industry.Z, industry.Id,
                    $"The {type.Name.ToLowerInvariant()} was bombed and is out of work for {(industry.DamagedUntil - Tick) / (float)_ticksPerDay:0.#} days so far");
        }
    }

    void BlastGateways(Flight flight, float x, float z, float radius)
    {
        var b = _war.Bombing;
        foreach (var gateway in _gateways)
        {
            float reach = radius + _gatewayTuning.Size * 0.5f;
            if ((gateway.X - x) * (gateway.X - x) + (gateway.Z - z) * (gateway.Z - z) > reach * reach) continue;
            bool first = gateway.DamagedUntil == 0;
            if (first)
            {
                _damaged++;
                _coverageDirty = true;
                BuildingsVersion++;
            }
            gateway.DamagedUntil = Math.Max(gateway.DamagedUntil, Tick + (long)b.GatewayDownDays * _ticksPerDay);
            flight.GatewaysHit++;
            if (first)
                AddEvent(SimEventKind.Raid, EventSeverity.Serious, gateway.X, gateway.Z, gateway.Id, $"The {gateway.Name} was bombed and is shut for {b.GatewayDownDays} days");
        }
    }

    void BlastStations(Flight flight, float x, float z, float radius)
    {
        var b = _war.Bombing;
        foreach (var station in _stations)
        {
            if (!station.Active) continue;
            var (sx, sz) = PointOf(station);
            if ((sx - x) * (sx - x) + (sz - z) * (sz - z) > radius * radius) continue;
            bool first = station.ShutUntil == 0;
            if (first) _damaged++;
            station.ShutUntil = Math.Max(station.ShutUntil, Tick + (long)b.StationDownDays * _ticksPerDay);
            if (!first) continue;
            flight.StationsShut++;
            AddEvent(SimEventKind.Raid, EventSeverity.Serious, sx, sz, station.Id, $"{StationName(station)} was bombed and is shut for {b.StationDownDays} days");
        }
    }

    void BlastDepots(Flight flight, float x, float z, float radius)
    {
        var b = _war.Bombing;
        foreach (var depot in _depots)
        {
            if (!depot.Active) continue;
            var (dx, dz) = _net.GetNode(depot.Node);
            if ((dx - x) * (dx - x) + (dz - z) * (dz - z) > radius * radius) continue;
            bool first = depot.ShutUntil == 0;
            if (first) _damaged++;
            depot.ShutUntil = Math.Max(depot.ShutUntil, Tick + (long)b.DepotDownDays * _ticksPerDay);
            if (!first) continue;
            flight.DepotsShut++;
            AddEvent(SimEventKind.Raid, EventSeverity.Serious, dx, dz, depot.Id, $"Depot {depot.Id + 1} was bombed and is shut for {b.DepotDownDays} days");
        }
        foreach (var depot in _railDepots)
        {
            if (!depot.Active) continue;
            var (dx, dz) = _rail.GetNode(depot.Node);
            if ((dx - x) * (dx - x) + (dz - z) * (dz - z) > radius * radius) continue;
            bool first = depot.ShutUntil == 0;
            if (first) _damaged++;
            depot.ShutUntil = Math.Max(depot.ShutUntil, Tick + (long)b.DepotDownDays * _ticksPerDay);
            if (!first) continue;
            flight.DepotsShut++;
            AddEvent(SimEventKind.Raid, EventSeverity.Serious, dx, dz, depot.Id, $"Rail depot {depot.Id + 1} was bombed and is shut for {b.DepotDownDays} days");
        }
    }

    /// <summary>The nearest signal within the blast that is working fails, and trains wait at it until a crew has repaired it.</summary>
    void BlastSignal(Flight flight, float x, float z, float radius)
    {
        RailSignal? nearest = null;
        float best = radius * radius;
        foreach (var signal in _rail.Signals)
        {
            if (!signal.Active || signal.Failed || signal.PendingTick >= 0) continue;
            var (sx, sz) = _rail.GetNode(signal.Node);
            float d = (sx - x) * (sx - x) + (sz - z) * (sz - z);
            if (d > best) continue;
            nearest = signal;
            best = d;
        }
        if (nearest == null) return;
        FailSignal(nearest);
        flight.SignalsFailed++;
    }

    /// <summary>Every moving vehicle whose middle is inside the blast becomes a wreck on the spot (one incident for the bomb); the people aboard are casualties.</summary>
    void BlastVehicles(Flight flight, float x, float z, float radius)
    {
        int incident = -1;
        for (int i = 0; i < _count; i++)
        {
            if (_kind[i] != VehicleKind.Car || _lane[i] < 0 || _ghost[i]) continue;
            float along = Math.Clamp(_pos[i] - _length[i] * 0.5f, 0f, _laneLength[_lane[i]]);
            var (vx, vz, _) = _net.LanePoint(new LaneId(_lane[i]), along);
            if ((vx - x) * (vx - x) + (vz - z) * (vz - z) > radius * radius) continue;
            if (incident < 0) incident = NewIncident(IncidentKind.Bombing, i);
            int people = _war.Bombing.CarPeople, counted = 0;
            if (_line[i] >= 0)
            {
                people = _load[i] > 0 && _loadCargo[i] == _passengerCargo ? _load[i] : 0;
                AddEvent(SimEventKind.Raid, EventSeverity.Serious, vx, vz, i,
                    $"{_models[_model[i]].Name} on {_lines[_line[i]].Name} was destroyed in the raid" + (people > 0 ? $": {people} casualties" : ""));
                // The riders are people of trip groups and die with the bus (what is aboard on a map without queues is only a number).
                if (_riders[i] is { } riders)
                {
                    foreach (var lot in riders)
                    {
                        KillOnTrip(flight, _groups[lot.Group], lot.Count);
                        counted += lot.Count;
                    }
                    _riders[i] = null;
                }
            }
            else if (_tripGroup[i] >= 0)
            {
                // A trip car carries people of a group: as many as an ambient car does die, and the rest of them arrive when it is cleared.
                counted = people = Math.Min(people, _tripPeople[i]);
                KillOnTrip(flight, _groups[_tripGroup[i]], people);
                _tripPeople[i] -= people;
            }
            MakeWreck(i, incident);
            flight.Wrecks++;
            AddCasualties(flight, TownAt(vx, vz), people - counted);
        }
    }

    /// <summary>With a chance the nearest road within reach gets a crater: debris across a lane that blocks it until a crew has cleared it, like a wreck. Whether it did.</summary>
    bool Crater(SimRandom rng, float x, float z, float chance)
    {
        var b = _war.Bombing;
        if (rng.NextFloat() >= chance) return false;
        if (_net.RoadAt(x, z, b.CraterRadius) is not { } roadId) return false;
        var road = _net.GetRoad(roadId);
        bool back = rng.NextFloat() < 0.5f;
        int lane = (back ? road.BackwardLanes : road.ForwardLanes)[0].Value;
        float length = _net.GetLane(new LaneId(lane)).Length;
        if (length < 2f * _traffic.SpawnClearance) return false;
        float along = Math.Clamp(road.Geometry.NearestAlong(x, z) / MathF.Max(road.Length, 0.01f) * length, _traffic.SpawnClearance, length - _traffic.SpawnClearance);
        NewIncidentAt(IncidentKind.Bombing, lane, back ? length - along : along);
        return true;
    }

    ulong RaidHash()
    {
        ulong h = (ulong)_raids;
        void MixF(float v) => h = h * 31 + BitConverter.SingleToUInt32Bits(v);
        h = h * 31 + (ulong)(_nextRaid + 1);
        h = h * 31 + (ulong)_casualties;
        h = h * 31 + (ulong)_bombsDropped;
        h = h * 31 + (ulong)ImpactCount;
        foreach (var industry in _industries) h = h * 31 + (ulong)industry.DamagedUntil;
        foreach (var gateway in _gateways) h = h * 31 + (ulong)gateway.DamagedUntil;
        foreach (var station in _stations) h = h * 31 + (ulong)station.ShutUntil;
        foreach (var depot in _depots) h = h * 31 + (ulong)depot.ShutUntil;
        foreach (var depot in _railDepots) h = h * 31 + (ulong)depot.ShutUntil;
        foreach (var flight in _flights)
        {
            h = h * 31 + (ulong)flight.Id;
            h = h * 31 + (ulong)flight.Aircraft;
            h = h * 31 + (ulong)flight.BombsDropped;
            h = h * 31 + (ulong)flight.InFlight;
            h = h * 31 + (ulong)flight.TargetKind + ((ulong)flight.TargetId << 8) + (flight.TargetRevealed ? 1UL << 40 : 0UL);
            MixF(flight.Travelled);
            MixF(flight.EntryX);
            MixF(flight.EntryZ);
            MixF(flight.DirX);
            MixF(flight.DirZ);
            MixF(flight.TargetDistance);
            MixF(flight.Length);
            for (int a = 0; a < flight.Aircraft; a++)
            {
                h = h * 31 + (ulong)flight.Next[a];
                MixF(flight.Aim[a]);
            }
            if (flight.ShotDown > 0)
            {
                for (int a = 0; a < flight.Aircraft; a++) h = h * 31 + (flight.Down[a] ? 1UL : 0UL);
                h = h * 31 + (ulong)flight.BombsLost;
            }
            var (f0, f1, f2, f3) = flight.Rng.State;
            h ^= f0 ^ (f1 << 1) ^ (f2 << 2) ^ (f3 << 3);
        }
        foreach (var bomb in _bombs)
        {
            h = h * 31 + (ulong)bomb.Id;
            h = h * 31 + (ulong)bomb.ReleaseTick;
            MixF(bomb.FromX);
            MixF(bomb.FromZ);
            MixF(bomb.ImpactX);
            MixF(bomb.ImpactZ);
        }
        if (_defenceDraws > 0) h = h * 31 + DefenceHash();
        var (a0, b0, c0, d0) = _raidRng.State;
        return h ^ a0 ^ (b0 << 1) ^ (c0 << 2) ^ (d0 << 3);
    }
}
