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
| [planet-kinds.md](planet-kinds.md) | Planet kinds: per-body generator recipes as data (stacks of feature layers: ridged mountains, mesas, canyons, craters, volcanoes, dunes, ice), full 3D layers (caves, arches, overhangs), bounds and per-LOD band-limits, costs, a starting catalogue. |
| [terrain-decoration.md](terrain-decoration.md) | Generated decoration: hashed scatter with acceptance rules, ground cover / props / large features / points of interest, LOD to impostors and the planet's far colour, GPU-driven rendering, harvesting, only deltas stored. |
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
| M2 | `PlanetGenerator`, shell, horizon pruning, `EditStore`, impostors, focus-body switch. M2b: sea sphere, global `OceanSurface`, shore wash on a planet beach. | Leaves/s and main-thread ms at 30, 300, 3000 m/s; orbit to ground with no holes. Measured 2026-11-26 at 93ac548 (`--planet-fly`, Release, 8 cores, RTX 4090, 1600x900, two runs, ~2,870 frames a leg; table and what is not met under "Carry-overs from M2"): leaves loaded/s 140 / 1371 / 2164 at 30 / 300 / 3000 m/s and 182 on the 800 km descent; terrain main-thread ms p50 / p95 0.12 / 0.16, 0.13 / 0.76, 0.02-0.11 / 0.23 and 0.05 / 0.12; frame work p50 0.29-0.37 ms. No leaf missing in any frame of any leg on 8 or 6 cores (0 of ~17,200 frames a run). At 93ac548 settled views from 100 to 5 km showed LOD seam cracks (up to 581 px a frame), which the hole counter did not see. **Fixed 2026-11-26 at 0b260e7** (skirt flanges, sea mask in terrain shading; details under "Carry-overs from M2"): the new pixel probe (`--seam-test`) counts 0 crack px at 100 / 50 / 20 / 5 km (42 / 43 / 86 / 15 before), `--planet-fly` on 8 cores still has 0 holes in every leg with the hole counter now requiring full cover, and leaves/s are unchanged (140 / 1371 / 2163 / 182). **Still not met:** pinned to 4 cores the 3000 m/s leg had a leaf missing in 2651 of 2878 frames (not re-measured). Shore wash is visible and moving on the spawn beach (`--shore-fly`). `--orbit-test` passes in both variants (jitter <= 2.5e-4 px, GPU against CPU <= 2.8e-3 px), `--moon-test` passes, and Debug runs of all four with validation log nothing. |
| M2k | Planet kinds, slice 1 ([planet-kinds.md](planet-kinds.md), [brief](briefs/planet-kinds-brief.md)): kinds as data, the generic layer evaluator, crater stamps and ergs, three kinds, row SIMD. | Slope and relief per kind against the brief's targets, lattice ns/point per LOD against 150, no holes over steep ground. Measured 2026-11-26 after the review pass (2646fb5; tables under "Planet kinds slice 1"): temperate 100 m slope p50 / p90 / p99 8.4 / 26.3 / 41.3 deg (target 9 / 28 / 42; v1 2.1 / 5.5 / 8.5), lattice 133-180 ns/point (v1 recipe 50-62), 0 holes over steep ground on all three kinds at 30 / 300 / 3000 m/s and on the descent, 68-72 % of desired leaves mesh empty (79-80 % before the pass, v1 66 %), 0 crack px, desert canyon rims clean in close shots (dcc64a2), selection 0.4-1.5 ms p50 on its worker in flight (e28eed4; 10-15 ms the first pass). **Not met:** temperate (LOD 0 to 8) and desert (LOD 8) are over 150 ns/point, to 180; `Height`, `Bounds` and a `Classify` nobody remembers (1.5-2.3 us). |
| M3 | `PhysicsRuntime` + `PhysicsScene`, surface bubble with 32 m rebases, `CharacterVirtual` with radial up. | Walk and dig 10 km from spawn; collision chunk memory measured. |
| M4 | Two players on foot over RakNet, save/load. | Bytes/s per client and reconcile error logged. |
| M5 | Minimal hull: box/wedge/cylinder brushes, add/subtract, mass, convex exterior, seat/thruster/tank/foot. | 15 m craft built in ~5 min; bake p50/p95 in profiler. |
| M6 | Atmospheric flight and landing, client ship prediction. | Fly 50 km at 300 m/s and land on a slope with a passenger. |
| M7 | Orbit, warp, the moon. | Two players reach the moon; orbit elements unchanged across frame transitions. |
| M8 | Walking inside a moving ship: grid bubble, `GridVolume`, door proxies, plating gravity and the inertial dampener. Then rooms, damage, hull LOD. | |

Later milestones live in their docs: A0-A4 (ship-atmosphere.md), CS0-CS7 (combat-and-sensors.md, all after M8), F0-F5 (factions-and-law.md), and the ship-design, rings, exotic-matter and wormhole ladders.

### Carry-overs from M0 (2026-11-25)

M0 landed on `space-pivot` (2f70258..1df5d87). Review items left for the milestone that replaces the island placeholder:

- **M2b shore depth:** the GPU swell has no depth cap past the ~1 km bathymetry window (no far field since layer B was cut); the CPU cap costs ~0.56 us per sample with no edits and ~3.3 us with 1000 edits (`TerrainShoreDepth` rebuilds the island set and scans every edit). The planet `IShoreDepth` must bin edits and cache the generator. (M2 step 2: `PlanetShoreDepth` reads the generator, ignoring edits; the GPU has no depth map until M2b.) **Done in M2 step 4:** `PlanetShore`, 0.37 us with no edits and 0.39-0.58 us with 1000; the far depth map caps the swell past the near window.
- **M2 protected shores (D30):** `ShoreGuard` refuses whole digs within 16 m of ground below sea + 2 m. planets.md specifies `ShoreProtectHeight` 6 m, clamping removal to the generated surface, letting players remove what they placed, and a per-chunk column mask instead of ~800 generator calls per big brush.
- **M2 pit rule:** CPU uses one voxel below sea level, the GPU uses 2^lod m at coarser LODs; base both on the generated sea mask (planets.md). **Done in M2 step 4:** both read `PlanetShore`, where digging never lowers the ground below the generated surface.
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

### M2 step 4: sea sphere and shore wash, M2b (2026-11-26)

The sea is the planet's, not the spawn bubble's (planets.md "Ocean and shore wash on a sphere" has the design as built):

- **One global sea (review C5).** `OceanSurface.Sample(double3 planetLocal, long tick)` lays the swell on the sea sphere by tile (`SeaTiles`: 49 rows of latitude, 3,058 tiles of ~64 km on the test planet, blended over 2 km either side of an edge with squared weights summing to 1). Rows and cells, not the cube sphere review C5 gave as an example: cube-sphere tile edges kink by up to 45° at cube edges, which the GPU's one linear blend coordinate per edge cannot follow; parallels and meridians can. Swell directions are now from east toward south everywhere (`--swell`'s default 0 runs east, onto the spawn's beach). The GPU's low band mirrors it through `SeaSlots` (a ported copy of `swellAt` agrees with the CPU to under 1 mm within 300 m of the camera, in a tile, on edges, at a corner, at the seam of longitude and by a polar cap).
- **Depth (review C7, D30).** `PlanetShore`: sea level minus the generated ground raised by placed fill; edits that reach the sea are binned on the sea sphere (two hash lookups a query, lock-free for readers, one writer). Digging only takes fill back to the generated ground, so the generated sea mask is the pit rule for CPU and GPU alike. The sim files edits into it, so a headless host caps the swell the same way.
- **GPU cache.** `ShoreDepthGrid`: a 1 km near window (512² × 2 m, toroidal) that follows the camera, and a 24.6 km far map (512² × 48 m) per sea frame for the swell's depth cap beyond it; jobs fill them, the main thread only swaps and uploads changed rectangles. The surf foam map is toroidal with the near window, so foam no longer clears when the window moves.
- **Drawing.** The clipmap lives in a sea frame under the camera (moved every 3 km, baked in the background, swapped whole), bent onto the sphere; 7 shell levels carry the far sea to the horizon, shaded only; from altitude the fine levels are skipped and the surface fades into the terrain's far sea by ~60 km. `OceanRenderer` no longer needs the camera near the spawn.

Measured (RTX 4090, 1600x900):

| What | Number |
| --- | --- |
| `PlanetShore.Depth` (Release) | 367 ns with no edits; 389 ns with 1000 edits along 4 km of coast nearby; 582 ns with 1000 edits among the samples (island: 560 ns and 3,300 ns) |
| `OceanSurface.Sample`, 152 components (Release) | 5.6 us inside a tile, 10.1 us in a two-tile blend, 15.3 us at a corner (12% of the sea is blended); the depth cap adds one `PlanetShore.Depth` in open water and on land, five within 4 m of depth of the capped band |
| Depth jobs (Release) | 13 ms of one worker per 512 × 64 cell band (0.4 us a cell); far map 85-100 ms worker; a sea frame move (far map + near window) takes about 0.2 s of the one worker an 8-core machine gives it; the main thread only copies changed rectangles to staging, at most 0.19 ms |
| GPU, `--bench` at the spawn view (Debug, alternating with the build before this step) | Ocean surface 0.052-0.061 ms against 0.040-0.053 ms: runs drifted by 25% that day, so by its ratio to the unchanged terrain scope, 0.59-0.64 against 0.55-0.59, about 8% more. Surf foam 0.004 ms, FFT unchanged |
| GPU, `--bench` low over the beach (`--cam 30 6 0 125 -12`) | Ocean surface 0.061-0.068 ms against 0.054-0.056 ms before (which had no depth map, so no wash) |
| `--shore-fly` (Release, vsync on, so GPU numbers read high): standing off the beach, the waterline at 15 and 150 m/s, climb to 150 km | Ocean p50 0.113 / 0.123 / 0.104 / 0.092 ms; frame work p50 0.36-0.38 ms, p95 0.46-0.79 ms; 24 and 260 depth jobs in the two flights, one sea frame move, depth jobs pending in 12 and 148 of ~4,300 frames |

Shore wash on the spawn beach was checked in motion on 60-frame sheets at eye level (2.2 m) and from the `--shore-fly` waterline flight: the sheets run up and drain over several metres of the beach each wave and the edge moves smoothly frame to frame. The beach is a 1.5% slope (0.34 m deep 20 m out), so the wash is wide, thin and slow.

Left for later:
- **Surge front:** now and then a dark patch a metre or two across shows for a frame or two where a surge front crosses the see-through shallows (the mesh folding over itself and blending twice, as on the island; a flat beach makes the see-through band 40 m wide). Coarser clipmap levels are now set back a hair in depth so their one-cell overlap with the finer level can't do the same.
- **Sea's limb from 20-60 km:** the far shell still reflects the ground-level sky, a pale rim until the atmosphere LUTs replace the sky and fog terms.
- **Far map and edits:** the far map is generated ground only (a pier is invisible at 48 m cells); placed fill shows in the near window and in `PlanetShore`.
- **Buoyancy:** nothing floats yet; `OceanSurface.Sample` is ready for M3 bodies. The surf carrier is taken from the camera's tile, so swash timing steps when the camera crosses a tile edge (every ~64 km).
- **Weather on the sphere (D31):** the swell's wind is still sampled in the spawn bubble's plane and applied planet-wide.
- **Spawn beach:** still the wide flat of step 2; a steeper coast would show the wash better.

### Carry-overs from M2 (2026-11-26)

The exit check, measured at 93ac548 with `--planet-fly` (Release, Ryzen 7 9850X3D with 8 cores and 3 streaming workers, RTX 4090, 1600x900; two runs, the worse number of the two where they differ; each leg starts settled; a hole is a frame where a desired leaf in view, not provably below the horizon or empty, has nothing loaded over it):

| Leg | Leaves loaded/s | Terrain main-thread ms p50 / p95 / p99 / max | Frame work ms p50 / p95 / max | Holes, 8 cores | Holes, 6 cores | Holes, 4 cores |
| --- | --- | --- | --- | --- | --- | --- |
| 30 m/s at 30 m AGL | 140 | 0.12 / 0.16 / 0.36 / 4.00 | 0.36 / 0.60 / 4.52 | 0 of 2839 frames | 0 of 2879 | 0 of 2879 |
| 300 m/s at 100 m | 1371 | 0.13 / 0.76 / 1.38 / 1.99 | 0.37 / 1.04 / 3.54 | 0 of 2874 | 0 of 2879 | 0 of 2879 (1001 leaves/s) |
| 3000 m/s at 300 m (finest LOD 3) | 2164 | 0.02-0.11 / 0.23 / 0.27 / 0.37 | 0.37 / 0.54 / 1.46 | 0 of 2874 | 0 of 2879 | **2651 of 2878** (1363 leaves/s, up to 4 leaves, LOD 10-11) |
| Descent 800 km to 30 m in 60 s | 182 | 0.05 / 0.12 / 0.15 / 0.44 | 0.32 / 0.41 / 14.96 | 0 of 8624 | 0 of 8626 | 0 of 8635 |

The 6 and 4 core columns are the same build pinned by process affinity (2 and 1 streaming workers, `WorkerBudget`). No gen-0 collection in any leg. `--orbit-test` passes in the surface and inertial frames (float jitter 2.2e-4 and 2.5e-4 px, GPU marker against CPU 2.8e-3 px, smoothness <= 3.8e-11 px); `--moon-test` passes (15 hole frames above 900 km, all behind the impostor; 0 below). Debug with validation: `--planet-fly`, both `--orbit-test`s, `--shore-fly` and `--moon-test` log no validation message. Shore wash (`--shore-fly`, standing off the spawn beach): the dry sand in a fixed crop of eight frames about 1 s apart shrank from 30.7% to 22.8% as one sheet ran up; ocean GPU p50 0.149 ms standing, 0.111 and 0.096 ms along the waterline at 15 and 150 m/s (vsync on).

Not met, or found while measuring:

- **Seam cracks: fixed 2026-11-26 (ebfaa5a, f7e0827).** Cause: a coarse leaf on the minus side of a finer one stops about half a fine voxel before the fine mesh begins, and skirts hanging straight down left that slot open to a view along it; the dark colour was those skirts shaded as sea bed, and past the depth maps the ocean surface drawn over them. Skirts now also lean one voxel outward along the surface (a flange, mitred per vertex), under-water shading follows the generated sea mask (a vertex bit), and land skirts stay above the sea sphere. Measured: `LodSeamTests` (rays at the seam strip of a coarse and a fine neighbour mesh, back faces culled, six LOD pairs) plan rays through 26-109 of 264-828 with the coarse leaf on the minus side (gap up to 1.25 fine voxels) to 0, and 0 on the plus side before and after; `--seam-test` (the pixel probe: octree alone over a key clear colour, one captured frame per view, a test-only readback) 42 / 43 / 86 / 15 crack px at 100 / 50 / 20 / 5 km to 0 / 0 / 0 / 0; the same static views in the normal game against the 93ac548 captures, seam-line px 437 / 574 / 870 / 416 to 25 / 11 / 46 / 95, all of the rest on coastlines. Left: seabed skirts are still shaded by their own depth, so a faint line shows along seams over shallow sea where no ocean surface covers it (97 px at 5 km in the probe's view); a fine leaf standing more than about a fine voxel above its coarse neighbour would reopen a slot (none seen); the ocean surface itself still knows no land past its depth maps (12 km, or above 28 km), so ground dug below sea level out there would show it. What was measured at 93ac548: settled, static views looking inland at pitch -35° show dashed dark lines 1-3 px wide along chunk borders where LODs meet, with the sea colour behind them: 315 px at 100 km, 461 at 50 km, 581 at 20 km, 42 at 5 km, none at 300 km or 1 km (dark outliers against a 9 px median over 1.04 M land pixels). They also show over the sea at 50 km and in the descent captures between about 65 and 3 km. The hole counter counts missing leaves, so it reads 0; the exit check needs a pixel test as well (a test-only probe like the orbit marker, not a readback in the frame loop).
- **Hole counter: stricter since 2026-11-26 (4f82642).** A desired leaf used to count as covered if any one descendant was ready; it now needs drawn leaves, or provably empty nodes, over all of it. 8 cores, Release: still 0 holes of 2879 / 2879 / 2854 / 8629 frames. The 6 and 4 core columns above are from the old counter.
- **Deep and high edits: fixed 2026-11-26 (c9e5069), found in review.** Selection dropped split nodes the shell called uniform unless an op showed at their own LOD, so a 1.5 m dig below about -400 m or a place at +300 m never got its LOD-0 leaf (no mesh, no collision). `EditStore` now marks every octree node above an op and split nodes ask that; leaves keep the visibility rule. Tested at -500 m and +300 m: leaf selected, meshed, collision shape built. Left: horizon pruning still bounds a node by its generated ground, so a tower could be pruned at the horizon.
- **Pit rule in terrain shading: fixed 2026-11-26 (f7e0827), found in review.** `terrain.frag` treated all ground under the sea radius as under water; it now needs the generated sea mask too, so a pit dug inland below sea level is dry (test: 356 pit vertices under the sea radius, none in the mask).
- **Depth banding: fixed 2026-11-26 (0b260e7), found in review.** The float radius at 1e6 m stepped the under-water depth by up to 10.7 cm; the stable difference on the camera's own height is good to 0.005 cm.
- **Review findings still open:** no compaction, so edit replay grows with the ops in a chunk (0.036 ms per op in Debug); `EditStore.File` allocates per touched cell on the tick path; `TerrainWorld.AddEdit` walks every loaded leaf per op; above 384 m/s the speed LOD bias leaves no LOD-0 leaves, so no collision.
- **Hole-free at 3000 m/s needs two streaming workers.** One worker loads 1363 leaves/s against the 2164 the leg asks for, and it is the coarse leaves at the horizon (LOD 10-11) that stay missing, below 100 km where no impostor stands behind them. A Debug build on 8 cores (648 leaves/s, selection 12 ms) has holes in 2689 of 2877 frames there (up to 13 leaves, LOD 10-12) and in the last 46 frames of the 300 m/s leg. Minimum spec is not decided; either the load order or the worker budget has to cover a 4-core machine.
- **Relief not exercised.** Every leg flies over the plain inland of the spawn (a flat horizon in every capture), so leaves/s and arena numbers are for gentle ground.
- **Allocations.** 20-22 MB in the 30 m/s leg and 4-6 MB in the 300 m/s leg, on all threads (step 2's pooled collision shapes growing); 0 at 3000 m/s where no collision is built.
- **Spikes.** Frame work reaches 4.5 ms once in the 30 m/s leg in both runs (terrain main-thread max 4.0 and 1.8 ms), and 15 ms once in one descent (1.2 ms in the other).
- **Day and night.** From orbit the spawn sits at the terminator, so the descent starts nearly black down to about 130 km, yet the ground below 20 km is in full daylight: the ground's sky does not follow the star. Seaward, the sea is dark blue at 100 km, turquoise at 50 km and grey-blue at 20 km, with a pale band at the limb (step 4's "sea's limb").
- **Foam tiling (M1 carry-over, checked).** From 200 m over the shallows the foam flecks repeat on a visible square grid.
- The "Left for later" lists of steps 2 to 4 still stand; D30's column mask and the refused-dig feedback (carry-overs from M0) and D29's heat hook were not measured here.

### Planet kinds slice 1 (2026-11-26)

Each body runs its own generator recipe: `data/planets/kinds/<id>.json` compiled per body into a `PlanetKindProgram` (`src/Terrain/Planet/Kinds/`) that `PlanetSurface` runs over rows of lattice points. `GeneratorVersion` is 2. The test system's planet is `temperate-continental`, its moon `barren-cratered` (`SimWorld.KindOf`); `legacy-rolling` reproduces v1 heights and chunk bytes bit for bit (golden hashes). Viewer: `--planet-kind <id> [--kind-seed N]` swaps the planet's and moons' kind, `--steep` moves `--planet-fly`, `--seam-test` and `--cam` to the planet's steepest ground.

Layer types: fbm, ridged, billow, eroded, warp (no-fold rule), curve, terrace, canyon, mask, combines, craters, ergs, sea clamp, relief knee. Every layer has an interval form; bounds take a LOD range and `PlanetHeightBounds` holds two bands (all LODs, LOD 3+). The noise's Lipschitz constant went from 14.73 to 3.8 (supremum 3.75 by hill climb; `NoiseSlopeBoundHoldsOnAGrid` bounds it at 3.792 on a grid of in-cell points, proven over directions, sampled between points). Its gradient's Lipschitz constant is 15 (hill climb 14.47, sampled at 11.6): an octave's interval is its centre value +- the least of 3.8 x distance and (its slope at the centre + 7.5 x distance) x distance. A kind's JSON is checked at compile: unknown keys, out-of-range values and a gate that is not a mask are errors naming the kind and the layer.

Slope and relief over land, 10^5 samples (`KindStatistics`; 1 m roughness is the 1 m slope less the 100 m slope, on ground under 20 deg):

| Kind | 10 km relief p50 / p95 (m) | 100 m slope p50 / p90 / p99 (deg) | Over 30 / 45 deg | 1 m rough |
| --- | --- | --- | --- | --- |
| v1 (`legacy-rolling`) | 215 / 489 | 2.1 / 5.5 / 8.5 | 0 / 0 | 9.1 % |
| temperate, target | 700 / 3,000 | 9 / 28 / 42 | 8 % / 0.8 % | 8 % |
| temperate, measured | 868 / 3,285 | 8.4 / 26.3 / 41.3 | 6.6 % / 0.40 % | 10.0 % |
| barren-cratered, target | 800 / 4,000 | 7 / 25 / 40 | 7 % / 0.3 % | 10 % |
| barren-cratered, measured (400 km moon) | 703 / 2,711 | 6.1 / 24.7 / 41.0 | 5.5 % / 0.54 % | 9.8 % |
| desert-canyon, target | 500 / 1,800 | 3 / 25 / 70 | 8 % / 4 % | 5 % flats |
| desert-canyon, measured | 718 / 1,257 | 2.6 / 17.5 / 70.1 | 7.0 % / 5.54 % | 11.0 % flats |

Cost (`KindCost`, Release, one thread, 48 surface leaves on each kind's steepest ground; budget 150 ns/point = 0.30 ms/leaf of lattice with row SIMD, whole leaf 0.7 ms):

| Kind | Lattice ns/point at LOD 0 / 2 / 4 / 6 / 8 / 12, SIMD | Scalar | Whole leaf ms, SIMD | `Height` / `Bounds` / `Classify` (us) | Impostor us/sample |
| --- | --- | --- | --- | --- | --- |
| Budget | 150 | 200 at LOD 0, 400 interim | 0.7 | 0.6 / 1 / 0.3 | 0.3 |
| v1 recipe | 51 / 51 / 51 / 50 / 54 / 62 | 132 / 130 / 131 / 130 / 143 / 173 | 0.36-0.43 (v1 code: 1.01 at LOD 0, 0.64 at LOD 4) | 0.42 / 0.73 / 0.53 | 0.28 |
| temperate | 152 / 165 / 158 / 180 / 178 / 133 | 358 / 394 / 389 / 501 / 496 / 388 | 0.64-0.99 | 1.47 / 1.29 / 1.50 | 0.17 |
| barren-cratered | 69 / 65 / 67 / 84 / 102 / 123 | 133 / 124 / 129 / 178 / 223 / 225 | 0.60-0.63 | 1.55 / 2.00 / 2.25 | 0.40 |
| desert-canyon | 97 / 110 / 141 / 148 / 159 / 109 | 190 / 232 / 370 / 402 / 444 / 291 | 0.53-0.88 | 1.47 / 1.69 / 1.95 | 0.17 |

Before the review pass (5117ebd) the last column read 2.18 / 0.98 / 1.17 (temperate), 1.68 / 1.90 / 2.05 (barren) and 1.74 / 1.28 / 1.52 (desert): a point query now runs a layer's octaves eight at a time, and bounds pay for each octave's derivative and each crater's profile.

Flights over steep ground (`--planet-fly --steep`, Release, 8 cores, RTX 4090, 1600x900, ~2,870 frames a leg, planet of 1000 km), after the review pass (e28eed4) and before it (5117ebd, flown again the same day; settled counts at the first leg's start; selection time is the p50 over each leg after the pass, the last selection of each leg before it):

| Kind | Leaves loaded/s at 30 / 300 / 3000 m/s / descent | Holes | Leaves desired / with geometry (empty share) | Selection ms (worker) |
| --- | --- | --- | --- | --- |
| temperate (start on 3,404 m ground) | 247 / 2314 / 2761 / 199 | 0 in every leg | 8,865 / 2,490 (72 %) | 1.46 / 1.00 / 0.71 / 0.53, max 5.2 |
| desert-canyon | 383 / 2202 / 2218 / 167 | 0 in every leg | 7,595 / 2,192 (71 %) | 1.36 / 0.90 / 0.63 / 0.44, max 4.9 |
| barren-cratered | 221 / 1907 / 2245 / 194 | 0 in every leg | 7,837 / 2,484 (68 %) | 1.49 / 1.08 / 0.79 / 0.76, max 5.6 |
| temperate, before | 375 / 3596 / 3521 / 286 | 0 in every leg | 11,732 / 2,486 (79 %) | 3.4-7.4 |
| desert-canyon, before | 476 / 3457 / 2782 / 340 | 0 in every leg | 10,301 / 2,192 (79 %) | 4.0-12.2 |
| barren-cratered, before | 469 / 3981 / 3528 / 708 | 0 at 30 m/s and on the descent; 353 of 2872 frames at 300 m/s and 39 of 2874 at 3000 (one LOD-11 leaf; the first measurement had 1156 of 2876 and 280 of 2867) | 12,614 / 2,484 (80 %) | 7.6-17.9 |

The late barren leaf was starved, not misclassified: loads went strictly by distance in the leaf's own voxels, which puts horizon leaves last, and each burst of new fine leaves filled the batch (96 jobs, no job free) ahead of it. Leaves now load by distance / voxel / (1 + LOD). The hole counter asks undrawn descendants of a waiting leaf at their own LOD, as selection does; with fewer empty leaves loaded it otherwise counted a coarse leaf's first frames after a speed-LOD change as holes over ground the frame before had shown whole.

After the pass (e28eed4, Debug with validation): `--seam-test --steep` on temperate, 0 crack px at 100 / 50 / 20 / 5 km, nothing logged; `--moon-test` passes on the cratered moon (0 holes, nothing logged; its 1024^2 impostor baked in 3.7 s on one worker in Release before the pass, not timed again). Band limit against box-filtered LOD 0, LOD 1-12: within 0.35 voxel RMS and 1.5 voxel max on every kind (asserted; worst RMS 0.16, desert at LOD 10; worst max 1.31, desert at LOD 5).

Changed from the design:
- **Craters add up and pass a knee** (a single crater is untouched, nested ones ease into 1.5 x the deepest class). The design's erase rule (`acc = acc(1 - e) + profile`) lifts a small crater on a basin floor back to the datum.
- **Eroded layers keep the damped sum and the running gradient on the coarse grid** (4 values), not each octave's raw value.
- **Curves have no per-LOD riser widening**; terraces widen their riser band by 2 voxels x the input's 90th-percentile slope (one number per body, so the height stays a pure function of the direction), canyons by their wall share.
- **The sea clamp's start is the kind's** (`sea.inlandStart`, default 0.06). Temperate starts it at 0.02, where its carving layers gate in, and has a beach profile at the coast line; before, hollows in the ramp flooded and the spawn stood on the shore of one.
- **Leaves load coarse first** (distance / voxel / (1 + LOD)), not by distance in voxels alone.
- **Steep rock is the shader's.** The voxels of the surface layer keep their material on steep ground (the layer under it still turns to rock) and `terrain.frag` shows sand and grass as rock by the pixel's slope, 0.55 to 0.85 rise over run, as the impostor bake blends it. A material is one per triangle, so the voxels' own switch drew every rim as a saw-tooth; `legacy-rolling` keeps it.
- Complex craters have a flat floor, terraces and a central peak; no peak rings, rays or age easing to a cosine bowl.

Carry-overs:
- **Shell tightness.** 68-72 % of desired leaves mesh empty (v1: 66 %; 79-80 % before the review pass). What tightened it: craters bound by each stamp's radial profile over the cap, the canyon by the field's slope at the cap centre, terraces with strata by the monotone hull, and every octave by its slope at the centre. What is left: nodes at LOD 5 and up read the pyramid, whose finest texel answers for a 9 km cap at 1000 km; refining LOD 5 as well cut barren's desired leaves by a further 8 % (8,550 to 7,843, measured before the per-octave slope bound) for 4 ms more selection and was not kept.
- **Selection cost.** A selector remembers each node's shell class (2 MB a body it has selected on, least recently used dropped), so only nodes new to the camera's neighbourhood ask `Bounds`: about 100 of 8,000-16,000 a selection in flight. `SelectionCost` (Release, 100 m over steep ground): 0.60-0.76 ms with the camera still, 0.75-0.92 ms mean and under 2 ms at worst at 300 m/s, 54-57 ns a node; in `--planet-fly` on a below-normal worker 0.4-1.5 ms p50 and 1.9 ms p95 a leg. The first selection on a body, or after a jump, bounds every node: 10.4 / 11.1 / 14.4 ms (temperate / desert / barren), and a leg's first selections reach 5.6 ms.
- **Budgets missed:** temperate lattice at LOD 0 to 8 and desert at LOD 8 (to 180 ns/point); whole leaf on temperate and desert (the voxel fill is 0.3-0.4 ms of it, as in M2); `Height` (1.5 us on the kinds; 0.42 on the v1 recipe, inside 0.6), `Bounds` (1.3-2.0 us), `Classify` asked afresh (1.5-2.3 us against 0.3, worse than before the pass; selection asks about 100 nodes a pass that way); the cratered impostor (0.40 us/sample).
- **Rims and risers.** The saw-teeth on desert rims were the rock material, one per triangle, not the mesh: shot with one albedo the rims are clean, and with the terrace layer muted the teeth stayed. With rock by the pixel's slope the d_low (1.2 km) and e_ground (60 m) shots show clean rims; about a pixel of wobble is left on some lower edges where vertex normals turn. Other material lines (the shore, the peak line) are still one material per triangle; not looked at. The terrace band follows one slope per body, not the slope at the point; no staircase showed in those two views.
- **The Lipschitz constants are searched, not proven:** 3.8 (hill climb 3.75; the grid test proves it over directions only) and 15 for the gradient (hill climb 14.47).
- **The horizon margin** still counts horizontal speed only; climbing out of a deep crater moves the horizon several times faster than the climb. No hole was traced to it.
- **Targets missed:** temperate share over 30 / 45 deg (6.6 / 0.40 %), barren relief p95 (2,711 m: the largest crater a 400 km moon's grid takes is ~24 km radius), desert slope p90 (17.5 deg) and relief p95 (1,257 m), roughness on temperate and desert flats.
- **Not built:** the ImGui "Planet kind" panel, `--kind-radius`, `--kind-orbit`, the program hash in a handshake (nothing reads `GeneratorVersion` yet), per-chunk culling of gated layers by cap interval (rows skip a layer whose gate is 0 across the window).
- **Coast.** Between the waterline and the inland clamp's start (continent 0-0.06) hills can dig below sea level, as v1's detail did: lagoons near coasts. The sim tests now search for open seabed instead of assuming the v1 shelf 16 km out.
- Contact sheets (orbit, 60 km, 8 km, 1.2 km, 60 m): the scratchpad's `kinds/<kind>_sheet.png`. Risers over 76 deg mesh as a one-voxel staircase until `undercut` (K2).

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
