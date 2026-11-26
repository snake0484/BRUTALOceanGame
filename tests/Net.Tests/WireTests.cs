using Brutal.Numerics;
using BrutalGame.Frames;
using BrutalGame.Net.Wire;
using BrutalGame.Sim.Entities;

namespace BrutalGame.Net.Tests;

public class WireTests
{
    [Fact]
    public void Varints_RoundTripAcrossMagnitudes()
    {
        Span<byte> buffer = stackalloc byte[256];
        long[] values = [0, 1, -1, 63, -64, 64, 8191, -8192, 1L << 40, -(1L << 40), long.MaxValue, long.MinValue];
        var writer = new NetWriter(buffer);

        foreach (var v in values)
            writer.VarInt(v);

        var reader = new NetReader(writer.Written);

        foreach (var v in values)
            Assert.Equal(v, reader.VarInt());

        Assert.False(reader.Failed);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Reader_PastTheEndFailsWithoutThrowing()
    {
        var reader = new NetReader([0x80, 0x80]);
        Assert.Equal(0UL, reader.VarUInt());
        Assert.True(reader.Failed);
        Assert.Equal(0L, reader.Long());
    }

    [Fact]
    public void Writer_OverflowDropsAndFlags()
    {
        Span<byte> buffer = stackalloc byte[3];
        var writer = new NetWriter(buffer);
        writer.UShort(1);
        writer.UShort(2);
        Assert.True(writer.Overflow);
        Assert.Equal(2, writer.Position);
    }

    /// <summary>Review C13: int64 at 1/1024 m, so error is at most half a step at any range a frame holds.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.2345678)]
    [InlineData(-9876.54321)]
    [InlineData(5.0e7)]
    [InlineData(-6.0e12)]
    public void Position_QuantisesToAThousandthOfAMetre(double metres)
    {
        var back = NetQuant.Position(NetQuant.Position(metres));
        Assert.True(Math.Abs(back - metres) <= 0.5 / 1024.0 + Math.Abs(metres) * 1e-15, $"{metres} -> {back}");
    }

    [Fact]
    public void SmallestThree_ErrorUnderAQuarterDegree()
    {
        var random = new Random(7);
        var worst = 0.0;

        for (var i = 0; i < 20000; i++)
        {
            var q = doubleQuat.Normalize(new doubleQuat(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1));
            var back = NetQuant.Rotation(NetQuant.Rotation(q));
            var dot = Math.Min(1.0, Math.Abs(doubleQuat.Dot(q, back)));
            worst = Math.Max(worst, 2.0 * Math.Acos(dot) * 180.0 / Math.PI);
        }

        Assert.True(worst < 0.25, $"worst {worst}°");
    }

    [Fact]
    public void FullState_RoundTripsBitExact()
    {
        var state = NetBodyState.From(Moving(new double3(1234.5, -6e6, 42.125)));
        Span<byte> buffer = stackalloc byte[64];
        var writer = new NetWriter(buffer);
        BodyCodec.WriteFull(ref writer, 77, state);

        var reader = new NetReader(writer.Written);
        BodyCodec.ReadHeader(ref reader, out var netId, out var age);
        var back = BodyCodec.ReadBody(ref reader, age, default);

        Assert.Equal(77u, netId);
        Assert.Equal(0, age);
        Assert.Equal(state, back);
        Assert.True(writer.Position <= 40, $"full state {writer.Position} B");
    }

    /// <summary>frames-and-zones Networking: a moving body costs about 20 B against a recent baseline; one at rest 3 B.</summary>
    [Fact]
    public void Delta_IsSmallAndRoundTrips()
    {
        var before = Moving(new double3(5000.0, 1.0e6 + 500.0, -300.0));
        var after = before;
        after.Position += before.Velocity * (6.0 / 60.0);
        after.Rotation = doubleQuat.Normalize(after.Rotation * doubleQuat.CreateFromAxisAngle(double3.UnitY, 0.05));
        after.Velocity += new double3(0.3, 0.0, -0.1);

        var baseline = NetBodyState.From(before);
        var state = NetBodyState.From(after);
        Span<byte> buffer = stackalloc byte[64];
        var writer = new NetWriter(buffer);
        BodyCodec.WriteDelta(ref writer, 300, 6, state, baseline);
        var moving = writer.Position;

        var reader = new NetReader(writer.Written);
        BodyCodec.ReadHeader(ref reader, out var netId, out var age);
        Assert.Equal(300u, netId);
        Assert.Equal(6, age);
        Assert.Equal(state, BodyCodec.ReadBody(ref reader, age, baseline));

        writer = new NetWriter(buffer);
        BodyCodec.WriteDelta(ref writer, 300, 2, baseline, baseline);

        Assert.True(moving <= 22, $"moving delta {moving} B");
        Assert.Equal(4, writer.Position);
    }

    private static Placement Moving(double3 position) => new()
    {
        Frame = new FrameId(3),
        Position = position,
        Rotation = doubleQuat.Normalize(new doubleQuat(0.1, 0.7, -0.2, 0.6)),
        Velocity = new double3(12.5, -3.0, 40.0),
        AngularVelocity = new float3(0.1f, -0.5f, 0.0f),
    };
}
