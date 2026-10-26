using System.Numerics;
using System.Runtime.InteropServices;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Where sea-zone water slams into something this tick, found by the zone sim; the ocean renderer throws spray there
/// (ocean_spray_splash.comp). Matches the GLSL WaterSplash in ocean_common.glsl.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WaterSplash
{
    public const float Wall = 0f;
    public const float Pour = 1f;

    /// <summary>
    /// xyz = where (world): a wall's foot on the water surface, or where a pour lands on the water below it;
    /// w = speed of the impact (m/s): into the wall, or of the fall.
    /// </summary>
    public Vector4 PositionSpeed;

    /// <summary>
    /// xy = unit direction (world x, z): into the wall, or the pour's along its flow; z = how much water: the depth at
    /// a wall (m), the discharge of a pour per metre of its lip (m²/s); w = kind (<see cref="Wall"/>, <see cref="Pour"/>).
    /// </summary>
    public Vector4 Direction;
}
