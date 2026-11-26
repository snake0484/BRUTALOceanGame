# History of planet-kinds.md

## Revision 2 notes

What changed against the first draft, and why.

- **3D layers are first-class** (user decision). The two optional types (`overhang3d` at K3, `caves3d` at K4 "if wanted") are replaced by a family of five in §1.6: `undercut`, `tubes3d`, `caves3d`, `displace3d`, `stamps3d`. Every kind uses some (table in §2). The old open question "are caves wanted" is gone.
- **One density.** §1.6 defines how the 2.5D stack and the 3D layers compose: the height density first, then displace, add and carve ops on a signed distance, each scaled by a gate that is exactly 0 when off. Features pinch out at gate edges instead of being cut.
- **Cheap forms first.** Two of the five types are column-profile layers: their shape is an analytic function of lattice channels and altitude, with no 3D noise. Overhanging cliffs are a riser profile; lava tubes are a canyon with a roof. Only `caves3d` and `displace3d` sample 3D noise, on an even-voxel grid with a per-cell interval test, never per voxel.
- **Three named heights** (`Hbase`, `Htop`, `Crossings`) replace "the height", with a table of who reads which. The sea mask, the D30 band, deep heat, the dig floor and the impostor read the base height, which stays single-valued and analytic.
- **Shell classification** gains conservative altitude bands (`airLo`, `rockHi`) plus band lines with per-layer box tests, so a cave band does not turn every chunk under it into a job. Cavity and sky chunks are new job bands; a cavity chunk skips the fine lattice.
- **A first-draft error is fixed:** it passed the node's own LOD to `Bounds`, but the selector is top-down and never visits the children of a uniform node, so a class must hold for every LOD from `wantLod` to the node's. This matters for 2.5D too (small craters, fine risers), not only for caves.
- **Band limit for 3D:** the weight `W3`; voids close from the inside, grounded solids fall back to their top profile, floating ones shrink away. The stated invariant is that each LOD's first crossing from above is LOD 0's top envelope filtered to that voxel. A 3D feature is capped at 512 m, so the impostor never needs 3D.
- **D30:** 3D layers are off in every protected column plus a 10 m guard, so those columns are byte-identical to the 2.5D density. The price is no sea caves or stacks (open question 3). (The mechanism was rewritten in review: see the shore rule in §1.6.)
- **Deep heat:** a compile rule keeps natural caves above the heat onset depth unless a layer opts in, since depth is measured from the base height and a level cave under a peak would otherwise be lethal.
- **Collision and edits:** no new path. Collision meshes already carry 3D shapes; casts above 200 m use `Htop + TopSlack`; edits replay on a density that already has the voids.
- **Lighting stop-gap:** the `enclosed` material bit, because there are no scene shadows yet.
- **Decoration hooks** (§6): the kind names its set and life chance, declares biome channels, and exposes `SiteSample`, exported masks, `Density`, `Crossings`, the inside-cave flag in three forms, the canopy slot and the `blob` stamp preset that terrain-decoration.md calls `boulders3d`. Everything that proposal lists under "asked of planet-kinds" is covered.
- **Budgets:** 3D is budgeted per chunk band (≤ +0.35 ms on a surface leaf, ≤ 0.6 ms for a whole cavity leaf) and per flight path (≤ +15 % mean cost, ≤ +20 % leaves against the same kind with 3D muted). Scratch +0.2 MB per job, bounds pyramid 2.1 → 10.5 MB per body with an octree (5.2 before review counted one band), a cavity leaf ≤ 0.6 ms after review, server collision memory +10–20 % in cave regions. All estimates until K0's tests print numbers.
- **Milestones:** the 3D phase, the cull and the new classification are built in K0 and proved on a test kind; K1's three kinds ship with surface-attached 3D (no cavity chunks); caves come in K2; floating rock in K3. K4 is gone.
- **Not verified:** nothing was built or run for either revision. The share of leaves that are cavity chunks, the false-positive rate of the box tests and the volume-grid costs are the least certain numbers here.

## Review notes

Lead review, 2026-11-26. Read-only against the repo (`PlanetSurface.cs`, `PlanetGenerator.cs`, `PlanetHeightBounds.cs`, `PlanetParams.cs`, `PlanetImpostor.cs`, `CubeLattice.cs`, `Noise.cs`, `PlanetLeafSelector.cs`) and docs/pivot (planets.md, decisions.md D29, D30, D60, D75–D77, exotic-matter.md, rings-and-asteroids.md), plus terrain-decoration.md. Nothing was built or run: every number is still an estimate. Fixes were made in place above.

**Confirmed as written**
- The diagnosis in §0 matches the code: 5 + 3 + 15 octaves, `ContinentScale` 1.25 R, ridges at 250, 125 and 62.5 km under a mask that needs c ≥ 0.4, detail 450 m × 0.574^i from 32,768 m. Amplitude over wavelength is 0.014 at every scale. There is no 3D term in `PlanetGenerator`; density is `(w − h) × inv` with `inv ≥ 0.25`.
- Each body has its own generator: a kind is data compiled into one program per body, bodies never share a lattice, and nothing in the evaluator is global.
- The steep kinds do fix the slope. Ridged at A0/λ0 ≈ 0.18 gives 0.42 of slope from the first octave at a crest and 0.87 (41°) with eight octaves at gain 0.45. Canyon walls are depth over 0.7 half-width, 45°. Simple crater walls reach 44° at the rim.
- Determinism holds for every layer type: integer hashes, `floor(double)` cells, no transcendental functions, stamps by chord distance in double. The 3D ops add only `sqrt`, min and max.
- `Bounds(…, minLod)` with `minLod = wantLod` is right: `PlanetLeafSelector.Visit` returns on a uniform node before it visits children.
- Impostor: no 3D feature reaches LOD 8 (512 m cap), so its height stays `Hbase`. Exact altitude per voxel is unchanged. The D30 mask stays a test on `Hbase`.
- Decoration hooks: everything terrain-decoration.md lists under "asked of planet-kinds" exists (site-only channels, the lattice-LOD point query, `boulders3d` as the `blob` preset, the `mouth` mask, `decor` and `life`, the set hash with the kind's).
- 3D layers are a standard family with bands, per-LOD weights, shell bounds and stated costs. Several bounds and costs were wrong (below); the structure is sound.

**Found and fixed**

| # | Problem | Fix |
| --- | --- | --- |
| 1 | Still too flat where it matters: temperate's median 10 km relief was 400 m against today's 350 m, and its median slope 5° against 3°. All of the gain was inside the belts. Ice was flatter than today (2°) and is the commonest cold kind | Temperate gains uplands on 30–40 % of land, taller and shorter hills, valleys and lowland scarps: targets 700 m and 9°. Ice gains chaos terrain: 4°. Alpine's 22° median needs belts on ≥ 45 % of land; range narrowed (§1.5, §2) |
| 2 | Warp bound not conservative: `min(2·A_w, L·f_w·ρ·A_w)` ignores that the warp is a 3-vector (√3) and has several octaves | `√3 × min(2·Σ A_w, ρ·L·Σ f_w·A_w)` (§3) |
| 3 | Warps could fold: the schema example allowed A_w/λ_w = 0.75, and "slope × (1 + 2π·A_w/λ_w)" is a sinusoid guess, not a bound | No-fold compile rule `Σ A_w/λ_w ≤ 0.25`; examples changed. The rule also bounds the sideways shift when a LOD skips a warp octave (§1.3, §3, schema) |
| 4 | `undercut` treated as a vertical skin of ≤ 30 m. An overhang puts rock up to a whole riser (250 m) above the `Hbase` of the column under it, so a node beside a cliff would be classed Air with a ledge inside. The same hole was in the chunk's early outs and in `Htop` | Horizontal reach: `Bounds` grows the cap by `reachH`; the chunk cull takes riser tops and bottoms in reach; `Htop` includes the host's upper level (§1.6, §3) |
| 5 | `displace3d` skin was A. Displacement is along the normal and density is `v × inv`, so the surface moves up to 4 A in altitude. The divisor that keeps the gradient near 1 also stretches the unsaturated band, so `Pad(lod)` is too small | `skin` carries 4 A; `pad × PadScale3` in `Classify` and in the chunk's early outs; `TopSlack` = 4 A + mouth radius (§1.6, §3) |
| 6 | Exact gate broken by the smooth blend: with `K = radius + 4.25 voxels`, a gated-off stamp gives `smin(4.25, 4.25) = 4.25 − k/4`, 3.75 voxels at k = 2 m, which is not the saturated byte | `K` includes `+ k`; a test case in §7 |
| 7 | Noise SDFs "divided by their gradient bound" to be 1-Lipschitz. With L = 14.73 and a warp factor, that bound is 10–100 times the real gradient, so a cave wall spans several voxels per density quantum and cannot be meshed | Divide by a compiled design gradient; nothing relies on 1-Lipschitz; conservative tests run in field units with the certified bound (§1.6) |
| 8 | Box tests for caves and stamps tested crossing, not saturation: a node 1 m from a cave wall would be classed Solid with unsaturated voxels | Every test carries `4.25 voxels + k`; the tube test is stated in channel units with the gradient bound (§3) |
| 9 | Presence "cells, one hash" would slice tunnels at cell faces | Value-noise presence on cell corners: exactly 0 with eight off corners, continuous otherwise (§1.6) |
| 10 | D30 gate on the column's own `Hbase` as a lattice channel. It is not exactly 0 for a protected voxel beside a sea cliff, and it allowed a void below the waterline a metre from the sea in a high cliff column | Shore rule on the coastal zone of the continent channel: (a) a per-voxel `h` ramp for all 3D layers, (b) no carve below `ProtectHeight + 30 m` in the zone. Cavity chunks no longer need the fine lattice near coasts. The flood-fill test was ill-defined and is restated (§1.6, §3, §7) |
| 11 | Coarse-grid interpolation of shaped octaves chamfers every crease by kink slope × 4 voxels, differently per LOD (1–2 voxels on steep ranges) | Interpolate raw octaves, shape per point (§1.2). `legacy-rolling` keeps the old order behind a compat flag for the golden test |
| 12 | "Coarser LODs get cheaper" is false for the kinds: the per-point window (2–256 voxels) holds the mountain stack at LOD 4–7, 230–380 ns per point against 95 today | Per-LOD cost table and per-LOD budget; row SIMD moved from K2 to K1; the 3,000 m/s case re-costed at 1.5–2.6 cores (§4, §7) |
| 13 | Cavity chunk cost assumed 35 % of cells kept (the Lipschitz test cannot deliver that at λ ≤ 100 m) and left the cave warp out (0.58 ms if sampled per field point) | Budget for the whole grid; warp on the 8-voxel grid; 0.4–0.6 ms per cavity leaf (§1.6, §4) |
| 14 | Volume grid step: "≥ 8 samples per wavelength" is an 8 % amplitude error, too much for a threshold field. Displacement at λ3 = 12 m had 6 samples | Error rule (≤ 0.3 voxel at LOD 0); λ3 ≥ 16 m; octaves of 8–16 voxels go per voxel in surface-band cells. The LOD step that drops an octave can move a crag by 1.6 voxels: stated, not hidden (§1.6, §4) |
| 15 | Relief clamp: only sea bodies had one, and alpine's worst-case sum is 13 km against +9 km. A hard clamp cuts flat tops; a hard-sum validation would reject the kind | Relief knee on every kind: a monotone eased curve over the top and bottom 20 % (§1.3) |
| 16 | Ridged octaves are not zero-mean, so skipping them biases the range low by 0.2–0.25 voxel at every LOD | The compiler adds the skipped mean per LOD (`RidgeMean`) (§3) |
| 17 | `canyon` width given in metres but computed as `\|n\| / w` in noise units: width and wall slope swing by 3 times with the local gradient | Gradient-normalised distance with a floor; bound restated (§1.3, §3) |
| 18 | `displace3d` on risers does nothing without `undercut`: above 76° the height density overstates distance by slope / 4 | Compile rule; `undercut` ordered first (§1.6) |
| 19 | Pyramid memory 5.2 MB counted one LOD band | 10.5 MB, with a 7 MB packing if needed (§3, §4) |
| 20 | `SiteSample` costed as one `Height`; to match chunk bytes it needs the four coarse corners | ≤ 3 × for one point, 1.2 × in a window (§4, §6) |
| 21 | `Density` "within 0.1 voxel of chunk bytes" cannot hold on steep kinds | 0.35 voxel at LOD 0, 1 voxel beyond (§6, §7) |
| 22 | Exotic matter: the lens void test sampled `Density` (it can miss a 6 m tunnel); steep 2.5D relief can cut a lens open in a cliff with no void at all; remnant chambers "at any depth" hand out deep, pure rock | Conservative box test; lens depth from the lowest `Hbase` over its footprint; remnant chambers in the top 300 m; open question 5 extended (§2, §5) |
| 23 | Asteroids "stay on their own path" contradicts rings-and-asteroids.md, which reuses the planet chunk path | Reworded: same chunk path, own density source; 3D combine ops and stamp primitives kept shareable (§2) |
| 24 | Crater bound slack quoted as 0.75 chunk | 1.6–3 voxels (§3) |
| 25 | "A riser is never under 2 voxels wide" | True only at the RMS input slope; reworded (§3) |

**Still open or unverified**
- Nothing here is measured. The least certain numbers, in order: per-point cost at LOD 4–7 and whether row SIMD reaches 2.5 times; cavity leaves as a share of all leaves (10–20 %); whether the 8³-cell test drops anything before a tighter `GradientLipschitz` is certified.
- The certified Lipschitz constant matters more than the doc first let on: warped bounds, the tube and cave box tests and the cell test are all weak at 14.73. It belongs in K0.
- Slope targets in §1.5 are knobs backed by rough mixture arithmetic, not by samples. The K1 slope test is the real check, and the kind sheet should be judged in motion from the ground, not only from orbit.
- The relief knee compresses the top 20 % of the range. On alpine that is everything above 7.2 km; if peaks look squashed, lower A0 instead.
- The coastal zone is set by a loose gradient bound, so it is typically 1–4 km wide, and inside it caves stay 30 m above the protected band. If that reads as "no caves near any coast", `CoastGuard` can shrink once the tighter constant exists.
- The displacement pop of up to 1.6 voxels at the LOD that drops an octave needs an in-motion look in K1.
- planets.md still describes a per-chunk gnomonic grid, a float altitude and an "existing overhang term". The code has the cube lattice, an exact double altitude and no overhang term. planets.md should be brought in line when this design lands; it was not edited here.
- Not re-examined: the material rules, landing sites, kind assignment weights, and the JSON schema beyond the two numeric fixes.
