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
  - Damage: holes are just more subtract brushes. Dents and bends (round 6, D59) are a small displacement map applied on top of the bake, never baked in ("Damage and splitting").
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

public enum SocketType : byte { Mount, Power, Fuel, DataIn, DataOut, Coolant }   // round 6 (D71): wiring is power and data only, and a data cable is one-way, so a data socket is an input or an output (round 5's Control socket is gone); see ship-design-and-construction.md "Wiring"
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
    // Round 6 (D59, combat-and-sensors.md section 2):
    public byte Material;                          // coarse material of the brick's solid cells; 0xFF = mixed, then 2 bits per coarse cell into a 4-entry palette in a side pool (1 KB, mixed bricks only)
    public int MaterialPalette;                    // side-pool handle when Material == 0xFF
    public byte DeformGroup;                       // which DeformGroup this brick moves with (0 = root); a brick in a hinge band also names the far group in the hinge
    public ushort DentFirst; public byte DentCount; // ≤ 16 dents touching this brick, a run in ShipHull.Deform.BrickDents
}

// ship-atmosphere.md replaces Room, EdgeKind and RoomEdge with ShipAir (its section 10); the three below are kept as the round 5 sketch.
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
    public HullDeformation Deform;                 // round 6 (D59): dents, hinges, deform groups. Null until the first dent or bend
    public DamageAccum Accum;                      // round 6: server only, only while under fire, never saved (combat-and-sensors.md)
}

// ---- Round 6 (D59): dents and deformation. Semantics, sizes from energy, merge rules: combat-and-sensors.md section 2. ----
public enum DentKind : byte { Dish, Crease, Bulge }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HullDent                              // 28 B, material space (the undeformed bake's coordinates)
{
    public uint Id;                                // server allocated; dents sum in Id order
    public int PosX, PosY, PosZ;                   // 1/1024 m
    public ushort Dir;                             // octahedral 16 bit: displacement axis
    public ushort Radius;                          // 1/256 m
    public short Depth;                            // 1/1024 m, + inward, − a bulge
    public DentKind Kind; public byte Group;       // Group: the DeformGroup it rides
    public ushort HalfLength, LenDir;              // Crease only: half length 1/256 m, octahedral direction
}
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HullHinge                             // 36 B: a yielded structural cut (tractor-beams.md structure graph)
{
    public uint CutId; public PlaneRef Plane;      // the cut plane, material space
    public int Cx, Cy, Cz;                         // cut centroid, 1/1024 m
    public ushort Axis; public short AngleQ;       // octahedral axis; angle in 1/1024 rad (≤ 12°)
    public short ShearX, ShearY, ShearZ;           // 1/1024 m (≤ 1 m)
    public byte GroupA, GroupB;
}
public struct DeformGroup { public uint Rotation; public int OffX, OffY, OffZ; public MassMoments Mass; }   // rigid transform relative to group 0 (smallest-three, 1/1024 m); Mass: this group's share of the undeformed bake (server)
public sealed class HullDeformation
{
    public NativeList<HullDent> Dents;             // ≤ 1024 per ship
    public NativeList<ushort> BrickDents;          // per-brick dent index runs (BrickState.DentFirst / DentCount)
    public NativeList<HullHinge> Hinges;           // ≤ 7
    public NativeList<DeformGroup> Groups;         // ≤ 8; [0] is the root (identity)
}
```

### Evaluation and bake (`HullBrickJob`, modelled on `ChunkJob`)

1. **Gather** the brushes whose AABB plus band overlaps the brick (`BrushBins`), in list order, adding cutters from placed components. Cap: **256 brushes per brick**; the build tool refuses placements past it.
2. **Evaluate** each brush only over its own band ∩ brick, in order, into padded 34³ `sbyte` density + `byte` material, plus a `ushort` "winning brush" scratch array. Add = min, Subtract = max(d, −b), Paint = material only, Intersect = max. Smooth union uses Bevel as `k`. Ops are scalar plus SIMD, no trig, fixed order: bit-identical on x64. Correctness never depends on it, though: the server owns mass, rooms and splits. A **Mesh** brush is evaluated by its own path (sign by exact ray parity, distance only in the narrow band; "Mesh brushes" below) and then combined with the same min/max ops, so it counts as one brush against the 256 cap.
3. **Damage layer:** subtract brushes with a jagged edge from `Noise.cs` (seeded by brush Id).
4. **Outputs:**
   - **All peers:** `MassMoments`, coarse 0.5 m solid/air bitsets, local connected-component labels, and (round 6) the **coarse material**: one byte when every solid coarse cell of the brick has the same material (the common case), else a 2-bit index per coarse cell into a 4-entry palette (majority material of the cell's fine samples; a fifth material in one brick maps to the nearest palette entry by `e_pen`). The hit test of combat-and-sensors.md reads it.
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
- **Life support:** 0 at rest (ship-atmosphere.md: connected rooms at equal pressure are one lazily settled zone; only rooms out of equilibrium are stepped, at 10 Hz). Was: walks the room graph, ~1 µs per room.
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
- **In-world cutting:** a cutting torch (boarding, salvage, rescue) writes Subtract brushes into the damage layer along the cut path (a swept capsule per 0.5 m of cut, coalesced by the existing rule). A closed cut loop through a wall makes a hole; a cut across a whole section is a split. Same caps (4096 per ship, 64 per brick). Station grids are cut the same way: boarding a station to reach its `CommandCore` is the D27 capture path (ship-design-and-construction.md; factions-and-law.md "Sieges and capture"). Cutting into another party's grid without consent is a hostile act and writes an `IncidentRecord` (kind `Boarding`; factions-and-law.md owns the record).
- **Mass, CoM, inertia, rooms, LOD, collision:** read the bake, so they need no mesh-specific code.
- **Dents and bends (round 6, D59):** modelled as a displacement map over the bake ("Damage and splitting"), so they work on mesh and primitive hulls alike. The earlier idea of displacing control vertices in a per-ship copy of the mesh is withdrawn (mesh brushes only, up to 90 KB per ship per mesh, a BVH rebuild per dent).

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

- **Wings (D10; superseded by D85: lift from shape, ship-design-and-construction.md "Aero").** A hull chunk of `Kind = Wing` is a lifting surface. Its planform is measured from the bake, not typed in: the chunk's occupancy is projected on its own chord plane and sliced into ≤ 16 spanwise **aero strips** (area, chord, quarter-chord point, local frame), stored next to `BrickState` (≈ 40 B per strip). `AeroProfile` picks a data airfoil (Cl(α), Cd(α), Cm, stall). A damage hole through a wing re-measures the strips of the touched bricks, so a holed wing loses lift on that side. Control surfaces are components hinged on a wing's trailing edge; flight physics are in ship-design-and-construction.md, "Aero".
- **Core ejection channel (D12/D13).** A drive's ejectable core is mounted on an eject cradle whose design carries a **cutter for its eject path**: a swept box from the cradle to the hull surface, ending in a **blow-out panel** (an Add brush flagged `BlowOut`, blown off by a damage-layer subtract at eject; replacing it removes that damage brush). Evaluation order in a brick: hull brushes in list order, then component cutters, then `BlowOut` brushes, then the damage layer, so the cutter opens the channel and the panel still closes its mouth. Validation: the swept core volume is clear of other components' housings and of anything that survives the cutter except its own panel (another `BlowOut` brush); Error otherwise. The ejected core ignores collision with its former ship until it clears the hull AABB.
- **Escape pod bays and station thrusters (D25).** A pod bay is a component whose cutter opens a launch tube to the hull surface (same clear-path rule) ending in an outer hatch (a door portal), so the bay stays a sealed room until launch; station thrusters are ordinary thruster placements.
- **Station command core (D27).** Every `PlanClass.Station` carries a `CommandCore` component (the ownership token; family, rating and panel in ship-design-and-construction.md). Its housing must sit inside a room (an air component of the bake), because capture is an `Override` held at the core with no defender contesting inside that room's `GridVolume`; Error otherwise. The room is reached like any other: doors, docking, or the cutting torch.
- **Inertial dampener coverage (D9).** A dampener's bubble covers the rooms (air components of the bake) whose **every** air cell lies inside its radius, plus designer-marked **bay volumes** (boxes) inside the radius for open hangars, which touch space and so are not rooms; bodies entering or leaving a bay box are grid-membership events. The covered list is recomputed with the rooms, on topology change only, and `m_in` on events (exotic-matter.md). Covered rooms feel `g_floor + a_man·max(0, 1 − a_d/|a_man|)` (frames-and-zones.md; `a_d`, `a_max`, `MassMax` in exotic-matter.md): the dampener cancels manoeuvre-type acceleration (thrust, tractor forces, throat drag) up to `a_d` and leaks the excess; plating only adds floor gravity; uncovered rooms feel all of `a_man`. Hangar tractors acting on a visitor's craft in a bay need the visitor's landing request (D26 consent, tractor-beams.md).
- **Wiring (D17, D71).** Cables are routes of two media, `Power` and `Data` (round 6, D71: no separate Control medium; a data cable is one-way, from a `DataOut` socket to any number of `DataIn` sockets), laid along hull inner surfaces in trays, or **concealed** inside a hull brush through penetrations (harder to cut, costs a penetration fitting each way). Their segments are tested against damage brushes like framed beams (capsule vs brush), and against dents and hinge bands by combat-and-sensors.md's rule (a segment crossing a dent of ≥ 0.25 m is damaged, ≥ 0.6 m cut); see ship-design-and-construction.md, "Wiring".
- **Shield emitters (round 6, D55).** Ship shields are components, defined in combat-and-sensors.md section 3 (families in ship-design-and-construction.md). What the hull gives them:
  - **Particle shield emitters** are exterior components. Each covers the hull facing within 60° of its **mount normal**, so where the designer mounts them is the coverage. The bake keeps a per-ship `FacingTable` (14 directions, the `ShieldTable` directions, each listing the emitters that cover it), rebuilt on component or topology change only. A dent of ≥ 0.25 m inside a panel's facing, or a hinge between the emitter and its facing, cuts its area until a re-tune.
  - **Energy shield projectors** may sit anywhere on the grid; their bubble is the ellipsoid of the grid's bounds plus 5 m, read from the hull AABB.
  - **Gravity shield emitters** are exterior, gimballed, and mount like a tractor emitter: the mount footprint must carry 1.5 × the class force (tractor-beams.md's mount table), checked against the structure graph.
  - Emitters take damage like any component (the shared Integrity rule) and detach by the anchor-cell rule.
- **Suit lockers (round 6, D64).** EVA suits are not worn by default, so `SuitLocker` components (ship-design-and-construction.md, "Suit lockers") stand beside airlocks, in hangars and in crew rooms. To the hull they are ordinary interior placements against a wall (the 70 % mount rule), with a Power socket and ship-atmosphere.md's `Air` socket. The designer's reach checks (an airlock needs one in the room on its ship side or within 10 m of its inner door; ship-atmosphere.md's validation rows) read the room labels and placement positions; nothing is stored in the hull for them.

### Moving parts: rotors and hinges (designer, 2026-11-27)

- **Data.** `HullJointSet` (plan format 13): up to 16 joints, each a kind (rotor or hinge), a pivot (1/1024 m), a snap rotation whose +Y is the axis, the joint it is mounted on (0 = the ship), and its motor: speed (0.1 rpm or 0.1°/s), torque (N·m), and for a hinge its limits and target (0.1°). Members map plan solids to joints; a solid in no joint is the ship's own. Joints nest at most 3 deep. Not wired yet: the motor settings are the drive.
- **Moving part = sub-hull.** Each joint's solids bake and mesh on their own (`JointGroups`), so a cut in one part never cuts another. Strokes, windows, decals and placements go with the solid under them; hallways stay with the ship. Mass is the sum of the parts.
- **Preview.** "Play joints" (P) turns each part about its axis: a rotor at its speed, a hinge from its lower limit to its upper one and back. The renderer turns a part's bricks with a per-draw rotation; paint, decals and finishes are worked out at rest, so they move with it.
- **Next.** Physics: each part a Jolt body with a hinge constraint and motor, on the wire as joint angles.
- **Air (D85).** Each part also has its own air table (lift from shape), so hinge and rotor parts are the control surfaces.
- **Sliders.** A third kind: the part slides along the axis between limits (travel in mm, speed in mm/s, force in N). Plan format 14.

### Doors and docking ports (designer, 2026-11-27, D86)

- **Components.** A door or docking port is a component plan: its frame or collar (solids), its leaf or hatch on a hinge or slider joint (solids under the joint), and an **opening** (`HullOpeningSet`, plan format 14, up to 16): a door profile (`ProfileKind.Door`, closed outline, X across, Y up from the sill), an origin on the sill, a snap rotation (Z through) and a cut depth each way. Kinds: `Door` and `DockingPort` (the port's face is the opening's plane, facing +Z).
- **Cuts.** The opening's prism cuts the component's fixed part (never a moving leaf). Placed in a ship, the same prism cuts the wall (a Room-role solid on a layered hull), besides the component's bounding-box bay. Generated ids `0xB0000000 | owner << 4 | opening` (owner 0: the plan's own; placement index + 1).
- **Leaves move in the ship.** A placed component's joints become ship groups after the plan's own (ids `0xA0000000 | placement << 5 | group`), placed with it and mounted on what it sits on; their parts are drawn from the prefab's per-part surface, binned on the spot (no grid bake, no worker). They get air tables like any part.
- **Docking.** Genderless: two ports mate face to face when their profiles are the same shape (the designer shows a four-digit port type); a lopsided outline is flagged, since two of it would not line up mirrored. In a ship, port faces are outlined, red when the hull stands within 2 m in front of one.
- **Catalog:** swing door and sliding door (1 x 2.1 m), docking port (1.4 m octagon). Add > Door or Docking port in a ship places one facing out of the wall clicked, upright; in the component editor Add > Doorway or Docking port face, and the joints.
- **Not yet:** door state on the wire and in the room graph (portals), powered and wired opening, docking itself (alignment, latching, the combined bake of D85).

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
- **Dents and bends (round 6, D59):** the convex hulls are built in material space, then the points (≤ 64 each) of the 4 m cells a dent touches are displaced by the dent map and those hulls rebuilt: one `ModifyShape` per section, as for a hole. Sections are children of the `MutableCompound`, so a section takes its `DeformGroup`'s rigid transform as its child transform; sections inside a hinge band are rebuilt with blended points.

**Interior (walking, loose items, small craft in hangars):** the ship is its own physics zone. Inside that grid the hull is a **static** body built from the simplified render mesh via `StaticMeshShape`, only for bricks within 32 m of an occupant (streamed like terrain), ≤ 2k tris per brick. Round 6: its vertices are displaced by the dent and group map when a brick's interior mesh is built or rebuilt, so a dented wall is dented to walk into. This fits the solar > planet > ship grid design and keeps interior precision local. Grid entry/exit rules belong to the zones design.

**Framework proposal (separate, not done here):** ask upstream BRUTAL to ship a native Jolt with the missing MeshShape and NarrowPhase exports. Until then, `TriangleBvh` serves build-tool picking against hull bricks.

### Authoring UX (build mode, ship-local grid)

- **Place a primitive** (Box, Wedge, Cylinder, Capsule, Sphere, Cone, Prism by drawing a 2D convex outline, Convex), then drag faces with gizmos. Grid snap 1/8, 1/4, 1/2 or 1 m; rotation snap 15° (free with a modifier). Mode toggle: Add / Subtract / Paint.
- **Mesh mode (round 5):** convert a primitive to a mesh or start from a cage, then model vertices, edges and faces (extrude, loop cut, bevel, mirror, subdivision...; "Mesh brushes" above). A mesh brush takes Add / Subtract / Paint like any brush, so modelled shapes and primitive cuts mix freely.
- **Hollow ("cut the interior"):** select Add brushes, give a wall thickness ≥ 0.5 m, and it emits an inset Subtract brush flagged `Interior`. This is the main "interiors cut away" gesture. There is also a "Cut room" tool: drag a box inside the hull.
- **Editing aids:** bevel handle, mirror planes (mirrored brushes are stored as real brushes), select / move / duplicate / group, and save a group as a HullChunk prefab.
- **Viewing aids:** cross-section clip plane, hull X-ray, hide groups.
- **Feedback while dragging:** a GPU-raymarched ghost of the one brush. Committing triggers the bake; nothing is baked per frame while dragging.
- **Undo/redo** is brush list diffs. Edits go to the server as requests, the server validates them (materials, permissions, limits; "permissions" means the grid's `HullTitle` holder, its organisation's rights, or build-site rules: factions-and-law.md) and broadcasts with a sequence number. (In survival play ship-design-and-construction.md supersedes this: build mode edits plans and the live ship changes through construction steps; this direct path is creative mode.)
- **Components:**
  - Palette of bought prefabs and the player's own designs. Placing raycasts the hull (`TriangleBvh`) and aligns the Mount socket to the surface normal, snapped to the grid.
  - It is valid only if ≥ 70% of the mount footprint has solid hull behind it, sampled against the coarse occupancy.
  - Typed sockets (power, fuel, data) snap within 0.25 m.
  - A component's cutters carve the hull automatically, e.g. door openings.
- **Player-made component** = modelled housing (same tools including Mesh mode, 1/16 m cells, ≤ 8 m, ≤ 16 mesh entries per housing) + a crafted/bought **function core** (data-defined stats). A prefab is just a saved `ShipBlueprint` or `ComponentDesign` keyed by `ContentHash`. Each design is baked once and drawn instanced across every ship.

### Networking and saves

- **On join or stream-in:** send `ContentHash`. The client asks for the blueprint only on a cache miss, then gets the brush array (delta + zlib via Brutal.ZLib). A 2k-brush ship is ~25 KB.
- **Edits and damage:** `{shipId, seq, insert/remove/modify brush at index}` (~40 B), and `{shipId, seq, damage brush}` (34 B). Round 6 (D59): `HullDentMsg` (28 B, add, change or remove one dent) and `HullBendMsg` (36 B hinge + the new group transform, followed by the server's mass properties, ~60 B), reliable, in the same per-ship sequence as damage brushes (combat-and-sensors.md "Storage, network, determinism").
- **Mesh entries (round 5):** fetched by `MeshHash` like blueprints (a ship that reuses a mesh prefab sends 8 B). A changed mesh goes as `MeshPatch {meshHashOld, firstVertex, count, int32×3 deltas}` when only positions moved, or a whole entry (≤ ~90 KB raw, typically 5–20 KB packed) after a topology op. In the plan model (ship-design-and-construction.md) these ride in the plan diff on save.
- **Determinism:**
  - **Server-authoritative:** mass, rooms, splits and collision. Clients' local values are used only for prediction and visuals.
  - **Deterministic by construction anyway:** quantized inputs, fixed order, no trig, Int128 moments.
- **Saves:** the same bytes. The bake cache on disk is optional and keyed by hash. Dents, hinges and group transforms are saved with the damage layer (≤ 28 KB + 252 B + 128 B per ship); `DamageAccum` is not saved (it is flushed into dents and leak area first). Dents and bends never touch the bake cache.

### Mass, volume, inertia

- Per coarse sample: occupancy fraction from density × material density (kg/m³, data-defined, representing a structural composite, not solid steel).
- Accumulate `MassMoments` per brick in fixed point, sum over bricks, add components via the parallel-axis theorem, and convert once to Jolt `MassProperties` (`SetMass`, `SetInertia`) with the CoM offset.
- Interior Subtract brushes remove mass by construction.
- Room volume = air cell count × 0.125 m³.
- **Round 6 (D59):** mass always comes from the **undeformed** bake, so a dent or bend never changes it (a dent moves material, it removes none). A bend moves whole groups: the server recomputes the centre of mass and inertia from each `DeformGroup`'s moments and transform on the bend event and sends them (review.md X3). A room's volume is reduced by the dent volume inside it, as one number per room.

### Rooms and pressurisation

ship-atmosphere.md owns gas, runtime rooms and the flow law (D38-D42, D60-D66), and wins where the bullets below differ. Its changes to this section: rooms are **design rooms** and never merge with space at runtime (a hole is an opening found from `Air & ~AirDesign`, one extra bitset on damaged bricks only); rooms and `GridVolume` share one labelling; cells opened by damage belong to the adjacent room; ≤ 255 rooms and ≤ 1,024 edges per grid; nothing runs per tick at rest.

- **Air grid:** the coarse 0.5 m grid. A cell is air only if all its fine samples are air.
- **Labelling:** per-brick labels plus union-find across bricks. The component touching the ship-bounds margin is space; every other component is a sealed room.
- **Doors and vents** are portals. Their cells count as solid when the door is closed, and their open state is an edge in the room graph.
- **Recompute** only on topology change (edit or damage), and only the touched bricks plus the global union-find (~0.1 ms).
- **Breaches:**
  - A hole of ≥ 0.5 m merges a room with space and creates a Breach edge with the damage brush's cross-section area.
  - A smaller hole: if the damage brush's AABB touches both room cells and space cells, it becomes a Leak edge with area πr².
  - **Round 6 (D59):** damage below brush size and dents make `Leak` edges without any re-bake: pinhole area from `DamageAccum` adds straight to the edge between the two regions it joins, and a deep dent on a room wall (`Depth / Radius ≥ 0.2`) or a hinge band opens seam leaks (areas in combat-and-sensors.md; flow in ship-atmosphere.md).
- **Per tick:** nothing at rest. Gas flow over edges is ship-atmosphere.md's event-driven step (rooms out of equilibrium only, 10 Hz).

### Damage and splitting

**Round 6 (D59): hull damage has three forms, holes, dents and deformation.** combat-and-sensors.md section 2 owns what causes each (the hit law, contact and ram hits, the impulsive structural pass), how big it is, what it does to doors, seals, components and crew, and how it is repaired. This doc owns what the hull **stores** and how each form reaches the bake, the mesh, collision, mass, splits, LOD and saves.

| Form | What the hull stores | Where | Size and caps | In the bake? |
| --- | --- | --- | --- | --- |
| **Hole** | A jagged Subtract `HullBrush` (sphere, ellipsoid or capsule), in material space | `ShipHull.Damage` | 34 B; ≤ 4096 per ship, ≤ 64 per brick | Yes: the touched bricks re-bake |
| Damage too small for a brush (pits, pinholes, shallow dents) | Removed volume, centroid, dent volume vector and pinhole area per 2 m cell | `ShipHull.Accum` (`DamageAccum`), server only, while under fire | 48 B × ≤ 256 active cells; dropped after 60 s without a hit; not saved | No. It emits a brush, a dent or `Leak` area when a cell crosses a threshold |
| **Dent** | A `HullDent` displacement record: position, axis, radius, depth, kind (Dish, Crease, Bulge), group | `ShipHull.Deform.Dents`, plus a per-brick index run | 28 B; ≤ 1024 per ship, ≤ 16 per brick | No |
| **Deformation** (a bend) | A `HullHinge` per yielded structural cut, a `DeformGroup` rigid transform per side, and a group id per brick | `ShipHull.Deform.Hinges` / `Groups`, `BrickState.DeformGroup` | 36 B per hinge, ≤ 7; 16 B per group transform, ≤ 8; 1 B per brick | No |
| (needed by all three) | Coarse material per 0.5 m solid cell | `BrickState.Material` (+ palette) | 1 B per brick, or 2 bits per cell for mixed bricks | Yes: an output of the bake |

- **Material space.** The brush list, the bake and its cache, occupancy, rooms, air, wiring and the structure graph all stay in the **undeformed** hull's coordinates. A dent or bend is a map from that space to ship space, `x = T_g(X + Σ u_k(X))` (dent displacements `u_k` summed in `Id` order and clamped to 2 m, then the brick's group transform, blended across a 4 m hinge band), applied only where shape is seen or touched:
  - **Render:** in the hull vertex shader (`hull.vert`), from a per-ship dent buffer and group-transform buffer; the per-brick instance record carries its dent index run (≤ 16) and its one or two group ids. Normals come from the analytic gradient. No re-mesh, no vertex upload, no bake.
  - **Exterior collision:** displaced convex-hull points and per-section child transforms ("Collision").
  - **Interior collision:** displaced vertices when a brick's walk mesh is built.
  - **World-to-hull queries** (a character's room, a hit point, a pick ray) map back by `X ≈ x − Σu(x)` after the inverse group transform (first order, ~50 ns).
- **Why not in the bake:** a brush list adds and subtracts, and a dent moves material without removing it. Resampling the SDF at displaced points would need a 2 m pad per brick (about 3× the bake), would invalidate the bake cache on every dent and would drift mass. The map costs no bake and conserves mass by construction.
- **Damage brushes are stored in material space**, so a hole made before a dent moves with the plate, and one made after lands where it was hit (the hit point is mapped back first).
- **Per-tick cost: still zero.** Dents and bends are events; nothing about them runs per tick on the CPU. The vertex shader pays ≤ 16 dent evaluations for bricks that have dents and none for the rest.

**Holes**
- Subtract brushes in the damage layer: sphere, ellipsoid or capsule (a perforation along a ray, the cutting torch's swept capsule), jagged, radius ≥ 0.25 m. Repair = remove the damage brush or add a patch brush.
- **Sources:** weapon perforations and craters, AM breaches, contacts and rams, tears (below), the cutting torch, blow-out panels. Smaller results never make a brush: they accumulate in `DamageAccum`, which emits one 0.25 m brush when a 2 m cell has lost 0.065 m³, and adds pinhole area straight to a `Leak` edge with no re-bake (combat-and-sensors.md "Form 1: holes").
- **Throttle:** ≤ 8 new damage brushes per ship per second (largest first, the rest merge into the accumulator), under the existing limit of 16 brick re-bakes started per tick across all ships.
- **Coalescing:** when overlap is > 50%, merge into the bounding sphere. Caps: 4096 per ship, 64 per brick.

**Dents**
- A `HullDent` displaces material along its axis by `Depth · w(ρ / Radius)`, `w(s) = (1 − s²)²`, within a slab along the axis (combat-and-sensors.md "Form 2: dents" has the full function). The displacement depends only on the distance from the axis, so it is a shear with zero divergence: volume is conserved.
- **Index:** adding, merging or removing a dent rewrites the `BrickDents` runs of the bricks its bounding box touches (≤ 16 per brick; past the cap the smallest dent in that brick merges into its nearest neighbour). ≤ 4 dent records created or changed per ship per second.
- **Tear:** at `Depth / Radius > 0.35` the panel ruptures: the record stays and a hole brush of radius `0.3 · Radius` is emitted at its centre, so a dent can become a breach.
- **Glazing never dents:** a dent whose radius covers a glazing-material cell shatters it to a hole instead.

**Deformation (bends)**
- The structure graph (tractor-beams.md: 4 m nodes, joints, cut capacities, built lazily from the same occupancy) yields a cut at 80 % of its capacity before it breaks. A yielded cut is a `HullHinge`: the bricks on its far side form a `DeformGroup` that rotates (≤ 12°) and shears (≤ 1 m) about the cut. Past either limit, at 8 groups, or where a brick would touch a third hinge, the cut **tears** instead: that is the split rule below.
- **Group assignment** is per brick (`BrickState.DeformGroup`), set once when the hinge forms, by which side of the cut plane the brick's solid centroid lies on. Bricks within 4 m of the plane carry both group ids and blend the two transforms by smoothstep on the signed distance, so the hull bends without a crack.
- **Dents ride their group** (`HullDent.Group`), so a dented plate on a bent tail stays dented in the right place.

**What follows from a dent or bend** (jammed doors, seam leaks, room volume, misaligned components, damaged cables and pipes, lost lift, weakened joints, crew injury, shield coverage) is evaluated on the event by the owning systems, by the rules in combat-and-sensors.md "What dents and bends do to the ship". The hull's part is to answer two queries: the displacement at a point, and the dents and hinge bands overlapping a box.

**Repair** removes records: press out (one dent), cut out and replace (the panel becomes a repair piece with weld joints, ship-design-and-construction.md), straighten in a yard jig (a hinge back to zero). Nothing heals on its own.

**Splitting**
- **Split check:** after each damage event, run solid union-find over the touched bricks.
  - If it becomes disconnected, the piece with the most mass keeps the body.
  - Each other piece becomes a new body whose blueprint is: the brushes whose AABB meets the piece's bounds (copied, re-centred; mesh brushes keep pointing at their `HullMesh` by `MeshHash`, so no mesh bytes are copied) + the damage brushes + `KeepSeedCell`. The bake discards solid not connected to the seed. Seeds are a few bytes, so this stays network-compact and deterministic.
  - A piece of < 2 m³ becomes debris: a convex hull plus a mesh snapshot, no blueprint, despawned on a timer.
  - **Dents and bends on a split (round 6):** the union-find runs in material space, so deformation never changes connectivity. Each new body takes the dents whose centre lies in its solid and the hinges whose cut lies inside it; the group holding its seed cell becomes its root, and that group's transform is folded into the new body's pose (so the piece appears exactly where it was drawn). A hinge whose cut is the split surface is dropped, and its far group ends with the piece that left.
- **Components** go with the piece holding their anchor cell. If the anchor cell is destroyed, the component detaches as its own dynamic body.

### LOD

- **Near:** LOD L re-bakes at 0.25·2^L m cells in 8·2^L m bricks, the same octree scheme as `ChunkCoord.Lod` and terrain skirts.
- **LOD ≥ 2:** skips `Interior`-flagged brushes unless they breach the outer surface, so far ships carry no interior triangles at all.
- **Mesh brushes at LOD ≥ 1** use one subdivision level fewer (level 0 at LOD ≥ 2): the SDF is re-baked at coarser cells anyway, so the finer cage would be wasted triangles in the BVH. Sign comes from the reduced mesh too, which can shift coarse occupancy by under one LOD cell; mass and rooms always come from the L0 bake, so this is visual only.
- **Beyond ~2 km:** one whole-ship bake at 2–4 m cells.
- **Beyond ~30 km:** impostor or lit point.
- **Dents and bends (round 6):** LOD ≥ 2 drops dents under 0.5 m depth from the brick's index run. The whole-ship far bake and the impostor ignore dents and keep group transforms (the far bake is drawn per group), so a broken-backed ship reads as one from 20 km.
- **L0 budget:** ≤ 400k tris per 100 m ship (~1M raw, ~100k after simplify). L0 mesh memory ~10–15 MB per 100 m ship; only nearby ships hold L0.

### Limits (enforced by the build tool and the server)

| Item | Limit |
| --- | --- |
| Ship extent | ≤ 1 km (float local frame error ≤ 6e-5 m) |
| Hull brushes | ≤ 8192 per ship |
| Damage brushes | ≤ 4096 per ship; ≤ 64 per brick; ≤ 8 new per ship per second (round 6) |
| Dents (round 6, D59) | ≤ 1024 per ship (28 KB); ≤ 16 per brick; summed displacement clamped to 2 m; ≤ 4 created or changed per ship per second |
| Hinges and deform groups (round 6, D59) | ≤ 7 hinges, ≤ 8 groups per ship; ≤ 2 hinges touching one brick; ≤ 12° and ≤ 1 m per hinge, then it tears |
| `DamageAccum` (round 6) | ≤ 256 active 2 m cells per ship, server only |
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
| **New** | `src/Ships` (Blueprint, Brushes, Bake, Mass, Rooms, Damage, Components), `src/Ships.Runtime` (jobs, collision sections, renderer, build tools), `data/components`, `data/ship-materials`. Round 5: `src/Ships/Meshes/` (`HullMesh.cs`, `MeshCanonical.cs` (canonical bytes, `MeshHash`), `IntSubdivision.cs` (integer Catmull-Clark, creases), `MeshSdf.cs` (sign rows, band distance, inset), `MeshValidate.cs`), `src/Ships/AeroStrips.cs` (wing planform from the bake), `src/Designer/Tools/MeshTool.cs` (editor ops, each an invertible op), `data/aero/airfoils.json`. Round 6 (D59): `src/Ships/Damage/HullDeformation.cs` (dent and hinge records, per-brick index runs, the displacement map and its inverse, group assignment, split hand-over), `src/Ships/Damage/CoarseMaterial.cs` (per-brick material byte and palette); `shaders/hull.vert` applies the dent and group map. The hit law, `DamageAccum` and the hull DDA live in combat-and-sensors.md's `src/Combat/`. |

### Open questions for the user

1. **Hull resolution:** is 0.25 m cells with a 0.5 m minimum wall acceptable for hulls? Each halving costs about 4–8× in bake time and memory; finer detail would go into components.
2. ~~**Smooth hulls**~~ (decided, D11: full mesh editing, still cuttable). Answered by mesh brushes with integer subdivision and creases; organic hulls are subdivision cages. Lofted / spline surfaces are not planned.
   - **New sub-question:** mesh detail finer than the 0.25 m cell (thin fins, small chamfers) is lost in the bake and flagged by the thin-feature warning. Is that acceptable, or should small craft (≤ 30 m) get an optional 0.125 m hull cell (about 4–8× bake time and memory for those ships)?
3. ~~**Damage**~~ (decided, D59: holes, dents and deformation, all three). Holes stay damage brushes; dents and bends are a displacement map over the bake ("Damage and splitting"; causes and effects in combat-and-sensors.md section 2).
4. ~~**Player-made components**~~ (answered by ship-design-and-construction.md: function is derived from a part graph, not from housing geometry).
5. ~~**Survival building**~~ (answered by ship-design-and-construction.md: plans, bought or made parts, and construction steps; creative mode builds instantly).

Revision and review history: [history/ship-hulls.md](history/ship-hulls.md).
