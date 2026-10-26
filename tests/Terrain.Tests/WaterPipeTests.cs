using System.Diagnostics;
using BrutalGame.Terrain.Water;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

/// <summary>The 3D pipe model (docs/voxel-water.md "Flow simulation v2"): the automaton's guarantees, plus momentum.</summary>
public sealed class WaterPipeTests(ITestOutputHelper output)
{
    private const int S = ChunkConstants.Size;
    private const float Dt = 1f / 240f;

    private static readonly (int X, int Y, int Z)[] Offsets = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];

    /// <summary>Minimal orchestrator: same per-step order as the runtime system with --water-pipes.</summary>
    private sealed class Sim
    {
        public readonly Dictionary<ChunkCoord, WaterBlock> Blocks = new();

        public WaterBlock Add(ChunkCoord coord)
        {
            var block = new WaterBlock(pipes: true);
            block.Reset(coord);
            Blocks.Add(coord, block);
            return block;
        }

        /// <summary>Halo flags, then each block's beds from them (flat: hand-built blocks have no density), then the beds' halos.</summary>
        public void LinkFlags()
        {
            foreach (var block in Blocks.Values)
                for (var f = 0; f < 6; f++)
                    WaterHalo.CopyFlags(block, Neighbour(block, f), (Face)f);

            foreach (var block in Blocks.Values)
                block.DeriveBeds();

            foreach (var block in Blocks.Values)
                for (var f = 0; f < 6; f++)
                    WaterHalo.CopyFlags(block, Neighbour(block, f), (Face)f);

            _linked = true;
        }

        private bool _linked;

        public void Step(int steps = 1)
        {
            if (!_linked)
                LinkFlags();

            for (var s = 0; s < steps; s++)
            {
                foreach (var block in Blocks.Values)
                {
                    for (var f = 0; f < 6; f++)
                    {
                        WaterHalo.CopyWater(block, Neighbour(block, f), (Face)f);

                        if (f != (int)Face.NegY)
                            WaterHalo.CopyFalling(block, Neighbour(block, f), (Face)f);
                    }
                }

                foreach (var block in Blocks.Values)
                    WaterPipes.UpdateFluxes(block, Dt);

                foreach (var block in Blocks.Values)
                    for (var f = 0; f < 6; f++)
                        WaterHalo.CopyOutScale(block, Neighbour(block, f), (Face)f);

                foreach (var block in Blocks.Values)
                    WaterPipes.ApplyFluxes(block, Dt);

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

        /// <summary>The runtime's sleep test at its 60 steps a second (1e-4 a step), scaled to this step.</summary>
        public bool Quiet()
        {
            const float rate = 1e-4f * 60f;

            foreach (var block in Blocks.Values)
                if (block.MaxDelta >= rate * Dt || block.MaxFlux * Dt >= rate * Dt)
                    return false;

            return true;
        }

        private WaterBlock? Neighbour(WaterBlock block, int face)
        {
            var (dx, dy, dz) = Offsets[face];
            var c = block.Coord;
            return Blocks.GetValueOrDefault(new ChunkCoord(c.X + dx, c.Y + dy, c.Z + dz));
        }
    }

    private static int I(int x, int y, int z) => ChunkConstants.Index(x, y, z);

    private static void FillSolid(WaterBlock b, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        for (var z = z0; z <= z1; z++)
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    b.Flags[I(x, y, z)] = (byte)WaterFlags.Solid;
                    b.Water[I(x, y, z)] = 0f;
                }
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

    /// <summary>Steps until the sim has been quiet for a second, as the runtime sleeps (60 quiet steps at 60 a second).</summary>
    private static int StepUntilSettled(Sim sim, int maxSteps)
    {
        var quiet = 0;

        for (var s = 1; s <= maxSteps; s++)
        {
            sim.Step();
            quiet = sim.Quiet() ? quiet + 1 : 0;

            if (quiet * Dt >= 1f)
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

        sim.Step(60);
        Assert.True(Sum(b, 12, 1, 12, 19, 19, 19) > 0.5, "water should be falling");

        var steps = StepUntilSettled(sim, 40000);
        output.WriteLine($"settled after {steps} steps ({steps * Dt:F1} s); layers 1-4: {Sum(b, 12, 1, 12, 19, 1, 19):F4} {Sum(b, 12, 2, 12, 19, 2, 19):F4} {Sum(b, 12, 3, 12, 19, 3, 19):F4} {Sum(b, 12, 4, 12, 19, 4, 19):F4}, floor cells {b.Water[I(12, 1, 12)]:F4} .. {b.Water[I(15, 1, 15)]:F4}");
        Assert.True(steps > 0, "pool should settle and sleep");
        Assert.Equal(start, sim.Mass(), 1e-3);
        Assert.True(Sum(b, 12, 2, 12, 19, 30, 19) < 1e-3, "all water rests in the floor layer");
        Assert.Equal(start / 64.0, b.Water[I(12, 1, 12)], 1e-2);
        Assert.Equal(start / 64.0, b.Water[I(19, 1, 19)], 1e-2);
    }

    /// <summary>
    /// A blob released 27 m over the floor lands about when free fall says, √(2 h / g) = 2.3 s: falling water carries its
    /// fall speed from cell to cell. Carrying the face flux instead drained it down a cell-a-step tube in 0.43 s.
    /// </summary>
    [Fact]
    public void BlobFallsAtGravity()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 0, 0));
        CarveBox(b, 10, 1, 10, 21, 30, 21);
        FillWater(b, 15, 28, 15, 16, 29, 16);
        var start = sim.Mass();
        var landed = -1;

        for (var s = 1; s <= 4000 && landed < 0; s++)
        {
            sim.Step();

            if (Sum(b, 10, 1, 10, 21, 1, 21) > 0.5 * start)
                landed = s;
        }

        var freeFall = MathF.Sqrt(2f * 27f / WaterPipes.Gravity);
        output.WriteLine($"half the blob on the floor after {landed * Dt:F2} s (free fall from its middle {freeFall:F2} s)");
        Assert.True(landed > 0, "the blob should land");
        Assert.InRange(landed * Dt, 0.6f * freeFall, 1.6f * freeFall);
        Assert.Equal(start, sim.Mass(), 1e-3);
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

        for (var s = 0; s < 2000; s++)
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

        for (var s = 0; s < 2400; s++)
        {
            sim.Step();
            crossed = Math.Max(crossed, b.Mass());
            Assert.Equal(start, sim.Mass(), 1e-2);
        }

        Assert.True(crossed > 1, "water should cross the X face");
        Assert.True(c.Mass() > 1, "water should fall through the Y face");
        Assert.True(d.Mass() > 1, "water should reach the diagonal block");
    }

    /// <summary>
    /// A basin split across an X seam steps bit for bit as the same basin inside one block: both blocks hold the shared
    /// faces and compute them from the same inputs. The floor is solid (y = 0) so no water sits in the bottom layer, where
    /// seam faces count as supported without bed drag while the single block's see the floor.
    /// </summary>
    [Fact]
    public void SeamStepMatchesSingleBlockBitForBit()
    {
        var split = new Sim();
        var a = split.Add(new ChunkCoord(0, 0, 0));
        var b = split.Add(new ChunkCoord(1, 0, 0));
        FillSolid(a, 0, 0, 0, 20, S - 1, S - 1);
        FillSolid(b, 11, 0, 0, S - 1, S - 1, S - 1);
        FillSolid(a, 21, 0, 0, S - 1, 0, S - 1);
        FillSolid(b, 0, 0, 0, 10, 0, S - 1);
        FillWater(a, 24, 1, 8, 31, 14, 23);
        FillWater(b, 0, 6, 8, 2, 9, 12, 0.7f);
        split.LinkFlags();

        var whole = new Sim();
        var w = whole.Add(new ChunkCoord(5, 5, 5));
        FillSolid(w, 22, 0, 0, S - 1, S - 1, S - 1);
        FillSolid(w, 0, 0, 0, 21, 0, S - 1);
        FillWater(w, 3, 1, 8, 10, 14, 23);
        FillWater(w, 11, 6, 8, 13, 9, 12, 0.7f);

        for (var s = 0; s < 600; s++)
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

        Assert.True(Sum(b, 3, 1, 0, 10, 3, S - 1) > 1, "water should spread across the seam");
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

    /// <summary>
    /// Momentum, which the automaton lacks: a dam break runs down a channel and piles up against the far wall above the
    /// level the channel finally settles at, then sloshes back.
    /// </summary>
    [Fact]
    public void DamBreakOvershootsAtTheFarWall()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 0, 0));
        CarveBox(b, 1, 1, 10, 30, 24, 12);
        FillWater(b, 1, 1, 10, 8, 10, 12);
        var mean = Sum(b, 1, 1, 10, 30, 24, 12) / (30 * 3);
        var peak = 0.0;

        for (var s = 0; s < 2400; s++)
        {
            sim.Step();
            peak = Math.Max(peak, Sum(b, 30, 1, 11, 30, 24, 11));
        }

        output.WriteLine($"settled depth {mean:F2} m, peak at the far wall {peak:F2} m");
        Assert.True(peak > 1.15 * mean, $"the surge should overshoot the settled depth {mean:F2} m (peak {peak:F2} m)");
    }

    /// <summary>
    /// The --pond-test scene in small: a pond at rest goes quiet (so its blocks can sleep), then pours out through a breach
    /// in its wall and down a channel whose bed falls a cell every six.
    /// </summary>
    [Fact]
    public void PondSettlesThenDrainsThroughABreach()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 0, 0));
        CarveBox(b, 4, 3, 4, 13, S - 1, 13);

        for (var x = 15; x <= 30; x++)
            for (var y = 4 - (x - 14) / 6; y < S; y++)
                for (var z = 8; z <= 9; z++)
                    b.Flags[I(x, y, z)] = 0;

        FillWater(b, 4, 3, 4, 13, 5, 13);
        sim.LinkFlags();
        var mass = sim.Mass();

        var settled = StepUntilSettled(sim, 720);
        output.WriteLine($"pond quiet after {settled * Dt:F2} s");
        Assert.InRange(settled, 1, 720);

        for (var y = 3; y < S; y++)
            for (var z = 8; z <= 9; z++)
                b.Flags[I(14, y, z)] = 0;

        sim.LinkFlags();
        sim.Step(2400);
        var pond = Sum(b, 4, 3, 4, 13, S - 1, 13);
        output.WriteLine($"pond after 10 s of breach {pond:F1} of 300 m3, mass drift {sim.Mass() - mass:E1}");
        Assert.True(pond < 260.0, $"the pond should pour out through the breach, still holds {pond:F1} m3");
        Assert.True(Math.Abs(sim.Mass() - mass) < 1e-3, $"mass drifted {sim.Mass() - mass}");
    }

    /// <summary>
    /// A breach in a long reservoir's wall: water over a broad crest 4 cells wide under 1 m of head passes about what weir
    /// flow says, 1.705 b H^1.5 m³/s for a smooth crest. The bed's roughness (n 0.1 over the 3 m crest, about 0.3 m of head
    /// at these speeds) costs up to a third of it.
    /// </summary>
    [Fact]
    public void BreachPassesWeirFlow()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 0, 0));
        FillSolid(b, 0, 0, 0, S - 1, S - 1, S - 1);

        // Reservoir x 1..20, z 1..30, water y 1..5; crest x 21..23 on solid y <= 4, open z 14..17; a free fall beyond it.
        for (var z = 1; z <= 30; z++)
            for (var y = 1; y < S - 1; y++)
                for (var x = 1; x <= 30; x++)
                    if (x <= 20 || (z >= 14 && z <= 17 && (x >= 24 || y >= 5)))
                        b.Flags[I(x, y, z)] = 0;

        FillWater(b, 1, 1, 1, 20, 5, 30);
        double Down() => Sum(b, 24, 1, 14, 30, S - 2, 17);
        double Head() => Sum(b, 10, 1, 5, 10, S - 2, 5) + 1 - 5;

        sim.Step((int)(2.5f / Dt));
        var (down, head) = (Down(), Head());
        sim.Step((int)(1.5f / Dt));
        var q = (Down() - down) / 1.5;
        var h = (head + Head()) / 2;
        var weir = 1.705 * 4 * Math.Pow(h, 1.5);
        output.WriteLine($"breach passes {q:F2} m3/s under {h:F2} m of head; weir flow {weir:F2} m3/s");
        Assert.InRange(q, 0.6 * weir, 1.25 * weir);
    }

    /// <summary>
    /// A spring feeding a sheet down a smooth 20 % slope, which the voxels turn into flat terraces and one-cell stairs. The
    /// sheet should run as on the slope, steadily: before the bed rises, landing momentum and film roughness it dammed
    /// behind each stair, and the surges it let go grew down the hill until it emptied between them.
    /// </summary>
    [Fact]
    public void SheetRunsSteadilyDownAVoxelSlope()
    {
        const float slope = 0.2f;
        var chunk = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0) };

        for (var z = -1; z <= S; z++)
            for (var y = -1; y <= S; y++)
                for (var x = -1; x <= S; x++)
                {
                    var d = z < 8 || z > 11 ? -127f : (y - (26f - slope * x)) / MathF.Sqrt(1f + slope * slope) * ChunkConstants.DensityScale;
                    chunk.Density[I(x, y, z)] = (sbyte)Math.Clamp(MathF.Round(d), -127f, 127f);
                }

        var sim = new Sim();
        var b = sim.Add(chunk.Coord);
        b.InitFrom(chunk, seaLevel: -100f);

        double Column(int x) => Sum(b, x, 0, 8, x, S - 1, 11) / 4;
        int[] probes = [12, 18, 24];
        var reached = new float[probes.Length];
        var depths = probes.Select(_ => new List<double>()).ToArray();

        for (var s = 1; s * Dt <= 30f; s++)
        {
            // A spring holds the top of the slope 0.3 m deep; the last columns drain away.
            for (var z = 8; z <= 11; z++)
            {
                b.Water[I(1, 26, z)] = MathF.Max(b.Water[I(1, 26, z)], 0.3f);

                for (var y = 0; y < S; y++)
                    b.Water[I(30, y, z)] = 0f;
            }

            sim.Step();

            for (var k = 0; k < probes.Length; k++)
            {
                var depth = Column(probes[k]);
                reached[k] = reached[k] == 0f && depth > 0.005 ? s * Dt : reached[k];

                if (s * Dt > 15f)
                    depths[k].Add(depth);
            }
        }

        for (var k = 0; k < probes.Length; k++)
        {
            var mean = depths[k].Average();
            var spread = Math.Sqrt(depths[k].Average(d => (d - mean) * (d - mean))) / mean;
            output.WriteLine($"x {probes[k]}: front at {reached[k]:F2} s, depth {mean:F3} m, varying by {spread:P0} ({depths[k].Min():F3} .. {depths[k].Max():F3})");
            Assert.InRange(reached[k], 0.1f, 15f);
            Assert.True(spread < 0.1, $"the sheet at x {probes[k]} should run steadily, its depth varies by {spread:P0}");
        }
    }

    [Fact]
    public void ReservoirFillsPit()
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

        sim.Step();
        Assert.True(b.Water[I(25, 1, 10)] < 1e-3, "far cell must not fill instantly");

        sim.Step(9600);
        var pit = Sum(b, 6, 1, 10, 25, 8, 10) / (20 * 8);
        output.WriteLine($"pit mean fill after 40 s {pit:F3}, far top cell {b.Water[I(25, 8, 10)]:F3}");
        Assert.True(pit > 0.95, $"pit should reach the reservoir level, mean fill {pit}");
        Assert.True(b.Water[I(25, 8, 10)] > 0.8f, "top layer at the far end levels with the sea");
    }

    [Fact]
    public void ResumingBlockTakesItsNeighboursSharedFluxes()
    {
        var a = new WaterBlock(pipes: true);
        var b = new WaterBlock(pipes: true);
        a.Reset(new ChunkCoord(0, 0, 0));
        b.Reset(new ChunkCoord(1, 0, 0));

        // A holds the faces it shares with B (on its +X side) in its x = 31 cells; B holds them in its x = −1 halo slots.
        a.FluxX![I(31, 4, 7)] = 0.75f;
        b.FluxX![I(-1, 4, 7)] = -3f;
        WaterHalo.SyncFluxes(b, a, Face.NegX);
        Assert.Equal(0.75f, b.FluxX[I(-1, 4, 7)]);

        WaterHalo.SyncFluxes(b, null, Face.NegX);
        Assert.Equal(0f, b.FluxX[I(-1, 4, 7)]);

        b.FluxX[I(-1, 9, 2)] = 1.5f;
        WaterHalo.SyncFluxes(a, b, Face.PosX);
        Assert.Equal(1.5f, a.FluxX[I(31, 9, 2)]);
    }

    [Fact]
    public void StepPassesDoNotAllocate()
    {
        var b = DamBreak();
        WaterPipes.UpdateFluxes(b, Dt);
        WaterPipes.ApplyFluxes(b, Dt);
        WaterFlow.Swap(b);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 20; i++)
        {
            for (var f = 0; f < 6; f++)
                WaterHalo.CopyWater(b, null, (Face)f);

            WaterHalo.CopyFalling(b, null, Face.PosY);
            WaterPipes.UpdateFluxes(b, Dt);

            for (var f = 0; f < 6; f++)
                WaterHalo.CopyOutScale(b, null, (Face)f);

            WaterPipes.ApplyFluxes(b, Dt);
            WaterFlow.Swap(b);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static WaterBlock DamBreak()
    {
        var b = new WaterBlock(pipes: true);
        FillWater(b, 0, 0, 0, 15, S - 1, S - 1);
        return b;
    }

    [Fact]
    public void ReportStepTimings()
    {
#if DEBUG
        output.WriteLine("Debug build: timings not meaningful, run with -c Release.");
#endif
        var warm = Stopwatch.StartNew();
        var scratch = DamBreak();

        while (warm.ElapsedMilliseconds < 500)
            StepSingle(scratch);

        Time("empty", new WaterBlock(pipes: true), 2000);
        Time("dam break (busy)", DamBreak(), 300);
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
            WaterPipes.UpdateFluxes(block, Dt);
            var t1 = Stopwatch.GetTimestamp();
            WaterPipes.ApplyFluxes(block, Dt);
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
        WaterPipes.UpdateFluxes(b, Dt);
        WaterPipes.ApplyFluxes(b, Dt);
        WaterFlow.Swap(b);
    }
}
