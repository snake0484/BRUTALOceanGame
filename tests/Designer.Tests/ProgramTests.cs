using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Logic;

namespace BrutalGame.Designer.Tests;

/// <summary>Component programmes: plan bytes and validation, the standard door sequence run tick by tick, and poses from the state record alone.</summary>
public class ProgramTests
{
    private static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly HullMaterialCatalog Catalog = HullMaterialCatalog.Load(DataFolder);
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");
    private static readonly CoreCatalog Cores = CoreCatalog.Load(DataFolder);

    // Door states and calls by index.
    private const int Closed = 0, Opening = 1, Open = 2, Closing = 3, Locked = 4;
    private const int CallOpen = 0, CallClose = 1, CallUnlock = 3;

    [Fact]
    public void Programme_RoundTrips_AndValidates()
    {
        var door = SampleComponents.SlidingDoor(Alloy);

        Assert.Equal(PlanFormat.CoreFormat, PlanFormat.FormatOf(door));
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(door), out var back, out var error), error.ToString());
        Assert.Equal(PlanFormat.HashBody(door), PlanFormat.HashBody(back));
        Assert.Equal(door.Program!.Interface.Key, back.Program!.Interface.Key);
        Assert.Equal(door.Program.Nodes, back.Program.Nodes);
        Assert.Equal(door.Program.Poses, back.Program.Poses);

        var log = new ValidationLog();
        PlanValidator.Validate(back, log, Catalog);
        Assert.DoesNotContain(log.Issues, i => i.Code is IssueCode.ProgramShape or IssueCode.ProgramCall);
    }

    [Fact]
    public void Validator_FlagsBrokenLinks_BadPoses_AndUnansweredCalls()
    {
        var door = SampleComponents.SlidingDoor(Alloy).Clone();
        var program = door.Program!;
        var first = program.Nodes[0];
        program.Nodes[0] = first with { Out0 = 999 };
        program.Poses.Add(new StatePose(Open, door.Joints!.Joints[0].Id, 5000));
        program.Nodes.RemoveAll(n => n.Kind == NodeKind.OnCall && n.A == 2);

        var log = new ValidationLog();
        PlanValidator.Validate(door, log, Catalog);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.ProgramShape && i.Value == first.Id);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.ProgramShape && i.Value == Open);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.ProgramCall && i.Severity == Severity.Warning && i.Value == 2);
    }

    [Fact]
    public void SlidingDoor_OpensClosesThenLocks()
    {
        var door = SampleComponents.SlidingDoor(Alloy);
        var program = door.Program!;
        var joints = door.Joints!.Joints;
        var run = Rig(door);
        ProgramRunner.Reset(program, joints, run, 0);
        Assert.Equal(Closed, run.State);

        // Open: 1.05 m at 0.8 m/s, about 79 ticks.
        Assert.True(ProgramRunner.Call(program, run, CallOpen));
        Steps(program, joints, run, 1, 1);
        Assert.Equal(Opening, run.State);
        Assert.Equal(400, ProgramRunner.Position(program, joints, run, 0, 1 + 30), 6);
        Steps(program, joints, run, 2, 120);
        Assert.Equal(Open, run.State);
        Assert.Equal(1050, ProgramRunner.Position(program, joints, run, 0, 120), 6);

        // Close: shut, then the bolt shoots 80 mm at 0.2 m/s.
        ProgramRunner.Call(program, run, CallClose);
        Steps(program, joints, run, 200, 200);
        Assert.Equal(Closing, run.State);
        Assert.Equal(650, ProgramRunner.Position(program, joints, run, 0, 230), 6);
        Steps(program, joints, run, 201, 300);
        Assert.Equal(Locked, run.State);
        Assert.Equal(0, ProgramRunner.Position(program, joints, run, 0, 300), 6);
        Assert.Equal(80, ProgramRunner.Position(program, joints, run, 1, 400), 6);

        // A locked door ignores Open; unlocked, it opens again.
        ProgramRunner.Call(program, run, CallOpen);
        Steps(program, joints, run, 401, 410);
        Assert.Equal(Locked, run.State);
        ProgramRunner.Call(program, run, CallUnlock);
        Steps(program, joints, run, 411, 411);
        Assert.Equal(Closed, run.State);
        ProgramRunner.Call(program, run, CallOpen);
        Steps(program, joints, run, 412, 412);
        Assert.Equal(Opening, run.State);
    }

    /// <summary>Open, then start closing; ticks 1 to 150. The sliding door's waits time out after 149 ticks (1.3 s of travel x 1.5 + 0.5 s).</summary>
    private static (HullProgram Program, List<HullJoint> Joints, ProgramInstance Run) ClosingDoor()
    {
        var door = SampleComponents.SlidingDoor(Alloy);
        var run = Rig(door);
        ProgramRunner.Reset(door.Program!, door.Joints!.Joints, run, 0);
        ProgramRunner.Call(door.Program!, run, CallOpen);
        Steps(door.Program!, door.Joints.Joints, run, 1, 120);
        ProgramRunner.Call(door.Program!, run, CallClose);
        Steps(door.Program!, door.Joints.Joints, run, 121, 150);
        return (door.Program!, door.Joints.Joints, run);
    }

    [Fact]
    public void Jammed_WhileClosing_TimesOut_ThenOpensAgain()
    {
        var (program, joints, run) = ClosingDoor();
        var midway = ProgramRunner.Position(program, joints, run, 0, 150);
        Assert.InRange(midway, 1, 1049);

        // Something in the doorway: the leaf stops where it is; the sequence sees it never get shut.
        run.Obstructed = true;
        Steps(program, joints, run, 151, 269);
        Assert.Equal(Closing, run.State);
        Assert.True(run.Held);
        Assert.InRange(ProgramRunner.Position(program, joints, run, 0, 269), midway - 15, midway + 1);

        // Timed out: it opens again, and does once the doorway clears.
        Steps(program, joints, run, 270, 300);
        Assert.Equal(Opening, run.State);
        run.Obstructed = false;
        Steps(program, joints, run, 301, 400);
        Assert.Equal(Open, run.State);
        Assert.Equal(1050, ProgramRunner.Position(program, joints, run, 0, 400), 6);
    }

    [Fact]
    public void Jammed_Briefly_ClosingCarriesOn()
    {
        var (program, joints, run) = ClosingDoor();
        run.Obstructed = true;
        Steps(program, joints, run, 151, 170);
        run.Obstructed = false;
        Steps(program, joints, run, 171, 360);
        Assert.Equal(Locked, run.State);
        Assert.Equal(0, ProgramRunner.Position(program, joints, run, 0, 360), 6);
    }

    [Fact]
    public void Jammed_Throughout_EndsBlocked()
    {
        var (program, joints, run) = ClosingDoor();
        run.Obstructed = true;
        Steps(program, joints, run, 151, 500);
        Assert.Equal(5, run.State);   // Blocked
        Assert.Equal(0, run.Node);
    }

    [Fact]
    public void StateRecord_Alone_GivesThePose()
    {
        var door = SampleComponents.SlidingDoor(Alloy);
        var program = door.Program!;
        var joints = door.Joints!.Joints;
        var run = Rig(door);
        ProgramRunner.Reset(program, joints, run, 0);
        ProgramRunner.Call(program, run, CallOpen);
        Steps(program, joints, run, 1, 50);

        // What a client gets: the state, the tick it was entered, where the joints started and are driven to, held or not. No graph.
        var copy = new ProgramInstance { State = run.State, Entered = run.Entered, Held = run.Held };
        run.Start.CopyTo(copy.Start, 0);
        run.Target.CopyTo(copy.Target, 0);

        for (var tick = 50L; tick < 120; tick++)
        {
            Steps(program, joints, run, tick, tick);

            if (run.State != copy.State)
                break;

            Assert.Equal(ProgramRunner.Position(program, joints, run, 0, tick), ProgramRunner.Position(program, joints, copy, 0, tick), 9);
        }
    }

    [Fact]
    public void BuiltInDoor_MatchesTheData()
    {
        var json = File.ReadAllText(Path.Combine(DataFolder, "interfaces", "door.json"));
        Assert.Equal(InterfaceLibrary.Door.Key, ComponentInterface.FromJson(json)!.Key);

        var library = new InterfaceLibrary(DataFolder, Path.Combine(Path.GetTempPath(), "brutal-interfaces-none"));
        library.Refresh();
        Assert.Contains(library.BuiltIn, i => i.Key == InterfaceLibrary.Door.Key);
    }

    [Fact]
    public void StandardDoor_WaitsOnTheLeaf_WithATimeout()
    {
        var door = SampleComponents.SlidingDoor(Alloy);
        var program = door.Program!;
        var leaf = door.Joints!.Joints[0];
        var waits = program.Nodes.Where(n => n.Kind == NodeKind.WaitUntil).ToList();

        Assert.Equal(3, waits.Count);
        Assert.All(waits, w => Assert.Equal(149, w.B));
        Assert.All(waits, w => Assert.Equal(NodeKind.SignalIs, program.Nodes[program.IndexOf(w.In)].Kind));
        Assert.All(waits, w => Assert.Equal(leaf.Id, door.Cores!.Cores[door.Cores.IndexOf(program.Nodes[program.IndexOf(w.In)].Joint)].Joint));

        // With no limit switches it tests the joint itself.
        var bare = program.Clone();
        Assert.True(ProgramCommands.StandardDoor(bare, door.Joints.Joints));
        Assert.All(bare.Nodes.Where(n => n.Kind == NodeKind.WaitUntil), w => Assert.Equal(NodeKind.JointAt, bare.Nodes[bare.IndexOf(w.In)].Kind));
        Assert.DoesNotContain(program.Nodes, n => n.Kind is NodeKind.WaitArrived or NodeKind.IfBlocked);

        // Laid out with room: no two nodes overlap.
        foreach (var a in program.Nodes)
        {
            foreach (var b in program.Nodes)
                Assert.True(a.Id == b.Id || Math.Abs(a.X - b.X) >= 200 || Math.Abs(a.Y - b.Y) >= 100, $"nodes {a.Id} and {b.Id} overlap");
        }
    }

    [Fact]
    public void Format15_Programmes_StillRead()
    {
        // A format 15 node: 14 bytes, no condition link or joint.
        var old = new HullProgram { Interface = InterfaceLibrary.Door };
        old.Nodes.Add(new ProgramNode { Id = 1, Kind = NodeKind.OnCall, X = 3, Y = 4, A = 1, Out0 = 2 });
        old.Nodes.Add(new ProgramNode { Id = 2, Kind = NodeKind.SetState, A = 3 });
        var wide = old.ToBytes();
        var header = wide.Length - 1 - old.Nodes.Count * HullProgram.NodeBytes - 1;
        var narrow = new List<byte>(wide[..(header + 1)]);

        for (var i = 0; i < old.Nodes.Count; i++)
            narrow.AddRange(wide.AsSpan(header + 1 + i * HullProgram.NodeBytes, HullProgram.NarrowNodeBytes).ToArray());

        narrow.Add(0);
        var back = HullProgram.Read(narrow.ToArray(), out var consumed, narrow: true);

        Assert.NotNull(back);
        Assert.Equal(narrow.Count, consumed);
        Assert.Equal(old.Nodes, back!.Nodes);
    }

    /// <summary>A run of <paramref name="door"/> with its cores laid out (its waits read its limit switches).</summary>
    private static ProgramInstance Rig(PlanDocument door) => new() { Cores = CoreLayout.Build(door.Cores, Cores, door.Joints!.Joints) };

    private static void Steps(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance run, long from, long to)
    {
        for (var tick = from; tick <= to; tick++)
            ProgramRunner.Step(program, joints, run, tick);
    }
}
