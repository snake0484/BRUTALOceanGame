namespace BrutalGame.Ships;

/// <summary>
/// Brush rotation as a 15 degree snap index (review X2): yaw about Y, pitch about X, roll about Z, 0..23 steps each, in
/// bits 0-4, 5-9 and 10-14. Brush to ship is R = Ry(yaw) · Rx(pitch) · Rz(roll). Bits 15-31 are reserved for a later
/// free-rotation form and must be zero. The step table is built from square roots only (no trig), so every peer decodes
/// the same doubles. Several triples name one rotation (gimbal lock, the 180 degree identity); <see cref="Canonical"/>
/// maps each to the smallest encoded triple of its rotation.
/// </summary>
public static class BrushRotation
{
    public const int Steps = 24;
    public const int DegreesPerStep = 15;
    public const uint IndexMask = 0x7FFF;
    public const uint Identity = 0;
    private const int TripleCount = Steps * Steps * Steps;
    private const double KeyScale = 1e6;

    private static readonly double[] Cos = new double[Steps];
    private static readonly double[] Sin = new double[Steps];
    private static ushort[]? _canonical;

    static BrushRotation()
    {
        var r2 = Math.Sqrt(2.0) * 0.5;
        var r3 = Math.Sqrt(3.0) * 0.5;
        var c15 = (Math.Sqrt(6.0) + Math.Sqrt(2.0)) * 0.25;
        var s15 = (Math.Sqrt(6.0) - Math.Sqrt(2.0)) * 0.25;
        ReadOnlySpan<double> quarterSin = [0.0, s15, 0.5, r2, r3, c15, 1.0];

        for (var i = 0; i < Steps; i++)
        {
            var q = i % 6;
            var quadrant = i / 6;
            var s = quarterSin[q];
            var c = quarterSin[6 - q];

            (Sin[i], Cos[i]) = quadrant switch
            {
                0 => (s, c),
                1 => (c, -s),
                2 => (-s, -c),
                _ => (-c, s),
            };
        }
    }

    public static uint FromSteps(int yaw, int pitch, int roll) => (uint)(Wrap(yaw) | (Wrap(pitch) << 5) | (Wrap(roll) << 10));

    public static (int Yaw, int Pitch, int Roll) ToSteps(uint rotation) => ((int)(rotation & 31), (int)((rotation >> 5) & 31), (int)((rotation >> 10) & 31));

    public static bool IsValid(uint rotation) =>
        (rotation & ~IndexMask) == 0 && (rotation & 31) < Steps && ((rotation >> 5) & 31) < Steps && ((rotation >> 10) & 31) < Steps;

    /// <summary>Adds steps about one local Euler axis (0 yaw, 1 pitch, 2 roll), wrapping.</summary>
    public static uint AddSteps(uint rotation, int axis, int steps)
    {
        var (yaw, pitch, roll) = ToSteps(rotation);

        if (axis == 0)
            yaw += steps;
        else if (axis == 1)
            pitch += steps;
        else
            roll += steps;

        return FromSteps(yaw, pitch, roll);
    }

    /// <summary>Row-major brush-to-ship matrix: ship = M · local. An invalid index decodes as identity.</summary>
    public static void Matrix(uint rotation, Span<double> m)
    {
        if (!IsValid(rotation))
            rotation = Identity;

        var (yaw, pitch, roll) = ToSteps(rotation);
        double cy = Cos[yaw], sy = Sin[yaw], cp = Cos[pitch], sp = Sin[pitch], cr = Cos[roll], sr = Sin[roll];

        // Ry · Rx
        double a00 = cy, a01 = sy * sp, a02 = sy * cp;
        double a10 = 0.0, a11 = cp, a12 = -sp;
        double a20 = -sy, a21 = cy * sp, a22 = cy * cp;

        // (Ry · Rx) · Rz
        m[0] = a00 * cr + a01 * sr;
        m[1] = -a00 * sr + a01 * cr;
        m[2] = a02;
        m[3] = a10 * cr + a11 * sr;
        m[4] = -a10 * sr + a11 * cr;
        m[5] = a12;
        m[6] = a20 * cr + a21 * sr;
        m[7] = -a20 * sr + a21 * cr;
        m[8] = a22;
    }

    /// <summary>The smallest encoded triple naming the same rotation. Invalid indices are returned unchanged.</summary>
    public static uint Canonical(uint rotation)
    {
        if (!IsValid(rotation))
            return rotation;

        var (yaw, pitch, roll) = ToSteps(rotation);
        return (_canonical ??= BuildCanonical())[yaw + Steps * (pitch + Steps * roll)];
    }

    private static ushort[] BuildCanonical()
    {
        var map = new ushort[TripleCount];
        var first = new Dictionary<RotationKey, ushort>(TripleCount);
        Span<double> m = stackalloc double[9];

        // Roll, pitch, yaw nesting visits encoded values in increasing order, so the first triple seen is the smallest.
        for (var roll = 0; roll < Steps; roll++)
            for (var pitch = 0; pitch < Steps; pitch++)
                for (var yaw = 0; yaw < Steps; yaw++)
                {
                    var encoded = (ushort)FromSteps(yaw, pitch, roll);
                    Matrix(encoded, m);
                    var key = new RotationKey(m);

                    if (!first.TryGetValue(key, out var smallest))
                    {
                        smallest = encoded;
                        first.Add(key, smallest);
                    }

                    map[yaw + Steps * (pitch + Steps * roll)] = smallest;
                }

        return map;
    }

    private static int Wrap(int steps) => ((steps % Steps) + Steps) % Steps;

    private readonly record struct RotationKey(long M0, long M1, long M2, long M3, long M4, long M5, long M6, long M7, long M8)
    {
        public RotationKey(ReadOnlySpan<double> m)
            : this(Q(m[0]), Q(m[1]), Q(m[2]), Q(m[3]), Q(m[4]), Q(m[5]), Q(m[6]), Q(m[7]), Q(m[8]))
        {
        }

        private static long Q(double v) => (long)Math.Round(v * KeyScale);
    }
}
