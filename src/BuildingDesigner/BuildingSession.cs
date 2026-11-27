using BrutalGame.Buildings;
using BrutalGame.Designer;
using BrutalGame.Plans;

namespace BrutalGame.BuildingDesigner;

/// <summary>
/// One open building: the structure is a ship designer <see cref="DesignSession"/>, so every hull tool works on it as is;
/// <see cref="Info"/> rides the same history. An info edit commits a plan group that changes nothing in the plan (a marker),
/// and the info after each group is kept by history position, so Ctrl+Z walks plan and info edits in one order. Any plan
/// undo (including the Designer panel's buttons) restores the info at that position; <see cref="Sync"/> picks it up.
/// </summary>
public sealed class BuildingSession
{
    private readonly List<BuildingInfo> _infoAt = new();
    private int _seenUndo;

    public BuildingSession()
    {
        Plan = new DesignSession();
        Open(NewPlan(), BuildingInfo.Default, "");
    }

    public DesignSession Plan { get; }

    public BuildingInfo Info { get; private set; }

    public string Path => Plan.Path;

    public bool Modified => Plan.Modified;

    public int Version => Plan.Version;

    public static PlanDocument NewPlan() => new() { Level = PlanLevel.Ship, Class = PlanClass.Station };

    public void Open(PlanDocument plan, in BuildingInfo info, string path)
    {
        Plan.Open(plan, path);
        Info = info;
        _infoAt.Clear();
        _infoAt.Add(info);
        _seenUndo = 0;
    }

    public void MarkSaved(string path) => Plan.MarkSaved(path);

    /// <summary>Commits <paramref name="after"/> as one undo group; false when unchanged, invalid or inside another group.</summary>
    public bool SetInfo(in BuildingInfo after)
    {
        Sync();

        if (after == Info || !after.IsValid || Plan.Undo.InGroup)
            return false;

        var joints = Plan.Doc.Joints;
        Plan.Edit(() => Plan.Apply(PlanOp.SetJoints(joints, joints)));
        Info = after;
        Advance(Plan.Undo.UndoCount, dropAhead: true);
        return true;
    }

    public bool Undo()
    {
        Sync();

        if (!Plan.UndoLast())
            return false;

        Sync();
        return true;
    }

    public bool Redo()
    {
        Sync();

        if (!Plan.RedoNext())
            return false;

        var at = Plan.Undo.UndoCount;

        if (at < _infoAt.Count)
            Info = _infoAt[at];

        Advance(at, dropAhead: false);
        return true;
    }

    /// <summary>
    /// Follows plan history moves made outside this session: back restores the info kept for that position; forward (a
    /// new plan group, or the panel's redo) keeps the current info there and drops positions past it.
    /// </summary>
    public void Sync()
    {
        var at = Plan.Undo.UndoCount;

        if (at == _seenUndo)
            return;

        if (at < _seenUndo)
        {
            Info = _infoAt[at];
            _seenUndo = at;
            return;
        }

        Advance(at, dropAhead: true);
    }

    /// <summary>The current info holds at every position up to <paramref name="at"/> passed since the last seen one.</summary>
    private void Advance(int at, bool dropAhead)
    {
        while (_infoAt.Count <= at)
            _infoAt.Add(Info);

        for (var k = _seenUndo + 1; k <= at; k++)
            _infoAt[k] = Info;

        if (dropAhead)
            _infoAt.RemoveRange(at + 1, _infoAt.Count - at - 1);

        _seenUndo = at;
    }
}
