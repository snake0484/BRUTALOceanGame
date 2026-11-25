using System.Numerics;
using Brutal.Numerics;
using BrutalGame.Frames;

namespace BrutalGame;

/// <summary>
/// Where the island placeholder sits on the test planet until M2 retires it: a tangent frame on the north pole (+Y) of
/// the planet's surface frame at sea level, with the surface frame's axes, so it is a pure offset. Terrain, physics, ocean
/// and weather keep their float island coordinates; the camera lives in the surface frame in double, about 1e6 m out.
/// </summary>
public sealed class IslandFrame
{
    public readonly FrameId Frame;
    public readonly double3 Origin;

    /// <summary>The planet's SOI (m from its centre, the frame's origin): past it the island is not drawn.</summary>
    public readonly double SoiRadius;

    /// <summary>Island axes to <see cref="Frame"/> axes: the same axes.</summary>
    public static doubleQuat Axes => doubleQuat.Identity;

    public IslandFrame(CelestialSystem celestial)
    {
        foreach (ref readonly var body in celestial.Bodies)
        {
            if (body.Kind != CelestialKind.Planet)
                continue;

            Frame = body.Surface;
            Origin = new double3(0.0, body.Radius, 0.0);
            SoiRadius = body.SoiRadius;
            return;
        }

        throw new InvalidOperationException("The celestial system has no planet for the island.");
    }

    /// <summary>A point in <see cref="Frame"/> as island coordinates.</summary>
    public Vector3 ToIsland(double3 point)
    {
        var local = point - Origin;
        return new Vector3((float)local.X, (float)local.Y, (float)local.Z);
    }

    /// <summary>A direction in <see cref="Frame"/> as island axes (the same axes).</summary>
    public static Vector3 ToIslandDirection(double3 direction) => new((float)direction.X, (float)direction.Y, (float)direction.Z);

    /// <summary>An island point in <see cref="Frame"/>.</summary>
    public double3 ToFrame(Vector3 point) => Origin + new double3(point.X, point.Y, point.Z);
}
