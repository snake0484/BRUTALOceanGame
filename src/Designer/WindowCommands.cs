using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer;

/// <summary>Window edits, each one undo group that swaps the plan's windows for an edited copy (the old set is never touched).</summary>
public static class WindowCommands
{
    public static string LastError { get; private set; } = "";

    /// <summary>A window at <paramref name="at"/> facing <paramref name="normal"/> (out of the hull); the rest from <paramref name="template"/> when given.</summary>
    public static uint AddWindow(DesignSession session, Vector3d at, Vector3d normal, DefId material, HullWindow? template = null)
    {
        if (session.Doc.Windows is { } set && set.Windows.Count >= HullWindowSet.MaxWindows)
        {
            LastError = "The plan has the most windows it can hold";
            return 0;
        }

        var id = session.Doc.AllocateId();
        Change(session, (s, data) =>
        {
            var window = template?.Clone() ?? new HullWindow();
            window.Id = id;
            (window.X, window.Y, window.Z) = ComponentCommands.Quantise(at);
            window.SetNormal(normal);
            window.Material = s.EnsureMaterial(material);
            data.Windows.Add(window);
        });
        return id;
    }

    public static bool SetWindow(DesignSession session, uint id, Action<HullWindow> edit)
    {
        if (session.Doc.Windows is not { } set || set.IndexOf(id) < 0)
            return false;

        Change(session, (_, data) => edit(data.Windows[data.IndexOf(id)]));
        return true;
    }

    public static bool SetMaterial(DesignSession session, uint id, DefId material)
    {
        if (session.Doc.Windows is not { } set || set.IndexOf(id) < 0)
            return false;

        Change(session, (s, data) => data.Windows[data.IndexOf(id)].Material = s.EnsureMaterial(material));
        return true;
    }

    public static bool RemoveWindow(DesignSession session, uint id)
    {
        if (session.Doc.Windows is not { } set || set.IndexOf(id) < 0)
            return false;

        Change(session, (_, data) => data.Windows.RemoveAt(data.IndexOf(id)));
        return true;
    }

    private static void Change(DesignSession session, Action<DesignSession, HullWindowSet> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Windows;
            var after = before?.Clone() ?? new HullWindowSet();
            mutate(s, after);
            s.Apply(PlanOp.SetWindows(before, after.IsEmpty ? null : after));
        });
}
