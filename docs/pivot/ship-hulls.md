## Ship construction: hull CSG + components

### Recommendation: (d) hybrid. A CSG brush list is the truth, baked to SDF bricks and meshed by dual contouring

A ship is a **blueprint**: an ordered list of compact, quantized CSG brushes (add, subtract, paint), plus a list of component placements. That list is the only thing that gets saved, sent over the network, edited or damaged. Every peer bakes it locally and in parallel:

**Round 5 (D11, full mesh editing):** a brush is either an analytic primitive or a **mesh brush**: a closed, quantized polygon mesh authored in a vertex/edge/face mesh editor (extrude, loop cut, bevel, mirror, subdivision; see "Mesh brushes"). A mesh brush is one more `BrushShape`, so it sits in the same ordered list and takes the same Add / Subtract / Intersect / Paint ops. The brush list stays the only truth, and every boolean, interior cut, damage hole, mass, room and LOD still comes from the SDF bake. The mesh is never boolean'd as polygons.

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
- **(c) Poly/box modelling with boolean subtract *as the truth*:** has (b)'s boolean robustness problems, and meshes don't compress for the network. **Round 5:** the vertex/edge modeller itself is now wanted (D11), so it is kept as an *authoring tool for one brush type* rather than as the representation: each mesh brush is a closed solid that is converted to SDF at bake, and the booleans stay in the SDF. Primitives remain the default tools for players who do not want to model.
- **Space Engineers block grid only:** not "3D modelling"; it can't cut arbitrary interiors.
- **V-HACD convex decomposition:** seconds per ship, not deterministic, has to rerun on every hole.

### Data model (`src/Ships`, no Vulkan dependency; runtime in `src/Ships.Runtime`)

```csharp
namespace BrutalGame.Ships;

public enum BrushShape : byte { Box, Wedge, Cylinder, Capsule, Sphere, Cone, Prism, Convex, Mesh }   // Prism/Convex: Param indexes PlaneTable; Mesh: Param indexes MeshTable
public enum BrushOp    : byte { Add, Subtract, Paint, Intersect }
[Flags] public enum BrushFlags : byte { None = 0, Interior = 1, NoCollide = 2, Damage = 4, Mirrored = 8, Inset = 16, BlowOut = 32 }   // Inset (Mesh only): SDF eroded by Bevel (the wall thickness); BlowOut: an eject or launch panel, evaluated after component cutters

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
    public ushort Param;          // cone taper, PlaneTable index for Prism/Convex, or MeshTable index for Mesh
}

public struct PlaneRef { public short OctU, OctV; public int Dist; }   // 8 B, octahedral normal + 1/1024 m distance; ≤16 per Convex

/// Round 5 (D11): one closed control mesh, shared by every brush whose Param points at it (e.g. the Add brush and its Inset Subtract).
public sealed class HullMesh
{
    public ulong MeshHash;                         // xxHash64 of the canonical bytes below: server "already validated" key
    public NativeList<MeshVertex> Vertices;        // ≤ 4096
    public NativeList<byte> FaceSizes;             // 3..16 corners per face, ≤ 4096 faces
    public NativeList<ushort> Corners;             // vertex indices, CCW seen from outside
    public NativeList<ulong> CreaseBits;           // 1 bit per canonical edge: sharp crease (kept by subdivision, frame member at construction)
    public NativeList<ushort> FacePanel;           // per face: panel index (construction piece, aero strip group), ≤ 4096 panels (a 100 m hull needs ~700 at ≤ 25 m² each)
    public byte SubdivLevels;                      // integer Catmull-Clark, 0..2
}
public struct MeshVertex { public int X, Y, Z; }   // 12 B, brush-local, 1/1024 m, |coord| ≤ 2^18 (256 m), so exact Int128 predicates

public sealed class ShipBlueprint                  // also what a prefab is (hull chunk, component housing, or whole ship)
{
    public ulong ContentHash;                      // xxHash64 of canonical bytes: prefab id + bake cache key
    public NativeList<HullBrush> Brushes;
    public NativeList<PlaneRef> PlaneTable;
    public List<HullMesh> MeshTable;               // round 5: ≤ 256 entries, indexed by HullBrush.Param for Shape == Mesh
    public NativeList<HullChunkGroup> Chunks;      // authoring groups = the user's "hull chunks"
    public NativeList<ComponentPlacement> Components;
    public NativeList<ushort> Materials;           // ids into data/materials
}

public enum ChunkKind : byte { Hull, Armour, Wing }   // round 5: Wing = lifting surface (D10), its planform is measured from the bake
public struct HullChunkGroup { public uint FirstBrushId, LastBrushId; public ulong SourcePrefab; public int NameIndex; public ChunkKind Kind; public ushort AeroProfile; public uint WingFrame; }   // Wing only: AeroProfile = data airfoil index, WingFrame = smallest-three span/chord frame set by the Wing tool

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

public enum SocketType : byte { Mount, Power, Fuel, Data, Coolant, Control }   // round 5 (D17): Control = hard-wired discrete signal (cancel loop, e-stop, eject, arm); see ship-design-and-construction.md "Wiring"
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
2. **Evaluate** each brush only over its own band ∩ brick, in order, into padded 34³ `sbyte` density + `byte` material, plus a `ushort` "winning brush" scratch array. Add = min, Subtract = max(d, −b), Paint = material only, Intersect = max. Smooth union uses Bevel as `k`. Ops are scalar plus SIMD, no trig, fixed order: bit-identical on x64. Correctness never depends on it, though: the server owns mass, rooms and splits. A **Mesh** brush is evaluated by its own path (sign by exact ray parity, distance only in the narrow band; "Mesh brushes" below) and then combined with the same min/max ops, so it counts as one brush against the 256 cap.
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

### Mesh brushes (round 5, D11: full mesh editing, still cuttable)

**Representation.** A mesh brush is a normal `HullBrush` with `Shape = Mesh`: its `Pos`/`Rotation` place the mesh, `Half` is the mesh's AABB half extent (for binning), `Param` indexes `ShipBlueprint.MeshTable`, `Op` and `Material` work as for any brush. The `HullMesh` is a closed polygon control mesh (n-gons of 3–16 corners) in brush-local int32 at 1/1024 m, optional sharp-crease bits per edge, a panel index per face, and 0–2 levels of subdivision.
- **Integer subdivision:** Catmull-Clark in int64 fixed point, rounding every level to the 1/1024 m grid (round half away from zero), creases by the sharp-edge rule. The subdivided mesh is integer, so every peer gets the same triangles, and every predicate on it (orientation, ray crossing, triangle-triangle) is exact in Int128. N-gons triangulate by a canonical ear clip (fixed start corner, fixed tie-break).
- **Several brushes may share one mesh entry.** Mirror, duplicate and Hollow emit brushes that point at the same `HullMesh`, so they add 34 B, not a mesh.

**SDF evaluation in `HullBrickJob`** (only for bricks the brush's AABB + band touches):
1. Per mesh version, once: subdivide, triangulate, build a `TriangleBvh` (Jolt-free `src/Geometry`), cached by `MeshHash`. About 1–3 ms for 64k triangles.
2. **Sign:** one ray per (y, z) row of the brick along the brick's +X axis, starting **outside the mesh's AABB** (not at the brick face, whose inside/outside state is unknown) and running through the whole BVH, crossings sorted; the parity of the crossings before each cell gives inside/outside for every cell of the row. Ray points are the cell centres taken into mesh-local space by the brush's fixed-point rotation (decoded once per brush, deterministic rounding) and rounded to the 1/1024 m grid, so they are integers; ties on edges and vertices are broken by symbolic perturbation, so the sign is exact and deterministic on every peer. A brick with no triangle within its band is uniform and takes a single ray. This is what mass, rooms and splits read.
3. **Distance:** closest-triangle queries only for cells within the band (2 cells, or `Inset + 2` cells for an Inset brush) of a triangle, found by rasterising each triangle's band AABB into the brick. The winning triangle's normal is the Hermite normal, so dual contouring puts the mesh's edges and creases exactly where they are (the same reason primitives get sharp edges).
4. Combine with the running density by the brush's `Op`. Paint, Subtract and Intersect with a mesh work exactly as with a primitive: a sculpted mesh can be a cutter (an intake, a sunken cockpit) and a primitive can cut a mesh hull.

**Still cuttable, "and whatnot".** Because the booleans are in the SDF, a mesh hull loses nothing the primitive hull had:
- **Interiors:** Hollow on a mesh brush emits an `Interior | Inset` Subtract brush pointing at the same mesh, `Bevel` = wall thickness (0.5–2 m): the mesh's SDF eroded by the wall, an exact offset inside the band. Cut room, cut corridor and any primitive or mesh Subtract still work over it.
- **Damage holes, breaches, leaks, splits, repair patches:** unchanged; the damage layer subtracts from whatever the brush list made.
- **In-world cutting:** a cutting torch (boarding, salvage, rescue) writes Subtract brushes into the damage layer along the cut path (a swept capsule per 0.5 m of cut, coalesced by the existing rule). A closed cut loop through a wall makes a hole; a cut across a whole section is a split. Same caps (4096 per ship, 64 per brick).
- **Mass, CoM, inertia, rooms, LOD, collision:** read the bake, so they need no mesh-specific code.
- **Dents:** still not modelled (open question 3), but a mesh brush is the natural place for them later: a dent could displace control vertices in a per-ship damage copy of the mesh.

**Validation (designer locally on every commit; server on save and import; validated `MeshHash`es are cached so an unchanged mesh is never re-checked; the cache keeps the canonical bytes and compares them on every hit, because xxHash64 is not collision resistant and a crafted collision must not pass an invalid mesh):**

| Check | Rule | Severity |
| --- | --- | --- |
| Closed | every edge has exactly two faces | Error |
| Manifold | every vertex's faces form one fan; no duplicate faces | Error |
| Oriented | adjacent faces traverse their shared edge in opposite directions; signed volume > 0 | Error |
| No self-intersection | of the subdivided triangles: BVH pair traversal, exact Int128 triangle-triangle tests | Error |
| Degenerate | no zero-area triangle, no edge under 1/64 m after subdivision | Error |
| Limits | table below | Error |
| Thin features | baked occupancy volume vs exact mesh volume; any region the bake drops (thinner than the 0.5 m wall) is outlined in the editor | Warning |
| Self-overlap between meshes | allowed: CSG resolves it | none |

**Mesh editor (designer tool; the "Hull modelling" tool's Mesh mode):**
- **Select** vertex / edge / face / loop / panel; box and lasso; grow and shrink.
- **Transform** with gizmos, snap to 1/64 m (1/1024 m quantum underneath), axis and normal constraints, proportional falloff (optional).
- **Modelling ops:** extrude (face, region, edge, vertex), inset face, loop cut and slide, edge bevel and vertex bevel (≤ 4 segments), bridge edge loops, merge / weld by distance, dissolve (vertex, edge, face), knife constrained to a plane, fill hole, recalculate normals outward, crease toggle. Ops that would leave the mesh open (delete face, knife without closing) are allowed mid-edit, but the brush cannot be committed until it validates.
- **Mirror:** live mirror across the brush's X, Y or Z plane, seam vertices welded and locked to the plane; committed as one symmetric mesh. The ship-level mirror still emits a second brush sharing the mesh.
- **Subdivision:** 0–2 integer Catmull-Clark levels with creases; the viewport shows cage and result. Level 2 is refused if the result exceeds the triangle limit; the editor also refuses levels whose edges would fall under 0.125 m (finer than the bake can show).
- **Convert:** any primitive brush to an editable mesh (box, wedge, cylinder with N sides, sphere as a quad sphere); "Apply group to mesh" (a group of primitives → one mesh by dual contouring the group's bake and decimating to the limits; lossy, so it is a copy, the originals are kept hidden).
- **Panels:** faces are auto-partitioned into panels (flood fill over face adjacency, stopping at creases, ≤ 25 m² each); the designer can repaint. Panels are construction pieces and aero strip groups.
- **Feedback:** while editing, the cage and its subdivided surface are drawn directly as polygons (cheap, no bake); committing an op bakes the touched bricks, and a toggle shows the baked result over the cage with lost-detail outlines. Each modelling op is one undoable op.

**Limits and costs:**

| Item | Limit or cost (estimates, measure in the "Ships" profiler panel) |
| --- | --- |
| Control mesh | ≤ 4096 vertices, ≤ 4096 faces, 3–16 corners per face, ≤ 4096 panels |
| Subdivided mesh | ≤ 65,536 triangles per mesh; ≤ 8192 mesh triangles overlapping one brick |
| Per ship | ≤ 256 `MeshTable` entries, ≤ 131,072 control vertices total, ≤ 1M subdivided triangles total |
| Mesh bytes | 12 B per vertex + 1 B per face + 2 B per corner + 1 bit per edge + 2 B per face panel: a 1,000-vertex all-quad mesh ≈ 23 KB raw, ≈ 8 KB after delta + zlib; a fully mesh-modelled 100 m hull of ~20k control vertices ≈ 160 KB packed, about 6× a 2k-brush primitive ship |
| Per-mesh prepare | subdivide + BVH, 1–3 ms per 64k triangles, once per `MeshHash` |
| Bake per brick | +0.5–1 ms for sign rows and +0.3–1 ms for the band per mesh brush touching it (≈ 2–3× a primitive-only brick); server skips meshing as before |
| Server validation | ≈ 1 ms topology per 4k faces + 5–30 ms self-intersection per 64k triangles, on low-priority workers, only for new `MeshHash`es; worst case for a whole ship (1M triangles) ≈ 0.5 s CPU, so new-mesh validation also has a per-player budget of 128k triangles per 10 s (ship-design-and-construction.md plan limits): a ship of all-new meshes imports over about 80 s, and 64 players at the cap stay under one core |
| Edit | a vertex move re-bakes the bricks the moved faces' old and new band AABBs touch (often 4–30 for a large mesh, against 1–8 for a primitive); topology ops re-prepare the mesh |

### Hull features for ship systems (round 5)

- **Wings (D10).** A hull chunk of `Kind = Wing` is a lifting surface. Its planform is measured from the bake, not typed in: the chunk's occupancy is projected on its own chord plane and sliced into ≤ 16 spanwise **aero strips** (area, chord, quarter-chord point, local frame), stored next to `BrickState` (≈ 40 B per strip). `AeroProfile` picks a data airfoil (Cl(α), Cd(α), Cm, stall). A damage hole through a wing re-measures the strips of the touched bricks, so a holed wing loses lift on that side. Control surfaces are components hinged on a wing's trailing edge; flight physics are in ship-design-and-construction.md, "Aero".
- **Core ejection channel (D12/D13).** A drive's ejectable core is mounted on an eject cradle whose design carries a **cutter for its eject path**: a swept box from the cradle to the hull surface, ending in a **blow-out panel** (an Add brush flagged `BlowOut`, blown off by a damage-layer subtract at eject; replacing it removes that damage brush). Evaluation order in a brick: hull brushes in list order, then component cutters, then `BlowOut` brushes, then the damage layer, so the cutter opens the channel and the panel still closes its mouth. Validation: the swept core volume is clear of other components' housings and of anything that survives the cutter except its own panel (another `BlowOut` brush); Error otherwise. The ejected core ignores collision with its former ship until it clears the hull AABB.
- **Escape pod bays and station thrusters (D25).** A pod bay is a component whose cutter opens a launch tube to the hull surface (same clear-path rule) ending in an outer hatch (a door portal), so the bay stays a sealed room until launch; station thrusters are ordinary thruster placements.
- **Inertial dampener coverage (D9).** A dampener's bubble covers the rooms (air components of the bake) whose **every** air cell lies inside its radius, plus designer-marked **bay volumes** (boxes) inside the radius for open hangars, which touch space and so are not rooms; bodies entering or leaving a bay box are grid-membership events. The covered list is recomputed with the rooms, on topology change only, and `m_in` on events (exotic-matter.md).
- **Wiring (D17).** Cables are routes (`Medium` Power, Data, Control) laid along hull inner surfaces in trays, or **concealed** inside a hull brush through penetrations (harder to cut, costs a penetration fitting each way). Their segments are tested against damage brushes like framed beams (capsule vs brush); see ship-design-and-construction.md, "Wiring".

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
- **Mesh mode (round 5):** convert a primitive to a mesh or start from a cage, then model vertices, edges and faces (extrude, loop cut, bevel, mirror, subdivision...; "Mesh brushes" above). A mesh brush takes Add / Subtract / Paint like any brush, so modelled shapes and primitive cuts mix freely.
- **Hollow ("cut the interior"):** select Add brushes, give a wall thickness ≥ 0.5 m, and it emits an inset Subtract brush flagged `Interior`. This is the main "interiors cut away" gesture. There is also a "Cut room" tool: drag a box inside the hull.
- **Editing aids:** bevel handle, mirror planes (mirrored brushes are stored as real brushes), select / move / duplicate / group, and save a group as a HullChunk prefab.
- **Viewing aids:** cross-section clip plane, hull X-ray, hide groups.
- **Feedback while dragging:** a GPU-raymarched ghost of the one brush. Committing triggers the bake; nothing is baked per frame while dragging.
- **Undo/redo** is brush list diffs. Edits go to the server as requests, the server validates them (materials, permissions, limits) and broadcasts with a sequence number. (In survival play ship-design-and-construction.md supersedes this: build mode edits plans and the live ship changes through construction steps; this direct path is creative mode.)
- **Components:**
  - Palette of bought prefabs and the player's own designs. Placing raycasts the hull (`TriangleBvh`) and aligns the Mount socket to the surface normal, snapped to the grid.
  - It is valid only if ≥ 70% of the mount footprint has solid hull behind it, sampled against the coarse occupancy.
  - Typed sockets (power, fuel, data) snap within 0.25 m.
  - A component's cutters carve the hull automatically, e.g. door openings.
- **Player-made component** = modelled housing (same tools including Mesh mode, 1/16 m cells, ≤ 8 m, ≤ 16 mesh entries per housing) + a crafted/bought **function core** (data-defined stats). A prefab is just a saved `ShipBlueprint` or `ComponentDesign` keyed by `ContentHash`. Each design is baked once and drawn instanced across every ship.

### Networking and saves

- **On join or stream-in:** send `ContentHash`. The client asks for the blueprint only on a cache miss, then gets the brush array (delta + zlib via Brutal.ZLib). A 2k-brush ship is ~25 KB.
- **Edits and damage:** `{shipId, seq, insert/remove/modify brush at index}` (~40 B), and `{shipId, seq, damage brush}` (34 B).
- **Mesh entries (round 5):** fetched by `MeshHash` like blueprints (a ship that reuses a mesh prefab sends 8 B). A changed mesh goes as `MeshPatch {meshHashOld, firstVertex, count, int32×3 deltas}` when only positions moved, or a whole entry (≤ ~90 KB raw, typically 5–20 KB packed) after a topology op. In the plan model (ship-design-and-construction.md) these ride in the plan diff on save.
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
  - Each other piece becomes a new body whose blueprint is: the brushes whose AABB meets the piece's bounds (copied, re-centred; mesh brushes keep pointing at their `HullMesh` by `MeshHash`, so no mesh bytes are copied) + the damage brushes + `KeepSeedCell`. The bake discards solid not connected to the seed. Seeds are a few bytes, so this stays network-compact and deterministic.
  - A piece of < 2 m³ becomes debris: a convex hull plus a mesh snapshot, no blueprint, despawned on a timer.
- **Components** go with the piece holding their anchor cell. If the anchor cell is destroyed, the component detaches as its own dynamic body.

### LOD

- **Near:** LOD L re-bakes at 0.25·2^L m cells in 8·2^L m bricks, the same octree scheme as `ChunkCoord.Lod` and terrain skirts.
- **LOD ≥ 2:** skips `Interior`-flagged brushes unless they breach the outer surface, so far ships carry no interior triangles at all.
- **Mesh brushes at LOD ≥ 1** use one subdivision level fewer (level 0 at LOD ≥ 2): the SDF is re-baked at coarser cells anyway, so the finer cage would be wasted triangles in the BVH. Sign comes from the reduced mesh too, which can shift coarse occupancy by under one LOD cell; mass and rooms always come from the L0 bake, so this is visual only.
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
| Mesh brushes (round 5) | ≤ 256 `MeshTable` entries; ≤ 4096 control vertices and faces each; ≤ 65,536 subdivided triangles each; ≤ 131,072 control vertices and ≤ 1M subdivided triangles per ship; ≤ 8192 mesh triangles per brick |
| Mesh inset (Hollow wall) | 0.5–2 m |
| Wing aero strips (round 5) | ≤ 16 per Wing chunk, ≤ 16 Wing chunks per ship |
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
| `src/Physics/TriangleBvh.cs` | **Reuse** for build picking; make it public. Round 5: moved to Jolt-free `src/Geometry` (ship-design-and-construction.md) and also used for mesh-brush sign rays, band distance and self-intersection checks; needs integer (Int128) exact ray and triangle-triangle predicates beside its float queries. |
| `src/Physics/PhysicsWorld.cs` | **Add** `AddDynamicCompound(shape, pose, MassMoments)`, `SetShape`, `SetMassProperties`, and compound/section handles. |
| `src/Physics/ObjectLayers.cs` | **Add** ShipExterior and ShipInterior layers. |
| `src/Terrain.Runtime/Rendering/TerrainRenderer.cs`, `src/Terrain.Runtime/Rendering/RangeAllocator.cs` | **Reuse** the arena, staging and indirect-draw code. Push constants become per-draw instance data (ship pose camera-relative from double, plus brick origin and scale). **Cut** the `WaterFogVolume` dependency. |
| `src/Terrain/Voxels/Noise.cs` | **Reuse** for jagged damage edges. |
| **New** | `src/Ships` (Blueprint, Brushes, Bake, Mass, Rooms, Damage, Components), `src/Ships.Runtime` (jobs, collision sections, renderer, build tools), `data/components`, `data/ship-materials`. Round 5: `src/Ships/Meshes/` (`HullMesh.cs`, `MeshCanonical.cs` (canonical bytes, `MeshHash`), `IntSubdivision.cs` (integer Catmull-Clark, creases), `MeshSdf.cs` (sign rows, band distance, inset), `MeshValidate.cs`), `src/Ships/AeroStrips.cs` (wing planform from the bake), `src/Designer/Tools/MeshTool.cs` (editor ops, each an invertible op), `data/aero/airfoils.json`. |

### Open questions for the user

1. **Hull resolution:** is 0.25 m cells with a 0.5 m minimum wall acceptable for hulls? Each halving costs about 4–8× in bake time and memory; finer detail would go into components.
2. ~~**Smooth hulls**~~ (decided, D11: full mesh editing, still cuttable). Answered by mesh brushes with integer subdivision and creases; organic hulls are subdivision cages. Lofted / spline surfaces are not planned.
   - **New sub-question:** mesh detail finer than the 0.25 m cell (thin fins, small chamfers) is lost in the bake and flagged by the thin-feature warning. Is that acceptable, or should small craft (≤ 30 m) get an optional 0.125 m hull cell (about 4–8× bake time and memory for those ships)?
3. **Damage:** are CSG holes (clean or jagged) enough, or do you want dents and deformation? This representation doesn't do dents.
4. ~~**Player-made components**~~ (answered by ship-design-and-construction.md: function is derived from a part graph, not from housing geometry).
5. ~~**Survival building**~~ (answered by ship-design-and-construction.md: plans, bought or made parts, and construction steps; creative mode builds instantly).

## Revision (round 5) notes

User decisions round 5 (decisions-r5.md) applied in place; they win over earlier text.

- **D11, full mesh editing, still cuttable.** New `BrushShape.Mesh` referencing a shared `HullMesh` in `ShipBlueprint.MeshTable`: closed n-gon control mesh in brush-local int32 1/1024 m, crease bits, face panels, 0–2 levels of integer Catmull-Clark. The CSG brush list stays the only truth; mesh brushes take Add / Subtract / Intersect / Paint and are converted to SDF at bake (exact ray-parity sign per brick row, narrow-band closest-triangle distance, triangle normals as Hermite data), so booleans, interiors, damage holes, mass, rooms, collision and LOD come from the bake unchanged. Rejected option (c) reworded: the modeller is now an authoring tool for one brush type, not the representation.
- **Hollow on meshes** uses a new `BrushFlags.Inset` Subtract brush sharing the mesh, eroding its SDF by the wall thickness (0.5–2 m).
- **In-world cutting** (cutting torch for boarding, salvage, rescue) writes swept-capsule Subtract brushes into the damage layer; works on primitives and meshes alike.
- **Mesh validation** table (closed, manifold, oriented, no self-intersection with exact Int128 tests, no degenerates, limits; thin-feature warning), done locally on commit and on the server only for new `MeshHash`es.
- **Mesh editor** tool list (select modes, snapped transforms, extrude, inset, loop cut, bevel, bridge, merge, dissolve, knife, fill, crease, live mirror, subdivision, convert primitive / group to mesh, panels) and its preview / commit model.
- **Mesh limits and costs** (vertices, faces, triangles per mesh, per ship and per brick; bytes; prepare, bake, validation and edit costs, all estimates) added to the section and to the Limits table; mesh network messages (`MeshHash` fetch, `MeshPatch`); LOD drops one subdivision level per LOD step.
- **Ship-system hull features:** `ChunkKind` (Hull, Armour, Wing) with `AeroProfile`; wing aero strips measured from the bake (D10); core eject path cutter and blow-out panel with a clear-path rule (D12/D13); escape pod bay launch tubes and station thrusters as placements (D25); dampener bubble room coverage (D9); `SocketType.Control` and concealed vs tray wiring with damage tests (D17). Behaviour of each lives in ship-design-and-construction.md.
- Open question 2 marked decided; a sub-question on an optional 0.125 m cell for small craft added. Files table: `src/Ships/Meshes/`, `AeroStrips.cs`, `MeshTool.cs`, `data/aero/airfoils.json`; `TriangleBvh` gains exact integer predicates.

## Round 5 review notes

Skeptical lead review, 2026-11-25, against decisions-r5.md and the round 5 copies of ship-design-and-construction.md, exotic-matter.md, warp.md and frames-and-zones.md. Decisions were not re-litigated; every fix below is made in place above.

- **D11 faithful:** full vertex / edge / face editing exists, and hulls stay cuttable (booleans, Hollow, damage, cutting torch) because the brush list stays the truth and meshes go through the SDF. Compact (mesh by hash, `MeshPatch`), deterministic and server-validated, as asked.
- **Sign rays were wrong:** a parity ray started at the brick's −X face cannot know whether it starts inside. Rays now start outside the mesh AABB; their points are integer (fixed-point rotation, rounded to 1/1024 m) so the "exact Int128" claim holds for rotated brushes; uniform bricks take one ray (perf).
- **Panel index overflow:** `FacePanel` was a byte (≤ 255 panels) but panels are ≤ 25 m², so a 100 m single-mesh hull (~16,000 m², ~700 panels) could not be partitioned. Now `ushort`, ≤ 4096; mesh bytes +1 B per face.
- **Exploit, validated-mesh cache:** keyed by xxHash64 alone, a crafted collision could pass an invalid mesh. The cache now byte-compares on hit, like the plan store.
- **Perf, server validation:** 0.5 s per ship "within the import rate limit" is 3.2 cores at 64 players. Added a per-player budget of 128k new triangles per 10 s (≈ 65 ms), matching ship-design-and-construction.md's plan limits.
- **D12/D13 channel:** "an Add brush flagged as the panel" had no flag and no evaluation order, so component cutters would have cut the panel away too. Added `BrushFlags.BlowOut` and the order: hull, cutters, `BlowOut`, damage. The ejected core ignores its own ship's collision until clear.
- **D25 pod bays:** a launch tube open to the hull surface would breach the bay room permanently; it now ends in an outer hatch.
- **D9 coverage:** "rooms whose cells lie inside" was ambiguous, and open hangars (exotic-matter covers docked craft) are not rooms. Now: every air cell inside, plus designer-marked bay volumes.
- **D10 wings:** the "chord plane" had no stored frame; `HullChunkGroup.WingFrame` added (set by the Wing tool).
- **Leftovers:** `Param` comment names Mesh; splits keep mesh references by hash; open questions 4 and 5 marked answered by ship-design-and-construction.md; the direct-edit networking path is noted as creative mode under the plans model.
- **D17 wiring:** concealed vs tray cables and capsule damage tests are consistent with ship-design-and-construction.md "Wiring"; nothing changed.
