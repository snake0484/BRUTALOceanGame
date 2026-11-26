# History of exotic-matter.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

## Review notes

**User decisions**
- **All nine decisions are present.** Two needed tightening to stay faithful.
  - "Only the most advanced harvest directly": H1 and H2 needed only shields and a tractor. Every black-hole tier now needs T3+ cores; H3 (crystal) is T4.
  - "Harvested in its entirety" holds through `DigDepth = Radius`, and needed the interior gravity fix below.
- **Plating:** "settles the gravity-plating question" became a proposed answer. Q3 is still the user's open question.

**Contradictions with the other docs**
- **Disc trap was impossible.** Wormholes.md's Occupancy body core is `R + MouthFloor + r` (named `WarpFloor` at the time), and a 2,200 km black-hole floor (warp's `RouteShell(BH)`) would cover the whole disc. Interdict catchers can't waive Occupancy, so "a captured end can open in the disc" could never happen. The black hole's core radius is now `InfallRadius + r` (64 km). That also keeps mouths out of the future remnant's volume.
- **Collapse moved announced mouths.** The shock kick and the μ re-derive would move rails mouths, but wormholes.md says B is never moved after announcement. Mouths and wormhole frames are now exempt and keep their elements until `TClose`.
- **Kick cap broken by its own shock.** The black-hole shock was 150 m/s against the shared 100 m/s cap; it is now 100 m/s.
- **Kick cap reworded.** "The flush cap rises to 100" was wrong: `V_flush` keeps its 80 m/s cap, and only the summed kick is bounded.
- **Kick budget.** A per-tick cap let charges on consecutive ticks stack without limit, so it became a per-body budget that refills over 1 s.
- **Stall could void a whole wormhole.** A 60 s stall plus 2× immunity left a 30 s wormhole with nothing, an early collapse in all but name. The stall is now capped at 0.25 · `OpenTicks`.
- **Stall vs the throat hold.** The stall now spells out the hold, `ThroatHoldMax` and `MouthQueue` interactions.
- **Aftermath term removed.** It was a hard Occupancy term evaluated at commit, which let a 4 kg crude charge push a paid wormhole out of a gate's catch slot. It was a cheap interdiction with no catcher, and a sustained gate denial.
- **Interdiction emitter kept power-only.** README and wormholes.md decided warp interdiction costs power only; the draft silently made it consume EM.
- **Charge starvation made deterministic.** "Any tick short" cancelled the charge, and force-curtailing life support in the lock-in window was a hack. Now the charge must be full by `TCommit − 31`, with catch-up draw and a cancel otherwise.
- **AM on every EM debit.** Catchers, `Φ_gate` and natural redirects spent EM without AM, contradicting "every gravity device runs on the reaction".
- **Forced cancel defined.** Wormholes.md never said what happens to reserved EM when a charge is cancelled by disable or destruction. The reservations now return to the generator's tanks.
- **Capital generator size.** Capped at r ≤ 200 m, against wormholes.md's 400 m maximum; corrected to 400 m.
- **Graviton lens bootstrap.** The crystal row listed a crystal lens for the horizon harvester, while H3 is the only pre-remnant crystal source and its own table said throat-grade. That was a bootstrap deadlock; it is throat-grade now.
- **Dark Gate MOID.** A co-orbital orbit with the same elements has MOID 0 and fails natural-mouth validity. It now has an inclination and eccentricity offset, and is kept out of the jet cones.
- **Warp time to the black hole.** "≥ 20 AU, about 75 min" fixed to 20–30 AU, 50–75 min (C class).

**Physics and precision near the black hole**
- **"No bubble size cap is needed" was wrong.**
  - At 20 km the gradient is 5 s⁻²: bodies 500 m apart diverge at 2.5 km/s².
  - That breaks the free-fall rebase rule (it would fire every tick) and the small-relative-velocity premise, and hits Jolt's 500 m/s velocity clamp.
  - Added a tidal bubble cap (one ship per bubble near the horizon) and the tidal bound in `MaxAccel`.
- **Integer `PeriodTicks` rails don't satisfy Kepler's third law exactly.** At a 240-tick period the rounding gives up to ~200 m/s² of spurious acceleration against `g` = 50,000 m/s². Bubble origins in the gradient zone are now integrated in double with the same `g()`.
- **Rails entities could pass through the kill sphere unnoticed.** Destruction is now scheduled from the periapsis when elements change.
- **Remnant interior gravity.** The point-mass `μ/r²` diverges at the centre of a body dug to its core; a uniform-sphere interior model was added.
- **`MinorBody` bubble anchor.** A FreeFall bubble at an asteroid would refit its origin away from the static chunks.
- **Escape figures.** "13 km/s from the disc edge" was really the escape Δv from 20 km. The descent from the disc edge is about 16 km/s.
- **Gradient-torque threshold** raised to 1e-3 s⁻², so remnants (1.5e-4 at the surface) don't pay for it.

**Exploits**
- **EM duplication.** "Purity is a function of the lens" plus Add brushes would let "mine ore, refill with rock, mine again" print EM, and the same on remnants. Ore and remnant materials are now derived only from generator material and are never placeable.
- **Snapshot leak.** Ore stored in chunk snapshots leaked hidden ore to joining clients. Snapshots now store generator material, and ore is re-derived at load.
- **Antimatter as a weapon.**
  - Hold power of 10 kW/g made a 10 g cell need a 720 kg battery; it is now 10 W/g.
  - The breach brush (a 10 g cell made a 65 m hole) is now mainly radiation with a smaller blast, and hits every grid in reach.
  - Traps have a scannable containment signature for station screening.
- **Bomb griefing:**
  - mines had no cap and would self-detonate when their battery died; they now have a per-group cap and expire as duds;
  - casings have a yield cap;
  - dampers protect whatever is inside their radius, not only pulses centred inside it, so a charge just outside 5 km can't stall a station's gate;
  - cascades go one hop per tick.
- **Black-hole events.**
  - Rebirth could spawn a black hole on top of a station, so births are now forecast 7 days ahead with MOID clearance.
  - The infall radius was secret (it depended on the remnant's size); it is now a public 64 km, so camping the collapse is fair.
- **Warp drive overspec.** Lower `EmPerLn` on big drives needed `MassMin`, or small ships would fit A drives for cheap fuel.
- **Inventory fragmentation.** Every mining op made a new stack. Stacks now store content, so auto-merge buckets aligned to every gate can never cost value.

**Arithmetic and economy**
- **Asteroid content was off by about 1,000×.** A 100 m EM asteroid at 10 % fill and q 0.01 holds about 1,500 t of content, not 1.3 t. Fill is now 1 %, giving about 150 t.
- **Disc debris was too rich.** As stated it held thousands of tonnes of field-grade EM per chunk, so it is now rock with 1–3 % EM inclusions.
- **Remnant yield.** The remnant used the gravity-implied density (2.6e5 kg/m³), which would yield 260 t per voxel. Yield density is now separate (3,000 kg/m³), and the surface purity was lowered from 0.35 to 0.08, so the remnant doesn't instantly make all warp fuel free.
- **Throughput.** Stated explicitly that concentrator and drill throughput, not deposit size, is the faucet.
- **The restated "cheaper" invariant held only for r = 25 m.** Wormhole cost grows with r², while the mass of a ship that fits grows with ρ³. With one `EmPerLn` for every class, any ship over about 6,000 t that fits a heavy or capital mouth finds a wormhole cheaper than its own warp, breaking README's "warp cheaper, wormholes costlier". Fixed with per-class `EmPerLn` plus `MassMin`, and an invariant checked per generator class at a reference density (new open question 7).
- **Enrich could lower purity.** For `q_in ≥ QCap` it gave a purity drop and a residue purer than 1; such inputs are now refused, and the residue-below-input test now provably holds.
- **Smaller fixes:**
  - fighter propellant 2.6 t → 3.3 t;
  - shear-damage radius 0.4 R → 0.5 R;
  - asteroid purity "×10" → about 15×;
  - dose units given;
  - `ItemStack` is now 32 B with content;
  - `WarpFuelPlan` needs `unsafe` for its fixed buffer.

**Perf**
- **No new all-entity per-tick cost.** Added rows for:
  - integrated origins (per bubble, gradient zone only);
  - kill-sphere scheduling (on element change);
  - the kick budget (per kicked body, per event);
  - mine proximity checks (10 Hz, per mine);
  - birth events.
- Crystal decay stays lazy; stasis power changes arrive as ledger events.

## Revision 2 notes

What changed against the first draft, decision by decision. The Review notes above are kept as written for the first draft, so their question numbers refer to the old list.

**Decision 1: runaway burst**
- New section "Runaway burst":
  - the line states (Cold, Primed, Charged and the rest);
  - the purity limit `q_lim = MixHi · (1 + Tol)`;
  - four triggers: off-spec mix, diluter breach, chamber rupture, and the owner override;
  - a deterministic damage-to-breach rule, the same component `Integrity` rule now used by traps and anchors;
  - the 3 s alarm with purge, auto-purge, core eject and Cold running;
  - the near-field brush `10 m · Y^(1/3)`, plus a far field through `PulseKick` as an `AgPulse` with source `Runaway`, capped at the 100 kg casing yield.
- **Slurry drives and low-grade feed are immune by rule:** a breach with true `q_feed ≤ q_lim` only disables the drive.
- **Lock-in kept:** off-spec bursts always land before `T0 − 30` (`SpoolTicks ≥ 270` is enforced). Bursts that would fall in the lock-in window are deferred to after arrival with prime yield.
- The first draft's "interlock refuses, no override in v1" became "interlock refuses; the override is the only bypass" (open question 8).
- The reaction makes anti-gravitons, not gamma, so a burst has no dose pulse of its own.

**Decision 2: industrial antimatter**
- The "Sources (proposal)" table and old open question 1 are gone.
- AM is produced only by `AmFactory` S/M/L (station-only) and seeded faction foundries, at 2 TJ per gram (lowered from the draft's 10 TJ so an early fission plant makes about a gram a day). The round trip is 20 %.
- Belt scoops, flare collectors and black-hole jet AM are removed. H1 jet skimming now yields EM dust only (1–4 kg/h). `PlanetParams.Magnetosphere` and the `FlareCollector` core are dropped.
- New "Radiation from annihilation" section:
  - one dose formula with `κ` and `σ0 = 400 kg/m²` for annihilation;
  - `ShieldTable` reused for external sources, and a new baked `InternalShield` for onboard sources;
  - shadow shields as real mass;
  - crew dose thresholds and recovery, a design limit and the Rad view;
  - a station AM-thrust limit.
  - Flares keep `σ0 = 200` on the shared path.
- **Thrusters:** AM thermal light and heavy added as the conventional AM engine. Every AM device has `f_rad` and a shadow shield. Dose examples show standard against crude shielding.
- **New "Antimatter weapons":** torpedo warheads (breach model) and the annihilation lance (muzzle loss irradiates a crude shooter).
- The breach dose formula is now defined (`κ · f_rad · E / 4πd²`).

**Decision 3: drive kinds**
- Warp fuel is rewritten around **class × kind**:
  - slurry (EmMul 1.5, all agent consumed, slurry 0.10–0.25, no line, no runaway);
  - recirculating (EmMul 1.0, feed 0.40–0.85, line 0.25–0.40, R 0.95, prime, line cap and loop per class).
- The old per-class purity windows (D 0.15–0.30 … A 0.40–0.75) and the content-0 "carrier" are replaced by the kind windows and the `agent.mix` item. Slurry is an EM-family item.
- New tables:
  - kinds;
  - classes, with prime, line cap, loop and capex;
  - cost per jump for both kinds;
  - capex against opex at reference prices (recirculating about 35 % cheaper to run, 3–5× lighter, about 3× the capex; break-even about 28 jumps for a 5,000 t hauler and about 460 for a 300 t ship);
  - failure modes by kind.
- "Higher grade slightly more available" is a market knob (`SlurryMarketWeight` 0.8). "Faction decision" is `data/factions` plus faction-keyed options.
- The wormhole "cheaper" invariant now uses the recirculating kind and includes AM. The test logs the AM price at which each ratio reaches 1 (heavy binds at about 0.7 per gram; see Revision 2 review notes).

**Decision 4: pirates**
- New "Pirates and improvised kit" section:
  - crude refinery cores (T1c, T2c; no licence, ship-mountable, unassayed output with 15–20 % spread);
  - mixed fleets (spool sync with priming, alarms to the leader, one runaway cancels the fleet's spool);
  - dual-drive ships (selector, 10 s idle-only switching, no double spool, auto-failover when idle, per-drive damage, the kind in `WarpDepart`);
  - salvage (faction-keyed options inert off-key).
- Supporting model:
  - `ItemStack` keeps 32 B by splitting `Flags` into `Flags`, `SpreadQ8` and `NomErrQ15`;
  - stacks carry a hidden nominal error and a public spread;
  - assay (station and portable), and merge rules that keep assayed and unassayed apart;
  - devices debit true content.

**Decision 5: station anchor**
- New "Station anchor" section:
  - `a_hold = ω×(ω×r) + μr/|r|³`, rate `EmPerNs · m · |a_hold|` (1e-12 kg/N·s, field grade, plus AM);
  - worked numbers for a 1 g, 1000 km planet with a 6 h day at 10, 50, 100, 500 and 1,000 km, the synchronous radius (3,876 km) and 10,000 km, with fall times and impact speeds on release, plus latitude and 0.27 g notes;
  - founding, capacity and overload, reserve and warnings, failure and release, and impulses paid instead of kicked.
- **Frames changes:** `GridFlags.Anchored`, `BubbleAnchor.Station` (static, never rebased), the anchored-zone row, release transfers and impact-wake events, and the `PulseKick` skip.
- The anchor joins the gravity-device table, the containment power class, the tick order (after AM breaches), networking events, per-tick costs, data, C# (`StationAnchor`, `AnchorTable`), milestone S1 and tests.

**Other edits**
- The Recommendation and decisions sections are rewritten to cover the five decisions.
- Bucket edges are now derived by the loader from data.
- The Progression table is updated: AM bought, then made; slurry early, recirculating later; anchors from T2.
- Data, C#, networking, per-tick costs, changes to other docs (warp, wormholes, planets, ship-hulls, frames-and-zones, README), milestones (D1, R1, S1, C1 added), files and tests are all extended.
- **Open questions:** the old question 1 (antimatter source) is removed and the rest renumbered: old 2–8 are now 1–7, and in-text references are updated. New questions 8–13: deliberate runaway, counterplay severity, radiation persistence, anchor scope, dual drives, AM production rate.

All costs are estimates. Nothing was built or run, and no repo files were changed.

## Revision 2 review notes

A skeptical pass over Revision 2 against warp.md, wormholes.md, frames-and-zones.md, review.md and README.md. Every worked number was recomputed (the anchor table by integrating the release trajectories). Corrections are already applied in the text above; this lists what changed and why.

**The five decisions: present and faithful**
- **1, runaway:** present. Pure EM dumped into a drive (the override, or off-spec stock) and damage (diluter breach, chamber rupture) both trigger it. Slurry drives can't run away: dry EM is refused by their sockets, and any slurry, however purely blended, is only over-rich and aborts. Tightened: the "low-grade recirculating drives are immune" extension holds only for **true** purity ≤ 0.42, so only for assayed feed (see the arithmetic fix below).
- **2, industrial AM:** present and faithful: station-only factories, weapons, conventional AM thermal thrusters, shielding mass, crude use irradiating crew and surroundings through the flares' `ShieldTable` plus a baked internal path.
- **3, drive classes:** present and faithful. Recirculating is **cheaper to run** (about 35 % per jump, recomputed: 6.1 against 9.6, and 101 against 160) and **costlier to set up** (3× capex), slurry burns its agent and has no dilution stage and no runaway, higher grade is slightly more available (`SlurryMarketWeight`), and kind is a faction choice.
- **4, pirates:** present: crude refineries raising runaway risk, mixed fleets, dual-drive selector with damage rules and engagement rules.
- **5, station anchor:** present and faithful. The grid stays in `BodySurface` at any altitude, the bubble is static, the rate grows with mass × hold acceleration and is zero at the equatorial synchronous radius, failure drops it with its inertial velocity, warnings and a reserve exist, and approaching ships join its frame.

**Physics and arithmetic errors fixed**
- **Off-spec formula was inverted.** With `nominal = true · (1 + e)`, the diluter dosing by nominal content gives `q_line = q_mix / (1 + e)`, not `q_mix · (1 + e)`. Stock that **understates** its purity is the dangerous one. Consequences: at the default `q_mix` 0.35, T2c feed (20 % spread) can run away (worst case 0.4375 > 0.42), not "sit on the edge"; the UI rule `q_mix ≤ MixHi / (1 + Spread)` was wrong. Fixed with a default `q_mix = min(MixTarget, MixHi · (1 − Spread))` that can't even run hot; raising it (to fit a big charge in the loop) is now the risk the decision asks for.
- **Off-cone dose was 10× too high.** `κ · P_γ / 4πd²` for a light plasma drive at full is 0.27 Gy/s at 100 m, not 2.7 (and 2.7e-3 at 1 km, 2.7e-5 at 10 km). "5 Gy in a few seconds at 100 m" became about 20 s.
- **Capital invariant row used the first draft's price.** At fuel grade (1.2) a 78,000 t A-class ship costs 14.1 per ℓ, not 17.6 (19.9 with AM); ratios are 2.1× / 1.8×. The AM price that breaks the invariant is set by the **heavy** class at about 0.7 per gram (capital about 1.85), not 1.4 by the capital class. Fleet break-evens with AM added (about 9 and 19 ships).
- **Anchor worked numbers all check** (rates, fall times, impact speeds, escape at 10,000 km, r_s, 0.27 g case, latitude). Small fixes: the vertical climb to 100 km is about 1.35 km/s; "over the pole of the synchronous radius" reworded (it is the 45° point at that radius); one M factory needs "just over four", not four, to feed a 35 g/day station; the hovering comparison now includes its 170,000 t a day of propellant. Added the release regimes (impact below about 2,670 km altitude, orbit to r_s and beyond, escape above about 5,140 km) and the impact-point drift.
- **`a_hold` wording.** "Relative to a natural co-rotating orbit" was ambiguous; it is the non-gravitational acceleration needed to co-rotate (centripetal minus gravity), evaluated at the centre of mass.

**Contradictions fixed**
- **Deferral used T1.** warp.md allows abort and interdiction exits mid-warp, so a deferred burst is now at `T_exit + 181`. Chamber ruptures in the lock-in window ("next tick") would have destroyed a locked-in drive; they now defer the same way. Throat placement defers too (immune like warp). Purge and eject are refused while intangible and open at `T_exit + 1`.
- **Dual drives bypassed cooldown.** warp.md checks "out of cooldown" per drive, so a 10 s switch beat a 30 s cooldown. Cooldown is now per ship.
- **Rails in a rotating frame.** frames-and-zones.md has rails only as Kepler elements in an inertial frame, and freezes parked things in `BodySurface`. A ship hovering in the anchored zone with nobody near would have frozen there for free. The zone now sets the frame only for bubble members; unsimulated entities go to `Rails` in `BodyInertial` and fall.
- **Zone vs bubble limits.** `r_station + 2.6 km` can exceed the 4 km hard radius, and clusters splitting off inside the zone were sent to `BodyInertial` while the zone said `BodySurface`. The zone is capped (stations up to about 1.4 km radius; larger is README Q9), and splits inside the zone stay in `BodySurface`. Two station bubbles merging, and zone entry at km/s (R7 prediction), are specified.
- **Release budget.** "Arrival-style ≤ 128" mixed warp.md's arrival (64) and merge (128) budgets; it uses the merge budget.
- **"Costs nothing per tick" vs a 20 ns ledger row.** Anchors are now lazily settled with scheduled warning, empty and failure events.

**Exploits closed**
- **Runaway as a suicide bomb.** "Adds no reach beyond a 100 kg charge" was false: charges have no near-field brush, a runaway does (up to 77 m). The bound is restated honestly, and docked drives can't prime or override (the warp.md collar lock), so the docked-bomb case is gone; what is left is a visible 10 s kamikaze (open question 8). The override also requires feed above `q_lim`.
- **Anchor drop onto a base.** A 10,000 t station released at 100 km is about 2 kt, landing within 3 km of the point below. Added: a public drop footprint checked at engage against other parties' zones (using the maximum rated mass, re-checked on core changes), a 60 s public release countdown broadcast to the footprint, and refusal of owner actions that would starve the anchor (draining the reserve, cutting control power), which were 10 s or instant back doors.
- **Anchored station immunity.** Rams and tractors against the static body moved nothing and cost nothing; they are now debited and count toward overload. Pulse shear still damages anchored hulls.
- **AM radiation griefing.** A heavy AM thermal hovering at a 5 km exclusion edge gives unshielded EVA crew 5 Gy in about 19 min. The station AM-thrust limit now has its own radius (default 20 km), and dose from another party's source is attributed to it.
- **Dual-drive abuse.** Cooldown chaining closed (above); a Hot unselected drive still shows its signature.

**Determinism**
- The kick budget was a float that clients "predict from the same rule"; it is now integer mm/s.
- The anchor rate is computed once in double on the server and persisted as integer ng/s, so saves and replays never recompute it; settlement and event ticks are integer.
- Merged `Spread` is defined as the true-content-weighted mean, rounded up by the server, which is the weighting that actually bounds the merged error.

**Perf**
- Anchors: 0 per tick (lazy), about 1 µs per footprint solve on engage or core change.
- External dose needs a per-source cull radius: a heavy AM thermal at full stays above the 1e-6 Gy/s floor out to about 330 km, so receivers come from the spatial hash and the cost must be measured on a busy server.

All costs are estimates. Nothing was built or run, and no repo files were changed.
## Revision 3 notes

What changed against revision 2, decision by decision (wormholes.md revision 3 carries decisions A–H on its side). The Review notes, Revision 2 notes and Revision 2 review notes above are kept as written; the stall they describe is superseded. Open questions are no longer renumbered: answered ones are struck through in place, so every earlier in-text reference still points at the right question.

**H, crystals and additives**
- **Decay floor:** `DecayFloor = 0.96` (data) bounds both decay and shock. It equals T3's cap, so decayed crystal is throat-grade stock and one T4 pass regrows it. A stack keeps decaying after it leaves the Crystal band until it reaches the floor, then is stable. Lost stasis costs about half the crystal's value, never all of it.
- **Purity cap:** `QMax = 0.995`, global. The loader refuses recipes and sources above it. The Crystal band is 0.97–0.995, and inside it purity buys margin (time and shocks before leaving the band) and price, not device stats (open question 15).
- **Additives:** a new "Wormhole additives" section. Seven families (binder, dilatant, chiral dopant, inverted dopant, damping flux, catalyst, seed lattice) matching wormholes.md's effects, each with its wormhole trade-off and its own recipe at a T3 additive lab, plus a crude ship-mountable lab for three families at lower potency. Additive stacks reuse `ItemStack` with `ContentMg` as active mass (potency, capped below 1). Mixed at the generator at charge start (no pre-doped EM item), at most 2 kinds and 10 % of X by mass, and the generator's 0.85 gate applies on true content with additives as impurity: a full dose needs EM at 0.929 or better. EM consumed by a recipe never reappears as content. Seed lattice is metastable like crystal.

**A–C, the stability fight from the exotic-matter side**
- **Pulses drain stability; the stall is removed.** "Wormhole disruption: the invariant is kept" became "Wormhole disruption: stability drain": `BombK · Y · (1 − d/R_s)²` per mouth, `2 · BombK · Y` inside a tunnel (the review made it 1× and rung down over 5 s; see Revision 3 review notes), dampers block it at covered mouths, a zero collapses early. The stall row, stall limits, immunity, "why not early collapse", the stall data keys and `AgPulse.StallRadius/StallEnd` are gone; `DrainRadius`, `DrainKJ` and the tunnel position replace them. Runaway far fields drain the same way. Worked examples, a cost ratio (about 1 : 24 against an unfed 1 AU tunnel, 1 : 6 against its re-establish) and the griefing bound are restated.
- **Stabilisers:** power only (Feed at class 2, Drain at class 3), outside the reaction like the interdiction emitter; AM only through cores (5 mg/s for a 2 GW kit). Crafted with sealed throat-grade lenses; the support projector also needs a crystal lens, making it T4. They hold no EM or AM.
- **Re-establish:** `0.25 · X`, its AM and 0.25 of the additives, drawn from the generator's own tanks, plus `E_re`. Fleets that expect a bombing carry a quarter-charge spare.
- **Throughput drain:** paid back in power, so about 2.5 mg of AM per GJ through cores (6.5 mg for a 78,000 t crossing). It shapes the band, not the economy.
- **Quote:** `E` now includes `EsMax`, X carries the class factor; examples updated for tunnel and instant. The per-class "cheaper" table prices tunnel class and is unchanged.

**F, graviton projection in tunnels**
- New section with a `gravitonProjection` field (None, Internal, External, per mode) on every function core, and the classification of every device here: tractors, dampers, armed charges, heavy scoops, the harvester, Drain stabilisers, catchers and emitters are struck; plating, dampening, compensators, stasis cases, Feed stabilisers and generators feeding are not; drives, traps, AM thrusters and AM weapons are not gravitational.
- Deploying mines in a tunnel is refused; an armed charge there is struck and detonates on the next tick.
- New "Tunnel hazards for EM and AM hardware": what an offline outage does to traps, stasis cases, drive cores and plating; Cold running advised through unstable bores.

**Other edits**
- Recommendation, decisions, purity table, power classes, device table, runaway effects, bomb effects table, resolution order, networking, data (`qMax`, `additives`, drain keys, additive and stabiliser files), C# (`CrystalRules`, `AdditiveRules`, `AdditiveMix`, `GravitonProjection`, `EmMath.Shock` and `AdditiveBlend`), per-tick costs, progression, rarity knobs, changes to other docs, milestones (W3, X1), files and tests updated.
- **Open questions:** 2 (bombs against wormholes) and 3 (perishable crystal) answered and struck. New: 14 floor and cap values, 15 what purity buys in the Crystal band, 16 additive scope, 17 stabiliser cost, 18 bombs in tunnels.

**Problems found while integrating, and fixes**
- **Additives could smuggle EM past the gate** if a recipe's EM input stayed counted as content in the product. Recipe EM is consumed; additive `ContentMg` is active mass and never counts as EM.
- **Unassayed throat stock** could pass the additive gate on nominal content and fail it on true. The server re-checks at charge start and refuses with nothing spent and no cooldown, the same "plan on nominal, act on true" rule as every other gate.
- **The decay floor sits below the Crystal band,** so "crystal" stacks could sit at 0.965 forever. They are shown as Throat, can't seed or make lenses, and decay continues to the floor, where the stack becomes stable.
- **A damper in a tunnel** was both a defence (it blocks drains at mouths) and a projector. It blocks only at the mouths it covers in normal space; inside a tunnel it is struck and blocks nothing.
- **Mines in tunnels** would have become one-tick proximity fuses after the first strike; deploying them there is refused.
- **Hot recirculating lines in tunnels:** wall discharges are damage brushes, and a burst inside drains up to 200 GJ, more than any player wormhole holds (100 GJ after the review). Documented, with a Cold-running prompt before unstable bores.
- **Re-establish with empty tanks:** the re-establish is a charge start and draws only from the generator's own sockets; stated, and tested.
- **Rails mouths at a black-hole collapse** kept their elements "until `TClose`", but a tunnel's mouths now live to `TEnd`. They keep them until they despawn.

**Unchanged on purpose:** the anti-graviton stoichiometry, warp fuel and both drive kinds, runaway triggers and the lock-in, industrial AM and radiation, station anchors, pirates' kit, black holes and remnants, and every earlier user decision not superseded above.

All costs are estimates. Nothing was built or run, and no repo files were changed.

## Revision 3 review notes

A skeptical pass over decisions A–C, F and H, checked against wormholes-r3 (the revision 3 wormhole doc). Fixes are made in place above; the wormhole doc was edited only where both docs state the same rule (listed under "Changes to other docs", wormholes.md).

**Decisions, checked:**
- **A, energy fight:** present (pulses drain, zero collapses early, paid maximum, re-establish at 0.25 X with its AM and additives). "Power through" was not real against a single pulse; fixed below.
- **B, holders:** present (battleship kit 2 GW / 5 km / η 1, support projector 1.5 GW / 50 km / η 0.7, power only, lenses as capital). Same numbers as wormholes.md. Destabilisers could beat any number of feeders on small wormholes; fixed below.
- **C, natural level and throughput:** present and consistent (`e_m` 33 J/kg, 2.6 GJ for 78,000 t, refilled at 2.5 mg of AM per GJ through cores). Additive trade-offs move the mass-drain bounds; now stated.
- **F, graviton projection:** present and consistent with wormholes.md's trigger and exempt lists (plating, dampening, compensators internal; tractors, dampers, armed charges, Drain stabilisers, catchers, emitters external; Feed stabilisers and feeding generators exempt as proposed).
- **H, crystals and additives:** present. Floor 0.96 (= T3 cap, so decayed crystal is throat grade), `QMax` 0.995, seven additive families matching wormholes.md's seven effects one to one, with identical trade-off factors (`m_X`, `m_E`, `m_S`, `m_C`, `m_L`, `e_m`, `VCrossMax`, `Ψ_s`). Gate arithmetic checked: 0.929 for a full 10 % dose, 0.888 for 5 %, 0.865 for 2 %.

**Units and rates, checked:** the reserve is integer kJ in both docs; `EsMax = 10 GJ · r̂² · m_S`; feed cap `EsMax / 5 s` (2 GW at r = 100 m); kit and projector `FeedW` match; AM per GJ (2.5 mg at η 0.4, 1 TJ/g) gives 5 mg/s per 2 GW kit; quote and re-establish AM (152 g, 380 g, 0.6 kg; 149 g and 24.9 GJ) recomputed and correct.

**Contradictions and exploits fixed:**
- **Power through was impossible against one pulse.** A pulse drained its whole energy in one tick, so feeders could never answer it: the only defence was the 25 % overcharge, and a 100 kg casing (about 150 units) collapsed even a prepared r = 100 m wormhole from up to 15 km away. Pulse drains now **ring down** over 300 ticks (a timed slope, exact in integer kJ, no new traffic: clients add it from `AgDetonation`). Feeds count during it. A wormhole survives a lone pulse iff `D < Ψ₀ · EsMax + P_net · 5 s`: an unsupported one (full, unfed) falls to `EsMax` (10 kg at point blank for r = 100 m, unchanged), a prepared one (1.25 and fed at the cap) holds to `2.25 · EsMax` (22.5 kg). A big enough bomb still collapses an unsupported wormhole up to r ≈ 316 m with one casing, and larger ones with several together.
- **In-tunnel drain was doubled.** A charge 1 m inside the disc drained twice what one 1 m outside drained, the tunnel already removes the damper defence, and owners cannot refuse entry, so the bore was strictly the best place to bomb (10 kg collapsed even a prepared r = 100 m tunnel). It is now the point-blank value, `BombK · Y` (`tunnelDrainMul` 1), and the 100 kg cap is 100 GJ everywhere. Open question 18 reworded; admission filtering offered as an option.
- **Destabilisers outran any feed on small wormholes.** The feed cap scales with `r²` but a kit's drain did not, so one Drain kit (1 GW) beat unlimited feeders below r ≈ 71 m, and one Drain projector below r ≈ 51 m: the smaller ships' wormholes that decision B's support ships exist to hold. Destabiliser drain now shares `FeedCapW`: at the cap it **pins** a wormhole (net zero) and a bomb finishes it, so breaking a held wormhole takes combined arms. At r = 100 m the old "two Drain kits cancel one Feed kit" still holds; a third no longer wins.
- **Fleets cannot carry dampers.** The gravity damper is a station core, yet both docs listed dampers in a fleet's power-through kit. Reworded to "where a station carries them"; whether a ship damper should exist is new open question 19.
- **Re-establish chains got cheaper each time** if "0.25 X" was read against the previous re-establish. `X` is now always the original plan's; the re-establish blend re-checks the purity gate on true content, and a Dilatant mix keeps its boosted `MaxRadius` for the eligibility check.
- **Additives silently moved the mass-drain bounds** ("one reserve per 300 s of flux", "40 % per entry"): binder plus damping flux gives about 160 s and 74 %. Stated as the intended price; entry refusal and the feed cap (about 30× the worst mass rate) still bound it. `m_S` is the only additive path into the energy fight, and it scales the feed cap, bomb thresholds and `E_re` together.

**Left for wormholes.md (not edited, its own structures):** the `Stability` struct needs a per-wormhole ring-down pool (end tick, slope, source `s`), and `CollapseCause.Pulse` should be chosen by the largest active ring-down at `Tz` (the collapse-point rule was edited; the enum is unchanged).

**Not changed:** `BombK`, `EsK`, the feed cap, overcharge, the strike cap (1 %/s), the decay floor and cap, additive recipes and potencies, and every number not named above. All costs are estimates; nothing was built or run, and no repo files were changed.

## Revision (round 5) notes

Applies the user's round 5 answers D9, D12, D13, D14, D20, D21, D22, D23, D24, D25 and D28 (decisions.md). They win over any older text in this doc, including the historical notes above.

- **D9, inertial dampener:** plating and dampening are split. Plating now bills only `|g_plate|` and gives floor gravity. The new `InertialDampener` S/M/L core keeps everything inside its bubble (crew, loose bodies, cargo, covered docked craft) moving with the ship, on graviton emission: `EmPerNsDamp` 1e-11 kg/(N·s) of field-grade content plus 1 W per N of bus power, billed on `m_in · a_d` only while accelerating, rated by `a_max` (3/6/10 g) and `MassMax` (50/500/5,000 t), with leak-through above either and a 0.5 s ramp-down when starved. Interior gravity (README Q3, frames Q1) is now decided. Updated: recommendation, bands, devices table, tunnel table, data, structs, ship-hulls and frames changes, tests.
- **D12, override:** the "pure dump" override is kept as a scuttle or kamikaze, with every safeguard (owner only, feed above `q_lim`, never docked, locked or in `Warp`/`Throat`, 10 s public arm, casing cap, line cap).
- **D13, runaway severity:** the shared damage rule gains a per-housing `Rating`. A recirculating drive has a core housing (rating 1: diluter breach 0.5, valve 0.25), a reaction chamber rated ×8 (rupture at 0) and an eject mount rated ×4. **Core eject is standard** on every recirculating drive (no longer a +240 option; capex unchanged at 2,400), ejects the entire core, works at any core Integrity and off-key, at 20 m/s (D, C) or 40 m/s (B, A) so the near-field brush misses the ship for every line; an auto-eject owner flag is added. `DriveOptions.EjectMount` became `AutoEject`.
- **D14, multiple drives** (superseded by Round 5 review notes; r6: `DriveGroup` folded into warp.md's `WarpDriveSet`): the revision 2 selector ("only one spools") is replaced by `DriveGroup`: participating drives spool together into one bubble, quality averaged by `BubbleW · Integrity`, capacity `Σ MassCap` (the round 5 review aligned these with warp.md: delivered power, `Σ f_i · MassCap_i`), each drive paying its own share by its own kind, any participant's runaway or bad mix aborting the shared spool before `T0 − 30`.
- **D20:** AM energy and production scale unchanged; old open questions 4 and 13 removed.
- **D21, black-hole lifetime:** natural life 5–20 years (was 60–180 days). New **Deliberate collapse** section: the T4 `CollapseInducer` (crystal lens, works from the harvesting band) pours throat-grade EM and AM into a `CollapseWork` budget (2,000 kg content, 10 kg AM, 20 kg/h per inducer, relaxing 2 %/h when unfed); a full budget schedules the collapse 24 h later with public progress events from 25 %; contributors are attributed for the damage. Data, structs, networking, milestone B2 and tests added.
- **D22, EM supply:** the remnant-flood framing is removed. All existing EM is recycled ancient EM in planets and asteroids, which are the main supply for the whole game; remnants are rare, late, local rushes. Recommendation, planet deposits, asteroids, remnant economy, progression row T4 and tuning target rewritten; old open question 7 removed.
- **D23, travel economics:** "warp cheaper, wormholes costlier" is replaced everywhere in this doc by natural cheapest (no EM or AM per crossing), warp the cheap way somewhere specific and close, manufactured fastest and dearest. The per-class invariant is kept as the manufactured-versus-warp half; the fleet break-even is reframed; the warp.md, wormholes.md and README change lists carry the restatement; old open question 6 removed.
- **D24, radiation:** transient plus lingering. Characters gain a lifetime dose with three chronic bands cured only by an expensive `MedBay` treatment; `med.antirad` treats acute exposure (suppresses sickness, faster recovery) without touching lifetime dose. Contamination records (hull activation 3-day half-life, breach fallout 20-day) per room and per terrain chunk, lazily decaying, with decon and contaminated rock stacks. Old open question 10 removed.
- **D25, anchors:** any grid can mount an anchor (`stationOnly` false); activation needs the set position (5 m) and mostly the velocity (≤ 5 m/s residual, paid as an impulse); anchored grids crawl over days (0.05 m/s along, 0.02 m/s vertical, 1 h segments, footprint re-checked, kinematic while crawling); no anchoring over asteroids (tractors hold those); a dropped station is a rare disaster with thrusters unlocking at `AnchorFailing`, an escape-pod rule for impact footprints over atmospheres, re-entry burn-up and fragmentation (proposed Sutton-Graves heating, `BurnUpMassKg` 5 t) and hard ground impact; footprint stays conservative. Old open question 11 removed.
- **D28, tractors:** without AM a tractor emits gravitons only and can **only pull**; push and hold need the AM reaction. A new "Graviton emission" mode (EM and power, `AmPerEm` 0) is defined under The anti-graviton reaction.
- **Open questions:** 2–13 removed as answered (numbers kept so references stay valid); question 1 trimmed to placement only; new questions 20 (plating on gravitons) and 21 (deliberate collapse cost).
- **Not changed here (owned elsewhere):** D17 wiring, D18 warp beacon and real-space tail, D15/D16 warp path and exit rules (warp.md); D26/D27 tractor consent and sieges; D10/D11 flight and hull editing (ship-hulls.md, ship-design-and-construction.md).

## Round 5 review notes

A skeptical pass over the round 5 edits (D9, D12, D13, D14, D20–D25, D28), checked against decisions.md and the round 5 copies of warp.md, frames-and-zones.md, ship-design-and-construction.md and ship-hulls.md, plus the repo's tractor-beams.md. Fixes are made in place above; where the text above and the "Revision (round 5) notes" differ, these notes and the text win.

**Decisions, checked:**
- **D9:** present (plating for gravity, a separate rated dampener on EM and energy, leak-through above the rating). Aligned with frames-and-zones: the dampener cancels the manoeuvre term `a_man` and never bills rest acceleration, so the old "a dampener cancels `a_hold` all the time" is gone; plating bills only the top-up it supplies. Added full-rating bills (an L core at 10 g on 5,000 t is 490 MW, cores only) and how it behaves on a bus shortfall.
- **D12, D13:** present. Chamber ×8, eject mount ×4, eject standard and wired (plus ship-design's mechanical pull handle, so cut wires can't stop an engineer). Eject distances re-checked: 20 m/s × 2.5 s = 50 m against `r_n` ≤ 43 m for a 75 kg C line; 40 m/s gives 100 m against 77 m for 450 kg.
- **D14:** present but it disagreed with warp.md's "Multiple drives" (owner of the bubble): weight by `BubbleW · Integrity` instead of delivered power, `Σ MassCap` instead of `Σ f_i · MassCap_i`, "after the lock-in the bubble holds" instead of the support row, Off/On toggles instead of wiring. Now follows warp.md; `P_i` is capped at its rating.
- **D20–D24:** present. D21's lifetime is 5–20 years, the inducer's AM figure re-checked (10 kg ≈ 116 days of an L factory, 11.6 of the foundry); a leftover "once a quarter" collapse cost line fixed. D22 and D23 leave no blanket "warp cheaper" text. D24's model is sound; contaminated stacks now feed one aggregate record per room.
- **D25:** present. Added the Lagrange target (frames-and-zones' extension) and the asteroid-contact engage refusal; unattended crawls are now `Frozen` with a pose linear in the tick, and crawl segments must clear terrain at plan time.
- **D28:** present. Anchors stay on the full reaction at every pose (a pull can't supply the poleward part); "holds an asteroid" now says tow, with a rigid hold needing AM.

**Contradictions and exploits fixed:**
- **D16 leftovers:** "Exits must still clear `WarpFloor`" for anchored stations and "`WarpFloor` still applies" in the warp.md change list contradicted "no forced floor". Removed; low exits are legal with warp.md's pressure wave and fines. The black hole keeps an exit exclusion (renamed `RouteShell(BH)`), stated as a hazard rule, not an atmosphere floor.
- **D17/D18 leftovers:** "the crew couldn't act during warp", "purge and eject are refused in warp", "damage cannot happen during Cruise" and `T_exit` = T1 predate the real-space segment and free crew. Now: purge and eject can be commanded in cruise and execute on the first real-space tick; interior damage can happen in cruise; `T_exit + 1` is `TDrop` (or `TDrop'`).
- **Mid-warp eject never worked under warp.md's timing.** A breach in cruise hard-cancels, so `TDrop'` is 180 ticks after onset, the same tick as the burst; warp.md held the burst to that tick, and the core separated on it, 0 m from the ship. Bursts held through cruise now resolve at `T_exit + 181`, giving a commanded eject 60–120 m.
- **Graviton power figure:** "the energy annihilation would otherwise supply" was off by about 500× against this doc's own 1 TJ per gram. `GravitonWPerN` is now a stated knob, with tractor-beams.md's old pumped figure as the fallback if pull proves too cheap.
- **Dampener stacking:** four S cores over one room no longer make a 12 g dampener; a docked ship's own dampener idles under its host's.

**Left for other docs (not edited here):**
- **warp.md** *(all applied in warp.md, r6)*: adopt a ship cooldown equal to the longest cooldown among the last warp's participants (per-drive cooldowns reopen revision 2's cooldown chaining); change "only an override or sabotage can start one mid-warp" to "only sabotage or interior damage" (the override stays refused in `Warp` placement, so a full line can't arrive pre-armed); replace "held to `max(burst tick, TDrop')`" with `T_exit + 181`; add the black-hole exit exclusion; set `PRated` watts per class.
- **tractor-beams.md:** drop Pumped mode for D28 pull on graviton emission (new entry under Changes to other docs). *(Applied, r6.)*
- **frames-and-zones.md:** its "(2–8 g, `F_cap`)" dampener mention should read 3/6/10 g S/M/L. *(Applied, r6: frames now cites `a_d`, `a_max`, `MassMax`.)*

**Noted, not changed:** an ejected Hot core shot to rupture is an improvised mine. It is bounded by the prime (1–20 kg) and the casing cap, so it is no worse than a legal charge. With a chamber rating of 8, a no-warning rupture needs sustained, focused fire, which is the intent of D13.

All costs are estimates. Nothing was built or run, and no repo files were changed.

## Consistency pass (r6) notes

- **Linked drives (D14):** weights are `w_i = min(P_i, PRated_i)`, averages `Σw·X/Σw`, `VCap` divided by `Σw`, fuel split by `w_i/Σw`, at most 8 drives join. The ship cooldown is warp.md's per-root-grid `ShipWarpCooldownEnd`. `DriveGroup` is folded into warp.md's `WarpDriveSet` (struct, `src/` list). Warp.md cooldown and `PRated` asks are marked applied, and the Round 5 D14 note is marked superseded.
- **Runaway in cruise:** a deferred burst is at `T_exit + 181`. The rebuttal of warp's old `max(burst, TDrop')` is deleted. The override is refused in `Warp` and `Throat`. Purge is queued to `T_exit + 1`. Eject runs its 30 ticks in cruise and separates at `max(T_exit+1, command+30)`, giving 60 m (D, C) or 120 m (B, A). Warp.md asks are marked applied.
- **Floors:** `WarpFloor` becomes wormholes' `MouthFloor`. A black hole's body core is `InfallRadius + r`. The "warp.md doesn't cover black holes" note now cites warp.md 149/307.
- **Dampener:** `a_d = min(|a_man|, a_max) · min(1, MassMax/m_in)`, also limited by delivered power, EM and `Limit`. `F_cap`, `a_eff` and "2–8 g" are retired. The tidal compensator is this doc's device. Frames and ship-design asks are marked applied.
- **Enums:** `CelestialKind` adds `GasGiant`. Exotic's `TractorMode` is replaced by tractor-beams' `TractorPolarity`/`TractorDrive`.
- **Tractors (D28):** pull is billed on emitted force, with bus power `BaseMW + 1 MW/km·R + GravitonWPerN·F_emit` and heat `0.2·GravitonWPerN·F_emit`. The AM row is renamed "push, or fixed-offset hold (Hold assist)". Without AM, Lock-Hold is a pull-only tether that still blocks warp spool. D26 (tractor consent) and D33 (miner liability) need no edits here: tractor-beams.md and rings-and-asteroids.md own them.
- **Station anchor (D27):** force overload from tractors and rams drains the reserve first. It then raises a public `AnchorOverload` alarm with a grace window (`ForceOverloadGraceTicks`, proposed 60 s; `AnchorState.ForceOverload`). Failure needs both control feeds cut. Capture at the `CommandCore` transfers anchor authority, and cutting bus power never cuts the anchor's containment feeds.
- **Systems:** "single-system v1" is replaced by per-system black-hole slots. There is no cross-system warp, and the Dark Gate's far end may be in this or another system. Open Q1 is reworded.
- **Names:** `decisions-r5.md` becomes `decisions.md`.

## Round 6 / r7 consistency notes

2026-11-26. This doc was not revised in round 6; the r7 cross-check applied what decisions D52-D74 and the round 6 docs ask of it. Decisions were not re-litigated, nothing was built or run. Where the history sections above still say otherwise, the edited text and this list win.

- **D71 (wiring is power and data):** docked ships join a linked warp through collars that carry power and data. Any eject, override or anchor input described as a control line is a Data input with the same dead-line behaviour (ship-design-and-construction.md "Wiring"). "Control feeds" of an anchor are its power feeds and are unchanged.
- **D66 (warp space is real space):** eject in cruise runs "with the cradle latched", not "inside the sealed subtree". A hand-carried trap or cell thrown past warp.md's bubble wall in cruise is destroyed like any lost item, with no burst (nothing resolves outside real space).
- **D55 (ship shields, combat-and-sensors.md):** `RadiationShield` is the S class of its `EnergyShield` (same `σ_shield` term in `RadiationDose`). Gravity shield lobes are `External` graviton projectors and are struck in tunnels; particle and energy shields are `None`. A gravity shield bills like a Reaction-drive tractor (EM and AM per newton-second). The lance and warheads meet shields as that doc's section 3 says.
- **D56 (weapons and sensors, combat-and-sensors.md):** warheads and the lance keep their numbers here (that doc quotes them and adds delivery: missiles, torpedoes, point defence). Proposed there and accepted here: an AM breach brush gains a `Blunt` dent ring out to twice its radius, and an AG pulse's shear goes to the structural impulse pass first (bends, D59), with damage brushes only for what the structure graph cannot express. Containment signature ranges (2 km, 50 km, 50,000 km) are the M signature reader's. Waste-heat figures reported here feed that doc's thermal ledger.
- **D57 (factions-and-law.md owns `IncidentRecord`):** dose from another party's source, a forced collapse and an anchor drop write records of kinds `RadiationDose`, `ForcedCollapse` and `StationDrop`. The station permission hook is `ZonePermission.Check`. `ForceOverloadGraceTicks` is replaced by `EvacGraceS` per station class. A surface claim is a `Claim` with the `Anchor` right. The `data/factions/*.json` schema is owned there; "faction access" on templates and `"licence"` on recipes are its access rule and `ProductionLicence`.
- **ship-atmosphere.md:** the fixed sub-order inside power class 1 stands (breach-shield recharge and barriers, life support, plating, dampeners); breach-shield recharge is requested only while a projector holds no patch.
