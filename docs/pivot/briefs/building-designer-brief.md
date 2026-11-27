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

Shipped buildings: `data/buildings/<id>.bbld` (ids like `outpost.hab-a`, `city.tower-12x12`), embedded the same way as `data/planets/`. **Settlements** (user answer 2) are files too: `.bset` in `data/settlements/<id>.bset`, a list of placements `{ building id, building hash, pos (int64 1/1024 m in the settlement frame, y up), yaw (15-degree steps) }` plus a pad covering them all. The planet layer places buildings or settlements: a `placements` list in `data/planets/<body>/body.json`, `{ "settlement" or "building": id, "site": "port-a", "pos": [x,y,z] (int64 1/1024 m, planet frame), "yaw": steps }`. The planet designer would place them (later slice there). Slice 1 ships the `.bset` data model and its round trip; editing settlements in the app is slice 2.

## 3. How a building reaches the game (later, not this build)

A placement puts the building's frame on its site: origin at `pos`, up = radial, yaw about up; the site's flattened height is the pad. Every peer loads the same `.bbld` from `data/` (zero bandwidth, D83). The **server** loads it headless (`Plans` + `Ships`, no Vulkan): when a bubble comes within reach it bakes the exact CSG triangles into static Jolt shapes per 16 m section (static mesh shapes are exact and cheap for something that never moves), and rooms and air from the brick bake. The **client** draws the exact surface with the hull renderer through a per-draw `relOrigin` (C9), simplified with MeshOptimizer at range, as impostors further out, and as city lights from orbit. The terrain under the footprint stays the planet's: the site flattens it, the foundation hides the seam. A building is **a grid anchored to the ground** (user answer 1), as a station is an anchored grid: holes, dents, splits, rooms and air come from the ship runtime, and players can build bases with the same tool. Authored ones are protected by law (secure zones), not by physics. An anchored building with nobody near is `Frozen` and costs nothing per tick.

## 4. App or mode

**A Building tab of BRUTALDesigner** (revised 2026-11-27: slice 1 first shipped as its own app; the user asked to merge it). File > New building or Open building opens a tab beside Ship and Component tabs, with every ship tool (windows, interiors, joints, components, paint, brushes) on day one and no duplicated hull pipeline. The cost is small hooks in `DesignerApp` and `DesignerPanels`, files another session changes often: the building code sits in its own partial, `DesignerApp.Buildings.cs`, so merges touch only a few lines.

## 5. Sharing code without breaking the ship designer

| Use as is (reference, no edit) | What it gives |
| --- | --- |
| `DesignKit`, `DesignKit.Ui` | `DesignApp` shell, `DesignerStyle`, `OrbitCamera`, `MoveGizmo`, `ViewAxes`, `OverlayRenderer`, `FileDialogs`, `InputScript`, `EditSession`, `UndoStack` |
| `Ships`, `Plans` | solids, CSG, meshes, windows, interiors, bake, mass, `.bplan` encode and decode, validator |
| `Designer` | `DesignSession` (`EditSession<PlanOp, PlanDocument>`), `PlanOp`, `HullCommands`, `Snap`, `Symmetry`, `WindowCommands`, `InteriorCommands`, picking |
| `Designer.Ui` | `HullRenderer`, `ViewportTool` (outlines, move gizmo, resize handles, picking), `TransformGizmo` rings, `ObjectTree`, `BrushesPanel`, `WindowsPanel`, `InteriorPanel` |

**Building-only code:** `src/Buildings` (headless, Plans only): `BuildingInfo`, `BuildingFile` (`.bbld`), `SettlementFile` (`.bset`), `BuildingCheck`. `src/Designer/Buildings`: `BuildingSession`, which adopts a tab's `DesignSession` and keeps `BuildingInfo` on its history, so Ctrl+Z and the Designer panel's Undo walk both in order. `DesignerApp.Buildings.cs`: ground grid and pad drawn with `OverlayRenderer`, the Building panel (pad, foundation, ground checks, building hash), `.bbld` save and load, `--demo-building`. Tests in `tests/Designer.Tests/BuildingTests.cs`.

**Designer.Ui hooks (additive):** `Workspace.Building`, the panel's `UndoHook`/`RedoHook`, File menu New building and Open building, a "Building:" tab prefix.

**DesignKit:** no change expected for slice 1. Anything needed goes in additively and is listed in the report. The planet branch's `OrbitCamera.Up` and `DesignApp.RecordBeforeScene` are not on `space-pivot`; this tool does not need them (+Y up).

## 6. Slice 1 (after the answers)

Building tab in BRUTALDesigner (first built as its own app on DesignKit, then merged) with the shared style, camera, gizmos and undo; ground plane and site pad; add, move, scale, subtract solids with the ship designer's tools (a door opening and a window are subtract solids, or the window tool); pad and foundation in a Building panel; save and load `.bbld`; headless tests for `BuildingInfo`, the file round trip (bytes and hash stable) and undo across plan and info edits; `runBuildingDesigner.bat`; a scripted check (`--input-script`) building walls, floor, roof, door and window, saving, reloading, undoing. Not in slice 1: placement on a planet, game loading, collision, floors as navigation, entrances as air portals.

## 7. Questions for the user (answered 2026-11-27: 1 anchored grids, 2 settlement files too, 3 shells first, 4 own app, revised to a tab of BRUTALDesigner; 5 and 6 still open, recommendations stand until answered)

1. **Static or anchored?** Are buildings static scenery (cheap: a static mesh, never changes) or grids anchored to the ground like stations (damageable, holes and splits, players can build bases with the same tool)? Recommendation: anchored grids, the station runtime reused; authored ones are protected by law (secure zones), not by physics.
2. **One building per file, or settlements?** Recommendation: one building per `.bbld`; a settlement is a list of placements in the planet layer, and a settlement prefab (a saved group of placements) can come later.
3. **Interiors from the start?** Shells with door and window openings first, or walkable floors, rooms and air in slice 1? Recommendation: shells and openings now, floors and rooms next slice.
4. **Own app or a mode of BRUTALDesigner?** Answered own app, then revised by the user: a Building tab of BRUTALDesigner (section 4).
5. **Do players use it?** Is the building designer bundled for players (like D69 for ships, with plans, cost and construction), or our authoring tool only for now?
6. **Who sizes the ground?** Does a building's pad write its own site stamp into the planet layer (a rotated rectangle sized from the footprint), or is the site placed first in the planet designer and the building must fit it? Recommendation: the building's pad drives the stamp, so the ground always fits.
