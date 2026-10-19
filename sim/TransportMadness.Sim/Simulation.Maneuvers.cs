namespace TransportMadness.Sim;

/// <summary>Lane changes on multi-lane carriageways and overtaking via the oncoming lane on 2-lane roads.</summary>
public sealed partial class Simulation
{
    const float HomeLaneLat = 0.5f; // from here on a vehicle no longer blocks the lane it left
    const float OncomingSightLat = 0.3f; // from here on a vehicle is a hazard for oncoming traffic

    void DecideManoeuvres()
    {
        for (int i = 0; i < _count; i++)
        {
            if (_cooldown[i] > 0) _cooldown[i]--;
            if ((i + Tick) % _manoeuvre.DecisionInterval != 0 || _halted[i] || _keepLane[i] || _phase[i] != 0) continue;
            if (!TryLaneChange(i)) TryStartOvertake(i);
        }
    }

    // ---- Lane changes (avenues: free lane choice) -------------------------------------------------------------

    bool TryLaneChange(int i)
    {
        int lane = _lane[i];
        int left = _laneLeft[lane], right = _laneRight[lane];
        if (left < 0 && right < 0) return false;
        // Not near a junction: traffic still arriving from the feeding lanes would not be seen from here.
        if (_cooldown[i] > 0 || _pos[i] < _laneChange.EndMargin || _laneLength[lane] - _pos[i] < _laneChange.EndMargin) return false;

        float v0 = FreeSpeed(i);
        bool hasLeader = FindLeader(i, out _, out float gap, out float leadSpeed);
        bool hindered = hasLeader && gap < _laneChange.HinderedGap && leadSpeed < v0 * _laneChange.HinderedSpeedShare && v0 - leadSpeed > _laneChange.HinderedSpeedDeficit;

        if (hindered && left >= 0 && CanMoveInto(i, left, out float newGap, out float newLeadSpeed)
            && (newGap > gap + _laneChange.LeftGapAdvantage || newLeadSpeed > leadSpeed + _laneChange.LeftSpeedAdvantage))
            return ChangeLane(i, left);

        if (!hindered && right >= 0 && CanMoveInto(i, right, out newGap, out newLeadSpeed)
            && (newGap > _laneChange.RightFreeGap || newLeadSpeed >= v0 * _laneChange.RightSpeedShare))
            return ChangeLane(i, right);
        return false;
    }

    /// <summary>Checks the gaps ahead and behind in a neighbouring lane; reports the gap and speed of the vehicle ahead.</summary>
    bool CanMoveInto(int i, int target, out float frontGap, out float frontSpeed)
    {
        float v = _speed[i];
        float pos = _pos[i];
        frontGap = float.MaxValue;
        frontSpeed = float.MaxValue;
        int ahead = -1, behind = -1;
        for (int r = _laneStart[target]; r < _laneStart[target + 1]; r++)
        {
            int j = _order[r];
            if (_pos[j] >= pos) { ahead = j; break; }
            behind = j;
        }
        if (ahead >= 0)
        {
            frontGap = _pos[ahead] - VehicleLength - pos;
            frontSpeed = _speed[ahead];
            if (frontGap < _laneChange.FrontGapBase + _laneChange.FrontGapPerSpeed * v || (frontGap < _laneChange.ClosingGap && frontSpeed < v - _laneChange.ClosingSpeedDelta)) return false;
        }
        if (behind >= 0)
        {
            float rearGap = pos - VehicleLength - _pos[behind];
            if (rearGap < _laneChange.RearGapBase + MathF.Max(0f, _speed[behind] - v) * _laneChange.RearGapPerSpeedDifference) return false;
        }
        return true;
    }

    bool ChangeLane(int i, int target)
    {
        int oldNextRoad = _laneRoad[_nextLane[i]];
        int oldLane = _lane[i];
        // Drawn as a gradual move: start where the old lane was (to the right if going left) and fade out.
        _shift[i] = target == _laneLeft[oldLane] ? LaneWidth : -LaneWidth;
        _lane[i] = target;
        _cooldown[i] = _laneChange.Cooldown;
        // Keep heading for the same road when the new lane can take it; otherwise pick a turn.
        int keep = -1;
        for (int k = _nextStart[target]; k < _nextStart[target + 1]; k++)
            if (_laneRoad[_nextLanes[k]] == oldNextRoad) { keep = _nextLanes[k]; break; }
        _nextLane[i] = keep >= 0 ? keep : ChooseNextLane(target);
        MoveInOrder(i, oldLane);
        return true;
    }

    // ---- Overtaking via the oncoming lane ------------------------------------------------------------------------

    void TryStartOvertake(int i)
    {
        int lane = _lane[i];
        int oncoming = _laneOncoming[lane];
        if (oncoming < 0 || _cooldown[i] > 0 || _pos[i] < _overtake.MinStart) return;

        float v0 = FreeSpeed(i);
        if (!FindLeader(i, out int lead, out float gap, out float leadSpeed) || _lane[lead] != lane
            || _halted[lead] || leadSpeed < _overtake.MinLeaderSpeed || gap > _overtake.MaxLeaderGap
            || leadSpeed > v0 * _overtake.SlowLeaderShare || v0 - leadSpeed < _overtake.MinSpeedDeficit)
        {
            _stuck[i] = MathF.Max(0f, _stuck[i] - _manoeuvre.DecisionInterval * StepSeconds);
            return;
        }
        if (HaltedAhead(lead, _overtake.HaltedLookahead) || QueueAhead(lead, _overtake.QueueLookahead))
        {
            _stuck[i] = 0f; // the slow leader is only slow because the road is blocked or queued; passing it gains nothing
            return;
        }
        _stuck[i] += _manoeuvre.DecisionInterval * StepSeconds;
        if (_stuck[i] < _overtake.StuckBase + _overtake.StuckPatience * _patience[i]) return;

        // Pessimistic timing: the pass starts from the current speed and accelerates, so use the mean speed.
        float passSpeed = v0 * OvertakeBoost(i);
        float meanSpeed = 0.5f * (_speed[i] + passSpeed);
        float relative = MathF.Max(meanSpeed - leadSpeed, _overtake.MinRelativeSpeed);
        float passTime = (gap + 2f * VehicleLength + _overtake.PassLengthPad) / relative + 1f / _manoeuvre.LateralRate;
        if (_laneLength[lane] - _pos[i] < passSpeed * passTime + _overtake.EndAbortMargin + _overtake.EndRoom) return;

        if (NearestOncoming(lane, oncoming, _pos[i], out _, out float onGap, out float onSpeed))
        {
            float sight = _overtake.SightBase + _overtake.SightAttention * _attention[i];
            if (onGap < sight)
            {
                float perceived = onSpeed * (1f - _overtake.Misperception * (1f - _attention[i]));
                // Inattentive drivers also underestimate how long the pass will take.
                float believedTime = passTime * (_overtake.BelievedTimeBase + _overtake.BelievedTimeAttention * _attention[i]);
                float required = (passSpeed + perceived) * believedTime + _overtake.SafetyMargin * (1f - _overtake.SafetyAggression * _aggression[i]);
                if (onGap < required) return;
            }
        }

        _phase[i] = (byte)OvertakePhase.MovingOut;
        _target[i] = lead;
        _aborting[i] = false;
    }

    /// <summary>
    /// Whether a vehicle crawling below <see cref="OvertakeTuning.QueueSpeed"/> is within <paramref name="range"/> metres ahead of
    /// <paramref name="vehicle"/>: the slow leader is the tail of a queue, and there is nowhere to pull back in.
    /// </summary>
    bool QueueAhead(int vehicle, float range)
    {
        int lane = _lane[vehicle];
        for (int r = _rankInOrder[vehicle]; r < _laneStart[lane + 1]; r++)
        {
            int j = _order[r];
            if (_pos[j] - _pos[vehicle] > range) return false;
            if (_speed[j] < _overtake.QueueSpeed) return true;
        }
        return false;
    }

    /// <summary>A halted vehicle (wreck, breakdown) within <paramref name="range"/> metres ahead of <paramref name="vehicle"/> in its lane.</summary>
    bool HaltedAhead(int vehicle, float range)
    {
        int lane = _lane[vehicle];
        for (int r = _rankInOrder[vehicle]; r < _laneStart[lane + 1]; r++)
        {
            int j = _order[r];
            if (_pos[j] - _pos[vehicle] > range) return false;
            if (_halted[j]) return true;
        }
        return false;
    }

    /// <summary>
    /// Nearest vehicle in the oncoming lane that is still ahead of a vehicle at <paramref name="pos"/> on
    /// <paramref name="lane"/>. Positions on the oncoming lane run the other way, so the gap is
    /// length - theirPosition - pos, measured to the front bumper they present to us.
    /// </summary>
    bool NearestOncoming(int lane, int oncoming, float pos, out int vehicle, out float gap, out float speed)
    {
        float length = _laneLength[lane];
        for (int r = _laneStart[oncoming + 1] - 1; r >= _laneStart[oncoming]; r--)
        {
            int j = _order[r];
            if (_lat[j] >= HomeLaneLat) continue; // that one has swung into our lane instead
            float g = length - _pos[j] - pos;
            if (g < 0f) continue;
            vehicle = j;
            gap = g;
            speed = _speed[j];
            return true;
        }
        vehicle = -1;
        gap = 0f;
        speed = 0f;
        return false;
    }

    /// <summary>Per step: observe oncoming traffic from every overtaker, then advance each manoeuvre.</summary>
    void PrepareOvertakes()
    {
        for (int i = 0; i < _count; i++)
        {
            _extraGap[i] = float.MaxValue;
            _oncGap[i] = float.MaxValue;
        }
        _overtakers.Clear();
        for (int i = 0; i < _count; i++)
            if (_phase[i] != 0 && _kind[i] != VehicleKind.Removed) _overtakers.Add(i);

        foreach (int o in _overtakers)
        {
            int lane = _lane[o];
            int oncoming = _laneOncoming[lane];
            if (oncoming < 0 || !NearestOncoming(lane, oncoming, _pos[o], out int other, out float gap, out float speed))
                continue;
            _oncGap[o] = gap;
            _oncSpeed[o] = speed;
            if (_lat[o] >= OncomingSightLat && gap < _extraGap[other])
            {
                _extraGap[other] = gap;
                _extraSpeed[other] = -_speed[o];
            }
        }

        foreach (int o in _overtakers)
            if (!_halted[o]) AdvanceOvertake(o);
    }

    void AdvanceOvertake(int o)
    {
        var phase = (OvertakePhase)_phase[o];
        if (phase is OvertakePhase.MovingOut or OvertakePhase.Passing && TrackStall(o))
        {
            // Nose to nose with oncoming traffic and nobody can back up: cut back in, even beside the target.
            _phase[o] = (byte)OvertakePhase.Returning;
            return;
        }
        switch (phase)
        {
            case OvertakePhase.MovingOut:
                _lat[o] = MathF.Min(1f, _lat[o] + _manoeuvre.LateralRate * StepSeconds);
                if (!ConsiderAbort(o) && _lat[o] >= 1f) _phase[o] = (byte)OvertakePhase.Passing;
                break;
            case OvertakePhase.Passing:
                if (!ConsiderAbort(o) && TargetPassed(o) && CanReturn(o)) _phase[o] = (byte)OvertakePhase.Returning;
                break;
            case OvertakePhase.Returning:
                _lat[o] = MathF.Max(0f, _lat[o] - _manoeuvre.LateralRate * StepSeconds);
                if (_lat[o] <= 0f) FinishOvertake(o);
                break;
        }
    }

    /// <summary>Counts how long an overtaker has been standing still; true once it has been stuck too long.</summary>
    bool TrackStall(int o)
    {
        _stall[o] = _speed[o] < _manoeuvre.StallSpeed ? _stall[o] + StepSeconds : 0f;
        return _stall[o] > _manoeuvre.StallLimit;
    }

    /// <summary>
    /// If oncoming traffic is closing too fast, bail out: back in behind the target when still behind it, or
    /// straight in when already clear of it. A vehicle alongside its target has no way back and carries on.
    /// </summary>
    bool ConsiderAbort(int o)
    {
        bool nearEnd = _laneLength[_lane[o]] - _pos[o] < _overtake.EndAbortMargin;
        if (!nearEnd)
        {
            if (_oncGap[o] >= float.MaxValue) return false;
            float closing = _speed[o] + _oncSpeed[o];
            bool tooFast = closing >= 0.1f && _oncGap[o] / closing < _overtake.AbortTimeBase + _overtake.AbortTimeAttention * _attention[o];
            if (!tooFast && _oncGap[o] >= _overtake.StandingObstacle) return false;
        }

        int t = _target[o];
        bool targetHere = t >= 0 && _lane[t] == _lane[o];
        if (targetHere && _pos[o] <= _pos[t] - VehicleLength)
        {
            _aborting[o] = true;
            _phase[o] = (byte)OvertakePhase.Returning;
            return true;
        }
        if (TargetPassed(o) && CanReturn(o))
        {
            _phase[o] = (byte)OvertakePhase.Returning;
            return true;
        }
        return false;
    }

    /// <summary>The overtaken vehicle is entirely behind us (or no longer in this lane).</summary>
    bool TargetPassed(int o)
    {
        int t = _target[o];
        return t < 0 || _lane[t] != _lane[o] || _pos[o] - VehicleLength > _pos[t];
    }

    /// <summary>The vehicle is clear of everything alongside it, with room ahead and behind to pull back in.</summary>
    bool CanReturn(int o)
    {
        int lane = _lane[o];
        float front = _pos[o];
        float rear = _pos[o] - VehicleLength;
        int ahead = -1, behind = -1;
        for (int r = _laneStart[lane]; r < _laneStart[lane + 1]; r++)
        {
            int j = _order[r];
            if (j == o || _lat[j] >= HomeLaneLat) continue;
            if (_pos[j] < rear) { behind = j; continue; }                    // entirely behind us
            if (_pos[j] - VehicleLength > front) { if (ahead < 0) ahead = j; continue; } // entirely ahead
            return false;                                                    // alongside
        }
        if (behind >= 0 && rear - _pos[behind] < _overtake.ReturnBehindBase + MathF.Max(0f, _speed[behind] - _speed[o]) * _overtake.ReturnBehindPerSpeed) return false;
        if (ahead >= 0 && _pos[ahead] - VehicleLength - front < _overtake.ReturnAheadBase + MathF.Max(0f, _speed[o] - _speed[ahead]) * _overtake.ReturnAheadPerSpeed) return false;
        return true;
    }

    void FinishOvertake(int o)
    {
        _phase[o] = 0;
        _lat[o] = 0f;
        _target[o] = -1;
        _aborting[o] = false;
        _stuck[o] = 0f;
        _stall[o] = 0f;
        _cooldown[o] = _overtake.Cooldown;
    }
}
