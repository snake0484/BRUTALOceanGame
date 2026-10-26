# Water architecture

The water surface that gameplay and physics see is built from three layers. The GPU only adds visual detail on top of it (see `ocean-gpu-contract.md`).

```
Sample(x, z) = C (if an active local sim covers x,z, blended at its edge)
             else  A modulated by B
```

## Layer A — open-ocean swell (implemented)

- `OceanSwell`: a global sum of discrete JONSWAP components driven by the local wind, plus an optional groundswell from a distant storm (a narrow Gaussian band with its own height, period and direction; `docs/surf-research.md`).
- The dominant band (the groundswell once its height is at least half the wind sea's) sets the surf carrier, the direction the coastal maps are solved for, and the set length.
- Deterministic from seed + wind history + time. Cheap, and queryable anywhere.
- It has no knowledge of obstacles. Near land it acts as the **incident field** for B and C.

## Layer B — coastal modulation maps (v1 implemented)

- Built per region from bathymetry and the wind field. Rebuilt when terrain chunks change or wind shifts a lot.
- Each wave component gets a local amplitude scale and direction rotation from the maps:
  - Sheltering: the lee of land, plus shorter fetch behind islands.
  - Shoaling: height grows as the water gets shallower.
  - Refraction: crests bend to follow depth contours.
  - Depth-correct dispersion: ω² = g k tanh(k h).
- Still analytic, so it stays deterministic and cheap. The GPU low band applies the same maps, so visuals still match physics.
- This is the only layer distant islands need. They never require a live simulation.

### Layer B v1 (`CoastalField`, `CoastalBuilder`)

- **Grid:** 256×256 cells at 4 m (1 km), on the bathymetry grid's origin. Depth comes from the **analytic terrain surface** (`TerrainGenerator.SurfaceHeight`) with the brush edits applied as spheres (`TerrainCoastalDepth`), averaged over 2×2 samples per cell. It is the same at every LOD; streamed bathymetry varied with LOD and made the maps change while flying. Land is ≤ 0.2 m.
- **Reference frequencies:** 12 fixed, log-spaced ω from 0.15 to 3.2 rad/s. Each swell component interpolates between the two nearest by log ω, using a reference coordinate carried in `GpuWave.Padding`.
- **Phase:** per reference, an **eikonal** solve |∇ψ| = k(ω, h), where ω² = g k tanh(k h).
  - Method: fast sweeping, first-order Godunov.
  - Boundary: the plane wave k₀ d̂·x is imposed on the upwind map edges. d̂ is the target wind direction.
  - Land is slow (8× k at 0.2 m), so fronts wrap round islands.
  - The map stores the **phase lag** δψ = ψ − k₀ d̂·x. A component's phase is k₀ᵢ·x + φᵢ − ωt + δψ, and its local wavevector is k₀ᵢ + ∇δψ.
  - This one field gives both depth dispersion (shorter, slower waves) and refraction (crests bend to the contours).
- **Amplitude:** per reference, energy is carried along the rays: u·∇F = −∇·u with F = ln(E c_g), upwind sweeps, F = 0 on the boundary.
  - S = √(e^F c_g0 / c_g) is shoaling and refraction together. It is clamped to 3.
- **Land cells** take their phase lag and amplitude from neighbouring water (a fill sweep), so bilinear samples at the shoreline stay smooth.
- **Edge fade:** the outer 16 cells fade to lag 0, amplitude 1 and deep, so there is no seam where the map ends.
- **Per sample:**
  - Bilinear lag (with its analytic gradient), amplitude and depth, read from the map.
  - Breaking cap: all amplitudes × min(1, γ h / H_s,local), with γ = 0.55. H_s,local = 4√(Σ_j E_j S_j²), where E_j is each reference's share of the swell variance, per tick.
  - Gerstner displacement follows the local wave direction. Choppiness is re-clamped per sample so the rms of the shoaled Gerstner trace Σ a·k·cos θ stays under 0.25 (a fold 4 σ out, on the crests that whitecap), the same statistical bound as the open sea's, so the two agree where a field fades out.
    - History: both clamps bounded the worst case Σ|a·k| by 0.9. Over 128 random phases that sum is 14× the rms, so from 8 m/s up the clamp overrode the asked choppiness (0.8) with 0.68 at 10 m/s, 0.52 at 20 and 0.44 at 30, pinning chop × rms slope at 0.065: the sea got higher but its crests peaked no harder, and read as smooth in heavy weather.
- **CPU ↔ GPU:**
  - The same 28-float texels: depth, active flag, and (lag, amplitude) × 12.
  - The GPU reads them from storage buffers with the same manual bilinear the CPU uses, and evaluates at the rest position x₀. There are two slots per frame in flight (current and fading-out field); a field keeps its slot, so a crossfade copies only the new one (7.3 MB).
  - The CPU prepares the coastal terms once at the query point and linearises the lag over the Gerstner inversion. Its result matches the GPU to second order in horizontal displacement.
- **Build:**
  - Depth jobs (8 row bands), then 12 jobs (one per reference), then one finish job (land fill, edge fade, flags), on the Brutal job system. Triple-buffered: fading out, fading in, being built.
  - Rebuilt only when the map re-centres (every 128 m of camera travel), the terrain is edited, or the wind direction moves by more than 5°. Never for LOD streaming.
  - **Crossfade:** a new field blends in over 1.5 s (`CoastalSystem.FadeSeconds`). Lag, gradient, amplitude and depth are lerped between the two fields, identically on CPU (`SampleCoastal`) and GPU. The first build fades in from the open ocean. No build starts until the fade ends.
  - Before this, rebuilds swapped instantly about once a second while flying, and the swell near shore jumped every few seconds. Measured on a 15 m/s scripted fly-by at a fixed nearshore point:
    - Before: a jump on every rebuild, and fades changed the surface by up to 3.6 m per unit of fade.
    - After: the largest per-tick height change is 0.026 m, including startup, against 0.006 m median and 0.018 m p99 for ordinary wave motion.
- **Toggle:** the Ocean panel's *Layer B coastal* checkbox, or `--no-coastal` on the command line.
- **Measured (Release, RTX 4090, 1600×900, 14 m/s wind):**
  - **Benchmark:** `--bench [seconds]` turns vsync off, waits for terrain and the coastal maps, warms up for 3 s, then alternates layer B on and off in 2 s blocks. It logs the median and mean of every GPU scope per side and exits. Alternating blocks cancel clock drift.
    - Run-to-run spread is about ±0.001 ms.
    - The vsync numbers below were inflated by an idle GPU at low clocks.
  - **Ocean pass with `--bench 12`:** 0.0655–0.0666 ms with layer B against 0.0461–0.0471 ms without, so **+0.020 ms** (four runs, medians). Frame +0.025 ms.
  - Earlier vsync-only readings, for history: first version about +0.115 ms (0.20 against 0.085); after optimisation about +0.05–0.07 ms.
  - What changed:
    - The 12-reference local arrays are gone. Components arrive sorted by frequency, so the shader keeps the two references they blend between and only re-reads (4 texel loads) when the pair changes.
    - The breaking cap and the steepness clamp loop only over the references in use (`counts.yz`), with steepness taken from per-reference sums (`RefChop`) instead of per component. The CPU mirrors this in `CoastalLimits`.
    - Components the anti-alias filter removes skip all coastal maths.
  - CPU `SetTime` is unchanged at about 0.004 ms.
  - Build: about 300 ms on jobs, including sampling the analytic seabed.
  - A coastal `Sample` prepares 128 components once, then runs the same SIMD loops as deep water.
- **Tests** (`CoastalFieldTests`), on a plane beach:
  - Snell's law: the along-shore wavenumber stays within 0.1%.
  - Dispersion: |k| matches ω² = g k tanh(k h) within 0.6%.
  - Normal incidence: amplitude matches the shoaling coefficient K_s within 0.1%.
  - Breaking: crests stay below the water depth.
  - Deep water leaves the swell bit-identical.
- **Limitations:**
  - The phase map is computed for the target wind direction. Off-axis components reuse it.
  - A rebuild crossfades over 1.5 s, so crests near shore slide smoothly after an edit or a recentre rather than jump.
  - There is no diffraction beyond first-arrival wrapping.
  - Sheltering of fetch is not modelled yet.

## Layer C — active local simulation (planned)

- A grid solver for shallow water flow (virtual pipe / height field, with momentum). It is the **same solver** as voxel water flow.
- It runs only in active regions: near the player, near important bodies, or near obstacles in view. Settled regions sleep.
- Its open edges are a blending band driven by A + B. Waves enter there, then reflect, run up the beach, break and flow around islands inside the grid.
- Breaking and splash events are uploaded once per tick to feed GPU foam and spray.
- **Limitation:** shallow water equations don't model deep-water wave speed (every wave moves at √(gh)). Use C nearshore, roughly where kh < 0.5. If deep-water reflection off cliffs looks wrong, add a dispersive wave-equation solver for that zone.
- **Moving bodies** (ships, large debris) inject displacement and pressure into C, which produces bow waves, wakes and sloshing. Outside C, bodies only receive buoyancy (one-way coupling).
- **Determinism:** C has state. It needs either a deterministic fixed-tick simulation or server authority with snapshots. Gameplay-critical queries must tolerate small differences.
- **Cost (estimate, not measured):** about 0.5 ms per tick across jobs for a 256×256 grid at 1 m with a few substeps. Cost scales with active area.

## Breaking waves (planned)

- **Why separate:** a heightfield cannot curl over itself, so breaking waves are separate **breaker entities** (`breaking-waves.md`).
- **Where they come from:** crest lines traced through the layer B maps. A wave breaks where height / depth > ~0.78; the beach slope picks spilling, plunging or surging.
- **Rendering:**
  - The face stays on the ocean heightfield, steepened by breaker displacement in the vertex shader.
  - Only the lip and tube are an extra mesh, built on the face and using the same shading as the ocean.
- **Hand-off to C:** when the lip lands, the breaker injects mass, momentum and foam into C, which then runs the white water, run-up and backwash.
- **Queries:** `OceanSurface` exposes breaker samples (face slope, pocket, curl, peel) for buoyancy and, later, surfing.

## Voxel terrain integration

- **Bathymetry and solids:** each chunk provides the seabed height per water column and a solid mask. These feed B (rebuilt when a chunk changes) and C (bed height and walls).
- **Bed friction:** voxel material IDs set friction (and later porosity).
- **Non-voxel statics** (piers, placed structures, large static Jolt bodies) are drawn into the same solid mask from their shapes.
- **One solver for all water:** lakes, rivers, flooding and the coastal zone all use C. Cells connected to the open sea hold "sea level + A + B" instead of tracking their own water volume (the ocean is an infinite reservoir).
- **Caves and overhangs:** a 3D voxel-water solver (volume fraction per cell) handles them, coupled to the 2.5D grid at openings.
- **Terrain edits** mark chunks dirty. That updates bathymetry and wakes the nearby C region (dig a channel and water flows in).
- **Rendering:** active C regions upload a height texture each tick. The clipmap blends it in near shore, so the surface stays seamless with the open ocean.

## Query API

- Gameplay, buoyancy, AI and audio query a single combined-surface facade (`OceanSurface`): built over A, B and the breaker faces (docs/breaking-waves.md, sixth pass); C (zones) still to blend in.
- They never call `OceanSwell` directly once that facade exists.

## Much later: flooding enclosed vehicle volumes

Example: a hull breach on a submarine.

- **Interior water lives in the vehicle's own frame**, as a compartment model (volume per compartment plus a free surface), or as a small vehicle-local voxel water grid for detailed interiors.
- **Inflow through an opening:** Q = Cd · A · sqrt(2 g Δh). The outside head comes from the combined ocean surface sampled at the hole's world position. Flow between compartments works the same way through doors and hatches.
- **Coupling to the hull's physics:**
  - Interior water adds mass and moves the centre of mass, including free-surface sloshing driven by the body's acceleration.
  - The flooded volume stops counting toward displaced buoyancy.
- Visuals render from the same vehicle-local data.
