using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer;

/// <summary>
/// Hallway edits, each one undo group that swaps the plan's interior for an edited copy (the old one is never touched).
/// New hallways start as a level 2 m run with a 1.2 × 2.2 m rounded profile, gravity the ship's own down.
/// </summary>
public static class InteriorCommands
{
    public const double DefaultWidth = 1.2;
    public const double DefaultHeight = 2.2;

    public static string LastError { get; private set; } = "";

    public static uint AddHallway(DesignSession session, Vector3d from, Vector3d to, DefId stairMaterial)
    {
        if (session.Doc.Interior is { } interior && interior.Hallways.Count >= HullInterior.MaxHallways)
        {
            LastError = "The plan has the most hallways it can hold";
            return 0;
        }

        var id = session.Doc.AllocateId();
        Change(session, (s, data) =>
        {
            var hallway = new HullHallway { Id = id, Material = s.EnsureMaterial(stairMaterial) };
            hallway.Nodes.Add(ComponentCommands.Quantise(from));
            hallway.Nodes.Add(ComponentCommands.Quantise(to));
            hallway.Profile.AddRange(HallwayGeometry.Preset(HallwayPreset.Rounded, DefaultWidth, DefaultHeight));
            data.Hallways.Add(hallway);
        });
        return id;
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

    /// <summary>Replaces a hallway's outline, moved so its lowest point is the floor (Y = 0).</summary>
    public static bool SetProfile(DesignSession session, uint id, IReadOnlyList<ProfilePoint> profile)
    {
        if (profile.Count is < HullHallway.MinProfilePoints or > HullHallway.MaxProfilePoints || HallwayGeometry.Area2(profile) == 0)
        {
            LastError = "A profile needs 3 to 32 points enclosing some area";
            return false;
        }

        var floor = profile.Min(p => p.Y);
        return SetHallway(session, id, h =>
        {
            h.Profile.Clear();

            foreach (var p in profile)
                h.Profile.Add(p with { Y = p.Y - floor });
        });
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
