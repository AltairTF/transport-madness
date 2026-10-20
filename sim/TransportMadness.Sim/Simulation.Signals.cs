namespace TransportMadness.Sim;

/// <summary>
/// Rail signals wear with the trains let through and sometimes fail: a failed signal shows red to everybody until a crew from the nearest depot
/// has repaired it. With the company policy on, a crew also services a signal whose Condition is under the policy's floor (or that is due by days).
/// </summary>
public sealed partial class Simulation
{
    int _signalPending; // signals with a crew on its way

    /// <summary>How long a crew takes to reach a place and do its work: from the nearest open depot, or the distant service when none is near.</summary>
    float CrewSeconds(float x, float z)
    {
        float best = float.PositiveInfinity;
        foreach (var depot in _depots)
        {
            if (!depot.Active) continue;
            var (dx, dz) = _net.GetNode(depot.Node);
            best = MathF.Min(best, (dx - x) * (dx - x) + (dz - z) * (dz - z));
        }
        foreach (var depot in _railDepots)
        {
            if (!depot.Active) continue;
            var (dx, dz) = _rail.GetNode(depot.Node);
            best = MathF.Min(best, (dx - x) * (dx - x) + (dz - z) * (dz - z));
        }
        float response = float.IsPositiveInfinity(best) ? _crew.FallbackResponse : MathF.Min(_crew.FallbackResponse, _crew.DispatchDelay + MathF.Sqrt(best) * _crew.RoadDetour / _crew.Speed);
        return response + _crew.ClearDuration;
    }

    long SignalPrice(RailSignal signal) => signal.Kind == SignalKind.Path ? _railTuning.PathSignalPrice : _railTuning.SignalPrice;

    /// <summary>A train comes up to a signal: it may fail, once for each approach, and then the train waits at it.</summary>
    void MaybeFailSignal(RailSignal signal)
    {
        if (signal.PendingTick >= 0 || _frequencyScale <= 0f || signal.Condition >= _maint.NoRiskAbove) return;
        if (_rng.NextFloat() < HazardPerMetre(signal.Condition) * _maint.Signals.PassageMetres * _frequencyScale) FailSignal(signal);
    }

    /// <summary>A train has been let through a signal: it wears, and the company policy may call a crew.</summary>
    void SignalPassed(RailSignal signal)
    {
        signal.Condition = MathF.Max(0f, signal.Condition - _maint.Signals.WearPerPassage);
        if (signal.PendingTick >= 0) return;
        if (DueBy(_companyPolicy, signal.Condition, signal.LastRepairTick) is { } kind) ScheduleSignalService(signal, kind);
    }

    void FailSignal(RailSignal signal)
    {
        var (x, z) = _rail.GetNode(signal.Node);
        signal.Failed = true;
        signal.FailedTick = Tick;
        signal.PendingKind = -1;
        signal.PendingTick = Tick + Ticks(CrewSeconds(x, z));
        _signalPending++;
        AddEvent(SimEventKind.SignalFailure, EventSeverity.Serious, x, z, signal.Id.Value, "A signal has failed: trains wait at it until a crew has repaired it");
    }

    void ScheduleSignalService(RailSignal signal, RepairKind kind)
    {
        if (signal.PendingTick >= 0 || signal.Failed) return;
        var (x, z) = _rail.GetNode(signal.Node);
        signal.PendingKind = (sbyte)kind;
        signal.PendingTick = Tick + Ticks(CrewSeconds(x, z));
        _signalPending++;
    }

    /// <summary>Lets the crews that have arrived do their work.</summary>
    void StepSignals()
    {
        if (_signalPending == 0) return;
        foreach (var signal in _rail.Signals)
        {
            if (signal.PendingTick < 0) continue;
            if (!signal.Active)
            {
                signal.PendingTick = -1;
                signal.Failed = false;
                _signalPending--;
                continue;
            }
            if (Tick < signal.PendingTick) continue;
            if (signal.Failed)
            {
                bool negligent = (signal.PendingTick - signal.FailedTick) * StepSeconds > _maint.Signals.FineAfterSeconds;
                Charge(_maint.Signals.RepairPrice, LedgerEntry.Maintenance);
                if (negligent) Charge(_maint.Consequences.NegligenceFine, LedgerEntry.Fines);
                signal.Failed = false;
                signal.Condition = _maint.Signals.RepairedCondition;
            }
            else
            {
                var kind = (RepairKind)signal.PendingKind;
                Charge(RepairPrice(kind, SignalPrice(signal)), LedgerEntry.Maintenance);
                signal.Condition = After(kind, signal.Condition).Condition;
            }
            signal.LastRepairTick = Tick;
            signal.PendingTick = -1;
            signal.PendingKind = -1;
            _signalPending--;
        }
    }

    /// <summary>At the turn of a day: signals that are due by the company policy get a crew.</summary>
    void CheckSignals()
    {
        if (_companyPolicy.Mode != PolicyMode.On) return;
        foreach (var signal in _rail.Signals)
        {
            if (!signal.Active || signal.PendingTick >= 0) continue;
            if (DueBy(_companyPolicy, signal.Condition, signal.LastRepairTick) is { } kind) ScheduleSignalService(signal, kind);
        }
    }

    ulong SignalHash()
    {
        ulong h = 0;
        foreach (var signal in _rail.Signals)
        {
            h = h * 31 + BitConverter.SingleToUInt32Bits(signal.Condition);
            h = h * 31 + (signal.Failed ? 1UL : 0UL) + (ulong)(signal.PendingTick + 1) + (ulong)(signal.PendingKind + 1) * 7UL;
            h = h * 31 + (ulong)signal.LastRepairTick;
        }
        return h;
    }
}
