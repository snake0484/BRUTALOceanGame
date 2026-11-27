namespace BrutalGame.Ships.Logic;

/// <summary>
/// One placed component's programme state. The part a renderer needs is the <b>state record</b>: <see cref="State"/>, the
/// tick it was <see cref="Entered"/>, where each joint <see cref="Start"/>ed from then, and whether it is
/// <see cref="Held"/> (stopped where it was by something in its way). From it every joint's position at any tick is
/// closed form (<see cref="ProgramRunner.Position"/>), so a client, a far ship or a save draws the component without
/// running its graph; only a new state record goes on the wire. The rest is the running sequence, server-side only.
/// </summary>
public sealed class ProgramInstance
{
    public const int MaxJoints = 16;

    public byte State;
    public long Entered;
    public bool Held;
    public readonly short[] Start = new short[MaxJoints];

    /// <summary>The node the running sequence is at (0: idle) and, in a Wait or Wait until, the tick it ends.</summary>
    public ushort Node;
    public long WaitUntil;

    /// <summary>
    /// Something physically stops the moving parts (the game's sweep; the designer's preview toggle): while set, parts on
    /// their way stop where they are (the record becomes held). The programme learns of it only by what it can see: a
    /// part that never reaches its position, so a Wait until times out.
    /// </summary>
    public bool Obstructed;
}

/// <summary>
/// Runs component programmes, deterministically, on the integer tick (60 a second): a call starts its sequence at the
/// call's node (replacing any running one); each tick the sequence runs through instant nodes until it waits, at most
/// <see cref="MaxStepsPerTick"/> nodes, so a loop cannot hang a tick. Joints move at their own speed toward the current
/// state's pose, unless something stops them. No allocation.
/// </summary>
public static class ProgramRunner
{
    public const int TicksPerSecond = 60;
    public const int MaxStepsPerTick = 32;

    /// <summary>At rest in the first state, every joint at its pose there (its target where the state names none).</summary>
    public static void Reset(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick)
    {
        instance.State = 0;
        instance.Entered = tick;
        instance.Held = false;
        instance.Node = 0;
        instance.WaitUntil = 0;

        for (var j = 0; j < joints.Count && j < ProgramInstance.MaxJoints; j++)
            instance.Start[j] = program.PoseOf(0, joints[j].Id, out var v) ? v : joints[j].Target;
    }

    /// <summary>Starts call <paramref name="call"/>'s sequence; false when the programme has no node for it.</summary>
    public static bool Call(HullProgram program, ProgramInstance instance, int call)
    {
        foreach (var n in program.Nodes)
        {
            if (n.Kind == NodeKind.OnCall && n.A == call)
            {
                instance.Node = n.Out0;
                instance.WaitUntil = 0;
                return true;
            }
        }

        return false;
    }

    /// <summary>Joint <paramref name="j"/>'s position at <paramref name="tick"/> (stored units: 0.1 degree, or mm), from the state record alone.</summary>
    public static double Position(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, int j, long tick)
    {
        var start = instance.Start[j];

        if (instance.Held || !program.PoseOf(instance.State, joints[j].Id, out var target))
            return start;

        var travel = Speed(joints[j]) * Math.Max(0, tick - instance.Entered) / TicksPerSecond;
        return target >= start ? Math.Min(target, start + travel) : Math.Max(target, start - travel);
    }

    /// <summary>Whether every joint the state names has reached its pose (a held state never gets there).</summary>
    public static bool Arrived(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick)
    {
        for (var j = 0; j < joints.Count && j < ProgramInstance.MaxJoints; j++)
        {
            if (program.PoseOf(instance.State, joints[j].Id, out var target) && Math.Abs(Position(program, joints, instance, j, tick) - target) > 1e-9)
                return false;
        }

        return true;
    }

    /// <summary>Enters <paramref name="state"/> at <paramref name="tick"/>: its joints set off from where they are now.</summary>
    public static void Enter(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, int state, long tick)
    {
        for (var j = 0; j < joints.Count && j < ProgramInstance.MaxJoints; j++)
            instance.Start[j] = (short)Math.Round(Position(program, joints, instance, j, tick));

        instance.State = (byte)state;
        instance.Entered = tick;
        instance.Held = false;
    }

    /// <summary>Whether condition node <paramref name="id"/> holds at <paramref name="tick"/> (a missing one never does).</summary>
    public static bool Holds(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, ushort id, long tick)
    {
        var index = program.IndexOf(id);

        if (index < 0)
            return false;

        var c = program.Nodes[index];

        switch (c.Kind)
        {
            case NodeKind.StateIs:
                return instance.State == c.A;
            case NodeKind.InPose:
                return Arrived(program, joints, instance, tick);
            case NodeKind.JointAt:
                for (var j = 0; j < joints.Count && j < ProgramInstance.MaxJoints; j++)
                {
                    if (joints[j].Id == c.Joint)
                        return Math.Abs(Position(program, joints, instance, j, tick) - c.B) <= c.A;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>One tick: an obstruction stops parts on their way, then the running sequence. True when the state record changed (what a server would send).</summary>
    public static bool Step(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick)
    {
        var changed = false;

        // Stopped where they are by whatever is in the way.
        if (instance.Obstructed && !instance.Held && !Arrived(program, joints, instance, tick))
        {
            Enter(program, joints, instance, instance.State, tick);
            instance.Held = true;
            changed = true;
        }

        for (var steps = 0; instance.Node != 0 && steps < MaxStepsPerTick; steps++)
        {
            var index = program.IndexOf(instance.Node);

            if (index < 0)
            {
                instance.Node = 0;
                break;
            }

            var node = program.Nodes[index];

            switch (node.Kind)
            {
                case NodeKind.OnCall:
                    instance.Node = node.Out0;
                    break;
                case NodeKind.SetState:
                    Enter(program, joints, instance, node.A, tick);
                    instance.Node = node.Out0;
                    changed = true;

                    if (instance.Obstructed && !Arrived(program, joints, instance, tick))
                        instance.Held = true;

                    break;
                case NodeKind.WaitUntil:
                    if (instance.WaitUntil == 0)
                        instance.WaitUntil = node.B > 0 ? tick + node.B : long.MaxValue;

                    if (Holds(program, joints, instance, node.In, tick))
                    {
                        (instance.WaitUntil, instance.Node) = (0, node.Out0);
                        break;
                    }

                    if (tick < instance.WaitUntil)
                        return changed;

                    (instance.WaitUntil, instance.Node) = (0, node.Out1);
                    break;
                case NodeKind.Wait:
                    if (instance.WaitUntil == 0)
                        instance.WaitUntil = tick + Math.Max((short)0, node.B);

                    if (tick < instance.WaitUntil)
                        return changed;

                    instance.WaitUntil = 0;
                    instance.Node = node.Out0;
                    break;
                case NodeKind.If:
                    instance.Node = Holds(program, joints, instance, node.In, tick) ? node.Out0 : node.Out1;
                    break;
                case NodeKind.IfState:
                    instance.Node = instance.State == node.A ? node.Out0 : node.Out1;
                    break;
                case NodeKind.WaitArrived:
                    if (Arrived(program, joints, instance, tick))
                        instance.Node = node.Out0;
                    else if (instance.Held && node.Out1 != 0)
                        instance.Node = node.Out1;
                    else
                        return changed;

                    break;
                case NodeKind.IfBlocked:
                    instance.Node = instance.Obstructed ? node.Out0 : node.Out1;
                    break;
                default:
                    instance.Node = 0;
                    break;
            }
        }

        return changed;
    }

    /// <summary>A joint's speed in stored units per second (a rotor's 0.1 rpm as 0.1 degree a second).</summary>
    public static double Speed(HullJoint joint) => Math.Abs(joint.Kind == JointKind.Rotor ? joint.Speed * 6.0 : joint.Speed);
}
