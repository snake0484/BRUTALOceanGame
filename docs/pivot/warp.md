## Warp drive and zone selection

### Recommendation in one paragraph

A warp is a closed-form path between two endpoints. Each endpoint is fixed in its own frame: the departure point is fixed in the departure frame, and the exit track is fixed in the target's frame. At engage (T0) the ship leaves its exterior bubble and takes its whole grid subtree with it: crew, loose cargo, docked ships and hangared ships. It becomes a `PlacementMode.Warp` entity, and its grid pose is written each tick from the formula, not from a Jolt body. The interior keeps its grid bubble. Crew stay seated until M8 and can walk after it.

At a **commit tick** 3 s before arrival, the server fixes the exit **frame, position, velocity and arrival tick** and announces them. It does **not** pick the bubble at commit. Bubbles are server-internal and can merge, split or rebase during those 3 s, so the bubble is chosen at T1 by the attach rule.

**The player picks the zone by picking the target:** a station, an outpost, a consenting ship, a body orbit or a marker. The exit snaps to the target's arrival ring inside its bubble, so most arrivals join an existing bubble when they appear. A target that has not given permission pushes the exit out to its exclusion radius. The arrival then forms its own zone, and a merge triggered by predicted closest approach joins the two as they close (R7). That is the user's "worst case", and it is safe at any closing speed.

On the wire a warp is a few reliable events with zero per-tick traffic. Third parties never learn the destination before the 3 s commit. Cross-shard hand-off later uses the same events.

### Warp states

| State | Ticks | Where the ship lives | Ends on |
| --- | --- | --- | --- |
| Plotted | until spool, or 30 s | own bubble; the plot is a server-held preview (D and T1 are recomputed at spool start) | spool request, or expiry |
| Spool | `SpoolTicks` (default 5 s) | own bubble. Auto-aligns to the path; exterior portals lock. The full plan goes to the crew at spool start | T0. Cancel, interdiction or a lock can end it only until T0 − 30 ticks |
| Cruise | T0 … TCommit | `Warp` placement, no exterior body | commit tick |
| Committed | TCommit = T1 − N … T1 (N = 180, 3 s) | same; exit announced; the path blends onto the exit track | T1 |
| Arrived | T1 | transferred into the bubble chosen at T1, or rails if it has no anchor | n/a |
| Cooldown | `CooldownTicks` | normal | ready |

**At engage (T0):**
- **What goes along:** the root ship's grid frame subtree. That covers everything in the grid (characters, loose items), docked ships (child grids with fixed poses) and ships in hangars (nested grids).
  - A foreign ship parked in the hangar goes along too. Hangar landing permission is the consent.
  - `ALocal` is the ship origin in `DepFrame` **at T0**, not at plot time. This keeps `p` continuous through the alignment and drift during spool.
- **What stays behind:** everything in the exterior bubble. That includes bodies touching the hull from outside and non-crew characters on the hull. They keep the hull point velocity in the exterior bubble.
- **Only a root ship can warp,** meaning one whose grid's parent is not a grid. Docked and hangared ships travel with their host.
- **Spool refuses to start, or is cancelled before T0 − 30 ticks,** while:
  - a **crew member** is parented to the grid but outside `GridVolume`. The pilot sees who and can choose to leave them behind. Non-crew never block: standing on someone's hull must not pin their ship in place.
  - an **explicit lock** holds the ship: a landing clamp, a docking collar to a grid outside the carried subtree, or a tractor beam. Incidental contact does not block; the touching bodies simply stay behind.
  - Damage does **not** cancel spool by default (knob `SpoolDamageCancel`). Otherwise light fire alone would pin any ship; interdiction is the intended counter.
- **The last 30 ticks are locked in.** After T0 − 30 nothing cancels, so a crew client holding the plan from spool start is never wrong under 0.5 s of latency.
- **Departure from the bubble** uses the existing transfer: destroy the ship compound body (the shape refcount is kept). If the bubble empties, it goes back to the pool. Members that only this ship's crew anchored leave by the existing 3 km rule.
- **Placement:** `Mode = Warp`, `Frame = Lca`, and `Bubble`/`Body` are invalid. The grid frame's parent becomes `Lca`, and its pose is written in tick step 2b. Membership checks skip `Warp` placements: no SOI, band or bubble transitions.
- **Grid scene:** exterior door proxies (terrain and hulls mirrored at openings) are deleted at T0. Every exterior portal collides as closed for the whole warp, because there is no exterior to step into.

**During warp (cheap by construction):**
- **The interior is unchanged.** The grid bubble steps only if awake (0 cost asleep). Gravity is the plating vector if powered, otherwise zero.
- **No fictitious forces.** The grid has a `GridInertial` flag from T0 to T1 + 1. The grid's proper-acceleration history is reset at T0 and again at T1 + 1. Without the reset, the velocity snaps at engage and exit would reach an unplated interior as a one-tick spike of about 1e5 g.
  - The same flag covers **unanchored rails ships**, for example crew who are offline: zero-g unless plated. It does not change the zones anchor rule. A ship with an online player aboard still has an exterior bubble.
- **Ship attitude is locked** to `Rotation`, facing along the path. The pilot has no control except **Abort**.
- **The ship is intangible.** There are no collisions, and paths may cross bubbles, ships and other paths.
  - At cruise speed it crosses a 50 km observer sphere in less than a tick, so mid-path visibility is not modelled.
- **No gravity, SOI checks or frame changes.** Patched conics are suspended, and the frame stays `Lca` until arrival.

### Path

- **`Lca`:** the lowest common ancestor of the departure frame and the target frame, raised to the nearest frame that is neither rotating nor a grid (`BodyInertial` or `Solar`). Examples:
  - planet orbit → moon station: planet inertial;
  - planet A → planet B: Solar (double ulp at 1 AU ≈ 3e-5 m).

```
a(t) = Pose(DepFrame → Lca, t) · ALocal                                  ALocal captured at T0
b(t) = Pose(TgtFrame → Lca, t) · BLocal                                  t < TCommit (live target)
b(t) = Pose(AttachFrame → Lca, t) · (E + U·(t − T1)/60) + (1 − w(t))·δ   t ≥ TCommit (announced exit track)
       δ = b_live(TCommit) − track(TCommit), computed locally by each peer;  w = smoothstep((t − TCommit)/N)
s(t) = x(τ(t)) / D                                                       0 → 1
p(t) = s ≤ ½ ? a + s·(b − a) : b + (1 − s)·(a − b)                        exact at both ends
```

- **Before commit, the endpoints are live.**
  - Rails targets (bodies, surface points, stations on Kepler rails, coasting ships) are exact through `FrameTree.TryPoseAt`.
  - Simulated targets use the current pose on the server and the interpolated pose on clients. The difference is visual only.
- **From commit, the exit follows the announced track (E, U)** in `AttachFrame`.
  - The track is identical on every peer and ends exactly on E at T1.
  - E is quantised to 1/1024 m before use (C13), so the server and clients hold the same value.
  - δ absorbs everything the commit changed: a target's thrust since the extrapolation, a slide, an exclusion push. It blends to zero over the 3 s, so no peer sees a jump.
  - A client that receives `WarpArrival` late starts its blend at the receive tick, over the ticks that remain.
- **Target loss:** if the target enters warp, is destroyed or despawns before commit, b freezes at its last pose in its exterior frame at that tick.
- **Speed profile:** speed is proportional to the distance from the nearer endpoint, with a cap. It is closed form.

```
v(x)  = min(VCap, V0 + K · min(x, D − x))
x_cap = (VCap − V0) / K
ramp:  x(τ) = V0/K · (e^{Kτ} − 1),   τ(x) = ln(1 + K·x/V0) / K
T(D)  = 2·ln(1 + K·D/(2·V0)) / K                    if D ≤ 2·x_cap
      = 2·ln(VCap/V0) / K + (D − 2·x_cap) / VCap    otherwise
```

- **Evaluation:**
  - On the descending ramp, compute the remaining distance `r = V0/K·(e^{K(T−τ)} − 1)` and use `1 − s = r/D`. Each half is evaluated from its own end.
  - Render-time poses are evaluated at the fractional render tick in double (X5), never lerped between ticks. At the cap the ship moves 16,700 km per tick.
- **Arrival tick (computed at spool start, not at plot):**
  - Solve `D ← |b(T0 + T(D)) − a(T0 + T(D))|`, using extrapolation for simulated targets. Stop after 3 iterations or once the change is under 1 m.
    - The contraction ratio is about `|u_tgt| / (D/T)`. If it is ≥ 0.5, reject with "target too fast for a hop this short". At the 50 km minimum (8 km/s average) that limits a hop to targets moving under about 4 km/s in `Lca`.
    - D only shapes the profile, because the endpoints are live. Any residual error slightly changes speed, never the end point.
  - Set `T1 = T0 + ceil(60·T)`, then evaluate the profile at `τ = (tick − T0)/(T1 − T0) · T`. This makes `s(T1) = 1` exactly on a tick.
  - `TCommit = max(T0, T1 − N)`. The minimum warp distance is 50 km (6 s); shorter than that, fly.
- **Velocity:** position is continuous at T0 and T1. Velocity snaps at both ends (V0 = 5 km/s relative at the endpoints). The flash covers the snap visually, and `GridInertial` hides it from the interior.
- **Obstruction:**
  - **Prune first.** Skip any body whose orbit's bounding sphere about its parent (apoapsis + R + floor) misses the path's bounding capsule. That costs ~5 ns and needs no Kepler solve.
  - **Then the capsule test.** For the remaining bodies (typically 5 or fewer), split the path into 32 equal-s segments. Test each as a capsule against a sphere, with body poses at the segment's mid tick plus a margin for body motion.
  - **Inflation:** the path test inflates bodies by `WarpFloor − 1 km`; endpoints must clear the full `WarpFloor`. Otherwise an endpoint sitting on the floor blocks its own path.
  - **Runs as a job;** the result arrives the next tick.
  - **If blocked:** reject the plot with the blocking body and a suggested via point. Multi-leg warps chain plots, with a 2 s re-spool between legs.
  - **Chords sag.** Between two points at floor altitude on a 1000 km body, the chord stays within that 1 km only if the points are under about 90 km apart. Surface-to-surface hops therefore need a via point (open question 1).
- **Determinism:** `Math.Exp` is not guaranteed bit-identical across OS libms. Clients use the formula for visuals and their own grid pose. The authoritative exit always arrives in `WarpArrival`.

### Speeds and travel times

Default drive: `V0 = 5 km/s`, `K = 0.3 s⁻¹`, `VCap = 1e6 km/s`. The two ramps take 81 s in total and cover 6.7e6 km. Spool (5 s) comes on top.

| Trip | D | Warp time |
| --- | --- | --- |
| Short hop (minimum) | 50 km | 6 s |
| Around a 1000 km planet (one leg) | 1,500 km | 26 s |
| Planet → moon | 20,000 km | 43 s |
| Planet → far moon | 50,000 km | 49 s |
| Compressed system, planets 0.1 AU apart | 1.5e7 km | 90 s |
| Planets 1 AU apart | 1.5e8 km | 225 s (3.7 min) |
| Planets 5 AU apart | 7.5e8 km | 825 s (13.7 min) |

- **K sets the in-SOI feel.** The log ramp compresses the range: a moon at 20k or 50k km takes about the same time.
- **`VCap` sets interplanetary time**, linearly. A higher-class drive raises `VCap`, not `K`.
- **The numbers are knobs, not physics.**
  - No physics or rendering system ever sees the warp speed: the ship has no Jolt body, and the camera is camera-relative in double.
  - There is no light lag or relativity. Only the ratios against spool, cooldown and fuel matter.
- **Fuel:** `cost = FuelPerLn · mass · ln(1 + D / 1e6 m)`, with mass including docked and hangared ships and cargo. It is consumed at T0, and an abort gives no refund.

### Choosing the exit point (the zone decision)

| Target | Default exit point | Attach frame | Arrival velocity |
| --- | --- | --- | --- |
| Body orbit | On the chosen circle (altitude ≥ `WarpFloor`, default plane equatorial prograde), nearest the approach line | Band rule (below) | Circular, prograde, converted through the transfer maths |
| Surface point / outpost | Above the point at `WarpFloor` | BodySurface | Zero in the surface frame (hover; the pilot descends) |
| Station / ship (a grid) | On its **arrival ring** (below) | The target's exterior frame (never the grid), then the band rule | `MatchTarget` (below) |
| Marker (bookmark, mission, scan) | The marker's int64 position in its frame | Band rule | Drop rule |
| Free point | Dragged in the map. Stored at plot in the deepest frame containing it (SOI, then band), so it moves with that frame. The preview draws bodies at T1 | Band rule | Drop rule, or `FrameRest` if the pilot chooses |
| Fleet member | Leader's exit + formation offset | The leader's | The leader's |

- **Band rule (attach frame for every target):** use the frame membership would put the exit in. That is the deepest SOI by the 0.98 rule, then `BodySurface` if altitude < `RotatingTop`.
  - Example: a 20 km orbit on an airless body is inside the 50 km rotating band, so it attaches to `BodySurface`.
  - Attaching it to `BodyInertial` would cost an extra transfer 0.1 s after arrival and would pick a bubble in the wrong frame.
- **Drop rule** (markers, free points, aborts, interdiction with no owner):
  - Inside an SOI: circular orbit about that body, in the plane of r and the path direction, prograde with respect to the path. It is converted into the attach frame.
  - In Solar: circular heliocentric orbit.
  - "Frame rest" in an inertial frame would mean zero inertial velocity, a vertical fall (about 80 s from 30 km).
- **MatchTarget velocity:**
  - Orbiting targets: `U = u_tgt + ω_orb × (E − X_tgt)`, at rest in the target's local orbital (LVLH) frame.
  - Other targets: `U = u_tgt`.
  - For simulated targets, `u_tgt` is extrapolated from the commit tick.
- **Arrival ring:**
  - **Standoff** = target radius + ship radius + `max(300 m, ½·a_tgt·(N/60)² + 100 m)`, where `a_tgt` is the target's maximum thrust acceleration (0 for rails targets). The term covers a target that burns hard during the 3 s commit window.
  - **Orbiting target:** the ring lies in the along-track / cross-track disc, perpendicular to the radial.
    - An along-track offset at LVLH rest is a Clohessy–Wiltshire equilibrium, so the arriving ship holds station.
    - A radial offset drifts at about 1.5·n·Δr. For 500 m in low orbit on a 1000 km 1 g planet that is about 2 m/s, 140 m a minute away from a friend's ship.
  - **Other targets:** the hemisphere facing the approach.
  - **Dispersion:** the position on the ring comes from a per-warp server seed, within ±60° of the approach or along the station's arrival lanes. There is no single camp point, and the seed is never sent before commit.
- **Exclusion zones (any grid, not only the named target):**
  - Each grid has an `ExclusionRadius`: stations 5 km by default, ships 2.5 km (beyond a bubble's 2 km soft radius).
  - An exit inside a grid's exclusion zone needs that grid's permission: owner, crew, party, or a station's permission hook. Stations are public by default unless restricted.
  - Without permission, the exit is pushed out radially to the exclusion radius at commit, silently.
  - A hostile warping "onto" a ship therefore appears as its own zone 2.5 km out and has to close under thrust. The predicted merge joins it before contact.
- **Floor:** `WarpFloor(body) = max(AtmosphereTop, TerrainMax + 2 km, 0.01·R)`, using global max terrain. Terrain edits are clamped to `TerrainMax`, so the floor stays a constant. Examples:
  - Airless 1000 km body with 10 km peaks: 12 km.
  - Body with a 100 km atmosphere: 100 km.

  No warp inside an atmosphere or below the floor, at departure or at exit.

**UX (ImGui map first):**
- **Selecting a target snaps the exit to its ring or default.** Free placement shows a preview sphere:
  - green: a named target or joinable contact;
  - blue: open space, a new zone;
  - red: invalid, for a reason the client can know (floor, a body on the path, fuel, cooldown, a visible exclusion zone).
- **Joinable places show as contacts with arrival rings,** published by the server at 0.2 Hz per SOI. Bubbles never reach the wire.
- **A target must be in the requester's known contacts** (radar or interest). An entity id the client was never sent cannot be targeted.

**Server validation** (preview at plot, authoritative at spool start, exit re-resolved at commit):
1. **Drive:** charged and out of cooldown, fuel ≥ cost(D), and total mass ≤ `MassCap`.
2. **Departure:** altitude ≥ `WarpFloor` for every body, no crew outside `GridVolume`, and no explicit lock.
3. **Path:** clear of all inflated bodies. Interdiction fields on the path give a warning, not a rejection.
4. **Exit: only checks the client could reproduce.** These are the floor, the target being known, and a visible grid's exclusion zone without permission.
   - Hull occupancy, hidden ships and reservations are **never** a reason to reject. They are resolved silently at commit.
   - "Exit blocked" as a reject reason, at 2 plots/s, would be a system-wide radar for hidden ships.

**Commit** (at `TCommit`, server):

Commits run in `(T1, WarpId)` order, so commits on the same tick see each other's reservations.
1. **Exit track:** E is the target state at `TCommit` extrapolated to T1 (exact for rails), plus the ring offset. U comes from the velocity rule.
2. **Exclusion push** for every grid that does not permit the exit.
3. **Occupancy:**
   - The test is an exit sphere (ship radius + 300 m; a fleet uses its union sphere) against:
     - the bounding spheres of every body near the exit (bubble members, rails and frozen entities), each extrapolated over N ticks with an acceleration margin;
     - every `ExitReservation` whose window overlaps `[T1 − 2 s, T1 + 10 s]`.
   - This uses our own entity data. It is not a Jolt query, because the broadphase and CollideShape exports are unverified (P1).
   - If blocked, slide along the ring or approach, away from the blocker, by 2× the sphere radius, up to 8 steps. If still blocked, exit at 2× the original distance. Then reserve.
4. **Arrival booking:** book the arriving bodies (ship compound plus docked ships) in the per-tick arrival budget at T1, at most 64 bodies per tick. If the budget is full, `T1 += 1` before announcing. Arrivals are never deferred after they are announced.
5. **Announce `WarpArrival`.**

**Prepared at T0, not at commit:**
- **Scene pool:** keep free scenes ≥ pending arrivals + 2. When short, the pool grows on a worker (~1 ms per system, off the main thread).
- **Surface targets:** collision rings and terrain prefetch around the exit start on the server and on crew clients.

**At T1** (tick step 3):
1. **Final occupancy test** (the same sphere test, ~1 µs). Something may have moved into the spot during the last 3 s, such as a ship parked there on purpose. If so, slide outward to the first clear step; the first snapshot corrects clients, and the flash hides the pop. The body is never created overlapping.
2. **Attach bubble** (server only):
   - **No physics anchor** (e.g. an AI ship with no crew): rails.
     - That is a Kepler orbit in the inertial frame.
     - A hover exit uses the thrusting rails integrator with a station-keeping controller. It is never `Frozen` in mid-air.
   - Otherwise, the bubble in the attach frame whose origin is within `soft radius − ship radius` of E (the nearest if several).
   - Otherwise, any bubble whose predicted closest approach to the exit state within H is below the merge gap.
   - Otherwise, a new bubble: a Surface anchor (origin = E snapped to 32 m) or FreeFall (`OriginRails` from the exit state).
   - Arrivals on the same tick are processed in `WarpId` order, so two ships arriving together share the bubble the first one created.
3. **Create the body** with the cached compound shape at E, with the path rotation, velocity U and angular velocity 0. The grid re-parents to the attach frame, and its pose comes from the body after the step. `GridInertial` clears at T1 + 1.

**Abort and late changes:**
- **Pilot abort** at tick `t < TCommit`:
  - It becomes an exit at `T1' = t + N`, at `p(T1')` on the original profile, with the commit run immediately.
  - The attach frame follows the band rule and the velocity the drop rule.
  - At the speed cap the ship still covers up to 3e6 km in those 3 s.
- **Aborts and interdictions with `t + N > TCommit` are ignored:** the ship is already arriving.
- **Fleet:** a leader's abort drops the whole fleet; a member's abort drops that member alone.

### Merges: predicted closest approach (replaces the 10 Hz gap check, R7)

Server only. Clients build their own prediction scenes and never merge.

- **State per bubble**, refreshed after the step: `BubbleMotion` (centroid c, velocity v, bounding radius r, `MaxAccel` = the largest thrust acceleration among members).
- **Pair test** in a common frame. Cross-frame pairs (surface vs inertial of the same body) go through the frame transfer maths. Pairs are pruned to the same body subtree.

```
Δp = c_B − c_A,  Δv = v_B − v_A
t* = clamp(−Δp·Δv / |Δv|², 0, H),  H = 2 s
gap = |Δp + Δv·t*| − r_A − r_B − ½(MaxAccel_A + MaxAccel_B)·H²
merge if gap < 500 m
```

- **Cadence:**
  - **Hot pairs** are evaluated **every tick**. A pair is hot if `|Δp| − r_A − r_B < |Δv| · 4 s + 2 km`.
  - All other pairs are evaluated at 10 Hz, staggered.
  - At a 3 km/s closing speed this merges about 2 s before contact.
- **Merge order:**
  - The smaller bubble (by body count) goes into the larger; ties go to the lower id. The merged bubble keeps the larger one's frame, and other members transfer through the frame maths.
  - Islands transfer nearest-first. Only the islands on the predicted contact may exceed the 32 transfers-per-tick budget, up to 128 bodies a tick. The rest stay on budget, and kinematic proxies (≤ 64 each way) bridge the gap until they move.
- **Fast ships:** inside a bubble, ships above 100 m/s use `MotionQuality.LinearCast` (probe the export). Merging alone doesn't stop tunnelling at 50 m per tick.
- **Merged size:**
  - The union rebases to its centroid at the next rebase.
  - Members are shed by zones' rule: more than **3 km** from every anchor and contact-free.
  - If anchors are still spread beyond the 4 km hard radius, don't merge: stitch with kinematic proxies both ways (≤ 64 each) and keep predicting.
- **Split:** group gap > 1500 m **and** a predicted minimum gap over H > 1500 m **and** at least 2 s since the last merge. This prevents merge/split thrash on passes.
- **Warp arrivals rarely trigger merges,** because the attach rule joins at birth. The expected merge sources are:
  - exits pushed out by an exclusion zone;
  - interdiction drops;
  - free-point exits near other players.
- **Cost:** about 15 ns per pair. A server with 128 bubbles averages about 15 µs per tick.

### Interdiction and aborts (optional hook)

- **Field:** `InterdictionField` (centre in a frame or on an owner entity, radius, start and end ticks). Gravity wells are not interdictors.
- **Detection:**
  - On warp start and on field activation, find the first tick each path enters each field, using the same 32-segment capsule test (~2 µs per pair).
  - Fields centred on a **simulated** owner are re-tested at 1 Hz, because the owner moves under thrust.
- **Forced exit:**
  - `t_exit = max(t_enter, now + N)`, committed at `t_exit − N`, so clients always get 3 s of warning. Entries detected after `TCommit` are ignored.
  - The exit is at `p(t_exit)`, then pushed by exclusion zones and slid if blocked.
  - **Velocity:**
    - Owned field: the interdictor's velocity plus 100 m/s along the path.
    - Static field: the drop rule plus 100 m/s along the path.
  - Attach: by the T1 rule, with predictive merge as the interdictor closes.
- **Protected volumes:** stations can forbid field activation inside their exclusion zone (permission hook), so arrival rings can't be turned into traps.
- **Cross-shard (interplanetary) warps** are not interdictable in v1.
- **Player abort** uses the same code path.

### Fleet warp

- **Group:** up to 16 root ships within 2 km of the leader, in the leader's bubble. Players accept; AI accepts automatically.
- **Shared timing and profile:** spool syncs to the slowest member, and T0 and T1 are shared. The profile is the most conservative one: min `V0`, min `K` and min `VCap` over the leader's D. Every member then has the identical `s(t)`.
- **Per member:** its own departure point `a_i`, and `b_i(t) = b(t) + R_exit · offset_i`. Offsets are the formation at engage, relative to the leader, rotated into the arrival attitude and clamped to 1 km.
- **Shared decisions:**
  - One union-sphere reservation, one exclusion and permission decision (against the leader's permissions), one arrival booking.
  - Members arrive in order on one tick, so they share one bubble.
  - **One** `WarpPlan` and **one** `WarpArrival` carry the member list.

### Networking

| Event (reliable, tick-stamped) | When | To | Size |
| --- | --- | --- | --- |
| `WarpPlotRequest` | player plots | server | ~40 B: target ref, exit local int64, flags |
| `WarpPlotResult` | reply | requester | ~40 B: ok or a client-knowable reason, D, duration preview |
| `WarpPlan` | spool start | crew of the whole carried subtree | ~100 B: id, T0, T1, Lca, DepFrame, TgtFrame/TgtEntity + BLocal, DriveId, D (int64 1/1024 m), rotation 48 bit, group |
| `WarpSpool` | spool start | interest of the ship | ~16 B (others see the spool-up effect) |
| `WarpCancel` | before T0 − 30 | crew, interest of the ship | ~8 B |
| `WarpStart` | T0 | crew | ~32 B: id, `ALocal` int64 |
| `WarpDepart` | T0 | interest of the departure point (non-crew) | ~24 B: entity, T0, rotation, DriveId. Direction and ramp only: no D, no target |
| `WarpInbound` (optional) | T0 | crew and owner of the target grid only | ~24 B: id, ETA, signature class. No exit position, no blueprint |
| `WarpArrival` | TCommit (T1 − 3 s) | crew; interest of the exit point | ~70 B: id, T1, attach frame, E int64, U float32 ×3, rotation, blueprint hash, DriveId, members |
| `WarpAbort` / `WarpInterdicted` | on event | crew | ~20 B, followed by `WarpArrival` |

- **The plan goes to the crew at spool start.** Crew clients therefore apply T0 on the right tick, rather than half an RTT after their grid has already jumped. The 0.5 s cancel lock-in makes this safe.
- **Third parties learn only two things:** the departure direction at T0, and the exit 3 s before arrival. Three seconds is about what they would get from seeing the ship appear anyway. Nobody but the crew ever gets the destination early.
- **No per-tick traffic during warp.**
  - Crew evaluate `p(t)` for their grid pose and keep full-rate interior snapshots in grid coordinates.
  - The ship's exterior snapshots stop.
  - There is no mid-path interest. Late joiners among the crew get the plan replayed from `WarpTable`.
- **`WarpArrival` is the existing frame-change event** ("E → frame F at T1 with state S"), carrying the warp id. Bubbles stay off the wire.
- **Arrival on other clients:**
  - At `WarpArrival` they fetch the blueprint by hash (brush lists of a few KB fit easily in 3 s) and start the flash, growing over N ticks.
  - The approach streak is drawn from the DriveId's ramp along the arrival rotation.
  - At T1 they add the ship to their prediction scene as a kinematic proxy, and normal snapshots take over.
  - If `WarpArrival` is lost past T1, they fall back to snapshots.
- **Crew and pilot clients:**
  - At T0 they know the destination. They prefetch terrain for surface targets, and the speed LOD bias suppresses everything mid-path.
  - At commit, the crew's interest centre moves to the exit point, so destination snapshots arrive before T1.
  - The pilot's prediction scene (C12) is dropped at T0 and rebuilt at TCommit around the exit point.

**Dedicated sharding (after M8): warp is the hand-off point.**
- **Shared clock:** all shards share one global `TickIndex`; rails depend on it.
- **Ownership:**
  - A warp whose target frame belongs to another shard (another planet's SOI subtree) is handed over during the warp.
  - Warps skip the interplanetary shard. The sublight SOI-crossing hand-off from zones.md stays.
- **At T0:**
  - The departure shard sends the carried subtree (grid contents, characters, inventories, docked ships, `WarpTable` row) to the destination shard as a blob.
  - Crew clients get `ShardHandoff {address, token}` and connect in parallel.
- **Authority switch at `T_h`:**
  - `T_h` = the tick when the destination has acked the blob and every crew client has connected (or a 10 s timeout, after which disconnected crew travel as offline characters), plus 30 ticks. It is announced.
  - Until `T_h` the old shard keeps stepping the grid bubble. At `T_h` it sends the final grid-local delta and stops.
- **If `T_h` isn't reached by TCommit − 60:** the old shard extends T1 in whole seconds, announced in a re-sent `WarpPlan`. The arrival is only ever performed by the shard that owns the target frame.
- **Short warps inside one shard** don't hand off.

### Data structures (zero alloc)

```csharp
public enum PlacementMode : byte { Simulated, Rails, Frozen, Warp }   // Placement also gains: public WarpId Warp;
public enum WarpPhase : byte { Spool, Cruise, Committed }
public enum WarpTargetKind : byte { BodyOrbit, Surface, Grid, Marker, FreePoint }
public enum ArrivalVelocity : byte { MatchTarget, CircularOrbit, FrameRest }
public readonly record struct WarpId(ushort Value);

/// Drive class, data-driven (data/drives/*.json); sent by id.
public struct WarpDrive
{
    public double V0, K, VCap;            // m/s, 1/s, m/s
    public int SpoolTicks, CooldownTicks;
    public double MassCap, FuelPerLn;     // kg; fuel per kg per ln(1 + D/1e6 m)
}

/// Closed-form profile; ~20 ns per evaluation (one Exp).
public readonly struct WarpProfile
{
    public readonly double V0, K, VCap, D, T, XCap;   // T = continuous duration, s
    public static double Duration(in WarpDrive drive, double distance);
    public double Fraction(double tau, out double oneMinusS);   // s and 1 − s, each from its own end
}

/// SoA, preallocated: 256 warps on a server, 64 on a client. Index = WarpId.Value.
public sealed class WarpTable
{
    public int[] Entity, Owner; public ushort[] Group; public WarpPhase[] Phase;
    public FrameId[] Lca, DepFrame, TgtFrame, AttachFrame;
    public double3[] ALocal, BLocal;        // ALocal captured at T0
    public int[] TgtEntity;                 // simulated target ship, -1 if rails-predictable
    public long[] T0, TCommit, T1;
    public WarpProfile[] Profile; public doubleQuat[] Rotation;
    public WarpTargetKind[] Kind; public ArrivalVelocity[] VelRule;
    public double3[] ExitPos, ExitVel;      // E (quantised 1/1024 m), U; in AttachFrame, set at commit
    public double3[] BlendDelta;            // δ in Lca, local to each peer
    public ushort[] FreeIds; public int FreeCount, ActiveCount; public ushort[] Active;
    public void Evaluate(long tick, FrameTree frames);                        // writes grid poses for all active warps
    public double3 PositionAt(int warp, double renderTick, FrameTree frames); // render, fractional tick, double
}

public struct ExitReservation { public FrameId Frame; public double3 Pos; public float Radius; public long From, To; } // fixed pool of 512, freed by expiry; full → plot rejected ("traffic")
public struct ArrivalBooking { public long Tick; public ushort Bodies; }  // ring of 256 ticks (bookings are ≤ 180 ticks ahead)
public struct WarpExclusion { public int Grid; public float Radius; }     // per grid; permission via hook
public struct InterdictionField { public FrameId Frame; public int Owner; public double3 Centre; public double Radius; public long Start, End; }
public struct BubbleMotion { public double3 Centroid, Velocity; public float Radius, MaxAccel; public long LastMergeTick; }

// FrameTree addition: exact for rails/spin chains at any tick; false if the chain has a simulated grid.
public bool TryPoseAt(FrameId from, FrameId to, long tick, out FramePose pose);
```

- The merge pair list and the hot-pair set are preallocated (`ushort` pairs, 8,128 max).
- Event serialization writes into pooled send buffers.
- Plot validation jobs use preallocated per-worker scratch.
- No LINQ or closures, and there are no per-tick allocations.

### Per-tick cost (estimates, to measure with `--zone-bench`)

| Item | Cost |
| --- | --- |
| Warp path evaluate (one Exp, two cached poses, grid pose write) | ~150 ns per warp; 64 warps ≈ 10 µs |
| Grid bubble during warp | same as at rest; 0 if asleep |
| Plot validation (job, rate-limited to 2/s per player) | ~20 µs after pruning (about 5 bodies × 32 Kepler solves); 0 on the main thread |
| Commit (once per warp): sphere tests, reservations, booking | ~10–30 µs |
| T1 final test + attach | ~2 µs, plus the transfer at ~5 µs per body |
| Scene pool growth | ~1 ms per system, on a worker, started at T0 |
| Interdiction | event-driven, ~2 µs per (warp, field) pair; 1 Hz for fields with moving owners |
| Merge prediction (server) | ~15 ns per pair; 128 bubbles ≈ 15 µs per tick on average |

### Changes to zones.md

- **`Placement`:** `PlacementMode.Warp` plus a `WarpId` field. **`FrameTree`:** `TryPoseAt`.
- **Gravity and rails:** remove "quantum travel" from the thrusting Verlet bullet. Warp is closed form in `WarpTable`, and no gravity applies during warp.
- **Grid bubble:**
  - Add the `GridInertial` flag (warp from T0 to T1 + 1, and unanchored rails ships): plating or zero-g, and no fictitious forces.
  - Reset the proper-acceleration history at T0 and at T1 + 1.
  - During warp, exterior portals collide as closed and exterior door proxies are dropped.
- **Membership:** skip `Warp` placements. The anchor rule is unchanged.
- **Enter/leave table:**
  - Add **Warp engage**: enter at T0 if validation passed; the carried subtree goes along.
  - Add **Warp exit**: at T1, attach to the frame announced at commit and to the bubble chosen at T1.
  - **Replace** the merge/split row:
    - merge when the predicted gap within 2 s is < 500 m (hot pairs every tick, others at 10 Hz);
    - split when the group gap > 1500 m, the predicted minimum gap > 1500 m, and at least 2 s have passed since the last merge.
  - The 3 km shed rule is unchanged.
- **Transfers:** warp arrivals are pre-booked in an arrival budget (≤ 64 bodies per tick) and never deferred. A merge may exceed the 32-transfers budget only for islands on the predicted contact (≤ 128).
- **Pool:** keep a low-water mark of free scenes and grow them on a worker.
- **Terrain:** edits are clamped to `TerrainMax`.
- **Tick order:**
  - Insert **2b. Warp paths:** evaluate, run the commits due (in `(T1, WarpId)` order), and queue the arrivals due at T1.
  - Step 3 transfers now include arrivals, with the final occupancy test.
  - Step 9 adds merge prediction (hot pairs every tick).
- **Networking:**
  - Add the warp events. Interest row: "In warp: crew only; third parties get departure direction at T0 and exit at T1 − 3 s".
  - Sharding: one global `TickIndex`. Warp is the cross-shard hand-off; the SOI-boundary hand-off stays for sublight.
- **Critique Q1 (getting to the moon)** is answered by the warp drive. Hohmann transfers remain possible but are not required.
- **Milestones:**
  - **M7:** warp with **seated** crew to moon orbit, to a surface point, and to the other player's ship (with consent; it joins their bubble), plus predicted merge. **Exit checks:**
    - at T1 the orbit elements match the requested circle;
    - commit plus T1 cost under 0.1 ms;
    - a 3 km/s interception merges more than 1 s before contact (logged);
    - an arrival next to an orbiting friend drifts under 0.05 m/s relative after 60 s.
  - **M8:** walking during warp (grid bubble, `GridInertial`, sealed portals).
  - **After M8:** fleet warp, interdiction, sharded hand-off.

### Files

- **`src/Frames/`** (no Jolt):
  - `WarpProfile.cs`, `WarpTable.cs`;
  - `WarpPlanner.cs` (LCA, D fixed point at spool start, pruned path vs bodies, via points, band and drop rules);
  - `WarpLimits.cs` (`WarpFloor`); `FrameTree.TryPoseAt`.
- **`src/Physics/`:**
  - `WarpArrivals.cs` (commit, exclusion push, occupancy, reservations, arrival booking, T1 attach rule);
  - `ApproachPredictor.cs` (inside `BubbleManager`);
  - `Interdiction.cs`.
- **`src/Net/WarpEvents.cs`:** plot, plan, spool, cancel, start, depart, inbound, arrival, abort, and shard hand-off.
- **Client:**
  - `src/Client/Warp/WarpView.cs` (streak, arrival flash, δ blend, destination prefetch);
  - `WarpPlotPanel.cs` (ImGui map, exit preview colours).
- **Other:**
  - `data/drives/*.json`;
  - `ProfilerPanel` rows: Warp eval, Plot jobs, Commits, Arrivals, Merge predict;
  - this section goes into `docs/frames-and-zones.md`.
- **Tests:**
  - `WarpProfile`: `s(T1) = 1` exactly; monotonic; `T(D)` matches integration; `p` is exact at both ends.
  - Path: continuous through commit for a target thrusting at 3 g (no jump larger than one tick of motion).
  - Commits: two exits plotted to the same ring point on the same tick never overlap.
  - Arrival in the rotating band: attaches to `BodySurface` with zero follow-up transfers.
  - `ApproachPredictor`: 3 km/s head-on and crossing cases merge ≥ 1 s before contact.
  - Attach: an exit inside a soft radius joins that bubble, never a new one.
  - Privacy: no `WarpPlotResult` reason ever depends on hull occupancy.

### Open questions for the user

1. **Line of sight:** should planets and moons block a warp path, so the player plots via points as in Star Citizen (proposed: reject, suggest a via point, chain legs)? Or should the server auto-route around bodies? Note that surface-to-surface hops more than ~90 km apart always need a via point.
2. **During warp:** crew walk freely and feel nothing, the pilot can only abort, and EVA and boarding are impossible in warp. OK, or do you want anything to happen mid-warp?
3. **Exit floor:** no warp inside an atmosphere, and nothing below 2 km over the body's highest terrain (about 12 km on an airless 1000 km body). Is that right, or should ships be able to warp to an outpost's doorstep?
4. **System scale:** at these settings 1 AU takes about 3.7 min, 0.1 AU about 1.5 min and 5 AU about 14 min. How far apart are the planets? That decides `VCap`, or whether to compress the system.
5. **PvP:**
   - Are the default exclusion radii right, with non-consenting arrivals kept 5 km from stations and 2.5 km from ships?
   - Should the target get an "inbound contact, ETA" warning?
   - Should damage cancel a spool?
   - Is interdiction in scope?

### Review notes

- **Bubble choice moved from commit to T1.** Bubbles are server-only and can merge, split or be recycled in those 3 s. Only the frame, E, U and T1 are fixed at commit.
- **Live b(t) replaced by the announced exit track from commit, plus a local δ blend.** Before, a target thrusting at 3 g dragged the arrival about 130 m off the announced, reserved and tested spot. A slide at commit also made the path jump by kilometres.
- **Attach frame follows the rotating band (C3) and SOI rules.** The old table put orbit exits below `RotatingTop` in `BodyInertial`, which forced an immediate extra transfer and a bubble in the wrong frame.
- **Abort and interdiction velocity: "frame rest" became the drop rule (circular orbit).** Zero inertial velocity meant a vertical fall.
- **Ring placement for orbiting targets moved into the along-track disc, with velocity at rest in the target's orbital frame.** A radial offset drifts about 2 m/s in low orbit.
- **Privacy:**
  - `WarpStart` went to everyone near the departure with the target, and `WarpInbound` sent the exit and blueprint to the destination 43 s early. Both were camping intel.
  - Now the destination goes to the crew only, and third parties get the exit 3 s before arrival.
  - Mid-path interest was dropped: the ship crosses any observer's range in less than a tick.
- **Plot rejections no longer reveal hull occupancy.** At 2 plots/s that was a system-wide radar for hidden ships. Occupancy is resolved silently at commit and again at T1.
- **Exclusion zones apply to every grid, with a silent push-out.** The old permission check covered only the named target, so a free point next to a hostile station bypassed it.
- **Anti-camping:** the server seeds dispersion on the ring and stations can define arrival lanes. Stations can forbid interdiction fields inside their exclusion zone.
- **Anti-griefing:**
  - Non-crew on the hull and incidental contact no longer block spool.
  - Damage doesn't cancel spool by default.
  - Cancels lock 0.5 s before T0, which also makes the crew's early copy of the plan safe.
- **New final occupancy test at T1.** A ship parked on the exit during the 3 s window would otherwise have the arrival created inside it.
- **Same-tick commits and arrivals are processed in a deterministic order.** Overlapping exits now slide apart, and simultaneous arrivals share one bubble.
- **Bubble creation latency:** the scene pool grows on a worker from T0, and surface collision and terrain prefetch also start at T0.
- **Arrivals are pre-booked in a per-tick arrival budget.** Before, the 32-per-tick deferral could delay an announced arrival.
- **The merge budget override is now capped** (≤ 128, with proxies for the rest) instead of unbounded.
- **Shedding uses zones' 3 km rule.** The old 1500 m contradicted the 2/3 km hysteresis.
- **`GridInertial`:**
  - It now spans T0 to T1 + 1 and resets the proper-acceleration history. Otherwise the velocity snaps would reach unplated interiors as a one-tick 1e5 g.
  - Exterior portals are sealed during warp.
  - The flag no longer implies a change to the anchor rule.
- **Path test:**
  - The capsule test now prunes bodies first; the old ~30 µs ignored 2048 Kepler solves (~0.2 ms).
  - It runs as a job.
  - Endpoints sitting on the floor no longer block their own path.
- **Precision:**
  - The lerp form is exact at both ends, and the descending ramp is evaluated from the exit end.
  - Render poses use fractional ticks in double (X5).
  - E is quantised before use (C13).
- **Timing:**
  - D and T1 are computed at spool start, and `ALocal` is captured at T0.
  - The D fixed point now has a convergence guard against fast targets.
- **Shard hand-off:** authority now switches only after the destination acks the blob and the crew have connected. The old fixed T0 + 2 s handed the crew off to a shard that might not hold their state yet.
- **Interdiction:** owned fields are re-tested at 1 Hz, and the drop velocity is the interdictor's.
- **Milestones:** warp in M7 is with seated crew only, because walking needs M8's grid bubble.
- **Minor fixes:** the 5 AU time is 825 s, not 823 s. The exit occupancy test uses our own entity spheres instead of unverified Jolt query exports (P1). Rails arrivals are never `Frozen` in mid-air. The reservation ring became a bounded pool. Targets must be among the requester's known contacts.

All costs are estimates. Nothing was built or run, and no repo files were changed.