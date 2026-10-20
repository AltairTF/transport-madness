namespace TransportMadness.Sim;

/// <summary>Traffic lights at a road junction.</summary>
public sealed class TrafficLight
{
    internal TrafficLight(int id, int node)
    {
        Id = id;
        Node = node;
    }

    public int Id { get; }
    public int Node { get; }
    /// <summary>False once taken down, or when the junction has lost roads and is none any more.</summary>
    public bool Active { get; internal set; } = true;
}

/// <summary>
/// Traffic lights: the approaches to a junction fall in two groups by the axis they come along, and the groups take turns at green, with
/// a short time of red for both between. A vehicle that can still stop stops at the edge of a junction when its light is red. There is
/// no state beyond the tick: which group has green follows from it, so the lights replay with the run.
/// </summary>
public sealed partial class Simulation
{
    readonly List<TrafficLight> _lights = new();
    int _lightCount;
    int _lightsSyncedFor = -1;
    byte[] _lanePhase = Array.Empty<byte>(); // per lane: 0 or 1 for a lane running into a junction with lights, 255 otherwise
    int[] _laneLightNode = Array.Empty<int>();

    public IReadOnlyList<TrafficLight> TrafficLights => _lights;
    public long TrafficLightPrice => _net.Tuning.Roads.TrafficLight.Price;

    TrafficLightTuning LightRules => _net.Tuning.Roads.TrafficLight;

    int BuildTrafficLight(int node)
    {
        if (node < 0 || node >= _net.NodeCount || _net.RoadsAt(node).Count < 3) return 0;
        foreach (var light in _lights)
            if (light.Active && light.Node == node) return 0;
        if (!TrySpend(LightRules.Price, LedgerEntry.Construction)) return 0;
        _lights.Add(new TrafficLight(_lights.Count, node));
        _lightCount++;
        _lightsSyncedFor = -1;
        return 1;
    }

    int RemoveTrafficLight(int node)
    {
        foreach (var light in _lights)
        {
            if (!light.Active || light.Node != node) continue;
            light.Active = false;
            _lightCount--;
            _lightsSyncedFor = -1;
            return 1;
        }
        return 0;
    }

    /// <summary>The active lights at a node, or null.</summary>
    public TrafficLight? LightAt(int node)
    {
        foreach (var light in _lights)
            if (light.Active && light.Node == node) return light;
        return null;
    }

    /// <summary>Works out which group each lane running into a lit junction belongs to. Done again when the roads or the lights change.</summary>
    void SyncLights()
    {
        int stamp = _net.Version * 31 + _lights.Count * 7 + _lightCount;
        if (stamp == _lightsSyncedFor && _lanePhase.Length == _net.LaneCount) return;
        _lightsSyncedFor = stamp;
        int lanes = _net.LaneCount;
        _lanePhase = new byte[lanes];
        _laneLightNode = new int[lanes];
        Array.Fill(_lanePhase, (byte)255);
        Array.Fill(_laneLightNode, -1);
        foreach (var light in _lights)
        {
            if (!light.Active) continue;
            var incoming = _net.IncomingLanes(light.Node);
            if (incoming.Count == 0 || _net.RoadsAt(light.Node).Count < 3) continue;
            // The axis of the first lane's approach, and every approach within 45 degrees of it (either way along it) goes with it.
            float axis = AxisOf(_net.GetLane(incoming[0]).AngleAtEnd);
            foreach (var laneId in incoming)
            {
                float d = MathF.Abs(AxisOf(_net.GetLane(laneId).AngleAtEnd) - axis);
                d = MathF.Min(d, MathF.PI - d);
                _lanePhase[laneId.Value] = (byte)(d < MathF.PI / 4f ? 0 : 1);
                _laneLightNode[laneId.Value] = light.Node;
            }
        }
    }

    static float AxisOf(float heading)
    {
        float a = heading % MathF.PI;
        return a < 0f ? a + MathF.PI : a;
    }

    /// <summary>Whether the group of a lane running into a junction with lights has green now (true for a lane at a junction without).</summary>
    public bool LaneHasGreen(LaneId lane)
    {
        SyncLights();
        return !LaneIsRedChecked(lane.Value);
    }

    bool LaneIsRed(int lane)
    {
        if (_lightsSyncedFor == -1 || _lanePhase.Length <= lane) SyncLights();
        return LaneIsRedChecked(lane);
    }

    bool LaneIsRedChecked(int lane)
    {
        if (lane >= _lanePhase.Length || _lanePhase[lane] == 255) return false;
        var rules = LightRules;
        long green = Ticks(rules.GreenSeconds), clear = Ticks(rules.ClearSeconds);
        long half = green + clear, cycle = half * 2;
        long m = (Tick + _laneLightNode[lane] * 37L) % cycle;
        long start = _lanePhase[lane] == 0 ? 0 : half;
        return !(m >= start && m < start + green);
    }

    ulong LightHash()
    {
        ulong h = (ulong)_lights.Count;
        foreach (var light in _lights) h = h * 31 + (ulong)light.Node + (light.Active ? 1UL : 0UL);
        return h;
    }
}
