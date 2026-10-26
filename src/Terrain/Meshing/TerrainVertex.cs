using System.Numerics;
using System.Runtime.InteropServices;

namespace BrutalGame.Terrain;

[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex
{
    public const int SizeInBytes = 28;

    public Vector3 Position;
    public Vector3 Normal;
    public uint Material;
}
