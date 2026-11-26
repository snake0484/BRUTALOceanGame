using System.Numerics;
using Brutal.Numerics;

namespace BrutalGame.Physics;

/// <summary>
/// Where a physics world sits on a planet (frames-and-zones "Surface bubbles"): a tangent frame in the planet's rotating
/// frame, origin on the sea sphere, +Y radially up there. Jolt works in bubble-local float metres. M2 keeps one bubble
/// with a fixed origin at the spawn, so float precision falls off with distance from it (1 mm at 10 km, 8 mm at 100 km);
/// rebasing in 32 m steps and several bubbles are M3 (frames-and-zones, review C4).
/// </summary>
public readonly struct PhysicsBubble
{
    public readonly double3 Origin;

    /// <summary>Bubble axes to planet axes.</summary>
    public readonly doubleQuat Axes;

    public PhysicsBubble(double3 origin, doubleQuat axes)
    {
        Origin = origin;
        Axes = axes;
    }

    /// <summary>The bubble at <paramref name="origin"/>, +Y along it from the planet centre, +X along the tangent part of <paramref name="towardX"/>.</summary>
    public static PhysicsBubble At(double3 origin, double3 towardX)
    {
        var up = double3.Normalize(origin);
        var x = double3.Normalize(towardX - up * double3.Dot(towardX, up));
        var z = double3.Cross(x, up);

        var axes = doubleQuat.Normalize(doubleQuat.CreateFromRotationMatrix(new double4x4(
            x.X, x.Y, x.Z, 0.0,
            up.X, up.Y, up.Z, 0.0,
            z.X, z.Y, z.Z, 0.0,
            0.0, 0.0, 0.0, 1.0)));

        return new PhysicsBubble(origin, axes);
    }

    /// <summary>Planet axes to bubble axes, as a float quaternion for rotating chunk-local vertices on jobs.</summary>
    public Quaternion PlanetToLocal
    {
        get
        {
            var q = doubleQuat.Conjugate(Axes);
            return new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
        }
    }

    public double3 LocalDouble(double3 planet) => doubleQuat.Conjugate(Axes) * (planet - Origin);

    public Vector3 ToLocal(double3 planet)
    {
        var local = LocalDouble(planet);
        return new Vector3((float)local.X, (float)local.Y, (float)local.Z);
    }

    public Vector3 ToLocalDirection(double3 direction)
    {
        var local = doubleQuat.Conjugate(Axes) * direction;
        return new Vector3((float)local.X, (float)local.Y, (float)local.Z);
    }

    public double3 ToPlanet(Vector3 local) => Origin + Axes * new double3(local.X, local.Y, local.Z);

    public double3 ToPlanet(double3 local) => Origin + Axes * local;

    public double3 ToPlanetDirection(Vector3 local) => Axes * new double3(local.X, local.Y, local.Z);

    public double3 ToPlanetDirection(double3 local) => Axes * local;
}
