# History of rings-and-asteroids.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

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
  - Fragments at up to 80 m/s (or 100 m/s from an AG kick) could be shattered or kicked from just outside `ProtectRadius` into a station. Added: asteroid debris does no hull damage inside protected zones, and chunks and clouds entering one become lost mass (open question 13). *(Superseded by revision 4: station shields and deflection; the debris stays real.)*
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

Lead review of revision 3, 2026-11-25, against tractor-beams.md, frames-and-zones.md, exotic-matter.md, planets.md and planets.md's "Gas giants" section (then proposed; resolved). The user's six answers were not re-litigated. Every fix below is made in place above, marked "review fix" where it changes a rule.

User answers, checked:
1. **Gone forever, effectively infinite, new ids, bounded storage: present and faithful.** Bits never clear, event ids carry a monotonic serial under the reserved source, and one population is live per cell. Three faithfulness gaps were fixed:
   - "~45 years to take 1 %" was 100× off: 1–3e4 naturals a day takes 1 % of the rocky ring in 5–17 months and all of it in 45–140 years. Still effectively infinite. Belts are now quantified too (1e8–1e9 bodies per belt; small fields can run out).
   - Storage was not bounded as claimed. Takeover wrote `Owner` into every footprint cell (up to ~50 KB per event, growing with time even where nobody mines), `RingEventLog` was kept forever, chip-sample marks on untouched naturals had no cap, and the per-type caps allowed ~84 MB per region against the 16 MB budget, with no fallback when no 30-day leftover exists. Now ownership is derived from footprints, superseded event records are dropped, sample marks count as cosmetic craters, and the compactor has a 20 MB hard ceiling.
   - The takeover erased players' work. It superseded worked naturals and caves, and the exclusion that was meant to prevent that (no spawned body or damage zone within 20 km) made nearly every mined window ineligible, since mined windows always hold re-railed leftovers. Now spawned bodies and worked naturals survive, and overlapping event slots are pre-removed once at takeover.
2. **Bystander damage: present and faithful.** Protected-zone rules are unchanged (revision 4 later replaced the protected-zone debris rule with station defence). The gap was attribution: only `Thrown` rocks (> 50 MJ) were attributed, so many 49 MJ throws were anonymous. Every beam-touched rock now carries `LastBeamOwner`.
3. **EVA sampling and cave mining: present and faithful.** The damage-zone rule keeps the user's packed-rock and wedge decisions intact. Fixes:
   - At the 64-zone cap, behaviour was undefined. Merging zones into bounding spheres would have grown the diggable volume for free, so splitters are refused at the cap instead.
   - `sample` items are excluded from every material path, not only refineries.
   - Caving memory (2–10 MB per character on the server) had no cap. It now has `CaveDetailMax` 32.
4. **Dense/sparse bands, colour and visibility, dust fog: present and faithful.** Every visibility, sensor-range and hit-odds number checks out (σ, `3/σ`, Lambert W ranges, 15 % per dense crossing). Note for the user's sign-off: "opaque" means ~45 % face-on at τ = 0.6 (fully opaque edge-on), plus the darkening. The rejection of ρ_b > 1 now says that particle τ (`TauRef`) can rise without touching body slots, if dense bands should look thicker.
5. **Gas giants: present.** Placement keeps rings in `BodyInertial`: R + 1.3 × 700 km + 10 km = 1.153 R < 1.2 R, matching frames-and-zones' `RotatingTop` and 1.3× leave rule. The ring numbers check (7.7/5.7 km/s, 98 min/4.1 h, 0.82/0.33 m/s shear, 11,719 lanes, 162k cells needing the widened arc field, Roche 3.7/1.9 R), except the body count, now ~2e10. Yields and hazards are reviewed in planets.md's "Gas giants" notes (resolved: no gas-giants.md exists). One fix: water from the giant is not an economic mixing-agent source, because ring ice-rock carries both halves of the agent.
6. **Tumbling, tractors, weapons: present, with one real exploit fixed.** `W_b` is emitter-relative, so tow-and-release (hold a rock, accelerate the tug, let go) gave untagged, uncapped rams, e.g. 20 GJ from 1e6 kg at 200 m/s. That contradicted "every rock weapon is bounded by `E_cap`/`EThrow`". Asteroid bodies now fade on `max(W_b, E_rail)`, the energy relative to the lane, and `Thrown` uses the same quantity. This is proposed to tractor-beams.md (Changes).
   - Also fixed: the `Thrown` tag timing out at 120 s reopened the coasting-through-parked-ships gap, which is closed by a 1 Hz look-ahead until 5 m/s.
   - Also fixed: rock-on-rock hits gave both rocks the full `½μv²` (2 × 55 % of the energy). They now get half each, so the example splits up to ~11 m whole, not ~16 m.

Consistency:
- **tractor-beams.md:** `BeamVMax` 100 m/s, `EThrow` = `E_cap` 500 MJ, lock-only conversion under `KinematicMassLimit`, grapples above it, refusals at S > 0.3 and in protected zones, "a tractor never fractures rock", despin across the beam, the 50 MJ tag, the 6-a-minute count and the zone line all match. Only the rail-relative energy cap is new.
- **frames-and-zones.md:** gas giant bands (`RotatingTop = max(atmo top, 0.05 R)` = 700 km, leave at 910 km) and terrain-free surface bubbles are consistent. Lane bubbles stepping because of spin is listed as a change there.
- **exotic-matter.md:** AM stays industrial (an optional belt trace knob defaults to 0 and is raised as its question). EM lenses for events come from the server-only `DepositSeed`. Cold cave extraction respects `T_em`. Item ids are aligned (`propellant.hydrogen`). The `MagneticScoop` and `RadiationDose` reuse is noted in planets.md "Gas giants" (resolved).
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

## Revision 4 notes

2026-11-25. Four user decisions, integrated in place. Read against exotic-matter.md (`PulseKick`, charges, gravity dampers, station anchors), wormholes.md (exclusion and permission), tractor-beams.md and README.md.

**Decisions integrated:**
1. **Station shields** (Station defence and hostile acts): a `StationShield` core with a lazy pool (capacity `E_max`, recharge `P_r`), absorbing shrapnel rays and the inward normal energy of debris bodies at a shell round the station; the excess leaks through as ordinary damage, so one hit bigger than the pool, or energy arriving faster than `P_r`, gets through. Stopping impulses are debited to the anchor (`EmPerNs`). Classes S/M/L at 200 MJ / 2 GJ / 20 GJ. Zero cost per tick when not hit. AG pulses and dose stay with gravity dampers and radiation shielding.
2. **Hostile acts:** a proposed minimal shared `HostileAct` record (~48 B, server only), with tracing (source event owner, thrower, `LastBeamOwner`, top heat contributor, charge owner), what counts (hull, component, character and **shield** damage to other parties), and classes Hostile / Reckless / Accidental from the forecast reach at the event tick and a 120 s window. A minimal station response (listing, docking refusal through the permission hook, deflection priority) stands in until a faction doc owns reputation, standing and crime.
3. **Debris stays real in protected zones:** the revision 2 review's "no hull damage, impulse only, lost mass" rule is withdrawn everywhere it appeared (summary, Manipulation, Abuse limits, Bystanders, Thrown rocks, Weapons, Anti-graviton charges, mining.json, tests, profiler rows, frames-and-zones changes, rejected alternatives), and so is the adopted tractor line zeroing `Thrown` contact damage in zones. Zones still stop rock being worked or broken inside them. A `StationDefence` controller watches at 2 Hz, prioritises (`Thrown`, listed actors, energy over time to impact) and assigns station tractors (Sweep or Hold) and `DebrisArm`s; capacity is one body per emitter or arm, ≤ 8 defence emitters, the class ranges and the station's tanks; what it can't serve is flagged `Unserved` and meets the shield alone. Undefended outposts take the hits, and their state is public on the HUD and in the forecasts.
4. **Whole-body shatter:** `E_split` is now the only shatter threshold. A shatter is a through-crack (primary sites on a plane through the hit, the shot axis and the body centre), sites spread over the whole body (2–4 large pieces), pieces flung outward from the centre with a downrange bias, fines as before, and half the lost fines held as a **dust cloud** (the ring dust model as a sphere: blinding optics for about a minute after a railgun shot on an 8 m rock, for its whole 10-minute life after a capital shot). Below `E_split` a hit craters and cracks; from `E_th` (the old regional threshold) it is a **crack zone** with a damage zone and no pieces, counted against the event rate. AmBlast follows the same whole-body-or-crack rule. Old open question 7 answered and removed, along with question 8 (decision 1–3).

**Numbers rechecked:** `E_split = 4·G0·F(10)·(0.8D)²` (silicate 9.5e5·D² J): 2 m 3.8 MJ, railgun ≤ 13 m, mass driver ≤ 6.5 m, ram (375 MJ of crack energy) ≤ 40 m, `E_cap` ≤ 46 m, cracked through ≤ 73 m; metal ≤ 7.9 / 3.9 / 24 / 28 / 44 m. The old regional reach columns (42 / 83 / 254 / 294 m silicate) are now crack-zone reach. Dust: `τ_c = 3 k_d M_d / (2π r²)`, so 2e4 kg clears at r ≈ 3.1 km (~4.7 min at 11 m/s). Station braking: `m v²/(2L)`, ~1 MN for a capped throw over 500 m.

**Consequences worth knowing:**
- Weapons now break only small rocks (silicate ≤ 46 m, ≤ 73 m cracked through); the old kill-vehicle "half a 64 m rock" outcome needs two or three cracking hits first, then breaks it whole. Big rocks are mined, and guns help by cracking (crack zones lower `G` and `H` × 0.4 where wedges seat). The yield table gains "guns crack, then wedge".
- Crack zones give EVA crews damage zones from gunfire, inside the fracture-event rate limit.
- `FractureEvent` grows by 8 B on Shatter and AmBlast (dust fields); `VoxelBody` gains `SourceEvent`, `SourceOwner` and `LastBeamOwner`.
- Station defence emitters are exempt from tractor-beams.md's 4-per-owner cap (8 per station, inside the bubble's 16; superseded by review: reserved slots outside the 16) and never set `LastBeamOwner`, so pushed-out debris stays its maker's.

**New open questions:** 12 (shield and defence numbers; ships without shields) and 13 (the hostile window and reach). Old 9–13 renumbered 7–11.

**Changes proposed to other docs:** README (decided rows, questions dropped, faction system missing), frames-and-zones (zone rule, shield sensor, profiler rows), ship-hulls (`StationShield`, `DebrisArm`), tractor-beams.md revision 2 (station defence, the withdrawn `Thrown` zone line, cloud sweeps, `max(W_b, E_rail)`), exotic-matter (shields complement dampers; anchor debit; charge owner as actor), review.md, and future combat and faction docs. Nothing outside this copy was edited, and nothing was built or run.

## Revision 4 review notes

Lead review of revision 4, 2026-11-25, against exotic-matter.md (`PulseKick`, charges, gravity dampers, station anchors, the "stations defend with equipment, not rules" line), wormholes.md and warp.md (exclusion radius 5 km, permission hook), tractor-beams.md revision 2 and README.md. The four decisions were not re-litigated. Every fix is made in place above, marked "review fix" or "(review)".

**Decisions, checked:**
1. **Shields: faithful.** Capacity + recharge, leak past the pool or above `P_r`, shrapnel and debris absorbed, AG kicks and dose left to dampers and radiation shields (consistent with exotic-matter). Fixed: the 2 m/s speed gate let a slow, massive rock (1e8 kg at 1.9 m/s, ~180 MJ) pass untouched; the gate is now 10 kJ of inward energy. Slow coasting rocks crossed the shell unseen (the coasting look-ahead runs only above 5 m/s); they are now scheduled against shells at coast start.
2. **Hostile acts: faithful in kind, too broad and too narrow in detail.** Too broad: any hit within 120 s inside the 9.6 km forecast reach was Hostile, so ordinary mining near anyone was a hostile act, against the "deliberately" in the decision. Too narrow: the fixed 120 s window let aimed slow debris off (a 32 m/s throw from just outside the zone arrives after ~150 s; big shatter pieces fly at 13–17 m/s). Now: aimed bodies (thrown, pushed, kicked) are classed by their release path whatever the arrival time, and keep their `Thrown` tag while that path leads into a zone; fracture debris is Hostile inside a zone from shots within 2 km of its edge, or outside zones within 2 km of the victim, Reckless out to 9.6 km, with escalation on repeats, a 1 MJ floor and an anti-farming rule. Q13 is rewritten to ask about the distances.
3. **Debris stays real, stations push it out: faithful, and no leftover says otherwise.** Searched for "lost mass", "no hull damage", "impulse only", "harmless", "zeroed", "suppressed": every hit is either withdrawn and marked (struck through or "superseded") or refers to rock *inside* zones being inert to weapons, which is kept (zones still stop rock being worked or broken there). Fixed: the defence exemption would have let a station (or any permitted party) lock, and so free, natural rail rocks inside its zone; it now covers Dynamic and Coasting debris and clouds only.
4. **Whole-body shatter: faithful.** Through-crack, 2–4 large pieces flung apart, dust fog for minutes, crater + cracks below; the old regional range is the crack zone. Old Q7 (now this decision) and Q8 are gone from the list. One stale number fixed: the autocannon's cold crack-zone floor is 0.30 MJ (ice-rock `E_th` at 2–4 m), not 0.33.

**Numbers rechecked (all hold unless noted):**
- `E_split = 43.2·G0·D²`: silicate 3.8 / 15 / 61 / 243 / 972 MJ at 2–32 m; the ice-rock, carbonaceous and metal rows; one-hit limits 76 / 62 / 46 / 28 m (cracked through 120 / 98 / 73 / 44 m); railgun ≤ 13 m, mass driver ≤ 6.5 m, ram (375 MJ of crack energy) ≤ 40 m.
- Worked examples: the 16 m crack zone (R_f 5.2 m, crater 0.05 m³), the 64 m one (R_f 10.4 m, crater 0.2 m³), the 8 m overkill (f_fines 0.22, E_kin ≈ 71 MJ over ~2.9e5 kg, v̄ ≈ 22 m/s, large pieces 13–17 m/s), the kill vehicle (~1.9e8 kg, v̄ ≈ 2.5 m/s, ~8e6 kg of dust).
- Dust: σ = 0.16 /m at 10 s and 8e-4 /m at 60 s in the 8 m case (visibility ~19 m, then ~3.7 km; T ≈ 0.34 through the centre); τ_c < 0.05 at r ≈ 3.1 km (~4.7 min). The capital cloud: ~25 m of visibility at 5 min, ~190 m at 10 min before the fade. `k_d` 50 m²/kg is under the ~110 m²/kg geometric value for 10 µm silicate grains, so the clouds are, if anything, conservative.
- Shields against fragment energies: S holds ~11 fragment hits of 18 MJ (a 1e4 kg piece at 60 m/s) and leaks 300 MJ of a capped throw; refill times 100 / 100 / 200 s; stopping a capped throw debits ~32 mg of EM. Added the check that matters: shatters can only happen ≥ 5 km from a station, and pieces fly in every direction, so a shell catches ~4e-4 (S) to ~4 % (L) of them: ≤ ~2 MW per player at the event rate. **Cheap gunfire shatters cannot overwhelm a shield** (~50 players against an L recharge), and detonation shrapnel (2.25 km) never reaches one. Thrown rocks are the real drain: one player sustains ~50 MW (6 × 500 MJ a minute), past an M shield's recharge.
- Deflection: braking a capped throw over 500 m needs ~1 MN, a 1e8 kg boulder at 3 m/s ~0.9 MN; force is never the limit; count, reach (S 150 m, M 500 m: 2–6 s for an 80 m/s chunk) and time are. Correct as written.

**Exploits found and closed:**
- **Baiting station defences:** decoys held emitters (a long Hold on a slow boulder, or cheap grit clouds). Clouds now have zero priority and use idle emitters only; a higher-priority threat preempts an emitter whose body is already on a missing path.
- **Slot denial:** defence beams sat inside the bubble's 16 beams, so four visitors with 4 beams each could switch a station's defence off. They now use 8 reserved slots per station.
- **Station pushback as a weapon:** the flat 2–5 m/s release gave a 1e8 kg boulder up to 1.25 GJ relative to the station, more than `E_cap`, unattributed. Release is now capped at 10 MJ (0.45 m/s for 1e8 kg), the direction is the controller's, chosen for clearance from every entity and the approach lanes, defence beams deliver nothing to ships, and debris a defence push redirected is Accidental for its maker.
- **Pushback that never ends:** a radial push in a lane bubble is a closed Hill ellipse that returns after one orbit (61–109 min), so the station would have re-pushed the same rocks every hour. The release test now runs over a lane period and prefers along-track pushes, which drift away.
- **Overwhelming shields with gunfire shatters:** not viable (above); no change needed beyond stating it.
- **Framing via someone else's fracture events:** (a) rounds or a hull shove redirecting another party's fragment left the miner as actor: any redirect (≥ 0.5 m/s from rounds, a hull contact, an AG kick) now sets `LastBeamOwner`; (b) topping up a miner's Critical site with weapon heat made the miner the actor of the detonation: a non-owner with ≥ 10 % of the heat is now the actor; (c) shooting someone's AM trap made the trap owner the AmBlast actor: the hitter is now, and the same rule is proposed to exotic-matter for carried charges; (d) parking a cheap hull next to a mining rock to farm Hostile records: "came to the hazard" caps that at Reckless with no escalation.
- **Persistent smoke:** overflow merges took the newer birth tick, so a trickle of shatters kept a cloud alive forever; the birth tick is now mass-weighted.

**Performance:**
- The watch walked the 64 m entity grid over 1.5–5 km (10⁴–10⁶ cells) and polled every coasting body per station; it now reads the bubble's asteroid list (≤ 256) and schedules coasting arrivals once per coast start. Idle cost stays ~1 µs per station per watch; a fully engaged station is ≤ ~10 µs a tick.
- The shield sensor on `Moving` would pair with every docked ship inside a 2 km shell each step; dynamic asteroid bodies now have their own `AsteroidMoving` layer, and the sensor sees only that and `Debris`.
- Dust clouds were per bubble, but they grow past a bubble and sensor segments cross bubbles: now one server-wide list in 4 km cells (≤ 8 per cell, ≤ 256 per server), typically 0–3 clouds per sensor segment at ~20 ns each. GPU cost unchanged (~0.01–0.03 ms, ≤ 8 in view).
- The `HostileAct` "unredirected since birth" rule is one flag bit; classification costs nothing until a hit.

**Consistency:** exotic-matter (dampers stop kicks inside their 5 km, so kicked debris arrives from outside; anchor debit at `EmPerNs` 1e-12 kg/N·s; "equipment, not rules") matches. warp.md's station `ExclusionRadius` (5 km) equals `ProtectRadius`, and the docking refusal now names its permission hook correctly (warp.md's, not exotic-matter's). wormholes.md's exclusion and permission rules are untouched. tractor-beams.md revision 2 was corrected to match (debris-only exemption, reserved slots, release energy, braking never faded for any body).

**Not changed, flagged:**
- The revision 4 notes above still say the defence emitters sit inside the bubble's 16; the reserved slots here supersede that. (Resolved r6: that note is marked superseded.)
- A station anchored (not orbiting) inside a ring slab would have the whole ring flow past it at km/s; nothing here handles that. Stations in rings are assumed to co-move with their lane.
- Every cost in this section is an estimate for the M5b and M9 exits to measure.

## Consistency pass (r6) notes

- User decisions: added D26 (tractors are a weapon unless requested; `TractorLock`), D27 (sieges, capture preferred), D28 (no AM, pull only), D33 (miner always liable for legal-mining debris damage near a station).
- Shared incident record (conflict 14): `HostileAct`/`HostileKind`/`HostileClass` renamed to `IncidentRecord`/`IncidentKind`/`IncidentClass` in the live design (historic notes untouched); kinds add `TractorLock`, `WarpBlast`, `WarpCollision`, `WarpBrakeCut`, `BeaconTamper`, `StationCapture`, `StationDrop`; class adds `Fined`; record adds `Liable`; the "no shared record" sentence now says it is shared with warp.md and tractor-beams.md, and `HostileAct` is the Hostile-class view. `HostileActs` runtime class and file renamed `Incidents`.
- D33 (conflict 13): `Liable` field, a Liability bullet in Class, "logged only" became "logged, miner liable" for in-zone and station damage, the come-to-the-hazard cap noted as outside zones only, Undefended outposts cites D33, open Q13 notes liability decided and only distances open.
- D28 (conflict 9): `StationDefence` assigns AM-fed emitters only (Hardware and Capacity); Hold reel-out is Hold assist (AM); the public HUD's deflector count is AM-fed emitters only; throws, rubble sweeps and the TractorBeam tool row note pull-only without AM.
- D27 (conflict 12): a "Sieges" bullet under Station defence and hostile acts (what is lifted, what is kept, barriers to destruction, capture via `CommandCore`).
- Stale refs (conflict 16): gas-giants.md / "proposed gas-giants section" now point at planets.md "Gas giants" (resolved); the revision 4 "inside the bubble's 16" note marked superseded by review, and its flag marked resolved.

## Round 6 notes

Round 6 (2026-11-26) applies D72, D73, D74 and D55/D56 from decisions.md. No user decision is reopened. Nothing was built or run; the new numbers are proposals.

**Edits in this doc:**
- **Header and User decisions:** a round 6 sentence in the status line; the revision 3 replenishment decision reworded to "modest events (asteroid captures and the like)"; new bullets for D72, D73, D74 and D55/D56.
- **D72 (cavers and other parties):**
  - Damage zones and caves: a new "Other parties and cavers" bullet. Physics unchanged (anyone may heat, wedge or shoot an occupied rock; no claim by presence). Hurting people is illegal, by reference to factions-and-law.md. New facts for that ruling: per-body cave occupancy (updated only on cave-detail enter and leave), `OccupiedAtFirstEvent` on the actor's first-event stamp, an `Occupied` bit on forecasts, scans and the `HazardBeacon`, and a one-off `SiteOpened` beacon to the cavers.
  - Abuse limits: "Cave miners get no special protection (open question 3)" became "no physical protection (decided)", with the law reference; the beacon bullet mentions `Occupied`.
  - Summary (Bystanders), the Multiplayer table (`HazardBeacon` row), the `caves` data block and the `HazardBeacon` struct comment updated.
- **D73 (modest replenishment):**
  - "Replenishment events" rewritten: kinds are `Capture`, `MinorCollision` and `FieldCapture`. Comet breakup and moonlet shattering are removed, and so are the 200–20,000-body clusters, the flash and dust-shell VFX, and the system-wide announcement and "gold rush".
  - Rates cut (a capture per 2 days per ring, a minor collision a week, a field capture per 2 weeks per belt); footprints cut to 0.5–2 km × 2–25 km; newcomers 1–12 (capture) or 50–300 (minor collision).
  - Content changed so a footprint is the band's ordinary density plus the newcomers. The draft's Gaussian cluster alone would have emptied a footprint's cells for a dozen rocks.
  - The swap is no longer shown under a flash: a window is rejected while any player is within `EventHideKm` (130 km), else the event waits an hour. `RingEvent` is sent only on interest entry.
  - Follow-on edits: the summary's Permanent mining bullet, the dust sentence under bands, "Effectively infinite", the Multiplayer bullet on events, the `RingEvent` row, the cost row, `events.json`, `RingEventKind`, the `RingEventRecord` comment, `RingEventVfx.cs` (withdrawn), the limits table (`EventHideKm`) and the milestone test line.
- **D74 (cave drills and tunnelling vehicles):**
  - Damage zones and caves: a new bullet. A `CaveDrill` core (ship-mounted, or on a small grid, which is a tunnelling vehicle), under the same Diggable test as the hand drill: 0.1 m³/s, op radius 0.75–1.5 m, its own splitter-charge setter, ≤ 2 per body, one `CaveDetailMax` slot per working vehicle, `regulated: true`.
  - The asteroid-side rule: in a held band it needs a band permit with `Tunnel`; an op without the right still runs and is counted toward `IllegalDig` or `ClaimJump`; free in unheld belts; protected zones keep their ban. Land ownership and approval on planets are left to planets.md and factions-and-law.md.
  - Tools as components (new `CaveDrill` row), the summary's EVA bullet, Abuse limits (hand digging line), the limits table, the `caves` data block and Files updated.
- **D55/D56 (weapons, shields and sensors owned by combat-and-sensors.md):**
  - Weapons against asteroids: the intro points at that doc. "Reference projectile classes (placeholders)" became "What the kinetic classes do to rock", keyed by that doc's weapon ids (`autocannon.s`, `massdriver.m`, `railgun.l`, `massdriver.xl`). Energies and every threshold are unchanged. "Kill vehicle" renamed in the two worked examples.
  - Energy weapons table: rows keyed by that doc's ids; "placeholder" removed; the plasma spot follows the weapon (0.3 / 0.5 m). `c_w`, deposit depth and the rock notes stay owned here.
  - Weapon-def examples use that doc's ids (`weapon.railgun.l`, `weapon.laser.m`, `weapon.pbeam.m`).
  - `StationShield`: the "proposed hook" for weapons fire is marked taken; ship shields (particle, energy, gravity) are that doc's; the class table is labelled station-only. The bullet on mining tools as anti-hull weapons points at that doc.
  - Fog and sensors: a lead sentence on ownership; the coupling list names all six `SensorKind`s (adds Gravimetric 0 / 0 and CrewScan 1 / 0.5, as that doc's table); the range rule is labelled as the medium's effect only; the stealth tactic is marked decided. `SensorKind` and `ring-obscuration.json` updated.
- **D57 touch-ups (pointers only):** "Hostile acts and the incident record", Consumers, the `Fined` note and Sieges now say factions-and-law.md owns the record, its costs and sieges, and wins where it differs. (r7: the struct, enums and interim rules were then replaced by pointers.)
- **Open questions:** 3, 5, 7, 9 and 11 struck as decided, 12 half decided (D55), 13 annotated; numbers kept. New 14 (cave drill numbers). The dependency line no longer waits for a sensor doc.
- **Changes to other pivot docs:** stale "future combat / sensor / faction doc" lines annotated, "ship shields are not proposed" struck, and a "Round 6" block added.

**Changes another doc needs:**
- **factions-and-law.md:** a band form of `LandRights.Check` for asteroid tools (`CaveDrill`, hand tools). The per-body occupancy and `OccupiedAtFirstEvent` defined here are what its "People in caves" ruling reads. Its remaining asks of this doc (struct and enums replaced by a pointer, `OwnerId`, drop `Fined`, delete the minimal station response and `CaptureTicks`, `src/Sim/Law/Incidents.cs`, `CaseNotice`) were done in the r7 consistency pass.
- **combat-and-sensors.md:** a `CaveDrill` row in "Mining tools and other things used as weapons". Its two remaining asks (the station watch using a `ContactTable`, and `e_hull` → `eCav`) were done in the r7 consistency pass.
- **planets.md:** the planet side of D74 (rate and op radius of a `CaveDrill` or tunnelling vehicle against planet voxels, with the land check per stroke).
- **ship-design-and-construction.md:** the `CaveDrill` core family (`regulated: true`, charge setter).
- **ai-ships-and-crew.md:** realised NPC crew in caves count toward occupancy; an NPC hurt there is a `PersonHarm` record.
- **README.md:** decided rows for D72, D73 and D74; the rings row's replenishment wording; drop rings questions 3, 5, 7, 9 and 11 from the collected list.
- **review.md:** unseen event placement is a rule now (`EventHideKm`); combat, sensor and faction docs are no longer missing systems.

**Not changed, flagged:**
- Historic revision and review notes (revisions 2–4) still mention comet breakups, moonlet shattering, the system-wide announcement and the "kill vehicle". They are history and are superseded by the text above.
- `EventHideKm` assumes impostors draw to at most 120 km. If M5 raises that range, the hide distance follows it.
- A capture's foreign composition is the only thing that makes it worth finding. If that still reads as a rush, set its composition to the band's in data.
