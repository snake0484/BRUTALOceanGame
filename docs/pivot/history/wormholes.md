# History of wormholes.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

## Revision 3 notes

**What the decisions changed:**
- **A, energy fight:** new Stability section. A fixed-point reserve `Es` with bands (Overcharged, Stable, Unstable, Critical, Zero), feeds, drains, a 60-tick notice and early collapse at zero. `TClose` became the paid maximum everywhere. Added scars and the re-establish rule (0.25 X, the remaining paid life kept, no capture), power-through for queues, and overcharge as the buffer.
- **B, holders:** new `Stabiliser` component with Feed and Drain modes. Battleships carry the 5 km kit; support ships project from 50 km at lower efficiency. Destabilisers are the same component, reversed.
- **C, natural recovery and throughput:** natural pairs relax linearly to a seeded `Ψ_nat,k` per cycle. Every entry drains `e_m · m` from every wormhole.
- **D, classes:** new class table. Instant is the existing design at 2.5× X and 1.5× opening energy. Tunnel is the baseline price and travels through a `Tunnel` frame with its own bubbles. The flow is a current with a per-body coupling `κ ∝ A/m`, so one-way emerges per ship.
- **E, stability shapes the tunnel:** a seeded field (twist, radius, wells, eddies, storms) driven by the quantised instability. Wells use the gravity scheme; the current uses the coupling force. Cruise control works only in the Stable band.
- **F, combat:** retaliation strikes on projectors outside the hull (escalating, disabling, draining 0.5 % each), ricochets for kinetic rounds, and armed charges struck into detonating.
- **G, collapse flow:** a collapse point (planned and published, or at the cause), an outward current of 0.9 `VCrossMax`, fronts at half that, ejection for the overtaken, and a mouth flush when each front arrives. Instant pairs keep the both-way flush.
- **H, additives:** a blend of up to 10 % additives in X, with effect and trade-off factors in the quote. The blend must meet `MinPurity` on true content. Item and crafting detail stays in exotic-matter.md.

**Reconciled with the fixed-`TClose` invariant:** admission still uses `TClose` as the latest collapse; queues drop slots after `Tz`; catch slots free after the scar window, with no fee refund; the flush runs at `Tcs`; natural schedules never move; the mouth stall is removed in favour of pulse drains; `Throat` holds and late cross-shard blobs flush at the collapse tick; decoys free their catch slot when they collapse; a warp-refused mouth host is released when the last mouth despawns.

**Problems found while integrating, and fixes:**
- A tunnel is longer than a bubble's 4 km limit. It is a frame with several bubbles; every occupant is simulated, so there are no rails or analytic crossings inside.
- A uniform-acceleration current would make one-way the same for every ship, and could not separate light from heavy ships in a collapse. The current is a coupling `κ = c_t · A_⊥ / m` instead: light ships ride, heavy ships lag, and one-way depends on thrust.
- Clients need `Ψ` for the field without per-tick traffic. It is an integer integrated from slope events, with linear natural relaxation (an exponential would drift between platforms).
- A feed arriving in the tick after zero could have flickered a collapse on and off. Zero is final, with a fixed 60-tick notice.
- Ejection needs a path for natural pairs, which have none. Their path is computed at each `TNC_k` with the planner's core-detour rule, from the record.
- A ram ejection could insert a raid into a base. Occupancy terms apply to ejections with warp's exit push; only natural (deadly) terms are ignored.
- Exits faster than `VCrossMax` would have reopened the infinite-fall loop. They are clamped, with impact damage except in a collapse.
- The tunnel lets anyone hold traffic indefinitely, so the instant hold pool is not needed there. The bore is the hold, bounded by `TunnelCap`.
- The fixed-`TClose` admission rule did not account for transit time. Tunnels close admission at `TClose − TunnelLead`.
- An armed charge in a tunnel would have been a one-sided weapon. Arming is a projection, so the wormhole strikes it at once, and it detonates in place.
- Retaliation strikes drain the reserve, so a swarm of cheap tractor drones could drain a wormhole. Strikes escalate per owner group and disable the projector, and their total drain is capped at 1 %/s per wormhole (see Revision 3 review notes).
- Bombs replacing the stall reopens the griefing concern behind exotic-matter.md's invariant. The drain scales with yield and falloff, dampers still protect mouths, overcharge and feeders buy margin, and the re-establish makes a collapse cost a quarter of the EM rather than all of it.
- Reducing X for the cheaper class would have broken exotic-matter.md's "cheaper than warp" check for heavy and capital generators (their ratios reach 1 near 0.7×). Tunnel prices are therefore the old baseline, and instant became the premium.

**Unchanged on purpose:** the 3 s commit reveal, the binding quote, the no-move-after-announcement rule (a re-establish is a new announcement), the privacy rules, catchers, signatures, flares and every earlier user decision not superseded above.

## Revision 3 review notes

**Decisions A–G, checked:**
- **A, energy fight:** present and faithful (reserve, feeds, drains, early collapse, paid maximum, power-through, cheaper re-establish). Fixed the leftovers of "nothing closes early" below (`TEnd`, late zeros, re-establish wording).
- **B, holders:** present (battleship kit 5 km, support projectors 50 km). No change.
- **C, natural level and throughput:** present. Bounded the mass drain (below).
- **D, classes and tunnels:** present. Fixed the admission lead, which made the document's own 1 AU example uncrossable, and the tunnel-frame velocity bookkeeping.
- **E, stability shapes the tunnel:** present. Fixed the ends, sleeping bodies, storm onset, determinism, ram exploits and the ejection velocity.
- **F, combat:** present. Scoped retaliation to the tunnel frame, fixed its targeting, escalation and drain, and stopped rounds, items and missiles from being ejected.
- **G, collapse flow:** present (point, outward flow, fronts, heavy thrown out, light carried out, instant pairs keep the flush). Fixed the lifetime bound it broke, and the ejection budget.

**Leftover reliance on a fixed `TClose`:**
- **A tunnel outlives `TClose`.** Its mouths, shells, occupancy and lanes live until the last front arrives, up to `L_t / V_f` (444 s for 60 km) after `Tcs`. Suppression, the rails sweep, mouth occupancy, catch-slot holds, fleet-block spacing, flare cones and the natural schedule all still ended at `TClose`, so another mouth, a warp exit or the next natural cycle could form on top of a collapsing mouth. Added `TEnd` (= `TClose` for instant, `TClose + ceil(60 · L_t / V_f)` for tunnels), fixed at charge start, and moved every despawn-bound window to it. The natural generation constraint gains the collapse term.
- **A zero in the last 60 ticks** gave `Tcs = Tz + 60 > TClose`, beyond the paid maximum and beyond `TEnd`. Now an early collapse needs `Tz + 60 < TClose`; a later zero only stops admissions and the planned collapse runs.
- **Re-establish** claimed "`TClose` never extends" while setting `TClose' = TOpen' + R` after the downtime. Clarified: the paid life is kept, not the wall-clock end; it is a new wormhole with its own `TClose'` and `TEnd'`. It now also needs `R ≥ 2 · TunnelLead` for tunnels.
- The collapse flush said flow stops at `TClose`; it stops at `Tcs`, including a tunnel's outer field.

**Contradictions:**
- **Admission lead vs open time:** `TunnelLead` at 100 m/s was 239 s for a 1 AU bore, longer than the 120 s open window in the cost example, so no one could ever enter; the table's 129 s transit also overran the window. `V_adm` is now 200 m/s (cruise control's default), tunnels need `OpenTicks ≥ 2 · TunnelLead`, and the 1 AU and 5 AU tunnel rows are quoted at 300 s (130 and 138 GJ, totals 197 s and 217 s, still ahead of warp). The break-even with warp moves from ~0.6 to ~0.7 AU; open question 22 updated, 26 added. The re-establish and fleet examples follow.
- **Mass could never collapse a wormhole** (an entry that would empty it bounces) yet `Throughput` was a collapse cause and "an entry's mass" set `s_c`. Kept the refusal; `Throughput` now only names the cross-shard race.
- **Storms** started at `u > 0.5` (`Ψ < 0.3`, still Unstable) while the bands said Critical only. Threshold moved to `u ≥ 0.58`.
- **Bands** overlapped (Stable `≥ 0.6` vs Overcharged `> 1`), and the Aperture additive raised the Stable threshold that `u` ignored. Stable is now `Ψ_s`–1 and `u` uses `Ψ_s`.
- **Retaliation timing:** "on the tick the projection starts" vs the tick order's "1 Hz, staggered". Strikes now run in step 3.
- **Natural pairs** had a Scar phase with no generator to re-establish. They leave no scar.

**Physics and precision:**
- **Twist at the ends:** `δ` up to 400 m at `s = L_t` would put the bore beside the disc, and a well near an end could push an exit past `VCrossMax`. A 500 m taper calms both ends; no well slot lies near them.
- **Sleeping bodies:** the coupling and well deltas are applied without activation, so a body asleep in a calm bore would ignore eddies, wells and the collapse flow when they appeared. Tunnel occupants never sleep (with a wake fallback if `AllowSleeping` isn't exported), bounded by the caps.
- **Galilean rebase:** the coupling `κ(u − v)` needs tunnel-frame velocity; after a bubble boost the scene velocity is off by the origin's velocity. Stated that tunnel tests use tunnel-frame state.
- **Ejection velocity:** `(1 − f) · u_A + f · u_B` in `Lca` left bodies near a planet almost at inertial rest (warp's "frame rest" fall) and mixed in the far body's km/s. Now warp's drop rule at `P(f)` plus half the bore velocity. Also noted the `L / L_t` compression along the path (6.3 million at 1 AU): `f` is taken at the centre only.
- **Ejection budget:** a body waiting more than 8 ticks for `ArrivalBooking` had no rule. It is now force-placed, as flushes are.
- **Determinism:** `Math.Sin`/`Cos` are CRT-dependent, so "bit for bit" between platforms needs our own `DetMath` and integer-lattice noise (also for natural `Φ(t)`).
- **Stability on clients:** discrete drains rode on crossing and detonation events sent only to local interest, so mid-bore occupants could miss them and evaluate the field at the wrong `u_q`. One coalesced `StabilityState` per tick with drains now goes to the whole stability audience.
- **Collapse and the wall:** from `Tcs` any wall contact is a collapse ejection (discharge, no impact), so the narrowing ends don't turn carried ships into rams.

**Exploits:**
- **Farming ejections from a cheap tunnel:** a 10 m/s bump turned any tunnel into a line of free warp exits that skipped interdiction fields and then warped on. Ram damage now uses at least `V_ram` and a full `u = 1` discharge, and a rammed grid cannot spool a warp or charge a wormhole for 30 s. Only grids and characters can be ejected: rounds, items, debris and missiles no longer use the wall to reach any point along the path.
- **Retaliation as a weapon:** strikes now hit only the projecting component and its own grid (no collateral along the arc, no foreign host for a limpet), escalate per owner group, and apply only to devices in the tunnel frame, so mouth-covering dampers outside are safe.
- **Retaliation drain:** 128 drones could drain ~6 %/s through strikes. Strike drain is capped at 1 %/s per wormhole.
- **Reflection:** rounds lose 40 % of their whole speed per bounce, are absorbed after 3, and are never ejected; exiting through a disc is the same as through an instant mouth.
- **Mass dumping:** one pushed 4 Mt asteroid would drain 13 reserves (refused, so it "only" left the pair in Critical) and block a direction for an hour through the flux debt. Entries over `MaxEntryFluxTicks` (2 min of flux) bounce; mass drain is then at most `EsMax / 300 s` on average and ~40 % per entry, which one feeder outpaces.
- **Bore denial:** 128 cheap drones or 512 crates could fill `TunnelCap`. Added a 32-grid per-group cap, wall absorption of the oldest uncrewed smalls on overflow, and a 4,096-occupant server cap that also bounds the never-sleep cost.

**Checked against warp.md, frames-and-zones.md and review.md:** the drop rule, exit push, `ArrivalBooking`, `GridInertial`/proper-acceleration resets, C2 (no activating forces), C12 (prediction scenes), C13 (int64 positions), P1 (own sphere tests) and R7 (predictive merge) are all used as those documents define them. `FrameKind.Tunnel` as a second root is consistent with `Relative` walking to a common ancestor (there is none, so it is refused). No further conflicts.

All costs are estimates. Nothing was built or run, and no repo files were changed.

## Revision (round 5) notes

Applied the user's round-5 decisions D19, D23, D21 and D26 (decisions.md wins over any earlier text here).

- **D19, proposals accepted:** "Open questions for the user" is now "Decided in round 5 (formerly open questions)". All 26 items are decided statements with their numbers kept. Questions with no explicit proposal take the design as written: Q10 path-pull reach, Q13 frame defence (no power against redirects; frames may carry stabilisers), Q15 natural size, Q16 copy ageing kept, Q19 energy-fight scale, Q20 no capture on re-establish, Q22 `LtK` 2 km, Q23 instant 2.5×/1.5×, Q24 collapse severity, Q26 `2 · TunnelLead`. Q14 fees use exotic-matter.md's suggestion (throat-grade EM). Q5 records the earlier round-5 decision that natural pairs lead to other star systems, with a pointer added under Natural wormholes, Generation.
- Inline "(proposed; open question N)" references in Stability, Re-establish, Tunnels (rams, retaliation) and Signatures now read "decided, round 5". The Signatures line now names frames and stabilisers as well as generators (Q11).
- **D23, roles:** natural wormholes are the cheapest travel, manufactured wormholes the fastest, warp the cheap option for close, specific trips. This replaces the blanket "warp cheaper, wormholes costlier" and the "more expensive but faster" invariant. Changed: the recommendation paragraph (a Roles line, class labels), the class table's Cost row, the Cost model's tuning invariants, the warp comparison table (a new Role row) and the invariant test line.
  - exotic-matter.md's per-generator-class check is kept, but only as a **single-ship** rule (a lone ship warps more cheaply than it can open the cheapest wormhole it fits). Fleets sharing a manufactured wormhole may pay less per ship than warping, and that is intended. exotic-matter.md should restate its invariant the same way.
  - **Break-even checked:** the cost table is unchanged and consistent with the new roles. Instant (31–71 s) beats warp from 20,000 km, so manufactured is the fastest option there. Tunnel beats warp from ~0.7 AU (1 AU: 197 s against 230 s) and loses to it at moon range (69 s against 48 s), which is warp's close-range niche. The cost formulas and constants are unchanged.
- **D21, black holes:** the suppression terms themselves are unchanged. (Corrected by the round 5 review: exotic-matter.md already overrides this doc's body-core term for black holes and adds disc and jet terms; those are now mirrored as a row in the suppression table, with forecast births counted as cores.)
- **D26, tractor consent:** under Decided after revision 2, an unrequested tractor lock is a weapon use, so interdiction pull-in is always an attack. Gates and stations may tractor a ship in only on the crew's request (a consent flag on the slot or docking request). Tunnel retaliation still strikes any active tractor, requested or not, so no one can be tractored through a tunnel.
- **Consistency touches from other round-5 decisions (no design change here):** the warp comparison table's Privacy cell now follows D18 (arrival location broadcast as a safety beacon, military or cut-wire opt-out, the last part of the trip in real space). The tractor note mentions D28 (pull only without antimatter).
- The Review notes, Revision 2 and Revision 3 sections are history and were left as written. Nothing was built or run, and no repo files were changed.

## Round 5 review notes

Skeptical lead review of the round-5 edits against decisions.md (D19, D23, D21, D26, plus D15, D16, D18 and D28 where this doc touches warp or tractors). Fixes are folded in above; this lists them.

- **D19 verified:** all 26 former questions read as decided rules, and each matches the proposal or the body text (checked leash, friendly fire, copy ageing, outflow push, stabiliser exemption, frame rules). Fixed: Q3 claimed the stability additive "covers" path stiffening, but it only enlarges the reserve and never affects capture; Q6 said "until the last mouth despawns", now "while any mouth is mounted (until it despawns or detaches)", matching warp's validation step; the Signatures line listed only catchers, generators and frames as copy loaders, now stabilisers too (Q11).
- **Q5 consequences (new rules, flag for the user):** natural pairs across systems broke two things. (1) A plan path is a polyline in `Lca`, which two `Solar` roots do not have, and `ℓ = ln(1 + L/1e6)` would price an interstellar throat at about twice a 1 AU one. Manufactured plans are now refused across roots; only natural pairs link systems. (2) Ram and collapse ejections map tunnel arc onto that path; a cross-system pair now uses two seeded legs (0.05–1 AU, one per system, from `pairSeed`, so the record does not grow), and an ejection never lands in interstellar space. Cost: one polyline as before. "2–6 pairs per system" now counts pairs with an end in the system; the shard-per-system wording is limited to dedicated servers.
- **D23 verified and tightened:** the cost table, formulas and break-evens are unchanged and match the roles. Fixed: "natural costs nothing (at most a frame owner's or gate's fee)" invented a natural-crossing fee that no mechanism charges; fees exist only for managed gates catching a generator's end, and a frame holder's toll is player pricing by force. "Fastest in the game from 20,000 km" became "fastest on-demand option within a system", since an open, well-placed natural bore can beat a 31 s instant charge on its own schedule. exotic-matter.md's check is stated as single-ship on both sides (its line 774 already reads that way).
- **Q14 fees:** `Fee` is `int`; it is now defined as throat-grade EM content in grams (an int of mg, the unit of `ContentMg`, would cap at 2.1 kg).
- **D21:** the earlier note said this doc never references black holes, but exotic-matter.md already overrides the body-core term (`InfallRadius + r`) and adds disc and jet terms. That row is now in the suppression table, and a forecast birth counts as a core, which matters more now that collapses (and so rebirths) can be forced. A pending collapse needs no new term. Cost: one extra sphere per system in the pruned source list.
- **D26:** the consent text let a gate tractor a ship "to its catch slot" via a `SlotRequest`; catch slots place wormhole ends, not ships, so consent now rides a docking or tow request. Consent is scoped to one emitter owner group and lapses on release, docking, crossing or after 120 s, so a third party can't ride it and a ship can't stage a fake attack. The rule that tunnel walls strike requested tractors too is kept (retaliation is physics, not law).
- **D16/D18 consistency:** "matches warp's 3 s exit announcement" is gone (warp now beacons its arrival; wormholes stay private until commit, on purpose). The warp table's Exit cell no longer cites `WarpFloor` (no forced floor; low-atmosphere exits mean pressure waves and fines), and its risks add beacon ambush and the real-space last leg. The suppression floor stays a mouth rule, keeping warp's formula (as `MouthFloor` if warp.md drops the name; applied in r6: `MouthFloor` everywhere, warp's name is `RouteShell`). D28: tractor push needs AM.
- **Other docs, not fixed here (resolved in r6: warp.md has D15/D16/D18; tractor-beams.md cites this doc's retaliation; exotic-matter.md's Dark Gate text now follows the multi-system ruling):** warp.md (then r5-warp.md) had not yet had D15/D16/D18 applied (still has the 3 s exit announcement and the exit-floor question); tractor-beams.md still says wormholes.md has no tunnel retaliation rule; exotic-matter.md's "single-system v1" Dark Gate text and its open question 6 ("keeps warp cheaper, wormholes costlier") predate D23 and Q5. No perf-relevant additions beyond the notes above. Nothing was built or run, and no repo files were changed.

## Consistency pass (r6) notes

- Floor naming (item 3): `WarpFloor` is gone. The validity row cites warp's `RouteShell`; the floor bullet defines `MouthFloor(body) = max(AtmosphereTop, TerrainMax + 2 km, 0.01·R)` with no hazard overrides; segment, occupancy and black-hole rows use `R + MouthFloor + r` (black hole: `InfallRadius + r`).
- Throat drag (item 5): the inertial dampener cancels it up to its rating and leaks the excess; plating only adds floor gravity; undamped rooms feel it.
- Enums (item 6): `FrameKind` adds `Lagrange`, `BubbleAnchor` adds `Station`, both quoted as "(frames-and-zones)"; `PlacementMode` already canonical.
- Tractors (items 10, 11, D26, D28): force limits point at tractor-beams.md; consent text kept verbatim with tractor-beams.md as owner and an `IncidentRecord` `TractorLock` on acquire; retaliation states this doc owns the strike path and tractor-beams calls it; without AM, Hold is a pull-only tether that still blocks spool.
- Systems (item 15): natural pairs may cross systems and only they do; no cross-system warp; Dark Gate far end in this or another system; sharding by system then SOI subtree.
- Stale refs (item 16): zones.md -> frames-and-zones.md; decisions-r5.md -> decisions.md; the round-5 "Other docs, not fixed here" note and the `MouthFloor` proposal marked resolved/applied. D27 and D33 do not touch this doc.

## Round 6 / r7 consistency notes

2026-11-26. This doc was not revised in round 6; the r7 cross-check applied what decisions D52-D74 and the round 6 docs ask of it. Decisions were not re-litigated, nothing was built or run.

- **D57 (factions-and-law.md):** it defines `FactionId` (a byte), organisations (this doc's "group" and "owner group"), enlistment, charters and key holders, and owns `IncidentRecord`. Managed-gate fee tables may key on its standing bands (members and Honoured at base, Trusted × 1.25, Neutral × 1.5, Unwelcome refused); natural pairs stay free (D23). Redirect contests pool per `FactionId` as before.
- **D55 (combat-and-sensors.md shields):** a raised gravity shield lobe is an `External` projection, struck on the tick it is raised and every 60 ticks; particle and energy shields are never struck. `RadiationShield` is the S class of its `EnergyShield`, which also absorbs wall discharges and storm hits. Lobes, like tractors, do not project through a mouth disc.
- **D56 (combat-and-sensors.md weapons and sensors):** `Sensor {Quality, Range}` is its signature reader, which runs this doc's contest and copy scans; mouths are gravimetric contacts at the stated range; spoofed transponders are this doc's signature copies broadcast as a claim. Wall contacts and rams resolve as its contact hit (`Blunt`, dents before holes, D59). Rounds cross instant mouths and ricochet in tunnels as written here.
- **D71:** this doc names no Control wiring.
- **D66:** nothing here depended on a warp seal.
