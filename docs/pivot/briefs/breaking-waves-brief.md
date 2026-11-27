# Breaking waves: slice 1 brief

Source: D79, D79a. Old code and docs (`docs/breaking-waves.md`, `docs/breaker-review.md`): tag `island-final`. Numbers are old or estimates. Not read in code: "unconfirmed".

## 1. Goal and scope

Waves roll in over the seabed, steepen, and break on the shore of a planet's sea. Slice 1 is the breaking face only: crest lift, steepening, pitch and whitewater on the heightfield. The face displaces the ocean surface; it is not a mesh.

Out: lips, spray, bores, zone hand-off, wind sea, voxel water, wave groups, surfing.

Shorelines are fixed (D30, protected band), so the break map is baked once per coast region and never rebuilt. The Water section of `CLAUDE.md` still lists coastal maps and breakers as gone. Update it when this lands.

## 2. The model (from the old code)

**Coastal map** (`CoastalField.cs`, `CoastalBuilder.cs`): a grid over a coast. Per cell it holds depth, an active flag, and for 12 reference frequencies a phase lag against deep water and an amplitude factor. Lag comes from an eikonal solve (fast sweeping, Godunov, `omega^2 = g k tanh(k h)`). Amplitude comes from energy transport along the rays (shoaling and refraction), clamped to 3. The old field was 256 x 256 cells x 28 floats = 7.3 MB.

**Carrier wave** (`OceanSwell.SurfCarrier`): the swell peak followed as one coherent wave. Phase `theta = k0 d.x + lag(x) - omega_p t`. Crests are where `theta` is a multiple of 2 pi. The real swell is a random sum that never reaches breaking height, so the carrier is what breaks.

**Break line**: breaking where `H / h > gamma` (`BreakingGamma` 0.55 in `CoastalField`). The capped height gives a cap in [0,1]: 1 shoaling, >= 0.85 curling, >= 0.6 plunging, below that a bore, depth < 0.3 m spent. Iribarren `xi = tan(beta) / sqrt(H / L0)`: below 0.35 spilling, above 0.65 plunging (`BreakerFace`). The break point moves along the crest, which gives the peel.

**Face** (`BreakerFace.Apply`, mirrored in `ocean_common.glsl breakerFace`): a pure function of the carrier. Two Newton steps find the nearest crest. The signed distance to it, wavelength and crest state come from the carrier there. It adds crest lift (0.2 x capped height), a shoreward shift of the crest top (fold-free, `|D'| <= 0.85`) and pitch, with slope and Jacobian terms, and a whitewater weight. Timing runs on wavelengths of travel since the break began (`OnsetTravel`, `CollapseTravel`).

**Old review lesson** (`breaker-review.md`): per-tick crest polylines (marching squares, chains) caused the popping. The face itself was continuous. So the port skips polylines: the face is evaluated per vertex from baked maps.

## 3. The sphere port

**What no longer holds.** The old code assumed a flat world, one island, one sea plane, a camera window rebuilt as it moved, float world coordinates, and seabed edits. Now the sea is a sphere and shores are fixed.

**Regions.** Key a coast region by `SeaTiles` tile index (64 km tiles, fixed tangent frame at the tile centre). A coast map covers a window inside one tile's tangent plane. Reusing the tile frame means the swell and the breakers share one frame.

**Precision.** Region origin is a double direction on the sea sphere. Inside a region, cell coordinates are float metres in the tile's tangent plane. The only double step is placing the region against the camera (`relOrigin`, camera-relative, as in `CLAUDE.md`).

**Bake.** One job per region on `JobScheduler`, never the main thread. Depth source is `PlanetShore.GeneratedDepth` (generated ground only; edits ignored by D30), via the direction-to-height path in `PlanetSurface.Height`. Cells sample the sphere through the tile frame (`SeaTiles.Plane` inverse). Only regions with water shallower than the break depth (`1.3 Hs / gamma`) are baked.

**Reduce the map.** The old 12-frequency layout served the swell surface. Slice 1 needs the carrier only. Per cell store: spatial phase (float32; half floats lose it), depth, shoal amplitude, wavevector direction (2 x half). The old review proposed this atlas: about 7 floats per cell, not 28.

**Swell direction.** The old map was solved for one direction and rebuilt on turn. The bake takes direction as an input and the region key includes it. Fixed per planet: unconfirmed (`GameApp.cs:186` reads a launch argument).

**Cache and lifetime.** Regions load near a coast and unload farther out, with hysteresis (about 3 km in, 6 km out; tune). Fixed LRU pool, fixed buffers, zero allocation after warm-up. A map is a pure function of seed, kind, swell params and key, so it can be dropped and rebaked.

**Far/near.** One bake resolution (4 m). Outside every loaded region the water is the plain swell. Edge cells blend to deep (old `EdgeWeight`), so the face fades out with no pop. GPU and CPU use the same fade.

## 4. Determinism and networking

- Everything is a function of seed + tick + region key. Zero bandwidth. No breaker state is sent.
- The face stays visual in slice 1: `OceanSurface.Sample` does not include it. Buoyancy agrees on server and clients because the sampled swell is unchanged. (The old design added the face to the CPU sample: 15 us against 3.6 us.)
- The GPU never feeds gameplay. No readbacks.
- The bake and the CPU mirror of the face live in `src/Water` (no Vulkan), so a headless server can run them. The server needs them only if the face enters `Sample`. Otherwise the server bakes nothing.
- The old solve uses `tanh`, `exp`, `log`, `sin`. Cross-machine bit equality is needed only if the face enters `Sample` (unconfirmed).

## 5. Budgets

Old measures are Release, RTX 4090, island view, and old. They are starting points.

| Item | Old | Slice 1 target |
| --- | --- | --- |
| Bake per region (job thread) | one 256 x 256 field, 12 refs: not recorded in the docs read (unconfirmed) | <= 30 ms per 1 km region of 4 m cells, carrier only |
| Memory per region | 7.3 MB | <= 1.5 MB (7 floats x 65,536 cells x 4 B = 1.8 MB; halves trim it) |
| Total resident | 3 fields (triple buffer) | <= 8 regions, <= 16 MB |
| CPU per frame, main thread | `BreakerSet.Update` 0.12 to 0.54 ms per tick | <= 0.05 ms: region load and unload only |
| GPU, ocean pass | 0.055 ms bare, 0.095 with face and map; frame 0.195 to 0.204 | <= +0.04 ms over the bare ocean pass |
| Allocations per frame | not zero | zero |

Old face cost was about 80 buffer loads per near vertex; a filtered atlas texture should need about 6 (estimate).

## 6. Build order

1. **Depth source and region keys.** Region key, window frame on the tile, depth sampler over `PlanetShore.GeneratedDepth`. Test: depth matches the direct height call.
2. **Carrier bake.** Port the eikonal phase and energy transport (carrier frequency only) from `CoastalBuilder`/`CoastalField`. Job, fixed buffers. Ported `CoastalFieldTests`: lag grows into shallows, amplitude shoals, deep water gives lag 0, amplitude 1.
3. **Carrier and face on the CPU.** Port `SurfCarrier` and `BreakerFace.Apply` as pure functions against the atlas. Ported `BreakerSetTests` (head-on crests break where cap < 1, crests move shoreward, oblique swell peels) plus tick-to-tick continuity.
4. **Region manager.** Load and unload by distance, LRU pool, jobs, camera-relative placement. Test: no allocation after warm-up.
5. **GPU face.** Atlas texture per region, `breakerFace` in the ocean shaders (vertex), foam weight to the fragment shader. A/B toggle flag.
6. **Tune and measure.** Stability, near/far, cost table, `CLAUDE.md` update.

One commit each. Steps 1 to 4 have no GPU change.

## 7. Files

Add (proposed names):
- `src/Water/Coast/CoastRegionKey.cs`, `CoastBake.cs`, `CoastAtlas.cs`, `CoastRegionSet.cs`
- `src/Water/Ocean/BreakerFace.cs` (port; CPU mirror)
- `src/Water.Rendering/CoastAtlasGpu.cs`
- `shaders/ocean_breaker.glsl` (included by `ocean_common.glsl`)
- `tests/Water.Tests/CoastBakeTests.cs`, `BreakerFaceTests.cs`

Change: `src/Water.Rendering/OceanRenderer.cs`, `shaders/ocean_common.glsl`, `ocean.vert`, `ocean.frag`, `src/Debug/OceanPanel.cs` (face toggle, region stats), `CLAUDE.md` (Water).

Port from `island-final`: `CoastalBuilder.cs` (solver only), `CoastalField.cs` (the carrier channels), `BreakerFace.cs`, the face part of `ocean_common.glsl`, `CoastalFieldTests.cs`, `BreakerSetTests.cs` (assertions only).

Drop: `BreakerSet.cs`, `BreakerFaces.cs`, `BreakerLip.cs`, `BreakerBores.cs`, spray and `ocean_lip.*` shaders, `CoastalSystem.cs` (camera window), `Coastline.cs`, `TerrainCoastalDepth.cs` (replaced by `PlanetShore`), `BreakerOverlay.cs`.

## 8. Tests and measurements

- Unit: ported tests, plus bake determinism (same key, same bytes).
- Viewer: `--shore-fly` already follows a waterline. Add `--surf-beach` (proposed): fixed beach camera. `--no-faces` for A/B.
- **Per tick, in motion** (the user judges water in motion): at fixed points along a break line, over 600 ticks, count face height jumps above 0.08 m per tick, normal jumps, and foam weight jumps. Old continuity was 0.038 to 0.076 m per tick on the face.
- Near versus far: no step in the face at region edges or the load boundary.
- Frame cost table: ocean pass ms with faces off and on, GPU timer, Release.

## 9. Exit check

- Waves steepen and break on a beach of the test planet, peeling when the swell is oblique.
- Per-tick face jump at or below the old numbers; no pop at region load or unload.
- Section 5 budgets held, measured table recorded; zero allocations per frame.
- Bake and CPU face build without a Vulkan reference.
- `CLAUDE.md` Water section updated.

## 10. Calls made for slice 1

1. **Regions:** several small windows per `SeaTiles` tile (a fixed grid in the tile's tangent plane, about 1 km of 4 m cells), baked only where the coast is. The eikonal solve needs a deep-water edge; if 1 km does not reach it, solve on a wider, coarser domain and store the near-shore window. State the boundary error.
2. **Swell direction varies** with weather. `SwellWind` only re-sends the wind after a turn of 10° or more, so direction moves in steps. The region key includes the direction as sent; on a turn, rebake in a job and cross-fade old to new. No rebake on the main thread, no pop.
3. **Visual only.** The face stays out of `OceanSurface.Sample`. Boats pushed by breakers is a later gameplay decision (open, for the user).
4. **No far map.** The face fades out at the region load distance. Measure whether the whitewater line visibly ends when seen from height; report it.
5. **Exit-check beach:** the test planet's own coast (temperate kind), found with `--shore-fly`.
