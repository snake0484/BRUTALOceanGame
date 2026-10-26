# Breaker review: cost and what decides a wave (2026-10-23)

A read-through of the breaker path (`BreakerSet`, `BreakerFaces`, `BreakerFace`, `BreakerLip`, `ocean_common.glsl`, `ocean_lip_base.comp`, `ocean_lip.vert/.frag`, `ocean_spray_spawn.comp`) after the seventeenth pass in `breaking-waves.md`, asking two things: where the CPU and GPU cost is, and what still makes the lip unstable in motion. Numbers quoted from the docs are the sixteenth and seventeenth passes (`--wind 14 --bench 12 --surf-test`, RTX 4090); a fresh run is at the end.

## 1. What decides a wave today

There are two different answers in the code, and that is the problem.

**The carrier field** (`OceanSwell.SurfCarrier`, `surfCarrierAt`). A wave is the set of points where the carrier's phase θ = k₀ d̂·x + lag(x) − ω_p t is a multiple of 2π. It is analytic, deterministic, continuous in space and time, and identical on the CPU and GPU. Since the seventh pass the face (peak, pitch, lift, foam), the crest state (curl, broken, height), the breaker type (plunge), and the lip's life (reach, crash) are all functions of this field plus the break-distance grid. Measured continuity is good: largest face change 0.038–0.076 m per tick, cap p99 0.004 per tick.

**The crest polylines** (`BreakerSet.ExtractSegments`, `Chain`, `BuildBreakers`; `BreakerFaces.Build`). Each tick, marching squares on sin θ over the 4 m coastal grid, chained through shared edges, kept only if some vertex is breaking, decimated to 128 vertices, Laplacian-smoothed twice, numbered, given a break point. `BreakerFaces` turns each chain into segments with a 12 m end taper (`EndTaper`) and vertex-averaged normals. The GPU then seats every segment end back onto the carrier crest (`seatOnCrest`) and takes its state from the carrier (`lipStateAt`).

So the polylines are only **seeds and connectivity** for the lips. Everything a lip is made of comes from the carrier; only *which* segments exist, how they are paired, and where their ends taper comes from the chains. The lip's remaining instability lives entirely in that second layer. The docs already say so ("what remains discontinuous is the crest polylines the lips are swept along", fifteenth pass), and the fix is to stop using chains as seeds, not to stabilise chains.

### Where the chains change topology between ticks

Each of these changes which segments exist or where their tapers sit, with no change in the wave itself:

- **Chain splits at surf-mask holes.** `_mask` is depth ∈ [−1, 1.3 · hs / 0.55] (`SurfMargin`, `CoastalField.BreakingGamma`), with hs the carrier's shoaled unbroken height. Over the surf-test bar (crest 1.6 m on a 2.2–3.8 m shelf, hs about 1–1.5 m, threshold 2.4–3.5 m) the boundary runs across the shelf, and any texel over a deeper channel is a hole. A crest crossing a hole is cut into two chains, and each new end carries a 12 m taper, so the lip vanishes over about 24 m while the crest crosses it.
- **Marching-squares saddles.** `ExtractSegments` pairs the four crossings of an ambiguous cell as (bottom, top) and (left, right), which are opposite edges, not a valid saddle resolution. Rare for sin θ (needs ∇θ ≈ 0, i.e. a caustic or shadow), but when it happens the two chains cross or swap partners as the saddle moves.
- **Decimation stride.** `stride = (length + 127) / 128`. When a chain's length crosses a multiple of 128 cells the stride changes and every vertex is resampled; the seated ends move only sub-decimetres, but segment lengths and the averaged `Normals` jump.
- **Chain limit and order.** `MaxBreakers` is 32 and chains are taken in cell-scan order. The surf test runs 25 crests; a long coast with sets can pass 32, and then which chains are dropped depends on scan order and flips as crests move.
- **Short chains.** `MinChainPoints` 4 drops chains under 3 cells; a chain hovering at that length flickers in and out.
- **The end taper itself.** Where a chain ends because the *mask* ends rather than because the *crest* ends, the taper hides geometry the carrier says should be there.

None of this is measured yet. A metric to write before changing anything: at fixed points along a breaking crest (the CPU mirror, as in `BreakerLipTests`), per tick, whether a segment covers the point and what taper and normal it has; count on/off flips and normal jumps over 600 ticks. `Face_IsContinuousTickToTick` is the model.

## 2. Cost audit

### GPU (per frame, RTX 4090, docs' figures)

| Scope | ms | Notes |
| --- | --- | --- |
| Ocean surface | 0.095 | 0.055 without layer B / faces; the surf zone roughly doubles a near vertex |
| Lip bases | 0.029 | two full swell sums per segment end (crest and landing) |
| Surf foam | 0.017 | 512² compute every frame |
| Spray pass total | 0.048 | update + surf foam + lip bases + spawn |
| Ocean lips (draw) | 0.004 | |
| Frame | 0.195–0.204 | |

**Where a near vertex spends its surf money** (`breakerFace`, every clipmap vertex under 8 m spacing): three `surfCarrierAt` (each 4 depth texels + 2 `coastalRefAt` × 4 texels = 12 vec4 SSBO loads), one `carrierDirection` (4 `carrierLagAt` × 2 `coastalRefAt` × 4 = 32 loads), `breakerPlunge` (2 `carrierDepthAt` × 4 = 8), `breakDistanceAt` (4), plus `waterDepth`, `zoneSurface` × 5 and `surfFoamAt`. About 80 vec4 loads with hand-rolled bilinear filtering, plus exp/log/pow in the peak and the soft ramps, before the swell sum itself. All of it reads the 28-float coastal texels through `std430 vec4 texels[]`, so nothing uses the texture units.

**Structural cut, GPU: a carrier atlas.** Everything the face and lips read from the coastal field for the carrier is static per field version: spatial phase (k₀ d̂·x + lag), the carrier wavevector direction, depth, shoal amplitude. Only `frame.carrier.z` (the time phase) and the group envelope move per frame, and both are cheap functions of the spatial phase. Build a 256² (or 2 m, 512²) image per coastal field version in one compute pass (or on the CPU: `BreakerSet.UpdateSurfState` already computes `_spatialPhase`, `_depth`, `_hs` per surf texel), with the direction pre-smoothed by the central differences `carrierDirection` does per vertex now. Channels: r32f spatial phase (half floats lose it: 200 rad at 10-bit mantissa is 0.2 rad), rg16f direction, r16f depth, r16f shoal. Then `breakerFace` does about 6 hardware-filtered fetches instead of about 80 loads, `carrierDirection` becomes one fetch, and the 4 m "texel line" shading artefact the docs list under not-yet can be fixed in the same place (bicubic on a texture is four fetches). Estimate, not a measurement: the surface pass's surf extra (about 0.04 ms) halves, and the lip bases and patch shaders inherit the same saving in their seating and state.

**Lip bases.** The landing sum was already tried as a lighter function and lost to warp divergence (sixteenth pass). The structural version of that idea is to put landings in their own workgroups (or dispatch) so no warp mixes the two, and only then lighten the landing. Modest gain (the swell sum dominates both). Not the first thing to do.

**Surf foam.** Runs the whole 512² grid at frame rate. Running it at tick rate (it decays with `dt` anyway) or over the surf band only would cut most of its 0.017 ms. Small, easy.

### CPU (per tick, main thread)

| Item | ms | Where |
| --- | --- | --- |
| `BreakerSet.Update` | 0.12 (island) – 0.54 (plane beach, 9.4k surf texels) | `GameApp.Tick`, main thread |
| `BreakerFaces.Build` | unmeasured; estimate 0.05–0.2 at a few thousand segments | per segment: `WaveNumberApprox`, two `VertexNormal`, two `Amplitude`, three distances |
| `OceanSurface` sample | 15.2 µs vs 3.6 µs bare | each `BreakerFace.Apply` does about 10 `CoastalField.Sample`s, each interpolating 12 references × 4 channels |
| `WriteBreakers` copy | small | 80 B × segments + 128 KB break bits, per tick per frame in flight |

- `CarryBreakDistance` clears the whole 256² grid (`Array.Clear(_breakNext)`, 256 KB) every tick and takes an `exp` per surf texel; it only needs to touch the surf cells (`_surfCells`), and the exp is 1 wherever `rate` is 1. The docs flagged both.
- `CarrierPhase`, `CarryBreakDistance` and `ExtractSegments` are independent per cell and sit on the main thread. CLAUDE.md puts heavy per-tick work on jobs. Splitting the surf cell list across jobs would take the plane-beach tick from about 0.5 ms to about 0.1 ms of main-thread time.
- `BreakerFaces.Shape` and most of `Heights` are computed per segment and never read by the GPU any more (CPU-only since the seventh pass); `Amplitude`, `Broken`, `Curl`, `Water` per vertex are dead work in the hot loop.
- The CPU face's cost is `CoastalField.Sample`: 112 lerps per lookup for 12 reference bands when the carrier needs one. A carrier-only lookup (the 7 floats per texel the atlas holds) would make `SurfCarrier`, `CarrierDirection` and `CarrierDepth` 5–10× cheaper and bring the surface sample back toward 2× bare. This matters once buoyancy samples many hull points.
- For scale: the hitches measured on 2026-10-22 were terrain-side (5 ms bathymetry upload, 5.7 ms rebuild, 3.2 ms LOD select), an order of magnitude above anything here.

## 3. Options for the lip

### A. Keep the analytic lip, replace its seeds (recommended)

Upload the raw marching-squares segments, not chains. `ExtractSegments` already produces edge pairs (`_segEdges`) and crossing points (`_edgePoint`); `BreakerFaces.Build` would walk those (within `LIP_DISTANCE` of the camera, so about a thousand) instead of `Chain`. Consequences:

- No chain, so no splits, no decimation stride, no `MaxBreakers`, no `MinChainPoints`, no end taper. Two segments that share a cell edge share that crossing point exactly, so ribbons stay seamless without the chain's averaged normals: the normal at an end is `carrierDirection` there, which is continuous and which both neighbours compute identically. Lips end where the carrier's curl, `Water(depth)` or the crash take them to zero, which is already smooth.
- Fix the saddle pairing by the cell-centre sign while touching that code.
- The lip-bases pass becomes one workgroup per *crossed edge* (edge id = 2c or 2c + 1), so shared ends are evaluated once by construction and `lipSharesNextStart`'s position comparison goes away.
- The CPU chains still run for gameplay (break point, peel, `BreakerBores`, the overlay), but nothing visual depends on them, so their flicker cannot show.
- Later, the same seed list feeds the fold's fine patch (`patchList`), which the seventeenth pass said is what the fold needs to replace the ribbon.

A second step, if the CPU tick needs it: generate the seeds on the GPU from the carrier atlas (marching squares per surf cell in a compute pass, compaction into an indirect draw). Then no breaker data but the break-distance grid crosses to the GPU per tick. This does not break the contract: the GPU is enumerating the same deterministic function it already evaluates per vertex for the face, not deciding where waves break, and nothing is read back.

### B. Stabilise the chains in place

Centre-sign saddles, a fixed decimation spacing (by distance, not by chain length), a deterministic chain ordering under the 32 cap, tapers only where the crest ends rather than where the mask ends. Cheaper to write, but every item is a patch on connectivity the lips do not need, and the mask-hole split remains.

### C. Particles with a goo (metaball) surface

- **For the lip sheet: no.** The tongue is a thin sheet; metaballs read as jelly unless the particle density is high enough that the blobs merge into a sheet, which for a 100 m crest at 0.1–0.2 m radius is thousands of particles per crest and the 16k ring is gone at two crests. A 3D density grid plus marching cubes is milliseconds; screen-space surfacing (depth sphere splats, bilateral blur, normals) is about 0.3–0.6 ms at 1440p on a 4090 and scales with pixels, roughly 3× the whole ocean pass today. It is stochastic, so the CPU `OceanSurface` cannot mirror it and physics would feel a different wave than the one drawn. And the instability it would be bought to fix is in the seeds, which the particles would inherit.
- **For the landing: later, and GPU-only.** The docs' open gap is that the crash "is foam and spray, not a splash volume". A screen-space thick-particle pass over the existing landing spray, visual only, bounded by the screen area the splash covers, is where a goo surface earns its cost. It needs no CPU mirror because nothing queries it. It is a feature for after the ribbon's seeds are stable, not a replacement for the ribbon.

### Recommendation

Do A's first step and the carrier atlas, in that order, with the chain-coverage metric written first so the before and after are numbers. Then job the breaker tick. Leave C for the splash, later.

**Done the same day:** A's first step (`breaking-waves.md`, eighteenth pass): the metric is `LipSeedTests` (raw crossings 0 misses and 0 flips over 22k lip samples against the chains' 5% and 10), the seeds are the raw crossings with one lip base slot per crossed edge, the saddle pairing is fixed, and `--chain-lips` keeps the old seeding for A/B. Then the carrier atlas (nineteenth pass): frame 0.199 → 0.193 ms, but the surface pass only 0.095 → 0.093, because the swell sum, not the carrier loads, is most of a near vertex; the surf foam pass, which evaluates `breakerFace` per cell of its 512² grid every frame, went 0.017 → 0.011. The estimate in section 2 that the surface's surf extra would halve was wrong by about 5×; the atlas was still worth 3% of the frame. What is left in the surface pass is the swell sum itself (the component count and the per-component coastal reference lerps). The surf foam pass then got its band test and tick gating (twentieth pass): 0.0113 → 0.0092 per update, and at uncapped frame rates the frame 0.191 → 0.180; its remaining cost is the walk over the 512² grid, not the evaluations.

## 4. Fresh benchmark

`--wind 14 --bench 12 --surf-test`, existing Release build (shaders of 2026-10-23 12:27), one run, idle GPU. "On" is layer B with faces, lips, spray and surf foam; "off" is the bare swell. Medians in ms.

| Scope | On | Off | Delta |
| --- | --- | --- | --- |
| Frame | 0.2046 | 0.1126 | 0.0920 |
| Ocean surface | 0.0952 | 0.0573 | 0.0379 |
| Lip bases | 0.0297 | 0.0031 | 0.0266 |
| Surf foam | 0.0174 | 0.0031 | 0.0143 |
| Spray draw | 0.0072 | 0.0000 | 0.0072 |
| Ocean lips | 0.0010 | 0.0000 | 0.0010 |
| Spray spawn | 0.0010 | 0.0010 | 0.0000 |
| Ocean FFT | 0.0306 | 0.0300 | 0.0006 |
| Terrain | 0.0184 | 0.0184 | 0.0000 |

The coastal and surf features are 45% of the frame on the 4090 (0.092 of 0.205 ms). Of that: the surface's surf extra 41%, the lip bases 29%, the surf foam 16%, the spray draw 8%. The two items the atlas addresses (surface extra, lip-base seating and state) are 70% of the delta; the surf foam's frame-rate update is another 16% for a map that only changes at tick rate.

## 5. The lip's life against the face it sits on (2026-10-23, later)

Seen in motion: a wave grows, tips over (a small lip), foams, the tip retreats, the crest is all foam, and the foam band sits at one place. `tests/Water.Tests/LipLifeProbe.cs` follows crests over the bar-beach probe (the storm scene's bar: 1.6 m crest, 1:50 shelf) at the bar's centre line and prints cap, break clock, curl, broken, H, lift, reach, throw and drop every 0.4 s. What it shows:

**Two clocks that do not agree.** The break clock (`BreakerSet.CarryBreakDistance`) starts at cap 0.97 (`ClockStartCap`) and runs the lip's whole life on travel alone: reach 1 by 0.175 λ, landed by 0.245 λ, broken (roller) by `BREAK_CRASH` 0.35 λ. The face's geometry runs on the *cap*: curl via `breaking` = smoothstep over cap 1 → 0.85, crest lift via `grow` over cap 1 → 0.6, the pitch with the curl. On a steep bed the two agree; on the 1:50 shelf, where the wind-20 sea's 2–3 m waves break (at 3.6–4 m of water, 40 m seaward of the bar), the cap falls 0.03 per 1.5 m of travel, so the clock has thrown the lip (reach 1 at 8 m, curl 0.3, lift 0.06) and collapsed the crest (broken 1 at 16 m, curl 0.45, lift 0.1) while the face is still standing up; the full face (curl 0.5, lift 0.7) arrives only as the bore. That is the sequence: a small tip on a face that has not stood up, foam, then the collapse.

**H falls with the depth while the lip is in the air.** `crestState`'s height is unbroken × cap, so on the bar flank (wind 14, 2.3 m unbroken) H goes 1.9 → 1.3 → 1.07 m over the 2.5 s of the throw as the cap goes 0.78 → 0.39, and the tongue's drop, throw and body follow it down. Physically the lip has left the crest; the height decays after it lands, not with the bed under it. The drawn carrier under the face shrinks the same way (`coastalTapAt`, the 0.55 d amplitude cap), without memory.

**The collapse retreats the tongue.** A plunging lip's crash (`lipLife`, 0.175–0.245 λ) feeds the fold's reach × (1 − smoothstep(0.4, 1, fall)), so the landed tongue shrinks back to the crest, and `broken` turns the crest into a roller at 0.35 λ. In the reference the lip stays thrown and the tube closes as the crest drops onto the landing, the whitewater erupting at the landing, not at the crest.

**Bigger waves, same tip.** H at the break is γ × depth, so the tongue's size is set by where the wave breaks, not by the offshore height, and on the bar top it is at most 0.78 × 1.6 = 1.25 m. In the storm scene the 2–3 m waves break on the shelf as spillers (ξ low on 1:50) and the 1.2 m lulls pass the bar at cap 1.0 without breaking at all: at wind 20 nothing plunges on the bar. The bar is sized for a 1.5–2.4 m wave (wind 14, which breaks on the flank at cap 0.78 with curl 1 at once).

**The foam band sits still** because each set's waves break where the depth is unbroken / 0.78 on the gentle shelf, and the bore's lift (`swash`, 0.35 × 0.55 × depth) is set by the depth, so every wave leaves the same band.

### What to change (done in the twenty-fifth pass, docs/breaking-waves.md)

1. Start the break clock when the face is ready (cap about 0.9, or `breaking` ≥ 0.5) and run the curl, crest lift and pitch *on the clock* from there, full within about 0.1 λ, so onset → stand-up → throw → landing → collapse has the same shape on any bed; the cap after onset feeds only `spent`.
2. Remember the breaking height: carry H at onset with the clock (or unbroken × max(cap, cap at onset) decaying after the landing), for the face, the lip and the drawn carrier's amplitude, so the tube is biggest while the lip is in the air and the wave decays after it lands.
3. Keep the tongue thrown once it has landed: no reach fade; the tube closes by the crest lift falling onto the landing as `broken` rises, the inner skin merging into the roller's whitewater, the splash at the landing.
4. Give the surf test a bar sized to the sea (`--bar-depth`, or depth from Hs / 0.78 with a 1:5 flank) so a wind-20 sea plunges on it rather than spilling on the shelf.

