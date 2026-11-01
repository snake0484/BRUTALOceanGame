using System.Numerics;
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
    /// the drop, where a skirt would hang in the air, its edge sunk under its bed. The halo rows hold the same pool, as a
    /// neighbouring block's would.
    /// </summary>
    [Fact]
    public void SurfaceSkirtsTheBankButNotTheDrop()
    {
        var block = new WaterBlock(pipes: true);

        for (var z = -1; z <= S; z++)
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
            // The drop's edge: 0.6 m of water over its bed at 4.5, sunk to just under it.
            if (v.Position.X == 2.5f)
            {
                Assert.Equal(4.5f - WaterSurfaceMesher.WaterlineDip, v.Position.Y, 3);
                Assert.Equal(0f, v.Thickness);
                continue;
            }

            Assert.Equal(5.1f, v.Position.Y, 3);
            if (v.Position.X == 10.5f)
                Assert.Equal(0f, v.Thickness);
        }
    }

    /// <summary>
    /// A film 0.1 m deep over flat ground at x 0..4 runs into column 5 as its fill grows from none, past
    /// <see cref="WaterSurfaceMesher.WetThreshold"/>, to the film's depth. The front (the waterline's furthest x) is a line
    /// across the flow as far into the column as the column is full against the film behind it, never the voxel a column
    /// drawn whole once wet jumped.
    /// </summary>
    [Fact]
    public void FrontGlidesAsAColumnFills()
    {
        var fills = new[] { 0f, 0.005f, 0.01f, 0.015f, 0.019f, 0.021f, 0.025f, 0.03f, 0.04f, 0.05f, 0.07f, 0.1f };
        var mesher = new WaterSurfaceMesher();
        var mesh = new WaterMesh();
        var previous = float.NegativeInfinity;

        foreach (var fill in fills)
        {
            var block = new WaterBlock(pipes: true);

            for (var z = -1; z <= S; z++)
            {
                for (var x = -1; x <= S; x++)
                {
                    // The halo starts solid (no neighbour); here it is the same open ground a neighbour would hold.
                    for (var y = 0; y < S; y++)
                        block.Flags[ChunkConstants.Index(x, y, z)] = y <= 4 ? Solid : (byte)0;

                    if (x is >= 0 and <= 4)
                        block.Water[ChunkConstants.Index(x, 5, z)] = 0.1f;
                }

                block.Water[ChunkConstants.Index(5, 5, z)] = fill;
            }

            mesher.Build(block, mesh);
            var front = mesh.VertexSpan.ToArray().Max(v => v.Position.X);

            Assert.True(front > previous, $"front at {front} for fill {fill}, was {previous}");
            Assert.Equal(4.5f + fill / 0.1f, front, 0.01f);
            previous = front;
        }
    }

    /// <summary>
    /// A stream 8 m wide crossing a block's height: 0.3 m deep on the block above's bottom layer (world y 32) over x 0..9,
    /// and over x 10..20, a voxel stair down, in the block below's top layer (world y 31). Each block meshed with the
    /// other in its halo, as the system meshes them, covers the stream between them, and the two surfaces meet at the
    /// stair (x 9.5) at one height. Each took the other's water for none: the block above dipped its waterline to its bed
    /// there and the one below skirted under it at its own level, a step with no wall that the bed showed through, a dry
    /// zig-zag across the river.
    /// </summary>
    [Fact]
    public void StreamCrossingABlockHeightIsCoveredAcrossIt()
    {
        var lower = new WaterBlock(pipes: true);
        var upper = new WaterBlock(pipes: true);
        lower.Reset(new ChunkCoord(0, 0, 0));
        upper.Reset(new ChunkCoord(0, 1, 0));

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                var channel = z is >= 8 and <= 15 && x <= 20;
                var ground = !channel ? 40 : x <= 9 ? 31 : 30;

                for (var y = 0; y < S; y++)
                {
                    lower.Flags[ChunkConstants.Index(x, y, z)] = y <= ground ? Solid : (byte)0;
                    upper.Flags[ChunkConstants.Index(x, y, z)] = 32 + y <= ground ? Solid : (byte)0;
                }

                if (channel && x <= 9)
                    upper.Water[ChunkConstants.Index(x, 0, z)] = 0.3f;
                else if (channel)
                    lower.Water[ChunkConstants.Index(x, 31, z)] = 0.3f;
            }
        }

        WaterHalo.CopyFlags(lower, upper, Face.PosY);
        WaterHalo.CopyWater(lower, upper, Face.PosY);
        WaterHalo.CopyFlags(upper, lower, Face.NegY);
        WaterHalo.CopyWater(upper, lower, Face.NegY);

        var mesher = new WaterSurfaceMesher();
        var meshes = new[] { new WaterMesh(), new WaterMesh() };
        mesher.Build(lower, meshes[0]);
        mesher.Build(upper, meshes[1]);
        var holes = new List<Vector2>();

        for (var z = 9.25f; z <= 14.75f; z += 0.5f)
            for (var x = 1.25f; x <= 18.75f; x += 0.5f)
                if (!meshes.Any(m => Covers(m, new Vector2(x, z))))
                    holes.Add(new Vector2(x, z));

        Assert.True(holes.Count == 0, $"{holes.Count} points of the stream uncovered, at {string.Join(" ", holes.Take(6))}");

        for (var z = 9; z <= 14; z++)
        {
            var below = Heights(meshes[0], 9.5f, z + 0.5f, 0f);
            var above = Heights(meshes[1], 9.5f, z + 0.5f, 32f);
            Assert.True(below.Length > 0 && above.Length > 0, $"both surfaces should reach the stair at z {z + 0.5f}");
            Assert.True(MathF.Abs(below.Max() - above.Max()) < 0.02f, $"the surfaces meet at the stair at z {z + 0.5f} at {below.Max():F2} below and {above.Max():F2} above");
        }
    }

    /// <summary>World heights of a mesh's vertices at (x, z), its block's origin <paramref name="originY"/> up.</summary>
    private static float[] Heights(WaterMesh mesh, float x, float z, float originY) =>
        mesh.VertexSpan.ToArray().Where(v => MathF.Abs(v.Position.X - x) < 0.01f && MathF.Abs(v.Position.Z - z) < 0.01f).Select(v => v.Position.Y + originY).ToArray();

    /// <summary>Some triangle of the mesh covers (x, z) in plan.</summary>
    private static bool Covers(WaterMesh mesh, Vector2 at)
    {
        var v = mesh.VertexSpan;

        for (var t = 0; t + 2 < mesh.IndexCount; t += 3)
        {
            var a = new Vector2(v[(int)mesh.Indices[t]].Position.X, v[(int)mesh.Indices[t]].Position.Z);
            var b = new Vector2(v[(int)mesh.Indices[t + 1]].Position.X, v[(int)mesh.Indices[t + 1]].Position.Z);
            var c = new Vector2(v[(int)mesh.Indices[t + 2]].Position.X, v[(int)mesh.Indices[t + 2]].Position.Z);
            var d1 = (b.X - a.X) * (at.Y - a.Y) - (b.Y - a.Y) * (at.X - a.X);
            var d2 = (c.X - b.X) * (at.Y - b.Y) - (c.Y - b.Y) * (at.X - b.X);
            var d3 = (a.X - c.X) * (at.Y - c.Y) - (a.Y - c.Y) * (at.X - c.X);

            if (!((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A finger of water 1.5 cm deep in two columns (x 5, z 10 and 11) ahead of the same 0.1 m film's front at x 4.5. It
    /// reaches as far into them as they are full against the film (about 0.15), a short stub across the flow, not the
    /// sliver a column long that averaging its depth into the corners drew. The front elsewhere stays at x 4.5.
    /// </summary>
    [Fact]
    public void ThinFingerIsAStubNotATongue()
    {
        var block = new WaterBlock(pipes: true);

        for (var z = -1; z <= S; z++)
        {
            for (var x = -1; x <= S; x++)
            {
                for (var y = 0; y < S; y++)
                    block.Flags[ChunkConstants.Index(x, y, z)] = y <= 4 ? Solid : (byte)0;

                if (x is >= 0 and <= 4)
                    block.Water[ChunkConstants.Index(x, 5, z)] = 0.1f;
            }
        }

        block.Water[ChunkConstants.Index(5, 5, 10)] = 0.015f;
        block.Water[ChunkConstants.Index(5, 5, 11)] = 0.015f;

        var mesh = new WaterMesh();
        new WaterSurfaceMesher().Build(block, mesh);

        var finger = 0f;

        foreach (var v in mesh.VertexSpan)
        {
            var p = v.Position;

            if (p.Z is >= 9.5f and <= 11.5f)
                finger = MathF.Max(finger, p.X);
            else
                Assert.True(p.X < 4.5f + 0.1f, $"edge at {p} beside the finger");
        }

        Assert.InRange(finger, 4.6f, 4.75f);
    }

    /// <summary>
    /// A lake's shore: held cells half full in y 5 (level 5.0), on beds from 0.4 m under the floor to 0.4 m over it.
    /// Held fill is the level over the floor, so every bed draws at the level, not stood on its bed above or below it.
    /// The halos hold the same lake, as neighbouring blocks would.
    /// </summary>
    [Fact]
    public void HeldShoreDrawsAtItsLevelOverAnyBed()
    {
        var block = new WaterBlock(pipes: true);

        for (var z = -1; z <= S; z++)
        {
            for (var x = -1; x <= S; x++)
            {
                for (var y = 0; y <= 4; y++)
                    block.Flags[ChunkConstants.Index(x, y, z)] = Solid;

                var i = ChunkConstants.Index(x, 5, z);
                block.Water[i] = 0.5f;
                block.Flags[i] = (byte)WaterFlags.Reservoir;
                block.Body[i] = WaterBodies.Sea + 1;
            }
        }

        block.DeriveBeds();

        for (var z = 0; z < S; z++)
            for (var x = 0; x < S; x++)
                block.Bed[ChunkConstants.Index(x, 5, z)] = (sbyte)((x % 3 - 1) * 102);

        var mesh = new WaterMesh();
        new WaterSurfaceMesher().Build(block, mesh);

        Assert.Equal(S * S * 6, mesh.IndexCount);

        foreach (var v in mesh.VertexSpan)
            Assert.Equal(5f, v.Position.Y, 3);
    }
}
