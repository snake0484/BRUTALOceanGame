# Ship atmospheres

Status: design, nothing built. Written 2026-11-26 against decisions.md (D9–D37), README.md, ship-hulls.md, ship-design-and-construction.md, frames-and-zones.md, exotic-matter.md, planets.md, warp.md, wormholes.md, rings-and-asteroids.md and tractor-beams.md. Every number is a data knob; every cost is an estimate to measure in the "Air" profiler rows. Lead review applied the same day: see "Review notes" at the end.

## User decisions this doc implements

Recorded as D38–D42 in decisions.md. Not re-litigated here.

- **D38** Ships have atmospheres inside.
- **D39** Hull damage can leak atmosphere.
- **D40** Airlocks are needed.
- **D41** More advanced ships can use **air barriers**: a small particle shield across an opening that keeps air in the room. Needs constant power.
- **D42** **Emergency shields** may exist that stop breaches from leaking.

## Recommendation in one paragraph

Every sealed room of the hull bake holds three integer gas amounts (total, O2, CO2) at one fixed temperature. Rooms and openings form a graph; gas moves over an edge by an orifice law (choked below half the upstream pressure), integrated per pair with an implicit, clamped, integer step that conserves gas exactly and cannot overshoot. A sealed ship at rest costs **nothing per tick**: connected rooms at equal pressure are one lazily integrated zone with constant rates and one scheduled event. Only rooms out of equilibrium are stepped, at 10 Hz in the ledger slot. Damage never relabels rooms: a hole is found as the cells the damage layer opened, and becomes an edge with an area. Doors, bulkheads, airlocks, air barriers and emergency shields are all the same thing to the solver: an edge whose effective area changes on events.

## 1. Gas model

### State per room

| Field | Type | Unit | Notes |
| --- | --- | --- | --- |
| `VolumeL` | int32 | litres | air cells of the bake × 125 (a 0.5 m coarse cell is exactly 125 L). Components and furniture do not subtract |
| `Q` | int64 | Pa·L (= mJ of pV) | total gas amount at the reference temperature |
| `QO2`, `QCO2` | int64 | Pa·L | partial amounts. Inert (N2 and anything else) is `Q − QO2 − QCO2`, never stored |
| `SetpointPa` | int32 | Pa | 101,325 by default; 0 marks a designed **vacuum room** (core bay, launch tube, open hold) |

- **Pressure is derived:** `p = Q / VolumeL` (Pa); partial pressures the same way. No pressure is stored, so nothing can drift from the amounts.
- **Why amounts in Pa·L:** pressure, mixing and flow are all moles-based, and Pa·L at a fixed temperature is moles times a constant (1 mol = 2,437,000 units at 293.15 K). 1 unit is about 0.012 mg of air. A 1,000 m³ room at 1 atm holds 1.0e11 units; the largest legal room (1e9 L at 100 bar) holds 1e16, inside int64.
- **Mass** is needed only for tanks and trade: O2 76,160 units per gram, N2 87,040, CO2 55,390 (data). **Air mass is not in `MassMoments`** (1.2 kg/m³ is under 1 % of hull mass) and not in the dampener's `m_in`.
- **No temperature.** All gas is at the hull's 293 K. The hull's thermal mass dominates, adiabatic chill during a blowdown is ignored, and nothing in the game reads cabin temperature. Heat hazards stay where they are: planets.md's deep heat and `MaxAmbientK` on components. A room that is open to the exterior is at the exterior's temperature (see `Exposed`).
- **Three species, not four.** Unbreathable air is expressed as low O2 or high CO2 (a CO2 world poisons, a hydrogen giant suffocates). Toxic trace gases, smoke and fire are out of scope.
- **Integer everywhere, in one fixed order.** The pair step is sequential (each edge sees the amounts the previous edge left), so the result depends on edge order; the order is the edge index, and edge indices are assigned deterministically (section 2, "Breaches from the bake"). The gas state is then a pure function of the previous state and the tick: testable to the unit, and the designer's breach sim reproduces the live ship. Only the wind force (a Jolt force) is float. Clients never simulate gas.
- **`SetpointPa` is capped at 120 kPa for rooms** (vessels have ratings instead), so a regulator cannot be used to blow doors.

**Changes ship-hulls.md:** `Room { float VolumeM3; float PressureKpa; GasMix Gas; }` and `RoomEdge` are replaced by the `ShipAir` arrays (section 10). `GasMix` is not needed for rooms (planets.md's `GasMixture` stays for scooped cargo).

### Vessels

Tanks are **rooms you cannot walk into**: the same arrays, flag `Vessel`, a rated pressure, a fixed volume. An O2 bottle, an N2 bottle and the mixed **air receiver** (where airlock pumps put reclaimed air) differ only in contents. Pumps and regulators are edges between a vessel and a room. One code path conserves everything.

| Vessel (catalogue) | Volume | Rated | Free gas at 1 atm | As air / O2 |
| --- | --- | --- | --- | --- |
| Bottle S | 50 L | 20 MPa | 9.9 m³ | 12 kg / 13 kg |
| Tank M | 1 m³ | 20 MPa | 197 m³ | 237 kg / 263 kg |
| Tank L | 10 m³ | 20 MPa | 1,970 m³ | 2.4 t / 2.6 t |
| Receiver S / M | 0.25 / 2 m³ | 2 MPa | 4.9 / 39 m³ | sized to the airlocks it serves |

### Exterior ambient

One `Ambient { int Pa; ushort O2Q16, CO2Q16; short TempK; AmbientKind Kind; }` per grid. It is computed on demand from the grid's altitude (closed form) and cached for the tick. It is refreshed at 1 Hz only while the grid has an active, slow or `Exposed` exterior edge and is moving inside an atmosphere, and on frame-change events. A sealed ship parked on a planet never asks. It is an infinite reservoir.

| Where | Pressure | Composition |
| --- | --- | --- |
| Space, tunnels, airless bodies | 0 | none |
| Rocky planet | `p0 · exp(−h / H)` from the body's `AtmosphereId` (the same record warp.md's blast and the aero drag read) | `o2Frac`, `co2Frac` from data. **Breathable** is derived, not declared: ppO2 16–50 kPa and ppCO2 < 1 kPa at that altitude |
| Gas giant | planets.md's closed form (1 bar at the datum, 3.7 bar at 50 km, 20 bar at 150 km) | O2 0, CO2 0 (hydrogen and helium count as inert) |
| Under the sea sphere | not gas: exterior edges below the sea surface carry no flow | flooding with water is not modelled (open question 4) |
| Another docked grid | not ambient: the collars make a room-to-room edge between the two grids (section 4) | |

`AtmosphereDef` gains `surfacePa`, `scaleHeightM`, `o2Frac`, `co2Frac` if it lacks them (change to planets.md data).

## 2. Flow

### Rooms are design rooms; they never relabel at runtime

- **A room is a connected component of design air with every portal treated as closed** that does not touch the ship-bounds margin. This is exactly review.md C11's `GridVolume` air, so rooms and `GridVolume` share one labelling pass.
- "Design" means the as-built bake **without the damage layer**: unbuilt (`Planned`, `Framed`) pieces are missing, so a half-skinned room touches space and is not a room, as ship-design-and-construction.md already says.
- **Door, duct, barrier and damage events never change room ids.** They change edges. Room ids are therefore stable keys for `ShieldTable`, contamination records, plating, dampener coverage, trap rooms and consoles. Rooms are relabelled only when the plan or as-built changes (construction step, refit), at ship-hulls' ~0.1 ms.
- **Changes ship-hulls.md "Rooms and pressurisation":** "a hole of ≥ 0.5 m merges a room with space" is dropped. A room with a hole keeps its identity and gets a `Breach` edge; whether it is effectively space is a matter of how fast it empties.
- An open hangar with no door and no barrier is not a room (it touches the margin). It has no air. ship-hulls' bay boxes still cover it for the dampener.
- Caps: **≤ 255 rooms and vessels per grid** (the `byte Room` in exotic-matter's `AntimatterTrap`, with 0xFF kept for "no room"), **≤ 1,024 edges** (design edges plus ≤ 256 damage openings). Bigger stations are several linked grids already.

### Edges

```csharp
public enum EdgeKind  : byte { Door, Duct, Valve, Breach, Leak, Barrier, Collar, Pump, Regulator }   // replaces ship-hulls' { Door, Vent, Breach, Leak }
public enum EdgeState : byte { Closed, Open, Held, Patched, Failed }                                 // Held: a barrier or emergency shield is up
```

| Kind | Between | Area | Mixing when open |
| --- | --- | --- | --- |
| `Door` (door, hatch, bulkhead) | room–room, room–exterior | portal cross-section (2 m² typical) | 75 L/s per m² |
| `Duct` (an `Air` route between two vent terminals) | room–room | duct bore (DN200 = 0.031 m²) | the fan's rating, 100–1,000 L/s, while powered |
| `Valve` (vent valve, equalising valve) | room–room, room–exterior | 50–500 cm² | none |
| `Breach` (damage opened ≥ one coarse cell) | any | measured from the bake (below) | 75 L/s per m² between rooms |
| `Leak` (sub-cell hole, cut, bad seam) | any | brush cross-section or seam area | none |
| `Barrier` | room–room, room–exterior | frame aperture | none while held |
| `Collar` | room on grid A – room on grid B | hatch cross-section | 75 L/s per m² |
| `Pump`, `Regulator` | room–vessel | forced, by rating | n/a |

Every edge has a design area `AreaMm2` (int32, mm²; 2,147 m² max) and an **effective area** `EffMm2` the solver uses. Closing a door sets it to 0; a barrier sets it to its seepage; a patch sets it to 0. All gameplay acts on `EffMm2` through events.

### Orifice law

For an edge between an upstream side `u` and a downstream side `d` (`p_u > p_d`), with `r = p_d / p_u`:

```
amount rate = C · A_eff · φ(r) · p_u            (Pa·m³/s; divide by p_u for the volume of upstream gas per second)

C    = 120 m/s      = Cd 0.6 × 199 m/s, the choked volume flux of 293 K air per m² of sharp-edged hole
φ(r) = 1                                   r ≤ 0.5     choked: flow does not care what is downstream
φ(r) = sqrt(1 − ((r − 0.5) / 0.5)²)        r > 0.5     subsonic (the ISO 6358 ellipse); 0 at r = 1
```

- A room venting to vacuum decays exponentially: `p(t) = p0 · exp(−t / τ)`, **`τ = V / (120 · A)`** with V in m³ and A in m².
- φ comes from a 33-entry Q16 table over `r`, linearly interpolated. No sqrt, no float.
- **Known error of the table:** in its last interval (`r > 31/32`) φ is linear in Δp where the true curve is a square root, so the final 3 kPa of an equalisation take about 0.45 τ instead of 0.18 τ. Accepted: it only stretches the tail, and it keeps pairs from chattering at the deadband. Blowdown to vacuum is always choked (φ = 1) and is not affected.

### Integration: implicit, clamped pair step

Stepped at **10 Hz** (`dt` = 6 ticks = 0.1 s). Per active edge, in fixed edge-index order, using the current (already updated) amounts:

```
S      = A_eff[mm²] · 12,000 · φ_Q16(r) >> 16          MICROLITRES of upstream gas swept through the hole this step (C · A · dt)
ΔQflow = Q_u · S / (V_u[µL] + S)                       implicit in the upstream amount: never takes more than is there
ΔQeq   = (Q_u · V_d − Q_d · V_u) / (V_u + V_d)         the transfer that makes the pair's pressures equal
ΔQ     = min(ΔQflow, ΔQeq)                             0 when Q_u = 0
ΔO2    = ΔQ · QO2_u / Q_u        ΔCO2 = ΔQ · QCO2_u / Q_u        (floor; the upstream room is well mixed)
ΔQ     = ΔO2 + ΔCO2 + min(ΔQ − ΔO2 − ΔCO2, Q_u − QO2_u − QCO2_u)   the inert part cannot exceed the inert present
u −= (ΔQ, ΔO2, ΔCO2)             d += (ΔQ, ΔO2, ΔCO2)
```

- **Why microlitres:** in whole litres a 1 cm² hole sweeps 1.2 L a step (17 % truncation) and anything under 0.8 cm² sweeps 0, so bottles and closets would never leak. `r = p_d · 65536 / p_u` is a plain int64 divide of the derived pressures.
- **Why the last line:** the two floors can leave `ΔO2 + ΔCO2` one or two units short of ΔQ; without the clamp a room holding only O2 and CO2 would be asked for inert gas it does not have and `QO2 + QCO2` would end above `Q`. The clamp trims ΔQ by at most 2 units; both sides still get the same integers.

- **Exterior as downstream:** `ΔQeq = Q_u − p_amb · V_u`, and what leaves is added to the grid's `VentedQ` counter. **Exterior as upstream** (planet air, a gas giant): `ΔQflow = p_amb · S`, `ΔQeq = p_amb · V_d − Q_d`, species by the ambient fractions.
- **Exact in mass:** the same integers are subtracted on one side and added on the other. `Σ Q + VentedQ + consumed − produced` is constant to the unit, forever (a test).
- **Cannot oscillate:** each pair exchange moves two pressures toward each other and never past their common equilibrium, so the highest pressure in the graph never rises and the lowest never falls (a discrete maximum principle). It holds for any `dt` and any area, so a 2,000 m² hole or a deferred step is as stable as a pinhole.
- **Accuracy:** the implicit form reads time constants long by `dt / 2τ`: 5 % at τ = 1 s, 0.5 % at 10 s. Faster than that, the room is empty in a few steps either way.
- **Small rooms in a chain (stable, but would be wrong without this):** a pair step cannot move more through a room than the room holds, so a small room between two big ones (a lock with both doors open, a collar vestibule) throttles the flow to one room-volume per step. A 200 m³ corridor draining to space through an open 4 m³ lock would read τ ≈ 6 s, where two 2 m² doors in series give 1.0 s; at a deferred 0.5 s step it would be 30 s. So a room whose open edges sweep more than its own volume in the step (`ΣS > V`) is **sub-stepped**: it and its edges run `n = min(8, ceil(ΣS / V))` times with `S / n`, in the same edge order. The lock case then reads τ ≈ 1.5 s. Smaller rooms or longer deferrals stay too slow, never unstable; conservation and the maximum principle are untouched. Sub-steps count against the global budget (section 10).
- **Intermediates** are `Int128` (ship-hulls already uses it). A pair step is about five 128-bit multiply-divides, so budget **~0.2 µs per edge** until measured (.NET's `Int128` divide is software; `Math.BigMul` plus a 128-by-64 divide helper is the obvious first optimisation).
- **Mixing** (composition only, at equal pressure, over open doors and powered ducts) uses the same shape. For each species amount X, with `M` = mix litres this step: `ΔX = (X_a · V_b − X_b · V_a) / (V_a + V_b) · m / (1 + m)`, `m = M · (V_a + V_b) / (V_a · V_b)`. Totals are untouched; the pair cannot overshoot its mixed state. ΔX is clamped so the receiving room keeps `QO2 + QCO2 ≤ Q` (the pair may differ by the 20 Pa deadband).
- **Forced edges:** a pump moves `Q_room · S_p / (V_room + S_p)` with `S_p = PumpLps · dt`, whatever the gradient, down to its floor pressure and up to the vessel's rating. A regulator moves `min(FillLps · 101,325 · dt, setpoint deficit, what the vessel's pressure allows)`.

### Sleeping, zones and events

- **Active set:** a room is active while any of its open edges has `|ΔQeq|` above the deadband (20 Pa across the pair), or an open mixing edge has a partial-pressure difference over 200 Pa (so two rooms with different air mix at the door's rate before they join a zone), or a pump or regulator is running on it. Only active rooms and their edges are stepped. When a room's edges are all inside the deadband, the pair amounts are redistributed by volume (exactly conserving) and the room sleeps.
- **Cut-offs:** a room below 50 Pa with an open exterior edge to vacuum snaps to 0 (remainder to `VentedQ`). A room whose exterior edge has `τ < 0.5 s` and which has reached ambient is flagged **`Exposed`**: its state *is* the ambient, read lazily, so a ship climbing with its ramp down costs nothing. Closing the edge snapshots ambient into the room.
- **Zones:** asleep rooms joined by open mixing edges form a zone (union-find over open `Door`, powered `Duct`, `Collar` and room-to-room `Breach`; rebuilt on door, fan and breach events only). A zone at rest is one well-mixed volume with **constant net rates** (crew breathing, scrubbers, O2 generation, regulator top-up, slow leaks) and a settle stamp. Inside a zone the door mixing rate is ignored: an open door means well mixed (accepted error: a crowd in one room shares its CO2 with the whole zone at once). Its state at any tick is linear in time, so it is integrated **lazily**: settled only when something changes it (an occupant enters or leaves the zone, a device switches, a door event). A **read** (console, vitals, crush check, replication) evaluates the line and does not settle or re-stamp. Settling distributes the zone's delta over its rooms by volume, largest remainder to the lowest room id, with the tick remainder carried.
- **One heap event per zone:** the earliest predicted threshold crossing (alarm band, device hysteresis, tank empty, shield exhaustion). All life-support devices are bang-bang with hysteresis so that rates stay constant between events.
- **Slow edges** (`τ ≥ 600 s` against the volume of the zone they drain: bad seams, pinholes, barrier and shield seepage) do not wake a room. They are a zone loss rate computed at settle, with a forced re-settle every `τ / 16`, clamped to 30–600 s (under 3 % error, mass still exact).
- **Rest cost: 0 per tick.** This replaces ship-hulls' "life support walks the room graph, ~1 µs per room per tick".

### Breaches from the bake

The bake of a damaged brick already evaluates hull brushes first and the damage layer last (ship-hulls bake step 3). The job snapshots the coarse air bitset **before** the damage layer (`AirDesign`, kept in a side pool only for bricks that hold damage brushes) and keeps the final `Air`.

1. **Opened cells** = `Air & ~AirDesign` in the touched bricks: cells the damage layer turned from solid to air.
2. **Cluster** them (6-neighbour flood fill over the opened cells only, union-find across the faces of touched bricks).
3. For each cluster, list the design regions (room ids, or Space) its cells share faces with. Two or more regions make an opening between each pair, kind `Breach`.
4. **Area** = the smaller of the two regions' shared-face counts with the cluster × 0.25 m². A hole through a one-cell wall gets its disc area; a tapered hole gets its narrow mouth. `Cd` covers the jagged edge.
5. **Position and outward normal** = centroid and mean face normal of the narrow mouth (for wind, vent thrust, shields and VFX).
6. **Sub-cell holes** (no whole coarse cell opened): ship-hulls' Leak rule stands. A damage brush whose volume touches cells of two regions makes a `Leak` edge of area `π r²` (sphere) or `2r · L` (a cutting-torch capsule, so the leak grows as the cut advances). A Leak edge is dropped when a Breach cluster swallows its brush.
7. **Room to room** works the same way: a hole in an interior wall is a `Breach` between two rooms.
8. **Coalescing:** more than 8 openings between the same pair merge smallest-into-nearest (areas add; flow is unchanged). At 256 damage openings per grid the same merge applies ship-wide. Only edges in the same state merge: a `Held` or `Patched` edge is never folded into an open one, so new holes cannot silently knock out a patch (they can still exhaust the projector's patch count).

- **Deterministic order:** bake jobs finish in any order. Their opening deltas are applied on the main thread sorted by (damage event tick, brick index, lowest cell of the cluster), and a new edge takes the lowest free index. Edge order, and with it the step result, never depends on worker timing.
- **Who owns an opened cell:** a cell the damage opened is in no design room. For membership and for "what air am I in" it takes the room its cluster shares the most faces with (lowest id on a tie), so someone standing in a hole in the wall is still in the grid and breathes that room. A cluster touching only Space is space. `GridVolume` gets the same cells.
- **`AirDesign` cost:** 512 B per brick that holds damage (the 16³ coarse bitset), from a pool. A refit of a damaged ship labels from `AirDesign` in those bricks.
- **Repair** removes damage brushes (section 6); the re-bake shows no opened cells there and the edge goes.
- **Cost budget:** bit ops over ≤ 8 bricks plus a fill over tens to hundreds of opened cells: **≤ 50 µs on the bake worker** per damage event, after the brick re-bake it rides on (0.4 ms per brick, ≤ 16 started per tick). The main thread applies an edge delta of ≤ 16 edges: **≤ 2 µs**. Room labels are not touched.
- **Cables and ducts** hit by the brush use ship-design's capsule test. A cut duct becomes a `Leak` from each end room to the region holding the cut.

### Hull split

Each piece becomes a new body with a fresh `ShipAir` built by the same algorithm on its own bake. The solid the bake discards (everything not connected to `KeepSeedCell`) counts as opened cells, so the missing half is one large cluster joining every room it bordered to Space: rooms along the cut blow down in a step or two, rooms away from the cut keep their gas, doors and edges. A room that straddles the cut exists on both pieces; its gas goes to the piece holding its seed cell (both are open to space anyway). Rooms wholly outside a piece's bounds are dropped. The fill over the discarded cells is ~1 ms on a worker, once per split. Debris pieces under 2 m³ get no `ShipAir`.

## 3. Decompression gameplay

### Time to empty

`τ = V / (120 · A)`. **The table lists τ** (pressure down to 37 %), not the time to zero. From τ: pressure passes **76 kPa** (ppO2 16, impairment) at **0.29 τ**, **48 kPa** (ppO2 10, collapse soon) at **0.76 τ**, **6.3 kPa** (vacuum for the body) at **2.8 τ**, 1 kPa at 4.6 τ.

| Room \ hole | 1 cm² (bullet, micrometeoroid) | 10 cm² | 100 cm² (11 cm, a torch cut) | 0.2 m² (0.5 m, smallest damage hole) | 2 m² (an open door) | 20 m² |
| --- | --- | --- | --- | --- | --- | --- |
| Cockpit, 10 m³ | 14 min | 83 s | 8.3 s | 0.4 s | instant | instant |
| Cabin, 50 m³ | 69 min | 6.9 min | 42 s | 2.1 s | 0.2 s | instant |
| Deck corridor, 200 m³ | 4.6 h | 28 min | 2.8 min | 8.3 s | 0.8 s | 0.1 s |
| Hold, 1,000 m³ | 23 h | 2.3 h | 14 min | 42 s | 4.2 s | 0.4 s |
| Hangar, 10,000 m³ | 9.6 d | 23 h | 2.3 h | 6.9 min | 42 s | 4.2 s |

Small holes are a maintenance problem, 10 cm holes are an emergency with time to act, anything a weapon makes empties a small room before a human can react. That is why bulkheads and emergency shields are automatic.

### Wind

Gas leaving a room drags what is in it. Per room with an outflow, each air step writes `WindQv = 120 · A_eff · φ` (m³/s through its largest outflow edge), that edge's position, and the room density `ρ = 1.2 · p / 101,325`.

```
v_air   = WindQv · max(1 / A_x, 1 / (2π d²))      toward the edge position; capped at 100 m/s; d = distance to the edge
v_rel   = v_air − v_body
k       = ½ · ρ · |v_rel| · CdA · dt / m
Δv      = v_rel · k / (1 + k)                       implicit drag: applied as F = m · Δv / dt
A_x     = room volume / its longest AABB extent     (a 2 × 3 × 30 m corridor: 6 m²), baked with the labels
CdA     = 0.7 m² for a character, else the body's smallest AABB face
```

For a body at rest this is `F = ½ · ρ · v_air² · CdA`, which is what the table shows. The drag is on the **relative** velocity and implicit, so nothing is ever blown faster than the air: with the plain `½ρv²` form a 1 kg panel with a 1 m² face would take 6,000 m/s² and leave the room as a projectile.

| Hole | Flow | Person in a 6 m² corridor | Person 1 m from the hole (open room) |
| --- | --- | --- | --- |
| 0.2 m² | 24 m³/s | 4 m/s, 7 N: a breeze | 6 N; 750 N at 0.3 m |
| 2 m² | 240 m³/s | 40 m/s, 670 N: off your feet | 600 N |
| 20 m² in a 400 m² hangar | 2,400 m³/s | 6 m/s, 15 N | 240 N at 4 m |

Honest physics: small holes do not suck people across rooms; door-sized ones in corridors do. The force dies with the room's density.

**How far a blowdown can throw someone (the bound on using it as a weapon):** the whole event gives a standing body at most `Δv = ½ · ρ0 · (CdA / m) · v_air · L`, with L the length of room upwind of it. For a person that is `0.005 · v_air · L` m/s: 6 m/s in a 30 m corridor through a 2 m² hole, 16 m/s at the 100 m/s cap, about 1 m/s out of a blown 4 m³ lock. Mag boots or a handle (800 N) hold against everything up to 44 m/s of air. It is a knock-down and a push through the hole, not a cannon.

**Attribution:** every air event with an actor (a door `Override`, the blow lever, a vent command, a barrier `Drop`, a shield `Inhibit`, a cut or shot that makes an opening) stores the actor on the edge event. Crew harmed or carried out by that edge's flow are attributed to the actor through the shared `IncidentRecord` (rings-and-asteroids.md), the same way an unrequested tractor is (D26). Whether venting boarders off your own ship is lawful is the law doc's call; the record exists either way.

**Applied per frames-and-zones' rule (scene vector + per-awake-body delta), never waking everything:**
- Scene gravity is untouched. Wind is a per-body force in the force pass (tick step 5), added with `EActivation::DontActivate`, only for **awake** bodies whose room is in the grid's `Windy` list (usually empty). ~30 ns per body.
- A room becomes `Windy` when `WindQv ≥ 10 m³/s`. On that event, once, the server wakes the bodies of that room within `sqrt(WindQv / (2π · 5 m/s))` of the edge, or the whole room if `WindQv / A_x ≥ 5 m/s`. Bodies elsewhere stay asleep.
- **Frozen contents** (a grid with no scene) follow the dampener's `WakeLeak` precedent: a scene is drawn only if a player is within 500 m and `WindQv / A_x ≥ 15 m/s`; otherwise loose cargo stays where it is.
- **Characters** take `F / m` as an external push in `CharacterVirtual`. Mag boots or a grabbed handle hold 800 N. A character carried through the opening leaves the `GridVolume` and transfers out by the normal membership rule with its velocity.
- Bodies do not plug holes (not modelled).

**Vent thrust (cheap and real):** an active exterior edge pushes the ship with `F = 0.75 · Δp · A_eff` along its inward normal, in the force pass, only while the ship has an awake body. A 30 t Wren with a 0.2 m² hole gets 15 kN (0.5 m/s²) for a couple of seconds: enough to tumble a small ship, nothing on a capital. ~10 ns per active exterior edge.

### Doors and bulkheads

| | Door | Bulkhead door |
| --- | --- | --- |
| Opens and closes | powered, 2 s; local button or Data | powered to open, 1 s; **closes on stored spring energy, no bus power needed** |
| Sensors | a mechanical Δp latch (no bus, no reading) | pressure sensor each side, and the latch |
| Auto-seal | no | closes when open and `|Δp| > 3 kPa` across it, or either side has fallen 3 kPa at more than 1 kPa/s. 0.2 s latency + 1 s travel (personnel size; larger doors travel at 0.5 m/s of their short side) |
| `Seal` Control input | none | **FailSafe** lockdown loop (bridge and engineering switches in series): an open or cut loop closes every door on it |
| Open | free while `|Δp| ≤ 5 kPa`; above it the latch needs `Override`; crank 5 s if unpowered | the same latch and `Override`; needs power, or an 8 s crank |
| Holds | 150 kPa | 500 kPa; above its rating a door blows and becomes a `Breach` |

- **Every door has the Δp latch.** No door, plain or bulkhead, interior or exterior, opens against more than 5 kPa on a button press or a single Data command. `Override` is a lever at the door held for 3 s (anyone physically there, audible on both sides), or a command from an authorised seat that sounds a 5 s klaxon at the door first. Both are `Logged`. This is the rule that stops one click, or one captured console, from emptying an occupied room through its doors.
- **Locks and access:** door, airlock and vent panels answer only characters on the ship's access list, the same filter ship-design applies to seats; there is no hacking mechanic in the docs, and one added later must not bypass the latch, the klaxon or the log. Exterior doors can be `Locked`: a locked door ignores its outside panel and its outside crank. From outside, a locked ship is entered by docking consent, a captured console, or the cutting torch (ship-hulls).
- The rate trigger needs a 3 kPa fall, so a lock equalising into the ship or a craft crossing a barrier does not slam every bulkhead (validation warns when `V_lock / V_zone > 2 %`).
- A body in the doorway delays a bulkhead by at most 1 s, then is pushed to the nearer side. Doors do no damage.
- **Wiring consequences (D17):** cutting a `Seal` loop locks a ship down (boarders can do it, so can the crew). Cutting a bulkhead's power does not stop it closing, only re-opening. A door on a dead Data bus still works from its own panel.
- **Ducts** carry pressure too. A vent terminal of class `Safety` has a spring damper (closes at 3 kPa); a plain grille does not.
- **Bulkheads are not enough alone.** A 50 m³ cabin with a 0.2 m² hole next to a 200 m³ corridor: the pair has lost 3 kPa after 0.3 s and the door is shut at 1.5 s with the corridor at 88 kPa. With a 2 m² hole the corridor is at about 30 kPa before the door shuts.

### Crew

Vitals run in the existing **1 Hz dose pass** (exotic-matter.md), next to radiation: ~0.1 µs per character. Breathing gas is the room's (helmet open) or the suit's (helmet closed). `Sat` is an integer 0–1000.

| Condition | Effect |
| --- | --- |
| ppO2 ≥ 16 kPa | `Sat` recovers +50/s |
| ppO2 10–16 kPa | `Sat` −5/s to a floor of 700: impaired (tunnel vision, half stamina) |
| ppO2 6–10 kPa | `Sat` −15/s: unconscious (< 400) in 40 s |
| ppO2 < 6 kPa | `Sat` −50/s: **unconscious in 12 s**; at 0, health −2 %/s (death about 70 s after exposure) |
| Body in < 6.3 kPa total, no suit | after 10 s health −1 %/s; over 30 s leaves an injury that needs a `MedBay` |
| ppCO2 > 1 / 3 / 6 / 10 kPa | console alarm / impaired / unconscious after 5 min / unconscious after 60 s, death 5 min later |
| Pressure falling faster than 20 kPa/s | 2 s stagger, no damage |
| Total > 500 kPa / > 1 MPa without a hard suit | impaired / health damage (deep gas giant inflow) |

Unconscious crew wake above `Sat` 600, so a rescuer with a bottle or a re-pressurised room saves them.

| Kit (data) | Air | Rated | Notes |
| --- | --- | --- | --- |
| None | room air only | none | |
| Flight suit (default crew wear) | 20 min bailout | vacuum | **helmet closes itself in 1 s** when ppO2 < 16 kPa, p < 60 kPa or the drop alarm fires |
| Emergency hood (wall locker, seats) | 10 min | vacuum | for anyone caught without a suit: 12 s to reach one |
| EVA suit | 4 h, refilled at a suit port from ship tanks | vacuum to 300 kPa | thrusters, mag boots, the suit σ exotic-matter already uses |
| Hard suit | 4 h | 3 MPa (about 180 km down a giant) | slow |

A suit is a small lazily integrated vessel on the character (one event: low, then empty). Suit tears are out of scope.

### Components and cargo

- **Components are vacuum-rated by default.** A data field `minAmbientKpa` (0 by default) takes a component offline below it: fabricators, med bays, galleys, hydroponics. No cooling model: nothing in the docs simulates heat inside a ship, and inventing one for this is not worth it.
- **Fire** is out of scope (no fire system exists).
- **Cargo:** an item flag `pressureSensitive` spoils stacks left 60 s in a room under 30 kPa (scheduled when the room crosses it, never polled). Loose cargo is moved by the wind rule.

## 4. Airlocks

An airlock is a **room pattern plus a controller**, not a special volume: any design room with an `AirlockController` (`fn.airlock`) in it. The catalogue "airlock" (ship-design's 60 cr line) is a hull-chunk prefab: a 2 × 2 × 2.5 m chamber, two airlock doors, the controller, a pump S and a vent valve.

- **Doors:** every portal of the room binds to the controller. Each airlock door has a Δp sensor and a hard-wired `Interlock` Control pair to the controller. Interlock pairs are **not** `SegLoopMask` loops (ship-design allows 64 FailSafe loops a ship, and a station has more lock doors than that): a dead pair only keeps a door locked, so its state is read from the cached binding when a door is asked to open. Only `Seal` loops (1–4 a ship) use the loop budget.
- **The rule: two doors that lead to different pressures are never open together.** A door unlocks only when `|Δp| < 2 kPa` across it (5 kPa to vacuum, so the last 5 % is let go) and every door on the other side is closed and latched. The interlock is Control wiring, so it works with the Data bus dead.
- **Cycle out:** close inner, pump the chamber into the receiver down to 5 kPa, open outer. **Cycle in:** close outer, fill from the receiver (then from O2 and N2 tanks at the setpoint mix), open inner.
- **Cycle time by volume and pump rating:** `t = (V / PumpLps) · ln(p0 / 5 kPa) = 3.0 · V / PumpLps`. Fill time is `V / FillLps`.

| Chamber | Pump S (250 L/s, 20 kW) | Pump M (1,000 L/s, 80 kW) | Pump L (4,000 L/s, 320 kW) |
| --- | --- | --- | --- |
| Personnel, 4 m³ | 48 s | 12 s | 3 s |
| Crew, 10 m³ | 2 min | 30 s | 7.5 s |
| Vehicle, 50 m³ | 10 min | 2.5 min | 38 s |

- **Vent cycle** (no pump, or in a hurry): open the vent valve to the exterior. 200 cm² empties 4 m³ to 5 kPa in 5 s and loses the air (4.8 kg).
- **Equalise valve** to the inner room instead of tanks: instant fill, at the cost of the ship's pressure dropping by `V_lock / V_ship`.
- **Emergency blow:** a mechanical lever on the outer door (no power, no wiring) opens it against pressure in 2 s. The chamber's air is lost and the wind rule pushes the occupants out at about 1 m/s. The inner door stays interlocked. The lever is inside the chamber only, and pulling it is `Logged`.
- **Manual override:** an unpowered door cranks in 8 s, but its mechanical latch still refuses above 5 kPa. So a dead airlock is slow, never unsafe; only the blow lever and a cutting torch defeat it. A `Locked` outer door has no outside crank, and the equalise valve's manual handle is on the ship side of the inner door, so a dead or unpowered lock is not a free way in.
- **Faults:** a dead interlock line holds both doors locked (`FailSafe`) and reports `Fault`. A full receiver stops the pump (`Fault`, vent cycle offered).
- **Warp:** exterior portals lock at spool (warp.md); a cycle in progress returns to "both closed".
- **EVA flow:** suit on (or let the helmet close itself) → panel inside the lock, outside it, or a console → 12–48 s → out. Suit ports beside the lock refill suits from ship tanks.

**Docking collars.** A collar is a hatch (an airlock-class door) plus a short vestibule. On lock (frames-and-zones: the child grid takes a fixed pose under the parent) the two vestibules join through a `Collar` edge held in a universe-level `AirLinks` table, and the child grid adopts the parent's ledger slot so both are stepped together (parent's edges, then the child's, then link edges in link-index order). The vestibule starts at vacuum; either side's equalise valve fills it; each hatch opens under the normal 2 kPa rule; then the two ships are one zone and share life support, as ship-design's collars already share power and control. Undocking closes both hatches first. A forced separation (tractor tear, damage) leaves each open hatch as an opening to space, which is what bulkheads are for.

**Designer validation of an airlock** (class Atmosphere, section 9):

| Check | Severity |
| --- | --- |
| The controller sits in a sealed design room | Error |
| Every portal of that room is an airlock-class door or collar hatch (no plain door, grille or barrier) | Error |
| Doors fall into at least two sides (different rooms, or a room and the exterior) | Error |
| Each door's `Interlock` pair is wired to the controller | Error |
| A pump with an `Air` route to a vessel, or a vent valve, is bound | Error if neither; Warning if vent-only ("loses the chamber's air every cycle") |
| Receiver volume ≥ chamber volume / 18 | Warning |
| Chamber clear space ≥ 1 × 1 × 2 m | Warning |
| Controller and pump on a powered bus; a Data path to a console | Error for power; Info for Data |

**Wiring needs:** Power (LV for the controller, HV for pump M and L), Data (remote cycle, status), Control (`Interlock` per door, FailSafe). **Changes ship-hulls.md:** `SocketType` gains `Air` (ducts, pump and regulator lines); ship-design routes gain medium `Air`.

## 5. Air barriers (D41)

A frame of emitters round an opening holds a thin sheet of confined, ionised particles across it (a plasma window). It stops gas and dust. It is **not gravity or exotic-matter technology**: the docs do not demand it, it burns no EM, and suppression fields and dampeners ignore it. It needs power, constantly.

- **Placement:** an `AirBarrier` component whose frame is a portal. Its cells count as closed for room labelling and `GridVolume`, so a hangar behind a barrier is a room and people in it are in the grid. One frame ≤ 64 m a side.
- **Collision:** for collision the frame is an always-open portal. C11's carve removes its swept volume from the filled exterior shape, and a room with a barrier frame over 8 m² is left **unfilled** (concave) like an open hangar, so craft can fly in. Without this, ship-hulls' "exterior = solid plus sealed rooms" would turn a barrier hangar into a solid block. (Changes ship-hulls' exterior fill rule.)
- **Holding:** edge state `Held`, effective area `A · ε`, `ε = 4e-7` (**seepage 0.05 L/s per m² at 1 atm**: a 2 m² door barrier loses 10 kg of air a day, an 800 m² hangar mouth 4 t a day, about 3 % of a 100,000 m³ hangar).
- **Power is what it holds with:**

```
P_request = A · (0.5 kW + 0.1 kW per kPa · min(Δp, Δp_rated))        per m²; Δp_rated = 110 kPa
Δp_hold   = min(Δp_rated, (P_granted / A − 0.5 kW) / 0.1 kW)          kPa
A_eff     = A · (ε + max(0, 1 − Δp_hold / Δp))
ΔQ       ≤ (Δp − Δp_hold) · V_u · V_d / (V_u + V_d)                  extra clamp in the pair step, above seepage (exterior: · V_u)
```

- **The clamp matters for small rooms.** `A_eff` is taken at the start of the step, so without it a doorway barrier on a 4 m³ room at half power would dump 76 % of the room in one step and stop at 24 kPa, not at the 48 kPa it can hold. With it a sagging barrier behaves as a relief valve set at `Δp_hold`, for any `dt`.
- **The request lags by one step** (it uses last step's Δp), so a Δp that jumps (a door opened behind an idling barrier) leaks for one 10 Hz step before the grant catches up. Accepted.

| Frame | Area | Power at 1 atm against vacuum | Idle (in atmosphere, Δp ≈ 0) | Cost |
| --- | --- | --- | --- | --- |
| Doorway 1 × 2 m | 2 m² | 21 kW | 1 kW | 1,300 cr |
| Bay 6 × 4 m | 24 m² | 255 kW | 12 kW | 10,000 cr |
| Hangar mouth 40 × 20 m | 800 m² | 8.5 MW | 400 kW | 320,000 cr |

- **Failure modes:**
  - **Power loss = immediate opening.** No internal storage (that is the emergency shield's job). Granted power under the idle term drops the sheet in the same 10 Hz step; the opening is then a plain hole of area A.
  - **Brown-out = leak.** With half the power a barrier holds 50 kPa: the room blows down to that and sits there, hypoxic. The formula is continuous, so there is no flicker.
  - **Over-pressure** (a gas giant, a neighbour pumped up): above `Δp_rated` the excess leaks the same way, inward or outward.
  - **Emitter damage:** Integrity under 0.5 drops it (the shared damage rule). A wormhole discharge that takes the emitter offline for 2–10 s opens it for that long.
- **Sniping the power is the intended counter, and it is bounded by design, not by a hidden buffer.** The emitter, its cable and its breaker are targets; a hit drops the sheet at once (D41: constant power). What the owner can do about it:
  - **two independent feeds** (validation Warning for a single feed on a frame ≥ 8 m², the anchors' two-feed precedent), concealed cable, armour over the emitter;
  - **the backing door on `BarrierDown`.** A personnel door shuts in 1 s. A big door travels at 0.5 m/s of its short side (bay 4 s, hangar mouth 40 s), and a 100,000 m³ hangar behind 800 m² empties with τ = 1 s, so a hangar that loses its barrier loses its air. That is the risk bought;
  - **a breach shield** catches a failed doorway or bay barrier as it would a hole (class L covers ≤ 25 m²). Nothing catches a hangar mouth;
  - **order inside power class 1:** grants go to breach-shield recharge and air barriers first, then life support, then plating, then dampeners. Today `PowerNet` orders classes only, and the dampener sits in class 1 (exotic-matter: an L draws up to 490 MW), so a tractor yank or a hard burn on trap-fed thrust would brown out every barrier on the bus. (Changes exotic-matter.md.)
- **Not a door against people.** A barrier, interior or exterior, stops gas only. Boarders walk through it as freely as the crew, so it never substitutes for a locked door; it is a hole in the ship's security that happens to hold air.
- **Passing through:** ships, people, cargo and tools cross freely in both directions; that is the point. Each crossing drags air: a one-off loss of `0.25 m × silhouette area` of room air (a person 0.18 m³; a 15 m craft about 10 m³, 12 kg). It is an event at crossing start, not a per-tick test (the portal trigger already exists for membership).
- **Not armour.** Projectiles, shrapnel, beams and missiles pass both ways untouched. Tractor beams pass (gravitons). It absorbs nothing, so D34 and rings-and-asteroids.md's "no ship shields" stand.
- **Heat:** no heat model; the draw is its waste heat. A raised barrier is a steady emitter of its draw for whatever sensors doc comes later, and it glows.
- **Power class 1 (life)** on the bus, with a `Drop` Data input and a `BarrierDown` Control output. The standard harness wires `BarrierDown` to the backing door's `Seal`.
- **Why advanced:** tier 2 parts (doorway, bay) and tier 3 (hangar); 20× the price of an airlock for a doorway; megawatts for a hangar, which only fusion or annihilation ships have; steady air loss; and total loss of the room on one power fault. A barrier **complements** a door, it does not replace it: validation warns about a barrier with no physical door behind it, and airlocks stay the cheap, safe default (D40).

## 6. Emergency shields (D42)

A `BreachShield` projector throws a containment patch over a hole that opens in a room it covers. It is the automatic first response; welding is the fix.

| Class | Covers rooms within | Patches | Largest hole | Capacitor | Recharge from the bus | Response | Cost |
| --- | --- | --- | --- | --- | --- | --- | --- |
| S | 15 m | 4 | 0.5 m² | 5 MJ | 5 kW | 0.5 s | 800 cr |
| M | 40 m | 8 | 4 m² | 50 MJ | 50 kW | 0.3 s | 6,000 cr |
| L | 100 m | 16 | 25 m² | 500 MJ | 500 kW | 0.2 s | 45,000 cr |

- **Coverage** is baked like the dampener's: the rooms whose every air cell is inside the radius, recomputed with the labels.
- **Detection** is pressure, not magic: a covered room that drops faster than **0.2 kPa/s** with a `Breach`, `Leak`, blown door or failed barrier edge of its own. The server knows the edge at once; the class's response time is the design latency. Pinholes under the rate never trigger it: those are found by the leak view and patched by hand.
- **Deploy:** the edge goes `Held` at `A · 1e-3` seepage. Holes larger than the class limit are not covered at all. Out of patches: priority to rooms with crew in them, then by loss rate.
- **Drain:** `30 kW per m² at 1 atm` (0.3 kW per m² per kPa of Δp), from the projector's **own capacitor**, which the bus recharges at power class 1. So a hole under `recharge / 30 kW` (S 0.17 m², M 1.7 m², L 17 m²) does not drain the capacitor while the bus lives, and a bigger one buys minutes: S holds a 0.5 m² hole 8 min on the bus and 5.6 min without; M holds 4 m² for 12 min; L holds 25 m² for 30 min (33 by energy; see the cap below). With the bus dead every class still holds its sustainable hole for 17 min (capacitor ÷ recharge rate = 1,000 s for all three).
- **It is an emergency device: `MaxHoldS` = 30 min per patch** (knob). A patch that reaches it drops with the same alarms and then cools for as long as it was held before it can cover that hole again. Without the cap an L unit (45,000 cr, 500 kW) would hold a 17 m² hole for ever and D39 would stop mattering on any ship that could afford one. (Open question 6.)
- **Exhausted:** alarms at 30 s and 10 s (scheduled from the drain rate, no polling), then the patch drops and the hole is a hole. The time bought is for sealing bulkheads, suiting up and patching.
- **No flicker after a drop:** the dropped edge is not covered again until it is patched, welded or raised by hand, and the projector deploys nothing while its capacitor is under 50 %. Otherwise a hole over the sustainable size would be held on a deploy, drain, drop, recharge cycle at a duty of `recharge / drain` for ever, with an edge message and a zone rebuild every cycle.
- **Cannot be wasted from outside:** it deploys only on `Breach`, `Leak`, blown-door and failed-barrier edges, never on a valve or a commanded vent, and never on a hole bigger than its class, so cycling valves or doors does not spend patches or charge.
- **Released** when Δp across the patch falls under 1 kPa (the room was sealed off and emptied, or repaired).
- **Bodies pass** exactly as through a barrier. Crew can step out to repair from outside; boarders can step in through the cut they made.
- **Wiring:** Power, Data (status, room priorities, manual raise and drop), Control `Inhibit` (FailOff: a dead line does not inhibit). Deliberate venting inhibits the shield for that room.
- **Relation to station shields (D34):** none. `StationShield` absorbs the energy of debris and shrapnel at a shell round the station and ignores gas, ships and people. A breach shield holds gas at the hull and absorbs nothing. A station can fit both; they share no pool.

**Permanent repair, in order of effort:**

| Fix | What it does | Limits |
| --- | --- | --- |
| **Patch kit** (hand tool, 5 s) | sets one `Leak` or `Breach` edge to `Patched` (area 0). An edge state, no re-bake | hole ≤ 0.25 m²; holds 150 kPa; lost to any damage brush within 2 m |
| **Weld repair** (construction) | a repair step per damaged piece, compiled from the damage brushes on it like a `Skin` step: plate stock by removed volume, weld joints with quality. Completing it removes those damage brushes, re-bakes, and the opened cells and their edges go | needs stock and a welder; can be done from inside a held or patched room |
| **`Test(room leak)`** | re-certifies the room (section 9) | 30 s |

A poor repair weld leaves a seam `Leak` of its own, by the same quality rule as new construction.

## 7. Life support

**Metabolism (data):** real figures are 0.84 kg O2 in and 1.0 kg CO2 out per person per day. The game multiplies them by **`metabolicScale` = 10** (open question 1), because sessions last hours and real rates never threaten anyone: at ×10 two people in a dead 10 m³ cockpit reach 3 kPa CO2 in 39 min, four in a 50 m³ cabin in 1.6 h. Per crew: 8.4 kg O2 a day (7,400 units/s), 10 kg CO2 a day.

| Function | Rule |
| --- | --- |
| **CO2 scrubbing** | removes CO2 from its zone at its rated rate while ppCO2 > 0.4 kPa (off under 0.2). Regenerative: power only. A cartridge variant for pods and small craft holds a fixed mass |
| **O2 generation** | electrolysis of `ice.water` (1.13 kg per kg O2) while ppO2 < 20.5 kPa (off at 21.5), or a regulator from O2 vessels |
| **Pressure regulation** | tops a room up toward its setpoint from the receiver first, then N2 and O2 vessels at the setpoint mix, at `FillLps`. Never feeds a room flagged leaking faster than it can fill unless told to |
| **Circulation** | fans on `Air` ducts: the mixing rate of `Duct` edges. A room with no open path to a scrubber is its own zone and goes stale on its occupants' schedule |
| **Reclaim** | airlock pumps and the room **pump-down** command move air into the receiver instead of space |
| **Intake** | an `AirCompressor` fills vessels from a breathable ambient (free air on the right planet). Inert gas otherwise comes from stations or gas-giant scooping |

| `fn.life_support` (catalogue) | Crew | Fan / fill | Power at rated crew | Cost |
| --- | --- | --- | --- | --- |
| S | 4 | 200 / 100 L/s | 8 kW | 150 cr (ship-design's Wren line) |
| M | 16 | 800 / 500 L/s | 30 kW | 500 cr |
| L | 64 | 3,200 / 2,500 L/s | 110 kW | 1,800 cr |

- **Power class 1 (life)**, as exotic-matter.md already lists. On a shortfall the units go off and the zone's rates change: an event, then the lazy clock runs.
- **Re-pressurising after a repair:** `t = V / FillLps` and 1.2 kg of gas per m³. A 50 m³ cabin takes 8 min on an S unit, 100 s on an M; a 1,000 m³ hold takes 33 min on an M and 1.2 t of air (0.92 t of it inert). **Lost inert gas is the real cost of a breach.**
- **Venting a room on purpose** (boarders, contamination, bad air): the console command closes the room's bulkheads, inhibits its breach shield and opens its vent valves or exterior door. Times are the table in section 3. Pump-down does the same into the receiver, slowly, and keeps the air.
  - It is a Data command from an **authorised seat** only, `Logged`, and it sounds a **5 s klaxon** in the room before anything opens (flight-suit helmets close on it). It opens only valves and exterior doors bound to that room, never an interior door.
  - Occupants are not helpless: suit air, the 3 s `Override` lever on any door out, and whatever the designer wired (D17): a `VentInhibit` key switch on a Control line blocks remote venting of that room whatever the Data bus says.
  - So whoever holds an engineering console can clear rooms on a 5 s warning and a suit timer, attackers included. That is the price of losing the console, not a one-click kill.
- **Sensors:** doors, life-support terminals, airlock controllers and consoles each carry a room sensor. An engineering console shows a room only if a Data path reaches one of its sensors; cut the bus and the room reads "no data". Bulkheads use their own sensors and need no bus.
- **Boarding:**
  - a cutting torch (ship-hulls) makes a growing `Leak` as it cuts, so a boarding cut is announced by the drop alarm and may be covered by a breach shield, through which the boarders then walk;
  - defenders vent rooms, drop barriers and trip the `Seal` loop; attackers cut the loop, inhibit shields and cut life-support feeds. Everyone in a flight suit has 20 minutes, so venting clears a room of people on a timer, not instantly;
  - an airlock can be commanded from a console the boarder controls, or cranked from inside, never forced past its latch; a `Locked` outer door does nothing for a stranger outside it;
  - barriers and breach-shield patches let bodies through, so neither is a door: a ship that relies on a barrier for its hangar has an open hangar as far as boarders are concerned;
  - D27 capture: the `CommandCore` room is contested by presence in its `GridVolume`. Venting it puts both sides on suit air; nothing else changes.

## 8. Interactions

| With | Rule |
| --- | --- |
| **Warp** (warp.md, "sealed in warp") | the seal covers gas too: from T0 to T1 + 1 **every exterior edge carries zero flow**, breaches included; interior edges run normally. A holed ship keeps its air in cruise and starts venting on the first real-space tick. Barriers idle at their base draw. On a hard cancel or a lost brake the field lets the openings go (warp.md), and exterior edges flow from that tick. One scheduled event at the end of the seal re-arms the exterior edges; nothing is polled in cruise, and a ship whose only imbalance was to the outside sleeps through it. (open question 2) |
| **Wormhole tunnels** | ambient is vacuum. Wall and storm discharges are ordinary damage brushes, so ordinary breaches. A discharge that takes a barrier emitter or life-support unit offline has the effects above |
| **Gas giant, crush** (planets.md) | the crush test uses the **difference**: a room implodes when `p_amb − p_room > p_crush`. (Changes planets.md, which compared ambient alone; at 1 atm inside the gap is 1 bar.) An imploded room gets its damage brush, a `Breach`, and fills with ambient gas in a step: `Exposed`, at ambient temperature and pressure, as planets.md says, lethal. planets.md's 1 Hz check reads `p_room` without settling the zone. **Consequence, accepted:** a room let up to ambient (a valve opened on purpose, or already `Exposed`) cannot be crushed, so a flooded ship's depth limit is its components' ratings (planets.md: disabled at rating, destroyed at 1.5×) and its crew's suits (hard suit 3 MPa), not its rooms. The crew pay with the > 500 kPa vitals row and a full purge and refill afterwards. On the way up the overpressure is outward: doors blow at 150 / 500 kPa and relieve it; the shell is not burst-tested |
| **Gas giant, inward leak** | any hole below the datum flows inward by the same law. Pressure rises and O2 is diluted, so the first symptom of a small leak is hypoxia at normal pressure: the O2 alarm, not the pressure alarm. Barriers saturate above 110 kPa of difference; doors hold to their rating |
| **Breathable planets** | open the doors: rooms with a wide exterior edge are `Exposed`, cost nothing, and crew breathe the planet. Altitude matters (37 % of surface pressure at one scale height; half at 0.69 H). Compressors refill tanks |
| **Unbreathable planets** | an open door lets the outside in. Scrubbers cannot clear a room of 95 % CO2 in useful time; you cycle an airlock or vent and refill. This is why D40 matters on the ground too |
| **Radiation and contamination** (D24) | records stay per room and are **not carried by flow**. One event rule: a room holding a `BreachFallout` record that is vented to under 5 kPa loses 30 % of that record's activity, once per record (a flag bit on `Contamination`). `HullActivation` is in the metal and ignores air |
| **Cargo** | section 3. Holds can be designed as vacuum rooms (`SetpointPa` 0): no alarms, no life support load |
| **Plating and dampener** | none. Gas has no weight or buoyancy here; coverage lists and `m_in` are unchanged |
| **Core ejection** | the blow-out panel leaves a `Breach` if the bay was pressurised (ship-design already warns). Core bays should be vacuum rooms |
| **Escape pods** | a pod is its own grid with its own `ShipAir`, a cartridge scrubber and an O2 bottle (crew × 24 h). The bay stays a sealed room until launch (ship-hulls); launching vents the tube unless the tube is a vacuum room (validation Info) |
| **Station anchors** | stations use the same `ShipAir`, per linked grid, joined by collars. Vent thrust on an anchored grid is debited to the anchor like any contact impulse |
| **Tractor tears and hull stress** (tractor-beams.md) | a torn part is a split or damage: breaches follow by the normal path. Gas pressure adds no load to the stress graph (only door ratings and the giant crush test read Δp) |
| **Splitting ships** | section 2 |
| **`GridVolume` membership** | **unchanged. Pressure never defines the grid.** A character in an emptied or `Exposed` room is still in the ship's grid; a hangar behind a barrier is inside it; an open hangar is covered by its bay box as before |
| **Power** (`PowerNet` classes) | 1 life: life support, air barriers, breach-shield recharge. 2 flight: airlock and pump-down pumps, compressors. Bulkhead springs and the blow lever need none |

## 9. Designer support

- **Air view** (designer and in-game overlay): rooms coloured by zone, setpoint or leak rate; anything that is not a room hatched as "space"; portals, ducts, dampers, barrier frames, breach-shield coverage, sensor reach, life-support capacity against berths per zone.
- **"Why is this not sealed?"** Click a point: the tool draws the shortest path of air cells from there to the bounds margin (BFS over the coarse grid). This is the one tool that makes D11 mesh hulls buildable airtight.
- **Breach sim:** place a hole of a chosen size on any wall and play. It runs the real `ShipAir` step headlessly (`src/Ships`, no Vulkan): per-room pressure curves, which bulkheads close and when, which shield catches it and for how long, time to each crew threshold. Also "fail this barrier" and "cut this wire".
- **Leak test (construction, `Test(room leak)`, dependency 7 in ship-design):** pressurise from tanks to 110 kPa, isolate, hold 30 s, read the decay. It reports the summed seam area and the predicted loss in kg/day and passes under 1 % of the room's air a day. Until a room passes, the regulator fills it only on a manual command.
- **Seam leaks from joint quality** (ship-design owns the rule: seal joints under `leakSeal` 0.45 leak, area `seam length × (0.45 − q) × 2 mm`). The threshold stays. The width does not survive this doc's flow law: 10 m of q 0.3 seam would be 30 cm² and empty a 50 m³ cabin with τ = 2.3 min, a hull breach from one mediocre bead. **Proposed change to ship-design:** `a = L_seam · 0.02 mm · max(0, 0.45 − q) / 0.45`. Then 10 m of q 0.3 seam is 0.67 cm² and the cabin's τ is 104 min: a maintenance problem, found by the leak test.

**Validation class "Atmosphere"** (added to ship-design's table):

| Check | Severity |
| --- | --- |
| > 255 rooms and vessels on one grid; > 768 design edges | Error |
| An exterior barrier frame ≥ 8 m² on a single power feed | Warning |
| A lock whose equalise valve opens into a zone under 50 × its volume ("equalising trips the bulkheads") | Warning |
| A room under 2 m³ with two or more doors ("flow through it is throttled", section 2) | Info |
| Airlock rules (section 4) | Error / Warning as listed |
| A component with `minAmbientKpa` > 0 placed outside any room, or in a vacuum room | Error |
| A seat or berth outside any pressurised room | Warning |
| A pressurised room with no open or ducted path to a life-support unit | Warning |
| Life-support crew rating per zone < berths and seats in it | Warning |
| Ship with no airlock ("opening the exterior door empties the room behind it") | Warning |
| An exterior door or barrier whose room has no bulkhead between it and the rest of the ship | Warning |
| A barrier with no physical door backing it; `BarrierDown` unwired | Warning |
| Bus cannot carry every barrier at 1 atm plus life support (power budget) | Warning |
| A duct through a bulkhead line without safety dampers | Warning |
| No `Seal` loop reaching the bridge | Warning |
| A crewed room outside every breach-shield radius; no hood locker within 10 m of a berth; O2 and inert reserve in hours; pod tube pressurised | Info |

**Costs** (catalogue, placeholders): door 20, bulkhead door 90, vent valve 30, airlock prefab 60 (pump S, as costed), pump M 250, pump L 900, bottle S 15, tank M 120, tank L 800, compressor 200, life support 150 / 500 / 1,800, breach shield 800 / 6,000 / 45,000, air barrier 500 + 400 per m².

## 10. Multiplayer and performance

### Authority and replication

The server owns all of it. Clients hold the last replicated room states and compute only presentation (wind particles, plumes, audio) and the prediction of the wind force on their own character (same formula in float, corrected by the normal reconcile).

| Message | To | When | Size |
| --- | --- | --- | --- |
| `RoomAir {grid, room, pPa/16 u16, o2 u8, co2 u8, flags u8}` | occupants of the grid: their room and its edge neighbours | 5 Hz while active; on wake, sleep and each alarm-band change otherwise (asleep states carry their rates, so the client extrapolates) | 8 B per room |
| same, whole ship | players at an engineering console, rooms with sensor reach only | 2 Hz, changed rooms only | 8 B per changed room |
| `AirEdge {grid, edge, kind, state, a, b, areaCm2, pos, normal}` | grid interest | on create, state change, remove (breach events, doors, barriers, patches) | 20 B |
| `SuitAir {character, …}` | owner | on band change | 8 B |

`pPa/16` in a u16 tops out at 1.05 MPa; a room above that (deep gas-giant inflow) sets a flag bit and sends `pPa/1024`.

Bandwidth: an occupant of a ship with 6 rooms decompressing gets ~240 B/s; a console watching 32 active rooms ~512 B/s; a quiet ship sends nothing.

### Per-tick cost bounds

| Item | Cost (estimate) | When |
| --- | --- | --- |
| Sealed or `Exposed` ship at rest | **0** | always |
| Zone event (settle, redistribute, reschedule) | ~0.3 µs + 20 ns per room in the zone | occupant room change, device switch, door, threshold |
| Active step | ~0.2 µs per active edge (five 128-bit multiply-divides; to measure) + 10 ns per active room, at 10 Hz in the grid's ledger slot, right after `PowerNet.Solve` (so pumps and barriers use this step's grants). A sub-stepped small room multiplies its own edges by ≤ 8 | rooms out of equilibrium only |
| Worst ship (255 rooms, 1,024 edges, all active) | ~0.2 ms per step, 35 µs a tick amortised | a capital ship torn open |
| **Global cap** | **0.15 ms a tick** for air steps over all grids, enforced as a **count: 750 edge-steps a tick** (retuned from the measured ns per edge), never as measured time. Grids past the count are deferred in grid-id round robin and step with the accumulated `dt`: the scheme is stable for any `dt` and still exact in mass. Because the deferral depends only on active-edge counts, the same battle replays to the same gas state | battles |
| Opening detection | ≤ 50 µs on the bake worker, ≤ 2 µs main thread | per damage event |
| Room labelling | ship-hulls' ~0.1 ms | plan or as-built change only |
| Wind | ~30 ns per awake body in a `Windy` room; one-shot wake on the event | force pass |
| Vent thrust | ~10 ns per active exterior edge | force pass |
| Vitals | ~0.1 µs per character **in the pass**. A character breathing green-band gas with `Sat` full is not in it; a band change (zone alarm band, helmet, zone change) enrols them | 1 Hz dose pass |
| Barrier and shield power | one request each in the 10 Hz ledger | while raised |

No allocation after the bake: arrays are sized at labelling (design counts + 256 edges of damage headroom), the event heap is preallocated at one slot per zone.

### C# (`src/Ships/Air`, no Vulkan, no Jolt)

```csharp
namespace BrutalGame.Ships.Air;

[Flags] public enum RoomFlags : byte { None = 0, Vessel = 1, Exposed = 2, Windy = 4, VacuumByDesign = 8, Certified = 16, Active = 32 }

public struct Ambient { public int Pa; public ushort O2Q16, CO2Q16; public short TempK; public AmbientKind Kind; }

public sealed class ShipAir                          // SoA per grid, preallocated at labelling, like PowerNet
{
    // rooms and vessels [0, RoomCount)
    public int RoomCount;
    public int[] VolumeL;  public long[] Q, QO2, QCO2;
    public int[] SetpointPa, RatedPa;  public RoomFlags[] Flags;  public ushort[] Zone;
    public float[] CrossSectionM2;  public int[] SeedCell;         // SeedCell: stable id across re-bakes
    // edges [0, EdgeCount); B = 0xFFFF is the exterior
    public int EdgeCount;
    public ushort[] EdgeA, EdgeB;  public EdgeKind[] Kind;  public EdgeState[] State;
    public int[] AreaMm2, EffMm2, MixLps;  public uint[] Source;   // component id, damage cluster id or seam id
    public short[] PosX, PosY, PosZ;  public ushort[] NormalOct;   // grid-local, 1/8 m
    public int[] RoomFirstEdge;  public ushort[] RoomEdges;        // CSR adjacency
    // active sets
    public NativeList<ushort> ActiveEdges, WindyRooms;  public ulong[] ActiveRoomBits;
    // zones at rest
    public int ZoneCount;
    public long[] ZoneRateQ, ZoneRateO2, ZoneRateCO2, ZoneRemQ, ZoneRemO2, ZoneRemCO2, ZoneT0, ZoneEventTick;   // rates in Pa·L per second
    public long VentedQ, ConsumedO2, ProducedCO2;                  // conservation audit
    public Ambient Ambient;

    public void Step(int dtTicks);                                  // active set only
    public void Settle(int zone, long tick);
    public int  PressurePa(int room);                               // settles lazily
}

public struct AirLink { public int GridA, GridB; public ushort EdgeA, EdgeB; }   // collars; universe-level table, ≤ 256
```

### Data, files, tests

- **Data:** `data/atmosphere/air.json` (C, critical ratio, deadbands, cut-offs, mix rates, vitals bands, `metabolicScale`, wind knobs), `data/components/air/*.json` (doors, bulkheads, valves, pumps, vessels, compressors, life support, barriers, breach shields, sensors, hood lockers), `data/items/suits.json`, atmosphere fields in the body data.
- **`src/Ships/Air/`:** `ShipAir.cs`, `AirStep.cs` (pair step, φ table), `AirZones.cs` (union-find, settle, events), `Openings.cs` (opened-cell clusters, areas), `RoomLabels.cs` (shared with `GridVolume`), `AirAmbient.cs`.
- **`src/Sim/Systems/`:** `AirSystem.cs` (ledger hook, global cap, links), `Airlocks.cs`, `Bulkheads.cs`, `AirBarriers.cs`, `BreachShields.cs`, `LifeSupport.cs`, `AirWind.cs` (force-pass hook, vent thrust), `Vitals.cs` (in the dose pass), `Suits.cs`.
- **`src/Plans/`:** `Families/Airlock.cs`, `Families/AirBarrier.cs`, `Families/BreachShield.cs`, `Families/LifeSupport.cs`, `Validation/Atmosphere.cs`.
- **`src/Designer/Tools/`:** `AirView.cs`, `SealPath.cs`, `BreachSim.cs`; `src/Designer.Ui/AirPanel.cs`. **`src/Net/AirEvents.cs`.** **`src/Debug/`:** profiler rows Air step, Air zones, Openings, Wind, Vitals; an ImGui room table.
- **`tests/Ships.Tests` (Air):**
  - conservation: the audit sum is constant to the unit over 1e6 random steps, door events and breaches;
  - maximum principle: with no sources the highest pressure never rises and the lowest never falls; a two-room pair never swaps sign;
  - blowdown against `exp(−t/τ)`: within `dt/2τ` for τ from 1 s to 1 h; the time table reproduces;
  - step-size independence: 0.1 s and 0.5 s steps agree within the stated error and both conserve;
  - species: `0 ≤ QO2`, `0 ≤ QCO2`, `QO2 + QCO2 ≤ Q` in every room after every step, including pure-O2 vessels and rooms emptied to 0;
  - small holes: a 0.1 cm² hole in a 50 L bottle leaks at the analytic rate (the microlitre sweep is not truncated to 0);
  - series: corridor, open 4 m³ lock, space drains within 50 % of the two-orifice time with sub-stepping; the same case at a 0.5 s deferred step is still stable and conserving;
  - determinism: the same damage events applied with the bake jobs finishing in shuffled order give bit-identical `ShipAir`; the same scene under a tightened edge-step budget replays identically;
  - wind: no body's speed toward a hole ever exceeds `v_air`; a 1 kg, 1 m² panel is carried, not launched;
  - rest: a sealed ship reports 0 active rooms and 0 steps; lazy settle equals brute-force stepping of the same rates;
  - openings: a hole of known size between two rooms, and to space, gives an edge within one cell face of the analytic area; a sub-cell brush gives a Leak; repair removes it; a split exposes exactly the cut rooms;
  - airlock: no fuzzed command and fault sequence ever has two sides open; cycle times match the table;
  - doors: no sequence of button presses and single Data commands opens a door across more than 5 kPa; `Override` always logs its actor;
  - barrier: half power holds half the pressure, on a 4 m³ room as well as a hangar (the hold clamp); zero power is a hole; crossing loss is charged once; a dampener at full draw on the same bus does not sag it (class-1 order);
  - breach shield: exhaustion fires at the predicted tick; sustainable holes hold to `MaxHoldS` and no longer; a dropped patch does not redeploy on its own;
  - vitals: 12 s to unconsciousness in vacuum; CO2 timeline at `metabolicScale`.

### Milestones (after M8's rooms)

| | Scope | Exit check |
| --- | --- | --- |
| **A1** (with M8 rooms) | `RoomLabels` shared with `GridVolume`, `ShipAir`, doors, pair step, vacuum ambient, openings from the bake, `RoomAir` and `AirEdge` replication, vitals, flight-suit helmet | the time table reproduces in game within 6 %; a sealed ship shows 0 in the Air rows; conservation test green |
| **A2** | zones and lazy rates, vessels, life support, regulators, airlocks, vent valves, bulkheads and the `Seal` loop, wind and vent thrust | a Wren cycles its lock in 48 s and reclaims 95 % of the air; a 2 m² corridor breach knocks a player down and the bulkhead leaves the next room at the predicted pressure; wind costs nothing with no breach |
| **A3** (with ship-design M8 seal joints) | seam leaks, `Test(room leak)`, patch kit, weld repair, Air view, seal-path tool, breach sim, validation class | a q 0.3 seam leaks at the predicted area (ship-design's own exit check); the breach sim's curve equals the live ship's |
| **A4** | air barriers, breach shields, collar links | a hangar barrier at half power settles at half pressure; an M shield holds a 4 m² hole for its 12 min and a 1.5 m² hole to `MaxHoldS`; two docked ships equalise through the collar and separate cleanly |
| **A5** (with planets' atmosphere and gas giants) | planet and giant ambient, `Exposed` rooms, compressor, differential crush | doors open on a breathable world cost 0; a leak below the datum raises the O2 alarm first; a room implodes at `p_amb − p_room = p_crush` |

## Changes to other docs

- **ship-hulls.md:** `Room`, `RoomEdge`, `EdgeKind` replaced by `ShipAir` (section 10). Rooms are design rooms and never merge with space at runtime; openings come from `Air & ~AirDesign` (one extra bitset on damaged bricks only). Rooms and `GridVolume` share one labelling. `SocketType` gains `Air`. The "life support ~1 µs per room per tick" line becomes 0 at rest. Limits table gains ≤ 255 rooms and ≤ 1,024 edges per grid. The exterior collision fill skips rooms with a barrier frame over 8 m², and barrier frames are carved as open portals. Cells opened by damage belong to the adjacent room for `GridVolume`.
- **ship-design-and-construction.md:** families `fn.airlock` (controller), bulkhead door, vent valve, pump, vessel, compressor, `fn.air_barrier`, `fn.breach_shield`, with the ports above; route medium `Air`; validation class Atmosphere; `Test(room leak)` defined; a weld-repair step compiled from damage brushes; the seam leak width 2 mm → 0.02 mm scaled by `(0.45 − q) / 0.45` (threshold unchanged; theirs to own); doors gain the Δp latch, `Locked` and a `Logged` `Override`; door, lock and vent panels use the seat access filter; airlock `Interlock` pairs are read on demand and are not `SegLoopMask` loops.
- **exotic-matter.md:** `PowerClass` assignments above; **a fixed sub-order inside class 1** (breach-shield recharge and barriers, life support, plating, dampeners); one flag bit on `Contamination` for the vent rule; vitals join the 1 Hz dose pass.
- **planets.md:** crush test on `p_amb − p_room`; `AtmosphereDef` composition fields; an imploded room is `Exposed`.
- **warp.md:** the warp seal also stops gas at exterior edges.
- **frames-and-zones.md:** the force pass gains the wind and vent-thrust terms (awake bodies in `Windy` rooms only); the air step runs in the 10 Hz ledger slot.
- **rings-and-asteroids.md:** a note that breach shields are not ship shields and share nothing with `StationShield`; `IncidentRecord` gains the air-event kinds (vent, override, blow, barrier drop, shield inhibit).
- **README.md:** "Missing systems" gains character vitals and suits.

## Open questions

1. **`metabolicScale` 10×.** Real breathing rates make a dead life-support unit a problem measured in days. Is ten times real (cockpit stale in ~40 min) the right pace, or should it be real with life support mattering only as a consumable?
2. **Air in warp.** The doc holds air in with everything else while the warp seal is up, so warp is a short refuge for a holed ship. Keep that, or let breaches vent in cruise?
3. **Default suit.** With flight suits worn by default, decompression is a 20-minute timer and only the unprepared die in 12 s. Is that the intended lethality, or should crews usually be in shirt sleeves?
4. **Water.** A hull opened below the sea surface exchanges nothing here. Is flooding with sea water wanted at all, given water is cut back to shore wash?
5. **Do emergency shields stop bodies?** The doc lets people and objects pass (so they also admit boarders). The alternative is a patch that is solid to bodies, which keeps crew aboard and makes a shielded cut a dead end for boarders. (Review: keep "pass". A solid patch is a free, self-deploying door against boarders.)
6. **How long may an emergency shield hold?** The review capped a patch at `MaxHoldS` = 30 min with an equal cool-down, so "emergency" stays a timer and a breach always has to be welded. The first draft let a hole under the sustainable size be held for as long as the bus lived. Which is intended?
7. **How big is a "small particle shield"?** D41 says small. Doorway (2 m²) and bay (24 m²) frames clearly fit. The doc also offers an 800 m² hangar mouth at tier 3 and 8.5 MW. Keep it, or cap barrier frames at bay size and leave hangars to doors?
8. **Barrier ride-through.** As written a barrier has no storage: one cut cable drops it that tick (D41: constant power). A 1–2 s hold-up capacitor would let a backing personnel door close first without changing "needs constant power". Wanted, or is the instant drop the point?

## Review notes

Lead review, 2026-11-26. Checked against decisions.md, ship-hulls.md, ship-design-and-construction.md, exotic-matter.md, frames-and-zones.md, planets.md, warp.md, rings-and-asteroids.md and review.md (C11). Fixes are in the text above; this is the list. Nothing was built or measured: every cost is still an estimate.

**The five decisions.** D38–D42 are all present and match the user's wording (they were already in decisions.md; the header said "proposed"). D39's "potentially" holds: only an opening leaks, and a shield or patch can stop it. D40: airlocks are needed by the physics, not by a rule (a ship without one gets a Warning, since a cockpit-only craft is legal). D41: gas only, constant power, no storage, advanced tiers. D42: optional component. Two places go past the wording and are now open questions rather than silent choices: an 800 m² hangar barrier against "small" (question 7), and how long an "emergency" shield may hold (question 6).

**Numbers verified by hand and found correct:** `C` = 0.6 × 199 m/s; `τ = V / (120 A)` and all 30 cells of the time table; the 0.29 / 0.76 / 2.8 / 4.6 τ thresholds; the `dt / 2τ` error of the implicit step; the pair-equalising transfer and the mixing fraction; units per mole and per gram; vessel contents; airlock cycle and vent times; barrier seepage, power and cost tables; breach-shield hold times on and off the bus; metabolic and CO2 timelines; refill times; vent thrust (0.75 Δp A is the choked-jet figure with Cd 0.6, an exhaust speed of about 530 m/s, so venting is a poor thruster, not an exploit); gas-giant pressures and the hard suit's 180 km.

**Fixed: flow maths**
- The sweep volume `S` was in whole litres: 17 % truncation at 1 cm², and zero flow under 0.8 cm². Now microlitres.
- Species could go inconsistent by a unit (`QO2 + QCO2 > Q`) from the two floors, and `ΔQ / Q_u` had no guard at 0. Clamp and guard added; mixing clamped the same way.
- **Small rooms in a chain were stable but wrong:** a 4 m³ lock between a corridor and space throttled the corridor to τ ≈ 6 s against 1.0 s physical (30 s at a deferred step). Sub-stepping of rooms with `ΣS > V` (≤ 8) brings it to 1.5 s. Tinier rooms stay slow; validation says so.
- A sagging barrier overshot its hold pressure on a small room (24 kPa instead of 48). A hold clamp makes it a relief valve at `Δp_hold` for any `dt`.
- The φ table's last interval stretches the final 3 kPa of an equalisation about 2.5×. Documented, accepted.
- Zones are well mixed, but nothing mixed two rooms of different composition before they joined one. A composition difference now keeps the pair active. Room-to-room breaches join zones.
- Bulkhead rate trigger now needs a 3 kPa fall (a lock equalising into the ship used to trip it); the two worked examples were recomputed (88 kPa at 1.5 s; about 30 kPa).
- Small corrections: "holds its sustainable hole 14 min" is 17 min; "50 kPa at one scale height" is 37 %; the wind table's 7 N at 1 m is 6 N; the time table is labelled as τ.

**Fixed: determinism**
- "Identical for any step order" was false: the pair step is sequential. Determinism now rests on a stated fixed edge order, with opening deltas applied in a sorted order so bake-job timing cannot change edge indices.
- The global cap was in milliseconds, which would make gas state depend on server load. It is now a count of edge-steps.

**Fixed: performance**
- 35 ns per edge was one divide, not a pair step. Restated as ~0.2 µs (worst ship 0.2 ms a step, 35 µs a tick amortised); the 0.15 ms cap is unchanged and now bites at about four fully torn capitals, which then step at a larger `dt`.
- Ambient was refreshed at 1 Hz for every awake grid in an atmosphere; now on demand, so a sealed parked ship is really 0.
- Reads (vitals, console, crush check) no longer settle zones; characters in good air are not in the vitals pass; an occupant moving inside a zone is not an event.
- Airlock interlock pairs would have blown ship-design's 64 FailSafe loops on a station. They are read on demand.

**Fixed: consistency with other docs**
- ship-hulls: a hangar behind a barrier is a sealed room and would have been filled solid in the exterior collider. Barrier frames are carved as open portals and big barrier rooms stay unfilled. Cells opened by damage had no room; they now belong to the adjacent one for air and `GridVolume`.
- ship-design: its seam rule (threshold 0.45, width 2 mm) was replaced here by a different threshold. Threshold restored; only the width change is proposed, with the reason (2 mm empties a cabin in 2.3 min).
- exotic-matter: room cap is 255, not 256 (`byte Room` needs a "none"). Class 1 needs an internal order or dampeners starve barriers.
- warp: a hard cancel releases the openings, so gas flows from that tick.
- planets: differential crush kept; its consequence (a flooded ship has no room crush depth) is now stated, with what still limits it.
- rings-and-asteroids: D34 station shields are untouched; "no ship shields" still holds because barriers and breach shields absorb nothing.
- Replication pressure saturated at 1.05 MPa; scale flag added.

**Exploits looked at**
- *Venting an enemy's rooms through doors or a captured console:* every door now has a Δp latch, so no button press or single Data command opens a pressurised room; `Override` is a 3 s lever at the door or an authorised, klaxoned, logged command. Remote venting needs an authorised seat and gives 5 s warning. The docs have no hacking mechanic; one added later must not bypass these.
- *Barrier power sniping:* intended and kept (D41). Bounded by two feeds, the backing door, class-1 ordering and a breach shield for frames ≤ 25 m². A hangar mouth has no answer but its door; question 8 asks about a ride-through.
- *Emergency-shield stalling:* indefinite hold capped (`MaxHoldS`, cool-down); the deploy, drain, drop, recharge flicker removed by a lockout; valves and commanded vents cannot spend patches; merges never fold a held edge into an open one.
- *Air barrier or patch as a free door against boarders:* bodies pass both, stated in both sections and under Boarding. A barrier is a security hole, not a door. Question 5 recommends keeping patches passable.
- *Airlock as a free way in:* panels use the access list, exterior doors lock, outside cranks and the manual equalise handle are not available to a stranger.
- *Decompression used to throw players:* drag is now on relative velocity and implicit, so nothing outruns the air (a light panel used to become a projectile). The throw is bounded and stated (`0.005 · v_air · L` m/s for a person; boots hold to 44 m/s), and every actor-caused opening is attributed.
- *Blowing doors with the regulator:* room setpoints capped at 120 kPa.

**Not fixed, for the owner to decide**
- Open questions 1–8. Questions 6–8 are new.
- Closing a door at depth in a gas giant captures free "inert" gas. Harmless while inert is one species; revisit if hydrogen becomes cargo through this path.
- The pump power figures (20 kW for 250 L/s into 2 MPa) are under the ideal compression work at the start of a cycle. Placeholder, like the costs.
- Sub-stepping is capped at 8, so rooms under about 1 m³ with large doors, and any small room during a long deferral, still pass gas too slowly. If the breach sim shows it mattering, the next step is to treat such a room as a junction with no storage.
