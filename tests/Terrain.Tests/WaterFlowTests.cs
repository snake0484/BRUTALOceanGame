using System.Numerics;
using BrutalGame.Terrain.Water;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

/// <summary>Flows of running water (docs/voxel-water.md "Flows"): the river's identity in blocks, its routing, and its edges.</summary>
public sealed class WaterFlowTests(ITestOutputHelper output)
{
    private const int S = ChunkConstants.Size;
    private const float Dt = 1f / 60f;

    private static readonly Face[] Sides = [Face.NegX, Face.PosX, Face.NegZ, Face.PosZ];

    /// <summary>The lake registered as a body with its spring, and the river as a flow out of it, as the runtime registers them.</summary>
    private static (WaterBodies Bodies, WaterBodyInfo Lake, WaterFlows Flows, WaterFlowInfo River) Register(in TerrainParams p)
    {
        var bodies = new WaterBodies(p.SeaLevel);
        var lake = TerrainGenerator.Bodies(p)[0];
        lake = lake with { Id = bodies.Add(lake.Level, lake.Area, lake.Centre, lake.Radius, lake.Bottom) };
        var flows = new WaterFlows();
        var river = TerrainGenerator.Flows(p)[0];
        river = river with { Id = flows.Add(river.Path, lake.Id, new Vector3(lake.Centre, lake.Radius), p.SeaLevel) };
        return (bodies, lake, flows, river);
    }

    /// <summary>
    /// With no reach simulated, the river is routed from end to end: the lake's spring and the weir over the sill balance
    /// at the generated level, every reach carries the discharge, and no water is made or lost on the way.
    /// </summary>
    [Fact]
    public void RoutedRiverCarriesItsDischargeAndConserves()
    {
        var p = new TerrainParams();
        var (bodies, lake, flows, river) = Register(p);
        var flow = flows[river.Id];
        var stored = Storage(flow);
        Assert.True(flow.Reaches.Length > 4, $"the river should cross several chunk columns, crossed {flow.Reaches.Length}");

        for (var t = 0f; t < 600f; t += Dt)
        {
            bodies.Exchange(lake.Id, lake.Inflow * Dt);
            flows.Route(Dt, bodies);
            bodies.SettleLevels();
        }

        var level = bodies[lake.Id].Level;
        var mouth = flow.Reaches[^1].Outflow;
        var gained = Storage(flow) - stored;
        output.WriteLine($"after 600 s: lake {level:F3} (generated {lake.Level:F3}), weir {flow.Weir:F2}, mouth {mouth:F2} m3/s, drawn {flow.Drawn:F0}, delivered {flow.Delivered:F0}, storage +{gained:F1} m3");
        Assert.InRange(level, lake.Level - 0.05f, lake.Level + 0.05f);
        Assert.InRange(flow.Weir, 0.9f * p.RiverDischarge, 1.1f * p.RiverDischarge);
        Assert.InRange(mouth, 0.8f * p.RiverDischarge, 1.2f * p.RiverDischarge);
        Assert.True(Math.Abs(flow.Drawn - flow.Delivered - gained) < 0.01 * flow.Delivered + 1.0, "routing should conserve the water it draws");
    }

    /// <summary>
    /// A reach the sim takes over passes on what its routed storage held more than its blocks; let go again, its blocks'
    /// water is its storage. Booking an edge moves water into (or out of) a routed reach. Calibrated to the water its
    /// blocks are generated with, a reach keeps its stage.
    /// </summary>
    [Fact]
    public void ReachesReconcileAsTheSimTakesThemOverAndLetsThemGo()
    {
        var p = new TerrainParams();
        var (_, _, flows, river) = Register(p);
        var flow = flows[river.Id];
        var reach = flow.Reaches[2];
        var below = flow.Reaches[3];
        var before = below.Storage;

        reach.Storage = reach.Design + 10f;
        WaterFlows.Activate(flow, 2, reach.Design);
        Assert.True(reach.Active);
        Assert.Equal(before + 10f, below.Storage, 1e-3f);

        WaterFlows.Book(flow, 3, 5f);
        Assert.Equal(before + 15f, below.Storage, 1e-3f);

        WaterFlows.Freeze(flow, 2, 42f);
        Assert.False(reach.Active);
        Assert.Equal(42f, reach.Storage);

        var other = flow.Reaches[4];
        other.Storage = 0.5f * other.Design;
        WaterFlows.Calibrate(flow, 4, 1.2f * other.Design);
        Assert.True(other.Calibrated);
        Assert.Equal(0.5f, other.Stage, 1e-4f);
    }

    /// <summary>
    /// A block of the river loaded from the generator: its columns know the flow, its water is free (never held, never
    /// born a body), the lake's colour, and in the pipe model its surface already moves downstream.
    /// </summary>
    [Fact]
    public void RiverBlockKnowsItsFlow()
    {
        var p = new TerrainParams();
        var (bodies, _, _, river) = Register(p);
        var (block, x, z) = RiverBlock(p, bodies, river, river.Path.Count * 2 / 5);
        Assert.True(block.HasFlow);
        Assert.Equal(river.Id, block.FlowAt(x, z));
        Assert.True(block.FlowWater() > 1f, $"the block should hold the river's water, holds {block.FlowWater():F2} m3");

        var top = -1;
        for (var y = S - 1; y >= 0 && top < 0; y--)
            top = block.Water[ChunkConstants.Index(x, y, z)] > WaterSurfaceMesher.WetThreshold ? y : -1;

        Assert.True(top >= 0, "the river's column should be wet");
        var i = ChunkConstants.Index(x, top, z);
        Assert.Equal((byte)WaterFlags.None, block.Flags[i]);
        Assert.Equal(WaterColour.Lake, block.Colour[i]);

        var dir = river.Path.Directions[river.Path.Count * 2 / 5];
        var along = 0.5f * (block.FluxX![i - 1] + block.FluxX[i]) * dir.X + 0.5f * (block.FluxZ![i - ChunkConstants.PaddedArea] + block.FluxZ[i]) * dir.Y;
        Assert.True(along > 0f, "the surface should start moving downstream");

        block.BornBodies(minSurface: 1);
        Assert.Empty(block.Joined);
    }

    /// <summary>
    /// One block of the river stepped alone, its neighbours not loaded: its edges open onto the chunk's apron, so the river
    /// runs in from upstream and on out downstream instead of against walls, at about its discharge, and holds about the
    /// water it was generated with. Every cubic metre that crosses an edge is booked, so the block's water plus what left
    /// it is what it started with plus what came in. (With the bed at 0.4 % a metre on its flats the block carried a
    /// quarter of the discharge and drained.)
    /// </summary>
    [Fact]
    public void RiverRunsOnAcrossUnloadedNeighbours()
    {
        var p = new TerrainParams();
        var (bodies, _, _, river) = Register(p);
        var (block, _, _) = RiverBlock(p, bodies, river, river.Path.Count * 2 / 5);
        var start = block.FlowWater();
        var mass = block.Mass();
        var outflow = 0.0;
        var inflow = 0.0;
        var crossed = 0.0;

        for (var t = 0f; t < 30f; t += Dt)
        {
            StepAlone(block, open: true);

            foreach (var face in Sides)
            {
                var moved = block.EdgeOutflow(face, Dt);
                crossed += moved;
                outflow += t > 20f ? Math.Max(moved, 0f) : 0f;
                inflow += t > 20f ? Math.Max(-moved, 0f) : 0f;
            }

            WaterFlow.Swap(block);
        }

        var left = block.FlowWater();
        var q = p.RiverDischarge;
        output.WriteLine($"river block {block.Coord}: flow water {start:F1} -> {left:F1} m3; over the last 10 s {inflow / 10f:F2} m3/s in, {outflow / 10f:F2} m3/s out (discharge {q}); mass {mass:F1} -> {block.Mass():F1}, crossed {crossed:F1}");
        Assert.InRange(inflow / 10f, 0.75f * q, 1.35f * q);
        Assert.InRange(outflow / 10f, 0.75f * q, 1.35f * q);
        Assert.InRange(left, 0.8f * start, 1.2f * start);
        Assert.True(Math.Abs(block.Mass() + crossed - mass) < 0.01 * mass, "the edges should account for every cubic metre");
    }

    /// <summary>
    /// A straight channel 8 m wide fed by a spring carries the uniform flow <see cref="RiverPath.UniformFlow"/> gives at the
    /// depth it runs at, its <see cref="RiverPath.Conveyance"/> included, along the grid and across it: the generated depth
    /// is then the depth the sim runs the river at. (Measured: 1.20 along an axis, 1.01–1.02 at 30–60° on a 5 % slope;
    /// 0.81 at 45° on 10 %, where the voxel stairs come twice as often.)
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(45f)]
    [InlineData(60f)]
    public void ChannelCarriesItsUniformFlow(float degrees)
    {
        const float slope = 0.05f;
        const float spring = 0.3f;
        var (sin, cos) = MathF.SinCos(degrees * MathF.PI / 180f);
        var chunk = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0) };

        // Along (cos, sin) from (2, 6), its bed falling at the slope along it.
        for (var z = -1; z <= S; z++)
            for (var y = -1; y <= S; y++)
                for (var x = -1; x <= S; x++)
                {
                    var along = (x - 2f) * cos + (z - 6f) * sin;
                    var across = -(x - 2f) * sin + (z - 6f) * cos;
                    var d = MathF.Abs(across) > 4f || along < -1f ? -127f : (y - 26f + slope * along) / MathF.Sqrt(1f + slope * slope) * ChunkConstants.DensityScale;
                    chunk.Density[ChunkConstants.Index(x, y, z)] = (sbyte)Math.Clamp(MathF.Round(d), -127f, 127f);
                }

        var block = new WaterBlock(pipes: true);
        block.InitFrom(chunk, new WaterBodies(-100f));
        var (q, depth, steps, samples) = (0.0, 0.0, 0, 0);

        for (var s = 1; s * Dt <= 40f; s++)
        {
            for (var z = 0; z < S; z++)
            {
                for (var x = 0; x < S; x++)
                {
                    var along = (x - 2f) * cos + (z - 6f) * sin;

                    for (var y = 0; y < S; y++)
                    {
                        var i = ChunkConstants.Index(x, y, z);

                        if (along < 1.5f && (block.Flags[i] & (byte)WaterFlags.Solid) == 0 && (block.Flags[i - ChunkConstants.Padded] & (byte)WaterFlags.Solid) != 0)
                            block.Water[i] = MathF.Max(block.Water[i], spring);

                        if (x >= 30 || z >= 30)
                            block.Water[i] = 0f;
                    }
                }
            }

            StepAlone(block, open: false);
            WaterFlow.Swap(block);

            if (s * Dt <= 25f)
                continue;

            // The discharge through the cross-section 14 m along (every face it cuts), and the mean water over its middle 6 m.
            steps++;

            for (var z = 0; z < S - 1; z++)
            {
                for (var x = 0; x < S - 1; x++)
                {
                    var along = (x - 2f) * cos + (z - 6f) * sin;
                    var across = -(x - 2f) * sin + (z - 6f) * cos;

                    for (var y = 0; y < S; y++)
                    {
                        var i = ChunkConstants.Index(x, y, z);
                        q += along < 14f && along + cos >= 14f ? block.FluxX![i] : 0f;
                        q += along < 14f && along + sin >= 14f ? block.FluxZ![i] : 0f;
                    }

                    if (MathF.Abs(along - 14f) >= 0.5f || MathF.Abs(across) >= 3f)
                        continue;

                    for (var y = 0; y < S; y++)
                        depth += block.Water[ChunkConstants.Index(x, y, z)];

                    samples++;
                }
            }
        }

        q /= steps;
        depth /= samples;
        var uniform = RiverPath.UniformFlow((float)depth, 8f, slope);
        output.WriteLine($"{degrees}°: {q:F2} m3/s at {depth:F3} m deep; uniform flow {uniform:F2} (ratio {q / uniform:F2})");
        Assert.InRange(q / uniform, 0.85, 1.3);
    }

    /// <summary>One pipe step of a block alone (no neighbours), its flow edges open onto its apron if asked; the swap is left to the caller.</summary>
    private static void StepAlone(WaterBlock block, bool open)
    {
        for (var f = 0; f < 6; f++)
        {
            WaterHalo.CopyFlags(block, null, (Face)f);
            WaterHalo.CopyWater(block, null, (Face)f);
            WaterHalo.CopyColour(block, null, (Face)f);
            WaterHalo.CopyFroth(block, null, (Face)f);

            if (f != (int)Face.NegY)
                WaterHalo.CopyFalling(block, null, (Face)f);
        }

        if (open)
            foreach (var face in Sides)
                block.OpenFlowEdge(face);

        WaterPipes.UpdateFluxes(block, Dt);

        for (var f = 0; f < 6; f++)
            WaterHalo.CopyOutScale(block, null, (Face)f);

        WaterPipes.ApplyFluxes(block, Dt);
    }

    /// <summary>The block holding a station's water, loaded from the generator with the river, and the station's column in it.</summary>
    private static (WaterBlock Block, int X, int Z) RiverBlock(in TerrainParams p, WaterBodies bodies, WaterFlowInfo river, int station)
    {
        var path = river.Path;
        var at = new Vector3(path.Points[station].X, path.Beds[station] + 0.1f, path.Points[station].Y);
        var coord = ChunkCoord.FromWorld(at);
        var chunk = new VoxelChunk { Coord = coord };
        TerrainGenerator.Generate(chunk, p);
        var block = new WaterBlock(pipes: true);
        block.InitFrom(chunk, bodies, flows: [river]);
        var local = at - coord.WorldOrigin;
        return (block, Math.Clamp((int)MathF.Round(local.X), 0, S - 1), Math.Clamp((int)MathF.Round(local.Z), 0, S - 1));
    }

    private static float Storage(WaterFlows.Flow flow)
    {
        var sum = 0f;

        foreach (var reach in flow.Reaches)
            sum += reach.Storage;

        return sum;
    }
}
