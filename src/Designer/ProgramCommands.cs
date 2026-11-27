using BrutalGame.Ships;
using BrutalGame.Ships.Logic;

namespace BrutalGame.Designer;

/// <summary>
/// The interfaces a component can implement: the built-in ones (<c>data/interfaces/*.json</c>) and the player's own
/// (<c>*.binterface</c> files in their library folder, saved from the programme panel).
/// </summary>
public sealed class InterfaceLibrary
{
    public const string Extension = "binterface";

    public readonly List<ComponentInterface> BuiltIn = new();
    public readonly List<(ComponentInterface Interface, string Path)> Mine = new();

    public InterfaceLibrary(string dataFolder, string userFolder)
    {
        DataFolder = dataFolder;
        UserFolder = userFolder;
    }

    public string DataFolder { get; }

    public string UserFolder { get; }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BRUTALGame", "Interfaces");

    public void Refresh()
    {
        BuiltIn.Clear();
        Mine.Clear();
        var builtIn = Path.Combine(DataFolder, "interfaces");

        if (Directory.Exists(builtIn))
        {
            foreach (var file in Directory.EnumerateFiles(builtIn, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (ComponentInterface.FromJson(File.ReadAllText(file)) is { } face)
                    BuiltIn.Add(face);
            }
        }

        if (!Directory.Exists(UserFolder))
            return;

        foreach (var file in Directory.EnumerateFiles(UserFolder, "*." + Extension, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ComponentInterface.FromJson(File.ReadAllText(file)) is { } face)
                Mine.Add((face, file));
        }
    }

    /// <summary>Saves (or replaces) <paramref name="face"/> in the user folder under its name; the path, or null if it is not valid.</summary>
    public string? Save(ComponentInterface face)
    {
        if (!ComponentInterface.IsValid(face))
            return null;

        Directory.CreateDirectory(UserFolder);
        var name = string.Concat(face.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(UserFolder, $"{name}.{Extension}");
        File.WriteAllText(path, face.ToJson());
        Refresh();
        return path;
    }
}

/// <summary>
/// Programme edits in a component plan, each one undo group that swaps the programme for an edited copy; and the
/// standard door sequence (built from the Door interface's call and state names) the catalog doors use.
/// </summary>
public static class ProgramCommands
{
    /// <summary>
    /// Implements <paramref name="face"/>: a new programme with default poses (each hinge or slider at its upper limit in a
    /// state named Open or Opening, at its lower limit otherwise; a Blocked state holds), or the interface swapped on the
    /// existing one (its nodes and poses kept).
    /// </summary>
    public static void Implement(DesignSession session, ComponentInterface face)
    {
        var joints = session.Doc.Joints?.Joints ?? [];
        Change(session, p =>
        {
            var fresh = p.Nodes.Count == 0 && p.Poses.Count == 0;
            p.Interface = face.Clone();

            if (!fresh)
                return;

            for (var s = 0; s < face.States.Count; s++)
            {
                var name = face.States[s];

                if (name.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
                    continue;

                var open = name.StartsWith("Open", StringComparison.OrdinalIgnoreCase);

                foreach (var j in joints)
                {
                    if (j.Limited)
                        p.Poses.Add(new StatePose((byte)s, j.Id, open ? j.Max : j.Min));
                }
            }
        });
    }

    public static void Set(DesignSession session, Action<HullProgram> edit) => Change(session, edit);

    public static void Remove(DesignSession session) =>
        session.Edit(s => s.Apply(PlanOp.SetProgram(s.Doc.Program, null)));

    /// <summary>Joint <paramref name="joint"/> stands at <paramref name="value"/> in state <paramref name="state"/> (null: it holds there).</summary>
    public static void SetPose(DesignSession session, int state, uint joint, short? value) =>
        Change(session, p =>
        {
            p.Poses.RemoveAll(x => x.State == state && x.Joint == joint);

            if (value is { } v)
                p.Poses.Add(new StatePose((byte)state, joint, v));

            p.Poses.Sort((a, b) => a.State != b.State ? a.State.CompareTo(b.State) : a.Joint.CompareTo(b.Joint));
        });

    /// <summary>
    /// Replaces the graph with the standard door sequence, if the interface has a door's calls and states:
    /// <list type="bullet">
    /// <item>Open: not when locked; Opening, then Open on arrival (Blocked if stopped).</item>
    /// <item>Close: not when something is in the way; Closing, then Closed and at once Locked on arrival; if something gets in the way, it stops and opens again.</item>
    /// <item>Lock: from Closed. Unlock: from Locked, back to Closed.</item>
    /// </list>
    /// False when a name is missing.
    /// </summary>
    public static bool StandardDoor(HullProgram program)
    {
        var face = program.Interface;
        int Call(string n) => face.Calls.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        int State(string n) => face.States.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        int open = Call("Open"), close = Call("Close"), lockCall = Call("Lock"), unlock = Call("Unlock");
        int closed = State("Closed"), opening = State("Opening"), opened = State("Open"), closing = State("Closing"), locked = State("Locked"), blocked = State("Blocked");

        if (Math.Min(Math.Min(Math.Min(open, close), Math.Min(lockCall, unlock)), Math.Min(Math.Min(closed, opening), Math.Min(Math.Min(opened, closing), Math.Min(locked, blocked)))) < 0)
            return false;

        program.Nodes.Clear();
        ushort id = 0;
        ushort Add(NodeKind kind, int col, int row, int a = 0, short b = 0)
        {
            program.Nodes.Add(new ProgramNode { Id = ++id, Kind = kind, X = (short)(20 + col * 200), Y = (short)(20 + row * 120), A = (byte)a, B = b });
            return id;
        }

        void Link(ushort from, int output, ushort to)
        {
            var i = program.IndexOf(from);
            var n = program.Nodes[i];

            if (output == 0)
                n.Out0 = to;
            else
                n.Out1 = to;

            program.Nodes[i] = n;
        }

        // Open.
        var onOpen = Add(NodeKind.OnCall, 0, 0, open);
        var isLocked = Add(NodeKind.IfState, 1, 0, locked);
        var setOpening = Add(NodeKind.SetState, 2, 0, opening);
        var waitOpen = Add(NodeKind.WaitArrived, 3, 0);
        var setOpen = Add(NodeKind.SetState, 4, 0, opened);
        var setBlocked = Add(NodeKind.SetState, 4, 1, blocked);
        Link(onOpen, 0, isLocked);
        Link(isLocked, 1, setOpening);
        Link(setOpening, 0, waitOpen);
        Link(waitOpen, 0, setOpen);
        Link(waitOpen, 1, setBlocked);

        // Close: refuse while in the way; on arrival Closed, then Locked; stopped on the way: open again.
        var onClose = Add(NodeKind.OnCall, 0, 2, close);
        var inWay = Add(NodeKind.IfBlocked, 1, 2);
        var setClosing = Add(NodeKind.SetState, 2, 2, closing);
        var waitClose = Add(NodeKind.WaitArrived, 3, 2);
        var setClosed = Add(NodeKind.SetState, 4, 2, closed);
        var setLocked = Add(NodeKind.SetState, 5, 2, locked);
        var reopen = Add(NodeKind.SetState, 4, 3, opening);
        var waitReopen = Add(NodeKind.WaitArrived, 5, 3);
        var setReopened = Add(NodeKind.SetState, 6, 3, opened);
        Link(onClose, 0, inWay);
        Link(inWay, 1, setClosing);
        Link(setClosing, 0, waitClose);
        Link(waitClose, 0, setClosed);
        Link(setClosed, 0, setLocked);
        Link(waitClose, 1, reopen);
        Link(reopen, 0, waitReopen);
        Link(waitReopen, 0, setReopened);

        // Lock and unlock.
        var onLock = Add(NodeKind.OnCall, 0, 4, lockCall);
        var isClosed = Add(NodeKind.IfState, 1, 4, closed);
        var lockIt = Add(NodeKind.SetState, 2, 4, locked);
        Link(onLock, 0, isClosed);
        Link(isClosed, 0, lockIt);
        var onUnlock = Add(NodeKind.OnCall, 0, 5, unlock);
        var isLocked2 = Add(NodeKind.IfState, 1, 5, locked);
        var unlockIt = Add(NodeKind.SetState, 2, 5, closed);
        Link(onUnlock, 0, isLocked2);
        Link(isLocked2, 0, unlockIt);
        return true;
    }

    private static void Change(DesignSession session, Action<HullProgram> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Program;
            var after = before?.Clone() ?? new HullProgram();
            mutate(after);
            s.Apply(PlanOp.SetProgram(before, after));
        });
}
