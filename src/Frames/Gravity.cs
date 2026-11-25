using Brutal.Numerics;

namespace BrutalGame.Frames;

/// <summary>
/// Patched conics: only the dominant body pulls, the deepest one whose SOI contains the point. Lagrange zones (both
/// bodies pull) are not handled here yet.
/// </summary>
public static class Gravity
{
    /// <summary><c>-mu r / |r|^3</c> for <paramref name="offset"/> from the body's centre.</summary>
    public static double3 PointMass(double mu, double3 offset)
    {
        var r2 = offset.LengthSquared();
        return offset * (-mu / (r2 * Math.Sqrt(r2)));
    }

    /// <summary>Celestial index of the dominant body at <paramref name="position"/> in <paramref name="frame"/>; -1 in a tree with no star.</summary>
    public static int Dominant(CelestialSystem bodies, FrameId frame, double3 position)
    {
        var frames = bodies.Frames;
        var current = Star(bodies, frames.System[frame.Value]);

        if (current < 0 || !frames.TryRelative(frame, bodies[current].Inertial, out var pose))
            return -1;

        var point = pose.TransformPoint(position);
        var all = bodies.Bodies;
        var local = frames.Local;
        var descended = true;

        while (descended)
        {
            descended = false;

            for (var i = 0; i < all.Length; i++)
            {
                ref readonly var body = ref all[i];

                if (body.Parent != current)
                    continue;

                ref readonly var rail = ref local[body.Inertial.Value];
                var offset = doubleQuat.Conjugate(rail.Rotation) * (point - rail.Position);

                if (offset.LengthSquared() >= body.SoiRadius * body.SoiRadius)
                    continue;

                current = i;
                point = offset;
                descended = true;
                break;
            }
        }

        return current;
    }

    /// <summary>Gravity (m/s^2, in <paramref name="frame"/>'s axes) of the dominant body, or zero with none.</summary>
    public static double3 Sample(CelestialSystem bodies, FrameId frame, double3 position, out int body)
    {
        body = Dominant(bodies, frame, position);

        if (body < 0 || !bodies.Frames.TryRelative(frame, bodies[body].Inertial, out var pose))
            return double3.Zero;

        var acceleration = PointMass(bodies[body].Mu, pose.TransformPoint(position));
        return doubleQuat.Conjugate(pose.Rotation) * acceleration;
    }

    private static int Star(CelestialSystem bodies, SystemId system)
    {
        var all = bodies.Bodies;

        for (var i = 0; i < all.Length; i++)
        {
            if (all[i].Parent < 0 && all[i].Inertial == system.Root)
                return i;
        }

        return -1;
    }
}
