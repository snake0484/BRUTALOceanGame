using System.Numerics;

namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// How the focus body's impostor draws against its octree: the opacity of its ground in front of the octree (the overlay,
/// nearer by <paramref name="OverlayDepthScale"/>) and behind it (the backdrop, which shows wherever the octree has
/// nothing), and of the atmosphere rim.
/// </summary>
public readonly record struct FocusBlend(float Overlay, float Backdrop, float Rim, float OverlayDepthScale)
{
    /// <summary>No octree drawn: the impostor alone.</summary>
    public static FocusBlend Alone => new(1f, 0f, 1f, 1f);

    /// <summary>The impostor hides everything the octree has not loaded.</summary>
    public bool Covers => Overlay >= 1f || Backdrop >= 1f;
}

/// <summary>
/// The detail ladder's switches (planets.md "Detail ladder"), by a body's size on screen (diameter in pixels) and, for the
/// focus body, by altitude: point of light under ~2 px, crossfading to the disc by 3 px; the impostor's relief fading in
/// from 48 to 96 px; the impostor fading out over the octree between 900 and 700 km, then drawn behind it down to
/// 100 km, where the ground's own sky and fog take over by 20 km. Pure functions.
/// </summary>
public static class DetailLadder
{
    public const float PointFull = 1.5f;
    public const float DiscFull = 3f;
    public const float ReliefStart = 48f;
    public const float ReliefFull = 96f;

    /// <summary>Focus-body altitudes (m above the datum): the impostor is opaque above the high end and gone below the low one.</summary>
    public const double SwitchLow = 700_000.0;
    public const double SwitchHigh = 900_000.0;

    /// <summary>The octree streams below <see cref="OctreeTop"/> and unloads above <see cref="OctreeRelease"/>.</summary>
    public const double OctreeTop = 1_000_000.0;
    public const double OctreeRelease = 1_100_000.0;

    /// <summary>The impostor draws over the octree in the switch band by this depth scale (reverse-Z: nearer by ~3 %).</summary>
    public const float OverlayDepthScale = 1.03f;

    /// <summary>The backdrop draws behind the octree by this depth scale.</summary>
    public const float BackdropDepthScale = 0.75f;

    /// <summary>Camera altitudes (m) between which the octree's ground look (sky, fog) turns into the impostor's; the backdrop and rim fade in with it.</summary>
    public const double SpaceLow = 20_000.0;
    public const double SpaceHigh = 100_000.0;

    /// <summary>The point sprite's area in pixels: a Gaussian of σ = 0.6 px (body_point.frag).</summary>
    public const float SpriteArea = 2f * MathF.PI * 0.36f;

    /// <summary>Faint points keep at least this display brightness until their light is 1e−6 of a lit pixel.</summary>
    public const float PointFloor = 0.15f;

    private static readonly Vector3 Luma = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>Diameter in pixels of a sphere of <paramref name="radius"/> at <paramref name="distance"/> from the camera.</summary>
    public static float PixelDiameter(double radius, double distance, double radiansPerPixel) =>
        (float)(2.0 * Math.Asin(Math.Min(radius / Math.Max(distance, radius), 1.0)) / radiansPerPixel);

    public static float PointWeight(float diameter) => 1f - Smooth(PointFull, DiscFull, diameter);

    public static float Relief(float diameter) => Smooth(ReliefStart, ReliefFull, diameter);

    /// <summary>The focus body's impostor opacity over its octree at <paramref name="altitude"/> (m above the datum).</summary>
    public static float ImpostorFade(double altitude) => Smooth((float)SwitchLow, (float)SwitchHigh, (float)altitude);

    /// <summary>
    /// The focus body's impostor over its octree at <paramref name="altitude"/>. <paramref name="octreeCover"/> is 0 until
    /// the octree has loaded round the camera and 1 once it may show: until then the impostor stays in front of it (as
    /// far as <see cref="SpaceLook"/> lets the impostor stand for the ground: near the ground a loading octree shows).
    /// Across the switch band the overlay fades out; once it is not opaque the backdrop draws behind the octree (gaps and
    /// the limb past the octree's edge show the impostor, never the sky), fading out with <see cref="SpaceLook"/>.
    /// </summary>
    public static FocusBlend Blend(double altitude, float octreeCover)
    {
        var space = SpaceLook(altitude);
        var hold = (1f - Math.Clamp(octreeCover, 0f, 1f)) * space;
        var overlay = 1f - (1f - ImpostorFade(altitude)) * (1f - hold);
        return new FocusBlend(overlay, overlay < 1f ? space : 0f, space, OverlayDepthScale);
    }

    /// <summary>How far the focus body's terrain is shaded as its impostor at <paramref name="altitude"/>: 0 on the ground, 1 from orbit.</summary>
    public static float SpaceLook(double altitude) => Smooth((float)SpaceLow, (float)SpaceHigh, (float)altitude);

    /// <summary>A Lambert sphere's brightness at phase angle α (cos α given), 1 at full phase.</summary>
    public static float LambertPhase(float cosPhase)
    {
        var alpha = MathF.Acos(Math.Clamp(cosPhase, -1f, 1f));
        return (MathF.Sin(alpha) + (MathF.PI - alpha) * MathF.Cos(alpha)) / MathF.PI;
    }

    /// <summary>
    /// A body's point of light in display space from its disc-averaged radiance: its light spread over the sprite
    /// (π (D/2)² / sprite area), with a floor so faint bodies stay visible, then the shaders' tonemap.
    /// </summary>
    public static Vector3 PointColour(Vector3 radiance, float diameter)
    {
        var hdr = radiance * (MathF.PI * 0.25f * diameter * diameter / SpriteArea);
        var luma = Vector3.Dot(hdr, Luma);

        if (luma <= 0f)
            return Vector3.Zero;

        var floor = PointFloor * Math.Clamp((MathF.Log10(luma) + 6f) / 5f, 0f, 1f);

        if (luma < floor)
            hdr *= floor / luma;

        return Display(hdr);
    }

    /// <summary>The shaders' tonemap: ACES fit, then gamma 2.2.</summary>
    public static Vector3 Display(Vector3 x)
    {
        var a = x * (2.51f * x + new Vector3(0.03f));
        var b = x * (2.43f * x + new Vector3(0.59f)) + new Vector3(0.14f);
        var c = Vector3.Clamp(a / b, Vector3.Zero, Vector3.One);
        return new Vector3(MathF.Pow(c.X, 1f / 2.2f), MathF.Pow(c.Y, 1f / 2.2f), MathF.Pow(c.Z, 1f / 2.2f));
    }

    private static float Smooth(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
