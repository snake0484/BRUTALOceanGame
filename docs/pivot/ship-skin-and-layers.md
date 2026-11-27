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

**Brush profiles (2026-11-27, BODY format 10; plans without sweeps or sizes keep their format):** a sweep's outline is
one of the plan's profiles, kind brush (closed, 3-16 points, X across, Y out of the hull), made and shaped in the
Profiles tab and kept in the profile library like the others. Each sweep brush sets its **line point** (where in the
outline the stroke runs; the cross in the Brushes panel, or typed) and its **size**; stamps have a size too. Every
stroke **point has a size**, blended along the line to the next, so a stroke can taper or swell. Sizes multiply: brush x
stroke x point. Older plans' inline outlines become plan brush profiles on load, the line at their origin as before.

**Paint and decals (2026-11-27, BODY format 10):** drawn only, never baked, massed or collided. Both are projector boxes
(`HullProjector`) the hull shader colours the surface with, hull and flair alike, where the surface faces along the box's
axis. A **paint** brush (pattern: line cross; colour with opacity, band width, line spacing and width) turns a stroke into
a band: boxes along the stuck path, merged while it runs straight, reaching 1 m out (over raised flair) and 0.1 m in; the
pattern runs on through the pieces by the distance along the stroke, the strongest paint winning where pieces overlap.
A **decal** is a game image (`data/decals/decals.json`; players pick only from game data, mods add more) placed like a
window (Add > Decal, click the hull) with width, height, projection depth, turn and opacity; later decals draw over
earlier ones and over paint. Images load once into an sRGB texture array (512 px layers, CPU mips). Each brick draw gets
the list of boxes touching its bounds (built on commit), so a fragment tests only those; the Stats panel shows the box
and brick-list counts.

**Paints (2026-11-27, BODY format 11):** a ship has up to 32 **paint slots**; each holds a paint, a **finish** from game
data (`data/paints/finishes.json`: CC0 texture sets, see `data/paints/LICENSE.md`) in a colour. Everything painted names a
slot, so changing a slot, or importing another paint into it, repaints every use at once. Uses: the hull's **base paint**
(without it, surfaces keep the structural materials' colours), brush **strokes' own paint** (a fill band over the stroke's
flair, as wide and tall as it; brushes without one carry the hull's), and **paint shapes** (decals that are a rectangle,
ellipse, triangle or diamond filled with a slot instead of an image). Paints save to and import from a library like
profiles (`Documents/BRUTALGame/Paints/**/*.bpaint`, JSON naming the finish by its data key); a slot keeps its copy and
takes a changed file only on Reimport. Drawing: finishes are 1024 px layers of two arrays (sRGB colour with a bare-metal
mask; normal, roughness, metalness), sampled triplanar in ship space (the hull has no UVs) with whiteout-blended normals;
the paint's colour replaces the finish's own, keeping its light and dark, except where scratched to bare metal; roughness
and metalness drive the highlight. Order on a surface: base paint, stroke fills, paint shapes and images, pattern paint.

## Interior spaces: hallways (2026-11-27)

User direction: hallways are like pipes, a route of nodes with an area that must fit humanoids, and an adjustable
profile; any angle, each hallway with its own gravity (a ship may have several), stairs where expected; cut as a room.

- **Data** (`Ships/Interior`, plan BODY format 8; 6 and 7 load converted): the plan's profiles (name, library source,
  closed outline X across, Y up from the floor, 3-32 points) and per hallway an id, nodes (1/1024 m) joined by runs,
  the profile it uses, a gravity rotation (15° snap; down = it applied to -Y) and a stair material.
- **Networks and profiles (2026-11-27):** three or more runs at a node make a junction; each run there reaches past the
  node by half the profile's width so the runs overlap into one space, two runs are mitred, a free end is square.
  Profiles live in the Profiles tab: the plan's own copies (one change reshapes every hallway using it) and the library
  on disk, `Documents/BRUTALGame/Profiles/**/*.bprofile` (JSON, folders as categories). Importing copies a file into
  the plan and records its path; a later change to the file only shows as "the library has changed" until Reimport.
- **Clearance:** a person needs 0.8 m wide by 1.9 m tall clear above the floor; every further 0.8 m of clear width lets
  one more pass at once. Measured on the profile as the widest span round the floor's middle that stays clear up to
  1.9 m (2 mm slack for the grid). Too small is a plan Error.
- **Runs:** each run's floor is level side to side against its gravity. Up to 20° it is a ramp; to 50° it gets stairs
  (risers about 0.18 m, one convex block per step); steeper is a shaft (a plan Warning: ladders and zero-g crawlways are
  not built yet). A run along gravity keeps the gravity frame's front as the profile's up.
- **Expansion** (`HallwayGeometry.Expand`, deterministic): each run is a closed prism of the profile, mitred at the nodes
  (sharp turns of more than 120° are not mitred), split every 128 m for the mesh limits. Cuts are Room-role solids on a
  layered hull (own wall stack) and Subtract otherwise; stairs come after every cut (Panel role on a layered hull, so the
  room cut does not remove them). Their meshes follow the plan's in the mesh table; they feed the bake and the exact
  surface like plan solids.
- **Designer:** Add > Hallway drops a level 4 m run at the camera target with the plan's first profile (a 1.2 × 2.2 m
  rounded one if there is none). The Interior panel shows the selected hallway's profile choice, clearance, gravity,
  stair material and runs. The Profiles tab's editor takes presets (rectangle, rounded, octagon, arch at a width and
  height), then drag points (0.05 m snap), double-click an edge to add one, right-click to remove; it shows one
  0.8 × 1.9 m lane per person abreast. In the view: select a node; the bar beside it adds a node (extends or branches),
  deletes the node or the hallway; the marker mid-run splits it; arrows move it, rings turn gravity (one gizmo: a selection shows only the handles it can use; a picked node, window, placement or part has it instead of the selected solid). `--demo-hallway` (layered hull) and
  `--demo-hallway-plain` (solid block) show one. The selection's menu opens on a right-click in the view (a right-drag
  still orbits).
- **Wall profiles (2026-11-27, BODY format 9; plans without them keep format 8):** half a hallway profile, for rooms. A
  profile's kind is hallway or wall; a wall is an open line from floor to ceiling, X how far it stands in from the
  room's side, Y height (never going down). A box room (Room role, or a subtract) picks one under Walls in Properties;
  mirror partners follow. The bake swaps the box for its walled mesh in place (`WallGeometry.Expand`: a stack of the box
  inset by the profile at each height, its rotation baked in). The profile's lower half keeps to the floor and its upper
  half to the ceiling, so taller rooms stretch the middle; lower rooms squeeze it. Presets: straight, chamfered, coved,
  sloped; "Make a wall from half of it" takes a hallway outline's right side. Library files carry the kind.
  `--demo-walls` shows a coved box room and a round one.
- **Interior view** (top bar, or I): the hull is drawn see-through (both sides of every face, blended, no depth
  writes, stronger edge on) and locked: hull panels greyed, no solid picking, gizmos, strokes or hull keys. Hallways,
  placed components and parts inside stay editable.

## Windows (2026-11-27)

User direction: windows deform to the hull shape, cut away the hull to the interior spaces, and come in different
materials.

- **Data** (`Ships/Interior/HullWindows.cs`, plan BODY format 7, flag 8 after the interior): per window an id, centre
  (1/1024 m), outward normal, shape (rectangle, rounded, circle, arch), width and height, roll (15° steps), cut depth,
  pane thickness (at least 16 mm, the mesh's shortest edge) and pane material.
- **Expansion** (`WindowGeometry.Expand`, deterministic): the centre sticks to the hull along the normal each rebuild
  (cast back from 2 m out, like strokes). The opening is a straight prism of the outline from 0.5 m outside to the cut
  depth in: Room role on a layered hull (it reaches the interior with its own wall stack), Subtract otherwise, with the
  hallway cuts. The pane is rings of the outline (one per 0.2 m of half size, thinned so the smallest keeps 2 cm edges),
  every point cast onto the hull, thickened inward, closed, triangles only: its outer face is the hull's own shape. Panes
  are flair: drawn from their own geometry and weighed (volume x density), not cut and not in the grid.
- **Glazing:** materials carry an `opacity` (data); below 1 they are see-through. Window glass, borosilicate,
  polycarbonate, transparent aluminium (ALON) and sapphire use handbook densities and strengths. The opaque pass skips
  them; a blended pass after it draws them tinted, stronger edge on, with a sun glint. Any material can be a pane.
- **Designer:** the Windows panel (ship tabs): Place window, then click the hull (a live outline shows where); list;
  shape, size, cut depth, pane thickness, turn, material (glazings first), Move (click the hull again), Delete. The
  outline is drawn on the hull. `--demo-windows`: a rounded side window, a porthole and one curving over the cylinder.

## Add menu and context bars (2026-11-27)

New things come from the top bar's Add menu (box, wedge, cylinder; hallway and window on a ship; parts and a pipe on a
component), and the actions on a selection sit in a bar beside it in the view: solids (duplicate, add/subtract,
delete), hallway and pipe nodes (add a node or branch, delete), windows (move, delete), placed components (open a
copy, delete), parts (delete). One bar shows at a time; the panels keep properties. Interior, Profiles and Windows dock
with Solids and Components (lower left).

## Cutaway bar (2026-11-27)

The cutaway moved from the Hull layers panel into the view, under the edit mode bar: Cut, X / Y / Z, Flip, the position
(drag, or double-click to type) and Pick: click the hull and the plane goes 1 mm inside the point clicked (the ray goes
through hull already cut away, so it lands on what is shown).

## Limits of this slice

- Depth comes from the solids' combined distance field: where two skin solids only touch (not overlap), a thin
  internal wall forms at the joint. Overlap skin solids, or model one mesh skin.
- Layers thinner than a 0.25 m cell are resolved per sample only; stacks must total ≥ 0.25 m.
- Section cuts are axis-aligned planes; free-form panel outlines on the skin are later.
- Skin breakage, panel detachment and per-layer damage are runtime work (game side), not in the designer yet.
- On a layered hull the exact skin and the grid's inner surfaces meet at rooms that cross the skin: the opening is
  exact, its walls are grid.
- Strokes are not mirrored by the mirror modes yet.
- Hallways: no ladders or zero-g crawlways for shafts yet; doors and hatches where a hallway meets a room are the next
  step; the room graph (atmosphere, crew nav) does not read hallways yet.
- Windows: panes are not in the grid yet (no collision or air seal from them); the opening's depth is set by hand, not
  found from the interior; mirror modes do not mirror windows.
