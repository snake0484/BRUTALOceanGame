using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace BrutalGame.Ships.Logic;

/// <summary>
/// What a kind of component answers to, so anything can drive any component of that kind the same way: its calls (a
/// door's Open, Close, Lock, Unlock) and its states (Closed, Opening, Open, Closing, Locked, Blocked). Built-in ones
/// are data (<c>data/interfaces</c>); players make their own and keep them in a library. A component's programme
/// carries a copy of its interface, so a plan stands alone; two interfaces are the same when <see cref="Key"/> is.
/// </summary>
public sealed class ComponentInterface
{
    public const int MaxNames = 16;
    public const int MaxNameBytes = 32;

    public string Name { get; set; } = "";
    public List<string> Calls { get; set; } = new();
    public List<string> States { get; set; } = new();

    /// <summary>The name, calls and states in order: two interfaces with the same key are interchangeable.</summary>
    public string Key => $"{Name}|{string.Join(',', Calls)}|{string.Join(',', States)}";

    public ComponentInterface Clone() => new() { Name = Name, Calls = new List<string>(Calls), States = new List<string>(States) };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>An interface from its JSON, or null if it does not parse or breaks the limits.</summary>
    public static ComponentInterface? FromJson(string text)
    {
        try
        {
            var i = JsonSerializer.Deserialize<ComponentInterface>(text, Json);
            return i is not null && IsValid(i) ? i : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsValid(ComponentInterface i) =>
        Fits(i.Name) && i.Calls.Count is > 0 and <= MaxNames && i.States.Count is > 0 and <= MaxNames
        && i.Calls.TrueForAll(Fits) && i.States.TrueForAll(Fits)
        && i.Calls.Distinct().Count() == i.Calls.Count && i.States.Distinct().Count() == i.States.Count;

    private static bool Fits(string s) => s.Length > 0 && Encoding.UTF8.GetByteCount(s) <= MaxNameBytes;
}

/// <summary>
/// A programme's nodes. Flow runs from a node's outputs (<see cref="ProgramNode.Out0"/>, and <see cref="ProgramNode.Out1"/>
/// for a branch) to the nodes they point at. Conditions are nodes too, with no flow: a flow node that tests one points at
/// it with <see cref="ProgramNode.In"/>.
/// <list type="bullet">
/// <item><see cref="OnCall"/>: where a call (A) starts; out 0.</item>
/// <item><see cref="SetState"/>: enters state A, its joints moving from where they are to that state's pose; out 0.</item>
/// <item><see cref="WaitUntil"/>: waits until its condition holds (out 0), or B ticks pass first (out 1, timed out: a
/// part that never got there was stopped by something; B = 0 waits for ever).</item>
/// <item><see cref="Wait"/>: waits B ticks; out 0.</item>
/// <item><see cref="If"/>: out 0 when its condition holds, else out 1.</item>
/// <item><see cref="IfState"/>: out 0 when the state is A, else out 1.</item>
/// <item>Conditions: <see cref="StateIs"/> (the state is A), <see cref="JointAt"/> (joint <see cref="ProgramNode.Joint"/>
/// is within A stored units of B), <see cref="InPose"/> (every joint the state names has reached its pose).</item>
/// <item><see cref="OnStart"/>: where the programme starts when the component starts (at rest, or powered up); out 0.</item>
/// <item><see cref="SetInput"/>: sets input A of core <see cref="ProgramNode.Joint"/> to B counts of its step (an
/// effector's: a motor's Target, a light's On); out 0.</item>
/// <item>Condition <see cref="SignalIs"/>: signal (A &amp; 15) of core <see cref="ProgramNode.Joint"/> compared (A &gt;&gt; 4, a
/// <see cref="Compare"/>) with B counts; a dead signal (no power, no reading yet) never holds.</item>
/// <item>Older nodes, still run but no longer offered: <see cref="WaitArrived"/> (in pose, or out 1 when its parts were
/// stopped) and <see cref="IfBlocked"/> (out 0 while the parts are obstructed).</item>
/// </list>
/// </summary>
public enum NodeKind : byte { OnCall, SetState, WaitArrived, Wait, IfState, IfBlocked, WaitUntil, If, StateIs, JointAt, InPose, OnStart, SetInput, SignalIs }

/// <summary>A <see cref="NodeKind.SignalIs"/> comparison.</summary>
public enum Compare : byte { Equal, NotEqual, Less, Greater, LessOrEqual, GreaterOrEqual }

public struct ProgramNode
{
    public ushort Id;
    public NodeKind Kind;

    /// <summary>Where it sits in the editor (pixels at zoom 1).</summary>
    public short X, Y;

    /// <summary>The call or state it names (an index into the interface's); a <see cref="NodeKind.JointAt"/>'s tolerance.</summary>
    public byte A;

    /// <summary>Wait and Wait until: ticks (60 a second); a <see cref="NodeKind.JointAt"/>'s position (stored units).</summary>
    public short B;

    /// <summary>The nodes its outputs lead to (0: nothing; the programme stops there).</summary>
    public ushort Out0, Out1;

    /// <summary>The condition a Wait until or If tests (a condition node's id; 0: none, never holds).</summary>
    public ushort In;

    /// <summary>The joint a <see cref="NodeKind.JointAt"/> reads; the core a <see cref="NodeKind.SetInput"/> or <see cref="NodeKind.SignalIs"/> names.</summary>
    public uint Joint;

    public readonly bool IsCondition => Kind is NodeKind.StateIs or NodeKind.JointAt or NodeKind.InPose or NodeKind.SignalIs;

    /// <summary>Whether a programme with it needs plan format 17 (cores).</summary>
    public readonly bool NeedsCores => Kind is NodeKind.OnStart or NodeKind.SetInput or NodeKind.SignalIs;

    public readonly bool TakesCondition => Kind is NodeKind.WaitUntil or NodeKind.If;

    public readonly bool HasInput => Kind is not (NodeKind.OnCall or NodeKind.OnStart) && !IsCondition;
}

/// <summary>Where joint <see cref="Joint"/> stands in state <see cref="State"/> (its stored units: 0.1 degree, or mm for a slider).</summary>
public readonly record struct StatePose(byte State, uint Joint, short Value);

/// <summary>
/// A component's programme: the interface it implements, its node graph, and each state's pose (where each joint stands;
/// a joint a state does not name holds where it is). The pose is all a renderer needs: with the state and the tick it was
/// entered (<see cref="ProgramInstance"/>) any client draws the component without running the graph. BODY format 15.
/// </summary>
public sealed class HullProgram
{
    public const int MaxNodes = 64;
    public const int MaxPoses = 128;

    public ComponentInterface Interface = new();
    public readonly List<ProgramNode> Nodes = new();
    public readonly List<StatePose> Poses = new();

    /// <summary>Node <paramref name="node"/>'s index, or -1 (a loop: the runner calls it every step, so no closure).</summary>
    public int IndexOf(ushort node)
    {
        for (var i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i].Id == node)
                return i;
        }

        return -1;
    }

    public ushort NextNodeId() => (ushort)(Nodes.Count == 0 ? 1 : Nodes.Max(n => n.Id) + 1);

    /// <summary>The pose of <paramref name="joint"/> in <paramref name="state"/>, if the state names it.</summary>
    public bool PoseOf(int state, uint joint, out short value)
    {
        foreach (var p in Poses)
        {
            if (p.State == state && p.Joint == joint)
            {
                value = p.Value;
                return true;
            }
        }

        value = 0;
        return false;
    }

    public HullProgram Clone()
    {
        var copy = new HullProgram { Interface = Interface.Clone() };
        copy.Nodes.AddRange(Nodes);
        copy.Poses.AddRange(Poses);
        return copy;
    }

    /// <summary>Bytes of a node: plan format 15's (no condition fields), and 16's.</summary>
    public const int NarrowNodeBytes = 14, NodeBytes = 20;

    /// <summary>
    /// <c>name, u8 call count, calls, u8 state count, states</c> (each a u8-length UTF-8 string), <c>u8 node count</c>, per
    /// node <c>u16 id, u8 kind, i16 x, y, u8 a, i16 b, u16 out0, out1, in, u32 joint</c> (20 B; plan format 15 stopped at
    /// out1, 14 B), <c>u8 pose count</c>, per pose <c>u8 state, u32 joint, i16 value</c> (7 B).
    /// </summary>
    public byte[] ToBytes()
    {
        var b = new List<byte>(64 + Nodes.Count * NodeBytes + Poses.Count * 7);
        Text(b, Interface.Name);
        b.Add((byte)Interface.Calls.Count);
        Interface.Calls.ForEach(c => Text(b, c));
        b.Add((byte)Interface.States.Count);
        Interface.States.ForEach(s => Text(b, s));
        b.Add((byte)Nodes.Count);
        Span<byte> n = stackalloc byte[NodeBytes];

        foreach (var node in Nodes)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(n, node.Id);
            n[2] = (byte)node.Kind;
            BinaryPrimitives.WriteInt16LittleEndian(n[3..], node.X);
            BinaryPrimitives.WriteInt16LittleEndian(n[5..], node.Y);
            n[7] = node.A;
            BinaryPrimitives.WriteInt16LittleEndian(n[8..], node.B);
            BinaryPrimitives.WriteUInt16LittleEndian(n[10..], node.Out0);
            BinaryPrimitives.WriteUInt16LittleEndian(n[12..], node.Out1);
            BinaryPrimitives.WriteUInt16LittleEndian(n[14..], node.In);
            BinaryPrimitives.WriteUInt32LittleEndian(n[16..], node.Joint);
            b.AddRange(n);
        }

        b.Add((byte)Poses.Count);
        Span<byte> p = stackalloc byte[7];

        foreach (var pose in Poses)
        {
            p[0] = pose.State;
            BinaryPrimitives.WriteUInt32LittleEndian(p[1..], pose.Joint);
            BinaryPrimitives.WriteInt16LittleEndian(p[5..], pose.Value);
            b.AddRange(p);
        }

        return b.ToArray();
    }

    /// <summary>A programme from its bytes; <paramref name="narrow"/> for plan format 15's nodes.</summary>
    public static HullProgram? Read(ReadOnlySpan<byte> s, out int consumed, bool narrow = false)
    {
        var size = narrow ? NarrowNodeBytes : NodeBytes;
        consumed = 0;
        var o = 0;
        var program = new HullProgram();

        if (!ReadText(s, ref o, out var name) || !ReadNames(s, ref o, program.Interface.Calls) || !ReadNames(s, ref o, program.Interface.States))
            return null;

        program.Interface.Name = name;

        if (s.Length < o + 1 || s[o] > MaxNodes || s.Length < o + 1 + s[o] * size + 1)
            return null;

        int nodes = s[o++];

        for (var i = 0; i < nodes; i++, o += size)
        {
            var n = s.Slice(o, size);
            program.Nodes.Add(new ProgramNode
            {
                Id = BinaryPrimitives.ReadUInt16LittleEndian(n),
                Kind = (NodeKind)n[2],
                X = BinaryPrimitives.ReadInt16LittleEndian(n[3..]),
                Y = BinaryPrimitives.ReadInt16LittleEndian(n[5..]),
                A = n[7],
                B = BinaryPrimitives.ReadInt16LittleEndian(n[8..]),
                Out0 = BinaryPrimitives.ReadUInt16LittleEndian(n[10..]),
                Out1 = BinaryPrimitives.ReadUInt16LittleEndian(n[12..]),
                In = narrow ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(n[14..]),
                Joint = narrow ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(n[16..]),
            });
        }

        if (s.Length < o + 1 + s[o] * 7)
            return null;

        int poses = s[o++];

        for (var i = 0; i < poses; i++, o += 7)
            program.Poses.Add(new StatePose(s[o], BinaryPrimitives.ReadUInt32LittleEndian(s[(o + 1)..]), BinaryPrimitives.ReadInt16LittleEndian(s[(o + 5)..])));

        consumed = o;
        return program;
    }

    private static void Text(List<byte> b, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        b.Add((byte)Math.Min(bytes.Length, 255));
        b.AddRange(bytes.AsSpan(0, Math.Min(bytes.Length, 255)).ToArray());
    }

    private static bool ReadText(ReadOnlySpan<byte> s, ref int o, out string text)
    {
        text = "";

        if (s.Length < o + 1 || s.Length < o + 1 + s[o])
            return false;

        text = Encoding.UTF8.GetString(s.Slice(o + 1, s[o]));
        o += 1 + s[o];
        return true;
    }

    private static bool ReadNames(ReadOnlySpan<byte> s, ref int o, List<string> names)
    {
        if (s.Length < o + 1 || s[o] > ComponentInterface.MaxNames)
            return false;

        int count = s[o++];

        for (var i = 0; i < count; i++)
        {
            if (!ReadText(s, ref o, out var name))
                return false;

            names.Add(name);
        }

        return true;
    }
}
