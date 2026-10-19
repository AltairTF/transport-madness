namespace TransportMadness.Sim;

/// <summary>
/// The centre line of a road piece in the ground plane: a straight line or a circular arc. It starts at (X, Z) heading
/// <see cref="Heading0"/> (atan2 of z over x), turns by <see cref="Sweep"/> radians in total (positive is an increasing
/// heading, zero is straight) and is <see cref="Length"/> metres long. Distances along it are arc lengths.
/// </summary>
public readonly record struct RoadGeometry(float X, float Z, float Heading0, float Sweep, float Length)
{
    /// <summary>Below this total turn a piece is treated as straight.</summary>
    public const float StraightSweep = 1e-4f;

    public bool IsStraight => MathF.Abs(Sweep) < StraightSweep;

    /// <summary>The piece joining two points, bending by <paramref name="sweep"/> radians between them.</summary>
    public static RoadGeometry FromChord(float x0, float z0, float x1, float z1, float sweep = 0f)
    {
        float dx = x1 - x0, dz = z1 - z0;
        float chord = MathF.Sqrt(dx * dx + dz * dz);
        float heading = MathF.Atan2(dz, dx);
        if (MathF.Abs(sweep) < StraightSweep) return new RoadGeometry(x0, z0, heading, 0f, chord);
        float length = chord * MathF.Abs(sweep) / (2f * MathF.Sin(MathF.Abs(sweep) * 0.5f));
        return new RoadGeometry(x0, z0, heading - sweep * 0.5f, sweep, length);
    }

    /// <summary>Radius of the arc, or infinity when straight.</summary>
    public float Radius => IsStraight ? float.PositiveInfinity : Length / MathF.Abs(Sweep);

    public float HeadingAt(float s) => Heading0 + Sweep * (s / Length);

    public (float X, float Z) PointAt(float s)
    {
        if (IsStraight) return (X + MathF.Cos(Heading0) * s, Z + MathF.Sin(Heading0) * s);
        float h = HeadingAt(s);
        float k = Length / Sweep;
        return (X + k * (MathF.Sin(h) - MathF.Sin(Heading0)), Z - k * (MathF.Cos(h) - MathF.Cos(Heading0)));
    }

    /// <summary>Distance along the piece of the point on it nearest to (x, z).</summary>
    public float NearestAlong(float x, float z)
    {
        if (IsStraight)
            return Math.Clamp((x - X) * MathF.Cos(Heading0) + (z - Z) * MathF.Sin(Heading0), 0f, Length);
        // The centre of the arc lies k to the side the road turns toward, and a point at heading h is k * (sin h, -cos h) from it.
        float k = Length / Sweep; // signed radius
        float cx = X - k * MathF.Sin(Heading0), cz = Z + k * MathF.Cos(Heading0);
        float sign = MathF.Sign(k);
        float heading = MathF.Atan2((x - cx) * sign, -(z - cz) * sign);
        float delta = heading - Heading0;
        delta -= MathF.Tau * MathF.Floor((delta + MathF.PI) / MathF.Tau); // into [-pi, pi)
        return Math.Clamp(delta / Sweep * Length, 0f, Length);
    }

    /// <summary>How far (x, z) is from the centre line.</summary>
    public float DistanceTo(float x, float z)
    {
        var (px, pz) = PointAt(NearestAlong(x, z));
        return MathF.Sqrt((px - x) * (px - x) + (pz - z) * (pz - z));
    }

    public (float X, float Z) End => PointAt(Length);

    /// <summary>The same centre line travelled the other way.</summary>
    public RoadGeometry Reversed()
    {
        var (x, z) = End;
        return new RoadGeometry(x, z, HeadingAt(Length) + MathF.PI, -Sweep, Length);
    }

    /// <summary>The part from <paramref name="from"/> to <paramref name="to"/> metres.</summary>
    public RoadGeometry Slice(float from, float to)
    {
        var (x, z) = PointAt(from);
        return new RoadGeometry(x, z, HeadingAt(from), Sweep * ((to - from) / Length), to - from);
    }

    /// <summary>
    /// The sweep of the arc that runs from the first point through the second to the third, or zero when they are
    /// (nearly) in a line. The result is signed like <see cref="Sweep"/>.
    /// </summary>
    public static float SweepThrough(float x0, float z0, float xm, float zm, float x1, float z1)
    {
        float ax = xm - x0, az = zm - z0, bx = x1 - xm, bz = z1 - zm;
        float cross = ax * bz - az * bx;
        float dot = ax * bx + az * bz;
        // Turning angle between the two legs is the inscribed-angle complement; the arc turns by twice that.
        float turn = MathF.Atan2(cross, dot);
        return 2f * turn;
    }
}
