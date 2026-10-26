using System.Numerics;
using Brutal.GlfwApi;
using Brutal.Numerics;

namespace BrutalGame.Engine.Scene;

/// <summary>
/// Free-fly debug camera. Right mouse drag looks, WASD/EQ/Space moves. Infinite reverse-Z projection.
/// </summary>
public sealed class FlyCamera
{
    private const float MaxPitch = 89f * MathF.PI / 180f;
    private const float FastMultiplier = 8f;
    private const float SlowMultiplier = 0.2f;

    public Vector3 Position = new(0f, 12f, 0f);
    public float Yaw;
    public float Pitch = -0.15f;
    public float FovY = 60f * MathF.PI / 180f;
    public float Near = 0.1f;
    public float MoveSpeed = 15f;
    public float LookSensitivity = 0.0025f;

    private bool _looking;
    private double2 _lastCursor;

    public Vector3 Forward
    {
        get
        {
            var cp = MathF.Cos(Pitch);
            return new Vector3(cp * MathF.Cos(Yaw), MathF.Sin(Pitch), cp * MathF.Sin(Yaw));
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4x4 Projection(float aspect)
    {
        var f = 1f / MathF.Tan(FovY * 0.5f);

        return new Matrix4x4(
            f / aspect, 0f, 0f, 0f,
            0f, -f, 0f, 0f,
            0f, 0f, 0f, -1f,
            0f, 0f, Near, 0f);
    }

    public Matrix4x4 ViewProjection(float aspect) => View * Projection(aspect);

    public void Update(GlfwWindow window, float dt, bool uiWantsMouse, bool uiWantsKeyboard)
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
        var dx = (float)(cursor.X - _lastCursor.X);
        var dy = (float)(cursor.Y - _lastCursor.Y);
        _lastCursor = cursor;

        Yaw += dx * LookSensitivity;
        Pitch = Math.Clamp(Pitch - dy * LookSensitivity, -MaxPitch, MaxPitch);
    }

    private void UpdateMove(GlfwWindow window, float dt)
    {
        var forward = Forward;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var move = Vector3.Zero;

        if (Down(window, GlfwKey.W)) move += forward;
        if (Down(window, GlfwKey.S)) move -= forward;
        if (Down(window, GlfwKey.D)) move += right;
        if (Down(window, GlfwKey.A)) move -= right;
        if (Down(window, GlfwKey.E) || Down(window, GlfwKey.Space)) move += Vector3.UnitY;
        if (Down(window, GlfwKey.Q)) move -= Vector3.UnitY;

        if (move == Vector3.Zero)
            return;

        var speed = MoveSpeed;

        if (Down(window, GlfwKey.LeftShift))
            speed *= FastMultiplier;
        else if (Down(window, GlfwKey.LeftControl))
            speed *= SlowMultiplier;

        Position += Vector3.Normalize(move) * (speed * dt);
    }

    private static bool Down(GlfwWindow window, GlfwKey key) => window.GetKey(key) != GlfwKeyAction.Release;
}
