using System.Buffers.Binary;
using BrutalGame.Chemistry;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Tests;

/// <summary>Pipes and hallways as node networks: junctions, node edits, old formats read in, the profile library.</summary>
public class RouteNetworkTests
{
    private static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly ChemistryData Chemistry = ChemistryData.Load(DataFolder);
    private static readonly DefId Steel = DefId.FromName("mat.part.steel");
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");

    [Fact]
    public void PipeTee_IsOneGas_WithAnInletPerOpenEnd()
    {
        var session = new DesignSession(new PlanDocument { Level = PlanLevel.Component });
        var id = ComponentCommands.AddPipe(session, PipeEnd.Open, PipeEnd.Open, [new Vector3d(0, 0, 0), new Vector3d(0, 0, 1)], 40, 32, Steel);
        var middle = ComponentCommands.SplitPipeRun(session, id, 0);
        var branch = ComponentCommands.AddPipeNode(session, id, middle, new Vector3d(0.5, 0, 0.5));
        Assert.True(ComponentCommands.SetPipeRun(session, id, 2, 20, 16));

        var pipe = session.Doc.Components!.Pipes.Single();
        Assert.Equal(3, pipe.Degree(middle));
        Assert.Equal([0, 1, branch], pipe.OpenEnds().Order().ToArray());

        // The tee is rated by its thinnest-walled run: 2 x 215 x (2 mm) / 20 mm, the same as 4 mm / 40 mm.
        Assert.Equal(43e6, pipe.Rating(215), 0);

        var supplies = pipe.OpenEnds().Select(n => new Supply { Pipe = id, Node = n, Mixture = "mix.air", Pressure = 2e5 }).ToList();
        var model = ComponentModel.Compile(session.Doc.Components, Chemistry, _ => 215, supplies, 0);
        Assert.Single(model.PipeGas);
        Assert.DoesNotContain(model.Issues, i => i.Contains("supply"));

        // Removing the junction node joins nothing (three runs): the branch and the two halves come apart.
        Assert.True(ComponentCommands.RemovePipeNode(session, id, middle));
        Assert.Empty(session.Doc.Components!.Pipes.Single().Runs);
    }

    [Fact]
    public void HallwayJunction_CutsEveryRun_AndNodesComeAndGo()
    {
        var session = new DesignSession();
        var id = InteriorCommands.AddHallway(session, new Vector3d(0, 0, 0), new Vector3d(0, 0, 8), Alloy);
        var middle = InteriorCommands.SplitRun(session, id, 0);
        var arm = InteriorCommands.AddNode(session, id, middle, new Vector3d(4, 0, 4));
        var hallway = session.Doc.Interior!.Hallways.Single();
        Assert.Equal(3, hallway.Degree(middle));

        var expansion = HallwayGeometry.Expand(session.Doc.Interior, layered: false, meshBase: 0);
        Assert.Equal(3, expansion.Solids.Count);
        Assert.All(expansion.Meshes, m => Assert.Equal(MeshIssue.None, MeshValidator.Validate(m, out _)));

        // The arm's end goes; then the middle node, between two runs again, dissolves into one run.
        Assert.True(InteriorCommands.RemoveNode(session, id, arm));
        Assert.True(InteriorCommands.RemoveNode(session, id, middle));
        hallway = session.Doc.Interior!.Hallways.Single();
        Assert.Equal(2, hallway.Nodes.Count);
        Assert.Single(hallway.Runs);
    }

    [Fact]
    public void LegacyPipes_ReadAsTwoEndedNetworks()
    {
        // Format 5-7 pipe: id, from part/port, to part/port, outer, inner, material, three nodes.
        var b = new byte[2 + 2 + 20 + 3 * 12 + 2];
        var o = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), 1);
        o += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), 3);
        b[o + 8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 14), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 16), 32);
        b[o + 19] = 3;
        o += 20;

        for (var i = 0; i < 3; i++, o += 12)
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 8), i * 1024);

        var data = ComponentData.Read(b, out var consumed, legacyPipes: true);
        Assert.NotNull(data);
        Assert.Equal(b.Length, consumed);
        var pipe = data.Pipes.Single();
        Assert.Equal(2, pipe.Runs.Count);
        Assert.Equal(new PipeEnd(3, 1), pipe.EndAt(0));
        Assert.Equal([2], pipe.OpenEnds().ToArray());
    }

    [Fact]
    public void ProfileLibrary_SavesInFolders_AndPlansKeepTheirCopy()
    {
        var folder = Path.Combine(Path.GetTempPath(), "brutal-profiles-" + Guid.NewGuid().ToString("N"));

        try
        {
            var library = new ProfileLibrary(folder);
            var profile = InteriorCommands.Default();
            var relative = library.Save(profile, Path.Combine(folder, "Corridors", "wide.bprofile"));
            Assert.Equal("Corridors/wide.bprofile", relative);

            library.Refresh();
            var node = library.Root.Children.Single(c => c.Name == "Corridors").Children.Single();
            Assert.True(node.Profile!.SameShape(profile));

            // Imported into a plan, then the file changes: the plan keeps its copy until reimported.
            var session = new DesignSession();
            var index = InteriorCommands.AddProfile(session, node.Profile.Name, node.Profile.Points, node.Relative);
            var wider = new HullProfile { Name = "wide" };
            wider.Points.AddRange(HallwayGeometry.Preset(HallwayPreset.Rectangle, 2.0, 2.2));
            library.Save(wider, Path.Combine(folder, "Corridors", "wide.bprofile"));
            Assert.True(session.Doc.Interior!.Profiles[index].SameShape(profile));
            Assert.False(library.Current(relative)!.SameShape(session.Doc.Interior.Profiles[index]));

            Assert.True(InteriorCommands.SetProfilePoints(session, index, library.Current(relative)!.Points));
            Assert.True(session.Doc.Interior!.Profiles[index].SameShape(wider));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
