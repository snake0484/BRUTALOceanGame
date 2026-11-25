# Shore wash

The water's edge on a beach: swash sheets that run up the sand and drain back, a foam line at the edge, and the foam the up-rush leaves behind. It is **visual only**, GPU only, and never feeds gameplay. Physics sees the depth-capped swell (`water-architecture.md`), not the sheets.

It replaces the island's breaker stack (breakers, lips, spray, bores into the zone sim) and layer B's coastal maps. The design history lives in git (`docs/breaking-waves.md` before the space pivot, "Swash and lace foam" and the thirty-third to thirty-fifth passes); this page is the system as it is now.

## Inputs

Everything the wash reads is a per-frame uniform or a bilinear map read, so nothing snaps tick to tick.

| Input | Where | What |
| --- | --- | --- |
| Signed depth map | `depthMap` (binding 4) ← `BathymetryTexture` ← `BathymetryGrid` | Sea level − ground per 2 m cell, 512² (1 km) around the camera; negative on land, `Pit` (−1000) in pits cut off from the sea, `depthRect.w` (deep) outside the map |
| Carrier | `frame.carrier` ← `OceanSwell.GpuCarrier` | ω, phase −∫ω dt, set group ratio, set group phase |
| Shore | `frame.shore` ← `OceanSwell.GpuShore` | xy = carrier travel direction, z = breaker height H_b (0 turns the wash off), w = open swell Hs |
| Set envelope | `groupEnvelope` (ocean_common.glsl) | Height × (1 ± 0.7) / √(1 + 0.7²/2), mean square 1 |

**The carrier** is the dominant band's peak (`WaveSet.CarrierOmega`, `CarrierDirection`): the groundswell once its energy is at least a quarter of the wind sea's (`SwellDominance`), otherwise the wind sea. Its deep-water height H0 = 2√(0.75 × the energy of the components within the peak band) (`CarrierShare`, `BandInner/Outer`). Sets come every 1 / `GroupRatio` waves, `GroupRatio` = 2 × the band's relative bandwidth within 1/20..1/4 (every 8 waves for a wind sea, 10 for a 5%-wide groundswell). On a wind change ω, `GroupRatio` and the direction blend with the components' weight, and both phases integrate ω per tick, so the swash never jumps (test: `WindChange_CarrierIsContinuous`).

**Breaker height** (`OceanSwell.PrepareCarrier`, once per tick): Komar and Gaylord (1972), H_b = 0.39 g^0.2 (T H0²)^0.4. Test: `Shore_BreakerHeightFromTheCarrier`.

**The depth map** is built from the streamed terrain leaves (column tops and sea flags per chunk, merged per 2 m cell from the finest leaf), shifted on re-centre every 128 m of travel and uploaded by changed regions only (`docs/terrain-lod.md`, "Streaming without stalls"). Sea flags come from the generated ground below sea level less edits that filled it. A below-sea-level column counts as sea within 4 cells (8 m) of open sea through other below-sea-level columns (`BathymetryTexture.UpdateShoreReach`, a breadth-first `Flood` rerun around changed regions only); further inland it is a pit and the ocean is not drawn there.

## Swash sheets (`swashSheet`, ocean_common.glsl)

Evaluated per ocean vertex in `oceanAt` and per foam cell in `ocean_surf_foam.comp`, for points with −`SWASH_MAX` ≤ d ≤ `SWASH_SEA` (2 m above sea level to 0.5 m deep).

1. **The shoreline below the point.** The beach slope is the depth gradient by central differences `SWASH_SLOPE_SPAN` 6 m either side (four taps, a 12 m span: one 2 m cell's slope varies by a third cell to cell and stepped the edge). The shoreline point is x0 − d ∇d / |∇d|²; s0 = −d / slope is the distance up the beach from it.
2. **Timing (Snell).** Along a straight shore k0 sin(angle) is conserved, so the arrival phase at the shoreline point is the open carrier's phase there: spatial = (ω²/g) dir · shore. `p = fract(−(spatial + phase) / 2π)` is how many periods ago the last crest arrived.
3. **The bore's energy height.** unbroken = H_b × exposure × exp(−surfWidth / `SWASH_DISSIPATE` 100 m), where exposure = √max(dir · seaward normal, 0) kills run-up on lee shores facing away from the carrier, and surfWidth = (H_b / 0.78) / slope is the distance from where the wave breaks to the shore (an analytic stand-in for the breaker grid's break distance). Each event also carries the set envelope its crest brought: `groupEnvelope(spatial + 2π since)`.
4. **Run-up events (Shen and Meyer).** For each of the last `SWASH_EVENTS` 4 crests, `since` = p + n periods ago, the edge runs up the beach ballistically: x = u0 t − a t² / 2 with u0 = `SWASH_U0` 1.2 × √(g × energy height) and a = max(g tan β (1 + `SWASH_FRICTION` 0.3), `SWASH_DECEL_MIN` 0.6 m/s²), then drains at `SWASH_BACK` 0.7 × a, so it stands at the top and drains slower than it rushed up. Vertical run-up is capped at `SWASH_MAX` 2 m (u0 ≤ √(2 a × 2 / slope)). The oldest event fades over its last period, so nothing pops when the phase wraps.
5. **The sheet is a lens.** Behind an edge the thickness is (x_edge − x)² / (9 g t²), t at least `SWASH_LENS_TIME` 1.5 s, over a floor of `SWASH_SHEET` 0.25 × the edge's height above the point and a `SWASH_MIN_SHEET` 3 cm film, capped at `SWASH_LENS_MAX` 0.25 m. The floor is kept because a film a centimetre thick is cut into blocks by the voxel terrain's facets (mesh and depth map disagree by centimetres). Past its edge each sheet dives under the sand at twice the beach slope, so the edge falls between vertices, not on them.
6. **Highest wins.** The surface above a point is the highest of the four sheets, so a bore arriving during a backwash pushes the edge out again.

In `oceanAt` the sheet replaces the swell height where it is higher, blended in over the first `SWASH_SEA` 0.5 m of water, and fades out with the clipmap's grid spacing between `SURF_SPACING_FULL` 4 m and `SURF_SPACING_NONE` 8 m (far vertices skip the work; no seam between levels).

**Numbers on the island's 1:35 beach** (thirty-fourth pass, before step 6's re-pointing): a 3–4.5 m/s edge runs 7–17 m up in 5–7 s and drains in 6–8 s, each bore its own excursion of about two periods.

## Depth cap and calm near shore

- **Depth cap** (`oceanAt`, mirrored by `OceanSwell.Cap` for `OceanSurface`): every swell component × min(1, 0.55 h / Hs) (`DepthCapGamma`), 0 on land and in pits, so the open swell cannot run through a beach. The CPU reads `IShoreDepth` (`TerrainShoreDepth`: the analytic terrain surface plus the brush edits as spheres), never the camera window, so a server with no camera gets the same answer (review ruling C7). Tests: `DeepWater_IsTheSwell`, `ShallowWater_IsCappedByDepth`, `Land_IsFlatAtSeaLevel`, `Sampling_IsAllocationFree`.
- **FFT calm**: the GPU cascades fade over `SHORE_CALM` 0–`SHORE_FULL` 1.5 m of water column under the surface (depth + height), so the chop rides the swell's crests, settles in its troughs, and the thin water at the edge stays glassy.

## Foam

- **Up-rush foam** (`ocean_surf_foam.comp`, binding 5): one float per 2 m cell of the depth map's 512² grid, GPU only. Once per tick (or at once when the depth map re-centres, which clears it): last value × e^(−dt / `FOAM_LIFE` 2.5 s), max'd with `SWASH_FOAM` 0.6 × `front`. `front` is 1 at the edge of an up-rushing sheet, falling to 0 `SWASH_FOAM_FRONT` 2 m behind it, full from `SWASH_FOAM_U0` 3 m/s of edge speed, on the beach only. Cells outside the depth band −`SWASH_MAX` ≤ d ≤ `SWASH_SEA` + 1 only decay. The vertex shader reads it bilinearly (`surfFoamAt`); the fragment draws it as lace at 0.6 × coverage.
- **Edge line** (`ocean.frag`): lace at `SWASH_EDGE_FOAM` 0.7 over the last `SWASH_EDGE_WIDTH` 0.5 m behind the edge of whichever sheet is the drawn surface (`swashEdge` → `vSwashEdge`), up-rush or backwash, plus 0.6 where the water is under 4 cm thick.
- Lace is `foamLace` (water_shading.glsl): a ridged-noise vein network on rotated grids, coverage sets how much is white, evened out to its mean where a pixel cannot resolve the cells.

## See-through shallows (`ocean.frag`)

- Thin water shows the terrain behind it: the share of a pixel that is neither the sky's Fresnel reflection, foam nor the water body (which builds up over `SHEET_OPAQUE` 0.6 m of thickness) is left out of the colour and taken off the alpha (straight alpha), darkened to `WET_SAND` 0.7. A film reads as wet sand under the sky's reflection; the body colour takes over by 0.6 m. Opaque again with distance (× e^(−dist × fog)).
- The waterline is anti-aliased: alpha × `clamp(thickness / fwidth(thickness) + 0.5, 0, 1)`.

## Cost

- **CPU:** the breaker height, once per tick inside `OceanSwell.SetTime` (a loop over the components). The depth cap on CPU samples is one `IShoreDepth.Depth` call (analytic height plus a loop over the edit ops).
- **GPU:** unmeasured since step 6. Last measured with the breakers in (`--wind 14 --bench 12 --surf-test`, RTX 4090, thirty-fifth pass): surf foam pass 0.0113 ms, when it also evaluated the breaker face per cell. The pivot inventory estimates the swash-only surf foam pass at 0.003–0.008 ms. `--bench` now alternates the shore wash on and off (Ocean panel F4 "Shore wash"), so the next bench run measures it directly.

## Not yet

- Runs on the flat island depth map. On planets the source becomes `ShoreDepthGrid` from the planet generator and edits (`IShoreDepth`), in a fixed per-region tangent frame (`docs/pivot/inventory.md`, `docs/pivot/review.md` C5, C7).
- Wet sand dries the instant a sheet leaves it; a wetness map read by the terrain shader would hold the dark band.
- The sheet is GPU only, so physics feels nothing of the run-up.
- Refraction is Snell's on a straight shore only: no wrapping round headlands, no sheltering beyond `exposure`.
- The edge scallops slightly between vertices on the coarser clipmap levels.
