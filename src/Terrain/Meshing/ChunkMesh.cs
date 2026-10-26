namespace BrutalGame.Terrain;

public sealed class ChunkMesh(int vertexCapacity = 4096, int indexCapacity = 16384)
{
    public TerrainVertex[] Vertices = new TerrainVertex[vertexCapacity];
    public int VertexCount;
    public uint[] Indices = new uint[indexCapacity];
    public int IndexCount;

    public ReadOnlySpan<TerrainVertex> VertexSpan => Vertices.AsSpan(0, VertexCount);
    public ReadOnlySpan<uint> IndexSpan => Indices.AsSpan(0, IndexCount);
    public bool IsEmpty => IndexCount == 0;

    public void Clear()
    {
        VertexCount = 0;
        IndexCount = 0;
    }

    internal void EnsureCapacity(int vertices, int indices)
    {
        if (vertices > Vertices.Length)
            Array.Resize(ref Vertices, Math.Max(vertices, Vertices.Length * 2));

        if (indices > Indices.Length)
            Array.Resize(ref Indices, Math.Max(indices, Indices.Length * 2));
    }
}
