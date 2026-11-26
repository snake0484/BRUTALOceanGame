using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

/// <summary>Layered hulls: skin shell by depth, sections, rooms with walls, panels. Faces sit half a cell off the sample lattice so occupancy is exact.</summary>
public class LayerTests
{
    private const int Ti = 0;
    private const int Alloy = 1;
    private static readonly int[] Densities = [1100, 800];

    private static HullBrush Box(uint id, double x, double y, double z, double hx, double hy, double hz, BrushRole role = BrushRole.Skin, byte material = Alloy, uint rotation = 0) => new()
    {
        Id = id,
        Shape = BrushShape.Box,
        Op = BrushOp.Add,
        Flags = BrushRoles.With(BrushFlags.None, role),
        Material = material,
        PosX = (int)Math.Round(x * 1024),
        PosY = (int)Math.Round(y * 1024),
        PosZ = (int)Math.Round(z * 1024),
        Rotation = rotation,
        HalfX = (ushort)Math.Round(hx * 256),
        HalfY = (ushort)Math.Round(hy * 256),
        HalfZ = (ushort)Math.Round(hz * 256),
    };

    /// <summary>Stack 0: 250 mm Ti + 500 mm alloy. Stack 1: 250 mm alloy (rooms). Stack 2: 1.25 m alloy.</summary>
    private static HullLayering Layers()
    {
        var layers = new HullLayering { DefaultStack = 0, RoomStack = 1, ComponentStack = 1 };
        var hull = new LayerStack();
        hull.Layers.Add(new HullLayer(Ti, 250));
        hull.Layers.Add(new HullLayer(Alloy, 500));
        var wall = new LayerStack();
        wall.Layers.Add(new HullLayer(Alloy, 250));
        var heavy = new LayerStack();
        heavy.Layers.Add(new HullLayer(Alloy, 1250));
        layers.Stacks.Add(hull);
        layers.Stacks.Add(wall);
        layers.Stacks.Add(heavy);
        return layers;
    }

    private static HullBaker Bake(HullBrush[] brushes, HullLayering layers, bool exact = true)
    {
        var baker = new HullBaker(4) { ExactSurfaces = exact };
        baker.Start(brushes, [], Densities, layers);
        baker.Wait();
        return baker;
    }

    private static double Box3(double x, double y, double z) => x * y * z;

    private static (double Ti, double Alloy) MaterialVolumes(HullBaker baker)
    {
        double ti = 0, alloy = 0;

        foreach (var brick in baker.Bricks)
        {
            foreach (var m in brick.MaterialSpan)
            {
                var v = m.Occupancy * HullMass.OccupancyUnitM3;

                if (m.Material == Ti)
                    ti += v;
                else
                    alloy += v;
            }
        }

        return (ti, alloy);
    }

    [Fact]
    public void SkinShell_IsTheStackDeep_AndHollowInside()
    {
        using var baker = Bake([Box(1, 0, 0, 0, 5.125, 3.125, 4.125)], Layers());
        var outer = Box3(10.25, 6.25, 8.25);
        var tiInner = Box3(9.75, 5.75, 7.75);
        var inner = Box3(8.75, 4.75, 6.75);
        var (ti, alloy) = MaterialVolumes(baker);

        Assert.Equal(outer - inner, baker.Mass.VolumeM3, 6);
        Assert.Equal(outer - tiInner, ti, 6);
        Assert.Equal(tiInner - inner, alloy, 6);
        Assert.Equal((outer - tiInner) * 1100 + (tiInner - inner) * 800, baker.Mass.MassKg, 3);
    }

    [Fact]
    public void Sections_UseTheirOwnStack()
    {
        var layers = Layers();
        layers.Cuts.Add(new SectionCut(0, 0));
        layers.Assignments.Add(new SectionStack(2048, 0, 0, 2));
        using var baker = Bake([Box(1, 0, 0, 0, 5.125, 3.125, 4.125)], layers);

        // -X: 0.75 m shell; +X: 1.25 m shell. Samples on the cut plane belong to the +X section, so its side starts half a
        // cell early: thin covers x in [-5.125, -0.125], thick [-0.125, 5.125].
        var thin = Box3(5.0, 6.25, 8.25) - Box3(4.25, 4.75, 6.75);
        var thick = Box3(5.25, 6.25, 8.25) - Box3(4.0, 3.75, 5.75);
        Assert.Equal(thin + thick, baker.Mass.VolumeM3, 6);
    }

    [Fact]
    public void Rooms_AreCutOutAndWalled_PanelsAdd()
    {
        var skin = Box(1, 0, 0, 0, 7.125, 4.125, 6.125);
        var room = Box(2, 0, 0, 0, 2.125, 1.625, 2.125, BrushRole.Room);
        var panel = Box(3, 0, 0, 8.125, 2.125, 2.125, 0.5, BrushRole.Panel, Ti);
        using var withRoom = Bake([skin, room], Layers());
        using var bare = Bake([skin], Layers());
        using var withPanel = Bake([skin, panel], Layers());

        // Wall: 0.25 m around the room (edges rounded by the distance field, so a little under the box shell).
        var wall = Box3(4.75, 3.75, 4.75) - Box3(4.25, 3.25, 4.25);
        Assert.InRange(withRoom.Mass.VolumeM3 - bare.Mass.VolumeM3, wall * 0.93, wall * 1.001);
        Assert.Equal(Box3(4.25, 4.25, 1.0), withPanel.Mass.VolumeM3 - bare.Mass.VolumeM3, 3);
    }

    [Fact]
    public void RoomCrossingTheShell_CutsThroughIt()
    {
        var skin = Box(1, 0, 0, 0, 5.125, 3.125, 4.125);
        var hangar = Box(2, 0, -2, 0, 2.125, 1.625, 2.125, BrushRole.Room);
        using var bare = Bake([skin], Layers());
        using var cut = Bake([skin, hangar], Layers());

        Assert.True(cut.Mass.VolumeM3 < bare.Mass.VolumeM3 + 30);
        Assert.True(cut.TriangleCount > bare.TriangleCount);
    }

    /// <summary>Exact skin with a layered inside: the outer surface is the brush's own faces, the inner one is still meshed.</summary>
    [Fact]
    public void ExactSkin_KeepsTheInnerSurface()
    {
        var skin = Box(1, 0.3, 0.1, 0.2, 5.2, 3.1, 4.3, rotation: BrushRotation.FromSteps(2, 1, 0));
        using var exact = Bake([skin], Layers());
        using var grid = Bake([skin], Layers(), exact: false);

        Assert.True(exact.ExactBricks > 0);
        Assert.Equal(grid.Mass, exact.Mass);
        Assert.InRange(exact.TriangleCount, grid.TriangleCount / 4, grid.TriangleCount);
    }

    [Fact]
    public void LayeredPlan_RoundTripsAndValidates()
    {
        var doc = new PlanDocument();
        doc.Materials.Add(DefId.FromName("mat.hull.titanium"));
        doc.Materials.Add(DefId.FromName("mat.hull.alloy"));
        doc.Brushes.Add(Box(1, 0, 0, 0, 5, 3, 4));
        doc.Brushes.Add(Box(2, 0, 0, 0, 2, 1.5, 2, BrushRole.Room));
        doc.Layers = Layers();
        doc.Layers.Cuts.Add(new SectionCut(2, 2048));
        doc.Layers.Cuts.Add(new SectionCut(0, 0));
        doc.Layers.Assignments.Add(new SectionStack(1000, 0, 9000, 2));
        doc.FixNextId();

        var log = new ValidationLog();
        PlanValidator.Validate(doc, log);
        Assert.Equal(0, log.Errors);

        PlanCanonical.Canonicalise(doc);
        Assert.Equal(0, doc.Layers!.Cuts[0].Axis);
        var bytes = PlanFormat.Encode(doc);
        Assert.Equal(3, bytes[4]);
        Assert.True(PlanFormat.TryDecode(bytes, out var loaded, out var error), error.ToString());
        Assert.Equal(bytes, PlanFormat.Encode(loaded));

        loaded.Layers!.Stacks[1].Layers[0] = new HullLayer(Alloy, 100);
        PlanValidator.Validate(loaded, log);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.LayerThickness);

        var plain = new PlanDocument();
        plain.Materials.Add(DefId.FromName("mat.hull.alloy"));
        plain.Brushes.Add(Box(1, 0, 0, 0, 2, 2, 2, BrushRole.Room, 0));
        PlanValidator.Validate(plain, log);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.RoleWithoutLayers);
    }
}
