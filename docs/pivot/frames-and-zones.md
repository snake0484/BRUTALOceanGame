## Coordinate frames and physics zones

### Recommendation in one paragraph

Split the user's "solar > planetary > ship grid" idea into two layers. **Frames** are a cheap double-precision tree with no limit on count: Solar root, then each celestial body's inertial frame, its rotating surface frame, and ship/station grids (grids can nest). **Physics scenes** are a flat, pooled, limited set of Jolt `PhysicsSystem`s. Each one simulates a float "bubble" around a cluster of interacting things inside one frame. Gameplay, networking and rendering speak frames. Only the physics layer knows about bubbles. Gravity is patched conics: only the dominant body pulls, and celestial bodies ride Kepler rails evaluated from the integer tick. Everything stores `double3` relative to its parent frame. int64 fixed point is used only on the wire.

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
public readonly record struct FrameId(ushort Value);          // 0 = Solar root
public enum FrameKind : byte { Solar, BodyInertial, BodySurface, Grid }

/// Pose of a frame relative to its parent, refreshed once per tick (rails, spin) or after the physics step (grids).
public struct FramePose
{
    public double3 Position;        // origin in parent axes, m
    public doubleQuat Rotation;     // child axes -> parent axes
    public double3 Velocity;        // origin velocity in parent, m/s
    public double3 AngularVelocity; // of child axes, in parent, rad/s
}

/// SoA, preallocated (4096 frames), zero alloc. Index = FrameId.Value.
public sealed class FrameTree
{
    public FrameId[] Parent; public FrameKind[] Kind; public byte[] Depth;
    public FramePose[] Local;
    public int[] OwnerEntity;      // grid -> ship entity; body frames -> celestial index
    public FramePose Relative(FrameId from, FrameId to); // walk to common ancestor, compose in double
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

/// Where any entity is. Bubble/Body are invalid when the entity is on rails or frozen.
public struct Placement
{
    public FrameId Frame; public double3 Position; public doubleQuat Rotation;
    public double3 Velocity; public float3 AngularVelocity;
    public BubbleId Bubble; public BodyHandle Body; public KeplerOrbit Rails; public PlacementMode Mode; // Simulated, Rails, Frozen
}
```

- **Tree shape:** Solar → Planet inertial → {Planet surface (rotating), Moon inertial → Moon surface} → grids. A grid's parent is whatever frame its ship is in: a surface, an inertial frame, or another grid for a docked ship.
- **Why double3 and not int64 fixed point:** positions are always stored relative to the parent frame. Inside a 1000 km planet's SOI (around 5e7 m) the double ulp is about 1e-8 m. Even heliocentric at 40 AU it is about 1 mm, and only ships in transit live there. Rotation, Kepler and transform maths all need double anyway. Rejected: int64 fixed point in the sim (uniform precision isn't needed with hierarchical frames, and it makes every rotation and orbit awkward). int64 is used **on the wire** at 1/1024 m.
- **Time:** one global `TickIndex`. Orbit phase and spin angle come from `tick % PeriodTicks`, so they never lose precision, even after years of server uptime.

### Gravity and rails

- **Patched conics:** the dominant body is the deepest one whose SOI (Laplace, `a·(m/M)^0.4`) contains the entity. Gravity is `-μ r/|r|³` in double, about 10 ns.
  - Rejected: n-body (it breaks rails, determinism and cheap extrapolation).
- **Celestial bodies:** pure Kepler rails, about 100 ns each including the Newton solve. 64 bodies cost about 6 µs a tick.
- **Ships with no bubble:**
  - Coasting: `KeplerOrbit` costs nothing per tick; position is evaluated on demand.
  - Thrusting (AI haulers, quantum travel): a double Verlet integrator, about 30 ns each.
  - On leaving a bubble, state vector to elements (about 200 ns); on entering, elements to state.
- **Planets rotate.** Everything below the rotating band uses the **rotating surface frame**: terrain is static in Jolt, a landed ship sleeps, and the atmosphere co-rotates.
  - Fictitious forces are applied to awake bodies: centrifugal `−ω×(ω×r)` (0.085 m/s² at the equator for a 6 h day on a 1000 km planet) and Coriolis `−2ω×v` (0.17 m/s² at 300 m/s).
  - Rejected: an inertial frame for the surface (terrain would move at ωR ≈ 290 m/s inside Jolt).

### Physics scenes (bubbles)

```csharp
public enum BubbleAnchor : byte { Surface, FreeFall, Grid }

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
  - Gravity is the plating vector when powered. Otherwise it is real gravity minus the ship's proper acceleration, plus `−α×r − ω×(ω×r) − 2ω×v` on awake bodies.
  - Coupling is one-way: the ship moves the interior; interior contents don't push the ship. Cargo mass is added to the hull compound on load and unload only.
- **Size limits:** soft radius 2 km, hard 4 km (float ulp 0.24 mm and 0.49 mm). Jolt's own guidance is a few km in single precision.
- **Who gets a Jolt body:**
  - Any entity within 2 km of a *physics anchor* (a player, or an entity in contact with something simulated). It leaves to rails or frozen at more than 3 km from all anchors, and only when contact-free.
  - Parked ships with nobody nearby are `Frozen` in the surface frame.
  - Interiors without occupants or awake loose items have no bubble; their contents are frozen in grid coordinates.
- **Merge and split:**
  - Merge when two bubbles in the same frame come within 500 m of each other (smaller into larger, as an island transfer).
  - Split when a contact- and proximity-connected group is more than 1500 m from the rest.
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
| Solar ↔ body SOI | r < 0.98 r_SOI | r > 1.02 r_SOI | 4% band; gravity source switches with the frame |
| Body inertial ↔ body surface (rotating) | altitude < `RotatingTop` | altitude > 1.3 × `RotatingTop` | `RotatingTop = max(atmo top, 0.05 R)`, e.g. 50 / 65 km on a 1000 km airless body |
| Into a ship grid | centre inside the grid's interior volume by ≥ 0.5 m, **or** grounded / mag-booted on any hull surface (`CharacterVirtual` ground body) | outside interior volume + 1 m **and** not grounded on the grid for 0.25 s | the ground rule beats the volume rule (EVA on the hull stays in the grid) |
| Ship into hangar (nested grid) | ship centre inside hangar volume shrunk by 0.5 × its radius | centre outside hangar volume + 2 m | ship body moves from the exterior scene into the host's grid scene; its own grid re-parents |
| Docking collar | lock engaged (gameplay event) | undock event | docked ships merge into one compound body; child grid gets a fixed pose under the parent grid |
| Into a bubble (gets a Jolt body) | ≤ 2 km of a physics anchor | > 3 km from all anchors, contact-free | otherwise rails (coasting / thrusting) or frozen (parked) |
| Bubble merge / split | gap < 500 m | group gap > 1500 m | evaluated at 10 Hz |
| Rebase | centroid > 1024 m (surface) / > 1 km or > 50 m/s (free fall) | n/a | at most once per second per bubble |

- **Nesting limit:** depth 3 (station > carrier > fighter).
- **Transfers wait for clean moments.** A body changes scene only when it is not touching anything staying behind. Otherwise its whole contact island moves together (a crate stack on a cargo lift). The transfer is forced at the band's outer edge.
- **Timing:** membership is checked at 10 Hz, staggered across entities. Transfers queue and run at the start of the next tick, before forces.

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
- With gravity (surface, or plated grid): `CharacterVirtual` with up = `−ĝ(p)`.
- In zero-g (EVA in a free-fall bubble, unplated grid): a dynamic capsule body with thrusters.
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
| Asleep or empty scene | 0 (Update skipped) |
| Small scene, few awake bodies, single-threaded | ~20–50 µs, in parallel across workers |
| Same scene on the thread pool | ~50–100 µs (barrier wake-ups): reason for the split above |
| `CharacterVirtual.ExtendedUpdate` | ~20–50 µs each; 64 players ≈ 2 ms CPU, spread over jobs |
| Membership checks at 10 Hz, staggered | ~100 ns per entity; 5000 entities ≈ 50 µs per tick on average |
| Transfer (destroy + create, shape reused) | ~5 µs per body; budget 32 per tick, overflow deferred |
| Rebase | ~1 µs per body in the scene, ≤ once per second per bubble |
| Rails ship (coasting / thrusting) | 0 / ~30 ns |

- Typical single-player overhead of the zone system, excluding Jolt's own step: under 0.3 ms per tick.
- Server with 64 players and about 2000 awake bodies: Jolt about 3 ms CPU, about 0.6 ms wall on 6 workers.
- All of this is zero-alloc: SoA arrays, preallocated transfer and proxy queues, no LINQ or closures.
- `ProfilerPanel` gets rows for Rails, Forces, Scenes (stepped / asleep / pool vs job), Characters, Maintenance, Transfers and Rebases.

**Tick order:**
1. Clock.
2. Rails and pose cache.
3. Queued transfers and rebases.
4. Inputs.
5. Forces.
6. Big scenes on the pool, then small scenes as jobs.
7. Characters per scene, in parallel.
8. Write grid poses back from ship bodies.
9. Membership checks (10 Hz) and net snapshot (30 Hz).

### Camera-relative rendering

- **Camera:** `FrameId` + `double3` + `doubleQuat`. The view matrix has **no translation**.
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

  Budget: 64 players × about 50 visible bodies × 30 Hz × 20 B ≈ 2 MB/s server out, about 30 KB/s per client.
- **Frame changes** are reliable, tick-stamped events: "entity E → frame F at tick T, state S". The client applies them at T.
- **Prediction happens in the local frame.**
  - On foot inside a ship: predicted in grid coordinates, so ship error never jitters the player.
  - Piloting: predict the ship in the client's exterior bubble.
  - Reconcile only owned entities: snap to server state, then replay inputs through our own flight or character integrator. Collisions are left to error smoothing.
  - Full Jolt rollback isn't available (`SaveState` / `RestoreState` are not exported). Custom per-body snapshots via `BodyInterface` are enough for owned entities.
- **Dedicated sharding later** goes by frame subtree: each planet SOI plus its grids is a shard, and interplanetary space is a shard. The hand-off is exactly the frame-change event above, at the SOI boundary with the same 4% hysteresis. Keep the transfer message serializable from day one.

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
  - `src/Frames/`, with no Jolt dependency: `FrameTree`, `KeplerOrbit`, `CelestialSystem`, `Gravity`, `FrameMath`.
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
- Revisit double precision only if one bubble must span more than 4 km (see question 2).

### Open questions for the user

1. **Interior gravity:** is gravity plating always on, Star Citizen style (calm interiors, and ship manoeuvres don't throw you around), or do you want real inertial effects inside unplated or damaged ships? It decides which fictitious forces the grid scenes apply.
2. **Very large grids:** will players build ships or stations bigger than about 4 km, or fight capital battles spread over more than 4 km that must be one contiguous physics cluster? If yes, either big grids tile into several scenes or double-precision Jolt comes back on the table.
3. **Patched conics** (one dominant body, rails, as in KSP): OK, or do you want n-body features (Lagrange points, perturbations)? n-body gives up rails, cheap extrapolation and near-zero network cost for orbits.

All costs above are estimates. Nothing was built or run, and no files were changed.