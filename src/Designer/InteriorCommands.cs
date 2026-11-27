using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer;

/// <summary>
/// Hallway and profile edits, each one undo group that swaps the plan's interior for an edited copy (the old one is never
/// touched). Profiles are the plan's own copies: changing one reshapes every hallway that uses it; the library on disk
/// reaches a plan only through an import or a deliberate reimport.
/// </summary>
public static class InteriorCommands
{
    public const double DefaultWidth = 1.2;
    public const double DefaultHeight = 2.2;
    public const string DefaultProfileName = "Rounded 1.2 x 2.2 m";

    public static string LastError { get; private set; } = "";

    /// <summary>A two-node hallway; it uses <paramref name="profile"/>, or the plan's first profile (made if there is none).</summary>
    public static uint AddHallway(DesignSession session, Vector3d from, Vector3d to, DefId stairMaterial, int profile = -1)
    {
        if (session.Doc.Interior is { } interior && interior.Hallways.Count >= HullInterior.MaxHallways)
        {
            LastError = "The plan has the most hallways it can hold";
            return 0;
        }

        var id = session.Doc.AllocateId();
        Change(session, (s, data) =>
        {
            if (data.Profiles.Count == 0)
                data.Profiles.Add(Default());

            var hallway = new HullHallway
            {
                Id = id,
                Material = s.EnsureMaterial(stairMaterial),
                Profile = (byte)Math.Clamp(profile, 0, data.Profiles.Count - 1),
            };
            hallway.Chain([ComponentCommands.Quantise(from), ComponentCommands.Quantise(to)]);
            data.Hallways.Add(hallway);
        });
        return id;
    }

    public static HullProfile Default()
    {
        var p = new HullProfile { Name = DefaultProfileName };
        p.Points.AddRange(HallwayGeometry.Preset(HallwayPreset.Rounded, DefaultWidth, DefaultHeight));
        return p;
    }

    public static bool SetHallway(DesignSession session, uint id, Action<HullHallway> edit)
    {
        if (session.Doc.Interior is not { } interior || interior.IndexOf(id) < 0)
            return false;

        Change(session, (_, data) => edit(data.Hallways[data.IndexOf(id)]));
        return true;
    }

    public static bool SetMaterial(DesignSession session, uint id, DefId material)
    {
        if (session.Doc.Interior is not { } interior || interior.IndexOf(id) < 0)
            return false;

        Change(session, (s, data) => data.Hallways[data.IndexOf(id)].Material = s.EnsureMaterial(material));
        return true;
    }

    public static bool RemoveHallway(DesignSession session, uint id)
    {
        if (session.Doc.Interior is not { } interior || interior.IndexOf(id) < 0)
            return false;

        Change(session, (_, data) => data.Hallways.RemoveAt(data.IndexOf(id)));
        return true;
    }

    // ---- the node network ----

    /// <summary>A new node at <paramref name="at"/> joined to node <paramref name="from"/>: from an end it extends the hallway, from a node mid-way it branches (a junction). Returns its index, or -1.</summary>
    public static int AddNode(DesignSession session, uint id, int from, Vector3d at)
    {
        if (Find(session, id) is not { } hallway || from < 0 || from >= hallway.Nodes.Count)
            return -1;

        if (hallway.Nodes.Count >= HullHallway.MaxNodes || hallway.Runs.Count >= HullHallway.MaxRuns)
        {
            LastError = "The hallway has the most nodes it can hold";
            return -1;
        }

        var node = hallway.Nodes.Count;
        SetHallway(session, id, h =>
        {
            h.Nodes.Add(ComponentCommands.Quantise(at));
            h.Runs.Add(((byte)from, (byte)node));
        });
        return node;
    }

    /// <summary>Splits run <paramref name="run"/> at its middle with a new node. Returns its index, or -1.</summary>
    public static int SplitRun(DesignSession session, uint id, int run)
    {
        if (Find(session, id) is not { } hallway || run < 0 || run >= hallway.Runs.Count || hallway.Nodes.Count >= HullHallway.MaxNodes)
            return -1;

        var node = hallway.Nodes.Count;
        SetHallway(session, id, h =>
        {
            var (a, b) = h.Runs[run];
            h.Nodes.Add(ComponentCommands.Quantise(Scale(Add(h.Node(a), h.Node(b)), 0.5)));
            h.Runs[run] = (a, (byte)node);
            h.Runs.Add(((byte)node, b));
        });
        return node;
    }

    /// <summary>Removes a node (one between two runs is dissolved); a hallway left with fewer than two nodes goes too.</summary>
    public static bool RemoveNode(DesignSession session, uint id, int node)
    {
        if (Find(session, id) is not { } hallway || node < 0 || node >= hallway.Nodes.Count)
            return false;

        if (hallway.Nodes.Count <= 2)
            return RemoveHallway(session, id);

        return SetHallway(session, id, h => h.RemoveNode(node));
    }

    // ---- profiles ----

    /// <summary>A new plan profile (its floor moved to Y = 0). Returns its index, or -1.</summary>
    public static int AddProfile(DesignSession session, string name, IReadOnlyList<ProfilePoint> points, string source = "")
    {
        if (!Check(points))
            return -1;

        if (session.Doc.Interior is { } interior && interior.Profiles.Count >= HullInterior.MaxProfiles)
        {
            LastError = "The plan has the most profiles it can hold";
            return -1;
        }

        var index = session.Doc.Interior?.Profiles.Count ?? 0;
        Change(session, (_, data) =>
        {
            var profile = new HullProfile { Name = name, Source = source };
            profile.Points.AddRange(Floored(points));
            data.Profiles.Add(profile);
        });
        return index;
    }

    /// <summary>Replaces a profile's outline (its floor moved to Y = 0): every hallway using it changes.</summary>
    public static bool SetProfilePoints(DesignSession session, int profile, IReadOnlyList<ProfilePoint> points)
    {
        if (!Check(points) || session.Doc.Interior is not { } interior || profile < 0 || profile >= interior.Profiles.Count)
            return false;

        Change(session, (_, data) =>
        {
            data.Profiles[profile].Points.Clear();
            data.Profiles[profile].Points.AddRange(Floored(points));
        });
        return true;
    }

    public static bool SetProfile(DesignSession session, int profile, Action<HullProfile> edit)
    {
        if (session.Doc.Interior is not { } interior || profile < 0 || profile >= interior.Profiles.Count)
            return false;

        Change(session, (_, data) => edit(data.Profiles[profile]));
        return true;
    }

    /// <summary>Removes a profile no hallway uses (later profiles move down one, and the hallways' indices with them).</summary>
    public static bool RemoveProfile(DesignSession session, int profile)
    {
        if (session.Doc.Interior is not { } interior || profile < 0 || profile >= interior.Profiles.Count)
            return false;

        if (interior.Hallways.Any(h => h.Profile == profile))
        {
            LastError = "Hallways use this profile: give them another first";
            return false;
        }

        Change(session, (_, data) =>
        {
            data.Profiles.RemoveAt(profile);

            foreach (var h in data.Hallways)
            {
                if (h.Profile > profile)
                    h.Profile--;
            }
        });
        return true;
    }

    public static bool SetHallwayProfile(DesignSession session, uint id, int profile)
    {
        if (session.Doc.Interior is not { } interior || profile < 0 || profile >= interior.Profiles.Count)
            return false;

        return SetHallway(session, id, h => h.Profile = (byte)profile);
    }

    private static HullHallway? Find(DesignSession session, uint id) =>
        session.Doc.Interior is { } interior && interior.IndexOf(id) is var index and >= 0 ? interior.Hallways[index] : null;

    private static bool Check(IReadOnlyList<ProfilePoint> points)
    {
        if (points.Count is < HullProfile.MinPoints or > HullProfile.MaxPoints || HallwayGeometry.Area2(points) == 0)
        {
            LastError = "A profile needs 3 to 32 points enclosing some area";
            return false;
        }

        return true;
    }

    private static IEnumerable<ProfilePoint> Floored(IReadOnlyList<ProfilePoint> points)
    {
        var floor = points.Min(p => p.Y);
        return points.Select(p => p with { Y = p.Y - floor });
    }

    private static void Change(DesignSession session, Action<DesignSession, HullInterior> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Interior;
            var after = before?.Clone() ?? new HullInterior();
            mutate(s, after);
            s.Apply(PlanOp.SetInterior(before, after.IsEmpty ? null : after));
        });
}
