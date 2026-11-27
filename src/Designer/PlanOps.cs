using BrutalGame.DesignKit;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer;

public enum OpKind : byte { InsertSolid, RemoveSolid, ModifySolid, AddMaterial, RemoveMaterial, AddMesh, RemoveMesh, SetMesh, SetLayering, SetBrushes, SetComponents, AddPrefab, RemovePrefab, SetInterior, SetWindows, SetDecals, SetPaint, SetJoints, SetOpenings, SetProgram, SetCores }

/// <summary>
/// One invertible edit, a solid-list diff (ship-hulls "undo = solid list diffs"). Insert and Remove carry the solid and
/// its list index; Modify carries before and after; AddMaterial / RemoveMaterial append or drop the table's last entry
/// (<see cref="Value"/> = DefId). Mesh ops (round 5) carry immutable mesh snapshots: AddMesh / RemoveMesh append or drop
/// the mesh table's last entry, SetMesh replaces entry <see cref="Index"/> (one op per modelling step).
/// </summary>
public struct PlanOp : IUndoOp<PlanOp, PlanDocument>
{
    public OpKind Kind;
    public int Index;
    public uint Value;
    public HullSolid Before;
    public HullSolid After;
    public HullMesh? MeshBefore;
    public HullMesh? MeshAfter;
    public HullLayering? LayersBefore;
    public HullLayering? LayersAfter;
    public HullBrushSet? BrushesBefore;
    public HullBrushSet? BrushesAfter;
    public ComponentData? ComponentsBefore;
    public ComponentData? ComponentsAfter;
    public HullInterior? InteriorBefore;
    public HullInterior? InteriorAfter;
    public HullWindowSet? WindowsBefore;
    public HullWindowSet? WindowsAfter;
    public HullDecalSet? DecalsBefore;
    public HullDecalSet? DecalsAfter;
    public HullPaintSet? PaintBefore;
    public HullPaintSet? PaintAfter;
    public HullJointSet? JointsBefore;
    public HullJointSet? JointsAfter;
    public HullOpeningSet? OpeningsBefore;
    public HullOpeningSet? OpeningsAfter;
    public Ships.Logic.HullProgram? ProgramBefore;
    public Ships.Logic.HullProgram? ProgramAfter;
    public Ships.Logic.HullCoreSet? CoresBefore;
    public Ships.Logic.HullCoreSet? CoresAfter;
    public PlanDocument? Prefab;
    public ulong PrefabHash;

    /// <summary>Replaces the plan's layering (null: a solid hull). Snapshots are never mutated after this.</summary>
    public static PlanOp SetLayering(HullLayering? before, HullLayering? after) => new() { Kind = OpKind.SetLayering, LayersBefore = before, LayersAfter = after };

    public static PlanOp SetBrushes(HullBrushSet? before, HullBrushSet? after) => new() { Kind = OpKind.SetBrushes, BrushesBefore = before, BrushesAfter = after };

    public static PlanOp SetComponents(ComponentData? before, ComponentData? after) => new() { Kind = OpKind.SetComponents, ComponentsBefore = before, ComponentsAfter = after };

    /// <summary>Replaces the plan's interior (hallways). Snapshots are never mutated after this.</summary>
    public static PlanOp SetInterior(HullInterior? before, HullInterior? after) => new() { Kind = OpKind.SetInterior, InteriorBefore = before, InteriorAfter = after };

    /// <summary>Replaces the plan's windows. Snapshots are never mutated after this.</summary>
    public static PlanOp SetWindows(HullWindowSet? before, HullWindowSet? after) => new() { Kind = OpKind.SetWindows, WindowsBefore = before, WindowsAfter = after };

    public static PlanOp SetDecals(HullDecalSet? before, HullDecalSet? after) => new() { Kind = OpKind.SetDecals, DecalsBefore = before, DecalsAfter = after };

    public static PlanOp SetPaint(HullPaintSet? before, HullPaintSet? after) => new() { Kind = OpKind.SetPaint, PaintBefore = before, PaintAfter = after };

    public static PlanOp SetJoints(HullJointSet? before, HullJointSet? after) => new() { Kind = OpKind.SetJoints, JointsBefore = before, JointsAfter = after };

    public static PlanOp SetOpenings(HullOpeningSet? before, HullOpeningSet? after) => new() { Kind = OpKind.SetOpenings, OpeningsBefore = before, OpeningsAfter = after };

    public static PlanOp SetProgram(Ships.Logic.HullProgram? before, Ships.Logic.HullProgram? after) => new() { Kind = OpKind.SetProgram, ProgramBefore = before, ProgramAfter = after };

    public static PlanOp SetCores(Ships.Logic.HullCoreSet? before, Ships.Logic.HullCoreSet? after) => new() { Kind = OpKind.SetCores, CoresBefore = before, CoresAfter = after };

    public static PlanOp AddPrefab(ulong hash, PlanDocument prefab) => new() { Kind = OpKind.AddPrefab, PrefabHash = hash, Prefab = prefab };

    public static PlanOp AddMesh(int index, HullMesh mesh) => new() { Kind = OpKind.AddMesh, Index = index, MeshAfter = mesh };

    public static PlanOp SetMesh(int index, HullMesh before, HullMesh after) => new() { Kind = OpKind.SetMesh, Index = index, MeshBefore = before, MeshAfter = after };

    public static PlanOp Insert(int index, in HullSolid solid) => new() { Kind = OpKind.InsertSolid, Index = index, After = solid };

    public static PlanOp Remove(int index, in HullSolid solid) => new() { Kind = OpKind.RemoveSolid, Index = index, Before = solid };

    public static PlanOp Modify(int index, in HullSolid before, in HullSolid after) => new() { Kind = OpKind.ModifySolid, Index = index, Before = before, After = after };

    public static PlanOp AddMaterial(int index, DefId material) => new() { Kind = OpKind.AddMaterial, Index = index, Value = material.Value };

    public readonly PlanOp Inverse() => Kind switch
    {
        OpKind.InsertSolid => Remove(Index, After),
        OpKind.RemoveSolid => Insert(Index, Before),
        OpKind.ModifySolid => Modify(Index, After, Before),
        OpKind.AddMaterial => this with { Kind = OpKind.RemoveMaterial },
        OpKind.RemoveMaterial => this with { Kind = OpKind.AddMaterial },
        OpKind.AddMesh => new PlanOp { Kind = OpKind.RemoveMesh, Index = Index, MeshBefore = MeshAfter },
        OpKind.RemoveMesh => new PlanOp { Kind = OpKind.AddMesh, Index = Index, MeshAfter = MeshBefore },
        OpKind.SetLayering => SetLayering(LayersAfter, LayersBefore),
        OpKind.SetBrushes => SetBrushes(BrushesAfter, BrushesBefore),
        OpKind.SetComponents => SetComponents(ComponentsAfter, ComponentsBefore),
        OpKind.SetInterior => SetInterior(InteriorAfter, InteriorBefore),
        OpKind.SetWindows => SetWindows(WindowsAfter, WindowsBefore),
        OpKind.SetDecals => SetDecals(DecalsAfter, DecalsBefore),
        OpKind.SetPaint => SetPaint(PaintAfter, PaintBefore),
        OpKind.SetJoints => SetJoints(JointsAfter, JointsBefore),
        OpKind.SetOpenings => SetOpenings(OpeningsAfter, OpeningsBefore),
        OpKind.SetProgram => SetProgram(ProgramAfter, ProgramBefore),
        OpKind.SetCores => SetCores(CoresAfter, CoresBefore),
        OpKind.AddPrefab => new PlanOp { Kind = OpKind.RemovePrefab, PrefabHash = PrefabHash, Prefab = Prefab },
        OpKind.RemovePrefab => AddPrefab(PrefabHash, Prefab!),
        _ => SetMesh(Index, MeshAfter!, MeshBefore!),
    };

    /// <summary>Applies to <paramref name="doc"/>; refuses an op whose recorded state does not match (a corrupt log).</summary>
    public readonly void ApplyTo(PlanDocument doc)
    {
        switch (Kind)
        {
            case OpKind.InsertSolid:
                doc.Solids.Insert(Index, After);

                if (After.Id >= doc.Meta.NextId)
                    doc.Meta.NextId = After.Id + 1;

                break;
            case OpKind.RemoveSolid:
                Expect(doc.Solids[Index] == Before);
                doc.Solids.RemoveAt(Index);
                break;
            case OpKind.ModifySolid:
                Expect(doc.Solids[Index] == Before);
                doc.Solids[Index] = After;
                break;
            case OpKind.AddMaterial:
                Expect(Index == doc.Materials.Count);
                doc.Materials.Add(new DefId(Value));
                break;
            case OpKind.RemoveMaterial:
                Expect(Index == doc.Materials.Count - 1 && doc.Materials[Index].Value == Value);
                doc.Materials.RemoveAt(Index);
                break;
            case OpKind.AddMesh:
                Expect(Index == doc.Meshes.Count);
                doc.Meshes.Add(MeshAfter!);
                break;
            case OpKind.RemoveMesh:
                Expect(Index == doc.Meshes.Count - 1 && ReferenceEquals(doc.Meshes[Index], MeshBefore));
                doc.Meshes.RemoveAt(Index);
                break;
            case OpKind.SetMesh:
                Expect(ReferenceEquals(doc.Meshes[Index], MeshBefore));
                doc.Meshes[Index] = MeshAfter!;
                break;
            case OpKind.SetLayering:
                Expect(ReferenceEquals(doc.Layers, LayersBefore));
                doc.Layers = LayersAfter;
                break;
            case OpKind.SetBrushes:
                Expect(ReferenceEquals(doc.Brushes, BrushesBefore));
                doc.Brushes = BrushesAfter;
                break;
            case OpKind.SetComponents:
                Expect(ReferenceEquals(doc.Components, ComponentsBefore));
                doc.Components = ComponentsAfter;
                break;
            case OpKind.SetPaint:
                Expect(ReferenceEquals(doc.Paint, PaintBefore));
                doc.Paint = PaintAfter;
                break;
            case OpKind.SetCores:
                Expect(ReferenceEquals(doc.Cores, CoresBefore));
                doc.Cores = CoresAfter;
                break;
            case OpKind.SetProgram:
                Expect(ReferenceEquals(doc.Program, ProgramBefore));
                doc.Program = ProgramAfter;
                break;
            case OpKind.SetOpenings:
                Expect(ReferenceEquals(doc.Openings, OpeningsBefore));
                doc.Openings = OpeningsAfter;

                if (OpeningsAfter is not null)
                {
                    foreach (var o in OpeningsAfter.Openings)
                        doc.Meta.NextId = Math.Max(doc.Meta.NextId, o.Id + 1);
                }

                break;
            case OpKind.SetJoints:
                Expect(ReferenceEquals(doc.Joints, JointsBefore));
                doc.Joints = JointsAfter;

                if (JointsAfter is not null)
                {
                    foreach (var j in JointsAfter.Joints)
                        doc.Meta.NextId = Math.Max(doc.Meta.NextId, j.Id + 1);
                }

                break;
            case OpKind.SetDecals:
                Expect(ReferenceEquals(doc.Decals, DecalsBefore));
                doc.Decals = DecalsAfter;

                if (DecalsAfter is not null)
                {
                    foreach (var d in DecalsAfter.Decals)
                        doc.Meta.NextId = Math.Max(doc.Meta.NextId, d.Id + 1);
                }

                break;
            case OpKind.SetWindows:
                Expect(ReferenceEquals(doc.Windows, WindowsBefore));
                doc.Windows = WindowsAfter;

                if (WindowsAfter is not null)
                {
                    foreach (var w in WindowsAfter.Windows)
                        doc.Meta.NextId = Math.Max(doc.Meta.NextId, w.Id + 1);
                }

                break;
            case OpKind.SetInterior:
                Expect(ReferenceEquals(doc.Interior, InteriorBefore));
                doc.Interior = InteriorAfter;

                if (InteriorAfter is not null)
                {
                    foreach (var h in InteriorAfter.Hallways)
                        doc.Meta.NextId = Math.Max(doc.Meta.NextId, h.Id + 1);
                }

                break;
            case OpKind.AddPrefab:
                Expect(!doc.Prefabs.ContainsKey(PrefabHash));
                doc.Prefabs[PrefabHash] = Prefab!;
                break;
            case OpKind.RemovePrefab:
                Expect(doc.Prefabs.TryGetValue(PrefabHash, out var held) && ReferenceEquals(held, Prefab));
                doc.Prefabs.Remove(PrefabHash);
                break;
        }
    }

    private static void Expect(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Plan op does not match the document");
    }
}

/// <summary>
/// Op log with group boundaries: one user action (add, hollow, move up) is one group, undone and redone whole. Undo
/// applies a group's inverses in reverse; redo re-applies it. A new group after an undo drops the redo tail.
/// </summary>
public sealed class UndoStack : UndoStack<PlanOp, PlanDocument>
{
}
