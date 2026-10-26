# Breaking waves (design; CPU breaker entities v1 implemented)

Waves rolling onto the beach: they shoal, curl, plunge and run up the sand. The design leaves room for surfing later.

The rest of the water is a heightfield (layers A, B and C in `water-architecture.md`), and a heightfield cannot curl over itself. So waves are drawn mostly on the heightfield, and only the overhanging part is a separate **breaker** object that the renderer merges in.

## Life of a wave coming ashore

| Stage | Drawn by | Simulated by |
|---|---|---|
| Swell | Ocean heightfield (CPU swell + GPU FFT) | Layer A, `OceanSwell` |
| Shoaling (steepens, grows) | Heightfield, steepened by breaker sideways displacement (near-vertical face, no overhang) | Layer B maps + breaker entity |
| Lip and curl | **Breaker mesh**: a curl profile swept along the crest | Breaker entity |
| Plunge / broken white water | Heightfield + heavy foam, spray particles | Zone sim (layer C), fed by the breaker |
| Run-up and wash | Heightfield, thin translucent film | Zone sim (layer C) |

## Breaker entities (CPU)

### Where they come from

- Each regional layer B map is built from bathymetry. Its build also traces **crest lines** for the 1–3 most energetic swell components (by height, then period).
  - The wave rays bend with the depth (refraction, ω² = g k tanh(k h)).
  - Height grows by shoaling and conservation of wave energy along each ray.
- Each traced crest becomes a polyline, a few metres per vertex, valid for one component phase. The crest's position along the ray at time t comes from that component's phase, so it stays deterministic.
- **Breaking criterion:** H / h > γ, with γ ≈ 0.78, adjusted by beach slope (Iribarren number ξ = tan β / √(H / L₀)):
  - ξ < 0.5: spilling (a gentle beach; foam rolls down the face, little curl);
  - 0.5–3.3: plunging (the lip throws forward; this makes the tube);
  - ξ > 3.3: surging (a steep beach; the wave runs up without breaking).
- The **break point** along each crest is where the criterion first holds. It moves along the crest as the wave travels, which gives the **peel**.
  - Peel speed and direction come from the angle between the crest and the depth contours. A point, reef or beach break falls out of the seabed, with no authored data.

### State per breaker

```
struct Breaker
    crest polyline (world xz, local height H, local depth h, ξ per vertex)
    component index, phase
    stage per vertex: Shoaling | Curling | Plunging | Bore | Spent
    peel direction (+1 / -1 along the crest), peel speed (m/s)
    lip curve (curl angle 0..π per vertex)
```

- **Lifetime:** a breaker spawns when a crest first passes within a shoaling depth (kh < 1) and retires once every vertex is Spent.
  - A typical beach has a few breakers alive per swell set near the camera.
- **Determinism:** breakers are derived from seed, time, wind and bathymetry like the swell, so nothing is networked. Terrain edits rebuild the layer B maps and so the crest tracing.
- **Budget:** a fixed pool, e.g. 16 breakers × 64 crest vertices. They are updated in one job per tick. Expected well under 0.1 ms (measure before claiming).

### v1 as built (`BreakerSet`, `src/Water/Ocean/BreakerSet.cs`)

- **Carrier wave:** the swell's peak frequency is followed as one wave, phase θ = k₀ d̂·x + δψ(x) − ω_p t. δψ is the layer B lag interpolated at ω_p, read straight from the coastal texels.
  - The real surface is a sum of 128 random-phase components, so no single component ever reaches breaking height. The carrier is the coherent "dominant wave" a breaker follows.
  - It has no wave groups yet: every crest of the carrier is a set wave.
- **Crests:**
  - Contours where sin θ = 0 and cos θ > 0, found by marching squares on the 4 m coastal grid and chained into polylines.
  - Up to 32 crests per tick, each decimated to at most 128 vertices.
  - A crest's number is round(θ / 2π) at its first vertex. It stays the same while the crest travels in, so crests can be tracked between ticks.
- **Surf zone:** texels shallower than 1.3 · H_s,local / γ (and down to 1 m onshore).
  - The candidate list is cached per field and wave height. Each texel's depth, H_s, cap and spatial phase are cached per field and spectrum, and recomputed every tick only during a layer B crossfade (blended the same way).
  - Steady-state cost is one sincos per surf texel per tick.
- **Stage per vertex** comes from the same depth-limited cap the surface uses, interpolated along the crossing edge, so breakers sit where the drawn waves lose height:
  - cap = 1: shoaling;
  - cap ≥ 0.85: curling;
  - cap ≥ 0.6: plunging;
  - otherwise bore;
  - depth < 0.3 m: spent.
  - Only crests with a breaking vertex are kept.
- **Break point:** where the cap leaves 1 between a shoaling vertex and a breaking one, interpolated so it moves smoothly. Peel direction points toward the unbroken side.
- **Peel speed:** the break point's motion projected on the crest tangent, which removes the crest's own shoreward travel, smoothed per crest number.
- **Type:** Iribarren ξ = tan β / √(H / L₀) at the break point, using the seabed slope from the coastal depth: < 0.5 spilling, < 3.3 plunging, otherwise surging.
- **Debug:**
  - The Ocean panel's *Breakers* checkbox (or `--breakers`) draws crests over the scene by stage: blue shoaling, green curling, yellow plunging, orange bore, grey spent. A white dot with a tick marks the break point and peel direction.
  - The panel shows crest count, surf texels and update ms.
- **Measured (Release):**
  - Island at 14 m/s: 13 crests, 2,051 surf texels, 0.075 ms per tick.
  - Plane beach test (1 km of straight coast, Hs 3.2 m, the worst case): 9.4k surf texels, 0.30 ms per tick.
- **Tests** (`BreakerSetTests`):
  - Head-on swell gives crests parallel to the shore that only break where the cap is below 1.
  - Crest numbers persist and crests move shoreward at a few m/s.
  - Oblique swell gives break points peeling at 9–18 m/s along the crest.
- **Not yet:**
  - Wave groups (sets).
  - The zone sim hand-off.
  - Face displacement and lip mesh (rendering steps 3–4).
  - `OceanSurface.SampleBreaker`.
  - A job for very long coasts.
### Face displacement as built (`BreakerFaces` + `ocean.vert`)

- **CPU, each tick:** `BreakerFaces` turns crests into up to 4,096 segments. Each carries:
  - its ends;
  - the extra crest amplitude and the curl at each end;
  - the shoreward normal, celerity and local wavelength.
  - Segments are binned on a 64×64 grid of 16 m cells, up to 31 per bin, over the coastal map.
  - Uploaded to a storage buffer per frame in flight (binding 6) only when a tick produced new segments.
- **Amplitude** is the extra crest a breaker adds on top of the heightfield, which already draws the unbroken wave: 0.2 × the capped local height (it was 0.4).
  - It is a continuous function of the cap: zero while cap = 1, full by cap 0.6 (it was 0.9), easing off in the bore and as the water runs out, and tapered over 12 m at crest ends.
  - Breaking crests used to pop up above the surrounding swell: up to 1.1 m of extra lift in 0.95 s. They now rise to at most 0.4 m over 2.3–3.5 s (FaceLift_GrowsGradually requires at least a 1 s rise from 10% to 90%).
  - The lean (LEAN 1.8) and lip radius (LIP_RADIUS 5.0) were doubled to keep the same curl and lip size.
  - A crest therefore always appears at zero height and never pops in.
  - The first version scaled the whole crest by the full local height, including unbroken parts. That nearly doubled the swell and made crests pop to full size as they started breaking.
- **Curl:** 0 unbroken, 1 by cap 0.85, easing to 0.25 as it turns into a bore.
- **Vertex shader:**
  - Finds the nearest segment in the vertex's bin, advanced by celerity × (render − tick time) so crests move smoothly between ticks.
  - Adds a crest e^(−(s/w)²) across the crest, with back width 0.18 λ and front width narrowing to 0.05 λ (minimum 0.8 m) with curl.
  - Shifts the crest top shoreward by C·e^(2·…), with C capped so the surface never folds (|D′| ≤ 0.85).
  - Fades past crest ends, and adds the matching slope and Jacobian terms.
  - Zones override it like the rest of the swell.
  - The heightfield cannot overhang; the lip mesh (step 4) adds the curl itself.
- **Foam:** curl × crest shape is sent to the fragment shader as breaker foam, with the usual breakup noise.
- **CPU:** `OceanSwell.Sample` is the bare swell. `OceanSurface` adds the face on the CPU (sixth pass, below).
- **Test scene:** `--surf-test` builds a sandbar 2–3 m deep off the west beach (434 place brushes), so waves break over open water.
  - `--no-faces` turns the face off for A/B shots.
  - With `--breakers` it shows 25 crests at 0.12 ms per tick.
- **Fixed along the way:**
  - Every brush edit used to open a sea zone. With the sandbar's 434 underwater place-brushes that meant 434 zone discs (very slow), and the zone sim kept water depth as the bed rose, raising a standing wall of water. Now only digs open zones, and a rising bed displaces water instead of lifting it (`ZoneFlowSim.RefreshBed`).
### Lip mesh as built (`ocean_lip.vert` / `ocean_lip.frag`)

- **Shared surface code:** the ocean surface evaluation now lives in `shaders/ocean_common.glsl`: frame block, maps, layer B, breaker faces, and `oceanAt(x0, spacing)`.
  - `ocean.vert` is a thin CDLOD wrapper around it.
  - `ShaderLoader` resolves `#include` next to the including file (ShaderC's default include callbacks).
- **Instancing:** one instance per crest segment, with 11 quads across the lip, drawn after the ocean with the same layout and descriptor set. Segments without curl or amplitude cull in the vertex shader.
- **Base:** `oceanAt` at the moving crest point, so the lip grows out of the drawn surface (same swell, coastal terms, face, FFT and zone fade).
- **Cross-section:** an arc starting at the crest heading up and shoreward (−1 rad), sweeping up to 4.2 rad × curl.
  - It pitches over and throws down, closing toward a tube at full curl.
  - Radius is 2.5 × the face amplitude × curl (about the wave height), tapering 35% toward the edge.
  - Starting upward matters: an arc that heads straight down hugs the leaning face and is hidden inside the water by the depth test.
- **Shading:** the ocean's sky, Fresnel, tonemap and fog. A thin sheet glows green toward the sun (exp(−0.9 · thickness)), and the thrown edge turns to foam, more of it at higher curl.
- **Crest smoothing:** `BreakerSet` applies two Laplacian passes to crest vertices (ends fixed), which removes the marching-squares staircase from both the face and the lip.
- **Measured** (`--bench 12 --surf-test`, alternating layer B with faces and lips against none): ocean pass 0.110 against 0.055 ms. Layer B alone was +0.020 ms in the island view, so faces and lips add roughly 0.03 ms.
- **Looks:** modest on the test sandbar at 14 m/s (capped wave height about 1.1 m): the lips read as thin translucent sheets along the crests. It needs taller surf and a closer camera to judge; tuning is open.
- **Not yet:**
  - Spray where the lip lands (step 6).
  - The zone sim hand-off (step 5).
  - The lip inherits FFT detail only at its base.
  - No tube interior shading or self-shadowing.
### Zone hand-off as built (`BreakerBores`, `ZoneFlowSim.Inject`)

- **Each tick:** after the breakers update, `BreakerBores` walks crest vertices in the plunging or bore stage (at least 0.3 m deep) and calls `ZoneFlowSim.Inject`. Vertices outside a sea zone are refused, so the bore stays visual there.
- **Mass-conserving:** a bore moves up to 0.1 × H × √(g(h+H)) (capped at 6 m/s) per metre of crest per second from the zone cells behind the crest to those ahead, never more than half of what is behind.
  - Only the moving layer (thickness H) gets its outflow flux raised to the bore velocity, shoreward; the pipe model's limiter does the rest.
  - Foam is added at 0.8/s under a plunging lip and 0.3/s in a bore.
  - Nothing is created. The wave's water already arrives through the zone's sea boundary, and a real bore's return flow takes back what it carries.
  - The first version injected net mass. With bores arriving every tick at the same spot, water piled up against the beach into spikes several metres tall.
  - Also, pushing the whole water column (rather than the bore layer) moved 50 m of water at 5 m/s.
- **Shore foam:** the ocean shader's static shore foam now only marks the swash edge (thickness under 0.3 m, it was 1.2 m). Breakers supply the surf's whitewater, and on a flat shelf the old band painted tens of metres white.
- **Tests** (`ZoneInjectionTests`): volume is unchanged by an injection; the surge travels with its velocity (0.25 s later, +1.3 m ahead and −1.8 m behind in deep water); injection outside the zone is refused.
- **Measured** (`--seaside-test`, 14 m/s): about 240 m³ moved in 22 s, in bursts as sets arrive.
  - Zone sim 1.0–1.5 ms per tick at about 5,100 cells, against roughly 0.6 ms in earlier runs.
  - Part of that comes from extra zone growth the surges trigger (they exceed `GrowSurge` near zone edges). Something to watch.
- **Panel:** the Terrain panel's sea-zone line shows the volume breakers have moved.
- **Partial hand-off only:** zones are opened by edits, never by surf. Where a zone already runs, bores hand it their water movement and foam, and the ocean shader fades the face and lip out across the zone's 5 m edge blend. Everywhere else the wave stays purely visual, at no simulation cost.
- **Bores don't grow zones:** while bores have arrived within the last 2 s (`ZoneFlowSim.BoreQuiet`), a zone does not grow; growth is for edits draining. It's zone-wide, because a surge spreads to other edge cells within that time.
  - `Bores_DoNotGrowTheZone` checks it, with a control run where the same bores do grow the zone.
  - In `--seaside-test` the zone stays at 2–3 discs and about 1,200–1,500 cells at 0.21–0.27 ms per tick. Before, it was 30+ discs and 5,400 cells at 1.0–1.5 ms.
- **Zone foam comes from turbulence, not speed:**
  - Fast flow only foams when supercritical (Froude number over `FoamFroude` 1.5, white at 3) with depth behind it.
  - Pours (falls over 0.5 m) are unchanged.
  - Crashing fronts need a surface slope over 0.6 (it was 0.35).
  - Build 1.5/s (was 2.5) and decay 0.2/s (was 0.1, about 5 s to settle).
  - Bore foam is 0.15/s and plunge foam 0.5/s.
  - Mean foam over the zone's water (`ZoneFlowSim.MeanFoam`, logged by `--seaside-test`) dropped from 0.74–0.92 to 0.24–0.38 after the pour starts.
- **Not yet:** spray (step 6); return flow is left to the pipe model.
### Spray as built (`ocean_spray_*.comp`, `ocean_spray.vert/.frag`)

- **GPU-only particle ring:** 16,384 particles, a single host-visible storage buffer at descriptor binding 7, never read back.
  - Slots come from an atomic head, so the oldest particles are overwritten.
  - It's read-only everywhere except the two compute shaders (`SPRAY_WRITABLE`), because vertex stages may not see writable buffers without an extra device feature (validation caught it).
- **Frame order:** `OceanRenderer` is split into three steps.
  - `Prepare` writes the UBO, coastal maps and breaker segments before the render pass.
  - `RecordSpray` runs the update, then the spawn compute (outside the pass). Barriers order it after the previous frame's draw and before this frame's vertex reads.
  - `Record` draws the ocean, lips, then spray.
  - The ocean set's bindings are visible to compute, and its push constants cover the vertex and compute stages.
- **Update:** ballistic flight with drag (1.2/s), gravity and growth (0.6 m/s of radius as droplets mist out), until the particle's life runs out.
- **Spawn:** one invocation per breaker segment.
  - Spray only comes from throwing lips: smoothstep of curl from 0.6 to 0.95.
  - Rate: 12 per second per metre of crest per metre of wave height (height = face amplitude / 0.2), with stochastic rounding and at most 16 per segment per frame.
  - Particles start at the lip's landing point (the end of the lip arc, using the same constants as `ocean_lip.vert`, now in `ocean_common.glsl`) on the drawn surface.
  - They're thrown shoreward at 0.3–0.8 × celerity and up at (1.5–4.5) × √H m/s, live 0.8–2.2 s, and start 30–70 cm across.
  - None spawn inside sea zones.
- **Draw:** camera-facing quads, soft round alpha (opacity 0.55, fading over the last 0.5 s), lit by sun and sky, with the ocean's tonemap and fog. Depth-tested, not depth-written; dead slots cull in the vertex shader.
- **Measured** (`--bench 12 --surf-test`): spray compute 0.002 ms median, and the ocean pass including the spray draw is 0.112 against 0.110 ms before spray.
- **Looks:** subtle on the test sandbar at 14 m/s (lips about 1 m). It's a stream of white puffs riding ahead of the breaking crests. Tuning is open.
### Look pass: haze and blending

The shores read as a white haze, and breakers didn't look part of the wave. A/B shots with `--no-faces` showed most of the haze was not the breakers:
- **Shallows** (`ocean.frag`):
  - The sand-through-water body used the terrain's weak absorption, so 1–2 m of water read as nearly white sand.
  - Now light passes down to the sand and back up (twice the column), with sea-water absorption (0.45, 0.07, 0.04)/m and half the sun term.
  - The colour blends toward a turquoise in-scatter (1 − e^(−0.8 · thickness)), so shallows read as clear tinted water.
- **Lip** (`ocean_lip.frag`):
  - The "thin sheet glow" colour was far brighter than anything on the ocean surface, so the lip read as a separate cyan ribbon.
  - It now uses the ocean's body model (deep tint, wrap lighting, the same sun scatter, with a little extra through thin sheets).
  - It fades in from its root (alpha 0 to 1 over the first quarter), so it grows out of the face.
- **Face foam** (`ocean_common.glsl`): only on the front of a crest that is actually throwing (smoothstep of curl from 0.6 to 1), not the whole hump.
### Lip mesh switched off by default (superseded: on again since plan B)

- **Why:** in motion, lips looked bizarre.
  - They bob: each lip sits on the synthetic carrier crest, pinned to the drawn surface at that point. The drawn surface is a sum of 128 random-phase components whose own crests don't line up with the carrier, so the lip rides up and down on swell that isn't its wave.
  - Per-segment instances also show seams, and the shape reads as a regular pipe.
- **What still runs:** the face lean (added into the heightfield itself), face foam, zone hand-off and spray.
- **Turning it back on:** the Ocean panel's *Lips* checkbox, or `--lips`.
- **To make lips work:** breakers would need to come from the drawn surface's own crests (for example, local maxima of the CPU swell sum along the ray, or a narrow-band group carrier with phase-locked lips), and lips would need one continuous strip per crest rather than per-segment instances.
### Plan: lips on the drawn surface's own crests (steps 1–4 built, see below)

**Problem.** Breakers follow a synthetic carrier (one wave at the peak frequency). The drawn surface is a sum of 128 random-phase components whose crests don't line up with it, so lips pinned to the carrier ride up and down on unrelated swell.

**Options considered.**
- **A. Find crests on the drawn surface.** Sample the full swell sum on the surf grid and take local maxima along the wave direction.
  - On the CPU that is about 1–2 µs per texel × 2–9k surf texels, so 4–18 ms per tick. Too slow.
  - On the GPU, crests can be found but not chained into stable, numbered crest lines without a readback. Gameplay (surfing, buoyancy) also needs them on the CPU.
- **B. Make the surface follow the carrier in the surf zone (recommended).** Real surf turns coherent as it shoals: sets arrive as regular lines.
  - Near shore, the peak-band components hand their energy to the carrier, so the drawn crests *are* the carrier crests the breakers already track.
  - This is deterministic, CPU and GPU stay identical, and it costs one extra component per sample.

**Plan (B).**
1. **Surf-zone coherence** (`OceanSwell` and `ocean_common.glsl`, identically).
   - Weight w(x) = smoothstep from k_p·h = 1.2 down to 0.4, using the peak wavenumber and local depth from layer B. It's 0 offshore, 1 in the surf zone.
   - Components whose reference coordinate is within ±1.5 of the carrier's (the peak band) scale their amplitude by √(1 − w).
   - The carrier enters as one more Gerstner component: amplitude √(w · Σ_band a_i²) × its layer-B amplitude factor × the breaking cap.
     - Phase: θ_c = k_c d̂·x + δψ_c(x) − ω_p t, the same phase `BreakerSet` contours.
     - Displacement follows the local wavevector, with the same per-sample steepness clamp.
   - Energy (and so Hs) is unchanged, and offshore samples are bit-identical (w = 0).
2. **Breakers.** No change to extraction; carrier crests are now real crests. Each vertex's height and slope come from the drawn surface.
3. **Lip strip without seams.**
   - Per-vertex tangents: the average of adjacent segments' directions, uploaded with the segments (`BreakerSegment` gains end tangents). Both segments meeting at a vertex then build identical cross-sections and normals there.
   - Fade the lip by "crest-ness" at its base, using the drawn surface's slope along the shoreward normal (|∂η/∂n| / k_c A_c). A lip never stands where the surface isn't at its crest.
4. **Lip size and timing.**
   - Lip radius from the carrier's own capped amplitude (not the face lift).
   - Curl tied to the carrier crest's progress through the break zone, so a lip forms, throws and collapses over about one wave period.
5. **Later: sets.** Modulate the carrier with a group envelope from the beat of the two strongest band components. Crests then vary in size and only the bigger ones break, as on real beaches.

**Tests.**
- Hs and the energy spectrum are unchanged by the coherence transfer, and offshore samples are bit-identical.
- At breaker vertices, the drawn surface's height is a local maximum along the normal (zero slope within tolerance) on a plane beach.
- CPU and GPU agree.
- Lip seam: adjacent segments give identical vertex positions and normals at shared crest vertices.

**Cost.** CPU and GPU: one Gerstner component more per sample in the surf zone (well under 1% of the swell sum). Breakers are unchanged, and lips change one storage-buffer field.

**Risks.**
- The surf zone looks too regular until step 5 (sets).
- The coherence blend changes the wave pattern visibly if w ramps too fast; that's why it ramps over k_p·h 1.2 to 0.4, many metres.
- The zone sim boundary samples the swell, so zones see the coherent surf too. That's consistent, but more regular forcing.
### Lips on the drawn crests, as built (plan B steps 1–4)

- **Coherence** (`OceanSwell.CoastalWaves`, `oceanAt` in `ocean_common.glsl`, the same maths in both):
  - w = 1 − smoothstep(0.4, 1.2, k_c·h), where k_c = |k₀d̂ + ∇δψ_c| is the carrier's eikonal wavenumber from layer B at the sample.
  - The band edge is soft: band_i = 1 − smoothstep(1.0, 1.5, |ref_i − ref_c|), and each component scales by √(1 − w·band_i).
  - The carrier is written as one extra Gerstner component after the others (CPU slot `_count`; on the GPU after the loop, with the same anti-alias filter).
    - Amplitude √(w·Σ a_i² band_i) × the layer-B amplitude × the cap.
    - Phase −ω_p t + δψ_c − k₀d̂·x, so its crest is θ ≡ 0, the contour `BreakerSet` extracts.
  - The carrier's k₀, reference, phase at render time and band energy reach the GPU in the frame UBO (`OceanSwell.GpuCarrier`).
  - Scratch arrays are padded for the extra slot (`CoastalPadded`), and offshore (w = 0) the sum is unchanged.
- **Tests** (`BreakerSetTests`):
  - `Coherence_ConservesVariance`: Σ A² at 5, 2 and 1 m depth is within 1–3% with coherence on and off (the carrier's local k differs slightly from each component's).
  - `Coherence_PutsDrawnCrestsUnderBreakers`: the drawn crest (highest `SampleHeight` within ±λ/4 across the crest) lies within λ/8 of the breaker vertex for 89% of vertices with coherence, 26% without, which is about chance.
- **Seam-free lip:** `BreakerSegment` is now 80 bytes and gains:
  - `Normals`: a shoreward normal per end, from the crest tangent across the vertex's two neighbours. Adjacent instances build the same base point, cross-section and normal at a shared vertex.
  - `Heights`: H per end, tapered at crest ends and as the water runs out.
- **Crest fade:** at its base the lip measures the slope of the CPU surface across the crest (`cpuPartials`) against the carrier's a·k (a ≈ 0.35 H), and shrinks from 0.5 to 0.9 of it. A lip never stands on a face or trough.
- **Size:** lip radius = 0.4 × H × curl (`LIP_SCALE`), no longer from the face lift. Spray's rate and launch speed use H directly.
- **Not done from step 4:** curl still comes from the cap, not from the crest's progress through the break zone.
- **Measured** (`--wind 14 --bench 12 --surf-test`): ocean pass 0.099 ms against 0.112 ms before coherence, so it adds nothing measurable. With `--lips` the pass is 0.119 ms.
- **Looks:** the Debug run has no validation errors. In stills from the test camera, the lip is one continuous ribbon along a drawn crest. Judged right in motion, so lips are on by default again. `--no-lips` or the Ocean panel's *Lips* checkbox turns them off.
### Surf look pass: grow, crash, wash (as built)

Goal: a wave grows as it rolls in, collapses into a rolling crash and washes up the beach, all drawn as the water itself.

- **Crossfade bug (waves running backwards):**
  - Cause: when a coastal rebuild faded in, both the CPU (`CoastalSample.LerpFrom`) and the GPU (`coastalRef`) blended the two fields' phase lags. The lag builds up over hundreds of metres, so after a big seabed change (the `--surf-test` sandbar, any large edit) the fields differ by tens of radians. Sweeping that over the 1.5 s fade made the waves race or run backwards.
  - Fix: during a fade the surface is evaluated through each field and the results blended (`OceanSwell.SampleFields`; `swellAt` per tap in `ocean_common.glsl`, with the old field's direction in `coastDir.w`). The swell costs double, but only during the fade. Breakers take their carrier phase from the current field only.
  - Test: `FadingIn_DoesNotSweepThePhase` (1–2 zero crossings over a fade from the open ocean).
- **Less regular surf:** the carrier takes at most 75% of the peak band (`MaxCoherence`), and the band is narrower (±0.5 reference steps, fading out by ±1.0). The rest keeps its random phases.
- **Waves grow (carrier's own breaking):**
  - The coherent surf wave is no longer under the significant-height cap (γ = 0.55 on Hs, which shrank everything from about 6 m deep with Hs 3.2 m). It shoals until its own height reaches `WaveBreakingGamma` (0.78) × depth, then is held there (`OceanSwell.CarrierBreaking`, mirrored in `swellAt`). The rest of the spectrum keeps the Hs cap.
  - `BreakerSet` stages from the same cap, with coherence from k(ω, h). Breakers sit where the drawn wave actually breaks: 100% of breaker vertices are on a drawn crest in `Coherence_PutsDrawnCrestsUnderBreakers`.
  - On the plane beach (Hs 3.2 m, Tp 7.3 s), waves now keep 2.5–3 m height into about 2.5 m of water, then break and die out over the last 2.5 m. Before, they shrank steadily from 6 m.
- **Peaked crests:** the carrier carries bound 2nd and 3rd harmonics with weights (1, s, s²), normalised to its energy and all in phase at the crest. Sharpness s rises from 0 to 0.55 as the wave's H/h goes from 0.1 to 0.45. At full sharpness the crest stands about 1.56 × a and the trough about −0.64 × a: tall crests over flat troughs. The harmonics are in the CPU swell and the GPU identically. Energy is unchanged (`Coherence_ConservesVariance`, now taken at 8–10 m where nothing breaks).
- **Rolling crash:** `BreakerSegment.Heights.zw` carries "broken" per end, which rises as the cap falls from 0.85 to 0.55. Once broken, the face foam becomes a roller on the bore front, plus a short trailing tail (0.35 × e^(s / 0.12 λ)). Before, foam stopped as soon as curl fell in the bore. Lips shrink away by the same amount, so the lip falls into the crash; spray still uses the curl.
- **Wash:** a broken crest keeps a lift of 0.35 × γ × max(h, 0.6 m) down to the waterline. It runs up the beach until the crest is 0.8 m above sea level, so a thin sheet washes up and drains back. Before, the lift was cut off at 0.3 m depth.
- **Lip is the water:** the ocean's colour model moved into `shaders/ocean_shading.glsl` (`shadeWater`, `cascadePartials`, `foamBreakup`, `waterMap`). `ocean.frag` and `ocean_lip.frag` both use it, and `ocean.frag` now includes `ocean_common.glsl` instead of copying its declarations. The lip gets the same seabed body, depth tint, FFT detail normals and foam as the surface at its root. Before, it had its own fixed deep-water tint and read as a teal stripe over the shallows.
- **Camera:** `--cam` now overrides `--surf-test`'s framing, for close-up checks.
- **Measured** (`--wind 14 --bench 12 --surf-test`, lips on): ocean pass 0.124 ms against 0.119 ms before this pass.
- **Not yet:** the lip still leaves the crest at a fixed angle (`START`), not along the face's own tangent. Sets (group envelopes) are still open.

#### Swash and lace foam (second look pass)

Compared with a real beach, the broken wave wiped up the sand as a smooth white band. The water should slosh in and out, with lacy foam.

- **Swash** (`swashLevel` in `ocean_common.glsl`, GPU only like the faces; superseded by the thirty-third pass's run-up events, `swashSheet`):
  - Each crest that reaches the shoreline runs the waterline up the beach and back, once per wave: up over 30% of the period, back over the rest (a half sine in a warped phase).
  - It is timed by the carrier phase at the shoreline below each point. That point is found along the depth gradient, and the phase comes from layer B. So the whole edge of the water moves together instead of sweeping up with the crest.
  - Run-up height is 0.45 × the surf wave's unbroken height at the shore, at most 2 m. It is also capped so the edge's mean up-rush speed stays at or under 3 m/s on the local beach slope: run-up ≤ 3 × slope × 0.4 × period.
    - Without that cap, a 1 m run-up on a 1:20 beach crossed 20 m in about 2 s. The edge "zooped" up the beach and swallowed the incoming wave.
  - The edge rushes up over 40% of the wave period and drains back over the rest.
  - The sheet is one continuous surface, ground + 0.25 × (level − ground) + 3 cm, faded into the sea over the first 0.5 m of water. Above the waterline it just carries on under the sand, where it is hidden.
    - The first version dropped vertices above the waterline straight back to the swell. The edge then fell on mesh edges and drew a staircase along the beach.
  - The bore lift (`BreakerFaces.BoreLift`) now fades out over the last 0.4 m of water and hands over to the swash, instead of riding up the sand to 0.8 m above sea level.
- **Lace foam** (`foamLace` in `ocean_shading.glsl`):
  - Surf foam is a ridged-noise vein network. Coverage sets how much of it is white: a little foam is thin veins with the water showing through, and only a fresh roller is near solid.
  - It drifts with the breaker (crest velocity from `breakerFace`) in two layers half a 2 s cycle apart, so it never stretches. It evens out to its mean where a pixel can't resolve the cells.
  - Breaker foam coverage is capped at 0.7 and zone whitewater at 0.8. Shore foam is now a thin line where the water is under 6 cm thick; it used to be a 30 cm band that whitened whole swash sheets.
  - The FFT's persistent foam fades over the shallows with the FFT waves (`vShoreDamp`). Before, it left soft white blobs across the surf zone.
  - The lip uses the same lace.
- **Measured** (`--wind 14 --bench 12 --surf-test`): ocean pass 0.131 ms against 0.124 ms.

#### Lingering foam, visible swash, a lip that curls over, shallower beaches (third look pass)

- **Surf foam that outlives its breaker** (`ocean_surf_foam.comp`, binding 8):
  - One float of coverage per cell of the depth map's 512² grid (2 m), GPU only, shared by all frames like the spray. It is updated in `RecordSpray`'s barrier window.
  - Each frame: max(last value × e^(−dt / 4 s), what the surf makes now). That is `breakerFace` foam, plus 0.55 → 0 on a swash sheet during its up-rush.
  - Foam lingers behind a crest and fades as lace where a crest ends, instead of vanishing with the crest. It clears when the depth map re-centres.
  - The vertex shader reads it bilinearly (`surfFoamAt`); the fragment shader uses it at 0.6 × coverage.
- **Swash visibility:**
  - Open water is opaque by 6 cm (it was 15 cm), so a few-cm sheet reads as water.
  - Sand under water is 0.7 × as bright (`WET_SAND`), so the sheet stands out from the dry beach.
  - The up-rush leaves foam (above), and the edge line marks water under 4 cm thick.
- **Lip:** radius 0.75 × H × curl (`LIP_SCALE`, was 0.4), a tube about 1.5 H across. It holds full size until the crest is well into breaking (broken > 0.5, cap about 0.7), then collapses. Before, it started collapsing at full curl.
- **Lace:** each noise octave sits on its own rotated grid, with a light domain warp, so veins don't form squares. Distant foam evens out to 0.4 × coverage (was 0.6), less haze.
- **Shallower beaches** (`TerrainGenerator.Height`, `TerrainParams.BeachSlope` 1/50, `ShelfLength` 200 m):
  - Offshore of an island's coast the seabed is now a shelf: depth = d × slope × (1 + d / 200 m) at distance d, floored at the seabed. It blends into the open seabed over the outer 70% of the island's reach.
  - The old smoothstep profile was flat at the waterline but about 1:13 at 10 m out, 1:7 at 20 m and 1:4 at 40 m, so waves met a wall. The main island now has about 3–5 m of water 100 m out, and a wide surf zone with several lines of breakers.
  - Layer B and the bathymetry read the same function. The terrain golden hashes (`Lod0OutputMatchesReference`) were updated for the change.
- **Measured** (`--wind 14 --bench 12 --surf-test`): ocean pass 0.164 ms against 0.131 ms (bigger lips, the surf-foam reads, a wider surf zone). The spray scope, now including the surf-foam compute, is 0.037 ms against 0.002 ms.
#### Performance pass, peeling open lips and sets (fourth pass)

**GPU** (`--wind 14 --bench 12 --surf-test`, layer B on): frame 0.247 → 0.157 ms, ocean 0.164 → 0.083 ms.
- **Clipmap frustum culling:**
  - About 70% of the ocean's cost was vertex work (looking at the sky still cost 0.091 ms), drawn for the whole ring round the camera.
  - Each level is now 4 × 4 tiles of 24 × 24 quads, each one index range. A ring's inner tiles keep only their quads round the hole.
  - Tiles are culled against the frustum's side and near planes, with margins for displacement (12 m) and height (±(2 Hs + 4), and 25 m down for dug holes).
  - Sea view 0.129 → 0.101 ms; sky view 0.091 → 0.030 ms. `OceanRenderer.TilesDrawn` reports the count.
- **Lip bases (`ocean_lip_base.comp`):**
  - The drawn surface at each segment end is evaluated once, instead of in every one of the 66 lip vertices per segment (lips 0.042 → 0.004 ms). Spray spawn interpolates the same two ends instead of evaluating the surface per particle.
  - One thread per end left the GPU idle while each thread walked the whole wave sum (0.032 ms). Now a 32-thread workgroup per end splits it (`swellPart`/`swellShare`, reduced in shared memory) and one thread does the rest (`oceanFinish`): 0.016 ms.
  - Lips and their spray are built only within 400 m of the camera (`LIP_DISTANCE`).
- **Faces and swash fade with grid spacing:** they fade out between 4 and 8 m spacing (`SURF_SPACING_*`), smoothly, so there's no seam between clipmap levels.
- **Coastal maps:**
  - Three buffers are shared by all frames, instead of a pair per frame in flight (three copies of about 7 MB per rebuild).
  - A new field goes into one no frame in flight reads, a sixteenth per frame, and is drawn once complete. Until then the GPU keeps drawing the field it replaces.
- **Breaker CPU:**
  - `Jonswap.WaveNumberApprox`, the explicit Fenton–McKee form within 1.6% of the exact solve (tested), replaces the per-texel and per-segment iterative solve.
  - During a crossfade the surf state is recomputed at 10 Hz, not every tick. These were 4 ms ticks.
- **GPU scopes:** `Ocean surface`, `Ocean lips`, `Spray draw`, `Surf foam`, `Lip bases` and `Spray spawn` show in the profiler and in `--bench`.

**Lips:**
- **Direction:**
  - Crest normals were oriented against the swell's mean direction, so wherever refraction turned a crest more than 90° the lip threw seaward.
  - Each crest vertex now carries a travel hint (`BreakerVertex.Travel`): the grid edge it crosses, signed by the rise of sin θ, since the phase increases the way the wave travels.
  - Faces, lips and bore injection orient by it.
- **Open throw:**
  - The lip is a thrown sheet (`lipProfile`), not a closing circle. It leaves the crest at tan⁻¹ 0.35 above horizontal and lands `LIP_THROW` 1.1 H ahead and `LIP_DROP` 0.9 H below at full curl, so the tube under it stays open.
  - Once the crest is well into breaking (broken past 0.5) the lip keeps its shape, turns to foam and fades into the crash, instead of shrinking back.
  - Spray leaves from the throw's landing point.
- **Peel:**
  - Lips exist where the crest curls, so they peel wherever breaking moves along a crest.
  - The `--surf-test` bar now runs at about 19° to the crests (`Skew` 0.35, 36 m wide, 2.8 m deep), like a point break. Each crest breaks first at one end and peels at roughly celerity / 0.35, with whitewater behind and a green shoulder ahead.
- **Sets (`OceanSwell.GroupEnvelope`):**
  - The surf wave's height is modulated by a group envelope, (1 + 0.7 cos φ_g) / √(1 + 0.245), with φ_g = (k₀d̂·x + δψ − ωt) / 8.
  - It travels with the crests, gives a set every 8 waves and has mean square 1, so energy is unchanged on average (the variance test now averages over a group).
  - Later: the set length follows the dominant band's bandwidth, 1 / (2 × bandwidth) waves, carried to the GPU in `carrierChop.w` (`surf-research.md`, Groundswell).
  - Set waves stand about 1.5× the mean and break further out; the waves between them are small. The CPU surface, the GPU (`groupEnvelope`, phase in `carrierChop.z`), the breakers (cap recomputed per surf texel per tick) and the swash run-up all use it.
- **Crest fade loosened:** the lip shrinks only on a clear face, where slope across the crest is past 0.7–1.2 × the carrier's a·k with a ≈ H/2. The residual random swell tilts even a true crest a little.
- **Stable break points:**
  - A crest can have several breaking sections, and its chain can run either way between ticks, so the first transition flipped between them.
  - The break point is now the transition nearest this crest's last one. It eases toward it (0.15 per tick) and snaps only past 25 m. Peel speed is measured against the nearest remembered point of the same crest.
  - Break point speed went from p90 104 m/s to 29 m/s (`BreakPoint_MovesSmoothly`). The per-vertex cap that drives lips and foam was already smooth: p99 0.004 per tick (`BreakingCap_ChangesSmoothlyTickToTick`).
- **`--surf-cam`:** follows the curl of the tallest breaking crest near the one it followed, low in front of its face. `--surf-cam side` watches from the shoulder. Both ease, and stay 1.5 m above the ground.
- **Not yet:** the lip is a single sheet with no thickness, so from some angles it reads as a pale blade rather than a body of water. It needs a thickness (two surfaces or a tube) and judging in motion. (Done in the fifth pass.)

#### Pitched walls, a hook lip and churning foam (fifth pass)

The wave itself never curled: the surf carrier is a sine about 2 m high and 42 m long, so its front face sloped at about 0.15 and the old crest lean (under 1 m, over a band about 2 m wide) barely changed it. A lip on top of a mound reads as a cap, and one that throws 2 m ahead buries itself in a face that runs on for 10 m.

- **Peak (`breakerFace`, `PEAK` 3):**
  - As it curls, the wave gathers into a narrow crest over flat troughs.
  - Height above the trough, as a share q of H, goes to q^γ, with γ rising from 1 to 3 with curl. It is linear past the crest and unchanged below the trough.
  - Its slope terms include the window's own slope.
- **Pitch (replaces `LEAN`):**
  - The crest is sheared shoreward over the trough ahead by a tent in sn. The tent rises over the back (0.3 L) and falls over the upper face (0.2 L), built from soft ramps (`softRamp`).
  - The upper face is squeezed to 1 − `PITCH` (0.12) of its width: a near-vertical wall for the lip to throw from.
  - The crest top stays sharp (`PITCH_TOP` 20) and the wall's foot rounds into a concave trough (`PITCH_FOOT` 4).
  - Both troughs stay put.
- **Window:** peak and pitch fade out between 0.3 and 0.42 L, and by 30 m at most, before the nearest crest changes and inside the segments' bin reach.
- **Strength:** both scale with curl × (1 − 0.5 broken) × the crest-end fade, so the wall rises where the crest curls and peels with it. Curl is smoothed along each crest (`BreakerFaces.CurlSmoothPasses`, 4 passes), so the wall and lip grow out of the shoulder instead of starting at full size at the break point.
- **Whitecaps ignore the pitch:**
  - Open-sea whitecaps come from the surface's Jacobian, so the squeezed wall turned solid white: plaster aprons in front of every breaker.
  - `breakerFace` now reports the pitch's own d(shift)/ds (`OceanPoint.pitch`, `vPitch`), and the whitecap test uses J − pitch.
  - The face is now clean water. Whitewater comes only from the lip, the throwing crest top and the rollers.
- **Hook lip (`lipCurve`):**
  - The lip is a cubic Bézier with a body: an outer surface that rises off the wall, pitches over and plunges ahead, and an inner surface (the tube's ceiling) `LIP_THICKNESS` 0.22 × size inside it, thinning to a sheet at the edge.
  - `fall` (the collapse) pitches it down and in as the crest breaks.
  - `OceanRenderer.LipVertices` now covers both surfaces.
- **Foam churns instead of sliding:**
  - The drifting two-layer lace (and `foamFlow`) is gone. The lace sits on the water's rest position and churns in place: the domain warp and fine octave drift with time, so the veins wriggle, break and re-form.
  - Fresh whitewater churns faster (`LACE_ROLLER` 1.6) than foam left lying (`LACE_CALM` 0.35). The clock wraps at 600 s.
  - Dense coverage keeps holes and is thick on the lace's ridges, thin between them.
- **`--surf-cam side`:** sits 14 m along the crest toward the shoulder and 12 m ahead of it, at least 0.6 m above the crest. The pitch draws the crest about 6 m ahead of the CPU crest line, and the eased camera lags about 3 m, so nearer than this it ended up behind or inside the wave.
- **Cost:** `--wind 14 --bench 12 --surf-test` frame 0.157 → 0.161 ms, ocean 0.083 → 0.086 ms (RTX 4090). Debug validation clean. Water tests 24/24, terrain tests 49/49.
- **Not yet:**
  - Peak and pitch were GPU-only (the sixth pass mirrors them on the CPU).
  - A thin dark crease sometimes shows along a wall's foot or where two crests' windows meet.
  - Small slivers of lip appear on short crest chains.

#### The drawn face on the CPU: `OceanSurface` (sixth pass)

Physics and surf queries must feel the wave that is drawn. The pitched crest stands about 6 m shoreward of the bare swell's, with a wall at about 80°.

- **`OceanSurface`** (`src/Water/Ocean/OceanSurface.cs`) is the facade CLAUDE.md plans: the swell (layers A and B) with the breaker faces on it.
  - `Sample`, `SampleHeight`, `SampleNormal`, `SampleVelocity` and `SampleHeights` take world (x, z). `SampleRest` and `SurfacePoint` take a rest position.
  - `WaveSample.Whitewater` carries the face's foam weight (throwing crest or roller).
  - It is the same wave the GPU draws apart from the FFT detail, which the contract keeps GPU-only.
- **The face on the CPU** is a line-by-line port of `breakerFace`: crest lift, peak, pitch and their slope and Jacobian terms, with the same constants. The shader's comment and the C# constants both say to keep the two in step. It started as `BreakerFaces.Apply` on the crest segments; since the seventh pass it is `BreakerFace.Apply` on the carrier.
- **`OceanSwell.Sample<TShape>` / `SampleRest<TShape>`** take a shape (`IRestShape`, a struct, so no boxing) applied at rest positions.
  - **Newton inversion:**
    - The pitch squeezes the wall to about 0.1 and stretches the back to about 1.9, where the swell's fixed-point inversion (contraction about 0.9) stalls. Shaped samples invert with Newton steps on the full 2 × 2 Jacobian.
    - Steps are capped at 4 m. A step that doesn't reduce the miss is halved (up to 4 times), and failing that the plain fixed-point step is tried, which shrinks the miss wherever the Jacobian is in (0, 2).
    - It stops at a 0.1 mm miss or 16 steps.
  - **Coastal retakes:** coastal terms are linearised about where they are taken, and a pitched crest's rest position lies about 6 m from the query point, where the depth-limited surf wave is centimetres per metre taller or shorter. They are retaken at the rest point found (up to 3 times, until it moves under 5 cm), as the GPU evaluates them at each rest position. Retaking only once to within 1 m left 2.4 cm of error on the wall.
- **Consumers:**
  - `--surf-cam` keeps 0.8 m above the drawn sea under it.
  - The zone sim's sea boundary stays on the bare swell: breakers reach zones as bores (`BreakerBores`) and the drawn face fades out inside zones, so the face would count twice.
  - There is no buoyancy yet. When it lands it should query `OceanSurface`.
- **Tests (`OceanSurfaceTests`, beach fixture at a tick where a crest curls along its length):**
  - Round trip `SampleHeight(SurfacePoint(rest)) = height` across 3,488 points over curling crests, 763 of them on squeezed walls (least Jacobian across the crest 0.084, so no fold): worst 5 mm.
  - The face's analytic slope and d(shift)/ds against finite differences, on a plain sine: errors 2 × 10⁻⁴ and 1.6 × 10⁻³. The shader uses the same formulas for its normals and whitecap test.
  - With no segments, the surface matches `OceanSwell.Sample` to 6 mm (the retakes take coastal terms at the rest point rather than the query point).
  - Sampling allocates nothing.
- **Cost:** a surface sample over curling crests costs about twice a bare swell sample: about 11.6 µs against 5.9 µs in a Release test run, with run-to-run noise up to 2×. Both are dominated by building the per-point coastal wave set (a scalar loop over every swell component, up to 2 × 256 while wind sets blend); the surface builds it up to 4 times. A buoyancy pass with many hull points will want that build shared per body (all of its points lie within metres) rather than per point.
- **Not yet:**
  - `OceanSurface.SampleBreaker` (pocket, curl, peel and distance to the break point for surfing).
  - Face water velocity: the sample carries the swell's orbital velocity, not the faster water of a steepened face.
  - CPU/GPU parity is by construction (same formulas, tested derivatives), not measured against a GPU readback.

#### Stable faces: the face from the carrier, not the crest polylines (seventh pass)

**What was wrong:** the waves snapped between shapes, and the surface looked like it switched between two textures. A per-tick detector tracked the face's shear at 651 fixed points over the bar. It found steady snaps: about 880 jumps over 0.4 m (up to 3.6 m) in 40 s, with the camera fixed and no coastal rebuilds. They came from two causes.
- **The face's state lived on the crest polylines.**
  - Curl, broken and height were stored per `BreakerSet` chain vertex, then smoothed and tapered along the chain.
  - A chain that splits, merges, gains an end vertex or is resampled changes them at the same spot between ticks: curl 0.64 → 0.44, broken 0.23 → 0.50.
  - The pitch shears the crest by metres, so every such step became a visible snap. (Before the pitch, the 0.2 H crest lift hid them.)
- **The pitch was gated on the lift.** It was sized by curl but switched off when the crest lift was under 1 mm. Curl smoothed along the chain gave curl to vertices whose own lift was zero, so a 0.4–1.2 m shear flicked on and off.

**Now `breakerFace` (and `BreakerFace.Apply` on the CPU) is a function of the surf carrier alone:**
- **`surfCarrierAt`** (`OceanSwell.SurfCarrier`) gives the carrier at any point:
  - its phase, 0 on a crest;
  - its wavevector, with direction from the lag map and magnitude k(h) from `waveNumberApprox` (`Jonswap.WaveNumberApprox`);
  - its unbroken height, sets included, as `swellPart` draws it;
  - the depth.
- **Finding the crest:** two Newton steps along the wavevector find where the phase is 0, the nearest crest. sn is the distance from there along the crest's direction of travel.
  - It is a distance, not phase / k, so it moves one for one with x0.
  - The bilinear lag map's gradient steps at every 4 m texel line, by several percent in |k| in shallow water, so phase / k had kinks. The face's shear jumped 0.2 m across 6 mm there, and the CPU inversion stalled on the jump.
- **Direction (`carrierDirection`):** from central differences of the lag a cell apart, which are continuous. Off the end of the bar the map's own gradient swings 20–35° between cells, and the shear's direction flipped tick to tick as the crest search landed either side of a cell line.
- **Wavelength and state:** the wavelength and the crest's surf state (`crestState`: curl, broken, height, lift, from the carrier's breaking cap and depth there) come from the crest point. They are constant across one wave and continuous along it.
- **Fades:** the lift and foam fade out with the same window as peak and pitch, before the trough where the phase hands over to the next crest.
- **Coherence:** the drawn carrier's coherence uses k(h) h (GPU `swellPart`, CPU `CoastalWaves`), continuous where |∇lag| h stepped at texel lines.
- **During a coastal crossfade:** the face is evaluated per field and blended as the swell is. That is the field fading out, or the open ocean where it has nothing (GPU `oceanFinish`, CPU via `IRestShape.Apply(..., previous, ...)`). Switching to the new field at once snapped up to 1.1 m.

**Crest polylines now only carry the lips.**
- **`BreakerFaces`:** segments keep their geometry plus the chain's end taper (`Heights.zw`), so lips still narrow off where a crest chain ends. Bins, curl smoothing and the segment face are gone.
- **`ocean_lip_base.comp`:** writes each end's surf state (`LipBase.state`, from the carrier) every frame, zero for segments without a lip. `ocean_lip.vert` and `ocean_spray_spawn.comp` read it instead of segment curl and heights.
- **`--no-faces`:** now sets `breakers.info.w` (`OceanRenderer.FacesEnabled`) and `OceanSurface.FacesEnabled`.

**Measured:**
- **In game** (`--surf-cam --wind 16`, 40 s, two coastal rebuilds): no shear jumps over 0.4 m after the scene is built. The only ones were 7 points of at most 0.46 m in the first 3 s, while the first field fades in and the sandbar is placed.
- **Tests:**
  - `Face_IsContinuousTickToTick`: 900 ticks over 480 points, largest change 0.11 m per tick, against about 0.15 m from the crest's own motion.
  - Round trip: worst 4.3 mm over 3,488 points, 762 of them on walls.
  - Derivatives: 8 × 10⁻⁴ (slope) and 3.5 × 10⁻³ (d(shift)/ds).
  - Totals: water 29, terrain 49, physics 10. Debug validation clean.
- **Cost:** `--wind 14 --bench 12 --surf-test` frame 0.161 → 0.178 ms. Of that, ocean surface 0.076 → 0.081, surf foam 0.008 → 0.015 and lip bases 0.016 → 0.022 ms.
  - A face vertex costs four carrier lookups (12 SSBO reads each) and four lag lookups.
  - Lip bases evaluate the state for every segment end.

**Not yet:**
- **Coastal lag map texel artifacts:** the map's bilinear gradient still steps at texel lines in the swell's own normals (the carrier's slopes use it). That's a faint 4 m pattern in shading, older than the faces; a C¹ (bicubic) carrier lag would remove it.
- **Lip look:** lips still read as glassy sheets from some angles.

#### Water that runs the right way, and lips that stay (eighth pass)

**Texture flowing backwards.**
- **The pitch.** It was a shear tied to the moving crest, and the surface texture rides the rest grid. So as a wave passed, its water was pushed forward under the arriving crest and pulled back over the whole back of the wave at about 0.6 c.
  - In a real breaker, water ahead draws back toward the wave, runs up the face, and at the crest moves forward faster than the wave and overruns it.
  - The pitch now leaves the crest where the swell has it and draws the face back under it: from the crest to the foot the face is squeezed into the wall as before, and the trough ahead (`PITCH_RECOVER` 0.17 L, sharp at its far end, `PITCH_END`) gives back what the wall drew in.
  - As a wave passes, texture ahead of it draws back toward it and runs up the wall. Behind the crest the shear is under 1 cm (`Pitch_LeavesTheBackOfTheWaveAlone`), so the back is left as the swell has it.
- **The window.** Pitch and peak now fade from 0.4 to 0.48 L, measured on the phase at x0, which reaches the hand-over to the next crest at exactly ±π. Shoaling makes the spacing ahead shorter than the crest's wavelength, and a window on sn left 0.66 m jumps there. Its slope uses k(h) at x0. Past 0.35 L, where it fades, the derivatives are approximate, because the carrier's phase gradient steps at the lag map's texel lines.
- **The lip's foam and ripples** were pinned to the world while the lip moved through it at c, so they streamed back over it.
  - They now use the lip's own frame (`vLipUV`: metres along the crest from the lip base, and out along the hook), which travels with the crest. They stream outward at `LIP_FLOW` 2.5 m/s, water pouring over the crest to the edge.

**A lip that switched between sky and water colour.**
- The lip is drawn without culling, and its fragments turned their normal toward the eye by triangle winding.
- The strip runs out along the outer skin and back along the inner one, so the skins wind oppositely, and the winding also changes with the crest's orientation. Whenever the turn went the wrong way, the normal faced away from the eye, Fresnel went to 1 and that skin drew as pure sky reflection.
- Each skin is now shaded with its own outward normal (outer: away from the tube; inner: into it). A fragment whose side faces away from the eye is the far skin, and is discarded.

**Lips that flipped between having a tip and not.**
- The lip's size was multiplied by a crest test that shrank it wherever the drawn surface sloped across its base, at past 0.7–1.2 × the carrier's a k. On the peaked, pitched crest the surface is that steep within decimetres of the top.
- The base sat on the CPU crest polyline, contoured on the 4 m coastal grid once a tick and up to 0.14 m off the drawn crest. So over 600 ticks on the beach fixture, of lip ends that should throw (curl > 0.3), the test killed 16% and flickered another 16%.
- The test is gone. Each lip base is seated on the drawn crest instead (`seatOnCrest`: two Newton steps to where the carrier's phase is 0, as the face finds its crest).

**Measured:**
- Water tests 30/30. Continuity: largest change in the face's shear 0.076 m per tick in the surf zone.
- Derivatives within 0.35 L: 8 × 10⁻⁴ and 4 × 10⁻³. Round trip: worst 5 mm.
- Debug validation clean.

**Not yet:**
- Judged in motion only by the user. The stills used here (2 s apart) missed every one of these.
- The crash itself: as the crest breaks (broken 0.5 → 1) the lip pitches down into the trough and fades to foam, and the face relaxes into the bore. The lip doesn't yet land and explode into whitewater.

#### A break with memory, and a lip that is the wave going over (ninth pass)

**The whole crest curled at once, as a constant tunnel.**
- Curl came only from the local breaking cap, γ h / H: a property of where the water is, not of how long it has been breaking.
- Over the flat bar top, every part of a crest held the same mid-break cap for as long as it was there. So the lip threw and never fell, and the bar's skew only set how fast the whole bar filled (about 15 m/s).
- A real break has memory: a section throws once its face stands up, its lip lands within a fraction of a wavelength, and it rolls on as whitewater. The curl is only ever a pocket where the break begins, peeling along the crest, with an open face ahead and whitewater behind.

**Break distance (`BreakerSet.BreakDistance`):** per 4 m surf texel, how far the wave there has travelled since its face stood up.
- **Carrying it:** each tick it is carried from upwind, a tick's travel back along the carrier's local direction (the gradient of its spatial phase), at the carrier's celerity.
  - It grows by that travel times how far the face has stood up: from cap 0.92, full speed by 0.85, as the curl comes up.
  - It fades over 10 m (`ReformDistance`) by the rest, so a wave that re-forms over deeper water breaks again on the beach.
  - Re-centring shifts it with the grid.
- **It is a property of the water:** a wave that isn't breaking brings its 0 in from upwind at its celerity.
  - Zeroing it wherever the cap rose past a threshold flipped a collapsed wave back to a fresh one in a tick as a set went by (1.28 m jumps).
  - Starting it at a cap threshold ran the crash clock before the lip had formed.
- **Crest state** (`crestState`, `BreakerFace.CrestState`) takes the break distance at the crest point.
  - Progress = distance / (`BREAK_CRASH` 0.35 L). Broken rises over progress 0.5–1, so the lip pitches down and lands, and curl eases to a quarter over 0.6–1, on top of what the cap alone gives.
  - The face's peak and pitch, the lip's size and fall, and the foam all follow.
- **On the GPU:** the field goes up each tick as half floats after the segments in the breakers buffer (`breakDistanceAt`, bilinear like the CPU's). Header `grid.xy` = its origin, `grid.z` = cell size, `info.y` = resolution.

**The lip stood out from the wave.**
- **Root:** the outer skin started in front of and below the crest, rising out of the face. That left a seam where it emerged, and its normal there pointed shoreward where the surface's points up. `lipCurve` now starts on the crest top, nearly level, carrying on from the back of the wave before pitching over the wall. The inner skin, the tube's ceiling, starts inside the water.
- **Foam:** it aerated from 45% of the way out, plus the face's whitewater at the root, so every lip drew as a white curtain. Now only the thrown edge aerates (from 75% out at full curl), the root's whitewater is weaker, and the whole sheet goes white only as it lands.
- **Ripples:**
  - The lip's texture position is now the crest's rest point (`LipBase.surface.zw`), so at the root it samples the same FFT detail and lace as the surface it grows from. It moves out along the normal round the hook, travels with the crest, and the lace churns in place.
  - The previous frame projected the world position onto the crest's direction, which swung the texture metres whenever that direction wobbled a degree, the base being hundreds of metres from the origin.
  - An outward stream, u × `LIP_FLOW` × a clock, spread the lip over 2.5 t metres of texture, crushing its foam into strings within a minute of play. It is gone.
- **Body:** `LIP_THICKNESS` 0.22 → 0.4 of the lip's size at the root, tapering as (1 − u)^0.4 instead of ^0.6. The lip is a mass of water going over, opaque near the crest, rather than a translucent pipe skin.

**Measured:**
- `Break_LandsWithinItsCrashDistance` (beach fixture): breaking starts at x 778.5, the distance grows 0.94 m per metre, the pocket curls to 0.55 on that line, and past the crash distance curl ≤ 0.25 and broken = 1.
- `Face_IsContinuousTickToTick` (with the break distance): largest change 0.076 m per tick.
- Round trip: worst 4.4 mm, 490 points on walls, least Jacobian 0.24.
- Totals: water 31, terrain 49, physics 10. Debug validation clean.
- **Cost:**
  - GPU frame 0.183 ms (0.178 before; the break distance lookups).
  - CPU `BreakerSet.Update` 0.40 → 0.54 ms per tick for the carry over 9,439 surf texels (`SteadyStateCost`). That is main-thread tick time to trim: the full clear and the exp per texel could go.

**Not yet:** judged in motion only by the user. The crash still lands as a fade into whitewater rather than a splash.

#### Waves that stand up, spill and roll in (tenth pass)

**What was wrong on the beach** (`--cam -300 12 0 0 -8`, the island's west beach, 1.2 m 13 s swell): the swell came in without growing, broke as a line of lips about 100 m out, and from there to the sand the water was a flat, glassy, milky sheet with no crests in it. Three causes:
- **The FFT detail was faded out from 8 m of depth** (`shore`), so everything inside the break was glass: the "stationary shoreline water". The GPU band is wavelengths under 8 m, which the depth hardly touches until the last metre. It now fades over 0.2–1.5 m, under the swash only. (Later the fade runs over 0–1.5 m of the water column, bed plus swell height, so the chop rides the crests in and thins gradually to the water's edge: `ocean-gpu-contract.md`, Shore fade.)
- **The face only began at breaking.** Peak and pitch were driven by curl, which rises as the cap falls below 1, so a wave reared up only once it was already breaking; before that it was the swell plus the bound harmonics. After the crash, curl eased to a quarter and `amount` = curl × (1 − 0.5 broken) fell to 0.12, so a bore was nearly a sinusoid: nothing rolled.
- **Every breaker threw a lip and crashed within 0.35 L.** On a 1:50 beach with a 13 s swell ξ is about 0.4: the waves should spill (`surf-research.md`, gap 2), a steep foaming front rolling in for many wavelengths, not a tube.

**Now:**
- **Breaker type** (`breakerPlunge`, `BreakerFace.Plunge`): per crest point, ξ_b = tan β / √(H / L₀), with H the carrier's unbroken height there (sets included) and L₀ from the carrier's period. tan β is the depth difference over `SLOPE_REACH` (0.25 L) either side of the crest along its travel, read from the coastal field's depth channel (`carrierDepthAt`, `OceanSwell.CarrierDepth`), so a bar's flat top doesn't flip the type cell by cell. plunge = smoothstep(`PLUNGE_FROM` 0.35, `PLUNGE_FULL` 0.65, ξ).
  - Curl (lips, throwing whitewater, spray) is multiplied by plunge: a spilling wave throws no lip.
  - `BreakerSet.Classify` still labels whole crests for the overlay; the face and lips use the per-point value.
- **Stand-up** (`faceDrive`, `CrestState.Steep`): the peak and pitch strength is now max(curl × (1 − 0.5 broken), `STAND_AMOUNT` 0.65 × stand), with stand = smoothstep(`STAND_FROM` 0.45, γ 0.78, H / h). A wave rears up over the last stretch before it breaks, and a broken wave keeps H / h above γ, so its front stays a steep wall: the bore rolls in as a sawtooth rather than relaxing.
- **Spilling whitewater** (`faceDrive.y`, `CrestState.Spill`): (1 − plunge) × smoothstep((1 − cap) / 0.12): once breaking has begun, a spilling crest's front foams without a lip; the roller takes over once it is broken.
- **Less standing white:** the roller's trailing foam 0.35 over 0.12 L → 0.25 over 0.08 L; surf foam lingers 2.5 s instead of 4; the up-rushing swash seeds 0.35 instead of 0.55. The sheet over the inner surf zone hid every bore.
- **Tests:** the plane beach the breaker tests use (1:20 under a 16 m/s wind sea) now spills (ξ ≈ 0.3), so the face tests that need curling crests and pitched walls (`OceanSurfaceTests.Surf`, `Break_LandsWithinItsCrashDistance`) run on `PlungingBeachForProbe`: 1:12 under a 3 m, 12 s groundswell with light wind, whose 2–4 m surf wave breaks in 3–5 m of water at ξ ≈ 0.6–0.8. (A 1:8 beach under the wind sea plunged too, but its surf wave broke 9 m, two texels, from the shoreline: no crest chains.) `Surf` runs on until a crest is curling, since the pocket lasts a few seconds of each 12 s wave. `FacePartials_MatchFiniteDifferences` skips the last 0.6 m of water, where the carrier's wavenumber is clamped and the crest search steps between texels; the stronger bore front made that old error visible (0.23 at two points per segment). `Face_IsContinuousTickToTick` stays on the 1:20 beach.
  - Measured, water 35/35: breaking starts at x 782.5, the distance grows 1.03 m/m, pocket curl 1.00, past the crash curl ≤ 0.25 and broken = 1. Round trip worst 5 mm over 3,552 points, 343 on walls, least Jacobian 0.40. Derivatives 1.2 × 10⁻³ and 2.1 × 10⁻³. Shear behind the crest 7 mm, ahead 3.29 m. Largest tick-to-tick change 0.038 m. Surface sample 15.2 µs against 3.6 µs bare, allocation-free.
- **Cost:** two extra depth reads (8 texel loads) per face vertex and per lip base, no new buffers. Measured with the eleventh pass below.

**Not yet:** judged in motion only by the user. The spilling front is whitewater on the heightfield; it has no tumbling foam volume of its own. Peel still comes only from the seabed.

#### A lip that pours over (eleventh pass)

**What was wrong** (`--surf-cam side --wind 4 --swell 1.8 15 0`, a magenta debug lip shader to find them): the followed wave had no lip at all; lips drew only on the far beach lines, and there as white frills lying on the face. Three causes:
- **The breaker type's slope reach.** ξ took the bed slope ±0.25 L either side of the crest: ±20 m for a 15 s swell, which averaged the bar's 14 m face away and cut its lips to a third. The slope is now taken where the wave began to break (the break distance back along its travel, up to `ONSET_BACK_MAX` 60 m), over `SLOPE_REACH` twice the depth either side, at least a texel and at most 20 m: a bar's face sets the type and its flat top keeps it, as the lip it threw is kept.
- **The collapse followed the depth cap.** `broken` was max(cap-based, progress-based) and the curl eased on the cap too, so on a bar top (h 1.6 m under a 2.4 m wave: cap 0.52) every crest was collapsed the moment it arrived and every lip drew as a landed sheet. The collapse is now the break's memory alone (progress since onset); the cap only marks water that has run out (`SPENT_CAP` 0.45). A thrown lip lands after its set travel however fast the bed shoals under it.
- **The surf-test bar was buried.** Its crest sat 2.8 m deep on a shelf that is 2.2–3.8 m deep there, so the wave broke on the gentle shelf instead. `SurfTest.CrestDepth` 2.8 → 1.6 m stands 0.6–2.2 m proud with a face steep enough to plunge. The shoulder surf-cam also moved in (8 m along, 7 m ahead, 0.8 m above).

**The lip itself** (`ocean_lip.vert` / `.frag`, `lipCurve`):
- **Shape:** the tongue lifts a little as it throws (p1 at +0.18 drop), reaches 1.05 throw out at the crest's height and curls back under itself to land at 0.8 throw, 1 drop below, the tip pointing back into the tube.
- **Body:** full thickness (`LIP_THICKNESS` 0.45 of the size) out to `LIP_TAPER` 0.45 of the hook, then thinning to a feathered edge.
- **Pour:** the lip's foam lace streams out along the hook at `LIP_POUR` 3 m/s by flow mapping: two copies half a cycle (`LIP_POUR_CYCLE` 2.5 m) apart, each fading as it has slid a cycle, so the water visibly pours without the texture ever stretching.
- **Foam:** only the edge feathers (from 80% out at full curl, was 70%), the root carries a fifth of the face's whitewater (was 60%), and the whole sheet goes white as it lands. The face's own throwing whitewater now sits ahead of the crest where the lip lands (0.6) with little on the crest (0.15), so the crest stays glassy under the lip. A landed lip is gone by collapse 0.9 (was 1).
- **Spray:** `FEATHER_SHARE` 35% of the particles leave the lip's thin edge (u 0.7–1) as it throws, fine and short-lived, carried on with the lip; the rest leave the landing as before. `SPRAY_RATE` 12 → 14.

**Measured:** water tests 35/35 (the plunging fixture: breaking starts at x 782.5, pocket curl 1.00, landed curl ≤ 0.25, broken = 1). In game, from the shoulder, the followed wave now shows a glassy tongue over the wall with whitewater under it; head-on the tongue comes at the camera and reads as the whitewater patch where it lands. Judged in motion only by the user.

- **Cost** (`--wind 14 --bench 12 --surf-test`, tenth and eleventh passes together): frame 0.202 ms with layer B against 0.183 at the ninth pass; ocean surface 0.095 (0.081), lip bases 0.025 (0.022), surf foam 0.017, spray 0.044 ms. The surface's extra is the two depth reads per face vertex and the stand-up running on every near-shore vertex.

**Not yet:** no shadow under the lip, so the tube has no dark mouth; the landing is foam and spray, not a splash volume.

#### Spilling crests that tip (twelfth pass)

**What was wrong** (`--wind 20`): the wind sea (Tp ≈ 8 s, 4–5 m faces) has ξ ≈ 0.1–0.15 on the 1:50 beach, so the tenth pass's gating made every crest a pure spiller: curl = 0, no lip at all. The stand-up pitch still drew each one as a near-vertical wall, so they looked about to tip over and never did.
- A real spilling breaker does overturn at the crest, a small lip that tumbles straight down the face as whitewater; what it lacks is the thrown tube.
- Now curl = pocket × mix(`SPILL_TIP` 0.4, 1, plunge) (`crestState`, `BreakerFace.CrestState`): a spilling crest tips a lip 40% of a plunging one's size, which the break's memory collapses as before, and its face foams under it (the spilling whitewater), so the lip's root carries 0.6 × that foam over most of its length (`ocean_lip.frag`): tumbling white on a spiller, glass on a plunger (whose root whitewater is 0.15).

**Still not tipping** (the user's shot: a near-vertical spilling wall mid-run with whitewater streaking down it and no overturn). The spill lip was collapsed by the break's memory like a plunging one, so after 0.35 L of travel the wall rolled on bare for the rest of its run.
- Now curl = breaking × mix(`SPILL_TIP` × (1 − spent), plunging pocket, plunge): a spilling crest keeps tipping its lip for as long as the face stands, until the water runs out (`SPENT_CAP`). `crestStateAt` gives the lip its own fall in y: the collapse for a plunging lip, spent for a spilling one, so the spill lip never pitches down and fades (`ocean_lip.vert` read `broken` for that).
- `SPILL_TIP` 0.4 → 0.5; `STAND_AMOUNT` 0.65 → 0.55, since an unbroken face at 0.65 stood near vertical with nothing tipping over it.

**Measured:** water tests 35/35. From the shoulder at wind 20 the whole spilling crest now carries a white tumbling lip down its face; the plunging bar lip is unchanged.

**Not yet:** judged in motion only by the user. The spill lip is one hook shape scaled down; a tumbling spiller's crest should break up into separate falls along the crest.

#### A lip that goes over (thirteenth pass)

**What the stills hid.** A debug lip shader coloured by position along the hook showed the lips of the eleventh and twelfth passes for what they were: small flat blades riding the crest line, a fixed cap carried along with the crest, never reaching the trough, drawn as an opaque slab. Drop was 0.9 × size with size = H × curl, so a spilling lip on a 4 m wave fell 1.8 m from a crest 4 m above the trough; the hook's shape only changed with curl and the final collapse, so nothing in it ever moved; and the "verification" was shots from 7 m away with the lip shaded as water, where a foamed sliver on a foamed crest passed for a tongue. The user was right that it was not working.

**Now the tongue travels and falls to the foot.**
- **Reach** (`lipReach`, `BreakerLip.Reach`): how far along its path the tongue has got, 0 at the crest top to 1 at the landing, is smoothstep over the wave's travel since its break began, reaching the foot after `LIP_REACH_TRAVEL` 0.8 of the crash distance. The tongue grows out of the crest, arcs over and falls; a plunging lip then lands (collapse, fade) and a spilling one holds at the foot as a cascade while the face stands.
- **Path** (`lipPath`, `BreakerLip.Path`): a cubic Bezier in the crest frame from the crest top to the landing (throw ahead, drop below). Plunging: lifts as it throws, reaches `LIP_OVERHANG` 1.2 × throw out and curls back under to land with the tip pointing into the tube. Spilling: falls down the face it hugs. The drawn tongue is the first `reach` of it, with the body thick to `LIP_TAPER` and feathered at the tip.
- **The landing is the drawn face's foot.** `ocean_lip_base.comp` now evaluates the surface at two points per end, the crest and the landing, splitting both swell sums across its 32 lanes, and stores the landing (`LipBase.landing`, w = reach), the crest's surface normal (`LipBase.normal`, w = plunge) and the throw (`surface.x`). The landing's distance ahead is where the pitch's wall ends, `lipFootDistance` = `PITCH_FRONT` L (1 − `PITCH` × amount) (`BreakerFace.FootDistanceAt`), plus `LIP_THROW` 0.4 × H × curl. The drop is then the real height of the face there. (Sampling only 0.7 H ahead put the "foot" on the rounded crest top, 0.09 m below the crest: the first measurement caught it.)
- **One surface:** the tongue's normal blends from the surface's own over the first quarter of its length (`ROOT_BLEND`), so the root shades as the crest; its underside and the tube ceiling are darkened to `LIP_UNDERSIDE` 0.45 (the tube's mouth); the tip feathers out while still reaching rather than ending in a cut edge.
- **Spray** leaves the tip while the tongue is reaching, and the landing once it is there.
- **Buffer:** `LipBase` is 80 bytes (5 vec4), `OceanRenderer.LipBaseBytes`.

**Measured** (`BreakerLipTests`, the CPU mirror `BreakerLip` seated on every crest along a beach line, sampled every 0.1 s over a 12 s wave period):
- Plunging fixture: 1,316 lips; reach rises with travel (0.1 at 2.5 m, 0.5 at 5.5 m, 1 from 6.9 m at the earliest); while reaching the tip clears the surface under it by at least 0.10 m (up to 1.56 m at mid-reach); landed tips miss the drawn surface at the landing by at most 0.001 m and sit 1.23 H below the crest, 3.0 m ahead of it.
- Spilling beach (1:20): 543 spilling lips, all with the cascade at the foot, 1.31 H below the crest. (The wind sea's small waves plunge even on 1:20, ξ growing as H shrinks, so the test judges the spillers.)
- Totals: water 37/37. Release build clean.

**Second measurement, after the first in-game look.** With the landing at the wall's foot, a 1 m wave on the 80 m swell threw a 3 m slab (its "foot" is 0.2 L × (1 − pitch) = metres ahead, because such a face is not steep). The throw is now capped at `LIP_MAX_THROW` 1.2 × H, the landing stays exactly the drawn surface there (floor 0.02 H, miss 0.000 m), the tip feathers out always, and the pour now moves the ripple detail as well as the foam, accelerating down the tongue (`pourPhases`, `pouringDetail`: a pattern that only rode with the crest read as frozen).
- What that measures: within 1.2 H of the crest the drawn surface is only 0.20 H (plunging fixture), 0.14 H (1:20 spilling beach) and 0.34 H (`SteepBeachForProbe`, 20 m/s on 1:50, waves over 1 m) below the crest. The heightfield's crest is a rounded hump within a wave height of the top: the pitch squeezes the whole upper 0.2 L uniformly, so right under the crest the slope is still the cosine's, and only the biggest waves (the 4 m walls at wind 20 in game) stand steep enough for the tongue to overhang. `SteepFace_TongueFallsDownTheFace` is skipped with that reason: the face's pitch must steepen the crest itself before a lip can fall a face height. That is the next pass, on the face, measured the same way.
- In game (debug-coloured lips): tongues now grow with travel and hang out over the wind 20 walls; on the 15 s bar they lick down a gentle face. Segment chains still end in a cut edge along the crest.

**A life, not a state** (the user: tongues sat in a permanent state of tipping, and their shading was ribbed). Lips drew as soon as curl began, before the break distance grew, so they sat at reach 0; plungers held at the foot from 0.8 to 1.0 of the crash; spillers held at the foot for ever.
- `lipLife` (`BreakerLip.Life`): reach grows over the whole crash distance (`LIP_REACH_TRAVEL` 1.0), then the crash rises over `LIP_CRASH_TRAVEL` 0.2 more and the tongue fades to whitewater. No tongue below `LIP_MIN_REACH` 0.03. A spilling tongue restarts the cycle as soon as it has crashed, so the crest keeps throwing, reaching and crashing rather than holding a cascade.
- The ribbing was the FFT ripple slopes at full strength on a sheet seen edge-on; `LIP_DETAIL` 0.3 of them now.
- Measured: plunging fixture 1,198 lips, 158 just begun, 675 reaching, 250 at the foot, 151 crashed, the foot reached after 7.8 m at the earliest; 1:20 beach 518 spilling lips, 343 reaching, 96 at the foot, 37 crashing. Water 37 pass, 1 skipped.

#### The tongue falls (fourteenth pass)

**Judged in motion at last.** Thirty frames at 100 ms from the shoulder surf-cam at wind 20, tiled into a sheet, with the lip debug-coloured by position along it and by reach. The tongue was a flat shelf at crest height, a few metres long, that never dropped. Three causes:
- **Rest against world.** The landing was sampled a rest distance ahead of the crest, but the pitch squeezes rest space under the crest: 4.8 m of rest was 2.5 m of world, on the crest's rounded top, 0.14 H down (the 0.14–0.34 H the thirteenth pass measured and blamed on the face). The landing is now sampled down the wall's full rest span (`lipRestDistance`, `BreakerFace.LipRestDistanceAt`: `LIP_FOOT_SHARE` 0.95 × `PITCH_FRONT` L, or less where that would land further than `LIP_MAX_THROW` 1.2 H ahead in the world through the wall's Jacobian), and the throw is read back from the two samples in world metres (`LipBase.surface.x`).
- **A spiller's wall was a 25° slope.** Its pitch amount was 0.55 (Jacobian 0.52); the peaked top made it look steep. A breaking crest now stands at least `BREAK_STEEP` 0.85 under its lip whatever its type (`faceDrive`, `BreakerFace.CrestState`), easing back with `broken` into the bore front.
- **The reach eased out**, sitting at 85–100% for two of its three seconds. It now accelerates (r², the tip falling under gravity) and lands after `LIP_REACH_TRAVEL` 0.5 of the crash distance, about a second at wind 20.
- Pour lines on the glass: stretched noise scrolling down the tongue grooves the normal (`STREAK_*`, `ocean_lip.frag`) and feathers into foam toward the tip, so the sheet reads as water running, not a slab.

**Measured** (`BreakerLipTests`): plunging fixture, landed tips 0.91 H below the crest (was 0.20) and within 2 mm of the drawn surface, reaching tips clear the surface by at least 0.40 m (up to 1.9 m), the foot reached after 5.7 m of travel; 1:20 spilling beach 462 lips, 251 reaching, 127 at the foot, 69 crashing, 0.44 H below the crest (was 0.14); the 20 m/s 1:50 beach, waves over 1 m, 0.97 H below the crest and 1.26 H ahead. Water 38/38.

**Second look in motion.** With the fall right, the sheets showed a new tongue still hanging on the crest for two seconds before it moved, each spilling cycle starting as a flat shelf, and a net of thin veins over the glass.
- **The clock.** The lip appears as the curl rises from cap 1 (`LIP_MIN_CURL` by 0.98), but the break distance that drives its reach ran at full speed only below cap 0.85: 17 m of travel for a 20 m/s beach wave. `BreakerSet.ClockStartCap` 0.97 (full speed by 0.92) starts the clock as the lip appears. `Break_LandsWithinItsCrashDistance` still measures 1.04 m/m.
- **The path.** The first third of `lipPath` was horizontal (the plunging lift scaled to nothing), so a spilling tongue began each cycle as a shelf. A spiller now tumbles down from the start (p1 at −0.25 drop; p2 at −0.6), a plunger still lifts as it throws.
- **The foam.** `foamLace` at low coverage is thin veins: on the tongue it read as a mesh. Lip foam is now the streak field whitened from its ridges down as coverage rises (`streaks`, `ocean_lip.frag`), sliding toward the tip at `STREAK_POUR` 4 m/s; the lace only churns in as the lip crashes.

**Not yet:** the cost of the second surface evaluation per lip base; the landing is still foam and spray, not a splash volume; a segment chain still ends in a cut edge along the crest, and a segment whose two ends disagree on the shoreward normal draws a bow-tie.

#### The lip measured against the wave it sits on (fifteenth pass)

**Method.** Three things replaced stills from the shoulder surf-cam, which sits at the break point where every lip is newborn and places the surf-test sandbar whether or not `--surf-test` is given: (1) 3 s bursts at 100 ms tiled into contact sheets, shaded and with the lip debug-coloured by position along it and by reach; (2) the surf-cam logging the break points it follows (`SurfTest`: position, crest height, depth, type), so a fixed camera can be put on the break zone (wind 20: x −270 to −240 at 0.3–1.4 m crest height and 0.4–1.8 m of depth; the default scene: x −315 to −230); (3) CPU measurements in `BreakerLipTests`: one crest followed through its lip's life, the tongue's drop against the drawn face's own height, and a profile of the surf zone along one line.

**What they found, and what changed.**
- **The face under the lip is twice the carrier.** The drawn surface is the carrier plus the random sea: on the 20 m/s beach the drawn face at a landed lip is 1.96 m on average against 1.03 m of carrier, on the plunging fixture 1.16 against 0.64. The landing was placed from the carrier's capped height, so the tongue spanned 46–56% of its wall, a tenth of them under 37%. The landing is now sampled down the wall's full rest span (`lipRestDistance`) and only then pulled back along its chord where the face is gentle, so the tongue is never further ahead than `LIP_MAX_THROW` × the drop it actually falls (`lipClampLanding`); the body is sized from the real drop (`LIP_FACE_SIZE`).
- **The wall was a slope.** The pitch squeezed a fixed `PITCH_FRONT` 0.2 L of rest by 1 − 0.88, so a 0.64 m wave on a 65 m swell had a wall 3 m wide and 0.5 m tall. The squeeze is now chosen per wave so the wall's world width is `WALL_WIDTH` 1.0 × H, between `PITCH` 0.88 and `PITCH_SQUEEZE_MAX` 0.94. Round trip still 5.5 mm, least Jacobian 0.48.
- **The clock started late.** The lip appears as the curl rises from cap 1 (visible by 0.98) but the break distance ran at full speed only below cap 0.85, 17 m of travel for a 20 m/s beach wave, so a new tongue hung on the crest for two seconds. `BreakerSet.ClockStartCap` 0.97 (full speed by 0.92) starts it as the lip appears.
- **The spilling cycle was a sawtooth** (the user: the lip "inconsistently moves back and forth"). Throw, crash, throw again every 1.7 s made every point of the crest snap back to the top. A spilling tongue now extends over `LIP_SPILL_SPAN` half the wall and holds as a steady tumble, the pour (`pouringLace`, `pouringDetail`, the `STREAK_*` pour lines) carrying the motion, and falls only as the water runs out (`SPENT_CAP`); a plunging one still throws once, lands and crashes. The lip's state is the wave's: the break distance field carried with the water, so a tongue never jumps; what remains discontinuous is the crest polylines the lips are swept along (a chain that splits or re-forms changes which segments draw).
- Every tongue throws forward off the crest top before falling (`lipPath`: a spiller sooner and steeper); a spiller's tongue is tumbling whitewater (`SPILL_WHITE`), a plunger's is glass to the feathered edge; the pour lines are coarse and two-octave (2.5 cells/m ribbed the sheet); a segment whose ends disagree on the shoreward normal is culled rather than drawn as a bow-tie.

**Measured** (`BreakerLipTests`): plunging fixture, one crest followed: born, at the foot 1.7 s later, crashed 0.3 s after that, 1.66 H below the crest at the deepest; 1,316 lips over a period, the foot reached after 6 m of travel at the earliest, landed tips within 7 mm of the drawn surface, reaching tips above it by at least 8 cm. Steep (20 m/s, 1:50) beach: landed tips 0.96 H below the crest and 1.23 H ahead; the tongue spans 46% of the crest-to-trough face (the wall is its upper half; below the wall's foot the swell's own gentle slope runs to the trough), 38% at the tenth percentile; plunging fixture 40% and 16%. The 1:20 spilling beach: 543 lips, all holding at their span, 0.78 H below the crest. Surf-zone profile on the steep beach at one tick: break 0 m and cap 1.0 at x 696, then 14 m and cap 0.85 at 704 (the set wave there began breaking 14 m out), curl 0.5 from there in, 0 once the water is spent (cap under 0.24). Water tests pass; the Release build is clean.

**Does the root sit on a flank?** The hypothesis was that under a heavy random sea the drawn surface slopes through the carrier crest, so the tongue's root sits on a flank of the visible wave. Measured (`Lip_RootSitsOnTheDrawnCrest`: a root is on a flank when the drawn surface within 3 m along the travel stands a tenth of the face, and 10 cm, above it), with the root on the carrier crest against the root seated on the drawn surface's own crest by a secant step on its slope (`BreakerLip.SeatOnDrawn`, two more swell sums per end on the GPU):

| Fixture | Carrier crest | Seated on the drawn crest |
| --- | --- | --- |
| 20 m/s wind sea, 1:50 | 0% (surface nearby 1 cm higher) | 0% |
| The game's storm scene: 20 m/s plus the 1.2 m 13 s swell, 1:50 (`StormBeachForProbe`) | 0% (0.8 cm) | 0% |
| 3 m 12 s swell, 1:12 | 8% (6 cm) | 6% (7 cm) |

The face's peak sharpens the drawn surface around the carrier crest, so the carrier crest is the drawn crest. The GPU seating cost lip bases 0.025 → 0.079 ms and the frame 0.241 → 0.257 ms for that 8% → 6%, so it was taken out again; the CPU seat stays as an off-by-default diagnostic and the test guards the carrier-rooted rate (under 15%). The in-game sliver on a 4 m wall was the landing sized from the carrier, fixed above, not the root.

**Cost** (`--wind 14 --bench 12 --surf-test`, two runs on an idle GPU, medians within 0.002 ms): frame 0.220–0.222 ms with layer B against 0.202 at the eleventh pass; lip bases 0.045 (0.025: the tongues are larger and more segments draw), spray 0.065 (0.044: the feather spray and more throwing segments), surf foam 0.017 (0.017), surface 0.095–0.097 (0.095). A first run while another game held the GPU read 0.241 ms and 0.069 for spray; the seated-root run (0.257) was under the same load and its own lip-bases figure (0.079 against 0.045) stands. The spray spawn is the next thing to trim.

**Not yet:** judged in motion only by the user; the two in-game views here (a fixed camera on the wind 20 break zone and the shoulder surf-cam) show a white tumble pouring over the crest of the spilling walls. The lips' per-segment chains still flicker where a chain re-forms.

#### Trimming the spray pass (sixteenth pass)

The benchmark's **Spray** scope is the whole pass: the particle update, the surf foam map (0.017 ms), the lip bases and the spawn (0.002). Its growth, 0.044 → 0.065 ms, was the lip bases (0.025 → 0.045): two surface evaluations per segment end, for more and larger tongues. Each step measured with `--wind 14 --bench 12 --surf-test`, two runs each, medians within 0.001 ms:

| Change | Lip bases | Spray pass | Frame |
| --- | --- | --- | --- |
| Start | 0.045 | 0.065 | 0.220 |
| **Shared vertices evaluated once.** Consecutive segments of a chain share a vertex, so segment n's end 1 is segment n + 1's end 0 (`lipSharesNextStart`); the workgroup for the next segment's end 0 evaluates the point once and writes it to both segments' slots, or zeros per segment where one does not draw, so every slot is written every frame. (Two earlier versions had readers take the neighbour's slot: where the neighbour was culled its zeroed entry put an end at the world origin, and where it was skipped a stale entry from an earlier frame put the end metres away; either drew the lip as a slab across the whole scene. Caught by the 3 s debug-coloured sheets, not by the benchmark.) | 0.038 | 0.057 | 0.213 |
| **Serial work on two lanes.** The pass is latency-bound: lanes 0 and 1 seat and state the two ends side by side (lanes 2 and 3 the neighbour's), and finish the crest and the landing side by side. | 0.035 | 0.054 | 0.210 |
| Contiguous blocks per lane instead of a strided split, to keep the coastal reference cache warm: **slower** (0.043), the warp loses its coalesced reads of the wave array. Reverted. | | | |
| The shared end-1 workgroup returns at once instead of seating four ends to decide. | 0.034 | 0.053 | 0.209 |
| **`LIP_DISTANCE` 400 → 250 m.** A 1 m lip at 300 m is under 3 px tall at 900 px. | 0.029 | 0.048 | 0.203 |

| A position-only evaluation for the landing (the face on the swell, no swash, cascades, zones or foam reads): **slower** (0.033). Lanes 0 and 1 of one warp ran the same `oceanFinish` together; with a lighter function for the landing the warp ran both in series. Reverted; the two lanes must run the same code. | | | |

So the spray pass is 0.048 ms against the 0.044 before the tongues, and the frame is back at 0.203, the eleventh pass's figure, with the lips three times larger. The feather and landing spray are untouched (`SPRAY_RATE`, `FEATHER_SHARE`). What is left in the lip bases is the two `oceanFinish` calls per end, and they are already as parallel as a warp allows; the next cut would have to be fewer segment ends (coarser crest chains far from the camera) or a cheaper `breakerFace`. Water tests pass; the in-game sheets at the wind 20 break zone and the plunging bar are unchanged.

#### The lip as a fold of the ocean mesh (seventeenth pass, prototype behind `--fold-lip`)

**Why** (the user: why not bend the ocean mesh?). The surface is Gerstner: every vertex is displaced horizontally, so a fold is only that displacement overshooting until the rest-to-world map turns back on itself, the loop the steepness clamp forbids elsewhere. A fold needs no ribbon, no lip-bases pass, no crest polylines, and has no seam at the root.

**What was built** (`breakerFace`, `breakers.info.z`, `OceanRenderer.FoldLip`, the Ocean panel's *Fold lip*): with the flag, the rest strip just ahead of each breaking crest, 2 foldW wide (`LIP_FOLD_SPAN` 2 rest metres per metre of tongue size, times the lip's reach from `lipLife`), is mapped onto the ribbon's own path (`lipPath`): the first foldW as the outer skin from the crest top to the tip, the second as the inner skin back to the root a body's thickness inside, with the skin's normal written as the slope the fragment rebuilds it from. The face beyond the strip (wall, foot, trough) carries on from the tongue's root, drawn back by the strip's unfolded width and given back over the pitch's recovery. The tongue's foam is the ribbon's (spill white, feathered edge, crash), its size and drop from the carrier height (`LIP_FOLD_DROP` 1.2, `LIP_FOLD_THROW` 1.0, capped by `LIP_MAX_THROW` × the drop). GPU only; `--fold-lip` also turns the ribbon off.

**Cost** (`--wind 14 --bench 12 --surf-test`, two runs each, medians): ribbon 0.204 ms frame, 0.095 surface; fold 0.195 / 0.094; no lips at all 0.195 / 0.094. The fold costs nothing the benchmark can see: it is a few dozen instructions in a branch the vertices near a breaking crest already take.

**Look** (3 s sheets, shaded and with the fold's vertices painted magenta, from the fixed break-zone camera, the shoulder surf-cam, and a new `--surf-cam back` from the broken side where the lip in view is mature):
- At the finest clipmap spacing (0.25 m) a 1–1.5 m tongue gets 4–6 vertices per skin: it reads as a thick white wedge or hook pouring off the crest with a visible underside, and no root seam. Where the surf-cam's 0.8 m clearance keeps vertices fine, the hook is there in every frame.
- Past about 10 m from the camera the spacing is 0.5–1 m and the strip is one or two vertices: a shelf, or a tooth. The break zone from a fixed camera showed almost nothing, the strip falling between vertices.
- At wind 20 from the back view the folded crests are a sawtooth of white teeth, each the strip of one vertex column; the ribbon there is a smooth sheet.
- The fold also shows the FFT ripple detail on the tongue at full strength (the ribbon damps it to 30%), and the fragment's whitecap test fires on the folded Jacobian.

**Verdict:** the geometry works and costs nothing, but the ocean grid is too coarse for it beyond the first few metres. To replace the ribbon it needs the local fine patch the design always called for (a 0.1–0.25 m grid along breaking crests within 30–50 m, drawn after the clipmap), its own ripple damping and foam, and the whitecap test to skip folded vertices. Until then the ribbon stays the default and the fold stays behind the flag. Water tests 46/46 (the CPU surface does not fold).

#### Lips seeded by the crest crossings, not the chains (eighteenth pass; measurements pending a build)

**What decides a wave** (`breaker-review.md`): since the seventh pass, everything a lip is made of comes from the carrier field, which is continuous. The chained crest polylines only supplied the lips' *seeds and connectivity*: which segments exist, how they pair, and where the 12 m end tapers sit. Every way a chain changes topology between ticks changed the lips with no change in the wave:
- a chain split wherever the crest crossed a hole in the surf mask (a texel deeper than 1.3 · hs / 0.55, such as the trough behind a bar), and each new end tapered the lip away over 12 m;
- the decimation stride (`MaxVertices` 128) resampled every vertex when a chain's length crossed a multiple of 128 cells;
- `MaxBreakers` 32 dropped chains in cell-scan order; `MinChainPoints` 4 dropped short ones;
- a saddle cell paired opposite edges (bottom with top), two crossing pieces.

**Now `BreakerFaces` builds its segments from the raw marching-squares crossings** (`BreakerSet.RawSegment`, `EdgeVertex`, `EdgeNormal`), within `LipDistance` of the camera:
- No chain, so no splits, stride, chain limit or end taper. Two segments meeting at a grid edge share that crossing point exactly, and the normal at an end is the carrier's wavevector there (the spatial phase gradient at the edge's two texels, interpolated along it), which both compute identically, so the ribbons stay seamless without the chain's neighbour-averaged normals. Lips end where the carrier's curl, `Water(depth)` or the crash take them to zero, which is already smooth: at the mask's deep bound the cap is at least 1.08, so the curl is 0 before any chain ever ended.
- **Lip base slots.** Each crossed edge gets one slot (`BreakerSegment.Slots`, `BreakerFaces.SlotRefs`), shared by the two segments meeting there; `ocean_lip_base.comp` runs one workgroup per slot and writes every slot every frame. A slot is evaluated from half the lip's thresholds (`LIP_SLOT_*`), so a segment's last end, where the ribbon tapers to nothing, still has its point; below that it holds no lip (`position.w` = 1, `lipBaseEmpty`) and a segment with such an end never draws. `lipSharesNextStart`'s position comparison and the previous-segment logic are gone, and so are the redundant seats (each end was seated two to four times).
- Saddle cells pair by the sign at the cell's centre (`AddSaddle`).
- The chains still run for gameplay (break point, peel, `BreakerBores`, the overlay) and are untouched; nothing visual depends on them. `--chain-lips` and the Ocean panel's *Chain seeds* keep the old seeding for A/B (`BreakerFaces.UseChains`); it uses the same slots.
- The breakers buffer's header grows to 48 bytes (lip base slot count) and gains the slot refs after the patch list.

**Metric** (`LipSeedTests.RawCrossings_CoverEveryLip_AndNeverFlicker`, written before the change): on `BarBeachForProbe` (the storm scene on a 1:50 beach whose shoreline bows 40 m and carries a skewed bar with a 5 m trough behind it, so chains split and are resampled), with the camera on the bar and segments built within `LipDistance` of it as in the game, along rows every 16 m across the surf zone, every crest point within range that the carrier puts a lip on (BreakerLip's gate), every tick for 8 s after a 10 s warmup: is a segment within 1 m at taper ≥ 0.5 (a miss if not), and does that hold from one tick to the next on the same crest while the lip does (a flip if not). Both seedings are measured on the same ticks; it asserts the raw crossings miss under 1% and flip under 1 per 1000 tick pairs, and no worse than the chains.

| Seeds | Segments a tick | Lip samples | Uncovered | Nearest segment when uncovered | Tick pairs | Coverage flips |
| --- | --- | --- | --- | --- | --- | --- |
| Chained polylines | 380 | 22,263 | 1,107 (4.97%) | up to 2.2 m | 22,244 | 10 |
| Raw crossings | 1,565 | 22,263 | 0 | | 22,244 | 0 |

The chains' misses are decimated, smoothed vertices lying 1.4–2.2 m off the crest at the same spots tick after tick, and their tapers. A first run without the camera range, over the whole 1 km coast, had both seedings miss lips with the nearest segment tens of metres away: the 4,096-segment cap (3,022 raw segments a tick), which the game's range keeps clear of.

**Measured:** water tests 47/47; Debug validation clean over 30 s at wind 20 on the bar. `--wind 14 --bench 12 --surf-test`, one run each on an idle GPU: frame 0.2004 ms against 0.2046 before, lip bases 0.0276 (0.0297), spray draw 0.0051 (0.0072), surface 0.0942 (0.0952). More segments (4 m raw crossings against 8 m decimated chain vertices) but one seat per slot instead of two to four per workgroup, and fewer draw instances pass the cull.

**Look in motion** (3 s bursts at 100 ms, the lip painted flat (1, along, reach) by a debug fragment shader in the bin copy, raw and `--chain-lips` runs of the same deterministic scene, `--wind 20 --surf-cam side`, which places the bar): lips in both runs sit on the same crests in every frame, no slab, bow-tie or stray ribbon in either. Counting lip pixels per frame, the two runs agree within timing jitter for the first 2.3 s, then from the camera's arrival at the bar the raw run draws about twice the lip area the chain run does (frames 23–29: 99–129k pixels against 43–60k), where the bar's trough splits the chains and their end tapers cut the lips; at frame 25 the chain run's second lip begins with a hard vertical edge at a chain end, while the raw run's runs on and dips with the crest, and the far crest's lips are continuous. A hard cut at the near lip's end sits in both runs: the surf-cam's own near plane, not the seeds. From `--surf-cam back` the camera jumps between breaks, so per-frame counts there only measure the runs' frame offset; both draw the same lips, and both show a pre-existing artefact, detached pink flags hanging under a crest for a few frames where the ribbon's far skin is seen from behind. Judged by the user next.

**Not yet:** the far-skin flags; jobbing the breaker tick.

#### The carrier atlas (nineteenth pass)

**Why** (`breaker-review.md`): every near-shore vertex's `breakerFace` read the surf carrier three times (12 vec4 loads each from the 28-float coastal texels, two references mixed per lookup), its travel direction once (`carrierDirection`: four lag lookups, 32 loads) and the bed depth twice for the breaker type (8 loads), about 80 hand-filtered SSBO loads before the swell sum; the lip bases' seating and state read the same. Everything the carrier takes from a field is static per field version and carrier wavenumber.

**Now** `ocean_carrier_atlas.comp` writes a 256² `rgba32f` image array once per (field version, carrier k₀, carrier reference): layer 2 tap = (carrier lag, depth, shoal amplitude, active flag) per texel, layer 2 tap + 1 = the lag's central differences a cell apart (one-sided at the map's edge). `surfCarrierAt`, `carrierDirection` and `carrierDepthAt` read it with the same four-texel bilinear as before (`carrierTexels`, `imageLoad`, exact weights, so the CPU's `SurfCarrier` still matches by construction: the reference mix commutes with the bilinear, and a bilinear sample of per-texel central differences equals the old central differences of the bilinear lag). Per `breakerFace`: 28 image loads against about 80 SSBO loads, and no per-reference lerps. `OceanRenderer.Prepare` keys the two taps (`AtlasKey`) and `RecordSpray` dispatches the rebuild before anything reads it, with a `Carrier atlas` GPU scope; the carrier's k₀ and reference only move when the wind's target band changes (`OceanSwell.PrepareCoastalRefs`), so the rebuild is rare. Binding 10, a storage image declared `readonly` outside the compute shader (vertex stages may not see writable images without a device feature, as the lip bases buffer found).

**Measured** (`--wind 14 --bench 12 --surf-test`, the old carrier functions and the new swapped into the bin's shader copy alternately, two runs each, medians within 0.001 ms):

| Scope | Before | After |
| --- | --- | --- |
| Frame | 0.1992 / 0.1993 | 0.1930 / 0.1926 |
| Ocean surface | 0.0942 / 0.0952 | 0.0932 / 0.0922 |
| Surf foam | 0.0174 / 0.0174 | 0.0113 / 0.0113 |
| Lip bases | 0.0276 / 0.0276 | 0.0266 / 0.0266 |
| Spray pass | 0.0471 / 0.0471 | 0.0399 / 0.0399 |

The frame is 3.3% shorter. The surface pass gains only 1.6%: the carrier loads were never most of a near vertex, the swell sum is. The surf foam pass, which evaluates `breakerFace` for every cell of its 512² grid each frame, loses a third of its cost, and the lip bases 4%. The `Carrier atlas` scope reads 0.007–0.008 ms per rebuild, two taps; in `--bench` it repeats that figure every frame because the bench reads each ring's last sample and the scope is only written when a tap rebuilds (the frame total shows it is not per frame). Debug validation clean over 30 s at wind 20 on the bar (a `readonly` storage image read in the vertex stage passes without the stores feature). In game (`--wind 20 --surf-cam side`, 3 s at 100 ms, shaded) the faces stand and the lips pour over the walls as before; CPU and GPU still agree by construction, since the atlas holds the same per-texel values the CPU interpolates.

**Not yet:** the swell sum is what is left in a near vertex.

#### The surf foam pass over the surf band, at tick rate (twentieth pass)

**Why:** `ocean_surf_foam.comp` ran `breakerFace` and `swashLevel` for every cell of the depth map's 512² grid every frame, 0.011 ms after the atlas, though only cells where a crest can be breaking or the swash runs can make foam, and the surf that seeds the map moves once a tick.

**Now:**
- **The surf band** (`surfBandAt`, `ocean_common.glsl`): a cell first does one atlas lookup and skips the two evaluations unless it is inside the current field, in water no deeper than `SURF_BAND_MARGIN` 1.3 × the coherent surf wave's shoaled height at the top of a set over the breaking ratio, plus `SURF_BAND_REACH` 1 m for the roller's tail behind a crest, and no higher up the beach than `SWASH_MAX`. That is BreakerSet's surf mask (1.3 · hs / γ, with the set peak in) plus a metre, so every cell the breakers can mark keeps its evaluation; outside, the cell only decays. The roller's tail reaches about 15 m seaward of a crest, 0.3 m of depth on 1:50 and 1.25 m on 1:12, within the margin.
- **Tick rate** (`OceanRenderer.RecordSpray`): the pass runs when the tick index has advanced since its last run, or at once when the depth map re-centres, decaying by the seconds since the last update. At 60 Hz vsync with 60 Hz ticks that is every frame as before; above it the pass runs 60 times a second.

**Measured.** The band test alone (`--wind 14 --bench 12 --surf-test`, the old and new compute shader swapped into the bin's copy alternately, two runs each, medians within 0.001 ms): surf foam 0.0113 / 0.0113 → 0.0092 / 0.0092, frame 0.1934 / 0.1924 → 0.1910 / 0.1908. What is left is the walk over 262k cells (read, decay, write and the atlas test: 0.002 with layer B off, when the test returns at once) plus the in-band evaluations. With the tick gating, one run at the bench's uncapped frame rate: frame 0.1799, spray pass 0.0276 (0.0379), the surf foam scope reading its last update (0.0102) since the bench repeats a scope's last sample; at 60 Hz vsync with 60 Hz ticks the gating changes nothing. Debug validation clean over 30 s on the bar; from the shoulder surf-cam at wind 20 the foam left behind the crests and the rollers look as before, with no edge where the band ends (outside it nothing was ever made; cells there only decay, as they did).

Over the day's three passes (seeds, atlas, foam band and gating) the frame went 0.2046 → 0.1799 ms at the bench's frame rate, and 0.2046 → about 0.191 at 60 Hz vsync, where the gating does not apply.

**Not yet:** the foam walk could be confined to the band's bounding box or a coarser grid.

#### One flow over the back, the crest and the lip (twenty-first pass)

**What was wrong** (the user: the water should run up the back of the wave, over the top and into the lip, unbroken). Three things changed at the crest line at once. The surface's lace and ripples sit on the rest grid and churn in place, so relative to the moving crest they stream *backward* at the wave speed, while the lip's pour streamed *forward* at a fixed 3 m/s; the heightfield handed over to a separate ribbon; glass handed over to white. In the crest's frame real water slides back down the back face, moves with the crest at the top (u → c is the breaking criterion), is thrown forward in the lip and tumbles down the front with the crest as the roller.

**Now, GPU only (texture, the CPU face has no need of it):**
- **A flow speed in `breakerFace`** (`FLOW_*`): the water's forward speed relative to the rest grid along the crest's travel. Nothing at the trough behind, rising as (smoothstep)² over `FLOW_BACK` half a wavelength to `FLOW_CREST` 0.6 × celerity at the crest top of a face at full steepness (`max(steep, broken)`, so a broken bore's front keeps flowing), holding and fading over `FLOW_FRONT` 0.37 wavelengths down the front as the roller, inside the face's window so it is gone before the next crest's trough. Carried as `OceanPoint.flow` through `oceanFinish` (crossfaded like the face, faded with grid spacing and zones) to the fragment as `vFlow`.
- **Shared flow mapping** (`ocean_shading.glsl` `flowPhases`, `flowingLace`, `flowingPartials`): the lip's two-copies-half-a-cycle scheme, moved out of `ocean_lip.frag`, slides the surface's lace (surf foam and whitecaps) and its FFT ripple normals along the flow; where the flow is zero the old `foamLace` and `cascadePartials` paths run unchanged, so offshore is untouched.
- **The lip streams on from the surface:** `lipStateAt` returns the same flow speed at the crest top (`flowRoot`, in `LipBase.state.w`, which held the unused lift), the lip's pour starts at that speed and rises by `FLOW_TONGUE` to twice it at the tip, on the same clock. The lip's texture position was already the crest's rest point moved out along the normal, so at the root the surface and the tongue now sample the same texture moving at the same speed in the same direction. The pour-line streaks keep their own constant speed (a varying speed with a plain clock shears them without bound).
- **The flow map's clock is global** (`FLOW_PERIOD` 1 s) **and only the displacement scales with the speed.** The first version kept the lip's old form, a clock running at the speed (time × speed / cycle): wherever the speed varies across the surface the phase then wraps a different number of times per metre, and the stretch along a speed gradient grows with the clock. Twenty-five seconds into the first capture the near faces were ribbed with bands about half a metre apart, whole texture cycles crushed between wraps. With a global clock each copy slides by speed × its phase (−½ to ½ of a period) and the stretch is at most half a period × the gradient, whatever the time; the lip's own pour inherits the fix (its ribbing at the thirteenth pass may have been this).
- **The flow map blends the lace noise, not the lace** (`laceField`, `laceVeins`, 2026-10-25). The first version crossfaded two thresholded lace patterns by the triangle weight, so twice a second (the quarter points of the 1 s period) the foam on every flowing face was a 50/50 double exposure: both vein sets at half strength, and the thin foam's veins all but gone (a 50/50 mix of two independent lace fields has 0.70 x the standard deviation; at coverage 0.3 the vein share fell from 8% to 2%). It read as the foam fading between two textures. Now the two copies' noise values are mixed, rescaled about the lace mean (`LACE_MEAN` 0.47) by 1 / sqrt(w^2 + (1 - w)^2) to keep the variance, and thresholded once: one set of veins at every phase, morphing from one copy's to the other's. Offshore (no flow) nothing changed. Measured from a fixed camera behind the bar's bores (a foam-only debug view of the flow-mapped regions, 80 frames at 80 ms): the frame-to-frame change in the mid-grey share of the foam fell from 4.2 to 0.7 points (largest 10.6 to 2.4). `flowingPartials` still plain-mixes the ripple normals, which flattens them half-way through the period; the same 1 / sqrt(w^2 + (1 - w)^2) applies there if it shows.

**Measured.** Root continuity, from a debug fragment painting the flow speed red on surface and lip: at a near lip's root the surface reads 102–108 and the lip 105 (of 255, speed/6), rising to 158 along the tongue. Validation clean. Cost (`--wind 14 --bench 12 --surf-test`, `FLOW_CREST` 0 against 0.6 swapped into the bin's shader copy alternately, two runs each): surface 0.0942 / 0.0942 → 0.0963 / 0.0963 ms, the second lace and cascade taps on the pixels inside a face's window; frame 0.1827 / 0.1822 → 0.1829 / 0.1833, within the run-to-run noise; lips unchanged.

**In motion** (3 s at 100 ms, `--wind 20 --surf-cam side`, bursts 24 s and 70 s after launch): no banding at either time. From the shoulder at a breaking crest the lace streaks on the face run up toward the crest and continue into the white mass pouring over, and move that way from frame to frame, where before the lip was a ruled white curtain on a surface whose foam churned in place. Judged by the user next.

**Not yet:** the spiller's ragged crest. The FFT *displacement* still rides the rest grid while its normals stream, which may show on the largest ripples.

#### The fold as the lip, by default (twenty-second pass)

**The fine patch, built after the seventeenth pass and undocumented until now** (`ocean_patch.vert`, `OceanRenderer.PatchAlong` 8 × `PatchAcross` 28, `breakers.patchList`): one instance per crest segment within `PatchDistance`, an indexed grid of 8 × 28 quads over the tongue's rest strip and `PATCH_MARGIN` 0.6 m either side, each column seated on the carrier crest (`seatOnCrest`) and the rows spread across the strip's width (`foldWidthAt`), evaluated with the clipmap's own `oceanAt` with `gFoldTongue` set, so `breakerFace` draws the strip as the tongue (`foldFrom`, `lipPath`): the first half of the strip the outer skin from the crest top to the tip, the second the inner skin back to the root a body's thickness inside, 0.2 m between vertices on a 4 m tongue. Drawn after the clipmap with `ocean.frag`, pushed `PATCH_BIAS` 3 cm toward the camera where it overlaps the clipmap's wall. The clipmap never folds: it draws the plain wall under the tongue, the tube's back.

**Now the fold is the lip.** `OceanRenderer.FoldLip` defaults on (`--ribbon-lip` for the old ribbon, the Ocean panel's *Fold lip*); the ribbon draws only with the fold off (`ribbonInRange`), nowhere beyond the patch as it did, so the far panes go; lip bases and spray are unchanged (`lipInRange`). `PATCH_DISTANCE` 40 → 80 m, fading over the last `PATCH_FADE` 10 m; beyond, the face's whitewater marks the crest, where a 1 m tongue is a few pixels. The tongue's inner skin reports itself (`OceanPoint.fold`, `vFold`) and `ocean.frag` darkens it to `LIP_UNDERSIDE` as the ribbon's ceiling was. Since the fold shares the surface's parameterisation, normals, foam and shading there is no root seam, and no per-segment pane: the strip's shape varies with the carrier along the crest.

**Culled only as a whole instance.** The patch culled each column whose fold width was zero by sending its vertices to the clip volume's corner; where an instance had columns with a tongue and columns without, triangles ran from real vertices to that corner, slivers right across the scene (the first fold-by-default sheets were full of them, and the bench charged them to the surface and patch scopes). A column with no tongue now collapses its strip to zero width and draws the plain wall there; the instance culls uniformly, on range, the flags and the bow-tie test the ribbon has.

**First cost and look, and what they taught.** With the slivers fixed: ribbon frame 0.1831 / 0.1830, fold 0.2764 / 0.2753; patch scope 0.044 and the surface scope 0.0963 → 0.1413 while the patch's work overlapped the clipmap's tail. Every segment within 88 m drew a full 8 × 28 patch, tongue or not. And the tongues read as dark glossy hoses along the crests, for two reasons the crops showed: the fold's landing was a fixed `LIP_FOLD_DROP/THROW` × H that ignored the wall, so a spiller's tongue ended 0.5 H ahead and 0.6 H below the crest, in the air in front of a near-vertical wall, with both skins showing and the ceiling darkened; and the lace evens out to 0.4 × coverage where a pixel can't resolve its cells, which a tongue seen edge-on at 50 m never does, so the spill white came out grey.

**Now:**
- **The fold's throw, drop and crest point are the segment's lip bases'** (`gFoldThrow`, `gFoldDrop`, `gFoldCrest`, read in `ocean_patch.vert` from the two slots and mixed along the segment): the landing measured on the drawn surface and clamped to the wall, as the ribbon's was, so a spiller's tongue lies on the face and its inner skin is inside the water. A segment with an end holding no lip gets no fold, as it gets no ribbon.
- **A closed tube.** The outer skin runs from the bases' crest point out along the lip path to the tip; the inner skin, the tube's ceiling, runs from the tip back to the wall at each vertex's own rest distance (its unfolded position, which the evaluation already gave), and the strip spans the rest distance the landing was sampled at (`lipRestDistance`, a spiller's half of it) times the reach, so its last row meets the wall exactly where the landing was measured and the wall below closes the tube; a tongue that has landed is a sheet from the crest to the foot. Two things it replaces: the prototype unfolded each vertex's own displacement back to the crest through the local Jacobian and the carrier's cosine, which held for its 2 m strip but sent the inner skin's far rows off along the surface as streaks once the strip was sized to the path; and its inner skin returned to the crest a body's thickness inside the outer, which left a sheet from the crest to wherever the next unfolded row fell, the foot once the strip was wide, hung in front of the concave wall.
- **A tongue is at least as white as the fold says** (`ocean.frag`: foam ≥ `vFold.x` × the fold's coverage): tumbling whitewater on a spiller, the feathered edge on a plunger, at any distance.
- **Only breaking segments get a patch**: each end's breaking cap rides in `BreakerSegment.Slots.zw` and `WriteBreakers` lists a segment only below `PatchCap` 0.985 (the lip appears as the curl rises from cap 1). The grid is 4 × 20 (1 m along a 4 m segment, about 0.2 m across a 2 m tongue); without a tongue the rows collapse onto the crest and draw nothing.

**Measured** (Release, `--bench`, wind 20, surf cam; the GPU was shared with a browser that session, so the absolutes sit a third above the twentieth pass's 0.18 and only the ABAB pairs count): ribbon frame 0.2695 / 0.2748, fold 0.2809 / 0.2682, the same within the noise; surface scope ribbon 0.1464 / 0.1495, fold 0.1516 / 0.1413; the patch scope 0.0041 (was 0.044 when every segment in range drew a full 8 × 28 patch); the lips scope 0 with the fold on, 0.0020 with the ribbon. Lip bases and surf foam unchanged. The Debug build ran 25 s through the same scene with the validation layers on and reported nothing. `LipSeedTests` still 0 misses / 0 flips.

**Judged in motion** (30 frames at 100 ms, side cam, wind 20): no slivers, no streaks, no hanging sheets. The far tongues (30–80 m) read as white hooks with dark mouths that advance with the crest and keep their shape frame to frame, the look the ribbon never gave at that range. The near young spiller (under 20 m) is the problem that remains: a flat white shelf, straight-edged and opaque, sitting on the face with a grey rim where its inner skin shows. Three causes, all in the shape rather than the shading: the lip path's first stretch is flat (a spiller's tongue is a thin sheet lying on the face, so its strip is nearly a plane seen from the side); the foam floor makes the whole strip the same white, with no lace or gap for the eye to read thickness or motion; and the strip ends hard at a segment whose end holds no lip, a straight cut across the crest. Still to do: vary the tongue along the crest (ragged crest, cap and reach noise per column) and lace the floor so the shelf breaks up, and taper the strip at the ends.

**The flow on the strip.** Rest points pass through the strip as the crest advances, so the fold alone streams its texture *backward* through the lip at the wave speed. With `FLOW_CREST` 1.0 the heightfield's crest top is pinned (in rest space the crest moves at c, so a flow of c holds the lace on the crest, as breaking water moves with the wave), and on the strip the flow is the crest's own at the root and `FLOW_TONGUE` 0.6 × c faster by the tip on both skins (the inner skin runs back along the strip, so its pour is the other way in rest space): water pours forward over the lip, continuous with the back and the crest.

**Measured:** by the flow probe (`ocean_debugfold.frag` painting the flow speed) the strip's root matches the surface's crest flow within the 8-bit quantisation (102–108 of 255 on the surface, 105 at the root) and rises to 158 at the tip, FLOW_TONGUE's 0.6 × c; on the 100 ms sheets the lace on the back, the crest and the outer skin streams as one, with no seam at the root. On the shelf of a near spiller the motion is hard to see because the floor is uniformly white (see above).

#### The tongue ragged along the crest (twenty-third pass)

The near young spiller read as a flat white shelf: a straight-edged, opaque plane on the face with a grey rim. Three changes, all on the patch and the tongue's pixels only (the clipmap never folds):

- **Ragged along the crest** (`foldRaggedness`, `FOLD_RAG_*`). Each column of the strip varies its breaking cap by up to ±0.06 and shortens its reach by up to half (less on a plunger, whose lip is one sheet) with a value noise over the crest point's offset *across the carrier's travel*, the wave's own frame, so the pattern rides along with the crest instead of lying on the sea floor and sliding through it, drifting a quarter of a lobe a second so successive crests differ; lobes about 3 m, with a finer octave on the reach. The shifted cap moves each column's onset and die-off along the crest (`foldFrom` rescales the crest's curl by the shifted break over the unshifted one, so the fold's curl is what the column would have had with its own cap), and the fold now grows in over `FOLD_ONSET` 0.05 of curl above `LIP_MIN_CURL` instead of stepping to full width there. The strip width follows the reach, so the texture density is unchanged. Only `foldFrom` sees the noise: two value-noise taps per patch vertex, twice (the row placement and the evaluation). The patch is back to 8 columns per 4 m segment (`PatchAlong`, 0.5 m): at 1 m the lobes came out as a sawtooth, the tip line a polyline between columns.
- **The floor is lace** (`ocean.frag`). On a tongue the foam is the surface's own lace at 0.7 × the fold's coverage (as the surface's is, so water shows between the veins at a spiller's root and a fresh tip is a lumpy mass, never flat) instead of the flat coverage. It is drawn at the surface's density *along the skin*, not the strip's rest density: the strip is as wide in rest as the landing's rest distance, folded onto a path a fraction of that, so in rest the lace was squeezed past what a pixel resolves and the first try fell straight back to the flat floor. Each strip vertex carries a rest offset (`gFoldPour`, `vFold.zw`) that puts its lace at its arc length along the skin (the outer skin's path walked in four steps, the inner skin's chord from the tip) stretched by `FOLD_POUR_STRETCH` 0.4, zero at the root so the lace carries on from the surface's, and down the tongue it tears into strands along the pour rather than the net of veins the ribbon's pass found. Where a pixel still can't resolve it (the tongue edge-on at range, the far hooks that already read well) it blends back to the flat coverage, since the lace alone evens out to a grey there. `foamLaceAt` / `flowingLaceAt` take the footprint from the caller, measured outside the tongue branch where derivatives are defined.
- **Tapered ends** (`ocean_patch.vert`, `gFoldTaper`). A segment with a lip at one end only used to draw nothing, so its neighbour's full-width tongue ended in a straight cut across the crest. It now draws, with the lip end's throw and drop, its reach (and so its width) tapering to nothing at the lipless end over the segment. For that end's crest point the lip bases now evaluate every slot in range, writing an empty one with its crest point and the empty flag (`ocean_lip_base.comp`): the first try evaluated the column's own crest in the patch vertex instead, a second full evaluation per vertex on those segments, and since the patch draw is latency-bound (a few thousand vertices each walking every wave component) it stretched the patch scope from 0.001 to 0.036 ms; more warps in the one lip-base dispatch cost no latency. A segment with no lip at either end is culled with the rest.
- **Thrown lips** (`lipThrowLanding`, `BreakerLip.Sample`). Judged in motion, the plunging lips were barely leaving the wave: the landing is the drawn surface at the wall's foot, and `lipClampLanding` only ever pulls it *back* toward the crest, so under a pitched, near-vertical wall the tip landed against the face and the tube was a sliver. A plunging lip's tip now leaves the crest at `LIP_THROW_SPEED` 0.7 × the celerity and falls its drop under gravity, landing that far ahead (at most `LIP_THROWN_MAX` 2 × the drop) at the foot's height, however close the foot is; a spiller (plunge 0) still tumbles to the foot as measured. The path, the fold's strip, the spray and the CPU mirror all follow the landing.

**Measured** (Release `--bench`, wind 14, surf test, GPU unshared this time: the frame back at the twentieth pass's 0.19): before 0.1901 / 0.1892; ragged and laced at 4 columns 0.1909 / 0.1909; at 8 columns 0.1915 / 0.1914; thrown 0.1919 / 0.1909. Patch scope 0.0010 → 0.0041 (the lace on the tongue's pixels and twice the columns); lip bases 0.0266 → 0.0276 (every slot in range evaluated); surface 0.1004 → 0.0993 / 0.1004. The first try, with the crest point re-evaluated in the patch vertex, read patch 0.0358 and frame 0.2216 / 0.2214, which is what sent the crest point into the lip bases. The Debug build ran 25 s through the surf scene with the validation layers on and reported nothing; `BreakerLipTests`, `LipSeedTests` and `BreakerSetTests` pass (24), the lip tests covering the CPU mirror of the throw.

**Judged in motion** (30 frames at 100 ms, side cam, wind 20): the near spiller is a lumpy laced mass with rounded lobes along its front and water showing between the veins, no longer a straight-edged shelf; at 4 columns the lobes were a sawtooth. The far hooks keep their shape, laced rather than flat white where they resolve. The plunging lips arc out ahead of the crest with a dark mouth under them and read as tubes; before the throw they were slivers against the face. A lipless end tapers rather than cutting. Still to judge: the thrown plunger's outer skin is glass and shows mostly its dark underside from a low camera, so a lip reads as a dark flag; the near spiller is still large and very white.

#### The tongue lit through (twenty-fourth pass)

Thrown, a plunging lip read as a dark flag: a glassy outer skin showing the deep body colour, a ceiling darkened flat to `LIP_UNDERSIDE`, and a hard edge. The ribbon had a sun-through term, a body thickness and a feathered tip; the fold, drawn by the surface's own shader, had none. Now, on tongue pixels only (`ocean.frag`, `FOLD_*`):

- **Thickness and path position** ride with the vertex (`gFoldBody`, `OceanPoint.tongue`, `vTongue`): the ribbon's body, `LIP_THICKNESS` × the tongue's size out to `LIP_TAPER` of the path, thinning to the feathered edge, and the position along the path on either skin.
- **Lit through.** Light reaches the eye through the sheet, `exp(-FOLD_ABSORPTION × thickness)` of it, 6 per metre so the root lets a third through and the edge nearly all: sun toward the eye (the ribbon's fourth-power lobe, `FOLD_GLOW_SUN`) and sky from any angle (`FOLD_GLOW_SKY`), in the water's scatter colour, added in display space as the ribbon's was. The ceiling's darkening is scaled by what does not come through, so only the thick root of the tube is in shadow.
- **Feathered.** A plunging lip at full curl feathers into foam over the last `FOLD_FEATHER` half of its path (a fifth before), the lace strands along the pour as the twenty-third pass drew them, and its last stretch fades by `FOLD_TIP_FADE` between the strands: spray, not a cut edge.

**Measured** (Release `--bench`, wind 14, surf test): frame 0.1907 / 0.1901 against the twenty-third pass's 0.1919 / 0.1909, unchanged within the noise; patch 0.0041, lip bases 0.0276, both unchanged. The Debug build ran 25 s through the surf scene with the validation layers on and reported nothing.

**Judged in motion** (30 frames at 100 ms, side cam, wind 20): a thrown lip reads as water. Its thin part glows the scatter colour on both skins, its last stretch tears into white strands that fade between them, and the underside is grey rather than black except at the root, where the tube's mouth stays dark as it should. The plungers 20–40 m off keep a dark hook at the root with a lit, feathered edge; the far hooks the same. The near spiller is as the twenty-third pass left it.

#### The break on one clock (twenty-fifth pass)

Judged in motion: a wave grew, tipped over with a small lip, foamed, the tip retreated, the crest went to foam, and the foam band sat at one place; bigger seas gave no bigger tubes. `docs/breaker-review.md` §5 has the probe (`tests/Water.Tests/LipLifeProbe.cs`) and the diagnosis: the break clock ran the lip's whole life on travel from cap 0.97 while the face's curl and lift followed the cap, which on a 1:50 shelf falls 0.03 per 1.5 m, so the lip was thrown and the crest collapsed before the face had stood up; H = unbroken × cap shrank with the bed while the lip was in the air; the landed tongue faded back to the crest; and the surf test's 1.6 m bar was too shallow for a 20 m/s sea, whose waves broke on the shelf as spillers. Four changes, each mirrored on the CPU (`BreakerFace`, `BreakerLip`, `OceanSwell`) and the GPU (`ocean_common.glsl`):

- **One clock.** `BreakerSet.ClockStartCap` 0.97 → 0.92 (ramp 0.06): the clock starts when the wave is 8% past its breaking limit, the face ready. Everything then runs on the clock: the face stands up over the first `ONSET_TRAVEL` 0.15 λ (`breakOnset` drives the curl, the crest lift, the pitch's steepness and a spiller's whitewater, in place of the cap's `breaking` and `grow` ramps), and the lip's life and the collapse run on the travel past that (`breakTravel`, `breakLanded`: reach 1 by 0.175 λ more, landed by 0.245, the crest collapsed into the roller by `BREAK_CRASH` 0.35). The cap after the onset only says when the water has run out (`spent`). The same sequence on any bed.
- **The breaking height is held** (`heldCap`, `BreakerFace.HeldCap`): once the face is up, H is the cap the clock started from (at most `HOLD_GAMMA` 1.5 × the depth) until the lip has landed, then falls to the depth's own cap, so the tube is biggest while the lip is in the air and the wave decays after it lands. The drawn carrier under the face holds the same way (`swellShare`, `OceanSwell.SampleFields`, which now reads the break clocks through `OceanSwell.Breaking`, set by `OceanSurface`), its hold taken at the wave's own crest: the clock is carried per position, and a wave's crest and the trough ahead of it hold different values, so taken at the sample point, or blended across the wavelength, it pulled the drawn crest off the carrier crest (41% of lip roots on a flank); the crest behind's hold covers most of the wavelength and hands over to the crest ahead's around the trough (`holdBlend`), so only the trough's depth varies.
- **The peak's span is the carrier's.** With H held past γ h the carrier's bound harmonics put its crest 1 + s + s² over the norm above the mean, above the face's ±H / 2 peak reference, and the peak's linear continuation tripled the excess: a 2.7 m wave drew a 5 m crest. The peak now maps the carrier's own crest-to-trough span (`carrierSharpness`, `OceanSwell.Sharpness`), so the crest sits at the harmonic crest plus the lift (3.4 m for that wave) and the trough is unchanged.
- **The landed tongue stays** (`foldFrom`): no reach fade with the fall. The tube closes by the crest dropping onto the landing (the crest lift × (1 − landed), the wall easing with `broken`), the throw and drop re-measured each frame from the lip bases, so the tongue flattens into the roller's front, its foam the fall's.
- **The surf test's bar is sized to the sea** (`SurfTest`): the carrier's deep-water height (`OceanSwell.CarrierHeight`) shoaled by 1.1 over γ gives the depth it breaks in; the bar's crest sits at 0.55 of that (at least 1.2 m) and its seaward foot where the beach is 1.4 × that deep, found on the terrain, so the set's waves (0.3–1.7 × the carrier) break on the flank and plunge. Wind 14 gives the old bar (1.55 m crest in 3.9 m); wind 20 moves it 75 m out with a 2 m crest.

The probe on the bar flank (wind 14, 2.3 m wave): onset → full curl in 1.4 s with H held at 2.4 m → lip thrown, throw 2.8 m, drop 2.8 m → landed at 2.6 s → crest dropped onto it and the roller by 3.4 s, the tongue staying as its front. On the plunging beach's set waves (2.8 m): drawn face 3.4 m, the tongue spans 89% of it, 94% of roots on the crest, landed tips 1.08 H below the crest. Tests that measured landed tongues now skip crashed ones (they persist as the roller front) and, on the plunging beach, scan a whole set (64 s) of waves over 1.5 m, since lull waves of 1.2 m now start their clock 15 m from the shore; the crash-distance test takes snapshots over a set for the same reason.

**Measured** (Release `--bench`, wind 14, surf test, the bar now sized by the code to the same place): frame 0.1920 / 0.1963 against 0.1907 / 0.1901 before the pass, +0.002–0.006 ms: the surface 0.0993 → 0.1034 / 0.1014, the two break-clock reads per vertex in the coastal band (the carrier's held height at the crests behind and ahead) and the peak's sharpness; patch and lip bases unchanged. The Debug build ran 25 s through the surf scene with the validation layers on and reported nothing. All 52 water tests pass. The surf test logs its bar: wind 14 a 1.48 m carrier breaking in 2.1 m, crest 1.2 m, foot in 2.9 m; wind 20 a 2.01 m carrier breaking in 2.8 m, crest 1.56 m, foot in 4.0 m, 10 m further out.

**Judged in motion** (30 frames at 100 ms, side and front cams, wind 20 and 14): the sequence is the one asked for. The face stands up, the lip throws from a crest that keeps its height, the tube is tall and hollow while the lip is in the air, and the crest drops onto the landing so the tongue becomes the roller's front instead of retreating; bigger seas give bigger tubes. What it looks like, though, is not yet the reference: the peaked crest reads as a pyramid (the peak's cubic over the carrier's span, with the lift on top), and from the side the barrel is a dark angular notch, its ceiling a flat dark facet, the lip a straight-edged sheet; from the front the tube is a small dark hook at the apex. The shape of the tube is now the question: the lip path's curve and the wall's concavity, not the lifecycle.

#### The barrel's shape (twenty-sixth pass)

With the break on one clock the tube was tall and hollow but read as a pyramid crest with a dark angular notch: the lip path's control points (1.2 × the throw at −0.1 × the drop, then the landing) drew a flat ceiling and a corner; the inner skin was a straight chord from the tip to the wall's foot, a flat dark facet; and the crest lift was a 0.05 λ gaussian spike on a cubic peak. Three changes:

- **The lip path is a barrel arc** (`lipPath`, `BreakerLip.Path`): a plunging lip follows an ellipse from the crest top (leaving it rising 10°) round to the landing 115° past the top, so it goes out past the barrel's widest point and comes down to land with the tip pointing down and back into the tube; the cubic is fitted to the arc (handle 4/3 tan(E / 4)), its axes from the throw and the drop (a_x = throw / sin E, a_y = drop / (1 − cos E)). A spiller keeps its tumble down the face; the two are mixed by the breaker type as before. `LIP_OVERHANG` goes.
- **A thick lip with a rounded ceiling and a shadowed back** (`breakerFace`, `FOLD_CEILING`, `lipBodyThickness`). The inner skin is the tube's inside, a C: its first half of rows run from the tip back along the lip's underside, the path offset inward by the ribbon's body thickness (`LIP_THICKNESS` × the size at the root, thinning to nothing at the tip), to the root inside the wall under the crest; the second half run from that root down the back wall to the foot (each vertex's own unfolded wall point), coincident with the clipmap's wall and winning its depth by the patch's bias, so the wall under the lip is drawn as the tube's inside and darkened as such; the trough from the foot to the landing is the heightfield, the floor. A spiller's inside darkens less (`gFoldInner` 0.3 at plunge 0), being its face under the whitewater.
- **The peak softened** (`PEAK` 3 → 2.5, `FRONT_WIDTH` 0.05 → 0.09 λ, CPU `Peak`, `FrontWidth`): the crest lift's gaussian is twice as wide and the peak's cubic gentler, so the crest is a peaked wave, not a spike.

**Measured** (Release `--bench`, wind 14, surf test): frame 0.1982 / 0.1971 against 0.1920 / 0.1963 for the twenty-fifth pass, within that session's spread; patch 0.0041 → 0.0051 (the ceiling's second `lipPath` per inner vertex); surface and lip bases unchanged. The Debug build ran 25 s through the surf scene with the validation layers on and reported nothing. All 52 water tests pass.

**Judged in motion** (30 frames at 100 ms, side and front cams, wind 20 and 14): the barrel is round. From the side at 20–30 m the crest is a dome, not a pyramid, with a rounded tube inside it whose ceiling catches the light; up close the lip passes as a thick curl with the tube's inside showing as a curve; from the front the apex is a rounded hook with a dark mouth. What remains to judge: the lip body is thick and pale (the ribbon's `LIP_THICKNESS` on a 2.5 m wave is a metre of body, lit through), so the tube's inside reads grey rather than the dark turquoise of the reference, and the lace on it is coarse blotches at close range; the back wall under the ceiling is the pitched wall, straight, so the barrel is a D rather than an O.

#### The tube's inside (twenty-seventh pass)

The barrel was round but its inside read grey: the ceiling and back wall were shaded as water, with the sky in their Fresnel, then darkened flat, and a metre-thick lip body lit through to a pale glow; the surface's lace cells were blotches on a lip at close range. Shader-only:

- **A thinner lip** (`LIP_THICKNESS` 0.45 → 0.25, `LIP_TAPER` 0.45 → 0.3): a quarter of the body size at the root, thinning from a third of the way out.
- **The inside is the tube's** (`TUBE_*`, `ocean.frag`): the ceiling and the back wall are mixed toward a dark turquoise (`TUBE_COLOUR`, `TUBE_SHADE` of it in the lip's shadow plus `TUBE_THROUGH` × the light through the body where it is thin), tonemapped and fogged as `shadeWater` does, in place of the `LIP_UNDERSIDE` darkening; the glow through the sheet stays on the outer skin only, with `FOLD_ABSORPTION` 6 → 10 for the thinner body and the sky's share 0.5 → 0.3. A spiller's inside mixes 30%. The first try scaled the tube colour by the sun's intensity (3) before the tonemap, as the body colour is, and the inside came out pale mint; the colour is now set in linear space as seen.
- **Finer lace toward the tip** (`FOLD_LACE_FINE` 2.5): the lip's lace coordinate is scaled up to 2.5 × the surface's density by the tip, continuous at the root, the flow slide scaled with it; coverage 0.7 → 0.5 × the fold's, so veins with water between rather than soft white blotches.
- **Small waves** (asked mid-pass: their lips and crashes were too small to read). The whitewater where a throwing lip lands was gated on the crest lift (`smoothstep(0, 0.3, amplitude)`), a fifth of H, so waves under 1.5 m had little; it is gated on H itself now (`THROW_FOAM_HEIGHT` 0.6 m, CPU `ThrowFoamHeight`). The lip body is sized from at least `LIP_MIN_BODY` 0.6 m, and the crash sprays as a wave at least `SPRAY_MIN_HEIGHT` 0.8 m tall (`ocean_spray_spawn.comp`).

**Measured** (Release `--bench`, wind 14, surf test): frame 0.1962 / 0.1951 against 0.1982 / 0.1971 for the twenty-sixth pass; patch 0.0041; surface and lip bases unchanged. The Debug build ran 25 s through the surf scene with the validation layers on and reported nothing. All 52 water tests pass.

**Judged in motion** (30 frames at 100 ms, side and front cams, wind 20 and 14): the tube's inside is dark turquoise under a white lip, at 20–30 m a white curl with a dark mouth and up close a white curl over a dark teal hollow; the lip is thin enough to read as a lip. Small waves at wind 14 show white hooks and whitewater where they land. Still to judge: at close range the lip is a smooth white mass rather than lace, because the surface's own lace term is also evaluated on the tongue's squeezed rest coordinates, where it cannot resolve and falls back to a flat 0.4 × coverage under the tongue's lace; and the crest drops as soon as the lip lands (the hold and the lift both release over the second half of `BREAK_CRASH`, 0.175 λ, about 1.5 s), which reads as the wave top lowering when it tips over; a real crest holds its height until the lip has landed and decays over about a period after.

#### The crest held, the barrel an ellipse (twenty-eighth pass)

Judged in motion: the wave top lowered as soon as the lip tipped over, the lip left the crest flat and the tube's inside met the wall at a sharp angle, and up close the lip was a white mass. Asked: hold the crest until the lip has landed, then decay it over a period; drop the surface's lace under the lip; and (mid-pass) a steeper departure and a curved inside.

- **The crest holds until the lip lands** (`breakCollapse`, `BreakerFace.Collapse`): the held height, the crest lift and the wall's steepness now ease away over `COLLAPSE_TRAVEL` 1 λ of travel (about a period) from `LANDED_TRAVEL` 0.245 λ past the onset, the point at which the lip has landed and crashed, instead of over the second half of `BREAK_CRASH` (0.175 λ, 1.5 s) from the moment the lip reached the foot. The whitewater (`broken`, the roller) still comes with the landing. The hold itself now comes on over `HOLD_RAMP` 0.03 λ from the clock's start rather than with the 0.15 λ onset, since ramping it with the onset let H dip as the cap fell under it on a steep bed (1.9 → 1.65 → 2.4 m on the bar flank).
- **No surface lace under the lip** (`ocean.frag`): the surface's surf and whitecap lace terms are scaled by 1 − `vFold.x`, since on the tongue's squeezed rest coordinates they could not resolve and fell back to a flat 0.4 × coverage, a white mass under the tongue's own lace.
- **The barrel is the ellipse itself** (`lipPath`, `BreakerLip.Path`): a plunging lip's path is the ellipse evaluated by angle, linear in t from `LIP_ARC_START` 35° before the top (so the lip leaves the crest rising 35°; the Bezier fitted from the top read as coming out flat) to `LIP_ARC_END` 115° past it, its axes from the throw and the drop; one cubic could not fit 150° of arc. The spiller keeps its cubic; the two are mixed by the breaker type.
- **A fillet inside** (`breakerFace`): the back rows run from the ceiling's root to the foot through a quadratic that carries the ceiling's tangent on past the root for a body thickness before turning down the wall, so the inside is one curve from the tip to the foot rather than a ceiling meeting a wall at a corner; the bend lies inside the wall, and with the steeper departure the ceiling meets the wall at a more open angle.

**Measured** (Release `--bench`, wind 14, surf test): frame 0.1939 / 0.1933 against 0.1962 / 0.1951 for the twenty-seventh pass; patch 0.0020 (the surface's two lace evaluations gone from tongue pixels). The Debug build ran 25 s through the surf scene with the validation layers on and reported nothing. All 52 water tests pass. The probe on the bar flank (wind 14, a 2.3 m wave): H climbs 2.2 → 2.42 m through the stand-up with no dip and eases 2.4 → 1.6 m over 2.8 s after the landing, the drop from 3.0 to 1.4 m with it, against 2.4 → 1.0 m in 1.6 s before.

**Judged in motion** (30 frames at 100 ms, side and front cams, wind 20 and 14): the crest keeps its height as the lip tips over and the tube keeps its size until the lip has landed, then eases down with the whitewater on it; the lip leaves the crest climbing and arcs over; the inside is one dark turquoise curve from the tip to the foot. Two things the pass brought out: once a plunging lip has landed its whole tongue is foam (the fold's foam takes the crash everywhere along the path), and now that the landed tongue persists under a held crest for a period it reads as a white sail on the crest, where the reference keeps the lip's body glassy and whitens from the impact back; and up close the lip is still a smooth white rather than lace, since between the veins the outer skin shows the sky in its Fresnel at the grazing angles a low camera sees it at, which is as pale as the veins.

#### The lip's body and the crash front (twenty-ninth pass)

Judged in motion: a landed lip was a white sail on the held crest, the lip read as a smooth white up close, and some waves could not decide whether they were crashing. Three changes:

- **The crash whitens from the tip back** (`breakerFace`, `FOLD_CRASH_TIP`): the fold's foam took the crash everywhere along the path at once. Now a front runs from the tip toward the root: `FOLD_CRASH_TIP` 0.35 of the path as the lip lands (its fall), the rest as the crest collapses (`breakCollapse`), so a landed lip keeps a glassy body and whitens from the impact over the period it persists.
- **The lip's body between its lace** (`LIP_*`, `ocean.frag`): the outer skin was shaded as water, and at the grazing angles a low camera sees a lip at its Fresnel showed the sky, as pale as the veins. Between the veins it is now mixed `LIP_BODY_MIX` 0.75 toward a thin sheet lit from behind: a bright turquoise (`LIP_COLOUR`), brighter where the body is thin and toward the sun, tonemapped and fogged as `shadeWater` does; the rest keeps the water's gloss. The sheet-glow terms (`FOLD_GLOW_*`) go.
- **The break clock has hysteresis** (`BreakerSet.CarryBreakDistance`, asked mid-pass): the clock started and stopped at one cap, so a wave hovering at its limit, its height moving with the set envelope, kept starting its break and letting it decay. Once it has run `ClockStartedDistance` 1 m it keeps running until the wave is under its breaking limit by `ClockStopCap` (the cap, unclamped, at 1.1: H a tenth under γ h), with the same ramp; then it decays over `ReformDistance` as before.

**Measured** (Release `--bench`, wind 14, surf test): frame 0.1932 / 0.1928 against 0.1939 / 0.1933; patch 0.0020; lip bases unchanged (the hysteresis is one compare per surf cell on the CPU tick). The Debug build ran 25 s through the surf scene with the validation layers on and reported nothing. All 52 water tests pass; `LipSeedTests` still 0 misses / 0 flips. The probe at wind 20 on the shelf: a 3.1 m set wave's clock runs 0.9 → 27 m without a reset while its cap wanders 0.89 → 0.54, H held at 2.9–3.0 m.

**Judged in motion** (30 frames at 100 ms, side and front cams, wind 20 and 14): the lip is a translucent turquoise sheet over a dark hollow, white only at its thrown edge, and after it lands the whitening runs back from the impact over the collapse rather than taking the whole tongue at once; the sail is gone. Up close the body is a pale sea-green, lighter where it thins toward the tip, with the gaps between veins no longer sky-white. No wave on the sheets starts and abandons a break. Still to judge: the lip's lace is sparse on a 2.5 m wave (coverage 0.5 × the fold's, and the fold's is low on a glassy plunger until the feather), so the sheet reads clean where the reference shows strands running down it; and the body's colour is flat along the crest, with no lighter streaks where it thins.

#### Strands down the lip (thirtieth pass)

The lip read as a clean translucent sheet: its lace was sparse on a glassy plunger and its body colour flat along the crest. Shader-only (`ocean.frag`, `STRAND_*`): water pouring over the lip runs down it in strands. A value noise on the lip's rest coordinates, `STRAND_ACROSS` 1.8 per metre across the pour and stretched along it (the fold's own u as the along coordinate, so the strands run the path whatever the crest's bend), slid with the flow on the same two-phase clock as the lace, ridged so it is high on the strands and low in the thin sheet between. It grooves the outer skin's normal (`STRAND_SLOPE`, more toward the tip, as the ribbon's pour lines did), thins the body between the strands by up to `STRAND_THIN` 0.6 so the light comes through in lighter streaks, and raises the lip's lace coverage by up to `STRAND_FOAM` 0.5 × the fold's on the strands so the foam gathers along them. The inside is left alone.

**Measured** (Release `--bench`, wind 14, surf test): frame 0.1937 / 0.1930 against 0.1932 / 0.1928; patch 0.0020 → 0.0031 (four value-noise taps per tongue pixel); surface and lip bases unchanged. The Debug build ran 25 s through the surf scene with the validation layers on and reported nothing.

**Judged in motion** (30 frames at 100 ms, side and front cams, wind 20 and 14): the lip's foam runs in strands along the pour, torn into streaks that slide with the water, with lighter bands between them where the body thins; the body's colour varies along the crest and down the sheet rather than lying flat. At 20–30 m the lip is a translucent curl with white strands over a dark mouth; from the front a rounded hook with streaks down its face. Up close the strands read, though the sheet between them is still a smooth pale green and the lace veins on it soft-edged.

#### The tube's shadow bounded (thirty-first pass)

Seen from above: the tube's dark inside was painted over the whole face of every breaking wave, from the crest to the wall's foot, and beyond the lip's size. The inner skin's back rows run down the wall to the foot at the landing's rest distance (0.19 λ, a spiller's half of it), and every one of them was mixed to `TUBE_COLOUR` by `vFold.y`, whatever the lip was doing: on a spiller (30%), and through the collapse, when the wall's steepness eases (`faceDrive`) and the pitch's squeeze relaxes, so the rest span draws out to metres of gently sloping face, all dark, its foot edge a sawtooth along the crest from the raggedness. Shader-only:

- **The shade follows the enclosed tube** (`gFoldBody.z`, `vTongue.z`, `TUBE_MOUTH`, `TUBE_BACK_LIT`): the inside is darkened by `plunge × reach × (1 − collapse)`, so a spiller has no shadow, a lip casts one as it reaches, and the shadow goes as the crest's collapse closes the tube; on the ceiling it is full at the root and `TUBE_MOUTH` 0.4 at the tip, where the mouth lights it; down the back wall it is full for the first `TUBE_BACK_LIT` 0.3 below the root and fades to nothing at the foot, so the lower wall and the floor are lit from the mouth and the foot edge is not drawn. `gFoldInner` is back to a flag (1 on any inner row) for the body mix.
- The inside's rows take the surface's lace coverage (0.7 × the fold's) rather than the lip's 0.5, so the back wall meets the clipmap at the foot without a seam.

**Measured** (Release `--bench`, wind 14, surf test): frame 0.1935 / 0.1929 against 0.1937 / 0.1930; patch 0.0031. The Debug build ran 25 s through the surf scene with the validation layers on and reported nothing. Shader-only, no CPU mirror.

**Judged in motion** (30 frames at 100 ms from the side and front cams at wind 20 and 14, and 60 frames at 500 ms from a camera 25–30 m up over the bar looking back at the faces): from above, no dark zone anywhere over 30 s at either wind. The spillers and collapsed waves pass as white rollers on turquoise; the shade is gone from them. From the side the tubes keep their dark mouth under the lip and the lower wall is lit toward the foot. From 16 m up over the bar top looking back at the flank, a thrown lip shows as a white curl with a dark band under it the lip's own size, the face below it lit, and the landed waves behind it as plain rollers.

#### The slab after the landing, and lace that resolves (thirty-second pass)

Seen from above and behind a crest (the user's screenshot): the lip a flat, uniformly white band along the whole crest, straight-edged, like a shelf on the wave; and on small waves the tongue flickered and feathered along its length. Debug-coloured captures (u along the path, inner skin, fold foam) from a camera 7 m up looking along the wind 14 bar's crests showed the band was the outer skin, u 0 → 1 across it, foam saturated, persisting 6 s and more after the landing; and thin hard white lines along the shoulders where the tongue was a sliver. Four causes:

- **The landed tongue persisted as a thrown arc through the whole collapse** (`foldFrom`: the twenty-fifth pass's "landed tongue stays", with `COLLAPSE_TRAVEL` 1 λ, about a period) with `LIP_THROWN_MAX` 2 × its drop: the ellipse's roof from 35° before its top to 60° past it spans 1.44 a_x horizontally within 0.5 a_y of height, and with a_x / a_y = 0.84 × throw / drop = 1.7 that roof was a shelf twice as wide as the drop, white once the crash front had run back along it, sitting there for a period. Now a landed tongue **closes** over `LIP_CLOSE_TRAVEL` 0.3 λ of travel past the landing (`lipClose`, `BreakerFace.Close`): on the strip each row sinks to its own unfolded rest point, the plain wall the clipmap draws (`breakerFace`, position, slopes, Jacobian and flow all mixed by the close), so by the end the strip is that wall and nothing else; the lip bases' throw shortens with it (`ocean_lip_base.comp`, the thrown landing × (1 − close), mirrored in `BreakerLip.Sample`), so the spray's landing comes back to the foot; the tube's shadow and the fold's own look (`gFoldWeight`) fade with it, leaving the surface's lace at the crash's coverage on the wall as the roller's front; the crash front (`whiteFrom`) runs back over the close rather than the collapse, and fades down the inside's last rows to the wall's own foam at the foot so the strip's far edge never cuts. A first try morphed the arc into the spiller's cubic instead: a white sheet lying on the face with a sawtooth lower edge (the raggedness) and slivers where the inside's rows met it. `LIP_THROWN_MAX` 2 → 1.5: a_x / a_y 1.26, a square barrel rather than a flat roof.
- **The tongue's lace never resolved away from the origin** (`ocean.frag`): the lace position was `(vRestXZ − vFold.zw) × fine`, `fine` rising 1 → 2.5 along the skin, so hundreds of metres of world coordinate were scaled by a factor that varied across the tongue, the position's derivative was metres a pixel, and `far` fell the lace back to its flat coverage: every tongue away from the origin was flat white, which is what the band, the near spillers of earlier passes and the "smooth white mass up close" were. The density is now applied in the vertex (`gFoldPour`) to the arc length alone.
- **A young tongue drew as a hard line** along the shoulder: `foldFoam` ≥ 0.55 from birth on a spiller while the strip was centimetres wide. The fold's look (foam, body, `gFoldWeight`) fades in over `FOLD_GROW` 0.3 m of path.
- **The landing was measured on the full drawn surface**, chop included: on a 1 m wave the random sea's short components were a third of the drop, and the throw (≤ 1.5 × the drop) and drop the tongue is built from shimmied with them frame to frame (small lips less "confident" than big ones, as the user put it). The landing's swell sum now runs at `LIP_LANDING_SPACING` 2 m, which fades components under 4–8 m (`swellShare`); the root keeps `LIP_BASE_SPACING` 0.25 so it sits on the drawn crest.

**Measured** (debug frames painting throw and drop, 50 at 100 ms from the fixed wind 14 camera, per-frame medians over the near tongue's root-side pixels, steps between frames of the same tongue): before, throw steps 4.1% of the median on average (max 15%), drop 5.0% (max 14%); after the landing filter 4.2% / 4.4% (max 15% / 23%). The metric is coarse (a median over a tongue whose extent changes as it closes), so this pass does not claim the shimmy is gone; the captures below are the judgement. The CPU mirror is `BreakerLip.Sample`'s throw and `BreakerFace.Close`; the Release build is clean (0 errors, the 10 warnings pre-existing) and all 52 water tests pass (28 s), the landed-tongue tests unaffected since the close begins only once the crash is complete.

**Judged in motion** (80 frames at 150 ms from the fixed camera 7 m up looking along the wind 14 bar's crests, `--cam -322 7 -42 270 -20`; 40 from `--surf-cam side` at wind 20; 60 from 9 m up over the wind 20 bar, `--cam -336 9 -45 270 -20`): the near crest that was the white band is a laced whitewater front for the whole 12 s, the lace with holes of water in it, no shelf, no straight edge, no sheet lying on the face; the far crests' lips are translucent curls with lace on them and dark mouths, where the earlier captures had flat white hooks. On the small crests the thin white line down the shoulder is gone: the lip is a small curl at the peak that grows in over about a second rather than a line that pops. From the side at wind 20 the lip is a glassy curl with foam at its crest; from above the wind 20 bar the landed waves are laced rollers, with no white block anywhere. The first try (the cubic morph) is recorded above for what it showed: a white sheet on the face with a sawtooth edge, and striped slivers near the camera where the inside's rows met it. Still to judge, by the user in game: the small-wave shimmy the landing filter was aimed at (the metric could not separate it from the tongue's own motion), and the tighter barrel from `LIP_THROWN_MAX` 1.5.

#### The shoreline sloshes with the bores, and a bore is a front (thirty-third pass)

**What was wrong** (the user: "the shoreline itself is quite unrealistic, I would like the water edge to slosh in and out depending on the waves"). Two things, sharing the shore:

- **The whole beach strip lit up with foam the moment a bore arrived.** The face's whitewater (`roller`, `spilling`, `throwing` in `breakerFace`) used the face geometry's Gaussian, `BACK_WIDTH` 0.18 × the local wavelength, which on a bore in 1–2 m of water is still 20–30 m: a 10–20 m white band on a wave under a metre tall. As it reached the beach the band covered the strip to the waterline at once, `ocean_surf_foam.comp` stamped that maximum into every 2 m cell, and the shore lit up and faded as one (measured from `--wind 14 --surf-test --cam -222 5 10 20 -25` with a channel view, R = `vBreakerFoam`, G = `vSurfFoam`: face whitewater > 0.15 on up to 77% of the visible water, the surf foam map > 0.15 on 100% of it for about 3 s). The roller also ran on up the sand: nothing faded it with the water, so the foam map was stamped on the beach itself.
- **The swash was a metronome.** `swashLevel` raised the waterline above every beach point by one half sine per carrier period, its height `SWASH_RUNUP` 0.45 × the shoaled height capped by an edge-speed limit (`SWASH_SPEED` 3 m/s × slope × 0.4 period) that on the 1:35 beach was the binding one, 0.19 m for every wave; so the edge was a straight line moving on a fixed rhythm with the same run-up whatever arrived, whether or not a bore had.

**Now:**

- **Whitewater in metres, from the wave's height** (`WHITE_*` in `ocean_common.glsl`, mirrored in `BreakerFace.Apply`): the three whitewater terms share one envelope whose front width ahead of the crest is `WHITE_FRONT` 1.5 × H, between `WHITE_FRONT_MIN` 0.8 m and `WHITE_FRONT_MAX` 4 m, `WHITE_BACK` 1.6 × that behind the crest, and the roller's trail e-folds over `ROLLER_TAIL` 2 × H (at least 1.5 m) instead of 0.08 wavelengths. At the bar (H 2–3 m, λ 40 m) that is the width the face already had (4 m front, 6 m back against 7 m), so the judged breaking look is unchanged; on a bore in the last metre of water it is a 1–2 m front with a few metres of wake. The whole weight fades out over the last `WHITE_DRY_DEPTH` 0.15 m of water, so nothing is stamped on the sand: at the waterline the swash's own front carries the foam on. The face geometry (lift, peak, pitch) keeps its wavelength-scaled width, so `OceanSurface` is unchanged apart from its `Whitewater`.
- **Swash as run-up events per shoreline point** (`swashSheet`, replacing `swashLevel`; GPU only as before). For a beach point, the shoreline below it is found along the depth gradient as before and the carrier's phase there says how many periods ago each of the last `SWASH_EVENTS` 4 crests reached it. Each is a bore that collapsed there into a sheet whose edge runs up the beach ballistically (Shen and Meyer): x = u0 t − a t² / 2 along the beach with a = g tan β × (1 + `SWASH_FRICTION` 0.3) on the way up, then back down at `SWASH_BACK` 0.5 × g tan β, so it drains slower than it rushed up and stands at the top. u0 = `SWASH_U0` 1.0 × √(g H), H that bore's energy height: the carrier's shoaled height at the shore for the crest that arrived (the set envelope travels with the crests, so the crest `since` periods ago carried `groupEnvelope(spatial + 2π since)`), less `exp(−breakDistance / SWASH_DISSIPATE 100 m)` for how long it has been breaking where it hands over (`SWASH_SEA` 0.5 m deep): a wave that broke on the bar arrives with less than one that broke at the beach. The run-up is capped at `SWASH_MAX` 2 m vertical as before, and the oldest event fades over its last period so nothing pops. The surface above the point is the highest of the four sheets, so a bore arriving during a backwash pushes the edge out again rather than one half sine a period.
- **The sheet is a lens.** Behind an edge the sheet is the Shen–Meyer lens (x_edge − x)² / (9 g t²), thick just behind a fresh edge (t at least `SWASH_LENS_TIME` 1.5 s) and thinning as it ages, over a `SWASH_SHEET` 0.25 × (edge height above the point) floor and a `SWASH_MIN_SHEET` 3 cm film, capped at `SWASH_LENS_MAX` 0.25 m. The first try had the lens alone: a film a centimetre or two thick far behind the edge was cut into blocks by the voxel terrain's facets (the mesh and the depth map disagree by centimetres), which is what the old 0.25 × (level − ground) term had been hiding; the floor is kept for that. Past its edge each sheet dives under the sand at twice the beach slope, so the edge still falls between vertices.
- **Swash foam is a line at the up-rushing edge** (`ocean_surf_foam.comp`): `SWASH_FOAM` 0.6 × `front`, which `swashSheet` reports as 1 at the edge of an up-rushing sheet falling to 0 `SWASH_FOAM_FRONT` 2 m behind it, scaled by the bore (full from `SWASH_FOAM_U0` 3 m/s of edge speed), on the beach only; the foam map's 2.5 s decay leaves it as a film behind the edge. It used to seed 0.35 over every wet cell under the swash during the up-rush, the underwater strip included.
- Removed: `SWASH_RUNUP`, `SWASH_UP`, `SWASH_SPEED`, the old `SWASH_SHEET` sheet term.

**Measured** (channel view, same camera, 60 frames at 100 ms lined up with the before run; the sim is deterministic from launch): face whitewater > 0.15 on at most 51% of the visible water (77% before) and, in the near water, a front a metre or two wide instead of the band; the surf foam map > 0.15 on 65–95% (100% before, a flash that faded as one): what is left is the trail the front leaves, about 10 m at a bore's 2–3 m/s over the map's 4 s fall below 0.15, so a bore now reads as a white front with a wake rather than the strip lighting up. From a plan camera 45 m up (`--cam -240 45 10 0 -60`, 120 frames at 250 ms) the sheet's area on the beach, which used to swing 0.6% → 18% of the strip once per period everywhere at once, now rises and falls per bore by different amounts, and the edge is a wavy line, further up where the bores are strong (off the bar's end) and with a tongue where one ran up. From the shore camera (`--cam -222 5 10 20 -25`, 30 s): successive run-ups covered 8.3%, 6.2%, 3.6%, 2.2% and 3.6% of the near beach, each draining over 5–7 s with the next bore pushing the edge out again. Nothing snaps tick to tick: every input is a per-frame uniform or a bilinear map read; the run-up events hand over continuously as the carrier phase wraps.

Cost (`--wind 14 --bench 12 --surf-test`, two runs each, before → after): ocean 0.1495 / 0.1485 → 0.1495 / 0.1485 ms; surface 0.1116 / 0.1116 → 0.1085 / 0.1096 (the wavelength-scaled envelope and the old swash's sine go, the four-event loop comes); surf foam 0.0092 / 0.0092 → 0.0113 / 0.0113 (the beach cells now run the four events, at tick rate); patch 0.0307 / 0.0307 → 0.0338 / 0.0328; frame, layer B on minus off, 0.1194 / 0.1183 → 0.1203 / 0.1198. The frame's off side moved 0.1123 → 0.1064 between the runs: another session changed the FFT shaders (`ocean_h0.comp`, `ocean_fft.comp`) in between, so the frame totals are not a clean A/B; the ocean scopes above are.

**CPU:** `BreakerFace.Apply` mirrors the whitewater envelope; a new test, `Whitewater_IsContinuousTickToTick_AndNarrowNearTheBeach`, probes the last 48 m before the probe beach's shoreline for 900 ticks and asserts no point's whitewater jumps more than 0.2 in a tick and no row is white (> 0.15) for more than 14 m at once. Not built or run in this pass (no build was asked for).

**Not yet:** the sheet is still GPU only, so physics feels nothing of the run-up; the bore from 0.5 m of depth to the shoreline is the carrier's small depth-limited wave with the white front on it, not a drawn step; the swash does not yet take the lips' along-crest taper or peel, only the break distance; and the up-rush of a bore arriving over a standing sheet is hidden inside that sheet until it overtakes it (its foam line shows through).

#### See-through shallows and a visible edge (thirty-fourth pass)

The user, on the thirty-third pass in game: better, but the water's edge hardly seemed to move, the edge was hard to see, and the depth of the water could not be read; a transparency of some kind was asked for. Three causes, all in the shaders:

- **Open water was opaque by 6 cm** (`ocean.frag`'s alpha), so a swash sheet and the shallows showed the body colour's flat `SAND_ALBEDO` approximation of the bed, lit without the terrain's normals or its own absorption, and a 3 cm film looked like the dry sand beside it: no edge, no depth.
- **The edge line was 4 cm of thickness**, half a metre of beach on the swash's dive, drawn at 0.6 coverage: a hairline.
- **The run-up events were long and small.** On the 1:35 beach the Shen–Meyer deceleration g tan β × 1.3 is 0.37 m/s², so a 3 m/s edge ran up for 8 s and drained for 11 more: successive bores overlapped into a slow envelope and the edge moved a few metres per wave.

**Now:**

- **Thin water is see-through** (`ocean.frag`, `SHEET_OPAQUE` 0.6 m). The share of a pixel that is neither the sky's Fresnel reflection, foam nor the water body (which builds up over the first 0.6 m of thickness) shows the terrain behind, darkened to `WET_SAND` as wet sand is. The pipeline blends straight alpha, so that share is left out of the colour and taken off the alpha (α = 1 − through × 0.7, colour × (1 − through) / α): a film composites as the terrain at 0.7 under the sky's reflection; the body colour and its sand-through-water approximation take over by 0.6 m, where the two sands meet. Opaque again at distance (× e^(−dist × fog)), on the fold, and in zones, which keep their own fade.
- **The edge is marked in metres** (`OceanPoint.swashEdge`, `vSwashEdge`, `SWASH_EDGE_WIDTH` 0.5 m, `SWASH_EDGE_FOAM` 0.7): `swashSheet` reports how far behind the edge of the highest sheet a point lies, `oceanFinish` passes it where that sheet is the drawn surface, and the fragment lays lace at 0.7 coverage over the last half metre of every sheet, up-rush or backwash, on top of the thickness line.
- **Shorter, bigger events** (`SWASH_U0` 1.0 → 1.2, `SWASH_DECEL_MIN` 0.6 m/s², `SWASH_BACK` 0.5 → 0.7 of the up-rush's deceleration): the up-rush decelerates at least 0.6 m/s² (bed friction on the thin tip; steep beaches are above it anyway), so on the 1:35 beach a 3–4.5 m/s edge runs 7–17 m up in 5–7 s and drains in 6–8 s, each bore its own excursion of about two periods instead of a 20 s overlap.

**Measured** (`--wind 14 --bench 12 --surf-test`, two runs each, against the thirty-third pass): ocean 0.1495 / 0.1485 → 0.1495 / 0.1485 ms; surface 0.1085 / 0.1096 → 0.1157 / 0.1147 (the fragment's Fresnel, distance and blend on the shallows; a branch skips it over `SHEET_OPAQUE`, worth 0.001); patch 0.0338 / 0.0328 → 0.0287 / 0.0287; surf foam 0.0113 → 0.0102; frame 0.2267 / 0.2267 → 0.2250 / 0.2241. Against the day's baseline (0.2317 / 0.2311) the frame is 0.007 ms shorter and the ocean scope equal, with the FFT session's changes in between.

**Judged in motion** (30 s at 250 ms from the shore camera `--cam -222 5 10 20 -25`, 15 s from a low camera along the beach `--cam -224 2.2 -20 75 -8`, and the default scene at wind 14 from the shore camera; shaded, same frames as the thirty-third pass): the sheet on the beach is a grey wet band with a crisp edge against the dry sand where before it was a pale wash the colour of the sky, the sand shows through the shallows and gives way to turquoise as the water deepens, each bore's up-rush runs up the beach as a foam line over the wet sheet and the edge recedes between bores, and a bore arriving is a turquoise face with a white laced front. Next, by the user in game: whether the wet band's edge reads as the water's edge at their camera heights, and the run-up's size on their beaches.

**Not yet:** the wet sand dries the instant the sheet leaves it (a wetness map that the terrain shader reads would hold the dark band for tens of seconds, which is the strongest edge cue on a real beach; it needs a terrain descriptor, so C#); the terrain seen through the water is the voxel mesh's flat-shaded facets; the lip shader (`ocean_lip.frag`) keeps its own opacity.

#### A clean waterline (thirty-fifth pass)

The user, in game: dark translucent shards over the shallows and a jagged, toothed edge on the beach. Three causes:

- **The wall folded the swash.** The pitch (`lean = pitch × front × amount`) never faded as the water ran out, and with the height gone (H → 0) its clamp gave the strongest squeeze, 0.94 of a front 0.2 wavelengths wide: every bore reaching the beach drew the vertices ahead of it back by 1–1.5 m, over sand where the swash sheet (a rest-frame function) is the surface and the crest search steps between texels (the last `ShorelineDepth` 0.6 m the face tests already leave out). The mesh folded; in see-through water each layer composites the terrain at `WET_SAND` again, so two or three layers were the dark shards. A debug frag drawing back faces red showed them on every bore's arrival; `--no-faces` removed them.
- **The swash edge stepped.** `swashSheet` took the beach slope from central differences ±2 m apart, one depth cell, and the cell-to-cell slope on the voxel beach varies by about a third. The edge sits where the run-up (independent of the slope while the deceleration floor `SWASH_DECEL_MIN` binds) meets `s0 = −d / slope`, so the edge jumped by metres per cell: a staircase along each up-rush, the teeth with lace at their tips.
- **The edge was a hard pixel step.** Open water's alpha is `alphaOpen` in the shallows, so the 6 cm fade only set the discard and the waterline aliased and crawled.

**Now:**

- **The wall draws back only water that is there** (`PITCH_DRY_DEPTH` 0.6 m, mirrored in `BreakerFace.Apply`): the lean fades by the depth under each point, `smoothstep(0, 0.6, depthHere)`. Toward the shore that fade only stretches the face (tent ≤ 0), so it cannot fold it, and it is 1 with zero slope from 0.6 m, so the face partials and every face test outside the last 0.6 m are unchanged. The bore lift and the swash carry the last of the water as before.
- **The beach slope is the foreshore's** (`SWASH_SLOPE_SPAN` 6 m either side): same four taps, a 12 m span.
- **The waterline is anti-aliased**: open water's alpha is scaled by the share of the pixel it covers, `clamp(thickness / fwidth(thickness) + 0.5, 0, 1)`.

**Measured** (`--surf-test --cam -224 7 0 0 -55`, 30 frames at 300 ms, back faces counted as red pixels): while a bore reaches the edge (frames 7–20) 14.3k / 13.6k px (two runs) → 60–80 px. A bore in the foreground in 0.6–1 m of water folds about 30k px in both, unchanged: opaque water there. Cost (`--wind 14 --bench 12 --surf-test`, ABAB): frame 0.2337 / 0.2338 → 0.2335 / 0.2330 ms, surface 0.1178 / 0.1178 → 0.1178 / 0.1167, surf foam 0.0113 → 0.0113.

**Judged in motion** (same cam, and the default scene with `--rain 0.9`; the two builds' frames line up): the shards over the shelf go, each up-rush keeps a smooth edge with its lace, and the edge no longer crawls. A fade to 1 m removed the last slivers too, but weakens bore walls over 0.4–1 m and puts a slope the analytic partials leave out into the band `FacePartials_MatchFiniteDifferences` checks.

**Not yet:** small slivers remain on bores in 0.4–0.8 m of water (a row of dashes along the crest for about a second, from the crest search stepping between texels); the opaque foreground folds; the sheet's edge still scallops slightly between vertices on the coarser clipmap levels.

### Handing off to the zone sim (layer C)

- When a vertex goes from Plunging to Bore, the breaker **injects mass and momentum** into the zone sim:
  - a volume about H × crest spacing × H/2;
  - with the speed of a breaking wave front, about √(g (h + H)), shoreward;
  - plus a foam burst (`ZoneFlowSim` foam injection).
- From there the existing pipe sim does the broken white water, the wash up the beach and the backwash. No second water model.
- Inside a zone, the open-sea boundary keeps feeding the swell as it does now. The breaker only adds the energy the heightfield cannot express. This avoids counting the wave twice:
  - The breaker owns the face and lip until the bore.
  - The zone sim owns the water after it.
- Breakers spawn wherever kh < 1, zone or not. Outside zones (distant beaches), the bore stage is visual only: foam and the heightfield's own run-up.

## Rendering: merging the breaker into the ocean

1. **Face (heightfield):**
   - Per tick, the breaker crest data is uploaded into the persistent UBO/SSBO, as a small array of crest segments.
   - The ocean vertex shader adds a **breaker displacement** near each crest:
     - forward horizontal displacement that grows with curl, which steepens the face to near-vertical;
     - plus vertical lift, so the face reaches the full breaker height.
   - The face therefore stays the ocean mesh, with no seam, and keeps the FFT detail and foam.
   - The clipmap is too coarse for this. A local finer patch, or an extra clipmap level, is needed around active breakers.
2. **Lip (separate mesh):**
   - A curl profile, a parametric 2D lip curve, is swept along the crest polyline:
     - The profile is a function of (curl angle, H, stage), sampled at about 12 points. Plunging profiles close into a tube; spilling ones barely leave the face.
     - The base vertices sit on the displaced face, evaluated with the same function the vertex shader uses, so the lip grows out of the surface.
   - It uses the **same shading** as the ocean (one shared GLSL include for lighting, absorption and foam):
     - thickness-based subsurface: a thin lip glows green;
     - foam along the lip edge and the landing line.
   - One instanced draw for all lips, alpha-blended after the ocean, depth test on.
3. **Spray:**
   - GPU compute particles spawn where the lip lands, at a rate set by plunge speed × crest length.
   - They live and die on the GPU only (no readback).
4. **Foam:**
   - Lip-edge foam and the plunge foam burst on the zone grid come from breaker stage. Outside zones they come from a breaker foam texture.

The GPU never decides where waves break. It only draws breaker data the CPU uploads, keeping the CPU ↔ GPU contract.

## Gameplay queries and surfing

Breakers are queryable through `OceanSurface` (built: height, normal, velocity and whitewater with the face on; `SampleBreaker` planned):

```
bool SampleBreaker(x, z, out BreakerSample s)
    face height and slope at x,z (including breaker displacement)
    water velocity on the face (orbital + breaker)
    stage, distance to the curl, distance to the break point along the crest
    peel direction and speed
```

- **Surfing** needs:
  - the face slope and water velocity (board planing and drive);
  - the pocket, the steep section just ahead of the break point (best speed);
  - the curl region (wipeout or barrel);
  - the peel speed (can the rider make the section).
- All of these come straight from the breaker entity. A heightfield sample alone cannot give them.
- **Buoyancy** for boats in the surf zone uses the same query, so a boat caught by a breaker feels the steep face and the bore.
- **Wipeouts and bodies in the bore** sample the zone sim velocity (already exposed by `ZoneFlowSim`).

## Dependencies and order

1. **Layer B maps** (shoaling, refraction, depth dispersion) per region from bathymetry. Required: the crest tracing comes from them. They also improve the open ocean near shore on their own.
2. **Breaker entities**, CPU only, with debug lines in the ImGui panel (crest, stage colours, break point). Measure the cost.
3. **Face displacement** in the ocean vertex shader, plus a fine local patch around breakers.
4. **Lip mesh** and the shared shading include.
5. **Zone sim hand-off** (bore mass, momentum and foam injection).
6. Spray particles.
7. `OceanSurface.SampleBreaker`, then surfing.

## Open questions

- **Crest tracing method:** wave-ray marching or an eikonal solve on the layer B grid. Rays are simpler. An eikonal solve handles caustics (crossing rays) better.
- **Breaking against vehicles:** a moving hull in the surf zone should alter the breaker (block or reflect it). For now, the hull only feels the breaker.
- **Not chosen:** a 3D particle or grid fluid sim (SPH, FLIP) for the curl. It is too slow for the frame budget and not deterministic. It might come back much later for spray detail only.
