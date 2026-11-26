# History of planets.md

Revision and review notes moved out of the design doc to keep it short. The doc itself is current; this is the record of what changed and why.

## Revision (round 5) notes

User decisions 2026-11-25 (decisions.md round 5), applied in place above:
- **D29 relief and dig depth:** −8…+9 km relief and the 2 km dig floor are decided. New "Deep heat" section: rock temperature `T(d)` by depth below the unedited surface (25 K/km, plus a quadratic excess of 900 K from 800 m to 2 km); components lose integrity above `MaxAmbientK` (400 K default ≈ 1.2 km; heat-rated ≤ 900 K ≈ 1.75 km), suits likewise; the last ~250 m is impractical. `PlanetParams` gains `HeatOnsetDepth`, `HeatExcessK`.
- **D30 shores and seabeds unminable:** new "Protected shores and seabeds" section. The band is exact: columns whose unedited surface is ≤ sea + `ShoreProtectHeight` (6 m), from the surface to the dig floor; the generator's inland clamp is raised to sea + 7 m so only seabed and the coastal strip qualify. Removal is clamped to the generated density (adding allowed); all crater/bomb brushes obey it; 128 B column mask per chunk; `ShoreDepthMap` note updated.
- **D31 weather ported to planets:** new "Weather on a sphere" section: latitude rows of 24 km cells with per-row zonal steering, hemisphere-dependent cyclone sense with an equatorial fade, the same ≤ 18-candidate neighbour bound, a tangent-plane snapshot map, server analytic sampling for drag and lift, a GPU-baked planet-wide coverage map for the focus body; costs and files.
- **D32 edits permanent:** "Permanence and growth bounds": ≤ ~10.5 KB per touched chunk forever, coarse-tier compaction, an 8 ops/s per-player cap, a worst-case ~150 MB/day bound for 64 heavy miners, `GeneratorVersion` locked for the universe's life, admin region restore, no regrowth.
- **Game time stops when empty:** noted for edit growth and weather (both keyed on ticks).
- **Open questions:** relief/dig depth and edit permanence removed; day length kept. Gas-giant references updated for D10 (wing lift), D25 (anchors on any grid) and 1 g rocky worlds.
- **Flagged, not changed:** "Fictitious forces are ignored inside the co-rotating zone" and the enter rule "0.25 R if there is no atmosphere" in "Precision, frames, gravity, rotation" disagree with frames-and-zones (fictitious forces applied; `RotatingTop = max(atmo top, 0.05 R)`); frames-and-zones owns both and should win. (Resolved in the round 5 review below.)

## Round 5 review notes

Lead review, 2026-11-25, against decisions.md round 5 (D29, D30, D31, D32, D10/D25 for gas giants, time stops when empty). Fixes made in place above:
- **D29, D30, D31, D32 faithful.** Heat table re-derived (300 … 1,238 K), and the 400 K / 900 K component limits land at ~1.15 km and ~1.75 km as stated. The protected band is a pure seed function and the clamp covers every removing brush. Weather stays a function of seed + tick. No leftover text treats edits as decaying or seabeds as minable. Added: a body with no ocean has no band.
- **Frame conflicts resolved (were flagged, not changed).** "Precision, frames, gravity, rotation" now defers to frames-and-zones: Surface bubbles with 1,024 m / 32 m rebases (not ~4 km), `RotatingTop = max(atmo top, 0.05 R)` with the 1.3× leave (not 0.25 R / +10 %), fictitious forces applied (not ignored), scene gravity plus per-body delta (not zero Jolt gravity plus `AddForce`), character up `−ĝ`. The pose is from seed + tick.
- **D32 perf: unbounded huge tier.** `_huge` was one flat list that every LOD ≥ 6 leaf scanned. Edits are now permanent, so impacts, dropped stations and warp arrivals that hit would grow that scan forever. It is keyed by LOD-10 cell and compacts at 256 ops. Fine and coarse tiers now match the k = 2 rule (fine → LOD 0, coarse → LOD 1..5).
- **D32 visual loss.** Coarse compaction used to bake only a 32 m-voxel LOD-5 snapshot, which would blur 4–64 m craters for LOD 1–4 leaves. It now bakes touched LOD-1..5 surface chunks at their own resolution (≤ ~4 MB per fully worked km², under a third of the LOD-0 bound).
- **D32 exploit: storage griefing.** The 8 ops/s cap still allowed ~3.6 MB per played hour (64 ops per new chunk). A per-`Author` byte budget (1 MB per played hour, knob) now throttles to 1 op/s; honest heavy mining (≤ 0.3 MB/h) never hits it.
- **D31 bound.** "≤ 3 cells per row" fails poleward of ~75° because row cells taper within the row. It is now ≤ 4 per row (≤ 24 candidates) there, and the sampler buffer is sized to match.
- **D30 consistency.** The sea mask is defined as the generated `H(dir) < sea`, the same quantity the band and the inland clamp use, so the drawn sea, the band and dry inland mines (no underwater fog) can't disagree.
- **Gas giants (D10).** Drag and lift point at frames-and-zones' aero pass with `GasGiantWind`.
- **Still open:** day length (Q1) and the gas-giant questions.

## Consistency pass (r6) notes

- **Shared enums (item 6):** `CelestialKind.GasGiant` now quotes the canonical enum owned by frames-and-zones (`{Star, Planet, Moon, GasGiant, BlackHole, Remnant}`).
- **Stale names (item 16):** "decisions-r5" in the round 5 review header now reads decisions.md (round 5). No `zones.md`, `ship-design.md`, `gas-giants.md` or `WarpFloor` references were present.
- **D27:** the huge edit tier's "dropped stations" notes that drops are rare (capture preferred) and are logged as the `StationDrop` incident (the shared `IncidentRecord`, owned by factions-and-law.md).
- **No change needed:** D26/D28 (planets defines no tractors), D33 (planet digging makes no station-threatening fragments; mining near stations is rings-and-asteroids'), the dampener and plating rename (no `F_cap`/`a_eff` here), warp drives, wormhole floors.

## Round 6 / r7 consistency notes

2026-11-26. This doc was not revised in round 6; the r7 cross-check applied what the round 6 decisions and docs ask of it. Decisions were not re-litigated.

- **D74:** server edit validation gains `LandRights.Check`; cave drills and tunnelling vehicles dig through the ordinary edit path; `Reserved` settlement footprints feed the protected-column mask for tools only; the surface region grid and settlement footprints are generated world data (all owned by factions-and-law.md).
- **D60:** no sea-water flooding of ships; terrain around water is solid (already the D30 rule here).
- **D57:** `IncidentRecord` is owned by factions-and-law.md.
- **D58:** a warp exit's pressure wave over a settlement is that doc's `WarpAttack`; a raised region shield scales the blast for receivers inside it and is a warp exclusion (warp.md). What a settlement contains is still a missing system.
- **ship-atmosphere.md:** the gas-giant crush test compares `p_amb − p_room` with the room's rating (its change to this doc, accepted).
