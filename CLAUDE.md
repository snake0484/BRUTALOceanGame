# BRUTALGame

Vehicle building and crafting game in C#, built directly on the BRUTAL framework (`BRUTAL/`, cloned from https://github.com/ahwootech/BRUTAL).
Core pillars, in priority order:

1. **Performance** – every system is designed for frame budget first. No feature ships if it blows the budget.
2. **Ocean water** – the best-looking oceans possible. Split simulation: CPU owns the physics-relevant water, GPU owns visual detail.
3. **Voxel terrain** – fully editable, chunked, streamed.
4. **Vehicle building** – players assemble land/sea/air vehicles from parts, simulated physically.
5. **Crafting** – resource gathering from terrain feeds parts and vehicles.

## Rules for Claude

- Never build the project unless asked.
- Read `~/.claude/cs-standards.md` before writing C#.
- `BRUTAL/` is the upstream framework. Do not modify it; game code lives outside it. If the framework needs a change, say so and propose it separately.
- BRUTAL is deliberately small: no renderer, scene graph or editor. We write our own. Do not pull in a general-purpose engine.
- Before adding a NuGet package, check if BRUTAL already covers it.

## Tech stack (from BRUTAL)

| Need | Library |
| --- | --- |
| Rendering / compute | `Brutal.Vulkan`, `Brutal.Vulkan.Vma`, `Brutal.Slang` / `Brutal.ShaderC` |
| Window / input | `Brutal.Glfw` |
| Physics | `Brutal.Jolt` |
| Threading | `Brutal.Concurrency` (`JobScheduler`, `IJob`) |
| Memory | `Brutal.Memory`, `Brutal.Collections` (`NativeList`, `FreeList`) |
| Maths | `Brutal.Numerics`, `Brutal.Maths` |
| Audio | `Brutal.Fmod` |
| Debug UI / profiling | `Brutal.ImGui`, `Brutal.ImPlot`, `Brutal.RenderDoc` |
| Assets | `Brutal.Gltf`, `Brutal.Ktx`, `Brutal.Texture`, `Brutal.MeshOptimizer` |
| Networking (later) | `Brutal.RakNet` |

## Performance rules

- Zero allocations in per-frame / per-tick paths. Use unmanaged memory, `Span<T>`, `NativeList`, pooled buffers. No LINQ, no boxing, no closures, no `params` in hot paths.
- Data-oriented layout: structs of arrays for bulk data (voxels, water cells, particles). Keep hot data contiguous and cache friendly.
- `unsafe` and pointers are fine where they measurably help.
- Heavy work goes through the job system; main thread only coordinates. Jobs must not share mutable state without explicit ownership.
- Fixed simulation tick (decoupled from render rate). Rendering interpolates.
- GPU work is batched: indirect draws, persistent mapped buffers, minimal pipeline/descriptor changes. Async compute for water and terrain meshing where possible.
- Every major system exposes timings in an ImGui profiler panel. Any perf claim needs a measurement.
- Consider cost of anything that runs on tick or update before adding it.

## Water

Water is split by responsibility. **CPU = truth that gameplay and physics can query. GPU = everything that only needs to look good.**

### Drivers: wind and terrain

All water motion is physically driven by **wind** and **terrain**. No hand-authored wave or flow directions.

- **Wind field**: a CPU wind simulation (global weather + local terrain influence) on a coarse grid. Speed, direction, gustiness. Terrain shelters, channels and deflects wind. Sampled by water, particles, vehicles (sails, aircraft) and audio.
  - **Weather** (`docs/weather.md`, `src/Weather/`, implemented v1): localized, analytic pressure systems (storms, lows, highs) from seed + time, baked into a 32 km snapshot map around the camera every 5 s. Gameplay queries `WeatherMap.Sample`. Drives the swell wind and the sky; terrain influence still to come.
  - **Rain** (`docs/rain.md`, `RainRenderer`, `rain_*.glsl`, implemented v1): GPU streaks carried by the wind, splash crowns and rings on the water, hidden under any solid (`RainCover`). Heavy rain costs 0.05 ms.
- **Bathymetry**: water depth from the voxel terrain, cached as a coarse depth grid per region and rebuilt when terrain chunks change.

### Layers (full design: `docs/water-architecture.md`)

The queryable surface is built from three layers, composed by a single `OceanSurface` facade (implemented over A, B and the breaker faces; C still to blend in). Gameplay never queries a layer directly.

- **A, open swell** (`OceanSwell`, implemented): global analytic waves. Near land it is the incident field for B and C.
- **B, coastal modulation maps**: per-region maps from bathymetry that scale and rotate swell components (sheltering, shoaling, refraction). Analytic and deterministic. The GPU low band applies the same maps.
- **C, active local sim**: the voxel-water shallow-water solver, run near the player and near obstacles, with open edges driven by A + B. It handles reflection, run-up, flow around islands and breaking. Moving bodies inject displacement into it (two-way coupling). Outside C, bodies only feel buoyancy. Use it nearshore only: shallow water equations get deep-water wave speed wrong.
- Voxel terrain and static Jolt shapes supply bathymetry and the solid mask to B and C. Terrain edits rebuild B and wake C.
- **Voxel water** (`docs/voxel-water.md`): every voxel carries a water fill. Voxels decide *where* water is: sea fill at generation, edits, and later the flow sim. The swell and FFT decide *how* the open-sea surface moves. The ocean surface is drawn only over columns whose top sea voxel holds water. C is the voxel flow sim.
- **Much later:** flooding enclosed vehicle volumes (e.g. a submarine hull breach). Interior water lives in the vehicle frame; inflow is driven by the outside head from `OceanSurface`; flooding changes mass, centre of mass and buoyancy.

### CPU side

- **Ocean waves**: wind-generated swell from a physical spectrum (JONSWAP / Pierson–Moskowitz) parameterised by local wind speed, direction, fetch and duration. Only the large, physics-relevant wavelengths. Wind changes blend the spectrum over time, never snap.
  - Depth-aware dispersion `ω² = g·k·tanh(k·h)`: waves slow, shorten and steepen in shallow water.
  - Terrain effects: shoaling (height grows near shore), refraction (crests bend to follow depth contours), sheltering behind land, and breaking when steepness/depth limit is exceeded (breaking events feed GPU foam).
  - Exposes `SampleHeight(x, z, t)`, `SampleNormal`, `SampleVelocity` for buoyancy, vehicles, AI, audio.
  - Deterministic from seed + time + wind/terrain state, so it is cheap to evaluate anywhere and network-friendly.
- **Currents**: wind-driven surface currents and tidal flow, steered by terrain. Advect floating objects, foam and voxel water.
- **Voxel water flow**: physics-based, not a simple fill-level automaton. Shallow-water equations (virtual pipe / height-field model) for surface flow with momentum, plus volume-conserving cell transfer for 3D cases (caves, waterfalls, filling enclosed spaces).
  - Forces: gravity down terrain slope, hydrostatic pressure, wind stress on the surface, bed friction by terrain material, inflow from currents/rivers.
  - Mass is conserved exactly; velocities are stored per cell so water carries momentum (overshoot, sloshing, waves running up beaches).
  - Only active cells are simulated; settled regions sleep. Chunked, jobified, double-buffered, CFL-limited substeps.
  - The ocean acts as an infinite reservoir at sea level: voxel water connected to the ocean equalises to it rather than being simulated cell by cell.
  - Terrain edits wake neighbouring water chunks.
- **Buoyancy**: vehicles sample the CPU ocean and voxel water at hull sample points (submerged volume, drag, lift). Forces applied through Jolt.

### GPU side

- High-frequency FFT cascades layered on top of the CPU swell (only frequencies above the CPU cutoff, so the visual surface never disagrees with physics).
- Foam: Jacobian / wave-folding foam, whitecaps, persistent foam textures that advect and decay, shoreline foam from terrain depth, vehicle wakes and splash injection.
- Shading: physically based, subsurface scattering, reflections (SSR + probe fallback), refraction, depth-based absorption, caustics, underwater fog and god rays.
- Spray/mist particles in compute.
- Surface rendering: projected grid or clipmap LOD around the camera, seamless with voxel water meshes near shore.

### CPU ↔ GPU contract

- Both sides share spectrum parameters, wind field, depth grid, seed and time. GPU high-frequency detail also follows local wind and depth. The GPU low band must match CPU output exactly; GPU only adds detail.
- CPU uploads interaction data (wake sources, splashes, voxel water surface) once per tick into persistent buffers.
- The GPU never feeds back into gameplay. No GPU readbacks in the frame loop.

## Voxel terrain

- Chunked world (e.g. 32³ voxels), streamed around the player, generated by jobs.
- Density/material per voxel for smooth terrain (surface nets / dual contouring / marching cubes; pick one and keep it consistent). Material IDs drive resources for crafting.
- LOD for distant chunks with seam stitching.
- Meshing on jobs (or GPU compute), uploads batched. Collision meshes built for Jolt per chunk, rebuilt only on edit.
- Edits are local: only dirty chunks remesh. Edits notify water.
- Voxel storage compressed (palette / RLE) for memory; uncompressed only while active.

## Vehicles

- Built from parts placed on a grid (structure, engines, wheels, propellers, floats, control parts).
- Each vehicle is a Jolt compound body; mass, inertia and centre of mass derived from parts.
- Parts are data-driven definitions (stats, mesh, attach points, buoyancy volume).
- Damage and detachment split compounds into new bodies.

## Crafting

- Data-driven recipes and items (no hard-coded content in code).
- Resources come from terrain voxel materials and world objects.
- Vehicle parts are crafted items.

## Code layout (proposed)

```
BRUTAL/            upstream framework, do not modify
src/
  Game/            entry point, game loop, tick scheduling
  Engine/          our engine layer: renderer, assets, input, jobs glue
  Water/           CPU ocean, voxel water flow, GPU water renderer
  Terrain/         voxel storage, generation, meshing, streaming
  Vehicles/        parts, builder, physics integration
  Crafting/        items, recipes, inventory
  Weather/         pressure systems, weather snapshot map
  Debug/           ImGui panels, profiling
shaders/           Slang shaders
data/              part, item, recipe definitions
```
