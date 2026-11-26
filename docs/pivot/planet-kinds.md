# Planet kinds

Status: design proposal, 2026-11-26. Nothing built. Written from the working tree on `space-pivot` (`src/Terrain/Planet/*`, `src/Terrain/Voxels/Noise.cs`) and docs/pivot (planets.md, decisions.md, exotic-matter.md, rings-and-asteroids.md, README.md M2 numbers). Nothing was built or run for this doc: every slope, cost and count below is an analytic estimate to be measured, and is marked as a target or an estimate.

Revision 2 (same day, see "Revision 2 notes" at the end): 3D layers are first-class, and the hooks terrain decoration needs are added. Also read for this revision: `src/Terrain.Runtime/PlanetLeafSelector.cs`, `PlanetChunkScratch.cs` and the parallel proposal `terrain-decoration.md`.

Lead review (same day): fixes were made in place; "Review notes" at the end lists each one. Where a paragraph says "corrected in review" the earlier text was wrong, not just reworded.

User direction (not re-litigated):
- The planets are far too flat, and there will be different kinds of planet generation. Bodies never touch, so each body has its own generator and there are no seams between bodies to solve.
- **Full 3D features must be possible.** Caves and cave systems, arches, overhangs, floating and undercut rock and lava tubes are a standard layer family any kind can use, not an optional extra.
- **Terrain decoration (rocks, trees) is generated too** (terrain-decoration.md). A kind names its decoration set and exposes what decoration reads: slope, material, biome channels, an inside-cave flag, and surface point and normal queries on 3D terrain.

Consequences taken as decisions here:
- **One kind per body.** No blending between kinds. Variety inside a body comes from masks inside its kind.
- **A kind is data**, evaluated by one generic evaluator. New kinds need no C#; a new *layer type* does.
- **Steepness comes from amplitude over wavelength and from non-Gaussian shapes** (ridges, steps, stamps, carved level sets), not from more octaves.
- **Terrain is a density function, not a height function.** A kind composes a 2.5D height stack with 3D layers into one signed distance (§1.6). The height stack still carries almost all of the relief and of the cost; 3D layers act in bounded bands round it, and a chunk that no 3D layer can touch runs the 2.5D path unchanged.

## 0. Diagnosis of today's generator (checked against the code)

`PlanetSurface` is one recipe for every rocky body: 23 octaves of double-lattice gradient noise in three groups, composed by `Compose`.

| Group | Octaves | Wavelengths (R = 1000 km) | Height it carries | Slope it can make |
| --- | --- | --- | --- | --- |
| Continent mask | 5 | 1,250 km … 78 km (`ContinentScale` 1.25 R; the brief said 0.x R, the code says 1.25 R) | Shelf 150 m, abyss to −7.4 km, land rise 3,000 m × c | Under 1 % on land |
| Ridges `(1 − \|n\|)²` | 3 | 250, 125, 62.5 km (`RidgeScale` 0.25 R) | `MountainHeight` 3.5–5.5 km × `Smooth01((c − 0.1) / 0.3)` | About 2.5 % per octave, 7–15 % at the very most. The mask needs c ≥ 0.4 for full height and c seldom passes 0.3, so real ranges are 1.5–2.5 km high over 60–250 km |
| Detail fBm | 15 | 32,768 m … 2 m | 450 m × 0.574^i (H ≈ 0.8) | RMS about 8 % (4–5°) at scales ≥ 100 m, about 19 % with the metre-scale octaves |

- Amplitude over wavelength is 0.01–0.014 at every scale above 100 m. A 35° face needs about 0.3. The terrain is 20–30 times too shallow at mountain scale.
- Every term is smooth and near-Gaussian: no steps, no carved features, no stamps. Estimated 100 m slopes: median 3–4°, p99 about 12–15°, nothing over 30°. So the ground reads as rolling hills everywhere, on every body.
- The mountain mask ramps with the continent value, so mountains are one inland dome per continent, not ranges.
- Heights are a 2D function on a cube-sphere lattice (cell ≤ 1 voxel per LOD). Density is `(w − h) × inv` with `inv ≥ 0.25`, exact up to 76° of slope. There is no 3D term: planets.md's "existing overhang term" is not in `PlanetGenerator`. Overhangs and caves cannot exist today.
- Differences from planets.md worth knowing: altitude is already exact (double `sqrt` per voxel at every LOD, stronger than review X1's "LOD ≥ 9"); the height grid is the per-LOD cube lattice, not a per-chunk gnomonic grid; octaves of ≥ 256 voxels come from every 8th lattice point.

Cost baseline (estimates from planets.md's 12 ns per octave sample; the `GenerationCost` Perf test prints the real numbers and they are not recorded anywhere, so run it before K0 and write them into this doc):

| Quantity | Today |
| --- | --- |
| `Height(dir)` at LOD 0 | 23 octaves ≈ 0.28 µs |
| Per LOD-0 lattice point | 7 fine octaves (128 m … 2 m) + compose ≈ 95 ns; the other 16 come from the coarse grid |
| Per LOD-0 surface leaf, lattice | ≈ 2,000 points ≈ 0.19 ms |
| Per LOD-0 surface leaf, total render-only | ≈ 0.5–0.7 ms (planets.md) |
| Impostor 256² per body | ≈ 393k samples × 11 octaves ≈ 50 ms worker |

## 1. Architecture

### 1.1 Kind, program, evaluator

- **`PlanetKind`**: a JSON recipe in `data/planets/kinds/<id>.json`. It holds an ordered stack of feature layers, material rules, deposits, landing-site rules, and the body-level data that goes with the look (sea, atmosphere, gravity range, climate).
- **`PlanetKindProgram`**: the kind compiled for one body at system load. Parameter ranges are resolved from the body seed, octaves are flattened into SoA tables, per-LOD tables are built. Immutable, shared by all jobs, a few KB.
- **`PlanetSurface`** stays the one evaluator and keeps its public surface (`Height`, `Bounds`, `IsSea`, `ProtectHeight`). Its hard-coded three groups become the program's channels. No per-kind classes. `Height` now means the **base height** of the 2.5D stack (§1.6); the evaluator gains `TopHeight`, `Density`, `Crossings` and `SiteSample` for 3D terrain and decoration (§6).
- Parameter randomisation: any number in the JSON may be `[min, max]`. The value is `lerp(min, max, U(hash(bodySeed, FNV1a64(paramPath))))`, so each parameter draws independently of the others.

### 1.2 Evaluation pipeline

The evaluator is a small register machine over named float **channels** (≤ 24 per kind) and the height register `h`. It generalises today's `Accumulate` + `Compose` and keeps the coarse-grid trick.

1. **Noise phase.** Every noise octave adds into one channel. Octaves of ≥ 256 voxels are evaluated on every 8th lattice point and bilinearly interpolated, exactly as now; shorter ones run per lattice point. Shaped octaves (ridged, billow, eroded) on the coarse grid store the **raw** clamped `n` per octave (and the running gradient for `eroded`); the shape, the ridged weight chain and the damping are applied per lattice point after interpolation, at about 2 ns per octave (corrected in review: today's ridge group interpolates the shaped sum, which is harmless at 250 km but would chamfer every crease by `kink slope × 4 voxels`, 1–2 voxels on a 3 km range, and differently at each LOD). Coarse values live only in the coarse arrays, so they do not count against the 24 per-point channels. Warp channels follow the same split: on the coarse grid when λ_w ≥ 256 voxels, per point otherwise. Channels that feed a steep curve can be flagged `precise` (coarse only from ≥ 1,024 voxels) so the interpolation error (≈ 0.2 voxel of level-set shift at 256 voxels) does not facet a cliff line.
2. **Stamp phase.** Seeded features (craters, volcanoes, dune fields, landing pads) add into channels from a per-chunk stamp list. Stamps of radius ≥ 256 voxels go to the coarse grid.
3. **Compose phase.** Per lattice point, the ordered compose ops (curves, masks, combines, glacier, sea clamp) turn channels into `h`. Pure scalar maths, 20–40 ns.
4. **3D phase** (§1.6). A per-chunk cull lists the 3D layers that can touch the chunk. Most chunks list none and run exactly the 2.5D path. For a listed layer, each voxel inside the layer's band combines the layer's signed distance with the height density in `Fill`.

Order dependencies (a warp must be evaluated before the layer it warps; the eroded accumulators run coarse to fine) are resolved by the compiler, which sorts the octave table once.

Exactness rules that make neighbours agree bit for bit (the apron test `NeighboursAgreeOnAprons` keeps passing):
- A lattice point's value depends only on (face, i, j, LOD) and the program. Stamp distances are computed in double from the point's planet-frame direction, never in chunk-local floats.
- Every gate is exact: a mask clamps to exactly 0, and a gated term is `h += m × c`, which adds exactly 0. A chunk may skip a layer whose gate interval is ≤ 0 over its cap, and a neighbour that evaluates it gets the same bits.
- Stamps have compact support with an exact 0 outside it; per-chunk culling is conservative.
- A 3D layer's value at a voxel depends only on the voxel's planet-frame position, its LOD and the column's lattice values. Volume fields are sampled on the LOD's global even-voxel grid, never on a chunk-local one. A 3D layer that is off is off exactly (§1.6), so a chunk that culls it writes the same bytes as a neighbour that evaluates it.
- The SIMD path must equal the scalar path bit for bit (no FMA).

### 1.3 Layer types

Fourteen 2.5D types here; the five 3D types are in §1.6. Wavelengths may be given in metres (`lambdaM`) or as a fraction of the radius (`lambdaR`), so a kind works from a 40 km planetoid to a 1,500 km moon.

| Type | What it makes | Main parameters | Smallest feature | Slopes with the default ranges |
| --- | --- | --- | --- | --- |
| `fbm` | Plains, basins, regolith, soil | λ0, octaves, amplitude, gain | Octave wavelength | `1.2 × a/λ` RMS per octave |
| `ridged` | Mountain ranges. Musgrave ridged multifractal: `k = (1 − \|n\|)²`, each octave weighted by the one before, so peaks are rough and valleys smooth | λ0 16–24 km, A0 2.8–4.2 km, gain 0.45, 8 octaves, `warp` | λ/2 (crease) | Crest to valley 3 km over ~4 km: 30–40° faces, 45° near crests |
| `billow` | Rounded hills, hummocks, lava pillows: `2\|n\| − 1` | λ0 2–8 km, A 150–400 m | λ/2 | 10–20 % |
| `eroded` | Erosion-like fBm with no simulation: octave i is scaled by `1 / (1 + κ·\|G\|²)`, G the running tangential gradient sum (analytic noise derivative). Steep places stay clean and sharp, flats collect detail: ridgelines, fans, smooth valley floors | λ0, octaves, amplitude, gain 0.5, κ 0.6–1.5 | Octave wavelength | Slope distribution with a heavy tail: median 10–15°, p99 40° |
| `warp` | A 3D offset field added to another layer's sample position. Bends ridges, meanders canyons, breaks lattice alignment | λ_w, amplitude A_w (0.15–0.4 × the warped λ0), 2–3 octaves. **No-fold rule (compiler):** `Σ A_w,i / λ_w,i ≤ 0.25`, so λ_w ≥ 4 A_w for one octave; past that the map `p → p + W(p)` folds and smears | Octave wavelength | None itself; multiplies the warped layer's slope by about `1 + 2·Σ A_w/λ_w` (≤ 1.5 under the rule). The rigorous factor for bounds is `1 + √3·L·Σ A_w/λ_w` (§3) |
| `cellular` | Worley F1 and F2 − F1 on the double lattice: plates, cracks, columns, crevasses, spires | cell size, outputs | Crack width | Whatever the curve on it asks for |
| `curve` | Monotone-per-segment remap of a channel or of `h`, ≤ 8 knots, eased. Covers the continent shape (shelf, abyss, land rise), plateaus, and **scarps** (a step of height A along a noise level set: a cliff line) | knots, riser band | Riser width | To vertical at LOD 0 |
| `terrace` | A `curve` on `h` with generated levels: seeded level heights, hard risers, gently sloped benches, talus-eased toe. Mesas, benches, strata steps. Levels are zero-mean about the input, so fading the layer does not move the mean height | level count, range, jitter, riser height 20–250 m, bench slope, strength mask | Riser width | Risers 60–90°, benches 2–6° |
| `canyon` | Carves along the zero set of a warped low-frequency channel: `h −= D·m·P(\|n\| / w)`. P is a wall profile with 0–3 benches and a floor share. Branching, closed-contour networks; depth fades with a second channel so canyons begin and end. V and U profiles give dry valleys. `w` is in metres: the distance to the zero set is `\|n\| / max(\|∇n\|, g_min)` with the analytic derivative (19 ns per octave instead of 12), otherwise the width and the wall slope swing by 3 times or more with the local gradient and the half-width below means nothing. `g_min` (a third of the RMS gradient) caps how far a canyon can swell at a saddle | half-width 0.4–3 km, depth 150–1,200 m, floor 0.2–0.4, benches | Riser width; whole feature at half-width | Walls 45° mean, risers 70–85° |
| `craters` | Seeded impact stamps by size class (§1.4) | size range, coverage per octave, age range, complex-crater diameter | 0.15 r (rim crest) | Simple crater walls 30–44°; complex: terraced walls, flat floor, central peak |
| `volcanoes` | Seeded cones: shield (r 20–120 km, 2–6 km high, 3–6°, caldera with cliff walls), strato (r 4–15 km, 1.5–4 km high, concave, 30–35° near the summit), cinder cones (r 150–600 m, 33°) clustered on flanks. Radial flow texture without trig (noise sampled at distance and the unit tangential offset) | class mix, density, heights | 0.1 r; caldera riser | As listed |
| `ergs` | Seeded dune fields: each erg has its own wind axis (zonal direction at its latitude ± 30°) and wavelength; profile is asymmetric (windward 10°, slip face 33°) | cell 300 km, presence, r 60–220 km, λ 60–400 m, height/λ 0.06–0.10 | λ/4 (slip face) | 10° and 33° |
| `pads` | Seeded landing benches (§6): flatten to one constant height inside a core, blend out to the rim | r 150–400 m, density, interest mask | r | ≤ 2° inside the core, guaranteed |
| `glacier` | `h = max(h, hSmooth + T·m)`: an ice surface over the long-wavelength channels fills valleys, peaks stand through; fine channels are attenuated under ice. Ice caps by latitude mask; terminus cliffs by a scarp on the ice mask | thickness 100–1,500 m, smooth cutoff 4 km, mask | Cutoff λ | Under 5° on ice; 20–80 m terminus cliffs |

The 2.5D layers above produce the base height. Everything past vertical (overhangs, caves, tubes, arches, floating rock) is a 3D layer (§1.6) combined with it.

Final stages, fixed and not layers:
- **Relief knee, every body** (added in review). `MinHeight` and `MaxHeight` bound the shell, the impostor's R16 range and the horizon test, so `Hbase` must stay inside them on every kind, sea or not. A hard clamp would cut flat tops into the tallest peaks, and the worst-case amplitude sum of a steep kind is far above its real peaks (alpine: 9 km of ridged sum + 3 km of land rise + 1.2 km of foothills against +9 km). The compiler computes the worst-case sum `Hsum` by interval evaluation of the whole program and appends a monotone eased `curve` that maps `[0.8·max, Hsum]` onto `[0.8·max, max]` (and the same at the bottom). Below 80 % of the range nothing changes; above it heights compress but stay strictly increasing. A kind whose `Hsum` is over twice its relief range is rejected.
- **`seaClamp`, bodies with a sea:** the current inland floor. A sea-bearing kind must name its `continent` channel.

**Masks.** A mask is a channel in [0, 1] made by `mask` ops: `lat` (smoothstep on `|dot(dir, spinAxis)|`, which is sin of latitude, no trig), `alt` (smoothstep on the current `h`), `channel` (smoothstep on any channel, e.g. a belt noise or the continent value), stamp coverage (crater interior, volcano flank, erg, ice), and `mul`, `min`, `max`, `invert`. Every layer takes an optional `gate` mask and an optional amplitude mask.

**Mountain belts, not domes.** Ranges come from a `belt` channel (ridged, λ 0.25–0.5 R, thresholded to bands 80–200 km wide) intersected with the inland mask, and a `massif` channel varying height along the belt. This replaces `Mountains(c)`.

### 1.4 Crater stamps

- **Size classes:** one class per octave of radius `[r_k, 2 r_k)`. Each class has its own cube-sphere grid in its own fixed rotation of the cube (a table of 16 rotations), so class grids and the cube's face edges never line up visibly. Cell arc ≥ 6 r_k.
- **One candidate per cell**, present with the class's probability, centre jittered so bowl and rim (1.3 r) stay inside the cell. Same-class bowls therefore never overlap, which keeps the per-point lookup O(1) per class and the bounds tight. Coverage is up to 19 % of the area per size octave (the saturated look).
- **Profile** (x = distance / r, distance = R × chord between unit directions, no trig):
  - simple: bowl `(rim + depth)·x² − depth`, depth 0.4 r, wall slope 0.96 x (44° at the rim); rim falls as `rim / x³`, cut at 3 r for classes with an ejecta blanket and at 1.5 r for small ones. `rim = 0.27 × depth` makes the blanketed stamp zero-mean.
  - complex (diameter above the kind's `complexDiameterM`, scaled by 1/g): flat floor, depth growing as the fourth root of diameter (two `sqrt`), 2–3 wall terraces, central peak (0.3 depth, 0.2 r); peak ring on the largest basins.
  - age (hash 0..1): depth × (1 − 0.7 age), rim × (1 − 0.9 age), profile eased toward a cosine bowl. Bright ray albedo for the youngest 5 % (impostor and shader only).
- **Overlap:** classes apply large to small (old to young). A crater erases what is under its bowl: `acc = acc·(1 − e(x)) + profile(x)`, and the product of `(1 − e)` is exported as the `fresh` mask that attenuates fine detail on crater floors.
- **Per LOD:** radius ≥ 8 voxels full; 4–8 eased to the smooth profile (the rim crest is under a voxel); 2–4 bowl only; under 2 skipped. Skipping a zero-mean stamp adds no bias; the small compact classes are not zero-mean, and their bias is ≤ 0.02 r, under 4 % of a voxel at the LOD that drops them.

The same grid machinery serves `volcanoes`, `ergs` and `pads`.

### 1.5 Relief and slope targets

Design rule: a face of angle θ needs peak-to-peak height A over half a wavelength with `tan θ ≈ 2A/λ`: A/λ = 0.29 for 30°, 0.5 for 45°. Today's is 0.01.

Targets per kind (knobs; tests in §7 assert bands round them). "100 m slope" is the slope of H over a 100 m baseline. "1 m roughness" is RMS slope over 1 m.

| Kind | Relief in a 10 km window, p50 / p95 | 100 m slope p50 / p90 / p99 | Area over 30° / over 45° | 1 m roughness |
| --- | --- | --- | --- | --- |
| Today (estimate) | 350 m / 900 m | 3° / 7° / 13° | 0 / 0 | 19 % |
| Temperate continental | 700 m / 3,000 m | 9° / 28° / 42° | 8 % / 0.8 % | 8 % |
| Archipelago | 300 m / 1,800 m (land) | 6° / 22° / 40° | 5 % / 1 % | 8 % |
| Desert, canyons and mesas | 500 m / 1,800 m | 3° / 25° / 70° (bimodal: flats and walls) | 8 % / 4 % | 5 % flats, 15 % walls |
| Alpine / tectonic | 1,500 m / 5,000 m | 22° / 38° / 55° | 30 % / 6 % | 15 % |
| Volcanic | 600 m / 4,000 m | 6° / 25° / 40° | 8 % / 1.5 % | 25 % on flows |
| Ice | 300 m / 1,500 m | 4° / 16° / 38° | 4 % / 1 % | 3 % |
| Barren cratered | 800 m / 4,000 m | 7° / 25° / 40° | 7 % / 0.3 % | 10 % |
| Shattered | 2,500 m / 8,000 m | 10° / 50° / 85° | 20 % / 12 % | 12 % |

All rows are over land (sea floor left out). Two rows were raised in review, because the first numbers did not answer "far too flat" where it matters most:
- **Temperate** had a median 10 km relief of 400 m against today's 350 m and a median slope of 5° against 3°: the home world's typical place was still rolling plain, with all the gain inside the belts. Its stack (§2) now has uplands on a third of the land, taller and shorter hills, and incised valleys outside the belts. Rough check of the median: 20 % coastal plain (3°), 20 % hills (RMS slope `2.4 × 350 / 5,000 × 1.57` for three octaves ≈ 0.26, median 11–12°), 35 % uplands (12°), 25 % belts (28°) puts the median inside the hills and uplands, 9–11°.
- **Ice** was flatter than today at the median (2°) and is the commonest kind for cold planets and giants' moons. It now has chaos terrain on 20–35 % of the surface.
- Alpine's median of 22° needs belts on at least 45 % of the land (belts 28–30°, the rest 12°); with 35 % it is nearer 17°. The belt range in §2 is narrowed to match.

What the mesh can show: the heightfield density alone is exact to 76°; steeper risers would mesh as a one-voxel staircase along the lattice. Clean vertical and overhanging faces come from the `undercut` layer (§1.6), which replaces a riser's vertical-offset density by a true horizontal distance. It ships with the first kinds (K1), so no kind is capped at 75°.

### 1.6 3D layers

A standard layer family (user decision). Any kind may use them; the evaluator treats them like any other layer: data in the kind file, compiled into the program, band-limited per LOD, bounded for the shell.

**Three named heights.** The ground is no longer single-valued, so every consumer says which height it reads.

| Name | Definition | Properties | Read by |
| --- | --- | --- | --- |
| `Hbase(dir)` (`Height`) | The result of the 2.5D stack | Single-valued, analytic, a pure function of the column | Sea mask, D30 band, dig floor, deep heat depth, shore depth, shell bounds, 3D bands and gates, pads, landing sites, decoration site samples |
| `Htop(dir, lod)` (`TopHeight`) | The highest solid in the column: the largest of `Hbase`, the top profile of grounded solid stamps (closed form per stamp), and the host's upper level wherever the column lies inside an `undercut`'s largest outward offset (a caprock ledge stands a whole riser, up to 250 m, above the `Hbase` of the column under it; corrected in review). Displacement and mouths are left out | Single-valued, analytic, within `TopSlack` of the true first crossing from above. `TopSlack` = displacement amplitude / `InvMin` (4 × A: displacement acts along the normal, so on a steep face the crossing moves up to 4 A vertically) + largest mouth radius; a compiled constant per kind, 4–12 m on most kinds, up to 50 m on shattered (corrected in review, was ≤ 16 m) | Coarse-LOD geometry of grounded solids, horizon prune, analytic ground casts above 200 m (as `Htop + TopSlack`), altimeter |
| `Crossings(dir)` | Every sign change of the density along the radial column: floor or ceiling, open-sky or roofed, with normal and material | Multi-valued; a short sphere trace of the density | Decoration, spawn points in caves, exact casts |

**One density.** Per voxel `Fill` already has the exact radial altitude `w`, the bilinear lattice height `h` and the slope factor `inv`. With `v = w − h`:

```
d = v × inv                                             // today's density, metres, + is air
m = gate3d(column) × mask(column) × band(v or datum) × W3[lod]     // per layer, in [0, 1], exactly 0 when off
undercut:  d = lerp(d, x(p) − offset(riser fraction), m)           // x = horizontal distance to the host's level set; a true distance in the riser band
displace:  d = (d + A × m × n3(p)) / D,   D = 1 + cA × A × m / λ3  // the divisor keeps |∇d| near 1; it does not move the zero set
add:       d = smin(d,   f(p) + K × (1 − m), k)                    // f < 0 inside the solid
carve:     d = smax(d, −(f(p) + K × (1 − m)), k)                   // f < 0 inside the void
```

- `f` is the layer's distance **estimate** in metres. `p` is the voxel's planet-frame position in double.
  - Stamps and column profiles have true signed distances.
  - Noise fields (corrected in review): `f = (field − threshold) / g`, with `g` a compiled constant, twice the field's RMS gradient. The first draft divided by the rigorous gradient bound to make `f` 1-Lipschitz. That bound is `14.73 / λ` per octave and `× (1 + √3·L·Σ A_w/λ_w)` once the field is warped, 10 to 100 times the typical gradient, which leaves `f` so shallow that a wall moves several voxels per density quantum (1/32 voxel) and Surface Nets has nothing to interpolate. With `g`, `|∇f|` is about 0.5 typically and at most `G / g`; a steeper `f` only saturates sooner, which is safe.
  - Nothing may rely on `f` being 1-Lipschitz. Every conservative test (node box tests, the 8³-cell test, saturation) is done in **field units** with the certified bound `G`: all of a box is saturated solid when `|field(centre) − threshold| > G × (half-diagonal + 4.25 voxels + k)`.
- `K` = the feature's largest inside depth (`−min f`: the radius of a stamp or tube plus its roughening, `threshold / g` for a noise field) + the saturation distance (4.25 voxels) + `k`. As `m` falls a feature shrinks continuously (a tunnel pinches out at a gate edge instead of being cut off), and at `m = 0` the term is at least `4.25 voxels + k`, so the op returns the byte it was given. That is the exact gate rule of §1.2 for 3D. (Corrected in review: without the `+ k`, `smin(4.25, 4.25)` is `4.25 − k/4`, which at k = 2 m and LOD 0 is 3.75 voxels, under the 3.97 that quantises to 127. A gated-off stamp would then change bytes 4 m above the ground and break the culled-equals-evaluated rule.)
- `smin` and `smax` are polynomial (`min(a, b) − max(k − |a − b|, 0)² / 4k`, k = 0–2 m): exact `min`/`max` once the two differ by k, and never more than `k/4` from it.
- **Saturation with displacement** (corrected in review). `d' = (d + A·m·n3) / D` is saturated only when `|d| ≥ 4.25 voxels × D + A`, and `d = v × inv` with `inv ≥ 0.25`, so in altitude that is `|v| ≥ Pad(lod) × D + 4 A`. The divisor does not move the zero set but it does stretch the unsaturated band. Every early out and every shell test in a chunk or cap where a `displace3d` can be on therefore uses `Pad(lod) × PadScale3`, `PadScale3 = max D` over those layers (1.2–2 with `cA·A/λ3` held under 1 by the compiler), and `skin` carries the `4 A` (§3).
- Order: `undercut` first, then displace, add, carve (caves cut through boulders and arches). A kind may reorder the last three.
- **`displace3d` on a host riser needs that host's `undercut`** (compile rule, added in review). On a face steeper than 76° the height density is `v × 0.25`, which overstates the distance to a vertical face by `slope / 4` (25 m for a voxel 1 m from a 100 m riser), so adding `A·n3` moves the face by centimetres. After `undercut`, `d` in the riser band is a true horizontal distance and the displacement acts as intended. Below 76° no host is needed.
- The result is still a clamped signed distance quantised to 1/32 voxel, so Surface Nets, edits, collision, snapshots and the decoration scan read it unchanged.
- Each voxel also records its **source** (base, displaced, or the 3D layer that won the min or max). Materials (§5) and the inside-cave flag come from it.

**Layer types.**

| Type | Family | What it makes | Main parameters | Feature size | Cost per voxel in its band (est.) |
| --- | --- | --- | --- | --- | --- |
| `undercut` | Column profile | Rewrites the riser of a `curve`, `terrace`, `canyon`, `cellular` crack, caldera or crater wall as a horizontal distance to the host's level set, offset by a profile x(height in riser): true vertical faces, caprock ledges, undercut cliffs, alcoves, notches, slot canyons with overhanging walls, crevasse lips | host layer, profile knots (−30 … +30 m), link to hard strata bands | Overhang depth | ≈ 10 ns (two more bilinear channels, one `sqrt`) |
| `tubes3d` | Column profile | Round or flat-floored tunnels along the zero set of a lattice channel, at a depth below a datum. It is a `canyon` with a roof, so it branches and closes on itself the same way. Lava tubes (radial channel of a volcano stamp), cave storeys (≤ 4, one channel each), ice tunnels at a glacier bed, natural bridges where a tube crosses a fin. Skylights are 2.5D pit stamps placed on the tube | channel, r 3–50 m, depth 8–300 m, datum, floor share | 2 r | ≈ 12 ns, after a per-column reject |
| `caves3d` | Volume noise | Fully 3D systems. `worm`: two warped 3D fields both near zero, tunnels 6–16 m wide that climb, dive and cross. `cavern`: one low-frequency field past a threshold, chambers 20–120 m with pillars. Only inside cave regions (a column gate, λ ≥ 2 km) and where the **presence** mask is on: value noise on a 256 m lattice whose corners hash to 0 or 1, `m = smooth(trilinear)`. It is exactly 0 in a cell with eight off corners (the cull: 8 hashes) and continuous across cells, so tunnels pinch out instead of being cut at cell faces (the first draft's one hash per cell would slice them). The warp is sampled on the 8-voxel global grid and interpolated (λ_w ≥ 64 voxels), not per field sample | λ 60–250 m, width, depth band 10–300 m, region coverage, `roofM` ≥ 6, mouths mask | Tunnel width | ≈ 20–35 ns in kept cells |
| `displace3d` | Volume noise | Displaces the surface in 3D: crags, overhanging faces, fractured walls, rough clinker | A 1–12 m, λ3 16–60 m, `A ≤ 0.4 λ3`, 1–2 octaves, mask | λ3 / 2 | ≈ 10–18 ns at step 2; about 4 times that for an octave under 16 voxels (per voxel, see "Volume fields") |
| `stamps3d` | Volume stamps | Seeded SDF primitives on the class grids of §1.4. Solid shapes: `blob` (boulders of 8 m and more; terrain-decoration.md's `boulders3d` is this preset and the schema accepts that name), `needle`, `cap` (hoodoo), `arch` (torus segment), `slab`, `shard` (floating). Void shapes: `chamber`, `shaft`, `pit` | shape, r 4–250 m, op, `grounded` or a height band, roughening A ≤ 0.2 r | Thinnest part | ≈ 4 ns per stamp in reach, ≤ 8 per chunk |

Compile rules: a 3D feature is at most 512 m across (anything larger is 2.5D relief with 3D detail on it); every layer declares its band; the compiler bounds `Hbase − datum` from above (the amplitude sum of what the datum leaves out) and rejects a deep band that could come within `Pad(0)` of the dig floor, or pass `HeatOnsetDepth` (800 m) below the true surface unless the layer sets `allowHot` (deep heat is measured from `Hbase`, so a level cave under a high peak would otherwise be lethal by surprise); a kind's summed worst-case 3D cost must fit §4's budget. Added in review:
- An `undercut` host's input may hold no octave shorter than 16 × the largest offset (≥ 500 m for 30 m ledges). `x(p)` is the first-order distance `(c − c0) / |∇c|` from the lattice, and it is only good within the offset reach if the input is that smooth. A terrace on a detailed `h` cannot host an undercut; terrace a smooth channel and add detail after.
- A `tubes3d` channel has λ ≥ 16 r for the same reason, and its distance uses the same gradient floor as `canyon`.
- `cA·A/λ3 ≤ 1` for `displace3d`, and a volume field's interpolation error must be ≤ 0.3 voxel at LOD 0 ("Volume fields").
- A stamp's bounding sphere includes its roughening.

**Column inputs.** A 3D layer reads per-column values from the lattice, never from other voxels.
- `gate3d` ∈ [0, 1]: exactly 0 inside `pads` cores plus 20 m, wherever the kind's own mask says, and by the shore rule below. Every 3D layer is multiplied by it. Every other gate of a deep layer must be coarse (channels of 2 km and longer, or stamp masks); the compiler checks.
- **Shore rule** (bodies with a sea; rewritten in review, D30 in §3). The first draft gated on the column's own `Hbase ≤ ProtectHeight + 10 m` as a lattice channel. That fails twice at a steep coast. A protected voxel next to a sea cliff interpolates a gate from lattice points that stand 100 m higher, so its gate is not 0. And a cave in the cliff column, where `Hbase` is high, may sit below the waterline one metre from the water: a column's own height says nothing about how far away the sea is. The rule is now:
  - **Coastal zone:** `z = ramp((c − InlandStart − CoastGuard[lod]) / CoastRamp)` on the continent channel, 0 in the zone and 1 inland. `seaClamp` already guarantees that every sea column and every protected column has `c < InlandStart`. The continent channel is long-wavelength with a certified gradient bound `G_c` (about 3–4 × 10⁻⁵ per metre on a 1,000 km body), so `CoastGuard = G_c × (128 m + 2 lattice cells of the LOD)`, about 0.004–0.01, puts every column with `z > 0` at least 128 m, horizontally, from any sea or protected column. Typical distances are 10 or more times that, because the bound is loose.
  - **(a) All 3D layers** are multiplied by `max(z, ramp((h − ProtectHeight − 10 m) / 20 m))` with `h` the **voxel's own interpolated height**, not an interpolated gate channel: a protected voxel has `h ≤ ProtectHeight` and `z = 0`, so the factor is exactly 0.
  - **(b) Carve layers** are also multiplied by `max(z, ramp((w − ProtectHeight − 30 m) / 20 m))` on the voxel's altitude `w`: in the coastal zone no natural void exists at or below 30 m above the protected band, whatever the cliff geometry. Inland, voids may go below sea level.
  - Cavity chunks need no fine lattice for this. `c` is in the datum set. A voxel a deep layer can carve in the coastal zone has `w > ProtectHeight + 30 m` and lies at least `roofM` (≥ 6 m) under the surface, so `h > ProtectHeight + 36 m`, where (a) is exactly 1.
  - Stamp-built islands (volcanoes on the sea floor, `c` far below `InlandStart`) are coastal zone throughout: they keep tubes, caves and crags, above the 30 m line only.
- **Surface-attached layers** (`undercut`, `displace3d`, grounded `stamps3d`, and the roof rule of caves) read the true `v = w − h`.
- **Deep layers** (`tubes3d`, `caves3d`, void stamps) measure depth from a **datum**: absolute altitude (level cave storeys), or `hLong`, which the compiler derives by running the compose program without any octave shorter than 2 km and without any stamp or carved feature narrower than 2 km. `hLong` follows continents, belts and the long ridge octaves, so a cave keeps a sensible depth under a range and opens where canyons, valleys and cliffs cut below it. Its octaves are on the coarse grid at LOD ≤ 3 (one or two per datum layer are per-point at each of LOD 4–7, where only large tubes, caverns and stamps remain), so a chunk wholly below the surface never runs the fine octaves (cavity chunks, §4), and the same cave sits at the same place at LOD 0 and LOD 3 (to ≈ 0.3 voxel of the coarser one).
- **Roofs and mouths.** A deep layer's `m` includes `roof(v)`: it may not come within `roofM` of the true surface, except where its `mouths` mask is on (riser masks of cliffs and canyon walls, pit stamps, a kind's own channel). So caves stay sealed under flat ground and open in walls and pits, which is where relief cuts below the datum. Below `roofM` the term is exactly 1, so chunks with no surface need no `h`.

**Per-chunk cull** (job, 0.3–1 µs): for each 3D layer active at the chunk's LOD, test the gate's maximum over the lattice window (exact for the bilinear values voxels read), the band against the chunk's altitude range, and the layer's own box test (the channel interval for tubes, the presence corners and a Lipschitz interval for volume noise, cell enumeration for stamps). What survives is the chunk's 3D list. Empty list: the 2.5D path, same bytes, same cost. For `undercut` the cull also sets the chunk's `UpActive` and `DownActive` from the host channels in its window: the riser top and bottom of every column within the offset reach, because overhanging rock stands above the `hMax` of the columns under it.

**Volume fields are sampled on a grid where the error allows it.** The LOD's global even-voxel grid (19³ points for a padded chunk at step 2, 11³ at step 4), interpolated trilinearly per voxel. Both chunks at a seam read the same grid points. The step rule (corrected in review; "≥ 8 samples per wavelength" was too coarse):
- Trilinear error is `4.9 × amplitude × (step·s / λ)²`. For a threshold field (`caves3d`) that moves a wall by about `4 × (step·s)² / λ` metres: step 2 at LOD 0 needs λ ≥ 55 m for 0.3 voxel, which caves' 60–250 m meets; step 4 never does. At LOD ≥ 1 the same fields carry up to 1 voxel of wall error. That is accepted: it is deterministic and equal on both sides of a seam, inside the one-voxel allowance the shell tests already make, and the voids are closing there anyway.
- A displacement octave is on the step-2 grid while λ3 ≥ 16 voxels (error ≤ 8 % of A). From 16 down to 8 voxels it is evaluated **per voxel** in kept cells; it fades from 8 to 4 voxels and is then skipped. Dropping it at 16 voxels instead would pop a crag silhouette by up to 0.4 λ3, 3 voxels of the coarser LOD.
- Before sampling, a test per 8³-voxel cell drops cells the layer cannot reach, and those voxels keep `d`. For displacement the test is exact and cheap: a cell is kept only if some column in it has `|v| < Pad × PadScale3 + 4 A` (the surface band, 25–35 % of a surface chunk's cells). For threshold fields it is the Lipschitz interval in field units; with today's `GradientLipschitz` (14.73) that drops almost nothing at λ ≤ 100 m, so the cost tables in §4 assume **every cell of a kept chunk is sampled** until the certified tighter constant exists.

**Band limit per LOD.** Every 3D layer has a compiled weight `W3[lod]`: 1 while its feature size is ≥ 4 voxels, falling linearly to 0 at 2 voxels. At the nearest distance a LOD is drawn, a voxel is about 11 px at 1080p and 60° (`SplitFactor` 3), so a feature starts to shrink near 43 px and is gone near 21 px, the same threshold every 2.5D layer has. What the weight does:

| Layer | Response as the LOD coarsens | Effect on the silhouette |
| --- | --- | --- |
| Carve (`caves3d`, `tubes3d`, void stamps) | Radius × W3: the void closes from the inside, the rock round it and the ground above it do not move | None. Only mouths show from outside; a `mouth` coverage mask darkens the albedo of the closed mouth at coarser LODs |
| `undercut` | Overhang offset × W3, down to the plain riser, which itself widens per LOD (§3) | Continuous: the cliff line does not move, the lip retreats by ≤ its own depth |
| `displace3d` | Octave fades from 8 to 4 voxels of λ3, then skipped (zero-mean) | Zero-mean, not sub-voxel: with `A ≤ 0.4 λ3` the LOD step that drops an octave moves a crag face by at most 1.6 voxels of the coarser LOD (was claimed sub-voxel). Judge it in motion in K1; if it pops, lower the `A / λ3` cap |
| Grounded solid stamps (boulders, needles, arches, hoodoos) | When the thinnest part is under 2 voxels the stamp moves from the 3D phase to the stamp phase as its **top profile** (a 2.5D stamp: the opening under an arch fills, a hoodoo becomes a spike), and from there follows the crater rule (softened, then the area mean, then skipped) | The top envelope is kept at every LOD; this is what `Htop` returns |
| Floating stamps (`shard`) | Radius × W3, then skipped | A floating rock vanishes near 21 px (open question 6) |

The rule behind the table: **at every LOD, the first crossing from above equals LOD 0's top envelope band-limited to that LOD's voxel**, to within one voxel plus `TopSlack`. Voids never reach the limb, and solids fall back to their top profile, so nothing on the far silhouette depends on 3D evaluation. The test in §7 asserts it.

**Lighting.** The scene has no shadows yet, so a cave would be sunlit inside. The generator knows which voxels are enclosed: a voxel whose source is a carve layer and which lies deeper than `roofM` below the true surface (so not in a mouth) gets its material's `enclosed` bit (the top bit of the material byte, leaving 127 materials). `terrain.frag` gives enclosed surfaces no direct sun and a low sky ambient. This is a stop-gap until scene shadows exist (open question 4).

## 2. The kinds

Layer stacks are listed bottom to top. All numbers are ranges resolved per body seed. Every kind ends with `pads`, the material rules and, with a sea, `seaClamp`.

**Temperate continental.** The home-world look: continents with coastal plains, hill country, mountain belts with real faces, dry valleys, cliffed stretches of coast. Sea, breathable air, weather.
- `continent` fbm (5 octaves from 1.25 R) → `curve` (shelf, abyss, land rise 1.5–3 km × c) with coast profiles chosen by a 40 km mask: beach, or a 20–120 m sea cliff scarp on a third of the coast.
- `belt` + `massif` → `ridged` mountains (λ0 16–24 km, A0 2.8–4.2 km, 8 octaves, warp amplitude 3–4 km at λ_w 32–48 km, 2 octaves: `Σ A_w/λ_w` ≤ 0.25) inside belts, which cover 20–30 % of the land.
- `eroded` **uplands** (λ0 12 km, 600–1,200 m) on an `upland` mask (fbm, λ 300 km) over 30–40 % of the land, not only as foothills (raised in review, §1.5).
- `billow` hills elsewhere inland (λ0 4–6 km, 250–450 m, 3 octaves, gain 0.45); coastal plains stay gentle (the spawn and the first landing are there).
- `canyon` in U/V valley mode in belts, uplands and hills (depth 150–600 m, half-width 1–3 km); lowland scarps (`curve` on a 60 km channel, 30–120 m) so even the plains have a cliff line in view.
- `fbm` soil detail (λ 64 m … 2 m, 2.5 m): smooth at walking scale.
- `glacier` above the snow line near the poles (optional, 30 % of seeds).

**Archipelago / ocean world.** Land fraction 5–15 %: island arcs, volcanic islands, atolls on a deep ocean. Sea, usually breathable, stormy.
- `continent` with low land bias and more octaves of coast (λ to 20 km); arcs from `belt` (thin, 30–60 km) → `ridged` (A0 1.5–3 km) so islands are ridge tops.
- `volcanoes` strato and shield classes, many of them below sea level (seamounts) so some become islands; calderas flooded where the floor is below sea level (ordinary sea mask).
- Cliff scarps on 60 % of coasts; `billow` lowlands; `fbm` soil.

**Desert with canyons and mesas.** Layered plateau country: flat benches, mesa and butte fields, deep branching canyons, dune seas in the basins, a few old craters. No sea, thin dusty air.
- `fbm` basins (5 octaves from 1.0 R, 0.9–1.6 km) → `curve` plateau (lowlands, a 1–2.4 km plateau, a marginal escarpment).
- `terrace` (5–8 levels, risers 60–220 m, benches 3 %, warped by a strata channel) gated by a mesa mask; `canyon` (half-width 0.7–1.6 km, depth 0.5–1.1 km, 2–3 benches) on the plateau.
- `eroded` badlands (λ0 3 km, 150–300 m) on slopes; `ergs` below 500–700 m; `craters` at 2 % coverage, old; `fbm` gravel detail (1 m).

**Alpine / tectonic.** A young, folded world: several long ranges with 4–7 km peaks, deep glacial troughs, high plateaus between ranges, small seas. Cold, thin to breathable air.
- As temperate, with belts covering 45–55 % of land, `ridged` A0 3.5–4.5 km and gain 0.5 (worst-case sum 9 km: the relief knee of §1.3 is what keeps this kind inside +9 km without flat-topped peaks), `eroded` everywhere on land, plateaus by `curve` between paired belts, `canyon` troughs in U mode (depth 0.6–1.5 km), `glacier` above a latitude-dependent snow line, talus-eased `terrace` on faces (cliff bands 40–150 m).

**Volcanic / lava world.** Shields the size of countries, strato cones, calderas, fissure ridges, fresh flow fields with rough clinker, ash plains. Hot, no sea (or a lava "sea" as a material, not water), toxic air.
- `fbm` base (0.6–1.2 km); `volcanoes` all three classes at high density; `ridged` fissure ridges along a belt (λ0 6 km, 300–700 m).
- Flow fields: `billow` (λ0 400 m, 8–25 m) and `cellular` clinker (cells 3–6 m, 0.5–1.5 m) gated by the volcano flow mask; `curve` flow fronts (10–40 m scarps on a flow channel); `fbm` ash (smooth) elsewhere; `craters` 1 %.

**Ice world.** A smooth ice shell with pressure ridges, long straight fracture bands, crevasse fields, ice cliffs, scattered nunataks, few craters (young surface). Cold; thin or no air; no liquid sea.
- `fbm` bedrock (small, 0.5–1 km) → `glacier` over almost everything (thickness 0.5–1.5 km).
- `cellular` (cells 80–300 km) F2 − F1 → `curve` double ridges and troughs along cell edges (100–400 m high, 1–3 km wide); `ridged` pressure ridges (λ0 2 km, 40–120 m); `cellular` crevasses (cells 20–60 m, cracks 1–4 m wide, 5–30 m deep) in shear zones; `ergs` in sastrugi mode (λ 5–20 m, 0.3–1.5 m); `craters` 3 %.
- **Chaos terrain** (added in review, §1.5) on 20–35 % of the surface by a 400 km mask: `cellular` blocks (cells 0.3–2 km, `precise`) with a per-block height step and tilt (50–300 m scarps between blocks, matrix between them 30–80 m lower) and `terrace` ice cliffs on the block edges.

**Barren cratered moon.** The airless default: saturated highlands, smoother dark plains (maria) in the largest basins, regolith-softened small scale, a few wrinkle ridges and rilles.
- `fbm` base (1–2 km) with a highland/maria `curve` (maria flood the low basins flat).
- `craters` over every size class from 4 m to 0.06 R, coverage 12–19 % per octave on highlands and 3–5 % on maria, full age range, complex above 15 km / g-scaled; `ridged` wrinkle ridges on maria (λ0 20 km, 100–300 m); `canyon` rilles (half-width 300–800 m, depth 100–400 m, sinuous warp); `fbm` regolith (0.3 m), attenuated by `fresh`.

**Shattered / riven world.** A cracked crust: tilted plates 150–400 km across with escarpments where they meet, chasms 3–7 km deep and 5–30 km wide between them, fractured walls, spire fields along the rims. One per system at most. Thin air or none.
- `cellular` plates (cell 0.15–0.4 R): per-plate tilt (a linear height ramp from the cell hash, ± 0.5–3 km at the edges) and a `canyon` on F2 − F1 with a stepped wall profile; relief uses the full −8 … +9 km.
- `eroded` plate tops (λ0 8 km, 400–900 m); `ridged` wall fracturing (λ0 1.5 km, 200–500 m) gated to walls; `terrace` strata in walls; `craters` 5 % on plate tops; 3D layers on walls, rims and over the chasms (table below).

**Small bodies.**
- **Minor moon** (R 20–300 km, airless): a `fbm` shape layer at λ 2 R … R/2 with amplitude up to 0.15 R (a lumpy, still star-shaped body), `craters` dominant, grooves (a `canyon` on a plane-wave channel: parallel rills round one axis), thick smooth regolith.
- **Remnant planetoid** (exotic-matter.md: R 20–60 km, relief ± 2 km, 0.15–0.4 g, no air, no sea, `DigDepth = Radius`, materials by depth fraction): young and strange. 3–6 seeded shock foci with concentric ring terraces (a `curve` on distance to the focus), `cellular` spire fields (needles 50–300 m at 60–80°), radial fracture `canyon`s, almost no craters. Its `TerrainSeed` comes from `RemnantParams`; clients get it at collapse and generate it like any body.
- **Moons of 300–1,500 km** use the main kinds above.
- **Asteroids** have no kind. rings-and-asteroids.md has them reuse the planet **chunk path** (octree, 32³ chunks, Surface Nets, edits) through their own `IDensitySource` (`AsteroidDensity`: archetype SDF + noise + ops), not `PlanetSurface`. Two things here must stay shareable with that path and not be planet-only: the combine ops of §1.6 (`smin`, `smax`, the gate rule) and the `stamps3d` primitives, which live in `Layers3D.cs` and `Stamps3D.cs` with no dependency on the lattice. **Gas giants** have no surface and no kind.

### 3D layers by kind

Every kind uses the family. Ranges are resolved per body seed; "regions" is the share of land a deep layer's column gate turns on.

| Kind | `undercut` | `displace3d` | `tubes3d` | `caves3d` | `stamps3d` |
| --- | --- | --- | --- | --- | --- |
| Temperate continental | Inland cliff bands and valley walls (ledges 2–6 m) | Crags in belts, A 2–5 m | None | Karst in limestone strata: `worm` + small `cavern`, regions 10–20 %, depth 10–150 m, mouths in valley and cliff walls, a few `shaft` sinkholes | `blob` boulders on scree and in belts |
| Archipelago | Inland cliffs only (sea cliffs are in the shore band, open question 3) | Volcanic crags | Lava tubes on island shields, r 4–12 m | Karst on the larger islands, regions 5–10 % | `blob`; `pit` skylights |
| Desert, canyons and mesas | The signature: caprock overhangs on mesa rims, alcoves in canyon walls, slot canyons | Weathered walls, A 1–3 m | Short tubes through fins: windows and natural bridges | Shallow `worm` alcove systems behind canyon walls, regions 5–15 %, depth 10–120 m | `arch`, `needle` + `cap` hoodoo fields on mesa margins, `blob` talus blocks |
| Alpine / tectonic | Cliff bands on faces, bergschrund lips | Faces and arêtes, A 3–8 m | Ice tunnels at glacier beds | Deep potholes: `shaft` + `worm` to 300 m, regions 10–15 % | `blob` erratics, `needle` gendarmes on ridges |
| Volcanic | Caldera walls, flow-front lips | Clinker and spatter, A 1–3 m on the flow mask | The signature: lava tubes radial on shield flanks, r 5–25 m, depth 8–60 m, with `pit` skylights | None (gas-bubble `chamber` stamps instead) | `chamber` bubbles, `needle` spatter spires, `blob` bombs |
| Ice | Crevasse lips, ice-cliff notches | Seracs, A 2–6 m | Moulins and sub-ice tunnels at the bedrock contact | `cavern` voids under the shell near fracture bands, regions 5 % | `shaft` moulins, `blob` ice blocks |
| Barren cratered moon | Fresh crater walls and rille walls (slight) | None | Mare lava tubes, r 15–50 m, depth 20–150 m, rare `pit` skylights (the lunar-pit look; a natural base site) | None | `blob` ejecta blocks of 8 m and more round young craters |
| Shattered | Plate rims and chasm walls, deep (10–30 m) | Fractured walls, A 5–12 m | None | `cavern` fracture voids through plate edges, regions 20–30 % | The signature: floating `shard`s over the chasms, `arch` bridges across narrow chasms, `slab` overhangs |
| Minor moon | Groove walls (slight) | None | None | Sparse `worm` fracture voids, regions 5 % | `blob` boulder fields |
| Remnant planetoid | Ring-terrace risers | A 3–8 m | None | `cavern` chambers in the top 300 m only (changed in review: remnant purity is `q(u) = 0.08 + 0.80·u²` by depth fraction, so a natural chamber at depth is walk-in access to rock that exotic-matter.md gates behind a 30 km tunnel; open question 5) | `needle` spires, floating `shard`s near shock foci |

Floating rock is not physically supported. It is offered only on the shattered and remnant kinds, where exotic matter is the excuse (open question 2). All generated terrain is static: nothing here falls.

### Assigning kinds

- `SystemGenerator` (planets.md "Seeding") already produces radius, gravity, orbit and spin per body. It then picks a kind: `zone` from equilibrium temperature (`sqrt(sqrt(L)) / sqrt(a)`, sqrt only) as hot / temperate / cold, plus body class (planet, giant's moon, small). `data/planets/assignment.json` holds integer weights per (class, zone); the pick is a hash against the cumulative weights, restricted to kinds whose `requires` (radius, zone, gravity) accept the body.

  | Class, zone | Weights (per mille) |
  | --- | --- |
  | Planet, hot | volcanic 450, desert 250, cratered 250, shattered 50 |
  | Planet, temperate | temperate 350, desert 250, archipelago 200, alpine 150, volcanic 50 |
  | Planet, cold | ice 500, cratered 250, alpine 150, shattered 100 |
  | Giant's moon (300–1,500 km) | ice 350, cratered 300, volcanic 200 (inner moons only), desert 100, shattered 50 |
  | R < 300 km | minor moon 1,000 |

- Rules after the pick: at most one shattered world per system; the starting system has one temperate or archipelago world with breathable air.
- **Hand-authored bodies:** a body entry in `data/systems/*.json` may set `"kind"`, `"kindSeed"` and `"overrides"` (a map from parameter path to a value or range, e.g. `"layers.mountains.amplitudeM": 5200`), plus the usual `PlanetParams` overrides. A hand-made kind file is just another kind.

## 3. Hard constraints and how each layer meets them

**Pure function of seed + position.** Every layer is noise on the double lattice, a hash of a lattice cell, or scalar maths on those. No layer reads a neighbour's result, an earlier bake or any stored state. The "erosion" is derivative damping and curve shaping, not a simulation. Landing pads are stamps whose reference height is the long-wavelength channels at the pad centre, itself a pure function. 3D layers are the same kinds of term (3D noise at the voxel's position, cell hashes for stamps and presence, scalar maths on lattice channels); their SDFs, smooth min and trilinear lookup need only add, multiply, divide, `sqrt`, min and max, so the determinism rules below cover them unchanged.

**Deterministic across machines.**
- Hashes are integer (`Noise.Mix`, `Fold`); cells come from `floor(double)` as `long`.
- The evaluator and the compiler use only add, subtract, multiply, divide, `floor`, `sqrt`, min, max and compare. No `Sin`, `Cos`, `Atan`, `Exp`, `Log`, `Pow` anywhere (latitude is `dot(dir, axis)`; fractional powers are repeated `sqrt`; radial flow texture uses the unit offset vector, not an angle).
- Derived tables (normalisation sums, per-LOD slope tables, the land-bias quantile from 4,096 fixed directions) are built with the same operations in a fixed order.
- JSON numbers parse to the nearest IEEE value in .NET, the same everywhere. The compiled program has a content hash that joins `GeneratorVersion` in the save and the handshake.
- The server's collision stays authoritative regardless.

**Band-limited per LOD.** Every layer declares its smallest feature and one of four responses when a LOD cannot show it: skip (zero-mean terms), fade amplitude, widen (steps and rims), or replace by the mean. The rule generalises today's "skip octaves under 2 voxels" (`s` = voxel size at the LOD).

| Layer | Response at coarse LODs |
| --- | --- |
| `fbm`, `eroded`, `warp` | Octave skipped when λ < 2 s (as now). `eroded` damping runs coarse to fine, so dropping fine octaves does not change the coarse ones. A skipped warp octave shifts the warped layer sideways by at most its amplitude, which the no-fold rule keeps under λ_w/4, so under half a voxel |
| `ridged`, `billow` | Octave skipped when λ < 4 s (the crease is a half-wavelength feature). `(1 − \|n\|)²` is not zero-mean (mean ≈ 0.35), so the skipped tail lowers the range by `0.35 × Σ` of its amplitudes: 0.2–0.25 voxel at every LOD for gain 0.45–0.5 (15 m at LOD 6, 1 km at LOD 12 for alpine). The compiler adds that mean back as a constant per LOD times the belt mask |
| `curve`, `terrace`, scarps | Riser band widened to `max(band, s × g(L))`, where g(L) is the input's RMS slope from the octaves that LOD keeps (a per-LOD table built at compile time): a riser is 2 voxels wide where the input has its RMS slope, and narrower in proportion where the input is locally steeper (not a guarantee; there it meshes as the one-voxel staircase until `undercut` takes over). A terrace fades to the unterraced height when its step is under one voxel (zero-mean, no shift) |
| `canyon`, `cellular` cracks | Wall risers widen as above; the whole feature fades to its area-mean depth when its half-width is under 2 s |
| `craters`, `volcanoes` | By radius: full ≥ 8 s, softened 4–8 s, bowl or cone only 2–4 s, skipped under 2 s (§1.4). Caldera walls widen like risers |
| `ergs` | Dunes skipped when λ < 4 s and replaced by the erg's mean height (A/2 × mask), so the dune sea keeps its bulk |
| `pads`, `glacier` | Smooth by construction; pads skipped when r < 2 s |
| 3D layers | The weight `W3[lod]` of §1.6: voids close from the inside between 4 s and 2 s of feature size, overhang lips retreat, displacement octaves are skipped under 4 s, grounded solids fall back to their top profile and floating ones shrink away. A 12 m tunnel is full at LOD 0–1, half-width at LOD 2 and gone at LOD 3; a 60 m lava tube lasts to LOD 4; a 120 m cavern to LOD 5 |

These widenings are the per-LOD slope clamp: at LOD L no layer can make a slope above about `stepHeight / 2s`. Sub-texel features still show from orbit as colour: the impostor shades with the prefiltered masks (canyon and crack coverage darken, ejecta rays brighten, mouth coverage darkens).

**Conservative height bounds.** `PlanetSurface.Bounds` becomes interval evaluation of the same program: every op has an `Eval` and an `EvalInterval`. `Bounds(dir, angle, minLod)` bounds `Hbase` at every LOD ≥ `minLod`. That lets bounds drop what those LODs skip, which matters once small classes and fine risers carry real height. Every layer's interval contains its filtered value at coarser LODs (0 or the mean lies inside it; curves use an earliest-riser and a latest-riser envelope). Why 0 is inside an octave's interval whenever some LOD in range skips it (stated in review, because a tight interval such as `a·[0.3, 0.4]` would not hold 0): a node's cap is never smaller than `LatticePadCells` (12) cells of its own LOD, and an octave that any LOD up to the node's skips has λ < 4 voxels of that LOD, so its spread `L·ρ/λ` is at least 30 and the interval is the full `[−a, a]` (`[0, a]` for ridged). A refined `Bounds` call with a smaller cap than that must pass the LOD it is asked for, and the compiler's unit test covers it.

Which `minLod` is safe (a correction to the first draft, which passed the node's own LOD): `PlanetLeafSelector.Visit` is top-down and drops a uniform node without visiting its children. So a node's class must hold for every LOD a leaf inside it can take, from `wantLod` (the finest LOD the selector will ask for inside the node, from its distance, `SplitFactor` and the speed bias) up to its own LOD. `Classify(node, wantLod)` passes `minLod = wantLod`. The pyramid keeps two bound sets per texel, "every LOD" (as today) and "LOD ≥ 3" (no small craters, fine risers or small 3D features); a refined query (the node's own cap) passes `wantLod` directly.

| Layer | Interval over a cap of arc radius ρ (metres on the datum) |
| --- | --- |
| `fbm` octave | `a·clamp(n(centre) ± L·f·ρ', −1, 1)`; `[−a, a]` when `L·f·ρ' ≥ 1` (as now, L = `GradientLipschitz`). For a warped layer the centre sample is `n(c + W(c))` and ρ' = ρ + the warp's variation over the cap, `√3 × min(2·Σ A_w,i, ρ·L·Σ f_w,i·A_w,i)` (corrected in review: W is a vector of three channels, each within ± Σ A_w,i, so its length and its Lipschitz constant both carry √3, and every warp octave counts; the first draft's `min(2·A_w, L·f_w·ρ·A_w)` was short by √3 and by the octave sum, which is not conservative). With today's L the Lipschitz arm is 25 ρ·Σ A_w/λ_w, about 6 ρ under the no-fold rule, so warped octaves resolve 7 times later than plain ones until the tighter constant is certified |
| `ridged`, `billow` | The `\|n\|` interval, then `(1 − \|n\|)²` or `2\|n\| − 1` (monotone in `\|n\|`, as the ridge group now). The octave weight is in [0, 1], so intervals multiply as non-negatives |
| `eroded` octave | Damping is in (0, 1], so `a·[min(nLo, 0), max(nHi, 0)]`; tighter when every coarser octave is resolved and the gradient sum is bounded |
| `cellular` | F1 is 1-Lipschitz and F2 − F1 is 2-Lipschitz in cell units: value at the centre ± ρ/cell (± 2ρ/cell), clamped to its range. Plate tilt: exact inside one plate (F2 − F1 at the centre > 2ρ/cell), else ± the largest edge offset |
| `curve`, `terrace`, masks | Monotone per segment: min and max over the two interval ends and the knots between them. Exact |
| Combines | Sum; product (four corner products); min/max componentwise; `lerp(a, b, m)` is the hull of a and b; smooth min is `min − [0, k/4]` |
| `canyon` | `−D·m·P(x)` with P monotone decreasing in the distance x. The gradient-normalised distance has no useful interval, so: `[−D·mHi, 0]` if the `\|n\|` interval can come within `w × G` of zero (G the field's gradient bound, warp factor included), else exactly 0. The benches do not tighten it |
| `craters` | Per class active at ≥ `minLod`: if the cap covers ≤ 16 of the class's cells, hash them and take the hull of `[−depth_c, +rim_c]` over craters present within reach (often exactly [0, 0]); else `[−depth_max, +rim_max]`. Erasure never widens (`acc·(1 − e)` stays between acc and 0). Classes sum, because craters do nest. Unresolved classes are those under about 4 voxels of radius at the node's LOD (cells of 6 r against a cap 80 voxels across). With `minLod` = the node's LOD that is one class and 1.6 voxels of slack on the low side; with `minLod` = 0 under a LOD-5 node it is five classes and about 3 voxels (100 m). Small against `Pad` (17 voxels); the first draft's "0.75 chunk" was 8 times too high |
| `volcanoes` | Always enumerated (cells ≥ 100 km): `[−caldera, +height]` of the cones within reach |
| `ergs`, `pads` | Ergs: `[0, A_max]` × the mask's upper bound if an erg is within reach. Pads: the hull of `h` and the smooth-channel height over the cap grown by the pad radius |
| `glacier`, `seaClamp` | `max` of intervals; the floor is monotone in the continent value (as now) |
| 3D layers | Leave the `Hbase` interval alone. They widen the shell through `skin`, `rockHi` and `airLo` (next paragraph) |

**Shell classification with 3D layers.** Besides `lo` and `hi` of `Hbase`, the bounds give three more numbers over a cap, for each LOD band (LOD ≥ 0 and LOD ≥ 3), counting only layers with `W3 > 0` in that band and multiplying each layer's reach by the upper bound of its gates over the cap:
- `skin` (corrected in review): how far, **in altitude**, the surface-attached layers can move the base surface either way: `Σ A / InvMin` over `displace3d` layers (4 × A, because the displacement is along the normal and the density is `v × inv` with `inv ≥ 0.25`) plus the largest smooth radius `k`; or the deep layers' `roofM` if larger. Up to 50 m on shattered, 5–15 m typically. Below `lo − skin − pad` the base density is saturated rock and every roof term is exactly 1.
- `pad` in every test below is `Pad(lod) × PadScale3` (§1.6): displacement's divisor stretches the unsaturated band, so the plain `Pad(lod)` is not enough where a `displace3d` can be on.
- **`undercut` is horizontal reach, not skin** (corrected in review). An overhang puts rock in a column up to its offset (≤ 30 m) away from the riser, at any altitude between the riser's bottom and top: up to 250 m above that column's own `Hbase`. So where an `undercut` can be on, `Bounds` grows the cap by `reachH` = the largest offset + the riser band at that LOD before it evaluates `lo` and `hi`. The grown cap holds the riser's own columns, so `[lo, hi]` holds its bottom and top, and no vertical term is needed. A vertical skin of 30 m, as the first draft implied, would class the air beside a cliff as Air with a caprock ledge inside it.
- `rockHi`: the highest altitude at which any layer may leave rock: the largest of `hi + skin`, `hi` + the tallest grounded stamp in reach, and the top of the floating band.
- `airLo`: the lowest altitude at which any layer may leave air: the smallest of `lo − skin` and, for deep layers, the datum's lower bound − the band's depth − the feature radius. Deep layers hang from the datum, not from `Hbase`, so under a mountain this is far below `lo`; that is why it is stored as an altitude.

The pyramid stores all five per texel and band as shorts: 524,288 texels × 5 values × 2 bands × 2 B = 10.5 MB per body that has an octree (was 2.1 MB; the first draft's 5.2 MB counted one band). `PadScale3` and `reachH` are per kind and band, not per texel. Where every column is gated off (sea, shore band, pads, a kind's cave-free regions) `skin` is 0, `rockHi = hi`, `airLo = lo`, and the shell is exactly as thin as today.

```
Classify(node, wantLod):                      wMin, wMax = altitude range of the padded box; pad = Pad(lod) × PadScale3
                                              lo, hi, skin, rockHi, airLo over the node's cap grown by reachH
  wMin > rockHi + pad                 →  Air
  wMax < airLo − pad                  →  Solid
  wMin > hi + skin + pad              →  upper band:  Air   unless a solid layer's box test says it may touch the node → Surface
  wMax < lo − skin − pad              →  lower band:  Solid unless a carve layer's box test says it may touch the node → Surface
  otherwise                           →  Surface
```

- The first two lines are the conservative min/max altitude bands: outside [`airLo`, `rockHi`] no layer can add or remove material, whatever the noise does.
- The band lines stop the thick shell from costing anything where there are no features. Without them a 200 m cave band would turn about six layers of LOD-0 chunks under every cave region into jobs.
- **Box tests** (each conservative, 30–150 ns, only layers with `W3 > 0` somewhere in [`wantLod`, node LOD]):
  - A class of Air or Solid promises **saturated** voxels, not just no crossing, so every test carries the saturation margin `sat = 4.25 voxels + k` at the node's LOD (added in review; the first draft's cave and stamp tests stopped at the surface of the feature and would have classed a node Solid with unsaturated voxels one metre from a cave wall).
  - `tubes3d`: no tube if the channel's interval over the cap stays further from zero than `(r + sat) × G`, G the channel's gradient bound (the distance is `|n| / max(|∇n|, g_min)`, so `|n| > (r + sat)·G` is the conservative form), or if the node's altitude range misses the tube's depth range, grown by `r + sat`, below the datum interval.
  - `caves3d`: no cave if the region gate's interval is ≤ 0, or every presence corner of the 256 m cells the box overlaps is off (≤ 27 hashes for a node of ≤ 256 m), or either field at the box centre is further from its threshold than `G × (half-diagonal + sat)` in field units, warp factor included in G.
  - `stamps3d`: enumerate the class cells in reach, as `craters` does for bounds; test each stamp's bounding sphere grown by its roughening and by `sat` (and down to the ground for grounded shapes, so the top-profile fallback is covered).
- **Why it is safe at every LOD in the range:** `W3` only shrinks a feature, so a feature at a coarser LOD lies inside its LOD-0 extent plus one voxel, and the tests use the LOD-0 extent plus `pad`.
- A false positive (a node called Surface that fills uniform) costs one job that ends at the chunk cull's uniform fill; the shell statistics already count these as empty leaves.
- `top` for horizon pruning becomes `rockHi`. The trivial tests (`GlobalClass`, the first lines of `Classify`) use the kind's global `RockHiMax` and `AirLoMin` in place of `MaxHeight` and `MinHeight`.
- **In the job.** Selection passes the node's band (surface, cavity, sky) to the job. `Generate`'s own early outs widen the same way: `airAbove = hMax + pad + upActive`, `solidBelow = hMin − pad − downActive`, with `pad` scaled by the list's `PadScale3`; the active reaches come from the chunk's 3D list (4 A for displacement, riser tops and bottoms within reach for `undercut`, stamp tops, band depths) and are 0 when it is empty.

Estimate to be measured: with these tests, cave regions add 10–20 % to the LOD 0–2 leaves round the camera (a 12 m tunnel crosses 40–60 LOD-0 chunks per km; 2–4 km of tunnel per km² against about 1,400 surface chunks per km²), and nothing at LOD ≥ 3 except the few large tubes and caverns.

Shell tightness is already the weak spot (README: about 65 % of leaves mesh empty) and steep terrain makes it matter more. Two cheap wins belong with this work: the LOD-aware bounds above, and a certified tighter `GradientLipschitz` (14.73 is loose by an estimated factor of 3–4; prove a smaller constant offline by interval subdivision and pin it with a test). Genuinely steep ground also crosses more chunks per column: expect 1.3–2 times the LOD-0 surface leaves in alpine and shattered regions. That is real geometry, not slack; the arenas (80 + 64 MB) need re-measuring in K1.

**Exact altitude and exact positions.** Unchanged and independent of kinds: `Fill` computes the altitude per voxel with a double `sqrt` at every LOD (stronger than review X1's "LOD ≥ 9"). 3D layers keep the same standard:
- A 3D field is a function of the voxel's planet-frame position in double (double-lattice noise: the cell from `floor(double)` as `long`, the fraction in float). It does not know the chunk or the cube face, so there are no seams and no loss of precision at 1,000 km.
- 3D noise is sampled at the true position `p`, in metres, not at `dir × Radius`: a cave is isotropic and the same size at any depth.
- Column-profile layers use lattice channels (the same bilinear lookup as `h`) and the exact `w`.
- Height is multi-valued only through `Crossings`. Everything that needs one number per column reads `Hbase` or `Htop` (§1.6 table); neither depends on a mesh, a chunk or an edit.

**D30 protected band and sea mask.** Both stay pure tests on the **base height**, which is single-valued whatever the 3D layers do: `protected ⇔ Hbase ≤ sea + ShoreProtectHeight` (the whole column, as now), `sea ⇔ Hbase < sea`. `seaClamp` runs last in the 2.5D stack, so no layer (crater, canyon, caldera) can make dry ground below sea level past the coastal ramp. Inside the ramp a carved feature that dips below sea level is sea by the same mask (a ria or a flooded caldera) and is protected like any shore. With 3D layers:
- The shore rule of §1.6 (rewritten in review) does it in two parts inside the coastal zone (continent value under `InlandStart + CoastGuard`, which holds every sea and protected column with at least 128 m to spare horizontally).
- (a) Every 3D layer's weight is exactly 0 in a voxel whose interpolated `h ≤ ProtectHeight + 10 m`, and ramps to 1 over the next 20 m. So a protected column's density is the 2.5D density, byte for byte: no cave, tube, undercut, displaced face or stamp exists in it, above or below sea level, down to the dig floor. The test is on the voxel's own `h`, the same number the protected mask uses, so the two can never disagree by a lattice cell.
- (b) No carve layer acts below `ProtectHeight + 30 m` of altitude anywhere in the coastal zone. This is what keeps cave air away from sea water at a cliffed coast, where the column next to the sea is 100 m high and (a) alone would let a void sit below the waterline a metre from it.
- A tunnel heading for the coast pinches out in the ramps (`K × (1 − m)` shrinks its radius to nothing). A void stamp is clipped the same way.
- Natural voids below sea level are allowed inland, as dug mines are: the nearest sea column is at least 128 m away through generated rock, and nothing floods (D60: terrain round water is solid; there is no flow model to cross that rock, dug or not). The sea sphere is drawn only in sea columns, and no void exists at or below the waterline within the coastal zone, so sea surface and cave air never meet. A test flood-fills from the sea, through air at or below `ProtectHeight`, to prove it (§7).
- `IsSea` and the other analytic readers use the direct `Height`, the voxel mask uses the lattice `h`; with steep kinds the two differ by up to about 0.2 m (coarse-grid interpolation). The 10 m guard in (a) covers that, and the voxel mask stays the one authority for edits.
- The removal clamp (`d = max(d_op, d_gen)`) uses the generated density copy, which now includes 3D terms; in protected columns that copy has none, so the clamp is unchanged.
- Shore wash and `IShoreDepth` read `sea − Hbase`: unchanged.
- Cost of the rule: sea cliffs are plain 2.5D risers up to 10 m above the protected band (overhangs and crags start above that), there are no sea caves, sea arches or stacks (open question 3), and within the coastal zone, typically 1–4 km from the shore, caves stay above the 30 m line.
- Bodies with no sea have no band, as now. Deep heat and the dig floor read `Hbase`; ground casts above 200 m read `Htop + TopSlack`.

**Impostor.** The bake samples the **base height**, `Height(dir, lod)`, at the LOD whose voxel is a texel (LOD 12 for 256², LOD 10 for the 1,024² focus bake), with the program at that band limit (stamps by a direct cell lookup per class). No 3D layer reaches it: a 3D feature is at most 512 m, so `W3` is 0 from LOD 8 on, and grounded solids have already fallen back to top-profile stamps that are themselves skipped under 2 voxels. So the impostor's height, its limb silhouette and its visible-height rule (`max(Hbase, sea)`) are unchanged and single-valued. 3D layers reach the impostor only as colour: prefiltered `mouth` and stamp coverage in the albedo, and decoration's canopy channel (§6). Shading uses the kind's material rules and prefiltered masks instead of the hard-coded `Land`. The height stays R16 over `[MinHeight, MaxHeight]`.

Between the impostor and the ground (octree LOD 12 down to 0) the rule of §1.6 holds: each LOD's first crossing from above is LOD 0's top envelope band-limited to that voxel, so there is no step in the silhouette at any LOD switch, including the impostor-to-octree switch at about 800 km.

**Edits.** `EditStore` replay works on the density arrays after generation, so ops need no change. What does change:
- Digging into a natural cave, filling one, or bridging a chasm is ordinary Subtract and Add on a density that already holds the void.
- A uniform node an op reaches is generated as now (`EditStore.Reaches`), with whatever 3D layers are active at that LOD.
- The masked generate copies the generated density, 3D terms included, for the removal clamp and for decoration's anchors (terrain-decoration.md reads `PlanetChunkScratch.Generated`).
- `IsFullyProtected(op)` stays valid: its shore test needs every column the op reaches to be protected, and those have no 3D terms; its dig-floor test is safe because no deep band comes within `Pad(0)` of the dig floor (compile rule).
- Snapshots store final density and material, so a compacted chunk keeps its caves. The program hash already locks the kind per universe.
- The edit visibility rule per LOD is unchanged. A dug hole into a cave whose LOD has closed the cave shows as a plain pit, which is what that LOD can show.
- Terrain never falls: rock cut loose by digging stays where it is, as generated floating rock does.
- Cave drills and tunnelling vehicles (D74) reach natural caves through the same land check per stroke.

**Collision.**
- Mesh collision is built from LOD-0 chunk meshes, which carry caves, overhangs and arches as triangles, so `ChunkJob.BuildShape` needs no change. Cavity and sky chunks are ordinary Surface-class leaves: an anchor (64 m round a player, a ship's swept box) loads them like any other, on the server's collision-only octree too.
- Above about 200 m, ground casts, CCD and landing guidance use `Htop + TopSlack`, a conservative roof over arches, needles and displaced faces. The 200 m switch itself is measured from `rockHi`, so a ship near a tall arch or a floating shard gets mesh collision in time.
- A cast that needs the true first crossing (a long shot, an AI line-of-sight test) sphere-traces `Density` (≤ 24 steps, ≈ 0.5 µs each, estimate). Never per tick for many entities.
- Deep heat in a natural cave follows the existing rule (depth below `Hbase`); the compile rule keeps natural caves above the onset depth unless a kind asks for hot ones.
- Server memory: the collision-only octree gains the cavity leaves in reach, an estimated +10–20 % in cave regions (worst case 190 MB becomes about 230 MB at 64 spread-out players).

## 4. Performance

Scalar estimates per LOD-0 lattice point: 12 ns per plain octave, 19 ns with the analytic derivative (`eroded`), 70 ns for a 27-cell `cellular`, about 15 ns per crater class, 20–40 ns compose. A LOD-0 surface leaf has about 2,000 lattice points.

| Kind | Fine work per point at LOD 0 | ns per point (est.) | Lattice ms per leaf |
| --- | --- | --- | --- |
| Today (v1) | 7 fbm octaves | 95 | 0.19 |
| Temperate | In belts: 5 ridged + 2 soil + curves, pads; plains gated to hills + soil | 90–155 | 0.18–0.31 |
| Archipelago | As temperate; seabed below −50 m gated to 3 octaves | 70–150 | 0.14–0.30 |
| Desert | 4 eroded + terrace + canyon walls + dunes in ergs | 120–170 | 0.24–0.34 |
| Alpine | 7 ridged/eroded with derivatives + cliff bands | 190 | 0.38 |
| Volcanic | 5 fbm + 2 ridged + cones; clinker cellular on 30 % of chunks | 115–185 | 0.23–0.37 |
| Ice | 3 fbm + sastrugi; crevasse cellular on 10 % of chunks | 80–170 | 0.16–0.34 |
| Cratered | 4 fbm + 7 crater classes | 155 | 0.31 |
| Shattered | 4 eroded + 4 ridged on walls; plates are coarse | 150 | 0.30 |

**The table is LOD 0 only, and LOD 0 is not the dear case** (corrected in review; the first draft said coarser LODs get cheaper, "60 % at LOD 3, only coarse content by LOD 6"). Per-point work at a LOD is every octave between 2 (or 4) and 256 voxels of that LOD. Today's detail fBm fills that window with 7 octaves at every LOD up to 8, so today's cost per point is flat. The kinds are not flat: at LOD 0 the window (2–256 m) holds only soil and the last one or two mountain octaves, but at LOD 4–7 (window 64 m – 32 km) it holds nearly the whole mountain stack.

| Kind, in a belt | LOD 0–2 | LOD 4–6 | LOD 9–12 |
| --- | --- | --- | --- |
| Temperate: per-point octaves | 1–2 ridged + 3 eroded + 6 soil | 6 ridged + 6 eroded (derivative) + 3 billow; the warp (λ_w ≥ 32 km, second octave ≥ 16 km) is coarse to LOD 5 | 1–3 ridged |
| ns per point (est.) | 90–155 | 230–300 | 40–80 |
| Alpine | 190 | 300–380 | 60–100 |

- A LOD 4–6 leaf in a belt therefore costs 0.5–0.75 ms of lattice, twice the LOD-0 figure, and those rings hold as many leaves as LOD 0 does. The 3,000 m/s case (finest LOD 3) is made mostly of them.
- A warp with λ_w under 256 voxels would add 3 samples per warp octave per point on top; the kinds above avoid most of it by keeping λ_w ≥ 32 km (coarse to LOD 5; at LOD 6–7 it costs 3–6 samples per point, inside the ranges shown). The desert's 6–9 km canyon warp is per point from LOD 3; the compiler prices it.
- The budget below is therefore **per LOD**, and the row SIMD path is needed for the mid LODs from K1, not as a K2 tuning item.
- Ways to hold it if SIMD is not enough: cap the per-point octaves of one layer at 5 by moving the coarse threshold of smooth (unshaped, `fbm`) octaves to 128 voxels; gate uplands and belts so their stacks do not overlap; evaluate `eroded` without the derivative below 8 voxels of wavelength.

The LOD-0 table above is the 2.5D lattice only. 3D layers add the costs below, per chunk, and only where the chunk cull keeps a layer.

**3D cost per sample** (estimates; a padded chunk has 39,304 voxels):

| Work | Cost | Where it runs |
| --- | --- | --- |
| Chunk cull | 0.3–1 µs per chunk | Every chunk of a kind with 3D layers |
| Column-profile layer (`undercut`, `tubes3d`) | +5 ns per lattice point for the host's distance channel; 10–12 ns per voxel inside the band after a 3 ns column reject | Columns within reach of a riser or a tube (typically 10–20 % of a kept chunk's columns) |
| Volume field sample | ≈ 14 ns per octave per grid point (3D double-lattice noise) | The even grid: 6,859 points at step 2 (step 4 fails the error rule of §1.6 for every field in this doc). Budget for all of them: the interval test drops little with today's Lipschitz constant. A displacement octave of 8–16 voxels: per voxel in the surface-band cells |
| Volume warp sample | ≈ 14 ns per channel per octave per point | The 8-voxel global grid: 216 points per padded chunk |
| Volume field per voxel | ≈ 6 ns (trilinear + combine) | Voxels in kept 8³ cells |
| Stamp | ≈ 4 ns per voxel per stamp in reach | Voxels inside a stamp's bounding sphere |

**3D cost per chunk** (LOD 0, estimates):

| Chunk | Share of generated leaves | Cost |
| --- | --- | --- |
| No 3D layer survives the cull | 80–95 % on most kinds | 2.5D cost + the cull |
| Column-profile only (cliff with `undercut`, tube crossing) | 3–10 % | +0.05–0.12 ms |
| Displaced surface (`displace3d`, 2 octaves, step 2) | 5–15 % (to 30 % on shattered walls) | +0.15–0.35 ms at LOD 0. At LOD 1–2 an octave of 8–16 voxels is per voxel in the surface-band cells (about 12,000 voxels × 14 ns): +0.17 ms per such octave |
| Stamps | ≤ 5 % | ≤ +0.1 ms (≤ 8 stamps) |
| Surface chunk crossed by a cave | 1–3 % | +0.15–0.25 ms |
| **Cavity chunk** (below the surface, holds a cave or tube) | An extra 10–20 % of leaves in cave regions, none elsewhere | 0.4–0.6 ms **in all** (corrected in review, was 0.3–0.5): no fine lattice (datum octaves only, ≈ 10 µs); two fields on every point of the step-2 grid, 2 × 6,859 × 14 ns ≈ 190 µs (the first draft's 70 µs assumed 35 % of cells kept, which the Lipschitz test cannot deliver with today's constant, and left the warp out); the warp on the 8-voxel grid, 3 channels × 2 octaves × 216 points ≈ 18 µs; ≈ 6 ns per voxel to combine; runs of solid written by `memset`; then a mesh about the size of a surface chunk's. A warp sampled per field point instead would add 6 × 6,859 × 14 ns ≈ 0.58 ms, which is why it is not |
| **Sky chunk** (floating rock) | Shattered and remnant only, under 1 % | 0.2–0.4 ms in all |

How chunks that cannot hold a 3D feature skip the work, in order: the kind has no such layer at this LOD (`W3 = 0`, a table lookup); the shell never asks for the chunk (classification, §3); the chunk cull drops the layer (gate, band, box test); the per-voxel early outs (`airAbove`, `solidBelow`) and the column reject; the 8³-cell interval test. Each step is exact, so skipping never changes a byte.

**Budgets (Perf-tagged tests, per kind):**
- Lattice at LOD 0: ≤ 200 ns per point mean and ≤ 0.40 ms per surface leaf, scalar. With the row SIMD path: ≤ 80 ns and ≤ 0.16 ms, which is under today's scalar cost.
- Lattice at **every** LOD (added in review): ≤ 150 ns per point and ≤ 0.30 ms per surface leaf with the row SIMD path, measured at LOD 0, 2, 4, 6, 8 and 12. Scalar is allowed up to 400 ns at LOD 3–7 only as an interim before SIMD lands.
- Whole render-only LOD-0 leaf with no 3D layer kept: ≤ 0.9 ms scalar (interim), ≤ 0.7 ms with SIMD (today's estimate). Same ceiling at every other LOD with SIMD.
- 3D, per chunk: ≤ +0.35 ms on a surface leaf with every layer of the kind kept (≤ +0.5 ms at LOD 1–2, where displacement octaves go per voxel); ≤ 0.6 ms for a whole cavity or sky leaf; chunk cull ≤ 1 µs.
- 3D, per flight path: mean leaf cost ≤ +15 % and leaf count ≤ +20 % against the same kind with its 3D layers muted, on the kind's fixed bench path (the mute toggle is in the debug panel, so this is one A/B run).
- `Classify`: ≤ 0.3 µs mean, ≤ 1 µs with every box test. `Bounds`: ≤ 1 µs per refined node.
- `Height(dir)` at LOD 0: ≤ 0.6 µs. `TopHeight`: ≤ +0.1 µs. `Density(p)`: ≤ 1 µs. `Crossings`: ≤ 3 µs per column. `SiteSample`, single point: ≤ 3 × `Height` at that lattice LOD (it must return the chunk's lattice value, so it evaluates the long octaves at the four coarse-grid corners and interpolates, as a chunk does; corrected in review, was "the cost of `Height`"); window form: ≤ 1.2 × `Height` per point.
- A kind that misses a budget does not ship (pillar 1); its layers are thinned or gated harder.

**How the budget is met:**
- **Coarse grid, kept:** everything of ≥ 256 voxels (continent, belts, plates, big craters, volcano cones, erg envelopes) is evaluated at 1/64 of the points.
- **Gates:** after the coarse pass, a chunk computes each gate mask's interval over its cap and drops layers that are exactly off (dunes outside ergs, mountains outside belts, clinker off the flows). Most chunks of every kind run under half its worst case.
- **Stamp cache per chunk:** one cull per chunk fills a `StampList` in the job scratch (classes whose cells are larger than the chunk: ≤ 4 craters each); the three smallest classes use a direct cell hash per point. About 1–2 µs per chunk.
- **SIMD by lattice row:** points along a row share the octave loop. Four points per `Vector256<double>` for position and floor, then integer hashes and float gradients 8 wide, gradients by bit tricks instead of the table. Expected 2.5–3 times scalar. Channels are SoA in the scratch (`[channel][point]`).
- **Only the coarsest LODs get cheaper:** from about LOD 9 the per-point window is above the mountain stack and only coarse content remains. LOD 3–7 are dearer than LOD 0 (see the per-LOD table above).

**At flight speeds** (README M2 step 2, measured): 300 m/s at 100 m runs 1,456 chunk jobs/s; at 0.9 ms that is 1.3 worker cores (about 0.9 at today's estimate), and about 2.0 with alpine's extra leaves, against roughly 7 streaming workers. 3,000 m/s (finest LOD 3) runs 2,164 jobs/s. Those are mid-LOD leaves, the dear ones (table above): about 1.0–1.2 ms scalar in mountain country, 2.2–2.6 cores, or 0.7 ms and 1.5 cores with the row SIMD path (corrected in review, was 0.6 ms and 1.3 cores). Still inside 7 workers, but it is the case to bench first, not the LOD-0 one. The 60 s descent (183 jobs/s) is negligible. Main-thread cost does not change. Selection (1.5–2.5 ms on its worker for 12–16k nodes) may rise with dearer refined bounds and the 3D box tests (only nodes with `wantLod` ≤ 5 near cave or stamp regions run them: an estimated +0.3–1 ms): budget ≤ 4 ms on the worker. A node's class depends only on the node and its LOD band, so it can be cached if this is missed.

With 3D layers at the budget (+20 % leaves, +15 % mean cost) the 300 m/s case goes from 1.3 to about 1.8 worker cores, and alpine from 2.0 to about 2.8, against roughly 7 streaming workers. At 3,000 m/s the finest LOD is 3, where only features of 16 m and more exist: a few large tubes, caverns and stamps, a few per cent of leaves. Flying fast never pays for small caves.

**At system load:**
- Impostor 256² per body: ≤ 0.3 µs per sample at LOD 12 ≈ 120 ms worker (20 ms wall on six face jobs); was ~50 ms. Focus body 1024²: ≈ 2.2 s worker, 0.4 s wall, streamed in on approach as now.
- Height-bounds pyramid: about 0.5 s worker per body (was ~60 ms), 0.7 s with the second LOD band and the three 3D reach values; 10.5 MB (see Memory). Build it only for bodies that load an octree (the focus and up to two secondaries), not for every body in the system.
- Compile a kind: under 1 ms, including the land-bias quantile.

**Memory:** program a few KB per body; scratch grows by the channel arrays (≤ 24 channels × ~5,000 points × 4 B ≈ 0.5 MB per job, 96 jobs ≈ 46 MB worst case; size channels per kind, typical 8–12, ≈ 20 MB). 3D adds per job: up to four volume fields on the even grid (4 × 6,859 floats, 110 KB), a cell-keep bitmap, the per-voxel source byte (39 KB) and a 3D stamp list: about 0.2 MB, 19 MB over 96 jobs. The bounds pyramid grows from 2.1 to 10.5 MB per body with an octree (10 shorts per texel; three bodies: 31 MB). If that is too much, `skin` fits a byte in 0.25 m steps and the LOD ≥ 3 band can drop `airLo` and `rockHi` where they equal `lo − skin` and `hi + skin`: about 7 MB. Chunk and mesh arenas (80 + 64 MB) hold the extra cavity leaves; re-measure with K2.

Profiler rows (Terrain panel): kind id, ns per lattice point, lattice ms per leaf by LOD, layers gated off per chunk, stamps per chunk, empty-leaf share, bounds calls and µs. For 3D: leaves by band (surface, cavity, sky), 3D layers kept per chunk, grid points sampled and cells kept, 3D ms per chunk by layer type, shell false positives (jobs that filled uniform), box tests per selection and their µs.

## 5. Materials and resources

**Voxel materials become data** (`data/materials/voxel/*.json`: id, albedo, roughness, `placeable`, `yields`), as exotic-matter.md already assumes. `VoxelMaterial` stays a byte. `PlanetMaterials`' four fixed slots go. Starting set: granite, basalt, sandstone (two bands), limestone, regolith, sand, gravel/scree, soil, turf, snow, ice, ash, lava crust, clinker, salt, impact melt, plus exotic-matter's `ExoticOre` and the three remnant materials.

**Surface rules.** Each kind has an ordered rule list; the first match wins. Inputs are per lattice point, so the rule runs once per point and `Fill` still only picks by depth: altitude, slope (the `inv` already computed), `|sin latitude|`, any mask channel, and a hashed dither that breaks straight boundaries.
- Steep is rock everywhere (slope > 0.7, as `SteepInv` now); scree on 30–40° below a cliff mask.
- Snow line: altitude above `snow0 × (1 − sin²lat × k)` and slope under 50° (snow does not hold on faces); ice where the glacier mask is on.
- Lava crust and clinker on the flow mask, ash elsewhere on volcanic worlds; a `hot` mask marks active flows (emissive in `terrain.frag`).
- Sand in ergs, salt on playa floors, impact melt on complex crater floors, bright ejecta on the youngest craters, regolith on airless bodies.
- Shore material within `BeachHeight` of the sea, as now.

**Strata.** Below the surface layers, material is a strata table indexed by **absolute altitude** plus a warp channel, not by depth: `strata[floor((w + warp) / thickness) & 7]`. Canyon walls and mesa sides then show continuous horizontal bands, and `terrace` levels are placed on the hard bands, so the cliffs and the rock agree. Chasm and canyon floors expose low strata at the surface without anyone digging 2 km.

**Materials on 3D surfaces.** The surface rules above run per lattice point and describe the open-sky ground. A voxel whose source (§1.6) is a 3D layer picks differently, per voxel, from data on the layer:
- **Walls and ceilings** of a carve layer: the strata table at that altitude, so a cave shows the same bands as the canyon outside it. A layer may set a `lining` material and depth instead (glazed basalt in a lava tube, ice in an ice cave).
- **Floors** of a carve layer (the density gradient within 35° of up): the layer's `floor` material (cave soil, cave sand, rubble, frost) to 1.5 voxels deep. This is the material the GPU ground cover keys on, so moss and cave pebbles need no extra vertex data.
- **Solid stamps:** the stamp's material, or strata by altitude (arches and hoodoos keep the banding; a `cap` takes the hard band it sits in).
- **Displaced and undercut faces:** rock or strata, as steep ground is today; soil and turf never hang under an overhang.
- The `enclosed` bit (§1.6, lighting) is set here.
- Ordinary veins are evaluated in cavity chunks like any other (LOD ≤ 2), so veins crop out in cave walls: prospecting underground works with no extra rule.

**Deposits.**
- **Exotic matter and natural voids:** a lens is dropped where a natural void may touch its bounding box grown by 4 m. The test is the carve layers' conservative **box test** of §3, run once per lens on the server at lens generation (about 60 lenses per planet), not point samples of `Density`, which can step over a 6 m tunnel (corrected in review). It is a pure function of the two seeds, so a restarted server gets the same lens list. So no cave exposes exotic ore for free and the reveal rules of exotic-matter.md (scan, exposure by digging, cue) are untouched. Open question 5 asks whether rare natural exposures are wanted instead.
- **Exotic matter and steep ground** (added in review): exotic-matter.md buries a lens 30–1,500 m "below the local surface". With 1 km canyon walls a lens 100 m under a plateau can lie 10 m behind a cliff face. Depth is therefore measured from the **lowest `Hbase` within the lens footprint grown by 30 m** (sampled on a 4 m grid at lens generation, server only), so no wall, crater or chasm cuts a lens open. The violet surface cue reads `Hbase` as before.
- **Exotic matter:** otherwise unchanged from exotic-matter.md. Lenses come from the server-only `DepositSeed`; ore is derived, never stored, never placeable; `DepositRecord`s reach clients only by scan, exposure or cue. A kind contributes only public multipliers in its `deposits` block: lens density (default 5 per 10⁶ km²), depth range and a `q_b` scale. It never contributes positions. Lenses are still skipped at LOD ≥ 3.
- **Ordinary ores** (public seed): per kind, a list of veins: a host strata band, a 3D ridged-noise threshold and an ore material. Evaluated only in chunks whose altitude range meets the host band, at LOD ≤ 2.
- Kind identity through resources (data, a starting point for the crafting design): volcanic → sulphur, basalt, metal-rich flows; ice → water ice and volatile ices (propellant); desert → salts and silicates; cratered → impact melt metals in crater floors, regolith volatiles; shattered → deep strata ores exposed in chasm walls; remnant → all rock is EM-bearing by depth fraction.

**Impostor colour.** Per texel the impostor runs the same surface rules on the texel's height, slope (from neighbouring texels, as now), latitude and prefiltered masks, and blends material albedos by coverage where a rule boundary is sub-texel (snow line, canyon coverage, ejecta rays). A per-kind 16 × 16 tint table (latitude × altitude) gives biome colour (vegetation bands, dust tone, ice blue). Sea colour stays as now.

## 6. Gameplay hooks

**Landing sites.**
- The `pads` layer makes them by construction: a seeded bench of radius 150–400 m, flattened to one height (the long-wavelength channels at the pad centre), cut into the slope on one side and filled on the other. Inside the core the slope is ≤ 2°, whatever the terrain around it. Presence per 10–25 km cell is a kind parameter, multiplied by an interest mask (belt edges, canyon rims, volcano flanks, crater rims), so pads sit next to the scenery rather than out on the plains.
- Natural flats also register: complex crater floors, caldera floors, mesa tops, playas.
- `LandingSites.Query(dir, radius, Span<Site>)` enumerates site cells near a direction. It is a pure function of the public seed, lives in the Terrain assembly (no Vulkan, so the server and AI ships use it), and costs a few hashes. Consumers: the nav HUD, AI landing, spawn, points of interest. Zero bandwidth.
- Ships near the 1 km cap do not land on pads; that is intended.

**Spawn.** `PlanetSpawn` picks from the site index instead of searching for a coast: the kind's `spawn` rule (e.g. coastal pad under 300 m altitude, `|lat|` < 50°, breathable), scanned outward from a seeded direction. Deterministic, server-side.

**Points of interest.** A `Site` is (type, direction, radius, height, tags). Settlements, outposts, wrecks and mission markers attach to sites; factions holding land (D74) can key ownership to site cells. This doc only provides the index.

**Body data carried by the kind** (resolved per seed into `PlanetParams`, replacing its `BiomeSetId` and `AtmosphereId` sketches):
- `sea`: present or not, level range, target land fraction (the compiler solves `LandBias` as a quantile of the continent channel).
- `atmosphere`: present, surface pressure, scale height (the exponential density profile drag uses), composition tag, `breathable`, and the sky's scattering preset.
- `gravityG`: the range the kind allows. The system generator owns the actual value (1 g on 1,000 km planets is decided); it only assigns kinds whose range fits.
- `climate`: storminess, base cloud, zonal wind scale, precipitation type (rain, snow, dust, ash, none). This feeds `PlanetClimate` in the sphere weather (planets.md D31). No atmosphere means no weather.
- `relief`: min and max height, within D29's −8 … +9 km on 1,000 km worlds and ≤ 0.1 R on small bodies.
- `life`: a chance resolved per body seed into `PlanetParams.Life` (false with no atmosphere). Decoration drops every living species on a dead body.
- `decor`: the decoration set the kind uses.
- Rotation and orbit stay with the frames.

**Landing sites and 3D.** `gate3d` is 0 in a pad core plus 20 m, so a pad is plain 2.5D ground: its height is `Hbase`, nothing overhangs it and no void opens in it. A deep layer may still pass under a pad below `roofM`. Natural sites gain two types from 3D layers: `skylight` (a pit over a lava tube: a sheltered base site) and `cave-mouth`.

### Decoration hooks (terrain-decoration.md)

Decoration is a pure function of seed + position built on the kind's outputs. This is everything it reads; each item is a pure function of the public seed and the program, and none reads edits (decoration applies edits itself).

| Hook | What the kind provides | Notes |
| --- | --- | --- |
| Set and life | `"decor": "<set id>"` and `"life": { "chance": … }` in the kind file. The compiled `DecorProgram` hash joins the kind's content hash | One set per kind |
| Biome channels | A kind may declare named `biome` channels (`moisture`, `temperature`, and any others): `fbm` layers with optional latitude and altitude terms. Surface rules and the tint table may read them too | A set may declare more channels of its own; on a name clash the kind's wins |
| Site-only channels | Channels flagged `siteOnly` (the set's `moisture`, `grove`, …) are compiled into the program but never run by `EvaluateWindows`; only `SiteSample` evaluates them | No cost per voxel column |
| `SiteSample(face, i, j, latticeLod)` and a window form | The kind's per-lattice-point outputs at a named lattice LOD: `Hbase`, `inv` (slope), curvature (second difference), the surface rule's material, `|sin latitude|`, the continent channel, `gate3d`, every exported mask, the biome and site-only channels | Depends only on (face, i, j, LOD, program): the same from any chunk, any LOD and any peer. It reproduces the chunk path exactly, coarse-grid interpolation included (long octaves at the four coarse corners), so a tree stands on the mesh. Cost: up to 3 × `Height` for one point, about 1.2 × per point in the window form |
| Exported masks | Every layer exports its coverage under its name, plus standard sub-masks: `.riser`, `.rim`, `.floor` (curves, terraces, canyons, craters), `.near` (a distance ramp round each stamp, 2.5D or 3D, for talus and moss), `fresh`, `flow`, `ice`, `pads`, `mouth`, `caveRegion`, and `veins.<id>` (a public vein's host band within 8 m of the surface) | Names are the kind file's layer names, as in the surface rules |
| Slope and material | `inv` and the surface material per lattice point (above); per voxel, the material byte with its `enclosed` bit | The GPU ground cover reads the material from the mesh vertex |
| Inside-cave flag | Three forms: the `enclosed` bit on voxel materials; the `roofed` and `enclosed` flags on each `Crossing`; the chunk's band (surface, cavity, sky) and its 3D list on the chunk record | Decoration picks open-sky, roofed-floor or ceiling species from these |
| Surface point and normal on 3D terrain | In a loaded chunk: the density volume itself (decoration scans columns and takes the trilinear gradient; the kind guarantees a clamped distance estimate whose zero set is exact; its gradient magnitude is near 1 on height, stamp and column-profile surfaces and 0.25–3 on noise-carved walls, so decoration must normalise the gradient and must not read it as a distance scale). Without a chunk: `Crossings(dir, lod, altMin, altMax, hits)` returns every floor and ceiling in the column with altitude, normal (6-tap gradient of `Density`), material, source layer and flags | `Crossings` sphere-traces the analytic density: ≤ 3 µs per column (estimate) |
| `Density(p, lod)` | The analytic density at a planet-frame point: the same ops as `Fill`, with `h` evaluated directly | Equal to chunk bytes within 0.35 voxel plus one quantum at LOD 0 (was 0.1: coarse-grid interpolation of a steep octave is worth up to 0.2 voxel and the step-2 volume grid up to 0.3), 1 voxel at LOD ≥ 1; for open-sky tests above a chunk's top and for queries with no chunk. Never where bytes must match |
| 3D reach | Per chunk, `UpActive` of its 3D list (how far above the lattice height something solid can be); per kind, `TopSlack` and `RockHiMax` | Bounds decoration's open-sky scan |
| `stamps3d` preset `blob` | Rock features of 8 m and more are terrain: the layer decoration calls `boulders3d` | Same class grids and band-limit rule as craters |
| Canopy | The surface rule gains a `canopy` slot: at LOD ≥ 3 the evaluator calls the set's `DecorCanopy(site)` and swaps in the set's canopy display material where cover passes a hashed dither. The impostor bake writes the same value into decoration's R8 canopy channel | The call is a pure function of `SiteSample` outputs |
| Exclusions the kind enforces | No decoration input is needed to keep pads clear or the sea empty: `pads` and the sea mask are exported, and `gate3d` already keeps 3D features out of both | |
| Generated density copy | `PlanetChunkScratch.Generated` (3D terms included) whenever edits replay | Anchors never depend on edits |

Identity across LODs relies on one rule from §1.2: a lattice point's value depends only on (face, i, j, LOD) and the program. Decoration always samples at the **class's** lattice LOD, not the chunk's, so a tree seen from a LOD-3 chunk is the tree a LOD-0 chunk makes on arrival.

## 7. Migration

### Steps

3D layers are in from the start; the steps order them by how much of the pipeline each touches.

- **K0, refactor with no visual change.** Build the program, compiler and generic evaluator, **with the 3D phase in place**: the density composition in `Fill`, the chunk cull, `Classify(node, wantLod)` with the band lines, the job bands. Express today's recipe as the kind `legacy-rolling` (no 3D layers) and prove it reproduces v1 bit for bit (golden heights and chunk hashes). Two review changes differ from v1, so the legacy kind carries `"compat": "v1"`, read only by this test: the ridge group interpolated as a shaped sum (§1.2 now interpolates raw octaves) and the hard relief clamp (§1.3 now has a knee). A test-only kind with one layer of each 3D family proves exactness, seams and the shell before any real kind uses them. Record the `GenerationCost` baseline. LOD-aware `Bounds`. All existing tests pass unchanged.
- **K1, the first three kinds, with surface-attached 3D:** `temperate-continental` (the home planet: fixes the flat world the user sees and the flat spawn), `barren-cratered` (the test system's moon; proves stamps), `desert-canyon` (proves curves, terraces, carved level sets, ergs; no sea). 3D in K1: `undercut` (vertical and overhanging cliffs, caprock), `displace3d` (crags), solid `stamps3d` (boulders, arches, hoodoos, ejecta blocks), `Htop`, the top-profile fallback. None of these puts anything below the surface band, so K1 has no cavity chunks. `GeneratorVersion` becomes 2. Data-driven materials and surface rules, impostor shading from rules, `pads` and `LandingSites`, `SiteSample` and the exported masks (decoration's DK0 needs them), the debug viewer. The row SIMD path moves here from K2 (review): the mid LODs of a mountain kind are 2–3 times today's lattice cost without it (§4), and the home planet ships in this step.
- **K2, caves:** `tubes3d`, `caves3d`, void stamps; cavity chunks, the lower-band box tests, the even-grid volume sampling, `Density` and `Crossings`, the `enclosed` flag, cave materials. Karst on temperate, lava tubes on the cratered moon's maria, alcoves on the desert. Gates and stamp cache tuned to budget; `alpine`, `volcanic`, `ice`, `archipelago`; kind assignment in `SystemGenerator`; hand-authored overrides.
- **K3:** floating stamps, sky chunks and the upper-band test; `shattered`, `minor-moon`, `remnant`.

`GeneratorVersion` 1 is retired, not kept as a code path: no persistent universe exists yet (save/load is M4, and `SimWorld` regenerates the test system each launch). From version 2 on, planets.md's lock holds: a universe stores `GeneratorVersion` and each body's program hash, and a kind file is frozen for any universe that uses it. A changed kind is a new kind id.

### Files

| File | Change |
| --- | --- |
| `src/Terrain/Planet/Kinds/PlanetKind.cs` | JSON model and loader (System.Text.Json source generation, load time only) |
| `src/Terrain/Planet/Kinds/PlanetKindCompiler.cs` | Resolve ranges from the seed, flatten, sort, build per-LOD tables, validate (relief sum against the clamp, budgets, a sea kind has a `continent`), content hash |
| `src/Terrain/Planet/Kinds/PlanetKindProgram.cs` | The SoA tables below |
| `src/Terrain/Planet/Kinds/LayerOps.cs`, `LayerOps.Interval.cs`, `LayerOps.Simd.cs` | `Eval`, `EvalInterval` and the row path per layer type |
| `src/Terrain/Planet/Kinds/Stamps.cs` | Class grids, cube rotations, per-chunk cull, crater, volcano, erg and pad profiles |
| `src/Terrain/Planet/Kinds/Layers3D.cs`, `Layers3D.Bounds.cs` | The five 3D layer types: signed distances, column-profile maths, the combine ops, `W3`; chunk cull and node box tests |
| `src/Terrain/Planet/Kinds/VolumeGrid.cs` | Even-grid sampling of volume fields, the 8³-cell interval test, trilinear lookup |
| `src/Terrain/Planet/Kinds/Stamps3D.cs` | SDF primitives, top profiles, 3D stamp lists |
| `src/Terrain/Planet/Kinds/LandingSites.cs` | Site query |
| `src/Terrain/Planet/PlanetSurface.cs` | Becomes the evaluator over a program: `Height` (base), `TopHeight`, `Density`, `Crossings`, `SiteSample`, `Bounds(…, minLod)` with `skin`, `rockHi`, `airLo`, `IsSea`, row evaluation |
| `src/Terrain/Planet/PlanetGenerator.cs` | `EvaluateWindows` calls the row evaluator and writes per-point surface materials; a cavity or sky chunk evaluates the datum octaves only; `Fill` gains the 3D ops, the per-voxel source and the 3D material pick; `Generate` takes the node's band and widens its early outs; lattice, windows, altitude and masks unchanged |
| `src/Terrain/Planet/PlanetHeightBounds.cs` | Two LOD bands per texel, five values each; `Classify(node, wantLod)` with the band lines and box tests; lazy build |
| `src/Terrain.Runtime/PlanetLeafSelector.cs` | Computes `wantLod` per node, passes the band to the job, prunes with `rockHi` |
| `src/Terrain.Runtime/Rendering/TerrainLighting.cs`, `shaders/terrain.frag` | The `enclosed` material bit: no direct sun, low sky ambient |
| `src/Terrain/Planet/PlanetImpostor.cs` | Shade from surface rules and masks; palette from material data |
| `src/Terrain/Planet/PlanetParams.cs` | Gains `KindId`, `KindSeed`, `ProgramHash`, `Life`, `DecorSetId`, atmosphere and climate fields; loses `LandBias`, `MountainHeight`, `PlanetMaterials` |
| `src/Terrain/Planet/PlanetChunkScratch.cs` | Channel arrays, `StampList`, per-point material bytes; the chunk's 3D list, volume grids, cell-keep bitmap, per-voxel source bytes |
| `src/Terrain/Voxels/Noise.cs` | `Gradient3` with analytic derivative, `Cellular3`, row variants, a grid-batch `Gradient3` for the even grid; certified Lipschitz constants |
| `src/Terrain/Voxels/VoxelMaterial.cs`, `shaders/terrain.frag` | Material table from data; palette uploaded, not hard-coded |
| `SystemGenerator` (planned in planets.md), `src/Sim/SimWorld.cs` | Kind assignment; the test system names its kinds |
| `data/planets/kinds/*.json`, `data/planets/assignment.json`, `data/materials/voxel/*.json` | New |
| `tests/Terrain.Tests/PlanetKindTests.cs` | New (below) |

### Structs

```csharp
public enum LayerOp : byte
{
    Fbm, Ridged, Billow, Eroded, Warp, Cellular,          // noise phase: write a channel
    Craters, Volcanoes, Ergs, Pads,                       // stamp phase
    Curve, Terrace, Canyon, Mask, Combine, Glacier, SeaClamp,   // compose phase
    Undercut, Tubes3D, Caves3D, Displace3D, Stamps3D,     // 3D phase (§1.6)
}

public enum Combine3 : byte { Displace, Add, Carve }
public enum ChunkBand : byte { Surface, Cavity, Sky }

/// One body's compiled kind. Immutable; arrays are structs-of-arrays, indexed by octave, layer or class.
public sealed class PlanetKindProgram
{
    // Octaves of every noise layer, sorted by dependency then by descending wavelength.
    public double[] Frequency, OffsetX, OffsetY, OffsetZ;
    public float[] Wavelength, Amplitude;
    public uint[] Seed;
    public byte[] OctaveChannel, OctaveShape, OctaveWarp;   // warp = first of three channels, or 255

    // Layers in evaluation order.
    public LayerOp[] Op;
    public byte[] Dst, SrcA, SrcB, Gate;                   // channel indices; 255 = none
    public ushort[] FirstOctave, OctaveCount, ParamBase;
    public float[] Params;                                 // flat; curves store knots here

    // Per LOD (0..12), built at compile time.
    public ushort[] OctaveEnd;                             // [lod * layers + layer]: octaves the LOD keeps
    public float[] RiserBand;                              // [lod * curves + curve]
    public float[] ClassWeight;                            // [lod * classes + class]: full, softened, bowl only, 0
    public ulong[] LayersAtLod;                            // bit per layer

    // 3D layers in op order (≤ 16 per kind).
    public LayerOp[] Op3; public Combine3[] Mode3;
    public byte[] Gate3, Mask3, Mouths3, Host3, Datum3;    // channel indices; 255 = none; Datum3: 254 = absolute altitude
    public float[] BandLo3, BandHi3, Radius3, Feature3, Smooth3, Roof3;   // metres
    public float[] Amplitude3, Wavelength3; public uint[] Seed3; public byte[] GridStep3;
    public byte[] Floor3, Lining3;                         // voxel materials; 255 = strata
    public float[] W3;                                     // [lod * layers3 + layer]: 1 … 0
    public float[] Skin, RockHiMax, AirLoMin;              // [band]: LOD ≥ 0, LOD ≥ 3
    public int Gate3dChannel, DatumFirstOctave, DatumOctaveCount;
    public float ShoreGuard, CoastRamp, TopSlack;
    public float[] CoastGuard;                             // [lod]: continent-channel margin of the coastal zone (§1.6 shore rule)
    public float[] PadScale3, ReachH;                      // [band]: displacement's pad stretch (≥ 1); undercut's horizontal reach (m)
    public float[] RidgeMean;                              // [lod * layers + layer]: mean of the ridged octaves that LOD skips

    public StampClass[] Classes;
    public SurfaceRule[] Rules;
    public byte[] Strata; public float StrataThickness;
    public int ChannelCount, ContinentChannel;             // −1 with no sea
    public ulong ContentHash;
}

public struct StampClass   // 48 B
{
    public LayerOp Kind; public byte Rotation, Channel, Flags;
    public int CellsPerSide;                               // on the class's rotated cube
    public float RadiusMin, Presence, DepthRatio, RimRatio, Reach, AgeMin, AgeMax;
    public uint Seed;
}

/// Job scratch: stamps that can reach the chunk, culled once.
public struct StampList
{
    public int Count;                                      // ≤ 64
    public double[] Cx, Cy, Cz;                            // unit direction of the centre
    public float[] Radius, Depth, Rim, Age;
    public byte[] Class;
}

public struct SurfaceRule  // 32 B; first match wins
{
    public float AltMin, AltMax, InvMin, InvMax, LatMin, LatMax;
    public byte MaskChannel; public float MaskMin;
    public byte Surface, Subsurface;
}

public readonly record struct Site(byte Type, double3 Dir, float Radius, float Height, ushort Tags);

/// Job scratch: the 3D layers the cull kept for this chunk, and their volume grids.
public struct Layer3DList
{
    public int Count;                                      // ≤ 16
    public byte[] Layer;                                   // index into the program's 3D tables
    public float UpActive, DownActive, PadScale;           // widen the chunk's early outs; PadScale stretches pad (displace3d)
    public float[] Grid;                                   // [field * 6859 + point]: even-voxel grid, step 2 or 4
    public ulong[] CellKeep;                               // bit per 8³-voxel cell
    public byte[] Source;                                  // per voxel: 255 base, 254 displaced, else layer
}

/// One sign change of the density along a radial column.
public struct Crossing        // 20 B
{
    public float Altitude;                                 // m about Radius
    public float Nx, Ny, Nz;                               // out of the rock, planet frame
    public byte Flags;                                     // 1 floor (else ceiling) | 2 roofed | 4 enclosed
    public byte Material, Layer;                           // Layer 255 = base surface
}

/// Per-lattice-point outputs for decoration (§6). SoA in the window form.
public ref struct SiteOut
{
    public float Hbase, Inv, Curvature, SinLat, Continent, Gate3d;
    public byte Material;
    public Span<float> Channels;                           // exported masks, biome and site-only channels, by compiled index
}

// PlanetSurface, added (all allocation-free, thread-safe, pure functions of the program):
//   float Height(dx, dy, dz, lod)                         base height, as now
//   float TopHeight(dx, dy, dz, lod)                      highest solid: Height + grounded stamp top profiles
//   float Density(in double3 p, lod)                      metres, + is air
//   int   Crossings(dx, dy, dz, lod, altMin, altMax, Span<Crossing> hits)
//   void  SiteSample(face, i, j, latticeLod, ref SiteOut site)
//   void  Bounds(dx, dy, dz, angle, minLod, out lo, out hi, out skin, out rockHi, out airLo)
```

No allocation after warm-up: the program is built once at load; jobs write only into their `PlanetChunkScratch`.

### Schema, one full kind

```json
{
  "id": "desert-canyon",
  "schema": 1,
  "requires": { "radiusKm": [300, 1500], "zone": ["hot", "temperate"], "gravityG": [0.3, 1.2] },
  "relief": { "minM": -3000, "maxM": 7000 },
  "sea": { "present": false },
  "atmosphere": { "present": true, "pressureBar": [0.2, 0.9], "scaleHeightM": [9000, 14000],
                  "composition": "co2-dust", "breathable": false, "sky": "dust-orange" },
  "climate": { "storminess": [0.2, 0.6], "baseCloud": [0.02, 0.08], "zonalWind": [0.8, 1.6], "precipitation": "dust" },

  "layers": [
    { "name": "basins",     "type": "fbm",     "out": "base",   "lambdaR": 1.0, "octaves": 5, "amplitude": 1.0, "gain": 0.5 },
    { "name": "plateau",    "type": "curve",   "in": "base",    "out": "h",
      "knots": [[-1.0, -1800], [-0.15, -400], [0.05, [900, 1400]], [0.12, [1300, 1900]], [1.0, [1800, 2400]]] },
    { "name": "mesaMask",   "type": "fbm",     "out": "mesa",   "lambdaM": 90000, "octaves": 3, "amplitude": 1.0, "gain": 0.5 },
    { "name": "strataWarp", "type": "fbm",     "out": "strata", "lambdaM": 24000, "octaves": 2, "amplitude": [20, 60], "gain": 0.5 },
    { "name": "mesas",      "type": "terrace", "in": "h",       "out": "h",
      "levels": { "count": [5, 8], "fromM": 300, "toM": 2600, "jitter": 0.35 },
      "riserM": [60, 220], "riserBandM": 2, "benchSlope": 0.03, "talus": 0.3, "warp": "strata",
      "strength": { "channel": "mesa", "from": -0.1, "to": 0.25 } },
    { "name": "meander",    "type": "warp",    "out": "cwarp",  "lambdaM": [6000, 9000], "octaves": 2, "amplitudeM": [400, 750] },
    { "name": "canyonField","type": "fbm",     "out": "canyon", "lambdaM": [20000, 80000], "octaves": 3, "amplitude": 1.0,
      "gain": 0.45, "warp": "cwarp", "precise": true },
    { "name": "canyonFade", "type": "fbm",     "out": "cfade",  "lambdaM": 150000, "octaves": 2, "amplitude": 1.0, "gain": 0.5 },
    { "name": "canyons",    "type": "canyon",  "field": "canyon", "out": "h",
      "halfWidthM": [700, 1600], "depthM": [500, 1100], "floor": 0.3, "benches": [2, 3], "profile": "stepped",
      "gate": { "all": [ { "alt": [600, 900] }, { "channel": "cfade", "from": -0.2, "to": 0.2 } ] } },
    { "name": "badlands",   "type": "eroded",  "out": "h", "op": "add", "lambdaM": 3000, "octaves": 9,
      "amplitudeM": [150, 300], "gain": 0.5, "damping": [0.6, 1.5],
      "gate": { "channel": "mesa", "from": -0.4, "to": 0.0 } },
    { "name": "dunes",      "type": "ergs",    "out": "h", "op": "add", "cellKm": 300, "presence": [0.25, 0.45],
      "radiusKm": [60, 220], "lambdaM": [90, 260], "heightRatio": [0.06, 0.10], "slipDeg": 33,
      "gate": { "alt": [700, 500] } },
    { "name": "oldCraters", "type": "craters", "out": "h", "op": "add", "radiusM": [64, 8192], "coverage": 0.02,
      "age": [0.5, 1.0], "complexDiameterM": 4000 },
    { "name": "gravel",     "type": "fbm",     "out": "h", "op": "add", "lambdaM": 32, "octaves": 5, "amplitudeM": 1.0, "gain": 0.5,
      "scale": { "channel": "fresh" } },
    { "name": "pads",       "type": "pads",    "out": "h", "cellKm": [12, 20], "presence": 0.5, "radiusM": [150, 400],
      "interest": [ "canyons.rim", "mesas.riser" ] }
  ],

  "layers3d": [
    { "name": "caprock",  "type": "undercut", "host": "mesas",
      "profile": [[0.0, 0], [0.55, [-7, -3]], [0.85, [3, 9]], [1.0, 0]], "hardBands": ["limestone", "basalt"] },
    { "name": "slots",    "type": "undercut", "host": "canyons",
      "profile": [[0.0, [1, 4]], [0.4, [-5, -1]], [1.0, 0]] },
    { "name": "weather",  "type": "displace3d", "amplitudeM": [1, 3], "lambdaM": [16, 32], "octaves": 2,
      "mask": [ "mesas.riser", "canyons.riser" ] },
    { "name": "arches",   "type": "stamps3d", "shape": "arch", "op": "add", "grounded": true, "cellKm": 6,
      "presence": [0.10, 0.30], "radiusM": [15, 60], "thicknessM": [5, 14], "material": "strata",
      "gate": { "channel": "mesa", "from": 0.0, "to": 0.3 } },
    { "name": "hoodoos",  "type": "stamps3d", "shape": "needle+cap", "op": "add", "grounded": true, "cellM": 90,
      "presence": [0.2, 0.6], "radiusM": [4, 9], "heightM": [12, 40], "material": "strata",
      "gate": { "mask": "mesas.rim", "above": 0.4 } },
    { "name": "blocks",   "type": "stamps3d", "shape": "blob", "op": "add", "grounded": true, "cellM": 120,
      "presence": [0.1, 0.4], "radiusM": [4, 14], "gate": { "mask": "canyons.floor", "above": 0.3 } },
    { "name": "alcoves",  "type": "caves3d", "mode": "worm", "op": "carve", "lambdaM": [80, 160], "widthM": [6, 12],
      "band": { "datum": "hLong", "depthM": [10, 120] }, "roofM": 6,
      "regions": { "lambdaM": 8000, "coverage": [0.05, 0.15] },
      "mouths": [ "canyons.riser", "mesas.riser" ], "floor": "cave-sand" }
  ],
  "biome": [ { "name": "moisture", "lambdaM": 60000, "octaves": 3, "gain": 0.5, "altScale": -0.0002 } ],
  "decor": "desert",
  "life": { "chance": [0.1, 0.3] },

  "materials": {
    "strata": { "thicknessM": [40, 90], "bands": ["sandstone-red", "sandstone-pale", "limestone", "sandstone-red",
                                                 "basalt", "sandstone-pale", "limestone", "granite"] },
    "surface": [
      { "when": { "slopeAbove": 0.7 },                  "surface": "strata", "subsurface": "strata" },
      { "when": { "mask": "dunes", "above": 0.5 },      "surface": "sand",   "subsurface": "sand" },
      { "when": { "mask": "canyons.floor", "above": 0.5 }, "surface": "gravel", "subsurface": "sand" },
      { "when": { "altBelow": -300 },                   "surface": "salt",   "subsurface": "sand" },
      { "when": {},                                     "surface": "gravel", "subsurface": "sandstone-pale" }
    ],
    "tint": "desert-warm"
  },
  "deposits": {
    "exotic": { "lensesPerMkm2": [4, 7], "depthM": [30, 1500], "qScale": [0.8, 1.3] },
    "veins": [ { "ore": "salt-ore", "host": "limestone", "lambdaM": 40, "threshold": 0.72 } ]
  },
  "sites": { "natural": ["mesa-top", "playa", "crater-floor"],
             "spawn": { "type": "pad", "altBelowM": 1500, "latBelow": 0.77 } }
}
```

A number given as `[a, b]` is a per-seed range, except where the field is a pair by nature (`alt`, `knots`, `age`, `radiusM`), which the schema lists. `"out": "h", "op": "add"` adds into the height; a `curve` or `terrace` with `"out": "h"` sets it.

`layers3d` is ordered (displace, add, carve unless the file says otherwise). An `undercut` profile is a list of [height fraction within the host's riser, horizontal offset in metres, + out over the air]: `caprock` above cuts a 3–7 m notch under the hard band and lets the cap stand 3–9 m proud. A deep layer's `band` names its datum (`hLong` or `"altitude"`) and depth range; `regions` is its coarse column gate; `mouths` lists the masks where it may break the surface. The compiler adds the shore and pad terms of `gate3d` itself.

### Tests

Quick (run in the normal targeted pass):
- **Legacy golden (K0):** `legacy-rolling` reproduces v1 heights and chunk density hashes bit for bit.
- **Determinism:** same body seed gives the same program hash, heights and chunk bytes; scalar equals SIMD bit for bit; a committed golden hash per kind catches drift across machines and runtimes.
- **Neighbours and seams:** `NeighboursAgreeOnAprons` and `CornerChunksSpanSeveralFaces` per kind, with points chosen on stamp rims, gate edges and cube edges.
- **Bounds by brute force:** `BoundsHoldEverySample` per kind and per `minLod`, with samples at every LOD ≥ `minLod`; plus a unit test per layer type (random parameters, random caps, 10⁴ samples inside the interval).
- **Shell with 3D:** `ShellClassificationMatchesBruteForce` per kind and per `wantLod`: no node classed Air or Solid holds an unsaturated voxel at any LOD in [`wantLod`, node LOD] (the top-down selector depends on exactly this); every box test has its own brute-force test on random boxes; the false-positive share (Surface nodes that fill uniform) is recorded and capped at 35 % of the nodes 3D layers add.
- **Bounds, the cases review found** (each failed on paper in the first draft): a warped layer with three warp octaves at the no-fold limit; an `undercut` ledge over a node whose own columns are all at the cliff foot; a `displace3d` at `cA·A/λ3 = 1` on a 76° face (the stretched pad and the 4 A skin); a gated-off stamp with k = 2 m centred 4.25 voxels above the ground (the `+ k` in K); a node 1 m from a cave wall (saturation, not crossing).
- **3D exactness:** a chunk with a layer culled and the same chunk with the layer forced on write identical bytes (10³ chunks on gate edges, band edges and presence-cell borders); apron agreement between surface and cavity neighbours, across tunnels, stamps, displaced cliffs and cube edges; the volume grid read from either side of a seam is the same points.
- **3D band limit:** for each LOD, the first crossing from above against LOD 0's top envelope box-filtered to that voxel: within 1 voxel + `TopSlack`; void volume never grows with LOD; no void at a LOD where its layer's `W3` is 0; the impostor height equals the base height with every 3D layer muted.
- **Heights and queries:** the true first crossing lies within `TopSlack` of `TopHeight` on 10⁵ columns; `Density` is within 0.35 voxel plus one quantum of chunk bytes at LOD 0; `Crossings` finds the floors and ceilings a voxel scan of the chunk finds, with the same flags; `SiteSample` returns the same values whichever chunk or LOD asks.
- **3D targets** (the 3D "not flat" test, per kind): share of land in cave regions, void fraction inside them, mean tunnel width, share of void volume connected to a mouth (≥ 60 %, so caves can be found and walked), arches and overhang length per 100 km², within bands round the kind's numbers.
- **Edits with 3D:** dig into a cave, fill one, clamp a removal against a generated copy that holds 3D terms; `IsFullyProtected` against brute force on kinds with deep layers.
- **Band limit:** for each LOD, H at that LOD on its lattice against the box-filtered LOD-0 function (8 × 8 sub-samples per cell): RMS error ≤ 0.35 voxel, max ≤ 1.5 voxel. An alias metric: the lattice-Nyquist share of the second difference stays under a per-kind threshold for LOD ≥ 1.
- **D30:** no dry ground below sea level past the coastal ramp (10⁵ samples per sea kind); protected mask equals the base-height test; in protected columns the density equals the same kind's density with every 3D layer muted, byte for byte; a flood fill from the sea columns of 200 coastal chunk blocks, through generated air at or below `ProtectHeight`, never enters a carve void (the first wording, "through generated air", would enter any cave with a mouth); 100 of the blocks are on cliffed coasts, where the first draft's gate failed; no carve-sourced voxel exists below `ProtectHeight + 30 m` in the coastal zone.
- **Sites:** every pad core measures ≤ 2° at LOD 0; `Query` finds every pad a brute-force scan finds.
- **Zero allocation:** `SamplingAndGenerationAreAllocationFree` per kind.
- **Slope targets:** per kind, the §1.5 table from 10⁵ samples (100 m slope percentiles, area over 30° and 45°, 10 km relief), asserted within bands. This is the test that says "not flat".

`[Trait("Category", "Perf")]`:
- ns per lattice point, lattice ms and total ms per leaf at LOD 0, 2, 4, 6, 8, 12 against §4's per-LOD budgets, on a chunk set inside a belt (the mid LODs are the dear ones); the 3,000 m/s bench leg over mountain country; `Height` and `Bounds` cost; impostor bake ms; empty-leaf share on a fixed flight path per kind.
- 3D: ms per chunk by band and by layer type (none kept, column profile, displaced, cave-crossed surface, cavity, sky); chunk cull µs; `Classify` ns with and without box tests and selection ms; `Density`, `Crossings` and `SiteSample` cost; and the A/B of §4 on the kind's bench path: leaves and mean ms with 3D layers on against muted.

### Debug viewer

- `--planet-kind <id> [--kind-seed N] [--kind-radius km]`: replaces the test planet's kind in the existing `--planet-fly` and `--moon-test` harnesses. `--kind-orbit` starts at 2.5 R on the impostor and descends; `--kind-ground` starts on the first landing site.
- `--kind-sheet`: for each kind, the impostor from orbit plus three ground views tiled into one PNG sheet, for judging a kind at a glance and for A/B against the previous sheet.
- `--kind-cave`: starts inside the nearest cave-region mouth to a seeded direction (found with `Crossings`), for judging caves in motion and for the cavity-chunk bench leg.
- ImGui "Planet kind" panel: the layer list with mute toggles (2.5D and 3D, plus "mute all 3D" for the A/B), ns per point per layer, a live slope histogram of loaded LOD-0 chunks against the kind's targets, empty-leaf share, leaves by band, a wireframe of cavity and sky chunks, a column probe that lists `Hbase`, `Htop` and the crossings under the cursor, site markers, and a dev-only hot reload of the JSON (regenerates the planet; never available in a saved universe).

### Open questions

1. **How much cave.** Caves are in (decided). The defaults here: cave regions on 10–20 % of a temperate world's land, systems 10–150 m deep (300 m on alpine), tunnels 6–16 m wide, at least 60 % of void volume reachable from a mouth. Each of these scales the extra leaves near the player (budget +20 %). More and deeper, or about right?
2. **Floating rock.** It is not physically supported, and nothing generated or dug loose ever falls. Proposed: floating shards only on the shattered and remnant kinds (exotic matter as the reason). Wanted on other kinds too, or is that the right limit?
3. **Sea cliffs, sea caves, arches and stacks.** D30 protects every shore column to the dig floor, so this design puts no 3D feature there: sea cliffs are plain risers and there are no sea caves. Solid-only 3D in shore columns (stacks and arches standing in the water, unminable like the rest of the shore) would not open any hole and would fit D30 as written, but shore wash would not see them. Allow that, or keep the shore plain?
4. **Dark caves.** With no scene shadows yet, the `enclosed` flag makes cave interiors ambient-only, so they are dark a few metres in. That needs a suit lamp and ship lights to be playable. Are those in scope soon, or should caves get a faint fill light until then?
5. **Exotic matter in natural caves.** Proposed: lenses avoid natural voids and cliff faces, so a cave or a canyon wall never exposes ore for free. Or are rare natural exposures wanted as a reward for exploring (a visible cue with no scan)? The same question for the **remnant planetoid**: its rock gets purer with depth, so review limited its natural chambers to the top 300 m. Deep chambers would be a shortcut past the 30 km tunnel that exotic-matter.md uses as the gate to crystal. Keep the limit, or allow a few deep ones as prizes?
6. **Far landmarks.** A 3D feature leaves the voxel LODs at about 21 px on screen: a 60 m arch near 3 km, a 100 m floating shard near 5 km. Grounded ones keep their top profile, floating ones vanish. Should features of 64 m and more get an L-class impostor from the decoration pipeline out to 12 km?
7. **Gravity on small bodies.** 1 g on 1,000 km planets is decided. Should moons of 300–1,500 km and minor moons scale down (lower g changes jumping, landing and how tall cliffs feel), or is everything rocky near 1 g? Kinds carry a range either way.
8. **Lava as a hazard.** Is hot lava only a look (emissive material), or does standing on or flying low over active flows heat components and suits through the deep-heat rule? The same question now covers lava tubes flagged `allowHot`.
9. **How common breathable worlds are.** Proposed: one guaranteed in the starting system, otherwise set by the temperate-zone weights (about half of temperate-zone planets). This drives spawn, settlement and the economy more than terrain does.

Revision and review history: [history/planet-kinds.md](history/planet-kinds.md).
