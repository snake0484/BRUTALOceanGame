using System.Diagnostics;
using System.Numerics;
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
        public readonly WaterBodies Bodies;
        public WaterBodyInfo[] Generated = [];

        public Sim(float seaLevel = 0f) => Bodies = new WaterBodies(seaLevel);

        /// <summary>Step length (s); the runtime's pipe model steps at 1/60.</summary>
        public float StepLength = Dt;

        /// <summary>Registers the generator's bodies (the crater lake) so blocks read from the generator hold their water.</summary>
        public void Register(in TerrainParams p)
        {
            Generated = TerrainGenerator.Bodies(p);

            for (var i = 0; i < Generated.Length; i++)
                Generated[i] = Generated[i] with { Id = Bodies.Add(Generated[i].Level, Generated[i].Area, Generated[i].Centre, Generated[i].Radius, Generated[i].Bottom) };
        }

        /// <summary>What the bodies' held cells gave or took in the last step, booked as the runtime does.</summary>
        private void Book()
        {
            foreach (var block in Blocks.Values)
                foreach (var id in block.Touched)
                    Bodies.Exchange(id, block.BodyExchange[id]);
        }

        public WaterBlock Add(ChunkCoord coord)
        {
            var block = new WaterBlock(pipes: true) { Bodies = Bodies };
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
                        WaterHalo.CopyColour(block, Neighbour(block, f), (Face)f);
                        WaterHalo.CopyFroth(block, Neighbour(block, f), (Face)f);

                        if (f != (int)Face.NegY)
                            WaterHalo.CopyFalling(block, Neighbour(block, f), (Face)f);
                    }
                }

                foreach (var block in Blocks.Values)
                    WaterPipes.UpdateFluxes(block, StepLength);

                foreach (var block in Blocks.Values)
                    for (var f = 0; f < 6; f++)
                        WaterHalo.CopyOutScale(block, Neighbour(block, f), (Face)f);

                foreach (var block in Blocks.Values)
                    WaterPipes.ApplyFluxes(block, StepLength);

                foreach (var block in Blocks.Values)
                    WaterFlow.Swap(block);

                Book();
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
                if (block.MaxDelta >= rate * StepLength || block.MaxFlux * StepLength >= rate * StepLength)
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

        var sim = new Sim(-100f);
        var b = sim.Add(chunk.Coord);
        b.InitFrom(chunk, sim.Bodies);

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

    /// <summary>
    /// A block of the crater lake loaded from the generator, its shore included: its water is the lake's reservoir, held
    /// at its hydrostatic fill under the surface (half full at the top layer, then 1 + Compression × the water above), and
    /// at rest.
    /// </summary>
    [Fact]
    public void LakeLoadsAtRest()
    {
        var p = new TerrainParams();
        var lake = TerrainGenerator.Lake(p);
        // 24 m inside the fill radius: 16 m inside the shore, where the bed is well under the top layer.
        var coord = ChunkCoord.FromWorld(new Vector3(lake.Radius - 24f, lake.Top, 0f));
        var chunk = new VoxelChunk { Coord = coord };
        TerrainGenerator.Generate(chunk, p);

        var sim = new Sim();
        sim.Register(p);
        var b = sim.Add(coord);
        b.InitFrom(chunk, sim.Bodies, sim.Generated);
        var top = lake.Top - coord.Y * S;
        Assert.Equal((byte)WaterFlags.Reservoir, b.Flags[I(0, top, 16)]);
        Assert.Equal(sim.Generated[0].Id, b.Body[I(0, top, 16)]);
        Assert.Equal(0.5f, b.Water[I(0, top, 16)], 1e-6f);
        Assert.Equal(1f + 0.5f * WaterFlow.Compression, b.Water[I(0, top - 1, 16)], 1e-6f);
        Assert.Equal(0f, b.Water[I(0, top + 1, 16)]);

        var mass = sim.Mass();
        var settled = StepUntilSettled(sim, 480);
        output.WriteLine($"lake block {coord}: {mass:F0} m3, quiet after {settled * Dt:F2} s, max delta {b.MaxDelta:E1}");
        Assert.InRange(settled, 1, (int)(1.05f / Dt));
        Assert.True(Math.Abs(sim.Mass() - mass) < 1e-3, "mass drifted");
    }

    /// <summary>
    /// A pit dug into the crater lake's edge (no disc: the hole alone) fills from the lake's held cells, comes to rest, and
    /// then joins the lake (<see cref="WaterBlock.JoinBodies"/>): its cells become the lake's at its level, the water it
    /// took booked against the lake, and the block is at rest.
    /// </summary>
    [Fact]
    public void PitDugIntoTheLakeFillsAndJoinsIt()
    {
        var p = new TerrainParams();
        var lake = TerrainGenerator.Lake(p);

        // The water reaches the shoreline only in its top layer or two, so the pit cuts 2 m inside it, a layer down.
        var shore = lake.Radius - 8f;
        var centre = new Vector3(2f - shore, lake.Top - 1f, 0f);
        var (sim, b, coord) = LakeBlock(p, lake, centre);
        b.RefreshFrom(Chunk(p, coord, centre, dig: true));

        var local = centre - coord.WorldOrigin;
        var top = lake.Top - coord.Y * S;
        var floor = I((int)local.X - 2, top - 2, (int)local.Z);
        Assert.Equal((byte)WaterFlags.None, b.Flags[floor]);
        Assert.Equal(0f, b.Water[floor]);

        var settled = StepUntilSettled(sim, (int)(40f / Dt));
        var id = sim.Generated[0].Id;
        output.WriteLine($"the pit filled and came to rest after {settled * Dt:F1} s; the lake gave {sim.Bodies[id].Given:F2} m3");
        Assert.True(settled > 0, "the pit should fill from the lake and come to rest");
        Assert.True(sim.Bodies[id].Given > 5f, "the lake should have paid for the pit");

        b.JoinBodies();
        Assert.Equal((byte)WaterFlags.Reservoir, b.Flags[floor]);
        Assert.Equal(id, b.Body[floor]);
        Assert.Equal(b.HeldFill(floor), b.Water[floor]);
        Assert.Single(b.Joined);
        Assert.True(MathF.Abs(b.Joined[0].Paid) < 2f, $"the pit stands at the lake's level, yet joining paid {b.Joined[0].Paid} m3");
        Assert.InRange(StepUntilSettled(sim, (int)(2f / Dt)), 1, (int)(1.05f / Dt));
    }

    /// <summary>A pocket dug in the crater's bank under the lake's level, out of the water's reach, stays dry.</summary>
    [Fact]
    public void PocketTheLakeCannotReachStaysDry()
    {
        var p = new TerrainParams();
        var lake = TerrainGenerator.Lake(p);
        var centre = new Vector3(-lake.Radius - 12f, lake.Top - 2f, 0f);
        var (_, b, coord) = LakeBlock(p, lake, centre);
        b.RefreshFrom(Chunk(p, coord, centre, dig: true));

        var local = centre - coord.WorldOrigin;
        var i = I((int)local.X, (int)local.Y, (int)local.Z);
        Assert.Equal((byte)WaterFlags.None, b.Flags[i]);
        Assert.Equal(0f, b.Water[i]);
        b.JoinBodies();
        Assert.Empty(b.Joined);
    }

    /// <summary>
    /// A disc around a dig into the lake (docs/voxel-water.md "Bodies of water"): inside it the lake's water is free, at the
    /// fill it was held at, so it loads at rest; a pit dug in it fills by flow from the lake, the lake paying for the room
    /// the dig opened under its level (WaterBlock.Opened); and once still, with the disc ended, the pit joins the lake.
    /// </summary>
    [Fact]
    public void LakeDiscPoursIntoAPitAndRejoins()
    {
        var p = new TerrainParams();
        var lake = TerrainGenerator.Lake(p);
        var shore = lake.Radius - 8f;
        var centre = new Vector3(2f - shore, lake.Top - 1f, 0f);
        var coord = ChunkCoord.FromWorld(centre);
        var beyond = new ChunkCoord(coord.X + 1, coord.Y, coord.Z);
        var sim = new Sim();
        sim.Register(p);
        var id = sim.Generated[0].Id;
        var disc = new Vector4(centre.X, centre.Z, 3f + 6f, id);
        var a = sim.Add(coord);
        var b = sim.Add(beyond);
        a.InitFrom(Chunk(p, coord, centre), sim.Bodies, sim.Generated, [disc]);
        b.InitFrom(Chunk(p, beyond, centre), sim.Bodies, sim.Generated, [disc]);

        var local = centre - coord.WorldOrigin;
        var top = lake.Top - coord.Y * S;
        // The lake reaches the shore only in its top layer: probe that layer, 6 m inside the shore and well beyond the disc.
        var inside = I((int)local.X + 6, top, (int)local.Z);
        var firstBeyond = (int)MathF.Floor(disc.X + disc.Z - beyond.WorldOrigin.X) + 1;
        var outside = I(firstBeyond + 2, top, (int)local.Z);
        Assert.Equal((byte)WaterFlags.None, a.Flags[inside]);
        // Freed at the fill whose surface on its bed is the lake's (WaterBlock.FreeFill): its sideways head is the held cells'.
        Assert.Equal(WaterBodies.Fill(lake.Top, lake.Surface) - 0.5f, WaterPipes.Head(a.Water[inside]) + WaterBlock.BedRise(a.Bed[inside], a.Water[inside]), 1e-3f);
        Assert.Equal((byte)WaterFlags.Reservoir, b.Flags[outside]);
        Assert.Equal(id, b.Body[outside]);

        var mass = sim.Mass();
        var settled = StepUntilSettled(sim, (int)(4f / Dt));
        output.WriteLine($"disc loads at rest: quiet after {settled * Dt:F2} s, mass drift {sim.Mass() - mass:E1}");
        Assert.InRange(settled, 1, (int)(2f / Dt));
        Assert.True(Math.Abs(sim.Mass() - mass) < 0.05, $"mass drifted by {sim.Mass() - mass}");

        a.RefreshFrom(Chunk(p, coord, centre, dig: true), [disc]);
        a.BookOpened();
        var floor = I((int)local.X - 2, top - 2, (int)local.Z);
        Assert.Equal((byte)WaterFlags.None, a.Flags[floor]);
        Assert.Equal(0f, a.Water[floor]);

        sim.Step((int)(10f / Dt));
        var gave = sim.Bodies[id].Given;
        output.WriteLine($"10 s after the dig: pit floor {a.Water[floor]:F3}, the lake gave {gave:F2} m3");
        Assert.True(a.Water[floor] > 0.15f, $"the pit should fill from the lake, its floor holds {a.Water[floor]}");
        Assert.True(gave > 3f, $"the lake should pay for the pit, it gave {gave} m3");

        // Once still, the disc ends: the pit's water at the lake's level joins it, paying about nothing more.
        var rest = StepUntilSettled(sim, (int)(90f / Dt));
        Assert.True(rest > 0, "the pour should come to rest");
        a.MarkDiscs(default);
        b.MarkDiscs(default);
        a.JoinBodies();
        b.JoinBodies();
        sim.LinkFlags();
        output.WriteLine($"at rest after {rest * Dt:F1} s more; rejoined: the lake paid {(a.Joined.Count > 0 ? a.Joined[0].Paid : 0f):F2} m3");
        Assert.Equal((byte)WaterFlags.Reservoir, a.Flags[floor]);
        Assert.Equal(id, a.Body[floor]);
        Assert.True(a.Joined.Count > 0 && MathF.Abs(a.Joined[0].Paid) < 3f, "joining water at the level should pay about nothing");
        Assert.InRange(StepUntilSettled(sim, (int)(2f / Dt)), 1, (int)(1.05f / Dt));
    }

    /// <summary>
    /// Water a disc freed is still its body's (WaterPipes books free water in disc columns as held water): a pool freed by a
    /// disc drains through a gap in a wall into a channel beyond the disc, and the body pays exactly what crossed. Booked
    /// only at held cells (there are none here) it paid nothing, and the water the disc freed was the body's twice over.
    /// </summary>
    [Fact]
    public void DiscWaterDrainingAwayIsPaidForByItsBody()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 1, 0));
        CarveBox(b, 2, 1, 12, 29, 10, 20);
        FillSolid(b, 13, 1, 12, 13, 10, 20);

        for (var z = 14; z <= 17; z++)
            for (var y = 1; y <= 2; y++)
                b.Flags[I(13, y, z)] = 0;

        const float level = 32f + 4f;
        var id = sim.Bodies.Add(level, 100f, new Vector2(6.5f, 16f), 10f, 32.5f);
        // The pool and the gap lie inside the disc; the channel (x 14 and on) beyond it.
        b.MarkDiscs([new Vector4(6.5f, 16f, 7f, id)]);

        for (var z = 12; z <= 20; z++)
            for (var y = 1; y <= 4; y++)
                for (var x = 2; x <= 11; x++)
                    b.Water[I(x, y, z)] = WaterBodies.Fill(32 + y, level);

        sim.Step((int)(10f / Dt));
        var crossed = Sum(b, 14, 1, 12, 29, 10, 20);
        var gave = sim.Bodies[id].Given;
        output.WriteLine($"{crossed:F1} m3 crossed out of the disc, the body gave {gave:F1} m3");
        Assert.True(crossed > 50.0, $"the pool should drain into the channel, {crossed} m3 did");
        Assert.Equal(crossed, gave, 0.01 * crossed);
    }

    /// <summary>
    /// A body's level stops at its highest held cell's top (WaterBodies.Body.Top): over it its cells have no room, and the
    /// level stood only for pressure. What more it took waits in its ledger and is given back first.
    /// </summary>
    [Fact]
    public void BodyLevelStopsAtItsTopCell()
    {
        var bodies = new WaterBodies(0f);
        var id = bodies.Add(35.2f, 100f, Vector2.Zero, 10f, 30f);
        Assert.Equal(35.5f, bodies[id].Top);

        bodies.Exchange(id, 100f);
        bodies.SettleLevels();
        Assert.Equal(35.5f, bodies[id].Level, 1e-4f);
        Assert.Equal(70f, bodies[id].Ledger, 1e-2f);

        bodies.Exchange(id, -90f);
        bodies.SettleLevels();
        Assert.Equal(35.3f, bodies[id].Level, 1e-4f);
        Assert.Equal(0f, bodies[id].Ledger);
    }

    /// <summary>
    /// A born body fed past its top is no still pool (WaterBodies.SettleLevels): held at a fixed level in a stream it
    /// swallowed water on one side and poured it out on the other without end. It is released, its cells free again at the
    /// fill they were held at. A pool under WaterBlock.MinBornSurface is never born.
    /// </summary>
    [Fact]
    public void BornBodyFedPastItsTopIsReleased()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 1, 0));
        CarveBox(b, 4, 1, 4, 19, 10, 19);
        const float level = 32f + 3f;

        for (var z = 4; z <= 19; z++)
            for (var y = 1; y <= 3; y++)
                for (var x = 4; x <= 19; x++)
                    b.Water[I(x, y, z)] = WaterBodies.Fill(32 + y, level);

        StepUntilSettled(sim, (int)(5f / Dt));
        b.BornBodies();
        var (id, _, _) = Assert.Single(b.Joined);
        var held = b.Water[I(10, 3, 10)];
        var mass = sim.Mass();

        sim.Bodies.Exchange(id, sim.Bodies[id].Area * 1f);
        sim.Bodies.SettleLevels();
        Assert.Contains(id, sim.Bodies.Released);
        Assert.False(sim.Bodies[id].Alive);

        Assert.True(b.ReleaseDead());
        Assert.False(b.Holds(id));
        Assert.Equal((byte)0, b.Flags[I(10, 3, 10)]);
        Assert.Equal(held, b.Water[I(10, 3, 10)]);
        Assert.Equal(mass, sim.Mass(), 1e-3);

        // Too small to be born: a 6 by 6 pool stays free.
        var small = new Sim();
        var c = small.Add(new ChunkCoord(0, 1, 0));
        CarveBox(c, 4, 1, 4, 9, 10, 9);

        for (var z = 4; z <= 9; z++)
            for (var y = 1; y <= 3; y++)
                for (var x = 4; x <= 9; x++)
                    c.Water[I(x, y, z)] = WaterBodies.Fill(32 + y, level);

        StepUntilSettled(small, (int)(5f / Dt));
        c.BornBodies();
        Assert.Empty(c.Joined);
    }

    /// <summary>
    /// Free water left standing level becomes a body (docs/voxel-water.md "Bodies of water"): a pond at rest in a box is
    /// born a body at its level with its surface as its area, its cells held; a step of its ledger moves its level and the
    /// refill lowers its held cells.
    /// </summary>
    [Fact]
    public void StillPondIsBornABodyAndDrainsUniformly()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 1, 0));
        CarveBox(b, 4, 1, 4, 19, 10, 19);
        const float level = 32f + 3f;

        for (var z = 4; z <= 19; z++)
            for (var y = 1; y <= 3; y++)
                for (var x = 4; x <= 19; x++)
                    b.Water[I(x, y, z)] = WaterBodies.Fill(32 + y, level);

        var settled = StepUntilSettled(sim, (int)(5f / Dt));
        Assert.InRange(settled, 1, (int)(3f / Dt));

        b.JoinBodies();
        Assert.Empty(b.Joined);
        b.BornBodies();
        var (id, paid, _) = Assert.Single(b.Joined);
        Assert.True(id > WaterBodies.Sea);
        ref var body = ref sim.Bodies[id];
        output.WriteLine($"born body {id}: level {body.Level:F3}, area {body.Area}, paid {paid:E1} m3");
        Assert.Equal(level, body.Level, 0.05f);
        Assert.Equal(256f, body.Area);
        Assert.True(MathF.Abs(paid) < 0.5f);
        Assert.Equal((byte)WaterFlags.Reservoir, b.Flags[I(10, 3, 10)]);
        Assert.Equal(id, b.Body[I(10, 1, 10)]);
        Assert.True(b.AllHeld() || !b.HasFreeWater());

        // A small drain: a level step of water out of the ledger lowers the whole pond by a centimetre.
        sim.Bodies.Exchange(id, -body.Area * WaterBodies.LevelStep);
        sim.Bodies.SettleLevels();
        Assert.Contains(id, sim.Bodies.Moved);
        Assert.Equal(level - WaterBodies.LevelStep, sim.Bodies[id].Level, 1e-4f);
        b.RefillHeld();
        Assert.Equal(0.49f, b.Water[I(10, 3, 10)], 1e-4f);
    }

    /// <summary>
    /// A pocket dug under the lake bed, opening into the water above (a disc frees the column): the lake drops into it and
    /// must come to rest, its fills near hydrostatic. In game it rang for good: fills of 3-4 where 1.2 is hydrostatic, a
    /// mound on the surface, water falling into the pocket forever and spray with it.
    /// </summary>
    [Fact]
    public void PocketUnderTheLakeBedSettles()
    {
        var p = new TerrainParams();
        var lake = TerrainGenerator.Lake(p);
        var shore = lake.Radius - 8f;
        var x = 40f - shore;
        var bed = TerrainGenerator.SurfaceHeight(x, 0f, p);
        var centre = new Vector3(x, bed - 2f, 0f);
        // The pocket's block and the lake's over it, stepped as the game steps the pipe model, 60 a second.
        var coord = ChunkCoord.FromWorld(centre);
        var above = new ChunkCoord(coord.X, coord.Y + 1, coord.Z);
        var sim = new Sim { StepLength = 1f / 60f };
        sim.Register(p);
        var id = sim.Generated[0].Id;
        var disc = new Vector4(centre.X, centre.Z, 3f + 6f, id);
        var b = sim.Add(coord);
        var a = sim.Add(above);
        b.InitFrom(Chunk(p, coord, centre), sim.Bodies, sim.Generated, [disc]);
        a.InitFrom(Chunk(p, above, centre), sim.Bodies, sim.Generated, [disc]);
        b.RefreshFrom(Chunk(p, coord, centre, dig: true), [disc]);
        a.RefreshFrom(Chunk(p, above, centre, dig: true), [disc]);
        output.WriteLine($"pocket at {centre} (bed {bed:F1} m, {lake.Top - bed:F1} m of water over it)");

        var dt = sim.StepLength;
        var settled = -1;
        var quiet = 0;
        var peak = 0f;

        for (var s = 1; s * dt <= 40f; s++)
        {
            sim.Step();
            quiet = sim.Quiet() ? quiet + 1 : 0;
            peak = MathF.Max(peak, MathF.Max(MaxFree(a), MaxFree(b)));

            if (s % (int)(5f / dt) == 0)
                output.WriteLine($"{s * dt:F0} s: max delta {MathF.Max(a.MaxDelta, b.MaxDelta):E1}, max flux {MathF.Max(a.MaxFlux, b.MaxFlux):F3} m3/s, largest free fill {MathF.Max(MaxFree(a), MaxFree(b)):F2}");

            if (quiet * dt >= 1f)
            {
                settled = s;
                break;
            }
        }

        output.WriteLine($"settled after {(settled > 0 ? settled * dt : -1):F1} s; peak free fill {peak:F2}");
        Assert.True(settled > 0, "the pocket should fill and the lake over it come to rest within 40 s");
        Assert.True(peak < 2f, $"free fills should stay near hydrostatic, peaked at {peak}");
    }

    private static float MaxFree(WaterBlock b)
    {
        var max = 0f;

        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var i = I(0, y, z); i < I(S, y, z); i++)
                    if ((b.Flags[i] & (byte)(WaterFlags.Solid | WaterFlags.Reservoir)) == 0)
                        max = MathF.Max(max, b.Water[i]);

        return max;
    }

    private static VoxelChunk Chunk(in TerrainParams p, ChunkCoord coord, Vector3 centre, bool dig = false)
    {
        var chunk = new VoxelChunk { Coord = coord };
        TerrainGenerator.Generate(chunk, p);

        if (dig)
            VoxelEditor.Apply(chunk, [new BrushOp(centre, 3f, BrushMode.Dig, VoxelMaterial.Air)]);

        return chunk;
    }

    private static (Sim Sim, WaterBlock Block, ChunkCoord Coord) LakeBlock(in TerrainParams p, in LakeInfo lake, Vector3 at)
    {
        var coord = ChunkCoord.FromWorld(at);
        var sim = new Sim();
        sim.Register(p);
        var b = sim.Add(coord);
        b.InitFrom(Chunk(p, coord, at), sim.Bodies, sim.Generated);
        return (sim, b, coord);
    }

    [Fact]
    public void ReservoirFillsPit()
    {
        var sim = new Sim(-23.5f);
        var b = sim.Add(new ChunkCoord(0, -1, 0));
        CarveBox(b, 2, 1, 10, 25, 8, 10);

        for (var y = 1; y <= 8; y++)
            for (var x = 2; x <= 5; x++)
                b.Hold(x, y, 10, WaterBodies.Sea);

        sim.Step();
        Assert.True(b.Water[I(25, 1, 10)] < 1e-3, "far cell must not fill instantly");

        sim.Step(9600);
        var pit = Sum(b, 6, 1, 10, 25, 8, 10) / (20 * 8);
        output.WriteLine($"pit mean fill after 40 s {pit:F3}, far top cell {b.Water[I(25, 8, 10)]:F3}");
        Assert.True(pit > 0.95, $"pit should reach the reservoir level, mean fill {pit}");
        Assert.True(b.Water[I(25, 8, 10)] > 0.8f, "top layer at the far end levels with the sea");
    }

    [Fact]
    public void ChannelRisesAndFallsWithTheSwell()
    {
        // The sea's top layer is y 8 (level −23.5), its run-up layers 9..12 held empty over it. A channel 4 m deep and 8 m
        // long opens off it: a quarter wave in it takes 5 s, so a 16 s swell should carry it up and down nearly whole.
        const float amplitude = 0.5f;
        const float period = 16f;
        var sim = new Sim(-23.5f) { StepLength = 1f / 60f };
        var b = sim.Add(new ChunkCoord(0, -1, 0));
        CarveBox(b, 2, 5, 10, 17, 12, 12);

        for (var z = 10; z <= 12; z++)
            for (var y = 5; y <= 12; y++)
                for (var x = 2; x <= 9; x++)
                    b.Hold(x, y, z, WaterBodies.Sea);

        FillWater(b, 10, 5, 10, 17, 8, 12);
        Assert.True(StepUntilSettled(sim, 6000) > 0, "the channel settles against a flat sea");
        var rest = Sum(b, 10, 5, 10, 17, 12, 12);
        var low = float.MaxValue;
        var high = float.MinValue;
        var steps = (int)(2f * period / sim.StepLength);

        for (var s = 1; s <= steps; s++)
        {
            b.Swell.AsSpan().Fill(amplitude * MathF.Sin(2f * MathF.PI * s * sim.StepLength / period));
            b.Swelling = true;
            sim.Step();

            if (s * 2 < steps)
                continue;

            var surface = -27.5f;
            for (var y = 5; y <= 12; y++)
                surface += MathF.Min(b.Water[I(17, y, 11)], 1f);

            low = MathF.Min(low, surface);
            high = MathF.Max(high, surface);
        }

        output.WriteLine($"far end of the channel {low:F3} to {high:F3} m against the sea's -24.000 to -23.000");
        Assert.InRange((high - low) * 0.5f, 0.7f * amplitude, 1.4f * amplitude);
        Assert.InRange((high + low) * 0.5f, -23.5f - 0.1f, -23.5f + 0.1f);

        b.Swell.AsSpan().Clear();
        b.Swelling = false;
        Assert.True(StepUntilSettled(sim, 12000) > 0, "the channel settles again once the sea is flat");
        var after = Sum(b, 10, 5, 10, 17, 12, 12);
        output.WriteLine($"channel water {rest:F3} before the swell, {after:F3} after");
        Assert.InRange(after, rest - 0.01 * rest, rest + 0.01 * rest);
    }

    [Fact]
    public void CrestNeverSpillsOntoDryBeach()
    {
        // The sea's top layer is y 8 (level −23.5), its run-up layers 9..12 held over it; a dry beach shelf at y 9..12
        // beside it. A ±0.5 m swell stands half a cell up the run-up layer at its crests, but the beach stays dry.
        const float amplitude = 0.5f;
        const float period = 8f;
        var sim = new Sim(-23.5f) { StepLength = 1f / 60f };
        var b = sim.Add(new ChunkCoord(0, -1, 0));
        CarveBox(b, 2, 5, 10, 9, 12, 12);
        CarveBox(b, 10, 9, 10, 17, 12, 12);

        for (var z = 10; z <= 12; z++)
            for (var y = 5; y <= 12; y++)
                for (var x = 2; x <= 9; x++)
                    b.Hold(x, y, z, WaterBodies.Sea);

        var wettest = 0.0;
        var steps = (int)(2f * period / sim.StepLength);

        for (var s = 1; s <= steps; s++)
        {
            b.Swell.AsSpan().Fill(amplitude * MathF.Sin(2f * MathF.PI * s * sim.StepLength / period));
            b.Swelling = true;
            sim.Step();
            wettest = Math.Max(wettest, Sum(b, 10, 9, 10, 17, 12, 12));
        }

        output.WriteLine($"most water on the beach {wettest:F4} m3");
        Assert.Equal(0.0, wettest);
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

    /// <summary>A dam break of pond-coloured water: every cell the water reaches carries its colour (docs/water-fog.md), and no wet cell is left unset.</summary>
    [Fact]
    public void ColourTravelsWithTheWater()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 0, 0));
        FillWater(b, 0, 0, 0, 15, S - 1, S - 1);

        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var x = 0; x <= 15; x++)
                    b.Colour[I(x, y, z)] = WaterColour.Pond;

        sim.Step(300);
        var reached = 0;

        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var x = 0; x < S; x++)
                {
                    var i = I(x, y, z);
                    if (b.Water[i] < WaterSurfaceMesher.WetThreshold)
                        continue;

                    Assert.Equal(WaterColour.Pond, b.Colour[i]);
                    if (x > 15)
                        reached++;
                }

        Assert.True(reached > 100, $"the water should have spread right, {reached} cells did");
    }

    /// <summary>
    /// A column falling 20 m lands white (<see cref="WaterBlock.Froth"/>): the falling water aerates and carries its froth
    /// into the pool, which spreads it and lets it fade (1/e in 4 s) once the pour has stopped.
    /// </summary>
    [Fact]
    public void PourFrothsWhereItLandsAndFades()
    {
        var sim = new Sim();
        var b = sim.Add(new ChunkCoord(0, 0, 0));
        CarveBox(b, 8, 1, 8, 23, 30, 23);
        FillWater(b, 15, 22, 15, 16, 29, 16);
        var start = sim.Mass();
        var peak = 0f;

        for (var s = 0; s < 3 * 240; s++)
        {
            sim.Step();
            peak = MathF.Max(peak, MaxFroth(b, 1));
        }

        sim.Step(20 * 240);
        var after = MaxFroth(b, 1);
        output.WriteLine($"floor froth peaked at {peak:F2}, {after:F3} 20 s later");
        Assert.True(peak > 0.4f, $"the landing should froth, peaked at {peak:F2}");
        Assert.True(after < 0.05f, $"the froth should fade, {after:F3} left");
        Assert.Equal(start, sim.Mass(), 1e-3);
    }

    private static float MaxFroth(WaterBlock b, int y)
    {
        var max = 0f;

        for (var z = 0; z < S; z++)
            for (var x = 0; x < S; x++)
                if (b.Water[I(x, y, z)] > WaterSurfaceMesher.WetThreshold)
                    max = MathF.Max(max, b.Froth![I(x, y, z)]);

        return max;
    }

    [Fact]
    public void ColourBlendsByMass()
    {
        var a = WaterColour.Pack(200, 100, 0, 255);
        var c = WaterColour.Pack(0, 100, 200, 51);
        var mixed = WaterColour.Blend(a, 3f, c, 1f);

        Assert.Equal(150, WaterColour.R(mixed));
        Assert.Equal(100, WaterColour.G(mixed));
        Assert.Equal(50, WaterColour.B(mixed));
        Assert.Equal(204, WaterColour.A(mixed));
        Assert.Equal(a, WaterColour.Blend(a, 2f, WaterColour.None, 5f));
        Assert.Equal(c, WaterColour.Blend(WaterColour.None, 2f, c, 5f));
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
