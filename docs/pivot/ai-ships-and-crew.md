# AI ships and crew

Status: design, nothing built. Written 2026-11-25 against docs/pivot (README, decisions D9-D37, frames-and-zones, warp, wormholes, ship-hulls, ship-design-and-construction, exotic-matter, tractor-beams, rings-and-asteroids, review). All costs are estimates to be measured with `--ai-bench` and `--crew-bench`; nothing here is a perf claim yet. Lead review 2026-11-26: fixes made in place, listed in "Review notes" at the end.

## Recommendation in one paragraph

The world is full of AI ships and crewed stations, but almost none of it is simulated. A ship lives at one of four levels: **L0** a schedule entry (~100 B, no entity, zero per tick), **L1** an entity on rails (the existing `Rails` / `Warp` / `Frozen` placements, 0-30 ns a tick), **L2** a Jolt body flown by a ship AI through the same flight computer a player uses, **L3** an interior grid bubble with real characters. Crew live at three levels: **C0** a manifest (16 B header and a 64-bit alive mask per grid), **C1** ambient figures that are a pure function of `(crew seed, plan, tick)` evaluated on each client for free, **C2** real characters, spawned only within 20 m of a player and capped at 16 per grid. The pilot in the seat is a placeholder: a C0/C1 figure with no control authority. The ship AI flies. Proposed on top of that decision (open question 7, a per-class data flag): the bridge watch is the AI's *liveness token* (kill or remove it and the AI loses the helm until a relief walks there), never its operator. Everything that crosses a level is reconstructed from state that persists (fuel, cargo, damage brushes, alive mask, bodies, crew deltas, delay) and has exactly one owner at a time, so nothing heals, refuels, duplicates, teleports or respawns by being looked away from.

## User decisions this doc implements

- The game has AI ships.
- AI crew are not fully simulated until needed (C0 and C1 are not simulation).
- The pilot is a placeholder for a ship-flying AI (`ShipAi` issues the commands; the seated figure is visual and nothing reads its hands). Tying the AI's helm to that figure being alive is this doc's **proposal**, not the user's decision (open question 7).
- Players see the crew of other ships and stations passing the windows, in a simpler render (C1 through `ViewPortal`s: instanced low-poly figures near, silhouette cards farther, lit-window flicker beyond).

## 1. Ship simulation levels

### The four levels

| Level | What exists | `PlacementMode` (frames-and-zones) | Server cost per tick | Cap (dedicated / listen) |
| --- | --- | --- | --- | --- |
| **L0 statistical** | One `TrafficShips` row (~100 B). **No entity, no `Placement`, no frame.** Position is a function of the row and the tick, evaluated only when asked | none | 0; one heap event per leg change (~0.5 us each, ~15 a second server-wide at the cap) | 4096 / 1024 per server |
| **L1 rails** | An entity with a `Placement`: a guided schedule leg (closed form), coasting Kepler, thrusting Verlet (off schedule only), warp closed form, or frozen (docked, parked). No Jolt body, no bubble | `Rails`, `Warp`, `Frozen`, `Throat` | 0 on a schedule leg / coasting / frozen / warp table; ~30 ns thrusting off schedule; decide at 1 Hz ~1 us only while off schedule (on a schedule leg `FollowTrip` is event-driven) | 512 / 128 |
| **L2 simulated exterior** | A free-flying Jolt compound body in an exterior bubble, the plan's bake (shared by plan hash), `ShipAi` writing `FlightCommand`s, `ShipLedger` and `PowerNet` at 10 Hz | `Simulated` | ~1.5 us Jolt share + ~0.5 us flight computer + ~0.1 us AI average | 24 per bubble; 96 / 24 per server (soft: see "Caps throttle approach") |
| **L3 interior active** | L2 (or L1 in warp) plus a grid bubble and C2 crew agents | grid bubble exists | grid scene step + ~20-50 us per walking agent | bounded by the C2 caps (section 3) |

L0 is the only new level. L1, L2 and L3 are frames-and-zones' existing rules applied to ships nobody is aboard; this doc adds caps, hysteresis for AI, and the state that must survive each crossing.

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
| L0 > L1 | The hull's located position is within **200 km** of a player (warp.md's `BeaconRange`), **or** its next 60 s of legs come within 200 km of a player, of a player's committed warp exit (from that warp's T0) or of a gate crossing a player has booked, **or** a player has interest in the `Tunnel` or `Mouth` frame its leg is in | Beyond 300 km of every player and every such exit for 60 s **and** on a schedule leg or a stray **and** not pinned (below) | <= 64 a tick, nearest first (an entity and a `Placement`, no body: ~0.3 us each). Invisible by construction, not by distance: a traffic contact and an L1 entity on the same leg are the same closed form on the client (section 7). A hull within 200 km of a player is never L0; the L1 cap evicts only hulls beyond that, farthest first, and an evicted hull stays a traffic contact, so nothing leaves a map |
| L1 > L2 | frames' rule: within 2 km of a physics anchor, **or** predicted closest approach to an anchor's bubble within 2 s under 2 km (warp.md's merge predictor), **or** a player's weapon lock, tractor lock or tracked projectile on it while it is inside that player's bubble hard radius (4 km) | > 3 km from all anchors **and** contact-free **and** >= 5 s at L2 **and** no damage, lock, tractor or incident involving it in the last 10 s **and** not `CombatUntilTick`. Past the 4 km hard radius it leaves whatever its state (frames' size limit) and carries on at L1 (see "Pursuit and combat across levels") | <= 4 ship promotions a tick (inside frames' 32-transfer budget; warp arrivals keep their own pre-booked budget). A damaged hull's bake starts when it comes within 10 km closing, so the body is ready at 2 km |
| L2 > L3 | frames' rule: a character is in the grid (`GridVolume`, C11) or grounded on it. **C0 and C1 crew are not occupants and never create a grid bubble** | no player character in the grid for 20 s and no C2 agent pinned (section 3) | the grid scene returns to the pool; C2 agents demote first |

- **AI is never a physics anchor in its own right.** A ship is L2 only because a player (or something in contact with what a player made simulate: frames' contact rule) is near. An AI convoy fighting AI pirates with no player in 2 km stays L1 and is resolved statistically (section 6). This is frames-and-zones' anchor rule unchanged.
- **Caps throttle approach, never tangibility.** Nothing within 2 km of a player is ever intangible. At a cap (24 free-flying AI bodies in a bubble, 96 on a server) an arriving lawful AI ship holds on rails **outside 3 km** (a `Hold` leg) instead of entering. An AI ship already inside 2 km when a player appears (a player drops out of warp beside a convoy) is promoted regardless; the overflow is absorbed by sending the farthest lawful AI ship that meets the demotion conditions out to a hold, and if none qualifies the cap is exceeded and counted in the profiler. Docked ships are children of the station's compound and landed-parked ships are `Frozen` statics (frames): they are not free-flying bodies and do not count.
- **Priority at a cap.** Ships hostile to or pursuing a player, security responders, and ships a player has locked, tractored or boarded outrank lawful traffic: one of them entering a full bubble displaces the farthest lawful AI ship to a hold. A bubble full of haulers is never a shield against pursuers.
- Player-owned and player-crewed ships are never counted against, or refused by, the AI caps.
- **Pins (never below L1, persisted as an entity):** a player character aboard, online or offline (warp.md's offline crew); a player-owned item, body or docked ship aboard; a player's tractor lock; items in a hold that are not the hull's `CargoItem` (a stash, a bomb); a damage layer that does not coalesce to 64 brushes within 10 % of its removed volume. Pinned hulls count against the L1 cap first and are never evicted.

### Pursuit and combat across levels

- **Leaving range does not despawn a pursuer.** `Pursue`, `Attack`, `Escort` and security response are behaviour state in `AiShips`, not a property of L2. A pursuer that falls outside 3-4 km continues at L1 on the thrusting rails integrator along an intercept solution (decide at 1 Hz) and is promoted again by the ordinary 2 km / 2 s predictor. It gives up only by behaviour data (`pursuitMaxS`, fuel floor, lost-contact time, the target entering a zone where it may not fight), never because of its level, and it cannot demote to L0 while pursuing (it is off schedule).
- **Rails are neither a shield nor a weapon.** An L1 ship cannot be hit and cannot fire (warp.md: a rails arrival can't collide while on rails). So demotion from L2 is refused while `CombatUntilTick` runs or within 10 s of a damage, lock, tractor or incident, and a lock or tracked projectile promotes inside the hard radius. Engagements beyond 4 km (long-range weapons) need the combat doc to put target and shooter in one bubble or resolve the hit on rails: listed as a requirement on that doc, not solved here.
- **Warping away** loses a pursuer that cannot follow: the departure side learns only the direction (warp.md). The `IncidentRecord` and the owner's standing do not go away; the response moves to wherever the player next asks to dock.

### What is reconstructed on each crossing

Nothing may pop or cheat. State that survives every level lives in the hull slot or in lazy side pools keyed by hull slot:

| State | Where it persists | L0 > L1 | L1 > L2 | L2 > L1 / L1 > L0 |
| --- | --- | --- | --- | --- |
| Position, velocity | derived: `Locate(row, tick)` | `Placement` built from the current leg (elements for a coast, leg start state for a burn, `WarpTable` entry for a warp, station-local pose when docked). Exact by construction: L1 runs the same legs | elements or leg state to a body state (frames, ~200 ns + ~5 us body create). Shape from the plan-hash bake cache; catalog AI plans are pre-baked at server start | body state to elements (~200 ns). The AI replans the rest of the trip from the current state; the difference from the nominal schedule goes into `DelayTicks`, so L0 carries the delay forward |
| Fuel, propellant, EM, AM | `FuelQ` (fraction of the plan's tanks, per commodity class, 4 x q16) | copied | tank items are materialised from the fractions (item ledger entries, exotic-matter's `ItemStack`) and the row's fractions are zeroed; the 10 Hz `ShipLedger` takes over | summed back to fractions, **rounded down**, so cycling a ship across the 2-3 km band can never add fuel (it loses at most 1/65535 of a tank per cycle). At L0/L1 each completed leg debits its closed-form burn (same thrust, Isp and warp fuel formulas, evaluated per leg, not per tick). Refuelling happens only at a `Docked` leg and debits station stock |
| Cargo | `CargoItem`, `CargoQty` (exact integer units) | copied | **moved**, not copied: inventory entries are created in the plan's holds and `CargoQty` is zeroed in the same step (crates become bodies only if a hold is breached or a player opens it) | only entries of `CargoItem` that are **inside this hull's holds** at the demotion tick are summed back, and they are removed as they are summed. A crate in a player's hands, in a tractor beam, on another grid or adrift outside stays its own entity and is not counted. Other items in the holds pin the hull at L1 |
| Hull damage | `DamagePool`: the ship-hulls damage brush layer coalesced to <= 64 brushes (2.2 KB), allocated only for damaged hulls (pool 256) | pool row follows the entity; the brushes are part of the L1 entity's stream-in, so a damaged hull looks damaged before it has a body | brushes are replayed into `ShipHull.Damage` before the body is created, so mass, rooms and breaches are the ones the player left | re-coalesced (ship-hulls' bounding-sphere merge only ever grows a hole, so it never heals; a layer that would grow by more than 10 % of removed volume is kept whole and pins the hull at L1). Repair happens only during `Docked` legs: it extends the dwell by `RepairTicksPerBrush` and debits station stock; the pool row is freed when empty |
| Component and wiring state | `SystemsQ`: 16 bits of destroyed-or-cut flags for the critical set (helm, flight computer, main drive, warp drive, reactor, life support, beacon, comms), plus the wiring state bytes ship-design already keeps for non-factory ships, kept in the same pool row | copied | applied before the first tick at L2 | summarised. A ship with no drive is `Adrift` (an L1 coast, a salvage and piracy target, a distress call), not teleported home |
| Crew | `CrewAlive` (64 bits), `CrewBodies`, `CrewDose` and `CrewDelta` side pools (section 3) | copied to the grid's `CrewManifest` | unchanged | unchanged. Replacement crew join only at a `Docked` leg, after `CrewReplaceTicks` each, and their kit is debited from the owner's station stock |
| Incident and standing | the incident log (server) keyed by owner | n/a | n/a | n/a |

- **No free healing rule (tested):** for every field above, the only writers at L0/L1 are closed-form leg debits and dock events that take time and station stock.
- **One owner of truth per level (tested).** Below L2 the hull row and its side pools are the truth. At L2 and L3 the materialised ledgers, inventories and `ShipHull` are the truth and the row's quantity fields are zero. A crossing moves state; it never copies it. A **save** writes each L2/L3 AI ship either as its entity (if pinned) or through the same sum-back as a demotion, never both, so a save and reload cannot double anything.
- **Split hulls.** A piece that separates (ship-hulls' split check) is a new body. The hull slot follows the piece that keeps the body; cargo and crew are divided by where their holds and scheduled nodes are at the split tick. The lost piece is a wreck entity with its own contents (pinned while it holds any), not a second hull slot.
- **Continuity exit checks:** (1) L0 and L1 agree: demote and promote the same on-schedule ship 1,000 times along a trip; `Locate` and the L1 pose are equal to the bit at every crossing and `DelayTicks` does not change. (2) L1 > L2 hands over the exact state (frames' transfer error, <= 0.5 mm) and L2 > L1 fits the body's state; the L2 flight is real physics, so its deviation from the plan goes into `DelayTicks`, which for an undisturbed flight stays inside the trip plan's 2 % tolerance.

## 2. Ship AI (the "pilot")

### Authority: the AI flies, the pilot is its token

- `ShipAi` is a per-ship server controller. At L2 it writes a `FlightCommand`, the same struct a player's seat input is turned into before it reaches the flight computer (ship-design "Flight computer": control allocation over thrusters and surfaces, Space / Atmo / Decoupled). It also calls the same server entry points players call: warp plot and engage, docking request, queue and catch-slot booking, tractor consent, comms hails. **It never writes a pose, a velocity or a force.** An AI ship can do nothing a player in that hull could not.
- **The seated pilot is a placeholder: a C0/C1 figure with no control authority.** Nothing reads its hands, it operates no controls, and no character simulation is needed for a ship to fly. This is the user decision.
- **Proposed (open question 7; `helmNeedsWatch` on the ship class, default true): the AI is a bridge-watch capability, not an always-on autopilot.** The AI has the helm only while all of these hold:
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

- At the caps: 96 L2 ships average ~8 us a tick for decide and steer; 512 L1 ships ~9 us if every one were off schedule (on-schedule ships are event-driven and cost nothing here, so typical is under 1 us). The Jolt bodies (96 awake compounds, ~0.15 ms CPU) are the real cost, which is why the cap is on L2 count and not on thinking.
- Decide and steer run in tick step 4 (inputs) as one job over the due ships; each ship reads the tick-start snapshot of its bubble's contact list and writes only its own `FlightCommand`. No shared mutable state.
- **Contacts:** `BubbleManager` already holds each bubble's entity list. A per-bubble `ContactGrid` (positions, velocities, radii, owner; <= 256 entries, rebuilt at 2 Hz, ~5 us a bubble) gives each AI its <= 8 nearest by time to closest approach. This is not the sensors system (README lists sensors as missing); when sensors exist, the contact list is filtered through them.

### Behaviours (data-driven)

A behaviour is a named set of **options**. Each option has a utility (a sum of weighted, clamped inputs from a fixed input table) and an **action** from a fixed C# action table. Decide picks the highest utility, with a 15 % stickiness bonus for the running option so ships don't dither. No scripting language, no allocation.

- **Actions** (C#, each a small state machine over `FlightCommand`s and requests): `FollowTrip` (run the route legs), `Undock`, `ClearZone`, `PlotWarp`, `Approach`, `HoldAt`, `RequestDock`, `DockWithConsent` (asks the station tractor, D26) or `DockManual`, `QueueGate`, `CrossGate`, `TunnelTransit`, `Patrol` (waypoint ring), `Escort` (formation offset on a leader, joins its fleet warp: warp.md "AI accepts automatically"), `Mine` (rings-and-asteroids lasering cycle, outside every `ProtectRadius`), `Pursue`, `Attack`, `Evade`, `Flee` (sublight out of weapons range, then warp to the nearest friendly station), `Hail`, `Distress`, `Yield` (stop and submit to scan or boarding).
- **Inputs** (fixed table, all O(1) reads): hull fraction, crew fraction, fuel fraction, cargo value, threat score of the top contact, own strength / threat strength, distance and time to closest approach, inside a protected zone, standing with the top contact's owner, hazard severity ahead, queue position, time behind schedule.
- **Shipped behaviours:** `hauler`, `miner`, `patrol`, `escort`, `pirate`, `courier`, `tug`, `station_security`. Each is ~30 lines of JSON (section 8).
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

An unseen hazard (silent warp arrival, cut beacon) is not in the list, so AI ships can be hit by it. That is intended, and the liability falls where warp.md puts it.

### Combat basics

Combat, weapons and sensors have no doc yet (README "Missing systems"). This doc fixes only what the AI needs from them and what it guarantees:

- **Target choice:** utility over the contact list by threat score, standing and escort duty. Re-evaluated at 1 Hz.
- **Manoeuvre:** hold a preferred range band and aspect (both from the ship class's weapon data), jink by skill, break off when `hull < fleeHull` or `crew < fleeCrew` or own strength / threat strength < `fleeRatio` (behaviour data).
- **Fire control:** aim at 10 Hz with a lead solution degraded by the skill's aim error; fire decisions gated by rules of engagement in the behaviour (`returnFireOnly`, `protectedZoneNoFire`).
- **Subsystem intent:** pirates aim to disable (drive, then helm) and board; security aims to disable; nobody AI-controlled tries to destroy a station (D27: capture is preferred).
- **Calls for help:** `Distress` and `Hail` are comms events that station security and escorts read as inputs.

### Liability

- `IncidentRecord` applies to AI exactly as to players. **Change to rings-and-asteroids.md:** `Actor`, `CoActor*`, `VictimOwner` and `Liable` become `OwnerId` (a tagged id: a player, or an NPC owner such as a faction, a company or a station), not `PlayerId`, and so do the tracing fields that feed them (`SourceOwner`, `LastBeamOwner`, site `Owner`, `Shooter`, `ThrownTag.Owner`, `HeatContributor`). Records are still server-only.
- **Crew are victims too.** Killing or wounding a crew member or a crowd member at any level (C0 through a window, C2 face to face) writes a `Hostile` record with the grid's owner as `VictimOwner`.
- An AI ship's actor is its owner. A lawful AI hauler whose mining debris hits a station is liable under D33 like any miner; an AI pirate's `TractorLock` or `WarpCollision` lowers its faction's standing with the victim's.
- Players attacking AI ships write records with the AI's owner as `VictimOwner`. That is what drives station security response and prices (section 6). The future factions/law doc consumes all of it.

## 3. Crew simulation levels

### The three levels

| Level | What exists | Who evaluates | Cost |
| --- | --- | --- | --- |
| **C0 aggregate** | `CrewManifest` row per crewed grid: template, seed, `Alive` mask (64 bits), condition, competence. Side pools only when needed: bodies, dose, overrides | server | 0 per tick. Read on events |
| **C1 ambient** | Nothing new in the sim. A figure's pose is `CrewSchedule.Evaluate(manifest, nav, index, tick)`, a pure function | each client that can see a window; the server only on events and near players | ~30 ns idle / ~150 ns walking per evaluated figure, at 10 Hz |
| **C2 full** | A character entity: `CharacterVirtual` (or the zero-g capsule), health, needs, task, inventory, weapon. Replicated like a player | server | ~20-50 us per walking agent per update, ~0 parked |

**Cap: 64 crew per grid** (one `ulong` each for `Alive`, `Full`, `Suited`). Bigger ships are fiction-heavy on automation; stations are several linked grids (frames-and-zones "Huge structures") with 64 staff each plus anonymous crowds (section 5).

### C0: the manifest and what it changes

- A **crew template** (data, per ship class) lists roles with counts, posts and a watch plan. `index` 0..63 maps to a role by cumulative counts, in template order, forever.
- **Identity** is `CrewId = (hull or station id, index)`. Name, face, body and outfit come from `hash(CrewSeed, index)`; role, watch and home bunk from the template. Identity never depends on the level.
- **Ship stats from the manifest** (recomputed on casualty events, never per tick):
  - *Helm:* the on-watch pilot's skill tier (section 2).
  - *Reaction time:* decide and react delays scale with `1 + 0.5 x (1 - bridgeFilled)`.
  - *Damage control:* repair rate of the damage layer and of cut wiring = `engineersAlive x repairPerEngineer` (data), spent on the most critical item first. Zero engineers, zero repair.
  - *Gunnery:* turrets beyond the count of alive gunners run on the fire-control computer at one tier lower.
  - *Life support:* consumables debit by alive count (exotic-matter's ledger, a per-grid scalar).
  - *Morale threshold:* `fleeCrew` in the behaviour reads the alive fraction.
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
- **Conditions:** `Normal`, `Alert` (battle posts, suits on, running at 4 m/s), `Abandon` (to pod posts), `ShoreLeave` (docked: half the off-watch crew are off the ship; they are the "+6 per docked ship" in the station's crowd and return at `Undock`).
- **Epochs (condition and unsafe-room changes).** A change of condition or of the unsafe-room mask is one event `CrewEpoch {condition, unsafeRooms, tick}`, at most one per 30 s per grid. From that tick everyone walks from where they were at that tick to their post under the new state. A walk can outlast 30 s, so the origin cannot be re-derived by chaining earlier epochs: at the epoch tick the server and every client that holds the manifest **store** each alive figure's origin (`node, nextNode, offset`: 6 B, <= 384 B a grid). A client that receives the manifest later gets the stored origins with it while any epoch walk is unfinished (`tick < epochTick + MaxWalkTicks`); after that every figure is on the base schedule again and the origins are dropped.
- **Overrides:** a small per-grid table (<= 16 entries: `index, fromNode, fromOffset, startTick, toPost, flags`) covers everything that is not the base schedule: a relief walking to the helm, a demoted C2 agent rejoining, a survivor fleeing a breach, an attendant pinned at a counter. An override expires when its walk ends and the base schedule's post equals `toPost`, or at the next slot. With the table full, whatever needs a row waits (a C2 agent stays C2, a relief stays at its post): nobody snaps to make room.
- **Rooms are named by plan-time ids.** ship-hulls relabels rooms on every topology change, so `NavNode.RoomId` and `ViewPortal.RoomId` are the intact plan's room indices and never runtime labels. The server maps live rooms to plan rooms on topology events and publishes only the result: `unsafeRooms`, a bitset over plan rooms (<= 64; vacuum, fire, merged with space by a breach, dose above the threshold).
- **Doors:** ambient figures pass doors cosmetically (the client plays the leaf animation; the room graph is not touched). **No re-pathing at C1:** `NextHop` is one static table per plan, so a figure whose next hop enters an unsafe room it is not `Suited` for stops at the last safe node, and a post inside an unsafe room is replaced by the role's muster post from the template. Both are pure functions of the epoch's mask, so peers still agree.
- **Zero-g:** an unplated room swaps the walk loop for the float loop on the same route.

### C2: when a figure becomes a character

| Trigger | Scope promoted |
| --- | --- |
| A player character inside the grid (`GridVolume`), or on the hull within 5 m of an open portal | crew whose pose is within **20 m** of that player (straight line, same grid), same room and rooms joined to it by open portals first |
| **Hostile** boarding imminent: a collar lock or docking without a granted `DockRequest`, a player's tractor lock without consent (D26), a hull cut in progress, or a player EVA within 50 m of the hull of a ship whose owner's standing with that player is hostile | crew within 20 m of the entry point; the grid goes to `Alert`. A cleared docking, a consented tow or a visitor walking aboard never raises `Alert` |
| Line of fire: small-arms or a player's weapon hit resolved inside a room while a player character is within 100 m with line of sight through open portals or glazing | crew in that room and the adjacent rooms, within 40 m of the shooter |
| Interaction: an interact ray on a figure or on its `ServicePoint` | that one figure |
| Scripted need: succession relief while a player is aboard, a capture `Override` at a `CommandCore` being contested | the crew involved |

- **Caps:** 16 **active** C2 agents per grid; 48 active per dedicated server, 12 on a listen server. **Parked** agents (at a post, no `ExtendedUpdate`) have their own cap, 128 / 32, because they cost a kinematic capsule and nothing per tick; a parked agent that starts to move needs an active slot. Crowd post density is validated in data so no point has more than 12 posts within 20 m.
- **Priority, so the cap cannot be starved.** Active slots go by class: (1) crew of a grid under hostile boarding or in a fight with a player, (2) interaction and scripted need, (3) proximity alone, staff before crowd. A higher class takes the slot of the lowest-class agent farthest from any player, which demotes seamlessly as below. Without this, a group idling in a station concourse could hold all 48 slots and leave a ship elsewhere to be boarded against figures that cannot react.
- **Past the cap** the remaining figures stay C1. They cannot fight or block a doorway, but they are not immune: every hit is still resolved against their scheduled capsule (Casualties), and a kill frees nothing for free (it is a body and an incident).
- **Demote** when the agent has been more than 30 m from every player for 20 s, is not in combat, not restrained, carried, wounded-and-being-treated or mid-conversation. Dead agents demote at once (a body record).
- **`CrewDelta`: what a person keeps across levels.** Any crew member whose state differs from the template gets a 4 B entry in a per-grid side pool (<= 16, persisted): health in 16ths, a kit mask (weapon, suit, tool, keycard: a cleared bit means taken or spent), and flags (restrained, hostile to a named player group). Promotion applies it and demotion writes it. So backing off 30 m for 20 s does not heal the crew you wounded, and a weapon taken from a crew member does not come back when they are promoted again. Kit is reissued only at a `Docked` leg, from the owner's stock. With the pool full, the least-changed entry is rounded to the template (health up, never kit).
- **Promotion (identity and position continuity):** the server evaluates the schedule at the promotion tick and spawns the agent at exactly that pose, with that animation phase, name, appearance seed, role, suit state and (from the dose pool) dose. `CrewPromote {grid, mask, tick}` tells clients to stop drawing those indices as ambient on that tick; the entity's first snapshot is stamped the same tick. Clients also de-duplicate by `CrewId`: a character entity carrying a `CrewId` hides the ambient figure with that id whether or not the event has arrived yet, and the ambient figure is drawn until one of the two arrives, so a late packet gives neither a double nor a gap. **A crew member seen through a window at a console is at that console when you board**, because both come from the same function at the same tick.
- **While C2** the schedule still supplies the agent's goal ("be at post P by tick T") unless a task interrupts it (fight, flee, treat, repair, surrender, talk). Its `Full` bit masks it out of the ambient set everywhere.
- **Demotion:** alive agents write `Alive`, dose and suit back, then get an override "walk from here to where the schedule says". Clients receive `CrewDemote {grid, index, node, offset, tick}` and draw the ambient figure from that spot. Nobody snaps.
- **Cost at the cap:** agents standing or sitting at a post are **parked**: a kinematic capsule and no `ExtendedUpdate`. Walking agents update at 30 Hz, alternating ticks. 48 agents with 40 % walking is ~0.25 ms CPU on jobs; all 48 walking is ~0.85 ms. They step in tick step 7 with players' characters.

### Casualties at each level

The C0 rule is a conservative summary of what C2 would do: nobody dies at C0 who could not have died at C2 (never anyone outside the affected rooms), and the outcome is decided by where the schedule puts each person at the event tick.

| Cause | C0 and C1 (same rule; C1 only shows it) | C2 |
| --- | --- | --- |
| Damage brush (weapon hit, collision, shatter fragment) | evaluate alive crew at the event tick (<= 64 x ~100 ns, only on damage events to crewed grids); anyone whose capsule the brush overlaps dies; anyone within 1.5 x the radius is wounded (counts as absent from their post for `WoundedTicks`) | real damage on the character |
| Breach, decompression (ship-hulls `Breach` edge) | crew in the breached room: `Suited` survive; others survive if `Dist(to the nearest pressure door) / 4 m/s` is under the room's time to 30 kPa (from volume and breach area, ship-hulls' flow), and get a flee override; otherwise they die at that time, not instantly | the room's real pressure acts on the character |
| Leak | sets the room unsafe after its time to 30 kPa; crew reroute; nobody dies | same pressure model |
| Radiation (exotic-matter 1 Hz room dose) | a lazy per-grid `CrewDose` array (64 x ushort cGy) is allocated on the first dose; each second, crew take the dose of their scheduled room; thresholds as exotic-matter | per-character `DoseGy`, same thresholds |
| Small-arms through a window | the round first meets the glazing as it would at C2: it must defeat the glazing material's rating (ship-hulls strength), which makes a damage brush and so a `Leak` or `Breach`; a round that does not is stopped. Past the glass the server tests the ray against the scheduled capsules in the rooms it crosses, **at the shooter's fire tick** (the schedule is a pure function of the tick, so lag compensation is exact and free). The first hit puts the grid on `Alert` and promotes by the line-of-fire rule | real hit, same glazing test |
| Off-screen combat (L0/L1) | a seeded draw per engagement from the loss table (section 6): a number of indices are cleared, lowest-priority roles first | n/a |

- Each casualty is `CrewCasualty {grid, index, tick, cause, node, offset}` (12 B). Clients play the fall where the figure was. The body is a `CrewBodies` entry (<= 16 per grid, persisted): boarding later finds the body where it fell, as an inspectable entity.
- A dead index stays dead for that hull. Replacements are new people (a new `CrewSeed` salt for that index) taken on at a dock.
- **Loot from people is finite.** A body carries exactly its role's kit minus its `CrewDelta` kit mask, once. Replacement crew draw their kit from the owner's station stock (no stock, no kit). Crowd members carry nothing lootable. Killing crew is never a source of items.
- **One evaluation per grid per tick.** Damage events to a crewed grid are batched: the alive crew are evaluated once for that tick (<= 6 us) and every brush of the tick is tested against the cached capsules, so a shrapnel burst of 50 brushes costs one evaluation, not 50.
- **Same answer at both levels (tested):** the C0 rule uses the same capsule, the same glazing and brush tests and the same tick as C2 would, so whether a given shot or breach kills a given person does not depend on whether a player happened to be within 20 m.

## 4. Seeing crew through windows

### Windows are data from the hull bake (change to ship-hulls.md)

- **`Glazing` materials:** a flag on `data/materials` entries (transparent, with strength like any material). Glazing surfaces draw in the transparent pass (inventory.md already reserves it for "ship glass").
- **`ViewPortal`s:** the bake clusters glazing surface cells per (room, dominant normal) into oriented rectangles: `{RoomId, centre, normal, halfU, halfV}` in int16 1/32 m, <= 64 per ship, largest first. `RoomId` is the plan-time room index (section 3). They ride with `CrewNav` in the server-sent `CrewPlan` blob.
- **Breaches are portals too.** A `Breach` edge (a hole of >= 0.5 m, already replicated with the damage layer) counts as a dynamic view portal into its room, as a disc of the damage brush's cross-section, so figures and bodies in a holed room are not culled while the room is in plain sight. <= 8 per grid, largest first.
- **Glazing is never LOD-skipped.** ship-hulls drops `Interior` brushes at LOD >= 2, so a far ship has no room behind its glass. From LOD 2 the glazing shader does **interior mapping** against the room's AABB (one ray-box per fragment, wall tint and light level from the room record), so a lit window still reads as a room.
- **Room light:** one RGBA per room (`RoomLight`: powered and lit, emergency, dark), driven by the room's bus state, already known to clients that stream the ship.

### Which figures are evaluated

Per client, on a job, staggered so each grid updates at 10 Hz:

1. **Grids in ambient range:** crewed grids within 600 m of the camera, at most 16, nearest first. A station counts per linked grid.
2. **Visible rooms:** for each `ViewPortal`: facing (`dot(normal, toCamera) > 0.1`), in frustum, projected area >= 64 px2. The union of their `RoomId`s is a room bitset. If the camera is inside the grid, rooms come from the hull's own room visibility instead and this path only handles glass looking into other rooms. ~30 ns per portal.
3. **Figures:** evaluate the alive, non-`Full` crew of that grid (<= 64 x ~30-150 ns ≈ 4-8 us per grid update) and keep those whose node's room is in the bitset, plus bodies in those rooms.
4. **Size and count:** drop figures under 4 px tall (about 420 m at 1080p and 60° vertical FOV; scales with resolution and FOV). Keep the nearest 256 overall.
5. Write one 32 B instance per figure into a persistent mapped buffer: `relOrigin (float3), velocity (float3, for extrapolation between 10 Hz updates), yaw, anim, animPhase, appearanceSeed, roomLight index, portal index`.

CPU: ships are <= 64 figures (~8 us an update); a station grid with staff and a full crowd is up to 256 (~30 us). Worst case, 16 full station grids: ~0.5 ms of job time per 100 ms, about 80 us a frame at 60 fps; typical (a station and a few ships) is under 10 us a frame. All on a job, ~0 on the main thread beyond one buffer flip. Zero allocations (fixed arrays).

### How they are drawn

| Tier | Range (1080p) | What | How |
| --- | --- | --- | --- |
| **Near** | <= 60 m, cap 48 | ~800-triangle figure, 20 bones, 8 baked loops (stand, console, walk, run, eat, sleep, float, fallen) in one bone-matrix texture; 4 body types x seeded palette and outfit by role | one instanced indirect draw in the opaque pass. The hull at LOD 0-1 has real interior geometry and a real hole behind the glass, so ordinary depth testing does the rest |
| **Mid** | 60 m to the 4 px limit, cap 208 | a two-triangle card with a 16-frame silhouette atlas per loop, tinted by `RoomLight` | **portal-projected cards:** the vertex shader projects each card vertex through the camera onto its `ViewPortal`'s plane (plus a 2 cm bias toward the camera) and clips to the portal rectangle with four clip distances. The card then lies on the glazing surface, so it depth-tests correctly against everything in front of the hull, needs no interior geometry (LOD >= 2), and its perspective is exact by construction |
| **Far** | beyond the 4 px limit | no figures | the glazing shader modulates the window's emitted light with a slow seeded flicker scaled by the room's scheduled head count (one byte per portal at 1 Hz), so a crewed, lit ship still looks inhabited |

- **The tier follows the hull's LOD, not only the distance.** A 3D figure needs the real room behind the glass, which exists only where the bricks around that portal are at LOD 0-1 (ship-hulls drops interiors at LOD >= 2). So a figure is Near only if it is within 60 m **and** its portal's bricks are at LOD 0-1; otherwise it is a card. Without this rule a figure would be drawn in the opaque pass inside a hull with no interior and the interior-mapped glass would composite over it.
- **Lighting:** room light colour and level, plus the sun term through the portal for near figures. Dark rooms draw silhouettes only when something bright is behind them; otherwise the figures are skipped.
- **GPU estimate:** near 48 x 800 = 38k skinned triangles covering few pixels, mid 208 cards: ~0.03-0.05 ms from outside. Inside a busy station concourse (48 near figures filling the view) ~0.1-0.15 ms. Two indirect draws, one pipeline each, no per-figure descriptors. Budget: <= 0.15 ms GPU, <= 0.02 ms main-thread CPU, measured in `--crew-bench` (a station flyby and a concourse walk).
- **Memory:** bone texture 8 loops x 32 frames x 20 bones x 2 texels ≈ 80 KB; silhouette atlas ~1 MB; meshes ~200 KB.

### Station crowds

- Staff (<= 64 per grid) are scheduled crew like a ship's. **Crowds** are anonymous: `crowd(j, tick)` uses the same function over the grid's crowd posts and wander nodes with a per-grid `CrowdSeed`; count = `min(CrowdMax, base + 6 x docked ships)`, <= 192 per grid. They cannot be individually tracked across sessions, and promote to C2 by the same 20 m rule at the lowest priority (they get an identity for as long as they are C2 or dead).
- **The count is replicated and never pops.** `CrowdCount {grid, count, fromSlot}` (8 B) takes effect at the next slot boundary. Figure `j` exists while `j < count`: one that starts existing walks in from the grid's nearest `Entrance` node (a dock tube, a lift) during that slot, and one that stops existing is given an `Entrance` node as its post and is dropped on arrival. Nobody appears or vanishes mid-concourse.
- **A killed crowd member stays dead.** `CrowdDead` is a 192-bit mask per grid in the manifest; a set bit removes `j` from the function and leaves a body. The bit clears after `CrowdReplaceTicks` (data, hours), when a new person with a new seed salt walks in from an `Entrance`. Without it the same figure would walk back out of the function a tick after being shot.
- The 256-figure client cap applies across staff and crowd; nearest win.

### Multiplayer

- **Zero bandwidth for motion.** Clients evaluate the same function from: the `CrewPlan` blob (`CrewNav`, resolved template and role tables, `ViewPortal`s, fetched by content hash with the blueprint) and `CrewManifestNet` (`CrewPlan` hash, seed, `Alive`, `Full`, `Suited`, the current epoch (condition, unsafe rooms, tick), overrides, bodies, crowd count and `CrowdDead`: ~40 B typical; <= 300 B with overrides and bodies; <= 700 B in the rare case that epoch origins ride along), sent when a crewed grid comes within 1 km and on change.
- **Corrections are events:** `CrewEpoch` 20 B, `CrewCasualty` 12 B, `CrewPromote` 12 B, `CrewDemote` 16 B, `CrewOverride` 12 B, `CrowdCount` 8 B. All reliable and tick-stamped, applied on their tick like frame changes. Non-urgent ones (a proximity promotion, a watch-driven override, a crowd count) are stamped 12 ticks ahead so they arrive before they apply; urgent ones (a casualty, a line-of-fire promotion) are stamped now and a late arrival is covered by the `CrewId` de-duplication (section 3) or plays the fall late.
- **C2 agents** replicate as character entities (~20 B per update, frames' interest table). Parked agents send nothing until they move.
- **Accepted leak:** a client within 1 km knows where every crew member of a ship is, since it computes it. The schedule is public knowledge from the plan anyway; C2 positions (people reacting to you) are only ever sent by ordinary interest. See open question 2.
- **Test:** the server and a client evaluate 10,000 random `(manifest, index, tick)` triples and must agree bit-for-bit on `(node, offset)`, including across an epoch with unfinished walks, with a client that joined after the epoch, and between a Debug and a Release build on two machines (the CRC pattern rings-and-asteroids uses for its ring tables).

## 5. Stations

- **Populated the same way:** a `CrewManifest` per linked grid (staff), crowds, C2 by proximity. Stations are `Frozen` or anchored grids, so they are L1 until a player is within 2 km and get interior bubbles only where players are.
- **`StationController`** (server, one per station, 2 Hz; ~3 us per update while any ship is in its zone, 0 otherwise). It is the AI of the station, gated like a ship AI: it needs a powered traffic console and an on-watch `Controller` post filled. Unfilled: no new clearances, lights go to "uncontrolled" and visitors dock at their own risk and liability.
  - **Traffic control:** a ring of `DockSlot`s (pad or collar, size class, booked-until tick), approach lanes and hold points (station data; rings-and-asteroids' defence already keeps clear of "declared approach lanes"), a speed limit inside `ExclusionRadius`. `DockRequest` returns a slot, a lane and a window, or a hold point and a queue position. AI ships and players use the same request; AI haulers at L0/L1 book the same slots through their `Hold` and `Dock` legs, so a player really does wait behind scheduled traffic and can really take a slot an AI wanted (the AI's `DelayTicks` grows).
    - **Bookings cannot be hoarded.** One live booking per ship and `MaxBookingsPerGroup` per owner group (the rule wormholes.md's `TrafficControl` already has); a booking lapses if its ship is not on the approach lane when the window opens. So a group cannot starve a station's trade, or another player, by holding its slots.
    - **AI does not crowd players out of shared queues.** Scheduled traffic may hold at most `aiQueueShare` (data, default a quarter) of a station's dock slots of each size class and of a gate's `MouthQueue` ring (64 slots a direction) in any window. A player's request takes an unconfirmed AI slot beyond that share (the AI's `DelayTicks` grows). L0 and L1 bookings use the same rings, tunnel caps and direction windows as everyone's.
  - **Tractor docking** is offered only on request (D26): the request carries the consent flag for the station's emitter group.
  - **Gates:** a station gate's wormhole `TrafficControl` (wormholes.md, `data/traffic/*.json`) is unchanged; AI ships are ordinary requesters of catch slots and `MouthQueue` slots.
- **Defence automation:** `StationDefence` (rings-and-asteroids: debris, AM-fed emitters only, D28) and `StationShield` are unchanged and need no crew. **Security response** is new and is an AI behaviour, `station_security`: on a `Hostile` or `Reckless` `IncidentRecord` inside `ProtectRadius`, the controller hails, then launches up to its garrison (docked security hulls from its `TrafficShips` rows: L0 > L1 > L2 through an `Undock` leg, so response takes real time and can be exhausted). Station turrets wait for the combat doc. During a declared siege (D27) the garrison fights the besiegers; capture still goes through the `CommandCore`, where the defending staff within 20 m of the attackers are C2 and contest the `Override`.
- **Shops and NPC services:** a `ServicePoint` is a component (console family) with a `Post` and a service id from `data/stations/services.json`: vendor, shipyard desk, dockmaster, refuel, medic, mission board, plan market.
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
- **Off-screen conflict (L0/L1 vs L0/L1)** is statistical and sparse: per route and per 10 min, a seeded draw against `lossRate(patrol level, pirate presence)` picks at most one hull to be `Raided` (cargo lost, a seeded number of crew cleared, damage brushes added from a template) or `Destroyed`. Rates default low (a hull per route per ~20 h played) and are data. No AI fleet battles are simulated off-screen in v1.
- **Persistence:** saved with the world: `TrafficRoute`s, `TrafficShips`, `TrafficStrays`, `DamagePool`, crew side pools (bodies, dose, `CrewDelta`, epoch origins), `StationStock`, dock bookings, the event heap, `AiShips` behaviour state for off-schedule ships (a pursuer is still pursuing after a restart), and pinned hulls as entities. Everything is keyed on `TickIndex`, so it all pauses while the server is empty (user decision) and nothing catches up on resume. On load every unpinned AI ship that is not within 200 km of a player starts at L0 from its summed-back row.

## 7. Networking, data, costs, files, tests, milestones

### Networking summary

| What | Wire |
| --- | --- |
| Traffic contacts (L0 **and** on-schedule L1) | **The wire speaks legs, never levels** (as it speaks frames, never bubbles). For every hull in an SOI a client has interest in, the client holds a **leg record**: `TrafficLeg {hull, leg, frame, tick, state, accel, duration}` (~56 B) or elements (~48 B), sent once per leg change from the same heap event that advances the schedule. The client extrapolates the closed form for its map, radar and distant dot. Whether the server holds that hull as a row or as an entity is invisible, so promotion, demotion and cap eviction change nothing a client can see. Other SOIs: 0.2 Hz radar contacts from `Locate` (one evaluation per hull per system, shared by all clients). Station boards (arrivals and departures) are queried on demand |
| Off-schedule L1 ships | frames' rails rules: elements once per change (~48 B); thrusting as `RailsBurn {entity, tick, state, accel, duration}` (~56 B) per thrust change, at most 1 Hz; "Same SOI" 1 Hz corrections |
| L2 ships | ordinary body snapshots (~20 B), interest by frame tree. AI ships are server-owned: no client predicts them (answers review.md's "who owns a ship" for AI) |
| Warp, docking, queues | the existing events (`WarpDepart`, `WarpBeacon`, `WarpArrival`, queue and catch-slot events). AI civilians always beacon |
| Crew | section 4: manifest on approach, five small events, C2 as characters |
| Ship AI intent | never sent. Clients see only what the ship does |

At the caps a player near a busy station sees ~24 L2 AI ships (24 x 30 Hz x 20 B ≈ 14 KB/s before delta compression, inside frames' 30 KB/s per-client budget only because most are slow or parked; AI ships docked are `Frozen` and send nothing). Measure in A2. Leg records: ~200 hulls in a system changing leg ~10 times an hour is under 1 record a second (~30 B/s per client), plus one burst of <= ~11 KB when a client first takes interest in a busy SOI, spread over a few seconds.

### Data structures (SoA, preallocated, zero alloc in the tick)

```csharp
namespace BrutalGame.Sim.Ai;

public readonly record struct OwnerId(uint Value);            // tagged: player or NPC owner (faction, company, station)
public enum ShipLevel : byte { Statistical, Rails, Simulated, Interior }
public enum TripLeg : byte { Docked, Undock, ClearZone, Spool, Warp, RealSpace, Approach, Hold, Dock, QueueGate, Cross, Tunnel, Adrift }

public struct TrafficRoute                                    // world data, ~40 B
{
    public EntityId Origin, Destination; public ushort ShipClass, CargoItem; public uint PeriodTicks, PhaseTicks;
    public ushort FirstHull; public byte HullCount, PatrolLevel; public OwnerId Owner; public ushort Behaviour;
}

public sealed class TrafficShips                              // SoA, index = hull slot, cap 4096; ~106 B a row, ~435 KB at the cap
{
    public ushort[] Route; public uint[] CrewSeed; public ulong[] CrewAlive;
    public ushort[] FuelQ;                                    // 4 per row, q16, rounded down: propellant, warp fuel, EM, AM. Zero while L2+
    public uint[] CargoQty;                                   // exact units. Zero while L2+ (the holds own it)
    public ushort[] SystemsQ; public ushort[] DamageRow;      // 0xFFFF = undamaged
    public ushort[] StrayRow, CrewDeltaRow;                   // 0xFFFF = none
    public long[] TripStartTick; public uint[] DelayTicks; public long[] DestroyedUntilTick;
    public uint[] LegEnds;                                    // 12 per row: offsets from TripStartTick
    public ShipLevel[] Level; public byte[] Flags;            // Pinned, Stolen
    public int[] Entity;                                      // -1 at L0
}
public struct TrafficStray { public FrameId Frame; public KeplerOrbit Rails; public long SinceTick, RecoverAtTick; }   // pool 256
public struct CrewDelta    { public byte Index, HealthQ, KitMask, Flags; }                                             // 16 per row, 4 B
public static class TrafficSchedule
{
    public static TripLeg Locate(TrafficShips s, int hull, long tick, in CelestialSystem rails, out FrameId frame, out double3 p, out double3 v);
    public static void PlanTrip(TrafficShips s, int hull, long departTick, in ShipClassStats c);   // once per trip, ~5 us
}

public sealed class AiShips                                   // SoA, index = ai slot, cap 608 (L1 + L2)
{
    public int[] Entity; public int[] Hull;                   // hull slot, or -1 for non-traffic AI (security, pirates off route)
    public ushort[] Behaviour; public byte[] Option, ActionState, Skill; public OwnerId[] Owner;
    public EntityId[] Goal; public double3[] GoalLocal; public FrameId[] GoalFrame;
    public long[] NextDecideTick, ReactAtTick, CombatUntilTick, HelmLostTick; public byte[] Helm;   // Held, DeadMan, Relief, Derelict
    public FlightCommand[] Command;                           // L2 only
}
public struct Hazard { public byte Kind, Severity; public float3 A, B; public float Radius; public long From, To; }   // capsule in bubble space, 44 B

public sealed class CrewManifests                             // SoA, index = crewed grid slot, cap 1024
{
    public int[] GridEntity; public ushort[] Template; public uint[] Seed;
    public ulong[] Alive, Full, Suited; public byte[] Condition, Competence;
    public ulong[] UnsafeRooms; public long[] EpochTick;      // the current epoch: condition + unsafe plan rooms, from EpochTick
    public ushort[] OverrideRow, BodiesRow, DoseRow, DeltaRow, EpochOriginRow, CrowdRow;   // lazy side pools, 0xFFFF = none
}
public struct CrewEpochOrigin { public ushort Node, NextNode, Offset; }   // 64 per row, kept only while an epoch walk is unfinished
public struct CrowdState      { public byte Count, PrevCount; public uint FromSlot; public ulong Dead0, Dead1, Dead2; }   // station grids only
public struct CrewOverride { public byte Index, Flags; public ushort FromNode, FromOffset, ToPost; public long StartTick; }   // 16 per row
public struct CrewBody     { public byte Index, Cause; public ushort Node, Offset; public long Tick; }                         // 16 per row

public sealed class CrewNav                                   // per plan hash, shared; sent by the server with the blueprint
{
    public NativeList<NavNode> Nodes;                         // <= 256: int16 x3 at 1/32 m, RoomId, PostKind, Facing, Anim
    public NativeList<byte> NextHop; public NativeList<ushort> Dist;   // N x N
    public NativeList<ViewPortal> Portals;                    // <= 64
}
public struct CrewPose { public ushort Node, NextNode, Offset; public byte Anim, Flags; }       // fixed point; the only thing peers must agree on
public static class CrewSchedule
{
    public static CrewPose Evaluate(in CrewManifestView m, CrewNav nav, CrewTemplate t, int index, long tick);
    public static int EvaluateRoom(in CrewManifestView m, CrewNav nav, CrewTemplate t, int roomId, long tick, Span<byte> indices);
}

public sealed class CrewAgents                                // C2, SoA, cap 176 (48 active + 128 parked)
{
    public int[] Entity, Grid; public byte[] Index; public float[] Health, DoseGy; public byte[] Task, Parked, Priority, KitMask; public ushort[] GoalPost;
}

public struct DockSlot { public EntityId Holder; public OwnerId Group; public long From, Until; public byte SizeClass, Kind, Flags; public ushort Lane; }   // ring of 32 per station; Flags: Ai, Confirmed
public struct AmbientInstance { public float3 RelOrigin; public float3 Velocity; public ushort Yaw; public byte Anim, AnimPhase; public uint Appearance; public byte RoomLight, Portal; }   // 32 B, client
```

### Data definitions

```jsonc
// data/ai/behaviours/hauler.json
{ "id": "hauler", "lawful": true, "skill": "Trained",
  "options": [
    { "action": "FollowTrip", "base": 1.0 },
    { "action": "Evade",      "base": 0.0, "inputs": { "hazardAhead": 2.0 } },
    { "action": "Flee",       "base": 0.0, "inputs": { "threat": 1.5, "hullLost": 1.0, "crewLost": 1.0 }, "min": 0.8 },
    { "action": "Yield",      "base": 0.0, "inputs": { "threatRatio": 1.2, "disabledDrive": 3.0 } },
    { "action": "Distress",   "base": 0.0, "inputs": { "underAttack": 2.0 }, "cooldownS": 30 } ],
  "fleeHull": 0.6, "fleeCrew": 0.5, "returnFireOnly": true }

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
| L1, 512 ships | 0 on schedule; worst case ~15 us integrators + ~9 us decide | worst case = every ship off schedule and thrusting; typical is a few dozen |
| L2, 96 ships: decide, steer, react | ~8 us | staggered |
| L2: flight computer and force pass | ~50 us | same as 96 piloted ships |
| L2: Jolt | ~0.15 ms CPU on workers | 96 awake compounds |
| Contact grids and hazards, 32 bubbles at 2 Hz | ~10 us average | |
| Level checks | inside frames' 10 Hz membership pass | +~50 ns per AI ship |
| Station controllers, 64 stations at 2 Hz | ~6 us average | 0 when nobody is in the zone |
| Crew events (schedule evaluation on damage) | ~6 us per damaged crewed grid per tick | batched: one evaluation per grid per tick however many brushes |
| C2 promotion checks, 5 Hz | ~6 us per check; 64 grids with players ≈ 30 us a tick average | only grids with a player within 100 m |
| Dose on crewed grids, 1 Hz | ~6 us per dosed grid per second | only grids with a dose |
| C2 agents, 48 active + 128 parked | ~0.25 ms typical, ~0.85 ms worst, CPU on jobs | parked agents cost 0 per tick |
| **Total** | **~0.15 ms main-thread-equivalent plus <= 1 ms of job CPU** | listen server at its caps: about a quarter of that |

**Against 64 players (frames' budget: ~3 ms Jolt CPU and ~2 ms of player `CharacterVirtual` CPU a tick, ~0.6 ms wall on 6 workers):** the AI adds 96 bodies to ~2000 (about 5 %), and the C2 worst case adds 48 walking characters to 64 (0.85 ms on 2 ms, ~0.15 ms wall). That fits, but the two worst cases land together in exactly the scene that is already the heaviest (a boarding fight at a busy station), so A4's exit check measures that scene, not an empty one. 64 players spread over ~16 bubbles get about 6 free-flying AI ships each at the server cap; if that reads as empty, the lever is the 96 (a Jolt cost, measured in A2), not the thinking.

`ProfilerPanel` rows: Traffic (L0 events), AI (decide / steer, counts per level), Crew (C2 count, schedule evaluations), and client-side Ambient (figures near / mid, job us, GPU ms).

### Changes to other docs

- **frames-and-zones.md:** "Who gets a Jolt body" is unchanged; add that C0 and C1 crew are not occupants and never create a grid bubble or anchor physics, and the AI caps and the 4-promotions-a-tick budget. Tick order: step 2c pops traffic events; step 4 runs `ShipAi` as input; step 9 adds crew promotion checks at 5 Hz for grids with a player within 100 m. Interest table: add "Crewed grid within 1 km: manifest and events only" and "Traffic in the same SOI: leg records, once per leg". L0 ships are not entities and have no `Placement`; no new `PlacementMode`. "Ships with no bubble": a thrusting rails ship **on a schedule leg** is evaluated from the leg's closed form (a guided leg), and the Verlet integrator is for off-schedule thrust only. "Who gets a Jolt body": a weapon or tractor lock or a tracked projectile inside the hard radius also pulls the target in; the AI caps hold arrivals outside 3 km and never leave anything intangible inside 2 km.
- **warp.md:** no rule changes. AI warps go through the same plan validation, beacon and exclusion rules at every level (an L0 warp's commit is a scheduled event); the "no physics anchor: rails" arrival is this doc's L1. AI civilians never run silent. A scheduled convoy is a fleet warp by construction (same legs, formation offsets), within the 16-ship limit.
- **wormholes.md:** no rule changes; AI are ordinary requesters and `TunnelCruise` users. L0 gate crossings are schedule legs that still occupy `MouthQueue` slots, count against `TunnelCap` and wait for direction windows; scheduled traffic is limited to `aiQueueShare` of a ring; a collapse or a closed window re-plans the affected rows. Player interest in a `Mouth` or `Tunnel` frame promotes the L0 hulls whose leg is in it.
- **ship-hulls.md:** `Glazing` material flag with a small-arms rating; `ViewPortal`s from the bake; glazing kept at every LOD with interior mapping from LOD 2; `CrewNav` compiled beside rooms and sent by the server in the `CrewPlan` blob; **plan-time room indices** that survive relabelling (the server maps live rooms to them on topology events); a bridge-glazing Warning in validation.
- **ship-design-and-construction.md:** `Post` data on the seat, console, bunk and service families; wander nodes from the Standard harness pass; new families `fn.autonomy` and `fn.service_point`; a crew template reference on catalog ship plans; Warnings for "helm post unreachable from berths" and "pod seats < crew".
- **exotic-matter.md:** `ShipLedger` and `PowerNet` run for L2+ only; L0/L1 use per-leg closed-form debits with the same formulas. Life support debits by alive crew count.
- **rings-and-asteroids.md:** `IncidentRecord` ids and the tracing fields that feed them become `OwnerId`; AI miners carry D33 liability; security response hooks into incidents; harming crew or crowd is a `Hostile` record.
- **A future combat doc (requirement, not a change):** engagements beyond the 4 km hard radius must either share a bubble or resolve hits on rails; until it exists, an L1 ship can neither be hit nor fire.
- **A future factions/law doc:** consumes `Stolen` hulls and crew-harm records, and decides what lawless ports pay.
- **tractor-beams.md:** AI sets the consent flag only through `DockWithConsent` and tow requests, and treats any other lock as hostile.
- **review.md:** resolves "who owns a ship" for AI ships (the server) and "rails ships with occupants" for AI crew (they never anchor; `GridInertial` applies).

### Files

- `src/Sim/Ai/` (no Vulkan): `TrafficRoutes.cs`, `TrafficShips.cs`, `TrafficSchedule.cs`, `TrafficEvents.cs`, `ShipLevels.cs` (promotion, demotion, reconstruction), `AiShips.cs`, `ShipAi.cs` (decide, steer, react), `Actions/*.cs`, `Behaviours.cs` (data load, utility), `ContactGrid.cs`, `Hazards.cs`, `Helm.cs` (authority, succession), `FlightCommand.cs`.
- `src/Sim/Crew/` (no Vulkan): `CrewManifests.cs`, `CrewTemplates.cs`, `CrewNav.cs`, `CrewNavCompiler.cs`, `CrewSchedule.cs`, `CrewCasualties.cs`, `CrewAgents.cs`, `CrewPromotion.cs`.
- `src/Sim/Stations/`: `StationController.cs`, `DockSlots.cs`, `ServicePoints.cs`, `StationStock.cs`, `SecurityResponse.cs`.
- `src/Ships/`: `ViewPortals.cs` and the glazing flag in the bake.
- `src/Engine/` (client): `AmbientCrewRenderer.cs`, `AmbientCrewJob.cs`; `shaders/crew_figure.*`, `shaders/crew_card.*`, interior mapping in the glazing shader.
- `src/Debug/`: `AiPanel.cs` (levels, caps, per-ship behaviour and helm state), `CrewPanel.cs`.
- `data/ai/`, `data/crew/`, `data/traffic/`, `data/stations/`.
- `tests/Sim.Tests/`: below.

### Tests

- **Schedule:** peers agree bit-for-bit on 10,000 random evaluations (two builds, two machines); no two crew are assigned the same post in the same slot while posts suffice; a casualty moves nobody else; pose is continuous across slot, watch and epoch changes, including two epochs 30 s apart with walks unfinished (no step over the running speed per tick); an override ends at the scheduled pose; a crowd count change adds and removes figures only at `Entrance` nodes; a killed crowd member does not reappear before `CrowdReplaceTicks`.
- **Level crossings:** the two continuity checks; cargo, damage brushes, `SystemsQ` and `CrewAlive` are identical and fuel is equal or lower by at most one q16 step after any L0 > L2 > L0 round trip with no events; no field increases outside a `Docked` leg; 1,000 forced cycles across the 2-3 km band never raise any quantity.
- **No duplication:** at every tick, `row quantity + materialised quantity` for a hull equals what it was before the crossing; a crate removed from the hold (in hands, in a tractor, adrift) is not summed back; a save taken while a ship is L2, then loaded, holds the same totals; a foreign item in a hold pins the hull.
- **Caps and tangibility:** with a bubble at 24 and the server at 96, an AI ship already within 2 km of an arriving player still gets a body; a pursuer displaces a lawful ship from a full bubble; no entity within 2 km of any player is ever on rails.
- **Pursuit:** a pursuer left behind at 5 km is still pursuing at L1, is promoted again on approach, survives a save and load, and gives up only by its behaviour data.
- **C2 priority:** with 48 active agents held by idle proximity in one station, a hostile boarding elsewhere still gets its defenders; a wounded, disarmed crew member demoted and promoted again is still wounded and disarmed.
- **Window fire:** a round that does not defeat the glazing kills nobody at C0 or C2; one that does gives the same casualty at both levels and a `Hostile` record.
- **Raiding:** over a simulated 100 h, the goods taken from a route never exceed what its origin stock was debited; a `Stolen` hull is refused buyback at a lawful station.
- **Trip plan:** leg durations at L0 match an L2 flight of the same class and route within 2 %.
- **Helm:** cutting the helm Data run, killing the watch, and displacing the pilot each give dead-man hold on the same tick; relief time equals path length / 4 m/s; no qualified crew gives `Derelict`.
- **Casualties:** a breach never kills outside the breached room at C0; a C0 outcome and a C2 replay of the same breach agree on who was in the room; bodies persist through demotion and save / load.
- **Promotion:** the C2 spawn pose equals the schedule pose at the promotion tick; a demoted agent's ambient figure starts within 1/32 m of the agent.
- **Economy:** stock is conserved across a trip (debit at undock equals credit at dock minus losses); a destroyed hull stays out for `ReplaceTicks` and needs its bill of materials; nothing advances while the clock is paused.
- **Caps and allocation:** at every cap the tick allocates nothing (allocation counter test); the 25th AI ship holds outside a full bubble.
- **Liability:** an AI miner's debris writes a record with its owner as `Liable`; a player hit on an AI hull writes one with the AI owner as victim.

### Milestones

| | Needs | Scope | Exit check |
| --- | --- | --- | --- |
| **A1** | M6 (flight computer) | `FlightCommand`, `ShipAi` at L2 only, `patrol` and `escort`, contact grid, debris avoidance, skill tiers, `AiPanel` | 24 AI ships fly a patrol in one bubble with a player; AI row <= 10 us a tick; zero allocations |
| **A2** | M7 (rails, warp) | L0 and L1, `TrafficSchedule`, promotion and demotion with reconstruction, AI warp, `StationController` docking, `hauler` | 4096 L0 + 512 L1 + 96 L2 within the cost table; continuity check passes; a player watches a hauler arrive, dock, load and leave on schedule |
| **A3** | M8 plus rooms | `Glazing`, `ViewPortal`s, `CrewNav`, `CrewManifests`, the schedule, ambient rendering, station crowds | 256 figures: <= 0.15 ms GPU, <= 0.02 ms main thread; peer agreement test; figures visible through a hauler's bridge window from a passing ship |
| **A4** | A3 plus the on-foot character and damage | C2 agents, promotion and demotion, `CrewDelta`, priority at the cap, casualties, helm authority and succession, service points | board a hauler seen from outside: the same people are at the same posts; kill the watch and the ship goes dead-man; 48 active agents within budget **in the worst scene** (a boarding fight at a station with 24 AI ships in the bubble and 16 players present) |
| **A5** | combat, sensors, factions/law docs | combat actions, `pirate`, security response, economy coupling, off-screen losses, persistence of all of it | destroying haulers on one route measurably raises the destination's prices and brings a patrol |

Hazard responses to beacons, flares and dust land with the systems that produce them; wormhole queueing lands with wormholes.

## Open questions for the user

1. **AI for players' own ships.** The tables allow an `OwnerId` that is a player: hired crew, an AI pilot that flies your hauler on a route, AI wingmen. Should players be able to hire crew and run their own AI ships, and if so does an offline player's AI fleet keep trading (it would, at L0, whenever the server has anyone on it)?
2. **Crew positions as intelligence.** Clients within 1 km compute where every crew member of a ship is. Fine for civilians. For military or hostile ships, should the server instead send only the figures in rooms a client can actually see through glass (costs ~8 B per visible figure per second, and removes the wallhack)?
3. **Surrender and capture of people.** Do AI crews surrender, and can they be taken prisoner, ransomed, rescued from pods or recruited? This decides how much C2 behaviour boarding needs beyond fight and flee.
4. **Off-screen conflict.** v1 has only a low statistical loss rate per route. Should factions fight wars that move station ownership with no player watching, or does ownership change only through player action?
5. **Traffic density.** Proposed: ~200 hulls per star system, 5-15 ships in sight around a major station, 1-2 on a frontier outpost. Busier, or emptier?
6. **Crew cap of 64 per grid.** Acceptable for AI capital ships (heavily automated), or do you want hundreds aboard the largest hulls (that needs a second manifest word per grid and denser ambient caps)?
7. **Does the placeholder pilot matter to the ship?** You said the pilot is a placeholder for a ship-flying AI. This doc proposes one step more: the AI keeps the helm only while that bridge watch is alive and at the helm, so storming the bridge or killing the watch stops the ship until a relief arrives (`helmNeedsWatch`, default on). The alternative is that the pilot is purely cosmetic and only cutting power or the helm wiring stops the AI. Which do you want?
8. **Stolen AI hulls.** Interim rule: a captured AI ship can be flown and stripped but not sold to, refitted at or docked at lawful stations. Too strict, or right until the factions/law doc exists?

## Review notes

Lead review, 2026-11-26, against the four user decisions, frames-and-zones, warp, wormholes, ship-hulls and review.md. Fixes were made in place above. Nothing was built or run; every cost is still an estimate.

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
- Combat beyond the 4 km hard radius (needs the combat doc).
- Escape pods carrying C0 crew (which manifest they belong to after launch) depend on open question 3.
- The mined-ore faucet needs a finite site budget from rings-and-asteroids to be fully closed; until then it is a data cap per route.
- Glazing ratings, `aiQueueShare`, `RecoverTicks`, `CrowdReplaceTicks` and the 10 % coalescing threshold are new knobs with guessed defaults.
