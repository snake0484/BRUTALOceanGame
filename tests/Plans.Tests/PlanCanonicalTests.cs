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

        foreach (ref var solid in b.Solids.AsSpan())
            solid.Material = solid.Material == 0 ? (byte)2 : (byte)3;

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
        a.Solids[2].Rotation = SolidRotation.FromSteps(0, 6, 0);
        b.Solids[0].Rotation = SolidRotation.FromSteps(12, 12, 12);
        b.Solids[2].Rotation = SolidRotation.FromSteps(12, 6, 12);
        c.Solids[2].Rotation = SolidRotation.FromSteps(3, 6, 3);
        d.Solids[2].Rotation = SolidRotation.FromSteps(3, 6, 0);

        var ha = CanonicalHash(a);
        Assert.Equal(ha, CanonicalHash(b));
        Assert.Equal(ha, CanonicalHash(c));
        Assert.Equal(SolidRotation.Identity, b.Solids[0].Rotation);
        Assert.NotEqual(ha, CanonicalHash(d));
    }

    [Fact]
    public void CanonicalRotation_IsTheSameMatrix()
    {
        Span<double> m = stackalloc double[9];
        Span<double> n = stackalloc double[9];

        for (var r = 0; r < SolidRotation.Steps; r += 5)
            for (var p = 0; p < SolidRotation.Steps; p++)
                for (var y = 0; y < SolidRotation.Steps; y += 3)
                {
                    var rotation = SolidRotation.FromSteps(y, p, r);
                    var canonical = SolidRotation.Canonical(rotation);
                    SolidRotation.Matrix(rotation, m);
                    SolidRotation.Matrix(canonical, n);
                    Assert.True(canonical <= rotation);
                    Assert.Equal(canonical, SolidRotation.Canonical(canonical));

                    for (var i = 0; i < 9; i++)
                        Assert.Equal(m[i], n[i], 9);
                }
    }

    [Fact]
    public void UnusedFields_AreZeroedOrMirrored()
    {
        var a = Sample();
        var b = Sample();
        b.Solids[0].Param = 77;
        b.Solids[2].HalfZ = 999;

        Assert.Equal(CanonicalHash(a), CanonicalHash(b));
        Assert.Equal(b.Solids[2].HalfX, b.Solids[2].HalfZ);
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
    public void SolidOrder_IsDesignAndChangesTheHash()
    {
        var a = Sample();
        var b = Sample();
        var first = b.Solids[0];
        b.Solids.RemoveAt(0);
        b.Solids.Insert(1, first);

        Assert.NotEqual(CanonicalHash(a), CanonicalHash(b));
    }
}
