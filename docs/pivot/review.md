## Contradictions

I checked each claim about existing code by reading it. File and line references are to the current branch.

| # | Where they disagree | Ruling |
|---|---|---|
| C1 | **Fictitious forces.** Zones applies centrifugal and Coriolis in the rotating surface frame. Planets ignores both, "as Star Citizen does". | **Apply them, as zones says.** If they are ignored, a ship orbiting at 49 km in the rotating frame has a speed set by g alone. When it crosses `RotatingTop` it gains ω×r ≈ 290 m/s, about 10 % of the 3.1 km/s orbital speed, so the orbit changes as it passes the frame boundary. On airless bodies orbits sit inside the band, so this cannot be avoided by moving the band. Characters may skip the forces (0.08 m/s²); dynamic bodies may not. |
| C2 | **How gravity is applied.** Planets: Jolt gravity 0 and `AddForce(m·g)` on every body every tick. Zones: scene gravity = g(origin) + centrifugal, plus a per-body delta on awake bodies only. | **Zones.** `BodyInterface.AddForce` defaults to `EActivation::Activate` (BodyInterface.gen.cs:1022), so the planets version wakes every body every tick. Nothing would ever sleep, and parked ships and debris would all simulate. |
| C3 | **Rotating band.** Zones: `RotatingTop = max(atmo top, 0.05 R)`, leave at 1.3×. Planets: atmosphere top or 0.25 R, leave at +10 %. | Take zones' numbers. Once C1 is settled the band only affects the cost of a transfer, not correctness. |
| C4 | **Bubble size and rebase.** Planets: rebase past ~4 km. Zones: soft 2 km, hard 4 km, rebase when the centroid is more than 1024 m away, in 32 m steps. | Zones. 32 m steps keep chunk statics at exact integer offsets. |
| C5 | **Ocean coordinates.** Inventory: a 2D `SeaFrame` per surface physics zone, with phases anchored to it. Planets: 3D wavevectors in the planet frame. Zones: `OceanSurface` is queried with planet-frame double. | Zones also says clients build their own bubbles. If the sea frame follows bubbles, server and client compute different wave heights at the same point, so boat buoyancy can never be predicted correctly. **Rule:** `OceanSurface.Sample(double3 planetLocal, long tick)` has one global definition. Use fixed per-region tangent frames, e.g. cube-sphere tiles of about 64 km keyed by tile id, blended at the edges. A single 3D plane wave `k·p` is not usable: 90° around the planet its tangent component goes to zero. The camera-window grid is a GPU cache only. |
| C6 | **Shore carrier.** Inventory: an analytic Snell phase plus a `frame.shore` uniform, with no pass. Planets: a `ShoreCarrier` compute pass sweeping 1/c(h). | Use inventory's for the slice: it costs nothing and is shader-only. Add the pass only if bays look wrong in game. |
| C7 | **Depth cap for buoyancy.** Inventory mirrors the swell depth cap on the CPU with a read from `ShoreDepthGrid`. | That grid is a 1 km window around the camera, and a dedicated server has no camera. Buoyancy must read `IShoreDepth` (analytic H + edits) directly: about 100 ns per sample, or cache it per body. |
| C8 | **Indirect draws.** Hulls says "reuse the arena, staging and indirect-draw code" from TerrainRenderer. | **That code does not exist.** TerrainRenderer.cs:306–338 issues one `DrawIndexed` per chunk with a 16 B push constant. CLAUDE.md's indirect-draw rule describes what is wanted, not what is there. Planets is right that this has to be built. |
| C9 | **terrain.vert.** Planets: "math unchanged" with a relative origin. Zones and hulls: per-body and per-ship rotation. | terrain.vert (lines 13–15, 40–44) only adds a translation. A camera in a ship grid, a secondary body (moon) or a ship brick all need a rotation. One shared per-draw instance record covers terrain and hulls: `float3 relOrigin; float scale; float4 quat`. |
| C10 | **Hull server work.** Hulls says the server skips meshing, yet interior collision is "built from the simplified render mesh". | Walking is server-authoritative, so the server must mesh interior bricks near occupants. It can skip the simplify step and drop the mesh after the shape is built. Exterior convex hulls also need coarse surface-nets vertices, so the server needs a mesher either way. |
| C11 | **Entering a grid.** Zones enters the grid by "interior volume". Hulls defines rooms as sealed air only, and fills sealed rooms in the exterior collision. | (a) Open a ramp or hangar and the bay becomes "space", so everyone inside drops out of the grid. (b) The filled exterior collider is solid behind a closed-room door, so a player outside physically cannot walk in to reach the 0.5 m entry depth. **Fix:** a separate `GridVolume`, meaning air enclosed when every portal is treated as closed. Carve each portal's swept volume out of the filled exterior shape, and make the door's outer face plus 2 m an entry trigger. |
| C12 | **Ship prediction.** Zones: snap, then "replay inputs through our own flight integrator". On the server the ship is a Jolt dynamic body. | Two integrators diverge, worst of all on contact. Instead, the client keeps a small Jolt scene for its own bubble: its own ship, statics, and kinematic proxies of others. Reset the owned body with `SetPositionRotationAndVelocity` and re-step about 8 ticks at 20–50 µs each, roughly 0.4 ms. No `SaveState` is needed. |
| C13 | **Persistent positions in edits.** Terrain `BrushOp` is `Vector3 Centre` today (BrushOp.cs:15). Planets moves it to `double3`. Hulls and the wire use int 1/1024 m. | Store and send every persistent position as int64 at 1/1024 m in its frame; double only at runtime. One quantisation rule means op replay is identical on every peer. |
| C14 | Octree depth: inventory says MaxLod ~16; planets says roots at LOD 15 and leaves up to 12. | Planets. Today's value is `MaxLod = 4`, `RootRadius = 2` (TerrainWorld.cs:18–20). |

Minor inventory slips: `swashSheet` starts at ocean_common.glsl:1499, not 1466. The gravity line is PhysicsWorld.cs:99, not ~88. Everything else I checked held: the per-instance `JobSystemThreadPool` and 32 MB temp allocator, `(float)renderTime` at OceanRenderer.cs:946, the world-float clipmap snap at :1114, `LoopPeriod = 1000`, `LACE_WRAP = 600`, `MinChunkY/MaxChunkY`, and `ChunkCoord` with float `WorldOrigin`.

## Risks

**Performance**
- **P1: Jolt native has no MeshShape or CastRay. Verified:** `JoltRuntime.cs:38–39` probes them, and `StaticMeshShape.cs:110–114` falls back to a StaticCompound of individual TriangleShapes. Every terrain collision chunk, and every hull interior brick, is roughly 2k sub-shapes. Memory is plausibly 150–300 KB per chunk against planets' 60 KB estimate. For the dedicated-server ring (3,200 chunks) that is 0.5–1 GB, and build cost and broadphase are worse too. **The upstream export ask is P0.** Measure the fallback in M3 before trusting any server numbers.
- **P2: Allocations on the streaming path.** `new uint[indices.Length]` per shape (StaticMeshShape.cs:42), plus a managed `new TriangleBvh` per chunk because native ray casts are missing. That is 60 chunks/s in low flight, all GC churn. Use a pooled or job-owned scratch buffer.
- **P3: Unbounded edit scan.** `ChunkJob.SetOps(List<BrushOp>)` copies every op it is handed and grows with `Array.Resize` (ChunkJob.cs:44–56). That fails on a 64-player server. Planets' three-tier `EditStore` fixes it and has to land with the planet milestone, not later.
- **P4: Draw recording.** About 2.5k terrain leaves plus about 180 hull bricks per nearby ship, at one draw each, is 3+ ms of command recording. GPU cull plus `DrawIndexedIndirectCount` is required before planet streaming is judged.
- **P5: Listen-server load.** 64 `CharacterVirtual` updates (2–3 ms), collision rings around every remote player anywhere in the system, and server-side bakes for everyone's hull edits all run on the host's gaming PC, in the same frame as rendering. Realistic listen-server cap is about 8 players; 32–64 means dedicated. The host should also share one chunk-generation cache between its render octree and its collision octree, so nothing is generated twice.
- **P6: Hull GPU memory.** 10–15 MB per 100 m ship at L0. Ten ships parked at a base are 150 MB on top of 50–85 MB of terrain. L0 must be capped by count (the N nearest ships), not only by distance.

**Correctness**
- **R7: Bubble merge vs closing speed.** Merge is checked at 10 Hz on a 500 m gap. At a 3 km/s closing speed, 300 m pass between checks, so collisions and docking approaches at speed are missed. Merge on predicted closest approach within 2 s, using rails and velocity, instead of on the current gap.

**Precision**
- **X1: Altitude approximation in planets.** The formula `w = a + (|l|² − a²)/(2·r_c)` is not "exact to µm". Its error is about `b²·a/(2·r_c²)`: 0.06 m at LOD 8, but about 275 m at LOD 12 (b ≈ 92 km, a ≈ 65 km). Neighbouring LODs then disagree, so cracks appear past what the skirts cover. Use exact `sqrt` per height-grid column for LOD ≥ 9; it costs very little.
- **X2: Hull rotation quantisation.** Smallest-three at 10 bits per component gives a step of about 0.0014, roughly 0.08°. On a brush with a 256 m half-extent the edge moves up to 0.35 m, more than one 0.25 m cell, so plates snapped to the same angle leave seams. Use 15 bits per component (48 bits, +2 B per brush), or store snapped angles as a snap index.
- **X3: Bake determinism across peers.** Hulls claims "bit-identical on x64" for its SIMD bake, but `Vector<T>` width differs between AVX2 and AVX-512 machines, which changes reduction order. Don't rely on it: the server sends mass properties (about 60 B) with the blueprint, and clients use them for prediction.
- **X4: Time and float world coordinates in the ocean.** `CameraPos.w = (float)renderTime` (OceanRenderer.cs:946), and FFT tiling uses world `x0`. Inventory's fixes (wrap at 3000 s, `camera mod L` per cascade) are correct and are required from M1, not M2.
- **X5: Render interpolation must happen in double.** Ship grid poses, planet spin angle and the camera frame chain all need to be interpolated in double at render rate. Interpolating after the float cast puts ship-speed jitter on every interior.

**Hand-waved**
- The atmosphere is a single line (Hillaire LUTs).
- The impostor-to-octree swap is unspecified.
- How a ship's interior volume is produced is unspecified.
- Who owns a ship on the network is unspecified: pilot vs server, and handover when the pilot leaves the seat.
- How rails ships with occupants interact with bubbles is unspecified: a sleeping crew on a coasting ship.
- The cost of a single `PhysicsScene` is "~4 MB, to be measured".

## Missing

None of these exist in `src/` today. There is no character controller, no networking and no entity model (grep finds no `CharacterVirtual` or `RakNet` use). `CharacterVirtual` is in the bindings and `Brutal.RakNet` and `Brutal.ZLib` are in BRUTAL.

1. **Sim/client split.** A `Sim` assembly with no Vulkan reference: entities, frames, physics, terrain data and hull bake without meshing. A headless server executable. `GameApp` (38 constructor args) becomes a client shell around it. Every feature must be built behind this line, or networking becomes a rewrite.
2. **Entity model.** SoA entity table (`EntityId`, kind, `Placement`, owner, netId), spawn and despawn, and a seat-attach relation.
3. **Transport and replication.** RakNet session with join, input commands stamped with a tick, 30 Hz snapshots, a client interpolation buffer, reliable events (frame change, edit, blueprint), blueprint fetch by hash, and a two-process local test (`--host`, `--connect`).
4. **Character.** On foot with radial up, a jump and an interact ray. The interact ray uses a managed BVH because native CastRay is missing. Seat enter and exit. EVA in zero-g (a dynamic capsule with thrust).
5. **Flight model.**
   - Thruster components apply force at their mount, giving torque from the real centre of mass.
   - A flight computer: 6-DOF with flight assist (velocity hold, rotation damping) and a decoupled mode.
   - Fuel and power as a single scalar each.
   - Landing gear as convex feet with friction; suspension later.
6. **Atmosphere physics.** A per-body exponential density profile. Drag uses `½ρv²·Cd·A(dir)`, where `A` is three projected areas plus a centre of pressure computed from hull occupancy at bake time. Lift only from wing components, if the user wants wings. Re-entry heating later.
7. **Orbital UI.** Altitude, vertical and orbital speed, apoapsis and periapsis from rails, a target marker for the moon, and the SOI indicator. ImGui is enough.
8. **Save format.** Universe seed, `GeneratorVersion`, per-region op logs and snapshots, an entity table (frame, int64 position), blueprints by hash, and player state. Versioned and chunked. Writes are serialized on a job from a tick snapshot so autosave never hitches.
9. **Lighting per body.** The sun as a body, its direction moved into each body frame, and eclipse by the planet (a ray–sphere test). `SceneLighting` takes ownership of the sky from `OceanRenderer`.
10. **Impostor ↔ octree handoff and focus-body switching.** Needed so the moon becomes the focus body during approach.
11. **Profiling.** Zones, streaming, hull bake and network rows in `ProfilerPanel`, plus a two-client bench path. Without these, milestone budgets can't be checked.

**Round 6 note (2026-11-26).** Designed since this review, so no longer missing as designs (nothing is built): ship atmospheres (ship-atmosphere.md), AI ships and crew (ai-ships-and-crew.md), combat, shields and sensors (combat-and-sensors.md), factions, law, sieges and land (factions-and-law.md, which owns the shared `IncidentRecord`). Still missing: settlements and NPC population on planets; death, medical and respawn; missions and contracts; a character doc (vitals, suits and their penalties). Rings' unseen event placement is a rule now (`EventHideKm`), not a risk. X3 (the server sends mass properties) also covers bend events (D59).

## Questions for user

1. **Getting to the moon.** Real orbital transfer at real time, a quantum/jump drive (Star Citizen style), or a moon placed close? With 1 g on a 1000 km planet, low orbit is 3.1 km/s and a Hohmann transfer to a moon at 20,000 km takes a few hours. This decides time-warp policy, rails usage and render distances.
2. **Surface gravity at this scale.** 1 g on 1000 km (orbital speed 3.1 km/s, unrealistic density, Star Citizen-like), or physically scaled (~0.27 g, orbits at 1.6 km/s)?
3. **Planet rotation in the first playable.** On, with a 6 h day (290 m/s at the equator and fictitious forces from day one), or spin off until after the slice? Turning it off removes C1 and C3 from the slice entirely.
4. **Interior gravity plating.** Always on, so interiors are calm, or real inertial effects inside unpowered or damaged ships? This only affects grid bubbles (M8).
5. **Flight model.** Pure 6-DOF Newtonian with flight assist everywhere, or aerodynamic lift from wing parts in atmosphere?
6. **Hosting.** Is about 8 players on a listen server, and dedicated servers for 32–64, acceptable?
7. **Building in the slice.** Free creative build, or survival with material cost and weld time? Is 0.25 m hull cells with a 0.5 m minimum wall OK?

## Milestones

Each milestone ends with a measured exit check. Nothing moves forward on estimates.

**M0: Cut and split.**
- Tag `island-final`.
- Inventory cut steps 1–5. Then 6a: point swash at the Snell phase plus the depth band, and add the depth cap. Run `--bench` A/B on the island so shore wash is proven to survive before the island is deleted. Then 6b.
- Create the `Sim` assembly boundary, with `PhysicsWorld` moving behind it.
- File the upstream BRUTAL asks: `MeshShapeSettings_Create_3`, `NarrowPhaseQuery_CastRay`, `PhysicsSystem_SaveState`.
- Exit: the island runs with shore wash, the GPU surf frame is about 0.11 ms, and the sim builds without Vulkan.

**M1: Big-world foundations.**
- `FrameTree` with Solar, planet inertial and surface frames, and a moon on Kepler rails driven by the integer tick.
- Double-precision `FlyCamera` in a frame, with a view matrix that has no translation.
- The per-draw instance record (C9).
- GPU cull and indirect-count terrain draws.
- Ocean time and anchor rebasing.
- Exit: fly from the surface of a smooth 1000 km sphere to 1e8 m with no jitter, and terrain draw recording under 0.1 ms.

**M2: Planet and moon terrain (render).**
- `PlanetGenerator` with double-lattice noise and exact altitude for LOD ≥ 9.
- Shell classification, height bounds, horizon pruning, selection on a job, the speed LOD bias, and `EditStore`.
- Impostors for every body, and focus-body switching.
- **M2b:** the ocean sphere, the clipmap in the tangent plane, a global `OceanSurface` (C5), and shore wash on `ShoreDepthGrid` from `IShoreDepth`.
- Exit: leaves per second and main-thread time per frame at 30, 300 and 3,000 m/s; orbit to ground with no holes; shore wash visible on a planet beach.

**M3: Physics scenes and walking.**
- `PhysicsRuntime` with shared pools, and `PhysicsScene` with settable gravity.
- A surface bubble with 32 m rebases, a `CharacterVirtual` with radial up, and anchor-driven collision rings (pooled, zero-alloc).
- Edits replayed from int64 positions.
- Exit: walk and dig 10 km from spawn. Measure collision chunk memory and build ms with the triangle-compound fallback (decides whether P1 blocks).

**M4: Two players on foot.**
- Listen server in process, plus a second process connecting over RakNet.
- Input commands, snapshots, interpolation, owned-character reconcile, edit replication, join with snapshots and ops.
- Save and load the world: seed, ops, players.
- Exit: two clients walk and dig together; bytes/s per client and reconcile error are logged.

**M5: Minimal hull.**
- `HullBrush` list with Box, Wedge and Cylinder brushes, Add and Subtract, surface nets first (dual contouring later).
- `MassMoments`, an exterior compound of greedy boxes, and components: seat, thruster, fuel tank, landing foot.
- Build mode in a ship-local frame; blueprint save and replication by hash.
- Defer rooms, damage, splitting, LOD and player-made components.
- Exit: build a 15 m craft in about 5 minutes; bake ms p50/p95 shown in the profiler.

**M6: Atmospheric flight and landing.**
- Thruster forces, the flight computer, drag from the occupancy areas, seats, and pilot ownership.
- The client predicts its own ship in a small Jolt scene (C12).
- Collision anchors along the flight path.
- Exit: take off, fly 50 km at 300 m/s, land on a slope, with a passenger in a second seat watching it smoothly.

**M7: Orbit and the moon.**
- Rotating ↔ inertial transfers with fictitious forces applied.
- Free-fall bubbles, rails when a ship leaves every bubble, SOI switch with 4 % hysteresis.
- Moon focus switch and the orbital HUD.
- Exit: both players fly to orbit, transfer, and land on the moon. Orbit elements stay unchanged across every frame transition (log the delta).

**M8: Walking inside a moving ship.**
- Grid bubble, `GridVolume` and door triggers (C11), kinematic proxies at doors, plating gravity.
- After this, the hull features from the hulls design: rooms, damage, LOD, dual contouring.
