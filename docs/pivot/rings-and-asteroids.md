# Rings and asteroids

Status: design only. Nothing has been built or run, and no repo files were changed. Written 2026-11-25 against the pivot docs (README, planets, frames-and-zones, ship-hulls, review, and exotic-matter as it stands) and the current `src/Terrain` and `src/Physics`. Every cost here is an estimate until the profiler rows below measure it. Revised after a lead review; the changes are listed in "Review notes" at the end. Revision 2 adds weapons against asteroids (kinetic shatter, energy-weapon heat, antimatter breaches, anti-graviton pulses); its changes are listed in "Revision 2 notes". Revision 2 was then lead-reviewed and corrected in place; the corrections are listed in "Revision 2 review notes". Revision 3 (2026-11-25, also read against tractor-beams.md) integrates the user's answers to the first six open questions: permanent mining with replenishment events and bounded deltas, bystander damage, EVA sampling and cave mining, dense and sparse bands with dust fog, gas giant hosts, and tumbling, manipulable asteroids. Its changes are listed in "Revision 3 notes"; revision 3 was then lead-reviewed and corrected in place ("Revision 3 review notes", at the very end). The gas giant body type itself is proposed for planets.md (section "Gas giants").

## User decisions (fixed)

- Planetary rings are meshes from far away, a haze closer in, and real asteroids up close.
- Asteroids are made of well-packed material, so they cannot be dug directly. They must first be cut open or broken with a beam laser, and they may break apart violently.
- Like an egg in a microwave, too much energy causes a violent detonation.
- Mechanic: a laser warms the asteroid, then a wedge fired into it breaks it apart.
- Exotic matter (EM) occurs, impure, in asteroids and on planets. Here it is one of the asteroid materials. Its purity, rarity and privacy rules come from exotic-matter.md.
- (Revision 2) Big enough kinetic weapons shatter an asteroid regardless of the heat window: above a kinetic-energy threshold scaled by the asteroid's size and toughness, the hit fractures it directly. It is less controlled and more wasteful than laser + wedge mining (more fines lost, ore scattered, faster fragments), so mining skill still pays.
- (Revision 2) Energy weapons transfer heat into asteroids, but inefficiently compared with the mining beam laser (most of their energy is lost). They feed the same heat model, so they can still push volatile-rich rock to detonation, and the abuse limits cover weapons as well as mining tools.
- (Revision 3) Mined asteroids are gone forever, but belts are effectively infinite. Events (collisions, comet breakups, moonlet shattering) may bring asteroids back, but never the same asteroid: they get new ids. Deltas compact (for example a whole cell marked mined-out), so storage stays bounded.
- (Revision 3) Shrapnel and fragments from someone else's site damage bystanders. The abuse limits around protected zones still apply.
- (Revision 3) Handheld EVA mining exists: sampling (scanning and chipping samples for composition) and cave mining (players tunnelling by hand inside large, cracked-open asteroids).
- (Revision 3) Rings have both dense and sparse bands. A band's density is tied to its colour and visibility (dense = darker and opaque, sparse = faint and clear), so players can read richness and danger from afar. Each band also has a dust level that acts as fog: it cuts visibility and sensor range.
- (Revision 3) Gas giants exist (a new body type with no voxel surface) and host rings. Their atmospheres can be harvested by gas scooping (planets.md "Gas giants", proposed).
- (Revision 3) Asteroids are manipulable: they tumble a little (natural spin), can be moved with tractor beams, and can be used as weapons.

## Recommendation (summary)

- **A ring is a function of its seed, like a planet's surface.** Radial optical depth, gaps, composition bands, the vertical profile and the size distribution are all evaluated from seed and lane index. Only deltas are stored: removed asteroids, spawned bodies and edits.
  - **Bands (revision 3)** run from sparse to dense. One density parameter per band drives the body count, the optical depth and how dark the band looks, so dense bands read as dark and opaque and sparse ones as faint and clear. A separate dust level per band is fog: it lowers visibility, sensor range and, as a bonus, how far impostors must be drawn.
  - **Hosts (revision 3):** rocky planets and gas giants. Gas giant rings are wider and denser; their arc cells use the id's spare 12 bits.
- **Permanent mining (revision 3).** A removed natural asteroid never returns. Replenishment events (collisions, comet breakups, moonlet shattering) take over a footprint of cells with a new cluster whose ids come from a never-reused event serial. Cell deltas compact to mined-out bits and each region has a byte budget, so storage is bounded.
- **Asteroids are instanced per cell by integer hashing.** The CPU (authority) and the GPU (impostors) use the same integer hash and the same server-checked tables, so existence and identity match exactly. The ~5e8 asteroids of 2 m or more in a ring are never stored.
- **Ring material moves in rigid 512 m lanes.** Each lane turns at the Kepler rate of its centre radius, and the contents of a lane never move relative to each other. **Natural asteroids tumble a little (revision 3):** 70 % spin slowly about their max-inertia axis (rim speed ≤ ~0.12 m/s, periods 5 min–12 h by size), the rest are tidally locked. Spin is an integer phase of id and tick, as exact and free as the lane phase. Neighbouring lanes slide past each other at up to ~1.3 m/s, which is real Keplerian shear, applied in steps. A fit rule keeps every asteroid inside its lane and cell, so rail asteroids never overlap and never need collision with each other.
- **Detail ladder:**
  1. a lit annulus mesh, shadowed by the planet and casting its shadow onto it;
  2. a volumetric haze;
  3. GPU gravel points and instanced impostors per size class;
  4. real voxel bodies;
  5. Jolt bodies.

  One cross-section table drives every stage, so the crossfades conserve optical depth.
- **Asteroid bodies reuse the planet chunk path at a small scale:** an SDF (archetype + noise + composition), 32³ chunks, Surface Nets, `BrushOp` edits and `MassMoments`. Base voxels are 0.25, 0.5 or 1 m.
  - Far collision is a convex decomposition, as for hull exteriors.
  - Near characters, big bodies use the planets' LOD-0 chunk collision ring.
- **Miners work in a lane bubble.** This is a physics bubble whose axes turn with the worked asteroid's lane: a Hill frame, with the same fictitious-force rule as the rotating surface bubble. Non-spinning asteroids in that lane are **static** Jolt bodies, so they sleep, cost nothing per tick and never drift. Spinning ones are kinematic with a fixed angular velocity, resynced from the rail at 1 Hz. Asteroids in other lanes are kinematic, moved to their exact rail pose every tick. A disturbed asteroid under 1e8 kg (a tractor lock, a contact, a fracture) becomes dynamic and keeps its spin. Bigger ones stay static or kinematic, like terrain, and are mined by cutting fragments out of them.
- **Manipulation (revision 3):** a tractor lock frees a rock under 1e8 kg as a dynamic body; it can be towed, thrown or used as a shield within tractor-beams.md's caps (100 m/s relative to the emitter, 500 MJ of net beam work per body), plus one asteroid rule: no beam raises a rock's kinetic energy relative to its local lane past 500 MJ, so tow-and-release can't beat the throw cap (review fix). A rock released with over 50 MJ relative to its lane is tagged `Thrown`: it is swept against every entity like a fast ship until it slows, its rams are kinetic hits and ship damage attributed to the thrower, and each player has at most 8 live. Lighter releases are still attributed.
- **Mining loop:**
  1. Scan the rock.
  2. Warm a site with the beam laser. A 16³ heat grid runs only while the rock is being worked.
  3. Watch the temperature and the stability S.
  4. Fire a wedge once the site is in the window (the wedge seats and S < 1).
  5. A Voronoi fracture on the SDF splits the rock. It is computed by the server and sent as fragment summaries.

  Volatiles (and EM) store pressure energy as the rock heats. If the rock overheats and S reaches 1, the site detonates and throws fragments and shrapnel. This is the microwave egg.
- **Fragments by size:**
  - ≥ 8 m: still packed (but hand-diggable: revision 3). Repeat the cycle.
  - 0.5–8 m: loose chunks. Drill them, or swallow them whole.
  - < 0.5 m: a rubble cloud that the intake sweeps up.
- **EVA (revision 3):** hand scanners and chip samplers read composition anywhere without mining (samples can't be refined). Every fracture leaves a **damage zone** (1.5 × its radius) in the rock it broke, and fragments born from a fracture are fractured throughout. EVA players dig those zones by hand with a cold drill, and extend them with splitter charges, so caves can only start where a ship has already cracked the rock open. Cave mining is slow next to ship mining but precise: it follows veins and takes EM lenses out cold, with no heat decay.
- **Bystanders (revision 3):** outside protected zones, shrapnel, fragments and thrown rocks damage anyone, attributed to the event's owner. A public hazard beacon warns everyone within 5 km of a Critical site, a queued fracture or a thrown rock.
- **Weapons (revision 2):** a kinetic hit whose crack energy reaches `G_eff · F(10) · R_sh²` (with `R_sh = max(0.75 m, D/8)`) shatters the rock whatever its heat: no blade, 8–16 pieces, faster small pieces, and 15–40 % of the region crushed to fines, half of it lost. At the threshold a shatter breaks out the region round the hit; it breaks the whole rock once the crack reaches the far side (`R_f ≥ R_reach`), which a single hit can do only up to ~46 m of silicate. Energy weapons (lasers, particle beams, plasma, the AM lance) feed the same heat grid at 1–30 % of the beam's coupling, under one 10 MW absorbed-power cap per site. A contact AM breach (a torpedo warhead or a hit trap) is a crater plus an `AmBlast` fracture. AG pulses skip rail asteroids like terrain and never break rock. Every weapon effect is a server fracture event, inside the same energy, rate and per-bubble body caps, and asteroid debris does no damage inside protected zones.
- **Multiplayer:** the server owns heat, fracture, detonation and damage. Each fracture is one reliable event of ~56 B, plus ~46 B per fragment with its server-computed id, mass properties and velocity. Clients rebuild fragment geometry for visuals only. They never derive which fragments exist, their ids or their mass from their own float maths.

## Scale (reference: 1000 km planet at 1 g, μ = 9.81e12 m³/s²)

| Quantity | Value |
| --- | --- |
| Default ring | Inner radius 1.5 R = 1500 km, outer 2.2 R = 2200 km. This is inside the Roche limit: the planet's mean density at 1 g is ~35 t/m³, which gives a rigid-body Roche limit of ~3.3 R for 2 t/m³ rock. The ring lies in the planet's equatorial plane, in the **BodyInertial** frame, so it does not co-rotate with the surface. The system generator keeps moon orbits outside 1.2 × the ring's outer radius |
| Orbital speed, period | 2557 m/s and 61 min at 1.5 R; 2112 m/s and 109 min at 2.2 R. At 0.27 g (README Q1), speeds and shear scale by 0.52 |
| Lane shear (3/2·Ω·512 m) | 1.31 m/s at the inner edge, 0.74 m/s at the outer edge |
| Area | 8.1e12 m²: 1367 lanes of 512 m, and ≤ 27k class-0 arc cells per lane |
| Vertical profile | Gaussian, scale height h = 500 m (±1.5 km at 3h) |
| Face-on optical depth (densest band, ρ_b = 1; revision 3) | Peak τ = 0.6, almost all from particles of 1 cm–2 m. In the mid-plane the extinction is τ/(√(2π)·h) ≈ 4.8e-4 m⁻¹: a ~2 km mean free path and ~6 km visibility. A haze, not a wall |
| Asteroids ≥ 2 m | Peak 48 per 512 × 512 m column in a dense band (ρ_b = 1), ring mean ~16, so ~5e8 per ring; a sparse band holds 1–12 per column. ≥ 32 m: ~2e6. ≥ 256 m: ~3e4. Peak 3D spacing ~190 m. They add only ~1 % of τ |
| Asteroid gravity | Ignored. A 400 m silicate rock has ~1.5e-4 m/s² at its surface, against ~1.7e-3 m/s² of tide (inside Roche, loose material does not stay on). Players on asteroids use EVA and magboots |
| Natural spin (revision 3) | 70 % of rocks spin about their max-inertia axis. Rim speed ≤ ~0.12 m/s; centripetal ω²r ≤ ~1e-3 m/s² at the rim of a fast small rock, ~2e-5 m/s² on a 400 m rock. Magboots and heat sites don't notice it |
| Gas giant ring (reference, revision 3) | Host: R = 6,000 km at 1.2 g (μ = 4.24e14 m³/s², planets.md "Gas giants"). Ring 1.2–2.2 R = 7,200–13,200 km, outside the rotating band (top ~1.15 R), inside the fluid Roche limit (~3.7 R for 2 t/m³ rock; the rigid limit is ~1.9 R, so moonlets can survive in the outer ring). 7.7 km/s and 98 min at the inner edge, 5.7 km/s and 4.1 h at the outer. Shear 0.82–0.33 m/s per lane. 11,719 lanes, 3.9e14 m² (48× the rocky ring), up to 162k class-0 arc cells per lane (needs the widened arc field, see Identity). ~2e10 bodies ≥ 2 m at ring-mean density (review fix: was ~1e10) |

## Ring definition (function, no storage)

```
G(r)     = prod_g (1 - depth_g * gap_g(r))                                                                    gaps and ringlets
tau(r)   = TauRef * sum_b rho_b * band_b(r) * (1 + 0.4 * fbm1(r / lambda_b)) * G(r)                            particles 1 cm-2 m (TauRef 0.6)
tauD(r)  = TauDustRef * sum_b D_b * band_b(r) * (1 + 0.6 * fbm1(r / lambda_d)) * G(r)                          dust < 1 cm, fog only (TauDustRef 1.5; revision 3)
column(r)= PeakColumnCount * sum_b rho_b * band_b(r) * G(r)                                                     bodies >= 2 m per 512 m column (no fbm: integer-built)
n(r, z)  = column(r) * exp(-z^2 / (2 h^2)) / (sqrt(2 pi) h)                                                     bodies >= 2 m
nD(r, z) = tauD(r) * exp(-z^2 / (2 hD^2)) / (sqrt(2 pi) hD)                                                     dust extinction, hD = 1.5 h (dust puffs up)
```

- **Bands:** 2–6 seeded radial bands with smoothstep edges. Each band carries:
  - a **composition**: weights over ice-rock, silicate, carbonaceous and metal;
  - an `EmFraction`: the share of asteroids that bear EM. The default is 0.02, and 0.3 near a black hole (exotic-matter.md);
  - a colour and albedo derived from the composition;
  - (revision 3) a **density** `ρ_b` in [0.02, 1], which scales the band's body count, its particle optical depth and its darkness together (see "Density, dust and what players read");
  - (revision 3) a **dust level** `D_b` in [0, 1], which sets its fog (`τ_D`), independent of density, so a sparse band can still be a blind one.

  Players can therefore prospect by eye: hue is composition, darkness and opacity are density (richness and collision danger), milkiness is dust (blindness). EM is never visible this way (see Composition).
  - `ρ_b` stops at 1 because the class-0 slot clamp (63) would otherwise start cutting the mean count: at ρ_b = 1 the peak class-0 expectation is ~36, and the clump multiplier takes it to 63. The densest band is the revision 2 default, so every GPU estimate below is already the dense case.
  - `ρ_b` enters `CountFx` as a 0.16 fixed-point integer from data, so existence stays integer-exact (determinism rule below). Dust never affects existence; it is render and sensor data only.
- **Gaps:**
  - Resonance gaps from moons at `r = a_moon · (p/q)^(2/3)` (2:1, 3:2), each 0.5–3 % of r wide, seeded.
  - Plus 0–4 seeded gaps, and 0–3 narrow ringlets (positive bands under 2 km wide).
- **Size distribution: a broken power law with two populations.**
  - **Particles, 1 cm–2 m:** cumulative `N(>D) ∝ D^-α_s` with α_s = 2 by default, normalised so their cross-section gives τ(r). These are visual only: the haze and the gravel points.
  - **Bodies, 2 m–400 m:** cumulative `N(>D) ∝ D^-α_b` with α_b = 2 by default, normalised by `column(r)` (`PeakColumnCount`). These are the instanced asteroids.
  - The two populations are not one continuous law. A continuous law would put ~100× more bodies above 2 m into the ring. Real rings also have a sharp upper size cutoff, with rare moonlets.
  - **Cross-section table:** `Aless(D)` is the cumulative cross-section of everything smaller than D, from both populations. It is a 64-entry log table per band, and it drives every crossfade.
- **Size classes** (bodies): 8 classes of one octave each. Each class has its own arc cell length, so cell enumeration stays bounded at every draw range.

| Class | D (m) | Share of count | Arc cell L_c (nominal) | Impostor range (300·D_hi) | Voxel-body range (40·D_hi, 100 m..8 km) |
| --- | --- | --- | --- | --- | --- |
| 0 | 2–4 | 75 % | 512 m | 1.2 km | 160 m |
| 1 | 4–8 | 19 % | 512 m | 2.4 km | 320 m |
| 2 | 8–16 | 4.7 % | 512 m | 4.8 km | 640 m |
| 3 | 16–32 | 1.2 % | 512 m | 9.6 km | 1.3 km |
| 4 | 32–64 | 0.29 % | 1 km | 19 km | 2.6 km |
| 5 | 64–128 | 0.073 % | 2 km | 38 km | 5.1 km |
| 6 | 128–256 | 0.018 % | 4 km | 77 km | 8 km |
| 7 | 256–400 | 0.004 % | 8 km | 120 km | 8 km |

- **Arc cells nest.** Lane i has `ArcCells0_i = 16 · round(2π r_i / (16 · 512))` class-0 cells. Class c has `ArcCells0_i / 2^max(0, c−3)` cells, each `2π r_i / count` long, so the real lengths are within 0.1 % of nominal. A cell of a larger class therefore holds whole cells of the smaller classes.
- The physics range is not a per-class distance. See Realisation.

**Generator** (`RingGenerator`, at system load on a job, ~1 ms):
- Builds the `LaneTable`. Per lane and class it holds the expected count as 16.16 fixed point and the arc cell count. Per lane it holds the composition band, τ and (revision 3) τ_D and ρ_b for the render texture and the sensor model, and the lane phase rate.
- (Revision 3) Builds the per-class **spin tables** (64 periods per class, log-spaced between the class's bounds, as `SpinRate` 0.32 fixed point; see Identity).
- Builds the class size-quantile tables (256 entries of D in cm) and the cross-section table.
- The 256-entry z-quantile table of the unit Gaussian and the `(p/q)^(2/3)` resonance factors are **constant tables in code**, scaled by h in integer.
- **Determinism rule:** every table that affects existence or identity (`CountFx`, arc counts, D quantiles) is built only with `+ − × ÷`, floor and sqrt, in a fixed order. `Math.Pow`, `Exp`, `Erf` and trig are banned there; the planets.md generator uses the same rule.
  - For α ≠ 2, D quantiles use a fixed-point integer power routine.
  - The server sends a CRC of each ring's tables on interest entry. If the CRC does not match, the client downloads the tables instead: ~100 KB, zlib.
- Ring parameters come from the system generator (seeded) or from `data/systems/*.json` overrides.

**Ring hosts (revision 3).**
- **Rocky planets** (the 1000 km voxel worlds): 0–1 ring, usually narrow and sparse to medium (band presets weighted toward ρ_b ≤ 0.5), often clear.
- **Gas giants** (planets.md "Gas giants", proposed: no voxel surface, rotating band, no landing): 1–3 rings, wide, with many bands weighted toward dense, and dustier on average. They are where the dense, dark, rich rings are. The giant's moons cut resonance gaps as usual.
- **Placement rules for any host:**
  - inner radius ≥ max(1.15 R, R + 1.3 · atmosphere top + 10 km), so the whole ring is in the host's **BodyInertial** frame and never inside its rotating band (for the reference giant: 1.2 R against a band that ends at ~1.15 R);
  - moon orbits stay outside 1.2 × the outer radius (unchanged);
  - a ring wider than 16,384 lanes (8.4e6 m) is split into adjacent sources, one per 16k lanes, so the 14-bit lane field holds.
- **Gas giant radiation belts** (planets.md) are depleted across a ring's radial span: rings are radiation shelters, gaps and the space outside them are not. That makes ring slabs the safe place to work round a giant.
- The ring shadow term (`ring_shadow.glsl`) is included by the gas giant's cloud and atmosphere shaders too, so a giant shows its ring shadow on its cloud tops.

## Lanes, cells and identity (the instancer)

**Lane frame.**
- Lane `i` covers radii `[R_in + 512 i, R_in + 512 (i+1))`.
- **Lane phase:** `phase_i(tick) = tick · F_i`, a wrapping `ulong` in 0.64 fixed point (revolutions per tick).
  - `F_i = round(2^64 · Ω_i · dt / 2π)`, computed once from `Ω_i = sqrt(μ / r_i³)` using only sqrt, mul and div, so it is identical on every peer.
  - The phase is exact forever. This is the same idea as frames-and-zones' `tick % PeriodTicks`, for rates that are not a whole number of ticks.
  - Render interpolation adds `frac · F_i` in double.
- An asteroid's planet-inertial pose is `Rz(2π · phase_i) · p_lane`, and its orientation is `Rz(2π · phase_i) · q_lane(tick)`.
  - `p_lane` is fixed: cylindrical (r, arc, z) in the lane frame, int64 at 1/1024 m (C13). Radii reach 2.25e9 units, which overflows int32 (1.35e10 units round a gas giant).
  - `q_lane(tick) = q_hash · R(â_s, 2π · spin(tick))` (revision 3). `q_hash` is the hashed orientation. `â_s` is the body's **max-inertia axis** (the archetype's shortest scaled axis), so the rotation is the torque-free stable state: a fixed axis and rate, with no precession, exact in closed form.
  - **Spin phase:** `spin(tick) = (uint)(tick · S)`, a wrapping `uint` in 0.32 fixed point (revolutions per tick), with `S` from the class spin table. The GPU computes the same product from the low 32 bits of the tick in `uint` arithmetic, which wraps identically, and adds `frac · S` for interpolation.
  - **Spin share and rates:** `SpinFraction` (0.7) of rocks spin, by one hash byte; the rest are tidally locked (`S = 0`) and turn once per orbit with their lane, as before. Periods are log-uniform per class, chosen so the rim speed `π·D/P` stays ≤ ~0.12 m/s:

    | Classes | D | Period range | Rim speed at the fastest |
    | --- | --- | --- | --- |
    | 0–1 | 2–8 m | 5–30 min | ≤ 0.08 m/s |
    | 2–3 | 8–32 m | 15 min–2 h | ≤ 0.11 m/s |
    | 4–5 | 32–128 m | 1–5 h | ≤ 0.11 m/s |
    | 6–7 | 128–400 m | 3–12 h | ≤ 0.12 m/s |

**Cell.** A cell is `(source, class, lane, arcCell)`. Cells are 2D: z is drawn from the vertical quantile table, so there are no vertical cells.

**Per cell, all integer:**
1. `h = Hash32(sourceSeed, cellKey)`. The same function lives in `AsteroidHash.cs` and `asteroid_hash.glsl`.
2. **Count:** `k = ((CountFx[lane, class] · ClumpMul[h >> 28] >> 4) + (h & 0xFFFF)) >> 16`.
   - This is stratified rounding times a 16-level clump multiplier in 1/16 units (0.25×..1.75×, mean exactly 1, so τ is preserved).
   - There is no Poisson `exp`, so the count is bit-identical on CPU and GPU.
   - `k` is clamped to 63 slots. At the peak the class-0 expectation is ~36, so the clamp never cuts the mean.
3. For each slot `s < k`: `hs = Hash32(h, s)`.
   - D comes from the class quantile table: integer cm, indexed by one hash byte.
   - The position follows the **fit rule**:
     - radius in `[r_i − 256 + D/2, r_i + 256 − D/2]`;
     - arc in `[D/2, L_c − D/2]`;
     - z from the quantile table.
   - D/2 is the radius of the archetype's **bounding sphere** (archetypes are normalised to the unit sphere before axis scaling), so the fit and overlap tests hold at every orientation: spin can never make two rail rocks touch (revision 3).
   - Spin (revision 3): one byte against `SpinFraction`, a 6-bit index into the class spin table, and the axis is fixed by the archetype and axis scales. No extra hash call.
4. **Overlap rejection, integer-exact, non-recursive.** Tests use 1/16 m units in int32, so squared distances stay under 2^31 and no GPU int64 is needed. Radii are padded up by one unit, so the test is conservative.
   - A slot is dropped if it overlaps an earlier slot of the same cell.
   - A slot is also dropped if it overlaps any **candidate** (before rejection) of a larger class in the containing cell of that class. Testing candidates instead of survivors avoids recursion through the classes.
   - Because cells nest and the fit rule holds, the containing cell is the only cell that can overlap.
   - No rail asteroid ever overlaps another, so rails need no collision.

**`AsteroidId`** (`ulong`): `source 13 | class 3 | a 14 | b 16 | c 12 | slot 6`.
- Ring: a = lane (≤ 16k lanes), b = arc cell low 16 bits, c = arc cell high 12 bits (revision 3). Arcs reach 2^28 cells, r ≤ 2.2e10 m at 512 m cells; revision 2 kept c = 0 and so stopped at r ≤ 5.3e6 m, short of every gas giant ring (162k cells per lane at 13,200 km).
- Field: a, b, c = cell xyz relative to the field centre, biased (b and c cover ±2048 cells, past the 5a cutoff).
- **Event cluster (revision 3):** `source = 8191 (EventSource) | class 3 | serial 30 | cell 12 | slot 6`. `serial` is the system's replenishment-event counter, which only ever increases (2^30 events), and `cell` indexes the cell inside that event's footprint, per class. A cluster's asteroids therefore never share an id with a natural asteroid or with any earlier event's, which is the user's "never the same asteroid".
- `source` counts rings, ring segments and fields per star system: up to 8191, against the 2000+ fields a belt can hold, with 8191 reserved for event clusters. Ids are scoped per system.
- Ids are never stored for natural or event-cluster asteroids. An id is the key for deltas, events and shape caches.

**Parity rule.**
- Existence, identity, D, bulk composition and spin (revision 3) are integer and match exactly between CPU and GPU, and between peers (CRC-checked tables).
- Poses match to float rounding (sub-mm), which only matters visually.
- A unit test compares CPU and GPU outputs on a reference set by test-time readback, never in the frame loop.

**Why rigid lanes.**
- If each asteroid ran its own Kepler rate, shear would carry rocks at a lane's edges ~56 km a day away from its centre, or ~110 km a day apart across one lane. Cell lookup would break, and rail asteroids would interpenetrate.
- With rigid lanes, the contents of a cell are fixed forever. Neighbours drift at most ~1.3 m/s across a lane boundary that no asteroid straddles.

**Accepted error.** A lane is a circular orbit at its centre radius, held rigid in r and z. A freed asteroid feels the real Hill-frame forces:
- the radial tide `3Ω²Δr` (up to 2.2e-3 m/s² at a lane edge);
- the vertical tide `−Ω²z` (2.9e-3 m/s² at 1 km);
- Coriolis `−2Ω×v`.

So it drifts off its rail position: at a lane edge a rail rock is ~0.65 m/s off a free orbit. That is real physics; the rigid lane is the approximation. A ship hovering beside a rail asteroid drifts the same way and needs light station-keeping.

## Free belts and fields (around the star)

- A **belt** is a seeded annulus in the Solar frame (for example 2–3.5 AU) that holds **fields**: 100–2000 seeded clusters.
  - Real belts are empty at gameplay scale, so only fields are populated.
  - Trojan fields sit on a planet's orbit at ±60° phase. On patched-conic rails this is simply the planet's orbit with a phase offset.
- **A field is a frame:** `FrameKind.Field`, on Kepler rails, with no SOI, no gravity and no spin.
  - It is allocated in `FrameTree` only while a player is within 200 km; otherwise it is evaluated on demand.
  - Members are rigid in the field frame with no shear (shear at 2.5 AU is ~4e-5 m/s per 512 m anyway). They spin by the same `SpinFraction` and class tables as ring members (revision 3).
- **Field profile:** Plummer, `n(ρ) = n0 · (1 + ρ²/a²)^(-5/2)`, with a = 2–20 km and a cutoff at 5a.
  - λ per cell comes from a 1024-entry table indexed by the **integer** squared distance of the cell centre, so parity holds.
  - Bulk composition comes from the belt band at the field's semi-major axis.
  - (Revision 3) Each field style carries the same density and dust levels as ring bands: density scales `n0` and the Plummer haze's darkness, dust adds a Plummer dust term (`a_D = 1.5 a`) that fogs vision and sensors.
  - `EmFraction` comes from exotic-matter.md: 0.02 by default, 0.3 near a black hole.
- **Cells:** 3D cartesian per class, as `L_c` cubes (the same `L_c` as rings, nesting the same way), with the same fit and overlap rules and the same `AsteroidId`.
- **Rendering:**
  1. A point of light while it is under ~2 px.
  2. A Plummer haze: closed-form column density `∝ (1 + b²/a²)^-2` from outside, 8 march steps from inside.
  3. Then the same gravel, impostor and body stages as rings.
- **Remnant debris arcs** (exotic-matter.md) use the lane instancer around the star, with an azimuthal window of ±10°, instead of a separate band scheme.

## Ring detail ladder (rendering)

| Stage | When | What is drawn | Cost (est.) |
| --- | --- | --- | --- |
| 1. Annulus | Planet ≥ 2 px, and the slab's projected thickness is under 8 px or the camera is > 300 km from it | A thin annulus mesh: top and bottom faces at ±1.5h plus an outer and inner rim strip, so it never collapses to zero pixels edge-on. The fragment shader takes r and samples a 1D 8192-texel texture (τ, τ_D, colour, albedo, darkness; two RGBA16F texels, revision 3). Alpha = `1 − exp(−(τ + τ_D)/max(|μ_view|, μ_min))`, where μ_min keeps the edge-on view finite. Particles: HG g = −0.3 (boulders), colour = composition albedo × `(1 − 0.6·ρ_b)`, so dense bands are dark. Dust: HG g = +0.75 in a pale dust colour, so dusty bands glow milky when backlit and soften band edges (revision 3). The planet's shadow is analytic (a cylinder, with penumbra from the sun's angular size). Planetshine is ambient light from the planet's albedo × phase | ~0.02–0.05 ms |
| 2. Haze | Within 300 km of the slab, or once its projected thickness is ≥ 8 px (crossfade over 20 %) | A half-res ray march of the slab segment: 24 steps, 32 when grazing, stopping at scene depth. Extinction is `σ(r)·g(z)·f_unres(s) + σ_D(r)·g_D(z)` (revision 3: the dust channel is never resolved into particles, so it is always haze; its own scale height and phase function). Sun visibility through the planet is tested per step, and self-shadow uses the analytic slab term (below) for both channels. Bilateral upsample | ~0.15–0.3 ms, only near the ring; the dust channel adds ~0.02–0.05 ms of ALU |
| 2b. Gravel | Within 1 km of the camera, inside the slab | GPU-only points for 2 cm–2 m particles with `D ≥ s/300`. They sit in a camera-wrapped grid of 32 m cells, and each point takes the phase of **its own lane** (lane index from its radius), so gravel shears with the rails. ~60k points at peak density. No collision | ~0.05 ms |
| 3. Impostors | Per class, `voxel range ≤ d < 300·D_hi`, and (revision 3) only while the line of sight's optical depth `τ_los < 5.5` (transmittance > 1/255) | A compute pass enumerates in-range cells per class, hashes them (event-owned cells with their event's seed), skips removed, mined-out and realised cells and ids (GPU sets), culls cells hidden by fog (`τ_los` from the closed-form slab integral, below), and writes instances. It also composes each instance's spin rotation from the tick's low 32 bits (one sincos per instance, not per vertex) and writes the rotated orientation into the instance record, so the vertex shader is unchanged. 16 archetype meshes × 3 LODs (1280/320/80 tris, by projected size), drawn with `DrawIndexedIndirectCount` (needs M1's GPU-driven path, review C8). Triplanar material from the bulk composition. Spawned bodies (fragments put back on rails) add a small CPU-uploaded instance list with their own baked low-poly meshes | Enumeration ~0.02–0.05 ms (~35k cells, mostly empty); draws ~0.15–0.3 ms for ~5–6k instances at peak (each class contributes ~700) |
| 4. Voxel body | `d < clamp(40·D_hi, 100 m, 8 km)` | The real asteroid: chunked SDF, Surface Nets, the planet LOD octree at asteroid scale, edits and craters | Per body; see Performance |
| 5. Physics | See Realisation | A Jolt body in the anchor's bubble | Server, plus client prediction |

**Transitions are crossfades over one cross-section table.**
- **Annulus to haze:** the annulus shades `τ(r)/|μ|`, which is exactly the haze's integral over a slab seen from far away. The crossfade only changes resolution, not brightness.
- **Haze to particles:** at march distance `s`, everything with `D > s/300` is drawn as a particle (a gravel point below 2 m, an impostor above). The haze keeps only the unresolved share: `f_unres(s) = Aless(s/300) / Aless(D_max)`, looked up in the per-band 64-entry table. Total optical depth is conserved, and the factor 300 also caps the gravel count (~60k within 1 km).
- **Impostor to voxel body:** the body's SDF is the archetype's SDF (the same one the impostor mesh was baked from) plus band-limited noise of amplitude ≤ 3 % of D.
  - At the 40·D switch the silhouette error is under ~0.08 % of the distance, about 1 px at 1440p.
  - Both are drawn during a 10 % distance band with dithered alpha.
- **Edits only close up**, as in the planets' detail ladder. Impostors never show craters or cuts.
  - A natural asteroid that is split, or loses more than 10 % of its volume, is converted: its slot goes into `RemovedSlots`, and the remainder becomes a spawned body with its own baked mesh LODs.
  - So a half-mined rock never appears whole at a distance next to its own fragments.

**Density, dust and what players read (revision 3).** Three channels, one per question a pilot asks from afar:

| Channel | Driven by | Looks like | Means |
| --- | --- | --- | --- |
| Hue | Composition weights | The material's albedo colour | What the ore is (never EM) |
| Darkness and opacity | Density `ρ_b` (τ = 0.6·ρ_b, darkening `1 − 0.6·ρ_b`) | Dense: dark, opaque, blocks the stars and casts a deep shadow. Sparse: faint, clear | How many rocks: richness, and collision odds |
| Milkiness | Dust `D_b` (τ_D = 1.5·D_b, scale height 1.5 h) | Glows when backlit, blurred edges, a halo above and below the slab | How blind you will be inside it |

| Density preset | ρ_b | Peak τ | Bodies ≥ 2 m per column | 3D spacing | Mid-plane visibility (no dust) | Hit odds per slab crossing (30 m ship) |
| --- | --- | --- | --- | --- | --- | --- |
| Dense | 0.7–1.0 | 0.42–0.6 | 34–48 | 190–215 m | 6–9 km | 10–15 % |
| Medium | 0.25–0.7 | 0.15–0.42 | 12–34 | 215–300 m | 9–25 km | 4–10 % |
| Sparse | 0.02–0.25 | 0.012–0.15 | 1–12 | 300–700 m | 25–300 km | 0.3–4 % |

| Dust preset | D_b | τ_D | Mid-plane visibility from dust alone | Dense band + this dust |
| --- | --- | --- | --- | --- |
| Clear | 0–0.1 | ≤ 0.15 | ≥ 38 km | 5.4–6.3 km |
| Hazy | 0.1–0.4 | 0.15–0.6 | 9–38 km | 3.8–5.4 km |
| Dusty | 0.4–0.7 | 0.6–1.05 | 5.4–9 km | 2.9–3.8 km |
| Choked | 0.7–1.0 | 1.05–1.5 | 3.8–5.4 km | 2.3–2.9 km |

Visibility here is the 5 % contrast distance `3/σ`, with mid-plane `σ = τ/(√(2π)·h)` per channel. The system generator draws ρ_b and D_b per band from the host's style (rocky: mostly sparse and clear; gas giant: mostly dense, often dusty). Replenishment events raise local dust for hours (Deltas).

**Fog and sensors (revision 3).** Dust and particles are a medium, not a post effect, so the server can use them:
- **Line-of-sight optical depth:** `τ_los(a, b) = Σ_k ∫ κ_k,s · σ_k(r, z) ds` over the two channels k (particles, dust) and a sensor kind s. It is evaluated on public ring data: an 8-point Simpson rule along the segment in lane cylindrical coordinates, ~0.3 µs, with a per-bubble cache of the local σ for short paths (~20 ns). `RingObscuration.Transmittance(a, b, kind)` is the function a future sensor doc calls; no sensor doc exists yet.
- **Coupling per sensor kind** (`κ_part` / `κ_dust`, data): optical and IR imaging 1 / 1; thermal (long-wave) 1 / 0.5; radar and lidar ranging 0.5 / 0.02 (cm–m particles scatter radar, µm dust barely does); gravimetric, hold-field and AM containment signatures (exotic-matter's 2 km signature) 0 / 0.
- **Range:** a passive sensor detects while `(R_nom/R)² · T ≥ 1`, and an active one while `(R_nom/R)⁴ · T² ≥ 1`. Both give `R ≤ R_nom·√T`; in a uniform medium `R_eff = (2/σ_s)·W(σ_s·R_nom/2)` (Lambert W). Examples for a 50 km sensor in the mid-plane:

  | Medium | Optical | Radar |
  | --- | --- | --- |
  | Sparse (ρ_b 0.05), clear (σ_opt ≈ 2.4e-5 /m) | ~33 km | ~39 km |
  | Medium (ρ_b 0.5) + hazy dust (D_b 0.2; σ_opt ≈ 4e-4) | ~8.7 km | ~17 km |
  | Dense + choked (σ_opt ≈ 1.3e-3, σ_radar ≈ 2.6e-4) | ~4 km | ~11.5 km |

  So radar beats dust, optics go blind in it, and signature sensors ignore rings: hiding in a choked band works against eyes, not against a hot drive.
- **Rendering cull from the same integral:** impostor enumeration computes `τ_los` from the camera to each cell centre with the closed-form slab term (`σ0(r_mid)` × the erf of the z-crossing; ~15 ALU + one rational erf) and drops cells past `τ_los = 5.5`. In a dusty band that cuts impostor draws by roughly 30–70 % for ~10 % more enumeration ALU, so fog pays for itself (measure). The cull is per cell, so looking out of the slab never culls what is visible.
- **HUD:** the orbital and local HUD show the current band's density and dust presets, and the predicted visibility and radar range along the flight path.

**Shadows.**
- **Ring on planet:** terrain, hull, impostor and atmosphere shaders intersect the sun ray from the surface point with the equatorial plane.
  - In the rotating surface frame that plane is static (the ring normal is the spin axis), and τ is axisymmetric, so spin does not matter.
  - Transmittance is `exp(−τ(r)/|μ_sun|)`: about 10 ALU and one fetch (`ring_shadow.glsl`).
  - Obliquity gives seasonal ring shadows for free.
  - This depends on the per-body sun direction, which is review's missing system 9.
- **Inside the slab** (ships, asteroids, the haze self-shadow): `T = exp(−σ0(r) · h · sqrt(π/2) · erfc(z_p/(h√2)) / |μ_sun|)`, toward the side the sun is on. The CPU evaluates it per ship for lighting; shaders use a rational erfc approximation.
- **Planet on ring:** analytic, as above.
- **Mutual shadows between asteroids and ships** wait for a scene shadow system, which does not exist yet.

**Precision.** Impostor and gravel positions are computed camera-relative in **lane cylindrical coordinates**.
- Per frame, for each in-range lane, the CPU uploads the camera's position in that lane's frame:
  - the arc cell index as an int, and the fraction inside it as a float;
  - (Δr, z) relative to the lane, as floats computed in double.

  That is 16 B × ≤ 1367 lanes ≈ 22 KB into a persistent mapped buffer.
- The GPU forms `Δarc = (cell − camCell, wrapped) · L_c + offset − camFrac`. It works from the integer cell difference, so it never handles a 1.4e7 m arc in float.
- `Δx = Δr·cosΔθ − 2·r_cam·sin²(Δθ/2)`. Every magnitude stays under the draw range (≤ 120 km, 1.5 cm float ulp), so there is no cancellation at 2e6 m.

## Realisation and physics (frames, bubbles, rails)

**States.**
- **Rails (default):** a natural asteroid has no entity, no body and no bandwidth. Its pose is evaluated from id and tick.
- **Realised:** it has a Jolt body in a bubble. A natural asteroid is realised when one of these covers it:
  - **a near sphere:** within `150 m + D/2` of an anchor;
  - **a swept capsule:** along the anchor's velocity relative to the local lane, over the next 3 s, with radius `100 m + anchor radius + D/2`, while the relative speed is ≤ 300 m/s;
  - **any active heat site, tractor lock or wedge in flight** on that asteroid.

  This replaces frames-and-zones' generic "≤ 2 km of an anchor" for rail scenery, as planets' collision ring does for terrain. At peak density it realises ~5–15 asteroids per anchor, against ~280 for a 2 km sphere. Realisation is checked at 10 Hz, staggered.
- **Kinds of realised body:**

  | Asteroid | Bubble | Jolt motion |
  | --- | --- | --- |
  | Not spinning, in the bubble's own lane, or a field member in its field bubble | Lane or field bubble | **Static**, created with `AllowDynamicOrKinematic`. It sleeps and costs 0 per tick |
  | Spinning (revision 3), in the bubble's own lane or its field bubble | Lane or field bubble | **Kinematic** with its rail angular velocity set once (`SetAngularVelocity`); Jolt integrates the rotation. `MoveKinematic` resyncs it to the exact rail pose at 1 Hz, staggered (float drift over 1 s is sub-µm). Jolt leaves `MoveKinematic`'s one-tick correction velocity on the body, so the next tick sets the rail ω (and zero linear velocity) again. ~0.05 µs per tick amortised, plus Jolt's per-step cost for an active body (~0.2–1 µs: broadphase update, no contacts with rails) |
  | In any other lane (neighbours slide at ≤ 1.3 m/s), or in a plain FreeFall bubble | Any | **Kinematic**, moved to its exact rail pose (spin included) by `MoveKinematic` every tick: ~0.3 µs each, ≤ 64 bodies ≈ 20 µs. No drift and no resync rule |

  - **Cost of spin (revision 3):** a lane bubble holding any spinning realised rock has active bodies, so its Jolt `Update` is no longer skipped: ~20–50 µs per lane bubble per tick as a small single-threaded scene, where revision 2 had 0 for an all-static own lane. With ~70 % of rocks spinning, practically every lane bubble steps. 16 mining bubbles cost ~0.3–0.8 ms of server CPU a tick, in parallel jobs. Measure in `--zone-bench`; the knob is `SpinFraction`. Freezing slow spinners as static while realised was rejected: the rail pose would jump when the rock re-rails.

**Lane bubble (new `BubbleAnchor.Source`, generalising exotic-matter's `MinorBody`).**
- **When it forms:** an anchor inside a ring slab with < 50 m/s relative to its local lane gets a bubble in `FrameKind.Lane`.
  - A lane frame is a deterministic virtual frame (ring, lane index), rotating about the spin axis at the lane's phase. Every peer builds it from the tick with no allocation message.
  - The bubble's lane is the lane of the asteroid being worked; otherwise it is the anchor's lane.
- **Gravity:** exactly the rotating-surface rule (C1, C2).
  - Jolt gravity = `g(o) − Ω×(Ω×o)`, which is ~0 when the origin is on the lane centre.
  - Each **awake** body gets `m·(g(p) − g(o) − Ω×(Ω×(p−o))) − 2m·Ω×v`.
  - Sleeping bodies get nothing, and neither do kinematic rocks (spinning or not): they follow their rails, so the force pass skips them (revision 3).
- **Rebase:** in 32 m steps of lane coordinates, as surface bubbles do. Static and own-lane kinematic asteroids move by `SetPosition` (~1 µs each).
- **Merging:** bubbles in sibling lane frames of the same ring are merge-compatible. The merged bubble keeps the frame of the bigger worked asteroid, and the other bodies transfer with the frames-and-zones rotating-frame maths.
- **Field bubbles** use the field frame the same way, with no rotation and no Coriolis.
- **The wire speaks frames:** placements are in the lane or field frame while the entity is in such a bubble. For interest, sibling lanes of one ring count as one frame.

**Disturbed → dynamic** (mass < `KinematicMassLimit`, 1e8 kg ≈ 40 m silicate):
- **Triggers:** a tractor lock, a contact with Δv > 0.3 m/s, or being a fracture fragment. A tractor **sweep** never converts a static or kinematic rock (tractor-beams.md).
- **Switch:** `BodyInterface.SetMotionType`. It is bound in `Brutal.Jolt`; add it to `JoltRuntime`'s export probe. It needs `AllowDynamicOrKinematic` at creation. The fallback is destroy and recreate with the same shape (~5 µs, the transfer pattern).
- **Spin carries over (revision 3):** the new dynamic body starts with its rail velocity (lane point velocity, zero in its own lane) and its rail angular velocity, so a freed rock keeps tumbling where it was. Torque-free spin about the max-inertia axis is stable, so it stays tidy until something hits it.
- **Mass and inertia** come from server `MassMoments` (X3), not from shapes.
- **Cap:** 32 contact-freed natural asteroids per bubble. Past the cap, contacts no longer free rocks: they behave as static, so ramming cannot turn a field into awake bodies.
- **Bookkeeping:** a natural asteroid that goes dynamic sets its `RemovedSlots` bit and becomes a spawned body.
- **Above the limit,** asteroids stay static or kinematic forever, like terrain. They are mined by cutting fragments out of them.

**Coasting debris (perf).** A dynamic fragment, freed rock or loose chunk can leave Jolt and coast. It does so when it is contact-free, not tractored, not `Thrown` (revision 3, see Manipulation), outside every near sphere and capsule, and more than 500 m from every player.
- It then follows the closed-form Hill (Clohessy–Wiltshire) solution in its lane frame, or a straight line in a field frame.
- It is evaluated on demand and costs 0 per tick. It re-enters Jolt at 400 m.
- One state message (~48 B) gives clients the same elements.

**Leaving.** A body leaves when it is outside every bubble: frames-and-zones' rule, > 3 km from anchors and contact-free.
- **Packed bodies (≥ 8 m) are re-railed** as a `SpawnedBody` delta in the lane or field frame. Nobody is within 3 km, so dropping its velocity is invisible. Its spin is snapped (revision 3): to its max-inertia axis at the nearest class table rate if `|ω| ≥` the class minimum, else to 0 (static), and stored in 3 bytes of the record. The re-rail placement:
  1. Snap the radius so the body sits wholly inside one lane. If it is wider than the lane margin allows, it stays coasting.
  2. Test for overlap against natural rails (instancer query) and other spawned bodies in the cell.
  3. On overlap, try 8 arc offsets of up to D.
  4. If all fail, it keeps coasting and retries at the next membership check.

  This keeps the no-overlap invariant for everything on rails.
- **Loose chunks and clouds** despawn instead (the mass is lost; accepted).

**Anchors.**
- Asteroids are not anchors by themselves. Only players, things touching simulated bodies and **active heat sites** anchor.
- A site keeps its bubble alive until it is freed, so a hot rock never resolves with no physics around it (the same rule exotic-matter applies to armed charges).

**Fast transit** (relative speed > 300 m/s inside a slab or field):
- Nothing is realised. Each tick, the server sweeps each fast ship's bounding capsule against the instancer: the cells along the sweep, ~20–50 µs per ship.
- A hit is an impact event:
  - Ship damage uses the reduced mass: `½·μ·v_rel²` with `μ = m_s·m_r/(m_s + m_r)`, which is about the ship's own KE for any big rock.
  - The asteroid takes a `KineticHit` with `E_p = ½μv²` at the contact point (revision 2): a shatter above the threshold, otherwise a crater and maybe a crack. See "Weapons against asteroids".
- Gravel and dust do no damage (a game choice).
- Crossing the peak slab of a dense band once in a 30 m ship gives ~15 % odds of hitting a body ≥ 2 m (scaled by ρ_b: ~1 % in a sparse band). The orbital HUD warns of ring crossings on the predicted path, with the band's density and dust presets.

**Tumbling (revision 3).** What spin changes, and what it doesn't:
- **Rails, impostors, the instancer:** nothing but the orientation term. Spin is an integer phase; existence, the fit rule and no-overlap are untouched (bounding spheres).
- **Heat sites** were already in body-local axes, so the grid rides the rock. The **beam laser keeps a body-local aim point** (like a tractor lock point) and its turret slews to follow it; at ≤ 0.12 m/s of rim speed the spot moves ≤ 1.2 cm between 10 Hz traces.
- **Wedges and projectiles** sphere-trace bodies at the tick's pose, spin included. The seat test already uses `v_surface`, which now includes `ω × r` (≤ 0.12 m/s: no measurable change).
- **Characters** stand on a spinning kinematic rock as a moving platform (`CharacterVirtual` takes the ground body's point velocity). Centripetal and Coriolis terms are ≤ 1e-3 m/s² and are ignored. The near-character LOD-0 chunk collision ring on a rock ≥ 32 m becomes sub-shapes of the rock's `MutableCompound`, rebuilt when the ring changes, not per tick (P1 still applies).
- **Big rocks (≥ `KinematicMassLimit`)** spin too (3–12 h for 128 m and up) and stay kinematic forever. They can't be pushed; tractors treat them as grapple points.
- **Re-railed bodies** keep a snapped spin (above), so a returned boulder tumbles like its neighbours.

**Manipulation (revision 3).** Tractor rules are tractor-beams.md's; this doc adds only what rock needs:
- **Locks:** a Hold or Haul lock on a natural or event rock under `KinematicMassLimit` turns it dynamic (with its spin, above), sets its `RemovedSlots` bit and makes it a spawned body. Above the limit the rock is a grapple point (the emitter moves). Sweeps deliver force only to bodies that are already dynamic. A Hold lock damps spin across the beam (despin emerges from per-hit radial damping).
- **Caps (tractor-beams.md, unchanged):** a beam adds no speed past `BeamVMax` (100 m/s) relative to its emitter, and every beam on a body shares one signed work ledger `W_b ≤ EThrow` (500 MJ, equal to `E_cap` here), not reset by catching up and re-pushing. A 1e6 kg rock tops out at ~32 m/s, a 1e8 kg one at ~3 m/s.
- **Rail-relative energy cap (review fix, proposed to tractor-beams.md):** `W_b` is measured in the emitter's frame, so a steady tow does ~0 work. A tug could hold a rock, accelerate itself to 200 m/s and let go: a 1e6 kg rock carrying 20 GJ with `W_b` ≈ 0, untagged. For asteroid bodies the fade therefore uses `max(W_b, E_rail)`, with `E_rail = ½ m |v − v_rail|²` relative to the local lane point velocity (the field frame in fields). A beam force that would raise `E_rail` past `EThrow` is faded to zero (braking is never faded), so a tow can't carry a rock past 500 MJ relative to its lane: the rock lags, and the lock stretches and breaks at range. Cost: one dot product per (beam, asteroid body).
- **Refusals (unchanged):** locks are refused within `ProtectRadius` of protected zones and on bodies whose site has S > 0.3; a locked body accepts no beam heat; sweeps deliver zero to asteroid bodies in protected zones. A tractor never fractures rock.
- **Attribution of every release (review fix):** any asteroid body a beam has touched carries `LastBeamOwner` (the owner of the last emitter to deliver force) until it is re-railed or back under 5 m/s relative to its lane. Its contact damage is attributed to that owner even below the `Thrown` threshold, so many 49 MJ throws are not anonymous.
- **Thrown bodies:** a rock released with `max(W_b, E_rail)` > 50 MJ (`ThrownTagJ`) is tagged `Thrown` with its emitter's owner. While tagged:
  - it never coasts. Each tick its swept capsule is tested against every entity's bounding sphere within reach (frozen and parked ships included) and against the instancer, like a fast-transit ship: ~2–4 µs per body;
  - a hit is a **ram**: the ship takes reduced-mass contact damage, a rock takes a `KineticHit` (source Ram) that may crater, crack or shatter it. Both are attributed to the thrower, and a shatter counts against the thrower's fracture rate limit;
  - it counts against the owner's 6 fracture events a minute at release (tractor-beams.md);
  - the tag ends after 120 s, or once its speed relative to its local lane falls under 5 m/s, or when a fracture consumes it. Then normal coasting resumes, with one addition (review fix): a coasting body still over 5 m/s relative to its lane is tested at 1 Hz along its closed-form Hill path against frozen and parked entities (a look-ahead over the next second, as the projectile rail look-ahead), so a rock whose tag timed out can't pass through a parked ship. ~1 µs per such body per second; it stops at 5 m/s or on re-rail.
  - **Caps:** ≤ 8 live `Thrown` bodies per player (`ThrownMax`): at the cap the player's new asteroid locks are refused until a tag ends; ≤ 128 server-wide, past which new throws are clamped at `W_b` = 50 MJ (a push, not a throw).
- **Rocks as weapons, the full list:** a throw (a ram of ≤ 500 MJ), a shatter or a cooked site next to a target (fragments and shrapnel now damage bystanders, see Abuse limits), a shield (a held rock interposed in a tractor cone or a line of fire takes the force or the rounds), and a rock pulled into a fast ship's path. Every one is bounded by `E_cap`/`EThrow`, attributed, rate-limited and inert inside protected zones.
- **Rock on rock:** a dynamic or thrown rock hitting a rail rock is a `KineticHit` on each, with **half** of the reduced-mass energy `½μv²` each (review fix: giving both the full amount spent 2 × 55 % of the impact energy and broke the no-energy-gain rule). A 1e6 kg rock at 32 m/s into an equal one has `½μv²` ≈ 256 MJ, so 128 MJ per rock: enough to shatter a ~64 m silicate rock regionally (`E_th` ≈ 95 MJ) and to split one up to ~11 m whole. Cascades advance one fracture event per bubble per tick, inside the fracture queue.
- **Per-tick cost of a held rock:** Hill force terms (~30 ns), the tractor's force and `AddForceAndTorque` (~0.8 µs), one awake Jolt body (~1 µs), and its share of the 10 Hz beam raster (~0.2 µs amortised). ≤ 16 beams per bubble bounds it at ~30 µs a bubble; thrown sweeps add ≤ 8 × 4 µs per player.

**Layers.**
- `AsteroidRail`: static and kinematic natural rails and re-railed bodies. It does not collide with itself, because the fit rule makes that unnecessary.
- Dynamic asteroids and boulders use `Moving`; loose chunks use `Debris`. Both collide with `AsteroidRail`, so a freed rock does hit its rail neighbours.

## Asteroid bodies

**Shape** (body-local metres, float, origin at the bounding centre):

```
d(p) = s_min * A_k(S^-1 p / (D/2)) * (D/2)              archetype k: ellipsoid + <= 8 smooth-union lobes - <= 8 smooth craters (unit space)
     + a_n * D * fbm3(p / lambda_n, DetailSeed)          a_n <= 0.03; octaves under 2 voxels skipped (planets' band limit)
then  max(d, -edit_i)           BrushOp ablation digs (server only), int64 1/1024 m body-local (C13)
then  max(d, -cut_j)            fracture cuts: the jagged ball of each spall event on this body
then  max(d, cell_sdf)          fragments only: intersect with their Voronoi cell (chain depth <= 4)
```

- **16 archetypes** in `data/asteroids/archetypes.json`. Axis scales S are 0.6–1.0 per axis, from the hash. Impostor meshes are baked from the same archetype SDFs at load (Surface Nets + meshopt, on jobs, ~1 MB in total).
- **Composition** `MaterialAt(p)`, evaluated in voxel fill, heat-site setup, mass integration and scans. First match wins:
  1. **EM lenses (server only).** Only asteroids drawn under the band's `EmFraction` have them, and they fill ~1 % of the body volume with `ExoticOre` at purity q (0.003–0.03, median 0.01; 0.02–0.3 near black holes). They come from the server-only `DepositSeed` (exotic-matter.md), never from the public hash.
     - Clients learn a lens only through an `AsteroidComposition` / `DepositRecord`: from a scan, from exposure by a cut or fracture, or by being in the scanner's group.
     - Client meshes paint EM only for known lenses.
  2. **Veins:** 0–3 noisy slabs, `|dot(p − c, n) + noise| < t`, with t 0.3–2 m, of a secondary band material. Public.
  3. **Core:** where `|S⁻¹p|/(D/2) < CoreFraction` (metal-rich classes only). Public.
  4. **Bulk material.** Public.
- **Voxels:**
  - The planet chunk path at asteroid scale. Base voxel = 0.25 m (D < 16 m), 0.5 m (D < 64 m) or 1 m.
  - The root LOD covers the bounding cube, and leaves are selected by camera distance, as on planets.
  - `ChunkJob` takes the density source as a generic struct (`IDensitySource`: planet, asteroid or fragment).
  - The mesher takes the cell scale as a parameter; ship-hulls asks for the same change. `ChunkConstants.VoxelSize` stops being a global const.
  - A 400 m body near the camera is ~300 leaves at mixed LOD; a 4 m body is one chunk.
- **Mass:** `MassMoments` (from Ships), by integrating occupancy × ρ(material) over a 32³ sampling of the bounds (~0.3–1 ms on a worker).
  - The server also keeps mass **per material** (including EM content) for yields.
  - It is server-authoritative and sent with realisation and fracture (X3). Static rail bodies need mass only for the scanner UI and for a later conversion to dynamic.
- **Collision:**
  - D < 8 m: one `ConvexHull` of ≤ 32 support points (32 fixed directions against the coarse occupancy surface).
  - D ≥ 8 m: the hull-exterior method from ship-hulls (per-cell connected components, convexity ≥ 0.85, else octants, then greedy boxes) at D/16 cells, ≤ 64 parts, as a `MutableCompound` so cuts touch only the changed sections. This is for ships and dynamic bodies, which never get closer than its error matters.
  - **Near characters** on static or kinematic bodies ≥ 32 m: the planets' collision ring, with LOD-0 chunk collision (`StaticMeshShape`) within 64 m of each character. Otherwise a 400 m rock's 25 m convex cells would leave magbooted players floating metres off the visible surface. This inherits review P1, the triangle-compound memory risk.
  - The decomposition code moves to a shared `src/Physics/ConvexDecomposition.cs`.
- **Integrity:**
  - Every natural asteroid, of any size, and every fragment ≥ 8 m is **Packed**. Player dig brushes are refused, and intakes refuse them: swallowing whole 2–4 m natural rocks would bypass the user's laser + wedge rule for 75 % of all asteroids.
  - Only server ops remove material from a packed body without fracturing it: laser ablation, kinetic craters and AM craters (revision 2). All three remove it as vapour or lost ejecta, with no yield (kinetic craters send 10 % to grit). Revision 3 adds hand digging inside damage zones (below) and chip samples (≤ 0.2 L, no geometry change).
  - Fragments under 8 m are **Loose** (see Fragments).
  - (Revision 3) Packed bodies born from a fracture (boulders ≥ 8 m) are **Fractured** throughout: hand tools may dig them anywhere; ship intakes still refuse them and the wedge cycle is still the fast way to break them.

**Damage zones and caves (revision 3).** The user's "cave mining inside large cracked-open asteroids" needs a rule for when packed rock becomes diggable. The decision here: **rock a fracture has shocked is diggable by hand; untouched packed rock never is.**
- **Damage zone:** every Spall, Split, Shatter, AmBlast and Detonation leaves a `DamageZone` sphere on the body it broke, centred on the event centre `c`, radius `R_dz = ζ_dz · R_f` (`ζ_dz` 1.5; 1.3 × `R_det` for detonations), clipped to the remaining body. Remainders keep the zones that intersect them. A Crack result (R_f < R_min) makes no zone: a crack is not an opening.
- **Diggable test (server):** a hand dig op on a Packed body is accepted only if its centre is inside one of the body's zones and its sphere lies within that zone + 0.5 m. Fractured bodies pass everywhere; Loose bodies are drilled as before.
- **Extending a cave:** a **splitter charge** (EVA tool, cold expansive splitter, no heat) set in a drilled hole ≥ 0.25 m deep makes a `Crack`-kind fracture event with a fixed `R_f` = 1.5 m at the hole, which adds a zone of 2.25 m. It needs a hole, and a hole needs a zone, so a cave can only grow from where a ship first broke the rock. Splitter events have no fragments and no bake (~5 µs), and their own limits (6 a minute per player, 16 a tick server-wide). At the 64-zone cap a splitter is refused (zones are never merged into a bounding sphere, which would grow the diggable volume for free); a ship fracture at the cap replaces the smallest splitter zone that lies wholly inside dug-out volume, else its zone is dropped.
- **Why hand caves don't bypass the wedge:** a hand drill moves ~0.016 m³/s (~43 kg/s of silicate), against a wedge cycle that breaks out 1e5–5e6 kg in 60–90 s. Caves are for precision: following a vein, taking out a metal core, and above all extracting **EM lenses cold** (heating past 700 K decays EM; a drill doesn't heat), at ~100 % lens recovery against the losses of a wedge or shatter.
- **Cave detail:** inside a damage zone and within `CaveDetailRadius` (24 m) of an EVA character, the body's octree refines up to two levels below its base voxel, to 0.25 m, so 0.25–0.35 m hand ops show and collide (planets' rule: an op under ~1 voxel does nothing). The character collision ring there is 16 m of 8 m chunks (~35 chunks) instead of 64 m at base. Cost: ~35 chunk builds when a character enters (0.5–0.8 ms each on workers, as planets), then 1–2 rebuilds per op; memory ~35 × 60–300 KB = 2–10 MB per caving character (review P1's fallback). Characters within 16 m of each other share chunks. Server cap `CaveDetailMax` 32 caving characters (≤ ~0.3 GB worst; review fix: 64 cavers spread over 64 rocks would otherwise be ~0.6 GB): past it, new cavers queue for detail and their hand ops are refused until a slot frees, with a HUD notice.
- **No collapse:** tide (≤ 2.2e-3 m/s²) and self-gravity (~1e-4 m/s²) are far below any rubble cohesion the game models, so tunnels stand. A later fracture whose region crosses a cave simply makes fragments there; characters inside take fragment impulses and, for a detonation, shrapnel.
- **Shrapnel inside rock:** shrapnel rays that start inside the remaining rock (a site in a cave or deep in a crack) are first traced through the parent's SDF and stop at rock (≤ 32 steps per ray, once), so a cave blast is lethal inside the cave and harmless outside it.
- **Heat near caves:** hand dig ops are refused within 6 m of an active heat site's grid (hot rock, and digging would rewrite the site's cells). A heat site may still open on a rock with people in its caves; the hazard beacon (Abuse limits) tells them.
- **Persistence:** hand ops are mining edits: persisted, inside the ≤ 4 ops/s per body budget, and a natural that takes more than 64 ops or 10 % of its volume converts to a spawned body as before. Zones are ≤ 64 per body, 16 B each.

## Mining

### The loop

1. **Scan** (composition scanner): find bulk, veins and EM lenses, and pick a site away from EM.
2. **Warm** (beam laser, power throttled 0–100 %): the first hit opens a heat site. Temperature climbs, volatiles vaporise, and stability S rises. Throttling holds a site at a steady temperature, so S holds too.
   - Optionally cut a **vent groove** first: ablation at T_melt opens surface, which raises venting so S stays low. Ice-rock needs this.
   - **Cutting a piece off by ablation alone** is allowed. It is the user's "cut open with a beam laser", and the slow, safe path: the ablated volume is lost, and a severed piece becomes a spawned body through the union-find in Fracture.
3. **Watch** (thermal imager): the site goes Cold → Warming → Window → Critical.
4. **Wedge** (launcher): aim into the hot zone and fire inside the window. The wedge seats and the rock splits. Outside the window it ricochets (too cold), or the rock detonates first (too hot).
5. **Collect:**
   - Loose chunks: tractor them to the intake, or drill them if they are too big.
   - Clouds: sweep them with the intake.
   - Packed fragments ≥ 8 m: repeat from step 2.
6. **EVA (revision 3, optional):** before step 2, crew can hand-scan and chip-sample to place the site precisely. After a fracture, crew can enter the damage zone (or any fractured boulder) and dig by hand along veins and round EM lenses, extending the cave with splitter charges. The ship keeps working the same rock from outside, under the hazard beacon rule.

### Material properties (data, game-tuned)

The thermal constants are gameplay values, not physical ones. Real rock conducts about a centimetre a minute, and a real megawatt laser would take hours. Radiation uses the real σ (knob `RadScale = 1`).

| Property | Ice-rock | Silicate | Carbonaceous | Metal | ExoticOre (lens host) |
| --- | --- | --- | --- | --- | --- |
| ρ (kg/m³) | 1500 | 2700 | 2000 | 7800 | 2800 (exotic-matter.md) |
| C_v (J/m³K) | 1600 | 2200 | 1800 | 3000 | 2200 |
| κ (m²/s) | 0.10 | 0.12 | 0.08 | 0.40 | 0.12 |
| α laser absorption / ε emissivity | 0.6 / 0.9 | 0.8 / 0.9 | 0.95 / 0.95 | 0.5 / 0.3 | 0.85 / 0.9 |
| T_soft / T_melt (K) | 250 / 900 | 500 / 1600 | 400 / 1300 | 700 / 1800 | 500 / 1600 |
| H0 hardness (Pa) | 2e8 | 4e8 | 2.5e8 | 9e8 | 4e8 |
| G0 fracture energy (J/m²) | 8e3 | 2.2e4 | 1.2e4 | 6e4 | 2.2e4 |
| σ_t burst strength (Pa) | 1.2e7 | 2.4e7 | 1.5e7 | 6e7 | 2.4e7 |
| f_v volatile mass fraction | 0.15 | 0.01 | 0.02 | 0 | 0.01 |
| T_vap (K) / L_v (J/kg) / e_gas (J/kg) | 300 / 5e3 / 1.5e5 | 600 / 5e3 / 2e5 | 450 / 5e3 / 2e5 | n/a | 600 / 5e3 / 2e5 |
| packing (venting resistance) | 0.9 | 0.95 | 0.85 | 1.0 | 0.95 |
| β_th thermal stress (J/m³K²) | 0.03 | 0.05 | 0.04 | 0.08 | 0.05 |
| e_abl ablation (J/m³) | 5e6 | 2e7 | 1.5e7 | 3e7 | 2e7 |
| EM (ExoticOre only) | | | | | EM mass fraction = lens purity q. T_em 700 K, λ_em 0.5 /s, e_em 5e6 J/kg. Proposed to exotic-matter.md, pending |

- **Outgassed bodies:** if `T_bg ≥ T_vap` for a material, its volatiles boiled off long ago, so f_v = 0. An ice-rock ring inside ~0.85 AU therefore holds no pressure.

Weakening, shared by hardness and toughness:

```
w(T) = 1                                                        T <= T_soft
     = w_min + (1 - w_min) * (T_melt - T) / (T_melt - T_soft)   T_soft < T < T_melt      (w_min = 0.05)
     = w_min                                                    T >= T_melt
H(T) = H0 * w(T) * (1 - 0.6 * crack)        G(T) = G0 * w(T) * (1 - 0.6 * crack)
```

### Heat model (server only, only for worked asteroids)

- **Site:** a 16³ grid in body-local axes, centred on the first beam hit and snapped to dx.
  - `dx = 1 m`, or `clamp(D/16, 0.375, 1)` when D < 16 m, so the grid covers the whole body.
  - Body-local axes ride the body. A spinning rock (revision 3) carries its grid with it, and the beam keeps a body-local aim point.
  - At most 2 sites per body. A second site opens if a hit lands more than 6 m from the first. With both sites taken, beam heat goes to the bulk node.
- **Bulk node:** a lumped node holds the rest of the body (`C_body = C_v · V_body`) and exchanges heat across the grid boundary.
- **Starting temperature:** `T_bg = 278 K · sqrt(1 AU / d_star)` for both cells and bulk.
- **Per cell:** T (float), solid fraction φ, material, exposed faces, volatile kg (half) and EM kg (half). 12 B × 4096 ≈ 48 KB per site.
- **Step:** 10 Hz while heating and 2 Hz while cooling, on a job. Server-only, so cross-machine float determinism is not needed.
  - **Substeps** for stability and resolution: `n = clamp(max(ceil(6·κ_max·Δt/dx²), ceil(ΔT_max_prev/25 K)), 1, 8)`.
  - Explicit conduction is unstable above κΔt/dx² = 1/6. Metal at dx = 0.375 m reaches 0.28.
  - At full power, a single small-dx cell can otherwise jump past a whole carbonaceous window (~30 K) in one 0.1 s step.

```
per solid cell c (phi_c > 0), dt = 0.1 s / n:
  C_c    = Cv(m_c) * phi_c * dx^3
  Q_cond = sum over 6 faces: K_cn * dx * (T_n - T_c) * dt      K_cn = harmonic mean of kappa*Cv, * min(phi_c, phi_n); outside the grid n = bulk
  Q_las  = alpha(m_c) * P_beam * W_c * dt                       W_c = share of the beam spot on c, spot radius r_b = r0 + theta * range
  Q_rad  = eps(m_c) * sigma * RadScale * A_c * (T_c^4 - T_bg^4) * dt    A_c = dx^2 * faces open to vacuum
  T_c   += (Q_cond + Q_las - Q_rad) / C_c
  if T_c > Tvap and mv_c > 0:  dm = min(mv_c, C_c*(T_c - Tvap)/Lv);  mv_c -= dm;  T_c -= dm*Lv/C_c;  Q_gas += dm*e_gas
  if T_c > Tem  and me_c > 0:  dm = me_c*(1 - exp(-lambda_em*dt));   me_c -= dm;  U_em += dm*e_em          (EM lost as yield)
  if T_c > Tmelt and A_c > 0:  V_abl += C_c*(T_c - Tmelt)/e_abl;  T_c = Tmelt
per site:
  Q_gas  *= exp(-k_vent*dt)       k_vent = k_v0*(1 - packing) + k_open*A_open/A_hot + k_crack*cracks     (k_v0 0.2, k_open 0.5, k_crack 0.1 /s)
  T_bulk += (boundary flux - eps*sigma*A_body*(T_bulk^4 - T_bg^4)*dt) / C_body
  V_hot   = sum phi*dx^3 over cells with T > Tvap  (min 1 m^3);    w_hot = w(mean T of those cells)
  U_th    = beta_th * sum over solid, unmolten cells of phi*dx^3*(T_c - T_bulk)^2
  S       = (Q_gas + U_em) / (sigma_t * w_hot * V_hot)             S >= 1 -> detonation
  ablation: once V_abl >= V_op = 4/3 pi r_op^3, with r_op = max(0.35 m, BaseVoxel) (planets' visibility rule: an op under ~1 voxel does nothing),
            emit a Dig BrushOp (r_op) at the hottest open face (server edit, <= 4/s per site); the groove raises A_open
```

- **Laser hit point:** found by SDF sphere-trace on the server at the heat-step rate (10 Hz), not every tick. The trace uses the body's coarse occupancy first, and the full SDF with edits only for the last steps.

**Designer's closed form (a tuning aid, not the truth).**
- With retained share `r` (1 with no venting), `S ≈ r · f_v · ρ · e_gas / (σ_t · w(T))`. With uniform heating, S does not depend on the hot volume; it is a material and temperature property.
- The sim uses the hot zone's *mean* T, which is lower than the peak, so the sim's S runs below this form.
- The wedge seats when `w(T) ≤ E_k / (H0 · A_w · δ_min)`, which gives `T_seat`. `T_det` solves `S = 1`.
- Standard wedge: 20 kg at 250 m/s, E_k = 625 kJ, A_w = 0.01 m², δ_min = 0.4 m.

| Material | T_seat | S at T_seat (no venting) | Window → Critical (S = 0.9) | T_det | Character |
| --- | --- | --- | --- | --- | --- |
| Silicate | 1206 K | 0.58 | 1368 K | ~1400 K | Workable. ~1 min to the window with a 3 MW laser (estimate; radiation from the hot face is a large share of the input) |
| Carbonaceous | 755 K | 0.85 | 786 K | ~840 K | Tight window: throttle and fire fast |
| Ice-rock | 400 K | 3.6 | n/a | below T_seat (S ≈ 3 at 300 K) | Detonates unless vented: cut a groove first, or heat slowly at low power. Inert if outgassed |
| Metal | 1656 K (heavy 60 kg wedge: 1355 K) | 0 | never | never | Safe and slow. Needs high power or heavy wedges |
| EM lens heated past 700 K (q = 0.01) | | U_em gives S ≈ 8+ per heated m³ within seconds | | | Never heat EM. Put sites ≥ 3 m from known lenses |

### Site state machine

`T_site` is the hottest exposed cell near the site centre. `T_aim` is the cell under the current wedge aim; it drives the forecast and the seat test. The thresholds are knobs in `mining.json` (`SCrit` 0.9, `SCritExit` 0.85).

| State | Enter | Leave | Server work |
| --- | --- | --- | --- |
| Cold | The first beam hit opens the site | `T_site ≥ T_soft` → Warming | Heat step, 10 Hz |
| Warming | `T_soft ≤ T_site < T_seat` | `≥ T_seat` → Window, or `S ≥ SCrit` → Critical | 10 Hz |
| Window | `T_site ≥ T_seat` and `S < SCrit` | `S ≥ SCrit` → Critical; the wedge seats → Fracture | 10 Hz, forecast 5 Hz |
| Critical | `S ≥ SCrit` (the wedge still seats if `T_aim ≥ T_seat`) | `S ≥ 1` → Detonation; `S < SCritExit` → Window or Warming | 10 Hz |
| Cooling | No heat input (beam or weapon) for 2 s, from any state above | Beam again → back; `max(T − T_bulk) < 20 K` and `S < 0.02` → site freed | 2 Hz. S can still rise while hot cells pass T_vap |
| Fracture / Detonation | Event | Site freed, body changed | Event evaluation |

Weapons add transitions into and out of this machine (kinetic shatter, weapon heat, AM breaches); see "Weapons in the mining state machine".

The body has three separate axes of state:
- **Motion:** `Rails | Static | Kinematic | Dynamic | Coasting`.
- **Integrity:** `Packed | Loose`.
- **Lifetime:** `Natural` (possibly cratered), `Spawned` (a fragment or re-railed body), `Removed`.

### Wedge

- **Flight:** a server-swept projectile. Each tick it sphere-traces the SDFs of candidate bodies along its path, for ≤ 2 s of flight. No Jolt ray cast is needed (native `CastRay` is missing). Clients draw it from the fired event.
- **Energy:** `E_k = ½ · m_w · |v_w − v_surface|²`.
- **Seat test:** the wedge seats if penetration `δ = E_k / (H(T_aim) · A_w) ≥ δ_min`. Loose bodies use `0.2 · H0`, so they seat cold.
  - On failure it **ricochets**: the wedge is consumed and only an impulse is applied.
- **Fracture energy:**

```
Q_rel   = Q_gas + U_em of the site
E_crack = eta_w * E_k + U_th + gamma * Q_rel                         eta_w from the wedge item (0.6), gamma = 0.1
E_frac(R, N) = G_eff * R^2 * (4.91 * N^(1/3) + 6.28)                 Voronoi faces filling a hemisphere + its shell
R_f     = sqrt(E_crack / (G_eff * (4.91 * N^(1/3) + 6.28)))          N = N0 = 6; G_eff = volume mean of G(T) over the ball (heat grid inside the site, w(T_bulk) outside); 3 fixed iterations from R = 2 m
R_f < R_min = max(0.75 m, 3 * BaseVoxel)   -> Crack: CrackRecord(centre, R_f, blade normal); G and H x0.4 there for later wedges; venting +k_crack
R_f >= R_reach (farthest body point)       -> Split: R_f = R_reach, N = clamp(N0 + 2*floor(log2(E_crack / E_frac(R_reach, N0))), 2, 16)
otherwise                                  -> Spall: crater + fragments, parent keeps the rest
E_kin   = min(E_cap, chi * min(S, 1) * Q_rel + max(0, E_crack - E_frac(R_f, N)))   chi = 0.5; the excess over E_cap becomes grit and heat
```

- **Silicate at S = 0.58** (V_hot ≈ 30 m³): E_crack ≈ 17 MJ gives R_f ≈ 7–9 m and ~4–5e6 kg in the region. E_kin ≈ 47 MJ gives v̄ ≈ 4.5 m/s.
  - Vented to S = 0.05: R_f ≈ 3.6 m and v̄ ≈ 1.6 m/s.
  - So more pressure means a bigger, livelier split, and players choose the risk.

### Fracture (server-evaluated, shared by wedge, kinetic shatter, AM blast and detonation)

- **Sites:**
  - Two primary sites at `c ± 0.5·R_f·n_blade`, so the first face is the blade plane. The blade roll is the shooter's choice and sets the plane through the wedge axis.
  - N − 2 further sites at `c + R_f · u_k · (0.5 + 0.5|u_k|)`, clustered near the impact so pieces are finer there, shifted by `0.3·R_f·biasDir`. `biasDir` is the server's heat-weighted offset of the hot zone.
  - `u_k` is uniform in the ball, from `Hash32(seed, k)`.
- **Region:** the ball `|p − c| − R_f + 0.15·R_f·noise(p) ≤ 0`. Voronoi faces get `0.1·cellSize·noise(p)` for jagged surfaces. Float maths, no trig, fixed order.
- **Fragment i** = parent ∩ region ∩ cell_i, in the parent's geometry frame. There is no transform: the body pose carries an offset, and Jolt gets an `OffsetCenterOfMassShape`.
- **Parent** = parent − region, stored as a `cut_j` record.
- **Remainders:** a coarse-occupancy union-find, as in ship-hulls' split check, finds disconnected pieces of the parent.
  - The largest 4 pieces become spawned bodies via `KeepSeedCell`. Smaller ones become chunks or cloud mass by size.
  - A natural parent that splits or loses more than 10 % of its volume converts to a spawned body (see the detail ladder).
- **The server decides everything discrete.** Which cells are non-empty, which remainders exist, their ids, masses, inertia and velocities all come from server floats and go into the event (X3). Clients bake the same geometry for visuals. A thin bridge that is connected on one machine and broken on another cannot fork the body set.
- **Velocities** (server): `v_i = v̄ · (m̄/m_i)^(1/6) · dir_i + v_parent(x_i)`.
  - Constraints: `Σ ½ m_i |v_i − v_parent|² = E_kin`, `|v_i − v_parent| ≥ v_sep = 0.2 m/s`, `dir_i = normalize(x_i − c + 0.5·R_f·n_out)`.
  - Spin: `|ω_i| ≤ 0.3·|v_i|/D_i`, from the hash.
  - Momentum is conserved: a dynamic parent takes `−Σ p_i`, and a static or kinematic parent absorbs it. For every kind, the dynamic parent's recoil energy counts inside `E_kin` (the energy is shared in the centre-of-mass frame), and its speed change is capped like a piece's (revision 2 review).
- **Grit:** `m_grit = 0.05 m · ρ · A_crack` becomes a rubble cloud at c with the parent's velocity.
- **Mass check:** fragments + remainders + grit + ablated + lost (shatter fines and crater vapour, revision 2) = the parent before the event, per material. This is a server assert and a unit test.

### Detonation (the microwave egg)

- **Trigger:** S ≥ 1 in a heat step. The wedge does not trigger it, although a wedge into a Critical site releases a lot.
- **Region:** sized by the hot zone, not the energy. `R_det = 1.5 · (3·V_hot/(2π))^(1/3)`, centred on the hot-cell centroid, with `N = 16` and no blade (isotropic sites).
- **Energy:**
  - `E_kin = min(E_cap, χ_det · Q_rel + U_th)`, with χ_det = 0.9.
  - A share `ψ = 0.2` goes to **shrapnel**: K = 32 rays (knob), 1–5 kg each from the hash, at `v = sqrt(2ψE_kin / Σm_s)`, capped at `ShrapnelVMax` (1.5 km/s).
  - The rest goes to the fragments, using the velocity formula above.
- **Shrapnel sweep:** shrapnel rays are swept by the server for 1.5 s, in double in the asteroid's frame, against **every** entity within `ShrapnelVMax · 1.5 s`, not only the bubble's. A hull hit becomes a ship-hulls damage brush with `r = (½ m_s v_s² / e_hull)^(1/3)`.
- **No blast wave in vacuum.** A gas momentum pulse at 50 m is mm/s on a ship. The danger is what flies.
- **Silicate example:** V_hot = 30 m³ gives R_det ≈ 3.6 m and ~2.7e5 kg of fragments. Q_rel ≈ 280 MJ gives v̄ ≈ 39 m/s (small pieces ~80 m/s) and 32 shards at ~1 km/s. The hit chance for a 10 m ship is about 5 % at 100 m and about 50 % at 30 m. A 1e4 kg fragment at 60 m/s is an 18 MJ ram.
- **EM:** U_em is not ventable. A heated lens is a grenade inside the egg, and the decayed EM is lost.

### Fragments by size (`D_eq = (6V/π)^(1/3)`)

| Tier | Size | Representation | How it is collected |
| --- | --- | --- | --- |
| Boulder | ≥ 8 m (`D_dig`) | Packed voxel body, dynamic, born with the event's velocities. It carries the parent's cracks inside its bounds, and is Fractured throughout (revision 3) | Repeat the laser + wedge cycle, or dig it by hand (slow, precise). Fragment chain depth ≤ 4; deeper fragments bake to `ChunkSnapshot` |
| Chunk | 0.5–8 m | Loose rigid convex body (hull ≤ 32 points + a mesh snapshot at D/16 voxels), layer Debris, mass per material from the server | Tractor it into an intake whose mouth ≥ D. Otherwise drill it down (grind: mass to cargo, uniform shrink, shape rebuilt at 4 Hz, or `ScaledShape` if exported), or wedge it cold |
| Grit | < 0.5 m, plus crack grit | One `RubbleCloud` entity: mass per material, centre, velocity, radius `r0 + v_cloud·t`, propagated analytically | Intake sweep: `dm/dt = ρ_cloud · A_intake · |v_rel|` (×2 in tractor vacuum mode) |

**Caps:**
- 128 chunks per bubble. Overflow converts the smallest and oldest chunks to cloud mass, preserving mass.
- 16 clouds per bubble. A cloud despawns below 0.01 kg/m³, or with no player within 3 km for 60 s. Overflow merges the new cloud into the nearest existing one (mass and momentum preserved, radius = the larger), so gunfire cannot grow the cloud count.
- Chunks despawn with no player within 3 km for 60 s.
- **Per-bubble body budget (revision 2 review):** an event may add fragments only while the bubble stays under its 256 asteroid-related bodies. If it would go over, its smallest pieces join its cloud (mass preserved) until it fits; if its boulders alone do not fit, the bubble is saturated and the hit degrades to crater + crack, like the over-limit rule in Abuse limits.

### Abuse limits (detonation weapons, shrapnel, fragment spam)

Detonations, rams, thrown rocks and the tools themselves can be turned on other players. Revision 3 settles the old open question 2: **outside protected zones, shrapnel and fragments from anyone's site damage bystanders** (see Bystanders below). These limits apply on top:
- **Protected zones** (stations, gates, and anything a later zone system declares; radius `ProtectRadius`, 5 km, which is more than shrapnel reach):
  - the server caps heat sites at Warming, by throttling the beam;
  - wedges ricochet;
  - tractor locks on asteroid bodies are refused.
- **Hot bodies can't be moved as bombs:**
  - A tractor lock is refused on a body whose site has S > 0.3.
  - A body under a lock stops accepting beam heat.
- **Energy caps:** `E_cap` (500 MJ) per fracture or detonation event, plus `ShrapnelVMax` and K. One event therefore has a bounded reach and bounded per-shard energy (≤ 5.6 MJ).
- **Attribution:** every event records the site owner, or the shooter for wedges. Damage from its fragments and shrapnel is tagged with that owner for the future faction and crime system.
- **Rate limits:**
  - ≤ 6 fracture events a minute per player, besides the wedge reload.
  - A server-wide budget of 4 events per tick with a fair queue: one per bubble per tick, round-robin.
  - ≤ 16 fragments plus 4 remainders per event.
- **Persistence caps:** ≤ 4096 `SpawnedBody` records per region file and a per-region byte budget (revision 3, see Deltas). Past either, the oldest re-railed boulders under 16 m become removed, and their mass is lost.
- **The beam laser and wedge launcher are also weapons against hulls.** Which rules govern that is a combat question, outside this doc.

**Bystanders (revision 3).** User decision: shrapnel and fragments from someone else's site damage bystanders.
- **Outside protected zones,** shrapnel, fragments, chunks, rubble rams and `Thrown` rocks damage any ship and any EVA character, whoever owns the site. Damage is the existing rule (shrapnel → damage brush `r = (½ m_s v_s² / e_hull)^(1/3)`; bodies → reduced-mass contact damage), tagged with the event's owner (or the thrower) for the future faction and crime system. Characters take shrapnel through the character health rule; the shrapnel sweep already tests every entity within reach.
- **Inside protected zones,** nothing changes: asteroid debris does no hull damage there, and chunks and clouds that enter become lost mass.
- **Hazard beacon:** a site in Critical, a queued fracture on a body, an armed contact AM trap on rock, and every `Thrown` rock broadcast a public `HazardBeacon` (position, kind, predicted reach: `ShrapnelVMax · 1.5 s` = 2.25 km for a site, the predicted path for a throw) at 2 Hz to every client within 5 km, 16 B each. The HUD draws it whatever imager the bystander has, so a griefer can't cook a rock next to a stranger unseen, and attribution names them if they try.
- **Rate and energy limits are unchanged**, so a griefer's reach is one `E_cap` event at a time, ≤ 6 a minute, ≤ 1.5 km/s per shard and ≤ 80 m/s per fragment, all attributed.
- **Cave miners get no special protection** from other parties' heat and wedges on the same rock (open question 3): the beacon and attribution are the protection.

**Thrown rocks and EVA (revision 3).**
- `Thrown` rocks follow Manipulation: ≤ 8 live per player, ≤ 128 per server, swept like fast ships, attributed; a `Thrown` body that enters a protected zone keeps its tag and its contact damage to other parties' grids there is zeroed and logged against the owner (tractor-beams.md's proposed line, adopted).
- **Sampling is not mining:** chip and core samples are `sample` items in no material family: no refinery, recipe, socket, blender or market accepts them as material (an assay reads them into a record). A body keeps ≤ 64 sample marks (cosmetic, oldest dropped); on an otherwise untouched natural they count as cosmetic craters, so they persist only inside the region's cratered-naturals cap and are ephemeral past it (review fix: chip-sampling thousands of rail rocks could otherwise grow storage without limit).
- **Hand digging** is accepted only inside damage zones (or on Fractured bodies), at ≤ 1 op/s per player inside the ≤ 4 ops/s per body budget, and never within 6 m of an active heat site. Splitter charges: 6 a minute per player, 16 a tick server-wide, and refused in protected zones like wedges.
- **Replenishment events** are never placed within 20 km of an anchor, a realised body or an active site, so nothing changes under anyone's physics; the visible swap happens under the event's flash and dust shell (Deltas). Players' work in a footprint (spawned bodies, worked naturals, caves) survives the takeover.

**Weapons (revision 2).** The limits above were written for mining tools. They now cover every source that heats, cracks or blasts rock:
- **Protected zones:** asteroids inside `ProtectRadius` are inert to weapons and rams. Weapon heat goes nowhere (no sites, no bulk), kinetic hits and rams leave no crater, crack or fracture, and contact AM breaches make no crater or fracture (their hull damage and dose are exotic-matter's, unchanged). AG pulses already skip rails everywhere.
- **Absorbed power cap:** `P_siteMax` (10 MW absorbed per site, all sources summed, the excess lost to plume shielding). This bounds how fast anyone can drive a site to detonation. It does not bound substeps (10 MW into one 1 m³ silicate cell is ~450 K per step, past the 8-substep cap); cells clamp at `T_melt` and ablate, so the cap only coarsens the forecast. The mining beam (~1.2 MW absorbed) is far below it.
- **Weapon sites:** opened only once weapon heat on one body reaches `E_open` (1 MJ absorbed) within a 1 s window; ≤ 4 weapon-opened sites per player, and ≤ 128 server-wide, so weapons can never take more than half of the 256 sites from miners. They count in the 2 per body cap. Past a cap, weapon heat goes to the bulk node.
- **Bulk-node weapon heat is capped:** weapon heat (and the AM standoff flash) can raise `T_bulk` only to `max(T_bg, min(T_soft, T_vap))`; the excess is lost. The bulk node has no volatiles and no S, so without this cap sub-`E_open` fire could warm a small body past `T_vap` silently (a 4 m rock's bulk node holds only ~7e4 J/K), or weaken it to cut its shatter threshold, both outside the egg rule.
- **One energy cap:** `E_cap` bounds `E_crack` and `E_kin` of every shatter and AmBlast, as for wedges and detonations. `E_kin` is measured in the centre-of-mass frame of the parent and its pieces, so a dynamic parent's recoil comes out of the same budget. Every fragment and every recoiling remainder is capped at `ShatterVMax` (80 m/s) relative to the parent, and shrapnel at `ShrapnelVMax`.
- **One rate limit:** Shatter and AmBlast events count against the same 6 fracture events per player per minute and the server's 4 per tick (fair-queued, 1 per bubble). A player over the limit gets crater + crack instead of a shatter (an AmBlast over the limit keeps its crater only). A queued event resolves on a later tick with its original inputs; the hit itself is not refused.
- **One pending fracture per body:** while a body has a queued event, later kinetic hits on it take the crater + crack path, applied after the queued event in hit order. The queue holds ≤ 64 events server-wide; a hit that finds it full degrades to crater + crack. So sustained fire on one rock, or on many, cannot build an unbounded backlog of fracture work.
- **Worker budget:** fracture bakes and mass integration have a server job budget (`FractureJobMsPerTick`, 4 ms of worker time a tick). An event that does not fit waits in the queue; the budget, not the 4-per-tick count, is what holds sustained load (4 events of 20 bakes at up to 3 ms would be 240 ms of work).
- **Asteroids as kinetic weapons:**
  - No energy gain: a cold shatter gives its pieces at most `ξ_k · E_p` (30 % of the round's energy), spread over 8–16 pieces and a cone. Only stored site energy adds to it, and heating is refused in protected zones, capped by `P_siteMax` and attributed.
  - Spall pieces leave through the entry face (back toward the shooter's side); only a whole-body split throws pieces downrange of the shot.
  - **Debris in protected zones:** an asteroid body (boulder, chunk, freed rock, cloud) does no hull damage inside `ProtectRadius`; its contacts there are impulse only, and chunks and clouds that enter it become lost mass. Fragments shattered just outside a station therefore cannot be aimed into it. Outside protected zones, debris damages bystanders and is tagged with the event's owner (revision 3, see Bystanders).
- **Edit rate:** crater ops share the ≤ 4 ops/s per body budget with ablation. Crack records stay capped at 8 per body, so cratering one rock forever does not grow its record.
- **Attribution:** shatter → the shooter (rams → the pilot); AmBlast → the trap owner, and the shooter whose hit breached it if there was one; weapon-driven detonations → the top heat contributor of the last 30 s, with all 4 contributors listed.
- **Stray fire is cheap:** projectile impulses never free natural rocks, sub-`E_open` heat never opens a site, low-energy rounds test rails by a 10 Hz look-ahead only (Performance), and rounds that hit Loose chunks turn them into cloud mass instead of making new bodies.
- **Hot bodies as kicked bombs:** see "Anti-graviton charges" (gradient venting; open question 4).

## Weapons against asteroids

Revision 2. Two user decisions (fixed): a big enough kinetic hit shatters an asteroid whatever its heat state, and energy weapons heat asteroids through the same heat model, but far less efficiently than the mining beam. Exotic-matter weapons (AM breaches, anti-graviton charges) are mapped onto the same server-owned fracture event model. Weapon stats themselves (damage to hulls, rates of fire, ammo) belong to a future combat doc. This section defines only what each weapon does to rock, through per-weapon `asteroid` fields.

### Kinetic hits: the shatter threshold

Any server-resolved kinetic hit on an asteroid body is a `KineticHit` (projectile, or a ram from the fast-transit sweep or a contact). Wedges are tools, not weapons: they keep the seat path only (`ShatterCoupling = 0`), so a cold wedge still ricochets.

Projectile energy is split three ways, by per-weapon data:

```
E_p      = 1/2 * m_p * |v_p - v_surface|^2                        rams: 1/2 * mu * v_rel^2 (reduced mass, as before)
E_crack  = min(E_cap, eta_k * E_p)                                eta_k  = 0.25  fracture surface (crack energy)
E_motion = xi_k * E_p                                             xi_k   = 0.30  fragment kinetic energy
                                                                  rest   = 0.45  crater vapour, plume, heat, escaping ejecta: lost
F(N)     = 4.91 * N^(1/3) + 6.28                                  the Fracture section's face factor
G_eff    = volume mean of G(T) over the ball of radius R_sh (as for wedges; G(T) already carries the (1 - 0.6 * crack) term, applied once)
R_sh     = max(R_min, zeta_sh * D / 2)                            zeta_sh = 0.25: the smallest region a shatter may make, scaled by body size
E_th     = G_eff * F(N_sh0) * R_sh^2 / eta_k                      N_sh0 = 10: the shatter threshold in projectile energy (cold site: U_th = Q_rel = 0)
E_crack  = min(E_cap, eta_k * E_p + U_th + gamma * Q_rel)         the site terms join as for wedges (0 with no site)
shatter  <=>  E_crack >= G_eff * F(N_sh0) * R_sh^2                (for a cold rock, E_p >= E_th unless E_cap binds)
```

- **Size scaling:** `E_th ∝ G · D²`. The threshold grows with size and toughness, while the threshold per kilogram of the body falls as 1/D, as in real strength-regime disruption. Below `R_min` it is flat, so even 2 m rocks need a real weapon.
- **What a shatter breaks.** At the threshold, a shatter breaks out the `R_sh` region round the hit (radius D/8: ~1 % of the body's volume) into 8–16 pieces, and the parent keeps the rest, like a spall. More energy grows the region. It becomes a **whole-body shatter** (the Split path) once `R_f ≥ R_reach`, the distance from the hit to the far side (≈ 0.8·D for the archetypes). Both outcomes are a Shatter; the forecast shows which.
- **Heat lowers it, but is not needed:** `G_eff` uses the heat grid where a site exists and `w(T_bulk)` elsewhere, so a warmed rock shatters more easily (down to `w_min` = 5 % of the cold threshold at `T_melt`). Cold rock works at the full threshold, which is the user's "regardless of the heat window". A laser-warmed site shot with a gun instead of a wedge therefore shatters easily, but as a shatter (no blade, fines lost), so the wedge still pays.
- **Cracks lower it, with a floor:** crack records do not stack. A point inside any record has `crack = 1` (G × 0.4); outside all of them, 0. A fully cracked ball has `G_eff = 0.4·G`, so **hits below 0.4·E_th never shatter a cold rock**, however many there are. A hit of ~0.67·E_th or more shatters on the second hit on the same spot, because the first one's crack (radius ≈ `sqrt(E_p/E_th)·R_sh`) weakens enough of the ball; hits between 0.4 and 0.67·E_th need several records tiling the ball (≤ 8 per body).
- **E_cap binds the biggest rocks.** `E_crack ≤ 500 MJ` gives a largest shatter region `R_max = sqrt(E_cap / (G0 · F(N_sh0)))`. Bodies with `R_sh > R_max` can never be shattered by one hit, even locally; they only crater and crack: about D ≤ 294 m for silicate, 178 m for metal, 398 m for carbonaceous and 487 m for ice-rock (`D ≤ 8·R_max`). Those rocks are mined, or cratered and cracked over many hits. A whole-body shatter is capped far lower: `0.8·D ≤ R_max` gives D ≤ ~46 m silicate, ~28 m metal, ~62 m carbonaceous and ~76 m ice-rock.
- **Rail asteroids outside every bubble** (a long shot at an unrealised rock): the server realises the parent for the event only, and the pieces are born **Coasting** (closed-form Hill motion, 0 per tick) if no player is within 500 m, with their directions clamped outward from the parent, since coasting has no collision. They enter Jolt by the normal 400 m rule.
- **Loose chunks** (0.5–8 m) are not fractured by weapons. A hit at or above the chunk's threshold (with `0.2·G0`, as loose wedges use `0.2·H0`) turns the whole chunk into cloud mass, `FinesLost` (half) of it lost; below it, an impulse only. This removes bodies instead of adding them, needs no fracture evaluation (~1 µs) and is not a fracture event. It is still worse than tractoring the chunk whole (100 %).

**Threshold `E_th` by size (cold, uncracked; E_cap-limited cells marked):**

| D | R_sh | Ice-rock (G0 8e3) | Carbonaceous (1.2e4) | Silicate (2.2e4) | Metal (6e4) |
| --- | --- | --- | --- | --- | --- |
| 4 m | 0.75 m | 0.30 MJ | 0.45 MJ | 0.83 MJ | 2.3 MJ |
| 16 m | 2 m | 2.2 MJ | 3.2 MJ | 5.9 MJ | 16 MJ |
| 64 m | 8 m | 35 MJ | 52 MJ | 95 MJ | 259 MJ |
| 256 m | 32 m | 0.55 GJ | 0.83 GJ | 1.5 GJ | never (R_sh > R_max = 22 m) |
| 400 m | 50 m | 1.35 GJ (R_max = 61 m) | never (R_max = 49.7 m) | never (R_max = 36.7 m) | never |

**Whole-body shatter `E_split` (cold, uncracked, `R_reach ≈ 0.8·D`, N = 10: `4 · G0 · F(10) · (0.8 D)²`):**

| D | Ice-rock | Carbonaceous | Silicate | Metal |
| --- | --- | --- | --- | --- |
| 4 m | 5.5 MJ | 8.3 MJ | 15 MJ | 41 MJ |
| 8 m | 22 MJ | 33 MJ | 61 MJ | 166 MJ |
| 16 m | 88 MJ | 133 MJ | 243 MJ | 663 MJ |
| 32 m | 354 MJ | 530 MJ | 972 MJ | never (past ~28 m) |
| Largest by one hit (E_cap) | ~76 m | ~62 m | ~46 m | ~28 m |

So a railgun slug (160 MJ) breaks a whole silicate rock up to ~13 m and a mass-driver slug (40 MJ) up to ~6.5 m; bigger rocks lose a shattered region round the hit.

**Reference projectile classes** (placeholders until a combat doc owns them; only the energy matters here):

| Class | m_p, v | E_p | Shatters, silicate (cold) | Shatters, metal (cold) |
| --- | --- | --- | --- | --- |
| Autocannon round | 0.4 kg, 1.2 km/s | 0.29 MJ | nothing, even cracked (floor 0.33 MJ). Cracked 2–6 m ice-rock and carbonaceous (floors 0.12 and 0.18 MJ): yes, after a few cracking hits | nothing |
| Wedge.Heavy (tool, for comparison) | 60 kg, 250 m/s | 1.9 MJ | never: wedges take the seat path only | never |
| Mass driver slug | 20 kg, 2 km/s | 40 MJ | D ≤ 42 m | D ≤ 25 m |
| Railgun slug | 5 kg, 8 km/s | 160 MJ | D ≤ 83 m | D ≤ 50 m |
| Kinetic kill vehicle (capital) | 500 kg, 3 km/s | 2.25 GJ | D ≤ 294 m (E_cap binds) | D ≤ 178 m (E_cap binds) |
| Ram, 300 t ship at 100 m/s into a big rock | μ ≈ 3e5 kg | 1.5 GJ | D ≤ 254 m | D ≤ 154 m |

**Below the threshold** a kinetic hit makes a crater, and maybe a crack:
- **Crater:** `V_c = eta_c · E_p / H(T)` with `eta_c = 0.5`, accumulated per body like `V_abl`, and emitted as a server cut (`BrushOp`, radius `r_op`) once it reaches `V_op` (≤ 4 ops/s per body, shared with ablation). An autocannon round digs ~0.36 L of silicate, so roughly 500 rounds make one 0.35 m op on a small rock (~1,450 for a 0.5 m op on a 16–64 m rock, ~11,600 for a 1 m op on a bigger one).
- **Crater mass is mostly lost:** 90 % vapour and escaping ejecta, and 10 % goes to the body's grit cloud (the nearest cloud of that body, or a new one under the cloud cap's merge rule). This is deliberate: sandblasting with small arms must not be a mining tool.
- **Crack:** if `R_f ≥ 0.5 · R_min` (solved with N_sh0), a `CrackRecord` is laid at the hit, exactly as the wedge Crack result.
- **No freeing:** projectile impulses never turn a static or kinematic natural asteroid dynamic. Only contacts, tractor locks and fractures do. Stray fire therefore cannot wake a field.

### Shatter fracture versus wedge fracture

A shatter is `FractureKind.Shatter`. It is evaluated by the shared Fracture code (same Voronoi-on-SDF, same server-sent records, same caps); only its inputs differ:

| | Wedge (laser + wedge) | Shatter (kinetic weapon or ram) |
| --- | --- | --- |
| Needs the window | Yes: seat test on `H(T_aim)` | No: energy threshold on `G_eff` |
| Crack energy | `η_w·E_k + U_th + γ·Q_rel` (η_w 0.6) | `min(E_cap, η_k·E_p) + U_th + γ·Q_rel` (η_k 0.25) |
| Sites | Two primary sites on the blade plane (player-chosen roll), then N − 2 clustered at `0.5 + 0.5|u|` | No blade. N sites clustered harder toward the entry point, `c + R_f · u_k · (0.2 + 0.8|u_k|²)`, shifted `0.3·R_f` along the shot axis instead of the heat bias |
| N | `N0 = 6`, up to 16 on a split | `N = clamp(N_sh0 + 2·floor(log2(E_crack / E_frac(R_sh, N_sh0))), 8, 16)` |
| Size distribution | One clean face, 2–3 large pieces; largest ~35–50 % of the region. Mostly boulders and big chunks | No clean face; largest ≤ ~25 %. Mostly chunks (0.5–8 m), plus fines |
| Fines | Grit `0.05 m · ρ · A_crack` (~5–10 % of the region), as a slow cloud at the parent's velocity. Recoverable | `f_fines = clamp(0.15 + 0.05·log2(E_p / E_th), 0.15, 0.40)` of the region. Half of it (`FinesLost` 0.5) is lost at once (vapour, escaping ejecta); the rest is a cloud expanding at `max(2 m/s, 0.5·v̄)`. The event's crack grit joins this cloud: one cloud per event |
| Fragment energy | `χ·min(S,1)·Q_rel + max(0, E_crack − E_frac)` | `min(E_cap, ξ_k·E_p + χ·min(S,1)·Q_rel + max(0, E_crack − E_frac))` |
| Velocities | `v̄ · (m̄/m_i)^(1/6)`, dir `x_i − c + 0.5·R_f·n_out` | `v̄ · (m̄/m_i)^(1/3)` (small pieces fly much faster), dir as wedge plus a hashed 60° cone jitter. `v_sep = 1 m/s`; every piece capped at `ShatterVMax` (80 m/s), the excess lost. `E_kin` is shared in the centre-of-mass frame with a dynamic parent's recoil, which is capped the same way |
| Fragments born with | The parent's cracks in their bounds | The parent's cracks, plus a crack record at their centre: shattered boulders are easier to break again, and easier to break badly |

**Worked examples (silicate, cold, ρ 2700):**
(v̄ below is over the pieces, after the fines are taken out.)
- **Mass driver into a 16 m rock (40 MJ, E_th 5.9 MJ):** E_crack 10 MJ, N = 14, R_f ≈ 5.0 m: a spall of ~7e5 kg (a regional shatter; the whole rock would need ~243 MJ). f_fines = 0.29: ~1e5 kg lost outright and ~1e5 kg in a cloud. E_kin = 12 MJ over the ~5e5 kg of pieces gives v̄ ≈ 7 m/s, ~21 m/s for pieces 1/30 of the mean mass, and a cloud spreading at ~3.5 m/s.
- **Railgun into a 64 m rock (160 MJ, E_th 95 MJ):** E_crack 40 MJ, N = 10, R_f ≈ 10.4 m: ~6.4e6 kg. f_fines = 0.19. E_kin = 48 MJ over ~5.1e6 kg of pieces gives v̄ ≈ 4.3 m/s.
- **Railgun into an 8 m rock (160 MJ, E_split 61 MJ, E_th 1.5 MJ):** a whole-body shatter of ~3.7e5 kg (with the archetype's axis scales) into N = 16 pieces, f_fines = 0.15 + 0.05·log2(160/1.5) ≈ 0.49, capped at 0.40. E_kin ≈ 70 MJ (48 MJ plus the ~23 MJ of crack energy left over after the split) over ~2.2e5 kg of pieces gives v̄ ≈ 25 m/s, and most small pieces hit the 80 m/s cap. Overkill on small rocks is the most wasteful case, as intended.
- **Kill vehicle into a 64 m rock (2.25 GJ; ~1.9e8 kg with the archetype's axis scales, above `KinematicMassLimit`, so static):** E_crack hits E_cap (500 MJ), N = 16, R_f ≈ 35 m. R_reach ≈ 51 m, so even this is not a whole-body shatter (that needs ~1 GJ of crack energy, twice E_cap): the region takes about half the rock (~1e8 kg) and the rest stays as one static spawned body. f_fines = 0.38: ~2e7 kg lost outright. E_kin, capped at 500 MJ, over ~6e7 kg of pieces gives v̄ ≈ 4 m/s, with small pieces at the 80 m/s cap.
- **Compare a wedge cycle** on the same silicate (vented to S = 0.05): R_f ≈ 3.6 m, v̄ ≈ 1.6 m/s, ~5–10 % grit, all of it slow and recoverable.

**Yield, so mining skill still pays** (an estimate the M5b sandbox must measure):

| Path | Mass lost at once | Mass at risk (fast pieces leave the bubble and despawn as chunks or clouds) | Typical recovered |
| --- | --- | --- | --- |
| Laser + wedge, held in the window | Ablated vapour only (~1–2 %) | Small: v̄ 1.6–4.5 m/s | ~90–95 % |
| Laser + wedge into Critical, or a detonation | ~0 | Large: 40–80 m/s pieces, shrapnel | ~40–70 % |
| Kinetic shatter | 7–20 % (fines) + crater vapour | Medium to large: v̄ 4–7 m/s near the threshold, ~25 m/s on an overkilled small rock; small pieces 15–80 m/s; cloud spreading at ≥ 2 m/s | ~55–80 % (lower for overkill) |
| Small-arms cratering | 90 % of each crater | n/a | ~10 % of what is removed |

EM in a shatter region is not heated, so it is not decayed; it is simply scattered with the ore, and EM in lost fines is lost.

### Energy weapons: heat coupling

Energy weapons feed the **same heat grid** through the same `Q_las` term, scaled by a per-weapon coupling `c_w`. The mining beam is the reference (`c_w = 1`): a continuous, defocused spot matched to rock. Weapon beams are pulsed and tightly focused for hulls, so most of their energy goes into a vapour plume, reflection and a deep narrow pit.

```
Q_wpn  = alpha(m_c) * c_w * P_wpn * W_c * dt                  same grid, same substeps, same S
         spot radius r_w from the weapon (small), so cells under it reach T_melt fast and ablate: more vapour, no yield
         particle beams spread Q over the first DepositDepth along the beam (2 m), not the surface cell: less venting
P_abs  = sum over all sources on a site per heat step <= P_siteMax (10 MW absorbed): plume shielding; the excess is lost
```

| Heat source | `c_w` | Spot | Bus → absorbed (silicate, α 0.8) | Notes |
| --- | --- | --- | --- | --- |
| Mining BeamLaser (3 MW bus, η 0.5) | 1.0 | 0.3 m + 1 mrad | 0.40 W per bus W | The reference. Throttle holds a window |
| Pulsed laser weapon (wall plug 0.3, placeholder) | 0.20 | 0.05 m | 0.048 (~8× worse) | No throttle hold: pulses overshoot the window |
| Particle beam weapon (wall plug 0.25) | 0.30 | 0.1 m, deposits to 2 m depth | 0.06 | Interior heating vents poorly: S rises fastest per joule absorbed. α is not a surface property for particles; the 0.8 is kept as a tuning factor so one formula serves |
| Plasma cannon bolt | 0.10 | 0.5 m | per bolt energy × 0.08 | One-tick deposit |
| AM annihilation lance (exotic-matter, 1 GW at the target within 2 km) | 0.02 | 0.25 m bore | 16 MW, so `P_siteMax` binds | Also bores rock as vapour at exotic-matter's 2e10 J/m³ (0.05 m³/s), emitted as server cut ops inside the 4 ops/s budget, mass lost. It sets volatile rock off within seconds: an anti-ship weapon, not a mining tool |
| Plasma thruster exhaust (exotic-matter heat cone) | 0.01 | the cone's intersection | bounded by `P_siteMax` | A 2 MN heavy thruster's ~30 GW jet would otherwise cook any rock in a second. Tested at 10 Hz against realised asteroid bodies only (rails are scenery) |
| AM standoff flash (breach not in contact) | The body's share of the gamma: `0.5 · E_AM · r_b² / (4 d²)`, surface cells only | facing hemisphere | ≤ 1 MJ per site (one step at `P_siteMax`), bulk cap applies | 1 g at 1 km on a 20 m rock would be ~12 MJ: the cap always binds near a breach. Contact breaches are a fracture, below |

- **Sites from weapons.** Weapon heat opens a heat site only once a body has absorbed at least `E_open` (1 MJ) from weapons within a 1 s window (sustained ~1 MW absorbed, or one big pulse); the site opens at the energy-weighted centroid of those hits, and the window's energy goes into it. Below that, weapon heat goes to the bulk node, which costs nothing per tick and is capped at `max(T_bg, min(T_soft, T_vap))` for weapon heat (Abuse limits), so it can neither weaken the rock nor boil volatiles out of sight of S. A per-step threshold (1 MJ in 0.1 s, i.e. 10 MW) would have equalled `P_siteMax`, so no hand weapon could ever open a site. Weapon-opened sites count against the per-body 2, at most 4 per player and 128 server-wide (of the 256).
- **Same S, same detonation.** Weapon heat drives volatiles, EM decay and S exactly like the beam. Ice-rock (S ≈ 3 once past T_vap) detonates after a few seconds of particle-beam fire, and carbonaceous rock overshoots its 30 K window under pulsed fire. Energy weapons are therefore an inefficient way to warm rock and an efficient way to set it off: the egg rule applies to them, and so do the abuse limits.
- **Ownership.** A site keeps a heat ledger of its top 4 contributors (player, joules absorbed in the last 30 s). The site owner stays the opener; a detonation is attributed to the top contributor of the last 30 s, with all 4 listed.

### Antimatter weapons

Exotic-matter.md's AM weapons are **torpedo warheads** (micro-traps of 1, 5 or 20 g; "detonation is an AM breach"), the **annihilation lance** (a beam, mapped as a heat source with a bore in the table above), and any other **breach** (a hit trap, an AM cell, a charge's leftover AM). Plasma and AM thermal exhaust heat rock through the heat-cone row above. On an asteroid a breach acts as follows (one event, server-resolved in tick step 3 in `(tick, TrapId)` order, as exotic-matter orders breaches). A contact-triggered torpedo is a contact breach; a proximity-triggered one usually is not:
- **Contact** (the trap within `r_am` of the surface; a breach inside a fragment cloud or wedged in a crack counts):
  1. **Crater:** a vapour cut, the sphere of radius `r_am = min(8 m · m_g^(1/3), 30 m)` centred on the trap, intersected with the rock. That is the terrain rule from exotic-matter (1 g → 8 m, a 10 g cell → 17 m, a 20 g warhead → 22 m, cap 30 m). Everything in it is lost, EM included. The crater is exotic-matter's and is not rate-limited; only the fracture below is.
  2. **Fracture:** a `FractureKind.AmBlast` centred on the crater, resolved with the shatter rules (no blade, shatter fines and velocities) from `E_crack = min(E_cap, η_am · E_AM)` and `E_kin = min(E_cap, ξ_am · E_AM)`, with `η_am = 1e-4`, `ξ_am = 2e-4` and `E_AM = m_AM · AmEnergy`. One gram gives 100 MJ and 200 MJ; `E_kin` saturates at E_cap from 2.5 g and `E_crack` from 5 g. The fracture region is measured from the crater rim, so the crater and the fracture are one cut record. Over the rate limit, with the bubble saturated, or with the per-body pending event taken, the AmBlast is dropped and only the crater (and the heat below) remain.
  3. **Heat:** the crater rim shell (one dx) is set to `T_melt` in an existing or newly opened site. A volatile-rich rock usually crosses S = 1 at its next heat step and **detonates on a later tick**: cascades advance one hop per tick, as exotic-matter rules.
  4. **No EM chain from ore (a proposal for exotic-matter to own; its chain rule has no purity floor today).** Exotic-matter's same-room chain (an AG pulse of yield `0.25 · min(c_EM, m_AM/AmPerEm)`) would need EM at q ≥ 0.15, the charge `MinPurity`. Until exotic-matter accepts the floor, every EM-bearing asteroid chains a small pulse (1 g of AM caps the yield at 0.05 kg). Lens ore (q ≤ 0.03, ≤ 0.3 near black holes) is below it except in the richest black-hole lenses; those chain a pulse on the next tick with `c_EM` = the content inside the crater.
  5. **Dose:** the dose pulse is exotic-matter's unchanged. Asteroids block line of sight: realised bodies by their bounding inner sphere, unrealised ones are ignored (dose is long-range and rails are cheap scenery, an accepted error).
- **Standoff** (farther than `r_am`): no crater and no fracture (no blast in vacuum). Only the flash heat row above, and the dose.

### Anti-graviton charges

Exotic-matter's `PulseKick` gives every mass the same Δv, with damage only from the gradient, and makes no terrain edits. Applied to asteroids:
- **Every asteroid body that is not Dynamic or Coasting is on the pulse skip list**, like static grids and terrain: rail asteroids, realised naturals that are static or kinematic, re-railed spawned bodies (layer `AsteroidRail`), and the permanently static bodies above `KinematicMassLimit`. They are ring structure, not bodies: kicking them would rewrite thousands of rails (a 1 kg charge's 1 km sphere holds ~500 asteroids ≥ 2 m at peak density, a 100 kg one's 4.6 km sphere ~1e4). Ships kicked into nearby rocks hit them as ordinary rams, the same "kicked into terrain" behaviour exotic-matter calls intended.
- **Dynamic asteroid bodies** (fragments, freed rocks, chunks) get the kick like any body, inside the shared per-body kick budget. Coasting debris gets it as an element update (the rails path), with no re-entry into Jolt.
- **Rubble clouds** get the Δv at their centre added to their velocity (~10 ns each). Their radius is unchanged. Bodies inside a powered gravity damper's radius are skipped, as exotic-matter rules for everything.
- **Kicked debris and protected zones:** a pulse can throw fragments at up to the 100 m/s kick cap. They fall under the protected-zone debris rule (no hull damage inside `ProtectRadius`) and are tagged with the charge's owner outside.
- **No fracture, no edits, no heat:** a pulse never breaks, cuts or warms rock, and never changes S. Charges are not mining tools; the explosive mining paths are kinetic shatter and contact AM.
- **An armed charge's physics anchor** (`A = max(2 km, R + 200 m)`) does not realise rail asteroids, since the pulse skips them. Only the normal near sphere around the charge casing applies, so arming in a dense band costs nothing extra.
- **Hot dynamic boulders can be kicked** while they carry a heat site. That is a moving bomb, and the hot-body tractor rule cannot stop it. Mitigation: a kicked body with a site whose S > 0.3 vents (k_crack + 0.5/s for 5 s) from the gradient shock. This is an open question below.

### Weapons in the mining state machine

Weapons enter the site machine at three points. The existing table is unchanged; these transitions are added:

| From | Event | To | Notes |
| --- | --- | --- | --- |
| No site | Weapon heat on the body ≥ `E_open` within 1 s | Cold (site opened at the hits' centroid, owner = shooter) | Below `E_open`: bulk node only, capped below `T_soft` and `T_vap` |
| Any heat state | Weapon heat | Same rules as the beam | `P_siteMax` caps the summed absorption. Cooling starts after 2 s with **no heat input of any kind**, not only no beam |
| Any heat state, or no site | `KineticHit` with `E_crack ≥ G_eff·F(N_sh0)·R_sh²` | Fracture (Shatter) | The site's `Q_rel` and `U_th` join the crack and kinetic energy, as a wedge would, and the site is freed. A shatter into Critical is therefore as violent as a wedge into Critical. If the event is queued, the site keeps stepping until it resolves; a detonation in between replaces it (the hit then takes the crater path) |
| Any heat state | Sub-threshold `KineticHit` in the hot zone | Same state | A crater raises `A_open` and a crack raises `k_crack`: venting rises and S drops |
| Any heat state, or no site | Contact AM breach | Fracture (AmBlast), then the rim shell's heat at the next heat step | Can lead to Detonation one or more ticks later |
| Any | AG pulse | Unchanged | Pulses never add heat or S. The only effect is the hot-kick vent on a kicked Dynamic body with S > 0.3, which lowers S |

The thermal imager's grade-3 forecast gains a **kinetic row** for the aimer's selected weapon: Crater / Crack / Shatter (region) / Shatter (whole body), plus `E_th` and `E_split` at the aim point. It is computed at 5 Hz, server-side, for the aimer only, like the wedge forecast.


## Tools as components

All are ship-hulls `ComponentDesign` function cores, data-driven in `data/components/mining/*.json`. Power is the flight model's scalar.

| Core | Key stats (defaults) | Notes |
| --- | --- | --- |
| BeamLaser | PowerMW 3, Efficiency 0.5 (beam 1.5 MW), throttle 0–100 %, SpotRadius0 0.3 m, Divergence 1 mrad, Range 400 m | One site per laser. Aims at a body-local point the turret tracks on spinning rocks (revision 3). The server finds the hit point by sphere-trace at 10 Hz. Waste heat `(1−η)·P` is reported for a future ship thermal system |
| WedgeLauncher | Muzzle 250 m/s, Reload 3 s, Magazine 6, blade roll chosen at fire | Ammo items: `Wedge.Std` (20 kg, 0.01 m², η_w 0.6) and `Wedge.Heavy` (60 kg, 0.012 m², 0.7; for metal). Recipes in `data/items/wedges.json`, from refined ore |
| ThermalImager | Grade 1: surface glow. Grade 2: site summary (T_site, S bar, state). Grade 3: 16³ slices, time to S = 1 at the current power, the **wedge forecast** (Ricochet / Crack / Spall / Split, predicted v̄), and the **kinetic forecast** for the selected weapon (Crater / Crack / Shatter region / Shatter whole, `E_th` and `E_split` at the aim point) | The server computes forecasts at 5 Hz, for the aimer only |
| CompositionScanner | Power 50 kW, 4 s per scan, range 200 m. Grade 1: bulk mix. Grade 2: 8³ map of a 32 m sphere (or the whole body ≤ 64 m), EM ≥ 1 m. Grade 3: 16³, EM ≥ 0.5 m | The server evaluates `MaterialAt` (4096 samples, ~0.1 ms job) and sends `AsteroidComposition` / `DepositRecord` to the scanner's group (exotic-matter.md privacy) |
| TractorBeam | Class S of tractor-beams.md: 200 kN, Range 150 m, PowerMW 0.5 (0.35 base + 1 MW/km), defaults to Lock-Hold | Defined in tractor-beams.md (cone, Hold/Haul/Sweep, EM + AM billing). A Hold lock is a per-hit spring-damper, so it despins fragments across the beam. Locks free static or kinematic rocks under `KinematicMassLimit` (which go dynamic, spin kept); sweeps never do. Capped by `BeamVMax` and `W_b ≤ EThrow`; throws over 50 MJ are `Thrown` (Manipulation). Vacuum mode is a wide pull sweep on the intake mouth. Subject to the abuse limits |
| Intake | Mouth 1, 2, 4 or 8 m; Throughput 500 kg/s; SweepArea 12 m² | Swallows only Loose bodies with D ≤ mouth, plus cloud mass. Packed bodies, including whole natural rocks, jam it (refused). Output is ore items per material; EM-bearing mass becomes an `em` stack with server content (exotic-matter.md) |
| MiningDrill | PowerMW 1, GrindEnergy 2e3 J/kg (500 kg/s) | Loose bodies only. Mass goes straight to cargo |

**EVA tools (revision 3).** Suit-carried items, data-driven in `data/items/eva-mining.json`, powered from the suit (power numbers are placeholders until a suit doc exists):

| Tool | Key stats (defaults) | Notes |
| --- | --- | --- |
| HandScanner | 2 kW, range 20 m, 4 s per scan. Bulk mix of the target, plus a 4³ map of an 8 m sphere; EM lenses ≥ 2 m | The server evaluates `MaterialAt` (64 samples, ~5 µs) and sends `AsteroidComposition` / `DepositRecord` to the scanner's group, as the ship scanner does (exotic-matter privacy) |
| ChipSampler | Contact (2 m), 5 s per sample, ≤ 0.2 L removed | Works on any body, Packed included. The removal is below one voxel, so there is no geometry op, only a decal mark (≤ 64 per body). Gives a `sample` item: the true composition of the 0.5 m round the point, EM content and purity included (assayed). Reveals a lens it touches to the sampler's group. Refineries refuse `sample` items |
| CoreSampler | Contact, 60 s, a 2 m core of 8 readings | As the chip sampler, along a line. The way to find a vein's direction or a lens edge before digging |
| HandDrill | 3 kW, cold, 0.016 m³/s at silicate hardness (scaled by `H0_silicate / H(T)`: metal ×0.44, ice-rock ×2), op radius 0.25–0.35 m | Only inside damage zones or on Fractured or Loose bodies. Removed volume accumulates and emits a server dig op every ~4 s (0.065 m³ at 0.25 m); the client shows a predicted local dig. Output goes to an `OreSack` within 10 m, or to a small rubble cloud at the drill (cloud merge rule) |
| SplitterCharge | 5 kg item, needs a drilled hole ≥ 0.25 m deep, 10 s to set | A `Crack` event with `R_f` = 1.5 m: adds a 2.25 m damage zone. No heat, no fragments. 6 a minute per player |
| OreSack | Loose container body, 2 t capacity, 1.5 m, tether 10 m | Tractorable and intake-accepted (mouth ≥ 2 m) like a chunk. Contents per material, EM as an `em` stack |

## Deltas and persistence

Revision 3 rewrites this section around three user decisions: mined asteroids are gone forever, belts are effectively infinite, and events may bring asteroids back, but never the same ones. Deltas compact so storage stays bounded.

**Records.**
- **Natural and event-cluster asteroids** cost nothing until they change. Changes are stored as:
  - `CellDelta { Owner: uint, RemovedSlots: ulong }` per (source, class, lane, arc) cell, written only when a cell's live population first loses a rock. A cell's owner is derived, not stored per footprint cell: it is the newest live event footprint covering the cell (the region's event list), else 0 for the natural population. `Owner` in the delta records which population the mask belongs to, so a stale mask is never applied to a later one (review fix: writing `Owner` into every footprint cell at takeover cost up to ~50 KB per event even where nobody mines, growing with time);
  - `AsteroidEdits` per cratered or worked rock: ablation, crater and hand-dig `BrushOp`s (int64 body-local), up to 8 `CrackRecord`s, up to 64 `DamageZone`s, up to 64 sample marks, and fracture cuts as event refs.
- **Spawned bodies** (re-railed packed fragments, remainders, converted naturals, freed rocks) are a `SpawnedBody` (~64 B), stored in the cell where they were re-railed. A record holds:
  - the source chain: root `AsteroidId`, event refs and fragment indices;
  - the lane or field pose (int64 1/1024 m + rotation), its snapped spin (3 B, revision 3), integrity and server mass.
- **`FractureLog`:** events and their fragment records persist (~56 B + 46 B per fragment) only while a persisted body references them (revision 2 review).
- **`RingEventLog` (revision 3):** one ~48 B record per replenishment event (below), kept while any cell still has it as owner: it is what a footprint's cells are generated from. A record whose footprint later events have covered completely is dropped (review fix: "kept forever" grew with time), so live records are bounded by the ring's area over the smallest footprint.
- **Region files:** one per 64 lanes × 64 class-0 arc cells (32 × 32 km), or one per field. Each holds cell deltas, edits, the logs and spawned bodies, like planets' LOD-10 region files. Larger-class cells file under the region of their first class-0 cell; event records file under every region their footprint touches (by reference).

**Gone forever.** A `RemovedSlots` bit never clears, and an id is never reissued: natural ids are positions in the seeded pattern, and event ids carry a serial that only increases. There is no regrowth timer.

**Effectively infinite.** The rocky reference ring holds ~5e8 rocks ≥ 2 m, and a gas giant ring ~2e10. A busy server finishing 10k mining cycles a day consumes ~1–3e4 natural rocks a day (parents, plus rocks freed by locks or shattered by gunfire; re-railed pieces are spawned bodies, not naturals). That takes 1 % of the rocky ring in ~5–17 months and all of it in ~45–140 years; a gas giant ring lasts ~40× longer. (Review fix: the earlier "~45 years to take 1 %" was 100× off.) A belt of 100–2000 fields holds ~1e8–1e9 bodies (a Plummer field holds `n0 · (4π/3) · a³`: ~6e5 at a = 10 km and peak ring density, ~5e3 at a = 2 km), so belts are effectively infinite as a whole, while a small field can be mined out; field collision events add new field blocks. What does run out is the rock **near stations**: cells there deplete and push miners outward. Replenishment events do not refill them in practice (the depletion weight below only nudges placement among ~1e5 candidate windows); they open new rich spots elsewhere.

**Replenishment events (revision 3).**

| Kind | Rate (default, per ring) | Footprint | Bodies | Composition | Signature |
| --- | --- | --- | --- | --- | --- |
| Collision | 1 a day (gas giant ring 3) | 2–8 lanes × 32–128 class-0 cells (1–4 km × 16–66 km) | 200–2,000, classes 0–5 | The band's, shifted toward its densest material | A flash and a dust shell; τ_D × 3 in the footprint, decaying over 6 h |
| Comet breakup | 1 a week | 4–16 lanes × 64–256 cells | 50–500, classes 0–4 | Ice-rock and carbonaceous only (volatile-rich: detonation-prone, see the heat model) | A long bright dust tail along the arc; τ_D × 5 decaying over 24 h |
| Moonlet shattering | 1 a month (only where the ring holds moonlets: gas giant rings past ~1.9 R) | 8–16 lanes × 128–256 cells | 2,000–20,000, all classes, rich in 6–7 | The band's, with metal cores | A visible moonlet (a class 7+ impostor) breaks; τ_D × 4 over 3 days |
| Field collision (belts) | 1 a week per belt | A new field cell block inside an existing field, or a new small field | 100–1,000 | The belt band's | As a collision |

- **Schedule:** events are drawn from a server-only `EventSeed` (like exotic-matter's `NaturalSeed`): per ring and kind, the next event tick is `last + interval`, with the interval from a hashed exponential table (integer, no `exp`). Nobody can predict events from the universe seed. Each event is announced to the whole system when it happens (a `RingEvent` message): it is a spectacle and a gold rush.
- **Placement:** the server picks the footprint among candidate windows of the ring, weighted by `1 + 4 · removedFraction` of the window's cells, so new rubble leans toward what was mined ("debris settles into the gaps"); with ~1e5 windows per ring this is flavour, not a refill. A window is rejected if it is within 20 km of any anchor, realised body or active site, or if any of its lanes would then hold more than 4 live footprints within 240 km of arc (the GPU's per-lane list). Placement is one job on a worker (~1 ms).
  - Review fix: the old rule also excluded spawned bodies and damage zones, but every mined window holds re-railed leftovers, so depleted windows were almost never eligible. And 20 km does not hide a swap: class 4–7 impostors draw to 19–120 km. So the swap is shown instead: it happens under the event's flash and dust shell, with the footprint's old impostors fading out and the new ones in over 30 s (a function of the event tick, no bandwidth). It reads as the collision itself.
- **Takeover:** the footprint's cells get the event as owner (derived from the footprint, above), and their old `CellDelta`s, mined-out bits and cosmetic crater records are deleted. The previous population of those cells, natural or an older event's, is superseded: gone forever, like mined rock, and its ids never return. Exactly one population is live per cell, so the no-overlap invariant holds within each cell as before.
  - **Players' work survives (review fix):** spawned bodies in the footprint are kept, and so are naturals with mining edits, damage zones or caves, which convert to spawned bodies at takeover. The server tests the new population's slots against them once, at takeover (the same integer overlap test), and stores the overlapping slots as the new owner's initial `RemovedSlots`, so clients and the GPU need nothing new.
- **Content:** the event instancer is the ring instancer with the event's seed (`Hash32(ringSeed, serial)`), its own per-cell counts (a 2D Gaussian blob over the footprint times the kind's class mix, built in integer like `CountFx`) and the event id layout. Fit, overlap, spin and parity rules are unchanged. EM lenses come from the server-only `Hash(DepositSeed, serial)` at the band's `EmFraction`.
- **Visuals:** a short VFX at the event tick (flash, expanding dust shell) and the dust boost above, decaying with time; both are functions of the event record and the tick, so they cost no bandwidth after the announcement.
- **Serials:** 2^30 per system, so at even 100 events a day they last ~29,000 years.

**Compaction (bounded storage).**
1. **Mined-out cells:** a cell whose `RemovedSlots` covers all of its live slots is stored as one bit in the region's mined-out bitmap (per class; ~2.5 KB per region in total) instead of a 12 B record. If every cell of a class in a region is mined out, one region bit replaces the bitmap.
2. **Dense switch:** once more than a quarter of a region's cells hold deltas, the region stores a dense array of `(Owner, RemovedSlots)` instead of a sparse map. Worst case for every cell of every class: 20,224 cells × 12 B ≈ 243 KB per region.
3. **Crater ops on naturals:** a natural keeps ≤ 32 crater ops; later small craters on it merge into the nearest op (the union's bounding sphere, conservative). At most 4096 cratered naturals per region persist; past that, craters on further untouched naturals are **ephemeral**: kept in server memory while the region is loaded, never saved. Small-arms craters are cosmetic (90 % lost, no yield), so nothing of value is dropped. Mining edits (ablation, cuts, hand digs) always persist, and a worked natural converts to a spawned body past 64 ops or 10 % of its volume.
4. **Spawned bodies:** ≤ 4096 per region (unchanged), each with ≤ 512 edit ops before its chunks bake to snapshots.
5. **Region byte budget:** 16 MB per region file (`RegionBudgetBytes`). Past it, the compactor removes the oldest re-railed boulders under 16 m that no player has touched for 30 days and that have no player within 20 km, mass lost, until the region fits. These are leftovers of finished mining, so removing them is consistent with "gone forever".
   - **Hard bound (review fix):** the per-type caps alone allow far more than 16 MB (4096 spawned bodies × 512 ops × 40 B ≈ 84 MB), and a busy region may hold no 30-day-old leftover. If the region is still over 1.25 × the budget, the compactor drops the age rule, then the size rule, removing the oldest untouched spawned bodies with no player within 20 km; bodies with an active site, a lock or an occupied cave are never removed. Every removal is logged and counted in the profiler. So 20 MB per region is a hard ceiling, and the soft 16 MB is what the user's Q10 is about.
6. **Fracture records** are dropped as soon as no persisted body references them.

The compactor runs on a worker when a region is saved (~1 ms per region). Per region the worst case is therefore ~16 MB (20 MB hard), and typical busy regions near stations sit at 0.5–3 MB. Total storage grows only with the number of regions anyone has worked, never with time spent in them.

- **Gunfire (revision 2 review, still true):** a shatter whose pieces all became chunks, clouds or despawned costs only its 56 B event until the event is unreferenced, and then nothing. Rate limits keep 64 players firing all day at ~30 MB a day of short-lived events, compacted away.

**GPU side.**
- **Mined-out bitmaps** for the regions in draw range (≤ 64 regions × 2.5 KB ≈ 160 KB) are tested first: exact, and never overflow.
- **Partial cells** are uploaded as a cell-keyed set: cell key → (64-bit removed mask, 16-bit owner slot), 24 B, 64k entries (1.5 MB), filled nearest-first and updated incrementally, plus the ids currently realised as voxel bodies.
- **Event footprints** in range (≤ 256, 48 B each) are uploaded with a per-lane list (≤ 4 footprints per lane); the enumerator hashes an owned cell with its event's seed and id layout.
- **Overflow:** if the partial set overflows, the farthest partially mined cells show their removed rocks as impostors. Fully mined cells never do (bitmaps), so the revision 2 "rocks reappear far from busy stations" artefact is now limited to partial cells beyond the 64k nearest. The profiler shows the fill.

## Multiplayer

- **Server-authoritative:** the realisation set, contacts, heat, fracture outcome (fragment set, ids, mass properties, velocities), detonation, shrapnel and hull damage.
- **Clients:**
  - rebuild rails, impostors and realised static or kinematic asteroids from id + tick, at zero bandwidth, using CRC-checked ring tables;
  - keep nearby rail asteroids in their prediction scene (C12): static in their own lane bubble, or kinematic with the rail ω if they spin (revision 3), from id + tick.
- **Messages:**

| Message | Rate / reliability | Size | To |
| --- | --- | --- | --- |
| Ring tables | Once on interest entry: CRC, plus a download only on mismatch | 8 B (~100 KB fallback) | Arriving clients |
| `HeatSiteSummary` | 10 Hz unreliable | 32 B | Crew of the lasering ship |
| `WedgeForecast` | 5 Hz | 8 B | Owner of a grade-3 imager while aiming |
| `HeatSiteVisual` (8³ u8 surface temperature, zlib) | 2 Hz | ~200 B | Clients drawing the body at voxel LOD |
| Laser on/off and throttle, wedge fired | Reliable event | 16–24 B | Interest |
| `FractureEvent` + `FragmentRecord`s + cloud | Reliable, tick-stamped | ~56 B + ~46 B per fragment + 24 B | Same frame (sibling lanes count) within 20 km, frames-and-zones' 10 Hz tier |
| Shatter and AmBlast (revision 2) | The same `FractureEvent` (Kind Shatter or AmBlast; `BladeOct` = shot axis; `Owner` per the attribution rule) plus records and one fines cloud (its `Lost` mass is in the record, not on the wire). An AmBlast references the exotic-matter `AmBreach` by tick and trap id | ~56 B + ≤ 16 × 46 B + 24 B (≤ ~0.8 KB) | As fracture events |
| Kinetic hit on rock, no fracture | Crater ops batched with ablation ops; a `CrackRecord` as a 16 B edit | 40 B per op | Interest |
| Chunk turned to cloud by a weapon | Reliable event: chunk id, the cloud record, the lost mass | ~40 B | Bubble interest |
| `KineticForecast` | 5 Hz while aiming a kinetic weapon with a grade-3 imager | 8 B | Aimer only |
| Weapon heat | No new message. Weapon heat shows through `HeatSiteVisual`; `HeatSiteSummary` also goes to the crews of the site's contributors | — | — |
| `AgDetonation` | Unchanged (exotic-matter). Clients skip rail asteroids by the same rule as the server, and kick dynamic asteroid bodies and clouds | ~48 B | As exotic-matter |
| Ablation dig ops | As planets' edits | 40 B | Interest |
| Fragments | State on change plus a 1 Hz correction, propagated with the bubble's force model (Hill terms included). Full rate only while in contact or tractored. Coasting debris: elements once per change | ~20 B per update, ~48 B per coast | Bubble interest |
| `AsteroidComposition` / `DepositRecord` | On scan or exposure | 16 / 40 B | The scanner's group, or interest of the exposing event |
| Cell deltas | On interest entry: all deltas within 20 km, plus removed masks of classes ≥ 4 within impostor range, plus mined-out bitmaps of regions in draw range (revision 3) | Tiny (bitmaps ~2.5 KB per region, zlib) | Joining or arriving clients |
| Spin (revision 3) | None: from id + tick | 0 | — |
| `RingEvent` (revision 3) | Reliable, once at the event tick; on interest entry for footprints in draw range | ~48 B | Everyone in the system (announcement) |
| `HazardBeacon` (revision 3) | 2 Hz while a site is Critical, a fracture is queued, a contact AM trap is armed on rock, or a rock is `Thrown` | 16 B | Every client within 5 km |
| `ThrownTag` / end (revision 3) | Reliable | 12 B | Interest |
| Chip/core sample result (revision 3) | Reliable | 24 B (+ a `DepositRecord` if a lens was touched) | The sampler's group |
| Hand dig ops, splitter `Crack` events and `DamageZone`s (revision 3) | As edits; zones ride the `FractureEvent` that makes them (16 B each) | 40 B per op, 24 B per splitter event | Interest |

- **Fragment ids:** `FirstBodyId + record index`. Records are in server order, so no per-fragment id traffic is needed beyond the record itself.
- **A client applies an event at its tick:**
  1. It bakes each listed fragment's geometry from the event (Voronoi cell or remainder seed). The geometry is for visuals and local prediction only; collision authority stays on the server.
  2. It takes masses, inertia, poses and velocities from the records, never from its own bake.
  3. It extrapolates ballistically to now, then blends to snapshots.
  4. The miner's own wedge impact is predicted visually only. The split appears with the event, about RTT/2 later.
- **Bandwidth per active mining site:** ~0.3 KB/s to the crew and ~0.4 KB/s per watcher, plus a burst of ≤ ~0.8 KB per fracture and a few seconds of fragment updates. Rail asteroids cost nothing.

### Determinism summary

| What | How peers agree |
| --- | --- |
| Rail existence, id, D, bulk composition, position in the lane frame, spin axis and phase (revision 3) | Integer hash + CRC-checked integer tables: exact |
| Event clusters (revision 3) | Server-scheduled from the server-only `EventSeed`; the `RingEvent` record + the same integer instancer: exact |
| Damage zones, hand dig acceptance, `Thrown` tags (revision 3) | Server decisions, sent as records and ops |
| Fog and sensor transmittance (revision 3) | Server-only floats for detection; clients use the same function for display only |
| Rail pose in a bubble | Double maths from the integer phase; ≤ 1e-9 m between peers, irrelevant |
| EM lenses | Server-only `DepositSeed`; clients receive records |
| Heat, S, forecast | Server-only floats; clients receive summaries |
| Fracture outcome (which fragments exist, ids, mass, inertia, velocities) | Server-sent records (X3) |
| Fragment geometry | Client float bake. It is near-identical on x64 but never trusted: it is visual and prediction only |
| Kinetic threshold, weapon heat, shatter and AmBlast outcome | Server-only floats; clients receive events and records like any fracture. The fracture queue is ordered by (hit tick, then AM breaches in `TrapId` order, then kinetic hits by (shooter, round sequence)), served one per bubble per tick round-robin by bubble id, so a server run is repeatable for tests and replays |
| AG pulse on asteroids | Shared `PulseKick` rule with rails on the skip list; clients predict it, and snapshots correct it |

## Performance and memory (estimates; measure before trusting)

| Item | Cost |
| --- | --- |
| Rail asteroids | 0 per tick (no entities), spinning or not |
| Spinning realised rocks (revision 3) | Own lane: kinematic with set ω, 1 Hz resync (~0.05 µs/tick each) + Jolt's active-body cost (~0.2–1 µs). The lane bubble's `Update` can no longer be skipped: ~20–50 µs per mining bubble per tick. Measure in `--zone-bench` |
| Held and thrown rocks (revision 3) | ~2 µs per held rock per tick (force, awake body, raster share); `Thrown` sweep 2–4 µs per body per tick, ≤ 8 per player, ≤ 128 per server (≤ 0.5 ms worst, as a job) |
| Replenishment events (revision 3) | Placement ~1 ms on a worker per event (a few a day). GPU: footprint test per enumerated cell, ≤ 4 per lane, ~+3 % enumeration |
| Fog (revision 3) | Haze dust channel +0.02–0.05 ms GPU. Impostor fog cull +~10 % enumeration ALU, −30–70 % impostor draws in dusty bands. `RingObscuration` ~0.3 µs per sensor pair (~20 ns cached); at 64 players × 50 candidates × 2 Hz ≈ 20 µs per tick |
| Caves (revision 3) | ~35 chunk builds (0.25 m) on entering a zone, then 1–2 per hand op; ≤ 1 op/s per player. Damage-zone test ~50 ns per op |
| Compaction (revision 3) | ~1 ms per region on a worker, at save |
| CPU instancer query (realisation, 10 Hz staggered, near sphere + capsule ≤ ~30 cells per anchor) | ~15 µs per anchor |
| Lane phase upload (lanes in range) | ~40 µs/frame CPU, 22 KB |
| Realised asteroids | ~5–15 per anchor at peak density (× ρ_b). Own-lane non-spinning ones are static and asleep (0 per tick); own-lane spinning ones as above. Others: `MoveKinematic` ~0.3 µs each per tick. Cap 64 per bubble. Measure in `--zone-bench` |
| Realisation (server shape) | D < 8 m: 0.3–1 ms. 40 m: ~5 ms. A shape LRU of 512 ids covers back-and-forth flight. At 100 m/s through peak density ≈ 1 realisation/s |
| Voxel bodies (client render) | ~60 bodies in range at peak (mostly classes 2–5). At 100 m/s, ~40 builds/s ≈ 2 % of one core |
| Coasting debris | 0 per tick |
| Fast-transit sweep | ~20–50 µs per fast ship per tick |
| Heat site step (16³) | ~20 µs per substep. 10 Hz × 1–8 substeps → ~3–25 µs per tick averaged. 64 active sites ≈ 0.2–1.6 ms per tick on jobs. Server cap 256 sites |
| Fracture evaluation | ~50 µs main thread (sites, velocities). Fragment bakes and mass integration ≤ 20 × 0.5–3 ms on workers (10–60 ms per event). ≤ 4 events per tick server-wide, fair-queued, inside `FractureJobMsPerTick` (4 ms of worker time a tick): sustained, that is ~4–24 events/s. 64 players at their limit (0.1 events/s each) make ~6.4 events/s: inside the budget unless most are worst-case 16-piece bakes, when the queue (64, then crater + crack) absorbs the excess. Measure |
| Shrapnel sweep | 32 rays × ≤ 90 ticks, against the entities within reach: ~5 µs per tick per live detonation |
| Projectile sweep against rails (revision 2) | Each round's tick segment against the instancer cells along it (≤ 133 m a tick at 8 km/s, 3–6 cells): ~2–4 µs per round per tick. Rounds under `RailTestMinEnergy` (1 MJ) test rails at 10 Hz as a **look-ahead** over their next 6 ticks (rounds fly straight and rails are a function of the tick, so the result is exact), and a hit truncates the round's life at that tick, so it cannot also hit a ship behind the rock: ~0.5 µs per tick amortised. Energy is only a cost class: under-1 MJ rounds can still crater, crack, and shatter small or cracked rocks (cold 4 m silicate is 0.83 MJ). Server budget 512 rounds a tick, run as a job (1–2 ms of worker time at the budget); past it the oldest low-energy rounds test realised bodies only and pass through rails, an accepted error. Realised bodies are tested by the combat code's own sweep, at no extra cost here |
| Kinetic hit resolution | Threshold test ~2 µs (8 `G_eff` samples plus crack lookup); crater accumulation ~50 ns; a shatter costs one fracture evaluation (same row as above, with up to 16 fragment bakes on workers) |
| Weapon heat | ~50 ns per hit into a per-site accumulator, applied at the next heat step. No extra steps: weapon heat rides the 10 Hz site step. Sub-`E_open` hits: ~20 ns into the bulk node plus the body's 1 s window accumulator. Plasma exhaust: a 10 Hz cone test against realised asteroid bodies, ~0.2 µs per (thruster, body) pair |
| Chunk to cloud (weapon hit on a Loose chunk) | ~1 µs; removes a body |
| AM contact breach | One crater cut + one AmBlast fracture evaluation; dose occlusion by realised rocks ~0.5 µs per (receiver, rock) pair |
| AG pulse on asteroids | Rails and non-dynamic naturals: 0 (skip list). Dynamic asteroid bodies: exotic-matter's ~1 µs each. Clouds ~10 ns each |
| Dynamic asteroid bodies in a mining bubble | Fragments and freed rocks never sleep in a Hill bubble (tide plus Coriolis). Up to ~150 awake bodies near miners ≈ 0.2–0.5 ms of Jolt per bubble per tick, until coasting takes them out. Measure |
| GPU, far from rings | Annulus 0.02–0.05 ms + ring shadow term in terrain, hull and atmosphere shaders (~free) |
| GPU, inside a ring | Annulus + haze 0.15–0.3 + gravel 0.05 + enumeration 0.02–0.05 + impostors 0.15–0.3 ≈ **0.4–0.7 ms** at 1440p in the densest clear band (ρ_b = 1 is the revision 2 default). Sparse bands cost less in proportion to ρ_b; dust adds ~0.02–0.05 ms to the haze and removes impostors by the fog cull. If it is over 0.6 ms, the first knob is the 300 factor |

| Memory | Value |
| --- | --- |
| Lane table + quantiles + cross-section table + τ texture, per ring | ~0.3 MB |
| Archetype impostor meshes | ~1 MB |
| Instance buffer (64k × 32 B), GPU removal set (revision 3: 24 B entries), mined-out bitmaps in range, event footprints | 2 MB + 1.5 MB + 0.16 MB + 12 KB |
| Ring texture (revision 3: two RGBA16F texels per radius) | 128 KB per ring |
| Realised voxel bodies (client) | ~60 bodies, mostly small: ~10 MB, plus at most 4 bodies over 100 m at LOD-0 detail (~9 MB each) |
| Chunk collision rings on big asteroids (server) | As planets: ~50 chunks per character on a rock × 60–300 KB (P1 fallback) |
| Heat sites (server) | 48 KB each; 12 MB at the 256 cap |
| Shapes (server) | ~1–2 MB per bubble |
| Deltas | Resident only for loaded regions; ≤ 16 MB per region file by the byte budget, typically 0.5–3 MB near stations (revision 3) |
| Cave detail chunks (revision 3) | ~2–10 MB per caving character (P1 fallback) |

**Caps:**

| Item | Cap |
| --- | --- |
| Slots per cell | 63 |
| Realised rail asteroids per bubble | 64 |
| Contact-freed natural asteroids per bubble | 32 |
| All asteroid-related bodies per bubble | 256 |
| Chunks per bubble | 128 |
| Clouds per bubble | 16 |
| Heat sites per body / server | 2 / 256 |
| Heat substeps | 8 |
| Fragments + remainders per event | 16 + 4 |
| Fragment chain depth | 4 |
| Crack records per body | 8 |
| Edit ops per body before a chunk snapshot | 512 |
| Fracture events per tick (server / bubble) | 4 / 1 |
| Fracture events per player per minute | 6 |
| Event energy `E_cap` / shrapnel speed | 500 MJ / 1.5 km/s |
| Shatter and AmBlast fragment speed `ShatterVMax` | 80 m/s |
| Absorbed heat power per site `P_siteMax` | 10 MW |
| Weapon-opened heat sites per player / server | 4 / 128 |
| Projectile rail sweeps per tick (server) | 512 |
| Fracture queue (server) / pending fracture events per body | 64 / 1 |
| Fracture worker time per tick `FractureJobMsPerTick` | 4 ms |
| Spawned bodies per region file | 4096 |
| Region file byte budget (revision 3) | 16 MB soft, 20 MB hard (review) |
| Cratered naturals persisted per region / crater ops per natural (revision 3) | 4096 / 32 |
| Damage zones / sample marks per body (revision 3) | 64 / 64 |
| Hand dig ops per player (revision 3) | 1 per s, inside the body's 4 per s |
| Splitter events per player per minute / server per tick (revision 3) | 6 / 16 |
| Live `Thrown` rocks per player / server (revision 3) | 8 / 128 |
| Event footprints drawn (GPU) / per lane (revision 3) | 256 / 4 |
| Band density `ρ_b` (revision 3) | ≤ 1 (slot clamp) |
| Caving characters with sub-base detail (review) | 32 per server |
| Live event footprints per lane within 240 km of arc (review) | 4 |
| Asteroid beam energy relative to the lane, `max(W_b, E_rail)` (review) | 500 MJ |

**Profiler rows:**
- Rings: CPU query ms, lanes uploaded, GPU enumeration / haze / gravel / impostor ms, instance count, removal-set fill.
- Asteroids: realised (static / kinematic / dynamic / coasting), shape builds/s, bake p50/p95, MoveKinematic count.
- Heat: sites active / cooling, substeps, step ms.
- Fracture: events by kind (incl. Shatter and AmBlast), queue depth, fragments, chunks, clouds, bake ms, mass lost per event, mass-check failures.
- Weapons: kinetic hits on rock (crater / crack / shatter region / shatter whole / degraded by limit, queue, saturation), chunks turned to cloud, rail-sweep rounds and µs, look-ahead truncations, weapon-heat joules absorbed and lost to `P_siteMax` and to the bulk cap, weapon-opened sites, AG-pulse asteroid kicks, fracture job ms against `FractureJobMsPerTick`, debris contacts suppressed in protected zones.
- Net: mining bytes/s, table CRC mismatches.
- Revision 3: spinning realised rocks (kinematic own-lane / other-lane), lane bubbles stepped because of spin, held and `Thrown` rocks and sweep µs, ram hits by thrown rocks; events placed, footprints drawn, partial-set fill and overflow, mined-out bitmap regions; fog: haze dust ms, impostors culled by fog, sensor transmittance calls; caves: caving characters, cave chunk builds, hand ops accepted and refused (zone, heat, rate), splitter events, samples; compaction: region bytes, ephemeral craters, removed leftovers.

## Data definitions

| File | Holds |
| --- | --- |
| `data/asteroids/materials.json` | The material table above, plus colour/albedo and ore item id |
| `data/asteroids/archetypes.json` | 16 unit-space SDF recipes (ellipsoid, lobes, craters) |
| `data/asteroids/ring-styles.json` | Band palettes: composition weights, colour, α_s, α_b, `EmFraction`, gap and ringlet counts. Revision 3: per band `density` ρ_b and `dust` D_b ranges, the darkening factor, the dust colour and phase g; density and dust presets; per host kind (rocky, gas giant) the band count and preset weights |
| `data/asteroids/fields.json` | Belt and field styles: Plummer a, density, dust (revision 3), composition, `EmFraction` |
| `data/asteroids/events.json` (revision 3) | Replenishment event kinds: rates per host, footprint ranges, body counts, class mixes, composition rules, dust boost and decay, the placement weight and exclusion radius |
| `data/items/eva-mining.json` (revision 3) | EVA tools (table above), `sample` and `OreSack` items |
| `data/sensors/ring-obscuration.json` (revision 3) | `κ_part` and `κ_dust` per sensor kind, for the future sensor doc |
| `data/asteroids/mining.json` | Global knobs: γ, χ, χ_det, ψ, K, w_min, k_v0, k_open, k_crack, δ_min, `D_dig`, `KinematicMassLimit`, `SCrit`, `SCritExit`, heat rates and substeps, `E_cap`, `ShrapnelVMax`, `ProtectRadius`, rate limits. Revision 2: a `weapons` block (ζ_sh, N_sh0, η_c, the fines curve, `FinesLost`, `CraterLost`, `ShatterVMax`, `P_siteMax`, `E_open` and its window, the bulk cap, weapon sites per player and server, `RailTestMinEnergy`, the sweep budget, the fracture queue and job budget, the bubble body budget, the protected-zone debris rule, η_am, ξ_am, the AM contact rule). Revision 3: a `spin` block (`SpinFraction`, period bounds per class), a `manipulation` block (`ThrownMax`, server cap, tag end rules), a `caves` block (ζ_dz, splitter R_f, cave detail radius and levels, heat exclusion, op rates), a `bystanders` block (beacon radius and rate), and a `persistence` block (crater op merge cap, cratered naturals per region, region byte budget, leftover age) |
| Weapon item and component defs (combat, later) | Each weapon carries an `asteroid` block: `kind` (Kinetic, Heat, None), η_k, ξ_k, `shatterCoupling` (0 for wedges), c_w, spot radius, deposit depth, bore energy (the AM lance) |
| `data/systems/*.json` | Per-body `Ring` overrides, belt overrides |
| `data/components/mining/*.json` | Function cores (table above) |
| `data/items/wedges.json`, `data/items/ores.json` | Ammo and ore items, recipes |

EM fraction, purity and lens shape are not in these files; they live in exotic-matter.md's `data/deposits/*.json`. AM energy, breach radii and pulse parameters stay in exotic-matter.md's `data/physics/exotic.json`.

```json
{ "id": "silicate", "density": 2700, "cv": 2200, "kappa": 0.12, "absorb": 0.8, "emissivity": 0.9,
  "tSoft": 500, "tMelt": 1600, "h0": 4e8, "g0": 2.2e4, "sigmaT": 2.4e7,
  "volatile": { "fraction": 0.01, "tVap": 600, "latent": 5e3, "gasEnergy": 2e5 },
  "packing": 0.95, "betaTh": 0.05, "ablation": 2e7, "ore": "ore.silicate", "colour": [0.42, 0.38, 0.33] }

// data/asteroids/mining.json, revision 2 block
"weapons": { "zetaSh": 0.25, "nSh0": 10, "nShMin": 8, "craterEta": 0.5, "craterLost": 0.9,
  "fines": { "base": 0.15, "perOctave": 0.05, "max": 0.40, "lost": 0.5, "cloudMinSpeed": 2.0, "cloudSpeedOfMean": 0.5 },
  "shatterVMax": 80, "shatterVSep": 1.0, "shatterSpeedExponent": 0.3333, "coneJitterDeg": 60,
  "pSiteMaxW": 1.0e7, "eOpenJ": 1.0e6, "eOpenWindowS": 1.0, "bulkWeaponCap": true,
  "weaponSitesPerPlayer": 4, "weaponSitesServer": 128, "heatLedgerS": 30,
  "railTestMinEnergyJ": 1.0e6, "railSweepBudget": 512,
  "fractureQueueMax": 64, "fractureJobMsPerTick": 4.0, "bubbleBodyBudget": 256,
  "looseChunkG0Factor": 0.2, "protectedDebrisNoDamage": true,
  "am": { "crackPerJ": 1.0e-4, "kineticPerJ": 2.0e-4, "contactUsesBreachRadius": true, "chainMinPurity": 0.15 },
  "pulse": { "skipRails": true, "hotKickVentPerS": 0.5, "hotKickVentS": 5, "hotKickS": 0.3 } }

// a weapon def's asteroid block (combat owns the rest of the def)
{ "id": "weapon.railgun.s", "asteroid": { "kind": "Kinetic", "etaK": 0.25, "xiK": 0.30, "shatterCoupling": 1 } }
{ "id": "weapon.laser.pulse", "asteroid": { "kind": "Heat", "couplingW": 0.20, "spotRadiusM": 0.05, "depositDepthM": 0 } }
{ "id": "weapon.particle.beam", "asteroid": { "kind": "Heat", "couplingW": 0.30, "spotRadiusM": 0.1, "depositDepthM": 2.0 } }
{ "id": "weapon.am.lance", "asteroid": { "kind": "Heat", "couplingW": 0.02, "spotRadiusM": 0.25, "depositDepthM": 0, "boreJPerM3": 2.0e10 } }
{ "id": "weapon.am.torpedo.s", "asteroid": { "kind": "None" } }   // its breach is exotic-matter's AmBreach, mapped by the AM contact rule

// revision 3: a band in data/asteroids/ring-styles.json
{ "composition": { "silicate": 0.6, "carbonaceous": 0.3, "metal": 0.1 }, "emFraction": 0.02,
  "density": [0.7, 1.0], "dust": [0.4, 0.7], "darken": 0.6, "dustColour": [0.82, 0.85, 0.9], "dustG": 0.75 }

// revision 3: data/asteroids/mining.json blocks
"spin": { "fraction": 0.7, "periodMinS": [300, 300, 900, 900, 3600, 3600, 10800, 10800],
          "periodMaxS": [1800, 1800, 7200, 7200, 18000, 18000, 43200, 43200], "tableSize": 64 },
"manipulation": { "thrownMaxPerPlayer": 8, "thrownMaxServer": 128, "thrownEndS": 120, "thrownEndSpeed": 5.0 },
"caves": { "zetaDz": 1.5, "zetaDzDetonation": 1.3, "splitterRf": 1.5, "detailRadiusM": 24, "detailLevels": 2,
           "collisionRadiusM": 16, "heatExclusionM": 6, "opsPerPlayerS": 1, "splittersPerPlayerMin": 6, "zonesPerBody": 64 },
"bystanders": { "beaconRadiusM": 5000, "beaconHz": 2 },
"persistence": { "craterOpsPerNatural": 32, "crateredNaturalsPerRegion": 4096, "regionBudgetBytes": 16777216,
                 "leftoverAgeDays": 30, "leftoverMaxD": 16 }

// revision 3: data/asteroids/events.json (one kind)
{ "kind": "Collision", "ratePerDay": { "rocky": 1, "gasGiant": 3 }, "lanes": [2, 8], "arcCells": [32, 128],
  "bodies": [200, 2000], "classes": [0, 5], "dustBoost": 3, "dustDecayH": 6, "depletionWeight": 4, "exclusionM": 20000 }
```

## C# types (sketch, `src/Asteroids`, no Vulkan)

```csharp
public readonly record struct RingParams(ulong Seed, double InnerRadius, double OuterRadius,
    float ScaleHeight, float PeakTau, float PeakColumnCount, float ParticleAlpha, float BodyAlpha,
    RingStyleId Style, RingHostKind Host);                                                 // planet inertial, equatorial; Host: revision 3

public enum RingHostKind : byte { Rocky, GasGiant }      // revision 3

public struct RingBand                                   // revision 3: per band, from the style (data)
{
    public float Inner, Outer, Edge; public byte Composition; public float EmFraction;
    public ushort DensityFx;                             // rho_b in 0.16 fixed point: enters CountFx (existence)
    public float Dust, Darken;                           // render and sensors only
}

public readonly record struct FieldParams(ulong Seed, KeplerOrbit Orbit, float PlummerA, float PeakDensity,
    float BodyAlpha, float Dust, FieldStyleId Style);                                      // rides FrameKind.Field

public readonly record struct AsteroidId(ulong Value);   // source 13 | class 3 | a 14 | b 16 | c 12 | slot 6
                                                         // ring: a lane, b|c<<16 arc (28 bits, revision 3)
                                                         // event cluster: source 8191 | class 3 | serial 30 | cell 12 | slot 6

public sealed class LaneTable                            // built at load, SoA, zero alloc after; CRC sent by server
{
    public int LaneCount; public double InnerRadius;     // lane i centre = InnerRadius + 512 (i + 0.5)
    public uint[] CountFx;     // [lane*8 + class] 16.16 expected count per cell
    public int[] ArcCells;     // [lane*8 + class], nested: class-0 count is a multiple of 16
    public ulong[] PhaseRate;  // rev per tick, 0.64 fixed; phase = tick * rate (wrapping)
    public byte[] Band; public float[] Tau;
    public float[] TauDust;    // revision 3: per lane, render + sensors
    public uint[] SpinRate;    // revision 3: [class*64 + index] rev per tick, 0.32 fixed; 0 = locked
    public uint Crc;           // covers CountFx, ArcCells, PhaseRate, SpinRate, quantiles
}

public struct AsteroidSeed                               // decoded on demand, never stored; public data only
{
    public AsteroidId Id; public long3 LocalFx;          // lane/field frame, 1/1024 m
    public ushort DiameterCm; public byte Archetype; public byte ScaleX, ScaleY, ScaleZ;
    public uint Rotation;                                // q_hash in the lane/field frame
    public uint SpinRate; public byte SpinAxis;          // revision 3: spin(tick) = (uint)(tick * SpinRate); axis = archetype's shortest scaled axis
    public byte Bulk, Core, CoreFraction, Veins; public uint DetailSeed;
    public bool EmBearing => false;                      // never decodable on clients: server reads DepositSeed
}

public static class AsteroidInstancer                    // CPU authority; mirrored by asteroid_enum.comp
{
    public static int Cell(LaneTable t, in CellKey cell, Span<AsteroidSeed> output);
    public static int Query(LaneTable t, in RealiseVolume v, long tick, Span<AsteroidSeed> output); // sphere + capsule
    public static double3 PoseAt(LaneTable t, in AsteroidSeed a, long tick, out doubleQuat rotation);
}

public enum Integrity : byte { Packed, Fractured, Loose }   // revision 3: Fractured = hand-diggable throughout (boulders born from a fracture)
public enum MotionKind : byte { Rails, Static, Kinematic, Dynamic, Coasting }

public sealed class VoxelBody                            // realised asteroid or fragment (pooled)
{
    public BodyKey Key; public BodySource Source;        // root AsteroidId + (eventSeq, record) chain, depth <= 4
    public float BaseVoxel; public byte RootLod;
    public Integrity Integrity; public MotionKind Motion;
    public EditStore Edits;                              // small per-body store, int64 body-local
    public NativeList<CrackRecord> Cracks; public NativeList<uint> Cuts;   // event seqs
    public NativeList<DamageZone> Zones;                 // revision 3: <= 64, hand-dig acceptance
    public ThrownTag Thrown;                             // revision 3: Owner == 0 when not thrown
    public MassMoments Mass; public MaterialMass MassByMaterial;          // the second is server only
    public BodyRef Physics;
    public short SiteA = -1, SiteB = -1;
    public int PendingFracture = -1;                     // revision 2 review: queue slot of this body's one pending event
    public float WeaponJWindow; public long WeaponWindowTick; public float3 WeaponCentroidSum;   // E_open 1 s window (server)
}

public struct AsteroidDensity : IDensitySource { /* archetype + noise + edits + cuts + cell; MaterialAt (EM only with server DepositIndex) */ }

public sealed class HeatSite                             // pooled, 256 on a server
{
    public BodyKey Body; public int3 OriginFx; public float Dx; public byte Substeps;
    public float[] T; public Half[] Volatile, Exotic; public byte[] Solid, Material, Exposed;
    public float TBulk, CBulk, QGas, UEm, UTh, VHot, VAbl, VentRate, Stability;
    public float PendingWeaponJ, AbsorbedThisStep;      // revision 2: weapon heat accumulator, P_siteMax check
    public HeatContributor C0, C1, C2, C3;               // top 4 heat contributors (attribution)
    public SiteState State; public long LastBeamTick; public PlayerId Owner;
}
public enum SiteState : byte { Cold, Warming, Window, Critical, Cooling }

public enum FractureKind : byte { Crack, Spall, Split, Detonation, Shatter, AmBlast, Sever }   // revision 2: Impact -> Shatter (rams and projectiles)

public enum KineticSource : byte { Projectile, Ram, Contact }
public struct KineticHit                                 // server only, from the combat sweep or the fast-transit sweep
{
    public BodyKey Body; public int3 PointFx; public float3 Dir; public float EnergyJ, EtaK, XiK;
    public KineticSource Source; public PlayerId Shooter; public long Tick;
}
public struct WeaponAsteroidDef { public byte Kind; public float EtaK, XiK, ShatterCoupling, CouplingW, SpotRadius, DepositDepth, BoreJPerM3; }
public struct HeatContributor { public PlayerId Player; public float Joules; }   // HeatSite keeps 4, decayed over 30 s

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FractureEvent                              // 56 B wire + persistence
{
    public BodyKey Body; public uint EventSeq; public long Tick; public uint Seed;
    public int CX, CY, CZ;                               // body-local 1/1024 m
    public ushort BladeOct, BiasOct, BiasStrength, RadiusCm;
    public byte N; public FractureKind Kind; public byte FragmentCount, RemainderCount;
    public uint FirstBodyId; public PlayerId Owner;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FragmentRecord                             // ~46 B, server-computed (X3)
{
    public byte Cell;                                    // 0..15 Voronoi cell, 0xFF = remainder
    public byte Flags;                                   // tier, Packed/Loose
    public ushort SeedX, SeedY, SeedZ;                   // remainder KeepSeedCell in the parent's coarse grid
    public float MassKg;
    public short CoMx, CoMy, CoMz;                       // 1/64 m from the event centre (+-512 m)
    public float Ixx, Iyy, Izz;                          // principal moments (Half overflows past ~6.5e4 kg m^2)
    public uint PrincipalAxes;                           // smallest-three
    public Half Vx, Vy, Vz, Wx, Wy, Wz;                  // relative to the parent point velocity
}

public struct CrackRecord { public int CX, CY, CZ; public ushort RadiusCm, NormalOct; }
public struct RubbleCloud { public uint Id; public double3 Centre; public float3 Velocity; public float Radius0, Speed; public long BirthTick; public MaterialMass Mass; }
public struct CellDelta { public uint Owner; public ulong RemovedSlots; public int FirstSpawned, SpawnedCount; }   // Owner: 0 natural, else event serial (revision 3)
public struct SpawnedBody { public uint Id; public BodySource Source; public long3 PoseFx; public uint Rotation; public ushort SpinAxisOct; public byte SpinIndex; public Integrity Integrity; public float MassKg; }

// revision 3
public enum RingEventKind : byte { Collision, CometBreakup, MoonletShatter, FieldCollision }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RingEventRecord                            // ~48 B, persisted forever, announced system-wide
{
    public uint Serial; public RingEventKind Kind; public ushort Source; public long Tick; public uint Seed;
    public ushort Lane0, Lanes; public uint Arc0; public ushort Arcs;   // footprint in class-0 cells
    public ushort PeakFx, SigmaLanes, SigmaArcs; public byte ClassMix, Composition; public float DustBoost, DustDecayH;
}
public struct DamageZone { public int CX, CY, CZ; public ushort RadiusCm; public ushort Flags; }   // 16 B, body-local 1/1024 m
public struct ThrownTag { public PlayerId Owner; public long EndTick; public float WorkJ; }
public struct HazardBeacon { public EntityId Source; public byte Kind; public float3 Position; public float ReachM; }  // 16 B on the wire
public enum SensorKind : byte { Optical, Thermal, Radar, Signature }

public static class RingObscuration                      // public ring data; server for detection, client for display
{
    public static float OpticalDepth(in RingParams ring, LaneTable t, double3 a, double3 b, SensorKind kind);   // 8-point Simpson
    public static float Transmittance(in RingParams ring, LaneTable t, double3 a, double3 b, SensorKind kind);
    public static float EffectiveRange(float nominalRange, float sigma);                                       // (2/sigma) W(sigma R/2)
}

public static class RingEvents                           // server: schedule from EventSeed, place, take over cells
{
    public static bool TryPlace(in RingParams ring, RingEventKind kind, long tick, ReadOnlySpan<double3> exclusions, out RingEventRecord e);
    public static int Cell(in RingEventRecord e, LaneTable t, in CellKey cell, Span<AsteroidSeed> output);   // the instancer with the event seed
}

public static class Fracture                             // server: full; clients: geometry only
{
    public static int Sites(in FractureEvent e, Span<float3> sites);
    public static float CellSdf(in FractureEvent e, ReadOnlySpan<float3> sites, int cell, float3 p);
    public static void SolveVelocities(in FractureEvent e, ReadOnlySpan<FragmentRecord> m, ReadOnlySpan<float3> sites,
                                       Span<float3> v, Span<float3> w);  // server only; results go into records
}

public static class MiningRules                          // every formula above, one place, knobs from mining.json
{
    public static float Weakening(in AsteroidMaterialDef m, float t);
    public static bool Seats(in WedgeDef w, float relSpeed, in AsteroidMaterialDef m, float tAim, float crack, Integrity i);
    public static FractureKind Resolve(HeatSite s, in WedgeHit hit, out float radius, out int n, out float eKin);
    public static bool Allowed(in AbuseContext c, MiningAction a);   // protected zones, hot-tractor, rate limits, weapons
    public static float ShatterThreshold(in AsteroidMaterialDef m, float gEff, float diameter);  // E_th, J of projectile energy
    public static float WholeShatterEnergy(float gEff, float rReach);                            // E_split, J of projectile energy
    public static bool ChunkToCloud(in AsteroidMaterialDef m, in KineticHit hit, float chunkDiameter); // Loose chunks: cloud or impulse
    public static FractureKind ResolveKinetic(HeatSite s, in KineticHit hit, float gEff, float rReach,
                                              out float radius, out int n, out float eKin, out float finesFraction);
    public static FractureKind ResolveAmContact(HeatSite s, float amGrams, out float craterRadius, out float radius, out float eKin);
    public static bool HandDigAllowed(VoxelBody b, float3 centre, float radius, in AbuseContext c);   // revision 3: zones, heat exclusion, rates
    public static float DamageZoneRadius(FractureKind k, float rF);                                 // revision 3
}
```

## Files

| File | Fate |
| --- | --- |
| `src/Terrain/Meshing/SurfaceNetsMesher.cs`, `ChunkMesh.cs`, `PackedTerrainVertex.cs` | Reuse. Cell scale becomes a parameter (ship-hulls asks the same); 16-bit indices (planets) |
| `src/Terrain/Voxels/VoxelChunk.cs`, `ChunkConstants.cs`, `ChunkCoord.cs` | Reuse the 34³ layout. `VoxelSize` stops being a global const: the voxel scale is the volume's base × 2^Lod, passed to the job and the mesher |
| `src/Terrain/Editing/BrushOp.cs` (`VoxelEditor`) | Reuse the apply loop with int64 positions (C13). Packed bodies refuse player Dig ops; ablation ops come from the server |
| `src/Terrain.Runtime/ChunkJob.cs` | Change: generic `IDensitySource` (planet, asteroid, fragment). Revision 3: leaves below a body's base voxel (to 0.25 m) inside damage zones near EVA characters |
| `src/Terrain/Voxels/Noise.cs` | Reuse (a float lattice is enough in body-local space) |
| `src/Physics/PhysicsWorld.cs` → `PhysicsScene` | Add static creation with `AllowDynamicOrKinematic`, kinematic `MoveKinematic` rails, `SetMotionType` (export probe), dynamic convex compound, `OffsetCenterOfMassShape`, Hill-frame force terms (shared with the surface-frame pass). Revision 3: kinematic bodies with a set angular velocity and 1 Hz resync, chunk collision sub-shapes on a kinematic body's `MutableCompound` |
| `src/Physics/ObjectLayers.cs` | Add `AsteroidRail` (no self-collision) and `Debris` (already planned) |
| `src/Physics/StaticMeshShape.cs` | Reuse for the chunk collision ring on big asteroids (pooled index buffer, review P2) |
| New `src/Physics/ConvexDecomposition.cs` | Shared by hulls and asteroids (moved out of the hull plan) |
| New `src/Frames/` additions | `FrameKind.Lane` (virtual, from ring + lane + tick), `FrameKind.Field`, `BubbleAnchor.Source` |
| New `src/Asteroids/` | `RingParams.cs`, `RingGenerator.cs`, `LaneTable.cs`, `FieldParams.cs`, `AsteroidId.cs`, `AsteroidHash.cs`, `AsteroidInstancer.cs`, `Archetypes.cs`, `AsteroidDensity.cs`, `Composition.cs`, `VoxelBody.cs`, `HeatSite.cs`, `HeatSim.cs`, `MiningRules.cs`, `Fracture.cs`, `HillCoast.cs`, `RubbleCloud.cs`, `AsteroidDeltaStore.cs`, `FractureLog.cs`, `MaterialDefs.cs`. Revision 3: `RingBand.cs`, `SpinTable.cs`, `RingEvents.cs` (schedule, placement, takeover, event instancer), `RingEventLog.cs`, `RegionCompactor.cs` (mined-out bitmaps, dense switch, crater merge, byte budget), `DamageZones.cs`, `RingObscuration.cs` |
| New `src/Asteroids.Runtime/` | `AsteroidRealiser.cs` (sphere + capsule membership), `VoxelBodyJob.cs`, `FastTransitSweep.cs`, `ShrapnelSweep.cs`, `ProjectileRailSweep.cs`, `AsteroidWeaponHits.cs` (kinetic hits, weapon heat, AM contact, pulse skip glue), `RingRenderer.cs` (annulus, haze, gravel), `AsteroidImpostors.cs` (enumeration + indirect draws + spawned-body list), `MiningTools/*` (laser, wedge, imager, scanner, tractor, intake and drill behaviours), `MiningPanel.cs` (ImGui sensors), profiler rows. Revision 3: `ThrownSweep.cs`, `HazardBeacons.cs`, `CaveDetail.cs` (sub-base refinement and the 16 m collision ring), `EvaMiningTools/*` (scanner, chip and core sampler, hand drill, splitter, ore sack), `RingEventVfx.cs` |
| New shaders | `ring_annulus.vert/.frag`, `ring_haze.frag`, `ring_gravel.vert`, `asteroid_hash.glsl`, `asteroid_enum.comp`, `asteroid_instance.vert/.frag`, `ring_shadow.glsl` (included by terrain, hull, impostor and atmosphere, and the gas giant cloud shaders). Revision 3: the dust channel in `ring_annulus.frag` and `ring_haze.frag`, per-instance spin rotation in `asteroid_enum.comp` (not per vertex), fog cull, mined-out bitmaps and event footprints in `asteroid_enum.comp`, `ring_slab_depth.glsl` (closed-form line-of-sight optical depth, shared with the cull) |
| New tests | CPU/GPU instancer parity; lane-fit no-overlap (including candidate-vs-larger-class); ring table CRC equality across Debug/Release and two machines; re-rail never overlaps; fracture run-to-run determinism on the server; client bake vs server records within visual tolerance; mass conservation per event; heat sim stable at κ = 0.4, dx = 0.375 m; forecast vs sim outcome per material. Revision 2: shatter threshold monotone in D, G0, heat and cracks; wedges never shatter; mass conservation with the Lost bucket for Shatter and AmBlast; `P_siteMax` holds with every source at once; weapon heat per bus watt ≤ 1/5 of the beam's; AG pulses leave rails and static naturals untouched; protected-zone asteroids inert to every weapon; over-limit shatters degrade to crater + crack. Revision 2 review: crack records do not stack (hits under 0.4·E_th never shatter cold rock); `E_kin` including a dynamic parent's recoil stays ≤ `E_cap` and every speed ≤ `ShatterVMax`; sub-`E_open` weapon heat never lifts `T_bulk` past the cap; look-ahead rail hits truncate the round before any later hit; the per-bubble body budget and cloud merge hold under sustained fire; one pending event per body and the 64-event queue bound; asteroid debris does no hull damage in protected zones; Loose chunks hit by weapons become cloud mass, never new bodies. Revision 3: CPU/GPU spin parity (orientation within float tolerance after 2^32 + 10 ticks of wrap); spinning rails never overlap (bounding spheres) over a full period; a freed rock keeps its rail ω; removed ids never reappear after save/load and compaction; event ids never collide with natural or earlier event ids; takeover leaves exactly one live population per cell; region bytes stay ≤ the budget under a synthetic year of mining and gunfire; hand digs refused outside zones and within 6 m of a heat site; splitter zones only grow from existing zones; samples are refused by every refinery; a cave detonation's shrapnel stops at rock; `Thrown` rocks hit frozen ships; `W_b` and `ThrownMax` caps hold; fog cull never drops a cell with `τ_los` < 5.5; `RingObscuration` against a brute-force 1,000-step march within 2 % |

## Changes to other pivot docs

- **README.md:**
  - Update the `rings-and-asteroids.md` row (no longer pending).
  - Decided row: "Rings and asteroids: function-defined rings (annulus, haze, impostors, bodies); asteroids packed; mining = laser heat + wedge; overheating detonates".
  - Milestones M5b and M9 (below); open questions (below).
- **planets.md:**
  - `PlanetParams` gains `RingParams? Ring`.
  - Ring shadow term in the terrain, hull, impostor and atmosphere shaders.
  - The `ChunkJob` density source becomes generic, with a base voxel per volume.
  - The detail ladder notes that rings have their own ladder and that asteroid edits follow its "edits only close up" rule.
  - The point-of-light stage adds ring albedo to body brightness.
- **frames-and-zones.md:**
  - `FrameKind.Lane` (virtual rotating lane frames) and `FrameKind.Field` (Kepler rails, no SOI, no gravity, no spin).
  - `BubbleAnchor.Source`: a bubble in a lane or field frame. In a lane frame, gravity uses the rotating-surface rule (C1, C2) with the Hill terms. Rebases are 32 m steps. Sibling lanes may merge.
  - Asteroid realisation uses a near sphere + velocity capsule instead of the generic 2 km rule.
  - Own-lane asteroids are static; other rails are moved with `MoveKinematic` every tick.
  - Coasting debris leaves Jolt for closed-form Hill motion.
  - Asteroids are not anchors, but active heat sites are.
  - Protected zones (revision 2 review): asteroid bodies do no hull damage inside `ProtectRadius`, and chunks and clouds entering it become lost mass. This belongs with whatever zone system declares protected zones.
  - Caps: 64 realised rails, 32 freed rocks, 256 asteroid-related bodies and 128 chunks per bubble.
  - The > 300 m/s analytic sweep rule.
  - Layers `AsteroidRail` and `Debris`.
  - Profiler rows.
- **ship-hulls.md:**
  - Mining function cores (table above).
  - Shrapnel and ram damage become damage brushes, so ship materials need `EnergyPerVolume` (e_hull).
  - Convex decomposition moves to `src/Physics`; `MassMoments` is shared.
  - This depends on a contact-damage rule (impulse energy → damage brush, reduced mass), which is a missing system.
- **A future combat doc** (revision 2): weapon defs carry the `asteroid` block (kind, η_k, ξ_k, `shatterCoupling`, c_w, spot, deposit depth). Its projectile sweep feeds `KineticHit`s here, and its rounds in rings and fields use `ProjectileRailSweep`. Its weapon numbers replace the placeholder classes above.
- **review.md:**
  - Risks:
    - CPU/GPU instancer parity, and peer table determinism (CRC plus fallback);
    - heat and wedge tuning needs a sandbox before rings;
    - export probes for `SetMotionType`, `ScaledShape` and `OffsetCenterOfMassShape`;
    - detonations as weapons (abuse limits);
    - awake fragment counts in Hill bubbles.
  - Add contact damage and protected zones to the missing systems.
  - Add M5b and M9.
- **exotic-matter.md (in progress)**. Its Asteroids section should defer geometry, rails, realisation and mining to this doc:
  - `AsteroidRef(belt, cell, index)` → `AsteroidId`.
  - Per-asteroid `MinorBody` frames and `BubbleAnchor.MinorBody` → lane and field frames with `BubbleAnchor.Source`. Asteroids are static in them, which keeps that doc's "never refit off the asteroid" guarantee.
  - "Bands of equal a with epicycles" → rigid lanes for rings. Belts populate fields only. Debris arcs use the lane instancer with an azimuthal window.
  - "EditStore keyed by AsteroidRef" (direct digging) → packed bodies accept only server ablation ops (user decision).
  - "Asteroids under 30 m tractored whole into an intake" → only Loose fragments. Natural rocks are Packed and jam the intake (user decision). H2 disc debris "taken whole by tractor" needs the same ruling, or an explicit Loose exception for disc debris.
  - Size range 5 m–2 km (radius) → 2–400 m (diameter) as the default, with `D_max` as a knob.
  - Kept from exotic-matter: `EmFraction` (2 %, 30 % near black holes), lens fill 1 %, q ranges, the server-only `DepositSeed` and the `AsteroidComposition` / `DepositRecord` privacy rule. Only EM is private; bulk composition is visible by colour.
  - Proposed for exotic-matter to own: EM in rock decays when heated past T_em (λ_em, e_em), and is lost as yield while adding unventable pressure.
  - Revision 2, weapons on asteroids:
    - `PulseKick`'s skip list gains rail asteroids and realised natural asteroids that are not Dynamic. An armed charge's anchor does not realise rail asteroids.
    - A breach in contact with an asteroid uses the terrain crater rule (`8 m · m_g^(1/3)`, cap 30 m) plus an `AmBlast` fracture here.
    - Proposed: the same-room EM chain needs q ≥ 0.15 (the charge `MinPurity`), so lens ore does not chain. Exotic-matter's chain has no purity floor today; until it takes this, ore chains small pulses.
    - Plasma exhaust heating of asteroids goes through this doc's heat grid, capped by `P_siteMax`.
    - Torpedo warheads on rock follow the breach rules here (contact: crater + `AmBlast`; proximity: flash and dose). The annihilation lance on rock is a heat source (`c_w` 0.02) plus a vapour bore at its own 2e10 J/m³.
    - Gravity dampers skip asteroid bodies too, by its existing rule.
- **warp.md:** the exit occupancy test adds an asteroid term: no exit within 200 m of an asteroid's bounding sphere (instancer query, ~10 µs). Fields and slabs are otherwise allowed.
- **wormholes.md:** the suppression field gains an Occupancy row (asteroids ≥ 32 m: geometric, hard within `ρ_a + r + 100 m`) and a soft ring/field density term `min(τ_local/τ_ref, 4)`.
- **Orbital UI (review missing system 7):** ring-crossing warnings on the predicted path.

**Revision 3:**
- **README.md:** decided rows: "Rings: dense and sparse bands, density shown as darkness and opacity, dust as fog (vision and sensors); mined asteroids gone forever, replenishment events with new ids, bounded deltas; EVA sampling and cave mining in fractured rock; asteroids tumble and can be thrown; gas giants host rings and can be scooped". Add a gas giant row to the docs list once planets.md takes the section. Drop rings questions 1–6 from the collected list.
- **planets.md:** a new "Gas giants" section (written separately as a proposal): `CelestialKind.GasGiant`, no voxel surface, impostor + atmosphere + cloud decks, depth/pressure model and crush depth, deterministic storms, radiation belts depleted by rings, gas scooping, no landing. `PlanetParams.Ring` applies to gas giants through their own params.
- **frames-and-zones.md:**
  - spinning own-lane rocks are kinematic with a set ω and 1 Hz resync, so lane bubbles with spinning rocks always step (~20–50 µs each);
  - `Thrown` rocks are swept against every entity, frozen ones included, like fast-transit ships;
  - gas giant bodies get a rotating band with no terrain (planets.md);
  - profiler rows for the above.
- **tractor-beams.md:**
  - its proposed protected-zone line for `Thrown` bodies is adopted here;
  - new here: `Thrown` bodies don't coast (swept until the tag ends), ≤ 8 per player and 128 per server, and a lock at the player's cap is refused;
  - the `W_b` reset's "rail velocity" for asteroids is the lane point velocity; spin is not part of the reference (a despun rock is not "back at rest" until it is re-railed, which is fine: re-railing drops the ledger);
  - a lock on a spinning kinematic rock converts it with its spin; Hold then damps spin across the beam;
  - (review) for asteroid bodies the fade uses `max(W_b, E_rail)`, with `E_rail` the kinetic energy relative to the local lane, so towing and releasing can't beat `EThrow`; the `Thrown` threshold uses the same quantity; every beam-touched rock carries `LastBeamOwner` for attribution below the threshold.
- **exotic-matter.md:**
  - event clusters carry EM lenses from `Hash(DepositSeed, serial)` at the band's `EmFraction`: the privacy rule is unchanged;
  - cave mining extracts lens ore cold (no `T_em` decay), the best EM recovery route on rock; its yield is ordinary `em` stacks (assayed);
  - (review) its item ids are used as-is: scooped hydrogen and ammonia are `propellant.hydrogen` and `propellant.ammonia`, the same items its AM factories and thrusters take;
  - gas giant products (planets.md) feed its economy: hydrogen for AM factories and AM propellant, D and He-3 for fusion, ammonia toward cryo-solvent. Mixing agent (ice + rock flux, 0.02 per kg) comes from the giant's ice-rock ring bands, which carry both halves; the water deck, 54–82 km down, is never the cheap source. No natural AM by default (its revision 2 rule); an optional belt antiproton trace knob is raised there as a question.
- **ship-hulls.md:** EVA tools are items, not function cores; `OreSack` is a Loose container body. Sealed rooms get a pressure rating for gas giant depths (planets.md).
- **review.md:** risks: the always-stepping lane bubbles (spin), cave detail collision under P1, region byte budgets, event placement never visible; missing system: a sensor doc (this doc only supplies `RingObscuration`).
- **wormholes.md:** the soft ring/field density term becomes `min((τ_local + τ_D,local)/τ_ref, 4)`, so dusty bands suppress like dense ones (proposed).
- **A future sensor doc:** consumes `RingObscuration.Transmittance` and the per-kind couplings in `data/sensors/ring-obscuration.json`.

## Milestones

- **M5b: Mining sandbox.** After M3 (PhysicsScene) and M5 (components).
  - One asteroid of each material in a zero-g test scene, inside a synthetic lane bubble (a fixed Ω, so the Hill terms are exercised). No ring, no network.
  - Laser (with throttle), wedge, imager, scanner, tractor, intake and drill via debug UI. Heat sim with substeps, fracture, detonation, fragment tiers, clouds, coasting, abuse limits.
  - Exit:
    - heat step and fracture-bake p50/p95 in the profiler;
    - the grade-3 forecast matches the simulated outcome (kind and v̄ within 20 %) for every material;
    - the closed form is within 20 % of the measured T_det in a uniform-heating test;
    - a silicate cycle takes 60–90 s with a 3 MW laser;
    - per-event mass conservation holds;
    - the user plays the loop and signs off the feel;
    - revision 2, with debug weapons (mass driver, railgun, pulse laser, particle beam, AM cell, AG charge): the measured shatter threshold is within 20 % of `E_th` for each material at 4, 16 and 64 m; recovered mass is ≥ 90 % for a held wedge cycle and 55–80 % for a shatter of the same rock; weapon heat per bus watt is ≤ 1/5 of the beam's; a particle beam detonates ice-rock; an AG pulse moves fragments and leaves rails still; the measured whole-body shatter energy is within 20 % of `E_split` at 4 and 8 m; 60 s of sustained autocannon and railgun fire from 8 debug shooters into one bubble keeps the bubble at ≤ 256 asteroid bodies and ≤ 16 clouds, and the fracture job time at ≤ `FractureJobMsPerTick`;
    - revision 3: test rocks spin (kinematic, set ω) and a lane bubble's step ms with 15 spinning rocks is logged; a lock frees a spinning rock with its spin; a throw past 50 MJ tags it, its sweep hits a frozen dummy ship, and the caps hold; an EVA character samples, enters a wedge's damage zone, digs a 10 m tunnel with the hand drill, extends it with splitters, and takes a lens out with no EM loss; cave chunk builds and memory per caving character are logged; a detonation in the cave kills inside and leaves the outside untouched; the user signs off cave feel.
- **M9: Rings and fields.** After M7 (orbit, free-fall bubbles). This absorbs exotic-matter's asteroid part of A1.
  - Ring generator, lane table with CRC, annulus, haze, gravel, impostors, ring shadow.
  - Lane bubbles, realisation, fast-transit and shrapnel sweeps, coasting.
  - Deltas and region files, fracture events and records over RakNet, belt fields, EM lenses from `DepositSeed`.
  - Exit:
    - GPU ring cost inside the densest band ≤ 0.6 ms at 1440p;
    - no pop at any stage switch, including edge-on (recorded fly-throughs at 50, 500 and 5000 m/s);
    - two players mine one asteroid and get identical fragment sets, ids and masses, with geometry within visual tolerance;
    - a mining bubble's Jolt ms and awake-body count are logged;
    - mining bytes/s logged;
    - revision 3: one ring with dense, medium and sparse bands at each dust preset reads correctly by eye at 50,000 km, 500 km and inside (user sign-off); GPU cost inside a dense choked band ≤ the dense clear band's (the fog cull pays for the dust); `RingObscuration` ranges match the table within 10 %; a gas giant ring (the widened arc field) passes the instancer parity test at 13,200 km; forced replenishment events place only outside the 20 km exclusion, swap populations only under their flash, dust shell and 30 s crossfade on recorded fly-ins, keep every spawned body and worked natural in the footprint, and their ids never collide; a scripted month of mining near a station keeps every region under budget with the compactor's ms logged.

## Rejected alternatives

- **Stored asteroids** (~5e8 per ring): rejected. Seed plus deltas only.
- **An independent Kepler orbit per asteroid:** shear drift of ~110 km a day across a lane breaks cell membership and makes rail asteroids interpenetrate. Rigid 512 m lanes instead.
- **All nearby asteroids dynamic:** they never sleep in free fall, because the tide keeps them moving. Static in their own lane bubble, kinematic elsewhere, dynamic only when disturbed, and permanently static above 1e8 kg.
- **Kinematic rails coasting at a constant velocity in a non-rotating FreeFall bubble, resynced every 10 s:** the rail path curves (Ω²Δ ≈ 3e-3 m/s² at 1 km), giving ~15 cm of error per 10 s. Spin made it worse. A lane bubble plus `MoveKinematic` per tick is exact and cheap.
- **Per-asteroid `MinorBody` frames (exotic-matter's first draft):** one frame and one bubble per rock. A lane frame makes every rock of the lane static at once.
- ~~**Spinning natural asteroids**~~ (revisions 1–2): superseded by the user's revision 3 decision that rocks tumble a little. The cost that drove the rejection (no static sleep for spinning rocks) is now paid and measured; heat sites, footing and collision rings ride the body, which turned out to need only a body-local aim point and compound sub-shapes.
- **Free torque-free tumbling on rails** (revision 3): an arbitrary tumble (spin off a principal axis) is closed-form only through elliptic functions, which are float-heavy and not integer-exact on GPU and peers. Stable spin about the max-inertia axis is exact and looks the same at ≤ 0.12 m/s.
- **Dynamic spinning rocks near players** (revision 3): they never sleep in a Hill bubble and drift off their rails. Kinematic with a set ω is exact and cheaper.
- **Regrowing cells after N days** (revision 3): the user decided mined rocks are gone forever. Replenishment events with new ids give the same supply without bringing any rock back.
- **Event clusters overlaid on live natural cells** (revision 3): two populations in one cell break the no-overlap invariant and would need rail-to-rail collision. One owner per cell instead.
- **Hand digging any packed rock** (revision 3): it would bypass the laser + wedge rule. Only damage zones from fractures, and fractured boulders, are diggable.
- **A hand laser for EVA** (revision 3): it would let crews cook rocks from inside caves, the egg rule at arm's length. EVA tools are cold.
- **Fog as a screen-space post effect only** (revision 3): the server needs the same medium for sensors and the GPU for the impostor cull, so dust lives in the ring function.
- **Density above 1** (revision 3): the 63-slot clamp would cut the mean count. Denser-looking bands use dust instead, or a higher `TauRef` per style: the haze and gravel particles are not slots, so their optical depth can rise without touching body counts (gravel cost scales with it; review note).
- **One α = 2 power law from 1 cm to 400 m:** with τ = 0.6 it implies ~100× more bodies than the instancer draws, or else almost no haze. A broken law with one shared cross-section table instead.
- **Physically real thermal constants:** conduction would reach millimetres per minute and heating would take hours. Gameplay-tuned C_v and κ, with real radiation.
- **FEM, peridynamics or pre-fractured meshes:** costly, non-deterministic across peers, and incompatible with SDF edits. Voronoi-on-SDF from one event instead.
- **Clients deriving the fragment set from shared float maths:** a borderline union-find or empty-cell test forks the body set and ids. The server sends fragment records instead.
- **Poisson counts via `exp`:** GPU and CPU float differences would make impostors disagree with bodies. Fixed-point stratified counts instead.
- **Sizing the detonation region by energy:** bigger blasts would give slower fragments. Detonations use the hot zone as the region, so stored energy shows up as speed.
- **Hull brick bodies for asteroids:** asteroids are a function plus ops, like planets, so they reuse the planet chunk path. Hull bricks need brush lists that asteroids do not have.
- **A fixed kinetic threshold for every rock** (revision 2): 2 m pebbles and 300 m rocks would need the same gun. The threshold scales with D² and toughness instead.
- **Kinetic hits that fracture through the wedge path** (seat test plus blade): weapons would make clean, controlled splits as good as mining. Shatter has no blade, more fines and faster pieces.
- **Energy weapons with their own damage model on rock:** two heat models would disagree about S, and weapons could not set off the egg. One grid with a coupling factor instead.
- **AG pulses moving rail asteroids:** thousands of conversions per charge, rails rewritten and the no-overlap invariant broken. Rails are treated as terrain.
- **Crater mass as recoverable grit:** small arms would sandblast rock into the intake and bypass the packed rule. 90 % of a crater is lost.
- **Stacking crack factors** (revision 2 review): 0.4 per record over 8 records is a 0.4⁸ ≈ 7e-4 factor, so autocannon fire would first crack and then shatter any rock. Records do not stack; the cracked floor is 0.4·E_th.
- **Fragments that keep all their energy in the parent's frame with a free recoil:** a dynamic parent's recoil came on top of `E_kin`, so one shot could throw a remainder boulder as a second, uncapped ram. The budget is now in the centre-of-mass frame.
- **Weapons fracturing Loose chunks:** every shot would turn one body into up to 20. Chunks become cloud mass instead.

## Open questions for the user

Revision 3: the six earlier questions on regrowth, bystanders, EVA mining, ring density, ring hosts and tumbling are answered and integrated. The rest are renumbered (old 7–13 are now 1, 2, 5, 4, 6, 7 and 8), and new ones follow from the answers.

1. **Crater yield (revision 2):** should sub-threshold kinetic craters give any recoverable grit (proposed 10 %), or none?
2. **AG pulses in rings:** is "rails are terrain" acceptable (ships kicked into ring rocks take ram damage), or should pulses in a slab also kick realised small naturals, up to the 32 freed-rock cap? Revision 3 makes most realised rocks kinematic (spinning), which the skip list already covers.
3. **Cave miners and other parties (revision 3):** a rock with people in its caves can still be heated, wedged or shot by anyone outside protected zones; the hazard beacon and attribution are the only protection. Keep that, or refuse other parties' heat sites and wedges within ~30 m of an EVA character inside the rock (which lets a single caver "claim" a rock by presence)?
4. **Hot kicked bombs:** a hot dynamic boulder kicked by an AG pulse is a moving bomb. Is gradient venting (proposed) enough, or should a pulse detonate or quench hot sites?
5. **Weapon numbers:** the projectile classes and weapon couplings here are placeholders for a combat doc. Who owns weapon stats, and is a 2.25 GJ capital kinetic weapon in scope?
6. **Black-hole lens chains:** near black holes, lens ore reaches q 0.3, above the 0.15 chain floor, so an AM breach on such a lens chains an AG pulse. Keep that as a rare hazard (proposed), or exclude ore entirely?
7. **What "shatter" means at the threshold (revision 2 review):** as written, a hit at `E_th` breaks out a region of radius D/8 round the hit (~1 % of the rock) into 8–16 pieces, and only `E_split` (18× `E_th` for a 4 m rock, ~41× from 6 m up) breaks the whole rock; under `E_cap` no single hit breaks a whole silicate rock above ~46 m. Is that the intended reading of "the hit shatters it", or should the threshold itself be the whole-body energy? Then no single hit could shatter silicate above ~46 m at all, and a railgun would only shatter silicate up to ~13 m.
8. **Debris near stations (revision 2 review):** is "asteroid debris does no hull damage inside protected zones" acceptable as a game rule, or should stations defend themselves (point defence, shields) instead? Revision 3's bystander damage makes this rule the only thing standing between a thrown rock and a docked ship.
9. **Replenishment events (revision 3):** proposed public (announced to the whole system when they happen, a gold rush) but unpredictable (server-only seed), at ~1 collision a day per ring, 1 comet a week and 1 moonlet a month. Is that the cadence and visibility you want, or should events be hidden until scanned, or tied to something players do (for example heavy mining raising the collision rate)?
10. **Leftovers past the region budget (revision 3):** to keep storage bounded, a region over 16 MB removes its oldest small re-railed boulders that nobody has touched for 30 days, and small-arms craters past 4096 cratered rocks in a region stop being saved. Both lose only leftovers and cosmetics, never untouched rock. Acceptable, or should the budget be larger and the server warn instead?
11. **Sensors (revision 3):** no sensor doc exists. This doc defines how rings and dust cut each sensor kind (optics blinded by dust, radar mostly not, signature sensors not at all). Who owns detection ranges and the sensor kinds, and should hiding in a choked band be a real stealth tactic as proposed?
12. **Spin numbers (revision 3):** "tumble a little" is taken as 70 % of rocks spinning, rim speeds ≤ ~0.12 m/s (a 4 m rock turns in 5–30 min, a 400 m one in 3–12 h). More spin costs nothing on rails but more spinning rocks keep more lane bubbles stepping. Right feel?
13. **Cave vehicles (revision 3):** caves are EVA only (hand drill, splitters, ore sacks). Is a small tunnelling vehicle or a ship-mounted cave drill wanted later? It would need the same damage-zone rule and a rate that keeps the wedge cycle the bulk path.

These are dependencies, not questions: the surface gravity decision (README Q1) scales lane speeds and shear by sqrt(g) (and the gas giant's numbers in planets.md); the EM heat behaviour waits for exotic-matter.md; detection ranges wait for a sensor doc.

## Review notes

User decisions, checked:
- Meshes far → haze → asteroids: present. The annulus is now a thin mesh with rims, so it does not vanish edge-on, and the haze switch also triggers on projected thickness.
- Packed, cannot be dug directly, must be cut or broken by laser, may break violently: present. Tightened so that every natural asteroid, including 2–4 m rocks, is Packed and refused by intakes, since otherwise 75 % of all asteroids bypass the rule. Severing a piece by laser ablation alone is now explicitly allowed as the user's "cut open with a beam laser" path.
- Microwave egg (too much energy detonates): present (S ≥ 1 → detonation).
- Laser warms, then a wedge breaks it: present. The laser gained a throttle, so players can hold the window.

Contradictions fixed:
- **exotic-matter.md:**
  - EM placement came from the public hash, which violates its server-only `DepositSeed` privacy. EM lenses are now server-only, and only bulk composition is public.
  - EM pockets (0–3 spheres, f_em 0.05, ExoticOre ρ 4000) did not match its 2 % EM-bearing, 1 % lens fill, q 0.003–0.03 and ρ 2800. Aligned.
  - Its asteroid section (`AsteroidRef`, `MinorBody` frames, direct-dig `EditStore`, whole intake of < 30 m rocks, 5 m–2 km sizes) conflicts with this doc and with the user's packed rule. Listed under "Changes to other pivot docs".
  - EM heat decay is marked as a proposal for exotic-matter to own.
- **frames-and-zones.md:**
  - "Asteroid kinematic with constant velocity, resync every 10 s, error ≤ mm" was wrong in a non-rotating free-fall bubble: ~15 cm per 10 s from rail curvature, more with spin. Replaced with a lane bubble (Hill frame, the C1/C2 rotating rule): own-lane rocks are static, and other rails use `MoveKinematic` every tick.
  - Field and lane bubbles generalise exotic-matter's `MinorBody` anchor.
  - The `Asteroid` layer did not collide with other asteroids, so freed rocks passed through rail rocks. Split into `AsteroidRail` (no self-collision) versus Moving/Debris.
- **planets.md** "edits only close up": made explicit. Impostors never show edits, and heavily cut naturals convert to spawned bodies so the far view stays consistent.
  - Ablation ops under one base voxel did nothing on 1 m-voxel bodies, so the op radius is now ≥ BaseVoxel.
- **ship-hulls.md / review.md:**
  - Large-body collision at D/16 convex cells (25 m on a 400 m rock) cannot support magbooted characters. Added the planets' LOD-0 chunk collision ring near characters, which inherits P1.
  - Fast-transit ship damage now uses the reduced mass, not `½ m_rock v²`.

Determinism:
- Fragment existence, ids, masses and velocities were derived on clients from float maths (union-find, empty cells, v̄). X3 forbids relying on that. The server now sends `FragmentRecord`s, and clients bake geometry for visuals only.
- `FragmentMass` was mis-sized (16 B, not 18). Half inertia overflows above ~6.5e4 kg·m², and the diagonal-only inertia had no principal axes. Replaced by a ~46 B record with float principal moments, axes, CoM at 1/64 m and velocities.
- Ring tables (`CountFx`, D quantiles, z quantiles, resonance factors) were built with transcendental float functions, so peers could disagree on existence. They now use only basic operations and sqrt, constant tables and a CRC handshake with a download fallback.
- The count formula had unbalanced parentheses. The clump multiplier now has mean 1, so τ is preserved.
- Overlap rejection against larger classes was recursive (it needed survivors). It now tests candidates. Integer tests use 1/16 m int32, so the GPU needs no int64.
- Arc cells did not nest across classes (circumference is not a multiple of L_c). Counts are now multiples of 16.

Precision:
- Impostor arc maths could pass a 1.4e7 m arc through float. The camera's cell index is now uploaded as an int, with the fraction as a float.
- `AsteroidId` fields overflowed: field cells needed ±390 cells against 8 bits, and belts hold 2000 fields against 10 bits. The id is re-laid out.
- Gravel took one lane's frame and so did not shear. It now uses per-lane phase.
- The "170 km/day" shear figure is corrected to ~56–110 km/day. Asteroid gravity is corrected to ~1.5e-4 m/s².
- The accepted-error note now includes the radial tide and Coriolis.

Physics and numbers:
- **Size distribution:** τ = 0.6 with one α = 2 law implies ~2400 bodies ≥ 2 m per column, not 48 (bodies give τ ≈ 0.006). Changed to a broken law: particles set τ and bodies set counts. The crossfade now uses one cross-section table, and gravel uses the same `D > s/300` cut, which also bounds it to ~60k points.
- **Realised body count:** "~50 per anchor" was really ~280 with the 100·D, 2 km ranges. Replaced by a near sphere + velocity capsule: ~5–15 per anchor, ~1 realisation/s at 100 m/s.
- **Impostor count:** "3.5k at peak" was ~14k at 500·D (each class adds ~1.7k once its range exceeds the slab). Range is now 300·D_hi (≥ 2.3 px): ~5–6k instances, ~35k cells. The GPU estimate is raised to 0.4–0.7 ms against the 0.6 ms exit, with a named knob.
- **Heat sim:**
  - Explicit conduction was unstable for metal at dx = 0.375 m (κΔt/dx² = 0.28).
  - One step could skip a whole carbonaceous window.
  - Added substeps (≤ 8), with costs updated.
- **Volatiles:** bodies with T_bg ≥ T_vap are now outgassed, where before they would have detonated on the first step.
- **State machine:** Critical at S ≥ 0.6 made the silicate window 18 K wide, and carbonaceous (S = 0.85 at seat) skipped Window entirely. Thresholds are now knobs (0.9 / 0.85), with the window per material listed. Wedges still seat in Critical.
- **M5b exit:** "closed form within 10 %" would fail, because the sim uses the hot zone's mean T. The exit now checks forecast against sim, and the closed form within 20 % in a uniform test.
- **Rest-stability:** fragments in a Hill bubble never sleep. Added coasting debris (closed-form Clohessy–Wiltshire outside 500 m of players), a 32 cap on contact-freed rocks, and a measured row for awake bodies.
- **Re-railing** could break the no-overlap invariant (lane straddling, overlap with rails or spawned bodies). Added snap, overlap tests, and continued coasting on failure.
- **Remainders** from union-find were unbounded. Capped at 4 bodies; the rest become chunks or cloud.
- A hot site with everyone gone had no physics to resolve in. Active heat sites are now anchors.

Exploits:
- Added the "Abuse limits" section: protected zones, no tractoring of hot bodies, per-event `E_cap`, shrapnel speed 1.5 km/s (was 3 km/s, up to 22 MJ a shard), owner attribution, per-player and per-bubble rate limits with a fair queue, persistence caps, and a cap on ramming rocks free.
- The shrapnel sweep now covers every entity within reach, not only the bubble's, since 1.5 s at km/s outruns a 2 km bubble.
- The GPU removal set was per-id, 64k slots, and would overflow near busy stations. It is now cell-keyed with nearest-first fill, and the overflow is visible.
- The laser and wedge as anti-ship weapons are flagged as a combat-rules question.

Gaps filled:
- Laser throttle.
- Laser trace at 10 Hz instead of every tick.
- Spawned-body impostors.
- Ring-crossing HUD warning.
- Moon orbits kept outside the ring.
- Edge-on annulus.
- Remnant debris arcs on the lane instancer.
- Tractor despin.
- Per-material mass for yields and an EM `em` stack from the intake.
- Mass-conservation assert and tests.
- New open question on tumbling rocks (tidal locking).

## Revision 2 notes

User decisions implemented (2026-11-25, not re-litigated; every earlier decision still holds):
- **Big enough kinetic weapons shatter regardless of the heat window:** the threshold `E_th = G_eff·F(N_sh0)·R_sh²/η_k`, with `R_sh = max(R_min, ζ_sh·D/2)`, scales with size (∝ D²) and toughness (G0, cracks, heat). Worked thresholds for 4–400 m and six projectile classes are tabulated.
- **Less controlled and more wasteful than laser + wedge:** no blade, more and smaller pieces (N 8–16), `(m̄/m)^(1/3)` velocities, 15–40 % of the region crushed to fines with half lost at once, a faster cloud, cracked boulders, and 45 % of projectile energy lost outright. Estimated recovery 55–80 % against ~90–95 % for a held window (an M5b exit check).
- **Energy weapons feed the same heat model inefficiently:** `c_w` 0.1–0.3 against the mining beam's 1, tight spots that waste energy on ablation, ~8× less absorbed heat per bus watt. They can still push volatile-rich rock to detonation, and every abuse limit now covers weapons.
- **Exotic-matter weapons:** contact AM breaches become a crater plus an AmBlast fracture (with exotic-matter's 30 m terrain crater cap); standoff breaches only flash-heat and dose. AG pulses skip rail asteroids like terrain, kick dynamic fragments within the shared kick budget, and never fracture, edit or heat rock.

Changes to earlier parts of this doc:
- `FractureKind.Impact` is replaced by `Shatter` (rams and projectiles alike) and `AmBlast` is added. `FractureEvent` stays 56 B: for Shatter and AmBlast, `BladeOct` carries the shot axis.
- **Fast-transit rams** used `E_crack = 0.5 · ½μv²` and always fractured. They now take the kinetic rule (η_k 0.25, ξ_k 0.3) and only fracture above `E_th`; below it they crater and crack. A 30 m ship at 300 m/s still shatters most rocks it hits.
- **Cooling** starts after 2 s with no heat input of any kind, not only no beam.
- **The mass check** gains a `Lost` bucket per material (fines lost, crater vapour), so fragments + remainders + grit + ablated + lost = parent.
- **New caps and knobs:** `P_siteMax`, `E_open`, weapon sites per player, `ShatterVMax`, `RailTestMinEnergy`, the projectile sweep budget, ζ_sh, N_sh0, η_k, ξ_k, η_c, the fines curve and `FinesLost`, η_am, ξ_am. (The review adds the `E_open` window, the bulk cap, 128 weapon sites server-wide, the fracture queue and job budget, the bubble body budget and the protected-zone debris rule.)
- **The rate limit now covers all fracture sources**, and the over-limit fallback (crater + crack) is defined, so a weapon hit is never refused outright.

Consistency checks made:
- **Wedges never take the shatter path.** Wedge.Heavy carries 1.9 MJ, above the 0.30 MJ threshold of a 4 m ice-rock, so without the `ShatterCoupling = 0` rule the tool would bypass the window it exists to enforce.
- **Small arms cannot mine:** an autocannon round (0.29 MJ) is under every cold, uncracked threshold, and 90 % of crater mass is lost. (Corrected in review: after cracking hits it can shatter small ice-rock and carbonaceous rocks, whose cracked floors are 0.12 and 0.18 MJ. That is wasteful and rate-limited, so it is still not mining.)
- **E_cap and the largest rocks:** without the cap check, a 2.25 GJ kill vehicle would split a 400 m rock. With it, a single hit shatters a region only up to `D ≤ 8·R_max` (294 m silicate), and a whole rock only up to ~46 m silicate (added in review); bigger rocks are mined or worn down.
- **Kicking the ring:** a pulse's physics anchor would have realised every rail asteroid in 2–4.6 km (up to ~1e4). Rails are on the skip list and anchors do not realise them.
- **Ore EM and AM:** exotic-matter's same-room chain would have turned any breach on an EM asteroid into an AG pulse. This doc proposes that it needs q ≥ 0.15, the charge `MinPurity`, which almost no lens ore reaches; exotic-matter has to accept the floor (corrected in review: it is a proposal, not yet a rule there).
- **Plasma exhaust:** a heavy thruster's ~30 GW jet at even 1 % coupling would detonate any rock in a tick. `P_siteMax` bounds it, and so bounds every other weapon.
- **Projectile sweeps against rails were a new per-tick cost.** They are bounded per round, by a 10 Hz look-ahead for low-energy rounds, and by a server budget.

Still open (added as questions 7–11): crater yield, AG pulses on rails, combat ownership of weapon numbers, the black-hole lens AM chain, and gradient venting of hot kicked bodies.

## Revision 2 review notes

Lead review of revision 2, 2026-11-25. The user decisions were not re-litigated. Every correction below has been made in place above.

User decisions, checked:
- **Kinetic shatter regardless of heat: present and faithful.** The threshold needs no site (a cold rock works at the full `E_th`), heat and cracks only lower it, and wedges are kept off the path. One faithfulness gap was fixed: at `E_th` a "shatter" breaks out only the D/8 region round the hit (~1 % of the rock), which a player could read as a big crater. The doc now names the two outcomes (Shatter region, Shatter whole body), adds an `E_split` table and forecast field, and asks the user (Q12) whether the threshold should mean whole-body. The only exceptions to "big enough always shatters" are the abuse limits (protected zones, `E_cap`, rate limit, queue, bubble saturation), and each degrades to crater + crack, so the hit is never refused.
- **Less controlled and more wasteful: present.** No blade, N 8–16, `(m̄/m)^(1/3)` speeds, 15–40 % fines with half lost, 45 % of projectile energy lost. An overkill example was added (a railgun on an 8 m rock: v̄ ~25 m/s, fines at the 0.40 cap), the most wasteful case.
- **Energy weapons inefficient but able to heat: present, but in practice they could not.** `E_open` was "1 MJ in one 0.1 s heat step", which is 10 MW absorbed and equal to `P_siteMax`. A pulsed laser would need ~200 MW of bus power to open a site, so the "particle beam detonates ice-rock" check could not pass with ship-scale weapons. `E_open` is now 1 MJ within 1 s (~1 MW absorbed, ~17–21 MW of bus for the listed weapons). The ≤ 1/5-of-beam efficiency per bus watt is unchanged.

Formula and numbers:
- **Threshold table: verified.** `F(10)` = 16.858, `E_th = 67.4·G·R_sh²`. All 4–256 m cells, the `R_max` values (61 / 49.7 / 36.7 / 22.2 m) and the `D ≤ 8·R_max` limits (487 / 398 / 294 / 178 m) check out, as do the mass driver (42 / 25 m), railgun (83 / 50 m), kill vehicle and ram (254 / 154 m) reach columns.
- **`G_eff` applied the crack factor twice** (`G(T)` already carries `1 − 0.6·crack`, and the formula multiplied by it again). Fixed: applied once.
- **Crack stacking:** "every record applies the 0.4 factor" read as multiplicative, giving 0.4⁸ ≈ 7e-4 over 8 records, so autocannon fire could crack and then shatter anything. Fixed: records do not stack, the cracked floor is 0.4·E_th, and hits under it never shatter cold rock. The "eventually shatters" claim is now quantified (≥ ~0.67·E_th: second hit; 0.4–0.67: several records).
- **Site terms in the threshold test** were ambiguous (the test used `η_k·E_p`, while the state machine added `U_th + γ·Q_rel`). Fixed: one `E_crack` with the site terms inside `E_cap`, used for the test and the fracture.
- **Kill-vehicle example was wrong.** From an entry point on the surface, `R_reach` is the far side (~51 m on a 64 m rock), not the radius, so `R_f` ≈ 35 m at `E_cap` is a spall of about half the rock, not "the whole body splits into 16 pieces". The 2.2e8 kg figure was not the body's mass either way. Rewritten, with whole-body limits under `E_cap` (~46 m silicate, ~28 m metal).
- **v̄ in the examples** was taken over the whole region, fines included. Now taken over the pieces: the mass driver goes 5.9 → ~7 m/s, the railgun 3.9 → ~4.3 m/s. The yield table's v̄ range was updated to match.
- **Crater rate:** 0.36 L per autocannon round makes ~500 rounds per 0.35 m op, not 300. Larger ops on bigger rocks were added.
- **Autocannon claims:** "under every threshold" holds only for cold, uncracked rock. With the 0.4 floor it can still shatter small cracked ice-rock and carbonaceous (floors 0.12 and 0.18 MJ). Stated in the table and the Revision 2 notes.
- **`RailTestMinEnergy` "cannot shatter any rock"** was false: 1 MJ is above cold 2–6 m ice-rock (0.30), carbonaceous (0.45) and silicate (0.83) thresholds. It is now only a cost class.

Determinism (server-side fracture summaries):
- Kinetic, AmBlast and weapon-heat outcomes stay server-only, sent as `FractureEvent` + `FragmentRecord`s. Clients never derive the threshold. Confirmed.
- **The fracture queue had no defined order**, which the run-to-run determinism test needs. It is now ordered by (tick, AM breaches by `TrapId`, kinetic hits by shooter and round), round-robin by bubble id.
- **The 10 Hz rail test for low-energy rounds** could find a rail hit after the per-tick combat sweep had already let the round hit a ship behind the rock. It is now an exact look-ahead (rounds fly straight and rails are a function of the tick) that truncates the round's life.
- **Two hits on one body while an event is queued** would each resolve "with original inputs" against a body the first had already changed. Now there is one pending event per body; later hits take the crater path, in order.
- **Shatters on unrealised rail rocks** (long shots) had nowhere to put dynamic pieces. They are now born Coasting when no player is within 500 m.

Exploits:
- **Rocks as kinetic weapons:**
  - A dynamic parent's recoil came on top of `E_kin` (momentum was conserved, but its energy was uncounted). One shot could throw a remainder boulder at tens of m/s, an uncapped ram. Fixed: the energy is shared in the centre-of-mass frame, and the recoil is capped at `ShatterVMax`.
  - Fragments at up to 80 m/s (or 100 m/s from an AG kick) could be shattered or kicked from just outside `ProtectRadius` into a station. Added: asteroid debris does no hull damage inside protected zones, and chunks and clouds entering one become lost mass (open question 13).
  - No energy gain from shattering, which was implied before, is now explicit: pieces get ≤ 30 % of the round's energy plus stored site energy.
- **Fragment spam via gunfire:**
  - Per-player limits alone did not bound a bubble. Added a per-bubble budget (256 asteroid bodies: the smallest pieces fold into the cloud, then saturation degrades to crater + crack), a cloud overflow merge (16 per bubble), and the chunk rule: weapons turn Loose chunks into cloud mass instead of fracturing them.
  - Weapon-opened sites could take all 256 server sites. Capped at 128.
- **Silent heating:** sub-`E_open` weapon heat went to the bulk node, which has no volatiles and no S, and has a tiny heat capacity with the game's C_v (a 4 m rock holds ~7e4 J/K). Fire could warm small bodies past `T_vap` outside the egg rule, or weaken them to cut the shatter threshold. Weapon heat into the bulk node is now capped at `max(T_bg, min(T_soft, T_vap))`.
- **Laser plus gun instead of a wedge** is allowed and intended: it shatters easily, but wastefully, so the wedge still pays.

Performance (fragment caps under sustained gunfire):
- **"≤ 4 events per tick" was not a sustainable bound:** 4 events of up to 20 bakes at 0.5–3 ms is up to 240 ms of worker time a tick. Added `FractureJobMsPerTick` (4 ms) and a 64-event queue that falls back to crater + crack. The per-player limit keeps 64 players at ~6.4 events/s.
- The projectile rail sweep (1–2 ms at its 512-round budget) is now stated to run as a job. The plasma-exhaust heat test (10 Hz, realised bodies only) got a cost row.
- Persistence under gunfire was not costed. Fragment records are now kept only while a persisted body references them; worst case ~30 MB a day of 56 B events at the rate limit, tied to the regrowth question.
- New profiler rows (degraded shatters, chunk-to-cloud, look-ahead truncations, bulk-cap losses, fracture job ms, suppressed debris contacts) and an M5b sustained-fire exit check.

Consistency with exotic-matter.md:
- **"Exotic-matter has no AM warhead" was wrong.** It has torpedo warheads (1 / 5 / 20 g; detonation is a breach) and the annihilation lance. Warheads now map to the contact and standoff breach rules. The lance is a heat source (`c_w` 0.02, so `P_siteMax` binds) plus a vapour bore at its own 2e10 J/m³, with a weapon-def example.
- **The q ≥ 0.15 chain floor** was presented as exotic-matter's rule. Its chain has no purity floor; this is now marked as a proposal, with what happens until it is accepted (small pulses, 0.05 kg per gram of AM).
- Breach crater: the sphere is now centred on the trap and intersected with the rock, and the 20 g warhead (22 m) was added. Exotic-matter's crater is not rate-limited; only the AmBlast is, and an over-limit AmBlast keeps the crater only. `E_kin` saturates at 2.5 g, not 5 g.
- The AG skip list now names re-railed spawned bodies and permanently static big bodies, not only naturals. Damper coverage and the 100 m/s kick cap are carried over. The state machine no longer says pulses never change heat state, since the hot-kick vent does.
- The standoff flash used a flat 1e-3 of the breach energy at any distance. It is now the body's share of the gamma, `0.5·E_AM·r_b²/(4d²)`, which the 1 MJ per-site cap binds near any breach.
- Unchanged and consistent: `AmEnergy` 1e15 J/kg, the `8 m·m_g^(1/3)` brush with the 30 m terrain cap, breaches in tick step 3 in `(tick, TrapId)` order, cascades one hop per tick, `PulseKick` with the integer kick budget, no terrain edits from pulses, and an armed charge's anchor `max(2 km, R + 200 m)`.

Not changed, flagged:
- Particle beams use the laser α as a tuning factor (noted in the table).
- Rounds past the 512 rail-sweep budget pass through rail rocks (accepted error).
- New open questions: Q12 (what "shatter" means at the threshold) and Q13 (debris near stations).

## Revision 3 notes

User answers implemented (2026-11-25, not re-litigated; every earlier decision still holds):
1. **Mined asteroids are gone forever; belts are effectively infinite; events bring new asteroids with new ids; deltas compact.** `RemovedSlots` bits never clear and ids are never reissued. Replenishment events (collision, comet breakup, moonlet shattering, field collision) take over a footprint of cells with a cluster whose ids carry a monotonic 30-bit serial under a reserved source (8191), so they can't collide with natural or earlier ids. Exactly one population is live per cell. Compaction: mined-out bitmaps, a dense switch per region, crater-op merging and ephemeral cosmetic craters past a cap, the existing spawned-body cap, and a 16 MB byte budget per region that removes only old, untouched leftovers. Storage grows with the number of regions worked, not with time. "Deltas and persistence" is rewritten.
2. **Bystanders take damage from others' shrapnel and fragments.** Outside protected zones, every piece and `Thrown` rock damages any ship or EVA character, attributed to the event's owner. Protected-zone rules are unchanged. A public hazard beacon (5 km, 2 Hz) announces Critical sites, queued fractures, armed contact traps on rock and thrown rocks.
3. **Handheld EVA mining: sampling and cave mining.** Hand scanner, chip and core samplers (composition and EM readings without mining: samples can't be refined), a cold hand drill, splitter charges and ore sacks. Decision on diggability: every fracture leaves a damage zone (1.5 × R_f) and fractured boulders are diggable throughout; untouched packed rock never is, so caves start only where a ship has cracked the rock and grow with splitters. Caves refine to 0.25 m voxels near EVA characters, with a 16 m collision ring. Cold digging makes caves the best route to EM lenses (no heat decay).
4. **Dense and sparse bands; density shown as colour and visibility; dust as fog.** Per band `ρ_b` (0.02–1) scales body count, optical depth and darkness together; `D_b` (0–1) adds a dust channel with its own scale height and forward-scattering phase. Hue = composition, darkness and opacity = density, milkiness = dust. Dust and particles reduce sensor range through a public line-of-sight integral (`RingObscuration`), per sensor kind, and the same integral culls impostors hidden by fog.
5. **Gas giants host rings and can be scooped.** Rings on gas giants (wider, denser, dustier) need the arc field widened into the id's spare 12 bits and rings over 16k lanes split into sources; placement keeps every ring outside the giant's rotating band; giants' radiation belts are depleted across their rings. The body type, scooping yields and hazards are proposed for planets.md ("Gas giants", a separate file).
6. **Asteroids tumble, can be moved by tractors and used as weapons.** Spin is an integer phase about the max-inertia axis (70 % of rocks, rim ≤ ~0.12 m/s): exact, free on rails and impostors. Realised spinning rocks are kinematic (own lane: set ω, 1 Hz resync; other lanes: per-tick `MoveKinematic`). A lock frees a rock under 1e8 kg with its spin; tractor-beams.md's `BeamVMax` (100 m/s) and `W_b ≤ EThrow` (500 MJ = `E_cap`) cap throws. Throws over 50 MJ are `Thrown`: swept against every entity like a fast ship (frozen ships included), rams attributed, ≤ 8 per player and 128 per server.

Changes to earlier parts of this doc:
- Natural rocks are no longer tidally locked; the revision 1 rejection of spinning rocks is marked superseded. Re-railed bodies keep a snapped spin (3 B in `SpawnedBody`).
- The kinds table gains "spinning, own lane: kinematic". Lane bubbles with spinning rocks now always step (~20–50 µs each), the main new per-tick cost.
- `AsteroidId`: rings use `c` as the arc's high 12 bits; source 8191 is reserved for event clusters.
- `Integrity` gains `Fractured`; `VoxelBody` gains damage zones and a `Thrown` tag; `CellDelta` gains `Owner`; new `RingEventRecord`, `DamageZone`, `HazardBeacon`, `RingObscuration`, `RingEvents`.
- The ring function gains `τ_D` and `column(r)` scaled by `ρ_b`; the annulus and haze gain the dust channel and the density darkening; the enumerator gains the fog cull, mined-out bitmaps and event footprints; the GPU partial-removal entry grows to 24 B.
- The tractor row points to tractor-beams.md (class S, Hold default, caps), and the beam laser aims at a body-local point.
- The "Abuse limits" intro no longer defers to an open question; "Bystanders" and "Thrown rocks and EVA" blocks are added.
- Open questions: old 1–6 removed; old 7–13 renumbered to 1, 2, 5, 4, 6, 7, 8; new 3 and 9–13 added. (Older review notes keep their old numbers.)

Consistency checks made:
- **Slot clamp and density:** `ρ_b` is capped at 1 because the class-0 expectation (36 at ρ_b = 1, ×1.75 clump) already reaches the 63-slot clamp. The densest band is the revision 2 default, so every GPU and realisation estimate is the dense case and sparse bands only cost less.
- **Determinism:** `ρ_b` enters `CountFx` as fixed point; spin is an integer `uint` phase that the GPU reproduces with 32-bit wrapping maths; event content uses the same integer instancer with a recorded seed. Dust and fog never affect existence.
- **No-overlap with spin:** the fit and overlap tests already use bounding spheres; archetypes are now stated to be normalised to the unit sphere, so any orientation is safe.
- **Tractor caps:** `EThrow` = `E_cap` = 500 MJ, so a thrown rock's ram can't exceed what one fracture event may release (once the review's rail-relative cap `max(W_b, E_rail)` closes tow-and-release); the `Thrown` sweep closes the gap where coasting debris (no collision) could carry a thrown rock through a frozen ship. A 1e8 kg rock can be pushed to ~3 m/s at most; above that mass rocks are grapple points.
- **Exotic-matter:** AM stays industrial-only (its revision 2 rule); no natural AM from rings or giants by default. Event clusters take EM lenses from the server-only `DepositSeed`, so the privacy rule holds. Cave mining extracts lenses cold, which matches its `T_em` decay rule rather than bypassing it.
- **Frames:** gas giant rings stay in BodyInertial (inner radius ≥ R + 1.3 × atmosphere top + 10 km, ≥ 1.15 R), so no ring ever sits in a rotating band.
- **Packed rule:** no hand tool removes packed rock outside a fracture's damage zone; samples remove ≤ 0.2 L and can't be refined; splitters need a hole, and a hole needs a zone.
- **Bounded storage:** every persisted record type now has a count cap and a per-record size cap, and the region byte budget bounds the sum.

Still open (questions 3 and 9–13): cave miners' protection, event cadence and visibility, the leftover budget rule, sensor ownership, spin numbers, and cave vehicles. The gas giant's own questions are in planets.md's proposed section.

## Revision 3 review notes

Lead review of revision 3, 2026-11-25, against tractor-beams.md, frames-and-zones.md, exotic-matter.md, planets.md and the proposed gas-giants section. The user's six answers were not re-litigated. Every fix below is made in place above, marked "review fix" where it changes a rule.

User answers, checked:
1. **Gone forever, effectively infinite, new ids, bounded storage: present and faithful.** Bits never clear, event ids carry a monotonic serial under the reserved source, and one population is live per cell. Three faithfulness gaps were fixed:
   - "~45 years to take 1 %" was 100× off: 1–3e4 naturals a day takes 1 % of the rocky ring in 5–17 months and all of it in 45–140 years. Still effectively infinite. Belts are now quantified too (1e8–1e9 bodies per belt; small fields can run out).
   - Storage was not bounded as claimed. Takeover wrote `Owner` into every footprint cell (up to ~50 KB per event, growing with time even where nobody mines), `RingEventLog` was kept forever, chip-sample marks on untouched naturals had no cap, and the per-type caps allowed ~84 MB per region against the 16 MB budget, with no fallback when no 30-day leftover exists. Now ownership is derived from footprints, superseded event records are dropped, sample marks count as cosmetic craters, and the compactor has a 20 MB hard ceiling.
   - The takeover erased players' work. It superseded worked naturals and caves, and the exclusion that was meant to prevent that (no spawned body or damage zone within 20 km) made nearly every mined window ineligible, since mined windows always hold re-railed leftovers. Now spawned bodies and worked naturals survive, and overlapping event slots are pre-removed once at takeover.
2. **Bystander damage: present and faithful.** Protected-zone rules are unchanged. The gap was attribution: only `Thrown` rocks (> 50 MJ) were attributed, so many 49 MJ throws were anonymous. Every beam-touched rock now carries `LastBeamOwner`.
3. **EVA sampling and cave mining: present and faithful.** The damage-zone rule keeps the user's packed-rock and wedge decisions intact. Fixes:
   - At the 64-zone cap, behaviour was undefined. Merging zones into bounding spheres would have grown the diggable volume for free, so splitters are refused at the cap instead.
   - `sample` items are excluded from every material path, not only refineries.
   - Caving memory (2–10 MB per character on the server) had no cap. It now has `CaveDetailMax` 32.
4. **Dense/sparse bands, colour and visibility, dust fog: present and faithful.** Every visibility, sensor-range and hit-odds number checks out (σ, `3/σ`, Lambert W ranges, 15 % per dense crossing). Note for the user's sign-off: "opaque" means ~45 % face-on at τ = 0.6 (fully opaque edge-on), plus the darkening. The rejection of ρ_b > 1 now says that particle τ (`TauRef`) can rise without touching body slots, if dense bands should look thicker.
5. **Gas giants: present.** Placement keeps rings in `BodyInertial`: R + 1.3 × 700 km + 10 km = 1.153 R < 1.2 R, matching frames-and-zones' `RotatingTop` and 1.3× leave rule. The ring numbers check (7.7/5.7 km/s, 98 min/4.1 h, 0.82/0.33 m/s shear, 11,719 lanes, 162k cells needing the widened arc field, Roche 3.7/1.9 R), except the body count, now ~2e10. Yields and hazards are reviewed in gas-giants.md's notes. One fix: water from the giant is not an economic mixing-agent source, because ring ice-rock carries both halves of the agent.
6. **Tumbling, tractors, weapons: present, with one real exploit fixed.** `W_b` is emitter-relative, so tow-and-release (hold a rock, accelerate the tug, let go) gave untagged, uncapped rams, e.g. 20 GJ from 1e6 kg at 200 m/s. That contradicted "every rock weapon is bounded by `E_cap`/`EThrow`". Asteroid bodies now fade on `max(W_b, E_rail)`, the energy relative to the lane, and `Thrown` uses the same quantity. This is proposed to tractor-beams.md (Changes).
   - Also fixed: the `Thrown` tag timing out at 120 s reopened the coasting-through-parked-ships gap, which is closed by a 1 Hz look-ahead until 5 m/s.
   - Also fixed: rock-on-rock hits gave both rocks the full `½μv²` (2 × 55 % of the energy). They now get half each, so the example splits up to ~11 m whole, not ~16 m.

Consistency:
- **tractor-beams.md:** `BeamVMax` 100 m/s, `EThrow` = `E_cap` 500 MJ, lock-only conversion under `KinematicMassLimit`, grapples above it, refusals at S > 0.3 and in protected zones, "a tractor never fractures rock", despin across the beam, the 50 MJ tag, the 6-a-minute count and the zone line all match. Only the rail-relative energy cap is new.
- **frames-and-zones.md:** gas giant bands (`RotatingTop = max(atmo top, 0.05 R)` = 700 km, leave at 910 km) and terrain-free surface bubbles are consistent. Lane bubbles stepping because of spin is listed as a change there.
- **exotic-matter.md:** AM stays industrial (an optional belt trace knob defaults to 0 and is raised as its question). EM lenses for events come from the server-only `DepositSeed`. Cold cave extraction respects `T_em`. Item ids are aligned (`propellant.hydrogen`). The `MagneticScoop` and `RadiationDose` reuse is noted in gas-giants.md.
- **planets.md:** `PlanetParams.Ring` for rocky hosts, and the giant's own `RingParams?`. No conflicts.

Performance:
- **Tumbling near players:** kinematic own-lane spinners are the right call (exact, no drift), at ~20–50 µs per stepping lane bubble. Two fixes:
  - `MoveKinematic` leaves its one-tick correction velocity on the body, so the rail ω is re-set after each 1 Hz resync.
  - Impostor spin moved from the vertex shader (trig per vertex on ~5–6k instances × up to 1280 tris) to the enumeration pass (one sincos per instance).
  - Client prediction scenes now carry spinners as kinematic too.
- **Dust fog:** +0.02–0.05 ms of haze ALU, paid back by the τ_los impostor cull. The server cost of `RingObscuration` (~20–30 µs a tick at 64 × 50 × 2 Hz) is fine. The M9 exit (choked band ≤ clear band) is the right measurement.

Exploits:
- **Respawn-event farming:** events can't be predicted (server-only `EventSeed`), the per-ring rate is fixed, and online anchors only block 20 km each. The depletion weight is now stated to be a nudge among ~1e5 windows, so mining an area out doesn't summon events there. Any future "heavy mining raises the rate" option (Q9) would be farmable. If adopted, it should count mass removed ring-wide with a cap, never per area.
- **Cave mining:** no bypass found. Hand rates (~43 kg/s per driller, ≤ 16 drillers per body) stay far below a wedge cycle; caves grow only from ship-made zones; zones are capped and never merged; hand ops are refused near heat sites. Denial (opening a heat site on someone's cave rock) is the same issue as Q3.
- **Thrown rocks:** see answer 6. Lock-freed rocks fall under the 256 per-bubble body budget, and the `ThrownMax` and fracture-rate counts apply at release.

Not changed, flagged:
- Event clusters supersede untouched natural rock in the footprint: a natural EM lens nobody had found is lost. This is consistent with "gone forever".
- Q3 (cave protection), Q9 (event cadence), Q10 (now the soft 16 MB budget with a 20 MB hard ceiling) and Q12 (spin numbers) stay open.
