# History of warp.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

## Revision (round 5) notes

User decisions D12–D18 and D23 (2026-11-25) applied. Where older text above (Review notes) disagrees, this section and the edited sections win.

- **Route (D15):**
  - The straight lerp path and the line-of-sight rejection are gone, along with manual via points and chained legs.
  - The server builds a C¹ Hermite route through live knots: the departure, ≤ 4 automatic waypoints each fixed in the inertial frame of the body it avoids, and the drop point.
  - `WarpFloor` became `RouteShell`, a routing clearance only. The ~90 km chord-sag limit is gone. "No clear route" is the only route rejection.
  - `WarpRoute` was added to the data structures, and the waypoints go out in `WarpPlan`.
- **Real-space final segment (D18):**
  - New phase `RealSpace` from TDrop = T1 − 2 s. The ship drops 2 km short of E at 2 km/s relative to the exit track and is braked by the drive field (1,000 m/s²) to U at T1.
  - Commit moved to TDrop − 3 s (T1 − 5 s), and the δ blend ends at TDrop. The ring standoff now covers the 5 s.
  - The ship is a dynamic `LinearCast` body: collisions do damage both ways, and contact impulses reach the interior while the brake doesn't.
  - Commit clears the corridor capsule of everything it can coast-predict. At TDrop only the ship's own drop sphere is cleared. Anything that moves into the announced corridor is hit.
  - The new overrun rule picks `d_r` (tangent to rings, along-track for orbits, horizontal for surfaces) so a failed or cut brake's 10 s ballistic path misses grids that didn't consent (round 5 review: gravity included; terrain never moves the exit).
  - The exclusion push still applies, to E and the whole corridor.
  - Attach is now at TDrop against the corridor capsule, and the brake counts in `MaxAccel` for the predictive merge.
- **No exit floor (D16):**
  - Departures and drops are allowed at any altitude and in any atmosphere, limited only by `ExitClearance` (terrain + ship radius + 100 m, not submerged).
  - The new `WarpBlast` model: yield `f_air(ρ)·½·m·v_r² + 2.5·p·V_hull`, overpressure from a Sadovsky fit with Sachs scaling at the receiver ×2, and data thresholds for glass, structures, wildlife and people.
  - Worked table for a 2,000 t ship. `QuietAltitude` (≈2.7 km for that ship) is the default exit altitude for surface targets.
  - Damage goes to the shared `IncidentRecord` (blame, fines, hostile act if a player dies). An interdictor is liable for drops it forces.
- **Arrival beacon (D18):**
  - Replaces the crew-only/3 s privacy model and `WarpInbound`.
  - Civilian drives always beacon, military drives can switch it off, and a cut wire or antenna gives `Tampered`, a fine and full blame.
  - Sent at TDrop − 30 s (or T0) to everyone within 200 km of E, to the target's owner and traffic control, and to the planetary authority for atmospheric drops. Re-sent exact at commit.
  - The ring point is now fixed at `TBeacon`.
  - Anti-camping re-checked: the departure side still learns only a direction. Dispersion, exclusion zones, lane policing and intruder liability remain. Beaconed civilian arrivals are now interceptable near the exit, which is accepted as the safety trade, and silence costs a fine or needs a military drive.
- **Mid-warp (D17):**
  - Crew move freely and interact with anything aboard.
  - The drive has wired ports (power, control, nav data, beacon; round 6, D71: power and data only), and a cancel table lists soft cancels (abort signal, interdiction; braked) and hard cancels (power, damage, core ejection, support loss, runaway; unbraked, except that support loss keeps the brake if the remaining drives hold power).
  - Every cancel before commit becomes a 3 s-announced drop on the original route. After TDrop only the brake can be lost: the deliberate-ram lever, bounded by the overrun rule and logged as a hostile act.
  - Portals, undocking and launches stay sealed until T1 + 1. (Superseded in round 6 by D66: doors lock, collars and clamps latch, nothing is sealed.)
- **Runaway and core ejection (D12, D13):**
  - A mid-warp runaway (only by sabotage or interior damage; the override is refused in `Warp` placement) is a hard cancel. Its burst is held to `T_exit + 181` (round 5 review; was the first real-space tick).
  - Core ejection works mid-warp: queued in cruise, the core separates on the first real-space tick, unbraked, and bursts about 3 s later, 60–120 m from the ship (r6: separation at `max(T_exit + 1, command + 30)`).
- **Multiple drives (D14):**
  - New section and `WarpDriveSet`. Drives join at power fraction ≥ 0.5.
  - Quality is the power-weighted average (VCap also scaled by fraction). `MassCap` sums, spool is the max of the drives, and fuel is split by weight.
  - The profile stays fixed. Losing a drive cancels only if support drops below the carried mass.
  - Flagged for exotic-matter.md: its single-chamber-per-spool assumptions need a matching note.
- **Travel economics (D23):** a role note and tuning checks under Speeds:
  - natural wormholes are cheapest where they go;
  - manufactured wormholes are fastest;
  - warp is the cheap way to a specific nearby place, in-system (a light year at `VCap` is ~110 days).
- **Updated** to match all of the above: networking (`WarpBeacon`, `WarpBrakeLost`, `WarpBlast`, larger `WarpPlan` and `WarpArrival`, interest rows), data structures, per-tick costs, frames-and-zones.md changes, milestones, files, tests and open questions (five old ones closed, four new ones).

All new numbers are proposals and knobs. Nothing was built or run.

## Round 5 review notes

Lead review, 2026-11-25, against decisions.md (D12–D18, D23 warp side). Checked each decision against the edited text, with the other pivot copies (r5-exotic-matter, r5-ship-design-and-construction, r5-frames-and-zones, r5-wormholes, tractor-r2, r5-planets). Fixes are already made in place above.

**Decisions verified as faithful:** D14 (bubbles support each other, quality power-weighted, `MassCap` sums), D15 (automatic smooth route around bodies, no via points), D16 (no altitude floor, `WarpBlast` with fines and blame), D17 (free crew, wired drive, many cancel sources), D18 (beacon with a military switch or a cut wire, last 2 s in real space as a colliding body), D23 (natural cheapest, manufactured fastest, warp for close specific trips).

**Fixed:**
- **Runaway timing contradicted exotic-matter and broke D13.** The burst was held to `max(burst tick, TDrop')` while the ejected core separated on that same tick. With N = 180 a cruise-started runaway's burst fell exactly on TDrop', so the eject gave 0 m of separation. Now every burst due in `Warp` placement resolves at `max(burst tick, T_exit + 1 + 180)` (exotic-matter's lock-in deferral), and queued purge or eject runs at `T_exit + 1`. The eject gets its normal ~50/100 m (r6: 60/120 m), and the purge its 120 ticks. `RunawaySignature` goes public on the first real-space tick. A bound was added: a warp-delivered burst falls at T1 + 1 s or later, ≥ 300 m off the target's hull, beyond the ≤ 120 m near field. **exotic-matter.md line "A runaway that starts in cruise … held to `max(burst tick, TDrop')`" must be changed to match.** (resolved: applied in exotic-matter.md)
- **D12 override mid-warp.** The cancel table and runaway text said an override could start a runaway mid-warp. Exotic-matter refuses the override in `Warp` placement, keeping the 10 s public arm in real space. Aligned.
- **Multiple drives (D14) exploit:** weight was the raw delivered watts, so overfeeding one high-`VCap` drive bought it unbounded weight. Now `w_i = f_i · PRated_i`, capped at the rating. Also capped at 8 supporting drives (`WarpDriveSet`); ship-design's `DriveGroup` (FixedList64<int>, ~15) must cap at 8 too. (resolved r6: one struct, `WarpDriveSet`, capped at 8 everywhere)
- **Route waypoint bug (D15):** `c + n̂·1.15·RouteShell` used the shell's altitude as a radius, which put waypoints inside the planet. Now `1.15·(R + RouteShell)`. Hazard bodies (the black hole's 2,200 km sphere, exotic-matter) override `RouteShell` and refuse exits.
- **Overrun rule ignored gravity** and doubled as a terrain floor. The straight 20 km line missed the ~490 m fall of an unbraked ship over 10 s at 1 g, and raising the exit to clear terrain was a hidden floor (D16). It is now a 10 s ballistic path tested against grids only. A ground impact is the pilot's risk, shown orange in the preview, and filed as a permanent impact edit (D32, planets.md huge tier).
- **D23 interstellar claim:** "a light year takes ~110 days" implied that warp could cross systems. Systems are separate Solar roots (frames-and-zones.md), so there is no `Lca`, and cross-system plots are refused. The natural-wormhole tuning check was vacuous, so it was restated.
- **Hull sealing (D17 × D11):** only portals were sealed, so a hole cut mid-cruise was a way out of the carried subtree. Now every hull opening is closed from T0 to T1 + 1. Jettison and eject are queued to the first real-space tick, and anything that leaves then is unbraked. Openings open at once with no brake. (Superseded in round 6 by D66: openings stay open, the bubble has a pocket and a wall; jettison and eject stay latched.)
- **Beacon (D18) loopholes:** an unpowered antenna is now `Tampered` (a breaker was a free silence). Liability goes to whoever the `Logged` input names, so a beacon shot out by a hostile doesn't fine the victim. A fleet beacon no longer lists silent members, but they ride inside the announced union corridor.
- **Warp-blast griefing (D16):** "public by default" let anyone airburst a public surface base at 250 m. Station permission may now carry lane and altitude conditions, enforced by the existing exclusion push. This is the owner's policy, not a global floor. New open question 6 covers ungridded ground.
- **Cross-doc wiring:** drive ports now name ship-design's `Hold` FailSafe loop, Data `Cancel` and local panel (an engineer can always stop the drive at the drive). The tractor lock threshold (`m · 0.5 m/s²`, sweeps never block) now comes from tractor-beams.md.
- **Numbers re-checked:** speed table, fuel ratio, blast table, footprint radii and the 2.15× mass scaling all reproduce. `QuietAltitude` corrected from 2.6 to ~2.7 km (Z ≈ 27.6). Standoff at 3 g corrected from 475 to ~470 m. Stale "T1" occupancy and bubble references in the old Review notes are now marked TDrop. The interdiction "Forced exit" heading was renamed so it doesn't read against D16.

**Flagged for other docs (not edited here):**
- exotic-matter.md: the cruise-started runaway hold (above). (resolved)
- wormholes.md: it still names `WarpFloor` in its Occupancy core and floor sections, and its own round-5 note says `MouthFloor`. Pick one name. Warp no longer has a `WarpFloor`. (resolved r6: wormholes use `MouthFloor`)
- ship-design-and-construction.md: linked drives are said to group by the same `Engage` signal in one place and by `Sync` ports in another, and the JSON has `Spool`, not `Engage`. Pick one. Also the 8-drive cap. (resolved r6: `Engage` is a Data input; 8-drive cap)

**Perf of new items:** the ballistic overrun path is 32 capsules per commit (µs, one-shot). Runaway deferral, beacon tamper logging and fleet beacon filtering are event-driven. Nothing new runs per tick. All costs are still estimates, and nothing was built or run.

## Consistency pass (r6) notes

Applied from the r6 conflict list and decisions D26–D28, D33. Edits in place:
- **Linked drives (D14):** one joining rule (root grid or wired collar, reachable from the Data `Engage`, `Integrity` ≥ 0.5, drive out of cooldown, Hot or primable within spool, fuel plan passes, `f_i` ≥ 0.5); weights `w_i = min(P_i, PRated_i)` with `Σw·X/Σw` averages; cap of 8; per-drive cooldown plus a per-root-grid `ShipWarpCooldownEnd` (anti-chaining) in the state table, the bullet, validation and data structures; `WarpDriveSet` gains `SupportKg` and absorbs exotic's `DriveGroup`; `PowerRated` renamed `PRated` with the D:C:B:A = 1:4:12:40 class note and a `PRatedBase` knob; new test "chain A then B is refused until A's cooldown ends".
- **Ports:** `Engage` is ship-design-and-construction's Data input; Abort comes from the `Hold` Control loop, Data `Cancel` or the local panel. (Round 6, D71: `Hold` is a data line.)
- **Runaway and eject:** bursts due before the first real-space tick resolve at `T_exit + 181` (TDrop, TDrop' or throat exit); the eject's 30-tick sequence runs in cruise and the core separates at `max(T_exit + 1, command + 30)`, giving 60 m (120 m for B/A); purge queued to `T_exit + 1`; the D12 override is refused in `Warp` and `Throat` placement. Revision note numbers updated to 60–120 m.
- **Enums:** `PlacementMode` adds `Throat` (frames-and-zones owns it).
- **Shared `IncidentRecord`/`IncidentKind`** (rings-and-asteroids.md) replaces "incident record" and the `WarpBlast` cause; tamper, brake cut, collision and blast use `BeaconTamper`, `WarpBrakeCut`, `WarpCollision`, `WarpBlast`; runaway liability is `AmBlast`.
- **D26/D28:** a Hold or Haul lock on another party's ship without consent is a `TractorLock` weapon use, so anti-escape locks are always hostile; a pull-only Hold tether without AM still blocks spool under the `m·0.5 m/s²` rule.
- **D27:** the exclusion push for non-permitted arrivals and a station's interdiction ban both hold during sieges (exclusion zones, anti-camping, interdiction).
- **D33:** already cited in Liability; no change.
- **Names:** "Changes to zones.md" is now marked as merged into frames-and-zones.md; `gas-giants.md` → planets.md "Gas giants"; `ship-design.md` → ship-design-and-construction.md; `zones.md` → frames-and-zones.md; `decisions-r5` → decisions.md. Round 5 "Flagged for other docs" items marked resolved.

## Round 6 notes

User decisions D53, D58, D66 and D71 (2026-11-26) applied in place. Where older text or the history sections above disagree, the edited sections and this list win. Nothing was built or run; every number is a knob and every cost an estimate.

**Edits:**
- **D66, warp space is real space.** Round 5's "the carried subtree is sealed in warp" is deleted. New block "The warp bubble is real space" under "Warp states":
  - The drive seals nothing: no opening collides as closed, and no air is held. Ambient is vacuum from T0 to the first real-space tick, exterior edges flow throughout, and barriers and emergency shields work as at rest. A holed ship loses air in cruise.
  - What is still held is mechanical: collars and hangar clamps latch until T1 + 1; jettison racks and the eject cradle latch until the first real-space tick (exotic-matter.md's deferral is unchanged); exterior doors lock from spool to T0 and from TCommit to the end of the brake, and are free in cruise.
  - **Proposal, flagged (open questions 7 and 8):** the bubble has a pocket (`WarpShell`, hull bounds + 20 m) of ordinary vacuum in the grid scene. EVA on the hull in cruise is allowed. Anyone gripping the hull at T0 goes along, crew or not, so boarding in a warp means riding the hull; nobody arrives from outside in cruise. A body that crosses the wall in cruise is lost; in a braked real-space segment it leaves unbraked into the exterior bubble; with no brake the shell ends.
  - Spool no longer blocks for crew on the hull, only for crew floating off it.
  - Cost: nothing per tick (the shell test replaces the grid leave rule in the 10 Hz membership check; two ambient events per warp).
- **D58, warp exits as a weapon.** Recommendation, exclusion zones, overrun rule, blast resolution, beacon, UX, validation and commit now cite factions-and-law.md:
  - raised region shields are exclusion volumes for E and the corridor (`WarpRegionExclusion`), pushed to shell + ship radius + 300 m; a drained shield excludes nothing; the overrun rule treats a raised shell like terrain;
  - a shield scales the blast for receivers inside its dome, inside the existing `WarpBlast` resolution;
  - `WarpAttack` and the fine schedule are the law doc's. Warp supplies `Ring035M`, `BelowQuiet` and `RingCovered` on `WarpBlast` from one commit-time query (drops below `QuietAltitude` only);
  - the preview and the beacon footprint gain the 0.35 bar ring; `MilitaryOff` is lawful only where the commission is recognised; shielded settlements in the footprint get the beacon; the permission hook is `ZonePermission.Check(.., WarpArrive)`; `IncidentRecord` is owned by factions-and-law.md.
- **D71, power and data only.** The drive's ports are Power in, Data in (`Engage`, `Plan`, `Cancel`, `Hold`, `BeaconSwitch` on military drives), Data out (`Beacon`), and the local panel. `Hold` is a one-way data line with e-stops in line; a dead line is an abort and blocks spool. The cancel table, the joining rule ("power and data wiring" at collars) and Files follow.
- **D53, AI pilots.** New section "AI pilots": `WarpPlotStyle` (plot time, abort delay, exit altitude factor, accept flags). Skill limits and style chooses among what a player could choose; route, validation and commit are unchanged. No AI pilot commits a `RingCovered` drop.
- **Open questions:** 4 and 6 closed (law doc; D58). Recorded as decided: air in warp (D66), wiring media (D71), AI pilot hook (D53). New: 7 (the bubble wall) and 8 (hull riders and boarding). 1, 2, 3 and 5 remain.
- **Also:** data structures (`WarpTable.ShellMin/ShellMax`, `WarpBlast` fields, `WarpRegionExclusion`, `WarpPlotStyle`), a `WarpShellLoss` event, cost rows, M8 text, `src/Physics/WarpShell.cs`, and tests for the bubble, region shields, `Hold` and AI plots. Superseded lines in the round 5 history are marked, not removed.

**Changes other docs need (not edited here).** r7 consistency pass: the ship-atmosphere.md, frames-and-zones.md, exotic-matter.md and factions-and-law.md items below were applied in the staged docs (factions-and-law.md rules on the bubble wall and hull riders; exotic-matter.md confirms a trap thrown past the wall is destroyed with no burst); ship-design-and-construction.md had applied its items in round 6. From combat-and-sensors.md: `WarpDepart` goes to contact holders and gravimeters, and a silent commit reaches gravimeters as a bearing (Networking table). ai-ships-and-crew.md and decisions.md are outside this pass.
- **ship-atmosphere.md:**
  - "With: Warp" row: delete the zero-flow seal and its re-arm event. Replace with: ambient is vacuum from T0 to the first real-space tick (a `Warp` case for `Ambient`), exterior edges flow, barriers and emergency shields draw as at rest.
  - "Other docs: warp.md: the warp seal also stops gas" and the review line "a hard cancel releases the openings": delete. Open question 2 is closed by D66.
  - Airlocks: exterior doors lock from spool to T0 and from TCommit to the end of the brake, not for the whole warp.
  - Wind that carries a body out in cruise leaves it in warp's pocket; past the wall it is lost, with the edge's actor attributed.
- **frames-and-zones.md:**
  - `GridInertial` bullet: delete "exterior portals collide as closed".
  - "Into a ship grid" row: while a `WarpShell` exists the leave rule is the shell test; a crossing in cruise is a loss, not a transfer.
  - "Warp engage" row: grid members on the hull are carried.
- **ship-design-and-construction.md:**
  - D71: the warp drive's `Hold`, `Eject` and `BeaconSwitch` inputs are declared `"medium": "Control"`; they become Data. `Hold` keeps its FailSafe dead-line behaviour as a one-way line with e-stops in line (the "series loop" wording and `estopLoops` template need restating). Open question 6 is closed.
  - "Linked warp drives": "inside the sealed subtree" becomes "with the cradle latched". Collars carry power and data.
  - Latches that hold in `Warp` placement: docking collars, hangar clamps, jettison racks, eject cradle. Optional harness default: a pressure sensor split to the drive's `Cancel`.
- **exotic-matter.md:** "inside the sealed subtree" (runaway burst, eject in cruise) becomes "with the cradle latched". Confirm that a hand-carried trap or cell thrown past the bubble wall in cruise is simply destroyed, with no burst.
- **factions-and-law.md:**
  - Its warp.md change list is applied. The `WarpAttack` test reads `Ring035M`, `BelowQuiet` and `RingCovered` from `WarpBlast`; warp runs the commit-time query.
  - New cases to rule on: a person pushed or vented past the bubble wall in cruise (`PersonHarm`, as venting at rest), and boarding by riding the hull through a warp (`Boarding`).
- **ai-ships-and-crew.md:** owns the `WarpPlotStyle` values per skill tier and style (`PlotTicks` 600 / 300 / 120 / 60 proposed), folds `PlotTicks` into the schedule's spool leg, and decides whether AI crew do outside repairs or repel hull riders in cruise.
- **combat-and-sensors.md:** no change needed (its warp row already leaves D66's venting alone). Hull riders in the pocket cannot be shot from outside in cruise.
- **decisions.md:** D66 could gain a line once questions 7 and 8 are answered.
