using BrutalGame.Designer.Tools;
using BrutalGame.Plans;
using BrutalGame.Ships;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer;

/// <summary>
/// Rotor, hinge and slider edits, each one undo group that swaps the plan's joints for an edited copy. A joint is mounted where it
/// is placed: on the ship, or on the moving part of the joint whose solid it sits on (at most three deep). Solids join a
/// joint's moving part with <see cref="SetMember"/>.
/// </summary>
public static class JointCommands
{
    public static string LastError { get; private set; } = "";

    /// <summary>A joint at <paramref name="at"/> turning about <paramref name="axis"/> (snapped to 15 degrees), mounted on whatever <paramref name="mountedOn"/> moves with; returns its id, or 0.</summary>
    public static uint AddJoint(DesignSession session, JointKind kind, Vector3d at, Vector3d axis, uint mountedOn)
    {
        var set = session.Doc.Joints;

        if (set is not null && set.Joints.Count >= HullJointSet.MaxJoints)
            return Fail($"A ship has at most {HullJointSet.MaxJoints} rotors and hinges");

        var parent = set?.JointOf(mountedOn) ?? 0;

        if (parent != 0 && set!.Depth(parent) >= HullJointSet.MaxDepth)
            return Fail($"Joints nest at most {HullJointSet.MaxDepth} deep");

        var id = session.Doc.AllocateId();
        Change(session, data =>
        {
            var joint = new HullJoint { Id = id, Kind = kind, Parent = parent, Rotation = Snap(axis) };
            (joint.X, joint.Y, joint.Z) = ComponentCommands.Quantise(at);

            if (kind == JointKind.Hinge)
                joint.Speed = 300;

            if (kind == JointKind.Slider)
                (joint.Speed, joint.Min, joint.Max, joint.Target) = (500, 0, 1000, 0);

            data.Joints.Add(joint);
        });
        return id;
    }

    public static bool SetJoint(DesignSession session, uint id, Action<HullJoint> edit)
    {
        if (session.Doc.Joints is not { } set || set.IndexOf(id) < 0)
            return false;

        Change(session, data => edit(data.Joints[data.IndexOf(id)]));
        return true;
    }

    /// <summary>Removes a joint: its solids and the joints mounted on it pass to whatever it was mounted on.</summary>
    public static bool RemoveJoint(DesignSession session, uint id)
    {
        if (session.Doc.Joints is not { } set || set.IndexOf(id) < 0)
            return false;

        Change(session, data =>
        {
            var parent = data.Joints[data.IndexOf(id)].Parent;
            data.Joints.RemoveAt(data.IndexOf(id));

            foreach (var j in data.Joints)
            {
                if (j.Parent == id)
                    j.Parent = parent;
            }

            for (var i = data.Members.Count - 1; i >= 0; i--)
            {
                if (data.Members[i].Joint != id)
                    continue;

                if (parent == 0)
                    data.Members.RemoveAt(i);
                else
                    data.Members[i] = (data.Members[i].Solid, parent);
            }
        });
        return true;
    }

    /// <summary>
    /// Mounts joint <paramref name="id"/> on joint <paramref name="parent"/>'s moving part (0: on the ship). Refused when the
    /// parent is the joint itself or mounted on it (a loop), or the joint and those on it would nest more than
    /// <see cref="HullJointSet.MaxDepth"/> deep.
    /// </summary>
    public static bool SetParent(DesignSession session, uint id, uint parent)
    {
        if (session.Doc.Joints is not { } set || set.IndexOf(id) < 0 || (parent != 0 && set.IndexOf(parent) < 0))
            return false;

        for (var at = parent; at != 0; at = set.Joints[set.IndexOf(at)].Parent)
        {
            if (at == id)
            {
                LastError = "A joint cannot be mounted on its own moving part";
                return false;
            }
        }

        if ((parent == 0 ? 0 : set.Depth(parent)) + Height(set, id) > HullJointSet.MaxDepth)
        {
            LastError = $"Joints nest at most {HullJointSet.MaxDepth} deep";
            return false;
        }

        Change(session, data => data.Joints[data.IndexOf(id)].Parent = parent);
        return true;
    }

    /// <summary>How many joints deep the chain from <paramref name="id"/> down through those mounted on it goes (1: none on it).</summary>
    private static int Height(HullJointSet set, uint id, int guard = 0)
    {
        var most = 0;

        foreach (var j in set.Joints)
        {
            if (j.Parent == id && guard < HullJointSet.MaxJoints)
                most = Math.Max(most, Height(set, j.Id, guard + 1));
        }

        return most + 1;
    }

    /// <summary>Solid <paramref name="solid"/> moves with joint <paramref name="joint"/> (0: it is the ship's own again).</summary>
    public static bool SetMember(DesignSession session, uint solid, uint joint)
    {
        if (session.Doc.Solids.IndexOf(solid) < 0 || (joint != 0 && (session.Doc.Joints is not { } set || set.IndexOf(joint) < 0)))
            return false;

        Change(session, data =>
        {
            data.Members.RemoveAll(m => m.Solid == solid);

            if (joint != 0)
                data.Members.Add((solid, joint));
        });
        return true;
    }

    /// <summary>The snap rotation whose +Y is nearest <paramref name="axis"/> (no roll when several are as near).</summary>
    public static uint Snap(Vector3d axis)
    {
        axis = Normalise(axis, new Vector3d(0, 1, 0));
        Span<double> m = stackalloc double[9];
        var best = SolidRotation.Identity;
        var bestDot = double.MinValue;

        for (var roll = 0; roll < 24; roll++)
        {
            for (var pitch = 0; pitch < 24; pitch++)
            {
                for (var yaw = 0; yaw < 24; yaw++)
                {
                    var rotation = SolidRotation.FromSteps(yaw, pitch, roll);
                    SolidRotation.Matrix(rotation, m);
                    var dot = m[1] * axis.X + m[4] * axis.Y + m[7] * axis.Z;

                    if (dot > bestDot + 1e-9)
                        (best, bestDot) = (rotation, dot);
                }
            }
        }

        return best;
    }

    private static uint Fail(string message)
    {
        LastError = message;
        return 0;
    }

    private static void Change(DesignSession session, Action<HullJointSet> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Joints;
            var after = before?.Clone() ?? new HullJointSet();
            mutate(after);
            s.Apply(PlanOp.SetJoints(before, after.IsEmpty ? null : after));
        });
}
