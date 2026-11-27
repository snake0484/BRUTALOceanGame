# Building designer: brief

Why: the world is authored (D83). The planet designer (branch `claude/planet-designer-d833e4`) paints maps and places `flatten` sites and city markers; nothing yet makes the buildings that stand on them. This tool models those buildings with the ship designer's tools. Numbers are estimates unless measured.

## 1. What a building is

**A building is a hull plan standing on the ground.** Walls, floors, roofs, door and window openings are exactly what the ship designer already makes: an ordered list of quantized CSG solids (add, subtract, paint), drawn as exact polygon CSG (`HullCsg` / `ExactSurface`), baked to 0.25 m bricks for occupancy, rooms and mass. So the structure is a `PlanDocument`, unchanged, and every ship modelling tool works on it as is.

| | Building |
| --- | --- |
| **Shares as is** | Solids and exact CSG, mesh solids (D11), brushes and strokes, paint, windows (`HullWindowSet`), hallways and walls (`HullInterior`), layered hulls, rooms and air per room (`ship-atmosphere.md`: sealed outposts on airless moons need it), validation, the 1/1024 m quantization, `.bplan` bytes. |
| **Adds** (`BuildingInfo`) | **Origin and ground:** building-local frame, +Y up (the body's radial up at the placement), y = 0 is finished ground. **Pad:** footprint rectangle (width, depth, metres) the building sits on; the site under it must hold it. **Foundation depth:** how far solids go below y = 0 (hides the gap between the terrain function and the pad; collision and drawing stop there). **Floors:** a list of named levels (height of each floor surface) for floor-by-floor cutaway now, AI navigation and lifts later. **Entrances:** doors to the outside, a portal rectangle on the hull that joins a room to the outside air (ship doors join room to room). |
| **Drops** | Thrusters and flight components, aero tables, flight mass properties (inertia, CoM for flight), joints (hangar doors on hinges can come back later), symmetry defaults stay but mirror is optional. |

Mass and volume are still computed (cost, crafting, collapse later), just not used for flight.

## 2. File format and where buildings live

`.bbld`, binary, little endian, one building per file:

```
header 32 B: magic 'BBLD', format u16, flags u16, building hash u64 (xxHash64 of INFO bytes + plan BODY hash), reserved
INFO  section: BuildingInfo (pad, foundation depth, floors, entrances), fixed layout, ints at 1/1024 m
PLAN  section: the structure's .bplan bytes verbatim (PlanFormat.Encode)
```

Wrapping the plan rather than adding a section to `.bplan` means **no change to `src/Plans`**, which the ship designer session edits often. The plan inside keeps its own BODY hash; `PlanClass` stays `Station` (anchored structure) until a `PlanClass.Building` is worth a one-line change there.

Shipped buildings: `data/buildings/<id>.bbld` (ids like `outpost.hab-a`, `city.tower-12x12`), embedded the same way as `data/planets/`. Placement is the planet layer's job, not the building's: a `buildings` list in `data/planets/<body>/body.json`, `{ "building": "outpost.hab-a", "site": "port-a", "pos": [x,y,z] (int64 1/1024 m, planet frame), "yaw": 15-degree steps }`. The planet designer would place them (later slice there).

## 3. How a building reaches the game (later, not this build)

A placement puts the building's frame on its site: origin at `pos`, up = radial, yaw about up; the site's flattened height is the pad. Every peer loads the same `.bbld` from `data/` (zero bandwidth, D83). The **server** loads it headless (`Plans` + `Ships`, no Vulkan): when a bubble comes within reach it bakes the exact CSG triangles into static Jolt shapes per 16 m section (static mesh shapes are exact and cheap for something that never moves), and rooms and air from the brick bake. The **client** draws the exact surface with the hull renderer through a per-draw `relOrigin` (C9), simplified with MeshOptimizer at range, as impostors further out, and as city lights from orbit. The terrain under the footprint stays the planet's: the site flattens it, the foundation hides the seam. Whether a building is static scenery or a grid anchored to the ground (and so damageable like a station) is question 1.

## 4. App or mode

**Recommendation: its own app, `BRUTALBuildingDesigner`** (`src/BuildingDesigner`, `src/BuildingDesigner.App`, `runBuildingDesigner.bat`, tests in `tests/BuildingDesigner.Tests`), referencing the ship designer's libraries without changing them.

- For: the ship designer session is actively changing `DesignerApp` and `DesignerPanels`; a mode would edit both and conflict often. The scene differs (ground plane, pad, sun, 10-200 m scale, floors). The planet designer set the precedent of a standalone tool per job.
- Against: the ship app's hull pipeline orchestration (bake, exact surface, upload, validate on commit, about 300 lines in `DesignerApp`) is duplicated in a smaller form until lifted, and ship features reach buildings only when the building app wires them in.
- A mode of BRUTALDesigner (a third tab kind beside Ship and Component) gets every tool on day one with no duplication, at the price of edits in the busiest files of another session. Question 4.

## 5. Sharing code without breaking the ship designer

| Use as is (reference, no edit) | What it gives |
| --- | --- |
| `DesignKit`, `DesignKit.Ui` | `DesignApp` shell, `DesignerStyle`, `OrbitCamera`, `MoveGizmo`, `ViewAxes`, `OverlayRenderer`, `FileDialogs`, `InputScript`, `EditSession`, `UndoStack` |
| `Ships`, `Plans` | solids, CSG, meshes, windows, interiors, bake, mass, `.bplan` encode and decode, validator |
| `Designer` | `DesignSession` (`EditSession<PlanOp, PlanDocument>`), `PlanOp`, `HullCommands`, `Snap`, `Symmetry`, `WindowCommands`, `InteriorCommands`, picking |
| `Designer.Ui` | `HullRenderer`, `ViewportTool` (outlines, move gizmo, resize handles, picking), `TransformGizmo` rings, `ObjectTree`, `BrushesPanel`, `WindowsPanel`, `InteriorPanel` |

**Lift later:** the hull pipeline in `DesignerApp` (bake + surface builder + upload + validation + stats) into a `HullView` in `Designer.Ui`. Done in one commit at a quiet point of the ship session, with its OK, both apps switched together. Until then the building app keeps its own small copy and never renames or moves a ship type.

**Building-only code:** `src/BuildingDesigner` (headless): `BuildingInfo`, `BuildingFile` (`.bbld`), `BuildingSession`, which wraps a `DesignSession` for the plan plus a small log for `BuildingInfo` edits, with one history so Ctrl+Z walks both in order. App: ground grid and pad drawn with `OverlayRenderer`, a Building panel (pad, foundation, floors), save and load.

**DesignKit:** no change expected for slice 1. Anything needed goes in additively and is listed in the report. The planet branch's `OrbitCamera.Up` and `DesignApp.RecordBeforeScene` are not on `space-pivot`; this tool does not need them (+Y up).

## 6. Slice 1 (after the answers)

App on DesignKit with the shared style, camera, gizmos and undo; ground plane and site pad; add, move, scale, subtract solids with the ship designer's tools (a door opening and a window are subtract solids, or the window tool); pad and foundation in a Building panel; save and load `.bbld`; headless tests for `BuildingInfo`, the file round trip (bytes and hash stable) and undo across plan and info edits; `runBuildingDesigner.bat`; a scripted check (`--input-script`) building walls, floor, roof, door and window, saving, reloading, undoing. Not in slice 1: placement on a planet, game loading, collision, floors as navigation, entrances as air portals.

## 7. Questions for the user

1. **Static or anchored?** Are buildings static scenery (cheap: a static mesh, never changes) or grids anchored to the ground like stations (damageable, holes and splits, players can build bases with the same tool)? Recommendation: anchored grids, the station runtime reused; authored ones are protected by law (secure zones), not by physics.
2. **One building per file, or settlements?** Recommendation: one building per `.bbld`; a settlement is a list of placements in the planet layer, and a settlement prefab (a saved group of placements) can come later.
3. **Interiors from the start?** Shells with door and window openings first, or walkable floors, rooms and air in slice 1? Recommendation: shells and openings now, floors and rooms next slice.
4. **Own app or a mode of BRUTALDesigner?** Recommendation in section 4: own app.
5. **Do players use it?** Is the building designer bundled for players (like D69 for ships, with plans, cost and construction), or our authoring tool only for now?
6. **Who sizes the ground?** Does a building's pad write its own site stamp into the planet layer (a rotated rectangle sized from the footprint), or is the site placed first in the planet designer and the building must fit it? Recommendation: the building's pad drives the stamp, so the ground always fits.
