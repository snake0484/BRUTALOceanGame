# Planet kinds: slice 1 brief

Source: `docs/pivot/planet-kinds.md` (section numbers refer to it). Numbers are targets or estimates unless measured. Slice = K0 + 2.5D part of K1. Design's K1 kinds: `temperate-continental`, `barren-cratered`, `desert-canyon`.

## 1. Goal and exit check

Replace the fixed 23-octave recipe in `PlanetSurface` with a generic layer evaluator driven by JSON kinds. Today: 100 m slope median 3 to 4 deg, p99 12 to 15, none over 30.

Targets (§1.5, land only):

| Kind | 10 km relief p50/p95 | 100 m slope p50/p90/p99 | Over 30 / 45 deg | 1 m rough |
| --- | --- | --- | --- | --- |
| temperate | 700/3,000 m | 9/28/42 | 8% / 0.8% | 8% |
| barren-cratered | 800/4,000 m | 7/25/40 | 7% / 0.3% | 10% |
| desert-canyon | 500/1,800 m | 3/25/70 | 8% / 4% | 5% flats, 15% walls |

Without `undercut`, risers over 76 deg mesh as a one-voxel staircase; assert slopes on the height function.

Budgets (§4, estimates; a kind over budget does not ship):
- Lattice at LOD 0, 2, 4, 6, 8, 12: <= 150 ns/point, <= 0.30 ms/surface leaf with row SIMD. LOD 0 scalar <= 200 ns / 0.40 ms; scalar to 400 ns at LOD 3 to 7 as interim only. Mid LODs are the dear ones.
- Render-only leaf <= 0.9 ms scalar, <= 0.7 ms SIMD. `Height` <= 0.6 us, `Bounds` <= 1 us, `Classify` <= 0.3 us. Impostor 256^2 <= 0.3 us/sample. Zero allocation.

## 2. Data

`data/planets/kinds/<id>.json`: `legacy-rolling` (`"compat":"v1"`), `temperate-continental`, `barren-cratered`, `desert-canyon`.
- Top: `id`, `schema:1`, `requires`, `relief{minM,maxM}`, `sea{present}`, `layers[]`.
- Layer: `name`, `type`, `out` (channel, `"h"` = height), `in`, `op` (add), `gate`, `scale`, `precise`, `warp`, `lambdaM`|`lambdaR`, `octaves`, `amplitude`, `gain`, `knots`, type params.
- Any number may be `[min,max]`: `lerp(min,max,U(hash(bodySeed,FNV1a64(path))))`. Sea kinds name a `continent` channel. <= 24 channels.
- Full example: §7 "Schema" (ignore `layers3d`, `biome`, `materials`, `deposits`, `sites`, `pads`).

## 3. Evaluator

Pipeline (§1.2): noise -> stamps -> compose per lattice point. Octaves >= 256 voxels on every 8th point, bilinear. Ridged/billow/eroded store RAW clamped `n` on the coarse grid; shape and damping applied per point (about 2 ns/oct). `precise` channels coarse only from >= 1,024 voxels. Compiler orders warps first, eroded coarse to fine.

| Type | Formula |
| --- | --- |
| fbm | sum a_i n(p/lambda_i) |
| ridged | `(1-abs(n))^2`, weighted by previous octave |
| billow | `2 abs(n) - 1` |
| eroded | octave x `1/(1+kappa abs(G)^2)`, G running gradient |
| warp | 3-channel offset on sample position; no-fold: `sum A_w/lambda_w <= 0.25` (compiler rejects) |
| curve | monotone eased remap, <= 8 knots; scarp = step on a noise level set |
| terrace | curve on h with seeded levels (zero-mean), hard risers, benches |
| canyon | `h -= D m P(abs(n)/max(abs(grad n), g_min))`, g_min = 1/3 RMS gradient |
| craters | class stamps (below) |
| ergs | dune fields, 10/33 deg asymmetric |
| mask | `lat` (dot axis), `alt`, `channel`, mul/min/max/invert |
| seaClamp | inland floor, last |
| relief knee | compiler interval-evaluates worst-case `Hsum`, appends eased curve `[0.8 max, Hsum] -> [0.8 max, max]` (and bottom); reject `Hsum` > 2x range |

Craters (§1.4): one class per radius octave, own cube grid in one of 16 rotations, cell arc >= 6 r, one jittered candidate per cell. Simple: `(rim+depth)x^2 - depth`, depth 0.4 r, rim = 0.27 depth, `rim/x^3` tail cut at 3 r (1.5 r small). Complex above `complexDiameterM`: flat floor, depth ~ diameter^(1/4), 2 to 3 terraces, central peak. Age: depth x (1-0.7a), rim x (1-0.9a). Large to small: `acc = acc(1-e)+profile`; export `fresh = prod(1-e)`. Double maths, no trig.

Band limit per LOD (voxel s): fbm/eroded/warp skip octave at lambda < 2 s; ridged/billow < 4 s plus `RidgeMean` add-back (0.35 x skipped amplitudes x belt mask); curve/terrace/canyon riser = `max(band, s g(L))`, g(L) per-LOD RMS slope table, terrace/canyon fade to mean when step or half-width < 1 or 2 s; craters full >= 8 s, softened 4 to 8, bowl 2 to 4, skipped < 2; ergs skipped < 4 s, replaced by mean.

Bounds (§3): every op has `EvalInterval`; `Bounds(dir, angle, minLod,...)` holds for every LOD >= `minLod`; `Classify(node, wantLod)` uses `wantLod`, not node LOD.

| Layer | Interval over cap rho |
| --- | --- |
| fbm octave | `a clamp(n(c) +- L f rho', -1, 1)`; L = 14.73 (loose, est. 3 to 4x) |
| warped layer | centre `n(c+W(c))`; `rho' = rho + sqrt(3) min(2 sum A_w,i, rho L sum f_w,i A_w,i)`, all warp octaves; rigorous factor `1 + sqrt(3) L sum A_w/lambda_w` |
| ridged/billow | `abs(n)` interval then monotone map; weights in [0,1] |
| eroded | `a [min(nLo,0), max(nHi,0)]` |
| curve/terrace/mask | monotone per segment, exact |
| combines | sum, 4-corner product, lerp hull, smooth min `min - [0,k/4]` |
| canyon | `[-D mHi, 0]` if abs(n) interval can come within `w G` of 0 (G incl. warp factor), else 0 |
| craters | per class active at >= `minLod`: cap <= 16 cells: hull of present craters; else `[-depth_max,+rim_max]`; classes sum. Slack: unresolved classes (< ~4 voxels) leave 1.6 voxels (minLod = node LOD) to about 3 voxels (minLod 0 under LOD-5 node); small against `Pad` (17 voxels) |
| ergs | `[0, A_max]` x mask bound if an erg is in reach |
| seaClamp | max of intervals |

0 lies inside a skipped octave's interval because caps are >= 12 lattice cells (`LatticePadCells`), so spread >= 30 and interval is full `[-a,a]` (`[0,a]` ridged).

## 4. Rules that must not break

- Determinism: add, sub, mul, div, floor, sqrt, min, max only; no Sin/Cos/Atan/Exp/Log/Pow. Integer hashes (`Noise.Mix`, `Fold`), cells from `floor(double)` as `long`. SIMD == scalar bit for bit (no FMA). `ContentHash` joins `GeneratorVersion` in save/handshake.
- Seams bit-exact: lattice value depends only on (face,i,j,LOD)+program; `NeighboursAgreeOnAprons` passes. Gates exact (mask clamps to 0, gated term adds exactly 0; culled == evaluated). Stamps compact support, exact 0 outside.
- Shell classification conservative for every LOD in [`wantLod`, node LOD].
- Exact altitude: per-voxel double `sqrt` at every LOD (as now).
- D30: protected <=> `Hbase <= sea + ShoreProtectHeight`; sea <=> `Hbase < sea`; `seaClamp` last. Voxel mask uses lattice h, `IsSea` direct `Height` (differ up to about 0.2 m on steep kinds).
- Impostor samples the same function: `Height(dir, lod)` at LOD 12 (256^2) / 10 (1,024^2), visible height `max(Hbase, sea)`, R16 over `[MinHeight, MaxHeight]`.
- `GeneratorVersion` 1 -> 2; v1 retired, not a code path. Changed kind = new id.

## 5. Files and structs

New in `src/Terrain/Planet/Kinds/`: `PlanetKind.cs` (JSON model/loader), `PlanetKindCompiler.cs` (ranges, flatten, sort, per-LOD tables, validate no-fold/relief/sea-needs-continent, hash), `PlanetKindProgram.cs`, `LayerOps.cs` + `.Interval.cs` + `.Simd.cs`, `Stamps.cs`; `tests/Terrain.Tests/PlanetKindTests.cs`.
Change: `PlanetSurface.cs` (evaluator; keep `Height`, `Bounds`, `IsSea`; add `minLod`, rows), `PlanetGenerator.cs` (`EvaluateWindows` -> row evaluator), `PlanetHeightBounds.cs` (two bands, `Classify(node,wantLod)`), `PlanetParams.cs` (+`KindId`,`KindSeed`,`ProgramHash`; drop `LandBias`,`MountainHeight`), `PlanetChunkScratch.cs` (channels `[channel][point]`, `StampList`), `PlanetImpostor.cs`, `src/Terrain.Runtime/PlanetLeafSelector.cs` (`wantLod`), `src/Terrain/Voxels/Noise.cs` (`Gradient3` + derivative, row variants), `src/Sim/SimWorld.cs`.

Structs (fields in §7 Structs):
- `LayerOp`: Fbm, Ridged, Billow, Eroded, Warp, Craters, Ergs, Curve, Terrace, Canyon, Mask, Combine, SeaClamp.
- `PlanetKindProgram`: immutable SoA of octaves, layers, per-LOD tables (`OctaveEnd`, `RiserBand`, `ClassWeight`, `LayersAtLod`, `RidgeMean`), `Classes`, `ContinentChannel`, `ContentHash`.
- `StampClass` (48 B): crater/erg class params, rotation, cells per side, seed.
- `StampList`: per-chunk culled stamps, <= 64.

## 6. Tests and viewer

- Legacy golden: `legacy-rolling` equals v1 heights and chunk hashes bit for bit.
- Determinism: program hash, heights, bytes; scalar == SIMD; golden hash per kind.
- `NeighboursAgreeOnAprons`, `CornerChunksSpanSeveralFaces` per kind, on crater rims, gate edges, cube edges.
- `BoundsHoldEverySample` per kind and `minLod`; per-layer-type interval test (10^4 samples); warp at the no-fold limit with 3 octaves.
- `ShellClassificationMatchesBruteForce` per kind and `wantLod`.
- Band limit: per LOD RMS <= 0.35 voxel, max <= 1.5 voxel vs box-filtered LOD 0; alias metric LOD >= 1.
- D30: no dry ground below sea level past the ramp (10^5 samples per sea kind); protected mask equals base-height test.
- Slope targets: section 1 table from 10^5 samples.
- Perf: ns/point and ms/leaf at LOD 0, 2, 4, 6, 8, 12 inside a belt; `Height`/`Bounds`/`Classify`; impostor ms; record `GenerationCost` baseline first.

Viewer: `--planet-kind <id> [--kind-seed N] [--kind-radius km]` swaps the test planet's kind in `--planet-fly` / `--moon-test`; `--kind-orbit` starts at 2.5 R. ImGui "Planet kind" panel: layer mute toggles, ns/point per layer, slope histogram vs targets, empty-leaf share.

## 7. Traps

- v1 interpolated the SHAPED ridge sum; now raw octaves, plus the new relief knee: hence `compat:"v1"`.
- Warp bounds need sqrt(3) and every octave; first draft lacked both.
- `Classify` with node LOD instead of `wantLod` is not conservative.
- Small crater classes are not zero-mean; do not skip them in bounds.
- Mid LODs cost about 2x LOD 0 (est.); row SIMD is needed in this slice.
- Coarse-interpolating a channel that feeds a steep curve facets cliffs (0.2 voxel at 256); use `precise`.

## 8. Not in this slice

| Item | Section |
| --- | --- |
| 3D layers, cavity/sky chunks, `Htop`, `Density`, `Crossings` | §1.6, §3, §4, K2/K3 |
| Materials, strata, surface rules, deposits | §5 |
| Decoration hooks, `SiteSample`, masks, biome | §6 |
| Landing sites, `pads` | §6, §1.3 |
| `volcanoes`, `cellular`, `glacier` | §1.3 |
| Other kinds (archipelago, alpine, volcanic, ice, shattered, small bodies) | §2 |
| Kind assignment (`SystemGenerator`) | §2 "Assigning kinds" |
| `--kind-sheet`, `--kind-ground`, `--kind-cave` | §7 Debug viewer |
