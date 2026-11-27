# BRUTALGame

Interplanetary space ship game in C# (in the vein of Star Citizen / Space Engineers), built directly on the BRUTAL framework (`BRUTAL/`, cloned from https://github.com/ahwootech/BRUTAL).

**Pivot in progress (2026-11-25).** The project was an island ocean game. The design for the space game is in `docs/pivot/` (start at `docs/pivot/README.md`). Where it lists open questions, ask rather than assume. The island and water code is being cut down (`docs/pivot/inventory.md`); docs under `docs/` other than `docs/pivot/` describe the old island game.

Core pillars, in priority order:

1. **Performance** – every system is designed for frame budget first. No feature ships if it blows the budget.
2. **Scale** – a solar system of ~1000 km planets and moons, seamless from orbit to ground. Double-precision frames; Jolt runs in local float bubbles.
3. **Multiplayer** – server-authoritative from day one. Single player is an in-process listen server (~8 players); 32-64 players on dedicated servers.
4. **Ship building** – hulls are 3D modelled (CSG brushes), interiors cut away; components are prefab or player-made (a prefab is a saved player build).
5. **Voxel planets** – fully editable, chunked, streamed spheres.
6. **Crafting** – resources from terrain feed parts and ships.

## Rules for Claude

- Build and test when implementing (the user asked for this on 2026-11-26; it applies to subagents and sessions doing implementation work too). Don't build for docs or design-only work. Prefer quick, targeted tests: build once, then `dotnet test` only the touched projects with `--no-build --filter "Category!=Perf"`.
- Read `~/.claude/cs-standards.md` before writing C#.
- `BRUTAL/` is the upstream framework. Do not modify it; game code lives outside it. If the framework needs a change, say so and propose it separately.
- BRUTAL is deliberately small: no renderer, scene graph or editor. We write our own. Do not pull in a general-purpose engine.
- Before adding a NuGet package, check if BRUTAL already covers it.

## Tech stack (from BRUTAL)

| Need | Library |
| --- | --- |
| Rendering / compute | `Brutal.Vulkan`, `Brutal.Vulkan.Vma`, `Brutal.Slang` / `Brutal.ShaderC` |
| Window / input | `Brutal.Glfw` |
| Physics | `Brutal.Jolt` (single precision: `RVec3` binds as `float3`) |
| Threading | `Brutal.Concurrency` (`JobScheduler`, `IJob`) |
| Memory | `Brutal.Memory`, `Brutal.Collections` (`NativeList`, `FreeList`) |
| Maths | `Brutal.Numerics` (`double3`, `doubleQuat`, `double4x4`), `Brutal.Maths` |
| Audio | `Brutal.Fmod` |
| Debug UI / profiling | `Brutal.ImGui`, `Brutal.ImPlot`, `Brutal.RenderDoc` |
| Assets | `Brutal.Gltf`, `Brutal.Ktx`, `Brutal.Texture`, `Brutal.MeshOptimizer` |
| Networking | `Brutal.RakNet`, `Brutal.ZLib` |

## Performance rules

- Zero allocations in per-frame / per-tick paths. Use unmanaged memory, `Span<T>`, `NativeList`, pooled buffers. No LINQ, no boxing, no closures, no `params` in hot paths.
- Data-oriented layout: structs of arrays for bulk data (voxels, entities, frames, brushes). Keep hot data contiguous and cache friendly.
- `unsafe` and pointers are fine where they measurably help.
- Heavy work goes through the job system; main thread only coordinates. Jobs must not share mutable state without explicit ownership.
- Fixed simulation tick (60 Hz, integer `TickIndex`), decoupled from render rate. Rendering interpolates, in double.
- GPU work is batched: indirect draws, persistent mapped buffers, minimal pipeline/descriptor changes. (Terrain still draws one `DrawIndexed` per chunk; GPU cull + indirect count is milestone M1.)
- Every major system exposes timings in an ImGui profiler panel. Any perf claim needs a measurement.
- Consider cost of anything that runs on tick or update before adding it.

## Frames, zones and precision (`docs/pivot/frames-and-zones.md`)

- **Frames:** a double-precision `FrameTree`: Solar root > body inertial > body surface (rotating) > ship grids (nestable, depth ≤ 3). Every entity stores `double3` relative to its parent frame.
- **Persistent positions** (edits, entities, saves, wire) are int64 at 1/1024 m in their frame. Double only at runtime, float only inside a bubble, chunk or draw.
- **Orbits:** celestial bodies on Kepler rails from the integer tick (exact forever). Patched conics: only the dominant body pulls. Coasting ships with no bubble ride rails too.
- **Physics bubbles:** one pooled Jolt `PhysicsSystem` per cluster of interacting things inside one frame, axes = frame axes, soft radius 2 km, hard 4 km. Shared job pools; asleep scenes are not stepped. Bubbles merge and split; surface bubbles rebase in 32 m steps.
- **Rotating surface frames** apply centrifugal and Coriolis to awake dynamic bodies. Scene gravity is set per scene, plus a per-awake-body delta; never `AddForce` gravity on every body (it wakes them).
- **Ship grids:** players inside a ship live in grid coordinates; who is in the grid is decided by `GridVolume` (air with portals closed) and grounded contact.
- **Warp:** the warp exit point is where a ship picks its frame and bubble (`docs/pivot/warp.md`).
- **Rendering is camera-relative:** view matrix has no translation; per-draw `relOrigin` computed in double, then cast.

## Planets (`docs/pivot/planets.md`)

- Cartesian voxels axis-aligned in the rotating planet frame: 1 m voxels, 32³ chunks, the existing octree, Surface Nets, skirts, 12 B packed vertex. Only the SDF changes (local height − radial altitude).
- Voxels only in a shell around the surface; one octree to LOD 12, then a baked cube-sphere impostor.
- **The world is authored (D83).** Each body's seed is fixed by us, never entered by the player. The seed gives the generated layout; we then change it by hand (cities, mining outposts, reshaped land) and ship that as an authored layer in `data/`.
- The untouched surface is a pure function of seed + authored layer + position: no storage beyond the shipped layer, and it shows at every level of detail. Player edits are a separate layer on top: nothing stored but edits (op replay, as now); busy chunks compact to snapshots.
- Detail ladder by distance: point of light > disc > impostor with atmosphere and rough outline > function terrain (each LOD evaluates only the noise octaves it can show) > edits, applied only close up (op radius ≥ 2 voxels at that LOD, within the edit radius).
- Radial gravity, per-position up. Atmosphere: exponential density per body for drag.

## Water

Cut to the ocean only (`docs/pivot/inventory.md`). Kept: analytic swell, GPU FFT cascades, ocean shading, **shore wash** (GPU swash sheets, edge foam, surf foam) and **breaking waves** (slice 1: the breaking face, `docs/pivot/briefs/breaking-waves-brief.md`), on a sea sphere per planet.

- `OceanSurface.Sample(double3 planetLocal, long tick)` has one global definition (fixed tangent tiles), so server and clients agree on buoyancy.
- Each 64 km sea tile has its own swell from its own wind (`SeaSwells`): the sphere weather's mean over the tile, read each 60 s epoch and quantized to 1 m/s and 10°; a new key crossfades in over 45 s from the epoch's start, its components seeded by tile and key. The surf carrier's phase is closed form. A tile's sea is a pure function of seed, tile and tick, so a tile made late agrees with one live all along; only tiles round the sim's anchors (bubble, view, entities near sea level) are live. Each GPU slot draws its own tile's waves.
- Shore wash reads a depth window built from the planet generator plus edits (`IShoreDepth`), not from voxel water.
- Breaking waves: coast regions (`src/Water/Coast`) are 1 km windows of 4 m cells on a fixed lattice in each `SeaTiles` tile's plane, overlapping their neighbours by 128 m. Each is baked on a job from the generated ground (shores are fixed, D30): eikonal phase, shoaling, break clock, solved 128 m past the window so neighbours agree; only water joined to the open sea carries the wave. At most 8 resident, loaded within 3 km. The maps are kept while the swell's carrier stays within 1°, 1 % of frequency and 5 % of height of the one they were baked for (the drawn height follows through a uniform); past that they are baked again and cross-faded. The face (`BreakerFace`, `ocean_breaker.glsl`: the surf carrier, crest lift, pitch, whitewater) is a pure function of the atlas and the tick: no per-tick state, nothing on the wire.
- The carrier is drawn only in water shallower than 8 x its deep-water height. The grid draws the face out to 16-32 m cells, filtered at a spacing that follows the camera's height without the clipmap's steps; past that the fragment shader shades the surf zone as a time-averaged whitewater band from the atlas. The swash arrives on the regions' carrier phase. A carrier belongs to one sea tile and is handed over in a 540 m band at a tile edge (`SeaTiles.Hand`), phases matched under the camera.
- Judge the face by second differences (`BreakerStability` kicks, `--surf-beach`, `--surf-climb`): a pop is a tick out of step, not a big step.
- The face is drawn only: it is not in `OceanSurface.Sample`, and the server bakes nothing.
- Gone: breaker lips, bores, spray, the swell's own coastal maps (layer B refraction of every component), voxel water and its flow sim, river, lakes, wind sea, water fog, SSR, rain.
- The GPU never feeds back into gameplay. No GPU readbacks in the frame loop.

## Ships (`docs/pivot/ship-hulls.md`)

- A ship is a blueprint: an ordered list of quantized CSG **solids** (add, subtract, paint) plus component placements and brush strokes. Only this is saved, sent, edited or damaged.
- The drawn surface is exact polygon CSG of the solids (`HullCsg`), never the grid. Every peer also bakes 0.25 m SDF bricks for occupancy (rooms, connectivity), mass moments and convex exterior collision; the grid draws only a layered hull's inner layers.
- **Brushes** are small visual flair (a stamp mesh or a swept profile) drawn along strokes that stick to the hull: drawn from their own geometry, counted for mass only (`docs/pivot/ship-skin-and-layers.md`).
- The server is authoritative for mass, rooms, splits and collision.
- Components (thrusters, seats, tanks, doors…) attach to the hull; player-made ones are a modelled housing plus a function core. Prefabs are saved blueprints keyed by content hash.
- Each ship is a Jolt compound body. Damage has three forms: holes (subtract solids), dents (displacement records) and deformation (plastic hinges on the structure graph); disconnected pieces split into new bodies (`docs/pivot/combat-and-sensors.md`).
- Hulls can also use closed mesh solids (full mesh editing) inside the CSG list, so they stay cuttable. Wiring has two media: power and one-way, splittable data.
- Interiors hold air per room (`docs/pivot/ship-atmosphere.md`); ships can carry particle, energy and gravity shields.

## Networking

- Server-authoritative 60 Hz sim, 30 Hz snapshots. The wire speaks frames, never bubbles.
- Planets, orbits, terrain, weather and swell are deterministic from seed + tick: zero bandwidth.
- Clients predict only what they own, in its local frame (on foot: grid or surface coordinates; piloting: re-step a small local Jolt scene).
- Keep a `Sim` assembly with no Vulkan reference, so a headless server can run it.

## Crafting

- Data-driven recipes and items (no hard-coded content in code).
- Resources come from terrain voxel materials and world objects.
- Ship parts and components are crafted items.

## Code layout (target)

```
BRUTAL/            upstream framework, do not modify
src/
  Game/            client shell, entry point, game loop
  Sim/             headless simulation: entities, tick scheduling (no Vulkan)
  Frames/          FrameTree, Kepler rails, gravity, frame maths (no Jolt)
  Physics/         PhysicsRuntime, PhysicsScene, bubbles, transfers
  Engine/          renderer, assets, input, jobs glue
  Terrain/         voxel storage, planet generation, meshing, streaming
  Water/           ocean swell, FFT, shore wash
  Ships/           blueprints, solids, bake, mass, rooms, components (parts, pipes)
  Chemistry/       gases, reactions, flow networks, nozzles (shared by designer and game)
  Net/             transport, replication, interest management
  Crafting/        items, recipes, inventory
  Debug/           ImGui panels, profiling
shaders/           shaders
data/              component, item, recipe, material definitions
```
