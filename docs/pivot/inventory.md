## Codebase inventory for the space pivot

### Recommendation

Cut the water stack down to four pieces: **analytic swell (layer A), GPU FFT detail, ocean shading, and a GPU-only shore wash**. Shore wash means swash sheets, the water's edge foam line and the surf-foam map. It is fed by a **depth window built from the planet generator and edits**, not from voxel water or the chunk column summaries. Everything else in the water stack goes: layer B, breakers/lips/spray/bores, voxel water and zones, river/lake, wind sea, fog volume, SSR and rain.

Do it in six steps (end of this section), leaves first. Swash depends on layer B and the breakers in exactly two places. Re-point those two lookups before deleting layer B (step 6a).

Why:
- Shore wash is already GPU-only (`swashSheet`, ocean_common.glsl:1466–1562). It needs only a depth texture, the carrier uniforms and three external lookups. All three can be replaced analytically.
- The measured cost being removed is about 45 % of the GPU frame in the surf scene. CPU ticks near water drop by about 1–3 ms.
- What stays is deterministic from seed + tick, so multiplayer has nothing to replicate except per-planet `SwellParams`.

Rejected alternatives:
- Keep layer B on planets: a 4 km eikonal solve per region plus 44 MB of buffers, and a 300–500 ms rebuild per recentre, all for refraction that only matters on curved coasts.
- Keep the voxel-water sea and draw swash from it: 0.65–2 ms a tick of sim, plus edit hitches, for something that is visual only.
- Build the depth from chunk column scans (today's `ChunkJob.SummariseColumns`): it scans along +Y, so it is wrong everywhere on a sphere except near one axis.
- Run swash on the CPU: it never feeds gameplay, so the CPU would be doing visual work.

### What shore wash depends on today (exact)

| Need | Where today | Keep / replace |
|---|---|---|
| Swash sheets (run-up events, Shen–Meyer lens, 4 bores remembered) | `ocean_common.glsl` `swashSheet` 1466–1562, applied in `oceanFinish` 1825–1837, `p.swashEdge` 1896 | KEEP |
| Edge foam line (thin water + swash edge) | `ocean.frag` 22, 29, 96–99 (`edgeFoam`, `vSwashEdge`); `ocean.vert` 22/54 | KEEP |
| Foam left behind by the up-rush | `ocean_surf_foam.comp` (swash `front` × `SWASH_FOAM`), read by `surfFoamAt` 1565 | KEEP the swash term. Replace the `surfBandAt` gate (carrier atlas) with a depth band `-SWASH_MAX ≤ d ≤ SWASH_SEA + 1`. Drop the `breakerFace` term |
| Water depth | `waterDepth`/`depthMapAt` 1415–1464 ← `BathymetryTexture` ← `BathymetryGrid` ← `ChunkJob` column tops/beds/sea flags (sea flags come from `VoxelChunk.Water`) | KEEP the texture with one layer and the R channel only. REPLACE the source with `ShoreDepthGrid` from the planet generator + edits |
| Carrier phase, energy, ω, sets | `frame.carrier`, `frame.carrierChop` ← `OceanSwell.GpuCarrier`; `groupEnvelope` | KEEP |
| Phase lag + shoal amplitude at the shoreline | `coastalSample(shore)`, `coastalRef(0, jc)` → layer B `CoastalField` SSBOs (binding 5) | REPLACE with the Snell phase plus an analytic breaker height (sketch below) |
| Dissipation since the break began | `breakDistanceAt` → `BreakerSet` grid (binding 6) | REPLACE with an analytic surf width `(H_b/γ)/slope` |
| FFT calm near shore, shore thickness fade | `oceanFinish` 1868–1877 (`SHORE_CALM/FULL`, `bed`) | KEEP |
| Pits cut off from the sea (no ocean drawn) | `BathymetryTexture.Flood`/`UpdateShoreReach`, `Pit` sentinel | KEEP the flood. Seed it from generator sea cells instead of voxel sea flags |

Re-pointed swash (shader-only, cheaper than today):

```glsl
// Snell: a straight shore conserves k0·sin(angle) along it, so the arrival phase at a shoreline point is the open
// carrier's phase there. Exposure kills run-up on lee shores. One uniform replaces layer B + the break grid.
// frame.shore: xy = carrier travel dir, z = breaker height H_b (CPU, Komar–Gaylord from carrier H0, T), w = Hs.
float spatial  = frame.carrier.x * dot(frame.shore.xy, shore);
float exposure = sqrt(max(dot(frame.shore.xy, -normalize(seaward)), 0.0));
float unbroken = frame.shore.z * exposure * exp(-(frame.shore.z / WAVE_BREAKING_GAMMA) / slope / SWASH_DISSIPATE);
```

Add one more term so the open swell does not pass through beaches now that layer B is gone: a depth-limited cap `min(1, 0.55·h/Hs)` on the swell components in `swellPart`. Mirror it on the CPU in `OceanSurface` so buoyancy matches. Then add spill foam where the cap is below 1. Both are a single depth read that the vertex shader already does.

### Data structures (sketch)

```csharp
/// Planet-fixed tangent frame the sea is evaluated in; one per surface physics zone (interface to the zones design).
public readonly struct SeaFrame(double3 origin, double3 east, double3 up, double3 north, double seaRadius)
{
    public readonly double3 Origin = origin;   // planet-local, on the sea sphere
    public readonly double3 East = east, Up = up, North = north;
    public readonly double SeaRadius = seaRadius;
    public float2 ToLocal(in double3 planetLocal) { var d = planetLocal - Origin; return new((float)dot(d, East), (float)dot(d, North)); }
}

/// Deterministic, thread-safe depth below the sea sphere (negative on land): generator height + edit spheres.
/// Replaces TerrainCoastalDepth's idea (analytic surface + brush spheres) and the chunk column summaries.
public interface IShoreDepth { float Depth(in double3 planetLocal); }

/// Was BathymetryGrid: 512² × 2 m window in SeaFrame coords, rebuilt in strips on jobs when the camera moves 128 m.
public sealed class ShoreDepthGrid
{
    public const int Resolution = 512;
    public const float CellSize = 2f;
    public readonly Half[] Depth = new Half[Resolution * Resolution];
    public readonly byte[] Reach = new byte[Resolution * Resolution]; // BathymetryTexture.Flood result, sea seeds from generator
    public int2 OriginCell;                                           // SeaFrame cells; no float world origin
}

// OceanSwell API delta: phases rebased to an anchor in double, so the GPU sees small coordinates.
public int CopyGpuWaves(Span<GpuWave> destination, double renderTime, double2 anchor); // phase += k·anchor, wrapped
public float SampleHeight(float x, float z);                                           // x, z in SeaFrame metres (≤ ~16 km)
```

Trimmed GPU frame: keep `viewProj` (camera-relative), `cameraPos` (relative, w = time mod 3000 s, the LCM of `LoopPeriod` 1000 and `LACE_WRAP` 600), sun/sky/water colours, `cascadeL/KLo`, `foamParams`, `depthRect` (relative), `carrier`, `carrierChop`, plus new `shore` and `planet` (x = sea radius for the bend `y -= r²/2R`, y = Hs). Waves[512] stay. Drop `coastRect`, `refEnergy/Chop/K0`, `coastDir/Prev/Far/FarPrev`, `windSea` and `counts.yzw`.

Descriptor set: keep bindings 0–4 (frame, disp, deriv, foam, depth) and 8 (surf foam). Remove 5 (coastal), 6 (breakers), 7 (spray), 9 (lip bases), 10 (carrier atlas), 11 (rain cover), 12 (splashes), 13 (zone flow), and the fog and SSR sets.

### Cost of what stays (estimates to measure, except where marked measured)

| Item | Cost |
|---|---|
| `OceanSwell.SetTime` per tick | 0.004 ms (measured, docs/water-architecture.md) |
| Depth cap on CPU samples | +1 bilinear grid read per sample (~10 ns est.) |
| `ShoreDepthGrid` strip per 128 m of travel | 512×64 cells × 1 height eval ≈ 5–10 ms on jobs (est.); 64 KB R16F upload |
| Weather tick (if kept) | 0.0002–0.0003 ms (measured) |
| GPU ocean pass, bare swell | 0.0573 ms (measured "Off" column, docs/breaker-review.md §4). Swash + depth cap add ~0.005–0.01 ms (est.) |
| Ocean FFT | 0.030 ms (measured) |
| Surf foam compute, swash only, depth band | ~0.003–0.008 ms (est.; was 0.0174 with breakers) |
| GPU frame, surf scene | 0.2046 → ~0.11 ms (measured bare-swell frame 0.1126, plus swash) |

### Classification by file

`src/Game`
| File | Class | Change |
|---|---|---|
| GameApp.cs | ADAPT | Strip ~60 %: water, zones, breakers, coastal, rain, fog, SSR, wind sea, 8 test scenes. Camera-relative viewProj. Sky owner moves out of `OceanRenderer`. Constructor goes from 38 args to a config struct |
| Program.cs | ADAPT | Keep `--wind --swell --cam --bench --render-scale --hide-ui --fly-test --edit-test --foam-debug`. Cut ~30 flags (`--water-*`, `--*-test` water scenes, `--surf-*`, `--breakers`, `--no-faces/lips`, `--fold/ribbon/chain-lips`, `--no-coastal`, `--bench-rain/fog/ssr`, `--no-ssr/fog`, `--rain`, `--no-voxel-swell`) |
| BreakerBores.cs, CoastalSystem.cs, Coastline.cs | CUT | |
| TerrainCoastalDepth.cs | ADAPT → `PlanetShoreDepth : IShoreDepth` | Same approach (analytic surface + edit spheres), radial |
| WeatherSystem.cs | ADAPT later | Unhook now: swell gets per-planet `SwellParams`, sky from the planet's sun |
| TerrainBrush.cs | ADAPT | Ray in the zone frame; op centre planet-local double |
| EditTest.cs | ADAPT | Planet up vector; still the physics + edit regression |
| FlyTest.cs | ADAPT | Keep `FrameSections` and percentiles; path becomes surface/orbit flight (drops bathymetry, coastal and rain-cover args) |
| GpuBenchmark.cs | ADAPT | Generic feature toggle in place of layer B |
| CaveTest, FloodTest, LakeTest, PondTest, RiverTest, RoofTest, SeasideTest, SurfTest | CUT | |

`src/Engine`
| File | Class | Change |
|---|---|---|
| Jobs/WorkerBudget, Time/FixedClock, FrameStats, TimingRing | KEEP | FixedClock's `TickIndex` is the network tick |
| GpuBuffer, GpuTimer, ImGuiLayer, PipelineCache, PipelineStats, SceneTarget, ShaderLoader, Swapchain, VulkanContext | KEEP | |
| Renderer.cs | KEEP | The opaque/water pass split stays as opaque/transparent (ocean, later ship glass) |
| SceneLighting.cs | ADAPT | Becomes the sun/sky owner (today the fields live on `OceanRenderer`) |
| DebugShapeRenderer.cs | ADAPT | Camera-relative instances |
| Scene/FlyCamera.cs | ADAPT | `double3` position, rotation-only view, keep the infinite reverse-Z projection |
| RainCover.cs, WaterFogVolume.cs, ScreenReflections.cs, WaterSplash.cs | CUT | |

`src/Physics`
| File | Class | Change |
|---|---|---|
| BodyHandle, JoltRuntime, RayHit, StaticMeshShape, TriangleBvh | KEEP | Shapes and BVH are local-space |
| ObjectLayers.cs | ADAPT | Ship, character and debris layers |
| PhysicsWorld.cs | ADAPT | Zone-local `Vector3` only. Gravity is fixed `(0,-9.81,0)` at line ~88; make it per zone or per body radial. Add compound/convex shapes for ships. One instance per zone or zone layers (zones design decides) |

`src/Terrain`
| File | Class | Change |
|---|---|---|
| Meshing/* (SurfaceNetsMesher, ChunkMesh, PackedTerrainVertex, TerrainVertex) | KEEP | Chunk-local, frame-agnostic |
| Voxels/Noise.cs | KEEP | Add sphere sampling with integer lattice offsets (see hazards) |
| Voxels/VoxelMaterial.cs | KEEP | Extend for resources |
| Voxels/ChunkCoord.cs | KEEP planet-local | Add `double3 OriginD`. Never use it in a solar frame |
| Voxels/ChunkConstants.cs | ADAPT | Remove the `MinChunkY = -2 / MaxChunkY = 3` slab |
| Voxels/VoxelChunk.cs | ADAPT | Drop `Water[]` (39 KB per chunk) |
| Voxels/TerrainGenerator.cs | ADAPT → PlanetGenerator | density = r − (R + h(dir)). Keep the material, overhang and all-air/all-rock early-outs (radial min/max). Cut islands, lake, river, `FillSea` |
| Voxels/TerrainParams.cs | ADAPT → PlanetParams | Radius, sea radius, amplitudes |
| Editing/BrushOp.cs | ADAPT | Centre planet-local double or chunk-anchored; per-region op index (today `ChunkJob.SetOps` scans every op ever made, which grows without bound on a 64-player server) |
| Voxels/RiverPath.cs, Water/* (10 files, 4,365 lines), TerrainGenerator.cs (0-byte stray at project root) | CUT | |

`src/Terrain.Runtime`
| File | Class | Change |
|---|---|---|
| TerrainWorld.cs | ADAPT | Octree root at the planet centre (MaxLod ~16). Radial shell test replaces the Y slab. Double distances. Cut the bathymetry merge (`MarkBathymetry`, `MergeLeaves`, rebuild bands). Collision set per zone |
| ChunkJob.cs | ADAPT | Drop `SummariseColumns`, `ColumnTops/Beds/SeaFlags` |
| BathymetryGrid.cs | ADAPT → ShoreDepthGrid | Drop zones, discs, fades, sea-state LOD merge (~60 % of 678 lines). Keep recentre/shift and regions |
| BathymetryTexture.cs | ADAPT | One layer, R16F, no zone/level uploads. Keep strip uploads, GPU shift and `Flood` |
| Rendering/RangeAllocator, TerrainLighting | KEEP | |
| Rendering/TerrainRenderer.cs | ADAPT | Push `origin − camera` computed in double; frustum test relative; drop the fog binding |
| Rendering/VoxelWaterRenderer.cs, Water/VoxelWaterSystem.cs, ZoneFlowSim.cs, WaterJobs.cs | CUT | 4,860 lines |

`src/Water`
| File | Class | Change |
|---|---|---|
| Ocean/Jonswap, SplitMix64, SwellParams, WaveSet | KEEP | |
| Ocean/OceanSwell.cs | ADAPT | Strip the coastal/surf paths (`Coastal*`, `SampleFields`, `CoastalWaves`, `SurfCarrier`, `CarrierDirection/Depth`, `SampleSurf`, `SurfVariance`, `CarrierBreaking`, `Breaking`); ~1,379 → ~700 lines. Keep `GpuCarrier`, `GroupEnvelope`, sets. Add the depth cap and anchored `CopyGpuWaves` |
| Ocean/OceanSurface.cs | ADAPT | Facade over swell + depth cap only; stays the single gameplay query |
| Ocean/BreakerFace, BreakerFaces, BreakerLip, BreakerSet, CoastalBuilder, CoastalField | CUT | 2,410 lines |
| Rendering/OceanFft.cs | KEEP | Add the per-cascade anchor offset (camera mod L) |
| Rendering/OceanRenderer.cs | ADAPT | Cut lips, patch, spray, coastal/breaker/zone/splash buffers, rain, fog, SSR (~1,461 → ~550). Add the curvature bend and a coarse far ocean shell for orbit views |
| Rendering/RainRenderer.cs, WindSeaBands.cs | CUT | |

`src/Weather`: KEEP the library (pure, 544 lines, tests) but park it unhooked. Spherical adaptation (systems placed by unit direction, great-circle distance) comes later.

`src/Debug`
| File | Class | Change |
|---|---|---|
| ProfilerPanel.cs | ADAPT | Constructor loses RainCover, fog and VoxelWaterRenderer; drop the "Fog fill" row |
| OceanPanel.cs (F4) | ADAPT | Swell tuning + sampling perf only; drop coastal, breaker and lip toggles |
| TerrainPanel.cs (F5) | ADAPT | Drop the "Water audit", "Water probe", "Water fog" and "Water reflections" sections |
| WeatherPanel.cs (F6) | PARK | Goes with Weather |
| BreakerOverlay.cs | CUT | |

`shaders/`
| Class | Files |
|---|---|
| KEEP | debug_shape.*, ocean_fft.comp, ocean_h0.comp, ocean_foam_threshold.comp |
| ADAPT | terrain.vert (relative origin); terrain.frag (drop the `water_fog.glsl` include); ocean.vert, ocean.frag (drop rain, zone, fold); ocean_common.glsl (1,905 → ~600: trimmed Frame, open-ocean `swellPart` + cap, re-pointed `swashSheet`, `waterDepth`, `surfFoamAt`, trimmed `oceanFinish`); ocean_shading.glsl, water_shading.glsl (drop fog/SSR); ocean_surf_foam.comp (swash only) |
| CUT (~2,600 lines) | ocean_carrier_atlas.comp, ocean_lip.vert/.frag, ocean_lip_base.comp, ocean_patch.vert, ocean_spray*.{vert,frag,comp} (5), rain*.{vert,frag,comp,glsl} (8), voxel_water.vert/.frag, voxel_water_wind.glsl, water_fog.glsl, water_ssr.glsl, wind_sea.glsl, wind_sea_fft.comp, wind_sea_h0.comp |

`docs/`
- KEEP: job-runner-priority.md, weather.md (parked).
- ADAPT: ocean-gpu-contract.md (drop zones/coastal, add anchor rebasing), terrain-design.md and terrain-lod.md (planet).
- REWRITE: water-architecture.md down to swell + shore wash.
- NEW: docs/shore-wash.md, extracted from breaking-waves.md "Swash and lace foam" and the thirty-third pass.
- CUT (~2,030 lines): breaking-waves.md, breaker-review.md, surf-research.md, voxel-water.md, water-fog.md, water-ssr.md, rain.md.
- CLAUDE.md: the Water section shrinks to match.

### Tests and scenes that die

- **Test files cut (83 tests):** WaterFlowTests 5, WaterPipeTests 29, WaterSimTests 15, WaterSurfaceMesherTests 5, ZoneInjectionTests 5, BreakerLipTests 7, BreakerSetTests 10, CoastalFieldTests 5, LipLifeProbe 1, LipSeedTests 1.
- **Cut inside kept files:**
  - OceanSurfaceTests: `HeightInvertsSurfacePoint_AcrossPitchedWalls`, `FacePartials_MatchFiniteDifferences`, `Face_IsContinuousTickToTick`, `Whitewater_IsContinuousTickToTick_AndNarrowNearTheBeach`, `Pitch_LeavesTheBackOfTheWaveAlone`, `Break_LandsWithinItsCrashDistance`, `WithoutFaces_MatchesTheSwell`.
  - TerrainTests: `IslandHasBeachesAndSeabed`, `GenerationFillsSeaOnlyInAirBelowSeaLevel`, `CraterLakeIsGeneratedFullAndHeldByItsRim`, `RiverRunsFromTheLakeOverItsSillToTheSea`, `RiverIsGeneratedWithItsWater`, `DigKeepsOpenWaterAndPlaceDisplacesIt`, `InlandPitDugBelowSeaLevelIsDry`.
  - BathymetryGridTests: `Ground_IgnoresLandOverTheSea`.
- **Need new reference data:** `Lod0OutputMatchesReference`, `CoarseGeneratedSurfaceFollowsHeightField` (radial).
- **Kept as they are:** Physics.Tests (all 9), PackedTerrainVertexTests, the TerrainTests mesher/seam/LOD/allocation tests (~20), OceanSwellTests (13; `Carrier_*` and `Sets_*` stay because swash uses them), WeatherTests.
- **Scenes cut:** `--cave-test`, `--flood-test`, `--lake-test`, `--pond-test`, `--river-test`, `--roof-test`, `--seaside-test`, `--surf-test` / `--surf-cam`. `--edit-test`, `--fly-test` and `--bench` survive in adapted form.
- **Panels:** BreakerOverlay dies. Ocean, Terrain and Profiler panels shrink. Weather is parked.

### Float-precision and int hazards, by file

| File | Hazard | Severity at 1,000 km |
|---|---|---|
| Engine/Scene/FlyCamera.cs | `Vector3 Position`; `CreateLookAt(Position…)` bakes world translation into viewProj | High: 6–12 cm vertex jitter |
| Game/GameApp.cs | One world-space `viewProj` for every renderer; `CameraXZ` Vector2; `(float)(renderTime % 1000.0)` | High |
| Terrain.Runtime/Rendering/TerrainRenderer.cs + terrain.vert | Push constant `slot.Origin` world float, `world = origin + local`, then `viewProj * world`; `Visible()` on world floats | High: fix by pushing `origin − camera` from double |
| Terrain/Voxels/ChunkCoord.cs | `WorldOrigin` Vector3 is exact for multiples of 32 m only below 2^24 m (16,777 km), so OK planet-local. `int X` overflows in a solar frame (1 AU / 32 m = 4.7e9) | Planet-local OK; solar frame fatal |
| Terrain/Voxels/TerrainGenerator.cs + Noise.cs | `Height(float x, float z)`: `x * freq` loses ~1e-3 noise units at 1e6 m, worse for high octaves; flat 2.5D | Medium: use the integer lattice cell from the chunk origin plus a float fraction |
| Terrain/Voxels/ChunkConstants.cs, TerrainWorld.cs | `WorldMinY/MaxY` slab (−64..192 m), `RootRadius 2 @ MaxLod 4` (~1.3 km view), `Vector3 _camera`, float `DistanceToNode` | Fatal for a sphere |
| Terrain/Editing/BrushOp.cs | `Vector3 Centre` world float (6 cm quantised); used as replicated state | High for networking determinism |
| Physics/PhysicsWorld.cs | All `Vector3`; `AddStatic(shape, position)` world; gravity −Y; `CastRay` world | High: zone-local only |
| Physics/TriangleBvh.cs | Local-space Min/Max | OK if shapes stay local |
| Engine/Rendering/DebugShapeRenderer.cs | World float instances | Medium |
| Terrain.Runtime/BathymetryGrid.cs / BathymetryTexture.cs | `Vector2 Origin` world XZ, `SeaLevel = 0` const on the Y axis | High: replace with SeaFrame + radial depth |
| Water/Ocean/OceanSwell.cs | `SampleHeight(float x, float z)` world; phase `k·x` (8 m waves at 1e6 m lose ~0.06 rad) | High: SeaFrame coords + anchored phases |
| Water/Rendering/OceanRenderer.cs | Clipmap snap `MathF.Round(cameraPos.X / snap)` world float (line 1114); `CameraPos.w = (float)renderTime` (line 946; ulp 0.06 s after ~11 days of server uptime); flat plane at y = 0 | High |
| shaders/ocean_common.glsl + OceanFft.cs | `textureLod(dispMap, x0 / L)` with world `x0` (uv ~2e4: half-texel error); `k·x0` with world `x0`; eddy/lace noise on `cameraPos.w` time | High: pass `camera mod L` per cascade, wrap time at 3000 s |
| Weather/WeatherMap.cs | Float XZ, flat 32 km snapshot | Low precision risk; needs a sphere |
| Game/TerrainBrush.cs | Ray from a world-float camera | Medium |

### Ordered cut list (least breakage first) and time recovered

0. Tag the current branch `island-final` so the island game stays reachable.
1. **Test scenes and flags:** the 8 scenes above, Program.cs flags, GameApp fields, LipLifeProbe and LipSeedTests. Nothing depends on them. Recovers 0 at runtime and about 1,360 lines.
2. **Breakers:** BreakerSet/Faces/Face/Lip, BreakerBores, BreakerOverlay; shaders lip, lip_base, patch, spray×5; `breakerFace` paths in ocean_common; OceanSurface's face hook; OceanPanel toggles; tests. Swash keeps working once `breakDistanceAt` is a constant (the analytic surf width). Recovers **0.075–0.54 ms per tick** (BreakerSet update, measured from 2k to 9.4k surf texels) and **GPU ~0.05 ms** (lip bases 0.027, spray draw 0.007, surface surf extra ~0.02 of 0.038; measured).
3. **Voxel water and zones:** VoxelWaterSystem, ZoneFlowSim, WaterJobs, VoxelWaterRenderer, Terrain/Water/*, RiverPath, lake/river/sea fill, `VoxelChunk.Water`; voxel_water* and wind_sea* shaders, WindSeaBands; zone bindings 12/13; BathymetryGrid zone arrays; BathymetryTexture layer 1. Interim sea seeds come from "ground below sea" plus the existing flood. Tests: 59. Recovers **0.65–1.3 ms per tick** of zone sim (measured 0.65 to 1.25 ms at 1.6k to 5.2k cells) **plus 0.3–1.3 ms per voxel step** (river 0.8–1.3 ms at the 32 m radius; budget cap 2 ms), **0.07 ms per tick** of voxel swell sampling, the **3.5–5 ms** settle joins and the **~15 ms** edit frames near water. GPU: wind sea FFT 0.006 ms, voxel water pass ~0.05–0.065 ms where visible.
4. **Fog volume + SSR:** WaterFogVolume, water_fog.glsl (+ terrain.frag include), ScreenReflections, water_ssr.glsl, the water_shading strip. Recovers **0.12 ms per stale fog block** of main-thread fill, **GPU +0.036–0.039 ms SSR + 0.011 ms copy**, and the fog's reach march (+0.013 ms at the lake).
5. **Rain + RainCover:** RainRenderer, rain shaders, ocean.frag ripples, windSea's `windCover` (set to 1 = open fetch), weather rain hooks. Recovers **GPU up to 0.05 ms** (heavy rain) and **RainCover rebuild p50 0.086 ms**.
6. **Layer B.**
   - 6a: re-point `swashSheet` (Snell phase + `frame.shore`) and `ocean_surf_foam` (depth band), add the depth cap. Run `--bench` A/B against step 5.
   - 6b: delete CoastalSystem, CoastalBuilder, CoastalField, the carrier atlas shader, the ocean_common coastal paths, the OceanSwell coastal paths and CoastalFieldTests.
   - Recovers **GPU +0.020 ms** in the ocean pass (measured), the **300–500 ms background rebuild** per recentre or wind turn, and **44 MB** of host-visible buffers.
7. **Planet adaptation** (not a cut): PlanetGenerator, PlanetShoreDepth into ShoreDepthGrid (replaces the ChunkJob column summaries and the bathymetry merge with its 9.6–13.3 ms rebuilds and 5 ms upload hitches), SeaFrame, camera-relative rendering, ocean curvature bend and far shell.

Net, measured where noted:
- Surf-scene GPU frame: **0.2046 → ~0.11 ms**.
- CPU ticks near water: **4–5 ms → ~0.3 ms** after an edit (physics + swell + weather; 4–5 ms is the measured post-edit figure).
- Open sea: **~1–2 ms → ~0.1 ms**.
- Source: about 15.2k of ~25k src lines, 4.1k test lines, ~3.9k shader lines and ~2k doc lines go.

### Open questions for the user

1. **Weather and rain on planets in the first playable:** port the pressure-system weather to the sphere, or use a fixed per-planet wind and swell until later? Rain gets cut in step 5 either way; this decides whether Weather and its panel stay hooked.
2. **Digging below sea level at the shore:** without a flow sim, a hole connected to the sea either fills to sea level instantly (ocean drawn over it via the flood) or stays dry. Which one?
3. **The island during the pivot:** keep it as a flat regression world until the planet generator lands, or go straight to the sphere? Keeping it delays steps 6b–7 deleting `TerrainGenerator`'s island code and `Coastline.cs`.

Key paths: C:\Users\Ashley\Documents\BRUTALGame\shaders\ocean_common.glsl (swashSheet 1466–1562, oceanFinish 1770–1905), C:\Users\Ashley\Documents\BRUTALGame\shaders\ocean_surf_foam.comp, C:\Users\Ashley\Documents\BRUTALGame\shaders\ocean.frag, C:\Users\Ashley\Documents\BRUTALGame\src\Water\Ocean\OceanSwell.cs, C:\Users\Ashley\Documents\BRUTALGame\src\Terrain.Runtime\BathymetryTexture.cs, C:\Users\Ashley\Documents\BRUTALGame\src\Game\TerrainCoastalDepth.cs, C:\Users\Ashley\Documents\BRUTALGame\src\Game\GameApp.cs, C:\Users\Ashley\Documents\BRUTALGame\docs\breaker-review.md (measured GPU table §4).