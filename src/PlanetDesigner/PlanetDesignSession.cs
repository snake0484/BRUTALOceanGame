using Brutal.Numerics;
using BrutalGame.DesignKit;
using BrutalGame.Terrain;

namespace BrutalGame.PlanetDesigner;

/// <summary>Where a change landed: a unit direction and an angle (rad) about it; the whole body when the angle is π.</summary>
public readonly record struct DesignRegion(double3 Direction, double Angle)
{
    public DesignRegion Union(in DesignRegion other)
    {
        if (Angle == 0.0)
            return other;

        var between = Math.Acos(Math.Clamp(double3.Dot(Direction, other.Direction), -1.0, 1.0));
        return new DesignRegion(Direction, Math.Min(Math.PI, Math.Max(Angle, between + other.Angle)));
    }

    /// <summary>The ground a site's reach covers.</summary>
    public static DesignRegion Of(in AuthoredSite site, double radius) => new(double3.Normalize(site.Position), (site.CoreM + site.BlendM) / radius);
}

/// <summary>The planet designer's document: the body's authored layer, the height map strokes paint, and where the terrain changed since the app last refreshed.</summary>
public sealed class PlanetDoc(AuthoredLayer layer, AuthoredMap shape, double radius)
{
    public readonly AuthoredLayer Layer = layer;
    public readonly AuthoredMap Shape = shape;
    public readonly double Radius = radius;

    public DesignRegion? Pending;

    public void Mark(in DesignRegion region)
    {
        if (region.Angle > 0.0)
            Pending = Pending is { } p ? p.Union(region) : region;
    }
}

public enum PlanetOpKind : byte
{
    Tile,
    Site,
    Marker,
}

/// <summary>
/// One reversible change of the authored layer: a 32² map tile (codes before and after, and the stroke's region), or a
/// site or marker (before and after; null for none, so add and delete are the same op the other way round).
/// </summary>
public readonly struct PlanetOp : IUndoOp<PlanetOp, PlanetDoc>
{
    public readonly PlanetOpKind Kind;
    public readonly int Face, Tx, Ty, Index;
    public readonly short[]? TileBefore, TileAfter;
    public readonly DesignRegion Region;
    public readonly string Id;
    public readonly AuthoredSite? SiteBefore, SiteAfter;
    public readonly AuthoredMarker? MarkerBefore, MarkerAfter;

    private PlanetOp(PlanetOpKind kind, string id, int index, int face, int tx, int ty, short[]? tileBefore, short[]? tileAfter, DesignRegion region,
        AuthoredSite? siteBefore, AuthoredSite? siteAfter, AuthoredMarker? markerBefore, AuthoredMarker? markerAfter)
    {
        (Kind, Id, Index, Face, Tx, Ty, TileBefore, TileAfter, Region) = (kind, id, index, face, tx, ty, tileBefore, tileAfter, region);
        (SiteBefore, SiteAfter, MarkerBefore, MarkerAfter) = (siteBefore, siteAfter, markerBefore, markerAfter);
    }

    public static PlanetOp Tile(int face, int tx, int ty, short[] before, short[] after, DesignRegion region) =>
        new(PlanetOpKind.Tile, "", 0, face, tx, ty, before, after, region, null, null, null, null);

    public static PlanetOp Site(string id, int index, AuthoredSite? before, AuthoredSite? after) =>
        new(PlanetOpKind.Site, id, index, 0, 0, 0, null, null, default, before, after, null, null);

    public static PlanetOp Marker(string id, int index, AuthoredMarker? before, AuthoredMarker? after) =>
        new(PlanetOpKind.Marker, id, index, 0, 0, 0, null, null, default, null, null, before, after);

    public PlanetOp Inverse() => new(Kind, Id, Index, Face, Tx, Ty, TileAfter, TileBefore, Region, SiteAfter, SiteBefore, MarkerAfter, MarkerBefore);

    public void ApplyTo(PlanetDoc doc)
    {
        switch (Kind)
        {
            case PlanetOpKind.Tile:
                doc.Shape.WriteTile(Face, Tx, Ty, TileAfter);
                doc.Mark(Region);
                break;
            case PlanetOpKind.Site:
                Replace(doc.Layer.Sites, Id, s => s.Id, SiteAfter);

                if (SiteBefore is { } before)
                    doc.Mark(DesignRegion.Of(before, doc.Radius));

                if (SiteAfter is { } after)
                    doc.Mark(DesignRegion.Of(after, doc.Radius));

                break;
            case PlanetOpKind.Marker:
                Replace(doc.Layer.Markers, Id, m => m.Id, MarkerAfter);
                break;
        }
    }

    private void Replace<T>(List<T> list, string id, Func<T, string> idOf, T? after) where T : struct
    {
        var k = list.FindIndex(x => idOf(x) == id);

        if (after is not { } value)
            list.RemoveAt(k);
        else if (k < 0)
            list.Insert(Math.Min(Index, list.Count), value);
        else
            list[k] = value;
    }
}

/// <summary>
/// One body's authored layer open in the planet designer (planet-designer-brief.md §6, D89, D90) on the shared
/// <see cref="EditSession{TOp,TDoc}"/>: strokes paint the working map and commit as tile ops when they end; sites and
/// markers are ops; save writes <c>data/planets/&lt;body&gt;/</c> with the content version bumped. Jobs only ever see
/// frozen snapshots. Main thread only.
/// </summary>
public sealed class PlanetDesignSession : EditSession<PlanetOp, PlanetDoc>
{
    public const string ShapeMap = "shape";
    public const int MapRes = 1024;
    public const float MapScale = 0.05f;

    private readonly PlanetGenerator _generated;
    private AuthoredMap? _strokeStart;
    private DesignRegion _strokeRegion;

    /// <param name="generated">The body without its authored layer: site heights are picked from it.</param>
    public PlanetDesignSession(AuthoredLayer layer, string folder, PlanetGenerator generated)
        : base(Document(layer, generated.Params.Radius), new UndoStack<PlanetOp, PlanetDoc>())
    {
        Folder = folder;
        Path = folder;
        _generated = generated;
    }

    private static PlanetDoc Document(AuthoredLayer layer, double radius)
    {
        if (layer.Map(ShapeMap) is null)
            layer.Maps.Add(new AuthoredMapBinding(new AuthoredMap(ShapeMap, MapRes, MapScale), AuthoredMapMode.Height));

        return new PlanetDoc(layer, layer.Map(ShapeMap)!, radius);
    }

    /// <summary>Opens the body's saved layer, or starts an empty one made on these params.</summary>
    public static PlanetDesignSession Open(string folder, in PlanetParams p, string bodyId)
    {
        var generated = new PlanetGenerator(p with { BodyId = null });

        if (File.Exists(System.IO.Path.Combine(folder, AuthoredLayer.BodyFile)))
        {
            var loaded = AuthoredLayer.Load(folder);

            if (!loaded.Matches(p))
                throw new InvalidDataException($"Authored layer {bodyId} was made on seed {loaded.Seed}, kind {loaded.Kind} {loaded.KindSeed}; this body is {p.Seed}, {p.KindId} {p.KindSeed}");

            return new PlanetDesignSession(loaded, folder, generated);
        }

        return new PlanetDesignSession(new AuthoredLayer { Id = bodyId, Seed = p.Seed, Kind = p.KindId, KindSeed = p.KindSeed }, folder, generated);
    }

    public AuthoredLayer Layer => Doc.Layer;

    public string Folder { get; }

    /// <summary>The height map strokes paint into (editable; never handed to jobs).</summary>
    public AuthoredMap Shape => Doc.Shape;

    public PlanetGenerator Generated => _generated;

    public bool Stroking => _strokeStart is not null;

    public bool CanUndo => UndoLog.UndoCount > 0 && !Stroking;

    public bool CanRedo => UndoLog.RedoCount > 0 && !Stroking;

    /// <summary>The version the next save writes: what new and moved sites are stamped with (D90).</summary>
    public int NextVersion => Layer.Version + 1;

    /// <summary>Where the terrain changed since the app last took it (commits, undo and redo).</summary>
    public DesignRegion? TakePending()
    {
        var region = Doc.Pending;
        Doc.Pending = null;
        return region;
    }

    /// <summary>A frozen copy for the evaluator (register it in <see cref="AuthoredWorld"/>).</summary>
    public AuthoredLayer Snapshot() => Layer.Snapshot();

    public void BeginStroke()
    {
        if (Stroking)
            throw new InvalidOperationException("A stroke is already open");

        _strokeStart = Shape.Clone();
        _strokeRegion = default;
    }

    /// <summary>One dab of the open stroke at unit direction d, radius in metres on the datum sphere.</summary>
    public int Dab(double3 d, double radiusM, float metres, AuthoredBrush brush, float strength)
    {
        if (!Stroking)
            throw new InvalidOperationException("No stroke open");

        var angle = radiusM / Doc.Radius;
        var changed = Shape.Paint(d.X, d.Y, d.Z, angle, metres, brush, strength);

        if (changed > 0)
            _strokeRegion = _strokeRegion.Union(new DesignRegion(d, angle));

        return changed;
    }

    /// <summary>Commits the stroke as one undo group: an op per changed tile.</summary>
    public void EndStroke()
    {
        var start = _strokeStart ?? throw new InvalidOperationException("No stroke open");
        _strokeStart = null;

        if (_strokeRegion.Angle == 0.0)
            return;

        Edit(() =>
        {
            Span<short> before = stackalloc short[AuthoredMap.Tile * AuthoredMap.Tile];
            Span<short> after = stackalloc short[AuthoredMap.Tile * AuthoredMap.Tile];

            for (var face = 0; face < AuthoredMap.Faces; face++)
            {
                for (var ty = 0; ty < Shape.TilesPerSide; ty++)
                {
                    for (var tx = 0; tx < Shape.TilesPerSide; tx++)
                    {
                        start.ReadTile(face, tx, ty, before);
                        Shape.ReadTile(face, tx, ty, after);

                        if (!before.SequenceEqual(after))
                            Apply(PlanetOp.Tile(face, tx, ty, before.ToArray(), after.ToArray(), _strokeRegion));
                    }
                }
            }
        });
    }

    /// <summary>A flatten site at unit direction d, its height the ground there unless given; returns its id.</summary>
    public string AddSite(double3 d, float coreM = 250f, float blendM = 200f, float? heightM = null)
    {
        var id = NewId("site", Layer.Sites.Select(s => s.Id));
        var height = heightM ?? SiteHeight(d);
        var site = AuthoredSite.At(id, d * (Doc.Radius + height), coreM, blendM, height, NextVersion);
        Edit(() => Apply(PlanetOp.Site(id, Layer.Sites.Count, null, site)));
        return id;
    }

    /// <summary>Moves, resizes or re-heights a site (one undo step); a moved or widened site is stamped with the next version.</summary>
    public void SetSite(string id, double3 d, float coreM, float blendM, float heightM)
    {
        var k = SiteIndex(id);
        var old = Layer.Sites[k];
        var moved = double3.Dot(double3.Normalize(old.Position), d) < 1.0 - 1e-15;
        heightM = Math.Clamp(heightM, SiteFloor, _generated.Params.MaxHeight);
        var site = AuthoredSite.At(id, d * (Doc.Radius + heightM), coreM, blendM, heightM, moved || coreM > old.CoreM ? NextVersion : old.Since);
        Edit(() => Apply(PlanetOp.Site(id, k, old, site)));
    }

    /// <summary>Deletes a site and the markers on it (one undo step).</summary>
    public void DeleteSite(string id)
    {
        var k = SiteIndex(id);
        var old = Layer.Sites[k];

        Edit(() =>
        {
            for (var m = Layer.Markers.Count - 1; m >= 0; m--)
            {
                if (Layer.Markers[m].Site == id)
                    Apply(PlanetOp.Marker(Layer.Markers[m].Id, m, Layer.Markers[m], null));
            }

            Apply(PlanetOp.Site(id, k, old, null));
        });
    }

    public string AddMarker(string type, string name, string? site)
    {
        var id = NewId(type, Layer.Markers.Select(m => m.Id));
        Edit(() => Apply(PlanetOp.Marker(id, Layer.Markers.Count, null, new AuthoredMarker(id, type, name, site))));
        return id;
    }

    public void SetMarker(string id, string type, string name, string? site)
    {
        var k = Layer.Markers.FindIndex(m => m.Id == id);
        Edit(() => Apply(PlanetOp.Marker(id, k, Layer.Markers[k], new AuthoredMarker(id, type, name, site))));
    }

    public void DeleteMarker(string id)
    {
        var k = Layer.Markers.FindIndex(m => m.Id == id);
        Edit(() => Apply(PlanetOp.Marker(id, k, Layer.Markers[k], null)));
    }

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
        if (!Stroking)
            UndoLast();
    }

    public void Redo()
    {
        if (!Stroking)
            RedoNext();
    }

    /// <summary>Writes the body file and maps with the version bumped (only when something changed since the last save).</summary>
    public void Save()
    {
        if (Modified || !File.Exists(System.IO.Path.Combine(Folder, AuthoredLayer.BodyFile)))
        {
            Layer.Version = NextVersion;
            Layer.Save(Folder);
        }

        MarkSaved(Folder);
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
