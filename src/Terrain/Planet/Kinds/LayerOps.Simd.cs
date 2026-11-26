using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace BrutalGame.Terrain;

/// <summary>
/// The lattice noise eight lanes at a time (AVX2): eight points of one octave, or eight octaves of one point. Every
/// operation is the scalar one in the same order, with no FMA:
/// the cell comes from a double floor split into exact 32-bit halves, the gradients from byte shuffles of the same
/// table (so a zero component still multiplies), and the float maths lane for lane. Results equal
/// <see cref="Noise.Gradient3(double, double, double, uint)"/> bit for bit.
/// </summary>
internal static partial class LayerOps
{
    /// <summary>Off forces the scalar rows (tests compare the two).</summary>
    public static bool SimdEnabled = true;

    private const double TwoTo32 = 4294967296.0;

    private static readonly Vector256<byte> TableX = GradientBytes(0);
    private static readonly Vector256<byte> TableY = GradientBytes(1);
    private static readonly Vector256<byte> TableZ = GradientBytes(2);

    private static Vector256<byte> GradientBytes(int component)
    {
        Span<byte> bytes = stackalloc byte[32];

        for (var i = 0; i < 32; i++)
            bytes[i] = (byte)(sbyte)Noise.GradientTable[(i & 15) * 4 + component];

        return Vector256.Create((ReadOnlySpan<byte>)bytes);
    }

    /// <summary>
    /// One octave over the leading multiple of eight points; returns how many it wrote (0 without AVX2). With gradient
    /// rows it also writes the analytic derivative, as <see cref="Noise.Gradient3(double, double, double, uint, out float, out float, out float)"/>.
    /// </summary>
    private static int NoiseRowSimd(ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, ReadOnlySpan<float> wx, ReadOnlySpan<float> wy, ReadOnlySpan<float> wz,
        double f, double ox, double oy, double oz, uint seed, Span<float> n, Span<float> gx, Span<float> gy, Span<float> gz)
    {
        if (!SimdEnabled || !Avx2.IsSupported)
            return 0;

        var count = n.Length & ~7;
        var warped = !wx.IsEmpty;
        var derivative = !gx.IsEmpty;
        var seeds = Vector256.Create(seed);
        ref var rx = ref MemoryMarshal.GetReference(x);
        ref var ry = ref MemoryMarshal.GetReference(y);
        ref var rz = ref MemoryMarshal.GetReference(z);

        for (var i = 0; i < count; i += 8)
        {
            Vector256<double> x0 = Vector256.LoadUnsafe(ref rx, (nuint)i), x1 = Vector256.LoadUnsafe(ref rx, (nuint)i + 4);
            Vector256<double> y0 = Vector256.LoadUnsafe(ref ry, (nuint)i), y1 = Vector256.LoadUnsafe(ref ry, (nuint)i + 4);
            Vector256<double> z0 = Vector256.LoadUnsafe(ref rz, (nuint)i), z1 = Vector256.LoadUnsafe(ref rz, (nuint)i + 4);

            if (warped)
            {
                Offset(ref x0, ref x1, wx, i);
                Offset(ref y0, ref y1, wy, i);
                Offset(ref z0, ref z1, wz, i);
            }

            Vector256<double> vf = Vector256.Create(f), vx = Vector256.Create(ox), vy = Vector256.Create(oy), vz = Vector256.Create(oz);
            var value = Lanes(Scaled(x0, vf, vx), Scaled(x1, vf, vx), Scaled(y0, vf, vy), Scaled(y1, vf, vy), Scaled(z0, vf, vz), Scaled(z1, vf, vz), seeds, derivative, out var dx, out var dy, out var dz);
            value.StoreUnsafe(ref MemoryMarshal.GetReference(n), (nuint)i);

            if (!derivative)
                continue;

            dx.StoreUnsafe(ref MemoryMarshal.GetReference(gx), (nuint)i);
            dy.StoreUnsafe(ref MemoryMarshal.GetReference(gy), (nuint)i);
            dz.StoreUnsafe(ref MemoryMarshal.GetReference(gz), (nuint)i);
        }

        return count;
    }

    /// <summary>
    /// One point's octaves, eight at a time (the last lanes padded): the same values as the scalar noise octave by
    /// octave. False without AVX2, with nothing written.
    /// </summary>
    [SkipLocalsInit]
    private static bool NoiseOctavesSimd(double x, double y, double z, ReadOnlySpan<double> f, ReadOnlySpan<double> ox, ReadOnlySpan<double> oy, ReadOnlySpan<double> oz, ReadOnlySpan<uint> seed,
        Span<float> n, Span<float> gx, Span<float> gy, Span<float> gz)
    {
        if (!SimdEnabled || !Avx2.IsSupported)
            return false;

        var derivative = !gx.IsEmpty;
        Span<double> table = stackalloc double[4 * 8];
        Span<uint> seeds = stackalloc uint[8];
        Span<float> lanes = stackalloc float[4 * 8];
        Vector256<double> vx = Vector256.Create(x), vy = Vector256.Create(y), vz = Vector256.Create(z);
        ref var t = ref MemoryMarshal.GetReference(table);

        for (var i = 0; i < n.Length; i += 8)
        {
            var take = Math.Min(8, n.Length - i);
            table.Clear();
            seeds.Clear();
            f.Slice(i, take).CopyTo(table);
            ox.Slice(i, take).CopyTo(table[8..]);
            oy.Slice(i, take).CopyTo(table[16..]);
            oz.Slice(i, take).CopyTo(table[24..]);
            seed.Slice(i, take).CopyTo(seeds);
            Vector256<double> f0 = Vector256.LoadUnsafe(ref t), f1 = Vector256.LoadUnsafe(ref t, 4);
            var value = Lanes(
                Scaled(vx, f0, Vector256.LoadUnsafe(ref t, 8)), Scaled(vx, f1, Vector256.LoadUnsafe(ref t, 12)),
                Scaled(vy, f0, Vector256.LoadUnsafe(ref t, 16)), Scaled(vy, f1, Vector256.LoadUnsafe(ref t, 20)),
                Scaled(vz, f0, Vector256.LoadUnsafe(ref t, 24)), Scaled(vz, f1, Vector256.LoadUnsafe(ref t, 28)),
                Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(seeds)), derivative, out var dx, out var dy, out var dz);
            value.CopyTo(lanes);
            lanes[..take].CopyTo(n[i..]);

            if (!derivative)
                continue;

            dx.CopyTo(lanes[8..]);
            dy.CopyTo(lanes[16..]);
            dz.CopyTo(lanes[24..]);
            lanes.Slice(8, take).CopyTo(gx[i..]);
            lanes.Slice(16, take).CopyTo(gy[i..]);
            lanes.Slice(24, take).CopyTo(gz[i..]);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Scaled(Vector256<double> position, Vector256<double> f, Vector256<double> offset) => Avx.Add(Avx.Multiply(position, f), offset);

    /// <summary>Eight lanes of noise at lattice positions (already position × f + offset), clamped to [−1, 1], and the unclamped derivative if asked.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Lanes(Vector256<double> px0, Vector256<double> px1, Vector256<double> py0, Vector256<double> py1, Vector256<double> pz0, Vector256<double> pz1,
        Vector256<uint> seeds, bool derivative, out Vector256<float> dx, out Vector256<float> dy, out Vector256<float> dz)
    {
        var one = Vector256.Create(1f);
        Cells(px0, px1, out var fx, out var cx0, out var cx1);
        Cells(py0, py1, out var fy, out var cy0, out var cy1);
        Cells(pz0, pz1, out var fz, out var cz0, out var cz1);
        var hx0 = Avx2.Xor(seeds, Avx2.MultiplyLow(cx0, Vector256.Create(0x8DA6B343u)));
        var hx1 = Avx2.Xor(seeds, Avx2.MultiplyLow(cx1, Vector256.Create(0x8DA6B343u)));
        var hy0 = Avx2.MultiplyLow(cy0, Vector256.Create(0xD8163841u));
        var hy1 = Avx2.MultiplyLow(cy1, Vector256.Create(0xD8163841u));
        var hz0 = Avx2.MultiplyLow(cz0, Vector256.Create(0xCB1AB31Fu));
        var hz1 = Avx2.MultiplyLow(cz1, Vector256.Create(0xCB1AB31Fu));
        var u = Fade(fx);
        var v = Fade(fy);
        var w = Fade(fz);
        Vector256<float> fx1 = Avx.Subtract(fx, one), fy1 = Avx.Subtract(fy, one), fz1 = Avx.Subtract(fz, one);
        var n000 = Corner(Avx2.Xor(Avx2.Xor(hx0, hy0), hz0), fx, fy, fz, out var ax000, out var ay000, out var az000);
        var n100 = Corner(Avx2.Xor(Avx2.Xor(hx1, hy0), hz0), fx1, fy, fz, out var ax100, out var ay100, out var az100);
        var n010 = Corner(Avx2.Xor(Avx2.Xor(hx0, hy1), hz0), fx, fy1, fz, out var ax010, out var ay010, out var az010);
        var n110 = Corner(Avx2.Xor(Avx2.Xor(hx1, hy1), hz0), fx1, fy1, fz, out var ax110, out var ay110, out var az110);
        var n001 = Corner(Avx2.Xor(Avx2.Xor(hx0, hy0), hz1), fx, fy, fz1, out var ax001, out var ay001, out var az001);
        var n101 = Corner(Avx2.Xor(Avx2.Xor(hx1, hy0), hz1), fx1, fy, fz1, out var ax101, out var ay101, out var az101);
        var n011 = Corner(Avx2.Xor(Avx2.Xor(hx0, hy1), hz1), fx, fy1, fz1, out var ax011, out var ay011, out var az011);
        var n111 = Corner(Avx2.Xor(Avx2.Xor(hx1, hy1), hz1), fx1, fy1, fz1, out var ax111, out var ay111, out var az111);
        var a = Lerp(Lerp(n000, n100, u), Lerp(n010, n110, u), v);
        var b = Lerp(Lerp(n001, n101, u), Lerp(n011, n111, u), v);
        var value = Avx.Min(Avx.Max(Lerp(a, b, w), Vector256.Create(-1f)), one);
        dx = dy = dz = default;

        if (!derivative)
            return value;

        dx = Avx.Add(Blend(ax000, ax100, ax010, ax110, ax001, ax101, ax011, ax111, u, v, w),
            Avx.Multiply(FadeSlope(fx, fx1), Lerp(Lerp(Avx.Subtract(n100, n000), Avx.Subtract(n110, n010), v), Lerp(Avx.Subtract(n101, n001), Avx.Subtract(n111, n011), v), w)));
        dy = Avx.Add(Blend(ay000, ay100, ay010, ay110, ay001, ay101, ay011, ay111, u, v, w),
            Avx.Multiply(FadeSlope(fy, fy1), Lerp(Lerp(Avx.Subtract(n010, n000), Avx.Subtract(n110, n100), u), Lerp(Avx.Subtract(n011, n001), Avx.Subtract(n111, n101), u), w)));
        dz = Avx.Add(Blend(az000, az100, az010, az110, az001, az101, az011, az111, u, v, w), Avx.Multiply(FadeSlope(fz, fz1), Avx.Subtract(b, a)));
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Offset(ref Vector256<double> lo, ref Vector256<double> hi, ReadOnlySpan<float> warp, int i)
    {
        ref var r = ref MemoryMarshal.GetReference(warp);
        lo = Avx.Add(lo, Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref r, (nuint)i)));
        hi = Avx.Add(hi, Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref r, (nuint)i + 4)));
    }

    /// <summary>Per lane of lattice position: the in-cell fraction as float and the folded cell hashes of the cell and the next.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Cells(Vector256<double> p0, Vector256<double> p1, out Vector256<float> fraction, out Vector256<uint> cell, out Vector256<uint> next)
    {
        var f0 = Avx.Floor(p0);
        var f1 = Avx.Floor(p1);
        fraction = Vector256.Create(Avx.ConvertToVector128Single(Avx.Subtract(p0, f0)), Avx.ConvertToVector128Single(Avx.Subtract(p1, f1)));
        cell = Fold(f0, f1);
        next = Fold(Avx.Add(f0, Vector256.Create(1.0)), Avx.Add(f1, Vector256.Create(1.0)));
    }

    /// <summary><see cref="Noise.Fold"/> of whole doubles: the 64-bit integer's halves, each exact in double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Fold(Vector256<double> lo, Vector256<double> hi)
    {
        Halves(lo, out var low0, out var high0);
        Halves(hi, out var low1, out var high1);
        var low = Vector256.Create(low0, low1).AsUInt32();
        var high = Vector256.Create(high0, high1).AsUInt32();
        return Avx2.Xor(low, Avx2.MultiplyLow(high, Vector256.Create(0x9E3779B1u)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Halves(Vector256<double> whole, out Vector128<int> low, out Vector128<int> high)
    {
        var top = Avx.Floor(Avx.Multiply(whole, Vector256.Create(1.0 / TwoTo32)));
        var rest = Avx.Subtract(whole, Avx.Multiply(top, Vector256.Create(TwoTo32)));
        high = Avx.ConvertToVector128Int32WithTruncation(top);
        low = Sse2.Xor(Avx.ConvertToVector128Int32WithTruncation(Avx.Subtract(rest, Vector256.Create(2147483648.0))), Vector128.Create(int.MinValue));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Mix(Vector256<uint> h)
    {
        h = Avx2.Xor(h, Avx2.ShiftRightLogical(h, 16));
        h = Avx2.MultiplyLow(h, Vector256.Create(0x7FEB352Du));
        h = Avx2.Xor(h, Avx2.ShiftRightLogical(h, 15));
        h = Avx2.MultiplyLow(h, Vector256.Create(0x846CA68Bu));
        return Avx2.Xor(h, Avx2.ShiftRightLogical(h, 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Component(Vector256<byte> table, Vector256<byte> index) =>
        Avx.ConvertToVector256Single(Avx2.ShiftRightArithmetic(Avx2.ShiftLeftLogical(Avx2.Shuffle(table, index).AsInt32(), 24), 24));

    /// <summary>The corner's gradient · offset, (g.x·x + g.y·y) + g.z·z as the scalar table dot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Corner(Vector256<uint> hash, Vector256<float> x, Vector256<float> y, Vector256<float> z, out Vector256<float> gx, out Vector256<float> gy, out Vector256<float> gz)
    {
        var index = Avx2.And(Mix(hash), Vector256.Create(15u)).AsByte();
        gx = Component(TableX, index);
        gy = Component(TableY, index);
        gz = Component(TableZ, index);
        return Avx.Add(Avx.Add(Avx.Multiply(gx, x), Avx.Multiply(gy, y)), Avx.Multiply(gz, z));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Fade(Vector256<float> t)
    {
        var inner = Avx.Add(Avx.Multiply(t, Avx.Subtract(Avx.Multiply(t, Vector256.Create(6f)), Vector256.Create(15f))), Vector256.Create(10f));
        return Avx.Multiply(Avx.Multiply(Avx.Multiply(t, t), t), inner);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> FadeSlope(Vector256<float> t, Vector256<float> tLessOne) =>
        Avx.Multiply(Avx.Multiply(Avx.Multiply(Avx.Multiply(Vector256.Create(30f), t), t), tLessOne), tLessOne);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Lerp(Vector256<float> a, Vector256<float> b, Vector256<float> t) => Avx.Add(a, Avx.Multiply(Avx.Subtract(b, a), t));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Blend(Vector256<float> c000, Vector256<float> c100, Vector256<float> c010, Vector256<float> c110, Vector256<float> c001, Vector256<float> c101, Vector256<float> c011, Vector256<float> c111,
        Vector256<float> u, Vector256<float> v, Vector256<float> w) =>
        Lerp(Lerp(Lerp(c000, c100, u), Lerp(c010, c110, u), v), Lerp(Lerp(c001, c101, u), Lerp(c011, c111, u), v), w);
}
