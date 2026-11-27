namespace BrutalGame.Ships;

public enum ProjectorKind : byte { Paint = 1, Decal = 2, Fill = 3 }

/// <summary>
/// A box that colours the hull and its flair where they pass through it, projected along <see cref="N"/>: a piece of a paint
/// stroke's band, a decal (an image, or a shape filled with a paint slot), or a fill: a brush stroke's own paint over its
/// flair (whatever way the surface faces). Ship space, metres. Drawn only: it changes no shape, mass or collision. Deterministic from
/// the plan, like the flair.
/// </summary>
public struct HullProjector
{
    public ProjectorKind Kind;
    public Vector3d Centre;

    /// <summary>Unit axes: U along (paint: the stroke; decal: the image's right), V across (decal: up), N out of the hull.</summary>
    public Vector3d U, V, N;

    public double HalfU, HalfV, HalfN;

    /// <summary>Colour as sRGB bytes, R in the low byte, alpha (opacity) in the high one.</summary>
    public uint Colour;

    /// <summary>Paint: the distance along the stroke at <see cref="Centre"/>, metres (keeps the pattern running through the pieces).</summary>
    public double Along;

    /// <summary>Paint: the pattern's line spacing and width, metres.</summary>
    public float Spacing, LineWidth;

    public PaintPattern Pattern;

    /// <summary>Decal: the image's layer in the decal texture array.</summary>
    public int Layer;

    /// <summary>Decal: an image or a shape (filled with <see cref="Paint"/>).</summary>
    public DecalShape Shape;

    /// <summary>A shape's or fill's paint slot.</summary>
    public byte Paint;

    /// <summary>The box's ship-space bounds.</summary>
    public readonly void Bounds(out Vector3d min, out Vector3d max)
    {
        var ex = Math.Abs(U.X) * HalfU + Math.Abs(V.X) * HalfV + Math.Abs(N.X) * HalfN;
        var ey = Math.Abs(U.Y) * HalfU + Math.Abs(V.Y) * HalfV + Math.Abs(N.Y) * HalfN;
        var ez = Math.Abs(U.Z) * HalfU + Math.Abs(V.Z) * HalfV + Math.Abs(N.Z) * HalfN;
        min = new Vector3d(Centre.X - ex, Centre.Y - ey, Centre.Z - ez);
        max = new Vector3d(Centre.X + ex, Centre.Y + ey, Centre.Z + ez);
    }
}
