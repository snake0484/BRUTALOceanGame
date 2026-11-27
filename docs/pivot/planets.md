## Planets: ~1000 km voxel spheres on the existing terrain system

### Recommendation (summary)

- **Grid:** keep the voxel grid **cartesian and axis-aligned in the planet's rotating body frame**: 1 m voxels, 32³ chunks, the current octree of chunks (`ChunkCoord` with a `Lod`). Only the SDF changes: density = local surface height − radial altitude.
  - The mesher, skirts, edits, packed vertex, Jolt collision and the retire/streaming machinery all carry over unchanged.
  - Space Engineers' planets work this way.
- **Shell:** voxels exist only in a **shell** around the surface. Selection classifies chunks outside it as uniform solid or air using a per-planet height-bounds pyramid, with no job and no storage. A chunk is generated deeper only where an edit reaches.
- **Far LOD: one octree all the way, not a near-voxel / far-quadtree hybrid.** Leaves go up to LOD 12 (4 km voxels, 131 km chunks). LOD 13–15 are internal nodes only. Beyond about 800 km altitude the octree unloads and the planet is a **baked cube-sphere impostor**, so there is no voxel/quadtree seam to solve.
- **Storage:** nothing is stored except edits, and edits are **permanent** (user decision D32). Ops are the edit, network and persistence unit, as now (`BrushOp` replay). Busy LOD-0 chunks compact into zlib snapshots. Growth bounds: "Edits, storage and network".
- **Limits on digging:** shores and seabeds are unminable (a precise protected band), and rock heats with depth until digging past ~1.7 km is impractical (D29, D30).
- **Weather:** the pressure-system weather is ported to the sphere (D31).
- **Precision:** planet frame in `double3`. Chunk origins are integer × size, so they are exact. Everything inside a chunk is float, relative to the chunk centre. Rendering and physics are camera-relative or zone-relative floats.
- **Ocean:** a sea sphere at `SeaRadius`, drawn where an analytic sea mask says "sea". No voxel water.
  - Shore wash is kept. It runs as now on a 1 km tangent-plane depth map around the camera, which now holds **radial** depth, with a cheap replacement for the coastal carrier.

### Detail ladder (user direction, 2026-11-25)

A planet's untouched surface is a pure function of seed + position, as the island's was before the pivot. No edits means no storage. Since it is a function, quality is just how much of it is evaluated, so detail scales smoothly with distance. Edits apply only close up.

| Stage | When (planet's screen size / distance) | What is drawn | Cost |
| --- | --- | --- | --- |
| 1. Point of light | Under ~2 px | One point sprite: brightness from radius, albedo, phase angle to the sun and distance. Batched for every body in the system | ~0 (one instanced draw for all points) |
| 2. Disc | ~2-64 px | The impostor sphere at its smallest mip: albedo, terminator, atmosphere rim. No relief | One draw per body |
| 3. Rough outline | ~64 px up to ~800 km altitude | The baked impostor (256² per face, 1024² for the focus body) with the atmosphere shell. The silhouette comes from the impostor height, so mountains show on the limb | 1.2 MB per body; ~0.1-0.2 ms atmosphere |
| 4. Function terrain | Below ~800 km | The voxel octree (LOD 12 down to 0). Each LOD evaluates only the noise octaves its voxel size can show (band-limited: octaves with wavelength < ~2 voxels are skipped). Coarse chunks are cheap; full detail only at LOD 0-2 | Per-chunk generation as in "Generation" |
| 5. Edits | Close up only (see below) | The same chunks with `BrushOp` replay | Edit scan only on near chunks |

**Edits are applied only where they can be seen.** An op of radius `r` is applied to a chunk at LOD `L` only if `r ≥ k · voxelSize(L)` (proposed `k = 2`) and the chunk is within the edit radius (proposed 8 km, a knob). So:
- A 2 m hole shows from a few hundred metres at most, a 200 m quarry from much further, and a planet-scale crater (if one ever exists) at every LOD.
- Coarse chunks skip almost every op, which also fixes the edit scan cost (review P3) for the far rings.
- The impostor (stages 1-3) never sees edits. A very large edit can be baked into the focus body's impostor if needed later.
- Collision is unaffected: it only exists near players, where edits are always applied.

`EditStore` already splits ops into size tiers; the tier gives the coarsest LOD an op reaches, so the per-chunk check is a table lookup, not a scan.

Transitions between stages are hidden by blending, not popping: stage 1 to 2 crossfades by pixel size, the atmosphere covers the impostor-to-octree switch (as above), and a chunk's edits fade in over its LOD morph.

### Why cartesian voxels, not a warped cube-sphere

- Every existing system assumes uniform cubic voxels and flat 32³ arrays: Surface Nets with bit-exact seams, the 12 B packed vertex, sphere brushes in metres, aprons, skirts, Jolt mesh shapes and the "finer leaf is inside iff its origin is" coverage test. Cartesian keeps all of them.
- The SDF does not care that "down" is diagonal to the grid. Surface Nets with sbyte SDF at 1/32 m and vertices snapped to 1/256 voxel gives smooth tilted ground.
- Cost: a tilted surface crosses about 1.0–1.7× as many chunks as an axis-aligned one, so about 1.4× more surface leaves on average. That is acceptable.

Rejected alternatives:
- **Cube-sphere warped voxels:** non-uniform voxels, warped brushes, cube edges and corners need rotated aprons and special seams, and mesher and collision both need the warp. That rewrites everything that works today.
- **Near voxels + far cube-sphere heightfield quadtree:** a second geometry path, a seam band (discard caps, overlap, height agreement) and edits invisible past the band. It would only save about 4× per far patch, and far rings churn as 1/size, so it is a small share of total cost.
- **Full-depth octree to the core:** 2.5e17 LOD-0 voxels in a 20 km shell. Procedural plus edits only is the only option.
- **Storing generated chunks:** about 1.7e10 surface LOD-0 chunks. Regenerate from seed.

### Numbers for a 1000 km planet at 1 m voxels

| Quantity | Value |
|---|---|
| Surface area | 4πR² = 1.26e13 m² |
| LOD-0 chunks crossing the surface | ≈ 1.26e13 / 1024 × 1.4 ≈ **1.7e10** (never stored) |
| Relief (decided, D29) | −8 km (sea floor) … +9 km; dig limit 2 km under the local surface, with heat making the last ~250 m impractical |
| LOD-0 chunk coord range | ±(1,011 km / 32 m) = ±31,600; voxel coords ±1.01e6 (int32 fine) |
| Octree | LOD 0..15 (LOD 15 = 1,048 km). 8 roots at LOD 15, coords −1..0, planet centre at the origin |
| Leaf LODs | 0..12. LOD 13–15 always split, because a leaf spanning > ~8° of arc breaks the gnomonic height grid |
| Storage / network key | `ulong`: 3 × 18-bit signed + 4-bit LOD + 6 spare. Covers planets up to ~4,000 km radius at 1 m |

### Types (sketch)

```csharp
// Data-driven per body; generated from the system seed or overridden in data/systems/*.json.
public readonly record struct PlanetParams(
    ulong Seed, uint GeneratorVersion,
    double Radius,              // datum sphere (m)
    double SeaRadius,           // NaN = no ocean
    float MinHeight, float MaxHeight,   // relief bounds about Radius (m), e.g. -8000, +9000
    float DigDepth,             // 2000: how far below the local surface chunks may exist
    float HeatOnsetDepth, float HeatExcessK,   // 800, 900: deep-rock heat (see "Deep heat")
    float ShoreProtectHeight,   // 6: unedited ground at or below sea + this is unminable (see "Protected shores and seabeds")
    double SurfaceGravity,      // m/s^2 at Radius -> GM = g R^2
    double RotationPeriod, double3 SpinAxis, double RotationPhase0,
    BiomeSetId Biomes, AtmosphereId Atmosphere);

public readonly record struct ChunkCoord(int X, int Y, int Z, int Lod = 0)
{
    public double SizeMetres => 32.0 * (1 << Lod);
    public double3 Origin => new double3(X, Y, Z) * SizeMetres;          // planet frame, exact
    public double3 Centre => Origin + 0.5 * SizeMetres;
    public ulong Key => Pack18(X) | Pack18(Y) << 18 | Pack18(Z) << 36 | (ulong)Lod << 54;
    public static ChunkCoord FromPlanet(double3 p, int lod);              // replaces float FromWorld
}

/// Built once per job: the chunk's tangent frame. Everything per-voxel is float relative to Centre.
public struct ChunkFrame
{
    public double CentreRadius;      // |Centre|
    public float3 Up, T1, T2;        // Up = normalize(Centre)
    public float AltitudeOfCentre;   // (float)(CentreRadius - Radius), computed in double
}

/// Per planet, built on jobs at system load: conservative min/max surface height per cube-sphere texel
/// (6 x 256^2, mip pyramid), coarse octaves evaluated plus the analytic sum of finer octave amplitudes.
/// 1.5 MB per planet. Lets selection classify a node as Air / Solid / Surface with no job.
public sealed class PlanetHeightBounds
{
    public NodeClass Classify(in ChunkCoord node, ReadOnlySpan<EditBounds> edits);
    public float MaxHeightAround(double3 dir, double angle); // for horizon pruning
}

public readonly record struct BrushOp(double3 Centre, float Radius, BrushMode Mode, VoxelMaterial Material,
                                      uint Seq, ushort Author);    // 40 B; network and persistence unit

/// Edits: three tiers so a coarse chunk never scans thousands of small ops.
public sealed class EditStore
{
    Dictionary<ulong, OpBucket> _fine;      // key = LOD-5 cell (1 km); every op; read by LOD 0 leaves (k = 2: smaller ops never show coarser)
    Dictionary<ulong, OpBucket> _coarse;    // key = LOD-10 cell (32 km); ops with Radius >= 4 m; LOD 1..5
    Dictionary<ulong, OpBucket> _huge;      // key = LOD-10 cell (32 km), op filed in every cell its AABB meets; Radius >= 32 m; LOD >= 6
                                            // (keyed, not one list: edits are permanent, so a flat list would grow every coarse leaf's scan forever)
    Dictionary<ulong, ChunkSnapshot> _baked;// LOD-0 chunks compacted after 64 ops; LOD-1..5 chunks baked by coarse compaction (zlib via Brutal.ZLib)
}

public struct CollisionAnchor { public double3 Position; public double3 Velocity; public float Radius; }
```

`VoxelChunk` drops `Water` (−39 KB per pooled chunk). `ChunkConstants.MinChunkY/MaxChunkY` go, replaced by the shell test. `TerrainParams` (the island, lake and river fields) is replaced by `PlanetParams`.

### Generation (`PlanetGenerator`, replaces `TerrainGenerator`)

**Height function `H(dir)`:**
- 3D gradient fBm evaluated at `dir × Radius`. 3D noise on the sphere means no cube-face seams.
- `Noise` gets a **double-lattice** variant: the cell index comes from `Math.Floor(double)` as `long` and is hashed, the fraction is float. At 1e6 m a float has a 6 cm ulp, which would step the 1/16 m-frequency detail.
- A continent mask in the low octaves sets ocean basins against land. Where the mask is past its coastal ramp (inland), ground is clamped ≥ sea + `ShoreProtectHeight` + 1 m (the same trick as the crater lake rim, raised from sea + 1 m), so there are no dry pits below sea level that the sea sphere would flood, and the only ground at or below sea + `ShoreProtectHeight` is seabed or coast joined to the sea. That makes the protected band a pure height test.

**Per chunk:**
1. Build `ChunkFrame`.
2. **2D height grid** in the chunk's gnomonic tangent plane: 48² samples at voxel spacing, wide enough to cover the cube's projected footprint.
   - Octaves with wavelength > 4 × chunk size are evaluated on a 9×9 grid and interpolated.
   - Only the ~8 finer octaves run at full grid density.
   - Octaves shorter than 2 voxels are skipped. This is what makes coarse LODs cheap.
3. **Volume fill**, per voxel, from local `l` (float, relative to the centre):
   - Altitude: `w = AltitudeOfCentre + dot(l, Up) + (|l|² − dot(l, Up)²) / (2·CentreRadius)`. This is exact to µm in float, with no per-voxel double.
   - Gnomonic coordinates: `u = r_c·dot(l, T1) / (r_c + dot(l, Up))`, and likewise `v` with `T2`.
   - Density: `density = clamp((H_bilinear(u, v) − w) / s × 32)`. The 8-wide SIMD loop needs no sqrt.
   - The existing overhang term runs only inside the surface band.
4. **Materials:** slope = 1 − the grid gradient magnitude in the tangent frame, plus altitude, latitude (`dot(Up, SpinAxis)`) and the biome. This is the existing `PickMaterial` with new inputs.
5. **Early outs:** the `PlanetHeightBounds` classify in selection, plus min/max of the grid against the chunk's `w` range in the job.
6. Replay ops (the tier chosen by LOD; ops with `Radius < s/2` are skipped at LOD ≥ 3), or load a snapshot and then replay later ops, then mesh, then collision if anchored. In columns flagged protected (step 2 sets a 32×32-bit mask, 128 B, from the height grid), removal is clamped to the generated density (see "Protected shores and seabeds").

**Estimated cost per surface leaf on one core** (estimates; verify with `LastBatchMs / LastBatchChunks` plus per-stage counters before committing to budgets):

| Stage | Cost |
|---|---|
| Height grid (2304 samples × 8 octaves × ~12 ns) | ~0.22 ms |
| Volume fill (39k voxels, SIMD) | ~0.15 ms |
| Overhang band | ~0.05–0.1 ms |
| Surface Nets | ~0.1–0.2 ms |
| **Total, render-only leaf** | **≈ 0.5–0.7 ms** |
| Jolt `MeshShape` build (LOD 0 inside an anchor ring only) | +0.3–0.8 ms |
| Uniform leaves | 0 (no job) |

Determinism: the generator uses only add, mul, floor and sqrt (no `MathF.Sin/Exp`), so server and client grids match. The server's collision is authoritative regardless.

### Streaming at flight speeds

The split rule is unchanged (split LOD L when distance < 3 × size_L). LOD-L leaves therefore exist only within about 6·size_L. The finest LOD present at altitude h is L0 ≈ ⌈log2(h / 192)⌉: LOD 0 only below about 200 m above ground.

New leaves per second ≈ 12·v / size_L summed over the rings ≈ **1.1·v / 2^L0**, ×1.4 for tilt.

A speed bias keeps the finest ring's fill latency under 0.5 s: `MinLod = max(L0, ⌈log2(v / 384)⌉)`. That gives LOD 0 to 380 m/s, LOD 3 at 2 km/s and LOD 5 at 10 km/s.

| Case | L0 | Leaves/s | Worker load (0.6 ms/leaf) |
|---|---|---|---|
| On foot / rover 30 m/s | 0 | ~45 | 0.03 core |
| Low flight 300 m/s, 100 m AGL | 0 | ~460 | 0.28 core (+ collision ring along the path) |
| 300 m/s at 1 km AGL | 3 | ~60 | negligible |
| Re-entry 2 km/s at 20 km | 7 | ~25 | negligible |
| 10 km/s at 20 km (speed bias L5–7) | 7 | ~120 | 0.07 core |
| Vertical descent, orbit to ground | — | about 16 levels × ~200 leaves ≈ 3,200 in total, the last 4 levels (~800) in the final ~10 s | fine |

Throughput is not the limit. `WorkerBudget.Streaming` (≈ 7 workers on 16 cores) does about 10k leaves/s. Main-thread work is.

### Main-thread work per frame (client)

| Item | Cost |
|---|---|
| Leaf select: ~6k node visits × ~40 ns when the camera crosses ¼ of its finest active chunk (every frame at 10 km/s) | ≈ 0.25 ms → **move it to a job**, main thread 0.02 ms to swap the desired set |
| Retire (existing budget) | ≤ 0.5 ms |
| Uploads, typically 5–30 leaves/frame | 0.1–0.3 ms |
| Camera-relative origins for ~2.5k leaves (double → float) into an SSBO | ~0.02 ms |
| Gravity for 64 bodies | < 0.01 ms |

- **Draws must become GPU-driven indirect.** One `DrawIndexed` per leaf is about 3 ms of command recording at 2.5k leaves. The plan is a compute frustum + horizon cull, then `vkCmdDrawIndexedIndirectCount`.
- **Horizon pruning in selection:** a leaf whose top (bounds max) is below the horizon of the `Radius + MinHeight` sphere is not even generated. It is view-independent, so it does not pop when the camera turns.

### Memory

**Per surface leaf:** about 13 KB of vertices (12 B packed) plus 25 KB of 32-bit indices plus skirts ≈ 40–60 KB. Switching to **16-bit indices** (a leaf has fewer than 65k vertices) gives ≈ 25–35 KB.

| Item | Value |
|---|---|
| Leaves at ground level (LOD 0..10, horizon pruned, 9 km peaks visible to ~134 km) | ~1,500–2,500 surface leaves → **50–85 MB GPU** with 16-bit indices (current arenas: 144 MB) |
| Same, triangles | ~3–5M tris, about a third after cull |
| Low orbit (100 km) | LOD 9..12, horizon 447 km, ~1,500 leaves |
| CPU: job pool (96 × (chunk 78 KB + mesh scratch ~200 KB)) | ~27 MB |
| CPU: chunk records (column arrays dropped, ~100 B each) | < 1 MB |
| CPU: collision ring (~150 chunks × ~60 KB) | ~9 MB |
| Per planet: height bounds | 1.5 MB |
| Per planet: impostor at 256² per face (R16 height + BC1 albedo + BC5 normal) | ~1.2 MB |
| Focus body impostor upgraded to 1024² | ~19 MB |
| Edits (permanent) | 40 B per op; snapshots ~3–8 KB per compacted LOD-0 chunk; ≤ ~10.5 KB per touched chunk forever; resident only for loaded regions |

**Bodies loaded at once (client):**
- 1 **focus body** with the full octree.
- Up to 2 **secondary bodies** (moon or parent planet within ~800 km of altitude) with the octree capped at LOD ≥ 8: ~300–500 leaves each, 10–20 MB.
- **Every** body in the system as an impostor: 30 bodies × 1.2 MB ≈ 36 MB.

Total for a system: roughly 150–200 MB GPU and 50–80 MB CPU.

**Dedicated server:** no render LOD. It runs a collision-only octree per planet that has players: LOD-0 chunks around anchors, generate + collision, no render mesh kept. 64 players spread out × ~50 chunks (64 m walker ring) ≈ 3,200 × 60 KB ≈ 190 MB worst case. Clustered players are far less.

### Precision, frames, gravity, rotation

- **Planet frame:** `double3`, origin at the planet centre, rotating with the planet. Terrain never moves in it.
  - The planet's pose in the solar frame (orbit position, plus the spin angle ω·t + phase) is evaluated in double from seed + tick each tick (the tick stops while the server is empty, frames-and-zones).
  - The sun direction is transformed into the planet frame for lighting and day/night.
- **Render:** the camera position is in the planet frame (double). Each leaf's origin is sent as `(float)(origin − camera)`; the push-constant slot is kept until indirect draws land, then it becomes an SSBO. `terrain.vert` math is unchanged.
  - At LOD 12 the relative origin can reach ~2e6 m, a 0.25 m ulp against 4 km voxels: invisible.
  - Reverse-Z infinite projection is already in use (`FlyCamera`).
- **Physics** (frames-and-zones owns all of this; summarised here): the planet's physics zones are Surface bubbles in the rotating `BodySurface` frame, rebased by 32 m multiples when the cluster centroid passes 1,024 m from the origin (soft radius 2 km, hard 4 km). Chunk bodies sit at `(float)(chunkOrigin − bubbleOrigin)`.
  - Entering and leaving the rotating frame: enter below `RotatingTop = max(atmosphere top, 0.05 R)`, leave above 1.3 × `RotatingTop`. On the transition, `v_inertial = v_rot + ω × r` (the exact transfer maths).
- **Fictitious forces are applied** to awake bodies (frames-and-zones): centrifugal 0.085 m/s² at the equator and Coriolis 0.17 m/s² at 300 m/s for a 6 h day. Landed ships still sleep, because the scene gravity already includes the centrifugal term at the origin.
- **Gravity:**
  - Scene gravity is `g(origin) + centrifugal(origin)`; each awake body adds `m·(g(p) − g(origin))` plus Coriolis, evaluated in double at its centre of mass.
  - Characters take `up = −ĝ(p)` (gravity plus centrifugal), which differs from `r̂` by at most ~0.25° (at 45° latitude) on a 1 g, 6 h world.
  - Each chunk record stores `Up = normalize(Centre)` for slope classification and placement.

### Collision (landing and walking)

- Collision is LOD-0 leaves only, built on the worker (`ChunkJob.BuildShape` as now). It is driven by **anchors** instead of the camera:
  - each player (64 m radius),
  - each ship within ~200 m above ground (its AABB swept along `velocity × 1 s`, so a 300 m/s approach needs ~60 chunks/s).
- **Above ~200 m above ground:** no mesh collision. Ground proximity, CCD and landing guidance cast against the analytic `H(dir)`. Edits are ignored at that height, which is fine.
- **Fast ships near terrain:** Jolt `LinearCast` motion quality against the mesh chunks.
- Fix while in there: `StaticMeshShape.Create` allocates `new uint[]` for every chunk. It should use a pooled or job-owned buffer (zero-alloc rule).

### Edits, storage and network

- **Server-authoritative.** The client sends an edit request. The server validates it, stamps `Seq`, appends it to the region's op log, and broadcasts it to clients whose interest regions overlap. Clients apply it through the existing `ApplyBrush` path, which marks dirty every loaded leaf at any LOD the op affects.
  - **Land rights (round 6, D74):** validation covers materials, the shore mask and `LandRights.Check` (factions-and-law.md "Land and mining rights"; ~50 ns, cached on the tool for 1 s per cell and claim). It returns `Allowed`, `Illegal` (the op runs and its volume is counted toward an `IllegalDig` or `ClaimJump` case) or `Refused` (settlement ground, see "Protected shores and seabeds"). Factions control most planets, so digging, tunnelling and building need owned land or approval; `Wild` bodies are free.
  - **Cave drills and tunnelling vehicles (D74):** the `CaveDrill` core (rings-and-asteroids.md; `regulated`) edits planet voxels through this same path as ordinary Subtract ops, one land check per stroke. Proposed rate and op radius as on asteroids (0.1 m³/s, 0.75–1.5 m), data per planet material.
- **Compaction:** after 64 ops a LOD-0 chunk is baked: final 32³ density + material, RLE then zlib (`Brutal.ZLib`, already in BRUTAL), ~3–8 KB. Its ops are dropped from the fine tier, but kept in the coarse tiers (until the coarse cell compacts, below) so distant views still show large craters.
- **Joining clients** download snapshots plus pending ops for their interest region.
- **Persistence:** one file per LOD-10 region (32 km) holding snapshots plus the op log.
  - `GeneratorVersion` is locked per universe: ops survive a generator change, snapshots do not. Because edits are permanent and compaction drops fine ops, the lock is **for the life of the universe**: generator fixes ship behind a new `GeneratorVersion`, and old universes keep the old code path.

**Permanence and growth bounds (user decision D32: planet edits are permanent).** Nothing regrows, and nothing is aged out. Growth is bounded by structure, not by time:
- **Per LOD-0 chunk:** at most one snapshot (≤ 8 KB; a chunk dug out to air or filled solid compacts to a 16 B uniform record) plus fewer than 64 pending fine ops (< 2.6 KB). So **≤ ~10.5 KB per touched chunk, forever**, however many times it is edited.
- **Coarse tiers:** ops with `Radius ≥ 4 m` were kept forever in the coarse tier for distant views. They now compact too: after 256 ops in a LOD-5 cell (1 km), the cell bakes one snapshot per touched **LOD-1..5 surface chunk** (32³ at that chunk's own voxel size, ~4–8 KB each), and its coarse ops drop. Baking only the LOD-5 chunk would show a 20 m crater at 32 m voxels from a LOD-2/3 leaf 1 km away; per-LOD snapshots keep each leaf at its own resolution. A fully worked km² holds ~480 such chunks (~4 MB), under a third of its LOD-0 bound (~1,400 chunks × 10.5 KB).
- **Huge tier** (`Radius ≥ 32 m`: craters, bombs, impacts, dropped stations (rare under D27: factions prefer capture, and a drop is the `StationDrop` incident in the shared `IncidentRecord`, owned by factions-and-law.md), warp arrivals that hit): 40 B per op, bounded by its cost (AM, impacts), filed by LOD-10 cell (32 km) so a leaf scans only the cells it covers (≤ 64 lists for a LOD-12 leaf). Once a cell's list passes 256 ops, it compacts like the coarse tier into snapshots of the touched LOD-6..12 surface chunks.
- **Edit rate:** the server caps each player at 8 ops/s sustained (burst 32) through the existing validation; a drill is one op per stroke, not per voxel.
- **Adversarial bound and byte budget:** the rate cap alone still lets a griefer put 64 ops into each new chunk (one fresh 8 KB snapshot per 8 s, ~3.6 MB per played hour, ~1.8 GB a day for 64 such players). The server therefore counts stored bytes per `Author` and, past a budget per played hour (knob, 1 MB; heavy honest mining is ≤ ~0.3 MB), drops that player to 1 op/s until the hour's average is back under. A cap on storage, not on digging where it already has been: re-editing a compacted chunk adds nothing.
- **Worst-case growth** (estimates): a heavy miner tunnelling 3 m wide at 10 m/min crosses ~30 LOD-0 chunks an hour, ≤ ~300 KB/h. 64 such miners for 8 played hours a day is ≤ ~150 MB a day on disk, an upper bound: typical play (building, surface quarries reusing the same chunks) is about a tenth. Game time stops when the server is empty, so idle servers grow by nothing.
- **Memory:** only regions near players are resident (snapshots plus ops of loaded LOD-10 regions), as before, so RAM does not grow with world age.
- **Operator tools:** a disk quota warning in the server log and profiler (`EditStore` bytes on disk / resident), and an admin-only "restore region to generated" command for griefing cleanup. No automatic regrowth.

### Protected shores and seabeds (user decision D30: unminable)

There are no holes below sea level at the coast, so the sea sphere never has to flood anything. D60 (round 6) says the same from both sides: terrain around water is solid, and ships are never flooded with sea water (ship-atmosphere.md: an opening below the sea surface carries no flow).

**The band, precisely.** A point `p` is protected iff its column's **unedited** surface is low:
```
h(dir) = H(dir)                          the generated surface height about Radius (edits never move it)
protected(p) = h(dir) ≤ (SeaRadius − Radius) + ShoreProtectHeight      ShoreProtectHeight = 6 m (knob)
               and |p| ≥ Radius + h(dir) − DigDepth                     the whole column, surface to the dig floor
```
- The generator's inland clamp (≥ sea + `ShoreProtectHeight` + 1 m past the coastal ramp) means this holds only for the **seabed** (every column under the sea) and the **coastal strip** inside the ramp. Its width is set by the beach slope: 6 m of rise is 30 m on a 20 % slope, 300 m on a 2 % beach. 6 m clears the highest swash run-up and the storm surge the sea shading draws.
- It is a function of the column (seed only), so server and clients agree bit for bit, and a protected column stays protected forever.
- Protection covers the **whole column** down to the dig floor, so nobody can tunnel under a beach or the seabed from inland either. Inland mines below sea level are allowed (no water reaches them).
- A body with no ocean (`SeaRadius` = NaN) has no protected band and no inland clamp: all of it is minable.

**What edits may do there.**
- **Removal is clamped, not refused:** in a protected voxel, Subtract and Smooth can't take density below the **generated** density (`d = max(d_op, d_gen)`). A drill stroke that crosses the band still digs its inland part; it just stops at the protected face. The client shows the band as a shaded overlay when a mining tool is out.
- **Adding is allowed** (piers, sea walls, fill), and added material above the generated surface may be removed again, since that never goes below the unedited ground.
- **The same clamp applies to every terrain-removing brush:** impact craters, breach damage, anti-graviton and other bomb brushes. Seabeds and shores never crater.
- **Cost:** the column mask is 128 B per chunk from the height grid already built in step 2. The clamp needs the generated densities during replay, a 32 KB job-scratch copy taken only for chunks that have protected columns and ops (a small share of coastal chunks). The server's validation uses the same mask, so a fully protected op is dropped before it is logged (no storage for it).
- The `ShoreDepthMap` refinement scan (Ocean section) now only ever sees additions on the shore.
- **Settlement ground (round 6, D74; factions-and-law.md):** `Reserved` settlement footprints are one more source for the same per-chunk protected-column mask, for **tools only**. Drills, cave drills and tunnelling vehicles are clamped to the generated density there. The Huge tier (impacts, bombs, blasts) is not clamped on settlement ground: region shields stop those, or nothing does. Elsewhere on held land an unlicensed dig is not refused; it runs and is illegal. Each held body's surface region grid (cube-sphere, 6 × 16 × 16 bytes) and its settlement footprints are generated world data.

### Deep heat (user decision D29)

Digging stays limited to 2 km under the local surface (the shell). Past a depth, the rock heats until components fail, so in practice digging stops short of the floor.

**Rock temperature** by depth `d` below the **unedited** surface (`Radius + H(dir) − |p|`, so digging a pit never moves the gradient):
```
T(d) = T_surf + 25 K/km · d + HeatExcessK · ((d − HeatOnsetDepth) / (DigDepth − HeatOnsetDepth))²    for d > HeatOnsetDepth (800 m)
T(d) = T_surf + 25 K/km · d                                                                             above it
```

| Depth | 500 m | 1.0 km | 1.2 km | 1.4 km | 1.6 km | 1.8 km | 2.0 km |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T at T_surf = 288 K | 300 K | 338 K | 418 K | 548 K | 728 K | 958 K | 1,238 K |

- **Who feels it:** an entity's ambient temperature is `T(d)` when it is below the unedited surface (in a dug cavity or tunnel), else the atmosphere's. Checked at 1 Hz per entity below the surface (one `H(dir)` at ~0.2 µs; most entities skip it via the height bounds).
- **Damage:** the gas giants' component rule, with a wear stage. Each component has `MaxAmbientK` (default 400 K, about 1.2 km down); above it, it loses `Integrity` at a rate proportional to `T / MaxAmbientK − 1`, is disabled above 1.25×, and is destroyed above 1.5×. Heat-rated variants (data; mining drills, cooled housings) raise `MaxAmbientK` to at most 900 K, about 1.75 km down, at a cost. Suits use the same rule for characters (heat damage, then lethal).
- **Result:** default kit works to ~1.2 km, rated kit to ~1.6–1.75 km, and nothing lasts at the 2 km floor, so the floor is almost never reached and the shell bound stays a storage bound only. Deep rock can hold richer deposits as the reward (crafting data, not this doc).
- **Visuals:** a heat tint on deep rock materials (from `T(d)` in `terrain.frag`, no new pass) and a heat shimmer post effect while the camera is below ~1.2 km. Under 0.05 ms.

### Ocean and shore wash on a sphere

- **Sea:** a sphere at `SeaRadius`, drawn where the **sea mask** says sea. The mask is `H(dir) < SeaRadius − Radius` on the generated height, which the inland clamp makes equal to the continent mask's ocean plus its coastal ramp (analytic, and baked into the impostor). Edits never change it. No voxel water, no lakes, no rivers.
  - The underwater fog is the closed-form sea fog only (`WaterFogVolume` is cut). "Under water" is `sea mask(dir) and |p| < SeaRadius`, so an inland mine dug below sea level is dry and fog-free.
- **Near surface (as built, M2b):** the existing clipmap (`OceanRenderer`) laid out in a **sea frame**: a tangent frame on the sea sphere near the camera (x east, z south), bent onto the sphere by `y −= r² / (R + √(R² − r²))` (`r² / 2R` near its origin). Swell, FFT and swash keep using its `(x, z)` as their horizontal coordinates. The client moves the frame to the camera once the camera is 3 km from its origin; the new frame's depth maps are baked in the background and swapped in whole, and the FFT tiles and foam lace keep their place across the move.
  - 10 wave levels reach 6.1 km. 7 more levels are the **far shell** out to the horizon (786 km): bare sphere, shading only (the sun's glint widened by the slope variance of the waves a pixel can't resolve, from the wind). From altitude the finest levels are skipped (cells of about 1/96 of the camera's height), and by ~60 km the surface has faded into the terrain's far sea and the impostor.
- **One global sea (review C5):** `OceanSurface.Sample(planetLocal, tick)` lays the swell's components on the sphere by **tile** (`SeaTiles`): rows of latitude about the spin axis, each cut into whole cells of longitude (~64 km; 49 rows and 3,058 tiles on a 1000 km planet, a cap at each pole), each with a fixed tangent frame at its centre (east, up, south). A tile's field lives in its plane; within 2 km of an edge the neighbours' fields are blended with weights whose squares sum to 1 (up to 4 tiles at a corner).
  - Rows and cells, not a cube sphere: every edge is then a parallel or a meridian, a smooth function of position that the GPU's linearised copy follows exactly. A cube sphere's tile edges kink by up to 45° where they cross a cube edge.
  - The GPU mirrors it (`SeaSlots`): up to 4 tile slots, each with its plane map linearised at the camera and each wave's phase at the camera in that tile (double, mod 2π, once per frame). Moving the sea frame never jumps the phase. Measured against the CPU: under 1 mm within 300 m of the camera.
- **Depth** (`PlanetShore`, and its GPU cache `ShoreDepthGrid` + `ShoreDepthTexture`):
  - The value is **radial** depth `SeaRadius − (Radius + ground)`, the ground being `H(dir)` raised by placed fill. Edits that reach the sea's band are binned by their place on the sea sphere (16 m and 128 m cells), so a query replays only the ops over its own column. Digging only ever takes fill back down to `H`: the sea mask is the generated one (this is the pit rule, the same on CPU and GPU).
  - Near map: 512² × 2 m round the camera in the sea frame, stored toroidally (cell g at texel g mod 512), so a move only fills the strips that came in, on jobs (a 128 m strip is 32k samples); until they land the shader reads those cells from the far map.
  - Far map: 512² × 48 m (24.6 km) centred on the sea frame's origin, generated ground at LOD 4 detail, baked once per frame: the swell's depth cap beyond the near window.
  - Both R16F; only changed rectangles are uploaded. Above 600 m the near window is dropped, above 28 km both.
- **Swash on a curved coast:**
  - `swashSheet` already finds the shoreline along the depth gradient and the beach slope over ±6 m. Bays and headlands are handled by that gradient.
  - Planet curvature over 100 m is 5 mm, so nothing changes once "up" means radial.
  - **What it loses:** the surf carrier comes from `CoastalField`'s lag map, which is cut. Replace it with a **shore carrier** pass: a compute pass over the depth map, rerun only when the map shifts (~0.02 ms GPU). It takes the shore-normal from the depth gradient smoothed over 32 m, and the phase lag by sweeping 1/c(h) along that normal from deep water. Waves arrive shore-normal, which is where refraction would mostly bring them anyway.
  - Swash cost stays inside the ocean pass as today. The water design decides whether breaker faces and lips survive.
- **Atmosphere (brief):** per-planet precomputed scattering (Hillaire 2020 LUTs: transmittance, multi-scatter, sky-view, aerial-perspective froxels), ~0.1–0.2 GPU ms. It replaces the sky and fog terms in `terrain.frag` and `ocean.frag`, and hides the impostor ↔ octree switch.

### Weather on a sphere (user decision D31: port the pressure-system weather)

The island's `src/Weather` model (docs/weather.md: analytic pressure systems from seed + time, compact-support profiles, a 32 km snapshot map baked every 5 s) carries over. Only the spawn lattice, the steering wind and the map's plane change. It stays a pure function of seed + tick, so it costs no bandwidth and pauses with game time when the server is empty. Bodies without an atmosphere have no weather.

**Climate per body** (from `PlanetParams.Atmosphere` and the seed):
- **Steering wind by latitude:** a zonal profile `u(lat)` replaces the single prevailing wind: trades (easterly, 0–30°), westerlies (30–60°), polar easterlies (60–90°) for an Earth-like spin; the band count grows with spin rate (knob). Storminess and base cloud also vary by latitude band from the seed.
- **Hemisphere sense:** circulation round lows and storms is cyclonic about local up in the north and reversed in the south (sign of `sin lat`). The pressure-gradient wind fades to 0 within 3–10° of the equator (`smoothstep`), so no cyclones spin on the equator.

**Spawn lattice on the sphere.**
- Rows of 24 km in latitude (1000 km planet: ~131 rows pole to pole). Row `j` holds `n_j = max(1, round(2πR cos lat_j / 24 km))` cells in longitude, so cells stay ~24 km wide to the poles (~22,000 cells on a 1000 km planet, never stored: cell and slot are hashed on demand).
- Each row's cells drift with that row's steering wind in closed form, `lon_cell(t) = lon0 + u(lat_j)·(t − t0) / (R cos lat_j)` (as the gas giants' storms); a system also has its own drift ≤ 1.5 m/s, as now. Slots and lives (2 slots, 30–60 min periods) are unchanged.
- **Neighbour bound:** a point looks at its own row and the rows above and below, and in each row the cells whose longitude span lies within 21.1 km (16 km radius + 5.1 km drift) of it (the angular window is taken at the row's poleward edge, so it is conservative). Equatorward of ~75° latitude that is at most 3 per row, still ≤ 9 cells and ≤ 18 candidate systems. Poleward of ~75° a row's cells narrow across its own 24 km width by more than the window's slack (`tan φ · 36 km / R` > 14 %), so up to 4 per row: ≤ 12 cells and ≤ 24 candidates there (the sampler's fixed buffer is sized 24). Rows with very few cells next to the pole simply contribute fewer. The existing bit-for-bit test (`PointSampleSeesEverySystemThatReachesIt`) is ported to the sphere.
- **Geometry:** each candidate's centre is mapped into the sample point's tangent plane (gnomonic, from the planet-frame unit vectors). Over the ≤ 37 km a system reaches, curvature changes distances by < 0.05 %, so profiles and the no-sqrt wind ring are unchanged.

**Snapshot map.** (Superseded 2026-11-27: the client uses `SphereWeatherCache`, a planet-fixed cube-sphere lattice; `WeatherMap` is deleted.) `WeatherMap` stays 32 × 32 nodes at 1 km, baked every 5 s of game time, one row per tick. Its plane is the camera's tangent plane at the camera's surface point snapped to the LOD-5 lattice (1,024 m), the same plane family the clipmap and `ShoreDepthMap` use. A snapshot's nodes are fixed once baked and samples lerp in time between two snapshots, so re-centring never pops; the from/to pair may differ slightly in node placement, within the map's measured interpolation error (0.12 m/s wind).

**Who samples it:**
- client: the sky, rain, the sea's wind (swell and FFT), audio, as on the island;
- server and client: drag and wing lift (frames-and-zones, D10) use the wind relative to the co-rotating air. The server samples the analytic field per awake ship in an atmosphere at 10 Hz (≤ 18 systems, ~0.3 µs) and lerps; it bakes no maps.

**Seen from afar.** For the focus body only, a planet-wide cloud and rain coverage map (cube-sphere 6 × 256², ~6 km texels) is baked on the **GPU** every 60 s of game time: the CPU enumerates the systems alive on the visible hemisphere (≤ ~22k slots × ~50 ns ≈ 1.1 ms worker per bake) and uploads them (≤ 0.7 MB); a compute pass evaluates ≤ 18 systems per texel (< 0.1 ms GPU). The impostor and atmosphere read it for clouds and cloud shadows. Other bodies use a static seeded cloud texture. This also closes weather.md's "visible weather at a distance" gap on planets.

**Cost (estimates):** the tick stays as measured on the island (~0.0003 ms average, ~0.005 ms on bake ticks) plus the row lookup; server ~3 µs a tick for 64 awake ships in atmosphere (10 Hz × 0.3 µs, amortised); the far bake above. Lightning on rocky planets, if added, follows the gas giants' server-only `WeatherSeed` rule.

**Files:** `src/Weather/` gains `SphereWeatherField.cs` (lattice, zonal steering, hemisphere sense) and `PlanetClimate.cs`; `WeatherMap` takes a tangent basis; `GasGiantWeather` can share the row lattice. Shader `weather_coverage.comp`.

### Seeding

- `SystemSeed = SplitMix64(GalaxySeed ^ systemId)`. Body *i* gets `Seed = SplitMix64(SystemSeed + i)`. `SplitMix64` moves from `src/Water/Ocean` to `src/Engine`.
- A deterministic system generator turns star class → orbits → body class → `PlanetParams`: radius, sea, relief, gravity, day length, axial tilt, biomes, atmosphere.
- Hand-made systems and bodies override it from `data/systems/*.json`.
- **At system load (on jobs):**

  | Job | Cost |
  |---|---|
  | Height bounds per body (6 × 256² coarse samples) | ~60 ms worker |
  | Impostor per body at 256² | ~30 ms worker |
  | Focus body impostor at 1024² | ~0.5 s worker, streamed in when approached |

### Files

| File | Fate |
|---|---|
| `src/Terrain/Meshing/SurfaceNetsMesher.cs`, `ChunkMesh.cs`, `PackedTerrainVertex.cs`, `TerrainVertex.cs`, `Terrain.Runtime/Rendering/RangeAllocator.cs`, `TerrainLighting.cs` | **Reuse unchanged** (16-bit index option added to `ChunkMesh`) |
| `src/Terrain/Voxels/Noise.cs` | Reuse; **add** double-lattice `Gradient3/Fbm3` |
| `src/Terrain/Voxels/ChunkCoord.cs`, `ChunkConstants.cs`, `VoxelChunk.cs`, `VoxelMaterial.cs` | **Change:** double origin plus `Key`; drop Y bounds; drop `Water`; materials extended per biome |
| `src/Terrain/Editing/BrushOp.cs` | **Change:** `double3` centre, `Seq`/`Author`; `ApplyOne` takes the chunk origin in double. Core loop kept |
| `src/Terrain/Voxels/TerrainGenerator.cs`, `TerrainParams.cs` | **Replace** with `PlanetGenerator.cs`, `PlanetParams.cs`, `PlanetHeightBounds.cs` (keep `PickMaterial`, overhang band, early-out structure) |
| `src/Terrain.Runtime/ChunkJob.cs` | **Change:** `ChunkFrame`, op tiers / snapshot; `SummariseColumns` and column arrays cut |
| `src/Terrain.Runtime/TerrainWorld.cs` | **Change:** planet roots (LOD 15, leaves ≤ 12), shell classify, horizon prune, speed LOD bias, select on a job, collision anchors, double camera. Retire, batching and upload kept |
| `src/Terrain.Runtime/Rendering/TerrainRenderer.cs`, `shaders/terrain.vert` | **Change:** camera-relative origins, GPU cull + indirect count draws, 16-bit indices |
| `src/Terrain.Runtime/BathymetryGrid.cs` | **Change** into `ShoreDepthMap` (analytic radial depth; keep shift and banded rebuild; drop sea/zone/LOD arrays) |
| `src/Terrain.Runtime/BathymetryTexture.cs` | Reuse |
| `src/Physics/StaticMeshShape.cs` | Small change: pooled index buffer (zero-alloc) |
| **New** | `EditStore.cs` (with coarse compaction), `ChunkSnapshot.cs` (RLE + zlib), `ShoreProtection.cs` (column mask, removal clamp), `DeepHeat.cs` (`T(d)`, 1 Hz ambient), `PlanetImpostor.cs` + shader, `ShoreCarrier` compute pass, `SystemGenerator.cs`, `src/Weather/SphereWeatherField.cs`, `PlanetClimate.cs`, `weather_coverage.comp` |
| `src/Terrain/Voxels/RiverPath.cs`, `src/Terrain/Water/*` (WaterBlock, WaterBodies, WaterColour, WaterFlow(.Simd), WaterFlows, WaterHalo, WaterMesh, WaterPipes, WaterSurfaceMesher), `src/Terrain.Runtime/Water/*` (VoxelWaterSystem, WaterJobs, ZoneFlowSim), `Rendering/VoxelWaterRenderer.cs`, `shaders/voxel_water.*`, `voxel_water_wind.glsl`, `Engine/Rendering/WaterFogVolume.cs`, `water_fog.glsl`, `Game/TerrainCoastalDepth.cs`, `CoastalSystem.cs`, `Coastline.cs`, island scene tests (Lake/River/Pond/Flood/Cave/Seaside/Surf/Roof) | **Cut** |
| `src/Water/Ocean/CoastalField.cs`, `CoastalBuilder.cs` | **Cut** (shore carrier replaces the lag map). `OceanSwell` loses its `Coastal*` inputs; `OceanFft`, `Jonswap`, `WaveSet`, clipmap and `swashSheet` stay |

### Open questions for the user

Relief and dig depth (D29) and edit permanence (D32) are decided and removed from this list.

1. **Day length.** Is a realistic rotation period wanted (it sets the jump of 290 m/s at the equator, for a 6 h day, between the planet frame and space), or slow rotations that keep the frame change mild?

### Gas giants

Proposed with rings-and-asteroids revision 3 (2026-11-25). User decision: gas giants exist as a new body type with no voxel surface, they host rings, and their atmospheres can be harvested (gas scooping). Every number is a knob; every cost is an estimate to measure.

**Body type.**
- `CelestialKind.GasGiant` (frames-and-zones owns the enum: `CelestialKind {Star, Planet, Moon, GasGiant, BlackHole, Remnant}`; exotic-matter's `BlackHole` sits beside it): a `CelestialBody` on Kepler rails with a `BodyInertial` frame, a rotating `BodySurface` frame, an SOI and patched conics as usual. It has no octree, shell, `EditStore`, collision, deposits or sea.
- `GasGiantParams(Seed, Radius /*1-bar datum*/, Gravity1Bar, RotationPeriod, SpinAxis, RotationPhase0, Atmosphere, BandStyle, StormStyle, Belt, RingParams? Ring)`, from the system generator or `data/systems/*.json`.
- **Scale (game-compressed, like the 1000 km rocky worlds):** R = 6,000 km (range 4,000–10,000), 1.2 g at 1 bar (0.9–2.5; rocky worlds are decided at 1 g), day 10 h (8–14). That gives μ = 4.24e14 m³/s², escape 11.9 km/s from the datum, low orbit (R + 700 km) 7.95 km/s, equatorial rotation 1.05 km/s, centrifugal 0.18 m/s² at the equator, Coriolis 0.1 m/s² at 300 m/s.
- **System generator:** 0–3 giants per system, in the outer system. Each has 2–8 voxel moons (rocky planets of 300–1,500 km radius) between 1.2 × the ring's outer radius and 0.5 × its SOI, and usually 1–3 rings (rings-and-asteroids: dense-leaning band presets, ring inner radius ≥ max(1.15 R, R + 1.3 × atmosphere top + 10 km), so rings stay in `BodyInertial`). Moons cut the rings' resonance gaps and nest under the giant in the frame tree (Solar → giant inertial → moon inertial → moon surface).

**Depth and pressure (closed form, identical on server and clients).**
```
z = |p| - R                     altitude above the 1-bar datum; depth d = -z
above the datum:  p = p0 * exp(-z / H)                          H = R_gas*T0 / (M*g) ≈ 50 km (T0 = 165 K, M = 2.3 g/mol)
below the datum:  T = T0 + Γ*d,  p = p0 * (T / T0)^κ            Γ = 1.5 K/km, κ = 3.5 (adiabatic H2)
rho = p*M / (R_gas*T)           gravity: the point-mass μ/r² (patched conics), unchanged
```

| Depth below datum | 0 | 50 km | 100 km | 150 km | 200 km | 300 km | 500 km | 1,000 km |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Pressure | 1 bar | 3.7 | 9.6 | 20 | 38 | 100 | 400 | 3,300 |
| Temperature | 165 K | 240 | 315 | 390 | 465 | 615 | 915 | 1,665 |

- **Atmosphere top** (1e-6 bar) is 14 H ≈ 700 km above the datum.
- **Cloud decks**, from the giant's composition (seeded): a high haze (1e-3–0.1 bar), the ammonia-ice deck (0.5–1 bar, 0–35 km up), the NH4SH deck (1.5–3 bar, 14–40 km down) and the water deck (4–7 bar, 54–82 km down).
- **Crush depth** comes from the ship, not the body:
  - each **sealed room** gets a rating at hull bake, `p_crush = k_shape · 2·t·σ_c / r_room` (thin-shell with a buckling knob `k_shape` 0.1; ship materials gain `compressiveMPa`, as tractor-beams.md already asks). For example, 0.25 m composite walls round a 10 m room give ~15 bar, a crush depth of ~128 km;
  - each **component housing** has `PressureRatingBar` in data (default 10 bar, ~100 km; deep-rated variants cost more), plus `MaxAmbientK` (default 400 K, ~157 km);
  - **1 Hz server check** per ship below the datum: a room whose `p_amb − p_room` is over its rating implodes (ship-atmosphere.md: the difference counts, not ambient alone, so a room let up to ambient cannot be crushed) (a damage brush on its walls through the ship-hulls damage path; the room floods with ambient gas, lethal to crew inside); a component over its rating is disabled until back under, and destroyed past 1.5×. Thrusters are components, so a ship that dives too deep can lose the means to climb out;
  - the HUD shows depth, ambient pressure and temperature, and "crush in N km" for the weakest room and the weakest critical component.
- **Abyss:** at `AbyssDepth` (1,000 km, ~3,300 bar) anything left is destroyed by a scheduled event, like a black hole's kill sphere. There is no core to reach.

**Frames, flight, and why there is no landing.**
- **Rotating band:** `RotatingTop` = atmosphere top (~700 km), leave at 1.3× (frames-and-zones' rule). The frame turns at the giant's interior (System III) rate. Differential rotation is the zonal **wind**, not frame motion.
- **Bubbles** in the band are surface-anchored in the rotating frame with the C1/C2 rules (scene gravity `g(o)` + centrifugal; per-awake-body gravity delta + Coriolis; 32 m rebases). They hold no terrain colliders: there is nothing to touch.
- **Landing does not exist.** Below the datum, pressure and heat rise until the crush and then the abyss. A ship stays up by thrust, by wings (the flight model has wing lift in atmosphere, D10), or by an anchor (any grid, D25; exotic-matter; very costly this far from an orbit). Buoyant aerostats are an open question.
- **Unattended ships** can't be frozen in mid-air. A ship whose last anchor leaves while it is inside the band gets a **sinking rail** unless its orbit's periapsis is above `RailSafeP` (1e-5 bar, ~575 km): closed-form descent (ballistic down to the 0.1 bar level, then terminal velocity along the local wind), with a destruction event scheduled at 1.5 × its crush depth. It costs 0 per tick; the owner sees the predicted loss tick at logout. Only orbits with periapsis above `RailSafeP` get normal orbital rails (with drag decay evaluated at element refresh). (Review fix: the old test, "below 0.1 bar now", gave a ship hovering at 0.09 bar an ordinary rail that fell through the decks with no drag and no crush check.)
- **Drag and lift** are frames-and-zones' aero pass (D10: drag on the hull, lift per wing part, airspeed relative to `GasGiantWind`) with this density profile; it closes review's missing system 6. **Buoyancy** `ρ_gas·V_sealed·g` is added per ship (under 3 % of a ship's weight at 10 bar, so it matters only for aerostats).

**Weather (deterministic from seed + tick, zero bandwidth).**
- **Zonal bands:** 8–20 latitude bands from the seed, each with a jet speed `u(lat, depth)` (±50–180 m/s, alternating), a colour and deck heights. The band texture and its motion come from them.
- **Storms** reuse the parked `src/Weather` pressure-system model, adapted to the sphere (inventory.md already plans the spherical adaptation):
  - per 6 h epoch, a hashed, stratified number of storms (no Poisson `exp`), each with a birth tick, latitude, radius (200–3,000 km), peak wind (80–250 m/s) and lifetime (1–20 days), plus 0–2 permanent giant storms from the seed;
  - a storm's longitude is `lon0 + u(lat)·(t − t0)/(R·cos lat)`, closed form; the active set (≤ 64) is rebuilt every 5 s (~10 µs);
  - `GasGiantWind.Sample(lat, lon, depth, tick)`: zonal jet + Rankine vortices + deterministic gust noise (±20 %), ~0.3 µs.
- **Storm hazards:** winds and gusts through drag; core downdrafts up to 30 m/s that pull ships deeper; lightning: the server rolls once a second per ship inside a storm from `Hash(WeatherSeed, storm, second, entity)`, where `WeatherSeed` is server-only like exotic-matter's `NaturalSeed` (review fix: with the public seed, clients could precompute every strike and dodge it). A strike disables a few components for 5–20 s and applies a small damage brush (a 16 B event); in-deck visibility and optical sensor fog (deck extinction through the rings doc's `RingObscuration` couplings: optics blinded, radar mostly not).
- Storm and band forecasts are public (they're functions of the seed), so a pilot can plan a dive around them.

**Radiation belts.** A torus about the spin axis from 1.3 to 4 R, peak `doseBelt` 0.01 Gy/s unshielded (knob), through exotic-matter's `RadiationDose` with σ0 = 200 kg/m² (as flares): behind 0.5 m of composite that is ~2.4e-4 Gy/s, 5 Gy in ~6 h. **Rings deplete the belt** across their radial span (× 0.05), so ring slabs are shelters and gaps are not. 1 Hz per entity, ~50 ns.

**Gas scooping.**
- **Cores** (ship-hulls function cores, `data/components/scoops/*.json`): `GasScoop` S/M/L (mouth 1/4/16 m², lip heat rating, mass), `GasCompressor` (liquefaction work `w_liq` ≈ 1 MJ/kg of bus power), `GasSeparator` (station, or a slow ship version: splits `gas.raw` into products by its stored composition, mass conserved like ore per material), `CryoTank`.
- **Skim** (orbital pass, periapsis in 1e-4–1e-2 bar, ~7 km/s relative to the co-rotating air on a prograde equatorial pass):
```
mdot = eta_s * rho * v_rel * A_scoop                         eta_s = 0.5
D    = 1/2 * rho * v_rel^2 * Cd * A_ship + mdot * v_rel     the second term is the captured gas's momentum
qdot = 1.74e-4 * sqrt(rho / r_lip) * v_rel^3                stagnation heating on the lip (Sutton-Graves), W/m²
propellant per kg gathered = (v_rel / v_e) * (1 + Cd*A_ship / (2 * eta_s * A_scoop))     independent of depth
```
  - At 1 mbar with a 4 m² scoop: 2.3 kg/s (8.5 t/h), ~1 MW/m² on the lip.
  - Depth sets only the rate and the heating, not the economics. A streamlined scoop ship (`Cd·A_ship / A_scoop` ≈ 2) on plasma drives (v_e 40 km/s) pays ~0.5 kg of propellant per kg gathered, and if it burns scooped hydrogen as its propellant it nets about half of what it takes. Chemical or AM-thermal ships lose mass on every pass. That is the design: skimming is for efficient drives.
- **Dive** (slow, in the rotating frame, ≤ 300 m/s airspeed, into the decks): a pumped intake, `mdot = min(PumpKgS, P_pump / w_pump)`. It reaches the heavier species below. The costs are staying up (thrust at ~1.2 g, or wings), the crush depth and storms, and climbing back to orbit (~8 km/s, a full launch). **AM-thermal ramjet mode:** an AM thermal thruster may take ambient gas through a scoop as its propellant (exotic-matter: "any `Propellant` item"), so in a giant it burns only AM. Hovering a 300 t ship that way still costs ~110 g of AM an hour, so dives are short, or done by winged craft and anchored or floating stations with shuttles.
- **Yields** (mass fractions of `gas.raw`; each giant scales them ×0.5–2 from its seed, and its band colours show which: white ammonia zones, brown NH4SH belts, blue water showing in storm eyes, so players read a giant's chemistry from afar as they read a ring's):

  | Layer | Pressure | Raw mix | Products | Use |
  | --- | --- | --- | --- | --- |
  | Upper (skim) | 1e-4–1e-2 bar | H2 ~75 %, He ~24 %, CH4 0.5 % | `propellant.hydrogen` (exotic-matter's item), `gas.helium`, `fuel.he3` (~3e-5 of the mix), `fuel.deuterium` (~4e-5), `gas.methane` | AM propellant and AM factory feed (exotic-matter's 1 g H per g AM), D–He3 fusion fuel (exotic-matter's fusion), coolant, methalox fuel |
  | Ammonia deck | 0.5–1 bar | + NH3 ~0.5 % | + `propellant.ammonia` | Propellant (exotic-matter's AM thrusters take it); cryo-solvent feedstock for recrystallisers (proposed recipe: ammonia + methane) |
  | NH4SH deck | 1.5–3 bar | + NH4SH and H2S ~0.2 % | + `chem.sulphur` | Industry (later) |
  | Water deck | 4–7 bar | + H2O 1–3 % | + `ice.water` | Propellant and life support. Not an economic mixing-agent source: the agent is ice + rock flux at ~0.02 per kg (exotic-matter), and the giant's ice-rock ring bands supply both halves without a 54–82 km dive. A giant with icy rings is an agent hub through its rings, not its water deck |
  | Deep | > 10 bar | + Ne, Ar, Xe traces (~1e-4) | + `gas.argon`, `gas.xenon` | Ion and Hall propellant |

- **No EM** in gas (EM is in rock: the rings and moons carry it). **No AM by default:** exotic-matter's revision 2 rule is that AM is made, not found. An optional `BeltAntiprotonYield` knob (default 0) would let exotic-matter's `MagneticScoop` core trap antiprotons in the radiation belts at ≤ 10 µg/h, about 3e-4 of an S factory, never a source. Turning it on is exotic-matter's decision (open question). Exotic-matter dropped `Magnetosphere` when belt AM went; `RadiationBelt` here is a dose source only.
- **What scoops refuse (review fix):** a `GasScoop` takes only atmospheric gas. It refuses rubble clouds (intake-only, by the mouth rule) and takes nothing from ring dust or gravel, which are render and sensor data with no mass.

**Hazards (summary).**

| Hazard | Where | Effect | Counterplay |
| --- | --- | --- | --- |
| Pressure | Below the datum | Rooms implode, components shut down, then are destroyed at 1.5× | Rated rooms, deep-rated components, the depth gauge |
| Heat | Skim lip (MW/m²); deep ambient (> 400 K below ~157 km) | Lip damage; components degrade | Lip rating, shallower skims, short dives |
| Storms | Storm discs | Winds, downdrafts, lightning strikes, fog | Public forecast, routing round them |
| Radiation belts | 1.3–4 R | Dose | Shielding mass, staying in ring shadow |
| Drag and Δv | Skims, dives | Orbit decay, an ~8 km/s climb out | Efficient drives, ramjet mode, planned passes |
| Abyss | 1,000 km down | Destroyed | Don't go there |

**Rendering.**
- **Far stages** as planets' detail ladder: a point of light, a disc, then an impostor sphere with no relief, carrying a banded cloud texture (equirect 2048 × 1024 + deck height and density maps, baked at load on a worker in ~60 ms, ~6 MB with mips) whose bands scroll at their own zonal speeds (`offset = u(lat)·t / R`), storms as swirl terms in the shader (≤ 64), the Hillaire atmosphere LUTs (H2 Rayleigh plus haze), the ring's shadow on the cloud tops, and lightning flashes. ~0.1–0.2 ms, like a planet's atmosphere.
- **Inside the atmosphere** (within ~200 km of a deck): a half-resolution volumetric march through the decks (32–48 steps, a 128³ tiling noise volume of 2 MB, the same dust-and-fog code path as ring haze). Estimated 0.5–0.9 ms at 1440p; the knob is the step count. That is above the rings' 0.6 ms inside-ring target, accepted because a giant's band draws no terrain or sea; it needs its own exit check (≤ 0.9 ms) when it is built. This replaces the impostor, as the octree does on rocky planets.
- **Memory:** ~10 MB for the focus giant; others are 1.2 MB impostors like any body.

**Costs (estimates).**
- **Per giant:** rails and frames as any body (~100 ns a tick); storm active set ~10 µs per 5 s. The server holds no voxels and no collision, so a giant costs far less than a rocky planet.
- **Per ship inside the band:** wind ~0.3 µs, drag and buoyancy ~0.2 µs a tick; pressure and temperature check at 1 Hz (~10 ns per room); lightning roll ~50 ns a second; dose ~50 ns at 1 Hz; sinking rails 0.
- **Scooping:** one ledger entry per scoop at 10 Hz (mass by species, integer mg), as exotic-matter's ledgers.
- **Profiler rows:** giants loaded, storms active, ships in band, wind samples, pressure events (implosions, components disabled), lightning strikes, scoop kg/h by species, cloud march ms.

**Files (proposed).** `src/Celestial/GasGiantParams.cs`, `GasGiantAtmosphere.cs` (p, T, ρ, decks), `GasGiantWeather.cs` (bands, storms, wind; uses `src/Weather`), `RadiationBelt.cs`, `SinkingRails.cs`; `src/Resources/GasMixture.cs`; `src/Ships/Devices/GasScoop.cs`, `GasSeparator.cs`; `src/Ships/Pressure/RoomPressure.cs`; `src/Engine/Rendering/GasGiantRenderer.cs`; shaders `gasgiant_bake.comp`, `gasgiant_impostor.frag`, `gasgiant_clouds.frag`; data `data/bodies/gas-giants.json`, `data/gases/species.json`, `data/components/scoops/*.json`.

**Open questions.**
1. **Scale:** R ≈ 6,000 km at 1.2 g (6× the rocky worlds, ~8 km/s to orbit), or smaller and lighter so dives are less of a launch each time?
2. **Floating stations:** should buoyant aerostats (hot-hydrogen or vacuum envelopes) be a station type that floats at a pressure level, or are cloud bases only possible with costly anchors (now usable on any grid, D25)?
3. **Belt antimatter traces:** keep AM strictly industrial (default), or enable `BeltAntiprotonYield` as a tiny flavour trickle? That reopens exotic-matter's revision 2 decision.
4. **Deep reward:** is anything worth the crush zone below the water deck beyond noble gases (for example a rare deep species), or should depth stay a pure risk/cost gradient?
5. **Lightning and pressure damage to crew:** are implosions and lightning lethal as proposed, or only damaging?
6. **Logout in an atmosphere:** is a scheduled loss (sinking rail) acceptable, or should the server refuse logout below 0.1 bar unless anchored?

**Review notes** (lead review, 2026-11-25; fixes made in place above):
- **Faithful to the user's answer.** It is a new body type with no voxel surface. It hosts rings (dense-leaning, outside the rotating band). Scooping yields cover fuel gases (H2, D, He-3, CH4, NH3, noble gases), agent-related feedstock and an optional AM trace knob that defaults to 0, so exotic-matter's industrial-only rule holds. The hazards cover pressure, heat, storms, depth limits (crush depth, abyss) and radiation belts.
- **Numbers checked:** μ, escape, orbit, rotation, centrifugal, Coriolis, H ≈ 50 km, the pressure and temperature table, the deck depths, skim `ṁ` (2.3 kg/s), lip heating (~1 MW/m²), the propellant-per-kg form, ramjet hover AM (~114 g/h from exotic-matter's light thermal thruster at 4.5 mg/s per 500 kN), belt dose, and He-3 and D fractions. The crush depths were wrong (15 bar is ~128 km, not ~145 km; 10 bar is ~100 km) and are fixed.
- **Frames:** `RotatingTop` = 700 km with the 1.3× leave rule matches frames-and-zones. The rings' inner bound (1.153 R) clears it. Bubbles are terrain-free surface bubbles. An anchored station's zone beats the band (exotic-matter).
- **Sinking rails** now key on periapsis (`RailSafeP` 1e-5 bar), not current pressure. A hovering ship at 0.09 bar previously got an ordinary rail that fell through the decks unchecked.
- **Lightning** rolls on a server-only `WeatherSeed`. Storm tracks stay public, but strikes can't be precomputed.
- **Item ids** are aligned with exotic-matter (`propellant.hydrogen`, `propellant.ammonia`). The water deck is no longer called a mixing-agent hub, because ring ice-rock is the cheap source.
- **Scooping abuse:** scoops take gas only. They can't vacuum rubble clouds or ring dust. Skims are self-limiting by Δv: depth changes the rate, not the economics. Ledgers are integer mg at 10 Hz. Gas is infinite, but its cost is propellant, heat and the climb, so abundance only affects prices: one 8.5 t/h skimmer yields ~0.25 kg/h of He-3, enough for ~100 fusion plants at 200 MW. Flagged to exotic-matter as an economy knob (the He-3 fraction), not an exploit.
- **Perf:** the in-atmosphere cloud march (0.5–0.9 ms) is above the rings' 0.6 ms target. That is accepted only because the band draws no terrain or sea, and it gets its own ≤ 0.9 ms exit check. Server cost per giant is far below a rocky planet's.
- **Still open:** questions 1–6 above. Q3 reopens exotic-matter's revision 2 decision only if the user wants the trace, and stays off by default.

Revision and review history: [history/planets.md](history/planets.md).
