using BrutalGame.Chemistry;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;

namespace BrutalGame.Designer.Tests;

/// <summary>Components: the sample rocket's parts snap together, compile to a flow network, fire on the bench; plans round-trip.</summary>
public class ComponentTests
{
    private static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly ChemistryData Chemistry = ChemistryData.Load(DataFolder);
    private static readonly HullMaterialCatalog Catalog = HullMaterialCatalog.Load(DataFolder);
    private static readonly DefId Copper = DefId.FromName("mat.part.copper");
    private static readonly DefId Superalloy = DefId.FromName("mat.part.inconel");
    private static readonly DefId Steel = DefId.FromName("mat.part.steel");

    private static PlanDocument Rocket() => SampleComponents.ChemicalRocket(Copper, Superalloy, Steel);

    private static double Yield(PlanDocument doc, byte material) => material < doc.Materials.Count && Catalog.TryGet(doc.Materials[material], out var m) ? m.YieldMpa : 0;

    private static ComponentModel Bench(PlanDocument doc, double ambient, double supplyPa = 30e5)
    {
        var data = doc.Components!;
        var supplies = new List<Supply>
        {
            new() { Pipe = data.Pipes[0].Id, Node = data.Pipes[0].OpenEnds().Single(), Mixture = "mix.methane", Pressure = supplyPa },
            new() { Pipe = data.Pipes[1].Id, Node = data.Pipes[1].OpenEnds().Single(), Mixture = "mix.oxygen", Pressure = supplyPa },
        };
        return ComponentModel.Compile(data, Chemistry, m => Yield(doc, m), supplies, ambient);
    }

    [Fact]
    public void Rocket_PartsSnapOntoTheirPorts()
    {
        var doc = Rocket();
        var links = ComponentModel.Links(doc.Components!);

        // Nozzle, two injectors and the igniter each meet the chamber.
        Assert.Equal(4, links.Count);
        Assert.All(doc.Components!.Pipes, p => Assert.False(p.EndAt(0).IsOpen));
        Assert.All(doc.Components.Pipes, p => Assert.Single(p.OpenEnds()));

        var log = new ValidationLog();
        PlanValidator.Validate(doc, log, Catalog);
        Assert.True(log.Errors == 0, string.Join(", ", log.Issues.Select(i => $"{i.Code} {i.Value}")));
    }

    [Fact]
    public void Snap_SkipsAPortAnotherPartAlreadyUses()
    {
        var data = Rocket().Components!;
        var injectors = data.Parts.Where(p => p.Kind == PartKind.Injector).ToList();
        var fuel = injectors[0];
        var gap = Math.Abs(injectors[1].Position.X - fuel.Position.X);

        // Nudged most of the way toward its neighbour, within snap range of the neighbour's chamber port: it must not
        // land on that port (it is taken), only back on its own.
        var moved = fuel;
        (moved.PosX, moved.PosY, moved.PosZ) = ComponentCommands.Quantise(new Vector3d(fuel.Position.X + Math.Sign(injectors[1].Position.X - fuel.Position.X) * gap * 0.8, fuel.Position.Y, fuel.Position.Z));
        ComponentCommands.Snap(data, ref moved);

        Assert.NotEqual(injectors[1].Position.X, moved.Position.X, 3);
    }

    [Fact]
    public void Rocket_CompilesWithoutIssues_AndPipesAreRated()
    {
        var doc = Rocket();
        var model = Bench(doc, 0);

        Assert.Empty(model.Issues);
        Assert.Single(model.Network.Nozzles);
        Assert.Single(model.Igniters);

        // Steel 215 MPa, 40 x 32 mm: 2 * 215e6 * 0.004 / 0.04 = 43 MPa.
        Assert.Equal(43e6, model.PipeRating[doc.Components!.Pipes[0].Id], 0);
    }

    [Fact]
    public void Rocket_FiresOnSpark_AndLosesThrustAtSeaLevel()
    {
        var vacuum = Fire(Rocket(), 0);
        var sea = Fire(Rocket(), 101325);

        Assert.True(vacuum.Lit);
        Assert.InRange(vacuum.Thrust, 1500, 15000);
        Assert.InRange(vacuum.Isp, 200, 400);
        Assert.True(sea.Thrust < vacuum.Thrust);
    }

    private static (bool Lit, double Thrust, double Isp) Fire(PlanDocument doc, double ambient)
    {
        var model = Bench(doc, ambient);

        for (var i = 0; i < 30; i++)
            model.Network.Step(1.0 / 60);

        var before = model.Network.Gases[model.ChamberGas.Values.First()];
        Assert.True(model.Spark(), $"p {before.Pressure:F0} T {before.Temperature:F0} " + string.Join(" ", Chemistry.Species.Select(sp => $"{sp.Key}={before.Moles[sp.Index]:G3}")) + " links " + string.Join(" ", model.Network.Links.Select(l => $"{l.From}->{l.To}:{l.MassFlow:G3}")));

        for (var i = 0; i < 120; i++)
            model.Network.Step(1.0 / 60);

        var chamber = model.Network.Gases[model.ChamberGas.Values.First()];
        return (chamber.Lit.Any(l => l), model.Network.Thrust, model.Network.Nozzles[0].Last.Isp);
    }

    [Fact]
    public void Rocket_WithoutAnIgniter_DoesNotLight()
    {
        var session = new DesignSession(Rocket());
        var igniter = session.Doc.Components!.Parts.First(p => p.Kind == PartKind.Igniter).Id;
        ComponentCommands.RemovePart(session, igniter);
        var model = Bench(session.Doc, 0);

        Assert.False(model.Spark());
        model.Network.Step(0.5);
        Assert.False(model.Network.Gases[model.ChamberGas.Values.First()].Lit.Any(l => l));
    }

    [Fact]
    public void PartSurfaces_AreClosed_AndGiveTheirMetalVolume()
    {
        var chamber = PartGeometry.Default(PartKind.Chamber);
        var polygons = new List<SurfacePolygon>();
        PartGeometry.Surface(chamber, polygons);

        // Tube wall plus head plate (minus its 1 mm pin hole): pi((r+w)^2 - r^2) L + pi((r+w)^2 - pin^2) w.
        double r = 0.1, w = 0.008, l = 0.4, pin = 0.001;
        var exact = Math.PI * ((r + w) * (r + w) - r * r) * l + Math.PI * ((r + w) * (r + w) - pin * pin) * w;

        // Revolved with 40 sides: a polygon of 40 sides holds cos(pi/40)·sin(pi/40)·40/pi of the circle's area.
        var polygonShare = 40 * Math.Sin(Math.Tau / 40) / (2 * Math.PI);
        Assert.Equal(exact * polygonShare, PartGeometry.Volume(polygons), 6);
    }

    [Fact]
    public void ComponentAndShipPlans_RoundTrip_WithAPlacedPrefab()
    {
        var rocket = Rocket();
        var bytes = PlanFormat.Encode(rocket);
        Assert.Equal(PlanFormat.RouteFormat, bytes[4]);
        Assert.Equal((byte)PlanLevel.Component, bytes[6]);
        Assert.True(PlanFormat.TryDecode(bytes, out var loaded, out var error), error.ToString());
        Assert.Equal(PlanFormat.EncodeBody(rocket), PlanFormat.EncodeBody(loaded));

        var ship = new DesignSession();
        var start = PlanFormat.EncodeBody(ship.Doc);
        var id = ComponentCommands.PlacePrefab(ship, loaded, new Vector3d(0, 2, -6), SolidRotation.FromSteps(0, 6, 0));
        Assert.NotEqual(0u, id);
        Assert.Single(ship.Doc.Prefabs);
        ComponentCommands.PlacePrefab(ship, rocket, new Vector3d(3, 2, -6));
        Assert.Single(ship.Doc.Prefabs);

        var copy = ship.Doc.Clone();
        PlanCanonical.Canonicalise(copy);
        var shipBytes = PlanFormat.Encode(copy);
        Assert.True(PlanFormat.TryDecode(shipBytes, out var shipLoaded, out error), error.ToString());
        Assert.Equal(shipBytes, PlanFormat.Encode(shipLoaded));
        Assert.Equal(copy.Components!.Placements[0].Prefab, shipLoaded.Prefabs.Keys.Single());

        var log = new ValidationLog();
        PlanValidator.Validate(shipLoaded, log, Catalog);
        Assert.Equal(0, log.Errors);

        while (ship.UndoLast())
        {
        }

        Assert.Equal(start, PlanFormat.EncodeBody(ship.Doc));
        Assert.Empty(ship.Doc.Prefabs);
    }
}
