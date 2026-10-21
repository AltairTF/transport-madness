namespace TransportMadness.Sim;

/// <summary>
/// Anti-aircraft (C13). A battery is an industry the player places on free ground from the Mobilisation phase on. It has jobs filled by residents like
/// the other big industries and an ammunition stock that trucks fill, the ammunition being a cargo only the gateways import. It fires once a second at
/// every aircraft within range, a salvo costing ammunition, and each salvo has a chance to bring the aircraft down: the base chance times the battery's
/// staffing. A flight under fire has its bombs scattered more and let go early, and the enemy wants a target less the more batteries cover it.
/// </summary>
public sealed partial class Simulation
{
    const ulong DefenceSeedMix = 0x4141444546454E43UL; // the shots have a stream of their own

    readonly int _batteryType;  // index of the battery among the industry types, -1 when the tuning has none
    readonly int _ammunition;   // the cargo (index) a battery fires, -1 when it has none
    readonly SimRandom _defenceRng;
    int _defenceDraws;
    int _shotDown;

    /// <summary>Aircraft brought down so far.</summary>
    public int AircraftShotDown => _shotDown;
    /// <summary>The cargo (index into the cargo list) a battery fires, -1 when the tuning has no battery.</summary>
    public int AmmunitionCargo => _ammunition;
    /// <summary>The index of the battery among the industry types, -1 when the tuning has none.</summary>
    public int BatteryType => _batteryType;
    public AntiAircraftTuning AntiAircraft => _war.AntiAircraft;

    /// <summary>Whether an industry is an anti-aircraft battery.</summary>
    public bool IsBattery(Industry industry) => industry.Type == _batteryType;

    /// <summary>The batteries, in id order.</summary>
    List<Industry> Batteries()
    {
        var list = new List<Industry>();
        if (_batteryType < 0 || _ammunition < 0) return list;
        foreach (var industry in _industries)
            if (industry.Type == _batteryType) list.Add(industry);
        return list;
    }

    // ---- Building one ------------------------------------------------------------------------------------------

    /// <summary>
    /// Why a battery cannot be built at a place now, null when it can: the campaign is over, it is before the month the guns are available, the ground is
    /// not level and dry, something else stands too near (an industry, a gateway, a battery, a building), a road or a track is under it, or the money
    /// is not there.
    /// </summary>
    public string? WhyNotBattery(float x, float z)
    {
        var a = _war.AntiAircraft;
        if (_batteryType < 0 || _ammunition < 0) return "There are no anti-aircraft guns";
        if (Company.CampaignOver) return "The campaign is over";
        if (MonthIndex < a.FromMonth) return $"Anti-aircraft guns are available from month {a.FromMonth}";
        var type = _industryTuning.Types[_batteryType];
        float reach = type.Size * 1.42f;
        if (!IndustryGenerator.LevelAndDry(_net, _townTuning, x, z, reach, a.MaxHeightRange)) return "The ground is not level and dry";
        float clear2 = a.Clearance * a.Clearance;
        foreach (var industry in _industries)
            if (Dist2(industry.X, industry.Z, x, z) < clear2) return "Too near another industry";
        foreach (var gateway in _gateways)
            if (Dist2(gateway.X, gateway.Z, x, z) < clear2) return "Too near a gateway";
        if (BuildingsNear(x, z, reach + a.BuildingClearance).Count > 0) return "Too near a building";
        if (_net.RoadAt(x, z, reach) != null) return "A road is in the way";
        if (RailInUse && _rail.TrackAt(x, z, reach) != null) return "A track is in the way";
        if (!CanAfford(a.Price)) return "Not enough money";
        return null;
    }

    /// <summary><see cref="BuildBatteryCommand"/>. Returns the id of the industry that is the new battery, -1 when refused.</summary>
    int BuildBattery(float x, float z)
    {
        if (WhyNotBattery(x, z) != null) return -1;
        var a = _war.AntiAircraft;
        Charge(a.Price, LedgerEntry.Construction);
        float y = _net.Terrain?.HeightAt(x, z) ?? 0f;
        var industry = new Industry(_industries.Count, _batteryType, x, y, z, 0f, _cargo.Count);
        _industries.Add(industry);
        SetAccessPoint(industry);
        StaffNewIndustry(industry);
        IndustriesVersion++;
        BuildingsVersion++;
        _coverageDirty = true;
        AddEvent(SimEventKind.War, EventSeverity.Info, x, z, industry.Id, $"An anti-aircraft battery was built; it needs {_industryTuning.Types[_batteryType].Jobs} people and ammunition");
        return industry.Id;
    }

    /// <summary>The upkeep of the batteries, at the turn of a month.</summary>
    void ChargeBatteries()
    {
        if (_batteryType < 0) return;
        foreach (var industry in _industries)
            if (industry.Type == _batteryType && _war.AntiAircraft.UpkeepPerMonth > 0) Charge(_war.AntiAircraft.UpkeepPerMonth, LedgerEntry.Upkeep);
    }

    /// <summary>The upkeep a month of the batteries that stand.</summary>
    public long BatteryUpkeepPerMonth => _industries.Count(i => i.Type == _batteryType) * _war.AntiAircraft.UpkeepPerMonth;

    /// <summary>At the turn of a month: the news that the guns can be had, and that raids are expected.</summary>
    void DefenceNewsForMonth(long nextMonth)
    {
        var a = _war.AntiAircraft;
        if (nextMonth == a.FromMonth)
            AddEvent(SimEventKind.War, EventSeverity.Warning, -1, FormattableString.Invariant(
                $"Anti-aircraft guns can now be built, {a.Price} each and {a.UpkeepPerMonth} a month. A battery needs people to man it and ammunition, which the government sends through the gateways"));
        if (nextMonth == a.ExpectMonth && _frequencyScale > 0f)
            AddEvent(SimEventKind.War, EventSeverity.Serious, -1,
                "Enemy raids are expected: bombers will come over the Region. Batteries that are manned and have ammunition bring them down and spoil their aim; the enemy will go for what is least defended");
    }

    // ---- Cover and fire pressure -------------------------------------------------------------------------------

    /// <summary>
    /// How much the anti-aircraft cover of a place lowers the enemy's wish to bomb it, as a factor to multiply the weight by (C13): the cover is the
    /// staffing of the working batteries that reach the place over the saturation, at most 1, and a place with full cover is wanted <c>strongCover</c>
    /// times as much. The enemy does not know the stock of ammunition a battery holds.
    /// </summary>
    float DefenceFactor(float x, float z)
    {
        if (_batteryType < 0) return 1f;
        var a = _war.AntiAircraft;
        float range2 = a.Range * a.Range, sum = 0f;
        foreach (var industry in _industries)
        {
            if (industry.Type != _batteryType || industry.DamagedUntil > Tick || Dist2(industry.X, industry.Z, x, z) > range2) continue;
            sum += StaffingOf(industry);
        }
        if (sum <= 0f) return 1f;
        return 1f - (1f - a.StrongCover) * MathF.Min(1f, sum / a.CoverSaturation);
    }

    /// <summary>Whether a battery fires at what is in range now: it is not damaged, somebody mans it and it has ammunition for a step of firing.</summary>
    bool CanFire(Industry battery) =>
        battery.DamagedUntil <= Tick && StaffingOf(battery) > 0f && battery.Input[_ammunition] >= _war.AntiAircraft.AmmoPerSalvo * StepSeconds;

    /// <summary>The staffing of the batteries that are firing at a point of the sky, added up: the fire pressure there (0 without batteries).</summary>
    internal float FirePressure(float x, float z)
    {
        if (_batteryType < 0 || _ammunition < 0) return 0f;
        float range2 = _war.AntiAircraft.Range * _war.AntiAircraft.Range, sum = 0f;
        foreach (var industry in _industries)
            if (industry.Type == _batteryType && Dist2(industry.X, industry.Z, x, z) <= range2 && CanFire(industry)) sum += StaffingOf(industry);
        return sum;
    }

    /// <summary>How many times as much a bomb scatters under a fire pressure.</summary>
    internal float ScatterFactor(float pressure) => pressure <= 0f ? 1f : MathF.Min(_war.AntiAircraft.ScatterCap, 1f + _war.AntiAircraft.ScatterGrowth * pressure);

    /// <summary>How many metres early a stick is let go under a fire pressure (the whole of it at a pressure of 1 or more).</summary>
    internal float EarlyRelease(float pressure) => pressure <= 0f ? 0f : _war.AntiAircraft.EarlyRelease * MathF.Min(1f, pressure);

    // ---- Firing ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The batteries fire at the aircraft of a flight that are in range, in a fixed order (aircraft by index, batteries by id): each salvo spends
    /// ammunition, and the chances of the batteries firing at an aircraft add up as 1 - the product of what each misses; a second of firing
    /// is a step's share of that. A battery that finds its stock empty with an aircraft in range says so, once until it has ammunition again.
    /// </summary>
    void Defend(Flight flight, List<Industry> batteries)
    {
        var a = _war.AntiAircraft;
        float spend = a.AmmoPerSalvo * StepSeconds, range2 = a.Range * a.Range;
        for (int k = 0; k < flight.Aircraft; k++)
        {
            if (flight.Down[k]) continue;
            var (x, z) = AircraftPoint(flight, k);
            float miss = 1f;
            foreach (var battery in batteries)
            {
                if (battery.DamagedUntil > Tick || Dist2(battery.X, battery.Z, x, z) > range2 || StaffingOf(battery) <= 0f) continue;
                if (battery.Input[_ammunition] < spend)
                {
                    if (!battery.OutOfAmmo)
                    {
                        battery.OutOfAmmo = true;
                        AddEvent(SimEventKind.Raid, EventSeverity.Warning, battery.X, battery.Z, battery.Id, $"{_industryTuning.Types[_batteryType].Name} out of ammunition");
                    }
                    continue;
                }
                battery.OutOfAmmo = false;
                battery.Input[_ammunition] -= spend;
                miss *= 1f - a.HitChance * StaffingOf(battery);
            }
            if (miss >= 1f) continue;
            _defenceDraws++;
            if (_defenceRng.NextFloat() >= MathF.Pow(miss, StepSeconds)) ShootDown(flight, k, x, z);
        }
    }

    /// <summary>An aircraft is hit: it drops no more bombs, and falls as far as a bomb falls in the time, carrying on a share of the way it would have flown.</summary>
    void ShootDown(Flight flight, int aircraft, float x, float z)
    {
        var a = _war.AntiAircraft;
        flight.Down[aircraft] = true;
        flight.ShotDown++;
        int left = flight.BombsEach - flight.Next[aircraft];
        flight.BombsLost += left;
        flight.Next[aircraft] = flight.BombsEach;
        long fall = FallTicks();
        float carry = flight.Speed * fall * StepSeconds * a.FallCarry;
        _falling.Add(new FallingAircraft(_shotDown++, flight.Id, aircraft, Tick, Tick + fall, x, z, x + flight.DirX * carry, z + flight.DirZ * carry, flight.Altitude, flight.Heading));
        AddEvent(SimEventKind.Raid, EventSeverity.Warning, x, z, flight.Id, "Anti-aircraft shot down an aircraft");
    }

    /// <summary>A downed aircraft reaches the ground: a flash, and a crater in the road if it came down on one.</summary>
    void Crash(FallingAircraft fall)
    {
        _falling.RemoveAt(0);
        AddImpact(fall.ToX, fall.ToZ, fall.Flight);
        Crater(_defenceRng, fall.ToX, fall.ToZ, _war.AntiAircraft.FallCraterChance);
    }

    /// <summary>Where a falling aircraft is, and its height above the ground there, <paramref name="tickFraction"/> of a step after the last step.</summary>
    public (float X, float Height, float Z) FallPoint(FallingAircraft fall, float tickFraction = 0f)
    {
        float f = Math.Clamp((Tick + tickFraction - fall.HitTick) / (float)(fall.LandTick - fall.HitTick), 0f, 1f);
        return (fall.FromX + (fall.ToX - fall.FromX) * f, fall.Altitude * (1f - f * f), fall.FromZ + (fall.ToZ - fall.FromZ) * f);
    }

    /// <summary>For tests: how the batteries fire at a flight's aircraft now, without moving it.</summary>
    internal void DefendForTest(Flight flight) => Defend(flight, Batteries());

    ulong DefenceHash()
    {
        ulong h = (ulong)_defenceDraws;
        h = h * 31 + (ulong)_shotDown;
        foreach (var fall in _falling)
        {
            h = h * 31 + (ulong)fall.Id;
            h = h * 31 + (ulong)fall.LandTick;
            h = h * 31 + BitConverter.SingleToUInt32Bits(fall.ToX);
            h = h * 31 + BitConverter.SingleToUInt32Bits(fall.ToZ);
        }
        var (a, b, c, d) = _defenceRng.State;
        return h ^ a ^ (b << 1) ^ (c << 2) ^ (d << 3);
    }
}
