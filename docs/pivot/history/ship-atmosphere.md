# History of ship-atmosphere.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

## Review notes

*Round 6 (D55, D59, D60–D66, D71) supersedes this review where they differ: the `MaxHoldS` cap and cool-down, the warp seal on gas, flight suits, the 5 s vent klaxon, `metabolicScale` 10, "no ship shields" and Control wiring (loops, pairs) are gone, and open questions 1–7 are closed. See "Round 6 notes".*

Lead review, 2026-11-26. Checked against decisions.md, ship-hulls.md, ship-design-and-construction.md, exotic-matter.md, frames-and-zones.md, planets.md, warp.md, rings-and-asteroids.md and review.md (C11). Fixes are in the text above; this is the list. Nothing was built or measured: every cost is still an estimate.

**The five decisions.** D38–D42 are all present and match the user's wording (they were already in decisions.md; the header said "proposed"). D39's "potentially" holds: only an opening leaks, and a shield or patch can stop it. D40: airlocks are needed by the physics, not by a rule (a ship without one gets a Warning, since a cockpit-only craft is legal). D41: gas only, constant power, no storage, advanced tiers. D42: optional component. Two places go past the wording and are now open questions rather than silent choices: an 800 m² hangar barrier against "small" (question 7), and how long an "emergency" shield may hold (question 6).

**Numbers verified by hand and found correct:** `C` = 0.6 × 199 m/s; `τ = V / (120 A)` and all 30 cells of the time table; the 0.29 / 0.76 / 2.8 / 4.6 τ thresholds; the `dt / 2τ` error of the implicit step; the pair-equalising transfer and the mixing fraction; units per mole and per gram; vessel contents; airlock cycle and vent times; barrier seepage, power and cost tables; breach-shield hold times on and off the bus; metabolic and CO2 timelines; refill times; vent thrust (0.75 Δp A is the choked-jet figure with Cd 0.6, an exhaust speed of about 530 m/s, so venting is a poor thruster, not an exploit); gas-giant pressures and the hard suit's 180 km.

**Fixed: flow maths**
- The sweep volume `S` was in whole litres: 17 % truncation at 1 cm², and zero flow under 0.8 cm². Now microlitres.
- Species could go inconsistent by a unit (`QO2 + QCO2 > Q`) from the two floors, and `ΔQ / Q_u` had no guard at 0. Clamp and guard added; mixing clamped the same way.
- **Small rooms in a chain were stable but wrong:** a 4 m³ lock between a corridor and space throttled the corridor to τ ≈ 6 s against 1.0 s physical (30 s at a deferred step). Sub-stepping of rooms with `ΣS > V` (≤ 8) brings it to 1.5 s. Tinier rooms stay slow; validation says so.
- A sagging barrier overshot its hold pressure on a small room (24 kPa instead of 48). A hold clamp makes it a relief valve at `Δp_hold` for any `dt`.
- The φ table's last interval stretches the final 3 kPa of an equalisation about 2.5×. Documented, accepted.
- Zones are well mixed, but nothing mixed two rooms of different composition before they joined one. A composition difference now keeps the pair active. Room-to-room breaches join zones.
- Bulkhead rate trigger now needs a 3 kPa fall (a lock equalising into the ship used to trip it); the two worked examples were recomputed (88 kPa at 1.5 s; about 30 kPa).
- Small corrections: "holds its sustainable hole 14 min" is 17 min; "50 kPa at one scale height" is 37 %; the wind table's 7 N at 1 m is 6 N; the time table is labelled as τ.

**Fixed: determinism**
- "Identical for any step order" was false: the pair step is sequential. Determinism now rests on a stated fixed edge order, with opening deltas applied in a sorted order so bake-job timing cannot change edge indices.
- The global cap was in milliseconds, which would make gas state depend on server load. It is now a count of edge-steps.

**Fixed: performance**
- 35 ns per edge was one divide, not a pair step. Restated as ~0.2 µs (worst ship 0.2 ms a step, 35 µs a tick amortised); the 0.15 ms cap is unchanged and now bites at about four fully torn capitals, which then step at a larger `dt`.
- Ambient was refreshed at 1 Hz for every awake grid in an atmosphere; now on demand, so a sealed parked ship is really 0.
- Reads (vitals, console, crush check) no longer settle zones; characters in good air are not in the vitals pass; an occupant moving inside a zone is not an event.
- Airlock interlock pairs would have blown ship-design's 64 FailSafe loops on a station. They are read on demand.

**Fixed: consistency with other docs**
- ship-hulls: a hangar behind a barrier is a sealed room and would have been filled solid in the exterior collider. Barrier frames are carved as open portals and big barrier rooms stay unfilled. Cells opened by damage had no room; they now belong to the adjacent one for air and `GridVolume`.
- ship-design: its seam rule (threshold 0.45, width 2 mm) was replaced here by a different threshold. Threshold restored; only the width change is proposed, with the reason (2 mm empties a cabin in 2.3 min).
- exotic-matter: room cap is 255, not 256 (`byte Room` needs a "none"). Class 1 needs an internal order or dampeners starve barriers.
- warp: a hard cancel releases the openings, so gas flows from that tick.
- planets: differential crush kept; its consequence (a flooded ship has no room crush depth) is now stated, with what still limits it.
- rings-and-asteroids: D34 station shields are untouched; "no ship shields" still holds because barriers and breach shields absorb nothing.
- Replication pressure saturated at 1.05 MPa; scale flag added.

**Exploits looked at**
- *Venting an enemy's rooms through doors or a captured console:* every door now has a Δp latch, so no button press or single Data command opens a pressurised room; `Override` is a 3 s lever at the door or an authorised, klaxoned, logged command. Remote venting needs an authorised seat and gives 5 s warning. The docs have no hacking mechanic; one added later must not bypass these.
- *Barrier power sniping:* intended and kept (D41). Bounded by two feeds, the backing door, class-1 ordering and a breach shield for frames ≤ 25 m². A hangar mouth has no answer but its door; question 8 asks about a ride-through.
- *Emergency-shield stalling:* indefinite hold capped (`MaxHoldS`, cool-down); the deploy, drain, drop, recharge flicker removed by a lockout; valves and commanded vents cannot spend patches; merges never fold a held edge into an open one.
- *Air barrier or patch as a free door against boarders:* bodies pass both, stated in both sections and under Boarding. A barrier is a security hole, not a door. Question 5 recommends keeping patches passable.
- *Airlock as a free way in:* panels use the access list, exterior doors lock, outside cranks and the manual equalise handle are not available to a stranger.
- *Decompression used to throw players:* drag is now on relative velocity and implicit, so nothing outruns the air (a light panel used to become a projectile). The throw is bounded and stated (`0.005 · v_air · L` m/s for a person; boots hold to 44 m/s), and every actor-caused opening is attributed.
- *Blowing doors with the regulator:* room setpoints capped at 120 kPa.

**Not fixed, for the owner to decide**
- Open questions 1–8. Questions 6–8 are new.
- Closing a door at depth in a gas giant captures free "inert" gas. Harmless while inert is one species; revisit if hydrogen becomes cargo through this path.
- The pump power figures (20 kW for 250 L/s into 2 MPa) are under the ideal compression work at the start of a cycle. Placeholder, like the costs.
- Sub-stepping is capped at 8, so rooms under about 1 m³ with large doors, and any small room during a long deferral, still pass gas too slowly. If the breach sim shows it mattering, the next step is to treat such a room as a junction with no storage.

## Round 6 notes

2026-11-26. User decisions D60–D66 applied in place. Nothing was built or measured; new numbers are data knobs, checked by hand against the flow law and the vitals table.

**Edits in this doc**

- **D60 (no sea flooding):** the "Under the sea sphere" ambient row states it; open question 4 closed.
- **D61 (no barrier size cap):** the "one frame ≤ 64 m a side" limit is removed. New "No size cap" bullet (a frame over 2,147 m² is several `Barrier` edges on one pair, because `AreaMm2` is an int32), a 4,000 m² example row, the per-m² figures stated as the only law. The power law is unchanged. Open question 7 closed.
- **D62 (battery-fed emergency shield):** section 6 is rebuilt on a battery. Removed: `MaxHoldS` = 30 min, the equal cool-down, the "sustainable hole" held on bus power, the 50 % deploy lockout. Added: `t_hold = E_battery / Σ(k · A · Δp)` with a hold-time table by hole size and class, recharge from the bus only while no patch is up, lazy energy bookkeeping with one scheduled event per projector, `MinHoldS` = 60 s as the anti-flicker rule. Updated: the barrier section's breach-shield cross-reference, the per-tick cost table, the tests, milestone A4's exit check. Open question 6 closed; question 9 added.
- **D63 (shields do not stop bodies):** already the doc's behaviour; now stated with the decision and tested. Open question 5 closed.
- **D64 (suits):** the self-closing flight suit is gone and normal clothes are the default. New: suit penalty table, `SuitLocker` component (`fn.suit_locker`: slots, 30 s on and 15 s off for an EVA suit, the slot is the refill port, access list), hood at 3 s, the normal-clothes timeline table and the four-step survival chain. Updated: airlock EVA flow, boarding and capture bullets, validation rows, costs, data, files, tests, milestones A1 and A2. **Consequence taken:** the vent klaxon and the remote door `Override` klaxon go from 5 s to `VentWarnS` = 15 s, because no helmet closes by itself any more. Open question 3 closed; question 10 added.
- **D65 (real air use):** `metabolicScale` = 1. Timelines restated as a table (two in a dead 10 m³ cockpit: 3 kPa CO2 in 6.5 h, was 39 min). New bullets: dead life support is logistics, and leaks, lock cycles and breaches set the gas budget, not breathing. The reserve check reads in crew-days. Hood and suit durations are unchanged (they are times). Open question 1 closed.
- **D66 (warp is real space):** the section 8 Warp row is rewritten: no seal on gas, breaches vent to vacuum in cruise, barriers at full draw, no re-arm event. Warp cruise added to the vacuum ambients. Airlock warp bullet: the spool lock is a lock, not a seal. Room sensors gain a `PressureDrop` Data output that can be wired to the drive's `Cancel`. Open question 2 closed.
- **Review notes** are kept as history, with one line saying what round 6 supersedes.

**Changes other docs need**

- **warp.md (D66):** done in its round 6. Nothing is sealed; door, collar and rack locks are mechanisms; ambient is vacuum from T0 to the first real-space tick; `PressureDrop` can be split to the drive's `Cancel`; vent thrust does nothing in cruise. A body that goes out through a hole in cruise is in its `WarpShell` pocket and is lost past the wall (a proposal there, its open questions 7 and 8).
- **ship-design-and-construction.md:** family `fn.suit_locker` (LV power and `Air` ports, 1 / 2 / 6 slots) and hood lockers in the catalogue; the four D64 validation rows of class Atmosphere; `PressureDrop` as a Data output of room sensors; remote door `Override` uses `VentWarnS` (15 s).
- **exotic-matter.md:** the class-1 sub-order stands, but "breach-shield recharge" is requested only while a projector holds no patch, and no patch ever draws from the bus.
- **ai-ships-and-crew.md (being revised elsewhere):** crew are in normal clothes by default, so `Suited` is no longer the usual state in the breach row; `Alert` "suits on" costs a walk to a suit locker plus 30 s each and needs locker slots; unsuited crew have hoods (3 s, 10 min); "an emergency shield that holds the breach means nobody is at risk" is true only for the battery's hold time at that hole size; life-support failure at L0 and L1 runs on real rates (hours on a small craft, days on a ship).
- **combat-and-sensors.md:** its "Emergency breach shields" row now reads: hold time from the battery by hole size, no recharge while holding, several holes on one projector share the battery (done in the r7 consistency pass).
- **factions-and-law.md:** venting a room is now lethal within seconds to anyone not hooded or suited, after a 15 s klaxon; its "Venting people" ruling states that weight, and section 3 here points to it (done in the r7 consistency pass).
- **README.md:** this doc's index line should mention battery-fed emergency shields and suit lockers; "Missing systems" still needs a character doc to own the suit penalties.

**Applied in the r7 consistency pass (was pending here)**

- **D55 (ship shields):** section 5 "Not armour" no longer says "no ship shields"; barriers and breach shields are stated as separate from combat-and-sensors.md's particle, energy and gravity shields, and the barrier's draw feeds the thermal ledger.
- **D59 (dents and deformation):** a "Dents and bends" row in section 8: pinhole and seam `Leak` deltas, room volume, sticky and jammed doors, by reference to combat-and-sensors.md section 2.
- **D71 (two wiring media, power and data):** no Control lines remain. `Seal` is a FailSafe hold line, airlock `Interlock`s are a Data cable each way, `BarrierDown` is a Data output, the breach shield's `Inhibit` is a Data input, `VentInhibit` is a key switch in series on the vent command cable, and `SegLoopMask` is ship-design's `SegLineMask`. The `Air` medium is a pipe, not a wire.
- **Warp (D66):** the airlock bullet and the section 8 row follow warp.md's round 6 text (locks from spool to T0 and from TCommit to the end of the brake; the `WarpShell` pocket and wall).
- **Law:** the attribution paragraph cites factions-and-law.md (owner of `IncidentRecord`; harm from an air event is `PersonHarm`) and its venting ruling.

**Observation, not changed**

- A held patch seeps at `A · 1e-3`, so the room behind it sags with τ = V / (0.12 · A): 14 min for a 50 m³ cabin behind a 0.5 m² patch, the same order as the hold time. The hold-time table is therefore the shortest case, and a long hold on a small room ends with a thin room anyway. If long holds should keep a room fully breathable, the seepage factor is the knob.

**Open after this round:** questions 8 (barrier ride-through), 9 (charging during a hold) and 10 (suit numbers). The rule for bodies at a hole in cruise is warp.md's `WarpShell` proposal (its open questions 7 and 8).
