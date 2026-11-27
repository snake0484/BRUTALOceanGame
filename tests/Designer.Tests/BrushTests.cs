using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Tests;

/// <summary>Hull brushes: strokes stick to the hull, sweeps and stamps expand to flair meshes, plans round-trip.</summary>
public class BrushTests
{
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

    /// <summary>One brush profile from a preset, as the plan's profile list.</summary>
    private static List<HullProfile> Outline(ProfilePreset preset = ProfilePreset.Rib, double width = 0.5, double depth = 0.5)
    {
        var profile = new HullProfile { Name = "rib", Kind = ProfileKind.Brush };
        profile.Points.AddRange(BrushCommands.Profile(preset, width, depth));
        return [profile];
    }

    /// <summary>A stroke along X on top of a box whose top is at y = 3, drawn 0.4 m above it (sticking pulls it down), sweeping profile 0.</summary>
    private static HullBrushSet RibAlongTop(ProfilePoint lineAt = default, ushort startSize = HullBrush.UnitScale, ushort endSize = HullBrush.UnitScale)
    {
        var set = new HullBrushSet();
        set.Brushes.Add(new HullBrush { Kind = BrushKind.Sweep, Outline = 0, Anchor = lineAt });
        var stroke = new HullStroke { Id = 10, Brush = 0 };
        stroke.Points.Add(StrokePoint.From(new Vector3d(-3, 3.4, 0), new Vector3d(0, 1, 0)) with { Scale = startSize });
        stroke.Points.Add(StrokePoint.From(new Vector3d(3, 3.4, 0), new Vector3d(0, 1, 0)) with { Scale = endSize });
        set.Strokes.Add(stroke);
        return set;
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
    public void Sweep_IsFlairWithItsOwnVolume()
    {
        HullSolid[] solids = [Box(1, 0, 0, 0, 4, 3, 4)];
        var expanded = StrokeExpansion.Expand(solids, [], RibAlongTop(), Outline());
        var surface = HullCsg.Build(solids, [], false, expanded);
        var plain = HullCsg.Build(solids, [], false);

        // 0.5 m wide, from 0.125 m under the surface to 0.5 m above it, 6 m long.
        Assert.Equal(0.5 * 0.625 * 6.0, surface.FlairVolume[0], 3);
        Assert.True(surface.Triangles > plain.Triangles);
        Assert.Equal(plain.Fragments, surface.Fragments);
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
        var stroke = new HullStroke { Id = 10, Brush = 0 };
        stroke.Points.Add(StrokePoint.From(new Vector3d(-3, 3, 0), new Vector3d(0, 1, 0)));
        stroke.Points.Add(StrokePoint.From(new Vector3d(3, 3, 0), new Vector3d(0, 1, 0)));
        set.Strokes.Add(stroke);

        var expanded = StrokeExpansion.Expand([Box(1, 0, 0, 0, 4, 3, 4)], [cube], set, null);

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
            0);
        Assert.NotEqual(0u, id);
        Assert.True(BrushCommands.SetStroke(session, id, s => s.Offset = 100));
        Assert.False(BrushCommands.RemoveBrush(session, sweep));

        var log = new ValidationLog();
        PlanValidator.Validate(session.Doc, log);
        Assert.True(log.Errors == 0, string.Join(", ", log.Issues.Select(i => $"{i.Code} {i.Value}")));

        var copy = session.Doc.Clone();
        PlanCanonical.Canonicalise(copy);
        var bytes = PlanFormat.Encode(copy);
        Assert.Equal(PlanFormat.BrushProfileFormat, bytes[4]);
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

    [Fact]
    public void Sweep_RunsThroughTheLinePoint()
    {
        HullSolid[] solids = [Box(1, 0, 0, 0, 4, 3, 4)];

        // The line at the rib's top (y = 0.5): the rib now hangs from the line, 0.625 m into the hull, none above it.
        var expanded = StrokeExpansion.Expand(solids, [], RibAlongTop(new ProfilePoint(0, 512)), Outline());
        var ys = expanded.Solids[1..].SelectMany(s => expanded.Meshes[s.Param].Vertices.Select(v => (s.PosY + v.Y) / 1024.0)).ToArray();
        Assert.Equal(3.0, ys.Max(), 2);
        Assert.Equal(3.0 - 0.625, ys.Min(), 2);
    }

    [Fact]
    public void Sweep_BlendsPointSizesAlongTheLine()
    {
        HullSolid[] solids = [Box(1, 0, 0, 0, 4, 3, 4)];
        var expanded = StrokeExpansion.Expand(solids, [], RibAlongTop(default, 1000, 3000), Outline());
        var path = expanded.Paths[0];
        Assert.Equal(1.0, path.Scales[0], 3);
        Assert.Equal(3.0, path.Scales[^1], 3);
        Assert.Equal(2.0, path.Scales[path.Scales.Count / 2], 1);

        // The rib's top: 0.5 m above the surface at the start, 1.5 m at the end.
        var vertices = expanded.Solids[1..].SelectMany(s => expanded.Meshes[s.Param].Vertices.Select(v => (X: (s.PosX + v.X) / 1024.0, Y: (s.PosY + v.Y) / 1024.0))).ToArray();
        Assert.Equal(3.5, vertices.Where(v => v.X < -2.99).Max(v => v.Y), 2);
        Assert.Equal(4.5, vertices.Where(v => v.X > 2.99).Max(v => v.Y), 2);
    }

    [Fact]
    public void OldInlineOutlines_BecomePlanBrushProfiles()
    {
        // The format 4-9 brush block: a sweep with an inline triangle, one stroke of two points.
        var block = new List<byte> { 1, (byte)BrushKind.Sweep, 0, 0, 0, 4, 0, 0, 3 };

        foreach (var (x, y) in new[] { (-100, 0), (100, 0), (0, 200) })
        {
            block.AddRange(BitConverter.GetBytes(x));
            block.AddRange(BitConverter.GetBytes(y));
        }

        block.AddRange(BitConverter.GetBytes((ushort)1));
        block.AddRange(BitConverter.GetBytes(10u));
        block.AddRange(BitConverter.GetBytes((ushort)0));
        block.Add(0);
        block.AddRange(BitConverter.GetBytes(0));
        block.AddRange(BitConverter.GetBytes((ushort)1000));
        block.Add(2);

        for (var i = 0; i < 2; i++)
        {
            block.AddRange(BitConverter.GetBytes(i * 1024));
            block.AddRange(BitConverter.GetBytes(0));
            block.AddRange(BitConverter.GetBytes(0));
            block.AddRange(BitConverter.GetBytes((short)0));
            block.AddRange(BitConverter.GetBytes((short)32767));
            block.AddRange(BitConverter.GetBytes((short)0));
        }

        var inline = new List<(int Brush, List<ProfilePoint> Points)>();
        var set = HullBrushSet.Read(block.ToArray(), out var consumed, BrushLayout.Inline, inline)!;
        Assert.Equal(block.Count, consumed);
        Assert.Single(inline);
        Assert.Equal(3, inline[0].Points.Count);
        Assert.Equal(HullBrush.UnitScale, set.Strokes[0].Points[1].Scale);
    }

    [Fact]
    public void BrushProfile_InUse_StaysAndSizesRoundTrip()
    {
        var session = new DesignSession();
        var sweep = BrushCommands.AddSweep(session, ProfilePreset.Ridge, 0.4, 0.2);
        Assert.True(sweep >= 0, BrushCommands.LastError);
        var profile = session.Doc.Brushes!.Brushes[sweep].Outline;
        Assert.Equal(ProfileKind.Brush, session.Doc.Interior!.Profiles[profile].Kind);
        Assert.False(InteriorCommands.RemoveProfile(session, profile));

        var id = BrushCommands.AddStroke(session, sweep,
            [StrokePoint.From(new Vector3d(-3, 3, 0), new Vector3d(0, 1, 0)), StrokePoint.From(new Vector3d(3, 3, 0), new Vector3d(0, 1, 0))], 0);
        Assert.True(BrushCommands.SetPointScale(session, id, 1, 2.5));
        Assert.True(BrushCommands.SetAnchor(session, sweep, new ProfilePoint(0, 100)));
        Assert.True(BrushCommands.SetScale(session, sweep, 0.5));

        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(session.Doc), out var back, out var error), error.ToString());
        Assert.Equal(2500, back.Brushes!.Strokes[0].Points[1].Scale);
        Assert.Equal(new ProfilePoint(0, 100), back.Brushes.Brushes[sweep].Anchor);
        Assert.Equal(500, back.Brushes.Brushes[sweep].Scale);

        // A profile ahead of it goes: the brush follows its profile down one, and undo puts both back.
        var hall = InteriorCommands.AddProfile(session, "hall", HallwayGeometry.Preset(HallwayPreset.Rectangle, 1, 2));
        InteriorCommands.SetProfile(session, hall, _ => { });
        var before = session.Doc.Interior!.Profiles.Count;
        Assert.True(InteriorCommands.RemoveProfile(session, hall));
        Assert.Equal(before - 1, session.Doc.Interior!.Profiles.Count);
        Assert.Equal(ProfileKind.Brush, session.Doc.Interior.Profiles[session.Doc.Brushes!.Brushes[sweep].Outline].Kind);
    }

    [Fact]
    public void RemovingAnEarlierProfile_MovesTheBrushDown()
    {
        var session = new DesignSession();
        var hall = InteriorCommands.AddProfile(session, "hall", HallwayGeometry.Preset(HallwayPreset.Rectangle, 1, 2));
        var sweep = BrushCommands.AddSweep(session, ProfilePreset.Rib, 0.4, 0.2);
        Assert.Equal(1, session.Doc.Brushes!.Brushes[sweep].Outline);

        Assert.True(InteriorCommands.RemoveProfile(session, hall));
        Assert.Equal(0, session.Doc.Brushes!.Brushes[sweep].Outline);
        Assert.Equal(ProfileKind.Brush, session.Doc.Interior!.Profiles[0].Kind);

        session.UndoLast();
        Assert.Equal(1, session.Doc.Brushes!.Brushes[sweep].Outline);
        Assert.Equal(2, session.Doc.Interior!.Profiles.Count);
    }
}
