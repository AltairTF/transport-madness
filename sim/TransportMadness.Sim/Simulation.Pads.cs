namespace TransportMadness.Sim;

/// <summary>
/// The pad under a depot or a battery (M6g): the ground levelled to one height. <see cref="Height"/> is where the floor stands, <see cref="Cut"/> and <see cref="Fill"/>
/// the earth moved in cubic metres, <see cref="MaxCut"/> and <see cref="MaxFill"/> the largest drop and rise in metres, <see cref="Money"/> what the work costs.
/// <see cref="Reason"/> is empty when the pad can be made.
/// </summary>
public readonly record struct PadPlan(string Reason, float Height, float Cut, float Fill, float MaxCut, float MaxFill, long Money, float X = 0f, float Z = 0f, float Angle = 0f, float HalfAlong = 0f, float HalfAcross = 0f)
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
    public PadPlan PlanPad(float x, float z, float angle, float half, float baseHeight, float raise) => PlanPad(x, z, angle, half, half, baseHeight, raise);

    /// <summary>The same for a rectangle <paramref name="halfAlong"/> long in the direction <paramref name="angle"/> and <paramref name="halfAcross"/> across it.</summary>
    public PadPlan PlanPad(float x, float z, float angle, float halfAlong, float halfAcross, float baseHeight, float raise)
    {
        if (_net.Terrain is not { } terrain) return new PadPlan("", 0f, 0f, 0f, 0f, 0f, 0);
        float height = (float.IsNaN(baseHeight) ? terrain.MeanHeight(x, z, angle, halfAlong, halfAcross) : baseHeight) + raise;
        var change = terrain.LevelPad(x, z, angle, halfAlong, halfAcross, height, _accessTuning.PadShoulder, false, SkipRoadSample);
        var roads = _net.Tuning.Roads;
        long money = (long)MathF.Round((change.Cut * roads.CutCostPerCubicMetre + change.Fill * roads.FillCostPerCubicMetre) * _accessTuning.PadCostShare);
        string reason = "";
        if (height < terrain.WaterSurfaceAt(x, z) + 0.2f) reason = "the floor would be under water";
        else if (change.MaxCut > _accessTuning.PadMaxChange) reason = $"it would cut {change.MaxCut:0.#} m into the ground (at most {_accessTuning.PadMaxChange:0.#} m)";
        else if (change.MaxFill > _accessTuning.PadMaxChange) reason = $"it would fill {change.MaxFill:0.#} m (at most {_accessTuning.PadMaxChange:0.#} m)";
        return new PadPlan(reason, height, change.Cut, change.Fill, change.MaxCut, change.MaxFill, money, x, z, angle, halfAlong, halfAcross);
    }

    /// <summary>A height sample a pad would change, for drawing: where it is and how much it moves (negative: cut).</summary>
    public readonly record struct PadCell(float X, float Z, float Delta);

    /// <summary>The samples a planned pad changes and by how much, for a preview of the cut and the fill.</summary>
    public List<PadCell> PadCells(PadPlan pad)
    {
        var list = new List<PadCell>();
        if (_net.Terrain is not { } terrain || pad.HalfAlong <= 0f) return list;
        foreach (var (x, z, delta, _, _) in terrain.PadSamples(pad.X, pad.Z, pad.Angle, pad.HalfAlong, pad.HalfAcross, pad.Height, _accessTuning.PadShoulder, SkipRoadSample))
            list.Add(new PadCell(x, z, delta));
        return list;
    }

    /// <summary>Whether a sample is under a road or a track, where a pad leaves the ground alone.</summary>
    bool SkipRoadSample(float x, float z) => _net.RoadAt(x, z, PadRoadClearance) != null || (RailInUse && _rail.TrackAt(x, z, PadRoadClearance) != null);

    /// <summary>Levels the ground for a pad that <see cref="PlanPad"/> approved (call it after the roads of the building are made, so they are left alone).</summary>
    void LevelPad(float x, float z, float angle, float half, PadPlan pad) => LevelPad(x, z, angle, half, half, pad);

    void LevelPad(float x, float z, float angle, float halfAlong, float halfAcross, PadPlan pad) =>
        _net.Terrain?.LevelPad(x, z, angle, halfAlong, halfAcross, pad.Height, _accessTuning.PadShoulder, true, SkipRoadSample);
}
