# Crafting and resources core (`src/Crafting`)

Status: built 2026-11-26. Headless library, BCL only (no Vulkan, Engine, Jolt or other projects; `CraftingBoundaryTests` enforces it). Implements the resource model of `docs/pivot/exotic-matter.md` (Resource model, Wormhole additives, EM crystal stability, AM production) and the pricing primitives of `docs/pivot/ship-design-and-construction.md` section 3. All content is data in `data/`; code holds only the formula families. 71 tests in `tests/Crafting.Tests`.

## Units and fixed point

- Mass and content: `long` mg. AM in traps: ng. Energy: J.
- Every fraction (purity, potency, yield, loss, spread, gates): ppm `int` (`Fixed.One` = 1,000,000). JSON stores plain fractions (`0.85`); the loader rounds them to ppm.
- Products go through `Int128`, so gates compare exactly (`Fixed.AtLeast(content, mass, ppm)`) with no rounding.
- Ticks: `tickRateHz` from `data/physics/exotic.json` (60).

## Data formats (all files carry `"version": 1`; anything else is refused)

| Path | Contents |
| --- | --- |
| `physics/exotic.json` | Crafting subset of exotic-matter.md's file: `tickRateHz`, `qMax`, `bands` (`[name, lowerEdge]`, from 0), `mergeBucket`, `deviceGates` (gates of devices not yet in data: slurry 0.17, recirculating 0.25/0.40, field 0.60, throat 0.85), `crystal` {`decayPerDay`, `floor`, `shockLoss`}, `additives` {`maxKinds`, `maxMassFraction`, `fullActiveFraction`, `potencyBucket`, `labPotency`, `crudeLabPotency`, `crudeRateFactor`}, `amPerEm`, `amEnergyJPerKg`, `amProductionJPerKg`, `coreEfficiency`. Other systems may add keys; unknown keys are ignored |
| `economy/pricing.json` | ship-design section 8 verbatim plus `version` |
| `items/*.json` | One item per file: `id`, `name`, `family`, `densityKgM3`, optional `unitMassKg` (discrete items: stacks hold whole units), `antimatterCapacityG` (AM cells), `refCostPerKg`, `content` (`None`/`Exotic`/`Active`/`Antimatter`; default from family: `em`/`em_slurry` → Exotic, `additive` → Active), `metastable` (`"crystal"` or `{decayPerDay, floorPotency}`), optional embedded `recipe` (a Fixed recipe named `make.<id>`, default output 1 kg or 1 unit) |
| `em_additives/*.json` | exotic-matter.md's shape: `id`, `item` {family, density, cost, metastable}, `effect`, `tradeOff`, `recipe` {station, `crude`, `timeSPerKg`, `powerW`, `inputs`}. Makes item + `make.<id>` (lab potency 0.90) and, when `crude`, `make.<id>.crude` (station `<station>.crude`, potency 0.60, time ÷ `crudeRateFactor`). Effect keys are multiplicative factors; keys ending in `Add` sum (`flowBiasAdd`) |
| `materials/voxel/*.json` | `id`, `densityKgM3`, `hardness`, `placeable`, `yields` {`item`, `purityFrom` (`deposit`/`range`), `purityRange` [min, max]}. EM yields need a range |
| `recipes/*.json` | `kind`: `Enrich`, `Blend`, `Fixed` or `AmProduction` (shapes below) |

Recipe shapes:
- **Enrich** (exotic-matter.md's): `input` {`family`, `minPurity`}, `alpha`, `yield`, `loss`, `purityCap`, `throughputKgPerHour`, `powerW`, `spreadOut`, `consumes` [≤ 1 {`item`, `perKgInput`}], optional `amGPerKg`, optional `seed` {`minPurity`, `minContentFraction`, `consumedFraction`}.
- **Blend**: `inputs` [{family, minPurity, maxPurity}, {item: agent}], `agentMinFraction`, `targetPurity`, `output`, `throughputKgPerHour`, `powerW`, `feePerKg`.
- **Fixed**: `station`, `timeS` (per unit) or `timeSPerKg` (per kg of output), `powerW`, `inputs`, `outputs`. Inputs: `{item, kg}` (bulk), `{item, count}` (discrete), `{item, amG}` (AM from cells), `{family, minPurity, maxPurity?, contentKg}` (nominal EM content, gated). Outputs: `{item, count}` or `{item, kg, potency?}`.
- **AmProduction**: `feed` {`item`, `kgPerKg`}; energy per ng comes from `amProductionJPerKg` (2,000 J/ng).

Ids (`ItemId`, `RecipeId`) are dense `ushort`s assigned in ordinal key order, so every machine loading the same data agrees.

## Load-time refusals (`CraftingDataException` lists them all)

Unknown version, unknown item or family, duplicate ids; `qMax` outside (0, 1); bands not ascending from 0 or reaching `qMax`; crystal floor at or above the Crystal band; AM round trip ≥ 1 (free energy); buyback ≥ price, `designStamp` ≤ plan licence. Enrich: `alpha` ≤ 1, `loss` ≤ 0 (loops must be lossy), `yield + loss` > 1, `purityCap` > `qMax`, `minPurity` ≥ `purityCap`, spread outside [0, 0.5]. Blend: target outside (0, `qMax`], empty input range. Fixed: input purity no stock can meet (> `qMax` or > its max), additive output potency outside (0, `qMax`], any EM or AM output (it would bypass refining), kg on discrete or count on bulk items. Materials: a placeable material that yields EM, a source purity above `qMax`. Any derived gate above `qMax`.

## Stacks

`ItemStack` is 40 B: `Item`, `Flags` (Metastable, InStasis), `SpreadPpm`, `MassMg`, `ContentMg` (true), `NominalMg` (shown), `Stamp`.
- **Deviation from the doc's 32 B layout:** nominal content is stored in mg rather than as a Q15 error, so nominal sums conserve exactly on every split, merge and blend (the doc's test asks for that). `NomErr = NominalMg / ContentMg − 1`; assayed ⇔ `SpreadPpm == 0`. The bucket is cached in the inventory, not the stack.
- Split moves content by mass and the nominal error with it (truncated toward zero); the remainder keeps the rest, so sums are exact. Blend/merge spread is the true-content-weighted mean, rounded up.
- **Decay** (crystal 0.01/day to 0.96, seed lattice 0.02/day to 0.45) is lazy: content and nominal are stored at the decay origin; `c(t) = max(floor·m, c0 − rate·m·elapsed)`. In stasis `Stamp` holds the elapsed ticks instead of the origin tick, so powering a case on or off is exact and settles nothing (lazy equals per-tick, tested over 400k ticks with toggles). Merges, splits and shocks settle to the tick. Leaving the band keeps decaying until the floor; at the floor the flag clears for good. A stack becomes metastable when it enters an inventory at or above its band (crystal, mined geodes, T4 output).

## Auto-merge buckets

`PurityRules.BucketKey`, on nominal purity:
- Edges are band edges ∪ `deviceGates` ∪ every recipe gate (Enrich `minPurity` and seed, Blend min/max, Fixed family min/max). With today's data that is exactly the doc's 0.10, 0.15, 0.17, 0.25, 0.30, 0.40, 0.60, 0.85, 0.90, 0.97.
- Above Ore: same 0.005 bucket, same gate segment, same assayed state, same metastable state. A merged nominal purity is a mass-weighted mean inside one interval, so it never crosses a gate (fuzz-tested over 6,000 stacks).
- Ore (< 0.15) merges whole, assayed or not, **except** that the 0.10 gate (T1 input) still splits it: the doc lists 0.10 as an edge and "never below a gate" wins.
- Additives: 0.05 potency buckets. AM cells never auto-merge. Items without content: one bucket.
- Metastable stacks never merge with stable ones of the same purity, so stable throat stock is never dragged into decay.

## Operations

- `Inventory`: SoA, preallocated, capacity in slots, mg and mL (volume = mass ÷ density, rounded up). `TryAdd` (auto-merge), `TryTake`, `TryTakeNominal`, `TryDebitContent` (AM out of cells), `Read(slot, tick)`, `SetStasis`, `Shock`, `Assay`. Hot ops allocate nothing (tested). Emptied slots swap-remove.
- `EnrichMath.Balance`: the doc's formulas in integer mg. Product mass is rounded up so purity never exceeds `q_out` or the cap; then `m_in = m_p + m_r + c_burnt` and `c_in = c_p + c_r + c_burnt` exactly. Gates check nominal purity; the cap is also checked on true purity (the maths needs `q_in < QCap`). Unassayed: product and residue keep the input's error, or the recipe's seeded error (`spreadOut · (2·hash01(seed, batchId) − 1)`) when larger; spread is the max of both.
- `ProductionQueue`: min-heap by `(EndTick, Id)`, payload outputs computed at start, starved ticks per station applied lazily on pop. No per-tick cost, no allocation.
- `Production.StartEnrich / StartBlend / StartFixed`: check everything first (gates, solvent, AM, seed, inputs, queue room), then debit and schedule; a refusal spends nothing. T4 takes a seed ≥ 2 % of the batch's nominal content at ≥ 0.97, consumes 10 % of it, returns the rest with the product. Fixed recipes draw family EM from the lowest-purity qualifying stack first. `Deliver` adds outputs (new crystal starts decaying there).
- Slurry blend: agent = max(enough to reach `targetPurity`, enough for `agentMinFraction`). With 0.40 the fraction usually dominates (0.30 stock → 0.18 slurry, inside the slurry drives' 0.10–0.25 window). Fee is data only.
- `AdditiveMath.Check`: ≤ 2 kinds, Σ additive mass ≤ 10 % of X, gate `X ≥ MinPurity·(m_EM + Σ m_add)` on nominal (plan) or true (charge start); effects `min(1, f/FullActive)`. `Factors` blends effects and trade-offs (shared keys multiply).
- `AntimatterProduction.Step`: banks integer J, produces `floor(J / 2000)` ng with the remainder kept, hydrogen 1:1 by mass (drawn in whole mg, credit kept), stops on full traps or no feed (bank drops to its remainder). `Ticks` schedules a factory batch; `RoundTrip` = 0.2.
- `Pricing`: catalog wholesale/retail, premade, job shop, self-assemble, self-produce, raw materials, stamp, NRE, tooling, licences, buyback, fleet average, stamp-vs-licence check. Tests rebuild the doc's drive and Wren tables from the primitives.

## Known gaps and doc notes

- The doc's tables round intermediate values; part rows match exactly, composed routes within ±2 cr. The self-produced drive row is internally about 1.5 cr inconsistent (unit 2,373 implies N = 5 of 2,627, the doc prints 2,629). Drive self-assembly consumables are not stated; 14 cr reproduces the 2,802 unit.
- Not here (owned elsewhere or later): device gates and `FeedMax` interlocks, warp fuel mixer and runaway, traps and `PowerNet`, factory cores, mining yield (`Deposits` in Terrain), assay as a timed service, faction availability, market prices, batch power curtailment beyond the starved counter.
- Dilatant's "Stable threshold 0.6 → 0.7" is stored as `stableThresholdMul` 1.1667; wormholes.md owns effect semantics and may rename keys.
