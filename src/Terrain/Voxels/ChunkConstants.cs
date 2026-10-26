using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

public static class ChunkConstants
{
    public const int Size = 32;
    public const int Padded = Size + 2;
    public const int PaddedArea = Padded * Padded;
    public const int PaddedVolume = Padded * Padded * Padded;
    public const float VoxelSize = 1f;
    public const float DensityScale = 32f;
    public const int MinChunkY = -2;
    public const int MaxChunkY = 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Index(int x, int y, int z) => (x + 1) + (y + 1) * Padded + (z + 1) * PaddedArea;
}
