using System.Numerics;
using Brutal.Numerics;

namespace BrutalGame.Frames;

/// <summary>
/// The camera's translation-free view and infinite reverse-Z projection: the float matrices every draw uses. Points are
/// camera-relative (point − camera, subtracted in double, then cast). Also the exact double projection and the float
/// probes --orbit-test compares against it.
/// </summary>
public static class CameraProjection
{
    /// <summary>Camera axes (right X, up Y, back Z) to frame axes, looking along <paramref name="forward"/>, level about <paramref name="up"/>.</summary>
    public static doubleQuat LookRotation(double3 forward, double3 up, double3 fallbackRight)
    {
        var back = -double3.Normalize(forward);
        var right = double3.Cross(up, back);
        var length = right.Length();
        right = length > 1e-9 ? right / length : fallbackRight;
        var cameraUp = double3.Cross(back, right);

        return doubleQuat.Normalize(doubleQuat.CreateFromRotationMatrix(new double4x4(
            right.X, right.Y, right.Z, 0.0,
            cameraUp.X, cameraUp.Y, cameraUp.Z, 0.0,
            back.X, back.Y, back.Z, 0.0,
            0.0, 0.0, 0.0, 1.0)));
    }

    /// <summary>Rotation only: camera-relative points in frame axes to view space. <paramref name="rotation"/> is camera axes to frame axes.</summary>
    public static Matrix4x4 View(doubleQuat rotation)
    {
        var q = doubleQuat.Conjugate(rotation);
        return Matrix4x4.CreateFromQuaternion(new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W));
    }

    /// <summary>Infinite reverse-Z: depth = near / distance along the view axis, 1 at the near plane, 0 at infinity.</summary>
    public static Matrix4x4 Projection(float fovY, float aspect, float near)
    {
        var f = 1f / MathF.Tan(fovY * 0.5f);

        return new Matrix4x4(
            f / aspect, 0f, 0f, 0f,
            0f, -f, 0f, 0f,
            0f, 0f, 0f, -1f,
            0f, 0f, near, 0f);
    }

    /// <summary>Pixel of a camera-relative point through the float matrices, as a vertex shader computes it, and its reverse-Z depth.</summary>
    public static Vector2 ProjectFloat(Vector3 relative, in Matrix4x4 viewProj, Vector2 viewport, out float depth)
    {
        var clip = Vector4.Transform(new Vector4(relative, 1f), viewProj);
        depth = clip.Z / clip.W;
        return new Vector2((clip.X / clip.W * 0.5f + 0.5f) * viewport.X, (clip.Y / clip.W * 0.5f + 0.5f) * viewport.Y);
    }

    /// <summary>The exact pixel of a camera-relative point, all in double.</summary>
    public static double2 ProjectExact(double3 relative, doubleQuat rotation, double fovY, double aspect, double2 viewport)
    {
        var view = doubleQuat.Conjugate(rotation) * relative;
        var f = 1.0 / Math.Tan(fovY * 0.5);
        var x = f / aspect * view.X / -view.Z;
        var y = f * view.Y / view.Z;
        return new double2((x * 0.5 + 0.5) * viewport.X, (y * 0.5 + 0.5) * viewport.Y);
    }

    /// <summary>
    /// Distance along unit <paramref name="direction"/> to a sphere the camera is outside of, in float as planet.frag
    /// computes it. <paramref name="toCentre"/> only gives the up direction; altitude h and c = h (2R + h) come from
    /// double, so the root t = c / (√(b² − c) − b) keeps full precision metres above a 1000 km surface. NaN on a miss.
    /// </summary>
    public static float SphereDistance(Vector3 toCentre, float radius, float altitude, float c, Vector3 direction)
    {
        var up = -Vector3.Normalize(toCentre);
        var b = (radius + altitude) * Vector3.Dot(up, direction);
        var disc = b * b - c;
        return b < 0f && disc >= 0f ? c / (MathF.Sqrt(disc) - b) : float.NaN;
    }
}
