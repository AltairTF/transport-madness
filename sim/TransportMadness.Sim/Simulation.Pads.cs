namespace TransportMadness.Sim;

/// <summary>
/// The pad under a depot or a battery (M6g): the ground levelled to one height. <see cref="Height"/> is where the floor stands, <see cref="Cut"/> and <see cref="Fill"/>
/// the earth moved in cubic metres, <see cref="MaxCut"/> and <see cref="MaxFill"/> the largest drop and rise in metres, <see cref="Money"/> what the work costs.
/// <see cref="Reason"/> is empty when the pad can be made.
/// </summary>
public readonly record struct PadPlan(string Reason, float Height, float Cut, float Fill, float MaxCut, float MaxFill, long Money)
{
    public bool Ok => string.IsNullOrEmpty(Reason);
}

public sealed partial class Simulation
{
    /// <summary>Half the width of a road that a pad leaves alone: the ground under a road is the road's, and a pad does not move it.</summary>
    const float PadRoadClearance = 9f;

    /// <summary>How far one step of the player's keys raises or lowers a pad, in metres.</summary>
    public float PadRaiseStep => _accessTuning.PadRaiseStep;

    /// <summary>
    /// The pad for a square of half-side <paramref name="half"/> at (x, z) turned by <paramref name="angle"/>: at <paramref name="baseHeight"/> (the mean ground under it when NaN)
    /// plus <paramref name="raise"/>. Refused in water, when it would cut or fill more than <c>access.padMaxChange</c> or when the floor would be at the water.
    /// </summary>
    public PadPlan PlanPad(float x, float z, float angle, float half, float baseHeight, float raise)
    {
        if (_net.Terrain is not { } terrain) return new PadPlan("", 0f, 0f, 0f, 0f, 0f, 0);
        float height = (float.IsNaN(baseHeight) ? terrain.MeanHeight(x, z, angle, half) : baseHeight) + raise;
        var change = terrain.LevelPad(x, z, angle, half, height, _accessTuning.PadShoulder, false, SkipRoadSample);
        var roads = _net.Tuning.Roads;
        long money = (long)MathF.Round((change.Cut * roads.CutCostPerCubicMetre + change.Fill * roads.FillCostPerCubicMetre) * _accessTuning.PadCostShare);
        string reason = "";
        if (height < terrain.WaterSurfaceAt(x, z) + 0.2f) reason = "the floor would be under water";
        else if (change.MaxCut > _accessTuning.PadMaxChange) reason = $"it would cut {change.MaxCut:0.#} m into the ground (at most {_accessTuning.PadMaxChange:0.#} m)";
        else if (change.MaxFill > _accessTuning.PadMaxChange) reason = $"it would fill {change.MaxFill:0.#} m (at most {_accessTuning.PadMaxChange:0.#} m)";
        return new PadPlan(reason, height, change.Cut, change.Fill, change.MaxCut, change.MaxFill, money);
    }

    bool SkipRoadSample(float x, float z) => _net.RoadAt(x, z, PadRoadClearance) != null;

    /// <summary>Levels the ground for a pad that <see cref="PlanPad"/> approved (call it after the roads of the building are made, so they are left alone).</summary>
    void LevelPad(float x, float z, float angle, float half, PadPlan pad) =>
        _net.Terrain?.LevelPad(x, z, angle, half, pad.Height, _accessTuning.PadShoulder, true, SkipRoadSample);
}
