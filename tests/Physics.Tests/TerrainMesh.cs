using System.Numerics;

namespace BrutalGame.Physics.Tests;

internal static class TerrainMesh
{
    /// <summary>Wavy height-field grid of <paramref name="quads"/>² quads (2 triangles each), CCW from above.</summary>
    public static (Vector3[] Vertices, uint[] Indices) Grid(int quads, float spacing, float phase)
    {
        int row = quads + 1;
        var vertices = new Vector3[row * row];
        var indices = new uint[quads * quads * 6];

        for (int z = 0; z < row; z++)
        {
            for (int x = 0; x < row; x++)
            {
                float h = MathF.Sin(x * 0.3f + phase) * MathF.Cos(z * 0.25f + phase) * 2f;
                vertices[z * row + x] = new Vector3(x * spacing, h, z * spacing);
            }
        }

        int n = 0;

        for (int z = 0; z < quads; z++)
        {
            for (int x = 0; x < quads; x++)
            {
                uint i0 = (uint)(z * row + x);
                uint i1 = i0 + 1;
                uint i2 = i0 + (uint)row;
                uint i3 = i2 + 1;
                indices[n++] = i0;
                indices[n++] = i2;
                indices[n++] = i3;
                indices[n++] = i0;
                indices[n++] = i3;
                indices[n++] = i1;
            }
        }

        return (vertices, indices);
    }
}
