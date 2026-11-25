using BrutalGame.Ships;
using static BrutalGame.Plans.Tests.PlanFormatTests;

namespace BrutalGame.Plans.Tests;

public class PlanCanonicalTests
{
    private static PlanHash CanonicalHash(PlanDocument doc)
    {
        PlanCanonical.Canonicalise(doc);
        return PlanFormat.HashBody(doc);
    }

    [Fact]
    public void MaterialTableOrderAndUnusedEntries_HashTheSame()
    {
        var a = Sample();
        var b = Sample();

        // b lists the same two materials the other way round, plus an unused third and a duplicate.
        b.Materials.Clear();
        b.Materials.Add(DefId.FromName("mat.unused"));
        b.Materials.Add(Titanium);
        b.Materials.Add(Alloy);
        b.Materials.Add(Titanium);

        foreach (ref var brush in b.Brushes.AsSpan())
            brush.Material = brush.Material == 0 ? (byte)2 : (byte)3;

        Assert.NotEqual(PlanFormat.HashBody(a), PlanFormat.HashBody(b));
        Assert.Equal(CanonicalHash(a), CanonicalHash(b));
        Assert.Equal([Alloy, Titanium], b.Materials);
    }

    [Fact]
    public void EquivalentRotations_HashTheSame()
    {
        var a = Sample();
        var b = Sample();
        var c = Sample();
        var d = Sample();

        // Ry(180) Rx(t) Rz(180) = Rx(180 - t), so (12, 12, 12) is the identity and (12, 6, 12) is (0, 6, 0); at pitch
        // 90 degrees yaw and roll trade (gimbal lock): Ry(a) Rx(90) Rz(b) = Rx(90) Rz(b - a).
        a.Brushes[2].Rotation = BrushRotation.FromSteps(0, 6, 0);
        b.Brushes[0].Rotation = BrushRotation.FromSteps(12, 12, 12);
        b.Brushes[2].Rotation = BrushRotation.FromSteps(12, 6, 12);
        c.Brushes[2].Rotation = BrushRotation.FromSteps(3, 6, 3);
        d.Brushes[2].Rotation = BrushRotation.FromSteps(3, 6, 0);

        var ha = CanonicalHash(a);
        Assert.Equal(ha, CanonicalHash(b));
        Assert.Equal(ha, CanonicalHash(c));
        Assert.Equal(BrushRotation.Identity, b.Brushes[0].Rotation);
        Assert.NotEqual(ha, CanonicalHash(d));
    }

    [Fact]
    public void CanonicalRotation_IsTheSameMatrix()
    {
        Span<double> m = stackalloc double[9];
        Span<double> n = stackalloc double[9];

        for (var r = 0; r < BrushRotation.Steps; r += 5)
            for (var p = 0; p < BrushRotation.Steps; p++)
                for (var y = 0; y < BrushRotation.Steps; y += 3)
                {
                    var rotation = BrushRotation.FromSteps(y, p, r);
                    var canonical = BrushRotation.Canonical(rotation);
                    BrushRotation.Matrix(rotation, m);
                    BrushRotation.Matrix(canonical, n);
                    Assert.True(canonical <= rotation);
                    Assert.Equal(canonical, BrushRotation.Canonical(canonical));

                    for (var i = 0; i < 9; i++)
                        Assert.Equal(m[i], n[i], 9);
                }
    }

    [Fact]
    public void UnusedFields_AreZeroedOrMirrored()
    {
        var a = Sample();
        var b = Sample();
        b.Brushes[0].Param = 77;
        b.Brushes[2].HalfZ = 999;

        Assert.Equal(CanonicalHash(a), CanonicalHash(b));
        Assert.Equal(b.Brushes[2].HalfX, b.Brushes[2].HalfZ);
    }

    [Fact]
    public void Canonicalise_IsIdempotent()
    {
        var doc = Sample();
        PlanCanonical.Canonicalise(doc);
        var once = PlanFormat.EncodeBody(doc);
        PlanCanonical.Canonicalise(doc);

        Assert.Equal(once, PlanFormat.EncodeBody(doc));
    }

    [Fact]
    public void BrushOrder_IsDesignAndChangesTheHash()
    {
        var a = Sample();
        var b = Sample();
        var first = b.Brushes[0];
        b.Brushes.RemoveAt(0);
        b.Brushes.Insert(1, first);

        Assert.NotEqual(CanonicalHash(a), CanonicalHash(b));
    }
}
