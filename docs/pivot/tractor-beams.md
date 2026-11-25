# Tractor beams and hull stress

Status: design only. Nothing has been built or run, and no repo files were changed. Written 2026-11-25 against README.md, ship-hulls.md, frames-and-zones.md, exotic-matter.md, rings-and-asteroids.md, warp.md and wormholes.md. Every number is a knob and every cost is an estimate to measure (`--tractor-bench`, profiler rows below). Where this doc changes another doc, the change is listed under "Changes to other docs". Review.md's rulings still win where they apply. Lead-reviewed the same day; see "Review notes" at the end.

## Recommendation in one paragraph

A tractor beam is a ship component that projects a **cone** from an apex on its housing. The pilot sets polarity (**push or pull**), throttle, range and the cone's **half-angle** (wide = weak, area; narrow = strong, focused). Output is spread evenly over the cone's solid angle, so a body receives the share of the output its exposed area covers, found by a small z-buffered raster of the bodies' **structure nodes** in angle space. Force is applied at the nodes it lands on, so an off-centre lock makes torque. A lock either **Holds** (a gentle spring to a set range, for towing) or **Hauls** (constant full force, for ripping and tearing). The beam burns field-grade EM content per MN·s **emitted**, plus AM at the fixed reaction ratio, plus bus power that grows with range. Without AM it can run **pumped** from bus power at a quarter of its rating. Every newton delivered (after every clamp) pushes back on the emitter's ship. Hulls get a **structure graph**, built lazily when a ship first comes under tractor load: 4 m nodes and components, joined by joints whose strength is the contact area × the material's strength, and by mounts with a rated load. A maximum spanning tree of that graph gives N−1 **cuts** (exact for necks and mounts), plus one axis-aligned **plane cut** per 4 m layer (so plain hulls have cross-sections to tear along), all with exact summed capacities. Each tick a ship is under tractor load, a single O(N) pass sums external and inertial forces up the tree. That gives the exact net force and moment across every cut. A cut or mount over its rating breaks through the existing damage and split path. A lock on an antenna puts nearly the whole beam through a 20 kN mount, so the antenna comes off. Two opposing beams have almost no net force but put their full tension through the cuts between them, so the ship tears.

## User decisions this doc implements

- Tractor beams use exotic matter, energy and potentially antimatter to **push or pull a cone with an adjustable FOV** (wide = weak, area; narrow = strong, focused).
- **Locking onto the wrong component** can put too much force on it: grabbing a ship by a thruster or an antenna rips the part off instead of moving the ship.
- **A powerful set of opposing tractor beams can tear a ship apart.**
- Inherited: tractors are gravity devices on the anti-graviton reaction (exotic-matter.md), they act through the per-body force pass (wormholes.md), a lock blocks warp spool (warp.md), a lock breaks on a wormhole crossing (wormholes.md), and asteroid locks follow the abuse limits (rings-and-asteroids.md).

## Beam model

### Geometry

| Quantity | Meaning |
| --- | --- |
| Apex `o` | Fixed point on the emitter housing (data), ship-local |
| Axis `â` | Turret direction. In Lock mode it tracks the lock point at `SlewDegS`; in Sweep mode the pilot aims it |
| Half-angle `θ` | Pilot setting in `[θmin, θmax]` of the class. Footprint radius at depth z is `z·tanθ` |
| Range `R` | Pilot setting `≤ RangeMax`. Power grows with it |
| Polarity `s` | +1 push, −1 pull. A flip costs `FlipTicks` (30, 0.5 s) at zero output |
| Throttle `τ` | 0–1. Slews at most 2 per second (0 → 100 % in 0.5 s), so loads ramp and the quasi-static stress model stays valid |

**Output.** `F_out = τ · F_rated · k_mode · k_int`:
- `k_mode`: 1 in reaction mode, `PumpedFrac` (0.25) pumped, `OverdriveMul` (1.5) in overdrive (see Consumption);
- `k_int`: emitter `Integrity` factor, `min(1, 2·Integrity)`, so a half-wrecked emitter gives half output. Integrity uses exotic-matter's shared damage rule.

**Intensity: top-hat over solid angle.** Output is uniform over the cone's angular disc. A body that covers angular area `Ω_b` of the disc (after occlusion) receives the fraction `f_b = Ω_b / (π tan²θ)`. This is "energy spread over solid angle": the geometric 1/d² is inside `Ω_b ≈ A_⊥/d²`. Rays that hit nothing are **spill**. They are paid for and do nothing.

**Range fade.** `η(z) = 1` for `z ≤ 0.8 R`, linear to 0 at `R`. Beyond `R` nothing is delivered.

**Own body is never a target.** Nodes of the emitter's own body, and of anything in its docked compound, are rastered as occluders (they shadow what is behind them) but receive nothing: their share is spill. A ship can't push or tear itself, and can't make torque from its own hull.

**Footprint at minimum half-angle** (the "focus" a lock can reach):

| Class | θmin | At range | Footprint radius / area |
| --- | --- | --- | --- |
| S | 0.25° | 150 m | 0.65 m / 1.3 m² |
| M | 0.10° | 500 m | 0.87 m / 2.4 m² |
| L | 0.05° | 2 km | 1.75 m / 9.6 m² |
| XL | 0.05° | 3 km | 2.6 m / 21 m² |

A narrow beam's footprint fits on a component, so the component takes the whole output. Opened up, the same beam spreads over the hull. **Choosing the half-angle is the main skill.**

### Where the force lands (beam raster)

The force samples are each body's **structure nodes** (see Structure model): 4 m hull nodes, one node per component housing, and for rocks up to 8 nodes. Rocks use the octants of a packed voxel body's occupancy, or one node for a loose convex chunk. One raster per beam is shared by every body in the cone, so a body in front shadows a body behind:

1. **Candidates:** bodies in the emitter's bubble whose bounding sphere meets the cone (our own entity spheres, not a Jolt query). They come from a per-bubble uniform grid of entity spheres (64 m cells), walking only the cells the cone's bounding capsule touches, so the cost scales with what is near the cone, not with the bubble's body count (a linear scan of 2,000 bodies would be ~40 µs per beam). At most 64 candidates, nearest first. Characters are handled separately (see Uses).
2. **Project** each candidate node to angle space: depth `z = (p − o)·â`, angular position `(x/z, y/z)`, angular radius `r_node/z`. Cull nodes outside the disc.
3. **Raster:** a 32 × 32 grid over the disc `[−tanθ, tanθ]²` (~804 cells inside it). Each node splats as a disc into the cells it covers and keeps the nearest depth per cell (z-buffer of node index). A node smaller than one cell adds its fractional coverage to its centre cell, but only if no nearer node already owns that cell.
4. **Weights:** each owned cell gives its node `1/804` of the disc. Node weight `w_i` = the sum of those shares, times `η(z_i)`. Per body, `f_b = Σ w_i`.
5. **Hits:** per (beam, body), keep the 15 heaviest nodes plus one "rest" hit at the weighted centroid of the others, assigned to the nearest node. That is at most 16 `BeamHit`s, each with a body-local point (the node centroid). These drive the Jolt force and torque only.
   - **The structural pass does not use the lumped rest hit.** Lumping 99 % of a wide sweep onto one node would make a false hot spot and a false tear there. For a body that has a structure graph and is live (see Triggers), the raster also keeps the full node-weight list (≤ 804 `(node, weight)` pairs, 8 B each, pooled), and the pass spreads the force over it.
6. **Cap:** at most 32k cell writes per beam per update. Past the cap the farthest nodes are dropped. A narrow cone has few nodes in it; a wide cone has coarse cells.

The geometry runs at **10 Hz**, staggered across beams. Between updates the hits stay fixed in body-local coordinates and only the forces are recomputed. **Weights blend** linearly from the previous update's to the new one's over the 6 ticks to the next update (a node absent from one side has weight 0 there). Without the blend, a narrow sweep crossing from hull onto an antenna would step the antenna from 0 to full output in one tick, which breaks the quasi-static assumption below and pops the force.

### Forces each tick

Per hit `i`, with `u_i` the unit ray from the apex to the hit's current world point and `v_i` the hit point's velocity relative to the apex:

**Sweep mode** (no target): `F_i = s · F_out · w_i · g_i · u_i`.

**Lock mode** has two controllers, switched on the HUD:
- **Haul** (constant force): `F_i = s · F_out · w_i · g_i · u_i` on the locked body, like a sweep, but the axis tracks the lock point. This is the hard pull or push: ripping parts off, opposed tearing, dragging a ship into a firing line or a mouth.
- **Hold** (spring-damper to a range `d*`, below): towing, docking, mining. Its demand is scaled by the target's mass (`k = m_t ω_n²`), so it is gentle by design: on a 30 t fighter, `k` is 7.5 kN/m, and 20 MN would need a 2.7 km range error. **Hold can't tear a light ship; Haul can.** That split is deliberate: the user's "wrong component" and "opposing beams" outcomes come from Haul or Sweep, or from Hold on a heavy target whose spring demand is large.

**Emitted output in Lock mode.** Hold emits only what it needs: `F_emit = min(F_out, Σ|F_i| / f_b)`. Haul and Sweep emit `F_out`. Billing, heat and the interposer share all use `F_emit`.

**Other bodies in a lock's cone** (interposers, debris, a second ship) receive the sweep share at the lock's emitted level, `s · F_emit · w_i · g_i · u_i`. The cone is emitting whatever it is aimed at.

**Hold** (one target point `p_L` on a chosen body, a desired range `d*`): a spring-damper on range, spread over the hits by weight. The damping uses each hit's own point velocity:

```
F_avail = F_out · f_b                                   (what the beam can deliver to this body)
ω_n = 0.5 rad/s, ζ = 1, k = m_t ω_n², c = 2ζ m_t ω_n     (m_t: target mass, or the reduced mass if the emitter is lighter)
F_i,rad = (w_i/f_b) · [ −k (|p_L − o| − d*) − c (v_i·u_i) ]
F_i,lat = −(w_i/f_b) · c · (v_i − (v_i·u_i) u_i),   |F_i,lat| ≤ tanθ · |F_i,rad|
F_i = clamp to polarity( F_i,rad u_i + F_i,lat ),  scaled so Σ|F_i| ≤ F_avail;  then × g_i
```

- **Polarity clamp:** in Pull, the radial part may only point toward the apex; in Push, only away. **Hold assist** (an option) flips polarity automatically when the spring needs the other sign, with the 0.5 s flip gap. That gives a soft hold, not a rigid one.
- **Lateral authority is `tanθ`:** a needle-thin lock is a rope. The target swings and the turret tracks it. A wider lock steers and damps sideways motion but delivers less. Docking assist uses a wide lock at short range.
- **Despin emerges** from per-point radial damping: different hit points have different radial velocities on a tumbling body. Spin about the beam axis is not damped by a narrow beam. Rings-and-asteroids' "a lock despins fragments" holds for any spin with a component across the beam.

**Speed and work fade `g_i`** (abuse limit, applies to every target):
```
v_b  = relative speed of the body along the beam direction (away for push, toward the apex for pull), relative to the emitter
W_b  = the body's beam-work ledger: W_b ← max(0, W_b + Σ_beams F_b · (v_body − v_emitter) · dt), int64 kJ, per body
g = clamp((BeamVMax − v_b) / (0.2 BeamVMax), 0, 1) · clamp((EThrow − W_b) / (0.2 EThrow), 0, 1)
BeamVMax = 100 m/s,  EThrow = 500 MJ (= rings' E_cap)
```
- A beam can't add speed past 100 m/s relative to its emitter. A 1e6 kg rock tops out at 32 m/s, a 1e4 kg chunk at 100 m/s.
- **Why a ledger and not `½ m v_b²`:** an emitter-relative kinetic energy resets whenever the emitter catches up. Throw, thrust to match the rock, throw again, and the rock reaches any speed. `W_b` is signed net work summed over every beam on the body, so catching up and re-pushing keeps adding to it, and several emitters share one budget. Braking a body (negative work) refunds it. A steady tow does near-zero net work (tug and tow move together), and opposed tearing beams do none, so neither is limited by it.
- `W_b` resets to 0 only when the body is back within `0.2 · BeamVMax` of its reference velocity (rail velocity for asteroids, the bubble frame otherwise) and no beam has touched it for 60 s. It is stored only for bodies a beam has touched (one int64), dropped on reset.
- **Grapples** (static targets, below) apply the same `g` with the emitter as the moving body, so pushing off terrain or a big rock can't launch a ship past 100 m/s.

**Application (Jolt):** per (beam, body), sum `F = ΣF_i` and `τ = Σ(r_i − r_cm) × F_i` and make one `BodyInterface.AddForceAndTorque(id, F, τ, activation)` call.
- **Activation:** `Activate` only if `|F| ≥ WakeAccel · m` (1e-3 m/s²). Below that the call is skipped entirely. Unrelated bodies never get a call, so a beam grazing a sleeping field wakes nothing.
- **Static and kinematic targets** take no force. A natural rock under `KinematicMassLimit` turns dynamic on a **lock only** (rings' rule); a sweep never converts a static body.
- **Above the limit, and terrain:** the body is a grapple point. Only the reaction acts, so the emitter pulls itself in or pushes itself off.
- **Anchored station targets:** the station's anchor is debited `EmPerNs · |F| · dt` (exotic-matter's rule) and the reaction acts on the emitter.

### Reaction on the emitter (Newton's third law)

- The emitter's ship gets `−ΣF` over every body the beam delivered to, applied at the apex through `AddForceAndTorque`. Pulling a heavy target pulls you; pushing a rock away pushes you back.
- **Delivered means after every clamp.** `ΣF` is taken after the fades `g_i`, the character cap, protected-zone zeroing and any `TractorDampener` cancellation. Force that a dampener cancels is spill, so it has no reaction either. Otherwise a ship could push on a friendly dampened ship, feel the full reaction while the other feels nothing, and fly as a propellant-free pair.
- **Spill has no reaction.** Only delivered force is an interaction, so the beam can't be used as a reactionless drive: momentum in the bubble is conserved exactly. A unit test checks `Σ momentum` before and after.
- **Angular momentum too.** Radial hit forces lie on the ray through the apex, so their reaction at the apex is collinear. The lateral (damping) part is not, so the emitter also gets the couple `−Σ (p_i − o) × F_i,lat`. With it, `Σ angular momentum` is conserved as well (same unit test), and a held target can't be used to spin the emitter up for free.
- Push and pull pairs between two ships cancel exactly (each beam's force and reaction sum to zero), and a beam that brakes a body only dissipates (nothing is returned to the bus or the tanks), so no arrangement of beams is a free energy or momentum source. Grapples (static or anchored targets) exchange momentum with the world, like pushing off a wall, and pay EM at 100× an anchor's rate.
- **The reaction goes through the emitter's own mount** (rated `1.5 × F_rated × OverdriveMul`) into the emitter ship's structure. A strong emitter on a weak hull can tear its own ship (see Structure model).
- **Station-mounted emitters:** an anchored station's reaction is debited to its anchor.

### Lock and sweep

| | Lock | Sweep |
| --- | --- | --- |
| Target | One body and one node or component, plus a body-local point | None: everything in the cone |
| Axis | Tracks the lock point at `SlewDegS` | Pilot aim, or fixed to the hull |
| Force | Hold: spring-damper toward `d*` (reel in or out with the range control), clamped to `F_avail`. Haul: constant `s · F_out · w_i` on the locked body | Constant `s · F_out · w_i` |
| Acquire | 30 ticks of ramp after the server accepts | Immediate (subject to the throttle slew) |
| Blocks warp spool | Yes (Hold or Haul), while `F_avail ≥ m_target · WarpHoldAccel` (0.5 m/s²). While it blocks, `F_emit` is at least `m_target · WarpHoldAccel / f_b`, so a tackle always pays for the hold it claims, even when the spring is idle | No |
| Breaks on | Range > R; lock point not hit for 60 ticks (occluded or out of the cone); target crosses a wormhole; the emitter is starved, unpowered or destroyed; target becomes intangible (Warp, Throat); the pilot releases | n/a |
| Locked part detaches | The lock follows the node's new body (rip off a thruster and you are now holding the thruster) | n/a |

Rate limit: at most 1 lock acquisition per second per emitter. At most 4 active beams per owner per bubble (inside the 16 per bubble), so one player can't take every beam slot in a bubble, and stacking many S emitters is capped.

### Consumption, power and heat

Units follow exotic-matter.md: 1 EM = 1 kg of content, `AmPerEm = 0.005`.

```
EM content:  ċ_EM = EmPerMNs · F_out          EmPerMNs = 0.1 g per MN·s, at q ≥ 0.60 (field grade or purer)
AM:          ṁ_AM = AmPerEm · ċ_EM             reaction mode only (0.5 mg per MN·s)
Bus power:   P = BaseMW + 1 MW per km of R     always
             + PumpMWPerMN · F_out             pumped mode only (100 MW per MN)
Heat:        Q = HeatWPerMN · F_out            reaction mode (0.5 MW per MN, ×3 in overdrive)
             Q = 0.2 · PumpMWPerMN · F_out     pumped mode
```

- **Billed on emitted output (`F_emit`, see Lock mode), not on delivered output.** A focused lock delivers what it emits, so exotic-matter's 0.1 g per MN·s figure is unchanged for it. A wide sweep pays for its spill. That is the price of area. In the formulas below `F_out` means `F_emit`.
- **Three modes:**
  - **Reaction** (EM + AM): full rating. This is the normal mode.
  - **Pumped** (EM + bus power, no AM): bus power drives the field instead of annihilation, capped at `PumpedFrac` = 25 % of rating. It is the "potentially antimatter" case: ships without AM traps still have a weak tractor, but it is power-hungry and hot. An S beam pumped is 50 kN for 5 MW, which fission can run. An L beam pumped is 5 MN for 500 MW, which takes cores.
  - **Overdrive** (EM + AM, purity boost): `OverdriveMul` 1.5 × rating for at most 10 s, recharging at 1 s per 6 s. It needs feed with nominal q ≥ 0.85 (throat grade) in the emitter's tank, and triples heat per MN. Throat-grade stock buys a burst, not a better rate.
- **Devices debit true content** (exotic-matter's unassayed-stock rule). Unassayed stock runs out early; it has no other effect on a tractor.
- **Starvation:** if EM, AM (in reaction mode) or bus power runs short, output ramps to 0 over 0.5 s. Tractors are bus class 2 (flight and sensors).
- **Heat:** each emitter has its own heat store `HeatCapJ` and cooling `CoolW` (more with a `Coolant` socket once a ship thermal system exists). When the store is full, the throttle is clamped to the cooling-limited steady state. Settled in the 10 Hz ledger.
- **Accounting:** each tick adds `F_emit` (integer N) to an int64 accumulator per emitter, a few ns. The 10 Hz ledger debits content, AM and heat from the accumulators, which keeps it deterministic.

**Worked rates:**

| Case | Emitted | EM content | AM | Power |
| --- | --- | --- | --- | --- |
| S mining lock on a 2 m chunk, full | 200 kN | 72 g/h | 0.36 g/h | 0.5 MW |
| M towing a 300 t ship at 0.5 m/s² | 150 kN | 54 g/h | 0.27 g/h | 2.0 MW |
| M at full | 2 MN | 0.72 kg/h | 3.6 g/h | 2.0 MW |
| L at full (combat lock) | 20 MN | 7.2 kg/h (2 g/s) | 36 g/h | 10 MW |
| L in overdrive, 10 s | 30 MN | 30 g | 0.15 g | 10 MW |

## Structure model

### Choice: quasi-static loads over a baked cut tree

**Why it is enough.** Tractor loads ramp (throttle slew 0.5 s) and sit far below the hull's natural frequencies. For a rigid body, Newton's law on any sub-body gives the **exact** net force and moment across any cut: the sum over one side of (external force − mass × local acceleration). The only approximation is assuming that load spreads over the cut's joints in proportion to their strength. So we never solve for internal stress. We only pick which cuts to test and bake their capacities.

**The cut tree.**
- Build a weighted graph at bake: nodes plus joints (below), each joint weighted by its capacity.
- Take its **maximum spanning tree** (Kruskal by capacity).
- Each tree edge `e` defines a **fundamental cut**: the subtree under `e` against the rest. Both sides are connected, so a break there is a real two-piece split.
- **Cut capacity** is the sum over **every** graph joint crossing that cut, tree and non-tree edges alike. It is computed for all N−1 cuts at once with the subtree-sum and LCA trick:
  - **Unoriented sums** (`ΣσA` per strength, `ΣσA·r`, `ΣσA·|r|²`): each joint `(u,v)` adds its value at `u` and at `v` and subtracts it twice at `lca(u,v)`; a subtree sum then gives each cut's total. (A tree edge's LCA is its parent end, so it lands only on the child's cut.)
  - **Oriented sums** (`ΣA·n̂_uv`, `n̂_uv` from `u` to `v`): `+` at `u`, `−` at `v`, nothing at the LCA, which leaves the normal pointing out of the subtree.
  - The centroid and gyration sums must use the unoriented rule. With `+u −v` they would cancel to garbage.
- **Deterministic bake:** joint capacities are quantized to integers (cell-face count × material strength code) before Kruskal, ties broken by edge index, sums in fixed order. All peers get the same tree.
- **Plane cuts (added to the MST cuts).** The MST family is exact for necks (a neck is the last edge Kruskal adds, and its fundamental cut holds every neck joint), but it is **not** guaranteed to contain a uniform hull's cross-sections. With equal joint strengths, Kruskal's tie order can make the tree a comb whose fundamental cuts are long strips, and then no cut separates nose from tail, so opposed beams on a plain tube would never tear it. So the bake also builds, for each of the ship grid's 3 axes, one **layer cut** per 4 m layer boundary: every node below the plane against every node above it.
  - Capacities are per-layer sums of the joints crossing the plane (one pass over joints).
  - Per evaluation: bucket `f_j` and `r_j × f_j` by layer, prefix-sum along the axis. That is ~3N adds and L ≤ 3 × 128 cut tests, about +30 % on the pass.
  - A side may be disconnected (a U-shaped hull); a break still severs along the plane, and the split check sorts out the pieces.
  - Oblique tears are still not tested. Accepted: they are bounded below by the nearest axis plane plus the necks.
- **Components are leaves:** a component node's parent is the hull node holding its anchor cell, and its cut capacity *is* its mount rating. Hull cuts and mount failures are one pass.

**Per tick, for a live ship** (only ships under tractor load, see Triggers):
```
a_cm = ΣF_ext / M,   α = I⁻¹ (Στ_ext − ω × Iω)         (M, I from MassMoments; F_ext = this tick's tractor hits (full node-weight lists) + own thrust + other force-pass terms; contacts excluded)
f_j  = F_ext,j − m_j (a_cm + α × r_j + ω × (ω × r_j))    per node (uniform gravity cancels in free fall; plating is interior-only)
post-order (children first):  S_F[j] = f_j + Σ S_F[child],   S_M[j] = r_j × f_j + Σ S_M[child]
cut of node j: F = S_F[j],  M = S_M[j] − c_j × F           (c_j: cut centroid)
F_n = F·n̂_j  (n̂ out of the subtree: +tension),  F_t = F − F_n n̂_j
u_j = max(F_n / T_j, −F_n / C_j) + |F_t| / S_j + |M| / M_j
```
- The root's sum is zero by construction (`Σf = 0`). A unit test asserts it as the equilibrium check.
- **Why opposed beams tear with zero net force:** two equal Haul beams on nose and tail give `ΣF_ext ≈ 0`, so `a_cm ≈ 0` and there is no inertial relief: `f_nose = +F`, `f_tail = −F`. Every cut (MST or plane) with the nose on one side and the tail on the other carries the full `F` as tension. One beam alone is partly relieved by the target's acceleration (`F` × the far side's mass fraction), and the speed cap ends that within a fraction of a second. Opposition is the way to tear.
- **Anchored stations:** the station anchor component is the root, and `a_cm = α = ω = 0`. All tractor load flows to the anchor, so grabbing a module far from the anchor can tear the module off.
- **Docked compounds:** the two graphs are joined by a collar edge, forced into the tree with the collar's rating. Pulling a docked ship away breaks the collar, which is a forced undock event. Landing clamps work the same way.

**Capacities (baked):**
- **Nodes:** the 4 m cells of the 0.5 m solid occupancy, one node per connected solid component inside a cell. These are the cells the exterior convex decomposition already labels.
  - Node size doubles (8 m, 16 m) until `N ≤ 4096`. A 100 × 40 × 30 m ship has ~1,000 nodes; a 500 m capital has ~3,000 at 8 m.
  - Node mass is occupancy × density, with components as their own nodes. Σ node mass equals `MassMoments.MassGrams` by construction.
- **Joints:** between face-adjacent nodes. Area `A = (solid 0.5 m cell-face pairs across the shared 4 m face) × 0.25 m²`, at the face centre. Strengths come from the weaker material of each cell pair: `T = Σ σ_t A`, `C = Σ σ_c A`, `S = Σ σ_s A`.
- **Per cut**, from additive sums: area, centroid `c = ΣσA·r / ΣσA`, normal `n̂ = normalize(Σ A n)`, and radius of gyration `ρ = sqrt(ΣσA |r|² / ΣσA − |c|²)`. Moment capacity `M = T · ρ`.

**Material strengths** (`data/ship-materials`, MPa per m² of **solid** cross-section; game-scaled for a structural composite, like the densities):

| Material | Density kg/m³ | Tensile | Compressive | Shear | Use |
| --- | --- | --- | --- | --- | --- |
| `hull.light` | 1,200 | 0.5 | 1.0 | 0.4 | Fairings, cheap shells |
| `hull.standard` | 1,500 | 1.0 | 2.0 | 0.75 | Default (exotic-matter's 750 kg/m² per 0.5 m) |
| `hull.armour` | 4,000 | 2.5 | 6.0 | 2.0 | Armour belts |
| `hull.frame` | 2,500 | 6.0 | 8.0 | 4.5 | Spars, keels, tow-point backing |

A 4 m run of 0.5 m standard wall is 2 m² of joint, which holds 2 MN in tension. A 15 m fighter's mid-section is ~8 m², so 8 MN. A 100 m, 2,000 t ship's mid-section (shell plus decks) is ~100 m², so 100 MN.

**Mount ratings** (`MountRating` on each `ComponentDesign`, defaults by core; the effective rating is `min(core rating, footprint solid area × hull σ)`):

| Core | Tension / shear | Moment |
| --- | --- | --- |
| Antenna, sensor, light | 20 kN | 20 kN·m |
| Seat, console, door | 50 kN | 50 kN·m |
| RCS block | 100 kN | 100 kN·m |
| Thruster | 3 × max thrust | 3 × thrust × 1 m |
| Tank, trap, cargo pod | 10 × full mass × 9.81 | same × 1 m |
| Reactor, drive core | 5 MN | 5 MN·m |
| Tractor emitter | 1.5 × rated × OverdriveMul | same × 1 m |
| Docking collar | 5 MN / 2 MN | 5 MN·m |
| **TowPoint** S / M / L / XL | 1 / 10 / 50 / 200 MN | same × 1 m |
| Option `ReinforcedMount` | × 3, +20 % housing mass | |

### Triggers, breaks and caps

- **Live ships:** a ship is evaluated only while it receives ≥ 1 kN from any beam, or its own emitter delivers ≥ 1 kN, and for 60 ticks after.
  - While live, its own thrust is included in `F_ext`, so a ship thrusting against a lock loads its own structure. Running is part of the tug-of-war.
  - So a 1 kN docking nudge can't make a ship's own thrust tear it, the build tool **refuses** (not just warns about) a blueprint whose full own thrust on any axis gives `u ≥ 0.9` on any cut or mount. Every intact ship can fly itself. A damaged ship can fall below that, and then a tow can finish it: accepted, it is the physics.
  - Not live means no evaluation, whatever else happens. Rams, pulses, tidal stress and shrapnel keep their own damage models (exotic-matter, rings-and-asteroids).
- **Rate:** 20 Hz per live ship (every third tick, staggered).
- **Break rule:**
  - A cut or mount breaks at `u ≥ 1` on 2 consecutive evaluations (0.1 s), or at once at `u ≥ 1.5`.
  - Highest `u` first. At most 1 hull cut and 4 mounts per ship per evaluation, and at most 4 hull cuts per ship per second, so a capital ship comes apart in visible stages, not 50 pieces in one tick.
  - Server-wide, at most 4 hull cuts per tick, in a fair queue (one per ship, round-robin).
  - Breaks are queued in the force pass and applied in the next tick's step 3, in damage-application order. That is the same "cascades go to the next tick" rule as everything else.
- **Mount break:** the component detaches as its own dynamic body. This extends ship-hulls' detach rule, which today covers only "anchor cell destroyed". A shallow jagged subtract brush (0.25 m) marks the footprint. It counts against the damage cap and can't breach a 0.5 m wall on its own.
- **Hull cut break (tear):**
  - MST cut: walk the subtree's contiguous post-order range. Every adjacency to a node outside the range is a crossing 4 m face. Plane cut: the crossing faces are the joints between the two layers.
  - Coplanar adjacent faces merge greedily into rectangles, giving at most 32 jagged subtract **tear brushes**. Each is a box 1.0 m thick on the face plane, edged with `Noise.cs` and flagged `Damage`.
  - Then the existing split check runs (solid union-find over the touched bricks): the heavier piece keeps the body, the other gets a seeded blueprint, and pieces under 2 m³ become debris.
  - If brush caps (64 per brick, 4096 per ship) stop a full sever, the cut stays loaded and breaks again at the next evaluation.
  - Mass inside the tear brushes is lost (debris VFX only).
- **After a break:** that ship's graph rebuilds on a worker (~0.2 ms per 1,000 nodes). Its evaluation pauses until the new graph lands, 1–3 ticks, so one load can't double-break. Locks follow the body that holds their node.
- **Graph build is lazy** (deterministic integer traversal, server-authoritative). A ship's graph is built only when it becomes live (server) or is a client's forecast target, and is dropped 10 s after it stops being live. Damage only marks it dirty. Rebuilding on every damage event for every ship would cost ~0.6 ms × 6 Hz per 3,000-node ship in a battle, for ships nobody is tractoring.
  - A ship that turns live evaluates only once its graph lands (1–3 ticks). Loads ramp (acquire 30 ticks, throttle slew), so nothing is missed.
  - A live ship's graph reruns after a topology change, at most once per 10 ticks.

**Limitations (accepted):** tears follow node boundaries (4 m or coarser), smoothed by the jagged brushes. A node never breaks internally. Buckling and fatigue are not modelled. Loads are quasi-static, which is valid because of the throttle slew, the acquire ramp and the hit-weight blend; impact loads belong to rams. Contacts are excluded, so a ship pinned against a rock by a beam is evaluated as if free (it under-reports crushing; rams own that). Only MST cuts and axis plane cuts are tested, not oblique ones.

### Worked cases (standard hull unless noted)

| Case | What happens |
| --- | --- |
| M beam, θ 0.1°, Haul, locked on a 300 t ship's antenna (50 kg, 20 kN) at 300 m | Footprint 0.86 m² sits on the antenna: up to 2 MN through a 20 kN mount. `u` passes 1.5 at 1.5 % of the acquire ramp, so the antenna rips off at the first evaluation. The lock now holds the antenna |
| Same, but Hold reeling in | `k` = 300 t × 0.25 = 75 kN/m, set by the **ship's** mass, so any range error over ~0.3 m demands more than the 20 kN mount. The antenna still rips: the wrong component fails whichever controller is used |
| Same beam opened to θ 10° | Footprint 8,800 m², the ship covers ~300 m², so 68 kN is delivered over the whole hull and the antenna takes ~230 N. Nothing breaks; the ship drifts in at 0.2 m/s² |
| M beam (2 MN) Haul on a thruster (2 t, rated 4.5 MN / 4.5 MN·m) of the 300 t ship | Mount load ≈ 2 MN − 2 t × 6.7 m/s² ≈ 1.99 MN: tension `u` 0.44, plus 0.44 per metre of housing offset from its anchor cell in moment. It holds (about 0.45–0.9). With an L beam (20 MN) it rips |
| M beam Haul on a 30 t fighter's wingtip; wing root 1 m² | Wing root carries ~1.9 MN in tension plus bending, `u > 2`: the wing tears off |
| Two L beams (20 MN) Haul, pulling a 30 t fighter's nose and tail apart | Net ≈ 0, no inertial relief, no speed fade (`v_b ≈ 0`) and no work (`W_b` stays 0); mid-section (8 MN) carries 20 MN, `u ≈ 2.5`: torn in half within 0.2 s of the second beam's ramp |
| One L beam Haul on the fighter's nose | With the ramp (0.5 s) the fighter reaches the 80 m/s fade start at 0.35 s, when the beam is at 13.9 MN: mid-section load ≈ 7 MN, `u ≈ 0.87`, and then the fade cuts the force. It does **not** tear, unless the fighter thrusts away hard (its own thrust adds to the load) |
| Two L beams on a 2,000 t, 100 m ship, opposed | Mid-section (100 MN) carries 20 MN, `u` 0.2: holds. Its exposed thrusters and antennas still go |
| Two XL beams (100 MN) on the same ship, opposed | Mid-section carries 100 MN plus misalignment moment, `u ≥ 1`: the ship tears at its weakest plane or neck cut. It needs a station or two capitals |
| One L beam Haul on a module of an anchored station (neck 8 m²) | The anchor is the root and `a = 0`, so there is no inertial relief: the whole 20 MN goes through the neck, `u ≈ 2.5`. One beam suffices on a pinned structure. That is why delivery onto another party's station is gated by protected zones (Interactions) |
| L emitter (20 MN) fitted to a light-hull outrigger (2 m², 1 MN) | Pulling a heavy target tears the emitter's own outrigger off. The build tool's Stress view flags it |

## Targeting and UX

- **Pick:** a reticle ray hits the target's component housings first, then the hull (`TriangleBvh` on the client, coarse occupancy march on the server's check). The lock stores `(entity, component id or hull node, body-local point)`. **TowPoints snap** within 2 m of the reticle.
- **Settings on the HUD:** mode (Sweep, Lock-Hold, Lock-Haul), polarity, throttle, half-angle (mouse wheel), range, desired range `d*` (reel in/out), Hold assist.
- **Stress gauge on the locked part:**
  - The client runs the same structural pass for its locked target at 10 Hz. It already has the blueprint, damage layer and therefore the graph by hash, and the beam state is replicated.
  - It shows:
    - the locked part's mount `u`;
    - the worst cut's `u`, with that cut's faces outlined on the target;
    - the emitter's own mount `u` (the reaction);
    - a "max safe throttle" marker. `u` is close to linear in throttle, so it is solved from two evaluations.
  - Colours: green below 0.6, amber 0.6–1.0, red "WILL TEAR" at 1.0 or more.
- **Safe-lock** (default on): clamps throttle so the forecast `u_max ≤ 0.9`. Turn it off to rip parts on purpose.
  - It is a forecast, not a guarantee. The client doesn't know the target's next thrust command or damage the server hasn't replicated yet, and it can only lower the throttle at the slew rate. So a wrong-component lock with Safe-lock on can still rip a part. It is never a working tow either: an antenna held at `u` 0.9 gives 18 kN, which moves a 300 t ship at 0.06 m/s², and the gauge says why.
  - **Server-enforced Safe-lock:** wherever a protected zone permits a lock on another party's body (docking assist), the server clamps it to `u_max ≤ 0.9` with its own evaluation, and the pilot can't turn it off. A station's docking assist can't be used to tear visitors or force-undock them at the collar.
- **Victim side:** crew of a tractored ship get a hull-stress gauge from the server (5 Hz) and the emitter's position.
- **Skill = hardpoints:**
  - The **`TowPoint`** component is a lock target with a high mount rating and a large footprint (4 × 4 m for M).
  - Back it with `hull.frame` and the load goes into the keel, not a panel.
  - Lock narrow on a tow point to move a ship hard; open the cone to move a ship that has no tow points gently.
- **Build tool Stress view:** applies each tow point's rated load and full own thrust as test loads and colours every cut by `u`. It also warns when an emitter's reaction or a tow point's rating exceeds the weakest cut behind it.

## Uses

| Use | How |
| --- | --- |
| **Towing** | Lock on the tow point; Hold assist keeps the range. A tow is a rope with `tanθ` of lateral authority. Tug and tow form one contact island for bubble transfers |
| **Salvage** | Wrecks and debris pieces (split bodies) are ordinary dynamic bodies. Rip the loose parts off on purpose: lock narrow on a component and pull |
| **Cargo** | Loose pods and containers by lock; hangar tractors inside a grid bubble park small craft and crates (beams never cross a hull between grid and exterior scenes) |
| **Mining** | Rings' S tractor: chunks to the intake by lock. Rings' "×2 tractor vacuum mode" is a wide pull sweep centred on the intake mouth. Cloud mass is not a body; the ×2 stays a flat multiplier on intake sweep |
| **Asteroid manipulation and throwing** | Lock a cold rock, accelerate it, release: it keeps its velocity. Capped by `BeamVMax` and the per-body work ledger `W_b ≤ EThrow` (500 MJ = rings' `E_cap`), shared by every beam and not reset by catching up and re-pushing. Lock refused within `ProtectRadius` of protected zones and on bodies with S > 0.3; sweep delivers zero to asteroid bodies in both cases. A released body with `W_b` above 50 MJ is tagged `Thrown` with the emitter's owner for attribution, and counts against the owner's rings rate limit (6 events a minute). A `Thrown` body that later enters a protected zone keeps the tag, and its contact damage to other parties' grids there is zeroed and logged against the owner (proposed rings abuse-limit line), so a rock thrown from 5.1 km can't do what a lock inside the zone may not |
| **Pulling ships into wormholes** | Lock the victim and drag it through the mouth. The lock breaks on its crossing (wormholes.md) and the emitter stays behind unless it follows. Pairs with interdiction catchers: interdiction moves the far end, the tractor provides the force |
| **Anti-escape** | A lock with `F_avail ≥ m_target · 0.5 m/s²` blocks the target's warp spool: S holds ≤ 400 t, M ≤ 4,000 t, L ≤ 40,000 t, XL ≤ 200,000 t. The emitter's own lock auto-releases when its pilot requests spool |
| **Combat** | Narrow Haul locks rip thrusters (no escape), antennas (blind) and turrets off. Opposed L/XL Haul locks tear hulls (two emitters, from two ships or from both ends of one capital). Pull a ship into a rock or a firing line, within the work ledger |
| **Docking assist** | Station emitters lock the visitor's collar or tow point with a wide cone and Hold assist, at low throttle. The anchor pays the reaction. Rotation alignment stays the pilot's job |
| **Rescue** | EVA characters (dynamic capsules) take at most 1 kN, and the beam stops adding speed past 10 m/s relative. A tractor can't fling or crush a person; it can carry one |

**Counterplay:**
- **Thrust:** a target out-thrusting the delivered force walks out of range (M against a 300 t ship with 9 MN of thrust: free in seconds). While it thrusts against the beam, its own thrust also loads its structure.
- **Cut the beam:** shoot the emitter. Below Integrity 0.5 the output is halved; at 0 the lock drops.
- **Interpose:** a body in the cone shadows the target in the raster and takes the force. Recessed components behind hull lips are naturally shielded, so building them in is a defence.
- **Turn or roll:** move the locked part out of the cone or out of line of sight. A lock with its point unhit for 60 ticks breaks.
- **`TractorDampener`** (anti-tractor field, a new core): subtracts up to `CancelN` per tick from the force delivered to its own ship, proportionally from every incoming beam. It burns `EmPerMNs` per MN cancelled plus `BaseMW`, so an EM war is symmetric. Sizes S/M/L cancel 0.5/5/25 MN, with base 1/5/15 MW. It also cancels friendly docking assist unless switched off.
- **Reinforce:** `ReinforcedMount`, frame spars, armour on necks.

## Interactions

- **Wormholes:**
  - **No projection through a mouth.** Rays crossing an Open mouth's disc are clipped and count as spill, so beams never reach through a wormhole.
  - **No beams while intangible.** Beams from or to bodies in `Warp` or `Throat` placement are refused.
  - **Graviton projection inside a tunnel → retaliation** (rule from the design brief; not yet written in wormholes.md, see Changes). A tractor beam is a graviton projection outside the hull.
    - Where wormholes.md defines "inside a tunnel", a beam whose emitter or target is inside it is cut that tick. The server raises a `GravitonProjection {mouth, emitterEntity, tick}` event to the wormhole's retaliation rule.
    - The lock breaks on crossing, so an honest pull-in never triggers it.
- **Warp:** Lock-mode hold as above; sweeps never block. A warping ship is intangible, so beams drop at T0.
- **Asteroid fracture:**
  - **A tractor never fractures rock.** Asteroids have no structure graph; tractors move them as rigid bodies, and opposed beams on a rock only cost EM.
  - Hot bodies (S > 0.3) can't be locked, and a locked body stops accepting beam heat (rings' rule).
  - Fracture stays the heat-and-wedge cycle. A second fracture path would bypass it and its abuse limits.
- **Station anchors:**
  - Beams on an anchored station move nothing; its anchor is debited, and a sustained push above `Σ HoldMaxN` counts as overload (exotic-matter).
  - The structural pass still runs: the anchor is the root, so modules far from it can be torn off, by one beam (no inertial relief).
  - Both of those make stations the easiest tractor target in the game, and an overloaded anchor drops the station. Every station is a protected zone (rings), so the protected-zone rule below stops other parties doing either unless the zone permits it.
  - Station emitters' reactions are debited to the anchor.
- **Protected zones (all tractor delivery, not just locks):**
  - A hit on another party's body delivers nothing (it is spill) if **either** the target body **or** the emitter is inside a protected zone, unless the zone permits it. This is tested per (beam, body) at each geometry update and covers Sweep as well as Lock: two narrow opposed sweeps from outside the zone can't tear a ship docked inside it, and a ship parked inside a zone can't tear ships outside it from safety.
  - Own and party bodies are always allowed.
  - Grid bubbles count as inside the zone their host is in, so hangar tractors follow the same rule for visitors' craft.
  - Rings' asteroid rules (lock refused, sweep delivers zero) stay as they are.
- **Frames and bubbles:**
  - **A lock needs emitter and target in one bubble.** Acquiring one merges two bubbles of the same frame (as a contact island). It is refused if the frames differ or the merged extent would pass the 4 km hard radius; XL range is 3 km for that reason.
  - **Locked pairs count as contact** for transfers and splits, so they never separate across bubbles.
  - **Sweeps act only inside the emitter's bubble.**
  - Tractored bodies stay realised and never coast (rings' rule).
- **Interior gravity:** tractor-driven acceleration is proper acceleration. Plating cancels it up to `a_max` and leaks the excess (exotic-matter), so a hard Haul on a light ship (~47 g for a 30 t fighter under an L beam, for the fraction of a second before the speed fade) throws the crew about.
- **Radiation:** reaction mode carries no dose (the reaction converts the AM); the emitter's AM trap breaches like any trap.

## Multiplayer

- **Server-authoritative:** beam geometry, forces, structural evaluation, breaks and detaches. Clients run the same functions only to predict their own ship and to forecast the gauge.
- **Wire:**

  | Message | When | Size |
  | --- | --- | --- |
  | `TractorCommand {emitter, mode, polarity, throttle, halfAngle, range, d*, pick(entity, part, local point int16 at 1/256 m), seq}` | client → server on change | ~28 B |
  | `TractorLock {emitter, target entity, part, local point, tick}` / `TractorUnlock {emitter, reason, tick}` | reliable, tick-stamped, interest of both ends | ~24 B / ~10 B |
  | `TractorState {emitter, mode, polarity, throttle q8, halfAngle q8, range q8, overdrive, pumped}` | reliable, on change | ~12 B |
  | `TractorStress {emitter, uMount q8, uMax q8, cutId}` | 5 Hz, to the emitter's owner and the target's crew | ~8 B |
  | Breaks | existing damage-brush messages (~34 B per tear brush, ≤ 32) plus `ComponentDetach {ship, component, tick, state}` (~40 B) | |

  The `GravitonProjection` event follows wormholes' event path.
- **Interest:** a tractored pair is full-rate for both ends (rings' "full rate while tractored" generalised).
- **Prediction:**
  - A client predicting its own ship (C12, re-stepping a local Jolt scene) adds the forces of beams touching it, computed from replicated beam state and the other end's replicated pose, which is full rate.
  - Errors come only from that pose's latency and the spring-damper, and normal reconcile handles them.
  - **Breaks and detaches are never predicted.** When the forecast exceeds 1 the client plays creak and warning effects, but the snap waits for the server event, so a mispredicted tear never has to be undone.
- **Permissions:** tractor force on another party's ship is attributed to the emitter's owner (as dose is). Protected zones refuse locks on, and zero any sweep delivery to, other parties' bodies when either end is inside the zone, unless the zone permits it (see Interactions); stations permit their own docking assist, server-clamped by enforced Safe-lock.

## Data

`data/components/tractors/*.json` (function cores):

| Class | Rated | Range max | θ | Base MW | Slew | Heat cap / cooling | Mount | Mass | Tier |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| S (mining, utility) | 200 kN | 150 m | 0.25–30° | 0.35 | 60°/s | 50 MJ / 0.2 MW | 450 kN | 1.5 t | T2 |
| M (tow, salvage) | 2 MN | 500 m | 0.10–25° | 1.5 | 30°/s | 300 MJ / 1.5 MW | 4.5 MN | 6 t | T2 |
| L (capital) | 20 MN | 2 km | 0.05–20° | 8 | 10°/s | 2 GJ / 12 MW | 45 MN | 40 t | T3 |
| XL (station, siege) | 100 MN | 3 km | 0.05–15° | 30 | 4°/s | 8 GJ / 50 MW | 225 MN | 200 t | T3, grid class Station or ≥ 20,000 t |

The S row keeps rings-and-asteroids' 200 kN, 150 m and 0.5 MW: 0.35 MW base + 0.15 MW for 150 m.

```json
{ "id": "tractor.m", "class": "M", "ratedN": 2.0e6, "rangeMaxM": 500, "halfAngleDeg": [0.1, 25],
  "baseMW": 1.5, "mwPerKm": 1.0, "slewDegS": 30, "minPurity": 0.60,
  "pumpedFrac": 0.25, "pumpMWPerMN": 100, "overdriveMul": 1.5, "overdriveMinPurity": 0.85, "overdriveMaxS": 10,
  "heatCapJ": 3.0e8, "coolW": 1.5e6, "heatWPerMN": 5.0e5, "pumpedHeatFrac": 0.2,
  "mount": { "tensionN": 4.5e6, "shearN": 4.5e6, "momentNm": 4.5e6 }, "massKg": 6000 }
```

- `data/components/towpoints.json`: S/M/L/XL ratings as in the mount table, with footprints of 2, 4, 8 and 16 m.
- `data/components/tractor-dampeners.json`.
- `data/ship-materials/*.json` gains `tensileMPa`, `compressiveMPa` and `shearMPa`.
- `data/physics/tractor.json`:
  `{ "emPerMNs": 1e-4, "beamVMax": 100, "eThrowJ": 5e8, "workResetS": 60, "warpHoldAccel": 0.5, "wakeAccel": 1e-3, "flipTicks": 30, "acquireTicks": 30, "losLossTicks": 60, "geometryHz": 10, "raster": 32, "maxCellWrites": 32768, "maxCandidates": 64, "candidateCellM": 64, "beamsPerOwnerPerBubble": 4, "characterMaxN": 1000, "characterVMax": 10, "thrownTagJ": 5e7 }`
- `data/physics/structure.json`:
  `{ "nodeSizeM": 4, "maxNodes": 4096, "evalHz": 20, "liveMinN": 1000, "liveLingerTicks": 60, "graphDropS": 10, "planeCuts": true, "buildRefuseOwnThrustU": 0.9, "breakU": 1.0, "instantBreakU": 1.5, "breakEvals": 2, "tearSlabM": 1.0, "tearBrushesPerBreak": 32, "cutBreaksPerShipPerS": 4, "mountBreaksPerEval": 4, "cutBreaksPerTick": 4, "safeLockU": 0.9 }`

## C# structures

```csharp
namespace BrutalGame.Ships.Structure;

public struct MaterialStrength { public float TensileMPa, CompressiveMPa, ShearMPa; }
public struct MountRating      { public float TensionN, ShearN, MomentNm; }

/// Baked with the hull (all peers, deterministic). Nodes in post-order, root last. SoA, zero alloc at runtime.
public sealed class StructureGraph
{
    public int Version;                       // ShipHull damage/edit version it was built from
    public int NodeCount; public float NodeSizeM;
    public NativeList<float3> Pos;            // CoM-relative node centroid
    public NativeList<float>  Mass;           // kg
    public NativeList<int>    Parent;         // -1 = root
    public NativeList<int>    SubtreeFirst;   // subtree(i) = [SubtreeFirst[i], i] in post-order
    public NativeList<uint>   ComponentId;    // 0 = hull node; else a leaf whose Cut is its mount
    public NativeList<CutCapacity> Cut;       // per non-root node: its fundamental cut
    public NativeList<int> AdjStart; public NativeList<StructEdge> Adj;   // CSR, for tear brushes
    public NativeList<byte> Layer0, Layer1, Layer2;   // per node: its 4 m layer on each ship-grid axis
    public NativeList<CutCapacity> PlaneCut;          // per axis, per layer boundary (≤ 3 × 128)
}

public struct CutCapacity { public float3 Centroid, Normal; public float TensionN, CompressionN, ShearN, MomentNm; }
public struct StructEdge  { public int To; public float AreaM2; public float3 FaceCentre; public sbyte FaceAxis; }

/// Per live ship, pooled scratch. Evaluate = one IJob per ship.
public struct StructuralEval
{
    public NativeArray<float3> ExtForce;      // per node, this tick (tractor hits + thrusters)
    public NativeArray<float3> SumF, SumM;    // post-order accumulators
    public float UMax; public int UMaxNode; public byte OverEvals;
}

namespace BrutalGame.Ships.Devices;

public enum TractorMode : byte { Off, Sweep, LockHold, LockHaul }
public enum TractorPolarity : sbyte { Pull = -1, Push = 1 }
public enum TractorDrive : byte { Reaction, Pumped, Overdrive }

public struct TractorTarget { public EntityId Body; public int Node; public uint ComponentId; public float3 LocalPoint; public float DesiredRange; }

public struct TractorState                    // one per emitter component; SoA arrays in TractorSystem
{
    public EntityId Ship; public uint ComponentId;
    public TractorMode Mode; public TractorPolarity Polarity; public TractorDrive Drive; public bool HoldAssist, SafeLock;
    public float Throttle, ThrottleCmd, HalfAngle, Range;
    public TractorTarget Target; public short AcquireTicks, FlipTicks, LosMissTicks;
    public float HeatJ, OverdriveS; public long EmittedNTicks;   // ledger accumulator (integer N·ticks)
}

public struct BeamHit { public int Node; public float Weight; public float3 LocalPoint; }
public unsafe struct BeamBodyHits { public BodyRef Body; public float Capture; public byte Count; public fixed byte Hits[16 * 20]; public int FullWeightsSlot; }  // ≤ 16 BeamHit; FullWeightsSlot: pooled (node, prev weight, new weight) list for live structural targets, −1 if none
public struct BeamWorkLedger { public EntityId Body; public long WorkKJ; public long LastTouchTick; }  // W_b, one per touched body

public sealed class TractorSystem             // server + client prediction; Sim assembly, no Vulkan
{
    public void UpdateGeometry(long tick);    // 10 Hz slots: cone cull, 32×32 raster, hits (IJob per beam)
    public void ApplyForces(long tick);       // every tick, step 5: controller, fades, AddForceAndTorque, reaction
    public void Ledger(long tick);            // 10 Hz: EM/AM/power/heat from accumulators
}
```

## Per-tick costs and caps

| Item | Estimate | Cap |
| --- | --- | --- |
| Beam geometry (10 Hz per beam, staggered) | ~2 µs + candidate-grid walk + ~10 ns per node in the cone: ship target 5–15 µs, rock ~2 µs | ≤ 16 beams per bubble (≤ 4 per owner), ≤ 128 per server; ≤ 64 candidates and ≤ 32k cell writes per beam |
| Forces (every tick) | ~0.5 µs per (beam, body) + one `AddForceAndTorque` (~0.3 µs) + the reaction | ≤ 8 bodies per sweep beam (largest capture first) |
| Structural pass (20 Hz per live ship) | ~33 ns per node with plane cuts: 1,000 nodes ≈ 33 µs; 4,096 ≈ 135 µs. It only reads this tick's forces and pose, so it runs as jobs alongside step 6 | ≤ 4,096 nodes per ship; ≤ 16 live ships per tick slot; server budget 0.25 ms per tick, overflow round-robin at a lower rate, never skipped |
| Graph build (lazy, worker) | ~0.25 ms per 1,000 nodes (MST + LCA sums + plane sums) | Only for live ships and client forecast targets; ≤ 1 per ship per 10 ticks; dropped 10 s after a ship stops being live |
| Break | ~10 µs for tear brushes + the existing split and bake costs | 1 cut + 4 mounts per ship per evaluation; 4 cuts per ship per s; 4 cuts per server tick |
| Ledger | a few ns per beam per tick + 10 Hz settle | |
| Client forecast | one structural evaluation at 10 Hz for the locked target | |
| Ships not under tractor load | **0** (no graph, no evaluation, no rebuild on damage) | |

- **Typical single player** (2 beams, 1–2 live ships of ~1,000 nodes): about 30–55 µs per tick.
- **Server battle** (64 beams, 16 live ships): about 0.45–0.7 ms of CPU per tick, plus lazy graph builds as ships turn live (~0.75 ms on a worker for a 3,000-node ship, once). Geometry and structural passes are independent jobs per beam and per ship; force application runs on the main thread before the step.
- **Tick order (frames-and-zones):**
  - step 5 (forces): geometry for this tick's slots → controller and forces → structural evaluation for this tick's slots;
  - next tick's step 3: breaks and detaches → split → graph rebuild queued;
  - 10 Hz slot: ledger.
- **Profiler rows:** `Tractor.Geometry`, `Tractor.Forces`, `Tractor.Ledger`, `Structure.Eval`, `Structure.Break`, `Structure.Build`. The ImGui Tractor panel shows per beam: emitted, capture, delivered, spill, reaction, EM/AM/power rate, heat; and per live ship: nodes, `u_max`, worst cut and eval µs.

## Files

- **New `src/Ships/Structure/`:**
  - `StructureGraph.cs`, `StructureBake.cs` (nodes from 4 m occupancy cells, joints, Kruskal MST, LCA cut sums);
  - `StructuralEval.cs` (the O(N) pass, `IJob`), `StructureBreak.cs` (tear brushes, detach requests);
  - `MaterialStrength.cs`, `MountRating.cs`.
- **New `src/Ships/Devices/`:**
  - `TractorDef.cs`, `TractorState.cs`, `TractorSystem.cs`;
  - `BeamRaster.cs` (angle-space z-buffer);
  - `TractorRules.cs` (abuse limits, protected zones, warp hold, wormhole clip, bubble rules);
  - `TractorDampener.cs`, `TowPoint.cs`.
- **New `src/Ships.Runtime/Tractors/`:**
  - `TractorTargeting.cs` (pick, tow-point snap, forecast, Safe-lock), `TractorHud.cs`;
  - `TractorBeamRenderer.cs` (cone VFX from replicated state, no extra bandwidth);
  - `StressView.cs` (build tool).
- **New other files:**
  - `src/Net/TractorEvents.cs`;
  - `src/Debug/TractorPanel.cs`;
  - `tests/Ships.Tests/StructureGraphTests.cs`: Σf = 0; dumbbell, ring, T and plate cut capacities against brute-force min cuts; MST + plane cuts against Gomory–Hu on random hulls; a uniform solid box under opposed nose/tail loads must report its mid cross-section (the comb-tree case); LCA centroid sums against brute force; identical graphs from shuffled input order; antenna rip under Haul and under Hold; opposed-beam tear; single-beam no-tear with ramp and fade.
  - `tests/Ships.Tests/TractorTests.cs`: capture fraction against analytic discs; linear and angular momentum conservation, including with a dampener on the target; `BeamVMax` cap; `W_b` cap under throw–catch-up–re-push; steady tow and opposed beams leave `W_b` near 0; warp hold thresholds and hold charge; occlusion by an interposer; own-body hits are spill; protected-zone zeroing for sweeps with the target inside and with the emitter inside.
- **Change:**
  - `src/Physics/JoltRuntime` export probes for `BodyInterface_AddForceAndTorque` and `BodyInterface_AddForce` at a point.
  - `PhysicsScene` gains `AddForceAndTorque(BodyRef, F, τ, activate)`.
  - `GameApp` adds `--tractor-bench` (N beams × M live ships, logs µs).

## Changes to other docs

- **exotic-matter.md:**
  - The tractor row bills on **emitted** output (equal to delivered for a fully captured lock), with power = base + 1 MW per km of set range.
  - Tractors are the one device allowed a **pumped** mode (bus power instead of AM, ≤ 25 % of rating, 100 MW per MN), which adds an exception to "every gravity device runs on the reaction".
  - Overdrive at q ≥ 0.85.
  - A warp-blocking lock pays at least `m_target · 0.5 m/s²` of emitted output.
  - Station anchors: tractor overload of `Σ HoldMaxN` by other parties is only reachable where a protected zone permits it.
  - Classes S–XL.
  - The new `TractorDampener` row (same `EmPerMNs` per MN cancelled).
  - Tractors are bus class 2.
- **rings-and-asteroids.md:**
  - The `TractorBeam` row points here. S keeps 200 kN, 150 m and 0.5 MW.
  - "A lock is a spring-damper" now means per-hit, with despin emerging from it.
  - Add `BeamVMax`, the per-body work ledger `W_b ≤ EThrow = E_cap`, the `Thrown` tag and the zero sweep in protected zones.
  - Abuse limits gain: a `Thrown` body's contact damage to other parties' grids inside a protected zone is zeroed and logged.
  - A lock has Hold (the existing spring-damper) and Haul (constant force) controllers; the S mining tractor defaults to Hold.
  - "Tractors never fracture rock" becomes an abuse-limit line.
  - Vacuum mode is a wide pull sweep.
- **ship-hulls.md:**
  - `StructureGraph` is built lazily, for ships under tractor load (server) and forecast targets (client), not in every bake.
  - The build tool refuses blueprints whose own full thrust gives `u ≥ 0.9` anywhere.
  - `ComponentDesign` gains `MountRating`.
  - Components also detach when their mount fails.
  - Ship materials gain strengths.
  - New `TowPoint`, `ReinforcedMount`, `TractorDampener` and tractor cores.
  - The split path accepts tear brushes.
  - Build-tool Stress view.
  - The limits table adds ≤ 4,096 structure nodes per ship.
- **frames-and-zones.md:**
  - The force pass (step 5) gains tractors and the structural evaluation.
  - A lock is a contact for island transfers, merges and splits; acquiring a lock may merge bubbles of one frame.
  - The activation rule (`WakeAccel`).
  - New profiler rows.
- **warp.md:** a tractor lock (Hold or Haul) blocks spool only while `F_avail ≥ m_target · 0.5 m/s²`, and sweeps never block. The emitter's own lock auto-releases on a spool request.
- **wormholes.md:**
  - Beams are clipped at mouth discs and refused for `Warp`/`Throat` placements.
  - "Limited by range and by the target's mass and thrust" now points here.
  - **Add the graviton-projection retaliation rule** from the design brief: what "inside a tunnel" is, and what retaliation does. This doc only raises the `GravitonProjection` event.
- **README.md:** drop "(Pending.)" from the tractor-beams row and add questions 1 and 4 below to the collected list.

## Rejected

- **Spring-network relaxation** (nodes as masses, joints as springs, a few Jacobi iterations per tick):
  - it needs 10+ iterations to converge on stiff networks, or it under-reports long load paths;
  - it oscillates unless damped and sub-stepped;
  - it costs about 10–20× the cut pass for no better answer to "which cut fails".
- **FEM or a per-joint stress solve:** statically indeterminate, so it needs a sparse solve per tick. That is unaffordable and pointless at 4 m resolution.
- **Gomory–Hu cut tree** (all-pairs min cuts): N−1 max-flows per bake. It is kept only as the offline test oracle; the MST cuts with exact capacities are shipped unless tests show missed necks.
- **Force at the body's centre of mass only:** no torque, no wrong-component failure, no tearing.
- **Billing on delivered output only:** wide sweeps would be free area control.
- **Running the stress pass on every ship, or on thrust and rams:** it would cost on every tick for nothing, and rams and pulses already have damage models.

## Open questions for the user

1. **Pumped mode:** you said tractors use "potentially antimatter". Is a 25 %-of-rating, 100 MW per MN bus-powered mode without AM what you meant, or should AM be strictly required (like every other gravity device), or strictly optional at full strength?
2. **Tunnel retaliation:** wormholes.md has no tunnel interior and no retaliation rule yet. Where is "inside a tunnel" (the throat hold, the mouth shell, a volume behind the mouth), and what does retaliation do (a pulse on the emitter, collapse, a flush kick)?
3. **Tractors on players' ships outside protected zones:** always allowed, as proposed (attribution only), or gated by a PvP or faction rule?
4. **Station sieges:** the XL row says "station, siege", but every station is a protected zone, and this doc zeroes other parties' tractor delivery there (one beam can tear a module off a pinned station, and overload can drop it). Should some stations (unclaimed, at war, or below a tier) be siegeable, and by what rule?

## Review notes

Lead review, 2026-11-25, against README.md, ship-hulls.md, frames-and-zones.md, exotic-matter.md, rings-and-asteroids.md, warp.md and wormholes.md. The fixes are already folded into the sections above. This lists what changed and why.

**User decisions: faithful, with two fixes needed to make 2 and 3 actually happen.**
- Decision 1 (push or pull cone, adjustable FOV, EM + energy + optional AM): faithful. The top-hat over solid angle makes narrow strong and wide weak, and the footprint table checks out (e.g. M at 0.1°, 500 m: 0.87 m radius). Pumped mode is this doc's reading of "potentially antimatter" and stays open question 1.
- Decision 2 (wrong component): the mount-as-leaf-cut model is right. But the only lock controller was a spring scaled by target mass, and Safe-lock was on by default, so the outcome depended on settings the doc didn't spell out. Fixed: Haul (constant force) added; worked cases show the antenna rips under Haul and also under Hold on a heavy ship (the spring's `k` uses the ship's mass); Safe-lock is now described as a fallible forecast, and a Safe-locked antenna is still never a working tow.
- Decision 3 (opposing beams tear): **was not delivered by the original model in two ways.** (a) The Hold spring on a 30 t fighter is 7.5 kN/m, so two opposing locks could never reach the 20 MN the worked case claimed. Haul fixes that. (b) MST fundamental cuts are exact for necks but not guaranteed to include a uniform hull's cross-sections: with equal-strength joints, Kruskal's tie order can produce a comb tree whose cuts are long strips, and then opposed nose and tail loads find no cut to tear. Axis plane cuts were added (≈ +30 % on the pass), with a uniform-box test.

**Physics.**
- The LCA rule was wrong for the centroid and gyration sums: they need the unoriented `+u +v −2·lca` rule, not `+u −v`. Fixed and tested against brute force.
- The 16-hit summary lumped the "rest" of a wide sweep onto one node, which would make a false hot spot and a false tear. The structural pass now uses the full node-weight list for live targets.
- Hits switching at 10 Hz were step loads, contradicting the quasi-static claim. Weights now blend over the 6 ticks.
- The worked case "one L beam tears a 30 t fighter" was wrong once the 0.5 s ramp and the 100 m/s fade are applied: it peaks at `u ≈ 0.87`. Corrected. Opposition is the way to tear, which matches decision 3.
- Lateral (damping) forces made a non-collinear pair, so angular momentum wasn't conserved and a held target could spin the emitter up. The emitter now gets the couple.
- The structural pass running on every bake and rebuilding on every damage event would have cost about 1 ms per tick in a battle for ships nobody tractors. Graph builds are now lazy.
- A ship's own thrust is in `F_ext` while it is live, so a weak design could tear itself the moment a 1 kN nudge made it live. The build tool now refuses designs whose own thrust gives `u ≥ 0.9`.

**Exploits closed.**
- *Repeated throwing:* the emitter-relative `½mv²` cap reset whenever the emitter caught up with the rock, so throw, match speed and re-push gave unbounded speed. Replaced by the per-body signed work ledger `W_b`, shared by all beams.
- *Tearing inside protected zones:* only locks were refused, so two narrow opposed **sweeps** from outside could tear a docked ship, and an emitter parked inside a zone could tear ships outside from safety. Delivery to other parties is now zeroed per hit, for both modes, when either end is in a zone. Grid bubbles inherit the zone.
- *Tractor-tearing stations:* a pinned station has no inertial relief, so one L beam tears a module, and overload can drop the anchor. Now gated by zones; docking assist is server-clamped by enforced Safe-lock, which also stops forced undocking at the collar. Station sieges are open question 4.
- *Perpetual motion and reactionless drives:* push and pull pairs already cancel. The real hole was the dampener: cancelled force still produced a reaction on the emitter. The reaction is now taken after every clamp, and cancelled force is spill. Own-body hits are spill, so a ship can't push or torque itself. Grapples keep the speed fade and pay 100× an anchor's EM rate.
- *Cheap tackle:* a Hold at its set range emits almost nothing yet blocked warp. A blocking lock now pays for `m · 0.5 m/s²`.
- *Slot denial and stacking:* at most 4 beams per owner per bubble.
- *Thrown rocks into zones:* the `Thrown` tag persists, and its contact damage to other parties inside a zone is zeroed and logged (proposed rings line).

**Perf and determinism.**
- The candidate search was a linear scan of the bubble (~40 µs per beam at 2,000 bodies). It now walks a 64 m entity-sphere grid, capped at 64 candidates.
- No unrelated body wakes: the `WakeAccel` gate stays, sweeps hit ≤ 8 bodies, and protected-zone zeroing happens before activation.
- The graph bake now quantizes capacities and breaks Kruskal ties by edge index, so all peers produce identical trees.
- Server breaks stay authoritative and are never predicted.
- Revised budget: 30–55 µs per tick in single player, 0.45–0.7 ms in a 64-beam battle. Still estimates; `--tractor-bench` must measure them.

**Consistency with other docs.**
- exotic-matter bills tractors on "delivered" output; this doc changes that to emitted and lists it.
- warp.md's "a tractor beam" lock now has a threshold; listed.
- wormholes.md has no tunnel interior or retaliation rule yet (confirmed by search), so the `GravitonProjection` event stays a hook, and that is open question 2.
- The rings S row (200 kN, 150 m, 0.5 MW), `E_cap`, `ProtectRadius`, `KinematicMassLimit`, the hot-body refusal and the 6-a-minute rate limit all match.
- frames-and-zones tick steps 3 and 5, the 4 km hard radius and island-transfer rules match.

**Still unverified:** every cost figure, the material strengths against real blueprints (is 8 MN right for a fighter's mid-section?), whether plane cuts plus MST miss real oblique failure modes (Gomory–Hu oracle test), and how Hold assist's polarity flips feel in play (it may limit-cycle).
