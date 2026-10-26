namespace BrutalGame.Terrain.Water;

/// <summary>
/// Copies a neighbour's border layer into a block's halo. <c>face</c> is the face of <c>dst</c> that <c>src</c> sits on.
/// Only the 32×32 face plane is copied; edge and corner halo cells are never read by the sim (see WaterFlow).
/// A null <c>src</c> means no active neighbour: Solid flags, zero water, unit out-scale.
/// </summary>
public static class WaterHalo
{
    private const int S = ChunkConstants.Size;

    public static void CopyWater(WaterBlock dst, WaterBlock? src, Face face) => Copy(dst.Water, src?.Water, face, 0f);

    public static void CopyOutScale(WaterBlock dst, WaterBlock? src, Face face) => Copy(dst.OutScale, src?.OutScale, face, 1f);

    public static void CopyFlags(WaterBlock dst, WaterBlock? src, Face face) => Copy(dst.Flags, src?.Flags, face, (byte)WaterFlags.Solid);

    /// <summary>Same-block copy of all six halo planes, used by Swap so the halo of the new state stays meaningful.</summary>
    internal static void CopyHaloPlanes(float[] from, float[] to)
    {
        for (var f = 0; f < 6; f++)
        {
            WaterBlock.Plane((Face)f, halo: true, out var start, out var su, out var sv);

            for (var v = 0; v < S; v++)
                for (var u = 0; u < S; u++)
                    to[start + u * su + v * sv] = from[start + u * su + v * sv];
        }
    }

    private static void Copy<T>(T[] dst, T[]? src, Face face, T empty)
    {
        WaterBlock.Plane(face, halo: true, out var to, out var su, out var sv);

        if (src is null)
        {
            for (var v = 0; v < S; v++)
                for (var u = 0; u < S; u++)
                    dst[to + u * su + v * sv] = empty;

            return;
        }

        WaterBlock.Plane((Face)((int)face ^ 1), halo: false, out var from, out _, out _);

        if (su == 1)
        {
            for (var v = 0; v < S; v++)
                src.AsSpan(from + v * sv, S).CopyTo(dst.AsSpan(to + v * sv, S));

            return;
        }

        for (var v = 0; v < S; v++)
            for (var u = 0; u < S; u++)
                dst[to + u * su + v * sv] = src[from + u * su + v * sv];
    }
}
