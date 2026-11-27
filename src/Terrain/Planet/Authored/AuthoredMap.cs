using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrutalGame.Terrain;

public enum AuthoredBrush : byte
{
    Raise,
    Level,
    Erase,
}

/// <summary>
/// A painted cube-sphere map of one authored channel (planet-designer-brief.md §2): per face a (Res + 1)² vertex grid of
/// int16 codes times <see cref="Scale"/>, gnomonic (u, v) = the two minor components over the major one. A point picks
/// its face from its position alone, so every chunk and peer reads the same vertices; edge vertices are kept equal on
/// both faces (<see cref="Set"/> writes every twin), so values are continuous across cube edges. Code 0 is exactly 0.
/// A face nobody painted holds no array. Bounds come from per-tile min/max (<see cref="Freeze"/>).
/// </summary>
public sealed class AuthoredMap
{
    public const int Faces = 6;
    public const int Tile = 32;
    public const uint Magic = 0x50414D42u;
    public const int FormatVersion = 1;

    public readonly string Name;
    public readonly int Res;
    public readonly float Scale;
    public readonly int Side;
    public readonly int TilesPerSide;

    private readonly short[]?[] _faces = new short[Faces][];
    private readonly short[]?[] _tileMin = new short[Faces][];
    private readonly short[]?[] _tileMax = new short[Faces][];
    private readonly short[] _faceMin = new short[Faces];
    private readonly short[] _faceMax = new short[Faces];
    private readonly double _half;
    private bool _frozen;

    public AuthoredMap(string name, int res, float scale)
    {
        if (res < Tile || (res & (res - 1)) != 0)
            throw new InvalidDataException($"Authored map {name}: res {res} must be a power of two of at least {Tile}");

        if (!(scale > 0f))
            throw new InvalidDataException($"Authored map {name}: scale must be positive");

        Name = name;
        Res = res;
        Scale = scale;
        Side = res + 1;
        TilesPerSide = res / Tile + 1;
        _half = 0.5 * res;
    }

    public bool IsEmpty
    {
        get
        {
            foreach (var face in _faces)
            {
                if (face is not null)
                    return false;
            }

            return true;
        }
    }

    public ReadOnlySpan<short> FaceCodes(int face) => _faces[face];

    public short Code(int face, int i, int j) => _faces[face] is { } f ? f[j * Side + i] : (short)0;

    public float Value(int face, int i, int j) => Code(face, i, j) * Scale;

    public short Encode(float metres) => (short)Math.Clamp(MathF.Round(metres / Scale), short.MinValue, short.MaxValue);

    /// <summary>The unnormalised direction of a vertex: ±1 on the face's axis, the vertex's (u, v) on the other two.</summary>
    public void VertexDirection(int face, int i, int j, out double x, out double y, out double z)
    {
        var u = i / _half - 1.0;
        var v = j / _half - 1.0;
        var s = (face & 1) == 0 ? 1.0 : -1.0;

        (x, y, z) = (face >> 1) switch
        {
            0 => (s, u, v),
            1 => (u, s, v),
            _ => (u, v, s),
        };
    }

    /// <summary>Writes a vertex and every copy of it on the neighbouring faces.</summary>
    public void Set(int face, int i, int j, short code)
    {
        if (_frozen)
            throw new InvalidOperationException($"Authored map {Name} is frozen; clone it to edit");

        Write(face, i, j, code);

        if (i != 0 && i != Res && j != 0 && j != Res)
            return;

        VertexDirection(face, i, j, out var x, out var y, out var z);
        Span<double> c = [x, y, z];

        for (var axis = 0; axis < 3; axis++)
        {
            if (axis == face >> 1 || Math.Abs(c[axis]) != 1.0)
                continue;

            var other = axis * 2 + (c[axis] > 0 ? 0 : 1);
            var (a, b) = Minor(axis, c[0], c[1], c[2]);
            Write(other, (int)Math.Round((a + 1.0) * _half), (int)Math.Round((b + 1.0) * _half), code);
        }
    }

    private void Write(int face, int i, int j, short code)
    {
        var f = _faces[face];

        if (f is null)
        {
            if (code == 0)
                return;

            _faces[face] = f = new short[Side * Side];
        }

        f[j * Side + i] = code;
    }

    private static (double A, double B) Minor(int axis, double x, double y, double z) => axis switch
    {
        0 => (y, z),
        1 => (x, z),
        _ => (x, y),
    };

    /// <summary>The face a direction falls on and its gnomonic (u, v) in [−1, 1]; ties go to x, then y.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Project(double x, double y, double z, out double u, out double v)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);

        if (ax >= ay && ax >= az)
        {
            var inv = 1.0 / ax;
            (u, v) = (y * inv, z * inv);
            return x >= 0.0 ? 0 : 1;
        }

        if (ay >= az)
        {
            var inv = 1.0 / ay;
            (u, v) = (x * inv, z * inv);
            return y >= 0.0 ? 2 : 3;
        }

        var invZ = 1.0 / az;
        (u, v) = (x * invZ, y * invZ);
        return z >= 0.0 ? 4 : 5;
    }

    /// <summary>The bilinear value at a point (any length, not the origin).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Sample(double x, double y, double z)
    {
        var face = Project(x, y, z, out var u, out var v);
        var f = _faces[face];

        if (f is null)
            return 0f;

        var fi = (u + 1.0) * _half;
        var fj = (v + 1.0) * _half;
        var i = Math.Min((int)fi, Res - 1);
        var j = Math.Min((int)fj, Res - 1);
        var fu = (float)(fi - i);
        var fv = (float)(fj - j);
        var k = j * Side + i;
        float h00 = f[k], h10 = f[k + 1], h01 = f[k + Side], h11 = f[k + Side + 1];
        var gu = 1f - fu;
        return ((h00 * gu + h10 * fu) * (1f - fv) + (h01 * gu + h11 * fu) * fv) * Scale;
    }

    /// <summary>
    /// Rigorous bounds of the map over every point within <paramref name="reach"/> (m) of q: per face the ball's box
    /// projected to a (u, v) rectangle, one vertex of slack, then the min/max of the tiles it touches (bilinear stays in
    /// the hull of its corners). Needs <see cref="Freeze"/>.
    /// </summary>
    public void Bounds(double qx, double qy, double qz, double reach, out float lo, out float hi)
    {
        if (!_frozen)
            throw new InvalidOperationException($"Authored map {Name} must be frozen before bounds");

        int cLo = int.MaxValue, cHi = int.MinValue;
        Span<double> boxLo = [qx - reach, qy - reach, qz - reach];
        Span<double> boxHi = [qx + reach, qy + reach, qz + reach];

        for (var face = 0; face < Faces; face++)
        {
            if (!FaceRect(face, boxLo, boxHi, out var iLo, out var iHi, out var jLo, out var jHi))
                continue;

            if (_faces[face] is null)
            {
                cLo = Math.Min(cLo, 0);
                cHi = Math.Max(cHi, 0);
                continue;
            }

            int tx0 = iLo / Tile, tx1 = iHi / Tile, ty0 = jLo / Tile, ty1 = jHi / Tile;

            if ((tx1 - tx0 + 1) * (ty1 - ty0 + 1) > 16)
            {
                cLo = Math.Min(cLo, _faceMin[face]);
                cHi = Math.Max(cHi, _faceMax[face]);
                continue;
            }

            var mins = _tileMin[face]!;
            var maxs = _tileMax[face]!;

            for (var ty = ty0; ty <= ty1; ty++)
            {
                for (var tx = tx0; tx <= tx1; tx++)
                {
                    cLo = Math.Min(cLo, mins[ty * TilesPerSide + tx]);
                    cHi = Math.Max(cHi, maxs[ty * TilesPerSide + tx]);
                }
            }
        }

        if (cLo > cHi)
            cLo = cHi = 0;

        lo = cLo * Scale;
        hi = cHi * Scale;
    }

    /// <summary>The vertex rectangle of a face that points in the box can fall on (one vertex of slack); false if none.</summary>
    private bool FaceRect(int face, ReadOnlySpan<double> boxLo, ReadOnlySpan<double> boxHi, out int iLo, out int iHi, out int jLo, out int jHi)
    {
        var axis = face >> 1;
        var positive = (face & 1) == 0;
        double mLo = positive ? boxLo[axis] : -boxHi[axis], mHi = positive ? boxHi[axis] : -boxLo[axis];
        (iLo, iHi, jLo, jHi) = (0, Res, 0, Res);

        if (mHi <= 0.0)
            return false;

        if (mLo <= 0.0)
            return true;

        var (a, b) = axis switch { 0 => (1, 2), 1 => (0, 2), _ => (0, 1) };
        var uLo = Math.Max(Math.Min(boxLo[a] / mLo, boxLo[a] / mHi), -1.0);
        var uHi = Math.Min(Math.Max(boxHi[a] / mLo, boxHi[a] / mHi), 1.0);
        var vLo = Math.Max(Math.Min(boxLo[b] / mLo, boxLo[b] / mHi), -1.0);
        var vHi = Math.Min(Math.Max(boxHi[b] / mLo, boxHi[b] / mHi), 1.0);

        if (uLo > uHi || vLo > vHi)
            return false;

        iLo = Math.Max((int)Math.Floor((uLo + 1.0) * _half) - 1, 0);
        iHi = Math.Min((int)Math.Ceiling((uHi + 1.0) * _half) + 1, Res);
        jLo = Math.Max((int)Math.Floor((vLo + 1.0) * _half) - 1, 0);
        jHi = Math.Min((int)Math.Ceiling((vHi + 1.0) * _half) + 1, Res);
        return true;
    }

    /// <summary>
    /// One brush dab about unit direction d: every vertex within <paramref name="radius"/> (chord on the unit sphere) moves
    /// by the smooth falloff weight: <see cref="AuthoredBrush.Raise"/> adds <paramref name="metres"/>, <see cref="AuthoredBrush.Level"/>
    /// eases toward it, <see cref="AuthoredBrush.Erase"/> toward 0. Returns how many vertices changed.
    /// </summary>
    public int Paint(double dx, double dy, double dz, double radius, float metres, AuthoredBrush brush, float strength = 1f)
    {
        Span<double> boxLo = [dx - radius, dy - radius, dz - radius];
        Span<double> boxHi = [dx + radius, dy + radius, dz + radius];
        var changed = 0;

        for (var face = 0; face < Faces; face++)
        {
            if (!FaceRect(face, boxLo, boxHi, out var iLo, out var iHi, out var jLo, out var jHi))
                continue;

            for (var j = jLo; j <= jHi; j++)
            {
                for (var i = iLo; i <= iHi; i++)
                {
                    VertexDirection(face, i, j, out var x, out var y, out var z);
                    var l = 1.0 / Math.Sqrt(x * x + y * y + z * z);
                    double ex = x * l - dx, ey = y * l - dy, ez = z * l - dz;
                    var d = Math.Sqrt(ex * ex + ey * ey + ez * ez) / radius;

                    if (d >= 1.0)
                        continue;

                    var w = strength * (1f - LayerOps.Smooth01((float)d));
                    var now = Value(face, i, j);
                    var next = brush switch
                    {
                        AuthoredBrush.Raise => now + w * metres,
                        AuthoredBrush.Level => now + w * (metres - now),
                        _ => now - w * now,
                    };
                    var code = Encode(next);

                    if (code == Code(face, i, j))
                        continue;

                    Set(face, i, j, code);
                    changed++;
                }
            }
        }

        return changed;
    }

    /// <summary>Global min and max (m).</summary>
    public void Range(out float lo, out float hi) => Bounds(0.0, 0.0, 0.0, 4.0, out lo, out hi);

    /// <summary>Builds the tile ranges and forbids further writes: the evaluator and its jobs share a frozen map.</summary>
    public AuthoredMap Freeze()
    {
        if (_frozen)
            return this;

        for (var face = 0; face < Faces; face++)
        {
            if (_faces[face] is not { } f)
                continue;

            var mins = _tileMin[face] = new short[TilesPerSide * TilesPerSide];
            var maxs = _tileMax[face] = new short[TilesPerSide * TilesPerSide];
            short faceLo = short.MaxValue, faceHi = short.MinValue;

            for (var ty = 0; ty < TilesPerSide; ty++)
            {
                for (var tx = 0; tx < TilesPerSide; tx++)
                {
                    short lo = short.MaxValue, hi = short.MinValue;

                    // A tile owns cells [32t, 32t + 32): its vertices run to 32t + 32 inclusive.
                    for (var j = ty * Tile; j <= Math.Min(ty * Tile + Tile, Res); j++)
                    {
                        for (var i = tx * Tile; i <= Math.Min(tx * Tile + Tile, Res); i++)
                        {
                            var c = f[j * Side + i];
                            lo = Math.Min(lo, c);
                            hi = Math.Max(hi, c);
                        }
                    }

                    mins[ty * TilesPerSide + tx] = lo;
                    maxs[ty * TilesPerSide + tx] = hi;
                    faceLo = Math.Min(faceLo, lo);
                    faceHi = Math.Max(faceHi, hi);
                }
            }

            _faceMin[face] = faceLo;
            _faceMax[face] = faceHi;
        }

        _frozen = true;
        return this;
    }

    public AuthoredMap Clone()
    {
        var copy = new AuthoredMap(Name, Res, Scale);

        for (var face = 0; face < Faces; face++)
            copy._faces[face] = _faces[face] is { } f ? (short[])f.Clone() : null;

        return copy;
    }

    /// <summary>Copies the 32² vertices tile (tx, ty) of a face starts (clipped at the face edge, zeros beyond) into <paramref name="codes"/>.</summary>
    public void ReadTile(int face, int tx, int ty, Span<short> codes)
    {
        codes.Clear();

        if (_faces[face] is not { } f)
            return;

        for (var j = 0; j < Tile && ty * Tile + j <= Res; j++)
        {
            for (var i = 0; i < Tile && tx * Tile + i <= Res; i++)
                codes[j * Tile + i] = f[(ty * Tile + j) * Side + tx * Tile + i];
        }
    }

    /// <summary>Writes a tile's vertices as they are (no twins: a tile restored from <see cref="ReadTile"/> on every face restores the map).</summary>
    public void WriteTile(int face, int tx, int ty, ReadOnlySpan<short> codes)
    {
        if (_frozen)
            throw new InvalidOperationException($"Authored map {Name} is frozen; clone it to edit");

        var f = _faces[face];

        if (f is null)
        {
            if (!codes.ContainsAnyExcept((short)0))
                return;

            _faces[face] = f = new short[Side * Side];
        }

        for (var j = 0; j < Tile && ty * Tile + j <= Res; j++)
        {
            for (var i = 0; i < Tile && tx * Tile + i <= Res; i++)
                f[(ty * Tile + j) * Side + tx * Tile + i] = codes[j * Tile + i];
        }
    }

    /// <summary>Drops faces that hold only zeros, so an erased map is empty again.</summary>
    public void Compact()
    {
        for (var face = 0; face < Faces; face++)
        {
            if (_faces[face] is { } f && !f.AsSpan().ContainsAnyExcept((short)0))
                _faces[face] = null;
        }
    }

    /// <summary>
    /// The .bmap file: magic, format version, res, scale, tile count, then per non-zero tile its face, tx, ty and the
    /// 32² vertices it starts (rows of j, clipped at the face edge), little endian.
    /// </summary>
    public byte[] Write()
    {
        using var stream = new MemoryStream();
        Span<byte> head = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(head[8..], Res);
        BinaryPrimitives.WriteSingleLittleEndian(head[12..], Scale);
        var countAt = 16;
        stream.Write(head);
        var count = 0;
        Span<byte> tileHead = stackalloc byte[5];
        Span<short> codes = stackalloc short[Tile * Tile];

        for (var face = 0; face < Faces; face++)
        {
            if (_faces[face] is not { } f)
                continue;

            for (var ty = 0; ty < TilesPerSide; ty++)
            {
                for (var tx = 0; tx < TilesPerSide; tx++)
                {
                    codes.Clear();
                    var any = false;

                    for (var j = 0; j < Tile && ty * Tile + j <= Res; j++)
                    {
                        for (var i = 0; i < Tile && tx * Tile + i <= Res; i++)
                        {
                            var c = f[(ty * Tile + j) * Side + tx * Tile + i];
                            codes[j * Tile + i] = c;
                            any |= c != 0;
                        }
                    }

                    if (!any)
                        continue;

                    tileHead[0] = (byte)face;
                    BinaryPrimitives.WriteUInt16LittleEndian(tileHead[1..], (ushort)tx);
                    BinaryPrimitives.WriteUInt16LittleEndian(tileHead[3..], (ushort)ty);
                    stream.Write(tileHead);

                    if (!BitConverter.IsLittleEndian)
                        BinaryPrimitives.ReverseEndianness(codes, codes);

                    stream.Write(MemoryMarshal.AsBytes(codes));
                    count++;
                }
            }
        }

        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(countAt), count);
        return bytes;
    }

    public static AuthoredMap Read(string name, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            throw new InvalidDataException($"Authored map {name}: not a .bmap file");

        if (BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != FormatVersion)
            throw new InvalidDataException($"Authored map {name}: format {BinaryPrimitives.ReadInt32LittleEndian(bytes[4..])} is not {FormatVersion}");

        var map = new AuthoredMap(name, BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]));
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        const int tileBytes = 5 + Tile * Tile * 2;

        if (count < 0 || bytes.Length != 20 + count * tileBytes)
            throw new InvalidDataException($"Authored map {name}: {bytes.Length} bytes do not hold {count} tiles");

        var at = 20;

        for (var t = 0; t < count; t++, at += tileBytes)
        {
            int face = bytes[at], tx = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 1)..]), ty = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 3)..]);

            if (face >= Faces || tx >= map.TilesPerSide || ty >= map.TilesPerSide)
                throw new InvalidDataException($"Authored map {name}: tile {t} out of range");

            var f = map._faces[face] ??= new short[map.Side * map.Side];

            for (var j = 0; j < Tile && ty * Tile + j <= map.Res; j++)
            {
                for (var i = 0; i < Tile && tx * Tile + i <= map.Res; i++)
                    f[(ty * Tile + j) * map.Side + tx * Tile + i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(at + 5 + 2 * (j * Tile + i))..]);
            }
        }

        return map;
    }
}
