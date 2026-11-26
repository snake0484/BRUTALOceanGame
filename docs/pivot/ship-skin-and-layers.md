# Ship skin, sections and layers

Status: first slice built on `claude/ship-designer-853ea3` (2026-11-26). User direction: you design the craft's outer
shell; rooms and embedded components you place are cut out of the hull automatically; the hull under the skin is several
layers. Changes ship-hulls.md's "solids are the solid" model for layered plans; plans without layers behave as before.

## Model

- **Skin:** solids with no role (Add, Subtract, Intersect, mesh solids, mirror modes) model the outer shell only.
- **Sections:** cut planes (X, Y or Z at a position) split the skin and hull into sections. Each section uses a **depth
  stack**: layers of (material, mm) from the skin inward. The skin is the outer face of the first layer and has its
  properties. Anything deeper than the stack is **void**.
- **Rooms and components:** solids with role Room or Component are cut out of the hull and get their **own wall stack**
  measured outward from their surface, kept inside the skin.
- **Panels:** solids with role Panel are add-on solids outside the skin (armour plates); they count for mass now and for
  damage later.
- Roles live in `SolidFlags` bits 6-7. Layering (stacks, cuts, section assignments by anchor point, the default, room and
  component stacks) is a block in plan BODY format 3.

## Bake

Per sample, from float fields per role: shell = max(s, −s − T(section)); walls = max(−r, r − W) inside the skin; minus
rooms and components; plus panels. The material is the layer at that depth. Mass, rooms and collision still come from
the grid. Bricks only one skin solid reaches draw the skin from the solid's own polygons and dual contour the inner
layer surface.

## Drawn surface (2026-11-26)

The user wants the surface perfect, not a grid mesh. `HullCsg` builds it as exact polygons: each solid's faces are
split against the others (convex solids by their planes, mesh solids by the planes of crossing triangles), and a piece
is kept where the ordered CSG flips across it. Later solids win coplanar faces; T-junctions are closed; the polygons are
binned into 8 m bricks for the renderer. Cylinders are prisms within 1 mm and shade smooth. It runs on job workers
(`HullSurfaceBuilder`); the main thread only prepares the scene. The grid stays for mass, rooms and collision, and draws
a layered hull's inner layer surfaces only. Stats has a "Show grid mesh (debug)" toggle.

Measured (100-solid bench hull, `--bake-bench 100`): 25 ms wall on 6 workers, 45 ms on one thread, of which the join
(T-junctions, bricks) is 16 ms single-threaded; with 20 smoothed mesh solids 33 ms wall. Per-solid caching (rebuild only
solids near an edit) is the next step for large hulls.

## Brushes

Solids are what used to be called brushes. A **brush** is now small visual flair: a stamp (a mesh, made from a solid,
copied along the line at a spacing) or a swept profile (rib, half round, ridge) extruded along it. A **stroke** is a line
of points clicked on the hull; it sticks to the hull (each rebuild re-projects its points along their normals with a
CSG ray against the solids, then resamples between them every 0.25 m), and its points can be dragged along the surface.
Strokes only add; they are drawn from their own geometry and counted for mass (volume x material density), never baked
into the grid or collision. Plan BODY format 4 stores the brushes and strokes.

## Limits of this slice

- Depth comes from the solids' combined distance field: where two skin solids only touch (not overlap), a thin
  internal wall forms at the joint. Overlap skin solids, or model one mesh skin.
- Layers thinner than a 0.25 m cell are resolved per sample only; stacks must total ≥ 0.25 m.
- Section cuts are axis-aligned planes; free-form panel outlines on the skin are later.
- Skin breakage, panel detachment and per-layer damage are runtime work (game side), not in the designer yet.
- On a layered hull the exact skin and the grid's inner surfaces meet at rooms that cross the skin: the opening is
  exact, its walls are grid.
- Strokes are not mirrored by the mirror modes yet.
