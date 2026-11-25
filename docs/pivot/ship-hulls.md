## Ship construction: hull CSG + components

### Recommendation: (d) hybrid. A CSG brush list is the truth, baked to SDF bricks and meshed by dual contouring

A ship is a **blueprint**: an ordered list of compact, quantized CSG brushes (add, subtract, paint), plus a list of component placements. That list is the only thing that gets saved, sent over the network, edited or damaged. Every peer bakes it locally and in parallel:

- brush list → padded 34³ SDF bricks at **0.25 m cells** (8 m bricks)
- → dual contouring mesh (render), coarse 0.5 m occupancy (rooms, connectivity), mass moments, convex collision.

The SDF is scratch memory: it is thrown away after the bake, as terrain chunks are now.

**Why:**
- **Terrain already works this way.** `BrushOp` ops are replayed over freshly generated chunks at any LOD, with no persistent voxel storage (`VoxelEditor.Apply` in `ChunkJob`). Hulls are the same idea with richer brushes, so most of the pipeline carries over.
- **It is compact.** A brush is 34 B raw and about 12 B after delta + zlib. A 2,000-brush ship is about 25 KB on the wire. An edit is about 40 B.
- **Everything else falls out of the voxel bake:**
  - LOD: re-bake at coarser cells.
  - Rooms: flood fill the air.
  - Mass, CoM and inertia: integrate occupancy × material density. Interior cuts lower mass automatically.
  - Splitting: solid connectivity.
  - Damage: just more subtract brushes.
- **Sharp hard-surface edges.** Brushes are analytic, so each sign-change edge records the brush that won there and uses its exact gradient as Hermite data. The QEF vertex then puts corners and creases exactly where they belong. Dual contouring uses the same cell/quad topology as `SurfaceNetsMesher`; only vertex placement differs, so this is one mesher family, not a second one.

**Rejected:**
- **(a) Fine voxel grid as truth:** MBs per ship, edits ship as voxel deltas, resolution locked forever, no clean source to re-bake LOD from.
- **(b) Polygon CSG with exact booleans (BSP / mesh booleans):** float robustness (slivers, T-junctions), cost explodes with hundreds of damage holes, no rooms, mass or LOD for free, cross-client determinism fragile.
- **(c) Poly/box modelling with boolean subtract:** needs a real vertex/edge modeller UI (players are bad at it), has (b)'s boolean robustness problems, and meshes don't compress for the network.
- **Space Engineers block grid only:** not "3D modelling"; it can't cut arbitrary interiors.
- **V-HACD convex decomposition:** seconds per ship, not deterministic, has to rerun on every hole.

### Data model (`src/Ships`, no Vulkan dependency; runtime in `src/Ships.Runtime`)

```csharp
namespace BrutalGame.Ships;

public enum BrushShape : byte { Box, Wedge, Cylinder, Capsule, Sphere, Cone, Prism, Convex }   // Prism/Convex: Param indexes PlaneTable
public enum BrushOp    : byte { Add, Subtract, Paint, Intersect }
[Flags] public enum BrushFlags : byte { None = 0, Interior = 1, NoCollide = 2, Damage = 4, Mirrored = 8 }

/// Wire + save format, blittable, little endian, 34 B. Order in the list is evaluation order.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HullBrush
{
    public uint Id;               // stable across edits (server allocated)
    public BrushShape Shape;
    public BrushOp Op;
    public BrushFlags Flags;
    public byte Material;         // index into ShipBlueprint.Materials (data-driven: density kg/m³, strength, visual)
    public int PosX, PosY, PosZ;  // ship-local, 1/1024 m (±2097 km; ships capped at 1 km extent)
    public uint Rotation;         // smallest-three quaternion 2+3×10 bits; decode uses only mul/add/sqrt (no trig)
    public ushort HalfX, HalfY, HalfZ; // 1/256 m (≤256 m half extent)
    public ushort Bevel;          // 1/1024 m: edge rounding (Add) or smooth-union radius
    public ushort Param;          // cone taper, or PlaneTable index for Prism/Convex
}

public struct PlaneRef { public short OctU, OctV; public int Dist; }   // 8 B, octahedral normal + 1/1024 m distance; ≤16 per Convex

public sealed class ShipBlueprint                  // also what a prefab is (hull chunk, component housing, or whole ship)
{
    public ulong ContentHash;                      // xxHash64 of canonical bytes: prefab id + bake cache key
    public NativeList<HullBrush> Brushes;
    public NativeList<PlaneRef> PlaneTable;
    public NativeList<HullChunkGroup> Chunks;      // authoring groups = the user's "hull chunks"
    public NativeList<ComponentPlacement> Components;
    public NativeList<ushort> Materials;           // ids into data/materials
}

public struct HullChunkGroup { public uint FirstBrushId, LastBrushId; public ulong SourcePrefab; public int NameIndex; }

public struct ComponentPlacement                    // 30 B
{
    public uint Id;
    public ulong DesignHash;                       // ComponentDesign.ContentHash
    public int PosX, PosY, PosZ;                   // 1/1024 m
    public uint Rotation;
    public ushort Flags;                           // door open, powered, ...
}

public sealed class ComponentDesign                 // bought prefab or player made: same type
{
    public ulong ContentHash;
    public ushort FunctionCore;                    // data-defined crafted item: Thruster, Reactor, Seat, Door... stats live there
    public ShipBlueprint Housing;                  // player-modelled shell, baked at 1/16 m cells, bounds ≤ 8 m, instanced
    public NativeList<HullBrush> Cutters;          // subtracted from the hull it is placed in (door opening, nozzle recess)
    public NativeList<Socket> Sockets;             // mount face + typed conduits
    public MassMoments Mass;                       // housing + core, precomputed once per design
    public PortalDef Portal;                       // doors/vents: rooms edge when open
}

public enum SocketType : byte { Mount, Power, Fuel, Data, Coolant }
public struct Socket { public SocketType Type; public int PosX, PosY, PosZ; public uint Rotation; public ushort FootprintHalfX, FootprintHalfZ; }

/// Fixed-point, exactly incremental: subtract old brick, add new, identical on every peer whatever the edit history.
public struct MassMoments
{
    public long MassGrams;
    public Int128 Mx, My, Mz;                      // g·mm
    public Int128 Ixx, Iyy, Izz, Ixy, Ixz, Iyz;    // g·mm²
}

public readonly record struct BrickKey(short X, short Y, short Z, byte Lod);   // LOD L: cells 0.25·2^L m, brick 8·2^L m

public struct BrickState                            // per baked brick, kept after the SDF is dropped (~1 KB)
{
    public int Version;
    public MassMoments Mass;
    public fixed ulong Solid[8];  public fixed ulong Air[8];   // 16³ coarse 0.5 m occupancy bitsets (512 B with Filled)
    public byte SolidLabels, AirLabels;            // local component counts; labels per coarse cell in a side pool
    public int MeshHandle;                         // renderer slot, client only
    public int SectionIndex;                       // collision section (2×2×2 bricks = 16 m)
}

public struct Room     { public int Id; public float VolumeM3; public float PressureKpa; public GasMix Gas; public int SeedCell; }
public enum EdgeKind : byte { Door, Vent, Breach, Leak }
public struct RoomEdge { public int A, B; /* B = -1: space */ public EdgeKind Kind; public float AreaM2; public uint ComponentId; }

public sealed class ShipHull                        // runtime, one per ship body
{
    public ShipBlueprint Blueprint;
    public NativeList<HullBrush> Damage;           // separate layer, cap 4096, coalesced
    public BrushBins Bins;                         // brick → brush index lists, rebuilt per edit for touched bricks only
    public Dictionary<BrickKey, int> Bricks;  public NativeList<BrickState> BrickPool;
    public MassMoments Total;
    public NativeList<Room> Rooms;  public NativeList<RoomEdge> Edges;
    public NativeList<CollisionSection> Sections;  // StaticCompound per section, children of one MutableCompound
    public uint KeepSeedCell;                      // after a split: the body keeps only the solid component holding this cell
}
```

### Evaluation and bake (`HullBrickJob`, modelled on `ChunkJob`)

1. **Gather** the brushes whose AABB plus band overlaps the brick (`BrushBins`), in list order, adding cutters from placed components. Cap: **256 brushes per brick**; the build tool refuses placements past it.
2. **Evaluate** each brush only over its own band ∩ brick, in order, into padded 34³ `sbyte` density + `byte` material, plus a `ushort` "winning brush" scratch array. Add = min, Subtract = max(d, −b), Paint = material only, Intersect = max. Smooth union uses Bevel as `k`. Ops are scalar plus SIMD, no trig, fixed order: bit-identical on x64. Correctness never depends on it, though: the server owns mass, rooms and splits.
3. **Damage layer:** subtract brushes with a jagged edge from `Noise.cs` (seeded by brush Id).
4. **Outputs:**
   - **All peers:** `MassMoments`, coarse 0.5 m solid/air bitsets, local connected-component labels.
   - **Client only:** dual contouring mesh (QEF from exact brush gradients), split vertices at creases over 40°, `meshopt_simplify` with border vertices locked (Brutal.MeshOptimizer), then pack to `PackedTerrainVertex`. Its 16-bit chunk-local format already works at any cell scale.
   - The server skips meshing.
5. **Global pass (cheap, main thread or job):** union-find across brick faces, done for solid and for air.

**Bake cost** (estimates; nothing here is measured yet, and the ImGui "Ships" panel must show bake ms p50/p95, queue length, tris and sub-shapes before any of these numbers are trusted):

| Item | Estimate |
| --- | --- |
| SDF evaluation per brick (~120k brush evals, SIMD) | 0.2–0.6 ms |
| Dual contouring + QEF (~3k surface cells) | ~0.6 ms |
| Simplify | ~0.5 ms |
| Client total per brick | ~1–2 ms on one worker |
| Server total per brick (no mesh) | ~0.4 ms |
| 100 m × 40 m × 30 m ship | ~180 surface bricks: 0.2–0.35 s CPU, ~15–25 ms wall on 16 workers. Cached on disk by `(ContentHash, BrickKey)`. |
| 500 m capital ship | ~8k bricks: ~0.5–1 s wall at load, async, cached |
| One edit | touches 1–8 bricks: 1–16 ms CPU, visible 1–2 frames later |
| Main-thread budget | at most 16 brick rebakes started per tick across all ships, priority by distance; uploads go through `TerrainRenderer`-style staging under budget |

**Per-tick cost:**
- **Hull geometry:** zero. Nothing about the hull runs per tick.
- **Life support:** walks the room graph, ~1 µs per room (50 ships × 20 rooms ≈ 0.02 ms, jobified).
- **Edits:** mass update is O(1) per changed brick (Int128 subtract/add). Collision update is one `MutableCompoundShape.ModifyShape` per changed section, then `MotionProperties.SetMassProperties`; estimated < 0.1 ms main thread.

### Collision (Jolt)

**Constraints found:**
- Jolt `MeshShape` can't be a proper dynamic body: no volume or inertia, and no mesh-vs-mesh or mesh-vs-terrain contact.
- The shipped native `jolt.dll` doesn't export `MeshShapeSettings_Create_3` at all (`JoltRuntime.NativeMeshShape` is false), so even static meshes fall back to triangle compounds.
- What it does export: `ConvexHullShapeSettings_Create(points)`, `StaticCompoundShapeSettings`, `MutableCompoundShape_{Add,Remove,Modify}Shape`, `OffsetCenterOfMassShape`, `BodyInterface_SetShape`, `MotionProperties_SetMassProperties`, `MassProperties_SetMass/SetInertia`, `BodyCreationSettings_SetOverrideMassProperties`.

**Exterior (dynamic body in the parent grid):** a convex compound of the **filled** hull, meaning solid plus sealed rooms. The exterior doesn't need interior concavity. Open hangars stay unfilled, so they stay concave.
- Per 4 m cell of the 0.5 m filled grid, take each connected component. If convexity (voxel volume ÷ hull volume) is ≥ 0.85, emit one `ConvexHull` of at most 64 points (coarse surface-nets vertices, inflated ≤ 0.25 m). Otherwise split into octants, down to 1 m, then greedy boxes. Deterministic integer traversal.
- Sections of 16 m become `StaticCompound`s. A `MutableCompound` holds the sections (linear scan stays fine at ~30–100 children), wrapped in `OffsetCenterOfMassShape` so the body uses the real CoM.
- Mass and inertia come from `MassMoments`, never from the shapes, because filled rooms would make the shapes far too heavy.
- **Limits:** ≤ 2048 hulls per ship, ≤ 64 vertices per hull.

**Interior (walking, loose items, small craft in hangars):** the ship is its own physics zone. Inside that grid the hull is a **static** body built from the simplified render mesh via `StaticMeshShape`, only for bricks within 32 m of an occupant (streamed like terrain), ≤ 2k tris per brick. This fits the solar > planet > ship grid design and keeps interior precision local. Grid entry/exit rules belong to the zones design.

**Framework proposal (separate, not done here):** ask upstream BRUTAL to ship a native Jolt with the missing MeshShape and NarrowPhase exports. Until then, `TriangleBvh` serves build-tool picking against hull bricks.

### Authoring UX (build mode, ship-local grid)

- **Place a primitive** (Box, Wedge, Cylinder, Capsule, Sphere, Cone, Prism by drawing a 2D convex outline, Convex), then drag faces with gizmos. Grid snap 1/8, 1/4, 1/2 or 1 m; rotation snap 15° (free with a modifier). Mode toggle: Add / Subtract / Paint.
- **Hollow ("cut the interior"):** select Add brushes, give a wall thickness ≥ 0.5 m, and it emits an inset Subtract brush flagged `Interior`. This is the main "interiors cut away" gesture. There is also a "Cut room" tool: drag a box inside the hull.
- **Editing aids:** bevel handle, mirror planes (mirrored brushes are stored as real brushes), select / move / duplicate / group, and save a group as a HullChunk prefab.
- **Viewing aids:** cross-section clip plane, hull X-ray, hide groups.
- **Feedback while dragging:** a GPU-raymarched ghost of the one brush. Committing triggers the bake; nothing is baked per frame while dragging.
- **Undo/redo** is brush list diffs. Edits go to the server as requests, the server validates them (materials, permissions, limits) and broadcasts with a sequence number.
- **Components:**
  - Palette of bought prefabs and the player's own designs. Placing raycasts the hull (`TriangleBvh`) and aligns the Mount socket to the surface normal, snapped to the grid.
  - It is valid only if ≥ 70% of the mount footprint has solid hull behind it, sampled against the coarse occupancy.
  - Typed sockets (power, fuel, data) snap within 0.25 m.
  - A component's cutters carve the hull automatically, e.g. door openings.
- **Player-made component** = modelled housing (same tools, 1/16 m cells, ≤ 8 m) + a crafted/bought **function core** (data-defined stats). A prefab is just a saved `ShipBlueprint` or `ComponentDesign` keyed by `ContentHash`. Each design is baked once and drawn instanced across every ship.

### Networking and saves

- **On join or stream-in:** send `ContentHash`. The client asks for the blueprint only on a cache miss, then gets the brush array (delta + zlib via Brutal.ZLib). A 2k-brush ship is ~25 KB.
- **Edits and damage:** `{shipId, seq, insert/remove/modify brush at index}` (~40 B), and `{shipId, seq, damage brush}` (34 B).
- **Determinism:**
  - **Server-authoritative:** mass, rooms, splits and collision. Clients' local values are used only for prediction and visuals.
  - **Deterministic by construction anyway:** quantized inputs, fixed order, no trig, Int128 moments.
- **Saves:** the same bytes. The bake cache on disk is optional and keyed by hash.

### Mass, volume, inertia

- Per coarse sample: occupancy fraction from density × material density (kg/m³, data-defined, representing a structural composite, not solid steel).
- Accumulate `MassMoments` per brick in fixed point, sum over bricks, add components via the parallel-axis theorem, and convert once to Jolt `MassProperties` (`SetMass`, `SetInertia`) with the CoM offset.
- Interior Subtract brushes remove mass by construction.
- Room volume = air cell count × 0.125 m³.

### Rooms and pressurisation

- **Air grid:** the coarse 0.5 m grid. A cell is air only if all its fine samples are air.
- **Labelling:** per-brick labels plus union-find across bricks. The component touching the ship-bounds margin is space; every other component is a sealed room.
- **Doors and vents** are portals. Their cells count as solid when the door is closed, and their open state is an edge in the room graph.
- **Recompute** only on topology change (edit or damage), and only the touched bricks plus the global union-find (~0.1 ms).
- **Breaches:**
  - A hole of ≥ 0.5 m merges a room with space and creates a Breach edge with the damage brush's cross-section area.
  - A smaller hole: if the damage brush's AABB touches both room cells and space cells, it becomes a Leak edge with area πr².
- **Per tick:** pressure and gas flow over edges.

### Damage and splitting

- **Holes** are Subtract brushes in the damage layer: sphere or ellipsoid, jagged, radius ≥ 0.25 m. Repair = remove the damage brush or add a patch brush.
- **Coalescing:** when overlap is > 50%, merge into the bounding sphere. Caps: 4096 per ship, 64 per brick.
- **Split check:** after each damage event, run solid union-find over the touched bricks.
  - If it becomes disconnected, the piece with the most mass keeps the body.
  - Each other piece becomes a new body whose blueprint is: the brushes whose AABB meets the piece's bounds (copied, re-centred) + the damage brushes + `KeepSeedCell`. The bake discards solid not connected to the seed. Seeds are a few bytes, so this stays network-compact and deterministic.
  - A piece of < 2 m³ becomes debris: a convex hull plus a mesh snapshot, no blueprint, despawned on a timer.
- **Components** go with the piece holding their anchor cell. If the anchor cell is destroyed, the component detaches as its own dynamic body.

### LOD

- **Near:** LOD L re-bakes at 0.25·2^L m cells in 8·2^L m bricks, the same octree scheme as `ChunkCoord.Lod` and terrain skirts.
- **LOD ≥ 2:** skips `Interior`-flagged brushes unless they breach the outer surface, so far ships carry no interior triangles at all.
- **Beyond ~2 km:** one whole-ship bake at 2–4 m cells.
- **Beyond ~30 km:** impostor or lit point.
- **L0 budget:** ≤ 400k tris per 100 m ship (~1M raw, ~100k after simplify). L0 mesh memory ~10–15 MB per 100 m ship; only nearby ships hold L0.

### Limits (enforced by the build tool and the server)

| Item | Limit |
| --- | --- |
| Ship extent | ≤ 1 km (float local frame error ≤ 6e-5 m) |
| Hull brushes | ≤ 8192 per ship |
| Damage brushes | ≤ 4096 per ship |
| Brushes overlapping one brick | ≤ 256 |
| Hull cell size | 0.25 m |
| Minimum hull wall / feature | 0.5 m (finer detail belongs in components, at 1/16 m cells, ≤ 8 m bounds) |
| Render triangles | ≤ 8k per brick after simplify; ≤ 400k per ship at L0 |
| Exterior collision | ≤ 2048 convex sub-shapes |
| Interior collision | ≤ 2k tris per brick, near occupants only |
| Brick rebakes | ≤ 16 started per tick across all ships |

### Existing files

| File | Fate |
| --- | --- |
| `src/Terrain/Meshing/SurfaceNetsMesher.cs` | **Change.** Make it generic over a struct `IVertexPlacer` (surface-nets mean for terrain, dual contouring QEF for hulls; zero cost to terrain), and take cell scale as a parameter instead of `chunk.Coord.VoxelScale`. Skirts reused for hull LOD. |
| `src/Terrain/Meshing/ChunkMesh.cs`, `src/Terrain/Meshing/PackedTerrainVertex.cs` | **Reuse as is.** The 12 B packed vertex is scale-agnostic. |
| `src/Terrain/Voxels/VoxelChunk.cs`, `src/Terrain/Voxels/ChunkConstants.cs` | **Reuse** the 34³ Density/Material layout as bake scratch. **Cut** `VoxelChunk.Water` along with the water kill. |
| `src/Terrain/Editing/BrushOp.cs` | **Pattern reused** (ops replayed after generation, band-limited apply loop). Terrain keeps its sphere ops; hulls get `HullBrush`. |
| `src/Terrain.Runtime/ChunkJob.cs` | **Template** for `HullBrickJob`. |
| `src/Physics/StaticMeshShape.cs` | **Reuse** for interior-zone static hull collision. |
| `src/Physics/TriangleBvh.cs` | **Reuse** for build picking; make it public. |
| `src/Physics/PhysicsWorld.cs` | **Add** `AddDynamicCompound(shape, pose, MassMoments)`, `SetShape`, `SetMassProperties`, and compound/section handles. |
| `src/Physics/ObjectLayers.cs` | **Add** ShipExterior and ShipInterior layers. |
| `src/Terrain.Runtime/Rendering/TerrainRenderer.cs`, `src/Terrain.Runtime/Rendering/RangeAllocator.cs` | **Reuse** the arena, staging and indirect-draw code. Push constants become per-draw instance data (ship pose camera-relative from double, plus brick origin and scale). **Cut** the `WaterFogVolume` dependency. |
| `src/Terrain/Voxels/Noise.cs` | **Reuse** for jagged damage edges. |
| **New** | `src/Ships` (Blueprint, Brushes, Bake, Mass, Rooms, Damage, Components), `src/Ships.Runtime` (jobs, collision sections, renderer, build tools), `data/components`, `data/ship-materials`. |

### Open questions for the user

1. **Hull resolution:** is 0.25 m cells with a 0.5 m minimum wall acceptable for hulls? Each halving costs about 4–8× in bake time and memory; finer detail would go into components.
2. **Smooth hulls:** are primitives + convex brushes + bevel / smooth-union enough for the hull shapes you want, or do you need lofted / spline surfaces (Star Citizen-style organic hulls)? Those are a bigger tool and a new brush type.
3. **Damage:** are CSG holes (clean or jagged) enough, or do you want dents and deformation? This representation doesn't do dents.
4. **Player-made components:** do they work as "modelled housing + crafted/bought function core", or should players be able to design function too (e.g. engine performance from nozzle geometry)?
5. **Survival building:** do brushes cost materials by volume and take weld time in place (Space Engineers), or are ships designed freely and then printed at a shipyard?
