using System.Text.Json;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Logic;

namespace BrutalGame.Designer;

/// <summary>The limits on a tool (D91), from <c>data/tools/limits.json</c>. The mass is a placeholder until decided.</summary>
public sealed class ToolLimits
{
    public double MaxMassKg { get; set; } = 20;

    public static ToolLimits Load(string dataFolder)
    {
        var path = Path.Combine(dataFolder, "tools", "limits.json");

        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ToolLimits>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

/// <summary>
/// What makes a tool usable (D91): it is light enough to carry (its solids' mass, from <see cref="SolidMass"/>, plus its cores') and has a grip to
/// hold it by. The hull-only features it may not have are the validator's (<see cref="IssueCode.ToolFeature"/>).
/// </summary>
public static class ToolRules
{
    public readonly record struct Budget(double SolidsKg, double CoresKg, double MaxKg, bool HasGrip)
    {
        public double TotalKg => SolidsKg + CoresKg;

        public bool Over => TotalKg > MaxKg;

        public bool Usable => !Over && HasGrip;
    }

    public static Budget Check(PlanDocument doc, double solidsKg, CoreCatalog catalog, ToolLimits limits)
    {
        var cores = 0.0;
        var grip = false;

        foreach (var core in doc.Cores?.Cores ?? [])
        {
            if (!catalog.TryGet(core.Def, out var def))
                continue;

            cores += def.MassKg;
            grip |= def.Kind == CoreKind.Grip;
        }

        return new Budget(solidsKg, cores, limits.MaxMassKg, grip);
    }
}

/// <summary>Catalog tools built through the same commands as the UI.</summary>
public static class SampleTools
{
    /// <summary>
    /// A plasma cutting torch: a pistol grip under a 0.24 m alloy body, a 150 Wh battery in the body, the cutting head at
    /// the nozzle's tip firing along +Z. It implements Tool: Ready fires while the trigger is held, Safe never fires.
    /// </summary>
    public static PlanDocument CuttingTorch(DefId alloy)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        var session = new DesignSession();
        session.Doc.Level = PlanLevel.Tool;
        session.Doc.Meta.Name = "Plasma cutting torch";
        session.Snap.Enabled = false;
        var along = SolidRotation.FromSteps(0, 6, 0);
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, (int)(0.06 * m), (int)(0.05 * m), (ushort)(0.025 * h), (ushort)(0.035 * h), (ushort)(0.12 * h), SolidRotation.Identity, alloy);
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Subtract, 0, (int)(0.06 * m), (int)(0.03 * m), (ushort)(0.02 * h), (ushort)(0.03 * h), (ushort)(0.09 * h), SolidRotation.Identity, alloy);
        HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, (int)(-0.03 * m), 0, (ushort)(0.016 * h), (ushort)(0.06 * h), (ushort)(0.016 * h), SolidRotation.Identity, alloy);
        HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, (int)(0.06 * m), (int)(0.2 * m), (ushort)(0.01 * h), (ushort)(0.03 * h), (ushort)(0.01 * h), along, alloy);
        var grip = CoreCommands.Add(session, CoreIds.Grip, 0, new Vector3d(0, -0.03, 0), new Vector3d(0, 0, 1));
        CoreCommands.Add(session, CoreIds.SmallBattery, 0, new Vector3d(0, 0.06, -0.02), new Vector3d(0, 0, 1));
        var head = CoreCommands.Add(session, CoreIds.CuttingTorch, 0, new Vector3d(0, 0.06, 0.23), new Vector3d(0, 0, 1));
        ProgramCommands.Implement(session, InterfaceLibrary.Tool);
        ProgramCommands.Set(session, p => ProgramCommands.TriggerSequence(p, grip, head));
        session.Doc.FixNextId();
        return session.Doc;
    }
}
