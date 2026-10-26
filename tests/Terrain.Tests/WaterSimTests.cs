using System.Diagnostics;
using System.Numerics;
using BrutalGame.Terrain.Water;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

public sealed class WaterSimTests(ITestOutputHelper output)
{
    private const int S = ChunkConstants.Size;

    private static readonly (int X, int Y, int Z)[] Offsets = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];

    /// <summary>Minimal orchestrator: same per-step order as the runtime system.</summary>
    private sealed class Sim
    {
        public readonly Dictionary<ChunkCoord, WaterBlock> Blocks = new();

        public WaterBlock Add(ChunkCoord coord)
        {
            var block = new WaterBlock();
            block.Reset(coord);
            Blocks.Add(coord, block);
            return block;
        }

        public void LinkFlags()
        {
            foreach (var block in Blocks.Values)
                for (var f = 0; f < 6; f++)
                    WaterHalo.CopyFlags(block, Neighbour(block, f), (Face)f);
        }

        public void Step(int steps = 1)
        {
            for (var s = 0; s < steps; s++)
            {
                foreach (var block in Blocks.Values)
                    for (var f = 0; f < 6; f++)
                        WaterHalo.CopyWater(block, Neighbour(block, f), (Face)f);

                foreach (var block in Blocks.Values)
                    WaterFlow.ComputeOutflowScale(block);

                foreach (var block in Blocks.Values)
                    for (var f = 0; f < 6; f++)
                        WaterHalo.CopyOutScale(block, Neighbour(block, f), (Face)f);

                foreach (var block in Blocks.Values)
                    WaterFlow.ApplyFlows(block);

                foreach (var block in Blocks.Values)
                    WaterFlow.Swap(block);
            }
        }

        public double Mass()
        {
            var sum = 0.0;

            foreach (var block in Blocks.Values)
                sum += block.Mass();

            return sum;
        }

        public float MaxDelta()
        {
            var max = 0f;

            foreach (var block in Blocks.Values)
                max = MathF.Max(max, block.MaxDelta);

            return max;
        }

        private WaterBlock? Neighbour(WaterBlock block, int face)
        {
            var (dx, dy, dz) = Offsets[face];
            var c = block.Coord;
            return Blocks.GetValueOrDefault(new ChunkCoord(c.X + dx, c.Y + dy, c.Z + dz));
        }
    }

    private static int I(int x, int y, int z) => ChunkConstants.Index(x, y, z);

    private static void Solid(WaterBlock b, int x, int y, int z)
    {
        b.Flags[I(x, y, z)] = (byte)WaterFlags.Solid;
        b.Water[I(x, y, z)] = 0f;
    }

    private static void FillSolid(WaterBlock b, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    Solid(b, x, y, z);
    }

    private static void FillWater(WaterBlock b, int x0, int y0, int z0, int x1, int y1, int z1, float w = 1f)
    {
        for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    b.Water[I(x, y, z)] = w;
    }

    /// <summary>Solid everywhere except the box [x0..x1]×[y0..y1]×[z0..z1].</summary>
    private static void CarveBox(WaterBlock b, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        FillSolid(b, 0, 0, 0, S - 1, S - 1, S - 1);

        for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    b.Flags[I(x, y, z)] = 0;
    }

    private static double Sum(WaterBlock b, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        var sum = 0.0;

        for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    sum += b.Water[I(x, y, z)];

        return sum;
    }

    private static int StepUntilSettled(Sim sim, int maxSteps)
    {
        var quiet = 0;

        for (var s = 1; s <= maxSteps; s++)
        {
            sim.Step();
            quiet = sim.MaxDelta() < 1e-4f ? quiet + 1 : 0;

            if (quiet >= 60)
                return s;
        }

        return -1;
    }

    [Fact]
    public void ColumnFallsOntoFloorAndSettles()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 0, 0));
        CarveBox(b, 12, 1, 12, 19, 30, 19);
        FillWater(b, 15, 20, 15, 16, 27, 16);
        var start = sim.Mass();

        sim.Step(10);
        Assert.True(Sum(b, 12, 1, 12, 19, 19, 19) > 0.5, "water should be falling");

        var steps = StepUntilSettled(sim, 4000);
        Assert.True(steps > 0, "pool should settle");
        Assert.Equal(start, sim.Mass(), 1e-3);
        Assert.True(Sum(b, 12, 2, 12, 19, 30, 19) < 1e-3, "all water rests in the floor layer");
        Assert.Equal(start / 64.0, b.Water[I(12, 1, 12)], 1e-3);
    }

    [Fact]
    public void ClosedBoxConservesMassExactly()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 0, 0));
        CarveBox(b, 4, 2, 4, 13, 21, 9);
        FillWater(b, 4, 2, 4, 7, 21, 9);
        FillWater(b, 8, 10, 4, 13, 12, 9, 0.6f);
        var start = sim.Mass();

        for (var s = 0; s < 500; s++)
        {
            sim.Step();
            Assert.Equal(start, sim.Mass(), 1e-3);
        }

        Assert.True(Sum(b, 10, 2, 4, 13, 21, 9) > 10, "water should have spread");
    }

    [Fact]
    public void WaterCrossesBlockBordersAndConservesMass()
    {
        var sim = new Sim();
        var a = sim.Add(new ChunkCoord(0, 0, 0));
        var b = sim.Add(new ChunkCoord(1, 0, 0));
        var c = sim.Add(new ChunkCoord(0, -1, 0));
        var d = sim.Add(new ChunkCoord(1, -1, 0));

        // Basin: floor at y = 20 in the lower pair, open above into the upper pair. Water starts high in A only.
        FillSolid(c, 0, 0, 0, S - 1, 20, S - 1);
        FillSolid(d, 0, 0, 0, S - 1, 20, S - 1);
        FillWater(a, 20, 0, 10, 31, 12, 21);
        sim.LinkFlags();
        var start = sim.Mass();

        var crossed = 0.0;

        for (var s = 0; s < 600; s++)
        {
            sim.Step();
            crossed = Math.Max(crossed, b.Mass());
            Assert.Equal(start, sim.Mass(), 1e-3);
        }

        Assert.True(crossed > 1, "water should cross the X face");
        Assert.True(c.Mass() > 1, "water should fall through the Y face");
        Assert.True(d.Mass() > 1, "water should reach the diagonal block");
    }

    [Fact]
    public void SeamStepMatchesSingleBlockBitForBit()
    {
        // A 22-wide basin split across an X seam (A x 21..31 + B x 0..10) vs the same basin inside one block (x 0..21).
        var split = new Sim();
        var a = split.Add(new ChunkCoord(0, 0, 0));
        var b = split.Add(new ChunkCoord(1, 0, 0));
        FillSolid(a, 0, 0, 0, 20, S - 1, S - 1);
        FillSolid(b, 11, 0, 0, S - 1, S - 1, S - 1);
        FillWater(a, 24, 0, 8, 31, 14, 23);
        FillWater(b, 0, 6, 8, 2, 9, 12, 0.7f);
        split.LinkFlags();

        var whole = new Sim();
        var w = whole.Add(new ChunkCoord(5, 5, 5));
        FillSolid(w, 22, 0, 0, S - 1, S - 1, S - 1);
        FillWater(w, 3, 0, 8, 10, 14, 23);
        FillWater(w, 11, 6, 8, 13, 9, 12, 0.7f);

        for (var s = 0; s < 300; s++)
        {
            split.Step();
            whole.Step();
        }

        for (var z = 0; z < S; z++)
        {
            for (var y = 0; y < S; y++)
            {
                for (var x = 0; x < 11; x++)
                {
                    Assert.Equal(w.Water[I(x, y, z)], a.Water[I(x + 21, y, z)]);
                    Assert.Equal(w.Water[I(x + 11, y, z)], b.Water[I(x, y, z)]);
                }
            }
        }

        Assert.True(Sum(b, 3, 0, 0, 10, 2, S - 1) > 1, "water should spread across the seam");
    }

    [Fact]
    public void ReservoirFillsPitGradually()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, -1, 0));
        b.ReservoirTopY = -32 + 8;
        CarveBox(b, 2, 1, 10, 25, 8, 10);

        for (var y = 1; y <= 8; y++)
            for (var x = 2; x <= 5; x++)
            {
                b.Flags[I(x, y, 10)] = (byte)WaterFlags.Reservoir;
                b.Water[I(x, y, 10)] = b.ReservoirFill(y);
            }

        Assert.Equal(1f, b.ReservoirFill(8));
        Assert.Equal(1f + 7 * WaterFlow.Compression, b.ReservoirFill(1), 1e-6);

        sim.Step();
        Assert.True(b.Water[I(25, 1, 10)] < 1e-3, "far cell must not fill instantly");
        Assert.True(b.Water[I(6, 1, 10)] > 0f, "neighbour of the reservoir starts filling");

        sim.Step(100);
        Assert.True(b.Water[I(25, 8, 10)] < 1e-3, "pit fills over time, not instantly");

        sim.Step(7900);
        var pit = Sum(b, 6, 1, 10, 25, 8, 10) / (20 * 8);
        Assert.True(pit > 0.95, $"pit should reach the reservoir level, mean fill {pit}");
        Assert.True(b.Water[I(25, 8, 10)] > 0.8f, "top layer at the far end levels with the sea");
    }

    [Fact]
    public void CommunicatingVesselsEqualise()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 0, 0));
        FillSolid(b, 0, 0, 0, S - 1, S - 1, S - 1);

        void Open(int x0, int y0, int x1, int y1)
        {
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    b.Flags[I(x, y, 5)] = 0;
        }

        Open(2, 1, 2, 28);
        Open(2, 1, 12, 1);
        Open(12, 1, 12, 28);
        FillWater(b, 2, 1, 5, 2, 22, 5);
        var start = sim.Mass();

        sim.Step(15000);
        var left = Sum(b, 2, 2, 5, 2, 28, 5);
        var right = Sum(b, 12, 2, 5, 12, 28, 5);
        output.WriteLine($"left leg {left:F3}, right leg {right:F3}");

        Assert.Equal(start, sim.Mass(), 1e-3);
        Assert.True(right > 3, "water should rise in the right leg");
        Assert.True(Math.Abs(left - right) < 0.1, $"legs should level: {left} vs {right}");
    }

    [Fact]
    public void InitFromMarksSeaAsReservoirAndDugSeabedAsDry()
    {
        var p = new TerrainParams();
        var (centre, coord) = FindSeabedDig(p);
        var chunk = new VoxelChunk { Coord = coord };
        TerrainGenerator.Generate(chunk, p);
        VoxelEditor.Apply(chunk, [new BrushOp(centre, 2.5f, BrushMode.Dig, VoxelMaterial.Air)]);

        var block = new WaterBlock();
        block.InitFrom(chunk);
        var reservoirs = 0;

        for (var z = 0; z < S; z++)
        {
            for (var y = 0; y < S; y++)
            {
                for (var x = 0; x < S; x++)
                {
                    var i = I(x, y, z);
                    var flags = (WaterFlags)block.Flags[i];
                    Assert.NotEqual(WaterFlags.Solid | WaterFlags.Reservoir, flags);
                    Assert.Equal(chunk.Density[i] < 0, flags.HasFlag(WaterFlags.Solid));
                    Assert.Equal(flags.HasFlag(WaterFlags.Reservoir) ? block.ReservoirFill(y) : 0f, block.Water[i]);

                    if (flags.HasFlag(WaterFlags.Reservoir))
                        reservoirs++;
                }
            }
        }

        Assert.True(reservoirs > 1000);
        var local = centre - coord.WorldOrigin;
        var dug = I((int)local.X, (int)local.Y, (int)local.Z);
        Assert.Equal(0, block.Flags[dug]);
        Assert.Equal(0f, block.Water[dug]);
        Assert.True(block.FaceOpen(Face.PosY) || block.FaceOpen(Face.NegX), "open sea leaves through the faces");
    }

    private static (Vector3 Centre, ChunkCoord Coord) FindSeabedDig(in TerrainParams p)
    {
        for (var cx = 4; cx < 64; cx++)
        {
            var x = cx * 8f + 0.5f;
            var z = 16.5f;
            var h = TerrainGenerator.SurfaceHeight(x, z, p);

            for (var depth = 4; depth < 16; depth++)
            {
                var centre = new Vector3(MathF.Floor(x), MathF.Round(h) - depth, MathF.Floor(z));
                var coord = ChunkCoord.FromWorld(centre);
                var localY = centre.Y - coord.WorldOrigin.Y;

                if (h < -8f && coord.Y == -1 && localY is >= 4 and <= 26)
                    return (centre, coord);
            }
        }

        throw new InvalidOperationException("no seabed column found");
    }

    [Fact]
    public void RefreshKeepsSimWaterAndZeroesPlacedSolid()
    {
        var chunk = new VoxelChunk { Coord = new ChunkCoord(0, 1, 0) };
        chunk.Fill(127, VoxelMaterial.Air);
        var block = new WaterBlock();
        block.InitFrom(chunk);
        block.Water[I(5, 5, 5)] = 0.7f;
        block.Water[I(6, 5, 5)] = 0.4f;
        chunk.Density[I(6, 5, 5)] = -50;
        block.RefreshFrom(chunk);

        Assert.Equal(0.7f, block.Water[I(5, 5, 5)]);
        Assert.Equal(0f, block.Water[I(6, 5, 5)]);
        Assert.Equal((byte)WaterFlags.Solid, block.Flags[I(6, 5, 5)]);
    }

    [Fact]
    public void MesherMakesOneQuadPerWetColumnAndSkipsReservoir()
    {
        var b = new WaterBlock();
        FillSolid(b, 0, 0, 0, S - 1, 0, S - 1);
        FillWater(b, 4, 1, 4, 11, 1, 11, 0.5f);

        for (var x = 20; x < 24; x++)
        {
            b.Flags[I(x, 1, 20)] = (byte)WaterFlags.Reservoir;
            b.Water[I(x, 1, 20)] = 1f;
        }

        var mesher = new WaterSurfaceMesher();
        var mesh = new WaterMesh();
        mesher.Build(b, mesh);
        mesher.Build(b, mesh);

        Assert.Equal(64 * 6, mesh.IndexCount);
        Assert.Equal(81, mesh.VertexCount);

        foreach (var v in mesh.VertexSpan)
        {
            Assert.Equal(1f, v.Position.Y, 1e-5);
            Assert.Equal(0.5f, v.Thickness, 1e-5);
            Assert.Equal(Vector3.UnitY, v.Normal);
            Assert.InRange(v.Position.X, 3.5f, 11.5f);
        }

        var t = mesh.IndexSpan;
        var p0 = mesh.Vertices[t[0]].Position;
        var normal = Vector3.Cross(mesh.Vertices[t[1]].Position - p0, mesh.Vertices[t[2]].Position - p0);
        Assert.True(normal.Y > 0f, "quads wind counter-clockwise seen from above");

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 10; i++)
            mesher.Build(b, mesh);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void VertexLayoutMatchesGpu()
    {
        Assert.Equal(WaterVertex.SizeInBytes, System.Runtime.CompilerServices.Unsafe.SizeOf<WaterVertex>());
    }

    [Fact]
    public void StepPassesDoNotAllocate()
    {
        var b = DamBreak();
        WaterFlow.ComputeOutflowScale(b);
        WaterFlow.ApplyFlows(b);
        WaterFlow.Swap(b);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 20; i++)
        {
            for (var f = 0; f < 6; f++)
                WaterHalo.CopyWater(b, null, (Face)f);

            WaterFlow.ComputeOutflowScale(b);

            for (var f = 0; f < 6; f++)
                WaterHalo.CopyOutScale(b, null, (Face)f);

            WaterFlow.ApplyFlows(b);
            WaterFlow.Swap(b);
            _ = b.AnyWaterOnFace(Face.PosX);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void SimdAndScalarPathsMatchBitForBit()
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
            return;

        static WaterBlock Scene()
        {
            var b = DamBreak();
            b.Reset(new ChunkCoord(0, -1, 0));
            FillWater(b, 0, 0, 0, 15, S - 1, S - 1);
            FillSolid(b, 0, 0, 0, S - 1, 3, S - 1);
            FillSolid(b, 20, 4, 5, 22, 20, 25);

            for (var z = 0; z < S; z++)
                for (var x = 26; x < S; x++)
                {
                    b.Flags[I(x, 4, z)] = (byte)WaterFlags.Reservoir;
                    b.Water[I(x, 4, z)] = b.ReservoirFill(4);
                }

            return b;
        }

        var simd = Scene();
        var scalar = Scene();

        try
        {
            for (var s = 0; s < 200; s++)
            {
                WaterFlow.UseSimd = true;
                StepSingle(simd);
                WaterFlow.UseSimd = false;
                StepSingle(scalar);
                Assert.Equal(scalar.MaxDelta, simd.MaxDelta);
                Assert.Equal(scalar.BorderActive, simd.BorderActive);
            }
        }
        finally
        {
            WaterFlow.UseSimd = true;
        }

        Assert.True(simd.Water.AsSpan().SequenceEqual(scalar.Water));
        Assert.True(simd.OutScale.AsSpan().SequenceEqual(scalar.OutScale));
    }

    private static WaterBlock DamBreak()
    {
        var b = new WaterBlock();
        FillWater(b, 0, 0, 0, 15, S - 1, S - 1);
        return b;
    }

    private static WaterBlock Full()
    {
        var b = new WaterBlock();
        FillWater(b, 0, 0, 0, S - 1, S - 1, S - 1);
        return b;
    }

    [Fact]
    public void ReportStepTimings()
    {
#if DEBUG
        output.WriteLine("Debug build: timings not meaningful, run with -c Release.");
#endif
        // Let tiered JIT promote the kernels before measuring.
        var warm = Stopwatch.StartNew();
        var scratch = DamBreak();

        while (warm.ElapsedMilliseconds < 500)
            StepSingle(scratch);

        Time("empty", new WaterBlock(), 2000);
        Time("pool 1 layer", Pool(), 2000);
        Time("dam break (busy)", DamBreak(), 300);
        Time("full block", Full(), 300);
    }

    private static WaterBlock Pool()
    {
        var b = new WaterBlock();
        FillWater(b, 0, 0, 0, S - 1, 0, S - 1, 0.5f);
        return b;
    }

    private void Time(string name, WaterBlock block, int steps)
    {
        for (var i = 0; i < 300; i++)
            StepSingle(block);

        var pass1 = 0L;
        var pass2 = 0L;

        for (var i = 0; i < steps; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            WaterFlow.ComputeOutflowScale(block);
            var t1 = Stopwatch.GetTimestamp();
            WaterFlow.ApplyFlows(block);
            var t2 = Stopwatch.GetTimestamp();
            WaterFlow.Swap(block);
            pass1 += t1 - t0;
            pass2 += t2 - t1;
        }

        var toUs = 1e6 / Stopwatch.Frequency / steps;
        output.WriteLine($"{name}: pass1 {pass1 * toUs:F1} us, pass2 {pass2 * toUs:F1} us, total {(pass1 + pass2) * toUs:F1} us");
    }

    private static void StepSingle(WaterBlock b)
    {
        WaterFlow.ComputeOutflowScale(b);
        WaterFlow.ApplyFlows(b);
        WaterFlow.Swap(b);
    }
}
