using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

/// <summary>Paint slots: the hull's base paint, strokes' own paints, paint shapes, the library on disk.</summary>
public class PaintSlotTests
{
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");
    private static readonly uint Satin = DefId.FromName("finish.powder.satin").Value;
    private static readonly uint Gloss = DefId.FromName("finish.gloss").Value;

    /// <summary>A plan with a box, a rib stroke along its top and two slots: the hull slate, the rib orange.</summary>
    private static (DesignSession Session, uint Stroke) Painted()
    {
        var session = new DesignSession();
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 0, 0, 4 * 256, 3 * 256, 4 * 256, SolidRotation.Identity, Alloy);
        var rib = BrushCommands.AddSweep(session, ProfilePreset.Rib, 0.4, 0.3);
        var stroke = BrushCommands.AddStroke(session, rib, [StrokePoint.From(new Vector3d(-3, 3, 0), new Vector3d(0, 1, 0)), StrokePoint.From(new Vector3d(3, 3, 0), new Vector3d(0, 1, 0))], 0);
        PaintCommands.AddSlot(session, new HullPaint { Name = "Slate", Finish = Satin, Colour = 0xFF5A4A3C });
        PaintCommands.AddSlot(session, new HullPaint { Name = "Orange", Finish = Gloss, Colour = 0xFF1E6EF0 });
        Assert.True(PaintCommands.SetHull(session, 0));
        Assert.True(PaintCommands.SetStroke(session, stroke, 1));
        return (session, stroke);
    }

    [Fact]
    public void Slots_RoundTripAndValidate()
    {
        var (session, stroke) = Painted();
        DecalCommands.AddDecal(session, new Vector3d(0, 3, 2), new Vector3d(0, 1, 0), default, new HullDecal { Shape = DecalShape.Ellipse, Paint = 1 });

        Assert.Equal(PlanFormat.PaintFormat, PlanFormat.FormatOf(session.Doc));
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(session.Doc), out var back, out var error), error.ToString());
        Assert.Equal(PlanFormat.HashBody(session.Doc), PlanFormat.HashBody(back));
        Assert.Equal(0, back.Paint!.Hull);
        Assert.Equal(1, back.Paint.SlotOf(stroke));
        Assert.Equal(("Orange", Gloss, 0xFF1E6EF0u), (back.Paint.Slots[1].Name, back.Paint.Slots[1].Finish, back.Paint.Slots[1].Colour));
        Assert.Equal((DecalShape.Ellipse, (byte)1), (back.Decals!.Decals[0].Shape, back.Decals.Decals[0].Paint));

        var log = new ValidationLog();
        PlanValidator.Validate(back, log);
        Assert.DoesNotContain(log.Issues, i => i.Code is IssueCode.PaintRef or IssueCode.PaintSlots or IssueCode.DecalShape);
    }

    [Fact]
    public void ChangingASlot_RepaintsEveryUse_AndAUsedSlotStays()
    {
        var (session, stroke) = Painted();

        // One slot, many uses: change it once.
        Assert.True(PaintCommands.SetSlot(session, 1, new HullPaint { Name = "Red", Finish = Satin, Colour = 0xFF0000FF, Source = "reds/red.bpaint" }));
        Assert.Equal(1, session.Doc.Paint!.SlotOf(stroke));
        Assert.Equal(("Red", "reds/red.bpaint"), (session.Doc.Paint.Slots[1].Name, session.Doc.Paint.Slots[1].Source));

        Assert.False(PaintCommands.RemoveSlot(session, 1));
        Assert.False(PaintCommands.RemoveSlot(session, 0));

        // An unused slot before the used ones goes; the uses follow their slots down.
        PaintCommands.AddSlot(session, new HullPaint { Name = "Spare", Finish = Satin });
        Assert.True(PaintCommands.SetStroke(session, stroke, 2));
        Assert.True(PaintCommands.RemoveSlot(session, 1));
        Assert.Equal(1, session.Doc.Paint!.SlotOf(stroke));
        Assert.Equal("Spare", session.Doc.Paint.Slots[1].Name);

        session.UndoLast();
        Assert.Equal(3, session.Doc.Paint!.Slots.Count);
    }

    [Fact]
    public void StrokeWithItsOwnPaint_GetsFillBoxes_OverItsFlair()
    {
        var (session, stroke) = Painted();
        var doc = session.Doc;
        var expanded = StrokeExpansion.Expand(doc.Solids.AsSpan(), doc.Meshes, doc.Brushes, doc.Interior?.Profiles, id => doc.Paint!.SlotOf(id));

        var fill = Assert.Single(expanded.Projectors, p => p.Kind == ProjectorKind.Fill);
        Assert.Equal(1, fill.Paint);

        // The rib is 0.4 m wide and 0.3 m tall: the fill reaches a little past both.
        Assert.InRange(fill.HalfV, 0.2, 0.25);
        Assert.InRange(fill.Centre.Y + fill.HalfN * fill.N.Y, 3.3, 3.4);
        Assert.Empty(StrokeExpansion.Expand(doc.Solids.AsSpan(), doc.Meshes, doc.Brushes, doc.Interior?.Profiles).Projectors);
    }

    [Fact]
    public void PaintLibrary_SavesAndLoads_ByFinishKey()
    {
        var folder = Path.Combine(Path.GetTempPath(), "brutal-paints-" + Guid.NewGuid().ToString("N"));

        try
        {
            var library = new PaintLibrary(folder);
            var relative = library.Save(new HullPaint { Name = "Signal", Finish = Gloss, Colour = 0x801E6EF0 }, "finish.gloss", Path.Combine(folder, "signs", "signal.bpaint"));
            Assert.Equal("signs/signal.bpaint", relative);

            library.Refresh();
            var node = Assert.Single(Assert.Single(library.Root.Children).Children);
            Assert.Equal(("Signal", Gloss, 0x801E6EF0u, "signs/signal.bpaint"), (node.Paint!.Name, node.Paint.Finish, node.Paint.Colour, node.Paint.Source));
            Assert.True(library.Current(relative)!.SameLook(node.Paint));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void FinishCatalog_ReadsGameData()
    {
        var catalog = FinishCatalog.Parse("""{ "finishes": [ { "id": "finish.a", "name": "A", "folder": "a", "tileMetres": 2.5, "metalness": 0 }, { "id": "finish.b", "folder": "b" } ] }""");
        Assert.Equal(2, catalog.All.Count);
        Assert.Equal((2.5f, 0f), (catalog.All[0].TileMetres, catalog.All[0].Metalness));
        Assert.Equal((1f, 1f), (catalog.All[1].TileMetres, catalog.All[1].Metalness));
        Assert.Equal(1, catalog.IndexOf(DefId.FromName("finish.b")));
    }

    [Fact]
    public void GameData_HasEveryFinishsMaps()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");

        if (!Directory.Exists(Path.Combine(data, FinishCatalog.Folder)))
            return;

        var catalog = FinishCatalog.Load(data);
        Assert.NotEmpty(catalog.All);

        for (var i = 0; i < catalog.All.Count; i++)
        {
            foreach (var map in new[] { "color", "normal", "roughness" })
                Assert.True(File.Exists(catalog.MapPath(i, map)), $"{catalog.All[i].Key}: no {map}");
        }
    }
}
