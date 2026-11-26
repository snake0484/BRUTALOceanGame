## Warp drive and zone selection

### Recommendation in one paragraph

A warp is a smooth, server-computed curve between two endpoints that bends around planets and moons on its own (no via points). Each endpoint is fixed in its own frame: the departure point in the departure frame, the drop track in the target's frame, and each avoidance waypoint in the frame of the body it avoids. At engage (T0) the ship leaves its exterior bubble and takes its whole grid subtree with it: crew, loose cargo, docked ships and hangared ships. It becomes a `PlacementMode.Warp` entity, and its grid pose is written each tick from the formula, not from a Jolt body. The interior keeps its grid bubble. **Crew move freely and can interact with anything aboard** (D17); what can stop the drive is decided by the ship's wiring.

**Warp space is real space** (D66). The bubble carries a pocket of ordinary vacuum around the hull, and the drive seals nothing: a holed ship vents all the way through cruise, doors and holes are as open as they were built or cut, and crew can go out onto the hull. Only latches hold (docking collars, hangar clamps, jettison racks, the core's eject cradle). Whatever crosses the bubble wall in cruise is lost (proposal, see "The warp bubble is real space").

**The last part of every warp is in real space** (D18). At the **drop tick** TDrop = T1 − 2 s the ship leaves the warp at 2 km/s relative to its exit track, 2 km short of the exit point, as an ordinary Jolt body. The drive's collapsing field brakes it to the exit velocity by T1. For those 2 s it is a large ship-shaped projectile: it collides, and damage goes both ways.

At a **commit tick** 3 s before the drop (T1 − 5 s), the server fixes the exit **frame, corridor, velocity and arrival tick** and announces them. It does **not** pick the bubble at commit. Bubbles are server-internal and can merge, split or rebase in those 3 s, so the bubble is chosen at TDrop by the attach rule.

**Civilian warps broadcast their arrival** (D18). A safety beacon sends the arrival corridor and ETA to everyone near the exit, 30 s before the drop. Military drives can switch it off. Cutting the beacon wire also silences it, but the ship is then fined and blamed for any damage its arrival causes.

**There is no exit floor** (D16). A warp may end low in an atmosphere, but the drop then sends out a pressure wave that scales with the ship's mass and the air density. It can kill wildlife and people and damage structures, and it is fined and logged against the pilot. **Used as a weapon it is a crime** (D58): a low drop whose injury ring covers a settlement, someone else's structure or a person is the `WarpAttack` atrocity in factions-and-law.md, whether or not anyone is hurt. Towns, cities and capital worlds that paid for a **region shield** absorb the blast and push exits out of their shell; open ground has only the law.

**The player picks the zone by picking the target:** a station, an outpost, a consenting ship, a body orbit or a marker. The exit snaps to the target's arrival ring inside its bubble, so most arrivals join an existing bubble when they drop. A target that has not given permission pushes the exit out to its exclusion radius. The arrival then forms its own zone, and a merge triggered by predicted closest approach joins the two as they close (R7). That is the user's "worst case", and it is safe at any closing speed.

On the wire a warp is a few reliable events with zero per-tick traffic during cruise. Third parties learn the destination from the beacon (30 s ahead, near the exit only) or, for a silent warp, from the commit 3 s before the drop. Cross-shard hand-off later uses the same events.

### Warp states

| State | Ticks | Where the ship lives | Ends on |
| --- | --- | --- | --- |
| Plotted | until spool, or 30 s | own bubble; the plot is a server-held preview (route, D and T1 are recomputed at spool start) | spool request, or expiry |
| Spool | `SpoolTicks` (default 5 s; the longest of the supporting drives) | own bubble. Auto-aligns to the route; exterior doors lock (a door lock, not a seal: D66). The full plan goes to the crew at spool start | T0. Cancel, interdiction or a lock can end it only until T0 − 30 ticks |
| Cruise | T0 … TCommit | `Warp` placement, no exterior body. Beacon goes out at `TBeacon` (below) | commit tick |
| Committed | TCommit = TDrop − N … TDrop (N = 180, 3 s) | same; corridor announced; the route blends onto the drop track | TDrop |
| Real space | TDrop = T1 − NR … T1 (NR = 120, 2 s) | **simulated body** in the bubble chosen at TDrop (rails if it has no anchor), braked by the drive field. Collides | T1 |
| Arrived | T1 | normal; the brake field ends, `GridInertial` clears at T1 + 1 | n/a |
| Cooldown | `CooldownTicks` per drive, plus a ship cooldown = latest participant end (`ShipWarpCooldownEnd`, see "Multiple drives") | normal | ready |

**At engage (T0):**
- **What goes along:** the root ship's grid frame subtree. That covers everything in the grid (characters, loose items), docked ships (child grids with fixed poses) and ships in hangars (nested grids).
  - A foreign ship parked in the hangar goes along too. Hangar landing permission is the consent.
  - `ALocal` is the ship origin in `DepFrame` **at T0**, not at plot time. This keeps `p` continuous through the alignment and drift during spool.
  - **Hull riders go along (D66, proposal).** A character grounded, mag-booted or tethered on the hull is already a member of the grid (frames-and-zones.md: the ground rule beats the volume rule), so it is inside the bubble and is carried, crew or not. This is what makes boarding during a warp possible: get on the hull before T0.
- **What stays behind:** everything in the exterior bubble: bodies touching the hull from outside, other ships, and characters floating near the hull without a grip on it. They keep the hull point velocity in the exterior bubble.
- **Only a root ship can warp,** meaning one whose grid's parent is not a grid. Docked and hangared ships travel with their host.
- **Spool refuses to start, or is cancelled before T0 − 30 ticks,** while:
  - a **crew member** is within 50 m of the hull but not a member of the grid (floating outside with no grip or tether). The pilot sees who and can choose to leave them behind. Crew on the hull do not block: they go along, and the plot panel lists them ("on hull: 2"). Non-crew never block: standing on someone's hull must not pin their ship in place.
  - an **explicit lock** holds the ship: a landing clamp, a docking collar to a grid outside the carried subtree, or a tractor lock (Hold or Haul) with `F_avail ≥ m · WarpHoldAccel` (0.5 m/s², tractor-beams.md). A pull-only Hold tether from an emitter without antimatter (D28) still blocks under the same rule. Sweeps never block, and the ship's own tractor locks release on a spool request. A lock on another party's ship without its consent is a weapon use (D26): an `IncidentRecord` of kind `TractorLock` (factions-and-law.md), so anti-escape locks are always hostile acts. Incidental contact does not block; the touching bodies simply stay behind.
  - Damage does **not** cancel spool by default (knob `SpoolDamageCancel`). Otherwise light fire alone would pin any ship; interdiction is the intended counter.
- **The last 30 ticks are locked in.** After T0 − 30 nothing cancels, so a crew client holding the plan from spool start is never wrong under 0.5 s of latency.
- **Departure from the bubble** uses the existing transfer: destroy the ship compound body (the shape refcount is kept). If the bubble empties, it goes back to the pool. Members that only this ship's crew anchored leave by the existing 3 km rule.
- **Placement:** `Mode = Warp`, `Frame = Lca`, and `Bubble`/`Body` are invalid. The grid frame's parent becomes `Lca`, and its pose is written in tick step 2b. Membership checks skip `Warp` placements: no SOI, band or bubble transitions.
- **Grid scene:** exterior door proxies (terrain and hulls mirrored at openings) are deleted at T0 and come back at T1 + 1; there is nothing outside to mirror. **Nothing collides as closed that is not closed** (D66): doors, airlocks, barrier frames and holes keep their real state. Outside the hull is the bubble's pocket of vacuum, still in grid coordinates (see "The warp bubble is real space"). Undocking and hangar launches are refused until T1 + 1: the collars and clamps are latched, which is a mechanism and not a field.
- **Departure inside an atmosphere is allowed** (no floor, D16). The hull vanishing leaves a void that collapses as a thunderclap: a `WarpBlast` with only the displacement term (see "Low and atmospheric exits"). It is loud and harmless except at point-blank range.

**During cruise (cheap by construction):**
- **The interior is unchanged.** The grid bubble steps only if awake (0 cost asleep). Gravity is the plating vector if powered, otherwise zero.
- **No fictitious forces.** The grid has a `GridInertial` flag from T0 to T1 + 1. The grid's proper-acceleration history is reset at T0, at TDrop and at T1 + 1. Without the resets, the velocity snaps at engage and drop would reach an unplated interior as a one-tick spike of about 1e5 g.
  - **The brake is not felt either.** In the real-space segment the brake field acts on every body of the carried subtree alike, like gravity, so it is excluded from proper acceleration. **Contact impulses are not excluded.** A collision during the drop is felt inside, and the inertial dampener (D9) absorbs it up to its rating, as for any other impact.
  - The same flag covers **unanchored rails ships**, for example crew who are offline: zero-g unless plated. It does not change the zones anchor rule. A ship with an online player aboard still has an exterior bubble.
- **Ship attitude is locked** to the route tangent until TDrop, then to the corridor direction until T1. The pilot cannot steer a warp. The drive's only warp input is **Abort**, which is wired like any other data input (below).
- **The cruise is intangible from outside.** There are no collisions, and routes may cross bubbles, ships and other routes. Inside the bubble space is ordinary (D66, below).
  - At cruise speed it crosses a 50 km observer sphere in less than a tick, so mid-route visibility is not modelled.
  - Only the 2 s real-space segment is tangible (see "Real-space segment").
- **No gravity, SOI checks or frame changes in cruise.** Patched conics are suspended, and the frame stays `Lca` until TDrop.

**The warp bubble is real space (D66; replaces round 5's "sealed subtree"):**
- **The drive seals nothing.** No field shuts an opening or holds air. A hole cut or blown in the hull (D11, D59) is a hole in cruise as at rest. What is held is held by a mechanism:
  - **Latches:** docking collars and hangar clamps stay latched from T0 until T1 + 1 (with no brake: until the first real-space tick). Jettison racks and the core's eject cradle stay latched until the first real-space tick (see Latches below).
  - **Door locks:** exterior doors and airlocks lock from spool start to T0, and again from TCommit until the brake ends (T1 + 1, or the first real-space tick with no brake), so membership is settled when the ship leaves and when its body is created. A cycle in progress returns to "both closed" (ship-atmosphere.md). In cruise between those they work normally. It is the door's own lock: it can be overridden, forced or cut like any locked door.
- **Air** (ship-atmosphere.md owns the simulation; these are the inputs warp gives it):
  - **Ambient is vacuum** for every grid of the carried subtree from T0 until the first real-space tick, wherever the ship left from. From then it is the attach frame's ambient at the ship's altitude, through the frame-change refresh ship-atmosphere.md already has.
  - **Exterior edges flow all the way through.** Breaches, open doors and dropped barriers vent in cruise at the normal rates (D65). Warp is no refuge for a holed ship: a 5 AU warp is 14 minutes of venting.
  - **Air barriers** (D41, D61) and **emergency shields** (D42, D62) work as at rest, with their normal draw and hold time. Emergency shields still don't stop bodies (D63).
  - A ship that engages inside an atmosphere with a door open loses that room's air from T0. A pressure sensor split to `Cancel` is the designer's guard against warping holed.
  - **Vent thrust** does nothing in cruise (the pose is written from the formula). In the real-space segment the brake servo absorbs it.
  - **Cost:** two ambient-change events per warp (T0 and the first real-space tick). Nothing is polled, and a tight ship still reports 0 active rooms through a warp.
- **The pocket (`WarpShell`):** the bubble encloses the carried subtree's bounding box in root-grid coordinates plus `WarpShellMargin` = 20 m (knob), fixed at T0. Inside it is ordinary vacuum at rest relative to the ship. Outside the hull there is no plating, so it is zero-g, and `GridInertial` keeps the cruise from being felt.
  - **It lives in the grid scene.** A body that goes out through a hole or a door stays in the grid scene, in grid coordinates. While the shell exists, the shell test replaces the grid's leave rule (frames-and-zones.md: outside the interior volume + 1 m and not grounded) in the same staggered 10 Hz membership check. There is no new scene and no per-tick cost. The hull's outer faces already collide there for mag-booted EVA.
  - **EVA in cruise is allowed.** Crew suit up (D64) and go out through an airlock to patch a breach from outside or to reach wiring. Hull riders can cut their way in. Wind through a breach carries bodies out (ship-atmosphere.md), and they have the margin to catch a grip or be caught.
  - **Nobody arrives from outside in cruise.** The cruise is intangible, so no ship can match it, dock or shoot. Anyone who boards during a warp was aboard or on the hull at T0.
- **The wall (proposal; open question 7):**
  - **In cruise, whatever crosses the shell is lost.** A character dies (ordinary death and respawn rules), an item or loose body is destroyed, and a child grid that tore its clamps is destroyed with what is in it. Gas goes the same way. One reliable event per loss goes to the crew. The wall is visible from the hull (the streak field starts there).
  - **Blame:** when an opening with an actor carried someone out, ship-atmosphere.md's edge attribution writes the `IncidentRecord` (`PersonHarm`); factions-and-law.md decides the offence, as for venting at rest.
  - **Why lost and not left behind:** leaving a body behind in real space needs an entity created at an arbitrary point of the route (up to 16,700 km per tick), a lone bubble in deep space for a stranded player, an extra shard hand-off, and it lets a craft leave a warp with no commit, beacon, exclusion push or occupancy test. Lost costs nothing and bypasses nothing.
  - **In the real-space segment, with the brake,** the shell stays until T1 + 1 and the brake acts on everything inside it, hull riders included. A body that crosses the shell then leaves the carried subtree into the exterior bubble at the ship's current velocity, unbraked: a projectile on the overrun path with the ship liable (see "Real-space segment").
  - **With no brake** (a hard cancel, or the brake lost) the shell ends at once and the grid's normal leave rule returns, because nothing aboard is being decelerated relative to the ship.
- **Latches (jettison and eject):**
  - A cargo jettison commanded in cruise is queued and its rack releases at `T_exit + 1`. A core eject runs its 30-tick sequence in cruise and the cradle releases at `max(T_exit + 1, command + 30)`. Anything released from then on is unbraked.
  - **Why these stay latched** when a hand-carried item can simply be thrown at the wall: a runaway core fed to the wall would make core ejection free. exotic-matter.md's rule stands: nothing resolves until the ship is back in ordinary space, where the alarm is public.

**Crew, wiring and cancel sources (D17):**
- **Crew move freely and interact with anything aboard:** consoles, inventories, repairs, fights, sabotage, and work on the outside of the hull (D66). The interior is an ordinary grid bubble, so nothing here costs more than at rest.
- **The drive is a wired component.** Wiring has two media, **power and data** (D71); there is no Control medium. A data cable is one-way and can be split to any number of inputs. The drive's ports are:
  - **Power in:** from the ship's buses through breakers;
  - **Data in:**
    - `Engage`, from the plotting console;
    - `Plan`, the nav data from the nav computer;
    - `Cancel`, an abort raised by any console or automation the designer split a line to: the helm, an engineering panel, a hull-breach or pressure sensor;
    - `Hold`, the fail-safe line. The drive aborts when `Hold` goes dead: an e-stop on the line opens it, and so does cutting the cable or losing its source. Because data is one-way, the stops sit in line between the source and the drive; the drive needs a live `Hold` to spool;
  - **Local panel:** on the drive itself, no wire, so an engineer at the drive can always stop it;
  - **Data out:** `Beacon`, to the beacon antenna (D18, below).
  
  Abort (below) is `Cancel` asserted, `Hold` dead, or the local panel. What can stop a warp is therefore up to the ship's wiring. Cutting or splicing a wire is a gameplay action, done from inside or outside (in cruise too, D66). Port and signal names and their dead-line behaviours are ship-design-and-construction.md's; the cancel semantics are this doc's.
- **Cancel sources while cruising** (before TCommit):

| Source | Kind | Brake in the real-space segment |
| --- | --- | --- |
| Abort: Data `Cancel`, a dead `Hold` line, or the drive's local panel (helm, engineer's panel, automation) | soft | yes |
| Interdiction (below) | soft | yes |
| Power below the hold threshold for 30 ticks: an engineer opens the breaker, a bus fails, or a power wire is cut | hard | **no** |
| Drive `Integrity` < 0.5, destroyed (sabotage from inside, chamber or diluter breach), or its core ejected (D13) | hard | **no** |
| Support lost: the supporting drives' combined `MassCap` falls below the carried mass (see "Multiple drives") | hard | only if the remaining drives still hold power |
| Runaway on any supporting drive, from sabotage or interior damage (the D12 override is refused in `Warp` placement, exotic-matter.md) | hard | **no** |

  - **Every cancel becomes an exit** on the original route (see "Abort and late changes"). The collapsing bubble carries the ship for the 3 s commit window, then drops it into real space. The commit and the announcement still happen, so every peer gets 3 s of warning.
  - **Soft cancels** keep the brake. **Hard cancels** drop the ship at the full 2 km/s relative to the drop-rule velocity, with no brake, and it keeps that speed: a ship-shaped projectile until the pilot flies it. The overrun rule (below) keeps that line clear of grids that didn't consent.
  - **With several drives,** power loss, damage or ejection on one drive only removes its share of support. It cancels the warp only through the support row. A runaway on any one of them cancels the whole warp.
  - **Cancels after TCommit** don't change the exit, because the ship is already arriving. Between TDrop and T1, killing the drive's power (or cutting its wire) removes the brake at once, and the ship carries on at whatever speed it had. That is the deliberate ramming lever. The overrun rule decides what such a ship can hit; anything it does hit is a hostile act logged against whoever cut the brake.
- **Runaway and core ejection mid-warp** (D12, D13; rules in exotic-matter.md "Runaway burst"):
  - A runaway active in `Warp` placement is a hard cancel (before TCommit; after it the ship is already arriving). Nothing resolves while the ship has no real space. **Every burst due between the lock-in (T0 − 30) and the first real-space tick is rescheduled to `T_exit + 181`**, with the prime-only yield, where `T_exit + 1` is TDrop (or TDrop' after a cancel, or the throat exit). Since onset ≤ T_exit, `max(burst tick, T_exit + 181)` always equals `T_exit + 181`; exotic-matter.md uses the same expression. Bursts due on or after `T_exit + 1` resolve as scheduled. This is exotic-matter's lock-in deferral ("Timing and the warp lock-in"), applied to runaways that start in cruise too, so every mid-warp runaway gets the standard 3 s alarm in real space.
  - **The alarm goes public on the first real-space tick:** `RunawaySignature` to everyone within 50 km, as at onset at rest. If onset was before commit, `WarpArrival` and the beacon also carry a runaway flag.
  - **Core ejection is the counterplay and works mid-warp.** Purge commanded in cruise is queued to `T_exit + 1`; its 120 ticks beat the burst. An eject commanded in cruise runs its 30-tick sequence with the cradle still latched, and the core separates at `max(T_exit + 1, command + 30)`, unbraked. With +20 m/s (+40 for B/A) that gives 60 m (120 m for B/A) of separation at the burst. The core leaves the carried subtree, so the field doesn't brake it: after a normal arrival or a soft cancel it runs on along the overrun path at up to 2 km/s relative while the ship brakes, and bursts there, which the overrun rule keeps ≥ 300 m clear of every hull. After a hard cancel ship and core share the unbraked path.
  - **Bound on warp-delivered runaways:** the burst falls at T1 + 1 s at the earliest, with the ship near E, at least 300 m beyond the target's hull (ring standoff). The near-field brush (≤ 120 m) can't reach the target. Only the far-field pulse can, and the `IncidentRecord` (kind `AmBlast`) holds the runaway ship liable.
  - Off-spec runaways still resolve during spool (exotic-matter's `SpoolTicks ≥ MixTicks + RunawayDelay + 30`). The D12 override (scuttle or kamikaze) is refused in `Warp` and `Throat` placement, so a kamikaze arms its public 10 s override in real space after T1. Only sabotage or interior damage can start a runaway mid-warp.

### Route (smooth, server-computed, D15)

- **`Lca`:** the lowest common ancestor of the departure frame and the target frame, raised to the nearest frame that is neither rotating nor a grid (`BodyInertial` or `Solar`). Examples:
  - planet orbit → moon station: planet inertial;
  - planet A → planet B: Solar (double ulp at 1 AU ≈ 3e-5 m).
  - Both endpoints must be in one star system. Each system is its own Solar root (frames-and-zones.md), so there is no `Lca` across systems and such a plot is refused; systems are linked only by wormholes.
- **The route is a C¹ cubic Hermite spline through live knots:** the departure point `a`, up to `MaxWaypoints` = 4 avoidance waypoints `w_j`, and the drop point `b`. The pilot never places via points. The server builds the route at plot (preview) and again at spool start (authoritative).
- **Every knot is fixed in its own frame and moves with it:**

```
a(t)   = Pose(DepFrame → Lca, t) · ALocal                                    ALocal captured at T0
w_j(t) = Pose(BodyInertial_j → Lca, t) · WLocal_j                             fixed beside the body it avoids
b(t)   = Pose(TgtFrame → Lca, t) · BLocal                                    t < TCommit (live target; BLocal = planned drop point)
b(t)   = Pose(AttachFrame → Lca, t) · (Q + V·(t − TDrop)/60) + (1 − w(t))·δ   t ≥ TCommit (announced drop track)
         δ = b_live(TCommit) − track(TCommit), computed locally by each peer;  w = smoothstep((t − TCommit)/N)
s(t)   = x(τ(t)) / D                                                         0 → 1, arc-length fraction
u(s)   = ArcTable(s)                                                         spline parameter, 64-entry table built at spool start
p(t)   = Hermite(u; knots, tangents), each piece evaluated from its nearer knot   exact at a, at every w_j and at b
```

- **Tangents:**
  - At `a`: toward the next knot. If `a` lies inside its body's `RouteShell` (below), the tangent is bent to at least 10° above the local horizon, so a surface departure climbs instead of cutting into the ground.
  - At `b`: the corridor direction `d_r` (see "Real-space segment"), so the route runs straight into the drop with no kink.
  - At the waypoints: Catmull–Rom (centripetal), which avoids cusps and self-intersections between closely spaced knots.
- **Waypoints and live endpoints:**
  - Waypoints ride with their bodies, so a moon that moves 6,000 km during a long warp carries its detour with it.
  - Changes in the live endpoints only reshape the route slightly. The profile uses D only to shape speed, and `s = 1` still lands exactly on `b`.
- **Before commit, the endpoints are live.**
  - Rails targets (bodies, surface points, stations on Kepler rails, coasting ships) are exact through `FrameTree.TryPoseAt`.
  - Simulated targets use the current pose on the server and the interpolated pose on clients. The difference is visual only.
- **From commit, the drop point follows the announced drop track (Q, V)** in `AttachFrame`:
  - `Q = E − U·(NR/60) − d_r·L_r`;
  - `V = U + v_r·d_r` (see "Real-space segment").
  - The track is identical on every peer and ends exactly on Q at TDrop.
  - E, Q and the corridor are quantised to 1/1024 m before use (C13), so the server and clients hold the same values.
  - δ absorbs everything the commit changed: a target's thrust since the extrapolation, a slide, an exclusion push, an overrun rotation. It blends to zero over the 3 s before the drop, so no peer sees a jump.
  - A client that receives `WarpArrival` late starts its blend at the receive tick, over the ticks that remain.
- **Target loss:** if the target enters warp, is destroyed or despawns before commit, b freezes at its last pose in its exterior frame at that tick.
- **Speed profile:** speed is proportional to the distance along the route from the nearer endpoint, with a cap. It is closed form. D is the route's arc length from `a` to the drop point. The 2 s real-space segment comes after it.

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
- **Drop tick (computed at spool start, not at plot):**
  - Solve `D ← RouteLength(T0 + T(D))`, using extrapolation for simulated targets. Stop after 3 iterations or once the change is under 1 m.
    - The contraction ratio is about `|u_tgt| / (D/T)`. If it is ≥ 0.5, reject with "target too fast for a hop this short". At the 50 km minimum (8 km/s average) that limits a hop to targets moving under about 4 km/s in `Lca`.
    - D only shapes the profile, because the endpoints are live. Any residual error slightly changes speed, never the end point.
  - Set `TDrop = T0 + ceil(60·T)` and `T1 = TDrop + NR`. Evaluate the profile at `τ = (tick − T0)/(TDrop − T0) · T`. This makes `s(TDrop) = 1` exactly on a tick.
  - `TCommit = max(T0, TDrop − N)`. The minimum warp distance is 50 km (6 s, plus the 2 s real-space segment); shorter than that, fly.
- **Velocity:** position is continuous at T0, TDrop and T1. Velocity snaps at T0 and at TDrop, from V0 = 5 km/s along the route to `V` (2 km/s relative to the exit track). It is continuous at T1, because the brake ends exactly on U. The flash covers the snaps visually, and `GridInertial` hides them from the interior.
- **Route building (replaces line-of-sight rejection and manual via points):**
  - **`RouteShell(body) = max(AtmosphereTop, TerrainMax + 2 km, 0.01·R)`**, an altitude; the shell sphere has radius `RS = R + RouteShell`. This is the old `WarpFloor`, now a routing clearance only and never a limit on endpoints. Exception: hazard bodies override it with their own sphere, which is also an exit exclusion (exotic-matter.md's black hole: `RouteShell(BH)` = 1.1 × `DiscOuter`). Cruise stays outside every shell except where it climbs out of the departure's shell and descends into the drop's. Aborts and interdictions mid-route therefore almost never drop a ship inside an atmosphere by accident.
  - **Prune first.** Skip any body whose orbit's bounding sphere about its parent (apoapsis + R + shell) misses the route's bounding capsule (the hull of its knots). That costs ~5 ns and needs no Kepler solve.
  - **Then the capsule test.** For the remaining bodies (typically 5 or fewer), split the route into 32 equal-s segments. Test each as a capsule against the body's shell sphere, with body poses at the segment's mid tick plus a margin for body motion.
    - Each endpoint's own body is tested only beyond 2 × the endpoint's distance to the shell, so a low endpoint doesn't block its own climb or descent.
  - **Add a waypoint for the worst violation:**
    - Find the body centre c at the violating segment's mid tick, and the route's closest point to it, x.
    - Place the waypoint at `c + n̂ · 1.15 · RS` (the shell radius, not the altitude), where `n̂ = normalise(x − c)`. If `|x − c| < 1 m`, use the route plane's normal, or the body's orbit normal when the route is radial.
    - Store it in that body's inertial frame and re-test.
    - Repeat up to 6 times with at most 4 waypoints. Push a waypoint out by 1.25× when the spline still cuts the shell between knots.
  - **Same-body hops** fall out of this: a chord through the body gets a waypoint above its midpoint, and near-antipodal hops get two or three. The old ~90 km chord-sag limit is gone.
  - **No route** after 6 iterations (rare: dense moon systems) rejects the plot with "no clear route". That reason uses only rails bodies, so the client can know it.
  - **Runs as a job;** the result arrives the next tick. The preview draws the route.
- **Determinism:** `Math.Exp` and the spline are not guaranteed bit-identical across OS libms. Clients use the formula for visuals and their own grid pose. The authoritative waypoints go out in `WarpPlan`, and the authoritative drop and exit in `WarpArrival`.

### Speeds and travel times

Default drive: `V0 = 5 km/s`, `K = 0.3 s⁻¹`, `VCap = 1e6 km/s`. The two ramps take 81 s in total and cover 6.7e6 km. Spool (5 s) and the 2 s real-space segment come on top. Times are along the route, and detours around bodies add a little.

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
- **Fuel:** `cost = FuelPerLn · mass · ln(1 + D / 1e6 m)`, with mass including docked and hangared ships and cargo. It is consumed at T0, and an abort gives no refund. With several drives, `FuelPerLn` is the effective (power-weighted) value, and each drive pays its weight's share from its own feed (see "Multiple drives").
- **Role in travel economics (D23).** This replaces the old blanket "warp cheaper, wormholes costlier":
  - **Natural wormholes are the cheapest** way to cover a span they serve, but they go only where they go: other star systems, fixed mouths.
  - **Manufactured wormholes are the fastest.**
  - **Warp is the cheap way to a specific place relatively close by:** in-system, minutes per AU. There is no interstellar warp: systems share no frame (see `Lca`). It is not a missing feature either, because a light year at `VCap` would take about 110 days, and the ln fuel curve would make it cheap (about 2× a 1 AU warp), which would undercut natural wormholes.
  - **Tuning checks** (wormholes.md and exotic-matter.md own the wormhole prices):
    - natural wormholes are the only interstellar link, so there is no warp price to undercut. If a natural pair ever has both mouths in one system, its crossing, owners' fees included, must cost less than warp to the same mouth;
    - for any trip a manufactured wormhole serves, its time is below warp's;
    - for in-system trips to a chosen point, warp is cheaper than a manufactured wormhole.
    
    The ln fuel curve keeps long in-system warps cheap (1 AU costs about 4× a moon hop), which is what makes warp "cheap for somewhere specific".

### Multiple drives (D14)

A ship may carry several warp drives. **Their bubbles support each other:** they spool together and form one bubble around the carried subtree, with a quality that is the power-weighted average of the drives.

- **Who supports (the one joining rule; exotic-matter.md and ship-design-and-construction.md cite it):** a drive joins if all of these hold:
  - it is on the root grid, or on a docked ship whose collar carries power and data wiring (D71);
  - it is reachable from the plotting console's `Engage` (a Data input, ship-design-and-construction.md), and its `Hold` line is live. Abort comes from a dead `Hold`, a Data `Cancel` or the drive's local panel;
  - `Integrity` ≥ 0.5;
  - the drive itself is out of cooldown;
  - it is Hot or primable within the spool, and its fuel plan passes (exotic-matter.md);
  - `f_i ≥ 0.5` (below).
  
  A drive that fails any of these simply doesn't join. It doesn't block the others. The root ship's own `ShipWarpCooldownEnd` (below) is the only thing that blocks the whole ship.
- **Per drive i, fixed at spool start:**
  - power fraction `f_i = clamp(P_i / PRated_i, 0, 1)`, where P_i is the power its wiring actually delivers. A drive with `f_i < 0.5` cannot form its part of the bubble and doesn't join;
  - weight `w_i = min(P_i, PRated_i) = f_i · PRated_i` (watts provided, capped at the rating). Without the cap, overfeeding one high-`VCap` drive would buy it unbounded weight and drown out the others. Averages are written `Σw·X/Σw`; exotic-matter's normalised `P_i/ΣP` form was the same thing and is deleted;
  - at most 8 drives support one warp (`WarpDriveSet`, the one struct; exotic-matter's `DriveGroup` is folded into it): the 8 with the largest `w_i` join, and the rest don't (ship-design-and-construction.md caps at 8 too).
- **Effective drive:**
  - `X = Σ w_i·X_i / Σ w_i` for X in {`V0`, `K`, `FuelPerLn`, `VReal`, `TReal`};
  - `VCap = Σ w_i·f_i·VCap_i / Σ w_i`, so an under-powered drive drags the cap down;
  - `MassCap = Σ f_i·MassCap_i`: support **adds**, so two drives carry a heavier ship than either alone;
  - `SpoolTicks = max(SpoolTicks_i)`: the bubbles must phase-lock;
  - **cooldown:** every participating drive starts its own `CooldownTicks`. The root ship also cannot spool again until `ShipWarpCooldownEnd`, the latest cooldown end among the last warp's participants (anti-chaining: a ship can't warp on drive A, then at once on drive B). It is stored per root grid, not in `WarpTable`;
  - fuel: each drive pays `w_i / Σ w_j` of the cost from its own feed. Exotic-matter feed and runaway rules apply per drive.
- **The profile is fixed at spool start.** Losing a drive mid-warp never reshapes the route or the timing, because the closed form stays closed:
  - recompute the support `Σ f_i·MassCap_i` over the drives still holding, event-driven;
  - if it is still ≥ the carried mass, the warp continues unchanged;
  - otherwise it is a hard cancel ("bubble can't hold").
  
  The hold threshold for power is the same `f ≥ 0.5` per drive.
- **Fleets don't pool drives:** each root ship holds its own bubble. Fleet warp (below) shares timing, not support.
- **Exotic-matter.md's single-drive assumptions** (one chamber per spool) need a matching note: each supporting drive mixes and can run away on its own.

### Choosing the exit point (the zone decision)

| Target | Default exit point | Attach frame | Arrival velocity |
| --- | --- | --- | --- |
| Body orbit | On the chosen circle (any altitude clear of terrain; default plane equatorial prograde), nearest the approach line | Band rule (below) | Circular, prograde, converted through the transfer maths |
| Surface point / outpost | Above the point at the pilot's exit altitude: default `QuietAltitude`, minimum `ExitClearance` (see "Low and atmospheric exits") | BodySurface | Zero in the surface frame (hover; the pilot descends) |
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
  - **Standoff** = target radius + ship radius + `max(300 m, ½·a_tgt·((N + NR)/60)² + 100 m)`, where `a_tgt` is the target's maximum thrust acceleration (0 for rails targets). The term covers a target that burns hard during the 5 s from commit to T1: about 470 m at 3 g.
  - **Orbiting target:** the ring lies in the along-track / cross-track disc, perpendicular to the radial.
    - An along-track offset at LVLH rest is a Clohessy–Wiltshire equilibrium, so the arriving ship holds station.
    - A radial offset drifts at about 1.5·n·Δr. For 500 m in low orbit on a 1000 km 1 g planet that is about 2 m/s, 140 m a minute away from a friend's ship.
  - **Other targets:** the hemisphere facing the approach.
  - **Dispersion:** the position on the ring comes from a per-warp server seed, within ±60° of the approach or along the station's arrival lanes. There is no single camp point. The ring point is fixed at `TBeacon` and revealed by the beacon (30 s before the drop, near the exit only), or at commit for a silent warp. It is never revealed earlier.
- **Exclusion zones (any grid, not only the named target):**
  - Each grid has an `ExclusionRadius`: stations 5 km by default, ships 2.5 km (beyond a bubble's 2 km soft radius).
  - **Sieges (D27):** the exclusion push for non-permitted arrivals and a station's interdiction ban both hold during a declared siege (anti-telefrag: besiegers still drop 5 km out). A siege lifts only zone courtesies and tractor zeroing toward besiegers (factions-and-law.md "Sieges and capture (D27)").
  - **Raised region shields (D58, factions-and-law.md "Planetary and city shields").** A raised Town or City dome, or a Planetary shell, is an exclusion volume like a grid's: a `WarpExclusion` in region form (`body, direction, radius`). An exit point or corridor inside it is pushed out to the shell + ship radius + 300 m at commit. Shield state is public, so the client can check it and the plot shows it as a visible exclusion zone. The authority's traffic control may permit arrivals on published lanes or above its `QuietAltitude`. The push is physical, not legal: a drained, unpowered or destroyed shield excludes nothing, and there is no no-drop law for open ground (D16 stands).
  - An exit inside a grid's exclusion zone needs that grid's permission: owner, crew, party, or a station's permission hook (`ZonePermission.Check(zone, owner, WarpArrive)`, factions-and-law.md). Stations are public by default unless restricted.
  - **Permission can be conditional.** A station's hook may grant arrivals only along its published lanes or above a drop altitude it sets (for example, a surface base asking for drops above its `QuietAltitude`). An arrival that breaks the condition is treated as non-permitted and pushed out like any other. This is the grid owner's policy, not a global exit floor (D16), and it stops "public" from meaning "may be warp-blasted at 250 m".
  - Without permission, the exit is pushed out radially to the exclusion radius at commit, silently.
  - A hostile warping "onto" a ship therefore appears as its own zone 2.5 km out and has to close under thrust. The predicted merge joins it before contact.
  - **The exclusion push still applies with the real-space segment,** and it now covers the whole corridor. E is pushed to the exclusion radius, and the corridor capsule (Q → E, ship radius wide) must also stay outside every non-permitting exclusion sphere. The overrun rule (below) covers a brake that fails.

### Real-space segment (D18)

The last 2 s of every warp happen in real space. The ship is a simulated body there: a large ship-shaped projectile.

- **Nominal motion,** relative to the exit track in the attach frame, for `TDrop ≤ t ≤ T1`:

```
x(t) = E + U·(t − T1) − d_r · ½·a_r·(T1 − t)²        a_r = v_r / T_r
v(t) = U + d_r · a_r·(T1 − t)
drop state: Q = E − U·T_r − d_r·L_r,  V = U + v_r·d_r,  L_r = ½·v_r·T_r
```

- **Defaults** (`WarpDrive.VReal`, `TReal`; per drive class, power-weighted with several drives):
  - `v_r = 2 km/s`, `T_r = 2 s` (NR = 120 ticks), so `a_r = 1,000 m/s²` (about 100 g) and `L_r = 2 km`.
  - **Why these numbers:** the segment fits inside one bubble's 4 km hard radius. It is long enough to be a real projectile (33 m per tick at the start, so `LinearCast` applies). It is short enough to keep the brake's energy, which is what an atmospheric drop dumps, at a meaningful but bounded ½·m·v_r²: 4e12 J ≈ 1 kt TNT for a 2,000 t ship.
- **The brake is the drive's collapsing field.** It is a velocity servo onto the nominal `v(t)`, with U propagated on rails (gravity included), clamped to 1.25·`a_r` and applied to every body of the carried subtree alike.
  - With no disturbance it reproduces E and U at T1 to within the servo error (test: < 1 cm, < 1 mm/s).
  - In an atmosphere it also cancels air drag, so the nominal holds. The energy goes into the blast (below).
  - After a collision it keeps braking toward the nominal velocity, from wherever the ship now is, until T1. Any leftover relative velocity remains the pilot's problem.
  - **No brake when the field is gone:** after a hard cancel, or when the drive loses power or is destroyed or ejected after TDrop. The ship then continues ballistically at its current velocity.
- **Corridor direction `d_r`** (chosen at commit, announced in `WarpArrival` and in the beacon):

| Target | Default `d_r` |
| --- | --- |
| Grid on its ring | Tangent to the ring (perpendicular to the line to the target), on the approach side |
| Body orbit | Along-track (prograde) |
| Surface / marker / free point | Horizontal (within ±10°) along the route's final heading |
| Abort / interdiction drop | The route tangent at `p(TDrop')` |

- **Overrun rule:** the corridor's ballistic extension must be clear, so that a failed or cut brake can't hit anything by accident.
  - The extension is the **ballistic path** from Q at V with no brake and no drag: the attach frame's gravity, propagated on rails, for `OverrunHorizon` = 10 s (about 20 km past the drop), tested as 32 capsules. A straight line was wrong near a surface: a horizontal unbraked ship falls about 490 m in those 10 s on a 1 g world.
  - It must clear every grid's hull sphere + 300 m and every non-permitting grid's exclusion sphere, up to the point where it meets terrain (coarse tile maximum + ship radius), if it does.
  - **Terrain is not a reason to move the exit** (D16, no floor). If the path meets the ground, a brake failure means a crash at that point, which is the pilot's risk: the preview shows it (orange), and a crash is an ordinary impact, filed as a permanent edit by planets.md.
  - **A raised region shell is treated like terrain here** (D58): the path is tested only up to where it meets the shell. An unbraked ship that reaches it is a projectile crossing a shield, absorbed or leaked by factions-and-law.md's bombardment rule, and a hostile act if the brake was cut.
  - If it isn't clear of grids, rotate `d_r` within ±60° of the default (8 tries, nearest first), then raise the exit (surface targets) or push E outward by 2× the sphere radius (others).
  - A brake failure therefore skims past the target. A deliberate ram has to be flown with thrusters after T1, like any other ram.
- **Collisions, both ways:** the ship is an ordinary dynamic body (compound shape, `MotionQuality.LinearCast`), and the normal collision damage rule applies to both parties.
  - A 2,000 t ship at 2 km/s carries 4e12 J. Whatever it hits is wrecked, and its own bow takes the same impulse.
  - Contact impulses reach the interior (see `GridInertial`).
  - **What it can hit:** commit and drop clear everything the server can predict (see "Commit" and "At TDrop"). Only bodies that move into the announced corridor after commit, debris below `OccupancyMinRadius` (2 m), and weapons fire can be in the way.
- **Liability** (written to the shared `IncidentRecord` that tractor beams (D26) and mining (D33) also use; kinds `WarpCollision`, `WarpBrakeCut`, `WarpBlast`, `BeaconTamper`; the record and what each kind costs are factions-and-law.md's):
  - Something that enters a **beaconed** corridor after the beacon: the intruder's liability. It was warned.
  - A **silent** arrival (military beacon off): ordinary rules. The arriving ship is liable for damage to non-hostile parties.
  - A **cut wire** (or any `Tampered` silence by its own side): the arriving ship is fined, and blamed for all damage its arrival causes (collision and blast). A beacon silenced by hostile fire falls back to the silent rule, with the hostile named in the record.
  - A brake deliberately killed after TDrop: a hostile act by whoever cut it (`IncidentKind.WarpBrakeCut`, class Hostile).
- **Bubbles:** the body is created at TDrop by the attach rule, which now tests the whole corridor capsule (see "At TDrop").
  - The brake counts toward the bubble's `MaxAccel` until T1. The predicted merge therefore treats the arrival as hot and joins any bubble it is closing on within the tick, which is the intended outcome for a ship decelerating into someone's zone.
  - After T1 `MaxAccel` falls back to the ship's thrust.

### Low and atmospheric exits (D16: no floor)

There is no exit floor. A warp may depart or drop anywhere that is clear of terrain, at any altitude and inside any atmosphere. The cost is the pressure wave.

- **`ExitClearance`:** the corridor and the exit sphere must clear the coarse terrain tile maximum by ship radius + 100 m. The exit must also be above the sea surface: submerged exits are refused in v1. On a body with no voxel surface (planets.md "Gas giants") its datum stands in for the sea surface. Hazard exclusions owned by other docs (the black hole's `RouteShell(BH)`) also refuse exits. Those are the only hard limits, and the client can check them. (The overrun path is not part of this: see the overrun rule.)
- **Blast model (`WarpBlast`),** evaluated once at TDrop (and at T0 for an atmospheric departure):
  - **Drop:** `W = f_air · ½·m·v_r² + 2.5·p(h)·V_hull`, where m is the carried mass and V_hull the hull volume. The brake field sheds the ship's real-space kinetic energy into whatever surrounds it.
    - `f_air = ρ(h) / (ρ(h) + ρ_c)`, with `ρ_c = 0.01 kg/m³` (knob) and ρ and p from the body's `AtmosphereId` at the drop altitude.
    - In vacuum f = 0 and the energy leaves as a harmless flash. In low atmosphere f ≈ 1.
  - **Departure:** only the displacement term `2.5·p(h)·V_hull` (the thunderclap).
  - **Overpressure** at a receiver at distance R from the nearest corridor point (conservative: the full W treated as a point charge there):
    - `Z = R·(p_rcv/p0)^{1/3} / W_TNT^{1/3}`, with `W_TNT = W / 4.184e6 J/kg`;
    - `ΔP = 2 · (p_rcv/p0) · (0.85/Z + 3/Z² + 5/Z³)` bar. That is the Sadovsky fit with Sachs scaling at the receiver, × 2 for ground reflection.
  - **Effect thresholds** (data, `data/hazards/blast.json`):

| ΔP | Effect |
| --- | --- |
| 0.07 bar | glass and light fittings break; Nuisance blast threshold |
| 0.2 bar | light structures and parked small craft damaged |
| 0.35 bar | people injured; wildlife stunned; the `WarpAttack` ring (D58) |
| 0.5 bar | wildlife killed |
| 1.0 bar | heavy structures damaged |
| 1.5 bar | people killed |

  - **Example:** a 2,000 t ship (W ≈ 950 t TNT) dropping horizontally over sea-level ground. Ground overpressure directly below:

| Exit altitude | ΔP below | Outcome |
| --- | --- | --- |
| 250 m | 2.2 bar | people killed within ~170 m of the ground track, wildlife within ~530 m |
| 500 m | 0.64 bar | wildlife killed under the track, people injured |
| 1 km | 0.23 bar | light structures damaged |
| 2 km | 0.10 bar | windows broken (fined) |
| 5 km | 0.036 bar | a loud boom |
| 20 km | 0.008 bar | distant thunder |

    Radii scale with m^{1/3}: a 20,000 t ship reaches about 2.15× as far.
  - **Size and density** enter through m (kinetic energy), V_hull and ρ(h). The effect fades with altitude in two ways: through `f_air`, and through the distance to the ground.
- **Resolution:** the server emits one `WarpBlast {centre, corridor, W, tick}`. Each system applies its own thresholds:
  - fauna and NPC populations (deaths, scatter);
  - characters (injury, death);
  - grids and placed structures, through the shared explosion damage rule;
  - clients (shock ring, sound, dust).
  
  Every damage result is logged to the `IncidentRecord` against the pilot and owner, with `IncidentKind.WarpBlast`. Beacons don't reduce blast liability; they only give people time to shelter.
- **Region shields (D58):** inside the same resolution, each raised region shield in reach is evaluated once (~0.2 µs). A dome of radius `r_d` at distance R from the blast intercepts `f = ½·(1 − sqrt(1 − (r_d/R)²))` of W; its pool absorbs what it can, and receivers **inside** the dome get the blast scaled by the fraction that leaked. Receivers outside are unaffected. The formula, pools and classes are factions-and-law.md's ("Planetary and city shields"); a Town shield takes about five 2,000 t drops at 250 m before it is empty.
- **The law (D58; factions-and-law.md owns the offence table, fines, standing and heat):**
  - **`WarpAttack` (atrocity):** the pilot dropped below `QuietAltitude`, and the 0.35 bar ring (people injured) predicted at commit covered a settlement footprint, a station or claim structure of another party, or a person outside the actor's party. The preview drew that ring with its warning, so the pilot knew. It is the offence whether or not anyone was hurt and whether or not a shield absorbed it.
  - **Reckless damage or Nuisance blast,** by result, for a drop below `QuietAltitude` whose 0.35 bar ring touched nobody (≥ 0.07 bar on property with nobody hurt is the Nuisance blast).
  - **What warp supplies:** at commit, for a drop below `QuietAltitude` only, one radius query of the 0.35 bar ring against settlement footprints, other parties' structures and people (~1 µs plus the query). The result rides in the `WarpBlast` event (`Ring035M`, flags `BelowQuiet` and `RingCovered`) and is copied into the `IncidentRecord`. Warp never classifies; the law doc does.
  - An interdictor that forces a drop carries the offence instead of the pilot. AI pilots never make a warp attack (see "AI pilots").
- **`QuietAltitude(body, ship)`:** the lowest exit altitude whose predicted ground ΔP stays under 0.07 bar. It is about 2.7 km for a 2,000 t ship over sea-level ground (Z ≈ 27.6), and 0 on airless bodies.
  - It is the **default** exit altitude for surface targets. The pilot may go lower, down to `ExitClearance`.
  - Aborts and interdictions that drop inside an atmosphere use the same model. An interdictor that forces a drop is liable for the blast it causes.
- **Cost:** one evaluation per atmospheric drop, about 1 µs plus a radius query for receivers, ~0.2 µs per region shield in reach, and the commit-time ring query for drops below `QuietAltitude`. It is not per tick.

### Arrival beacon (D18; replaces the crew-only privacy model)

A warp transmits its arrival as a safety feature, so that others can clear the corridor and shelter from a blast.

- **Hardware:**
  - The drive's **Beacon out** port is wired to a beacon antenna. Every drive class has one.
  - **Civilian drives** have no off switch. The beacon is silent only when the wire or the antenna is cut or destroyed, or the antenna is unpowered.
  - **Military drives** (a data flag on faction-restricted drive classes, bought with a commission: factions-and-law.md) have a `BeaconSwitch`, a Data input wired like any other (D71).
    - `MilitaryOff` is lawful only where the jurisdiction recognises the commission: the issuing faction, factions Friendly or better with it, and Lawless space. Anywhere else a silent arrival is the Unbeaconed arrival offence, the same as a cut wire (factions-and-law.md). Liability for what the arrival hits is unchanged.
- **State is fixed at T0:** `On`, `MilitaryOff` or `Tampered`.
  - Any civilian silence is `Tampered`: a cut wire, a cut or unpowered antenna (opening its breaker is a cut by another name), or a destroyed one. The input is `Logged` (ship-design-and-construction.md), so the `IncidentRecord` (kind `BeaconTamper`) names who did it. The fine and the blame go to that party: the ship's own crew or owner, or a hostile whose fire destroyed it. In the second case the arrival is judged by the ordinary silent rules, and the ship isn't fined.
  - If the wire is cut or repaired mid-warp, the state changes at that tick.
  - A beacon that has already gone out stays valid, and the corridor it gave is still binding for liability.
- **What it sends (`WarpBeacon`):**
  - warp id and the ship's transponder id;
  - drop tick and T1;
  - attach frame;
  - planned corridor (Q, E, `d_r`), U, and an uncertainty radius (0 for rails targets; otherwise target drift plus the maximum commit slide);
  - mass class and size;
  - whether the drop is atmospheric, with the predicted footprint radii at 0.35 bar (people injured) and 0.5 bar.
- **When:** at `TBeacon = max(T0, TDrop − BeaconLead)`, with `BeaconLead = 30 s` (drive data).
  - The ring point and corridor are fixed at TBeacon, not at commit. The beacon must be truthful.
  - At commit it is re-sent with the exact corridor (`uncertainty = 0`), and again if a T1 change moves the arrival.
  - Short warps beacon at T0, which gives as much lead as there is.
- **Who receives it:**
  - every client whose character or grid is within `BeaconRange` = 200 km of E in the attach frame (a radio broadcast; no receiver part needed in v1);
  - the target grid's owner, and the station's traffic control, at any range;
  - for an atmospheric drop, the planetary authority of the region (the authority of the `Region` holding, factions-and-law.md), which feeds cases, and any shielded settlement inside the predicted footprint (D58).
  - Nobody at the departure end receives it unless they are also within range of the exit.
- **Clients** draw an inbound marker with the corridor, ETA countdown and blast footprint, and AI ships avoid beaconed corridors.
- **Silent warps** (`MilitaryOff`, `Tampered`) send no beacon. Third parties then get only what they got before: the departure direction at T0, and the corridor at commit, 3 s before the drop.
  - A `Tampered` ship is flagged in the `IncidentRecord` (`BeaconTamper`) from T0 until the beacon is repaired, and a station's docking scan can read the flag.
  - **Liability:** all damage the arrival causes is the ship's (see "Real-space segment"), plus the Unbeaconed arrival fine in a jurisdiction (factions-and-law.md).

**Anti-camping, re-checked with beacons:**
- **What changed:** before, nobody but the crew learned the destination earlier than 3 s before arrival. Now a civilian arrival's corridor is public **30 s** ahead within 200 km of the exit. A camper already near a station can see a beaconed arrival coming and reposition: at 10 g it covers about 45 km in 30 s.
- **What still holds:**
  - **The departure side learns only a direction,** so you can't follow someone from their dock to their destination.
  - **Ring dispersion (±60°) still prevents pre-positioning** before the beacon, so a camper has to cover a ring, not a point.
  - **Exclusion zones** still keep non-consenting arrivals 5 km from stations and 2.5 km from ships. Stations can still forbid interdiction fields inside their exclusion zone. Both hold during sieges (D27).
  - **Stations can police their lanes:** a station's hook can flag armed loitering inside its exclusion zone as hostile.
  - **Hitting an arrival is never free.** Something that enters a beaconed corridor is liable for the collision, and the arrival's kinetic energy usually wins.
  - **The privacy option exists at a price:** military ships switch the beacon off, and civilians can cut the wire and accept the fine and full blame. Either way the arrival is back to the old 3 s window.
- **New exposure, accepted:**
  - The 2 s real-space segment is a window in which the arriving ship cannot steer.
  - A beaconed civilian arrival at a camped location is now interceptable.
  
  This is the user's trade (safety over privacy for civilians). A station that wants safe arrivals gets them by policing its lanes.
- **Unchanged:** plot rejections still never reveal hull occupancy, and the beacon is server-generated, so a client can't spoof one.

**UX (ImGui map first):**
- **Selecting a target snaps the exit to its ring or default.** Free placement shows a preview sphere:
  - green: a named target or joinable contact;
  - blue: open space, a new zone;
  - orange: an atmospheric drop, or an overrun path that meets the ground. It shows the predicted ground footprint (0.07 / 0.35 / 0.5 / 1.5 bar rings), the brake-failure impact point, and a fine warning. When the 0.35 bar ring of a drop below `QuietAltitude` covers a settlement, another party's structure or a person the client knows of, the warning reads "warp attack: atrocity" (D58);
  - red: invalid, for a reason the client can know (terrain clearance, submerged, no clear route, fuel, cooldown, mass over the drives' combined `MassCap`, a visible exclusion zone, a raised region shield).
- **The preview draws the route curve, the corridor and its overrun path,** and the beacon state (On, MilitaryOff, Tampered).
- **Joinable places show as contacts with arrival rings,** published by the server at 0.2 Hz per SOI. Bubbles never reach the wire.
- **A target must be in the requester's known contacts** (radar or interest). An entity id the client was never sent cannot be targeted.

**Server validation** (preview at plot, authoritative at spool start, exit re-resolved at commit):
1. **Drives:** the root ship past its `ShipWarpCooldownEnd`, at least one supporting drive (the joining rule in "Multiple drives"), fuel ≥ cost(D), and total mass ≤ the combined `MassCap`.
2. **Departure:** clear of terrain (`ExitClearance`; any altitude, any atmosphere), no crew floating off the hull (crew on the hull go along, D66), and no explicit lock.
3. **Route:** a clear route found with ≤ 4 waypoints. Interdiction fields on the route give a warning, not a rejection.
4. **Exit: only checks the client could reproduce.** These are terrain clearance and not submerged, the target being known, and a visible grid's exclusion zone or a raised region shield without permission. An atmospheric drop is a warning (footprint and fine), never a rejection.
   - Hull occupancy, hidden ships and reservations are **never** a reason to reject. They are resolved silently at commit.
   - "Exit blocked" as a reject reason, at 2 plots/s, would be a system-wide radar for hidden ships.

**Commit** (at `TCommit`, server):

Commits run in `(TDrop, WarpId)` order, so commits on the same tick see each other's reservations.
1. **Exit track:** E is the target state at `TCommit` extrapolated to T1 (exact for rails), plus the ring offset fixed at `TBeacon`. U comes from the velocity rule, and `d_r` from the corridor table.
2. **Exclusion push** for every grid that does not permit the exit and every raised region shield (D58), applied to E and to the corridor capsule.
3. **Overrun rule:** rotate `d_r`, then raise or push E, until the 10 s ballistic path is clear of grids.
4. **Occupancy:**
   - The test is the **corridor capsule** from Q to E, with radius ship radius + 300 m (a fleet uses its union capsule), against:
     - the bounding spheres of every body near the corridor of radius ≥ `OccupancyMinRadius` (bubble members, rails and frozen entities), each **coast-extrapolated** to TDrop … T1 with a 50 m margin. The margin deliberately covers no full thrust: a body that thrusts into an announced corridor accepts the collision and the liability;
     - every `ExitReservation` whose window overlaps `[TDrop − 2 s, T1 + 10 s]`.
   - This uses our own entity data. It is not a Jolt query, because the broadphase and CollideShape exports are unverified (P1).
   - If blocked, slide the corridor along the ring or approach, away from the blocker, by 2× the capsule radius, up to 8 steps. If still blocked, exit at 2× the original distance. Re-run the overrun rule, then reserve the capsule.
5. **Arrival booking:** book the arriving bodies (ship compound plus docked ships) in the per-tick arrival budget at TDrop, at most 64 bodies per tick. If the budget is full, `TDrop += 1` and `T1 += 1` before announcing. Arrivals are never deferred after they are announced.
6. **Announce `WarpArrival`,** and re-send `WarpBeacon` with the exact corridor if the beacon is on.

**Prepared at T0, not at commit:**
- **Scene pool:** keep free scenes ≥ pending arrivals + 2. When short, the pool grows on a worker (~1 ms per system, off the main thread).
- **Surface targets:** collision rings and terrain prefetch around the exit start on the server and on crew clients.

**At TDrop** (tick step 3):
1. **Drop-sphere test** (~1 µs): the ship's own starting volume at Q only (ship radius + 20 m), not the whole corridor.
   - If something is sitting exactly there (for example a ship parked on the drop point on purpose), slide the whole corridor sideways by the minimum clear step. The first snapshot corrects clients, and the flash hides the pop. The body is never created overlapping.
   - Anything that has moved into the rest of the corridor since commit is **not** avoided. It is in the projectile's path (see "Real-space segment", liability).
2. **Attach bubble** (server only):
   - **No physics anchor** (e.g. an AI ship with no crew): rails.
     - That is a Kepler orbit in the inertial frame, run through the segment as a thrusting rails integrator with the brake as its thrust.
     - A hover exit uses the thrusting rails integrator with a station-keeping controller. It is never `Frozen` in mid-air.
     - A rails arrival can't collide while it stays on rails. If its corridor crosses an anchored bubble's soft radius, normal membership pulls it into that bubble, and from then on it can.
   - Otherwise, the bubble in the attach frame whose hard radius contains the corridor capsule (the nearest origin to the capsule midpoint if several).
   - Otherwise, any bubble whose predicted closest approach to the drop state within H (with the brake in `MaxAccel`) is below the merge gap.
   - Otherwise, a new bubble: a Surface anchor (origin = corridor midpoint snapped to 32 m) or FreeFall (`OriginRails` from the exit state at T1, so the origin ends up where the ship stops).
   - Arrivals on the same tick are processed in `WarpId` order, so two ships arriving together share the bubble the first one created.
3. **Create the body** with the cached compound shape at Q, with the corridor rotation, velocity V and angular velocity 0, and `MotionQuality.LinearCast`. The grid re-parents to the attach frame, and its pose comes from the body after the step. Start the brake.
4. **If the drop is atmospheric,** emit `WarpBlast`.

**At T1:** the brake ends, and `GridInertial` clears at T1 + 1. If the ship is below 100 m/s it returns to discrete motion quality. The pilot has control again.

**Abort and late changes:**
- **Any cancel at tick `t < TCommit`** (abort signal, engineer shutdown, power or support loss, damage, runaway; see the cancel table):
  - It becomes a drop at `TDrop' = t + N` at `p(TDrop')` on the original route and profile, then the 2 s real-space segment to `T1' = TDrop' + NR`. The commit runs immediately.
  - The attach frame follows the band rule and the velocity the drop rule. `d_r` is the route tangent there, then the overrun rule applies.
  - Soft cancels brake. Hard cancels drop at V with no brake.
  - At the speed cap the ship still covers up to 3e6 km in those 3 s.
- **Cancels and interdictions at `t ≥ TCommit` are ignored:** the ship is already arriving, because the cancel's drop would come no earlier than the scheduled one. After TDrop only the brake can be lost.
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
- **Warp arrivals rarely trigger merges,** because the attach rule joins at birth: at TDrop, against the whole corridor capsule. When one does, the brake in its `MaxAccel` makes the pair hot at once. The expected merge sources are:
  - exits pushed out by an exclusion zone;
  - interdiction drops;
  - free-point exits near other players.
- **Cost:** about 15 ns per pair. A server with 128 bubbles averages about 15 µs per tick.

### Interdiction and aborts (optional hook)

- **Field:** `InterdictionField` (centre in a frame or on an owner entity, radius, start and end ticks). Gravity wells are not interdictors.
- **Detection:**
  - On warp start and on field activation, find the first tick each path enters each field, using the same 32-segment capsule test (~2 µs per pair).
  - Fields centred on a **simulated** owner are re-tested at 1 Hz, because the owner moves under thrust.
- **Interdicted drop** (the only exit forced on a ship; D16's "no forced exit" is about an altitude floor):
  - `t_drop = max(t_enter, now + N)`, committed at `t_drop − N`, so clients always get 3 s of warning before the drop. The 2 s real-space segment follows, with the brake. Entries detected after `TCommit` are ignored.
  - The drop is at `p(t_drop)`, then pushed by exclusion zones, checked by the overrun rule and slid if blocked. A beaconed ship re-sends its beacon with the new corridor at once.
  - If the forced drop is inside an atmosphere, the interdictor is liable for the blast.
  - **Velocity:**
    - Owned field: the interdictor's velocity plus 100 m/s along the path.
    - Static field: the drop rule plus 100 m/s along the path.
  - Attach: by the T1 rule, with predictive merge as the interdictor closes.
- **Protected volumes:** stations can forbid field activation inside their exclusion zone (permission hook), so arrival rings can't be turned into traps. The ban holds during sieges (D27).
- **Cross-shard (interplanetary) warps** are not interdictable in v1.
- **Player abort** uses the same code path.

### Fleet warp

- **Group:** up to 16 root ships within 2 km of the leader, in the leader's bubble. Players accept; AI accepts automatically.
- **Shared timing and profile:** spool syncs to the slowest member, and T0 and T1 are shared. The profile is the most conservative one: min `V0`, min `K` and min `VCap` over the leader's D. Every member then has the identical `s(t)`.
- **Per member:** its own departure point `a_i`, its own drives (fleets don't pool support), and `b_i(t) = b(t) + R_exit · offset_i`. Offsets are the formation at engage, relative to the leader, rotated into the arrival attitude and clamped to 1 km. Members share the leader's waypoints, so their routes stay parallel.
- **Shared decisions:**
  - Parallel corridors with one `d_r`. One union-capsule reservation, one exclusion, overrun and permission decision (against the leader's permissions), one arrival booking, and one beacon.
  - The fleet beacon goes out if any member's beacon is `On`. It covers the union corridor and lists only the beaconing members. Silent members ride inside an announced corridor, so joining a beaconed fleet costs silence in practice. A fleet that is silent throughout needs every member silent.
  - Members arrive in order on one tick, so they share one bubble.
  - **One** `WarpPlan` and **one** `WarpArrival` carry the member list.

### AI pilots (D53, minimal hook)

The pilot matters to an AI ship, in warp as elsewhere. The server builds the route for everyone (D15), so no pilot plots a better curve. What a pilot decides is what a player decides in the plot panel, and AI pilots differ only there. **Skill limits and style chooses; validation, the route builder, the commit and the beacon are the same for all.**

- **`WarpPlotStyle`** (8 B per pilot, filled from ai-ships-and-crew.md's skill tier and style data; that doc owns the names and values):
  - `PlotTicks`: time at the plotting console before the spool request. Proposed 600 / 300 / 120 / 60 ticks for Green / Trained / Veteran / Ace. This is the skill cost that shows: a Green pilot needs 10 s to plot an escape, an Ace 1 s.
  - `AbortDelayTicks`: the tier's reaction delay before the pilot raises `Cancel` on an event (a breach alarm, an interdiction warning, a beacon in its corridor).
  - `AltitudeQ8`: exit altitude for surface targets as a multiple of `QuietAltitude`. Cautious styles use 1.5, the default is 1.0. Below 1.0 only for behaviours with `lawful: false`, and never below `ExitClearance`.
  - `Flags`: `AcceptGroundOverrun` (takes a plot whose overrun path meets the ground, the orange preview), `AcceptAtmosphericDrop`, `HoldForHullRiders` (waits for crew on the hull to come in before spooling, D66).
- **Never a warp attack.** Whatever its style, no AI pilot commits a drop whose `RingCovered` flag would be set (factions-and-law.md: AI never makes a warp attack). It raises the exit until the ring is clear.
- **Cost:** read at plot and on the events above. Nothing per tick. A scheduled (L0) leg folds `PlotTicks` into its spool leg.
- **Players** have no plot skill. AI crew flying a player's hull use their own tier (the AI doc's `fromCrew`).

### Networking

| Event (reliable, tick-stamped) | When | To | Size |
| --- | --- | --- | --- |
| `WarpPlotRequest` | player plots | server | ~40 B: target ref, exit local int64, flags |
| `WarpPlotResult` | reply | requester | ~40 B: ok or a client-knowable reason, D, duration preview |
| `WarpPlan` | spool start | crew of the whole carried subtree | ~150 B: id, T0, TDrop, T1, Lca, DepFrame, TgtFrame/TgtEntity + BLocal, effective profile (V0, K, VCap, VReal, TReal), D (int64 1/1024 m), rotation 48 bit, group, up to 4 waypoints (FrameId + int64 ×3) |
| `WarpSpool` | spool start | interest of the ship | ~16 B (others see the spool-up effect) |
| `WarpCancel` | before T0 − 30 | crew, interest of the ship | ~8 B |
| `WarpStart` | T0 | crew | ~32 B: id, `ALocal` int64 |
| `WarpDepart` | T0 | non-crew observers that hold the departing ship as a contact (Q1 or better) or have a gravimeter in range of its spool (combat-and-sensors.md "Beacons and transponders"; was: interest of the departure point) | ~24 B: entity, T0, rotation, profile id. Direction and ramp only: no D, no target |
| `WarpBeacon` | `TBeacon` (TDrop − 30 s, or T0); re-sent at commit | everyone within `BeaconRange` of E; the target grid's owner and traffic control; the planetary authority for atmospheric drops. Never sent for a silent warp | ~80 B: id, transponder, TDrop, T1, attach frame, Q and E int64, `d_r`, U, uncertainty, mass and size class, blast footprint radii (0.35 and 0.5 bar) |
| `WarpArrival` | TCommit (TDrop − 3 s) | crew; interest of the corridor; for a silent warp also, as a bearing-only Q1 contact, any gravimeter within arrival range (combat-and-sensors.md; no more warning than the 3 s here) | ~90 B: id, TDrop, T1, attach frame, E and Q int64, `d_r` and U float32 ×3, rotation, blueprint hash, profile id, members |
| `WarpAbort` / `WarpInterdicted` | on event | crew | ~20 B with the cause (abort, power, support, damage, runaway, interdiction), followed by `WarpArrival` |
| `WarpBrakeLost` | on event, TDrop … T1 | interest of the corridor | ~16 B: id, tick (clients stop drawing the brake glow; snapshots carry the motion) |
| `WarpBlast` | TDrop (or T0) when atmospheric | interest within the blast radius at 0.07 bar | ~48 B: centre, corridor, W, tick (the law fields `Ring035M` and flags stay on the server) |
| `WarpShellLoss` | on event, cruise only | crew | ~12 B: warp id, entity, tick (a body crossed the bubble wall, D66) |

- **The plan goes to the crew at spool start.** Crew clients therefore apply T0 on the right tick, rather than half an RTT after their grid has already jumped. The 0.5 s cancel lock-in makes this safe.
- **What third parties learn:**
  - everyone near the departure: the departure direction at T0;
  - everyone within 200 km of the exit: the corridor 30 s before the drop, if the beacon is on (D18);
  - everyone with interest in the corridor: the corridor at commit, 3 s before the drop.
  
  The departure side never learns the destination. A silent warp's destination stays crew-only until commit.
- **No per-tick traffic during cruise.**
  - Crew evaluate `p(t)` for their grid pose and keep full-rate interior snapshots in grid coordinates.
  - The ship's exterior snapshots stop.
  - There is no mid-route interest. Late joiners among the crew get the plan replayed from `WarpTable`.
  - From TDrop the ship is an ordinary simulated body, and exterior snapshots resume.
- **`WarpArrival` is the existing frame-change event** ("E → frame F at TDrop with state S"), carrying the warp id. Bubbles stay off the wire.
- **Arrival on other clients:**
  - At `WarpArrival` they fetch the blueprint by hash (brush lists of a few KB fit easily in 3 s, and a beacon gives 30 s). They start the flash at Q, growing over N ticks.
  - The approach streak is drawn from the profile's ramp along the corridor.
  - At TDrop they add the ship to their prediction scene as a kinematic proxy that follows the nominal brake curve, and normal snapshots take over. A collision or a lost brake shows up in the next snapshot.
  - If `WarpArrival` is lost past TDrop, they fall back to snapshots.
- **Crew and pilot clients:**
  - At T0 they know the destination. They prefetch terrain for surface targets, and the speed LOD bias suppresses everything mid-route.
  - At commit, the crew's interest centre moves to the corridor, so destination snapshots arrive before TDrop.
  - The pilot's prediction scene (C12) is dropped at T0 and rebuilt at TCommit around the corridor.

**Dedicated sharding (after M8): warp is the hand-off point.**
- **Shared clock:** all shards share one global `TickIndex`; rails depend on it.
- **Ownership:**
  - Shards cut by star system first, then by SOI subtree within a system (frames-and-zones.md). A warp never leaves its system, so its hand-offs stay within one.
  - A warp whose target frame belongs to another shard (another planet's SOI subtree) is handed over during the warp.
  - Warps skip the interplanetary shard. The sublight SOI-crossing hand-off from frames-and-zones.md stays.
- **At T0:**
  - The departure shard sends the carried subtree (grid contents, characters including hull riders, inventories, docked ships, room air state, `WarpTable` row) to the destination shard as a blob.
  - Crew clients get `ShardHandoff {address, token}` and connect in parallel.
- **Authority switch at `T_h`:**
  - `T_h` = the tick when the destination has acked the blob and every crew client has connected (or a 10 s timeout, after which disconnected crew travel as offline characters), plus 30 ticks. It is announced.
  - Until `T_h` the old shard keeps stepping the grid bubble. At `T_h` it sends the final grid-local delta and stops.
- **If `T_h` isn't reached by TCommit − 60:** the old shard extends TDrop and T1 in whole seconds, announced in a re-sent `WarpPlan` (and a re-sent `WarpBeacon` if one went out). The drop and the real-space segment run on the destination shard. The arrival is only ever performed by the shard that owns the target frame.
- **Short warps inside one shard** don't hand off.

### Data structures (zero alloc)

```csharp
public enum PlacementMode : byte { Simulated, Rails, Frozen, Warp, Throat }   // (frames-and-zones) Placement also gains: public WarpId Warp;
public enum WarpPhase : byte { Spool, Cruise, Committed, RealSpace }
public enum WarpTargetKind : byte { BodyOrbit, Surface, Grid, Marker, FreePoint }
public enum ArrivalVelocity : byte { MatchTarget, CircularOrbit, FrameRest }
public enum BeaconState : byte { On, MilitaryOff, Tampered }
public enum WarpCancelCause : byte { Abort, Interdiction, Power, Support, Damage, Runaway }
public readonly record struct WarpId(ushort Value);

/// Drive class, data-driven (data/drives/*.json).
public struct WarpDrive
{
    public double V0, K, VCap;            // m/s, 1/s, m/s
    public double VReal, TReal;           // real-space segment: m/s, s (2 km/s, 2 s)
    public int SpoolTicks, CooldownTicks;
    public double MassCap, FuelPerLn;     // kg; fuel per kg per ln(1 + D/1e6 m)
    public double PRated;                 // W; f = clamp(P / PRated, 0, 1), joins at f ≥ 0.5; weight w = min(P, PRated).
                                          // Per class in data/drives, D:C:B:A = 1:4:12:40 × PRatedBase (knob, proposed 2 MW)
    public double BeaconLead;             // s (30)
    public bool Military;                 // has a beacon switch
}

/// Supporting drives of one warp, fixed at spool start (D14). Preallocated, ≤ 8 drives.
public struct WarpDriveSet
{
    public int Count; public fixed int Part[8]; public fixed double Weight[8], Fraction[8];
    public WarpDrive Effective;           // Σw·X/Σw; VCap also × f; SpoolTicks max
    public double SupportKg;              // Σ f_i · MassCap_i, recomputed when a drive drops out
}

/// Per root grid (on the grid record, not in WarpTable): anti-chaining ship cooldown (D14).
public struct ShipWarpCooldown { public long ShipWarpCooldownEnd; }   // = max cooldown end of the last warp's participants

/// Route knots besides the endpoints (D15). ≤ 4 waypoints, each fixed in its body's inertial frame.
public struct WarpRoute
{
    public int WaypointCount; public fixed int WaypointFrame[4]; public double3 W0, W1, W2, W3;
    public fixed float ArcTable[64];      // s → u, built at spool start
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
    public long[] T0, TBeacon, TCommit, TDrop, T1;
    public WarpProfile[] Profile; public doubleQuat[] Rotation;
    public WarpRoute[] Route; public WarpDriveSet[] Drives;
    public WarpTargetKind[] Kind; public ArrivalVelocity[] VelRule;
    public double3[] ExitPos, ExitVel;      // E (quantised 1/1024 m), U; in AttachFrame, set at commit
    public double3[] DropPos, DropVel;      // Q, V; in AttachFrame, set at commit
    public float3[] CorridorDir;            // d_r
    public BeaconState[] Beacon; public bool[] BrakeOn;
    public float3[] ShellMin, ShellMax;     // WarpShell (D66): carried subtree bounds + WarpShellMargin, root-grid coordinates, fixed at T0
    public double3[] BlendDelta;            // δ in Lca, local to each peer
    public ushort[] FreeIds; public int FreeCount, ActiveCount; public ushort[] Active;
    public void Evaluate(long tick, FrameTree frames);                        // writes grid poses for all active warps
    public double3 PositionAt(int warp, double renderTick, FrameTree frames); // render, fractional tick, double
}

public struct ExitReservation { public FrameId Frame; public double3 Pos, PosEnd; public float Radius; public long From, To; } // corridor capsule Q → E; fixed pool of 512, freed by expiry; full → plot rejected ("traffic")
public struct ArrivalBooking { public long Tick; public ushort Bodies; }  // ring of 256 ticks (booked at TDrop, ≤ 180 ticks ahead)
public struct WarpBlast { public FrameId Frame; public double3 Centre, CorridorEnd; public double YieldJ; public long Tick; public int Culprit;
                          public float Ring035M; public byte Flags; }     // one-shot event. Flags: BelowQuiet, RingCovered (D58; set at commit, read by the law doc)
public struct WarpExclusion { public int Grid; public float Radius; }     // per grid; permission via hook (ZonePermission.Check, WarpArrive)
public struct WarpRegionExclusion { public int Body; public float3 Direction; public float Radius; public int Shield; } // raised region shield (D58): dome about a surface direction, or the whole body (Radius = shell). Listed only while raised
public struct WarpPlotStyle { public ushort PlotTicks, AbortDelayTicks, AltitudeQ8; public byte Flags; }               // AI pilots (D53); values from the AI doc's skill and style data
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
| Warp route evaluate (one Exp, arc-table lookup, up to 6 cached knot poses, one Hermite piece, grid pose write) | ~250 ns per warp; 64 warps ≈ 16 µs |
| Grid bubble during warp | same as at rest; 0 if asleep |
| Bubble pocket and wall (D66) | 0 per tick: the shell test replaces the grid leave rule in the existing 10 Hz membership check (~5 ns per body outside the interior volume, usually none) |
| Air in warp (D66) | two ambient-change events per warp; venting costs what it costs at rest (ship-atmosphere.md); 0 for a tight ship |
| Region shields (D58) | ~15 ns per raised shield in the exclusion push at commit; ~0.2 µs per shield per blast; ring query at commit only for drops below `QuietAltitude` |
| AI plot style (D53) | read at plot and on events; 0 per tick |
| Plot / route build (job, rate-limited to 2/s per player) | ~20 µs per pass after pruning (about 5 bodies × 32 Kepler solves), up to 6 passes ≈ 120 µs; 0 on the main thread |
| Commit (once per warp): capsule tests, overrun path (32 capsules), reservations, booking | ~20–50 µs |
| TDrop drop-sphere test + attach | ~2 µs, plus the transfer at ~5 µs per body |
| Real-space segment (2 s) | one brake force per carried body per tick (~50 ns), plus a `LinearCast` body in the bubble |
| Beacon, blast | one-shot events; blast ~1 µs plus one receiver radius query |
| Mid-warp cancel / support checks | event-driven (power, damage and wire events); 0 per tick |
| Scene pool growth | ~1 ms per system, on a worker, started at T0 |
| Interdiction | event-driven, ~2 µs per (warp, field) pair; 1 Hz for fields with moving owners |
| Merge prediction (server) | ~15 ns per pair; 128 bubbles ≈ 15 µs per tick on average |

### Changes to frames-and-zones.md

Merged into frames-and-zones.md (r6), which owns these rules; kept here for reference.

- **`Placement`:** `PlacementMode.Warp` plus a `WarpId` field. **`FrameTree`:** `TryPoseAt`.
- **Gravity and rails:** remove "quantum travel" from the thrusting Verlet bullet. Warp is closed form in `WarpTable`, and no gravity applies during warp.
- **Grid bubble:**
  - Add the `GridInertial` flag (warp from T0 to T1 + 1, and unanchored rails ships): plating or zero-g, and no fictitious forces. In the real-space segment it removes the brake field but keeps contact impulses.
  - Reset the proper-acceleration history at T0, TDrop and T1 + 1.
  - During warp, exterior door proxies are dropped. **Round 6 (D66): exterior portals no longer collide as closed.** While a `WarpShell` exists, the grid's leave rule is replaced by the shell test, and a body that crosses the shell in cruise is lost (frames-and-zones.md says the same since the r7 consistency pass).
- **Membership:** skip `Warp` placements. The anchor rule is unchanged.
- **Enter/leave table:**
  - Add **Warp engage**: enter at T0 if validation passed; the carried subtree goes along.
  - Add **Warp drop**: at TDrop, attach to the frame announced at commit and to the bubble chosen at TDrop (corridor capsule rule). The ship is simulated and braked until T1.
  - **Replace** the merge/split row:
    - merge when the predicted gap within 2 s is < 500 m (hot pairs every tick, others at 10 Hz);
    - split when the group gap > 1500 m, the predicted minimum gap > 1500 m, and at least 2 s have passed since the last merge.
  - The 3 km shed rule is unchanged.
- **Transfers:** warp arrivals are pre-booked in an arrival budget (≤ 64 bodies per tick) and never deferred. A merge may exceed the 32-transfers budget only for islands on the predicted contact (≤ 128).
- **Pool:** keep a low-water mark of free scenes and grow them on a worker.
- **Terrain:** edits are clamped to `TerrainMax`. This keeps `RouteShell` constant. It is no longer an exit floor.
- **Tick order:**
  - Insert **2b. Warp routes:** evaluate, send due beacons, run the commits due (in `(TDrop, WarpId)` order), and queue the drops due at TDrop.
  - Step 3 transfers now include drops, with the drop-sphere test and any `WarpBlast`.
  - The brake force is applied before the physics step (with the other per-body forces), and stops at T1.
  - Step 9 adds merge prediction (hot pairs every tick).
- **Networking:**
  - Add the warp events. Interest row: "In cruise: crew only. Third parties get the departure direction at T0, the beacon 30 s before the drop within 200 km of the exit (unless silent), and the corridor at TDrop − 3 s. From TDrop: normal snapshots."
  - Sharding: one global `TickIndex`. Warp is the cross-shard hand-off; the SOI-boundary hand-off stays for sublight.
- **Critique Q1 (getting to the moon)** is answered by the warp drive. Hohmann transfers remain possible but are not required.
- **Milestones:**
  - **M7:** warp with **seated** crew to moon orbit, to a surface point, and to the other player's ship (with consent; it joins their bubble), with the smooth route, the real-space segment, the beacon and predicted merge. **Exit checks:**
    - at T1 the orbit elements match the requested circle;
    - an undisturbed real-space segment ends within 1 cm and 1 mm/s of (E, U);
    - a surface-to-surface hop of 1,500 km on a 1000 km body routes clear of the shell with no via points;
    - commit plus TDrop cost under 0.1 ms;
    - a 3 km/s interception merges more than 1 s before contact (logged);
    - an arrival next to an orbiting friend drifts under 0.05 m/s relative after 60 s.
  - **M8:** crew moving freely during warp (grid bubble, `GridInertial`, the `WarpShell` pocket and wall, vacuum ambient with venting in cruise: D66). Wired drive ports (power and data, D71) and the cancel table, multiple drives, atmospheric drops with `WarpBlast`, region shields as exclusions (with factions-and-law.md's F4).
  - **After M8:** fleet warp, interdiction, sharded hand-off.

### Files

- **`src/Frames/`** (no Jolt):
  - `WarpProfile.cs`, `WarpTable.cs`;
  - `WarpPlanner.cs` (LCA, D fixed point at spool start, band and drop rules);
  - `WarpRoute.cs` (Hermite route, arc table, pruned shell test, automatic waypoints);
  - `WarpDriveSet.cs` (supporting drives, power weighting, support check);
  - `WarpLimits.cs` (`RouteShell`, `ExitClearance`, `QuietAltitude`); `FrameTree.TryPoseAt`.
- **`src/Physics/`:**
  - `WarpArrivals.cs` (commit, exclusion push, overrun rule, corridor occupancy, reservations, arrival booking, TDrop attach rule);
  - `WarpBrake.cs` (real-space segment servo, brake loss);
  - `WarpBlast.cs` (yield, overpressure, receivers, `IncidentRecord`s);
  - `ApproachPredictor.cs` (inside `BubbleManager`);
  - `Interdiction.cs`.
- **`src/Ships/WarpDriveWiring.cs`:** drive ports (power in; data `Engage`, `Plan`, `Cancel`, `Hold`, `BeaconSwitch`; data out `Beacon`), cancel sources, beacon state, latches and door locks.
- **`src/Physics/WarpShell.cs`:** the pocket's bounds, the shell test in the membership check, wall losses, ambient-change events (D66).
- **`src/Net/WarpEvents.cs`:** plot, plan, spool, cancel, start, depart, beacon, arrival, abort, brake lost, blast, and shard hand-off.
- **Client:**
  - `src/Client/Warp/WarpView.cs` (streak, drop flash, brake glow, δ blend, destination prefetch, beacon markers, blast shock);
  - `WarpPlotPanel.cs` (ImGui map, route curve, corridor and overrun path, blast footprint, exit preview colours).
- **Data:** `data/hazards/blast.json` (thresholds).
- **Other:**
  - `data/drives/*.json`;
  - `ProfilerPanel` rows: Warp eval, Route jobs, Commits, Drops, Brake, Blasts, Merge predict;
  - this section goes into `docs/frames-and-zones.md`.
- **Tests:**
  - `WarpProfile`: `s(TDrop) = 1` exactly; monotonic; `T(D)` matches integration; `p` is exact at both ends.
  - `WarpRoute`: exact at every knot; C¹ at the waypoints; no 32-segment capsule inside any shell for random planet-to-moon, surface-to-surface (including near-antipodal) and moon-occluded cases; ≤ 4 waypoints.
  - Route: continuous through commit for a target thrusting at 3 g (no jump larger than one tick of motion).
  - Real-space segment: an undisturbed brake ends on (E, U) within 1 cm and 1 mm/s; a brake lost at TDrop keeps V, and the 10 s ballistic overrun path (gravity included) misses every grid's hull sphere + 300 m and every non-permitting exclusion sphere up to any ground impact; terrain never raises an exit.
  - Commits: two exits plotted to the same ring point on the same tick never get overlapping corridors.
  - Arrival in the rotating band: attaches to `BodySurface` with zero follow-up transfers.
  - `ApproachPredictor`: 3 km/s head-on and crossing cases merge ≥ 1 s before contact.
  - Attach: a corridor inside a bubble's hard radius joins that bubble, never a new one.
  - Blast: yield is monotonic in ρ and m; the 2,000 t sea-level table reproduces; vacuum drops give W = 0.
  - Beacon: never sent for `MilitaryOff` or `Tampered`; sent at `TDrop − 30 s` only to receivers within 200 km, the target's owner and traffic control, and (atmospheric drops) the planetary authority; an unpowered or cut antenna gives `Tampered` and the `IncidentRecord` (`BeaconTamper`) names who did it; a mid-warp wire cut sets `Tampered` and the liability flag.
  - Multiple drives: effective parameters match the weighted formulas; at most 8 join (largest w); a drive with f < 0.5, in cooldown, or failing its fuel plan doesn't join; chain A then B is refused until A's cooldown ends (`ShipWarpCooldownEnd`); losing a drive below the support threshold hard-cancels; losing one above it changes nothing.
  - Privacy: no `WarpPlotResult` reason ever depends on hull occupancy.
  - Bubble (D66): a room with a 0.1 m² hole loses air through a 60 s cruise at ship-atmosphere.md's rate for vacuum, and a tight ship reports 0 active rooms; a character mag-booted on the hull at T0 arrives with the ship; one floating 5 m off the hull stays behind; a body pushed past the shell in cruise is lost with one `WarpShellLoss`; one that crosses during a braked real-space segment appears in the exterior bubble at the ship's velocity; a queued jettison releases on the first real-space tick.
  - Region shields (D58): an exit plotted inside a raised dome is pushed to the shell + ship radius + 300 m; the same plot with the shield drained is not moved; a 250 m drop beside a settlement sets `BelowQuiet` and `RingCovered`, the same drop over empty ground sets only `BelowQuiet`.
  - Wiring (D71): cutting the `Hold` cable in cruise is a soft cancel; a drive with a dead `Hold` refuses to spool.
  - AI pilots (D53): a Green pilot's spool request comes `PlotTicks` after its decision; no AI plot commits with `RingCovered`.

### Open questions for the user

Decided in round 5 (no longer open): line of sight → smooth automatic route (D15); mid-warp → free crew and wiring-driven cancels (D17); exit floor → none, with a blast (D16); system scale → realistic spacing, 3.7 min/AU; inbound warning → the beacon (D18).

Decided in round 6 (no longer open; numbers 4 and 6 are retired, the others keep theirs):
- **4, blast fines:** factions-and-law.md owns the offence table (Nuisance blast, Reckless damage, `WarpAttack`). The overpressure thresholds stay data here.
- **6, warp blast as a weapon (D58):** definitely illegal (`WarpAttack`, an atrocity), and planetary or city shielding stops it where someone paid for a shield. No no-drop law: a raised shield is a physical exclusion, open ground has only the law.
- **Air in warp (ship-atmosphere.md's question 2, D66):** warp space is real space. Nothing is sealed; breaches vent in cruise.
- **Wiring media (D71):** power and data only. The drive's `Hold` is a data line.
- **AI pilots (D53):** the pilot matters; `WarpPlotStyle` is the hook.

1. **PvP:**
   - Are the default exclusion radii right, with non-consenting arrivals kept 5 km from stations and 2.5 km from ships?
   - Should damage cancel a spool?
   - Is interdiction in scope?
2. **Real-space segment numbers:** 2 km/s over 2 s (2 km, ~1 kt per 2,000 t of ship in air). A longer or faster segment is more dangerous and harder to fit in one bubble. OK?
3. **Beacon reach:** 30 s ahead, 200 km radius, plus the target's traffic control at any range. Is that the right safety/privacy balance?
5. **Submerged exits** are refused in v1. Do you want warp exits into the sea (a huge splash and surge) later?
7. **The bubble wall (D66 follow-up).** Proposed: the bubble carries 20 m of ordinary vacuum around the hull, and anything that crosses its wall in cruise is lost (a character dies, an item is destroyed). The alternative is that it drops out alive at that point of the route, stranded in deep space. That costs a mid-route entity, a lone bubble and a shard hand-off per loss, and it lets a launched craft leave a warp with no commit, beacon or exclusion push. Is "lost" acceptable?
8. **Hull riders and boarding in warp (D66 follow-up).** Proposed: anyone gripping the hull at T0 goes along, crew or not, so a boarder can limpet on before the jump and cut in during cruise, and crew can do outside repairs mid-warp. Nobody can arrive from outside in cruise. The alternative keeps round 5's rule (non-crew on the hull are left behind), which makes a warp a clean escape from boarders. Which do you want?

### Review notes

- **Bubble choice moved from commit to T1** (round 5: to TDrop, the first real-space tick). Bubbles are server-only and can merge, split or be recycled in those 3 s. Only the frame, E, U and T1 are fixed at commit.
- **Live b(t) replaced by the announced exit track from commit, plus a local δ blend.** Before, a target thrusting at 3 g dragged the arrival about 130 m off the announced, reserved and tested spot. A slide at commit also made the path jump by kilometres.
- **Attach frame follows the rotating band (C3) and SOI rules.** The old table put orbit exits below `RotatingTop` in `BodyInertial`, which forced an immediate extra transfer and a bubble in the wrong frame.
- **Abort and interdiction velocity: "frame rest" became the drop rule (circular orbit).** Zero inertial velocity meant a vertical fall.
- **Ring placement for orbiting targets moved into the along-track disc, with velocity at rest in the target's orbital frame.** A radial offset drifts about 2 m/s in low orbit.
- **Privacy** (superseded in round 5 by the arrival beacon, D18; kept for history):
  - `WarpStart` went to everyone near the departure with the target, and `WarpInbound` sent the exit and blueprint to the destination 43 s early. Both were camping intel.
  - Now the destination goes to the crew only, and third parties get the exit 3 s before arrival.
  - Mid-path interest was dropped: the ship crosses any observer's range in less than a tick.
- **Plot rejections no longer reveal hull occupancy.** At 2 plots/s that was a system-wide radar for hidden ships. Occupancy is resolved silently at commit and again at T1 (round 5: at TDrop, drop sphere only).
- **Exclusion zones apply to every grid, with a silent push-out.** The old permission check covered only the named target, so a free point next to a hostile station bypassed it.
- **Anti-camping:** the server seeds dispersion on the ring and stations can define arrival lanes. Stations can forbid interdiction fields inside their exclusion zone.
- **Anti-griefing:**
  - Non-crew on the hull and incidental contact no longer block spool.
  - Damage doesn't cancel spool by default.
  - Cancels lock 0.5 s before T0, which also makes the crew's early copy of the plan safe.
- **New final occupancy test at T1** (round 5: the drop-sphere test at TDrop). A ship parked on the exit during the 3 s window would otherwise have the arrival created inside it.
- **Same-tick commits and arrivals are processed in a deterministic order.** Overlapping exits now slide apart, and simultaneous arrivals share one bubble.
- **Bubble creation latency:** the scene pool grows on a worker from T0, and surface collision and terrain prefetch also start at T0.
- **Arrivals are pre-booked in a per-tick arrival budget.** Before, the 32-per-tick deferral could delay an announced arrival.
- **The merge budget override is now capped** (≤ 128, with proxies for the rest) instead of unbounded.
- **Shedding uses zones' 3 km rule.** The old 1500 m contradicted the 2/3 km hysteresis.
- **`GridInertial`:**
  - It now spans T0 to T1 + 1 and resets the proper-acceleration history. Otherwise the velocity snaps would reach unplated interiors as a one-tick 1e5 g.
  - Exterior portals are sealed during warp. (Superseded in round 6 by D66: nothing is sealed.)
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

Revision and review history: [history/warp.md](history/warp.md).
