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
            {
                var cost = _net.EstimateCost(c.X0, c.Z0, c.X1, c.Z1, c.Type, c.Sweep);
                if (cost == null) return 0;
                var inTheWay = BuildingsInTheWay(new[] { (RoadGeometry.FromChord(c.X0, c.Z0, c.X1, c.Z1, c.Sweep), c.Type) });
                if (!CanAfford(cost.Value.Money + DemolitionOf(inTheWay).Price)) return 0;
                int built = _net.BuildRoad(c.X0, c.Z0, c.X1, c.Z1, c.Type, c.SpeedLimit, c.Sweep).Count;
                if (built > 0)
                {
                    Charge(Price(cost.Value.Money), LedgerEntry.Construction);
                    DemolishForRoad(inTheWay);
                }
                return built;
            }
            case RemoveRoadCommand c:
                if (!IsLiveRoad(c.Road) || !CanAfford(_net.DemolitionCost(new RoadId(c.Road)))) return 0;
                return Paid(_net.DemolitionCost(new RoadId(c.Road)), _net.RemoveRoad(new RoadId(c.Road)));
            case UpgradeRoadCommand c:
            {
                if (!IsLiveRoad(c.Road) || _net.EstimateUpgrade(new RoadId(c.Road)) is not { } cost || !CanAfford(cost.Money)) return 0;
                return Paid(cost.Money, _net.UpgradeRoad(new RoadId(c.Road), c.SpeedLimit) != null);
            }
            case DowngradeRoadCommand c:
            {
                if (!IsLiveRoad(c.Road) || _net.DowngradeCost(new RoadId(c.Road)) is not { } cost || !CanAfford(cost)) return 0;
                return Paid(cost, _net.DowngradeRoad(new RoadId(c.Road), c.SpeedLimit) != null);
            }
            case SetSpeedLimitCommand c:
            {
                if (!IsLiveRoad(c.Road) || _net.SpeedLimitChangeCost(new RoadId(c.Road)) is not { } cost || !CanAfford(cost)) return 0;
                return Paid(cost, _net.SetSpeedLimit(new RoadId(c.Road), c.SpeedLimit) != null);
            }
            case MoveNodeCommand c:
            {
                if (c.Node < 0 || c.Node >= _net.NodeCount) return 0;
                if (_net.EstimateMoveNode(c.Node, c.X, c.Z) is not { } cost) return 0;
                var inTheWay = _net.PlannedMoveRoads(c.Node, c.X, c.Z) is { } rebuilt ? BuildingsInTheWay(rebuilt) : new List<int>();
                if (!CanAfford(cost.Money + DemolitionOf(inTheWay).Price)) return 0;
                if (!_net.MoveNode(c.Node, c.X, c.Z, out int moved)) return 0;
                Charge(Price(cost.Money), LedgerEntry.Construction);
                DemolishForRoad(inTheWay);
                _coverageDirty = true;
                foreach (var depot in _depots)
                    if (depot.Active && depot.Node == c.Node) depot.Node = moved; // the depot goes with its node
                foreach (var station in _stations)
                    if (station.Active && station.Node == c.Node) station.Node = moved; // and so does a station
                return 1;
            }
            case AddDepotCommand c:
                if (c.Node < 0 || c.Node >= _net.NodeCount || _net.RoadsAt(c.Node).Count == 0) return -1;
                foreach (var depot in _depots)
                    if (depot.Active && depot.Node == c.Node) return -1;
                if (!TrySpend(_economy.DepotPrice.For(c.Level), LedgerEntry.Depots)) return -1;
                return AddDepot(c.Node, c.Level);
            case BuildStationCommand c:
                return BuildStation(c.X, c.Z, c.Bays);
            case CreateLineCommand c:
                return CreateLine(c.Stations);
            case EditLineCommand c:
                return EditLine(c.Line, c.Stations);
            case RenameLineCommand c:
                return RenameLine(c.Line, c.Name);
            case SetLineColorCommand c:
                return SetLineColor(c.Line, c.Rgb);
            case RenameStationCommand c:
                return RenameStation(c.Station, c.Name);
            case CloseStationCommand c:
                return CloseStation(c.Station);
            case AddStationBaysCommand c:
                return AddStationBays(c.Station);
            case DeleteLineCommand c:
                return DeleteLine(c.Line);
            case BuyVehicleCommand c:
                return BuyVehicle(c.Depot, c.Model, c.Line);
            case SellVehicleCommand c:
                return SellVehicle(c.Vehicle);
            case TakeLoanCommand c:
                return TakeLoan(c.Amount);
            case RepayLoanCommand c:
                return RepayLoan(c.Amount);
            case SeedTrafficCommand c:
                return SeedTraffic(c.Count);
            case CrashSomeoneCommand:
                return CrashSomeone();
            default:
                throw new ArgumentException($"Unknown command {command.GetType().Name}", nameof(command));
        }
    }

    bool IsLiveRoad(int road) => road >= 0 && road < _net.RoadCount && !_net.GetRoad(new RoadId(road)).Retired;

    /// <summary>Charges the construction price when the work was done. Returns 1 or 0 for the command's result.</summary>
    int Paid(float cost, bool done)
    {
        if (!done) return 0;
        Charge(Price(cost), LedgerEntry.Construction);
        return 1;
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
