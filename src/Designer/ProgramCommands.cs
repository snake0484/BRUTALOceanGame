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

    /// <summary>The built-in Door interface (the same as <c>data/interfaces/door.json</c>), for doors made in code.</summary>
    public static ComponentInterface Door => new()
    {
        Name = "Door",
        Calls = ["Open", "Close", "Lock", "Unlock"],
        States = ["Closed", "Opening", "Open", "Closing", "Locked", "Blocked"],
    };

    /// <summary>The built-in Lamp interface (<c>data/interfaces/lamp.json</c>): Auto follows its sensor, On and Off override it.</summary>
    public static ComponentInterface Lamp => new()
    {
        Name = "Lamp",
        Calls = ["Auto", "On", "Off"],
        States = ["Auto", "On", "Off"],
    };

    /// <summary>The built-in Tool interface (<c>data/interfaces/tool.json</c>): Ready follows the trigger, Safe never fires.</summary>
    public static ComponentInterface Tool => new()
    {
        Name = "Tool",
        Calls = ["Ready", "Safe"],
        States = ["Ready", "Safe"],
    };

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
    /// Replaces the graph with the standard door sequence, if the interface has a door's calls and states. The door's
    /// leaf is the first hinge or slider that stands differently in Open and Closed; "got there" is that joint reaching
    /// its end, within <see cref="TimeoutSlack"/> times the time it needs (a part stopped on the way never gets there, so
    /// the wait times out: something is in the way).
    /// <list type="bullet">
    /// <item>Open: not when locked; Opening, wait until the leaf is open: Open; timed out: Blocked.</item>
    /// <item>Close: Closing, wait until the leaf is shut: Closed, then at once Locked; timed out: open again (Opening, wait, Open; timed out again: Blocked).</item>
    /// <item>Lock: from Closed. Unlock: from Locked, back to Closed.</item>
    /// </list>
    /// False when a name is missing.
    /// </summary>
    public static bool StandardDoor(HullProgram program, IReadOnlyList<HullJoint> joints, HullCoreSet? cores = null)
    {
        var face = program.Interface;
        int Call(string n) => face.Calls.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        int State(string n) => face.States.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        int open = Call("Open"), close = Call("Close"), lockCall = Call("Lock"), unlock = Call("Unlock");
        int closed = State("Closed"), opening = State("Opening"), opened = State("Open"), closing = State("Closing"), locked = State("Locked"), blocked = State("Blocked");

        if (Math.Min(Math.Min(Math.Min(open, close), Math.Min(lockCall, unlock)), Math.Min(Math.Min(closed, opening), Math.Min(Math.Min(opened, closing), Math.Min(locked, blocked)))) < 0)
            return false;

        // The leaf, its two ends, and how long it may take to get from one to the other.
        HullJoint? leaf = null;
        short openAt = 0, shutAt = 0;

        foreach (var j in joints)
        {
            if (j.Limited && program.PoseOf(opened, j.Id, out openAt) && program.PoseOf(closed, j.Id, out shutAt) && openAt != shutAt)
            {
                leaf = j;
                break;
            }
        }

        var timeout = leaf is null ? (short)(5 * ProgramRunner.TicksPerSecond)
            : (short)Math.Min(short.MaxValue, Math.Ceiling(Math.Abs(openAt - shutAt) / Math.Max(1.0, ProgramRunner.Speed(leaf)) * ProgramRunner.TicksPerSecond * TimeoutSlack) + ProgramRunner.TicksPerSecond / 2);

        program.Nodes.Clear();
        ushort id = 0;

        ushort Add(NodeKind kind, int col, double row, int a = 0, short b = 0)
        {
            program.Nodes.Add(new ProgramNode { Id = ++id, Kind = kind, X = (short)(20 + col * ColumnPixels), Y = (short)(20 + row * RowPixels), A = (byte)a, B = b });
            return id;
        }

        void Set(ushort node, Func<ProgramNode, ProgramNode> change)
        {
            var i = program.IndexOf(node);
            program.Nodes[i] = change(program.Nodes[i]);
        }

        // "The leaf is at its end": its limit switch there pressed, else the joint within tolerance (with no leaf, "in pose"); placed under the wait that tests it.
        ushort At(bool shut, int col, double row)
        {
            if (leaf is null)
                return Add(NodeKind.InPose, col, row);

            var end = shut ? shutAt : openAt;

            if (cores?.Cores.Find(c => c.Def == CoreIds.LimitSwitch && c.Joint == leaf.Id && c.At == end) is { } limit)
            {
                var pressed = Add(NodeKind.SignalIs, col, row, (int)Compare.Equal << 4, 1);
                var core = limit.Id;
                Set(pressed, n => n with { Joint = core });
                return pressed;
            }

            var c = Add(NodeKind.JointAt, col, row, PositionTolerance, shut ? shutAt : openAt);
            var joint = leaf.Id;
            Set(c, n => n with { Joint = joint });
            return c;
        }

        void Link(ushort from, int output, ushort to) => Set(from, n => output == 0 ? n with { Out0 = to } : n with { Out1 = to });

        void Test(ushort wait, ushort condition) => Set(wait, n => n with { In = condition });

        // Open.
        var onOpen = Add(NodeKind.OnCall, 0, 0, open);
        var isLocked = Add(NodeKind.IfState, 1, 0, locked);
        var setOpening = Add(NodeKind.SetState, 2, 0, opening);
        var waitOpen = Add(NodeKind.WaitUntil, 3, 0, 0, timeout);
        var setOpen = Add(NodeKind.SetState, 4, 0, opened);
        var setBlocked = Add(NodeKind.SetState, 4, 1, blocked);
        Test(waitOpen, At(false, 2, 1));
        Link(onOpen, 0, isLocked);
        Link(isLocked, 1, setOpening);
        Link(setOpening, 0, waitOpen);
        Link(waitOpen, 0, setOpen);
        Link(waitOpen, 1, setBlocked);

        // Close: on arrival Closed, then Locked; timed out (something in the way): open again.
        var onClose = Add(NodeKind.OnCall, 0, 2.3, close);
        var setClosing = Add(NodeKind.SetState, 1, 2.3, closing);
        var waitClose = Add(NodeKind.WaitUntil, 2, 2.3, 0, timeout);
        var setClosed = Add(NodeKind.SetState, 3, 2.3, closed);
        var setLocked = Add(NodeKind.SetState, 4, 2.3, locked);
        Test(waitClose, At(true, 1, 3.3));
        var reopen = Add(NodeKind.SetState, 3, 3.3, opening);
        var waitReopen = Add(NodeKind.WaitUntil, 4, 3.3, 0, timeout);
        var setReopened = Add(NodeKind.SetState, 5, 3.3, opened);
        var setStuck = Add(NodeKind.SetState, 5, 4.3, blocked);
        Test(waitReopen, At(false, 3, 4.3));
        Link(onClose, 0, setClosing);
        Link(setClosing, 0, waitClose);
        Link(waitClose, 0, setClosed);
        Link(setClosed, 0, setLocked);
        Link(waitClose, 1, reopen);
        Link(reopen, 0, waitReopen);
        Link(waitReopen, 0, setReopened);
        Link(waitReopen, 1, setStuck);

        // Lock and unlock.
        var onLock = Add(NodeKind.OnCall, 0, 5.6, lockCall);
        var isClosed = Add(NodeKind.IfState, 1, 5.6, closed);
        var lockIt = Add(NodeKind.SetState, 2, 5.6, locked);
        Link(onLock, 0, isClosed);
        Link(isClosed, 0, lockIt);
        var onUnlock = Add(NodeKind.OnCall, 0, 6.8, unlock);
        var isLocked2 = Add(NodeKind.IfState, 1, 6.8, locked);
        var unlockIt = Add(NodeKind.SetState, 2, 6.8, closed);
        Link(onUnlock, 0, isLocked2);
        Link(isLocked2, 0, unlockIt);
        return true;
    }

    /// <summary>
    /// Replaces the graph with the lamp sequence (the Lamp interface): on start and on Auto, full brightness and then
    /// for ever: wait until <paramref name="sensor"/> detects something, light <paramref name="lamp"/>, wait until it
    /// detects nothing, put it out. On and Off set the lamp and stop following the sensor. False when a name is missing.
    /// </summary>
    public static bool LampSequence(HullProgram program, uint sensor, uint lamp)
    {
        var face = program.Interface;
        int Call(string n) => face.Calls.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        int State(string n) => face.States.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        int auto = Call("Auto"), on = Call("On"), off = Call("Off"), autoState = State("Auto"), onState = State("On"), offState = State("Off");

        if (Math.Min(Math.Min(auto, on), Math.Min(Math.Min(off, autoState), Math.Min(onState, offState))) < 0)
            return false;

        program.Nodes.Clear();
        ushort id = 0;

        ushort Add(NodeKind kind, int col, double row, int a = 0, short b = 0, uint core = 0)
        {
            program.Nodes.Add(new ProgramNode { Id = ++id, Kind = kind, X = (short)(20 + col * ColumnPixels), Y = (short)(20 + row * RowPixels), A = (byte)a, B = b, Joint = core });
            return id;
        }

        void Link(ushort from, int output, ushort to)
        {
            var i = program.IndexOf(from);
            program.Nodes[i] = output == 0 ? program.Nodes[i] with { Out0 = to } : program.Nodes[i] with { Out1 = to };
        }

        void Test(ushort wait, ushort condition)
        {
            var i = program.IndexOf(wait);
            program.Nodes[i] = program.Nodes[i] with { In = condition };
        }

        // Lamp inputs: 0 On, 1 Brightness. Sensor signals: 0 Detected.
        var onStart = Add(NodeKind.OnStart, 0, 0);
        var onAuto = Add(NodeKind.OnCall, 0, 1, auto);
        var setAuto = Add(NodeKind.SetState, 1, 0, autoState);
        var bright = Add(NodeKind.SetInput, 2, 0, 1, 100, lamp);
        var waitSeen = Add(NodeKind.WaitUntil, 3, 0);
        var light = Add(NodeKind.SetInput, 4, 0, 0, 1, lamp);
        var waitGone = Add(NodeKind.WaitUntil, 5, 0);
        var dark = Add(NodeKind.SetInput, 6, 0, 0, 0, lamp);
        Test(waitSeen, Add(NodeKind.SignalIs, 3, 1, (int)Compare.Equal << 4, 1, sensor));
        Test(waitGone, Add(NodeKind.SignalIs, 5, 1, (int)Compare.Equal << 4, 0, sensor));
        Link(onStart, 0, setAuto);
        Link(onAuto, 0, setAuto);
        Link(setAuto, 0, bright);
        Link(bright, 0, waitSeen);
        Link(waitSeen, 0, light);
        Link(light, 0, waitGone);
        Link(waitGone, 0, dark);
        Link(dark, 0, waitSeen);

        var onOn = Add(NodeKind.OnCall, 0, 2.5, on);
        var setOn = Add(NodeKind.SetState, 1, 2.5, onState);
        var fullOn = Add(NodeKind.SetInput, 2, 2.5, 1, 100, lamp);
        var lightOn = Add(NodeKind.SetInput, 3, 2.5, 0, 1, lamp);
        Link(onOn, 0, setOn);
        Link(setOn, 0, fullOn);
        Link(fullOn, 0, lightOn);

        var onOff = Add(NodeKind.OnCall, 0, 3.5, off);
        var setOff = Add(NodeKind.SetState, 1, 3.5, offState);
        var darkOff = Add(NodeKind.SetInput, 2, 3.5, 0, 0, lamp);
        Link(onOff, 0, setOff);
        Link(setOff, 0, darkOff);
        return true;
    }

    /// <summary>
    /// Replaces the graph with the trigger sequence (the Tool interface): on start and on Ready, full power, then for
    /// ever: wait until <paramref name="grip"/>'s trigger is pulled, fire <paramref name="emitter"/>, wait until it is let
    /// go, stop firing. Safe stops firing and stops following the trigger. False when a name is missing.
    /// </summary>
    public static bool TriggerSequence(HullProgram program, uint grip, uint emitter)
    {
        var face = program.Interface;
        int ready = face.Calls.FindIndex(c => c.Equals("Ready", StringComparison.OrdinalIgnoreCase)), safe = face.Calls.FindIndex(c => c.Equals("Safe", StringComparison.OrdinalIgnoreCase));
        int readyState = face.States.FindIndex(c => c.Equals("Ready", StringComparison.OrdinalIgnoreCase)), safeState = face.States.FindIndex(c => c.Equals("Safe", StringComparison.OrdinalIgnoreCase));

        if (Math.Min(Math.Min(ready, safe), Math.Min(readyState, safeState)) < 0)
            return false;

        program.Nodes.Clear();
        ushort id = 0;

        ushort Add(NodeKind kind, int col, double row, int a = 0, short b = 0, uint core = 0)
        {
            program.Nodes.Add(new ProgramNode { Id = ++id, Kind = kind, X = (short)(20 + col * ColumnPixels), Y = (short)(20 + row * RowPixels), A = (byte)a, B = b, Joint = core });
            return id;
        }

        void Set(ushort node, Func<ProgramNode, ProgramNode> change)
        {
            var i = program.IndexOf(node);
            program.Nodes[i] = change(program.Nodes[i]);
        }

        // Emitter inputs: 0 Fire, 1 Power. Grip signals: 1 Trigger.
        var onStart = Add(NodeKind.OnStart, 0, 0);
        var onReady = Add(NodeKind.OnCall, 0, 1, ready);
        var setReady = Add(NodeKind.SetState, 1, 0, readyState);
        var full = Add(NodeKind.SetInput, 2, 0, 1, 100, emitter);
        var pulled = Add(NodeKind.WaitUntil, 3, 0);
        var fire = Add(NodeKind.SetInput, 4, 0, 0, 1, emitter);
        var released = Add(NodeKind.WaitUntil, 5, 0);
        var stop = Add(NodeKind.SetInput, 6, 0, 0, 0, emitter);
        var down = Add(NodeKind.SignalIs, 3, 1, 1 | (int)Compare.Equal << 4, 1, grip);
        var up = Add(NodeKind.SignalIs, 5, 1, 1 | (int)Compare.Equal << 4, 0, grip);
        Set(pulled, n => n with { In = down });
        Set(released, n => n with { In = up });
        Set(onStart, n => n with { Out0 = setReady });
        Set(onReady, n => n with { Out0 = setReady });
        Set(setReady, n => n with { Out0 = full });
        Set(full, n => n with { Out0 = pulled });
        Set(pulled, n => n with { Out0 = fire });
        Set(fire, n => n with { Out0 = released });
        Set(released, n => n with { Out0 = stop });
        Set(stop, n => n with { Out0 = pulled });

        var onSafe = Add(NodeKind.OnCall, 0, 2.5, safe);
        var setSafe = Add(NodeKind.SetState, 1, 2.5, safeState);
        var cold = Add(NodeKind.SetInput, 2, 2.5, 0, 0, emitter);
        Set(onSafe, n => n with { Out0 = setSafe });
        Set(setSafe, n => n with { Out0 = cold });
        return true;
    }

    /// <summary>How much longer than a part needs to reach its end a wait allows before it counts as stopped.</summary>
    public const double TimeoutSlack = 1.5;

    /// <summary>How near its end a joint counts as there (stored units: 0.1 degree, or mm).</summary>
    public const byte PositionTolerance = 5;

    /// <summary>The standard sequence's layout: room between nodes (a node is 190 pixels wide).</summary>
    private const int ColumnPixels = 260, RowPixels = 110;

    private static void Change(DesignSession session, Action<HullProgram> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Program;
            var after = before?.Clone() ?? new HullProgram();
            mutate(after);
            s.Apply(PlanOp.SetProgram(before, after));
        });
}
