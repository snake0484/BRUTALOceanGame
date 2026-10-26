# Rain

Rain falls where the weather (`weather.md`) says it does, at the intensity it gives at the camera. It is carried by the local wind and hidden under anything solid. It also lands on the water: rings ride the waves, splash crowns pop up and their flashes sparkle further out. It is all GPU, stateless but for one buffer of splash slots, and costs nothing when it isn't raining.

## Pieces

| What | Where | Runs |
| --- | --- | --- |
| Falling streaks | `rain.vert` / `rain.frag`, `RainRenderer` | instanced quads, up to 24 576 |
| Splash crowns | `rain_splash.comp` (placement), `rain_splash.vert` / `.frag` (drawing) | 4096 cells, one per 0.5 m |
| Rings, flashes and roughness on water | `rain_ripples.glsl` in `ocean.frag` and `voxel_water.frag` | per pixel |
| Where rain can't reach | `RainCover` (Engine), `rain_cover.glsl` | 64 × 64 columns of 2 m, plus a 128 × 128 fetch map of 8 m |

`GameApp.PrepareRain` sets the intensity, using the Weather panel's override when it is on, and otherwise the weather's rain at the camera while *Drive sky + rain* is on. It also sets the wind and fills the cover every frame (the ocean reads its wind exposure even when dry). `RainRenderer.Prepare` then passes intensity and time to the ocean (`foamParams.xy`) and to voxel water (`Rain`).

## Falling rain (`rain.vert`)

- **Boxes:** drops live in two world-anchored boxes that wrap around the camera.
  - Every third drop is in the near box (16 m wide, 16 m tall). The rest are in the far box (48 m wide, 36 m tall). 40% of each box's height is below the eye.
  - Each drop's position is its hash, plus how far the air has carried it, minus how far it has fallen. It wraps into the box with `mod`, so drops stay put in the world as the camera moves.
- **Motion:**
  - The air's drift is integrated on the CPU from the wind (`RainRenderer._airX/_airZ`, wrapped at the far box's width, a multiple of the near's), so a wind change turns the rain without a jump.
  - Each drop falls at 0.75–1.25 × 7.5 m/s, the terminal speed of 2–3 mm drops. Fall time loops every 1024 s to keep float precision; the restart happens once every 17 minutes and is invisible among so many drops.
- **Shape:** a camera-facing quad along the drop's velocity, 30 ms of motion blur long. It is never thinner than 1.2 px. Thinner real drops fade by the square root of their pixel coverage, so distant streaks neither alias nor vanish.
- **Fades:** drops fade at the box faces, so the wrap never shows, and right at the eye.
- **Light:** sky ambient plus a share of the sun, tonemapped like the ocean's spray, then fogged.
- **Count:** the instance count scales with intensity^0.8.

## Rain on water (`rain_ripples.glsl`)

- **Impact grid:** each cell gets one drop per cycle, kept with probability `min(1.4 × rain, 1)`. Position, phase and keep are an integer hash (pcg4d) of the cell and the cycle, so the grid is world-anchored at any coordinate.
- **Ring layers:** three layers, each a packet of capillary crests, h = A cos(k s) exp(−s²/w²), spreading and fading quadratically.
  - The rings ride the waves because `ocean.frag` evaluates them at the rest position.

| Layer | Cell | Ring | Wavelength | Notes |
| --- | --- | --- | --- | --- |
| Impacts | 0.5 m | 0.36 m/s for 0.5 s | 6 cm | the same grid as the crowns: every crown sits at its ring's centre |
| Small drops | 0.26 m | 0.24 m/s for 0.4 s | 3 cm | |
| Largest drops | 0.9 m | 0.5 m/s for 0.62 s | 10 cm | resolved furthest out |

- **Neighbour bound:** speed × life + 2 × width stays under half a cell, so a pixel only checks its 2 × 2 nearest cells per layer.
  - A cycle is never shorter than a ring's life, so no ring is cut short.
- **Anti-aliasing:** a layer fades out once the pixel footprint (`fwidth` of the rest position) passes a third of its wavelength. It is skipped entirely past half.
- **Fresh impacts** get a white dot 8 mm across for 40 ms.
- **Flashes:** beyond the drawn crowns (10–16 m) their flashes stand in for them, at 80 per m² per second in a downpour.
  - Each flash lights the one pixel its centre falls in. The fragment's `dFdx`/`dFdy` map the offset into pixels.
  - Its brightness is a crown's area (0.002 m², about 5 cm across) over that pixel's footprint on the water, capped at 1.
  - So a flash keeps its real size at any distance: a spark at mid range, fading into the roughness far out.
  - The cells are world-anchored, about a pixel in size, at the two power-of-two sizes around the footprint, blended by it, so flashes stay put as the view moves.
  - **The first version** drew a spot a fixed 3 px cell wide. Far out that was a blob tens of centimetres wide, and at grazing views a wide dash, because a pixel's footprint stretches along the view. The user saw them as "abnormally large" impacts.
- **Roughness:** rings finer than a pixel roughen the sea. `gRainRough` broadens the GGX lobe toward roughness 0.3 and blurs the sky reflection toward its average.

## Splash crowns (`rain_splash.comp`, `rain_splash.vert/.frag`)

- **Placement:**
  - The window holds one slot per impact cell within 16 m, 64 × 64 cells. A cell with a live impact (crown life 0.32 s), thinned out from 10 to 16 m, puts its crown where the drop lands.
  - On the ocean, the crown goes at `oceanAt`'s displaced position of the impact's rest point, so it sits on the surface as drawn and rides the swell with its ring.
  - It goes on the terrain's top instead where that is higher, or where no ocean is drawn.
- **Lanes:** the surface evaluation is a long serial chain, and there are few crowns to hide it behind.
  - So each cell gets 8 lanes. Each lane sums every 8th swell component (`swellShare`, as `ocean_lip_base.comp` does), the lanes reduce in shared memory, and lane 0 finishes the point (`oceanFinish`).
  - This halved the pass: 0.036 → 0.017 ms. Packing live impacts to the front of each workgroup did nothing (0.036 → 0.036). Stubbing the surface out showed the rest of the pass costs 0.002 ms.
- **Drawing:** a camera-facing quad. The fragment shader draws the crown analytically, in projection: heights shrink with the cosine of the camera's elevation, and the rim's depth grows with its sine, so from above a crown is a ring.
  - **Sheet:** a thin cylinder, clear at its foot and thickening to the rim. The near wall shows its outside; above the near rim, the far wall's inside shows.
  - **Rim:** a ragged ellipse, bright where the sheet is seen edge-on.
  - **Points and droplets:** six points on the rim each fling a droplet at 0.3 of the crown's life, which flies out and falls under gravity.
  - **Jet:** on water, a Worthington jet rises after 0.4 and pinches off a drop.
  - **On ground:** a smaller crown with no jet.
  - **Size:** 5 cm half width, about 3× real, since a real crown is invisible at game distances.
  - **Small crowns:** a crown under a few pixels tall fades out, leaving it to the flashes.

## Cover (`RainCover`, `rain_cover.glsl`)

- **Data:** per 2 m column in a 128 m window around the camera, the highest solid surface, the **wind exposure** and the **fetch** (48 KB), after a fetch map over the whole bathymetry grid (128 × 128 cells of 8 m, 64 KB).
  - The top comes from `BathymetryGrid.Tops`, the finest leaf with a surface in each column, so an overhang's top counts.
  - Water at sea level is open to the sky where the column's top is under sea level (or unknown, open sea).
  - Fetch is the effective fetch of the open water upwind, along 9 radials over ±45° of the ocean spectrum's wind, to land or cover, up to 400 m, from the fetch map (`ocean-gpu-contract.md`, "Wind sea"); outside the window the shader reads the map directly. Exposure is 1 on open water and falls linearly to 0 at `ShelterDistance` (10 m) from it under cover, by a two-pass chamfer distance that also carries the nearest open water's fetch under the cover. The ocean limits its wind sea by both.
  - The buffer is (window rect, fetch map rect, fetch map, window columns); the map is copied to a frame's buffer only when a new one has landed.
  - `GameApp.PrepareRain` writes it every frame, rain or not. The window moves in 4-cell (8 m) steps, so the camera always has 56 m or more of it on every side. It is rebuilt only when it moves a step, the bathymetry version changes or the wind turns past one of 64 headings, and copied to a frame's buffer only when that buffer holds an older window.
  - **Cost** (`--fly-test`, 30 s round the coast at 40 m/s, Release, RTX 4090 machine, 2026-10-25): a rebuild is p50 0.086 ms and p95 0.12 ms on the main thread, with rare 2 ms outliers. Moving in 2 m steps it rebuilt 770 times (65 ms over the 30 s); in 8 m steps 255 times (26 ms, about 0.006 ms a frame). The Profiler panel shows it as *Cover rebuild*.
- **Bindings:** the ocean's set binds it at binding 11, so the rain pipelines read it through that set, and voxel water binds it at binding 1.
- **What it hides:**
  - A drop below its column's top culls itself.
  - A crown lands on the top.
  - Rings and flashes fade out where the top is above the water (`rainReaches`).
- **Result:** nothing falls, splashes or ripples under a roof, an overhang, or in a cave.
- **Resolution:** 2 m, nearest cell, so cover edges are blocky at that scale. Wind-slanted rain still falls straight down past a roof's edge.
- **Check:** `--roof-test` floats three rows of rock spheres, 6 m up, over the camera and the water ahead of it.
  - Under the roof, no drops fall and the water has no rings or crowns; past its edge it rains.
  - (The water under the roof shades as pale shallows. Bathymetry takes a column's highest surface as its seabed. This is a separate, pre-existing ocean behaviour, seen with any terrain floating over the sea.)

## Command line and panel

| Flag | Effect |
| --- | --- |
| `--rain <0..1>` | rain at that intensity whatever the weather (the panel's *Override rain*) |
| `--weather-offset <min>` | 933 is the storm and 725 is calm at the start camera (`weather.md`) |
| `--bench-rain` | `--bench`, but alternating rain on and off instead of layer B |
| `--roof-test` | the cover check above |

- **Weather panel (F6):** *Drive sky + rain*, *Override rain* with its slider, and the number of drops drawn.
- **Profiler:** GPU scopes *Rain splash sim*, *Rain splashes* and *Rain drops*. `GpuTimer.MaxScopes` went from 16 to 24 to fit them.

## Cost

`--weather-offset 725 --rain 0.9 --cam -265 2.5 0 180 -18 --bench-rain`, RTX 4090, 1600 × 900, Release, 2026-10-25. Rain on vs off in alternating 2 s blocks, medians in ms:

| Scope | Rain on | Rain off |
| --- | --- | --- |
| Frame | 0.248 | 0.198 |
| Ocean surface | 0.142 | 0.103 |
| Rain splash sim | 0.017 | — |
| Rain splashes | 0.003 | — |
| Rain drops | 0.004 | — |

So heavy rain costs **0.05 ms**, about three quarters of it on the water: the rings and flashes (+0.039 ms in the ocean pass) and placing the crowns. The rain scopes aren't written when it's dry; the bench repeats their last sample, so their "off" column means nothing.

The CPU side is a 12-float header while it rains, and the cover (above), rebuilt when the camera crosses a 2 m cell or the bathymetry changes.

## Not yet

- **Distant rain:** past the far box (24 m), rain is only the weather's fog. A distant rain-shaft layer would let a storm be seen from outside it.
- **Wetness:** wet ground and surfaces (darker albedo, glossier), and puddles. Rain on voxel water gets rings but no crowns.
- **Gusts:** drops follow the mean wind; gusty sheets of rain need the gust time series (`weather.md`).
- **Damping:** heavy rain also damps the short wind waves. The FFT's finest cascade could follow it.
- **Audio.**
