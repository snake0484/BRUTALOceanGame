# Ship design and construction

Status: design only. Nothing built, no repo files changed. Written 2026-11-25 against README.md, ship-hulls.md, exotic-matter.md, frames-and-zones.md and review.md. **ship-hulls.md is the base**: blueprint = CSG brush list, `ContentHash` prefabs, `HullBrush`, 0.25 m SDF bricks, components = modelled housing + function. Where this doc changes it, the change and the reason are under "Changes to other docs". Review.md's rulings (C13 int64 1/1024 m positions, X2 rotation bits, X3 server-sent mass) still win. Every number is a knob in `data/`; every cost and timing is an estimate to measure.

## Recommendation in one paragraph

Everything a player can design is a **plan**: a versioned, content-hashed document at one of five levels (laminate, part, component, hull chunk, ship). Materials are data, never plans. A **prefab** is any plan that has been certified and published. One **Designer core** library (no Vulkan, no ImGui) owns the document model, edit operations, undo, validation, cost quotes and assembly-order compilation. Two hosts show the same ImGui UI: **Designer.App**, a standalone executable with its own window and plan files on disk (no Sim, no network), and the **in-game host**, a design terminal or build-mode tablet whose plans live on the server as inventory items. A component's function is **derived from its parts**: a data-defined function family (warp drive, thruster, tank...) declares roles, required flow paths and formulas, and a cheap deterministic graph evaluation turns the part graph into the same stats struct the runtime already uses. Cost follows one rule: **every level you redefine pays an engineering (certification) fee once, a type-inspection stamp on every build, and loses catalog economies of scale**, so premade < plans < custom ship < custom component < custom parts **at any fleet size**, and only your own fabrication capacity closes the gap, to about the same price. Buying a ship hands you a finished ship in the show hangar. Plans give you a construction site, a **dependency graph of steps** shown as a holographic overlay, stock that must be cut (I-beams and pipes as long bars, offcuts kept), and joints that must be made (weld, bolt, screw, rivet, nail, flange, crimp, bond), each with a quality that feeds mass, leaks, component integrity and the stress model. Construction is event-driven: an idle or finished ship costs nothing per tick.

## User decisions this doc implements

- Ship designing, modification and building is an important part of the game.
- A dedicated designer **application** saves and loads plans. In game you use the **same** application to modify ship designs or create new ones.
- The hull can effectively be 3D modelled. Armour plates can be custom built or made from prefab components. Prefabs are designable too.
- Components are made from **parts** (pipe segment, reaction chamber...). Hierarchy: ship > components > parts > materials.
- Each level you redefine costs more: a premade ship is cheaper than your own like-for-like design; your own warp drive costs more than its parts; having each part custom-manufactured costs more again. Own production capacity from materials can edge out those margins to the same price.
  - Read here as: **without own production, no route is cheaper in money than buying premade**, including building the catalog plan yourself from bought components, and including fleets that amortise the engineering fee. Own production brings a route to about the premade price, not far below it (section 3).
- A bought ship flies out of the show hangar.
- With plans you buy components and/or parts and assemble them yourself, with the plan as a **required** guide, sometimes in a specific order.
- I-beams and pipes come as long segments you cut. Prefabs come complete.
- Construction steps: welding, screwing, nailing, etc.

This answers ship-hulls.md open questions 4 (player-made components: function from parts, not from housing geometry) and 5 (survival building: plans + bought or made parts + construction steps; creative mode builds instantly), and README questions 6 and 8.

## 1. Design data model

### Levels

| Level | What it is | Made of | Designed in | Catalog examples |
| --- | --- | --- | --- | --- |
| Material (data) | Density, strength, price, visual. Never a plan | n/a | n/a (`data/materials`) | structural alloy, titanium, copper, ceramic, seal polymer |
| **Laminate** | A layered material: ordered layers of sheet materials with thicknesses. Yields an effective density, strength, shielding σ, cost | materials | Laminate panel | `lam.armour.ceramic_ti`, `lam.hull.std` |
| **Part** | A parametric template instance: template + parameter values + material. Stock parts (beam, pipe, sheet, cable, fastener) and machined parts (chamber, valve, pump, mixer, emitter, nozzle...) | materials | Part panel (parameters only, no modelling) | `pipe.ti.dn40`, `chamber.c.0_5m3`, `valve.dn40` |
| **Component** | Housing (brushes, ≤ 8 m, 1/16 m cells, as ship-hulls) + part graph (parts, connections = joints, internal routes) + function family | parts, laminates | Component designer | `drive.warp.c.recirc`, `thruster.chem.s` |
| **Hull chunk** | A saved group of hull brushes (ship-hulls `HullChunkGroup` prefab). **Armour plates are hull chunks** whose brushes use a laminate | brushes, laminates | Hull and plate tools | `plate.armour.2x2`, `chunk.nose.cone4m` |
| **Ship** | Hull brushes + component placements + routes between sockets + order constraints + keel | everything above | Ship designer | `ship.wren` |

**Why armour plates are hull brushes, not components.** Damage (subtract brushes), radiation shielding (`ShieldTable`, `InternalShield`) and mass all already read the hull occupancy. A plate as a component would need its own damage, shielding and room rules. The 0.5 m minimum wall is not a problem: a laminate is a material whose density and σ represent the whole layup, exactly as ship-hulls' "structural composite" materials do. A 0.5 m brush of `lam.armour.ceramic_ti` *is* a plate. Plates differ from hull pieces only in how they are built: they are **bolted on** (removable, replaceable) instead of welded in. "Made from prefab components": a plate design may nest prefab plate pieces (tiles, edge strips, corner caps: hull-chunk plans referenced by `PlanRef`) next to its own laminate brushes, and may carry prefab components on it (hatches, sensor windows, hardpoints), placed like any component. Saved, it is a new plate prefab.

**Why parts are parametric, not modelled.** Function needs numbers (volume, diameter, wall, rating), not shapes. A modelled part would need a geometry-to-physics solver per part kind. Shape detail lives in the component housing; parts draw as instanced meshes generated from their parameters.

### Plan identity and versions

- `PlanHash` = xxHash64 of the canonical BODY bytes (the ship-hulls `ContentHash`, generalised). It is the prefab id, the bake cache key, the network fetch key and the save key.
- Element ids (brushes, part instances, connections, routes, placements) are allocated by the document, monotonic, never reused, and stored in BODY. They are stable across revisions, so a modification is a diff by id (section 4). Two identical designs with different edit histories hash differently; that is accepted.
- **Versioned:** META carries `Parent` (the previous revision's hash), `Revision` and name. META is not hashed, so renaming never changes identity.
- **Server identity:** the server never trusts a header hash. It decodes, validates and re-encodes BODY canonically and hashes that. Its store keeps one BODY per hash and **compares bytes on insert**: an upload whose hash matches a stored BODY with different bytes is refused (xxHash64 is not collision resistant, so a crafted collision must not replace or alias a stored plan). Catalog plans are loaded into the store at server start, so their hashes cannot be squatted.
- **References:** a plan refers to sub-designs by `PlanRef` = either a catalog `DefId` (stable string id from `data/`, hashed to u32; the catalog loader refuses two ids with the same hash) or a `PlanHash`. Hash references form a Merkle DAG, so a plan cannot reference itself or a cycle. Catalog content is itself stored as plans in `data/plans/` (authored in Designer.App), so there is one format and one evaluator for both.
- `DataVersion` in the header records the data set the plan was validated against. Stats are never stored in a plan; they are re-derived on load, so rebalancing data changes every ship consistently. A plan referencing a removed catalog id loads with a placeholder and an Error.
- **Rebalance rule:** when a data change makes a certified plan invalid (an envelope shrinks, a rule tightens), ships already built keep flying with their stats re-derived and clamped to the new envelope; new builds of that plan are refused until it is revised, and the fixing revision pays no engineering fee.

### How function is derived from parts

A **function family** (`data/functions/*.json` + one evaluator struct in code) declares:
- **roles**: which part tags fill it, and min/max count (e.g. `Chamber` 1..1, `Emitter` 4..32, `Diluter` 0..1);
- **paths**: required flows over ports of one medium (e.g. EM: `FeedSocket → Diluter → Chamber`);
- **loops**: required cycles (agent: `Reservoir → Pump → Diluter → Chamber → Separator → Reservoir`);
- **adjacency**: e.g. `Trap` connected to `Chamber` by an AM port;
- **coefficients** and **envelope** (per-stat min/max clamps, per class);
- **formulas**: fixed C# per family, coefficients from data.

Evaluation, on commit only, never per frame:
1. **Role assignment** by tag, in part-index order (deterministic). Missing or excess roles are Errors.
2. **Port graph**: ≤ 256 parts, ≤ 1024 connections, adjacency in CSR arrays. Paths and loops are BFS over ports filtered by medium. About 2–20 µs.
3. **Aggregate** part properties along the found paths (volumes, minimum pressure rating, counts).
4. **Formulas** produce the family's stats struct, clamped to the envelope. Fixed point (q16, mg, ticks), no trig, no randomness.
5. The result is cached by `(PlanHash, DataVersion)`. The server always re-evaluates; client results are previews.

**Warp drive family** (`fn.warp.recirc`; slurry is the same family minus diluter, separator and loop). The catalog C plan must evaluate to exotic-matter.md's C row exactly (a test):

| Stat (exotic-matter name) | Derived from | Formula | Catalog C plan |
| --- | --- | --- | --- |
| Class, `MassCapKg` | Emitter segments (grade D/C/B/A, rating each) | class = lowest segment grade; `MassCap = min(n · rating, ClassMassCap)`; `EmPerLn`, `VCap`, spool, cooldown come from the class table | 8 × C segments at 625 t → 5,000 t |
| `LineCapKg` | Chamber volume `V_c` | `ρ_line · V_c`, ρ_line 150 kg/m³ | 0.5 m³ → 75 kg |
| `PrimeKg` | Line volume: diluter + pipes on the EM path diluter → chamber | `ρ_line · V_line` | 0.0067 m³ → 1.0 kg |
| `LoopAgentKg` | Reservoir + loop pipe volume | `1400 · V_loop` | 0.057 m³ → 80 kg |
| `AgentRecovery` R | Separator grade × loop joints | `R_sep · Π(1 − leak_j)` (design: leak 0) | 0.95 (0.98 quality separator) |
| `Tolerance` | Diluter stages s | `0.02 + 0.0075 · s`, cap 0.08 | 4 stages → 0.05 |
| `MixTicks` | Diluter stages | `60 + 10 · max(0, s − 4)` | 60 |
| `FeedMax` | Interlock part on the feed path | interlock grade (0.85); **no interlock → 1.0** (Warning: any purer feed runs away) | 0.85 |
| Purge | Purge valve DN d on the line | `PurgeTicks = 120 · (40/d)²`; no valve → no purge (Warning) | DN40 → 120 |
| Options | Parts: inline assayer on feed path, auto-purge controller (faction-keyed part), eject rail on the housing mount, quality separator | presence | none |
| Line safety SF | Every pipe and fitting on the EM line | `SF = min(P_rating) / P_op`, Barlow `P_rating = 2·σ_y·t / D_o`, `P_op` 20 MPa | Ti grade 2 (σ_y 275 MPa) DN40, 48.3 × 3.7 mm → 42 MPa → 2.1 |
| `BreachIntegrity` | SF | `0.5 + 0.25 · clamp((1.5 − SF) / 0.5, 0, 1)`; SF < 1.0 is an Error. With the as-built quality term (section 4) the total is capped at 0.85 | 0.5 |
| `SeparatorW`, mass, power | Part data | sums | 2 MW, 1.1 t |

Validation also enforces exotic-matter's loader rules on the derived numbers, strictly: `SpoolTicks > MixTicks + RunawayDelay + 30`, so an off-spec burst always lands before `T0 − 30` (at C's 300-tick spool `MixTicks` must stay under 90: a 7-stage or deeper diluter is refused; up to 6 stages is legal, Tolerance ≤ 0.065), and `PrimeKg + C(MassCap, ℓ = 3) ≤ LineCapKg` (a Warning: "cannot reach the moon at full mass").

**Failure modes come out of the same graph, not a risk roll.** A thin-walled line (SF 1.1) breaches at Integrity 0.70 instead of 0.5. A missing interlock lets pure feed reach the chamber. A missing purge valve removes the counterplay. A two-stage diluter (Tolerance 0.035, `q_lim` 0.414) runs away on smaller feed errors. All of these are visible in the stats panel before anything is built.

**Thruster family** (`fn.thruster.chem`), to show the pattern on a small component: chamber pressure `P_c` from the pump's rating, throat area `A_t` from the nozzle's throat diameter, `F = C_F(ε) · P_c · A_t` with `C_F` tabulated by expansion ratio ε in data, `v_e` from propellant data × `η(ε)`, gimbal from a gimbal part's range. The chamber's pressure rating vs `P_c` gives SF exactly as above.

Families planned: warp (both kinds), chemical / AM-thermal / plasma / ion thrusters, tank, trap, reactor (fission, fusion, annihilation core), battery / capacitor, life support, plating, door / airlock, seat / cockpit, landing foot, refinery cores, fabricator. **Families without a meaningful part graph** (seat, door, sensor) accept a single "core" part whose stats are data, which is exactly ship-hulls' bought function core, so nothing is lost.

### Validation rules

| Class | Checks | Severity |
| --- | --- | --- |
| Schema | Format version, counts within limits, every `PlanRef` resolvable, parameters within template ranges, rotation snap index valid | Error |
| Geometry | Part OBBs do not interpenetrate (except declared nesting, e.g. pipe in flange); parts inside the housing bounds; component mount ≥ 70 % solid behind (ship-hulls); plates lie on hull surface within 0.05 m; ≤ 256 brushes per brick | Error |
| Connectivity | Every port connection has matching medium and size (DN, voltage); routes connect two compatible sockets; no dangling required ports | Error, Warning for optional ports |
| Function | Family roles, paths, loops; formulas within envelope; family-specific rules (SF, spool vs mix ticks) | Error or Warning per rule |
| Buildability | Assembly graph acyclic; every hull piece reachable from the keel; every stock length ≤ the longest stock bar (else a splice joint is added, Warning); each component either fits through its room's portals or gets a pre-close order edge (section 4) | Error |
| Economy | Cost quote for each sourcing mode; NRE and per-build stamp per redefined level | Info |
| Access | Every part template's `access` (tier, faction key): designing with it is always allowed; buying or producing it needs the access (section 2) | Warning in the designer, Error at build or production |

**Plan limits** (server-enforced at import and save, so a hostile file cannot cost more than one bounded validation):

| Item | Limit |
| --- | --- |
| Bundle | ≤ 1 MB packed; every section's `rawLen` ≤ 8 MB and ≤ 32 × `packedLen`, checked before inflating |
| Distinct sub-plans in a bundle | ≤ 256; nesting depth ≤ 6 (ship > component > part, hull chunk > hull chunk ≤ 3) |
| Ship | ship-hulls limits (≤ 8192 brushes, ≤ 1 km), ≤ 1024 component placements, ≤ 4096 routes, ≤ 65,536 waypoints, ≤ 1024 order edges |
| Component | ≤ 8 m housing, ≤ 256 parts, ≤ 1024 connections, ≤ 256 routes, ≤ 4096 waypoints |
| Part | ≤ 8 parameters, each inside its template range |
| Laminate | ≤ 16 layers |

## 2. Designer application

### One core, two hosts

```
src/Ships/            (ship-hulls) blueprint, brushes, bake, mass, rooms — no Vulkan
src/Resources/        (exotic-matter) items, recipes, prices, power — no Vulkan
src/Plans/            plan model, .bplan format, catalog loader, function families, validation,
                      compile (assembly graph, cut list, BOM), cost quotes, as-built model — no Vulkan, no ImGui
                      refs Ships, Resources
src/Designer/         DesignSession, ops, undo/redo, selection, tool logic (snap, sockets, route A*,
                      plate projection, cross-section), host interfaces — no Vulkan, no ImGui
                      refs Plans
src/Ships.Rendering/  hull brick renderer, instanced part meshes, ghost raymarch, hologram pass — Vulkan
                      (split out of ship-hulls' Ships.Runtime so the designer does not pull in Jolt sections)
                      refs Ships, Engine
src/Designer.Ui/      ImGui panels, viewport, gizmos, camera — refs Designer, Ships.Rendering, Engine
src/Designer.App/     Exe "BRUTALDesigner": window, FilePlanHost, job workers — refs Designer.Ui, Engine
                      no Sim, no Net, no Physics
src/Sim/              (review M0) Construction runtime (claims, step events, drones, as-built) — refs Plans
src/Game/             in-game host: DesignTerminal, GamePlanHost — refs Designer.Ui, Sim, Net
server (headless)     refs Sim → Plans. Validates, compiles, quotes, never references Designer
tests/Plans.Tests, tests/Designer.Tests
```

- **The headless server uses `Plans` only**: validation, function evaluation, assembly compile, cost quotes, certification. It never sees `Designer` (editing state) or anything with Vulkan.
- **Dependency direction (no cycles):** `Ships` must not reference `Plans`. So the types the bake reads stay in `Ships`: `ComponentDesign` remains there as the *derived* runtime view (housing, cutters, sockets, `MassMoments`, portal, a stats blob), and `Plans` compiles a `ComponentPlan` into it; the per-brush build state the bake skips on is a `Ships` type (`PieceState`), which `AsBuilt` fills.
- **Jolt-free picking and rendering:** ship-hulls picks with `TriangleBvh`, which lives in `src/Physics` (references `Brutal.Jolt`). Move it to a small Jolt-free `src/Geometry` that `Physics`, `Ships` and `Designer` all reference. Likewise `Ships.Rendering` must not reference `Terrain.Runtime` (it references `Physics`): the arena, staging and draw code it reuses from `TerrainRenderer` moves into `Engine` first.
- **Enforced, not promised:** a test in `tests/Plans.Tests` loads the `Plans`, `Designer`, `Ships`, `Resources` and `Sim` assemblies and fails if any of them references `Engine`, `Brutal.Vulkan*`, `Brutal.Glfw` or `Brutal.ImGui*`, and fails if `Designer.App` references `Physics`, `Sim` or `Net`.
- `Designer.App` adds `Ahwoo.BRUTAL.NativeFileDialog` to `src/Directory.Packages.props` (central package versions); it is in BRUTAL already, no other package is needed.
- **The editing core is headless too**, so tool logic (snapping, socket matching, route search, op invert) is unit-tested without a window.
- **Designer.App**: opens a window through `Engine` (Glfw + Vulkan + ImGui), runs bakes on `Brutal.Concurrency` workers, opens and saves files through `Brutal.NativeFileDialog`, keeps a plan library folder (`Documents/BRUTALGame/Plans/`). It loads the same `data/` folder as the game, so the catalog is identical. Fully offline.
- **In-game host**: the `DesignTerminal` function core (any ship or station) or the build-mode tablet opens the designer **full screen** with its own scene (the plan, not the world) through the same `DesignViewport`. The world keeps simulating; in multiplayer the player stands at the terminal and can be interrupted. For modifications there is also an **overlay** mode that draws the plan ghost in world space over the real ship.
- Host abstraction:

```csharp
public interface IPlanHost
{
    IPlanStore Store { get; }          // File: folder of .bplan; Game: server store + inventory items
    ICatalog Catalog { get; }          // data/ catalog; Game also filters by faction access
    QuoteContext Prices { get; }       // File: reference prices; Game: local market + owner certifications
    bool CanCommit { get; }            // File: false (no building); Game: true
    void RequestBuild(PlanHash plan, SourcingMode mode);   // Game only: production order or construction site
}
```

### Editing model

- `DesignSession` holds one `PlanDocument` (any level) plus sub-documents opened for drill-down (double-click a component → edit its plan in place; saving it creates a new component hash and offers to update references).
- **Ops** are small invertible structs (`PlanOp`, 96 B: kind, target id, before and after payloads), appended to a `NativeList<byte>` op log. Undo applies the inverse; redo re-applies. This generalises ship-hulls' "undo = brush list diffs" to parts, connections, routes and parameters.
- Every op marks dirty bits: geometry (re-bake touched bricks), function (re-evaluate the family), buildability (recompile on demand), cost (re-quote). Function and cost run on a job after the op; buildability runs on Save or when the assembly viewer is open.
- Multi-user editing later: the op log is already serialisable and ids are document-allocated, so ops can be relayed through the server with sequence numbers. Not in v1.

### Tools

| Tool | Level | Behaviour |
| --- | --- | --- |
| Hull modelling | Ship, hull chunk, housing | As ship-hulls.md: primitives, face drag, Add/Subtract/Paint, hollow, cut room, bevel, mirror, group, save group as hull-chunk prefab. Ghost raymarch while dragging, bake on commit |
| Plate designer | Ship, hull chunk | Draw a 2D outline on a hull face (projected along the face normal), pick a laminate and thickness (≥ 0.5 m wall rule, finer layups via laminate density), auto bolt pattern (spacing from data). Emits a Prism brush flagged `Armour` in an armour chunk group. Or drop a catalog plate prefab, which conforms by the same projection |
| Laminate panel | Laminate | Ordered layers (material, thickness mm); live effective density, strength, σ per m, cost |
| Part panel | Part | Template + parameter sliders within ranges + material; live mass, rating, cost; "catalog" badge when values match a catalog preset |
| Part placement | Component | Palette of templates and presets. Ports snap port-to-port (same medium and size, faces opposed, 1/16 m grid); rotation snap 15° stored as a snap index (review X2). A connection is created with the family's default joint kind, editable |
| Routing | Component, ship | Pick two ports/sockets, A* on a 0.125 m grid through free space (component) or ship-interior air and along hull inner surfaces (ship), cost favours straight runs and existing route bundles; manual waypoints; corners become elbow fittings (or bends if the plan marks the route "bent"); hull crossings become penetration fittings |
| Component designer | Component | Housing modelling + part graph + roles panel (which part fills which role, what is missing) + live stats |
| Cross-section | all | Clip plane (ship-hulls), X-ray, hide groups, plus orthographic 2D "blueprint" sheets (plan, profile, sections) for printing or the in-game plan item view |
| Stats and cost | all | Function stats with envelope bars; mass, CoM, inertia; quotes for every sourcing mode with the level breakdown (section 3) |
| Validation | all | List with severity; clicking focuses the element |
| Assembly viewer | ship, component | Steps graph (critical path highlighted), step scrubber "show the build at step N", crew-count estimate, cut list, shopping list (what to buy vs what is in inventory) |
| Diff | any | Two revisions side by side: removed, added, changed elements; strip and add steps for a modification |

### File format (`.bplan`)

Same bytes for files, saves and the network.

```
Header (36 B, little endian)
  u32 magic 'BPLN'   u16 format   u8 level   u8 flags (Bundle, Sealed)
  u64 hash (xxHash64 of BODY canonical bytes)   u64 parent   u32 dataVersion
  u16 sectionCount   u16 reserved   u32 reserved
Sections: u32 tag, u32 rawLen, u32 packedLen, u8 codec (0 raw, 1 zlib), payload
  BODY  canonical design: counted blittable arrays in fixed order; positions delta-encoded (int 1/1024 m);
        zlib via Brutal.ZLib. The only hashed section.
  DEPS  PlanRefs this plan uses; in a Bundle, embedded BODYs of every non-catalog dependency
  META  UTF-8 name, description, revision, tags, optional author display name. Not hashed
  THMB  optional 256×256 preview (≤ 40 KB)
```

- **xxHash64**: implement it in `src/Plans` (about 60 lines) instead of adding `System.IO.Hashing`; BRUTAL has none and ship-hulls needs it too.
- **Sizes:** a component of 40 parts, 60 connections, 10 routes and 100 housing brushes is about 4 KB raw, about 1.5 KB packed. A 2,000-brush ship is about 25 KB (ship-hulls) plus routes and placements, about 30 KB. A bundle with ten custom components adds about 15 KB.
- **Network:** the server keeps one BODY per hash (deduplicated across all players, bytes compared on insert). Clients fetch by hash on a cache miss, as ship-hulls already does for blueprints. A sealed component's full BODY is served only to licensees; everyone else fetching that hash gets its sealed view (housing, cutters, sockets, stats), which is all rendering and physics need.

### Plans in game, sharing, and IP

**Decision: plan bytes are not secret; certification is the value.** Anyone can copy a plan file. What costs money is the right to build it without paying the engineering fee.

- **Plan items.** In game a plan is an inventory item `PlanLicence {PlanHash, Kind, BuildsLeft, Issuer}`: `Build-N`, `Build-Unlimited`, `Modify` (derivatives inherit the parent's certification for unchanged sub-plans), `Full` (transfer of certification). Catalog plans are sold at shipyards; player plans on the **plan market** at a price the issuer sets, with an optional per-unit royalty. Licences are server inventory rows, never client data, so a copied file never copies a licence.
  - **A build is debited when its site is created** (the keel step), not at completion, so one `Build-1` cannot start several sites. Stripping a site back to nothing before 10 % of its steps are done refunds the build; later, it does not.
  - **The `Modify` licence that comes with a bought ship is bound to that ship entity:** it allows refits of that ship (section 4), with `BuildsLeft = 0`. It is not a licence to build copies or derivatives elsewhere.
- **Certification** is server state: `(OwnerId, PlanHash) → CertFlags`, where the owner is a player or an organisation (members share it). It is created by paying the engineering fee (NRE, section 3) the first time a redefined plan is committed to production or construction. Designing, saving, quoting and sharing are free. A certification never removes the per-build stamp (section 3), so an organisation that certifies once and lets anyone join saves the NRE, not the per-unit premium; builds under an organisation's certification are logged per member, and the organisation can charge members per build.
- **Standalone files import as untrusted designs.** "Import" uploads the bundle; the server re-validates and re-evaluates everything (nothing from the client is trusted) and stores it uncertified. Building it costs exactly what designing it in game would have cost, so the standalone app is never an exploit, and sharing files on forums is harmless: it saves design effort, not money.
  - A locally edited `data/` in the standalone app changes only the local preview. The server evaluates with its own data: unknown `DefId`s are Errors, a different `DataVersion` is flagged, and an "overpowered" local result simply does not survive import.
- **Copying a catalog design byte for byte** yields the catalog hash, but certification is per owner: without a licence you pay the NRE as if you had designed it, and every build then pays the design stamp instead of the (smaller) plan licence. That is the "own like-for-like costs more" rule, at any fleet size.
- **Faction and tier access lives on part templates** (`access` in `data/parts`), not only on catalog presets. Otherwise a player could design a custom diluter or separator from the open template and self-produce a recirculating drive that exotic-matter.md keeps behind faction access. Designing with any template is free and offline; buying, job-shop ordering or self-producing a part from a gated template needs that access (faction standing or a production licence for the template, plus the fabricator tier).
- **Sealed prefabs.** A player who certifies a component can manufacture and sell it as a sealed item. Non-licensees receive only the sealed view (housing, sockets, stats), never the part graph. A buyer may **strip** a sealed unit (section 4); stripping reveals the part graph as an uncertified plan, so reverse engineering is possible and costs the NRE.
- **Faction-keyed parts** (exotic-matter's auto-purge, inline assay) stay keyed whatever plan they sit in.
- **Rate limit:** imports cost the server a validation bake (about 70 ms CPU on jobs for a 2k-brush ship, no meshing, plus about 0.4 ms per custom housing brick at 1/16 m, ≤ 64 bricks per housing), so one import or server save per player per 10 s, imports queued on low-priority workers, and a per-player plan store quota (64 MB). The in-game designer validates locally while editing and only sends on Save, so editing costs the server nothing.

## 3. Economics

### Sourcing modes and the rule

A redefined level is any level whose design is not a catalog design. Each item in a plan is sourced in one of four modes:

| Mode | Who makes it | Unit price |
| --- | --- | --- |
| **Catalog** | Faction factory (jigs, bulk) | `p_wh = (in + w·t) · S · (1 + μ_v)`, sold at retail `p_wh · r_L` |
| **Job shop** | A contractor builds your design | `(in_retail + J·w·t) · (1 + μ_j)` + NRE once per owner |
| **Self-assemble** | You, from bought children | `in_retail + consumables` + your time + NRE once |
| **Self-produce** | You, from materials, on your own machines | `r_raw · m + M_rate · t` + your time + NRE once |

`in` = cost of the children (recursively, each in its own mode), `m` = material cost, `t` = labour or machine minutes, `w` = wage. **Every build of a redefined design** (part, component or ship) is multiplied by `1 + designStamp` at that level: the type inspection and registration a catalog design already has. Stamps compound per redefined level, which is what makes each deeper redefinition cost more per unit, not only once. Building a catalog design pays the plan or part licence instead.

| Knob (`data/economy/pricing.json`) | Value | Meaning |
| --- | --- | --- |
| `wage` w | 4 cr/min | labour, same step times as construction |
| `catalogScale` S | 0.85 | catalog factories' jigs and bulk buying |
| `vendorMargin` μ_v | 0.20 | catalog maker margin |
| `retail` r_L | stock 1.10, part 1.40, component 1.08 | individual purchase markup over wholesale (parts sold singly carry the most handling) |
| `yardMargin` μ_y | 0.05 | premade ship margin: the faction yard sells volume at a thin margin over its wholesale inputs |
| `jobLabour` J | 1.6 | job shop has no jigs |
| `jobMargin` μ_j | 0.35 | one-off work margin |
| `rawMarkup` r_raw | 1.15 | buying raw materials on the market |
| `machineRate` M_rate | 5.0 cr/min | own fabricator: capex amortised over rated life + power + wear (per machine tier in data). Tuned so a self-produced custom drive reaches the catalog price at about 20 units and never sits more than about 3 % under it |
| `nre` E_L | part 40, component 250, ship 150 | engineering/certification base per level |
| NRE formula | `E_L · (1 + 0.25·u + 0.01·j)` | u = distinct child designs, j = joints. A revision of a plan you are certified for (or hold `Modify` for) pays NRE on the diff only: u and j count added or changed elements |
| `designStamp` | 0.06 per build, per redefined level | must stay above `planLicence × A / X` (X = the like-for-like build's unit before stamp; 0.051 here) so a like-for-like custom ship never undercuts the catalog plans |
| `tooling` | 0.5 · m per custom part design | dies and fixtures; a job shop keeps them, self-produce makes them an item you keep |
| `partLicence` | 5 % of wholesale, once per part design | right to self-produce a catalog part |
| `componentLicence` | 5 % of wholesale, per unit | right to self-assemble or self-produce a catalog component |
| `planLicence` | 5 % of the premade price, per build | catalog ship plans |
| `buyback` | ≤ 0.8 × wholesale (items), ≤ 0.6 × premade price (ships; 0.7 with Certified build) | the most any NPC market pays; keeps every produce-and-sell loop at a loss except for real value added from mining |

Rules that fall out (the P1 tests check each, at N = 1, 5, 20 and the limit):
- **Premade < plans**: both use the catalog design; the yard buys wholesale and charges 5 %, while you pay component retail (8 %) and the 5 % plan licence. Plans cost about 3 % more money *and* your time. They are still the route for: a ship the local hangar does not stock (stock 3, restock 2 days) or a faction will not sell there, building at a remote site, refits, and building with your own produced components.
- **Plans < custom ship**: the custom hull adds the ship NRE once and the design stamp (6 %, more than the 5 % licence) on every build, so no fleet size makes a like-for-like design cheaper than the catalog plans.
- **Custom component > its parts**: parts at retail (1.40) already exceed the catalog component, and the component NRE and stamp come on top.
- **Custom parts > custom component**: each part design adds its own NRE, tooling and stamp, and job-shop pricing compounds at every level (`1.35 × 1.06` per level).
- **Self-produce closes the gap to the same price**: no retail or job markups; the machine rate stands in for the factory. The custom drive reaches parity at about 20 units and levels at 0.975; own-produced catalog components land at 0.99 of retail.
- NRE is paid once per (owner, plan), so fleets amortise it, but the stamp never amortises. Catalog prices carry the vendor's NRE amortised over a huge run (treated as 0).
- **Arbitrage invariants** (tests over the whole catalog): stripping any catalog item or premade ship and valuing every recovered child at retail yields less than its price (salvage condition sees to it: welded × 0.9, bonded and seals lost; the Wren strips to about 4,500 against 5,006); `r_raw·m + M_rate·t > buyback` for every part; a yard build of a catalog plan (5,713) costs more than buying it premade.

### Worked numbers: the Wren (15 m, 30 t) and a custom C-class recirculating drive

Catalog components of the Wren, wholesale: cockpit + 2 seats 120, chemical thruster S ×6 360, fission 1 MW 400, battery 80, propellant tank ×2 80, AM trap S 300, life support S 150, landing foot ×4 100, airlock 60: **1,650**. Hull stock: 9 t structural alloy at 0.03 + 1.5 t armour laminate at 0.18, +8 % kerf and offcut waste: **583**. Yard assembly 70 min: 280.

**The drive's parts** (catalog C recirculating plan; m = material cr, t = factory minutes):

| Part | Qty | m | t | Wholesale | Retail (×1.40) | Self-produced (1.15 m + 5.0 t) |
| --- | --- | --- | --- | --- | --- | --- |
| Reaction chamber C, 0.5 m³, Ti liner | 1 | 300 | 40 | 469 | 657 | 545 |
| Bubble emitter segment C | 8 | 34 | 7 | 63 | 89 | 74 |
| Diluter, 4-stage static mixer | 1 | 70 | 15 | 133 | 186 | 156 |
| Agent separator, centrifugal | 1 | 140 | 25 | 245 | 343 | 286 |
| Chamber buffer trap | 1 | 60 | 15 | 122 | 171 | 144 |
| Agent reservoir 0.06 m³ | 1 | 20 | 4 | 37 | 51 | 43 |
| Pump | 2 | 22 | 6 | 47 | 66 | 55 |
| Valve DN40 | 5 | 8 | 2 | 16 | 23 | 19 |
| Interlock controller | 1 | 30 | 10 | 71 | 100 | 85 |
| Stock: Ti pipe DN40 24 m, cable 60 m, flanges/seals/fasteners, housing frame + sheet | | | | 152 | 167 (×1.10) | 167 |
| **Total** | | | | **1,910** | **2,629** | **2,225** |

Catalog drive: `(1,910 + 4·75 min) · 0.85 · 1.20` = **2,255** wholesale, **2,435** retail (×1.08; exotic-matter's capex reference is 2,400, +1.5 %).

**Drive alone**, first unit / average over 5 / over 20, and the ratio to the catalog retail drive. Units include the 6 % stamp per redefined level:

| Route | Unit | Once (NRE, tooling, licences) | N = 1 | N = 5 | N = 20 | Limit |
| --- | --- | --- | --- | --- | --- | --- |
| Catalog drive | 2,435 | 0 | 2,435 (1.00) | 2,435 (1.00) | 2,435 (1.00) | 1.00 |
| Custom drive from catalog parts, **job shop** | 4,449 | 1,212 | 5,662 (2.33) | 4,692 (1.93) | 4,510 (1.85) | 1.83 |
| Custom drive from retail catalog parts, **self-assembled** | 2,802 | 1,212 | 4,015 (1.65) | 3,045 (1.25) | 2,863 (1.18) | 1.15 |
| Custom drive, **each part custom-made** by job shops | 5,675 | 1,929 | 7,604 (3.12) | 6,061 (2.49) | 5,772 (2.37) | 2.33 |
| Custom drive, catalog part designs **self-produced** from bought materials, self-assembled | 2,373 | 1,272 | 3,647 (1.50) | 2,629 (1.08) | 2,438 (1.00) | 0.975 |

(Component NRE: u = 13 distinct designs, j = 60 joints → 250 · 4.85 = 1,212. Each custom part design: 41.6 + tooling 0.5 m. Self-produced once: NRE + part licences 60.)

**Whole ship**, with the ratio to the premade price A:

| Route | N = 1 | N = 5 | N = 20 | Limit |
| --- | --- | --- | --- | --- |
| **A** Buy premade (show hangar): `(1,650 + 2,255 + 583 + 280) · 1.05` | 5,006 (1.00) | same | same | 1.00 |
| **B** Catalog plans, catalog components at retail, self-built: licence 250 + 4,217 + 641 + consumables 40 | 5,149 (1.03) | same | same | 1.03 |
| **B-own** B with every catalog component self-produced (licences; estimated at the drive's 0.99 ratio) | 5,110 (1.02) | same | same | 1.02 |
| **C** Own hull design, catalog components: (4,217 + 641 + 40) · 1.06 + ship NRE 975 | 6,167 (1.23) | 5,387 (1.08) | 5,241 (1.05) | 1.04 |
| **D** C + custom drive by job shop | 9,515 (1.90) | 7,765 (1.55) | 7,437 (1.49) | 1.46 |
| **D'** C + custom drive self-assembled from retail parts | 7,770 (1.55) | 6,020 (1.20) | 5,692 (1.14) | 1.12 |
| **E** C + custom drive with every part custom-made | 11,531 (2.30) | 9,208 (1.84) | 8,772 (1.75) | 1.72 |
| **F** C + custom drive from self-produced parts | 7,375 (1.47) | 5,577 (1.11) | 5,240 (1.05) | 1.02 |

Reading it:
- **At every N:** A < B < C < D' < D < E. No route without own production gets below the premade price, and each redefined level costs more, at first unit and at fleet scale.
- **Own production closes the margin:** F (custom drive from own-made parts) ends at 1.02, just under C (1.04): the redefined drive costs the same as the catalog one at about 20 units; B-own sits within 2 % of premade. Own-refined stock and mined ore close the last few percent in cash, not in value.
- A custom **component** never reaches the catalog price from bought parts (D' limit 1.15): the part retail markup and stamp see to it.
- **Mined materials** cut F's cash cost to about 1,060 a unit, but the ore has a market value; at opportunity cost it is F.
- If plans should instead be a money-saver against premade (time against money), flip it with `yardMargin` 0.12 and `retail.component` 1.05; that breaks the "premade cheapest" reading above and is not the default.
- Time: B for the Wren is about 70 min solo with T1 tools, 140 min with T0 hand tools, about 40 min for two players (critical path), about 12 min in a T2 bay with a jig and 4 drones. A is zero.

### Production orders

- A **production order** (job shop service at a faction station, or your own fabricator / assembler cores) takes `(PlanRef, quantity, mode)`, debits inputs and money at start, and completes as a scheduled event after `Σ t / machines`. Output: complete prefab items (sealed if the plan is). Cancelling returns the inputs of units not yet started and the money for them, less a 10 % fee; started units finish or are lost.
- A **yard build** is a production order for a whole ship: a shipyard builds your plan from components you supply or it buys, charging `J·w·t · (1 + μ_j)`. The ship appears in that yard's hangar.
- Player factories selling certified sealed prefabs on the market is how "prefabs are designable" reaches other players.

## 4. Construction gameplay

### Buying a ship

- Show hangars (`data/shipyards/*.json`: catalog ship list per faction, stock count, restock days) display catalog ships as real entities, `Frozen` in the station grid, as-built `Factory`.
- Buying transfers ownership, opens the hangar doors and grants launch clearance. The ship is fully built, propellant and batteries full. EM, AM and agent are hazardous goods: a "fly-away package" option sells a starter load at market price into the traps and tanks.
- A bought ship comes with a `Modify` licence for its own plan, bound to that ship, so it can be refitted later (section 4, Modification). It does not license copies.

### Building from plans

1. **Site.** A construction site is a ship grid in `Construction` state. It is anchored like any grid (on ground, in a station bay, or free in space) and needs a clear volume of the plan's AABB + 2 m. The first step places the **keel** piece (plan-designated, default: the largest Add brush nearest the CoM), and debits the build from the licence. Site rules, so sites cannot be used as cheap obstacles:
   - not inside a station's or another owner's no-build zone (data per station; default: 2 km and every docking lane);
   - at most 4 active sites per player (16 per organisation);
   - an untouched site decays: after 7 days without a completed step it loses its claim and becomes salvage, owner first for 24 h;
   - only the owner and the people they list may claim steps or draw from the site container.
   Anchor and world position are int64 at 1/1024 m in the grid's frame (review C13); positions inside a plan stay int32 at 1/1024 m, which covers the 1 km plan extent.
2. **Compile.** The server compiles the plan into an `AssemblyGraph` (once per plan hash, cached): steps, dependencies, joints, item needs, cut list. About 5–10 ms on a job for a 2k-brush ship.
3. **Guide.** Available steps (all predecessors done) are drawn as a holographic overlay: the next pieces as translucent ghosts at their exact pose, joints as glowing seams, required items listed. The nearest available step is highlighted; the step's card shows tool, items, time and what it unlocks.
4. **The plan is required.** The construction tool only accepts actions that match an available step: parts snap to their ghost and nowhere else. Free placement exists only in the designer. To build something different, revise the plan (a modification).

### The assembly graph

Steps are **coarse**: one step covers one element and all its joints, so a small ship is about 200 steps, not thousands. Joints are sub-items of steps, each with its own quality.

| Step kind | Target | Contains |
| --- | --- | --- |
| `Frame` | hull piece | place its frame members (cut beams), weld or bolt their nodes |
| `Skin` | hull piece | place its plates (cut sheet), weld seams, including seams to neighbouring skinned pieces |
| `Armour` | plate | place, bolt pattern |
| `Mount` | component | place the prefab, fasten its mount (bolts or welds per its mount spec) |
| `Route` | route | place pipe/cable segments and fittings, make every joint (flange, weld, clamp, crimp) |
| `Penetrate` | route × hull piece | cut the hole, fit the penetration, seal |
| `Test` | room, line, component | pressure test a line, leak test a room, power-on, drive proving fire |
| `Load` | tank, trap | fuel, agent, AM |
| `Inspect` | any set of joints | NDT scan; reveals qualities |
| `Strip` | any element | reverse of its build steps (modification only) |

**Hull pieces.** A piece is one Add brush; Add brushes under 1 m³ merge into their largest same-group neighbour. Subtract brushes are design voids, never steps; Paint is a cost, not a step. Per piece, the compiler derives:
- **frame members** analytically from the brush shape: polyhedral edges for Box, Wedge, Prism and Convex; rings and longerons for Cylinder, Capsule, Cone and Sphere; ribs every `ribSpacing` (1 m) across faces wider than 2 m. Members whose midpoint lies inside a Subtract brush are dropped. Profile from the material's spec (`data/materials`: `frameProfile`, `skinSheet`).
- **skin area and seams** from a `BrushYield` table the bake already has the data for: per brush, the surface cells it won (× 0.0625 m²) and the boundary faces it shares with each other brush (seam length to that neighbour). One extra counter array in `HullBrickJob`; deterministic on the server.
- **curved skin** (Cylinder, Capsule, Cone, Sphere faces) needs formed plates: a roller step (formed on site with a plate roller) or bought pre-formed stock.

**Dependencies (all edges are AND; one runtime predicate):**
1. `Frame(p)` is available only if the keel is placed and some neighbour of p is framed and connected to the keel (**support predicate**, checked over the piece adjacency list, so building can grow from any side). The compiler guarantees a spanning tree exists.
2. `Frame(p) → Skin(p)`, `Skin(p) → Armour(plates on p)`.
3. `Skin(every piece under a component's mount footprint) → Mount(c)`.
4. **Access rule:** if a component's smallest cross-section exceeds the largest portal (door cutter) of its room in the fully built plan, then `Mount(c) → Skin(last piece closing that room)`. The compiler picks the room's highest-numbered piece as the closing piece. This is the "you may have to follow a specific order" case that comes from geometry.
5. `Mount(both endpoint components) → Route(r)`; `Frame(crossed piece) → Penetrate(r, p) → Route(r)`.
6. `Route(every route of a line) → Test(line pressure) → Load(that line's tanks)`. Pressure test before fuel is forced.
7. `Skin(all pieces of a room) + Mount(its doors) → Test(room leak)`.
8. `Test(lines of a drive) + Mount(drive) → Test(drive proving)`; certification of a custom drive needs this test to pass once.
9. **Designer order edges** (`OrderEdge` in the plan): "install the reactor before plate 12", for anything the rules cannot infer.

**Sequence shown:** a topological order with priority (support distance from the keel, then structure before outfitting), and a critical path for a given crew size (list scheduling). Players may do any available step in any order.

### Stock, cutting and offcuts

- Stock items are bars and sheets of a profile: I-beam W100–W300, pipe DN15–DN200 by material and schedule, sheet by thickness, cable by gauge. Bought in standard lengths (6 m, 12 m bars; 2 × 6 m sheets).
- Inventory: `PieceStack {Item, LengthMm, Count, Condition, Design}`. Identical lengths stack; every distinct length is its own stack.
- **Cut list:** the compiler packs each profile's required lengths into stock bars by first-fit decreasing (kerf: saw 3 mm, laser 1 mm) and sheets by shelf packing of plate bounding rectangles (nesting is a later upgrade). The shopping list is derived from it, minus what is in inventory, offcuts included.
- **Cutting** is an inventory action at a saw (hand saw T0: 3 s per cut, bench saw: 1 s, laser cutter T2: 0.5 s and 1 mm kerf), not a graph step. "Cut for steps…" cuts everything the selected steps need in one action. A hand saw needs the player there; a bench saw or laser cutter runs the cut list as a timed job while the player works elsewhere (200 cuts are about 3.5 min on a bench saw). Any piece of the right profile and length (± 2 mm) satisfies a step; pieces are not tagged.
- **Offcuts** return as stock with their remaining length **rounded down to 0.1 m** (the lost sliver goes to scrap), so a site holds at most ~120 offcut stacks per profile instead of one per distinct millimetre length. Below `minUseful` (0.25 m bars, 0.25 m² sheet) they become scrap by mass, recyclable at a smelter at 90 % of the material (bought at 1.10 as stock, so cutting stock into scrap is always a loss).
- A required length longer than the longest bar gets a **splice** joint (butt weld or splice plate), added by the compiler with a Warning.
- **Prefabs come complete:** catalog and player prefab components, parts and plates are single items; their step is just place + fasten + connect.

### Joints, tools, time and quality

| Joint | Used for | Tool (T0 / T1 / T2) | Time | Strength rating | Removable |
| --- | --- | --- | --- | --- | --- |
| Weld (fillet, butt) | frames, skin seams, structural mounts | stick welder / MIG / laser welder | 8 / 2.5 / 1 s per m | `σ_weld · throat area · (0.4 + 0.6 q)` | cut out (grind) |
| Bolt | armour plates, mounts, flanges | wrench / impact driver / torque robot | 3 / 1 / 0.5 s per bolt | `n · proof load · (q ≥ 0.5 ? 1 : 0.7)` | yes |
| Screw | panels, light brackets, interior fit-out | screwdriver / driver | 2 / 0.6 s each | `n · pull-out · (0.6 + 0.4 q)` | yes |
| Rivet | thin skins, light structure | rivet gun | 1 / 0.5 s each | `n · shear · (0.6 + 0.4 q)` | drill out |
| Nail (powder-actuated pin) | non-structural interior, insulation, trays | nail gun | 0.3 s each | low; never structural (validation) | pry out, destroyed |
| Flange + gasket | pipe joints | wrench | 20 / 8 s | as bolts; leak if q < 0.5 | yes |
| Clamp | hoses, low-pressure lines, cable trays | hand | 4 s | low | yes |
| Crimp / solder | cables | crimper / soldering iron | 4 / 6 s | n/a; resistance fault if q < 0.4 | cut |
| Bond (adhesive) | composite laminates, seals | applicator | 2 s per m + cure 300 s (event) | `σ_bond · area · q` | no |
| Seal bead | room boundaries, penetrations | applicator | 1 s per m | n/a; leak if q < 0.45 | scrape |

Placement adds `2 s + mass / handlingRate`. Parts over 50 kg need a hoist, crane or construction drone (in zero-g too: inertia still applies).

**Quality** `q ∈ [0, 1]`, stored as a byte per joint, computed by the server at step completion, deterministic:

```
q = clamp( toolBase(tier) + 0.0015 · skill(trade) − conditions − pace + jitter, 0, 1 )
toolBase: T0 0.70, T1 0.80, T2 0.90, robot 0.95, jig 0.98
conditions: out-of-position (overhead, zero-g unbraced) 0.05 each
pace: Fast (0.6× time) 0.15, Normal 0, Careful (1.5× time) −0.05
jitter: ±0.04 from hash(shipId, jointId)
```

- **Hands-on without trusting the client:** the worker holds the tool on the seam ghost and the bead advances at tool speed; leaving the seam pauses it. The client animates; the server only checks presence (within 3 m of the step target, tool equipped) at 2 Hz for claimed steps and completes the step on its end tick.
- **Trade skills** (welding, fastening, pipefitting, electrical) rise with completed joints.
- **What quality does:**
  - structural joints set the **joint ratings** the tractor-beam stress model reads (section 5);
  - sealing joints below 0.45 create **Leak** edges in the ship-hulls room graph, area `seam length × (0.45 − q) × 2 mm`;
  - line joints below 0.5 leak fluid: agent recovery becomes `R · Π(1 − leak_j)`;
  - a drive's line quality shifts its breach threshold: `BreachIntegrity += 0.3 · max(0, 0.7 − q_line_min) / 0.7`, total capped at 0.85 (so an undamaged drive never counts as breached; above 0.7 the drive panel warns and the build cannot be Certified);
  - component mount joints scale the detach threshold of the existing detach rule.
- **Inspection:** the worker sees a coarse indicator (good / poor) per joint. An NDT scanner `Inspect` step reveals exact values and flags joints below spec. **Rework** = a strip of that joint (grind out, 50 % of its make time) and a redo.
- A ship whose joints are all inspected and at or above spec gets the **Certified build** flag: player buyers can trust it (the plan market shows it), NPC buyback rises from 0.6 to 0.7 of the premade price (never more, so building to sell to NPCs is not a money tap), and stations may require it for docking capital-class ships.

### Co-op and automation (keeping it not tedious)

- **Claims:** a worker (player or drone) claims an available step; the server holds `StepClaim {step, worker, start, end}`. A claim lapses if the worker leaves for 10 s. Items are debited at claim, refunded on lapse; a claim ends exactly once (completed or lapsed, one server state transition), so a lapse racing the end tick cannot both refund and complete. Only workers on the site's list may claim, and one player holds at most 2 claims at a time, so nobody can lock a site by claiming everything. Several workers take different available steps in parallel; a step's `MaxCrew` (data, e.g. 2 for large skins) lets two players share one step at 1.8× speed.
- **Batch actions:** "cut for selection", "fetch items for selection" (from the site's container), "weld all seams of this piece" are one action each.
- **Tiers of automation:**
  - **T1 construction rig** (deployable): a hoist, a cut bench, and 2 drones that carry and place (no joints).
  - **T2 construction bay** (station or ship module): 4–8 drones that also make welds, bolts and routes at `toolBase` 0.95, working the graph's available steps in priority order.
  - **T3 shipyard with jigs:** a jig is tooling made for one hull plan (an item). It places every frame piece of that plan at once at q 0.98 and cuts frame time to 0.3×. Jigs are what make catalog factories cheap, and players can build them too.
- **Drones are bay-bound and abstract:** no physics bodies, instanced animation only. Each drone's step is a scheduled event. A bay with 8 drones costs nothing per tick.

**Tedium risks and their mitigations** (each is a P1/M8 playtest check, measured in minutes of player input, not step count):

| Risk | Where it bites | Mitigation |
| --- | --- | --- |
| Step count scales with size | 2k-brush ship ≈ 12k steps: impossible by hand | **Work orders:** select a piece, room or the whole site and the character (or a bay) works the available steps in priority order while the player holds the action or walks away from a bay. Ships over 50 m require at least a T1 rig to start (the site says so up front). Hands-on stays for small craft, repairs and refits |
| Shopping for 40 item kinds | every build from plans | Shopping list → **"buy missing"** in one action at any market in range, delivered to the site container (delivery time by distance). Stock is still bought as long bars and cut: no pre-cut kits |
| Fetching and carrying | every step | Site container + "fetch for selection"; T1 rig drones carry; items in the container within 25 m count as in hand |
| Cutting | hundreds of cuts | Bench saw / laser run the cut list as a background job; offcuts round to 0.1 m (no stack explosion) |
| Tests and loading | every line and room | `Test` and `Load` steps are one action each with a timer; failures point at the joint |
| Rework | a botched weld means find, grind, redo | Inspect flags joints in the hologram; "rework all flagged" is one work order |
| Tiny refits | moving one plate or swapping one valve | Diff build: only the delta, NRE on the diff only, soft fields instant |
| Designing a drive from 25 parts | first custom component | "Fork from catalog" opens the catalog plan as an editable copy; the roles panel lists what is missing with a one-click "add suggested part"; auto-route all open ports; "replace part everywhere" |
| Repeat builds | fleets | Production orders and yard builds for repeats; jigs for hulls; the plan's assembly viewer shows the critical path so co-op splits the work |
| Learning curve | first ship | The first catalog plan a new player gets is a 30-step skiff (keel, 6 frames, skins, seat, thruster, tank) with guided steps |

### Partial ships

- An unfinished ship is a valid physical ship: the as-built state decides what exists (section 5). A framed hull with three skinned pieces, two thrusters and a cockpit can fly if the flight model allows it.
- **Framed pieces** are beam instances (render) plus one box sub-shape per beam in the exterior compound (≤ 24 per piece, construction only), and their beam mass as point moments. They contribute nothing to the SDF bake, rooms or shielding. When the piece is skinned, its beams' sub-shapes go and the bake takes over (hull material density already includes frames, as ship-hulls says).
- **Framed pieces take damage too.** Because they are not in the SDF, damage brushes would otherwise pass through them and an unskinned frame cage would be invulnerable armour. Any damage brush whose volume meets a beam's box destroys that beam: the piece's `Frame` step is marked undone for that member (a `Destroyed` deviation), its mass and sub-shape go, and the support predicate is re-checked (a piece no longer connected to the keel splits off by the ship-hulls rule).
- **What a partial ship cannot do:** components mount only on skinned pieces (dependency 3), a drive needs its tests (so no warp on an untested line), rooms are not sealed, and stations treat an `InProgress` ship as a construction site, not a ship (no docking-class privileges, no Certified build). A partial ship is never a way around a plan's validation: every intermediate state is a subset of a valid plan.
- **Functions work when their prerequisites are done:** a component needs its mount, its required routes and its tests. A drive with an untested line refuses to prime.
- Rooms are sealed only when all their pieces are skinned and doors mounted; until then they are space for life support.

### Modification

- Open the ship's plan (in the terminal, or overlay mode standing in the ship), edit, save: a new revision B with `Parent = A`.
- **Diff by element id:** removed elements produce `Strip` steps, added elements produce build steps, changed elements produce strip + build, except soft fields (paint, flags) which apply instantly.
- **Strip dependencies are the reverse graph:** you cannot strip a frame under a skinned piece or a plate over it; stripping a line requires `Drain` (fuel, agent) or `Purge` (EM line, exotic-matter's purge) first, and AM traps must be emptied or moved with their containment powered. These are forced.
- **Salvage:** bolted, screwed and clamped parts come back at their condition. Welded parts come back at condition × 0.9 (cut free). Stock cut out returns as shorter offcuts (lose 5 cm per cut end). Skin plates cut along seams return as offcuts; pieces cut through the middle become scrap. Components keep their `Integrity` and options. Seals, gaskets, consumables and bonded joints are lost. Stripping a catalog component into parts uses the same rule (its factory joints are mostly welded), which keeps the strip-for-parts value under the component's price (section 3 invariants).
- The as-built of B starts as "A's done steps that survive in B", so a refit only does the delta.
- **Engineering fee on a revision** is charged on the diff only (added and changed elements), if you are certified for A or hold its `Modify` licence. A revision that replaces most of a catalog ship pays about the full NRE, so "modify a bought ship" is not a back door to cheap custom designs, and moving one plate costs a few credits, not 975.
- **Revising an in-progress site** is allowed; the new revision needs its own build licence or certification exactly as a new plan would, and the original build debit is not refunded.
- **Creative mode:** saving a plan applies it instantly (`Factory` as-built). This is ship-hulls' original direct build mode, kept for creative and admins.

## 5. Runtime representation

```
AsBuilt per ship
  Plan            PlanHash
  State           Factory | InProgress | Complete        Factory: nothing else allocated, all joints = FactoryQuality
  Done            1 bit per step                          ~200 steps small ship, ~12k for a 2k-brush ship (1.5 KB)
  JointQ          1 byte per joint (0 = not made)         ~300 small, ~20k large
  Deviations      sparse 16 B each                        substitutions, rework counts
  Claims          server only, live
```

- **Factory ships:** 16 B (plan hash, state, factory quality). Every bought ship and every catalog NPC ship.
- **In progress or player built:** about 2 KB packed for the Wren; 10–25 KB raw and 4–10 KB packed for a 2k-brush ship (quality bytes cluster, zlib handles them well).
- **Deviations:** a substitution is allowed when the substitute has the same template and ports and is at or above spec (e.g. a schedule 80 pipe for schedule 40, a T2 valve for T1). It is recorded, its mass and ratings are used, and the plan is not changed. Below-spec substitution is refused.
- **Bake input:** the hull bake takes the plan's brushes plus a per-brush state span from the as-built (`Planned`, `Framed`, `Skinned`). `Planned` and `Framed` Add brushes are skipped; Subtract and interior brushes always apply. Each completed `Skin` step re-bakes 1–8 bricks, the same cost as one ship-hulls edit. **Cache key:** ship-hulls caches bricks by `(ContentHash, BrickKey)`, which would serve the finished plan's brick to a half-built ship. Partial ships key their bricks by `(PlanHash, BrickKey, hash of the piece states of the brushes binned to that brick)`; Factory and Complete ships share the plain plan key.
- **Mass:** `MassMoments` of the bake + framed beams + mounted components (their designs' `MassMoments`, deviations applied) + routes (length × kg/m per segment) + fluids. Fixed point, server authoritative; the server sends the totals with the as-built (review X3).
- **Stress graph** (for tractor-beams.md): nodes are pieces, plates and component mounts; edges join pieces that share joints, with ratings summed from those joints' strength formulas: tensile N, shear N, bending N·m. Built from the assembly graph plus `JointQ` on load and on each step event (only the touched edges). A failed edge detaches through ship-hulls' split rule. Factory ships use the plan's nominal ratings at factory quality, shared per plan hash, with no per-ship memory.
- **Network:**
  - stream-in: plan hash (blueprint fetch on cache miss) + as-built blob (16 B for factory ships);
  - `StepClaimed {ship, seq, step, worker}` 16 B; `StepDone {ship, seq, step, n, q[n]}` 12 B + 1 B per joint;
  - `PlanRevised {ship, newHash}` 20 B, then the diff steps are compiled on each peer from the two cached plans.
- **Saves:** the same as-built bytes; plans by hash in the plan store.

## 6. Performance

| Item | Cost (estimate, measure in the "Plans" and "Construction" profiler rows) | When |
| --- | --- | --- |
| Designer hull edit | 1–8 brick re-bakes, 1–16 ms CPU on workers, visible 1–2 frames later (ship-hulls) | on commit, never while dragging |
| Housing edit | 1/16 m cells, ≤ 8 m: up to 64 bricks worst case, typically 2–8 | on commit |
| Part place / connect / parameter | function evaluation 2–50 µs, cost quote ~20 µs | on commit, on a job |
| Validation (full ship) | ≤ 5 ms on a job | on save; incremental per dirty class otherwise |
| Assembly compile (2k-brush ship) | 5–10 ms on a job; cut list FFD ≤ 1 ms for 4k pieces | on save, on viewer open, server once per plan hash |
| Server import validation | ~70 ms CPU across workers, no meshing (+ ≤ 26 ms per custom housing); worst case bounded by the plan limits | rate-limited, 1 per player per 10 s, low-priority workers: 64 players at the cap is under 1 core |
| Idle or finished ship | **0 per tick** | always |
| Active construction | claim presence check at 2 Hz for claimed steps only, ~50 ns each; completions are heap events | while claimed |
| Drones | scheduled events only | per step |
| Step completion | 1–8 brick re-bakes (skin), one `MutableCompound` modify, stress-edge update O(joints in step) | per event |
| Hologram | the plan baked at **LOD 1** (about a quarter of L0's triangles and memory; review P6), cached per plan hash; drawn as one instanced pass with a per-brick mask of not-yet-built bricks, only for sites within 200 m. Only the available steps' pieces are translucent-filled; the rest of the plan is edges only (a depth-tested outline), so a capital-ship hologram does not cost full-screen translucent overdraw. Exit check: ≤ 0.1 ms GPU for a 2k-brush site filling the screen | per frame, sites near the camera |
| Framed-beam damage | damage brush AABB against the piece's ≤ 24 beam boxes, only for `InProgress` ships | per damage event |
| Memory | plan bake cache shared by hash; as-built arrays only for non-factory ships | |

Rules: no allocation in the claim check or event pop (`NativeList`, preallocated heaps); compile and evaluation scratch from pooled buffers; nothing in construction runs on the 60 Hz path except popping due events.

## 7. C# structures

```csharp
namespace BrutalGame.Plans;

public enum PlanLevel : byte { Laminate, Part, Component, HullChunk, Ship }
public readonly record struct PlanHash(ulong Value);
public readonly record struct DefId(uint Value);                  // hashed data id
public struct PlanRef { public DefId Catalog; public PlanHash Plan; }   // exactly one is non-zero

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlanHeader                                           // 36 B
{
    public uint Magic; public ushort Format; public PlanLevel Level; public byte Flags;
    public PlanHash Hash, Parent; public uint DataVersion; public ushort SectionCount, Reserved0; public uint Reserved1;
}

public struct LaminateLayer { public DefId Material; public ushort ThicknessUm10; }   // 10 µm units
public sealed class LaminatePlan { public NativeList<LaminateLayer> Layers; }

public unsafe struct PartPlan                                      // custom part = template + params + material
{
    public DefId Template, Material; public byte ParamCount; public fixed int Params[8];   // 1/1000 units, ranges in data
}

public struct PartInstance                                         // 28 B
{
    public uint Id; public ushort Dep; public ushort Flags;          // Dep: index into the plan's DEPS table (a PlanRef there)
    public int PosX, PosY, PosZ;                                   // 1/1024 m, plan-local
    public ushort RotA, RotB, RotC;                                // 48-bit smallest-three or snap index (review X2)
    public ushort Reserved;
}

public struct PortRef { public uint Element; public byte Port; }   // part instance or socket
public enum JointKind : byte { Weld, Bolt, Screw, Rivet, Nail, Flange, Clamp, Crimp, Solder, Bond, Seal }
public struct Connection { public uint Id; public PortRef A, B; public JointKind Joint; public DefId Spec; public ushort Size; }  // Size: weld cm or fastener count

public enum Medium : byte { None, Em, Slurry, Agent, Antimatter, Propellant, Coolant, Gas, Power, Data }
public struct Route { public uint Id; public DefId Spec; public Medium Medium; public PortRef From, To; public ushort FirstWaypoint, WaypointCount; public byte Flags; } // Bent, Bundle
public struct Waypoint { public int X, Y, Z; }                     // 1/1024 m

public enum ElementKind : byte { Brush, Part, Connection, Route, Placement, Test }
public struct ElementRef { public ElementKind Kind; public uint Id; }
public struct OrderEdge { public ElementRef Before, After; }

public sealed class ComponentPlan                                  // source of truth; compiles to Ships.ComponentDesign (derived view, FunctionCore → stats blob)
{
    public PlanHash Hash; public DefId Family;
    public ShipBlueprint Housing; public NativeList<HullBrush> Cutters; public NativeList<Socket> Sockets;
    public NativeList<PartInstance> Parts; public NativeList<Connection> Connections;
    public NativeList<Route> Routes; public NativeList<Waypoint> Waypoints; public NativeList<OrderEdge> Order;
    public uint MountPart;                                         // part whose joints fasten the component to the hull
}

public sealed class ShipPlan
{
    public ShipBlueprint Hull;                                     // brushes, chunks (incl. armour groups), placements, MaterialTable
    public NativeList<Route> Routes; public NativeList<Waypoint> Waypoints;
    public NativeList<OrderEdge> Order; public uint KeelBrushId;
}

// ---- Function families ----
public interface IFunctionFamily                                   // static dispatch, one struct per family
{
    static abstract bool Evaluate(in PartGraphView graph, in FamilyDef def, Span<byte> statsOut, ref ValidationLog log);
}
public readonly ref struct PartGraphView { /* CSR ports, part props, role assignment */ }

public struct WarpDriveStats
{
    public DriveClass Class; public DriveKind Kind; public DriveOptions Options;
    public long MassCapKg, LineCapMg, PrimeMg, LoopAgentMg, SeparatorW, MassG;
    public ushort ToleranceQ16, FeedMaxQ16, AgentRecoveryQ16, BreachIntegrityQ16, LineSafetyQ8_8;
    public ushort MixTicks, PurgeTicks;
}

public enum Severity : byte { Info, Warning, Error }
public struct ValidationIssue { public Severity Severity; public ushort Code; public ElementRef Element; public int Value; }
public struct ValidationLog { public NativeList<ValidationIssue> Issues; public int Errors; }

// ---- Economy ----
public enum SourcingMode : byte { Catalog, JobShop, SelfAssemble, SelfProduce }
public struct CostQuote { public long UnitMilli, OnceMilli, MaterialsMilli, LabourMilli, MarkupMilli, StampMilli, LicenceMilli, NreMilli, ToolingMilli; public int TimeS; }
public enum LicenceKind : byte { BuildN, BuildUnlimited, Modify, Full }
public struct PlanLicence { public PlanHash Plan; public LicenceKind Kind; public ushort BuildsLeft; public OwnerId Issuer; }
[Flags] public enum CertFlags : byte { None = 0, Certified = 1, Sealed = 2, Proven = 4 }

// ---- Assembly ----
public enum StepKind : byte { Keel, Frame, Skin, Armour, Mount, Route, Penetrate, Test, Load, Inspect, Strip, Drain }
public struct AssemblyStep                                         // 28 B
{
    public StepKind Kind; public byte MaxCrew; public ushort ToolMask; public ElementRef Target;
    public int FirstJoint; public ushort JointCount, ItemCount; public int FirstItem, BaseTimeMs;
}
[Flags] public enum JointFlags : byte { Structural = 1, Sealing = 2, Line = 4, Mount = 8 }
public struct JointRec { public JointKind Kind; public JointFlags Flags; public DefId Spec; public ElementRef A, B; public ushort Size; }
public struct ItemNeed { public DefId Item; public PlanHash Design; public int LengthMm; public ushort Count; }
public struct CutPiece { public DefId Profile; public int LengthMm; public int Step; }
public struct StockBar { public DefId Profile; public int LengthMm, FirstPiece, PieceCount, OffcutMm; }

public sealed class AssemblyGraph                                  // compiled once per plan hash, cached
{
    public PlanHash Plan;
    public NativeList<AssemblyStep> Steps; public NativeList<int> PredStart, Preds, SuccStart, Succs;
    public NativeList<int> PieceNeighbourStart, PieceNeighbours;   // support predicate
    public NativeList<JointRec> Joints; public NativeList<ItemNeed> Items;
    public NativeList<CutPiece> Cuts; public NativeList<StockBar> Bars;
    public NativeList<StressEdge> Stress;                          // nominal ratings per piece pair
}
public struct StressEdge { public uint A, B; public float TensileN, ShearN, BendingNm; public int FirstJoint, JointCount; }

// ---- As built ----
public enum BuildState : byte { Factory, InProgress, Complete }
// PieceState { Planned, Framed, Skinned } lives in BrutalGame.Ships (the bake reads it; Ships must not reference Plans)
public enum DeviationKind : byte { Substitute, Rework, Destroyed }
public struct Deviation { public ElementRef Element; public DeviationKind Kind; public byte Count; public ushort Member; public uint Actual; }   // 16 B; Actual indexes AsBuilt.Substitutes, Member = beam index for Destroyed
public struct StepClaim { public int Step; public EntityId Worker; public long StartTick, EndTick, LastSeenTick; }

public sealed class AsBuilt
{
    public PlanHash Plan; public BuildState State; public byte FactoryQuality;
    public NativeBitArray Done; public NativeList<byte> JointQ; public NativeList<Deviation> Deviations;
    public NativeList<Ships.PieceState> Pieces;                    // derived from Done, fed to the bake
    public NativeList<PlanRef> Substitutes;                        // targets of Substitute deviations
}

public struct PieceStack { public ItemId Item; public ushort ConditionQ16; public int LengthMm, Count; public PlanHash Design; }  // 24 B: stock, parts, prefabs

namespace BrutalGame.Designer;

public enum OpKind : byte { InsertBrush, RemoveBrush, ModifyBrush, PlacePart, RemovePart, MovePart, SetParams, Connect, Disconnect, SetJoint, AddRoute, RemoveRoute, EditRoute, PlaceComponent, RemoveComponent, AddOrderEdge, SetMeta }
public unsafe struct PlanOp { public OpKind Kind; public byte Size; public ElementRef Target; public fixed byte Before[42], After[42]; }   // 96 B (12 + 2·42), invertible; payload fits a 36 B brush or a 28 B part
public sealed class UndoStack { public NativeList<byte> Log; public int Cursor; }
[Flags] public enum Dirty : byte { Geometry = 1, Function = 2, Buildability = 4, Cost = 8 }
public sealed class DesignSession
{
    public PlanDocument Doc; public UndoStack Undo; public Selection Selection; public Dirty Dirty; public IPlanHost Host;
    public void Apply(in PlanOp op);                               // mutate, push inverse, set dirty bits
}
```

## 8. Data

```json
// data/parts/chamber.json   (template; presets are the catalog parts)
{ "id": "part.chamber", "tags": ["Chamber"], "params": { "volumeM3": [0.05, 8.0], "wallMm": [4, 60], "linerMm": [0, 10] },
  "materials": ["mat.titanium", "mat.alloy.hs"], "liner": "mat.ceramic", "access": { "tier": 1, "faction": null },   // part.diluter and part.separator: tier 2, faction-gated (exotic-matter)
  "ports": [ { "name": "feed", "medium": "Em", "dn": "param:feedDn" }, { "name": "am", "medium": "Antimatter" },
             { "name": "agentOut", "medium": "Agent" }, { "name": "mount", "medium": "None" } ],
  "massFormula": "shell", "machineMinPerKg": 0.033, "pressureFormula": "barlow",
  "presets": [ { "id": "chamber.c.0_5m3", "volumeM3": 0.5, "wallMm": 18, "linerMm": 4, "material": "mat.titanium" } ] }

// data/stock/pipe_ti.json
{ "id": "stock.pipe.ti", "kind": "Pipe", "material": "mat.titanium",
  "sizes": [ { "dn": 40, "odMm": 48.3, "wallMm": 3.7, "kgPerM": 2.4, "crPerM": 2.0 } ],
  "lengthsMm": [6000, 12000], "minUsefulMm": 250, "elbow": "fitting.elbow.ti", "bendable": true }

// data/joints/weld.json
{ "id": "joint.weld", "kind": "Weld", "tools": { "T0": "tool.welder.stick", "T1": "tool.welder.mig", "T2": "tool.welder.laser" },
  "secondsPerM": { "T0": 8, "T1": 2.5, "T2": 1 }, "strength": "sigmaWeld*throat*(0.4+0.6q)", "consumablePerM": { "item": "weld.rod", "kg": 0.08 } }

// data/functions/warp_recirc.json
{ "id": "fn.warp.recirc", "evaluator": "WarpRecirc", "stats": "WarpDriveStats",
  "roles": { "Chamber": [1,1], "Emitter": [4,32], "Diluter": [1,1], "Separator": [1,1], "Reservoir": [1,1], "Pump": [1,4],
             "Interlock": [0,1], "PurgeValve": [0,1], "Trap": [1,1], "InlineAssay": [0,1], "AutoPurge": [0,1], "EjectRail": [0,1] },
  "paths": [ { "medium": "Em", "via": ["Socket:Fuel", "Interlock?", "Diluter", "Chamber"] } ],
  "loops": [ { "medium": "Agent", "via": ["Reservoir", "Pump", "Diluter", "Chamber", "Separator"] } ],
  "coeff": { "rhoLine": 150, "rhoAgent": 1400, "tolBase": 0.02, "tolPerStage": 0.0075, "tolCap": 0.08,
             "mixBase": 60, "mixPerExtraStage": 10, "purgeRefDn": 40, "pOpMPa": 20.0, "breachBase": 0.5, "breachSfGain": 0.25 },
  "envelope": { "lineCapKg": [1, 600], "loopAgentKg": [5, 600] } }

// data/economy/pricing.json
{ "wage": 4.0, "catalogScale": 0.85, "vendorMargin": 0.20, "retail": { "stock": 1.10, "part": 1.40, "component": 1.08 },
  "yardMargin": 0.05, "jobLabour": 1.6, "jobMargin": 0.35, "rawMarkup": 1.15, "machineRate": 5.0, "designStamp": 0.06,
  "nre": { "part": 40, "component": 250, "ship": 150, "perDistinct": 0.25, "perJoint": 0.01 },
  "toolingPerMaterial": 0.5, "partLicence": 0.05, "componentLicence": 0.05, "planLicencePerBuild": 0.05,
  "buyback": { "itemOfWholesale": 0.8, "shipOfPremade": 0.6, "shipCertified": 0.7 }, "revisionNreOnDiff": true }

// data/construction/quality.json
{ "toolBase": { "T0": 0.70, "T1": 0.80, "T2": 0.90, "robot": 0.95, "jig": 0.98 }, "skillPerLevel": 0.0015,
  "conditions": { "outOfPosition": 0.05, "zeroGUnbraced": 0.05 }, "pace": { "Fast": [0.6, 0.15], "Careful": [1.5, -0.05] },
  "jitter": 0.04, "leakSeal": 0.45, "leakLine": 0.5, "claimLapseS": 10, "presenceHz": 2, "reachM": 3 }

// data/shipyards/faction_a_hangar.json
{ "id": "yard.faction_a.prime", "showHangar": [ { "plan": "ship.wren", "stock": 3, "restockDays": 2 } ],
  "services": ["JobShop", "YardBuild", "Inspect"], "planMarket": true }
```

Also: `data/plans/catalog/*.bplan` (every catalog part preset, component and ship, authored in Designer.App), `data/materials/*.json` gains `frameProfile`, `skinSheet`, `ribSpacing`, `formedPlate`, `crPerKg`, `sigmaWeld`; `data/tools/*.json`; `data/fabricators/*.json` (machine tier, rate, power, `machineRate`).

## 9. Changes to other docs

**ship-hulls.md**
- `ComponentPlan` (in `Plans`) becomes the source of truth for a component: `Family` + part graph replace `FunctionCore`. `ComponentDesign` stays in `Ships` as the derived view the bake and runtime read (housing, cutters, sockets, `MassMoments`, portal, stats blob), compiled from the plan, so `Ships` never references `Plans`. A "bought function core" is a catalog component plan, sealed. Answers its open question 4.
- `ShipBlueprint.Materials` becomes a table of `PlanRef` (catalog material or laminate plan); `HullBrush.Material` still indexes it.
- `HullChunkGroup` gains a kind (`Hull`, `Armour`); armour plates are armour chunk groups of laminate brushes, bolted on.
- **Build mode edits plans, not the live ship.** The live ship changes only through construction steps, or instantly in creative mode. The reason: plans must be the required guide, and modifications need a diff between two plan revisions. The server still validates every edit, now at plan save and import.
- Undo is the Designer op log (generalises brush diffs).
- `HullBrickJob` also writes a `BrushYield` table (surface cells and shared boundary faces per brush) for the assembly compiler, and accepts a per-brush `PieceState` span (a `Ships` type) from the as-built. Partial ships key cached bricks by piece states too (section 5).
- `Ships.Runtime`'s renderer moves to `Ships.Rendering` so the standalone designer does not reference Jolt. `TriangleBvh` moves from `Physics` to a Jolt-free `src/Geometry`, and the `TerrainRenderer` arena/staging code it reuses moves into `Engine`, for the same reason.
- `SocketType` gains `Agent` and `Antimatter` (as exotic-matter.md said); sockets carry a size (DN, voltage) for route matching.
- Open question 5 (survival building) is answered by this doc.

**exotic-matter.md**
- Per-design drive numbers (`LineCapKg`, `PrimeKg`, `LoopAgentKg`, `Tolerance`, `AgentRecovery`, `FeedMax`, options, purge) come from the drive's component plan; `data/drives/*.json` keeps class constants (`VCap`, `EmPerLn`, `MassMin`/`MassCap`, spool, cooldown, `EmMul`, mix window). The catalog C plans must evaluate to its table exactly.
- Options (inline assay, auto-purge, eject mount, quality separator) are parts.
- Breach threshold per drive from line safety and as-built line quality, capped at 0.85 (its 0.25 purge-valve and 0 rupture thresholds are unchanged).
- The EM line's operating pressure is a data knob (`pOpMPa`, 20 MPa); the SF table here assumes it.
- The loader rule becomes strict (`SpoolTicks > MixTicks + RunawayDelay + 30`) now that `MixTicks` varies per design, so the "off-spec bursts land before `T0 − 30`" guarantee holds for every legal diluter.
- Faction access for recirculating drives moves onto the diluter and separator part templates, so a custom design cannot route around it.
- Option parts and the slurry C plan must price, through the pricing model, within 5 % of its capex table (assayer 480, auto-purge 240, eject mount 240, quality separator 400, slurry C 800; a P1 test).
- Discrete items (stock, parts, prefabs, plans) use `PieceStack`; `ItemStack` stays for bulk resources.
- Capex numbers become catalog retail prices from the pricing model (C recirculating 2,435 against its 2,400 reference).

**review.md / README.md**
- New projects: `Plans`, `Designer`, `Designer.Ui`, `Designer.App`, `Ships.Rendering`; `Sim` references `Plans`.
- Milestones below go into M5 and M8; new P1 and C2.
- README: this doc is no longer pending; questions 6 and 8 answered.

**tractor-beams.md (pending)**
- Consumes `StressEdge` ratings (nominal per plan, as-built per ship) and detaches through ship-hulls' split rule.

## 10. Milestones

| When | Scope | Exit check |
| --- | --- | --- |
| **M5** (minimal hull) | `Plans` (ship and component levels with catalog cores only), `.bplan` v1, xxHash64, plan store by hash; `Designer` core (session, ops, undo, validation schema/geometry); `Designer.App` with box/wedge/cylinder hull tools and component placement; in-game DesignTerminal with the same UI; creative instant build | Build a 15 m craft in the standalone app in about 5 min, save, import in game, instant-build; the same file loads byte-identical in both hosts; plan hash identical on client and server (server re-canonicalises); the assembly-reference test passes (no Vulkan, ImGui or Glfw in `Plans`, `Designer`, `Ships`, `Sim`) |
| **M5b** (after M6, which brings thrust and the flight computer) | Construction v1: assembly compile (keel, frame, skin, mount, route, test, load), support predicate, access rule, `BrushYield`, cut list, `PieceStack` stock and cutting, weld and bolt joints with quality, claims, hologram guide, as-built (Factory and InProgress) in bake and mass, show hangar purchase, catalog plan licences, pricing modes Catalog and SelfAssemble | Solo-build the catalog Wren from plans in ≤ 75 min with T1 tools; compile ≤ 10 ms for a 2k-brush plan; idle and finished ships 0 per tick in the profiler; a half-built Wren with the cockpit and thrusters flies |
| **P1** (with exotic-matter D1, after M7) | Parts and component designer: part templates, laminates, part graph, ports, routing A*, function families (chemical thruster, tank, trap, warp slurry and recirculating), validation function class, NRE and certification, job shop and production orders, fabricators (SelfProduce), plan market and licences, untrusted import | Catalog C recirculating plan evaluates exactly to exotic-matter's C row; a custom drive with SF 1.1 breaches at Integrity 0.70 under the shared damage rule; the Wren worked numbers reproduce within 1 %; the cost ordering A < B < C < D' < D < E holds at N = 1, 5, 20 and the limit for every catalog ship and drive, and self-produce ends within 3 % of catalog; the strip, buyback and yard-build arbitrage invariants hold over the whole catalog; a fuzzed import corpus (truncated, inflated, colliding-hash, over-limit, unknown-id, modded-data bundles) is refused within the plan limits' time bound; evaluation ≤ 50 µs |
| **M8** (with rooms) | Plates as armour chunk groups, seal joints and Leak edges from quality, inspection and rework, modification diffs (strip, drain, salvage), co-op claims with MaxCrew, T1 rigs, T2 bays with drones, jigs, Certified build flag, stress edges handed to tractor beams | A room with a 0.3 q seam leaks at the predicted area; a refit from A to B does only the delta and returns salvage as specified; two players halve a 200-step build within 15 %; a damage brush through an unskinned frame removes the beams it meets; a refit that moves one plate charges NRE on that plate only; tedium playtest: the Wren from plans needs ≤ 15 min of player input with a T1 rig (the rest is timers and drones) |
| **C2** (later) | Sealed prefab sales, reverse engineering by strip, multi-user design sessions, plate nesting, pipe bending | |

## 11. Files

- **`src/Plans/`:** `PlanFormat.cs` (header, sections, canonical BODY, bundle), `XxHash64.cs`, `PlanStore.cs`, `Catalog.cs` (data loader, `DefId`), `Laminates.cs`, `Parts.cs` (templates, presets, derived props), `ComponentPlan.cs`, `ShipPlan.cs`, `PartGraph.cs` (CSR, role assignment, path/loop BFS), `Families/WarpDrive.cs`, `Families/Thruster.cs`, `Families/Tank.cs`, `Families/Core.cs` (single-core families), `Validation.cs`, `Pricing.cs` (modes, NRE, quotes), `Certification.cs`, `Licences.cs`, `AssemblyCompiler.cs` (pieces, members, dependencies, access rule), `CutList.cs`, `StressGraph.cs`, `AsBuilt.cs`, `PlanDiff.cs`, `ImportGuard.cs` (limits, inflate bounds, canonical re-encode, byte-compare dedup).
- **`src/Geometry/`** (Jolt-free, moved from `Physics`): `TriangleBvh.cs`.
- **`src/Designer/`:** `DesignSession.cs`, `PlanOps.cs`, `UndoStack.cs`, `Selection.cs`, `Tools/HullTool.cs`, `Tools/PlateTool.cs`, `Tools/PartPlacer.cs` (port snapping), `Tools/Router.cs` (A*), `Tools/CrossSection.cs`, `IPlanHost.cs`.
- **`src/Designer.Ui/`:** `DesignViewport.cs`, `Gizmos.cs`, panels: `HullPanel`, `PlatePanel`, `LaminatePanel`, `PartPanel`, `ComponentPanel`, `StatsPanel`, `CostPanel`, `ValidationPanel`, `AssemblyPanel`, `DiffPanel`, `LibraryPanel`.
- **`src/Designer.App/`:** `Program.cs`, `DesignerApp.cs`, `FilePlanHost.cs`.
- **`src/Ships.Rendering/`:** `HullRenderer.cs`, `PartInstancer.cs`, `GhostRaymarch.cs`, `HologramPass.cs`; `shaders/hologram.*`, `shaders/part_instance.*`.
- **`src/Sim/Construction/`:** `ConstructionSites.cs`, `StepClaims.cs`, `StepEvents.cs`, `Drones.cs`, `Jigs.cs`, `ProductionOrders.cs`, `ShowHangars.cs`.
- **`src/Game/`:** `DesignTerminal.cs`, `GamePlanHost.cs`, `ConstructionTool.cs` (hologram, cutting, joint tools).
- **`src/Net/`:** `PlanEvents.cs` (fetch by hash, import, revise), `ConstructionEvents.cs`.
- **`tests/Plans.Tests`:** format round trip (bytes identical, hash stable), catalog drive evaluation equals the exotic-matter table, SF → breach threshold, NRE and quote numbers equal the worked tables, compile determinism (same graph on two runs and two threads), access rule, support predicate, cut list waste ≤ FFD bound, diff/strip ordering, cost ordering and arbitrage invariants (section 3), import fuzz corpus, assembly-reference rule (no Vulkan/ImGui/Glfw in headless assemblies). **`tests/Designer.Tests`:** every op inverts exactly; undo/redo of 10k random ops returns the original bytes; router finds paths and respects clearance.

## 12. Open questions for the user

1. **Hands-on joint quality.** Quality here comes from tool, trade skill, pace and conditions; the player's hands only move the bead. Do you want a skill minigame (keeping a weld bead in a band) to affect quality? It feels better but has to be trusted from the client, or replayed on the server from input samples.
2. **Fleet amortisation.** As written, the per-build design stamp means a custom design never beats the catalog per unit without own production, however many you build (your rule taken literally), and a self-produced custom drive reaches catalog parity at about 20 units. Should big fleets be rewarded instead (`designStamp` 0: a like-for-like custom ship then beats premade after about 10 units)?
3. **Standalone distribution.** Is Designer.App a free separate download (anyone can design without owning the game) or bundled with the game? A free app needs the catalog data shipped with it, which is fine under "bytes are not secret".
4. **Construction length.** About 70 min solo with T1 tools for a 15 m ship, 140 min with T0 hand tools, minutes with a T3 jig. Is that the right order of magnitude for an early ship?

## Review notes

Lead review, 2026-11-25. The user decisions were not re-litigated. Every correction below has been made in place above; numbers were recomputed with a script (`sdc/econ2.py` in the review scratchpad) that first reproduced the original tables exactly.

User decisions, checked:
- **Designer application, same in game, save/load plans: present and faithful.** One headless core, a standalone `Designer.App` and the in-game terminal/tablet host the same `Designer.Ui`; `.bplan` bytes are identical across files, saves and network.
- **3D-modelled hull, plates custom or from prefabs, prefabs designable: present.** Plates are laminate hull chunks; added that a plate may nest prefab plate pieces and carry prefab components, which is the literal "made using prefab components".
- **Ship > components > parts: present.** Function derived from the part graph; the catalog C plan must evaluate to exotic-matter's C row.
- **Cost ordering: present in words, broken in numbers. Fixed.**
  - The plans route (B) was 0.92 of premade, so building the catalog ship yourself from bought components was cheaper than buying it, against "premade is cheaper than your own like-for-like" and against "own production can edge it to the *same* price" (which implies nothing without own production is cheaper). The summary even said "premade < plans" while the table said the opposite.
  - NRE was the only redefinition cost and it amortises, so at N = 5 the custom hull (0.93), at N = 20 the self-assembled custom drive ship (0.97) and the own-production route (0.87) all undercut premade.
  - Fix: `yardMargin` 0.12 → 0.05, component retail 1.05 → 1.08, `planLicence` 2 % → 5 %, a per-build `designStamp` (6 %, compounding per redefined level, must exceed `planLicence·A/X` = 0.051), `machineRate` 4.5 → 5.0. Now A < B < C < D' < D < E at N = 1, 5, 20 and the limit; the custom drive costs more than its parts at every N; per-part manufacture costs more again; own production reaches the catalog drive price at about 20 units and levels at 0.975, and own-produced components put the plans route within 2 % of premade. The catalog C drive retails at 2,435 (exotic-matter reference 2,400). Open question 2 now offers the fleet reward as an opt-in (`designStamp` 0).
- **Fly out of the show hangar, plans as a required guide with order, cut long stock, prefabs complete, weld/screw/nail steps: present and faithful.** No pre-cut kits were added in the tedium fixes, so "you have to cut" stands.

Consistency:
- **Dependency cycle:** `Ships`' bake read `PieceState` and the replaced `ComponentDesign` from `Plans`, while `Plans` references `Ships`. `ComponentDesign` now stays in `Ships` as the derived view and `PieceState` is a `Ships` type.
- **Hidden Jolt/Vulkan pulls:** build picking used `TriangleBvh` from `src/Physics` (references `Brutal.Jolt`), and `Ships.Rendering` would reuse `TerrainRenderer` code from `Terrain.Runtime` (references `Physics`). Both move (to `src/Geometry`, and into `Engine`). "No Vulkan in the core" is now a test over assembly references, not a promise. `Engine` itself references only BRUTAL packages, so `Designer.App` stays free of Sim, Net and Physics. `Brutal.NativeFileDialog` and `Brutal.ZLib` exist in BRUTAL; no NuGet package is added.
- **Server validation:** `Plans` alone validates, evaluates, compiles and quotes; added server-side re-canonicalisation, byte-compare on dedup, plan limits and inflate bounds, so a server can validate any file in bounded time.
- **exotic-matter:** the SF example did not follow from its own data (Barlow on 48.3 × 3.7 mm Ti at 4 MPa is SF ≈ 10, not 2.1); `P_op` is now 20 MPa with grade 2 Ti. "A 9-stage diluter is refused" was wrong (8 already fails `≥`), and with per-design `MixTicks` the loader rule must be strict for the burst-before-lock-in guarantee: 7+ stages are refused. The breach threshold with the quality term could exceed 1.0 (an undamaged drive "breached"); capped at 0.85. Faction gating of recirculating drives was bypassable by designing a custom diluter from an open template; access now sits on templates.
- **review.md:** C13 noted (site anchors int64, plan-local int32 within 1 km); X2 kept (48-bit rotation in parts); X3 kept; P6 respected by baking the hologram at LOD 1. M5b's exit check (a half-built Wren flies) needs M6's flight model, so M5b now follows M6.
- **Struct sizes:** header 36 B (not 40), `AssemblyStep` 28 B (not 24), `PartInstance` 28 B via a DEPS index (a full `PlanRef` made it 48 B), `PlanOp` payloads 42 B to reach the stated 96 B, `Deviation` 16 B via an index.

Exploits found and closed:
- **Plan duplication:** licences are server rows; a build is debited at keel, not completion (one `Build-1` could start many sites); a bought ship's `Modify` licence is bound to that ship; a byte copy of a catalog plan pays NRE and then the stamp per build; organisation certification saves NRE only.
- **Standalone injection:** server never trusts header hashes or client stats; xxHash64 collisions cannot alias a stored plan (bytes compared, catalog preloaded); zip-bomb and size limits; modded local `data/` only changes the local preview; sealed component bodies are served only to licensees.
- **Arbitrage loops:** NPC buyback capped (items 0.8 × wholesale, ships 0.6/0.7 × premade; "Certified build at catalog parity" was a money tap for anyone with cheap materials); strip-for-parts and strip-a-premade values stay under the price (tested); yard builds cost more than premade; cutting stock into scrap is a loss; production-order cancel has a fee.
- **Partial ships:** unskinned frames were invisible to damage brushes (invulnerable cages); beams now take damage. Sites have no-build zones, caps, decay and an ACL; claims are capped per player and end exactly once (no refund-and-complete race). Partial ships cannot mount components on bare frames, warp on untested lines or get docking privileges.
- **Revisions:** NRE on the diff only, so small refits are cheap while "modify a bought ship into something else" still pays nearly full NRE.

Tedium: a risk table with concrete mitigations was added (work orders, one-action "buy missing" with delivery, background cutting, 0.1 m offcut rounding, one-action tests, rework orders, fork-from-catalog and suggested parts in the designer, a 30-step first skiff) plus an M8 playtest exit: the Wren from plans with ≤ 15 min of player input on a T1 rig.

Performance: estimates were plausible and per-tick cost stays event-driven. Added: the partial-ship bake cache key (otherwise a half-built ship is served the finished plan's bricks), hologram at LOD 1 with edge-only drawing beyond the available steps and a 0.1 ms GPU exit check, framed-beam damage cost, housing bake cost in imports and a server-load bound for 64 players. Nothing here is measured yet; every number stays a profiler-row exit check.

Still open for the user: questions 1–4 above, with question 2 rewritten around the new default.
