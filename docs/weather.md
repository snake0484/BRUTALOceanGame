# Weather

Localized weather: storms in one place, sun and calm in another. It is cheap because almost nothing about it changes from one tick to the next. Weather is **analytic** (a function of seed and time, like the swell), so it can be evaluated anywhere and needs no networking. A coarse **snapshot map** around the camera is the product everything queries.

## Model (`src/Weather/`, namespace `BrutalGame.Weather`, pure CPU, v1 implemented)

### Climate (`WeatherClimate`)

- **Prevailing wind:** speed and direction. It blows everywhere, and it steers every system (they move at 0.8 × it).
- **Storminess** 0..1: the share of systems that are storms (10% to 50%), and how hard storms blow (0.6× to 1.2× their peak wind).
- **Base cloud:** fair-weather cloud between systems (0.15).
- **Seed.**

### Pressure systems (`WeatherField`, `PressureSystem`)

- **Spawning:** systems spawn in 24 km cells of a frame that drifts with the steering wind. Each cell has 2 slots.
- **Lives:** each slot runs back-to-back lives. A slot's period is 30–60 min, from its hash. Each life lasts 60–95% of the period, then there is a gap.
  - The life envelope grows over the first 30% of the life and decays over the last 35%.
  - Every life draws a new kind, spawn point, own drift (≤ 1.5 m/s), size and power from `hash(seed, cell, slot, life)`.
- **Kinds:**

| Kind | Radius | Centre | Peak circulation | Cloud | Rain | Convection |
| --- | --- | --- | --- | --- | --- | --- |
| Storm | 8–12 km | −12 to −25 hPa | 14–26 m/s × (0.6 + 0.6 storminess) | 1 | 0.8–1 | 0.6–1 |
| Low | 10–16 km | −5 to −10 hPa | 5–10 m/s | 0.85–1 | 0.2–0.5 | 0–0.2 |
| High | 12–16 km | +5 to +10 hPa | 2–4 m/s | clears 0.6–0.9 | — | — |

  - One "power" roll drives depth, wind and rain together, so deep storms are the wild ones.
  - Highs also **still** 50–80% of the prevailing wind at their centre: subsidence makes a stable, calm boundary layer.
- **Profiles have compact support,** so a system has no effect past its radius. Take q = distance / radius:
  - Pressure and cloud: (1 − q²)³.
  - Rain falls inside 0.6 R and convection inside 0.45 R, each with the same shape over its core.
  - **Wind:** the gradient wind of the pressure profile, ∝ q (1 − q²)². It peaks at q = 1/√5 (0.45 R), where it equals the peak circulation.
    - It is cyclonic (counter-clockwise from above, +Y up) round lows and storms, and anticyclonic round highs.
    - It crosses the isobars by 20°: into lows, out of highs.
    - So a storm's wind adds to the prevailing wind on one flank and opposes it on the other, and its centre is a small calm eye.
    - Per system it costs no square root: the ring term × d/|d| simplifies to w² d / R.
- **Combining systems:**
  - Lows and storms **union** their cloud, rain and convection: 1 − Π(1 − cᵢ gᵢ).
  - Highs **scale down** all three: × Π(1 − clearᵢ gᵢ).
  - Wind and pressure add.
  - Gust factor = 0.2 + 0.5 × convection.
- **Neighbour bound:** a system's edge is at most 16 km (radius) plus 5.1 km (drift over its longest life) from its cell. That is less than one cell, so a point needs only its own and the 8 neighbouring cells: at most 18 candidates.
  - `PointSampleSeesEverySystemThatReachesIt` checks this bit for bit against every system within 200 km.

### Snapshot map (`WeatherMap`)

- **Grid:** 32 × 32 nodes at 1 km (32 km across) around the camera, baked every 5 s of weather time.
- **Sampling:** `Sample(x, z)` takes bilinear values from the two snapshots either side of now and lerps between them in time. Every field is linear, and wind is a vector, so lerping is exact for the blend.
- **Baking:**
  - Three snapshots rotate (from, to, baking).
  - While a pair is in use, the snapshot after next bakes **one row per tick**: 32 ticks of 300, then idle. The systems that reach the grid are gathered once per bake (≤ 50 by the cell bound; capacity 64).
  - A bake is centred on the camera. Origins snap to the 1 km lattice, so any snapshot that covers a point gives it the same value, and re-centring never pops.
- **Rebuilds:** a synchronous rebuild of all three happens on the first tick, on a time jump (debug scrub), after a climate change (`Invalidate`), or when the camera leaves the middle half of the far snapshot (a teleport). Normal flight never triggers one: even at 400 m/s the camera moves 4 km in two intervals, against 8 km of margin.
- **Off the grid,** a sample falls back to the analytic field (≤ 18 systems).
- **Layout:** cells are AoS (`WeatherSample`, 7 floats). Each sample reads all fields at 4 nodes, so they sit together.
- **Threading:** Tick on the main thread. Sample is read-only between ticks, like the swell.

## Game coupling (`src/Game/WeatherSystem.cs`)

- **Ticks:** `GameApp.Tick` calls `WeatherSystem.Tick(sim time + debug offset, camera)`. The cost is recorded into the Weather panel.
- **Per frame:** `WeatherSystem.Update` samples the map at the camera.
  - **Ocean wind:**
    - The swell takes the **sea wind**: the mean wind speed over a 3 × 3 grid of samples 2 km apart, along the mean wind's direction. The waves at a point were raised over the water around it, and the averaging also hides a storm's calm eye.
    - A new `SwellParams` wind is queued only when the sea wind has moved by more than 1 m/s or 10°.
    - `OceanSwell` then crossfades over its 30 s `BlendSeconds`, and queues the latest change while blending. So a sea builds as a storm arrives, and at most one spectrum rebuild starts per 30 s.
    - Fetch, depth, groundswell and the other swell parameters are left as they are.
  - **Sky:** sun intensity, zenith and horizon colours, and fog density are set on `OceanRenderer`. Terrain and voxel water read them from there, and the clear colour follows the horizon.
    - Overcast (smoothstep of cloud over 0.25–0.95) greys the sky and blocks up to 85% of direct sun.
    - Rain and storm cores darken the sky further and multiply fog by up to 10.5×. Fog's 1/e distance is then about 0.8 km, against 8 km clear.
- **Command line:**
  - The weather is on by default.
  - `--wind` pins the sea and turns the weather off, so tests, benchmarks and screenshot A/B runs are unchanged.
  - `--weather` forces it on, and `--no-weather` turns it off.
  - `--weather-offset <minutes>` starts weather time there. The first swell wind is built from that weather with no blend, so a run can open inside a storm with its sea already up.
    - At the start camera with seed 1: 933 is a storm (cloud 85%, rain 47%, 19 m/s, −16 hPa, Hs 3.9 m), and 725 is fair (cloud 5%, 1.8 m/s, +7 hPa).
  - The panel toggles *Drive ocean wind* and *Drive sky* at runtime. With the sky off, the clear-sky lighting is restored.

## Debug (`WeatherPanel`, F6)

- **Climate:** sliders and *Apply climate*, which rebuilds the map.
- **Time offset:** a slider in minutes, for scrubbing through systems' lives.
- **At camera:** wind speed, direction and gusts; cloud, rain, storm and pressure.
- **Map:** 32 km around the camera, Z down, so it is viewed from above.
  - Cloud is grey, rain blue, and storm cores red.
  - Wind arrows every 4 km.
  - Each system's extent is drawn as a circle: red storm, blue low, yellow high. Opacity shows strength.
- **Perf:** tick average and max, and the last rebuild's cost.

## Tests (`tests/Weather.Tests`)

- **Determinism:** the same seed is bit-identical; a different seed differs.
- **The 3 × 3 cell bound:** checked bit for bit against every system within 200 km.
- **Variety:** storm (cloud > 0.9, rain > 0.4, wind > 10 m/s) and fair weather (cloud < 0.2, no rain, wind below the prevailing) coexist over a 200 km square.
- **Rotation:** lows turn counter-clockwise from above with inflow; highs turn clockwise with outflow.
- **Map against field:** over 30 s, within 1.5 m/s of wind and 0.08 of cloud and rain.
- **Smoothness:** per-tick change at a fixed point, with the camera flying at 100 m/s across snapshot rotations and re-centres: < 0.05 m/s and < 0.005 cloud. One rebuild only.
- **Rebuilds:** a time jump rebuilds and matches the field.
- **Allocation:** none in Tick or Sample.

## Not yet

- **Visible weather at a distance.** There is no cloud rendering yet, so a storm 10 km away can't be seen. Only the sky where you are changes. Rain itself is drawn around the camera (`rain.md`).
  - Next step: upload the snapshot pair (cloud, rain, storm) as a small texture once per bake, for a cloud layer, cloud shadows and rain particles on the GPU. It is GPU-only, and nothing reads back.
- **Sea state is still global.** The swell follows the wind near the camera, so the whole visible ocean changes with it.
  - A local sea state would scale the wind-sea components by a coarse map of the weather wind, like layer B's amplitude maps. The FFT high band would follow the same map.
  - Storms should also radiate groundswell to the areas around them.
- **Terrain effects on wind:** sheltering, channelling and deflection (CLAUDE.md). This needs a finer local wind layer near the camera, over the terrain heights.
- **Gusts as a time series** (sails, aircraft, audio). Only the gust factor exists now.
- **Lightning,** and wet terrain.
- **Timescale:** a system lives for 18–57 min of sim time, and the steering moves it at about 4.8 m/s. Tune once there is gameplay to judge it against.

**Measured** (Debug build, 2026-10-25, 8/8 passing):

- Map against field: worst errors of 0.12 m/s wind, 0.0024 cloud and 0.0064 rain.
- Per-tick change across 64 points, 37 of them under a system: at most 0.00035 m/s wind and 0.00001 cloud.
- A full rebuild takes 0.24 ms.
- Over a 200 km square at storminess 0.5, 0.2% of points are stormy by the strict test, and 32% are fair.

## Cost

**Measured in game** (Release, RTX 4090 machine, 2026-10-25): the Weather panel's tick averages 0.0002–0.0003 ms, with a maximum of about 0.005 ms on bake ticks. The startup rebuild, cold, takes 1.4 ms.

- A tick bakes one row (32 nodes × the gathered systems, typically 10–20) during 32 of every 300 ticks. Other ticks do one floor and one compare.
- `Sample` is two bilinear reads of 7 floats.
- The game samples 10 points per frame: the camera, plus 9 for the sea wind.
- The panel's map samples about 1100 points per frame, but only while it is open.
- A rebuild is about 2 × 1024 node evaluations, only on the events listed above.
- To measure: the Weather panel's *Tick* row, and the profiler's sim tick.
