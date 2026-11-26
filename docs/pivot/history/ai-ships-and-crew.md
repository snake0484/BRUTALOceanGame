# History of ai-ships-and-crew.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

## Review notes

Lead review, 2026-11-26, against the four user decisions, frames-and-zones, warp, wormholes, ship-hulls and review.md. Fixes were made in place above. Nothing was built or run; every cost is still an estimate.

This section is kept as written. Its question numbers and figures are the first draft's (64 crew, a 200 km promotion range, the 1 km crew-position leak, open questions 1-8); "Revision 2 notes" below says what has changed since.

**The four decisions.**
- *AI ships exist:* present (sections 1, 2, 6). No change.
- *Crew not fully simulated until needed:* present and faithful (C0 and C1 are data and a pure function; C2 only by proximity, hostile boarding, line of fire, interaction or scripted need). One trigger was wrong: any player docking put the grid on `Alert` and promoted the airlock crew. It is now hostile boarding only; a cleared docking promotes nobody.
- *Pilot is a placeholder for the ship AI:* present (the seated figure has no control authority, nothing reads its hands). But the draft wrote "Decided: the AI is a bridge-watch capability", which is the author's extension, not the user's decision. It is now labelled a proposal behind a `helmNeedsWatch` data flag, with the fallback spelled out, and is open question 7.
- *Crew visible through windows, simpler render:* present (section 4). Two rendering bugs fixed: the Near tier was chosen by distance alone, so 3D figures would be drawn inside hulls whose interior had been dropped at LOD >= 2 (tier now follows the portal's hull LOD); and rooms open to view through a breach were culled (breaches are now dynamic portals).

**Consistency with frames-and-zones.**
- `PlacementMode` use is correct and no new mode is added. C0/C1 crew not being occupants matches "interiors without occupants have no bubble".
- **L0 and L1 did not actually agree.** L0 used constant-acceleration segments with no gravity, while frames' L1 thrusting ships run a Verlet integrator under gravity, so "exact by construction" was false and every crossing would have popped. Schedule legs are now guided legs: one closed form evaluated at both levels, with the implied thrust checked and fuel charged at plan time; Verlet is for off-schedule ships.
- The continuity test demanded < 1 m against a never-demoted reference across L2, which real physics cannot meet. Split into an exact L0/L1 check and a state-handover check for L2, with deviation absorbed by `DelayTicks`.
- "AI never anchors physics" overstated frames' rule (contact with something simulated also anchors). Reworded.
- **Caps left intangible ships next to players.** "New AI promotions wait on rails" at the server cap meant a ghost hull a player could fly through. Caps now only hold arrivals outside 3 km; anything already inside 2 km always gets a body, and docked or parked ships do not count.
- Demotion past the 4 km hard radius was not stated. It is now, with pursuit continuing at L1.

**Warp and wormhole traffic.**
- warp.md does not refuse an exit in an exclusion zone; it pushes it out silently. Wording fixed.
- L0 warps skipped warp.md's commit. They now run the plot at departure and the commit as a scheduled event, so the exclusion push-out and occupied drop points apply at every level, and the L0 > L1 lead (60 s, 200 km = `BeaconRange`) guarantees the 30 s beacon comes from an entity.
- L0 hulls in `Tunnel` and `Mouth` frames were never promoted (those frames are outside every SOI). Added. L0 crossings now also count against `TunnelCap` and direction windows.
- Scheduled traffic could fill a 64-slot `MouthQueue` ring or a station's dock ring. Added `aiQueueShare` and booking limits (one per ship, `MaxBookingsPerGroup`, lapse when not on approach), which also stops players hoarding slots to starve a station.
- World events (collapse, capture, drop) now re-plan affected L0 rows.

**Hull rooms and `GridVolume`.**
- `NavNode.RoomId` and `ViewPortal.RoomId` pointed at runtime room labels, which ship-hulls recomputes on every topology change. They are now plan-time room indices, with an `unsafeRooms` mask published by the server.
- "Routes avoid unsafe rooms" needed path-finding that a static `NextHop` table cannot do and that peers would have to reproduce. Replaced by a deterministic hold-at-the-door and muster-post rule.
- L2 > L3 by `GridVolume` (C11) is used correctly.

**Determinism of schedules across clients.**
- `hash` was unspecified (.NET's default hashes are seeded per process); role and template data came from each client's local JSON; walking speed was a float. Now: one fixed integer mix, integer speed, and a server-sent `CrewPlan` blob keyed by content hash.
- Per-person slot phase let two crew be assigned one post, contradicting the doc's own test. Slots are now per grid.
- Whether `rank` skipped the dead was unstated; if it did, one death would teleport a whole role. Rank is the template's.
- Condition changes assumed every walk finishes within 30 s. It does not on a big ship, and the origin then depends on an unbounded chain of earlier states. Epoch origins are now stored (<= 384 B a grid) and sent to late joiners while a walk is unfinished.
- Walks longer than a slot were possible (2 km of `Dist` range against a 10 min slot). The compiler now enforces the bound.
- Crowd count changes popped figures in and out, and the count was not replicated. Now a replicated count that takes effect at a slot boundary through `Entrance` nodes.
- Late reliable events gave doubles or gaps at promotion. Clients de-duplicate by `CrewId`, and non-urgent events are stamped 12 ticks ahead.

**Pops and exploits.**
- *Farming L0 ships for loot:* cargo, replacement hulls and crew kit are all debits from station stock; the one faucet (L0 mining) is capped per route; captured hulls are `Stolen` and cannot be sold to the NPC market, which closed a credit mint (snipe the watch, sell the hull at 0.6). Derelicts no longer sit forever: strays are recovered by the owner after `RecoverTicks`.
- *Duplicating cargo by demotion:* materialisation is now a move with one owner of truth per level; sum-back counts only this hull's item inside its own holds; a save writes one form, never both; foreign items and stowaways pin the hull. Fuel was q8 with unspecified rounding (a yo-yo across the 2-3 km band could round up): now q16, rounded down.
- *Despawning pursuers by leaving range:* pursuit is behaviour state that survives L2 > L1 and a save; a full bubble no longer keeps pursuers out; demotion is refused during combat; rails are stated to be neither shield nor weapon, with the over-4-km case handed to the combat doc.
- *Crew that vanish when boarded:* promotion at the scheduled pose was already right. Added: `CrewDelta` so wounds, taken weapons and restraints survive demotion (backing off 30 m used to heal and re-arm the crew, and re-promotion duplicated their weapons); a full override table makes things wait instead of snapping; a split hull divides its crew by where they were.
- *Killing ambient crew through windows:* a round must defeat the glazing first (same test at C0 and C2), the hit is resolved at the shooter's fire tick, it raises `Alert`, writes a `Hostile` record, and leaves a body with finite loot. Killed crowd members used to walk back out of the schedule function; `CrowdDead` stops that. Sniping the pilot is therefore a breach and an incident, not a free capture, and succession still applies.
- *Starving the C2 cap:* idle players in a station could hold all 48 slots so that a boarding elsewhere met figures that cannot react. Slots are now prioritised (hostile boarding first, proximity last) and parked agents have their own cap.

**Perf at 64 players.**
- `TrafficShips` was claimed at 48 B a row; the listed fields were ~75 B and six leg ends cannot hold a 9-12 leg trip. Now ~106 B a row, 435 KB at the cap. Still trivial; the claim was wrong.
- The client ambient job was misstated by 10x (16 x 8 us is 128 us, not 13) and ignored crowds (256 figures a station grid). Restated: ~80 us a frame worst case, on a job.
- With the 200 km promotion range and event-driven on-schedule legs, L1 holds far fewer ships than the SOI-wide trigger did, the 512 cap is rarely reached, and when it is, eviction is invisible because the wire carries legs, not levels.
- Server totals fit frames' budget (AI is ~5 % more bodies; C2 worst case ~0.85 ms of job CPU on top of ~2 ms for 64 players), but the worst cases coincide in the heaviest scene, so A4's exit check now measures that scene. Added the missing rows (promotion checks, dose, batched damage evaluation).
- 96 free-flying AI bodies across ~16 player bubbles is about 6 each. Workable, possibly sparse; flagged as the knob to measure in A2 (open question 5 covers density).

**Still open after this review.**
- Combat beyond the 4 km hard radius (needs the combat doc). *Revision 2: settled on the frame side (bodies at both ends, each in its own bubble, shots in frame coordinates); only the weapons are left for the combat doc.*
- Escape pods carrying C0 crew (which manifest they belong to after launch) depend on open question 3. *Revision 2: answered with D49 (the pod's own manifest, by the prisoner move rule).*
- The mined-ore faucet needs a finite site budget from rings-and-asteroids to be fully closed; until then it is a data cap per route.
- Glazing ratings, `aiQueueShare`, `RecoverTicks`, `CrowdReplaceTicks` and the 10 % coalescing threshold are new knobs with guessed defaults.

## Revision 2 notes

2026-11-26. User decisions D47-D51 integrated in place, read against ship-hulls (LOD), ship-atmosphere (rooms, suits, life support), frames-and-zones (bubble rules), review.md (P4, P6, C8, C9) and rings-and-asteroids (sensors, dust). Earlier decisions stand. Nothing was built or run; every number is an estimate until `--ai-bench`, `--crew-bench`, `--fleet-bench` and `--fight-calibrate` exist.

**What each decision changed.**
- **D47, player-owned AI crew** (section 3 "Player-owned crew"; section 2; section 6). The crew code never cared who the owner was, so this is mostly rules round it: hiring from a finite station pool against bunks and life-support rating, a wage per watch worked as a scheduled event, one morale byte, a fixed set of ship and crew orders that become the forced option of a `player_crew` behaviour, the helm watch flying the ship when no player sits (the liveness-token question applies unchanged and stays open), private routes as ordinary `TrafficRoute`s, owner liability for everything done under orders, and a logoff rule (finish the trip, dock, stop; fights are seen through; stations keep working). Loyalty is deliberately minimal: no mutiny or betrayal.
- **D48, scanning** (section 4, two new subsections). The first draft's "accepted leak" contradicted this decision outright, so the zero-bandwidth schedule is now a privilege: the schedule **key** goes only to a grid's owner group and, for grids marked open, to everyone. For private grids the server runs the visibility test and sends 12 B walk segments for figures actually in view, and a `ScanCrew` dwell adds per-room results gated by `q = (R_nom / R)^2 x T x S x B` (range, rings' dust transmittance, hull shielding from the bake, baffles): head count, node positions or exact segments. The first result is a snapshot with an age; the track is live only while the dwell holds; a scan never yields the key. A two-seed byte-identical stream test guards the rule.
- **D49, surrender** (section 3 "Surrender, capture and prisoners"). One state machine for people and ships (`Surrendering` > `Surrendered` > `Captured` or `Released`), an AI rule built from existing utility inputs plus a `mercy` value derived from the incident log, a player command, and abuse rules: `Perfidy` for a fake surrender, `KilledSurrendered` and `PrisonerHarm` as aggravated hostile acts, surrender as neither armour nor escape, no ownership change, ransom only from finite treasuries. Prisoners are manifest rows moved out of the origin's `Alive` mask, never copied, at every ship level. Escape pods, left open by the first review, use the same move rule.
- **D50, 200 rendered hulls; data unless in a fight** (section 1, two new subsections). Two separate ladders. *Drawing:* six client stages with count caps (4 interior, 12 shell, 64 whole-ship mesh, 120 proxy, then impostors and points), everything through the shared instance buffer and one indirect-count draw, whole-ship meshes and impostors shared by plan hash, <= 2.0 ms GPU and <= 130 MB (110 before the revision 2 review). *Simulating:* the L0 > L1 range drops from 200 km to 20 km, so far ships really are rows, and an `Engagement` promotes its members only as far as the fight needs: Real (bodies) when a player is party or witness, Stepped on rails otherwise, One-shot with nobody in the SOI.
- **D51, 100 crew** (section 3 cap and "Crew levels inside one grid"). Masks are two words; every dependent size moved (table below). The 20 m sphere is replaced by rings of rooms: characters in the player's room and next door, ambient figures on the rest of the deck, manifest beyond. Active characters per grid go from 16 to 24; the server's 48 does not move, so the cost does not either.

**Reconciliations.**
- **The 4 km bubble and "combat beyond the hard radius".** Float precision limits a bubble, not a fight. Both ends of a shot get bodies, each in its own bubble, and the shot crosses in frame coordinates (a rails projectile handed over by the existing predictor, or a frame-space ray). That needed one change to frames-and-zones: a promoted member of a Real engagement is a physics anchor, in at most 4 / 1 bubbles that hold no player. The first draft's "AI is never a physics anchor" now has exactly that exception.
- **"Rails are neither a shield nor a weapon" still holds toward players.** A player's lock or projectile promotes its target at any weapon range; an L1 ship never fires at a player. Abstract fighting exists only between AI ships, where no player can see the difference, and the cap overflow of a big battle ("background pairs") is by construction more than 3 km from every player and is swapped in the moment a player locks one.
- **Abstract against real.** One fight model serves Stepped and One-shot, generated from headless L2 duels and tested against them, so where a fight is resolved does not change who wins beyond a stated tolerance. Mode changes move state and keep the step index, so they cannot be used to re-roll.
- **ship-atmosphere's rooms.** That doc made rooms design rooms that never relabel, capped at 255 per grid. This doc's "plan-time room ids" and server mapping step are replaced by those ids, and `unsafeRooms` grows from one word (64 rooms, which was already too few) to four.
- **Zero bandwidth survives where it is safe.** Own and open grids still cost nothing for crew motion. Private grids cost a watching client under ~20 B/s and the server ~40 us a tick at 64 watched grids.

**Caps and sizes that moved.**

| Item | Was | Now |
| --- | --- | --- |
| Crew per grid | 64 | 100 |
| `Alive`, `Full`, `Suited` | one `ulong` each | `CrewMask`, two words, 16 B each |
| `unsafeRooms` | one `ulong` (64 rooms) | `RoomMask`, four words (255 rooms); a byte list on the wire |
| `TrafficShips` row / table | ~106 B / ~435 KB (claimed; ~108 B by the fields) | ~120 B / ~492 KB |
| `CrewDose` per grid | 128 B | 200 B |
| Epoch origins per grid | <= 384 B | <= 600 B |
| Overrides / bodies / `CrewDelta` per grid | 16 / 16 / 16 | 32 / 32 / 32 |
| Whole-grid schedule evaluation | ~6 us | <= 10 us (~5 us with the post-room prefilter) |
| `CrewManifestNet` typical / with pools / with origins | ~40 B / 300 B / 700 B | ~80 B / 850 B / 1.5 KB |
| `CrewPromote` | 12 B | 24 B |
| Active C2 per grid; per server | 16; 48 / 12 | 24 (12 listen); 48 / 12 |
| Parked C2 per server; `CrewAgents` cap | 128 / 32; 176 | 256 / 64; 304 |
| Bone loops; bone texture | 8; ~80 KB | 9; ~90 KB |
| Ambient job, worst case | ~80 us a frame | ~95 us a frame |
| L0 > L1 range; demote | 200 km; 300 km | 20 km; 30 km (warp drops still promote 60 s ahead within 200 km) |
| L1 cap, dedicated / listen | 512 / 128 | 1024 / 256, soft (200 drawn hulls within 20 km of a hub must all be entities, and three full hubs already passed 512) |
| `AiShips` size | 608 | 4352 (every hull + 256), so a soft cap never allocates |
| Hull GPU memory for 200 hulls | n/a | <= 130 MB |
| Server total at the caps | ~0.15 ms + <= 1 ms job | ~0.2 ms + <= 2.1 ms job, every worst case at once |

**Considered and not done.**
- *A scan that hands over the schedule key.* It would be free forever after one dwell.
- *Promoting every fight to L2.* A 200-hull battle would need 200 bodies against a cap of 96; and a fight nobody can see does not need bodies.
- *Resolving hits on rails against players.* It makes rails a weapon, which the first review ruled out.
- *Impostors for everything past 2 km.* 200 hulls of unique player plans would need 200 atlases; meshes shared by plan hash are cheaper and look better between 12 and 150 px.
- *Per-person loyalty and mutiny for hired crews.* Not asked for; one morale byte covers surrender and quitting.
- *A `UInt128` mask.* Two named words keep the struct blittable and its layout obvious in the SoA tables and on the wire.

**New knobs with guessed defaults.** `EntityRange` 20 km and the witness hysteresis (30 km, 30 s); `CombatTicks` 600 and the 600-tick fight step; the calibration tolerance (10 % win rate, 15 % hull loss); the stage caps (4 / 12 / 64 / 120) and thresholds (12 px, 3 px); impostor resolution (64 views of 16 px) and 128 slots; ring distances (20 m, 60 m), 24 active per grid and the 24-post validation; scanner ranges, dwell, tier boundaries (1 / 2 / 4) and the baffle factor; `surrenderTicks` 300, custody 120 s, parole 10 min, `noQuarterTicks`, the aggravation factor 3, `ransomMax`; wages, `signOnWatches`, `moraleLow`, `maxAiHullsPerPlayer` 4.

**Still open or waiting on other docs.**
- The pilot as the AI's liveness token (open question 1), untouched by this revision; it now also governs hired crews' helm watch.
- Offline trading, off-screen wars, prize and ransom law, which crews are private, captive-player time and crew loyalty (open questions 2-7).
- The combat doc: weapons, a range and lock limit that fit `EntityRange`, and data the calibrator can run. Until it exists the fight model has nothing to measure.
- The sensor doc: owns the scanner numbers, jamming and decoys.
- review.md P4: the indirect-count draw path does not exist yet (C8). The 200-hull budget assumes it; without it ~3.6k draw records are ~4 ms of recording on their own (review.md's 3+ ms for ~2.7k draws).
- The cost of drawing up to 24 active and 48 parked **full characters** in one grid is the character system's budget, not this doc's ambient budget, and no character doc exists.
- Whole-ship bake time, impostor quality at 12 px and the look of `HullSilhouette` proxies are unmeasured; `--fleet-bench` (milestone H1) is where they are judged, in motion.
- The mined-ore faucet still needs a site budget from rings-and-asteroids (unchanged from the first review).

## Revision 2 review notes

Lead review, 2026-11-26, of revision 2 against D47-D51, review.md (P1, P4, P6, C8-C10), ship-hulls (LOD, limits), frames-and-zones (bubble sizes, anchors, interest table), ship-atmosphere (design rooms) and rings-and-asteroids (`RingObscuration`). Fixes were made in place above; where a draft figure was wrong the corrected text says so. Nothing was built or run. Every cost is still an estimate.

**The five decisions.**
- *D47 player-owned AI crew:* faithful (same three levels, posts and helm watch, orders, stations). One hole: hired hulls were exempt from every AI cap, which is 256 uncapped AI-flown bodies at 64 players. They now have their own L2 cap (64 / 8) with the usual hold-outside-3-km rule.
- *D48 scanning:* faithful in mechanism (range, quality, dwell, counter-measures, no key from a scan). Three leaks and three abuse paths fixed, below.
- *D49 surrender:* faithful (people and ships, AI rule and player command, capture and prisoners). Five abuse paths fixed, below.
- *D50 200 hulls, data unless in a fight:* faithful in shape. The budget arithmetic had four errors and the fight rules had one re-roll and several unbounded promotions, below.
- *D51 100 crew:* faithful and consistent. No live 64-crew figure is left (masks, dose, epoch origins, evaluation costs, wire sizes, `CrewAgents`, station staff all read 100); the only 64s are in the first "Review notes", kept as written and labelled. The ring ladder matches the decision (characters in the room and next door, ambient on the deck, manifest beyond).

**200-hull budget (D50).**
- *Five H0 hulls, not four.* "4 nearest, plus the hull the camera is in" is 75 MB against a table that summed 60. The cap is now 4 with the camera's own hull first.
- *H0 was sized for 100 m hulls only.* ship-hulls' 400k triangles and 10-15 MB are per 100 m ship; a 500 m hull at LOD 0 is ~8k bricks and hundreds of MB, so a count cap alone does not answer review.md P6. H0 is now a 60 MB pool and 400k triangles a hull, met by per-brick LOD nearest the camera.
- *H2's 8k triangles did not hold for big hulls.* "4 m cells over 400 m" is ~30k triangles for a 400 m hull. The cell now doubles until the mesh fits (about longest axis / 50).
- *Components were in no column.* The triangle total left them out (now 100k a hull at H0: 2.9M total, was 2.5M); the draw records assumed ~30 per H0 hull (now <= 512 drawn, ~3.6k records inside the 4096 cull buffer, was 1.7k); and no doc budgets their meshes (16 MB assumed, flagged as a guess).
- *Shadows.* Casting LOD 0 for four hulls is 1.6M triangles a cascade, which 0.4 ms would not cover. Every caster but the camera's hull now casts its shell mesh (<= 0.8M a near cascade).
- *Memory total:* 127.4 MB summed, stated as <= 130 MB (was 110). GPU ms is unchanged at <= 2.0 but is now stated as a budget held by turning caps, on the dev machine's GPU class; no minimum spec exists to check it against.
- *"More than 16 hulls inside 2 km is rare" was wrong.* Docked hulls are outside the 24-body cap, so a busy hub shows 2 m cells at a few hundred metres every visit. Text corrected; it is the first thing `--fleet-bench` must judge, and the H1 cap is the lever (+12 MB for 24).
- *Blueprints for far hulls.* H2 needed the full blueprint (megabytes) for a hull at 5 km. The server now bakes and serves a far-mesh blob (<= 140 KB by plan hash).
- *P4 dependency restated:* without indirect-count draws the hull ladder alone is ~4 ms of recording.
- Verified and left alone: 935 px per metre and every px threshold (300 m, 2 km, 8 km, 31 km); 64 x 8k and 120 x 512 triangles; 130 KB per H2 mesh; the 16 MB impostor array; `HullRecord` 64 B, `HullSilhouette` 24 B, `TrafficHullInfo` 36 B; the GPU ms rows sum to 2.0.

**Data unless in a fight (D50).**
- *Re-roll on entering an SOI.* A one-shot encounter was cancelled and re-run as a fresh Stepped fight when a player arrived: a second roll. One-shot is now Stepped fast-forwarded with the same draws, so arrival replays to the current step and the outcome is identical unless someone intervenes.
- *Draws keyed by member index.* The index shifts as ships join and leave, so one ship's arrival changed everyone's luck. Keyed by hull slot now; `engagementId` is derived or a saved counter; the model is integer (q16), so a save, a replay and a different build agree.
- *Unbounded promotion at range.* Only bubbles without a player were capped. 64 players with 4 long-range locks each could ask for 256 bodies in 256 bubbles. There is now a remote budget (one remote bubble per player ship, 8 / 2 with a party, 4 / 1 without, 32 / 8 remote bodies) and past it a lock is refused rather than granted on a ship still on rails. Refusal is symmetric: an L1 ship cannot fire either.
- *Lock as a griefing tool.* A lock created a full engagement, which pushed a hazard that made every lawful hauler hold or delay: a turret pointed at traffic could starve a station and move prices, and 16 players could fill the 64-engagement pool for 60 s at a time. Lock-only engagements are now provisional (no hazard, 10 s, 4 per group, evicted first), holds inside a `ProtectRadius` time out, and a full pool resolves the stalest fight instead of refusing a new one.
- *Combat anchors pulled in the world.* As ordinary anchors they would have promoted bystanders within 2 km and realised ring asteroids round a fight nobody is in. Restricted to members, shots and wreck pieces, in free-fall frames only.
- *Unguided fire passed through data ships.* Only a lock promoted. A player-fired round now promotes the L1 hull it is about to meet; a beam still passes an unpromoted hull (stated limit).
- *Locked but unhittable.* A long-range lock on a damaged hull could complete before its bake. The lock now completes when the body exists.
- *"Background pairs are beyond 3 km of every player by the hold rule"* was false (the hold rule is for lawful arrivals, not for ships manoeuvring in a fight). They are now swapped in before 2 km. Player-owned hulls outrank AI pairs for bodies.
- *24 members against a 200-hull battle.* Stated: one engagement per hull, a big battle is several.
- *Step spikes.* 64 engagements stepping on one tick is ~3 ms. At most 8 a tick.
- *Player-built hulls have no fight-model row.* The model is per catalog class. Interim: the nearest class's row; real fix needs `FightStats` from the combat doc. Listed as open.
- *L1 cap against the 20 km rule.* "Never L0 inside 20 km" and a hard cap of 512 contradict each other with three full hubs. The cap is now 1024 / 256 and soft, and `AiShips` is sized for every hull so going over allocates nothing.
- *Wire.* A 200-hull battle is not "the same 14 KB/s": 72 more bodies at frames' 10 Hz make ~29 KB/s of a 30 KB/s budget. Bodies beyond 8 km now go at 2 Hz (a change to frames' interest table).
- *Server total* restated as <= 2.1 ms of job CPU with every worst case at once (was 1.3).

**Scanning (D48).**
- *Leak: `LitFraction`.* The head count was folded into every far hull's light flicker, so a private ship gave away an aggregate crew count at any range. Now room lights only, cosmetic flicker, for grids without the key.
- *Leak: manifest events.* Nothing said `CrewPromote`, `CrewDemote`, `CrewOverride`, `CrewEpoch` or a person's `SurrenderState` were key-only. They are, and the leak test covers them.
- *Leak: client-supplied view.* The sight feed's test now uses only the server's eye position and a reference view. Accepted remainder: no occlusion test, so a window facing a viewer within 600 m gives up its room even behind an obstacle.
- *Counter-measures were void at close range.* `(R_nom / R)^2` is unbounded, so any scanner near enough read through any shielding. The gain is clamped at 16.
- *Scan slots could be cornered* (64 a server, no per-owner limit): now 2 per player, 8 per group, 16 kept for AI. *Scan spam:* a broken dwell restarts from zero; `ScanAlert`, hails and `Alert` epochs are rate-limited to one per 30 s.
- *Beyond the decision:* a full-quality scan also shows player characters. Kept, marked as a proposal, added to open question 5.

**Surrender and prisoners (D49).**
- *Surrender to an accomplice.* Protection applied against everyone whoever the captor was, so a losing ship could surrender to a friend and make its real attacker the criminal. Captors are now the engagement's hostile members only, and one captor's release does not bind the others.
- *Surrender as a 120 s escape.* An unboarded surrendered ship was released on a timer, then free to warp. The clock now runs only while no captor is moving to take custody.
- *Flickering the notice.* A free cancel inside the 5 s window made surrender a pause button and a feint. Now a 60 s cooldown and 10 s of safed weapons, and AI keeps firing through the notice.
- *Walking away.* A surrendered person had no obligation to stay. Added, with flight (no record, protection ends) separated from treachery (`Perfidy`).
- *Records, loot, combat log.* Surrender clears none of the surrenderer's own records, opens the holds, and a disconnect leaves the ship or character in place.
- *Two truths for one prisoner.* Restraint was both a `CrewDelta` flag and a `Prisoners` row. Now only the row, with the delta moved into it: a person is exactly one of alive bit, prisoner row, body. Pool-full and prize-crew cases stated.
- *Ransom was a credit faucet.* "Finite treasury" is not defined by any doc, and half a hull's value every `ReplaceTicks` is the mint the first review closed for buyback. Ransom is now paid in kind from station stock with hulls at salvage value.
- *Recruits* skipped the sign-on fee. They pay it.

**Still open after this review.**
- `FightStats` for player-built plans, and with it any real calibration: both wait on the combat doc (D56 asks for one).
- The witness radius (20 km) gives bodies to fights that are 5 px across. It is honest but may starve the 96; 8 km (the H2 range) is the alternative to measure in A5.
- Scene pool memory for 12 remote bubbles (review.md: ~4 MB a scene, unmeasured) and the managed ray cast across bubbles until Jolt exports `CastRay` (P1).
- A component mesh budget and a per-ship component cap belong in ship-design; 16 MB and 512 drawn instances are placeholders.
- No minimum-spec GPU exists, so <= 2.0 ms is checked only on the dev machine.
- The sight feed's missing occlusion test (accepted, bounded).
- **Later decisions already in decisions.md and not integrated here, by instruction (this pass is D47-D51 only):** D53 answers open question 1 (the pilot does matter; pilots differ in style and skill), D54 answers open question 3 (factions fight wars out of view, mostly stable), D55 adds three shield kinds the fight model and the scan formula will have to read, D56 and D57 promise the combat, sensor and law docs this one leans on, D64 (crews not in EVA suits by default) touches the `Suited` mask and the breach casualty rule. The next revision should fold them in.
