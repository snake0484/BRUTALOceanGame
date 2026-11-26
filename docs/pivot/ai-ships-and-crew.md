# AI ships and crew

Status: design, nothing built. Written 2026-11-25 against docs/pivot (README, decisions D9-D37, frames-and-zones, warp, wormholes, ship-hulls, ship-design-and-construction, exotic-matter, tractor-beams, rings-and-asteroids, review). All costs are estimates to be measured with `--ai-bench` and `--crew-bench`; nothing here is a perf claim yet. Lead review 2026-11-26: fixes made in place, listed in "Review notes" at the end. Revision 2, 2026-11-26: user decisions D47-D51 integrated in place (player-owned AI crew, crew scanning, surrender, 200 rendered hulls with far ships as data unless they fight, 100 crew per grid), read against ship-atmosphere.md (D38-D42) as well; listed in "Revision 2 notes" at the end. New bench names: `--fleet-bench`, `--fight-calibrate`. Revision 2 lead review, 2026-11-26: fixes made in place, listed in "Revision 2 review notes" at the end.

## Recommendation in one paragraph

The world is full of AI ships and crewed stations, but almost none of it is simulated. A ship lives at one of four levels: **L0** a schedule entry (~120 B, no entity, zero per tick), **L1** an entity on rails (the existing `Rails` / `Warp` / `Frozen` placements, 0-30 ns a tick), **L2** a Jolt body flown by a ship AI through the same flight computer a player uses, **L3** an interior grid bubble with real characters. Crew live at three levels: **C0** a manifest (16 B header and a 128-bit alive mask per grid; at most 100 crew, D51), **C1** ambient figures that are a pure function of `(crew seed, plan, tick)` evaluated on each client for free, **C2** real characters, spawned only in a player's room and the rooms next to it (within 20 m) and capped at 24 active per grid. The pilot in the seat is a placeholder: a C0/C1 figure with no control authority. The ship AI flies. Proposed on top of that decision (open question 1, a per-class data flag): the bridge watch is the AI's *liveness token* (kill or remove it and the AI loses the helm until a relief walks there), never its operator. Everything that crosses a level is reconstructed from state that persists (fuel, cargo, damage brushes, alive mask, bodies, crew deltas, delay) and has exactly one owner at a time, so nothing heals, refuels, duplicates, teleports or respawns by being looked away from.

**Revision 2 (D47-D51) in one paragraph.** Distance decides how a ship is *drawn*; a fight decides how it is *simulated*; the two ladders are separate. The client draws up to 200 hulls at once through six stages (interior bricks, shell bricks, one whole-ship mesh, a proxy, an impostor, a lit point) with a count cap per stage, inside a budget of <= 2.0 ms GPU and <= 130 MB. The server keeps every ship beyond 20 km as data (an L0 row) unless combat involves it. Then an **engagement** promotes its members just far enough to resolve it: bodies when a player is a party or a witness, a stepped abstract fight on rails otherwise, and that same stepped fight run to its end in one go when no player is even in the SOI (one stream of draws, so nothing re-rolls). Shots cross between bubbles in frame coordinates, which settles combat beyond the 4 km bubble; promotion at range has its own budget, so a far fight cannot be used to load the server. Crew go to 100 per grid with a finer ladder inside one grid: characters in the player's room and the rooms next to it, ambient figures on the rest of the deck, manifest beyond. Players can hire the same crew for their own ships and stations (posts, helm watch, orders, wages). Crew positions stop being free intel: a client holds them only by key (own or open grids), by sight (through glazing or an open door) or by a `ScanCrew` dwell. People and ships can surrender, be captured, held as manifest entries, ransomed, rescued or recruited, with records for fake surrender and for harming the surrendered.

## User decisions this doc implements

- The game has AI ships.
- AI crew are not fully simulated until needed (C0 and C1 are not simulation).
- The pilot is a placeholder for a ship-flying AI (`ShipAi` issues the commands; the seated figure is visual and nothing reads its hands). Tying the AI's helm to that figure being alive is this doc's **proposal**, not the user's decision (open question 1).
- Players see the crew of other ships and stations passing the windows, in a simpler render (C1 through `ViewPortal`s: instanced low-poly figures near, silhouette cards farther, lit-window flicker beyond).
- **D47** Player ships can have AI crew: hired or assigned crew on player-owned ships and stations, at the same three levels, filling posts including the helm watch and obeying orders (section 3 "Player-owned crew", section 2 orders, section 6 wages).
- **D48** Crew positions can be scanned (section 4 "Who may know where the crew are" and "`ScanCrew`"). An unscanned private ship hands no crew positions to any client; the old "accepted leak" within 1 km is gone.
- **D49** People can surrender: crew and ships, as an AI behaviour and as a player option (section 3 "Surrender, capture and prisoners"; the ship side is the `Surrender` action in section 2).
- **D50** Prepare for 200 hulls actually rendered at once. Far ships are simplified further and become data unless they get into a fight (section 1 "Fights" and "Drawing 200 hulls").
- **D51** Max crew is 100 per ship (was 64). Crew on other floors or outside the player's room are simplified (section 3, the cap and "Crew levels inside one grid").

## 1. Ship simulation levels

### The four levels

| Level | What exists | `PlacementMode` (frames-and-zones) | Server cost per tick | Cap (dedicated / listen) |
| --- | --- | --- | --- | --- |
| **L0 statistical** | One `TrafficShips` row (~120 B). **No entity, no `Placement`, no frame.** Position is a function of the row and the tick, evaluated only when asked | none | 0; one heap event per leg change (~0.5 us each, ~15 a second server-wide at the cap) | 4096 / 1024 per server |
| **L1 rails** | An entity with a `Placement`: a guided schedule leg (closed form), coasting Kepler, thrusting Verlet (off schedule only), warp closed form, or frozen (docked, parked). No Jolt body, no bubble | `Rails`, `Warp`, `Frozen`, `Throat` | 0 on a schedule leg / coasting / frozen / warp table; ~30 ns thrusting off schedule; decide at 1 Hz ~1 us only while off schedule (on a schedule leg `FollowTrip` is event-driven) | 1024 / 256 (soft: see L0 > L1 below) |
| **L2 simulated exterior** | A free-flying Jolt compound body in an exterior bubble, the plan's bake (shared by plan hash), `ShipAi` writing `FlightCommand`s, `ShipLedger` and `PowerNet` at 10 Hz | `Simulated` | ~1.5 us Jolt share + ~0.5 us flight computer + ~0.1 us AI average | 24 per bubble; 96 / 24 per server (soft: see "Caps throttle approach") |
| **L3 interior active** | L2 (or L1 in warp) plus a grid bubble and C2 crew agents | grid bubble exists | grid scene step + ~20-50 us per walking agent | bounded by the C2 caps (section 3) |

L0 is the only new level. L1, L2 and L3 are frames-and-zones' existing rules applied to ships nobody is aboard; this doc adds caps, hysteresis for AI, and the state that must survive each crossing.

These are **simulation** levels, on the server. How a hull is **drawn** is a separate ladder on the client ("Drawing 200 hulls", stages H0-H5), chosen by size on screen and count. A hull at any level can be on screen, L0 included (the client draws it from its leg record). Revision 2 (D50) moves the L0 > L1 range from 200 km to 20 km: beyond 20 km a ship is data unless a fight involves it ("Fights").

### L0: the schedule entry

- A **route** (`TrafficRoute`, world data generated from seed at world generation plus authored routes) says: origin station, destination station, ship class, cargo item, period, hull count.
- A **hull slot** (`TrafficShips` row) is one persistent ship working a route. It carries the few fields that cannot be derived: fuel, cargo quantity, damage summary, crew alive mask, `DelayTicks`, `DestroyedUntilTick`.
- A **trip** is a fixed leg list, the same legs L1 runs: `Docked > Undock > ClearZone (sublight to the exclusion edge) > Spool > Warp > RealSpace > Approach > Hold (queue) > Dock > Docked`. Gate routes insert `QueueGate > Cross > Tunnel`. Leg durations come from the plan's stats (thrust / mass, warp profile) and the route geometry at the departure tick, computed once per trip at departure (~5 us plus warp.md's plot) and stored as twelve `uint` leg-end offsets (a gate route has 12 legs). A `Hold` leg's end is not known at departure: the dock or queue booking sets it and shifts the later legs through `DelayTicks`.
- `TrafficSchedule.Locate(row, tick)` returns `(leg, frame, position, velocity)`. Docked and held legs are a station-local pose; sublight legs are guided legs (below); the warp leg is warp.md's `p(t)`. Nothing is integrated at L0.
- **Guided legs (why L0 and L1 agree exactly):** a sublight schedule leg is a closed-form constant-acceleration segment of the ship's *net* motion in the leg's frame. The thrust it implies is `m x (a_net - g_eff(p))`. `PlanTrip` samples that at the leg's start, middle and end, slows the leg until the largest sample fits the skill's fraction of rated thrust, and debits fuel for the largest sample over the whole leg (a small overcharge, never an undercharge). An L1 ship on a schedule leg evaluates **the same closed form** (a `Rails` placement whose pose is written from the leg, zero per tick, as frames' crawling anchor does with its piecewise-linear pose). frames' Verlet thrusting integrator runs only for off-schedule L1 ships (pursuit, flight, a hover, a damaged ship under partial thrust). Without this, L1 (gravity integrated) and L0 (no gravity) would disagree by `g t^2 / 2` over a leg and every crossing would pop.
- **Warp legs obey warp.md at every level.** The leg is plotted by warp.md's planner at departure (D15 route, validation) and its commit runs as a scheduled event at `TDrop - 3 s` (two heap events per warp, nothing per tick), so the exclusion push-out, the corridor capsule and an occupied drop point are resolved the same way whether the hull is L0 or L1. An L0 hull is promoted at least 60 s before any drop within `BeaconRange` (200 km) of a player, so the 30 s `WarpBeacon` always comes from a real entity.
- **World events re-plan.** A wormhole collapse, a closed gate window, a captured or dropped station, a suspended route or a lost dock slot invalidates the affected rows (<= `HullCount` per route): their heap entries are re-planned on the event, or the hull is lost or ejected by the owning doc's rule. No L0 hull flies a leg the world no longer supports.
- **Off-schedule hulls (`TrafficStrays`).** A hull that is `Adrift` or `Derelict` has no legs. With no player near it becomes a stray: frame, Kepler elements and `RecoverAtTick` in a side pool (cap 256). Scheduled events: frames' impact and atmosphere wake events, and **recovery**, when the owner's tug brings it home after `RecoverTicks` (data, 1-3 h played) to a `Docked` leg with a repair dwell paid from station stock. Its cargo, fuel, damage and crew stay as they were until then. At the pool cap the oldest stray is recovered early. Wrecks neither pile up at L1 nor wait forever to be looted.
- **Determinism:** `Locate` is a pure function of the row and the tick (the row itself derives from world seed, route id, hull index, trip index and the events that hit it) and of the celestial rails. Integer ticks throughout. It is server-authoritative and never hash-compared. Clients never own a schedule: for hulls in an SOI they have interest in they receive **leg records** (section 7) and extrapolate the same closed form for display.
- **Per tick:** nothing. A binary heap of "next leg end" ticks pops due rows; arrivals and departures book cargo into station stock there (section 6).

### Promotion and demotion

| Transition | Promote when | Demote when | Hysteresis / extra rule |
| --- | --- | --- | --- |
| L0 > L1 | The hull's located position is within **20 km** of a player (`EntityRange`: frames' 10 Hz interest band, and about where a 100 m hull stops being a point; D50), **or** its next 60 s of legs come within 20 km of a player, of a player's committed warp exit (from that warp's T0) or of a gate crossing a player has booked, **or** it has a warp drop within `BeaconRange` (200 km) of a player in the next 60 s, **or** a player has interest in the `Tunnel` or `Mouth` frame its leg is in, **or** it is a member of a Stepped or Real engagement ("Fights") | Beyond 30 km of every player and every such exit for 60 s **and** on a schedule leg or a stray **and** not pinned (below) **and** not in an engagement | <= 64 a tick, nearest first (an entity and a `Placement`, no body: ~0.3 us each). Invisible by construction, not by distance: a traffic contact and an L1 entity on the same leg are the same closed form on the client (section 7). A hull within 20 km of a player is never L0; the L1 cap evicts only hulls beyond that, farthest first, and an evicted hull stays a traffic contact, so nothing leaves a map or the screen. If more hulls than the cap are inside 20 km of players (five full 200-hull hubs at once), the cap is exceeded and counted in the profiler, as for L2: an on-schedule L1 hull costs nothing a tick, and `AiShips` and the entity pool are sized for every hull (4096 + 256 non-traffic, ~0.7 MB), so going over allocates nothing. Between 20 km and the edge of sight a hull is data: the client draws it from its leg record as an impostor or a lit point (stages H4-H5) |
| L1 > L2 | frames' rule: within 2 km of a physics anchor, **or** predicted closest approach to an anchor's bubble within 2 s under 2 km (warp.md's merge predictor), **or** a player's weapon lock, tractor lock or tracked projectile on it at any range up to `EntityRange` (within the 4 km hard radius it joins that player's bubble; beyond it, it gets a body in its own combat-anchored bubble, inside the remote budget of "Fights"), **or** it is a promoted member of a Real engagement ("Fights") | > 3 km from all anchors **and** contact-free **and** >= 5 s at L2 **and** no damage, lock, tractor or incident involving it in the last 10 s **and** not `CombatUntilTick` (waived once its engagement has no player as party or witness: the fight carries on Stepped at L1). Past the 4 km hard radius of its bubble it leaves that bubble (frames' size limit): into its own combat-anchored bubble while it is a member of a Real engagement, otherwise to L1 (see "Pursuit and combat across levels") | <= 4 ship promotions a tick (inside frames' 32-transfer budget; warp arrivals keep their own pre-booked budget). A damaged hull's bake starts when it comes within 10 km closing, so the body is ready at 2 km |
| L2 > L3 | frames' rule: a character is in the grid (`GridVolume`, C11) or grounded on it. **C0 and C1 crew are not occupants and never create a grid bubble** | no player character in the grid for 20 s and no C2 agent pinned (section 3) | the grid scene returns to the pool; C2 agents demote first |

- **AI is a physics anchor only in a fight a player is party to or can see.** A ship is L2 because a player (or something in contact with what a player made simulate: frames' contact rule) is near, or because it is a promoted member of a Real engagement ("Fights"). Everything else stays data: an AI convoy fighting AI pirates with no player within 20 km has no bodies and is resolved abstractly. **Change to frames-and-zones (D50):** a Real engagement's members are physics anchors (a *combat anchor*), in a capped number of bubbles that hold no player ("Fights", the remote budget: 4 / 1 for fights with no party, 8 / 2 for players' targets at range). **A combat anchor is a restricted anchor:** it gives bodies to engagement members, their shots and their wreck pieces and to nothing else. It does not promote bystanders, realise ring asteroids or build a terrain collision ring, so a witnessed fight costs its members and no more. For the same reason combat-anchored bubbles exist only in free-fall frames: a fight in a rotating surface band with no player within 4 km has no ground to stand on, and stays Stepped with cosmetic fire. Nothing else about the anchor rule changes.
- **Caps throttle approach, never tangibility.** Nothing within 2 km of a player is ever intangible. At a cap (24 free-flying AI bodies in a bubble, 96 on a server) an arriving lawful AI ship holds on rails **outside 3 km** (a `Hold` leg) instead of entering. An AI ship already inside 2 km when a player appears (a player drops out of warp beside a convoy) is promoted regardless; the overflow is absorbed by sending the farthest lawful AI ship that meets the demotion conditions out to a hold, and if none qualifies the cap is exceeded and counted in the profiler. Docked ships are children of the station's compound and landed-parked ships are `Frozen` statics (frames): they are not free-flying bodies and do not count.
- **Priority at a cap.** Ships hostile to or pursuing a player, security responders, and ships a player has locked, tractored or boarded outrank lawful traffic: one of them entering a full bubble displaces the farthest lawful AI ship to a hold. A bubble full of haulers is never a shield against pursuers.
- Player-owned and player-crewed ships are never counted against, or refused by, the AI caps. That includes a player's hull flown by its hired crew (D47); those are bounded by `maxAiHullsPerPlayer` instead (section 3). That bound alone is 256 more bodies at 64 players, so player hulls **under AI helm with no player aboard** have their own server cap of 64 / 8 at L2: past it the one farthest from any player character holds on rails outside 3 km, by the same hold rule and the same tangibility guarantee (inside 2 km of a player it always has a body). They are player ships in frames' body budget, not part of the 96.
- **Pins (never below L1, persisted as an entity):** a player-owned hull, crewed or not (D47); a player character aboard, online or offline (warp.md's offline crew); a player-owned item, body or docked ship aboard; a prisoner held by a player (section 3); a player's tractor lock; items in a hold that are not the hull's `CargoItem` (a stash, a bomb); a damage layer that does not coalesce to 64 brushes within 10 % of its removed volume. Pinned hulls count against the L1 cap first and are never evicted.

### Pursuit and combat across levels

- **Leaving range does not despawn a pursuer.** `Pursue`, `Attack`, `Escort` and security response are behaviour state in `AiShips`, not a property of L2. A pursuer that falls outside 3-4 km continues at L1 on the thrusting rails integrator along an intercept solution (decide at 1 Hz) and is promoted again by the ordinary 2 km / 2 s predictor. It gives up only by behaviour data (`pursuitMaxS`, fuel floor, lost-contact time, the target entering a zone where it may not fight), never because of its level, and it cannot demote to L0 while pursuing (it is off schedule).
- **Rails are neither a shield nor a weapon.** An L1 ship cannot be hit and cannot fire (warp.md: a rails arrival can't collide while on rails). So demotion from L2 is refused while `CombatUntilTick` runs with a player as party or witness, or within 10 s of a damage, lock, tractor or incident, and a player's lock or tracked projectile promotes its target at any range up to `EntityRange`. **Engagements beyond 4 km** (long-range weapons) no longer wait for the combat doc: both ends get bodies, each in its own bubble, and the shot travels in frame coordinates ("Fights", cross-bubble fire). What is left for the combat doc is the weapons themselves.
- **Warping away** loses a pursuer that cannot follow: the departure side learns only the direction (warp.md). The `IncidentRecord` and the owner's standing do not go away; the response moves to wherever the player next asks to dock.

### Fights: data unless combat involves them (D50)

**Rule.** Distance alone never gives an AI ship more simulation than section 1's table: beyond 20 km of every player it is a row, beyond 2 km of every anchor it has no body. A fight is the one thing that overrides distance. The ships in it are promoted as far as is needed to resolve it honestly, and no further.

- **Engagement.** A server object (`Engagement`, pool 64 / 16, <= 24 members, ~72 B plus 12 B a member). It is created by the first hostile act between two ships (a shot, a hit, a weapon lock, a tractor lock without consent) or by a decide that picks `Attack` or `Pursue` on a target inside 1.5 x the attacker's class weapon range.
  - **One engagement per hull** (`EngagementRow`). A battle of more than 24 ships is several engagements, split by who is shooting whom; a ship that changes target moves between them and is never in two.
  - **A lock alone makes only a provisional engagement.** It promotes the target, but it pushes no hazard to traffic, ends 10 s after the lock drops (not 60 s), and an owner group may hold at most 4 of them (a fifth lock is refused at fire control). A shot or a hit makes it a full one.
  - **A full pool resolves, it never refuses.** Provisional engagements are evicted first; then the engagement that has gone longest without a party is run to its end as a fast-forwarded Stepped fight (below). No fight is left without an owner because the pool was full.
- **Who counts as in the fight (a member):** a ship that in the last `CombatTicks` (600) fired at, hit, locked or tractored a member, or had that done to it by a member; a ship whose running action is `Attack` or `Pursue` on a member; an escort in formation on a member; a ship that has surrendered to a member, until it is released or taken. Bystanders are not members: lawful traffic gets the engagement as a hazard capsule (section 2) and holds outside 3 km of it or evades.
- **It ends** when no member has acted against another for 60 s and none has `Attack` or `Pursue` on another, or when one side is destroyed, fled out of weapon range for 60 s, or surrendered and dealt with. Members return to whatever level distance gives them.
- **Party and witness.** A player is a *party* when aboard a member or holding a lock, tractor or tracked projectile on one. A player is a *witness* when within 20 km of any member (lost beyond 30 km for 30 s). 20 km is where a hull is an entity and more than a point on screen, so a fight a player can watch is never faked.

| Mode | When | Members' level | How it is resolved | Cost |
| --- | --- | --- | --- | --- |
| **Real** | a player is a party or a witness | L2: bodies, real weapons, crew at C0 (casualties by section 3's C0 rule from the damage brushes). L3 and C2 only where a player is aboard, as always | physics. Members more than 4 km from a bubble's origin are in their own combat-anchored bubble; shots cross between bubbles (below) | the L2 costs, inside the L2 caps |
| **Stepped** | no party or witness, and either a player is in the same SOI (the fight is contacts on a map) or a member is a player-owned hull (D47) | L1; L0 members are promoted for its length. They fly real rails (pursuit, flight, intercept) | `AbstractFight`: every 600 ticks each member deals `expected damage x seeded spread` from the fight model to its target: hull fraction, `SystemsQ` bits, crew indices cleared lowest-priority first, template damage brushes into `DamagePool`. Target choice, `Flee` and `Surrender` are the same behaviour utilities, read at each step | ~2 us per member per step (<= 64 x 24 members: ~5 us a tick at the pool cap, typically ~0). Steps fall on `StartTick + 600 n`; at most 8 engagements are stepped a tick and the rest wait a tick, so 64 fights never step together (that would be a 3 ms spike) |
| **One-shot** | no player in the SOI and no player-owned member | L0: nothing is promoted | `ResolveEncounter(rowA, rowB, engagementId)`: **the same `AbstractFight` steps with the same draws, run to the end at once** on a job, with each result applied to the rows at the tick its step would have fallen on, since a raid takes time (`DelayTicks`). It is Stepped fast-forwarded, never a second model or a second roll | ~2 us per member per step, all at once: ~0.1-0.3 ms on a job for a whole raid, on the route draw |

- *Which* off-screen encounters happen is still section 6's sparse seeded draw per route. This table decides how each one is resolved.
- **Cross-bubble fire (closes the old open issue).** Float precision limits a bubble to 4 km; it does not limit a fight. Both ends of any shot must have bodies, and they need not share a bubble. A projectile that leaves its bubble is a rails entity in the frame (closed form, as any coasting body) and enters the target's bubble by the ordinary 2 km / 2 s predictor. A beam or hitscan round is one ray built in frame doubles and cast in the target bubble's float space. **Requirements on the combat doc:** weapon range and lock range <= `EntityRange` (20 km), so anything that can be shot is already an entity; and a lock count per ship (interim 4).
  - **The ray meets what is in the way.** It is tested against every bubble its segment crosses, nearest first (a segment-sphere prefilter, ~15 ns a bubble), and against the celestial bodies' spheres for the limb of a planet or moon. A hull still on rails in the way is not hit: the accepted limit of an instant beam.
  - **A player-fired projectile is tracked whether or not it was locked.** On rails it runs frames' closest-approach test against the L1 hulls along its path; a predicted pass inside a hull's radius plus 50 m within 2 s promotes that hull first. Unguided fire cannot pass through a ship because it was data.
  - **review.md P1 applies:** the shipped Jolt exports no `CastRay`, so until the upstream ask lands the cross-bubble ray is the managed `TriangleBvh` cast, and its cost belongs to the combat doc's budget.
- **Caps throttle fidelity, never tangibility toward a player.** The L2 caps do not move (24 AI bodies a bubble, 96 / 24 a server). When a Real engagement needs more bodies than that, members are promoted in this order: ships fighting a player; ships within 2 km of a player (always, section 1's tangibility rule); player-owned hulls and whatever is fighting them (D47: an owner watching never sees their own ship take abstract damage); then AI-against-AI pairs, nearest a player first. Pairs left over stay L1 and fight Stepped *inside* the Real engagement as **background pairs**. A background ship that comes within 3 km of a player is swapped in before it reaches 2 km (the promoted AI-against-AI pair farthest from any player goes to the background; if none can, the cap is exceeded and counted). Clients receive `FightStep {a, b, tick, damageQ, flags}` (16 B per pair per step) and draw the fire between them cosmetically between steps, and damage brushes appear at the step. A player who locks a background ship swaps it in: it is promoted on that tick and the AI-against-AI pair farthest from any player goes to the background. So a 200-hull battle is 24-96 simulated ships around the players and the rest stepping, with all 200 drawn.
- **Promotion at range has a budget, so a far fight cannot stall the server.** A body more than 4 km from every player is a *remote* body, and every one of them is asked for by somebody.
  - Each player ship holds remote bodies in at most **one** remote bubble: its primary target's. Its other locks must fall inside that bubble or its own, or fire control refuses them.
  - The server runs at most 8 / 2 remote bubbles for players' targets, besides the 4 / 1 for fights with no party, and at most 32 / 8 remote bodies inside the 96 / 24.
  - Past any of these a new lock is **refused** ("no solution"). It is never granted on a ship left on rails, and a ship that already has a body can always be locked. A refused target is no safer than before and no more dangerous: on rails it cannot fire either.
  - A lock *starts* the promotion and completes only when the body exists (a damaged hull's bake included), so nothing is ever locked but unhittable.
  - A witnessed fight past the bubble cap is drawn as background pairs. That is the stated limit of "never faked", and the profiler counts it.
  - Worst case: 12 / 3 extra scene steps (~0.6 ms of job CPU) and as many scenes from frames' pool (review.md: ~4 MB each, unmeasured).
- **The fight model is measured, not authored.** `data/ai/fightmodel.json` holds, per (attacker class, target class, range band, skill tier), the expected damage per step and its spread. Tables load into q16 integers and every step is integer arithmetic, so a replay, a save and load, and a Debug and a Release build all give the same stream. **Player-built plans have no class row:** until the combat doc gives every plan derived combat stats (`FightStats`: damage per second by range band, armour and hull points, thrust over mass, computed at plan compile by the same code for catalog and player plans) and the model is fitted on those, a player-built hull in a Stepped fight borrows the row of the catalog class nearest in mass and weapon count. That is a known inaccuracy and is listed as open. `--fight-calibrate` generates the file from headless L2 duels. Test: for every class pair the Stepped and One-shot outcome distributions match L2 over 1,000 seeded fights (win rate within 10 %, mean hull loss within 15 %).
- **A mode change moves state and never re-rolls.** The engagement carries `Step`; the draw for step `n` and a member is `hash(worldSeed, engagementId, n, slot)`, where `slot` is the member's hull slot (or AI slot for non-traffic AI) and never its index in the member list, which shifts as ships join and leave. Integer, server-only, saved with the world. `engagementId` for a route encounter is `hash(route, hull, trip index, draw tick)`; for any other fight it is a counter saved with the world. Stepped > Real: members are promoted by the ordinary reconstruction (damage brushes, `SystemsQ` and the crew mask are already in the row) and the step counter stops. Real > Stepped: the ordinary sum-back, and the next step is due 600 ticks later. One-shot results never coexist with a live engagement, and **entering the SOI re-rolls nothing**: the rows are untouched until each outcome tick, so the server replays the steps up to the current one from them (same id, same draws) and carries on Stepped. Left alone, the fight ends exactly as the one-shot said it would. (The draft cancelled the one-shot and ran a fresh Stepped fight, which was a re-roll.)
- **Exploits closed.**
  - *Outcome shopping* (hovering at the witness edge, or hopping SOIs, to flip modes until an ally wins): One-shot and Stepped are one stream, so only the step between abstract and Real can differ, by the calibration tolerance; 20 / 30 km plus 30 s of hysteresis; no re-roll on any mode change; damage is state that moves. What is left is honest: a Real fight is physics a player may join.
  - *Rails as a shield:* a player's lock or projectile promotes its target at any range a weapon reaches, inside the remote budget. An L1 ship still cannot be hit, and an L1 ship still cannot fire at a player.
  - *Shooting ships that are data:* no player can damage a row. A lock promotes first, an unlocked round promotes what it is about to hit, a beam passes an unpromoted hull. A freshly promoted ship reacts on that tick plus its skill's reaction delay, exactly as it would to any first shot, so being data when the shot was planned costs it nothing.
  - *Lock spam to exhaust bodies, bubbles or the engagement pool:* locks per ship are limited; a lock promotes only inside the locker's weapon range and the remote budget; lock-only engagements are provisional (10 s, 4 per group, first evicted); combat bubbles are capped with and without a party, and past the cap the engagement that has gone longest without a party goes back to Stepped.
  - *Locking traffic to starve a station or move prices:* a lock pushes no hazard, so no hauler holds or delays for it. A real engagement inside a `ProtectRadius` holds lawful traffic for at most `engagementHoldMaxS` (120 s) before it routes round the capsule, and a lock or a shot there is an `IncidentRecord` that brings security.
  - *Provoking far fights to load the server:* every promotion is inside the L2 caps and the remote budget; sending hired hulls to attack far away is a Stepped fight at ~0 cost; a witness promotes at most the caps and the rest are background pairs; hired hulls at L2 have their own cap (section 1).
  - *Logging off to save a ship:* a member stays in its engagement until it ends, owner online or not (section 3, "When the owner logs off").
  - *Farming with cheap player-owned hulls:* the model reads both ships' real rows, the loot is the target's real cargo (section 6), and the owner is the `Actor` on every record.
  - *Hiding in traffic:* bystanders hold outside 3 km, so a hull parked in a lane is not screened by ships that cannot be hit.

### Drawing 200 hulls: the hull stage ladder (D50, client)

The client must draw 200 hulls at once. ship-hulls gives the near end (brick LODs, no interior from LOD 2, "one whole-ship bake beyond ~2 km", "impostor or lit point beyond ~30 km") and review.md gives the two risks: P4 (one draw per brick is 3+ ms of recording) and P6 (10-15 MB per 100 m hull at LOD 0, so LOD 0 must be capped by count). This ladder fills the gap between them. Thresholds are for a 100 m hull at 1080p and 60 degrees vertical FOV (935 px per metre at 1 m; they scale with size, resolution and FOV).

| Stage | What is drawn | Chosen when | Count cap | Triangles per hull | Draw records per hull | GPU memory |
| --- | --- | --- | --- | --- | --- | --- |
| **H0 interior** | ship-hulls bricks at LOD 0-1 with the interior, components, glazing over real rooms | within ~300 m (a 0.5 m cell is >= 1.5 px), or the camera is aboard | 4, the camera's own hull first; and a 60 MB pool (below) | <= 400k hull (ship-hulls' cap) + <= 100k components; ~100k typical | ~180 bricks + <= 512 component instances | <= 15 MB per hull, from the pool |
| **H1 shell** | bricks at the finest LOD from 2 up that needs <= 24 bricks (LOD 2-3 for a 100 m hull, coarser for a bigger one), no interior, interior-mapped glazing (section 4), components over 1 m at their own LOD | 300 m - 2 km, or pushed out of H0 by its cap | 12 | <= 25k, components included | <= 24 bricks + <= 32 components | ~1 MB per hull |
| **H2 whole-ship mesh** | one mesh from one whole-ship bake at 2 m cells, doubled until the mesh fits its 8k triangles (about longest axis / 50: 4 m at 200 m, 8 m at 400 m; the draft's "4 m over 400 m" would have been ~30k triangles), material colour and an emissive mask per vertex, components over 2 m baked in as boxes | 2 - 8 km (down to 12 px), or pushed out of H1 | 64 | <= 8k | 1 | ~130 KB per **plan hash**, shared by every hull of that plan |
| **H3 proxy** | the same bake at 8 m cells (never fewer than 6 cells along the longest axis) | pushed out of H2 by its cap; or a hull whose blueprint has not arrived | 120 (the rest of the 200) | <= 512 | 1 | ~10 KB per plan hash |
| **H4 impostor** | octahedral impostor: 8 x 8 views of 16 x 16 px, albedo + alpha and normal + emissive | 12 px down to 3 px (8 - 31 km) | 256 | 2 | all in one instanced draw | 128 KB per plan hash (two 128 x 128 RGBA8 layers) in one array of 128 plan slots: 16 MB, fixed |
| **H5 point** | a lit point: sunlit flux from size class, albedo and phase angle, plus drive plume and nav strobe | under 3 px (beyond ~31 km): every traffic contact in sight | 1024 | 1 point | all in one draw | 16 B an instance |

- **The 200** are hulls drawn as geometry (H0-H3). Impostors and points are on top of that and nearly free. A station's linked grids are hulls here like any other: each is one entry in the ladder and one of the 200, and the camera's own grid is the hull that is always H0.
- **H0 is a byte and triangle budget, not only a count.** ship-hulls' 400k triangles and 10-15 MB are for a 100 m ship; a 500 m capital ship at LOD 0 throughout would be ~8k bricks and several hundred MB, and four of those would end the budget. So an H0 hull picks its bricks' LOD per brick, nearest the camera first (ship-hulls' octree scheme), until its share is spent: 400k hull triangles and 15 MB for each of the 4, out of one 60 MB pool. A big hull is LOD 0 round the camera and LOD 1-3 beyond. This is review.md P6's cap by count, made safe for hulls that are not 100 m.
- **Selection** (one job a frame, ~50 ns a hull over <= 1024 contacts): compute each hull's size on screen, take the stage that gives, then sort by size and apply the count caps so overflow cascades down (the 5th-nearest hull is H1 however close, the 17th is H2, the 81st is H3, the 201st an impostor). A stage changes only past 20 % hysteresis in pixels and after 0.5 s, with a 0.25 s dithered cross-fade. With more than 16 hulls inside 2 km the outer ones show 2 m cells up close. That is **not** rare: docked hulls are station children outside the 24-body bubble cap, so a hub with 30-40 ships docked puts it on screen every visit (a 100 m hull at 500 m is ~190 px across at ~4 px a cell). It is the first thing `--fleet-bench` has to judge at a hub, in motion. The lever is the H1 cap: 12 > 24 costs +12 MB and +0.3M triangles.
- **Instancing through the shared instance buffer.** Every stage writes review.md C9's per-draw record (`relOrigin, scale, quat`, 32 B) into the one persistent mapped instance buffer terrain uses: a brick's record is the hull pose composed with the brick origin in double, then cast. A second 64 B **hull record** per hull carries what bricks share (plan slot, tint, scorch, `LitFraction`, `ThrustQ`, strobe phase, room-light table offset, impostor layer). H0-H3 share one vertex format, one mesh arena and one pipeline, so all of them are one GPU-culled `DrawIndexedIndirectCount`; glazing, impostors, points and plumes are one draw each. Worst case ~3.6k draw records (4 x (180 bricks + <= 512 component instances, nearest and largest first) + 12 x 56 + 184), inside the 4096-record cull buffer, for at most 6 draw calls plus one per shadow cascade. (The draft's ~1.7k counted ~30 components on an H0 hull; a 100 m ship has hundreds, and ship-design sets no cap, so this doc caps what is drawn.)
- **Whole-ship meshes (H2, H3)** are baked once per plan hash on a job (a 100 m hull at 2 m cells is 125k cells: ~2-4 ms, estimate) and cached on disk by content hash; catalog classes ship pre-baked. 200 AI hulls are ~20-40 plans, so the mesh memory is the plan count, not the hull count. For a player plan the **server** bakes them once (`WholeShipBake` has no Vulkan) and serves them as a **far-mesh blob** by plan hash (<= 140 KB, cached on disk by both sides), so a hull seen at 5 km costs 140 KB and never a blueprint.
- **Damage far away.** An L1 hull streams its <= 64 coalesced damage brushes (section 1). A damaged hull at H2 gets its own re-bake (at most one a second per hull and two a frame overall; pool of 32 damaged meshes, 4 MB). From H3 down damage is one scorch byte in the hull record.
- **A player-built hull the client has not fetched** draws at H3 from `HullSilhouette` (24 B with the entity or the traffic contact: half extents, two colours, emissive fraction, a shape code). The far-mesh blob is fetched for hulls that want H2 or H3; the blueprint itself (which can be megabytes: 8192 brushes and mesh brushes) only for hulls that want H1 or better, nearest first, inside frames' blueprint budget. Far player ships are a silhouette until they matter.
- **Impostor baking.** By plan hash, on first need: the H2 mesh is drawn from 64 octahedral directions (orthographic, fitted to the bounding sphere) into two 128 x 128 layers, 64 scissored tiles each (~0.05 ms GPU; at most 2 bakes a frame). At run time the three nearest views are blended and lit by the sun and planet shine through the baked normal. At <= 12 px parallax is under a pixel, so no depth is stored. The 128 slots are LRU by plan hash; past that, the farthest unique plans draw as H5.
- **Lights.** 200 hulls cannot each bring lights.
  - H0: real room lights aboard; <= 16 exterior hull lights per hull into the clustered light list.
  - H1: <= 2 real lights (largest floodlight, drive glow) for the 4 nearest H1 hulls; emissive only for the rest.
  - Frame cap: 64 hull lights, nearest and brightest first. No shadows from hull lights except aboard the camera's own ship.
  - H2-H3: the per-vertex emissive mask x `LitFraction` (one byte per hull from its room-light states), a plume billboard scaled by `ThrustQ`, nav strobes as sprites in the point draw.
  - H4: the impostor's emissive channel x `LitFraction`. H5: flux only.
- **Shadows:** H0 and H1 cast into the near sun cascades, H2 into the far cascade only, H3 and below cast none. Every caster but the camera's own hull casts with its **shell** mesh (the H1 bricks), so a cascade draws at most 16 x 25k + the camera hull's 400k ≈ 0.8M triangles and the far cascade 0.5M; the three other H0 hulls keep their shell bricks loaded for it (+~1 MB each). Casting LOD 0 for all four would be 1.6M triangles a cascade, and the 0.4 ms below would not hold.
- **Crew follow the stage:** Near figures need H0 at their portal, cards need H1 (section 4); from H2 down there are no figures. `LitFraction` comes from the room-light states only. Its flicker is seeded from the hull id, and follows the head count only on grids whose key the client holds: a private hull's lights say nothing about its crew (D48; the draft folded the head count in for every hull, which the leak test would have caught).
- **Where the pose comes from:** L2 snapshots, L1 rails or the L0 leg record's closed form. The server does not know render stages; `TrafficHullInfo {hull, plan hash, silhouette}` (36 B, once per hull on interest) is what lets a client draw a hull that is only data.

**Budget for 200 hulls in frame (4 + 12 + 64 + 120, plus impostors and points). Estimates; `--fleet-bench` measures them.** They are budgets to hold by turning the caps in `hull-lod.json`, not predictions, and they assume the dev machine's class of GPU (an RTX 4090 today) at 1080p. No minimum spec has been chosen; the same caps are what scale down to it.

| Item | Worst case | Typical busy station (30-40 hulls) |
| --- | --- | --- |
| Triangles | 2.0M (4 x (400k hull + 100k components)) + 0.3M + 0.51M + 0.06M ≈ 2.9M | ~0.6M |
| GPU: hull opaque (H0-H3) | <= 1.2 ms | ~0.5 ms |
| GPU: glazing and interior mapping | <= 0.15 ms | ~0.1 ms |
| GPU: shadows (shell meshes: <= 0.8M triangles a near cascade, 0.5M far) | <= 0.4 ms | ~0.2 ms |
| GPU: cull compute (<= 4096 records), impostors, points, plumes | <= 0.1 ms | ~0.05 ms |
| GPU: hull lights (<= 64, clustered) | <= 0.15 ms | ~0.1 ms |
| **GPU total** | **<= 2.0 ms** | **<= 1.0 ms** |
| CPU | <= 0.05 ms main thread (<= 6 draws recorded); <= 0.25 ms on a job (selection, <= 4096 instance records) | under half of that |
| Memory: H0 pool (4 hulls x <= 15 MB) / H1 12 x 1 MB / shell bricks kept by H0 hulls for shadows | 60 MB / 12 MB / 4 MB | the same caps |
| Memory: H2 cache 96 plans / damaged H2 pool / H3 cache 256 plans | 12.5 MB / 4 MB / 2.6 MB | |
| Memory: impostor array / instance and hull records | 16 MB / 0.3 MB | |
| Memory: component meshes, one library by component plan hash, LRU | 16 MB (a guess: no doc budgets component meshes, and player-made components are unique per plan) | |
| **GPU memory total** | **<= 130 MB** (127.4 summed; the draft's 110 left out components, the shadow shells and a fifth H0 hull) | review.md P6's ten parked ships were 150 MB uncapped |

Zero allocations a frame: fixed arrays for contacts, records and the LRU tables. The four `ProfilerPanel` rows are hulls per stage, triangles, GPU ms and MB.

### What is reconstructed on each crossing

Nothing may pop or cheat. State that survives every level lives in the hull slot or in lazy side pools keyed by hull slot:

| State | Where it persists | L0 > L1 | L1 > L2 | L2 > L1 / L1 > L0 |
| --- | --- | --- | --- | --- |
| Position, velocity | derived: `Locate(row, tick)` | `Placement` built from the current leg (elements for a coast, leg start state for a burn, `WarpTable` entry for a warp, station-local pose when docked). Exact by construction: L1 runs the same legs | elements or leg state to a body state (frames, ~200 ns + ~5 us body create). Shape from the plan-hash bake cache; catalog AI plans are pre-baked at server start | body state to elements (~200 ns). The AI replans the rest of the trip from the current state; the difference from the nominal schedule goes into `DelayTicks`, so L0 carries the delay forward |
| Fuel, propellant, EM, AM | `FuelQ` (fraction of the plan's tanks, per commodity class, 4 x q16) | copied | tank items are materialised from the fractions (item ledger entries, exotic-matter's `ItemStack`) and the row's fractions are zeroed; the 10 Hz `ShipLedger` takes over | summed back to fractions, **rounded down**, so cycling a ship across the 2-3 km band can never add fuel (it loses at most 1/65535 of a tank per cycle). At L0/L1 each completed leg debits its closed-form burn (same thrust, Isp and warp fuel formulas, evaluated per leg, not per tick). Refuelling happens only at a `Docked` leg and debits station stock |
| Cargo | `CargoItem`, `CargoQty` (exact integer units) | copied | **moved**, not copied: inventory entries are created in the plan's holds and `CargoQty` is zeroed in the same step (crates become bodies only if a hold is breached or a player opens it) | only entries of `CargoItem` that are **inside this hull's holds** at the demotion tick are summed back, and they are removed as they are summed. A crate in a player's hands, in a tractor beam, on another grid or adrift outside stays its own entity and is not counted. Other items in the holds pin the hull at L1 |
| Hull damage | `DamagePool`: the ship-hulls damage brush layer coalesced to <= 64 brushes (2.2 KB), allocated only for damaged hulls (pool 256) | pool row follows the entity; the brushes are part of the L1 entity's stream-in, so a damaged hull looks damaged before it has a body | brushes are replayed into `ShipHull.Damage` before the body is created, so mass, rooms and breaches are the ones the player left | re-coalesced (ship-hulls' bounding-sphere merge only ever grows a hole, so it never heals; a layer that would grow by more than 10 % of removed volume is kept whole and pins the hull at L1). Repair happens only during `Docked` legs: it extends the dwell by `RepairTicksPerBrush` and debits station stock; the pool row is freed when empty |
| Component and wiring state | `SystemsQ`: 16 bits of destroyed-or-cut flags for the critical set (helm, flight computer, main drive, warp drive, reactor, life support, beacon, comms), plus the wiring state bytes ship-design already keeps for non-factory ships, kept in the same pool row | copied | applied before the first tick at L2 | summarised. A ship with no drive is `Adrift` (an L1 coast, a salvage and piracy target, a distress call), not teleported home |
| Crew | `CrewAlive` (128 bits in two words, 100 used), `CrewBodies`, `CrewDose`, `CrewDelta` and `Prisoners` side pools (section 3) | copied to the grid's `CrewManifest` | unchanged | unchanged. Replacement crew join only at a `Docked` leg, after `CrewReplaceTicks` each, and their kit is debited from the owner's station stock. Prisoners follow the hull slot at every level and are never summed into the crew |
| Engagement and surrender | `EngagementRow` on the hull slot; `Surrender` state and captor in `AiShips` (off-schedule ships keep their slot) | the engagement's mode decides the level ("Fights") | members of a Real engagement are promoted with their `Step` index frozen | Stepped resumes from the same `Step`; a surrendered or prize hull stays surrendered across every crossing and a save |
| Incident and standing | the incident log (server) keyed by owner | n/a | n/a | n/a |

- **No free healing rule (tested):** for every field above, the only writers at L0/L1 are closed-form leg debits and dock events that take time and station stock.
- **One owner of truth per level (tested).** Below L2 the hull row and its side pools are the truth. At L2 and L3 the materialised ledgers, inventories and `ShipHull` are the truth and the row's quantity fields are zero. A crossing moves state; it never copies it. A **save** writes each L2/L3 AI ship either as its entity (if pinned) or through the same sum-back as a demotion, never both, so a save and reload cannot double anything.
- **Split hulls.** A piece that separates (ship-hulls' split check) is a new body. The hull slot follows the piece that keeps the body; cargo and crew are divided by where their holds and scheduled nodes are at the split tick. The lost piece is a wreck entity with its own contents (pinned while it holds any), not a second hull slot.
- **Continuity exit checks:** (1) L0 and L1 agree: demote and promote the same on-schedule ship 1,000 times along a trip; `Locate` and the L1 pose are equal to the bit at every crossing and `DelayTicks` does not change. (2) L1 > L2 hands over the exact state (frames' transfer error, <= 0.5 mm) and L2 > L1 fits the body's state; the L2 flight is real physics, so its deviation from the plan goes into `DelayTicks`, which for an undisturbed flight stays inside the trip plan's 2 % tolerance.

## 2. Ship AI (the "pilot")

### Authority: the AI flies, the pilot is its token

- `ShipAi` is a per-ship server controller. At L2 it writes a `FlightCommand`, the same struct a player's seat input is turned into before it reaches the flight computer (ship-design "Flight computer": control allocation over thrusters and surfaces, Space / Atmo / Decoupled). It also calls the same server entry points players call: warp plot and engage, docking request, queue and catch-slot booking, tractor consent, comms hails. **It never writes a pose, a velocity or a force.** An AI ship can do nothing a player in that hull could not.
- **The seated pilot is a placeholder: a C0/C1 figure with no control authority.** Nothing reads its hands, it operates no controls, and no character simulation is needed for a ship to fly. This is the user decision.
- **Proposed (open question 1; `helmNeedsWatch` on the ship class, default true): the AI is a bridge-watch capability, not an always-on autopilot.** The AI has the helm only while all of these hold:
  1. A helm console is powered and bound to the flight computer by intact wiring (ship-design's derived bindings; cutting the Data run or opening its breaker removes it, D17).
  2. The crew member holding the on-watch `Pilot` post is alive and not displaced (dead, removed from the seat by a player, promoted to C2 and away from the seat, or the bridge is in vacuum without suits).
  - **Why not always-on:** an always-on autopilot would make storming the bridge, killing the watch or cutting the helm cable pointless, and those are the boarding actions the wiring and capture decisions (D17, D27) exist for.
  - **If the user says no:** set `helmNeedsWatch` false. Condition 2 goes, the AI is the flight computer (condition 1 and power still apply, so cutting the cable still works), and the pilot figure is purely cosmetic. Nothing else in this doc changes.
  - **Not a one-shot kill switch.** The pilot can only be shot through the bridge glazing if the round defeats the glazing first (section 3, "Small-arms through a window"); that breaches the bridge, raises `Alert`, and succession applies. Catalog ship classes are validated so bridge glazing resists the small-arms class (a Warning otherwise).
- **Losing the helm:**
  - The flight computer keeps its last mode with a **dead-man hold**: velocity hold in space, level hold in atmosphere, station-keeping near a station. Any warp in spool is cancelled if before `T0 - 30`; a warp past lock-in completes. No new decisions are made.
  - **Succession:** the next alive crew member with `CanFly` (role data: pilots, then officers) is assigned the post. Relief arrives after its walk time from where its schedule puts it to the helm (nav path length / 4 m/s, minimum 5 s). Until then the hold stands. The relief's skill tier is its own (an engineer flies at the lowest tier).
  - **Console fallback:** with the helm console dead but another console bound to the flight computer (engineering panel, a local panel), the AI flies from there at one skill tier lower once a qualified crew member reaches it.
  - **No qualified crew or no bound console:** the ship is `Derelict`: dead-man hold until power or propellant runs out, then a coast. It keeps its beacon and transmits distress if comms are powered. It is a capture and salvage target; a player who takes a seat that reaches the flight computer flies it. Flying it does not make it theirs: the hull keeps its `OwnerId` and is `Stolen` (section 6).
- **Autonomous classes** (data flag on the ship class: drones, tugs, missiles' big cousins) have an `fn.autonomy` core instead of a crew post. Condition 2 becomes "the core is powered and intact". They carry no crew manifest.
- **Players who displace the pilot** take the helm by the ordinary seat rules; the AI does not fight the controls. If the player leaves the seat and the pilot post is still filled, the AI resumes after `ReliefTicks`.
- **Player-owned ships (D47).** A player's hull with hired crew has the same `ShipAi`, under the `player_crew` behaviour: the on-watch `Pilot` post gives the AI the helm exactly as above (`helmNeedsWatch` and open question 1 apply to it unchanged), and what it does with the helm is the owner's standing `ShipOrder` (section 3, "Player-owned crew"). So a player's ship holds station, follows, docks or runs a route while its owner is in a turret, in the hold or on another ship. The AI still issues only `FlightCommand`s and the ordinary requests, and the owner is the `Actor` for everything it does. On the wire the hull is server-owned while the AI has the helm and goes back to pilot prediction when a player sits (review.md's "who owns a ship", handed over on the seat event).

### Outputs

```csharp
/// What the flight computer consumes, from a player's seat input or from ShipAi. 40 B.
public struct FlightCommand
{
    public float3 TargetVelocity;      // in the ship's Placement.Frame, m/s
    public uint   TargetAttitude;      // smallest-three, or ignored when FaceMode != Attitude
    public FaceMode Face;              // Attitude, Prograde, Retrograde, TargetEntity, ThrustVector
    public FlightMode Mode;            // Space, Atmo, Decoupled (ship-design)
    public byte   ThrustLimitQ;        // 0..255 of rated: the skill tier and "lawful speed" clamp it
    public byte   Flags;               // Gear, DockingAssist, BeaconOn, RunCold
    public EntityId FaceTarget;
    public long   Tick;
}
```

High-level actions go through request structs that already exist or are owned elsewhere: `WarpPlan` request (warp.md validation, unchanged), `DockRequest` (section 5), `QueueSlot` and `CatchSlot` bookings (wormholes.md), the consent flag (tractor-beams.md, D26), `TunnelCruise` (wormholes.md: already "runs on the server for AI ships").

### Rates and cost

| Layer | Rate | Work | Cost |
| --- | --- | --- | --- |
| **Decide** | 1 Hz, staggered by `(aiIndex + tick) % 60` | utility pick over the behaviour's options (<= 8), goal update, request calls | ~2 us at L2, ~1 us at L1 |
| **Steer** | 10 Hz, staggered by `% 6`; L2 only | goal velocity (PD on position and speed errors), avoidance over <= 8 nearest contacts and <= 8 hazards, clamp by skill | ~0.3 us |
| **React** | on event (new hazard, damage, lock, hail) | forces the next decide and steer onto the next tick, after the skill's reaction delay | ~0.1 us |
| Flight computer | 60 Hz | existing: allocation over <= 64 actuator groups | ~0.3-0.5 us (already budgeted for any piloted ship) |

- At the caps: 96 L2 ships average ~8 us a tick for decide and steer; 1024 L1 ships ~18 us if every one were off schedule (on-schedule ships are event-driven and cost nothing here, so typical is under 1 us). The Jolt bodies (96 awake compounds, ~0.15 ms CPU) are the real cost, which is why the cap is on L2 count and not on thinking.
- Decide and steer run in tick step 4 (inputs) as one job over the due ships; each ship reads the tick-start snapshot of its bubble's contact list and writes only its own `FlightCommand`. No shared mutable state.
- **Contacts:** `BubbleManager` already holds each bubble's entity list. A per-bubble `ContactGrid` (positions, velocities, radii, owner; <= 256 entries, rebuilt at 2 Hz, ~5 us a bubble) gives each AI its <= 8 nearest by time to closest approach. This is not the sensors system (README lists sensors as missing); when sensors exist, the contact list is filtered through them.

### Behaviours (data-driven)

A behaviour is a named set of **options**. Each option has a utility (a sum of weighted, clamped inputs from a fixed input table) and an **action** from a fixed C# action table. Decide picks the highest utility, with a 15 % stickiness bonus for the running option so ships don't dither. No scripting language, no allocation.

- **Actions** (C#, each a small state machine over `FlightCommand`s and requests): `FollowTrip` (run the route legs), `Undock`, `ClearZone`, `PlotWarp`, `Approach`, `HoldAt`, `RequestDock`, `DockWithConsent` (asks the station tractor, D26) or `DockManual`, `QueueGate`, `CrossGate`, `TunnelTransit`, `Patrol` (waypoint ring), `Escort` (formation offset on a leader, joins its fleet warp: warp.md "AI accepts automatically"), `Mine` (rings-and-asteroids lasering cycle, outside every `ProtectRadius`), `Pursue`, `Attack`, `Evade`, `Flee` (sublight out of weapons range, then warp to the nearest friendly station), `Hail`, `Distress`, `Yield` (stop and submit to a lawful scan or inspection), `Surrender` (give the ship up to a hostile: section 3), `AcceptSurrender` (hold fire, close and board, or order the prize to follow), `ScanCrew` (hold a scanner dwell on a target: section 4), `ObeyOrder` (run a player owner's `ShipOrder`).
- **Inputs** (fixed table, all O(1) reads): hull fraction, crew fraction, fuel fraction, cargo value, threat score of the top contact, own strength / threat strength, distance and time to closest approach, inside a protected zone, standing with the top contact's owner, hazard severity ahead, queue position, time behind schedule. Revision 2 adds: cannot flee (no drive, no warp, or interdicted), morale, the top threat owner's `mercy` (section 3), being scanned, and the scanned crew count of the target (zero until a scan has returned one).
- **Shipped behaviours:** `hauler`, `miner`, `patrol`, `escort`, `pirate`, `courier`, `tug`, `station_security`, `player_crew`. Each is ~30 lines of JSON (section 7, "Data definitions").
- **Lawful conduct is the default and is data:** `lawful: true` behaviours keep their beacon on, obey the station speed limit and approach lanes, never plot a drop inside an exclusion zone without permission (warp.md would push the exit out to the exclusion radius at commit anyway), never activate interdiction in a protected volume, wait in `MouthQueue`s, mine only outside `ProtectRadius`, and request consent before any tractor contact.

### Flight skill

Four tiers in `data/ai/skills.json` (Green, Trained, Veteran, Ace). A tier sets: reaction delay (90 / 45 / 20 / 10 ticks), fraction of rated thrust it will use (0.5 / 0.7 / 0.9 / 1.0), look-ahead for avoidance (4 / 6 / 8 / 10 s), minimum separation it keeps (x ship radius: 6 / 4 / 3 / 2), docking approach speed, aim error and lead quality, jink amplitude. Skill only ever *limits* what the same flight computer would do for a player; an Ace does not exceed the hull.

### Hazards

One per-bubble list (`Hazard`, <= 64, rebuilt at 2 Hz from the owning systems, pushed immediately for new events):

| Hazard | Source | AI response |
| --- | --- | --- |
| Beaconed warp corridor and blast footprint | warp.md `WarpBeacon` (30 s ahead) | leave the capsule plus margin at lawful thrust; hold outside. warp.md already states AI avoids beaconed corridors |
| Forecast flare cone | wormholes.md / exotic-matter flare forecast | reroute or shelter in a body's shadow; haulers delay departure (`DelayTicks`) |
| Debris and asteroids on a collision course | contact list, time to closest approach | sidestep along the largest-clearance direction of 8 candidates |
| Dust cloud | rings-and-asteroids `DustCloud` | slow to the sensor-limited speed |
| Bomb, runaway alarm, drive plume radiation | exotic-matter events | open range to the pulse radius; turn the shadow shield if it has one |
| Tractor lock without consent | tractor-beams.md | treat as hostile: evade, hail, distress; an `IncidentRecord` is already written |
| Station zone | `ExclusionRadius`, `ProtectRadius`, lanes | speed limit, lane keeping, no weapons, no mining |
| Engagement | section 1 "Fights": a capsule round the members, pushed at the first shot or hit (a lock alone pushes nothing) | non-members hold outside 3 km or route round it; haulers delay departure, for at most `engagementHoldMaxS` (120 s) inside a `ProtectRadius` and then route round; escorts and security read it as a call |

An unseen hazard (silent warp arrival, cut beacon) is not in the list, so AI ships can be hit by it. That is intended, and the liability falls where warp.md puts it.

### Combat basics

Combat, weapons and sensors have no doc yet (README "Missing systems"). This doc fixes only what the AI needs from them and what it guarantees:

- **Target choice:** utility over the contact list by threat score, standing and escort duty. Re-evaluated at 1 Hz.
- **Manoeuvre:** hold a preferred range band and aspect (both from the ship class's weapon data), jink by skill, break off when `hull < fleeHull` or `crew < fleeCrew` or own strength / threat strength < `fleeRatio` (behaviour data).
- **Fire control:** aim at 10 Hz with a lead solution degraded by the skill's aim error; fire decisions gated by rules of engagement in the behaviour (`returnFireOnly`, `protectedZoneNoFire`).
- **Subsystem intent:** pirates aim to disable (drive, then helm) and board; security aims to disable; nobody AI-controlled tries to destroy a station (D27: capture is preferred).
- **Calls for help:** `Distress` and `Hail` are comms events that station security and escorts read as inputs.
- **Where the fight is resolved** (bodies, stepped on rails, or the same steps run to the end at once) is section 1's "Fights". The behaviour data is the same in all three modes: target choice, `fleeHull`, `fleeCrew`, `fleeRatio` and the surrender rule are read by the L2 controller, by `AbstractFight` and by `ResolveEncounter` alike, so a ship does not get braver or meeker by being far away.
- **Scouting:** `pirate`, `patrol` and `station_security` may run `ScanCrew` before committing. The AI reads only what its scanner's quality returns (section 4), through the same gate a player's client gets; it never reads a hostile manifest directly.
- **Giving up:** `Surrender` is an option in every crewed behaviour, with its rule, the captor's side and the abuse rules in section 3. A ship that cannot flee and will not surrender fights on; that is data (`neverSurrender`), not a default.

### Liability

- `IncidentRecord` applies to AI exactly as to players. **Change to rings-and-asteroids.md:** `Actor`, `CoActor*`, `VictimOwner` and `Liable` become `OwnerId` (a tagged id: a player, or an NPC owner such as a faction, a company or a station), not `PlayerId`, and so do the tracing fields that feed them (`SourceOwner`, `LastBeamOwner`, site `Owner`, `Shooter`, `ThrownTag.Owner`, `HeatContributor`). Records are still server-only.
- **Crew are victims too.** Killing or wounding a crew member or a crowd member at any level (C0 through a window, C2 face to face) writes a `Hostile` record with the grid's owner as `VictimOwner`.
- An AI ship's actor is its owner. A lawful AI hauler whose mining debris hits a station is liable under D33 like any miner; an AI pirate's `TractorLock` or `WarpCollision` lowers its faction's standing with the victim's.
- Players attacking AI ships write records with the AI's owner as `VictimOwner`. That is what drives station security response and prices (section 6). The future factions/law doc consumes all of it.
- **Orders make the owner liable (D47).** Whatever a hired crew does under a `ShipOrder` or `CrewOrder` is recorded with the owning player as `Actor`, and the order is logged with its tick. "My crew did it" is never a defence.
- **New record kinds (D49), all `Hostile` and aggravated:** `Perfidy` (breaking a surrender), `KilledSurrendered` (harming a surrendered ship or person) and `PrisonerHarm` (harming, or losing through neglect, someone in custody). They feed the per-owner `mercy` value in section 3.

## 3. Crew simulation levels

### The three levels

| Level | What exists | Who evaluates | Cost |
| --- | --- | --- | --- |
| **C0 aggregate** | `CrewManifest` row per crewed grid: template, seed, `Alive` mask (128 bits, 100 used), condition, competence, morale, owner. Side pools only when needed: bodies, dose, overrides, deltas, prisoners | server | 0 per tick. Read on events |
| **C1 ambient** | Nothing new in the sim. A figure's pose is `CrewSchedule.Evaluate(manifest, nav, index, tick)`, a pure function | each client that can see a window; the server only on events and near players | ~30 ns idle / ~150 ns walking per evaluated figure, at 10 Hz |
| **C2 full** | A character entity: `CharacterVirtual` (or the zero-g capsule), health, needs, task, inventory, weapon. Replicated like a player | server | ~20-50 us per walking agent per update, ~0 parked |

**Cap: 100 crew per grid (D51; was 64).** `Alive`, `Full` and `Suited` are each a `CrewMask`: two `ulong` words, 16 B, indices 0..99, bits 100..127 always zero (tested). 100 does not fit one word, and two words leave the mask a plain value type with no allocation. Bigger ships are fiction-heavy on automation; stations are several linked grids (frames-and-zones "Huge structures") with up to 100 staff each plus anonymous crowds (section 5). The same three levels and the same cap apply to player-owned grids (D47). 100 people are affordable because of the ladder inside a grid ("Crew levels inside one grid"): at most 24 of them are ever active characters, and the rest cost nothing a tick.

What 100 costs over 64, per crewed grid: masks +24 B in the manifest and on the wire; `CrewDose` 200 B (was 128); epoch origins <= 600 B (was 384); one schedule evaluation of the whole grid <= 10 us (was ~6); overrides, bodies and deltas grow to 32 entries each (below). Nothing per tick.

### C0: the manifest and what it changes

- A **crew template** (data, per ship class) lists roles with counts, posts and a watch plan. `index` 0..99 maps to a role by cumulative counts, in template order, forever.
- **Identity** is `CrewId = (hull or station id, index)`. Name, face, body and outfit come from `hash(CrewSeed, index)`; role, watch and home bunk from the template. Identity never depends on the level.
- **Ship stats from the manifest** (recomputed on casualty events, never per tick):
  - *Helm:* the on-watch pilot's skill tier (section 2).
  - *Reaction time:* decide and react delays scale with `1 + 0.5 x (1 - bridgeFilled)`.
  - *Damage control:* repair rate of the damage layer and of cut wiring = `engineersAlive x repairPerEngineer` (data), spent on the most critical item first. Zero engineers, zero repair.
  - *Gunnery:* turrets beyond the count of alive gunners run on the fire-control computer at one tier lower.
  - *Life support:* consumables debit by alive count (exotic-matter's ledger, a per-grid scalar).
  - *Morale threshold:* `fleeCrew` in the behaviour reads the alive fraction.
  - *Morale* is one byte per manifest, not per person: it starts at the class's value, falls with the casualty fraction of the current trip and (hired crews) with unpaid watches, and recovers at a dock. It is read by the surrender rule and by hired crews deciding to stay ("Player-owned crew"). Nothing else reads it.
- Competence is one byte per ship (the class's tier, plus or minus a seeded offset) and one tier per role; no per-person stats at C0.

### C1: the schedule function

A pure function of integers, identical on every peer:

```
watch(i, tick)   = ((tick / WatchTicks) + WatchOf(i)) % 3            // OnWatch, OffWatch, Sleep; ships under 6 crew have one permanent watch
slot(tick)       = (tick + GridPhase) / SlotTicks                    // SlotTicks = 10 min (36,000 ticks); GridPhase = hash(seed) % SlotTicks: one slot boundary per grid
post(i, k)       = RolePosts(role(i), watch)[(rank(i) + Perm(seed, k)) % n]   // rank and n are the template's, alive or dead; overflow goes to wander nodes
leave(i, k)      = slotStart(k) + hash(seed, i, k) % (60 s)
pose(i, tick)    = at post(i, k) once (tick - leave) x SpeedQ >= Dist[post(i, k-1), post(i, k)], else the point that far along the nav route
```

- **What makes it identical on every peer:**
  - `hash` is one fixed 32-bit integer mix in `CrewSchedule` (never `HashCode`, `string.GetHashCode` or anything seeded per process). `SpeedQ` is an integer in 1/1024 m per tick. No float appears before the instance record.
  - Every input is server data. Role tables, the template's posts and watch plan are resolved at compile time into the plan's **`CrewPlan` blob** (with `CrewNav` and the `ViewPortal`s), keyed by a content hash and fetched from the server like the blueprint. A client never evaluates from its own `data/crew` JSON or its own bake.
  - Slot boundaries are per grid, not per person (a per-person phase would let two crew in different slots be assigned one post). Within a slot, assignments are a permutation, so no two crew are assigned the same post; the only overlap is a handover of up to 60 s, drawn at the post's handover spot.
  - **Death never reassigns.** `rank(i)` and `n` come from the template, so a casualty leaves a post empty instead of reshuffling everyone (which would teleport them). The one exception is helm succession, which is an override.
  - **Every walk fits its slot.** The compiler checks `Dist max / SpeedQ + 60 s < SlotTicks` for every pair of posts a role can be given in consecutive slots, and drops farther posts from that role's list (a plan Warning). So a pose never depends on more than the previous slot.
- **Posts** are plan data: each seat, console, bunk, mess seat, workbench and service counter carries a `Post` (position, facing, kind, animation). ship-design's "Standard harness" pass also places wander nodes.
- **`CrewNav`** is a per-plan graph: <= 256 nodes (posts, door centres, corridor nodes), edges with lengths, a `NextHop` table (N x N bytes) and a `Dist` table (N x N ushort, 1/32 m). Typical 60 nodes: 11 KB. Positions are int16 at 1/32 m, grid-local. It is compiled once per plan hash **on the server** (or shipped with catalog plans) and sent with the blueprint, like mass properties (review X3): clients never derive it from their own bake.
- **Fixed-point walk:** distance along the route is integer 1/32 m; the hop loop is <= 32 iterations. The float conversion happens only for the instance record.
- **The on-watch pilot post is fixed:** whoever holds it sits at the helm for the whole watch.
- **Conditions:** `Normal`, `Alert` (battle posts, suits on, running at 4 m/s), `Abandon` (to pod posts), `ShoreLeave` (docked: half the off-watch crew are off the ship; they are the "+6 per docked ship" in the station's crowd and return at `Undock`), `Surrendered` (D49: everyone walks to their role's muster post and stays there in the hands-up loop; kit stowed).
- **Epochs (condition and unsafe-room changes).** A change of condition or of the unsafe-room mask is one event `CrewEpoch {condition, unsafeRooms, tick}`, at most one per 30 s per grid. From that tick everyone walks from where they were at that tick to their post under the new state. A walk can outlast 30 s, so the origin cannot be re-derived by chaining earlier epochs: at the epoch tick the server and every client that holds the manifest **store** each alive figure's origin (`node, nextNode, offset`: 6 B, <= 600 B a grid at 100 crew). A client that receives the manifest later gets the stored origins with it while any epoch walk is unfinished (`tick < epochTick + MaxWalkTicks`); after that every figure is on the base schedule again and the origins are dropped.
- **Overrides:** a small per-grid table (<= 32 entries, was 16: `index, fromNode, fromOffset, startTick, toPost, flags`) covers everything that is not the base schedule: a relief walking to the helm, a demoted C2 agent rejoining, a survivor fleeing a breach, an attendant pinned at a counter, a crew member standing aside for a player who took their seat. A hired crew's **standing assignments** (<= 8, "Player-owned crew") are overrides that do not expire. An override expires when its walk ends and the base schedule's post equals `toPost`, or at the next slot. With the table full, whatever needs a row waits (a C2 agent stays C2, a relief stays at its post): nobody snaps to make room.
- **Rooms are ship-atmosphere's design rooms.** ship-atmosphere.md (D38-D42) made rooms *design rooms*: labelled from the as-built bake without the damage layer, never relabelled by doors, breaches or damage, <= 255 per grid. `NavNode.RoomId` and `ViewPortal.RoomId` are those ids, so the mapping step this doc used to need is gone. The server publishes `unsafeRooms`, a `RoomMask` (four words, 256 bits, was one word for 64 rooms) set from `ShipAir` and the dose pass: below 30 kPa or 16 kPa ppO2 for the unsuited, on fire, dose above the threshold. On the wire an epoch carries it as a short list of room bytes (usually 0-4), not 32 B.
- **Decks and room hops are plan data too.** `CrewNavCompiler` gives every room a `Deck` (rooms whose floors are within 1.5 m along the plating axis share one, <= 32 decks) and fills `RoomHop[a][b]`, the number of people-passable design edges (door, hatch, stair, lift; open or closed) between two rooms, clamped to 3. <= 255 x 255 bytes, ~1.6 KB for a typical 40 rooms. Both ride in the `CrewPlan` blob and drive the ladder inside a grid.
- **Doors:** ambient figures pass doors cosmetically (the client plays the leaf animation; the room graph is not touched). **No re-pathing at C1:** `NextHop` is one static table per plan, so a figure whose next hop enters an unsafe room it is not `Suited` for stops at the last safe node, and a post inside an unsafe room is replaced by the role's muster post from the template. Both are pure functions of the epoch's mask, so peers still agree.
- **Zero-g:** an unplated room swaps the walk loop for the float loop on the same route.

### Crew levels inside one grid (D51)

With 100 aboard, "everyone within 20 m in a straight line" is the wrong scope: it reaches through bulkheads and decks and promotes people the player cannot see or reach. The level of a crew member now follows **rooms**, measured from each player in the grid with the plan's `RoomHop` and `Deck` tables. With several players aboard a room takes the nearest ring any of them gives it.

| Ring | Rooms, relative to a player aboard | Server | Client draws | Budget on a 100-crew ship, one player aboard |
| --- | --- | --- | --- | --- |
| **G0 room** | the player's room | **C2**: everyone there, within 20 m along the nav graph (the clip matters only in a concourse or a hangar) | full characters | G0 + G1 together: typically 4-12 people, validated <= 24 (below) |
| **G1 next door** | rooms one people-passable edge away (door open or closed, hatch, stair, lift), within 20 m along the nav graph | **C2**: parked at their posts, active when walking | full characters | counted with G0 |
| **G2 deck** | the rest of the player's deck within 60 m along the nav graph, rooms within 2 hops on the decks above and below, and any room in view through open doors or interior glazing | **C1** | ambient Near figures (section 4), cards past 60 m | <= 48 Near figures (the client cap); typically 15-30 |
| **G3 beyond** | every other room on the grid | **C0**: evaluated on events only | nothing: not evaluated, not drawn | the other ~60: 0 on the server, ~1 us per update on the client for the prefilter |

- **Promotion** happens when a room becomes G0 or G1 for some player; **demotion** when it has been G2 or farther for every player for 20 s. A player running through a ship promotes a room's people 12 ticks before the event applies (the stamp-ahead rule in section 4), at their scheduled poses, at most 8 a tick per grid.
- **The server pays nothing for G2 and G3.** Both are the schedule function, which the server evaluates only on events. The split is what the *client* skips and what a client without the schedule key is *sent* (section 4): G3 rooms are neither.
- **Post-room prefilter (client and server).** A figure that has arrived at its post is in that post's room, and that is two table reads (~10 ns). Only figures whose post room is in a wanted ring, and figures still walking (`tick - leave` under the walk time), get the full evaluation. For 100 crew: ~1 us of prefilter plus ~120 ns for each of the ~30-40 that matter, ~5 us per 10 Hz update instead of ~12 us.
- **Fights widen the rings, inside the caps.** Under hostile boarding or line of fire (the trigger table below), G1 extends to 2 hops round each hostile player and along the nav route of crew sent to repel. Defenders beyond the active cap wait at C1 at the last door outside G1 and take a slot as one frees, so a 100-crew ship is fought through in waves of at most 24, not all at once and not as an empty hull.
- **Validation (plan Warning):** no room together with its next-door rooms may hold more than 24 posts within 20 m of one point. A 30-seat mess is fine only because seated people are parked.
- **Stations** use the same rings per linked grid; a concourse is one room, so the 20 m clip does the work there, as before.
- **Budget check.** One 100-crew ship with a boarding party of four in three rooms: <= 24 active agents (~0.4 ms of job CPU if all walk), <= 48 parked, the other ~30-70 crew at zero. Sixteen such fights do not fit the server cap of 48 active; the priority rule below decides who gets the slots.

### C2: when a figure becomes a character

| Trigger | Scope promoted |
| --- | --- |
| A player character inside the grid (`GridVolume`), or on the hull within 5 m of an open portal | crew in that player's rings **G0 and G1**: the player's room and the rooms next door, within **20 m** along the nav graph ("Crew levels inside one grid"; this replaces the 20 m straight-line sphere) |
| **Hostile** boarding imminent: a collar lock or docking without a granted `DockRequest`, a player's tractor lock without consent (D26), a hull cut in progress, or a player EVA within 50 m of the hull of a ship whose owner's standing with that player is hostile | crew in the entry point's room and the rooms next door to it; the grid goes to `Alert`. A cleared docking, a consented tow or a visitor walking aboard never raises `Alert` |
| Line of fire: small-arms or a player's weapon hit resolved inside a room while a player character is within 100 m with line of sight through open portals or glazing | crew in that room and the adjacent rooms, within 40 m of the shooter |
| Interaction: an interact ray on a figure or on its `ServicePoint` | that one figure |
| Scripted need: succession relief while a player is aboard, a capture `Override` at a `CommandCore` being contested | the crew involved |

- **Caps:** 24 **active** C2 agents per grid (was 16, for 100-crew ships; 12 on a listen server); 48 active per dedicated server, 12 on a listen server (unchanged: this is the cost). **Parked** agents (at a post, no `ExtendedUpdate`) have their own cap, 48 per grid and 256 / 64 per server (was 128 / 32: hired crews on players' own ships are mostly parked people near their owners), because they cost a kinematic capsule and nothing per tick; a parked agent that starts to move needs an active slot. Crowd post density is validated in data so no point has more than 12 posts within 20 m.
- **Priority, so the cap cannot be starved.** Active slots go by class: (1) crew of a grid under hostile boarding or in a fight with a player, (2) interaction and scripted need, including a prisoner being restrained, moved or freed, (3) proximity alone: a player's own hired crew, then staff, then crowd, then prisoners at rest. A higher class takes the slot of the lowest-class agent farthest from any player, which demotes seamlessly as below. Without this, a group idling in a station concourse could hold all 48 slots and leave a ship elsewhere to be boarded against figures that cannot react.
- **Past the cap** the remaining figures stay C1. They cannot fight or block a doorway, but they are not immune: every hit is still resolved against their scheduled capsule (Casualties), and a kill frees nothing for free (it is a body and an incident).
- **Demote** when the agent's room has been outside G0 and G1 of every player for 20 s, and the agent is not in combat, being restrained or carried, wounded-and-being-treated or mid-conversation. (A restrained prisoner at rest demotes like anyone: the restraint is their `Prisoners` row.) Dead agents demote at once (a body record).
- **`CrewDelta`: what a person keeps across levels.** Any crew member whose state differs from the template gets a 4 B entry in a per-grid side pool (<= 32, was 16; persisted): health in 16ths, a kit mask (weapon, suit, tool, keycard: a cleared bit means taken or spent), and flags (surrendered, on parole, hostile to a named player group; restraint is not a flag but a `Prisoners` row, "Surrender, capture and prisoners"). Promotion applies it and demotion writes it. So leaving the room for 20 s does not heal the crew you wounded or free the one you tied up, and a weapon taken from a crew member does not come back when they are promoted again. Kit is reissued only at a `Docked` leg, from the owner's stock. With the pool full, the least-changed entry is rounded to the template (health up, never kit).
- **Promotion (identity and position continuity):** the server evaluates the schedule at the promotion tick and spawns the agent at exactly that pose, with that animation phase, name, appearance seed, role, suit state and (from the dose pool) dose. `CrewPromote {grid, mask, tick}` tells clients to stop drawing those indices as ambient on that tick; the entity's first snapshot is stamped the same tick. Clients also de-duplicate by `CrewId`: a character entity carrying a `CrewId` hides the ambient figure with that id whether or not the event has arrived yet, and the ambient figure is drawn until one of the two arrives, so a late packet gives neither a double nor a gap. **A crew member seen through a window at a console is at that console when you board**, because both come from the same function at the same tick.
- **While C2** the schedule still supplies the agent's goal ("be at post P by tick T") unless a task interrupts it (fight, flee, treat, repair, surrender, talk). Its `Full` bit masks it out of the ambient set everywhere.
- **Demotion:** alive agents write `Alive`, dose and suit back, then get an override "walk from here to where the schedule says". Clients receive `CrewDemote {grid, index, node, offset, tick}` and draw the ambient figure from that spot. Nobody snaps.
- **Cost at the cap:** agents standing or sitting at a post are **parked**: a kinematic capsule and no `ExtendedUpdate`. Walking agents update at 30 Hz, alternating ticks. 48 agents with 40 % walking is ~0.25 ms CPU on jobs; all 48 walking is ~0.85 ms. They step in tick step 7 with players' characters.

### Casualties at each level

The C0 rule is a conservative summary of what C2 would do: nobody dies at C0 who could not have died at C2 (never anyone outside the affected rooms), and the outcome is decided by where the schedule puts each person at the event tick.

| Cause | C0 and C1 (same rule; C1 only shows it) | C2 |
| --- | --- | --- |
| Damage brush (weapon hit, collision, shatter fragment) | evaluate alive crew at the event tick (<= 100 x ~100 ns, only on damage events to crewed grids); anyone whose capsule the brush overlaps dies; anyone within 1.5 x the radius is wounded (counts as absent from their post for `WoundedTicks`) | real damage on the character |
| Breach, decompression (ship-hulls `Breach` edge) | crew in the breached room: `Suited` survive; others survive if `Dist(to the nearest pressure door) / 4 m/s` is under the room's time to 30 kPa (ship-atmosphere's `tau = V / (120 A)` table; an emergency shield or air barrier that holds the breach means nobody is at risk), and get a flee override; otherwise they die at that time, not instantly | the room's real pressure acts on the character |
| Leak | sets the room unsafe after its time to 30 kPa; crew reroute; nobody dies | same pressure model |
| Radiation (exotic-matter 1 Hz room dose) | a lazy per-grid `CrewDose` array (100 x ushort cGy, 200 B) is allocated on the first dose; each second, crew take the dose of their scheduled room; thresholds as exotic-matter | per-character `DoseGy`, same thresholds |
| Small-arms through a window | the round first meets the glazing as it would at C2: it must defeat the glazing material's rating (ship-hulls strength), which makes a damage brush and so a `Leak` or `Breach`; a round that does not is stopped. Past the glass the server tests the ray against the scheduled capsules in the rooms it crosses, **at the shooter's fire tick** (the schedule is a pure function of the tick, so lag compensation is exact and free). The first hit puts the grid on `Alert` and promotes by the line-of-fire rule | real hit, same glazing test |
| Off-screen combat (L0/L1) | a seeded draw per engagement from the loss table (section 6): a number of indices are cleared, lowest-priority roles first | n/a |

- Each casualty is `CrewCasualty {grid, index, tick, cause, node, offset}` (12 B). Clients play the fall where the figure was. The body is a `CrewBodies` entry (<= 32 per grid, was 16; persisted): boarding later finds the body where it fell, as an inspectable entity. Past 32 the oldest record is dropped with whatever kit it still held (lost, never duplicated).
- A dead index stays dead for that hull. Replacements are new people (a new `CrewSeed` salt for that index) taken on at a dock.
- **Loot from people is finite.** A body carries exactly its role's kit minus its `CrewDelta` kit mask, once. Replacement crew draw their kit from the owner's station stock (no stock, no kit). Crowd members carry nothing lootable. Killing crew is never a source of items.
- **One evaluation per grid per tick.** Damage events to a crewed grid are batched: the alive crew are evaluated once for that tick (<= 10 us at 100 crew) and every brush of the tick is tested against the cached capsules, so a shrapnel burst of 50 brushes costs one evaluation, not 50.
- **Same answer at both levels (tested):** the C0 rule uses the same capsule, the same glazing and brush tests and the same tick as C2 would, so whether a given shot or breach kills a given person does not depend on whether a player happened to be in the next room.

### Player-owned crew (D47)

- **The same thing with a different owner.** A player-owned ship or station grid can carry a `CrewManifest` exactly like an AI one: C0, C1 and C2, the ladder inside a grid, 100 at most, the same schedule function, casualty rules, bodies, deltas and stats from the manifest. `OwnerId` is the player or their group. Nothing in the crew code asks who the owner is, except for the items below.
- **The crew plan comes from the ship plan.** Posts are plan data already (seats, consoles, bunks, mess seats). The designer's crew page lists roles, counts, one to three watches and alert posts, and the Standard harness pass proposes one. It compiles to the same `CrewPlan` blob as a catalog class. Two limits: a hire needs a free bunk post, and the grid's life-support crew rating must cover the new head count (ship-atmosphere's Warning becomes a refusal to hire).
- **Hiring.** At a `crew_office` `ServicePoint` (section 5). Each station has a finite `hirePool` of (role, tier) counts, drawn from its seed and refilled over `CrewReplaceTicks`. The sign-on fee is the role's wage x `signOnWatches` plus the role's kit at retail, so killing your own crew for their kit is a loss. A hire takes the lowest free index of that role, gets its identity from `hash(CrewSeed, index, salt)` like anyone, and walks aboard from the dock's `Entrance` node during the next slot. Dismissal happens only at a dock: they walk off and the index is freed. No hiring or dismissal in flight; recruiting a prisoner is the one exception ("Surrender, capture and prisoners").
- **Wages and upkeep (hooks; every number is data).**
  - A wage per role and tier per **watch worked**, debited from the owner's account at each watch change: one event per crewed grid per watch, zero per tick, keyed on ticks so it pauses with the server.
  - Life support already debits by alive count (exotic-matter's ledger).
  - Unpaid watches lower `Morale`; so do casualties. Morale does three things only: below `moraleLow` the crew surrender sooner and will not sign on again; at zero they leave at the next dock.
  - **No loyalty system beyond that in v1:** no mutiny, no sabotage, no betrayal, no per-person opinion.
- **Posts and the helm.** Hired crew fill the plan's posts by the schedule, including the on-watch `Pilot`, so `ShipAi` can fly the ship (section 2). A player who takes a seat supersedes the crew member holding that post, who stands at the post's handover spot (an override) and takes it back `ReliefTicks` after the player leaves. Engineers, gunners and medics give the stats in "C0" above.
- **Orders.** From the owner, or a group member the owner gave `Command`, at any console bound to the flight computer, by comms from another ship, or by talking to a C2 officer. Fixed verbs, no scripting:
  - *Ship orders* (`ShipOrder`, the forced option of the `player_crew` behaviour): `Hold`, `Follow` (escort the owner), `GoTo`, `Dock`, `RunRoute` (origin, destination, cargo item, buy and sell limits), `Mine`, `ReturnHome`, `Flee`, `Surrender`, and rules of engagement (`HoldFire`, `ReturnFire`, `FreeFire`). The behaviour's safety options (`Evade`, `Flee`, `Surrender`) still run by its data unless the order sets `standFast`.
  - *Crew orders* (`CrewOrder`): the condition (`Normal`, `Alert`, `Abandon`, `ShoreLeave`), `RepelBoarders` on or off, the damage-control priority, and up to 8 standing assignments of a named crew member to a post.
  - Crew obey. The two refusals: they will not fire on a surrendered target (the player can, by hand, and owns the record), and they will not walk into a room that is unsafe for them (the C1 door rule).
- **`RunRoute` is a private route.** It creates a `TrafficRoute` owned by the player with one hull and a `TrafficShips` row, so the hull runs the same legs, books the same dock slots and moves real stock at real prices (section 6). At most `maxAiHullsPerPlayer` (4; 16 per group) hulls fly without a player aboard. The hull is pinned: never below L1, zero per tick on a schedule leg like any L1 hull.
- **When the owner logs off** (nobody with `Command` for that grid online, server still running):
  - A ship under way finishes what it is doing to the next dock (`RunRoute`: the current trip; anything else: `ReturnHome`), then stays `Docked` with its crew on `ShoreLeave`. Laid up like that it earns nothing and is paid nothing. No new trip starts (`offlineTrading` is false; open question 2).
  - A ship in an engagement stays in it until it ends, fighting, fleeing or surrendering by its standing order and the behaviour's data. Logging off saves nothing.
  - The owner's offline character aboard is warp.md's offline crew, unchanged. A Stepped fight never rolls against a player character: an offline character shares the hull's fate by warp.md's rule and nothing else.
  - A crewed station keeps working (controller, services, defence). Its wages come from the station's own till; when the till is empty the staff stand down (services close) and wait. They never desert while the owner is offline.
  - With the server empty nothing advances at all (the standing user decision).
- **No farming.** Kit is bought, wages are a sink, the hire pool is finite, a dead hire is a body with finite kit, and a private route moves stock that exists.

### Surrender, capture and prisoners (D49)

Crew and ships can surrender, as an AI behaviour and as a player option. One state machine serves a person (a `CrewId` or a player character) and a ship.

| State | Entered by | What holds while in it | Leaves to |
| --- | --- | --- | --- |
| `Surrendering` | the AI rule below, a player's `Surrender` command, or a `ShipOrder.Surrender` | 300 ticks of notice. A person lowers the weapon and raises hands; a ship safes its weapons, goes to dead-man hold and sets its transponder to `Striking`. **No protection yet**, and attackers owe it nothing: AI attackers keep firing until `Surrendered` | `Surrendered` after 300 ticks; back to `Free` if the surrenderer cancels inside the window: no record, but the weapons stay safed for the 10 s below and `surrenderCooldownTicks` (60 s) must pass before it may offer again, so the notice cannot be flickered to buy a pause or a feint |
| `Surrendered` | the timer | **Protected:** harming it is `KilledSurrendered`. **Obliged:** no weapon use, no thrust beyond the hold, no warp spool, beacon on; a person stays within 5 m of where they stood unless a captor orders otherwise; a ship's collar, holds and tractor consent (D26) are open to the captors' groups; a ship's crew are in the `Surrendered` condition | `Captured` when a captor takes custody; `Released` if every captor says so or leaves (20 km for a ship; the room and next-door rooms for a person), or if none of them has moved to take custody for 120 s (no `AcceptSurrender`, nobody closing inside 5 km; the clock stops while one is, so a surrender cannot be waited out from under a captor who is on the way); `Free` on a breach: **treachery** (using a weapon) with a `Perfidy` record, **flight** (thrust, spool, walking off) with no record, but protection ends on that tick and the cooldown applies |
| `Captured` | custody. A person: restrained by a 3 s interaction within 2 m, which makes a `Prisoner` row. A ship: a member of the captor's group at a seat that reaches the flight computer, or the prize obeying the captor's `Follow` or `Dock`; the hull is flagged `Prize` | a prisoner, or a prize. The hull keeps its `OwnerId` | `Released` (by the captor, by ransom, or handed over at an `authority` service point); rescued (own side opens the restraint in 3 s, or retakes the helm); recruited (people only); dead |
| `Released` | above | `ParoleTicks` (10 min): attacking the releasing group in that time is `Perfidy`. An AI ship goes home; an AI person walks to a muster post or leaves at the next dock | `Free` |

**The AI decision rule.**
- *Ships* (in decide, 1 Hz, sticky like any option): `Surrender` is a utility over cannot-flee, disabled drive, threat ratio, hull lost, crew lost and low morale, with a floor (default 0.8). Two gates: `neverSurrender` on the behaviour (autonomous classes, fanatics by data), and the captor owner's `mercy` at or above `mercyMin`. Shipped defaults: haulers surrender early, pirates surrender to security when they cannot flee, security never surrenders to pirates. Stations do not surrender as ships: ownership still moves only by D27's capture, but a garrison whose members have all surrendered no longer contests the `Override`.
- *People* (C2 agents, in the task update): surrender when wounded below `surrenderHealth`, or disarmed, or armed hostiles in G0-G1 are at least twice the armed friends there, or the grid's condition is `Surrendered`; and a draw `hash(seed, index, tick / 600) % 256 < p(role, morale, mercy)` passes. The draw is integer and server-side, so it is the same on a replay.
- *C0 and C1 crew* surrender only with their ship (the condition), since nobody is there for them to surrender to. A person's own state lives in `CrewDelta.Flags`, so it survives demotion and a save.
- **`mercy`** is one server-only byte per (actor owner, victim owner group), derived from the incident log: it falls with each `KilledSurrendered`, `PrisonerHarm` and `Perfidy`, and recovers with each release, ransom or hand-over honoured. Players who shoot prisoners stop being surrendered to: crews fight on, flee, or scuttle the cargo.
- **The same rule runs in every fight mode** (section 1): a Stepped or One-shot fight can end in a surrender, which becomes a `Raided` hull with prisoners taken instead of a wreck.

**The player's options.**
- On foot: `Surrender` (hold 2 s). From the helm or a command console: surrender the ship. To hired crew: `ShipOrder.Surrender`.
- As captor: hold fire (that is acceptance), restrain, board, take the cargo, order a surrendered AI ship to `Follow` or `Dock` (it complies as far as its fuel goes), release, ransom, hand over or recruit.
- **A captive player is never held out of the game:** after 60 s restrained, `GiveUp` is the ordinary death and respawn rule with the kit left to the captor. Logging off while restrained is the same after 60 s. Player prisoners are characters with a `Restrained` flag, never manifest rows.

**Rules against abuse.**
- *Surrender is to the fight, not to a friend.* The **captors** are the hostile members of the surrenderer's engagement: the owner groups that acted against it within `CombatTicks` (for a person, the armed hostiles in its rooms). A group that is the surrenderer's own, is allied to it, or has not fought it cannot accept, take custody or release. A release by one captor leaves the ship `Surrendered` to every other captor still within 20 km. So a ship about to lose cannot hand itself to an accomplice and turn its real attacker into the criminal, and an accomplice's token lock buys nothing, because their release binds nobody else.
- *Surrender wipes no record and saves no loot.* The surrenderer's own incidents, fines and bounties stand. The holds are open to the captors. A player's kit goes to the captor on `GiveUp` exactly as it would on death, so giving up a fight is never cheaper than losing it, only less final for the ship.
- *No combat log.* A ship or a character that disconnects while `Surrendering`, `Surrendered` or `Captured` stays in the world in that state until the engagement ends, like any member ("When the owner logs off").
- *Fake surrender.* Breaking an obligation by treachery while `Surrendered`, or the parole while `Released`, writes `Perfidy` against the surrenderer's owner. Protection ends on that tick, and for `noQuarterTicks` nobody is obliged to accept a surrender from that owner. Friction, not prevention: a surrendered ship's weapons stay safed for 10 s and re-arming shows on its transponder; a surrendered player's weapon is locked in the holster for 10 s. **AI never fakes a surrender in v1.**
- *Surrender is not armour.* Damage applies as always; only the record changes. A shot whose fire tick is before the `Surrendered` tick is an ordinary shot, and the 5 s `Surrendering` window is the attacker's notice.
- *Surrender is not an escape.* A surrendered ship cannot warp, dock on its own, or lock its holds, and it stays an engagement member until released or taken, so it is not demoted out of reach.
- *Killing or harming the surrendered* is `KilledSurrendered`: a `Hostile` record with aggravation (interim: three times the standing loss of an ordinary kill, top priority for security response, fines for the law doc), and `mercy` falls.
- *Custody is a duty.* A prisoner who dies in custody of vacuum, dose or neglect is the custodian's `PrisonerHarm` unless another actor is on the cause (ship-atmosphere's edge attribution comes first). Prisoners breathe: life support counts them.
- *No laundering.* Surrender and capture never change `OwnerId`. A prize is `Stolen` to NPC markets like any taken hull (section 6).
- *No ransom mint.* A ransom is a `Hail` with a price. An AI owner accepts when the price is at most `ransomMax` x the value of what is held, with a hull counted at its **salvage** value (what stripping it would give, never retail: section 6 already refuses a taken hull NPC buyback for this reason). It pays **in kind from its station stock**, items that were debited from somewhere, until the factions/law doc makes owner treasuries a conserved ledger. No doc defines a station's credits as finite today, so the draft's "finite treasury" was a faucet: capture, ransom at half value, repeat every `ReplaceTicks`. No credits are ever created to pay a ransom. One ransom per hull or person per `ReplaceTicks`. A player owner decides for themselves.
- *No prisoner farming.* A prisoner is one person moved, never copied; their kit is the mask that moved into their row.

**Prisoners are manifest entries.**
- `Prisoners` is a lazy side pool keyed like the other crew pools: <= 16 per grid, 32 B each: origin hull or station, origin index and seed salt (so the name and face stay the same person), origin owner, custodian, health, kit mask, flags, post, since-tick. With the pool full a restraint is refused; a row is never dropped or overwritten. The crew of a prize are not rows: they stay on their own manifest under the `Surrendered` condition, and only people restrained one by one or taken off their ship become `Prisoners`.
- **One representation.** A restraint is always a `Prisoners` row on the grid where the person is, their own ship included; no "restrained" flag exists anywhere else (the draft had one in `CrewDelta` as well, which is two truths for one person). The person's `CrewDelta` entry (health, kit) moves into the row and is freed. At every tick a person is exactly one of: an `Alive` bit, a `Prisoners` row, a body. A split hull's rows follow the piece that holds their post.
- **Moved, not copied.** Capture clears the person's bit in the origin `Alive` mask with cause `Captured` (no body) in the same step that writes the row. If the origin has replaced that index by the time they come back, they return to the owner's replacement pool instead: one fewer replacement to hire, no kit debited.
- **On the captor's grid** a prisoner holds a `Brig` post (a new post kind in ship-design), or with no brig the muster post of the room they were restrained in. They are not crew: not in the captor's masks, no stats, no schedule. They draw as C1 figures in the restrained loop for whoever may see that room, and promote to C2 by the ring rule at the lowest priority.
- **At every ship level.** The rows follow the hull slot like `CrewDelta`. A player's hull holding prisoners is pinned. AI captors carry them in the row: pirates' prisoners are ransomed off-screen after `RansomTicks` (back to the owner's pool, the owner's stock debited) or stay in the base's manifest; security hands them over at its station.
- **Outcomes:** release (at a dock, into a pod, or back aboard their own ship), hand-over at an `authority` `ServicePoint` (bounties are a hook for the law doc), ransom, rescue, death, or **recruitment**: a hired-crew offer at 1.5 x the wage or more, accepted only if the origin's morale was low and the recruiter's `mercy` is at or above `mercyMin`; they become a hire with a new index on the recruiter's manifest, with the kit they hold; the sign-on fee is paid as for any hire (less the kit), and a bunk and life-support rating are needed as for any hire.
- **Escape pods** (the question this used to leave open): on `Abandon`, crew who reach pod posts move to the pod grid's own manifest by the same move rule (a pod is its own grid with its own air, ship-atmosphere). A pod is a stray transmitting `Distress`. Recovered by its owner's side or a neutral, the occupants are rescued and go back to the owner's pool at the next dock; recovered by a hostile, they are `Surrendered` on recovery.

## 4. Seeing crew through windows

### Windows are data from the hull bake (change to ship-hulls.md)

- **`Glazing` materials:** a flag on `data/materials` entries (transparent, with strength like any material). Glazing surfaces draw in the transparent pass (inventory.md already reserves it for "ship glass").
- **`ViewPortal`s:** the bake clusters glazing surface cells per (room, dominant normal) into oriented rectangles: `{RoomId, centre, normal, halfU, halfV}` in int16 1/32 m, <= 64 per ship, largest first. `RoomId` is the design room id (section 3). They ride with `CrewNav` in the server-sent `CrewPlan` blob.
- **Breaches are portals too.** A `Breach` edge (a hole of >= 0.5 m, already replicated with the damage layer) counts as a dynamic view portal into its room, as a disc of the damage brush's cross-section, so figures and bodies in a holed room are not culled while the room is in plain sight. <= 8 per grid, largest first.
- **Glazing is never LOD-skipped.** ship-hulls drops `Interior` brushes at LOD >= 2, so a far ship has no room behind its glass. From LOD 2 the glazing shader does **interior mapping** against the room's AABB (one ray-box per fragment, wall tint and light level from the room record), so a lit window still reads as a room.
- **Room light:** one RGBA per room (`RoomLight`: powered and lit, emergency, dark), driven by the room's bus state, already known to clients that stream the ship.

### Which figures are evaluated

Per client, on a job, staggered so each grid updates at 10 Hz. Steps 2-3 are for grids whose schedule **key** the client holds (its own, and open ones: "Who may know where the crew are"). For a private grid the server runs steps 2-3 for that client and sends the figures in view as walk segments; steps 4-5 are then the same.

1. **Grids in ambient range:** crewed grids within 600 m of the camera, at most 16, nearest first. A station counts per linked grid.
2. **Visible rooms:** for each `ViewPortal`: facing (`dot(normal, toCamera) > 0.1`), in frustum, projected area >= 64 px2. The union of their `RoomId`s is a room bitset. If the camera is inside the grid, the rooms are ring G2 of section 3's ladder (the hull's own room visibility on the camera's deck) and this path only handles glass looking into other rooms. ~30 ns per portal.
3. **Figures:** run the post-room prefilter over the alive, non-`Full` crew of that grid (<= 100 x ~10 ns), fully evaluate those whose post room is in the bitset and those still walking (~30-150 ns each), and keep the ones whose node's room is in the bitset, plus bodies and prisoners in those rooms. <= 12 us per grid update with everyone in view; ~5 us typical.
4. **Size and count:** drop figures under 4 px tall (about 420 m at 1080p and 60° vertical FOV; scales with resolution and FOV). Keep the nearest 256 overall.
5. Write one 32 B instance per figure into a persistent mapped buffer: `relOrigin (float3), velocity (float3, for extrapolation between 10 Hz updates), yaw, anim, animPhase, appearanceSeed, roomLight index, portal index`.

CPU: ships are <= 100 figures (<= 12 us an update, ~5 us typical); a station grid with 100 staff and a full crowd of 192 is up to 292 scheduled figures (~35 us), of which the nearest 256 are kept. Worst case, 16 full station grids: ~0.56 ms of job time per 100 ms, about 95 us a frame at 60 fps; typical (a station and a few ships) is under 10 us a frame. All on a job, ~0 on the main thread beyond one buffer flip. Zero allocations (fixed arrays).

### How they are drawn

| Tier | Range (1080p) | What | How |
| --- | --- | --- | --- |
| **Near** | <= 60 m, cap 48 | ~800-triangle figure, 20 bones, 9 baked loops (stand, console, walk, run, eat, sleep, float, fallen, and hands-up / restrained for D49) in one bone-matrix texture; 4 body types x seeded palette and outfit by role | one instanced indirect draw in the opaque pass. The hull at LOD 0-1 has real interior geometry and a real hole behind the glass, so ordinary depth testing does the rest |
| **Mid** | 60 m to the 4 px limit, cap 208 | a two-triangle card with a 16-frame silhouette atlas per loop, tinted by `RoomLight` | **portal-projected cards:** the vertex shader projects each card vertex through the camera onto its `ViewPortal`'s plane (plus a 2 cm bias toward the camera) and clips to the portal rectangle with four clip distances. The card then lies on the glazing surface, so it depth-tests correctly against everything in front of the hull, needs no interior geometry (LOD >= 2), and its perspective is exact by construction |
| **Far** | beyond the 4 px limit | no figures | the glazing shader modulates the window's emitted light with a slow seeded flicker scaled by the room's scheduled head count (one byte per portal at 1 Hz), so a crewed, lit ship still looks inhabited |

- **The tier follows the hull's LOD, not only the distance.** A 3D figure needs the real room behind the glass, which exists only where the bricks around that portal are at LOD 0-1 (ship-hulls drops interiors at LOD >= 2). So a figure is Near only if it is within 60 m **and** its portal's bricks are at LOD 0-1; otherwise it is a card. Without this rule a figure would be drawn in the opaque pass inside a hull with no interior and the interior-mapped glass would composite over it. In the hull stage ladder (section 1): Near needs stage H0 at that portal, cards need H1, and from H2 down there are no figures and the window flicker is the hull's `LitFraction` (cosmetic on a private hull: section 1). Only 4 hulls are H0 at once, so at most 4 ships (the one the camera is in included) ever show 3D figures; that is inside the 48-figure cap by a wide margin.
- **Inside a grid** the same two tiers serve ring G2 (section 3): Near figures on the camera's deck within 60 m, cards beyond or through interior glazing. Rings G0 and G1 are real characters and are never drawn by this path.
- **Lighting:** room light colour and level, plus the sun term through the portal for near figures. Dark rooms draw silhouettes only when something bright is behind them; otherwise the figures are skipped.
- **GPU estimate:** near 48 x 800 = 38k skinned triangles covering few pixels, mid 208 cards: ~0.03-0.05 ms from outside. Inside a busy station concourse (48 near figures filling the view) ~0.1-0.15 ms. Two indirect draws, one pipeline each, no per-figure descriptors. Budget: <= 0.15 ms GPU, <= 0.02 ms main-thread CPU, measured in `--crew-bench` (a station flyby and a concourse walk).
- **Memory:** bone texture 9 loops x 32 frames x 20 bones x 2 texels ≈ 90 KB; silhouette atlas ~1.1 MB; meshes ~200 KB.

### Station crowds

- Staff (<= 100 per grid) are scheduled crew like a ship's. **Crowds** are anonymous: `crowd(j, tick)` uses the same function over the grid's crowd posts and wander nodes with a per-grid `CrowdSeed`; count = `min(CrowdMax, base + 6 x docked ships)`, <= 192 per grid. They cannot be individually tracked across sessions, are always open (their schedule key is public: a crowd is not intelligence), and promote to C2 by the same ring rule (G0 and G1, within 20 m) at the lowest priority (they get an identity for as long as they are C2 or dead).
- **The count is replicated and never pops.** `CrowdCount {grid, count, fromSlot}` (8 B) takes effect at the next slot boundary. Figure `j` exists while `j < count`: one that starts existing walks in from the grid's nearest `Entrance` node (a dock tube, a lift) during that slot, and one that stops existing is given an `Entrance` node as its post and is dropped on arrival. Nobody appears or vanishes mid-concourse.
- **A killed crowd member stays dead.** `CrowdDead` is a 192-bit mask per grid in the manifest; a set bit removes `j` from the function and leaves a body. The bit clears after `CrowdReplaceTicks` (data, hours), when a new person with a new seed salt walks in from an `Entrance`. Without it the same figure would walk back out of the function a tick after being shot.
- The 256-figure client cap applies across staff and crowd; nearest win.

### Who may know where the crew are (D48)

The first design let every client within 1 km compute every crew position, because it held the schedule's inputs. D48 makes crew positions something you scan for, so an unscanned private ship must hand a client nothing. A client now learns where a crew member is through exactly three channels, and the server sends nothing that depends on the schedule outside them.

1. **Key.** The schedule key is `CrewKey {CrewSeed, GridPhase, ScheduleSalt}` with the resolved template (`CrewBill`), the masks, epochs and overrides. A holder evaluates everything locally at zero bandwidth, exactly as section 3 describes. It is given to:
   - the grid's owner group and anyone the owner grants (hired crews, D47);
   - every client, for grids whose `crewPrivacy` is `open`. That is data on the ship class or station deck. Defaults: open for lawful civilian shipping and public station decks; **private** for military, security and pirate classes and for every player-owned grid until its owner opens it.
   - An open grid closes toward an owner group at that group's first hostile act against it: the `Alert` epoch redraws `ScheduleSalt` (server random, mixed into `Perm` and `leave`) and sends the new salt only to holders still entitled. The closed group falls back to sight. What it can still infer, the battle posts of a ship class it has seen before, is fair knowledge.
2. **Sight.** For a client without the key the server runs that client's visibility test itself, at 2 Hz and 0.5 s ahead: the portal test of "Which figures are evaluated" from outside (facing glazing or a breach, within 600 m, >= 64 px2), or, aboard, the client's rings G0-G2 limited to rooms joined to its own by open portals or interior glazing. For each figure in a room in view it sends a **walk segment**, `CrewSight {index, anim, fromNode, fromOffset, toNode, startTick}` (12 B, batched under one header per grid): from where the figure is to where it will stop, cut at the last node inside a room in view. The client draws the same closed-form walk from the segment, so the picture is identical to the keyed one. `CrewSightEnd {grid, room}` (4 B) when a room leaves view. The far tier's head count is one byte per portal in view, on change.
   - **The test uses only what the server owns:** the character's eye position, no frustum, and a reference 1080p view at the narrowest field the optic in hand allows. Nothing a client reports about its camera can widen what it is sent.
   - **It does not test for things in the way** (another hull, a rock between). The accepted remainder: a window that faces a viewer within 600 m gives up its room even when something stands between them. Bounded (window rooms, 600 m, facing), and cheaper than a server ray per portal while Jolt has no native `CastRay` (review.md P1).
3. **Scan.** A `ScanCrew` dwell (below).

- **Manifest events are for key holders.** `CrewPromote`, `CrewDemote`, `CrewOverride`, `CrewEpoch`, `CrewCasualty`, `PrisonerMove` and a person's `SurrenderState` on a private grid go only to holders of its key. A keyless client gets their effect as sight segments and room-filtered entities, or not at all. A ship's own `SurrenderState` (its transponder) is public.

- **Layout is not secret; people are.** The `CrewPlan` blob is split: `CrewNav`, decks, room hops and `ViewPortal`s travel with the blueprint as before (a client needs them to draw anything), and `CrewBill` (template, role tables, watch plan) travels with the key.
- **C2 agents of a private grid** replicate to a keyless client only while they are in a room that client has in sight or under scan: a room filter on top of frames' interest table. Aboard and face to face that is always the case.
- **Hits do not change.** The server resolves every shot against the scheduled capsules at the fire tick whoever can see them. A keyless client is told of a casualty only if the room is in its sight or scan.
- **Cost.** Server: one evaluation per watched private grid per update, shared by all its watchers (~5-12 us), plus ~2 us per watcher for the portal test: ~40 us a tick on a job with 64 private grids being watched. The bound is the ambient cap of 16 grids a client: 64 clients each watching 16 different private grids is ~0.35 ms a tick on a job, and no client can ask for more than that by where it looks. Bandwidth: 12 B per figure in view per walk; a 100-crew warship with 20 people at its windows is ~240 B on first sight and under 20 B/s after. Keyed grids stay at zero.
- **Leak test:** run one scene twice with different `CrewSeed` and `ScheduleSalt` on a private grid and a client that has no key, no sight and no scan; the two client streams must be byte-identical.

### `ScanCrew` (D48)

A sensor action that reveals crew positions on another ship or station. Sensors have no doc yet (README "Missing systems"), so the numbers below are interim data in `data/sensors/crewscan.json`; the mechanism and the privacy rule are what this doc fixes.

| | Ship scanner S / M / L (`fn.sensor.crewscan`) | Hand scanner (EVA, boarding) |
| --- | --- | --- |
| Nominal range `R_nom` | 2 / 5 / 10 km | 30 m |
| Power | 20 / 50 / 150 kW | 2 kW |
| Dwell to the first result | 240 ticks (4 s, as rings' composition scanner) | 240 ticks |
| Cone | 20 degrees half angle; the target's bounding sphere centre must stay inside | 30 degrees |

- **Target:** any crewed grid that is an entity (L1 or above, so within `EntityRange`); a station is scanned one linked grid at a time. Scanning does not promote the target: positions come from the schedule, which exists at every level.
- **Quality, per room:** `q = min((R_nom / R)^2, gainMax) x T x S x B`, with `gainMax` 16. Without the clamp the range term has no ceiling: a small scanner at 100 m would have a gain of 400 and read through any armour or baffle, so shielding would be dead mass at exactly the ranges boarding happens at. With it, a room whose `S x B` is under 1/16 cannot be read from outside at all, a baffled room (0.25) reads exactly only inside `R_nom / 4`, and an unshielded one reads exactly inside `R_nom / 2`.
  - `T` is `RingObscuration.Transmittance(scanner, target, Thermal)`, rings-and-asteroids' line-of-sight term: ring particles, band dust and shatter `DustCloud`s all cut it (thermal coupling 1 / 0.5). One call per scan update, ~0.3 us. `(R_nom / R)^2 x T >= 1` is that doc's active-sensor range law.
  - `S` is the room's `ScanShield` byte from the bake: the transmission of the least-shielded path from the room to the outside, a product over the walls on it of `exp(-scanOpacity x thickness)` per material (`data/materials`; glazing ~1, armour and lined bulkheads low). A room open to space is 1. Deep rooms are harder to read than outer ones, and shielding costs mass.
  - `B` is 0.25 in a room inside a powered `ScanBaffle`'s radius (a component, 20 kW per 10 m of radius, on the wiring like anything else, so it can be cut: D17), else 1.

| `q` for a room | What the scanner's group gets for that room |
| --- | --- |
| under 1 | "no return" (shown as unread, which is not the same as empty) |
| 1 to 2 | a head count |
| 2 to 4 | positions to the nearest nav node (2-4 m), no identity |
| 4 or more | exact walk segments (as sight), role class, armed or not; C2 agents and prisoners included and marked. Player characters too, at 1 Hz: that goes past D48's "crew" and is a **proposal** (open question 5) |

- **A snapshot with an age, or a live track.** The first result is a `ScanResult` stamped with its tick: a snapshot, drawn as ghosts through the hull with the age shown. **While the dwell continues** (target in the cone and in range, scanner powered) the server keeps the track live: segment changes for rooms at `q >= 2` as they happen, counts at 1 Hz for rooms between 1 and 2, `q` re-evaluated at 2 Hz as range, aspect and dust change. When the dwell breaks the stream stops on that tick; the ghosts finish their last segment, freeze and age, and leave the view after 60 s (the log keeps them).
- **Never the key.** A scan sends positions, not the seed: the knowledge decays the moment the dwell ends, and a re-posted crew makes an old snapshot wrong.
- **Shared with the scanner's group**, as rings' `AsteroidComposition` is (exotic-matter's privacy pattern).
- **A scan is an emission.** The target gets `ScanAlert {bearing, strength}` if it has a powered sensor suite. AI reads it as the being-scanned input: lawful ships hail; military and pirate ships go to `Alert`, which re-posts the crew. A scan is not an `IncidentRecord` by itself; inside a station's `ProtectRadius` it is `Logged` (whether that is an offence is the law doc's call: open question 5).
- **Counter-measures:** range and aspect; hull and bulkhead shielding (`S`); a `ScanBaffle` (`B`); sitting in a dusty band or behind a shatter cloud (`T`); going to `Alert` on a `ScanAlert`; breaking the dwell by manoeuvre or by killing the scanner. Jamming and decoys belong to the sensor doc. Turning the lights off does nothing.
- **Limits and cost:** one target per scanner, <= 4 scanners per ship, <= 64 concurrent scans per server.
  - **Nobody can take every slot:** <= 2 dwells per player and 8 per owner group, and 16 of the 64 are kept for AI. (With only the server cap, sixteen ships of one group could hold all 64 and switch scanning off for everyone else.)
  - **A broken dwell starts again from zero** (240 ticks), so the first-result cost and its <= 1.4 KB cannot be asked for more than once per 4 s per scanner: <= 350 B/s to the scanner's group at worst.
  - **A scan cannot be used to pester:** a target raises at most one `ScanAlert` per scanner per 30 s, and an AI answers scans with at most one hail or one `Alert` epoch per 30 s (the epoch limit), so toggling a scanner neither spams hails nor keeps a crew running to battle posts. The first result is one grid evaluation (<= 10 us) and <= 255 room bytes; a live track is event-driven (~1 us a tick at the cap). `ScanResult` is 12 B plus 2 B per readable room plus 8 B per figure: <= 1.4 KB for a 100-crew ship read at full quality, once.
- **AI uses the same gate.** A scouting AI (section 2) gets the head count and positions its scanner's `q` would return and no more.

### Multiplayer

- **Zero bandwidth for motion, for holders of the key.** Clients evaluate the same function from: the `CrewPlan` blob (`CrewNav`, decks and room hops, `ViewPortal`s, fetched by content hash with the blueprint; `CrewBill`, the resolved template and role tables, only with the key) and `CrewManifestNet` (`CrewPlan` hash, the key, `Alive`, `Full`, `Suited`, morale, the current epoch (condition, unsafe rooms, tick), overrides, bodies, prisoners, crowd count and `CrowdDead`: ~80 B typical with the three 16 B masks; <= 850 B with 32 overrides and 32 bodies; <= 1.5 KB in the rare case that epoch origins ride along; up to 512 B more on a grid holding 16 prisoners), sent when a crewed grid comes within 1 km and on change. A client without the key gets only the `CrewPlan` hash and the room lights, then sight segments.
- **Corrections are events:** `CrewEpoch` 20 B plus one byte per unsafe room (and 4 B when it carries a new salt), `CrewCasualty` 12 B, `CrewPromote` 24 B (a two-word mask), `CrewDemote` 16 B, `CrewOverride` 12 B, `CrowdCount` 8 B. Revision 2 adds `CrewSight` 12 B, `CrewSightEnd` 4 B, `ScanResult`, `ScanAlert` 12 B, `SurrenderState {who, state, captor, tick}` 16 B and `PrisonerMove` 16 B. All reliable and tick-stamped, applied on their tick like frame changes. Non-urgent ones (a proximity promotion, a watch-driven override, a crowd count) are stamped 12 ticks ahead so they arrive before they apply; urgent ones (a casualty, a line-of-fire promotion) are stamped now and a late arrival is covered by the `CrewId` de-duplication (section 3) or plays the fall late.
- **C2 agents** replicate as character entities (~20 B per update, frames' interest table). Parked agents send nothing until they move.
- **No leak (D48; this replaces the "accepted leak" of the first draft).** A client that holds a grid's key computes every position on it, and that is only ever its own grid or an open one. For a private grid a client holds exactly what it can see through glazing, a breach or an open door, and what a scan returned.
- **Test:** the server and a client evaluate 10,000 random `(manifest, index, tick)` triples and must agree bit-for-bit on `(node, offset)`, including across an epoch with unfinished walks, with a client that joined after the epoch, and between a Debug and a Release build on two machines (the CRC pattern rings-and-asteroids uses for its ring tables). A keyless client's figures, drawn from sight segments, must land on the same `(node, offset)` as a keyed client's at every tick the room is in view.

## 5. Stations

- **Populated the same way:** a `CrewManifest` per linked grid (staff, <= 100), crowds, C2 by the ring rule. A **player-owned station** is staffed by hiring like a ship (D47, section 3): its `StationController`, service points and defence run on the posts its owner filled, and keep running while the owner is offline. Stations are `Frozen` or anchored grids, so they are L1 until a player is within 2 km and get interior bubbles only where players are.
- **`StationController`** (server, one per station, 2 Hz; ~3 us per update while any ship is in its zone, 0 otherwise). It is the AI of the station, gated like a ship AI: it needs a powered traffic console and an on-watch `Controller` post filled. Unfilled: no new clearances, lights go to "uncontrolled" and visitors dock at their own risk and liability.
  - **Traffic control:** a ring of `DockSlot`s (pad or collar, size class, booked-until tick), approach lanes and hold points (station data; rings-and-asteroids' defence already keeps clear of "declared approach lanes"), a speed limit inside `ExclusionRadius`. `DockRequest` returns a slot, a lane and a window, or a hold point and a queue position. AI ships and players use the same request; AI haulers at L0/L1 book the same slots through their `Hold` and `Dock` legs, so a player really does wait behind scheduled traffic and can really take a slot an AI wanted (the AI's `DelayTicks` grows).
    - **Bookings cannot be hoarded.** One live booking per ship and `MaxBookingsPerGroup` per owner group (the rule wormholes.md's `TrafficControl` already has); a booking lapses if its ship is not on the approach lane when the window opens. So a group cannot starve a station's trade, or another player, by holding its slots.
    - **AI does not crowd players out of shared queues.** Scheduled traffic may hold at most `aiQueueShare` (data, default a quarter) of a station's dock slots of each size class and of a gate's `MouthQueue` ring (64 slots a direction) in any window. A player's request takes an unconfirmed AI slot beyond that share (the AI's `DelayTicks` grows). L0 and L1 bookings use the same rings, tunnel caps and direction windows as everyone's.
  - **Tractor docking** is offered only on request (D26): the request carries the consent flag for the station's emitter group.
  - **Gates:** a station gate's wormhole `TrafficControl` (wormholes.md, `data/traffic/*.json`) is unchanged; AI ships are ordinary requesters of catch slots and `MouthQueue` slots.
- **Defence automation:** `StationDefence` (rings-and-asteroids: debris, AM-fed emitters only, D28) and `StationShield` are unchanged and need no crew. **Security response** is new and is an AI behaviour, `station_security`: on a `Hostile` or `Reckless` `IncidentRecord` inside `ProtectRadius`, the controller hails, then launches up to its garrison (docked security hulls from its `TrafficShips` rows: L0 > L1 > L2 through an `Undock` leg, so response takes real time and can be exhausted). Station turrets wait for the combat doc. During a declared siege (D27) the garrison fights the besiegers; capture still goes through the `CommandCore`, where the defending staff in the attackers' rooms and the rooms next door (rings G0-G1) are C2 and contest the `Override`. Staff who have surrendered (section 3) do not contest. Security also scans: a `ScanCrew` on a suspect hull that returns prisoners or unlisted people is a finding for the law doc.
- **Shops and NPC services:** a `ServicePoint` is a component (console family) with a `Post` and a service id from `data/stations/services.json`: vendor, shipyard desk, dockmaster, refuel, medic, mission board, plan market, and (revision 2) `crew_office` (hire and dismiss crew from the station's finite `hirePool`: D47) and `authority` (hand over prisoners, pay or collect a ransom, register a prize: D49; what it pays is the law doc's).
  - The service is open while the scheduled attendant is alive and at the post (a schedule lookup at interaction time, ~100 ns). Otherwise it is closed until the next watch, or runs as an automated kiosk at the service's `kioskMarkup` if the component has one.
  - **Interaction is what promotes.** Using the service point or aiming an interact ray at the attendant promotes that one figure to a parked C2 agent (it turns, talks, can be hurt) and pins it at the post with an override for the length of the session plus 20 s. Walking past a counter at more than 20 m promotes nobody.
  - Trade itself talks to station stock (section 6), not to the NPC.

## 6. Economy and world

- **AI traffic is the consumer and producer of goods.** Each station has a `StationStock` (item id > quantity, capacity; items and recipes are the crafting data in `data/`, exotic-matter's `Items` and `Recipes`). Station industries are recipes run as batch events (`BatchRunner`), never per tick. A route moves `min(hold capacity, origin surplus)` of its item per trip, debited at `Undock` and credited at `Dock`.
- **Prices follow stock:** `price = reference x clamp((target / max(stock, 1))^0.5, 0.5, 3)`, evaluated on demand. ship-design's `buyback` caps stay as they are. Players selling into a deficit or buying out a surplus move the same numbers the haulers do.
- **Routes** are generated at world generation from the seed (for each deficit, the nearest surplus within warp reach; cross-system routes through natural wormhole gates), stored as world data and persisted. They are not re-planned continuously: a slow planner (every 10 min of played time, ~50 us on a job) may add a hull to the worst-supplied route or retire one from a glutted route, within each owner's hull budget.
- **Piracy targets:** a hauler's cargo is real (section 1). A disabled, boarded or destroyed hauler yields exactly its `CargoQty`, in crates. AI pirates work `pirate` behaviours around routes with a low patrol level; their loot goes to their base's stock.
- **Raiding cannot be farmed (tested).** Everything a player can take from AI traffic was debited from somewhere:
  - *Cargo* left the origin's stock at `Undock`. Loot from a route is bounded by that route's real throughput, and each loss shrinks it (delays, `ReplaceTicks`, a short origin).
  - *A replacement hull* needs its bill of materials from the owner's shipyard stock and `ReplaceTicks`; with no stock the slot stays empty. A hull is never rebuilt with its old cargo, fuel or crew.
  - *Mined ore* on an L0 `miner` route is the one faucet: it is capped per route by data (`yieldPerTrip`, charged against the belt's or ring's finite site budget in rings-and-asteroids where one exists).
  - *Stolen hulls.* A captured AI hull keeps its owner's `OwnerId` and is `Stolen`. Lawful stations refuse it a dock slot, a refit and NPC buyback (ship-design's 0.6 buyback never applies to it); stripping it yields parts at salvage condition, which ship-design's arbitrage tests already keep below retail. What lawless ports pay is for the factions/law doc; the interim is nothing. Without this, sniping a bridge watch and selling the hull would mint credits from the NPC market.
  - *Demotion and reload* move state, never copy it (section 1, "One owner of truth").
- **Player actions change schedules:**
  - *Destroyed hull:* `DestroyedUntilTick = tick + ReplaceTicks` (data, 2-6 h played) and the owner's shipyard station must hold the ship's bill of materials; without it the slot stays empty. The route runs short, the destination's stock falls and its prices rise.
  - *Delayed, damaged, robbed:* `DelayTicks`, the damage pool, a longer dock for repairs, a smaller delivery.
  - *Blockaded or captured station (D27):* routes to it are suspended by the owner's behaviour data while the incident level is high; a captured station's routes are regenerated for its new owner.
  - *Incidents:* an owner's standing with a player changes dock permission, prices and security response.
- **Off-screen conflict** is sparse, and since revision 2 it has two halves. *Which* encounters happen is still statistical: per route and per 10 min, a seeded draw against `lossRate(patrol level, pirate presence)` picks at most one hull to be attacked, by a named attacker row from the pirate presence on that route. Rates default low (a hull per route per ~20 h played) and are data. *How* each encounter ends is section 1's "Fights": One-shot from the two rows with nobody in the SOI, Stepped at L1 with a player in the SOI or a player-owned hull involved, Real when a player is party or witness. The outcomes are the same set at every mode: fled, `Raided` (cargo moved to the attacker's row, crew cleared, template damage brushes), surrendered (as `Raided`, with prisoners and less damage) or `Destroyed`. No AI fleet battles beyond these encounters are simulated off-screen in v1 (open question 3).
- **Density (D50).** The earlier proposal (~200 hulls per star system, 5-15 in sight at a major station) is superseded: a hub may show up to 200 hulls at once (docked, holding, on approach and in transit), and the caps allow it. Docked hulls are children of the station compound, holding and transit hulls are zero-cost schedule legs, and at most 24 are free-flying bodies in the bubble. Hull counts per route and per system are world data under the 4096 / 1024 L0 cap.
- **Hired crews are a sink, private routes are trade (D47).** Wages leave the owner's account per watch worked. A player's `RunRoute` hull buys at the origin's price and sells at the destination's, through the same `StationStock` debit and credit as a scheduled hauler, within the owner's buy and sell limits; it moves prices like any other trade and can be raided like any other hull.
- **Ransom and prizes mint nothing (D49).** An AI owner pays a ransom in kind from its station stock, with a hull valued at salvage (section 3); credits are never created for it. A prize hull is `Stolen` to NPC markets, as below. Prisoners handed to an `authority` desk pay whatever the law doc decides; the interim is nothing.
- **Persistence:** saved with the world: `TrafficRoute`s, `TrafficShips`, `TrafficStrays`, `DamagePool`, crew side pools (bodies, dose, `CrewDelta`, epoch origins, `Prisoners`), morale, schedule salts, standing orders and assignments, the next wage tick, hire pools, `StationStock`, dock bookings, the event heap, `AiShips` behaviour state for off-schedule ships (a pursuer is still pursuing after a restart), surrender states, engagements with their `Step` and mode, the `mercy` table, and pinned hulls as entities. Scans are not saved: a dwell ends with the session. Everything is keyed on `TickIndex`, so it all pauses while the server is empty (user decision) and nothing catches up on resume. On load every unpinned AI ship that is not within 20 km of a player and not in a Stepped or Real engagement starts at L0 from its summed-back row.

## 7. Networking, data, costs, files, tests, milestones

### Networking summary

| What | Wire |
| --- | --- |
| Traffic contacts (L0 **and** on-schedule L1) | **The wire speaks legs, never levels** (as it speaks frames, never bubbles). For every hull in an SOI a client has interest in, the client holds a **leg record**: `TrafficLeg {hull, leg, frame, tick, state, accel, duration}` (~56 B) or elements (~48 B), sent once per leg change from the same heap event that advances the schedule. The client extrapolates the closed form for its map, radar and distant dot. Whether the server holds that hull as a row or as an entity is invisible, so promotion, demotion and cap eviction change nothing a client can see. Other SOIs: 0.2 Hz radar contacts from `Locate` (one evaluation per hull per system, shared by all clients). Station boards (arrivals and departures) are queried on demand |
| Off-schedule L1 ships | frames' rails rules: elements once per change (~48 B); thrusting as `RailsBurn {entity, tick, state, accel, duration}` (~56 B) per thrust change, at most 1 Hz; "Same SOI" 1 Hz corrections |
| L2 ships | ordinary body snapshots (~20 B), interest by frame tree. AI ships are server-owned: no client predicts them (answers review.md's "who owns a ship" for AI) |
| Warp, docking, queues | the existing events (`WarpDepart`, `WarpBeacon`, `WarpArrival`, queue and catch-slot events). AI civilians always beacon |
| Crew | section 4. With the key (own and open grids): manifest on approach, small events, zero bytes for motion. Without it (private grids): 12 B walk segments for figures in sight only, and `ScanResult` / live-track segments while a `ScanCrew` dwell holds. C2 as characters, room-filtered on private grids |
| Hull drawing data (D50) | `TrafficHullInfo {hull, plan hash, HullSilhouette}` 36 B once per hull on interest; damage brushes with the L1 entity; the far-mesh blob (<= 140 KB by plan hash, baked by the server) for hulls that want H2 or H3; blueprints only for hulls that want H1 or better. No render stage is ever on the wire |
| Fights (D50) | members of a Real engagement are ordinary L2 snapshots; Stepped members are rails records plus their damage events; background pairs add `FightStep` 16 B per pair per 10 s; bodies beyond 8 km go at 2 Hz. A refused lock is `LockRefused {reason}` 4 B to the locker. The engagement object and its mode are never sent |
| Surrender and prisoners (D49) | `SurrenderState` 16 B on change. A ship's goes to everyone with interest in it (the transponder state is public in the bubble and to 20 km); a person's goes to key holders and to clients with that room in sight or scan, like any crew event (D48); `PrisonerMove` 16 B to holders of either grid's key or sight |
| Orders and wages (D47) | client to server: `ShipOrder` 32 B, `CrewOrder` 12 B, reliable, validated for `Command`; server to owner: `WageNotice` 12 B per crewed grid per watch |
| Ship AI intent | never sent. Clients see only what the ship does |

At the caps a player near a busy station sees ~24 L2 AI ships (24 x 30 Hz x 20 B ≈ 14 KB/s before delta compression, inside frames' 30 KB/s per-client budget only because most are slow or parked; AI ships docked are `Frozen` and send nothing). Measure in A2. Leg records: ~200 hulls in a system changing leg ~10 times an hour is under 1 record a second (~30 B/s per client), plus one burst of <= ~11 KB when a client first takes interest in a busy SOI, spread over a few seconds, and ~7 KB of `TrafficHullInfo` for the same 200 hulls.

**200 hulls on screen cost the wire almost nothing more (D50).** Of 200 drawn hulls at a hub at most 24 are free-flying L2 bodies sending snapshots; the docked ones are `Frozen`, and the holding and transit ones are leg records the client extrapolates. The 14 KB/s above is still the figure. A 200-hull battle is **not** quite the same. Besides the <= 24 bodies of the client's own bubble, up to 72 more in other bubbles within 20 km would arrive at frames' 10 Hz (~14 KB/s more), which puts AI bodies alone at ~29 KB/s before delta compression against a 30 KB/s client budget, with no room for players. So bodies beyond 8 km (under 12 px for a 100 m hull) go at 2 Hz with their velocity: 72 x 2 Hz x 20 B ≈ 3 KB/s. That is a **change to frames' interest table** ("Same frame within 20 km: 10 Hz" becomes 10 Hz to 8 km and 2 Hz beyond). Each background pair is 1.6 B/s of `FightStep`.

### Data structures (SoA, preallocated, zero alloc in the tick)

```csharp
namespace BrutalGame.Sim.Ai;

public readonly record struct OwnerId(uint Value);            // tagged: player or NPC owner (faction, company, station)
public enum ShipLevel : byte { Statistical, Rails, Simulated, Interior }
public enum TripLeg : byte { Docked, Undock, ClearZone, Spool, Warp, RealSpace, Approach, Hold, Dock, QueueGate, Cross, Tunnel, Adrift }
public struct CrewMask { public ulong Lo, Hi; }               // D51: 128 bits, indices 0..99; bits 100..127 are always zero. 16 B
public struct RoomMask { public ulong A, B, C, D; }           // 256 bits over ship-atmosphere's design rooms (<= 255). 32 B
public enum FightMode : byte { OneShot, Stepped, Real }
public enum SurrenderState : byte { Free, Surrendering, Surrendered, Captured, Released }

public struct TrafficRoute                                    // world data, ~40 B
{
    public EntityId Origin, Destination; public ushort ShipClass, CargoItem; public uint PeriodTicks, PhaseTicks;
    public ushort FirstHull; public byte HullCount, PatrolLevel; public OwnerId Owner; public ushort Behaviour;
}

public sealed class TrafficShips                              // SoA, index = hull slot, cap 4096; ~120 B a row summed over the fields below, ~492 KB at the cap (the first draft's row was ~108 B: +8 mask, +4 rows)
{
    public ushort[] Route; public uint[] CrewSeed; public CrewMask[] CrewAlive;
    public ushort[] PrisonerRow, EngagementRow;               // 0xFFFF = none
    public ushort[] FuelQ;                                    // 4 per row, q16, rounded down: propellant, warp fuel, EM, AM. Zero while L2+
    public uint[] CargoQty;                                   // exact units. Zero while L2+ (the holds own it)
    public ushort[] SystemsQ; public ushort[] DamageRow;      // 0xFFFF = undamaged
    public ushort[] StrayRow, CrewDeltaRow;                   // 0xFFFF = none
    public long[] TripStartTick; public uint[] DelayTicks; public long[] DestroyedUntilTick;
    public uint[] LegEnds;                                    // 12 per row: offsets from TripStartTick
    public ShipLevel[] Level; public byte[] Flags;            // Pinned, Stolen, Prize, PlayerRoute
    public int[] Entity;                                      // -1 at L0
}
public struct TrafficStray { public FrameId Frame; public KeplerOrbit Rails; public long SinceTick, RecoverAtTick; }   // pool 256
public struct CrewDelta    { public byte Index, HealthQ, KitMask, Flags; }                                             // 32 per row, 4 B; Flags: Surrendered, Parole, HostileToGroup (restraint is a Prisoner row, never a flag)
public static class TrafficSchedule
{
    public static TripLeg Locate(TrafficShips s, int hull, long tick, in CelestialSystem rails, out FrameId frame, out double3 p, out double3 v);
    public static void PlanTrip(TrafficShips s, int hull, long departTick, in ShipClassStats c);   // once per trip, ~5 us
}

public sealed class AiShips                                   // SoA, index = ai slot; sized 4352 (every hull slot + 256 non-traffic AI, ~0.7 MB) so the soft L1 and L2 caps can be exceeded without allocating
{
    public int[] Entity; public int[] Hull;                   // hull slot, or -1 for non-traffic AI (security, pirates off route)
    public ushort[] Behaviour; public byte[] Option, ActionState, Skill; public OwnerId[] Owner;
    public EntityId[] Goal; public double3[] GoalLocal; public FrameId[] GoalFrame;
    public long[] NextDecideTick, ReactAtTick, CombatUntilTick, HelmLostTick; public byte[] Helm;   // Held, DeadMan, Relief, Derelict
    public ushort[] Engagement;                               // 0xFFFF = none
    public SurrenderState[] Surrender; public OwnerId[] Captor; public long[] SurrenderTick;
    public ushort[] OrderRow;                                 // player-owned hulls only (D47): index into ShipOrders, 0xFFFF = none
    public FlightCommand[] Command;                           // L2 only
}
public struct Hazard { public byte Kind, Severity; public float3 A, B; public float Radius; public long From, To; }   // capsule in bubble space, 44 B
public struct ShipOrder { public byte Verb, Roe, Flags; public EntityId Target; public FrameId Frame; public double3 Local; public ushort Item; public uint BuyMax, SellMin; public long Tick; }   // pool 256; Flags: StandFast

public struct Engagement                                      // pool 64 dedicated / 16 listen, ~72 B
{
    public uint Id; public FrameId Frame; public double3 Centre; public float Radius;
    public long StartTick, LastHostileTick, WitnessLostTick; public uint Step;
    public FightMode Mode; public byte Count, Flags; public ushort FirstMember;   // Flags: HasParty, HasWitness, PlayerOwnedMember
}
public struct EngagementMember { public int Ai; public ushort Hull, Target; public byte Side, State, Flags; }   // <= 24 each, 12 B; State: Fighting, Fleeing, Surrendered, Destroyed; Flags: Promoted, Background, FightsPlayer
public static class AbstractFight
{
    public static void Step(ref Engagement e, Span<EngagementMember> m, TrafficShips s, FightModel model, uint worldSeed);   // Stepped: every 600 ticks
    public static void ResolveEncounter(TrafficShips s, int hullA, int hullB, FightModel model, uint worldSeed, uint engagementId, long startTick);   // One-shot: Step() looped to the end with the same draws; results applied at their step ticks
    public static uint Draw(uint worldSeed, uint engagementId, uint step, int slot);                                                             // slot = hull slot or AI slot, never the member index
}

public sealed class CrewManifests                             // SoA, index = crewed grid slot, cap 1024
{
    public int[] GridEntity; public ushort[] Template; public uint[] Seed, ScheduleSalt; public OwnerId[] Owner;
    public CrewMask[] Alive, Full, Suited; public byte[] Condition, Competence, Morale, Privacy;   // Privacy: Open, Private (D48)
    public RoomMask[] UnsafeRooms; public long[] EpochTick;   // the current epoch: condition + unsafe design rooms, from EpochTick
    public long[] NextWageTick;                               // hired crews only (D47); long.MaxValue otherwise
    public ushort[] OverrideRow, BodiesRow, DoseRow, DeltaRow, EpochOriginRow, CrowdRow, PrisonerRow;   // lazy side pools, 0xFFFF = none
}
public struct CrewEpochOrigin { public ushort Node, NextNode, Offset; }   // 100 per row (600 B), kept only while an epoch walk is unfinished
public struct CrowdState      { public byte Count, PrevCount; public uint FromSlot; public ulong Dead0, Dead1, Dead2; }   // station grids only
public struct CrewOverride { public byte Index, Flags; public ushort FromNode, FromOffset, ToPost; public long StartTick; }   // 32 per row; Flags: Standing (a hired crew's assignment, never expires)
public struct CrewBody     { public byte Index, Cause; public ushort Node, Offset; public long Tick; }                         // 32 per row
public struct Prisoner                                        // D49: 16 per row, 32 B; moved from the origin's Alive mask, never copied
{
    public uint Origin; public uint SeedSalt; public OwnerId OriginOwner, Custodian;
    public byte OriginIndex, HealthQ, KitMask, Flags; public ushort Post; public long SinceTick;
}
public struct CrewSightSeg { public byte Index, Anim; public ushort FromNode, FromOffset, ToNode; public uint StartTick; }   // D48: 12 B, the only crew motion a keyless client is sent
public struct CrewScan     { public EntityId Scanner, Target; public OwnerId Group; public long StartTick, LastTick; public float RNom; public byte State; }   // pool 64; State: Dwell, Live, Broken

public sealed class CrewNav                                   // per plan hash, shared; sent by the server with the blueprint
{
    public NativeList<NavNode> Nodes;                         // <= 256: int16 x3 at 1/32 m, RoomId, PostKind, Facing, Anim
    public NativeList<byte> NextHop; public NativeList<ushort> Dist;   // N x N
    public NativeList<ViewPortal> Portals;                    // <= 64
    public NativeList<byte> RoomDeck, RoomScanShield;         // per design room (<= 255): deck id; scan transmission 0..255 (D48)
    public NativeList<byte> RoomHop;                          // R x R, people-passable edges between rooms, clamped to 3 (D51)
}
public struct CrewPose { public ushort Node, NextNode, Offset; public byte Anim, Flags; }       // fixed point; the only thing peers must agree on
public static class CrewSchedule
{
    public static CrewPose Evaluate(in CrewManifestView m, CrewNav nav, CrewTemplate t, int index, long tick);
    public static int EvaluateRoom(in CrewManifestView m, CrewNav nav, CrewTemplate t, int roomId, long tick, Span<byte> indices);
}

public sealed class CrewAgents                                // C2, SoA, cap 304 (48 active + 256 parked)
{
    public int[] Entity, Grid; public byte[] Index; public float[] Health, DoseGy; public byte[] Task, Parked, Priority, KitMask; public ushort[] GoalPost;
    public SurrenderState[] Surrender; public byte[] Ring;    // Ring: G0 or G1 at the last 5 Hz check
}
public static class CrewRings                                 // D51: the ladder inside one grid
{
    public static int Ring(CrewNav nav, int playerRoom, int room, int navDistQ);   // 0..3 from RoomHop, RoomDeck and the 20 m / 60 m clips
}
public static class CrewScanner                               // D48
{
    public static float Quality(float rNom, double range, float transmittance, byte roomShield, bool baffled);   // q = min((rNom/R)^2, gainMax) * T * S * B
}

public struct DockSlot { public EntityId Holder; public OwnerId Group; public long From, Until; public byte SizeClass, Kind, Flags; public ushort Lane; }   // ring of 32 per station; Flags: Ai, Confirmed
public struct AmbientInstance { public float3 RelOrigin; public float3 Velocity; public ushort Yaw; public byte Anim, AnimPhase; public uint Appearance; public byte RoomLight, Portal; }   // 32 B, client

// D50, client only (src/Engine): the hull stage ladder
public enum HullStage : byte { Interior, Shell, WholeMesh, Proxy, Impostor, Point }                 // H0..H5
public struct HullSilhouette { public ushort HalfX, HalfY, HalfZ; public ushort ColourA, ColourB; public byte Emissive, Shape; public float Radius, Flux; public uint ShapeSeed; }   // 24 B, sent with the entity or traffic contact
public struct TrafficHullInfo { public uint Hull; public ulong PlanHash; public HullSilhouette Silhouette; }   // 36 B on the wire, once per hull on interest
public struct HullRecord                                      // 64 B per drawn hull, beside the shared 32 B per-draw instance record (review.md C9)
{
    public float3 RelOrigin; public float Radius; public float4 Quat; public uint PlanSlot, Tint;
    public byte LitFraction, ThrustQ, StrobePhase, Scorch; public HullStage Stage, PrevStage; public ushort FadeQ, ImpostorSlot, RoomLightOffset; public uint Pad0, Pad1, Pad2;
}
public static class HullStageSelector { public static void Select(ReadOnlySpan<HullContact> contacts, in CameraView view, Span<HullRecord> records, Span<int> countsPerStage); }   // <= 1024 contacts, ~50 ns each, on a job
```

### Data definitions

```jsonc
// data/ai/behaviours/hauler.json
{ "id": "hauler", "lawful": true, "skill": "Trained",
  "options": [
    { "action": "FollowTrip", "base": 1.0 },
    { "action": "Evade",      "base": 0.0, "inputs": { "hazardAhead": 2.0 } },
    { "action": "Flee",       "base": 0.0, "inputs": { "threat": 1.5, "hullLost": 1.0, "crewLost": 1.0 }, "min": 0.8 },
    { "action": "Yield",      "base": 0.0, "inputs": { "lawfulStop": 3.0 } },
    { "action": "Surrender",  "base": 0.0, "inputs": { "cannotFlee": 1.5, "disabledDrive": 1.5, "threatRatio": 1.2, "crewLost": 1.0, "lowMorale": 0.5 }, "min": 0.8 },
    { "action": "Distress",   "base": 0.0, "inputs": { "underAttack": 2.0 }, "cooldownS": 30 } ],
  "fleeHull": 0.6, "fleeCrew": 0.5, "returnFireOnly": true, "neverSurrender": false, "mercyMin": 64 }

// data/ai/behaviours/player_crew.json  (D47: a player's hull flown by hired crew; the owner's ShipOrder is the forced option)
{ "id": "player_crew", "lawful": true, "skill": "fromCrew",
  "options": [
    { "action": "ObeyOrder", "base": 2.0 },
    { "action": "Evade",     "base": 0.0, "inputs": { "hazardAhead": 2.0 } },
    { "action": "Flee",      "base": 0.0, "inputs": { "threat": 1.5, "hullLost": 1.0 }, "min": 0.9, "unlessStandFast": true },
    { "action": "Surrender", "base": 0.0, "inputs": { "cannotFlee": 1.5, "crewLost": 1.0, "lowMorale": 1.0 }, "min": 0.9, "unlessStandFast": true } ] }

// data/ai/fightmodel.json  (D50: generated by --fight-calibrate from headless L2 duels, never hand-edited)
{ "stepTicks": 600, "pairs": [ { "attacker": "pirate_cutter", "target": "hauler_5kt", "band": "mid", "skill": "Trained", "hullPerStep": 0.031, "spread": 0.4, "systemsChance": 0.08, "crewPerStep": 0.3 } ] }

// data/crew/hiring.json  (D47)
{ "signOnWatches": 12, "moraleLow": 64, "maxAiHullsPerPlayer": 4, "maxAiHullsPerGroup": 16, "offlineTrading": false,
  "wagePerWatch": { "pilot": [40, 60, 90, 140], "engineer": [35, 50, 80, 120], "hand": [15, 20, 30, 45] } }

// data/crew/surrender.json  (D49)
{ "surrenderTicks": 300, "surrenderCooldownTicks": 3600, "rearmTicks": 600, "custodyTicks": 7200, "custodyClosingM": 5000, "paroleTicks": 36000, "noQuarterTicks": 216000, "giveUpTicks": 3600,
  "surrenderHealth": 0.3, "ransomMax": 0.5, "ransomHullValue": "salvage", "ransomPaidIn": "stock", "ransomTicks": 432000, "aggravation": 3.0 }

// data/sensors/crewscan.json  (D48, interim until the sensor doc)
{ "dwellTicks": 240, "staleTicks": 3600, "baffle": 0.25, "gainMax": 16, "tiers": [1, 2, 4],
  "maxDwellsPerPlayer": 2, "maxDwellsPerGroup": 8, "reservedForAi": 16, "alertCooldownTicks": 1800,
  "scanners": { "S": { "rNomM": 2000, "kW": 20 }, "M": { "rNomM": 5000, "kW": 50 }, "L": { "rNomM": 10000, "kW": 150 }, "hand": { "rNomM": 30, "kW": 2 } } }

// data/render/hull-lod.json  (D50)
{ "caps": { "interior": 4, "shell": 12, "wholeMesh": 64, "proxy": 120, "impostor": 256, "point": 1024 },
  "pxPerMetre": { "interior": 3.0, "shell": 0.47 }, "minPx": { "wholeMesh": 12, "impostor": 3 },
  "interiorPoolMB": 60, "interiorTrianglesPerHull": 400000, "componentInstancesPerInteriorHull": 512, "componentMeshPoolMB": 16, "drawRecords": 4096,
  "hysteresis": 0.2, "dwellS": 0.5, "fadeS": 0.25, "hullLightsPerFrame": 64, "impostorSlots": 128 }

// data/ai/fights.json  (D50, revision 2 review: the budgets that keep a far fight from loading the server)
{ "remoteBubblesWithParty": [8, 2], "remoteBubblesNoParty": [4, 1], "remoteBodies": [32, 8], "remoteBubblesPerPlayerShip": 1,
  "provisionalPerGroup": 4, "provisionalEndTicks": 600, "engagementHoldMaxS": 120, "steppedPerTick": 8, "aiHelmPlayerHullsL2": [64, 8] }

// data/crew/roles.json
{ "id": "pilot",    "canFly": true,  "priority": 0, "posts": ["helm"],                 "offWatch": ["mess", "wander"], "sleep": ["bunk"] }
{ "id": "engineer", "canFly": true,  "priority": 2, "posts": ["eng_panel", "drive"],   "repairPerMin": 1.0 }
{ "id": "hand",     "canFly": false, "priority": 5, "posts": ["cargo", "wander"] }

// data/crew/templates/hauler_5kt.json  (referenced by data/traffic/shipclasses.json: plan hash, stats, behaviour, crew template)
{ "id": "hauler_5kt", "watchHours": 2, "roles": [ ["pilot", 3], ["officer", 2], ["engineer", 3], ["hand", 6] ],
  "alertPosts": { "pilot": "helm", "engineer": "drive", "hand": "damage_control" } }

// data/stations/services.json
{ "id": "vendor.general", "role": "vendor", "kioskMarkup": 1.15, "stock": "StationStock" }
```

`data/traffic/routes/*.json` holds authored routes; generated ones live in the save. `data/ai/skills.json` holds the four tiers. Loss tables and `ReplaceTicks` are in `data/traffic/rules.json`.

### Per-tick server cost at the caps (estimates)

| Item | Cost | Notes |
| --- | --- | --- |
| L0, 4096 hulls | ~1 us average | heap pops only |
| L1, 1024 ships | 0 on schedule; worst case ~30 us integrators + ~18 us decide; frames' membership pass ~17 us a tick for 1024 entities | worst case = every ship off schedule and thrusting; typical is a few dozen |
| L2, 96 ships: decide, steer, react | ~8 us | staggered |
| L2: flight computer and force pass | ~50 us | same as 96 piloted ships |
| L2: Jolt | ~0.15 ms CPU on workers | 96 awake compounds |
| Contact grids and hazards, 32 bubbles at 2 Hz | ~10 us average | |
| Level checks | inside frames' 10 Hz membership pass | +~50 ns per AI ship |
| Station controllers, 64 stations at 2 Hz | ~6 us average | 0 when nobody is in the zone |
| Crew events (schedule evaluation on damage) | <= 10 us per damaged crewed grid per tick (100 crew; was ~6 at 64) | batched: one evaluation per grid per tick however many brushes |
| C2 promotion checks (ring rule), 5 Hz | ~3 us per check with the post-room prefilter, <= 10 us without; 64 grids with players ≈ 16 us a tick average | only grids with a player aboard or within 100 m |
| Dose on crewed grids, 1 Hz | <= 10 us per dosed grid per second | only grids with a dose |
| C2 agents, 48 active + 256 parked | ~0.25 ms typical, ~0.85 ms worst, CPU on jobs | the active cap did not move, so neither did the cost; parked agents cost 0 per tick |
| Engagements: membership and witness checks, 2 Hz (D50) | ~2 us each; 64 engagements ≈ 4 us a tick average | 0 with no fights |
| Stepped fights (D50) | ~2 us per member per 600 ticks; <= 5 us a tick average at the pool cap | One-shot encounters ~2 us each, on the route draw |
| Remote combat bubbles (D50) | <= 12 extra scene steps (4 with no party, 8 for players' targets at range), ~50 us of job CPU each: <= 0.6 ms; the bodies are inside the 96 | only while a fight is more than 4 km from every player bubble |
| Player hulls under AI helm at L2 (D47) | <= 64 / 8 bodies outside the 96: ~0.1 ms Jolt on workers, ~35 us of flight computer | player ships in frames' body budget; the cap is this doc's |
| Cross-bubble shots (D50) | ~1 us per projectile hand-over; one ray cast per beam in the target bubble | the cast itself is the combat doc's cost |
| Crew sight feed for private grids, 2 Hz (D48) | ~5-12 us per watched grid plus ~2 us per watcher; ≈ 40 us a tick on a job at 64 watched grids; <= 0.35 ms at the bound (64 clients x 16 private grids each) | 0 for own and open grids |
| `ScanCrew` (D48) | <= 10 us on the first result; live tracks ~1 us a tick at 64 scans | event-driven after the first result |
| Surrender checks (D49) | inside decide (ships) and the C2 task update (people); a `mercy` read is O(1) | no new pass |
| Prisoners, wages, orders, morale (D47, D49) | 0 per tick: manifest rows and scheduled events (one wage event per hired grid per watch, ~0.2 us) | |
| **Total** | **~0.2 ms main-thread-equivalent plus <= 2.1 ms of job CPU with every worst case at once** (Jolt 0.15 + hired hulls 0.1 + C2 0.85 + remote bubbles 0.6 + sight 0.35; ~0.35 ms wall on 6 workers). The draft's 1.3 ms had no remote bubbles for players' targets, no hired hulls and the sight feed at its typical figure | listen server at its caps: about a quarter of that |

**Against 64 players (frames' budget: ~3 ms Jolt CPU and ~2 ms of player `CharacterVirtual` CPU a tick, ~0.6 ms wall on 6 workers):** the AI adds 96 bodies to ~2000 (about 5 %; 160 and 8 % with hired hulls at their cap), and the C2 worst case adds 48 walking characters to 64 (0.85 ms on 2 ms, ~0.15 ms wall). That fits, but the two worst cases land together in exactly the scene that is already the heaviest (a boarding fight at a busy station), so A4's exit check measures that scene, not an empty one. 64 players spread over ~16 bubbles get about 6 free-flying AI ships each at the server cap; if that reads as empty, the lever is the 96 (a Jolt cost, measured in A2), not the thinking. Since revision 2 it reads as full anyway: the other hulls round a player are drawn from rails, legs and docked children (up to 200), and only the bodies are rationed.

**Client, per frame (D50, D51):** hulls <= 2.0 ms GPU, <= 0.05 ms main thread, <= 0.25 ms on a job and <= 130 MB for 200 hulls (section 1's budget table); ambient crew unchanged at <= 0.15 ms GPU and <= 0.02 ms main thread, with the job at ~95 us a frame worst case (16 full station grids).

`ProfilerPanel` rows: Traffic (L0 events), AI (decide / steer, counts per level), Fights (engagements per mode, promoted and background members, steps), Crew (C2 active / parked, schedule evaluations, sight-feed us and bytes, scans, prisoners), and client-side Ambient (figures near / mid, job us, GPU ms) and Hulls (count per stage, triangles, draw records, GPU ms, MB, bakes and impostor bakes a frame).

### Changes to other docs

- **frames-and-zones.md:** "Who gets a Jolt body" is unchanged; add that C0 and C1 crew are not occupants and never create a grid bubble or anchor physics, and the AI caps and the 4-promotions-a-tick budget. Tick order: step 2c pops traffic events; step 4 runs `ShipAi` as input; step 9 adds crew promotion checks at 5 Hz for grids with a player within 100 m. Interest table: add "Crewed grid within 1 km: manifest and events only" and "Traffic in the same SOI: leg records, once per leg". L0 ships are not entities and have no `Placement`; no new `PlacementMode`. "Ships with no bubble": a thrusting rails ship **on a schedule leg** is evaluated from the leg's closed form (a guided leg), and the Verlet integrator is for off-schedule thrust only. "Who gets a Jolt body": a player's weapon or tractor lock or tracked projectile pulls the target in at any range up to 20 km (into the locker's bubble inside the hard radius, into its own bubble beyond); the AI caps hold arrivals outside 3 km and never leave anything intangible inside 2 km. **Revision 2:** a promoted member of a Real engagement is a physics anchor (a combat anchor; <= 4 / 1 bubbles without a player); a projectile leaving a bubble is a rails entity and a beam is a frame-space ray cast in the target's bubble, so two bubbles can fight across more than 4 km without either growing; `EntityRange` 20 km replaces 200 km as the L0 > L1 distance. **Revision 2 review:** the combat anchor is restricted (members, their shots and wreck pieces only; free-fall frames only); remote bubbles are budgeted (4 / 1 with no party, 8 / 2 for players' targets, one per player ship, 32 / 8 remote bodies) and each takes a scene from the pool; "Same frame within 20 km: 10 Hz" becomes 10 Hz to 8 km and 2 Hz beyond, or a 200-hull battle alone fills a client's 30 KB/s. Interest table: "Crewed grid within 1 km" becomes "key holders: manifest and events; others: sight segments only", and character entities on a private grid are room-filtered.
- **warp.md:** no rule changes. AI warps go through the same plan validation, beacon and exclusion rules at every level (an L0 warp's commit is a scheduled event); the "no physics anchor: rails" arrival is this doc's L1. AI civilians never run silent. A scheduled convoy is a fleet warp by construction (same legs, formation offsets), within the 16-ship limit.
- **wormholes.md:** no rule changes; AI are ordinary requesters and `TunnelCruise` users. L0 gate crossings are schedule legs that still occupy `MouthQueue` slots, count against `TunnelCap` and wait for direction windows; scheduled traffic is limited to `aiQueueShare` of a ring; a collapse or a closed window re-plans the affected rows. Player interest in a `Mouth` or `Tunnel` frame promotes the L0 hulls whose leg is in it.
- **ship-hulls.md:** `Glazing` material flag with a small-arms rating; `ViewPortal`s from the bake; glazing kept at every LOD with interior mapping from LOD 2; `CrewNav` compiled beside rooms and sent by the server in the `CrewPlan` blob; room ids are ship-atmosphere's design rooms (no relabelling, so no mapping); a bridge-glazing Warning in validation. **Revision 2 (D50):** the LOD section gains the stage ladder H0-H5 with its count caps (this settles review.md P6: LOD 0-1 for 4 hulls, LOD 2-3 for 12), the whole-ship bake at 2 m and 8 m cells cached by plan hash, the octahedral impostor and its baker, `HullSilhouette`, and the rule that every hull draw goes through the shared instance buffer and one indirect-count draw (review.md P4, C8, C9). "Beyond ~30 km: impostor or lit point" becomes "12 px to 3 px: impostor; under 3 px: point". **Revision 2 review:** the L0 budget (400k triangles, 10-15 MB) is restated as a per-hull budget met by per-brick LOD, whatever the hull's size, inside a 60 MB pool; the whole-ship bake's cell doubles until the mesh fits 8k triangles ("2-4 m cells" holds only up to ~200 m); the server bakes and serves the far-mesh blob for player plans; component meshes need a budget from ship-design (16 MB assumed here). **(D48):** `scanOpacity` per material and a per-room `ScanShield` byte from the bake; **(D51):** a `Deck` label per room and the `RoomHop` table.
- **ship-design-and-construction.md:** `Post` data on the seat, console, bunk and service families; wander nodes from the Standard harness pass; new families `fn.autonomy` and `fn.service_point`; a crew template reference on catalog ship plans; Warnings for "helm post unreachable from berths" and "pod seats < crew". **Revision 2:** a crew page in the designer for player plans (roles, counts, watches, alert and muster posts: D47); post kind `Brig`; families `fn.sensor.crewscan` and `fn.scan_baffle`; service kinds `crew_office` and `authority`; the crew cap of 100 per grid in plan limits; a Warning for more than 24 posts in a room and its next-door rooms within 20 m; `crewPrivacy` on ship classes and station decks.
- **ship-atmosphere.md:** rooms are shared as they stand (design rooms, <= 255). `unsafeRooms` is derived from `ShipAir` and the dose pass. Prisoners and hired crew count in the life-support head count; the life-support crew rating Warning becomes a refusal to hire past it. The `Surrendered` condition changes no air rule. A prisoner's death by air in custody uses that doc's edge attribution first, then the custodian. No change to its decisions D38-D42.
- **exotic-matter.md:** `ShipLedger` and `PowerNet` run for L2+ only; L0/L1 use per-leg closed-form debits with the same formulas. Life support debits by alive crew count.
- **rings-and-asteroids.md:** `IncidentRecord` ids and the tracing fields that feed them become `OwnerId`; AI miners carry D33 liability; security response hooks into incidents; harming crew or crowd is a `Hostile` record. **Revision 2:** `IncidentRecord` kinds `Perfidy`, `KilledSurrendered` and `PrisonerHarm`, each with an aggravation flag; `RingObscuration.Transmittance(.., Thermal)` gets its first consumer, `ScanCrew`, so ring dust and shatter `DustCloud`s hide crews from scanners as that doc proposed for sensors generally.
- **A future combat doc (requirements, not changes):** weapons, damage and fire control. This doc now fixes the frame side: both ends of a shot have bodies, in their own bubbles if more than 4 km apart, and shots cross in frame coordinates. Required of it: weapon and lock range <= 20 km (`EntityRange`), a lock limit per ship, and weapon data the `--fight-calibrate` tool can run headless. An L1 ship still can neither be hit nor fire.
- **A future sensor doc:** owns detection ranges, jamming and decoys. `ScanCrew`'s numbers (`data/sensors/crewscan.json`) are interim and move there; its privacy rule (key, sight or scan, nothing else) stays here.
- **A future factions/law doc:** consumes `Stolen` and `Prize` hulls, crew-harm, surrender and custody records and the `mercy` table, and decides what lawless ports, ransoms, bounties and prize claims pay, and whether scanning in a protected zone is an offence.
- **warp.md:** a surrendered ship cannot spool; an AI-crewed player hull plots and beacons like any civilian unless its owner's order says otherwise (the owner carries the liability). The offline-crew rule is unchanged.
- **tractor-beams.md:** AI sets the consent flag only through `DockWithConsent` and tow requests, and treats any other lock as hostile.
- **review.md:** resolves "who owns a ship" for AI ships (the server) and "rails ships with occupants" for AI crew (they never anchor; `GridInertial` applies). **Revision 2:** P6 gets its count caps and a byte pool (4 hulls at LOD 0-1 in 60 MB, <= 130 MB for 200 hulls); P4's indirect-count path is assumed by the 200-hull budget (~3.6k draw records, <= 6 draw calls) and must exist before `--fleet-bench` means anything; "who owns a ship" also covers a player's hull under AI helm (server-owned until a player sits).
- **decisions.md:** D47-D51 are recorded there already; this revision is their design.

### Files

- `src/Sim/Ai/` (no Vulkan): `TrafficRoutes.cs`, `TrafficShips.cs`, `TrafficSchedule.cs`, `TrafficEvents.cs`, `ShipLevels.cs` (promotion, demotion, reconstruction), `AiShips.cs`, `ShipAi.cs` (decide, steer, react), `Actions/*.cs`, `Behaviours.cs` (data load, utility), `ContactGrid.cs`, `Hazards.cs`, `Helm.cs` (authority, succession), `FlightCommand.cs`. Revision 2: `Engagements.cs` (membership, party and witness, modes, promotion order, background pairs), `AbstractFight.cs` (`Step`, `ResolveEncounter`), `FightModel.cs`, `CrossBubbleFire.cs` (projectile hand-over, frame-space rays), `ShipOrders.cs`, `ShipSurrender.cs`.
- `src/Sim/Crew/` (no Vulkan): `CrewManifests.cs`, `CrewTemplates.cs`, `CrewNav.cs`, `CrewNavCompiler.cs`, `CrewSchedule.cs`, `CrewCasualties.cs`, `CrewAgents.cs`, `CrewPromotion.cs`. Revision 2: `CrewMask.cs`, `CrewRings.cs` (decks, hops, rings, the post-room prefilter), `CrewKeys.cs` and `CrewSight.cs` (D48 privacy), `CrewScanner.cs`, `Surrender.cs` (the state machine and the `mercy` table), `Prisoners.cs`, `PlayerCrew.cs` (hire, dismiss, wages, morale, orders, the logoff rule).
- `src/Sim/Stations/`: `StationController.cs`, `DockSlots.cs`, `ServicePoints.cs`, `StationStock.cs`, `SecurityResponse.cs`. Revision 2: `HirePool.cs`, `Authority.cs`.
- `src/Ships/`: `ViewPortals.cs` and the glazing flag in the bake. Revision 2: `ScanShield.cs` (per-room transmission from the bake), `WholeShipBake.cs` (2 m and 8 m cells, no Vulkan, so the server and the calibrator can use the same bounds).
- `src/Engine/` (client): `AmbientCrewRenderer.cs`, `AmbientCrewJob.cs`; `shaders/crew_figure.*`, `shaders/crew_card.*`, interior mapping in the glazing shader. Revision 2: `HullStageSelector.cs`, `HullFarMeshes.cs` (H2 / H3 cache by plan hash, the damaged-mesh pool), `HullImpostorBaker.cs`, `HullInstanceWriter.cs` (the shared instance buffer and hull records), `HullLights.cs`; `shaders/hull_impostor.*`, `shaders/hull_point.*`, `shaders/hull_plume.*`; `ScanGhostRenderer.cs` (scan results drawn through the hull).
- `src/Debug/`: `AiPanel.cs` (levels, caps, per-ship behaviour and helm state, engagements), `CrewPanel.cs` (rings, keys, sight feeds, scans, prisoners), `HullLodPanel.cs`.
- Bench and tool paths: `--fleet-bench` (200 hulls of ~30 plans plus 20 unique round a station: stage counts, GPU ms, MB), `--fight-calibrate` (headless L2 duels that write `data/ai/fightmodel.json`).
- `data/ai/`, `data/crew/`, `data/traffic/`, `data/stations/`, `data/sensors/crewscan.json`, `data/render/hull-lod.json`.
- `tests/Sim.Tests/`: below.

### Tests

- **Schedule:** peers agree bit-for-bit on 10,000 random evaluations (two builds, two machines); no two crew are assigned the same post in the same slot while posts suffice; a casualty moves nobody else; pose is continuous across slot, watch and epoch changes, including two epochs 30 s apart with walks unfinished (no step over the running speed per tick); an override ends at the scheduled pose; a crowd count change adds and removes figures only at `Entrance` nodes; a killed crowd member does not reappear before `CrowdReplaceTicks`.
- **Level crossings:** the two continuity checks; cargo, damage brushes, `SystemsQ` and `CrewAlive` are identical and fuel is equal or lower by at most one q16 step after any L0 > L2 > L0 round trip with no events; no field increases outside a `Docked` leg; 1,000 forced cycles across the 2-3 km band never raise any quantity.
- **No duplication:** at every tick, `row quantity + materialised quantity` for a hull equals what it was before the crossing; a crate removed from the hold (in hands, in a tractor, adrift) is not summed back; a save taken while a ship is L2, then loaded, holds the same totals; a foreign item in a hold pins the hull.
- **Caps and tangibility:** with a bubble at 24 and the server at 96, an AI ship already within 2 km of an arriving player still gets a body; a pursuer displaces a lawful ship from a full bubble; no entity within 2 km of any player is ever on rails; no hull within 20 km of any player is ever L0 (was 200 km).
- **Pursuit:** a pursuer left behind at 5 km is still pursuing at L1, is promoted again on approach, survives a save and load, and gives up only by its behaviour data.
- **C2 priority:** with 48 active agents held by idle proximity in one station, a hostile boarding elsewhere still gets its defenders; a wounded, disarmed or restrained crew member demoted and promoted again is still wounded, disarmed or restrained.
- **Window fire:** a round that does not defeat the glazing kills nobody at C0 or C2; one that does gives the same casualty at both levels and a `Hostile` record.
- **Raiding:** over a simulated 100 h, the goods taken from a route never exceed what its origin stock was debited; a `Stolen` hull is refused buyback at a lawful station.
- **Trip plan:** leg durations at L0 match an L2 flight of the same class and route within 2 %.
- **Helm:** cutting the helm Data run, killing the watch, and displacing the pilot each give dead-man hold on the same tick; relief time equals path length / 4 m/s; no qualified crew gives `Derelict`.
- **Casualties:** a breach never kills outside the breached room at C0; a C0 outcome and a C2 replay of the same breach agree on who was in the room; bodies persist through demotion and save / load.
- **Promotion:** the C2 spawn pose equals the schedule pose at the promotion tick; a demoted agent's ambient figure starts within 1/32 m of the agent.
- **Economy:** stock is conserved across a trip (debit at undock equals credit at dock minus losses); a destroyed hull stays out for `ReplaceTicks` and needs its bill of materials; nothing advances while the clock is paused.
- **Caps and allocation:** at every cap the tick allocates nothing (allocation counter test); the 25th AI ship holds outside a full bubble.
- **Liability:** an AI miner's debris writes a record with its owner as `Liable`; a player hit on an AI hull writes one with the AI owner as victim.

Revision 2:

- **Masks at 100 (D51):** index 99 survives every round trip (row, manifest, wire, save); bits 100-127 are zero after every operation; a 100-crew grid's schedule, casualty, dose, epoch-origin and promotion paths agree with a brute-force reference; a save written with one-word masks is refused or migrated, never misread.
- **Rings (D51):** a crew member behind a bulkhead 3 m from the player but 3 hops away is not promoted; one next door is; a room is promoted before a walking player enters it and demoted 20 s after it leaves G1; with 100 crew and four boarders the grid never exceeds 24 active and the server never exceeds 48; defenders past the cap arrive as slots free; the post-room prefilter returns the same visible set as full evaluation over 10,000 random ticks.
- **Privacy (D48):** the leak test (two seeds, byte-identical keyless streams); a keyless client's figures match a keyed client's `(node, offset)` while in view; a figure entering view arrives with a segment stamped at or before its entry tick; an open grid stops being computable by a group after that group's first hostile act (the old key gives wrong posts from the `Alert` epoch on); C2 entities of a private grid are not sent for rooms out of sight and scan. **Review additions:** the leak test also covers a private hull drawn at H2-H5 (its `LitFraction` stream is byte-identical under two crew seeds) and every manifest event (`CrewPromote`, `CrewDemote`, `CrewOverride`, `CrewEpoch`, `CrewCasualty`, a person's `SurrenderState`): none reaches a keyless client; a client reporting a wider field of view, a higher resolution or a different camera is sent nothing more.
- **Scan (D48):** `q` never rises with range, dust optical depth, wall opacity or a baffle, and never exceeds `gainMax x T x S x B` at any range; a third dwell by one player and a ninth by one group are refused; a broken dwell restarts from zero; a target raises one `ScanAlert` per scanner per 30 s; the tier boundaries return count, node positions and exact segments as specified and nothing above the tier; a live track stops on the tick the cone, range or power is lost; a snapshot ages and is never updated after the break; a scan of an L1 target returns the same positions as boarding it would at that tick; the target gets `ScanAlert`; a scouting AI reads no more than its `q` allows.
- **Surrender (D49):** every transition of the state table, for a person and for a ship; a shot fired before the `Surrendered` tick writes an ordinary record and one fired after writes `KilledSurrendered`; breaking an obligation writes `Perfidy` and ends protection on that tick; no custody within 120 s releases; a surrendered ship cannot spool, and stays an engagement member; AI never fakes; `mercy` below `mercyMin` stops AI surrendering to that owner; the same engagement ends in the same surrender in Stepped and Real modes within the calibration tolerance. **Review additions:** an owner group that has not fought the surrenderer (or is its own or its ally) cannot accept, take custody or release; a release by one captor leaves the ship surrendered to another within 20 km; the 120 s clock does not run while a captor is closing inside 5 km; a cancelled offer cannot be repeated inside the cooldown and leaves the weapons safed for 10 s; AI attackers keep firing through the notice; walking or thrusting away ends protection with no `Perfidy`, using a weapon writes one; a disconnect while surrendered leaves the ship or character in place; surrendering clears none of the surrenderer's own records.
- **Prisoners (D49):** capture clears exactly one `Alive` bit and writes exactly one row on the same tick; `alive + prisoners held elsewhere + bodies + replacements owed` is constant per origin across capture, transfer, ransom, rescue, recruitment, a level crossing and a save; a prisoner's kit equals their `CrewDelta` kit; a death in custody with no other actor writes `PrisonerHarm` against the custodian; a restrained player can `GiveUp` at 60 s. **Review additions:** at every tick each person is exactly one of an `Alive` bit, a `Prisoners` row or a body, on their own ship as on a captor's; a restraint frees the person's `CrewDelta` entry; a 17th restraint on one grid is refused and overwrites nothing; a ransom changes no credit total anywhere (stock moves, at salvage value); a recruit pays the sign-on fee and needs a bunk.
- **Hired crew (D47):** hiring debits the fee and the pool and needs a bunk and life-support rating; wages are debited once per watch worked and never while the server is empty or the ship is laid up offline; unpaid watches lower morale and at zero the crew leave only at a dock; a player sitting at the helm displaces the watch without a pop and the AI resumes after `ReliefTicks`; every act under an order is recorded against the owner; crew refuse to fire on the surrendered.
- **Logoff (D47):** an owner logging off mid-trip leaves a ship that finishes the trip, docks and starts nothing; one logging off mid-fight leaves a ship that is still a member until the engagement ends; an offline owner's station keeps its services until its till is empty and loses no staff.
- **Fights (D50):** the calibration test (Stepped and One-shot against L2, per class pair); toggling witness 100 times during one fight changes neither the step stream nor any quantity except by the fight itself; a save and load mid-fight continues the same stream; a ship locked by a player at 15 km gets a body on that tick in its own bubble and is hit by a shot that left another bubble; no ship that is fighting a player or is within 2 km of one is ever a background member; with the L2 cap reached, a player's lock swaps a background ship in; an unwitnessed fight with no player in the SOI promotes nothing; lock spam past the limit promotes nothing; a member of a Stepped or Real engagement is never demoted to L0. **Review additions:** a One-shot encounter and the same encounter forced Stepped write identical rows at every outcome tick; a player entering the SOI mid-raid changes nothing; a member leaving does not change another member's draws (keyed by slot); a lock-only engagement pushes no hazard and delays no hauler, and a group's fifth is refused; a lock past the remote budget is refused, and the refused target can neither be hit nor fire; a lock never completes before the body exists; an unlocked round promotes the L1 hull on its path before it arrives; a combat anchor promotes no bystander and realises no asteroid; 64 engagements never step on one tick; with the pool full, a new fight evicts a provisional engagement and none is dropped unresolved.
- **Hull stages (D50):** per-stage counts never exceed their caps for any camera path through 1,000 hulls; stage choice is monotone in size on screen and does not flicker at a threshold (hysteresis and dwell); every hull in frustum over 3 px is drawn by exactly one stage outside a cross-fade; GPU memory stays under the budget with 256 unique plans (LRU eviction, no leak); an unfetched player hull draws from its silhouette and upgrades without a pop larger than the cross-fade; a hull at L0 beyond 20 km is drawn at the same place as the same hull forced to L1. **Review additions:** H0 memory never passes its 60 MB pool and no H0 hull its 400k hull triangles, whatever the hull's size; an H2 mesh never passes 8k triangles for hulls of 30 m to 1 km; draw records never pass 4096; shadow passes draw shell meshes for every hull but the camera's own; a player hull at H2 or H3 is drawn from the far-mesh blob without its blueprint being fetched.
- **200-hull bench:** `--fleet-bench` at the 4 + 12 + 64 + 120 mix meets <= 2.0 ms GPU, <= 0.05 ms main thread and <= 130 MB, with zero allocations a frame; the same with one 500 m hull as the camera's own (the H0 pool holds); and a hub with 40 docked hulls inside 1 km, judged in motion for the 2 m cells past the H1 cap.

### Milestones

| | Needs | Scope | Exit check |
| --- | --- | --- | --- |
| **A1** | M6 (flight computer) | `FlightCommand`, `ShipAi` at L2 only, `patrol` and `escort`, contact grid, debris avoidance, skill tiers, `AiPanel` | 24 AI ships fly a patrol in one bubble with a player; AI row <= 10 us a tick; zero allocations |
| **A2** | M7 (rails, warp) | L0 and L1, `TrafficSchedule`, promotion and demotion with reconstruction (L0 > L1 at 20 km), AI warp, `StationController` docking, `hauler`, `TrafficHullInfo` | 4096 L0 + 1024 L1 + 96 L2 within the cost table; continuity check passes; a player watches a hauler arrive, dock, load and leave on schedule |
| **H1** (new, D50) | M1's indirect-count draws and instance record (review.md P4, C9), ship-hulls LOD, A2 for the contacts | the hull stage ladder: `HullStageSelector`, whole-ship bakes by plan hash, the proxy, the impostor baker, points, hull lights, count caps, cross-fades, silhouettes for unfetched hulls | `--fleet-bench`: 200 hulls at 4 + 12 + 64 + 120 in <= 2.0 ms GPU, <= 0.05 ms main thread, <= 130 MB, no stage flicker on a fly-through, zero allocations a frame; a 40-ship hub and a 500 m hull as the camera's own. Judged in motion, not in stills |
| **A3** | M8 plus rooms (ship-atmosphere's design rooms) | `Glazing`, `ViewPortal`s, `CrewNav` with decks and room hops, `CrewManifests` with two-word masks at 100 crew, the schedule, ambient rendering, station crowds, **keys and the sight feed** (D48: built in from the start, since retrofitting privacy means re-plumbing the manifest) | 256 figures: <= 0.15 ms GPU, <= 0.02 ms main thread; peer agreement test; the leak test; figures visible through a hauler's bridge window from a passing ship, on an open ship by key and on a private one by sight |
| **A4** | A3 plus the on-foot character and damage | C2 agents, the ring ladder inside a grid (D51), promotion and demotion, `CrewDelta`, priority at the cap, casualties, helm authority and succession, service points, **surrender of people, restraint and `Prisoners`** (D49), the hand scanner | board a hauler seen from outside: the same people are at the same posts; kill the watch and the ship goes dead-man; a 100-crew ship boarded by four players never exceeds 24 active agents and the crew two decks away cost nothing; a cornered crew member surrenders, is restrained, and is still restrained after the player leaves and returns; 48 active agents within budget **in the worst scene** (a boarding fight at a station with 24 AI ships in the bubble and 16 players present) |
| **A5** | combat, sensors, factions/law docs | combat actions, `pirate`, security response, economy coupling, **engagements in all three modes, cross-bubble fire, `--fight-calibrate`, ship surrender, prizes and ransom** (D49, D50), the ship `ScanCrew` (D48), persistence of all of it | destroying haulers on one route measurably raises the destination's prices and brings a patrol; a pirate attack watched from 15 km, then from another planet, then with nobody in the SOI: the last two end identically (one stream), the first within the calibration tolerance; a 200-hull battle holds the tick budget with <= 96 bodies and the client's 30 KB/s; a hauler surrenders, is boarded and its crew are held |
| **A6** (new, D47) | A4 (C2, helm), A2 (routes); `ScanCrew` and surrender orders need A5 | hiring at a `crew_office`, the crew page in the designer, wages and morale, `ShipOrder` and `CrewOrder`, `player_crew`, `RunRoute` as a private route, player-owned station staff, the logoff rule | a player hires five crew, walks off the bridge and the ship holds station, follows and docks on order; a private route runs three trips and the account shows wages out and trade in; the owner logs off mid-trip and finds the ship docked and idle at the destination |

Hazard responses to beacons, flares and dust land with the systems that produce them; wormhole queueing lands with wormholes.

## Open questions for the user

Answered since the first draft and removed from this list: players' own AI crew (D47), crew positions as intelligence (D48), surrender and capture (D49), traffic density and what is simulated far away (D50), the crew cap (D51). Their designs are in place above. What is left:

1. **Does the placeholder pilot matter to the ship?** You said the pilot is a placeholder for a ship-flying AI. This doc proposes one step more: the AI keeps the helm only while that bridge watch is alive and at the helm, so storming the bridge or killing the watch stops the ship until a relief arrives (`helmNeedsWatch`, default on). The alternative is that the pilot is purely cosmetic and only cutting power or the helm wiring stops the AI. Which do you want?
2. **Offline fleets (follows from D47).** When an owner logs off, their AI-crewed ships finish the current trip, dock and stop (`offlineTrading: false`); crewed stations keep working. Should an offline player's ships instead keep running their routes while the server has anyone on it? It would earn while away, and leave the hulls open to raids the owner cannot answer.
3. **Off-screen conflict.** Encounters are still a low statistical rate per route, now resolved by the fight model. Should factions fight wars that move station ownership with no player watching, or does ownership change only through player action?
4. **Stolen and prize hulls, ransom and bounties.** Interim rule: a captured or surrendered AI ship can be flown and stripped but not sold to, refitted at or docked at lawful stations; ransoms are paid in kind from the victim owner's station stock, with hulls valued at salvage (no credits until owner treasuries are a real ledger); handing prisoners to an `authority` desk pays nothing yet. Too strict, or right until the factions/law doc exists?
5. **Which crews are private, and is scanning an offence?** Proposed: civilian shipping and public station decks are open (anyone near computes their crew for free), military, security, pirate and all player-owned grids are private (sight or scan only), and a `ScanCrew` is detectable and logged but not a crime. Should civilians be private too (more scanning gameplay, a little more bandwidth), and should scanning inside a station's protected zone be an offence? And should a full-quality scan show **player characters** as well as crew (proposed: yes, at 1 Hz; D48 only says crew)?
6. **Captive players.** A restrained player can `GiveUp` after 60 s (ordinary death and respawn, kit left to the captor). Is 60 s right, and should a captor be able to hold a willing player longer for ransom between players?
7. **Hired-crew loyalty.** v1 has one morale byte: unpaid or badly led crews surrender sooner and leave at the next dock. No mutiny, sabotage or defection in flight. Enough, or do you want crews that can turn on an owner?

Revision and review history: [history/ai-ships-and-crew.md](history/ai-ships-and-crew.md).
