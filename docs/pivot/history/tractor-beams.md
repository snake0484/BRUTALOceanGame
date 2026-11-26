# History of tractor-beams.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

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

## Round 6 / r7 consistency notes

2026-11-26. This doc was not revised in round 6; the r7 cross-check applied what decisions D52-D74 and the round 6 docs ask of it. Decisions were not re-litigated, nothing was built or run.

- **D59 (combat-and-sensors.md, ship-hulls.md):** cuts yield at `u ≥ 0.8` before they break (a `HullHinge`: bend rate, 12° and 1 m limits, 70 % capacity after yield). The impulsive evaluation is the "own damage model" for rams, shells and pulses. The graph stays in material space.
- **D55 (gravity shields):** a gravity shield lobe runs through this doc's force pass as a Reaction-drive emitter (EM and AM per newton-second, no AM means no shield, D28). Lobes count in the bubble's beam caps as reserved slots, like defence beams (≤ 2 per grid, 4 on stations, 8 per bubble). Deflecting another party's ship is a `TractorLock`-class weapon use unless it is on a collision course (time to impact under 10 s). Mount table rows added for gravity shield emitters and weapons.
- **D57 (factions-and-law.md):** it owns `IncidentRecord`, the fines for `TractorLock` and `TractorTear`, `ZonePermission.Check` ("unless the zone permits it") and sieges and capture; the Sieges bullet keeps only what is lifted and kept for tractors.
- **D67:** stress ratings scale with build quality.
- **D71:** this doc names no Control wiring; an emitter's commands and status are Data ports (ship-design-and-construction.md).
