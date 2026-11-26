# Rings and asteroids

Status: design only. Nothing has been built or run, and no repo files were changed. Written 2026-11-25 against the pivot docs (README, planets, frames-and-zones, ship-hulls, review, and exotic-matter as it stands) and the current `src/Terrain` and `src/Physics`. Every cost here is an estimate until the profiler rows below measure it. Revised after a lead review; the changes are listed in "Review notes" at the end. Revision 2 adds weapons against asteroids (kinetic shatter, energy-weapon heat, antimatter breaches, anti-graviton pulses); its changes are listed in "Revision 2 notes". Revision 2 was then lead-reviewed and corrected in place; the corrections are listed in "Revision 2 review notes". Revision 3 (2026-11-25, also read against tractor-beams.md) integrates the user's answers to the first six open questions: permanent mining with replenishment events and bounded deltas, bystander damage, EVA sampling and cave mining, dense and sparse bands with dust fog, gas giant hosts, and tumbling, manipulable asteroids. Its changes are listed in "Revision 3 notes"; revision 3 was then lead-reviewed and corrected in place ("Revision 3 review notes"). Revision 4 (2026-11-25) integrates four more user decisions: station shields, debris deflection by station tractors and arms, hostile-act attribution, and whole-body shatter with a dust cloud. It replaces the old "asteroid debris does no hull damage inside protected zones" rule everywhere; its changes are listed in "Revision 4 notes"; revision 4 was then lead-reviewed and corrected in place ("Revision 4 review notes", at the very end). The gas giant body type itself lives in planets.md (section "Gas giants"). Round 6 (2026-11-26) applies D72 (cavers and other parties), D73 (modest replenishment), D74 (cave drills and tunnelling vehicles) and D55/D56 (weapons, ship shields and sensors are owned by combat-and-sensors.md; offences, claims and permits by factions-and-law.md); its edits are listed in "Round 6 notes" at the very end.

## User decisions (fixed)

- Planetary rings are meshes from far away, a haze closer in, and real asteroids up close.
- Asteroids are made of well-packed material, so they cannot be dug directly. They must first be cut open or broken with a beam laser, and they may break apart violently.
- Like an egg in a microwave, too much energy causes a violent detonation.
- Mechanic: a laser warms the asteroid, then a wedge fired into it breaks it apart.
- Exotic matter (EM) occurs, impure, in asteroids and on planets. Here it is one of the asteroid materials. Its purity, rarity and privacy rules come from exotic-matter.md.
- (Revision 2) Big enough kinetic weapons shatter an asteroid regardless of the heat window: above a kinetic-energy threshold scaled by the asteroid's size and toughness, the hit fractures it directly. It is less controlled and more wasteful than laser + wedge mining (more fines lost, ore scattered, faster fragments), so mining skill still pays.
- (Revision 2) Energy weapons transfer heat into asteroids, but inefficiently compared with the mining beam laser (most of their energy is lost). They feed the same heat model, so they can still push volatile-rich rock to detonation, and the abuse limits cover weapons as well as mining tools.
- (Revision 3; events narrowed by D73) Mined asteroids are gone forever, but belts are effectively infinite. Modest events (asteroid captures and the like, nothing dramatic) may bring asteroids back, but never the same asteroid: they get new ids. Deltas compact (for example a whole cell marked mined-out), so storage stays bounded.
- (Revision 3) Shrapnel and fragments from someone else's site damage bystanders. The abuse limits around protected zones still apply.
- (Revision 3) Handheld EVA mining exists: sampling (scanning and chipping samples for composition) and cave mining (players tunnelling by hand inside large, cracked-open asteroids).
- (Revision 3) Rings have both dense and sparse bands. A band's density is tied to its colour and visibility (dense = darker and opaque, sparse = faint and clear), so players can read richness and danger from afar. Each band also has a dust level that acts as fog: it cuts visibility and sensor range.
- (Revision 3) Gas giants exist (a new body type with no voxel surface) and host rings. Their atmospheres can be harvested by gas scooping (planets.md "Gas giants", proposed).
- (Revision 3) Asteroids are manipulable: they tumble a little (natural spin), can be moved with tractor beams, and can be used as weapons.
- (Revision 4) Stations have **shields** against explosions and debris or shrapnel impacts: a capacity and a recharge rate. A big enough hit, or sustained fire, gets through. This replaces the old rule that asteroid debris does no hull damage inside protected zones.
- (Revision 4) Shattering an asteroid so that it deliberately causes damage is a **hostile act**. Damage traced to a shatter or a thrown rock is attributed to the event's owner and feeds reputation, faction standing, crime status and station response.
- (Revision 4) Debris that drifts into a protected zone stays real. Stations push it back out with tractor beams or mechanical arms, as far as their installed hardware allows. Undefended outposts near mining have a real problem.
- (Revision 4) A shatter at the threshold breaks the **whole body**: a crack through it, a lot of dust (a local fog cloud that cuts visibility and sensors for a while), and large fragments flung in different directions. Below the threshold a hit makes a crater and cracks.
- (D26, round 6) Tractor beams count as a weapon unless requested. A lock or sweep delivered to another party's ship, grid or character without consent (a docking or tow request) is an incident of kind `TractorLock`. Unowned asteroids and wrecks are not ships: the rules here apply to them. Owner: tractor-beams.md.
- (D27, round 6) Station sieges are allowed; factions prefer capture to destruction. A siege lifts some zone courtesies and keeps the debris protections (Station defence, "Sieges").
- (D28, round 6) A tractor without antimatter only pulls. Push polarity, flips and Hold assist need AM, so only AM-fed emitters can deflect or brake inbound debris (Station defence).
- (D33, round 6) Fragments, chunks, shrapnel and dust from legal mining (outside every `ProtectRadius`) that damage a station, its shield or anything in its protected zone are **always the miner's liability**, whatever the incident's class.
- (D72, round 6) Other parties may heat, wedge or shoot a rock with people in its caves. Injuring or killing players or NPCs that way is illegal: the physics here is unchanged, and the offence (Injury or Killing, `PersonHarm`) is factions-and-law.md's ("Offences", "People in caves").
- (D73, round 6) Replenishment events are modest: asteroid captures and the like. No comet breakups, no moonlet shattering, no system-wide announcement, no gold rush (Deltas and persistence, "Replenishment events").
- (D74, round 6) Tunnelling vehicles and cave drills are allowed but regulated. On asteroids they obey the damage-zone rule like hand drills, and in a held band they need a band permit with `Tunnel`. Land ownership and approval on planets, permits and the `IllegalDig` offence are factions-and-law.md's ("Land and mining rights").
- (D55, D56, round 6) Ships get shields too (particle, energy and gravity), and weapon stats, shields and sensors have their own design: combat-and-sensors.md. This doc keeps only what each weapon does to rock (the `asteroid` block), the `StationShield` against debris, and how rings and dust cut each sensor kind (`RingObscuration`).

## Recommendation (summary)

- **A ring is a function of its seed, like a planet's surface.** Radial optical depth, gaps, composition bands, the vertical profile and the size distribution are all evaluated from seed and lane index. Only deltas are stored: removed asteroids, spawned bodies and edits.
  - **Bands (revision 3)** run from sparse to dense. One density parameter per band drives the body count, the optical depth and how dark the band looks, so dense bands read as dark and opaque and sparse ones as faint and clear. A separate dust level per band is fog: it lowers visibility, sensor range and, as a bonus, how far impostors must be drawn.
  - **Hosts (revision 3):** rocky planets and gas giants. Gas giant rings are wider and denser; their arc cells use the id's spare 12 bits.
- **Permanent mining (revision 3).** A removed natural asteroid never returns. Modest replenishment events (D73: asteroid captures and minor collisions, unannounced, placed where nobody is looking) take over a small footprint of cells with fresh rock whose ids come from a never-reused event serial. Cell deltas compact to mined-out bits and each region has a byte budget, so storage is bounded.
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
- **EVA (revision 3):** hand scanners and chip samplers read composition anywhere without mining (samples can't be refined). Every fracture leaves a **damage zone** (1.5 × its radius) in the rock it broke, and fragments born from a fracture are fractured throughout. EVA players dig those zones by hand with a cold drill, and extend them with splitter charges, so caves can only start where a ship has already cracked the rock open. Cave mining is slow next to ship mining but precise: it follows veins and takes EM lenses out cold, with no heat decay. **Cave drills and tunnelling vehicles (D74)** are the powered version: a cold `CaveDrill` core on a small grid, ~6× a hand drill, under the same damage-zone rule, regulated in held bands (factions-and-law.md).
- **Bystanders (revision 3):** shrapnel, fragments and thrown rocks damage anyone, attributed to the event's owner. A public hazard beacon warns everyone within 5 km of a Critical site, a queued fracture or a thrown rock. **Cavers (D72):** anyone may still heat, wedge or shoot a rock with people in its caves; hurting them is illegal (factions-and-law.md), and the beacon and forecasts show the rock as `Occupied`.
- **Station defence (revision 4):** inside protected zones debris is real too. A station's **shield** absorbs blast, shrapnel and debris energy from a pool with a recharge rate, and leaks the excess. Its **tractors and arms** push debris back out, as many bodies at once as its hardware allows. Damage traced to a shatter, a detonation or a thrown rock is recorded as an **`IncidentRecord`** of its owner (the shared incident record, round 6), for reputation, faction standing, crime status and station response.
- **Weapons (revision 2, revision 4; weapon stats are combat-and-sensors.md's, D56):** a kinetic hit whose crack energy reaches the **whole-body** energy `G_body · F(10) · R_reach²` (`R_reach ≈ 0.8·D`, the distance from the hit to the far side) shatters the rock whatever its heat: a crack through the body, no blade, 8–16 pieces with large ones flung in different directions, 15–40 % crushed to fines, and a dust cloud that fogs vision and sensors for minutes. A single hit can do that only up to ~46 m of silicate (~73 m once the rock is cracked through). Below it, a hit makes a crater and cracks; from `E_th` (the old regional threshold) up, the crack is a **crack zone** that also leaves a damage zone, so guns can open rock for wedges and caves but never break pieces out. Energy weapons (lasers, particle beams, plasma, the AM lance) feed the same heat grid at 1–30 % of the beam's coupling, under one 10 MW absorbed-power cap per site. A contact AM breach (a torpedo warhead or a hit trap) is a crater plus an `AmBlast` fracture. AG pulses skip rail asteroids like terrain and never break rock. Every weapon effect is a server fracture event, inside the same energy, rate and per-bubble body caps. Asteroids inside protected zones stay inert to weapons, but debris entering one is met by the station's defences (revision 4).
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

Visibility here is the 5 % contrast distance `3/σ`, with mid-plane `σ = τ/(√(2π)·h)` per channel. The system generator draws ρ_b and D_b per band from the host's style (rocky: mostly sparse and clear; gas giant: mostly dense, often dusty). Replenishment events raise local dust a little for an hour or two (Deltas; modest under D73).

**Fog and sensors (revision 3).** Round 6 (D56): sensor kinds, signatures, detection ranges and stealth are owned by combat-and-sensors.md (section 4, "Sensors"). This doc owns only the medium: how ring particles, band dust and shatter dust clouds cut each kind (`RingObscuration`), which that doc's detection formula calls as its `T` term. Dust and particles are a medium, not a post effect, so the server can use them:
- **Line-of-sight optical depth:** `τ_los(a, b) = Σ_k ∫ κ_k,s · σ_k(r, z) ds` over the two channels k (particles, dust) and a sensor kind s. It is evaluated on public ring data: an 8-point Simpson rule along the segment in lane cylindrical coordinates, ~0.3 µs, with a per-bubble cache of the local σ for short paths (~20 ns). `RingObscuration.Transmittance(a, b, kind)` is the function combat-and-sensors.md's detection formula calls (its `T`; 1 in clear space).
- **Coupling per sensor kind** (`κ_part` / `κ_dust`, data; the same values as combat-and-sensors.md's "Kinds" table, which extends `SensorKind` with `Gravimetric` and `CrewScan`): Optical 1 / 1; Thermal (long-wave) 1 / 0.5; Radar (and lidar ranging) 0.5 / 0.02 (cm–m particles scatter radar, µm dust barely does); Signature (hold-field and AM containment emissions, exotic-matter's 2 km signature) 0 / 0; Gravimetric 0 / 0; CrewScan 1 / 0.5.
- **Range (the medium's effect only; nominal ranges, target signatures and quality levels are combat-and-sensors.md's, whose formula is this rule with the target's size made explicit, so the table below holds):** a passive sensor detects while `(R_nom/R)² · T ≥ 1`, and an active one while `(R_nom/R)⁴ · T² ≥ 1`. Both give `R ≤ R_nom·√T`; in a uniform medium `R_eff = (2/σ_s)·W(σ_s·R_nom/2)` (Lambert W). Examples for a 50 km sensor in the mid-plane:

  | Medium | Optical | Radar |
  | --- | --- | --- |
  | Sparse (ρ_b 0.05), clear (σ_opt ≈ 2.4e-5 /m) | ~33 km | ~39 km |
  | Medium (ρ_b 0.5) + hazy dust (D_b 0.2; σ_opt ≈ 4e-4) | ~8.7 km | ~17 km |
  | Dense + choked (σ_opt ≈ 1.3e-3, σ_radar ≈ 2.6e-4) | ~4 km | ~11.5 km |

  So radar beats dust, optics go blind in it, and signature and gravimetric sensors ignore rings. Decided (D56, combat-and-sensors.md "Stealth"): hiding in a choked band, or behind a shatter's dust, is a real tactic against optics and thermal, and is not one against radar, gravimeters or signature readers.
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
- **Refusals:** locks are refused within `ProtectRadius` of protected zones and on bodies whose site has S > 0.3; a locked body accepts no beam heat; sweeps deliver zero to asteroid bodies in protected zones. A tractor never fractures rock. **Revision 4 exemption:** the zone's own station-defence emitters, and parties the zone permits, may lock and sweep **debris** inside it (Station defence): bodies already Dynamic or Coasting, and rubble clouds. Rail, static and kinematic rocks inside a zone stay untouchable by everyone, so the exemption can't free or work natural rock there (review fix). The hot-body refusal still applies.
- **Attribution of every release (review fix):** any asteroid body a beam has touched carries `LastBeamOwner` (the owner of the last emitter to deliver force) until it is re-railed or back under 5 m/s relative to its lane. Its contact damage is attributed to that owner even below the `Thrown` threshold, so many 49 MJ throws are not anonymous.
- **Thrown bodies:** a rock released with `max(W_b, E_rail)` > 50 MJ (`ThrownTagJ`) is tagged `Thrown` with its emitter's owner. While tagged:
  - it never coasts. Each tick its swept capsule is tested against every entity's bounding sphere within reach (frozen and parked ships included) and against the instancer, like a fast-transit ship: ~2–4 µs per body;
  - a hit is a **ram**: the ship takes a contact hit (combat-and-sensors.md "Contact and rams": a `Blunt` hit from the reduced-mass contact energy), a rock takes a `KineticHit` (source Ram) that may crater, crack or shatter it. Both are attributed to the thrower, and a shatter counts against the thrower's fracture rate limit;
  - it counts against the owner's 6 fracture events a minute at release (tractor-beams.md);
  - the tag ends after 120 s, or once its speed relative to its local lane falls under 5 m/s, or when a fracture consumes it; but (revision 4 review) never while its release path still leads into a protected zone or a station shell, so a slow throw from outside the zone stays swept, top-priority for station defence and Hostile on arrival (it keeps its slot in the thrower's 8). Then normal coasting resumes, with one addition (review fix): a coasting body still over 5 m/s relative to its lane is tested at 1 Hz along its closed-form Hill path against frozen and parked entities (a look-ahead over the next second, as the projectile rail look-ahead), so a rock whose tag timed out can't pass through a parked ship. ~1 µs per such body per second; it stops at 5 m/s or on re-rail.
  - **Caps:** ≤ 8 live `Thrown` bodies per player (`ThrownMax`): at the cap the player's new asteroid locks are refused until a tag ends; ≤ 128 server-wide, past which new throws are clamped at `W_b` = 50 MJ (a push, not a throw).
- **Rocks as weapons, the full list (D28: a throw away from the emitter needs Push, so AM; a pull-only tractor can only drag a rock toward itself and let go):** a throw (a ram of ≤ 500 MJ), a shatter or a cooked site next to a target (fragments and shrapnel now damage bystanders, see Abuse limits), a shield (a held rock interposed in a tractor cone or a line of fire takes the force or the rounds), and a rock pulled into a fast ship's path. Every one is bounded by `E_cap`/`EThrow`, attributed and rate-limited. Rocks inside protected zones are inert to them; rocks and debris that enter one are real and meet the station's shield and deflection, and any damage they do there or elsewhere is an `IncidentRecord` of the thrower or the event's owner (revision 4, Station defence).
- **Rock on rock:** a dynamic or thrown rock hitting a rail rock is a `KineticHit` on each, with **half** of the reduced-mass energy `½μv²` each (review fix: giving both the full amount spent 2 × 55 % of the impact energy and broke the no-energy-gain rule). A 1e6 kg rock at 32 m/s into an equal one has `½μv²` ≈ 256 MJ, so 128 MJ per rock: enough to shatter a silicate rock up to ~11 m whole, and to leave a crack zone in a 64 m one (above its `E_th` ≈ 95 MJ; revision 4: no pieces below the whole-body threshold). Cascades advance one fracture event per bubble per tick, inside the fracture queue.
- **Per-tick cost of a held rock:** Hill force terms (~30 ns), the tractor's force and `AddForceAndTorque` (~0.8 µs), one awake Jolt body (~1 µs), and its share of the 10 Hz beam raster (~0.2 µs amortised). ≤ 16 beams per bubble bounds it at ~30 µs a bubble; thrown sweeps add ≤ 8 × 4 µs per player.

**Layers.**
- `AsteroidRail`: static and kinematic natural rails and re-railed bodies. It does not collide with itself, because the fit rule makes that unnecessary.
- Dynamic asteroids and boulders use `AsteroidMoving` (revision 4 review: it collides exactly like `Moving`, but is its own layer so station shield sensors pair with debris and never with ships); loose chunks use `Debris`. Both collide with `AsteroidRail`, so a freed rock does hit its rail neighbours.

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
- **Damage zone:** every Spall, Split, Shatter, AmBlast and Detonation leaves a `DamageZone` sphere on the body it broke, centred on the event centre `c`, radius `R_dz = ζ_dz · R_f` (`ζ_dz` 1.5; 1.3 × `R_det` for detonations), clipped to the remaining body. Remainders keep the zones that intersect them. A Crack result (R_f < R_min) makes no zone: a crack is not an opening. Revision 4: a kinetic **crack zone** (a sub-threshold hit at or above `E_th`, Weapons against asteroids) leaves a zone of `ζ_dz · R_f` round the hit, since that rock has been shocked as much as an old regional shatter's; ordinary weapon cracks below `E_th` still make none.
- **Diggable test (server):** a hand dig op on a Packed body is accepted only if its centre is inside one of the body's zones and its sphere lies within that zone + 0.5 m. Fractured bodies pass everywhere; Loose bodies are drilled as before.
- **Extending a cave:** a **splitter charge** (EVA tool, cold expansive splitter, no heat) set in a drilled hole ≥ 0.25 m deep makes a `Crack`-kind fracture event with a fixed `R_f` = 1.5 m at the hole, which adds a zone of 2.25 m. It needs a hole, and a hole needs a zone, so a cave can only grow from where a ship first broke the rock. Splitter events have no fragments and no bake (~5 µs), and their own limits (6 a minute per player, 16 a tick server-wide). At the 64-zone cap a splitter is refused (zones are never merged into a bounding sphere, which would grow the diggable volume for free); a ship fracture at the cap replaces the smallest splitter zone that lies wholly inside dug-out volume, else its zone is dropped.
- **Why hand caves don't bypass the wedge:** a hand drill moves ~0.016 m³/s (~43 kg/s of silicate), against a wedge cycle that breaks out 1e5–5e6 kg in 60–90 s. Caves are for precision: following a vein, taking out a metal core, and above all extracting **EM lenses cold** (heating past 700 K decays EM; a drill doesn't heat), at ~100 % lens recovery against the losses of a wedge or shatter.
- **Cave detail:** inside a damage zone and within `CaveDetailRadius` (24 m) of an EVA character, the body's octree refines up to two levels below its base voxel, to 0.25 m, so 0.25–0.35 m hand ops show and collide (planets' rule: an op under ~1 voxel does nothing). The character collision ring there is 16 m of 8 m chunks (~35 chunks) instead of 64 m at base. Cost: ~35 chunk builds when a character enters (0.5–0.8 ms each on workers, as planets), then 1–2 rebuilds per op; memory ~35 × 60–300 KB = 2–10 MB per caving character (review P1's fallback). Characters within 16 m of each other share chunks. Server cap `CaveDetailMax` 32 caving characters (≤ ~0.3 GB worst; review fix: 64 cavers spread over 64 rocks would otherwise be ~0.6 GB): past it, new cavers queue for detail and their hand ops are refused until a slot frees, with a HUD notice.
- **No collapse:** tide (≤ 2.2e-3 m/s²) and self-gravity (~1e-4 m/s²) are far below any rubble cohesion the game models, so tunnels stand. A later fracture whose region crosses a cave simply makes fragments there; characters inside take fragment impulses and, for a detonation, shrapnel.
- **Shrapnel inside rock:** shrapnel rays that start inside the remaining rock (a site in a cave or deep in a crack) are first traced through the parent's SDF and stop at rock (≤ 32 steps per ray, once), so a cave blast is lethal inside the cave and harmless outside it.
- **Heat near caves:** hand dig ops are refused within 6 m of an active heat site's grid (hot rock, and digging would rewrite the site's cells). A heat site may still open on a rock with people in its caves; the hazard beacon (Abuse limits) tells them.
- **Other parties and cavers (D72, decided).** Anyone may heat, wedge or shoot a rock with people in its caves, outside protected zones as for any rock: the server refuses nothing on their account, and presence in a cave claims nothing. **Injuring or killing a player or NPC that way is illegal.** The law side is factions-and-law.md's ("Offences": Injury and Killing, kind `PersonHarm`; "People in caves": Hostile when the people were in the rock before the actor's first site or fracture event on it, at most Reckless when they came to the hazard afterwards, which is the "came to the hazard" cap under Station defence and hostile acts). This doc supplies only the facts that ruling needs:
  - **Occupancy** is a count per body of characters (players and realised NPC crew) inside its damage zones or dug volume. It changes only on the cave-detail enter and leave events that already exist, so it costs nothing per tick (1 B per realised body).
  - **`OccupiedAtFirstEvent`:** when an actor's first site or fracture event on a body is recorded (the stamp the "came to the hazard" rule already keeps), the server also stores whether the count was non-zero. Any later injury record cites it.
  - **Nobody can say they didn't know:** the wedge and kinetic forecasts, the scanner readout and the `HazardBeacon` carry an `Occupied` flag (one bit; occupancy is public, and crew positions can be scanned, D48). Opening a site on, or queuing a fracture against, an `Occupied` body sends its cavers one `HazardBeacon` at once (kind `SiteOpened`), not only at Critical.
  - **Harm** is what the fracture and detonation rules above already do to characters (fragment impulses, shrapnel inside the cave); a hurt character writes the record with the event as `SourceEvent`.
- **Cave drills and tunnelling vehicles (D74, decided: allowed but regulated).**
  - **What they are:** a `CaveDrill` function core (Tools as components): a cold, powered drill mounted on a ship or on a small grid. A tunnelling vehicle is any small grid that carries one, built like any ship (ship-design-and-construction.md); there is no special vehicle type.
  - **Same rock rule as the hand drill:** its ops pass the Diggable test above (inside a damage zone, or on a Fractured or Loose body), so caves still start only where a ship has cracked the rock. Its charge setter places ordinary splitter charges ahead of the bit, under the same event and the same limits (6 a minute per player, 16 a tick, the 64-zone cap), so a vehicle can advance its own tunnel. Refused within 6 m of an active heat site, like hand ops.
  - **Rate:** 0.1 m³/s at silicate hardness (~270 kg/s, ~6× a hand drill, scaled by hardness like it), op radius 0.75–1.5 m. That is about a quarter of the slowest wedge cycle (1e5 kg in 90 s), so the wedge cycle stays the bulk path. At most 2 cave drills work one body at a time (proposed), inside the ≤ 4 ops/s per body budget.
  - **Collision and detail:** a working vehicle needs the LOD-0 chunk collision ring round it, so it takes one of the 32 `CaveDetailMax` slots (at base voxels: its ops are ≥ 1 voxel, so no refinement unless an EVA character is beside it).
  - **Regulated:** the template carries `regulated: true`. In a held band, using one needs a band permit with the `Tunnel` right (factions-and-law.md, "Rings, belts and asteroids"); in unheld belts it is free. The verdict is that doc's `LandRights.Check`, cached on the tool for 1 s. An op without the right still runs and is counted there toward an `IllegalDig` case (`ClaimJump` on another party's claimed rock). Protected zones keep their physical ban whatever the permit.
  - **Planets:** owning the land or having approval, tunnel corridors and the dig floor are planets.md's and factions-and-law.md's ("Land and mining rights"). Nothing here changes them.
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
- **Shrapnel sweep:** shrapnel rays are swept by the server for 1.5 s, in double in the asteroid's frame, against **every** entity within `ShrapnelVMax · 1.5 s`, not only the bubble's. A hull hit becomes a ship-hulls damage brush with `r = (½ m_s v_s² / e_cav)^(1/3)` (`e_cav` is combat-and-sensors.md's material value, `eCav` in data; earlier revisions called it `e_hull`).
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

Detonations, rams, thrown rocks and the tools themselves can be turned on other players. Revision 3 settles the old open question 2: **shrapnel and fragments from anyone's site damage bystanders** (see Bystanders below). Revision 4 extends that into protected zones: debris there is real, and stations defend themselves (see Station defence and hostile acts, below). These limits apply on top:
- **Protected zones** (stations, gates, and anything a later zone system declares; radius `ProtectRadius`, 5 km, which is more than shrapnel reach). They stop rock being worked or broken **inside** them; they no longer make debris harmless:
  - the server caps heat sites at Warming, by throttling the beam;
  - wedges ricochet;
  - tractor locks on asteroid bodies are refused, except on debris (Dynamic or Coasting bodies) by the zone's own station-defence emitters and parties the zone permits (revision 4).
- **Hot bodies can't be moved as bombs:**
  - A tractor lock is refused on a body whose site has S > 0.3.
  - A body under a lock stops accepting beam heat.
- **Energy caps:** `E_cap` (500 MJ) per fracture or detonation event, plus `ShrapnelVMax` and K. One event therefore has a bounded reach and bounded per-shard energy (≤ 5.6 MJ).
- **Attribution:** every event records the site owner, or the shooter for wedges. Damage from its fragments, shrapnel and other debris (and the energy a station shield absorbs from them) is traced to that owner and recorded as an `IncidentRecord` (revision 4, Station defence and hostile acts).
- **Rate limits:**
  - ≤ 6 fracture events a minute per player, besides the wedge reload.
  - A server-wide budget of 4 events per tick with a fair queue: one per bubble per tick, round-robin.
  - ≤ 16 fragments plus 4 remainders per event.
- **Persistence caps:** ≤ 4096 `SpawnedBody` records per region file and a per-region byte budget (revision 3, see Deltas). Past either, the oldest re-railed boulders under 16 m become removed, and their mass is lost.
- **The beam laser and wedge launcher are also weapons against hulls.** Decided (D56): combat-and-sensors.md, "Mining tools and other things used as weapons", defines what they, the drill and the splitter charge do to a hull. Each makes ordinary hits there.

**Bystanders (revision 3).** User decision: shrapnel and fragments from someone else's site damage bystanders.
- **Everywhere,** shrapnel, fragments, chunks, rubble rams and `Thrown` rocks damage any ship and any EVA character, whoever owns the site. Damage is the existing rule (shrapnel → damage brush `r = (½ m_s v_s² / e_cav)^(1/3)`; bodies → combat-and-sensors.md's contact hit, a `Blunt` hit from the reduced-mass contact energy, which dents before it holes), traced to the event's owner (or the thrower) and recorded as an `IncidentRecord`. Characters take shrapnel through the character health rule; the shrapnel sweep already tests every entity within reach.
- **Inside protected zones (revision 4, replacing "debris does no hull damage there and becomes lost mass"):** debris is just as real. What stands between it and a docked ship is the station's shield (which absorbs energy up to its pool and leaks the rest) and its deflection hardware (tractors and arms that push debris back out). A station with neither takes the hits. See Station defence and hostile acts.
- **Hazard beacon:** a site in Critical, a queued fracture on a body, an armed contact AM trap on rock, and every `Thrown` rock broadcast a public `HazardBeacon` (position, kind, predicted reach: `ShrapnelVMax · 1.5 s` = 2.25 km for a site, the predicted path for a throw) at 2 Hz to every client within 5 km, 16 B each. The HUD draws it whatever imager the bystander has, so a griefer can't cook a rock next to a stranger unseen, and attribution names them if they try. Round 6 (D72): the beacon carries an `Occupied` bit when the rock has people in its caves, and a site opened on such a rock beacons its cavers at once (Damage zones and caves).
- **Rate and energy limits are unchanged**, so a griefer's reach is one `E_cap` event at a time, ≤ 6 a minute, ≤ 1.5 km/s per shard and ≤ 80 m/s per fragment, all attributed.
- **Cave miners get no physical protection** from other parties' heat, wedges and gunfire on the same rock (decided, D72). The beacon and attribution warn and name; the law punishes: injuring or killing someone in a cave that way is illegal (factions-and-law.md, "People in caves").

**Thrown rocks and EVA (revision 3).**
- `Thrown` rocks follow Manipulation: ≤ 8 live per player, ≤ 128 per server, swept like fast ships, attributed. A `Thrown` body that enters a protected zone keeps its tag and is real there (revision 4: the old "contact damage zeroed and logged" line is withdrawn): the station's shield and deflection meet it, the station's watch gives it top priority, and whatever it does is an `IncidentRecord` of the thrower.
- **Sampling is not mining:** chip and core samples are `sample` items in no material family: no refinery, recipe, socket, blender or market accepts them as material (an assay reads them into a record). A body keeps ≤ 64 sample marks (cosmetic, oldest dropped); on an otherwise untouched natural they count as cosmetic craters, so they persist only inside the region's cratered-naturals cap and are ephemeral past it (review fix: chip-sampling thousands of rail rocks could otherwise grow storage without limit).
- **Hand digging** is accepted only inside damage zones (or on Fractured bodies), at ≤ 1 op/s per player inside the ≤ 4 ops/s per body budget, and never within 6 m of an active heat site. Splitter charges: 6 a minute per player, 16 a tick server-wide, and refused in protected zones like wedges. Cave drills (D74) follow the same rules, with ≤ 2 working per body; their permit side is factions-and-law.md's.
- **Replenishment events** are never placed within 20 km of an anchor, a realised body or an active site, so nothing changes under anyone's physics; and never where a player is within `EventHideKm` (130 km) of the footprint, so nobody watches the swap (D73: events are modest and unannounced; Deltas). Players' work in a footprint (spawned bodies, worked naturals, caves) survives the takeover.

**Weapons (revision 2).** The limits above were written for mining tools. They now cover every source that heats, cracks or blasts rock:
- **Protected zones:** asteroids inside `ProtectRadius` are inert to weapons and rams. Weapon heat goes nowhere (no sites, no bulk), kinetic hits and rams leave no crater, crack or fracture, and contact AM breaches make no crater or fracture (their hull damage and dose are exotic-matter's, unchanged). AG pulses already skip rails everywhere. So no new debris is **made** inside a zone; debris made outside and carried in is real (revision 4).
- **Absorbed power cap:** `P_siteMax` (10 MW absorbed per site, all sources summed, the excess lost to plume shielding). This bounds how fast anyone can drive a site to detonation. It does not bound substeps (10 MW into one 1 m³ silicate cell is ~450 K per step, past the 8-substep cap); cells clamp at `T_melt` and ablate, so the cap only coarsens the forecast. The mining beam (~1.2 MW absorbed) is far below it.
- **Weapon sites:** opened only once weapon heat on one body reaches `E_open` (1 MJ absorbed) within a 1 s window; ≤ 4 weapon-opened sites per player, and ≤ 128 server-wide, so weapons can never take more than half of the 256 sites from miners. They count in the 2 per body cap. Past a cap, weapon heat goes to the bulk node.
- **Bulk-node weapon heat is capped:** weapon heat (and the AM standoff flash) can raise `T_bulk` only to `max(T_bg, min(T_soft, T_vap))`; the excess is lost. The bulk node has no volatiles and no S, so without this cap sub-`E_open` fire could warm a small body past `T_vap` silently (a 4 m rock's bulk node holds only ~7e4 J/K), or weaken it to cut its shatter threshold, both outside the egg rule.
- **One energy cap:** `E_cap` bounds `E_crack` and `E_kin` of every shatter and AmBlast, as for wedges and detonations. `E_kin` is measured in the centre-of-mass frame of the parent and its pieces, so a dynamic parent's recoil comes out of the same budget. Every fragment and every recoiling remainder is capped at `ShatterVMax` (80 m/s) relative to the parent, and shrapnel at `ShrapnelVMax`.
- **One rate limit:** Shatter and AmBlast events count against the same 6 fracture events per player per minute and the server's 4 per tick (fair-queued, 1 per bubble). A player over the limit gets crater + crack instead of a shatter (an AmBlast over the limit keeps its crater only). A queued event resolves on a later tick with its original inputs; the hit itself is not refused.
- **One pending fracture per body:** while a body has a queued event, later kinetic hits on it take the crater + crack path, applied after the queued event in hit order. The queue holds ≤ 64 events server-wide; a hit that finds it full degrades to crater + crack. So sustained fire on one rock, or on many, cannot build an unbounded backlog of fracture work.
- **Worker budget:** fracture bakes and mass integration have a server job budget (`FractureJobMsPerTick`, 4 ms of worker time a tick). An event that does not fit waits in the queue; the budget, not the 4-per-tick count, is what holds sustained load (4 events of 20 bakes at up to 3 ms would be 240 ms of work).
- **Asteroids as kinetic weapons:**
  - No energy gain: a cold shatter gives its pieces at most `ξ_k · E_p` (30 % of the round's energy), spread over 8–16 pieces and a cone. Only stored site energy adds to it, and heating is refused in protected zones, capped by `P_siteMax` and attributed.
  - Weapons no longer make spalls (revision 4). A whole-body shatter throws its large pieces outward from the body's centre in every direction, biased downrange by the shot, so a shatter is a poor aimed weapon: most of its mass goes elsewhere.
  - **Debris in protected zones (revision 4, replacing the revision 2 review's "no hull damage, impulse only, lost mass"):** an asteroid body (boulder, chunk, freed rock, cloud) that enters `ProtectRadius` stays real and does normal damage there. Fragments shattered just outside a station can be aimed into it; that is answered by the station's shield and deflection, and by the shatter being an `IncidentRecord` of its owner (Station defence and hostile acts).
- **Edit rate:** crater ops share the ≤ 4 ops/s per body budget with ablation. Crack records stay capped at 8 per body, so cratering one rock forever does not grow its record.
- **Attribution:** shatter → the shooter (rams → the pilot); AmBlast → the shooter whose hit breached the trap if there was one (the trap owner is then only a co-actor), else the trap owner; detonations → the top heat contributor of the last 30 s, with all 4 contributors listed, except that a party other than the site owner who put ≥ 10 % of that heat into the site is the actor (revision 4 review: otherwise a griefer tops up someone's Critical site and the miner takes the blame). Revision 4: this owner is the actor of every `IncidentRecord` the event's debris causes.
- **Stray fire is cheap:** projectile impulses never free natural rocks, sub-`E_open` heat never opens a site, low-energy rounds test rails by a 10 Hz look-ahead only (Performance), and rounds that hit Loose chunks turn them into cloud mass instead of making new bodies.
- **Hot bodies as kicked bombs:** see "Anti-graviton charges" (gradient venting; open question 4).

### Station defence and hostile acts (revision 4)

User decisions (fixed): stations have **shields** against explosions and debris or shrapnel impacts (a capacity and a recharge; a big enough hit or sustained energy gets through); debris that drifts into a protected zone **stays real**, and stations push it back out with **tractor beams or mechanical arms**, as far as their installed hardware allows; damage deliberately caused by a shatter is a **hostile act** of its owner. This replaces the revision 2 review's rule that asteroid debris does no hull damage inside protected zones and becomes lost mass.

Protected zones keep their other job: rock inside `ProtectRadius` can't be heated past Warming, wedged, locked by other parties or broken by weapons, so no new debris is **made** there. What changes is debris made outside and carried in (shattered fragments, kicked or thrown rocks, chunks, stray shrapnel). Two layers of hardware meet it, and both are components the station owner installs on the station grid: a station without them takes the hits.

**Shield (`StationShield` core).**
- **Shell:** a sphere of radius `r_sh` round the station grid, from its bounding sphere + 20 m up to the class's maximum radius. Docked ships, the station's grid bubbles and anything already inside are inside.
- **Pool:** `E_s` joules, capacity `E_max`, recharged at `P_r` (pool watts) from the station bus at efficiency `η_s` = 0.5, plus a small base upkeep while raised. It is settled lazily, like the station anchor's reserve: `E_s(t) = min(E_max, E_s0 + P_r · (t − t0))`, stored as `(E_s0, t0)` and recomputed only when something hits it. A quiet shield costs nothing per tick.
- **What it absorbs** (inward crossings of asteroid debris and shrapnel only; ships, characters and traffic never interact with it. Gated by energy, not speed: an inward crossing is absorbed when it offers ≥ `ShieldMinJ`, 10 kJ. Review fix: the draft's 2 m/s speed gate let a 1e8 kg boulder creep in at 1.9 m/s, ~180 MJ, untouched):
  - **Shrapnel:** each ray crossing the shell offers its `½ m_s v_s²` relative to the station.
  - **Debris bodies** (fragments, chunks, remainders, freed, kicked and `Thrown` rocks): the shell removes the inward normal part of the body's velocity relative to the station, which offers `½ m v_n²` (an anchored station is an infinite mass; a free-flying one uses the reduced mass). The tangential part is kept, so a stopped rock slides along the shell, where deflection picks it up. A stopped body that stays in the shell sensor keeps having its inward normal velocity removed each tick (tide and Coriolis push it back in at ~mm/s, so this costs the pool almost nothing; ~0.2 µs per such body per tick). Round an outpost with a shield and no deflection those rocks pile up on the shell, awake, inside the bubble's 256-body budget: the "real problem" of undefended outposts, made visible.
  - **Blast:** there is no blast wave in vacuum (Detonation), so an explosion reaches a station as shrapnel and fragments, which the two rules above cover. An AM breach's dose and an AG pulse's kick are not shield business: exotic-matter's `ShieldTable`, an `EnergyShield` (combat-and-sensors.md; the former `RadiationShield`) and gravity dampers handle those.
  - **Not absorbed:** rubble clouds (grit does no hull damage, like gravel; deflection sweeps them), dust clouds, and characters.
  - **Weapons fire (hook taken, D55/D56):** combat-and-sensors.md routes rounds, shells, bolts and missiles through the same absorb call (they offer their energy like shrapnel). What weapons do to a `StationShield`, and the three **ship** shields (particle, energy, gravity: D55), are that doc's (section 3, "Shields"). This doc defines the `StationShield` only against asteroid debris and shrapnel; a ship's own shields meet debris by that doc's rules (grit, shrapnel and small fragments are particle-shield business, rocks and rams gravity-shield business).
- **Absorb and leak:** `a = min(E, E_s)`, `E_s −= a`. The rest gets through: a shrapnel ray continues with `E − a` (its damage brush sized from that), and a body continues inward at `v_n' = sqrt(v_n² − 2a/m)` and hits the hull as an ordinary contact hit (combat-and-sensors.md "Contact and rams"). So **one hit bigger than the pool** or **energy arriving faster than `P_r`** gets through, as decided.
- **Momentum:** the impulse that stops a body goes to the station like a contact, so an anchored station debits it to its anchor at `EmPerNs` (exotic-matter): stopping a 1e6 kg rock at 32 m/s (the 500 MJ throw cap) costs ~32 mg of EM content.
- **Down:** a drained shield stays raised and recharges at once at `P_r`, so sustained fire above `P_r` keeps it at zero. It drops with no bus power, or below core Integrity 0.5 (exotic-matter's shared damage rule).
- **Classes** (station shields only, proposed numbers, `data/components/station-defence/shields.json`; ship shield classes are combat-and-sensors.md's, D55):

  | Class | `E_max` | `P_r` (pool) | Bus while charging (+ base) | Max `r_sh` | Mass | Holds |
  | --- | --- | --- | --- | --- | --- | --- |
  | S (outpost) | 200 MJ | 2 MW | 4 MW (+0.5) | 200 m | 20 t | About ten fragment hits (an 18 MJ, 1e4 kg piece at 60 m/s); a capped 500 MJ throw leaks 300 MJ. Refills in 100 s |
  | M | 2 GJ | 20 MW | 40 MW (+2) | 800 m | 120 t | Four capped throws back to back. Refills in 100 s |
  | L (station) | 20 GJ | 100 MW | 200 MW (+5) | 2 km | 600 t | Forty capped throws; only a coordinated barrage or a sustained > 100 MW drain gets through. Refills in 200 s |

- **What can actually drain a shield (review check).** Rock inside the zone is inert, so every shatter is ≥ `ProtectRadius` (5 km) from the station, and its pieces fly outward from the body's centre in every direction with only a 0.3·R_b downrange bias. The shell of radius `r_sh` at distance d catches about `r_sh²/(4d²)` of them: ~4e-4 of an S outpost's share at 5 km, ~4 % for an L station's 2 km shell. A whole-body shatter carries ≤ 500 MJ of `E_kin`, so gunfire shatters put ≤ ~0.2 MJ (S) to ~20 MJ (L) into a shield each, and at 6 events a minute per player that is ≤ ~2 MW per player: **cheap gunfire shatters can't overwhelm a shield**; it would take ~50 players against an L station's recharge. Detonation shrapnel never reaches it (heat sites are capped at Warming inside the zone, and shrapnel reach is 2.25 km). The aimed threat is the `Thrown` rock: ≤ 500 MJ each, ≤ 6 releases a minute per player (the fracture-event count), so one player sustains ~50 MW: past an M shield's 20 MW recharge, half an L station's. That is the intended spread: hardware against coordination.

**Deflection (`StationDefence` controller).**
- **Hardware:** station tractor emitters (tractor-beams.md classes S–XL, reaction paid by the anchor) assigned to defence, **AM-fed emitters only (D28)**: deflecting or braking an inbound body needs Push, and a pull-only (graviton) emitter would draw it in, so `StationDefence` never assigns an emitter without AM in its trap to an inbound body. Arms and shields need no AM. Emitters without AM stay under crew control (a pull-only docking tether on a visitor's request, tractor-beams.md), and `DebrisArm`s: mechanical arms with 40 m reach from their mount, 500 kN of push, one body of ≤ 1e6 kg at ≤ 10 m/s relative at a time, 0.2 MW, no EM. Emitters not assigned stay under crew control.
- **Watch** (2 Hz per defended station): the asteroid-related bodies and rubble clouds of the station's bubble, read from the bubble's asteroid body list (≤ 256, ~20 ns each; review fix: the draft walked the 64 m entity grid over a sphere of 1.5–5 km, ~10⁴–10⁶ cells), plus **scheduled arrivals**. Coasting debris is not polled (review fix: the draft tested every coasting body against every station each watch, unbounded): when a body starts coasting, is born from a fracture outside a bubble, or changes its elements (a kick), its closed-form path is tested once, over `ReleaseHorizonS` (below), against the shells of shielded or defended stations it can reach in that time (~1 µs per station in reach; a few at most), and a crossing schedules it into that station's watch at `t_cross − WatchHorizonS` (and, for a body still coasting then, a shell absorb at `t_cross`). For each watched body, the closest approach to the shell over `WatchHorizonS` (120 s) gives a time to impact `t_i` and an energy `E_i` relative to the station. Bodies that miss the shell by more than `MissMarginM` (100 m) are ignored. The watch tracks only what the station's observer group holds in its `ContactTable` at Q2 or better (combat-and-sensors.md section 4, whose detection runs through `RingObscuration`): radar sees through dust, optics don't.
- **Priority:** `Thrown` bodies first, then bodies whose actor has a live Hostile record against this station, then by `E_i / max(t_i, 1 s)`. Rubble clouds do no hull damage, so they have `E_i = 0` and are served only by idle emitters (review fix: otherwise cheap grit clouds are decoys that hold emitters). **Preemption (review fix):** when a higher-priority threat would reach the shell before any emitter frees up, the emitter holding the lowest-priority body that is already on a missing path drops it and retargets (one retarget per emitter per second). Without it, baiting the station into a long Hold on a slow heavy boulder took an emitter out of the fight for minutes.
- **Response per body**, from the free emitter or arm that can reach it soonest:
  - **Sweep (push)** with a narrow cone, for light bodies with time to spare: pushes the body outward and off its line. No acquire ramp.
  - **Hold** (lock, then reel `d*` out; Hold assist, AM only) for heavy bodies, or when a body must be braked before it can be steered. Braking is never faded (`g_i`), so a station can always stop what it can reach.
  - **Arm**, the last line within 40 m of the hull: shoves out a slow body the shield has stopped, or one that leaked through.
  - **Release** once the predicted path misses the shell by ≥ `MissMarginM` and the body moves outward relative to the station at `v_rel = clamp(sqrt(2 · DefenceReleaseJ / m), 0.2, 5) m/s` (`DefenceReleaseJ` 10 MJ: a 1e6 kg rock leaves at ~4.5 m/s, a 1e8 kg boulder at ~0.45 m/s), or once it leaves `ProtectRadius`. Review fix: the draft's flat 2–5 m/s let the station's own push give a 1e8 kg boulder up to 1.25 GJ relative to the station, above `E_cap`, a free and unattributed ram.
  - **The miss test runs over a whole orbit, not the 120 s horizon (review fix).** In a lane bubble a pushed body follows the Hill (Clohessy–Wiltshire) solution: a purely radial Δv is a closed 2:1 ellipse that brings it back to the station after one lane period (61–109 min), so the draft's defence would have pushed the same rocks out every hour, forever. Release therefore tests the closed-form path over `ReleaseHorizonS` (one lane period, ≤ 2 h; 600 s in fields, where paths are straight), ~1 µs once at release, and prefers push directions with an along-track component, which drift away secularly (`−3·Δv_along·t`).
  - **Push direction:** chosen by the controller, not by crew: among 16 candidate directions, the one whose release path keeps the largest clearance from every entity within 5 km and from the station's declared approach lanes (data), ~3 µs at release. A station can't aim its defence at visitors. Defence beams deliver nothing to ships, grids or characters (asteroid bodies and clouds only), don't set `LastBeamOwner`, and release below the 5 m/s attribution threshold, so pushed-out debris stays attributed to whoever made it, never to the station (and see `IncidentRecord` classes for what that maker is liable for after a defence push).
- **Force budget (it is rarely the limit):** braking a body over a distance `L` needs `m v² / (2L)`. A capped throw (1e6 kg at 32 m/s) over 500 m needs ~1 MN, inside one M emitter (2 MN); a 1e8 kg boulder at 3 m/s needs ~0.9 MN over the same 500 m. The limits are count, reach and time.
- **Capacity:** AM-fed emitters only (D28); an emitter whose trap runs dry drops its body (flagged `Unserved` if it still threatens) and returns to crew control. One body per emitter or arm at a time; ≤ 1 lock acquisition per emitter per second, with the 30-tick acquire ramp; ≤ 8 defence emitters per station, in **reserved slots** outside the bubble's 16 beams and the 4-per-owner cap (review fix: inside the 16, four visitors with 4 beams each could fill the bubble's slots and switch the station's defence off), so a bubble holds at most 16 + 8 per defended station; each class's range and slew; EM, AM and bus power from the station's own tanks; emitter Integrity.
- **Overwhelmed:** when more bodies threaten than the hardware can serve before impact, the controller serves by priority and flags the rest `Unserved` (HUD, profiler); they meet the shield alone, then the hull. Ways it happens: a swarm (several shatters: a whole-body shatter is 8–16 pieces plus chunks at up to 80 m/s, ~60 s from the zone edge), short reach (S and M emitters see 150–500 m, so an 80 m/s chunk gives 2–6 s), starved tanks, damaged emitters, or an optical-only watch blinded by a shatter's dust cloud.
- **Cost:** ~1 µs per defended station per watch when nothing is near (100 stations at 2 Hz ≈ 3 µs a tick), ~0.2 µs per tracked body, ~1 µs per station in reach when a body starts coasting or is born outside a bubble, ~4 µs per release (orbit-long miss test and push direction); active defence beams cost what tractor-beams.md says (~2 µs geometry at 10 Hz, ~1 µs forces per tick per beam), ≤ 8 per station, so a fully engaged station is ≤ ~10 µs a tick.

**Undefended outposts.** A zone with no shield and no deflection still stops rock being worked inside it, but debris that drifts in hits whatever it reaches. Every zone's defence is public: the HUD shows it to anyone within 20 km ("shield 40 %, 2 deflectors" or "UNDEFENDED"; deflectors counts AM-fed defence emitters only, D28; arms are listed separately), and the kinetic and wedge forecasts flag any zone inside the predicted fragment reach. Mining next to an outpost is the miner's responsibility (D33: the miner is liable for any damage its debris does to the station, its shield or anything in its zone), and the record below says whose debris did what.

**Hostile acts and the incident record.** Round 6 (D57): the law doc now exists. factions-and-law.md ("The incident record") **owns** the record, its kinds (it appends `PersonHarm`, `IllegalDig`, `ClaimJump`, `WeaponFire` and others) and what a class costs (fines, standing, heat). The struct, the enums, station listing and the siege rules are defined there (r7: the copies that stood here are removed); what stays here is the asteroid side: tracing, what counts, the class distances, accumulation and the flush. The one server-only record is **shared with warp.md, tractor-beams.md, exotic-matter.md, ship-atmosphere.md and combat-and-sensors.md**, and is used by every system that attributes damage. Ids are that doc's `OwnerId` (a player, organisation, company, faction or authority; an AI ship's actor is its owner). A `HostileAct` is the Hostile-class view of it.

```
IncidentRecord, IncidentKind, IncidentClass: defined in factions-and-law.md ("The incident record" and its C# block; 64 B, server only).
Fields this doc fills: Tick, Actor and up to 3 co-actors (OwnerId), ActorHull, VictimOwner, VictimEntity, ZoneId (0 = none),
                       Kind, SourceEvent, EnergyJ (hull + shield), Class, Liable (OwnerId), PersonsHurt, PersonsKilled
Kinds this doc writes: Shatter, AmBlast, Detonation, Thrown, BeamPushed, Kicked, and PersonHarm for people hurt (D72)
Classes:               Hostile, Reckless, Accidental
```

- **Kinds from other docs:** `TractorLock` (D26: a lock on acquire, or a sweep's first delivered force, on another party's ship, grid or character without consent, even with no damage) and `TractorTear` (tractor-beams.md); `WarpBlast`, `WarpCollision`, `WarpBrakeCut`, `BeaconTamper` (warp.md); `StationCapture` and `StationDrop` (D27, Sieges below). There is no `Fined` class: a fine is a case state in factions-and-law.md.
- **Liable (D33):** who pays for the damage, separate from `Class` (intent). By default the actor. Debris of legal mining (made outside every `ProtectRadius`) that damages a station, its shield or anything inside its protected zone is **always the miner's liability**: `Liable` is the event's actor whatever the class (Accidental, Reckless, Hostile, or redirected by station defence). Class still sets the fine, standing and heat (factions-and-law.md).

- **Tracing (who the actor is):** every fragment, remainder, chunk, cloud and dust cloud carries its `SourceEvent` and that event's owner (the attribution rule in Abuse limits). A fragment of a fragment takes the later event's owner. A beam-touched body's `LastBeamOwner` (other than station defence) and a `Thrown` body's thrower take precedence over the event owner. **Review fix (framing):** `LastBeamOwner` is set by any party that redirects a dynamic asteroid body, not only by beams: rounds whose impulses change its velocity by ≥ 0.5 m/s within 2 s, a ship's hull contact (a nudge or a ram), and an AG kick. The field keeps its name. Without this, a griefer could steer someone else's fragment into a station with small-arms impulses or a hull shove, and the miner would be the actor. Shrapnel → the detonation's actor (Abuse limits: the top heat contributor, or a non-owner who put ≥ 10 % of the heat in), with the other contributors as co-actors. AG-kicked debris → the charge owner (exotic-matter's attribution), or the party whose hit breached or set off the charge (proposed to exotic-matter).
- **What counts:** hull or component damage, character injury, or **shield absorption** on anything whose owner is not the actor, the actor's party or a party the zone permits. A shielded station that took no hull damage was still attacked. Energy is summed per (actor, victim, source) over 1 s into one record, so a 16-piece shatter makes a few records, not hundreds. A record under `HostileMinJ` (1 MJ, hull + shield) is logged as Accidental whatever its class (review fix: a stray pebble on a 2 km L shell must not list anyone).
- **Class** (intent can't be read, so the server infers it from what the actor could see at the time). Review fix: the draft made any hit within 120 s on anything inside the 9.6 km forecast reach Hostile, so ordinary mining 8 km from another miner was a hostile act; and its fixed 120 s window let **aimed slow debris** escape (a rock thrown from just outside a 5 km zone at 32 m/s arrives after ~150 s and was Accidental, as was a 15 m/s large shatter piece). The decided rule is about *deliberate* damage, so:
  - **Hostile:**
    - every `Thrown`, beam-pushed or kicked hit whose path at release (the closed-form coast over `ReleaseHorizonS`) passed within `MissMarginM` of the victim or entered the protected zone it was in, **whatever the arrival time**; such a body keeps its `Thrown` tag, and its sweep, until that path no longer leads into the zone;
    - fracture debris that damages anything **inside a protected zone** within `HostileWindowS` (120 s), when the event was within `HostileReachM` (2 km) of the zone's edge (the zone is the standing "keep out" line, and the forecast flagged it); or that damages anything **outside zones** that was within `HostileReachM` of the event at its tick;
    - fracture debris whose own birth path (unredirected since) led onto the victim, under the same distance rules, whatever the arrival time;
    - a third Reckless record by the same actor against the same victim owner within 1 h (`RecklessEscalateN` 3): a pattern is deliberate.
  - **Reckless:** other damage within the window from events whose forecast reach (`ShatterVMax × HostileWindowS`, 9.6 km, for every fracture event including detonations: their fragments fly as far; the beacon's 2.25 km is shrapnel only) covered the victim or its zone at the event tick.
  - **Accidental:** after the window, outside the reach, or by debris that station defence pushed after the event (the station chose that path; the maker stays the actor). Logged; for in-zone and station damage, logged with the miner liable (D33).
  - **Came to the hazard (anti-farming, review fix):** a victim that moved to within `HostileReachM` of a rock after the actor's first site or fracture event on it in the last 10 min (the hazard beacon was up) gets at most Reckless, and those records never escalate. So parking a cheap hull next to someone's mining rock can't list them. This cap applies only outside protected zones, so D33 liability is unaffected.
  - **Liability (D33):** class decides the fine, standing and heat, not who pays. Station, shield and in-zone damage from legal mining's debris is the miner's liability in every class.
  - Self, party and permitted damage: no record.
  - Shattering a rock just outside a station's zone and hitting the station within two minutes, or throwing a rock that hits it at any time, is a hostile act, as decided; mining 5–10 km from someone who isn't in a zone is at worst reckless.
- **Consumers:** factions-and-law.md turns each flushed record into a case (fine, restitution, standing, heat), weighing class, victim and energy (and, for people hurt in caves, who was there first: D72). The interim station response that stood here (`HostileListS`, the 10 min Reckless listing) is removed:
  - listing is that doc's heat level: the zone beacon names everyone at Wanted or above;
  - docking refusal is its `ZonePermission.Check`, the one station permission hook (warp.md, tractor-beams.md and exotic-matter.md call the same one);
  - what stays here: `StationDefence` serves first the debris of an actor with a live Hostile record against this station (Priority, above);
  - Accidental records are logged, with the miner liable for in-zone and station damage (D33);
  - station turrets are combat-and-sensors.md's and fire on factions-and-law.md's response orders.
- **Physics never depends on it:** shields and deflection treat all debris alike, and attribution never makes damage go away.
- **Sieges and capture (D27):** factions-and-law.md "Sieges and capture" owns the rule: who may declare, notice, the Open state, the capture clock at the `CommandCore`, striking colours, and how hard destruction stays (`EvacGraceS`). The interim rules and the `CaptureTicks` knob that stood here are removed. What an Open siege means for this doc:
  - **Lifted, for besiegers only:** tractor-delivery zeroing toward the station and its zone, and zone courtesies (docking assist, consent).
  - **Kept:** the rock-working and weapon ban on asteroids inside `ProtectRadius` (no debris is manufactured inside a besieged zone either), the shield and debris defence, and incident records.
  - **On capture** this zone's shield and `StationDefence` pass to the new owner with the station. Cutting station power disables the shield and defence but never the anchor's containment feeds, and never transfers ownership.
  - **Debris during a siege:** rocks thrown or shattered at a besieged station are still records of their actor (Hostile when aimed). Between the siege's parties that doc's act-of-war disposition decides what they cost; a `StationDrop` stays an atrocity.

**Wire, tick order and costs.**
- Shield tests ride sweeps that already exist: the shrapnel and `Thrown` sweeps test the shell sphere instead of the station's bounding sphere; coasting bodies of any speed are caught by the scheduled-arrival test at coast start (Watch), which also serves undefended stations' shields (review fix: the 1 Hz coasting look-ahead runs only above 5 m/s, so slow coasting rocks crossed the shell unseen); and Jolt contacts use one sensor sphere per shield on the `AsteroidMoving` and `Debris` layers only (review fix: on `Moving` it would pair with every docked and visiting ship inside a 2 km shell every step), whose callback costs nothing until debris overlaps. A hit costs ~1 µs (settle, absorb, impulse, record), resolved in step 3 with the other damage.
- The "unredirected since birth" test for fracture debris (Class) is one flag bit per body, cleared when `LastBeamOwner` is set or a defence push releases it, so it costs nothing until a hit is classed.
- `ShieldState {station, pool, rate, tick}` reliable on change (clients extrapolate the lazy form; it is combat-and-sensors.md's `ShieldState` message with the station's shield core as the emitter); `ShieldHit {station, point oct, absorbed q8, leaked q8}` ~12 B to interest, for VFX. Defence beams use the tractor messages. `IncidentRecord`s never go on the wire: factions-and-law.md sends a `CaseNotice` (24 B, reliable) to the defendant, the victim's owner and the zone owner when a case opens, merges or settles.
- `IncidentRecord` accumulation ~0.2 µs per contribution, flushed at 1 Hz.

## Weapons against asteroids

Revision 2. Two user decisions (fixed): a big enough kinetic hit shatters an asteroid whatever its heat state, and energy weapons heat asteroids through the same heat model, but far less efficiently than the mining beam. Exotic-matter weapons (AM breaches, anti-graviton charges) are mapped onto the same server-owned fracture event model. Weapon stats themselves (classes, damage to hulls, rates of fire, ammo, heat, ranges) are owned by combat-and-sensors.md (section 1, "Weapons"; D56). This section defines only what each weapon does to rock, through the per-weapon `asteroid` block that doc's weapon defs carry. Revision 4 (user decision): a shatter at the threshold breaks the **whole body** (a crack through it, large fragments flung in different directions, and a dust cloud); below the threshold a hit makes a crater and cracks. The old regional shatter is gone: its energy range is now the sub-threshold **crack zone**.

### Kinetic hits: the shatter threshold

Any server-resolved kinetic hit on an asteroid body is a `KineticHit` (projectile, or a ram from the fast-transit sweep or a contact). Wedges are tools, not weapons: they keep the seat path only (`ShatterCoupling = 0`), so a cold wedge still ricochets.

Projectile energy is split three ways, by per-weapon data:

```
E_p      = 1/2 * m_p * |v_p - v_surface|^2                        rams: 1/2 * mu * v_rel^2 (reduced mass, as before)
E_crack  = min(E_cap, eta_k * E_p)                                eta_k  = 0.25  fracture surface (crack energy)
E_motion = xi_k * E_p                                             xi_k   = 0.30  fragment kinetic energy
                                                                  rest   = 0.45  crater vapour, plume, heat, escaping ejecta: lost
F(N)     = 4.91 * N^(1/3) + 6.28                                  the Fracture section's face factor
R_reach  = distance from the hit to the far side of the body along the shot (≈ 0.8 * D for the archetypes)
G_body   = volume mean of G(T) over the body (16 samples; G(T) already carries the (1 - 0.6 * crack) term, applied once)
E_split  = G_body * F(N_sh0) * R_reach^2 / eta_k                  N_sh0 = 10: THE shatter threshold in projectile energy (revision 4: whole body; cold site: U_th = Q_rel = 0)
E_crack  = min(E_cap, eta_k * E_p + U_th + gamma * Q_rel)         the site terms join as for wedges (0 with no site)
shatter  <=>  E_crack >= G_body * F(N_sh0) * R_reach^2            whole body (for a cold rock, E_p >= E_split unless E_cap binds)
below it:     crater (V_c), plus a crack of radius R_f = min(R_reach, sqrt(E_crack / (G_eff * F(N_sh0)))) if R_f >= 0.5 * R_min
G_eff    = volume mean of G(T) over the ball of radius R_sh round the hit (as for wedges)
R_sh     = max(R_min, zeta_sh * D / 2)                            zeta_sh = 0.25: the old smallest shatter region, kept as the crack-zone scale
E_th     = G_eff * F(N_sh0) * R_sh^2 / eta_k                      the crack-zone threshold (revision 4: was the regional shatter threshold)
crack zone  <=>  E_split > E_p >= E_th                            the crack also leaves a DamageZone (zeta_dz * R_f): rock opened for wedges and caves
```

- **Size scaling:** `E_split ∝ G · D²` (and `E_th` likewise). The threshold grows with size and toughness, while the threshold per kilogram of the body falls as 1/D, as in real strength-regime disruption. The smallest rocks (2 m) need 1.4–3.8 MJ cold (ice-rock to silicate; metal 10 MJ), so small arms never shatter rock.
- **What a shatter breaks (revision 4).** Always the whole body: a crack through it from the hit to the far side, 8–16 pieces of which 2–4 are large and flung outward in different directions, fines, and a dust cloud (see "Whole-body shatter", below). **Below the threshold** a hit craters and cracks: from `E_th` up the crack is a **crack zone** (the old regional shatter's energy range, now with no pieces) that leaves a damage zone; below `E_th` it is a plain crack, as before. The forecast shows which.
- **Heat lowers it, but is not needed:** `G_body` and `G_eff` use the heat grid where a site exists and `w(T_bulk)` elsewhere, so a warmed rock shatters more easily (down to `w_min` = 5 % of the cold threshold at `T_melt`). Cold rock works at the full threshold, which is the user's "regardless of the heat window". A laser-warmed small rock shot with a gun instead of a wedge therefore shatters easily, but as a shatter (no blade, fines lost), so the wedge still pays.
- **Cracks lower it, with a floor:** crack records do not stack. A point inside any record has `crack = 1` (G × 0.4); outside all of them, 0. A fully cracked body has `G_body = 0.4·G`, so **hits below 0.4·E_split never shatter a cold rock**, however many there are. A hit of roughly 0.7·E_split or more shatters on the second hit, because the first one's crack (radius ≈ `sqrt(E_p/E_split)·R_reach`) weakens enough of the body; hits between 0.4 and ~0.7·E_split need several records tiling the body (≤ 8 per body). The 0.7 is an estimate for M5b to measure.
- **E_cap binds the bigger rocks.** `E_crack ≤ 500 MJ` gives `R_max = sqrt(E_cap / (G0 · F(N_sh0)))` (61 / 49.7 / 36.7 / 22.2 m for ice-rock / carbonaceous / silicate / metal). A whole-body shatter needs `R_reach ≤ R_max` at `G_body`: one hit on a cold rock breaks at most D ≈ 76 m of ice-rock, ~62 m carbonaceous, ~46 m silicate and ~28 m metal; a rock already cracked through (`G_body = 0.4·G`) up to ~120, ~98, ~73 and ~44 m. Bigger rocks are never shattered by weapons. They crater and crack (crack zones of up to `R_max` radius, so `D ≤ 8·R_max` still bounds where one hit reaches `E_th`: ~487 / 398 / 294 / 178 m), and are mined: a crack zone has `G` and `H` × 0.4, so wedges seat there sooner and EVA crews can dig it. "Guns crack, wedges split."
- **Rail asteroids outside every bubble** (a long shot at an unrealised rock): the server realises the parent for the event only, and the pieces are born **Coasting** (closed-form Hill motion, 0 per tick) if no player is within 500 m, with their directions clamped outward from the parent, since coasting has no collision. They enter Jolt by the normal 400 m rule.
- **Loose chunks** (0.5–8 m) are not fractured by weapons. A hit at or above the chunk's `E_th` (with `0.2·G0`, as loose wedges use `0.2·H0`; unchanged by revision 4, since it already takes the whole chunk) turns the whole chunk into cloud mass, `FinesLost` (half) of it lost; below it, an impulse only. This removes bodies instead of adding them, needs no fracture evaluation (~1 µs) and is not a fracture event. It is still worse than tractoring the chunk whole (100 %).

**Crack-zone threshold `E_th` by size (cold, uncracked; E_cap-limited cells marked). Revision 4: this was the regional shatter threshold; a hit between it and `E_split` now leaves a crack zone and no pieces. "never": one hit can't reach `E_th` even at `E_cap`, so it lays a plain crack of radius ≤ `R_max`:**

| D | R_sh | Ice-rock (G0 8e3) | Carbonaceous (1.2e4) | Silicate (2.2e4) | Metal (6e4) |
| --- | --- | --- | --- | --- | --- |
| 4 m | 0.75 m | 0.30 MJ | 0.45 MJ | 0.83 MJ | 2.3 MJ |
| 16 m | 2 m | 2.2 MJ | 3.2 MJ | 5.9 MJ | 16 MJ |
| 64 m | 8 m | 35 MJ | 52 MJ | 95 MJ | 259 MJ |
| 256 m | 32 m | 0.55 GJ | 0.83 GJ | 1.5 GJ | never (R_sh > R_max = 22 m) |
| 400 m | 50 m | 1.35 GJ (R_max = 61 m) | never (R_max = 49.7 m) | never (R_max = 36.7 m) | never |

**The shatter threshold `E_split` (revision 4: the only shatter; whole body, cold, uncracked, `R_reach ≈ 0.8·D`, N = 10: `4 · G0 · F(10) · (0.8 D)²`):**

| D | Ice-rock | Carbonaceous | Silicate | Metal |
| --- | --- | --- | --- | --- |
| 2 m | 1.4 MJ | 2.1 MJ | 3.8 MJ | 10 MJ |
| 4 m | 5.5 MJ | 8.3 MJ | 15 MJ | 41 MJ |
| 8 m | 22 MJ | 33 MJ | 61 MJ | 166 MJ |
| 16 m | 88 MJ | 133 MJ | 243 MJ | 663 MJ |
| 32 m | 354 MJ | 530 MJ | 972 MJ | never by one hit (past ~28 m) |
| Largest by one hit (E_cap) | ~76 m | ~62 m | ~46 m | ~28 m |
| Largest by one hit, cracked through (`G_body = 0.4·G`) | ~120 m | ~98 m | ~73 m | ~44 m |

So a railgun slug (160 MJ) shatters a whole silicate rock up to ~13 m and a mass-driver slug (40 MJ) up to ~6.5 m. Bigger rocks are cratered and cracked, never broken in part (revision 4).

**What the kinetic classes do to rock.** Round 6 (D56): the weapons themselves are combat-and-sensors.md's ("Kinetic: autocannon to capital mass driver"), which replaces the placeholder classes this table used to define. Its four energies are the ones used here, so every threshold above stands. Round mass, speed and `E_p` are quoted from that doc for convenience and it wins if they change; only the two outcome columns are owned here. The old "kinetic kill vehicle (capital)" is the capital mass driver's shell, so a 2.25 GJ capital kinetic weapon is in scope.

| Weapon (combat-and-sensors.md id) | m_p, v | E_p | Shatters whole, silicate / metal (cold) | Crack zone (≥ `E_th`), silicate / metal |
| --- | --- | --- | --- | --- |
| Autocannon `autocannon.s` | 0.4 kg, 1.2 km/s | 0.29 MJ | nothing, even cracked (2 m floors: silicate 1.5 MJ, ice-rock 0.55 MJ) | nothing cold (ice-rock floor 0.30 MJ); cracked 2–6 m ice-rock and carbonaceous (floors 0.12 and 0.18 MJ) after a few hits; metal nothing |
| Wedge.Heavy (tool, for comparison) | 60 kg, 250 m/s | 1.9 MJ | never: wedges take the seat path only | never |
| Mass driver `massdriver.m` | 20 kg, 2 km/s | 40 MJ | D ≤ 6.5 m / ≤ 3.9 m | D ≤ 42 m / ≤ 25 m |
| Railgun `railgun.l` | 5 kg, 8 km/s | 160 MJ | D ≤ 13 m / ≤ 7.9 m | D ≤ 83 m / ≤ 50 m |
| Capital mass driver `massdriver.xl` (the old "kinetic kill vehicle") | 500 kg, 3 km/s | 2.25 GJ | D ≤ 46 m / ≤ 28 m (E_cap binds; ≤ 73 / 44 m cracked through) | D ≤ 294 m / ≤ 178 m (E_cap binds) |
| Ram, 300 t ship at 100 m/s into a big rock | μ ≈ 3e5 kg | 1.5 GJ | D ≤ 40 m / ≤ 24 m | D ≤ 254 m / ≤ 154 m |

**Below the threshold** a kinetic hit makes a crater, and maybe a crack or a crack zone (revision 4: this now covers the old regional shatter's whole energy range):
- **Crater:** `V_c = eta_c · E_p / H(T)` with `eta_c = 0.5`, accumulated per body like `V_abl`, and emitted as a server cut (`BrushOp`, radius `r_op`) once it reaches `V_op` (≤ 4 ops/s per body, shared with ablation). An autocannon round digs ~0.36 L of silicate, so roughly 500 rounds make one 0.35 m op on a small rock (~1,450 for a 0.5 m op on a 16–64 m rock, ~11,600 for a 1 m op on a bigger one).
- **Crater mass is mostly lost:** 90 % vapour and escaping ejecta, and 10 % goes to the body's grit cloud (the nearest cloud of that body, or a new one under the cloud cap's merge rule). This is deliberate: sandblasting with small arms must not be a mining tool.
- **Crack:** if `R_f ≥ 0.5 · R_min` (solved with N_sh0 over `G_eff`, capped at `R_reach`), a `CrackRecord` of radius `R_f` is laid at the hit, exactly as the wedge Crack result.
- **Crack zone (revision 4):** if also `E_p ≥ E_th`, the crack also leaves a `DamageZone` of `ζ_dz · R_f` (Damage zones and caves). No pieces, no fracture bake: ~2 µs and 32 B of edits. It counts against the shooter's 6 fracture events a minute; over that, the crack record is laid without the zone. Crack records (≤ 8) and zones (≤ 64) per body keep their caps.
- **No freeing:** projectile impulses never turn a static or kinematic natural asteroid dynamic. Only contacts, tractor locks and fractures do. Stray fire therefore cannot wake a field.

### Shatter fracture versus wedge fracture

A shatter is `FractureKind.Shatter`, and since revision 4 it is always whole-body (the Split path: the region is the whole body, `R_f = R_reach`). It is evaluated by the shared Fracture code (same Voronoi-on-SDF, same server-sent records, same caps); only its inputs differ:

| | Wedge (laser + wedge) | Shatter (kinetic weapon or ram; revision 4: whole body) |
| --- | --- | --- |
| Needs the window | Yes: seat test on `H(T_aim)` | No: energy threshold on `G_body` (`E_split`) |
| Crack energy | `η_w·E_k + U_th + γ·Q_rel` (η_w 0.6) | `min(E_cap, η_k·E_p) + U_th + γ·Q_rel` (η_k 0.25) |
| Sites | Two primary sites on the blade plane (player-chosen roll), then N − 2 clustered at `0.5 + 0.5|u|` | No blade. **A crack through the body:** two primary sites at `c_b ± 0.5·R_b·n_p`, with `c_b` the body's centre of mass, `R_b` its bounding radius and `n_p` normal to a plane through the hit, the shot axis and `c_b` (roll from the hash). Then N − 2 sites spread over the whole body at `c_b + R_b · u_k · (0.3 + 0.7|u_k|)`, not clustered at the entry, so pieces come out large |
| N | `N0 = 6`, up to 16 on a split | `N = clamp(N_sh0 + 2·floor(log2(E_crack / E_frac(R_reach, N_sh0))), 8, 16)` |
| Size distribution | One clean face, 2–3 large pieces; largest ~35–50 % of the region. Mostly boulders and big chunks | One rough through-crack; 2–4 large pieces (largest ~25–45 % of the body), the rest chunks (0.5–8 m), plus fines and dust |
| Fines | Grit `0.05 m · ρ · A_crack` (~5–10 % of the region), as a slow cloud at the parent's velocity. Recoverable | `f_fines = clamp(0.15 + 0.05·log2(E_p / E_split), 0.15, 0.40)` of the body. Half of it (`FinesLost` 0.5) is lost as yield at once: half of that as vapour and escaping ejecta, half (`DustFrac` 0.25 of the fines) as a **dust cloud** that stays as fog for minutes (Whole-body shatter, below). The other half is a grit cloud expanding at `max(2 m/s, 0.5·v̄)`; the event's crack grit joins it: one grit cloud and one dust cloud per event |
| Fragment energy | `χ·min(S,1)·Q_rel + max(0, E_crack − E_frac)` | `min(E_cap, ξ_k·E_p + χ·min(S,1)·Q_rel + max(0, E_crack − E_frac))` |
| Velocities | `v̄ · (m̄/m_i)^(1/6)`, dir `x_i − c + 0.5·R_f·n_out` | `v̄ · (m̄/m_i)^(1/3)` (small pieces fly much faster), **dir `normalize((x_i − c_b) + 0.3·R_b·â_shot)`** plus a hashed 30° jitter: outward from the body's centre in every direction, biased downrange, so the large pieces fly apart rather than down one cone. `v_sep = 1 m/s`; every piece capped at `ShatterVMax` (80 m/s), the excess lost. `E_kin` is shared in the centre-of-mass frame with a dynamic parent's recoil, which is capped the same way |
| Fragments born with | The parent's cracks in their bounds | The parent's cracks, plus a crack record at their centre: shattered boulders are easier to break again, and easier to break badly |

**Worked examples (silicate, cold, ρ 2700; revision 4):**
(v̄ below is over the pieces, after the fines are taken out.)
- **Mass driver into a 16 m rock (40 MJ; E_th 5.9 MJ, E_split 243 MJ):** below the threshold. A crater of ~0.05 m³ and a crack zone: R_f ≈ 5.2 m and a 7.8 m damage zone. No pieces. Even cracked through it would need ~97 MJ (0.4·E_split), so a mass driver never shatters this rock; a railgun (160 MJ, ~0.66·E_split) does after a few cracking hits.
- **Railgun into a 64 m rock (160 MJ; E_th 95 MJ, E_split ~3.9 GJ):** below the threshold. A crater of ~0.2 m³ and a crack zone: R_f ≈ 10.4 m and a 15.6 m damage zone, where a miner's wedge now seats sooner (`H` × 0.4) and EVA crews can start a cave. No pieces.
- **Railgun into an 8 m rock (160 MJ, E_split 61 MJ):** a whole-body shatter of ~3.7e5 kg (with the archetype's axis scales) into N = 12–16 pieces. f_fines = 0.15 + 0.05·log2(160/61) ≈ 0.22: ~8e4 kg, of which ~4e4 kg is a grit cloud, ~2e4 kg is vapour and ~2e4 kg is dust. E_kin ≈ 70 MJ (48 MJ plus the ~23 MJ of crack energy left over after the split) over ~2.9e5 kg of pieces gives v̄ ≈ 22 m/s: the 2–4 large pieces (5e4–1.2e5 kg) fly apart at ~13–17 m/s in different directions, and most small pieces hit the 80 m/s cap. The dust cloud blinds optics inside it for about a minute and clears in ~5 minutes (below). Overkill on small rocks is still the most wasteful case, as intended.
- **Capital mass driver shell (the old "kill vehicle") into a 64 m rock (2.25 GJ; ~1.9e8 kg with the archetype's axis scales, above `KinematicMassLimit`, so static):** E_crack hits E_cap (500 MJ), but a whole-body shatter needs ~970 MJ of crack energy (R_reach ≈ 51 m). So the first hit is a crater (~2.8 m³) and a crack zone of R_f ≈ 35 m (a 52 m damage zone, clipped to the body), which cracks about a third of it. Once two or three hits have cracked ~80 % of the body (`G_body` ≤ ~0.51·G), the next one shatters all of it: f_fines ≈ 0.16 (~3e7 kg, ~8e6 kg of it dust), E_kin capped at 500 MJ over ~1.6e8 kg of pieces gives v̄ ≈ 2.5 m/s, the largest pieces (4–7e7 kg, so dynamic) drift apart at ~1.5 m/s, and small pieces still reach the 80 m/s cap. The dust cloud blinds optics across 1–2 km for most of its 10-minute life: a capital shot leaves a smoke screen. Each of those hits counts against the shooter's 6 events a minute.
- **Compare a wedge cycle** on the same silicate (vented to S = 0.05): R_f ≈ 3.6 m, v̄ ≈ 1.6 m/s, ~5–10 % grit, all of it slow and recoverable.

**Yield, so mining skill still pays** (an estimate the M5b sandbox must measure):

| Path | Mass lost at once | Mass at risk (fast pieces leave the bubble and despawn as chunks or clouds) | Typical recovered |
| --- | --- | --- | --- |
| Laser + wedge, held in the window | Ablated vapour only (~1–2 %) | Small: v̄ 1.6–4.5 m/s | ~90–95 % |
| Laser + wedge into Critical, or a detonation | ~0 | Large: 40–80 m/s pieces, shrapnel | ~40–70 % |
| Kinetic shatter (whole body, revision 4) | 7–20 % (fines: vapour and dust) + crater vapour | Medium to large: v̄ 2–7 m/s near the threshold, ~22 m/s on an overkilled small rock; large pieces flying apart in every direction; small pieces 15–80 m/s; grit cloud spreading at ≥ 2 m/s; dust hides the pieces from optics for minutes | ~55–80 % (lower for overkill) |
| Guns crack, then laser + wedge (revision 4) | Crater vapour + the wedge cycle's | As laser + wedge | ~85–95 %, and the wedge seats sooner in the crack zone |
| Small-arms cratering | 90 % of each crater | n/a | ~10 % of what is removed |

EM in a shattered body is not heated, so it is not decayed; it is simply scattered with the ore, and EM in lost fines (vapour or dust) is lost.

### Whole-body shatter: dust cloud (revision 4)

User decision: a whole-body shatter throws up a lot of dust, a local fog cloud that cuts visibility and sensors for a while. It reuses the ring's dust fog model (Fog and sensors), as a sphere. Every Shatter, and every AmBlast (which follows the shatter rules), makes one `DustCloud`:

```
M_d    = DustFrac * f_fines * M_body             DustFrac 0.25: half of the lost fines; booked in the event's Lost bucket (mass check unchanged)
r(t)   = r0 + v_d * (t - t0)                      r0 = D/2;  v_d = max(DustVMin 3 m/s, 0.5 * v̄)
sigma  = k_d * M_d / (4/3 pi r^3) * fade(t)       k_d = DustMassExt 50 m²/kg (optical; ~10 µm grains); per sensor kind × kappa_dust (ring-obscuration.json)
fade   = 1 until (2/3) DustLifeS, then linear to 0 at DustLifeS (600 s): it settles and shears out along the lane
tau_c  = 2 r sigma = 3 k_d M_d / (2 pi r^2) * fade       through the centre; the cloud despawns at tau_c < 0.05 or at DustLifeS
```

- **Deterministic and free on the wire:** centre and velocity are the body's, so the cloud is a function of the `FractureEvent` (+8 B: `M_d` and `v_d`) and the tick. The centre follows the closed-form coast of the parent's velocity (Hill in a lane, straight in a field), like a rubble cloud's. No further traffic.
- **Not owned by a bubble (review fix):** a cloud reaches r ≈ 2–3 km, past a bubble's 2 km soft radius, and sensor segments run up to 50 km across bubbles, so a per-bubble list missed clouds next door. Live clouds sit in one server-wide list, bucketed by 4 km cells of their lane or field frame (re-bucketed at 1 Hz, they move slowly), capped at `DustCloudsMax` 256 per server; past it, the oldest cloud is merged into its nearest neighbour.
- **Sensors:** `RingObscuration.OpticalDepth` adds each cloud's chord × σ × κ_dust,s along the segment (a segment–sphere test, ~20 ns per cloud), visiting only the clouds in the 4 km cells the segment crosses (typically 0–3). Optics and IR go blind inside, thermal halves, radar barely notices (κ_dust 0.02) and signature sensors ignore it: a choked band, local and brief. A station whose watch is optical-only loses track of the pieces inside it (Station defence).
- **Rendering:** drawn in the haze pass as a sphere with the band's dust colour and phase and the same density law, ≤ 8 in view, ~0.01–0.03 ms GPU; the impostor fog cull counts it.
- **Not a body:** no hull damage, no yield (intakes don't take µm dust), no tractor force, no shield cost.
- **Caps:** ≤ 8 dust clouds within 4 km of any point (a new cloud past that merges into the nearest), ≤ 256 per server. A merge sums mass, keeps the larger current radius as `r0` from the merge tick, and takes the **mass-weighted** birth tick (review fix: "the newer birth tick" let a trickle of small shatters renew a big cloud's ten-minute life indefinitely; now a cloud lasts as long as the shooting that feeds it, plus at most `DustLifeS`). A screen kept up next to a station costs its makers their fracture-event rate (≤ 6 a minute each) and blinds only optical watches.
- **Examples (silicate):**
  - Railgun into an 8 m rock: M_d ≈ 2e4 kg, v_d ≈ 11 m/s. At 10 s, r ≈ 115 m and σ ≈ 0.16 /m: ~20 m of visibility, blind inside. At 60 s, r ≈ 670 m: ~3.6 km of visibility, a transmittance of ~0.33 straight through. Gone (τ_c < 0.05) after ~4.5 min, at r ≈ 3 km.
  - Capital mass driver shell shattering a cracked 64 m rock: M_d ≈ 8e6 kg, v_d = 3 m/s. At 5 min, r ≈ 930 m and visibility ~25 m; at the end of its 10 min life, r ≈ 1.8 km and ~200 m before the fade. Blind throughout.

### Energy weapons: heat coupling

Energy weapons feed the **same heat grid** through the same `Q_las` term, scaled by a per-weapon coupling `c_w`. The mining beam is the reference (`c_w = 1`): a continuous, defocused spot matched to rock. Weapon beams are pulsed and tightly focused for hulls, so most of their energy goes into a vapour plume, reflection and a deep narrow pit.

```
Q_wpn  = alpha(m_c) * c_w * P_wpn * W_c * dt                  same grid, same substeps, same S
         spot radius r_w from the weapon (small), so cells under it reach T_melt fast and ablate: more vapour, no yield
         particle beams spread Q over the first DepositDepth along the beam (2 m), not the surface cell: less venting
P_abs  = sum over all sources on a site per heat step <= P_siteMax (10 MW absorbed): plume shielding; the excess is lost
```

Round 6 (D56): the weapons in this table are combat-and-sensors.md's ("Energy: lasers, particle beams, plasma"; the lance and warheads are exotic-matter.md's). Bus power, power at the target, spot and range are that doc's and win if they change. This doc owns the coupling `c_w`, the deposit depth and the notes; that doc keeps the couplings and wall-plug figures listed here.

| Heat source | `c_w` | Spot | Bus → absorbed (silicate, α 0.8) | Notes |
| --- | --- | --- | --- | --- |
| Mining BeamLaser (3 MW bus, η 0.5) | 1.0 | 0.3 m + 1 mrad | 0.40 W per bus W | The reference. Throttle holds a window |
| Pulsed laser `laser.s/m/l` (wall plug 0.3) | 0.20 | 0.05 m | 0.048 (~8× worse) | No throttle hold: pulses overshoot the window |
| Particle beam `pbeam.m/l` (wall plug 0.25) | 0.30 | 0.1 m, deposits to 2 m depth | 0.06 | Interior heating vents poorly: S rises fastest per joule absorbed. α is not a surface property for particles; the 0.8 is kept as a tuning factor so one formula serves |
| Plasma cannon bolt `plasma.s/m` (5 / 50 MJ) | 0.10 | 0.3 / 0.5 m (the weapon's) | per bolt energy × 0.08 | One-tick deposit |
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
  2. **Fracture:** a `FractureKind.AmBlast` centred on the crater, resolved with the shatter rules (no blade, shatter fines and velocities) from `E_crack = min(E_cap, η_am · E_AM)` and `E_kin = min(E_cap, ξ_am · E_AM)`, with `η_am = 1e-4`, `ξ_am = 2e-4` and `E_AM = m_AM · AmEnergy`. One gram gives 100 MJ and 200 MJ; `E_kin` saturates at E_cap from 2.5 g and `E_crack` from 5 g. The fracture region is measured from the crater rim, so the crater and the fracture are one cut record. Revision 4: like a kinetic shatter, an AmBlast is whole-body or nothing. It breaks the body (through-crack, large pieces, dust cloud) only if `E_crack` reaches `G_body · F(N_sh0) · R_reach²`, with `R_reach` from the rim (1 g of AM: silicate up to ~20 m; at `E_cap`, ~46 m). Otherwise the crater stays and the rest is a crack zone of radius `R_f` from the rim, with no pieces. Over the rate limit, with the bubble saturated, or with the per-body pending event taken, the AmBlast is dropped and only the crater (and the heat below) remain.
  3. **Heat:** the crater rim shell (one dx) is set to `T_melt` in an existing or newly opened site. A volatile-rich rock usually crosses S = 1 at its next heat step and **detonates on a later tick**: cascades advance one hop per tick, as exotic-matter rules.
  4. **No EM chain from ore (a proposal for exotic-matter to own; its chain rule has no purity floor today).** Exotic-matter's same-room chain (an AG pulse of yield `0.25 · min(c_EM, m_AM/AmPerEm)`) would need EM at q ≥ 0.15, the charge `MinPurity`. Until exotic-matter accepts the floor, every EM-bearing asteroid chains a small pulse (1 g of AM caps the yield at 0.05 kg). Lens ore (q ≤ 0.03, ≤ 0.3 near black holes) is below it except in the richest black-hole lenses; those chain a pulse on the next tick with `c_EM` = the content inside the crater.
  5. **Dose:** the dose pulse is exotic-matter's unchanged. Asteroids block line of sight: realised bodies by their bounding inner sphere, unrealised ones are ignored (dose is long-range and rails are cheap scenery, an accepted error).
- **Standoff** (farther than `r_am`): no crater and no fracture (no blast in vacuum). Only the flash heat row above, and the dose.

### Anti-graviton charges

Exotic-matter's `PulseKick` gives every mass the same Δv, with damage only from the gradient, and makes no terrain edits. Applied to asteroids:
- **Every asteroid body that is not Dynamic or Coasting is on the pulse skip list**, like static grids and terrain: rail asteroids, realised naturals that are static or kinematic, re-railed spawned bodies (layer `AsteroidRail`), and the permanently static bodies above `KinematicMassLimit`. They are ring structure, not bodies: kicking them would rewrite thousands of rails (a 1 kg charge's 1 km sphere holds ~500 asteroids ≥ 2 m at peak density, a 100 kg one's 4.6 km sphere ~1e4). Ships kicked into nearby rocks hit them as ordinary rams, the same "kicked into terrain" behaviour exotic-matter calls intended.
- **Dynamic asteroid bodies** (fragments, freed rocks, chunks) get the kick like any body, inside the shared per-body kick budget. Coasting debris gets it as an element update (the rails path), with no re-entry into Jolt.
- **Rubble clouds** get the Δv at their centre added to their velocity (~10 ns each). Their radius is unchanged. Bodies inside a powered gravity damper's radius are skipped, as exotic-matter rules for everything.
- **Kicked debris and protected zones (revision 4):** a pulse can throw fragments at up to the 100 m/s kick cap. They stay real everywhere: inside a protected zone they meet the station's shield and deflection, and whatever they damage is an `IncidentRecord` of the charge's owner (Station defence and hostile acts). A station's gravity damper (exotic-matter) already stops pulses inside its radius, so kicked debris reaches a station only from a pulse outside it.
- **No fracture, no edits, no heat:** a pulse never breaks, cuts or warms rock, and never changes S. Charges are not mining tools; the explosive mining paths are kinetic shatter and contact AM.
- **An armed charge's physics anchor** (`A = max(2 km, R + 200 m)`) does not realise rail asteroids, since the pulse skips them. Only the normal near sphere around the charge casing applies, so arming in a dense band costs nothing extra.
- **Hot dynamic boulders can be kicked** while they carry a heat site. That is a moving bomb, and the hot-body tractor rule cannot stop it. Mitigation: a kicked body with a site whose S > 0.3 vents (k_crack + 0.5/s for 5 s) from the gradient shock. This is an open question below.

### Weapons in the mining state machine

Weapons enter the site machine at three points. The existing table is unchanged; these transitions are added:

| From | Event | To | Notes |
| --- | --- | --- | --- |
| No site | Weapon heat on the body ≥ `E_open` within 1 s | Cold (site opened at the hits' centroid, owner = shooter) | Below `E_open`: bulk node only, capped below `T_soft` and `T_vap` |
| Any heat state | Weapon heat | Same rules as the beam | `P_siteMax` caps the summed absorption. Cooling starts after 2 s with **no heat input of any kind**, not only no beam |
| Any heat state, or no site | `KineticHit` with `E_crack ≥ G_body·F(N_sh0)·R_reach²` (revision 4: whole body) | Fracture (Shatter) | The site's `Q_rel` and `U_th` join the crack and kinetic energy, as a wedge would, and the site is freed. A shatter into Critical is therefore as violent as a wedge into Critical. If the event is queued, the site keeps stepping until it resolves; a detonation in between replaces it (the hit then takes the crater path) |
| Any heat state | Sub-threshold `KineticHit` in the hot zone (crater, crack or crack zone) | Same state | A crater raises `A_open` and a crack raises `k_crack`: venting rises and S drops |
| Any heat state, or no site | Contact AM breach | Fracture (AmBlast), then the rim shell's heat at the next heat step | Can lead to Detonation one or more ticks later |
| Any | AG pulse | Unchanged | Pulses never add heat or S. The only effect is the hot-kick vent on a kicked Dynamic body with S > 0.3, which lowers S |

The thermal imager's grade-3 forecast gains a **kinetic row** for the aimer's selected weapon: Crater / Crack / Crack zone / Shatter (whole body; revision 4), plus `E_th` and `E_split` at the aim point, and a warning when the predicted fragment reach (`ShatterVMax × HostileWindowS`) covers another party or a protected zone (revision 4: such a shot is a hostile act if it hits). It is computed at 5 Hz, server-side, for the aimer only, like the wedge forecast.


## Tools as components

All are ship-hulls `ComponentDesign` function cores, data-driven in `data/components/mining/*.json`. Power is the flight model's scalar.

| Core | Key stats (defaults) | Notes |
| --- | --- | --- |
| BeamLaser | PowerMW 3, Efficiency 0.5 (beam 1.5 MW), throttle 0–100 %, SpotRadius0 0.3 m, Divergence 1 mrad, Range 400 m | One site per laser. Aims at a body-local point the turret tracks on spinning rocks (revision 3). The server finds the hit point by sphere-trace at 10 Hz. Waste heat `(1−η)·P` is reported for a future ship thermal system |
| WedgeLauncher | Muzzle 250 m/s, Reload 3 s, Magazine 6, blade roll chosen at fire | Ammo items: `Wedge.Std` (20 kg, 0.01 m², η_w 0.6) and `Wedge.Heavy` (60 kg, 0.012 m², 0.7; for metal). Recipes in `data/items/wedges.json`, from refined ore |
| ThermalImager | Grade 1: surface glow. Grade 2: site summary (T_site, S bar, state). Grade 3: 16³ slices, time to S = 1 at the current power, the **wedge forecast** (Ricochet / Crack / Spall / Split, predicted v̄), and the **kinetic forecast** for the selected weapon (Crater / Crack / Crack zone / Shatter, `E_th` and `E_split` at the aim point, plus the zone and bystander warning; revision 4) | The server computes forecasts at 5 Hz, for the aimer only |
| CompositionScanner | Power 50 kW, 4 s per scan, range 200 m. Grade 1: bulk mix. Grade 2: 8³ map of a 32 m sphere (or the whole body ≤ 64 m), EM ≥ 1 m. Grade 3: 16³, EM ≥ 0.5 m | The server evaluates `MaterialAt` (4096 samples, ~0.1 ms job) and sends `AsteroidComposition` / `DepositRecord` to the scanner's group (exotic-matter.md privacy) |
| TractorBeam | Class S of tractor-beams.md: 200 kN, Range 150 m, PowerMW 0.5 (0.35 base + 1 MW/km), defaults to Lock-Hold | Defined in tractor-beams.md (cone, Hold/Haul/Sweep, EM billing on emitted output; AM only for Push, Hold assist and Overdrive: D28, without AM it only pulls). A Hold lock is a per-hit spring-damper, so it despins fragments across the beam. Locks free static or kinematic rocks under `KinematicMassLimit` (which go dynamic, spin kept); sweeps never do. Capped by `BeamVMax` and `W_b ≤ EThrow`; throws over 50 MJ are `Thrown` (Manipulation). Vacuum mode is a wide pull sweep on the intake mouth. Subject to the abuse limits |
| Intake | Mouth 1, 2, 4 or 8 m; Throughput 500 kg/s; SweepArea 12 m² | Swallows only Loose bodies with D ≤ mouth, plus cloud mass. Packed bodies, including whole natural rocks, jam it (refused). Output is ore items per material; EM-bearing mass becomes an `em` stack with server content (exotic-matter.md) |
| MiningDrill | PowerMW 1, GrindEnergy 2e3 J/kg (500 kg/s) | Loose bodies only. Mass goes straight to cargo |
| CaveDrill (D74, round 6) | PowerMW 0.05, cold, 0.1 m³/s at silicate hardness (scaled like the HandDrill), op radius 0.75–1.5 m, contact reach 3 m, charge setter for `SplitterCharge` items; `regulated: true` | Ship-mounted, or on a small grid (a tunnelling vehicle). Only inside damage zones or on Fractured or Loose bodies (the Diggable test). Removed volume accumulates and emits a server dig op when it fills one (0.75 m: 1.8 m³, every ~18 s). Output goes to the grid's intake or cargo, or to `OreSack`s. ≤ 2 working per body. In a held band it needs a band permit with `Tunnel` (factions-and-law.md); used without one, its ops still run and are counted toward `IllegalDig` there. As a weapon it is a contact drill (combat-and-sensors.md) |

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

**Effectively infinite.** The rocky reference ring holds ~5e8 rocks ≥ 2 m, and a gas giant ring ~2e10. A busy server finishing 10k mining cycles a day consumes ~1–3e4 natural rocks a day (parents, plus rocks freed by locks or shattered by gunfire; re-railed pieces are spawned bodies, not naturals). That takes 1 % of the rocky ring in ~5–17 months and all of it in ~45–140 years; a gas giant ring lasts ~40× longer. (Review fix: the earlier "~45 years to take 1 %" was 100× off.) A belt of 100–2000 fields holds ~1e8–1e9 bodies (a Plummer field holds `n0 · (4π/3) · a³`: ~6e5 at a = 10 km and peak ring density, ~5e3 at a = 2 km), so belts are effectively infinite as a whole, while a small field can be mined out; field capture events add a few bodies to existing fields. What does run out is the rock **near stations**: cells there deplete and push miners outward. Replenishment events do not refill them in practice (the depletion weight below only nudges placement among ~1e5 candidate windows, and D73 keeps events small); they quietly top up a few cells elsewhere.

**Replenishment events (revision 3; made modest by D73, round 6).** User decision: nothing dramatic, things like asteroid captures. The revision 3 draft's comet breakups and moonlet shatterings, its daily 200–2,000-body collisions, its flash and dust-shell spectacle and its system-wide "gold rush" announcement are withdrawn. The machinery (footprints, event serials, takeover) is unchanged; the kinds, sizes, rates and visibility are:

| Kind | Rate (default, played time) | Footprint | Bodies | Composition | Signature |
| --- | --- | --- | --- | --- | --- |
| Capture (a stray rock and its companions settle into a lane) | 1 per 2 days per ring (gas giant ring 1 a day) | 1–2 lanes × 4–16 class-0 cells (0.5–1 km × 2–8 km) | The footprint at the band's ordinary density, plus 1–12 newcomers of classes 3–6 | Newcomers: drawn from the system's belt bands (a foreign rock may differ from the host band). The rest: the band's | None beyond the rocks themselves |
| Minor collision (two rocks grind into rubble) | 1 a week per ring (gas giant ring 2) | 2–4 lanes × 16–48 cells (1–2 km × 8–25 km) | The footprint at the band's ordinary density, plus 50–300 of classes 0–4 | The band's | A faint dust patch: τ_D × 1.5 in the footprint, decaying over 2 h. No flash |
| Field capture (belts) | 1 per 2 weeks per belt | A cell block inside an existing field. No new fields | 5–50 | The belt band's | None |

- **Schedule:** events are drawn from a server-only `EventSeed` (like exotic-matter's `NaturalSeed`): per ring and kind, the next event tick is `last + interval`, with the interval from a hashed exponential table (integer, no `exp`). Nobody can predict events from the universe seed. Rates are data. One heap event per ring and kind; nothing runs per tick.
- **Not announced (D73):** there is no system message, no map marker and no HUD notice. A `RingEvent` record reaches a client only when its footprint enters that client's draw range, because the client needs it to instance the cells. Players find fresh rock by flying past it or scanning it, like any other rock. An event is worth a few minutes of one ship's mining to a few hours, not a server-wide rush.
- **Placement:** the server picks the footprint among candidate windows of the ring, weighted by `1 + 4 · removedFraction` of the window's cells, so new rock leans toward what was mined ("debris settles into the gaps"); with ~1e5 windows per ring this is flavour, not a refill. A window is rejected if it is within 20 km of any anchor, realised body or active site, or if any of its lanes would then hold more than 4 live footprints within 240 km of arc (the GPU's per-lane list). Placement is one job on a worker (~1 ms).
  - **Unseen (round 6, replaces the revision 3 review's "show the swap under the flash"):** with no flash to cover it, the swap must not be watched. A window is also rejected if any player is within `EventHideKm` (130 km: past the 120 km the largest impostors draw to) of the footprint. Footprints are a few km and a ring is ~10⁴ km round, so a window is almost always free; if 64 tries find none, the event is put back one hour (a heap event). Nobody sees old impostors change, so no fade and no VFX are needed.
  - The earlier review fix stands: spawned bodies and damage zones do not exclude a window (every mined window holds re-railed leftovers).
- **Takeover:** the footprint's cells get the event as owner (derived from the footprint, above), and their old `CellDelta`s, mined-out bits and cosmetic crater records are deleted. The previous population of those cells, natural or an older event's, is superseded: gone forever, like mined rock, and its ids never return. Exactly one population is live per cell, so the no-overlap invariant holds within each cell as before.
  - **Players' work survives (review fix):** spawned bodies in the footprint are kept, and so are naturals with mining edits, damage zones or caves, which convert to spawned bodies at takeover. The server tests the new population's slots against them once, at takeover (the same integer overlap test), and stores the overlapping slots as the new owner's initial `RemovedSlots`, so clients and the GPU need nothing new.
- **Content:** the event instancer is the ring instancer with the event's seed (`Hash32(ringSeed, serial)`) and the event id layout. Its per-cell counts are the band's ordinary `CountFx` plus the kind's newcomers (a small 2D Gaussian blob over the footprint times the kind's class mix, built in integer and clamped like `CountFx`). So a footprint reads as ordinary ring with a little extra, and its mined-out cells are back at the band's density with new ids: that is the replenishment. Fit, overlap, spin and parity rules are unchanged. EM lenses come from the server-only `Hash(DepositSeed, serial)` at the band's `EmFraction`.
- **Visuals:** none at the event tick. A minor collision's dust boost is a function of the event record and the tick, so it costs no bandwidth.
- **Law:** a captured rock is unclaimed, and it sits under the band's existing holding and permits (factions-and-law.md, "Rings, belts and asteroids").
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
| Shatter and AmBlast (revision 2; revision 4: whole body, +8 B dust fields) | The same `FractureEvent` (Kind Shatter or AmBlast; `BladeOct` = shot axis; `Owner` per the attribution rule) plus records and one fines cloud (its `Lost` mass is in the record, not on the wire). An AmBlast references the exotic-matter `AmBreach` by tick and trap id | ~56 B + ≤ 16 × 46 B + 24 B (≤ ~0.8 KB) | As fracture events |
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
| `RingEvent` (revision 3; D73: no announcement) | Reliable, on interest entry for footprints in draw range. Nothing is sent at the event tick: no player is within `EventHideKm` of a new footprint | ~48 B | Clients with the footprint in draw range |
| `HazardBeacon` (revision 3) | 2 Hz while a site is Critical, a fracture is queued, a contact AM trap is armed on rock, or a rock is `Thrown`. Round 6 (D72): an `Occupied` bit in `Kind`; and once, reliable, to the cavers of an `Occupied` body when a site opens on it or a fracture is queued against it (kind `SiteOpened`) | 16 B | Every client within 5 km; `SiteOpened`: the body's cavers |
| `ThrownTag` / end (revision 3) | Reliable | 12 B | Interest |
| Chip/core sample result (revision 3) | Reliable | 24 B (+ a `DepositRecord` if a lens was touched) | The sampler's group |
| Hand dig ops, cave drill ops (D74), splitter `Crack` events and `DamageZone`s (revision 3) | As edits; zones ride the `FractureEvent` that makes them (16 B each) | 40 B per op, 24 B per splitter event | Interest |
| Kinetic crack zone (revision 4) | As a crack edit plus its `DamageZone` | 32 B | Interest |
| Dust cloud (revision 4) | Rides the `FractureEvent` (`M_d`, `v_d`); then a function of the tick | +8 B | As fracture events |
| `ShieldState` (revision 4) | Reliable, on change (pool, rate, tick; clients extrapolate the lazy form) | ~16 B | Interest of the station |
| `ShieldHit` (revision 4) | Unreliable, per absorbed hit, for VFX | ~12 B | Interest of the station |
| `CaseNotice` (factions-and-law.md; the `IncidentRecord` itself is server only) | Reliable, when the 1 Hz flush opens, merges or settles a case | 24 B | The defendant, the victim's owner and the zone owner |

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
| Shield pool, absorbs and leaks, defence assignments, `IncidentRecord` class (revision 4) | Server-only; clients receive state, hits, tractor messages and records. Dust clouds: a function of the event record and the tick, exact enough for display |
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
| Replenishment events (revision 3; D73) | Placement ~1 ms on a worker per event (under one a day per ring), plus the `EventHideKm` test (~10 ns per player per try). 0 per tick: heap events. GPU: footprint test per enumerated cell, ≤ 4 per lane, ~+3 % enumeration |
| Station defence (revision 4) | Shields: 0 per tick when not hit (lazy pool, sensor-sphere callbacks on asteroid layers only, existing sweeps test the shell); ~1 µs per absorbed hit; ~0.2 µs per tick per body resting on a shell. Watch: ~1 µs per defended station at 2 Hz when nothing is near (the bubble's asteroid list, no grid walk), ~0.2 µs per tracked body; coasting debris scheduled once at coast start, ~1 µs per station in reach (review). Release ~4 µs. Active defence: tractor-beams.md's per-beam cost, ≤ 8 reserved beams per station, ≤ ~10 µs a tick fully engaged. `IncidentRecord` ~0.2 µs per contribution, 1 Hz flush |
| Dust clouds (revision 4) | ~20 ns per (sensor pair, cloud) in `RingObscuration`, from a server-wide 4 km cell list (≤ 8 per 4 km, ≤ 256 per server; typically 0–3 per segment); 1 Hz re-bucket ~10 ns per cloud; GPU ~0.01–0.03 ms in the haze pass; 0 per tick otherwise (function of the tick) |
| Crack zones (revision 4) | ~2 µs per hit (threshold tests plus a record and a zone); no fracture bake. A whole-body shatter is one fracture evaluation, as before |
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
| Caving characters with sub-base detail (review) | 32 per server (a working cave-drill vehicle takes one slot, D74) |
| Cave drills working per body (round 6, D74) | 2, inside the body's 4 ops per s |
| Replenishment hide distance `EventHideKm` (round 6, D73) | 130 km from any player |
| Live event footprints per lane within 240 km of arc (review) | 4 |
| Asteroid beam energy relative to the lane, `max(W_b, E_rail)` (review) | 500 MJ |
| Dust clouds per 4 km / per server / lifetime (revision 4, review) | 8 / 256 / 600 s (merges take the mass-weighted birth tick) |
| Station defence emitters per station (revision 4) | 8, in reserved slots outside the bubble's 16 beams and the 4-per-owner cap (review) |
| Station defence release energy relative to the station (review) | `DefenceReleaseJ` 10 MJ (0.2–5 m/s) |
| `IncidentRecord` window / reach / minimum / escalation (revision 4, review) | 120 s (path-based for aimed bodies) / 2 km Hostile, 9.6 km Reckless / 1 MJ / 3 Reckless in 1 h escalate. Listing is factions-and-law.md's heat level |

**Profiler rows:**
- Rings: CPU query ms, lanes uploaded, GPU enumeration / haze / gravel / impostor ms, instance count, removal-set fill.
- Asteroids: realised (static / kinematic / dynamic / coasting), shape builds/s, bake p50/p95, MoveKinematic count.
- Heat: sites active / cooling, substeps, step ms.
- Fracture: events by kind (incl. Shatter and AmBlast), queue depth, fragments, chunks, clouds, bake ms, mass lost per event, mass-check failures.
- Weapons: kinetic hits on rock (crater / crack / crack zone / shatter (whole body) / degraded by limit, queue, saturation; revision 4), chunks turned to cloud, rail-sweep rounds and µs, look-ahead truncations, weapon-heat joules absorbed and lost to `P_siteMax` and to the bulk cap, weapon-opened sites, AG-pulse asteroid kicks, fracture job ms against `FractureJobMsPerTick`.
- Revision 4: station defence per station (shield pool, joules absorbed and leaked, hits; watched, served and `Unserved` bodies; defence beams and arms active, µs), dust clouds live and merged, `IncidentRecord` records by class.
- Net: mining bytes/s, table CRC mismatches.
- Revision 3: spinning realised rocks (kinematic own-lane / other-lane), lane bubbles stepped because of spin, held and `Thrown` rocks and sweep µs, ram hits by thrown rocks; events placed, footprints drawn, partial-set fill and overflow, mined-out bitmap regions; fog: haze dust ms, impostors culled by fog, sensor transmittance calls; caves: caving characters, cave chunk builds, hand ops accepted and refused (zone, heat, rate), splitter events, samples; compaction: region bytes, ephemeral craters, removed leftovers.

## Data definitions

| File | Holds |
| --- | --- |
| `data/asteroids/materials.json` | The material table above, plus colour/albedo and ore item id |
| `data/asteroids/archetypes.json` | 16 unit-space SDF recipes (ellipsoid, lobes, craters) |
| `data/asteroids/ring-styles.json` | Band palettes: composition weights, colour, α_s, α_b, `EmFraction`, gap and ringlet counts. Revision 3: per band `density` ρ_b and `dust` D_b ranges, the darkening factor, the dust colour and phase g; density and dust presets; per host kind (rocky, gas giant) the band count and preset weights |
| `data/asteroids/fields.json` | Belt and field styles: Plummer a, density, dust (revision 3), composition, `EmFraction` |
| `data/asteroids/events.json` (revision 3) | Replenishment event kinds: rates per host, footprint ranges, body counts, class mixes, composition rules, dust boost and decay, the placement weight, exclusion radius and `EventHideKm`. Round 6 (D73): kinds are `Capture`, `MinorCollision` and `FieldCapture` only |
| `data/items/eva-mining.json` (revision 3) | EVA tools (table above), `sample` and `OreSack` items |
| `data/sensors/ring-obscuration.json` (revision 3) | `κ_part` and `κ_dust` per sensor kind (six kinds, round 6), read by combat-and-sensors.md's detection formula. Revision 4: `κ_dust` also applies to shatter dust clouds |
| `data/components/station-defence/*.json` (revision 4) | `StationShield` classes (capacity, recharge, bus power, base, max radius, mass) and `DebrisArm` (reach, push, mass and speed limits, power) |
| `data/physics/station-defence.json` (revision 4) | `ShieldMinJ`, `η_s`, the watch rate, `WatchHorizonS`, `ReleaseHorizonS`, `MissMarginM`, `DefenceReleaseJ`, push-out speeds and direction candidates, defence emitters per station (reserved slots), the dust cloud cell and server caps, `HostileWindowS`, `HostileReachM`, `HostileMinJ`, the escalation count |
| `data/asteroids/mining.json` | Global knobs: γ, χ, χ_det, ψ, K, w_min, k_v0, k_open, k_crack, δ_min, `D_dig`, `KinematicMassLimit`, `SCrit`, `SCritExit`, heat rates and substeps, `E_cap`, `ShrapnelVMax`, `ProtectRadius`, rate limits. Revision 2: a `weapons` block (ζ_sh, N_sh0, η_c, the fines curve, `FinesLost`, `CraterLost`, `ShatterVMax`, `P_siteMax`, `E_open` and its window, the bulk cap, weapon sites per player and server, `RailTestMinEnergy`, the sweep budget, the fracture queue and job budget, the bubble body budget, η_am, ξ_am, the AM contact rule; revision 4 drops the protected-zone debris rule and adds the dust cloud knobs). Revision 3: a `spin` block (`SpinFraction`, period bounds per class), a `manipulation` block (`ThrownMax`, server cap, tag end rules), a `caves` block (ζ_dz, splitter R_f, cave detail radius and levels, heat exclusion, op rates), a `bystanders` block (beacon radius and rate), and a `persistence` block (crater op merge cap, cratered naturals per region, region byte budget, leftover age) |
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
  "looseChunkG0Factor": 0.2,
  "dust": { "frac": 0.25, "massExtM2PerKg": 50, "vMin": 3.0, "speedOfMean": 0.5, "lifeS": 600, "fadeFrom": 0.667, "despawnTau": 0.05, "per4Km": 8, "serverMax": 256, "mergeBirth": "massWeighted" },   // revision 4 (review: server-wide list)
  "crackZone": { "countsAsFractureEvent": true },                                                                                                       // revision 4
  "am": { "crackPerJ": 1.0e-4, "kineticPerJ": 2.0e-4, "contactUsesBreachRadius": true, "chainMinPurity": 0.15 },
  "pulse": { "skipRails": true, "hotKickVentPerS": 0.5, "hotKickVentS": 5, "hotKickS": 0.3 } }

// a weapon def's asteroid block (combat-and-sensors.md owns the rest of the def and the ids; these follow its `data/components/weapons/` names)
{ "id": "weapon.railgun.l", "asteroid": { "kind": "Kinetic", "etaK": 0.25, "xiK": 0.30, "shatterCoupling": 1 } }
{ "id": "weapon.laser.m", "asteroid": { "kind": "Heat", "couplingW": 0.20, "spotRadiusM": 0.05, "depositDepthM": 0 } }
{ "id": "weapon.pbeam.m", "asteroid": { "kind": "Heat", "couplingW": 0.30, "spotRadiusM": 0.1, "depositDepthM": 2.0 } }
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
           "collisionRadiusM": 16, "heatExclusionM": 6, "opsPerPlayerS": 1, "splittersPerPlayerMin": 6, "zonesPerBody": 64,
           "caveDrillsPerBody": 2, "occupiedBeacon": true },                                  // round 6: D74, D72
"bystanders": { "beaconRadiusM": 5000, "beaconHz": 2 },
"persistence": { "craterOpsPerNatural": 32, "crateredNaturalsPerRegion": 4096, "regionBudgetBytes": 16777216,
                 "leftoverAgeDays": 30, "leftoverMaxD": 16 }

// revision 4: data/components/station-defence/shields.json (one class) and data/physics/station-defence.json
{ "id": "shield.station.m", "class": "M", "capacityJ": 2.0e9, "rechargeW": 2.0e7, "efficiency": 0.5, "baseMW": 2,
  "maxRadiusM": 800, "massKg": 120000 }
{ "id": "arm.debris", "reachM": 40, "pushN": 5.0e5, "maxMassKg": 1.0e6, "maxSpeed": 10, "powerMW": 0.2 }
{ "shieldMinJ": 1.0e4, "watchHz": 2, "watchHorizonS": 120, "releaseHorizonS": "lanePeriod<=7200; field 600", "missMarginM": 100,
  "defenceReleaseJ": 1.0e7, "pushOutSpeed": [0.2, 5.0], "pushDirCandidates": 16, "retargetPerS": 1,
  "defenceEmittersPerStation": 8, "defenceSlotsReserved": true, "dustCloudsMax": 256, "dustCellM": 4000, "dustPerCell": 8,
  "hostileWindowS": 120, "hostileReachM": 2000, "hostileMinJ": 1.0e6, "recklessEscalateN": 3, "cameToHazardS": 600,
  "redirectDvMS": 0.5, "nonOwnerHeatShare": 0.1, "hostileListS": 3600, "recklessListS": 600, "recordFlushHz": 1 }   // review fixes

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
    public uint SourceEvent; public OwnerId SourceOwner, LastBeamOwner;   // revision 4: incident tracing (station defence never sets LastBeamOwner)
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
    public SiteState State; public long LastBeamTick; public OwnerId Owner;
}
public enum SiteState : byte { Cold, Warming, Window, Critical, Cooling }

public enum FractureKind : byte { Crack, Spall, Split, Detonation, Shatter, AmBlast, Sever }   // revision 2: Impact -> Shatter (rams and projectiles)

public enum KineticSource : byte { Projectile, Ram, Contact }
public struct KineticHit                                 // server only, from the combat sweep or the fast-transit sweep
{
    public BodyKey Body; public int3 PointFx; public float3 Dir; public float EnergyJ, EtaK, XiK;
    public KineticSource Source; public OwnerId Shooter; public long Tick;
}
public struct WeaponAsteroidDef { public byte Kind; public float EtaK, XiK, ShatterCoupling, CouplingW, SpotRadius, DepositDepth, BoreJPerM3; }
public struct HeatContributor { public OwnerId Player; public float Joules; }   // HeatSite keeps 4, decayed over 30 s

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FractureEvent                              // 56 B wire + persistence (revision 4: 64 B with the dust fields; dust only rides Shatter and AmBlast)
{
    public BodyKey Body; public uint EventSeq; public long Tick; public uint Seed;
    public int CX, CY, CZ;                               // body-local 1/1024 m
    public ushort BladeOct, BiasOct, BiasStrength, RadiusCm;
    public byte N; public FractureKind Kind; public byte FragmentCount, RemainderCount;
    public uint FirstBodyId; public OwnerId Owner;
    public float DustMassKg; public Half DustSpeed, Pad;  // revision 4: the dust cloud (Shatter, AmBlast), +8 B
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
public enum RingEventKind : byte { Capture, MinorCollision, FieldCapture }   // round 6 (D73): modest kinds only
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RingEventRecord                            // ~48 B, kept while it owns a cell; sent on interest entry only (D73: not announced)
{
    public uint Serial; public RingEventKind Kind; public ushort Source; public long Tick; public uint Seed;
    public ushort Lane0, Lanes; public uint Arc0; public ushort Arcs;   // footprint in class-0 cells
    public ushort PeakFx, SigmaLanes, SigmaArcs; public byte ClassMix, Composition; public float DustBoost, DustDecayH;
}
public struct DamageZone { public int CX, CY, CZ; public ushort RadiusCm; public ushort Flags; }   // 16 B, body-local 1/1024 m
public struct ThrownTag { public OwnerId Owner; public long EndTick; public float WorkJ; }
public struct HazardBeacon { public EntityId Source; public byte Kind; public float3 Position; public float ReachM; }  // 16 B on the wire; Kind bit 7 = Occupied (D72)
public enum SensorKind : byte { Optical, Thermal, Radar, Signature, Gravimetric, CrewScan }   // round 6: extended and owned by combat-and-sensors.md

public static class RingObscuration                      // public ring data; server for detection, client for display
{
    public static float OpticalDepth(in RingParams ring, LaneTable t, double3 a, double3 b, SensorKind kind);   // 8-point Simpson
    public static float Transmittance(in RingParams ring, LaneTable t, double3 a, double3 b, SensorKind kind);
    public static float EffectiveRange(float nominalRange, float sigma);                                       // (2/sigma) W(sigma R/2)
    // revision 4: OpticalDepth also sums the bubble's DustCloud chords (segment-sphere, <= 8 clouds)
}

// revision 4
public struct DustCloud { public uint SourceEvent; public double3 Centre; public float3 Velocity; public float MassKg, Radius0, Speed; public long BirthTick; }  // function of tick
// IncidentKind, IncidentClass and IncidentRecord (64 B, server only) are factions-and-law.md's (src/Sim/Law/Incidents.cs).
// This doc writes kinds Shatter, AmBlast, Detonation, Thrown, BeamPushed, Kicked and PersonHarm (D72),
// with OwnerId actors and Liable (D33: the miner for legal-mining debris in a zone, whatever the class).
public struct StationShield                              // one per shield core; lazy pool
{
    public EntityId Station; public float RadiusM, CapacityJ, RechargeW; public float PoolJ0; public long PoolTick; public bool Up;
    public float PoolAt(long tick);                      // min(Capacity, PoolJ0 + RechargeW * dt)
    public float Absorb(long tick, float energyJ);       // returns the leak; settles and stores (PoolJ0, PoolTick)
}
public struct DefenceTask { public BodyRef Body; public float TimeToImpactS, EnergyJ; public byte Emitter; public byte Mode; public byte Priority; }   // Sweep, Hold, Arm; Priority drives preemption (review)
// review: VoxelBody also carries one "unredirected since birth" flag bit (Class rules); LastBeamOwner is set by beams, rounds (>= 0.5 m/s in 2 s), hull contacts and AG kicks
public sealed class StationDefence                       // server; 2 Hz watch, assigns station emitters and arms
{
    public void Watch(long tick);                        // bubble asteroid list + scheduled coasting arrivals -> threats by priority, Unserved flags
    public void Schedule(in CoastElements e, long tick); // review: one path test per coast start or element change, against shells in reach
    public void Assign(long tick);                       // free emitter or arm that reaches first; release on miss margin
}
public static class Incidents                            // server: trace actor, classify, accumulate per (actor, victim, source), flush 1 Hz
{
    public static void Report(in DamageSource src, EntityId victim, float energyJ, bool shieldOnly, long tick);
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
    public static float CrackZoneThreshold(in AsteroidMaterialDef m, float gEff, float diameter); // E_th, J of projectile energy (revision 4: crack zone, no pieces)
    public static float ShatterThreshold(float gBody, float rReach);                              // E_split, J of projectile energy (revision 4: the only shatter, whole body)
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
| New `src/Asteroids.Runtime/` | `AsteroidRealiser.cs` (sphere + capsule membership), `VoxelBodyJob.cs`, `FastTransitSweep.cs`, `ShrapnelSweep.cs`, `ProjectileRailSweep.cs`, `AsteroidWeaponHits.cs` (kinetic hits, weapon heat, AM contact, pulse skip glue), `RingRenderer.cs` (annulus, haze, gravel), `AsteroidImpostors.cs` (enumeration + indirect draws + spawned-body list), `MiningTools/*` (laser, wedge, imager, scanner, tractor, intake and drill behaviours), `MiningPanel.cs` (ImGui sensors), profiler rows. Revision 3: `ThrownSweep.cs`, `HazardBeacons.cs`, `CaveDetail.cs` (sub-base refinement and the 16 m collision ring), `EvaMiningTools/*` (scanner, chip and core sampler, hand drill, splitter, ore sack), ~~`RingEventVfx.cs`~~ (withdrawn, D73: events have no VFX). Round 6: `MiningTools/CaveDrill.cs` (D74), cave occupancy in `CaveDetail.cs` (D72). Revision 4: `DustClouds.cs` (event dust, `RingObscuration` term, haze sphere list), `StationShield.cs` (lazy pool, shell sensor, absorb and leak), `StationDefence.cs` (watch, priorities, emitter and arm assignment), `DebrisArm.cs` |
| `src/Sim/Law/Incidents.cs` (owned by factions-and-law.md; was `src/Sim/Incidents.cs` here) | The shared `IncidentRecord`, its 1 s accumulation and the 1 Hz flush. The asteroid side (tracing, classification, `Liable` for D33) stays in `src/Asteroids` and writes through it |
| New shaders | `ring_annulus.vert/.frag`, `ring_haze.frag`, `ring_gravel.vert`, `asteroid_hash.glsl`, `asteroid_enum.comp`, `asteroid_instance.vert/.frag`, `ring_shadow.glsl` (included by terrain, hull, impostor and atmosphere, and the gas giant cloud shaders). Revision 3: the dust channel in `ring_annulus.frag` and `ring_haze.frag`, per-instance spin rotation in `asteroid_enum.comp` (not per vertex), fog cull, mined-out bitmaps and event footprints in `asteroid_enum.comp`, `ring_slab_depth.glsl` (closed-form line-of-sight optical depth, shared with the cull) |
| New tests | CPU/GPU instancer parity; lane-fit no-overlap (including candidate-vs-larger-class); ring table CRC equality across Debug/Release and two machines; re-rail never overlaps; fracture run-to-run determinism on the server; client bake vs server records within visual tolerance; mass conservation per event; heat sim stable at κ = 0.4, dx = 0.375 m; forecast vs sim outcome per material. Revision 2: shatter threshold monotone in D, G0, heat and cracks; wedges never shatter; mass conservation with the Lost bucket for Shatter and AmBlast; `P_siteMax` holds with every source at once; weapon heat per bus watt ≤ 1/5 of the beam's; AG pulses leave rails and static naturals untouched; protected-zone asteroids inert to every weapon; over-limit shatters degrade to crater + crack. Revision 2 review: crack records do not stack (hits under 0.4·E_th never shatter cold rock); `E_kin` including a dynamic parent's recoil stays ≤ `E_cap` and every speed ≤ `ShatterVMax`; sub-`E_open` weapon heat never lifts `T_bulk` past the cap; look-ahead rail hits truncate the round before any later hit; the per-bubble body budget and cloud merge hold under sustained fire; one pending event per body and the 64-event queue bound; Loose chunks hit by weapons become cloud mass, never new bodies. (The revision 2 review's "no hull damage in protected zones" test is withdrawn by revision 4.) Revision 3: CPU/GPU spin parity (orientation within float tolerance after 2^32 + 10 ticks of wrap); spinning rails never overlap (bounding spheres) over a full period; a freed rock keeps its rail ω; removed ids never reappear after save/load and compaction; event ids never collide with natural or earlier event ids; takeover leaves exactly one live population per cell; region bytes stay ≤ the budget under a synthetic year of mining and gunfire; hand digs refused outside zones and within 6 m of a heat site; splitter zones only grow from existing zones; samples are refused by every refinery; a cave detonation's shrapnel stops at rock; `Thrown` rocks hit frozen ships; `W_b` and `ThrownMax` caps hold; fog cull never drops a cell with `τ_los` < 5.5; `RingObscuration` against a brute-force 1,000-step march within 2 %. Revision 4: every Shatter is whole-body and no kinetic hit below `E_split` makes pieces; crack zones leave zones only from `E_th` up and count against the event rate; whole-body pieces' directions cover more than a hemisphere about the body centre; mass conservation with the dust share in the Lost bucket; dust `τ_c` against the closed form and despawn at 0.05 or the lifetime; shield absorb + leak = offered energy, and the lazy pool equals a per-tick integration; a shield's momentum debit to the anchor; a drained shield leaks a capped throw in full; defence never sets `LastBeamOwner` and releases ≤ 5 m/s; a swarm past the emitter count flags `Unserved`; `IncidentRecord` classes for a shot inside, at and outside the forecast reach, and none for own and party damage. Revision 4 review: a 1e8 kg boulder creeping in at 1.9 m/s is absorbed (energy gate); a defence-pushed body released in a lane never re-enters the shell within one lane period, and leaves with ≤ `DefenceReleaseJ`; preemption retargets an emitter from a baited Hold to a `Thrown` rock; 16 visitor beams in a bubble leave the station's 8 defence beams working; defence sweeps deliver nothing to a ship in the cone; the exemption never converts a rail rock inside a zone; a rock thrown from 5.5 km at 32 m/s that hits after 150 s is Hostile; gunfire impulses ≥ 0.5 m/s on someone's fragment make the shooter the actor; a non-owner topping up a Critical site to detonation is the actor; a ship that parks next to a mining rock and gets hit is at most Reckless; dust merges never extend a cloud past the mass-weighted lifetime; the shield sensor pairs with no ship |

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
  - Protected zones: ~~asteroid bodies do no hull damage inside `ProtectRadius`, and chunks and clouds entering it become lost mass~~ (revision 2 review, withdrawn by revision 4). Protected zones stop rock being worked or broken inside them; debris entering them is real and met by station shields and deflection (revision 4 block below). This belongs with whatever zone system declares protected zones.
  - Caps: 64 realised rails, 32 freed rocks, 256 asteroid-related bodies and 128 chunks per bubble.
  - The > 300 m/s analytic sweep rule.
  - Layers `AsteroidRail` and `Debris`.
  - Profiler rows.
- **ship-hulls.md:**
  - Mining function cores (table above).
  - Shrapnel and ram damage become damage brushes, so ship materials need `eCav` (combat-and-sensors.md's `e_cav`; called `EnergyPerVolume`, e_hull, in earlier revisions).
  - Convex decomposition moves to `src/Physics`; `MassMoments` is shared.
  - This depends on a contact-damage rule (impulse energy → damage brush, reduced mass), which is a missing system.
- **A future combat doc** (revision 2; now combat-and-sensors.md, which did this: see Round 6 below): weapon defs carry the `asteroid` block (kind, η_k, ξ_k, `shatterCoupling`, c_w, spot, deposit depth). Its projectile sweep feeds `KineticHit`s here, and its rounds in rings and fields use `ProjectileRailSweep`. Its weapon numbers replace the placeholder classes above.
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
  - its proposed protected-zone line for `Thrown` bodies is adopted here (revision 4: withdrawn; a `Thrown` body in a zone is real and met by station defence);
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
- **A future sensor doc** (now combat-and-sensors.md section 4, which does): consumes `RingObscuration.Transmittance` and the per-kind couplings in `data/sensors/ring-obscuration.json`.

**Revision 4:**
- **README.md:** decided rows: "Stations have shields (capacity + recharge; big or sustained energy gets through); debris in protected zones is real and pushed out by station tractors and arms; damage traced to a shatter or thrown rock is a hostile act of its owner; a shatter is whole-body (through-crack, large fragments, dust fog), below it crater and cracks". Drop the rings questions on what "shatter" means and on debris near stations (old 7 and 8) wherever they are collected, and the "(Revision 4 pending: ...)" note on the rings row. Add a faction, reputation and crime system (consumer of `IncidentRecord`) to the missing systems.
- **frames-and-zones.md:**
  - protected zones: the withdrawn debris rule (above) is replaced by station defence; the zone keeps its permission hook, which also names the parties allowed to help with deflection;
  - a new `AsteroidMoving` layer for dynamic asteroid bodies (collides like `Moving`); a shield is one Jolt sensor sphere per station on the `AsteroidMoving` and `Debris` layers only; the shrapnel and `Thrown` sweeps test the shell sphere, and coasting bodies are scheduled against shells once at coast start (review);
  - profiler rows for station defence and dust clouds.
- **ship-hulls.md:** new station function cores `StationShield` (S/M/L) and `DebrisArm`, and the station grid's defence controller assigning emitters. ~~Ship shields are not proposed (stations only, as decided).~~ Superseded by D55: ships get particle, energy and gravity shields, in combat-and-sensors.md.
- **tractor-beams.md (its revision 2):**
  - station defence: the zone owner's emitters (and parties the zone permits) may lock and sweep asteroid **debris** (Dynamic or Coasting bodies, and rubble clouds) inside the zone, never rail, static or kinematic rocks; up to 8 defence emitters per station in reserved slots outside the bubble's 16 and the 4-per-owner cap (review); defence beams deliver nothing to ships, grids or characters, don't set `LastBeamOwner`, and release at ≤ `DefenceReleaseJ` (10 MJ) relative to the station, tested over a lane period (review);
  - (review) braking is never faded for any body, not only asteroids: a ship whose `W_b` reached `EThrow` could otherwise never be braked by a beam;
  - the `Thrown` contact-damage zeroing inside protected zones is withdrawn; the zeroing of tractor delivery to other parties' bodies stays (it protects stations and docked ships from tearing);
  - a Sweep can push a `RubbleCloud` (AM-fed emitters, D28) (one node at its centre, Δv to the cloud's velocity);
  - the asteroid beam cap `max(W_b, E_rail)` written into the beam model.
- **exotic-matter.md:** shields complement gravity dampers and do not replace them: pulses, warp fields and dose are not shield business; a shield's stopping impulse is debited to the anchor like a contact (`EmPerNs`); the AG charge owner is the actor of kicked-debris `IncidentRecord`s. (Review) Proposed: a breach or detonation set off by another party's hit (a shot trap, cell or carried charge) is attributed to that hitter, the owner becoming a co-actor at most; otherwise shooting a rival's carried charge near a station frames the rival.
- **review.md:** missing systems: faction, reputation and crime (consumes `IncidentRecord`), and a combat doc that routes rounds through the shield's absorb call; risks: station defence saturation tuning, dust clouds blinding station watches.
- **A future combat doc** (now combat-and-sensors.md, which took the hook): may route weapons fire through `StationShield.Absorb`; owns turrets. Fines and bounties are factions-and-law.md's.
- **A future faction doc** (now factions-and-law.md): owns how `IncidentRecord` classes and energies change reputation, standing and crime status, and replaces the minimal station listing here.

**Round 6 (D72, D73, D74, D55/D56):**
- **factions-and-law.md:**
  - its "People in caves" ruling reads two facts defined here: per-body cave occupancy and `OccupiedAtFirstEvent` (Damage zones and caves). Its wording (Hostile when the people were there first, at most Reckless when they came after) matches the "came to the hazard" cap here; nothing to change there.
  - `LandRights.Check(owner, body, direction, depth, toolClass)` is written for surface cells. It needs a band form for asteroids (band id, the rock's claim, `toolClass` = `CaveDrill` or hand tool), since asteroid cave drills call it once a second per active tool.
  - Applied from its "Changes to other docs" list: open questions 3 and 11 closed, the beacon's `Occupied` flag, rock claims and band permits by reference. Applied in the r7 consistency pass: the `IncidentRecord` struct and enums are a pointer and ids are `OwnerId`, `IncidentClass.Fined` is gone, the minimal station response (`HostileListS`, the 10 min Reckless listing) is deleted, the Sieges bullet and `CaptureTicks` are a pointer, the file is `src/Sim/Law/Incidents.cs`, and the wire carries `CaseNotice`.
- **combat-and-sensors.md:**
  - its changes for this doc are applied: the reference projectile classes are its weapons (ids `autocannon.s`, `massdriver.m`, `railgun.l`, `massdriver.xl`; the example id is `weapon.railgun.l`), the `StationShield` weapons hook is taken, `SensorKind` gains `Gravimetric` and `CrewScan`, and open questions 5 and 9 are closed.
  - "Mining tools and other things used as weapons" needs a `CaveDrill` row (proposed: contact `Kinetic` like the `MiningDrill`, at its lower power).
  - Applied in the r7 consistency pass: the station watch reads the station's `ContactTable`, `e_hull` is that doc's `e_cav` (`eCav` in data), and "reduced-mass contact damage" is its contact hit.
- **planets.md:** D74's planet side (tunnelling vehicles and cave drills under land ownership or approval) can reuse the `CaveDrill` core defined here; it needs its own rate and op radius against planet voxels, and the land check per stroke that factions-and-law.md defines.
- **ship-design-and-construction.md:** a new function core family `CaveDrill` (`regulated: true`, with a charge setter that takes `SplitterCharge` items); the docking scan lists regulated cores (factions-and-law.md).
- **ai-ships-and-crew.md:** realised NPC crew inside a rock's caves count toward its occupancy, and an NPC hurt there is a `PersonHarm` record like a player (D72 names NPCs).
- **README.md:** decided rows for D72 (cavers: physics allowed, harm illegal), D73 (modest replenishment: captures, unannounced) and D74 (cave drills and tunnelling vehicles, regulated). Its rings row should say "modest replenishment events" and drop "comet breakups, moonlet shattering" wherever quoted. Drop rings questions 3, 5, 7, 9 and 11 from the collected list.
- **review.md:** "event placement never visible" is now a rule (`EventHideKm`), not a risk; the sensor, combat and faction docs leave the missing-systems list.

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
    - revision 2, with debug weapons (mass driver, railgun, pulse laser, particle beam, AM cell, AG charge): the measured crack-zone threshold is within 20 % of `E_th` for each material at 4, 16 and 64 m (revision 4); recovered mass is ≥ 90 % for a held wedge cycle and 55–80 % for a shatter of the same rock (revision 4: whole body; and a crack zone then a wedge recovers ≥ 85 %); weapon heat per bus watt is ≤ 1/5 of the beam's; a particle beam detonates ice-rock; an AG pulse moves fragments and leaves rails still; the measured whole-body shatter energy is within 20 % of `E_split` at 4, 8 and 16 m, and the second-hit fraction (~0.7·E_split) is measured; 60 s of sustained autocannon and railgun fire from 8 debug shooters into one bubble keeps the bubble at ≤ 256 asteroid bodies and ≤ 16 clouds, and the fracture job time at ≤ `FractureJobMsPerTick`;
    - revision 3: test rocks spin (kinematic, set ω) and a lane bubble's step ms with 15 spinning rocks is logged; a lock frees a spinning rock with its spin; a throw past 50 MJ tags it, its sweep hits a frozen dummy ship, and the caps hold; an EVA character samples, enters a wedge's damage zone, digs a 10 m tunnel with the hand drill, extends it with splitters, and takes a lens out with no EM loss; cave chunk builds and memory per caving character are logged; a detonation in the cave kills inside and leaves the outside untouched; the user signs off cave feel.
    - revision 4: a whole-body shatter shows a through-crack, large pieces flying apart and a dust cloud that blinds the optical debug sensor and not the radar one, for its computed time (user signs off the look); a debug station with an S shield and 2 M defence emitters 3 km from a railgun shattering 8 m rocks: shield pool, absorbs, leaks, served and `Unserved` bodies and defence µs logged; the same station undefended takes hull damage, and every hit produces the expected `IncidentRecord` class.
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
    - revision 3: one ring with dense, medium and sparse bands at each dust preset reads correctly by eye at 50,000 km, 500 km and inside (user sign-off); GPU cost inside a dense choked band ≤ the dense clear band's (the fog cull pays for the dust); `RingObscuration` ranges match the table within 10 %; a gas giant ring (the widened arc field) passes the instancer parity test at 13,200 km; forced replenishment events (D73: captures and minor collisions) place only outside the 20 km exclusion and only where no player is within `EventHideKm`, are never broadcast, keep every spawned body and worked natural in the footprint, and their ids never collide; a scripted month of mining near a station keeps every region under budget with the compactor's ms logged. Revision 4: a scripted hour of shatters, throws and kicks around a defended L station and an undefended outpost logs station defence ms per tick, shield hits and leaks, `Unserved` bodies, hull damage and `IncidentRecord` counts by class; defence cost with nothing near stays ~0.

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
- **Stacking crack factors** (revision 2 review): 0.4 per record over 8 records is a 0.4⁸ ≈ 7e-4 factor, so autocannon fire would first crack and then shatter any rock. Records do not stack; the cracked floor is 0.4·E_th (revision 4: 0.4·E_split, the whole-body threshold).
- **Fragments that keep all their energy in the parent's frame with a free recoil:** a dynamic parent's recoil came on top of `E_kin`, so one shot could throw a remainder boulder as a second, uncapped ram. The budget is now in the centre-of-mass frame.
- **Weapons fracturing Loose chunks:** every shot would turn one body into up to 20. Chunks become cloud mass instead.
- ~~**Debris harmless inside protected zones**~~ (revision 2 review): superseded by the user's revision 4 decision. Stations defend with shields and deflection instead, and the debris stays real.
- ~~**Regional shatter**~~ (revisions 2–3: a hit at `E_th` broke out ~1 % of the rock round the hit): superseded by the revision 4 decision that a shatter is whole-body. That energy range is now the crack zone.
- **Shields that delete debris** (revision 4): removing a body or ray at the shell would bring back "lost mass" by another name and make pushing debris out pointless. The shield takes energy; the body stays real.
- **Per-tick shield tests** (revision 4): testing every debris body against every station shell each tick costs for nothing. Existing sweeps and one sensor sphere per shield find hits; the pool is settled lazily.
- **Inferring intent from aim** (revision 4): the server can't read intent, and aim tests would be easy to game. A `IncidentRecord`'s class comes from what the actor could see at the event tick (the forecast reach) and the time to the damage.
- ~~**Comet breakups, moonlet shatterings and announced "gold rush" events**~~ (revision 3 draft): withdrawn by D73. Replenishment is modest, unannounced and placed unseen.
- **Refusing other parties' heat sites and wedges near a caver** (old question 3): D72 keeps the physics open and makes the harm illegal instead; a refusal would let one caver claim a rock by standing in it.
- **A special tunnelling-vehicle entity** (round 6): a vehicle is a small grid with a `CaveDrill` core, so it needs no new body type, collision path or replication.

## Open questions for the user

Revision 3: the six earlier questions on regrowth, bystanders, EVA mining, ring density, ring hosts and tumbling are answered and integrated. The rest are renumbered (old 7–13 are now 1, 2, 5, 4, 6, 7 and 8), and new ones follow from the answers. Revision 4: questions 7 (what "shatter" means at the threshold: whole body) and 8 (debris near stations: shields and deflection) are answered and removed; 9–13 became 7–11, and 12–13 are new. Round 6: questions 3, 5, 7, 9 and 11 are decided (D72, D56, D73, D56, D74), and half of 12 (D55). They are struck in place and keep their numbers, because other docs cite them. 14 is new. Still open: 1, 2, 4, 6, 8, 10, 12 (the spread), 13 and 14.

1. **Crater yield (revision 2):** should sub-threshold kinetic craters give any recoverable grit (proposed 10 %), or none?
2. **AG pulses in rings:** is "rails are terrain" acceptable (ships kicked into ring rocks take ram damage), or should pulses in a slab also kick realised small naturals, up to the 32 freed-rock cap? Revision 3 makes most realised rocks kinematic (spinning), which the skip list already covers.
3. ~~**Cave miners and other parties (revision 3)**~~ **Decided (D72):** kept as proposed. Anyone may heat, wedge or shoot a rock with people in its caves; there is no 30 m refusal and no claim by presence. Injuring or killing players or NPCs that way is illegal (factions-and-law.md, "People in caves"). See Damage zones and caves, "Other parties and cavers".
4. **Hot kicked bombs:** a hot dynamic boulder kicked by an AG pulse is a moving bomb. Is gradient venting (proposed) enough, or should a pulse detonate or quench hot sites?
5. ~~**Weapon numbers**~~ **Decided (D56):** combat-and-sensors.md owns weapon stats. The 2.25 GJ capital kinetic weapon is in scope: it is that doc's capital mass driver (`massdriver.xl`). This doc keeps only the `asteroid` block and the rock outcomes.
6. **Black-hole lens chains:** near black holes, lens ore reaches q 0.3, above the 0.15 chain floor, so an AM breach on such a lens chains an AG pulse. Keep that as a rare hazard (proposed), or exclude ore entirely?
7. ~~**Replenishment events (revision 3)**~~ **Decided (D73):** modest. Asteroid captures and minor collisions only, small footprints, no comet breakups or moonlet shatterings, no announcement, placed where no player is watching. The default rates (a capture per 2 days per ring, a minor collision a week) are data, not a question.
8. **Leftovers past the region budget (revision 3):** to keep storage bounded, a region over 16 MB removes its oldest small re-railed boulders that nobody has touched for 30 days, and small-arms craters past 4096 cratered rocks in a region stop being saved. Both lose only leftovers and cosmetics, never untouched rock. Acceptable, or should the budget be larger and the server warn instead?
9. ~~**Sensors (revision 3)**~~ **Decided (D56):** combat-and-sensors.md (section 4) owns sensor kinds, signatures and detection ranges; this doc supplies `RingObscuration` only. Hiding in a choked band or behind a shatter's dust is a real tactic against optics and thermal, and is not one against radar, gravimeters or signature readers.
10. **Spin numbers (revision 3):** "tumble a little" is taken as 70 % of rocks spinning, rim speeds ≤ ~0.12 m/s (a 4 m rock turns in 5–30 min, a 400 m one in 3–12 h). More spin costs nothing on rails but more spinning rocks keep more lane bubbles stepping. Right feel?
11. ~~**Cave vehicles (revision 3)**~~ **Decided (D74):** tunnelling vehicles and cave drills are allowed but regulated. Here: the `CaveDrill` core under the damage-zone rule, at a rate that keeps the wedge cycle the bulk path; in a held band it needs a band permit with `Tunnel` (factions-and-law.md, which also owns land ownership and approval on planets).
12. **Shield and defence numbers (revision 4):** the S/M/L shields (200 MJ / 2 GJ / 20 GJ, refilling in 100–200 s) make an outpost shield leak a single capped throw and a station shield shrug off forty. Is that the right spread? (Round 6: the second half, whether ships get shields too, is decided. D55: ships get particle, energy and gravity shields, designed in combat-and-sensors.md. Only the station spread stays open.)
13. **Hostile classes (revision 4, tightened in review):** fracture debris is Hostile when it damages something inside a protected zone within 120 s of a shatter made within 2 km of the zone's edge (or outside zones, within 2 km of the victim), or when its unredirected path led onto the victim; throws and pushes are Hostile whenever their release path led onto the victim or its zone. Damage from events 2–9.6 km away within the window is Reckless, three Reckless hits on one victim owner in an hour escalate to Hostile, and hits under 1 MJ only log. Is "deliberate" drawn at the right distances (2 km Hostile, 9.6 km Reckless), or should every hit inside a zone be Hostile regardless of where the shot was? (Round 6: liability is decided by D33, the miner pays for legal-mining debris damage in a zone or to a station in every class; only the class distances stay open. D57: what each class costs is factions-and-law.md's; D72's cave case uses the same "there first" test.)
14. **Cave drill numbers (round 6, D74):** proposed 0.1 m³/s (~6× a hand drill, a quarter of the slowest wedge cycle), op radius 0.75–1.5 m, at most 2 working per body, with a built-in splitter-charge setter so a vehicle can advance its own tunnel. Right feel, or should vehicles depend on EVA crew for splitters?

These are dependencies, not questions: the surface gravity decision (README Q1) scales lane speeds and shear by sqrt(g) (and the gas giant's numbers in planets.md); the EM heat behaviour waits for exotic-matter.md; detection ranges are combat-and-sensors.md's (round 6); land, permits and offences are factions-and-law.md's.

Revision and review history: [history/rings-and-asteroids.md](history/rings-and-asteroids.md).
