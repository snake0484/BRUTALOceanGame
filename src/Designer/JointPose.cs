using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>
/// Where each moving part is at a moment of the preview: a rotor turns at its speed; a hinge swings (a slider slides)
/// from its lower limit to its upper one and back at its speed. Each group's transform (p' = R p + t, ship space) turns it about its joint's
/// axis through the pivot, after its parent's transform.
/// </summary>
public static class JointPose
{
    /// <summary>
    /// A joint's angle (radians; a slider's travel, metres) <paramref name="seconds"/> into the preview: at rest (0 s) a
    /// hinge or slider is at its target, then runs to its upper limit, back to its lower one, and so on.
    /// </summary>
    public static double Angle(HullJoint joint, double seconds)
    {
        if (joint.Kind == JointKind.Rotor)
            return joint.Speed / 10.0 * 6.0 * seconds * Math.PI / 180.0;

        double min = Value(joint, joint.Min), max = Value(joint, joint.Max), range = max - min;
        var target = Math.Clamp(Value(joint, joint.Target), min, max);

        if (range <= 1e-9 || joint.Speed == 0)
            return target;

        var travel = (Math.Abs(Value(joint, joint.Speed)) * seconds + (target - min)) % (2 * range);
        return min + (travel < range ? travel : 2 * range - travel);
    }

    /// <summary>A stored limit, target or speed in radians (a slider's in metres).</summary>
    public static double Value(HullJoint joint, short stored) =>
        joint.Kind == JointKind.Slider ? stored / 1000.0 : stored / 10.0 * Math.PI / 180.0;

    /// <summary>Each group's rotation (row-major, 9 per group) and translation; group 0 stays put.</summary>
    public static void Transforms(JointGroups groups, double seconds, double[] rotations, Vector3d[] translations)
    {
        Span<double> angles = stackalloc double[groups.Count];

        for (var g = 1; g < groups.Count; g++)
            angles[g] = Angle(groups.Joints[g - 1], seconds);

        Transforms(groups, angles, rotations, translations);
    }

    /// <summary>Each group's transform with joint group g at angle <paramref name="angles"/>[g] (radians).</summary>
    public static void Transforms(JointGroups groups, ReadOnlySpan<double> angles, double[] rotations, Vector3d[] translations)
    {
        Span<bool> done = stackalloc bool[groups.Count];
        rotations.AsSpan(0, 9).Clear();
        (rotations[0], rotations[4], rotations[8]) = (1, 1, 1);
        translations[0] = default;
        done[0] = true;

        for (var g = 1; g < groups.Count; g++)
            Resolve(groups, g, angles, rotations, translations, done, 0);
    }

    private static void Resolve(JointGroups groups, int g, ReadOnlySpan<double> angles, double[] rotations, Vector3d[] translations, Span<bool> done, int depth)
    {
        if (done[g])
            return;

        var joint = groups.Joints[g - 1];
        var parent = depth > HullJointSet.MaxJoints ? 0 : groups.GroupOfJoint(joint.Parent);
        Resolve(groups, parent, angles, rotations, translations, done, depth + 1);

        // Own turn about the pivot: R (p - c) + c; a slider's: p + axis d.
        Span<double> r = stackalloc double[9];
        Vector3d own;

        if (joint.Kind == JointKind.Slider)
        {
            AxisAngle(joint.Axis, 0, r);
            var axis = joint.Axis;
            own = new Vector3d(axis.X * angles[g], axis.Y * angles[g], axis.Z * angles[g]);
        }
        else
        {
            AxisAngle(joint.Axis, angles[g], r);
            var c = joint.Pivot;
            own = Sub(c, Apply(r, c));
        }

        // After the parent's: P.R (R p + own) + P.t.
        var pr = rotations.AsSpan(parent * 9, 9);
        var out9 = rotations.AsSpan(g * 9, 9);

        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
                out9[i * 3 + j] = pr[i * 3] * r[j] + pr[i * 3 + 1] * r[3 + j] + pr[i * 3 + 2] * r[6 + j];
        }

        var moved = Apply(pr, own);
        translations[g] = new Vector3d(moved.X + translations[parent].X, moved.Y + translations[parent].Y, moved.Z + translations[parent].Z);
        done[g] = true;
    }

    private static void AxisAngle(Vector3d a, double angle, Span<double> m)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
        double x = a.X, y = a.Y, z = a.Z;
        (m[0], m[1], m[2]) = (t * x * x + c, t * x * y - s * z, t * x * z + s * y);
        (m[3], m[4], m[5]) = (t * x * y + s * z, t * y * y + c, t * y * z - s * x);
        (m[6], m[7], m[8]) = (t * x * z - s * y, t * y * z + s * x, t * z * z + c);
    }

    public static Vector3d Apply(ReadOnlySpan<double> m, Vector3d p) =>
        new(m[0] * p.X + m[1] * p.Y + m[2] * p.Z, m[3] * p.X + m[4] * p.Y + m[5] * p.Z, m[6] * p.X + m[7] * p.Y + m[8] * p.Z);

    private static Vector3d Sub(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}
