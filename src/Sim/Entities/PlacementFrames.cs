using BrutalGame.Frames;

namespace BrutalGame.Sim.Entities;

public static class PlacementFrames
{
    /// <summary>
    /// Re-expresses <paramref name="placement"/> in <paramref name="frame"/> at the tick's poses, keeping it where it is and
    /// moving as it moves. False across star systems (no relative pose).
    /// </summary>
    public static bool TryMoveTo(FrameTree frames, ref Placement placement, FrameId frame)
    {
        if (placement.Frame == frame)
            return true;

        if (!frames.TryRelative(placement.Frame, frame, out var pose))
            return false;

        placement.Velocity = pose.TransformVelocity(placement.Position, placement.Velocity);
        placement.Position = pose.TransformPoint(placement.Position);
        placement.Rotation = pose.Rotation * placement.Rotation;
        placement.AngularVelocity = VectorCast.ToFloat(pose.Rotation * VectorCast.ToDouble(placement.AngularVelocity) + pose.AngularVelocity);
        placement.Frame = frame;
        return true;
    }

    /// <summary>The body whose sphere of influence holds <paramref name="frame"/>: its nearest BodyInertial ancestor, else its root.</summary>
    public static FrameId Soi(FrameTree frames, FrameId frame)
    {
        var id = frame;

        while (frames.Kind[id.Value] != FrameKind.BodyInertial)
        {
            var parent = frames.Parent[id.Value];

            if (parent == id)
                return id;

            id = parent;
        }

        return id;
    }
}
