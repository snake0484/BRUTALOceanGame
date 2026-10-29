using BrutalGame.Terrain.Water;

namespace BrutalGame.Terrain.Tests;

/// <summary>The voxel water surface mesh (docs/voxel-water.md "Water surface rendering").</summary>
public sealed class WaterSurfaceMesherTests
{
    private const int S = ChunkConstants.Size;
    private const byte Solid = (byte)WaterFlags.Solid;

    /// <summary>
    /// A pool across x 3..9 standing 0.6 m into cell y 5: a bank rising over the water at x 10 and up, the ground falling
    /// to y 2 at x 2 and under. The surface carries one column over the bank (its terrain hides the rest) and stops at
    /// the drop, where a skirt would hang in the air.
    /// </summary>
    [Fact]
    public void SurfaceSkirtsTheBankButNotTheDrop()
    {
        var block = new WaterBlock(pipes: true);

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                var ground = x <= 2 ? 2 : x >= 10 ? 6 : 4;
                for (var y = 0; y <= ground; y++)
                    block.Flags[ChunkConstants.Index(x, y, z)] = Solid;

                if (x is >= 3 and <= 9)
                    block.Water[ChunkConstants.Index(x, 5, z)] = 0.6f;
            }
        }

        block.DeriveBeds();
        var mesh = new WaterMesh();
        new WaterSurfaceMesher().Build(block, mesh);

        Assert.Equal((7 + 1) * S * 6, mesh.IndexCount);

        var xs = mesh.VertexSpan.ToArray().Select(v => v.Position.X).Distinct().Order().ToArray();
        Assert.Equal(2.5f, xs[0]);
        Assert.Equal(10.5f, xs[^1]);

        foreach (var v in mesh.VertexSpan)
        {
            Assert.Equal(5.1f, v.Position.Y, 3);
            if (v.Position.X == 10.5f)
                Assert.Equal(0f, v.Thickness);
        }
    }
}
