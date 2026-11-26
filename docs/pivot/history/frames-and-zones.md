# History of frames-and-zones.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

## Revision (round 5) notes

User decisions 2026-11-25 (decisions.md round 5), applied in place above:
- **Several Solar roots:** frames are a forest, one Solar root per star system, sharing one `FrameTree`; `SystemId`, `TryRelative` fails across systems; the only crossing is a wormhole transit event (new transition row); rails evaluated only for systems with interest; other systems send nothing on the wire; sharding cuts by system first.
- **Game time stops while the server is empty:** `FixedClock` pauses with no player connected; everything time-based is keyed on ticks, so rails, weather, anchor reserves, lifetimes and fines all pause together; tick order step 1 notes it.
- **D9 interior gravity:** plating (floor gravity) and a separate inertial dampener (own volume, EM + power, rating `a_d`; was `a_damp`, renamed in the r6 consistency pass). Fictitious and manoeuvre forces reach the grid scene only above the dampener rating; landing/anchoring is not a manoeuvre (`a_rest`); `WakeLeak` wakes frozen contents; cost integrated per ship; characters' gravity rule updated.
- **D10 flight model:** 6-DOF with flight assist everywhere plus wing lift in atmosphere; airspeed relative to the co-rotating air and the planet weather's wind; rails ships wake at the atmosphere top; cost row added.
- **D25 anchors on any grid:** `BodySurface` or Lagrange targets, never asteroids (tractors); activation needs position within 50 m (superseded by review: 5 m) and velocity within 5 m/s, residual Δv paid in EM over spin-up; slow crawl over days (≤ 0.02 m/s, piecewise-linear pose, kinematic while crawling, rare Station-bubble rebase); thrusters unlock on release; escape pods; dropped stations are rare disasters.
- **Lagrange-point frames:** new `FrameKind.Lagrange`, pose computed each tick from the two railed bodies, created lazily only while an anchor holds there; a Lagrange zone with two-body gravity is the single exception to patched conics; `BubbleAnchor.Station` added to the enum.
- **Huge structures:** several linked grids/bubbles stitched by proxies; double-precision Jolt dropped.
- **Open questions:** all three earlier ones removed as answered; three tuning knobs listed instead.

## Round 5 review notes

Lead review, 2026-11-25, against decisions.md round 5 (D9, D10, D25, Lagrange frames, several Solar roots, time stops when empty). Fixes made in place above:
- **D9 faithful, now consistent with its owners.** The rating is exotic-matter's `a_d = min(|a_man|, a_max) · min(1, MassMax / m_in)`, limited by delivered power and EM and the `Limit` input (r6: `a_damp`, `a_eff`, `F_cap` and "2–8 g" retired), per covered room, stronger dampener wins on overlap. Plating now *adds* floor gravity as exotic-matter says ("cancels nothing"), but tops up to its setting instead of stacking, so a plated landed ship is not at 2 g and pays nothing for gravity it already has. Billing is the force-pass accumulator plus the 10 Hz ledger (exotic-matter, ship-design), on `a_d`; rest acceleration is never billed. Warp's `GridInertial` interior and the real-space last segment (D17, D18: brake not felt, collisions felt and damped up to the rating) are spelled out; "compensator" is exotic-matter's tidal compensator (wormholes and the black hole use it).
- **D25 aligned with exotic-matter, which owns anchors.** The engage tolerance was 50 m here and 5 m there: now 5 m / 5 m/s (`EngagePosTolM`, `EngageMaxVelMs`), thrusters hold the grid during spin-up. Crawl numbers now exotic's (0.05 m/s along, 0.02 m/s vertical, ≤ 1 h segments); the old "every 1 km" steps are gone. Thrusters and warp unlock at `AnchorFailing`, not at the release tick, so a failing station can still save itself. Tractoring an asteroid from an anchored grid stays legal (the user's "use tractor beams"), its reaction debited to the anchor.
- **Exploits closed.** Eccentric-pair Lagrange anchors bill the orbit average but test overload on the orbit maximum and solve the drop footprint at the worst phase. One Lagrange frame per (pair, index), shared; the 64 pool grows instead of refusing, so griefers can't exhaust Lagrange points. A frozen crawling grid plans segments clear of terrain and grids, and a kinematic crawl stops at terrain contact (kinematic bodies pass through statics).
- **Perf.** Unattended crawls stay `Frozen` (0 per tick) instead of exotic-matter's per-tick kinematic update; the cost row says one `MoveKinematic` only while in a bubble. Wind amortisation corrected to 0.05 µs a tick per ship (0.3 µs at 10 Hz), matching planets' 3 µs for 64 ships.
- **Several Solar roots.** `FrameKind` gains wormholes' `Mouth` and `Tunnel`, `BubbleAnchor` gains `Tunnel`; tunnels are extra roots outside any system. "Nothing renders across a system boundary" now excepts the mouth's portal view, which wormholes streams. `FrameTree` capacity (4096) now counts grids and mouths and grows in the maintenance window.
- **Time stops when empty, D10:** faithful; no change needed.
- **For other docs (not edited here) (resolved: exotic-matter now has the Frozen crawls, the Lagrange target, no `a_hold` billing, top-up plating and 3/6/10 g with `MassMax`; the plating/dampener and enum fixes in wormholes.md are in the r6 consistency pass):** exotic-matter should (1) make unattended crawls `Frozen` with piecewise-linear poses, (2) add the Lagrange target to `StationAnchors`, (3) drop "a dampener there cancels `a_hold` all the time" (rest acceleration isn't a manoeuvre), (4) bill plating on the supplied top-up, (5) reconcile its `fc.inertial_dampener` (3/6/10 g, `MassMax`) with ship-design's `fn.dampener` (2–8 g, `F_cap`); ship-design owns the family. wormholes.md says "plating absorbs" throat drag (should be the dampener, up to its rating), and its `FrameKind`/`BubbleAnchor` enums lack `Lagrange`/`Station`.

## Consistency pass (r6) notes

- **Shared enums owned here (item 6):** `FrameKind`, `PlacementMode {Simulated, Rails, Frozen, Warp, Throat}` and `CelestialKind {Star, Planet, Moon, GasGiant, BlackHole, Remnant}` declared in Frames; `Placement` comment lists all five modes and gains `WarpId Warp`. `BubbleAnchor` already canonical. Asteroids are not a `CelestialKind`.
- **Dampener (items 4, 5):** `a_damp` / `a_eff` / `F_cap` retired for exotic-matter's `a_d = min(|a_man|, a_max) · min(1, MassMax/m_in)`, limited by power, EM and `Limit` (S/M/L 3/6/10 g, 50/500/5,000 t). Billing is `m_in · a_d`. The tidal compensator is exotic-matter's. Throat drag and tractor forces are cancelled by the dampener up to `a_d`, never by plating.
- **warp.md zone changes merged (item 7):** predictive merge (gap within 2 s < 500 m, hot pairs every tick) and split (> 1500 m now and predicted, ≥ 2 s since merge); `GridInertial` bullet; Warp engage and Warp drop rows; arrival budget (≤ 64 a tick, never deferred; merge islands ≤ 128); tick step 2b and merge prediction; `WarpTable` in rails; merge-prediction cost row.
- **Cross-system (item 15):** no cross-system warp, manufactured wormholes stay in one system, only natural pairs link systems; sharding by system, then SOI subtree, warp as in-system hand-off.
- **D26-D28 (items 11, 12):** anchor section gains a Sieges and capture bullet: `Release` authorisation and countdown, `AnchorOverload` alarm and grace before failure, capture transfers anchor and crawl authority with no frame change; tractor consent and pull-only without AM noted as frame-neutral. D33 does not touch this doc.
- **Stale notes (item 16):** "decisions-r5" now decisions.md round 5; the review's "for other docs" asks marked resolved; the 50 m engage note marked superseded (5 m).

## Round 6 / r7 consistency notes

2026-11-26. This doc was not revised in round 6; the r7 cross-check applied what the round 6 docs ask of it. Decisions were not re-litigated.

- **D66 (warp.md):** the `GridInertial` bullet no longer says exterior portals collide as closed; the "Into a ship grid" row names the `WarpShell` test and the loss in cruise; "Warp engage" carries grid members on the hull.
- **D57 (factions-and-law.md):** the Sieges and capture bullet points at that doc (`EvacGraceS` is the overload grace); crawl claim checks are its `Claim`s; nothing of the law runs per tick.
- **D55/D56 (combat-and-sensors.md):** tick-order additions (steps 3, 4, 5, 8c, 9), "weapons never merge bubbles" beside the merge rule, and the interest table gated by contact or the eyeball rule.
