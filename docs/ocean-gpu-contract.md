# Ocean GPU contract

The CPU swell (`OceanSwell`) owns all wavelengths ≥ `MinWavelength` (default 8 m). The GPU adds three FFT cascades with **only** shorter wavelengths, in exclusive wavenumber bands, so the visual surface never disagrees with physics. GPU output never feeds back to gameplay.

## Conventions (must match CPU)

- Height field: η(x) = Σ_k h(k,t) e^{i k·x}. IFFT is the plain sum, no 1/N.
- Time: h(k,t) = h0(k) e^{-iωt} + conj(h0(-k)) e^{+iωt}. A wave travels along +k (phase k·x − ωt).
- Horizontal displacement (before choppiness λ): D̂x = i (kx/|k|) ĥ, D̂z = i (kz/|k|) ĥ. In real terms Dx = −Σ a (kx/|k|) sin θ, the same as the CPU.
- Derivatives: ∂/∂x ↔ multiply by i kx.
  - ĥx = i kx ĥ, ĥz = i kz ĥ
  - D̂xx = −kx²/|k| ĥ, D̂zz = −kz²/|k| ĥ, D̂xz = −kx kz/|k| ĥ
- Jacobian with choppiness λ: J = (1 + λDxx)(1 + λDzz) − (λDxz)².
- Normal from slope s = (hx, hz) and J terms dxx = 1 + λDxx, dzz = 1 + λDzz, dxz = λDxz:
  n = normalize(hz·dxz − dzz·hx, dzz·dxx − dxz², dxz·hx − hz·dxx).
  When the CPU and GPU bands are summed, add the raw partials (hx, hz, λDxx, λDzz, λDxz) from every band before forming n and J.

## Cascades

- Fixed in C#: N = 256, cascade count = 3. Images are `image2DArray`, layer = cascade.
- Texel (x, y) ∈ [0, N) maps to k = (x − N/2, y − N/2) · 2π/L. The spectrum is centred, so the resolve multiplies by (−1)^(x+y).
- Spatial texel n maps to world offset n·L/N. Sample with uv = worldXZ / L, repeat addressing.
- Band: keep k only if kLo ≤ |k| < kHi, otherwise h0 = 0. Cascade 0's kLo = 2π / MinWavelength.
- Time is looped: t = renderTime mod T (T = loop period). ω is quantised to multiples of ω0 = 2π/T so the loop is seamless.

## Spectrum

- Dispersion: ω = sqrt(g k tanh(k h)) with g = 9.81, and dω/dk = g (tanh(kh) + kh·(1 − tanh²(kh))) / (2ω). Deep water when depth ≥ 1e5: ω = sqrt(g k), dω/dk = g / (2ω).
- JONSWAP: S(ω) = α g² ω⁻⁵ exp(−1.25 (ωp/ω)⁴) γ^r, with r = exp(−(ω−ωp)² / (2 σ² ωp²)), σ = 0.07 if ω ≤ ωp else 0.09.
- Spreading: D(θ) = Ns · cos^{2s}((θ − θw)/2), where θ is the angle of k and Ns normalises it over [−π, π] (computed in C#).
- Directional wavenumber spectrum: Ψ(k) = S(ω) D(θ) (dω/dk) / |k|.
- h0(k) = (ξ1 + i ξ2) · sqrt(Ψ(k) Δk² / 2), Δk = 2π/L. ξ are standard normals from a deterministic hash of (x, y, cascade, seed) through Box–Muller. h0(−k) uses the hash at the texel of −k, i.e. ((N − x) mod N, (N − y) mod N).
- h0(k), h0(−k) and the quantised ω depend only on the spectrum parameters, the seed and the cascade geometry, so they are cached (`ocean_h0.comp` into h0Map and omegaMap) and rebuilt only when one of those changes (`OceanFft.WriteUniforms` compares the uniforms that reach it; during a wind blend that is every tick). Each frame does the time evolution alone.

## Compute pass descriptor set (set 0), shared by all compute shaders

```glsl
layout(std140, set = 0, binding = 0) uniform Fft {
    vec4 cascadeL;     // xyz = patch size L per cascade (m), w = N
    vec4 cascadeKLo;   // xyz = band low |k|
    vec4 cascadeKHi;   // xyz = band high |k|
    vec4 wind;         // x = U10 m/s, y = direction rad (waves travel toward), z = fetch m, w = depth m (>= 1e5 means deep)
    vec4 spectrum;     // x = alpha, y = peak omega, z = gamma, w = spread exponent s
    vec4 params;       // x = spread norm Ns, y = choppiness λ, z = looped time t, w = loop period T
    vec4 foam;         // x = foam decay factor this frame, exp(−decay·dt), y = target whitecap share, z = frame dt seconds, w = threshold ease per frame
    uvec4 seed;        // x = seed
} fft;
layout(set = 0, binding = 1, rgba32f) uniform image2DArray specA;    // (Â.re, Â.im, B̂.re, B̂.im)
layout(set = 0, binding = 2, rgba32f) uniform image2DArray specB;    // (Ĉ.re, Ĉ.im, D̂.re, D̂.im)
layout(set = 0, binding = 3, rgba16f) uniform image2DArray dispMap;  // (λDx, h, λDz, λDxz)
layout(set = 0, binding = 4, rgba16f) uniform image2DArray derivMap; // (hx, hz, λDxx, λDzz)
layout(set = 0, binding = 5, rgba16f) uniform image2DArray foamMap;  // r = persistent foam 0..1
layout(set = 0, binding = 6, rgba32f) uniform image2DArray h0Map;    // (h0(k).re, h0(k).im, h0(−k).re, h0(−k).im)
layout(set = 0, binding = 7, r32f) uniform image2DArray omegaMap;    // ω, quantised to the loop
layout(std430, set = 0, binding = 8) buffer FoamStats { uint histogram[2 * 128]; float threshold[4]; } stats;
layout(push_constant) uniform Pass { int direction; } pass; // FFT only: 0 = rows (along x), 1 = columns (along y)
```

The four complex signals packed two real fields each: A = Dx + i·Dz, B = h + i·Dxz, C = hx + i·hz, D = Dxx + i·Dzz (choppiness not applied yet).

| Pass | Shader | Local size | Dispatch |
| --- | --- | --- | --- |
| Spectrum cache | `ocean_h0.comp` | 16×16×1 | (N/16, N/16, 3), only when the spectrum parameters or seed change |
| Evolve + IFFT rows | `ocean_fft.comp`, direction 0 | 128×1×1 (N/2) | (N, 1, 3), workgroup x = row |
| IFFT columns + resolve + foam | `ocean_fft.comp`, direction 1 | 128×1×1 (N/2) | (N, 1, 3), workgroup x = column |
| Whitecap thresholds | `ocean_foam_threshold.comp` | 1×1×1 | (1, 1, 1) |

- The row pass evolves the cached spectrum to the frame's time straight into shared memory (one h0Map and one omegaMap load per texel; a texel with h0 = 0 skips the sincos), runs a radix-2 Stockham inverse FFT along x (log2 N stages, ping-pong shared buffers, twiddle e^{+2πi…}) and writes its line to specA/specB.
- The column pass loads a column of specA/specB, transforms it along y and resolves it from shared memory: the sign (−1)^(x+y) and λ, then dispMap, derivMap and the foam. specA/specB hold only the half-transformed field; nothing else reads them.
- **Foam:** f = max(f_prev · exp(−decay·dt), clamp((threshold_i − J) / 0.01 + 0.5, 0, 1)), where J is that cascade's own Jacobian.
  - Only the 48 m and 12 m cascades seed whitecaps; the 3 m ripples don't.
  - Each adds its J to a 128-bin histogram over [0.4, 1.4], first in shared memory per column, then in the `FoamStats` buffer (binding 8, storage).
- **Thresholds:** `ocean_foam_threshold.comp` reads each histogram after the column pass and finds the J below which the target share of that cascade's surface lies, interpolated within a bin. It eases `threshold_i` toward that J (0.2 per frame), then clears the histograms. The next frame's column pass seeds below it. It is GPU-only: no readback.
- **Barriers** (`OceanFft.Record`): a compute→compute barrier at the start orders the previous frame's column and threshold passes before this frame's compute. The previous frame's draw, which samples dispMap, derivMap and foamMap, is waited for only between the row and column passes, so a cache rebuild and the row pass overlap its tail. The barrier at the end makes the column pass's writes visible to the vertex and fragment stages.

## Graphics set (set 0) for `ocean.vert` / `ocean.frag`

- binding 0: the Frame UBO. Two vec4 are inserted after `waterScatter` and before `counts`:
  - `vec4 cascadeL;` (xyz = L, w = cascade count)
  - `vec4 cascadeKLo;` (xyz = band low |k|, w = persistent foam strength)
  - `vec4 foamParams;` (x = rain 0..1, y = rain time, looped; see `rain.md`; z = debug view, w = sim tick alpha)
  - `vec4 windSea;` after `carrierChop` (x = the spectrum's wind speed, for the fetch limit below)
- binding 1: `sampler2DArray dispMap`
- binding 2: `sampler2DArray derivMap`
- binding 3: `sampler2DArray foamMap`

All three use linear filtering, repeat addressing, no mips, and are in `GENERAL` layout.

- Vertex: x0 is the rest grid point after morph. Cascade i displaces vertices with weight wv_i = 1 − smoothstep(0.25 λlo_i, 0.5 λlo_i, se), where λlo_i = 2π / kLo_i and se is the morph-effective spacing. Sample with textureLod(…, 0).
- Fragment: per-pixel footprint fade per cascade, wf_i = clamp(1 − (fp/texel_i − 1)/3, 0, 1), with fp = length(fwidth(x0)) and texel_i = L_i/N. Sum the CPU partials (from the vertex) and the weighted cascade partials, then form n and J as above.
- Shore fade: the cascades' displacement, partials and whitecaps are scaled by shore = smoothstep(`SHORE_CALM` 0, `SHORE_FULL` 1.5 m, column), with column the water under the vertex: bed depth + swell height (a swash sheet's thickness; in sea zones the sim's depth). So the chop comes in on the crests and settles in the troughs, thins gradually toward the land and is gone at the water's edge.
  - History: 0.2–1.5 m of still depth left a glassy band that didn't move with the waves. Capping at 0.55 × column / 0.3 m (the band's Hs) put full chop right up to the sand, which the user judged unrealistically close to the land.
  - Measured on the surf-test beach (`--wind 14 --surf-test --cam -224 2.2 -20 75 -8`, mean luminance gradient per screen band, foam excluded, four frames): nearest the sand 0.80 against 0.81–0.87 for the still-depth fade (its run-to-run spread), the mid shallows 3.1–3.4 against 2.5–2.7. The cap gave 5–7 there.
- **Wind sea (fetch and shelter, `windSea`):** the cascades are wind sea, and the wind only raises what the water upwind allows. `RainCover` measures the **fetch** F over the whole bathymetry grid (a 128 × 128 map of 8 m cells, `fetchRect` / `fetchMap` in `rain_cover.glsl`), and per 2 m column around the camera the **exposure** e, 1 open to the sky and falling to 0 at 10 m under cover. Covered water takes the fetch of the nearest open water.
  - **Effective fetch** (after Saville 1962): F is the fetch along 9 radials spread over ±45° of the spectrum's wind, weighted by cos² (the wind sea's spreading) and normalised by the weights, so open sea keeps its full fetch. Each radial is an upwind sweep of u·∇F = 1 over open cells, 0 on any cell with land or cover in it, open sea upwind of the grid, capped at `FetchReach` 400 m. So a channel open at its ends takes the chop that runs in along it even when the wind crosses it, and a lone rock shelters only the radials it blocks. Blocked cells then take the largest fetch beside them, so sampling never drags the water next to a rock, roof or beach toward 0.
  - **Far matches near:** the window's open columns sample the map, and outside the window the shader reads the map itself (`gridFetch`), so water is judged the same from any distance. Before, fetch was measured only in the 128 m window and everything past it counted as open sea: the water near the camera in a sheltered channel was flat while the same water seen from farther off had full chop, with a hard line at the window's edge.
  - **Measured** on the channel between the start island and its eastern neighbour (`--cam 274 8 -69 90 -18`, default weather): the 12 m cascade's slope share there went from 0.13 to 0.33 on screen, the 3 m one stays at 1, and the 48 m one at 0 (a ~100–150 m effective fetch). From 230 m away (`--cam 274 45 -300 90 -12`) the channel now reads as sheltered too, instead of full open-sea chop. `--cave-test pool`: fetch 6 m at both edges (was 5 and 22); the cave's inside keeps exposure 0.
  - **Cost:** the openness is read on the main thread when the tops change (one `Vector128` compare per row of a cell); the radials are swept again only when that changed it, the grid moved or the wind turned one of 64 headings, as 9 jobs on a below-normal-priority scheduler, landing a frame or more later. `--fly-test` (30 s at 40 m/s): cover main-thread time 31.8–36.5 ms against 28.8–29.6 before, p95 per rebuild 0.20 against 0.13 ms; main-thread p95 and p99 unchanged (1.08–1.09 and 2.36–2.37 ms). Swept on the main thread it was 88 ms, p95 1.0 ms. GPU (`--wind 14 --bench 12 --surf-test`, A B A B): within the day's ±0.015 ms run-to-run spread.
  - Over F the wind U (`windSea.x`, the spectrum's) raises waves up to the JONSWAP peak kp = (22 (g²/(U F))^(1/3))² / g. Shorter waves are saturated as on the open sea; longer ones are missing.
  - Cascade i keeps the part of its band [kLo_i, kHi_i] at or above kp: in height (vertex displacement) √((k_c⁻² − kHi⁻²) / (kLo⁻² − kHi⁻²)), a k⁻³ tail; in slope (fragment partials and whitecaps, through `vShoreDamp`, now one weight per cascade) ln(kHi / k_c) / ln(kHi / kLo), with k_c = clamp(kp, kLo, kHi). The last cascade's kHi is its Nyquist, π N / L.
  - Both blend to 1 between 200 and 400 m of fetch, so the cap never shows, and both scale by e.
  - At 10 m/s: a 20 m pond keeps only ripples under 0.2 m, a 200 m cove chop under 1 m. Longer fetches than 200–400 m count as open sea, which overstates the 8–1.6 m cascade over bays with an offshore wind (it needs about 5 km to grow fully).
  - Fetch runs over any water under sea level, however shallow: a 0.3 m flat caps the wind sea near 0.2 m, which at 14 m/s is the height of about 900 m of fetch, so waves do cross it. A pool behind such a flat is open water, not a lake (`--cave-test pool` puts a rock berm on its upwind rim for that reason).
  - **Cost** (`--wind 14 --bench 12 --surf-test`, A B A B against the build before it, medians): *Ocean surface* +0.000 to +0.002 ms, *Ocean patch* 0.0276 → 0.0328 ms, frame +0.004 to +0.006 ms with layer B on.
- Whitecaps = foam strength · Σ wf_i · shore_i · foamMap_i.r, faded over the shallows and to the wind sea (shore), drawn as lace (`foamLace`). Surf foam is added by max. There is no whitecap term from the total J any more (see Foam tuning).
- **Sea zones' own flow** (binding 13, `zoneFlowAt`; `voxel-water.md`, "Sea caves and walls"): the zone sim's surface drift and turbulence per 1 m cell. In zones, a cascade's share is at least turbulence × (0.2, 0.8, 1.2) (`TURBULENCE_SHARE`), so churned water is rough whatever the wind. `vFlow` there is the drift plus turbulent eddies, so the cascades' partials and all the lace are flow-mapped with the current. `vTurbulence` (location 11) adds lace over churned water and speeds its churn.

## Foam tuning

**Whitecap coverage follows the measured curve.** W(U) = 3.84e-6 · U^3.41 is the share of the sea surface covered by whitecaps at 10 m wind speed U (Monahan & O'Muircheartaigh 1980): about 1% at 10 m/s, 5% at 16, 10% at 20, 22% at 25 and 42% at 30. It is capped at 0.6.
- Each frame the 48 m and 12 m cascades seed foam on the share W(U) / `WhitecapLinger` of their surface with the lowest J, the quantile read off their own J histogram.
- The foam decays at `FoamDecay` 0.6/s, 1/e in 1.7 s, within the 1.4–4.8 s effective decay times measured by Callaghan et al. (2012).
- `WhitecapLinger` (7) accounts for foam outliving the breaking that seeds it.

**Calibrated over deep water** (foam debug view, weight past 0.5, `--cam 1500 40 -1500 0 -89`):

| Wind (m/s) | 10 | 16 | 20 | 25 | 30 |
| --- | --- | --- | --- | --- | --- |
| Measured | 0.8% | 4.6% | 10.4% | 24.8% | 47.9% |
| W(U) | 1.0% | 4.9% | 10.5% | 22.5% | 41.8% |

**Why a quantile rather than a fixed J:** the surface's J distribution hardly changes with wind. With the old system, from 8 to 30 m/s the 1% quantile of the drawn J stayed between 0.89 and 0.93 and the median at 1.0, because short waves are saturated and the CPU swell clamps its steepness.
- So no fixed J can track W(U). The old fix raised both thresholds by 0.02 per m/s above 3 m/s, with no cap. Past about 23 m/s the whitecap threshold passed 1.0, the J of flat water, so with its 0.3-wide ramp every pixel got partial foam and the whole sea went white.
- Measured then: 0% of the surface past 0.5 foam weight below 25 m/s, and a uniform haze at 30.
- The quantile follows whatever the spectrum, fetch and choppiness make of J.
