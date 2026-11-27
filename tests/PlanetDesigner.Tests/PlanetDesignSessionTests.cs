using Brutal.Numerics;
using BrutalGame.Terrain;

namespace BrutalGame.PlanetDesigner.Tests;

public sealed class PlanetDesignSessionTests : IDisposable
{
    private static readonly PlanetParams Body = PlanetParams.Generate(PlanetParams.BodySeed(1, 1), 1.0e6, kind: "temperate-continental");
    private static readonly double3 Spot = double3.Normalize(new double3(0.3, 0.8, -0.5));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "brutal-design-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    private PlanetDesignSession Open() => PlanetDesignSession.Open(_folder, Body, "test-design");

    [Fact]
    public void StrokeUndoRedoRestoresTheMap()
    {
        var session = Open();
        var empty = session.Shape.Write();
        session.BeginStroke();
        Assert.True(session.Dab(Spot, 8000.0, 300f, AuthoredBrush.Raise, 1f) > 0);
        session.Dab(double3.Normalize(Spot + new double3(0.004, 0, 0)), 8000.0, 300f, AuthoredBrush.Raise, 1f);
        session.EndStroke();
        var painted = session.Shape.Write();
        Assert.NotEqual(empty, painted);
        Assert.NotNull(session.TakePending());

        session.Undo();
        Assert.Equal(empty, session.Shape.Write());
        Assert.False(session.Snapshot().ChangesTerrain);
        session.Redo();
        Assert.Equal(painted, session.Shape.Write());
    }

    [Fact]
    public void SitesAreUndoableAndStampedWithTheNextVersion()
    {
        var session = Open();
        var id = session.AddSite(Spot);
        var site = session.Layer.Sites[0];
        Assert.Equal(1, site.Since);
        Assert.True(site.HeightM >= session.SiteFloor);

        var moved = double3.Normalize(Spot + new double3(0, 0.001, 0));
        session.SetSite(id, moved, 300f, 150f, site.HeightM + 10f);
        Assert.Equal(site.HeightM + 10f, session.Layer.Sites[0].HeightM);
        session.Undo();
        Assert.Equal(site, session.Layer.Sites[0]);
        session.Undo();
        Assert.Empty(session.Layer.Sites);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void SaveBumpsTheVersionAndReloads()
    {
        var session = Open();
        var id = session.AddSite(Spot);
        session.AddMarker("city", "Newport", id);
        session.BeginStroke();
        session.Dab(Spot, 5000.0, -120f, AuthoredBrush.Raise, 1f);
        session.EndStroke();
        session.Save();
        Assert.Equal(1, session.Layer.Version);
        Assert.False(session.Modified);

        session.Save();
        Assert.Equal(1, session.Layer.Version);

        var again = Open();
        Assert.Equal(session.Layer.Hash(), again.Layer.Hash());
        Assert.Equal(2, again.NextVersion);

        // A site moved after the save carries the new version, so older saves lose player edits in its core (D85).
        again.SetSite(id, double3.Normalize(Spot + new double3(0.002, 0, 0)), 250f, 200f, again.Layer.Sites[0].HeightM);
        Assert.Equal(2, again.Layer.Sites[0].Since);
    }

    [Fact]
    public void SavedLayerShapesTheBody()
    {
        var session = Open();
        session.AddSite(Spot);
        session.Save();
        AuthoredWorld.Register(session.Snapshot());
        var surface = new PlanetSurface(Body.WithBody("test-design"));
        Assert.Equal(session.Layer.Sites[0].HeightM, surface.Height(Spot.X, Spot.Y, Spot.Z));
        Assert.NotEqual(Body.ProgramHash, surface.Params.ProgramHash);
    }

    [Fact]
    public void OpenRefusesALayerMadeOnAnotherBody()
    {
        var session = Open();
        session.Save();
        var other = PlanetParams.Generate(PlanetParams.BodySeed(1, 2), 1.0e6, kind: "temperate-continental");
        Assert.Throws<InvalidDataException>(() => PlanetDesignSession.Open(_folder, other, "test-design"));
    }
}
