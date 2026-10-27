using System.Numerics;
using System.Runtime.CompilerServices;
using BrutalGame.Terrain.Water;

namespace BrutalGame.Terrain;

/// <summary>
/// The main island's crater lake (<see cref="TerrainParams.LakeRadius"/>): every open voxel at or under <see cref="Top"/>
/// within <see cref="Radius"/> of <see cref="Centre"/> is generated full of lake water. Its surface stands at Top + 0.5.
/// </summary>
public readonly record struct LakeInfo(Vector2 Centre, float Radius, int Top, int Bottom)
{
    public bool Exists => Radius > 0f;

    /// <summary>
    /// World y of the water's surface as generated: the middle of the top layer, which is half full. At the layer's top
    /// the top cells sat full, where a cell's head turns from its surface to its pressure and grows 50 times as steep,
    /// and water meeting the lake there (a pit filling from it) chattered between the two and never settled.
    /// </summary>
    public float Surface => Top;

    /// <summary>The water's area at that surface (m²).</summary>
    public float Area => MathF.PI * Radius * Radius;
}

public static class TerrainGenerator
{
    private const int P = ChunkConstants.Padded;
    private const int HeightStride = P + 2;
    private const int HeightArea = HeightStride * HeightStride;
    private const int ColumnArea = P * P;
    private const int LatticeStep = 4;
    private const int LatticeAxis = (P + 2 + LatticeStep) / LatticeStep + 1;
    private const int LatticeVolume = LatticeAxis * LatticeAxis * LatticeAxis;
    private const float ClampMetres = 127f / ChunkConstants.DensityScale;
    private const float SurfaceLayer = 1.5f;
    private const float SubSurfaceLayer = 4f;
    private const float CarvedRock = 1.5f;
    private const float OverhangStartHeight = 3f;
    private const float OverhangRampHeight = 10f;
    private const int MaxIslands = 4;

    /// <summary>The lake bed's noise at its centre (m), fading to none at the shore; its banks take the island's full detail over this run.</summary>
    private const float LakeBedDetail = 1.5f;
    private const float LakeBankRamp = 40f;

    /// <summary>
    /// Lake water fills this far past the shore radius: the voxels the shoreline crosses, and every nook of the bank the
    /// detail noise sinks under the water line. Left dry, those seeped full through films for minutes once the lake woke.
    /// </summary>
    private const float LakeShoreCells = 8f;

    /// <summary>No overhang caves within this distance of the shore, so none opens under the rim below the water line.</summary>
    private const float LakeSolidRim = 20f;
    private const float LakeSolidRamp = 30f;

    private const uint SeabedSalt = 0x1B873593u;
    private const uint CoastSalt = 0xCC9E2D51u;
    private const uint CliffSalt = 0xE6546B64u;
    private const uint DetailSalt = 0x85EBCA6Bu;
    private const uint OverhangSalt = 0xC2B2AE35u;
    private const uint IslandSalt = 0xA511E9B3u;

    private struct Island
    {
        public float X;
        public float Z;
        public float Radius;
        public float Height;
    }

    [InlineArray(MaxIslands)]
    private struct IslandBuffer
    {
        private Island _element;
    }

    private struct IslandSet
    {
        public IslandBuffer Items;
        public int Count;
        public uint Seed;

        /// <summary>The crater lake, centred on the main island: its shore radius, surface height and depth; reach is where its banks clear any ground.</summary>
        public float LakeRadius;
        public float LakeSurface;
        public float LakeDepth;
        public float LakeBank;
        public float LakeReach;
    }

    public static float MaxHeight(in TerrainParams p) => p.IslandHeight + p.DetailHeight + 1f;

    public static float MinHeight(in TerrainParams p) => MathF.Min(p.SeabedDepth - p.SeabedUndulation, -p.DetailHeight) - 1f;

    public static float SurfaceHeight(float x, float z, in TerrainParams p)
    {
        var islands = CreateIslands(p);
        return Height(x, z, p, islands);
    }

    public static LakeInfo Lake(in TerrainParams p)
    {
        var s = CreateIslands(p);

        if (s.LakeRadius <= 0f)
            return default;

        var top = (int)(s.LakeSurface - 0.5f);
        return new LakeInfo(Vector2.Zero, s.LakeRadius + LakeShoreCells, top, (int)MathF.Floor(s.LakeSurface - s.LakeDepth - LakeBedDetail) - 1);
    }

    /// <summary>
    /// The bodies of still water the generator makes above sea level (docs/voxel-water.md "Bodies of water"): the crater
    /// lake, as a record the water sim holds at its level and drains by its area (the shore's, not the fill's).
    /// </summary>
    public static WaterBodyInfo[] Bodies(in TerrainParams p)
    {
        var lake = Lake(p);

        if (!lake.Exists)
            return [];

        var shore = lake.Radius - LakeShoreCells;
        return [new WaterBodyInfo(lake.Centre, lake.Radius, lake.Surface, lake.Bottom - 0.5f, MathF.PI * shore * shore)];
    }

    /// <summary>
    /// Fills density, material and water (padded apron included) at the chunk's LOD: voxel i samples world origin + i·s,
    /// density is stored in voxel units. Thread-safe, deterministic, allocation-free.
    /// </summary>
    public static void Generate(VoxelChunk chunk, in TerrainParams p)
    {
        GenerateSolids(chunk, p);
        FillSea(chunk, p.SeaLevel);
        FillLake(chunk, Lake(p));
    }

    /// <summary>
    /// Every open voxel at or under the lake's top layer and within its radius holds lake water. The basin is closed (its
    /// rim stands above the water all round, and no overhang cave opens under it), so the water is at rest as generated.
    /// </summary>
    public static void FillLake(VoxelChunk chunk, in LakeInfo lake)
    {
        if (!lake.Exists)
            return;

        var origin = chunk.Coord.WorldOrigin;
        var scale = chunk.Coord.VoxelScale;
        var low = origin - new Vector3(scale);
        var high = origin + new Vector3(ChunkConstants.Size * scale);

        if (low.Y > lake.Top || high.Y < lake.Bottom)
            return;

        var nearX = Math.Clamp(lake.Centre.X, low.X, high.X) - lake.Centre.X;
        var nearZ = Math.Clamp(lake.Centre.Y, low.Z, high.Z) - lake.Centre.Y;
        var r2 = lake.Radius * lake.Radius;

        if (nearX * nearX + nearZ * nearZ > r2)
            return;

        var water = chunk.Water;
        var density = chunk.Density;

        for (var z = -1; z <= ChunkConstants.Size; z++)
        {
            var dz = origin.Z + z * scale - lake.Centre.Y;

            for (var x = -1; x <= ChunkConstants.Size; x++)
            {
                var dx = origin.X + x * scale - lake.Centre.X;

                if (dx * dx + dz * dz > r2)
                    continue;

                for (var y = -1; y <= ChunkConstants.Size; y++)
                {
                    if (origin.Y + y * scale > lake.Top)
                        break;

                    var i = ChunkConstants.Index(x, y, z);

                    if (density[i] >= 0)
                        water[i] = byte.MaxValue;
                }
            }
        }
    }

    /// <summary>
    /// Every non-solid voxel below sea level is open sea. Generated terrain has no sealed air below sea level
    /// (docs/voxel-water.md), so no connectivity check is needed here.
    /// </summary>
    public static void FillSea(VoxelChunk chunk, float seaLevel)
    {
        const int padded = ChunkConstants.Padded;
        var water = chunk.Water.AsSpan();
        var density = chunk.Density;
        var originY = chunk.Coord.WorldOrigin.Y;
        var scale = chunk.Coord.VoxelScale;

        for (var y = -1; y <= ChunkConstants.Size; y++)
        {
            var worldY = originY + y * scale;

            for (var z = -1; z <= ChunkConstants.Size; z++)
            {
                var row = ChunkConstants.Index(-1, y, z);
                var waterRow = water.Slice(row, padded);

                if (worldY >= seaLevel)
                {
                    waterRow.Clear();
                    continue;
                }

                for (var x = 0; x < padded; x++)
                    waterRow[x] = density[row + x] >= 0 ? byte.MaxValue : (byte)0;
            }
        }
    }

    [SkipLocalsInit]
    private static unsafe void GenerateSolids(VoxelChunk chunk, in TerrainParams p)
    {
        var origin = chunk.Coord.WorldOrigin;
        var s = chunk.Coord.VoxelScale;
        var yLow = origin.Y - s;
        var yHigh = origin.Y + ChunkConstants.Size * s;
        var clamp = ClampMetres * s;
        var reach = clamp + p.OverhangAmplitude + s;

        if (yLow > MaxHeight(p) + reach)
        {
            chunk.Fill(sbyte.MaxValue, VoxelMaterial.Air);
            return;
        }

        if (yHigh < MinHeight(p) - reach)
        {
            chunk.Fill(-sbyte.MaxValue, VoxelMaterial.Rock);
            return;
        }

        var islands = CreateIslands(p);
        var heights = stackalloc float[HeightArea];

        for (var hz = 0; hz < HeightStride; hz++)
        {
            var wz = origin.Z + (hz - 2) * s;
            var row = heights + hz * HeightStride;

            for (var hx = 0; hx < HeightStride; hx++)
                row[hx] = Height(origin.X + (hx - 2) * s, wz, p, islands);
        }

        var colH = stackalloc float[ColumnArea];
        var colInv = stackalloc float[ColumnArea];
        var colAmp = stackalloc float[ColumnArea];
        var colSteep = stackalloc bool[ColumnArea];
        var minH = float.MaxValue;
        var maxH = float.MinValue;
        var maxAmp = 0f;
        var steep2 = p.SteepSlope * p.SteepSlope;
        var halfInvS = 0.5f / s;

        for (var z = 0; z < P; z++)
        {
            for (var x = 0; x < P; x++)
            {
                var h = heights + (z + 1) * HeightStride + (x + 1);
                var gx = (h[1] - h[-1]) * halfInvS;
                var gz = (h[HeightStride] - h[-HeightStride]) * halfInvS;
                var g2 = gx * gx + gz * gz;
                var c = x + z * P;
                var amp = p.OverhangAmplitude * Saturate((h[0] - OverhangStartHeight) / OverhangRampHeight);
                colH[c] = h[0];
                colInv[c] = 1f / MathF.Sqrt(1f + g2);
                colAmp[c] = amp;
                colSteep[c] = g2 > steep2;
                minH = MathF.Min(minH, h[0]);
                maxH = MathF.Max(maxH, h[0]);
                maxAmp = MathF.Max(maxAmp, amp);
            }
        }

        // Around the crater lake the ground carries no overhang caves (one under the rim could open below the water line).
        if (islands.LakeRadius > 0f && maxAmp > 0f)
        {
            maxAmp = 0f;

            for (var z = 0; z < P; z++)
            {
                var dz = origin.Z + (z - 1) * s;

                for (var x = 0; x < P; x++)
                {
                    var dx = origin.X + (x - 1) * s;
                    var c = x + z * P;
                    colAmp[c] *= Smooth01((MathF.Sqrt(dx * dx + dz * dz) - islands.LakeRadius - LakeSolidRim) / LakeSolidRamp);
                    maxAmp = MathF.Max(maxAmp, colAmp[c]);
                }
            }
        }

        var colReach = clamp + maxAmp + s;

        if (yLow > maxH + colReach)
        {
            chunk.Fill(sbyte.MaxValue, VoxelMaterial.Air);
            return;
        }

        if (yHigh < minH - colReach)
        {
            chunk.Fill(-sbyte.MaxValue, VoxelMaterial.Rock);
            return;
        }

        var lattice = stackalloc float[LatticeVolume];
        var latIdx = stackalloc int[P];
        var latT = stackalloc float[P];
        var band = p.OverhangBand;
        var invBand = 1f / band;
        var useOverhang = maxAmp > 0f && yLow < maxH + band && yHigh > minH - band;

        if (useOverhang)
        {
            var f = p.OverhangFrequency;
            var seed = islands.Seed + OverhangSalt;
            var step = LatticeStep * s;

            for (var lz = 0; lz < LatticeAxis; lz++)
            {
                var wz = (origin.Z + (lz - 1) * step) * f;

                for (var ly = 0; ly < LatticeAxis; ly++)
                {
                    var wy = (origin.Y + (ly - 1) * step) * f;
                    var row = lattice + (lz * LatticeAxis + ly) * LatticeAxis;

                    for (var lx = 0; lx < LatticeAxis; lx++)
                        row[lx] = Noise.Fbm3((origin.X + (lx - 1) * step) * f, wy, wz, 2, seed);
                }
            }

            for (var i = 0; i < P; i++)
            {
                var l = i - 1 + LatticeStep;
                latIdx[i] = l / LatticeStep;
                latT[i] = (l % LatticeStep) * (1f / LatticeStep);
            }
        }

        var beach = p.BeachHeight;
        var quant = ChunkConstants.DensityScale / s;
        var flags = 0;

        fixed (sbyte* density = chunk.Density)
        fixed (byte* material = chunk.Material)
        {
            for (var z = 0; z < P; z++)
            {
                for (var y = 0; y < P; y++)
                {
                    var wy = origin.Y + (y - 1) * s;
                    var vi = z * ChunkConstants.PaddedArea + y * P;
                    var colRow = z * P;
                    var sandLayer = wy < beach;

                    for (var x = 0; x < P; x++, vi++)
                    {
                        var c = colRow + x;
                        var dy = wy - colH[c];
                        var dm = dy * colInv[c];
                        var amp = colAmp[c];
                        var limit = clamp + amp;

                        if (dm > limit)
                        {
                            density[vi] = sbyte.MaxValue;
                            material[vi] = (byte)VoxelMaterial.Air;
                            flags |= 2;
                            continue;
                        }

                        if (dm < -limit)
                        {
                            density[vi] = -sbyte.MaxValue;
                            material[vi] = (byte)VoxelMaterial.Rock;
                            flags |= 1;
                            continue;
                        }

                        var carve = 0f;

                        if (useOverhang && amp > 0f)
                        {
                            var w = 1f - MathF.Abs(dy) * invBand;

                            if (w > 0f)
                                carve = amp * w * SampleLattice(lattice, latIdx, latT, x, y, z);
                        }

                        var dist = dm + carve;
                        var q = (int)MathF.Round(dist * quant);
                        q = Math.Clamp(q, -sbyte.MaxValue, sbyte.MaxValue);
                        density[vi] = (sbyte)q;

                        if (q >= 0)
                        {
                            material[vi] = (byte)VoxelMaterial.Air;
                            flags |= 2;
                            continue;
                        }

                        flags |= 1;
                        material[vi] = (byte)PickMaterial(-dist, sandLayer, colSteep[c] || MathF.Abs(carve) > CarvedRock);
                    }
                }
            }
        }

        chunk.HasSurface = flags == 3;
        chunk.Version++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static VoxelMaterial PickMaterial(float depth, bool sand, bool rocky)
    {
        if (depth >= SubSurfaceLayer)
            return VoxelMaterial.Rock;

        if (sand)
            return VoxelMaterial.Sand;

        if (rocky)
            return VoxelMaterial.Rock;

        return depth < SurfaceLayer ? VoxelMaterial.Grass : VoxelMaterial.Dirt;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float SampleLattice(float* lattice, int* idx, float* t, int x, int y, int z)
    {
        var ix = idx[x];
        var iy = idx[y];
        var iz = idx[z];
        var tx = t[x];
        var ty = t[y];
        var tz = t[z];
        var p00 = lattice + (iz * LatticeAxis + iy) * LatticeAxis + ix;
        var p10 = p00 + LatticeAxis;
        var p01 = p00 + LatticeAxis * LatticeAxis;
        var p11 = p01 + LatticeAxis;
        var a = Lerp(Lerp(p00[0], p00[1], tx), Lerp(p10[0], p10[1], tx), ty);
        var b = Lerp(Lerp(p01[0], p01[1], tx), Lerp(p11[0], p11[1], tx), ty);
        return Lerp(a, b, tz);
    }

    private static float Height(float x, float z, in TerrainParams p, in IslandSet s)
    {
        var seed = s.Seed;
        var seabed = p.SeabedDepth + Noise.Fbm2(x * p.SeabedFrequency, z * p.SeabedFrequency, 3, seed + SeabedSalt) * p.SeabedUndulation;
        var h = seabed;
        var sampled = false;
        var warp = 0f;
        var cliff = 0f;
        var detail = 0f;
        var reachScale = 1.5f / (1f - MathF.Min(p.CoastWarp, 0.9f));

        for (var i = 0; i < s.Count; i++)
        {
            var isl = s.Items[i];
            var dx = x - isl.X;
            var dz = z - isl.Z;
            var d2 = dx * dx + dz * dz;
            var reach = isl.Radius * reachScale;

            if (d2 >= reach * reach)
                continue;

            if (!sampled)
            {
                warp = Noise.Fbm2(x * p.CoastFrequency, z * p.CoastFrequency, 3, seed + CoastSalt) * p.CoastWarp;
                cliff = Smooth01((Noise.Fbm2(x * p.CliffFrequency, z * p.CliffFrequency, 2, seed + CliffSalt) - 0.05f) * 2.5f);
                detail = Noise.Fbm2(x * p.DetailFrequency, z * p.DetailFrequency, p.DetailOctaves, seed + DetailSalt);
                sampled = true;
            }

            var t = 1f - MathF.Sqrt(d2) / isl.Radius * (1f + warp);
            float e;

            if (t < 0f)
            {
                // A gently sloping shelf out from the waterline (sandy beaches run about 1:10-1:80 through the surf), steepening
                // offshore, blended into the open seabed over the outer part of the island's reach.
                var offshore = -t * isl.Radius;
                var shelf = MathF.Max(-offshore * p.BeachSlope * (1f + offshore / p.ShelfLength), seabed);
                var fade = Smooth01((offshore / ((reachScale - 1f) * isl.Radius) - 0.3f) / 0.7f);
                e = shelf + (seabed - shelf) * fade;
            }
            else
            {
                var tl = MathF.Min(t, 1f);
                var baseShape = Smooth01(tl);
                var cliffShape = 0.45f * Smooth01(tl * 12.5f) + 0.55f * baseShape;
                var shape = baseShape + (cliffShape - baseShape) * cliff;
                var detailScale = p.DetailHeight * Smooth01(tl * 3.3333f) * (isl.Height / p.IslandHeight);
                e = isl.Height * shape + detail * detailScale;
            }

            if (e > h)
                h = e;
        }

        if (s.LakeRadius > 0f)
        {
            var r = MathF.Sqrt(x * x + z * z);

            if (r < s.LakeReach)
                h = MathF.Min(h, LakeBed(r, detail, p, s));
        }

        return h;
    }

    /// <summary>
    /// The crater's ground at distance r from its centre: a bowl from the surface at the shore down to the full depth at the
    /// centre, then banks rising at <see cref="TerrainParams.LakeBankSlope"/> until the island's own ground is lower. The
    /// banks take the island's detail gradually, so they never dip under the water line; the bed a little of it.
    /// </summary>
    private static float LakeBed(float r, float detail, in TerrainParams p, in IslandSet s)
    {
        if (r < s.LakeRadius)
        {
            var u = r / s.LakeRadius;
            return s.LakeSurface - s.LakeDepth * (1f - u * u) + detail * LakeBedDetail * (1f - u);
        }

        var bank = r - s.LakeRadius;
        return s.LakeSurface + bank * s.LakeBank + detail * p.DetailHeight * Smooth01(bank / LakeBankRamp);
    }

    private static IslandSet CreateIslands(in TerrainParams p)
    {
        var s = new IslandSet { Seed = Noise.SeedOf(p.Seed) };
        s.Items[0] = new Island { X = 0f, Z = 0f, Radius = p.IslandRadius, Height = p.IslandHeight };

        var h = Noise.Mix(s.Seed ^ IslandSalt);
        var small = 2 + (int)(h & 1);
        var baseAngle = (h >> 8) * (MathF.Tau / 16777216f);
        var sector = MathF.Tau / small;

        for (var i = 0; i < small; i++)
        {
            var hi = Noise.Mix(s.Seed + (uint)(i + 1) * 0x68E31DA4u);
            var u0 = (hi & 0xFFFF) / 65535f;
            var u1 = ((hi >> 16) & 0xFF) / 255f;
            var u2 = (hi >> 24) / 255f;
            var angle = baseAngle + i * sector + (u0 - 0.5f) * 0.4f * sector;
            var dist = p.IslandRadius * (1.7f + 0.9f * u1);
            s.Items[i + 1] = new Island
            {
                X = MathF.Cos(angle) * dist,
                Z = MathF.Sin(angle) * dist,
                Radius = p.IslandRadius * (0.25f + 0.2f * u2),
                Height = p.IslandHeight * (0.35f + 0.35f * u0),
            };
        }

        s.Count = small + 1;

        // The lake's water stands under the lowest the main island's ground can be at its shore: the shape with the coast
        // warp at its worst (cliffs only raise it) less the full detail, rounded down to a voxel layer with 1.5 m to spare.
        if (p.LakeRadius > 0f)
        {
            var t = 1f - p.LakeRadius / p.IslandRadius * (1f + MathF.Min(p.CoastWarp, 0.9f));
            var lowest = p.IslandHeight * Smooth01(t) - p.DetailHeight;
            s.LakeRadius = p.LakeRadius;
            s.LakeSurface = MathF.Floor(lowest - 2f) + 0.5f;
            s.LakeDepth = p.LakeDepth;
            s.LakeBank = p.LakeBankSlope;
            s.LakeReach = p.LakeRadius + (MaxHeight(p) - s.LakeSurface) / p.LakeBankSlope;
        }

        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Saturate(float v) => Math.Clamp(v, 0f, 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Smooth01(float v)
    {
        v = Saturate(v);
        return v * v * (3f - 2f * v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
