# Ship design and construction

Status: design only. Nothing built, no repo files changed. Written 2026-11-25 against README.md, ship-hulls.md, exotic-matter.md, frames-and-zones.md and review.md. **ship-hulls.md is the base**: blueprint = CSG brush list, `ContentHash` prefabs, `HullBrush`, 0.25 m SDF bricks, components = modelled housing + function. Where this doc changes it, the change and the reason are under "Changes to other docs". Review.md's rulings (C13 int64 1/1024 m positions, X2 rotation bits, X3 server-sent mass) still win. Every number is a knob in `data/`; every cost and timing is an estimate to measure. **Round 5** (2026-11-25) applies user decisions D9, D10, D11, D12–D14, D17 and D25 (hull mesh editing, wiring, aero, dampener, ejectable cores, linked drives, station pods and thrusters); the changes are listed under "Revision (round 5) notes" at the end and win over older text here. **Round 6** (2026-11-26) applies D55, D59, D64 and D67–D71 (shield emitters and suit lockers as components, repair of dents and bends, no weld minigame, no fleet discount, the designer bundled with the game, construction time confirmed, wiring is power and data only); see "Round 6 notes" at the end, which win over everything older, including the round 5 notes' mentions of a Control medium.

## Recommendation in one paragraph

Everything a player can design is a **plan**: a versioned, content-hashed document at one of five levels (laminate, part, component, hull chunk, ship). Materials are data, never plans. A **prefab** is any plan that has been certified and published. One **Designer core** library (no Vulkan, no ImGui) owns the document model, edit operations, undo, validation, cost quotes and assembly-order compilation. Two hosts show the same ImGui UI: **Designer.App**, a standalone executable **bundled with the game** (D69) with its own window and plan files on disk (no Sim, no network), and the **in-game host**, a design terminal or build-mode tablet whose plans live on the server as inventory items. A component's function is **derived from its parts**: a data-defined function family (warp drive, thruster, tank...) declares roles, required flow paths and formulas, and a cheap deterministic graph evaluation turns the part graph into the same stats struct the runtime already uses. Cost follows one rule: **every level you redefine pays an engineering (certification) fee once, a type-inspection stamp on every build, and loses catalog economies of scale**, so premade < plans < custom ship < custom component < custom parts **at any fleet size**, and only your own fabrication capacity closes the gap, to about the same price. Buying a ship hands you a finished ship in the show hangar. Plans give you a construction site, a **dependency graph of steps** shown as a holographic overlay, stock that must be cut (I-beams and pipes as long bars, offcuts kept), and joints that must be made (weld, bolt, screw, rivet, nail, flange, crimp, bond), each taking a set time and getting a quality from the worker's skill tier alone (D67: no minigame), which feeds leaks, component integrity and the stress model. Construction is event-driven: an idle or finished ship costs nothing per tick.

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

Round 5 decisions (decisions.md):
- **D9:** plating gives gravity; a **separate inertial dampener** keeps everything inside its bubble at the ship's speed, using exotic matter and energy, up to a rating; beyond it occupants and cargo feel the excess. → "Inertial dampener family".
- **D10:** both pure 6-DOF with flight assist **and** aerodynamic lift in atmosphere; **D85:** that lift comes from shape (every exposed face), control surfaces are hinged moving parts. → "Aero: wings, control surfaces and the flight computer".
- **D11:** full mesh editing of hulls (vertex / edge / face), still cuttable. → mesh brushes (ship-hulls.md, "Mesh brushes"), the Mesh editor tool, mesh pieces in construction.
- **D12/D13:** a deliberate runaway override exists; the reaction chamber is very highly rated and the **entire core can be ejected**. → "Ejectable core assembly and override".
- **D14:** several warp drives on one ship support each other's bubbles; quality is an average weighted by provided power. → "Linked warp drives".
- **D17:** crew move freely mid-warp; engineers and many other things can cancel the warp; components have **wiring**, so what controls what is the ship's wiring setup. → "Wiring".
- **D25 (hardware part):** anchors usable on any grid, with inputs for a slow crawl; stations have thrusters and escape pods. → "Escape pods, station thrusters and anchors".
- **D26/D28 (r6, runtime owned by tractor-beams.md):** tractors are a weapon unless requested (consent is a runtime flag, nothing in the plan); a tractor emitter with no AM feed routed and fed is pull only. → the designer shows the derived drive kind (section 9, tractor-beams.md).
- **D27 (r6):** station sieges are allowed and factions prefer capture to destruction. → the `CommandCore` family (ownership token) in "Escape pods, station thrusters and anchors"; the two-feed anchor rule keeps destruction hard.
- **D33 (r6):** fragments from legal mining near a station are the miner's liability; rings-and-asteroids.md's liability rule on the shared `IncidentRecord` (owned by factions-and-law.md), nothing in plans.

Round 6 decisions (decisions.md):
- **D55:** ships get particle, energy and gravity shields. → shield emitters are component families; rules and numbers are combat-and-sensors.md section 3 ("Shields, weapons and sensors as components").
- **D59:** hull damage is holes, dents and deformation. → ship-hulls.md stores them; the repair steps are in "Modification" (rules in combat-and-sensors.md "Repair").
- **D64:** EVA suits are not worn by default; suit lockers sit by airlocks, in hangars and in crew rooms. → "Suit lockers".
- **D67:** no weld minigame; welding takes a set time; unskilled welders make poor-quality welds. → "Joints, tools, time and quality".
- **D68:** no fleet discount; fleets generally use their faction's ships. → section 3 (`designStamp` stays, no opt-in, no quantity pricing).
- **D69:** the standalone designer is bundled with the game. → "One core, two hosts".
- **D70:** about 70 minutes solo for a 15 m ship with basic tools is the right order of magnitude. → confirmed; the M5b exit check stands.
- **D71:** wiring has two media, power and data; a data cable can be split to all inputs and outputs but is one-way. → "Wiring" (the round 5 Control medium is removed).

This answers ship-hulls.md open questions 4 (player-made components: function from parts, not from housing geometry) and 5 (survival building: plans + bought or made parts + construction steps; creative mode builds instantly), and README questions 6 and 8.

## 1. Design data model

### Levels

| Level | What it is | Made of | Designed in | Catalog examples |
| --- | --- | --- | --- | --- |
| Material (data) | Density, strength, price, visual. Never a plan | n/a | n/a (`data/materials`) | structural alloy, titanium, copper, ceramic, seal polymer |
| **Laminate** | A layered material: ordered layers of sheet materials with thicknesses. Yields an effective density, strength, shielding σ, cost | materials | Laminate panel | `lam.armour.ceramic_ti`, `lam.hull.std` |
| **Part** | A parametric template instance: template + parameter values + material. Stock parts (beam, pipe, sheet, cable, fastener) and machined parts (chamber, valve, pump, mixer, emitter, nozzle...) | materials | Part panel (parameters only, no modelling) | `pipe.ti.dn40`, `chamber.c.0_5m3`, `valve.dn40` |
| **Component** | Housing (brushes, ≤ 8 m, 1/16 m cells, as ship-hulls) + part graph (parts, connections = joints, internal routes) + function family | parts, laminates | Component designer | `drive.warp.c.recirc`, `thruster.chem.s` |
| **Hull chunk** | A saved group of hull brushes (ship-hulls `HullChunkGroup` prefab), primitive or mesh (round 5). **Armour plates are hull chunks** whose brushes use a laminate; **wings are hull chunks** of kind `Wing` | brushes, meshes, laminates | Hull (primitive and Mesh modes), plate and wing tools | `plate.armour.2x2`, `chunk.nose.cone4m`, `wing.delta.12m` |
| **Ship** | Hull brushes + component placements + routes between sockets (pipes **and wiring**) + order constraints + keel + plan class (Ship, Station) | everything above | Ship designer | `ship.wren` |

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
| Options | Parts: inline assayer on feed path, auto-purge controller (faction-keyed part), quality separator, auto-eject controller, override key switch (round 5) | presence | none |
| Core eject (round 5, D13) | Eject cradle part (= exotic-matter's standard core-eject mount, its own housing at `Rating` 4) + a quick-disconnect on every route crossing the core boundary + an eject path to a blow-out panel (ship-hulls) | required for recirculating drives; optional for slurry. Missing quick-disconnect = Error ("core tethered") | cradle C, 6 quick-disconnects |
| Chamber rating (round 5, D13) | Chamber wall vs its own operating pressure `pChamberMPa` | `SF_chamber ≥ 3.0` (Error below) is what certifies the chamber housing at exotic-matter's `chamberRating` (8: its integrity loss is divided by 8) | chamber wall re-sized to meet it (P1 test) |
| Arrival beacon (round 5, D18) | Beacon antenna part (powered) fed by a Data cable from the drive's `Beacon` output (one-way, drive to antenna; warp.md "Arrival beacon") | required on every drive (warp.md: every drive class has one; Error if absent or unwired at certification). Only military-class drives (warp.md's data flag; for a designed drive it comes only from a faction-gated military emitter or controller part template, so a custom plan cannot claim it) expose a `BeaconSwitch` input; on any other drive the beacon goes silent only by cutting, destroying or unpowering it (a `Logged` tamper) | 1 beacon antenna |
| Line safety SF | Every pipe and fitting on the EM line | `SF = min(P_rating) / P_op`, Barlow `P_rating = 2·σ_y·t / D_o`, `P_op` 20 MPa | Ti grade 2 (σ_y 275 MPa) DN40, 48.3 × 3.7 mm → 42 MPa → 2.1 |
| `BreachIntegrity` | SF | `0.5 + 0.25 · clamp((1.5 − SF) / 0.5, 0, 1)`; SF < 1.0 is an Error. With the as-built quality term (section 4) the total is capped at 0.85 | 0.5 |
| `SeparatorW`, mass, power | Part data | sums | 2 MW, 1.1 t |

Validation also enforces exotic-matter's loader rules on the derived numbers, strictly: `SpoolTicks > MixTicks + RunawayDelay + 30`, so an off-spec burst always lands before `T0 − 30` (at C's 300-tick spool `MixTicks` must stay under 90: a 7-stage or deeper diluter is refused; up to 6 stages is legal, Tolerance ≤ 0.065), and `PrimeKg + C(MassCap, ℓ = 3) ≤ LineCapKg` (a Warning: "cannot reach the moon at full mass").

**Failure modes come out of the same graph, not a risk roll.** A thin-walled line (SF 1.1) breaches at Integrity 0.70 instead of 0.5. A missing interlock lets pure feed reach the chamber. A missing purge valve removes the counterplay. A two-stage diluter (Tolerance 0.035, `q_lim` 0.414) runs away on smaller feed errors. All of these are visible in the stats panel before anything is built.

**Thruster family** (`fn.thruster.chem`), to show the pattern on a small component: chamber pressure `P_c` from the pump's rating, throat area `A_t` from the nozzle's throat diameter, `F = C_F(ε) · P_c · A_t` with `C_F` tabulated by expansion ratio ε in data, `v_e` from propellant data × `η(ε)`, gimbal from a gimbal part's range. The chamber's pressure rating vs `P_c` gives SF exactly as above.

Families planned: warp (both kinds), chemical / AM-thermal / plasma / ion thrusters, tank, trap, reactor (fission, fusion, annihilation core), battery / capacitor, life support, plating, **inertial dampener**, **wing and control surface**, **escape pod and pod bay**, **station anchor**, **command core** (r6, D27), **console** (cockpit, engineering panel, local panel), door / airlock, seat / cockpit, landing foot, refinery cores, fabricator; round 6: **suit locker** (D64), **particle / energy / gravity shield** (D55), and combat-and-sensors.md's weapon, magazine and feed, sensor, fire-control computer, radiator, heat sink, jammer and decoy families. **Families without a meaningful part graph** (seat, door, sensor, suit locker) accept a single "core" part whose stats are data, which is exactly ship-hulls' bought function core, so nothing is lost.

Every family also declares its **wiring ports** (round 5, D17; round 6, D71): power in (W), **data-in** ports carrying its named **signal inputs** (commands), each with a dead-line behaviour, and one **data-out** port carrying its **signal outputs** (status, alarms, tracks). Data is one-way, so inputs and outputs are separate ports (section "Wiring"). A component works only through the inputs its ship's wiring actually reaches, and a console sees only the outputs wired back to it.

### Inertial dampener family (round 5, D9)

Plating and dampening are now **two components**. Plating (`fn.plating`, per room) gives floor gravity and nothing else. The **inertial dampener** (`fn.dampener`) projects a bubble that keeps everything inside it (crew, cargo, loose items, docked craft in a covered bay) moving with the ship. Its runtime rules (consumption, rating, leak-through, starvation ramp) are exotic-matter.md's "Gravity plating and the inertial dampener"; this family only derives its numbers from parts.
- **Parts and roles:** field coil segments `Coil` 2..32 (grade D/C/B/A, each rated in kg of covered mass), an EM feed cell (field grade, a small tank) `Feed` 1..1, a power conditioner `Conditioner` 1..1, a controller `Controller` 1..1.
- **Stats (exotic-matter's names):** `a_max` from the lowest coil grade (data: D 2 g, C 3 g, B 6 g, A 10 g), `MassMax = Σ coil rating`, bubble radius `R_b` from the coil ring's span, and exotic-matter's constants `EmPerNsDamp` (1e-11 kg/(N·s)), `DampWPerN` (1 W per N) and 10 kW standby. The catalog S/M/L plans must evaluate to exotic-matter's `fc.inertial_dampener` S/M/L rows exactly (3/6/10 g, 50/500/5,000 t; a P1 test, as for the C drive).
- **Runtime rule (exotic-matter's):** `a_d = min(|a_man|, a_max) · min(1, MassMax / m_in)`, further limited by the power and EM actually delivered and by the `Limit` input; interior acceleration is frames-and-zones' `g_interior = g_floor + a_man · max(0, 1 − a_d / |a_man|)`, where `a_man` is the manoeuvre acceleration (rest acceleration `a_rest` is never billed or cancelled) and `g_floor` is plating's top-up: above the rating the **excess leaks through** and throws people about. `m_in` is the mass covered (characters, loose bodies, cargo, docked craft in covered bays; not hull, not mounted components), updated on events. Power `DampWPerN · m_in · a_d` and EM settle in the 10 Hz ledger; nothing new per tick beyond the existing manoeuvre-acceleration term.
- **Coverage** (ship-hulls "Inertial dampener coverage"): rooms whose every air cell lies within `R_b`, plus designer-marked **bay volumes** inside `R_b` (open hangars, which are not rooms because they touch space).
- **Overlap:** a room covered by two dampeners takes the stronger one; ratings do not add.
- **Off, unpowered, unfed or unwired:** `a_d` ramps to 0 over 0.5 s and covered rooms fall to the real-effects branch (plating may still give floor gravity). Velocity snaps (warp, flush, pulses) stay excluded as before.
- **Wiring:** Data inputs `Enable` (FailOff: a dead line switches the dampener off) and `Limit` (FailHold: the crew may lower `a_d` to save power). Graviton projection: Internal (never struck in tunnels).

### Aero: wings, control surfaces and the flight computer (round 5, D10)

Both models run: **6-DOF thrust with flight assist everywhere**, plus **aerodynamic lift and drag in atmosphere**.

**Revised (D85, 2026-11-27; built in the designer):** lift comes from shape. The wing chunks, wing components, airfoil tables and `fn.aero.control` components below are superseded:
- **Every exposed face** of the exact hull surface feels the air by its angle φ to the flow: a face turned into it takes a pressure `0.8 sin φ`; a face turned away takes a suction `3.2 sin φ` up to 15°, falling over 10° to a wake's `0.35`; every face drags by skin friction `0.005`. A face in the wake of the part upstream takes no pressure; faces that cannot be seen from outside (rooms) do not count. A flat plate so lifts about 4 per radian, stalls near 15° at a lift coefficient near 1.1 and drags 1.15 broadside; a hull gives body lift. No leading-edge suction yet: a plate's lift-to-drag ratio is about 1 / tan α.
- **Baked, not evaluated:** each moving part (the ship's own, and each joint's) is sampled (about 4096 patches) and its force and moment over dynamic pressure tabulated for 2664 flow directions (5°), in the part's own frame (`src/Ships/Aero`, `AeroTable`, `HullAero`). Per tick a part costs one bilinear table read, turned by its pose. The bake is about 0.4 s per part on one worker, on change only. Parts are baked alone (one does not shadow another).
- **Control surfaces** are hinge or rotor parts: turning one turns its table. Their motor settings (speed, torque, limits) are the actuator.
- **Docking (D85):** docked vehicles fly as one; docking or undocking bakes the combined shape again. A vehicle stored inside another does not count.
- **Designer:** the Aero panel shows lift, drag, side force and moments about the centre of mass at a chosen speed, air density, angle of attack and sideslip; curves over ±30°; stall angle and the stall speed for 1 g; the neutral point (stable behind the centre of mass); each hinge's pitch, yaw and roll authority from limit to limit. Lift, drag and weight are drawn from the centre of mass.
- Still to do: forces on the ship's body in flight, rotation damping (`ω × r` per part), and the flight computer's control allocation over the parts.

The original round 5 design follows.
- **Lift sources, from geometry or from part stats:**
  - **Wing chunks** (ship-hulls `ChunkKind.Wing`): any size, modelled with primitives or the mesh editor. The bake measures ≤ 16 spanwise strips (area, chord, quarter-chord point, frame); the chunk's `AeroProfile` picks the airfoil table. Holes re-measure the touched strips.
  - **Wing components** (`fn.aero.wing`, housing ≤ 8 m): catalog or player designs whose stats come from parts: a spar (root bending rating), a skin panel set (area, span), an airfoil preset. Treated as 1–2 strips. The skin set's area and span are validated against the housing's own projected planform (they cannot exceed it), so an 8 m housing cannot declare a 200 m² wing.
- **Control surfaces** (`fn.aero.control`): hinge + actuator (rate °/s, max deflection, torque rating) + surface panel (chord fraction), mounted on a wing chunk's or wing component's trailing edge; they cover the strips their span overlaps and add `ΔCl(δ)`, `ΔCd(δ)` from the airfoil table. They need power and a Data cable from the flight computer's output; an unwired surface is locked neutral (Warning).
- **Never in warp:** aero is skipped in `Warp` placement and while warp.md's brake holds in the real-space segment (the field cancels air drag there; the energy goes into warp.md's blast). After a hard cancel or a lost brake the ship is an ordinary body at up to 2 km/s, and the full aero and wing-root stress rules apply.
- **Per tick, only for ships in atmosphere (ρ > 1e-4 kg/m³):** per strip, `v_rel = v + ω × r − wind` (planets.md weather), angle of attack α from the strip frame, `Cl(α) + ΔCl(δ)`, `Cd = Cd(α) + ΔCd + Cl² / (π e AR)`, lift and drag `½ ρ |v_rel|² S` along the usual axes at the quarter-chord, pitching moment `Cm`; stall and a compressibility factor above Mach 0.7 come from the tables. Body drag stays review.md's occupancy-area drag, minus the wing chunks' cells (no double count).
- **Structure:** a wing's root joints are stress-graph edges (section 5); a lift overload beyond their rating splits the wing off by the ship-hulls rule.
- **Flight computer:** one control-allocation step over all actuators (thrusters and surfaces, ≤ 64 actuator groups: thrusters are grouped by direction and bus, surfaces by hinge line), using a 6 × N effectiveness matrix rebuilt only on design, damage or wiring events, scaled by dynamic pressure each tick. Modes: **Space** (thrusters only, the existing 6-DOF assist), **Atmo** (surfaces first, thrusters fill what they cannot do, coordinated turns, stall protection), **Decoupled**. A winged ship without enough thrust on some axis relies on aero there.
- **Cost:** ~40 ns per strip and ~0.3 µs for the allocation mat-vec per ship per tick; a 32-strip aircraft ≈ 1.6 µs; the cap (16 wing chunks × 16 strips + components) ≈ 11 µs; 0 outside atmosphere. frames-and-zones.md's "lift per wing part" is this per-strip force (a wing component is 1–2 strips).

### Ejectable core assembly and override (round 5, D12/D13)

- **The core** = reaction chamber + chamber buffer trap + diluter + the line between them and its valves (exotic-matter's "entire core"; emitter segments stay with the ship). It sits in an **eject cradle** (rail and ejection charges, rated mass; exotic-matter's eject mount, its own housing at `Rating` 4, so losing the core's valves never loses the eject). Every route crossing the cradle boundary (EM, agent, AM, coolant, power, data) must go through a **quick-disconnect** fitting. The cradle's design carries the eject path cutter to a blow-out panel (ship-hulls).
- **Triggers:** an `Eject` Data input (FailHold: a dead line neither ejects nor blocks the handle) fed from whichever consoles the designer wires to it; a **mechanical pull handle** on the cradle that needs no power or wiring (always present, so an engineer in the drive room can always eject); and an optional **auto-eject controller** part fed by a Data cable from the drive's `Alarm` output (its presence sets exotic-matter's `AutoEject` flag: fires at runaway onset when purge is unavailable). Timing and Δv stay exotic-matter's (30 ticks; +20 m/s for D and C, +40 m/s for B and A). Mid-warp, an eject command is accepted: its 30-tick sequence may run in cruise with the cradle latched (warp.md, D66: the bubble seals nothing, latches hold) and the core separates, unbraked, at `max(T_exit + 1, command + 30)`; a burst due before then resolves at `T_exit + 181` (warp.md "Runaway and core ejection mid-warp", exotic-matter).
- **Clearing the ship:** the ejected core is a new body launched along the eject path; it ignores collision with its former ship until its AABB clears the hull's, so it cannot snag in its own channel. Its reach is the eject Δv × the time left to the burst (about 50 m at C in real space; 60 m for D and C, 120 m for B and A after a mid-warp eject), so an eject is not a torpedo; the override and casing caps in exotic-matter still bound a deliberate burst.
- **After an eject:** the blow-out panel is gone (a Breach edge if the bay was pressurised, so designers give the core its own unpressurised bay; validation Warning otherwise), the cradle is empty and the ship cannot warp until a new core is installed (a `Mount` step) and the panel replaced.
- **Chamber rating:** `SF_chamber ≥ 3` certifies the chamber housing at exotic-matter's `chamberRating` (8, dividing its integrity loss), so chamber ruptures need very heavy damage; breaches mostly come from the line and diluter, and eject is the main counter.
- **Override (D12):** the interlock part gains an `Override` Data input (FailOff: a dead line can never arm it). Arming needs an **override key switch** part in series on the Data cable from a console to that input (the key closes the line), plus owner authorisation, and keeps every exotic-matter safeguard (owner only, feed above `q_lim`, never docked, locked or in `Warp` / `Throat` placement, the 10 s public arm, casing and line caps). A design without the key switch cannot be overridden at all; "override-capable" is part of the sealed stats a docking scan shows, so stations can refuse such ships.

### Linked warp drives (round 5, D14)

The runtime rules are warp.md's "Multiple drives" (it owns the bubble) and exotic-matter's multi-drive section (fuel, runaway); this doc supplies only what the design and wiring decide.
- **Who supports (warp.md's join rule):** a drive joins if it is on the root grid, or on a docked ship whose collar carries power and data wiring (D71: a Data cable through the collar, as warp.md's round 6 text says); it is **reachable from the plotting console's `Engage`** (a directed Data path from that console's output to the drive's input); `Integrity ≥ 0.5`; it is out of its own cooldown; it is Hot or primable within the spool; its fuel plan passes; and `f_i = clamp(P_i / PRated_i, 0, 1) ≥ 0.5`, where `P_i` is the power its wiring delivers. Their bubbles support each other: one combined bubble. A drive the plotting console's `Engage` cannot reach simply doesn't join; it never blocks the others. **At most 8 join (largest w).**
- **Group stats** (warp.md's `WarpDriveSet`, fixed at spool start; the designer previews them on design, damage and wiring events): weights `w_i = min(P_i, PRated_i)` (`PRated` per class in `data/drives`, D:C:B:A = 1:4:12:40); averages are `Σ w·X / Σ w`, e.g. `VCap = Σ w_i f_i VCap_i / Σ w_i`; support `MassCap = Σ f_i · MassCap_i`; spool = the longest member's; fuel splits by `w_i / Σ w` from each drive's own line.
- **Cooldown (warp.md):** every participant starts its own `CooldownTicks`, and the root grid cannot spool again until `ShipWarpCooldownEnd` (the latest participant's cooldown end, stored per root grid), so chaining drive A then drive B is refused.
- **A member drops out mid-warp** (damage, eject, its bus or breaker failing, a cut power feed): support is recomputed event-driven; if it falls below the carried mass, warp.md's "support lost" hard cancel applies, otherwise the warp continues unchanged.
- Replaces exotic-matter's one-active-drive selector (its `DriveGroup` struct is folded into warp.md's `WarpDriveSet`).

### Escape pods, station thrusters and anchors (round 5, D25)

- **Escape pod** (`fn.escape_pod`): seats, life-support hours, heat shield rating (atmospheric entry), retro and parachute Δv, an always-on distress beacon. It sits in a **pod bay** component whose cutter opens a launch tube ending in an outer hatch (a door portal, so the bay stays sealed; ship-hulls clear-path rule); launch is the detach rule plus a bay impulse, and the pod leaves as a small grid through the normal frame transfer. Launch input: Data from consoles (FailHold), and a local handle in the pod that needs no wiring.
- **Station plan class** (`PlanClass.Station`, the exotic-matter grid class: orbital, anchored or surface base) validation, matching exotic-matter's build-tool rules: escape pod seats < crew berths is a Warning; no thrust on some axis, or thrust-to-weight at the planned anchor pose under `stationMinAccelG` (data, 0.01 g), is a Warning. Warnings, not Errors, because a surface base is a Station too; the hard gate is exotic-matter's: anchoring over an atmosphere with an `Impact` footprint is refused without pod seats for the crew berths.
- **Station anchor** components may be placed on any plan class; activation, crawl, failure and drops are exotic-matter.md's (never over a minor body: tractors hold asteroids). Wired ports:
  - `Engage` (Data, FailHold): refused at runtime unless at the required position and mostly the required velocity.
  - `Release` (Data, FailHold, `Logged`): still needs owner authorisation and exotic-matter's 60 s public countdown, so no wire state, cut or short can release an anchor.
  - `CrawlTarget` + `CrawlSpeed` (Data, FailHold; the slow move over days, capped at exotic-matter's `CrawlMaxMs`): a dead line stops the crawl at the end of its current segment.
  - `Failing` **output** (Data): the designer runs a cable from it, split to the flight computer and the pod alarm. The thruster unlock itself is exotic-matter's rule and needs no wire.
  - **Containment power, redundant by rule:** losing an anchor's containment-class power beyond its 10 s buffer drops the grid (exotic-matter), so wiring validation requires **two independent feeds** to every anchor core (no shared segment, breaker, junction box or source; Error), and the crew cannot open a breaker feeding a holding anchor (exotic-matter's no-starvation rule). Cutting one feed raises a warning; it takes both, beyond the buffer, to drop a station, which keeps a dropped station the rare disaster D25 wants even under D17 wire cutting and D27 sieges. Cutting station power (bus breakers and feeds) disables shields, defence, turrets and clamps, never these containment feeds.
- **Command core** (`fn.command_core`, D27, r6): every station's **ownership token**, the hook for capture (the siege and capture rule is factions-and-law.md's "Sieges and capture"). Parts: a core unit (single-core family, stats are data), its own armoured housing (`Rating` 4, like the eject cradle, so it outlasts the room around it) and a **local panel**. Validation: `PlanClass.Station` needs exactly one (Error; a Ship plan may carry none or one). Wired ports: power in (its own buffer, so cutting station power never transfers ownership), a Data input for owner policy from the station's consoles and a Data output for status; it has no input that transfers ownership. Capture is an `Override` interaction at the local panel, held until factions-and-law.md's capture clock completes (`CaptureS` by station class, 5 / 10 / 20 min for S / M / L, stretched by the staff and garrison still standing; paused while a defending character contests inside its room's `GridVolume`). The hold reports to that doc's `Sieges`; on completion, ownership, permissions, anchor `Release` / crawl authority, gate policies and catcher keys pass to the captor's group and a `StationCapture` incident is written (the shared `IncidentRecord`, factions-and-law.md). Designers place it deep inside, behind doors and armour: boarding (cutting torch, breach, forced dock) is the only way to it.

### Wiring (round 5, D17; round 6, D71)

Components have wiring, so **what can control or cancel what is the ship's wiring setup**, and cutting wires is gameplay.
- **Two media (D71):** **Power** (cable by gauge: ampacity; voltage classes LV 48 V, HV 1 kV, MV 10 kV) and **Data**. Round 5's third medium, Control (hard-wired pairs and series loops), is removed: every command, status, alarm, e-stop, arm and eject line is a data cable.
- **A data cable is one-way and can be split.** It runs from one **sender** (the data-out port of a console or component) to its **receivers** (data-in ports), and it may be split as often as wanted, so one cable can reach every input that should hear that sender. One cable carries **all** of its sender's output signals, and each receiver takes from it the signals its own inputs name: there is no cable per signal. Nothing travels back along it. A console that should also show a component's status needs a second cable the other way, from the component's data-out to the console's data-in.
  - So command and telemetry are separate cuts: cut the cable to the drive and the bridge can watch but not command; cut the return and it commands blind.
  - There is no shared bus to lose or take over at once: a line carries what its one sender says, to exactly the ports it is wired to.
- **One sender per line** (two outputs on one cable is an Error). An input that several senders may drive (a drive's `Cancel` from the bridge and from engineering) has several data-in ports (count per family in data, 2–4), or sits behind a **combiner**: a powered part with N inputs and one output that forwards whatever it receives. Splitters are passive.
- **Live and dead lines.** A powered sender keeps a carrier on its cable. An input's line is **live** when intact segments, splitters and closed switches join it to a powered sender (through a combiner: the combiner is powered and at least one of its inputs is live). Otherwise it is **dead**.
- **Signal inputs** declared by each family have a **dead-line behaviour**: `FailSafe` (a dead line asserts the input; e.g. the warp drive's `Hold`: dead = cancel), `FailHold` (no change), `FailOff` (dead = off). `Logged` lines (beacon, transponder) record who cut them. This replaces what the Control medium was for: fail-safe is a property of an input, not of a kind of wire.
- **Parts** (catalog presets of open templates): power cable stock by gauge, data cable stock, connectors (crimp / solder joints), junction box (power), **data splitter** (1 in, N out, passive), **data combiner** (N in, 1 out, powered), breaker (rating, trip), relay (a Data input switches a power contact), switch, e-stop button and key switch (placed **in series** on a power or data cable: open = dead downstream), console (cockpit, engineering panel, a component's local panel), transmitter (beacons, transponder).
- **Wire runs are routes** (`Medium` Power or Data; a data route is directed, `From` an output or a splitter / combiner output, `To` an input) laid by the router like pipes, along hull inner surfaces in trays or **concealed** through hull brushes. The compiler splits each run into **segments** of ≤ 4 m between nodes (ports, junctions, penetrations); each segment is an element with an id and a capsule for damage tests.
- **What controls what is derived, never declared:** console C can command input X of component K when C is powered, the seat is authorised, and a **directed** path of intact segments, splitters, powered combiners and closed switches leads from C's data-out to a data-in of K. C shows K's status when such a path leads from K's data-out to C's data-in. The designer's **schematic view** and its "what controls this?", "what does this see?" and "what does this cut?" queries show it.
- **Hold lines (replace round 5's Hold loop).** A FailSafe input is fed by exactly one line, never through a combiner (Error), so whether it is live is a plain series chain: the sender's power, every segment and every switch on the way. The warp drive's `Hold` is the standard case: a cable from a powered sender (by default the engineering console; any console or the drive's own local panel will do), run **through** each e-stop button and kill switch the designer wants, ending at the drive's `Hold` input. Press any e-stop on it, open any kill switch, cut the cable anywhere, or take the sender's power: the line is dead, `Hold` asserts and the warp cancels. A one-way cable needs no return leg, so there is no loop to close.
  - A hold line may be split: an e-stop upstream of the split stops everything on it, one on a branch stops only that branch (two drives on one bridge e-stop, each with its own drive-room e-stop).
- **Interlocks** are the same thing between components: an input of A fed from an output of B. "The reactor scrams unless the coolant pump reports running" is the pump's data-out cabled to the reactor's `Permit` input (FailSafe: a dead line or a "no" both scram). A mutual interlock (an airlock's doors and its controller, ship-atmosphere.md) is a cable each way. An interlock that only **keeps something locked or off** (FailOff, or FailSafe that only locks) is read from the cached bindings and needs no slot in the same-tick mask below.
- **Warp cancel (D17 example; warp.md's drive ports Power in, `Engage`, `Abort`, Nav data in, `Beacon` out):** warp.md's `Abort` (a soft cancel) is raised three ways:
  1. the drive's `Hold` input going dead (the hold line above: any e-stop or kill switch on it, a cut or damage anywhere along it, or its sender losing power);
  2. a `Cancel` command on a Data input, from any authorised console with a live directed path to the drive;
  3. the drive's local panel.

  Opening the drive's breaker or losing its feed is warp.md's power cancel (hard, no brake). Mid-warp, an engineer can always shut the drive down at the drive. The designer chooses how many other places can. What D71 changes: a console cancel needs that console's command cable intact (cut it and only the hold line and the local panel are left), and the bridge sees the spool state only while the drive's return cable reaches it.
- **Arrival beacon (D18 example):** the drive's `Beacon` output feeds the beacon antenna by one Data cable, drive to antenna; nothing has to come back. The cable may be split (a second antenna, a bridge indicator). Cutting it, unpowering or destroying the antenna silences the beacon and is a `Logged` tamper: the server records who opened the segment. A military drive's `BeaconSwitch` is a Data input (FailHold) fed from a console through a key switch; a civil drive has no such input, so no console can silence it. The transponder (combat-and-sensors.md) is wired the same way.
- **Power networks:** a bus is a connected set of power segments from generators and batteries through breakers, so exotic-matter's per-ship `PowerNet` becomes one per bus. Cable under-rated for its breaker or load is an Error. ("Bus" now only ever means a power bus.)
- **Faults from damage** (deterministic, event-driven, applied in tick step 3 with other damage): a damage brush overlapping a segment's capsule by at least the cable diameter **cuts** it (open); a lesser overlap **damages** it; dents and hinge bands damage and cut segments by combat-and-sensors.md's thresholds. A damaged Power segment with no breaker between it and its source **shorts**: an arc damage brush at the segment on the next tick (one hop per tick, like traps) and a brown-out on that bus until something isolates it; with a breaker, the breaker trips and everything downstream loses power. A damaged Data segment passes commands and status at 1 Hz, and a FailSafe input treats a damaged line as dead (it fails safe on doubt), so a nicked hold line cancels like a cut one. Poor crimps (q < 0.4) stay resistance faults.
- **Cutting and repair in game:** a wire cutter opens an exposed segment in 1 s; a concealed one needs the hull cut first (ship-hulls cutting torch). Repair is a splice step (crimp, 6 s, 0.5 m of cable) or a segment replacement step.
- **Designer help:** "Standard harness" does it in one click from data templates: every component to the nearest power bus through a breaker; a command cable from each bridge and engineering console, split to the inputs of every component (a combiner in front of any component with more senders than data-in ports); a status cable from every component back to those consoles through combiners; and the standard hold line (engineering console, then the drive-room, engineering and bridge e-stops in series, to each drive's `Hold`). Per-input Warnings for unreachable required inputs ("warp drive: no cancel path from the bridge") and an Info for status that reaches no console.
- **Cost of the second direction:** a standard harness is about 450 routes instead of round 5's 300 (the status returns), and about 1.4 × the data cable length; combiners replace the round 5 data hubs one for one. Data cable is the cheapest stock on a ship, so the Wren's stock line moves by a few credits; the P1 test re-runs the worked numbers with it.
- **Runtime cost:** one CSR graph per ship, directed for Data. **Immediate** effects need no search: each segment carries a mask of the ≤ 64 **FailSafe lines** and critical feeds (drive power, anchor feeds) it lies on. A FailSafe line has one sender and no combiner, so the path from the sender to the input is unique: a cut, damage, an open switch on a member, or the sender losing power kills that line in O(1) on the same tick (a cut warp hold line cancels at once). On a critical-feed member it runs one bounded search from that consumer back to its sources the same tick (a redundant path keeps it fed). The full pass that refreshes the cached bindings (console → component input, component output → console) first collapses intact data segments into **nets** (one sender each, one sweep over ≤ 16k segments), then searches the small net graph (≤ 1024 nets, joined only at combiners): ~20–100 µs. It is **coalesced** to at most one per ship per 10 Hz ledger step, and capped at 0.5 ms per tick across all ships (round robin), so a battle full of cut cables or a short spreading one hop per tick cannot cost 100 µs per ship per tick. **0 per tick** for an undamaged ship. One-way data is cheaper than the round 5 bus here: a net has one sender, so there is no arbitration and no per-console reachability set to keep.

### Shields, weapons and sensors as components (round 6, D55)

Ship shields are **components**, like everything else that does something. What they do, their class tables (S / M / L), their counters and every number are combat-and-sensors.md section 3; this doc gives them a family, ports and validation. The catalog plans must evaluate to that doc's class rows exactly (a P1 test, as for the C drive and the dampeners).

| Family | Component | Mounting | Feeds and wiring | Design-time checks |
| --- | --- | --- | --- | --- |
| `fn.shield.particle` | **Particle shield emitter**: a facing panel of confined plasma. Parts: emitter grid segments (area each), a plasma source, a capacitor bank (the pool), a controller | Exterior. Covers the hull facing within 60° of its mount normal, up to the class area | Power, class 2. Data in: `Raise` (FailHold), `FacingPriority`. Data out: pool state | Facing coverage: the designer paints the 14 `FacingTable` directions by which emitters cover them and lists bare ones (Info; never an Error, a bare flank is a design choice) |
| `fn.shield.energy` | **Energy shield projector**: one bubble round the grid; absorbs radiation, dumps it as heat. Parts: projector coils, a heat pool, a radiating surface, a controller. Its S class is the old `RadiationShield` part | Anywhere on the grid; the bubble is the grid bounds plus 5 m up to the class radius | Power, class 2. Data in: `Raise` (FailHold). Data out: heat pool. Its dump goes to the thermal ledger | Class radius against the plan's bounds (Warning if the hull pokes out) |
| `fn.shield.gravity` | **Gravity shield emitter**: a gimballed anti-graviton lobe. Parts: a graviton emitter (the tractor's), a gimbal, a controller | Exterior, gimballed. Mount rated 1.5 × the class force (tractor-beams.md's mount table) | Power, plus **EM and AM routes** (tractor Reaction drive; no AM feed, no shield, D28). Data in: `Raise` (FailHold) and a **track feed** from the fire-control computer's data-out. `gravitonProjection: External` | Mount rating (Error); no AM route (Error: it cannot work); no track feed (Warning: static mode only, a quarter of the rated force); lobe cones against the hull (Info) |

- **Wiring behaviour (combat-and-sensors.md "Interactions"):** a shield needs Power, and Data for raise, drop, facing priority and the lobe's track feed. A cut Data line leaves a shield in its last state (FailHold) with no tracking; a cut power feed drops it. The standard harness splits a `Raise` command line from the bridge and engineering consoles to every shield, and the fire-control computer's data-out to every gravity emitter and turret.
- **They are not the atmosphere parts.** Air barriers and emergency breach shields (ship-atmosphere.md) keep air in and stop no weapons; particle panels hold no air. Station shields (`StationShield`, rings-and-asteroids.md) keep their own definition.
- **Weapons, magazines and feeds, sensors, the fire-control computer, radiators, heat sinks, jammers and decoy launchers** are families defined by combat-and-sensors.md (sections 1 and 4) in the same way: parts, ports, class rows the catalog plans must match. Validation it asks for: turret and sensor arc masks baked from the hull, recoil against mount ratings (Error), and a Warning for a transponder with no `Logged` line. The plan's stats panel shows that doc's `CombatRating`.
- **Law hooks (round 6, D57, D74; factions-and-law.md owns the rules).**
  - `OwnerId` is that doc's shared id type. Every grid carries a `HullTitle` (registered owner, holder, `HullLaw` flags). NPC buyback, refit and plan `Modify` licences refuse a `Stolen` hull; a docking scan reads the `HullLaw` flags, open cases, override capability and regulated equipment.
  - Template `access {tier, faction}` is satisfied as that doc's section 1 says (`Public`, a standing band, a `ProductionLicence`, or a `Commission`); `MilitaryOnly` templates need a `Commission`.
  - **Regulated equipment (D74):** the `CaveDrill` function core family (rings-and-asteroids.md: ship-mounted, or on a small grid as a tunnelling vehicle, with a charge setter that takes `SplitterCharge` items) carries `regulated: true`. Buying it is open; using it on held land or in a held band needs a `Tunnel` right.
  - Catalogue and yard rows carry `faction` (D68).
- **Atmosphere parts (ship-atmosphere.md owns the families and their runtime).** `fn.airlock` (the controller), bulkhead door, vent valve, pump, vessel, compressor, `fn.air_barrier` and `fn.breach_shield`, with the route medium `Air` for ducts. Their ports follow "Wiring" (D71): Power, Data in, Data out and `Air`. A bulkhead's `Seal` is a FailSafe hold line, an airlock door's `Interlock` is a cable each way to its controller, a barrier's `BarrierDown` and a room sensor's `PressureDrop` are data-out signals (the second can be split to a warp drive's `Cancel`), and a remote door `Override` sounds that doc's `VentWarnS` klaxon (15 s) first.
### Suit lockers (round 6, D64)

EVA suits are restrictive, so crews do not wear them by default: they work in normal clothes and change at a **suit locker** before an EVA or a fight. Lockers therefore belong beside airlocks, in hangars and in crew rooms, and the designer checks that they are there. ship-atmosphere.md "Suit lockers (D64)" owns the runtime (suit items, change times, refill, access, the penalties of being suited); this doc owns the component as a plan element.

- **Family `fn.suit_locker`** (single-core family: its stats are data, `data/components/air/`). A wall-mounted interior component with 1, 2 or 6 slots (S / M / L). Each slot holds one suit item, or the clothes of whoever is wearing that suit. Suits are separate items, so a locker can be stocked, emptied or found empty.
- **Ports:** LV power in (charging) and an `Air` socket (ship-atmosphere.md's duct medium: the slot is the suit port and refills a hung suit from the ship's vessels). No data input: a locker cannot be shut out by cutting a cable; it answers the ship's access list like a door panel. A locker with no `Air` route still stores and dresses.
- **Hood lockers** (emergency hoods on walls, under seats and berths) are a second, smaller single-core component of the same kind with no ports.
- **Validation** (ship-atmosphere.md's Atmosphere class; never Errors, because a drone hull or a cargo pod needs none):

| Check | Severity |
| --- | --- |
| An airlock with no suit locker in the room on its ship side or within 10 m of its inner door | Warning |
| A hangar or a berth room with no suit locker in it or within 20 m; fewer suit-locker slots than berths | Info |
| A seat or berth with no hood locker within 10 m | Warning |
| A suit locker with no `Air` route ("stores suits, cannot refill them") | Info |

- **AI crew** (ai-ships-and-crew.md): a locker is a service post. A crew member sent on EVA, to a breach or to repel boarders walks to the nearest locker with a free suit first; the change time (30 s for an EVA suit) is part of its response time, so locker placement decides how fast a crew reacts.
- **Construction:** a `Mount` step, plus its power and `Air` `Route` steps. Catalog prices are ship-atmosphere.md's placeholders (suit locker 40 / 70 / 180, hood locker 10, suits extra). The catalog Wren gains one S suit locker by its airlock and a hood locker at the cockpit; like the round 5 additions, the worked tables in section 3 are the earlier numbers and the P1 test re-runs them.

### Validation rules

| Class | Checks | Severity |
| --- | --- | --- |
| Schema | Format version, counts within limits, every `PlanRef` resolvable, parameters within template ranges, rotation snap index valid | Error |
| Geometry | Part OBBs do not interpenetrate (except declared nesting, e.g. pipe in flange); parts inside the housing bounds; component mount ≥ 70 % solid behind (ship-hulls); plates lie on hull surface within 0.05 m; ≤ 256 brushes per brick | Error |
| Connectivity | Every port connection has matching medium and size (DN, voltage); routes connect two compatible sockets; a data route runs from an output to an input (round 6, D71); no dangling required ports | Error, Warning for optional ports |
| Function | Family roles, paths, loops; formulas within envelope; family-specific rules (SF, spool vs mix ticks) | Error or Warning per rule |
| Buildability | Assembly graph acyclic; every hull piece reachable from the keel; every stock length ≤ the longest stock bar (else a splice joint is added, Warning); each component either fits through its room's portals or gets a pre-close order edge (section 4) | Error |
| Mesh (round 5) | ship-hulls "Mesh brushes" table: closed, manifold, oriented, no self-intersection, no degenerates, mesh limits; thin features | Error; Warning for thin features |
| Wiring (round 5; round 6, D71) | Cable ampacity ≥ breaker rating and load; splitter and combiner port counts; exactly one sender on every data line; every FailSafe input fed by exactly one line, with no combiner on it, that is live as designed (a powered sender, every switch on it closed in its default state); at most 64 same-tick FailSafe lines and critical feeds per ship; required signal inputs reachable from at least one console; every warp drive has a wired, powered beacon antenna; two independent feeds to every anchor core; every component powered by some bus; bus power budget; components whose status reaches no console | Error for ratings, port counts, the sender rule, FailSafe lines, the beacon and anchor feeds; Warning for reachability and budget; Info for unseen status |
| Systems (round 5) | Core eject path and pod launch tubes clear; quick-disconnects on every core-crossing route; chamber SF ≥ 3; station class pods and thrust (section 1); control surfaces wired | Error; Warning for station pods and thrust and for unwired surfaces |
| Combat (round 6, D55) | combat-and-sensors.md's checks: gravity shield mount rating and AM route, recoil against mount ratings; shield facing coverage, track feed, arc masks, transponder `Logged` line | Error for mounts and the AM route; Warning or Info for the rest ("Shields, weapons and sensors as components") |
| Atmosphere (round 6, D64) | ship-atmosphere.md's class, including the suit-locker and hood-locker rows ("Suit lockers") | Warning or Info |
| Economy | Cost quote for each sourcing mode; NRE and per-build stamp per redefined level | Info |
| Access | Every part template's `access` (tier, faction key): designing with it is always allowed; buying or producing it needs the access (section 2) | Warning in the designer, Error at build or production |

**Plan limits** (server-enforced at import and save, so a hostile file cannot cost more than one bounded validation):

| Item | Limit |
| --- | --- |
| Bundle | ≤ 1 MB packed; every section's `rawLen` ≤ 8 MB and ≤ 32 × `packedLen`, checked before inflating |
| Distinct sub-plans in a bundle | ≤ 256; nesting depth ≤ 6 (ship > component > part, hull chunk > hull chunk ≤ 3) |
| Ship | ship-hulls limits (≤ 8192 brushes, ≤ 1 km, mesh limits), ≤ 1024 component placements, ≤ 4096 routes (pipes and wires), ≤ 65,536 waypoints, ≤ 1024 order edges, ≤ 16,384 wire segments, ≤ 1024 data nets (one sender each), ≤ 64 consoles, ≤ 64 same-tick FailSafe lines and critical feeds |
| Mesh validation (server) | only for `MeshHash`es not already validated (the cache keeps the canonical bytes and compares them on a hit, as the plan store does, so a crafted xxHash64 collision cannot pass an invalid mesh); bounded by the mesh limits (≈ 0.5 s CPU worst case per ship) **and** a per-player budget of 128k new subdivided triangles per 10 s (≈ 65 ms), so a 1M-triangle ship of all-new meshes imports over about 80 s instead of costing 0.5 s at once |
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
  - **Bundled with the game (D69):** `BRUTALDesigner.exe` ships in the game's install, beside the game executable, built from the same commit and reading the installed `data/`. It is not a separate free download, so there is no second distribution, no catalog data package to keep in step, and no version skew between the app and the game. Designing without the game running is still the point: it starts on its own, without Sim, Net or a login.
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
| Mesh editor (round 5) | Ship, hull chunk, housing | ship-hulls "Mesh brushes": vertex / edge / face / loop / panel selection; extrude, inset, loop cut, bevel, bridge, merge, dissolve, knife, fill, crease; live mirror; 0–2 subdivision levels; convert primitive or group to mesh; panel painting. Cage drawn as polygons while editing, bake on commit, baked-vs-cage overlay with lost-detail outlines; validation on commit |
| Wing tool (round 5) | Ship, hull chunk | Mark a chunk as `Wing`, pick an airfoil; shows measured strips, span, area, aspect ratio, lift curve at a chosen speed and altitude, centre of lift vs CoM; places control surfaces along the trailing edge |
| Wiring (round 5; round 6, D71) | Component, ship | Routing with Power and Data media; data cables drawn with their direction, split with one click on a cable; "Standard harness" auto-wire; **schematic view** (auto-laid-out 2D diagram of power buses, data lines with arrows, splitters, combiners, hold lines and consoles); "what controls this?", "what does this see?" and "what does this cut?" queries highlighting paths and the components a segment feeds |
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
- **Sizes:** a component of 40 parts, 60 connections, 10 routes and 100 housing brushes is about 4 KB raw, about 1.5 KB packed. A 2,000-brush ship is about 25 KB (ship-hulls) plus routes and placements, about 30 KB. A bundle with ten custom components adds about 15 KB. Round 5: mesh brushes add about 8 KB packed per 1,000 control vertices (a fully mesh-modelled 100 m hull with 20k vertices ≈ 160 KB packed, inside the 1 MB bundle limit); a standard harness adds about 450 routes, ≈ 15 KB (round 6: one-way data needs the status returns; round 5's bus harness was about 300).
- **Network:** the server keeps one BODY per hash (deduplicated across all players, bytes compared on insert). Clients fetch by hash on a cache miss, as ship-hulls already does for blueprints. A sealed component's full BODY is served only to licensees; everyone else fetching that hash gets its sealed view (housing, cutters, sockets, stats), which is all rendering and physics need.

### Plans in game, sharing, and IP

**Decision: plan bytes are not secret; certification is the value.** Anyone can copy a plan file. What costs money is the right to build it without paying the engineering fee.

- **Plan items.** In game a plan is an inventory item `PlanLicence {PlanHash, Kind, BuildsLeft, Issuer}`: `Build-N`, `Build-Unlimited`, `Modify` (derivatives inherit the parent's certification for unchanged sub-plans), `Full` (transfer of certification). Catalog plans are sold at shipyards; player plans on the **plan market** at a price the issuer sets, with an optional per-unit royalty. Licences are server inventory rows, never client data, so a copied file never copies a licence.
  - **A build is debited when its site is created** (the keel step), not at completion, so one `Build-1` cannot start several sites. Stripping a site back to nothing before 10 % of its steps are done refunds the build; later, it does not.
  - **The `Modify` licence that comes with a bought ship is bound to that ship entity:** it allows refits of that ship (section 4), with `BuildsLeft = 0`. It is not a licence to build copies or derivatives elsewhere.
- **Certification** is server state: `(OwnerId, PlanHash) → CertFlags`, where the owner is a player or an organisation (members share it). It is created by paying the engineering fee (NRE, section 3) the first time a redefined plan is committed to production or construction. Designing, saving, quoting and sharing are free. A certification never removes the per-build stamp (section 3), so an organisation that certifies once and lets anyone join saves the NRE, not the per-unit premium; builds under an organisation's certification are logged per member, and the organisation can charge members per build.
- **Standalone files import as untrusted designs** (the app is bundled, D69, but its files are still only files). "Import" uploads the bundle; the server re-validates and re-evaluates everything (nothing from the client is trusted) and stores it uncertified. Building it costs exactly what designing it in game would have cost, so the standalone app is never an exploit, and sharing files on forums is harmless: it saves design effort, not money.
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
| `designStamp` | 0.06 per build, per redefined level | must stay above `planLicence × A / X` (X = the like-for-like build's unit before stamp; 0.051 here) so a like-for-like custom ship never undercuts the catalog plans. Never waived or scaled by quantity (D68) |
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
- **No fleet discount (round 6, D68).** `designStamp` stays at 0.06 and there is no opt-in that zeroes it for large runs. No price in this section depends on quantity: catalog items, premade ships, plan and component licences, job-shop and yard quotes are per unit at any N, for players, organisations and NPCs alike. A production order for 50 ships costs 50 × the unit. The only thing a run spreads is the one-off NRE and tooling, which never takes a redefined design below the catalog route (the table below, "Limit" column). So a fleet's cheapest hull is a catalog hull: **fleets generally fly their faction's ships** (factions-and-law.md "Ships and catalogue": each faction's yards stock, license and refit only its own catalogue; a chartered organisation gets catalogue access, not a lower price). A custom fleet is a statement that costs 4 % or more per hull, for ever.
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

Round 5: the catalog C plan now also carries the eject cradle, six quick-disconnects, the beacon, a local panel and its wiring (D13, D17, D18), and its chamber wall is re-sized for SF ≥ 3. The tables below are the pre-round-5 numbers; the P1 test re-runs them with those parts, and the cost ordering and invariants must still hold (exotic-matter's 240 eject-mount option is folded into the base price).

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
- Time: B for the Wren is about 70 min solo with T1 tools, 140 min with T0 hand tools, about 40 min for two players (critical path), about 12 min in a T2 bay with a jig and 4 drones. A is zero. **Confirmed (round 6, D70):** about 70 minutes solo for a 15 m ship with basic tools is the right order of magnitude. Joint times are set per joint and tool (D67), so this figure is the sum of the table in section 4 and does not depend on how the player handles the tool.

### Production orders

- A **production order** (job shop service at a faction station, or your own fabricator / assembler cores) takes `(PlanRef, quantity, mode)`, debits inputs and money at start, and completes as a scheduled event after `Σ t / machines`. Output: complete prefab items (sealed if the plan is). Cancelling returns the inputs of units not yet started and the money for them, less a 10 % fee; started units finish or are lost.
- A **yard build** is a production order for a whole ship: a shipyard builds your plan from components you supply or it buys, charging `J·w·t · (1 + μ_j)`. The ship appears in that yard's hangar.
- Player factories selling certified sealed prefabs on the market is how "prefabs are designable" reaches other players.

## 4. Construction gameplay

### Buying a ship

- Show hangars (`data/shipyards/*.json`: catalog ship list per faction, stock count, restock days) display catalog ships as real entities, `Frozen` in the station grid, as-built `Factory`. A yard sells its own faction's catalogue (round 6, D68; the `faction` field, factions-and-law.md), at the same unit price for the first hull and the fiftieth.
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
| `Repair` (round 6, D59) | a dent or hinge record | press out, straighten or brace (see "Modification"); hole and deep-dent repairs are `Skin` steps on a repair piece |

**Hull pieces.** A piece is one Add brush; Add brushes under 1 m³ merge into their largest same-group neighbour. Subtract brushes are design voids, never steps; Paint is a cost, not a step. Per piece, the compiler derives:
- **frame members** analytically from the brush shape: polyhedral edges for Box, Wedge, Prism and Convex; rings and longerons for Cylinder, Capsule, Cone and Sphere; ribs every `ribSpacing` (1 m) across faces wider than 2 m. Members whose midpoint lies inside a Subtract brush are dropped. Profile from the material's spec (`data/materials`: `frameProfile`, `skinSheet`).
- **skin area and seams** from a `BrushYield` table the bake already has the data for: per brush, the surface cells it won (× 0.0625 m²) and the boundary faces it shares with each other brush (seam length to that neighbour). One extra counter array in `HullBrickJob`; deterministic on the server.
- **curved skin** (Cylinder, Capsule, Cone, Sphere faces) needs formed plates: a roller step (formed on site with a plate roller) or bought pre-formed stock.
- **Mesh brushes (round 5):** an Add mesh brush is split into one piece per **panel** (ship-hulls `FacePanel`, ≤ 25 m² each), piece id = (brush id, panel). Frame members follow the panel's crease edges and boundary edges, plus ribs every `ribSpacing` from plane slices of the subdivided surface; members inside Subtract or Inset brushes are dropped as before. A panel whose subdivided normals spread more than 10° needs formed plates (curved skin rule). `BrushYield` counts surface cells and seams per (brush, panel) by the winning triangle's panel. **Partial bake:** until every panel of a mesh brush is skinned, only its band cells (within the 2-cell band, where the nearest triangle and so its panel are known) whose nearest triangle lies on a skinned panel bake as solid; deep cells beyond the band and cells nearest an unskinned panel bake as air. A half-skinned mesh hull is therefore a 0.5 m shell of its skinned panels, and turns solid once its last panel is skinned (the server owns the resulting mass, as for every partial ship). The as-built state span is per (brush, panel) for mesh brushes, and the partial-ship brick cache key hashes those panel states too.
- **Wiring (round 5; round 6, D71):** wire runs, power and data alike, are ordinary `Route` steps (place cable in trays or conduits, crimp or solder every joint); concealed runs need their `Penetrate` steps. Breakers, splitters, combiners, switches, e-stops and consoles are `Mount` steps.

**Dependencies (all edges are AND; one runtime predicate):**
1. `Frame(p)` is available only if the keel is placed and some neighbour of p is framed and connected to the keel (**support predicate**, checked over the piece adjacency list, so building can grow from any side). The compiler guarantees a spanning tree exists.
2. `Frame(p) → Skin(p)`, `Skin(p) → Armour(plates on p)`.
3. `Skin(every piece under a component's mount footprint) → Mount(c)`.
4. **Access rule:** if a component's smallest cross-section exceeds the largest portal (door cutter) of its room in the fully built plan, then `Mount(c) → Skin(last piece closing that room)`. The compiler picks the room's highest-numbered piece as the closing piece. This is the "you may have to follow a specific order" case that comes from geometry.
5. `Mount(both endpoint components) → Route(r)`; `Frame(crossed piece) → Penetrate(r, p) → Route(r)`.
6. `Route(every route of a line) → Test(line pressure) → Load(that line's tanks)`. Pressure test before fuel is forced.
7. `Skin(all pieces of a room) + Mount(its doors) → Test(room leak)`.
8. `Test(lines of a drive) + Mount(drive) → Test(drive proving)`; certification of a custom drive needs this test to pass once.
   - **Wiring and eject (round 5):** `Route(every wire run of a circuit) → Test(circuit)` (continuity and insulation, one action) `→` the power-on of every component that circuit feeds; `Test(circuit of the drive's hold line, beacon line and eject line) → Test(drive proving)`. A data circuit's test checks direction too: the sender's carrier must arrive at every receiver the plan names. `Mount(eject cradle) + Skin(blow-out panel piece) → Test(eject dry run)` (fires the quick-disconnects only).
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

**Time is set (round 6, D67).** A joint takes the time in the table: per metre or per fastener, by joint kind and tool tier, and nothing else. There is **no weld minigame**, no pace setting (round 5's Fast / Careful is removed) and no bonus or penalty for how the player moves the tool. A better tool is faster; it does not make a better joint.

**Quality comes from the worker's skill tier, and only from it.** `q ∈ [0, 1]` is stored as a byte per joint, set by the server at step completion from a four-row table: the tier of whoever made the joint, in that joint's trade (welding, fastening, pipefitting, electrical).

| Skill tier | `q` | A weld holds (`0.4 + 0.6 q`) | What the joints are like |
| --- | --- | --- | --- |
| **Unskilled** | 0.40 | 64 % of rated | Poor: welds hold under two thirds of their rating, seal beads and seams leak (q < 0.45), line joints weep (q < 0.5), bolts count at 70 %. Below spec: never a Certified build |
| **Trained** | 0.65 | 79 % | Sound: nothing leaks; at spec |
| **Skilled** | 0.80 | 88 % | Good |
| **Master** | 0.95 | 97 % | Factory grade |

- **Why a table:** D67 asks for a set time and for unskilled welders to make poor welds. With time fixed and no input from the player's hands, skill is the only thing left that can differ, so tool tier, working position and the round 5 per-joint jitter no longer touch quality. The rule is the same for every joint kind, so welds are not a special case.
- **Poor welds feed joint strength.** The weld rating `σ_weld · throat area · (0.4 + 0.6 q)` goes into the stress graph (section 5) that tractor-beams.md and combat-and-sensors.md's structural pass read. A hull welded by an Unskilled worker carries 64 % of the plan's nominal cut capacities: it yields (bends) and tears at about two thirds of the load, and its mounts detach sooner. That is the cost of unskilled work, and it is visible in the designer's as-built stress view before the ship ever flies.
- **Skill tiers** are per character and per trade. A character starts Unskilled in every trade and moves up by finished work in that trade (data thresholds, proposed for welding: 50 m of weld to Trained, 1,000 m to Skilled, 10,000 m to Master; the guided 30-step starter skiff is sized to reach Trained in welding and fastening). Hired and AI crew (D47) work at their own tier: ai-ships-and-crew.md's Green / Trained / Veteran / Ace map to these four in order. Machines have a rated tier: T2 bay drones work at Skilled, yard robots and jigs at Master.
- **Mixed crews:** each joint takes the tier of the worker who made it; a shared step (`MaxCrew`) takes the lower of the two. So the skilled welder does the frames and the pressure seams and the new hand does brackets, which is a reason to crew a build.
- **Hands-on without trusting the client:** the worker holds the tool on the seam ghost and the bead advances at the tool's set speed; leaving the seam pauses it. The client animates; the server only checks presence (within 3 m of the step target, tool equipped) at 2 Hz for claimed steps and completes the step on its end tick. Nothing the client sends can change the time or the quality.
- **What quality does:**
  - structural joints set the **joint ratings** the tractor-beam stress model reads (section 5);
  - sealing joints below 0.45 create **Leak** edges in the ship-hulls room graph, area `seam length × 0.02 mm × (0.45 − q) / 0.45` (round 6: ship-atmosphere.md's width, which replaces round 5's `(0.45 − q) × 2 mm`; with Unskilled seams now common, the old width would empty a cabin in under a minute. A 50 m³ cabin with 60 m of Unskilled seam leaks through 1.3 cm², a time constant of about 50 minutes: the leak test fails, and the fix is rework by a Trained hand);
  - line joints below 0.5 leak fluid: agent recovery becomes `R · Π(1 − leak_j)`;
  - a drive's line quality shifts its breach threshold: `BreachIntegrity += 0.3 · max(0, 0.7 − q_line_min) / 0.7`, total capped at 0.85 (so an undamaged drive never counts as breached; above 0.7 the drive panel warns and the build cannot be Certified);
  - component mount joints scale the detach threshold of the existing detach rule.
- **Inspection:** the builder knows what they made. On anyone else's work (a bought, salvaged or crew-built ship) a worker sees a coarse indicator (good / poor) per joint; an NDT scanner `Inspect` step reveals the tier each joint was made at and flags joints below spec (spec is Trained, q 0.65, unless the plan asks for more on a line). **Rework** = a strip of that joint (grind out, 50 % of its make time) and a redo by someone better.
- A ship whose joints are all inspected and at or above spec gets the **Certified build** flag: player buyers can trust it (the plan market shows it), NPC buyback rises from 0.6 to 0.7 of the premade price (never more, so building to sell to NPCs is not a money tap), and stations may require it for docking capital-class ships.

### Co-op and automation (keeping it not tedious)

- **Claims:** a worker (player or drone) claims an available step; the server holds `StepClaim {step, worker, start, end}`. A claim lapses if the worker leaves for 10 s. Items are debited at claim, refunded on lapse; a claim ends exactly once (completed or lapsed, one server state transition), so a lapse racing the end tick cannot both refund and complete. Only workers on the site's list may claim, and one player holds at most 2 claims at a time, so nobody can lock a site by claiming everything. Several workers take different available steps in parallel; a step's `MaxCrew` (data, e.g. 2 for large skins) lets two players share one step at 1.8× speed.
- **Batch actions:** "cut for selection", "fetch items for selection" (from the site's container), "weld all seams of this piece" are one action each.
- **Tiers of automation:**
  - **T1 construction rig** (deployable): a hoist, a cut bench, and 2 drones that carry and place (no joints).
  - **T2 construction bay** (station or ship module): 4–8 drones that also make welds, bolts and routes at the Skilled tier (q 0.80), working the graph's available steps in priority order.
  - **T3 shipyard with jigs:** a jig is tooling made for one hull plan (an item). It places every frame piece of that plan at once at the Master tier (q 0.95) and cuts frame time to 0.3×. Jigs are what make catalog factories cheap, and players can build them too.
- **Drones are bay-bound and abstract:** no physics bodies, instanced animation only. Each drone's step is a scheduled event. A bay with 8 drones costs nothing per tick.

**Tedium risks and their mitigations** (each is a P1/M8 playtest check, measured in minutes of player input, not step count):

| Risk | Where it bites | Mitigation |
| --- | --- | --- |
| Step count scales with size | 2k-brush ship ≈ 12k steps: impossible by hand | **Work orders:** select a piece, room or the whole site and the character (or a bay) works the available steps in priority order while the player holds the action or walks away from a bay. Ships over 50 m require at least a T1 rig to start (the site says so up front). Hands-on stays for small craft, repairs and refits |
| Shopping for 40 item kinds | every build from plans | Shopping list → **"buy missing"** in one action at any market in range, delivered to the site container (delivery time by distance). Stock is still bought as long bars and cut: no pre-cut kits |
| Fetching and carrying | every step | Site container + "fetch for selection"; T1 rig drones carry; items in the container within 25 m count as in hand |
| Cutting | hundreds of cuts | Bench saw / laser run the cut list as a background job; offcuts round to 0.1 m (no stack explosion) |
| Tests and loading | every line and room | `Test` and `Load` steps are one action each with a timer; failures point at the joint |
| Rework | Unskilled joints mean find, grind, redo | Inspect flags joints in the hologram; "rework all flagged" is one work order. No minigame to fail (D67): quality is known from who did the work |
| Unskilled first build | a new player's first ship is all poor joints | The guided starter skiff reaches Trained in welding and fastening; a T2 bay's drones work at Skilled; a hired Trained welder is cheap |
| Tiny refits | moving one plate or swapping one valve | Diff build: only the delta, NRE on the diff only, soft fields instant |
| Designing a drive from 25 parts | first custom component | "Fork from catalog" opens the catalog plan as an editable copy; the roles panel lists what is missing with a one-click "add suggested part"; auto-route all open ports; "replace part everywhere" |
| Repeat builds | fleets | Production orders and yard builds for repeats; jigs for hulls; the plan's assembly viewer shows the critical path so co-op splits the work. These save time, never unit price (D68) |
| Learning curve | first ship | The first catalog plan a new player gets is a 30-step skiff (keel, 6 frames, skins, seat, thruster, tank) with guided steps |

### Partial ships

- An unfinished ship is a valid physical ship: the as-built state decides what exists (section 5). A framed hull with three skinned pieces, two thrusters and a cockpit can fly if the flight model allows it.
- **Framed pieces** are beam instances (render) plus one box sub-shape per beam in the exterior compound (≤ 24 per piece, construction only), and their beam mass as point moments. They contribute nothing to the SDF bake, rooms or shielding. When the piece is skinned, its beams' sub-shapes go and the bake takes over (hull material density already includes frames, as ship-hulls says).
- **Framed pieces take damage too.** Because they are not in the SDF, damage brushes would otherwise pass through them and an unskinned frame cage would be invulnerable armour. Any damage brush whose volume meets a beam's box destroys that beam: the piece's `Frame` step is marked undone for that member (a `Destroyed` deviation), its mass and sub-shape go, and the support predicate is re-checked (a piece no longer connected to the keel splits off by the ship-hulls rule).
- **What a partial ship cannot do:** components mount only on skinned pieces (dependency 3), a drive needs its tests (so no warp on an untested line), rooms are not sealed, and stations treat an `InProgress` ship as a construction site, not a ship (no docking-class privileges, no Certified build). A partial ship is never a way around a plan's validation: every intermediate state is a subset of a valid plan.
- **Functions work when their prerequisites are done:** a component needs its mount, its required routes and its tests. A drive with an untested line refuses to prime. Round 5: "required routes" includes its wiring; a component whose power or required signal inputs are not yet wired is mounted but dead, and a drive whose `Hold` line is not yet live cannot spool (FailSafe; round 6: a data line, D71).
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
- **Damage repair (round 6, D59)** uses the same machinery, with no plan revision: the plan says what the ship should be, the damage layer says how it differs. Rules, limits and times are combat-and-sensors.md "Repair" (holes: ship-atmosphere.md's ladder).
  - **Weld repair of a hole** and **cut out and replace** of a dent too deep to press (δ > 0.3 m, or torn): the compiler makes a repair piece from the damage brush or the dent's radius: plate stock by its volume, a `Skin` step with weld joints along its rim. Set time per metre of weld; each joint takes the worker's tier (D67), so an Unskilled patch is a weak spot in the stress graph and may leak. Completing it removes the damage brush or dent record.
  - **Press out** (portable hydraulic press, T1): a `Repair` step on one dent of δ ≤ 0.3 m, 30 s + 60 s per 0.1 m³. An Unskilled worker leaves 20 % of the depth behind.
  - **Straighten** (yard jig, T2 bay): a `Repair` step on a hinge; a bend of ≤ 2° can be braced in the field with a frame member welded across the band.
  - **Re-align** is the existing `Test` step on a misaligned component (60 s).
  - Nothing heals on its own; AI crews spend the same steps and stock.

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
- **Wiring state (round 5):** per segment 1 byte (`Intact`, `Damaged`, `Cut`, `Tripped`) plus per switch / relay / breaker 1 byte, only for non-factory ships or once something has changed (factory ships share the plan's all-intact default). Bindings (console × component input, and round 6: component output × console) and the data nets are derived on load and on change, never saved.
- **Aero (round 5):** wing strips live with the bake (ship-hulls); the flight computer's effectiveness matrix is derived on design, damage and wiring events.
- **Stress graph** (for tractor-beams.md): nodes are pieces, plates and component mounts; edges join pieces that share joints, with ratings summed from those joints' strength formulas: tensile N, shear N, bending N·m. Built from the assembly graph plus `JointQ` on load and on each step event (only the touched edges). A failed edge detaches through ship-hulls' split rule. Factory ships use the plan's nominal ratings at factory quality, shared per plan hash, with no per-ship memory. Round 6: `JointQ` is the maker's skill-tier value (D67), so an Unskilled build's edges carry 64 % of nominal weld capacity; an edge that reaches 80 % of its capacity yields into a hinge before it breaks (D59; ship-hulls.md "Deformation", combat-and-sensors.md).
- **Network:**
  - stream-in: plan hash (blueprint fetch on cache miss) + as-built blob (16 B for factory ships);
  - `StepClaimed {ship, seq, step, worker}` 16 B; `StepDone {ship, seq, step, n, q[n]}` 12 B + 1 B per joint;
  - `PlanRevised {ship, newHash}` 20 B, then the diff steps are compiled on each peer from the two cached plans.
  - round 5: `WireState {ship, seq, segment, state, actor}` 16 B (actor only for `Logged` inputs); `SwitchSet {ship, seq, element, value}` 12 B; `CoreEjected {ship, seq, cradle, newBody}` 16 B.
- **Saves:** the same as-built bytes; plans by hash in the plan store.

## 6. Performance

| Item | Cost (estimate, measure in the "Plans" and "Construction" profiler rows) | When |
| --- | --- | --- |
| Designer hull edit | 1–8 brick re-bakes, 1–16 ms CPU on workers, visible 1–2 frames later (ship-hulls) | on commit, never while dragging |
| Housing edit | 1/16 m cells, ≤ 8 m: up to 64 bricks worst case, typically 2–8 | on commit |
| Part place / connect / parameter | function evaluation 2–50 µs, cost quote ~20 µs | on commit, on a job |
| Validation (full ship) | ≤ 5 ms on a job | on save; incremental per dirty class otherwise |
| Assembly compile (2k-brush ship) | 5–10 ms on a job; cut list FFD ≤ 1 ms for 4k pieces | on save, on viewer open, server once per plan hash |
| Server import validation | ~70 ms CPU across workers, no meshing (+ ≤ 26 ms per custom housing; + ≤ 65 ms of new-mesh validation under the per-player triangle budget); worst case bounded by the plan limits | rate-limited, 1 per player per 10 s, low-priority workers: 64 players at the cap is under 1 core (64 × ~135 ms / 10 s ≈ 0.86) |
| Idle or finished ship | **0 per tick** | always |
| Active construction | claim presence check at 2 Hz for claimed steps only, ~50 ns each; completions are heap events | while claimed |
| Drones | scheduled events only | per step |
| Step completion | 1–8 brick re-bakes (skin), one `MutableCompound` modify, stress-edge update O(joints in step) | per event |
| Hologram | the plan baked at **LOD 1** (about a quarter of L0's triangles and memory; review P6), cached per plan hash; drawn as one instanced pass with a per-brick mask of not-yet-built bricks, only for sites within 200 m. Only the available steps' pieces are translucent-filled; the rest of the plan is edges only (a depth-tested outline), so a capital-ship hologram does not cost full-screen translucent overdraw. Exit check: ≤ 0.1 ms GPU for a 2k-brush site filling the screen | per frame, sites near the camera |
| Framed-beam damage | damage brush AABB against the piece's ≤ 24 beam boxes, only for `InProgress` ships | per damage event |
| Memory | plan bake cache shared by hash; as-built arrays only for non-factory ships | |
| Mesh edit (round 5) | 4–30 brick re-bakes for a vertex move on a large mesh; topology ops re-prepare the mesh (1–3 ms) first; validation on commit ≤ 30 ms per mesh on a job | on commit, never while dragging |
| Wiring reachability (round 5; round 6, D71) | O(1) FailSafe-line / feed bitset test per changed segment or sender; full pass (collapse ≤ 16k segments into one-sender nets, then search ≤ 1024 nets: ~20–100 µs) coalesced to ≤ 1 per ship per 10 Hz step, ≤ 0.5 ms per tick across all ships | per wire, switch, relay or sender-power event |
| Joint quality (round 6, D67) | one table lookup per joint at step completion; no per-joint hash, no client input | per step event |
| Wire damage (round 5) | damage brush AABB against segment capsules via per-section segment bins (`WireNet.SectionSegs`) | per damage event |
| Aero (round 5) | ~40 ns per strip + ~0.3 µs allocation per ship; ≈ 11 µs at the 256-strip cap | per tick, ships in atmosphere only, never in warp |
| Dampener (round 5) | ~2 ns per dampener in the force pass (exotic-matter); `m_in` on events; power and EM in the 10 Hz ledger | per tick, covered rooms only |

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

public enum Medium : byte { None, Em, Slurry, Agent, Antimatter, Propellant, Coolant, Gas, Power, Data }   // round 6 (D71): wiring is Power and Data; round 5's Control is removed (ship-atmosphere.md adds Air)
public struct Route { public uint Id; public DefId Spec; public Medium Medium; public PortRef From, To; public ushort FirstWaypoint, WaypointCount; public byte Flags; } // Bent, Bundle, Concealed (round 5). Data routes are directed: From = an output (or splitter / combiner out), To = an input

// ---- Wiring (round 5, D17) ----
public enum DeadLine : byte { FailSafe, FailHold, FailOff }
[Flags] public enum InputFlags : byte { None = 0, Required = 1, Logged = 2, MilitaryOnly = 4 }
public struct SignalInputDef { public ushort Signal; public DeadLine Dead; public InputFlags Flags; }    // declared per family in data; always Data (round 6: was ControlInputDef with a Medium)
public struct SignalOutputDef { public ushort Signal; public InputFlags Flags; }                       // carried on the component's one data-out port
public enum DataNodeKind : byte { Port, Splitter, Combiner, Switch }                                   // Splitter: 1 in, N out, passive; Combiner: N in, 1 out, powered; Switch: e-stop, key switch, kill switch in series
public enum WireState : byte { Intact, Damaged, Cut, Tripped }
public struct WireSegment                                          // compiled from routes, ≤ 4 m each
{
    public uint Id, Route; public Medium Medium; public WireState State; public ushort Gauge;
    public int NodeA, NodeB; public int Ax, Ay, Az, Bx, By, Bz;   // capsule ends, 1/1024 m ship-local; radius from Gauge. Data segments are directed NodeA → NodeB (sender side first)
}
public sealed class WireNet                                        // one per ship, server truth, derived on load and on change
{
    public NativeList<WireSegment> Segments; public NativeList<int> NodeStart, NodeEdges;   // CSR
    public NativeList<byte> SwitchState;                           // switches, relays, breakers
    public NativeList<int> NetOfSegment, NetSender;                // round 6: data nets (intact segments with one sender each, ≤ 1024); NetSender = the sending port, −1 = dead
    public NativeList<ulong> Bindings;                             // bitset (console, component input) reachable along directed data paths
    public NativeList<ulong> StatusBindings;                       // round 6: bitset (component output, console) reachable (what each console can see)
    public NativeList<ushort> BusOfNode;                           // power bus id per node (PowerNet per bus)
    public NativeList<int> SectionSegStart, SectionSegs;          // CSR: segments per 16 m collision section (damage tests)
    public NativeList<ulong> SegLineMask;                          // per segment: which of ≤ 64 FailSafe lines / critical feeds it lies on (same-tick cut test; round 5's SegLoopMask)
    public NativeList<ulong> SenderLineMask;                       // round 6: per sender, the FailSafe lines it drives (its power loss kills them the same tick)
}

// ---- Round 5 family stats ----
public struct DampenerStats { public ushort AMaxGQ8; public long MassMaxKg; public int RadiusMm; public int StandbyW; }   // EmPerNsDamp, DampWPerN: exotic-matter constants
public struct AeroStrip { public int AreaCm2, ChordMm; public int Qx, Qy, Qz; public uint Frame; public ushort Profile; public byte SurfaceMask; }   // quarter-chord point, smallest-three frame
public struct ControlSurfaceStats { public ushort MaxDeflectionQ8, RateDegPerSQ8; public ushort ChordFractionQ16; public int TorqueNm; }
public struct CoreEjectStats { public long CradleMassCapKg; public ushort EjectTicks, EjectDvCmS; public byte QuickDisconnects; public bool AutoEject, OverrideKey; }
// D14 runtime group: warp.md's WarpDriveSet (≤ 8 drives reachable from the plot's Engage, weights w = min(P, PRated) fixed at spool start; ShipWarpCooldownEnd per root grid); the designer only previews it
public struct CommandCoreStats { public byte HousingRating; public ushort BufferS; public bool LocalPanel; }   // r6, D27: required once on PlanClass.Station; the capture clock is factions-and-law.md's (CaptureS per station class)
// ---- Round 6 ----
public struct SuitLockerStats { public byte Slots; public bool Refill; }   // D64: 1 / 2 / 6 slots; Refill = an Air route and LV power are present (runtime in ship-atmosphere.md)
// D55: fn.shield.particle / .energy / .gravity evaluate into combat-and-sensors.md's shield structs (its "C# structures"); Plans only derives them and checks mounts, feeds and coverage.
public enum Trade : byte { Welding, Fastening, Pipefitting, Electrical }
public enum SkillTier : byte { Unskilled, Trained, Skilled, Master }       // D67: the only input to joint quality (data/construction/quality.json tierQ)
public struct EscapePodStats { public byte Seats; public ushort LifeSupportH; public ushort HeatShieldMWm2; public int DvCmS; }
public enum PlanClass : byte { Ship, Station }
public struct Waypoint { public int X, Y, Z; }                     // 1/1024 m

public enum ElementKind : byte { Brush, Part, Connection, Route, Placement, Test, Damage }   // Damage (round 6): a damage brush, dent or hinge id, the target of repair steps (per ship, not in the plan)
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
    public PlanClass Class;                                        // round 5: Station adds the pod and thruster rules; r6: and exactly one command core
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
public enum StepKind : byte { Keel, Frame, Skin, Armour, Mount, Route, Penetrate, Test, Load, Inspect, Strip, Drain, Repair }   // Repair: round 6 (D59), targets a dent or hinge record
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

public enum OpKind : byte { InsertBrush, RemoveBrush, ModifyBrush, PlacePart, RemovePart, MovePart, SetParams, Connect, Disconnect, SetJoint, AddRoute, RemoveRoute, EditRoute, PlaceComponent, RemoveComponent, AddOrderEdge, SetMeta,
                            MeshVertices, MeshTopology, MeshAttributes, SetChunkKind }   // round 5
public unsafe struct PlanOp { public OpKind Kind; public byte Size; public ElementRef Target; public fixed byte Before[42], After[42]; }   // 96 B (12 + 2·42), invertible; payload fits a 34 B brush or a 28 B part
// Round 5: mesh ops do not fit 42 B. They are variable-length records in the same byte log: {Kind, mesh index, before blob, after blob},
// MeshVertices = (first, count, old and new positions); MeshTopology = whole old and new face/corner/crease/panel arrays of that mesh (≤ ~90 KB each);
// MeshAttributes = crease or panel bits. The log keeps the last 64 MB and drops the oldest undo steps beyond it.
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
             "Interlock": [0,1], "PurgeValve": [0,1], "Trap": [1,1], "InlineAssay": [0,1], "AutoPurge": [0,1],
             "EjectCradle": [1,1], "QuickDisconnect": [1,16], "AutoEject": [0,1], "OverrideKey": [0,1], "Beacon": [1,1], "LocalPanel": [1,1] },   // round 5
  "dataInPorts": 4,                                                                 // round 6 (D71): every input and output is Data, so no "medium" field
  "inputs": [ { "signal": "Hold", "dead": "FailSafe", "flags": ["Required"] },      // one line, no combiner; e-stops in series on it
              { "signal": "Cancel", "dead": "FailHold" }, { "signal": "Engage", "dead": "FailHold" },
              { "signal": "Plan", "dead": "FailHold" },
              { "signal": "Purge", "dead": "FailHold" }, { "signal": "Eject", "dead": "FailHold" },
              { "signal": "Override", "dead": "FailOff" },
              { "signal": "BeaconSwitch", "dead": "FailHold", "flags": ["MilitaryOnly"] } ],
  "outputs": [ { "signal": "Beacon", "flags": ["Required", "Logged"] }, { "signal": "Alarm" }, { "signal": "Status" } ],   // warp.md's Beacon out; a cut is a Logged tamper
  "chamberRule": { "minSf": 3.0, "pChamberMPa": "data", "housingRating": "componentDamage.chamberRating" },   // 8, exotic-matter
  "paths": [ { "medium": "Em", "via": ["Socket:Fuel", "Interlock?", "Diluter", "Chamber"] } ],
  "loops": [ { "medium": "Agent", "via": ["Reservoir", "Pump", "Diluter", "Chamber", "Separator"] } ],
  "coeff": { "rhoLine": 150, "rhoAgent": 1400, "tolBase": 0.02, "tolPerStage": 0.0075, "tolCap": 0.08,
             "mixBase": 60, "mixPerExtraStage": 10, "purgeRefDn": 40, "pOpMPa": 20.0, "breachBase": 0.5, "breachSfGain": 0.25 },
  "envelope": { "lineCapKg": [1, 600], "loopAgentKg": [5, 600] } }

// data/functions/dampener.json   (round 5, D9)
{ "id": "fn.dampener", "evaluator": "Dampener", "stats": "DampenerStats",
  "roles": { "Coil": [2,32], "Feed": [1,1], "Conditioner": [1,1], "Controller": [1,1] },
  "inputs": [ { "signal": "Enable", "dead": "FailOff", "flags": ["Required"] }, { "signal": "Limit", "dead": "FailHold" } ],
  "coeff": { "aMaxG": { "D": 2, "C": 3, "B": 6, "A": 10 }, "constants": "exotic-matter dampener (emPerNs 1e-11, wPerN 1.0, standbyW 1e4, rampS 0.5)" },
  "catalogMustEqual": "fc.inertial_dampener.s|m|l", "gravitonProjection": "Internal" }

// data/functions/aero_control.json   (round 5, D10)
{ "id": "fn.aero.control", "evaluator": "ControlSurface", "stats": "ControlSurfaceStats",
  "roles": { "Hinge": [1,4], "Actuator": [1,4], "Panel": [1,1] },
  "inputs": [ { "signal": "Deflect", "dead": "FailHold" } ], "envelope": { "maxDeflectionDeg": [5, 40] } }

// data/wiring/harness_standard.json   (round 5, D17; round 6, D71: one-way data)
{ "id": "harness.standard", "powerBuses": "nearestPerDeck", "consoles": ["Cockpit", "Engineering"],
  "commandLines": "consoleToAllInputs", "statusReturns": true,
  "holdLines": [ { "input": "Hold", "sender": "Engineering", "stopsInSeries": ["DriveRoom", "Engineering", "Cockpit"] } ],   // was estopLoops
  "optional": [ { "from": "PressureSensor", "to": "Cancel" } ],                                                             // warp.md: a breach sensor split to the drive's Cancel
  "splitter": "part.data.splitter.8", "combiner": "part.data.combiner.8", "breakerPerConsumer": true }

// data/functions/suit_locker.json   (round 6, D64; runtime in ship-atmosphere.md)
{ "id": "fn.suit_locker", "evaluator": "Core", "stats": "SuitLockerStats",
  "classes": { "S": { "slots": 1 }, "M": { "slots": 2 }, "L": { "slots": 6 } },
  "sockets": ["Power:LV", "Air"], "inputs": [], "outputs": [] }

// data/functions/shield_particle.json   (round 6, D55; every number is combat-and-sensors.md's class table)
{ "id": "fn.shield.particle", "evaluator": "ParticleShield", "stats": "combat-and-sensors.md",
  "roles": { "EmitterGrid": [1,32], "PlasmaSource": [1,1], "Capacitor": [1,8], "Controller": [1,1] }, "mount": "Exterior",
  "inputs": [ { "signal": "Raise", "dead": "FailHold" }, { "signal": "FacingPriority", "dead": "FailHold" } ], "outputs": [ { "signal": "Status" } ],
  "catalogMustEqual": "combat-and-sensors particle shield S|M|L", "gravitonProjection": "None" }
// fn.shield.energy (roles ProjectorCoil, HeatPool, Radiator, Controller; gravitonProjection None) and
// fn.shield.gravity (roles Emitter, Gimbal, Controller; EM and AM feed sockets; inputs Raise, Track; gravitonProjection External) follow the same shape.

// data/plan_classes.json   (round 5, D25)
{ "Station": { "podSeatsPerBerth": 1.0, "thrustAllAxes": "Warning", "stationMinAccelG": 0.01, "anchorFeeds": 2 } }   // hard pod gate: exotic-matter RequirePodsForImpactFootprint

// data/economy/pricing.json
{ "wage": 4.0, "catalogScale": 0.85, "vendorMargin": 0.20, "retail": { "stock": 1.10, "part": 1.40, "component": 1.08 },
  "yardMargin": 0.05, "jobLabour": 1.6, "jobMargin": 0.35, "rawMarkup": 1.15, "machineRate": 5.0, "designStamp": 0.06,
  "nre": { "part": 40, "component": 250, "ship": 150, "perDistinct": 0.25, "perJoint": 0.01 },
  "toolingPerMaterial": 0.5, "partLicence": 0.05, "componentLicence": 0.05, "planLicencePerBuild": 0.05,
  "buyback": { "itemOfWholesale": 0.8, "shipOfPremade": 0.6, "shipCertified": 0.7 }, "revisionNreOnDiff": true }

// data/construction/quality.json
// round 6 (D67): quality is a lookup by skill tier; toolBase, skillPerLevel, conditions, pace and jitter are gone
{ "tierQ": { "Unskilled": 0.40, "Trained": 0.65, "Skilled": 0.80, "Master": 0.95 }, "specQ": 0.65, "factoryTier": "Master",
  "machineTier": { "bayDrone": "Skilled", "yardRobot": "Master", "jig": "Master" },
  "tierThresholds": { "Welding": { "unit": "m", "Trained": 50, "Skilled": 1000, "Master": 10000 } },   // other trades: same shape, by joint count
  "crewTierMap": ["Unskilled", "Trained", "Skilled", "Master"],                                         // ai-ships-and-crew.md Green / Trained / Veteran / Ace
  "leakSeal": 0.45, "seamLeakWidthMm": 0.02, "leakLine": 0.5, "claimLapseS": 10, "presenceHz": 2, "reachM": 3 }

// data/shipyards/faction_a_hangar.json
{ "id": "yard.faction_a.prime", "faction": "faction_a",                                   // round 6 (D68): a yard stocks, licenses and refits only its faction's catalogue; no quantity pricing anywhere
  "showHangar": [ { "plan": "ship.wren", "stock": 3, "restockDays": 2 } ],
  "services": ["JobShop", "YardBuild", "Inspect"], "planMarket": true }
```

Also: `data/plans/catalog/*.bplan` (every catalog part preset, component and ship, authored in Designer.App), `data/materials/*.json` gains `frameProfile`, `skinSheet`, `ribSpacing`, `formedPlate`, `crPerKg`, `sigmaWeld`; `data/tools/*.json`; `data/fabricators/*.json` (machine tier, rate, power, `machineRate`).

## 9. Changes to other docs

**ship-hulls.md**
- `ComponentPlan` (in `Plans`) becomes the source of truth for a component: `Family` + part graph replace `FunctionCore`. `ComponentDesign` stays in `Ships` as the derived view the bake and runtime read (housing, cutters, sockets, `MassMoments`, portal, stats blob), compiled from the plan, so `Ships` never references `Plans`. A "bought function core" is a catalog component plan, sealed. Answers its open question 4.
- `ShipBlueprint.Materials` becomes a table of `PlanRef` (catalog material or laminate plan); `HullBrush.Material` still indexes it.
- `HullChunkGroup` gains a kind (`Hull`, `Armour`, and in round 5 `Wing`); armour plates are armour chunk groups of laminate brushes, bolted on.
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

**tractor-beams.md** (revision 2; r6)
- Consumes `StressEdge` ratings (nominal per plan, as-built per ship) and detaches through ship-hulls' split rule.
- D28: a tractor emitter's `TractorDrive` is derived from the plan: `Graviton` (pull only) unless an AM feed is routed and fed, then `Reaction` (`Overdrive` by tractor-beams' quality rule). The designer and docking scan show it. D26 consent and hostile records are runtime (tractor-beams.md), not plan state.

**Round 5 changes to other docs**
- **ship-hulls.md:** mesh brushes (`BrushShape.Mesh`, `HullMesh`, `MeshTable`, `BrushFlags.Inset`), mesh validation, editor, limits and costs; `ChunkKind` with `Wing`; eject path and pod tube cutters; `SocketType.Control` (removed again in round 6, D71). (Done in its round 5 copy.)
- **exotic-matter.md:** (its round 5 copy already has the dampener core, standard core eject with the ×4 eject mount, the ×8 chamber, linked drives (now warp.md's `WarpDriveSet`) and anchors on any grid; this doc's families must evaluate to those numbers.) Still to add there: the eject mount is this doc's eject cradle with quick-disconnects and a mechanical handle that needs no power or wiring; `AutoEject` is set by an auto-eject controller part; the override additionally needs an override key switch part wired to a console; `SF_chamber ≥ 3` is the design rule that earns `chamberRating` 8; `PowerNet` is per bus, buses come from the wiring; anchor cores need two independent power feeds; mid-warp eject follows warp.md (accepted in warp, separates at `max(T_exit + 1, command + 30)`; already in its copy).
- **warp.md:** (its round 5 copy already has wired drive ports, the cancel table, the beacon and "Multiple drives".) Mapping only: its `Abort` is raised by this doc's `Hold` FailSafe input (round 6, D71: a one-way data line with the e-stops in series, not a Control loop), a Data `Cancel` or the local panel; its power cancel is a breaker trip or lost feed on the drive's bus; its Beacon out is this doc's `Beacon` output to a powered antenna part, military classes adding a `BeaconSwitch` input.
- **review.md / README.md:** flight model is both (D10): system 6 (atmosphere) gains wing strips and control surfaces; the M6 flight computer gains control allocation and Atmo mode. Interior gravity (D9): plating and dampener are separate components.
- **Stations (exotic-matter anchors, frames-and-zones):** anchors may sit on any grid; anchor `Engage` / `Release` / `CrawlTarget` / `CrawlSpeed` inputs and a `Failing` output are wired (D25), with two independent power feeds per anchor core; station plans are warned without escape pods or thrust (exotic-matter's impact-footprint pod rule is the hard gate).
- **Sieges and capture (r6, D27; the rule is factions-and-law.md's since round 6):** every station plan carries one `CommandCore` (ownership token, capture at its local panel); cutting station power never touches anchor containment feeds and never transfers ownership. exotic-matter's anchor overload alarm and "capture transfers anchor authority" are its own.

**Round 6 changes to other docs** (D55, D59, D64, D67–D71; the full list with reasons is in "Round 6 notes")
- **ship-hulls.md:** `SocketType` is `Mount, Power, Fuel, DataIn, DataOut, Coolant` (no `Control`); the three damage forms and what the hull stores; shield emitter and suit locker hull features. (Done in its round 6 copy.)
- **warp.md:** already on D71 (its `Hold` is a one-way data line with e-stops in line; collars carry power and data). Nothing more needed.
- **ship-atmosphere.md:** every Control port it declares becomes a Data port; its `Seal` "loop" is a hold line; `SegLoopMask` is `SegLineMask`; the seam-leak width it proposed is adopted here; no tier now produces q 0.3, so its A3 exit check should name an Unskilled seam (q 0.40).
- **exotic-matter.md:** "docked ships whose collar carries power and control wiring" becomes "power and data wiring"; "inside the sealed subtree" (eject in cruise) becomes "with the cradle latched" (warp.md, D66). tractor-beams.md, rings-and-asteroids.md and wormholes.md name no Control wiring.
- **combat-and-sensors.md:** its families are listed here; its shield class rows are the catalog targets; its repair steps are compiled here; D67's tier rule is the one it cites for pressing and weld repair.
- **ai-ships-and-crew.md:** crew skill tiers map to the four trade tiers; lockers are service posts; AI repair spends the same steps.
- **factions-and-law.md:** consistent (no fleet discount, faction catalogues); `data/shipyards` rows carry `faction` here too.
- **README.md, project CLAUDE.md:** "wired (power, data, control)" becomes "power and data, one-way data"; the designer is bundled with the game.

## 10. Milestones

| When | Scope | Exit check |
| --- | --- | --- |
| **M5** (minimal hull) | `Plans` (ship and component levels with catalog cores only), `.bplan` v1, xxHash64, plan store by hash; `Designer` core (session, ops, undo, validation schema/geometry); `Designer.App` with box/wedge/cylinder hull tools and component placement; in-game DesignTerminal with the same UI; creative instant build | Build a 15 m craft in the standalone app in about 5 min, save, import in game, instant-build; the same file loads byte-identical in both hosts; plan hash identical on client and server (server re-canonicalises); the assembly-reference test passes (no Vulkan, ImGui or Glfw in `Plans`, `Designer`, `Ships`, `Sim`) |
| **M5b** (after M6, which brings thrust and the flight computer) | Construction v1: assembly compile (keel, frame, skin, mount, route, test, load), support predicate, access rule, `BrushYield`, cut list, `PieceStack` stock and cutting, weld and bolt joints with set times and skill-tier quality (round 6, D67), claims, hologram guide, as-built (Factory and InProgress) in bake and mass, show hangar purchase, catalog plan licences, pricing modes Catalog and SelfAssemble | Solo-build the catalog Wren from plans in ≤ 75 min with T1 tools (D70: the confirmed order of magnitude); the same build by an Unskilled and a Skilled character takes the same time and gives weld edges at 64 % and 88 % of nominal; compile ≤ 10 ms for a 2k-brush plan; idle and finished ships 0 per tick in the profiler; a half-built Wren with the cockpit and thrusters flies |
| **P1** (with exotic-matter D1, after M7) | Parts and component designer: part templates, laminates, part graph, ports, routing A*, function families (chemical thruster, tank, trap, warp slurry and recirculating), validation function class, NRE and certification, job shop and production orders, fabricators (SelfProduce), plan market and licences, untrusted import | Catalog C recirculating plan evaluates exactly to exotic-matter's C row; a custom drive with SF 1.1 breaches at Integrity 0.70 under the shared damage rule; the Wren worked numbers reproduce within 1 %; the cost ordering A < B < C < D' < D < E holds at N = 1, 5, 20 and the limit for every catalog ship and drive, and self-produce ends within 3 % of catalog; the strip, buyback and yard-build arbitrage invariants hold over the whole catalog; a fuzzed import corpus (truncated, inflated, colliding-hash, over-limit, unknown-id, modded-data bundles) is refused within the plan limits' time bound; evaluation ≤ 50 µs |
| **M8** (with rooms) | Plates as armour chunk groups, seal joints and Leak edges from quality, inspection and rework, modification diffs (strip, drain, salvage), co-op claims with MaxCrew, T1 rigs, T2 bays with drones, jigs, Certified build flag, stress edges handed to tractor beams | A room sealed by an Unskilled worker (q 0.40) leaks at the predicted area and passes its leak test after rework by a Trained one; a refit from A to B does only the delta and returns salvage as specified; two players halve a 200-step build within 15 %; a damage brush through an unskinned frame removes the beams it meets; a refit that moves one plate charges NRE on that plate only; tedium playtest: the Wren from plans needs ≤ 15 min of player input with a T1 rig (the rest is timers and drones) |
| **M5c** (round 5, after M5) | Mesh brushes: `HullMesh`, canonical bytes and `MeshHash`, integer Catmull-Clark, mesh SDF (sign rows, band, inset), validation, Mesh editor (select, transform, extrude, inset, loop cut, bevel, bridge, merge, dissolve, knife, fill, crease, mirror, subdivision, convert), panels | A fully mesh-modelled 20 m hull with a hollowed interior, a primitive door cut and a damage hole bakes with sharp creases; mesh bake ≤ 3× a primitive brick in the profiler; the same mesh hashes and validates identically on client and server; a fuzzed corpus of open, non-manifold, self-intersecting and over-limit meshes is refused within the limits' time bound |
| **M5b+** (round 5, with M5b) | Wiring v1 (round 6, D71): Power and one-way Data routes, segments, splitters, combiners, consoles, breakers, standard harness, schematic view, command and status bindings, circuit test step, warp `Hold` line | A Wren built from plans: pressing its drive-room e-stop, cutting the hold line anywhere, or unpowering its sender cancels a spool the same tick; cutting the bridge's command cable leaves the bridge able to watch but not cancel, and cutting the return leaves it blind; "what controls this?" matches the bindings; reachability ≤ 100 µs for 16k segments |
| **M6+** (round 5, with M6 and planets' atmosphere) | Aero: wing chunks and strips, wing components, control surfaces, control allocation, Atmo mode | A winged 15 m craft glides and stalls as its tables predict; a hole in one wing rolls it; aero ≤ 2 µs per aircraft per tick |
| **M8+** (round 5, with M8) | Dampener family separate from plating; wire damage, shorts and breaker trips; splice repair; cutting torch on hulls | A 10 g ram over an S dampener (3 g) throws crew with 7 g; the catalog S/M/L dampener plans evaluate to exotic-matter's rows; a damage brush across a cable cuts what the schematic predicts; an unbreakered short spreads one hop per tick |
| **D1+** (round 5, with exotic-matter D1) | Eject cradle, quick-disconnects, mechanical handle, auto-eject, override key switch, linked drive groups, beacon part | Every runaway in the trigger matrix is survivable by eject started at onset; two drives on one `Engage` signal carry `Σ f_i · MassCap_i` at the `w`-weighted quality, one off that signal stays out, a ninth drive stays out, and chaining drive A then B is refused until A's cooldown ends; a drive without the key switch cannot be overridden |
| **Stations** (round 5) | Station plan class rules, escape pods and pod bays, anchor inputs (crawl, release, failing output), redundant anchor feeds, command core (r6, D27) | A station plan without a command core is refused; a station plan without pods or thrusters is warned, and its anchor refuses engagement over an atmosphere with an impact footprint; cutting one anchor feed never drops the station; a pod launched from an anchored station clears the tube and lands on its heat shield |
| **R6** (round 6; with ship-atmosphere A2 and combat-and-sensors' shield and damage milestones) | Suit and hood locker families and their validation rows (D64); shield emitter families, mounts, feeds and the facing-coverage view (D55); `Repair` steps: press out, cut out and replace, straighten, brace (D59) | A Wren plan with no locker by its airlock is warned; the catalog S / M / L shield plans evaluate to combat-and-sensors.md's class rows; a gravity emitter on an under-rated mount is refused; a pressed-out dent is gone, and one pressed by an Unskilled worker keeps 20 % of its depth; a cut-out patch welded by an Unskilled worker shows in the stress view |
| **C2** (later) | Sealed prefab sales, reverse engineering by strip, multi-user design sessions, plate nesting, pipe bending | |

## 11. Files

- **`src/Plans/`:** `PlanFormat.cs` (header, sections, canonical BODY, bundle), `XxHash64.cs`, `PlanStore.cs`, `Catalog.cs` (data loader, `DefId`), `Laminates.cs`, `Parts.cs` (templates, presets, derived props), `ComponentPlan.cs`, `ShipPlan.cs`, `PartGraph.cs` (CSR, role assignment, path/loop BFS), `Families/WarpDrive.cs`, `Families/Thruster.cs`, `Families/Tank.cs`, `Families/Core.cs` (single-core families), round 5: `Families/Dampener.cs`, `Families/Aero.cs` (wing component, control surface), `Families/EscapePod.cs`, `Families/Anchor.cs`, `Families/CommandCore.cs` (r6), `Wiring/WireCompile.cs` (routes → segments, nodes, power buses, directed data nets), `Wiring/Reachability.cs` (command and status bindings over directed data paths, FailSafe lines and their same-tick masks; round 6, D71), `Wiring/WireFaults.cs` (cut, damage, short, trip), round 6: `Families/Shields.cs` (D55; evaluates into combat-and-sensors.md's structs), `Families/SuitLocker.cs` (D64; listed by ship-atmosphere.md too), `Construction/JointQuality.cs` (D67: tier table), `Construction/RepairSteps.cs` (D59), `DriveGroups.cs` (D14 preview of warp.md's `WarpDriveSet`), `PlanClasses.cs`; `Validation.cs`, `Pricing.cs` (modes, NRE, quotes), `Certification.cs`, `Licences.cs`, `AssemblyCompiler.cs` (pieces, members, dependencies, access rule), `CutList.cs`, `StressGraph.cs`, `AsBuilt.cs`, `PlanDiff.cs`, `ImportGuard.cs` (limits, inflate bounds, canonical re-encode, byte-compare dedup).
- **`src/Geometry/`** (Jolt-free, moved from `Physics`): `TriangleBvh.cs`.
- **`src/Designer/`:** `DesignSession.cs`, `PlanOps.cs`, `UndoStack.cs`, `Selection.cs`, `Tools/HullTool.cs`, `Tools/PlateTool.cs`, `Tools/PartPlacer.cs` (port snapping), `Tools/Router.cs` (A*), `Tools/CrossSection.cs`, `IPlanHost.cs`; round 5: `Tools/MeshTool.cs` (ship-hulls), `Tools/WingTool.cs`, `Tools/Harness.cs` (standard harness), `Tools/Schematic.cs` (layout of the wiring diagram).
- **Round 5, elsewhere:** `src/Designer.Ui/` gains `MeshPanel`, `WingPanel`, `SchematicPanel`; `src/Sim/` gains `Flight/AeroStrips.cs` and `Flight/ControlAllocation.cs`, `Systems/Dampeners.cs`, `Systems/CoreEject.cs`; `src/Game/ConstructionTool.cs` gains the wire cutter, splice and cutting torch; `tests/Plans.Tests` gains wiring reachability and fault cases, drive-group aggregation, station class rules; `tests/Ships.Tests` gains mesh canonical hash, integer subdivision determinism, mesh SDF sign vs a brute-force winding number, and the invalid-mesh corpus.
- **`src/Designer.Ui/`:** `DesignViewport.cs`, `Gizmos.cs`, panels: `HullPanel`, `PlatePanel`, `LaminatePanel`, `PartPanel`, `ComponentPanel`, `StatsPanel`, `CostPanel`, `ValidationPanel`, `AssemblyPanel`, `DiffPanel`, `LibraryPanel`.
- **`src/Designer.App/`:** `Program.cs`, `DesignerApp.cs`, `FilePlanHost.cs`.
- **`src/Ships.Rendering/`:** `HullRenderer.cs`, `PartInstancer.cs`, `GhostRaymarch.cs`, `HologramPass.cs`; `shaders/hologram.*`, `shaders/part_instance.*`.
- **`src/Sim/Construction/`:** `ConstructionSites.cs`, `StepClaims.cs`, `StepEvents.cs`, `Drones.cs`, `Jigs.cs`, `ProductionOrders.cs`, `ShowHangars.cs`.
- **`src/Game/`:** `DesignTerminal.cs`, `GamePlanHost.cs`, `ConstructionTool.cs` (hologram, cutting, joint tools).
- **`src/Net/`:** `PlanEvents.cs` (fetch by hash, import, revise), `ConstructionEvents.cs`.
- **`tests/Plans.Tests`:** format round trip (bytes identical, hash stable), catalog drive evaluation equals the exotic-matter table, SF → breach threshold, NRE and quote numbers equal the worked tables, compile determinism (same graph on two runs and two threads), access rule, support predicate, cut list waste ≤ FFD bound, diff/strip ordering, cost ordering and arbitrage invariants (section 3), import fuzz corpus, assembly-reference rule (no Vulkan/ImGui/Glfw in headless assemblies). Round 6: one-way data (a command reaches an input only along a directed path; a cut return blinds a console without stopping commands; two senders on one line and a combiner on a FailSafe line are refused; a hold line dies the same tick from an e-stop, a cut, damage or its sender's power); joint quality is a pure function of tier (same inputs, same byte, no client data); no price in the quote model changes with quantity (D68); suit-locker and shield validation rows. **`tests/Designer.Tests`:** every op inverts exactly; undo/redo of 10k random ops returns the original bytes; router finds paths and respects clearance.

## 12. Open questions for the user

Questions 1–4 and 6 were answered in round 6 and are closed. They keep their numbers because the review notes cite them.

1. ~~**Hands-on joint quality.**~~ **Decided, D67:** no weld minigame. A joint takes a set time; quality comes from the worker's skill tier only, and unskilled welders make poor welds that weaken the joint ("Joints, tools, time and quality").
2. ~~**Fleet amortisation.**~~ **Decided, D68:** no fleet discount. `designStamp` stays, the `designStamp` 0 opt-in is withdrawn, no price depends on quantity, and fleets generally fly their faction's ships (section 3).
3. ~~**Standalone distribution.**~~ **Decided, D69:** Designer.App is bundled with the game, not a separate free download ("One core, two hosts").
4. ~~**Construction length.**~~ **Decided, D70:** about 70 minutes solo for a 15 m ship with basic tools is the right order of magnitude.
5. **(Round 5) Dampener overlap.** Proposed: a room covered by two dampeners takes the stronger one (ratings do not add), so big ships need one dampener per zone. Should overlapping dampeners add up instead, like linked warp drives?
6. ~~**(Round 5) Wiring depth.**~~ **Decided, D71:** two media, power and data. There is no Control medium; a data cable can be split to all inputs and outputs but is one-way ("Wiring").
7. **(Round 6) Skill tier numbers.** D67 fixes the rule, not the values. Proposed: four tiers with q 0.40 / 0.65 / 0.80 / 0.95, so an Unskilled weld holds 64 % of its rating and Unskilled seals leak slowly; Trained after 50 m of weld, Skilled after 1,000 m, Master after 10,000 m; the rule applied to every joint kind, not only welds. Right order of magnitude, and should new characters start Unskilled or Trained?

Revision and review history: [history/ship-design-and-construction.md](history/ship-design-and-construction.md).
