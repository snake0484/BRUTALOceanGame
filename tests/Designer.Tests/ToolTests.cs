using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Logic;

namespace BrutalGame.Designer.Tests;

/// <summary>Hand tools (D91): the Tool plan level, its mass budget, and the cutting torch's trigger, heat and battery.</summary>
public class ToolTests
{
    private static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly CoreCatalog Catalog = CoreCatalog.Load(DataFolder);
    private static readonly HullMaterialCatalog Materials = HullMaterialCatalog.Load(DataFolder);
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");

    private const int Ready = 0, Safe = 1;

    [Fact]
    public void Torch_IsAToolPlan_WithNoHullFeatures()
    {
        var torch = SampleTools.CuttingTorch(Alloy);

        Assert.Equal(PlanLevel.Tool, torch.Level);
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(torch), out var back, out var error), error.ToString());
        Assert.Equal(PlanLevel.Tool, back.Level);
        Assert.Equal(PlanFormat.HashBody(torch), PlanFormat.HashBody(back));

        var log = new ValidationLog();
        PlanValidator.Validate(back, log, Materials);
        Assert.True(!log.Issues.Exists(i => i.Severity == Severity.Error), string.Join("; ", log.Issues.Where(i => i.Severity == Severity.Error).Select(i => $"{i.Code} {i.Value}")));

        // A window is the hull's: refused on a tool.
        var session = new DesignSession();
        session.Open(back.Clone(), "");
        WindowCommands.AddWindow(session, new Vector3d(0, 0.095, 0.05), new Vector3d(0, 1, 0), Alloy);
        PlanValidator.Validate(session.Doc, log, Materials);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.ToolFeature && i.Value == 3);

        // And a tool cannot be placed on a hull.
        Assert.Equal(0u, ComponentCommands.PlacePrefab(new DesignSession(), torch, default));
    }

    [Fact]
    public void Torch_IsUnderTheMassLimit_AndHasAGrip()
    {
        var torch = SampleTools.CuttingTorch(Alloy);
        var densities = Materials.Densities(torch);
        var (solids, volume) = SolidMass.Sample(torch.Solids.AsSpan(), densities);

        // Body less its hollow, grip, nozzle (they barely overlap): the solids' own volumes, added and taken away.
        var expected = 0.0;

        foreach (var s in torch.Solids.AsSpan())
            expected += (s.Op == SolidOp.Subtract ? -1 : 1) * SolidGeometry.Volume(s);
        Assert.Equal(expected, volume, expected * 0.05);

        var limits = ToolLimits.Load(DataFolder);
        var budget = ToolRules.Check(torch, solids, Catalog, limits);

        Assert.Equal(20, limits.MaxMassKg);
        Assert.Equal(volume * densities[0], budget.SolidsKg, 6);
        Assert.Equal(0.15 + 0.9 + 0.7, budget.CoresKg, 6);
        Assert.True(budget.Usable);
        Assert.True(ToolRules.Check(torch, solids, Catalog, new ToolLimits { MaxMassKg = 1 }).Over);

        var gripless = torch.Clone();
        gripless.Cores!.Cores.RemoveAll(c => c.Def == CoreIds.Grip);
        Assert.False(ToolRules.Check(gripless, 1, Catalog, limits).HasGrip);
    }

    [Fact]
    public void Torch_FiresWhileTheTriggerIsHeld_NotWhenSafe()
    {
        var (torch, run) = Rig();
        var firing = Signal(run, torch, CoreIds.CuttingTorch, 2);

        Steps(torch, run, 1, 10);
        Assert.Equal(0, run.Signals[firing]);

        // Pulled, but not held: nothing.
        run.Holder.Trigger = true;
        Steps(torch, run, 11, 20);
        Assert.Equal(0, run.Signals[firing]);

        run.Holder.Holding = true;
        Steps(torch, run, 21, 23);
        Assert.Equal(1, run.Signals[firing]);

        run.Holder.Trigger = false;
        Steps(torch, run, 24, 26);
        Assert.Equal(0, run.Signals[firing]);

        ProgramRunner.Call(torch.Program!, run, Safe);
        run.Holder.Trigger = true;
        Steps(torch, run, 27, 40);
        Assert.Equal(0, run.Signals[firing]);

        ProgramRunner.Call(torch.Program!, run, Ready);
        Steps(torch, run, 41, 45);
        Assert.Equal(1, run.Signals[firing]);
    }

    [Fact]
    public void Torch_Overheats_CutsOut_ThenFiresAgainOnceCooled()
    {
        var (torch, run) = Rig();
        var firing = Signal(run, torch, CoreIds.CuttingTorch, 2);
        var heat = firing + 1;
        var overheated = firing + 2;
        run.Holder = new HolderInput { Holding = true, Trigger = true };

        // 8 % a second at full power: full heat after 12.5 s.
        Steps(torch, run, 1, 60 * 12);
        Assert.Equal(1, run.Signals[firing]);
        Assert.InRange(run.Signals[heat], 94, 97);
        Steps(torch, run, 60 * 12 + 1, 60 * 13);
        Assert.Equal(0, run.Signals[firing]);
        Assert.Equal(1, run.Signals[overheated]);

        // Cooling at 15 % a second: back under half after about 3.3 s, still with the trigger held.
        Steps(torch, run, 60 * 13 + 1, 60 * 15);
        Assert.Equal(0, run.Signals[firing]);
        Steps(torch, run, 60 * 15 + 1, 60 * 17);
        Assert.Equal(1, run.Signals[firing]);
        Assert.Equal(0, run.Signals[overheated]);
    }

    [Fact]
    public void Battery_PaysForFiring_AndEmptyMeansNoPower()
    {
        var (torch, run) = Rig();
        var layout = run.Cores!;
        var battery = layout.Entries.FindIndex(e => e.Def.Kind == CoreKind.Battery);
        var full = run.CoreState[battery];
        var draw = layout.Entries[battery].SignalBase + 1;
        Assert.Equal(150L * 3_600_000, full);

        run.Holder = new HolderInput { Holding = true, Trigger = true };
        Steps(torch, run, 1, 60);

        // A second at 1200 W (and the grip's 0.05 W): 1.2 kJ.
        Assert.InRange(full - run.CoreState[battery], 1_195_000, 1_210_000);
        Assert.Equal(1200, run.Signals[draw]);

        // Nearly flat: it runs out, then the grip reads nothing and the head stops.
        run.CoreState[battery] = 10_000;
        Steps(torch, run, 61, 70);
        Assert.False(run.Charged);
        Assert.False(run.IsLive(Signal(run, torch, CoreIds.Grip, 1)));
        Assert.Equal(0, run.Signals[Signal(run, torch, CoreIds.CuttingTorch, 2)]);
    }

    private static (PlanDocument, ProgramInstance) Rig()
    {
        var torch = SampleTools.CuttingTorch(Alloy);
        var run = new ProgramInstance { Cores = CoreLayout.Build(torch.Cores, Catalog, []) };
        Assert.Equal(0, run.Cores.Missing);
        ProgramRunner.Reset(torch.Program!, [], run, 0);
        return (torch, run);
    }

    private static int Signal(ProgramInstance run, PlanDocument doc, uint def, int signal)
    {
        var core = doc.Cores!.Cores.First(c => c.Def == def);
        return run.Cores!.Entries[run.Cores.IndexOf(core.Id)].SignalBase + signal;
    }

    private static void Steps(PlanDocument doc, ProgramInstance run, long from, long to)
    {
        for (var tick = from; tick <= to; tick++)
            ProgramRunner.Step(doc.Program!, [], run, tick);
    }
}
