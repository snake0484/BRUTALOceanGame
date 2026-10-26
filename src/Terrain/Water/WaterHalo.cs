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

    /// <summary>The flags and the bed rises (<see cref="WaterBlock.Bed"/>): both static between edits.</summary>
    public static void CopyFlags(WaterBlock dst, WaterBlock? src, Face face)
    {
        Copy(dst.Flags, src?.Flags, face, (byte)WaterFlags.Solid);
        Copy(dst.Bed, src?.Bed, face, WaterBlock.NotBed);
    }

    /// <summary>
    /// Pipe model: dst takes the fluxes of the faces it shares with src (on dst's <c>face</c> side), which stepped while dst
    /// did not, so both copies of every shared face agree again. A null <c>src</c> (no neighbour that kept stepping) zeroes
    /// them: a neighbour that slept holds none. A −side face is held in dst's halo slots and src's border cells, a +side
    /// face in dst's border cells and src's halo slots.
    /// </summary>
    public static void SyncFluxes(WaterBlock dst, WaterBlock? src, Face face)
    {
        var axis = (int)face >> 1;
        var to = axis == 0 ? dst.FluxX : axis == 1 ? dst.FluxY : dst.FluxZ;
        var from = src is null ? null : axis == 0 ? src.FluxX : axis == 1 ? src.FluxY : src.FluxZ;

        if (to is null)
            return;

        var negative = ((int)face & 1) == 0;
        WaterBlock.Plane(face, halo: negative, out var t, out var su, out var sv);
        WaterBlock.Plane((Face)((int)face ^ 1), halo: !negative, out var f, out _, out _);

        for (var v = 0; v < S; v++)
            for (var u = 0; u < S; u++)
                to[t + u * su + v * sv] = from is null ? 0f : from[f + u * su + v * sv];
    }

    /// <summary>
    /// Pipe model, before each step: the fall speeds and drifts of src's border cells into dst's halo plane on that side,
    /// for every face but −Y (falling water reaches a cell from above or beside, never from below). Dst's top cells read
    /// the water falling in from above; its faces read the water about to land beside them (<see cref="WaterPipes"/>).
    /// Zero without a neighbour.
    /// </summary>
    public static void CopyFalling(WaterBlock dst, WaterBlock? src, Face face)
    {
        if (dst.FallSpeed is null)
            return;

        Copy(dst.FallSpeed, src?.FallSpeed, face, 0f);
        Copy(dst.DriftX!, src?.DriftX, face, 0f);
        Copy(dst.DriftZ!, src?.DriftZ, face, 0f);
    }

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
