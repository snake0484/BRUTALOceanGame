using Brutal.Numerics;

namespace BrutalGame.Net.Wire;

/// <summary>
/// Wire quantisation (docs/pivot/frames-and-zones.md, Networking; review C13): positions are int64 at 1/1024 m in their
/// frame, rotations smallest-three in 32 bits, velocities half floats.
/// </summary>
public static class NetQuant
{
    public const double PositionScale = 1024.0;
    public const double PositionStep = 1.0 / PositionScale;

    private const int RotationBits = 10;
    private const int RotationMax = (1 << RotationBits) - 1;
    private const double RotationRange = 0.70710678118654752;

    public static long Position(double metres) => (long)Math.Round(metres * PositionScale);

    public static double Position(long units) => units * PositionStep;

    public static long3 Position(double3 metres) => new(Position(metres.X), Position(metres.Y), Position(metres.Z));

    public static double3 Position(long3 units) => new(Position(units.X), Position(units.Y), Position(units.Z));

    public static ushort Half(double value) => BitConverter.HalfToUInt16Bits((Half)value);

    public static double Half(ushort bits) => (double)BitConverter.UInt16BitsToHalf(bits);

    /// <summary>
    /// Smallest three: 2 bits name the largest component (made positive, so it is implied), then the other three in
    /// [-1/√2, 1/√2] at 10 bits each. Worst angle error about 0.23° (0.4 m at the tip of a 100 m hull).
    /// </summary>
    public static uint Rotation(doubleQuat q)
    {
        q = doubleQuat.Normalize(q);
        Span<double> c = [q.X, q.Y, q.Z, q.W];
        var largest = 0;

        for (var i = 1; i < 4; i++)
        {
            if (Math.Abs(c[i]) > Math.Abs(c[largest]))
                largest = i;
        }

        var sign = c[largest] < 0.0 ? -1.0 : 1.0;
        var packed = (uint)largest << 30;
        var shift = 20;

        for (var i = 0; i < 4; i++)
        {
            if (i == largest)
                continue;

            var unit = (c[i] * sign / RotationRange + 1.0) * 0.5;
            var bits = (uint)Math.Clamp((int)Math.Round(unit * RotationMax), 0, RotationMax);
            packed |= bits << shift;
            shift -= RotationBits;
        }

        return packed;
    }

    public static doubleQuat Rotation(uint packed)
    {
        var largest = (int)(packed >> 30);
        Span<double> c = stackalloc double[4];
        var shift = 20;
        var sum = 0.0;

        for (var i = 0; i < 4; i++)
        {
            if (i == largest)
                continue;

            var bits = (packed >> shift) & RotationMax;
            var value = (bits * (1.0 / RotationMax) * 2.0 - 1.0) * RotationRange;
            c[i] = value;
            sum += value * value;
            shift -= RotationBits;
        }

        c[largest] = Math.Sqrt(Math.Max(0.0, 1.0 - sum));
        return doubleQuat.Normalize(new doubleQuat(c[0], c[1], c[2], c[3]));
    }
}
