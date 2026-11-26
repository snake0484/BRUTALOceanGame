# History of terrain-decoration.md

## Review notes

Lead review, 2026-11-26. Read-only against the repo (`InstanceBuffer.cs`, `TerrainRenderer.cs`, `terrain_cull.comp`, `CubeLattice.cs`, `PlanetGenerator.cs`, `PackedTerrainVertex.cs`, `SurfaceNetsMesher.cs`, `Renderer.cs`) and docs/pivot (planets.md, README.md, rings-and-asteroids.md, ai-ships-and-crew.md, decisions.md, factions-and-law.md), plus the planet-kinds proposal. Nothing was built or run: every number is still an estimate. Fixes were made in place above; this section lists what changed and what is still open.

**Confirmed as written**
- Decoration is procedural and deterministic: integer cell hash on the generator's lattice, nothing stored for untouched instances, the same instance at every chunk LOD.
- S, T and L are never accepted on the GPU. The CPU resolves lists and the GPU only decodes, culls and draws them, so there is no rule evaluated in both C# and GLSL.
- Code facts check out: `GpuCapacity` 8,192 equals `MaxHandles`, `Cull` reserves the whole high-water mark, two frames in flight, `LatticeN0` is a multiple of 2^15, density is an `sbyte` at 1/32 voxel, the 12 B vertex carries position, material and an octahedral normal.
- Bit budgets: `DecorId` 59 + 5 spare, `DecorInstance` 64 of 64, 21 bits per axis for the S lattice up to a 4,000 km body.

**Found and fixed**

| # | Problem | Fix |
| --- | --- | --- |
| 1 | Acceptance was "integer after the site sample", but `λ` was a float product of float `smoothstep`s, quantised only at the end. Quantising a result does not remove a float disagreement, it only makes it rarer | Site inputs are quantised once; ramps are integer tables; products are 64-bit fixed point (1.2) |
| 2 | The anchor refine, normal and slope test were float, and the 2 m bucket in the id came from a float altitude | Integer trilinear refine in 1/64-voxel coordinates; integer normal and slope test (1.4) |
| 3 | Even so, everything stands on the generator's floats. For terrain a mismatch is cosmetic; for decoration it is a tree one side collides with | The server is the authority: CRC per LOD-0 list near each player, and the server's list replaces a client's that differs (7) |
| 4 | Variant and rock archetype had no bits in the record, and the rock archetype picks the collider | One `uint` hash of the record, same code in C# and GLSL, parity-tested (3). This is the only real CPU/GPU identity surface |
| 5 | Open-sky T instances took a clearance test on the chunk's own voxels, and cave mouths exist only at LOD ≤ 2: a tree shown from 1 km could be missing on arrival | Clearance only for roofed species; open-sky is the first crossing from above at every LOD; no T or L in the `mouths` mask (1.2, 1.4) |
| 6 | Roofed floors and ceilings shared the surface slot's liveness, so caves under bare ground had nothing | Liveness per (slot, surface kind) with its own salt and rule inputs (1.2) |
| 7 | L at 1 per 1,000 m² is 4,194 per LOD-6 chunk (cap 1,024) and 452k in reach; a LOD-3 chunk can hold ~1,900 thinned T (cap 1,024) | L capped at 1 per 10,000 m²; LOD-3 list cap 2,048; the compiler enforces class maxima (1.1, 4.1) |
| 8 | The LOD-0 stage cost left out S's site window, the LOD 1-2 costs were typical-only, and the canopy material at LOD ≥ 3 needs per-point rules (+0.1-0.2 ms a leaf, uncounted) | S reads the chunk's own lattice; costs given as typical and maximum; canopy on the coarse grid, ≤ 30 µs (4.5) |
| 9 | Impostor fill: ~21M px of quads at the class maxima against a 0.2 ms budget | Budget 0.3 ms, near buckets first; GPU total 1.8 ms. Arithmetic table added (4.4) |
| 10 | Impostor memory (24 MB) holds 34 layers at 0.7 MB, but a set could ask for far more (species × 3 variants, plus 16 rock archetypes) | Layer cap: 16 rock at 256² + 30 plant at 512², enforced by the compiler (3) |
| 11 | Instance store: the whole 9.2 MB was charged to decoration, and rings' 64k buffer was not mentioned | Decoration's share is 8.4 MB (98.4 MB in total); rings register a fourth range if they share the store (4.3) |
| 12 | Emit could write past the range on overflow; cover would double along chunk seams (the border cell layer and the skirt vertices are in the vertex buffer) | Clamped counts and a slot check; cover skips non-core and skirt vertices (4.2) |
| 13 | `Scour` as an edit op: 64 of them compact an untouched chunk into a 3-8 KB snapshot, one a second per hovering ship | A scour is a server event resolved to id deltas or sub-block masks; cover scorch is client-side and not stored (5) |
| 14 | Delta caps were lossy: 2,048 entries per block (13 % of its slots) and 2 MB per tile, enforced by clearing depleted sub-blocks tile-wide. Harvesting legally elsewhere in a tile could delete a settlement's trees | Bit-per-slot dense form: lossless, ≤ 3.1 KB of surface state per block, no tile cap. Only cave dressing has a lossy cap, inside its own block (7) |
| 15 | Leaving and returning: the rule was right but not stated as an invariant, and a changed decoration set would re-roll every id | Lists are built only after deltas load; state moves forward only; the set hash is frozen with the kind; restart and reload tests (1.3, 5, 9) |
| 16 | Land: the doc said factions-and-law.md did not exist and defaulted to "never refused", so a park could be cut down for a fine | Uses `LandRights.Check`; tools are refused on `Reserved` ground and illegal elsewhere, as for digging (5) |
| 17 | Body pool: 4,096 per bubble against ~800 T per player in a 64 m ring at the maxima, with no rule when full | 32 m and 16 m rings, fixed per-anchor quotas, pool 8,192, one shared scale byte so clients match (6) |
| 18 | Colliders could appear inside a hull that was built, spawned or set down where no collider was loaded; undermined 0.4-1 m rocks all became dynamic bodies (~100 a second at the edit cap) | Crush on overlap at add time; bodies arrive with their chunk; S never becomes a body; 4 bodies per op, 64 + 64 per bubble, idle break-up (5, 6) |
| 19 | Lattice cell sizes were quoted as 3-4, 6-8 and 48-64 m | 2.4-4, 5-8 and 38-64 m from `CubeLattice` (arc rate 0.5 to 0.83); blocks ≈ 150-256 m, tiles ≈ 20-33 km |
| 20 | "terrain (~0.1-0.3 ms measured)" | README has 0.07-0.12 ms on the Debug smoke bench, one view; the forest view is unmeasured |

**Still open or unverified**
- Every ms, µs and MB figure is an analytic estimate. The ones most likely to be wrong: impostor fill (0.3 ms), alpha-tested foliage overdraw inside the 0.5 ms mesh line, and the site window cost per point (220-300 ns), which depends on planet-kinds' real layer costs. `--decor-bench` has to exist before DK4's exit check means anything.
- The lattice arc-rate range (0.5 to 0.83) was worked by hand from `CubeLattice`, not printed from `MaxArcPerT`. Cell, block and tile sizes follow from it.
- The integer path removes float from decoration's own code, not from the generator under it. The list check repairs a disagreement near players; a coarse list that differs is only a visual difference.
- A ship can still strip `Reserved` ground with its plume or hull for the price of the fines (open question 2).
- Seams between chunk LODs can still draw a far tree twice or miss it once. Left as is (visual only); DK1's in-motion check should look for it.
- Regrowth is still the user's call (open question 1). The dense delta form stores no removal time, so regrowth would need a per-sub-block clock.
- Asked of other docs, not edited: planet-kinds (site-only channels, lattice-LOD point query, `boulders3d`, the `mouths` mask as a site input, the set hash stored with the kind) and factions-and-law (`Gather` and `Fell` tool classes, decoration value in records).
