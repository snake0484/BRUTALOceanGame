# Terrain LOD: an octree of chunks

Voxel size adapts to distance. The unit of work stays a dense 32³ chunk; what changes is the size of the voxels in it.

- A chunk at LOD *L* has voxel size `s = 2^L` m and covers `32·s` m per side. LOD 0 is the current 1 m chunk.
- The world is an octree of chunks. A node either splits into 8 children at LOD − 1, or is a **leaf** that gets generated and meshed.
- Uniform leaves (all air, all rock, all sea) are the "enormous voxels". They generate in microseconds, produce no mesh, and store nothing.
- Hot loops never walk a tree: generation, meshing, collision and the water flow sim all run on flat 32³ arrays.

## Keys and units

- `ChunkCoord(X, Y, Z, Lod = 0)`. Its world origin is `(X, Y, Z) · 32 · 2^Lod` m. The node's parent is `(X >> 1, Y >> 1, Z >> 1, Lod + 1)`, using arithmetic shift, which floors negatives.
- **Density is stored in voxel units.** The stored value is signed distance in metres / s × `DensityScale`, clamped. A coarse chunk therefore keeps a usable gradient across its large voxels, and surface nets places vertices correctly.
- **Mesh positions are chunk-local metres.** The mesher works in voxel units and the job scales positions by s, so renderer, collision and bathymetry never need to know the LOD.
- **Brush ops stay in metres.** They are replayed into any LOD by converting the centre, radius and falloff to that chunk's voxel units. Coarse chunks therefore show large edits (a big crater seen from far away) and smaller edits fade out naturally.

## Node selection (main thread, when the camera changes LOD-0 chunk)

- **Roots:** LOD `MaxLod` (v1: 4, so 16 m voxels and 512 m chunks) on a square of `RootRadius` roots around the camera in xz. The vertical extent is trimmed to the generator's height bounds plus edits; nodes entirely outside it are skipped.
- **Split rule:** split a node at LOD L > 0 when the distance from the camera to the node's AABB is less than `SplitFactor · nodeSize` (v1: 3). This gives LOD-0 detail within about 190 m, LOD 1 to about 380 m, and so on, roughly doubling per ring.
- The leaves of this subdivision are the **desired set**.

## Streaming and transitions

- Queue missing desired leaves to jobs, nearest and finest first. Edits come before streaming.
- **No holes during transitions:**
  - A loaded leaf that is no longer desired is **retiring**. It stays drawn until every desired leaf covering its volume is ready. That means either its desired ancestor, or all of its desired descendants.
  - Then the retiring leaf is freed, along with its GPU mesh and collision body.
- **Unload:** anything outside the root square is dropped.

## Seams: skirts (v1)

- Neighbouring leaves of different LOD don't share boundary vertices, so small cracks appear along their shared faces.
- **v1 fix:** every chunk mesh gets skirts. Each open boundary edge (an edge used by exactly one triangle, lying on the chunk's padded border) is extruded into a quad going 1 voxel against its average vertex normal. Seen from outside, the skirt hides any crack up to one voxel wide.
- **Cost:** about 5–10% more triangles.
- **Later:** transition cells (Transvoxel-style) for crack-free geometry if skirts show artefacts on steep cliffs.

## Per-system rules

- **Collision:** LOD-0 leaves only, within `CollisionRadius`. Coarse leaves have no bodies.
- **Edits:** ops are kept in one global list. A job gathers the ops whose AABB intersects its padded volume. That's O(ops) per job, which is fine for thousands of ops; a spatial index can come later. `ApplyBrush` marks every loaded leaf it overlaps dirty, at any LOD.
- **Bathymetry and sea mask:**
  - Each leaf reports column tops and sea flags per voxel column. Bathymetry cells (2 m) take the max top over all ready leaves covering them, which is valid because leaves are disjoint in 3D.
  - Sea state comes from the finest leaf that contains the sea-level voxel layer.
  - Affected cell ranges are rebuilt when leaves become ready or retire.
- **Water:** uniform sea leaves are one giant water cell. The flow sim (`voxel-water.md`) only ever runs in LOD-0 leaves.
- **Rendering:** chunk AABBs scale with the LOD. One draw per leaf, as today, with indirect draws later.

## Budgets (to verify with the Terrain panel)

- About 2–3k leaves in total, a few hundred of them with surface.
- View distance goes from about 400 m to about 2.5 km (`RootRadius = 2` roots of 512 m), at a similar triangle count, because coarse leaves carry about as many triangles as a LOD-0 leaf.

## Streaming without stalls (measured with `--fly-test`)

`--fly-test` flies the camera round the main island's coast at 40 m/s for 30 s, timing each frame's main-thread sections (ticks, terrain, water, coastal, UI, command recording, present). It then logs percentiles and the ten slowest frames, with what ran in each: leaf select, bathymetry rebuild (full or partial), depth-map upload and its cell count, coastal build, GC. Main-thread work went from p95 1.7 / p99 5.9 ms to p95 1.15 / p99 2.4 ms. Steady flight has no frames left above about 3 ms. What remains is the first ~1.5 s (JIT warm-up, the initial streaming flood).

- **Bathymetry re-centre shifts instead of rebuilding.** When the camera drifts 128 m the grid moves by whole chunks, and every per-cell array moves with it (`BathymetryGrid.ShiftCells`). Only the strips that came in are rebuilt. Pending and in-progress rebuild regions shift too. Before, every re-centre cost a 4–7 ms full rebuild.
- **Rebuilds run in bands under a budget.** A rebuild runs in bands of 16 rows, at 1 ms per frame (at least one band), so the first load or many streaming changes spread over frames.
- **Only real changes count.** `BeginRegion`/`EndRegion` compare a band before and after merging and mark changed only the cells whose top, sea state or sea LOD actually differ. A distant coarse leaf swapped for its children usually changes nothing.
- **Pending regions merge only when it pays.** Regions merge only if the bounding box isn't much larger than the two. Two strips meeting at a corner used to become the whole grid.
- **Retire is budgeted.** Coverage checks run under a 0.5 ms budget per update and resume where they stopped, with removals inside the budget. A coarse node's coverage is one pass over the desired leaves (the octree is aligned, so a finer leaf is inside exactly when its origin is), not a walk of up to 8^lod descendants. Before, a single check could take about 5 ms.
- The depth-map side of this (regional uploads, the GPU shift) is in `docs/voxel-water.md`, under the bathymetry texture.
