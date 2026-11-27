using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>Decal edits, each one undo group that swaps the plan's decals for an edited copy (the old set is never touched).</summary>
public static class DecalCommands
{
    public static string LastError { get; private set; } = "";

    /// <summary>A decal of image <paramref name="asset"/> at <paramref name="at"/> facing <paramref name="normal"/> (out of the hull); the rest from <paramref name="template"/> when given.</summary>
    public static uint AddDecal(DesignSession session, Vector3d at, Vector3d normal, DefId asset, HullDecal? template = null)
    {
        if (session.Doc.Decals is { } set && set.Decals.Count >= HullDecalSet.MaxDecals)
        {
            LastError = "The plan has the most decals it can hold";
            return 0;
        }

        var id = session.Doc.AllocateId();
        Change(session, data =>
        {
            var decal = template?.Clone() ?? new HullDecal();
            decal.Id = id;
            decal.Asset = asset.Value;
            (decal.X, decal.Y, decal.Z) = ComponentCommands.Quantise(at);
            decal.SetNormal(normal);
            data.Decals.Add(decal);
        });
        return id;
    }

    public static bool SetDecal(DesignSession session, uint id, Action<HullDecal> edit)
    {
        if (session.Doc.Decals is not { } set || set.IndexOf(id) < 0)
            return false;

        Change(session, data => edit(data.Decals[data.IndexOf(id)]));
        return true;
    }

    public static bool RemoveDecal(DesignSession session, uint id)
    {
        if (session.Doc.Decals is not { } set || set.IndexOf(id) < 0)
            return false;

        Change(session, data => data.Decals.RemoveAt(data.IndexOf(id)));
        return true;
    }

    /// <summary>One edit applied to every decal (one undo group).</summary>
    public static bool EditAll(DesignSession session, Action<HullDecal> edit)
    {
        if (session.Doc.Decals is null)
            return false;

        Change(session, data => data.Decals.ForEach(edit));
        return true;
    }

    private static void Change(DesignSession session, Action<HullDecalSet> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Decals;
            var after = before?.Clone() ?? new HullDecalSet();
            mutate(after);
            s.Apply(PlanOp.SetDecals(before, after.IsEmpty ? null : after));
        });
}
