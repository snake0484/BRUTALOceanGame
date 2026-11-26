using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;

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
