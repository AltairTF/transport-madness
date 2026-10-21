namespace TransportMadness.Sim;

/// <summary>
/// How trains move: a speed profile from the limits, the signals and the stops ahead; asking for the track beyond a boundary
/// before reaching it (block or path reservation); loading at stations; and finding trains that wait for each other for good.
/// </summary>
public sealed partial class Simulation
{
    const float ReversePenaltySeconds = 25f; // a train prefers to carry on when turning round would not be quicker by this much
    const int ReplanEveryTicks = 20;
    const float RerouteAfterSeconds = 1.5f;
    const int DeadlockCheckEveryTicks = 40;

    int[] _wagonCargo = Array.Empty<int>();
    long _lastDeadlockKey;

    void ResolveWagonCargo()
    {
        _wagonCargo = new int[_railTuning.Wagons.Count];
        for (int w = 0; w < _wagonCargo.Length; w++)
        {
            string id = _railTuning.Wagons[w].Cargo;
            _wagonCargo[w] = id == "freight" ? FreightAny : -1;
            for (int c = 0; c < _cargo.Count; c++)
                if (_cargo[c].Id == id) _wagonCargo[w] = c;
            if (_wagonCargo[w] == -1) throw new ArgumentException($"Wagon '{_railTuning.Wagons[w].Id}' carries unknown cargo '{id}'.");
        }
    }

    static int StartNodeOf(RailNetwork rail, RouteStep s) { var t = rail.GetTrack(s.Track); return s.Forward ? t.From : t.To; }
    static int EndNodeOf(RailNetwork rail, RouteStep s) { var t = rail.GetTrack(s.Track); return s.Forward ? t.To : t.From; }

    // ---- The step ---------------------------------------------------------------------------------------------------

    /// <summary>Moves every train by one step and then looks for deadlocks.</summary>
    void StepTrains()
    {
        if (_trains.Count == 0) return;
        for (int k = 0; k < _trains.Count; k++)
        {
            var train = _trains[k];
            if (train.Active) StepTrain(train);
        }
        if (Tick % DeadlockCheckEveryTicks == 0) FindDeadlocks();
    }

    void StepTrain(Train t)
    {
        var mv = _railTuning.Movement;
        if (t.Servicing)
        {
            t.Status = TrainStatus.Servicing;
            return;
        }
        if (t.InDepot)
        {
            if (_nodeOwner[t.SpawnNode] != -1) { t.Status = TrainStatus.InDepot; return; }
            AddHold(t, isNode: true, t.SpawnNode, 0f, 0f);
            t.InDepot = false;
            t.Status = TrainStatus.Running;
        }
        if (t.ReturnDepot >= 0 && !_railDepots[t.ReturnDepot].Active) t.ReturnDepot = -1; // its depot is gone: back to work on its line
        if (t.Dwell > 0)
        {
            t.Status = TrainStatus.AtStation;
            if (--t.Dwell == 0) DepartTrain(t);
            return;
        }
        if (t.HaltSeconds > 0f)
        {
            t.HaltSeconds -= StepSeconds;
            t.Status = TrainStatus.Halted;
            t.Speed = 0f;
            t.Acceleration = 0f;
            if (t.HaltSeconds <= 0f && t.Broken) TowTrain(t);
            return;
        }
        if (t.ReturnDepot < 0 && !_stations[_lines[t.Line].Stops[t.StopIndex]].Active && !float.IsPositiveInfinity(t.StopAt)) CutRouteAfterApproved(t);
        if (float.IsPositiveInfinity(t.StopAt) && Tick >= t.ReplanTick)
        {
            if (PlanLeg(t)) t.Status = TrainStatus.Running;
            else
            {
                t.Status = TrainStatus.NoRoute;
                t.ReplanTick = Tick + ReplanEveryTicks;
            }
        }

        ExtendAuthority(t);
        Drive(t);
        ReleaseBehind(t);

        if (!float.IsPositiveInfinity(t.StopAt) && t.Head >= t.StopAt - 0.3f && t.Speed < 0.4f)
        {
            t.Head = t.StopAt;
            t.Speed = 0f;
            t.Acceleration = 0f;
            BeginTrainStop(t);
        }
    }

    // ---- Asking for track -------------------------------------------------------------------------------------------

    float BrakingDistance(float speed)
    {
        var mv = _railTuning.Movement;
        return speed * speed / (2f * mv.ServiceBrake * mv.BrakePlanShare);
    }

    /// <summary>
    /// Asks for the track beyond the end of what the train may use (a boundary node: a signal, station or depot) once the front is within
    /// braking distance plus <c>reserveLookahead</c> of it. A block signal needs every track up to the next boundary free, whichever
    /// way the train goes; a path signal only the tracks on its route. The first thing it finds held by another train is why it waits.
    /// </summary>
    void ExtendAuthority(Train t)
    {
        float lookahead = BrakingDistance(t.Speed) + _railTuning.Movement.ReserveLookahead;
        bool waiting = false;
        for (int guard = 0; guard < 6; guard++)
        {
            if (t.Approved >= t.EndOfRoute - 1e-3f) break;
            if (t.Approved - t.Head > lookahead) break;
            int owner = TryReserveNext(t);
            if (owner == -1) { t.BlockedBy = -1; continue; }
            t.BlockedBy = owner;
            waiting = true;
            break;
        }
        if (waiting && t.Speed < 0.3f)
        {
            t.Status = TrainStatus.WaitingForTrack;
            t.WaitedSeconds += StepSeconds;
            if (t.WaitedSeconds >= RerouteAfterSeconds && Tick >= t.ReplanTick)
            {
                t.ReplanTick = Tick + ReplanEveryTicks;
                TryReroute(t);
            }
        }
        else
        {
            if (t.Status == TrainStatus.WaitingForTrack) t.Status = TrainStatus.Running;
            t.WaitedSeconds = 0f;
            if (!waiting) t.BlockedBy = -1;
        }
    }

    /// <summary>Reserves the stretch from the end of the approved track to the next boundary. Returns -1 when it is the train's, else the id of the train that holds a piece of it (-2 when the owner is not a train).</summary>
    int TryReserveNext(Train t)
    {
        int k = 0;
        while (k < t.Steps.Count && t.Steps[k].Start < t.Approved - 1e-3f) k++;
        if (k >= t.Steps.Count) return -1;

        int startNode = StartNodeOf(_rail, t.Steps[k]);
        var arrival = k > 0 ? t.Steps[k - 1].Track : TrackId.None;
        bool block = false;
        RailSignal? passing = null;
        if (!arrival.IsNone && IsBoundary(startNode))
        {
            passing = _rail.SignalFor(arrival, startNode);
            block = passing == null || passing.Kind == SignalKind.Block;
            if (passing != null && t.RolledSignal != passing.Id.Value)
            {
                t.RolledSignal = passing.Id.Value; // once for each approach, however long the train has to wait
                MaybeFailSignal(passing);
            }
            if (passing is { Failed: true }) return -2; // a failed signal shows red to everybody
        }

        // The route up to the next boundary.
        // The region does not include the boundary node where it ends: a train waiting at a signal has not entered it, and the other trains
        // may use the node. The one exception is the node of the station the train is stopping at: it has to stand on it.
        var items = new List<Hold>();
        int m = k;
        for (; m < t.Steps.Count; m++)
        {
            var s = t.Steps[m];
            int end = EndNodeOf(_rail, s);
            items.Add(new Hold { Id = s.Track.Value, Lo = s.Start, Hi = s.End });
            bool stopHere = m == t.Steps.Count - 1 && !float.IsPositiveInfinity(t.StopAt) && s.End >= t.StopAt - 1e-3f;
            if (!IsBoundary(end) || stopHere) items.Add(new Hold { IsNode = true, Id = end, Lo = s.End, Hi = s.End });
            if (IsBoundary(end)) break;
        }
        if (m >= t.Steps.Count) m = t.Steps.Count - 1;
        float newApproved = t.Steps[m].End;
        items.Add(new Hold { IsNode = true, Id = startNode, Lo = t.Approved, Hi = t.Approved });

        if (block)
        {
            // Every track reachable from the node without passing another boundary, the route's or not.
            var seen = new HashSet<int> { startNode };
            var stack = new Stack<int>();
            stack.Push(startNode);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                foreach (var id in _rail.TracksAt(node))
                {
                    if (node == startNode && id == arrival) continue;
                    var track = _rail.GetTrack(id);
                    int far = track.Other(node);
                    items.Add(new Hold { Id = id.Value, Lo = t.Approved, Hi = newApproved });
                    if (!IsBoundary(far)) items.Add(new Hold { IsNode = true, Id = far, Lo = t.Approved, Hi = newApproved });
                    if (seen.Add(far) && !IsBoundary(far)) stack.Push(far);
                }
            }
        }

        foreach (var item in items)
        {
            int owner = item.IsNode ? _nodeOwner[item.Id] : _trackOwner[item.Id];
            if (owner != -1 && owner != t.Id) return owner;
        }
        foreach (var item in items) AddHold(t, item.IsNode, item.Id, item.Lo, item.Hi);
        t.Approved = newApproved;
        if (passing != null)
        {
            t.RolledSignal = -1;
            SignalPassed(passing);
        }
        return -1;
    }

    /// <summary>
    /// A train held up by another that holds track on its route looks for another way to its station that does not use any track held by
    /// a train: it keeps what it has been given and changes the rest of its route. Passing loops depend on this.
    /// </summary>
    void TryReroute(Train t)
    {
        if (float.IsPositiveInfinity(t.StopAt) || t.Approved >= t.EndOfRoute - 1e-3f) return;
        int k = 0;
        while (k < t.Steps.Count && t.Steps[k].Start < t.Approved - 1e-3f) k++;
        if (k >= t.Steps.Count) return;
        int node = StartNodeOf(_rail, t.Steps[k]);
        var arrival = k > 0 ? t.Steps[k - 1].Track : TrackId.None;
        int targetNode = TargetNodeOf(t);
        float vmax = _railTuning.Locomotives[t.Locomotive].MaxSpeed;
        bool Held(TrackId id)
        {
            int owner = _trackOwner[id.Value];
            return owner != -1 && owner != t.Id;
        }
        if (!FindRailPath(LeavingSteps(node, arrival), targetNode, vmax, out var path, out _, Held)) return;
        TruncateRoute(t, k);
        AppendPath(t, path);
    }

    // ---- Moving -----------------------------------------------------------------------------------------------------

    void Drive(Train t)
    {
        var mv = _railTuning.Movement;
        var loco = _railTuning.Locomotives[t.Locomotive];
        float dt = StepSeconds;
        float v = t.Speed;
        float brake = mv.ServiceBrake * mv.BrakePlanShare;

        float vt = loco.MaxSpeed;

        // The end of what the train may use, and the stop it makes there.
        float authority = MathF.Min(t.Approved, t.EndOfRoute);
        bool atStation = !float.IsPositiveInfinity(t.StopAt) && authority >= t.StopAt - 1e-3f;
        float stopPoint = atStation ? t.StopAt : authority - mv.StopMargin;
        float hardStop = authority;
        float distance = stopPoint - t.Head;
        vt = MathF.Min(vt, distance <= 0f ? 0f : MathF.Sqrt(2f * brake * distance));
        if (_crossings.Count > 0)
        {
            // A crossing whose barriers are not down yet is passed only if the train cannot stop before it any more; one blocked by a vehicle or a wreck, never.
            CrossingStops(t, out float emergencyStop, out float blockedStop);
            if (blockedStop < float.PositiveInfinity)
            {
                float d = blockedStop - mv.StopMargin - t.Head;
                vt = MathF.Min(vt, d <= 0f ? 0f : MathF.Sqrt(2f * brake * d));
                // A train that can still stop short of it is not let through by a late brake; one that cannot goes on, and hits what is there.
                if (v * v <= 2f * mv.EmergencyBrake * MathF.Max(0f, blockedStop - 0.5f - t.Head + 1.5f)) hardStop = MathF.Min(hardStop, blockedStop - 0.5f);
            }
            if (emergencyStop < float.PositiveInfinity)
            {
                float d = emergencyStop - t.Head;
                vt = MathF.Min(vt, d <= 0f ? 0f : MathF.Sqrt(2f * mv.EmergencyBrake * d));
            }
        }

        // Limits along the train and ahead: each piece of track and junction holds the whole train to its limit while any of it is there.
        float rear = t.Head - t.Length;
        float horizon = t.Head + mv.Horizon;
        float grade = 0f;
        for (int k = 0; k < t.Steps.Count; k++)
        {
            var s = t.Steps[k];
            if (s.End <= rear) continue;
            if (s.Start > horizon) break;
            var track = _rail.GetTrack(s.Track);
            vt = MathF.Min(vt, LimitAt(track.SpeedLimit, s.Start, t.Head, brake));
            if (t.Head >= s.Start && t.Head <= s.End) grade = s.Forward ? track.Grade : -track.Grade;
            if (k + 1 < t.Steps.Count)
            {
                var n = t.Steps[k + 1];
                int node = EndNodeOf(_rail, s);
                float angle = _rail.TurnAngle(s.Track, node, n.Track);
                float turn = _rail.TurnSpeed(angle);
                if (!float.IsPositiveInfinity(turn)) vt = MathF.Min(vt, LimitAt(turn, s.End, t.Head, brake));
            }
        }

        float mass = MassOf(t);
        float resistance = mass * mv.Gravity * (mv.RollingResistance + grade) + mv.AirResistance * v * v;
        float command = mv.ControlGain * (vt - v);
        float a;
        if (command >= 0f)
        {
            float needed = command * mass + resistance;
            float available = v >= loco.MaxSpeed ? 0f : MathF.Min(loco.TractiveEffort, loco.Power / MathF.Max(v, 1f));
            a = (MathF.Min(needed, available) - resistance) / mass;
        }
        else
        {
            float limit = v - vt > 3f ? mv.EmergencyBrake : mv.ServiceBrake;
            a = MathF.Max(command, -limit);
        }

        float v1 = MathF.Max(0f, v + a * dt);
        float head = t.Head + 0.5f * (v + v1) * dt;
        if (head >= hardStop)
        {
            head = hardStop;
            v1 = 0f;
            a = 0f;
        }
        t.Head = head;
        t.Speed = v1;
        t.Acceleration = a;
    }

    /// <summary>The speed allowed now because of a limit that applies from route coordinate <paramref name="from"/> for <paramref name="span"/> metres (the whole train while it is on it).</summary>
    static float LimitAt(float limit, float from, float head, float brake)
    {
        if (head >= from) return limit;
        float d = from - head;
        return MathF.Sqrt(limit * limit + 2f * brake * d);
    }

    /// <summary>Lets go of what the rear has passed, and drops route that is wholly behind it.</summary>
    void ReleaseBehind(Train t)
    {
        float rear = t.Head - t.Length;
        for (int h = t.Holds.Count - 1; h >= 0; h--)
            if (rear >= t.Holds[h].Hi + 1e-3f) ReleaseHold(t, h);
        while (t.Steps.Count > 1 && t.Steps[0].End < rear - 1f)
        {
            float shift = t.Steps[0].End;
            t.Steps.RemoveAt(0);
            for (int i = 0; i < t.Steps.Count; i++)
            {
                var s = t.Steps[i];
                s.Start -= shift;
                t.Steps[i] = s;
            }
            t.Head -= shift;
            t.Approved -= shift;
            if (!float.IsPositiveInfinity(t.StopAt)) t.StopAt -= shift;
            for (int h = 0; h < t.Holds.Count; h++)
            {
                var hold = t.Holds[h];
                hold.Lo -= shift;
                hold.Hi -= shift;
                t.Holds[h] = hold;
            }
        }
    }

    // ---- Stations ---------------------------------------------------------------------------------------------------

    void BeginTrainStop(Train t)
    {
        if (t.ReturnDepot >= 0)
        {
            if (t.SvcKind >= 0) EnterTrainService(t);
            else ParkTrainAtDepot(t);
            return;
        }
        var line = _lines[t.Line];
        var station = _stations[line.Stops[t.StopIndex]];
        var wagon = _railTuning.Wagons[t.Wagon];
        if (IsShut(station))
        {
            // A raid has shut the station: the train stands the time of a stop and goes on with what it carries.
            t.Dwell = (int)Math.Max(1, Ticks(wagon.DwellSeconds));
            t.Status = TrainStatus.AtStation;
            return;
        }
        int fixedCargo = _wagonCargo[t.Wagon];
        bool passengers = Settled && _passengerCargo >= 0 && fixedCargo == _passengerCargo;
        int unloaded = 0, loaded = 0;
        // Due for a repair that a rail depot can do: it unloads what this stop takes, puts the rest down here and loads nothing.
        bool service = DecideTrainService(t);
        if (passengers)
        {
            unloaded = AlightRiders(t.Riders, line, station, out long income);
            t.Income += income;
            SetTrainRiderLoad(t);
            if (service)
            {
                if (t.Riders != null)
                    foreach (var lot in t.Riders) GiveUp(_groups[lot.Group], lot.Count, lot.From);
                t.Riders = null;
                SetTrainRiderLoad(t);
            }
            else if (station.Queue.Count > 0)
            {
                int room = t.Capacity - t.Load;
                if (room > 0)
                {
                    t.Riders ??= new List<PaxLot>();
                    loaded = BoardInto(t.Riders, line, station, room);
                    SetTrainRiderLoad(t);
                }
            }
        }
        else if (t.Load > 0 && Accepts(station, t.LoadCargo)) unloaded = DeliverTrain(t, station);
        if (service && t.Load > 0)
        {
            t.Load = 0;
            t.LoadCargo = -1;
            t.LoadFrom = -1;
        }

        if (!service && t.Load == 0 && !passengers)
        {
            int cargo = CargoToLoadFor(fixedCargo, line, t.StopIndex, station);
            if (cargo >= 0)
            {
                loaded = Math.Min(t.Capacity, (int)MathF.Floor(station.Waiting[cargo]));
                station.Waiting[cargo] -= loaded;
                t.Load = loaded;
                t.LoadCargo = cargo;
                t.LoadFrom = station.Id;
                t.LoadTick = Tick;
            }
        }
        t.Dwell = (int)Math.Max(1, Ticks(wagon.DwellSeconds + wagon.SecondsPerUnit * (unloaded + loaded)));
        t.Status = TrainStatus.AtStation;
    }

    void SetTrainRiderLoad(Train t)
    {
        int total = 0;
        if (t.Riders != null)
            foreach (var lot in t.Riders) total += lot.Count;
        t.Load = total;
        t.LoadCargo = total > 0 ? _passengerCargo : -1;
    }

    int DeliverTrain(Train t, Station station)
    {
        int cargo = t.LoadCargo;
        int onBoard = t.Load;
        int taken = Settled && cargo != _passengerCargo ? Distribute(station, cargo, onBoard) : onBoard;
        t.Income += PayFor(_lines[t.Line], cargo, taken, _stations[t.LoadFrom], station, t.LoadTick);
        t.Load = 0;
        t.LoadCargo = -1;
        t.LoadFrom = -1;
        return onBoard;
    }

    /// <summary>Loading is done: go on to the next open station of the line.</summary>
    void DepartTrain(Train t)
    {
        var line = _lines[t.Line];
        for (int tried = 0; tried < line.Stops.Count; tried++)
        {
            t.StopIndex = (t.StopIndex + 1) % line.Stops.Count;
            if (_stations[line.Stops[t.StopIndex]].Active) break;
        }
        t.StopAt = float.PositiveInfinity;
        t.ReplanTick = 0;
        if (t.SvcKind >= 0) StartTrainServiceTrip(t);
        if (PlanLeg(t)) t.Status = TrainStatus.Running;
        else
        {
            t.Status = TrainStatus.NoRoute;
            t.ReplanTick = Tick + ReplanEveryTicks;
        }
    }

    // ---- Planning a leg ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds a way from where the route ends (or, if turning round is quicker or the only way, from the back of the train) to the station
    /// the train is heading for, and adds it to the route. Trains have a cab at each end, so one standing still with all of it on the route
    /// can reverse. False when there is none.
    /// </summary>
    bool PlanLeg(Train t) => PlanLeg(t, apply: true, out _);

    /// <summary>The rail node a train is heading for: its depot when it is going back to park, else the station it is heading for. -1 when that is closed.</summary>
    int TargetNodeOf(Train t)
    {
        if (t.ReturnDepot >= 0) return _railDepots[t.ReturnDepot].Active ? _railDepots[t.ReturnDepot].Node : -1;
        var line = _lines[t.Line];
        for (int tried = 0; tried < line.Stops.Count && !_stations[line.Stops[t.StopIndex]].Active; tried++)
            t.StopIndex = (t.StopIndex + 1) % line.Stops.Count;
        var target = _stations[line.Stops[t.StopIndex]];
        return target.Active ? target.RailNode : -1;
    }

    /// <summary>
    /// <see cref="PlanLeg(Train)"/>, or with <paramref name="apply"/> false only whether there is a way and how long it would take; <paramref name="willStop"/> counts a train still under way as able to turn round once it has stopped at the end of its route.
    /// </summary>
    bool PlanLeg(Train t, bool apply, out float seconds, bool willStop = false)
    {
        seconds = 0f;
        int targetNode = TargetNodeOf(t);
        if (targetNode < 0) return false;
        float vmax = _railTuning.Locomotives[t.Locomotive].MaxSpeed;
        if (t.Steps.Count == 0)
        {
            // Standing in a depot: leave by any track toward the station.
            if (t.SpawnNode == targetNode || !FindRailPath(LeavingSteps(t.SpawnNode, TrackId.None), targetNode, vmax, out var leaving, out seconds)) return false;
            if (apply) AppendPath(t, leaving);
            return true;
        }

        var last = t.Steps[^1];
        int endNode = EndNodeOf(_rail, last);
        if (endNode == targetNode)
        {
            // Already where the leg ends (the route was cut short exactly there).
            if (apply) t.StopAt = t.EndOfRoute;
            return true;
        }
        bool forward = FindRailPath(LeavingSteps(endNode, last.Track), targetNode, vmax, out var forwardPath, out float forwardSeconds);
        if (!forward) forwardSeconds = float.PositiveInfinity;

        bool canReverse = (willStop || (t.Speed < 0.5f && t.Head >= t.Steps[^1].End - 1f)) && t.Head - t.Length >= t.Steps[0].Start - 0.01f;
        List<RouteStep>? reversePath = null;
        float reverseSeconds = float.PositiveInfinity;
        if (canReverse)
        {
            int kr = RearStep(t);
            var first = t.Steps[kr];
            int turnNode = StartNodeOf(_rail, first);
            if (turnNode == targetNode)
            {
                // Turning round is all it takes: the station is at the other end of the track it stands on.
                reversePath = new List<RouteStep>();
                reverseSeconds = ReversePenaltySeconds;
            }
            else if (FindRailPath(LeavingSteps(turnNode, first.Track), targetNode, vmax, out var rp, out float rs)) { reversePath = rp; reverseSeconds = rs + ReversePenaltySeconds; }
        }

        if (!forward && reversePath == null) return false;
        if (reversePath != null && (!forward || reverseSeconds < forwardSeconds))
        {
            seconds = reverseSeconds;
            if (apply)
            {
                ReverseRoute(t);
                AppendPath(t, reversePath);
            }
        }
        else
        {
            seconds = forwardSeconds;
            if (apply) AppendPath(t, forwardPath);
        }
        return true;
    }

    /// <summary>The index of the step that holds the rear of the train.</summary>
    int RearStep(Train t)
    {
        float rear = t.Head - t.Length;
        int k = 0;
        while (k < t.Steps.Count - 1 && t.Steps[k].End <= rear + 1e-3f) k++;
        return k;
    }

    void AppendPath(Train t, List<RouteStep> path)
    {
        float start = t.EndOfRoute;
        foreach (var step in path)
        {
            var s = step;
            s.Start = start;
            start += s.Length;
            t.Steps.Add(s);
        }
        t.StopAt = start;
    }

    /// <summary>Turns the train round in place: its route becomes the stretch it stands on, travelled the other way, and the front becomes the back.</summary>
    void ReverseRoute(Train t)
    {
        float reference = t.Head;
        int kr = RearStep(t);
        var flipped = new List<RouteStep>();
        for (int j = t.Steps.Count - 1; j >= kr; j--)
        {
            var s = t.Steps[j];
            flipped.Add(new RouteStep { Track = s.Track, Forward = !s.Forward, Length = s.Length, Start = reference - s.End });
        }
        // Coordinates start at zero again; a step cut by the old head would be cut at its start, which is where the front was.
        float shift = flipped[0].Start;
        for (int i = 0; i < flipped.Count; i++)
        {
            var s = flipped[i];
            s.Start -= shift;
            flipped[i] = s;
        }
        t.Steps.Clear();
        t.Steps.AddRange(flipped);
        t.Head = reference - (t.Head - t.Length) - shift;
        for (int h = t.Holds.Count - 1; h >= 0; h--)
        {
            var hold = t.Holds[h];
            float lo = reference - hold.Hi - shift, hi = reference - hold.Lo - shift;
            if (hi < -0.01f) { ReleaseHold(t, h); continue; }
            hold.Lo = lo;
            hold.Hi = hi;
            t.Holds[h] = hold;
        }
        t.Approved = t.EndOfRoute;
        t.StopAt = float.PositiveInfinity;
    }

    /// <summary>Drops the part of the route the train has not been given yet, so that it plans again from the end of what it may use.</summary>
    void CutRouteAfterApproved(Train t)
    {
        int k = 0;
        while (k < t.Steps.Count && t.Steps[k].Start < t.Approved - 1e-3f) k++;
        if (k < t.Steps.Count) TruncateRoute(t, k);
        t.StopAt = float.PositiveInfinity;
        t.ReplanTick = 0;
    }

    /// <summary>A rail line was edited: trains keep heading for their station when it is still on the line, else for the stop at the same place, and plan again.</summary>
    void EditTrainsOfLine(int line, IReadOnlyList<int> before, int[] stations)
    {
        foreach (var t in _trains)
        {
            // A train on its way to a depot for a repair comes back to the line, so it keeps its stop in step too (its route to the depot stays).
            if (!t.Active || t.Line != line || (t.ReturnDepot >= 0 && t.SvcKind < 0)) continue;
            int heading = before[Math.Min(t.StopIndex, before.Count - 1)];
            int at = Array.IndexOf(stations, heading);
            t.StopIndex = at >= 0 ? at : Math.Min(t.StopIndex, stations.Length - 1);
            if (at < 0 && t.Dwell == 0 && t.ReturnDepot < 0) CutRouteAfterApproved(t);
        }
    }

    /// <summary>Whether roads rebuilt by moving a node would cross a track at grade (not allowed: crossings are laid out when roads and tracks are built).</summary>
    bool MovedRoadsMeetTracks(IReadOnlyList<(RoadGeometry Geometry, RoadType Type)> roads)
    {
        for (int t = 0; t < _rail.TrackCount; t++)
        {
            var track = _rail.GetTrack(new TrackId(t));
            if (track.Retired) continue;
            foreach (var (geometry, _) in roads)
                foreach (var (_, along, _, _) in Intersections(geometry, track.Geometry))
                    return true;
        }
        return false;
    }

    // ---- Deadlocks --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Trains that have waited a long time for track form a graph: each waits for the train that holds what it needs. A ring in it can
    /// never resolve by itself; it is recorded once as a warning and its trains are marked.
    /// </summary>
    void FindDeadlocks()
    {
        float limit = _railTuning.Movement.BlockedSeconds;
        foreach (var t in _trains) t.Deadlocked = false;
        long key = 0;
        var reported = new HashSet<int>();
        foreach (var start in _trains)
        {
            if (!start.Active || reported.Contains(start.Id) || start.Status != TrainStatus.WaitingForTrack || start.WaitedSeconds < limit) continue;
            var path = new List<int>();
            var onPath = new Dictionary<int, int>();
            var t = start;
            while (t != null && t.Active && t.Status == TrainStatus.WaitingForTrack && t.WaitedSeconds >= limit && !onPath.ContainsKey(t.Id))
            {
                onPath[t.Id] = path.Count;
                path.Add(t.Id);
                t = t.BlockedBy >= 0 && t.BlockedBy < _trains.Count ? _trains[t.BlockedBy] : null;
            }
            if (t == null || !onPath.TryGetValue(t.Id, out int from)) continue;
            var ring = path.GetRange(from, path.Count - from);
            ring.Sort();
            foreach (int id in ring)
            {
                _trains[id].Deadlocked = true;
                reported.Add(id);
                key = key * 1000003 + id + 1;
            }
            if (!_deadlockKnown.Contains(RingKey(ring)))
            {
                _deadlockKnown.Add(RingKey(ring));
                _railWarnings.Add(new RailWarning(Tick, "deadlock", ring));
            }
        }
        _lastDeadlockKey = key;
    }

    readonly HashSet<string> _deadlockKnown = new();
    static string RingKey(List<int> ring) => string.Join(",", ring);

    /// <summary>Trains that are in a deadlock now.</summary>
    public IReadOnlyList<int> DeadlockedTrains => _trains.Where(t => t.Active && t.Deadlocked).Select(t => t.Id).ToList();

    // ---- Accounts ---------------------------------------------------------------------------------------------------

    void ChargeTrainUpkeep()
    {
        foreach (var t in _trains)
        {
            if (!t.Active) continue;
            var loco = _railTuning.Locomotives[t.Locomotive];
            var wagon = _railTuning.Wagons[t.Wagon];
            long upkeep = Price(loco.UpkeepPerDay + t.Wagons * wagon.UpkeepPerDay);
            Charge(upkeep, LedgerEntry.Upkeep);
            _lines[t.Line].Upkeep += upkeep;
            t.UpkeepPaid += upkeep;
        }
    }

    // ---- Queries ----------------------------------------------------------------------------------------------------

    /// <summary>The id of the station a train is heading for or standing at.</summary>
    public int TargetStationOf(Train train) => _lines[train.Line].Stops[train.StopIndex];

    /// <summary>The people on board a train, by the station they are going to.</summary>
    public IReadOnlyDictionary<int, int> RidersOf(Train train)
    {
        var by = new SortedDictionary<int, int>();
        if (train.Riders is { } riders)
            foreach (var lot in riders) by[lot.Dest] = by.GetValueOrDefault(lot.Dest) + lot.Count;
        return by;
    }

    /// <summary>
    /// Whether a signal shows clear: the track beyond it is not held by any other train (for a block signal the whole block; for a path signal
    /// the block too, as there is no particular train to ask). For drawing.
    /// </summary>
    public bool IsSignalClear(RailSignal signal)
    {
        if (!signal.Active) return true;
        if (signal.Failed) return false;
        SyncRail();
        var track = _rail.GetTrack(signal.Track);
        int startNode = signal.Node;
        var seen = new HashSet<int> { startNode };
        var stack = new Stack<int>();
        stack.Push(startNode);
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            foreach (var id in _rail.TracksAt(node))
            {
                if (node == startNode && id == signal.Track) continue;
                var t = _rail.GetTrack(id);
                int far = t.Other(node);
                int owner = _trackOwner[id.Value];
                if (owner != -1) return false;
                if (seen.Add(far) && !IsBoundary(far)) stack.Push(far);
            }
        }
        return true;
    }

    /// <summary>The train that holds a track at the moment, -1 when it is free.</summary>
    public int TrackHeldBy(TrackId track) => track.Value < _trackOwner.Length ? _trackOwner[track.Value] : -1;
}
