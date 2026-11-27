using Brutal.ImGuiApi;
using BrutalGame.Ships;
using BrutalGame.Ships.Aero;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The ship in the air (D10: lift from shape): flying forward (+Z) at a speed, air density, angle of attack and
/// sideslip, its lift, drag and side force, the moments about its centre of mass, its stall and the speed it needs to
/// hold 1 g, its neutral point (behind the centre of mass is stable), and what each hinge's moving part (a control
/// surface) can do from one limit to the other. Each moving part's table turns with its pose, so playing the joints
/// shows the loads change. Lift, drag and weight are drawn from the centre of mass, with the neutral point.
/// </summary>
public sealed class AeroPanel
{
    private const double Gravity = 9.81;
    private const double ArrowMetres = 8.0;
    private const int CurvePoints = 61;

    private static readonly uint LiftColour = OverlayRenderer.Rgba(90, 220, 120, 230);
    private static readonly uint DragColour = OverlayRenderer.Rgba(240, 90, 80, 230);
    private static readonly uint WeightColour = OverlayRenderer.Rgba(200, 200, 200, 200);
    private static readonly uint MassColour = OverlayRenderer.Rgba(255, 255, 255, 230);
    private static readonly uint NeutralColour = OverlayRenderer.Rgba(90, 200, 255, 230);
    private static readonly Brutal.Numerics.float2 GraphSize = new(0f, 60f);

    private readonly float[] _liftCurve = new float[CurvePoints];
    private readonly float[] _dragCurve = new float[CurvePoints];
    private readonly float[] _pitchCurve = new float[CurvePoints];
    private readonly double[] _rotations = new double[HullJointSet.MaxJoints * 9 + 9];
    private readonly Vector3d[] _translations = new Vector3d[HullJointSet.MaxJoints + 1];
    private readonly double[] _angles = new double[HullJointSet.MaxJoints + 1];

    private float _speed = 100f;
    private float _density = 1.225f;
    private float _alpha = 5f;
    private float _beta;
    private bool _arrows = true;
    private bool _visible;

    // This frame's result, for the view.
    private bool _valid;
    private Vector3d _centre, _lift, _drag, _weight, _neutral;

    public void Draw(JointGroups groups, ReadOnlySpan<AeroTable?> tables, double[] rotations, Vector3d[] translations, ReadOnlySpan<double> angles, in HullMass mass, bool baking)
    {
        ImGui.SetNextWindowDockID(new ImGuiID(ShipLayout.EditDock), ImGuiCond.FirstUseEver);
        _visible = false;
        _valid = false;

        if (ImGui.Begin("Aero"u8))
        {
            _visible = true;
            DrawBody(groups, tables, rotations, translations, angles, mass, baking);
        }

        ImGui.End();
    }

    private void DrawBody(JointGroups groups, ReadOnlySpan<AeroTable?> tables, double[] rotations, Vector3d[] translations, ReadOnlySpan<double> angles, in HullMass mass, bool baking)
    {
        ImGui.TextDisabled("Every exposed face lifts and drags by its angle to the air; hinge and rotor parts are the control surfaces."u8);
        ImGui.SeparatorText("Air"u8);
        ImGui.SliderFloat("Speed"u8, ref _speed, 1f, 400f, "%.0f m/s"u8, ImGuiSliderFlags.None);
        ImGui.InputFloat("Density"u8, ref _density, 0.1f, 0.5f, "%.3f kg/m3"u8, ImGuiInputTextFlags.None);
        _density = Math.Clamp(_density, 0f, 100f);
        ImGui.SliderFloat("Angle of attack"u8, ref _alpha, -30f, 30f, "%.1f deg"u8, ImGuiSliderFlags.None);
        ImGui.SliderFloat("Sideslip"u8, ref _beta, -30f, 30f, "%.1f deg"u8, ImGuiSliderFlags.None);
        ImGui.Checkbox("Show lift and drag in the view"u8, ref _arrows);

        var count = Math.Min(groups.Count, tables.Length);
        var ready = 0;
        var area = 0.0;
        var bakeMs = 0f;
        var samples = 0;

        for (var g = 0; g < count; g++)
        {
            if (tables[g] is { } t)
            {
                ready++;
                area += t.ExposedArea;
                bakeMs += t.BakeMs;
                samples += t.Samples;
            }
        }

        if (baking || ready < count)
            ImGui.TextDisabled($"Baking air loads ({ready} of {count} parts ready)...");

        if (ready == 0 || mass.MassKg <= 0)
            return;

        var weight = mass.MassKg * Gravity;
        var com = mass.CentreOfMass;
        var parts = tables[..count];
        var q = 0.5 * _density * _speed * _speed;

        // At the chosen attitude.
        Loads(parts, rotations, translations, _alpha, _beta, com, out var lift, out var drag, out var side, out var moment, out var v, out var up);
        ImGui.SeparatorText("Loads"u8);
        ImGui.Text($"Lift {lift * q / 1000:F1} kN ({lift * q / weight:F2} g)   drag {drag * q / 1000:F1} kN   side {side * q / 1000:F1} kN");
        ImGui.Text($"Lift / drag {(Math.Abs(drag) > 1e-9 ? lift / drag : 0):F2}   exposed area {area:F1} m2");
        ImGui.TextDisabled($"Air tables: {ready} part{(ready == 1 ? "" : "s")}, {samples} surface samples, baked in {bakeMs:F0} ms");
        ImGui.Text($"About CoM: pitch up {-moment.X * q / 1000:F1}, yaw to +X {moment.Y * q / 1000:F1}, roll +X up {moment.Z * q / 1000:F1} kN m");

        // Curves over angle of attack (m², and m³ for pitch).
        double maxLift = 0, stallAlpha = 0, levelAlpha = double.NaN;

        for (var i = 0; i < CurvePoints; i++)
        {
            var a = -30 + i;
            Loads(parts, rotations, translations, a, _beta, com, out var l, out var d, out _, out var m, out _, out _);
            (_liftCurve[i], _dragCurve[i], _pitchCurve[i]) = ((float)l, (float)d, (float)-m.X);

            if (a >= 0 && l > maxLift)
                (maxLift, stallAlpha) = (l, a);

            if (a >= -10 && double.IsNaN(levelAlpha) && l * q >= weight)
                levelAlpha = a;
        }

        ImGui.SeparatorText("Over angle of attack, -30 to 30 deg"u8);
        ImGui.PlotLines("Lift m2"u8, _liftCurve, 0, default, float.MaxValue, float.MaxValue, GraphSize);
        ImGui.PlotLines("Drag m2"u8, _dragCurve, 0, default, float.MaxValue, float.MaxValue, GraphSize);
        ImGui.PlotLines("Pitch up m3"u8, _pitchCurve, 0, default, float.MaxValue, float.MaxValue, GraphSize);

        ImGui.SeparatorText("Flight"u8);

        if (maxLift > 1e-6 && _density > 0)
            ImGui.Text($"Most lift at {stallAlpha:F0} deg; stall speed for 1 g {Math.Sqrt(2 * weight / (_density * maxLift)):F0} m/s");
        else
            ImGui.Text("No lift: it cannot hold itself up in this air.");

        ImGui.Text(double.IsNaN(levelAlpha) ? "Cannot hold 1 g at this speed." : $"Holds 1 g at this speed from {levelAlpha:F0} deg");

        // Neutral point: where extra lift from a small change in attitude acts.
        Loads(parts, rotations, translations, _alpha - 1, _beta, com, out var l0, out _, out _, out var m0, out _, out _);
        Loads(parts, rotations, translations, _alpha + 1, _beta, com, out var l1, out _, out _, out var m1, out _, out _);
        _neutral = com;

        if (Math.Abs(l1 - l0) > 1e-6)
        {
            var arm = (-m1.X + m0.X) / (l1 - l0);
            _neutral = new Vector3d(com.X, com.Y, com.Z + arm);
            ImGui.Text(arm < 0
                ? $"Neutral point {-arm:F2} m behind the CoM: stable in pitch"
                : $"Neutral point {arm:F2} m ahead of the CoM: unstable in pitch");
        }

        DrawAuthority(groups, parts, angles, com, q);

        _valid = true;
        _centre = com;
        var scale = ArrowMetres / Math.Max(weight, Math.Max(Math.Abs(lift), Math.Abs(drag)) * q);
        _lift = Scale(up, lift * q * scale);
        _drag = Scale(v, -drag * q * scale);
        _weight = new Vector3d(0, -weight * scale, 0);
    }

    /// <summary>What each hinge's part does, from its lower limit to its upper one, the others where they are.</summary>
    private void DrawAuthority(JointGroups groups, ReadOnlySpan<AeroTable?> parts, ReadOnlySpan<double> angles, Vector3d com, double q)
    {
        var any = false;

        for (var g = 1; g < groups.Count && g < _angles.Length; g++)
        {
            var joint = groups.Joints[g - 1];

            if (joint.Kind != JointKind.Hinge)
                continue;

            if (!any)
            {
                ImGui.SeparatorText("Control surfaces (lower limit to upper)"u8);
                any = true;
            }

            var low = MomentAt(groups, parts, angles, g, joint.Min / 10.0, com);
            var high = MomentAt(groups, parts, angles, g, joint.Max / 10.0, com);
            var d = Scale(Sub(high, low), q / 1000);
            ImGui.Text($"Hinge #{joint.Id}: pitch {-d.X:F1}, yaw {d.Y:F1}, roll {d.Z:F1} kN m");
        }
    }

    private Vector3d MomentAt(JointGroups groups, ReadOnlySpan<AeroTable?> parts, ReadOnlySpan<double> angles, int group, double degrees, Vector3d com)
    {
        var n = Math.Min(groups.Count, _angles.Length);
        angles[..Math.Min(angles.Length, n)].CopyTo(_angles);
        _angles[group] = degrees * Math.PI / 180;
        JointPose.Transforms(groups, _angles.AsSpan(0, n), _rotations, _translations);
        Loads(parts, _rotations, _translations, _alpha, _beta, com, out _, out _, out _, out var moment, out _, out _);
        return moment;
    }

    /// <summary>Loads over dynamic pressure flying forward at <paramref name="alpha"/>, <paramref name="beta"/> (degrees): lift, drag and side (m²), moment about the CoM (m³).</summary>
    private void Loads(ReadOnlySpan<AeroTable?> parts, ReadOnlySpan<double> rotations, ReadOnlySpan<Vector3d> translations, double alpha, double beta, Vector3d com,
        out double lift, out double drag, out double side, out Vector3d moment, out Vector3d velocity, out Vector3d up)
    {
        double a = alpha * Math.PI / 180, b = beta * Math.PI / 180;
        velocity = new Vector3d(Math.Cos(a) * Math.Sin(b), -Math.Sin(a), Math.Cos(a) * Math.Cos(b));
        HullAero.Total(parts, rotations, translations, Scale(velocity, -1), 2.0, out var force, out var origin);
        up = Normalise(Sub(new Vector3d(0, 1, 0), Scale(velocity, velocity.Y)), new Vector3d(0, 1, 0));
        lift = Dot(force, up);
        drag = -Dot(force, velocity);
        side = Dot(force, Cross(up, velocity));
        moment = Sub(origin, Cross(com, force));
    }

    /// <summary>Lift (green), drag (red) and weight (grey) from the centre of mass (white), the largest 8 m long; the neutral point (blue).</summary>
    public void DrawOverlay(OverlayRenderer overlay)
    {
        if (!_visible || !_arrows || !_valid)
            return;

        overlay.Cube(_centre, 0.2, MassColour);
        overlay.Cube(_neutral, 0.15, NeutralColour);
        Arrow(overlay, _centre, _lift, LiftColour);
        Arrow(overlay, _centre, _drag, DragColour);
        Arrow(overlay, _centre, _weight, WeightColour);
    }

    private static void Arrow(OverlayRenderer overlay, Vector3d from, Vector3d along, uint colour)
    {
        var length = along.Length();

        if (length < 0.05)
            return;

        if (length > 40)
            (along, length) = (Scale(along, 40 / length), 40);

        var neck = Add(from, Scale(along, 1 - Math.Min(0.3, 1.0 / length)));
        overlay.Ribbon(from, neck, 0.12, colour);
        overlay.Cone(neck, Add(from, along), 0.3, colour);
    }
}
