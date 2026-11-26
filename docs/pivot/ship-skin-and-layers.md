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

## Limits of this slice

- Depth comes from the solids' combined distance field: where two skin solids only touch (not overlap), a thin
  internal wall forms at the joint. Overlap skin solids, or model one mesh skin.
- Layers thinner than a 0.25 m cell are resolved per sample only; stacks must total ≥ 0.25 m.
- Section cuts are axis-aligned planes; free-form panel outlines on the skin are later.
- Skin breakage, panel detachment and per-layer damage are runtime work (game side), not in the designer yet.
