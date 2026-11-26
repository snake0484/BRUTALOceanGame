## Coordinate frames and physics zones

### Recommendation in one paragraph

Split the user's "solar > planetary > ship grid" idea into two layers. **Frames** are a cheap double-precision forest with no limit on count: one Solar root per star system (systems are linked only by wormholes), then each celestial body's inertial frame, its rotating surface frame, Lagrange-point frames computed from two railed bodies, and ship/station grids (grids can nest). **Physics scenes** are a flat, pooled, limited set of Jolt `PhysicsSystem`s. Each one simulates a float "bubble" around a cluster of interacting things inside one frame. Gameplay, networking and rendering speak frames. Only the physics layer knows about bubbles. Gravity is patched conics: only the dominant body pulls (except inside a Lagrange zone, where the pair both pull), and celestial bodies ride Kepler rails evaluated from the integer tick, which stops while the server is empty. Everything stores `double3` relative to its parent frame. int64 fixed point is used only on the wire.

### What exists today (and what blocks multi-world)

- `src/Physics/PhysicsWorld.cs` holds one Jolt system for the whole game, with gravity fixed at `(0,-9.81,0)` and 65,536 max bodies. **Each instance creates its own `JobSystemThreadPool` (`WorkerBudget.Tick` threads) and a 32 MB temp allocator.** Running N instances today would mean N × 6 threads and N × 32 MB. That has to be refactored before there is a second world.
- Positions are `System.Numerics.Vector3` everywhere: `FlyCamera.Position`, `TerrainWorld.Update(Vector3)`, `ChunkCoord.WorldOrigin` and `FromWorld`. The frame UBO's `cameraPos` is "xyz world".
- `terrain.vert` already decodes chunk-local 16-bit positions and adds a **per-chunk push-constant origin**. Camera-relative rendering is therefore a CPU change only: push `chunkOrigin − camera`, computed in double. The 12 B vertex format survives unchanged.
- `FixedClock` runs a 60 Hz tick from a `long TickIndex`. That is the right base for rails.
- Native Jolt (Ahwoo.BRUTAL.Jolt 2026.10.2), probed in the exported DLL:
  - **Present:** `CharacterVirtual_ExtendedUpdate`, `JobSystemSingleThreaded_*`, `PhysicsSystem_GetNumActiveBodies`, `BodyInterface_SetPositionRotationAndVelocity`, `SetGravityFactor`, `MoveKinematic`, `MutableCompoundShapeSettings`, `ConvexHullShapeSettings`, `SetBodyActivationListener`, `SetContactListener`.
  - **Missing:** `PhysicsSystem_SaveState` / `RestoreState`, plus the `NarrowPhaseQuery_CastRay` and indexed `MeshShape` already known to be missing. That rules out full-world Jolt rollback for client prediction (see Networking).
- `Brutal.Numerics` already has `double3`, `doubleQuat` and `double4x4`. No new package is needed.

### Frames

```csharp
public readonly record struct FrameId(ushort Value);          // 0..SystemCount-1 = the Solar roots, one per star system
public readonly record struct SystemId(ushort Value);         // == the FrameId of that system's Solar root
// Shared enums: this doc owns them; other docs quote them as "(frames-and-zones)".
public enum FrameKind : byte { Solar, BodyInertial, BodySurface, Lagrange, Grid, Mouth, Tunnel, Lane, Field }   // Mouth, Tunnel: wormholes.md; Lane (virtual, ring + lane + tick), Field (belt field on rails): rings-and-asteroids.md
public enum PlacementMode : byte { Simulated, Rails, Frozen, Warp, Throat }                       // Warp: warp.md; Throat: wormholes.md
public enum CelestialKind : byte { Star, Planet, Moon, GasGiant, BlackHole, Remnant }              // GasGiant: planets.md; BlackHole, Remnant: exotic-matter.md
// Asteroids are not a CelestialKind: they are exotic-matter's MinorBody entries / rings-and-asteroids' lane and field frames.
// Proposed, not yet adopted here: FrameKind.Lane, FrameKind.Field and BubbleAnchor.Source (rings-and-asteroids.md), which supersede
// exotic-matter's BubbleAnchor.MinorBody. Adopting them is this doc's call; until then they are rings-and-asteroids' proposals.

/// Pose of a frame relative to its parent, refreshed once per tick (rails, spin) or after the physics step (grids).
public struct FramePose
{
    public double3 Position;        // origin in parent axes, m
    public doubleQuat Rotation;     // child axes -> parent axes
    public double3 Velocity;        // origin velocity in parent, m/s
    public double3 AngularVelocity; // of child axes, in parent, rad/s
}

/// SoA, preallocated (4096 frames; grids, mouths and live Lagrange points included), zero alloc in the tick;
/// grown only in the maintenance window, up to the ushort limit. Index = FrameId.Value.
public sealed class FrameTree
{
    public FrameId[] Parent; public FrameKind[] Kind; public byte[] Depth;
    public SystemId[] System;      // root of each frame's tree; Parent of a Solar root is itself
    public FramePose[] Local;
    public int[] OwnerEntity;      // grid -> ship entity; body frames -> celestial index; Lagrange -> LagrangePoint index
    public bool TryRelative(FrameId from, FrameId to, out FramePose pose); // common ancestor, compose in double; false across systems
}

/// A Lagrange point of a railed pair (primary P, secondary S on rails about P). One per (pair, index), shared by
/// every anchor there. Created lazily, only while an anchor holds (or is engaging) at that point; pool of 64, grown
/// in the maintenance window (a full pool never refuses an engage).
public struct LagrangePoint
{
    public FrameId Frame;            // parent = P's BodyInertial (Solar root when P is the star)
    public int Primary, Secondary;   // celestial indices
    public byte Index;               // 1..5
    public double Xi, Eta;           // dimensionless position in the pair's rotating basis, from the mass ratio, solved once
    public double ZoneRadius;        // bound of the zone: max over its anchored grids of |grid − origin| + r_grid + 2.6 km
}

public struct KeplerOrbit           // relative to parent BodyInertial (or Solar)
{
    public double SemiMajor, Eccentricity, Inclination, Ascending, ArgPeriapsis;
    public long EpochTick, PeriodTicks;   // phase = ((tick - EpochTick) % PeriodTicks) / PeriodTicks: exact forever
    public double ParentMu;
}

public struct CelestialBody
{
    public FrameId Inertial, Surface;
    public double Mu, Radius, SoiRadius, RotatingTop;   // RotatingTop = max(atmosphere top, 0.05 R)
    public long DayTicks; public double3 SpinAxis;       // spin angle from tick modulo DayTicks: exact
    public KeplerOrbit Orbit;
}

/// Where any entity is. Bubble/Body are invalid when the entity is on rails, frozen, in warp or in a throat.
public struct Placement
{
    public FrameId Frame; public double3 Position; public doubleQuat Rotation;
    public double3 Velocity; public float3 AngularVelocity;
    public BubbleId Bubble; public BodyHandle Body; public KeplerOrbit Rails; public PlacementMode Mode; // Simulated, Rails, Frozen, Warp, Throat
    public WarpId Warp;            // valid in Warp placement (warp.md's WarpTable entry)
}
```

- **Tree shape:** Solar → Planet inertial → {Planet surface (rotating), Moon inertial → Moon surface, Lagrange (lazy)} → grids. A grid's parent is whatever frame its ship is in: a surface, an inertial frame, a Lagrange frame, or another grid for a docked ship.
- **Several Solar roots (user decision).** Natural wormholes lead to other star systems, so the frame structure is a forest: one tree per system, all in one `FrameTree` (the 4096 preallocated frames are shared; a system costs about 2 frames per body, so 8 systems of ~40 bodies take ~640).
  - Systems have no pose relative to each other. `TryRelative` across systems returns false; nothing renders, senses or simulates across a system boundary except through a wormhole mouth's portal map (wormholes.md: clients near a mouth get the far side streamed and drawn through the disc, never through a tree edge). Each root carries a galactic position used only for the star backdrop and naming.
  - Wormhole tunnels (`FrameKind.Tunnel`, wormholes.md) are further roots beside the Solar roots; they belong to no system and `TryRelative` refuses them too.
  - The only way between systems is a wormhole transit (wormholes.md): a reliable frame-change event from a mouth frame in one tree to a mouth frame in the other, through the mouths' own transform. There is no cross-system warp (warp is for relatively close destinations; there is no common ancestor, so plots are refused). Manufactured wormholes stay within one system: only natural pairs link systems, and a cross-system ram or collapse ejection lands on one leg (exotic-matter, wormholes.md).
  - Celestial rails are evaluated only for systems that hold a bubble or a player's interest. Other systems cost nothing: rails are closed form in the tick, so a system is exact whenever it is next looked at.
- **Why double3 and not int64 fixed point:** positions are always stored relative to the parent frame. Inside a 1000 km planet's SOI (around 5e7 m) the double ulp is about 1e-8 m. Even heliocentric at 40 AU it is about 1 mm, and only ships in transit live there. Rotation, Kepler and transform maths all need double anyway. Rejected: int64 fixed point in the sim (uniform precision isn't needed with hierarchical frames, and it makes every rotation and orbit awkward). int64 is used **on the wire** at 1/1024 m.
- **Time:** one global `TickIndex` for every system. Orbit phase and spin angle come from `tick % PeriodTicks`, so they never lose precision, even after years of server uptime.
  - **Tick convention (implemented):** `SimWorld.Tick()` steps from `TickIndex·dt` to `(TickIndex+1)·dt` and writes rails, spin and the swell time for the step's end before physics steps, so when it returns physics, the swell and the frame poses all describe `TickIndex·dt` with `TickIndex` already incremented. The ocean renders at `SimWorld.RenderTime`, like the frames.
  - **Rails are world data (implemented):** `PeriodTicks`, the fitted semi-major axis, `DayTicks` and the SOI are decided once at generation and stored (`RailElements`, `CelestialRecord`, `CelestialSystem.Write`/`Read`, bit-exact round trip), never re-derived from doubles on a client, so every build agrees on the integer period. Positions evaluated from them are server-authoritative and never hash-compared.
  - **Game time stops while the server is empty (user decision).** `FixedClock` advances only while at least one player is connected (a listen server always has its host). When the last player leaves, the server finishes the tick, persists `TickIndex` with the world, and pauses; nothing catches up on resume.
  - Everything time-based in the sim is keyed on ticks, never on wall clock: rails, spin, weather, anchor reserves and warnings, sinking rails, wormhole and black-hole lifetimes, fines and cooldowns. So they all pause together, and an offline owner's anchored station loses nothing while nobody plays.

### Gravity and rails

- **Patched conics:** the dominant body is the deepest one whose SOI (Laplace, `a·(m/M)^0.4`) contains the entity. Gravity is `-μ r/|r|³` in double, about 10 ns.
  - Rejected: n-body (it breaks rails, determinism and cheap extrapolation).
- **Celestial bodies:** pure Kepler rails, about 100 ns each including the Newton solve. 64 bodies cost about 6 µs a tick.
- **Ships with no bubble:**
  - Coasting: `KeplerOrbit` costs nothing per tick; position is evaluated on demand.
  - Thrusting (AI haulers): a double Verlet integrator, about 30 ns each.
  - In warp: closed form in warp.md's `WarpTable`; no gravity applies during warp.
  - On leaving a bubble, state vector to elements (about 200 ns); on entering, elements to state.
- **Planets rotate.** Everything below the rotating band uses the **rotating surface frame**: terrain is static in Jolt, a landed ship sleeps, and the atmosphere co-rotates.
  - Fictitious forces are applied to awake bodies: centrifugal `−ω×(ω×r)` (0.085 m/s² at the equator for a 6 h day on a 1000 km planet) and Coriolis `−2ω×v` (0.17 m/s² at 300 m/s).
  - Rejected: an inertial frame for the surface (terrain would move at ωR ≈ 290 m/s inside Jolt).
- **Atmospheric flight (user decision D10: both).** Every ship flies pure 6-DOF with flight assist everywhere; in an atmosphere, wing parts also give **lift**. Both are forces in the force pass, so frames only fix the reference:
  - Airspeed is taken in `BodySurface`, where the atmosphere co-rotates: `v_air = v − wind(p)`, with `wind` from the planet's weather (planets.md, "Weather on a sphere"; `GasGiantWind` on a giant; ~0.3 µs analytic sample at 10 Hz per awake ship, lerped between samples).
  - Drag `½ρ|v_air|²·Cd·A(dir)` on the hull and, per wing part, lift `½ρ|v_air|²·C_L(α)·S` normal to `v_air` in the wing's plane, applied at the part's centre of pressure (stall past `α_stall` from part data). About 40 ns per wing part per awake ship. Flight assist sees lift as one more force and trims thrusters against it.
  - In the inertial frame above `RotatingTop` there is no air, so no lift or drag. A coasting rails ship whose trajectory meets the atmosphere top gets a wake event there (computed like impact-wake events), because drag and lift can't ride rails.

### Lagrange frames and anchors (user decisions D25 and the Lagrange-point answer)

**Lagrange frame.** A `FrameKind.Lagrange` frame sits at L1–L5 of a railed pair (star–planet or planet–moon), computed each tick from the two rails, so it costs nothing to store and is exact from the tick:
```
d     = r_S − r_P,  ḋ = v_S − v_P            (S's rail about P, in P's inertial frame)
x̂ = d/|d|,  ẑ = (d × ḋ)/|d × ḋ|,  ŷ = ẑ × x̂     rotating, pulsating basis of the pair
origin = r_P + |d| · (Xi · x̂ + Eta · ŷ)          Xi, Eta from the mass ratio m_S/(m_P+m_S): L4/L5 exact, L1–L3 solved once (Newton, ~1 µs)
ω     = (d × ḋ)/|d|²                           frame angular velocity; the origin's velocity and acceleration follow from the Kepler state in closed form
```
- About 60 ns per live Lagrange frame per tick (only frames with an awake member; a frozen one is evaluated on demand). Lagrange frames exist only while an anchor holds, or is engaging, there (pool of 64, grown on demand), so there are no permanent frames per pair.
- **Lagrange zone:** within `r_grid + 2 km` of any grid anchored at that point (as the anchored zone, leaving at `+ 2.6 km`; `ZoneRadius` is only the cheap bound tested first), the zone beats the SOI test and members sit in the Lagrange frame. Inside it, gravity is **both** bodies, `g = −μ_P r_P/|r_P|³ − μ_S r_S/|r_S|³` (one extra term, ~10 ns): the one deliberate exception to patched conics, kept to the zone. Outside the zone everything is patched conics as before.
- Bubbles in the zone are `BubbleAnchor.Station` bubbles in the Lagrange frame: scene gravity = two-body gravity at the origin minus the frame origin's acceleration minus `ω×(ω×r) + ω̇×r` at the origin; per awake body the delta plus Coriolis. On an eccentric pair the frame pulsates, so these terms vary slowly over the orbit (they are recomputed at the 10 Hz membership pass).
- Unanchored entities without a Jolt body leave the zone to ordinary rails about the dominant body. Nothing is `Frozen` in a Lagrange frame except anchored and docked grids, so a ship left near L1 can't hang there for free.

**Anchors on any grid (D25).** exotic-matter's `StationAnchors` owns the anchor (cores, reserve, warnings, footprint, failure); this section is the frame side, and the Lagrange target is this doc's extension of it. An anchor holds any grid (ship or station) at a pose in a `BodySurface` frame (any altitude) or in a Lagrange frame. It can't target a minor body's or asteroid's frame, and it won't engage on a grid in contact with or tractored to an asteroid; tractor beams do that job (an anchored grid may still tractor an asteroid: the reaction is debited to the anchor and counts toward overload, tractor-beams.md).
- **Activation at position and velocity** (exotic-matter's numbers). The pilot picks the target pose (frame + position + attitude). Engage requires the grid's centre of mass within `EngagePosTolM` (5 m) of the target, velocity relative to the target frame below `EngageMaxVelMs` (5 m/s: "mostly" at the right velocity) and `|ω_rel|` below 0.05 rad/s, out of contact with terrain. During the 600-tick spin-up the grid's own thrusters hold it against gravity; the anchor pulls it onto the pose along a critically damped path and pays the residual `m·Δv` as an impulse at `EmPerNs`. At engage the grid transfers to the target frame through the exact transfer maths.
- **Hold cost** stays `EmPerNs · m · |a_hold|`, with `a_hold` = the acceleration of a point fixed in the target frame minus gravity there (two-body gravity in a Lagrange frame). It is zero at the equatorial synchronous radius and at an exact L4/L5 on a circular pair (already orbits, as exotic-matter's synchronous case), and small near L1/L2. On an eccentric pair `a_hold` varies over the orbit: billing uses its orbit average, computed once at engage (64 samples, ~5 µs) and settled lazily, but the overload test uses the orbit **maximum** and the drop footprint the worst of the 64 release phases, so an average can't hide an overload or a lethal release phase.
- **Crawl (slow moves over days).** exotic-matter's crawl: target pose, speed up to `CrawlMaxMs` (0.05 m/s along the surface, 0.02 m/s in altitude: ~4.3 and ~1.7 km per game day), straight segments of at most 1 h, billing, footprint and claim checks per segment (factions-and-law.md: a `Claim` with the `Anchor` right, or its holder's approval), never a frame change; plus a turn rate up to 1° per game hour. Frame side:
  - The pose is piecewise linear in the tick per segment, `Local(t) = p0 + v_c·(t − t0)`, so an **unattended crawling grid stays `Frozen`** and costs nothing per tick (docked children ride on their fixed poses). A segment is planned only if its swept bounds clear terrain (height bounds + edit bounds) and other grids at plan time; anything that could later enter it must be near a player, which puts the grid in a bubble.
  - In a bubble the grid becomes a **kinematic** compound (`MoveKinematic`, one per tick) while crawling, so walkers ride along and dynamic bodies are pushed, never it; static again when stopped. A kinematic body passes through static terrain, so a crawl also stops at the first terrain contact.
  - The Station bubble, which never rebases at rest, rebases by a 32 m multiple once a crawl has carried the grid more than 512 m from the origin (about 3 game hours at 0.05 m/s).
- **Release and drops.** Release or failure is as exotic-matter's: the grid returns to physics with its inertial velocity (plus the crawl velocity, at most 0.05 m/s). A dropped station is meant to be a rare disaster: it mostly burns up in the atmosphere (exotic-matter's entry heating) or hits the ground hard through the impact rules. Thrusters and warp drive are locked while anchored and **unlock at `AnchorFailing`** (before the release tick), so a station with enough thrust can hold itself or fly; escape pods are small grids launched through the normal transfer from the station's frame. A Lagrange-anchored grid released goes to ordinary rails about the dominant body.
- **Sieges and capture (D27; the rule is factions-and-law.md's "Sieges and capture", the anchor side is exotic-matter.md's).** Factions prefer capture to destruction; the frame side changes only as follows. `Release` needs owner authorisation and its 60 s countdown; tractor or ram overload above the anchor's hold limit first drains the reserve and raises a public `AnchorOverload` alarm with a grace window (`EvacGraceS` by station class, factions-and-law.md) before failure, so a drop goes through the normal release path above. Capture at the station's `CommandCore` transfers anchor `Release` and crawl authority with ownership; the pose, frame and Station bubble are untouched (no transfer, no rebase). Tractor delivery onto another party's grid without consent is a weapon use (D26, tractor-beams.md), and an emitter without antimatter only pulls (D28); neither changes frames.

### Physics scenes (bubbles)

```csharp
public enum BubbleAnchor : byte { Surface, FreeFall, Grid, Station, Tunnel, Source }   // Station: an anchored grid's static bubble (BodySurface or Lagrange); Tunnel: wormholes.md; Source: a ring-lane or field bubble, rings-and-asteroids.md

public sealed class PhysicsBubble
{
    public BubbleId Id; public FrameId Frame; public BubbleAnchor Anchor;
    public double3 Origin;          // in Frame. Surface: multiple of 32 m (chunk-exact). FreeFall: on Origin rails.
    public KeplerOrbit OriginRails; // FreeFall only: the bubble falls with its cluster
    public PhysicsScene Scene;      // one pooled Jolt PhysicsSystem, axes = Frame axes (never rotated)
    public int Entities, AwakeBodies, Characters, Proxies;
}
```

- **Axes always equal the frame's axes**, so terrain chunk statics sit at exact integer offsets with no rotation. Gravity is an arbitrary vector (`PhysicsSystem.SetGravity`), and `CharacterVirtual` takes a per-character up vector.
- **Surface bubble:**
  - Jolt gravity = `g(origin) + centrifugal(origin)`.
  - Per awake body, add `m·(g(p) − g(origin))` + Coriolis. The gravity direction error is 0.11° at 2 km.
- **Free-fall bubble** (orbit, deep space):
  - The origin rides a Kepler orbit with its cluster, and Jolt gravity is 0.
  - Per awake body, add tidal `m·(g(p) − g(origin))`. This matters in low orbit: about 0.02 m/s² at 2 km.
  - Relative velocities stay small, which keeps contacts, CCD and broadphase sweeps tight.
- **Grid (interior) bubble:**
  - Lives in ship-local coordinates. Interior meshes and both faces of the hull are **static** there forever, however the ship moves.
  - **Interior gravity is two separate devices (user decision D9): plating and the inertial dampener.**
    - **Plating** adds floor gravity and cancels nothing (exotic-matter). `g_rest` is what the grid feels at rest in its frame (real gravity on a landed ship, `−a_hold` on an anchored one, 0 in free fall). A powered, fed plated room tops the floor up to its setting along the room's floor-down axis `n̂`: `g_floor = g_rest + n̂ · max(0, g_set − dot(g_rest, n̂))`, so a 1 g plate on a 1 g landed ship supplies (and bills) nothing instead of doubling gravity; unplated rooms have `g_floor = g_rest`. Recomputed on events (plating change, landing, anchoring), never per tick.
    - **The inertial dampener** is its own device (ship-design's `fn.dampener`) with its own volume: the rooms inside its bubble radius (ship-hulls' coverage). It keeps everything inside moving with the ship, at the cost of energy, using exotic matter + power, up to its rating `a_d` (exotic-matter owns the runtime: `a_d = min(|a_man|, a_max) · min(1, MassMax / m_in)`, also limited by delivered power and EM and by the `Limit` input; catalogue S/M/L = 3/6/10 g, 50/500/5,000 t; ship-design derives `a_max` and `MassMax` from parts); a room covered by two dampeners takes the stronger. It cancels the **manoeuvre** part of the interior acceleration:
      `a_man(r, v) = −(a_proper − a_rest) − α×r − ω×(ω×r) − 2ω×v`, where `a_rest` is the proper acceleration at rest in the frame (so landing or anchoring never shows up as a manoeuvre).
    - **Fictitious forces are applied only above the dampener rating:** `g_interior = g_floor + a_man · max(0, 1 − a_d/|a_man|)`. Inside a powered, fed dampener below its rating the interior is calm; above it, occupants and cargo feel only the excess. Outside any dampener volume (or with it unpowered, unfed or destroyed) the full `a_man` applies, which is the old real-effects branch.
    - Scene gravity is `g_interior` at the bubble origin; per awake body, the delta of the `α×r`, `ω×(ω×r)` and `2ω×v` terms is added only for the leaking fraction (0 when fully damped, so a calm interior costs no per-body work).
    - **Frozen contents** (no bubble) stay frozen while the leak is under `WakeLeak` (0.5 m/s², knob). A larger leak wakes the dampener volume's contents (drawing a grid scene from the pool) so cargo shifts and crew stumble; that is rare by design.
    - **Cost** is exotic-matter's: power and EM in proportion to the mass inside × the cancelled acceleration. The force pass adds `m_in · a_d` (with `a_d ≤ |a_man|`) from the ship-level `a_man` to the grid's ledger accumulator (one multiply per dampener per tick, `m_in` updated on events), and the 10 Hz ledger debits it, so it is paid with or without a bubble. Rest acceleration is never billed: a landed or anchored ship's dampener idles at standby.
    - **`GridInertial` flag (warp.md):** set on a grid in warp from T0 to T1 + 1, on unanchored rails ships, and on grids held in `Throat` placement (wormholes.md). The interior is plating or zero-g with no manoeuvre or fictitious term. In warp's real-space segment it removes the brake field but keeps contact impulses. The proper-acceleration history is reset at T0, TDrop and T1 + 1. During warp, exterior door proxies are dropped. Nothing is sealed (D66, warp.md): exterior portals do not collide as closed, and while a `WarpShell` exists the grid's leave rule is the shell test.
    - Velocity snaps from warp, flush and pulses stay excluded by the proper-acceleration history reset. While warp.md's `GridInertial` flag is set (in warp, and unanchored rails ships) the interior is plating or zero-g with no manoeuvre term, so the dampener draws nothing; in warp's real-space last segment the brake acts on every body alike (not felt), but contact impulses are, and the dampener absorbs them up to its rating (D17, D18). Neither device cancels tidal stretch (that is exotic-matter's tidal compensator; wormholes and the black hole only use it). The same split holds for other manoeuvre-type accelerations (throat drag, tractor forces on the grid): the dampener cancels them up to `a_d` and the excess leaks; plating never cancels them, and undamped rooms feel all of it.
  - Coupling is one-way: the ship moves the interior; interior contents don't push the ship. Cargo mass is added to the hull compound on load and unload only.
- **Size limits:** soft radius 2 km, hard 4 km (float ulp 0.24 mm and 0.49 mm). Jolt's own guidance is a few km in single precision.
  - **Huge structures (user decision):** anything larger is built as several linked grids, each with its own frame and bubble, joined by fixed links (as docking collars) and stitched across the seams with kinematic proxies. Double-precision Jolt stays off the table.
- **Who gets a Jolt body:**
  - Any entity within 2 km of a *physics anchor* (a player, or an entity in contact with something simulated). It leaves to rails or frozen at more than 3 km from all anchors, and only when contact-free.
  - Parked ships with nobody nearby are `Frozen` in the surface frame.
  - Interiors without occupants or awake loose items have no bubble; their contents are frozen in grid coordinates.
- **Merge and split:**
  - Merge when the **predicted** gap between two bubbles in the same frame within the next 2 s is < 500 m (closest approach of the two clusters' current states; smaller into larger, as an island transfer). Hot pairs (closing fast or already near) are tested every tick, the rest at 10 Hz (warp.md, ~15 ns per pair).
  - **Weapons never merge bubbles** (combat-and-sensors.md section 5): rounds, beams and missiles live in the SOI's inertial frame in double and are tested against whatever bubble or rails hull their path meets, so ships 80 km apart fight from separate bubbles. Bubbles merge only for the physical reason above. Rounds are not bodies, so `ObjectLayers` needs nothing new.
  - Split when a contact- and proximity-connected group's gap to the rest is > 1500 m, its predicted minimum gap is also > 1500 m, and at least 2 s have passed since the last merge (warp.md).
  - Space Engineers runs Havok worlds per cluster in the same way; this is the proven pattern.
- **Rebase (origin shift inside a bubble):**
  - Surface: when the cluster centroid is more than 1024 m from the origin, shift by a multiple of 32 m (chunk origins stay exact) and re-set the scene gravity. Cost is about 1 µs per body via `SetPositionRotationAndVelocity`. At 1 km/s it happens once a second, about 0.3 ms with 300 terrain colliders.
  - Free-fall: when the offset exceeds 1 km or the cluster's mean velocity exceeds 50 m/s, re-fit `OriginRails` to the centroid state and apply a Galilean boost (positions and velocities).
- **Proxies give contact continuity at boundaries.** During a hysteresis band, each scene holds **kinematic proxies** (at most 64) of the other scene's geometry near openings.
  - The grid scene mirrors terrain chunks and nearby hulls within 20 m of occupants near doors and ramps, moved by `MoveKinematic` while the ship moves.
  - The exterior scene already has the hull as the real ship body.
  - A character walking down a ramp sees both sides before it transfers. Proxies push but are never pushed; that one-way push across the boundary is accepted.

### When things enter and leave (the user's question)

| Transition | Enter when | Leave when | Hysteresis / extra rule |
| --- | --- | --- | --- |
| Solar root ↔ Solar root (other star system) | wormhole transit event | n/a | only through a wormhole mouth (wormholes.md); never by distance |
| Solar ↔ body SOI | r < 0.98 r_SOI | r > 1.02 r_SOI | 4% band; gravity source switches with the frame |
| Into a Lagrange frame | inside a live Lagrange zone (`r_grid + 2 km`) | outside `r_grid + 2.6 km` | the zone beats the SOI test; two-body gravity inside |
| Anchored zone (`BodySurface`) | within `r_grid + 2 km` of an anchored grid | outside `r_grid + 2.6 km` | the zone beats the rotating-band rule |
| Body inertial ↔ body surface (rotating) | altitude < `RotatingTop` | altitude > 1.3 × `RotatingTop` | `RotatingTop = max(atmo top, 0.05 R)`, e.g. 50 / 65 km on a 1000 km airless body |
| Into a ship grid | centre inside the grid's interior volume by ≥ 0.5 m, **or** grounded / mag-booted on any hull surface (`CharacterVirtual` ground body) | outside interior volume + 1 m **and** not grounded on the grid for 0.25 s | the ground rule beats the volume rule (EVA on the hull stays in the grid). While a `WarpShell` exists (warp.md, D66) the leave rule is the shell test instead, and a crossing in cruise is a loss, not a transfer |
| Ship into hangar (nested grid) | ship centre inside hangar volume shrunk by 0.5 × its radius | centre outside hangar volume + 2 m | ship body moves from the exterior scene into the host's grid scene; its own grid re-parents |
| Docking collar | lock engaged (gameplay event) | undock event | docked ships merge into one compound body; child grid gets a fixed pose under the parent grid |
| Into a bubble (gets a Jolt body) | ≤ 2 km of a physics anchor | > 3 km from all anchors, contact-free | otherwise rails (coasting / thrusting) or frozen (parked) |
| Bubble merge / split | predicted gap within 2 s < 500 m | group gap > 1500 m **and** predicted minimum gap > 1500 m **and** ≥ 2 s since the last merge | hot pairs every tick, others at 10 Hz (warp.md) |
| Warp engage | T0, if validation passed: placement becomes `Warp`, the carried subtree goes along, with grid members on the hull (grounded or mag-booted, D66) | n/a | leaves its bubble; membership checks skip `Warp` placements (warp.md) |
| Warp drop | TDrop: attaches to the frame announced at commit and the bubble chosen at TDrop (corridor capsule rule), booked in the arrival budget | n/a | simulated and braked until T1; `GridInertial` until T1 + 1 (warp.md) |
| Rebase | centroid > 1024 m (surface) / > 1 km or > 50 m/s (free fall) | n/a | at most once per second per bubble |

- **Nesting limit:** depth 3 (station > carrier > fighter).
- **Transfers wait for clean moments.** A body changes scene only when it is not touching anything staying behind. Otherwise its whole contact island moves together (a crate stack on a cargo lift). The transfer is forced at the band's outer edge.
- **Timing:** membership is checked at 10 Hz, staggered across entities (skipping `Warp` and `Throat` placements). Transfers queue and run at the start of the next tick, before forces.
- **Arrival budget (warp.md):** warp arrivals are pre-booked at commit in a per-tick arrival budget (≤ 64 bodies per tick; a full tick pushes TDrop and T1 back one tick before announcing) and are never deferred once announced. A merge may exceed the 32-transfer budget only for islands on the predicted contact (≤ 128). Wormhole ejections use the same budget (wormholes.md).

**Transfer** (exact rigid-frame maths in double; Jolt bodies can't move between systems, so destroy and recreate with the same refcounted shape). For a child frame with origin `o`, rotation `R`, origin velocity `u` and angular velocity `ω` (all in the parent):

```
p_c = Rᵀ (p_p − o)
v_c = Rᵀ (v_p − u − ω × (p_p − o))
ω_c = Rᵀ (ω_p − ω)
```

- The surface frame is the case `o = 0`, `u = 0`, `ω = spin`.
- A grid frame takes `o`, `u` and `ω` from its ship body after each step.
- An EVA exit starts at the hull point velocity `v_ship + ω×r` plus the jump.
- Error introduced per transfer: at most one float ulp in the bubble, ≤ 0.5 mm.
- Warm-start contact cache is lost; the contact-free rule above makes that harmless.

**Characters:**
- With gravity (surface, plated grid, or an interior whose `g_interior` exceeds 0.5 m/s²): `CharacterVirtual` with up = `−ĝ(p)`.
- In zero-g (EVA in a free-fall bubble, an unplated grid under its dampener rating): a dynamic capsule body with thrusters or mag boots.
- Players inside a moving ship live in grid coordinates. Ship speed and spin never reach their controller.

### One Jolt system per bubble, pooled (the world-count answer)

- **Recommendation:** one `PhysicsSystem` per bubble, drawn from a pool built at load. Most of them don't step on a given tick:
  - **Shared:** one `JobSystemThreadPool` for big scenes, one `JobSystemSingleThreaded` per worker, and one `TempAllocatorImplWithMallocFallback` (4 MB) per worker plus one for the main thread.
  - **Skip** `Update` when `GetNumActiveBodies() == 0`. Characters don't need `Update`; they are queries via `ExtendedUpdate`.
  - **Big scenes** (more than 64 awake bodies) step on the shared pool, one after another; Jolt parallelises islands inside them.
  - **Small scenes** step concurrently, each as one `Brutal.Concurrency` job calling `Update` with a single-threaded job system, so there is no barrier overhead.
- **Rejected:** one shared system with a layer per zone.
  - All zones would have to overlap near the float origin, so their broadphase trees mix unless there is a broadphase layer per zone (byte-sized, layer tables fixed at `Init`, N² filter).
  - Every zone would step at one rate, one zone's spike would stall all of them, and it can't shard across servers later.
  - Its only win, one `Update` dispatch, is recovered by skipping asleep scenes.
- **Rejected:** pocket offsets in one float space (precision runs out past ±4 km).
- **Pool sizes:**
  - Client: 4 exterior (4096 bodies) + 8 grid (1024 bodies).
  - Listen server / single player: client sizes ×2.
  - Dedicated server: 32 exterior + 96 grid, grown on demand during the maintenance window. A new system costs about 1 ms.
  - Estimated memory, to be measured: about 4 MB per exterior and about 1 MB per grid scene, plus the per-worker temp allocators.

### Per-tick cost (estimates, to measure with `--zone-bench`)

| Item | Cost |
| --- | --- |
| Celestial rails + spin (≤ 64 bodies) | ~6 µs |
| Frame pose cache (≤ 512 live frames) | ~10 µs |
| Force pass (gravity delta, tidal / fictitious, thrust) | ~30 ns per awake body; 2000 bodies ≈ 60 µs |
| Aero (drag + lift per wing part, wind sample at 10 Hz) | ~40 ns per wing part per awake ship in an atmosphere; +0.05 µs a tick per ship amortised for the wind (0.3 µs at 10 Hz) |
| Dampener (ship-level `a_man`, cost integration) | ~10 ns per dampener per tick; per-body leak terms only while leaking |
| Lagrange frames (pool 64, two-body gravity in zone) | ~60 ns each a tick; +10 ns per awake body in a zone |
| Anchors, including crawl | 0 per tick when frozen (piecewise-linear pose, scheduled ≤ 1 h segments); one `MoveKinematic` per crawling grid per tick in a bubble |
| Asleep or empty scene | 0 (Update skipped) |
| Small scene, few awake bodies, single-threaded | ~20–50 µs, in parallel across workers |
| Same scene on the thread pool | ~50–100 µs (barrier wake-ups): reason for the split above |
| `CharacterVirtual.ExtendedUpdate` | ~20–50 µs each; 64 players ≈ 2 ms CPU, spread over jobs |
| Membership checks at 10 Hz, staggered | ~100 ns per entity; 5000 entities ≈ 50 µs per tick on average |
| Transfer (destroy + create, shape reused) | ~5 µs per body; budget 32 per tick, overflow deferred (warp arrivals pre-booked, ≤ 64 a tick, never deferred) |
| Merge prediction | ~15 ns per pair; 128 bubbles ≈ 15 µs per tick on average (warp.md) |
| Rebase | ~1 µs per body in the scene, ≤ once per second per bubble |
| Rails ship (coasting / thrusting) | 0 / ~30 ns |

- Typical single-player overhead of the zone system, excluding Jolt's own step: under 0.3 ms per tick.
- Server with 64 players and about 2000 awake bodies: Jolt about 3 ms CPU, about 0.6 ms wall on 6 workers.
- All of this is zero-alloc: SoA arrays, preallocated transfer and proxy queues, no LINQ or closures.
- `ProfilerPanel` gets rows for Rails, Forces, Scenes (stepped / asleep / pool vs job), Characters, Maintenance, Transfers and Rebases.

**Tick order:**
1. Clock (paused while no player is connected).
2. Rails and pose cache for the step's end, `TickIndex + 1` (systems with interest only; Lagrange frames from their pairs' rails).
   2b. Warp routes (warp.md): evaluate, send due beacons, run due commits in `(TDrop, WarpId)` order, queue the drops due at TDrop.
3. Queued transfers and rebases (including warp drops, with the drop-sphere test and any `WarpBlast`).
4. Inputs.
5. Forces.
6. Big scenes on the pool, then small scenes as jobs.
7. Characters per scene, in parallel.
8. Write grid poses back from ship bodies.
9. Membership checks (10 Hz), merge prediction (hot pairs every tick) and net snapshot (30 Hz).

Round 6 additions, each owned by its doc:
- **combat-and-sensors.md:** step 3 applies last tick's queued hits (in hit order) and bends; step 4 takes fire commands, turret slew and scans; step 5 adds recoil, gravity-lobe forces and the structural pass with yield; a new **step 8c**, after poses are final, integrates and sweeps rounds, beam samples and missile guidance and queues hits; step 9 adds the staggered sensor slice and contact messages.
- **factions-and-law.md:** nothing per tick. Heat, fines due, rent, surveys and the strategic step are lazy `(value, tick)` settles or heap events, and stop with the clock in step 1.

### Camera-relative rendering

- **Camera:** `FrameId` + `double3` + `doubleQuat`. The view matrix has **no translation**.
- **Render-rate poses in double (X5, implemented for rails and spin):** each frame `CelestialSystem.Evaluate(TickIndex − 1, alpha)` writes the closed-form poses at render time into `FrameTree.Render`, and draws compose them with `TryRelativeRender`. Render time trails the sim by one tick plus alpha, the same as interpolating grids between the last two ticks and as the ocean's render time. `--orbit-test inertial` flies the camera in the planet's inertial frame with the planet turning under it: pose error against the closed form ~1e-14 px, where tick poses would step ~1 px.
- **Per frame:**
  - Each planet: one `double` relative pose to the camera, giving a float rotation for its axes.
  - Each chunk: `(chunkOrigin_d − camPos_d)` cast to float into the existing push constant. About 20 ns × 5000 chunks ≈ 0.1 ms.
  - Ships: the grid chain is composed in double, then cast.
- Reverse-Z infinite projection (`FlyCamera.Projection`) already handles planets at 1e11 m: the error is relative, about 10 km at that range, which is sub-pixel.
- Shaders that use `frame.cameraPos` as world space (ocean, rain, spray) get `cameraPos = 0`. Anything that needs world phase (FFT tiling, noise) gets the camera position in the planet surface frame **modulo the tile period**, computed in double per cascade.
- `ChunkCoord` stays `int` (1000 km / 32 m = 31,250 fits). `WorldOrigin` and `FromWorld` become `double3` in the planet surface frame.

### Networking (32–64 players across a system)

- **Server-authoritative, 60 Hz sim**, snapshots at 30 Hz.
- **Single player is a listen server in-process.** The host's view reads server state directly: no second Jolt world, no prediction for the host.
- **The wire speaks frames, never bubbles.** Bubbles are server-internal; clients build their own for prediction and visuals.
  - Positions: frame-relative int64 at 1/1024 m, delta-compressed against the last acked state. Small motions take about 2 B per axis.
  - Rotation: smallest-three, 32 bits. Velocity: half floats.
  - About 20 B per body update.
  - Planets cost zero bandwidth (rails come from seed + tick). Coasting ships send elements once per change (about 48 B).
- **Interest by frame tree:**

  | What | Rate |
  | --- | --- |
  | Own bubble | full rate |
  | Same frame within 20 km | 10 Hz |
  | Same SOI | rails or 1 Hz |
  | Other SOIs | radar contacts at 0.2 Hz |
  | Other star systems | nothing (system id and player list only) |

  Round 6 (combat-and-sensors.md "What the server sends"): the rates stand, but every row beyond "Own bubble" (and anything within 2 km) is sent only if the client's group holds the entity as a contact or its character could see it (the eyeball rule). "Radar contacts" are that doc's `ContactBlip` and `ContactTrack` messages.

  Budget: 64 players × about 50 visible bodies × 30 Hz × 20 B ≈ 2 MB/s server out, about 30 KB/s per client.
- **Frame changes** are reliable, tick-stamped events: "entity E → frame F at tick T, state S". The client applies them at T.
- **Prediction happens in the local frame.**
  - On foot inside a ship: predicted in grid coordinates, so ship error never jitters the player.
  - Piloting: predict the ship in the client's exterior bubble.
  - Reconcile only owned entities: snap to server state, then replay inputs through our own flight or character integrator. Collisions are left to error smoothing.
  - Full Jolt rollback isn't available (`SaveState` / `RestoreState` are not exported). Custom per-body snapshots via `BodyInterface` are enough for owned entities.
- **Dedicated sharding later** goes by frame subtree: each star system is the natural first cut (its only boundary is the wormhole transit event); inside a system, each planet SOI plus its grids is a shard, and interplanetary space is a shard. The hand-off is exactly the frame-change event above, at the SOI boundary with the same 4% hysteresis for sublight; warp is the cross-shard hand-off within a system (warp.md). One global `TickIndex` for all shards. Keep the transfer message serializable from day one.

### Files

- **Change:**
  - `src/Physics/PhysicsWorld.cs` → `PhysicsScene`:
    - Takes a shared `PhysicsRuntime` (job pools, per-worker temp allocators) and per-scene max bodies.
    - Adds settable gravity, `SetPositionRotationAndVelocity`, user data (entity id), an activation listener (awake counts), dynamic convex and compound shapes, `MoveKinematic` proxies and `CharacterVirtual`.
    - Keeps the managed ray cast fallback.
  - `ObjectLayers.cs`: Static, Moving, Character, Proxy (no proxy-vs-static), Debris, Sensor. Same table in every scene.
  - `BodyHandle` gains a scene index (`BodyRef(ushort Scene, uint Id)`).
  - `FlyCamera` → double pose + frame, view without translation.
  - `Renderer` frame UBO goes camera-relative.
  - `ChunkCoord.WorldOrigin` / `FromWorld` → double.
  - `TerrainWorld`:
    - `Update` takes a planet-local `double3`.
    - `AddStatic` targets each surface bubble whose collision set covers the chunk, at `chunkOrigin − bubbleOrigin`.
  - `GameApp`: the single `_physics` becomes `Universe` (FrameTree + Celestials + BubbleManager). Tick order as above.
- **Reuse unchanged:**
  - `JoltRuntime` (add export probes for everything above).
  - `StaticMeshShape` and `TriangleBvh`: shapes are refcounted and shared across scenes and proxies.
  - `FixedClock`, `FrameStats`, `TimingRing`, `WorkerBudget` (add a Physics share for small-scene jobs).
- **New:**
  - `src/Frames/`, with no Jolt dependency: `FrameTree` (forest of Solar roots), `KeplerOrbit`, `CelestialSystem`, `Gravity`, `FrameMath`, `LagrangeFrames`, `Anchors` (engage, crawl, hold rate; shared with exotic-matter's `StationAnchors`), `Aero` (drag, wing lift).
  - `src/Physics/InteriorGravity.cs`: plating floor gravity and the dampener leak.
  - `src/Physics/`: `PhysicsRuntime`, `PhysicsScene`, `BubbleManager`, `Transfer`, `Proxies`.
  - `docs/frames-and-zones.md`.
- **Cut:** the test scenes tied to water (`FloodTest`, `PondTest`, `LakeTest`, `RiverTest`, `CaveTest`, `SeasideTest`, `SurfTest`) leave with the water systems.
  - Buoyancy and shore wash query `OceanSurface` with planet-surface-frame double positions; the water section owns the sphere mapping.

### Double-precision Jolt upstream: not worth it now

- `JPH_DOUBLE_PRECISION` makes only positions double. Velocities and shapes stay float, and Jolt is somewhat slower in that mode.
- It would remove surface rebasing, which is about 0.3 ms a second. It would **not** remove:
  - the rotating frames,
  - ship grids (walking in a ship at 1 km/s is about controller stability and local-frame prediction, not precision),
  - bubbles (needed for parallelism, sleep-skipping and interest anyway).
- The upstream ask that matters more is exporting what the native lib lacks: `PhysicsSystem_SaveState` / `RestoreState`, `NarrowPhaseQuery_CastRay`, indexed `MeshShapeSettings`. Propose that to BRUTAL separately (no `BRUTAL/` edits).
- Not needed: the user chose huge structures as several linked grids, so no single bubble spans more than 4 km.

### Open questions for the user

None left from earlier rounds: interior gravity (D9), very large grids (linked grids) and patched conics (rails + SOI, with Lagrange-point stations held by anchors) are decided. New knobs to confirm when they are tuned in game, not blocking: `WakeLeak` 0.5 m/s², the crawl turn rate 1°/h, the Lagrange zone margin 2 km (engage and crawl limits are exotic-matter's data).

All costs above are estimates. Nothing was built or run, and no files were changed.
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
