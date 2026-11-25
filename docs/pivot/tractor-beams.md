# Tractor beams and hull stress

Status: design only. Nothing has been built or run, and no repo files were changed. Written 2026-11-25 against README.md, ship-hulls.md, frames-and-zones.md, exotic-matter.md, rings-and-asteroids.md, warp.md and wormholes.md. Every number is a knob and every cost is an estimate to measure (`--tractor-bench`, profiler rows below). Where this doc changes another doc, the change is listed under "Changes to other docs". decisions.md wins over everything here, then review.md's rulings where they apply. Lead-reviewed the same day; see "Review notes". Revision 2 (2026-11-25) follows rings-and-asteroids.md revision 4: station defence with tractors, the withdrawn `Thrown` zone line, and the asteroid energy cap `max(W_b, E_rail)` from the rings revision 3 review; see "Revision 2 notes". Revision 2 was lead-reviewed with rings revision 4 and corrected in place ("Revision 2 review notes"). The r6 consistency pass applies user decisions D26 (consent), D27 (sieges and capture), D28 (no AM means pull only) and D33 (miner liability), and the shared `IncidentRecord`; see "Consistency pass (r6) notes", at the end.

## Recommendation in one paragraph

A tractor beam is a ship component that projects a **cone** from an apex on its housing. The pilot sets polarity (**push or pull**), throttle, range and the cone's **half-angle** (wide = weak, area; narrow = strong, focused). Output is spread evenly over the cone's solid angle, so a body receives the share of the output its exposed area covers, found by a small z-buffered raster of the bodies' **structure nodes** in angle space. Force is applied at the nodes it lands on, so an off-centre lock makes torque. A lock either **Holds** (a gentle spring to a set range, for towing) or **Hauls** (constant full force, for ripping and tearing). The beam burns field-grade EM content per MN·s **emitted**, plus bus power that grows with range. To **push** it runs on the reaction and also burns AM at the fixed reaction ratio. Without AM it can only emit gravitons, so it can only **pull** (D28), at full rating, paying bus power of 1 W per N emitted instead of AM. Any lock or sweep delivery to another party's ship, grid or character without its consent is a **weapon use** (D26). Every newton delivered (after every clamp) pushes back on the emitter's ship. Hulls get a **structure graph**, built lazily when a ship first comes under tractor load: 4 m nodes and components, joined by joints whose strength is the contact area × the material's strength, and by mounts with a rated load. A maximum spanning tree of that graph gives N−1 **cuts** (exact for necks and mounts), plus one axis-aligned **plane cut** per 4 m layer (so plain hulls have cross-sections to tear along), all with exact summed capacities. Each tick a ship is under tractor load, a single O(N) pass sums external and inertial forces up the tree. That gives the exact net force and moment across every cut. A cut or mount over its rating breaks through the existing damage and split path. A lock on an antenna puts nearly the whole beam through a 20 kN mount, so the antenna comes off. Two opposing beams have almost no net force but put their full tension through the cuts between them, so the ship tears.

## User decisions this doc implements

- Tractor beams use exotic matter, energy and potentially antimatter to **push or pull a cone with an adjustable FOV** (wide = weak, area; narrow = strong, focused).
- **Locking onto the wrong component** can put too much force on it: grabbing a ship by a thruster or an antenna rips the part off instead of moving the ship.
- **A powerful set of opposing tractor beams can tear a ship apart.**
- **D26:** tractor beams count as a **weapon** unless requested. A station may tractor you in to dock if you ask (a consent flag); otherwise you dock your own ship. This doc owns the consent rule (Multiplayer, Permissions).
- **D27:** station **sieges** are allowed, and factions prefer to **capture** stations rather than destroy them (Interactions, Protected zones, Sieges).
- **D28:** without antimatter a tractor can only emit gravitons, so it can only **pull**; push needs anti-gravitons, so AM. This replaces the earlier 25 % pumped mode.
- **D33 (inherited, rings-and-asteroids.md):** fragments from legal mining near a station are always the miner's liability, including debris a station's defence beams push back out.
- Inherited: tractors are gravity devices, pulling on graviton emission and pushing on the reaction (exotic-matter.md), they act through the per-body force pass (wormholes.md), a lock blocks warp spool (warp.md), a lock breaks on a wormhole crossing (wormholes.md), and asteroid locks follow the abuse limits (rings-and-asteroids.md).
- Inherited (revision 2, from rings-and-asteroids.md revision 4): debris that drifts into a protected zone stays real, and **stations push it back out with tractor beams** (or mechanical arms), as far as their installed hardware allows. Damage traced to a shatter or a thrown rock is a hostile act of its owner.

## Beam model

### Geometry

| Quantity | Meaning |
| --- | --- |
| Apex `o` | Fixed point on the emitter housing (data), ship-local |
| Axis `â` | Turret direction. In Lock mode it tracks the lock point at `SlewDegS`; in Sweep mode the pilot aims it |
| Half-angle `θ` | Pilot setting in `[θmin, θmax]` of the class. Footprint radius at depth z is `z·tanθ` |
| Range `R` | Pilot setting `≤ RangeMax`. Power grows with it |
| Polarity `s` | +1 push, −1 pull. A flip costs `FlipTicks` (30, 0.5 s) at zero output. Push and flips need AM (D28): a `Graviton` drive is Pull only |
| Throttle `τ` | 0–1. Slews at most 2 per second (0 → 100 % in 0.5 s), so loads ramp and the quasi-static stress model stays valid |

**Output.** `F_out = τ · F_rated · k_mode · k_int`:
- `k_mode`: 1 for the `Graviton` drive (no AM, Pull only, D28) and the `Reaction` drive, `OverdriveMul` (1.5) in `Overdrive` (see Consumption);
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

- **Polarity clamp:** in Pull, the radial part may only point toward the apex; in Push, only away. **Hold assist** (an option) flips polarity automatically when the spring needs the other sign, with the 0.5 s flip gap. That gives a soft hold, not a rigid one. Hold assist needs AM, since it pushes (D28).
- **Without AM (D28):** Push polarity, flips and Hold assist are refused. Hold becomes a **pull-only tether**: it limits range and reels in, but cannot stop a target closing on the emitter (the spring's push half is clamped to zero). A pull-only Hold still blocks warp spool under the same `m_target · 0.5 m/s²` rule.
- **Lateral authority is `tanθ`:** a needle-thin lock is a rope. The target swings and the turret tracks it. A wider lock steers and damps sideways motion but delivers less. Docking assist uses a wide lock at short range.
- **Despin emerges** from per-point radial damping: different hit points have different radial velocities on a tumbling body. Spin about the beam axis is not damped by a narrow beam. Rings-and-asteroids' "a lock despins fragments" holds for any spin with a component across the beam.

**Speed and work fade `g_i`** (abuse limit, applies to every target):
```
v_b    = relative speed of the body along the beam direction (away for push, toward the apex for pull), relative to the emitter
W_b    = the body's beam-work ledger: W_b ← max(0, W_b + Σ_beams F_b · (v_body − v_emitter) · dt), int64 kJ, per body
E_rail = ½ m |v_body − v_rail|²            asteroid bodies only: kinetic energy relative to the local lane point velocity (the field frame in fields)
W_eff  = W_b                                 every other body
       = max(W_b, E_rail)                    asteroid bodies (revision 2, rings revision 3 review)
g = clamp((BeamVMax − v_b) / (0.2 BeamVMax), 0, 1) · e_i
e_i = clamp((EThrow − W_eff) / (0.2 EThrow), 0, 1),  but e_i = 1 for a hit whose force brakes: relative to the emitter for W_b (F_i · (v_i − v_emitter) ≤ 0), and for an asteroid also relative to the lane (F_i · (v_i − v_rail) ≤ 0)   (review: braking is never faded for any body)
BeamVMax = 100 m/s,  EThrow = 500 MJ (= rings' E_cap)
```
- A beam can't add speed past 100 m/s relative to its emitter. A 1e6 kg rock tops out at 32 m/s, a 1e4 kg chunk at 100 m/s.
- **Why a ledger and not `½ m v_b²`:** an emitter-relative kinetic energy resets whenever the emitter catches up. Throw, thrust to match the rock, throw again, and the rock reaches any speed. `W_b` is signed net work summed over every beam on the body, so catching up and re-pushing keeps adding to it, and several emitters share one budget. Braking a body (negative work) refunds it. A steady tow does near-zero net work (tug and tow move together), and opposed tearing beams do none, so neither is limited by it.
- **Rail-relative cap for asteroids (revision 2, the rings revision 3 review's fix):** `W_b` is measured in the emitter's frame, so a steady tow does ~0 work. A tug could hold a rock, accelerate itself to 200 m/s and let go: a 1e6 kg rock carrying 20 GJ with `W_b` ≈ 0, untagged. For asteroid bodies the fade therefore uses `max(W_b, E_rail)`, with `E_rail` relative to the local lane point velocity (the field frame in fields). A beam force that would raise `E_rail` past `EThrow` is faded to zero (braking is never faded), so a tow can't carry a rock past 500 MJ relative to its lane: the rock lags, and the lock stretches and breaks at range. The `Thrown` threshold (50 MJ, below) uses the same quantity. Cost: one dot product per (beam, asteroid body). Ships and other bodies keep `W_b` alone: they have no rail to measure against, and towing a ship is the point of a tug.
- `W_b` resets to 0 only when the body is back within `0.2 · BeamVMax` of its reference velocity (for asteroids the lane point velocity, spin excluded, as rings revision 3 states; the bubble frame otherwise) and no beam has touched it for 60 s. It is stored only for bodies a beam has touched (one int64), dropped on reset. `E_rail` is not stored: it is computed from the body's state when the force is.
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

Rate limit: at most 1 lock acquisition per second per emitter. At most 4 active beams per owner per bubble (inside the 16 per bubble), so one player can't take every beam slot in a bubble, and stacking many S emitters is capped. **Exception (revision 2, review):** an anchored station's defence controller (Uses, Station defence) may run up to 8 defence beams in **reserved slots** outside the bubble's 16 and the 4-per-owner cap, so visitors filling the bubble's slots can't switch a station's defence off. They act only on asteroid debris (Dynamic or Coasting bodies) and rubble clouds inside the station's own zone, and deliver nothing to ships, grids or characters in their cones.

### Consumption, power and heat

Units follow exotic-matter.md: 1 EM = 1 kg of content, `AmPerEm = 0.005`.

```
EM content:  ċ_EM = EmPerMNs · F_out          EmPerMNs = 0.1 g per MN·s, at q ≥ 0.60 (field grade or purer), every drive
AM:          ṁ_AM = AmPerEm · ċ_EM             Reaction and Overdrive only (0.5 mg per MN·s)
Bus power:   P = BaseMW + 1 MW per km of R     always
             + GravitonWPerN · F_out           Graviton drive only (1 W per N, i.e. 1 MW per MN)
Heat:        Q = HeatWPerMN · F_out            Reaction (0.5 MW per MN, ×3 in Overdrive)
             Q = GravitonHeatFrac · GravitonWPerN · F_out   Graviton (0.2 × 1 W per N = 0.2 MW per MN)
```

- **Billed on emitted output (`F_emit`, see Lock mode), not on delivered output.** A focused lock delivers what it emits, so exotic-matter's 0.1 g per MN·s figure is unchanged for it. A wide sweep pays for its spill. That is the price of area. In the formulas below `F_out` means `F_emit`.
- **Three drives (`TractorDrive`, D28):**
  - **Graviton** (EM + bus power, no AM): graviton emission only, so polarity is **Pull only**, at full rating. Push, flips and Hold assist are refused; Hold is a pull-only tether (Lock mode). Any emitter can pull this way, so a ship with AM can still tow without spending it. An S beam at full is 200 kN for +0.2 MW, an M beam 2 MN for +2 MW, an L beam 20 MN for +20 MW (cores), an XL beam 100 MN for +100 MW.
  - **Reaction** (EM + AM): full rating, **Pull or Push**. Push, flips and Hold assist need it.
  - **Overdrive** (EM + AM, purity boost): `OverdriveMul` 1.5 × rating for at most 10 s, recharging at 1 s per 6 s. It needs feed with nominal q ≥ 0.85 (throat grade) in the emitter's tank, and triples heat per MN. Throat-grade stock buys a burst, not a better rate.
- **Devices debit true content** (exotic-matter's unassayed-stock rule). Unassayed stock runs out early; it has no other effect on a tractor.
- **Starvation:** if EM, AM (Reaction, Overdrive) or bus power runs short, output ramps to 0 over 0.5 s. An AM-starved beam in Pull falls back to the Graviton drive (if the bus can carry it); in Push it ramps to 0. Tractors are bus class 2 (flight and sensors).
- **Heat:** each emitter has its own heat store `HeatCapJ` and cooling `CoolW` (more with a `Coolant` socket once a ship thermal system exists). When the store is full, the throttle is clamped to the cooling-limited steady state. Settled in the 10 Hz ledger.
- **Accounting:** each tick adds `F_emit` (integer N) to an int64 accumulator per emitter, a few ns. The 10 Hz ledger debits content, AM and heat from the accumulators, which keeps it deterministic.

**Worked rates:**

| Case | Emitted | EM content | AM | Power |
| --- | --- | --- | --- | --- |
| S mining lock on a 2 m chunk, full | 200 kN | 72 g/h | 0.36 g/h | 0.5 MW |
| M towing a 300 t ship at 0.5 m/s² (Reaction) | 150 kN | 54 g/h | 0.27 g/h | 2.0 MW |
| M towing a 300 t ship at 0.5 m/s², pull (Graviton, no AM) | 150 kN | 54 g/h | 0 | ~2.15 MW |
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
- **Settings on the HUD:** mode (Sweep, Lock-Hold, Lock-Haul), drive (Graviton, Reaction, Overdrive), polarity, throttle, half-angle (mouse wheel), range, desired range `d*` (reel in/out), Hold assist. Without AM, Push and Hold assist are greyed out (D28). A lock on another party's body without consent shows a "HOSTILE ACT" warning before acquire (D26).
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
  - **Server-enforced Safe-lock:** wherever a protected zone permits a lock on another party's body (docking assist, run only on the visitor's request, D26), the server clamps it to `u_max ≤ 0.9` with its own evaluation, and the pilot can't turn it off. A station's docking assist can't be used to tear visitors or force-undock them at the collar.
- **Victim side:** crew of a tractored ship get a hull-stress gauge from the server (5 Hz) and the emitter's position.
- **Skill = hardpoints:**
  - The **`TowPoint`** component is a lock target with a high mount rating and a large footprint (4 × 4 m for M).
  - Back it with `hull.frame` and the load goes into the keel, not a panel.
  - Lock narrow on a tow point to move a ship hard; open the cone to move a ship that has no tow points gently.
- **Build tool Stress view:** applies each tow point's rated load and full own thrust as test loads and colours every cut by `u`. It also warns when an emitter's reaction or a tow point's rating exceeds the weakest cut behind it.

## Uses

| Use | How |
| --- | --- |
| **Towing** | Lock on the tow point; Hold assist keeps the range (AM; without AM Hold is a pull-only tether and the tow brakes with its own thrusters, D28). A tow is a rope with `tanθ` of lateral authority. Tug and tow form one contact island for bubble transfers. Another party's ship is towed **only on request** (a tow request carrying consent, D26); otherwise it is a hostile act |
| **Salvage** | Wrecks and debris pieces (split bodies) are ordinary dynamic bodies. Rip the loose parts off on purpose: lock narrow on a component and pull |
| **Cargo** | Loose pods and containers by lock; hangar tractors inside a grid bubble park small craft and crates (beams never cross a hull between grid and exterior scenes). Hangar tractors on visitors' craft run on the landing request (D26 consent) |
| **Mining** | Rings' S tractor: chunks to the intake by lock. Rings' "×2 tractor vacuum mode" is a wide pull sweep centred on the intake mouth. Cloud mass is not a body; the ×2 stays a flat multiplier on intake sweep |
| **Asteroid manipulation and throwing** | Lock a cold rock, accelerate it, release: it keeps its velocity. Capped by `BeamVMax` and the per-body ledger, `max(W_b, E_rail) ≤ EThrow` for asteroid bodies (500 MJ = rings' `E_cap`; revision 2), shared by every beam, not reset by catching up and re-pushing, and not beaten by tow-and-release. Lock refused within `ProtectRadius` of protected zones (except on debris, Dynamic or Coasting bodies, by the zone's own station-defence emitters and parties it permits; revision 2, review: never on rail, static or kinematic rocks, which a lock would free) and on bodies with S > 0.3; sweep delivers zero to asteroid bodies in both cases, with the same exception. Every beam-touched rock carries `LastBeamOwner` (rings), so its contact damage is attributed even below the tag. A released body with `max(W_b, E_rail)` above 50 MJ is tagged `Thrown` with the emitter's owner, and counts against the owner's rings rate limit (6 events a minute). The tag does not time out while the body's release path still leads into a protected zone (rings revision 4 review). A `Thrown` body that later enters a protected zone keeps the tag and **stays real** (revision 2, withdrawing the old proposed zeroing line): the station's shield absorbs what it can, its defence beams push the rock out, and any damage, to the hull or the shield, is an `IncidentRecord` (class Hostile, kind `Thrown`) of the thrower (rings revision 4) |
| **Station defence (revision 2)** | An anchored station's `StationDefence` controller (rings-and-asteroids.md, Station defence) assigns up to 8 of the station's own emitters to debris inside its protected zone. **AM-fed emitters only (D28):** deflecting or braking an inbound body needs Push, so the controller assigns only emitters whose traps hold AM; an emitter without AM is never assigned to an inbound body (a pull would draw it in), and the public defence HUD ("N deflectors") counts only AM-fed emitters. Arms and shields are unaffected. The defence uses a narrow **Sweep** (push) on light bodies with time to spare, a **Hold** lock reeling out on heavy ones, braking first when needed (braking is never faded). Mechanical `DebrisArm`s take the last 40 m. A higher-priority threat (a `Thrown` rock first) preempts an emitter whose body is already on a missing path. Release once the body's path misses the shield shell by ≥ 100 m over one lane period (a radial push in a Hill frame comes back after an orbit, so the controller prefers along-track pushes, which drift away), moving outward at `clamp(sqrt(2·DefenceReleaseJ/m), 0.2, 5)` m/s relative to the station (`DefenceReleaseJ` 10 MJ; review fix: the flat 2–5 m/s gave a 1e8 kg boulder up to 1.25 GJ). The controller picks the push direction for clearance from every entity and the approach lanes; crew can't aim it. Reactions are debited to the anchor. Defence beams never set `LastBeamOwner`, so pushed-out debris stays attributed to whoever made it; fragments of legal mining stay the miner's liability even after the station redirects them (D33, rings-and-asteroids.md). Capacity is the hardware: one body per emitter at a time, ≤ 1 acquisition per second, class range and slew, the station's EM, AM and power, in up to 8 reserved beam slots. What it can't serve meets the shield alone; an outpost with no emitters has no deflection |
| **Pulling ships into wormholes** | **Always a hostile act** (D26, `TractorLock`). Lock the victim and drag it through the mouth. The lock breaks on its crossing (wormholes.md) and the emitter stays behind unless it follows. Pairs with interdiction catchers: interdiction moves the far end, the tractor provides the force |
| **Anti-escape** | **Always a hostile act** (D26, `TractorLock`). A lock (a pull-only tether without AM is enough) with `F_avail ≥ m_target · 0.5 m/s²` blocks the target's warp spool: S holds ≤ 400 t, M ≤ 4,000 t, L ≤ 40,000 t, XL ≤ 200,000 t. The emitter's own lock auto-releases when its pilot requests spool |
| **Combat** | **Always a hostile act** (D26, `TractorLock`, plus `TractorTear` for breaks). Narrow Haul locks rip thrusters (no escape), antennas (blind) and turrets off. Opposed L/XL Haul locks tear hulls (two emitters, from two ships or from both ends of one capital). Pull a ship into a rock or a firing line, within the work ledger |
| **Docking assist** | **Only on the visitor's request** (a docking request carrying consent, D26), with server-enforced Safe-lock. Station emitters lock the visitor's collar or tow point with a wide cone and Hold assist, at low throttle. The anchor pays the reaction. Rotation alignment stays the pilot's job. Without AM (D28) it is a pull-only tether: the visitor brakes with its own thrusters |
| **Rescue** | EVA characters (dynamic capsules) take at most 1 kN, and the beam stops adding speed past 10 m/s relative. A tractor can't fling or crush a person; it can carry one. Another party's character is carried only on request (D26); otherwise it is a hostile act |

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
  - **Tunnel retaliation (owned by wormholes.md, "Tunnels", Retaliation).** A tractor beam is a graviton projection outside the hull.
    - An active tractor whose emitter or target is in a tunnel frame is struck on its first tick, then every 60 ticks while it lasts, with damage escalating per owner group. The emitter is disabled for 10 s, so the lock breaks. Only the projecting component and its own grid are damaged.
    - Consent does not matter: a requested tractor is struck too (D26 never exempts a tunnel strike).
    - This doc raises no separate event: `TractorSystem` calls wormholes' strike path, announced as `TunnelDischarge`.
    - The lock breaks on crossing, so an honest pull-in never triggers it.
- **Warp:** Lock-mode hold as above; sweeps never block. A warping ship is intangible, so beams drop at T0.
- **Asteroid fracture:**
  - **A tractor never fractures rock.** Asteroids have no structure graph; tractors move them as rigid bodies, and opposed beams on a rock only cost EM.
  - Hot bodies (S > 0.3) can't be locked, and a locked body stops accepting beam heat (rings' rule).
  - Fracture stays the heat-and-wedge cycle. A second fracture path would bypass it and its abuse limits.
- **Rubble clouds (revision 2):** a `RubbleCloud` (rings) is not a Jolt body. A Sweep treats it as one node at its centre with the cloud's radius; the delivered force changes the cloud's velocity by `F · dt / M` (~10 ns), with no reaction beyond the normal one on the emitter. `BeamVMax` applies to the cloud's speed relative to the emitter; clouds have no work ledger (grit does no hull damage, so a pushed cloud is no weapon). Locks can't take clouds. This is how station defence clears grit, and it also lets a vacuum sweep steer a cloud toward an intake. Dust clouds (µm dust) take no force.
- **Station anchors:**
  - Beams on an anchored station move nothing; its anchor is debited, and a sustained push above `Σ HoldMaxN` counts as overload (exotic-matter).
  - **Overload is not instant (D27, proposed):** tractor or ram load above `Σ HoldMaxN` first drains the anchor's reserve and raises a public `AnchorOverload` alarm with a grace window (knob, proposed 60 s) before the anchor fails (exotic-matter.md, Station anchor).
  - The structural pass still runs: the anchor is the root, so modules far from it can be torn off, by one beam (no inertial relief).
  - Both of those make stations the easiest tractor target in the game, and an overloaded anchor drops the station. Every station is a protected zone (rings), so the protected-zone rule below stops other parties doing either unless the zone permits it, or a declared siege lifts it for the besiegers (Sieges, below).
  - Station emitters' reactions are debited to the anchor.
- **Protected zones (all tractor delivery, not just locks):**
  - A hit on another party's body delivers nothing (it is spill) if **either** the target body **or** the emitter is inside a protected zone, unless the zone permits it. This is tested per (beam, body) at each geometry update and covers Sweep as well as Lock: two narrow opposed sweeps from outside the zone can't tear a ship docked inside it, and a ship parked inside a zone can't tear ships outside it from safety.
  - Own and party bodies are always allowed.
  - Grid bubbles count as inside the zone their host is in, so hangar tractors follow the same rule for visitors' craft.
  - **Kept in revision 2:** this zeroing of tractor delivery to other parties' bodies is unchanged outside a declared siege. It protects stations and docked ships from being torn or overloaded by beams.
  - **Sieges (D27; proposed, owned by a future factions and law doc, mirrored in rings-and-asteroids.md):**
    - A faction may declare a siege on a station grid; it is public and logged. Besieging parties are hostile to that station and its zone for the siege's duration.
    - **Lifted** for the besiegers only: the zeroing above (besiegers' delivery to the station and its zone's bodies, and the station's emitters' delivery to besiegers, are real), and zone courtesies (docking assist, consent). Neutral parties in the zone keep full protection.
    - **Kept:** rings' asteroid rules inside `ProtectRadius` (lock refused, sweep zero, no rock-working: no debris manufactured inside), shields and debris defence, warp.md's exclusion push for non-permitted arrivals, the station's interdiction ban, and incident records and listings.
    - **Destruction stays possible but hard:** the anchor needs both independent feeds cut beyond its 10 s buffer, `Release` needs owner authorisation and a 60 s countdown, and tractor overload first raises the `AnchorOverload` alarm and grace window. Dropping a station is the top incident kind (`StationDrop`, heaviest standing and crime penalty).
    - **Capture is preferred:** boarding and an `Override` held at the station's `CommandCore` (ship-design-and-construction.md) transfers ownership (`StationCapture`). Capture yields the station, its stock and its anchor and gate policies; destruction yields wreckage. Tractors serve a siege as tackle (anti-escape of defenders' ships), tearing exposed turrets and arms, and holding boarding craft to the hull.
  - Rings' asteroid rules (lock refused, sweep delivers zero) stay, with one exception (revision 2): the zone's own station-defence emitters, and parties the zone permits, may lock and sweep asteroid **debris** (Dynamic or Coasting bodies) and clouds inside it, never rail, static or kinematic rocks (review fix: a lock frees a rock, so the exemption would have let anyone the zone permits work natural rock inside the zone). Asteroid debris has no owner to protect, so this is not delivery to "another party's body"; defence beams still deliver nothing to ships and grids.
  - **Withdrawn in revision 2:** the proposed line that zeroed a `Thrown` body's contact damage to other parties' grids inside a zone. Rings revision 4 makes debris in zones real: stations defend with shields and these beams, and the damage is an `IncidentRecord` (class from intent; liability per D33 for legal mining fragments).
- **Frames and bubbles:**
  - **A lock needs emitter and target in one bubble.** Acquiring one merges two bubbles of the same frame (as a contact island). It is refused if the frames differ or the merged extent would pass the 4 km hard radius; XL range is 3 km for that reason.
  - **Locked pairs count as contact** for transfers and splits, so they never separate across bubbles.
  - **Sweeps act only inside the emitter's bubble.**
  - Tractored bodies stay realised and never coast (rings' rule).
- **Interior gravity:** tractor-driven acceleration is proper (manoeuvre-type) acceleration. The inertial dampener (exotic-matter, D9) cancels it up to its rating `a_d` and leaks the excess; plating only adds floor gravity, and undamped rooms feel all of it. So a hard Haul on a light ship (~47 g for a 30 t fighter under an L beam, for the fraction of a second before the speed fade) throws the crew about.
- **Radiation:** the Graviton drive carries no dose; the Reaction drive carries none either (the reaction converts the AM); the emitter's AM trap breaches like any trap.

## Multiplayer

- **Server-authoritative:** beam geometry, forces, structural evaluation, breaks and detaches. Clients run the same functions only to predict their own ship and to forecast the gauge.
- **Wire:**

  | Message | When | Size |
  | --- | --- | --- |
  | `TractorCommand {emitter, mode, polarity, throttle, halfAngle, range, d*, pick(entity, part, local point int16 at 1/256 m), seq}` | client → server on change | ~28 B |
  | `TractorLock {emitter, target entity, part, local point, tick}` / `TractorUnlock {emitter, reason, tick}` | reliable, tick-stamped, interest of both ends | ~24 B / ~10 B |
  | `TractorState {emitter, mode, polarity, throttle q8, halfAngle q8, range q8, drive}` | reliable, on change | ~12 B |
  | `TractorStress {emitter, uMount q8, uMax q8, cutId}` | 5 Hz, to the emitter's owner and the target's crew | ~8 B |
  | Breaks | existing damage-brush messages (~34 B per tear brush, ≤ 32) plus `ComponentDetach {ship, component, tick, state}` (~40 B) | |

  No tractor-specific tunnel event: strikes on tractors use wormholes' `TunnelDischarge`. Consent rides the existing docking, tow and landing requests (one flag, no new message).
- **Interest:** a tractored pair is full-rate for both ends (rings' "full rate while tractored" generalised).
- **Prediction:**
  - A client predicting its own ship (C12, re-stepping a local Jolt scene) adds the forces of beams touching it, computed from replicated beam state and the other end's replicated pose, which is full rate.
  - Errors come only from that pose's latency and the spring-damper, and normal reconcile handles them.
  - **Breaks and detaches are never predicted.** When the forecast exceeds 1 the client plays creak and warning effects, but the snap waits for the server event, so a mispredicted tear never has to be undone.
- **Permissions:** tractor force on another party's ship is attributed to the emitter's owner (as dose is). A beam-pushed rock that hits anything is Hostile when its release path led onto the victim or its zone, whatever the arrival time (rings revision 4 review). Revision 2: a mount break, tear or forced undock it causes on another party's ship is reported as an `IncidentRecord` (the shared server-only record defined in rings-and-asteroids.md; kind `TractorTear`), with the emitter's owner as actor. Protected zones refuse locks on, and zero any sweep delivery to, other parties' bodies when either end is inside the zone, unless the zone permits it or a siege lifts it for besiegers (see Interactions); stations run their own docking assist only on request, server-clamped by enforced Safe-lock.
- **Consent (D26; this doc owns the rule, adopted from wormholes.md's text):**
  - Any Lock or Sweep delivery to another party's ship, grid or character without consent is a **weapon use**: a hostile act with the same standing, fines and faction consequences as firing on it. It writes an `IncidentRecord` of kind `TractorLock` (class Hostile) on acquire, or on the first delivered force for a sweep, even with no damage. Tears add `TractorTear`.
  - **Consent** is a flag on a docking, tow or landing request from the target's crew or owner. It names one emitter owner group, and lapses on release, on docking, on the target crossing a mouth, or after 120 s without a lock. A third party locking a consenting ship is still a weapon use; the server judges by the flag, not by claims, so no one can stage a fake attack.
  - Own and party bodies need no consent. Unowned wrecks and asteroids are not ships: rings' rules apply.
  - Station docking assist runs only on a visitor's request, still with server-enforced Safe-lock. Hangar tractors on visitors' craft use the landing request.
  - Anti-escape, pull-into-wormhole and combat locks are always hostile acts.
  - Consent never exempts a tunnel strike (wormholes.md).

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
  "gravitonWPerN": 1.0, "overdriveMul": 1.5, "overdriveMinPurity": 0.85, "overdriveMaxS": 10,
  "heatCapJ": 3.0e8, "coolW": 1.5e6, "heatWPerMN": 5.0e5, "gravitonHeatFrac": 0.2,
  "mount": { "tensionN": 4.5e6, "shearN": 4.5e6, "momentNm": 4.5e6 }, "massKg": 6000 }
```

- `data/components/towpoints.json`: S/M/L/XL ratings as in the mount table, with footprints of 2, 4, 8 and 16 m.
- `data/components/tractor-dampeners.json`.
- `data/ship-materials/*.json` gains `tensileMPa`, `compressiveMPa` and `shearMPa`.
- `data/physics/tractor.json`:
  `{ "emPerMNs": 1e-4, "beamVMax": 100, "eThrowJ": 5e8, "asteroidRailCap": true, "workResetS": 60, "defenceBeamsPerStation": 8, "defenceSlotsReserved": true, "defenceReleaseJ": 1e7, "defenceReleaseSpeed": [0.2, 5], "warpHoldAccel": 0.5, "wakeAccel": 1e-3, "flipTicks": 30, "acquireTicks": 30, "losLossTicks": 60, "geometryHz": 10, "raster": 32, "maxCellWrites": 32768, "maxCandidates": 64, "candidateCellM": 64, "beamsPerOwnerPerBubble": 4, "characterMaxN": 1000, "characterVMax": 10, "thrownTagJ": 5e7, "consentLapseS": 120, "defenceNeedsAm": true }` (revision 2: `thrownTagJ` compares `max(W_b, E_rail)` for asteroid bodies; r6: `consentLapseS` (D26), `defenceNeedsAm` (D28))
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
public enum TractorDrive : byte { Graviton, Reaction, Overdrive }   // D28: Graviton = no AM, Pull only

public struct TractorTarget { public EntityId Body; public int Node; public uint ComponentId; public float3 LocalPoint; public float DesiredRange; public bool Consented; }   // Consented: a live TractorConsent covers (target, emitter owner group); else a hostile act on another party (D26)

/// D26 consent, server-only table keyed by target. Set by a docking, tow or landing request; one emitter owner group.
public struct TractorConsent { public EntityId Target; public uint EmitterOwnerGroup; public long GrantedTick, LastLockTick; }   // lapses on release, docking, mouth crossing, or consentLapseS without a lock

public struct TractorState                    // one per emitter component; SoA arrays in TractorSystem
{
    public EntityId Ship; public uint ComponentId;
    public TractorMode Mode; public TractorPolarity Polarity; public TractorDrive Drive; public bool HoldAssist, SafeLock;
    public bool StationDefence;               // revision 2: owned by the station's defence controller (debris-only zone exemption, no LastBeamOwner, no delivery to ships, 8 reserved slots per station)
    public float Throttle, ThrottleCmd, HalfAngle, Range;
    public TractorTarget Target; public short AcquireTicks, FlipTicks, LosMissTicks;
    public float HeatJ, OverdriveS; public long EmittedNTicks;   // ledger accumulator (integer N·ticks)
}

public struct BeamHit { public int Node; public float Weight; public float3 LocalPoint; }
public unsafe struct BeamBodyHits { public BodyRef Body; public float Capture; public byte Count; public fixed byte Hits[16 * 20]; public int FullWeightsSlot; }  // ≤ 16 BeamHit; FullWeightsSlot: pooled (node, prev weight, new weight) list for live structural targets, −1 if none
public struct BeamWorkLedger { public EntityId Body; public long WorkKJ; public long LastTouchTick; }  // W_b, one per touched body; E_rail (asteroids) is computed, not stored

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
| Beam geometry (10 Hz per beam, staggered) | ~2 µs + candidate-grid walk + ~10 ns per node in the cone: ship target 5–15 µs, rock ~2 µs | ≤ 16 beams per bubble (≤ 4 per owner), ≤ 128 per server, plus ≤ 8 reserved defence beams per defended station; ≤ 64 candidates and ≤ 32k cell writes per beam |
| Forces (every tick) | ~0.5 µs per (beam, body) + one `AddForceAndTorque` (~0.3 µs) + the reaction | ≤ 8 bodies per sweep beam (largest capture first) |
| Structural pass (20 Hz per live ship) | ~33 ns per node with plane cuts: 1,000 nodes ≈ 33 µs; 4,096 ≈ 135 µs. It only reads this tick's forces and pose, so it runs as jobs alongside step 6 | ≤ 4,096 nodes per ship; ≤ 16 live ships per tick slot; server budget 0.25 ms per tick, overflow round-robin at a lower rate, never skipped |
| Graph build (lazy, worker) | ~0.25 ms per 1,000 nodes (MST + LCA sums + plane sums) | Only for live ships and client forecast targets; ≤ 1 per ship per 10 ticks; dropped 10 s after a ship stops being live |
| Break | ~10 µs for tear brushes + the existing split and bake costs | 1 cut + 4 mounts per ship per evaluation; 4 cuts per ship per s; 4 cuts per server tick |
| Ledger | a few ns per beam per tick + 10 Hz settle | |
| Client forecast | one structural evaluation at 10 Hz for the locked target | |
| Ships not under tractor load | **0** (no graph, no evaluation, no rebuild on damage) | |
| Asteroid rail cap (revision 2) | one dot product per (beam, asteroid body) per tick | |
| Station defence beams (revision 2) | as any beam (rock geometry ~2 µs at 10 Hz, forces ~1 µs per tick); ~4 µs per release (orbit-long miss test, push direction); 0 while nothing threatens. The 2 Hz watch is rings-and-asteroids.md's | ≤ 8 per station, in reserved slots (review) |
| Sweep on a rubble cloud (revision 2) | ~10 ns per (beam, cloud) | inside the 8 bodies per sweep |

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
  - `TractorRules.cs` (abuse limits, protected zones, warp hold, wormhole clip, bubble rules; revision 2: the asteroid rail cap, the station-defence exemption, cloud sweeps; r6: D28 pull-only Graviton drive, D26 consent and `TractorLock` incidents, D27 siege lifts, the AM-fed-only defence assignment, calls into wormholes' tunnel strike path);
  - `TractorDampener.cs`, `TowPoint.cs`.
- **New `src/Ships.Runtime/Tractors/`:**
  - `TractorTargeting.cs` (pick, tow-point snap, forecast, Safe-lock), `TractorHud.cs`;
  - `TractorBeamRenderer.cs` (cone VFX from replicated state, no extra bandwidth);
  - `StressView.cs` (build tool).
- **New other files:**
  - `src/Net/TractorEvents.cs`;
  - `src/Debug/TractorPanel.cs`;
  - `tests/Ships.Tests/StructureGraphTests.cs`: Σf = 0; dumbbell, ring, T and plate cut capacities against brute-force min cuts; MST + plane cuts against Gomory–Hu on random hulls; a uniform solid box under opposed nose/tail loads must report its mid cross-section (the comb-tree case); LCA centroid sums against brute force; identical graphs from shuffled input order; antenna rip under Haul and under Hold; opposed-beam tear; single-beam no-tear with ramp and fade.
  - `tests/Ships.Tests/TractorTests.cs`: capture fraction against analytic discs; linear and angular momentum conservation, including with a dampener on the target; `BeamVMax` cap; `W_b` cap under throw–catch-up–re-push; steady tow and opposed beams leave `W_b` near 0; warp hold thresholds and hold charge; occlusion by an interposer; own-body hits are spill; protected-zone zeroing for sweeps with the target inside and with the emitter inside. Revision 2: tow-and-release of an asteroid (hold, accelerate the tug, release) never leaves `E_rail` above `EThrow`, and braking is never faded; `Thrown` tags on `max(W_b, E_rail)`; station-defence emitters lock and sweep debris inside their own zone while other parties' sweeps there still deliver zero; defence beams never set `LastBeamOwner`; a `Thrown` rock entering a zone keeps its contact damage; a Sweep moves a `RubbleCloud` by `F·dt/M` and conserves momentum with the emitter. Revision 2 review: a ship whose `W_b` is at `EThrow` can still be braked; 16 visitor beams leave the 8 defence beams working; defence cones deliver nothing to ships; the defence exemption never locks a rail rock; a defence release leaves ≤ `DefenceReleaseJ` and its path misses the shell for a whole lane period. r6: a Graviton (no AM) emitter refuses Push, flips and Hold assist, and its Hold never pushes a closing target; a pull-only Hold still blocks warp spool; an AM-starved Pull falls back to Graviton; an unconsented lock writes `TractorLock` on acquire and an unconsented sweep on first delivery; consent lapses after 120 s without a lock and never covers a third party; a consented tractor in a tunnel frame is still struck; `StationDefence` never assigns a no-AM emitter to an inbound body; a declared siege lifts zeroing for besiegers only.
- **Change:**
  - `src/Physics/JoltRuntime` export probes for `BodyInterface_AddForceAndTorque` and `BodyInterface_AddForce` at a point.
  - `PhysicsScene` gains `AddForceAndTorque(BodyRef, F, τ, activate)`.
  - `GameApp` adds `--tractor-bench` (N beams × M live ships, logs µs).

## Changes to other docs

- **exotic-matter.md:**
  - The tractor row bills on **emitted** output (equal to delivered for a fully captured lock), with power = base + 1 MW per km of set range.
  - **Graviton-mode exception (D28; replaces the withdrawn pumped mode):** tractors are the one gravity device that runs without AM. The `Graviton` drive emits gravitons only, so it is Pull only at full rating, billed EM `0.1 g/MN·s` on emitted output, bus power `BaseMW + 1 MW/km·R + GravitonWPerN·F_emit` (1 W/N) and heat `0.2·GravitonWPerN·F_emit`. Push and fixed-offset hold (Hold assist) need the reaction; without AM, Hold is a pull-only tether. Exotic's `TractorMode {Off, Pull, Push, Hold}` is replaced by this doc's `TractorPolarity` and `TractorDrive`.
  - Overdrive at q ≥ 0.85.
  - A warp-blocking lock pays at least `m_target · 0.5 m/s²` of emitted output.
  - Station anchors: tractor overload of `Σ HoldMaxN` by other parties is only reachable where a protected zone permits it or a siege lifts it (D27), and it first drains the reserve and raises the public `AnchorOverload` alarm with a grace window (proposed 60 s); capture transfers anchor authority.
  - Classes S–XL.
  - The new `TractorDampener` row (same `EmPerMNs` per MN cancelled).
  - Tractors are bus class 2.
- **rings-and-asteroids.md:**
  - The `TractorBeam` row points here. S keeps 200 kN, 150 m and 0.5 MW.
  - "A lock is a spring-damper" now means per-hit, with despin emerging from it.
  - Add `BeamVMax`, the per-body work ledger `W_b ≤ EThrow = E_cap`, the `Thrown` tag and the zero sweep in protected zones.
  - ~~Abuse limits gain: a `Thrown` body's contact damage to other parties' grids inside a protected zone is zeroed and logged.~~ Withdrawn in revision 2: rings revision 4 makes debris in zones real and defended.
  - Revision 2 (already in rings revision 4): the asteroid cap `max(W_b, E_rail) ≤ EThrow` and the `Thrown` threshold on the same quantity; the station-defence exemption from the zone refusals (debris only, reserved slots, release ≤ 10 MJ; review); defence beams don't set `LastBeamOwner`; Sweeps on `RubbleCloud`s; `LastBeamOwner` is also set by rounds, hull contacts and AG kicks that redirect a rock (rings' framing fix: the field means "last party to redirect it").
  - A lock has Hold (the existing spring-damper) and Haul (constant force) controllers; the S mining tractor defaults to Hold.
  - "Tractors never fracture rock" becomes an abuse-limit line.
  - Vacuum mode is a wide pull sweep.
  - r6: `StationDefence` assigns AM-fed emitters only (D28), and the "N deflectors" HUD counts only those; the shared record is `IncidentRecord`/`IncidentKind` with `TractorLock` (D26), `StationCapture` and `StationDrop` (D27); a "Sieges (D27)" bullet in "Station defence and hostile acts".
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
- **warp.md:** a tractor lock (Hold or Haul, including a pull-only tether without AM) blocks spool only while `F_avail ≥ m_target · 0.5 m/s²`, and sweeps never block. The emitter's own lock auto-releases on a spool request. Incidents use the shared `IncidentRecord`/`IncidentKind` names.
- **wormholes.md:**
  - Beams are clipped at mouth discs and refused for `Warp`/`Throat` placements.
  - "Limited by range and by the target's mass and thrust" now points here (done in wormholes.md, "Decided after revision 2").
  - ~~Add the graviton-projection retaliation rule.~~ Done in wormholes.md revision 3 ("Tunnels", Retaliation); this doc calls its strike path and raises no `GravitonProjection` event.
  - The D26 consent rule is owned here now (Multiplayer, Permissions); wormholes.md's text is the same rule.
- **ship-design-and-construction.md:** the `CommandCore` family (D27 capture) is listed there; tractors only interact with it as a siege target.
- **README.md:** the tractor-beams row gains revision 2 station defence, D26 consent, D27 sieges and D28 pull-only; this doc's former open questions 1–4 are answered (D28, wormholes.md revision 3, D26, D27) and leave the collected list.

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

None. The former questions are answered: 1 (pumped mode) by D28, no AM means pull only; 2 (tunnel retaliation) by wormholes.md revision 3; 3 (tractors on other players' ships) by D26, a weapon unless requested; 4 (station sieges) by D27, sieges allowed and capture preferred. The siege and capture details stay proposals until a factions and law doc exists.

## Review notes

Lead review, 2026-11-25, against README.md, ship-hulls.md, frames-and-zones.md, exotic-matter.md, rings-and-asteroids.md, warp.md and wormholes.md. The fixes are already folded into the sections above. This lists what changed and why.

**User decisions: faithful, with two fixes needed to make 2 and 3 actually happen.**
- Decision 1 (push or pull cone, adjustable FOV, EM + energy + optional AM): faithful. The top-hat over solid angle makes narrow strong and wide weak, and the footprint table checks out (e.g. M at 0.1°, 500 m: 0.87 m radius). Pumped mode is this doc's reading of "potentially antimatter" and stays open question 1. *(Superseded by D28: no AM means pull only, at full rating; pumped mode is withdrawn.)*
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
- *Tractor-tearing stations:* a pinned station has no inertial relief, so one L beam tears a module, and overload can drop the anchor. Now gated by zones; docking assist is server-clamped by enforced Safe-lock, which also stops forced undocking at the collar. Station sieges are open question 4. *(Superseded by D27: sieges lift the zeroing for besiegers only; see Interactions, Sieges.)*
- *Perpetual motion and reactionless drives:* push and pull pairs already cancel. The real hole was the dampener: cancelled force still produced a reaction on the emitter. The reaction is now taken after every clamp, and cancelled force is spill. Own-body hits are spill, so a ship can't push or torque itself. Grapples keep the speed fade and pay 100× an anchor's EM rate.
- *Cheap tackle:* a Hold at its set range emits almost nothing yet blocked warp. A blocking lock now pays for `m · 0.5 m/s²`.
- *Slot denial and stacking:* at most 4 beams per owner per bubble.
- *Thrown rocks into zones:* the `Thrown` tag persists, and its contact damage to other parties inside a zone is zeroed and logged (proposed rings line). *(Superseded in revision 2: the damage stays real, stations defend, and it is a `HostileAct` of the thrower.)*

**Perf and determinism.**
- The candidate search was a linear scan of the bubble (~40 µs per beam at 2,000 bodies). It now walks a 64 m entity-sphere grid, capped at 64 candidates.
- No unrelated body wakes: the `WakeAccel` gate stays, sweeps hit ≤ 8 bodies, and protected-zone zeroing happens before activation.
- The graph bake now quantizes capacities and breaks Kruskal ties by edge index, so all peers produce identical trees.
- Server breaks stay authoritative and are never predicted.
- Revised budget: 30–55 µs per tick in single player, 0.45–0.7 ms in a 64-beam battle. Still estimates; `--tractor-bench` must measure them.

**Consistency with other docs.**
- exotic-matter bills tractors on "delivered" output; this doc changes that to emitted and lists it.
- warp.md's "a tractor beam" lock now has a threshold; listed.
- wormholes.md has no tunnel interior or retaliation rule yet (confirmed by search), so the `GravitonProjection` event stays a hook, and that is open question 2. *(Superseded: wormholes.md revision 3 owns retaliation; this doc calls its strike path and the event is dropped.)*
- The rings S row (200 kN, 150 m, 0.5 MW), `E_cap`, `ProtectRadius`, `KinematicMassLimit`, the hot-body refusal and the 6-a-minute rate limit all match.
- frames-and-zones tick steps 3 and 5, the 4 km hard radius and island-transfer rules match.

**Still unverified:** every cost figure, the material strengths against real blueprints (is 8 MN right for a fighter's mid-section?), whether plane cuts plus MST miss real oblique failure modes (Gomory–Hu oracle test), and how Hold assist's polarity flips feel in play (it may limit-cycle).

## Revision 2 notes

2026-11-25. Follows rings-and-asteroids.md revision 4 (user decisions on station shields, debris deflection, hostile acts and whole-body shatter) and applies the rings revision 3 review's proposal for this doc. Nothing outside this copy was edited, and nothing was built or run.

- **Asteroid energy cap `max(W_b, E_rail)` (rings revision 3 review, now in the beam model):** `W_b` is emitter-relative, so tow-and-release (hold a rock, accelerate the tug, let go) gave untagged rams of any size, e.g. 20 GJ from 1e6 kg at 200 m/s. For asteroid bodies the energy fade now uses `max(W_b, E_rail)`, with `E_rail = ½ m |v − v_rail|²` relative to the local lane point velocity (the field frame in fields); a force that would raise it past `EThrow` (500 MJ) is faded to zero, and braking relative to the lane is never faded. The `Thrown` tag (50 MJ) uses the same quantity, and every beam-touched rock carries `LastBeamOwner`. Ships keep `W_b` alone. Cost: one dot product per (beam, asteroid body). The reset's reference for asteroids is the lane point velocity, spin excluded, as rings revision 3 stated.
- **Station defence (Uses):** stations push drifting debris out of their protected zone with their own emitters, run by rings' `StationDefence` controller: Sweep for light bodies, Hold (brake, then reel out) for heavy ones, `DebrisArm`s for the last 40 m, release at 2–5 m/s outward once the path misses the shield shell. Up to 8 defence beams per station, exempt from the 4-per-owner cap but inside the bubble's 16 *(superseded by review: reserved slots outside the 16, release by `DefenceReleaseJ`)*. Defence beams never set `LastBeamOwner`, so pushed-out debris stays attributed to its maker. Capacity is the installed hardware; an outpost without emitters has no deflection.
- **Protected-zone rules, reconciled:**
  - kept: the zeroing of tractor delivery to other parties' bodies when either end is in a zone. It protects stations and docked ships from tearing and overload, and no user decision changed it (sieges stay open question 4; *superseded by D27: a declared siege lifts it for besiegers only*);
  - changed: rings' asteroid refusals (lock refused, sweep delivers zero) exempt the zone's own station-defence emitters and parties the zone permits. Asteroid debris has no owner to protect;
  - withdrawn: the proposed line zeroing a `Thrown` body's contact damage to other parties inside a zone. Debris in zones is real (rings revision 4); the station's shield and defence beams meet it, and the damage is a `HostileAct` of the thrower.
- **Rubble clouds:** a Sweep can push a `RubbleCloud` as one node at its centre (Δv = `F·dt/M`), so station defence can clear grit; locks can't take clouds, and dust clouds take no force.
- **Hostile acts:** tractor tears, mount breaks and forced undocks on other parties' ships are reported through rings' proposed shared `HostileAct` record (kind `TractorTear`), actor = the emitter's owner. *(Now the shared `IncidentRecord`, kind `TractorTear`; r6.)*
- **Data and code:** `tractor.json` gains `asteroidRailCap`, `defenceBeamsPerStation` and `defenceReleaseSpeed`; `TractorState` gains a `StationDefence` flag; new tests for tow-and-release, the defence exemption, `Thrown` damage in zones and cloud sweeps; cost rows for the rail cap, defence beams and cloud sweeps.
- **Open questions:** unchanged (1–4). *(Since answered: D28, wormholes.md revision 3, D26, D27.)*

## Revision 2 review notes

Lead review of revision 2, 2026-11-25, together with rings-and-asteroids.md revision 4 (whose "Revision 4 review notes" carry the shared reasoning). The user's decisions were not re-litigated. Fixes are in place above, marked "review".

**Decisions, checked:**
- **Station defence with tractors (decision 3): faithful.** Stations push debris out with their own emitters (Sweep light, Hold heavy, arms last), capacity set by installed hardware, and an outpost without emitters has none. Fixed: the exemption let the zone owner and permitted parties lock *any* asteroid body in the zone, and a lock frees a rail rock, so it would have let them work natural rock inside a zone; it now covers Dynamic and Coasting debris and clouds only. Defence cones now deliver nothing to ships, grids or characters, so a defence sweep can't shove visitors.
- **Withdrawn `Thrown` zone line: done everywhere** (Uses, Interactions, Changes, the old review note marked superseded). No text left says thrown or other debris is harmless in zones. The kept zeroing of tractor delivery to other parties' bodies is a different rule (it stops beams tearing docked ships and stations) and no decision touched it.
- **Hostile acts:** tractor tears, mount breaks and forced undocks report through rings' `HostileAct` (kind `TractorTear`; now the shared `IncidentRecord`, r6). Beam-pushed rocks are now classed by their release path, so a slow push from outside a zone that arrives after the 120 s window is still Hostile.

**The asteroid energy cap `max(W_b, E_rail)`: consistent with rings.** The formula, the "braking is never faded" rule, the 500 MJ `EThrow`, the 50 MJ `Thrown` threshold on the same quantity, the lane point velocity (field frame in fields) as reference, the 20 GJ tow-and-release example and the one-dot-product cost all match rings' Manipulation text word for word in substance. One fix found on the way: the never-faded braking rule applied only to asteroids, so a ship whose `W_b` reached `EThrow` (a 300 t ship thrown at ~58 m/s) could never be braked by any beam afterwards; braking is now unfaded for every body.

**Exploits closed:**
- **Slot denial:** defence beams counted in the bubble's 16, so four visitors with 4 beams each switched a station's defence off. Now 8 reserved slots per defended station, outside the 16 and the 4-per-owner cap.
- **Pushback as a weapon:** releases at a flat 2–5 m/s gave a 1e8 kg boulder up to 1.25 GJ relative to the station, untagged and not attributed to the station. Now `DefenceReleaseJ` (10 MJ: 0.45 m/s for 1e8 kg, 4.5 m/s for 1e6 kg), with the push direction chosen by the controller for clearance, never aimed by crew.
- **Pushback loops:** in a lane bubble a radial push is a closed Hill ellipse that returns after one orbit; releases are now tested over a lane period and prefer along-track pushes, which drift away.
- **Baiting:** a higher-priority threat preempts an emitter holding a body that already misses; clouds take only idle emitters (rings).
- **Cloud sweeps:** `BeamVMax` now bounds a pushed cloud, which has no work ledger; grit does no hull damage, so it is no weapon.

**Perf:** defence beams cost what any beam costs (rock geometry ~2 µs at 10 Hz, forces ~1 µs a tick), plus ~4 µs per release for the orbit-long miss test and direction choice; ≤ 8 per station, 0 when nothing threatens, so a fully engaged station is ≤ ~10 µs a tick. The reserved slots raise the per-bubble beam ceiling to 16 + 8 per defended station; the server-wide 128 still bounds non-defence beams.

**Not changed, flagged:** the Revision 2 notes above keep their original wording (8 defence beams inside the bubble's 16, release at 2–5 m/s); this review supersedes both. Station sieges are decided by D27: a declared siege lifts the kept delivery zeroing for besiegers only, and defence beams and the station's shield stay up (Interactions, Sieges). Costs remain estimates for `--tractor-bench`.

## Consistency pass (r6) notes

2026-11-25. Applies the r6 conflict list items that touch this doc (5, 8, 9, 10, 11, 12, 14, 16) and user decisions D26-D28 and D33. Only this file was edited; nothing was built or run.

- **Precedence:** header now says decisions.md wins, then review.md's rulings.
- **D28, pull only without AM (item 8):** drives are `Graviton` (no AM, Pull only, full rating), `Reaction` (AM, Pull or Push) and `Overdrive`. Pumped mode is withdrawn everywhere: recommendation, user decisions, `k_mode`, Consumption (power `BaseMW + 1 MW/km·R + 1 W/N·F_emit`, heat 0.2 of that), the new Graviton tow row (54 g/h EM, no AM, ~2.15 MW), `TractorState.drive`, `tractor.m` JSON (`gravitonWPerN`, `gravitonHeatFrac`), `TractorDrive`, HUD, starvation fallback, and the exotic-matter change (now the graviton-mode exception, replacing exotic's `TractorMode`). Without AM, Push, flips and Hold assist are refused; Hold is a pull-only tether that still blocks warp spool.
- **Station defence under D28 (item 9):** `StationDefence` assigns only AM-fed emitters; no-AM emitters never take inbound bodies; the "N deflectors" HUD counts AM-fed emitters only; docking assist without AM is a pull-only tether. `defenceNeedsAm` added to `tractor.json`.
- **Dampener, not plating (item 5):** Interior gravity now credits exotic-matter's inertial dampener (`a_d`, D9); plating only adds floor gravity.
- **Tunnel retaliation (item 10):** Interactions cite wormholes.md "Tunnels", Retaliation (first tick then every 60, escalating per owner group, 10 s disable, consent irrelevant); the `GravitonProjection` event is dropped in favour of wormholes' strike path; Changes and the old review note marked done/superseded; open question 2 deleted.
- **Consent, D26 (item 11):** new Consent bullet under Multiplayer, Permissions (this doc owns it): unconsented delivery writes `TractorLock`; consent is a flag on a docking, tow or landing request, one emitter owner group, lapsing on release, docking, mouth crossing or 120 s; anti-escape, pull-in and combat are always hostile; Uses rows marked; `TractorTarget.Consented` and a `TractorConsent` table; `consentLapseS`; open question 3 deleted.
- **Sieges, D27 (item 12):** new Sieges bullet under Protected zones (lifted for besiegers only: delivery zeroing and courtesies; kept: asteroid rules, shields, defence, warp exclusion push, interdiction ban, records), destruction barriers (two feeds, 60 s `Release`, `AnchorOverload` alarm and grace window, `StationDrop`), capture via `CommandCore` (`StationCapture`). Station anchor overload text, exotic-matter changes, the XL "(station, siege)" row kept; open question 4 deleted; review notes at old 610, 639 and 667 updated.
- **D33 (inherited):** defence-pushed legal mining fragments stay the miner's liability.
- **Shared record (item 14):** body text uses `IncidentRecord`/`IncidentKind` (`TractorLock`, `TractorTear`, `Thrown`, `StationCapture`, `StationDrop`); historical notes keep `HostileAct` with an "(now `IncidentRecord`)" marker.
- **Stale notes (item 16):** Revision 2 notes' "8 defence beams inside the bubble's 16, release at 2-5 m/s" marked superseded by review; README change line rewritten (open questions answered, row content per item 17).
- **Tests and files:** `TractorRules.cs` scope and `TractorTests` gain the D26/D27/D28 cases.
