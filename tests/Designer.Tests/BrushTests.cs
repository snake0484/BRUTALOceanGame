using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

/// <summary>Hull brushes: strokes stick to the hull, sweeps and stamps expand to solids, cuts remove, plans round-trip.</summary>
public class BrushTests
{
    private static readonly int[] Densities = [1000, 1000];

    private static HullSolid Box(uint id, double x, double y, double z, double hx, double hy, double hz, SolidOp op = SolidOp.Add) => new()
    {
        Id = id,
        Shape = SolidShape.Box,
        Op = op,
        PosX = (int)Math.Round(x * 1024),
        PosY = (int)Math.Round(y * 1024),
        PosZ = (int)Math.Round(z * 1024),
        HalfX = (ushort)Math.Round(hx * 256),
        HalfY = (ushort)Math.Round(hy * 256),
        HalfZ = (ushort)Math.Round(hz * 256),
    };

    /// <summary>A stroke along X on top of a box whose top is at y = 3, drawn 0.4 m above it (sticking pulls it down).</summary>
    private static HullBrushSet RibAlongTop(ProfilePreset preset = ProfilePreset.Rib, SolidOp op = SolidOp.Add)
    {
        var set = new HullBrushSet();
        var brush = new HullBrush { Kind = BrushKind.Sweep };
        brush.Profile.AddRange(BrushCommands.Profile(preset, 0.5, 0.5));
        set.Brushes.Add(brush);
        var stroke = new HullStroke { Id = 10, Brush = 0, Op = op };
        stroke.Points.Add(StrokePoint.From(new Vector3d(-3, 3.4, 0), new Vector3d(0, 1, 0)));
        stroke.Points.Add(StrokePoint.From(new Vector3d(3, 3.4, 0), new Vector3d(0, 1, 0)));
        set.Strokes.Add(stroke);
        return set;
    }

    private static double Volume(HullSolid[] solids, IReadOnlyList<HullMesh> meshes, HullBrushSet? set)
    {
        var expanded = StrokeExpansion.Expand(solids, meshes, set, layered: false);
        using var baker = new HullBaker(4) { ExactSurfaces = false };
        baker.Start(expanded.Solids, expanded.Meshes, Densities);
        baker.Wait();
        return baker.Mass.VolumeM3;
    }

    [Fact]
    public void Surface_RaycastHonoursSubtract()
    {
        HullSolid[] solids = [Box(1, 0, 0, 0, 4, 3, 4), Box(2, 0, 3, 0, 1, 1, 1, SolidOp.Subtract)];
        var surface = new HullSurface(solids, []);

        // Straight down into the hole: the floor of the hole at y = 2.
        Assert.True(surface.Raycast(new Vector3d(0, 10, 0), new Vector3d(0, -1, 0), 20, out var t, out var n));
        Assert.Equal(8.0, t, 3);
        Assert.Equal(1.0, n.Y, 3);

        // Beside the hole: the box top at y = 3.
        Assert.True(surface.Raycast(new Vector3d(2.5, 10, 0), new Vector3d(0, -1, 0), 20, out t, out _));
        Assert.Equal(7.0, t, 3);

        // Sideways into the hole wall: the hole's side at x = -1 faces +X.
        Assert.True(surface.Raycast(new Vector3d(0, 2.5, 0), new Vector3d(-1, 0, 0), 20, out t, out n));
        Assert.Equal(1.0, t, 3);
        Assert.Equal(1.0, n.X, 3);
    }

    [Fact]
    public void Sweep_AddsTheProfileAlongTheStroke()
    {
        HullSolid[] solids = [Box(1, 0, 0, 0, 4, 3, 4)];
        var bare = Volume(solids, [], null);
        var withRib = Volume(solids, [], RibAlongTop());

        // 0.5 m wide, 0.5 m proud of the surface, 6 m long.
        Assert.InRange(withRib - bare, 1.5 * 0.9, 1.5 * 1.1);
    }

    [Fact]
    public void Sweep_CutsAChannel()
    {
        HullSolid[] solids = [Box(1, 0, 0, 0, 4, 3, 4)];
        var bare = Volume(solids, [], null);
        var cut = Volume(solids, [], RibAlongTop(ProfilePreset.Channel, SolidOp.Subtract));

        Assert.InRange(bare - cut, 1.5 * 0.9, 1.5 * 1.1);
    }

    [Fact]
    public void Strokes_StickToTheHullWhenItMoves()
    {
        var set = RibAlongTop();
        var unstuck = 0;
        var low = StrokeExpansion.Path(new HullSurface([Box(1, 0, 0, 0, 4, 3, 4)], []), set.Strokes[0], ref unstuck);
        var high = StrokeExpansion.Path(new HullSurface([Box(1, 0, 1, 0, 4, 3, 4)], []), set.Strokes[0], ref unstuck);

        Assert.Equal(0, unstuck);
        Assert.All(low.Points, p => Assert.Equal(3.0, p.Y, 3));
        Assert.All(high.Points, p => Assert.Equal(4.0, p.Y, 3));
        Assert.Equal(2, low.Controls.Count);
    }

    [Fact]
    public void Stamps_RepeatAtTheSpacing()
    {
        var cube = MeshPrimitives.FromSolid(Box(0, 0, 0, 0, 0.25, 0.25, 0.25));
        var set = new HullBrushSet();
        set.Brushes.Add(new HullBrush { Kind = BrushKind.Stamp, Mesh = 0, Spacing = 1024 });
        var stroke = new HullStroke { Id = 10, Brush = 0, Op = SolidOp.Add };
        stroke.Points.Add(StrokePoint.From(new Vector3d(-3, 3, 0), new Vector3d(0, 1, 0)));
        stroke.Points.Add(StrokePoint.From(new Vector3d(3, 3, 0), new Vector3d(0, 1, 0)));
        set.Strokes.Add(stroke);

        var expanded = StrokeExpansion.Expand([Box(1, 0, 0, 0, 4, 3, 4)], [cube], set, layered: false);

        Assert.Equal(7, expanded.Generated);
        Assert.Equal(8, expanded.Solids.Length);
        Assert.Equal(8, expanded.Meshes.Count);
        Assert.All(expanded.Solids[1..], s => Assert.Equal(3.0, s.PositionY, 3));
    }

    [Fact]
    public void BrushCommands_UndoRedoAndRoundTrip()
    {
        var session = new DesignSession();
        var start = PlanFormat.EncodeBody(session.Doc);
        var box = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 0, 0, 4096, 3072, 4096, 0, UndoTests.Alloy);
        var stamp = BrushCommands.AddStampFromSolid(session, box);
        var sweep = BrushCommands.AddSweep(session, ProfilePreset.HalfRound, 0.5, 0.25);
        Assert.Equal(0, stamp);
        Assert.Equal(1, sweep);
        var id = BrushCommands.AddStroke(session, sweep,
            [StrokePoint.From(new Vector3d(-3, 3, 0), new Vector3d(0, 1, 0)), StrokePoint.From(new Vector3d(3, 3, 0), new Vector3d(0, 1, 0))],
            SolidOp.Subtract, 0);
        Assert.NotEqual(0u, id);
        Assert.True(BrushCommands.SetStroke(session, id, s => s.Offset = 100));
        Assert.False(BrushCommands.RemoveBrush(session, sweep));

        var log = new ValidationLog();
        PlanValidator.Validate(session.Doc, log);
        Assert.True(log.Errors == 0, string.Join(", ", log.Issues.Select(i => $"{i.Code} {i.Value}")));

        var copy = session.Doc.Clone();
        PlanCanonical.Canonicalise(copy);
        var bytes = PlanFormat.Encode(copy);
        Assert.Equal(PlanFormat.BrushFormat, bytes[4]);
        Assert.True(PlanFormat.TryDecode(bytes, out var loaded, out var error), error.ToString());
        Assert.Equal(bytes, PlanFormat.Encode(loaded));
        Assert.Single(loaded.Meshes);
        Assert.Equal(100, loaded.Brushes!.Strokes[0].Offset);

        var end = PlanFormat.EncodeBody(session.Doc);

        while (session.UndoLast())
        {
        }

        Assert.Equal(start, PlanFormat.EncodeBody(session.Doc));
        Assert.Null(session.Doc.Brushes);

        while (session.RedoNext())
        {
        }

        Assert.Equal(end, PlanFormat.EncodeBody(session.Doc));
    }
}
