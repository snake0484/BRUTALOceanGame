# Combat, shields and sensors

Status: design, nothing built. Written 2026-11-26 against decisions.md (D9-D74), README.md, ship-hulls.md, tractor-beams.md, rings-and-asteroids.md, exotic-matter.md, wormholes.md, warp.md, frames-and-zones.md, ship-atmosphere.md, ship-design-and-construction.md, ai-ships-and-crew.md and factions-and-law.md (written the same day; it owns `IncidentRecord`, the law, off-screen wars and region shields). Every number is a data knob and every cost is an estimate to measure in the "Combat" and "Sensors" profiler rows. This doc owns weapon stats, hull damage from weapons, ship shields and sensors. It takes over the placeholders other docs left for it and lists the edits they need in "Changes to other docs".

## User decisions this doc implements

Recorded in decisions.md. Not re-litigated here.

- **D55** Ships get shields of three kinds: particle, energy and gravity.
- **D56** A combat and sensors design: weapon stats, sensor kinds and ranges.
- **D59** Hull damage has three forms: holes, dents and deformation.
- **D58** Warp exits used as a weapon are illegal, and planetary or city shielding may prevent the damage: factions-and-law.md owns those region shields; this doc routes weapons through them (section 3).
- **Hooks:** D53 (the pilot matters: skill and style), D50 (far ships are data unless they get into a fight), D54 (factions fight unseen wars), D48 (crew positions can be scanned), D49 (surrender), D34 (station shields), D41/D61-D63 (air barriers and emergency shields), D26/D28 (tractors), D66 (warp space is real space), D71 (wiring is power and data).

## Recommendation in one paragraph

Every weapon reduces to one server-resolved **`HullHit`**: an energy, a momentum, a contact area and a kind, marched through the target's baked 0.5 m occupancy by a DDA that needs no Jolt ray cast. One cost law (`e_pen · A · (1 + (v/v_h)²)` joules per metre) decides how deep a hit goes, and what it deposits becomes one of **three damage forms**: a **hole** (a subtract brush, as today), a **dent** (a displacement record that moves material and removes none) or a **deformation** (a plastic hinge at a structure-graph cut that rotates everything beyond it). Dents and bends are a **map from material space to ship space**, applied to vertices and collision points; the bake, rooms, mass and wiring stay in material space, so no brick re-bakes, mass is conserved exactly, and a dent costs 28 B. **Rounds, beams and missiles live in frame space in double precision, not in bubbles**, so ships in different bubbles or on rails fight without any merge: a far target is promoted to a **rails combatant** (rails pose plus a lazily baked hull, no Jolt body) and hits are resolved on it as data. **Shields** are three devices with three geometries and three counters: particle (facing panels, a pool, stops small matter), energy (a bubble, a heat pool, stops beams and dose) and gravity (per-emitter lobes, EM and antimatter per newton-second, deflects mass and is struck in tunnels). **Sensors** give contacts a quality level from one signal-excess formula per kind (optical, thermal, radar, gravimetric, signature, crew scan), and the server sends a client only what its contacts or its eyes could know. Fights a player can see but is not in are resolved by salvo arithmetic on ratings derived from the same data, and those ratings are the strength that factions-and-law.md's war step counts.

## 1. Weapons

### One struct for every hit

```
HullHit { Target, Ray (origin, dir: ship-local), EnergyJ, MomentumNs, AreaM2, SpeedMps, Kind, Shooter (OwnerId), Weapon, Tick, Seq }
HitKind = Kinetic, Blunt, Thermal, Particle, Plasma, Annihilation
```

Weapons differ only in how they make hits (a simulated round, a beam sample, a warhead) and in six numbers of data. Section 2 defines what a hit does.

### Kinetic: autocannon to capital mass driver

This table replaces rings-and-asteroids.md's "Reference projectile classes" placeholders. The four energies are unchanged, so every shatter and crack table there stands. The "kinetic kill vehicle (capital)" is the capital mass driver's shell.

| Weapon (`data/components/weapons/`) | Mount | Round | Muzzle | `E_p` | Momentum | Rate | Bus energy per shot (η) | Heat per shot | Life / reach |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Autocannon `autocannon.s` | S turret or fixed | 0.4 kg, `A` 12.6 cm² | 1.2 km/s | 0.29 MJ | 480 N·s | 10 /s | cased propellant; 30 kW drive | 0.06 MJ | 4 s / 4.8 km |
| Mass driver `massdriver.m` | M turret | 20 kg, `A` 200 cm² | 2 km/s | 40 MJ | 4e4 N·s | 1 per 3 s | 100 MJ (0.4), 33 MW at full rate | 60 MJ | 15 s / 30 km |
| Railgun `railgun.l` | L turret or spinal | 5 kg, `A` 40 cm² | 8 km/s | 160 MJ | 4e4 N·s | 1 per 6 s | 457 MJ (0.35), 76 MW | 297 MJ | 20 s / 160 km |
| Capital mass driver `massdriver.xl` | spinal, grid ≥ 5,000 t or station | 500 kg, `A` 0.2 m² | 3 km/s | 2.25 GJ | 1.5e6 N·s | 1 per 20 s | 5 GJ (0.45), 250 MW | 2.75 GJ | 60 s / 180 km |

- **Ammo** is items in a `Magazine` component (autocannon rounds 1 kg cased, slugs inert at their mass). A turret holds a ready magazine (600 / 40 / 60 / 20 rounds) refilled by an autoloader over a mechanical feed route (laid like a pipe, ≤ 16 m) or by hand. Cased ammo cooks off when its magazine reaches Integrity 0: a `Blunt` hit of 0.1 MJ per round left, from inside. Slugs do not burn.
- **Power:** every electric gun charges its own capacitor (2 / 1 / 1 shots) from the bus at power class 3 (charging). A browned-out bus slows the rate; it never fires a weak shot.
- **Heat:** each weapon has a heat capacity and a cooling rate (the tractor emitter's pattern): autocannon 30 MJ / 0.2 MW (75 s of continuous fire), mass driver 600 MJ / 8 MW, railgun 3 GJ / 25 MW, capital 20 GJ / 100 MW. At capacity the weapon locks out until it is under 50 %. Cooling dumps into the ship's thermal ledger (section 4), so firing is seen.
- **Recoil** is the round's momentum on the mount in the force pass, opposite the shot. A capital shell moves a 20,000 t ship by 0.075 m/s. Mount ratings (tractor-beams.md table gains a row): 3 × momentum / 0.1 s, so 15 kN, 1.2 MN, 1.2 MN and 45 MN. An under-built mount tears off under its own gun, through the existing mount-break rule.
- **Wear:** a railgun barrel loses 0.25 % Integrity a shot (400 shots, then a re-line part).
- **Reach in atmosphere:** rounds take drag `½ρv²·C_d·A` (ρ sampled at 10 Hz): an autocannon round's speed e-folds in 4.5 km of sea-level air, a railgun slug's in 22 km.

### Energy: lasers, particle beams, plasma

Couplings to rock stay as rings-and-asteroids.md lists them (`c_w` 0.20 / 0.30 / 0.10); that doc's wall-plug figures are the ones used here.

| Weapon | Bus | At the target | Spot | Effective / max range | Heat (capacity / cooling) |
| --- | --- | --- | --- | --- | --- |
| Pulsed laser `laser.s` | 2 MW | 0.6 MW | 0.05 m + 10 µrad | 4.8 km / 20 km | 1.4 MW (80 MJ / 1 MW) |
| Pulsed laser `laser.m` | 20 MW | 6 MW | 0.05 m + 5 µrad | 52 km / 200 km | 14 MW (800 MJ / 8 MW) |
| Pulsed laser `laser.l` | 200 MW | 60 MW | 0.05 m + 2 µrad | 460 km / 1,500 km | 140 MW (8 GJ / 80 MW) |
| Particle beam `pbeam.m` | 40 MW | 10 MW | 0.1 m + 50 µrad | 18 km / 60 km | 30 MW (1.5 GJ / 15 MW) |
| Particle beam `pbeam.l` | 400 MW | 100 MW | 0.1 m + 20 µrad | 45 km / 150 km | 300 MW (15 GJ / 150 MW) |
| Plasma cannon `plasma.s` | 20 MW | 5 MJ bolt, 2 /s, 3 km/s | 0.3 m | 9 km (bolt life 3 s, e-fold 2 s) | 10 MW (300 MJ / 5 MW) |
| Plasma cannon `plasma.m` | 50 MW | 50 MJ bolt, 1 per 2 s, 3 km/s | 0.5 m | 18 km (life 6 s, e-fold 4 s) | 25 MW (1 GJ / 12 MW) |

- **Power:** beams draw straight from the bus at class 3 while firing; plasma cannons charge a one-shot capacitor like the electric guns. None uses ammunition.
- **Lasers** are `Thermal` hits. A pulsed beam removes hull at `P / e_abl` (standard hull 5e9 J/m³: 0.12, 1.2 and 12 L/s) while its intensity is above `I_abl` = 20 MW/m²; the effective range is where the spot falls below that. Past it the beam only heats (the target's thermal ledger) and dazzles optical and thermal sensors it is pointed at (their `R_ref` × 0.25 while lit). A laser bores a 5 cm pinhole through 0.5 m of standard hull in 33 s (S), 3.3 s (M) or 0.33 s (L): it is the weapon for sensors, turrets, radiators, cables, missiles and exposed crew, not for breaking hulls. Atmospheres cost range (extinction per km per `AtmosphereId`, 0.1 /km in clear sea-level air).
- **Particle beams** are `Particle` hits. They deposit along the first 3,000 kg/m² of matter in the path (2 m of standard hull, 0.75 m of armour), not at the surface. They remove almost nothing; they damage what is inside: components in the path lose Integrity as if the deposited energy had cratered them at `e_cav / 20` (cores flagged `radHard` at `e_cav / 2`), and rooms within 2 m of the path take 1 Gy/s per 10 MW through `RadiationDose`. Useless in air (range × `ρ_c/ρ`).
- **Plasma bolts** are simulated rounds with a decaying energy. On a hull a bolt is half `Blunt` (the dent weapon: a 50 MJ bolt leaves a 15 cm dish 3 m across on standard hull) and half `Thermal` over its wide spot (a shallow scorch, and its whole energy into the target's thermal ledger, which fills heat sinks and lights the target up for every thermal sensor).

### Antimatter: warheads and the lance

Exotic-matter.md owns both. This doc adds only delivery and how they meet shields and hulls.

- **Warheads** (1, 5 and 20 g micro-traps) detonate as an AM breach: a jagged subtract brush of 8, 14 and 22 m and the dose pulse. That brush stays as exotic-matter.md defines it (it is not run through the hit law). Around it this doc adds a `Blunt` ring: from the brush radius to twice it, walls take a dent of depth `0.1 · r_am · (1 − (d − r_am)/r_am)`, pushed away from the centre, so a breach leaves buckled plate round a hole, not a clean bite.
- **Annihilation lance:** 1 mg/s, 1 GW at the target to 2 km, `(2 km/d)²` out to 10 km. An `Annihilation` hit sampled at 10 Hz: it bores at `e_vap` (2e10 J/m³ for standard hull, exotic-matter.md's figure: 0.05 m³/s, a 0.25 m bore advancing 0.25 m/s) and doses the rooms round the bore. Muzzle loss and housing shielding are exotic-matter.md's.

### Anti-graviton charges

Exotic-matter.md owns the pulse (`PulseKick`, shear, warp disruption, stability drain). Delivered by a torpedo, a mine or a drop. One change proposed there under D59: the **shear** result becomes deformation first. The pulse's gradient across a hull is fed to the impulsive structural evaluation (section 2); cuts that yield bend, cuts that fail tear, and only what the structure graph cannot express (shear over a single node) still becomes exotic-matter.md's damage brushes. A capital ship caught at 0.3 R comes out bent, with jammed doors and a spinal gun that no longer lines up, before it comes out in pieces.

### Missiles and torpedoes

A missile is a row in the projectile pool with a guidance record. It has no Jolt body.

| Class | Mass | Burn | Δv | Seeker `R_ref` | Warhead | Kill energy (point defence) |
| --- | --- | --- | --- | --- | --- | --- |
| Missile S | 50 kg | 300 m/s² for 20 s | 6 km/s | 8 km | 20 MJ shaped charge (`Kinetic`, `A` 100 cm², 3 km/s jet) or 20 MJ fragmentation (`Blunt` plus 16 shrapnel rays) | 0.5 MJ |
| Missile M | 400 kg | 150 m/s² for 40 s | 6 km/s | 20 km | 200 MJ shaped charge, or AM S (1 g) | 2 MJ |
| Torpedo L | 3,000 kg | 60 m/s² for 120 s | 7.2 km/s | 40 km | AM M or L (5, 20 g), an AG charge casing (≤ 10 kg yield), or a 2,000 kg kinetic bus (`Kinetic`, at closing speed) | 10 MJ |

- **Guidance:** proportional navigation (N = 4) at 10 Hz on the seeker's own contact. A seeker is a small sensor of one kind (thermal, active radar, optical, gravimetric: homes on gravity shields and tractors, or signature: homes on radar and jammers) and uses section 4's detection function against its target and every decoy in its 20° cone; the strongest return wins a seeded draw. With a data link (a Data line from the launcher's fire control to its antenna) the missile flies on the launcher's track until its seeker acquires. No track and no seeker lock: it coasts, then self-destructs at burn-out + 30 s.
- **Launch** needs a Q2 track (section 4) or a blind bearing. Launchers hold 4 / 2 / 1 ready rounds and reload from a magazine.
- **Point defence:** any turret flagged `pd` (autocannon, lasers S and M) engages missile tracks its ship holds at Q2 inside its reach, nearest time-to-impact first. Lasers deposit into the missile's kill energy (an S laser kills an S missile in 0.8 s). Autocannon rounds in `pd` mode carry a 3 m proximity fuse against missiles only. A coasting missile is cold and small (radar cross-section 0.1 m²: an S radar tracks it from 5 km), so point defence is a last 5 km affair unless the ship carries better sensors.
- **Killing an AM torpedo is still a breach** at the point it dies (exotic-matter.md: hits breach). One gram doses unshielded crew to 5 Gy at 890 m. Kill them far out.
- **Caps:** 256 live missiles per server, 32 per owner group, counted with exotic-matter.md's mines-and-charges group caps for AM and AG payloads.

### Mining tools and other things used as weapons

No special cases: each makes ordinary hits.

| Tool | As a weapon |
| --- | --- |
| Mining `BeamLaser` (1.5 MW continuous, 0.3 m spot, 400 m) | `Thermal` below `I_abl` on hull (5 MW/m²): heats and scorches, never bores. Burns EVA crew, sensors and cables. A nuisance, and an incident |
| `WedgeLauncher` (60 kg at 250 m/s, 1.9 MJ, blade 120 cm²) | `Kinetic`: 0.26 m into standard hull, through a light wall. The wedge lodges; pulling it leaves the hole |
| `MiningDrill` (1 MW, contact) | Contact `Kinetic` at `e_cav`: 5 L/s of standard hull. A boarding tool, like the cutting torch |
| `CaveDrill` (rings-and-asteroids.md, D74; regulated equipment) | Contact `Kinetic` like the `MiningDrill`, at its own power. On a tunnelling vehicle it is a slow boarding ram; using it on another party's hull is an incident like any other tool |
| Cutting torch (ship-hulls.md) | Unchanged: swept-capsule subtract brushes |
| Splitter charge (EVA) | `Blunt`, 2 MJ at 0.3 m: a 6 cm dent in standard hull; breaks glazing, exposed components and anyone beside it. It does not hole a 0.5 m wall |
| Tractor beams | tractor-beams.md (D26: a weapon unless requested). Tears stay its; cuts that only yield now bend (section 2) |
| Thrown and shattered rock, rams, wall contact | `Blunt` from the contact impulse (section 2, "Contact and rams"). This is the "reduced-mass contact damage" rings-and-asteroids.md and wormholes.md refer to |
| Thruster exhaust | `Thermal` over the cone's footprint at 10 Hz, as rings-and-asteroids.md tests it against rock |
| Warp real-space leg, core runaways | warp.md and exotic-matter.md; their contacts and brushes pass through the same dent and bend rules |

### Simulation: rounds are data in frame space

- **Server authority.** A client sends `FireCommand {weapon group, tick, target or direction}`. The server owns turret pose (slew rates are data), the lead solution, dispersion, the round and the hit. Clients never report hits. A client draws its own muzzle flash and tracer at once from the same closed form; nothing is rolled back because nothing a client predicts has consequences.
- **Turrets aim themselves.** A designated target gets a lead solution from the fire-control computer using the shooter's best track of it (section 4): the solution's error is the track's error, so better sensors hit more. Fixed guns fire along the hull; a pilot's aim is the ship's attitude. A gunner seat can take a turret in manual: it follows the gunner's aim, with a lead pip drawn from the same track. Each turret has a baked **arc mask** (32 × 16 azimuth-elevation bits from its own hull's occupancy, ~0.1 ms on a worker per topology change); the server refuses shots into its own hull.
- **Simulated rounds** (kinetic rounds, plasma bolts, missiles): one SoA pool. A round is a position and velocity in the **inertial frame of its SOI**, in double. Each tick it advances by `v·dt` plus gravity (`g(p)`, refreshed at 10 Hz; a railgun slug drops 2 km over a 20 s flight at 1 g), drag in atmosphere, gravity-shield lobes and, in a tunnel, wormholes.md's field. Its tick segment is swept, in this order:
  1. **Bubbles:** against the bounding spheres of bubbles and rails combatants (section 5) the segment can reach (a coarse hash of ≤ 128 bubbles, ~15 ns each tested).
  2. **Entities in a bubble it crosses:** the segment is taken into the bubble's frame through the pose cache (double) and walked through that bubble's `SweepGrid` (256 m cells holding entity bounding spheres, rebuilt each tick only for bubbles with rounds or beams near: ~5 µs per bubble). Shield shells are tested here (section 3).
  3. **A hull whose sphere it meets:** the occupancy DDA of section 2. Characters outside a hull are capsules in the same grid.
  4. **Asteroids:** realised bodies by their SDF sphere-trace (rings-and-asteroids.md); **rail asteroids through `ProjectileRailSweep`** with its budget of **512 rounds a tick**, fed from this pool: only rounds whose segment is inside a ring slab or field bound are candidates (one AABB test), highest energy first; rounds under 1 MJ use the 10 Hz look-ahead on the stagger `(round + tick) % 6`, so the budget covers ~3,000 autocannon rounds in flight inside rings. Past it rings' rule holds (oldest low-energy rounds skip rails).
  5. **Terrain and sea:** below the body's terrain maximum, collision chunks' `TriangleBvh` where they exist, else the generator height at 10 Hz look-ahead. A ground hit of ≥ 10 MJ makes one crater edit (`r = (E / 5e8)^(1/3)` m, capped at 30 m as breaches are; ≤ 4 a second per shooter; planets.md's protected shores refuse it). The sea stops rounds.
- **Hits are queued and applied in the next tick's step 3**, in the order `(hit tick, AM breaches by TrapId, then hits by (shooter entity, weapon component, round sequence))`, which extends rings-and-asteroids.md's order. Cascades advance one hop a tick, as everywhere.
- **Beams** (lasers, particle beams, the lance, mining lasers) are hitscan samples at 10 Hz, staggered. A sample is one ray through the same five tests; its energy for 0.1 s goes into a `BeamSpot` accumulator keyed by the 0.25 m cell it lands in, so a jinking target smears a beam and a steady one drills. Hitscan is exact enough: light crosses 460 km in 1.5 ms.
- **Rounds through mouths and tunnels:** unchanged from wormholes.md. Rounds cross instant mouths with the exit clamp and ricochet in tunnels; beams stop at a mouth disc and at tunnel walls.
- **Protected zones** do not stop weapons physically (the one physical rule in a zone is rings-and-asteroids.md's: rock inside `ProtectRadius` does not crater, crack or shatter). Stations defend with hardware (section 3) and with the law: every hit on another party's hull, shield or crew writes an `IncidentRecord` of factions-and-law.md's kind `WeaponFire` (shield absorption counts, as it does for debris), with `ActorHull` the firing grid. That doc owns what follows. **Station turrets** are ordinary weapons on a station grid, aimed by its fire control from its own sensors' tracks; they fire on the response orders that doc gives (the Outlaw response in a Secure zone) and never on a surrendered ship. **Surrender (D49):** a ship surrenders by `Yield` or by striking colours (factions-and-law.md): its weapons are safed, its shields drop, its transponder comes on flagged and the surrender is broadcast; this doc sets the flag on any hit record against such a ship, and that doc judges it (`SurrenderViolation`, `Perfidy`).

## 2. Damage to hulls: holes, dents and deformation (D59)

### What the hull offers

Ship-hulls.md keeps, per baked brick on every peer, a 16³ bitset of 0.5 m solid cells and one of air cells. Tractor-beams.md builds a structure graph (4 m nodes, joints, cut capacities) lazily from the same occupancy. Jolt's native ray cast is not exported (review.md P1). So:

- **The hit test is a DDA through occupancy, in ship-local space.** A dense bit grid over the hull's brick bounds says which 8 m bricks exist (a 100 m ship: 260 bits); the ray steps bricks, then 0.5 m cells inside the surface bricks it meets. About 1 µs per ray per ship, zero allocations, no Jolt, the same on a rails hull with no body.
- **Material per coarse cell** is new: one byte per brick when the brick is one material (the common case), else 2 bits per cell into a 4-entry palette from a side pool (1 KB for mixed bricks only). (Change to ship-hulls.md `BrickState`.)
- **Materials gain five numbers** (`data/ship-materials`; laminates derive them from their layers, thickness-weighted, with the face layer's `e_abl` and the back layer's `spall`):

| Material | `e_pen` J/m³ (resists a penetrator) | `e_cav` J/m³ (energy per volume destroyed; rings' `e_hull`) | `e_def` J/m³ (per volume displaced) | `e_abl` J/m³ (pulsed thermal) | `spall` |
| --- | --- | --- | --- | --- | --- |
| `hull.light` | 3e8 | 1.0e8 | 2e7 | 4e9 | 0.3 |
| `hull.standard` | 6e8 | 2.0e8 | 4e7 | 5e9 | 0.3 |
| `hull.frame` | 1.5e9 | 6e8 | 1.2e8 | 8e9 | 0.2 |
| `hull.armour` | 4e9 | 1.2e9 | 2.4e8 | 1.2e10 | 0.5 (0.1 with a spall-liner layer) |
| glazing | 3e8 | 5e7 | brittle: no dent, it shatters | 2e9 | 0.8 |

`e_vap = 4 · e_abl` is the continuous-beam figure (2e10 for standard hull, the lance's).

### The hit law

The DDA yields **layers**: runs of solid cells of one material with thickness `t` along the ray (so a sloped plate is thicker, with no extra rule), separated by gaps. For each layer in turn, while the hit has energy:

```
c      = e_pen · A · (1 + (v / v_h)²)            J per metre of this material      v_h = 2 km/s
perforates  <=>  E > c · t
E_dep  = min(E, c · t)                           deposited in this layer;  E -= E_dep
hole radius through a perforated layer:   r_h = sqrt(c / (π · e_cav))
stopped in the layer at depth d = E / c:  crater V = E_dep / e_cav at the stop point (a sphere)
after a perforation at v > v_h, each gap of ≥ 0.5 m crossed:  A ×= 4   (the slug is debris now)
Blunt and Plasma:  v term = 0, A = the contact patch, and a share η_d of E_dep dents instead of cratering
Thermal:  no march. Removal V = E / e_abl (pulsed, above I_abl) or E / e_vap (continuous), bored along the ray at the spot radius
Particle: deposits E evenly over the first 3,000 kg/m² of solid in the path; no removal
```

- **Why this law.** At low speed the depth is `E / (e_pen·A)`: energy-limited. Far above `v_h` it tends to `m / (2·e_pen·A / v_h²)`: set by the slug's mass per area and not by its speed, which is the hydrodynamic limit real long rods obey. The extra energy a fast slug carries goes into width (`r_h` grows with `v`), so a railgun makes a wider hole than a mass driver slug four times its mass. Spaced plates work against hypervelocity rounds through the `A × 4` rule and do nothing against slow heavy ones.
- **Depth in solid material and hole radius, by weapon** (from the table data; "walls" are 0.5 m):

| Round | Light | Standard | Frame | Armour | Hole through a wall |
| --- | --- | --- | --- | --- | --- |
| Autocannon (0.29 MJ) | 0.56 m | 0.28 m | 0.11 m | 0.04 m | 4 cm pinhole |
| Mass driver (40 MJ) | 3.3 m | 1.7 m | 0.67 m | 0.25 m | 0.2 m |
| Railgun (160 MJ) | 7.8 m | 3.9 m | 1.6 m | 0.59 m | 0.26 m |
| Capital shell (2.25 GJ) | 11.5 m | 5.8 m | 2.3 m | 0.87 m | 0.8 m |

  So an autocannon perforates fairings, pits standard hull and scratches armour; a mass driver goes through three standard walls and is stopped by one armour plate; a railgun runs the length of a small ship and through one armour plate; a capital shell needs a metre of armour, and then puts 2.25 GJ and 1.5e6 N·s into the plate that stopped it.

- **What is in the gap matters.** Between layers the ray is tested against component housings (capsule or box per placement in the bricks crossed) and crew capsules (ai-ships-and-crew.md's casualty rule, on damage events only). A housing in the path is a layer of its own material; the energy deposited in it lowers its Integrity by the shared rule with `V_overlap = E_dep / e_cav` (exotic-matter.md's `min(1, 4·V_overlap/V_housing) / Rating`), so a pinhole channel through a reactor counts without a brush. Cables and pipes use ship-design's capsule test against the channel.
- **Spall.** A round that stops within two crater radii of a layer's back face, with air behind it, throws a spall cone into that room: half-angle 30°, 6 m, energy `0.1 · spall · E_dep`. Components in the cone take it as deposited energy; crew are wounded above 1 kJ/m² and killed above 20 kJ/m² at their distance. A shallow scab brush (0.25 m, cannot breach on its own, like tractor-beams.md's mount footprint) marks the face. Armour without a liner stops the shell and still hurts the room.
- **Ricochet.** Below `v_h`, at more than 80° from the normal (normal from the 3×3×3 occupancy gradient, ~30 ns), a round keeps half its energy, reflects, and leaves a crease dent.

### Form 1: holes

Unchanged in kind: jagged subtract brushes in `ShipHull.Damage`, coalesced, capped at 4096 per ship and 64 per brick. What this doc adds is where they come from and what stays below a brush:

- A perforation with `r_h ≥ 0.2 m` emits one capsule brush along the ray through that layer, radius `max(0.25, r_h)`. A crater of ≥ 0.065 m³ emits a sphere.
- **Smaller results never make a brush.** They go to the ship's `DamageAccum`, a hashed grid of 2 m cells (pool of 256 active cells per ship under fire, dropped after 60 s without a hit): removed volume, energy-weighted centroid, dent volume as a vector, and pinhole area per room pair. When a cell's removed volume reaches 0.065 m³ it emits one 0.25 m brush at the centroid (about 45 autocannon rounds on standard hull, as rings-and-asteroids.md accumulates craters into ops). **Pinholes add straight to a `Leak` edge's area** between the two regions they join (ship-atmosphere.md: an edge delta, no re-bake): a strafed cabin leaks through fifty 4 cm holes long before it has a hole a brush can show. Clients draw pits and pinholes as decals from `HullHitFx`.
- Brush emission is capped at 8 per ship per second (largest first, the rest merge into the accumulator), because ship-hulls.md's limit of 16 brick re-bakes a tick server-wide is the real throttle.
- Breaches, leaks, splits, component detachment and wiring faults follow from the brushes exactly as the owning docs say.

### Form 2: dents

The bake cannot dent: brushes add and subtract, and a dent moves material without removing it. Dents are therefore **not in the bake at all**.

- **A dent is a displacement record in material space** (28 B):

```
HullDent { Id, Pos (int32 ×3, 1/1024 m), Dir (octahedral 16 bit), Radius (1/256 m), Depth (int16, 1/1024 m, + inward, − a bulge),
           Kind (Dish, Crease, Bulge), Group, HalfLength (Crease), LenDir (octahedral) }
u(X) = Depth · Dir · w(ρ / Radius) · a(h)        ρ: distance from the dent axis (Crease: from its segment)
w(s) = (1 − s²)²  for s < 1, else 0              a(h) = 1 within ± max(1 m, 2·Depth) along the axis, smooth to 0 by twice that
ship-space position of material point X:  x = X + Σ u_k(X)      (≤ 16 dents per brick, summed in Id order, |Σu| clamped to 2 m)
```

- **Material moves, none is lost.** The displacement is along the axis and depends only on the distance from it, so it is a shear with zero divergence: volume is conserved exactly in the continuum. **Mass, centre of mass and inertia are computed from the undeformed bake**, so mass is conserved to the gram by construction and repeated dents cannot drift it; the neglected shift is centimetres (20 t of plate pushed in 1 m moves a 2,000 t ship's centre by 1 cm).
- **Where the map is applied:**
  - **Rendering:** in the hull vertex shader, from a per-ship dent buffer and a per-brick index list in the instance record (≤ 16). Normals come from the analytic gradient of `u`. No re-mesh, no vertex upload, and a new dent can ease in over a few frames.
  - **Exterior collision:** the convex hulls' points (≤ 64 each) of the 4 m cells a dent touches are displaced and those hulls rebuilt: one `ModifyShape` per section, as for a hole.
  - **Interior collision:** the simplified mesh the walk collision is built from has its vertices displaced when it is built or rebuilt for bricks near occupants.
  - **Everything else stays in material space:** the brush lists, the brick bake and its disk cache (a dent invalidates nothing), occupancy, rooms, air, wiring, the structure graph. A point query from the world (a character's room, a hit's position) maps back by `X ≈ x − Σu(x)` (first order, ~50 ns). **Damage brushes are stored in material space**, so a hole made before a dent moves with the plate, and one made after lands where it was hit. The hit DDA runs in material space with an undented ray: its error is at most the dent depth along the ray.
- **Size from energy.** The share of a hit that dents is `η_d · E_stop` (`η_d` 0.15 for a stopped kinetic round, 0.5 for `Blunt` and the blunt half of plasma), and `V_dent = η_d · E_stop / e_def = π·R²·δ / 3`. Aspect `R / δ` is 4 for a stopped round and 10 for blunt hits and contacts. Dents under δ = 0.1 m accumulate in `DamageAccum` and emit or deepen a record at that step (about 16 autocannon rounds on standard hull, about 90 on armour).
- **Merging and caps.** A new dent within `R/2` of an existing one facing the same way (dot > 0.9) merges: volumes add, depth and radius are re-derived. ≤ 16 per brick, ≤ 1024 per ship (28 KB); past a cap the smallest merges into its nearest neighbour. ≤ 4 dent records created or changed per ship per second.
- **A dent that goes too far tears.** At `δ / R > 0.35` the panel ruptures: the record stays and a hole brush of radius `0.3·R` is emitted at its centre.

### Form 3: deformation

Large loads bend a ship before they break it. The structure graph already knows where.

- **Plastic hinges at cuts.** Tractor-beams.md evaluates every MST cut and axis plane cut of a live ship and breaks one at `u ≥ 1`. New: a cut at `u ≥ YieldU` (0.8) **yields**. Each evaluation it stays there, the far side of the cut rotates about the cut's centroid by `BendRateDeg · (u − YieldU) / (1 − YieldU)` per second (2°/s at `u` → 1) about the axis of the cut moment, plus a shear offset along `F_t` at 0.1 m/s by the same factor. A hinge holds at most 12° and 1 m; past either the cut breaks by the existing tear rule. A yielded cut keeps 70 % of its capacities.
- **Impulsive loads use the same pass once.** Tractor loads are quasi-static; hits are not, and tractor-beams.md leaves rams, pulses and shear to "their own damage models". This is that model: any single event delivering ≥ `1e5 N·s` to a ship (a capital shell, a ram, a thrown rock, a warp-leg collision, an AG pulse's gradient, a breach) queues one structural evaluation with the impulse as a force `J / 0.1 s` at the hit node (a gradient as per-node forces). Cuts over `YieldU` take a one-shot bend of `ImpulseBendDeg · (u − YieldU)` (5° per unit); cuts over 1.5 tear. ≤ 8 impulse evaluations a tick server-wide, queued; the graph is built lazily on a worker as tractor-beams.md already does (~0.25 ms per 1,000 nodes), so the bend lands 1-3 ticks after the hit.
- **Representation: deform groups.** A hinge splits the ship's bricks into the two sides of its cut. Each side is a `DeformGroup` with a rigid transform `T_g` relative to the root group (smallest-three quaternion and a 1/1024 m offset). ≤ 8 groups per ship, so ≤ 7 hinges; a brick touches ≤ 2 hinges, and a cut that would exceed either limit tears instead. A hinge is 36 B: cut id, plane, axis, angle, shear, the two group ids.
- **The hinge band is continuous.** Vertices within `BandM` (4 m, one node) of the hinge plane blend the two transforms by smoothstep on the signed distance, in the same vertex shader, so the hull bends without a crack. Displacement in the band is at most 1.9 m, inside the 2 m clamp. Collision sections are children of the `MutableCompound` and take their group's transform; sections in the band are rebuilt with blended points.
- **Mass properties.** Mass is unchanged. The server recomputes centre of mass and inertia from the per-group moments and their transforms on each bend event and sends them (review.md X3 already has the server send mass properties, ~60 B); the flight computer re-allocates thrust.
- **Dents live in a group** (the `Group` byte), so they ride its transform.

### What dents and bends do to the ship

Evaluated on the event, for the things inside the dent's radius or across the hinge, never per tick.

| Affected | Rule |
| --- | --- |
| Doors, hatches, airlocks | Spread of `|u|` over the frame's four corners ≥ 3 cm: `Sticky` (quarter speed). ≥ 8 cm, or the frame in a hinge band past 2°: `Jammed` until forced (crowbar 20 s, or cut). Bulkhead doors jam open or shut as they stood |
| Room seal | A dent with `δ/R ≥ 0.2` on a wall bounding a room opens its seams: a `Leak` edge of `0.002 m² per metre of rim · (δ/R − 0.2) / 0.15`. A hinge band leaks `0.001 m²` per metre of section perimeter per degree. This is the slow leak after a collision |
| Room volume | Reduced by the dent volume inside it (one number; pressure rises accordingly), never below 10 % of design |
| Components | Mount displaced ≥ 5 cm: `Misaligned` by family. Thruster: vector off by `Δu / L`. Turret and fixed gun: aim bias 2 mrad per cm until a 60 s `Test`. Sensor: `R_ref` − 20 %. Warp coil: bubble quality penalty. Feed, pipe flange: jam or `Leak`. ≥ 30 cm: mount rating halved. A wall pushed into a housing counts the intruding volume × 0.5 as overlap in the Integrity rule |
| Across a hinge | Everything beyond it moves rigidly and keeps working, but: main thrust no longer passes through the centre of mass (trim, wasted thrust); a spinal gun whose barrel crosses the hinge is unusable past 0.2°; pipes, feeds and cable segments crossing the band are damaged per degree (cut at 6°); docking collars and landing gear beyond tolerance refuse to mate or sit crooked; linked warp drives on both sides lose quality with the angle |
| Wiring and pipes | A segment crossing a dent of δ ≥ 0.25 m is damaged, ≥ 0.6 m cut (ship-design's fault rules follow) |
| Wings | Dent volume over a strip's area adds drag and removes lift for that aero strip |
| Structure graph | Joints inside a dent lose up to half their capacity (`× (1 − 0.5·min(1, (δ/R)/0.35))`); the graph is marked dirty, rebuilt only if the ship is live |
| Crew | A capsule inside the swept volume of a dent with δ ≥ 0.5 m is wounded; ≥ 1 m, killed |
| Glazing | Never dents. Any dent over a window shatters it to a hole |
| Shields | Particle panels on a dented or bent facing lose coverage until re-tuned (section 3) |

### Contact and rams

A Jolt contact whose impulse `J` gives `E = J² / (2μ) ≥ 50 kJ` (reduced mass μ) is a `Blunt` hit on each hull: `A` is the contact patch (the manifold's extent, at least 0.5 m in radius), half the energy each. Blunt hits perforate only when `E_dep > e_pen · A · t`, which for a wide patch is a great deal, so collisions mostly dent and bend: a 300 t ship hitting a far heavier one at 20 m/s (60 MJ) leaves a 0.15 m dish 3 m across on each hull and usually a jammed door; above about 56 m/s a nose-on hit holes a standard wall, and at 80 m/s (0.96 GJ) its 2.4e7 N·s also runs the impulsive pass on both ships. Contacts taken on components flagged `contactRated` (landing feet, docking collars, clamps, bumpers) are theirs up to their rating and make no hit, so landing and docking are free. This replaces the undefined "reduced-mass contact damage" in rings-and-asteroids.md and wormholes.md.

### Armour: penetration, spall or dent

Armour plates are hull brushes of a laminate (ship-design-and-construction.md), so they are just layers to the hit law. What the designer is choosing:

| Threat | Against 0.5 m of standard hull | Against 0.5 m armour over standard | Design answer |
| --- | --- | --- | --- |
| Autocannon | Pits, pinholes after sustained fire on one spot | Scratches | Any armour |
| Mass driver | Perforates, 0.2 m hole, carries on inside | Stopped at half depth: crater, dent, spall into the room behind | Armour with a liner; keep crew off the outer rooms |
| Railgun | Through eight walls | Perforates the plate (0.26 m hole) with 15 % of its energy left; a gap behind it spreads that over four times the area and the next wall stops it | Spaced armour |
| Capital shell | Through the ship | Stopped by ≥ 0.87 m: metre-wide crater, a deep dent, and a bend from 1.5e6 N·s | Thickness, and structure (frame keels) behind it |
| Laser | Slow bore | 2.4 × slower (`e_abl`) | Ablative face layer; an energy shield |
| Particle beam | Reaches 2 m in | Reaches 0.75 m in | Mass in the way; `radHard` cores |
| Plasma | Dents, heats | Shallower dents, same heat | Heat sinks; a particle shield |
| AM breach, lance | Bite and dose | Smaller dose behind (areal density) | Mass, distance, energy shield |

### Repair

| Fix | What it does | Limits |
| --- | --- | --- |
| Holes | ship-atmosphere.md's ladder, unchanged: patch kit, weld repair, test | |
| **Press out** (portable hydraulic press, T1) | Removes one dent record | δ ≤ 0.3 m; 30 s + 60 s per 0.1 m³. An unskilled worker leaves a residual dent of 20 % depth (D67's poor-quality rule applied to pressing) |
| **Cut out and replace** (the weld repair step) | The panel within the dent's radius becomes a repair piece: plate stock by its volume, weld joints with quality. Removes the dent and any tear | Needed for δ > 0.3 m or torn dents; set time per metre of weld (D67) |
| **Re-align** (`Test` on the component) | Clears `Misaligned` once its mount is within 5 cm | 60 s |
| **Straighten** (yard jig, T2 bay) | Reduces a hinge's angle and shear to 0 and restores the cut's capacities | A yard job: 10 min per degree per 1,000 t beyond the hinge. In the field a hinge of ≤ 2° can be braced (a frame member welded across the band: stops further yield, keeps the angle) |

No dent or bend heals on its own at any simulation level (ai-ships-and-crew.md's no-free-healing rule): dock repair takes time and stock.

### Storage, network, determinism

- **Storage per ship:** damage brushes ≤ 4096 × 34 B (existing), dents ≤ 1024 × 28 B, hinges ≤ 7 × 36 B, `DamageAccum` 256 × 48 B while under fire only (not saved; flushed into dents and leaks on save). For AI hulls below L2 the `DamagePool` row grows from ≤ 64 brushes to ≤ 64 brushes + ≤ 32 dents + the hinges (2.2 KB to 3.4 KB).
- **Wire:** `HullDentMsg` 28 B and `HullBendMsg` 36 B + mass properties 60 B, reliable, to the ship's interest, with the same sequence numbers as damage brushes. `HullHitFx` (10 B: ship, local point int16 ×3, kind, energy q8) is unreliable, to interest, ≤ 20 a second per ship, for sparks, decals and sound.
- **Determinism:** the server alone resolves hits and creates records, in the hit order above; records are quantised integers. Clients apply them: rendering in float (visual), collision points for prediction by the same function. As review.md X3 rules, nothing relies on bit-equal floats across peers; rooms, leaks, jams, misalignment and mass come from the server as events.
- **LOD:** hull LOD ≥ 2 drops dents under 0.5 m depth; the whole-ship far bake and impostors ignore dents and keep hinges (a broken-backed ship reads as one from 20 km).

## 3. Shields (D55, D58)

Three devices, built on technology the docs already have, with three geometries and three ways to fail. None is a general damage sponge: each stops one class of thing and is blind to the others, so a ship's shields say what its owner expects to be shot with.

| | **Particle shield** | **Energy shield** | **Gravity shield** |
| --- | --- | --- | --- |
| What it is | A dense sheet of confined plasma held a few metres off the hull: the air barrier's plasma window (D41), made denser and backed by a stored energy pool | A field that absorbs radiation and re-radiates it as heat: the `RadiationShield` of wormholes.md and exotic-matter.md, grown up | Anti-graviton lobes that push mass away: a reflex push tractor (D28: push needs antimatter) |
| Stops | Small, fast matter: rounds and fragments up to a mass limit, shrapnel, grit, plasma bolts' matter, particle beams, small missiles; the lance, briefly | Light and dose: lasers, the thermal half of plasma, flares, belts, annihilation gamma, wormhole wall discharges | Anything with mass that it tracks: slugs, shells, missiles, rocks, rams, boarding craft |
| Does not stop | Heavy bodies, light, dose, gravity, tractors, people, ships, air | Any matter, gravity, tractors, particle beams | Light, particle beams, dose; anything while it is saturated or untracked |
| Geometry | **Facing panels**: each emitter covers the directions within 60° of its mount normal | **Bubble**: one ellipsoid round the grid | **Lobes**: a 30° cone per emitter, slewed onto threats |
| Limit | Pool (J) and recharge (W), per-hit cap, mass limit | Heat pool (J), dump rate (W), absorption cap (W) | Rated force (N), field cap (m/s²), threats tracked |
| Costs | Bus power, class 2 | Bus power, class 2; its dump is thermal signature | Bus power, plus EM and AM per newton-second emitted |
| Graviton projection | None | None | **External**: struck in a tunnel |
| Main counters | Mass (slugs over the limit), lasers, volume of fire over the recharge, the lance | Kinetics, beam power over the cap, patience (fill the pool) | Saturation by mass in the lobe, beams, blind spots, EM or AM starvation, tunnels |

### Particle shields

- **Coverage.** A `ParticleShield` emitter covers hull facing within 60° of its mount normal, up to its class area. Per ship a baked `FacingTable` maps 14 directions (the `ShieldTable` directions) to the emitters that cover them, rebuilt on component or topology change. A round or ray entering the ship's bounding ellipsoid is assigned the nearest of the 14 directions to its entry normal: O(1). A direction with no emitter is bare. Two emitters on one direction: the fuller pool takes the hit.
- **Absorb** (the station shield's rule with two more limits): a body of mass ≤ `MaxBodyKg` offering energy `E` relative to the ship loses `a = min(E, E_pool, HitCapJ)`; `E_pool −= a`; it carries on with `E − a` at `v·sqrt((E − a)/E)`. A body over the mass limit is untouched. Shrapnel rays, plasma bolts (their matter half) and particle beams (their power, as a drain while the beam is on the panel) use the same call. Missiles under the mass limit take `a` against their kill energy.
- **Pool:** settled lazily as `(E_0, t_0)` like `StationShield`; zero per tick when not hit. Recharge is bus power × 0.5; the other half is waste heat.

| Class | Area | Pool | Recharge (pool) | Hit cap | Mass limit | Idle (0.5 kW/m²) | Bus while charging | Mass | Tier, cost |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| S | 300 m² | 20 MJ | 2 MW | 0.5 MJ | 2 kg | 0.15 MW | 4 MW | 3 t | T2, 4,000 cr |
| M | 2,000 m² | 200 MJ | 15 MW | 5 MJ | 30 kg | 1 MW | 30 MW | 20 t | T2, 30,000 cr |
| L | 12,000 m² | 2 GJ | 100 MW | 50 MJ | 600 kg | 6 MW | 200 MW | 150 t | T3, 250,000 cr |

- **What that means.** An S panel stops 69 autocannon rounds from full and 7 a second for ever: one gun is a stalemate, two get through. It does nothing to a 20 kg slug. An M panel takes 5 MJ off a mass driver slug (of 40) and off a railgun slug (of 160). An L panel stops mass driver slugs outright, takes 50 MJ off a railgun slug, kills S and M missiles at the sheet, and takes 2 % off a capital shell.
- **The lance** annihilates on the sheet: it drains the panel at beam power (an L pool lasts 2.2 s at 2 km) and, while the pool lasts, bores nothing; half the beam becomes gamma at the sheet and doses the rooms behind through `RadiationDose` as usual.
- **Not an air seal and not a wall.** Panels are open sheets with gaps; they hold no pressure (air barriers and breach shields do that), and people, craft and tractor beams pass, as through an air barrier. A panel pays `½·ṁ·v²` for rubble-cloud grit it flies through (10 Hz, only inside a cloud).
- **Own fire** leaves through a blanked aperture on the shot's tick, free.
- **Damage:** emitters are exterior components. Integrity < 0.5 drops the panel. A dent of δ ≥ 0.25 m inside a panel's facing, or a hinge between emitter and facing, cuts its area by the affected share until a 60 s re-tune (`Test`).

### Energy shields

- **Coverage:** an ellipsoid round the grid's bounds plus 5 m, up to the class radius. Several projectors add pools and dump rates; the bubble is the largest one's. A projector mounts anywhere on the grid (ship-hulls.md).
- **Beams:** a beam sample crossing the bubble inward offers its power. The shield absorbs `min(P, P_absMax)` while its heat pool has room, and only its dump rate once the pool is full; the rest reaches the hull. Pulses (plasma's thermal half, wall discharges, storm hits) are absorbed up to `2 s · P_absMax` and the free pool.
- **Dose:** while powered it adds `σ_shield` in every direction to `RadiationDose` for everything inside, exactly the existing `RadiationShield` term. This doc's `EnergyShield` replaces that component name; its S class is the old part.
- **The price is heat.** Everything absorbed leaves at the dump rate as thermal signature from the shield's own radiating surface: a ship soaking a laser is the brightest thing in the sky.

| Class | Radius | Heat pool | Dump | `P_absMax` | `σ_shield` | Standby | Mass | Tier, cost |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| S | 30 m | 50 MJ | 3 MW | 12 MW | 400 kg/m² | 0.2 MW | 4 t | T2, 6,000 cr |
| M | 150 m | 600 MJ | 30 MW | 120 MW | 800 kg/m² | 2 MW | 30 t | T3, 45,000 cr |
| L | 600 m | 8 GJ | 300 MW | 1.2 GW | 1,600 kg/m² | 15 MW | 220 t | T3, 400,000 cr |

- **What that means.** An S shield shrugs off S lasers for ever (0.6 MW against a 3 MW dump), soaks an M laser for 17 s and then lets half through, and passes 48 of an L laser's 60 MW at once. An L shield cuts a flare's dose by 3,000 and annihilation gamma by 55. It does nothing at all about a slug.
- **Own beams** leave through a synchronised window, free.

### Gravity shields

- **A lobe** is a 30° cone from a gimballed emitter (slew as the tractor classes), range `R_g`. Inside it the shield accelerates foreign bodies sideways off their line, away from the ship's centre: `a = min(a_max, F_alloc / m)`, with the emitter's rated force shared over the bodies it is deflecting (≤ 8 tracked threats per emitter, by time to impact, then mass). It is a force like a tractor's: the reaction lands on the mount (rated 1.5 × the class force, the tractor emitter row) and on the ship.
- **It needs a track.** The lobe follows threats the ship's sensors hold at Q2 (section 4). Untracked bodies get the **static** mode only: a radial push at 25 % of the rated force for whatever is in the cone. Blind a ship's sensors and its gravity shield is a quarter of itself.
- **Bills** are the tractor's Reaction drive (exotic-matter.md): 0.1 g of EM content per MN·s emitted, with `AmPerEm` of antimatter, plus standby power (base + 1 MW per km of range). A raised lobe that deflects nothing burns no EM. No AM in the trap: no shield (pull-only gravitons cannot deflect, D28).

| Class | Rated force | Range `R_g` | `a_max` | Standby | Mass | Tier, cost |
| --- | --- | --- | --- | --- | --- | --- |
| S | 0.5 MN | 150 m | 3e4 m/s² | 0.45 MW | 2.5 t | T3, 15,000 cr |
| M | 5 MN | 400 m | 3e4 m/s² | 1.9 MW | 10 t | T3, 120,000 cr |
| L | 50 MN | 1 km | 3e4 m/s² | 9 MW | 60 t | T3, 1,000,000 cr |

- **What that means** (sideways miss at the ship = `½·a·(R_g/v)²`): an S lobe moves a mass driver slug 70 m and an autocannon round 230 m, but a railgun slug only 5 m: enough to save a fighter, not a freighter. An M lobe moves a railgun slug 37 m. An L lobe throws a capital shell up to 1.6 km wide for at most 0.5 g of EM, pushes a 1e6 kg thrown rock away at 50 m/s², and stops a 2,000 t ram at 25 m/s². Guided missiles are held off while they burn (3e4 m/s² against their 300) and detonate at standoff if they can.
- **Saturation is the counter.** The force is shared, so mass in the lobe starves it: 167 kg inside an S lobe, or 17 t inside an L one, pulls `a` under 3,000 m/s². A rubble cloud, a shattered rock's pieces, canister rounds, or a salvo timed together does it. So does a second threat axis: one lobe, one cone.
- **It pushes friends too.** Own missiles, shuttles, EVA crew and docking craft in the cone are deflected like anything else (characters capped at tractor-beams.md's 1 kN). A lobe blinks off for 6 ticks for outgoing rounds and must be down for launch and recovery. Bodies in the ship's own `GridVolume`, docked compounds and anything a D26 consent covers are skipped.
- **Loud.** A raised lobe is a gravimetric source (section 4) and what gravimetric seekers home on.

### Interactions

| With | Rule |
| --- | --- |
| **Station shields (D34)** | `StationShield` keeps its definition (a shell, a pool, debris and shrapnel). This doc takes up its proposed hook: **weapon rounds, shells, plasma bolts and missiles crossing the shell inward offer their energy to the same absorb call** (no mass limit: it already stops boulders). Beams and dose are not its business: a station fits energy shields for those, and gravity shields if it wants them. A ship inside a station's shell is behind the shell first, then its own shields. Pools are never shared |
| **Air barriers (D41, D61)** | Same technology as a particle panel, with no pool: a barrier draws a steady 10.6 kW/m² to hold gas (ship-atmosphere.md), while a panel idles at 0.5 kW/m² and spends megajoules of stored pool on an impact. Still not armour: rounds and beams pass a barrier. A particle panel does not hold air. A round that kills a barrier's emitter drops the sheet, as ship-atmosphere.md says |
| **Emergency breach shields (D42, D62, D63)** | ship-atmosphere.md's rule, not changed here. A weapon hole trips them like any breach. The hold time comes from the projector's battery by hole size, with no recharge while it holds, and several holes on one projector share the battery, so sustained fire runs a projector down. They stop gas, not rounds or bodies. A patch kit's patch is lost to a new brush within 2 m |
| **Tractor beams** | Gravitons pass particle and energy shields untouched. A gravity lobe and a tractor superpose as forces on the same body; neither blocks the other. A lobe can push an emitter ship away if it is inside `R_g`. Deflecting or pushing another party's ship is a tractor-class weapon use (D26, `TractorLock`), except against a body on a collision course (time to impact under 10 s), which is defence |
| **Warp** | Lobes drop at T0 and stay down until T1 + 1 (nothing projects from `Warp` placement, and the real-space leg is a projectile, not a shielded ship). Panels and bubbles ride through; in cruise nothing can hit them, and D66's venting is unaffected. A raised lobe does not block a spool (warp.md lists no shield among its spool blockers): it keeps deflecting through the spool and drops at T0. No ship shield stops an arriving warp hull: 2,000 t at 2 km/s crosses an L lobe in 0.5 s and moves 3 m |
| **Wormhole mouths** | Lobes, like tractors, do not project through a disc (rays across it are spill). Rounds that cross a mouth meet the shields on the far side normally |
| **Wormhole tunnels** | A raised gravity lobe is an `External` projection: struck on the tick it is raised and every 60 ticks, disabled 10 s (wormholes.md retaliation, which already lists "gravity weapons"). Particle and energy shields are `None` and never struck. An energy shield absorbs wall discharges and storm hits as pulses; nothing softens a ram |
| **Gravity dampers, AG pulses** | A gravity shield is not a damper: pulses kick and shear through it (exotic-matter.md open question 19 stays open). A station's damper does not affect lobes |
| **Each other** | Independent. A round meets, in order: a gravity lobe (deflection), a station shell if any, a particle panel, the hull. A beam meets the energy bubble, then the hull |
| **Sensors** | Raised shields are signatures: panels glow (optical and signature readers), bubbles radiate what they absorb (thermal), lobes are gravimetric. Shields-up is never stealthy |
| **Wiring (D71)** | Power, and Data for raise, drop, facing priority and the lobe's track feed: these are data-in ports. Pool and status are a data-out port, and a console sees them only over a return cable (data is one-way). The same holds for turrets and the fire-control computer. A cut command line leaves a shield in its last state with no tracking |

### Region shields: towns, cities, planets (D58)

Factions-and-law.md owns these: `RegionShield` cores of class Town (10 TJ, 5 GW), City (100 TJ, 20 GW) and Planetary (5 PJ, 200 GW), each a lazy pool behind a shell that lets slow things through and makes fast things pay; the pressure-wave rule for `WarpBlast`; raised shells as warp exclusions; licences and who may build one; the `WarpAttack` offence. They are the station shield's big sibling, not a fourth kind of ship shield. This doc adds only what that doc asks of it.

- **Weapons through the shell.** A real round, shell, bolt or missile crossing a raised shell inward offers `½·m·(v_n² − v_pass²)` to the region pool through its absorb call (every weapon here is far above `PassSpeedMs`), and leaks as that doc says. **Beams** crossing inward offer their power for each 0.1 s sample; what the pool cannot take carries on to the ground or the grid behind. The lance and particle beams count as beams here.
- **One pool against everything fast.** A region shell has no mass limit, no hit cap, no facings and no heat pool: it does the particle, energy and station-shell jobs together, which is why it is measured in terajoules. A Town pool refills at 5 GW: beating that takes about 45 capital mass drivers (112 MW each) or 80 L lasers firing together. Sieges of shielded towns are fleet work, as D34 intends at city scale.
- **Still not stopped:** dose (buildings and ships under a dome rely on mass and their own energy shields), AG pulses (dampers), tractor beams, and anything slow. A torpedo slowed to `PassSpeedMs` by the shell still arrives and still detonates, and a blast inside the dome is not helped: point defence, not the dome, answers torpedoes.
- **With ship shields:** a ship under a raised dome is behind the dome first and its own shields second. Pools are never shared. The shell acts on inward crossings only, so fire going out from under it is untouched.
- **Cost:** shell tests ride the round sweep and the beam ray (a sphere test per raised shell in the round's SOI cell, ~10 ns), and only in bubbles and along paths that touch one.

## 4. Sensors

No sensor doc existed. Rings-and-asteroids.md defines how ring particles and dust cut each sensor kind (`RingObscuration`), wormholes.md defines the signature contest and a `Sensor {Quality, Range}` struct, exotic-matter.md lists signature ranges, and ai-ships-and-crew.md keeps a per-bubble contact list "until sensors exist". This section is what they were waiting for.

### Kinds

`SensorKind` (rings-and-asteroids.md's enum, extended): `Optical, Thermal, Radar, Signature, Gravimetric, CrewScan`.

| Kind | Reads | Mode | Falls with | Ring particles / dust (`κ_part` / `κ_dust`) | Blocked by |
| --- | --- | --- | --- | --- | --- |
| **Optical** | Reflected sunlight, lights, plumes, shield glow | Passive | `R²` | 1 / 1 | Bodies, hulls, shadow (no reflected light in eclipse), sun glare |
| **Thermal** | Radiated waste heat | Passive | `R²` | 1 / 0.5 | Bodies, hulls; warm backgrounds |
| **Radar** | Its own echo off the hull | Active | `R⁴` | 0.5 / 0.02 | Bodies, hulls; ground and ring clutter; jamming |
| **Signature** | Emissions: containment hold fields, radar and jammer emissions, transponders, shield emitters, faction signatures of wormholes and catchers | Passive (active only to copy, wormholes.md) | `R²` | 0 / 0 | Bodies |
| **Gravimetric** | Mass (tidal gradient) and graviton devices: tractors, dampeners, gravity shields, anchors, warp spools and arrivals, wormhole mouths | Passive | `R³` for mass, `R²` for devices | 0 / 0 | **Nothing** |
| **Crew scan** (D48) | People inside a hull | Active, dwell | `R²` and the hull in the way | 1 / 0.5 | Areal density, energy shields |

### Signatures

Each entity carries one number per kind, updated on events and in the 10 Hz ledger, never per observer.

- **Optical:** `A_proj · albedo · sun(r)`, plus lights, plumes and shield glow as equivalent lit area (data). Albedo is a material value (hull 0.3, dark coating 0.03). In a body's shadow the first term is 0.
- **Thermal:** what the **thermal ledger** radiates. The ledger is three numbers per ship in the existing 10 Hz `ShipLedger`: waste heat in (reactor `(1 − η)`, weapon and shield cooling, the tractor's and mining laser's reported waste heat, life support), heat stored (`HeatJ`, capacity = hull mass × 1e4 J/kg plus `HeatSink` components: S 0.5 GJ, M 5 GJ), and heat out through `Radiator` components (exposed panels, `P_rad` by area; open or closed by a Data input). Signature = radiated power + 10 % of thrust plume power + 1 % of stored heat as leak. Radiators closed: the signature falls to the leak and `HeatJ` climbs; at capacity the radiators open by themselves or the hottest component loses 1 % Integrity a second.
- **Radar cross-section:** `A_proj · reflectivity` (hull 1.0, absorbing laminate 0.05).
- **Signature emissions:** the ranges the owning docs give are the reference: traps, cells and hot chambers 2 km; an armed AG charge and a runaway 50 km; a `CollapseInducer` 50,000 km; a radar or jammer at 3 × and 10 × its own range; a raised particle panel 50 km; a transponder 200 km (warp.md's `BeaconRange`).
- **Gravimetric:** mass, and the force each graviton device is emitting (a gravity shield on standby counts 10 % of its rating; a warp spool counts as a fixed loud source; a mouth is wormholes.md's `2,000 km · r̂`).

### Detection: one formula, four quality levels

For a sensor of reference range `R_ref` against a target at range `R`:

```
S = (R_ref / R)^n · (sig / sig_ref) · T^(n/2) · dwell / clutter
n = 2 passive,  4 radar,  3 gravimetric mass
T = RingObscuration.Transmittance(a, b, kind)  (rings and dust clouds; 1 in clear space; atmospheres add their own extinction)
dwell   = min(8, 1 + t_stare / 5 s)      staring at one bearing or one contact (a scan action); 1 in search
clutter = product of the environment terms below (≥ 1)
```

Reference signatures (`sig_ref`): 30 m² of sunlit hull at 1 AU; 1 MW radiated; 100 m² of radar cross-section; 1 MN of graviton emission or 1e9 kg of mass. This is rings-and-asteroids.md's range rule (`(R_nom/R)²·T ≥ 1` passive, `(R_nom/R)⁴·T² ≥ 1` active) with the target's size made explicit, so its ring tables hold.

| `S` | Quality | What the observer knows | Enough for |
| --- | --- | --- | --- |
| ≥ 1 | **Q1 blip** | Bearing, rough range (± 20 %), kind of return | An alarm; a blind missile shot |
| ≥ 4 | **Q2 track** | Position and velocity with an error, size class | A fire-control lock, point defence, gravity-shield tracking, missile launch |
| ≥ 16 | **Q3 identified** | Hull plan (so the real silhouette), claimed owner and transponder, shields up or down, weapons run out | Being drawn as itself beyond eyesight; hails; the signature contest |
| ≥ 64 | **Q4 resolved** | Components and their state, heat map, damage, hardpoints | Subsystem targeting; salvage survey |

So Q2 is at half the detection range for a passive sensor and 0.7 of it for radar: thermal sensors find things early, radar is what fights. A contact's quality is the best over the observer group's sensors, with 3 s of hysteresis each way, and it decays to a **ghost** (last known state, dead-reckoned, marked stale) for 60 s after `S` drops under 1.

- **Clutter** (data, `data/sensors/detection.json`): optical within 5° of the sun × 16; thermal against a planet's lit disc × 8, in atmosphere × 4; radar on a target under 500 m above terrain × 8, inside a ring slab × `(1 + bodies per column / 8)`, inside a jammer's cone × the jammer's factor; gravimetric mass inside a strong tidal field × `(1 + g_tidal / 1e-3 m/s²)`.
- **Line of sight** (all kinds but gravimetric): planet spheres at terrain-maximum radius (ray-sphere, ~10 ns a body), realised asteroids ≥ 32 m and grids ≥ 100 m by an inner sphere. Rail asteroids are not tested one by one; they are in `T`.
- **Sharing:** contacts belong to an **observer group** (a ship; ships of one party within 200 km of each other share by data link; a station and its docked ships). One pooled `ContactTable`.
- **Evidence for the law.** `SensorSystem.HasTrack(actor, tick)` is true when a lawful third party's group held the actor at Q2 or better on that tick: factions-and-law.md's `Sensor` evidence flag, replacing its interim 20 km rule. A lawful grid that holds a hull at Q3 with no transponder inside Secure space is what writes that doc's `TransponderOff`.

### Detection ranges

Q1 ranges in clear space, sunlit at 1 AU, for the S / M / L sensor of each kind (table below). Halve for Q2 (radar: × 0.7), quarter for Q3 (radar: halve).

| Target | Optical (50 / 200 / 1,000 km) | Thermal (100 / 500 / 5,000 km) | Radar (30 / 150 / 600 km) |
| --- | --- | --- | --- |
| 15 m fighter, cold (5 kW) | 39 / 155 / 770 km | 7 / 35 / 350 km | 26 / 130 / 530 km |
| 15 m fighter, reactor on (1.5 MW) | same | 120 / 610 / 6,100 km | same |
| 15 m fighter, burning (20 MW of plume heat) | brighter | 450 / 2,200 / 22,000 km | same |
| 15 m fighter, dark coating and absorber | 12 / 49 / 245 km | as its heat | 12 / 62 / 250 km |
| 100 m freighter, cold (50 kW) | 275 / 1,100 / 5,500 km | 22 / 110 / 1,100 km | 70 / 350 / 1,400 km |
| 100 m freighter, reactor on (50 MW) | same | 710 / 3,500 / 35,000 km | same |
| 100 m freighter, main burn (3 GW) | brighter | 5,500 / 27,000 / 270,000 km | same |
| 500 m capital on a 2 GW core (3 GW of waste heat) | 1,100 / 4,500 / 22,000 km | 5,500 / 27,000 / 270,000 km | 140 / 710 / 2,800 km |
| Missile S, coasting | 3.5 / 14 / 70 km | 3 / 16 / 160 km | 5 / 27 / 107 km |
| Missile S, boosting | brighter | 150 / 740 / 7,400 km | same |

| Gravimetric target | M (100 km at 1e9 kg; 500 km at 1 MN) | L (1,000 km; 5,000 km) |
| --- | --- | --- |
| 30 t fighter, nothing running | 3 km | 31 km |
| 2,000 t ship, nothing running | 13 km | 126 km |
| 1e9 kg asteroid | 100 km | 1,000 km |
| S dampener at full rating (1.5 MN) | 610 km | 6,100 km |
| M gravity shield on standby (0.5 MN) | 350 km | 3,500 km |
| L tractor at full pull (20 MN) | 2,200 km | 22,000 km |
| Warp spool, or a warp arrival from its commit | 20,000 km | 200,000 km |
| Open wormhole mouth | wormholes.md: `2,000 km · r̂` | × 2 |

In the densest choked ring band these fall as rings-and-asteroids.md tabulates (a 50 km optical sensor sees ~4 km, a radar ~11.5 km); gravimetric and signature sensors do not fall at all.

### Stealth

There is no cloak. Hiding is managing four numbers: run cold (reactor idle, radiators shut, on batteries: minutes to hours, by heat capacity), coast (a plume is seen across a planetary system), stay dark (no radar, no transponder, shields down, dampener and plating off against gravimeters), and use the terrain: a body's shadow kills reflected light, dust kills optics, sun glare and a planet's lit disc bury you, a rock's bulk hides you from everything but a gravimeter, and a gravimeter only reads your mass up close. Firing ends it: a shot adds its muzzle heat for 2 s and a flash. Running with a transponder off in Secure space is factions-and-law.md's `TransponderOff` offence, and a ship with no transponder is drawn on other HUDs as unknown, never as friendly.

### What the server sends (no free intel)

A client's world is its own bubble, what its character could see with its eyes, and its group's contacts. Nothing else leaves the server.

| What | Sent when | As |
| --- | --- | --- |
| Entities in the client's own bubble, or within 2 km | Always (they can collide with it) | Ordinary snapshots; no HUD marker without a contact or line of sight |
| **Eyeball rule:** an entity whose angular size is ≥ 0.5 mrad (a 15 m ship at 30 km, a 100 m ship at 200 km), lit or self-luminous, with optical transmittance ≥ 0.05 and line of sight | While true | Snapshots at frames' interest rate, drawn at its LOD; no marker, no data |
| Q1 contact | 1 Hz | `ContactBlip` 8 B: alias id, bearing and range quantised to the error, kind. Never an `EntityId` |
| Q2 contact | 2-10 Hz by range | `ContactTrack` 14 B: alias, position and velocity with the track error baked in (a slow seeded bias, not white noise, so averaging does not beat it), size class |
| Q3 contact | on reaching it, then frames' interest rate | The entity itself: id, plan hash, claimed owner; snapshots or rails elements |
| Q4 contact | on change | `ContactDetail`: component states, shield pools, heat, damage summary |
| Traffic legs (ai-ships-and-crew.md) | Only for hulls whose transponder is on, or that the group holds at Q2+ | As that doc defines; a dark hull is not on anyone's map |
| Weapons fire | `FireFx` for shooters the client sees or holds at Q2+; rounds passing within 2 km of it regardless | Fire events, never per-round state |
| Warp events | Section "Beacons" | |
| Being scanned or locked | To the target's crew, if it has a signature reader (radar locks, active scans) | `ScanAlert`, `LockAlert` 8 B |

Frames-and-zones.md's interest table keeps its rates; its rows beyond "own bubble" become "if seen or held". Client-side sensor displays run the same formula on what they were sent, for the range rings and predicted-visibility HUD only.

### Components and power

All are exterior components with a baked field-of-view mask (as turret arcs) and the shared damage rule; a dent misaligns them. Power class 2.

| Component | S / M / L | Power | Notes |
| --- | --- | --- | --- |
| Optical telescope | `R_ref` 50 / 200 / 1,000 km | 5 kW / 50 kW / 0.5 MW | Dazzled by a laser on it |
| Thermal array | 100 / 500 / 5,000 km | 10 kW / 0.1 MW / 1 MW | The mining `ThermalImager` grades are S / M / L of this part at short range |
| Radar | 30 / 150 / 600 km | 0.2 / 2 / 20 MW while emitting | Emitting is a signature at 3 × range. Search or track-only (a narrow beam: 0.3 × the emission range) |
| Signature reader | Quality 0.4 / 0.7 / 1.0, range × 0.5 / 1 / 2 | 20 kW / 0.2 MW / 1 MW | Is wormholes.md's `Sensor {Quality, Range}`; runs its contest and copy scans |
| Gravimeter | n/a / 100 / 1,000 km (mass), 500 / 5,000 km (devices) | 0.5 / 5 MW | T3: a graviton lens (exotic-matter.md). Listens only: `gravitonProjection: None` |
| Crew scanner | 500 m / 2 km / n/a | 0.1 / 1 MW | D48, below |
| Fire-control computer | 4 / 16 / 64 simultaneous locks | 10 kW / 50 kW / 0.2 MW | Interior. Turrets, point defence and gravity lobes take tracks from it over Data lines (D71: one-way, split to every input) |
| Jammer, decoy launcher | below | | |

### Beacons and transponders

- **Transponder:** a broadcast of the ship's claimed identity, readable by anyone within 200 km with no sensor part (radio, as warp.md's beacon). It makes the ship a Q3-by-claim contact: the identity is what the ship says. Verifying it is wormholes.md's signature contest with a reader. Civilian hulls ship with it on; turning it off is a switch on military hulls and a `Logged` wire on civilian ones, exactly like the warp beacon.
- **Warp arrival beacon (D18):** unchanged, warp.md owns it. It needs no sensor.
- **`WarpDepart`** (direction only) goes to observers that hold the departing ship at Q1+ or have a gravimeter in range of its spool, instead of "interest of the departure point".
- **Silent arrivals:** warp.md gives third parties the corridor at commit, 3 s before the drop. That is delivered to clients within its existing interest, and, as a bearing-only Q1, to any gravimeter within arrival range. No more warning than warp.md allows.
- **Hazard beacons, distress, `ScanAlert`, zone defence status** stay public broadcasts as their docs say.

### Scan actions

A scan is a dwell on one contact: the sensor stares (`dwell` up to × 8 after 35 s: × 2.8 range for a passive sensor, × 1.7 for radar, or one quality level and most of another on a contact already held), and typed scans read more:

| Scan | Sensor | Dwell | Result | Target is told |
| --- | --- | --- | --- | --- |
| Identify | Any | 5-35 s | Quality up to Q3 or Q4 sooner | Radar only |
| Structure | Radar or optical at Q4 | 10 s | Component list with positions, for subsystem targeting | Radar only |
| Containment | Signature reader in range | 5 s | Traps, AM mass class, hot chambers, armed charges (station screening, exotic-matter.md) | No |
| Signature copy / verify | Signature reader | wormholes.md's evidence integral | Verdict or `SignatureCopy` | Copy: yes |
| Composition | `CompositionScanner` | rings-and-asteroids.md | Unchanged | No |
| **Crew (D48)** | Crew scanner | 5 s: head count per grid. 15 s: count per room. 40 s: live positions at 1 Hz while held | From the crew manifest and schedule (ai-ships-and-crew.md), at any simulation level, so a scan never promotes anyone | Yes |

Crew-scan range is `R_cs · exp(−σ / 1,500 kg/m²)` with `σ` the hull's `ShieldTable` density toward the scanner plus any energy shield's `σ_shield`: 500 m becomes 300 m through one standard wall, 180 m through two, and about 60 m behind an armour belt, a standard wall and an S energy shield. Boarders scan from the hull.

### Electronic warfare, kept minimal

Three tools, no minigame:

- **Jammer:** raises radar clutter × 16 for every radar inside its 30° cone out to its range (S 50 km, M 300 km), and is itself a signature at 10 × that. It hides range and count, never bearing. **It also jams comms:** a grid inside the cone cannot send distress, hails or reports to anyone farther from it than a tenth of the jammer's distance, which is what denies factions-and-law.md's `Victim` evidence (powered comms for `ReportDelayS` after the event). Jamming another party's ship is a weapon use on it (a `WeaponFire` record with no energy; factions-and-law.md's Weapon use row).
- **Decoys:** flares (a thermal source of 5 MW for 6 s) and chaff (200 m² of radar return for 20 s). They are real contacts to every sensor and seeker; a seeker picks among returns in its cone by strength. Magazines of 16.
- **Spoofed transponders:** wormholes.md's signature copies broadcast as a claim; the contest decides who is fooled.

## 5. Combat at range and the 4 km bubble

### The problem and the rule

A bubble is 2 km soft and 4 km hard (float precision), and weapons reach 5 to 460 km. Bubbles cannot be merged across that, and they do not need to be:

- **Weapons never merge bubbles.** Rounds, bolts, missiles and beam rays live in the SOI's inertial frame in double (section 1) and are tested against whatever bubble or rails hull their path meets. Two player ships 80 km apart, each in its own bubble, exchange railgun fire with no shared Jolt scene.
- **Bubbles merge only for physical reasons,** by frames-and-zones.md's existing rule: a predicted gap under 500 m within 2 s (R7, closest approach from current states). Ships that close to ram, board, tractor or dock merge then, as today. Rounds in flight are in neither scene and do not care.
- **A hit needs a hull, not a body.** The hit test is the occupancy DDA, so a target on rails can be hit as data.

### Rails combatants (D50: data unless they fight)

Ai-ships-and-crew.md's levels gain one, between L1 and L2. It leaves "resolve the hit on rails" to this doc; this is it.

| Level | What it is | Placement | Cost |
| --- | --- | --- | --- |
| **L1c rails combatant** | An L1 entity plus a `CombatHull`: its `ShipHull` with bricks **baked lazily along hit rays** (server bake, no mesh), component and wiring state, weapons, shields, power and thermal ledgers at 10 Hz, and an attitude quaternion slewed at 10 Hz toward the AI's aim or aspect goal within the plan's turn rates. **No Jolt body, no bubble, no grid scene** | `Rails` (thrusting or coasting) | ~3 µs per ship per 10 Hz slot, plus its hits. 0.4 ms per brick baked, inside ship-hulls.md's 16 a tick |

- **It can be hit:** the ray is taken into its rails pose at the hit tick (closed form) and resolved as on any hull. Holes, dents, bends, component damage, air loss (ship-atmosphere.md is event-driven and needs no body) and crew casualties (the manifest rule) are real and are what the ship is when it is next simulated. A hit that needs unbaked bricks waits for them (≤ 6 ticks) and is applied in order.
- **It can fire:** muzzles are plan positions under the rails pose; rounds are born in frame space like any others. This lifts ai-ships-and-crew.md's "an L1 ship cannot fire" for L1c only.
- **Impulses** are rails element updates (`Δv = J / M`, the path `PulseKick` already uses). A split makes new rails entities from the pieces.
- **It cannot be rammed, tractored or boarded.** Those need a body, and frames' ordinary rule gives it one when anything with a player anchor comes within 2 km or a 2 s predicted approach: L1c becomes L2 by adding the Jolt body to state that already exists.

| Promotion | When |
| --- | --- |
| L1 → L1c (target) | A player's fire-control lock starts on it, at any range; or any real round, missile or beam ray whose path meets its bounding sphere within the next 5 s (rounds test L1 hulls in their SOI through a 64 km hash at 10 Hz, ~30 ns a round) |
| L1 → L1c (shooter) | An AI's behaviour decides to fire on a player's ship or on anything in a player-anchored bubble from beyond 4 km (it needs a Q2 track and a weapon in reach) |
| L2 → L1c | It leaves a bubble's hard radius while `CombatUntilTick` runs or within 10 s of a hit or lock (it used to fall to plain L1) |
| L0 → L1 | New trigger for ai-ships-and-crew.md: a player group holds the hull at Q2 or better. A lock needs a contact, so nothing is ever locked at L0 |
| L1c → L2 | frames' rule (2 km, or 2 s predicted approach, of a physics anchor) |
| L1c → L1 | No lock, round, missile or beam involving it for 30 s and `CombatUntilTick` passed. Its damage coalesces into `DamagePool` (≤ 64 brushes, ≤ 32 dents, hinges) or pins it as an entity (that doc's pin rule) |

- **Caps:** 32 L1c hulls per dedicated server (8 on a listen server) and 16,384 lazily baked bricks among them (~32 MB). At a cap the quietest L1c that can demote does; if none can, a player's shot at a further hull is resolved by the salvo rule below against that hull's ratings (damage still lands, as templates), and AI shooters are refused a firing solution. A parked, uncrewed player ship shot from range becomes L1c like any other hull.
- **Players are always real.** A crewed player ship has an anchor and a bubble; only the far end of a long shot is ever data.

### Fights nobody is in (D54)

Factions-and-law.md decides the wars: once per played hour its strategic step counts each side's strength from the real traffic and station rows and moves a front's pressure, and far from players losses are ai-ships-and-crew.md's per-route loss draw. Neither simulates a battle, and this doc does not change that. It supplies two things: the **values** those systems count, and the **arithmetic** for fights a player can see but is not in. Both come from the same data as the simulated fight, so all three give the same sort of answer.

| Fidelity | When | How it is resolved |
| --- | --- | --- |
| **Statistical** | No participant is within 200 km of a player or held as a contact by one | The strategic step and the loss draw, in their own docs. From here: each hull row's **military value** and each station's **defence value**; and, when a loss draw fires, one salvo exchange between the drawn hull and a raider of the route's class decides `Raided`, `Damaged` or `Destroyed`, so an escorted convoy is not lost like a lone hauler. Nothing per tick, no entities (L0 rows) |
| **Witnessed** | A player holds a participant as a contact, or is within 200 km (the hulls are L1), but no fire starts, ends or passes within 4 km of a player anchor | Salvo rounds as scheduled events. Clients that hold both ends get `BattleFx` (12 B per exchange, 1 Hz) and draw the tracers and flashes between their contacts. Damage lands as templates. This is how L1 hulls "fight for real" when a player is in promotion range but not in reach |
| **Real** | The shooter or the target is in a player-anchored bubble, or is L1c because of a player, or the shot's path passes within 4 km of a player anchor (tested once at fire) | Simulated rounds, beams and hits |

- **Combat rating** (`CombatRating.FromPlan`, computed once per plan hash at certification):

```
Fire_k   = Σ weapons  E_hit · rate · duty(heat, power, ammo)        J/s by kind (Kinetic, Thermal, Particle, Plasma, Annihilation), with reach and round speed
Tough    = 0.01 · Σ materials  e_cav · V_solid                        J to mission-kill: 1 % of the energy to destroy the hull
Pass_k   = share of a kind-k hit that gets past the plan's shields (pool, recharge, caps, mass limit) and its median outer layup (the hit law)
Agility  = lateral acceleration and target radius;   Sense = best R_ref per kind
MilitaryValue = sqrt(Σ_k Fire_k · Tough) / (the same for the reference patrol corvette) · 100
```

  `MilitaryValue` is what the strategic step sums as a hull's strength (firepower times staying power under a square root, which is what Lanchester's square law says a unit is worth). A station's **defence value** is the same formula over its turrets, with its shield pools added to `Tough`. A damaged hull's value scales with its `hp` (below).
- **A salvo round** is 30 s of game time, one heap event per battle:

```
p_hit      = clamp(r_t / (r_t + σ_aim·R + ½·a_lat·t_f²·(1 − lead)), 0.02, 0.95)       t_f = R / v_round, 0 for beams
dmg(A → B) = Σ_k Fire_k(A) · 30 s · p_hit · Pass_k(B) · noise        noise in [0.75, 1.25] from hash(world seed, battle, round, A, B)
B.hp      −= dmg / Tough(B)
```

  The faster side picks the range band (the one where its ratio of dealt to taken is best). Targets are chosen by the behaviour (focus the weakest, or spread).
- **The pilot matters (D53).** `σ_aim`, `lead` and the jink that feeds `a_lat` come from the pilot's skill tier; the range band, the flee threshold, focus or spread, and whether it will close to ram or board come from the pilot's style. These are ai-ships-and-crew.md's `PilotProfile` numbers, the same ones `ShipAi` flies and shoots with at L2 and L1c, so an Ace in a worse ship beats a Green in a better one in both models. Player gunners and pilots get no modifier: their skill is their own.
- **Outcomes** at `hp` thresholds: under 0.5 `Damaged` (damage templates scaled to the loss go into `DamagePool`: brushes, dents and, under 0.3, a hinge; `SystemsQ` bits drawn; crew cleared by ai-ships-and-crew.md's loss table); under the style's flee threshold the hull breaks off if it is the faster; at 0.2 `Disabled` (an `Adrift` stray: salvage, rescue or capture later); at 0 `Destroyed`. Ammo, fuel and antimatter are debited from the hull row's fractions. Nothing heals afterwards except through dock repair. Every loss is a loss in the rows, so the strategic step sees it at its next count.
- **Materialising.** When a player arrives (200 km, or a contact), hulls are promoted with the losses already booked expressed as damage, and any fight in progress carries on as Witnessed, then Real for fire that concerns the player. No reset, no reroll.
- **Scale check:** `Tough` is 40 MJ for a 30 t fighter (about 140 autocannon hits: 35 s under two guns at 20 % hits), 2.7 GJ for a 2,000 t freighter (17 railgun hits) and about 0.3 TJ for a 100,000 t armoured capital (10-15 minutes under another capital's guns).
- **Caps:** 16 concurrent salvo battles per server, 32 hulls a side, 40 rounds (20 min), then a draw: both sides withdraw.

## 6. Multiplayer and performance

### Authority and tick order

Server-authoritative throughout: turret pose, fire, rounds, hits, damage records, shields, signatures, contacts. Clients predict only their own muzzle effects and recoil (through the C12 re-step, as a force). Additions to frames-and-zones.md's tick:

- **Step 3:** apply last tick's queued hits in hit order (shield absorb, march, holes, accumulators, dents, component Integrity, casualties), bends from last tick's structural results, warhead and breach results.
- **Step 4:** fire commands; `ShipAi` fire control (10 Hz); turret slew; scan actions.
- **Step 5:** recoil; gravity-lobe forces on bodies; the tractor structural pass with yield; queued impulsive evaluations (jobs).
- **Step 8c** (after poses are final): integrate and sweep rounds (jobs of 512), beam samples and missile guidance (10 Hz slots), queue hits.
- **Step 9:** the sensor slice (staggered), contact messages with the 30 Hz snapshot.
- **10 Hz ledger:** weapon capacitors and heat, shield power grants (pools are lazy), the thermal ledger, signatures.

### Per-tick costs and caps

| Item | Estimate | Cap |
| --- | --- | --- |
| Simulated rounds | ~80 ns per round per tick (integrate, bubble hash, sweep grid), as jobs: 0.33 ms of worker time at the cap | 4096 per server, 1024 per bubble, 256 per firing grid; past a cap the oldest low-energy rounds expire |
| Rail sweep (rings-and-asteroids.md) | 2-4 µs per round, or ~0.5 µs amortised for the 10 Hz look-ahead: 1-2 ms of worker time at the budget | 512 a tick, only rounds inside a ring slab or field |
| `SweepGrid` | ~5 µs per bubble per tick, only bubbles with rounds or beams near | 256 entities per bubble (existing) |
| Beams | ~1-2 µs per sample at 10 Hz: ~85 µs a tick at the cap | 256 active per server, 16 per grid |
| Missiles | a round, plus ~0.2 µs guidance at 10 Hz | 256 per server, 32 per owner group |
| Hull DDA | ~1 µs per ray per hull | |
| Hit resolution | 2-4 µs per hit; + ≤ 6 µs on a crewed grid (capsules) | 256 hits a tick per server, 32 per ship, one job per ship; overflow is carried to the next tick in order, never dropped |
| `DamageAccum` | ~30 ns per sub-brush hit | 256 cells per ship |
| Hole brushes | the bake: 0.4 ms per brick on the server | 8 brushes per ship per second; ship-hulls.md's 16 brick bakes a tick server-wide |
| Dents | ~1 µs per record, ≤ 10 µs of effects, ~20 µs of worker time per rebuilt collision section | 4 per ship per second, 1024 per ship, 16 per brick |
| Deformation | yield: +~3 ns per cut in the existing pass; impulsive pass 33 µs per 1,000 nodes plus the lazy graph build | 8 impulsive evaluations a tick; 7 hinges per ship |
| Shields | 0 while not hit (lazy pools); ~0.3 µs per absorb; lobes ~10 ns per (round, lobe) in a bubble, allocation ~1 µs per emitter at 10 Hz | per grid: 6 particle emitters, 2 energy projectors, 2 lobes (4 on stations); 8 lobes per bubble |
| Sensors | reject ~10 ns per pair, full evaluation ~0.5 µs (line of sight, `RingObscuration`, clutter). Held contacts at 2 Hz, search at 0.5 Hz, staggered, targets from the observer's own SOI: ~50-100 µs a tick at the caps, as jobs | 256 observer groups, 64 contacts shown per group, pool of 16,384 contacts |
| L0 hulls against M and L sensors | route bounding sphere, then `Locate` (~100 ns), at 0.1 Hz: ≤ 45 µs a tick worst case | |
| Rails combatants | ~3 µs per hull per 10 Hz slot | 32 hulls, 16,384 bricks |
| Salvo battles | 0 per tick; ~2 µs per pair per 30 s round | 16 battles |
| Idle | **0**: no rounds, no beams, lazy pools, signatures on events | |

- **A 16-ship fight in open space** (64 turrets, ~1,500 rounds in flight, 20 beams, ~30 hits a tick): about 0.3 ms of main-thread-equivalent work a tick and up to ~7 ms of worker time, nearly all of it brick bakes, which the 16-a-tick budget bounds. In a ring add up to 2 ms of worker time for rail sweeps. Measure before trusting any of it.
- **Listen servers** (8 players) take a quarter of every per-server cap.

### Bandwidth

Rounds are never replicated. Clients rebuild tracers from fire events and the turret's aim, by the same closed form.

| Message | When | Size | To |
| --- | --- | --- | --- |
| `FireCommand` (client → server) | on change | 12 B | |
| `TurretAim {grid, turret, az, el}` | 10 Hz while slewing or firing | 5 B | interest of a seen shooter |
| `FireFx {weapon, tick, seed, count}` | per burst start and stop; per shot for slow guns | 12 B | seen shooters; anyone a round passes within 2 km of |
| `BeamFx {weapon, end point, on}` | on change, end point at 5 Hz | 14 B | as above |
| Missile | a body-style snapshot at 10 Hz while held as a contact | 20 B | holders |
| `HullHitFx` | ≤ 20 a second per ship, unreliable | 10 B | interest |
| Damage brush / `HullDentMsg` / `HullBendMsg` | reliable, sequenced | 34 B / 28 B / 96 B | interest |
| `ShieldState {emitter, kind, pool, rate, tick}` / `ShieldHit` | on change (clients extrapolate the lazy form) / ≤ 10 Hz per emitter | 12 B / 12 B | Q3 holders / interest |
| `ContactBlip` / `ContactTrack` / `ContactDetail` | 1 Hz / 2-10 Hz / on change | 8 B / 14 B / variable | the observer group |
| `ScanAlert`, `LockAlert`, `BattleFx` | events / 1 Hz | 8 B / 12 B | target crew / witnesses |
| `IncidentRecord` (`WeaponFire`) | summed per (actor, victim) per second, as today | 64 B | as factions-and-law.md routes it |

A client in the middle of the 16-ship fight gets ≤ ~15 KB/s of combat traffic on top of frames' ~30 KB/s of snapshots. The combat channel is capped at 12 KB/s per client: unreliable effects are dropped by distance first; brushes, dents and bends are rate-limited at the source and sent for the client's own ship first.

## C# structures

```csharp
namespace BrutalGame.Combat;      // Sim assembly: no Vulkan, no Jolt types in the data

public enum HitKind : byte { Kinetic, Blunt, Thermal, Particle, Plasma, Annihilation }
public enum WeaponFamily : byte { Autocannon, MassDriver, Railgun, Laser, ParticleBeam, Plasma, Lance, Launcher, Tool }

public struct WeaponDef            // data/components/weapons; one per function core
{
    public WeaponFamily Family; public HitKind Kind; public byte MountClass; public bool PointDefence;
    public float RoundKg, MuzzleMps, RoundAreaM2, BeamW, SpotM, DivergenceRad, RangeEffM, RangeMaxM, LifeS, DecayS;
    public float ShotJ, CapacitorJ, RatePerS, HeatPerShotJ, HeatCapJ, CoolW, SlewRadS, DispersionRad, WearPerShot;
    public DefId Ammo; public ushort ReadyRounds; public WeaponAsteroidDef Asteroid;      // rings-and-asteroids.md's block
}

public struct WeaponState          // SoA in WeaponSystem, one per weapon component
{
    public EntityId Grid; public uint ComponentId; public float4 Aim, AimGoal;   // quaternions, mount-local
    public float CapacitorJ, HeatJ; public ushort Ready; public int NextShotTick; public int Lock; public byte Flags;
}

public sealed class RoundPool      // SoA, capacity 4096 + 256 missiles, preallocated; index reuse through a FreeList
{
    public double3[] Pos; public float3[] Vel;            // SOI inertial frame
    public float[] EnergyJ, MassKg, AreaM2; public int[] BornTick, DieTick; public ushort[] Weapon; public FrameId[] Soi;
    public OwnerId[] Shooter; public uint[] Seq; public byte[] Flags;   // Plasma, Missile, PdFused, RailLookahead
    public int[] Guidance;                                // -1, or a row of MissileGuidance
}
public struct MissileGuidance { public int Contact; public SensorKind Seeker; public float SeekerRef, ThrustMps2, BurnLeftS, KillJ; public bool DataLink; public DefId Warhead; }

public struct HullHit              // queued in step 8c, applied in step 3; 64 B
{
    public EntityId Target; public float3 Origin, Dir;    // ship-local, root group
    public float EnergyJ, MomentumNs, AreaM2, SpeedMps; public HitKind Kind;
    public OwnerId Shooter; public ushort Weapon; public int Tick; public uint Seq;
}
public struct HitLayer { public float Enter, Exit; public byte Material; public uint ComponentId; }   // from the DDA; ≤ 32 per ray, stackalloc

public static class HitLaw
{
    public static int March(in CombatHull hull, in HullHit hit, Span<HitLayer> layers);                 // occupancy DDA, ~1 µs
    public static void Resolve(ref CombatHull hull, in HullHit hit, ReadOnlySpan<HitLayer> layers, ref HitResults results);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HullDent             // 28 B, material space, wire and save
{
    public uint Id; public int PosX, PosY, PosZ; public ushort DirOct; public ushort Radius; public short Depth;
    public byte Kind, Group; public ushort HalfLength; public ushort LenDirOct;
}
public struct HullHinge            // 36 B
{
    public uint Id; public int CutId; public int PlaneX, PlaneY, PlaneZ; public ushort NormalOct, AxisOct;
    public short AngleMilliDeg; public short ShearMm; public byte GroupA, GroupB; public ushort Flags; public int Tick;
}
public struct DeformGroup { public uint Rotation; public int OffX, OffY, OffZ; public MassMoments Mass; }   // ≤ 8 per ship
public struct DamageAccumCell { public int Key; public float RemovedM3; public float3 Centroid, DentVec; public float LeakM2; public int LastTick; public short RoomA, RoomB; }

public sealed class HullDeformation    // per ShipHull; null until the first dent
{
    public NativeList<HullDent> Dents; public NativeList<HullHinge> Hinges; public NativeList<DeformGroup> Groups;
    public NativeList<byte> BrickGroup;                   // per brick
    public float3 ToShip(float3 material, int brick);     // rendering twin lives in hull.vert
    public float3 ToMaterial(float3 ship);                // first order
}

public enum ShieldKind : byte { Particle, Energy, Gravity }
public struct ShieldDef { public ShieldKind Kind; public float PoolJ, RechargeW, HitCapJ, MaxBodyKg, AreaM2, RadiusM, DumpW, AbsMaxW, SigmaKgM2, RatedN, RangeM, AMax, IdleW; }
public unsafe struct ShieldState   // SoA in ShieldSystem
{
    public EntityId Grid; public uint ComponentId; public ShieldKind Kind; public bool Raised;
    public float Pool0; public long Tick0; public float Rate;             // lazy: pool(t) = clamp(Pool0 + Rate·(t − Tick0))
    public float4 LobeAim; public fixed int Threats[8]; public long EmittedNTicks;       // gravity only
}
public unsafe struct FacingTable { public fixed byte EmitterA[14]; public fixed byte EmitterB[14]; }   // per grid, baked; 255 = bare

public sealed class CombatHull     // L1c: a ShipHull with lazy bricks and no body
{
    public ShipHull Hull; public NativeList<ulong> BrickBaked; public float4 Attitude, AttitudeGoal; public long QuietSinceTick;   // bitset; quaternions
}

public unsafe struct CombatRating  // per plan hash
{
    public fixed float Fire[5]; public fixed float Reach[5]; public fixed float RoundSpeed[5]; public fixed float Pass[5];
    public float ToughJ, LateralAccel, TargetRadius, MilitaryValue; public fixed float SenseRef[6];
}
public struct PilotProfile { public byte Skill, Style; }                  // ai-ships-and-crew.md owns the tables (D53)
public sealed class SalvoBattles { public int Start(ReadOnlySpan<int> sideA, ReadOnlySpan<int> sideB, long tick, ulong seed); public void RunRound(int battle, long tick); }   // hull rows; heap events only

namespace BrutalGame.Sensors;

public enum SensorKind : byte { Optical, Thermal, Radar, Signature, Gravimetric, CrewScan }   // rings-and-asteroids.md's enum, extended
public struct SensorDef { public SensorKind Kind; public float RefRangeM, Quality, PowerW, EmitRangeMul; public bool Active; }
public struct SignatureState       // per entity, SoA; written on events and at 10 Hz
{
    public float OpticalM2, ThermalW, RadarM2, GravN, MassKg, EmitRangeM; public byte Flags;   // TransponderOn, RadarOn, Jamming, ShieldsUp, Shadowed
}
public struct ThermalLedger { public float WasteW, HeatJ, HeatCapJ, RadiatorW; public bool RadiatorsOpen; }
public struct Contact              // pool of 16,384; index lists per observer group
{
    public int Group; public EntityId Target; public ushort Alias; public byte Quality, BestKind, Flags;   // Ghost, Claimed, Locked
    public float Signal; public float PosErrM, VelErrMps; public int LastSeenTick, QualityTick; public uint BiasSeed;
}
public sealed class SensorSystem
{
    public void UpdateSlice(long tick);                                   // staggered pairs, jobs
    public static float SignalExcess(in SensorDef s, in SignatureState t, double range, float transmittance, float clutter, float dwell);
    public bool TryGetTrack(int group, EntityId target, out Contact c);   // fire control, point defence, lobes, ShipAi
    public bool HasTrack(EntityId actor, long tick);                      // any lawful third-party group at Q2+: factions-and-law.md's Sensor evidence
}
```

## Data

- `data/components/weapons/*.json`, `data/ammo/*.json`, `data/components/missiles/*.json`: the tables of section 1. Each weapon keeps rings-and-asteroids.md's `asteroid` block.
- `data/ship-materials/*.json` gains `ePen`, `eCav` (rings' `EnergyPerVolume`), `eDef`, `eAbl`, `spall`, `albedo`, `radarReflectivity`. Laminates derive them.
- `data/components/shields/{particle,energy,gravity}.json`: section 3's tables. Gravity shields declare `gravitonProjection: "External"`. Region shield classes are factions-and-law.md's data.
- `data/components/sensors/*.json`, `data/sensors/detection.json` (reference signatures, quality thresholds, clutter terms, eyeball angle, ghost time); `data/sensors/ring-obscuration.json` (rings') gains rows for `Gravimetric` (0 / 0) and `CrewScan` (1 / 0.5).
- `data/combat/damage.json`:

```json
{ "vHydroMps": 2000, "gapAreaMul": 4, "brushMinHoleM": 0.2, "brushMinCraterM3": 0.065, "brushesPerShipPerS": 8,
  "etaDentKinetic": 0.15, "etaDentBlunt": 0.5, "dentAspectKinetic": 4, "dentAspectBlunt": 10, "dentEmitDepthM": 0.1,
  "dentMaxSumM": 2.0, "dentTearRatio": 0.35, "dentsPerBrick": 16, "dentsPerShip": 1024, "dentsPerShipPerS": 4,
  "spallConeDeg": 30, "spallRangeM": 6, "spallFrac": 0.1, "ricochetDeg": 80, "contactMinJ": 5.0e4, "contactPatchMinM": 0.5,
  "yieldU": 0.8, "bendRateDegS": 2.0, "impulseMinNs": 1.0e5, "impulseBendDegPerU": 5.0, "hingeMaxDeg": 12, "hingeMaxShearM": 1.0,
  "hingeBandM": 4, "deformGroups": 8, "impulseEvalsPerTick": 8, "hitsPerTick": 256, "hitsPerShipPerTick": 32 }
```

```json
{ "id": "weapon.railgun.l", "family": "Railgun", "kind": "Kinetic", "mount": "L", "ammo": "ammo.rail.5kg",
  "roundKg": 5, "muzzleMps": 8000, "roundAreaM2": 0.004, "shotJ": 4.57e8, "capacitorJ": 5.0e8, "ratePerS": 0.1667,
  "heatPerShotJ": 2.97e8, "heatCapJ": 3.0e9, "coolW": 2.5e7, "lifeS": 20, "slewDegS": 8, "dispersionMrad": 0.05, "wearPerShot": 0.0025,
  "asteroid": { "kind": "Kinetic", "etaK": 0.25, "xiK": 0.30, "shatterCoupling": 1 } }
{ "id": "shield.particle.m", "kind": "Particle", "areaM2": 2000, "poolJ": 2.0e8, "rechargeW": 1.5e7, "hitCapJ": 5.0e6,
  "maxBodyKg": 30, "idleWPerM2": 500, "efficiency": 0.5, "gravitonProjection": "None" }
{ "id": "sensor.thermal.m", "kind": "Thermal", "refRangeM": 5.0e5, "powerW": 1.0e5, "active": false }
```

## Files

| Where | What |
| --- | --- |
| New `src/Combat/` (Sim, no Vulkan) | `WeaponDefs.cs`, `WeaponSystem.cs` (fire control, capacitors, heat, recoil), `RoundPool.cs`, `RoundSweep.cs` (bubble hash, `SweepGrid`, hull, feeds `ProjectileRailSweep`), `Beams.cs`, `Missiles.cs`, `PointDefence.cs`, `HitLaw.cs`, `HullDda.cs`, `DamageAccum.cs`, `Spall.cs`, `ContactHits.cs`, `Shields/ParticleShield.cs`, `Shields/EnergyShield.cs`, `Shields/GravityShield.cs` (through `TractorSystem`'s force pass), `Shields/ShellHits.cs` (rounds and beams into `StationShield` and `RegionShield`), `CombatHull.cs`, `Engagement.cs` (L1c promotion), `CombatRating.cs`, `SalvoBattles.cs` |
| New in `src/Ships/` | `Deform/HullDent.cs`, `Deform/HullDeformation.cs`, `Deform/DeformEffects.cs` (doors, seals, mounts, wiring), `Deform/Repair.cs`; `Structure/StructuralYield.cs` and `Structure/ImpulseEval.cs` beside tractor-beams.md's graph; `BrickMaterial` in the bake |
| `src/Sensors/` (wormholes.md started it) | `Signatures.cs` stays; add `SignatureState.cs`, `ThermalLedger.cs`, `Detection.cs`, `ContactTable.cs`, `SensorSystem.cs`, `LineOfSight.cs`, `Scans.cs`, `CrewScan.cs`, `Ew.cs` |
| `src/Sim/Net/` | contact-gated interest, the combat channel and its messages |
| Client | `hull.vert` (dent and hinge map), `CombatFx` (tracers, beams, shield hits, decals), `SensorPanel`, `ContactHud`, `WeaponPanel`, `DamagePanel` (dents and hinges overlay); designer: Arcs view (turrets, sensors, shield facings), Armour view (depth by weapon per facing), combat rating in `StatsPanel` |
| Profiler | rows: Rounds (live, swept, rail budget), Beams, Hits (resolved, carried), Brushes, Dents, Bends, Shields, Sensors (pairs, contacts), L1c (hulls, bricks), Salvo battles |
| Tests | `tests/Combat.Tests/`, `tests/Sensors.Tests/` |

## Tests

- **Hit law:** depth and hole radius match the section 2 table for the four kinetic rounds and four materials within 2 %; a 60° plate is twice as thick; spaced plates stop a railgun that the same mass in one plate does not; sloped and layered laminates derive their numbers from layers.
- **DDA against the bake:** 10,000 random rays against three hulls agree with a brute-force SDF march to within one coarse cell; zero allocations.
- **Accumulators:** N sub-brush hits emit exactly `floor(N · V / 0.065)` brushes; pinholes add their area to the right room pair; an idle cell is dropped after 60 s.
- **Dents:** volume from energy; merge keeps volume; the map is divergence-free (a rendered dented box keeps its volume within 1 %); `MassMoments` are bit-identical before and after any number of dents; a hole made before a dent moves with it and one made after sits at the hit; tear at `δ/R` 0.35.
- **Effects:** door states at 3 and 8 cm; seam leak area; mount misalignment thresholds; a wall pushed into a housing lowers its Integrity; glazing never dents.
- **Deformation:** a cut held at `u` 0.9 bends at 1°/s and breaks at 12°; an impulse case from each source (shell, ram, AG gradient) bends the expected cut; mass unchanged, centre of mass and inertia match a brute-force sum over transformed bricks; ≤ 8 groups, overflow tears; hinge band vertices are continuous.
- **Ordering:** a recorded battle replays to identical brushes, dents and hinges on the server (hits applied in hit order whatever the job timing).
- **Shields:** absorb arithmetic and lazy settle for each kind; mass limit and hit cap; the facing lookup; energy pool fill and leak; lobe miss distances match section 3; saturation; reaction on the mount; a lobe raised in a tunnel is struck on that tick; a station shell and a region shell take a weapon round through their absorb calls, and a region shell takes beam power.
- **Sensors:** every row of the ranges table within 5 %; quality thresholds and hysteresis; `RingObscuration` ranges unchanged for the rings tables; eclipse removes reflected light; a planet occludes all kinds but gravimetric; a contact the group cannot detect never appears in any message to it (a fuzz test over the wire); track bias does not average out over 60 s; crew scan stages and wall attenuation; scans never promote crew.
- **Range combat:** two bubbles 80 km apart exchange fire with no merge; an L1 hull under a lock becomes L1c and takes the same damage, to the brush, as the same hull hit at L2 by the same rays; L1c → L2 → L1c → L1 keeps damage, ammo and heat (ai-ships-and-crew.md's no-free-healing and no-duplication checks extended to dents and hinges).
- **Salvo against simulated:** for 12 plan pairs and each skill tier, win rates of 100 headless simulated duels and of the salvo model agree within 20 points, and mean time to kill within a factor of 2; military values rank the 12 plans in the same order as their simulated win rates.
- **Caps:** round, beam, hit and brush caps hold under a 64-ship stress scene; carried hits drain; the combat channel stays under 12 KB/s per client.

## Milestones

All after M8 (rooms, damage, hull LOD). Each ends with a measured exit check.

| | Scope | Exit check |
| --- | --- | --- |
| **CS0** | Weapon data, `RoundPool`, frame-space sweep, `SweepGrid`, hull DDA, the hit law making holes; autocannon and mass driver on a turret; server authority and fire effects | `--gun-bench`: 4096 rounds under 0.4 ms of worker time; the depth table reproduced; two clients see the same holes |
| **CS1** | `DamageAccum`, pinhole leaks, components and crew in the path, spall, laminate numbers, railgun and capital driver, recoil, heat, capacitors, magazines | Strafing a cabin leaks it before a brush appears; an under-built railgun mount tears off; hits per tick and carried hits in the profiler |
| **CS2** | Dents: records, the shader map, collision points, effects, contact hits, press-out repair | A 20 m/s docking accident leaves the dish and jams the door; mass identical; no brick re-baked (bake counter) |
| **CS3** | Deformation: yield in the tractor pass, the impulsive pass, deform groups, hinge band, mass properties, straightening | A capital shell on a frigate's nose bends it and the spinal gun reports misaligned; two tractors bend a hull before they tear it |
| **CS4** | Signatures, thermal ledger, detection, `ContactTable`, contact-gated replication, HUD; optical, thermal, radar; then gravimeter, signature reader, crew scan, scans | The ranges table measured in game; the wire fuzz test; a cold ship in a planet's shadow is not in a second client's memory |
| **CS5** | Particle, energy and gravity shields; station shell hook; tunnel strike; `RadiationShield` folded in | Section 3's "what that means" lines measured: stalemate with one gun, miss distances, pool fill times |
| **CS6** | Lasers, particle beams, plasma, missiles, point defence, AM and AG delivery, jammer and decoys | A missile salvo against point defence resolves at the predicted range; a killed AM torpedo doses the predicted radius |
| **CS7** | Rails combatants, long-range engagement, salvo battles, combat ratings and military values; weapons through region shells; `HasTrack`, comms jamming and station turrets for factions-and-law.md's F3 | The range-combat and salvo-against-simulated tests; a witnessed NPC skirmish 100 km off costs under 5 µs a round and its losses show in the rows |

## Changes to other docs

- **ship-hulls.md:** damage has three forms; open question 3 is answered (D59) by the deformation map, and the "dents could displace control vertices" note is withdrawn. `BrickState` gains coarse material (a byte, or a 2-bit palette for mixed bricks). `ShipHull` gains `HullDeformation` and `DamageAccum`. Hull instance records gain a dent index list and group ids; `hull.vert` applies the map. Exterior collision points and interior collision vertices are displaced. Damage brushes are stored in material space. The hull LOD rules for dents and hinges.
- **tractor-beams.md:** cuts yield at `u ≥ 0.8` before breaking (bend rate, hinge limits, 70 % capacity after yield); the impulsive evaluation is the "own damage model" for rams, pulses and shells; mount table rows for weapons and gravity shields; gravity shields run through the force pass as Reaction-drive emitters and count in the bubble's beam caps as reserved slots, like defence beams.
- **rings-and-asteroids.md:** the reference projectile classes are this doc's weapons (same energies; the example id `weapon.railgun.s` becomes `weapon.railgun.l`); `e_hull` is `eCav`; "reduced-mass contact damage" is this doc's contact hit; `StationShield`'s weapons hook is taken (rounds, shells, bolts and missiles offer their energy); `SensorKind` gains `Gravimetric` and `CrewScan`, and the station watch uses a `ContactTable`; open question 9 (sensor ownership) is closed: hiding in a choked band or behind shatter dust is a real tactic against optics and thermal, and is not one against radar, gravimeters or signature readers.
- **exotic-matter.md:** `RadiationShield` becomes the `EnergyShield` S class; the lance and warheads meet shields as section 3 says; breach brushes gain the dent ring; pulse shear goes to the impulsive pass first (bends), brushes for the remainder; gravity shields join the graviton-projection table as `External`; containment signature ranges are the M reader's (S × 0.5, L × 2); the thermal ledger takes the waste-heat figures that doc reports.
- **wormholes.md:** `Sensor {Quality, Range}` is the signature reader component; gravity shields are among the "gravity weapons" the retaliation rule strikes; energy shields absorb wall discharges and storm hits; mouths are gravimetric contacts at the stated range.
- **warp.md:** `WarpDepart` is routed by contact or gravimeter; silent commits also reach gravimeters as a bearing. (Region shields as exclusions, and its open question 6, are factions-and-law.md's changes.)
- **ship-atmosphere.md:** "no ship shields" is superseded by D55; air barriers still stop no weapons, and particle panels hold no air; pinholes add `Leak` area directly; dents and hinges add seam leaks, change room volume and jam doors; the barrier's heat note feeds the thermal ledger.
- **ship-design-and-construction.md:** new families (weapons by class, magazine and feed, the three shields, sensors, fire-control computer, radiator, heat sink, jammer, decoy launcher); validation (arc masks, recoil against mount ratings, shield facing coverage, a Warning for a transponder with no `Logged` wire); the press-out and straighten repair steps; the combat rating shown with the plan. Wiring uses power and data only (D71).
- **ai-ships-and-crew.md:** the L1c level and its promotion rows; the L0 → L1 trigger on a Q2 contact; "an L1 ship cannot be hit and cannot fire" holds for plain L1 only; the per-bubble `ContactGrid` is filtered through `ContactTable`; traffic legs go only to clients entitled to them (transponder on, or held); `DamagePool` rows grow to hold dents and hinges; a loss draw's outcome and witnessed L1 fights use `SalvoBattles`; `PilotProfile` (D53) feeds both models; crew scans read the manifest and never promote.
- **frames-and-zones.md:** tick steps 3, 4, 5, 8c and 9 as above; the interest table's rows beyond "own bubble" are gated by contact or the eyeball rule; "weapons never merge bubbles" beside the merge rule; `ObjectLayers` needs nothing new (rounds are not bodies).
- **factions-and-law.md:** its asks of this doc are met: `HasTrack` (section 4), comms jamming (section 4), station turrets (section 1), weapons through the `StationShield` and `RegionShield` absorb calls (section 3). Its strategic step's "military value" and "defence value" are `CombatRating`'s `MilitaryValue` (section 5). This doc writes `WeaponFire` records and sets the surrendered flag; it defines no offence.
- **README.md:** docs table row; "sensors, combat" leave the missing-systems list; D55, D56, D59 in the Decided table.

**r7 consistency pass (2026-11-26).** The list above was checked against the staged docs and the gaps were closed in place:
- ship-hulls.md, ship-design-and-construction.md, rings-and-asteroids.md and warp.md had applied their parts in round 6. ship-hulls.md's round 6 copy holds the stored form of `HullDent`, `HullHinge` and `DeformGroup` and the hull-side rules (group per brick by solid centroid, the split hand-over, glazing, the palette fallback for a fifth material in a brick); where a field layout differs from the sketch here, its layout wins.
- Applied in r7: rings-and-asteroids.md (station watch on a `ContactTable`, `e_hull` → `e_cav`, the contact hit, `EnergyShield`), tractor-beams.md (yield at 0.8, the impulsive pass, mount rows for weapons and gravity shields, lobes as reserved beam slots), exotic-matter.md (`EnergyShield`, the gravity shield row in the graviton-projection table, signature ranges, the dent ring and pulse shear accepted), wormholes.md (`Sensor` is the signature reader, lobes among gravity weapons, energy shields against discharges), warp.md (`WarpDepart` and silent commits by contact or gravimeter), ship-atmosphere.md ("no ship shields" removed, dents and pinholes as `Leak`, volume and door deltas, thermal ledger), frames-and-zones.md (tick steps, contact-gated interest, weapons never merge bubbles), README.md.
- Added here from other docs' asks: the `CaveDrill` row, the breach-shield battery wording, data-in and data-out ports for shields, turrets and fire control, and where an energy projector mounts.
- ai-ships-and-crew.md is outside the staged set and is being revised separately: its items above are still asks.

## Rejected

- **Dents baked into the SDF** (resample the field at displaced points): a dented brick must be baked with a 2 m pad (~3 × the cost), every dent invalidates the bake cache, and repeated resampling drifts mass. The map costs no bake and conserves mass by construction.
- **Dents as displaced mesh-brush control vertices** (ship-hulls.md's earlier note): only works for mesh brushes, needs a per-ship copy of each mesh (up to 90 KB) and a BVH rebuild per dent.
- **Soft-body or finite-element hulls:** per-tick cost on every damaged ship, and not deterministic across peers.
- **Hit points per hull section:** cannot say where the hole is, so it cannot leak a room, cut a cable or kill a pilot.
- **Rounds as Jolt bodies or Jolt ray casts:** the native ray cast is not exported, bodies cap far below 4096, and neither works on a rails hull.
- **Merging bubbles for long-range fights, or double-precision Jolt:** the first is impossible past 4 km, the second is ruled out in frames-and-zones.md. Frame-space rounds need neither.
- **Client-reported hits with lag compensation:** turrets aim on the server from tracks, so there is nothing for a client to claim.
- **One shield with one health bar:** D55 names three kinds; separate rules are what make sensors, weapon choice and facing matter.
- **Sending every entity in the SOI and hiding it client-side:** free intel for any modified client.
- **Simulating unseen AI battles:** D50 and D54 want data, and factions-and-law.md counts rows. Ratings from the same definitions keep the counted, the salvo and the simulated models honest, and a test compares them.

## Open questions

1. **Pace.** The numbers aim at 30-90 s for a fighter duel and 5-15 min for capitals to a mission kill. Is that the right order of magnitude? One knob (`Tough`, and the weapon rates behind it) moves all of it.
2. **The eyeball rule.** An unlit ship that nobody's sensors hold is not sent to a client beyond 2 km, so it is not drawn at all until it is lit, large enough or detected. That is what stops free intel, but a dark ship can appear at 2 km. Acceptable, or always draw within some range (say 10 km) and accept the leak?
3. **Gravity shields against ships.** As written a lobe pushes away rams, boarding craft and shuttles as well as shells, and doing that to another party's ship is a tractor-class weapon use unless it is on a collision course. Should a gravity shield be able to hold boarders off at all, or should it act only on bodies under some mass?
4. **Lasers' role.** Lasers here are precise and instant but weak against hull: they kill sensors, turrets, radiators, missiles and exposed crew, and drain energy shields. If beam ships should also carve hulls at range, `eAbl` is the knob. Confirm the role.
