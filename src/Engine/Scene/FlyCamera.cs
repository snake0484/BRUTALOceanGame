using System.Numerics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using BrutalGame.Frames;

namespace BrutalGame.Engine.Scene;

/// <summary>
/// Free-fly debug camera with a double pose in a frame. Right mouse drag looks, WASD/EQ/Space moves. Up is radial from
/// the frame's origin (a body surface frame has the body's centre there). The view has no translation: renderers draw
/// at (origin − <see cref="Position"/>) computed in double. Infinite reverse-Z projection.
/// </summary>
public sealed class FlyCamera
{
    private const double MaxPitch = 89.0 * Math.PI / 180.0;
    private const double FastMultiplier = 8.0;
    private const double SlowMultiplier = 0.2;

    public FrameId Frame = FrameId.None;
    public double3 Position = new(0.0, 12.0, 0.0);

    /// <summary>Camera axes (right X, up Y, back Z) to frame axes.</summary>
    public doubleQuat Rotation = doubleQuat.Identity;

    public float FovY = 60f * MathF.PI / 180f;
    public float Near = 0.1f;
    public double MoveSpeed = 15.0;
    public double LookSensitivity = 0.0025;
    public bool InvertY;

    private bool _looking;
    private double2 _lastCursor;

    public double3 Forward => -(Rotation * double3.UnitZ);

    public double3 Right => Rotation * double3.UnitX;

    public double3 Up
    {
        get
        {
            var length = Position.Length();
            return length > 1e-9 ? Position / length : double3.UnitY;
        }
    }

    /// <summary>Unit direction from yaw (from +X toward +Z) and pitch (toward +Y) in the frame's axes.</summary>
    public static double3 Direction(double yaw, double pitch)
    {
        var cp = Math.Cos(pitch);
        return new double3(cp * Math.Cos(yaw), Math.Sin(pitch), cp * Math.Sin(yaw));
    }

    /// <summary>Points the camera along <paramref name="forward"/>, level about <see cref="Up"/>.</summary>
    public void LookAlong(double3 forward) => Rotation = CameraProjection.LookRotation(forward, Up, Right);

    /// <summary>Rotation only: world-relative (point − <see cref="Position"/>) to view space.</summary>
    public Matrix4x4 View => CameraProjection.View(Rotation);

    public Matrix4x4 Projection(float aspect) => CameraProjection.Projection(FovY, aspect, Near);

    /// <summary>Camera-relative view-projection: draws must be positioned at (origin − <see cref="Position"/>).</summary>
    public Matrix4x4 ViewProjection(float aspect) => View * Projection(aspect);

    public void Update(GlfwWindow window, double dt, bool uiWantsMouse, bool uiWantsKeyboard)
    {
        UpdateLook(window, uiWantsMouse);

        if (!uiWantsKeyboard)
            UpdateMove(window, dt);
    }

    private void UpdateLook(GlfwWindow window, bool uiWantsMouse)
    {
        var rightDown = window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press;

        if (!_looking)
        {
            if (!rightDown || uiWantsMouse)
                return;

            _looking = true;
            _lastCursor = window.CursorPos;
            window.SetInputMode(GlfwInputMode.Cursor, (int)GlfwCursorMode.Disabled);
            return;
        }

        if (!rightDown)
        {
            _looking = false;
            window.SetInputMode(GlfwInputMode.Cursor, (int)GlfwCursorMode.Normal);
            return;
        }

        var cursor = window.CursorPos;
        var dx = cursor.X - _lastCursor.X;
        var dy = cursor.Y - _lastCursor.Y;
        _lastCursor = cursor;

        if (dx == 0.0 && dy == 0.0)
            return;

        var up = Up;
        var forward = Forward;
        var sinPitch = Math.Clamp(double3.Dot(forward, up), -1.0, 1.0);
        var heading = forward - up * sinPitch;
        var length = heading.Length();
        heading = length > 1e-9 ? heading / length : double3.Cross(up, Right);
        heading = doubleQuat.CreateFromAxisAngle(up, -dx * LookSensitivity) * heading;

        var pitch = Math.Clamp(Math.Asin(sinPitch) - (InvertY ? -dy : dy) * LookSensitivity, -MaxPitch, MaxPitch);
        LookAlong(heading * Math.Cos(pitch) + up * Math.Sin(pitch));
    }

    private void UpdateMove(GlfwWindow window, double dt)
    {
        var move = double3.Zero;

        if (Down(window, GlfwKey.W)) move += Forward;
        if (Down(window, GlfwKey.S)) move -= Forward;
        if (Down(window, GlfwKey.D)) move += Right;
        if (Down(window, GlfwKey.A)) move -= Right;
        if (Down(window, GlfwKey.E) || Down(window, GlfwKey.Space)) move += Up;
        if (Down(window, GlfwKey.Q)) move -= Up;

        var length = move.Length();

        if (length < 1e-9)
            return;

        var speed = MoveSpeed;

        if (Down(window, GlfwKey.LeftShift))
            speed *= FastMultiplier;
        else if (Down(window, GlfwKey.LeftControl))
            speed *= SlowMultiplier;

        Position += move * (speed * dt / length);
    }

    private static bool Down(GlfwWindow window, GlfwKey key) => window.GetKey(key) != GlfwKeyAction.Release;
}
