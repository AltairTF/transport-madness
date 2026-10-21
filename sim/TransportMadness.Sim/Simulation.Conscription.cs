namespace TransportMadness.Sim;

/// <summary>
/// The call-up (C9b): waves in which a share of the people of every trip group leave, which lowers the workforce and so the output. Since C15c each wave
/// also lowers for good the capacity of the buildings of every town for people and jobs (<see cref="Town.CapacityFactor"/>), so that what growth builds again
/// holds fewer people.
/// </summary>
public sealed partial class Simulation
{
    int _nextWave;
    int _wavesFired;
    float _workforceGap;
    long _calledUp;
    int _pendingCalls; // groups with people still to take

    /// <summary>The share of the workforce that is missing now, 0 to 1.</summary>
    public float WorkforceGap => _workforceGap;
    /// <summary>The share of its output an industry manages with the workforce that is left.</summary>
    public float WorkforceFactor => 1f - _war.Conscription.OutputLoss * _workforceGap;
    /// <summary>People called up so far, in all the towns.</summary>
    public long CalledUp => _calledUp;
    /// <summary>The wave that comes next, null when there are no more.</summary>
    public WarWave? NextWave => _nextWave < _war.Conscription.Waves.Count ? _war.Conscription.Waves[_nextWave] : null;

    /// <summary>At the turn of a month: the workforce recovers a little, and the waves that begin with the coming month fire.</summary>
    void ConscriptionForMonth(long nextMonth)
    {
        _workforceGap = MathF.Max(0f, _workforceGap - _war.Conscription.RecoveryPerMonth);
        while (_nextWave < _war.Conscription.Waves.Count && _war.Conscription.Waves[_nextWave].Month <= nextMonth)
            CallUp(_war.Conscription.Waves[_nextWave++]);
    }

    void CallUp(WarWave wave)
    {
        _wavesFired++;
        _workforceGap = 1f - (1f - _workforceGap) * (1f - wave.Share);
        long expected = (long)Math.Round(_groups.Where(g => !g.Retired).Sum(g => g.People) * (double)wave.Share);
        foreach (var g in _groups)
        {
            if (g.Retired) continue;
            if (g.PendingCall <= 0f) _pendingCalls++;
            g.PendingCall = 1f - (1f - g.PendingCall) * (1f - wave.Share);
        }
        TakeCalledUp();
        foreach (var town in _towns)
        {
            town.CapacityFactor *= 1f - wave.Share; // the buildings of every town have room for fewer people and jobs for good (C15c)
            Dissatisfy(town.Id, _war.Conscription.SatisfactionLoss);
        }
        AddEvent(SimEventKind.War, EventSeverity.Serious, 0f, 0f, -1,
            $"Call-up: {wave.Share:P0} of the people are called up, about {expected} in all. The workforce is {_workforceGap:P0} short and industry makes {WorkforceFactor:P0} of its output" +
            (_towns.Count > 0 ? $"; the buildings of the towns now have room for {_towns.Min(t => t.CapacityFactor):P0} of the people and jobs they had" : ""));
    }

    /// <summary>Takes the called-up people from the groups that are not on a trip, as many as each group's share and a draw from its own seed say.</summary>
    void TakeCalledUp()
    {
        if (_pendingCalls == 0) return;
        foreach (var g in _groups)
        {
            if (g.PendingCall <= 0f || g.Travelling) continue;
            if (g.Retired) { g.PendingCall = 0f; _pendingCalls--; continue; }
            ulong mix = g.Seed ^ ((ulong)_wavesFired * 0x9E3779B97F4A7C15UL);
            mix ^= mix >> 31;
            mix *= 0xBF58476D1CE4E5B9UL;
            mix ^= mix >> 29;
            float u = (mix >> 40) * (1f / (1UL << 24));
            int leave = Math.Min(g.People, (int)MathF.Floor(g.People * g.PendingCall + u));
            g.PendingCall = 0f;
            _pendingCalls--;
            if (leave <= 0) continue;
            g.People -= leave;
            _calledUp += leave;
            _towns[_buildings[g.Home].Town].CalledUp += leave;
            if (g.Work >= 0) _jobsTaken[g.Work] = Math.Max(0, _jobsTaken.GetValueOrDefault(g.Work) - leave);
            if (g.People == 0) Retire(g);
        }
    }

    ulong ConscriptionHash()
    {
        ulong h = (ulong)_nextWave;
        h = h * 31 + BitConverter.SingleToUInt32Bits(_workforceGap);
        h = h * 31 + (ulong)_calledUp;
        foreach (var g in _groups) h = h * 31 + (ulong)g.People;
        foreach (var town in _towns) h = h * 31 + BitConverter.SingleToUInt32Bits(town.CapacityFactor);
        return h;
    }
}
