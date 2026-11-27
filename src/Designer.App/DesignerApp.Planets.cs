using System.Globalization;
using System.Numerics;
using Brutal.Numerics;
using Brutal.VulkanApi;
using BrutalGame.Designer.App.Planets;
using BrutalGame.Designer.Ui;
using BrutalGame.Terrain;

namespace BrutalGame.Designer.App;

/// <summary>
/// Planet tabs (D89): a body of the fixed system with its authored layer open (<see cref="PlanetWorkspace"/>), on the
/// same tab bar, File menu and Ctrl+Z/Y/S as ships. The tab keeps a ship session that stays empty so the shell's ship
/// paths have one; while a planet tab is in front, the hooks here run instead of them. Layers save to the repository's
/// <c>data/planets/&lt;body&gt;/</c> (the game embeds them when it is next built).
/// </summary>
public sealed partial class DesignerApp
{
    private static readonly float4 ShipClear = new(0.11f, 0.12f, 0.14f, 1f);
    private static readonly float4 SpaceClear = new(0f, 0f, 0f, 1f);

    private string? _planetsFolder;
    private List<string>? _planetBodies;

    /// <summary>--planet body (with --planet-look, --planet-distance, --planet-pitch): a planet tab opens on the first frame.</summary>
    public static PlanetStart? StartPlanet;

    /// <summary>--planet-embedded: show the layers built into the game, not the ones on disk.</summary>
    public static bool PlanetEmbedded;

    /// <summary>--planet-auto-edit: once the octree settles, one stroke and a site with a marker, saved (a hands-off check).</summary>
    public static bool PlanetAutoEdit;

    /// <summary>Reads the planet arguments; the rest are the shell's.</summary>
    public static void ParsePlanetArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--planet" && i + 1 < args.Length)
                (StartPlanet ??= new PlanetStart()).Body = args[++i];
            else if (args[i] == "--planet-look" && i + 3 < args.Length)
                (StartPlanet ??= new PlanetStart()).Look = new double3(Num(args[++i]), Num(args[++i]), Num(args[++i]));
            else if (args[i] == "--planet-distance" && i + 1 < args.Length)
                (StartPlanet ??= new PlanetStart()).Distance = Num(args[++i]);
            else if (args[i] == "--planet-pitch" && i + 1 < args.Length)
                (StartPlanet ??= new PlanetStart()).Pitch = Num(args[++i]) * Math.PI / 180.0;
            else if (args[i] == "--planet-embedded")
                PlanetEmbedded = true;
            else if (args[i] == "--planet-auto-edit")
                PlanetAutoEdit = true;
        }
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>The repository's <c>data/planets</c> (found above the app), else the copy beside it; disk layers registered once.</summary>
    private string PlanetsFolder()
    {
        if (_planetsFolder is not null)
            return _planetsFolder;

        _planetsFolder = Path.Combine(AppContext.BaseDirectory, "data", "planets");

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "planets", "kinds")) && File.Exists(Path.Combine(dir.FullName, "BRUTALGame.slnx")))
            {
                _planetsFolder = Path.Combine(dir.FullName, "data", "planets");
                break;
            }
        }

        if (!PlanetEmbedded)
            PlanetWorkspace.RegisterDiskLayers(_planetsFolder);

        return _planetsFolder;
    }

    /// <summary>A planet tab for a body of the fixed system, or the one already open for it.</summary>
    private void OpenPlanet(PlanetStart start)
    {
        if (_docs.Find(d => d.Planet?.Body == start.Body) is { } already)
        {
            Activate(already);
            return;
        }

        var folder = PlanetsFolder();
        var document = NewDocument(component: false, name: $"Planet: {start.Body}");

        try
        {
            document.Planet = new PlanetWorkspace(Renderer, document.Camera, folder, start);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException)
        {
            CloseDocument(document.Key);
            _stats.Message = $"Planet {start.Body}: {e.Message}";
            Console.WriteLine($"open planet failed: {e.Message}");
            return;
        }

        if (PlanetAutoEdit)
            document.Planet.OnSettled = AutoEdit;

        _stats.Message = $"Opened planet {start.Body}";
    }

    /// <summary>Per frame first: the start planet, the clear colour, and the planet tab's tick in place of the ship's.</summary>
    private bool TickPlanet()
    {
        if (StartPlanet is { } start)
        {
            StartPlanet = null;
            OpenPlanet(start);
        }

        _bar.Planets = _planetBodies ??= PlanetWorkspace.Bodies();
        Renderer.ClearColor = _active.Planet is null ? ShipClear : SpaceClear;
        _active.Planet?.Tick(Frames);
        return _active.Planet is not null;
    }

    protected override void RecordBeforeScene(CommandBuffer commands, int frameIndex, in Matrix4x4 viewProj)
        => _active.Planet?.RecordBeforeScene(commands, frameIndex, viewProj);

    /// <summary>
    /// A closed planet tab: earlier frames may still use its GPU buffers, so the device goes idle first. Tabs close in
    /// AfterPanels, before this frame records anything.
    /// </summary>
    private void ClosePlanet(Document closing)
    {
        if (closing.Planet is not { } planet)
            return;

        closing.Planet = null;
        Renderer.WaitIdle();
        planet.Dispose();
    }

    private void DisposePlanets()
    {
        foreach (var d in _docs)
            d.Planet?.Dispose();
    }

    private void SavePlanet(PlanetWorkspace planet)
    {
        planet.Session.Save();
        _stats.Message = $"Saved planet {planet.Body} (version {planet.Session.Layer.Version})";
        Console.WriteLine($"saved planet {planet.Body}: version {planet.Session.Layer.Version}, hash {planet.Session.Layer.Hash():X16}");
    }

    /// <summary>--planet-auto-edit: a 1500 m dab of 10 km radius at the target and a city site 5 km off on its slope, then save.</summary>
    private static void AutoEdit(PlanetWorkspace planet)
    {
        var d = planet.Target;
        var session = planet.Session;
        var axis = Math.Abs(d.Y) < 0.9 ? double3.UnitY : double3.UnitX;
        var slope = double3.Normalize(d + double3.Normalize(double3.Cross(axis, d)) * (5000.0 / session.Generated.Params.Radius));
        session.BeginStroke();
        session.Dab(d, 10_000.0, 1500f, AuthoredBrush.Raise, 1f);
        session.EndStroke();
        var site = session.AddSite(slope, 600f, 400f);
        session.AddMarker("city", "Newport", site);
        session.Save();
    }
}
