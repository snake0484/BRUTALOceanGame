using System.Numerics;

namespace BrutalGame.Physics;

public readonly struct RayHit
{
    public readonly bool Hit;
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly float Distance;
    public readonly BodyHandle Body;

    internal RayHit(Vector3 point, Vector3 normal, float distance, BodyHandle body)
    {
        Hit = true;
        Point = point;
        Normal = normal;
        Distance = distance;
        Body = body;
    }
}
