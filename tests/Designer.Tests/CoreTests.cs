using System.Diagnostics;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Logic;
using Xunit.Abstractions;

namespace BrutalGame.Designer.Tests;

/// <summary>Effectors and sensors (D88): the catalog, plan bytes, the doors' motors and limit switches, the proximity lamp, power loss, determinism and tick cost.</summary>
public class CoreTests(ITestOutputHelper output)
{
    private static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly CoreCatalog Catalog = CoreCatalog.Load(DataFolder);
    private static readonly HullMaterialCatalog Materials = HullMaterialCatalog.Load(DataFolder);
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");

    private const int Closed = 0, Open = 2, Closing = 3, Locked = 4;
    private const int CallOpen = 0, CallClose = 1;
    private const int LampOn = 1, LampOff = 2;

    /// <summary>A world with one thing in it, <see cref="Distance"/> metres ahead of every sensor.</summary>
    private sealed class Probe : ISensorWorld
    {
        public double Distance = double.PositiveInfinity;

        public double Nearest(Vector3d origin, Vector3d direction, double halfAngleRadians, double range) => Distance;
    }

    [Fact]
    public void Catalog_LoadsEveryCore_InTheRightRole()
    {
        Assert.Equal(9, Catalog.All.Count);
        Assert.All(Catalog.All, d => Assert.True(d.IsValid(), d.Id));
        Assert.True(Catalog.TryGet(CoreIds.LimitSwitch, out var limit) && limit.IsSensor);
        Assert.True(Catalog.TryGet(CoreIds.Lamp, out var lamp) && !lamp.IsSensor);
        Assert.True(Catalog.TryGet(CoreIds.Proximity, out var proximity));
        Assert.Equal(6, proximity.Period);

        // Signals must be the kind's, in order.
        var wrong = CoreCatalog.FromJson("""{ "id": "x", "kind": "Light", "signals": [ { "name": "Lit", "step": 1, "max": 1 } ] }""");
        Assert.NotNull(wrong);
        Assert.False(wrong!.IsValid());
    }

    [Fact]
    public void Cores_RoundTrip_AndValidate()
    {
        var door = SampleComponents.SlidingDoor(Alloy);

        Assert.Equal(PlanFormat.CoreFormat, PlanFormat.FormatOf(door));
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(door), out var back, out var error), error.ToString());
        Assert.Equal(PlanFormat.HashBody(door), PlanFormat.HashBody(back));
        Assert.Equal(door.Cores!.ToBytes(), back.Cores!.ToBytes());

        var log = new ValidationLog();
        PlanValidator.Validate(back, log, Materials);
        Assert.DoesNotContain(log.Issues, i => i.Code is IssueCode.CoreShape or IssueCode.ProgramShape);

        back.Cores.Cores[0].Joint = 9999;
        log = new ValidationLog();
        PlanValidator.Validate(back, log, Materials);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.CoreShape);

        // A plan with no cores keeps its older format and bytes.
        Assert.Equal(PlanFormat.OpeningFormat, PlanFormat.FormatOf(SampleComponents.DockingPort(Alloy)));
    }

    [Fact]
    public void SlidingDoor_MotorsAndLimitSwitches_Report()
    {
        var (door, run) = Rig(SampleComponents.SlidingDoor(Alloy));
        var program = door.Program!;
        var joints = door.Joints!.Joints;
        var layout = run.Cores!;
        var motor = Signal(layout, door, CoreIds.LinearMotor, 0, 0);
        var shut = Signal(layout, door, CoreIds.LimitSwitch, 0, 0);
        var open = Signal(layout, door, CoreIds.LimitSwitch, 1, 0);

        Steps(program, joints, run, 0, 1);
        Assert.Equal(1, run.Signals[shut]);
        Assert.Equal(0, run.Signals[open]);

        ProgramRunner.Call(program, run, CallOpen);
        Steps(program, joints, run, 2, 30);
        Assert.Equal(1050, run.Signals[motor]);
        Assert.Equal(1, run.Signals[motor + 1]);   // Moving
        Assert.Equal(0, run.Signals[shut]);

        Steps(program, joints, run, 31, 120);
        Assert.Equal(Open, run.State);
        Assert.Equal(1, run.Signals[open]);
        Assert.Equal(0, run.Signals[motor + 1]);

        ProgramRunner.Call(program, run, CallClose);
        Steps(program, joints, run, 121, 400);
        Assert.Equal(Locked, run.State);
        Assert.Equal(1, run.Signals[shut]);
        Assert.Equal(80, run.Signals[Signal(layout, door, CoreIds.SolenoidBolt, 0, 0)]);
    }

    [Fact]
    public void Jammed_MotorSaysSo_AndTheDoorOpensAgain()
    {
        var (door, run) = Rig(SampleComponents.SlidingDoor(Alloy));
        var program = door.Program!;
        var joints = door.Joints!.Joints;
        var jammed = Signal(run.Cores!, door, CoreIds.LinearMotor, 0, 2);

        ProgramRunner.Call(program, run, CallOpen);
        Steps(program, joints, run, 1, 120);
        ProgramRunner.Call(program, run, CallClose);
        Steps(program, joints, run, 121, 150);
        run.Obstructed = true;
        Steps(program, joints, run, 151, 200);
        Assert.Equal(Closing, run.State);
        Assert.Equal(1, run.Signals[jammed]);

        Steps(program, joints, run, 201, 300);
        run.Obstructed = false;
        Steps(program, joints, run, 301, 420);
        Assert.Equal(Open, run.State);
        Assert.Equal(0, run.Signals[jammed]);
    }

    [Fact]
    public void Unpowered_SensorsGoDead_MotorsStop_ThenCarryOn()
    {
        var (door, run) = Rig(SampleComponents.SlidingDoor(Alloy));
        var program = door.Program!;
        var joints = door.Joints!.Joints;
        var shut = Signal(run.Cores!, door, CoreIds.LimitSwitch, 0, 0);

        ProgramRunner.Call(program, run, CallOpen);
        Steps(program, joints, run, 1, 20);
        run.Unpowered = true;
        Steps(program, joints, run, 21, 21);
        var stopped = ProgramRunner.Position(program, joints, run, 0, 21);
        Steps(program, joints, run, 22, 60);

        Assert.True(run.Held);
        Assert.Equal(stopped, ProgramRunner.Position(program, joints, run, 0, 60), 6);
        Assert.False(run.IsLive(shut));

        run.Unpowered = false;
        Steps(program, joints, run, 61, 200);
        Assert.Equal(Open, run.State);
        Assert.True(run.IsLive(shut));
    }

    [Fact]
    public void ProximityLamp_LightsWhileSomethingIsNear()
    {
        var (lamp, run) = Rig(SampleComponents.ProximityLamp(Alloy));
        var program = lamp.Program!;
        var joints = lamp.Joints?.Joints ?? [];
        var probe = (Probe)run.World!;
        var detected = Signal(run.Cores!, lamp, CoreIds.Proximity, 0, 0);
        var lit = Signal(run.Cores!, lamp, CoreIds.Lamp, 0, 2);

        Steps(program, joints, run, 1, 30);
        Assert.Equal(0, run.Signals[detected]);
        Assert.Equal(0, run.Signals[lit]);

        // 2.3 m away: seen at the sensor's next reading (10 a second), its range read to the nearest 0.5 m.
        probe.Distance = 2.3;
        Steps(program, joints, run, 31, 40);
        Assert.Equal(1, run.Signals[detected]);
        Assert.Equal(5, run.Signals[detected + 1]);
        Assert.Equal(1, run.Signals[lit]);

        // Out of range: out again.
        probe.Distance = 6;
        Steps(program, joints, run, 41, 50);
        Assert.Equal(0, run.Signals[lit]);

        // Off overrides the sensor; Auto follows it again.
        ProgramRunner.Call(program, run, LampOff);
        probe.Distance = 1;
        Steps(program, joints, run, 51, 80);
        Assert.Equal(0, run.Signals[lit]);
        ProgramRunner.Call(program, run, 0);
        Steps(program, joints, run, 81, 100);
        Assert.Equal(1, run.Signals[lit]);
        ProgramRunner.Call(program, run, LampOn);
        probe.Distance = 9;
        Steps(program, joints, run, 101, 120);
        Assert.Equal(1, run.Signals[lit]);
    }

    [Fact]
    public void Runs_AreDeterministic()
    {
        var a = Script();
        var b = Script();

        Assert.Equal(a.Count, b.Count);

        for (var i = 0; i < a.Count; i++)
            Assert.Equal(a[i], b[i]);

        static List<string> Script()
        {
            var (door, run) = Rig(SampleComponents.SlidingDoor(Alloy));
            var log = new List<string>();

            for (var tick = 1L; tick < 1200; tick++)
            {
                if (tick % 300 == 1)
                    ProgramRunner.Call(door.Program!, run, tick % 600 == 1 ? CallOpen : CallClose);

                run.Obstructed = tick is > 450 and < 470;
                run.Unpowered = tick is > 800 and < 830;

                if (ProgramRunner.Step(door.Program!, door.Joints!.Joints, run, tick))
                    log.Add($"{tick} {run.State} {run.Entered} {run.Held} {string.Join(',', run.Start)} {string.Join(',', run.Target)}");

                log.Add(string.Join(',', run.Signals.Take(run.Cores!.Signals)) + $" {run.Live[0]}");
            }

            return log;
        }
    }

    [Fact]
    public void Step_AllocatesNothing()
    {
        var (door, run) = Rig(SampleComponents.SlidingDoor(Alloy));
        var (lamp, lampRun) = Rig(SampleComponents.ProximityLamp(Alloy));
        Cycle(door, run, lamp, lampRun, 1, 700);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Cycle(door, run, lamp, lampRun, 700, 2000);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void TickCost_PerComponent()
    {
        const int count = 1000;
        const int ticks = 600;
        var doors = new (PlanDocument Doc, ProgramInstance Run)[count];

        for (var i = 0; i < count; i++)
            doors[i] = Rig(SampleComponents.SlidingDoor(Alloy));

        var program = doors[0].Doc.Program!;
        var joints = doors[0].Doc.Joints!.Joints;
        var watch = Stopwatch.StartNew();

        for (var tick = 1L; tick <= ticks; tick++)
        {
            for (var i = 0; i < count; i++)
            {
                var run = doors[i].Run;

                if ((tick + i) % 200 == 1)
                    ProgramRunner.Call(program, run, (tick + i) / 200 % 2 == 0 ? CallOpen : CallClose);

                ProgramRunner.Step(program, joints, run, tick);
            }
        }

        var ns = watch.Elapsed.TotalNanoseconds / (count * (double)ticks);
        output.WriteLine($"sliding door (4 cores): {ns:F0} ns a tick, declared {doors[0].Run.Cores!.TickCostNs} ns");
        Assert.True(ns < 5000, $"{ns:F0} ns a door a tick");
    }

    private static void Cycle(PlanDocument door, ProgramInstance run, PlanDocument lamp, ProgramInstance lampRun, long from, long to)
    {
        var probe = (Probe)lampRun.World!;

        for (var tick = from; tick < to; tick++)
        {
            if (tick % 300 == 1)
                ProgramRunner.Call(door.Program!, run, tick % 600 == 1 ? CallOpen : CallClose);

            probe.Distance = tick % 120 < 60 ? 1.5 : double.PositiveInfinity;
            ProgramRunner.Step(door.Program!, door.Joints!.Joints, run, tick);
            ProgramRunner.Step(lamp.Program!, [], lampRun, tick);
        }
    }

    /// <summary>A run of <paramref name="doc"/> at rest at tick 0, its cores laid out, looking at a probe.</summary>
    private static (PlanDocument, ProgramInstance) Rig(PlanDocument doc)
    {
        var joints = doc.Joints?.Joints ?? [];
        var run = new ProgramInstance { Cores = CoreLayout.Build(doc.Cores, Catalog, joints), World = new Probe() };
        Assert.Equal(0, run.Cores.Missing);
        ProgramRunner.Reset(doc.Program!, joints, run, 0);
        return (doc, run);
    }

    /// <summary>Where signal <paramref name="signal"/> of the <paramref name="nth"/> core of catalog key <paramref name="def"/> sits in the bank.</summary>
    private static int Signal(CoreLayout layout, PlanDocument doc, uint def, int nth, int signal)
    {
        var cores = doc.Cores!.Cores.Where(c => c.Def == def).ToList();
        return layout.Entries[layout.IndexOf(cores[nth].Id)].SignalBase + signal;
    }

    private static void Steps(HullProgram program, IReadOnlyList<HullJoint> joints, ProgramInstance run, long from, long to)
    {
        for (var tick = from; tick <= to; tick++)
            ProgramRunner.Step(program, joints, run, tick);
    }
}
