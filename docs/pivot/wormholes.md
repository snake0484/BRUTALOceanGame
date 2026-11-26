## Wormholes

### Recommendation in one paragraph

A wormhole is a **pair of mouths**. Each mouth is a one-sided disc of radius `r`. The disc is the open front of a capped **throat shell**. Each mouth has its own frame (`FrameKind.Mouth`), which is fixed in a surface frame, fixed on a grid, or on Kepler rails.

**Roles (round 5, D23):** **natural** wormholes are the **cheapest** way to travel (free to cross; you go where and when they go). **Manufactured** wormholes are the **fastest** (the instant class beats every other option within a system from 20,000 km). **Warp** is for going somewhere specific, relatively close by, cheaply. This replaces the old blanket "warp cheaper, wormholes costlier". Natural pairs are also the only link between star systems: a manufactured plan needs both ends under one `Solar` root (see Natural wormholes, Destinations).

There are two **classes**:
- **Instant** (the most expensive, and the fastest): the two fronts are joined by a fixed throat map `J`. Crossing a mouth is an ordinary zones frame transfer preceded by `J`, so velocity relative to the mouth and orientation are kept exactly, up to a crossing speed limit. Nothing moves during travel.
- **Tunnel** (the cheaper player wormholes, and every natural pair, so also the cheapest travel overall): the mouths open onto a **tunnel**, a place with its own frame (`FrameKind.Tunnel`), much shorter than the real distance (6 km for a planet → moon trip, 24 km for 1 AU). Ships fly through it under their own thrust, in ordinary physics bubbles. The flow becomes a **current** along the bore: ride it, or fight it with thrust, and a strong current against a weak ship is one-way by plain physics.

The lifecycle is tick-exact and deterministic: **Plan → Charge → Commit → Open → Collapse**, with a **Scar** window after an early collapse. `TCommit`, `TOpen` and the planned `TClose` are fixed at charge start. The plan computes a path (a polyline in `Lca`, evaluated at the open tick) and a binding cost quote.

At commit, 3 s before open (warp's `N = 180`), the server:
- spends the power and exotic matter for good;
- decides capture by catchers;
- resolves the far end silently, using warp's exclusion push and occupancy slide. For a managed catcher, B goes to the catch slot it booked;
- creates both shells (and the tunnel frame), fills the **stability** reserve and announces the pair, including its flow.

**Stability** replaces "nothing can extend or close early". Every wormhole holds a reserve of energy, `Es`. Holders feed it with power: the generator, battleships' own stabilisers, and support ships projecting stabilisation from range. Bombs, destabilisers and the mass of every crossing drain it; natural pairs drift back to their natural level. `TClose` is the paid **maximum**: if the reserve reaches zero the wormhole collapses early, 1 s later (a zero in the last second just stops admissions before the planned collapse). A tunnel's mouths outlive its collapse tick while the collapse runs, up to `TEnd`. A fleet can power through a bombing by out-feeding it, and can **re-establish** a just-collapsed wormhole within 60 s for a quarter of the exotic matter, because the path is already drilled.

Stability also shapes a tunnel's interior. A **stable** tunnel is a straight, calm bore that cruise control flies end to end. An **unstable** one twists, and carries gravitational wells, eddies and, near zero, energy storms, all a deterministic function of seed, tick and stability, with no network traffic. Touching the wall discharges into the hull; ramming it throws the ship out into normal space at the matching point along the planned path. Ships can fight inside, but anything projecting gravitons outside its hull is struck by the wormhole, and kinetic rounds ricochet.

**Collapse:**
- An instant pair **flushes out both ways**: anything partway through leaves whole by the nearer mouth, and both mouths give an outward kick that falls off with distance. Nothing is cut.
- A tunnel collapses **at a point**. From there the bore flows outward toward each mouth and the collapse front follows. Light ships ride the flow out of the mouths; heavy ships may be overtaken and thrown out into normal space at their point along the path. Each mouth flushes when its front reaches it.

**Suppression** is an analytic field built from bodies, atmospheres, terrain, hulls, warp drives, other mouths and forecast flares. **Both** ends must pass it for as long as they exist.

**Catchers** are projected anchors, and friend and foe use the same mechanism:
- **Gate** mode pulls only paths that target it. It makes those ends cheaper and lets them sit lower.
- **Interdict** mode captures any path passing within reach. It waives the *deadly* (natural) suppression terms, but never the *invalid* (occupancy) ones.
- An optional **traffic control** computer, in either mode, turns the catch point into a catch volume. It assigns each wormhole a catch slot (position plus open tick), queues fleets, refuses, and charges fees.

**Signatures:** every generator, wormhole, catcher, stabiliser and frame broadcasts an **energy signature**, either a faction key or noise.
- A key can be copied by scanning and then broadcast as a spoof.
- Whether a reader sees through a forgery is a deterministic contest resolved on the server, from emitter quality, the copy's fidelity and freshness, and the reader's sensor quality, closeness and dwell.
- During charge, the crew read the signature of whatever is pulling their path.
- Natural mouths read as noise, so a hostile catcher posing as one only has to broadcast noise.

**Natural wormholes** are usually immensely large (150–1,200 m) and always tunnel class:
- Redirecting one end for one cycle is a fleet-scale interdiction, because its cost grows with `r²` and the power is pooled per faction.
- Players can build a **frame** around a charted mouth to sense traffic at the far side.

**Flow:** a pair may carry a flow. The inflow mouth pulls nearby space in, the outflow mouth pushes it out. An instant throat adds or removes axial speed, up to fully one-way; a tunnel carries it as a current. The pull is applied like gravity in bubbles: a per-scene vector plus a per-awake-body delta. It never uses an `AddForce` that wakes everything.

As with warp, the wire carries a few reliable events and no per-tick wormhole traffic. Stability travels as a piecewise-linear state that clients integrate exactly. Clients near either mouth get the other side streamed from commit onwards.

### Wormhole classes

| | Instant | Tunnel (player) | Natural |
| --- | --- | --- | --- |
| Who makes it | Player generators, as a bought option | Player generators, the default | The system seed |
| Cost | ×2.5 exotic matter and ×1.5 opening energy over tunnel (see Cost model) | The baseline formula. exotic-matter.md's per-class single-ship check (a lone ship warps more cheaply than the cheapest wormhole it fits) is priced at tunnel class | Free to cross: the **cheapest** travel (D23). Redirecting an end costs as before |
| Crossing | Portal: zones' transfer into A, `J`, the flow's `ΔV`, transfer out of B, in one tick | Cross A's disc into the tunnel, fly its length under your own thrust, cross B's disc out | As tunnel |
| Frames | Two `Mouth` frames | Two `Mouth` frames plus one `Tunnel` frame | As tunnel |
| Physics | Bubbles at each mouth only | Bubbles at each mouth, plus bubbles inside the tunnel frame wherever there are occupants | As tunnel |
| Travel time | 0 (transit speed through the disc) | `L_t` divided by the speed the ship can hold against or with the current. Reference (1 g, 200 m/s cruise, no flow): planet → moon 41 s, 1 AU 129 s, 5 AU 145 s | `L_t` is seeded, 5–40 km |
| Flow | Throat `ΔV` at the crossing; one-way at `|Φ| ≥ 0.83` | A current along the bore; one-way emerges per ship from thrust and coupling | As tunnel, seeded per cycle |
| Collapse | Both-way flush at the collapse tick | Collapse flow from a point, fronts run to the mouths, each mouth flushes on arrival | As tunnel |
| Stability effects | Bands change rim discharges, `VCrossMax` and admission only | Bands shape the whole interior (straight bore to twisting storm) | As tunnel, from its natural level |

- Class is chosen at plan and fixed at charge start. A re-establish keeps the class.
- "Mouth" rules (frames, orientation, size, shell, leash, crossing test, outer flow field, flush) are shared. Only what lies behind the disc differs.

### Shared with warp vs different

| Item | Shared (reuse warp.md as is) | Different for wormholes |
| --- | --- | --- |
| `Lca` | Same definition (lowest common ancestor, raised to a non-rotating, non-grid frame) | The path is evaluated once, at `TOpen` poses. There is no per-tick path evaluation |
| Target kinds and exit selection | Body orbit, surface point, grid ring, marker, free point. Band rule for the attach frame. Arrival ring, dispersion seed, `MatchTarget` | The far end is a **mouth**, a lasting entity, not a one-off arrival. It can also be a **catcher**, or a **catch slot** that a managed catcher assigns |
| Validity | Plot results depend only on facts the client can know. Hidden hulls are resolved silently at commit | Validity is the **suppression field**, applied to **both** ends over `[TCommit, TEnd]` (`TEnd`, the latest possible despawn, see Lifecycle), not warp's `RouteShell`. A tunnel's interior has no suppression |
| Exclusion and permission | Same `ExclusionRadius`, permission hook and silent radial push at commit | Exclusion is a hard occupancy term. **No catcher mode waives it** |
| Commit | `TCommit = TOpen − 180`, run in `(TOpen, Id)` order, `ExitReservation`, occupancy slide | Commit **spends resources irrevocably**, decides **catcher capture**, places B in its booked catch slot, fixes the flow, creates both shells (and a tunnel's frame) and fills the stability reserve. Confirmed catch-slot bookings hold their lane in the controller's own slot ring from charge start, not in the global reservation pool |
| Attach | Warp's T1 attach rule, applied **per crossing** | Arrival bubbles for queued traffic are created at `TOpen` |
| Merge | `ApproachPredictor` (predicted closest approach) | Exit lanes feed it like any bubble |
| Carried subtree | The whole grid subtree goes along (crew, items, docked and hangared ships). Proper-acceleration history is reset | The trigger is a physical crossing, not engage. Exterior bodies cross on their own. A warp is refused while its carried subtree contains a mouth host |
| Interdiction | Power-driven, detected with path geometry | Capture moves the far end and needs far more power plus exotic matter. A natural end is redirected per cycle, by fleet-scale power pooled per faction |
| Privacy | The destination goes to the crew only until 3 s before | After commit the destination is public: anyone can look through and follow. Signatures tell readers who *claims* to own a wormhole or catcher, and the claim can be forged |

### Lifecycle (tick-exact)

| Phase | Ticks | World state | Ends on |
| --- | --- | --- | --- |
| Plan | Server preview, valid for 30 s. A managed target also returns a catch-slot **offer**, valid for 30 s and not exclusive | Nothing exists | Charge request, or expiry |
| Charge | `TChargeStart … TCommit` (`ChargeTicks`, stretched to meet a booked catch slot) | The generator draws power into its capacitor. A charge glow shows at the planned A pose. Exotic matter (with any additives) is reserved, not spent. From 25 % charge the crew get `PathPull` readings | `TCommit`. The owner can cancel until `TCommit − 30`. Destroying or disabling the generator before `TCommit − 30` cancels it. In the last 30 ticks nothing cancels |
| Committed (Forming) | `TCommit … TOpen` (180 ticks) | Both mouths and their shells exist, and a tunnel's frame (empty). They are **not traversable**: crossings bounce. Their suppression and exit-lane reservations are active. There is no flow yet. The stability reserve is full; feeds and drains already count | `TOpen`, or an early collapse if the reserve reaches zero (nothing has crossed, so only the mouth flush runs) |
| Open | `TOpen … Tcs` (at most `OpenTicks`, 1,800–72,000 ticks, i.e. 30 s to 20 min; a tunnel needs `OpenTicks ≥ 2 · TunnelLead`) | Traversable both ways by anyone, subject to flow, stability bands and admission. "Closing" visuals play for the last 600 ticks before `TClose`. No new admission is allowed whose tail would clear after `TClose − 60` (tunnels: `TClose − TunnelLead`) | `Tcs`: the planned `TClose`, or `Tz + 60` when the reserve reaches zero at `Tz` and `Tz + 60 < TClose`. A zero later than that does not collapse early: admissions stop at `Tz` and the planned collapse (with its planned `s_c`) runs at `TClose` |
| Collapse | Instant: the single tick `Tcs`. Tunnel: `Tcs` until both collapse fronts have reached their mouths (≤ `L_t / V_f`) | **Instant flush:** straddlers leave whole by the nearer mouth, held bodies by B, both mouths kick outward with falloff. **Tunnel:** no admissions; the collapse flow carries occupants to the mouths or the front ejects them; each mouth flushes and despawns when its front arrives. Suppression and flow end with the last mouth | The last mouth despawns |
| Scar (early collapse of a player pair only; natural pairs leave no scar) | `ReestablishTicks` (3,600, 60 s) after the last mouth despawns | Both mouth frames remain as **scars**: no shell, no portal, still mouth occupancy for every other wormhole, still visible contacts | A re-establish charge start, or lapse |
| Cooldown | `CooldownTicks` on the generator, also started by an owner's cancel or an unused scar lapsing | Normal | Ready |

- **All four ticks are fixed at charge start:** `TCommit = TChargeStart + ChargeTicks`, `TOpen = TCommit + 180`, `TClose = TOpen + OpenTicks`. `TClose` is now the **paid maximum**, not a guarantee: any peer holding the plan knows the latest the wormhole can live, and stability decides whether it gets there. An early collapse is announced 60 ticks ahead (`WormholeCollapse`).
- **`TEnd`, the latest despawn,** is also fixed at charge start: `TEnd = TClose` for an instant pair, and `TEnd = TClose + ceil(60 · L_t / V_f)` for a tunnel, because a tunnel's mouths live on while its collapse fronts run (at most the whole length, from an end). Every window that used to end at `TClose` because "the mouth is gone by then" ends at `TEnd` instead: suppression and the rails sweep, mouth occupancy, exit-lane reservations, catch-slot holds, flare cones, and the natural-schedule constraint. Admission and queue rules keep `TClose`, the latest *open* tick.
- **Booked charge:** when B is a managed catch slot, `TOpen` is the slot tick `TSlot`.
  - `ChargeTicks = TSlot − 180 − TChargeStart`, which must be at least the quoted minimum and at most `MaxBookAhead` (10 min).
  - The same energy is drawn over the longer charge. The ticks are still all fixed at charge start.
- **Cancel by the owner (before `TCommit − 30`)** returns the reserved exotic matter and loses 50 % of the charged energy. It also starts the generator's cooldown, so a charge cannot be used as a cheap probe for path pulls.
  - A booking is released. Its fee is refunded less the controller's `CancelForfeit` share (from its rules), so booking and cancelling is not free.
  - A cancel forced by a controller revoking its catch slot loses no energy, refunds the whole fee and starts **no** cooldown.
  - The last 30 ticks are locked in, so a crew client that has had the plan since charge start is never wrong under 0.5 s of latency.
- **After commit the wormhole will open, unless its stability reaches zero first.**
  - It opens and lasts until `TClose` or an early collapse, even if the generator is destroyed (the wormhole then loses its feed), the owner disconnects, or a shard reply is late. B is never moved after it has been announced (a re-establish is a new announcement).
  - A warp is refused, until the last mouth despawns, for any ship whose carried subtree contains a mouth host. This covers a host docked to a carrier or parked in its hangar. Warp validation step 2 gains this rule.
  - If a grid mouth's host is destroyed, or the host would carry the mouth into hard suppression, the mouth detaches (see Leash).
- **Damage does not cancel a charge.** This is the same knob as `SpoolDamageCancel`, default off.

**Who is told what, and when** (warp's privacy lessons applied):

| Event | When | To | Contents |
| --- | --- | --- | --- |
| Plan result | On request | Requester | ok, or a reason the client can know. L, class, `L_t`, quote (with any additives), `ChargeTicks`, warning flags. For a managed target: a catch-slot offer or the controller's refusal |
| Full plan | Charge start | Crew of the generator host's subtree, plus its group | Everything, including B and the path |
| Charge visible | Charge start | Interest of A | A's pose, radius, `TOpen` and the generator's signature *as each reader reads it*, for the glow. **No B and no path** |
| Instability warning | During charge, on the client | Crew only | Crew clients compute it from catchers **in their own contacts**, against the known path |
| Path pull | During charge, from 25 % charge, on change at ≤ 1 Hz | Crew only | One entry for **every** catcher pulling the path or spinning up to be eligible by `TCommit`, whether it is in contacts or not: arc band, strength class, spinning flag and signature verdict. **No position and no entity id** (see Signatures) |
| Path read | Same | Owner of each pulling catcher | That a charging path is in its pull, and the generator's signature as the catcher reads it |
| Commit | `TCommit` | Crew, interest of A, interest of the final B, the owner of the capturing catcher, and the controller's owner for a managed catch slot | Both mouth poses and frames, radius, shell depth, `TOpen`, `TClose`, flux, flow, `R_f`, flush strength, captured flag, class, `EsMax`, `L_t`, tunnel radius and seed, planned collapse point |
| Crossing | Each transit | Interest of both mouths | The existing frame-change event, carrying the mouth id |
| Stability state | On each change of slope (a feeder or drainer joining, leaving or changing power, ≤ 1 Hz per device; natural relaxation reaching its level). Discrete drains ride on their own events | Interest of both mouths, tunnel occupants, crew, feeders | Reserve, slope and band (see Stability) |
| Early collapse | `Tz`, 60 ticks before `Tcs` | Interest of both mouths, tunnel occupants, crew, feeders | `Tcs`, collapse point, cause |
| Ejection | Each ram or collapse ejection | Interest of the tunnel and of the landing point | The frame-change event, flagged, with the arc fraction |
| Re-establish | Charge start inside the scar window | As "Charge visible", plus crew | A flagged `WormholeCharge` naming the collapsed wormhole |

- Third parties at B learn nothing before commit, which is about what they would learn from seeing the mouth form anyway. This is deliberately more private than warp, which since round 5 (D18) broadcasts its arrival location as a safety beacon unless the ship is military or the wire is cut: a wormhole's far end is announced to B's interest at commit and the mouth itself is the warning, so no beacon is added. A managed controller's owner learns of a booking when it is confirmed, because that is their own traffic control.
- **A capture is revealed to the crew at commit, 3 s before open.** Mouths can be seen through, so hiding a capture would mean faking the view (decided after revision 2: the view is always true).
- **Signatures do not change when a capture is revealed.**
  - The contacts warning and `PathPull` say who *claims* to be pulling the path during charge, and how hard.
  - Whether a pull **wins** is decided only at commit, from the catchers eligible at that tick, and revealed only then.
  - A pull that reads "your faction, Claimed" may be genuine customs or a spoof. A pull that reads "Noise" may be an open natural mouth, which never captures, or a dark interdictor.
  - The crew's decision point is still `TCommit − 30`.
- Interdiction therefore works by:
  - **denial and drain:** the exotic matter is gone;
  - **trapping** ships already committed to a crossing: a queued fleet moving at transit speed, or ships whose autopilot queue slot is less than 3 s away;
  - **traps at the far end:** a flare that starts after open, a fall from a hover mouth, or a flow toward the trap;
  - **deception during charge:** a spoofed or noise signature that persuades the crew not to cancel.
- **Plan rejections never depend on hidden hulls, hidden catchers or reservations.** At 2 plans/s that would be the same system-wide radar that warp's review removed.
  - A managed controller's refusal depends only on its own published policy, its own bookings and its owner's contacts. (The last means a refusal or a skipped catch slot can reflect what the *owner* sees; that is the owner's own intel, shared by the owner's choice of rules.)
  - `PathPull` arrives only after 25 % of a real charge, never in a plan result.

### Stability

Decision A of revision 3: an energy fight replaces "nothing can extend or close early". Decisions B and C add holders and natural recovery.

**State.**
- `Es`, the **reserve**, in joules. `EsMax = EsK · r̂² · m_S`, with `EsK = 10 GJ` and `m_S` from additives (1 by default). An r = 100 m wormhole holds 10 GJ, r = 400 m holds 160 GJ. Natural pairs use the same law.
- `Ψ = Es / EsMax`, from 0 to 1.25 (above 1 is **overcharge**).
- **Fixed point, exact on clients:** `Es` is an int64 in kJ and changes by an int64 `Slope` (kJ per tick) plus discrete drains. Server and clients add the same integers, so no per-tick traffic is needed. Natural relaxation is linear (a constant slope toward the natural level, then zero), not exponential, for the same reason.
- **Fill:** player wormholes start at `Ψ = 1` at `TCommit` (the reserve is paid in the quote's energy). Natural pairs start each cycle at `Ψ_nat,k` when Forming begins.
- **Upkeep is separate:** the duration energy `Eh · OpenTicks` in the quote pays the throat's upkeep to `TClose`. A player wormhole that nobody feeds or drains therefore keeps its reserve and lives to `TClose`. Only drains end it early.

**Bands** (thresholds are integers in kJ, so the band changes on an exact tick both sides agree on):

| Band | `Ψ` | Instant pair | Tunnel interior |
| --- | --- | --- | --- |
| Overcharged | > 1, ≤ 1.25 | As Stable. Leaks `0.01 · EsMax` per second down to 1 | As Stable |
| Stable | `Ψ_s` (0.6) to 1 | Normal | Straight, calm bore of radius `r`. Cruise control available |
| Unstable | 0.25 to `Ψ_s` | A hull touching the rim takes a discharge. `VCrossMax × 0.75` | Twist, wells and eddies scaled by the instability `u = clamp((Ψ_s − Ψ)/Ψ_s, 0, 1)`. Cruise control off. `Ψ_s` is 0.6, or 0.7 with the Aperture additive |
| Critical | above 0, below 0.25 | Every crossing takes a discharge | As Unstable, at `u ≥ 0.58`, with energy storms |
| Zero | 0 | Early collapse at `Tz + 60` | Collapse flow from `Tz + 60` |

**Feed (holders).**
- **Sources:**
  - **The generator,** while its host is within `FeedRange` (5 km) of either mouth, at up to its `ChargeRateW`. A generator that is feeding cannot charge another wormhole.
  - **Stabilisers** (decision B): a component in mode Feed, aimed at one wormhole. Battleships carry the full kit (`FeedW` 2 GW, range 5 km, efficiency 1), so they hold their own jumps. Dedicated **support ships** carry projectors (`FeedW` 1.5 GW, range 50 km, efficiency 0.7) and hold smaller ships' wormholes from a distance. All from `data/stabilisers/*.json`.
  - **Frames** around natural mouths may carry stabilisers (decided, round 5: former open question 13).
- **Range** is from the feeder's host centre to the nearer mouth centre, in normal space. Inside a tunnel, a feeder feeds the tunnel it is in, from anywhere along it (decided, round 5; see Combat for why it is not struck).
- **Rate:** `dEs/dt = Σ η_i · P_i`, capped at `FeedCapW = EsMax / 5 s` per wormhole (2 GW for r = 100 m), up to `Ψ = 1.25`.
- Anyone may feed any wormhole. The owner cannot refuse a feeder, and cannot refuse a drainer either.
- Power only, per the decision; the feeder's cores burn whatever they burn (exotic-matter.md).
- Feeder membership and power are evaluated at 1 Hz, staggered per device. A change of the summed slope sends `StabilityState`.

**Drains.**

| Drain | Amount | Notes |
| --- | --- | --- |
| **Mass throughput** (decision C, every wormhole) | `e_m · m` per **entry** crossing, `e_m = 33 J/kg / (1 + β)` for player pairs, seeded 10–60 J/kg for natural ones | Charged once, at the entry tick, by the entry shard. Exits charge nothing. One full reserve passes 300 s of the pair's flux `F`. Because entries are flux-limited and one entry is capped at `MaxEntryFluxTicks` (see Exit lane and throat hold), mass can drain at most `EsMax / 300 s` on average and at most ~40 % of a full reserve in one entry; one feeder outpaces any mass dump |
| **Anti-graviton pulse** (bombs, runaways) | `D = BombK · Y · (1 − d/R_s)²` for each mouth within `R_s = 5R` of the centre (d to the mouth centre); `D = BombK · Y` (the point-blank value) when the pulse is inside a tunnel. **Rung down** over `PulseDrainTicks` (300, 5 s): a drain slope of `floor(D / 300)` kJ per tick for 300 ticks, the remainder at `TDet` | `BombK = 1 GJ` per kg of yield. **Replaces the mouth stall.** A powered gravity damper covering a mouth at `TDet` blocks that mouth's whole drain, as it blocked the stall. Not capped: bombs are how a capped hold is broken |
| **Destabiliser** (a stabiliser in mode Drain) | `0.5 · η · P`, summed per wormhole and capped at `FeedCapW` | Range and data as Feed. Loud. Capped like the feed, so destabilisers can cancel holders at the cap but not outrun them; below the cap numbers win |
| **Retaliation strike** | `0.005 · EsMax` per strike, at most `0.01 · EsMax` per second per wormhole in total | The wormhole spends energy striking a projector (see Combat). Strikes beyond the cap still land but drain nothing, so a swarm of cheap projectors drains at most 1 %/s, a twentieth of one feeder at the cap |
| **Ejection** | `e_m · m` | Mass torn out through the wall (grids and characters only) |
| **Natural relaxation** (natural pairs only) | Slope `± EsMax / τ_nat` toward `Ψ_nat,k`, then 0 | `τ_nat` seeded, 120–900 s. Pulls overfeeding back down too |

- **Order:** continuous slopes integrate in tick step 2c, with `Es` clamped at 0. Discrete drains apply in step 3 as their events resolve, in step-3 order. A pulse's ring-down is a timed slope: its remainder and first tick apply in step 3 of `TDet`, the rest in step 2c until `TDet + 300`; each wormhole keeps its active ring-downs in a small list (end tick, slope, source `s`), and clients add them from `AgDetonation` with no further traffic. The zero test runs at the end of step 3.
- **Entry refusal:** an entry whose drain would take `Es` to zero is bounced (no damage), so on one shard mass alone never collapses a wormhole. The `Throughput` cause exists only for the cross-shard race: two entry shards each pass their own test against a reserve one hand-off old, and the sum empties it at the owner shard.
- **Examples (r = 100 m, `EsMax` 10 GJ, feed cap 2 GW):**
  - A 4 kg crude charge (1 kg yield) at `0.5 R_s` from a mouth drains 0.25 GJ (2.5 %).
  - A 10 kg-yield charge at point blank empties a full, unfed reserve by the end of its ring-down. A prepared one (overcharged to 12.5 GJ, fed at the 2 GW cap) gains 10 GJ of feed during the 5 s ring-down, so it survives any single pulse up to 22.5 GJ (22.5 kg at point blank, or a 100 kg casing detonated more than 12 km from the mouth).
  - A 78,000 t capital ship entering drains 2.6 GJ (26 %). A fleet of 16 × 5,000 t through r = 150 m (`EsMax` 22.5 GJ) drains 12 %.
  - One battleship kit on Feed (2 GW, the cap) refills 20 % per second. A kit on Drain removes 1 GW (half efficiency), so two of them cancel it; more add nothing, because drains share the 2 GW cap. With the feed cancelled, a prepared reserve survives only pulses up to its 12.5 GJ: destabilisers and bombs together break a hold that either alone cannot.
  - In general a wormhole at `Ψ₀` with net feed `P_net` survives a pulse of drain `D` iff `D < Ψ₀ · EsMax + P_net · 5 s`: up to `EsMax` unfed and full, up to `2.25 · EsMax` prepared.

**Zero and early collapse.**
- When `Es` reaches 0 at the end of step 3 of tick `Tz`:
  - admissions are refused at both mouths from `Tz` (a bounce, no damage);
  - feeds stop counting. **Zero is final:** nothing in the next 60 ticks revives it;
  - if `Tz + 60 < TClose`: `WormholeCollapse {Tcs = Tz + 60, s_c, cause}` goes out (see Tunnels, Collapse flow, for `s_c`), and at `Tcs` the instant flush or the tunnel collapse flow runs;
  - otherwise no event is sent: the planned collapse runs at `TClose` with the planned `s_c`. Early `Tcs` is therefore always before `TClose`, which keeps `TEnd` a true bound.
- `TClose` never extends. Overcharge buys a buffer, not time. (A re-establish is a new wormhole with its own `TClose'`; see below.)

**Reconciled with the old fixed-`TClose` invariant** (everything that relied on it):
- **Admission:** the tail rule still uses `TClose`, which is the latest possible collapse. Nothing can promise a crosser that a drain won't come first. The autopilot warns in Critical.
- **Queues:** `MouthQueue` slots are still laid out against `TClose`. On `WormholeCollapse`, every slot after `Tz` is dropped and transit autopilots pull back to hold points outside `R_f`. By default the queue also holds while the band is Critical; a leader's **power-through** flag keeps it running.
- **Catch slots:** a booking holds its lane until the last mouth despawns (at most `TEnd`), then through the scar window, then frees. The fee is not refunded after an early collapse: the gate caught the end as booked.
- **Flush:** the instant flush runs at `Tcs`, whatever caused it.
- **Natural cycles:** the schedule never moves. An early collapse ends that cycle's window; the next cycle forms on time at its own `Ψ_nat`. A redirect lasts only its cycle, as before.
- **Bombs:** the mouth stall, its quarter-of-`OpenTicks` cap and its immunity window are **removed** (exotic-matter.md's "Wormhole disruption: the invariant is kept" is superseded). Pulses drain instead. The griefing limits are now the drain law (∝ yield and falloff), dampers, feeders, overcharge, and the cheap re-establish.
- **Cross-shard:** each shard applies drains for events on its side and forwards them to the wormhole's owner shard (A's), which integrates and announces. Cross-shard drains land one hand-off late (≤ 12 ticks), applied in `(tick, source)` order on arrival.

**Power through and re-establish** (decision A):
- **Power through:** keep `Ψ` up. Bring feeders to the cap, overcharge before a known attack, cover the mouths with gravity dampers where a station can carry them (they are station cores), and keep `Ψ · EsMax + 5 s` of feed above any single pulse's drain (pulses ring down over 5 s, so feed answers them). A fleet's queue keeps running in Critical with the leader's flag.
- **Re-establish:** after an early collapse (any cause), within the scar window:
  - **Who:** the original generator, or any generator of the owner's group, whose host is within `ProjectRange` of scar A, is not cooling down, and has `MaxRadius ≥ r`.
  - **Condition:** the remaining paid life `R = TClose − Tcs` is at least `MinOpenTicks`, and for a tunnel at least `2 · TunnelLead` (otherwise nothing could be admitted).
  - **Cost:** `X_re = 0.25 · X` (additives at the same mix, also at 0.25), and `E_re = 0.25 · (opening energy) + EsMax`. `X` is always the original plan's, so every re-establish in a chain pays a quarter of it, not a quarter of the last re-establish. The upkeep for `R` is already paid.
  - **Charge:** at once, no plan. `ChargeTicks_re = max(180, ceil(60 · E_re / ChargeRateW))`. Path, vias, class, `r`, `Depth`, β and flow bias are reused. Cancel rules are as for any charge.
  - **Commit:** both scars are re-checked against suppression and pushed if hard, within the quote. **No capture runs:** the path is already drilled (decided, round 5: former open question 20). Then 180 ticks of Forming, `TClose' = TOpen' + R` and `TEnd'` from it. The paid *life* is kept, not the wall-clock end, so `TClose'` lies after the original `TClose` by the downtime; no upkeep is charged for the downtime and none is refunded.
  - A tunnel re-forms from the same tunnel seed, so it has the same geometry.
  - **Example:** r = 100 m tunnel over 1 AU, 300 s open. The original cost 119 EM and 129.6 GJ. The re-establish costs 30 EM and 24.9 GJ: a 12.5 s charge plus 3 s of Forming.
- An unused scar lapses after 60 s and starts the generator's cooldown.

### Mouth pair model

**Frames:** each mouth is a `FrameKind.Mouth` frame. Its parent changes at most once in its life, at detach (Grid → SurfaceFixed or Rails).

| Mouth parent | Allowed when | Motion |
| --- | --- | --- |
| `BodySurface` | Altitude below `RotatingTop` (band rule) | Fixed local pose: hovers and co-rotates |
| `BodyInertial` / `Solar` | Above `RotatingTop` | **Rails only.** Circular by default (warp's drop rule), or the `MatchTarget` orbit with an along-track offset. Never at rest in an inertial frame: anything leaving it would fall in (the lesson from warp's "frame rest") |
| `Grid` | Mounted on the generator's own host (projector), on a consenting target grid, or on a gate catcher's host. Leashed (below) | Fixed in grid coordinates, outside the hull and its `GridVolume`. Moves with the host body |
| `Mouth` | Never | n/a |

- The reverse direction is allowed: a **wormhole frame** grid (natural pairs only, see Natural wormholes) has a `Mouth` frame as its parent. It is fixed in the mouth's frame and rides its rails or surface pose.

**Where A goes:**
- A is either host-mounted (`Grid`) or projected up to `ProjectRange` from the generator, in the frame the band rule gives.
- Generator hosts need `MinHostMass` (a big ship or a station).
- **Both ends must pass suppression.** The near end is a wormhole being created too.
- The host's own hull is exempt for its own mounted mouth, but the shell must still clear that hull by 2 m.

**Orientation:**
- Local `z` is the disc normal `n` (the front). `x` and `y` span the disc.
- Near mouth A: `n_A` defaults to the projector axis. For a projected point it faces back along the path, so a ship enters flying along the path.
- Far mouth B: `n_B` defaults to the path direction at B. For a gate catcher it is the arrival lane. For a managed catch slot it is the slot's facing. For a captured end it is the interdictor's choice, within the lane rule.
- Stored as 48-bit smallest-three (15 bits per component, X2).

**Size:**
- **Radius:** `r ≥ 1.25 · ρ_max + 10 m`, where `ρ_max` is the largest cross-section radius among the hulls meant to pass. Player pairs buy it at plan, 25–400 m. Cost scales with `r²`. Natural pairs are 150–1,200 m.
- **Shell depth:** `Depth ≥ λ_max + 10 m`, where `λ_max` is the largest half-length along the travel axis. At most 2 km. It costs nothing, but the shell volume must pass suppression and the solid test.
- Hulls wider than `r` hit the rim. Hulls longer than `Depth` hit the cap before their centre reaches the plane. There is no special case.
- **Flow radius** `R_f` is fixed by `r` alone, flowing or not: `min(8r, 2 km)` for player pairs, `min(8r, 3.5 km)` for natural pairs. It sizes the flow field, the collapse kick and the anchor radius.

**Throat shell:**
- **Shape:** a rim of 24 boxes (48 when `r > 400 m`, so no box is wider than ~160 m; inner faces on radius `r`, 2 m thick), 24 staves of length `Depth`, and an end cap. One refcounted compound per `(r, Depth)`.
- **Where it lives:** in every bubble that overlaps it, the way terrain statics do.
  - SurfaceFixed: static, at an exact offset.
  - Rails: kinematic, moved with `MoveKinematic` in step 5.
  - GridFixed: sub-shapes of the host's compound, with the host's mass properties kept (override). A ship-mounted mouth is therefore not an infinite-mass ram.
- **Creation at `TCommit`:** small bodies inside the shell or the disc cylinder (characters, items, missiles) are moved to the nearest outside point, with velocity kept. This uses our own sphere tests (P1). Hulls are already kept out by suppression. Natural pairs also deal with hulls (see Natural wormholes, Frames).
- **A mouth is a physics anchor** from `TCommit` until it despawns (at `TClose`, or when an early collapse reaches it), under zones' anchor rule, with anchor radius `A_m = max(2 km, R_f + 200 m)` for **every** mouth. Anything within `A_m` gets a Jolt body, and the shed distance becomes `A_m + 1 km` (zones' 3 km rule, shifted). Rails and frozen entities therefore never need an analytic crossing test, the flow field or the collapse kick, and nothing crosses without meeting the shell.

**Throat map (instant class; the tunnel maps reduce to `J` at zero length):**
- `J` is a rotation by π about local `y`: `J = diag(−1, 1, −1)`, quaternion `(0, 1, 0, 0)`.
- It is a proper rotation (det +1, no mirroring) and its own inverse, so B → A uses the same steps with A and B swapped.
- The front of A connects to the front of B. **Backs are not traversable;** they are the solid cap.

**Transfer through the portal (instant class).** These are zones' transfer equations into A, then `J` and the flow, then the inverse transfer out of B. `(o_X, R_X, u_X, ω_X)` is mouth X's pose in the bubble frame `F_X`. Body state is `p, v, w, q`.

```
1. into A:   p_a = R_Aᵀ (p − o_A)
             v_a = R_Aᵀ (v − u_A − ω_A × (p − o_A))
             w_a = R_Aᵀ (w − ω_A)          q_a = R_Aᵀ ∘ q
2. throat:   p_b = J p_a   v_b = J v_a   w_b = J w_a   q_b = J ∘ q_a
   flow:     v_b.z ← v_b.z + σ·ΔV          σ = +1 with the flow, −1 against, 0 when Φ = 0
             with:    v_b.z ← min(v_b.z, VCrossMax)
             against: if v_b.z < 1 m/s → flow reject (no transfer, see Flow)
3. out of B: p' = o_B + R_B p_b
             v' = u_B + ω_B × (R_B p_b) + R_B v_b
             w' = ω_B + R_B w_b            q' = R_B ∘ q_b
```

- **Poses are taken at the crossing instant `t` for both mouths,** together with the destination bubble's origin at `t`.
  - The frame cache keeps a previous-tick slot for this.
  - The placed state is the state at `t`, and the scene steps it to `t + 1` like every other body.
  - Using the poses current at step 3 of `t + 1` would place arrivals one tick of mouth motion off: 1.7 m for a grid mouth at 100 m/s, enough to land inside the shell wall or behind the plane.
- **Velocity relative to the mouth is kept exactly** when there is no flow.
  - A ship entering A in low orbit at 50 m/s relative leaves a hover gate at 50 m/s relative to that planet's surface. Wormholes cancel frame velocity differences, which is why gates are valuable.
  - The flow changes only the axial component, by a known `ΔV`.
- **Energy is not conserved across the pair.** This is accepted, but it makes closed loops possible.
  - In Portal's infinite fall, two hover mouths of one pair are stacked in one gravity well, and each pass gains `g·Δh`. Within a 20-minute open that reaches km/s, which is a kinetic weapon.
  - Flow adds `ΔV` per with-flow pass.
  - The crossing speed limit below and the with-flow exit clamp cap both.
- **Error:** one float ulp per transfer, ≤ 0.5 mm, as in zones. Mouth positions travel as int64 at 1/1024 m (C13).

**Crossing speed limit:** `VCrossMax`.
- Default 300 m/s relative to the entry mouth. A generator-class knob; for natural pairs it comes from the seed. The Unstable band scales it by 0.75 for instant pairs.
- Bodies entering faster bounce.
- A with-flow exit is clamped to `VCrossMax`.
- This caps every loop at a speed ships reach anyway, and keeps a crossing to ≤ 5 m per tick.

**Crossing test** (both classes: entering a tunnel is a crossing of the disc; tick step 8b, after the grid poses are written):
- **Candidates:**
  - A per-mouth list, rebuilt at 10 Hz alongside the membership checks.
  - It holds root entities in any bubble overlapping the shell with `d < r + λ + 0.2 s · |v_rel| + 50 m`, including characters and loose items in exterior scenes.
  - Contents of a grid are never tested. They go with their grid.
  - A second, wider ring per mouth covers `A_m` (`d < R_f + 1 s · |v_rel|`). It feeds the flow wakes and the collapse kick.
- **Test, in A-local coordinates, with A's pose at each instant.** States come from the frame-relative double `Placement` at `t − 1` and `t`, not from scene coordinates, so a rebase between the two ticks cannot fake a crossing.

```
c0 = local(A, t−1, p(t−1)),   c1 = local(A, t, p(t))
edge:   z0 > 0 ≥ z1,   f = z0 / (z0 − z1),   h = c0.xy + f (c1.xy − c0.xy),   accept if |h| ≤ r
state:  z1 ≤ 0 and |c1.xy| ≤ r          (centre already inside the shell, e.g. it stopped there)
```

- **Accuracy:** the endpoints are exact. A mouth rotating within a tick bends the relative path by its sagitta, about 7 mm for a mouth 200 m off the spin axis of a ship turning at 1 rad/s. That is well under the 2 m rim, but not "exact".
- **Transfer state:** the body is placed at `z_b = max(−z1, 1 cm)`, in front of B by its overshoot, with no lost tick.
- **Exclusions:**
  - `Warp` and `Throat` placements are intangible and never cross.
  - Explicit locks to bodies outside the carried subtree (tractor beams, clamps) break on crossing.
- **Order:**
  - Crossings queue for tick step 3 of the next tick.
  - **All departures run first, then arrivals,** in `(MouthId, EntityId)` order. Each arrival's lane test sees the arrivals placed before it in the batch.
  - Crossings are not announced in advance. If warp's `ArrivalBooking` (≤ 64 bodies per tick) is full, a grid goes into the throat hold; anything else bounces.
- **Bubble:** chosen by warp's T1 attach rule. Grids reset their proper-acceleration history at the transfer. Characters are recreated with up `= −ĝ` at the exit, where `ĝ` includes any flow.

**Two-way traffic (instant class; tunnels: see Tunnels, Lanes).**
- **Safety comes from the exit lane and the hold, not from the half-lanes.** An opposing body's nose inside the destination shell occupies the exit lane, so opposing ships never overlap. One of them waits.
- **Half-lanes:**
  - `J` maps `x → −x`. Entering traffic uses the `x > 0` half and emerges in the far mouth's `x < 0` half.
  - That separates two-way traffic only when `r ≥ 2ρ + 10 m` for both bodies.
  - Below that, the queue alternates direction in batches.
- **Queue slots have priority.** Unqueued crossings are held if they would push `BusyUntil` past the next `MouthQueue` slot. Followers still get through, but only in the gaps.
- **One-way pairs** (`ΔV ≥ VCrossMax`) have no two-way traffic at all. The closed direction queues nothing.

**Exit lane and throat hold** (the exit lane applies to both classes; the hold is instant-class only, because a tunnel's bore is its own hold).
- **Exit lane:** a capsule of radius `ρ` along `n_B`, from `−λ` (inside B's shell) to `λ + v_exit · 2 s`. `v_exit` is the speed after the flow term.
- **Test:** our own entity spheres and capsules, plus `ExitReservation`s and catch-slot rings. The same test as warp's commit, ~1 µs, and not a Jolt query (P1).
- **When the lane is occupied, or the direction's flux budget is busy** (`BusyUntil > t`), a grid goes to `PlacementMode.Throat`:
  - It has no Jolt body and is intangible. Its mouth-local state is held, and it is drawn as having entered.
  - Its grid bubble keeps stepping, with `GridInertial` set.
  - It exits when the lane is clear, or after at most `ThroatHoldMax` (5 s). In that case it slides along the lane by 2× its sphere radius, up to 8 steps, as warp does.
  - At the collapse tick at the latest it is flushed out of B (see Collapse flush). This includes cross-shard holds.
  - Characters and loose items never take a hold place; they bounce instead. Hold places are only for grids, so a stream of cheap crates cannot fill the pool.
- **Admission** to the hold requires:
  - the tail to clear B before `TClose − 60`, i.e. `t_exit + 60·(λ / v_z) + 60 ≤ TClose`;
  - room in the hold pool (16 per direction).
- **Bounce:** a crossing is bounced when it is not admitted, arrives faster than `VCrossMax`, happens while the mouth is Forming, or would take the stability reserve to zero (see Stability).
  - `v_a.z ← −0.5·v_a.z`. The body is placed at `z_a = λ + 1 m` in front of A, after a sphere test on A's side, sliding along `+n_A` as warp does.
  - It goes through `SetPositionRotationAndVelocity`.
  - The proper-acceleration history is reset.
  - **Damage** (an impact of `m·1.5·|v_z|`) is reported only for the crosser's own faults: entering faster than `VCrossMax`, or during Forming. Bounces for failed admission (hold full, tail rule, arrival budget, stability) do no damage, so jamming a throat cannot be used to damage other traffic.
- **Flow reject:** an against-flow crossing whose exit speed would fall below 1 m/s.
  - It uses the bounce placement, with `v_a.z ← max(ΔV − |v_a.z|, 1 m/s)` outward.
  - There is **no impact damage**, because the flow is a uniform body force and stresses nothing.
  - It spends no flux.
- **Flux:** after each crossing, `BusyUntil_dir = max(BusyUntil_dir, t) + ceil(60·m/F)`. This is the throat's mass-throughput limit. When half-lanes don't fit, both directions share one budget.
  - **Per-entry cap:** an entry whose own debt `ceil(60·m/F)` exceeds `MaxEntryFluxTicks` (7,200, 2 min) bounces as an admission failure (no damage). Without it one pushed asteroid (a 72 m rock is ~4 Mt) would block a direction of an r = 100 m pair for an hour and drain 13 reserves in one tick. A 78,000 t capital ship through r = 100 m owes 78 s and passes. The plan shows the largest admissible mass, `F · 120 s`.

**Contact continuity at the mouth.**
- **v1: the shell.**
  - A straddling ship collides only in its own scene until its centre crosses.
  - Its leading part sits inside A's shell, where nothing else can be except traffic entering through the same disc, which is serialised by the lanes or by alternation.
  - The exit lane keeps the far side clear for the ship's length.
  - The part already through is a render-only ghost, clipped at the plane. There are no cross-mouth contacts.
- **v2: throat sleeve** (optional).
  - Up to 64 kinematic proxies per scene, mapped through `J`, with contacts filtered by side of the plane in `OnContactValidate`.
  - **Probe the export first.** If it is missing, stay on v1.

**Collapse flush** (instant class; tick step 3 of the collapse tick, before anything else in step 3; it replaces severance). In this flush, `TClose` means the collapse tick `Tcs`: the planned `TClose`, or `Tz + 60` after an early collapse (see Stability). Tunnels collapse by the collapse flow (see Tunnels); each tunnel mouth runs steps 1–3 below on the tick its collapse front reaches it:

At `TClose` the throat flushes out **both ways**, whatever its flow. All states used are those at the start of step 3 of `TClose` (the end of `TClose − 1`), and everything runs in `(MouthId, EntityId)` order. The flush has three steps.

**1. Anything partway through leaves whole by the nearer mouth.** The nearer mouth is the one on the side of the body's centre, in throat terms:

| Body | Detected by | Leaves by |
| --- | --- | --- |
| Straddler not yet crossed (centre in front of A, nose in A's shell) | Hull-occupancy test of the shell volume, run only on bodies whose bounding sphere meets the shell. A bounding sphere merely touching the disc cylinder is not enough, so a ship parked beside the rim is never moved. Characters and items use the centre rule | A |
| Straddler already crossed (centre in front of B, tail still in B's shell) | Same test on B's shell | B |
| Held in the throat (`PlacementMode.Throat`), including cross-shard holds | Hold pool | B |
| Crossing detected in step 8b of `TClose − 1` | Crossing queue | A. It can never be admitted (its tail cannot clear by `TClose − 60`), so it is treated as an uncrossed straddler: no transfer, no flow term, no bounce damage. A one-way flow therefore cannot be bypassed on the last tick |

- **Placement:**
  - The body moves along `+n` of its mouth by the depth of its deepest cell inside the shell, plus 1 m. Held bodies are placed at `z = λ + 1 m` in front of B through the transfer equations, without the flow term.
  - Each placement gets a sphere test and slides along `n` by 2× its radius, up to 8 steps, then tries twice the distance, as warp does. This is a forced slide: it always places.
  - The axial velocity is set outward (`v_z ← |v_z|`), the rest is kept, and the proper-acceleration history is reset.
- **Budget:** flush placements are outside the 64-per-tick arrival budget. They are bounded by the hold pool (16 per direction) plus the straddlers, of which there are a handful.
- **No hull is cut.** There is no kerf and no split, so the collapse does not depend on hull damage. Crew stay inside the body they are in.

**2. Outward kick from both mouths.**
- Every simulated root body whose centre is within `R_flush = R_f` of either mouth's centre gets `Δv = V_flush · (1 − d / R_flush)² · d̂`, outward from that mouth's centre. This covers both sides of the disc and includes the bodies just placed. A body in range of both mouths (a stacked pair) gets both contributions, summed.
- The anchor radius `A_m ≥ R_f + 200 m` guarantees every body in range has a Jolt body. Bodies in other bubbles are kicked in their own scene; `Warp` placements are skipped.
- `V_flush = 30 m/s · r̂^0.5`, capped at 80 m/s. Natural pairs take it from the seed.
- It is applied once through `SetLinearVelocity`. That wakes the body, which is fine on a single fixed tick.
- It acts like gravity, the same Δv for every mass element, so it does no damage by itself. Whatever a body hits afterwards is an ordinary impact.
- **Grid contents go with their grid, and interiors feel nothing.** A kicked grid has its proper-acceleration history reset, as for warp's velocity snaps. Without that, 80 m/s in one tick would reach an unplated interior as a ~490 g spike.
- Wormhole frames, static grids and the mouth host's own compound are skipped.
- For player pairs `R_f ≤ 2 km`, inside the 2.5 km ship exclusion radius that a hostile mouth must keep. A hostile collapse therefore cannot kick a non-consenting grid that stayed where it was.

**3. Despawn.** The shells and mouths despawn, and their suppression and flow end. Any flow stops at `Tcs` (a tunnel's outer field too: from `Tcs` the collapse flow inside drives the exits, and each mouth's flush is the kick); the flush overrides it. Scene gravity in every bubble that listed the mouth is re-set on the same tick (see Flow).

- **Predictable:** `TClose`, `V_flush` and `R_flush` are known to everyone from commit (for natural pairs, from the record), so clients predict the kick. An early collapse is announced by `WormholeCollapse` 60 ticks ahead.
- Admission never lets a tail clear after `TClose − 60`. The flush therefore only catches bodies that stalled, were held, or ignored the 600-tick Closing warning or the 60-tick early-collapse notice, plus anything drifting near the mouths.
- **Cross-shard:** each shard flushes its own mouth.
  - A held blob that has not reached the B shard by `Tcs` (B's front tick, for a tunnel) is created by the B shard when it arrives, at B's flush placement and with B's kick.
  - B's frame (no shell, no portal) stays in the frame tree until every outstanding blob has landed, so the placement uses B's pose at the landing tick. This holds however late the blob is; shards may lag by seconds.
  - The A shard never re-creates it.

**Leash (grid mouths):**
- In step 8b, a grid-fixed mouth **detaches at its current pose** if its host has carried it into hard occupancy or natural suppression. That covers:
  - an exclusion zone without permission;
  - an atmosphere;
  - a corona;
  - a body core.

  The test is on the shell's bounding sphere and costs ~0.3 µs.
- It becomes SurfaceFixed below `RotatingTop`, or rails on a circular orbit at its current radius above it.
- Host destruction triggers the same detach. The wormhole still lasts to its collapse, with its flow unchanged. A detached mouth's host feeds it only while it stays within feed range.
- The leash stops a host from flying a mouth into an enemy hangar, an atmosphere or a planet after commit.
- A host is never pulled by its own mounted mouth's flow. The shell is part of its compound, and the field skips the host's compound.

**Flow** (replaces "Forces near a mouth: none"):

A pair has a flow `Φ(t) ∈ [−1, 1]`. Positive means A → B: A is the **inflow** mouth and B the **outflow** mouth. With `Φ = 0` nothing below applies: there is no gravity leak, tidal pull or drag, and lensing, shimmer and audio stay cosmetic. Bodies feel the gravity of whichever side their centre is on.

1. **Field** (acts like gravity: the same acceleration for every mass, no stress, no wake force).
   - Within `R_f` of each mouth's disc centre, **on both sides of the plane**, the inflow mouth pulls toward its disc centre and the outflow mouth pushes away:
     ```
     a(p) = ∓ A_f · r² / (r² + d²) · (1 − d²/R_f²)² · d̂        d = p − o,  A_f = |Φ| · A_fmax
     ```
   - It is a central field, so it is conservative: no closed path gains energy. (A front-half-space-only field would not be: a guided loop in through the front and out behind the cap would gain up to `W` per lap.) Behind the cap the pull simply holds bodies against the solid back.
   - The potential depth is `W ≤ 1.45 · A_f · r` (the bound is `atan(8)`; the `(1 − d²/R_f²)²` taper makes it smaller).
   - Generation (natural) and generator caps (player) keep `sqrt(2W) ≤ VCrossMax / 2`, so a body falling from rest never reaches `VCrossMax` on the field alone, and drifting debris is never turned into damaging bounces.
   - **Fields never overlap.** The mouth suppression term keeps flowing mouths at least `R_f,m + R_f` apart, so the bound holds for every point, not just per mouth.
   - It is evaluated with the mouth pose of the current tick (rails and fixed mouths: step 2; grid mouths: step 8 of the previous tick, as for every grid pose), transformed into the bubble's axes. An acceleration only rotates between frames, so rotating surface frames and spinning hosts need nothing extra.
2. **Throat (instant class):** at transfer, the axial exit speed changes by `σ·ΔV`, with `ΔV = |Φ| · 1.2 · VCrossMax`.
   - With the flow, the exit is clamped to `VCrossMax`. Against it, a reject is possible (see Exit lane and throat hold).
3. **One-way (instant class; in a tunnel one-way emerges from the current, see Tunnels):** every admitted entry is at most `VCrossMax`.
   - Against-flow crossing is therefore **impossible** when `ΔV ≥ VCrossMax`, i.e. `|Φ| ≥ 0.83`.
   - Below that, it needs an entry speed above `ΔV`, plus the thrust to push in through the outflow field.
   - A body that comes out of the inflow mouth slower than `sqrt(2W)` is pulled back in and recrosses with the flow. That is the "to some degree" of one-way, and it is plain physics with no special case.

**Application** (zones' gravity scheme, C2; never `AddForce`, which defaults to `Activate` and would wake every body every tick):
- Each bubble keeps a list of flowing mouths whose `R_f` sphere overlaps it, at most 8, refreshed with the 10 Hz candidate lists.
- **Scene gravity** = zones' term + `Σ a_flow(origin)`. The flow part is stored as `OriginAccel` when it is set.
  - It is re-set **on the same tick** when the list changes (onset at `TOpen`, collapse at `Tcs`, a mouth entering or leaving the list).
  - Otherwise it is re-set when it drifts by more than 1 %, at most at 1 Hz; natural flow drifts over its cycle, and moving mouths move.
- **Per awake body,** the force pass adds `m · (Σ a_flow(p) − OriginAccel)`, against the **stored** value, in the same write as the gravity delta it already makes. Awake bodies are therefore exact whatever the throttle. That costs ~10 ns per body per flowing mouth.
- **Sleeping bodies** (never woken by the field itself):
  - At flow onset (`TOpen`), bodies inside `R_f` are woken once from the wide candidate ring.
  - Per mouth, `PhiAtWake` holds `Φ` at its last wake. When `|Φ − PhiAtWake| > 0.1` (growth, decay or a reversal through zero), bodies inside `R_f` are woken once and `PhiAtWake` is updated.
  - A body that newly enters `R_f` at the 10 Hz refresh is woken once. Sleeping bodies cannot move, so this only happens when a rails or grid mouth sweeps its field over them.
  - A body accelerating under the field never meets the sleep criterion again. One resting in contact may sleep, as under gravity.
- **Anchor radius** for every mouth is `A_m = max(2 km, R_f + 200 m)` (above), so rails and frozen entities never need the field.
- **Grid interiors** feel the flow exactly as they feel gravity: interior gravity is real gravity *including the flow* minus the host's proper acceleration, and the proper acceleration excludes the flow. A host falling under the flow is weightless inside.
- **Characters** include the flow in `ĝ`, for `CharacterVirtual` up and for EVA. In a free-fall bubble an EVA capsule feels it as a force like gravity.

**Natural flow:**
- `Φ(t) = clamp(Φ0_k + Φ1_k · sin(2π (t − openStart_k) / openLen_k), −1, 1)`, with both terms from `hash(pairSeed, k)`. `A_fmax` (up to 20 m/s², and at most `VCrossMax² / (8 · 1.45 · r)`) comes from the seed.
- The flow can reverse partway through a cycle.
- Clients evaluate it from the record.

**Player flow:** fixed at commit and announced in `WormholeCommit`.
```
Φ = clamp(Φ_gen + Φ_gate + Φ_asym, −1, 1)
Φ_asym = 0.5 · max(0, P_B − P_A) / (P_A + P_B)        0 for a free B
```
- `P_A` is the generator's `ChargeRateW`. `P_B` is the `HoldPowerW` of the catcher holding B (a gate, or a capturing interdictor): a dial on the catcher, capped by its host, paid **at commit** from the catcher's capacitor as `HoldPowerW · OpenTicks / 60` joules. A catcher that cannot pay holds with `P_B = 0`.
- **Space flows toward the stronger end,** and only a catcher that out-powers the generator creates an asymmetry. A gate with no hold power adds no flow. (Comparing the generator's 2 GW with a gate's 5 MW upkeep would have made every gated wormhole flow *away* from the gate at `|Φ| ≈ 0.5`.)
- `Φ_gen` is the owner's bought bias, quoted at plan, costing ×`(1 + 3Φ_gen²)` on X.
- `Φ_gate` is the gate's own policy bias (for example an inbound-only customs gate), paid from the gate's exotic matter at each catch. It applies **only** to wormholes that targeted that gate, never to captures, so an interdictor cannot make a victim's wormhole one-way into its trap by policy; it has to out-power the generator.
- For a capture, the asymmetry is only known at commit, together with the capture itself.
- `A_fmax = 2 m/s²` for player pairs.

### Tunnels

Tunnel class (decision D) and every natural pair. The mouths, shells, crossing test at the disc, outer flow field and leash are as above. Behind each disc is the tunnel.

**The tunnel frame.**
- `FrameKind.Tunnel`: a straight bore along its z axis, from A's disc at `s = 0` to B's disc at `s = L_t`, nominal radius `r`.
- **Length** is compressed: `L_t = clamp(LtK · ℓ · m_L, 2 km, 60 km)`, with `LtK = 2 km`. That is 6 km for 20,000 km, 24 km for 1 AU and 27 km for 5 AU. Natural pairs: seeded, 5–40 km.
- **Not in space:** the frame tree holds it as a root beside `Solar`, and `Relative` is refused across it, as the portal map is not a tree edge. It has no gravity source, rails, terrain or suppression. No mouth, catch point, warp exit or anchor can be placed in it, and generators, catchers and warp drives cannot plot from inside it.
- **Ends:** the bore narrows to exactly `r` over the last 200 m at each end, so ships line up with the disc. Outside the disc radius at an end is wall.
- Created at `TCommit` with the mouths, empty. Despawned with the last mouth.

**Bubbles inside.**
- Bubbles anchor as `BubbleAnchor.Tunnel`: axes are the tunnel's axes, gravity is the wells' term (below), and they follow zones' merge (500 m), split (1,500 m) and size limits (4 km hard). The origin moves with its cluster along z, with free-fall's rebase rule (offset > 1 km or mean velocity > 50 m/s gives a Galilean boost).
- **Every root entity in a tunnel is simulated.** There are no rails or frozen placements in a tunnel; the tunnel is a physics anchor along its whole length for its own occupants.
- **Nothing sleeps in a tunnel.** Occupants are created with sleeping disabled (`AllowSleeping = false`; probe the export, and if it is missing, wake every occupant once on each `u_q` change away from 0, each `|Φ − PhiAtWake| > 0.1` and at `Tcs`). Otherwise a body asleep in a calm bore would ignore the eddies, wells and current that appear when the band drops, because the coupling and well deltas are applied without activation (C2). The cost is bounded by the caps below.
- **Tunnel-frame velocity:** the current coupling, wall test, exit test and ejection use positions and velocities in the **tunnel frame**: the scene value plus the bubble origin's offset and velocity, which a Galilean rebase changes. Using scene velocities after a boost would make a body riding the current at 240 m/s feel drag toward `u − 240`.
- **Caps:** `TunnelCap` = 128 grids plus 512 small entities (characters, items, missiles, debris). An entry beyond the cap bounces as an admission failure, with no damage.
  - **Per group:** at most 32 grids per owner group, so one group cannot fill the bore with cheap drones and deny it.
  - **Small overflow:** when debris or items created inside (wrecks, fire) push smalls past the cap, the oldest uncrewed small entities are absorbed by the wall (despawned). Characters are never absorbed. Junk can therefore never keep traffic out.
  - **Server-wide:** at most 4,096 tunnel occupants per server, so never-sleeping occupants cost at most ~4,096 awake bodies.
- The wall is **not** Jolt geometry. It is the analytic wall test below.

**Entry and exit maps.** `p_a`, `v_a` are mouth-local, from step 1 of the portal transfer; the result goes through step 3 at the other end. Orientation and angular velocity map with the same rotations.

```
enter at A:   p_t = (−x_a, y_a, −z_a)           v_t = (−v_a.x, v_a.y, −v_a.z)      (π about y)
exit at B:    p_b = (x_t, y_t, z_t − L_t)        v_b = v_t                          (when z_t > L_t)
enter at B:   p_t = (x_b, y_b, L_t + z_b)        v_t = v_b
exit at A:    p_a = (−x_t, y_t, −z_t)            v_a = (−v_t.x, v_t.y, −v_t.z)      (when z_t < 0)
```

- With `L_t = 0`, A → B composes to exactly `J`, so instant and tunnel share `PortalMap`.
- **Entry** uses the disc crossing test as for instant pairs, with A's pose at the crossing instant. The body is placed in a tunnel bubble at `s` = its overshoot.
- **Exit** is the same edge test in tunnel coordinates (`z_t` crossing `L_t` or 0 with `|xy| ≤ r`), in step 8b, about 10 ns per occupant.
- **Speed:** an entry faster than `VCrossMax` bounces, as for instant pairs. An exit faster than `VCrossMax` is clamped to it, and the excess counts as an impact `m · (v − VCrossMax)`: the crosser's fault. Collapse exits are clamped with no damage.
- **Exit lane:** tested at the exit as for instant pairs. If it is occupied, the disc refuses: the body is reflected into the bore (`v_z ← −0.5 v_z`, no damage), and the autopilot holds at `L_t − (λ + 200 m)`. **The bore is the hold.** Tunnel traffic never takes a `Throat` placement except for a cross-shard hand-off.
- **Flux:** `BusyUntil` per entry direction and the per-entry cap `MaxEntryFluxTicks`, as for instant pairs. Each entry drains `e_m · m` from the reserve; an entry that would take the reserve to zero is refused (a bounce, no damage).
- **Admission** closes at `TClose − TunnelLead`, where `TunnelLead = ceil(60 · L_t / V_adm) + 60` ticks and `V_adm = 200 m/s`, cruise control's default speed (121 s for a 24 km bore). A ship whose average is slower than that is still inside at the planned collapse, at its own risk; the planned `s_c` is public, so it knows where the front starts.
  - **Open time:** the plan refuses a tunnel with `OpenTicks < 2 · TunnelLead`, a reason the client can compute. (With the old 100 m/s rule a 120 s, 1 AU tunnel had a 239 s lead, so admission would never have opened.)
- The carried subtree goes along, explicit locks break, and the proper-acceleration history resets at entry and exit. Characters may EVA in a tunnel.

**Lanes.**
- **Half-lanes:** traffic entering A at `x_a > 0` flies at `x_t < 0` and emerges in B's `x < 0` half; B → A traffic flies at `x_t > 0`. This holds when the bore's narrowest radius is at least `2ρ + 10 m`.
- Otherwise the tunnel's shard grants **direction windows**: one direction at a time, and a window closes when its last member exits.
- Collisions in a tunnel are ordinary physics.

**The current** (decision D: flow becomes a current).
- The axial current is `u_z = Φ(t) · U_t`, with `U_t = 0.8 · VCrossMax` (240 m/s by default). `Φ` is the pair's flow, as in the Flow section. The throat's `ΔV` term does not apply to tunnels; the mouths' outer field in normal space does.
- **Coupling:** every body is dragged toward the local current `u(p, t)` (the axial current plus the eddies below):
  ```
  a_i = κ_i · (u(p, t) − v_i)        κ_i = c_t · A_⊥,i / m_i,   c_t = 100 kg/(m²·s),   κ ≤ 1 s⁻¹
  ```
  - `A_⊥ = π ρ_⊥²`, from the hull bake's cross-section radius (the one used for mouth sizing). `κ` is cached per body and refreshed on mass change.
  - It is a surface force, applied in the force pass to awake bodies as `m · a_i` without activation. Interiors feel it as proper acceleration: the inertial dampener cancels it up to its rating (frames' `a_man`; exotic-matter's `a_d`) and leaks the excess; plating does not (it only adds floor gravity); undamped rooms feel it.
  - Examples: a C-class ship of 360 t with ρ 12 m has `κ` = 0.126 s⁻¹ (it matches the current in about 8 s). A 78,000 t capital ship with ρ 72 m has 0.021 s⁻¹ (about 48 s).
- **Ride or fight it:** the terminal axial speed under thrust `a` is `u + a/κ` with the current, and `a/κ − |Φ|·U_t` against it.
  - When `a/κ < |Φ|·U_t + 10 m/s`, the ship cannot make headway against the current. **One-way emerges per ship**, physically, with no rule.
  - A 3 g C-class ship (`a/κ` = 233 m/s) is shut out at `|Φ| > 0.93`; a 0.3 g capital ship (`a/κ` = 140 m/s) at `|Φ| > 0.54`.
  - The queue's `FlowClosed` becomes per ship: a member whose terminal speed against the current is below 10 m/s is refused its queue slot.
- With the current, ships can exceed `VCrossMax` mid-bore and must brake before the exit. The autopilot does.
- The coupling also damps sideways drift in a calm bore.

**Interior field** (decision E: stability shapes the tunnel). Inputs: the tunnel seed `TSeed` (player: `hash(WormholeId, TCommit, generator entity)`; natural: `hash(pairSeed, k)`), the tick, and `u_q`, the instability `u` quantised to 1/255 from the integer reserve. All of it is pure double maths, evaluated identically on the server and on clients. There is **no network traffic** for it.

```
u          = clamp((Ψ_s − Ψ) / Ψ_s, 0, 1)                        0 in a stable bore
taper      e(s) = smoothstep(0, 500 m, s) · smoothstep(0, 500 m, L_t − s)   0 at both ends
twist      δ(s, t) = e(s) · u · A_tw · Σ_{k=1..3} w_k · (cos(2π s/λ_k + Ω_k t + φ_k),  sin(2π s/λ'_k + Ω'_k t + φ'_k))
radius     R_t(s, t) = r · (1 − 0.15 · e(s) · u · n(s / 300 m, t / 20 s))            n: value noise in [0, 1]
wells      a_w(p, t) = −Σ_{j: h_j < u} A_j · W(|p − c_j(t)| / ε_j) · (p − c_j) / |p − c_j|
eddies     u_e(p, t) = e(s) · u · U_e · ( ζ(s, t) · ẑ × (p_⊥ − δ) / R_t  +  0.3 · γ(s, t) · ẑ )
storms     cell active  ⇔  hash(TSeed, cell, ⌊t / 60⌋) < max(0, 1.9 · (u − 0.58))
```

- **Ends:** the taper `e(s)` straightens and calms the last 500 m at each end, and no well slot lies within 500 m + 6ε of an end. Without it a 400 m twist at `s = L_t` would put the bore beside the disc, and a well at the end could push an innocent exit past `VCrossMax` (impact damage counted as the crosser's fault).
- **Determinism:** `Math.Sin`, `Math.Cos` and `Math.Exp` call the C runtime and are not bit-identical across platforms. The field (and natural `Φ(t)`) uses our own polynomial `DetMath` sin/cos and integer-lattice value noise, so "bit for bit" holds between Windows and Linux servers and clients. The server stays authoritative either way.

- **Twist:** `A_tw = min(2r, 400 m)`, wavelengths `λ` seeded in 1.5–6 km, and `Ω` chosen so the wall never moves sideways faster than 15 m/s. Following a 300 m twist of 2 km wavelength takes about 1 g of lateral thrust at 60 m/s and 12 g at 200 m/s, so **an unstable tunnel forces slower travel**.
- **Radius:** at least `0.85 r ≥ 1.06 ρ + 8.5 m`, so the design ship always fits, but not always on its centreline.
- **Wells:** one seeded slot every 500 m along `s`, with strength `h_j ∈ [0, 1]`. A slot's well exists while `h_j < u`, so wells appear one by one as stability falls, with no jumps. Peak `A_j` up to 30 m/s², softening `ε_j = 0.3 r`, `W` normalised to a peak of 1 and cut beyond 6 `ε`. Wells drift along `s` at 5–20 m/s within their slot, sit within `0.6 R_t` of the centreline, and pulse with a 20–60 s period.
- **Eddies:** `ζ` and `γ` are value noise in [−1, 1] over 300 m cells and 10 s, cubic-smoothed. `U_e` = 80 m/s. They act through the coupling, so light ships are thrown about more than heavy ones.
- **Storms:** 500 m cells, active per 1 s window. They start exactly at Critical (`u ≥ 0.58`, i.e. `Ψ < 0.25` with `Ψ_s` = 0.6); at `u = 1`, 80 % of cells are active. A body inside an active cell takes a discharge when `hash(TSeed, entity, ⌊t / 30⌋) mod 100 < 25`. Clients draw the cells from the seed; damage arrives as ordinary damage.
- **A calm bore** (`u = 0`) has `δ = 0`, `R_t = r`, no wells, no eddies and no storms: a straight cylinder with the axial current only.
- **Application** (zones' scheme, C2):
  - Wells act like gravity, the same acceleration on every mass. Each tunnel bubble's scene gravity is `a_w(origin)`, stored as `OriginAccel`. It is re-set on the same tick `u_q` changes, otherwise on more than 1 % drift, at most at 1 Hz. Each awake body gets `m · (a_w(p) − OriginAccel)`. Never `AddForce` with activation.
  - Nothing sleeps in a tunnel at all (see Bubbles inside), so a well or eddy that appears under a resting body always acts on it.
  - **Tidal damage:** at 1 Hz, `|∇a_w| · L_hull` above `TidalLimit` (data, 2 m/s²) adds damage brushes. A powered tidal compensator cancels it: it acts inside the hull, so it is not a projection.
  - Eddies go through the current's coupling term.
- **Cost:** a binary search of well slots by `s` (≤ 3 in range), 6 trig calls for the twist, and two noise lookups. About 100 ns per awake body per tick.

**Cruise control** (stable bores).
- `TunnelCruise` holds the half-lane centreline (or the axis when there are no half-lanes), accelerates to the pilot's cruise speed (default 200 m/s, limited by thrust and current), brakes to `0.8 · VCrossMax` before the far end, and crosses.
- It is a PD controller on lateral error and speed error, about 50 ns per ship per tick. It runs on the server for AI ships and in the owning client's prediction.
- **Available only in the Stable band.** On a drop to Unstable it warns for 3 s, then hands back control, holding the current heading. An **assist** mode keeps tracking `δ(s, t)` within the ship's thrust, but promises nothing.
- Queues and admission use its transit estimate, `L_t / v_c` plus the acceleration terms.

**Wall contact and discharge.**
- **Test** (step 8b, every occupant): `d = |p_⊥ − δ(s, t)|`; contact when `d + ρ_⊥ ≥ R_t(s, t)`. v1 uses the bounding cylinder; v2 may use the hull's support function. The closing speed is `v_r = (v − ∂δ/∂t) · n̂`, with `n̂` the outward normal.
- **Graze** (`v_r < V_ram`):
  - The wall pushes back: the radial velocity is reflected with restitution 0.3, through `SetLinearVelocity`.
  - **Discharge:** damage `DisK · (0.2 + u) · (1 + v_r / V_ram)` as a damage brush at the contact point (ship-hulls' damage layer). One component within 20 m of the contact, chosen by `hash(TSeed, entity, tick)`, goes offline for `2 s + 8 s · u`.
  - At most one discharge per body per 30 ticks. In a stable bore the damage is small (`0.2 · DisK`), but it is there.
- **Ram** (`v_r ≥ V_ram`, with `V_ram = 20 m/s · (0.5 + 0.5 · min(Ψ, 1))`, so 10 m/s at zero), or contact held for more than 2 s (pinned by a well): the body is **forced out into normal space** (below).
- **Who can be ejected:** grids and characters only. An EVA character can be thrown out mid-path, which is usually deadly. Loose items, debris and kinetic rounds reflect (see Combat) and are absorbed after 3 reflections; missiles detonate on contact. Otherwise a turret firing into the wall, or a crew throwing crates at it, would deliver rounds, cargo or warheads to any point along the path, past every defence there.
- **During the collapse** (from `Tcs`) the wall is frozen and any contact, graze or ram, is a collapse ejection: a discharge, no impact.

**Ram ejection: tunnel arc to the planned path.**
- **Point:** the arc fraction is `f = clamp(z_t / L_t, 0, 1)`. The path polyline is evaluated at the ejection tick, from A's current pose through the vias (fixed in `Lca` at `TOpen`) to B's current pose; `P(f)` is the point at fraction `f` of its length and `ê(f)` the tangent there.
  - **Cross-system pairs** (natural only; the mouths sit under different `Solar` roots, so there is no `Lca`): the path is two **legs**, one per system. Each leg runs from its mouth's current pose, behind the disc (along `−n`), to a tip fixed in that system's root at `TNC_k`, `L_leg` away (seeded per pair, 0.05–1 AU), core-detoured as usual. `f ≤ 0.5` maps onto A's leg (A at 0, its tip at 0.5) and `f > 0.5` onto B's (its tip at 0.5, B at 1). Axes are carried within each leg. A ram therefore always lands inside one of the two systems, never in interstellar space, and still at a point known from the record. Same cost as one polyline.
- **Axes:** `R_path(f)` starts as A's axes under the entry map (so the tunnel's z is A's `−n`) and is carried along the polyline by the minimal rotation between successive segment tangents. Deterministic, about 1 µs.
- **Position:** `p' = P(f) + R_path(f) · (x_t, y_t, 0)`. The lateral offset is kept, so ships side by side stay separated. Along the path the bore is compressed by `L / L_t` (6.3 million for 1 AU), so two ships 50 m apart along `s` land 300,000 km apart, and `f` is taken at the body's centre only.
- **Velocity:** `v' = v_drop(P(f)) + k_ej · R_path(f) · v_t`, with `k_ej = 0.5` (the rest is lost to the wall), the second term clamped to `VCrossMax`. `v_drop` is warp's **drop rule** (a circular orbit about the dominant body at `P(f)`, prograde with respect to the path tangent; circular heliocentric in `Solar`), the same rule warp uses for aborts. The earlier `(1 − f) · u_A + f · u_B` interpolation in `Lca` left a ship ejected 3,000 km from a planet nearly at rest in its inertial frame (warp's "frame rest" fall) and mixed in km/s of the far body's motion. Deadly places stay deadly through the position, not through an arbitrary velocity.
- **Frame:** the band rule at `P(f)` (a surface frame inside a rotating band, a body's inertial frame inside its SOI, otherwise `Solar`), then a transfer as for a warp exit. The proper-acceleration history resets.
- **Suppression:** natural terms are **ignored**. An ejection can land in a corona, an atmosphere, a forecast flare cone or a tidal zone; that is the danger. Occupancy terms are **applied** with warp's exit push: never into solid geometry or terrain, never inside a grid's exclusion radius without permission (so a tunnel can't insert a raid into a base), and a slide off warp exits, mouths and catch slots.
- **Damage:** a ram is an impact `m · max(v_r, V_ram)` at the contact point plus a full discharge at `u = 1` strength, whatever the band, so the wall is never a cheap door. A collapse ejection is a discharge only.
- **Ejection lockout:** a ram-ejected grid cannot spool a warp drive or start a wormhole charge for `EjectLockoutTicks` (1,800, 30 s). A deliberate ram is still a bail-out (decided, round 5: former open question 21), but not a free hop that skips interdiction fields and then warps on; and a cheap tunnel cannot be farmed as a line of free warp exits.
- **Budget:** warp's `ArrivalBooking` (64 per tick). If it is full, the body waits intangible in `Throat` placement for up to 8 ticks; after that it is placed outside the budget with the forced slide, as flush placements are, so an overtaken column always leaves.
- **Cross-shard:** the shard that owns the landing frame receives a blob, as for a warp exit.
- The reserve is drained by `e_m · m`.
- **Accepted:** a deliberate ram is a bail-out to a known point along the path, paid for in damage (decided, round 5: former open question 21).

**Combat in a tunnel** (decision F).
- Ordinary weapons work.
- **Kinetic rounds** feel the field: the wells (gravity-like), and the current through their own `κ` (data, about 0.05 s⁻¹: barely deflected in a calm bore, thrown about in turbulence).
  - At the wall a round **reflects** specularly with restitution 0.6 on its whole speed, plus a scatter of `u · 15°` from `hash(TSeed, round, tick)`. It is absorbed after 3 reflections and is never ejected. Ricochets can hit the shooter.
  - A round that reaches an end within the disc radius exits through the disc like any small body (with its exit clamp), as rounds already cross instant mouths. A tunnel is no better a gun barrel than an instant pair: rounds slow toward the current (`κ` 0.05 s⁻¹, 20 s) and the bore is long.
  - Missiles behave as ships (their own `κ` and thrust) but detonate on wall contact instead of being ejected.
  - Beams are straight and stop at the wall (v1).
- **Retaliation:** the wormhole strikes anything projecting gravitons or anti-gravitons **outside its own hull** while the device's host is **in the tunnel frame**. Devices in normal space (a damper covering a mouth, a destabiliser at range) are never struck.
  - **Triggers:** an active tractor beam (requested or not: the wormhole reacts to the projection, not to consent, so a gate cannot tractor a ship through a tunnel even on request; round 5, D26); an armed anti-graviton charge; gravity weapons, a raised gravity shield lobe among them (combat-and-sensors.md, D55); a gravity damper (it projects up to 5 km); a stabiliser in mode Drain; an interdiction emitter; a catcher.
  - **Exempt** (they act inside the hull): gravity plating, inertial dampening, tidal compensators and mag boots. A stabiliser in mode Feed pours into the throat, not outward, so it is exempt (decided, round 5: former open question 17).
  - **Strike:** on the tick the projection starts, then every 60 ticks while it lasts, a discharge from the nearest wall point to the projecting component. Damage is `RetK · (1 + n)`, with `n` the strikes so far on that component's **owner group** in this tunnel, so it escalates and a swarm of cheap projectors escalates fastest. The projecting component is disabled for 10 s, so a tractor lock breaks at once. Each strike drains `0.005 · EsMax`, within the drain cap (see Stability).
  - **Who takes the damage:** the projecting component and the grid it is mounted on, nothing else. The arc is drawn but hits no one in between, so a cheap projector parked beside an enemy is not a lightning rod. A projector clamped or docked onto a grid of another owner group is struck alone (its own body), never the foreign host, so planting a tractor limpet on an enemy hull cannot turn the wormhole on them.
  - **An armed charge is struck at once.** The strike breaches its trap, so it detonates on the next tick by exotic-matter.md's cascade rule. Inside the tunnel the pulse kicks and shears occupants as usual and drains `BombK · Y` (the point-blank value, rung down over 5 s) at its `s`. If that empties the reserve, the collapse starts there.
  - Server-side and deterministic; announced as `TunnelDischarge`. This doc owns tunnel retaliation: tractor-beams.md raises no separate event and calls this strike path (an emitter or target in a tunnel frame triggers it).
- Pushing or ramming an enemy into the wall is legal: it is kinetic.
- Nothing can warp, open a wormhole, project a catch point or anchor inside a tunnel.

**Collapse flow** (decision G; replaces the both-way flush for tunnels).
- **Start:** `Tcs`, the planned `TClose` or `Tz + 60`. From `Tcs` there are no admissions and feeds stop. The interior field freezes at its `u_q` at `Tcs`, and storms stop.
- **Collapse point `s_c`:**
  - Planned: `s_c = L_t · (0.25 + 0.5 · hash01(TSeed, collapse))`, published at commit, so pilots can plan.
  - Early: where the drain that emptied the reserve acted. A pulse inside the tunnel, a strike or an ejection uses its own `s`. Anything at a mouth (a cross-shard throughput race, a pulse near the mouth, a destabiliser) uses that mouth's end (`s = 0` or `L_t`). A destabiliser outpacing the feed uses the end nearer the strongest drainer (by drain power, then lower entity id). A slope that empties the reserve uses the same rule, and a pulse's ring-down counts as that pulse: the largest active ring-down slope at the zero tick (then lower `PulseId`) wins over destabilisers, and gives its own `s` or its mouth's end.
  - A collapse from an end is the gentlest for traffic heading away from it (everything flows to the far mouth) and the harshest for ships that just entered there; a pulse mid-bore ejects most. That is the attacker's choice, and the defender's reason to keep `Ψ` up.
- **Flow:** the axial current becomes `u_c(s) = sign(s − s_c) · U_c`, away from the collapse point toward each mouth, with `U_c = 0.9 · VCrossMax` (270 m/s), through the same coupling `κ`.
- **Fronts:** two fronts leave `s_c` at `V_f = 0.5 · U_c` (135 m/s): `s_±(t) = s_c ± V_f · (t − Tcs)`. The segment between them no longer exists.
- **Overtaken:** a body whose centre lies between the fronts at the end of a tick is **thrown out** at its own `s`, by the ram-ejection mapping, with a discharge but no impact and no ejection lockout. Items, debris and rounds overtaken by a front are absorbed, not ejected (see Wall contact). Overtaken ejections use the arrival budget and then the forced placement after 8 ticks, so none waits past the front.
- **Escaped:** a body reaching a mouth end exits through the disc with its axial speed clamped to `VCrossMax` and no damage. If its exit lane is occupied, it is placed by the flush's forced slide instead of being reflected. These exits are exempt from the arrival budget, which `TunnelCap` bounds.
- **Who escapes** (a body starting at rest at distance `D` ahead of the collapse point, toward the mouth): it escapes when `D > V_f/κ − (V∞ − V_f) · t*`, with `V∞ = U_c + a/κ` and `t* = −ln(1 − V_f/V∞) / κ`.

  | Ship | `κ` | Thrust toward the mouth | Must start ahead of `s_c` by |
  | --- | --- | --- | --- |
  | C-class, 360 t | 0.126 s⁻¹ | none (carried) | 330 m |
  | C-class, 360 t | 0.126 s⁻¹ | 3 g | 160 m |
  | Capital, 78,000 t | 0.021 s⁻¹ | none | 2.0 km |
  | Capital, 78,000 t | 0.021 s⁻¹ | 0.3 g | 1.2 km |

  Light ships ride the flow out; heavy ships near the collapse point are thrown into normal space at their point along the path. When `s_c` is at a mouth end, everything flows toward the other mouth and the whole length must be outrun.
- **Mouths:** when a front reaches a mouth end, that mouth runs the instant flush steps 1–3 on that tick (straddlers leave by it, the outward kick, despawn). The other mouth stays exit-only until its own front arrives. With `s_c` at an end, that mouth flushes at `Tcs`.
- **Duration:** at most `L_t / V_f`. A 24 km bore takes 89 s from the middle and 178 s from an end; the 60 km maximum takes up to 444 s from an end. This is why `TEnd = TClose + ceil(60 · L_t / V_f)`: the mouths, their shells, occupancy and lane reservations outlive `TClose` by up to that long.
- **Deterministic:** `Tcs`, `s_c`, `U_c`, `V_f`, every `κ` and the frozen field are known, so clients predict the fronts. Ejections arrive as frame-change events.

**Seeing and drawing.**
- Inside: `tunnel.glsl` draws the bore as a ring-swept tube generated on the GPU from `δ` and `R_t` (64 rings per km, out to 3 km, fogged beyond), with wells as lensing sprites and storms as arcs. About 0.15 ms at 1080p (estimate).
- From outside, a tunnel mouth shows the first 2 km of the bore with the same shader. In a stable bore the far mouth shows at the vanishing point, as a distortion-only disc. This replaces the cubemap for tunnel mouths.

### The path: meaning, geometry, cost

**Meaning.** The path is where the throat is "drilled" through space. Gameplay depends on it in four ways:
- the suppression it crosses raises the cost;
- bodies block it;
- catchers near it capture it;
- during charge, whatever pulls it can be read (`PathPull`).

It is a planning construct, invisible to third parties. Nothing travels along it in normal space, so it has no timing. For a tunnel it is also the **map** from tunnel arc length to normal space, used by ram and collapse ejections (see Tunnels).

**Geometry:**
- A polyline in `Lca` from A to B, with every pose taken at **`TOpen`**.
  - Rails chains are exact through `FrameTree.TryPoseAt` at plan time, because `TOpen` is fixed at charge start.
  - Simulated targets and hosts are extrapolated, then re-evaluated authoritatively at commit.
- **Only bodies' cores block a segment.** A core is `R + MouthFloor + r` (a black hole's is `InfallRadius + r`). With single-tick poses this is an exact segment-versus-sphere test, ~10 ns per body after warp's orbit-sphere prune.
- **Automatic detour:**
  - For each blocking body, one via point is inserted at the tangent of `1.05 × core`, in the plane of the segment and the body centre.
  - At most 2 vias. More than that is rejected as "path blocked".
  - Unlike warp, where the pilot places vias: for a wormhole, vias change only cost, never time (for a tunnel they also move where ejections land).
- The pilot may drag vias in the map to lower the cost, and the quote updates live.
- **Test capsules:** the polyline inflated by `r` for suppression sampling. For catcher tests the radius is the catcher's pull radius.

**Cost model.**
- `r̂ = r / 100 m` and `ℓ = ln(1 + L / 1e6 m)`.
- `Σ` is the mean of `min(S, 4)` over 64 samples along the path.
- `β ≥ 0` is a bought flux boost.
- `g = 1`, or the gate's `GateDiscount` (default 0.3) when B is a gate catcher.
- `Φ_gen` is the owner's bought flow bias.
- **Class factors:** tunnel `k_X = 1`, `k_E = 1`; instant `k_X = 2.5`, `k_E = 1.5`.
- **Additive factors** `m_X`, `m_E`, `m_S`, `m_C`, `m_L` are 1 without additives (see Additives).

```
Exotic   X = Xk · r̂² · (1 + β) · ℓ · (1 + Σ) · g · (1 + 3Φ_gen²) · k_X · m_X       Xk = 10 EM
Energy   E = r̂² · (1 + Σ) · g · (Eo · ℓ · k_E + Eh · OpenTicks / 60) · m_E + EsMax   Eo = 5 GJ, Eh = 0.2 GJ/s
Reserve  EsMax = EsK · r̂² · m_S                                                     EsK = 10 GJ
Charge   ChargeTicks = max(MinChargeTicks, ceil(60 · E / ChargeRateW · m_C))       capital generator 2 GW, min 600 ticks
                       (or stretched to a booked catch slot: TSlot − 180 − TChargeStart)
Flux     F = Fk · r̂² · (1 + β)   kg/s per direction                                Fk = 1e6 kg/s
Drain    e_m = 33 J/kg / (1 + β)                                                    per kg entering
Tunnel   L_t = clamp(LtK · ℓ · m_L, 2 km, 60 km)                                     LtK = 2 km
Fee      throat-grade EM content in grams (int; Q14), set by the managed controller's rules; escrowed at charge start, paid at commit;
         on an owner's cancel the controller keeps its CancelForfeit share
```

- **Mass and crossings:** cost does not depend on who crosses or how many. Mass throughput is bought through `r²·(1 + β)`. The flux limit enforces the rate, and each kilogram drains the reserve.
- **The upkeep to `TClose` is pre-charged,** so a wormhole never runs down on its own. Only drains end it early.
- **The quote is binding.** Commit spends exactly the quoted X and E, even if a push, slide or capture changes the geometry, and even if the target gate turns out to be busy (the discount stays). The fee is paid only if the managed gate actually catches the end.
- **Example:** r = 100 m, `Σ = 0`, `Φ_gen = 0`, 2 GW, no additives (`EsMax` = 10 GJ). Open 120 s, except the 1 AU and 5 AU tunnels, which need `OpenTicks ≥ 2 · TunnelLead` (241 s and 273 s) and are quoted at 300 s. Tunnel transit uses the reference profile: 1 g up to 200 m/s, no flow, a stable bore.

| Trip | ℓ | Class | Open | X (EM) | E (GJ) | Charge | Transit | Total (charge + 3 s + transit) | Warp (spool + travel) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Planet → moon, 20,000 km | 3.04 | Tunnel | 120 s | 30 | 49 | 25 s | 41 s (6.1 km) | 69 s | 48 s |
| Planet → moon, 20,000 km | 3.04 | Instant | 120 s | 76 | 57 | 28 s | 0 | 31 s | 48 s |
| Planets 1 AU apart | 11.9 | Tunnel | 300 s | 119 | 130 | 65 s | 129 s (23.8 km) | 197 s | 230 s |
| Planets 1 AU apart | 11.9 | Instant | 120 s | 298 | 123 | 62 s | 0 | 65 s | 230 s |
| Planets 5 AU apart | 13.5 | Tunnel | 300 s | 135 | 138 | 69 s | 145 s (27 km) | 217 s | 830 s |
| Planets 5 AU apart | 13.5 | Instant | 120 s | 338 | 135 | 68 s | 0 | 71 s | 830 s |

- **Tuning invariants** (round 5, D23: natural cheapest, manufactured fastest, warp cheap for close specific trips; replaces the earlier "more expensive but faster"), checked by tests rather than by formula:
  - **Natural is cheapest:** crossing a natural pair costs no exotic matter, no fuel, no opening energy and no game-charged fee (fees exist only for a managed gate catching a generator's end; whoever holds a natural mouth's frame may demand payment, but that is a player's price enforced by weapons, not a game cost), so for any trip a natural pair serves it is cheaper than both warp and any manufactured wormhole. Redirecting a natural end is a fleet operation, not a travel cost.
  - **Warp is cheap for one ship to a specific place:** exotic-matter.md's per-generator-class check stays, but as a **single-ship** rule, not a blanket one: a lone ship warps more cheaply than it can open the cheapest wormhole that fits it. The cheapest manufactured wormhole is tunnel class, so the check uses tunnel prices; instant pairs pass it with 2.5× the margin. A manufactured wormhole is paid once whoever crosses, so a fleet sharing one may pay less per ship than warping; that is allowed and intended.
  - **Manufactured is fastest:** with a capital generator, instant pairs beat warp for trips of 20,000 km and more (charge + 3 s), and tunnel pairs beat warp from about 0.7 AU (charge + 3 s + reference transit, at the minimum open time `2 · TunnelLead`; the test checks 1 AU). The instant class is therefore the fastest **on-demand** option within a system from 20,000 km (a natural pair that happens to be open and well placed can beat it, but only on its own schedule and route). A tunnel to a moon is slower than warping (that range is warp's niche): its value there is fleet capacity and cancelling frame velocity. The table bears this out: 1 AU tunnel 197 s against warp 230 s, moon tunnel 69 s against warp 48 s, instant 31–71 s everywhere. A booked catch slot may delay `TOpen` by choice; the invariants use the unbooked charge.

**Additives** (decision H; the items, mixing and crafting live in exotic-matter.md).
- A generator's X may be a **blend**: throat-grade EM plus up to 2 additives, together at most 10 % of X's content. The blend must still meet the generator's `MinPurity` (0.85) on its true content, and additives count as impurity there, so additive blends need purer EM. Purity is capped below 1.0, so there is always a cost in purity.
- Each effect scales linearly with its additive's fraction up to its cap. Effects and trade-offs are data (`data/em_additives/*.json`); placeholder values at the cap:

  | Effect | Gain | Trade-off |
  | --- | --- | --- |
  | Duration | Generator's `MaxOpenTicks` × 1.5 | `m_S` 0.8 (a smaller reserve) |
  | Aperture | Generator's `MaxRadius` × 1.25 | `m_X` 1.15, and the Stable threshold rises from 0.6 to 0.7 |
  | Flow bias | `Φ_gen` up to ±0.5 without the `(1 + 3Φ_gen²)` factor | `e_m` × 1.3 |
  | Flow reversal | `Φ_asym` from a capturing catcher's hold power applies with the opposite sign | `e_m` × 1.5, and `m_X` 1.1 |
  | Masking | The charge glow is visible only within 50 km instead of to A's whole interest; the mouths read as noise beyond 10 km even when keyed | `e_m` × 1.5, and `VCrossMax` × 0.8 |
  | Charge speed | `m_C` 0.6 (floor: half of `MinChargeTicks`) | `m_E` 1.25 |
  | Stability | `m_S` 1.5 | `m_X` 1.2, and `m_L` 1.25 (a longer tunnel) |

- Additives are reserved and spent with X, refunded with X on an owner's cancel, and drawn again at 0.25 on a re-establish. The quote shows the blended factors.

**Comparison with warp:**

| | Warp | Wormhole |
| --- | --- | --- |
| Role (round 5, D23) | Somewhere specific, relatively close by, cheaply | Natural: the **cheapest** travel (free to cross, on its schedule and route). Manufactured: the **fastest** (instant from 20,000 km, tunnel from ~0.7 AU) |
| Resource | Fuel ∝ mass · ln(1 + D/1e6), paid by **every ship** | Exotic matter ∝ r²·ℓ·(1 + Σ) (×2.5 for instant), plus power ∝ r²·duration plus the reserve. **Paid once, whoever crosses**. Plus any managed gate's fee, and the holders' power to keep it stable |
| Time before you can go | 5 s spool | 10 s–1 min charge (power-limited), or until a booked catch slot, plus 3 s commit |
| Travel time | 6 s (50 km) to 14 min (5 AU) | Instant: 0, crossing at transit speed up to `VCrossMax`, ±`ΔV` from flow. Tunnel: the bore's length at the speed the ship can hold, about 40 s to 2.5 min, longer when unstable or against the current |
| Capacity | One root ship and its subtree. A fleet of ≤ 16 with synced timing | Anything that fits through `r` and `Depth`, flux F kg/s each way, anyone, until collapse. Each kilogram drains the reserve. Possibly one way only |
| Exit | Anywhere along a smooth, planet-avoiding path (D15); no forced floor (D16), but exiting in low atmosphere makes a pressure wave with fines and blame for the damage | The suppression field (its floor stays a hard rule for mouths, see below). Gates bring it down to a pad; managed gates assign the catch slot. Warp the last hop if needed. A tunnel can also throw you out anywhere along its path |
| Abort | Before `TCommit` | Cancel before `TCommit − 30`. After commit it can only be collapsed, by draining it |
| Interdiction | `InterdictionField` forces a drop, with 3 s warning. Power only | Catcher capture at commit. 25× the power per unit area, plus exotic matter per capture. Pulls are readable during charge, but their signatures can be spoofed. Natural ends are redirected per cycle by fleet-scale power. Bombs and destabilisers can collapse an open wormhole |
| Privacy | The arrival location is broadcast as a safety beacon (round 5, D18; off on military ships, or the wire cut at the risk of fines and blame). The last part of the trip is in real space | Crew-only until commit. Public from then on, and enemies can follow. Signatures show who claims to own it |
| Main risks | Forced drop, camped rings, ambush at the beaconed arrival point, collision on the real-space last leg (D18) | Capture into a hazard, spoofed or noise signatures, followers, early collapse, the collapse flush or front, tunnel walls and wells, flares at the far end, flow against you |

### Suppression field

`S(p, t)` is a dimensionless, analytic function of world state.
- It is evaluated **server-side** for authority, and on clients only to draw the targeting UI.
- A point is **hard** (invalid for a mouth) if any term is ≥ 1. Soft terms add to the path cost.
- **Both** ends are checked over `[TCommit, TEnd]`:
  - SurfaceFixed mouths are static, and grid mouths are covered by the leash.
  - Rails mouths are sampled at 32 points across the window against rails sources, ~10 µs. A rails mouth therefore cannot drift into a station or a body core while it is open.
- Terms come in two classes:
  - **Natural** terms are the *deadly* ones. Gates scale them down, and Interdict waives them.
  - **Occupancy** terms are the *invalid* ones. No catcher waives them.
- **The floor is a wormhole rule.** `MouthFloor(body) = max(AtmosphereTop, TerrainMax + 2 km, 0.01·R)`, a constant of the body. It is the same base formula as warp.md's routing clearance `RouteShell(body)`, but without warp's hazard overrides (warp's `RouteShell(BH)` = 1.1×DiscOuter is warp's exit exclusion, not a mouth rule; the black-hole core is below). Round 5 (D16) removed any forced floor on warp exits; this floor binds mouths only. `WarpFloor` no longer exists.

| Source | Class | Term (d = distance to source, r = mouth radius) | Hard when |
| --- | --- | --- | --- |
| Body core and terrain | Occupancy | Inside `R + MouthFloor + r`. For catchers, the exact solid test on the disc, shell and lane (analytic height plus the `EditStore` bound, ~1 µs) | Inside |
| Body tidal | Natural | `s_tidal = 4μr / (A_max · d³)`, with `A_max = 3e-3 m/s²` (knob). Summed over all bodies pruned by distance, not by patched conics, so SOI boundaries leave no gap | ≥ 1. On a 1000 km, 1 g body: r = 25 m is never hard above the surface (floor and atmosphere govern); r = 100 m is hard below 94 km; r = 150 m below 252 km; r = 400 m below 736 km |
| Atmosphere | Natural | Inside `R + AtmosphereTop` | Always (part of the floor) |
| Star | Natural | `s_tidal`, plus a corona `R_corona = 3 R★` (also a heat zone) | Inside the corona, or tidal ≥ 1 |
| Black hole (owned by exotic-matter.md) | Occupancy core; Natural disc and jets | Core `InfallRadius + r` (64 km + r) **replaces** `R + MouthFloor + r`; Natural hard terms inside the disc slab and the jet cones, plus `s_tidal` as for any body. A forecast birth (`BlackHoleBirthForecast`) counts as a core from its forecast tick, and a pending collapse (forced or natural, D21) changes nothing, because `InfallRadius` already keeps mouths out of the remnant's volume | Inside the core; disc, jets or tidal ≥ 1 unless waived |
| Grid (hull) | Occupancy | Geometric: `d < ρ_g + r + Depth + 100 m`. Without permission, warp's `ExclusionRadius`. Soft `(ρ_g/d)²`. The host is exempt for its own mounted mouth, which must still clear its hull by 2 m. A wormhole frame is exempt for its own natural mouth, but must clear that mouth's shell, disc cylinder and a `LaneMin` exit lane | Geometric always. Exclusion unless permitted |
| Warp drive | Occupancy | A spool past its lock-in (from T0 − 30), or a committed warp exit `E` until T1 + 2 s | Within `5 km + r` |
| Mouth | Occupancy | Committed, open or collapsing player mouths, each until its `TEnd`. Natural mouths over their scheduled Forming, Open and collapse windows (to the cycle's `TEnd`) that overlap the evaluated window (see Privacy). Confirmed catch slots, through their controller's slot ring. Soft `(4 r_m / d)²` | Within `max(4 r_m + r + 1 km, R_f,m + R_f)`, so no two flow fields ever overlap |
| Flare | Natural | A point inside a cone **forecast by the evaluating tick**, active during `[TOpen, TEnd]`, inflated by the forecast's uncertainty | Always |
| Mouth scar | Occupancy | A collapsed mouth inside its re-establish window. Every wormhole except its own re-establish | Within the old mouth's hard radius |

- **Path blocking vs path cost:** only body cores block a path. Every other term adds `min(S, 4)` to `Σ`.
- **Privacy:**
  - Plan results use only sources in the requester's contacts, plus bodies, published forecasts and the natural mouths the requester has charted (at their charted poses and schedules).
  - Unknown hulls and unknown mouths are resolved silently at commit, by a radial push to `S < 1` within the binding quote.
  - **Commit** also treats every natural mouth whose scheduled Forming, Open or collapse window overlaps `[TCommit, TEnd]` as occupancy, charted or not. Otherwise a player mouth could sit where a natural mouth is about to form, with overlapping shells. The push this may cause is indistinguishable from a hidden-hull push, and it costs a whole committed wormhole to observe, so it is no probe.
  - Commit never uses the server's foreknowledge of flares that have not been forecast yet. A push would leak them.
- **Evaluation:**
  - Sources are pruned by distance. A body drops out where `s_tidal < 1e-3`; grids come from the frames near the sample.
  - About 0.3 µs per point, and 20–150 µs per plan or commit including vias and the rails sweep.
  - Run as a job, so the main thread pays nothing.
- **Client UI:** a fragment shader over the map takes up to 128 client-known sources in a UBO.
  - Shading: green below 0.25, amber in the soft range, red when hard.
  - About 0.05 ms at 1080p, only while the panel is open.
- **Warp gets the reverse rule:** the exit lanes and hard spheres of committed and open mouths (natural ones in their windows) are `ExitReservation`s in warp's occupancy test, and confirmed catch slots are tested through the catcher table (pruned by each catch volume's sphere).

- **Tunnels have no suppression.** No mouth, catch point or warp exit can be placed inside one, so nothing needs testing there. A ram or collapse ejection into normal space ignores natural terms and applies occupancy terms (see Tunnels).

### Catchers

A **catcher** is a component on a station or ship. It projects a **catch point** up to `ProjectRange` (default 500 km) from its host. The catch point sits in the frame the band rule gives (hover in a surface frame, rails, or the host grid), and has a facing. Friendly, station, managed and hostile catchers are one component. There is one pull law:
- the **mode** decides whose paths the catcher pulls;
- the optional **traffic control** decides where and when, inside its catch volume, a caught end goes;
- the **signature** decides who it appears to belong to.

| Mode | Whose paths it pulls | Suppression at the catch point | Cost | Visibility |
| --- | --- | --- | --- | --- |
| Gate (policy: Private, Party, Faction, Public) | Only paths that **target** it, from permitted owners. Policies are server truth (owner, party, faction membership) and cannot be spoofed | Natural terms ×`k_gate` (0.5 to 0.05 by tier; the tidal hard radius scales as `k^(1/3)`). The floor is replaced by the exact solid test. Occupancy terms and flares are unchanged | Establish 20 EM, upkeep 5 MW. Optional `HoldPowerW` (flow toward the gate). Wormholes ending there pay ×`GateDiscount` | Public gates are published as contacts with lanes, at 0.2 Hz per SOI, like arrival rings. Each listing shows the gate's **broadcast claim** with verdict Unknown until the reader has a real reading. Never the owner |
| Interdict | **Any** path whose capsule passes within `PullRadius`, except paths from the catcher owner's group | **Natural terms waived.** Occupancy terms kept: exclusion zones need the grid's permission, and warp drives, mouths and the solid test all apply. The exit lane must be clear for `LaneMin = 2 km` along `n_B` | `P = 250 MW · (PullRadius / 10 km)²` while active. Per capture, `max(5 EM, 0.25 · X_captured)`, plus any `HoldPowerW`. 30 s spin-up | Loud: sensor signature ∝ power. Its energy signature may be its faction key, a copy, or noise |
| Traffic control (option on either mode) | Unchanged | Unchanged, applied per catch slot | +2 MW. Rules from data | Bookings are visible to the controller's owner only. An offer shows a requester only the earliest free catch slot for its request |

- **Limits:** one catcher per host grid and at most 4 per group. Each host caps a catcher's total draw (`CatcherMaxPowerW`, 8 GW for a capital host). The pool holds 1024, replicated to every shard. This stops pool exhaustion by catcher spam.
- **Ground gates:** the disc, shell and lane must all clear terrain. A ground gate faces sideways, or hovers at least `Depth + 2 m` up.
- A catcher with a `RedirectTarget` (a natural mouth, see Natural wormholes) does not capture player paths.
- **A busy target gate.** A gate without traffic control has one catch slot. If it (or every slot of a managed gate) is held at commit, the targeted end falls back as for an unpowered controller (below). Plans to a plain public gate get a `GateBusy` warning when its slot is visibly held over the requested window; that is public information, since the mouth is visible.

**Managed catchers (traffic control):**
- **Known exit point:** a plain station gate with no traffic control is the simple case. It has one catch slot, at the catch point, on the published lane, so its exit is known in advance.
- **Catch volume:** a `TrafficControl` computer on the catcher's host turns the catch point into a sphere of radius `VolumeRadius` (≤ 20 km) about it, in the same frame. It holds up to `SlotCapacity` (≤ 8) mouths at once.
- **A catch slot** is a local position, facing, `TOpen`, `TClose`, `r` and `Depth`.
  - Catch slots are laid out so that every pair of them clears the mouth occupancy term and each other's exit lanes.
  - The controller uses the same `Suppression.Evaluate`, with natural terms × `k_gate` (or waived, for Interdict).
- **Rules are data run by the server,** never a player in the loop, so answers are deterministic:
  - admit or refuse lists keyed on server truth (owner, party, faction membership);
  - signature filters keyed on the requester's generator signature *as the controller's own sensor reads it*, which can be spoofed;
  - a fee table and a `CancelForfeit` share;
  - catch-slot spacing;
  - queue length (`MaxBookings`, ≤ 32) and a per-group cap (`MaxBookingsPerGroup`);
  - fleet blocks;
  - the gate's flow bias `Φ_gate` and its `HoldPowerW`.
- **Booking** (`SlotRequest`, sent with a plan whose target is a managed gate):
  - The controller answers with an **offer** of the earliest free catch slot at or after the plan's earliest `TOpen`, or a refusal (`Refused`, `Full`, `FeeUnpaid`, `FlowClosed`, `GroupCap`).
  - The answer depends only on the controller's published policy, its own bookings and mouths, and its owner's contacts, **never on server-only knowledge**. Plan results stay free of hidden hulls.
  - **An offer is not a hold.** It is valid for 30 s, but it reserves nothing, so requests cannot be used to lock a controller's catch slots for free.
  - **Confirmation** happens at charge start. Charge starts are processed in `(tick, requestId)` order on the controller's shard. If the offered catch slot is still free, the charge starts, `ChargeTicks` stretches to `TSlot`, and the fee is escrowed. If it was taken meanwhile, the charge request fails with `SlotTaken` and a fresh offer.
  - A confirmed booking holds its lane and hard sphere over `[TCommit, TEnd]` (freed earlier if the wormhole collapses early, once its scar window ends) in the controller's **own** catch-slot ring (32 per controller), not in the global `ExitReservation` pool. Warp commits and other wormholes' commits test it silently through the catcher table. Bookings therefore can never exhaust the global pool and refuse warps system-wide with "traffic".
  - **Griefing limits:** `MaxBookingsPerGroup`, the `CancelForfeit` kept on an owner's cancel, and the 50 % energy loss and cooldown of the cancel itself.
  - **Fleet queues:** a fleet can book in one of two ways:
    - one large catch slot sized for the fleet's union and flux, which crosses through one mouth using the `MouthQueue`;
    - a block of consecutive catch slots for several generators, spaced in space by the occupancy rule, or in time by `TEnd_i + 60` when the volume is full.

    Blocks are confirmed in request order, and a block confirms only whole.
  - **Revocation:** the controller may revoke a booking only before `TCommit − 30`. The traveller's charge is then cancelled with no energy loss and no cooldown, and the exotic matter and the whole fee are refunded. After `TCommit − 30` the booking is locked, like the charge.
- **At commit (catch-slot assignment vs commit-time resolution):**
  - The capture rule below runs unchanged. The managed gate is the plan's target at `s_c = 1`, so an interdictor earlier along the path still wins. In that case the booking is released and the fee is refunded.
  - If the gate wins, B is placed at the **booked catch slot**, not at the catch point. The ordinary occupancy test then runs against hidden hulls, unbooked warp arrivals and intruders parked in the catch slot:
    1. slide along the slot's facing in 2r steps, up to 8, staying **inside the volume**;
    2. then try the controller's next free catch slot;
    3. then leave the volume along the facing at twice the distance, as warp does.

    All of this happens before the announcement, so B is never moved after it.
  - If the controller is unpowered or destroyed at commit, or the gate is busy, the catch point is treated as a free point with unscaled suppression. If that point is hard, B is pushed outward along the facing to `S < 1`, within the binding quote. The fee is refunded.
  - The booking's ring entry becomes the mouth's own lane reservation. The fee is paid to the controller's owner.
- **Hostile traffic control:** the same computer on an Interdict catcher assigns captured ends to free catch slots at commit. There is no booking, because captures are never announced in advance. Such an interdictor keeps capturing until all `SlotCapacity` catch slots are held, so a decoy must fill every one.

**Capture rule** (at commit only, deterministic):
1. **Candidates** are catchers that meet all of these:
   - `Start + SpinUp ≤ TCommit < End`;
   - powered;
   - holding enough exotic matter for a capture;
   - no `RedirectTarget`.

   Cross-shard wormholes use `TCommit − 300` (see Networking). A catcher whose catch slots are all held is busy and skipped. Without traffic control it has one catch slot, at its catch point.
2. For each one, compute the closest point on the path polyline at `TOpen` poses: arc position `s_c ∈ [0, 1]` and distance `d_c`.
3. A capture needs `d_c ≤ PullRadius`, or, for a Gate, being the plan's target.
4. **Winner:** smallest `s_c` (first along the path from A), then larger `PullRadius`, then lower `CatcherId`. An interdictor between you and your gate wins.
5. **Place B** with the captured `r` and `Depth`:
   - at the catch point;
   - or, with traffic control, at the booked catch slot (for the target gate) or the first free catch slot (for a capture).

   If it is blocked:
   - it slides along the facing in 2r steps, up to 8, inside the volume when there is one;
   - if it is still blocked, there is no capture and the next candidate is tried;
   - on success, B moves there with the catch slot's or catcher's facing and frame, the path is truncated there, the catcher's exotic matter and hold energy are debited, and the asymmetry flow term is fixed.

   **Who decides what:** the shard deciding the commit chooses the **catcher**; the shard that owns the catcher's frame (which is B's frame) chooses the **catch slot** and runs the placement, in `(decision tick, TOpen, WormholeId)` order. On one shard these are the same. Across shards, a capture claim decided at `TCommit − 300` may find the catcher's last slot taken by a local commit in the meantime. The claim still stands (B cannot move after announcement anyway): the catcher runs over capacity by that mouth until it despawns, and placement uses the slide and fallbacks above. This needs two wormholes from different shards to hit one full catcher within 5 s.

**Reconciling "interdiction redirects" with "no abort after commit":**
- The redirect is decided **at** commit, from the catchers eligible then, and is part of the irrevocable commit.
- Catchers raised after `TCommit` have no effect on this wormhole.
- **Before commit the crew get two warnings.** Both feed one list in the plan panel:
  - **Instability warning (client):** if a catcher in the crew's contacts passes the capture test against their path. Crew clients evaluate it at 1 Hz, so the server carries no load.
  - **`PathPull` (server):** from 25 % charge, one reading for every catcher whose pull covers the path and that is spun up, **or spinning up and eligible by `TCommit`**, whether or not it is in contacts. Open natural mouths within `4 r_m` of the path are included and read as noise; they never capture. Each reading carries:
    - an arc band (`s_c` ± 0.1);
    - a strength class (`d_c / PullRadius` in quarters);
    - a spinning flag;
    - a signature verdict (see Signatures).

    It never carries a position or an entity id. The pulling catcher's owner gets a symmetric `PathRead`.
  - Including spinning catchers matters: `TOpen` is public from the charge glow, so an interdictor could time its 30 s spin-up to finish exactly at `TCommit` and never appear among spun-up pullers. A capturing catcher must be active from `TCommit − 30 s`, so the crew now always see it at least ~28 s before the cancel lock.
- The crew may cancel until `TCommit − 30`.
- **Counterplay:**
  - Charge faster than a catcher's 30 s spin-up, by bringing more power.
  - Route vias around known catchers.
  - Cancel.
  - Send a **decoy**: a small, cheap wormhole whose path crosses the catcher. Its capture holds one of the catcher's catch slots until it collapses and drains at least 5 EM from it. Minimum `OpenTicks` is 30 s. An attacker who drains a decoy's reserve frees the slot early, so decoys want feeders too. A traffic-control interdictor needs `SlotCapacity` decoys.
  - Read the pull. A claim to your own faction with a stale epoch is flagged as forged at once. A claim you can't verify within your charge is only a claim.
  - Rotate your faction's key, so that copies taken before the rotation go stale.
  - Simply don't cross a captured mouth.
- **Accepted consequence:** a captured wormhole is a two-way route paid for by the victim, subject to its flow. The interdictor can come through to A.

**Deadly is allowed; invalid is not.**
- An interdict catch point may sit in a flare cone, inside an atmosphere, low over terrain, or in a corona.
- It may not sit in solid geometry, inside any grid's exclusion zone without that grid's permission, or facing terrain closer than `LaneMin`.
- The `LaneMin` rule stops a facing aimed at the ground from becoming a kill box with no time to react.
- Exclusion protection is the default for every grid, not opt-in.
- The same rules hold for every catch slot in a catch volume.

**Warp interdiction revised: power cost.**
- `InterdictionField` gains an `Emitter` entity and `PowerDrawW = 10 MW · (Radius / 10 km)²`. No exotic matter.
- `End` is derived at activation from the emitter's capacitor: `Start + 60 · CapacitorJ / PowerDrawW`. It is announced, so it stays deterministic. Recharging sets a new `End`.
- Unpowered fields deactivate.
- Wormhole interdiction costs 25× more power per unit of pull area, plus exotic matter per capture.
- The emitter carries a signature like any other emitter.

### Signatures and keys

**What carries one:**
- Every generator (and so every wormhole it opens), every catcher, every wormhole frame, every stabiliser and every interdiction emitter emits an **energy signature**:
  - `Keyed(Faction)`, the owner's faction key by default;
  - `Noise`, run **dark**, as any owner may choose;
  - a loaded **copy** of another faction's key, which is a spoof.
- **A genuine keyed emitter always claims its faction's current epoch,** at every reading. A wormhole keeps the faction its generator's owner had at charge start, but its epoch follows the faction live, even after the generator is gone. Only copies are frozen at their scanned epoch. (Freezing genuine wormholes would make a key rotation flag the faction's own open wormholes as forged.)
- Natural mouths are always noise and have no key.
- Factions (`FactionId`, a set of groups) are new and hold the keys. A group without a faction is keyless. (Round 6: factions-and-law.md defines them. `FactionId` is a byte, 0 = none and 1–31. A "group" or "owner group" here is its `Organisation`; a party is not an owner. An organisation gets a key by chartering under a faction, and `GatePolicy.Faction` reads enlistment and charters.)

**Truth stays on the server:**
- Whether an emitter is genuine, and a copy's fidelity and age, never leave it.
- Readers receive only a **verdict**.
- Each reader–emitter pair is evaluated once per second, on its own stagger tick `hash(reader, emitter) mod 60`, from integer ticks and fixed inputs. There is no randomness, so re-reading never re-rolls.

**Contest inputs:**

| Input | Meaning |
| --- | --- |
| `Q_e` | Emitter quality, 0.2–1, from the component tier. It is hardware, so loading a copy does not change it |
| `φ_eff` | Effective fidelity of a copy: `φ · 0.5^(age / CopyHalfLife)`, with `age` = ticks since the copy's scan and `CopyHalfLife` = 6 h (knob). `φ ≤ 0.95`. Genuine emitters have none |
| `E_c` | The epoch the emitter claims |
| `E_ref` | The newest epoch of that faction the reader's group has seen in any reading. Members always hold the current epoch |
| `Q_s`, `R_s` | The reader's `Sensor` quality (0.2–1) and range |
| `c` | Closeness, 0–1. Direct scan: `clamp(1 − d / (0.1·R_s), 0, 1)`, i.e. a close scan within 10 % of range. Path pull: `clamp(1 − d_c / PullRadius, 0, 1) · f_charge`, or `f_charge` for the gate the path targets. Mouth seen at B, or frame sensing: direct, with quality ×0.5 for frames |
| `m` | 2 if the reader's group belongs to the claimed faction, otherwise 1 |
| `e` | Evidence. Each evaluation adds `Q_s · c · m / (30 · Q_e)` (one second's worth). It resets after 5 s at `c = 0` |

Evidence is integrated, not read off the current closeness. A threshold on dwell time with the *current* `c` would let a reader idle at the edge of range for minutes and then collect a full close-scan verdict in one brief pass.

**Verdicts:**
1. **Unknown:** a direct reading beyond `R_s`. Path pulls are always readable once the 25 % gate has passed.
2. **Noise:** a keyless emitter, either natural or dark.
3. **Forged, at once:** `E_c < E_ref`. The copy predates a key rotation the reader has already seen.
4. **Forged** when `e ≥ φ_eff`, for a copy. This is the close-scan tell. With `c = 0` there is no evidence and no tell.
5. **Verified** when `e ≥ 0.95`, for an emitter not flagged by rules 3–4. Because `φ_eff ≤ 0.95`, **a forgery is always flagged by the time it could be verified, so it is never Verified.**
6. **Claimed:** otherwise. The faction and epoch are shown but not proven.

There is no intermediate "Suspect" verdict. Only forgeries could ever reach one, so it would have been a certain tell earlier than Forged.

**Examples** (constant `c`, a fresh copy; the time is `30 s · φ_eff · Q_e / (Q_s · c · m)`):

| Situation | `Q_e` | `φ_eff` | `Q_s` | `c` | `m` | Detection time |
| --- | --- | --- | --- | --- | --- | --- |
| Mid-range scan | 0.8 | 0.9 | 0.6 | 0.5 | 1 | 72 s |
| Close scan, top sensor | 0.8 | 0.9 | 1 | 1 | 1 | 21.6 s |
| Close scan, member of the claimed faction | 0.8 | 0.9 | 1 | 1 | 2 | 10.8 s |
| Path pull at `c` = 0.25 | 0.8 | 0.9 | 0.6 | 0.25 | 1 | 144 s |
| Same copy, 12 h old | 0.8 | 0.225 | 0.6 | 0.25 | 1 | 36 s |

A fresh forgery usually outlasts a charge unless its key is stale. That is the intended value of rotating keys and of copy freshness.

**Copying:**
- Copying needs an **active scan** of a keyed emitter within `R_s`. Fidelity grows as `φ(τ) = φ_src · 0.95 · (1 − e^(−τ · Q_s · c / 60 s))`, where `φ_src` is 1 for a genuine source and the source copy's own `φ_eff` for a copied one.
  - At `Q_s = 1` and `c = 1`: 60 s gives 0.60, and 180 s gives 0.90.
- A copy records the claimed faction, the epoch and `ScannedAt` at the end of the scan. It is an inventory item (`SignatureCopy`, 16 B) and can be traded.
- Scanning noise yields nothing to copy.
- Path pulls, mouth readings and frame sensing never yield copies.
- **Active scans are visible:** the scanned emitter's owner sees a "being scanned" flag on the scanner's contact (`ScanAlert`).
- Any emitter can load a copy: catchers, generators, frames and stabilisers. The decision names catchers; generators, frames and stabilisers use the same mechanism (decided, round 5: former open question 11).

**Key rotation:**
- A faction's key holder (a faction officer, factions-and-law.md) rotates the key: `Epoch++`, with a 1 h cooldown.
- Every genuine emitter of the faction, and every open wormhole its members opened, claims the new epoch from the next tick, and members' `E_ref` updates (`KeyRotated`, to members only).
- Outsiders learn the new epoch only by reading a re-keyed emitter. From then on, every older copy is flagged as forged at once by their group.
- Copies are not re-keyed. Copiers must scan again.

**Where readings happen:**
1. **Contacts:** direct sensor readings, attached to contacts as `SignatureReading` events, on change.
2. **Path pull during charge:** "the travellers' sensors read the signature of whatever is pulling their path". The reader is the generator host's best `Sensor`. `c` grows with the pull's strength and with charge progress.
3. **Mouths:** readers in the interest of A and of B read the generator's signature on the forming and open mouths. The station at a managed catch slot reads every arriving wormhole at close range.
4. **Frames:** a frame reads through a natural throat at half quality (see Natural wormholes).

**Interactions:**
- **Readings vs capture at commit:** see "Who is told what". A reading says who claims to pull the path. Only commit says who wins.
- **A hostile catcher posing as a natural wormhole only needs to broadcast noise.**
  - An open natural mouth near a path also produces a noise pull, and it never captures. Signature alone cannot tell the two apart, and there is no copy to detect.
  - The only tells are physical:
    - no charted natural mouth is open at that arc. Clients evaluate charted schedules, so a noise pull where the chart shows nothing, or shows a closed mouth, is suspicious;
    - the interdictor's power signature, if its host is in direct contact.
  - Faking noise is cheap. That is intended: charts are the counter.
- **Spoofed customs:** a hostile interdictor loaded with a friendly faction's fresh copy reads as that faction's interdicting traffic control. Members see through it at `m = 2` when close, or at once if it is stale.
- **Lure gates:** a hostile Gate broadcasting a friendly copy can be published as a public gate. The listing shows only the claim, as Unknown. A member who targets it reads it through `PathPull` at `c = f_charge`, `m = 2`, so a fresh top-fidelity copy is usually flagged within the charge and a stale one at once. Faction-policy gates are server truth and cannot be faked at all.
- **Spoofed generators at managed gates:**
  - A controller's signature filter reads the requester's generator at plan time with `c = 0`, so only the epoch rule applies. A fresh copy passes.
  - When the mouth forms at the station's catch slot, the station reads it at close range, and a forgery shows once its evidence reaches `φ_eff`. The station's defences deal with it, not the gate.
  - Server-truth policies (Private, Party, Faction) cannot be spoofed.
- **Probing:** `PathPull` would reveal hidden catchers to anyone who charges. Several things limit it:
  - it starts only at 25 % of a real charge;
  - an owner's cancel loses half the charged energy and starts the cooldown;
  - it reveals no positions;
  - the catcher's owner gets `PathRead`.

### Natural wormholes

- **Generation:**
  - 2–6 pairs per system, from a **server-only `NaturalSeed`**. The universe seed is on clients for terrain, so it can't be used.
  - **Destinations (round 5, former open question 5):** natural pairs may lead to **other star systems**, and only natural pairs do (no cross-system warp, no cross-system manufactured plan; the Dark Gate's far end may be in its own system or another). Each system is its own `Solar` root (dedicated servers shard by system first, then by SOI subtree within it; a listen server hosts them all), so a natural pair's mouths may sit under different roots; crossing is already a portal-map transfer, never a tree edge, so `Relative` stays unchanged. The frame-tree and sharding detail belongs to frames-and-zones.md (one `Solar` root per system, systems linked only by wormholes).
    - "2–6 pairs per system" counts the pairs with an end in that system. The partner end is generated with its own system's validity rules, from the same server-only seed.
    - **Manufactured wormholes stay inside one system.** A plan's path is a polyline in `Lca`, and two `Solar` roots have none, so a plan whose ends lie under different roots is refused ("no common frame"). This also keeps `ℓ = ln(1 + L / 1e6 m)` from pricing an interstellar throat at about twice a 1 AU one. Cross-system travel is natural pairs only, which fits D23 (natural is the cheapest; manufactured is the fastest within reach).
    - The ejection path of a cross-system pair is two legs, one per system (see Tunnels, Ram ejection).
  - Each mouth is fixed for the life of the save. It is either on rails (a heliocentric orbit or a high orbit about a planet) or fixed in a body's surface frame, hovering inside the rotating band.
  - **Validity:** for rails mouths, the minimum orbit-orbit distance (MOID) to every body's orbit must exceed that body's hard radius for this mouth, plus a margin. Hovering mouths are checked against their own body's core and against the MOID to its moons' orbits. Both checks are exact forever, unlike sampling one synodic period.
  - **Size: usually immensely large.** Radius 150–1,200 m, skewed large (median ~500 m), so most exceed anything a player generator opens. `Depth = min(2r, 2 km)`. **Every natural pair is tunnel class.** Flux, the natural stability level and recovery time, the mass drain, the tunnel length (5–40 km), `VCrossMax`, `A_fmax`, `V_flush` and the flow terms come from the seed.
  - Signature: always noise.
- **Schedule:**
  - Cycle `k = floor((tick − Epoch) / Period)`, with `openStart_k = Epoch + k·Period + 180 + hash(pairSeed, k) mod Jitter` and `openLen_k = OpenMin + hash2(pairSeed, k) mod OpenSpread`.
  - Forming is `[openStart_k − 180, openStart_k)`, and its start is the cycle's **natural commit tick** `TNC_k`. Closing is the last 600 ticks of the open window, as for player-made wormholes.
  - Generation enforces `180 + Jitter + OpenMin + OpenSpread + ceil(60 · L_t / V_f) ≤ Period`, so Forming and the whole collapse lie inside cycle `k` and only cycles `k` and `k − 1` need checking: about 10 ns, exact forever. (Without the collapse term a 40 km bore collapsing from an end could still be flushing when the next cycle forms.)
  - Generation also enforces `openLen_k ≥ 2 · TunnelLead`, so every cycle admits traffic.
  - Typical: open 5–20 min every 1–6 h.
  - At the end of each open window the tunnel collapses exactly as a player-made tunnel does at `TClose` (collapse flow, then each mouth's flush). If its reserve is drained to zero first, it collapses early and stays closed for the rest of the cycle. The next cycle forms on schedule; the schedule never moves.
- **Stability:** each cycle forms at its seeded natural level `Ψ_nat,k` (0.45–0.95, so some cycles open Unstable) and relaxes back to it after drains or overfeeding (see Stability). Seeded parameters also set flux, schedule spread, `VCrossMax` and flow strength. Radius is constant, because a varying rim would complicate collisions for no gain.
- **Ejection path:** a natural pair has no plan, but its tunnel needs a path for ejections. At each `TNC_k` the planner's core-detour rule runs between the two mouths' poses at `openStart_k` (for a cross-system pair, along each mouth's own leg, with `L_leg` from `pairSeed`). Clients compute the same path from the record.
- **Flow:** seeded per cycle and possibly reversing within it (see Flow). A strongly flowing natural pair (`|Φ| ≥ 0.83`) is one-way for part or all of its open window.
- **Redirection** (replaces "cannot be captured"). A natural pair has no planned path (only the ejection path, fixed at `TNC_k`), so it is not captured. Instead, one end can be **dragged** for one cycle:
  - **Who can try:** a faction (or a factionless group) points interdict catchers at a **charted** mouth. Each catcher gets `RedirectTarget = MouthId`, which needs the record. Their catch points must lie within `ProjectRange` of that mouth's charted pose. A redirect catcher draws a chosen `PowerDrawW`, up to its host's `CatcherMaxPowerW`; its pull radius does not matter.
  - **Contest:** at `TNC_k`, the server sums, **per faction**, the power of its spun-up, powered redirect catchers on that mouth. Pooling per faction lets several groups, each held to 4 catchers, mount one operation.
    - The faction with the largest sum that is ≥ `P_redirect = 1 GW · r̂²` wins. Ties go to the lower id.
    - It pays `X_redirect = 10 EM · r̂²`, split across its catchers in proportion to their power.
  - **Placement:** that mouth opens for cycle k at the winning faction's **lead catcher's** catch point and facing. The lead catcher is the one with the most power, then the lower id.
    - It uses the same validity as a capture: occupancy terms kept, natural terms waived, `LaneMin` required.
    - With traffic control, the first free catch slot is used.
    - If it is blocked, the redirected mouth slides in 2r steps; if it is still blocked, the next faction is tried; if none succeeds, the mouth opens at its charted pose.
  - **Scope:** one cycle only, with a spare `MouthId` per pair for the redirected pose. The charted mouth stays closed for that cycle and reverts on the next.
    - Each end can be redirected independently, so both may move in one cycle.
  - **Scale** makes it fleet-scale. With each capital host's catcher capped at 8 GW:
    - r = 150 m needs 2.25 GW and 23 EM: one capital host;
    - r = 500 m needs 25 GW and 250 EM: four capital hosts;
    - r = 1,200 m needs 144 GW and 1,440 EM: eighteen capital hosts, a multi-group faction operation.
  - **Announcement:** `NaturalCommit`, at `TNC_k` and **only when the cycle is redirected**. It goes to the interest of both charted ends and the new pose, the redirecting faction's members, and the owners of frames on that pair. A client that joins the interest of a charted end or the new pose later in the cycle gets the cycle's `NaturalCommit` on joining, as warp replays plans to late joiners. Without it, clients assume the charted poses. As with captures, a redirect is revealed 3 s before open.
- **Frames:**
  - **What a frame is:** a grid built around a charted natural mouth. A `WormholeFrame` component marks it. Its parent is the mouth's frame, so it rides the mouth's rails or surface pose and is never moved by flow or the collapse kick.
  - **Build rules:**
    - The builder's group must hold the pair's record. The component names the pair from the record, so frame placement can't be used to guess uncharted mouths.
    - The hull must clear the mouth's shell, its disc cylinder, and a `LaneMin` exit lane.
    - The frame must ring the rim: frame cells covering at least 75 % of the circumference at radius ≤ 1.5r. For a 1,200 m mouth that is a station-scale ring.
    - **One frame per mouth.** A second frame on the same mouth fails validation. Holding the frame is then a matter of the weapons on it.
    - The client validates all this from the chart, and the server validates it authoritatively.
    - It is secured with ordinary weapons on the grid. There are no special rules.
  - **No leak through refusals:** building an ordinary grid near a closed, uncharted mouth is **never refused**, because a refusal would leak the mouth.
    - If a dynamic hull sits in the keep-out when the mouth starts Forming, it is pushed out whole along `n` (shell creation's displacement, extended to hulls for natural pairs).
    - A static grid (anchored to terrain, or itself a frame for another mouth) is never moved. Where it overlaps the shell it gets a collision-group exclusion against it until they separate, as in the cross-shard conflict rule, and its hull counts as occupying the exit lane.
    - That reveals nothing the opening does not.
  - **Sensing:** a powered frame with a sensor gets a `FrameReading` at 1 Hz, on change, for the owner's group. It describes the region within `R_fs = 50 km` of the **partner** mouth (or of its redirected pose this cycle):
    - **Forming and Open:** contact counts by mass class, plus signature readings at half sensor quality.
    - **Closed:** counts by mass class only, so the frame sees who is staging for the next cycle, but not their signatures.
    - The reading is computed by the shard that owns the partner mouth's frame and forwarded to the frame's shard: ~20 µs per frame per second, at most 12 frames per system.
  - A frame's own signature is its builder's key, or noise if it runs dark.
- **Discovery:**
  - Closed mouths are undetectable.
  - Open mouths are contacts within a sensor range of `2,000 km · r̂` (round 6: a gravimetric contact at that range for an M gravimeter, × 2 for L; combat-and-sensors.md section 4 owns sensor kinds and ranges).
  - First detection sends a `NaturalWormholeRecord` (~60 B: anchors, schedule parameters, radius, `pairSeed = hash(NaturalSeed, pairIndex)`) to the player and their party. Flow, flush and stability parameters derive from `pairSeed`. The record is persisted with the player and can be traded as a chart item. The pair seed reveals nothing about other pairs.
  - After discovery there is zero traffic: clients evaluate the schedule and flow from the tick. The only exception is `NaturalCommit` on redirected cycles.
- **Camping:** natural mouths can still be camped. Plans never see uncharted closed mouths, and commits only see their scheduled windows as occupancy, indistinguishable from hidden hulls.
- **Crossing** uses the same code as player-made tunnel pairs.

### Solar flares and star hazards

- **Events, deterministic from the star's server-only seed plus the tick:**
  - Stratified, so access is O(1) with no walk from the epoch: `onset_k = k · MeanInterval + hash(seed, k) mod MeanInterval`.
  - Per event: direction (a unit vector in the star's inertial frame), half-angle 5–20°, duration 30–120 s with a cos² onset and decay, and peak dose rate `D0` at 1 AU (default 0.05 Gy/s).
  - The cone is active along its whole length for the whole duration. There is no travelling front, consistent with warp's "no light lag".
- **Dose at p:**
  - `D(p, t) = D0 · env(t) · (1 AU / d)²` if `(p − c★)·dir ≥ cosθ · |p − c★|`, else 0.
  - Zero if a body occludes the star: a ray–sphere test against bodies pruned by angle, ~50 ns. Hiding behind a moon works.
- **Damage** (server, 1 Hz, staggered, only for entities with crew or components):
  - **Ship shielding comes from the hull bake.** A `ShieldTable` holds, per room and per component anchor, the areal density `σ[14 dirs]` in kg/m².
    - It is computed from the 0.5 m coarse occupancy, by marching from the room centroid to space and summing solid cells × 0.5 m × material density.
    - About 10 µs per room at bake time. Rebuilt with the bake.
  - **Interior dose** = `D · exp(−(σ(dir★) + σ_shield) / σ0)`, with `σ0 = 200 kg/m²`. 1 m of composite at 1,500 kg/m³ attenuates to about 5e-4.
  - The `EnergyShield` component (combat-and-sensors.md, D55; its S class is the `RadiationShield` of earlier revisions) adds `σ_shield` in every direction while it is powered. It also absorbs wall discharges and storm hits as pulses, up to its heat pool. Particle and energy shields are not graviton projectors and are never struck.
  - **Characters** accumulate dose into health. About 5 Gy is lethal, so roughly 100 s unshielded at 1 AU. EVA uses the suit's σ.
  - **Components** have a hardening threshold. Above it they are disabled for `dose / rate` seconds.
  - **Hulls** take damage only in the corona heat zone, as a flat heat rate.
  - Ships in warp, or held in a throat, are immune.
- **Forecast** (`FlareForecast` events to every client in the system; public information):
  - Coarse at onset − 10 min (direction ±15°).
  - Exact at onset − 2 min.
  - The map draws cones with countdowns.
- **Suppression:**
  - Cones forecast by the evaluating tick that overlap `[TOpen, TEnd]` are a hard term, so an ordinary plan can never put a mouth into a known flare.
  - Flares beyond the 10-minute horizon can still sweep a long-open mouth. That is the risk of a long `OpenTicks`.
  - **Interdict catchers waive this term,** and so do natural redirects. A catch point in a forecast cone timed for after open is the intended deadly redirect.
- **Cost:** at most 4 active flares per star. 5,000 entities × 4 × 20 ns at 1 Hz averages about 7 µs per tick.

### Fleet use

- **Sizing:** the leader plans `r ≥ 1.25 · ρ_max(fleet) + 10 m` (or `2ρ_max + 10 m` to keep half-lanes) and `Depth ≥ λ_max + 10 m`. β is picked so that `Σ m_i / F` fits inside `OpenTicks` with margin.
  - Example: 16 ships of 5,000 t, 100 m long with ρ = 50 m, through r = 150 m (`F = 2.25e6 kg/s`), with `Depth` = 60 m.
  - They need 36 s of flux in total, 2.2 s per ship.
  - At 50 m/s with 50 m gaps the queue-slot spacing is 3 s, so the column takes 45 s from first to last queue slot, plus about 1 s for the last tail to clear.
  - Half-lanes fit, since 150 ≥ 110 m.
- **Managed destination:** the leader books one fleet-sized catch slot, or a block of catch slots for several generators (see Managed catchers). The controller's confirmation fixes `TOpen`, and the `MouthQueue` below runs inside it.
- **Queue** (`MouthQueue`, per direction, owned by the entry mouth's server):
  - Queue slot `i` is at `max(slot_{i−1} + ((len_{i−1} + len_i)/2 + gap) / v_t, BusyUntil)`.
  - Default transit speed `v_t = 50 m/s` along `−n`, in the entering half-lane, or alternating batches when half-lanes don't fit.
  - The transit autopilot holds each member at the approach point `A + n_A · (λ + 200 m)` and launches it on its queue slot.
  - A queue slot ends when the tail clears B, and queue slots never end after `TClose − 60`.
  - Queue slots have priority. Unqueued traffic crosses only in the gaps; the hold and bounce cover the rest.
- **Queueing with flow** (natural flow varies, so each queue slot uses `Φ` at its own tick, from the record):
  - **Against the flow:** `v_t ≥ ΔV + 20 m/s`, and at most `VCrossMax`. If `ΔV + 20 m/s > VCrossMax`, the direction is closed and the queue refuses that queue slot (`FlowClosed`), rather than send members into rejects.
  - **With the flow, at the inflow mouth:**
    - The approach point moves out along `n` to where `|a_flow| ≤ 0.5 · a_thrust,min(fleet)`, but no further than `R_f`. Members hold station there under thrust.
    - The autopilot holds `v_t` against the field all the way in, so the queue-slot spacing formula is unchanged.
    - A member whose thrust cannot hold `v_t` in the field goes to the end of the queue, with a ballistic queue slot integrated at booking (a job, ~1k steps, ~20 µs).
  - **Exit speed** with the flow is `min(v_t + ΔV, VCrossMax)`. Exit-lane length and the arrival bubble's `L_fleet` scale by `v_exit / v_t`. The outflow mouth's push clears exit lanes sooner.
- **Arrival bubble:**
  - If crossings are queued by commit, reserve a pooled scene at commit (warp's low-water mark, grown on a worker).
  - At `TOpen`, create the bubble with its origin at `B + n_B · L_fleet / 2`: snapped to 32 m in a surface frame, or FreeFall with `OriginRails` from B's rails state.
  - `L_fleet = Σ len + gaps ≤ 2 km`. Longer columns spill into the normal split and merge.
  - The A side gets the same treatment for return traffic.
- **Formation:** the exit autopilot moves members to the leader's offsets, rotated by `R_B · J · R_Aᵀ`.
- **Throughput caps:** flux F, lane spacing, the 64-per-tick arrival budget, 16 held grids per direction, and the flow's closed direction.
- **Stability and fleets:**
  - The fleet's mass drains the reserve (`e_m · Σ m_i`): 12 % for the 16-ship example above. The leader plans feeders so `Ψ` stays Stable through the column, especially for a tunnel, where Unstable switches cruise control off.
  - Battleships hold their own jumps with their stabiliser kit. Support ships hold smaller ships' wormholes from up to 50 km, outside the column.
  - The queue holds in Critical unless the leader sets power-through. On `WormholeCollapse` it drops every remaining slot, and members in transit pull back.
- **Tunnel fleets:**
  - Entries are spaced by the same queue-slot formula. The column then flies the bore on cruise control at the leader's `v_c`, in its half-lane or in a direction window.
  - Example: the 16-ship column through a 24 km stable bore at 200 m/s. The first ship is out about 129 s after entering and the last about 45 s later. Exits reuse the arrival-bubble rule at B. The last entry must be before `TClose − TunnelLead` (121 s), so the leader buys at least 45 s + 121 s + margin of open time, above the 241 s minimum.
  - Fleets should cross early in the open window. Admission closes at `TClose − TunnelLead`, and a planned collapse at the middle of the bore ejects heavy ships within 1–2 km of `s_c`.
  - Heavy members fight the current worse at high `|Φ|` (see The current). The queue refuses them per ship.

### Seeing through the mouth

| Option | What you see | Frame cost (estimate) | Notes |
| --- | --- | --- | --- |
| Full remote render | A second camera in B's frame (camera pose mapped through `T_AB`), stencil-clipped to the disc, into a half-res offscreen target | GPU 1–2 ms when the disc fills ~20 % of the screen (terrain vertex cost doesn't shrink with resolution). CPU 0.3–1 ms of recording (P4) until indirect draws land. Needs a second terrain focus | Exact and live. Recursion capped at depth 1 |
| Distortion only | Lensing over the local scene, plus B's sky (sun direction and starfield rotated by `T_AB`) | ~0.02–0.05 ms | No ships or terrain at B |
| Cubemap at open, plus live layer | 6 × 256² faces captured at `TOpen` from B's centre, with parallax correction. **Terrain comes from the body impostors every client already has (M2), plus the streamed 2 km ring if the client has it.** Live layer: ships and flares near B (already streamed), drawn through the mapped transform | Capture ~1.5 ms in total, spread over 6 frames (0.25 ms each). Then ~0.05 ms per visible mouth, plus normal draw cost per live ship | No second terrain focus. Static terrain is stale for minutes, which is fine. Moving things are live |

- **Recommendation:** v1 uses distortion beyond 5 km or below 5 % of the screen, and the cubemap plus live layer when closer. This keeps the frame budget flat whatever the mouth count.
- Full remote render is v2. It is only for surface mouths seen within 2 km and above 15 % of the screen, where walking or driving through needs terrain continuity.
- B's 2 km terrain ring is already streamed for those clients (see Networking), so v2 adds GPU cost only.
- The back of each shell is drawn as an opaque throat; the disc is visible only from the front.
- Flow is drawn as streaming distortion in `mouth.glsl`: inward at the inflow mouth, outward at the outflow mouth, scaled by `|Φ|`. It is cosmetic and costs nothing extra.
- Seeing through needs an open mouth. A frame's sensing works through a closed natural throat as well, but returns only counts (see Natural wormholes).
- **Tunnel mouths** show the bore instead of the far side (see Tunnels, Seeing and drawing). From inside a stable bore the far mouth is a small distortion disc at the vanishing point; in an unstable one, the twist hides it.

### Networking

| Event (reliable, tick-stamped) | When | To | Size |
| --- | --- | --- | --- |
| `WormholePlanRequest` | Player plans | Server | ~100 B: generator, A pose, target ref, B local int64, r, `Depth`, β, `Φ_gen`, `OpenTicks`, up to 2 vias (24 B each) |
| `WormholePlanResult` | Reply | Requester | ~48 B: ok or a reason the client can know, L, X, E, `ChargeTicks`, warnings (incl. `GateBusy`) |
| `SlotRequest` | With a plan targeting a managed catcher | Server | ~20 B: catcher, earliest `TOpen`, r, `Depth`, `OpenTicks`, fleet block count |
| `SlotOffer` / `SlotRefuse` | Reply | Requester | ~40 B: catch-slot pose (int64 local, 48-bit facing), `TSlot`, fee, forfeit, `Φ_gate`, hold power; or a refusal reason from the controller's published policy |
| `SlotTaken` | Charge start, when the offered catch slot was confirmed by someone else | Requester | ~44 B: a fresh offer |
| `SlotRevoke` | Before `TCommit − 30` only | Crew | ~8 B |
| `WormholePlan` | Charge start | Crew of the generator host's subtree, plus group | ~150 B: id, the four ticks, `Lca`, A, target and B, vias, r, `Depth`, F, catch-slot ref |
| `WormholeCharge` | Charge start | Interest of A | ~24 B: id, generator, A pose, r, `TOpen` |
| `PathPull` | From 25 % charge, on change, ≤ 1 Hz | Crew | ~12 B per pull: opaque pull id, arc band, strength class, spinning flag, verdict, claimed faction, epoch |
| `PathRead` | Same | Owner of each pulling catcher | ~12 B: charge id, the generator's verdict as the catcher reads it, arc band |
| `WormholeCancel` | Before `TCommit − 30` | Crew, interest of A | ~8 B |
| `WormholeCommit` | `TCommit` | Crew, interest of A and of B, owner of the capturing catcher, managed controller's owner | ~110 B, plus ~48 B per rails mouth for its elements: id, `TOpen`, `TClose`, both mouths (frame, int64 position, 48-bit rotation), r, `Depth`, F, `VCrossMax`, captured `CatcherId`, catch-slot index, `ShardThroatTicks`, Φ, `A_f`, `R_f` and `V_flush` (f16 each); class, `EsMax`, `L_t`, tunnel radius and seed, planned collapse point (+~24 B) |
| `NaturalCommit` | `TNC_k`, redirected cycles only; replayed to late interest joiners | Interest of both charted ends and the new pose, redirecting faction, frame owners on the pair | ~60 B: pair, side, cycle, new frame, pose or elements, faction |
| `MouthDetach` | On leash or host loss | Interest of both mouths | ~60 B: mouth id, new parent, pose or elements |
| Crossing | Each transit | Interest of both mouths | Zones' frame-change event plus `MouthId`, and the exit tick if held (~30 B). Flow rejects use the bounce reconcile |
| `CatcherState` | On change | Owners. Others by sensor contact | ~44 B, including the broadcast signature claim (never the truth) |
| `SignatureReading` | On verdict change, 1 Hz staggered | Reader's crew and group | ~12 B: contact, claimed faction, epoch, verdict |
| `ScanAlert` | Active scan start and stop | Owner of the scanned emitter | ~8 B |
| `KeyRotated` | Rotation | Faction members only | ~8 B |
| `FrameReading` | 1 Hz, on change | Frame owner's group | ~8 B, + 6 B per mass-class bucket, + 12 B per signature reading |
| `NaturalWormholeRecord` | Discovery | Discoverer and party | ~60 B |
| `FlareForecast` | Onset − 10 min and − 2 min | Every client in the system | ~40 B |
| `StabilityState` | On each slope change (≤ 1 Hz per device), on band changes the client cannot derive, and once at the end of every tick in which a discrete drain applied | Interest of both mouths, tunnel occupants, crew, feeders | ~24 B: id, tick, `Es` (int64 kJ), slope (int64 kJ/tick), band. Discrete drains also carry the post-drain `Es` on their own events, but those (crossings, `AgDetonation`) go only to the interest of where they happened, so a tunnel occupant 20 km from both mouths would otherwise miss them and evaluate the field at the wrong `u_q`. Coalesced: at most one per wormhole per tick, ~1.4 KB/s at worst |
| `StabiliserState` | On change | Owners. Others by sensor contact | ~28 B: mode, target, power, broadcast signature claim |
| `WormholeCollapse` | `Tz` (60 ticks before `Tcs`), early collapses only | Interest of both mouths, tunnel occupants, crew, feeders | ~24 B: id, `Tcs`, `s_c` (f32), cause |
| `TunnelDischarge` | Each retaliation strike, wall discharge and storm hit | Interest of the tunnel within 20 km of the target | ~20 B: tunnel, target, tick, kind, component offline. Damage itself travels as ordinary damage |
| Ejection | Each ram or collapse ejection | Interest of the tunnel and of the landing point | Zones' frame-change event with an ejected flag, `f` and cause (~36 B) |

- **Planned collapses send no event:** `TClose`, `V_flush`, `R_flush` and a tunnel's planned collapse point are known from commit (natural pairs: from the record), so clients predict the flush and the collapse flow. Early collapses send `WormholeCollapse` 60 ticks ahead. Flush placements and ejections arrive as ordinary snapshots and reconciles.
- **No per-tick wormhole traffic:** mouth poses come from rails, fixed poses, or the host grid's snapshots. Flow comes from commit or from the record. Stability comes from `StabilityState` and is integrated on the client. The tunnel field comes from the seed, the tick and the stability.
- **Interest across the pair:**
  - From `TCommit` until the last mouth despawns, a client within 20 km of either mouth also gets the other mouth's 20 km region at 10 Hz.
  - Within `A_m` (2 km, or `R_f + 200 m` when larger), it gets B's exit-lane region at full rate.
  - The earliest crossing is ≥ 180 ticks after commit, so interpolation buffers are full before anyone crosses.
  - A client entering the 2 km zone later needs about 1 s of buffer, less than the time to fly 2 km at any sensible approach speed.
- **Terrain:** for a surface-frame B, clients within 2 km of A stream a 2 km collision and LOD ring around B from commit. On a listen server this ring is extra host load (P5).
- **Cost:** about +30 KB/s per client while it is near a mouth. Signature, pull and frame events add under 0.1 KB/s.
- **Prediction (C12):**
  - The owning client predicts its own crossing with the same `PortalMap` test, including the flow term, and the flow field in its own prediction scene.
  - While within 2 km of a mouth, it keeps a second small prediction scene at the partner, built from the streamed region.
  - A server hold, bounce or flow reject arrives as a normal reconcile.
  - The camera crosses on its own plane test, not with the ship's centre, so a chase camera never jumps to the far side early.
- **Privacy rules:**
  - Signature truth (genuine or forged, fidelity, age) never leaves the server.
  - Catch-slot answers depend only on the controller's own policy, bookings and owner's contacts.
  - `PathPull` carries no positions or ids, and starts at 25 % charge.
  - Building near closed natural mouths is never refused; frames need the record.
  - `NaturalCommit` is sent only for redirected cycles and only to the listed parties.

**Dedicated sharding (mouths on different shards):**
- **Shared data:** one global `TickIndex`. The catcher table (with traffic-control rings and redirect targets), the faction key epochs and the mouth rows are replicated to every shard; they are small and change rarely. `HeldSlots` and catch-slot rings are **owned** by the shard of the catcher's frame; replicas are read-only.
- **Commit across shards:**
  - At `TCommit − 300` the A shard chooses the capturing catcher from the replicated catchers (cross-shard capture uses catchers eligible at that tick).
  - It then sends `CommitRequest` (chosen catcher or booked catch slot, quote) to the shard that owns B's frame.
  - That shard chooses the catch slot, resolves the hidden-hull push and the occupancy slide inside the catch volume, and replies (see Capture rule for the over-capacity edge).
  - The A shard announces at `TCommit`.
  - If the reply is still missing at `TCommit`, it announces B unresolved. **B is never moved after the announcement.** The B shard handles conflicts when it creates the shell: small bodies are displaced, and a hull overlapping the shell gets a collision-group exclusion against it until they separate. This is an edge case, for a shard 5 s behind.
- **Catch-slot bookings** are offered and confirmed by the shard that owns the controller's frame. The requester's shard forwards the request and the answer, which takes one round trip within the 30 s offer window.
- **Natural redirects** are resolved by the shard that owns the charted mouth's frame, from the replicated catcher table at `TNC_k − 300`.
- **Frame readings** are computed by the partner mouth's shard and forwarded at 1 Hz.
- **Each crossing is an explicit hand-off:**
  - The entry shard detects the crossing, applies the flow term, and immediately sends the subtree blob, in warp's blob format.
  - The body holds in the throat for a fixed `ShardThroatTicks = 12` (200 ms, announced in `WormholeCommit`), so the exit tick is known at crossing.
  - The exit shard runs the lane, hold and flux checks for its side. If the blob is late, the hold extends in 6-tick steps, but never past the collapse tick; at that tick the collapse flush rule for late blobs applies.
- **Clients:** within 2 km of a cross-shard mouth, a client gets `ShardHandoff {address, token}` and stays connected to both shards.
- **Ownership:** each direction's flux budget belongs to its entry shard, so there is no cross-shard round trip per crossing. Each shard applies its own mouth's flow field and collapse flush.
- **Tunnels across shards:**
  - The tunnel frame and its bubbles are owned by the shard that owns A's frame (natural pairs: the lower `MouthId`'s shard).
  - An entry at B from another shard is a blob hand-off with `ShardThroatTicks`, as for an instant crossing. An exit at B, or an ejection landing in another shard's frame, is a blob hand-off the other way.
  - Clients inside a tunnel connect to the tunnel's shard. Within 2 km of an end they also get `ShardHandoff` for that mouth's shard.
- **Tunnel interest:** occupants get snapshots of tunnel bodies within 20 km along `s`, plus each end's mouth region when within 2 km of it. The field, the current and the collapse fronts are computed locally from the seed, the tick and the stability state. Tunnel traffic adds about the same as a bubble of the same population; there is no per-tick wormhole traffic.

### Data structures (zero alloc)

```csharp
public enum FrameKind : byte { Solar, BodyInertial, BodySurface, Lagrange, Grid, Mouth, Tunnel }   // (frames-and-zones)
public enum PlacementMode : byte { Simulated, Rails, Frozen, Warp, Throat }                        // (frames-and-zones)
public enum WormholePhase : byte { Charge, Committed, Open, Collapsing, Scar }   // a free row = none; Scar = early collapse inside its re-establish window
public enum WormholeOrigin : byte { Player, Natural }
public enum CatcherMode : byte { Gate, Interdict }
public enum GatePolicy : byte { Private, Party, Faction, Public }
public enum MouthMotion : byte { SurfaceFixed, Rails, GridFixed }
public enum SuppressionClass : byte { Natural, Occupancy }
public enum SignatureKind : byte { Noise, Keyed, Copied }
public enum SignatureVerdict : byte { Unknown, Noise, Claimed, Forged, Verified }
public enum SlotRefusal : byte { None, Refused, Full, FeeUnpaid, FlowClosed, GroupCap, SlotTaken }
public readonly record struct WormholeId(ushort Value);
public readonly record struct MouthId(ushort Value);
public readonly record struct CatcherId(ushort Value);
public readonly record struct FactionId(byte Value);              // round 6: owned by factions-and-law.md; 0 = none, 1..31 (was ushort)

/// Component, data-driven (data/wormgen/*.json); sent by id.
public struct WormholeGenerator
{
    public float MaxRadius, MaxDepth, VCrossMax, MaxFlowBias;
    public double ChargeRateW, CapacitorJ, ExoticCapacity, ProjectRange, MinHostMass;
    public int MinChargeTicks, CooldownTicks, MinOpenTicks, MaxOpenTicks, MaxBookAheadTicks;   // 600, -, 1800, 72000, 36000
    public Signature Sig;
}

public struct WormholeQuote { public WormholeClass Class; public double Length, PowerJ, Exotic, ReserveJ; public float PathSuppression, Flux, FlowBias, TunnelLength, MassDrainJPerKg; public int ChargeTicks, Fee; public AdditiveMix Additives; public byte Warnings; }

/// SoA, preallocated: 128 wormholes on a server, 32 on a client. Index = WormholeId.Value.
public sealed class WormholeTable
{
    public WormholeOrigin[] Origin; public WormholePhase[] Phase; public WormholeClass[] Class; public ushort[] Tunnel; public AdditiveMix[] Additives; public int[] Owner, Generator; public ushort[] Group;
    public long[] TChargeStart, TCommit, TOpen, TClose, TEnd;   // TClose = paid maximum; TEnd = latest despawn (tunnels: + L_t / V_f); collapse ticks live in StabilityTable
    public MouthId[] A, B; public FrameId[] Lca;
    public double3[] Via0, Via1; public byte[] ViaCount;           // in Lca, at TOpen poses
    public float[] Radius, Depth, Flux, VCrossMax, FlowRadius; public bool[] HalfLanes;
    public float[] Flow, FlowAccel, FlowDv, FlushDv;               // player: fixed at commit; natural: per tick from the record
    public long[] BusyUntilAB, BusyUntilBA;                         // shared value when !HalfLanes
    public CatcherId[] Captured; public ushort[] CatchSlotRef; public WormholeQuote[] Quote;
    public FactionId[] SigFaction; public Signature[] Sig;         // Keyed: faction fixed at charge start, epoch read live
    public ushort[] FreeIds, Active; public int FreeCount, ActiveCount;
    public void Advance(long tick, FrameTree frames);              // step 2c: commits in (TOpen, Id) order, opens, marks collapses
}

/// SoA, preallocated 256 mouths. Each mouth owns one FrameId of kind Mouth.
public sealed class MouthTable
{
    public FrameId[] Frame, Parent; public MouthMotion[] Motion;
    public double3[] LocalPos; public doubleQuat[] LocalRot;       // SurfaceFixed / GridFixed
    public KeplerOrbit[] Rails;                                    // Rails motion
    public FramePose[] PosePrev;                                   // pose at t−1 in the parent chain: crossing test and transfer
    public float[] Radius, Depth, PhiAtWake; public WormholeId[] Pair; public byte[] Side;   // 0 = A, 1 = B
    public ShapeRef[] Shell; public byte[] Detached;               // refcounted rim + staves + cap compound
    public ushort[] PendingBlobs;                                  // after the collapse: frame kept (no shell, no portal) until 0
}

/// The throat map; pure double maths, ~60 ns.
public static class PortalMap
{
    public static bool Map(in FramePose mouthIn, in FramePose mouthOut, float flowDv, sbyte flowSign, float vCrossMax,
                           ref double3 p, ref double3 v, ref double3 w, ref doubleQuat q);   // false = flow reject
    public static bool Crossing(in FramePose mouthPrev, in FramePose mouthNow, in double3 p0, in double3 p1, float radius, out double fraction); // front edge or in-shell state
}

/// Flow field; pure double maths, no Jolt. ~8 ns per (body, mouth). Central, both sides of the plane.
public static class ThroatFlow
{
    public static double3 Accel(in FramePose mouth, float radius, float accel, float rf, sbyte sign, in double3 p); // 0 beyond R_f
    public static float Phi(in NaturalWormhole n, long tick);                                                    // seeded per cycle
    public static float PlayerPhi(float genBias, float gateBias, double pA, double pBHold, bool targetedGate);   // gateBias only when targetedGate
}
public unsafe struct SceneFlowList { public fixed ushort Mouth[8]; public byte Count; public float3 OriginAccel; public long LastSet; } // per bubble; OriginAccel = value last put in scene gravity

public struct ThroatEntry { public int Entity; public WormholeId Hole; public byte Dir; public long EnterTick, ExitTick;
                            public double3 LocalPos, LocalVel, LocalAngVel; public doubleQuat LocalRot; }   // pool of 256, grids only
public struct QueueSlot { public int Entity; public long Tick, TailClearTick; public float Length, Radius, Mass, TransitSpeed; public sbyte Lane; }  // MouthQueue, per direction, ring of 64

/// Collapse: candidates gathered on a job at Tcs − 60 (early: at Tz) from the wide rings; distances and Δv computed in step 3 of Tcs from end-of-(Tcs − 1) states. Tunnel mouths: on their front's tick.
public struct FlushItem { public int Entity; public MouthId Mouth; public byte Kind; public float PushOut; public double3 Kick; } // Kind: straddler, held, late crosser, kick-only
public static class CollapseFlush { public static void Run(WormholeId id, Span<FlushItem> items, PhysicsScenes scenes); }

public struct Catcher
{
    public int Host, Owner; public ushort Group; public FactionId Faction; public CatcherMode Mode; public GatePolicy Policy;
    public FrameId Frame; public double3 Local; public doubleQuat Facing; public MouthMotion Motion;
    public float PullRadius, GateK, GateFlowBias; public double PowerDrawW, HoldPowerW, Exotic, CapacitorJ;
    public long Start, End; public ushort TrafficControl; public byte HeldSlots;    // 0xFFFF = none; 1 slot without TC; owned by the catcher's shard
    public MouthId RedirectTarget;                                                 // natural mouth; skips path capture
    public Signature Sig;
}   // pool of 1024, replicated to every shard; 1 per host, 4 per group; draw ≤ host CatcherMaxPowerW

/// Traffic control: data-driven rules (data/traffic/*.json) by id.
public struct TrafficControl { public int Catcher, RulesId; public float VolumeRadius, CancelForfeit; public byte SlotCapacity, MaxBookings, MaxBookingsPerGroup; }
public struct CatchSlot { public int Booking, Requester; public ushort RequesterGroup; public WormholeId Hole; public double3 Local; public doubleQuat Facing;
                          public long TOpen, TClose; public float Radius, Depth, LaneLength; public int Fee; } // ring of 32 per TC; doubles as the lane reservation

/// Signatures. Truth fields are server-only; clients only ever receive SignatureVerdict.
public struct Signature { public SignatureKind Kind; public FactionId Faction; public ushort Epoch; public float EmitQ, Fidelity; public long ScannedAt; } // Keyed: Epoch unused (current); Copied: frozen
public struct FactionKey { public FactionId Faction; public ushort Epoch; public long RotatedAt; }                               // server table
public struct SignatureCopy { public FactionId Faction; public ushort Epoch; public float Fidelity; public long ScannedAt; }      // item payload, 16 B
public struct Sensor { public float Quality, Range; }             // round 6: the signature reader component of combat-and-sensors.md (Quality 0.4 / 0.7 / 1.0, range × 0.5 / 1 / 2 for S / M / L)
public struct ReadingState { public int Reader, Emitter; public float Evidence; public long LastClose; public SignatureVerdict Verdict; }  // pool of 4096, 1 Hz staggered
public sealed class EpochSeen { public ushort[] Epoch; }      // [group * MaxFactions + faction], preallocated
public static class Signatures
{
    public static SignatureVerdict Resolve(in Signature e, ushort currentEpoch, ushort epochRef, long tick, float evidence);
    public static float Evidence(float qSensor, float qEmit, float closeness, float member);        // per 1 s evaluation
}

public enum SuppressionKind : byte { BodyCore, BodyTidal, Atmosphere, Star, Grid, WarpDrive, Mouth, Flare }
public struct SuppressionSource { public SuppressionKind Kind; public SuppressionClass Class; public FrameId Frame; public int Entity;
                                  public double3 Centre; public double HardRadius, Mu, SoftScale, FlowRadius; public long From, To; }
public static class Suppression
{
    public static float Evaluate(in double3 pLca, float mouthRadius, float naturalScale, ReadOnlySpan<SuppressionSource> pruned, out bool hard); // naturalScale: 1, k_gate, or 0 (interdict)
    public static float PathMean(ReadOnlySpan<double3> polyline, float r, ReadOnlySpan<SuppressionSource> pruned);   // 64 samples
    public static bool SweepRails(in KeplerOrbit mouth, long from, long to, float r, float depth, ReadOnlySpan<SuppressionSource> pruned); // 32 samples
}

public struct NaturalWormhole { public ulong PairSeed; public MouthId A, B, SpareA, SpareB; public MouthMotion MotionA, MotionB;
                                public float Radius, Flux, VCrossMax, FlowAccelMax, FlushDv;
                                public long Epoch, PeriodTicks, JitterTicks, OpenMin, OpenSpread; public float PsiNatLo, PsiNatHi, RecoverySeconds, MassDrainJPerKg, TunnelLength;   // replaces byte Stability
                                public long RedirectCycle; public FactionId RedirectA, RedirectB; }   // redirect state for the current cycle only
public struct WormholeFrame { public int Grid; public ushort Pair; public byte Side; public float SenseQuality, SenseRange; public long LastSense; } // one per mouth
public struct FlareEvent { public byte Star; public long Onset, End; public float3 Dir; public float CosHalfAngle, Dose1AU; }
public struct ShieldTable { public fixed float Sigma[14]; }   // per room and per component anchor, from the hull bake

// warp.md revision
public struct InterdictionField { public FrameId Frame; public int Owner, Emitter; public double3 Centre; public double Radius, PowerDrawW; public long Start, End; }
public struct ExitReservation { public FrameId Frame; public double3 Pos; public float Radius; public long From, To; } // pool raised to 1024 (mouth lanes and hard spheres); catch slots live in controller rings
```

```csharp
// Revision 3 additions
public enum WormholeClass : byte { Instant, Tunnel }
public enum StabilityBand : byte { Overcharged, Stable, Unstable, Critical, Zero }
public enum CollapseCause : byte { Planned, Throughput, Pulse, Destabiliser, Retaliation, Ejection }
public enum StabiliserMode : byte { Off, Feed, Drain }
// frames-and-zones.md owns: FrameKind gains Tunnel (above); BubbleAnchor { Surface, FreeFall, Grid, Station, Tunnel } (frames-and-zones)

/// Stability, SoA beside WormholeTable (same index). Integers in kJ, so clients integrate exactly.
public sealed class StabilityTable
{
    public long[] EsKJ, EsMaxKJ, SlopeKJPerTick, FeedCapKJPerTick, LastStateTick;
    public StabilityBand[] Band; public byte[] InstabilityQ;                 // u quantised to 1/255
    public float[] PsiNat; public long[] NaturalSlopeKJPerTick;              // natural pairs only
    public long[] Tz, Tcs; public float[] CollapseS; public CollapseCause[] Cause;
    public long[] ScarUntil, RemainingPaidTicks;                             // re-establish window
    public void Integrate(long tick);                                        // step 2c
    public void Drain(WormholeId id, long kj, float s, CollapseCause cause, long tick);   // step 3, in step-3 order
    public void TestZero(long tick);                                         // end of step 3
}

/// Component, data-driven (data/stabilisers/*.json). Pool of 512, replicated like catchers.
public struct Stabiliser { public int Host, Owner; public StabiliserMode Mode; public WormholeId Target;
                          public double FeedW, Range; public float Efficiency; public Signature Sig; }

/// SoA, preallocated: 64 tunnels on a server, 16 on a client. Index = tunnel id.
public sealed class TunnelTable
{
    public FrameId[] Frame; public WormholeId[] Hole; public double[] Length; public float[] Radius, CurrentMax;
    public ulong[] Seed; public bool[] HalfLanes; public sbyte[] DirWindow;   // 0 = both (half-lanes), ±1 = window
    public ushort[] Grids, Smalls; public float[] CollapseS; public long[] Tcs;
}

public struct TunnelOccupant { public int Entity; public float Kappa, RhoPerp, S; public long LastDischarge; public byte Strikes; public sbyte Dir; } // ring of 640 per tunnel

/// Entry, exit and the ejection path map; pure double maths, ~60 ns (path ~1 µs).
public static class TunnelMap
{
    public static void Enter(byte side, double length, ref double3 p, ref double3 v, ref double3 w, ref doubleQuat q);
    public static void Exit(byte side, double length, ref double3 p, ref double3 v, ref double3 w, ref doubleQuat q);
    public static void PathPoint(ReadOnlySpan<double3> polylineLca, in doubleQuat entryAxes, double f,
                                 out double3 point, out doubleQuat axes, out double3 tangent);
}

/// Interior field; pure double maths, no Jolt; identical on server and clients (~100 ns per body).
public static class TunnelField
{
    public static double2 Centre(ulong seed, float r, double s, long tick, byte uQ);            // δ(s, t)
    public static double Radius(ulong seed, float r, double s, long tick, byte uQ);             // R_t(s, t)
    public static double3 Wells(ulong seed, float r, in double3 p, long tick, byte uQ);         // ≤ 3 slots
    public static double3 Current(ulong seed, float r, float phi, float uMax, in double3 p, long tick, byte uQ,
                                  bool collapsing, double sc, float uc);                         // axial + eddies, or collapse flow
    public static bool StormActive(ulong seed, int cell, long tick, byte uQ);
}

public struct TunnelEjection { public int Entity; public ushort Tunnel; public float F; public CollapseCause Cause; public double3 VTunnel; public byte Waited; } // step 3 queue
public struct AdditiveMix { public ushort A0, A1; public float F0, F1; }   // items in exotic-matter.md; effects from data/em_additives/*.json
```

- Planner, commit, catch-slot, path-pull and frame-sensing jobs use per-worker scratch for pruned source and candidate lists (`Span` over preallocated arrays). There are no LINQ, closures or boxing.
- Candidate lists per mouth are preallocated rings: 128 entries for the crossing ring, and a wide ring sized for `A_m`, both refreshed at 10 Hz.
- Signature readings live in a fixed pool. When it is full, the oldest `c = 0` pairs are evicted first.
- Events serialise into pooled send buffers.

### Per-tick cost (estimates, to measure with `--zone-bench`)

| Item | Cost |
| --- | --- |
| Mouth pose (rails or fixed) in the frame cache, plus the previous-tick slot | ~50–100 ns per mouth; 64 mouths ≈ 5 µs |
| Rails shell `MoveKinematic` | ~1 µs per rails mouth per overlapping bubble |
| Natural schedules and flow | ~10 ns per pair, plus ~20 ns per flowing natural pair |
| Candidate refresh (10 Hz, with membership), both rings | ~100 ns per entity near a mouth |
| Crossing test (step 8b) | ~30 ns per candidate. About 100 candidates per busy mouth ≈ 3 µs; 32 open mouths ≈ 30–100 µs |
| Flow force (step 5) | ~10 ns per awake body per flowing mouth in the bubble's list; 500 bodies near 4 flowing mouths ≈ 20 µs |
| Flow scene gravity re-set | ~0.1 µs per bubble, on list changes and at most 1 Hz otherwise |
| Flow wakes (onset, `Φ` change > 0.1, entry into `R_f` at 10 Hz) | ~0.2 µs per woken body, each body once per event |
| Leash test | ~0.3 µs per grid mouth |
| Crossing transfer | ~5 µs per body plus ~60 ns for the map, on the arrival budget |
| Throat hold | ~50 ns per held entity, plus a 1 µs lane test when due |
| Plan (job, rate-limited to 2/s per player) | 20–150 µs, 0 on the main thread |
| Catch-slot offers and confirmations (job, with plans and charge starts) | ~10–50 µs per request, 0 on the main thread |
| Path pull (job, 1 Hz per charging wormhole) | Catcher pool pruned by path bounds, ~5–20 µs per second each |
| Signature contests (1 Hz, staggered) | ~50 ns per active reader–emitter pair; 4,096 pairs ≈ 3.4 µs per tick on average |
| Frame sensing (job, 1 Hz, on the partner's shard) | ~20 µs per frame per second; ≤ 12 frames per system |
| Commit (once per wormhole): path, capture, catch slot, push, slide, rails sweep, reservations, flow | ~60–220 µs, mostly in a job started at `TCommit − 60` (−300 for cross-shard) |
| Natural redirect resolution | ~2 µs per charted mouth with redirect catchers, once per cycle |
| Shell creation at commit, bubble pre-creation at open | ~10 µs, plus pool growth on a worker (~1 ms, started at commit) |
| Collapse flush | Candidates gathered on a job at `Tcs − 60` (an early collapse: at `Tz`). On `Tcs`: ~0.3 µs per kicked body plus ~5 µs per flush placement; 200 kicked + 20 placed ≈ 160 µs on that tick, once per wormhole |
| Flare dose, 1 Hz staggered | ~7 µs per tick on average |
| Client: suppression map shader | ~0.05 ms, panel open only |
| Client: mouth view (v1) | ~0.05 ms per visible mouth, plus 0.25 ms per frame for 6 frames at open |
| Stability integration | ~2 ns per wormhole per tick. Feeder membership and power at 1 Hz, ~0.2 µs per device |
| Tunnel field (twist, radius, wells, current and eddies) | ~100 ns per awake body in a tunnel; 200 occupants ≈ 20 µs |
| Tunnel scene gravity re-set | ~0.1 µs per tunnel bubble, on a `u_q` change and at most 1 Hz otherwise |
| Tunnel wall and exit tests | ~30 ns per occupant |
| Cruise control | ~50 ns per autopiloted ship |
| Retaliation and storms | 1 Hz: ~0.1 µs per projecting device in a tunnel, ~20 ns per occupant for storm cells |
| Ejection (ram or collapse) | ~10 µs per body (path point, band rule, push, transfer), on the arrival budget |
| Collapse fronts | ~5 ns per occupant per tick while collapsing |
| Client: tunnel interior | ~0.15 ms GPU at 1080p while inside or within 2 km of a tunnel mouth, plus the same field cost for its own prediction |

**Tick order (additions to zones and warp):**
- **2c. Wormholes,** after warp's 2b: phase changes, commits in `(TOpen, Id)` order (including catch-slot placement and flow), opens (with flow onset wake and immediate scene gravity re-set), collapse marking, natural schedules, and natural commits with redirect resolution.
  - A wormhole commit sees same-tick warp commits as suppression.
  - A warp commit sees wormholes committed on earlier ticks, and confirmed catch slots from their charge start.
  - Both orders are deterministic.
- **3. Transfers:**
  - At `Tcs`, the instant **collapse flush** runs first: straddlers and last-tick crossers leave by the nearer mouth, held bodies by B, then the outward kick, then despawn and the scene gravity re-set.
  - Then crossings queued last tick, departures before arrivals, then throat exits due, bounces and flow rejects, all in `(MouthId, EntityId)` order.
- **5. Forces:** also the rails shells' `MoveKinematic`, the flow deltas on awake bodies (against the stored `OriginAccel`), and drift re-sets of scene gravity.
- **8b. Mouth crossings and leash,** after the grid poses are written.
- **9.** Candidate refresh with the 10 Hz membership checks, and the entry-into-`R_f` wakes. At 1 Hz, staggered: flare dose, signature contests, path pulls and frame sensing (the last two as jobs).
- **Revision 3 additions to the tick order:**
  - **2c:** stability slopes integrate, natural relaxation and overcharge leak included; scheduled `Tcs` collapses start (instant flush flagged for step 3; tunnel collapse flow switches on).
  - **3:** discrete drains as their events resolve (crossings, detonations, strikes, ejections); tunnel entries and exits with the other crossings; queued ejections; collapse-front ejections, then mouth flushes for fronts that arrived; the zero test at the end.
  - **5:** tunnel well deltas and current coupling on awake occupants; cruise thrust.
  - **8b:** tunnel exit tests, wall tests (graze discharges at once, rams queued for step 3).
  - **3 (also):** retaliation strikes, on the tick a projection starts (detected in step 8b of the previous tick) and every 60 ticks while it lasts, in `(Tunnel, EntityId)` order.
  - **9:** at 1 Hz, staggered: feeder membership, storm hits and tidal damage.

### Changes to warp.md and frames-and-zones.md

- **FrameTree:**
  - Add `FrameKind.Mouth`, with frames that are fixed or on rails, and one parent change allowed at detach.
  - The frame cache keeps a previous-tick pose for mouths. A collapsed mouth's frame stays until its pending cross-shard blobs land.
  - The portal map is **not** a tree edge, so `Relative` is unchanged.
  - Wormhole frame grids may have a `Mouth` frame as their parent.
- **Anchors:** a Forming or Open mouth is a physics anchor out to `A_m = max(2 km, R_f + 200 m)`, with shedding at `A_m + 1 km`. Membership checks skip `Throat` placements, as they skip `Warp`.
- **Gravity and forces (zones' scheme, C2):**
  - Scene gravity adds `Σ a_flow(origin)` for flowing mouths overlapping the bubble, stored as `OriginAccel`.
  - The per-awake-body delta adds `m · (Σ a_flow(p) − OriginAccel)`.
  - Never `AddForce`.
  - Re-set scene gravity on the tick the flowing-mouth list changes. Wake bodies once at onset, on a `Φ` change of more than 0.1, and on entry into `R_f`.
  - `CharacterVirtual` up uses `ĝ` including flow. Interior gravity counts the flow as gravity.
- **Placement:** add `PlacementMode.Throat`, plus `GridInertial` while held. Reset the proper-acceleration history on every crossing, bounce, flow reject, flush placement and collapse kick.
- **Contacts and sensors:** add a `Sensor` component, and contacts carry a `SignatureVerdict`. Factions (`FactionId`) are new: a set of groups that holds a key.
- **Warp:**
  - Validation step 2: refuse a warp whose carried subtree contains a mouth host.
  - The commit occupancy test includes mouth exit lanes and hard spheres (natural mouths over their scheduled windows) as reservations, and confirmed catch slots through the catcher table. The `ExitReservation` pool grows to 1024; catch slots never use it.
  - Spools past lock-in and committed exits become suppression sources.
  - `InterdictionField` gains `Emitter` and `PowerDrawW`, with `End` derived from the capacitor. Its emitter carries a signature.
  - Warp placements never cross mouths and are never kicked.
- **Hull bake:** add `ShieldTable` per room and per component anchor. Collapse no longer needs a kerf or hull splitting.
- **Revision 3:**
  - **FrameTree:** add `FrameKind.Tunnel`, a root beside `Solar`, refused by `Relative`. Bubbles get `BubbleAnchor.Tunnel` (tunnel axes, wells as scene gravity, origin moving with its cluster along z).
  - **Gravity and forces:** tunnel bubbles use the same scene-gravity-plus-delta scheme for wells. The current coupling is a per-awake-body force in the same pass, without activation.
  - **Placement:** every root entity inside a tunnel is `Simulated`. `Throat` placement is used for tunnel traffic only for cross-shard hand-offs and ejections waiting on the arrival budget.
  - **Warp:** no spool inside a tunnel. Ejections use `ArrivalBooking` and warp's exit push.
  - **exotic-matter.md:** the mouth stall, its cap and its immunity are superseded by the pulse drain; its open question 2 is answered (early collapse through stability). Gravity dampers covering a mouth block pulse drains. Add a stabiliser device row (power only, per decision A). Its per-class "cheaper" table now prices tunnel class, and (round 5, D23) is a single-ship rule, not a blanket "warp cheaper" invariant. Its black-hole suppression terms are mirrored in the suppression table here. Additive items and mixing belong there.
  - **ship-hulls.md:** the bake exports `ρ_⊥` and `A_⊥` for the current coupling. Discharges and rams are ordinary damage brushes.
  - **README:** the Travel row gains classes, stability, tunnels and early collapse.
- **Milestones:**
  - **W1 (after M7):**
    - instant player pairs with seated crew and a generator on a station;
    - fixed and rails mouths, shells, crossings, hold, bounce, `VCrossMax` and leash;
    - flow from generator bias only, with the field, throat `ΔV` and flow rejects;
    - the collapse flush. The whole-body, nearer-mouth flush is the final rule for instant pairs, not a stand-in;
    - the stability reserve with generator feed, mass drain, early collapse at `Tz + 60`, scars and re-establish.
  - **W2 (after M8):**
    - walking through;
    - catchers, including traffic control and catch-slot booking, and hold power;
    - signatures, copies and key rotation;
    - flares (need rooms for `ShieldTable`);
    - stabilisers (Feed and Drain) and pulse drains;
    - **tunnel class with calm bores:** the tunnel frame and bubbles, the maps, the current and its coupling, cruise control, lanes and direction windows, the planned collapse flow and ejections;
    - cross-shard.
  - **W3:**
    - unstable interiors: twist, wells, eddies, storms and tidal damage;
    - wall discharges, rams and ram ejection;
    - combat in tunnels: retaliation and ricochets;
    - early collapse flow with cause-based collapse points;
    - natural pairs as tunnels, with seeded flow, natural stability, redirection and frames;
    - additives.
  - **Exit checks:**
    - relative velocity through a pair of instant mouths moving 3 km/s apart is kept within 1 mm/s (Φ = 0);
    - arrivals through a grid mouth moving at 100 m/s land within 1 cm of the mapped point;
    - a 16-ship queue crosses with no overlaps, including against a flow of `ΔV` = 0.5 · `VCrossMax`;
    - same-tick two-way crossings never overlap;
    - a stacked hover pair never takes a crate past `VCrossMax`, with or without flow, either class;
    - with `ΔV ≥ VCrossMax`, no body crosses an instant pair against the flow, including on the last tick;
    - a crate released at rest inside `R_f` never exceeds `VCrossMax / 2` from the field, and a guided closed loop around a mouth gains no energy;
    - the flow never wakes a sleeping body except at onset, on a `Φ` change of more than 0.1, or on entry into `R_f`;
    - after the collapse, no body straddles a plane, no hull has been cut, no unplated interior logs a spike from the kick, and no body is left in a tunnel;
    - booked catch slots never overlap warp arrivals or each other, and never consume the global reservation pool;
    - a forgery is never Verified, and verdicts are identical across reruns;
    - client and server stability agree to the kJ, and the tunnel field bit for bit, over a 20-minute fight;
    - a 16-ship column flies a stable 24 km bore on cruise control with no wall contact;
    - commit costs under 0.2 ms on the main thread, the flush tick under 0.25 ms, and 200 tunnel occupants under 0.05 ms per tick.

### Files

- **`src/Frames/`** (no Jolt):
  - `WormholeTable.cs`, `MouthTable.cs`, `PortalMap.cs` (with the flow term), `ThroatFlow.cs`, `Stability.cs` (reserve, slopes, bands, drains, zero test, scars, re-establish), `Stabilisers.cs`;
  - `TunnelTable.cs`, `TunnelMap.cs` (entry, exit, ejection path map), `TunnelField.cs` (twist, radius, wells, current, eddies, storms);
  - `WormholePlanner.cs` (path at `TOpen`, core detours, quote);
  - `Suppression.cs` (classes, rails sweep, natural windows), `Catchers.cs`, `TrafficControl.cs` (catch-slot layout, offers, confirmation, rules);
  - `NaturalWormholes.cs` (MOID validity, seeded flow, redirect resolution per faction);
  - `StarHazards.cs` (flare schedule, cone test, occlusion).
- **`src/Sensors/`** (no Jolt):
  - `Signatures.cs` (evidence, verdicts, copies, ageing, epochs);
  - `PathPull.cs`;
  - `FrameSensing.cs`.
- **`src/Physics/`:**
  - `MouthCrossings.cs` (candidates, detection, queue, lanes, throat hold, bounce, flow reject, leash);
  - `MouthShell.cs` (rim, staves, cap; static, kinematic or host sub-shapes; displacement at creation; natural hull push-out and static-grid exclusion);
  - `FlowForces.cs` (scene term, stored origin value, per-awake-body delta, wakes);
  - `WormholeCommit.cs` (capture, catch-slot placement, push, slide, reservations, flow, hold power, bubble pre-creation);
  - `CollapseFlush.cs` (replaces `Severance.cs`);
  - `RadiationDose.cs`;
  - `TunnelBubbles.cs` (tunnel anchor, occupants, caps, lanes and direction windows), `TunnelForces.cs` (well deltas, current coupling), `TunnelCruise.cs`, `TunnelWall.cs` (graze, discharge, ram, retaliation, ricochets, storms), `TunnelEject.cs`, `TunnelCollapse.cs` (collapse point, flow, fronts, mouth flush on arrival).
- **`src/Ships/`:** `ShieldBake.cs`, `WormholeFrameBuild.cs` (record check, rim coverage, one per mouth, keep-out validation).
- **`src/Net/WormholeEvents.cs`:** includes the cross-shard commit, capture-claim, catch-slot, frame-reading and transit blobs, and the signature events.
- **Client:**
  - `src/Client/Wormhole/MouthView.cs` (distortion, flow streaming, impostor cubemap capture, live layer, crossing prediction, camera crossing);
  - `src/Client/Wormhole/TunnelView.cs` (bore mesh, wells, storms, field prediction, collapse fronts);
  - `WormholePlanPanel.cs` (suppression overlay, quote, catch-slot offer, catcher warnings merged with `PathPull`, flow and one-way flags);
  - `SignaturePanel.cs` (verdicts, scans, copies);
  - `FrameReadingPanel.cs`;
  - `FlareForecastPanel.cs`.
- **Shaders:** `mouth.glsl` (flow streaming added), `suppression_map.glsl`, `tunnel.glsl`.
- **Data:** `data/wormgen/*.json`, `data/catchers/*.json`, `data/traffic/*.json` (controller rules, fee tables, forfeits, per-group caps), `data/sensors/*.json`, `data/stars/*.json` (flare parameters), `data/stabilisers/*.json`, `data/em_additives/*.json` (wormhole effects of additives).
- **Profiler:** `ProfilerPanel` rows for Mouth poses, Shells, Crossings, Throat, Flow, Flow wakes, Commits, Plans, Slots, Path pulls, Signatures, Frames, Flush, Dose, Stability, Tunnel field, Tunnel wall, Cruise, Ejections, Collapse fronts, Retaliation.
- **Tests:**
  - `PortalMap`: an A→B→A round trip is the identity at Φ = 0; relative velocity is kept for rotating, rails and grid mouths; front → front only; the flow term adds `±ΔV` and clamps with the flow.
  - Transfer uses poses at the crossing tick (grid mouth at 100 m/s).
  - Crossing: no tunnelling at 300 m/s; a spinning grid mouth is detected; the state test catches a body stopped in the shell; a rebase between ticks fakes no crossing.
  - `VCrossMax`: a stacked hover pair loop stays capped, with and without flow.
  - Flow:
    - against-flow crossing is impossible at `ΔV ≥ VCrossMax`, including a crossing detected at `TClose − 1`;
    - a flow reject does no damage;
    - the field's potential bound holds, and a guided loop in front of and behind the cap gains no energy;
    - awake bodies get exactly `a_flow(p)` between throttled scene-gravity re-sets;
    - no `AddForce` wakes;
    - a sleeping body is woken once at onset, once on a `Φ` reversal, and once when a moving mouth's field reaches it;
    - two flowing mouths can never be committed with overlapping fields;
    - natural flow is exact at ±1 tick over 1e9 ticks.
  - Two-way: same-tick crossings in both directions never overlap; no half-lanes when `r < 2ρ + 10`.
  - Hold: loose items never take hold places; admission-failure bounces do no damage.
  - Shell: an item inside the shell volume at commit is displaced; a ship parked beside the rim is never moved by the flush placement.
  - Leash: a grid mouth detaches before an unpermitted exclusion zone; a carrier holding a mouth host can't warp.
  - Capture:
    - first-along-path wins, with the tie order;
    - deterministic across same-tick commits;
    - interdict never lands in an exclusion zone without permission;
    - an interdictor before a managed gate wins, and the fee is refunded;
    - `PathPull` lists a catcher whose spin-up ends exactly at `TCommit`;
    - a cross-shard claim on a full catcher is placed by the slide fallbacks, never overlapping.
  - Catch slots:
    - an offer reserves nothing; a second confirmation of the same catch slot gets `SlotTaken`;
    - a confirmed booking is respected by a warp commit and uses no global reservation;
    - `MaxBookingsPerGroup` and `CancelForfeit` apply; revocation refunds fully and starts no cooldown;
    - an intruder in a catch slot slides B inside the volume;
    - an unpowered controller or a busy plain gate falls back to the push;
    - revocation after `TCommit − 30` is refused;
    - a stretched charge keeps the four ticks fixed.
  - Player flow: a gate with no hold power adds no flow; `Φ_gate` never applies to a capture.
  - Signatures:
    - a stale epoch is flagged at once after the reader sees a rotation;
    - a genuine faction wormhole opened before a rotation is never flagged by it;
    - a forgery is flagged no later than evidence 0.95, and an older copy is flagged sooner;
    - evidence gathered at the edge of range plus one close pass equals its integral, not a full close-scan verdict;
    - genuine emitters reach Verified;
    - noise is never Claimed;
    - path pulls and frames never produce copies;
    - verdicts are identical on rerun.
  - Privacy:
    - no plan result depends on hidden hulls, hidden catchers, unforecast flares or uncharted natural mouths;
    - catch-slot answers ignore server-only knowledge;
    - `PathPull` carries no positions and starts at 25 % charge;
    - building near a closed, uncharted natural mouth is never refused; a frame without the record is.
  - Collapse flush:
    - held and crossed bodies leave by B, uncrossed straddlers and last-tick crossers by A;
    - no body straddles a plane after `TClose`;
    - the kick falls off as specified and sums for a body near both mouths;
    - kicked grids reset their proper-acceleration history;
    - a cross-shard blob arriving seconds late is placed at B's pose at its landing tick.
  - Natural: schedule exact at ±1 tick over 1e9 ticks; Forming inside its cycle; MOID validity; a player commit near a mouth about to form is pushed; redirect contest order per faction and `P_redirect` threshold; the redirected mouth reverts next cycle; late interest joiners get `NaturalCommit`.
  - Flare occlusion behind a moon.
  - Cross-system: a plan with ends under different `Solar` roots is refused; a cross-system natural ram lands on the nearer leg inside one system, identical on clients.
  - Invariants (D23): crossing a natural pair costs no EM, fuel, opening energy or game fee; the cheapest (tunnel-class) wormhole passes exotic-matter.md's per-class single-ship check; instant pairs are faster than warp from 20,000 km; tunnel pairs with the reference transit are faster from 1 AU.
  - Stability:
    - client integration equals the server's to the kJ over 1e6 ticks of random feeds and drains;
    - a zero at `Tz` refuses admissions from `Tz`, sends `WormholeCollapse` and collapses at `Tz + 60`, and a feed in between does not revive it;
    - the feed cap and the overcharge leak hold; natural relaxation reaches `Ψ_nat` and pulls overfeeding back;
    - the mass drain is charged once per entry, by the entry shard, never on exit;
    - the pulse drain follows its falloff and rings down over 300 ticks in exact integer kJ; a damper over a mouth blocks it; a prepared r = 100 m reserve survives 22.5 GJ and an unfed full one empties at 10 GJ; destabiliser drain stops at the feed cap; no stall remains;
    - a player wormhole with no feeders or drains lives exactly to `TClose`;
    - queue slots after `Tz` are dropped, and catch-slot holds end after the scar window.
  - Re-establish: costs 0.25 X (and 0.25 of the additives); keeps the remaining paid life; runs no capture; scars block other wormholes and not their own; a lapsed scar starts the cooldown; a re-formed tunnel has the same geometry.
  - Tunnel maps: A → B through `L_t = 0` equals `J`; entry then exit is the identity; half-lanes map `x_a > 0` to `x_t < 0` and out into B's `x < 0` half.
  - Tunnel field: identical on server and client; `u = 0` gives a straight cylinder with no wells or eddies; the wall never moves sideways faster than 15 m/s; the radius never drops below `0.85 r`; wells appear one by one as `u` rises.
  - Current: terminal speeds match `u ± a/κ`; one-way appears exactly at `a/κ < |Φ|·U_t + 10 m/s`; a C-class ship rides and a capital ship lags; per-ship `FlowClosed`.
  - Cruise control: a stable-bore transit is within 2 % of the estimate; it disengages 3 s after a drop to Unstable.
  - Wall: grazes reflect and discharge at most once per 30 ticks; a ram at `V_ram` ejects; a body pinned for 2 s ejects.
  - Ejection: lands at `P(f)` plus its lateral offset within 1 m, with the drop-rule velocity; never in solid or terrain; never in a non-permitting exclusion zone; may land in a corona or atmosphere (logged); waits in `Throat` placement when the arrival budget is full and is force-placed after 8 ticks; items, rounds and debris are never ejected; a rammed grid cannot spool or charge for 30 s.
  - Lifetime: no mouth, shell, occupancy term or lane reservation outlives `TEnd`; a tunnel collapsing from an end at `TClose − 1` despawns by `TEnd`; a zero at `Tz ≥ TClose − 60` runs the planned collapse; a natural collapse never overlaps the next cycle's Forming.
  - Mass: an entry over `MaxEntryFluxTicks` bounces; a mass dump at full flux never outpaces one feeder; on one shard mass never collapses a wormhole.
  - Sleep: a body at rest in a calm bore reacts on the tick a well, eddy or collapse flow appears under it; occupants riding the current after a Galilean rebase feel the same coupling as before it.
  - Retaliation: a limpet projector on a foreign hull damages only itself; strike drain stays within 1 %/s per wormhole; strikes escalate per owner group.
  - Combat: a tractor in a tunnel is struck on its first tick and its lock breaks; strikes escalate; plating, compensators and a Feed stabiliser are never struck; an armed charge is struck and detonates on the next tick; kinetic rounds reflect specularly at `u = 0`, lose 40 % per bounce and are absorbed after 3.
  - Collapse flow: escape thresholds match the analytic `D` within 2 %; every occupant has left or been ejected by the time the last front reaches its mouth; `s_c` follows its cause; each mouth flushes on its front's tick.
  - Tunnel admission closes at `TClose − TunnelLead`; a plan with `OpenTicks < 2 · TunnelLead` is refused; `TunnelCap` and the 32-grid per-group cap bounce without damage; small overflow absorbs the oldest uncrewed smalls.
  - Natural tunnels: `L_t` and `Ψ_nat,k` from the seed; the ejection path computed at `TNC_k` is identical on clients.
  - Classes: instant pairs cost 2.5× X and 1.5× opening energy; the class is fixed at charge start and kept by a re-establish.
  - Additives: the blend meets `MinPurity` on true content; effects scale with fraction to their caps; trade-offs apply.

### Decided after revision 2

- **A captured wormhole opens straight onto the captured destination.** The view through the mouth is always true; there is no faking. Open question 1 of revision 2 is settled this way.
- **Interdiction never forces travel.** It only moves the far end. Getting a victim through takes a physical method:
  - **Tractor beams:** a component that applies force between two hulls, through the normal per-body force pass. Force limits, ranges, modes and billing are tractor-beams.md's (the owner). A tractor lock already blocks warp spool, so it is both anti-escape and pull-in. The lock breaks when the victim crosses (see the crossing rules above), so the tractoring ship stays behind unless it follows.
    - **Consent (round 5, D26; owned by tractor-beams.md "Consent (D26)", which adopts this text):** a tractor lock on a ship that did not request it is a **weapon** use: a hostile act, with the same standing, fines and faction consequences as firing on it. It writes an `IncidentRecord` of kind `TractorLock` (the shared record, owned by factions-and-law.md) on acquire, even with no damage. Pulling an interdicted victim toward a mouth is therefore always an attack. A managed gate or station may tractor a ship to a dock or into its mouth's entry lane only when the ship's crew has **requested** it (a consent flag on a docking or tow request; catch slots place wormhole ends, not ships, so a `SlotRequest` carries none). Otherwise ships manoeuvre themselves.
      - **Scope:** consent names one emitter owner group and lapses on release, on docking, on the ship crossing a mouth, or after 120 s without a lock. A third party locking a consenting ship is still a weapon use, and a ship cannot grant consent to stage a fake attack (the server judges by the flag, not by claims).
      - Inside a tunnel the wall strikes any active tractor, requested or not (see Tunnels, retaliation), so consent never lets a gate tow a ship through a bore.
    - Without antimatter a tractor can only **pull** (gravitons only); pushing needs anti-gravitons, so AM (round 5, D28). Without AM, Hold is a pull-only tether: it limits range and reels in but cannot stop a target closing; it still blocks warp spool. Pull-in is the interdiction use either way.
  - **Flow:** a captured wormhole with an inflow pulls nearby ships toward the mouth; a ship with enough thrust can escape.
  - Ordinary physics: rams, pushes, debris.

### Decided in round 5 (formerly open questions)

The user accepted the doc's proposals (round 5, D19). Where a question had no explicit proposal, the design as written is the decision. Every former question is now a decided rule; numbering is kept so older cross-references still resolve.

Answered earlier and dropped: old question 4 (exotic matter's source: settled by exotic-matter.md's decisions), and exotic-matter.md's question 2 on bombs (settled by decision A).

1. **Seeing a capture:** decided after revision 2 (the view is always true).
2. **Outflow push:** the outflow mouth pushes space out with the same law as the inflow pull.
3. **Counterplay to catchers:** the set as written is enough: charging faster than a catcher's 30 s spin-up, routing, cancelling, decoys, reading pulls, and rotating keys. There is no "stiffen the path" purchase that shrinks catchers' pull radius. (The stability additive only enlarges the reserve against drains after opening; it does not affect capture.)
4. **Exotic matter source:** answered by exotic-matter.md.
5. **Other star systems:** natural wormholes lead to other star systems (decided earlier in round 5). This means several `Solar` roots, a shard per system, and a natural pair whose two mouths may lie in different systems (see Natural wormholes). Manufactured plans need both ends under one `Solar` root, so only natural pairs cross between systems; a cross-system tunnel ejects along one leg per system.
6. **Mouths on moving ships:** a mouth may stay mounted on a moving ship while open, leashed, and the host cannot warp while any mouth is mounted on it (until that mouth despawns or detaches; warp's validation refuses a carried subtree containing a mouth host).
7. **How close to a planet may a big mouth open:** as written, `A_max = 3e-3`: an r = 100 m mouth may open down to 94 km over a 1 g, 1000 km planet, and r = 400 m down to 736 km.
8. **Interdictor friendly fire:** interdict catchers skip their own group's paths (not the whole faction's).
9. **Player flow from power asymmetry:** only a catcher that spends **hold power** above the generator's charge rate creates an asymmetry. Plain power ratings do not count.
10. **Path pull reach:** as written. `PathPull` reveals that hidden catchers exist, with an arc band and a signature, from 25 % charge.
11. **Generator spoofing:** generators, frames and stabilisers may load copied keys too, by the same mechanism as catchers.
12. **Natural redirect reach:** a local drag within `ProjectRange` of the charted mouth.
13. **Frame defence:** a powered frame adds **no** power against redirects of its own mouth (as written: a frame is held by the weapons on it, with no special rules). Frames **may** carry stabilisers to hold their natural mouth's reserve.
14. **Fees:** paid in throat-grade exotic matter, as exotic-matter.md suggests. `Fee` fields hold throat-grade EM content in integer grams (1 EM = 1 kg of content, exotic-matter.md), escrowed and refunded as stacks that meet throat grade. Fees apply only to managed gates catching a generator's end; natural crossings carry no game fee.
15. **Natural size:** 150–1,200 m (median ~500 m), as written.
16. **Copy freshness:** copy ageing is kept (half-life 6 h), on top of key rotation.
17. **Projected stabiliser fields in a tunnel:** a stabiliser on Feed inside a tunnel pours into the throat and is not struck. Battleships keep feeding while they cross.
18. **Stabilisers' cost:** power only (per decision A). They burn no field-grade EM.
19. **Energy-fight scale:** as written. `EsK` 10 GJ·r̂², pulse drain 1 GJ per kg of yield, feed cap `EsMax / 5 s`, destabilisers at half efficiency. A 10 kg-yield charge at point blank collapses an unfed r = 100 m wormhole; a prepared one (overcharged, fed at the cap) survives 22.5 kg. Two Drain kits cancel one Feed kit at r = 100 m and more add nothing; bombs break the hold. Values stay tunable data.
20. **Re-establish:** a 60 s window, 0.25 X, the remaining paid life kept, and **no capture** at re-commit because the path is drilled. Catchers get no second chance.
21. **Deliberate rams:** a ram is a bail-out to a known point along the path, paid in damage at `u = 1` strength and a 30 s warp and charge lockout.
22. **Tunnel length:** `LtK` = 2 km per unit of ℓ, as written. Tunnels are slower than warp below roughly 0.7 AU and faster beyond (default warp drive, minimum open time). This fits round 5's roles (D23): close, specific trips are warp's niche, and the instant class is the fastest on-demand option within a system from 20,000 km (see Cost model, tuning invariants).
23. **Class prices:** instant at 2.5× exotic matter and 1.5× opening energy over tunnel, as written. Instant is the premium, fastest manufactured class.
24. **Collapse severity:** fronts at half the collapse flow, as written. A C-class ship escapes from 160–330 m ahead of the collapse point; a 78,000 t capital ship needs 1.2–2 km.
25. **Ejection velocity:** half the tunnel-frame velocity is kept, rotated onto the path, on top of warp's drop-rule orbit at the landing point.
26. **Tunnel open time:** a tunnel must be quoted open for at least twice its admission lead (`2 · TunnelLead`, 241 s for a 1 AU bore at `V_adm` = 200 m/s), as written. Admission is not per ship.

There are no open questions left in this doc. All costs are estimates. Nothing was built or run, and no repo files were changed.

### Review notes

**Violations of the user's decisions:**
- **Interdict waived all suppression, including hulls, exclusion zones, warp drives and mouths.**
  - The user's rule is that suppression stops wormholes in *invalid or deadly* places, and interdiction redirects to *deadly* ones.
  - The draft let an attacker put an interdict catch point inside an enemy base's exclusion zone and capture their own fleet's path, dumping the fleet into the base.
  - Fix: terms are split into Natural (deadly) and Occupancy (invalid) classes. Interdict waives Natural only, exclusion protection is the default for every grid, and the exit lane must be clear for 2 km so a facing aimed at the ground is not a no-reaction kill box.
- **Wormholes were not faster within an SOI.**
  - With `A_max = 4.9e-4`, an r = 100 m mouth could not open below 1,000 km. A planet → moon-surface trip then needed a warp last hop and took longer than warping directly.
  - Retuned to `3e-3`: r = 100 m at ≥ 94 km, r = 400 m at ≥ 736 km. The speed comparison is now in the cost table, and question 7 makes it a knob.
- **"More expensive than warp" was never checked.** Added as a tuning invariant with a test.
- **The near end was never checked against suppression.**
  - A mouth A could be created in an atmosphere, in a flare, or in someone's exclusion zone.
  - Both ends are now checked over `[TCommit, TClose]`. The host's own hull is exempt only for its own mounted mouth.
  - The draft's hull term also contradicted A's own projector mount.
- **"Once spent, it will happen" had holes:**
  - The cross-shard late path moved B after it was announced. B is now never moved; conflicts are handled when the shell is created.
  - "Destroying the generator before then cancels it" was ambiguous against the 30-tick lock-in. Now: before `TCommit − 30` only.
  - Cross-shard holds could run past `TClose`. Every hold is now released at `TClose`.
  - Host loss used the drop rule even below the rotating band. Now: hover below `RotatingTop`, rails above.
- **Two-way traffic:** half-lanes cannot separate traffic at the draft's `r ≥ 1.25ρ + 10 m`, because a ship of radius ρ does not fit in half the disc. They now need `r ≥ 2ρ + 10 m`; otherwise the queue alternates. Safety comes from the lane test and the hold.
- **Natural "fixed":** natural mouths may now also be fixed in a surface frame, not only on rails, matching the user's "fixed". Never moved either way.

**Physics and precision bugs:**
- **Two-sided discs:**
  - Back-to-back traversal gave four flows over one disc.
  - In v1, a straddling ship's nose (logically at B) still collided with real bodies behind A's plane.
  - Replaced by a one-sided mouth with a capped throat shell of depth `λ_max + 10 m`. Straddling parts live where nothing else can be.
  - `Depth` also fixes long ships: the draft sized only by cross-section radius.
- **Transfer poses:** the draft mapped with poses at `t` but ran in step 3 of `t + 1`, without saying which pose and bubble origin it used. Both are now explicitly at `t`, with a previous-tick slot. The alternative put arrivals one tick of mouth motion off, into the shell wall or behind the plane.
- **Crossing test:**
  - "Exact for a spinning mouth" was wrong; the error is the rotation's sagitta, ~7 mm.
  - It used scene coordinates, which a rebase can shift between ticks; it now uses frame-relative doubles.
  - It missed bodies that stop with their centre in the throat; a state test was added.
  - Placement at exactly `z = 0` could re-cross; `z_b ≥ 1 cm`.
- **Same-tick crossings:** departures are now processed before arrivals, and each arrival's lane test sees earlier arrivals in the batch. Otherwise opposing crossings on one tick could overlap. A full arrival budget sends the body to the hold rather than deferring it silently.
- **Severance** (since replaced by the collapse flush):
  - "Bounding sphere cuts the disc cylinder" would cut ships parked beside the rim. Now only hulls with cells inside the shell are affected.
  - A zero-width half-space subtract cannot disconnect a hull; a 0.25 m kerf slab can.
  - Severance now ran before queued crossings at `TClose`.
  - Admission now uses tail-clear time, not centre time, so ships admitted near the end are not caught.
  - The exit check "exactly two pieces" became "no piece straddles".
- **Shell creation:** small bodies inside the shell or disc at commit are displaced, instead of being depenetrated violently at open.
- **Infinite-fall accelerator:** relative velocity is preserved, so a stacked hover pair gains `g·Δh` per pass, reaching km/s in 20 minutes. `VCrossMax` caps it at 300 m/s, and bounces count as impacts.

**Exploits:**
- **Ship-mounted mouths:**
  - A kinematic rim on a moving ship was an infinite-mass ram. The shell is now part of the host's compound.
  - After commit, a host could fly its mouth into an enemy hangar or an atmosphere. The leash now detaches the mouth at hard suppression.
  - A docked or hangared host could warp along with its carrier. Now any warp whose carried subtree holds a mouth host is refused.
- **Rails mouths drifting:** a rails mouth could drift into a station while open. Rails mouths are now swept against rails sources over the open window.
- **Rails entities:** they were tested analytically and could cross without meeting the rim. Mouths are now physics anchors, so everything nearby has a body.
- **Followers:**
  - Followers could jam a fleet's flux. Queue slots now have priority, and followers use only the gaps. They still can follow, per the user.
  - A captured wormhole gives the interdictor a route to A paid for by the victim. Accepted and flagged.
- **Catcher spam:** the pool of 256 could be exhausted. Now 1 catcher per host, 4 per group, and a pool of 1024. Decoy wormholes against busy catchers are listed as intended counterplay; minimum open time is 30 s.
- **Suppression gaps and leaks:**
  - Tidal terms are summed over all bodies, so there is no gap at SOI boundaries.
  - Closed natural mouths no longer suppress plans, so plans can't probe for them.
  - Spool suppression counts only past lock-in, so spool/cancel cycles can't toggle it.
  - Commit uses only published flare forecasts, so pushes can't leak future flares.
- **Natural validity:** 256 samples over one synodic period does not cover several bodies. Replaced by MOID, which is exact forever. The per-pair seed in the record lets clients evaluate the schedule without the server seed.

**Performance:**
- Crossing candidates came from scanning whole bubble member lists every tick, ~0.3 ms with 2,000 bodies × 32 mouths. They are now 10 Hz per-mouth lists with velocity margins.
- The open-time cubemap rendered B's terrain, which needed a second terrain focus. It now uses the body impostors every client has, plus the streamed ring.
- Added rows for rails sweep, shell moves, leash and candidate refresh.

**Minor:**
- Fleet column time is 45 s, not 48 s.
- `WormholePlanRequest` is ~100 B once vias are counted.
- `WormholeCommit` grows by ~48 B per rails mouth.
- Added `MouthDetach`.
- Owned crossings are predicted on the client, and the camera crosses on its own plane test.
- Cross-shard commit lead raised to `TCommit − 300`.
- The 2b-before-2c order between warp and wormhole commits is stated.
- `OpenTicks` is bounded to 30 s – 20 min.
- Catchers and flares moved to W2 (after M8), because they need rooms and more.

### Revision 2 notes

**What the five user decisions changed:**
- **Managed catchers:**
  - Traffic control is an option on the one catcher component, in either mode. Friendly and hostile catchers share the pull law and the capture rule.
  - A plain station gate is the one-slot case, which gives the "known exit point".
  - New: the catch volume, `CatchSlot`, booking (`SlotRequest`, offers, confirmation at charge start, `SlotRevoke`), fleet blocks, fees escrowed at charge and paid at commit, and revocation only before `TCommit − 30`.
  - Capture rule steps 1 and 5 now count held catch slots and place B at a catch slot.
- **Signatures and keys:**
  - New section. Emitters are keyed, dark (noise) or copied.
  - A deterministic, server-resolved contest gives verdicts Noise, Claimed, Forged or Verified. Copies come from active scans that their targets can see. Keys rotate with a 1 h cooldown.
  - `PathPull` lets crews read whatever is pulling their path during charge.
  - Factions and the `Sensor` component are new concepts.
- **Natural wormholes:**
  - "Cannot be captured" is replaced by per-cycle **redirection**, with cost growing with `r²`, so it is fleet-scale for large mouths.
  - Added `NaturalCommit` (sent only for redirected cycles) and a spare mouth row per pair.
  - Added **frames**: grids parented to the mouth's frame, with far-side sensing (counts while closed, signatures while open). Natural mouths read as noise.
- **Flow:**
  - "Forces near a mouth: none" is replaced by the Flow subsection: a gravity-like field within `R_f`, a throat `ΔV` with an exit clamp and flow rejects, and one-way at `|Φ| ≥ 0.83`.
  - It is applied through zones' scene-gravity-plus-awake-delta scheme, never `AddForce`.
  - Natural flow is seeded per cycle and may reverse. Player flow is the owner's bias, plus the gate's bias, plus the power asymmetry, fixed at commit.
- **Collapse:**
  - Severance (kerf and split) is replaced by the **collapse flush**. Bodies partway through leave whole by the nearer mouth: uncrossed straddlers by A; crossed straddlers and held bodies by B. Then both mouths give an outward kick with falloff.
  - This settles old open question 2. Collapse no longer depends on hull damage, so it moves to W1 as the final rule.

**Interactions made explicit:**
- **Readings vs capture at commit:** a reading during charge says who claims to pull the path. Only commit decides and reveals who wins. Both the contacts warning and `PathPull` feed the crew's cancel decision up to `TCommit − 30`.
- **A natural-looking hostile catcher needs only noise.** An open natural mouth's pull also reads as noise and never captures. The tells are the chart and the schedule, and the host's power signature when it is in contact.
- **Catch slots vs commit-time exit resolution:**
  - A confirmed booking holds its lane from charge start.
  - At commit, the capture order still lets an earlier interdictor win.
  - The catch slot's slide stays inside the volume and then falls back to the next catch slot and the push, all before the announcement, so B is still never moved after it.
- **Flow vs crossing limits and queues:**
  - Entry is capped at `VCrossMax` and with-flow exits are clamped, so loops stay capped.
  - The field's potential is bounded by `VCrossMax / 2`.
  - Against-flow queues need `v_t ≥ ΔV + 20 m/s`, otherwise the direction is closed (`FlowClosed`).
  - With-flow approach points move out until the fleet can hold station.
- **Flush vs held bodies and the old severance:**
  - Held bodies have crossed, so they leave by B, as the old forced release did.
  - Straddlers are no longer cut.
  - Late cross-shard blobs are placed at B's flush pose.

**Problems found while integrating, and fixes:**
- `PathPull` at 2 plans/s would have been a catcher radar. It is gated at 25 % of a real charge, a cancel starts the cooldown, it carries no positions, and the catcher's owner gets a symmetric `PathRead`.
- Refusing to build near a closed natural mouth would leak it. Building is never refused; a natural mouth pushes dynamic hulls out when it forms.
- `AddForce` for flow would wake every body every tick (C2). The gravity scheme is used instead, with one-time wakes.
- A strong field could accelerate drifting debris past `VCrossMax`, turning every drift into a damaging bounce. The potential is now bounded, and flow rejects do no damage.
- Flow adds energy on each with-flow pass. The exit clamp keeps loops capped.
- A body leaving an inflow mouth against the flow is pulled back. This is left as physics: it re-crosses with the flow, and there is no special case.
- Fixed ticks vs catch-slot ticks: the charge stretches to `TSlot`, so all four ticks are still fixed at charge start.
- Catch-slot answers must not leak hidden hulls. They use only the controller's own knowledge.
- A fee charged on a captured booking would make the victim pay twice. The fee is paid only if the gate actually catches the end.
- Flush placements could exceed the 64-per-tick arrival budget. They are exempt, and bounded by the hold pool and straddlers.
- Flowing mouths with `R_f` beyond 2 km would leave rails entities outside the field. The anchor radius is now `max(2 km, R_f + 200 m)`.
- The 4-per-group catcher limit stays. Redirect power is pooled per faction instead.
- Spoofed generator keys get past a managed gate's signature filters at plan time, where `c = 0`. The forgery shows at close range once the mouth forms. Server-truth policies cannot be spoofed.
- Added per-tick rows for flow, signatures, path pulls, catch slots, frames, redirects and the flush tick, and tests for each new rule.

### Revision 2 review notes

**The five decisions, checked:**
- **Managed catchers:** present and faithful (known exit at a plain gate; traffic control assigns position + tick in a catch volume, queues fleets, refuses, charges fees; one mechanism for friend and foe). Fixed the booking mechanics below.
- **Signatures/keys:** present. "Copy freshness" was only modelled as epoch staleness; added copy ageing (`φ_eff` half-life) so freshness is a contest input in its own right, with key rotation on top. Contest stays server-resolved and deterministic.
- **Natural wormholes:** present, but "usually immensely large" was 50–400 m, the same range as player mouths. Now 150–1,200 m (median ~500 m), with 48 rim boxes above 400 m and `Depth = min(2r, 2 km)`. Redirect cost still scales with `r²`.
- **Flow:** present. Fixed conservativeness, the scene-gravity bookkeeping, sleeping-body wakes and the power-asymmetry contradiction (below).
- **Collapse:** present (both-way flush, outward kick with falloff at the fixed tick, nearer-mouth exit, severance removed). Fixed the last-tick crosser contradiction, the anchor-radius gap and the interior spike (below).

**Contradictions:**
- **Power asymmetry pointed the wrong way.** `P_B` was a catcher's `PowerDrawW` (gate upkeep 5 MW, interdictor ~250 MW) against a generator's 2 GW `ChargeRateW`, so every gated or captured wormhole flowed *away* from the catcher at `|Φ| ≈ 0.5`, contradicting "a strong station gate draws traffic in" and putting outbound traffic against a 180 m/s flow. Now `Φ_asym = 0.5 · max(0, P_B − P_A)/(P_A + P_B)` with `P_B` the catcher's `HoldPowerW`, paid at commit from its capacitor. A gate with no hold power adds no flow. Open question 9 reworded.
- **Last-tick crossers.** The flush table sent a crossing detected at `TClose − 1` out through B, but the admission rule (tail must clear by `TClose − 60`) can never admit it. That also let a body bypass a one-way flow on the last tick, because the flush transfer skipped the flow term. It now leaves by A as an uncrossed straddler, with no transfer, no flow term and no damage.
- **Kick radius vs anchor radius.** `R_flush = R_f` reached 3.2 km for a non-flowing r = 400 m mouth while its anchor radius was 2 km, so rails and frozen bodies inside the kick radius had no body and were silently skipped. `R_f` is now defined by `r` alone, and every mouth anchors to `A_m = max(2 km, R_f + 200 m)`, with shedding at `A_m + 1 km`.
- **Late cross-shard blobs** used B's pose kept "≤ 1 s", while the sharding rules allow a shard 5 s behind. B's frame now stays (no shell, no portal) until every pending blob lands, and placement uses its pose at the landing tick.
- **"Interiors feel nothing"** from the kick was false: an 80 m/s one-tick `SetLinearVelocity` reads as ~490 g in an unplated interior through the proper-acceleration history (warp's 1e5 g lesson). Kicked grids now reset that history. The flow is also counted as gravity in interior gravity, so a ship falling under it is weightless inside.
- **Natural mouths vs player commits.** Natural mouths suppressed only "while Forming or Open", so a player mouth committed just before a natural window could share space with the natural shell when it formed. Commits now treat scheduled natural windows overlapping `[TCommit, TClose]` as occupancy. The push is indistinguishable from a hidden-hull push and costs a committed wormhole, so it is no probe. Plans still see only charted mouths.
- **Natural Forming timing** was undefined relative to `openStart_k`. Forming is now `[openStart_k − 180, openStart_k)`, inside the cycle, with the generation constraint extended by 180. `NaturalCommit` is replayed to late interest joiners (the "arrives 180 ticks before open" claim ignored latency and late joiners).
- **Naming collision:** "booked slots" meant both `MouthQueue` crossing slots and catch-volume slots. They are now "queue slots" (`QueueSlot`) and "catch slots" (`CatchSlot`).
- **Busy plain gate:** a one-slot gate already holding a mouth was "skipped" with no stated fallback for the path that targeted it. It now falls back like an unpowered controller (free point, unscaled suppression, outward push within the binding quote, fee refunded), and plans get a public `GateBusy` warning.

**Determinism and timing:**
- **Flow bookkeeping:** the per-body delta used `a_flow(origin)` live while scene gravity was re-set at most at 1 Hz, so awake bodies were wrong between re-sets. The delta now subtracts the stored `OriginAccel`, and scene gravity is re-set on the same tick the flowing-mouth list changes (onset, collapse, entry and exit).
- **Moving and rotating frames:** the field is evaluated with the current-tick mouth pose (grid mouths: the previous step-8 pose, like every grid pose) and rotated into bubble axes. It is an acceleration, so no fictitious terms are needed.
- **Signature contest:** verdicts used a dwell-time threshold on the *current* closeness, so idling at the edge of range and then making one close pass gave a full close-scan verdict. Evidence is now integrated per 1 Hz evaluation on a fixed per-pair stagger tick.
- **"Suspect" was a certain tell:** only forgeries could ever reach it, so it was simply an earlier Forged. It has been removed.
- **Genuine wormholes flagged after a rotation:** wormholes froze their generator's epoch, so a key rotation made the faction's own open wormholes read Forged at once. Genuine emitters and wormholes now always claim the current epoch; only copies freeze.
- **`PathPull` timing evasion:** only spun-up catchers were listed, and `TOpen` is public from the charge glow, so an interdictor could end its 30 s spin-up exactly at `TCommit` and never show. Spinning catchers that will be eligible by `TCommit` are now listed with a flag, which gives at least ~28 s of warning before the cancel lock.
- **Catch-slot races:** concurrent confirmations are ordered `(tick, requestId)` on the controller's shard. Across shards, the deciding shard chooses the catcher and the catcher's shard chooses the catch slot; a cross-shard claim that finds the catcher full stands and is placed by the slide fallbacks (over capacity until `TClose`), because B cannot move after announcement. `HeldSlots` is owned by the catcher's shard.
- **Flush evaluation state** is pinned to the end of `TClose − 1` in `(MouthId, EntityId)` order, and kicks from both mouths of a stacked pair sum.
- **Last-wake bookkeeping:** the "`|Φ|` grows by 0.1" wake rule missed reversals through zero. It is now `|Φ − PhiAtWake| > 0.1`.

**Exploits:**
- **Free slot locking:** exclusive 30 s grants let anyone hold every catch slot of a controller at no cost by re-requesting. Offers now reserve nothing, and confirmation at charge start is first-come. `MaxBookingsPerGroup` and a `CancelForfeit` share stop book-and-cancel griefing. A controller's revocation refunds fully and starts no cooldown.
- **System-wide warp denial:** catch-slot bookings were drawn from the global `ExitReservation` pool, which warp rejects on when full ("traffic"), so 32 bookings × many controllers could refuse every warp plot. Catch slots now live in each controller's own ring and are tested through the catcher table.
- **Flow as a weapon:**
  - A gate's `Φ_gate` applied to captures let an interdictor make any victim's wormhole one-way into its trap by policy. `Φ_gate` now applies only to wormholes that targeted the gate; an interdictor must out-power the generator with paid hold power.
  - The front-half-space field was non-conservative: a guided track in through the front and out behind the cap gained up to `W` per lap, a kinetic weapon. The field is now central on both sides.
  - Superposed fields broke the `VCrossMax / 2` bound. The mouth hard radius now includes `R_f,m + R_f`, so fields never overlap.
  - Player `R_f` is capped at 2 km, inside the 2.5 km ship exclusion a hostile mouth must keep.
- **Collapse flush as a weapon:** with `R_flush = R_f ≤ 2 km` for player pairs, a hostile collapse cannot kick a non-consenting grid that stayed put. Debris thrown by the kick remains possible and is predictable from commit; accepted.
- **Hold-pool griefing:** cheap crates could fill the 16-place hold, so real ships bounced and took `m·1.5·|v_z|` damage. Only grids take hold places, and admission-failure bounces now do no damage. Damage is kept only for over-speed and Forming entries.
- **Spoof lures:** public gate listings now show the claim as Unknown, never an implied verdict. A member targeting a lure gate reads it through `PathPull` at `c = f_charge`, `m = 2`.
- **Frames:**
  - Frame placement could probe for uncharted mouths, so building one now requires the record.
  - Several frames could nest at different radii around one mouth. It is now one frame per mouth.
  - Frames and path pulls could have been used to copy keys. Copying now needs an active scan.
  - A static base in a forming natural mouth's keep-out was "pushed out whole". It is now left in place and gets a collision exclusion; only dynamic hulls are pushed.
- **Redirect scale:** with 4 catchers per group, a big natural mouth was simply unredirectable rather than fleet-scale. Power now pools per faction, with a per-host catcher cap (`CatcherMaxPowerW`, 8 GW capital), and `P_redirect` / `X_redirect` are retuned to `1 GW · r̂²` / `10 EM · r̂²` for the larger mouths (r = 1,200 m needs eighteen capital hosts).

**Performance:**
- **Flow wakes:** the flow never wakes bodies by itself. One-time wakes happen at onset, on a `Φ` change of more than 0.1 since the last wake, and when a rails or grid mouth sweeps its field over sleeping bodies (10 Hz from a wide candidate ring). Bodies that start accelerating can't sleep, and the per-body cost stays ~10 ns.
- **Far-side sensing:** frame readings are computed by the partner mouth's shard and forwarded, ~20 µs per frame per second, at most 12 frames per system. A one-per-mouth limit bounds them.
- Added a profiler row and a cost row for flow wakes, and widened the candidate rings to `A_m`.

**Unchanged on purpose:** the 3 s commit reveal, the binding quote, the no-move-after-announcement rule, the earlier review's privacy rules, and every earlier user decision in wormholes.md.

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
