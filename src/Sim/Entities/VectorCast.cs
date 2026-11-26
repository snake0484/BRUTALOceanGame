using Brutal.Numerics;

namespace BrutalGame.Sim.Entities;

public static class VectorCast
{
    public static float3 ToFloat(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    public static double3 ToDouble(float3 v) => new(v.X, v.Y, v.Z);
}
