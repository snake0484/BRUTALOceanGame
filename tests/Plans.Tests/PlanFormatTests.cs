using System.Buffers.Binary;
using System.Text;
using BrutalGame.Ships;

namespace BrutalGame.Plans.Tests;

public class PlanFormatTests
{
    internal static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");
    internal static readonly DefId Titanium = DefId.FromName("mat.hull.titanium");

    internal static HullBrush Brush(uint id, BrushShape shape, BrushOp op, byte material, int x, int y, int z, ushort hx, ushort hy, ushort hz, uint rotation = 0) => new()
    {
        Id = id,
        Shape = shape,
        Op = op,
        Material = material,
        PosX = x,
        PosY = y,
        PosZ = z,
        Rotation = rotation,
        HalfX = hx,
        HalfY = hy,
        HalfZ = hz,
    };

    /// <summary>A hollow box with a cylinder nose and a wedge: every v1 shape and op.</summary>
    internal static PlanDocument Sample()
    {
        var doc = new PlanDocument();
        doc.Meta.Name = "Sample";
        doc.Meta.Description = "hollow box, cylinder nose";
        doc.Meta.Tags.Add("test");
        doc.Materials.Add(Alloy);
        doc.Materials.Add(Titanium);
        doc.Brushes.Add(Brush(1, BrushShape.Box, BrushOp.Add, 0, 0, 3072, 0, 1536, 768, 1024));
        doc.Brushes.Add(Brush(2, BrushShape.Box, BrushOp.Subtract, 0, 0, 3072, 0, 1408, 640, 896));
        doc.Brushes.Add(Brush(3, BrushShape.Cylinder, BrushOp.Add, 1, 0, 3072, 7168, 384, 768, 384, BrushRotation.FromSteps(0, 6, 0)));
        doc.Brushes.Add(Brush(5, BrushShape.Wedge, BrushOp.Add, 1, 0, 7168, -2048, 768, 256, 512));
        doc.Brushes.Add(Brush(6, BrushShape.Box, BrushOp.Paint, 1, -2048, 3072, 0, 256, 256, 256));
        doc.Meta.NextId = 7;
        return doc;
    }

    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]
    [InlineData("a", 0xD24EC4F1A98C6E5BUL)]
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    [InlineData("Nobody inspects the spammish repetition", 0xFBCEA83C8A378BF1UL)]
    public void XxHash64_MatchesReferenceVectors(string input, ulong expected)
    {
        Assert.Equal(expected, XxHash64.Hash(Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void XxHash64_LongInputMatchesStripeAndTailPaths()
    {
        var data = new byte[1000];

        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 31 + 7);

        // Every length crosses the 32 B stripe, 8 B, 4 B and 1 B tails: hashing a prefix must not read past it.
        var hashes = new HashSet<ulong>();

        for (var n = 0; n <= 100; n++)
            Assert.True(hashes.Add(XxHash64.Hash(data.AsSpan(0, n))));
    }

    [Fact]
    public void RoundTrip_IsBitExact()
    {
        var doc = Sample();
        PlanCanonical.Canonicalise(doc);
        var bytes = PlanFormat.Encode(doc);

        Assert.True(PlanFormat.TryDecode(bytes, out var loaded, out var header, out var error), error.ToString());
        Assert.Equal(bytes, PlanFormat.Encode(loaded));
        Assert.Equal(doc.Brushes.AsSpan().ToArray(), loaded.Brushes.AsSpan().ToArray());
        Assert.Equal(doc.Materials, loaded.Materials);
        Assert.Equal(doc.Meta.Name, loaded.Meta.Name);
        Assert.Equal(doc.Meta.Tags, loaded.Meta.Tags);
        Assert.Equal(doc.Meta.NextId, loaded.Meta.NextId);
        Assert.Equal(PlanHeader.MagicValue, header.Magic);
        Assert.Equal(PlanFormat.HashBody(doc), header.Hash);
    }

    [Fact]
    public void Header_Is36BytesAndBrush34()
    {
        Assert.Equal(PlanHeader.SizeInBytes, System.Runtime.InteropServices.Marshal.SizeOf<PlanHeader>());
        Assert.Equal(HullBrush.SizeInBytes, System.Runtime.InteropServices.Marshal.SizeOf<HullBrush>());
        Assert.Equal("BPLN"u8.ToArray(), PlanFormat.Encode(Sample())[..4]);
    }

    /// <summary>
    /// The body hash of a fixed design is pinned: a change here means every saved plan, prefab id and cache key moves,
    /// which needs a format version bump, not a silent edit.
    /// </summary>
    [Fact]
    public void Hash_IsStable()
    {
        var doc = Sample();
        PlanCanonical.Canonicalise(doc);

        Assert.Equal(PinnedSampleHash, PlanFormat.HashBody(doc).Value);
        Assert.Equal(PlanFormat.HashBody(doc), PlanFormat.HashBody(doc.Clone()));
    }

    internal const ulong PinnedSampleHash = 0x12578840C160E37DUL;

    [Fact]
    public void Meta_DoesNotChangeTheHash()
    {
        var a = Sample();
        var b = Sample();
        b.Meta.Name = "Renamed";
        b.Meta.Revision = 9;
        b.Meta.Tags.Add("more");
        b.Meta.NextId = 100;

        Assert.Equal(PlanFormat.HashBody(a), PlanFormat.HashBody(b));
        Assert.NotEqual(PlanFormat.Encode(a), PlanFormat.Encode(b));
    }

    [Fact]
    public void Body_ChangesTheHash()
    {
        var a = Sample();
        var b = Sample();
        b.Brushes[0].PosX += 1;

        Assert.NotEqual(PlanFormat.HashBody(a), PlanFormat.HashBody(b));
    }

    [Fact]
    public void Decode_RefusesDamagedFiles()
    {
        var bytes = PlanFormat.Encode(Sample());

        Assert.Equal(PlanFormatError.Truncated, Decode(bytes[..20]));
        Assert.Equal(PlanFormatError.Truncated, Decode(bytes[..^1]));
        Assert.Equal(PlanFormatError.TrailingBytes, Decode([.. bytes, 0]));

        var magic = (byte[])bytes.Clone();
        magic[0] = (byte)'X';
        Assert.Equal(PlanFormatError.BadMagic, Decode(magic));

        var format = (byte[])bytes.Clone();
        format[4] = 9;
        Assert.Equal(PlanFormatError.UnsupportedFormat, Decode(format));

        var level = (byte[])bytes.Clone();
        level[6] = (byte)PlanLevel.Part;
        Assert.Equal(PlanFormatError.UnsupportedLevel, Decode(level));

        var hash = (byte[])bytes.Clone();
        hash[8] ^= 1;
        Assert.Equal(PlanFormatError.HashMismatch, Decode(hash));

        var corrupt = (byte[])bytes.Clone();
        corrupt[PlanHeader.SizeInBytes + 13 + 4] ^= 0xFF;
        Assert.Equal(PlanFormatError.Corrupt, Decode(corrupt));

        Assert.Equal(PlanFormatError.TooLarge, Decode(new byte[PlanLimits.MaxBundleBytes + 1]));
    }

    /// <summary>A section claiming more than 32 × its packed size (a zip bomb) is refused before anything is inflated.</summary>
    [Fact]
    public void Decode_RefusesInflateBombsBeforeInflating()
    {
        var bytes = PlanFormat.Encode(Sample());
        var bodyRaw = PlanHeader.SizeInBytes + 4;
        var packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bodyRaw + 4));

        var ratio = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(ratio.AsSpan(bodyRaw), packed * PlanLimits.MaxInflateRatio + 1);
        Assert.Equal(PlanFormatError.InflateRatio, Decode(ratio));

        var huge = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(bodyRaw), PlanLimits.MaxSectionRawBytes + 1);
        Assert.Equal(PlanFormatError.SectionTooLarge, Decode(huge));
    }

    private static PlanFormatError Decode(byte[] bytes)
    {
        PlanFormat.TryDecode(bytes, out _, out var error);
        return error;
    }
}
