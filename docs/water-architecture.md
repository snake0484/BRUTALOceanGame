# Water architecture

The ocean is four pieces: the analytic swell (CPU and GPU), GPU FFT detail, shading, and a GPU-only shore wash. Gameplay and physics see the swell, depth-capped near shore, through one facade (`OceanSurface`). The GPU only adds what needs to look good and never feeds back.

```
OceanSurface.Sample(x, z) = swell(x, z) × min(1, 0.55 h(x, z) / Hs)      CPU and GPU, identical
drawn surface             = that + FFT detail (calmed near shore) + swash sheets on the beach      GPU only
```

The island's other water layers (layer B coastal maps, breakers and lips, voxel water and its zone sim, lake and river, wind sea on voxel water, fog volume, SSR, rain) were cut for the space pivot (`docs/pivot/inventory.md`). Their design history is in git.

## Swell (`OceanSwell`)

- A global sum of discrete components: a JONSWAP wind sea from the local wind (`SwellParams`: speed, direction, fetch, γ, spreading; 32 bands × 4 directions by default), plus an optional groundswell from a distant storm: a narrow Gaussian band in ω (σ = `SwellBandwidth` × ω_p, default 5%, sampled to ±3σ) with narrow cos-2s spreading (s = 40), scaled to give exactly the requested Hs (`WaveSet.BuildGroundswell`; `--swell H T dirDeg`, Ocean panel sliders). The game starts with a 1.2 m, 13 s swell toward +X.
- Only wavelengths ≥ `MinWavelength` (8 m); everything shorter is the GPU's.
- Dispersion ω² = g k tanh(k h) at the spectrum's depth.
- Wind changes blend the spectrum over `BlendSeconds` (30 s), never snap (`WindChangeBlendsWithoutSnap`).
- Gerstner displacement with choppiness re-clamped so the rms of Σ a·k·cos θ stays under 0.25 (a fold 4 σ out), so a high sea peaks harder than a low one (`ChoppinessHoldsInHighSeas`).
- **Dominant band and carrier:** once the groundswell's height is at least half the wind sea's it is the dominant band, otherwise the wind sea is. Its peak is the surf carrier the shore wash is timed from, and its bandwidth sets the set length (`shore-wash.md`).
- Deterministic from seed + wind history + time (`SameSeedIsBitIdentical_DifferentSeedDiffers`). `SetTime` once per tick on the main thread, about 0.004 ms; samples are read-only between ticks, safe from jobs and allocation-free.
- SIMD sampling: `Sample` inverts the Gerstner map (`InversionIterations` 4) to find the rest position under (x, z), then sums height, normal, orbital velocity and Jacobian.

## Depth cap

- Every swell component is scaled by min(1, `DepthCapGamma` 0.55 × h / Hs): 0 on land and in pits cut off from the sea, so the open swell cannot run through a beach.
- **CPU:** `OceanSwell.Cap` reads `IShoreDepth` (`TerrainShoreDepth`: the analytic terrain surface plus a snapshot of the brush edits as spheres), the same at every LOD and with no camera, so a server answers the same as a client (review ruling C7).
- **GPU:** `oceanAt` applies the same cap from the depth map (`BathymetryTexture`). The two sources agree to the depth map's 2 m cells and the streamed terrain's quantisation.
- Tests (`OceanSurfaceTests`): `DeepWater_IsTheSwell`, `ShallowWater_IsCappedByDepth`, `Land_IsFlatAtSeaLevel`, `Sampling_IsAllocationFree`.

## GPU FFT detail (`OceanFft`)

- Three cascades of 256², in exclusive wavenumber bands above the CPU cutoff, so the drawn surface never disagrees with physics. Full contract: `ocean-gpu-contract.md`.
- Driven by the swell's spectrum parameters (`OceanSwell.GpuSpectrum`: wind, fetch, depth), the same everywhere (no terrain cover). Time loops at `LoopPeriod` 1000 s with ω quantised to it.
- Sampled at (x − camera) / L + frac(camera / L) per cascade (`cascadeAnchorX/Z`), and shader time wraps at 3000 s (the LCM of the FFT loop and the lace's 600 s), so coordinates and time stay small anywhere (review X4).
- Whitecaps follow the Monahan curve: each frame the two larger cascades seed foam on the measured share of the surface from their J histograms (`ocean_foam_threshold.comp`), decaying by 1/e in about 1.7 s (`FoamDecay`), drawn as lace.
- Near shore the cascades calm over 0–1.5 m of water column under the surface (`SHORE_CALM`, `SHORE_FULL`).
- Measured: 0.030 ms for the FFT.

## Shore wash

Swash sheets, the water's edge foam line and the up-rush foam map, GPU only: `shore-wash.md`.

## Rendering

- `OceanRenderer`: a CDLOD clipmap around the camera, one draw per level; `ocean.vert` evaluates `oceanAt` (swell + cap + swash + FFT) per vertex, `ocean.frag` shades with `water_shading.glsl` / `ocean_shading.glsl` (Fresnel sky reflection, depth absorption, lace foam, see-through shallows).
- The frame is an opaque pass then a water pass; the ocean draws in the water pass.
- The ocean is drawn wherever the depth map is not a pit; dry land is hidden by the surface's height over the ground.
- Measured (`--bench 12`, RTX 4090, island surf scene, before the pivot cuts): bare-swell ocean pass 0.0573 ms. Not re-measured since the cuts; `--bench` now alternates the shore wash on and off.

## Query API

- Gameplay, buoyancy, AI and audio query `OceanSurface` (`Sample`, `SampleHeight`, `SampleNormal`, `SampleVelocity`, `SampleHeights`, `SurfacePoint`), never `OceanSwell` directly.
- On planets this becomes `OceanSurface.Sample(double3 planetLocal, long tick)` with one global definition, evaluated in fixed per-region tangent frames (review ruling C5); the camera-window depth map stays a GPU cache only.
