namespace BrutalGame.Frames;

/// <summary>
/// The frame forest: one tree per star system (Solar roots) plus wormhole tunnel roots, all in one preallocated SoA
/// store indexed by <see cref="FrameId.Value"/>. <see cref="Local"/> is the per-tick pose cache: rails and spin write it
/// once per tick (<see cref="CelestialSystem.Update"/>), grids after the physics step. Zero allocations after
/// construction. Main-thread only.
/// </summary>
public sealed class FrameTree
{
    public const int DefaultCapacity = 4096;

    public readonly FrameId[] Parent;
    public readonly FrameKind[] Kind;
    public readonly byte[] Depth;

    /// <summary>Root of each frame's tree; the Parent of a root is itself.</summary>
    public readonly SystemId[] System;

    public readonly FramePose[] Local;

    /// <summary>Grid: ship entity; body frames: celestial index; Lagrange: LagrangePoint index.</summary>
    public readonly int[] OwnerEntity;

    public FrameTree(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, ushort.MaxValue);

        Parent = new FrameId[capacity];
        Kind = new FrameKind[capacity];
        Depth = new byte[capacity];
        System = new SystemId[capacity];
        Local = new FramePose[capacity];
        OwnerEntity = new int[capacity];
    }

    public int Count { get; private set; }

    public int Capacity => Parent.Length;

    /// <summary>A new tree: a Solar root (one per star system) or a wormhole Tunnel, which belongs to no system.</summary>
    public FrameId AddRoot(FrameKind kind, int owner = -1)
    {
        if (kind is not (FrameKind.Solar or FrameKind.Tunnel))
            throw new ArgumentException($"{kind} can't be a root", nameof(kind));

        var id = Next();
        Parent[id.Value] = id;
        Depth[id.Value] = 0;
        System[id.Value] = new SystemId(id.Value);
        Init(id, kind, owner);
        return id;
    }

    public FrameId Add(FrameId parent, FrameKind kind, int owner = -1)
    {
        if (kind is FrameKind.Solar or FrameKind.Tunnel)
            throw new ArgumentException($"{kind} is a root", nameof(kind));

        if (parent.Value >= Count)
            throw new ArgumentOutOfRangeException(nameof(parent));

        var depth = Depth[parent.Value] + 1;

        if (depth > byte.MaxValue)
            throw new InvalidOperationException("Frame tree too deep");

        var id = Next();
        Parent[id.Value] = parent;
        Depth[id.Value] = (byte)depth;
        System[id.Value] = System[parent.Value];
        Init(id, kind, owner);
        return id;
    }

    /// <summary>
    /// Pose of <paramref name="from"/> in <paramref name="to"/>: maps points, velocities and axes given in
    /// <paramref name="from"/> into <paramref name="to"/>. Composed in double up to the common ancestor only, so nearby
    /// frames never pass through heliocentric magnitudes. False across trees (systems have no pose relative to each other).
    /// </summary>
    public bool TryRelative(FrameId from, FrameId to, out FramePose pose)
    {
        if (System[from.Value] != System[to.Value])
        {
            pose = default;
            return false;
        }

        var up = FramePose.Identity;
        var down = FramePose.Identity;
        var f = from.Value;
        var t = to.Value;

        while (Depth[f] > Depth[t])
        {
            up = FramePose.Compose(Local[f], up);
            f = Parent[f].Value;
        }

        while (Depth[t] > Depth[f])
        {
            down = FramePose.Compose(Local[t], down);
            t = Parent[t].Value;
        }

        while (f != t)
        {
            up = FramePose.Compose(Local[f], up);
            f = Parent[f].Value;
            down = FramePose.Compose(Local[t], down);
            t = Parent[t].Value;
        }

        pose = FramePose.Compose(FramePose.Inverse(down), up);
        return true;
    }

    private FrameId Next()
    {
        if (Count == Capacity)
            throw new InvalidOperationException($"Frame tree full ({Capacity})");

        return new FrameId((ushort)Count++);
    }

    private void Init(FrameId id, FrameKind kind, int owner)
    {
        Kind[id.Value] = kind;
        Local[id.Value] = FramePose.Identity;
        OwnerEntity[id.Value] = owner;
    }
}
