# Ship atmospheres

Status: design, nothing built. Written 2026-11-26 against decisions.md (D9–D37), README.md, ship-hulls.md, ship-design-and-construction.md, frames-and-zones.md, exotic-matter.md, planets.md, warp.md, wormholes.md, rings-and-asteroids.md and tractor-beams.md. Every number is a data knob; every cost is an estimate to measure in the "Air" profiler rows. Lead review applied the same day: see "Review notes" at the end. Round 6 decisions D60–D66 applied in place: see "Round 6 notes" at the end.

## User decisions this doc implements

Recorded as D38–D42 and D60–D66 in decisions.md. Not re-litigated here.

- **D38** Ships have atmospheres inside.
- **D39** Hull damage can leak atmosphere.
- **D40** Airlocks are needed.
- **D41** More advanced ships can use **air barriers**: a small particle shield across an opening that keeps air in the room. Needs constant power.
- **D42** **Emergency shields** may exist that stop breaches from leaking.
- **D60** No sea-water flooding of ships.
- **D61** Air barriers have **no size cap**.
- **D62** An emergency shield's hold time depends on the **breach size**; the shield feeds from a **battery**.
- **D63** Emergency shields do **not** stop bodies.
- **D64** EVA suits are restrictive, so crews are **not in them by default**; **suit lockers** sit around airlocks, hangars and crew rooms for changing.
- **D65** Air is used at **real rates**.
- **D66** Warp space is treated as real space: a holed ship is not sealed by warp; **breaches vent**.

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
| Space, tunnels, airless bodies, **warp cruise** (D66: from T0 to the drop, whatever the ship departed from) | 0 | none |
| Rocky planet | `p0 · exp(−h / H)` from the body's `AtmosphereId` (the same record warp.md's blast and the aero drag read) | `o2Frac`, `co2Frac` from data. **Breathable** is derived, not declared: ppO2 16–50 kPa and ppCO2 < 1 kPa at that altitude |
| Gas giant | planets.md's closed form (1 bar at the datum, 3.7 bar at 50 km, 20 bar at 150 km) | O2 0, CO2 0 (hydrogen and helium count as inert) |
| Under the sea sphere | not gas: exterior edges below the sea surface carry no flow | **no sea-water flooding of ships (D60).** A submerged opening neither leaks air nor lets water in |
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

Small holes are a maintenance problem, 10 cm holes are an emergency with time to act, anything a weapon makes empties a small room before a human can react. That is why bulkheads and emergency shields are automatic, and why a crew in normal clothes (D64) needs hoods within reach ("Crew" below).

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

**Attribution:** every air event with an actor (a door `Override`, the blow lever, a vent command, a barrier `Drop`, a shield `Inhibit`, a cut or shot that makes an opening) stores the actor on the edge event. Crew harmed or carried out by that edge's flow are attributed to the actor through the shared `IncidentRecord` (owned by factions-and-law.md; kind `PersonHarm`, with the edge event as `SourceEvent`), the same way an unrequested tractor is (D26). Whether a venting is lawful is that doc's ruling ("Venting people"): venting hostile boarders off your own ship is self-defence; venting guests, crew, prisoners or the surrendered is Injury or Killing, and an atrocity for the last two. The record exists either way.

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
| `Seal` Data input (D71) | none | **FailSafe** lockdown hold line (ship-design-and-construction.md "Wiring": a one-way data cable from one sender, with the bridge and engineering switches in series): a dead line (a switch opened, the cable cut, the sender unpowered) closes every door on it |
| Open | free while `|Δp| ≤ 5 kPa`; above it the latch needs `Override`; crank 5 s if unpowered | the same latch and `Override`; needs power, or an 8 s crank |
| Holds | 150 kPa | 500 kPa; above its rating a door blows and becomes a `Breach` |

- **Every door has the Δp latch.** No door, plain or bulkhead, interior or exterior, opens against more than 5 kPa on a button press or a single Data command. `Override` is a lever at the door held for 3 s (anyone physically there, audible on both sides), or a command from an authorised seat that sounds the `VentWarnS` klaxon at the door first (15 s, section 7: crews are in normal clothes, D64). Both are `Logged`. This is the rule that stops one click, or one captured console, from emptying an occupied room through its doors.
- **Locks and access:** door, airlock and vent panels answer only characters on the ship's access list, the same filter ship-design applies to seats; there is no hacking mechanic in the docs, and one added later must not bypass the latch, the klaxon or the log. Exterior doors can be `Locked`: a locked door ignores its outside panel and its outside crank. From outside, a locked ship is entered by docking consent, a captured console, or the cutting torch (ship-hulls).
- The rate trigger needs a 3 kPa fall, so a lock equalising into the ship or a craft crossing a barrier does not slam every bulkhead (validation warns when `V_lock / V_zone > 2 %`).
- A body in the doorway delays a bulkhead by at most 1 s, then is pushed to the nearer side. Doors do no damage.
- **Wiring consequences (D17, D71):** cutting a `Seal` hold line locks a ship down (boarders can do it, so can the crew). Cutting a bulkhead's power does not stop it closing, only re-opening. A door whose command cable is cut or dead still works from its own panel (there is no shared Data bus to lose: each cable carries one sender).
- **Ducts** carry pressure too. A vent terminal of class `Safety` has a spring damper (closes at 3 kPa); a plain grille does not.
- **Bulkheads are not enough alone.** A 50 m³ cabin with a 0.2 m² hole next to a 200 m³ corridor: the pair has lost 3 kPa after 0.3 s and the door is shut at 1.5 s with the corridor at 88 kPa. With a 2 m² hole the corridor is at about 30 kPa before the door shuts.

### Crew

Vitals run in the existing **1 Hz dose pass** (exotic-matter.md), next to radiation: ~0.1 µs per character. Breathing gas is the room's, or the hood's or suit's while one is worn and closed. `Sat` is an integer 0–1000.

| Condition | Effect |
| --- | --- |
| ppO2 ≥ 16 kPa | `Sat` recovers +50/s |
| ppO2 10–16 kPa | `Sat` −5/s to a floor of 700: impaired (tunnel vision, half stamina) |
| ppO2 6–10 kPa | `Sat` −15/s: unconscious (< 400) in 40 s |
| ppO2 < 6 kPa | `Sat` −50/s: **unconscious in 12 s**; at 0, health −2 %/s (death about 70 s after exposure) |
| Body in < 6.3 kPa total, no suit or hood kit | after 10 s health −1 %/s; over 30 s leaves an injury that needs a `MedBay` |
| ppCO2 > 1 / 3 / 6 / 10 kPa | console alarm / impaired / unconscious after 5 min / unconscious after 60 s, death 5 min later |
| Pressure falling faster than 20 kPa/s | 2 s stagger, no damage |
| Total > 500 kPa / > 1 MPa without a hard suit | impaired / health damage (deep gas giant inflow) |

Unconscious crew wake above `Sat` 600, so a rescuer with a bottle or a re-pressurised room saves them.

| Kit (data) | Air | Rated | Notes |
| --- | --- | --- | --- |
| **Normal clothes (default crew wear, D64)** | room air only | none | full speed and dexterity. Nothing closes by itself: a breach is survived by leaving, by a hood, or not at all |
| Emergency hood (hood locker on a wall, under seats and berths) | 10 min | vacuum | **3 s to pull on.** A hood and a thin pressure wrap, no thrusters, no tools: enough to reach a door or a suit locker |
| EVA suit (from a suit locker) | 4 h, refilled in its locker slot from ship tanks | vacuum to 300 kPa | thrusters, mag boots, the suit σ exotic-matter already uses. **Restrictive** (below); **30 s on, 15 s off** at a locker |
| Hard suit (from a suit locker) | 4 h | 3 MPa (about 180 km down a giant) | more restrictive; 60 s on, 30 s off |

A hood or suit is a small lazily integrated vessel on the character (one event: low, then empty). Suit tears are out of scope. Durations are times, not masses: at real rates (D65) 4 h is 0.14 kg of O2, so what a suit runs out of is its scrubber cartridge and cell, and a locker slot renews all three.

**Suits are restrictive, so nobody lives in one (D64).** Data in `data/items/suits.json`, placeholders until a character doc exists:

| | Move speed | Sprint | Tool, console and repair actions | Rest, eat, med bay |
| --- | --- | --- | --- | --- |
| Normal clothes | × 1 | yes | × 1 | yes |
| Hood | × 1 | yes | cannot use tools | no |
| EVA suit | × 0.7 | no | × 1.5 time | no |
| Hard suit | × 0.5 | no | × 2 time | no |

**Suit lockers (D64).** A `SuitLocker` (`fn.suit_locker`) is a wall component with 1, 2 or 6 slots (S / M / L). Each slot stores one suit item, or the clothes of whoever is wearing it.

- **Where:** beside airlocks, in hangars and in crew rooms. Validation (section 9) warns about an airlock with no locker and notes hangars and berth rooms without one.
- **Changing is a timed interaction at the locker** (`donS`, `doffS` on the suit item): one scheduled event, no per-tick work. Taking damage or walking away cancels it and it starts again. The character cannot use tools or weapons while changing.
- **A suit carried as a bulky item** can be put on anywhere in twice the time (EVA suit 60 s). The locker is the quick way and the only way to refill.
- **The slot is the suit port:** it refills the suit's air from the ship's vessels over an `Air` route and charges it on LV power, 60 s from empty. A locker with no `Air` route still stores and dresses, and reports "no refill".
- **Access:** lockers answer the ship's access list like door panels. A boarder cannot dress from a stranger's locker; they arrive suited.
- **Cost of a change is time, not air:** the change happens in a pressurised room, so there is no gas event.

**What a breach does to a crew in normal clothes.** From the moment of the breach, with τ from the table above: ppO2 is under 16 kPa at 0.29 τ, under 10 kPa at 0.76 τ and under 6 kPa at 1.26 τ (28 kPa total). `Sat` then runs by the vitals table.

| Room and hole | τ | Impaired from | Unconscious at about | Enough time for |
| --- | --- | --- | --- | --- |
| Any small room, 2 m² or more | under 1 s | at once | **12 s** | a hood already in reach (3 s) |
| Cabin 50 m³, 0.2 m² (smallest damage hole) | 2.1 s | 1 s | **14 s** | a hood at the seat or berth; a wall hood locker is a coin toss |
| Cabin 50 m³, 100 cm² (torch cut) | 42 s | 12 s | **57 s** | a hood, the door, or a suit change started in the first 20 s |
| Corridor 200 m³, 100 cm² | 2.8 min | 48 s | **2.5 min** | a suit change (30 s) |
| Cabin 50 m³, 10 cm² | 6.9 min | 2 min | **5.6 min** | a suit change and a patch kit |
| Cabin 50 m³, 1 cm² | 69 min | 20 min | **53 min** | the leak view |

Death follows unconsciousness by about a minute in vacuum (the vitals table). So decompression kills an unprepared crew in seconds: that is D64's intent, and it is what the automatic kit is for. In order of arrival:

1. **Emergency shield** (0.2–0.5 s): a held hole leaves the room breathable (a 50 m³ cabin with a 0.2 m² hole is at 80 kPa when a class S patch lands) and buys the minutes in section 6.
2. **Bulkheads** (1.2 s): save the next room, not this one.
3. **Hoods** (3 s, 10 min of air): get the people in the room out of it.
4. **Suit lockers** (30 s, 4 h): put the repair party back in.

A crew that expects a fight or an EVA changes first and pays the suit's penalties for as long as it stays dressed. That choice is the gameplay D64 asks for.

### Components and cargo

- **Components are vacuum-rated by default.** A data field `minAmbientKpa` (0 by default) takes a component offline below it: fabricators, med bays, galleys, hydroponics. No cooling model: nothing in the docs simulates heat inside a ship, and inventing one for this is not worth it.
- **Fire** is out of scope (no fire system exists).
- **Cargo:** an item flag `pressureSensitive` spoils stacks left 60 s in a room under 30 kPa (scheduled when the room crosses it, never polled). Loose cargo is moved by the wind rule.

## 4. Airlocks

An airlock is a **room pattern plus a controller**, not a special volume: any design room with an `AirlockController` (`fn.airlock`) in it. The catalogue "airlock" (ship-design's 60 cr line) is a hull-chunk prefab: a 2 × 2 × 2.5 m chamber, two airlock doors, the controller, a pump S and a vent valve.

- **Doors:** every portal of the room binds to the controller. Each airlock door has a Δp sensor and an `Interlock` with the controller: a Data cable each way (D71; ship-design-and-construction.md "Interlocks": the door's latched status out to the controller, the controller's permit back to the door, FailSafe). Interlock lines are **not** `SegLineMask` lines (ship-design allows 64 same-tick FailSafe lines a ship, and a station has more lock doors than that): a dead line only keeps a door locked, so its state is read from the cached binding when a door is asked to open. Only `Seal` hold lines (1–4 a ship) use the line budget.
- **The rule: two doors that lead to different pressures are never open together.** A door unlocks only when `|Δp| < 2 kPa` across it (5 kPa to vacuum, so the last 5 % is let go) and every door on the other side is closed and latched. The interlock is its own pair of cables between door and controller, so it works whatever has happened to the cables from the bridge.
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
- **Warp:** exterior doors lock from spool start to T0 and again from TCommit until the brake ends (warp.md); a cycle in progress returns to "both closed". In cruise between those the lock works normally, so crew can go out onto the hull (warp.md's `WarpShell` pocket). It is a door lock, not an air seal: warp holds no air (D66), so the chamber's vent valve works in cruise and vents to vacuum.
- **EVA flow:** change at the suit locker beside the lock (30 s, D64) → panel inside the lock, outside it, or a console → 12–48 s → out. Coming back: cycle in, change back (15 s); the locker slot refills the suit from ship tanks.

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

**Wiring needs (D71: power and data only):** Power (LV for the controller, HV for pump M and L), Data in (remote cycle), Data out (status), and the `Interlock` Data cables each way per door (FailSafe). **Changes ship-hulls.md:** `SocketType` gains `Air` (ducts, pump and regulator lines); ship-design routes gain medium `Air`.

## 5. Air barriers (D41)

A frame of emitters round an opening holds a thin sheet of confined, ionised particles across it (a plasma window). It stops gas and dust. It is **not gravity or exotic-matter technology**: the docs do not demand it, it burns no EM, and suppression fields and dampeners ignore it. It needs power, constantly.

- **Placement:** an `AirBarrier` component whose frame is a portal. Its cells count as closed for room labelling and `GridVolume`, so a hangar behind a barrier is a room and people in it are in the grid.
- **No size cap (D61).** A frame may span any opening. Power, seepage and cost scale with its area by the law below and nothing else limits it. An edge's `AreaMm2` tops out at 2,147 m², so a larger frame is stored as several `Barrier` edges on the same pair, one per emitter run (areas add and the flow is the same; these are exempt from the 8-per-pair merge of damage openings).
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
| Dock mouth 100 × 40 m | 4,000 m² | 43 MW | 2 MW | 1,600,000 cr |

The rows are examples, not classes: any area is legal (D61), at 10.6 kW and 400 cr per m² against vacuum and 5.2 kg of air a day per m².

- **Failure modes:**
  - **Power loss = immediate opening.** No internal storage (that is the emergency shield's job). Granted power under the idle term drops the sheet in the same 10 Hz step; the opening is then a plain hole of area A.
  - **Brown-out = leak.** With half the power a barrier holds 50 kPa: the room blows down to that and sits there, hypoxic. The formula is continuous, so there is no flicker.
  - **Over-pressure** (a gas giant, a neighbour pumped up): above `Δp_rated` the excess leaks the same way, inward or outward.
  - **Emitter damage:** Integrity under 0.5 drops it (the shared damage rule). A wormhole discharge that takes the emitter offline for 2–10 s opens it for that long.
- **Sniping the power is the intended counter, and it is bounded by design, not by a hidden buffer.** The emitter, its cable and its breaker are targets; a hit drops the sheet at once (D41: constant power). What the owner can do about it:
  - **two independent feeds** (validation Warning for a single feed on a frame ≥ 8 m², the anchors' two-feed precedent), concealed cable, armour over the emitter;
  - **the backing door on `BarrierDown`.** A personnel door shuts in 1 s. A big door travels at 0.5 m/s of its short side (bay 4 s, hangar mouth 40 s), and a 100,000 m³ hangar behind 800 m² empties with τ = 1 s, so a hangar that loses its barrier loses its air. That is the risk bought;
  - **a breach shield** catches a failed doorway or bay barrier as it would a hole (class L covers ≤ 25 m²), for as long as its battery lasts at that size (section 6: a 2 m² doorway 14 min on a class M, a 24 m² bay 12 min on a class L). Nothing catches a hangar mouth;
  - **order inside power class 1:** grants go to breach-shield recharge and air barriers first, then life support, then plating, then dampeners. Today `PowerNet` orders classes only, and the dampener sits in class 1 (exotic-matter: an L draws up to 490 MW), so a tractor yank or a hard burn on trap-fed thrust would brown out every barrier on the bus. (Changes exotic-matter.md.)
- **Not a door against people.** A barrier, interior or exterior, stops gas only. Boarders walk through it as freely as the crew, so it never substitutes for a locked door; it is a hole in the ship's security that happens to hold air.
- **Passing through:** ships, people, cargo and tools cross freely in both directions; that is the point. Each crossing drags air: a one-off loss of `0.25 m × silhouette area` of room air (a person 0.18 m³; a 15 m craft about 10 m³, 12 kg). It is an event at crossing start, not a per-tick test (the portal trigger already exists for membership).
- **Not armour, and not a ship shield (D55).** Projectiles, shrapnel, beams and missiles pass both ways untouched. Tractor beams pass (gravitons). It absorbs nothing. Ship shields are separate devices (combat-and-sensors.md section 3: particle, energy, gravity). A particle panel is the same plasma-window technology backed by a stored energy pool that it spends on impacts; it holds no air, just as a barrier stops no round. A round that kills a barrier's emitter drops the sheet.
- **Heat:** the draw is its waste heat, and it goes into the ship's thermal ledger (combat-and-sensors.md section 4). A raised barrier adds its draw to the ship's thermal signature, and it glows.
- **Power class 1 (life)** on the bus, with a `Drop` Data input and a `BarrierDown` Data output (D71). The standard harness runs a cable from the barrier's data-out to a `Seal` input of the backing door (FailSafe: the door closes when the barrier reports down or the line dies).
- **Why advanced:** tier 2 parts (doorway, bay) and tier 3 (hangar); 20× the price of an airlock for a doorway; megawatts for a hangar, which only fusion or annihilation ships have; steady air loss; and total loss of the room on one power fault. A barrier **complements** a door, it does not replace it: validation warns about a barrier with no physical door behind it, and airlocks stay the cheap, safe default (D40).

## 6. Emergency shields (D42)

A `BreachShield` projector throws a containment patch over a hole that opens in a room it covers. It is the automatic first response; welding is the fix.

| Class | Covers rooms within | Patches | Largest hole | Battery | Recharge from the bus | Response | Cost |
| --- | --- | --- | --- | --- | --- | --- | --- |
| S | 15 m | 4 | 0.5 m² | 5 MJ | 5 kW | 0.5 s | 800 cr |
| M | 40 m | 8 | 4 m² | 50 MJ | 50 kW | 0.3 s | 6,000 cr |
| L | 100 m | 16 | 25 m² | 500 MJ | 500 kW | 0.2 s | 45,000 cr |

- **Coverage** is baked like the dampener's: the rooms whose every air cell is inside the radius, recomputed with the labels.
- **Detection** is pressure, not magic: a covered room that drops faster than **0.2 kPa/s** with a `Breach`, `Leak`, blown door or failed barrier edge of its own. The server knows the edge at once; the class's response time is the design latency. Pinholes under the rate never trigger it: those are found by the leak view and patched by hand.
- **Deploy:** the edge goes `Held` at `A · 1e-3` seepage. Holes larger than the class limit are not covered at all. Out of patches: priority to rooms with crew in them, then by loss rate.
- **Feed (D62): the battery, and only the battery.** A patch draws from the projector's own battery. The bus never feeds a patch; it only recharges the battery. A dead or cut bus therefore changes nothing for a patch that is already up.
- **Hold time depends on the breach (D62):**

```
P_patch = k · A · Δp                  k = 0.3 kW per m² per kPa (data): 30 kW per m² holding 1 atm against vacuum
t_hold  = E_battery / Σ P_patch       every patch a projector holds shares its one battery
```

  Hold time of one patch from a full battery, 1 atm against vacuum:

| Hole | S (5 MJ) | M (50 MJ) | L (500 MJ) |
| --- | --- | --- | --- |
| 100 cm² (torch cut) | 4.6 h | 46 h | 19 d |
| 0.2 m² (smallest damage hole) | 14 min | 2.3 h | 23 h |
| 0.5 m² | 5.6 min | 56 min | 9.3 h |
| 2 m² (a door) | not covered | 14 min | 2.3 h |
| 4 m² | not covered | 6.9 min | 69 min |
| 25 m² | not covered | not covered | 11 min |

  - Δp is the pressure actually held. A room that has sagged to half pressure, or a hole held on a planet with air outside, lasts in proportion longer; the table is the shortest case.
  - Two equal patches on one projector last half as long each. Which rooms a projector spends itself on is the `room priorities` Data input; a console can drop one patch by hand to stretch the others.
  - There is no time cap and no cool-down: the battery is the limit. A small hole is held for hours, a large one for minutes, and every hole still has to be patched or welded (D39), because the battery is not charged while it holds.
- **Recharge from the bus:** at the class's rate, power class 1, **only while the projector holds no patch**. Empty to full is 1,000 s (17 min) for every class. With the charger live during a hold, every hole under `recharge / (k · Δp)` (S 0.17 m², M 1.7 m², L 17 m²) would be held for as long as the bus lived and the hold time would stop depending on the breach (open question 9 asks the user to confirm this reading of D62).
- **Bookkeeping:** `E` is an integer in mJ (the same unit as `Q`). It is linear in time between events at the current drain, so it is settled lazily: when a patch deploys or releases, and whenever a held room is stepped or settled (10 Hz only while that room is active). One scheduled event per projector marks the predicted empty tick, re-predicted at each of those settles. A held room normally only sags, so a stale prediction fires early and re-predicts; a regulator topping the room up is a device event and settles it first.
- **Exhausted:** alarms at 30 s and 10 s before the predicted empty tick (scheduled, no polling), then every patch of that projector drops and each hole is a hole. The time bought is for sealing bulkheads, reaching hoods, changing at a suit locker (30 s) and patching.
- **No flicker after a drop:** a dropped edge is not covered again until it is patched, welded or raised by hand. Any deploy, automatic or by hand, is refused unless the battery can hold that hole for `MinHoldS` = 60 s (knob) at the current Δp on top of what it already holds. So a recharging projector cannot be cycled on one hole at an edge message and a zone rebuild per cycle.
- **Cannot be wasted from outside:** it deploys only on `Breach`, `Leak`, blown-door and failed-barrier edges, never on a valve or a commanded vent, and never on a hole bigger than its class, so cycling valves or doors does not spend patches or charge.
- **Released** when Δp across the patch falls under 1 kPa (the room was sealed off and emptied, or repaired). The battery starts charging when the last patch is released or dropped.
- **Bodies pass (D63).** A patch stops gas, never people, cargo or craft, exactly like a barrier. Crew can step out to repair from outside; boarders can step in through the cut they made.
- **Wiring (D71):** Power, Data out (status), Data in (room priorities, manual raise and drop, and `Inhibit`, FailOff: a dead line does not inhibit). Deliberate venting inhibits the shield for that room.
- **Relation to ship shields (D55):** none. combat-and-sensors.md's particle, energy and gravity shields stop rounds, beams and mass and hold no gas; a breach shield holds gas and stops nothing else. A weapon hole trips it like any breach.
- **Relation to station shields (D34):** none. `StationShield` absorbs the energy of debris and shrapnel at a shell round the station and ignores gas, ships and people. A breach shield holds gas at the hull and absorbs nothing. A station can fit both; they share no pool.

**Permanent repair, in order of effort:**

| Fix | What it does | Limits |
| --- | --- | --- |
| **Patch kit** (hand tool, 5 s) | sets one `Leak` or `Breach` edge to `Patched` (area 0). An edge state, no re-bake | hole ≤ 0.25 m²; holds 150 kPa; lost to any damage brush within 2 m |
| **Weld repair** (construction) | a repair step per damaged piece, compiled from the damage brushes on it like a `Skin` step: plate stock by removed volume, weld joints with quality. Completing it removes those damage brushes, re-bakes, and the opened cells and their edges go | needs stock and a welder; can be done from inside a held or patched room |
| **`Test(room leak)`** | re-certifies the room (section 9) | 30 s |

A poor repair weld leaves a seam `Leak` of its own, by the same quality rule as new construction.

## 7. Life support

**Metabolism (data): real rates (D65).** A person takes in 0.84 kg of O2 and gives out 1.0 kg of CO2 a day: 740 units/s of O2 out of the zone and 641 units/s of CO2 into it. **`metabolicScale` = 1**; the knob stays in data but the design is balanced at 1.

With life support dead and the room sealed, from clean air:

| Who, where | ppCO2 1 kPa (alarm) | 3 kPa (impaired) | 6 kPa (unconscious after 5 min) | ppO2 16 kPa (impaired) |
| --- | --- | --- | --- | --- |
| 1 in a 4 m³ airlock | 1.7 h | 5.2 h | 10 h | 7.9 h |
| 2 in a 10 m³ cockpit | 2.2 h | 6.5 h | 13 h | 9.9 h |
| 4 in a 50 m³ cabin | 5.4 h | 16 h | 32 h | 25 h |
| 4 in a 300 m³ ship (one zone) | 32 h | 4.1 d | 8.1 d | 6.2 d |

- **CO2 comes first, and slowly.** A dead life-support unit is a problem for the next shift on a small craft and for next week on a ship. It is logistics and maintenance, not an emergency; the emergencies are breaches (section 3).
- **Lungs are a small part of the air budget.** One crew uses 0.84 kg of O2 a day (0.95 kg of `ice.water` by electrolysis). A Bottle S of O2 is 15 crew-days, a Tank M 310. Against that: one vented personnel lock cycle loses 4.8 kg of air (1.1 kg of it O2), a 2 m² door barrier seeps 10 kg a day, and one emptied 50 m³ cabin is 60 kg. Leaks, lock cycles and breaches set how much gas a ship carries, not breathing.
- **Scrubbing matters more than O2 supply:** 1.0 kg of CO2 per crew-day has to go somewhere. A regenerative scrubber needs only power; a cartridge holds a fixed mass (pods: crew × 1 kg per day rated).

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
  - It is a Data command from an **authorised seat** only, `Logged`, and it sounds a **klaxon for `VentWarnS` = 15 s** in the room before anything opens. It opens only valves and exterior doors bound to that room, never an interior door.
  - **Why 15 s and not the first draft's 5:** occupants are in normal clothes (D64) and no helmet closes for them. 15 s is the reach to a wall hood locker (12 s) plus the hood (3 s), or a walk out of the door. After it, a vented room knocks out anyone without a hood or suit in 12 s to a minute (section 3).
  - Occupants are not helpless: a hood, the door out while pressures are still equal, the 3 s `Override` lever afterwards, and whatever the designer wired (D17): a `VentInhibit` key switch in series on the command cable to that room's vent valves and exterior doors: opened, the line downstream is dead and no remote vent command reaches them, whatever any console sends (D71; ship-design-and-construction.md "Wiring").
  - So whoever holds an engineering console can clear rooms on a 15 s warning, attackers included. Against a suited boarding party it clears nothing; against a crew in shirt sleeves it is deadly to whoever ignores the klaxon. That is the price of losing the console, not a one-click kill.
- **Sensors:** doors, life-support terminals, airlock controllers and consoles each carry a room sensor. An engineering console shows a room only if a status cable runs from one of its sensors' data-out to the console (data is one-way, D71); cut it and the room reads "no data". Bulkheads use their own sensors and need no bus. Every room sensor has a `PressureDrop` Data output (raised on the bulkhead trigger: 3 kPa lost at more than 1 kPa/s) for alarms and for whatever the designer wires it to, a warp drive's `Cancel` included (section 8).
- **Boarding:**
  - a cutting torch (ship-hulls) makes a growing `Leak` as it cuts, so a boarding cut is announced by the drop alarm and may be covered by a breach shield, through which the boarders then walk;
  - defenders vent rooms, drop barriers and trip the `Seal` line; attackers cut the line, inhibit shields and cut life-support feeds (at real rates, D65, a cut feed takes hours to days to matter: it is not a boarding tactic);
  - **who is dressed decides who vents (D64).** Boarders come in EVA suits: 4 h of air, slower, slower with tools. Defenders are in normal clothes unless they had warning. A defender who vents a room must first be out of it or in a suit (30 s at a locker); a boarder who holes or vents a room drops everyone in it who is not hooded within seconds. Hoods give the crew 10 minutes to fall back, not to fight;
  - an airlock can be commanded from a console the boarder controls, or cranked from inside, never forced past its latch; a `Locked` outer door does nothing for a stranger outside it;
  - barriers and breach-shield patches let bodies through, so neither is a door: a ship that relies on a barrier for its hangar has an open hangar as far as boarders are concerned;
  - D27 capture: the `CommandCore` room is contested by presence in its `GridVolume`. Venting it removes whoever is not suited and leaves the suited side holding the room; presence still decides the capture.

## 8. Interactions

| With | Rule |
| --- | --- |
| **Warp** (D66: warp space is real space) | **Warp holds no air.** Ambient in cruise is vacuum, from T0 to the drop, whatever the ship departed from. Every exterior edge runs the normal law the whole way: a breach, a leak, a failed barrier or an open vent valve vents in cruise exactly as in space, with the same wind, bulkhead, emergency-shield and vitals rules. There is no seal event and nothing extra to schedule; a sound ship in cruise is still 0 per tick. Barriers hold against vacuum at full draw, on the same bus as the drive. A room sensor's drop alarm is a Data output (`PressureDrop`) that a designer can wire to the drive's `Cancel` (warp.md's "hull-breach sensor"). Exterior doors lock from spool to T0 and from TCommit to the end of the brake (warp.md): a lock, not a seal, and free in cruise. A body or loose object the wind carries out through a hole in cruise is in warp.md's `WarpShell` pocket (hull bounds + 20 m of ordinary vacuum), where it can catch a grip or be caught; past the wall it is lost, and an opening with an actor writes the `PersonHarm` record (warp.md's proposal, its open questions 7 and 8). Vent thrust does nothing in cruise. Replaces the earlier "the warp seal covers gas too" |
| **Wormhole tunnels** | ambient is vacuum. Wall and storm discharges are ordinary damage brushes, so ordinary breaches. A discharge that takes a barrier emitter or life-support unit offline has the effects above |
| **Dents and bends** (D59; combat-and-sensors.md section 2 owns causes and numbers) | Nothing re-bakes and no room is relabelled: each effect arrives as one edge or room delta on the damage event. **Pinholes** add their area straight to the `Leak` edge between the two regions they join. A **dent** with `δ/R ≥ 0.2` on a wall bounding a room opens its seams: a `Leak` of `0.002 m² per metre of rim · (δ/R − 0.2) / 0.15`. A **hinge band** leaks `0.001 m²` per metre of section perimeter per degree. A dent inside a room reduces its volume by the dent volume (never below 10 % of design), so its pressure rises to match. **Doors:** a frame spread of ≥ 3 cm makes a door `Sticky` (quarter speed); ≥ 8 cm, or a frame in a hinge band past 2°, makes it `Jammed` until forced (crowbar 20 s, or cut). Bulkhead doors jam open or shut as they stood, so a jammed-open bulkhead cannot auto-seal |
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
- **Seam leaks from joint quality** (ship-design owns the rule: seal joints under `leakSeal` 0.45 leak, area `seam length × (0.45 − q) × 2 mm`). The threshold stays. The width does not survive this doc's flow law: 10 m of q 0.3 seam would be 30 cm² and empty a 50 m³ cabin with τ = 2.3 min, a hull breach from one mediocre bead. **Change to ship-design (adopted there in round 6):** `a = L_seam · 0.02 mm · max(0, 0.45 − q) / 0.45`. Then 10 m of q 0.3 seam is 0.67 cm² and the cabin's τ is 104 min: a maintenance problem, found by the leak test. (Under D67 the lowest skill tier, Unskilled, welds at q 0.40: 10 m of that seam is 0.22 cm².)

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
| No `Seal` hold line reaching the bridge | Warning |
| An airlock with no suit locker in the room on its ship side or within 10 m of its inner door (D64) | Warning |
| A hangar or a berth room with no suit locker in it or within 20 m; fewer suit-locker slots than berths | Info |
| A seat or berth with no hood locker within 10 m ("12 s to unconsciousness, 3 s to hood") | Warning |
| A suit locker with no `Air` route ("stores suits, cannot refill them") | Info |
| A crewed room outside every breach-shield radius; O2, water and inert reserve in crew-days at real rates (D65); pod tube pressurised | Info |

**Costs** (catalogue, placeholders): door 20, bulkhead door 90, vent valve 30, airlock prefab 60 (pump S, as costed), pump M 250, pump L 900, bottle S 15, tank M 120, tank L 800, compressor 200, life support 150 / 500 / 1,800, breach shield 800 / 6,000 / 45,000, air barrier 500 + 400 per m² (any area, D61), hood locker 10, suit locker 40 / 70 / 180 (1 / 2 / 6 slots; suits are separate items).

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
| Vitals | ~0.1 µs per character **in the pass**. A character breathing green-band gas with `Sat` full is not in it; a band change (zone alarm band, hood or suit on or off, zone change) enrols them | 1 Hz dose pass |
| Suit change | one scheduled event | per change at a locker |
| Barrier power | one request in the 10 Hz ledger | while raised |
| Breach-shield battery | settled lazily (linear between events), one scheduled empty event per projector; one recharge request in the 10 Hz ledger | on deploy, release and held-room settles; recharge only while no patch is up |

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

- **Data:** `data/atmosphere/air.json` (C, critical ratio, deadbands, cut-offs, mix rates, vitals bands, `metabolicScale` = 1, `VentWarnS`, wind knobs), `data/components/air/*.json` (doors, bulkheads, valves, pumps, vessels, compressors, life support, barriers, breach shields with battery size, `k` and `MinHoldS`, sensors, hood lockers, suit lockers), `data/items/suits.json` (air time, rating, `donS`, `doffS`, speed and action multipliers), atmosphere fields in the body data.
- **`src/Ships/Air/`:** `ShipAir.cs`, `AirStep.cs` (pair step, φ table), `AirZones.cs` (union-find, settle, events), `Openings.cs` (opened-cell clusters, areas), `RoomLabels.cs` (shared with `GridVolume`), `AirAmbient.cs`.
- **`src/Sim/Systems/`:** `AirSystem.cs` (ledger hook, global cap, links), `Airlocks.cs`, `Bulkheads.cs`, `AirBarriers.cs`, `BreachShields.cs`, `LifeSupport.cs`, `AirWind.cs` (force-pass hook, vent thrust), `Vitals.cs` (in the dose pass), `Suits.cs` (hoods, suits, locker changes and refills).
- **`src/Plans/`:** `Families/Airlock.cs`, `Families/AirBarrier.cs`, `Families/BreachShield.cs`, `Families/LifeSupport.cs`, `Families/SuitLocker.cs`, `Validation/Atmosphere.cs`.
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
  - breach shield: hold time equals `E / (k · A · Δp)` within one step for holes from 100 cm² to the class limit (room pressure pinned), with the bus live and with it dead; a second equal patch on the same projector halves it; exhaustion fires at the predicted tick; no recharge while a patch is up, full in 1,000 s after release; a dropped patch does not redeploy on its own; a deploy under `MinHoldS` is refused; a body crosses a held patch (D63);
  - warp: a breached room vents to vacuum during cruise at the same rate as in space, and a sound ship in cruise reports 0 steps (D66);
  - vitals: 12 s to unconsciousness in vacuum; the normal-clothes timeline table reproduces (14 s, 57 s, 2.5 min, 5.6 min); CO2 at real rates (two in a dead 10 m³ cockpit reach 3 kPa in 6.5 h);
  - suits: an EVA suit takes 30 s on and 15 s off at a locker and twice that from a carried item; an interrupted change restarts; a slot refills a suit in 60 s and debits the vessel by exactly that gas.

### Milestones (after M8's rooms)

| | Scope | Exit check |
| --- | --- | --- |
| **A1** (with M8 rooms) | `RoomLabels` shared with `GridVolume`, `ShipAir`, doors, pair step, vacuum ambient, openings from the bake, `RoomAir` and `AirEdge` replication, vitals in normal clothes, emergency hoods | the time table reproduces in game within 6 %; a sealed ship shows 0 in the Air rows; conservation test green |
| **A2** | zones and lazy rates, vessels, life support, regulators, airlocks, vent valves, bulkheads and the `Seal` hold line, wind and vent thrust, suit lockers and EVA suits | a Wren cycles its lock in 48 s and reclaims 95 % of the air; a player changes at the lock's locker in 30 s and the slot refills the suit; a 2 m² corridor breach knocks a player down and the bulkhead leaves the next room at the predicted pressure; wind costs nothing with no breach |
| **A3** (with ship-design M8 seal joints) | seam leaks, `Test(room leak)`, patch kit, weld repair, Air view, seal-path tool, breach sim, validation class | an Unskilled seam (q 0.40, D67) leaks at the predicted area (ship-design's own exit check); the breach sim's curve equals the live ship's |
| **A4** | air barriers, breach shields, collar links | a hangar barrier at half power settles at half pressure; an M shield holds a 4 m² hole for 6.9 min and a 2 m² hole for 14 min from a full battery, with the bus live or dead, then recharges in 17 min; two docked ships equalise through the collar and separate cleanly |
| **A5** (with planets' atmosphere and gas giants) | planet and giant ambient, `Exposed` rooms, compressor, differential crush | doors open on a breathable world cost 0; a leak below the datum raises the O2 alarm first; a room implodes at `p_amb − p_room = p_crush` |

## Changes to other docs

- **ship-hulls.md:** `Room`, `RoomEdge`, `EdgeKind` replaced by `ShipAir` (section 10). Rooms are design rooms and never merge with space at runtime; openings come from `Air & ~AirDesign` (one extra bitset on damaged bricks only). Rooms and `GridVolume` share one labelling. `SocketType` gains `Air`. The "life support ~1 µs per room per tick" line becomes 0 at rest. Limits table gains ≤ 255 rooms and ≤ 1,024 edges per grid. The exterior collision fill skips rooms with a barrier frame over 8 m², and barrier frames are carved as open portals. Cells opened by damage belong to the adjacent room for `GridVolume`.
- **ship-design-and-construction.md:** families `fn.airlock` (controller), bulkhead door, vent valve, pump, vessel, compressor, `fn.air_barrier`, `fn.breach_shield`, with the ports above; route medium `Air`; validation class Atmosphere; `Test(room leak)` defined; a weld-repair step compiled from damage brushes; the seam leak width 2 mm → 0.02 mm scaled by `(0.45 − q) / 0.45` (threshold unchanged; theirs to own); doors gain the Δp latch, `Locked` and a `Logged` `Override`; door, lock and vent panels use the seat access filter; airlock `Interlock` lines are read on demand and are not `SegLineMask` lines (D71: every port here is Power, Data in, Data out or `Air`).
- **exotic-matter.md:** `PowerClass` assignments above; **a fixed sub-order inside class 1** (breach-shield recharge and barriers, life support, plating, dampeners); one flag bit on `Contamination` for the vent rule; vitals join the 1 Hz dose pass.
- **planets.md:** crush test on `p_amb − p_room`; `AtmosphereDef` composition fields; an imploded room is `Exposed`.
- **warp.md (D66):** done in its round 6: nothing is sealed, door locks are locks, and its `WarpShell` pocket and wall say what happens to a body at a hole in cruise.
- **frames-and-zones.md:** the force pass gains the wind and vent-thrust terms (awake bodies in `Windy` rooms only); the air step runs in the 10 Hz ledger slot.
- **rings-and-asteroids.md:** breach shields share nothing with `StationShield`.
- **combat-and-sensors.md:** breach shields and air barriers are not among its three ship shields (D55); its dents, hinges and pinholes arrive here as `Leak`, volume and door deltas (section 8).
- **factions-and-law.md** (owner of `IncidentRecord`): people harmed or carried out by an actor's air event (vent, override, blow, barrier drop, shield inhibit) are `PersonHarm` records with the edge event as `SourceEvent`. No air-event kinds are added.
- **README.md:** "Missing systems" gains character vitals and suits (normal clothes by default, suit penalties, D64).
- **ship-design-and-construction.md (round 6):** family `fn.suit_locker` (ports: LV power, `Air`), hood lockers, and the four D64 validation rows.

## Open questions

Questions 1–7 were answered in round 6 and are closed. They keep their numbers because the review notes cite them.

1. ~~`metabolicScale` 10×.~~ **Decided, D65:** real rates, `metabolicScale` = 1 (section 7).
2. ~~Air in warp.~~ **Decided, D66:** warp space is real space; nothing holds air in and breaches vent in cruise (section 8).
3. ~~Default suit.~~ **Decided, D64:** crews wear normal clothes; EVA suits are restrictive and come from suit lockers (section 3).
4. ~~Water.~~ **Decided, D60:** no sea-water flooding of ships (section 1).
5. ~~Do emergency shields stop bodies?~~ **Decided, D63:** no. Bodies pass (section 6).
6. ~~How long may an emergency shield hold?~~ **Decided, D62:** as long as its battery lasts at that breach size; no time cap, no cool-down (section 6).
7. ~~How big is a "small particle shield"?~~ **Decided, D61:** no size cap (section 5).

Still open:

8. **Barrier ride-through.** As written a barrier has no storage: one cut cable drops it that tick (D41: constant power). A 1–2 s hold-up capacitor would let a backing personnel door close first without changing "needs constant power". Wanted, or is the instant drop the point?
9. **Does the bus charge a breach-shield battery while a patch is up?** (Reading of D62 to confirm.) The doc says no: the patch runs on the battery alone and the battery recharges only once every patch is released, so the hold time is `E / (k · A · Δp)` for every hole (100 cm²: hours; 4 m²: minutes). If the charger ran during a hold, any hole under S 0.17 m², M 1.7 m², L 17 m² would be held for as long as the bus lived, and only larger ones would run down.
10. **Suit numbers.** EVA suit 30 s on and 15 s off at a locker, speed × 0.7, actions × 1.5, no sprint; hood 3 s and 10 min. Placeholders for D64's "restrictive": right order of magnitude?

Revision and review history: [history/ship-atmosphere.md](history/ship-atmosphere.md).
