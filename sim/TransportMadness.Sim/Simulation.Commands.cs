namespace TransportMadness.Sim;

/// <summary>Player actions, their log and replay (ADR 0003: same seed and same actions give the same run).</summary>
public sealed partial class Simulation
{
    const float CrashSpeedThreshold = 4f; // CrashSomeone only picks cars moving faster than this

    readonly List<LoggedCommand> _commandLog = new();

    /// <summary>Every command executed so far, in order, each with the tick it ran at.</summary>
    public IReadOnlyList<LoggedCommand> CommandLog => _commandLog;

    /// <summary>
    /// Applies a player action now (between steps) and records it in <see cref="CommandLog"/>. Refused commands are
    /// recorded too: they change nothing, and the replay refuses them again. Player actions must come through here;
    /// changing <see cref="Network"/> or the simulation directly leaves no trace and cannot be replayed.
    /// </summary>
    public int Execute(SimCommand command)
    {
        _commandLog.Add(new LoggedCommand(Tick, command));
        switch (command)
        {
            case BuildRoadCommand c:
                return _net.BuildRoad(c.X0, c.Z0, c.X1, c.Z1, c.Type, c.SpeedLimit, c.Sweep).Count;
            case RemoveRoadCommand c:
                return _net.RemoveRoad(new RoadId(c.Road)) ? 1 : 0;
            case UpgradeRoadCommand c:
                return _net.UpgradeRoad(new RoadId(c.Road), c.SpeedLimit) == null ? 0 : 1;
            case DowngradeRoadCommand c:
                return _net.DowngradeRoad(new RoadId(c.Road), c.SpeedLimit) == null ? 0 : 1;
            case SetSpeedLimitCommand c:
                return _net.SetSpeedLimit(new RoadId(c.Road), c.SpeedLimit) == null ? 0 : 1;
            case MoveNodeCommand c:
                return c.Node >= 0 && c.Node < _net.NodeCount && _net.MoveNode(c.Node, c.X, c.Z) ? 1 : 0;
            case AddDepotCommand c:
                if (c.Node < 0 || c.Node >= _net.NodeCount || _net.RoadsAt(c.Node).Count == 0) return -1;
                foreach (var depot in _depots)
                    if (depot.Active && depot.Node == c.Node) return -1;
                return AddDepot(c.Node, c.Level);
            case SeedTrafficCommand c:
                return SeedTraffic(c.Count);
            case CrashSomeoneCommand:
                return CrashSomeone();
            default:
                throw new ArgumentException($"Unknown command {command.GetType().Name}", nameof(command));
        }
    }

    /// <summary>Wrecks the first moving car found scanning from a slot chosen by the current tick.</summary>
    int CrashSomeone()
    {
        int n = _count;
        for (int k = 0; k < n; k++)
        {
            int i = (int)((Tick + k) % n);
            if (_kind[i] == VehicleKind.Car && _speed[i] > CrashSpeedThreshold)
            {
                Crash(i);
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Runs this (fresh) simulation up to <paramref name="untilTick"/>, executing the logged commands at the ticks they
    /// were logged at. The simulation must have been set up like the one that produced the log: same initial network and
    /// seed. Commands stamped exactly <paramref name="untilTick"/> are applied too, so the end state matches.
    /// </summary>
    public void Replay(IReadOnlyList<LoggedCommand> log, long untilTick)
    {
        int next = 0;
        while (true)
        {
            while (next < log.Count && log[next].Tick == Tick) Execute(log[next++].Command);
            if (Tick >= untilTick) break;
            if (next < log.Count && log[next].Tick < Tick)
                throw new ArgumentException($"Command log is out of order or starts before tick {Tick}", nameof(log));
            Step();
        }
    }
}
