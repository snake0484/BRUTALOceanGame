# History of ship-hulls.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

## Revision (round 5) notes

User decisions round 5 (decisions.md) applied in place; they win over earlier text.

- **D11, full mesh editing, still cuttable.** New `BrushShape.Mesh` referencing a shared `HullMesh` in `ShipBlueprint.MeshTable`: closed n-gon control mesh in brush-local int32 1/1024 m, crease bits, face panels, 0–2 levels of integer Catmull-Clark. The CSG brush list stays the only truth; mesh brushes take Add / Subtract / Intersect / Paint and are converted to SDF at bake (exact ray-parity sign per brick row, narrow-band closest-triangle distance, triangle normals as Hermite data), so booleans, interiors, damage holes, mass, rooms, collision and LOD come from the bake unchanged. Rejected option (c) reworded: the modeller is now an authoring tool for one brush type, not the representation.
- **Hollow on meshes** uses a new `BrushFlags.Inset` Subtract brush sharing the mesh, eroding its SDF by the wall thickness (0.5–2 m).
- **In-world cutting** (cutting torch for boarding, salvage, rescue) writes swept-capsule Subtract brushes into the damage layer; works on primitives and meshes alike.
- **Mesh validation** table (closed, manifold, oriented, no self-intersection with exact Int128 tests, no degenerates, limits; thin-feature warning), done locally on commit and on the server only for new `MeshHash`es.
- **Mesh editor** tool list (select modes, snapped transforms, extrude, inset, loop cut, bevel, bridge, merge, dissolve, knife, fill, crease, live mirror, subdivision, convert primitive / group to mesh, panels) and its preview / commit model.
- **Mesh limits and costs** (vertices, faces, triangles per mesh, per ship and per brick; bytes; prepare, bake, validation and edit costs, all estimates) added to the section and to the Limits table; mesh network messages (`MeshHash` fetch, `MeshPatch`); LOD drops one subdivision level per LOD step.
- **Ship-system hull features:** `ChunkKind` (Hull, Armour, Wing) with `AeroProfile`; wing aero strips measured from the bake (D10); core eject path cutter and blow-out panel with a clear-path rule (D12/D13); escape pod bay launch tubes and station thrusters as placements (D25); dampener bubble room coverage (D9); `SocketType.Control` and concealed vs tray wiring with damage tests (D17). Behaviour of each lives in ship-design-and-construction.md.
- Open question 2 marked decided; a sub-question on an optional 0.125 m cell for small craft added. Files table: `src/Ships/Meshes/`, `AeroStrips.cs`, `MeshTool.cs`, `data/aero/airfoils.json`; `TriangleBvh` gains exact integer predicates.

## Round 5 review notes

Skeptical lead review, 2026-11-25, against decisions.md (then decisions-r5.md) and the round 5 copies of ship-design-and-construction.md, exotic-matter.md, warp.md and frames-and-zones.md. Decisions were not re-litigated; every fix below is made in place above.

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

## Consistency pass (r6) notes

- **Doc names (item 16):** round 5 references to `decisions-r5.md` now point at decisions.md.
- **Cutting torch (D27, items 12, 14):** station grids are cut the same way; boarding to the `CommandCore` is the capture path; a non-consented cut into another party's grid writes an `IncidentRecord`.
- **Station command core (D27, item 12):** new hull feature: every `PlanClass.Station` carries a `CommandCore` whose housing must sit inside a room, since capture is contested per room `GridVolume` (Error otherwise). Family and panel stay in ship-design-and-construction.md.
- **Dampener coverage (items 4, 5):** covered rooms use frames' `g_floor + a_man·max(0, 1 − a_d/|a_man|)` with exotic-matter's `a_d`, `a_max`, `MassMax`; the dampener, not plating, cancels thrust, tractor forces and throat drag; uncovered rooms feel all of it.
- **Hangar bays (D26, item 11):** hangar tractors on a visitor's craft need the landing request.
- D28 and D33 do not touch this doc; no other conflict-list item names it.

## Round 6 notes

User decisions D52-D74 (decisions.md) applied in place; they win over earlier text. Decisions were not re-litigated.

Edits here:
- **D59, holes, dents and deformation.** "Damage and splitting" rewritten around three forms, with a table of what the hull stores for each: damage brushes (holes, material space), `DamageAccum` (sub-brush damage, server, transient), `HullDent` records with per-brick index runs (dents), `HullHinge` + `DeformGroup` + a per-brick group id (bends), and a coarse material per 0.5 m cell. New structs `HullDent` (28 B), `HullHinge` (36 B), `DeformGroup`, `HullDeformation`; `BrickState` gains `Material`, `MaterialPalette`, `DeformGroup`, `DentFirst` / `DentCount`; `ShipHull` gains `Deform` and `Accum`. The map is applied in `hull.vert`, to exterior convex-hull points and section child transforms, and to interior walk-mesh vertices; never in the bake. Mass stays the undeformed bake's; centre of mass and inertia are recomputed per group on a bend. Hull-side rules added: group assignment per brick, the tear rule, glazing never dents, what a split does with dents, hinges and groups, LOD rules, limits rows, `HullDentMsg` / `HullBendMsg`, saves, files. The round 5 "displace control vertices" note is withdrawn. Causes, sizes, effects and repair are referenced from combat-and-sensors.md section 2, not repeated.
- **D71, wiring is power and data.** `SocketType.Control` removed; `Data` became `DataIn` / `DataOut` because a data cable is one-way. The Wiring hull-feature bullet says two media and adds the dent and hinge test for cable segments.
- **D55, shield emitters.** New hull-feature bullet: particle emitters cover facing by mount normal (`FacingTable`), energy projectors use the grid bounds, gravity emitters mount like tractor emitters; all defined in combat-and-sensors.md section 3.
- **D64, suit lockers.** New hull-feature bullet: ordinary interior placements with Power and `Air` sockets; the reach checks are ship-atmosphere.md's validation rows.
- **Open questions:** 3 marked decided (D59). Still open: 1 (hull resolution) and the 0.125 m sub-question under 2.
- The round 5 notes above that name a `Control` socket or medium are history; this section and the text above supersede them.

Changes other docs need:
- **combat-and-sensors.md:** consistent with this doc. To add there or accept from here: `HullHinge`'s field layout and the 16 B `DeformGroup` transform; group assignment is per brick by solid centroid; the split hand-over rule (a piece takes its dents and inner hinges, its group transform folds into the new body's pose); glazing shatters under any dent whose radius covers it; the 5-materials-in-one-brick palette fallback. It does not say where an energy shield projector mounts (this doc assumes anywhere on the grid).
- **tractor-beams.md:** the structure graph stays in material space and is never rebuilt for a dent or bend alone; a yielded cut is this doc's `HullHinge`, and a cut that cannot yield (limits above) tears through the existing split rule.
- **ship-atmosphere.md:** (done in the r7 consistency pass) its remaining Control wiring (the `Seal` Control input and loop, the `Interlock` Control pair per door, the `BarrierDown` Control output, the breach shield's Control `Inhibit`, the `VentInhibit` key switch "on a Control line") must become Data lines (D71; ship-design-and-construction.md "Wiring": hold lines and interlocks). `SocketType` is now `Mount, Power, Fuel, DataIn, DataOut, Coolant` plus its `Air`. Pinhole and seam `Leak` edges arrive without a re-bake. Suit lockers (D64) are its `fn.suit_locker`; this doc and ship-design-and-construction.md follow its slots, ports and validation rows.
- **ai-ships-and-crew.md:** `DamagePool` rows for hulls below L2 carry dents and hinges as well as brushes (combat-and-sensors.md gives the sizes); a hull promoted to a body applies them with no re-bake.
- **README.md / project CLAUDE.md:** "damage adds subtract brushes" is now "holes are subtract brushes; dents and bends are a displacement map"; wiring is "power, data". (README.md done in the r7 pass; the project CLAUDE.md is outside the staged set.)

**r7 consistency pass (2026-11-26):** the asks above were applied in the staged docs (combat-and-sensors.md accepts this doc's stored layouts and hull-side rules and says where an energy projector mounts; tractor-beams.md states yield, hinges and material space). Here: "Rooms and pressurisation" now defers to ship-atmosphere.md (design rooms, `ShipAir`, 0 per tick at rest), the cutting-torch and edit-permission lines cite factions-and-law.md (`Boarding`, `HullTitle`).
