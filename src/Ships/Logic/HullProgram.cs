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
/// for a branch) to the nodes they point at.
/// <list type="bullet">
/// <item><see cref="OnCall"/>: where a call (A) starts; out 0.</item>
/// <item><see cref="SetState"/>: enters state A, its joints moving from where they are to that state's pose; out 0.</item>
/// <item><see cref="WaitArrived"/>: waits until the state's joints reach their pose (out 0), or something is in the way while they move (out 1).</item>
/// <item><see cref="Wait"/>: waits B ticks; out 0.</item>
/// <item><see cref="IfState"/>: out 0 when the state is A, else out 1.</item>
/// <item><see cref="IfBlocked"/>: out 0 when something is in the way now, else out 1.</item>
/// </list>
/// </summary>
public enum NodeKind : byte { OnCall, SetState, WaitArrived, Wait, IfState, IfBlocked }

public struct ProgramNode
{
    public ushort Id;
    public NodeKind Kind;

    /// <summary>Where it sits in the editor (pixels at zoom 1).</summary>
    public short X, Y;

    /// <summary>The call or state it names (an index into the interface's).</summary>
    public byte A;

    /// <summary>Wait: ticks (60 a second).</summary>
    public short B;

    /// <summary>The nodes its outputs lead to (0: nothing; the programme stops there).</summary>
    public ushort Out0, Out1;

    public readonly bool Branches => Kind is NodeKind.WaitArrived or NodeKind.IfState or NodeKind.IfBlocked;

    public readonly bool HasInput => Kind != NodeKind.OnCall;
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

    public int IndexOf(ushort node) => Nodes.FindIndex(n => n.Id == node);

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

    /// <summary>
    /// <c>name, u8 call count, calls, u8 state count, states</c> (each a u8-length UTF-8 string), <c>u8 node count</c>, per
    /// node <c>u16 id, u8 kind, i16 x, y, u8 a, i16 b, u16 out0, out1</c> (14 B), <c>u8 pose count</c>, per pose
    /// <c>u8 state, u32 joint, i16 value</c> (7 B).
    /// </summary>
    public byte[] ToBytes()
    {
        var b = new List<byte>(64 + Nodes.Count * 14 + Poses.Count * 7);
        Text(b, Interface.Name);
        b.Add((byte)Interface.Calls.Count);
        Interface.Calls.ForEach(c => Text(b, c));
        b.Add((byte)Interface.States.Count);
        Interface.States.ForEach(s => Text(b, s));
        b.Add((byte)Nodes.Count);
        Span<byte> n = stackalloc byte[14];

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

    public static HullProgram? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;
        var o = 0;
        var program = new HullProgram();

        if (!ReadText(s, ref o, out var name) || !ReadNames(s, ref o, program.Interface.Calls) || !ReadNames(s, ref o, program.Interface.States))
            return null;

        program.Interface.Name = name;

        if (s.Length < o + 1 || s[o] > MaxNodes || s.Length < o + 1 + s[o] * 14 + 1)
            return null;

        int nodes = s[o++];

        for (var i = 0; i < nodes; i++, o += 14)
        {
            var n = s.Slice(o, 14);
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
