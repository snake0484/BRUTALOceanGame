using Brutal.Numerics;
using BrutalGame.Terrain;

namespace BrutalGame.PlanetDesigner;

/// <summary>Where a change landed: a unit direction and an angle (rad) about it; the whole body when the angle is π.</summary>
public readonly record struct DesignRegion(double3 Direction, double Angle)
{
    public static readonly DesignRegion Everywhere = new(double3.UnitY, Math.PI);

    public DesignRegion Union(in DesignRegion other)
    {
        var between = Math.Acos(Math.Clamp(double3.Dot(Direction, other.Direction), -1.0, 1.0));
        return new DesignRegion(Direction, Math.Min(Math.PI, Math.Max(Angle, between + other.Angle)));
    }
}

/// <summary>
/// One body's authored layer open in the planet designer (planet-designer-brief.md §6, D84, D85): map strokes, sites and
/// markers as undoable commands, the working copy the brush paints into (jobs only ever see frozen snapshots), and save
/// to <c>data/planets/&lt;body&gt;/</c> with the content version bumped. Main thread only.
/// </summary>
public sealed class PlanetDesignSession
{
    public const string ShapeMap = "shape";
    public const int MapRes = 1024;
    public const float MapScale = 0.05f;
    public const int MaxUndo = 64;

    private readonly List<Entry> _undo = [];
    private readonly PlanetGenerator _generated;
    private int _cursor;
    private Entry? _stroke;
    private int _savedAt;
    private int _commits;

    private sealed class Entry
    {
        public AuthoredMap? MapBefore, MapAfter;
        public AuthoredSite[]? SitesBefore, SitesAfter;
        public AuthoredMarker[]? MarkersBefore, MarkersAfter;
        public DesignRegion Region;
    }

    /// <param name="generated">The body without its authored layer: site heights are picked from it.</param>
    public PlanetDesignSession(AuthoredLayer layer, string folder, PlanetGenerator generated)
    {
        Layer = layer;
        Folder = folder;
        _generated = generated;

        if (Layer.Map(ShapeMap) is null)
            Layer.Maps.Add(new AuthoredMapBinding(new AuthoredMap(ShapeMap, MapRes, MapScale), AuthoredMapMode.Height));

        Shape = Layer.Map(ShapeMap)!;
    }

    /// <summary>Opens the body's saved layer, or starts an empty one made on these params.</summary>
    public static PlanetDesignSession Open(string folder, in PlanetParams p, string bodyId)
    {
        var generated = new PlanetGenerator(p with { BodyId = null });

        if (File.Exists(Path.Combine(folder, AuthoredLayer.BodyFile)))
        {
            var loaded = AuthoredLayer.Load(folder);

            if (!loaded.Matches(p))
                throw new InvalidDataException($"Authored layer {bodyId} was made on seed {loaded.Seed}, kind {loaded.Kind} {loaded.KindSeed}; this body is {p.Seed}, {p.KindId} {p.KindSeed}");

            return new PlanetDesignSession(loaded, folder, generated);
        }

        return new PlanetDesignSession(new AuthoredLayer { Id = bodyId, Seed = p.Seed, Kind = p.KindId, KindSeed = p.KindSeed }, folder, generated);
    }

    public AuthoredLayer Layer { get; }

    public string Folder { get; }

    /// <summary>The height map strokes paint into (editable; never handed to jobs).</summary>
    public AuthoredMap Shape { get; private set; }

    public PlanetGenerator Generated => _generated;

    public bool Dirty => _commits != _savedAt;

    public bool CanUndo => _cursor > 0 && _stroke is null;

    public bool CanRedo => _cursor < _undo.Count && _stroke is null;

    /// <summary>The version the next save writes: what new and moved sites are stamped with (D85).</summary>
    public int NextVersion => Layer.Version + 1;

    /// <summary>Set by every commit, undo and redo: where the terrain changed since the app last took it.</summary>
    public DesignRegion? Pending { get; private set; }

    public DesignRegion? TakePending()
    {
        var region = Pending;
        Pending = null;
        return region;
    }

    /// <summary>A frozen copy for the evaluator (register it in <see cref="AuthoredWorld"/>).</summary>
    public AuthoredLayer Snapshot() => Layer.Snapshot();

    public void BeginStroke()
    {
        if (_stroke is not null)
            throw new InvalidOperationException("A stroke is already open");

        _stroke = new Entry { MapBefore = Shape.Clone() };
    }

    /// <summary>One dab of the open stroke at unit direction d, radius in metres on the datum sphere.</summary>
    public int Dab(double3 d, double radiusM, float metres, AuthoredBrush brush, float strength)
    {
        var stroke = _stroke ?? throw new InvalidOperationException("No stroke open");
        var angle = radiusM / _generated.Params.Radius;
        var changed = Shape.Paint(d.X, d.Y, d.Z, angle, metres, brush, strength);

        if (changed > 0)
            stroke.Region = stroke.Region.Angle == 0.0 ? new DesignRegion(d, angle) : stroke.Region.Union(new DesignRegion(d, angle));

        return changed;
    }

    public void EndStroke()
    {
        var stroke = _stroke ?? throw new InvalidOperationException("No stroke open");
        _stroke = null;

        if (stroke.Region.Angle == 0.0)
            return;

        Shape.Compact();
        stroke.MapAfter = Shape.Clone();
        Commit(stroke);
    }

    /// <summary>A flatten site at unit direction d, its height the generated ground there unless given; returns its id.</summary>
    public string AddSite(double3 d, float coreM = 250f, float blendM = 200f, float? heightM = null)
    {
        var id = NewId("site", Layer.Sites.Select(s => s.Id));
        var height = heightM ?? SiteHeight(d);
        var radius = _generated.Params.Radius;
        var entry = SitesEntry(new DesignRegion(d, (coreM + blendM) / radius));
        Layer.Sites.Add(AuthoredSite.At(id, d * (radius + height), coreM, blendM, height, NextVersion));
        entry.SitesAfter = [.. Layer.Sites];
        Commit(entry);
        return id;
    }

    /// <summary>Moves, resizes or re-heights a site (one undo step); a moved site is stamped with the next version.</summary>
    public void SetSite(string id, double3 d, float coreM, float blendM, float heightM)
    {
        var k = SiteIndex(id);
        var old = Layer.Sites[k];
        var radius = _generated.Params.Radius;
        var oldDir = double3.Normalize(old.Position);
        var moved = double3.Dot(oldDir, d) < 1.0 - 1e-15;
        var region = new DesignRegion(oldDir, (old.CoreM + old.BlendM) / radius).Union(new DesignRegion(d, (coreM + blendM) / radius));
        var entry = SitesEntry(region);
        heightM = Math.Clamp(heightM, SiteFloor, _generated.Params.MaxHeight);
        Layer.Sites[k] = AuthoredSite.At(id, d * (radius + heightM), coreM, blendM, heightM, moved || coreM > old.CoreM ? NextVersion : old.Since);
        entry.SitesAfter = [.. Layer.Sites];
        Commit(entry);
    }

    public void DeleteSite(string id)
    {
        var k = SiteIndex(id);
        var s = Layer.Sites[k];
        var entry = SitesEntry(new DesignRegion(double3.Normalize(s.Position), (s.CoreM + s.BlendM) / _generated.Params.Radius));
        Layer.Sites.RemoveAt(k);
        entry.SitesAfter = [.. Layer.Sites];
        Commit(entry);
    }

    public string AddMarker(string type, string name, string? site)
    {
        var id = NewId(type, Layer.Markers.Select(m => m.Id));
        SetMarkers(() => Layer.Markers.Add(new AuthoredMarker(id, type, name, site)));
        return id;
    }

    public void SetMarker(string id, string type, string name, string? site) =>
        SetMarkers(() => Layer.Markers[Layer.Markers.FindIndex(m => m.Id == id)] = new AuthoredMarker(id, type, name, site));

    public void DeleteMarker(string id) => SetMarkers(() => Layer.Markers.RemoveAll(m => m.Id == id));

    /// <summary>Ground a site may stand at: clear of the protected band (D30).</summary>
    public float SiteFloor
    {
        get
        {
            var p = _generated.Params;
            return p.HasSea ? p.SeaHeight + p.ShoreProtectHeight + 1f : p.MinHeight;
        }
    }

    /// <summary>The ground at d with the painted height (other sites aside) to half a metre, raised clear of the protected band.</summary>
    public float SiteHeight(double3 d)
    {
        var ground = _generated.Surface.Height(d.X, d.Y, d.Z) + Shape.Sample(d.X, d.Y, d.Z);
        return Math.Clamp(MathF.Round(ground * 2f) * 0.5f, SiteFloor, _generated.Params.MaxHeight);
    }

    public void Undo()
    {
        if (!CanUndo)
            return;

        var e = _undo[--_cursor];
        Apply(e.MapBefore, e.SitesBefore, e.MarkersBefore);
        _commits++;
        Mark(e.Region);
    }

    public void Redo()
    {
        if (!CanRedo)
            return;

        var e = _undo[_cursor++];
        Apply(e.MapAfter, e.SitesAfter, e.MarkersAfter);
        _commits++;
        Mark(e.Region);
    }

    /// <summary>Writes the body file and maps with the version bumped (only when something changed since the last save).</summary>
    public void Save()
    {
        if (Dirty || !File.Exists(Path.Combine(Folder, AuthoredLayer.BodyFile)))
        {
            Layer.Version = NextVersion;
            Layer.Save(Folder);
        }

        _savedAt = _commits;
    }

    private void Apply(AuthoredMap? map, AuthoredSite[]? sites, AuthoredMarker[]? markers)
    {
        if (map is not null)
        {
            Shape = map.Clone();
            Layer.Maps[Layer.Maps.FindIndex(m => m.Map.Name == ShapeMap)] = new AuthoredMapBinding(Shape, AuthoredMapMode.Height);
        }

        if (sites is not null)
        {
            Layer.Sites.Clear();
            Layer.Sites.AddRange(sites);
        }

        if (markers is not null)
        {
            Layer.Markers.Clear();
            Layer.Markers.AddRange(markers);
        }
    }

    private void SetMarkers(Action change)
    {
        var entry = new Entry { MarkersBefore = [.. Layer.Markers] };
        change();
        entry.MarkersAfter = [.. Layer.Markers];
        Commit(entry);
    }

    private Entry SitesEntry(DesignRegion region) => new() { SitesBefore = [.. Layer.Sites], Region = region };

    private void Commit(Entry entry)
    {
        _undo.RemoveRange(_cursor, _undo.Count - _cursor);
        _undo.Add(entry);

        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);

        _cursor = _undo.Count;
        _commits++;

        if (entry.MapBefore is not null || entry.SitesBefore is not null)
            Mark(entry.Region);
    }

    private void Mark(DesignRegion region)
    {
        if (region.Angle == 0.0)
            return;

        Pending = Pending is { } p ? p.Union(region) : region;
    }

    private int SiteIndex(string id)
    {
        var k = Layer.Sites.FindIndex(s => s.Id == id);
        return k >= 0 ? k : throw new KeyNotFoundException($"No site '{id}'");
    }

    private static string NewId(string prefix, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.Ordinal);

        for (var n = 1; ; n++)
        {
            var id = $"{prefix}-{n}";

            if (!used.Contains(id))
                return id;
        }
    }
}
