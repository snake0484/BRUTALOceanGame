using BrutalGame.Ships;
using BrutalGame.Ships.Logic;

namespace BrutalGame.Designer;

/// <summary>The catalog cores the samples and the standard door sequence name (keys of their ids in <c>data/sensors</c> and <c>data/effectors</c>).</summary>
public static class CoreIds
{
    public static readonly uint LimitSwitch = CoreCatalog.Hash("sensor.limit-switch");
    public static readonly uint Encoder = CoreCatalog.Hash("sensor.encoder-basic");
    public static readonly uint Proximity = CoreCatalog.Hash("sensor.proximity-basic");
    public static readonly uint LinearMotor = CoreCatalog.Hash("effector.linear-motor");
    public static readonly uint RotaryMotor = CoreCatalog.Hash("effector.rotary-motor");
    public static readonly uint SolenoidBolt = CoreCatalog.Hash("effector.solenoid-bolt");
    public static readonly uint Lamp = CoreCatalog.Hash("effector.lamp");
    public static readonly uint Grip = CoreCatalog.Hash("sensor.grip");
    public static readonly uint SmallBattery = CoreCatalog.Hash("power.battery-small");
    public static readonly uint CuttingTorch = CoreCatalog.Hash("effector.cutting-torch");
}

/// <summary>Core edits in a component plan, each one undo group that swaps the core set for an edited copy.</summary>
public static class CoreCommands
{
    /// <summary>A core of catalog key <paramref name="def"/> reading or driving <paramref name="joint"/> (0: none), at <paramref name="at"/> facing <paramref name="normal"/>; its id, or 0 when the set is full.</summary>
    public static uint Add(DesignSession session, uint def, uint joint, Vector3d at, Vector3d normal, short trip = 0)
    {
        var id = 0u;
        Change(session, set =>
        {
            if (set.Cores.Count >= HullCoreSet.MaxCores)
                return;

            id = set.NextId();
            var (x, y, z) = ComponentCommands.Quantise(at);
            var core = new HullCore { Id = id, Def = def, Joint = joint, At = trip, X = x, Y = y, Z = z };
            core.SetNormal(normal);
            set.Cores.Add(core);
        });
        return id;
    }

    public static void Set(DesignSession session, uint id, Action<HullCore> edit) =>
        Change(session, set =>
        {
            if (set.IndexOf(id) is var i and >= 0)
                edit(set.Cores[i]);
        });

    public static void Remove(DesignSession session, uint id) => Change(session, set => set.Cores.RemoveAll(c => c.Id == id));

    private static void Change(DesignSession session, Action<HullCoreSet> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Cores;
            var after = before?.Clone() ?? new HullCoreSet();
            mutate(after);
            s.Apply(PlanOp.SetCores(before, after.IsEmpty ? null : after));
        });
}
