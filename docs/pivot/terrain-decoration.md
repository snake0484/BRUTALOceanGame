# Terrain decoration

Status: design proposal, 2026-11-26. Nothing built, nothing run: every cost and count below is an analytic estimate to be measured, and is marked as an estimate, a budget or a cap.

Written from the working tree on `space-pivot` (`src/Engine/Rendering/InstanceBuffer.cs`, `src/Terrain.Runtime/Rendering/TerrainRenderer.cs`, `shaders/terrain_cull.comp`, `src/Terrain/Planet/*`, `PackedTerrainVertex.cs`) and docs/pivot (planets.md, README.md M1/M2 notes, rings-and-asteroids.md, ai-ships-and-crew.md, decisions.md), plus the planet-kinds proposal being written in parallel (kinds as data recipes, per-lattice-point channels and surface materials, `overhang3d` / `caves3d`, `pads` and `LandingSites`, milestones K0-K4). Land rules follow docs/pivot/factions-and-law.md, "Land and mining rights" (D57, D74): `LandRights.Check`, the `Reserved` clamp and the shared `IncidentRecord`.

User decision (not re-litigated): terrain decoration such as rocks and trees is generated procedurally, like the terrain itself. Full 3D terrain features (caves, arches, overhangs) must be possible, so decoration must work on them.

## Recommendation (summary)

- **Decoration is a pure function of seed + position.** Candidates come from integer hashes of cells on the generator's own cube-sphere lattice (`CubeLattice`); a candidate is a radial column, and it places an instance wherever that column crosses the terrain surface (ground, cave floor, ceiling, overhang underside). Acceptance, species, size, yaw and tint come from the hash and from the planet kind's own per-lattice-point outputs (height, slope, surface material, mask channels, latitude).
- **Identity is (class, face, cell, slot, layer)** in a 64-bit `DecorId`. Nothing is stored for untouched decoration. Only deltas are stored (removed, stump, looted): 4 B entries that switch to a bit per slot when a block gets busy, so surface state is lossless at ≤ 3.1 KB per ~200 m block, as the rings store `RemovedSlots` and mined-out bitmaps.
- **Peers agree by construction and by check.** Every rule input is quantised to an integer once; acceptance, species, size, pose and the anchor refine are integer after that. The server is the authority for which S, T and L instances exist: it sends a 4 B CRC per LOD-0 list near each player and replaces a client list that differs (section 7).
- **Who generates what:**
  - *Ground cover* (grass, pebbles, small plants) is GPU-only: a compute pass turns the LOD-0 terrain vertices near the camera into cover clumps by hash. No CPU identity, no collision, no memory per chunk.
  - *Props, standing things and landmarks* are resolved once per chunk, inside the chunk job that already holds the density volume, into a packed 8 B list uploaded with the mesh. Per frame the GPU does everything (cull, LOD bucket, instance record, indirect count draws); the CPU touches no instance. The near set that needs identity and collision is a view over the same lists of nearby LOD-0 chunks, not a second generator.
- **Why the ring's GPU enumerator is not copied one-to-one:** a ring's density is a 1D table, so the GPU can hash and accept by itself. A planet's acceptance needs the generator (a CPU register machine over double-lattice noise, different per kind). Accepting on the GPU would need the same per-cell generator outputs uploaded per chunk, would save no CPU, and would add a C#/GLSL parity surface for every rule. So the integer-hash identity and the deltas-only storage are reused, and the generator stays on one side.
- **Rock features of 8 m and more are terrain, not decoration**: SDF stamps in the planet kind (minable, collidable, visible at every LOD that can show them). Everything thin (trees) or under 8 m (boulders, bushes, outcrops) is an instanced mesh.
- **Far away, forests are colour**: a canopy term goes into the coarse terrain materials (LOD ≥ 3) and into the planet impostor bake, so vegetation reads from orbit and the thinning of instances with distance is hidden.
- **Budgets (dense forest at ground level, 1080p, the dev machine's GPU class):** ≤ 1.8 ms GPU, ≤ 0.03 ms main thread, ≤ 100 MB GPU memory, +5-15 % on a typical chunk job (up to ~30 % on a saturated LOD-2 forest leaf). Zero bandwidth for untouched decoration.

## 1. Scatter model

### 1.1 Classes and cells

Cells are cells of the generator lattice at a fixed LOD, so they nest (the lattice side is a multiple of 2^15) and every peer and every chunk LOD names the same cell.

| Class | What | Size | Cell (lattice LOD) | Slots | Max density | Emitted by chunk LODs | Reach |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A. Cover | Grass tufts, flowers, pebbles, small plants | < 0.4 m | none (terrain vertices, GPU) | up to 8 per vertex | ~8 per m² | 0 | 96 m |
| S. Small props | Stones, small bushes, debris, crystals, small ore outcrops | 0.2-1 m | LOD 2 (2.4-4 m) | 4 | 1 per 4 m² | 0 | 192 m |
| T. Standing | Boulders 1-8 m, bushes, trees to ~20 m, large outcrops | 1-20 m | LOD 3 (5-8 m) | 4 | 1 per 16 m² | 0-3 (knob: 4) | 1.5 km (3 km) |
| L. Landmark | Giant trees, fungus towers, crystal spires, minor points of interest | ≥ 20 m, thin | LOD 6 (38-64 m) | 4 | 1 per 10,000 m² | 0-6 | 12 km |
| P. Points of interest | Wrecks, ruins, outposts (authored) | any | the kind's `LandingSites` cells | 1 per site | per kind | realised entities | see section 2 |

- **Cell sizes** follow `CubeLattice`: the arc per lattice step runs from 0.60 to 1.0 of the voxel size (rate 0.5 to 0.83 against `MaxArcPerT`), so cell areas vary by up to a factor of two over a face. Every cell therefore carries an area factor (from `CubeLattice.ArcPerG × Slope` on both axes, quantised to 1/256) that scales its expected count. Density is per square metre of datum sphere everywhere, including at cube edges.
- **Max density is enforced by the set compiler** (the sum of a class's species densities, before overlap rejection) because the per-chunk and in-view caps of section 4 are sized from it. L was 1 per 1,000 m² in the draft: that is 4,194 per LOD-6 chunk against a 1,024 list cap and 452k inside the 12 km reach, so it is 1 per 10,000 m² (≤ 420 per LOD-6 chunk, ≤ 45k in reach).

### 1.2 Per cell, all integer after the site sample is quantised

1. `h = DecorHash.Cell(bodySeed, class, face, i, j)`, built from `Noise.Mix` (32-bit, already the generator's mixer).
2. **Site sample** for the cell: the planet kind's outputs at the cell's lattice point, at the cell's own lattice LOD: height, `inv` (slope), curvature (second difference of the neighbouring heights), surface material from the kind's surface rules, the kind's mask channels (belts, dunes, crater `fresh`, flow, ice, `pads`), `|sin latitude|`, the continent channel (distance-to-sea proxy) and up to four **decor channels** the decoration set declares (`moisture`, `grove`, ...: extra `fbm` layers compiled into the kind program, flagged site-only so they are never evaluated per voxel column).
   - A lattice point's value depends only on (face, i, j, LOD) and the program (planet-kinds' exactness rule), so the site sample is identical for every chunk and every peer. This is what makes an instance drawn from a LOD-3 chunk at 1 km the same instance, same species and size, as the one a LOD-0 chunk makes when the player arrives. Acceptance never reads the chunk's own LOD-dependent data for a class that is drawn from more than one LOD.
   - **S is LOD-0 only, so its site is the chunk's own LOD-0 lattice** (height, `inv`, material and masks at the cell's lattice point, already computed for the fill) plus the decor channels of the T cell that contains it (cells nest, so no interpolation). No second site window is evaluated for S.
   - **Quantised once.** Each site input is rounded to an integer as it is sampled: height to 1/16 m (`int`), `inv` and curvature to 8 bits, channels, masks and `|sin latitude|` to 16 bits, material as its byte, the area factor to 8 bits. **Everything after this line is integer**, so no rule can round differently on two machines, in Debug and Release, or in a scalar and a SIMD path. The one float step left is the generator's own output, which is the kind program's determinism contract (its golden hashes) and is backed at run time by the server's list check (section 7).
3. **Expected count per species** `λ_s = density_s × Π ramps(site) × area` in 0.16 fixed point. `density_s` is a fixed-point integer from data. Each ramp is a 33-entry integer table over one quantised input (built by the set compiler with add, multiply and divide only; linear between entries in integer); products are taken in 64 bits and shifted back after each factor. The class total is clamped to 3 of the 4 slots.
4. For each slot `s < 4`: `hs = Mix(h + s)`. The slot is live if `(hs & 0xFFFF) < λ_total / 4`. Species comes from a second 16-bit field against the cumulative `λ_s`. Size is a 6-bit log step in the species' range, yaw 6 bits, position 10 + 10 bits inside the cell inset by the species' exclusion radius (the ring's fit rule: a base never crosses its cell). Clumping (groves, clearings, boulder fields) comes from the `grove` channel and the kind's masks, not from a Poisson draw.
   - **Liveness is per (slot, surface kind).** Open-sky, roofed-floor and ceiling species have their own `λ` totals and their own hash salt (`Mix(h + s + 4·kind)`, and `^ bucket` for roofed layers), so a cave under a bare desert is still dressed and stacked floors do not repeat. Roofed rules cannot use the surface site: they read the anchor voxel's material, depth below the lattice height and the local normal (LOD-0 data, which is safe for a LOD-0-only layer).
5. **Overlap rejection, integer, non-recursive.** A slot is dropped if its base circle (1/64 m units) overlaps an earlier live slot of its cell, or a live slot of a larger class in the containing cell (L, then T, then S; cells nest, so the containing cell is the only one that can overlap). "Live" here means it passed step 4, before any rejection, so the test never recurses through the classes (the ring's candidate rule). Trunks never stand inside boulders.
6. **Exclusions from site inputs:** never inside a `pads` core (landing benches stay clear), never where the surface rule says water (sea mask) unless the species is flagged `submerged`, and no T or L where the kind's cave `mouths` mask is on (a shaft there exists at LOD ≤ 2 only, so a far tree would vanish on arrival).

### 1.3 Identity

`DecorId` (`ulong`): `class 2 | face 3 | i 21 | j 21 | slot 2 | ceiling 1 | roofed 1 | bucket 8 | spare 5`.
- 21 bits per axis cover the S lattice of a 4,000 km body.
- An open-sky instance (the first crossing from above) has `roofed = 0`, `bucket = 0`: at most one per slot, and its id does not depend on which chunk LOD found it.
- Roofed floors and ceilings add `bucket = floor((altitude − siteHeight) / 2 m) + 128` (integer, from the 1/64-voxel anchor and the 1/16 m site height; ± 256 m covers the 200 m cave depth), so one column can carry a tree on the arch, a fern under it and a stalactite in the cave below. These exist only in LOD-0 lists (section 1.4). Two crossings of the same kind in one 2 m bucket keep the lower one only, so an id is never shared.
- Ids are never stored for untouched decoration. An id is the key for deltas, events, harvest requests and collider bookkeeping.
- **State only moves forward:** live → stump → removed, or live → looted. Each step pays its yield once, and no rule, compaction or reload moves an id back.
- **The decoration set is frozen with the kind.** `DecorProgram.ContentHash` is stored in the universe beside the kind's program hash (planet-kinds' lock). A changed set is a new set id: otherwise every id would re-roll, harvested trees would return and deltas would point at other instances.

### 1.4 Placement on 3D terrain

The chunk job places instances after the volume fill and before meshing, from the **generated** density (the pre-edit copy `PlanetChunkScratch.Generated` when edits replay), so identity and anchors never depend on edits.

- **First guess, no march:** the column's direction gives the lattice height by the same bilinear lookup `Fill` uses. On a chunk with no 3D layer that is the surface.
- **Refine on the SDF, in integers:** density is a clamped signed distance (`sbyte`, 1/32 voxel, saturating at ±4 voxels). The column is walked in chunk-local 1/64-voxel integer coordinates: its entry point and step are rounded once from the column's double direction (itself built from the cell integers with add, multiply, divide and `sqrt`). Density at a position is the integer trilinear blend of the 8 neighbouring bytes (6-bit weights, fits 26 bits). The anchor is found by stepping along the column by that value: 2-3 steps, at most 6, converged under 1/16 voxel. No mesh and no float is involved, so the anchor, its bucket, the owning chunk and the packed record are the same bits on every peer.
- **Chunks the kind flags as 3D** (`overhang3d` inside its mask, `caves3d` presence cell: planet-kinds' bounds already know): each live slot scans its column through the chunk's voxels at one-voxel steps (≤ 56 nearest-voxel reads), then refines every sign change. Air above and rock below is a floor, the reverse is a ceiling.
  - The **open-sky** floor of a column is its first crossing from above, at every LOD, so a column with a live open-sky slot has exactly one such instance whatever the 3D layers do. A chunk decides it with its own voxels and, for slots whose scan reaches the chunk top inside the 3D band, the kind's analytic density above the chunk (≤ 12 point samples, to the lattice height plus the kind's 3D amplitude).
  - A floor more than the 3D amplitude + 2 voxels below the lattice height is **roofed** with no sampling: the heightfield term guarantees rock above it everywhere outside the `mouths` mask, and T and L are excluded there (section 1.2).
- **Species by crossing kind:** open-sky floors take the set's surface species; roofed floors take `cave-floor` species (fungi, crystals, rubble); ceilings take `ceiling` species (stalactites, hanging roots), with length clamped to the clearance scanned below.
- **Normal:** integer central differences of the same blend at the anchor (6 taps), packed straight into the record's 4 + 4-bit axis. Each species has `align` (0 = grows along local up, 1 = sits on the normal) and a slope limit on the **local** normal for S, tested as an integer inequality on the difference vector (S is LOD-0 only, so a local test cannot disagree between LODs).
- **Clearance** applies to roofed-floor and ceiling species only: a tall one needs air at half and full height along its axis, within the chunk volume (nearest-voxel sign reads). Open-sky instances take no clearance or local-slope test, because those would read LOD-dependent voxels and a tree shown from a LOD-3 chunk could then be missing from the LOD-0 list.
- **Ownership:** the chunk whose core cube holds the anchor owns the instance. Exact at LOD 0 (where identity, collision and harvest live). Between two chunks of different LOD a column's anchor can land on either side of the seam (the band-limited surfaces differ), so a far tree can be drawn twice with the same pose or missed once in a seam; both are invisible at the distances where seams between LODs sit.
- **Cliffs:** radial columns sample steep faces by `cos(slope)`, so walls get sparse decoration and vertical faces none. That is the intended look (bare rock); wall-specific growth would need a second column family and is not planned.
- **Coarse LODs** anchor on their own band-limited volume, so an instance sits on the mesh that is actually drawn; the anchor slides with the terrain's own LOD morph.
- **Without a loaded chunk** (tests, far blasts, the server enumerating what a crater removed): `DecorScatter.Query(cell)` does the same on the kind's analytic density, open-sky instances only, ~3-5 µs per cell.

**Cost per accepted instance (estimates):** ~70 ns on a plain chunk (3 refine samples, 6 normal taps, pack); ~0.3-0.6 µs in a 3D-flagged chunk. A LOD-0 chunk holds ~110 instances in a typical forest and at most ~300 at both class maxima (1,024 m² × (1/4 + 1/16)), so the anchors cost ~8 µs typically and ≤ 21 µs plain, ≤ 180 µs 3D-flagged.

### 1.5 Edits and undermining (derived, no storage)

After placement on the generated density, the job tests each instance against the chunk's **edited** density:
- **Unsupported:** air at 0.25 m below the anchor along its axis (for a base wider than 1 m, at two or more of four points on the base circle).
- **Buried:** rock at 0.75 m above the anchor.
- **Blasted:** within a density-removing op's radius + 1 m (craters, bombs, drills). A `Scour` (section 5) is not an op and is not tested here: it arrives as id deltas.

Any of the three removes the instance from the list. This costs no storage and is identical on every peer, because edits are (integer sphere tests on the packed anchors; ≤ ~300 instances × < 64 pending ops, ≤ ~15 µs, only in chunks that have ops). It follows planets.md's edit visibility rule for free: a coarse chunk only sees the ops it replays. Landmarks and felled trees must also vanish from far lists that skip small ops, so the server turns removals it can enumerate into id deltas (section 7).

## 2. Classes and their treatment

**A. Ground cover: GPU only.** Source data is the terrain mesh itself. `cover_gen.comp` reads the packed vertices (position, octahedral normal, material) of LOD-0 chunks within 96 m. Each Surface Nets vertex stands for about 1 m² of ground and spawns up to 8 candidates at hashed offsets in its tangent plane; the cover table maps voxel material to cover types and densities (turf: tufts and flowers; gravel and regolith: pebbles; cave soil: moss).
- It follows edits with no code: dug ground has a new material and new vertices, so grass stops at the hole. It works on cave floors and overhangs because those have vertices and normals too.
- No memory per chunk, no CPU work per chunk, no identity, no collision, nothing harvestable (hand-gathered plants are S props).
- Density by distance: full to 24 m, a rank cut falling to zero at 96 m, clump width scaled up to keep coverage. The speed LOD bias drops LOD 0 above ~380 m/s, and cover goes with it.

**S. Small props: instanced, identity, light collision.** LOD-0 lists only. Under 0.4 m: no collision (walked over; found by a ray against instance bounds). 0.4-1 m: a shared convex shape near characters. Hand-harvestable (stones, fibre, small ore outcrops that hint at a vein below).

**T. Standing: instanced, identity, real collision.** Trees to ~20 m, bushes, boulders of 1-8 m, large outcrops. Mesh LODs near, octahedral impostors beyond, canopy colour past their reach. Collision near characters and under ships. Felled, mined, crushed or blasted; break into pieces.

**L. Landmarks.** The same pipeline at the 64 m lattice with a 12 km reach: emergent giant trees, crystal spires, fungus towers, and minor authored points of interest (a crashed pod, a cairn, bones) as species with a glTF mesh and a loot table.

**Rock: mesh or terrain stamp, by size.**

| Rock size | Form | Why |
| --- | --- | --- |
| < 0.4 m | Cover pebbles (A) | Too small to matter |
| 0.4-8 m | Instanced mesh (S, T) with a convex collider, mined as one object, breaks into smaller debris | At 1 m voxels a boulder under 8 voxels across meshes as a blob; a rock mesh looks right and costs 80-1,280 triangles |
| ≥ 8 m (giant boulders, spires, arches, hoodoos, ejecta blocks) | **Terrain SDF stamp** in the planet kind | Ships land on it, players tunnel into it, it shows on the silhouette and in the LOD chain, and it needs no second collision path |

Proposed addition to planet-kinds (its doc, not changed here): a 3D stamp layer `boulders3d` on its class grids (rounded SDF blobs, r 4-30 m, unioned in the 3D phase, the same band-limit rule as craters). Its spire and hoodoo layers (`cellular`, `overhang3d`) already cover the rest. Decoration then dresses those features: T and S rules can key on the stamp masks (talus rocks round a spire, moss on boulders).

**P. Points of interest: hooks only.** Planet-kinds provides `LandingSites.Query` and says settlements, wrecks and markers attach to sites. A decoration set lists what may occupy which site type and how often; the pick is `DecorHash.Site(bodySeed, siteCell)`. The asset is authored: a glTF prop group, or a ship blueprint keyed by content hash for wrecks (the hull pipeline draws and collides it). Far away it is drawn as an L-class proxy. Within 2 km of a player the server realises it as entities (loot containers, wreck hull). Its state (looted, salvaged) is a delta keyed by the site id. Settlements, ownership and missions belong to the factions doc.

## 3. Assets

**Meshes: procedural by default, authored where data says so.**
- **Rocks:** the ring asteroids' archetypes (16 SDF archetypes, Surface Nets, 3 LODs of 1280 / 320 / 80 triangles), re-used with axis scales and a per-set material. One rock library for asteroids and planets.
- **Plants and trees:** a parametric generator (trunk and branch recursion, crown envelope, leaf cards) from species parameters. Any parameter may be a `[min, max]` range resolved per body seed (planet-kinds' rule), so each living world has its own flora. 3 variants per species, LODs by `Brutal.MeshOptimizer`. Baked on workers when a body becomes the focus body (~3 ms per variant, ≤ ~0.25 s of worker time per body, estimate) and cached on disk by content hash.
- **Authored:** a species with `"mesh": "x.glb"` loads through `Brutal.Gltf` (points of interest, hero plants, anything the generators do badly). Same LOD and impostor path.
- **Collision shapes never come from the render mesh.** Trunks are capsules from the species parameters; rocks use convex hulls (≤ 24 vertices) from a baked table `data/decor/rock-hulls.bin`, CRC-checked in the handshake, so a headless server builds identical colliders with no mesh code.
- **Variant and archetype are an integer hash of the packed record** (`DecorHash.Variant(lo, hi)`, the same `uint` code in `decor_hash.glsl`): the record has no spare bits, the GPU picks the mesh with it and the CPU picks the rock's collider hull with it. This is the one place where C# and GLSL must compute the same thing, and it is `uint` multiply, xor and shift only (no `%`, no float). Every other GPU decision (frustum, LOD bucket, fade, thinning, tint, wind) is float and visual only; nothing the GPU decides is read by gameplay.
- **Impostor layers are capped by the memory budget:** 16 rock archetype layers at 256² (0.17 MB each, shared by every set) and ≤ 30 plant layers at 512² (0.7 MB each: 10 T and L species × 3 variants) is the 24 MB of section 4.4. The set compiler rejects a set that needs more; S species never get an impostor (they fade out by size).

**LOD chain per species.**

| Stage | When (projected height at 1080p) | What |
| --- | --- | --- |
| Mesh LOD 0 / 1 / 2 | > 400 px / > 160 px / > 96 px | Tree: ~8k / 1.5k / 300 triangles; rock: 1280 / 320 / 80 |
| Octahedral impostor | 96 px down to 3 px | One quad; 6 × 6 hemi-octahedral views, albedo + alpha and normal + depth, 512² per species in two texture arrays (BC7 + BC5, ~0.7 MB per species), baked on the GPU at the mesh bake and cached as KTX2 (`Brutal.Ktx`). 10 % dithered crossfade with mesh LOD 2 |
| Thinned impostor | The class's last chunk LOD | One live slot per cell at most, scaled ×1.25, over canopy-coloured ground |
| Canopy term | Past the class reach: terrain LOD ≥ 3 and the planet impostor | No instances. `DecorCanopy(site)` = Σ density × crown area, clamped to 1, with the set's mean colour. At LOD ≥ 3 the kind's per-point surface rule picks the set's `canopy` display material where cover exceeds a hashed dither. `PlanetImpostor` gains a **canopy channel** (R8 per texel, BC4: +0.2 MB per body, +4 MB for the 1024² focus bake) evaluated from the same rules at the texel's band limit, and `body.frag` blends the canopy colour in, so forests, tree lines and the bare poles read from orbit |

**Wind.** The client already has the weather sample (`WeatherSystem.Local`, from the `SphereWeatherCache` round the camera). One wind vector and gust strength per frame go into the decoration uniforms; the vertex shaders sway trunks by height², flutter leaf cards, shear impostor tops and bend cover, with phase from the instance hash. Airless bodies have no weather, so nothing moves. Cost is a few ALU per vertex.

**Season and latitude tint.** A body's season phase comes from its orbit position and axial tilt (tick, zero bandwidth). The cull pass writes an 8-bit tint coordinate per instance from latitude, season and the instance hash; the species has a 16-texel gradient (spring to winter). Deciduous species raise their alpha threshold in winter (bare crowns). The canopy colour in `body.frag` and `terrain.frag` uses the same gradient by latitude, so the planet changes colour with the seasons from orbit.

## 4. Rendering

### 4.1 Data on the GPU

- **Packed list per chunk** (`DecorInstance`, 8 B): `position 3 × 11 bits` (1/64 voxel inside the chunk core: 1.6 cm at LOD 0) `| species 8 | size 6 | yaw 6 | axis 4 + 4` (octahedral, relative to the chunk's up) `| flags 3` (ceiling, removed, thinned). At most 1,024 per chunk (8 KB) at LOD 0-2 and LOD 4-6, and 2,048 (16 KB) at LOD 3, where one live slot per cell over 65,536 m² of 32-64 m² cells is up to ~1,900. A typical forest LOD-0 leaf has ~110 (0.9 KB). A job that would pass the cap drops the highest slots first and counts it in the profiler; the compiler's density limits (section 1.1) are what keep that at zero.
- **Decor arena:** 8 MB device-local (1M records) in 64-record pages, with a page table (page → chunk slot). A chunk's list is uploaded in the same staged copy batch as its mesh and freed with its handle. `DecorChunks[slot] = (firstPage, count)` sits beside the terrain chunk table and uses the same slot index, so the chunk origin, voxel scale and camera-relative maths are the ones `terrain_cull.comp` already has.
- **Species table** (64 B per species, ≤ 256): bounding radius, mesh bucket per LOD, pixel thresholds, impostor layer, tint gradient row, wind parameters.
- A removal (harvest, felling) patches one 8 B record in place (the `removed` flag, or the species byte swapped to the species' stump): no list rebuild, no chunk remesh.

### 4.2 Passes

| Pass | Work | Notes |
| --- | --- | --- |
| `decor_cull.comp`, count | One workgroup per arena page. The group tests its chunk's bounds (grown by the tallest species) once; then per record: decode, camera-relative position in exact integer chunk steps, sphere-frustum test, projected size, mesh LOD or impostor bucket, dither fade. Writes a bucket byte per record and `atomicAdd`s the bucket's count | ≤ 16k groups; a culled chunk's pages return after one test |
| `decor_prefix.comp` | One workgroup: prefix sums over ≤ 512 buckets, writes one `VkDrawIndexedIndirectCommand` per bucket (mesh range from the species table, `instanceCount`, `firstInstance` in the decoration range of the instance store) | Bucket order is priority: meshes (everything that can have a collider) first, impostors last. Each bucket's `instanceCount` is clamped to what is left of the decoration range, and the emit pass drops a record whose slot is past its bucket's clamped count, so an overflow cuts far impostors and never writes outside the range |
| `decor_cull.comp`, emit | Re-reads the bucket byte, takes a slot with `atomicAdd`, writes the shared 32 B `InstanceRecord` (`relOrigin`, scale, rotation from axis + yaw) and a 4 B `DecorExtra` (hash seed, fade, tint coordinate, wind phase) | Review C9's record is unchanged; the extra word is decoration's own buffer indexed by `gl_InstanceIndex − base` |
| `cover_gen.comp` | Threads over the terrain vertices of the ≤ 96 LOD-0 chunks in range (the CPU writes their vertex ranges into a small uniform array; the thread finds its chunk by binary search). Hash, material and normal test, distance rank cut, frustum test, append an 8 B clump record | Needs `StorageBufferBit` on the terrain vertex arena. A range covers the chunk's surface vertices only (skirt vertices are appended after them), and the thread skips vertices outside the chunk's core cube (the border cell layer that the neighbour also meshes), so seams get neither doubled nor missing cover |
| Draws | `vkCmdDrawIndexedIndirectCount` × 3 over the bucket commands: opaque (`decor.vert/.frag`: rocks, trunks), alpha-tested two-sided foliage (`decor_foliage.frag`), impostors (`decor_impostor.*`: one quad mesh, one bucket). One instanced indirect draw for cover (`cover.*`), its count written by the GPU | All decoration meshes share one vertex format and one arena, so buckets differ only in index range |

Nothing is read back in the frame loop. Stats (instances per bucket, drops) are copied to a readback buffer only while a profiler panel asks, as terrain does.

### 4.3 The instance store (M1 carry-over)

`InstanceBuffer.GpuCapacity` is 8,192, exactly terrain's `MaxHandles`, and `TerrainRenderer.Cull` reserves its whole table high-water mark, so a second GPU user silently gets −1. Fix with this work:
- The device-local store is split into **named fixed ranges registered at start-up**: Terrain 8,192, Hulls 4,096 (ai-ships' ≤ 4,096 instance records), Decoration 131,072. That is 143,360 records × 32 B = 4.6 MB per frame in flight, 9.2 MB over the two frames in flight (was 0.5 MB). Decoration's own share is 8.4 MB.
- Rings are not in that sum: rings-and-asteroids.md budgets a 64k-record instance buffer of its own (2 MB). If it moves into the shared store it registers a fourth range (+4.2 MB over two frames); the ranges are one table, so that is one line.
- Decoration's range covers its caps in view (section 4.4): 8k S + 4k T meshes + 96k impostors = 108k ≤ 131,072.
- `Reserve(InstanceUser user, int count)` reserves inside the user's range. A failed reserve is loud: a per-user overflow counter in the profiler, one log line, and a Debug assert. A user that needs more changes its registered size in one place.
- Cover does not use instance records (its 8 B clump buffer, 262,144 entries, 2 MB a frame).

### 4.4 Draw distance and density budgets

Caps live in `data/render/decor-lod.json`; the budgets are held by turning them, as the hull ladder's are. Estimates until `--decor-bench` measures them.

| Class | Reach | Cap in view | Triangles (worst) |
| --- | --- | --- | --- |
| Cover | 96 m | 200k clumps | 2.4M alpha-tested, small on screen |
| S | 192 m, faded by size at ~150 × size | 8k | 0.3M |
| T meshes | to 96 px (≈ 150 m for a 15 m tree) | 4k | 1.1M |
| T + L impostors | 1.5 km / 12 km | 96k quads | 0.2M |

Steady state in dense forest at ground level (estimate): ~275k records in loaded lists (2.2 MB), ~90k in the frustum.

**Arithmetic behind the caps** (flat ground at the class maxima, a 90° horizontal view, so a quarter of each ring is in the frustum; 1080p, 60° vertical, where a 15 m tree is `14,000 / d` px tall):

| Check | Sum | Against |
| --- | --- | --- |
| Records loaded | S 29k (LOD-0 disc of 192 m at 1 per 4 m²) + T 7k + 22k + 87k (LOD 0, 1, 2 rings at 1 per 16 m²) + 111k (LOD-3 ring, one per ~50 m² cell) + L ≤ 45k = ~300k | Arena 1M records (8 MB): 3.5 × headroom. 275k is the same sum with S at a forest-floor density |
| T mesh triangles | 60 trees within 35 m × 8k + 320 to 88 m × 1.5k + 670 to 146 m × 300 = 1.16M, 1,050 instances | Caps 4k and 1.1M: instances have 4 × headroom, triangles none. Mesh LOD 0 is the knob |
| S meshes | 17,700 m² in view inside 150 m at 1 per 4 m² = 4.4k × ~80 triangles = 0.35M | Cap 8k, 0.3M: at the limit at the class maximum |
| Impostors | T: 28k (150 m to 768 m) + 28k (to 1,536 m, thinned) = 56k; L ≤ 15k in view | Cap 96k quads |
| Impostor fill | Quad area sums to ~21M px (about 10 screens of overdraw before depth rejection), 88 % of it inside 768 m | The riskiest line of the GPU table. Near impostor buckets are drawn before far ones so early depth rejects most of it; the knob is the mesh-to-impostor distance |
| Cover | 452 m² within 24 m × 8 + 6,800 m² to 96 m at a falling rank cut ≈ 25-50k clumps in view | Cap 200k: 4 × headroom, so the 2.4M-triangle worst case is the cap, not the expectation |
| Cover generation | ≤ 96 chunks × ~1,100 surface vertices × 8 candidates = 0.85M hash tests a frame | Compute, ~0.05 ms (estimate) |

| Item (1080p, the dev machine's GPU class) | Worst case: dense forest | Typical | Airless moon |
| --- | --- | --- | --- |
| GPU: cull, prefix, emit | ≤ 0.12 ms | 0.05 | 0.02 |
| GPU: S / T / L meshes | ≤ 0.5 ms | 0.25 | 0.10 |
| GPU: impostors | ≤ 0.3 ms (was 0.2: see the fill row above) | 0.10 | 0.02 |
| GPU: cover (generation + draw) | ≤ 0.5 ms | 0.25 | 0.05 (pebbles) |
| GPU: shadows | ≤ 0.4 ms | 0.20 | 0.10 |
| **GPU total** | **≤ 1.8 ms** | **≤ 0.85 ms** | **≤ 0.3 ms** |
| Main thread per frame | ≤ 0.03 ms: 4 dispatches, 4 indirect draws, one uniform block, ≤ 96 cover ranges, ≤ 64 record patches | | |
| GPU memory | Lists 8 MB, decoration's instance range 8.4 MB, extras 1 MB (131,072 × 4 B × 2 frames), cover buffer 4 MB, mesh arena 48 MB, impostor arrays 24 MB, canopy channels ≤ 5 MB: **98.4 MB, ≤ 100 MB** for the focus body | | |
| CPU memory | List mirror and ids ≤ 12 MB (1M × 12 B, unmanaged), site scratch +64 KB per job, delta blocks ≤ 4 MB resident per isolated player at saturation (typically < 100 KB), colliders ≤ 2 MB per bubble | | |

Against a 16.6 ms frame this sits beside hulls (≤ 2.0 ms), terrain (0.07-0.12 ms GPU on the Debug smoke bench, one view; the forest view is unmeasured) and atmosphere (0.1-0.2 ms). Decoration plus hulls at their worst cases is 3.8 ms, 23 % of the frame. The dense-forest worst case and the 200-hull worst case can coincide over a forest base; `--decor-bench` has a leg for that.

**Shadows.** No scene shadow system exists yet (rings-and-asteroids notes the same gap). When it lands: T and L meshes go into the two nearest cascades at mesh LOD + 1 (≤ 0.6M triangles); impostors cast as cards into the next cascade; beyond that the canopy material darkens its own ground (an analytic term from canopy cover and sun height). Cover never casts; it takes the terrain's shadow and a contact-darkening term at its base.

### 4.5 Streaming at flight speed

- The decoration stage runs inside the chunk job, so it scales with the leaves/s that M2 measured, on workers only.

  | Chunk LOD | Stage cost (estimate) | What it does |
  | --- | --- | --- |
  | 0 | ~30 µs typical, ≤ 50 µs at the class maxima (+30-60 µs if 3D-flagged, ≤ 180 µs at the maxima) | T site window of ~64 lattice points at lattice LOD 3 (~14-20 µs: the 16 m to 1 km octaves per point, decor channels, compose and stamps, ~220-300 ns a point; a LOD-0 job's own lattice values cannot be reused because LOD 3 is band-limited differently). S reads the chunk's own lattice. ~110 anchors typical, ≤ 300 |
  | 1 / 2 | ~40 / ~95 µs typical, ≤ 55 / ≤ 170 µs at the T maximum | T site windows of ~144 / ~400 points; up to 256 / 1,024 T anchors |
  | 3 | ~40 µs + ≤ 30 µs canopy | Site is free (the chunk's own lattice); one live slot per cell |
  | 4-6 | 15-40 µs + ≤ 30 µs canopy | L class on a LOD-6 lattice window |
  | Empty leaves (~65 % today) | 0 | No surface, no stage |

- **Canopy at LOD ≥ 3 is not free:** the canopy material needs `DecorCanopy` per lattice point (2,000-5,000 a leaf). Evaluated per point with its decor channels it would be +0.1-0.2 ms a leaf, so it is evaluated on the coarse grid (every 8th point, 1/64 of the work), interpolated, and compared with a per-point hashed dither: ≤ 30 µs. It is visual only (the server builds no LOD ≥ 3 chunk).
- 300 m/s at 100 m (1,456 jobs/s, ~510 with a surface): ~25 ms of worker time per second typically (≤ ~70 ms at the class maxima, 0.07 core) and ~0.8 MB/s of list uploads. 3,000 m/s (finest LOD 3): ~760 surface leaves/s × 70 µs = 0.05 core. On a 0.6-0.9 ms leaf the stage is typically +5-15 %; a saturated LOD-2 forest leaf is up to ~+30 %.
- Nothing per instance ever runs on the main thread. Lists arrive and leave with their chunks; the list count for a slot is one 8 B table write.
- Meshes and impostors of the focus body's set are baked when it becomes the focus body (~800 km up), long before decoration is first visible (LOD ≤ 6, below ~12 km).

## 5. Interaction

**Harvesting.** A species carries `harvest`: tool class (hand, cutter, drill, mining laser), seconds of work, and yields as crafting item ids with kg ranges scaled by size³.
- Trees give `wood.log` and `fiber.plant`. A felled tree becomes a felled-trunk entity (a normal world entity, cut into log items), and the instance becomes a stump or is removed.
- Rocks give `stone.loose` plus the local material's ore items.
- **Ore outcrops** are S and T species whose rule keys on the kind's public vein channels, so outcrops mark veins below: prospecting by eye. Exotic matter stays hidden (its lenses are server-only), so no outcrop ever marks it.
- The client sends `HarvestRequest {DecorId, tool}`. The server checks that the id exists in its LOD-0 list, reach, tool and land rule, runs the work time, then writes the delta, grants items through the inventory ledger and broadcasts a `DecorEvent`. The client shows progress, never the result, before the event.
- **No second harvest, ever.** The server builds a LOD-0 list only after the block's deltas are loaded from the tile file, so an id removed before a restart, an unload or a trip away is absent from the list and the request fails the first check. State only moves forward (section 1.3), deltas are never aged out or rounded away (section 7), and yields are computed by the server from its own list (species, size), never from the request. A request for an id the server does not have is answered with `DecorEvent {Absent}`, which also patches the instance out on that client.

**Destruction.**
- **Weapons and blasts:** every blast, impact or plume that should clear decoration raises a **`Scour`**: a server event (centre, radius, cause, actor), **not an edit op**. The draft filed it as a `BrushMode` in the op log; 64 scours would then have compacted an untouched chunk into a 3-8 KB density snapshot, and a hovering ship could have written those for free. Instead the server resolves a scour at once into id deltas and sends the result:
  - radius ≤ 32 m: the exact set, from the LOD-0 lists it has loaded and `DecorScatter.Query` elsewhere (≤ ~1,000 ids at both class maxima), as one `DecorEvent` batch of 2 B local ids per block and class (≤ 2 KB, typically ~0.3 KB);
  - larger: every 32-cell sub-block whose centre is inside the radius is cleared (64-bit masks, 8 B per block and class). The edge is ragged at 19-32 m, which suits a blast.
  - Crater ops still clear to their radius + 1 m by the derived rule of section 1.5. Scours never touch terrain, so the D30 clamp is irrelevant to them. Storage is the delta store's, with its bound.
- **Ships:** a contact impulse above the species' `breakImpulse` (scales with trunk radius²) fells or shatters the instance; below it the tree is a static obstacle and the hull takes normal contact damage. Light craft are stopped by big trees; heavy ships crush a landing zone.
- **Landing thrust:** a thruster plume whose heat flux at the ground passes a threshold raises a `Scour` at its footprint (at most one per second per ship; a second scour over ground already cleared removes nothing and sends nothing). Cover has no identity, so scorched cover is client-side and not stored: each client keeps the last 64 scour spheres within 200 m for `cover_gen.comp` (≤ 8 per chunk in a side buffer), and the grass is back when the area is next loaded.
- **Warp exits low in an atmosphere (D16):** the `WarpBlast` pressure wave emits a `Scour` with the blast radius; the damage feeds the same `IncidentRecord`.
- Fire is out of scope. So is wildlife (D16 mentions it; it needs its own design).

**Terrain edits under decoration (the rule).** An instance whose ground is removed, or which is buried, is removed (section 1.5). If the server has the chunk loaded when the edit lands (always true for tool edits), it also enumerates the instances the edit unseats and resolves each once:
- trees fall as felled-trunk entities, so digging one out is a legitimate way to fell it, attributed to the edit's author;
- T-class rocks (1 m and more) become loose dynamic bodies, which are ordinary entities and may roll;
- S props, rocks of 0.4-1 m included, drop their hand-harvest items or vanish (the draft made them bodies too: one 4 m dig stroke in a boulder field would have spawned ~12 dynamic bodies, ~100 a second at the edit rate cap).

Each resolved instance gets an id delta, so later edits (filling the hole back in) never bring it back.

**Caps on what unseating and felling spawn** (so nobody can flood a bubble with bodies or bury a parked ship):
- at most 4 new bodies per edit op, and 64 felled trunks + 64 loose rocks per bubble. Past either, the instance breaks straight into its yield as item piles. It still gets its delta, so nothing is lost or duplicated.
- a trunk or loose rock that nobody has touched for 30 played minutes, with no player within 200 m, breaks up the same way. Trunks and rocks are dynamic and cuttable, so a ship can push through them or a cutter can clear them.

**Protected shore band (D30).** Terrain there cannot be removed, so nothing is ever undermined on a beach or seabed. Decoration there can still be harvested, felled and scoured: none of that opens a hole, so the flooding question does not arise. Species may not be placed below the sea mask unless flagged `submerged`; no submerged species ship in the first sets.

**Land ownership and permits (D57, D74; factions-and-law.md "Land and mining rights").** The draft invented a hook because it thought the law doc did not exist. It does, so decoration uses its check and its outcomes: `LandRights.Check(owner, body, direction, depth, toolClass)` → `Allowed`, `Illegal` or `Refused`, once per harvest request, cached on the tool for 1 s per (cell, claim) like a dig. Two tool classes are added (asked of that doc): `Gather` (hand) and `Fell` (cutter, drill or mining laser on T and L, and salvage of points of interest).

| Land policy | `Gather` (S props by hand) | `Fell` (T, L, points of interest) |
| --- | --- | --- |
| `Wild` (unheld bodies) | free | free |
| `Open` | free | permit |
| `Licensed` (most held land) | prospecting permit (free at Neutral) | permit, lease or freehold with `Dig` |
| `Reserved` (settlement footprints, parks, military ground) | **refused** | **refused** |

- **`Refused` means the server does not run the harvest**, as the dig clamp refuses tools there. Nobody can cut down a park or a settlement's trees with a tool, whatever fine they are willing to pay.
- **`Illegal` runs and is counted:** the yield's item value goes into the law doc's `IllegalVolume` counter for (owner, region cell or claim) and becomes an `IllegalDig` case, or `ClaimJump` on another party's claim (a freehold is not a wall), under that doc's evidence and survey rules. No new incident kind is needed.
- **Blasts, plumes, hulls and undermining are physics, and physics never depends on law** (law ground rule 1), so they do destroy decoration on any land. The cause's own record carries it: the instances' item value is added to the `ValueMilli` of the `WarpBlast`, `WeaponFire` or collision record, and a plume or hull that clears claimed or `Reserved` decoration with no other record writes a Reckless damage record (batched at 1 Hz per actor and claim). Settlement footprints always have `ZoneLog` evidence, so this is never unwitnessed there. A region shield scales a blast by its absorbed share before the scour radius is taken.
- **Residual risk, not solved here:** a player who accepts the fines can still strip a park permanently by hovering a ship over it. Tools are refused, plumes are not. See open question 2.

**Regrowth.** Default: **permanent**, like planet edits (D32) and mined asteroids. A felled tree stays felled; a harvested rock is gone. Slow regrowth of vegetation only is an open question (it would also let delta storage shrink over played time). If it is ever turned on it must be a server decision that clears the delta (the bit-per-slot form stores no removal time, so the clock would be per sub-block), it must never grow in a cell overlapped by a grid, structure, claim beacon or edit or within 10 m of one (otherwise a tree returns through a parked ship or a base), and it must not apply to anything with a yield worth farming unless the regrowth time is priced into that yield.

## 6. Collision and physics

| Class | Jolt shape | When it exists |
| --- | --- | --- |
| A cover | None, ever | Characters walk through grass. Up to 16 "pusher" spheres (characters, wheels, landing feet) in the cover uniforms bend clumps |
| S under 0.4 m | None | Picked by a ray against instance bounds on the CPU (≤ 8 chunk lists, ~1-3 µs, only while a tool or crosshair asks) |
| S of 0.4-1 m | Static body, the archetype's convex hull | Within 16 m of a character. Hulls ignore S (it is crushed without contact) |
| T, L | Static body. Tree: trunk capsule (the crown does not collide with characters; ships test the crown as a sensor that breaks branches). Rock: convex hull | Within 32 m of a character and inside a ship's anchor box (the swept AABB that already drives terrain collision below ~200 m) |
| P | The realised entities' own shapes | Within 2 km of a player |

- **Shared shapes:** one shape per (species, variant), wrapped in a scaled shape with the 6-bit size step, so a forest of 10,000 trees uses a few dozen shapes.
- **Pooled bodies, fixed quota per anchor.** The draft had 4,096 bodies per bubble, T within 64 m and ~400 bodies per player; at the class maxima a 64 m disc holds ~800 T alone, so six players in one forest bubble would have exhausted the pool, and what happens then was not defined (trees with no collision for whoever came last). Now:
  - a character anchor takes its nearest ≤ 384 bodies (worst case 201 T in 32 m + ~115 S in 16 m), a ship anchor its nearest ≤ 1,024 T and L in its box; nearest by integer distance, ties by id;
  - the pool is 8,192 per bubble (~2 MB): 16 characters and 2 ships at the class maxima, several times that in a typical forest;
  - if the anchors' quotas sum past the pool, every quota is scaled by the same factor from a table indexed by the bubble's anchor count, and that one byte is part of the bubble state clients receive. A client therefore builds exactly the server's set for its own anchor, full pool or not.
  - Bodies are added and removed in batches on the tick when an anchor crosses an 8 m cell (~20 µs per crossing to walk the lists); a ship at 100 m/s in low flight streams ~60 bodies/s. They are static and asleep: no per-tick cost.
- **Bodies arrive with their chunk.** A LOD-0 chunk's decoration bodies are added in the same batch as its terrain body (the client also needs the block's deltas first), so there is never ground with collision whose trees have none, and a ship descending through 200 m meets both together.
- **Decoration never materialises inside a grid.** When a batch would add a body that penetrates a hull, a structure or a blueprint under construction by more than 0.25 m (one `CollideShape` per added body, ~1-2 µs, only in bubbles that hold a grid), the grid got there while no collider existed: it was built or placed there, spawned, undocked or set down by a warp exit. The instance is crushed instead of added (delta `removed`, cause `Crushed`, attributed to the grid's owner, yield dropped as items). So a tree cannot appear inside a ship and pin or burst it, and building over a tree clears it. On `Reserved` ground the placement itself is refused by the land check, so this is not a way to clear a park.
- **Rays past the collider range** (weapons, tools, line of sight) test T and L instance bounds from the lists on the CPU, the walk S picking uses, so a trunk stops a shot at 300 m as it does at 10 m.
- **Bubble rebases** move decoration bodies with the terrain chunk bodies (same origin shift).
- **Server and client build the same set** from the same lists, so a client's prediction scene (review C12) contains the trees its ship can hit, at zero bandwidth.
- **Ships landing on forest:** contact, break test, then the fall rule above. A ship parked on felled trunks rests on ordinary dynamic bodies. Above 200 m there is no decoration collision; ground casts stay analytic (planets.md).
- **Loose rocks and trunks** are entities under the normal entity rules once they leave the decoration system.

## 7. Multiplayer and storage

- **Untouched decoration: zero bandwidth.** Clients need the kind program and the decoration set, both data, covered by the program content hash in the handshake.
- **Server authority:** harvest, felling, breakage, the unseat rule, point-of-interest realisation and loot. The server runs the same chunk-job stage on the LOD-0 chunks it already builds round anchors; it never builds coarse lists and loads no meshes.

| Message | Rate / reliability | Size | To |
| --- | --- | --- | --- |
| `HarvestRequest` | Client to server, reliable | 12 B | Server |
| `DecorEvent` (removed, stump, felled, crushed, looted, absent; cause; actor) | Reliable, tick-stamped | 12 B | S: within 200 m; T: within 2 km (3 km with the knob); L and P: within 12 km |
| Deltas on interest entry | Once per block entered, nearest first, ≤ 64 KB/s | Blocks within 2 km in full, landmark bits and cleared masks to 12 km; zlib; typically < 1 KB, worst 7.2 KB per block (≤ ~3.8 MB for a saturated 2 km disc, about a minute; a client adds a chunk's colliders only once its block has arrived) | Arriving clients |
| `DecorEvent` scour batch | Reliable, tick-stamped | 2 B per id (≤ 2 KB) for radius ≤ 32 m; 8 B per block and class above | As `DecorEvent`, by class |
| List check | Once per LOD-0 chunk entering a player's collision ring | 4 B CRC (≈ 200 B on arrival for ~50 chunks, ~240 B/s at 300 m/s in low flight); the list itself only on a mismatch (~1.3 KB) | That player |
| Felled trunks, loose rocks, realised points of interest | Normal entity replication | | |

**List check: the server is the authority for what exists.** Sections 1.2-1.4 make S, T and L lists integer after one quantisation, but they still stand on the generator producing the same floats everywhere, and a client that disagrees would walk through a tree the server collides with, or strike one the server does not have. So for every LOD-0 chunk in a player's collision ring the server sends the CRC32 of its canonical list (packed records and local ids, deltas applied). A client whose CRC differs asks for the list, replaces its own (records, colliders, GPU pages) and both sides log a determinism fault with their program hashes. Coarse lists are visual only and are not checked. Cost: one CRC over ~1-4 KB per LOD-0 chunk job (~1-4 µs).

**Delta store** (`DecorDeltaStore`, one file per lattice tile of 32,768 LOD-0 cells per side, ≈ 20-33 km, beside the edit region files, with a block directory so only blocks in somebody's interest are resident):
- **Block:** 256 lattice cells per side (≈ 150-256 m). Per class its open-sky state is held in the smaller of two forms (the ring's dense switch):
  - sparse: sorted 4 B entries = a 26-bit local id + state bits (removed, stump, looted);
  - dense: a bit per (cell, slot). S: 64 × 64 cells × 4 slots = 2 KB. T: 32 × 32 × 4 = 512 B, twice (removed, stump). L: 4 × 4 × 4 = 64 bits, always dense.
  - S goes dense past 512 entries and T past 256. **Open-sky state is therefore lossless at ≤ 3.1 KB per block**, however the block is harvested.
- **Cleared masks** (S and T; 64 bits per block and class, one per 32-cell sub-block, ≈ 19-32 m): set when every slot of the sub-block is gone, or by a large scour. A clear-cut forest costs 16 B a block. L needs none: its cell is larger than a sub-block and its 64 bits are the whole state.
- **Roofed entries** (cave floors and ceilings, LOD-0 lists only) stay sparse, ≤ 1,024 per block (4 KB). Past that, the block's most depleted sub-blocks get a roofed-cleared bit. This is the only lossy rule, and it can only remove cave dressing in the ~200 m block where 1,024 pieces were already taken.
- **No tile cap and no rounding up.** The draft capped a block at 2,048 entries and a tile at 2 MB by rounding depleted sub-blocks up to cleared, tile-wide. A block holds up to ~15k surface slots, so 13 % harvested would have triggered it; and 2 MB is only ~8 fully worked km² of a 400-1,000 km² tile, after which harvesting anywhere in the tile (legally, 20 km away) would have deleted other people's trees, a settlement's included. With the dense form the bound is structural and nothing is ever removed that nobody removed.
- **Compaction** runs when a tile is saved and is lossless: sparse to dense, all-gone sub-blocks to cleared bits.
- **Edit compaction:** when a LOD-0 chunk's ops bake into a snapshot (planets.md), the removals those ops derived are frozen into entries or cleared bits by the same job, since the ops are about to be dropped.
- **Bounds:** ≤ 3.1 KB surface + 4 KB roofed + masks ≈ 7.2 KB per block, forever. A fully worked km² holds 15-42 blocks, so ≤ ~300 KB, about 2 % of its terrain edit bound (~14.7 MB). A tile is ≤ 118 MB only if all 16,384 of its blocks are worked to saturation (over 10 million harvests). Harvest is capped at 4 requests/s per player (burst 16), so one player writes ≤ 58 KB of entries per played hour before the dense switch, against the 1 MB an hour planets.md allows the same player in edits. Storage grows with the area anyone has worked, never with time spent there. Game time stops with an empty server, and nothing here is keyed on time by default.
- **Resident:** blocks within 2 km of an anchor (≤ ~530 blocks, ≤ 3.8 MB per isolated player at saturation, typically < 100 KB), plus L bits and cleared masks to 12 km (≤ ~0.35 MB).
- **Operator tool:** the admin "restore region to generated" command of planets.md also clears the region's decoration deltas (griefing cleanup).
- **GPU side:** nothing. Deltas are applied when a list is built, and a live removal patches one record; there are no removed sets, bitmaps or overflow cases on the GPU (the ring needed them because its enumerator lives there).
- **Coarse lists and deltas:** a LOD 1-6 chunk job filters its T and L instances through the block's entries, bits and cleared masks (a per-block "has state for this class" bit skips the lookup almost everywhere).

**Determinism summary.**

| What | How peers agree |
| --- | --- |
| Existence, id, species, size, yaw of S, T, L | Integer hash + site samples from the kind program (add, multiply, floor, sqrt only; program hash checked), quantised once to integers; ramps, products, comparisons and overlap tests are integer: exact given the same generator output |
| Anchor position, bucket, owning chunk | Integer refine on the density bytes: exact at LOD 0 between peers given the same bytes; differs between LODs by the band limit (visual only) |
| The generator output itself (the one float dependency) | The kind program's determinism contract and golden hashes; at run time the server's CRC per LOD-0 list, with the server's list replacing a client's that differs |
| Variant, rock archetype (mesh on the GPU, collider on the CPU) | `uint` hash of the packed record, the same code in C# and GLSL, parity-tested |
| GPU cull, LOD bucket, fade, thinning, tint, wind | Float, per client, visual only; never read back |
| Unseated, buried, blasted | Derived from replicated edits; frozen into deltas by the server |
| Scoured | Server-resolved id sets or sub-block masks, sent as events |
| Harvest, breakage, crushing, loot | Server decisions, sent as `DecorEvent`s |
| Cover | Client only, GPU hash of vertex coordinates; never read by gameplay |
| Meshes, impostors | Client only |
| Colliders | Species parameters and the CRC-checked hull table: identical on a headless server |

## 8. Decoration sets per planet kind

- A kind names one set: `"decor": "temperate"` in `data/planets/kinds/<id>.json`. Sets live in `data/planets/decor/<set>.json`, species in `data/planets/decor/species/*.json` (shared between sets), cover types in `data/planets/decor/cover/*.json`. No content is hard-coded.
- **Life flag.** A set marks each species `"living": true | false`. A kind carries `"life": { "chance": 0..1 }`, resolved per body seed into `PlanetParams.Life`. A body with no atmosphere, or with `Life` false, compiles its set with every living species and cover type removed: rocks and debris only. The compiler rejects a set whose non-living part is empty for an airless kind (a bare moon still needs its boulders).
- Rules read the kind's own vocabulary: voxel material ids, mask channel names, altitude about the sea or datum, slope, `|sin latitude|`, the continent channel, and the set's decor channels.

```json
{
  "id": "temperate",
  "schema": 1,
  "channels": [
    { "name": "moisture", "lambdaM": 40000, "octaves": 3, "gain": 0.5 },
    { "name": "grove",    "lambdaM": 300,   "octaves": 2, "gain": 0.5 }
  ],
  "canopy": { "material": "canopy-temperate", "tint": "temperate-seasons" },
  "cover": [
    { "on": "turf",   "type": "grass-tuft",   "perM2": [4, 8], "normalUpMin": 0.7, "living": true },
    { "on": "turf",   "type": "meadow-flower", "perM2": 0.3,    "when": { "channel": "grove", "below": -0.2 }, "living": true },
    { "on": "gravel", "type": "pebbles",      "perM2": 3 }
  ],
  "species": [
    { "ref": "broadleaf-tree", "class": "T", "perHa": [120, 300], "living": true,
      "when": { "material": ["turf", "soil"], "altM": [2, 1400], "slopeBelow": 0.6,
                "channel": { "moisture": [-0.1, 1] }, "grove": [0.0, 1], "latBelow": 0.8 } },
    { "ref": "conifer", "class": "T", "perHa": [150, 400], "living": true,
      "when": { "material": ["turf", "soil"], "altM": [600, 2400], "slopeBelow": 0.8, "latBelow": 0.93 } },
    { "ref": "elder-tree", "class": "L", "perKm2": [8, 30], "living": true,
      "when": { "material": ["turf"], "altM": [10, 900], "channel": { "moisture": [0.3, 1] } } },
    { "ref": "granite-boulder", "class": "T", "perHa": [4, 25], "sizeM": [1, 6],
      "when": { "slopeAbove": 0.25, "mask": { "belt": [0.2, 1] } } },
    { "ref": "field-stone", "class": "S", "perHa": [200, 900], "sizeM": [0.2, 0.9] },
    { "ref": "fern", "class": "S", "perHa": 1500, "living": true, "surface": "roofed-floor" },
    { "ref": "stalactite", "class": "S", "perHa": 800, "surface": "ceiling" }
  ],
  "pois": [ { "site": "pad", "asset": "wrecks/light-freighter", "chance": 0.02, "loot": "wreck-common" } ]
}
```

| Kind | Cover | S | T | L | Canopy from orbit |
| --- | --- | --- | --- | --- | --- |
| **Temperate continental** (living) | Grass tufts and flowers on turf, pebbles on gravel, moss on roofed floors | Field stones, ferns, driftwood on shore material, stalactites | Broadleaf below ~1,400 m, conifers to the tree line, granite boulders in belts and on scree | Elder trees in wet lowlands | Green bands by latitude and altitude, bare above the tree line, seasonal |
| **Desert with canyons and mesas** (often dead; living with `life` chance) | Pebbles on gravel, sparse dry tufts where living | Loose sandstone slabs, dry brush, salt crystals on playa floors | Talus boulders under `mesas.riser` and `canyons` walls, dead trees on canyon floors where living | Lone rock needles are the kind's stamps, not decoration; a rare giant cactus-form where living | Faint olive along canyon floors only |
| **Barren cratered moon** (airless: no living species) | Regolith pebbles | Small ejecta rocks, density rising toward crater rims and falling with the `fresh` mask's age | Ejecta boulders of 1-8 m in ray patterns round young craters; blocks of 8 m and more are `boulders3d` stamps | None | None |

## 9. Structures, files, costs, tests, milestones

### C# sketch (zero allocation after warm-up, structs of arrays)

```csharp
public enum DecorClass : byte { Small, Standing, Landmark }

public readonly record struct DecorId(ulong Value);       // class 2 | face 3 | i 21 | j 21 | slot 2 | ceiling 1 | roofed 1 | bucket 8

/// GPU and CPU list record, 8 B: pos 3x11 | species 8 | size 6 | yaw 6 | axis 4+4 | flags 3.
[StructLayout(LayoutKind.Sequential)]
public struct DecorInstance { public uint Lo, Hi; }

/// One body's compiled set. Immutable, shared by every job; joins the kind program's content hash.
public sealed class DecorProgram
{
    // Species, indexed by species id (<= 256).
    public byte[] Class, Surface, MaxChunkLod, Collider, Flags;
    public float[] SizeMin, SizeMax, BaseRadius, CrownRadius, Align, BreakImpulse;
    public ushort[] HarvestFirst, HarvestCount;            // into the yield table (item id, kg range, tool, seconds)
    // Rules, one row per (species, term): lambda = density * product of ramps, all 0.16 fixed point.
    public ushort[] RuleSpecies; public byte[] RuleInput;  // altitude, inv, curvature, sinLat, channel k, material mask
    public int[] RuleFrom, RuleStep; public ushort[] RuleRamp;   // quantised input range; 33 ramp entries per rule
    public ulong[] RuleMaterials;
    public uint[] DensityFx;                               // per m2 of datum, 0.32 fixed point
    public ushort[] ClassFirstSpecies, ClassSpeciesCount;  // per (class, surface kind)
    public ulong ContentHash;                              // stored in the universe; a changed set is a new set
}

/// Job scratch (part of PlanetChunkScratch): site samples as SoA over the class's lattice window,
/// quantised as they are written. Nothing downstream reads a float.
public struct DecorSiteWindow
{
    public int Face, I0, J0, W, H;                         // lattice LOD 3 (T) or 6 (L); S reads the chunk's own lattice
    public int[] Height;                                   // 1/16 m
    public byte[] Inv, Curvature, Area, Material;
    public ushort[] SinLat;
    public short[] Channels;                               // [channel * W * H + point]
}

/// A chunk's resolved decoration: unmanaged, pooled with the chunk record, mirrored on the GPU.
public struct DecorChunkList
{
    public NativeList<DecorInstance> Packed;               // canonical order: class, cell, slot, layer
    public NativeList<uint> LocalId;                       // parallel; binary-searched to patch a removal
    public int FirstPage;
}

public static class DecorHash    { public static uint Cell(uint seed, DecorClass c, int face, int i, int j); public static uint Site(uint seed, ulong siteCell);
                                   public static uint Variant(uint lo, uint hi); }   // mirrored in decor_hash.glsl
public static class DecorScatter { public static void Resolve(in ChunkCoord coord, PlanetChunkScratch scratch, VoxelChunk chunk, DecorProgram program, DecorDeltaView deltas, ref DecorChunkList list);
                                   public static int Query(DecorClass c, int face, int i, int j, PlanetSurface surface, DecorProgram program, Span<DecorInstance> slots); }

public sealed class DecorDeltaStore  // per lattice tile; Terrain assembly, no Vulkan
{
    // Per block (256 lattice cells) and class: sorted uint entries (26-bit local id + state) or a bit per (cell, slot),
    // whichever is smaller; cleared masks for S and T; sparse roofed entries (<= 1,024).
    public bool IsRemoved(DecorId id); public void Remove(DecorId id, DecorState state); public void Compact();
    public uint ListCrc(in ChunkCoord coord, in DecorChunkList list);
}

// Land: factions-and-law.md's LandRights.Check(owner, body, direction, depth, toolClass) with toolClass Gather or Fell.
```

### Files

| File | Role |
| --- | --- |
| `src/Terrain/Decor/DecorHash.cs`, `DecorId.cs`, `DecorInstance.cs` | Hash, identity, packing (no Vulkan) |
| `src/Terrain/Decor/DecorSet.cs`, `DecorCompiler.cs`, `DecorProgram.cs` | JSON model, per-body compile (ranges, life flag, validation, content hash) |
| `src/Terrain/Decor/DecorScatter.cs`, `DecorCanopy.cs` | Chunk resolve, point query, canopy cover function |
| `src/Terrain/Decor/DecorDeltaStore.cs` | Deltas, cleared masks, compaction, tile files |
| `src/Terrain.Runtime/Decor/DecorWorld.cs` | List pool and mirror, near set, patches on events |
| `src/Terrain.Runtime/Rendering/DecorRenderer.cs`, `GroundCoverRenderer.cs`, `DecorMeshLibrary.cs`, `DecorImpostorBaker.cs` | Arena, passes, draws, mesh and impostor bake and cache |
| `src/Engine/Assets/Procedural/TreeGenerator.cs`, `RockArchetypes.cs` | Procedural meshes (rock archetypes shared with asteroids) |
| `src/Physics/DecorColliders.cs` | Shared shapes, pooled static bodies per bubble |
| `src/Sim/Decor/DecorInteraction.cs` | Server: harvest, breakage, crushing, scours, the unseat rule and its body caps, events, list CRCs, the land check |
| `shaders/decor_hash.glsl`, `decor_cull.comp`, `decor_prefix.comp`, `cover_gen.comp`, `decor.vert/.frag`, `decor_foliage.frag`, `decor_impostor.vert/.frag`, `cover.vert/.frag`, `decor_wind.glsl` | Passes |
| `data/planets/decor/*.json`, `species/*.json`, `cover/*.json`, `data/decor/rock-hulls.bin`, `data/render/decor-lod.json` | Data |
| **Changed:** `InstanceBuffer.cs` | Named ranges, loud overflow (section 4.3) |
| **Changed:** `ChunkJob.cs`, `PlanetChunkScratch.cs`, `PlanetGenerator.cs` | Decoration stage; site windows; canopy material at LOD ≥ 3 |
| **Changed:** `TerrainRenderer.cs` | Lists share the chunk slot and the staged copy; `StorageBufferBit` on the vertex arena |
| **Changed:** `PlanetImpostor.cs`, `shaders/body.frag`, `terrain.frag` | Canopy channel and colour |
| **Not changed:** `BrushMode.cs`, `EditStore.cs` | A `Scour` is a decoration event resolved by `DecorInteraction`, not an edit op |
| **Asked of planet-kinds:** site-only channels, a point query of per-lattice-point outputs at a given lattice LOD, the `boulders3d` stamp layer, `"decor"` and `"life"` in the kind file, the set's content hash stored with the kind's | Not edited here |
| **Asked of factions-and-law:** tool classes `Gather` and `Fell` in `LandRights.Check`; yield value counted in `IllegalVolume`; decoration value in `ValueMilli` of blast, fire and collision records | Not edited here |

### Per-frame and per-tick costs (summary)

| Where | Cost |
| --- | --- |
| Client main thread, per frame | ≤ 0.03 ms |
| Client GPU, per frame | ≤ 1.8 ms worst, ≤ 0.85 ms typical |
| Client and server workers, per surface chunk job | 15-95 µs by LOD typically, ≤ 170 µs at the class maxima (+30-60 µs in 3D chunks, ≤ 180 µs); +≤ 30 µs canopy at LOD ≥ 3 (client only) |
| Server, per tick | 0 while nobody interacts; ~20 µs when an anchor crosses an 8 m cell, plus ~1-2 µs per added body in a bubble that holds a grid; ~1 µs per harvest validation; a scour of ≤ 32 m ~0.1 ms on a worker; compaction ~1 ms per tile on a worker at save |
| Profiler rows (Decor panel) | Records in lists, pages used, instances per bucket, drops, cover clumps, GPU ms per pass, stage µs per LOD, bodies per bubble, delta bytes resident and on disk, store overflows per user |

### Tests

Quick (targeted pass):
- **Hash parity:** `DecorHash` in C# against `decor_hash.glsl` on a reference set, by test-time readback (never in the frame loop), `Variant` included: for 10,000 records the GPU's mesh pick equals the CPU's collider pick. Packed-record decode in GLSL against C# to one quantum of position, size and yaw.
- **Determinism:** the same body seed gives the same program hash and the same list bytes; Debug against Release; a committed CRC per set (the ring-table pattern).
- **Integer after quantisation:** perturbing every site float by ± 1 ulp before quantisation changes a list only where an input sat on a quantum boundary (the changed cells are counted and reported, as the measure of how much rests on the generator's floats), and the scatter and refine code paths contain no `float` or `double` after the site write (a source-level check on `DecorScatter`).
- **List check:** a client whose list is corrupted in one record gets the server's list after the CRC round trip, and its colliders and GPU pages match afterwards.
- **List against query:** for 10,000 random open-sky ids, `DecorScatter.Query` equals the LOD-0 chunk's list entry.
- **LOD consistency:** every T instance in a LOD 1-3 list and every L instance in a LOD 1-6 list exists in the LOD-0 list of the chunk that holds it, with the same species, size and yaw, and an anchor within the band-limit tolerance.
- **Ownership:** over a block of LOD-0 chunks, every live slot is owned exactly once; no two bases overlap.
- **3D:** on a kind with `overhang3d` and `caves3d`, floors, roofed floors and ceilings get the right species tables; nothing is anchored in air or inside rock; clearance holds.
- **Density targets:** instances per hectare per species over a 100 km² sample within ± 5 % of the rule's integral; `DecorCanopy` within 10 % of the summed crown area of the real instances.
- **Life flag:** an airless body's lists and cover table contain no living species.
- **Edits:** dig under a tree, bury a rock, scour a patch: gone from every list that replays the op; frozen into deltas at compaction; filling the hole back does not restore it.
- **Delta bounds:** a harvest fuzz never exceeds 7.2 KB per block; open-sky state is lossless (after any sequence and any compaction, the removed set equals exactly what the fuzz removed); compaction is idempotent; a removed id never reappears at any LOD, after a save and reload, or after its chunk is unloaded and rebuilt; a cleared sub-block equals the union of its entries.
- **No second harvest:** harvest, restart the server, return: the request is refused and no item is granted; the same after edit compaction and after filling a dug hole back in.
- **Scour storage:** 1,000 scours over untouched ground write no edit op and no chunk snapshot; a scour over cleared ground writes and sends nothing.
- **Land:** `Gather` and `Fell` on `Reserved` ground are refused and change nothing; on another party's claim they run and raise the counter.
- **Server and client colliders:** the same body set from the same lists, with the pool full and with it scaled by the bubble's quota byte.
- **No trap:** a hull placed, spawned or built across a T instance with no collider loaded leaves that instance crushed (delta written) when the collider batch runs, and a ship resting against a trunk (under 0.25 m penetration) does not crush it.
- **Body caps:** digging under a boulder field at the edit rate cap never puts more than 64 loose rocks and 64 trunks in a bubble, and every unseated instance still has exactly one delta.
- **Instance store:** a reserve past a user's range fails loudly; terrain, hulls and decoration can all reserve their maximum in one frame.
- **Zero allocation:** the stage, the near-set walk and a removal patch allocate nothing after warm-up.

`[Trait("Category", "Perf")]`:
- Stage µs per chunk LOD, plain and 3D-flagged, per set.
- `--decor-bench` (forest flyover legs at 30, 300 and 3,000 m/s, a ground walk, a forest base with 200 hulls): GPU ms per pass, main-thread ms, records, drops, MB, against section 4.4's budgets.

### Milestones (after planet-kinds K1; the first three kinds exist)

| | Scope | Exit check |
| --- | --- | --- |
| DK0 | Instance store ranges; `DecorHash`, set compile, `DecorScatter` for **T-class rocks and trees on open-sky ground, render only**; packed lists; cull, prefix, emit; mesh buckets; procedural rocks and one tree generator | T first because it proves the scatter, the lists, LOD consistency and the GPU path, and it is what makes a planet stop looking bare. Parity, determinism and LOD tests pass; GPU ms and stage µs recorded |
| DK1 | Impostors, canopy material, impostor canopy channel, season tint, wind, L class | A forest reads continuously from the ground to orbit on `--kind-orbit`; no pop over the ladder in motion (screenshot sheets, not stills) |
| DK2 (needs M3) | Colliders, near set, ship contact and breakage | Walk and land in forest; body counts and tick cost logged |
| DK3 (needs M4) | Deltas, events, harvest, `Scour`, the unseat rule and its caps, tile files, the list check, the land check (`LandRights`, or a stub that allows everything until the law doc is built) | Two players fell and mine; bytes per event and tile sizes logged; delta bound, no-second-harvest and no-trap tests pass |
| DK4 | S class, ground cover | `--decor-bench` inside every budget of section 4.4 |
| DK5 (needs K3, K4) | Roofed floors and ceilings, `boulders3d` dressing | 3D tests pass on a shattered-world arch and in a cave |
| DK6 | Points of interest on sites; shadows when the scene shadow system exists | |

### Rejected alternatives

- **Accepting props on the GPU from the hash alone** (the ring enumerator copied over): needs the generator on the GPU or its per-cell outputs uploaded per chunk, which is the same CPU work as resolving the list, plus every rule twice.
- **3D cartesian scatter cells:** density then depends on how the surface tilts through the grid, and a cell has no natural "one tree per ground area" meaning.
- **Voxel trees:** a 1 m voxel cannot make a trunk or a crown.
- **Storing generated instances, or per-instance CPU draw submission:** ~1e11 instances per planet; the first is impossible and the second breaks the frame budget at a few thousand.
- **Separate decoration for the far field (a second, coarser forest):** it would not be the same trees on arrival. The cell hierarchy gives one population at every range.

### Open questions for the user

1. **Regrowth.** Default here is permanent, matching planet edits and asteroids. Should vegetation (not rocks) regrow slowly in played time, say over tens of played days, so worked land near bases recovers and delta storage shrinks? Rocks would stay gone either way.
2. **Parks and settlements against ships.** Tools are refused on `Reserved` ground and unlicensed felling elsewhere runs and is fined, both as factions-and-law.md already rules for digging. What is left is a ship: a plume or a hull can still flatten a settlement's trees, permanently, for the price of the Reckless damage cases. Is that acceptable (it is physics, and D32 says permanent), or should the authority replant `Reserved` ground (its deltas cleared when the case is paid, or on the strategic step), which would be the one place decoration comes back?
3. **Tree reach.** Trees as instances to 1.5 km with canopy colour beyond (the default), or to 3 km (about +0.16 ms per LOD-4 chunk job and up to twice the impostors)? It is a client quality knob either way; the question is the default.
4. **Flora art direction.** Are generated plants (per-planet species from parameter ranges) acceptable as the main source, with authored glTF only for hero pieces and points of interest, or should a hand-made plant library be the default and generation the fallback?
5. **Underwater decoration** (kelp, coral, seabed rocks): wanted, or is the seabed bare? The scatter supports it (`submerged`), but nothing draws under the sea surface today.
6. **Wildlife.** D16 mentions it; this design covers only static decoration. Is fauna wanted as its own design?

Revision and review history: [history/terrain-decoration.md](history/terrain-decoration.md).
