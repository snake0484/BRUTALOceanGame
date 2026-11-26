# Components, first slice

Status: built on `claude/ship-designer-853ea3` (2026-11-26). Implements part of ship-design-and-construction.md's
component level: parametric parts, pipes, a component editor with a test bench, and prefabs placed in ships.

## What is built

- **Workspaces.** The designer has a Ship and a Component workspace in one window, each with its own plan. A component
  plan (level Component) holds housing solids, parts and pipes. Saving it under `Documents\BRUTALGame\Components`
  (subfolders are categories) makes it a prefab.
- **Parts** (`Ships/Components`): burn chamber, bell nozzle, injector (one-way orifice, fuel or oxidiser), igniter. Each
  is parametric (mm, 0.1°), drawn as an exact surface of revolution, weighed from that surface and its material. Ports
  have a position, direction and kind (flow, spark); a part moved within 0.15 m of a matching port facing it snaps on.
- **Pipes**: straight runs through nodes, outer and inner diameter, a material. Rating is Barlow, 2·σ_y·t / D_o, with
  yield strength from `data/materials/hull.json`. A pipe starts from a free port ("Pipe from here"); its nodes are
  dragged with arrows, added or removed; ends on ports follow them; open ends are inlets.
- **Chemistry** (`src/Chemistry`, shared with the game through `Sim`): species and reactions from `data/chemistry`, heat
  from formation enthalpies. Nothing burns unless the reactants share a volume inside their flammable window and a spark
  (or the autoignition temperature) lights them. Orifices use the compressible orifice equation; pipes burst above their
  rating. The bell nozzle's thrust comes from its contour: quasi-1D march, divergence loss from the lip angle,
  Summerfield separation in atmosphere.
- **Test bench**: feed each open inlet a mixture and pressure, open the valves, spark. The sample engine at 30 bar of
  methane and oxygen: cold flow 5.2 kN; lit 9.1 kN, Isp 345 s in vacuum, chamber 28 bar.
- **Prefabs in ships**: the Components panel lists the catalog and the user's prefabs as a tree; drag one into the view
  and it lands where the mouse ray meets the hull. Placements carry the prefab inside the ship plan (BODY format 5,
  keyed by plan hash) and can be moved and turned, not edited; "Open a copy in the component editor" edits a copy. Each
  placement cuts a box over its bounds out of the hull (a Component-role solid on layered hulls).

## Limits of this slice

- Constant cp and no dissociation: flame temperatures run hot (about 4400 K for methane–oxygen against about 3500 K real).
- The bench integrates explicitly in small substeps; fine for one engine in the editor, not for many at runtime
  (the game needs a steady-state or implicit solver).
- Pipes join part ports and open inlets only: no pipe-to-pipe junctions, valves, tanks or ship-side routes yet.
- The hull cutter is the prefab's bounding box; a placed prefab's housing mass is not yet added to the ship.
