using BrutalGame.Frames;
using BrutalGame.Sim.Entities;

namespace BrutalGame.Net;

public enum InterestClass : byte
{
    None,
    Owned,

    /// <summary>The viewer's own bubble: full rate.</summary>
    Bubble,

    /// <summary>Same frame within 20 km: 10 Hz.</summary>
    Frame,

    /// <summary>Same SOI: rails or 1 Hz.</summary>
    Soi,

    /// <summary>Other SOIs in the system: radar contacts at 0.2 Hz.</summary>
    System,
}

/// <summary>
/// Interest by frame tree (docs/pivot/frames-and-zones.md, Networking). Stub: bubbles don't exist yet, so "own bubble"
/// is the same frame within <see cref="BubbleRadius"/>; other frames in the same SOI fall to the SOI rate; rails
/// elements and radar contacts are sent as plain body states at their rates. Other star systems send nothing.
/// </summary>
public static class Interest
{
    public const double BubbleRadius = 2000.0;
    public const double FrameRadius = 20000.0;

    /// <summary>Ticks between updates at 60 Hz; snapshots go out every 2 ticks, so Owned and Bubble are every snapshot.</summary>
    public static int PeriodTicks(InterestClass interest) => interest switch
    {
        InterestClass.Owned or InterestClass.Bubble => 2,
        InterestClass.Frame => 6,
        InterestClass.Soi => 60,
        InterestClass.System => 300,
        _ => int.MaxValue,
    };

    public static InterestClass Classify(FrameTree frames, in Placement viewer, in Placement target)
    {
        var a = viewer.Frame;
        var b = target.Frame;

        if (!a.IsValid || !b.IsValid || frames.System[a.Value] != frames.System[b.Value])
            return InterestClass.None;

        if (a == b)
        {
            var distanceSquared = (target.Position - viewer.Position).LengthSquared();

            if (distanceSquared <= BubbleRadius * BubbleRadius)
                return InterestClass.Bubble;

            return distanceSquared <= FrameRadius * FrameRadius ? InterestClass.Frame : InterestClass.Soi;
        }

        return PlacementFrames.Soi(frames, a) == PlacementFrames.Soi(frames, b) ? InterestClass.Soi : InterestClass.System;
    }
}
