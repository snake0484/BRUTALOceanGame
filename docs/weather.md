# Weather

Localized weather: storms in one place, sun and calm in another. It is cheap because almost nothing about it changes from one tick to the next. Weather is **analytic** (a function of seed and time, like the swell), so it can be evaluated anywhere and needs no networking. A coarse **snapshot map** around the camera is the product everything queries.

On planets (D31) the same model runs on the sphere: `SphereWeatherField` and `SphereWeatherCache` (see "On planets" below). The flat `WeatherField` and `WeatherMap` stay until `src/Game` and `src/Sim` move over; nothing else uses them.

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

## On planets (`SphereWeatherField`, `SphereWeatherCache`, `PlanetClimate`; D31, implemented, not yet hooked up)

The island model carried over: kinds, lives, profiles and how systems combine are unchanged. What changed is where systems spawn, how they move, the distances and the circulation sense. It is still a pure function of seed + tick.

### Climate per body (`PlanetClimate`)

- `Seed`, `Radius`, `SpinAxis` (north; the body spins right-handed about it).
- **Zonal steering:** `ZonalCells` bands per hemisphere alternate easterly (trades, from the equator) and westerly. `u(lat) = −amp_b · sin(π x)` with `x = |lat| / 90° × cells`, so it is 0 on the equator, at every band edge and at the poles.
  - Each band's amplitude is `EasterlySpeed` or `WesterlySpeed` × (1 ± `JetVariation`) from the seed.
  - The prevailing wind adds `MeridionalShare` × u poleward: equatorward under trades, poleward under westerlies.
- **Bands from the seed:** storminess and base cloud are scaled per band by 1 ± their variation, linear between band centres (continuous across the equator).
- **Equatorial fade:** circulation round a system is × smoothstep(0, sin `EquatorFade`, |sin lat|) at its centre (5°), so nothing spins on the equator. Its pressure, cloud and rain stay.
- **Scales:** `SizeScale` (radii, cell size), `WindScale` (systems' own winds), `TimeScale` (lives). The own drift is 1.5 m/s × size / time, so the spawn-to-edge reach stays the same fraction of a cell at any scale.
- **Presets:**
  - `Rocky(seed, radius, day, axis)`: the island table. Bands = 3 × √(ΩR / Earth's ΩR), clamped 1..6. For the 1000 km, 6 h worlds that is **2** (trades to 45°, westerlies to the pole). Set `ZonalCells` to override.
  - `GasGiant(seed, radius, axis)`: 4–10 bands per hemisphere from the seed, jets 50–180 m/s, `SizeScale` 190 (storms 1,500–2,300 km, lows up to 3,000 km), `WindScale` 9.6 (storm winds ~130–250 m/s), `TimeScale` 200 (lives of ~2.5–8 days), base cloud 0.9. No rendering; `GasGiantWeather` (planets.md) can wrap it.

### Lattice (`SphereWeatherField`)

- **Rows:** `Rows = round(πR / (24 km × size))`, 131 on a 1000 km world. Row j holds `max(1, round(2πR cos lat_j / 24 km))` cells (~22,000 in all, never stored). A life is hashed from (row, cell, slot), as the island hashed (cx, cz, slot).
- **Drift:** each row turns rigidly about the pole at `ω_j = 0.8 u(lat_j) / (R cos lat_j)`, in closed form from the tick. Each system also drifts on its own, up to 1.5 m/s, along a great circle from its spawn point, so it can cross a pole.
- **Spawn:** a point within the row's latitude band and the cell's longitude span, 10% in from the edges.
- **Window:** a point visits the rows within `Reach` of its latitude (16 km radius + 5.1 km drift, + 1% safety). In each row it visits the cells whose longitude span can hold a spawn point within `Reach` of it.
  - The widest longitude offset is found per row. Where the reach circle is tangent to a meridian inside the row it is `asin(sin ρ / sin θ)`; otherwise it is the law-of-cosines value at the row's edges. If the reach circle holds a pole, that can be the full row.
  - Cells come in ascending order, even when the range wraps. So any wider window sees shared systems in the same order, which is what makes the bit-for-bit test possible.
- **The high-latitude cap,** measured over 0–90° every 0.01°: at most 3 cells per row below 75°, 4 above, and 9 cells (18 systems) in total. D31's bound was ≤ 24. The sample buffer holds 64 for gas giants' coarse rows.
- **Geometry:**
  - Distances are great-circle: angle² ≈ chord² (1 + chord²/12) (the next term is ~θ⁶/360), with no trig per system.
  - Circulation is `Spin × (cos 20° · p × d − sin 20° · tangent(d))`, with `d = p − centre`. It is counter-clockwise about local up for `Spin` > 0: northern lows and storms, and southern highs.
  - Inflow into lows (and outflow from highs) has the same sign in both hemispheres.
- **Trig:** sin and cos of row centres and cell centres are tabled at construction. Each row's drift costs one SinCos per gather, and spawn offsets up to 0.02 rad use a Taylor series that is exact to rounding. So a rocky world takes no CRT trig per system. Polar rows and gas giants fall back to `Math.SinCos`.

### API

- `Sample(double3 planetLocal, long tick)` returns a `WeatherSample`: wind, cloud, rain (precipitation intensity), storm, pressure and gust.
  - Wind is **(east, north)** in the local tangent frame.
  - Altitude is ignored: this is the 10 m wind.
- `ToPlanet(unit, wind)` gives the planet-frame vector, e.g. for `v_air = v − wind`. `Basis(unit, out east, out north)` and `Direction(lat, lon)` are also public.
- `Gather(unit, extraAngle, time, span)` and `Evaluate(unit, systems)` work as on the island. `Zonal(lat)` is the steering profile.
- Read-only, safe from jobs, no allocation.

### Cache (`SphereWeatherCache`)

- **Planet-fixed lattice:** an equal-angle cube sphere, with 1,568 cells per face edge on a 1000 km world (~1 km). It is cut into 16 × 16-cell tiles, each baked at 5 s snapshot times (300 ticks).
  - A sample is bilinear in its tile and a lerp between the snapshots either side of its tick.
  - Nodes store the **planet-frame** wind, projected to (east, north) at the sample point. So lerps stay right where east and north turn fast (the poles).
- **Camera-independent:** a node's value depends only on the node and the snapshot. Any cache that holds a tile (server or client, headless or not) returns bit-identical samples there.
  - Anchors only choose which tiles are kept: the camera on a client, the sim's sea reference point and ships on a server.
  - Shared face edges have identical node points (antisymmetric tan table), so samples are continuous across faces.
- **Tick(tick, anchors):**
  - Tiles within `CoverRadius` (4 km) of an anchor get the current and next snapshot. If either is missing it is baked synchronously; that happens on the first tick, a time jump or a teleport (`SyncBakes`, `LastSyncMs`).
  - Tiles within `PrefetchRadius` (12 km) get the next two snapshots, baked a few node rows per tick (at most 8) by deadline. Normal flight never bakes synchronously.
  - The tile window per anchor is conservative on every face it can reach: a face angle moves ≤ ρ / (n·p − ρ).
- **Capacity:** 48 tiles × 3 snapshots, 1.3 MB, preallocated. Tiles beyond that are dropped (`DroppedTiles`).
- **Sampling:**
  - `TrySample` returns false off the kept tiles.
  - `Sample` then falls back to the analytic field at the exact tick. So a caller that needs anchor-independent values must anchor its points.
- **Threading:** Tick on one thread. Sampling is read-only between ticks and safe from jobs.

### How the sim and renderer should consume it (for the M2 hookup)

- **Sim (headless, server):**
  - Own one `SphereWeatherField` per body with an atmosphere: `PlanetClimate.Rocky` from `PlanetParams`, with `TickSeconds` set to the sim's.
  - **Swell wind:** sample the field directly, as `SwellWind` does now, about a sim-chosen sea reference point (never the camera), every `WindInterval` ticks. Nine samples a second cost ~10–30 µs.
    - Alternatively, keep a `SphereWeatherCache` anchored on that point and on the awake ships. The values are the same on every machine.
  - **Drag and lift (D10):** `field.Sample(shipPos, tick)` at 10 Hz per awake ship in an atmosphere, with `ToPlanet` for the vector, lerped between samples.
- **Client renderer:**
  - One `SphereWeatherCache` for the focus body. Tick it with the camera as an anchor, plus the sim's sea point if the client runs the swell.
  - Sample it per frame at the camera for the sky, rain, audio and the sea's wind.
  - For clouds near the camera, upload the kept tiles' nodes (cloud, rain, storm) after a bake. It is GPU-only, and nothing reads back.
  - The planet-wide GPU coverage bake (planets.md) can enumerate systems with `Gather(centre, hemisphere angle, time, span)`.
- **Gas giants:** `new SphereWeatherField(PlanetClimate.GasGiant(...))`. `GasGiantWind` adds depth and gust noise on top.
- **Not done here:** `WeatherSystem`, `SwellWind`, `SimWorld` and `WeatherPanel` still use the flat field. Swap them during M2, then delete `WeatherField`, `WeatherMap` and their tests.

### Measured (2026-11-26, Release, this dev machine, with other sessions building)

- **Field sample:** ~1.0 µs in a clean process.
  - It is 3.6 µs after other trig-heavy work in the same process (`SampleCost`); the island's flat field measures 1.6 µs there.
  - Plan with ~1–4 µs, not planets.md's 0.3 µs estimate.
  - Breakdown: the window takes ~90 ns and `Evaluate` ~30 ns. The rest is ~13 slot lives (hashing, divisions).
- **Cache sample:** ~0.3–0.36 µs.
- **Cache tick,** anchor moving at 30 m/s: median ~12 µs, p99 ~25 µs. The first tick takes 2–3 ms for 8 synchronous tile snapshots, cold JIT included (~0.8 ms warm).
- **Cache against field:** worst errors of 0.66 m/s wind, 0.003 cloud and 0.005 rain, under a storm over 30 s.
- **Smoothness:**
  - Per-tick change at fixed points while the anchor flies at 100 m/s: 0.0002 m/s.
  - Per 10 m step across a cube face corner: 0.019 m/s.
  - Across the antimeridian, 1 mm apart: 1e-5 m/s.
  - Per 10 m step over the poles: ≤ 0.014 m/s.

## Game coupling (`src/Game/WeatherSystem.cs`, `src/Sim/SwellWind.cs`)

- **Ticks:** `GameApp.Tick` calls `WeatherSystem.Tick(SimWorld.WeatherTime, camera)` (sim time + debug offset). The cost is recorded into the Weather panel.
- **Ocean wind (sim):** `SimWorld.Tick` samples it every `WindInterval` ticks (1 s), from the analytic `WeatherField` (the map is camera-centred) about a fixed reference point, the world origin. So the swell depends on the tick alone, never the camera or frame timing, and a headless host reproduces it.
    - The swell takes the **sea wind**: the mean wind speed over a 3 × 3 grid of samples 2 km apart, along the mean wind's direction. The waves at a point were raised over the water around it, and the averaging also hides a storm's calm eye.
    - A new `SwellParams` wind is queued only when the sea wind has moved by more than 1 m/s or 10°.
    - `OceanSwell` then crossfades over its 30 s `BlendSeconds`, and queues the latest change while blending. So a sea builds as a storm arrives, and at most one spectrum rebuild starts per 30 s.
    - Fetch, depth, groundswell and the other swell parameters are left as they are.
- **Per frame:** `WeatherSystem.Update` samples the map at the camera.
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
- **Sphere (`SphereWeatherTests`):**
  - **Determinism:** the same seed is bit-identical; a different seed differs.
  - **Lattice window,** checked bit for bit against a 200 km gather (0.4 rad on the gas giant). It covers 764 points, including both poles, 89.999°, 75°/76° and either side of the antimeridian, on a rocky world and on a gas giant with a tilted axis.
  - **Candidate-cell cap:** at most 3 per row below 75°, 4 above.
  - **Continuity** across the antimeridian, and over each pole while a system sits on it.
  - **Rotation sense** by hemisphere and kind, with inflow. Every lattice system spins with its hemisphere; within 0.5° of the equator, |spin| < 0.1.
  - **Zonal bands** alternate, and are still at the equator and the poles.
  - **Cache:**
    - It matches the field.
    - Its values don't depend on the anchors (bit for bit).
    - It is smooth tick to tick, with no synchronous bakes after the first tick.
    - It is continuous across cube faces.
    - A time jump rebakes it.
  - **Allocation:** none.
  - **Cost** (`Perf` trait).

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
