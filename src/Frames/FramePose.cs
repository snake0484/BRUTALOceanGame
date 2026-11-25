using Brutal.Numerics;

namespace BrutalGame.Frames;

/// <summary>Pose of a frame relative to its parent, refreshed once per tick (rails, spin) or after the physics step (grids).</summary>
public struct FramePose
{
    /// <summary>Origin in parent axes, m.</summary>
    public double3 Position;

    /// <summary>Child axes to parent axes.</summary>
    public doubleQuat Rotation;

    /// <summary>Origin velocity in parent, m/s.</summary>
    public double3 Velocity;

    /// <summary>Of the child axes, in parent axes, rad/s.</summary>
    public double3 AngularVelocity;

    public static FramePose Identity => new() { Rotation = doubleQuat.Identity };

    public readonly double3 TransformPoint(double3 point) => Position + Rotation * point;

    public readonly double3 TransformVector(double3 vector) => Rotation * vector;

    /// <summary>Parent-frame velocity of a point at <paramref name="point"/> moving at <paramref name="velocity"/> in the child.</summary>
    public readonly double3 TransformVelocity(double3 point, double3 velocity)
    {
        return Velocity + double3.Cross(AngularVelocity, Rotation * point) + Rotation * velocity;
    }

    /// <summary>Pose of X in A from <paramref name="outer"/> (B in A) and <paramref name="inner"/> (X in B).</summary>
    public static FramePose Compose(in FramePose outer, in FramePose inner)
    {
        var offset = outer.Rotation * inner.Position;

        return new FramePose
        {
            Position = outer.Position + offset,
            Rotation = outer.Rotation * inner.Rotation,
            Velocity = outer.Velocity + double3.Cross(outer.AngularVelocity, offset) + outer.Rotation * inner.Velocity,
            AngularVelocity = outer.AngularVelocity + outer.Rotation * inner.AngularVelocity,
        };
    }

    /// <summary>Pose of A in B from the pose of B in A. Rotations are unit, so the conjugate inverts them.</summary>
    public static FramePose Inverse(in FramePose pose)
    {
        var inverse = doubleQuat.Conjugate(pose.Rotation);

        return new FramePose
        {
            Position = -(inverse * pose.Position),
            Rotation = inverse,
            Velocity = inverse * (double3.Cross(pose.AngularVelocity, pose.Position) - pose.Velocity),
            AngularVelocity = -(inverse * pose.AngularVelocity),
        };
    }
}
