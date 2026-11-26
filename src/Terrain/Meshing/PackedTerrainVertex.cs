using System.Numerics;
using System.Runtime.InteropServices;

namespace BrutalGame.Terrain;

/// <summary>
/// The GPU terrain vertex (terrain.vert). Position: chunk-local voxel units as 16-bit fixed point, 1/1024 voxel steps from
/// <see cref="OffsetVoxels"/> voxels below the chunk origin, so it spans [-16, 48) voxels (the chunk plus its apron and
/// skirts). The mesher snaps surface vertices to 1/256 voxel, so those round-trip exactly; only skirt vertices round.
/// Normal: octahedral, 2 × snorm16. Material: the palette index, plus <see cref="SeaColumn"/> on planet terrain.
/// Collision keeps the full-precision <see cref="TerrainVertex"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PackedTerrainVertex
{
    public const int SizeInBytes = 12;
    public const int StepsPerVoxel = 1024;
    public const int OffsetVoxels = 16;
    public const float InvStepsPerVoxel = 1f / StepsPerVoxel;
    private const float SnormMax = 32767f;

    /// <summary>
    /// Material bit of a vertex in the generated sea mask (terrain.frag): only there is ground under the sea radius under
    /// water, so a pit dug inland below sea level stays dry (planets.md "pit rule").
    /// </summary>
    public const uint SeaColumn = 1u << 15;

    /// <summary>The material's palette index.</summary>
    public const uint MaterialMask = SeaColumn - 1;

    public ushort X;
    public ushort Y;
    public ushort Z;
    public ushort Material;
    public short NormalU;
    public short NormalV;

    /// <param name="invScale">1 / the chunk's voxel size.</param>
    public static PackedTerrainVertex Encode(in TerrainVertex v, float invScale)
    {
        var n = v.Normal;
        var sum = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        n = sum > 1e-20f ? n / sum : Vector3.UnitY;
        float u = n.X, w = n.Y;

        if (n.Z < 0f)
        {
            u = (1f - MathF.Abs(n.Y)) * (n.X >= 0f ? 1f : -1f);
            w = (1f - MathF.Abs(n.X)) * (n.Y >= 0f ? 1f : -1f);
        }

        return new PackedTerrainVertex
        {
            X = Fixed(v.Position.X, invScale),
            Y = Fixed(v.Position.Y, invScale),
            Z = Fixed(v.Position.Z, invScale),
            Material = (ushort)v.Material,
            NormalU = (short)MathF.Round(Math.Clamp(u, -1f, 1f) * SnormMax),
            NormalV = (short)MathF.Round(Math.Clamp(w, -1f, 1f) * SnormMax),
        };
    }

    /// <summary>What terrain.vert reconstructs, in chunk-local metres.</summary>
    public readonly Vector3 DecodePosition(float voxelScale)
    {
        var step = voxelScale * InvStepsPerVoxel;
        const float offset = OffsetVoxels * StepsPerVoxel;
        return new Vector3((X - offset) * step, (Y - offset) * step, (Z - offset) * step);
    }

    /// <summary>What terrain.vert reconstructs, normalised.</summary>
    public readonly Vector3 DecodeNormal()
    {
        var u = MathF.Max(NormalU / SnormMax, -1f);
        var w = MathF.Max(NormalV / SnormMax, -1f);
        var n = new Vector3(u, w, 1f - MathF.Abs(u) - MathF.Abs(w));
        var t = MathF.Max(-n.Z, 0f);
        n.X += n.X >= 0f ? -t : t;
        n.Y += n.Y >= 0f ? -t : t;
        return Vector3.Normalize(n);
    }

    private static ushort Fixed(float metres, float invScale)
    {
        var steps = MathF.Round((metres * invScale + OffsetVoxels) * StepsPerVoxel);
        return (ushort)Math.Clamp(steps, 0f, ushort.MaxValue);
    }
}
