using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

public class UndoTests
{
    internal static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");
    internal static readonly DefId Titanium = DefId.FromName("mat.hull.titanium");

    private static byte[] Body(DesignSession s) => PlanFormat.EncodeBody(s.Doc);

    [Fact]
    public void Commands_UndoAndRedoToExactBytes()
    {
        var session = new DesignSession();
        var states = new List<byte[]> { Body(session) };

        var box = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 0, 0, 1024, 512, 768, 0, Alloy);
        states.Add(Body(session));
        HullCommands.Hollow(session, box, 0.5);
        states.Add(Body(session));
        var cyl = HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 0, 4096, 256, 512, 256, SolidRotation.FromSteps(0, 6, 0), Titanium);
        states.Add(Body(session));
        HullCommands.SetMaterial(session, box, Titanium);
        states.Add(Body(session));
        HullCommands.Move(session, cyl, -2);
        states.Add(Body(session));
        HullCommands.ToggleOp(session, cyl);
        states.Add(Body(session));
        HullCommands.Rotate(session, box, 0, 3);
        states.Add(Body(session));
        session.Selection.Set(box);
        HullCommands.DeleteSelected(session);
        states.Add(Body(session));

        Assert.Equal(states.Count - 1, session.Undo.UndoCount);

        for (var i = states.Count - 2; i >= 0; i--)
        {
            Assert.True(session.UndoLast());
            Assert.Equal(states[i], Body(session));
        }

        Assert.False(session.UndoLast());

        for (var i = 1; i < states.Count; i++)
        {
            Assert.True(session.RedoNext());
            Assert.Equal(states[i], Body(session));
        }

        Assert.False(session.RedoNext());
    }

    [Fact]
    public void UndoneInsert_NeverReusesItsId()
    {
        var session = new DesignSession();
        var a = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 0, 0, 512, 512, 512, 0, Alloy);
        session.UndoLast();
        var b = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 0, 0, 512, 512, 512, 0, Alloy);

        Assert.NotEqual(a, b);
        Assert.Equal(0, session.Undo.RedoCount);
    }

    [Fact]
    public void EmptyEdit_KeepsTheRedoTail()
    {
        var session = new DesignSession();
        var id = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 0, 0, 512, 512, 512, 0, Alloy);
        HullCommands.Duplicate(session, id);
        session.UndoLast();
        session.TryGetSolid(id, out var same);

        Assert.False(HullCommands.SetSolid(session, id, same));
        Assert.Equal(1, session.Undo.RedoCount);
    }

    /// <summary>Every op inverts exactly: 2000 random commands, then undo to the empty plan's bytes and redo to the end.</summary>
    [Fact]
    public void RandomCommands_UndoToStartAndRedoToEnd()
    {
        var session = new DesignSession();
        var rng = new Random(7);
        var start = Body(session);
        var ids = new List<uint>();

        for (var i = 0; i < 2000; i++)
        {
            var pick = ids.Count > 0 ? ids[rng.Next(ids.Count)] : 0;

            switch (ids.Count == 0 ? 0 : rng.Next(8))
            {
                case 0:
                    ids.Add(HullCommands.AddSolid(session, (SolidShape)rng.Next(3), rng.Next(4) == 0 ? SolidOp.Subtract : SolidOp.Add,
                        rng.Next(-40, 40) * 256, rng.Next(-10, 10) * 256, rng.Next(-40, 40) * 256,
                        (ushort)rng.Next(64, 2048), (ushort)rng.Next(64, 2048), (ushort)rng.Next(64, 2048), SolidRotation.FromSteps(rng.Next(24), 0, 0), rng.Next(2) == 0 ? Alloy : Titanium));
                    break;
                case 1:
                    session.TryGetSolid(pick, out var moved);
                    moved.PosX += rng.Next(-8, 8) * 256;
                    HullCommands.SetSolid(session, pick, moved);
                    break;
                case 2:
                    HullCommands.Rotate(session, pick, rng.Next(3), rng.Next(-3, 4));
                    break;
                case 3:
                    HullCommands.Move(session, pick, rng.Next(2) == 0 ? -1 : 1);
                    break;
                case 4:
                    ids.Add(HullCommands.Duplicate(session, pick));
                    break;
                case 5:
                    var inner = HullCommands.Hollow(session, pick, 0.5);

                    if (inner != 0)
                        ids.Add(inner);

                    break;
                case 6:
                    HullCommands.SetMaterial(session, pick, rng.Next(2) == 0 ? Alloy : Titanium);
                    break;
                default:
                    session.Selection.Set(pick);
                    HullCommands.DeleteSelected(session);
                    ids.Remove(pick);
                    break;
            }
        }

        var end = Body(session);

        while (session.UndoLast())
        {
        }

        Assert.Equal(start, Body(session));

        while (session.RedoNext())
        {
        }

        Assert.Equal(end, Body(session));
    }

    [Fact]
    public void Snap_PutsFacesOnTheGrid()
    {
        var snap = new Snap { Step = GridStep.Half };
        var solid = new HullSolid { PosX = 1000, PosY = -300, PosZ = 77, HalfX = 300, HalfY = 129, HalfZ = 1 };
        solid = snap.SnapSolid(solid);

        foreach (var (pos, half) in new[] { (solid.PosX, solid.HalfX), (solid.PosY, solid.HalfY), (solid.PosZ, solid.HalfZ) })
        {
            Assert.Equal(0, half % 64);
            Assert.True(half >= 64);
            Assert.Equal(0, (pos - half * 4) % 512);
            Assert.Equal(0, (pos + half * 4) % 512);
        }

        foreach (var step in Snap.Steps)
        {
            snap.Step = step;
            Assert.Equal((int)step, snap.HalfStep * 8);
        }
    }

    [Fact]
    public void Rotation_StepsWrapAt360()
    {
        var r = SolidRotation.AddSteps(SolidRotation.Identity, 0, -1);
        Assert.Equal((23, 0, 0), SolidRotation.ToSteps(r));
        Assert.Equal(SolidRotation.Identity, SolidRotation.AddSteps(r, 0, 1));
        Assert.Equal(15, SolidRotation.DegreesPerStep);
    }
}
