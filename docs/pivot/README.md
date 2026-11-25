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
| Ships | Hull chunks are 3D modelled (D11: full mesh editing as a mesh brush, still cuttable); interiors are cut away from them. Components are prefab (bought) or player-made (function from a part graph); a prefab is a saved player build. Plating gives floor gravity and a separate inertial dampener (EM + energy, up to its rating) cancels manoeuvres (D9). 6-DOF with flight assist plus aerodynamic lift in atmosphere (D10). Deliberate runaway override, ejectable reactor cores (D12, D13). Several warp drives support each other, weighted by delivered power (D14). Components are wired (power, data, control); what can cancel or control what depends on the wiring, and wires can be cut (D17). |
| Stations, tractors, PvP | Station anchors on any grid, activated at the required position and velocity, crawl slowly over days; a dropped station is a rare disaster, so stations carry thrusters and escape pods (D25). Stations have **shields** (capacity + recharge; a big hit or sustained fire gets through) and **debris deflection** by station tractors and arms; debris in a protected zone stays real (D34, D35). **Tractor beams are a weapon unless requested** (D26: consent flag on a docking or tow request; a station docks you only if you ask). Tractors **without antimatter only pull** (D28), so only AM-fed station emitters deflect debris. **Station sieges** are allowed and factions prefer **capture** to destruction (D27: `CommandCore` override; destruction possible but hard). A deliberate damaging shatter or thrown rock is a hostile act of its owner (D36, one shared `IncidentRecord`); fragments from legal mining near a station are always the **miner's liability** (D33). A threshold shatter breaks the whole body into fragments and a dust cloud (D37). |
| Water | Killed except shore wash. Keep analytic swell, GPU FFT detail, ocean shading and the GPU swash, all on a sea sphere. |
| Jolt precision | `Brutal.Jolt` is single precision (`RVec3` binds as `float3`). World positions are `double3` per frame on our side; Jolt runs in local float bubbles. No double-precision Jolt build for now. |

## Documents

| Doc | What it holds |
| --- | --- |
| [decisions.md](decisions.md) | User decisions D9-D37 (round 5 and the r6 pass). These win over every doc. |
| [inventory.md](inventory.md) | Keep / adapt / cut per file, what shore wash needs, float hazards, ordered cut list with measured time recovered. |
| [frames-and-zones.md](frames-and-zones.md) | `FrameTree` (one Solar root per system, Lagrange frames), Kepler rails, patched conics, pooled Jolt scenes ("bubbles"), enter/leave rules, predictive merge and split, transfers, camera-relative rendering, networking. Owns the shared enums (`FrameKind`, `PlacementMode`, `BubbleAnchor`, `CelestialKind`). |
| [warp.md](warp.md) | Warp drive: states, smooth closed-form path round bodies (D15), exit-point selection as zone selection (bubble picked at arrival), merge on predicted closest approach, interdiction fields, fleet warp. Round 5: the last 2 s in real space as a ship-shaped projectile, the civilian arrival beacon (D18), no exit floor (low exits make a `WarpBlast` pressure wave, fined, D16), linked drives in one `WarpDriveSet` (D14), cancels through the ship's wiring (D17). |
| [wormholes.md](wormholes.md) | Wormholes (revision 3: instant vs tunnel classes, stability reserve and energy fight, tunnel frame with current, eddies, wall discharge and ram ejection, retaliation, collapse flow, re-establish; round 5: proposals accepted (D19), natural cheapest and manufactured fastest (D23), the tractor consent rule (D26), owner of tunnel retaliation): plan, charge, commit, open, collapse flush; mouth pairs; flow; suppression field; catchers (gate, interdict, traffic control); signatures and spoofing; natural wormholes and frames; solar flares; seeing through the mouth. |
| [exotic-matter.md](exotic-matter.md) | Exotic matter and antimatter: sources (black holes, deposits), refining, grades, warp fuel mix, anti-gravitons, plasma thrust, anti-graviton bombs, black holes collapsing to rocky planets. Revision 2 adds slurry vs recirculating drives, runaway bursts, antimatter production and radiation, pirate kit, station anchors. Revision 3: crystal decay floor (0.96), purity cap 0.995, seven wormhole additive families, stabilisers, bombs drain wormhole stability. Revision 5 (round 5): the inertial dampener runtime (D9), core ejection and the runaway override (D12, D13), linked drives (D14), long-lived black holes and deliberate collapse (D21), EM mostly from planets and asteroids (D22), lingering radiation (D24), anchors on any grid with crawl and an overload alarm (D25, D27). |
| [rings-and-asteroids.md](rings-and-asteroids.md) | Planetary rings (mesh, haze, asteroids; band density tied to colour, dust as fog) and asteroid fracture mining: laser heat, wedge, violent breakup, weapon shatter, EVA sampling and cave mining, tumbling and tractored rocks, permanent removal with replenishment events. Revision 4: station shields, debris deflection by station tractors and arms, hostile-act attribution, whole-body shatter with a dust cloud (D34-D37). Owns the shared `IncidentRecord` (with liability, D33) and the interim siege rules (D27). |
| [tractor-beams.md](tractor-beams.md) | Tractor beams (push/pull cone, adjustable FOV, EM + power; push needs AM, without it a `Graviton` drive only pulls, D28) and the hull stress model that lets a wrong lock rip a part off and opposing beams tear a ship apart (Haul and Hold locks, 4 m node graph with mount and plane cuts, solved only for loaded ships). Revision 2: station debris defence (AM-fed emitters only). r6: owns the consent rule (D26: a weapon unless requested), sieges and capture (D27), tunnel strikes via wormholes.md. |
| [ship-design-and-construction.md](ship-design-and-construction.md) | Ship designer app (standalone and in game, one core library), material > part > component > hull/armour > ship hierarchy, plans, cost per redefined level, buying vs building, cutting stock, construction steps (weld, bolt, rivet), assembly order. Projects: `src/Plans` (headless: validate, cost, order), `src/Designer`, `src/Designer.Ui`, `src/Designer.App`. Round 5: wiring (D17), aero parts (D10), dampener coils (D9), ejectable cores (D13), linked drives (D14), station pods, thrusters and anchors (D25), the `CommandCore` (D27). |
| [planets.md](planets.md) | Includes a Gas giants section (no surface, scooping, crush depth, storms, ring host). Round 5: relief and deep heat (D29), unminable shores and seabeds (D30), ported weather (D31), permanent edits (D32). Cartesian voxels in the rotating planet frame, surface shell, one octree to LOD 12 then an impostor, streaming at flight speed, memory, sea sphere. |
| [ship-hulls.md](ship-hulls.md) | Blueprint = CSG brush list (plus mesh brushes, D11), baked to 0.25 m SDF bricks, dual contouring, mass, rooms, collision, damage, LOD, build UX. |
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

Sim/client split (a `Sim` assembly with no Vulkan, plus a headless server), entity model, RakNet transport and replication, character controller (radial up, EVA), flight model (thrusters at mounts, flight assist), atmosphere drag, orbital HUD, save format, per-body lighting and eclipses, impostor-to-octree handoff, factions, law and fines (the consumer of `IncidentRecord`), siege and capture beyond the interim rules (D27), sensors, combat, profiler rows for all of it.

## Milestones

Each ends with a measured exit check (details in review.md).

| | Milestone | Exit check |
| --- | --- | --- |
| M0 | Tag `island-final`. Cut water down to the ocean core and shore wash shaders (inventory steps 1-6; the island itself is retired, no A/B on it), carve the `Sim` assembly, file the upstream Jolt asks. | Game starts on a flat placeholder; sim builds without Vulkan. Shore wash is proven on a planet beach in M2b. |
| M1 | `FrameTree`, rails, double camera with translation-free view, per-draw instance record, GPU-culled indirect terrain draws, ocean time rebasing. | Surface of a smooth 1000 km sphere to 1e8 m with float quantisation error <= ~2e-4 px; terrain draw recording < 0.1 ms. Measured (`--orbit-test` and `--orbit-test inertial`, 1600x900, ~4450 frames each, island terrain GPU-culled every frame and drawn out to 1e8 m): float quantisation jitter max 2.1e-4 px (limit 0.1); smoothness (second difference against poses evaluated analytically at render time) <= 2.4e-12 px, where tick poses would give 1.4 px; GPU marker (fragment readback) vs CPU prediction <= 2.8e-3 px in every decade (the 1/256 px subpixel snap), jitter as drawn <= 4.8e-3 px; impostor surface error <= 1.5e-5 of range; reverse-Z depth ok, no NaN/Inf. Terrain recording 0.024 ms (`--bench`). |
| M2 | `PlanetGenerator`, shell, horizon pruning, `EditStore`, impostors, focus-body switch. M2b: sea sphere, global `OceanSurface`, shore wash on a planet beach. | Leaves/s and main-thread ms at 30, 300, 3000 m/s; orbit to ground with no holes. |
| M3 | `PhysicsRuntime` + `PhysicsScene`, surface bubble with 32 m rebases, `CharacterVirtual` with radial up. | Walk and dig 10 km from spawn; collision chunk memory measured. |
| M4 | Two players on foot over RakNet, save/load. | Bytes/s per client and reconcile error logged. |
| M5 | Minimal hull: box/wedge/cylinder brushes, add/subtract, mass, convex exterior, seat/thruster/tank/foot. | 15 m craft built in ~5 min; bake p50/p95 in profiler. |
| M6 | Atmospheric flight and landing, client ship prediction. | Fly 50 km at 300 m/s and land on a slope with a passenger. |
| M7 | Orbit, warp, the moon. | Two players reach the moon; orbit elements unchanged across frame transitions. |
| M8 | Walking inside a moving ship: grid bubble, `GridVolume`, door proxies, plating gravity and the inertial dampener. Then rooms, damage, hull LOD. | |

### Carry-overs from M0 (2026-11-25)

M0 landed on `space-pivot` (2f70258..1df5d87). Review items left for the milestone that replaces the island placeholder:

- **M2b shore depth:** the GPU swell has no depth cap past the ~1 km bathymetry window (no far field since layer B was cut); the CPU cap costs ~0.56 us per sample with no edits and ~3.3 us with 1000 edits (`TerrainShoreDepth` rebuilds the island set and scans every edit). The planet `IShoreDepth` must bin edits and cache the generator.
- **M2 protected shores (D30):** `ShoreGuard` refuses whole digs within 16 m of ground below sea + 2 m. planets.md specifies `ShoreProtectHeight` 6 m, clamping removal to the generated surface, letting players remove what they placed, and a per-chunk column mask instead of ~800 generator calls per big brush.
- **M2 pit rule:** CPU uses one voxel below sea level, the GPU uses 2^lod m at coarser LODs; base both on the generated sea mask (planets.md).
- **M4 networking:** the swell carrier phase is now integrated per tick (no snapping), which makes it history-dependent; give it a closed form or put it in the sim snapshot before late join.
- **Debug paths:** OceanPanel and GameApp still write swell wind straight into the sim; route through sim commands. Refused digs have no feedback (show the count, then the D30 band overlay).
- **Docs:** docs/ocean-gpu-contract.md's Frame UBO section is stale.

## Open questions

Decisions so far are logged in [decisions.md](decisions.md) (D9-D37) and in the Decided table above. Remaining open questions live in each doc's own list.

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
