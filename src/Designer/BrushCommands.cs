using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>Sweep profile presets: raised detail, with a short skirt under the surface so it seats on curved hulls.</summary>
public enum ProfilePreset : byte { Rib, HalfRound, Ridge }

/// <summary>
/// Brush and stroke commands, each one undo group. The brush set is replaced whole on every edit (a
/// <see cref="OpKind.SetBrushes"/> op holding before and after copies), so undo restores it exactly.
/// </summary>
public static class BrushCommands
{
    public static readonly string[] PresetNames = ["Rib", "Half round", "Ridge"];

    private const int Units = HullSolid.PositionUnitsPerMetre;
    private const int HalfRoundSegments = 8;

    public static string LastError { get; private set; } = "";

    /// <summary>A preset profile <paramref name="width"/> across and <paramref name="height"/> deep, in metres.</summary>
    public static List<ProfilePoint> Profile(ProfilePreset preset, double width, double height)
    {
        int w = Metres(width / 2.0), h = Metres(height), skirt = Math.Max(Metres(height / 4.0), 1);
        var points = new List<ProfilePoint>();

        switch (preset)
        {
            case ProfilePreset.HalfRound:
                points.Add(new ProfilePoint(w, -skirt));

                for (var i = 0; i <= HalfRoundSegments; i++)
                {
                    var a = Math.PI * i / HalfRoundSegments;
                    points.Add(new ProfilePoint((int)Math.Round(w * Math.Cos(a)), (int)Math.Round(h * Math.Sin(a))));
                }

                points.Add(new ProfilePoint(-w, -skirt));
                break;
            case ProfilePreset.Ridge:
                points.Add(new ProfilePoint(-w, -skirt));
                points.Add(new ProfilePoint(w, -skirt));
                points.Add(new ProfilePoint(w, 0));
                points.Add(new ProfilePoint(0, h));
                points.Add(new ProfilePoint(-w, 0));
                break;
            default:
                points.Add(new ProfilePoint(-w, -skirt));
                points.Add(new ProfilePoint(w, -skirt));
                points.Add(new ProfilePoint(w, h));
                points.Add(new ProfilePoint(-w, h));
                break;
        }

        return points;
    }

    /// <summary>A new swept-profile brush; returns its index.</summary>
    public static int AddSweep(DesignSession session, ProfilePreset preset, double width, double height)
    {
        var index = -1;
        Change(session, set =>
        {
            var brush = new HullBrush { Kind = BrushKind.Sweep };
            brush.Profile.AddRange(Profile(preset, width, height));
            index = set.Brushes.Count;
            set.Brushes.Add(brush);
        });
        return index;
    }

    /// <summary>
    /// A new stamp brush from a solid's shape: its mesh (a primitive is meshed), in the solid's own axes, lifted so its
    /// base sits just under the hull surface. The solid stays as it is. Returns the brush index, or -1.
    /// </summary>
    public static int AddStampFromSolid(DesignSession session, uint solidId)
    {
        if (!session.TryGetSolid(solidId, out var solid))
            return Fail("Select a solid to make a stamp from");

        HullMesh mesh;

        if (solid.Shape == SolidShape.Mesh && solid.Param < session.Doc.Meshes.Count)
            mesh = session.Doc.Meshes[solid.Param];
        else if (SolidGeometry.IsSupported(solid.Shape))
            mesh = MeshPrimitives.FromSolid(solid);
        else
            return Fail("That shape cannot be a stamp");

        var lifted = Lift(mesh);
        var (_, _, z) = lifted.Extent();
        var index = -1;

        Change(session, set =>
        {
            var slot = session.Doc.Meshes.Count;
            session.Apply(PlanOp.AddMesh(slot, lifted));
            index = set.Brushes.Count;
            set.Brushes.Add(new HullBrush { Kind = BrushKind.Stamp, Mesh = (ushort)slot, Spacing = Math.Max(z * 3, Units / 4) });
        });

        return index;
    }

    public static bool SetProfile(DesignSession session, int index, ProfilePreset preset, double width, double height) =>
        ChangeBrush(session, index, b =>
        {
            b.Profile.Clear();
            b.Profile.AddRange(Profile(preset, width, height));
        });

    public static bool SetSpacing(DesignSession session, int index, double metres) =>
        ChangeBrush(session, index, b => b.Spacing = Math.Max(Metres(metres), HullBrush.MinSpacing));

    /// <summary>Removes a brush no stroke uses; later brushes move down one index.</summary>
    public static bool RemoveBrush(DesignSession session, int index)
    {
        var set = session.Doc.Brushes;

        if (set is null || index < 0 || index >= set.Brushes.Count)
            return false;

        if (set.Strokes.Exists(s => s.Brush == index))
        {
            LastError = "Delete the strokes drawn with this brush first";
            return false;
        }

        Change(session, s =>
        {
            s.Brushes.RemoveAt(index);

            foreach (var stroke in s.Strokes)
            {
                if (stroke.Brush > index)
                    stroke.Brush--;
            }
        });

        return true;
    }

    /// <summary>A new stroke through <paramref name="points"/> (≥ 2); returns its id, or 0.</summary>
    public static uint AddStroke(DesignSession session, int brush, IReadOnlyList<StrokePoint> points, byte material)
    {
        var set = session.Doc.Brushes;

        if (set is null || brush < 0 || brush >= set.Brushes.Count)
        {
            LastError = "Pick a brush first";
            return 0;
        }

        if (points.Count < HullStroke.MinPoints || points.Count > HullStroke.MaxPoints)
        {
            LastError = $"A stroke needs {HullStroke.MinPoints} to {HullStroke.MaxPoints} points";
            return 0;
        }

        var id = session.Doc.AllocateId();
        Change(session, s =>
        {
            var stroke = new HullStroke { Id = id, Brush = (ushort)brush, Material = material };
            stroke.Points.AddRange(points);
            s.Strokes.Add(stroke);
        });
        return id;
    }

    /// <summary>Edits a stroke (points, material, offset, scale, brush) as one undo group.</summary>
    public static bool SetStroke(DesignSession session, uint id, Action<HullStroke> edit)
    {
        var set = session.Doc.Brushes;

        if (set is null || set.IndexOfStroke(id) < 0)
            return false;

        Change(session, s => edit(s.Strokes[s.IndexOfStroke(id)]));
        return true;
    }

    public static bool RemoveStroke(DesignSession session, uint id)
    {
        var set = session.Doc.Brushes;

        if (set is null || set.IndexOfStroke(id) < 0)
            return false;

        Change(session, s => s.Strokes.RemoveAt(s.IndexOfStroke(id)));
        return true;
    }

    private static bool ChangeBrush(DesignSession session, int index, Action<HullBrush> edit)
    {
        var set = session.Doc.Brushes;

        if (set is null || index < 0 || index >= set.Brushes.Count)
            return false;

        Change(session, s => edit(s.Brushes[index]));
        return true;
    }

    private static void Change(DesignSession session, Action<HullBrushSet> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Brushes;
            var after = before?.Clone() ?? new HullBrushSet();
            mutate(after);
            s.Apply(PlanOp.SetBrushes(before, after.IsEmpty ? null : after));
        });

    /// <summary>The mesh moved up so its lowest point is a little under the origin (the hull surface).</summary>
    private static HullMesh Lift(HullMesh mesh)
    {
        var minY = int.MaxValue;
        var maxY = int.MinValue;

        foreach (var v in mesh.Vertices)
        {
            minY = Math.Min(minY, v.Y);
            maxY = Math.Max(maxY, v.Y);
        }

        var sink = Math.Min(Units / 10, (maxY - minY) / 4);
        var shift = -minY - sink;
        var vertices = new MeshVertex[mesh.Vertices.Length];

        for (var i = 0; i < vertices.Length; i++)
            vertices[i] = mesh.Vertices[i] with { Y = mesh.Vertices[i].Y + shift };

        return new HullMesh(vertices, mesh.FaceSizes, mesh.Corners, mesh.Panels, mesh.Creases, mesh.SubdivLevels);
    }

    private static int Fail(string message)
    {
        LastError = message;
        return -1;
    }

    private static int Metres(double metres) => (int)Math.Round(metres * Units);
}
