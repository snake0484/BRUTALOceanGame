# Pivot: interplanetary space ship game

Status: design, nothing built. Written 2026-11-25 from a design pass over the current code (island + ocean game on `voxel-water-3d`, last commit `3a31ae6`).

The game becomes an interplanetary ship game in the vein of Star Citizen / Space Engineers. The island ocean game is retired; its terrain, renderer, job and physics foundations carry over.

## Decided

| Topic | Decision |
| --- | --- |
| Players | Single player works. Multiplayer 32-64 on dedicated servers, ~8 on a listen server. Server-authoritative; single player is an in-process listen server. |
| Planets | ~1000 km radius, 1 g, rotating. Several star systems, linked by natural wormholes. Bodies on rails with spheres of influence; game time stops while a server is empty. Go straight to the sphere: the island is retired, not kept as a test world. Chunked voxel terrain stored as now (edits only, regenerated from seed), generated as a sphere. |
| Physics zones | Required. Frame hierarchy solar > planetary > ship grids. Warp exit is where a ship picks its zone; zones merge when they get close. |
| Travel | Two modes. Roles (D23): **natural wormholes are the cheapest** travel (free to cross; you go where and when they go), **manufactured wormholes are the fastest**, and **warp** is for going somewhere specific, relatively close by, cheaply. Star systems are linked only by natural wormhole pairs; there is no cross-system warp, and manufactured wormholes stay within one system. **Warp:** takes travel time; a smooth server-computed path round bodies (D15), no forced exit floor (D16), a civilian arrival beacon and a real-space last 2 s (D18). **Wormholes:** power + exotic matter, instant (or a short tunnel) once open, the fleet option; natural (seeded, huge, noise signature; gate frames can be built round them) and player-made; two-way while open, but a flow can bias or block one direction; irrevocable once paid for; last a set time, then flush out both ways as they collapse. Catchers can be station gates or traffic control that assigns exit slots. Wormholes and catchers carry faction signatures that can be copied and spoofed. A captured wormhole opens visibly onto the captured destination; interdiction never forces travel, only tractor beams, flow or other physics do. Far ends open anywhere a suppression field (bodies, atmospheres, terrain, hulls, warp drives) allows. Interdiction costs power (wormholes: far more, plus exotic matter) and works through catchers: cheap anchors that bend planned paths to them, possibly somewhere deadly such as a solar flare's path. |
| Ships | Hull chunks are 3D modelled (D11: full mesh editing as a mesh brush, still cuttable); interiors are cut away from them. Components are prefab (bought) or player-made (function from a part graph); a prefab is a saved player build. Plating gives floor gravity and a separate inertial dampener (EM + energy, up to its rating) cancels manoeuvres (D9). 6-DOF with flight assist plus aerodynamic lift in atmosphere (D10). Deliberate runaway override, ejectable reactor cores (D12, D13). Several warp drives support each other, weighted by delivered power (D14). Components are wired with two media, power and data; a data cable is one-way and can be split to any number of inputs (D71, which drops D17's third medium). What can cancel or control what depends on the wiring, and wires can be cut (D17). Hull damage has three forms: holes, dents and deformation (D59). No weld minigame: a weld takes a set time and its quality follows the welder's skill (D67). No fleet discount; fleets fly their faction's ships (D68). The standalone designer is bundled with the game (D69). About 70 minutes solo for a 15 m ship with basic tools (D70). |
| Ship atmosphere | Ships hold air; hull damage leaks it; airlocks are needed (D38-D40). Air barriers need constant power and have no size cap (D41, D61). Emergency breach shields run on a battery, hold for a time set by the breach size and do not stop bodies (D42, D62, D63). Crews wear normal clothes; restrictive EVA suits come from suit lockers by airlocks, hangars and crew rooms (D64). Air is used at real rates (D65). Warp space is real space: a holed ship vents in warp (D66). No sea-water flooding of ships; terrain around water is solid (D60). |
| Combat, shields, sensors | Ships get three kinds of shield: particle, energy and gravity (D55). Weapon stats, sensor kinds and ranges are designed in combat-and-sensors.md (D56). |
| Factions and law | Factions fight wars outside the players' view, but things are mostly stable (D54). Fines, crime status, stolen hulls, sieges and land are designed in factions-and-law.md (D57). A warp exit used as a weapon is definitely illegal, and planetary or city shielding may prevent the damage (D58). Factions control most planets: tunnelling vehicles and cave drills are allowed but regulated, on land you own or have approval for (D74). |
| AI ships and crew | AI ships exist; crew are not fully simulated until needed; windows show crew; player ships can have AI crew; crew can be scanned; people can surrender; 200 hulls rendered at once and at most 100 crew per ship (D43-D51). The pilot matters: flying styles and skill (D53). |
| Asteroids, round 6 | Others may heat, wedge or shoot a rock with people in its caves, but injuring or killing players or NPCs that way is illegal (D72). Replenishment events are modest, such as asteroid captures (D73). |
| Building the game | Sessions and agents doing implementation work may build and test (D52). |
| Stations, tractors, PvP | Station anchors on any grid, activated at the required position and velocity, crawl slowly over days; a dropped station is a rare disaster, so stations carry thrusters and escape pods (D25). Stations have **shields** (capacity + recharge; a big hit or sustained fire gets through) and **debris deflection** by station tractors and arms; debris in a protected zone stays real (D34, D35). **Tractor beams are a weapon unless requested** (D26: consent flag on a docking or tow request; a station docks you only if you ask). Tractors **without antimatter only pull** (D28), so only AM-fed station emitters deflect debris. **Station sieges** are allowed and factions prefer **capture** to destruction (D27: `CommandCore` override; destruction possible but hard). A deliberate damaging shatter or thrown rock is a hostile act of its owner (D36, one shared `IncidentRecord`, owned by factions-and-law.md); fragments from legal mining near a station are always the **miner's liability** (D33). A threshold shatter breaks the whole body into fragments and a dust cloud (D37). |
| Water | Killed except shore wash. Keep analytic swell, GPU FFT detail, ocean shading and the GPU swash, all on a sea sphere. |
| Jolt precision | `Brutal.Jolt` is single precision (`RVec3` binds as `float3`). World positions are `double3` per frame on our side; Jolt runs in local float bubbles. No double-precision Jolt build for now. |

## Documents

| Doc | What it holds |
| --- | --- |
| [decisions.md](decisions.md) | User decisions D9-D74 (round 5, the r6 pass, ship atmospheres, AI ships and crew, round 6). These win over every doc. |
| [inventory.md](inventory.md) | Keep / adapt / cut per file, what shore wash needs, float hazards, ordered cut list with measured time recovered. |
| [frames-and-zones.md](frames-and-zones.md) | `FrameTree` (one Solar root per system, Lagrange frames), Kepler rails, patched conics, pooled Jolt scenes ("bubbles"), enter/leave rules, predictive merge and split, transfers, camera-relative rendering, networking. Owns the shared enums (`FrameKind`, `PlacementMode`, `BubbleAnchor`, `CelestialKind`). |
| [warp.md](warp.md) | Warp drive: states, smooth closed-form path round bodies (D15), exit-point selection as zone selection (bubble picked at arrival), merge on predicted closest approach, interdiction fields, fleet warp. Round 5: the last 2 s in real space as a ship-shaped projectile, the civilian arrival beacon (D18), no exit floor (low exits make a `WarpBlast` pressure wave, fined, D16), linked drives in one `WarpDriveSet` (D14), cancels through the ship's wiring (D17). Round 6: the warp bubble is real space and seals nothing (D66; a `WarpShell` pocket round the hull), raised region shields are exit exclusions and a blast aimed at people is `WarpAttack` (D58), drive ports are power and data (D71), a plot-style hook for AI pilots (D53). |
| [wormholes.md](wormholes.md) | Wormholes (revision 3: instant vs tunnel classes, stability reserve and energy fight, tunnel frame with current, eddies, wall discharge and ram ejection, retaliation, collapse flow, re-establish; round 5: proposals accepted (D19), natural cheapest and manufactured fastest (D23), the tractor consent rule (D26), owner of tunnel retaliation): plan, charge, commit, open, collapse flush; mouth pairs; flow; suppression field; catchers (gate, interdict, traffic control); signatures and spoofing; natural wormholes and frames; solar flares; seeing through the mouth. |
| [exotic-matter.md](exotic-matter.md) | Exotic matter and antimatter: sources (black holes, deposits), refining, grades, warp fuel mix, anti-gravitons, plasma thrust, anti-graviton bombs, black holes collapsing to rocky planets. Revision 2 adds slurry vs recirculating drives, runaway bursts, antimatter production and radiation, pirate kit, station anchors. Revision 3: crystal decay floor (0.96), purity cap 0.995, seven wormhole additive families, stabilisers, bombs drain wormhole stability. Revision 5 (round 5): the inertial dampener runtime (D9), core ejection and the runaway override (D12, D13), linked drives (D14), long-lived black holes and deliberate collapse (D21), EM mostly from planets and asteroids (D22), lingering radiation (D24), anchors on any grid with crawl and an overload alarm (D25, D27). |
| [rings-and-asteroids.md](rings-and-asteroids.md) | Planetary rings (mesh, haze, asteroids; band density tied to colour, dust as fog) and asteroid fracture mining: laser heat, wedge, violent breakup, weapon shatter, EVA sampling and cave mining, tumbling and tractored rocks, permanent removal with replenishment events. Revision 4: station shields, debris deflection by station tractors and arms, hostile-act attribution, whole-body shatter with a dust cloud (D34-D37). Round 6: cavers and other parties (D72), modest replenishment (captures and minor collisions, D73), cave drills and tunnelling vehicles (D74). Keeps the asteroid side of incidents (tracing, classes, D33 liability); the `IncidentRecord` itself and sieges are factions-and-law.md's, and weapon numbers and sensors are combat-and-sensors.md's. |
| [tractor-beams.md](tractor-beams.md) | Tractor beams (push/pull cone, adjustable FOV, EM + power; push needs AM, without it a `Graviton` drive only pulls, D28) and the hull stress model that lets a wrong lock rip a part off and opposing beams tear a ship apart (Haul and Hold locks, 4 m node graph with mount and plane cuts, solved only for loaded ships). Revision 2: station debris defence (AM-fed emitters only). r6: owns the consent rule (D26: a weapon unless requested), tunnel strikes via wormholes.md. r7: cuts yield and bend before they tear (D59); sieges and capture are factions-and-law.md's. |
| [ship-design-and-construction.md](ship-design-and-construction.md) | Ship designer app (standalone and in game, one core library), material > part > component > hull/armour > ship hierarchy, plans, cost per redefined level, buying vs building, cutting stock, construction steps (weld, bolt, rivet), assembly order. Projects: `src/Plans` (headless: validate, cost, order), `src/Designer`, `src/Designer.Ui`, `src/Designer.App`. Round 5: wiring (D17), aero parts (D10), dampener coils (D9), ejectable cores (D13), linked drives (D14), station pods, thrusters and anchors (D25), the `CommandCore` (D27). Round 6: wiring is power and one-way data (D71), set weld times with quality from skill tier (D67), no fleet discount (D68), bundled designer (D69), suit lockers (D64), shield, weapon and sensor families (D55), repair steps for dents and bends (D59). |
| [ship-atmosphere.md](ship-atmosphere.md) | Ship atmospheres: gas per room, flow through doors, ducts and breaches (event-driven, exact in mass), decompression, bulkhead doors, airlocks, air barriers (plasma window, constant power, no size cap), battery-fed emergency breach shields, life support at real rates, normal clothes by default with suit lockers and hoods, venting in warp (D38-D42, D60-D66). |
| [combat-and-sensors.md](combat-and-sensors.md) | Combat, shields and sensors (D55, D56, D59). Owns weapon stats (kinetic, energy, antimatter delivery, missiles, point defence), the hit law and the three damage forms (holes, dents, deformation) with repair, the three ship shields (particle, energy, gravity) and how weapons meet station and region shields, sensor kinds, signatures, detection ranges and contact-gated replication, combat across bubbles (rounds in frame space, rails combatants), salvo battles and combat ratings. |
| [factions-and-law.md](factions-and-law.md) | Factions and law (D54, D57, D58, D68, D72, D74). Owns `OwnerId`, factions, relations, standing, the shared `IncidentRecord`, evidence, offences, fines, restitution, heat (crime status), enforcement and bounties, land and mining rights, region (town, city, planetary) shields, off-screen wars as a strategic step, the `ZonePermission` hook, sieges and capture (replacing the interim rules), stolen hulls and title, salvage, surrender and captives. |
| [ai-ships-and-crew.md](ai-ships-and-crew.md) | AI ships and crew: four ship levels (schedule row, rails, simulated, interior), ship AI feeding the flight computer, crew as manifest / ambient figures / full characters with one identity, crew seen through windows from a shared schedule (zero bandwidth), stations, AI traffic moving real stock. Round 6 (revised separately): pilots differ by style and skill (D53); combat-and-sensors.md adds a rails-combatant level between rails and simulated; factions-and-law.md replaces its interim stolen-hull rule and owns off-screen wars. |
| [planets.md](planets.md) | Includes a Gas giants section (no surface, scooping, crush depth, storms, ring host). Round 5: relief and deep heat (D29), unminable shores and seabeds (D30), ported weather (D31), permanent edits (D32). Cartesian voxels in the rotating planet frame, surface shell, one octree to LOD 12 then an impostor, streaming at flight speed, memory, sea sphere. |
| [ship-hulls.md](ship-hulls.md) | Blueprint = CSG brush list (plus mesh brushes, D11), baked to 0.25 m SDF bricks, dual contouring, mass, rooms, collision, damage, LOD, build UX. Round 6: holes are subtract brushes, dents and bends are a displacement map outside the bake (D59); sockets are power, data in, data out (D71). |
| [review.md](review.md) | Cross-check of the four designs: contradictions with rulings, risks, missing systems, milestones M0-M8. |

The section docs were written independently. **Where they disagree, decisions.md wins, then review.md's rulings, then the owning doc.** The review.md rulings that change a section:

- **C1, C2:** in the rotating surface frame apply centrifugal and Coriolis to dynamic bodies, and set gravity per scene with a per-awake-body delta. Planets.md's "ignore fictitious forces" and "AddForce gravity on every body" are overruled (the latter wakes every body every tick).
- **C5:** `OceanSurface.Sample(double3 planetLocal, long tick)` has one global definition (fixed ~64 km tangent tiles, blended), so server and clients agree on buoyancy. The camera-window grid is a GPU cache only.
- **C7:** buoyancy reads `IShoreDepth` directly; a dedicated server has no camera window.
- **C8:** indirect terrain draws do not exist today (one `DrawIndexed` per chunk). They must be built.
- **C9:** one per-draw instance record (`relOrigin`, `scale`, `quat`) for terrain and hulls; translation alone is not enough.
- **C11:** a separate `GridVolume` (air with all portals closed) decides who is in a ship grid; door faces are entry triggers.
- **C12:** a client predicts its own ship by re-stepping a small local Jolt scene, not a second flight integrator.
- **C13:** every persistent position (edits, entities, saves, wire) is int64 at 1/1024 m in its frame; double only at runtime.

## Big risks

- **Jolt native lacks `MeshShape`, `NarrowPhaseQuery_CastRay`, `PhysicsSystem_SaveState`.** Terrain collision today falls back to a compound of single triangles (`StaticMeshShape.cs`). Likely 150-300 KB per chunk; a dedicated server ring could hit 0.5-1 GB. Ask BRUTAL upstream to export these (separate proposal, no `BRUTAL/` edits). Measure in M3.
- **Listen server cap is about 8 players.** 64 character updates, collision rings around every remote player and server-side hull bakes don't fit alongside rendering on the host. 32-64 means dedicated.
- **Draw recording.** ~2.5k terrain leaves plus hull bricks at one draw each is 3+ ms. GPU cull + `DrawIndexedIndirectCount` comes before planet streaming is judged.
- **Allocations on streaming paths** (`StaticMeshShape` index copies, per-chunk managed `TriangleBvh`, `ChunkJob.SetOps` growth). Fix with the planet milestone.

## Missing systems (none exist today)

Sim/client split (a `Sim` assembly with no Vulkan, plus a headless server), entity model, RakNet transport and replication, character controller (radial up, EVA), flight model (thrusters at mounts, flight assist), atmosphere drag, orbital HUD, save format, per-body lighting and eclipses, impostor-to-octree handoff, settlements and NPC population on planets, death, medical and respawn, missions and contracts, a character doc (vitals, suits and their penalties, D64), profiler rows for all of it. Designed in round 6 and no longer missing as designs (still unbuilt): factions, law, sieges and capture (factions-and-law.md), sensors and combat (combat-and-sensors.md).

## Milestones

Each ends with a measured exit check (details in review.md).

| | Milestone | Exit check |
| --- | --- | --- |
| M0 | Tag `island-final`. Cut water down to the ocean core and shore wash shaders (inventory steps 1-6; the island itself is retired, no A/B on it), carve the `Sim` assembly, file the upstream Jolt asks. | Game starts on a flat placeholder; sim builds without Vulkan. Shore wash is proven on a planet beach in M2b. |
| M1 | `FrameTree`, rails, double camera with translation-free view, per-draw instance record, GPU-culled indirect terrain draws, ocean time rebasing. | Surface of a smooth 1000 km sphere to 1e8 m with float quantisation error <= ~2e-4 px; terrain draw recording < 0.1 ms. Measured (`--orbit-test` and `--orbit-test inertial`, 1600x900, ~4450 frames each, island terrain GPU-culled and drawn every frame out to the planet's SOI, ~5e7 m, and skipped beyond it): float quantisation jitter max 2.1e-4 px (limit 0.1); smoothness (second difference against poses evaluated analytically at render time) <= 2.4e-12 px, where tick poses would give 1.4 px; GPU marker (fragment readback) vs CPU prediction <= 2.8e-3 px in every decade (the 1/256 px subpixel snap), jitter as drawn <= 4.8e-3 px; impostor surface error <= 1.5e-5 of range; reverse-Z depth ok, no NaN/Inf. Terrain recording 0.024 ms (`--bench`). |
| M2 | `PlanetGenerator`, shell, horizon pruning, `EditStore`, impostors, focus-body switch. M2b: sea sphere, global `OceanSurface`, shore wash on a planet beach. | Leaves/s and main-thread ms at 30, 300, 3000 m/s; orbit to ground with no holes. |
| M3 | `PhysicsRuntime` + `PhysicsScene`, surface bubble with 32 m rebases, `CharacterVirtual` with radial up. | Walk and dig 10 km from spawn; collision chunk memory measured. |
| M4 | Two players on foot over RakNet, save/load. | Bytes/s per client and reconcile error logged. |
| M5 | Minimal hull: box/wedge/cylinder brushes, add/subtract, mass, convex exterior, seat/thruster/tank/foot. | 15 m craft built in ~5 min; bake p50/p95 in profiler. |
| M6 | Atmospheric flight and landing, client ship prediction. | Fly 50 km at 300 m/s and land on a slope with a passenger. |
| M7 | Orbit, warp, the moon. | Two players reach the moon; orbit elements unchanged across frame transitions. |
| M8 | Walking inside a moving ship: grid bubble, `GridVolume`, door proxies, plating gravity and the inertial dampener. Then rooms, damage, hull LOD. | |

Later milestones live in their docs: A0-A4 (ship-atmosphere.md), CS0-CS7 (combat-and-sensors.md, all after M8), F0-F5 (factions-and-law.md), and the ship-design, rings, exotic-matter and wormhole ladders.

### Carry-overs from M0 (2026-11-25)

M0 landed on `space-pivot` (2f70258..1df5d87). Review items left for the milestone that replaces the island placeholder:

- **M2b shore depth:** the GPU swell has no depth cap past the ~1 km bathymetry window (no far field since layer B was cut); the CPU cap costs ~0.56 us per sample with no edits and ~3.3 us with 1000 edits (`TerrainShoreDepth` rebuilds the island set and scans every edit). The planet `IShoreDepth` must bin edits and cache the generator. (M2 step 2: `PlanetShoreDepth` reads the generator, ignoring edits; the GPU has no depth map until M2b.)
- **M2 protected shores (D30):** `ShoreGuard` refuses whole digs within 16 m of ground below sea + 2 m. planets.md specifies `ShoreProtectHeight` 6 m, clamping removal to the generated surface, letting players remove what they placed, and a per-chunk column mask instead of ~800 generator calls per big brush.
- **M2 pit rule:** CPU uses one voxel below sea level, the GPU uses 2^lod m at coarser LODs; base both on the generated sea mask (planets.md).
- **M4 networking:** the swell carrier phase is now integrated per tick (no snapping), which makes it history-dependent; give it a closed form or put it in the sim snapshot before late join.
- **Debug paths:** OceanPanel and GameApp still write swell wind straight into the sim; route through sim commands. Refused digs have no feedback (show the count, then the D30 band overlay).
- **Docs:** docs/ocean-gpu-contract.md's Frame UBO section is stale.

### Carry-overs from M1 (2026-11-25)

- **Orbit test:** smoothness can only fail in the inertial variant and does not catch uneven render time (also log the second difference of RenderTime against wall-clock); the GPU marker probe counts occluded fragments (wrap it in an occlusion query).
- **Instance store:** the device-local instance store (8192) equals terrain's max handles, so a second GPU-culled user (hulls) would silently get nothing; size it for every user or fail loudly.
- **Frames:** `CelestialSystem.Read` leaves render poses at identity until `Evaluate` runs; grids must write `FrameTree.Render` once they exist (M3). SimWorld still regenerates the test system each launch (save/load in M4).
- **Ocean:** the foam lace now tiles every 182.9 m; check it from altitude with `--planet-fly` screenshot sheets.

### M2 step 2: planet streaming (2026-11-26)

The island is gone (`TerrainGenerator`, `TerrainParams`, `Coastline`, `IslandFrame`, `ShoreGuard`, `TerrainShoreDepth`; D30 is now `PlanetGenerator.IsFullyProtected` plus the clamped removal). `TerrainWorld` streams the test planet's octree in its rotating frame: 8 roots at LOD 15, leaves LOD 0..12, maps keyed by `ChunkCoord.Key` (ulong), selection on a job (`PlanetLeafSelector`: shell classify, horizon pruning nested parent to child with a 2 km + 2 s of travel margin, speed LOD bias with hysteresis), retire by walking the selection's split nodes. Empty leaves take no renderer slot; terrain indices are 16-bit (planets.md "Memory"); arenas 80 MB vertex + 64 MB index. Collision: LOD-0 leaves within 128 m of the camera, pooled `StaticMeshShape`s with growable buffers (review P2). The camera spawns on a coast found by `PlanetSpawn`.

**Physics bubble (interim):** one Jolt world in a surface bubble (`PhysicsBubble`) with its origin fixed on the sea sphere at the spawn, +Y radially up, scene gravity g + centrifugal there. Float precision falls off with distance from it (about 1 mm at 10 km); collision chunks are placed in it from planet-frame doubles. The sea frame and the weather plane share it. Rebasing in 32 m steps and several bubbles are M3.

Measured with `--planet-fly` (Release, RTX 4090, 1600x900; each leg starts settled; holes = frames where a desired leaf in view, not provably below the horizon or empty, has nothing loaded over it):

| Leg | Leaves loaded/s | Chunk jobs/s | Terrain main-thread ms p50 / p95 / p99 | Frame work ms p50 / p95 | Holes |
| --- | --- | --- | --- | --- | --- |
| 30 m/s at 30 m AGL | 140 | 152 | 0.12 / 0.16 / 0.38 | 0.36 / 0.50 | 0 of 2876 frames |
| 300 m/s at 100 m | 1371 | 1456 | 0.13 / 0.74 / 1.41 | 0.38 / 1.02 | 0 of 2864 |
| 3000 m/s at 300 m (finest LOD 3) | 2164 | 2164 | 0.02 / 0.24 / 0.29 | 0.34 / 0.58 | 0 of 2861 |
| Descent 800 km to 30 m in 60 s | 182 | 183 | 0.06 / 0.12 / 0.17 | 0.32 / 0.55 | 0 of 8559 |

Selection runs 1.5-2.5 ms on its worker (12-16k nodes). At the ground ~8,000 leaves are desired but only ~2,800 have geometry (81-83 MB of arena): the shell's conservative band keeps 2-3 chunks per column, most of them at LOD 5-7. Before the fixes above, the 32-bit index arena filled at the 300 m/s leg and streaming deadlocked (old leaves wait for new ones that cannot upload); a full arena now frees a few uncovered leaves per frame instead (`ForcedRetires`, 0 in every leg). Smoke bench (`--bench`, Debug, one view): skipping empty leaves and 16-bit indices took terrain GPU from 0.121 to 0.072 ms and the frame from 0.223 to 0.153 ms; at the final spawn view (the offshore-depth probe moved it) terrain is 0.108 ms and the frame 0.252 ms.

Left for later in M2 and beyond:
- **Shell tightness:** ~65% of leaves mesh empty (LOD 5-7 worst). Tighten `PlanetHeightBounds` or early-out in the job from the height grid before the volume fill.
- **Collision allocations:** pooled shapes grow their buffers on first use: 22 MB in the first 20 s of low flight, 3-5 MB a leg after; the managed BVH exists only because native ray casts are missing (review P1).
- **Spawn:** coasts are gentle everywhere (continent ramp), so the spawn beach is a wide flat with tide pools; a steeper coast needs the generator's coastal profile.
- **Collision anchors:** collision is still driven by the camera, not sim-owned anchors (planets.md "Collision"); the headless sim has no ground.

### M2 step 3: impostors and the detail ladder (2026-11-26)

Every body is drawn on stages 1-3 of the ladder (planets.md "Detail ladder") by `CelestialView` + `BodyRenderer`, inside the opaque pass after terrain, lit by the star through the frame tree's render poses, with eclipses by the two bodies that look largest from each (ray-sphere, penumbra from the star's size):

- **Point of light** (under 1.5 px, gone by 3 px): one instanced draw for all bodies; colour from mean albedo, Lambert phase and size, set on the CPU.
- **Disc** (to 48 px): the base sphere with albedo from the impostor mip the pixel asks for, antialiased limb, atmosphere rim. The star is an emissive disc.
- **Impostor** (relief fades in 48-96 px): a cube map per rocky body, R16 height + sRGB albedo (alpha = water), full mips, 256² per face at load and 1024² for the focus body on one worker. The heightfield is marched with steps bounded by the steepest baked slope, so mountains show on the limb. Normals come from four height taps, not a third texture.
- **Switch to the octree:** the octree streams below 1000 km (unloads above 1100 km). The impostor stays opaque in front until the octree has settled, fades out from 900 to 700 km, then draws behind the octree down to 100 km so gaps and the limb never show sky. Between 100 and 20 km the terrain's shading turns from the impostor's look (star only, same atmosphere code, `atmosphere.glsl`) into the ground's sky and fog.
- **Focus body:** the rocky body the camera is lowest over (0.8 hysteresis). A switch moves the free camera into that body's surface frame and `TerrainWorld.SetFocus` moves the octree once nothing is in flight. Each body has its own `PlanetParams`, height bounds, selector and edit replica; moons are airless and dry (`PlanetMaterials.Barren`). Collision and the ocean stay on the spawn planet (bubbles are M3).

`--moon-test` falls from 1e9 m onto the moon in 60 s. Measured (Release, RTX 4090, 1600x900, GPU ms p50 / max):

| Stage | Points | Discs | Impostors | Terrain |
| --- | --- | --- | --- | --- |
| Point (under 1.5 px) | 0.002 / 0.011 | 0.009 / 0.030 | 0 | 0 |
| Disc (3-48 px) | 0 / 0.007 | 0.009 / 0.032 | 0 / 0.012 | 0 |
| Relief fade-in (48-96 px) | 0 | 0 / 0.008 | 0.015 / 0.040 | 0 |
| Impostor (96 px to 900 km) | 0 | 0 | 0.031 / 0.103 | 0 / 0.045 |
| Switch band (900-700 km) | 0 | 0 | 0.089 / 0.342 | 0.040 / 0.169 |
| Octree with backdrop (700-100 km) | 0 | 0 | 0.024 / 0.302 | 0.043 / 0.390 |
| Octree, ground (under 100 km) | 0 | 0 | 0 / 0.245 | 0.051 / 0.363 |

Focus moved to the moon at 11,500 km; its 1024² impostor was on screen 1.6 s later (bake 1.63 s on one worker; the 256² bakes take 0.39 s at load); the octree settled at 963 km, before the switch band; no hole frame went uncovered. A 256² impostor is 3.1 MB with mips (not planets.md's 1.2 MB: albedo is RGBA8), the 1024² one 50 MB. `--orbit-test` passes in both variants with unchanged limits; its sphere now passes through the marker at the visible height there (999.968 km), since the impostor above the switch is that surface. `--planet-fly` is unchanged from step 2 (0 holes in every leg).

Left for later:
- **Focus impostor size:** 50 MB plus the same in staging while it uploads; BC4/BC1 or 512² would cut it.
- **Other bodies' ground:** no collision, ocean or brush off the spawn planet until bubbles (M3).
- **Gas giants and rings:** not on the ladder yet (planets.md "Gas giants").
- **Edits in the impostor:** it never sees them (planets.md allows baking large ones into the focus body's later).

## Open questions

Decisions so far are logged in [decisions.md](decisions.md) (D9-D74) and in the Decided table above. Remaining open questions live in each doc's own list; the index after item 34 says where.

Collected from all sections, duplicates merged.

1. ~~Surface gravity~~: decided, 1 g on 1000 km planets (low orbit 3.1 km/s).
2. ~~Planet rotation~~: decided, planets rotate (fictitious forces from day one). Stations can use an exotic-matter **station anchor** to hold position over a planet where no orbit is possible: a grid fixed in the rotating surface frame at any altitude, burning EM at a rate that grows with how far it is held from a natural orbit; anchor failure drops it back to physics. Refined by D25 (decisions.md): anchors work on any grid, activated only at the required position and (mostly) velocity; anchored stations crawl slowly over days; Lagrange points have their own frames; a dropped station is a rare disaster, so stations carry thrusters and escape pods.
3. ~~Interior gravity~~ (decided, decisions.md D9: plating plus a separate inertial dampener, EM + energy, up to a rating). Was: plating always on (calm interiors) or real inertial effects in unpowered or damaged ships?
4. ~~Flight model~~ (decided, D10: both). Was: pure 6-DOF with flight assist, or aerodynamic lift from wing parts in atmosphere too?
5. ~~Hosting~~: decided, ~8 on a listen server, 32-64 on dedicated.
6. ~~Building economy~~ (decided, ship-design-and-construction.md). Was: free creative building, or survival (material cost by volume, weld time in place, or print at a shipyard)?
7. ~~Hull detail~~ (decided, D11: full mesh editing, still cuttable). Was: 0.25 m cells with a 0.5 m minimum wall (finer detail in components)? Primitives + bevels enough, or lofted/spline hulls? CSG holes for damage, or dents too?
8. ~~Player-made components~~ (decided, ship-design-and-construction.md and ship-hulls.md open question 4: modelled housing + a function derived from the part graph, not from housing geometry). Was: modelled housing + bought/crafted function core, or function from geometry?
9. ~~Huge grids~~: left to engineering. Plan: one grid stays within a 4 km bubble (ships are capped at 1 km by ship-hulls.md); bigger stations are several linked grids; battles spread wider than 4 km run as several bubbles joined by predictive merge. Revisit double-precision Jolt only if that proves wrong.
10. ~~Orbits~~: decided, bodies on fixed rails with spheres of influence (patched conics). Stations may be placed where no orbit is possible, e.g. Lagrange points, held by a station anchor (exotic matter).
11. ~~Terrain~~ (decided, D29, D32: relief and depth fine, deep heat, edits permanent). Was: relief -8 km .. +9 km and digging 2 km down OK? Edits permanent forever, or regrow after N days unvisited?
12. ~~Water edge cases~~ (decided, D30, D31: shores and seabeds unminable, weather ported). Was: a hole dug below sea level at the shore floods instantly or stays dry? Weather ported to planets in the slice, or fixed wind per planet?
13. ~~Exotic matter source~~: decided, see [exotic-matter.md](exotic-matter.md) (black holes; impure deposits in asteroids and planets; refined by recrystallisation; low grade is warp fuel with antimatter; EM + antimatter make anti-gravitons).
14. ~~System scale~~: decided for now, realistic spacing; warp ~3.7 min per AU.
15. ~~Other star systems~~: decided, natural wormholes lead to other systems (several `Solar` roots; no cross-system warp, manufactured wormholes stay in one system; shards cut by system, then by SOI subtree).
16. ~~Warp details~~ (decided, D15-D18). Was: do bodies block warp paths (plot via waypoints) or does the server route around them? Anything happening mid-warp? No exits inside an atmosphere or under 2 km above the highest terrain?
17. ~~Wormhole details~~ (decided, D19: proposals accepted) (wormholes.md, open questions 2-16): outflow push, counterplay to catchers, mouths on moving ships, how close big mouths may open, friendly fire, flow from power asymmetry, what path-pull readings reveal, generator spoofing, natural redirect reach, frame defence, fee currency, natural size, copy ageing.
18. ~~During the pivot~~: decided, go straight to the sphere. The island is not kept as a regression world; shore wash is proven on a planet beach (M2b) instead of on the island in M0.
19. ~~Game time while a server is empty~~: decided, it does not advance. Black-hole lifetimes, anchors, wormhole timers and regrowth count in played (server-running) time.
20. ~~Tractor beams in PvP~~ (decided, decisions.md D26, D28): a weapon unless requested (consent flag); without antimatter, pull only.
21. ~~Station attacks~~ (decided, D27, D34-D36): sieges allowed, capture preferred; shields and debris deflection; hostile-act attribution.
22. ~~Mining debris near stations~~ (decided, D33): always the miner's liability.
23. ~~Do ships get shields?~~ (decided, D55): particle, energy and gravity shields (combat-and-sensors.md). Was rings-and-asteroids.md question 12, second half.
24. ~~Weapon numbers and sensors~~ (decided, D56): combat-and-sensors.md owns them. Were rings-and-asteroids.md questions 5 and 9.
25. ~~Fines, crime, stolen hulls, sieges, land~~ (decided, D57): factions-and-law.md. Closes warp.md question 4 (blast fines), the interim siege rules and the AI doc's stolen-hull rule.
26. ~~Warp exits as a weapon~~ (decided, D58): illegal; region shields may prevent the damage. Was warp.md question 6.
27. ~~Dents as well as holes?~~ (decided, D59): holes, dents and deformation. Was ship-hulls.md question 3.
28. ~~Ship atmosphere questions 1-7~~ (decided, D60-D66): no sea flooding, no barrier size cap, battery-fed emergency shields that do not stop bodies, normal clothes and suit lockers, real air rates, breaches vent in warp.
29. ~~Construction questions~~ (decided, D67-D70): no weld minigame, no fleet discount, bundled designer, about 70 minutes for a 15 m ship. Were ship-design-and-construction.md questions 1-4.
30. ~~Wiring depth~~ (decided, D71): power and data; a data cable is one-way and can be split. Was ship-design-and-construction.md question 6.
31. ~~Cave miners and other parties~~ (decided, D72): physically allowed; hurting people is illegal. Was rings-and-asteroids.md question 3.
32. ~~Replenishment events~~ (decided, D73): modest. Was rings-and-asteroids.md question 7.
33. ~~Cave vehicles~~ (decided, D74): allowed but regulated; own the land or have approval. Was rings-and-asteroids.md question 11.
34. ~~Off-screen conflict, surrender, does the pilot matter~~ (decided, D54, D49, D53). Were AI doc questions.

Still open, by doc (numbers are each doc's own):
- **combat-and-sensors.md:** 1 pace of fights, 2 the eyeball rule (a dark ship can appear at 2 km), 3 gravity shields against ships and boarders, 4 the role of lasers.
- **factions-and-law.md:** 1 player-founded factions, 2 sieges on offline owners, 3 undiggable settlement ground, 4 captive players' opt-out, 5 faction count and names.
- **warp.md:** 1 PvP radii, spool cancel by damage, interdiction scope; 2 real-space segment numbers; 3 beacon reach; 5 submerged exits; 7 the bubble wall (lost, or left behind); 8 hull riders and boarding in warp.
- **ship-atmosphere.md:** 8 barrier ride-through, 9 charging a breach-shield battery during a hold, 10 suit numbers.
- **ship-design-and-construction.md:** 5 dampener overlap, 7 skill tier numbers and the starting tier.
- **ship-hulls.md:** 1 hull resolution, and the 0.125 m sub-question under 2.
- **rings-and-asteroids.md:** 1, 2, 4, 6, 8, 10, 12 (station shield spread), 13 (class distances), 14 (cave drill numbers).
- **planets.md:** 1 day length.
- **exotic-matter.md, wormholes.md, ai-ships-and-crew.md:** their own lists (not revised in round 6, or revised separately).
