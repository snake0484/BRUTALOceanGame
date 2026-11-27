using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer;

/// <summary>
/// Doorway and docking port edits in a component plan, each one undo group that swaps the plan's openings for an edited
/// copy. A new opening faces out of the surface it is put on, upright, centred where it is put, on the plan's first door
/// profile (a 1 x 2.1 m door or a 1.4 m port is made when there is none).
/// </summary>
public static class OpeningCommands
{
    public static string LastError { get; private set; } = "";

    public static uint AddOpening(DesignSession session, OpeningKind kind, Vector3d at, Vector3d normal)
    {
        if (session.Doc.Openings is { } set && set.Openings.Count >= HullOpeningSet.MaxOpenings)
        {
            LastError = $"A component has at most {HullOpeningSet.MaxOpenings} openings";
            return 0;
        }

        var profile = DoorProfile(session, kind);

        if (profile < 0)
            return 0;

        var points = session.Doc.Interior!.Profiles[profile].Points;
        var height = (points.Max(p => p.Y) - points.Min(p => p.Y)) * HullSolid.MetresPerPositionUnit;
        var rotation = Facing(normal);
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(rotation, m);
        var up = new Vector3d(m[1], m[4], m[7]);
        var origin = Sub(at, Scale(up, height / 2));
        var id = session.Doc.AllocateId();
        Change(session, data =>
        {
            var opening = new HullOpening { Id = id, Kind = kind, Profile = (byte)profile, Rotation = rotation };
            (opening.X, opening.Y, opening.Z) = ComponentCommands.Quantise(origin);
            data.Openings.Add(opening);
        });
        return id;
    }

    public static bool SetOpening(DesignSession session, uint id, Action<HullOpening> edit)
    {
        if (session.Doc.Openings is not { } set || set.IndexOf(id) < 0)
            return false;

        Change(session, data => edit(data.Openings[data.IndexOf(id)]));
        return true;
    }

    public static bool RemoveOpening(DesignSession session, uint id)
    {
        if (session.Doc.Openings is not { } set || set.IndexOf(id) < 0)
            return false;

        Change(session, data => data.Openings.RemoveAt(data.IndexOf(id)));
        return true;
    }

    /// <summary>The plan's first door profile, or a new one for <paramref name="kind"/> (-1 if none could be made).</summary>
    public static int DoorProfile(DesignSession session, OpeningKind kind)
    {
        var existing = session.Doc.Interior?.Profiles.FindIndex(p => p.Kind == ProfileKind.Door) ?? -1;

        if (existing >= 0)
            return existing;

        var shape = kind == OpeningKind.Door
            ? HallwayGeometry.Preset(HallwayPreset.Rectangle, 1.0, 2.1)
            : HallwayGeometry.Preset(HallwayPreset.Octagon, 1.4, 1.4);
        var index = InteriorCommands.AddProfile(session, kind == OpeningKind.Door ? "Door" : "Port", shape, kind: ProfileKind.Door);

        if (index < 0)
            LastError = InteriorCommands.LastError;

        return index;
    }

    /// <summary>The snap rotation whose +Z is nearest <paramref name="normal"/> with +Y as near straight up as that allows.</summary>
    public static uint Facing(Vector3d normal)
    {
        normal = Normalise(normal, new Vector3d(0, 0, 1));
        var up = Math.Abs(normal.Y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(0, 0, normal.Y > 0 ? -1 : 1);
        Span<double> m = stackalloc double[9];
        var best = SolidRotation.Identity;
        var bestScore = double.MinValue;

        for (var roll = 0; roll < 24; roll++)
        {
            for (var pitch = 0; pitch < 24; pitch++)
            {
                for (var yaw = 0; yaw < 24; yaw++)
                {
                    var rotation = SolidRotation.FromSteps(yaw, pitch, roll);
                    SolidRotation.Matrix(rotation, m);
                    var score = 4 * (m[2] * normal.X + m[5] * normal.Y + m[8] * normal.Z) + (m[1] * up.X + m[4] * up.Y + m[7] * up.Z);

                    if (score > bestScore + 1e-9)
                        (best, bestScore) = (rotation, score);
                }
            }
        }

        return best;
    }

    private static void Change(DesignSession session, Action<HullOpeningSet> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Openings;
            var after = before?.Clone() ?? new HullOpeningSet();
            mutate(after);
            s.Apply(PlanOp.SetOpenings(before, after.IsEmpty ? null : after));
        });
}
