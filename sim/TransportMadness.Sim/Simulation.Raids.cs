namespace TransportMadness.Sim;

/// <summary>
/// Raids (C9d): from the winter of 1942 enemy aircraft bomb the Region at random moments. A raid stops an industry or shuts a gateway for a time, fails
/// a railway signal, destroys buildings of a town (their people counted as casualties, a number only) and leaves craters in the nearest roads; the
/// crews clear the craters and the signals as they clear any wreck.
/// </summary>
public sealed partial class Simulation
{
    const ulong RaidSeedMix = 0x5241494453425F31UL;

    readonly SimRandom _raidRng;
    long _nextRaid = -1;
    int _raids;
    long _casualties;
    int _damaged; // industries and gateways under repair

    /// <summary>Raids so far.</summary>
    public int Raids => _raids;
    /// <summary>People killed or hurt in raids so far, in all the towns (a number only).</summary>
    public long Casualties => _casualties;
    /// <summary>The tick the next raid comes at, -1 before the raids have begun or with the frequency option off.</summary>
    public long NextRaidTick => _nextRaid;

    long RaidGapTicks()
    {
        var gap = _war.Bombing.GapOf(Phase.Id);
        int days = gap.MinDays + _raidRng.NextInt(gap.MaxDays - gap.MinDays + 1);
        return (long)Math.Max(1f, days * _ticksPerDay / _frequencyScale) + _raidRng.NextInt((int)_ticksPerDay);
    }

    /// <summary>One step of the raids: the damage that has been repaired is cleared, and a raid comes when it is time.</summary>
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
    }

    void Raid()
    {
        var kinds = _war.Bombing.Targets.Where(HasTarget).ToList();
        if (kinds.Count == 0) return;
        int roll = _raidRng.NextInt(kinds.Sum(t => t.Weight));
        RaidTargetTuning pick = kinds[0];
        foreach (var t in kinds)
        {
            roll -= t.Weight;
            if (roll < 0) { pick = t; break; }
        }
        _raids++;
        switch (pick.Kind)
        {
            case "industry": BombIndustry(); break;
            case "gateway": BombGateway(); break;
            case "rail": BombRailway(); break;
            default: BombTown(); break;
        }
    }

    bool HasTarget(RaidTargetTuning kind) => kind.Kind switch
    {
        "industry" => _industries.Any(i => i.DamagedUntil == 0),
        "gateway" => _gateways.Any(g => g.DamagedUntil == 0),
        "rail" => _rail.Signals.Any(s => s.Active && !s.Failed && s.PendingTick < 0),
        _ => _towns.Any(t => t.BuildingIds.Any(b => _buildings[b].State == BuildingState.Active)),
    };

    void BombIndustry()
    {
        var open = _industries.Where(i => i.DamagedUntil == 0).ToList();
        var industry = open[_raidRng.NextInt(open.Count)];
        var type = _industryTuning.Types[industry.Type];
        industry.DamagedUntil = Tick + (long)_war.Bombing.IndustryDownDays * _ticksPerDay;
        _damaged++;
        for (int c = 0; c < industry.Input.Length; c++) industry.Input[c] *= 1f - _war.Bombing.InputLoss;
        industry.Output *= 1f - _war.Bombing.InputLoss;
        industry.Load = 0f;
        BuildingsVersion++;
        AddEvent(SimEventKind.Raid, EventSeverity.Serious, industry.X, industry.Z, industry.Id,
            $"Raid: the {type.Name.ToLowerInvariant()} was bombed and is out of work for {_war.Bombing.IndustryDownDays} days");
        Craters(industry.X, industry.Z);
    }

    void BombGateway()
    {
        var open = _gateways.Where(g => g.DamagedUntil == 0).ToList();
        var gateway = open[_raidRng.NextInt(open.Count)];
        gateway.DamagedUntil = Tick + (long)_war.Bombing.GatewayDownDays * _ticksPerDay;
        _damaged++;
        _coverageDirty = true;
        BuildingsVersion++;
        AddEvent(SimEventKind.Raid, EventSeverity.Serious, gateway.X, gateway.Z, gateway.Id,
            $"Raid: the {gateway.Name} was bombed and is shut for {_war.Bombing.GatewayDownDays} days");
        Craters(gateway.X, gateway.Z);
    }

    void BombRailway()
    {
        var signals = _rail.Signals.Where(s => s.Active && !s.Failed && s.PendingTick < 0).ToList();
        var signal = signals[_raidRng.NextInt(signals.Count)];
        var (x, z) = _rail.GetNode(signal.Node);
        FailSignal(signal);
        AddEvent(SimEventKind.Raid, EventSeverity.Serious, x, z, signal.Id.Value, "Raid: the railway was bombed. Trains wait until a crew has repaired the line");
    }

    void BombTown()
    {
        var towns = _towns.Where(t => t.BuildingIds.Any(b => _buildings[b].State == BuildingState.Active)).ToList();
        var town = towns[_raidRng.NextInt(towns.Count)];
        long dead = 0;
        int destroyed = 0;
        for (int k = 0; k < _war.Bombing.BuildingsHit; k++)
        {
            var standing = town.BuildingIds.Where(b => _buildings[b].State == BuildingState.Active).ToList();
            if (standing.Count == 0) break;
            var b = _buildings[standing[_raidRng.NextInt(standing.Count)]];
            var tier = _townTuning.Of(b.Type, b.Tier);
            int people = b.Type == BuildingType.Home ? tier.People : tier.Jobs;
            dead += (long)Math.Round(people * _war.Bombing.CasualtyShare);
            Demolish(b);
            destroyed++;
        }
        _casualties += dead;
        town.Casualties += (int)dead;
        Dissatisfy(town.Id, _war.Bombing.TownLoss);
        AddEvent(SimEventKind.Raid, EventSeverity.Serious, town.X, town.Z, town.Id,
            $"Raid: {town.Name} was bombed. {destroyed} buildings destroyed, {dead} casualties");
        Craters(town.X, town.Z);
    }

    /// <summary>Leaves craters in the nearest roads: debris across a lane that blocks it until a crew has cleared it.</summary>
    void Craters(float x, float z)
    {
        float radius = _war.Bombing.CraterRadius;
        for (int k = 0; k < _war.Bombing.Craters; k++)
        {
            float angle = _raidRng.NextFloat() * MathF.PI * 2f, d = _raidRng.NextFloat() * radius;
            if (_net.RoadAt(x + MathF.Cos(angle) * d, z + MathF.Sin(angle) * d, radius) is not { } roadId) continue;
            var road = _net.GetRoad(roadId);
            if (road.Retired) continue;
            int lane = road.ForwardLanes[0].Value;
            float length = _net.GetLane(new LaneId(lane)).Length;
            if (length < 2f * _traffic.SpawnClearance) continue;
            float position = _traffic.SpawnClearance + _raidRng.NextFloat() * (length - 2f * _traffic.SpawnClearance);
            NewIncidentAt(IncidentKind.Bombing, lane, position);
        }
    }

    ulong RaidHash()
    {
        ulong h = (ulong)_raids;
        h = h * 31 + (ulong)(_nextRaid + 1);
        h = h * 31 + (ulong)_casualties;
        foreach (var industry in _industries) h = h * 31 + (ulong)industry.DamagedUntil;
        foreach (var gateway in _gateways) h = h * 31 + (ulong)gateway.DamagedUntil;
        var (a, b, c, d) = _raidRng.State;
        return h ^ a ^ (b << 1) ^ (c << 2) ^ (d << 3);
    }
}
