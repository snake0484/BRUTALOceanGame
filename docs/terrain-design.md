# Voxel terrain design (v1)

Scope of v1: procedural island terrain, chunk storage, generation and meshing on jobs, streaming around the camera, Vulkan rendering, and a bathymetry grid for water. Not yet: edits, compression, LOD, Jolt collision.

## Units and conventions

- Voxel size 1 m, chunk = 32³ voxels (32 m). Y is up. Sea level is y = 0.
- `ChunkCoord(X, Y, Z)` integer chunk index. World origin of a chunk = coord × 32.
- Density is a clamped signed distance: **negative = solid, positive = air**. Stored as `sbyte`. The value is the distance in metres × `DensityScale` (= 32, so ±4 m range), clamped to [-127, 127]. The surface is the 0 crossing.
- Vertical world range for v1 is chunk Y ∈ [−2, 2] (−64 m … +96 m).

## Storage (`BrutalGame.Terrain`, project `src/Terrain`, no Vulkan dependency)

```csharp
public static class ChunkConstants
{
    public const int Size = 32;
    public const int Padded = Size + 2;               // apron of 1 voxel on each side: local index -1 .. 32
    public const int PaddedVolume = Padded * Padded * Padded;
    public const float VoxelSize = 1f;
    public const float DensityScale = 32f;
    public static int Index(int x, int y, int z) => (x + 1) + (y + 1) * Padded + (z + 1) * Padded * Padded; // x,y,z in [-1, 32]
}

public readonly record struct ChunkCoord(int X, int Y, int Z)
{
    public Vector3 WorldOrigin { get; }   // (X, Y, Z) * Size * VoxelSize
}

public sealed class VoxelChunk
{
    public ChunkCoord Coord;
    public readonly sbyte[] Density = new sbyte[ChunkConstants.PaddedVolume];
    public readonly byte[] Material = new byte[ChunkConstants.PaddedVolume];
    public bool HasSurface;   // false if every padded sample has the same sign (all air or all solid): skip meshing
    public int Version;       // bumped on regenerate/edit
}

public enum VoxelMaterial : byte { Air = 0, Rock = 1, Sand = 2, Grass = 3, Dirt = 4 }
```

Chunks are pooled and reused, never allocated per frame. The apron is generated procedurally, so each chunk can be meshed alone. Later, edits will write into neighbours' aprons.

## Generation

```csharp
public struct TerrainParams()
{
    public ulong Seed = 1;
    public float SeabedDepth = -30f;     // open-sea floor height
    public float IslandRadius = 220f;
    public float IslandHeight = 55f;
    // plus fBm octave settings, as the implementation needs
}

public static class TerrainGenerator
{
    // Thread-safe, deterministic, allocation-free. Fills the full padded volume + HasSurface.
    public static void Generate(VoxelChunk chunk, in TerrainParams p);

    // Analytic height (top of the land/seabed) at world x,z. Used by generation; bathymetry may use it as a fast path.
    public static float SurfaceHeight(float x, float z, in TerrainParams p);
}
```

- **Shape:** one main island at the origin plus 2–3 smaller islands (positions derived from the seed) on a seabed with low-frequency undulation. Use fBm coherent noise (own deterministic implementation, e.g. 2D/3D simplex or gradient noise; no System.Random). Island radial falloff gives beaches near sea level and cliffs in places.
- **Overhangs:** add a 3D noise term near the surface band only (|y − height| < ~12 m), to give overhangs and arches without a full 3D cost.
- **Speed:** compute a 34×34 column height map first, then fill the volume. Columns far above or below the surface clamp to ±127 without noise. Chunks entirely above max height or below min height should exit early with `HasSurface = false`.
- **Materials:**
  - Sand below about +2 m and on the seabed.
  - Grass on gentle slopes above that.
  - Rock on steep slopes and cliffs.
  - Dirt as the sub-surface layer.

## Meshing: Surface Nets

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex      // 28 bytes, matches the GPU vertex input
{
    public Vector3 Position;     // chunk-local metres
    public Vector3 Normal;       // normalised, pointing into air
    public uint Material;
}

public sealed class ChunkMesh  // reusable output buffers; grows capacity when needed, never shrinks
{
    public TerrainVertex[] Vertices;
    public int VertexCount;
    public uint[] Indices;
    public int IndexCount;
    public void Clear();
}

public sealed class SurfaceNetsMesher   // one per worker thread; not thread-safe; zero allocations after warm-up
{
    public void Mesh(VoxelChunk chunk, ChunkMesh output);
}
```

- **Vertices:** one per cell with a sign change, placed at the average of the edge crossings (classic Surface Nets).
- **Normal:** the trilinear gradient of the cell's 8 corner densities, pointing toward positive (air).
- **Material:** the most common solid material among the cell's corners.
- **Watertight seams:** a chunk emits quads only for crossing edges whose base corner lies in local [0, 32). Vertices for apron cells (local index −1) are computed as needed. Two neighbouring chunks therefore produce bit-identical positions on their shared boundary, and each boundary quad is emitted exactly once.
- **Winding:** counter-clockwise when viewed from the air side, with Vulkan's y-down clip handled by the projection matrix. Equivalently: the triangle normal (cross(b − a, c − a)) points into air, i.e. agrees with the vertex normal.

## Tests (`tests/Terrain.Tests`, xunit, same setup as `tests/Water.Tests`)

1. **Generation is deterministic:** the same seed gives identical bytes, and the generator is thread-safe (parallel generation equals serial).
2. **Sphere SDF mesh:**
   - Fill a chunk with a sphere of radius 10 at its centre and mesh it.
   - Every vertex lies within ~0.6 m of radius 10.
   - Every normal points outward (dot with the radial direction > 0.8).
   - Every triangle's winding agrees with its normal.
   - The mesh is closed: every edge is shared by exactly two triangles.
3. **Seam:** two adjacent chunks cut through a plane or sphere. Positions on the shared boundary match exactly, and no boundary quad is duplicated or missing (combined edge-manifold check across both meshes, offsetting the second by 32).
4. **Empty and full chunks** produce no geometry and set `HasSurface = false`.
5. **Mesher allocations:** after warm-up, `GC.GetAllocatedBytesForCurrentThread` delta is 0 across 10 Mesh calls.

## Streaming and jobs (main thread: `TerrainWorld` in `src/Terrain`)

- Each frame, the camera's chunk sets the desired set: a horizontal radius R (v1: 12 chunks) and all Y layers in the vertical range.
- Missing chunks are queued by distance to a `JobScheduler` (Brutal.Concurrency). The job generates and meshes the chunk, using a pooled job object that owns a `VoxelChunk`, a `SurfaceNetsMesher` and a `ChunkMesh`.
- **Batch model:**
  - When the scheduler is idle, collect finished jobs (upload their meshes, return them to the pool).
  - Queue up to `MaxJobsPerBatch` new ones and call `ExecuteJobs()`.
  - Never `Wait()` on the main thread.
- **Unloading:** chunks beyond R + 2 are unloaded (GPU mesh freed, voxel data released to the pool).

## Rendering

- A `TerrainRenderer` owns one device-local vertex arena and one index arena, sub-allocated with a free list.
- Uploads go through a host-visible staging ring with a copy before the main pass.
- One `DrawIndexed` per visible chunk, with chunk origin as a push constant. Frustum culling on the chunk AABB.
- **Shaders:** `terrain.vert`/`terrain.frag` share the ocean's sun/sky/fog parameters through a small UBO, so lighting and fog match. The material colour palette is in the shader for now.

## Bathymetry

- `BathymetryGrid` is a 2D grid (cell 2 m) covering the streamed area, centred on the camera and snapped.
- **Value:** water depth = max(0, seaLevel − seabedHeight), where seabedHeight is the topmost solid surface in that column. It is taken from loaded chunk densities (first downward zero crossing), so edits will be respected later. Columns without loaded data fall back to `TerrainGenerator.SurfaceHeight`.
- **Updates:** columns are rebuilt when their chunks finish loading or change. The CPU side exposes `SampleDepth(x, z)`, and the grid is uploaded to the GPU as an R16F texture when it changes.
- **Ocean use in v1:**
  - Shore foam and shallow-water tint in `ocean.frag`.
  - Damping of FFT and CPU wave amplitude where depth falls toward 0, so waves don't pass through beaches. This is only a placeholder until the Layer B coastal maps (`docs/water-architecture.md`).
