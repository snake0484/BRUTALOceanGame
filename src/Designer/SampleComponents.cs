using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using BrutalGame.Ships.Logic;

namespace BrutalGame.Designer;

/// <summary>Catalog components built through the same commands as the UI.</summary>
public static class SampleComponents
{
    /// <summary>
    /// A basic chemical rocket: a copper burn chamber (200 mm bore, 400 mm long), a nickel-superalloy bell nozzle on its
    /// aft port (48 mm throat, expansion 16, 30° → 8° bell), a fuel and an oxidiser injector on its head (one-way
    /// orifices of 24 and 40 mm: molar flow goes as area / sqrt(molar mass), so oxygen needs 2 sqrt 2 times the area for a stoichiometric mix at one supply pressure), an igniter on its side, and a
    /// steel pipe from each injector out to an open inlet. Fires along −Y. Whether it burns is the chemistry's business:
    /// feed the inlets and spark it on the test bench.
    /// </summary>
    public static PlanDocument ChemicalRocket(DefId copper, DefId superalloy, DefId steel)
    {
        var session = new DesignSession();
        session.Doc.Level = PlanLevel.Component;
        session.Doc.Meta.Name = "Chemical rocket, methane-oxygen, 9 kN";

        var chamber = ComponentCommands.AddPart(session, PartKind.Chamber, copper, new Vector3d(0, 0, 0));
        var nozzle = ComponentCommands.AddPart(session, PartKind.Nozzle, superalloy, new Vector3d(0, -0.21, 0));
        var fuel = ComponentCommands.AddPart(session, PartKind.Injector, steel, new Vector3d(-0.05, 0.25, 0));
        var oxidiser = ComponentCommands.AddPart(session, PartKind.Injector, steel, new Vector3d(0.05, 0.25, 0));
        var igniter = ComponentCommands.AddPart(session, PartKind.Igniter, steel, new Vector3d(0.15, 0, 0), SolidRotation.FromSteps(0, 0, -6));

        var data = ComponentCommands.Data(session);
        var ox = data.Parts[data.IndexOfPart(oxidiser)];
        ox.P0 = 40;
        ox.P3 = (int)InjectorFeed.Oxidiser;
        ComponentCommands.SetPart(session, ox);

        PipeFrom(session, fuel, -1, steel);
        PipeFrom(session, oxidiser, 1, steel);
        _ = (chamber, nozzle, igniter);
        session.Doc.FixNextId();
        return session.Doc;
    }

    /// <summary>
    /// A swinging door: a 2.4 x 2.4 x 0.2 m frame with a 1 x 2.1 m doorway (its sill at the origin, through along +Z) and a
    /// 5 cm leaf on a hinge at the doorway's left side, opening 90 degrees. It implements Door with the standard sequence
    /// (Locked looks like Closed: it has no bolt).
    /// </summary>
    public static PlanDocument SwingDoor(DefId alloy)
    {
        var session = Door("Swing door, 1 x 2.1 m", alloy, 1.2);
        var leaf = Leaf(session, alloy);
        var hinge = JointCommands.AddJoint(session, JointKind.Hinge, new Vector3d(-0.5, 0, 0), new Vector3d(0, 1, 0), 0);
        JointCommands.SetJoint(session, hinge, j => (j.Min, j.Max, j.Target, j.Speed) = (0, 900, 0, 450));
        JointCommands.SetMember(session, leaf, hinge);
        CoreCommands.Add(session, CoreIds.RotaryMotor, hinge, new Vector3d(-0.5, 2.2, 0), new Vector3d(0, 1, 0));
        CoreCommands.Add(session, CoreIds.LimitSwitch, hinge, new Vector3d(-0.45, 2.15, 0), new Vector3d(0, 0, 1), 0);
        CoreCommands.Add(session, CoreIds.LimitSwitch, hinge, new Vector3d(-0.45, 2.15, 0.1), new Vector3d(1, 0, 0), 900);
        DoorProgramme(session);
        session.Doc.FixNextId();
        return session.Doc;
    }

    /// <summary>
    /// A sliding door: a 3.2 m wide frame the 1 x 2.1 m leaf slides into, 1.05 m along +X, and a lock bolt in the frame
    /// at the leaf's opening edge that slides 80 mm across it. It implements Door with the standard sequence: Close slides
    /// it shut (stopping and opening again if something is in the way), then shoots the bolt; a locked door ignores Open.
    /// Its leaf is driven by a linear motor and the bolt by a solenoid; limit switches at the leaf's two ends tell the
    /// programme it got there.
    /// </summary>
    public static PlanDocument SlidingDoor(DefId alloy)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        var session = Door("Sliding door, 1 x 2.1 m", alloy, 1.6);
        var leaf = Leaf(session, alloy);
        var slider = JointCommands.AddJoint(session, JointKind.Slider, default, new Vector3d(1, 0, 0), 0);
        JointCommands.SetJoint(session, slider, j => (j.Min, j.Max, j.Target, j.Speed) = (0, 1050, 0, 800));
        JointCommands.SetMember(session, leaf, slider);

        var bolt = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, (int)(0.56 * m), 1 * m, 0, (ushort)(0.04 * h), (ushort)(0.1 * h), (ushort)(0.03 * h), SolidRotation.Identity, alloy);
        var boltSlider = JointCommands.AddJoint(session, JointKind.Slider, new Vector3d(0.56, 1, 0), new Vector3d(-1, 0, 0), 0);
        JointCommands.SetJoint(session, boltSlider, j => (j.Min, j.Max, j.Target, j.Speed) = (0, 80, 0, 200));
        JointCommands.SetMember(session, bolt, boltSlider);
        CoreCommands.Add(session, CoreIds.LinearMotor, slider, new Vector3d(0.6, 2.25, 0), new Vector3d(0, 1, 0));
        CoreCommands.Add(session, CoreIds.SolenoidBolt, boltSlider, new Vector3d(0.6, 1, 0), new Vector3d(-1, 0, 0));
        CoreCommands.Add(session, CoreIds.LimitSwitch, slider, new Vector3d(0.5, 2.15, 0), new Vector3d(-1, 0, 0), 0);
        CoreCommands.Add(session, CoreIds.LimitSwitch, slider, new Vector3d(1.55, 2.15, 0), new Vector3d(1, 0, 0), 1050);

        DoorProgramme(session);
        var states = InterfaceLibrary.Door.States;

        for (var s = 0; s < states.Count; s++)
            ProgramCommands.SetPose(session, s, boltSlider, states[s] == "Blocked" ? null : states[s] == "Locked" ? (short)80 : (short)0);

        session.Doc.FixNextId();
        return session.Doc;
    }

    /// <summary>Implements Door (default poses: open at the upper limits, otherwise the lower) with the standard door sequence.</summary>
    private static void DoorProgramme(DesignSession session)
    {
        ProgramCommands.Implement(session, InterfaceLibrary.Door);
        ProgramCommands.Set(session, p => ProgramCommands.StandardDoor(p, session.Doc.Joints?.Joints ?? [], session.Doc.Cores));
    }

    /// <summary>
    /// A proximity lamp: a 0.3 x 0.3 x 0.1 m housing on the surface at the origin facing +Z, a proximity sensor and a lamp
    /// on its face. It implements Lamp: on Auto (and at start) it lights while something is within the sensor's range.
    /// </summary>
    public static PlanDocument ProximityLamp(DefId alloy)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        var session = new DesignSession();
        session.Doc.Level = PlanLevel.Component;
        session.Doc.Meta.Name = "Proximity lamp";
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 0, (int)(0.05 * m), (ushort)(0.15 * h), (ushort)(0.15 * h), (ushort)(0.05 * h), SolidRotation.Identity, alloy);
        var sensor = CoreCommands.Add(session, CoreIds.Proximity, 0, new Vector3d(0, -0.08, 0.1), new Vector3d(0, 0, 1));
        var lamp = CoreCommands.Add(session, CoreIds.Lamp, 0, new Vector3d(0, 0.05, 0.1), new Vector3d(0, 0, 1));
        ProgramCommands.Implement(session, InterfaceLibrary.Lamp);
        ProgramCommands.Set(session, p => ProgramCommands.LampSequence(p, sensor, lamp));
        session.Doc.FixNextId();
        return session.Doc;
    }

    /// <summary>
    /// A docking port: a 2 m collar standing 0.6 m out along +Z from the origin (the hull's surface), a 1.4 m octagonal
    /// port through it whose face is the collar's front, cutting 1.5 m each way, and a hatch hinged at the top of the bore.
    /// Any two ports of the same profile mate.
    /// </summary>
    public static PlanDocument DockingPort(DefId alloy)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        var session = new DesignSession();
        session.Doc.Level = PlanLevel.Component;
        session.Doc.Meta.Name = "Docking port, 1.4 m";
        var pitch = SolidRotation.FromSteps(0, 6, 0);
        HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 0, (int)(0.3 * m), 1 * h, (ushort)(0.3 * h), 1 * h, pitch, alloy);
        var port = OpeningCommands.AddOpening(session, OpeningKind.DockingPort, new Vector3d(0, 0, 0.6), new Vector3d(0, 0, 1));
        OpeningCommands.SetOpening(session, port, o => o.DepthMm = 1500);
        var hatch = HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 0, (int)(0.3 * m), (ushort)(0.72 * h), (ushort)(0.03 * h), (ushort)(0.72 * h), pitch, alloy);
        var hinge = JointCommands.AddJoint(session, JointKind.Hinge, new Vector3d(0, 0.72, 0.3), new Vector3d(1, 0, 0), 0);
        JointCommands.SetJoint(session, hinge, j => (j.Min, j.Max, j.Target, j.Speed) = (-1000, 0, 0, 450));
        JointCommands.SetMember(session, hatch, hinge);
        session.Doc.FixNextId();
        return session.Doc;
    }

    /// <summary>A door's frame (2.4 m tall, <paramref name="halfWidth"/> to each side, 0.2 m deep) and its 1 x 2.1 m doorway.</summary>
    private static DesignSession Door(string name, DefId alloy, double halfWidth)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        var session = new DesignSession();
        session.Doc.Level = PlanLevel.Component;
        session.Doc.Meta.Name = name;
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, (int)(1.2 * m), 0, (ushort)(halfWidth * h), (ushort)(1.2 * h), (ushort)(0.1 * h), SolidRotation.Identity, alloy);
        OpeningCommands.AddOpening(session, OpeningKind.Door, new Vector3d(0, 1.05, 0), new Vector3d(0, 0, 1));
        return session;
    }

    private static uint Leaf(DesignSession session, DefId alloy)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        return HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, (int)(1.05 * m), 0, (ushort)(0.49 * h), (ushort)(1.04 * h), (ushort)(0.025 * h), SolidRotation.Identity, alloy);
    }

    /// <summary>A pipe up from an injector's inlet, then out sideways to an open end.</summary>
    private static void PipeFrom(DesignSession session, uint injector, int side, DefId steel)
    {
        var data = ComponentCommands.Data(session);
        ComponentCommands.Port(data, new PipeEnd(injector, 1), out var inlet);
        var up = HullSurface.Add(inlet, new Vector3d(0, 0.15, 0));
        var outward = HullSurface.Add(up, new Vector3d(side * 0.3, 0, 0));
        ComponentCommands.AddPipe(session, new PipeEnd(injector, 1), PipeEnd.Open, [inlet, up, outward], 40, 32, steel);
    }
}
