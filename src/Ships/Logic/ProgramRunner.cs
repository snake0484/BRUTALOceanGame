namespace BrutalGame.Ships.Logic;

/// <summary>
/// One placed component's programme state. The part a renderer needs is the <b>state record</b>: <see cref="State"/>, the
/// tick it was <see cref="Entered"/>, where each joint <see cref="Start"/>ed from then and is driven to (<see cref="Target"/>,
/// its motor's input), whether it is <see cref="Held"/> (stopped where it was by something in its way, or no power), and
/// the other effectors' <see cref="Inputs"/> (a light's On). From it every joint's position at any tick is closed form
/// (<see cref="ProgramRunner.Position"/>), so a client, a far ship or a save draws the component without running its
/// graph; only a new state record goes on the wire. The rest (the running sequence, the sensors' <see cref="Signals"/>)
/// is server-side only.
/// </summary>
public sealed class ProgramInstance
{
    public const int MaxJoints = 16;
    public const int MaxSignals = 128;
    public const int MaxInputs = 32;

    public byte State;
    public long Entered;
    public bool Held;
    public readonly short[] Start = new short[MaxJoints];
    public readonly short[] Target = new short[MaxJoints];
    public readonly short[] Inputs = new short[MaxInputs];

    /// <summary>The node the running sequence is at (0: idle) and, in a Wait or Wait until, the tick it ends.</summary>
    public ushort Node;
    public long WaitUntil;

    /// <summary>
    /// Something physically stops the moving parts (the game's sweep; the designer's preview toggle): while set, parts on
    /// their way stop where they are (the record becomes held), and set off again when it clears. The programme learns of
    /// it only by what it can see: a part that does not reach its position in time, so a Wait until times out.
    /// </summary>
    public bool Obstructed;

    /// <summary>No power: sensors read nothing (their signals go dead), motors stop, lights go out.</summary>
    public bool Unpowered;

    /// <summary>The component's cores (built per plan version) and what its sensors look at; null: no cores.</summary>
    public CoreLayout? Cores;
    public ISensorWorld? World;

    /// <summary>A tool's holder (D91): read by its grip.</summary>
    public HolderInput Holder;

    /// <summary>Per core (by layout entry), what it keeps between ticks: a battery's charge (mJ), an emitter's heat (millionths of its limit). Server-side.</summary>
    public readonly long[] CoreState = new long[HullCoreSet.MaxCores];

    /// <summary>Whether its batteries (if it has any) hold any charge.</summary>
    public bool Charged
    {
        get
        {
            if (Cores is not { HasBatteries: true } cores)
                return true;

            for (var i = 0; i < cores.Entries.Count; i++)
            {
                if (cores.Entries[i].Def.Kind == CoreKind.Battery && CoreState[i] > 0)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Every core's signals in counts of their steps (<see cref="CoreLayout"/> places them), and which are live (have a reading, or a value set).</summary>
    public readonly int[] Signals = new int[MaxSignals];
    public readonly ulong[] Live = new ulong[MaxSignals / 64];

    public bool IsLive(int signal) => (Live[signal >> 6] & (1UL << (signal & 63))) != 0;

    public void SetLive(int signal, bool live)
    {
        if (live)
            Live[signal >> 6] |= 1UL << (signal & 63);
        else
            Live[signal >> 6] &= ~(1UL << (signal & 63));
    }
}

/// <summary>
/// Runs component programmes, deterministically, on the integer tick (60 a second). Each tick: an obstruction or power
/// loss stops the motors; sensors due on this tick take their readings (D88: quantized to their step, at their rate);
/// the running sequence runs through instant nodes until it waits, at most <see cref="MaxStepsPerTick"/>, so a loop
/// cannot hang a tick; then the effectors report what they did. A call starts its sequence (replacing any running one).
/// Joints move at their own speed toward their targets. No allocation.
/// </summary>
public static class ProgramRunner
{
    public const int TicksPerSecond = 60;
    public const int MaxStepsPerTick = 32;

    /// <summary>At rest in the first state, every joint at its pose there (its target where the state names none); then the start sequence, if any.</summary>
    public static void Reset(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick)
    {
        instance.State = 0;
        instance.Entered = tick;
        instance.Held = false;
        instance.Node = 0;
        instance.WaitUntil = 0;
        Array.Clear(instance.Inputs);
        Array.Clear(instance.Signals);
        Array.Clear(instance.Live);
        Array.Clear(instance.CoreState);

        // Batteries start full.
        if (instance.Cores is { } cores)
        {
            for (var i = 0; i < cores.Entries.Count; i++)
            {
                if (cores.Entries[i].Def.Kind == CoreKind.Battery)
                    instance.CoreState[i] = CoreLayout.Capacity(cores.Entries[i].Def);
            }
        }

        for (var j = 0; j < joints.Count && j < ProgramInstance.MaxJoints; j++)
            instance.Start[j] = instance.Target[j] = program.PoseOf(0, joints[j].Id, out var v) ? v : joints[j].Target;

        foreach (var n in program.Nodes)
        {
            if (n.Kind == NodeKind.OnStart)
            {
                instance.Node = n.Out0;
                break;
            }
        }
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
        var target = instance.Target[j];

        if (instance.Held)
            return start;

        var travel = Speed(joints[j]) * Math.Max(0, tick - instance.Entered) / TicksPerSecond;
        return target >= start ? Math.Min(target, start + travel) : Math.Max(target, start - travel);
    }

    /// <summary>Whether every joint has reached its target (a held record never gets there).</summary>
    public static bool Arrived(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick)
    {
        for (var j = 0; j < joints.Count && j < ProgramInstance.MaxJoints; j++)
        {
            if (Math.Abs(Position(program, joints, instance, j, tick) - instance.Target[j]) > 1e-9)
                return false;
        }

        return true;
    }

    /// <summary>Enters <paramref name="state"/> at <paramref name="tick"/>: its joints set off from where they are now to its pose (a joint it does not name holds).</summary>
    public static void Enter(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, int state, long tick)
    {
        Rebase(program, joints, instance, tick);

        for (var j = 0; j < joints.Count && j < ProgramInstance.MaxJoints; j++)
            instance.Target[j] = program.PoseOf(state, joints[j].Id, out var v) ? v : instance.Start[j];

        instance.State = (byte)state;
    }

    /// <summary>Every joint starts again from where it is now (targets kept), so one can change without the others jumping.</summary>
    public static void Rebase(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick)
    {
        for (var j = 0; j < joints.Count && j < ProgramInstance.MaxJoints; j++)
            instance.Start[j] = (short)Math.Round(Position(program, joints, instance, j, tick));

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
            case NodeKind.SignalIs:
                return SignalHolds(instance, c);
            default:
                return false;
        }
    }

    /// <summary>One tick. True when the state record changed (what a server would send).</summary>
    public static bool Step(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick)
    {
        var changed = false;
        var powered = !instance.Unpowered && instance.Charged;
        var stopped = instance.Obstructed || (!powered && instance.Cores is { HasMotors: true });

        // Stopped where they are by whatever is in the way (or no power); when it clears, the motors drive them on from there.
        if (stopped && !instance.Held && !Arrived(program, joints, instance, tick))
        {
            Rebase(program, joints, instance, tick);
            instance.Held = true;
            changed = true;
        }
        else if (!stopped && instance.Held)
        {
            Rebase(program, joints, instance, tick);
            changed = true;
        }

        if (instance.Cores is { } cores)
            Sense(cores, program, joints, instance, tick, powered);

        changed |= Run(program, joints, instance, tick, stopped);

        if (instance.Cores is { } after)
            Report(after, program, joints, instance, tick, powered);

        return changed;
    }

    /// <summary>A joint's speed in stored units per second (a rotor's 0.1 rpm as 0.1 degree a second).</summary>
    public static double Speed(HullJoint joint) => Math.Abs(joint.Kind == JointKind.Rotor ? joint.Speed * 6.0 : joint.Speed);

    /// <summary>A joint's stored units in its signals' unit: mm on a slider, degrees otherwise.</summary>
    public static double JointUnit(HullJoint joint) => joint.Kind == JointKind.Slider ? 1.0 : 0.1;

    /// <summary>Counts of <paramref name="step"/> nearest <paramref name="value"/>.</summary>
    public static int Quantise(double value, double step) => (int)Math.Clamp(Math.Round(value / step), int.MinValue, int.MaxValue);

    /// <summary>The running sequence: instant nodes until one waits.</summary>
    private static bool Run(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick, bool stopped)
    {
        var changed = false;

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
                case NodeKind.OnStart:
                    instance.Node = node.Out0;
                    break;
                case NodeKind.SetState:
                    Enter(program, joints, instance, node.A, tick);
                    instance.Node = node.Out0;
                    changed = true;

                    if (stopped && !Arrived(program, joints, instance, tick))
                        instance.Held = true;

                    break;
                case NodeKind.SetInput:
                    changed |= SetInput(program, joints, instance, node, tick, stopped);
                    instance.Node = node.Out0;
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

    /// <summary>Sensors due on this tick read the world (one with no reading yet reads at once; unpowered, their signals go dead).</summary>
    private static void Sense(CoreLayout cores, HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick, bool powered)
    {
        for (var i = 0; i < cores.Entries.Count; i++)
        {
            var e = cores.Entries[i];

            if (!e.Def.IsSensor)
                continue;

            var signals = e.Def.Signals;

            if (!powered)
            {
                for (var s = 0; s < signals.Count; s++)
                {
                    instance.Signals[e.SignalBase + s] = 0;
                    instance.SetLive(e.SignalBase + s, false);
                }

                continue;
            }

            if ((tick - e.Phase) % e.Period != 0 && instance.IsLive(e.SignalBase))
                continue;

            switch (e.Def.Kind)
            {
                case CoreKind.JointPosition:
                    Write(instance, e.SignalBase, Quantise(Position(program, joints, instance, e.Joint, tick) * JointUnit(joints[e.Joint]), signals[0].Step), signals[0]);
                    break;
                case CoreKind.JointLimit:
                    Write(instance, e.SignalBase, Math.Abs(Position(program, joints, instance, e.Joint, tick) - e.At) <= e.Def.Tolerance ? 1 : 0, signals[0]);
                    break;
                case CoreKind.Proximity:
                    var range = e.Def.Range;
                    var d = instance.World?.Nearest(e.Position, e.Normal, e.Def.HalfAngleDeg * Math.PI / 180.0, range) ?? double.PositiveInfinity;
                    var seen = d <= range;
                    Write(instance, e.SignalBase, seen ? 1 : 0, signals[0]);
                    Write(instance, e.SignalBase + 1, Quantise(seen ? d : range, signals[1].Step), signals[1]);
                    break;
                case CoreKind.Grip:
                    var holder = instance.Holder;
                    Write(instance, e.SignalBase, holder.Holding ? 1 : 0, signals[0]);
                    Write(instance, e.SignalBase + 1, holder.Holding && holder.Trigger ? 1 : 0, signals[1]);
                    Write(instance, e.SignalBase + 2, holder.Holding && holder.Alt ? 1 : 0, signals[2]);
                    Write(instance, e.SignalBase + 3, holder.Mode, signals[3]);
                    break;
            }
        }
    }

    /// <summary>
    /// The effectors report: their inputs as set, and what they did (unpowered, what they did is dead). Then the
    /// batteries pay for the tick: every powered sensor, and each effector while it works, draws its power, taken from
    /// the first battery with charge left.
    /// </summary>
    private static void Report(CoreLayout cores, HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, long tick, bool powered)
    {
        var drawW = 0.0;

        for (var i = 0; i < cores.Entries.Count; i++)
        {
            var e = cores.Entries[i];
            var signals = e.Def.Signals;

            switch (e.Def.Kind)
            {
                case CoreKind.JointMotor:
                    var target = instance.Target[e.Joint];
                    var away = Math.Abs(Position(program, joints, instance, e.Joint, tick) - target) > 1e-9;
                    Write(instance, e.SignalBase, Quantise(target * JointUnit(joints[e.Joint]), signals[0].Step), signals[0]);
                    Flag(instance, e.SignalBase + 1, away && !instance.Held, powered);
                    Flag(instance, e.SignalBase + 2, away && instance.Held, powered);
                    drawW += powered && away && !instance.Held ? e.Def.PowerW : 0;
                    break;
                case CoreKind.Light:
                    var on = instance.Inputs[e.InputBase];
                    var brightness = instance.Inputs[e.InputBase + 1];
                    Write(instance, e.SignalBase, on, signals[0]);
                    Write(instance, e.SignalBase + 1, brightness, signals[1]);
                    Flag(instance, e.SignalBase + 2, on > 0 && brightness > 0, powered);
                    drawW += powered && on > 0 ? e.Def.PowerW * brightness / 100.0 : 0;
                    break;
                case CoreKind.Emitter:
                    drawW += Emit(e, i, instance, powered);
                    break;
                default:
                    drawW += powered && e.Def.IsSensor ? e.Def.PowerW : 0;
                    break;
            }
        }

        if (!cores.HasBatteries)
            return;

        var cost = (long)Math.Round(drawW * 1000.0 / TicksPerSecond);

        for (var i = 0; i < cores.Entries.Count; i++)
        {
            var e = cores.Entries[i];

            if (e.Def.Kind != CoreKind.Battery)
                continue;

            var taken = Math.Min(cost, instance.CoreState[i]);
            instance.CoreState[i] -= taken;
            cost -= taken;
            var capacity = Math.Max(1, CoreLayout.Capacity(e.Def));
            Write(instance, e.SignalBase, Quantise(100.0 * instance.CoreState[i] / capacity, e.Def.Signals[0].Step), e.Def.Signals[0]);
            Write(instance, e.SignalBase + 1, Quantise(taken * (double)TicksPerSecond / 1000.0, e.Def.Signals[1].Step), e.Def.Signals[1]);
        }
    }

    /// <summary>An emitter's tick: it fires while told to, powered and not overheated, heating with its power; off, it cools. Full heat cuts it out until it is down to half. Its draw (W).</summary>
    private static double Emit(in CoreLayout.Entry e, int index, ProgramInstance instance, bool powered)
    {
        const long limit = 1_000_000;
        var signals = e.Def.Signals;
        var fire = instance.Inputs[e.InputBase];
        var power = instance.Inputs[e.InputBase + 1] * signals[1].Step / 100.0;
        var overheated = instance.IsLive(e.SignalBase + 4) && instance.Signals[e.SignalBase + 4] != 0;
        ref var heat = ref instance.CoreState[index];

        if (overheated && heat <= limit / 2)
            overheated = false;

        var firing = powered && fire > 0 && power > 0 && !overheated;
        heat = firing
            ? Math.Min(limit, heat + (long)Math.Round(e.Def.HeatPerSecond * power * limit / 100.0 / TicksPerSecond))
            : Math.Max(0, heat - (long)Math.Round(e.Def.CoolPerSecond * limit / 100.0 / TicksPerSecond));
        overheated |= heat >= limit;

        Write(instance, e.SignalBase, fire, signals[0]);
        Write(instance, e.SignalBase + 1, instance.Inputs[e.InputBase + 1], signals[1]);
        Flag(instance, e.SignalBase + 2, firing, powered);
        Write(instance, e.SignalBase + 3, Quantise(100.0 * heat / limit, signals[3].Step), signals[3]);
        Flag(instance, e.SignalBase + 4, overheated, true);
        return firing ? e.Def.PowerW * power : 0;
    }

    /// <summary>Sets an effector's input: a motor's Target moves its joint's target (the others start again from where they are); other inputs go in the record. True when the record changed.</summary>
    private static bool SetInput(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance instance, in ProgramNode node, long tick, bool stopped)
    {
        if (instance.Cores is not { } cores || cores.IndexOf(node.Joint) is not (>= 0 and var at))
            return false;

        var e = cores.Entries[at];
        var input = -1;
        var k = 0;

        for (var s = 0; s < e.Def.Signals.Count; s++)
        {
            if (!e.Def.Signals[s].Input)
                continue;

            if (k == node.A)
            {
                input = s;
                break;
            }

            k++;
        }

        if (input < 0)
            return false;

        var signal = e.Def.Signals[input];
        var counts = (short)Math.Clamp((int)node.B, signal.MinCount, signal.MaxCount);

        if (e.Def.Kind == CoreKind.JointMotor)
        {
            var joint = joints[e.Joint];
            var stored = (short)Math.Round(counts * signal.Step / JointUnit(joint));

            if (joint.Limited)
                stored = Math.Clamp(stored, joint.Min, joint.Max);

            Rebase(program, joints, instance, tick);
            instance.Target[e.Joint] = stored;
            instance.Held = stopped && !Arrived(program, joints, instance, tick);
            return true;
        }

        var changed = instance.Inputs[e.InputBase + k] != counts;
        instance.Inputs[e.InputBase + k] = counts;
        return changed;
    }

    private static bool SignalHolds(ProgramInstance instance, in ProgramNode c)
    {
        if (instance.Cores is not { } cores || cores.IndexOf(c.Joint) is not (>= 0 and var at))
            return false;

        var e = cores.Entries[at];
        var s = c.A & 15;

        if (s >= e.Def.Signals.Count || !instance.IsLive(e.SignalBase + s))
            return false;

        var v = instance.Signals[e.SignalBase + s];
        return (Compare)(c.A >> 4) switch
        {
            Compare.Equal => v == c.B,
            Compare.NotEqual => v != c.B,
            Compare.Less => v < c.B,
            Compare.Greater => v > c.B,
            Compare.LessOrEqual => v <= c.B,
            Compare.GreaterOrEqual => v >= c.B,
            _ => false,
        };
    }

    private static void Write(ProgramInstance instance, int signal, int counts, CoreSignal def)
    {
        instance.Signals[signal] = Math.Clamp(counts, def.MinCount, def.MaxCount);
        instance.SetLive(signal, true);
    }

    private static void Flag(ProgramInstance instance, int signal, bool value, bool live)
    {
        instance.Signals[signal] = value ? 1 : 0;
        instance.SetLive(signal, live);
    }
}
