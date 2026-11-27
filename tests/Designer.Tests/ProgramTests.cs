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

    // Door states and calls by index.
    private const int Closed = 0, Opening = 1, Open = 2, Closing = 3, Locked = 4;
    private const int CallOpen = 0, CallClose = 1, CallUnlock = 3;

    [Fact]
    public void Programme_RoundTrips_AndValidates()
    {
        var door = SampleComponents.SlidingDoor(Alloy);

        Assert.Equal(PlanFormat.ProgramFormat, PlanFormat.FormatOf(door));
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
        var run = new ProgramInstance();
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

    [Fact]
    public void Blocked_WhileClosing_StopsThenOpensAgain()
    {
        var door = SampleComponents.SlidingDoor(Alloy);
        var program = door.Program!;
        var joints = door.Joints!.Joints;
        var run = new ProgramInstance();
        ProgramRunner.Reset(program, joints, run, 0);
        ProgramRunner.Call(program, run, CallOpen);
        Steps(program, joints, run, 1, 120);
        ProgramRunner.Call(program, run, CallClose);
        Steps(program, joints, run, 121, 150);
        var midway = ProgramRunner.Position(program, joints, run, 0, 150);
        Assert.InRange(midway, 1, 1049);

        // Something steps into the doorway: the door stops where it is and opens again from there.
        run.Blocked = true;
        Steps(program, joints, run, 151, 151);
        Assert.Equal(Opening, run.State);
        Assert.InRange(run.Start[0], midway - 15, midway + 1);   // one more tick of closing, then stopped
        run.Blocked = false;
        Steps(program, joints, run, 152, 260);
        Assert.Equal(Open, run.State);

        // Refused while something is in the way at the start.
        run.Blocked = true;
        ProgramRunner.Call(program, run, CallClose);
        Steps(program, joints, run, 261, 261);
        Assert.Equal(Open, run.State);
    }

    [Fact]
    public void StateRecord_Alone_GivesThePose()
    {
        var door = SampleComponents.SlidingDoor(Alloy);
        var program = door.Program!;
        var joints = door.Joints!.Joints;
        var run = new ProgramInstance();
        ProgramRunner.Reset(program, joints, run, 0);
        ProgramRunner.Call(program, run, CallOpen);
        Steps(program, joints, run, 1, 50);

        // What a client gets: the state, the tick it was entered, where the joints started, held or not. No graph.
        var copy = new ProgramInstance { State = run.State, Entered = run.Entered, Held = run.Held };
        run.Start.CopyTo(copy.Start, 0);

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

    private static void Steps(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance run, long from, long to)
    {
        for (var tick = from; tick <= to; tick++)
            ProgramRunner.Step(program, joints, run, tick);
    }
}
