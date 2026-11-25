# Exotic matter and antimatter

Status: design only. Nothing has been built or run, and no repo files were changed. Written 2026-11-25 against README.md, warp.md, wormholes.md, frames-and-zones.md, planets.md and ship-hulls.md. **Revision 2** (2026-11-25) adds five user decisions: runaway bursts, antimatter made in industrial facilities, two warp drive kinds, pirate kit, and station anchors. They are listed under "Revision 2 notes". A skeptical review of Revision 2 corrected several formulas, numbers and exploits in place; those corrections are listed under "Revision 2 review notes". **Revision 3** (2026-11-25) follows the wormhole stability decisions (wormholes.md revision 3) from the exotic-matter side: a decay floor and a purity cap for crystal, wormhole additives, stabilisers and what they burn, anti-graviton pulses that drain wormhole stability (replacing the stall), and which devices draw a wormhole's retaliation inside a tunnel. They are listed under "Revision 3 notes" at the end. **Revision 5** (2026-11-25) applies the user's round 5 answers (D9, D12–D14, D20–D25, D28): a separate inertial dampener, the runaway override kept, core ejection as the main runaway counterplay, multiple drives whose bubbles support each other, very long-lived black holes with deliberate collapse equipment, EM supply from planets and asteroids (remnants rare), natural < warp < manufactured travel costs, lingering radiation, anchors on any grid that can crawl, and pull-only tractors without AM. They are listed under "Revision (round 5) notes" at the end and win over any older text; a skeptical review aligned them with warp.md, frames-and-zones.md and ship-design (see "Round 5 review notes"). Every number is a knob and every cost is an estimate to measure. Where this doc changes another doc, the change is listed under "Changes to other docs". Review.md's rulings still win where they apply.

## Recommendation in one paragraph

**Exotic matter (EM)** is one item family with a continuous **purity** `q ∈ [0, QMax]`, where `QMax = 0.995`: complete purity is never reached. A stack stores its **mass and its content** (`ContentMg`, the EM-bearing part). Purity is content ÷ mass. Named grade bands are used for display and for each device's purity gates. Every amount is **content**: 1 EM = 1 kg of content, the unit wormholes.md already counts in (`Xk = 10 EM`). Stacks from crude refineries are **unassayed**: their shown (nominal) purity can be off from the true purity by up to a stated spread. Only the server knows the true value. An assay removes the error.

**Where EM comes from (decision D22):** black holes make EM, but there is no flood of it. All the EM that earlier generations of black holes made has long since been **recycled into planets and asteroids**, the way uranium came from earlier stars. Those deposits are the **main supply**: planet rock at 0.02–0.2 % and asteroids at 0.3–3 %, poor but everywhere. A living black hole yields 40–99 %, but only to the most advanced harvesters. Black holes **last a very long time** (decision D21), so their collapse into a small **remnant** planetoid (EM-bearing rock all the way down, poorest at the surface, richest at the core, crystal geodes at the centre, diggable whole) is a **rare, late event**, not a supply line. Equipment can deliberately force a collapse sooner, at great cost (see Deliberate collapse).

Purity is raised in steps by **recrystallisation**. Ore first goes through a concentrator. Refining is one data-driven recipe family: each tier multiplies the odds of purity by a factor α, loses some yield, and leaves a residue that can be recycled. The top tier needs **seed crystals**, and the first seed crystals can only come from a black hole. Crude refineries anyone can build are cheaper, but they lose more yield and their output is unassayed. Crystal outside stasis decays, but only down to a **floor** (0.96, still throat grade), never to nothing.

**Additives** are a crafting and trading layer on top: up to two additive items, together at most 10 % of the exotic matter's mass, can be mixed into a wormhole generator's throat-grade charge for specific effects (longer life, a wider mouth, flow bias or reversal, masking, faster charge, a bigger stability reserve), each with a trade-off. Additives count as impurity against the generator's purity gate, so heavy doping needs purer EM.

**EM + antimatter (AM) → anti-gravitons**, at a fixed 0.005 kg of AM per kg of EM content. **EM driven by bus power alone emits only gravitons** (decision D28): it can pull, never push. Every device that manipulates gravity runs on one of the two, and each one has a minimum purity:
- **warp bubbles**, in two drive kinds (several drives on one ship form one supporting bubble, decision D14):
  - **slurry drives** burn a premixed slurry of crude EM and a mixing agent, all of it consumed. They are heavy per jump and can never run away.
  - **recirculating drives** dilute fuel-grade EM on board and recover about 95 % of the agent. They use about 3–5× less mass per jump and cost less to run, but cost more to build;
- gravity plating (floor gravity);
- the **inertial dampener**, a separate device (decision D9): it keeps everything inside its bubble moving with the ship, burning field-grade EM and bus energy (graviton emission, no AM) up to its rating; beyond the rating, occupants and cargo feel the excess;
- tractor beams: **pull only** on graviton emission; pushing needs the AM reaction;
- wormhole throats, catchers and natural redirects (high grade);
- tidal compensators and gravity dampers;
- **station anchors**, which hold a grid fixed in a planet's rotating frame at any altitude. Any grid can carry one (decision D25), but it engages only once the grid has reached the required position and, mostly, the required velocity. An anchored grid can crawl slowly to a new pose over days through the anchor's inputs. Anchors never attach to asteroids (tractors do that). Their cost grows with mass × the acceleration the anchor must supply to hold the grid co-rotating; it is zero at the equatorial synchronous radius.

The warp interdiction emitter stays power-only, as README and wormholes.md decided. So do wormhole **stabilisers** (battleship kits and support-ship projectors that feed a wormhole's stability reserve, or drain it in reverse): they pour bus power into a throat that already exists rather than making anti-gravitons, so they burn AM only through the annihilation cores that supply GW-class power (about 5 mg/s for a 2 GW feed). Their crafting needs a throat-grade graviton lens.

**Runaway:** a recirculating drive can run away when EM purer than its window reaches the reaction. That happens through a deliberate overfeed, off-spec unassayed stock, or a diluter breached by damage. The crew get a 3 s alarm, then an anti-graviton burst wrecks the ship and pulses its surroundings through the shared `PulseKick`. The reaction chamber is very highly rated, so the no-warning chamber rupture is rare, and **the entire core can always be ejected** (decision D13): core ejection is the main counterplay, with purging the line as the other. The owner can also deliberately override the interlock as a scuttle or kamikaze (decision D12), with its safeguards kept. Slurry drives can't run away, whatever they burn; neither can recirculating drives whose feed is truly (assayed) low fuel grade, at most 0.42.

**Antimatter is made only in power-hungry industrial facilities:** 2 TJ per gram. The round trip back through an annihilation core returns 20 %. AM is stored in traps and burnt in several places:
- annihilation cores, the only GW-class power on a ship;
- plasma and thermal thrusters;
- the anti-graviton reaction;
- weapons (torpedo warheads and the annihilation lance).

Annihilation radiation is dosed through the hull's occupancy shielding: the flares' `ShieldTable` for sources outside the ship, plus a baked internal path from each onboard source. Crude or lightly shielded use irradiates the crew and anything nearby. Radiation is both transient and lingering (decision D24): breaches and long exposure leave contamination that keeps dosing, short-term exposure can be treated with medicine, and long-term exposure causes health effects that need expensive treatment.

**Power** means generators feeding a per-ship bus, with capacitors on that bus.

An **anti-graviton charge** (the crude mix) makes a gravity pulse:
- the same Δv for every body in its radius, so damage comes only from the gradient;
- a short-lived warp-interdiction field;
- a **stability drain** on nearby wormholes (`BombK = 1 GJ` per kg of yield, with falloff outside a tunnel and at its point-blank value inside one), rung down over 5 s so that feeders can answer it. If it empties a wormhole's reserve, the wormhole collapses early, 1 s after the zero.

The stall of revision 2 is gone: wormholes.md revision 3 made `TClose` a paid maximum rather than a guarantee, so a bomb fight is now an energy fight. Feeders at the cap, overcharge, gravity dampers over the mouths (station cores, so at stations) and a cheap re-establish are the defence. An unfed r = 100 m wormhole falls to a 10 kg-yield charge at point blank; the same wormhole overcharged and fed at the cap survives 22.5 kg. Bombs, drive runaways, black-hole collapses and wormhole flushes all go through one shared `PulseKick`, which has a per-body kick budget.

**Inside a tunnel** (wormholes.md, Tunnels), anything projecting gravitons or anti-gravitons outside its own hull draws a retaliation strike from the wormhole: tractors, gravity dampers, armed charges, destabilisers, emitters and catchers. Plating, the inertial dampener and tidal compensators act inside the hull and are never struck. Each function core's data says which kind it is.

Everything runs on events or at 10 Hz (1 Hz for dose):
- Ship upkeep (power, consumables, factories) is one staggered ledger at 10 Hz.
- An anchor's rate is constant for its fixed pose (and per crawl segment while it crawls), so its reserve is settled lazily and its warnings and failure are scheduled events. An anchor adds nothing per tick; an unattended anchored grid that is not crawling is `Frozen` and costs no physics.
- The inertial dampener's EM and energy are accumulated in the force pass with thrust (a few ns per grid) and debited in the 10 Hz ledger.
- Batches, runaways and collapses are scheduled events. Crystal decay is evaluated lazily, and stops at the floor.
- Stabiliser feeds are ordinary bus draws in the same ledger; wormholes.md integrates the reserve from slope events. Additives are drawn once, with X, at charge start.
- The only per-body costs at 60 Hz are a few nanoseconds of thrust accounting, plus tidal torque and an integrated bubble origin near a black hole.

## User decisions this doc implements

**First draft:**
- Black holes create EM. Deposits also exist in asteroids and on planets, but are less pure.
- Accessible black holes are rare and dangerous, and only the most advanced players can harvest them directly. Every black-hole harvesting tier here needs T3+ components, and the crystal tier needs T4.
- A black hole eventually collapses into a small rocky planet, which can be harvested in its entirety (`DigDepth = Radius`).
- Pure EM is an impossible purple crystal, refined by recrystallisation.
- Low-quality EM is warp fuel. Warp mixes AM with low-grade EM, diluted on purpose if needed, to make a bubble that compresses space.
- EM + AM → anti-gravitons, which is how gravity is manipulated.
- A crude EM + AM mix is an anti-graviton bomb. It may disrupt active wormholes and warp. (The first draft proposed a stall plus a warp field; revision 3 replaces the stall with a stability drain that can collapse a wormhole early.)
- Annihilation energy can drive a plasma thruster.

**Revision 2:**
1. **Runaway burst.** Dumping pure (high-grade) EM into a warp drive can cause a catastrophic, uncontrolled anti-graviton burst that probably destroys the ship. Damage can cause it too, for example a breached diluter. Ships running only low-grade fuel may have no dilution step, so they are not vulnerable in the same way.
2. **Antimatter source.** AM is created in a power-hungry process at industrial facilities. It is used in weapons and in more conventional antimatter thrusters. Annihilation makes high-frequency radiation, so use can't be too crude: it needs shielding mass, and crude or lightly shielded use irradiates the crew and surroundings. This reuses the hull occupancy shielding built for solar flares.
3. **Drive classes.**
   - Low-quality warp drives take a slurry of EM and a mixing agent straight from a tank. The agent is consumed: heavy, poor mass efficiency, no dilution stage, no runaway.
   - High-quality drives mix on board with an agent that is mostly recirculated, for much greater mass efficiency.
   - Higher-grade EM is slightly more available, and the high-quality drive is **cheaper to run** but more complicated to set up. The drive and its supporting systems cost more (capex against opex).
   - The choice is mostly a faction decision about what they can get their hands on.
4. **Pirates use what they can get:**
   - crude refinery systems: poor yield and inconsistent purity, which raises runaway risk in recirculating drives;
   - mixed fleets of both drive kinds;
   - even two warp drives in one ship (since round 5, D14: several drives whose bubbles support each other).
5. **Planets rotate.** Stations can use a **station anchor**. It consumes EM, plus AM through the reaction, to keep the station in one place above a body where an orbit may not normally be possible, which simplifies low stations.

**Revision 3** (the wormhole decisions A–H; this doc carries the exotic-matter side, wormholes.md the rest):
- **A, energy fight:** a wormhole has a stability reserve. Holders feed it with power; bombs and disruption drain it; at zero the wormhole collapses early. The paid lifetime is a maximum, not a guarantee. A fleet can re-establish a just-collapsed wormhole faster and cheaper, and can power through a bombing. Here: anti-graviton pulses drain stability (the stall is removed), and the re-establish's EM and AM.
- **B, holders:** battleships carry full stabiliser kits for their own jumps; dedicated support ships project stabilisation to hold smaller ships' wormholes. Here: what stabilisers burn and what they are made of.
- **C, natural level and throughput:** natural wormholes return to a natural energy level; matter passing through drains stability for every wormhole. Here: what the throughput drain costs in power and AM.
- **D, E, G (tunnels, interiors, collapse flow):** wormholes.md only. Here: what tunnel discharges and collapses do to EM and AM devices on board.
- **F, combat in tunnels:** anything projecting gravitons or anti-gravitons outside the ship draws a retaliation strike; gravity plating is fine. Here: which devices count.
- **H, crystals and additives:** crystal decays only down to a set floor, not to nothing. Complete purity is not expected (a cap below 1.0). Additives can be mixed into throat-grade EM for specific wormhole effects (duration, mouth size, flow bias or reversal, signature masking, charge speed, stability), each with trade-offs: a crafting and trading layer.

**Round 5** (the user's answers to the consolidated open questions; these win over any older text here):
- **D9, interior gravity:** plating gives gravity; a **separate inertial dampener** keeps everything inside its bubble moving with the ship, at the cost of energy, using EM and energy up to a limit (beyond its rating, occupants and cargo feel the excess). See Gravity plating and the inertial dampener.
- **D12, deliberate runaway override:** yes (scuttle or kamikaze), with its safeguards kept.
- **D13, runaway severity:** the reaction chamber is very highly rated, and the **entire core can be ejected**; core ejection is the main counterplay.
- **D14, several warp drives on one ship:** their bubbles **support each other**; the resulting quality is an average weighted by the power each provides (replaces "only one spools").
- **D20, AM energy and production scale:** fine for now.
- **D21, black-hole lifetime:** they naturally last a very long time, but equipment can deliberately make them collapse sooner.
- **D22, remnant abundance:** no flood. All existing EM is assumed recycled into planets and asteroids (as uranium came from earlier stars); remnants are rare, late events, not the main supply.
- **D23, travel economics:** natural wormholes are the **cheapest**, manufactured wormholes the **fastest**, and warp is for going somewhere specific, relatively close by, cheaply. This replaces the blanket "warp cheaper, wormholes costlier".
- **D24, radiation:** both transient and lingering. Short-term exposure can be treated with medicine; long-term exposure causes health effects that need expensive treatment.
- **D25, anchors:** usable on any grid, but activation needs the required position and (mostly) velocity first; anchored grids can move slowly over days through anchor inputs; no anchoring to asteroids (tractors do that); a dropped station is a serious, very uncommon disaster that mostly burns up in the atmosphere or hits the ground hard; stations should carry thrusters (in case they need to fly) and escape pods.
- **D28, tractors:** without AM a tractor can only emit gravitons, so it can only **pull**; pushing needs anti-gravitons, so AM.

## Resource model

### Purity: continuous, with bands

**Recommendation: continuous purity.** Fixed tiers were rejected for three reasons:
- dilution and blending are mass-weighted averages, which tiers can't represent;
- refining yields fractional gains;
- deposits vary smoothly in space.

Bands exist for the UI and for device gates only.

| Band | Purity q | Look | Used for |
| --- | --- | --- | --- |
| Ore | < 0.15 | grey rock with violet veins | Feedstock only (concentrate first) |
| Crude | 0.15–0.40 | dull violet grit | Slurry feed (≥ 0.17), anti-graviton charges, feedstock |
| Fuel | 0.40–0.60 | violet glass | Recirculating-drive feed (safe from runaway up to a true 0.42, so only when assayed), feedstock |
| Field | 0.60–0.85 | translucent purple | Recirculating feed (lighter per jump, runaway-prone), plating, inertial dampeners, tractors, tidal compensators, gravity dampers, station anchors, T3/T4 refining consumables |
| Throat | 0.85–0.97 | clear purple | Wormhole generators, catchers (establish, capture), gates, natural redirects, graviton lenses for the `HorizonHarvester`, heavy scoops and stabilisers. The base for additive blends (0.89 or purer for a 5 % dose, 0.93 for the full 10 %). Refused by recirculating interlocks |
| Crystal | 0.97–0.995 (`QMax`) | the "impossible" purple crystal, faint glow | **A crafting material, not a consumable:** T4 seed crystals, crystal graviton lenses for capital generators and redirect catchers, and the crystal fines in the Stability additive |

- **A device consumes content at or above its `MinPurity`.** Purer stock can be used anywhere a lower band is accepted, with one exception: recirculating warp drives also have a **maximum feed purity** (`FeedMax`) enforced by an interlock (see Warp fuel).
- **Crystal is never burnt in bulk.** Demand for it is finite (seeds, lenses and Stability additive), plus decay outside stasis, which stops at the floor (see EM crystal stability).
- **The horizon harvester uses a throat-grade lens on purpose.** Crystal first comes from horizon harvesting, so a harvester that needed a crystal lens could never be built.

**The purity cap (decision H).** Complete purity is not expected. `QMax = 0.995` is a global data value:
- the loader refuses any recipe whose `QCap` exceeds it, and any source (deposit, geode, harvester) whose purity range does;
- `odds(QMax) = 199`, so the integer odds maths never approaches its guard;
- the UI shows purity to three places and never shows 1.000.

**What the top band means.** Within Crystal (0.97–0.995) purity buys **margin, not power**, in v1:
- **time as crystal outside stasis:** at 0.01 a day, a 0.995 stack stays Crystal for 2.5 days and reaches the floor after 3.5; a 0.975 stack leaves the band in half a day. Shock costs 0.02 per event, so a purer stack survives one or two hits that would drop a geode-grade (0.97) stack out of the band;
- **eligibility:** seeds and lenses need q ≥ 0.97 **at the tick they are used or crafted**, so the margin is what carries crystal home;
- **price:** markets quote crystal by purity, because the margin is time.
- Seeds of any purity in the band grow the same T4 product, and a crafted lens's stats do not depend on the crystal's purity. Whether lens grade should scale with purity is open question 15.

### Stacks, blending, assay

- **A stack is** `(ItemId, MassMg, ContentMg, Stamp, NomErr, Spread)`.
  - Content is stored, so every blend, split and debit conserves content exactly in integers.
  - True purity is `ContentMg / MassMg`, shown at q16.
- **Nominal versus true purity (unassayed stock):**
  - `NomErr` is the hidden relative error of the shown content: `nominal = true · (1 + NomErr)`. `Spread` is its public bound, `|NomErr| ≤ Spread`. A negative `NomErr` means the stock is purer than it looks.
  - Proper refineries, mining and assayed stacks have both at 0. Crude refineries set `NomErr = Spread · u`, with `u = 2·hash01(ServerSeed, BatchId) − 1`.
  - Clients only ever see the nominal content and the spread, shown as "unassayed ±15 %". The true content is server state.
- **Devices debit true content.** A stack that overstates its purity runs a device for less time than its owner predicted.
  - Gates (`MinPurity`, `FeedLo`, `FeedMax`) accept stock by nominal purity, because that is all the client can check.
  - The only place the error has safety consequences is a recirculating drive's diluter (see Runaway burst).
- **Assay:**
  - An `Assayer` station core (50 kg/h, 0.1 MW) or a portable ship core (1 kg/min) sets `NomErr` and `Spread` to 0. The owner then sees the true purity.
  - It is a service at faction stations. Pirates often skip it.
- **Auto-merge buckets:**
  - All Ore-band stacks of one item merge, because everything below 0.15 is concentrated anyway.
  - Above Ore, stacks merge automatically when their nominal purities fall in the same 0.005-wide bucket **and** they are both assayed, or both unassayed.
  - A merge of unassayed stacks keeps exact contents. The merged `NomErr` is recomputed from the nominal and true sums. `Spread` is the mean of the inputs' spreads weighted by **true** content, computed by the server and rounded up; the merged error is the same true-content-weighted mean of the inputs' errors, so it stays a valid bound.
  - Bucket edges sit on every band edge and every device gate. The loader derives them from data; today they are 0.10, 0.15, 0.17, 0.25, 0.30, 0.40, 0.60, 0.85, 0.90 and 0.97. An auto-merge can therefore never push content below a gate it used to pass.
  - Without buckets, every mining op makes its own stack, and the inventory fragments into thousands of them.
- **Blend** (at a Blender station, a slurry blender, or inside a recirculating drive's diluter):
  - sums mass and content (true and nominal);
  - **mixing agent** (`agent.mix`) is a non-EM fluid made cheaply from ice and common rock flux (reference 0.02 per kg). It replaces the first draft's "carrier";
  - **slurry** (`em.slurry`) is an EM-family item whose mass includes its agent. Its purity is the slurry purity, and it never merges with dry EM;
  - diluting with crude EM is the same operation as diluting with agent, but it adds content.
- **EM must not share a room with antimatter outside a reaction chamber** (see Hazards). A recirculating drive's line and chamber count as a reaction chamber.

### Refining: one `Enrich` recipe family

Separation and recrystallisation are modelled as odds-ratio enrichment, which is how real separation cascades are modelled:

```
odds(q) = q / (1 − q)
q_out   = min(QCap, odds⁻¹(α · odds(q_in)))        requires MinIn ≤ q_in < QCap
c_p     = Y · c_in                                  product content;  m_p = c_p / q_out
c_r     = (1 − Y − λ) · c_in                        residue content (λ = burnt loss)
m_r     = c_r + [(m_in − c_in) − (m_p − c_p)]       residue mass = its content + the impurities removed
time    = m_in / Throughput                          one batch, event-scheduled
```

- **Inputs at or above `QCap` are refused.**
  - Otherwise a recipe could lower purity. For example, crystal fed to T2 would come out at 0.85.
  - It would also make `m_r < c_r`, a residue purer than 1.
  - With `q_in < QCap` and `α > 1`, `q_out ≥ q_in`, the removed impurity is never negative, and the residue purity is always below the input's.
- **Residue** can be fed back into a lower tier. The burnt loss λ keeps every loop lossy.
- **Fixed point:** content and mass are in `long` mg, and odds are integer math with a guard near q → 1. The server is authoritative; clients only preview.
- **Unassayed inputs:** the recipe runs on true content. The product inherits the input's error, combined with the recipe's own spread as `max`.

| Station (function core) | α | Y | λ | MinIn | QCap | Throughput | Power | Output spread | Extra inputs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Concentrator T0 | 50 | 0.85 | 0.02 | 0 | 0.20 | 2 t/h | 0.2 MW | 0 | none |
| Recrystalliser T1 | 3 | 0.85 | 0.03 | 0.10 | 0.60 | 50 kg/h | 0.5 MW | 0 | cryo-solvent 0.5 kg/kg |
| Recrystalliser T2 | 4 | 0.80 | 0.03 | 0.30 | 0.85 | 20 kg/h | 5 MW | 0 | solvent 0.5 kg/kg |
| Graviton recrystalliser T3 | 6 | 0.75 | 0.03 | 0.60 | 0.96 | 5 kg/h | 50 MW | 0 | solvent; AM 0.05 g/kg (a controlled-gravity growth field) |
| Seeded crystalliser T4 | 10 | 0.70 | 0.02 | 0.90 | 0.995 | 0.5 kg/h | 200 MW | 0 | AM 0.5 g/kg; seed crystal (q ≥ 0.97) of at least 2 % of the batch content, 10 % of the seed consumed. Output goes straight into a stasis case |
| **Crude still T1c** (pirate kit) | 2 | 0.70 | 0.08 | 0.10 | 0.55 | 40 kg/h | 0.3 MW | 0.15 | none |
| **Crude recrystalliser T2c** (pirate kit) | 2.5 | 0.65 | 0.08 | 0.30 | 0.80 | 15 kg/h | 3 MW | 0.20 | solvent 0.8 kg/kg |

- **T3's cap (0.96) sits just below Crystal.** Crystal can only be grown at T4, and T4 needs seeds.
- **The decay floor equals T3's cap.** Decayed crystal is ordinary throat-grade stock: a generator burns it, an additive blend welcomes its headroom, and one T4 pass (odds 24 × 10 → 0.996, capped at 0.995) regrows it, at T4's 70 % yield. Every recipe's `QCap` is at or below `QMax`.
- The first seeds come only from black holes: horizon harvesting, or the core of a remnant. That is the "only the most advanced" gate. Once seeds exist they are traded like anything else.
- **The crude cores need no licence.** They are crafted from T0/T1 parts and can be mounted on ships. See Pirates and improvised kit.

**Typical chain** (1 kg of content at the start, proper refineries):

| Step | Purity | Content left |
| --- | --- | --- |
| Planet ore, mined | 0.0006 | 1.00 kg |
| Concentrator ×2 | 0.029 → 0.20 | 0.72 kg (crude) |
| T1 | 0.43 | 0.61 kg (fuel) |
| T2 | 0.75 | 0.49 kg (field) |
| T2 | 0.85 (capped) | 0.39 kg (throat) |
| T3 | 0.96 (capped) | 0.29 kg |
| T4 | 0.995 | 0.21 kg (crystal) |

Residues bring the overall recovery back to roughly 0.3–0.35. With crude cores, the same 0.72 kg of crude becomes about 0.50 kg at nominal 0.33 after T1c, and about 0.33 kg at nominal 0.55 after T2c, unassayed.

**Throughput is the faucet, not the deposit size.**
- One concentrator takes 2 t/h of ore. At planet purity that is about 1.2 kg of content an hour; at asteroid purity (0.01), about 20 kg/h.
- Deposits are large, so EM supply scales with the stations and digging players build, which is what these knobs tune.
- The mining rate is the other half of the faucet. Planets.md does not define a drill rate yet; it is a knob that belongs to the tool data.

**Batches run on events.**
- A batch is stored as `(station, recipe, start tick, end tick)` in a min-heap, and completion is popped on its tick.
- A batch needs power for its whole length. The 10 Hz ledger counts the ticks on which the `PowerNet` starves the station.
- When a batch is popped with uncounted starved ticks, it is re-pushed with `EndTick += starved` (lazy, no decrease-key). This stays deterministic.

## Sources of exotic matter

### Planet deposits (terrain voxel material)

- **Origin (decision D22):** planet and asteroid deposits are EM recycled from long-dead black holes, the way uranium came from earlier stars. Together they are the **main EM supply for the whole game**, not just the early game: every grade above Ore is refined from them, and black holes and remnants add only the rare top end (first seeds, crystal) and occasional local rushes.
- **Material:** a new data-defined voxel material `ExoticOre` (`data/materials/voxel/exotic_ore.json`).
  - **Ore is never stored. It is derived.** A voxel is ore when its material is the generator's own rock (not a placed material) and it lies inside a lens's vein field.
    - Purity is a deterministic function `q(p)` of the lens that contains `p`.
    - Chunk snapshots (planets.md compaction) store the generator material, and ore is re-derived at load.
    - So snapshots sent to clients carry no hidden ore.
  - **Placed materials never yield EM.** Add brushes may only place materials from a `Placeable` list, which never includes ore or remnant materials. Placed rock carries a distinct material id that is never re-derived as ore.
    - Without this, "mine ore, refill with cheap rock, mine again" would print EM.
  - Mining an ore voxel yields an `em` stack: rock mass (2,800 kg/m³ × the removed fraction) with content `q(p) × mass`. It is assayed by definition.
- **Deposit lenses** come from a **server-only `DepositSeed`** per body (as with `NaturalSeed`), so clients can't data-mine ore from the universe seed:
  - about 5 lenses per 10⁶ km² (about 60 on a 1000 km planet), Poisson on the sphere;
  - depth 30–1,500 m below the local surface, skewed deep;
  - ellipsoid half-axes 10–80 m (median 20 m), at a seeded rotation;
  - inside a lens, ridged 3D vein noise above a threshold fills 5 % of the volume with ore;
  - `q(p) = q_b · (1 + depth / 1 km) · (0.5 + vein(p))`, with the surface reference `q_b` log-normal over 0.0002–0.002 (median 0.0006);
  - surface cue: lenses shallower than 200 m tint the surface material violet within 150 m (shading only, no geometry).
  - **Per lens:** about 1,700 ore voxels, about 4,700 t of ore holding about 2.8 t of content. **Per planet:** about 170 t of content, half of it below 500 m.
- **Generator integration (`PlanetGenerator`, step 4 "Materials"):**
  - Each body keeps a lens list hashed by LOD-10 cell (32 km), built at system load (about 0.1 ms).
  - A chunk does a hash lookup (about 50 ns). Only chunks whose AABB meets a lens AABB evaluate the ellipsoid and vein noise for voxels below the surface. That adds about 0.05 ms to roughly 0.01 % of chunks.
  - Lenses are skipped at LOD ≥ 3, because they are underground and never visible there.
- **Mining yield:**
  - When the server applies a Subtract `BrushOp`, the chunk job sums the removed volume per material, plus the content `Σ q(p)·mass` for ore voxels. It also flags newly exposed ore for the privacy rule below.
  - It uses the before and after density of the collision-ring chunks. Chunks the op touches outside that ring are generated on a worker for the yield.
  - About 20–100 µs per op, on a worker. The yield lands in the miner's inventory the next tick.
- **Privacy:**
  - Clients receive a `DepositRecord` (about 40 B: lens id, shape and `q_b`) only when:
    - a scan reveals the lens;
    - an edit exposes one of its voxels;
    - they come within 2 km of a surface cue.
  - Client meshes paint ore only for known lenses. Before that it shows as ordinary rock, which is harmless because it is underground.

### Asteroids

**Decision (user): asteroids carry deposits.** They are procedural belts of small voxel bodies, instantiated on demand and on rails. Asteroids are the main mid- and late-game EM source (decision D22: recycled ancient EM, not remnant debris): about 15× planet purity, and no gravity well to climb out of. They can come after M7; planet lenses carry the early game. **Station anchors never attach to asteroids** (decision D25: a minor body has no rotating frame to hold in); a grid that wants to stay with an asteroid holds it with tractor beams, or holds station on thrusters.

- **Belts:**
  - `AsteroidBelt` (data or system generator): a torus about a star or planet (a range, inclination spread, density), split into **bands of equal `a`**.
  - Every asteroid in band k shares `a_k` and its mean motion, so the band's cell pattern **turns rigidly**: there is no Keplerian shear between cells, ever.
  - e and i are bounded so that each epicycle (`a·e`, `a·sin i`) stays within its cell plus one neighbour. Asteroids in a cell are spaced at generation so their epicycles never overlap.
  - Cell lookup at tick t is O(1): `(band from r, sector from θ − n_k·t, z slab)`, plus the neighbouring cells.
- **An asteroid** is `AsteroidRef(belt, cell, index)`, with everything seeded: its Kepler elements, radius (5 m to 2 km, power law), shape seed and composition class.
  - Shapes come from the universe seed, which all clients have. Composition comes from `DepositSeed` and is revealed by scan or exposure.
- **Instantiation:**
  - Asteroids within interest of a player become `MinorBody` entries: a `BodyInertial` frame on Kepler rails, **not rotating**, with no SOI and no gravity (a 1 km body has about 1e-4 m/s²).
  - Pools: 256 on a server, 32 on a client. An entry is released 10 km after the last anchor leaves.
  - **A bubble at an asteroid uses a new `BubbleAnchor.MinorBody`:** its origin rails are the asteroid's and never refit away from it. It rebases only by whole-chunk offsets, so the asteroid's chunks stay static in Jolt. A plain FreeFall bubble would refit its origin and drag the chunks.
- **Voxels:**
  - The same octree and mesher, with the root sized to the body (LOD ≤ 7 for 2 km).
  - Density is a 3D SDF: `|p| − R·(1 + shape(dir))` plus crater dimples, with no height field.
  - Low octaves go on a trilinear 17³ grid and only 3 octaves run at full rate: about 1 ms per leaf (3D noise), against about 0.6 ms for planets.
- **Composition:**
  - 2 % of asteroids in ordinary belts carry EM. Their 3D lenses fill 1 % of the body with ore at q 0.003–0.03 (median 0.01).
  - A 100 m-diameter EM asteroid holds about 15,000 t of ore and about 150 t of content.
  - **Belts near a black hole, and remnant debris arcs,** are 30 % EM-bearing, with q 0.02–0.3.
  - The same derived-ore and placed-material rules as planets apply.
- **Edits:** `EditStore` keyed by `AsteroidRef`. Persistent, like planets.
- **Whole-body harvesting (later):**
  - Asteroids under 30 m in diameter can be tractored whole into a refinery ship's intake.
  - The intake turns the body into inventory stacks and removes it from the belt for good; the removal is stored in the `EditStore`.
- **Cost:**
  - Zero when unloaded.
  - About 100 ns of rails per instantiated body per tick, on demand.
  - About 5 µs per lookup when an interest region moves.

### Black holes

**New celestial kind.** `CelestialKind.BlackHole` is a `CelestialBody` on Kepler rails with a `BodyInertial` frame, an SOI and patched conics, as usual. It has **no surface frame** until it collapses, so a station anchor can't engage there.
- Each slot's black-hole and remnant frames are preallocated at system load, up to `MaxRemnants`, so `FrameId`s stay stable.

**Scale (planetary-mass, "primordial"):** `μ` is 1e13–4e13 m³/s², about Mercury's mass. The physical horizon is sub-millimetre; gameplay uses these radii:

| Radius | Default (μ = 2e13) | Meaning |
| --- | --- | --- |
| `KillRadius` | 2 km | The drawn "horizon". Anything inside is destroyed and removed. Rails entities whose periapsis is inside it get a scheduled destruction event at the crossing tick (computed when their elements change, about 50 ns) |
| `InfallRadius` | 64 km (`RemnantRadiusMax + ReliefMax + 2 km`) | Public. Destroyed at collapse. Wormhole occupancy core (see Wormhole suppression) |
| Tidal death (unprotected) | about 40 km | `2μL/d³` over a 100 m hull reaches about 60 m/s², the default `TidalLimit` |
| Horizon harvesting band | 15–40 km | Circular speed 23–37 km/s; period about 4 s at 20 km. About 16 km/s of Δv down from the disc edge and about 13 km/s to escape back out |
| Accretion disc | 200–2,000 km, thin slab | Heat zone (corona rule) plus dose `doseDisc · (1,000 km / d)²` Gy/s. Seeded EM debris on rails |
| Jets | ±8° about the spin axis, to 10⁶ km | Permanent dose cones, `doseJet · (1,000 km / d)²` Gy/s, using the flare dose code with a fixed cone |
| `RouteShell(BH)` / exit exclusion | `1.1 × DiscOuter` = 2,200 km | Warp routes avoid this sphere (warp.md `RouteShell`), and exits inside it are refused. This is a hazard exclusion like the kill sphere, not an atmosphere floor (D16 removed those for bodies with air): a 2 km/s real-space leg into the disc or the tidal zone is a death sentence, not a choice. Wormholes' body core for a black hole is `InfallRadius + r`, not `MouthFloor` |
| SOI at 30 AU | about 8.4e6 km | Ordinary Laplace SOI |

**Physics.** It uses the zones scheme, with four additions:
- **Tidal bubble cap.** The per-awake-body delta `m·(g(p) − g(origin))` is exact in double, but near the horizon the gradient `2μ/d³` reaches 5 s⁻² (at 20 km). Bodies 500 m apart then separate at 2.5 km/s². That breaks three things:
  - the free-fall bubble's premise of small relative velocities;
  - its rebase rule (more than 1 km or 50 m/s would fire every tick);
  - Jolt's maximum linear velocity, which defaults to 500 m/s and clamps silently.

  So within the gradient zone, the bubble radius is `clamp(20 m/s² ÷ (2μ/d³), 50 m, 2 km)`. That is 50 m (one ship per bubble) inside about 100 km, and the full 2 km beyond about 200 km. Merges respect the cap, and contacts across bubbles use the existing proxies. `ApproachPredictor`'s `MaxAccel` adds the tidal bound `2μ/d³ · r` across the pair.
  - Symplectic Euler stays stable while `ω_tidal·dt = sqrt(2μ/d³)/60 < 2`: about 0.02 at 30 km and 0.04 at 20 km.
- **Integrated bubble origins near compact bodies.** `KeplerOrbit` rounds the period to whole ticks. At a 240-tick period, that rounding makes the origin's rails acceleration differ from `g(origin)` by up to about 0.4 %, while `g` itself is 50,000 m/s² at 20 km. That would put a spurious ~200 m/s² on everything in the bubble.
  - Where `2μ/d³ > 1e-3 s⁻²`, a FreeFall bubble's origin is therefore stepped in double, with the thrusting rails integrator and the same `g()` as the force pass (about 30 ns per bubble per tick).
  - It is refit to rails only after it leaves that zone. Planets are unaffected: a low orbit there has an error of about 1e-5 m/s².
- **Gravity-gradient torque:** when `2μ/d³ > 1e-3 s⁻²` (inside about 340 km of a μ = 2e13 black hole), the force pass adds `τ = 3μ/d³ · r̂ × (I·r̂)`, about 20 ns per body. Near a black hole ships swing radial; without the torque they wouldn't.
- **Tidal stress (1 Hz, grids within 100 × the tidal-death radius):**
  - `a_tid = 2μ·L/d³ · (1 − f_comp)` over the hull's long axis L.
  - Above the hull material's `TidalLimit` (data: `data/ship-materials`), damage brushes accumulate at a rate proportional to the excess.
  - Grid interiors add the tidal delta per awake body, so crew feel the stretch: at 20 km, a 50 m offset is 25 g.
  - The compensator cancels the stress and the interior stretch. It does not cancel the trajectory divergence of separate bodies.
- **Hazards:** disc heat, disc and jet dose (the shared `RadiationDose` path, with `ShieldTable` attenuation), tidal stress, the kill sphere, and debris at km/s relative.

**Wormhole suppression:**
- **Core term.** For a black hole, the Occupancy "body core" radius is `InfallRadius + r` (64 km + r), not `R + MouthFloor + r`.
  - With a hazard-sized floor (2,200 km, warp's `RouteShell(BH)`) the core would cover the whole disc, so no capture could ever open there.
  - `InfallRadius` also keeps every mouth out of the volume the remnant will occupy, whenever the collapse falls.
- The existing `s_tidal` already makes the black hole hard out to about 1,390 km for r = 100 m (874 km for r = 25 m).
- Add two **Natural** hard terms: inside the **disc slab**, and inside the **jet cones**.
- They are Natural, so **interdict catchers waive them** (and `s_tidal`). A captured end can open in the disc or a jet, but never inside 64 km. That is the deadly-but-valid trap wormholes.md intends. Occupancy terms are unchanged.

**Placement and rarity:**
- There is one **black-hole slot** per system, from the server-only `NaturalSeed`.
- Where a system's slot holds one, it sits in the outer system, at 20–30 AU. There are several Solar roots and no cross-system warp; only natural pairs link systems. That is about 50–75 min of warp at the default `VCap` (C class), and about 8 h with a D drive.
- **The "Dark Gate":** one natural wormhole pair has an end about 50,000 km from the first incarnation, far end in this or another system (same system: warp.md's price rule applies).
  - It is heliocentric and co-orbital with the black hole, with an inclination and eccentricity offset so that its MOID to the black hole's orbit is about 50,000 km. An identical orbit would have MOID 0 and fail natural-mouth validity.
  - It lies outside both jet cones.
  - It is fixed for the save, so the collapse never moves it. After the collapse it serves the remnant.
  - Later incarnations in the same system are reached by warp (open question 1).
- So access is a long warp or a scheduled, contested natural crossing.
- The black hole is public (it is a body). **Its collapse tick is not.**

**Harvesting tiers** (all need T3+ crafted cores, so direct harvesting is late game by construction):

| Tier | Method | Where | Needs | Yield | Main danger |
| --- | --- | --- | --- | --- | --- |
| H1 | Jet skimming | Jet sheath, 5,000–50,000 km up the axis | Heavy `MagneticScoop` (T3 core, throat-grade lens), `RadiationShield`, plasma drive | EM dust (q 0.4–0.7) at 1–4 kg/h of content. No antimatter (Revision 2: AM is made only industrially) | Dose |
| H2 | Disc mining | Disc, 200–2,000 km | Tractor, heat shielding, about 5 km/s of Δv | Rock debris of 2–10 m diameter on rails, with EM inclusions of 1–3 % of mass at q 0.5–0.85, taken whole by tractor. A 5 m chunk is about 180 t of rock and about 3 t of content | Heat, dose, debris at km/s |
| H3 | Horizon harvesting | 15–40 km | `TidalCompensator`, `HorizonHarvester` core (throat-grade graviton lens), heavy hull, about 16 km/s down and 13 km/s back out | **Crystal**, q 0.95–0.99, 0.2–1 kg/h of content, delivered into a stasis case | The compensator starves → tidal break-up in a few seconds |

H3 is where the first seed crystals come from.

### Black-hole collapse into a remnant

**Everything is deterministic from `NaturalSeed` and the tick.** The slot's incarnation k has `BirthTick_k`, `CollapseTick_k = BirthTick_k + Life_k`, and its orbit.
- **Life (decision D21): very long.** The natural life is 5–20 years of server ticks (knob `BlackHoleLifeDays`, default 1,825–7,300 days), longer than most saves run, and game time does not advance while a server is empty. Single player uses the same ticks. In practice a black hole collapses when a player **forces** it (see Deliberate collapse); the natural collapse is a backstop that keeps the seeded schedule deterministic.
- **Rebirth:**
  - Incarnation k+1 is born `RebirthDelay` (14 days) after the collapse, at a new seeded outer-system orbit.
  - That orbit is chosen with MOID clearance from every body, remnant and natural mouth.
  - **`BlackHoleBirthForecast` goes out 7 days ahead**, with the orbit and the public `InfallRadius`.
  - At birth, everything within `InfallRadius` of the new black hole is destroyed, as at collapse. Without the forecast, a black hole could appear on top of a player station.
- **Remnants stay,** up to `MaxRemnants` (8); the slot then goes dormant (open question 1). Frames and the 64-body rails budget are finite. With natural lives of years and forced collapses costing a faction-scale effort, a system gains a remnant rarely (decision D22): a few per save, not one a quarter.
- **The forced collapse** replaces `CollapseTick_k` with an earlier tick (below); everything after it (rebirth delay, forecasts, the next incarnation) follows from the new tick exactly as from a natural one.

| Phase | Ticks | Effect |
| --- | --- | --- |
| Stable | Birth … Collapse − 7 d | as above |
| Forecast | Collapse − 7 d | `BlackHoleForecast` to every client in the system ("instability", ± 12 h). Persisted and replayed to late joiners |
| Waning | Collapse − 24 h | Exact countdown. Jet dose and jet yield ×4 (the gold-rush window) |
| Collapse | `CollapseTick`, tick step 3, after wormhole flushes | below |
| Remnant | from then on | A small voxel body, harvestable in its entirety |

**At `CollapseTick`** (server). Clients learn the tick at −24 h and get the remnant parameters in `BlackHoleCollapse`.
1. **Infall:** every entity within the public `InfallRadius` (64 km) of the centre is destroyed. They had 7 days of warning.
   - Wormhole mouths are never inside it (core term). Natural mouths and wormhole frames are exempt.
2. **Shock:** a `PulseKick` with `R = 50,000 km` and `Δv = 100 m/s · (1 − d/R)²`, outward, inside the shared kick budget.
   - Bodies in bubbles are kicked in their own scene.
   - Rails entities get the Δv added to their state vector.
   - Skipped: `Warp` and `Throat` placements, mouth frames (player and natural), and wormhole frames. Moving a rails mouth would move an announced B, which wormholes.md forbids.
3. **μ drops** to the remnant's (`g_s·R²`, about 4.7e9 m³/s² for 40 km at 0.3 g).
   - Every rails entity in the SOI re-derives its elements from its kicked, unchanged state vector, in the same pass as step 2 (one solve, about 200 ns each).
   - Bubbles refit their origins.
   - Most orbiters become hyperbolic and leave at km/s, which is correct.
   - Kicked rails entries refresh their pose-cache entries for this tick.
   - Rails mouths keep their announced elements and period until they despawn (at most `TEnd`, wormholes.md revision 3: a tunnel's mouths outlive `TClose` while its collapse runs). They are kinematic and must not move.
   - 10k entities ≈ 2 ms, once per collapse (years apart, or a forced collapse; D21). Run it as a job announced at −24 h if that spike matters.
4. **Frames:** the kind changes to `Remnant`, and the preallocated `BodySurface` frame activates (slow spin). The SOI shrinks (about 2e5 km at 20 AU); membership sorts itself out at 10 Hz. From here on station anchors can engage over the remnant.
5. **Debris arc:** the disc becomes an EM-rich `AsteroidBelt` arc on heliocentric orbits co-orbital with the remnant (±10° of mean anomaly, 30 % EM-bearing). It uses the rigid-band scheme above.

**The remnant body (`RemnantParams` from `hash(NaturalSeed, slot, k)`):**
- **Shape:** radius 20–60 km. No atmosphere, no sea. Relief ±2 km.
- **Gravity:** surface gravity 0.15–0.4 g (knob, "degenerate EM"). At 40 km and 0.3 g, escape velocity is 485 m/s.
  - This is a gravity knob only. **Mined voxels use the data density (3,000 kg/m³),** never the 2.6e5 kg/m³ the gravity implies. Otherwise one voxel would yield 260 t.
- **Interior gravity:**
  - Planets dig only 2 km down, so a point mass is fine there.
  - The remnant is dug to the centre, where `μ/r²` would diverge.
  - Inside `R`, gravity is therefore `μ·r/R³` (uniform sphere). `Gravity` takes a body flag `InteriorModel`.
- **`DigDepth = Radius`:** the whole body is diggable to the centre. `PlanetHeightBounds` classifies interior chunks as Solid, so nothing is generated until an edit reaches them.
- **Material is a function of the depth fraction `u = depth / R`:**
  - every generated voxel is EM-bearing rock, mined directly as an `em` stack;
  - `q(u) = 0.08 + 0.80·u²`, ±0.03 vein noise. That gives Ore (0.08) at the surface, Crude by mid-depth, Field by u ≈ 0.8, and about 0.88 at the core;
  - the inner 20 % holds crystal geodes at q 0.97–0.99 (seeds). Crystal in the ground does not decay; its stamp is set at the mining tick;
  - three generator-only data materials: `RemnantRegolith`, `RemnantMantle`, `RemnantCore`. Placed rock never yields.
- **Scale:** it is effectively unlimited (about 1e14 t of rock for a 40 km body). Digging rate, refinery throughput and the 30+ km tunnel to the crystal are the gates.
  - **Economy consequence (decision D22: rare, late, local):**
    - one surface cubic metre (3 t × 0.08) holds about as much content as a tenth of a planet lens, so a remnant is a local rush for whoever reaches it;
    - remnants are rare (years of natural life, or a forced collapse that costs a faction-scale effort), so they never replace planets and asteroids as the supply. They make Crude and Fuel cheap near them for a while and give a second, contested source of seeds and crystal at the core.
- **Multiplayer:** a server-wide event. The forecast, countdown and collapse are reliable broadcasts to the system. Clients get the remnant seed only at collapse, then generate its terrain like any body.

### Deliberate collapse (decision D21)

Black holes last a very long time on their own; equipment can make one collapse sooner. It is a T4, faction-scale project with public warning and real consequences.

- **Equipment:** the `CollapseInducer`, a T4 function core (capital ship or station class, about 2,000 t with its shielding), crafted with a crystal graviton lens (5 kg). It projects anti-gravitons into the horizon from the harvesting band (15–40 km), so its host needs a `TidalCompensator`, a heavy hull and everything H3 needs.
- **What it burns:** throat-grade EM (q ≥ 0.85) plus its AM through the reaction, plus bus power:
  - each black hole has a destabilisation budget `CollapseWork` (data, default 2,000 kg of EM content, so 10 kg of AM at `AmPerEm`, scaled by `μ / 2e13`);
  - an inducer delivers at most `InduceRateKgPerH` (data, 20 kg of content an hour, 0.1 kg of AM, plus 2 GW of bus power), so one inducer needs about 100 hours of station-keeping at the horizon and several work in parallel. The AM alone is about four months of an L factory or about 12 days of the system's foundry: a coalition's stockpile;
  - accumulated work **relaxes** at `CollapseRelax` (data, 2 % of the budget an hour) whenever no inducer is feeding, so an interrupted attempt drains away and defenders can stop it by breaking the inducers.
- **Public signature:** an active inducer is an `External` projector with a 50,000 km signature, and once accumulated work passes 25 % of the budget a `BlackHoleInduction` progress event goes to every client in the system (persisted, replayed to late joiners).
- **When the budget fills:** the server sets `CollapseTick = now + 24 h` (never later than the natural tick), and the normal Waning phase runs: exact countdown, jet dose and jet yield ×4, then the collapse as above. The 7-day forecast is replaced by the induction progress events and the 24 h countdown, which gives everyone within `InfallRadius` a day to leave.
- **Consequences:**
  - the owners of every inducer that contributed are **attributed** for the collapse (infall destructions, shock damage, a lost H3 crystal source), in proportion to the work each delivered. Faction standing, fines and PvP rules read that attribution (the same hook as dose attribution);
  - the black hole's H1–H3 harvesting ends; a remnant, a debris arc and a rebirth 14 days later at a new orbit follow exactly as for a natural collapse;
  - inducers inside `InfallRadius` at the collapse tick are destroyed like anything else, so the inducing fleet must leave in the countdown.
- **Determinism:** induction is integer kg of content and ticks on the server; relaxation is settled lazily on each feed change, and the fill tick is a scheduled event. No per-tick cost.

## Antimatter

### Production: industrial facilities (decision)

**AM is made, not found.** The only source is a power-hungry production process in industrial facilities. The natural sources in the first draft are gone: belt scoops, flare collectors and black-hole jet AM. Salvage and trade move AM around but never create it.

```
m_AM produced = ∫ P_granted dt / AmProductionJ        AmProductionJ = 2e15 J/kg  (2 TJ per gram)
η_prod        = AmEnergy / AmProductionJ = 0.5
round trip    = η_prod · η_core = 0.5 · 0.4 = 0.2      (power → AM → core → power: no free energy)
```

| Facility (function core, `StationOnly`) | Tier | Power draw | Output | Typical power source |
| --- | --- | --- | --- | --- |
| `AmFactory` S | T1 | 20 MW | 36 mg/h, about 0.86 g a day | a fission plant at an outpost |
| `AmFactory` M | T2 | 200 MW | 0.36 g/h, about 8.6 g a day | a stack of fusion reactors |
| `AmFactory` L | T3 | 2 GW | 3.6 g/h, about 86 g a day | a fusion farm, or solar collectors near the star (8 km² at 1 AU) |
| Seeded faction foundry (data station, one per system) | n/a | 20 GW | about 860 g a day | close-in solar collectors. It sells AM cells, so early players buy AM before they can make it |

- **Station-only:** a factory core can only be placed on a grid of class Station (orbital, anchored or surface base). Ships can't carry production.
- **Feed:** 1 g of hydrogen per gram of AM (the matter half; a demand hook for ice).
- **Output:** into a trap on the factory's `Antimatter` socket. When every connected trap is full, the draw drops to a 1 % idle and production stops.
- **Power class 4 (industry)**, so it is curtailed first on a shortfall. The 10 Hz ledger banks granted joules and produces `floor(J / 2000)` ng (2,000 J per ng), keeping the remainder. It is integer and deterministic.
- **Annihilation cores can't power their own factory.** The round trip is 20 %, so AM is an energy *store and carrier*, never a source.
- **Scale check:** one C-class 300 t recirculating hop of 1 AU needs 18 g, about two days of an M factory. A fighter's planet-to-moon hop (0.46 g) takes about 13 hours of an S factory. AM is the premium consumable, and power plants are the faucet, the same role concentrator throughput plays for EM.
- **Storage** uses the traps below. A station tank farm should spread its AM over several traps in separate rooms. One 50 kg trap breach is a station-killer.
- **Hazard:** the factory's own trap, plus a production leak of `1e-4` of the draw as hard gamma (2 kW for an S factory). Factory rooms need shielding like any other annihilation source (see Radiation).

### Containment and hazards

- **AM only exists in traps.** Traps are function cores (`AntimatterTrap`). AM moves trap to trap through a new `Antimatter` socket, or in portable **AM cells**.
  - An AM cell is a 10 g item with its own trap and a 1 h battery. Cells are the only form of AM that may sit in an inventory or cargo room.
  - In cargo they run on their battery unless racked in a powered `CellRack`.
  - A recirculating drive's chamber buffer is a trap too (see Runaway burst).
- **Trap model:**
  - `AmNg` and `CapacityNg`;
  - hold power of **10 W per gram of capacity**, at containment priority (first on the bus). A cell draws 100 W, so its 1 h battery is about 0.7 kg. A 2 kg trap draws 20 kW and a 50 kg station trap draws 500 kW;
  - a built-in buffer of 120 s of hold power;
  - `Integrity` from 0 to 1, reduced by hits on the trap's housing (the shared component damage rule, see Runaway burst).
  - Sizes: cell 10 g, small 100 g, large 2 kg, station 50 kg.
- **Containment signature:** every trap, cell and hot drive chamber has a hold-field signature readable within 2 km. Stations can scan for AM and hot drives, and refuse docking through the existing permission hook. This is the counter to carrying cells in as bombs.
- **Breach** happens when the buffer reaches 0 or `Integrity` reaches 0. The whole content annihilates on that tick, in tick step 3, in `(tick, TrapId)` order. In vacuum most of the energy leaves as gamma rays and pions, so the breach is mainly a radiation weapon with a local blast:
  - **Hull:** a jagged subtract damage brush of radius `8 m · m_g^(1/3)`, capped at 120 m, on every grid it reaches (1 g → 8 m, a 10 g cell → 17 m, 2 kg → 100 m). On terrain the crater is capped at 30 m.
  - **Dose pulse:** `D = κ · f_rad · E / (4πd²)` with line of sight, through the radiation model below (`f_rad = 0.5` for a bare annihilation). This is the long-range lethal part.
  - **Chain:** if EM sits in the same room, an anti-graviton pulse of yield `0.25 · min(c_EM,room, m_AM / AmPerEm)` follows **on the next tick**. **Keep EM and AM in different rooms.**
- **Warning and counterplay:**
  - a "Containment on buffer" alarm, with the seconds left;
  - **jettison:** the component detaches as its own body, using the existing detach rule, and keeps its buffer;
  - **dump-burn:** feed AM to thrusters or the core at the maximum rate. This only helps small traps; a 2 kg trap at 43 mg/s takes about 13 h.
- **Energy scale:** `AmEnergy = 1e15 J/kg` usable (1 TJ per gram, about 1/180 of 2mc²). This is a knob: warp.md's rule that numbers are knobs, not physics, applies.

### Radiation from annihilation (decision: shielding mass, crude use irradiates)

One dose path, `RadiationDose`, is shared with flares and black-hole dose. Only the source geometry and the attenuation length differ.

```
continuous source:  Ḋ = κ · P_γ / (4π d²) · exp(−σ / σ0)        Gy/s,  P_γ = f_rad · ṁ_AM · AmEnergy
pulse source:       D = κ · f_rad · E / (4π d²) · exp(−σ / σ0)   Gy
κ = 1e-4 m²/kg (effective absorbed fraction)     σ0 = 400 kg/m² for annihilation gamma (flares keep 200)
```

- **σ, the shielding in the way, comes from hull occupancy:**
  - **External sources** (another ship's drive, a warhead, a breach, a lance impact): σ is the receiving room's `ShieldTable` areal density in the direction of the source, exactly as for flares, plus a powered `RadiationShield`'s `σ_shield`. EVA uses the suit's σ.
  - **Onboard sources** (own thrusters, lance housings, the chamber buffer, factory rooms): a new **`InternalShield`** table, baked with the hull. For each AM source component and each room, it marches the 0.5 m coarse occupancy from the source anchor to the room centroid, the same march as `ShieldTable`. It adds the source's **shadow shield** σ when the room lies inside the shield's cone.
    - It stores `att[s][room] = κ/(4πd²) · exp(−σ/σ0)`, so the runtime dose is one multiply-add per pair. Bake cost is about 10 µs per pair, on a worker.
- **Shadow shields** are part of each AM device's data: a directional areal density (kg/m²) over a cone toward the ship. Their mass is real hull mass. The standard is 4,000 kg/m². **Crude** devices (pirate kit, salvaged or stripped units) carry 1,500 or less.
- **Hulls are armour against radiation as much as against hits:** 0.5 m of composite (750 kg/m²) passes 15 %, 1 m (1,500 kg/m²) 2.4 %, and 2 m 0.055 %.
- **Crew dose (per character, acute, `DoseGy`):**
  - accumulates from every source the character's room or suit receives;
  - recovers at 0.05 Gy a day (lazy: settled when read or at the 1 Hz dose pass);
  - ≥ 0.5 Gy: radiation sickness I (stamina −25 %);
  - ≥ 2 Gy: sickness II (maximum health capped at 50 % until the dose falls below 1 Gy);
  - ≥ 5 Gy: lethal (wormholes.md's "about 5 Gy"), after 60 s.
- **Short-term treatment (decision D24):** medicine items treat acute exposure. An anti-radiation dose (`med.antirad`, a cheap crafted consumable) suppresses sickness I and II for 6 h and raises the recovery rate to 0.5 Gy a day while it acts; it does not raise the lethal threshold. Medicine never lowers the lifetime dose.
- **Long-term exposure (decision D24):** each character also keeps a **lifetime dose** (`LifetimeGy`) that only grows, by every gray received.
  - Above 2 Gy lifetime: chronic condition I (maximum stamina −10 %, slower healing).
  - Above 5 Gy lifetime: chronic condition II (maximum health −20 %, and acute thresholds fall by 20 %).
  - Above 10 Gy lifetime: chronic condition III (both, doubled).
  - Conditions are cured only by an **expensive treatment**: a `MedBay` procedure (a T3 station core) that consumes costly medical items and hours of downtime, and lowers `LifetimeGy` to the bottom of the next band down. It is a deliberate money sink for crews who work around AM.
  - All of it is lazy: settled at the 1 Hz dose pass or when read.
- **Design limit:** the build tool's Rad view colours every crewed room by its dose rate at full rated AM output. Green is ≤ 1e-4 Gy/s (5 Gy in about 14 h of continuous full output); amber is up to 1e-3; red is above.
- **Surroundings:**
  - the shadow shield protects only its own cone, so anyone off-axis or behind takes the drive's unshielded leakage. EVA crew within about 100 m of a full-throttle light plasma drive reach 5 Gy in about 20 s; a heavy AM thermal drive at full, off its cone, gives unshielded EVA crew 5 Gy in about 19 min at 5 km and about 5 h at 20 km (table below);
  - stations set an **AM thrust limit** inside `AmLimitRadius` (a permission hook, like the interdiction ban; default 20 km, never less than the exclusion radius, so a heavy thermal hovering at the edge leaves crewed rooms behind 0.5 m of hull in the green). Default limit 0: non-permitted ships have AM throttle clamped by the server inside the radius and dock on chemical, ion or RCS;
  - dose received from another party's source is attributed to that source's owner (for faction and PvP rules).
- **Lingering contamination (decision D24).** Radiation is not only transient:
  - **what contaminates:** an AM breach (trap, cell, warhead, chamber buffer) contaminates every room its dose pulse reaches above 1 Gy and, on terrain, its crater; a lance bore contaminates the rooms within 5 m of it; long exposure of hull structure (a room or hull cell absorbing more than 50 Gy in total from a drive or core, typically inside or just off a shadow cone) activates it; black-hole disc and jet exposure activates hulls the same way;
  - **model:** a contamination record per room (and per terrain chunk for craters) holds an activity `A` (a dose rate at 1 m, Gy/s) and a half-life, from data: `HullActivation` 3 days, `BreachFallout` 20 days. It is a source in the shared dose pass, `Ḋ = A · att(room)` for occupants of that room and its neighbours (the `InternalShield` march, baked once per new record), and decays as `A · 2^(−Δt / T½)`, settled lazily;
  - **decontamination:** a `Decon` tool or station service strips a room's activity (time and consumables proportional to `A`); replacing activated hull cells removes their record; craters decay or can be dug out, and the dug material carries the activity as an item flag (contaminated rock stacks dose their cargo room until they decay). A stack carries its activity like content (mass-weighted on merge, settled lazily), and a room's stacks feed **one** aggregate record per room, updated on inventory events, so a hold full of dug crater rock costs the same as one record;
  - **cost:** zero until a record exists; then one dose-pass multiply-add per (record, nearby occupant) at 1 Hz, and records are dropped when their dose rate falls below the 1e-6 Gy/s receiver floor. A breach makes at most one record per room it reaches.
- **Components** keep wormholes.md's hardening threshold: above it they are disabled for `dose / rate` seconds.

## Power: what "power" means

- **Bus:** each grid has a `PowerNet` over `Power` sockets.
  - **Generators:**
    - Solar: 250 W/m² at 1 AU, ∝ 1/d².
    - Fission: 1–20 MW, uranium items.
    - Fusion: 20–200 MW, D-He3 from ice or regolith.
    - **Annihilation core:** 0.2–5 GW at η = 0.4, so 2.5 mg AM per GJ. It is also an onboard radiation source (`f_rad = 0.02` with the core's own shield).
  - **Storage:** batteries (slow, about 0.5 MJ/kg) and **capacitor banks** (fast, about 50 kJ/kg).
  - **Consumers** declare a continuous draw (W) or a charge draw into their own capacitor. Charge draws include the wormhole generator's `CapacitorJ`, the interdiction emitter and catchers.
- **Solved in the 10 Hz ledger, by priority.** On a shortfall the highest-numbered classes are curtailed first:
  - 0: containment (traps, cell racks, stasis cases, **station-anchor control**);
  - 1: life (life support, plating, tidal compensators);
  - 2: flight and sensors (including recirculating-drive separators, and **wormhole feeds**: a generator or stabiliser on Feed, because a feed protects crossings already under way);
  - 3: charging (wormhole generators, emitters, catchers, and stabilisers on Drain);
  - 4: industry (refineries, **AM factories**, blenders, assayers).
- **GW-class power in practice means annihilation cores.** A capital generator's 2 GW `ChargeRateW` needs cores, or precharged capacitor banks (about 800 t for 39 GJ). Catchers at 250 MW can run on stacked fusion at a station. The same holds for stabilisers: a 2 GW battleship kit at full feed burns 5 mg of AM a second (18 g an hour) in its cores, or empties 400 t of capacitors in 10 s.
- **Wormhole charge under starvation (determinism kept):**
  - A generator may draw up to `1.25 × ChargeRateW` to catch up after a shortfall.
  - The charge must hold the full `E` by `TCommit − 31`. If it doesn't, the generator counts as **disabled** and the charge cancels; that is wormholes.md's existing rule.
  - Nothing is drawn after the lock-in, so no class is ever force-curtailed. `ChargeTicks` includes the 31-tick margin.

## Uses

### The anti-graviton reaction

One stoichiometry for every gravity device, in `data/physics/exotic.json`:

```
anti-graviton output ∝ c_EM (content, from stock at q ≥ the device's MinPurity)
m_AM = AmPerEm · c_EM          AmPerEm = 0.005  (1 g AM per 200 g of EM content)
```

- **Reaction chambers run on their own annihilation,** so a device's EM and AM rates need no bus power beyond small control loads.
- **The reaction turns the AM's energy into anti-gravitons, not gamma.** A runaway burst therefore carries no dose pulse of its own; only the trap breaches it cascades into do. AM that breaches without reacting (a trap, or a chamber buffer whose line is safe) annihilates as an ordinary breach.

Devices: warp bubbles, plating, tractors in push mode, wormhole devices, tidal compensators, gravity dampers, station anchors. **Not on the reaction:** the warp interdiction emitter and wormhole stabilisers, which are power-only (see Stabilisers).

**Graviton emission (no AM; decision D28).** EM driven by bus power emits gravitons only, so a device on it can **attract but never repel**:

```
graviton output ∝ c_EM (content at q ≥ the device's MinPurity)      m_AM = 0
P_bus = GravitonWPerN · F                                            bus power drives the field in place of annihilation (GravitonWPerN = 1 W per N, a knob)
```

- It is the cheap, AM-free mode: tractor pull and the inertial dampener (D9: "EM and energy") run on it. The dampener qualifies because it only ever pulls its occupants along the hull's own acceleration; its field is projected over the bubble, not radiated from a point, so the pull can point any way the hull accelerates. Anything that must push (a tractor's push or fixed-offset hold, a warp bubble, a pulse) needs anti-gravitons and so AM.
- **Station anchors stay on the full reaction at every pose.** Below the synchronous radius they push up; above it they pull down, but off the equator `a_hold` also has a poleward part that a pull toward the body cannot supply, and a graviton-only mode for the rare high equatorial anchor would save only the 0.5 % AM. One mode keeps the anchor ledger simple.
- **The 1 W per N is a gameplay knob, not an energy balance.** The reaction's 0.5 mg of AM per MN·s carries about 0.5 GJ at this doc's 1 TJ per gram, about 500 W per N, so "the power annihilation would otherwise supply" would make AM-free pull 500× dearer than written. Graviton emission is defined as a cheaper channel that can only pull; the dampener has its own `DampWPerN`. tractor-beams.md's pumped mode (100 MW per MN, capped at 25 % of rating) is replaced by this pull mode at full rating. If pull at 1 MW per MN proves too cheap against reaction-mode tractors, raise `GravitonWPerN` toward the old pumped 100 W per N (see Changes to other docs, tractor-beams.md).
- Graviton emitters hold no AM, so they have no containment signature and nothing to breach; they still count as gravity devices for the tunnel classification.

### Warp fuel: two drive kinds (replaces warp.md's `FuelPerLn`)

Every drive has a **class** (D, C, B, A: speed, mass range, economy of scale) and a **kind** (Slurry or Recirculating: how the EM reaches the bubble). Both are data. Both kinds use the same content formula; they differ in what else goes into the line and what comes back.

```
ℓ        = ln(1 + D / 1e6 m)                                          (unchanged form)
C        = EmMul(kind) · EmPerLn(class) · max(M, MassMin(class)) · ℓ  (EM content per jump)
m_AM     = AmPerEm · C

Slurry:          m_burnt = C / q_s                   q_s ∈ [MixLo, MixHi], the slurry's purity; all of it consumed
Recirculating:   m_em    = C / q_f                   q_f ∈ [FeedLo, FeedMax], blended nominal feed purity
                 m_ag    = C / q_mix − m_em          q_mix ∈ [MixLo, min(MixHi, q_f)], the diluter target; m_ag ≤ LoopAgentKg
                 m_burnt = m_em + (1 − R) · m_ag     R = AgentRecovery; the rest of the agent goes back to the loop
```

- `M` includes docked and hangared ships, cargo and the fuel itself. Fuel is consumed at T0, with no refund on abort (unchanged).
- **Economy of scale.** `EmPerLn` falls with drive class.
  - Wormhole cost grows with `r²`, but the mass of a ship that fits grows with `r³`. With one `EmPerLn` for every class, any ship over about 6,000 t that fits a heavy mouth would find a manufactured wormhole cheaper than its own warp. That breaks decision D23: warp is the cheap way to go somewhere specific and relatively close; manufactured wormholes are the fastest, and pay for it.
  - `MassMin` stops a small ship from fitting an oversized drive for the cheaper rate: below it, the drive bills as if the ship had `MassMin`.

**The two kinds:**

| | Slurry (low quality) | Recirculating (high quality) |
| --- | --- | --- |
| Feed | `em.slurry` straight from a tank: crude EM (≥ 0.17) premixed with agent at a slurry blender. Its `Fuel` sockets refuse dry EM | Dry EM from tanks, 0.40 ≤ nominal q ≤ 0.85; agent in a closed loop |
| Content per jump | `EmMul = 1.5` (the suspension wastes a third of the reaction) | `EmMul = 1.0` |
| Mix window | slurry purity 0.10–0.25 (spec 0.20); agent ≥ 40 % of the slurry mass | line purity 0.25–0.40, target 0.35 (pilot knob; lower by default for unassayed feed) |
| Agent | all consumed | `R = 0.95` recovered (0.98 with a quality separator); separator 2 MW during cooldown (C class) |
| Dilution stage | none | the diluter, on board |
| Line | none: the chamber takes slurry and AM only at T0 | prime (when hot) plus the jump's charge during spool |
| Runaway | never | yes (see Runaway burst) |
| Mass burnt per kg of base charge (`EmPerLn · M · ℓ`) | 7.5 kg (1.5 × 1/0.20) | 2.04 kg (feed 0.50), 1.41 kg (feed 0.75), 2.52 kg (feed 0.40) |
| Capex, C class (reference units) | 800 | 2,400 for the core, diluter, separator, agent loop and the standard core-eject mount (decision D13). Options: inline assayer +480, auto-purge +240 |

| Class | VCap | MassMin–MassCap | EmPerLn | Recirculating prime / line cap / agent loop | Capex slurry / recirculating | Tier |
| --- | --- | --- | --- | --- | --- | --- |
| D | 1e5 km/s | 0–500 t | 1.0e-6 | not offered by default | 300 / n/a | early |
| C | 1e6 km/s | 0–5,000 t | 1.0e-6 | 1 / 75 / 80 kg | 800 / 2,400 | mid |
| B | 2e6 km/s | 5,000–20,000 t | 0.5e-6 | 5 / 150 / 160 kg | 4,000 / 12,000 | mid–late |
| A | 5e6 km/s | 20,000–200,000 t | 0.15e-6 | 20 / 450 / 480 kg | 20,000 / 60,000 | late |

- **Line cap** = `MassCap · EmPerLn · 15`: a full charge for a `MassCap` ship over ℓ ≤ 15 (about 22 AU). Validation requires `Prime + C ≤ LineCapKg`.
- **Loop** is sized for a full charge at feed 0.50 and target 0.35. Bigger jumps at purer feed push the pilot to a higher `q_mix` to fit the loop; that headroom matters for unassayed fuel.
- **Spool is 5 s for every class.** The loader requires `SpoolTicks ≥ MixTicks + RunawayDelay + 30` (270), so an off-spec runaway always resolves before the lock-in (see Runaway burst).

**Cost per jump** (C class; slurry at q_s 0.20 from crude at 0.30; recirculating with feed 0.50, q_mix 0.35, R 0.95):

| Ship | Kind | Planet → moon (ℓ 3.04): content / mass burnt / AM | 1 AU (ℓ 11.9): content / mass burnt / AM |
| --- | --- | --- | --- |
| 30 t fighter | Slurry | 137 g / 0.68 kg / 0.68 g | 536 g / 2.7 kg / 2.7 g |
| 30 t fighter | Recirculating | 91 g / 0.19 kg / 0.46 g | 358 g / 0.73 kg / 1.8 g |
| 300 t | Slurry | 1.37 kg / 6.8 kg / 6.8 g | 5.4 kg / 27 kg / 27 g |
| 300 t | Recirculating | 0.91 kg / 1.9 kg / 4.6 g | 3.6 kg / 7.3 kg / 18 g |
| 5,000 t hauler | Slurry | 23 kg / 114 kg / 114 g | 89 kg / 447 kg / 0.45 kg |
| 5,000 t hauler | Recirculating | 15 kg / 31 kg / 76 g | 60 kg / 122 kg / 0.30 kg |

**Capex against opex** (reference prices per kg of content: crude 1, fuel 1.2, field 1.8, throat 3, crystal 6; agent 0.02/kg; slurry blending 0.05/kg; AM 0.1/g):

| C class, 1 AU | Slurry per jump | Recirculating per jump | Saving | Break-even on the 1,600 capex gap |
| --- | --- | --- | --- | --- |
| 300 t | 5.4 + agent 0.2 + blending 1.3 + AM 2.7 ≈ **9.6** | 4.3 + agent loss 0.003 + AM 1.8 ≈ **6.1** | 3.5 | about 460 jumps |
| 5,000 t | 89 + 3 + 22 + 45 ≈ **160** | 72 + 0.05 + 30 ≈ **101** | 58 | about 28 jumps |

- **The high-quality drive is cheaper to run** (about 35 % here), and it is 3–5× lighter in fuel mass, which also lowers `M` and tank volume. It costs about 3× as much to set up.
  - Haulers that jump a lot amortise it in a few dozen jumps.
  - A light raider takes hundreds of jumps, so slurry is a rational choice when the recirculating parts are hard to get.
- **Availability (decision): higher-grade feed is slightly more available.** Refined fuel-grade EM is a standard trade good from every T1 refinery and ships light. Slurry is made only at blending plants and is about 4× heavier per unit of content to haul.
  - Tune market and loot weights so fuel grade is slightly easier to find than slurry (knob `SlurryMarketWeight`, default 0.8 of fuel grade).
  - So "cheaper to run" holds in the field too, not only at the refinery gate.
- **Which kind is mostly a faction decision.** `data/factions/*.json` lists the drive kinds and options each faction builds and sells.
  - Recirculating drives need T2 components (diluter, separator) behind faction access.
  - Auto-purge and inline assay modules are keyed to the faction's signature (wormholes.md signatures). On a drive salvaged or stolen from that faction they don't work unless the key is spoofed.

**Mixer (deterministic, server), at plot (preview) and spool start (authoritative):**
- **Slurry:** draw `em.slurry` stacks on the drive's `Fuel` sockets, sorted by `(nominal purity ascending, StackId)`, until the content reaches C. The blend's nominal purity must lie in `[MixLo, MixHi]`. Fail with "fuel" otherwise.
- **Recirculating:** EM stacks on `Fuel` sockets, sorted the same way.
  1. Draw from stacks with nominal q inside `[FeedLo, FeedMax]`, by the burn-order policy (lowest grade first by default, or keep crude).
  2. If short, pair stacks below `FeedLo` with purer ones, so that the blend stays at or above `FeedLo`. Crude stock then adds content instead of being wasted.
  3. Agent: `m_ag = C/q_mix − m_em` from the loop. It must fit `LoopAgentKg`, and the loop must hold that much agent (only `(1 − R)·m_ag` is consumed).
  4. Fail with "fuel", "agent" or "line" (charge over `LineCapKg − Prime`).
  5. The interlock refuses any stack whose nominal purity is above `FeedMax`. The only way past it is the override (see Runaway burst).
- **Pilot knobs:** `q_mix`, the burn-order policy, and Hot or Cold standby.
  - **Default `q_mix = min(MixTarget, MixHi · (1 − Spread))`**, using the drawn feed's content-weighted spread: 0.35 for assayed feed, 0.34 at 15 % spread, 0.32 at 20 %. At that setting unassayed feed can't even run hot. Raising it is the pilot's call, and the UI shows the spread warning.
- **Cost:** the mixer costs about 1 µs per evaluation. Validation step 1 becomes "EM content, AM, agent and line capacity ≥ plan".
- **Server-only truth:** at spool start the server also computes the line's true purity from the drawn stacks' true contents (`WarpFuelTruth`). It never leaves the server; the outcome is announced at the mix tick.

**Failure modes by kind** (all evaluated at the mix tick, spool start + 60, unless noted):

| Case (true values) | Slurry | Recirculating |
| --- | --- | --- |
| Line or slurry below `MixLo` | Weak bubble: spool aborts, charge kept, no wear | same |
| Above `MixHi`, within `MixHi · (1 + Tol)` (Tol = 0.05) | Runs hot: jump completes, drive `Integrity − 0.05 · o/Tol` | same |
| Above `MixHi · (1 + Tol)` | Over-rich: spool aborts, charge lost, `Integrity − 0.10`. **No burst** | **Runaway** (off-spec) |
| Feed above `FeedMax` | n/a (dry EM is refused by the socket) | Interlock refuses at plot; override → runaway |
| Agent or AM short | AM: plot refused | Plot refused |
| Damage, Integrity < 0.5 | Drive disabled | Diluter breach: runaway if the true feed is above the limit and the line holds content, otherwise disabled |
| Damage, Integrity 0 (recirculating: the chamber housing, rated ×8) | Destroyed; no AM in the chamber, nothing reacts | Chamber rupture: runaway on the next tick if the true feed is above the limit, otherwise an ordinary breach of the chamber buffer |

`o = q_true / MixHi − 1` is the overshoot.

### Runaway burst (recirculating drives only)

**Why only recirculating drives:** the diluter is the only place where EM purer than the bubble window meets AM. A slurry drive's agent is premixed at ≥ 40 % of the mass and always in excess, so it quenches any excursion, and its chamber holds no AM before T0. A slurry built from purer stock is merely over-rich: the spool aborts with wear.

**What immunity does not cover:** every warp ship carries AM traps for its jumps, and a recirculating chamber buffer is a trap. Those breach like any trap (radiation and blast). Slurry and safe-feed ships avoid the runaway, not the AM hazard.

**The line.** A recirculating drive's line runs from feed tank to diluter to chamber. It holds EM content, and the chamber buffer holds the matching AM (`AmPerEm ×` the line content). The buffer is a trap, so it has a containment signature and hold power.

| Line state | Content in the line | Notes |
| --- | --- | --- |
| Cold | 0 | Nothing can run away. Spool first needs priming (`PrimeTicks`, 30 s) |
| Primed (hot) | `PrimeKg` | 5 s spool available. Standing risk under fire |
| Charged (spool start → T0) | Prime + C | The jump's charge is mixed at the mix tick (spool start + 60) |
| After T0 | Prime | The charge went into the bubble. The line refills during cooldown |
| Runaway / Purging / Purged / Ejected / Destroyed | see below | |

- **Purity limit:** `q_lim = MixHi · (1 + Tolerance)`, which is 0.42 by default.
- **Safe feed:** if the **true** feed purity in the line is ≤ `q_lim`, the line can't run away, even undiluted. Recirculating drives fed only assayed low fuel grade (true 0.40–0.42) are therefore immune to breaches. Unassayed stock shown at 0.41 may truly be up to `0.41 / (1 − Spread)` (0.51 at 20 % spread), so the immunity is only guaranteed for assayed feed. Slurry drives are immune whatever they burn. Together these are the user's "low-grade fuel, no dilution, not vulnerable".

**Triggers:**

| Trigger | Condition (server, true values) | Onset | Burst |
| --- | --- | --- | --- |
| Off-spec mix | at the mix tick, `q_line > q_lim` | mix tick | onset + 180 (3 s) |
| Diluter breach | the drive core, or an agent tank on its `Agent` sockets, crosses below `Integrity` 0.5 while the line holds content and `q_feed > q_lim` | damage tick | onset + 180 |
| Chamber rupture | the **chamber housing** (rated ×8, decision D13) reaches `Integrity` 0 while the line holds content and `q_feed > q_lim` | damage tick | **next tick**, no counterplay |
| Override ("pure dump", decision D12: kept) | owner command, accepted only when the feed tanks hold stock with nominal purity above `q_lim` (otherwise there is nothing to dump), and never in `Warp` or `Throat` placement or while held by an explicit lock (docking collar, landing clamp: warp.md). Arms for 600 ticks (10 s) with a public signature, then bypasses the diluter and fills the line to `LineCapKg` from the feed tanks | arm end | arm end + 1 |

All bursts are subject to the lock-in rule below.

- **Off-spec arithmetic.** The diluter doses agent by nominal content, so the true line purity is `q_line = q_mix / (1 + e)`, where `e` is the drawn feed's content-weighted `NomErr`. Stock that understates its purity (`e < 0`) makes the line rich.
  - The line is safe while `q_mix ≤ q_lim · (1 − Spread)`, and never even runs hot while `q_mix ≤ MixHi · (1 − Spread)`, which is the mixer's default.
  - At `q_mix` 0.35, crude-still feed (`|e| ≤ 0.15`) can at worst run hot (0.41), but T2c feed (`|e| ≤ 0.20`) runs away whenever `e < −0.167`.
  - The pressure to raise `q_mix` is the loop: a full 74 kg C-class charge at feed 0.50 needs 83 kg of agent at `q_mix` 0.32, against an 80 kg loop. Raised to 0.40, any stock understated by more than about 5 % runs away.
  - An **inline assayer** doses by true purity and removes this trigger entirely. Breaches still apply.
- **Damage-to-breach rule (deterministic):**
  - Every damage brush that overlaps a component's housing lowers its `Integrity` by `min(1, 4 · V_overlap / V_housing) / Rating`, where `Rating` is the housing's data rating (1 for ordinary cores). That covers hull hits, breach brushes, pulse-shear brushes and the burst's own near-field brush.
  - **The reaction chamber is very highly rated (decision D13).** A recirculating drive has two housings: the **core** (diluter, separator, valves; `Rating` 1) and the **reaction chamber** with its buffer trap (`Rating` 8, `ChamberRating` in data). A hit that would take an ordinary core to 0 takes the chamber down by an eighth, so a no-warning chamber rupture needs sustained, focused fire; almost every runaway under fire starts as a diluter breach with its 3 s alarm, which the core eject answers.
  - It uses q16 arithmetic, with `V_overlap` counted in coarse 0.5 m occupancy cells against the housing's cell count.
  - It runs in damage-application order in tick step 3. Thresholds are checked after each brush: core 0.5 means a diluter breach, core 0.25 means the purge valve is lost, and chamber 0 means a chamber rupture. The eject mount has its own housing (`Rating` 4), so losing the core's valves never loses the eject.
  - There is no randomness. The same rule drives traps, anchors and every other function core.
- **Timing and the warp lock-in:**
  - An off-spec onset is always at spool start + 60, so its burst falls at spool start + 240. That is before `T0 − 30` (spool start + 270). The burst cancels the warp before the lock-in, and `WarpCancel` goes out as usual.
  - **Any burst that would fall at or after `T0 − 30`** (a breach onset in the last 210 ticks before T0, or a chamber rupture in the last 31) is deferred. The lock-in holds: the drive engages and the charge goes into the bubble.
    - The burst is rescheduled to `T_exit + 181`, with yield = the prime only (since onset ≤ `T_exit`, warp.md's `max(burst, T_exit + 181)` always equals this; bursts due on or after `T_exit + 1` resolve as scheduled). `T_exit` is the last tick of `Warp` or `Throat` placement: `TDrop − 1` (warp.md: the 2 s real-space segment from `TDrop` to T1 is a simulated, tangible body), `TDrop' − 1` after a cancel, or the throat exit. A recirculating drive that is breached or ruptured is a hard cancel in cruise (warp.md cancel table: `Integrity` < 0.5, or a runaway), so in practice the ship drops early at `TDrop'` with no brake.
    - A deferred chamber rupture also gets the 180 ticks; with the purge valve gone, eject is its only counter.
    - The alarm stays up. In cruise nothing resolves, because the burst has no real space to resolve in. **The crew act freely in cruise (D17):** they can command a purge or an eject at any time. A purge is queued to the first real-space tick (`T_exit + 1`); started then, its 120 ticks always beat the burst. An eject runs its 30-tick sequence in cruise inside the sealed subtree and the core separates at `max(T_exit + 1, command + 30)`, unbraked (warp.md "Runaway and core ejection mid-warp").
    - **A runaway that starts in cruise** (only sabotage or interior damage can start one, since the D12 override is refused in `Warp` and `Throat` placement) is a hard cancel before TCommit (warp.md), and its burst is deferred exactly like a lock-in burst: to `T_exit + 181`, with yield the prime only (nothing refills the line in cruise). The 180 real-space ticks give a commanded eject 60 m (D, C) or 120 m (B, A) of separation and a purge time to finish.
  - **External** damage cannot happen in cruise (the hull is intangible), but interior damage can: crew fights and sabotage inside the grid bubble (D17) hit the drive's housings by the ordinary rule. The real-space segment (`TDrop` to T1) is tangible, so hits and collisions there count, and any burst due then resolves normally.

**Warning and counterplay:**
- **Alarm:** at onset the crew get `DriveAlarm` (cause, burst tick, line content).
  - Everyone within 50 km gets a `RunawaySignature` contact, like an armed charge, so neighbours can scatter.
  - Fleet leaders also get members' alarms.
- **Purge:**
  - It vents the line to space in 120 ticks (2 s), if the drive core's `Integrity ≥ 0.25`.
  - Content in the line is lost. The chamber buffer's AM is dumped through the micro-annihilator, as with a charge's fail-safe.
  - If it completes before the burst tick, there is no burst. The drive is "purged": it needs re-priming, plus repair if it is damaged.
- **Auto-purge (option):** the purge starts at onset by itself, so it always beats the 180-tick countdown while the valve is intact. Pirates' drives usually lack it, or have it locked to another faction's key.
- **Core eject (standard on every recirculating drive; decision D13, the main counterplay):** in 30 ticks the **entire core** (diluter, chamber with its buffer trap, the line and its valves) detaches as its own body (the existing detach rule) at +20 m/s, and bursts on schedule about 50 m away. The near-field brush then misses the ship for lines up to about 125 kg (r_n ≤ 50 m: every C-class charge); the pulse still kicks it. B and A classes eject at +40 m/s (`EjectDvMs` per class), putting about 100 m between ship and burst.
  - It works whatever the core's `Integrity` (its mount has its own rated housing), off-key on salvaged drives, and mid-warp: commanded in cruise (D17), the 30-tick sequence may run in cruise; separation at `max(T_exit + 1, command + 30)`, unbraked, ahead of the deferred burst at `T_exit + 181`, so separation at the burst is 60 m (D, C) or 120 m (B, A), not the 50/100 m of a real-space eject.
  - The ship loses the drive: an ejected core is gone unless recovered after the burst (it isn't, if it bursts). A drive whose core was ejected without bursting (the purge finished in flight) can be recovered and re-mounted.
  - An **auto-eject** owner flag (default off) ejects at onset when the purge can't win (valve lost, or auto-purge absent).
  - **Wiring (D17):** eject is an `Eject` control input wired to whatever consoles the designer chose, plus a mechanical pull handle on the cradle that needs no power or wire (ship-design's "Ejectable core assembly"), so cutting the bridge's wires can delay an eject but never prevent one by an engineer in the drive room.
- **Cold running:** keep the line empty between jumps, at the cost of 30 s of priming before each spool. Default: Cold when docked, Hot in flight (an owner setting). Priming is refused while held by an explicit lock (docking collar, landing clamp), so a docked drive is always Cold.
- **What doesn't help:** aborting the spool, or jettisoning the agent tank.

**Burst effects** (`Y` = the line's true content at the burst tick):
1. **Near field:** a jagged subtract damage brush of radius `r_n = 10 m · Y^(1/3)` (capped at 120 m), centred on the chamber, on every grid in reach. Examples: 1 kg → 10 m; a 300 t ship spooling 1 AU (1 + 3.6 kg) → 17 m; a 5,000 t hauler (1 + 60 kg) → 39 m; a full A-class line (450 kg) → 77 m. The drive and its chamber buffer are destroyed; the reaction consumes that AM, so there is no dose pulse.
2. **Far field:** an `AgPulse` with source `Runaway`, centred on the chamber, with yield `Y_p = min(Y, 100 kg)` (the casing cap). It uses the anti-graviton charge's whole effect table: pulse, shear, warp field, stability drain and catcher knock-down. The source grid is skipped by the kick (it is at the centre) but not by the near-field brush.
   - Example (300 t ship, 4.6 kg): `R` = 1.7 km, Δv at the centre 66 m/s, warp field 33 km for 33 s, and up to 4.6 GJ drained from a mouth within 8.3 km (9.2 GJ inside a tunnel).
3. **Cascade:** traps the brushes damaged breach on the next tick (one hop per tick, as everywhere).
4. **Dampers:** a gravity damper covering the centre voids the far-field kick and warp field for what it covers, not the near-field brush. A damper covering a mouth blocks the stability drain at that mouth, wherever the centre is.
5. **Griefing bound:**
   - The far field uses the casing cap and the kick budget, and its warp field counts toward the 4-per-group-per-SOI limit, so it reaches no further than a 100 kg charge.
   - The near-field brush is the runaway's extra: up to 77 m for a full A-class line, about what the same reaction's AM would do breaching in a trap (450 kg of content carries 2.25 kg of AM, a 105 m breach brush).
   - Counters: chamber signatures (2 km), stations refusing docking to primed drives, the collar lock (no priming or override while docked), and the override's 10 s public arm. To use it as a weapon a ship must close to within its near-field radius while armed and visible: a kamikaze. **Decision D12: the override stays** (scuttle or kamikaze), with every safeguard here kept: owner only, feed above `q_lim`, never docked, locked, in `Warp` or `Throat` placement, the 10 s public arm, the casing cap on the far field and the line cap on the near field.

### Wormholes and catchers

- **Unit:** every "EM" in wormholes.md is kg of content, drawn from stock at or above the device's `MinPurity`.
  - Generators:
    - Light (r ≤ 50 m) and Heavy (r ≤ 100 m) take **0.85** (throat).
    - Capital (r ≤ 400 m, wormholes.md's maximum) also takes 0.85, but its **crafting** needs a crystal graviton lens (2 kg).
  - Catchers (establish 20 EM, capture `max(5, 0.25 X)`) and the gate's `Φ_gate` bias: 0.85.
  - Natural redirect `X_redirect`: 0.85. A redirect catcher needs a crystal lens.
- **Every EM debit carries its AM:** `AmPerEm ×` the EM. That covers the generator's X, a catcher's establish and capture costs, `Φ_gate` and `X_redirect`.
  - A catcher is a capture candidate only if it holds enough EM **and** AM.
  - `X_redirect`'s AM is split across the redirecting catchers by power, as for the EM.
- **Quote:** `AM = AmPerEm · X + E / (η_core · AmEnergy)`. The second term applies only if the energy comes from cores. Since wormholes.md revision 3, `E` includes the stability reserve `EsMax`, and X carries the class factor (instant 2.5×). Additives carry no AM: they hold no EM content.
  - r = 100 m, planet → moon, tunnel, 120 s: X = 30.4 EM, E = 49 GJ, AM = 152 g + 0.12 g.
  - The same trip, instant: X = 76 EM, E = 57 GJ, AM = 380 g + 0.14 g.
  - r = 100 m, 1 AU, tunnel, 300 s: X = 119 EM, E = 130 GJ, AM = 0.6 kg + 0.33 g.
- **Reservation:** AM is reserved at charge start, alongside X and its additives, and spent at commit.
  - An owner's cancel refunds all three, as for X.
  - A forced cancel (the generator disabled, destroyed or pulsed before `TCommit − 30`) releases the reservations back into the generator's own tanks, which can then be salvaged, or breach with the wreck. Wormholes.md left this case unstated.
- **Re-establish** (wormholes.md, Stability; decision A): within the 60 s scar window after an early collapse, the owner's group recharges at `X_re = 0.25 · X`, additives at the same mix and also at 0.25, with `AmPerEm · X_re` of AM, plus `E_re = 0.25 · (opening energy) + EsMax`. The path is already drilled, so it pays no capture and no plan.
  - The 1 AU tunnel above re-establishes for 30 EM, 149 g of AM and 24.9 GJ (about 62 mg more AM if cores supply the energy).
  - The generator must already hold `X_re` and the additives in its own tanks when the scar window opens: a re-establish is a charge start, and charges draw only from the generator's sockets. A fleet that expects to power through a bombing therefore loads a quarter-charge spare.
  - `X` is always the **original** plan's X: a second collapse and re-establish costs another 0.25 X, not 0.25 of the first re-establish, so repeated collapses never get cheaper.
  - The re-establish blend passes the same `MinPurity` gate on true content as the original charge (the spare may be different stock), and with a Dilatant mix the `MaxRadius ≥ r` check uses the boosted radius, since the quarter dose keeps the same fractions and effects.
- **Stability reserve and feeds:** the reserve is energy, not EM. Feeding it, by the generator or by stabilisers, costs bus power only (see Stabilisers). The mass-throughput drain (decision C) is therefore paid back in power, and so in AM only through cores: about 2.5 mg of AM per GJ refilled. A 78,000 t capital crossing drains 2.6 GJ, which costs 6.5 mg of AM to refill. Throughput matters for the band a wormhole sits in, not for the economy.
- **Travel economics (decision D23)** replace the blanket "warp cheaper, wormholes costlier". The ordering is by what each option is for:
  - **natural wormholes are the cheapest:** a crossing costs no EM or AM at all, only the trip to the mouth, any owner's fees and the band's risks. Their limit is where they go (other star systems, seeded ends), not price;
  - **warp is the cheap way somewhere specific and relatively close by:** any destination in the system, priced by `ln(1 + D/1e6 m)`, slower with distance (minutes per AU);
  - **manufactured wormholes are the fastest** (an instant pair, or a short tunnel, to anywhere a generator can reach) and the dearest per ship.
  - The invariant this doc tests is the warp-against-manufactured half; the natural half needs no test, because a natural crossing burns nothing. Natural wormholes stay cheapest only while owners' fees stay below a ship's warp cost to the same place, which is a fee knob for wormholes.md, not a rule here.
- **The manufactured-wormhole invariant is restated**, which changes wormholes.md:
  - **Old:** "costs more than warping one `MassCap` ship". That depends on which drive is meant.
  - **New, per generator class:** the cheapest manufactured wormhole the class can open costs more than warping the largest ship that fits through it, so speed is what a manufactured wormhole sells. "Largest" means the largest at reference density `ρ_ref = 50 kg/m³` over the bounding sphere, capped at `MassCap` of the drive class that carries it. The warp side uses the **recirculating** kind on fuel grade, the cheapest to run. Slurry ships pay more, so for them the invariant holds with more margin.
  - Reference prices are as in Warp fuel. The AM column adds `AmPerEm × 0.1/g` = 0.5 per kg of content to both sides.
  - **Prices are tunnel class** (wormholes.md revision 3), the cheapest wormhole a generator can open, so the table below is unchanged. Instant pairs pass with 2.5× the margin, and no additive lowers X below the baseline (their `m_X` is 1 or more), so neither can break it.
  - Checks (per ℓ):

    | Class | Cheapest r | Ship that fits | Ship cost (EM / with AM) | Wormhole cost (EM / with AM) | Ratio (EM / with AM) |
    | --- | --- | --- | --- | --- | --- |
    | Light | 25 m | ρ 12 m, ≈ 360 t (C) | 0.43 / 0.61 | 1.9 / 2.2 | 4.4× / 3.6× |
    | Heavy | 50 m | ρ 32 m, ≈ 6,900 t (B) | 4.1 / 5.8 | 7.5 / 8.75 | 1.8× / 1.5× |
    | Capital | 100 m | ρ 72 m, ≈ 78,000 t (A) | 14.1 / 19.9 | 30 / 35 | 2.1× / 1.8× |

  - **AM price pushes the heavy and capital ratios toward 1** (the light ratio rises, because its wormhole carries more AM than its ship). **Heavy binds:** its ratio reaches 1 at about 0.7 per gram of AM at these EM prices; capital at about 1.85. The test logs both, and the economy must keep AM below the heavy figure.
  - Ships denser than `ρ_ref` can still beat it, by their density ratio. The test logs that break-even; it is accepted as a tuning edge (decision D23 settles the ordering, not every density).
  - **Fleet break-even:** r = 150 m pays for itself at about 11 C-class or 22 B-class ships of 5,000 t on EM alone (about 9 and 19 including AM at 0.1 per gram). A big fleet moving together therefore gets the fastest option at warp's price: speed for one ship is a premium, for a fleet it is logistics. That fits D23, which ranks per-trip cost for a ship, not for a fleet.
- **Fees** (wormholes Q14) could be paid in throat-grade EM. That is a natural currency now, but it is still the user's call.

### Wormhole additives (decision H)

Wormholes.md owns the effects (its Additives table and cost factors `m_X`, `m_E`, `m_S`, `m_C`, `m_L`). This section owns the items, how they are made, traded and mixed, and the purity rule.

**Items.** An additive is an item of family `additive`, never EM:
- A stack uses the same `ItemStack`. `ContentMg` holds the **active** mass, so `potency = ContentMg / MassMg`, the additive's analogue of purity. It is never counted as EM content by any device, blend or gate.
- Potency is capped below 1 too: proper labs make potency 0.90, crude labs 0.60 (data). Stacks are always assayed (no nominal error) in v1.
- Additive stacks of one item merge in 0.05-wide potency buckets.

**Mixing: at the generator, at charge start.** There is no pre-doped EM item.
- A generator gains up to two `Additive` sockets. The plan names the additive items and their masses; the quote shows the blended factors.
- At charge start the generator reserves X of EM content (as before) plus the named additive stacks, sorted `(potency descending, StackId)`. The blend exists only in the charge.
- Why not pre-doped stock: a doped stack would need its additive composition per stack, which breaks the 32 B `ItemStack`, the merge buckets and every blend rule. Additives therefore trade as their own goods, and the mix is a plan setting.

**Rules** (server, integer mg, at plan and at charge start):

```
a_i    = m_add,i / X                    additive mass per kg of EM content
f_i    = c_add,i / X                    active fraction (c = active mass)
effect_i = min(1, f_i / FullActive_i)   FullActive = 0.045 (data), so 5 % of mass at potency 0.90 gives the full effect
limits:  at most 2 additive kinds;  Σ a_i ≤ 0.10
gate:    X / (m_EM + Σ m_add,i) ≥ MinPurity          on TRUE content (0.85 for every generator class)
         ⇔  1/q_EM ≤ 1/MinPurity − Σ a_i
```

- **The gate is the purity cost.** Additives count as impurity. A full 10 % dose needs EM at a true 0.929 or better (T3 output or decayed crystal); a single additive at 5 % needs 0.888; a 2 % dose needs 0.865. Plain T2 output (0.85, capped) takes no additive at all.
- **Crude additives cost twice:** at potency 0.60 a full effect needs 7.5 % of mass, so two crude additives can't both reach full strength inside the 10 % limit, and they eat more purity headroom for the same effect.
- **Unassayed EM:** the plan checks the gate on nominal content and the server re-checks it at charge start on true content. A blend that fails on true content refuses the charge start ("purity"), with nothing spent and no cooldown. Pirates' unassayed throat stock can therefore fail a doped charge they expected to pass.
- Effects and trade-offs both scale with `effect_i`: a factor whose full value is `m_full` applies as `1 + (m_full − 1) · effect_i`. Where two additives touch the same factor (both raise `m_X`, say), their factors multiply.

**Families** (placeholder data, `data/em_additives/*.json`; the effect at full strength is wormholes.md's):

| Additive (item) | Effect at full | Trade-off on the wormhole | Made at `fc.additive_lab` from (per kg, potency 0.90) | Notes |
| --- | --- | --- | --- | --- |
| Lattice binder (`add.binder`) | Duration: `MaxOpenTicks` × 1.5 | `m_S` 0.8 (smaller reserve) | 0.5 kg of EM content at q ≥ 0.40 (T2 and T3 **refinery residue** qualifies), 0.3 kg rare metal | Gives Enrich residue a second market |
| Dilatant (`add.dilatant`) | Aperture: `MaxRadius` × 1.25 | `m_X` 1.15; Stable threshold 0.6 → 0.7 | 0.2 kg throat-grade EM content (consumed, not counted as EM in the blend), 0.6 kg heavy metal | The only way past a generator's radius rating |
| Chiral dopant (`add.chiral`) | Flow bias up to ±0.5 without the `(1 + 3Φ_gen²)` factor | `e_m` × 1.3 | 0.6 kg disc-debris inclusions (black hole H2) | Sign set in the plan |
| Inverted dopant (`add.chiral_inv`) | Flow reversal of a capturing catcher's `Φ_asym` | `e_m` × 1.5; `m_X` 1.1 | 1 kg chiral dopant, AM 0.5 g (consumed in the lab) | Counter-interdiction stock |
| Damping flux (`add.mask`) | Masking: charge glow within 50 km only; mouths read as noise beyond 10 km | `e_m` × 1.5; `VCrossMax` × 0.8 | 0.5 kg crude EM content, 0.4 kg `agent.mix` | Cheap; the crude lab makes it, so pirates have it |
| Catalyst (`add.catalyst`) | Charge speed: `m_C` 0.6 (floor half of `MinChargeTicks`) | `m_E` 1.25 | 0.4 kg fuel-grade EM content, AM 1 g (consumed in the lab) | AM-hungry to make |
| Seed lattice (`add.lattice`) | Stability: `m_S` 1.5 | `m_X` 1.2; `m_L` 1.25 (longer tunnel) | 0.1 kg crystal (q ≥ 0.97) as fines, 0.7 kg throat-grade EM content | **Metastable:** potency decays like crystal (0.02 a day toward a 0.45 floor) outside a stasis case |

- **Crafting:** `fc.additive_lab` is a T3 station core (5 MW, 2 kg/h). `fc.additive_lab.crude` is ship-mountable, needs no licence, makes only binder, flux and catalyst, at potency 0.60 and half the rate (the pirate kit). Each family is a `Fixed` recipe in `data/recipes/add_*.json`; new families are data only.
- **EM consumed in a recipe is gone,** not hidden in the product (the product's `ContentMg` is active additive mass), so no additive can smuggle EM content past the gate.
- **Trading:** additives are light (a 120 EM charge takes at most 12 kg), stable (except seed lattice) and specific, so they are natural trade goods. Faction access mirrors drives: `data/factions/*.json` lists which families each faction makes and sells.
- **Only generators accept additives** in v1. Catchers, gates and redirects do not (open question 16).
- **Trade-offs move wormholes.md's mass-drain bounds,** and that is intended: they are the price. The "one reserve per 300 s of flux" figure scales by `m_S / (e_m factor)`. The worst legal pair, binder plus damping flux at full strength (`m_S` 0.8, `e_m` × 1.5), passes a full reserve in about 160 s of flux, and one maximal entry (`MaxEntryFluxTicks`, 2 min) can take about 74 % of it instead of 40 %. Entry refusal still keeps mass alone from collapsing it, and one feeder at the cap (`EsMax / 5 s`) still outpaces it about 30-fold. Seed lattice goes the other way (450 s).
- **Additive effects meet stability through `m_S` only.** `EsMax` scales the feed cap (`EsMax / 5 s`), the bomb thresholds and the re-establish's `E_re` together, so a Seed lattice wormhole needs 1.5× the bomb to collapse and pays 1.5× the reserve in its quote; a binder wormhole is 20 % easier to bomb. No additive changes `BombK`, the ring-down or the destabiliser law.

### Stabilisers (decisions A–C)

Wormholes.md defines the `Stabiliser` component, its Feed and Drain modes, ranges, efficiencies and the drain law. This is what they cost and what they are made of.

- **Power only.** A stabiliser pours bus power into a reserve that already exists; it makes no anti-gravitons. It is outside the reaction, like the interdiction emitter (proposed; wormholes.md open question 18).
  - Feed draws at class 2 (flight); Drain at class 3 (charging).
  - GW-class draw means annihilation cores in practice: 2.5 mg of AM per GJ, so a 2 GW kit burns 5 mg/s (18 g/h) and a 1.5 GW projector 3.75 mg/s (13.5 g/h). At 0.1 per gram that is about 1.8 and 1.35 reference units an hour: feeding is cheap next to opening, so the energy fight is decided by **installed power and position**, not by fuel.
  - Example: holding a r = 100 m tunnel at the 2 GW feed cap for its whole 300 s life burns 1.5 g of AM, against 0.6 kg in its X.
- **Units and caps match wormholes.md:** `FeedW` is bus watts, the reserve is integer kJ, and the summed feed into one wormhole is capped at `FeedCapW = EsMax / 5 s` (2 GW at r = 100 m, 125 MW at the light generator's r = 25 m, 32 GW at r = 400 m). Destabiliser drain (`0.5 · η · P`) is capped at the same `FeedCapW`.
  - **Why the drain cap:** the feed cap scales with `r²` but a kit's drain does not, so uncapped, one battleship kit on Drain (1 GW) would beat any number of feeders on every wormhole under r ≈ 71 m (cap below 1 GW), and one support projector on Drain (0.53 GW) every one under r ≈ 51 m: exactly the smaller ships' wormholes that decision B says support ships hold. With both capped, a holder at the cap is cancelled by destabilisers at the cap, never outrun; breaking that hold takes a bomb.
  - **Who reaches the cap:** one battleship kit reaches it for r ≤ 100 m, and one support projector (1.05 GW delivered) for r ≤ 72 m, so a single support ship holds any light generator's wormhole, and heavy ones up to r = 72 m, at the cap from 50 km; two hold any heavy one. An r = 400 m capital wormhole needs 16 kits (or a mix) to reach its 32 GW cap.
- **Crafting** (data, `data/stabilisers/*.json`):

  | Stabiliser | FeedW | Range | η | Graviton lens | Mass | Tier | Signature |
  | --- | --- | --- | --- | --- | --- | --- | --- |
  | Battleship kit | 2 GW | 5 km | 1.0 | throat grade, 5 kg content | 800 t (with its capacitor buffer) | T3 | yes (wormholes.md, Signatures) |
  | Support projector | 1.5 GW | 50 km | 0.7 | throat grade, 10 kg content, plus a 1 kg crystal lens for the projection optics | 400 t | T4 | yes |

  - Lenses are crafted, sealed and never consumed, so a stabiliser's EM is capital, not opex. The projector's crystal lens makes support ships a T4 item, as dedicated fleet support should be.
- **Not a reaction chamber.** A stabiliser holds no EM or AM; its cores and capacitors are elsewhere on the bus. Breaking one breaches nothing.
- **Natural recovery (decision C)** needs nothing from this doc: natural pairs relax by seeded slopes. Frames around natural mouths may carry stabilisers (wormholes.md open question 13); they pay the same power.

### Anti-graviton pulses against wormhole stability

The bomb rules for wormholes now live under Anti-graviton charges, "Wormhole disruption: stability drain". In short: every pulse (charge, runaway, chain) drains `BombK · Y · (1 − d/R_s)²` from each mouth within `R_s = 5R`, or `BombK · Y` (the point-blank value) inside a tunnel, rung down over 5 s; a powered gravity damper covering a mouth blocks the drain there; a zero collapses the wormhole early.

### Gravity plating and the inertial dampener (decision D9)

Two separate devices: **plating gives gravity; the inertial dampener keeps everything inside its bubble moving with the ship.** A room can have either, both or neither.

- **Plating** is a per-room function core that consumes field-grade EM for its floor gravity only:
  `rate = 2e-4 g content / (m² · h) · g_top / 9.81`, where `g_top = max(0, g_set − dot(g_rest, n̂))` is the top-up it actually supplies along the floor-down axis (frames-and-zones: a 1 g plate on a 1 g landed ship supplies and bills nothing; in free fall `g_top = g_set`)
  - The plated area A is about the room's air volume ÷ 2.5 m.
  - A 200 m² ship at 1 g uses about 0.04 g/h (about 1 g a day). A 5,000 m² capital ship uses about 24 g a day.
  - Plating cancels nothing: on its own, a plated room under a 3 g burn has 1 g of floor plus the 3 g of the burn.
- **Inertial dampener** (`fc.inertial_dampener` S/M/L, a function core): it projects an internal bubble over the rooms it covers (its radius on its own grid, or an assigned room set) and applies to everything inside (crew, loose bodies, cargo, docked small craft in a covered hangar) the same proper acceleration as the hull, so they keep the ship's speed.
  - **Consumption (graviton emission: EM and energy, no AM):**
    ```
    a_d    = min(|a_man|, a_max) · min(1, MassMax / m_in)   the acceleration actually cancelled; also limited by delivered power and EM and by the `Limit` input
    a_man  = −(a_proper − a_rest) − α×r − ω×(ω×r) − 2ω×v   frames-and-zones' manoeuvre term, ship-level for billing
    ċ_EM   = EmPerNsDamp · m_in · a_d               EmPerNsDamp = 1e-11 kg/(N·s), content at q ≥ 0.60
    P_bus  = DampWPerN · m_in · a_d                 DampWPerN = 1 W per N, at class 1 (life)
    m_in   = the mass inside the bubble: characters, loose bodies, cargo stacks and covered docked craft
    ```
    The hull itself is not billed: thrust already moves it. **Rest acceleration is never billed** (`a_rest`: gravity on a landed ship, `a_hold` on an anchored one), so landing or anchoring is not a manoeuvre. A 300 t ship with 30 t inside its bubble pulling 3 g cancels 0.88 MN: about 32 g of content an hour and 0.88 MW, only while it manoeuvres. Cruising, landed, anchored, docked or coasting costs nothing beyond 10 kW of standby.
  - **Rating:** `a_max` per core (data: S 3 g, M 6 g, L 10 g) and `MassMax` inside (S 50 t, M 500 t, L 5,000 t). Above either, the excess **leaks through**:
    `g_interior = g_floor + a_man · max(0, 1 − a_d / |a_man|)` (frames-and-zones' form), with `a_d` as above (overfull scaling, delivered power and EM, `Limit` input). Names are `a_max`, `MassMax`, `a_d`; frames' `F_cap`, ship-design's `a_eff` and the old "2–8 g" are retired. Ship-design derives each core's rating from parts; this doc owns the runtime.
    Hard burns and rams above the rating throw people and cargo about.
  - **Full-rating bills:** S (50 t at 3 g) 1.5 MN, 53 g/h and 1.5 MW; M (500 t at 6 g) 29 MN, 1.1 kg/h and 29 MW; L (5,000 t at 10 g) 490 MN, 17.6 kg/h and 490 MW. A full L only runs on cores, and its draw sits at class 1 above flight, so on a shortfall class 2 loads (bus-driven thrust, sensors) are curtailed first, which lowers `a_proper` and with it the dampener's demand; trap-fed AM thrusters are not on the bus, so under AM thrust a short bus starves the dampener instead and the excess leaks through (both are self-limiting, never a lock-up). Owners can cap a dampener below its rating to keep its draw in budget.
  - **Ratings don't stack.** Each room takes one dampener, the covering core with the highest `a_max` (then the lowest component id), as ship-design and frames-and-zones rule; its bodies are billed to that core, re-assigned on the same events as `m_in`. Overlapping cores split the mass (each core's `MassMax` covers its own assignees) but never add their `a_max`, so four S cores are not a 12 g dampener. A crew member's own body counts once, wherever the rooms overlap.
  - **Docked craft with their own dampener:** a covered docked ship's mass is billed to the host's dampener and its own dampener idles (one bubble per body, the host's wins), so docking never double-bills.
  - **Starved** (EM, power or both): `a_d` ramps to 0 over 0.5 s and the covered rooms get the existing real-effects branch.
  - Velocity snaps from warp, flush and pulses are still excluded by the existing proper-acceleration history reset; a pulse kicks everything by the same Δv anyway.
  - **Cost per tick:** the force pass adds `m_in · a_d` to the grid's ledger accumulator (about 2 ns per dampener); the 10 Hz ledger debits EM and energy. `m_in` is updated on events (cargo change, a character changing room, docking), never per tick.
- Neither cancels tidal stretch; that is this doc's tidal compensator's job (wormholes and the black hole only use it).
- On an anchored grid the proper acceleration is the constant `a_hold` (see Station anchor). It is rest acceleration, not a manoeuvre: an unplated room feels it as floor gravity (like a building), plating tops it up to its setting, and a dampener idles at standby. (The round 5 draft had the dampener cancel `a_hold` all the time; frames-and-zones' `a_rest` rule replaces that.)
- **Interior gravity is decided** (D9; answers frames Q1 and README Q3):
  - plated rooms have floor gravity while plating is powered and fed;
  - dampened rooms move with the ship up to the dampener's rating; the excess leaks through;
  - unplated, undamped, unpowered or unfed rooms get the existing real-effects branch (real gravity − proper acceleration + fictitious);
  - early ships have neither and use mag boots.

### Tractors, compensators, dampers, emitters, anchors

| Device | MinPurity | EM content consumed | Power | Notes |
| --- | --- | --- | --- | --- |
| Tractor beam, **pull** (graviton emission, no AM; decision D28) | 0.60 | 0.1 g per MN·s emitted | `BaseMW` + 1 MW per km of range + `GravitonWPerN` (1 W per N) of emitted force; heat `0.2 · GravitonWPerN · F_emit` (tractor-beams.md owns the numbers) | Force pass, as wormholes.md decided. **Attracts only**: the force on the target points at the emitter (and the reaction pulls the emitter toward it). 1 MN for 60 s = 6 g and 1 MW. 100× the anchor's rate per N·s (beam losses), so tractoring an anchored station costs the tractor far more than the anchor. This is how a grid tows an asteroid or keeps it from drifting off (anchors can't); a rigid fixed-offset hold that also stops it closing needs push, so AM |
| Tractor beam, **push, or fixed-offset hold (Hold assist)** (anti-graviton reaction) | 0.60 | 0.1 g per MN·s emitted, plus `AmPerEm` of AM | 1 MW per km of range | Repels, or holds a fixed offset by mixing pull and push. **Needs AM:** a tractor with no AM in its traps can only pull (D28). Replaces any reduced-power push mode |
| Inertial dampener (D9) | 0.60 | `1e-11` kg per N·s cancelled (`m_in · a_d`) | `1 W per N` cancelled, class 1; 10 kW standby | Graviton emission, no AM. See Gravity plating and the inertial dampener |
| Interdiction emitter (warp) | n/a | none | `10 MW · (R/10 km)²` (unchanged) | Stays power-only, as README and wormholes.md decided. Interdiction disrupts warp bubbles; it is not counted as gravity manipulation |
| Tidal compensator | 0.60 | 1e-12 kg per (kg · m/s² · s) of tidal acceleration cancelled | 5 MW | 5,000 t at 30 km from μ = 2e13: about 2.7 kg/h. Sets `f_comp`; starved means 0, ramping down over 1 s |
| Gravity damper (station) | 0.60 | Per pulse, 1 × the share of the pulse's yield that lands inside its radius | 2 MW upkeep | Protects every body and mouth inside its radius (≤ 5 km) from anti-graviton pulses and runaway far fields, wherever the source was: kicks, warp fields and **stability drains** at a covered mouth. Partial if it runs short. It never affects wormhole flushes, collapse flows or black-hole shocks, which clients predict. Inside a tunnel it is a projector and is struck |
| Wormhole stabiliser (kit, projector) | n/a | none (crafted lenses only) | 1.5–2 GW while feeding or draining | Power only. See Stabilisers |
| **Station anchor** | 0.60 | `EmPerNs = 1e-12` kg per N·s of hold (`m · |a_hold|`), plus impulses and forces it absorbs | 0.1–2 MW control, containment class | See Station anchor |
| Stasis case | n/a | none | 10 kW, containment class | Stops crystal decay and shock loss |

### Graviton projection inside tunnels (decision F)

Wormholes.md (Tunnels, Combat) strikes any device that projects gravitons or anti-gravitons **outside its own hull** while its host is in a tunnel frame: a discharge on the tick the projection starts, then every 60 ticks, escalating per owner group, disabling the device for 10 s and draining 0.5 % of the reserve (capped at 1 %/s per wormhole). Devices in normal space are never struck.

**Classification is data.** Every function core declares `gravitonProjection`: `None` (not gravitational), `Internal` (acts on its own grid only) or `External` (projects outside the hull). The loader requires the field on every core that uses the anti-graviton reaction, and on emitters, catchers, stabilisers and generators. "Projecting" means the device is active in that state; a switched-off tractor is not struck.

| Device (this doc) | Class | Struck in a tunnel? | Notes |
| --- | --- | --- | --- |
| Gravity plating; inertial dampener | Internal | Never | Decided (F); the dampener's bubble stays inside its own hull |
| Tidal compensator | Internal | Never | Cancels stress inside the hull |
| Stasis case | Internal | Never | |
| Mag boots | None | Never | |
| Tractor beam | External | While the beam is on | The first strike disables it, so the lock breaks at once |
| Gravity damper | External | While powered | It projects up to 5 km. A station core, so only a station-class grid could carry one through; the rule applies anyway |
| Anti-graviton charge | External from the tick arming starts | At once | The strike breaches its trap; it detonates on the next tick (cascade rule) and drains `BombK · Y` at its own `s`, rung down over 5 s. Unarmed charges are `None`. **Deploying a mine inside a tunnel frame is refused**: a mine arms on proximity, and a strike would turn it into a one-tick fuse |
| Heavy `MagneticScoop`, `HorizonHarvester` | External | While active | Graviton-lens devices |
| Wormhole generator on Feed | Internal (pours into the throat) | No (proposed, as for Feed stabilisers) | It cannot charge or project a catch point in a tunnel |
| Stabiliser on Feed / on Drain | Internal / External | No / yes | Feed exemption proposed in wormholes.md open question 17 |
| Catcher | External | While spun up | It cannot capture from a tunnel |
| Warp interdiction emitter | External | While emitting | Power-only, but it projects a field |
| Warp drive (either kind) | None | Never | It cannot spool in a tunnel. A Hot line is internal. A drive override is internal too, but its burst is a pulse (below) |
| Station anchor | n/a | n/a | Nothing anchors in a tunnel |
| `CollapseInducer` | External | While inducing | Only ever used at a black hole's horizon |
| AM thrusters, AM traps and cells, annihilation cores, warheads, the lance | None | Never | Not gravitational. The lance is a beam and stops at the wall; a warhead is an AM breach and drains no stability (unless it chains EM in its room into a pulse) |

- **Who the strike hurts:** a strike is a damage brush on the projecting component (wormholes.md: that component and the grid it is mounted on, nothing else). The shared component damage rule applies, so a strike on a tractor mounted beside a trap or a drive core can breach either. The build tool's Rad view gains a "tunnel" overlay that shows which housings sit within a projector's brush.
- **Pulses are not projections.** A runaway burst or a struck charge resolves as an `AgPulse` in the tunnel's bubbles: it kicks and shears occupants and drains `BombK · Y` at its `s`, rung down over 5 s. If that empties the reserve, the tunnel's collapse point is there.

### Tunnel hazards for EM and AM hardware

Wall touches and storms (wormholes.md, Tunnels) are ordinary damage brushes plus one component within 20 m going **offline** for `2 s + 8 s · u`. On the hardware in this doc:
- **AM traps and cell racks** run on their 120 s buffer while offline, so offline alone never breaches them (at most 10 s). Damage still applies through the Integrity rule.
- **Stasis cases** offline let their crystal decay for the downtime (settled lazily, as for any stasis power change), and a brush reaching their room while they are offline is a shock event (0.02, never below the floor).
- **Recirculating drive cores** offline can't purge, and auto-purge waits until they are back. A Hot line under discharges is a standing breach risk, and a burst inside the tunnel drains up to `BombK · 100 kg` (100 GJ), which empties any unfed player wormhole under r ≈ 316 m and any prepared one under r ≈ 210 m. **Run Cold through tunnels:** the transit autopilot prompts it before entry whenever the band is below Stable. Slurry drives and assayed low-fuel-grade lines are immune here as everywhere.
- **Plating** offline drops the room into the real-effects branch for the downtime.
- **Ejections** (a ram, or a collapse front) land the ship in normal space with its tanks and traps intact. The 30 s ejection lockout refuses a warp spool; a Hot line keeps its prime through it.

### Station anchor

**What it is (decision; scope widened by D25).** A station anchor holds **any grid** at a fixed pose in a body's rotating `BodySurface` frame, at any altitude. Stations are the normal user, so this section says "station", but a ship, a platform or a construction rig can carry an anchor too. It needs a body with a `BodySurface` frame (planets, moons, remnants) or a Lagrange frame (below): **anchors never attach to asteroids or other minor bodies** (D25); tractors hold those. It may sit inside the atmosphere, below the floor of any stable orbit, or above the rotating band; it stays in `BodySurface` whatever the altitude. It does this by running the anti-graviton reaction against the difference between gravity and the acceleration the pose needs.
- **Lagrange targets** (the earlier round 5 answer that stations may sit where no orbit is possible): frames-and-zones' "Lagrange frames and anchors" extends the same anchor to a pose in a `FrameKind.Lagrange` frame of a railed pair. Everything below applies with `a_hold` = the acceleration of the fixed point in that frame minus two-body gravity there; on an eccentric pair billing uses the orbit average while overload and the drop footprint use the worst phase (frames' rule). An anchor never engages on a grid touching or tractored to an asteroid.
- A station can sit over its ground base forever, at an altitude reached by a vertical climb (about 1.35 km/s ideal to 100 km on a 1 g, 1000 km planet) instead of the 3 km/s of an orbit.
- There is no orbital phasing, and no drag inside a co-rotating atmosphere, because the station co-rotates with the air.

**Hold acceleration and consumption:**

```
a_hold = ω × (ω × r) + μ r / |r|³            (r from the body centre to the station's centre of mass, in BodySurface; constant for a fixed pose)
       = (μ/r² − ω² r cos²φ) r̂ + (ω² r cos φ sin φ) n̂     at latitude φ (n̂ poleward)
ċ_EM   = EmPerNs · m · |a_hold|               EmPerNs = 1e-12 kg/(N·s), content at q ≥ 0.60
ṁ_AM   = AmPerEm · ċ_EM
m      = the station's billed mass: hull + docked and hangared ships + cargo
```

- `a_hold` is the non-gravitational acceleration the anchor must supply so the station co-rotates: the centripetal acceleration of co-rotation minus gravity. A natural orbit at the same radius would need none, so it measures how far the pose is from a co-rotating orbit. It is **zero only at the synchronous radius on the equator**, `r_s = (μ/ω²)^(1/3)`. Below r_s the anchor pushes up; above it, it pulls down. Off the equator it also pushes poleward.
- **Rate is constant and settled lazily.** The pose is fixed, so `|a_hold|` never changes. The rate (integer ng/s, persisted) is recomputed only when the billed mass changes (dock, undock, cargo). The reserve is settled to the tick on access or on a rate change, and the warning, empty and failure ticks are scheduled as events from the current reserve and rate. There is no per-tick work.
- **Impulses:** anchored grids are skipped by `PulseKick` (pulses, runaways, flushes). The anchor pays `EmPerNs · m · Δv` instead, with the Δv the budget would have allowed. A 40 m/s kick on 10,000 t costs 0.4 g. If the tanks can't pay, the anchor fails on the next tick.
- **Contacts and tractors:** the station is static in Jolt, so rams can't move it. Contact impulses on it (summed per tick from the contact listener as the other body's momentum change) and tractor forces on it are debited the same way (`EmPerNs · J`). A sustained push above `Σ HoldMaxN` counts as a force overload (see Overload below: it drains the reserve first and raises a public `AnchorOverload` alarm with a grace window before failure). Hull damage from rams and pulse shear still applies: the anchor holds the station whole, it doesn't armour it.

**Worked numbers:** a 1000 km planet at 1 g (μ = 9.81e12 m³/s²) with a 6 h day (ω = 2.909e-4 rad/s). The synchronous radius is 4,876 km (altitude 3,876 km). The station is 10,000 t, on the equator.

| Altitude | g(r) m/s² | ω²r m/s² | `a_hold` | EM content / AM per day | Inertial speed at release (ωr) | Circular speed | If the anchor fails |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 10 km | 9.62 | 0.085 | 9.53 up | 8.2 kg / 41 g | 294 m/s | 3,117 m/s | Falls; hits the ground after about 46 s at about 0.44 km/s |
| 50 km (airless `RotatingTop`) | 8.90 | 0.089 | 8.81 up | 7.6 kg / 38 g | 305 m/s | 3,057 m/s | About 1.8 min, about 0.96 km/s |
| 100 km | 8.11 | 0.093 | 8.01 up | 6.9 kg / 35 g | 320 m/s | 2,986 m/s | About 2.6 min, about 1.3 km/s |
| 500 km | 4.36 | 0.127 | 4.23 up | 3.7 kg / 18 g | 436 m/s | 2,557 m/s | About 7.6 min, about 2.5 km/s |
| 1,000 km | 2.45 | 0.169 | 2.28 up | 2.0 kg / 9.9 g | 582 m/s | 2,215 m/s | About 14 min, about 3.1 km/s |
| 3,876 km (synchronous) | 0.413 | 0.413 | 0 | 0 / 0 | 1,418 m/s | 1,418 m/s | Nothing: it is already an orbit |
| 10,000 km | 0.081 | 0.931 | 0.85 down | 0.73 kg / 3.7 g | 3,200 m/s | 944 m/s | Flung off: above escape (1,335 m/s), leaving at about 2.9 km/s |

- Impact speeds are relative to the ground. The impact point leads the point below the station eastward by about 0.1 km (10 km release), 3 km (100 km) and 140 km (1,000 km).
- **Release regimes:** below about 2,670 km altitude a released station hits the ground; from there to the synchronous radius it enters an orbit with apoapsis at the anchor; above it, an orbit with periapsis at the anchor; above about 5,140 km (`2^(1/3) · r_s`) it escapes.
- **Scale with mass:** a 1,000 t outpost at 100 km uses 0.69 kg of field-grade content and 3.5 g of AM a day.
- **Latitude:** at 45° and 100 km, `|a_hold|` is 8.06 m/s² (+0.6 %). Off the equator it is not zero even at the synchronous radius: at 45° it is 0.29 m/s².
- **Against hovering:** holding 10,000 t at 100 km on light plasma thrusters would burn about 230 kg of AM and about 170,000 t of propellant a day. The anchor uses about 6,600× less AM and no propellant. This is why anchors exist.
- **Against the economy:** 6.9 kg of field grade plus 35 g of AM is about 16 reference units a day at 100 km, about a sixth of a 5,000 t hauler's 1 AU jump. An M factory (8.6 g a day) can't feed a 10,000 t low station's AM alone; it takes just over four, five with a margin.
- **Surface gravity (README Q1) changes all of this.** At 0.27 g (μ = 2.65e12) the synchronous radius is 3,153 km (altitude 2,153 km), and holding at 100 km costs about a quarter of the 1 g figure.

**Engaging, capacity and overload:**
- Anchor cores are function cores that **any grid** can mount (D25): S 2e7 N (10 t), M 2e8 N (60 t), L 2e9 N (400 t). Their own mass is the practical limit on small grids. Their control draw is 0.1, 0.5 and 2 MW, at containment class with a 10 s buffer. Several cores sum their `HoldMaxN`, each scaled by its `Integrity`.
- **Activation (D25): get there first.** An anchor engages only when the grid has **reached the required position and, mostly, the required velocity**:
  - **position:** the grid's centre of mass is within `EngagePosTolM` (data, 5 m) of the pose the owner set (or anywhere, if none was set), the pose is valid (footprint check below) and the grid is not in contact with terrain;
  - **velocity:** its velocity in `BodySurface` (measured through the frame maths if the grid is still in `BodyInertial` above the band) is below `EngageMaxVelMs` (data, 5 m/s) and |ω| below 0.05 rad/s. The anchor absorbs the residual during spin-up and pays for it as an impulse, `EmPerNs · m · |v_residual|`, so arriving "mostly" at rest is cheap and arriving exactly at rest is free. Above the limit engagement is refused: the pilot must fly the match first (a co-rotating hover at 100 km means cancelling about 2.7 km/s of orbital speed).
  - Spin-up takes 600 ticks, during which the grid's own thrusters hold it against gravity. At engage the grid transfers to `BodySurface` if it isn't there already.
  - Stations are normally founded by a hovering construction ship that drops an anchor seed, and they grow by construction.
  - A grid resting on the ground should be a landed base (`Frozen`), not anchored.
- **Drop footprint (anti-bombardment):** the release trajectory is fixed by the pose, so at engage (and whenever a core is added) the server solves it once: impact point, or orbit, or escape. For an impact it takes the energy at the anchor's maximum rated mass (`Σ HoldMaxN / |a_hold|`) and a footprint radius `100 m · (E / 1 TJ)^(1/3)` (a 10,000 t station released at 100 km carries about 8.8 TJ, 2 kt, and a 210 m footprint). Engaging is refused if the footprint meets another party's Station exclusion zone or surface claim without that party's permission. The footprint is public, and the build tool shows it to anyone founding a base under it.
- **While anchored:** the grid's thrusters and warp drive are locked; the anchor moves it.
- **Crawl (D25): anchored grids can move, slowly, over days.** The anchor has crawl inputs: a target pose (position, and optionally heading) and a speed setting up to `CrawlMaxMs` (data, 0.05 m/s along the surface and 0.02 m/s in altitude: about 4.3 km and 1.7 km a day).
  - The server plans the crawl as straight segments of at most 1 h in `BodySurface`. Each segment's `a_hold` is evaluated at its far end (the larger of its two ends for billing), so the rate stays constant per segment and settles lazily as now; a segment boundary is a scheduled event. Starting and stopping cost `EmPerNs · m · Δv` (negligible at these speeds).
  - Each segment's swept bounds must clear terrain (the analytic height plus the `EditStore` bound, as for catcher solid tests) and other anchored grids at plan time, since an unattended crawl has no contacts to stop it; a terrain edit or a new anchor in the way stops the crawl at the segment boundary.
  - The drop footprint is re-solved per segment, and a crawl whose path would carry the footprint into another party's exclusion zone or claim without permission is refused at plan time (and stops at the last valid segment if a zone appears later).
  - While crawling, the grid is a **kinematic** Jolt compound moved along the segment (contacts push others, never it), and its bubble re-snaps its origin by whole 32 m steps when the grid has crawled that far: a rare rebase, not a refit. Unattended (no physics anchor within 3 km), a crawling grid needs no body at all: its pose is the segment's linear function of the tick, evaluated by the pose cache on demand, and it stays `Frozen` for physics; segment ends are scheduled events. Only an attended crawl costs a kinematic body update per tick. (The round 5 draft had every crawling grid unfrozen; with crawls lasting days, a server full of crawling outposts would have paid a body each for nothing.)
  - Crawls longer than the reserve can pay are refused; warnings use the highest segment rate on the path.
  - Crawling never changes the frame: to leave the body, release (below) and fly.
- **Overload:** if `m · |a_hold| > Σ HoldMaxN`, an `Overload` alarm starts a 30 s grace (`OverloadGraceTicks` 1800). The grid must undock ships, jettison cargo or repair cores, or it fails.
  - The station's docking clamps refuse any docking that would overload it, through the existing permission hook.
  - **Force overload (tractors and rams; D27 sieges):** while summed tractor forces and contact impulses on the grid exceed `Σ HoldMaxN`, the excess is paid from the anchor reserve first (`EmPerNs · J`, as above), and a **public** `AnchorOverload` alarm (to the station's interest, its drop footprint and the listing) starts a grace window, `ForceOverloadGraceTicks` (knob, proposed 3600 = 60 s). The anchor fails only if the excess persists past the grace or the reserve empties; the window resets once the excess has stopped for the same time. This is one of the barriers that make destroying a station slow and visible (D27 "prefer capture", not "forbid destruction"), alongside the two independent control feeds (both must be cut beyond the 10 s buffer; ship-design-and-construction), the owner-authorised 60 s `Release` countdown, and dropping a station being the top incident kind (`StationDrop`, rings-and-asteroids.md).
- **Capture (D27):** cutting a station's bus power (shields, defence, turrets, clamps) never cuts the anchor's containment feeds and never transfers ownership. When an attacker completes capture at the station's `CommandCore` (ship-design-and-construction; interim rules in tractor-beams.md and rings-and-asteroids.md until a factions/law doc exists), anchor authority (`Release`, crawl targets, the reserve's owner lock) transfers to the captor's group with the rest of the station. A captured anchor keeps holding.

**Reserve and warnings:**
- The anchor draws field-grade EM from tanks on its `Fuel` sockets, and AM from traps on its `Antimatter` sockets. A **reserve** tank is reserved for it: no other device may draw from it.
- **Owner actions that would starve a holding anchor are refused:** moving fuel out of the reserve, jettisoning its tanks or traps, switching off its control power. The only owner path down is Release.
- **Example:** 100 kg of field-grade content plus 0.5 kg of AM holds 10,000 t at 100 km for 14 days.
- **Warnings** go to the owner, the station's party, and the owners of docked ships. They fire at 24 h, 6 h, 1 h, 10 min and 1 min of time-to-empty at the current mass, as scheduled events. Warnings are persisted, so late joiners get them.
- At 1 min (or the start of an overload or force-overload grace, or a release command), `AnchorFailing` goes to the interest of the station and of its drop footprint, with the predicted release tick and impact point, so docked ships can leave and people below can clear out.

**Failure (decision: drops back to physics with its current inertial velocity).**
- **Causes:**
  - the reserve is empty (EM or AM);
  - control power is lost beyond the 10 s buffer (both independent feeds);
  - the overload grace or the force-overload grace expires (or the reserve empties paying a force overload);
  - every core is at `Integrity` 0;
  - the owner releases it (60 s public countdown).
- **At the release tick F** (tick step 3, after AM breaches) the grid stops being anchored. Its velocity in `BodySurface` is 0, so its inertial velocity is `ω × r` (320 m/s at 100 km). No velocity snaps, so the proper-acceleration history needs no reset; unplated interiors simply go from `a_hold` to free fall.
  - **In a bubble:** the station body turns dynamic, with `MassMoments`. Above `1.3 × RotatingTop`, the bubble converts at F + 1 to a FreeFall bubble in `BodyInertial`, with `OriginRails` from the station's state. All members transfer through the frame maths, within the merge transfer budget (≤ 128 bodies a tick, proxies for the rest). Below that height it stays a Surface bubble and may now rebase.
  - **Unattended (`Frozen`):** it becomes `Rails` in `BodyInertial`, with Kepler elements from `(r, ω × r)`. A scheduled wake event (computed like kill-sphere events) makes it a physics anchor when its trajectory reaches `TerrainMax + 2 km`, or at once if it is already below. Its impact is then simulated, never skipped, through the existing impact damage rules (terrain crater capped at 30 m, as for breaches).
- Docked ships fall with the compound and can undock and fly. Offline crew ride along.

**A dropped station (D25): a serious, very uncommon disaster.**
- **Why uncommon:** the reserve warnings from 24 h out, the refusal of owner starvation, containment-class control power with its buffer, the 30 s overload grace, the public 60 s force-overload grace and the 60 s release countdown all stand between a station and a drop. Nothing here makes drops routine.
- **Thrusters unlock at `AnchorFailing`.** Stations should carry thrusters in case they need to fly (D25). From the failing warning the grid's thrusters and drive unlock, so a station with enough thrust can hold itself, fly to a safe orbit or set itself down. The build tool shows a station's **thrust-to-weight at its anchor pose** and the `Drop` view's outcome with and without thrust; under 1.0 it can only slow its fall.
- **Escape pods.** Stations should carry escape pods (D25). The build tool warns when pod berths are fewer than crew berths, and a grid anchored over an atmosphere with an `Impact` footprint is refused engagement without pod berths for its crew berths (`RequirePodsForImpactFootprint`, data, default true). Pods launch from `AnchorFailing` onward; they are ordinary small grids with chutes or retro thrusters (ship-hulls.md).
- **Falling through an atmosphere: mostly burns up.** Stations have no heat shield. From the release tick a falling grid takes re-entry heating and dynamic pressure like any grid. Re-entry heating is "later" in review.md; dropped stations need it, so this doc proposes the Sutton-Graves stagnation heating planets.md already uses for scoop lips (`qdot ∝ sqrt(ρ / r_nose) · v³`, with `r_nose` from the hull's windward occupancy) plus `½ρv²` against the hull material's limit:
  - above the hull's `EntryLimit` (data in ship materials: heat flux and dynamic pressure), heat and pressure damage brushes erode its windward face; structural cells that lose their connection **detach** as their own bodies by the existing detach rule;
  - debris below `BurnUpMassKg` (data, 5 t) in the heating zone is **removed as burnt up** (a visual trail, no impact), the rest keeps falling and keeps eroding;
  - what reaches the ground hits **hard** through the existing impact damage rules, as several fragments rather than one body. Terrain craters stay capped as for breaches.
  - Low, slow releases (below about 50 km on a 1 g world, where the fall is short and the speed under 1 km/s) burn little and mostly hit the ground; high releases into a thick atmosphere mostly burn up. On airless bodies nothing burns: the whole station hits.
  - The drop footprint stays **conservative**: it assumes nothing burns, so burn-up never weakens the anti-bombardment check.
- **Blame:** the owner is attributed for impact damage on others (fines and faction standing read the same attribution hook as dose), unless the drop was caused by another party's attack, in which case the attacker is.
- **Cost:** entry heating is about 1 µs per falling grid per tick (a few faces' fluxes, at 10 Hz for the damage brushes); fragmentation is a detach event per split; burnt-up debris is removed. A drop is one event-heavy minute, once in a long while.

**Frames placement** (the change to frames-and-zones.md):
- **Anchored grid:** `Placement.Frame = BodySurface` at any altitude, `GridFlags.Anchored`, and a constant `FramePose.Local` (pose cache cost nil).
  - Membership checks skip it: there are no band or SOI transitions for the station itself.
  - In a bubble it is a **static** Jolt compound, like terrain. With no anchors nearby it is `Frozen` and costs nothing per tick.
- **`BubbleAnchor.Station` (the static bubble):**
  - a bubble in `BodySurface` whose origin is the station origin snapped to 32 m;
  - it **never rebases or refits**, the same idea as `BubbleAnchor.MinorBody`;
  - scene gravity is `g(origin) + centrifugal(origin)`, with the per-awake-body delta plus Coriolis. These are Surface-bubble physics, now allowed above `RotatingTop` inside an anchored zone;
  - other bubbles merge **into** it, never the reverse. Two station bubbles within the merge gap share one static bubble at the older station's origin (both origins are 32 m-snapped in the same frame, so offsets stay exact); if their union would exceed the 4 km hard radius they stay separate and stitch with proxies;
  - a cluster that drifts past the soft radius splits into its own bubble: in `BodySurface` with Surface-bubble physics while inside the anchored zone or below the rotating band, FreeFall in `BodyInertial` once it has left the zone above `1.3 × RotatingTop`.
- **Anchored zone:** within `r_station + 2 km` (leaving at `r_station + 2.6 km`), the band rule returns `BodySurface` whatever the altitude. The zone beats the band.
  - The zone must fit the bubble's hard radius (`r_station + 2.6 km ≤ 4 km`), so anchored stations are limited to about 1.4 km in radius; larger ones fall under README Q9 (huge grids).
  - Zone entry uses the predicted-closest-approach test (R7, warp.md), so a ship crossing at km/s joins before contact.
  - The zone sets the frame only for bubble members. An entity in the zone with no Jolt body (beyond 3 km of every physics anchor) is never `Frozen` there unless it is docked or landed: it goes to `Rails` in `BodyInertial` as usual, with an impact-wake event if its trajectory meets the terrain. Otherwise a ship left hovering beside a low station would freeze in `BodySurface` and hang there for free.
  - Entities leaving the zone above `1.3 × RotatingTop` transfer to `BodyInertial` through the existing exact maths (`o = 0`, `u = 0`, `ω = spin`).
  - At 100 km the frame velocity is 320 m/s. It is bookkeeping, not a jolt.

**Ships approaching:**
- **Join its frame:** a ship entering the anchored zone transfers into `BodySurface` and the station's bubble. Its inertial velocity is unchanged and the transfer handles the frame velocity.
- **Hover physics:** inside the bubble, free bodies feel the full `g − ω²r` (8 m/s² at 100 km). An approaching ship has to cancel its orbital speed (about 2.7 km/s at 100 km, against 320 m/s of co-rotation) and then hover or dock. Loose crates and EVA crew fall away toward the planet: EVA near a low anchored station needs a jetpack or a tether.
- **Docked ships** merge into the static compound (the existing docking rule), and their mass is billed.
- **Warp arrivals:** the band rule gives `BodySurface`, the velocity rule gives `U = 0` (`u_tgt = 0`), and the arrival ring is the hemisphere facing the approach. A warp arrival therefore needs no braking, only hovering, which chemical engines can do inside the AM thrust limit.
  - **No exit floor (D16):** a ship may warp straight to a station anchored inside an atmosphere. The 2 s real-space leg ends braked at the ring point (warp.md), but an exit in low atmosphere sends a pressure wave that can kill wildlife and people below, with fines and blame on the arriving ship. Stations over populated ground therefore publish arrival lanes above their air or ask arrivals to drop high and fly down; a beaconed arrival's corridor shows its blast footprint (warp.md).
- **AM thrust limit:** stations default to banning AM thrust inside `AmLimitRadius` (see Radiation), so docking traffic doesn't irradiate the station.

### Antimatter thrusters and the flight model (decision: conventional AM thrusters)

AM thrusters annihilate AM in a propellant (any `Propellant` item: water, hydrogen, ammonia) and expel it. They draw AM straight from traps through `Antimatter` sockets, **not from the bus**. There are two families:
- **thermal** (the conventional one): annihilation heats the propellant in a heat exchanger. It gives high thrust at moderate Isp, is simple, and is leaky;
- **plasma**: a magnetic nozzle. It gives high Isp at lower thrust and is cleaner.

```
F      = u · F_max                      u = throttle from flight assist, per thruster
ṁ_prop = F / v_e
P_jet  = F · v_e / 2
ṁ_AM   = P_jet / (η · AmEnergy)
P_γ    = f_rad · ṁ_AM · AmEnergy        radiation leaving the thruster (see Radiation)
```

| Thruster (data) | F_max | v_e (Isp) | η | AM at full | Propellant at full | f_rad | Standard shadow shield |
| --- | --- | --- | --- | --- | --- | --- | --- |
| AM thermal, light | 500 kN | 9 km/s (918 s) | 0.5 | 4.5 mg/s | 56 kg/s | 0.30 | 4,000 kg/m², 0.5 m² (2 t) |
| AM thermal, heavy | 5 MN | 10 km/s (1,020 s) | 0.55 | 45 mg/s | 500 kg/s | 0.30 | 4,000 kg/m², 4 m² (16 t) |
| Plasma, light | 200 kN | 40 km/s (4,080 s) | 0.6 | 6.7 mg/s | 5 kg/s | 0.05 | 4,000 kg/m², 0.5 m² (2 t) |
| Plasma, heavy | 2 MN | 30 km/s (3,060 s) | 0.7 | 43 mg/s | 67 kg/s | 0.05 | 4,000 kg/m², 3 m² (12 t) |
| Chemical (methalox) | 50 kN–5 MN | 3.5 km/s | n/a | none | fuel + oxidiser | 0 | none |
| Ion / Hall | 0.5 N–5 kN | 30–50 km/s | 0.6 | none (bus power `F·v_e/2η`) | xenon / argon | 0 | none |
| Jet (atmosphere only) | ∝ air density | n/a | n/a | none | hydrocarbon fuel | 0 | none |

- **Example:** a 30 t fighter on three light plasma thrusters at 441 kN (1.5 g of thrust) for 300 s uses about 4.4 g of AM and 3.3 t of propellant.
- **Dose examples** (full throttle, one light thruster, a crewed room 10 m forward behind 0.5 m of composite):

  | Thruster | Shadow shield | Room dose rate | 300 s burn |
  | --- | --- | --- | --- |
  | Plasma, light | standard 4,000 kg/m² | 1.9e-4 Gy/s | 0.06 Gy |
  | Plasma, light | crude 1,500 kg/m² | 0.096 Gy/s | lethal in about 52 s |
  | AM thermal, light | standard | 7.5e-4 Gy/s | 0.22 Gy |
  | AM thermal, light | crude | 0.39 Gy/s | lethal in about 13 s |

  - Off the shadow cone, unshielded, a light plasma drive at full gives about 0.27 Gy/s at 100 m (5 Gy in about 19 s), 2.7e-3 Gy/s at 1 km and 2.7e-5 Gy/s at 10 km. Behind 1 m of hull, multiply by 0.024. A heavy AM thermal drive at full gives about 4.3e-3 Gy/s at 5 km and 2.7e-4 Gy/s at 20 km.
- **Conventional engines still carry the early game:**
  - Chemical lift-off is painful on a 1 g body (about 64 % propellant for an SSTO) and easy at 0.27 g (about 42 %), so README Q1 (surface gravity) changes how early ships feel.
  - AM thermal is the first AM engine (T1). It halves lift-off propellant against chemical, at the price of shielding mass and of being banned near stations.
  - Plasma exhaust carries a heat cone of length `k·sqrt(F)` that damages hulls and terrain, so pads use chemical engines or plasma at low throttle.
- **Accounting at 60 Hz** happens in the force pass, which runs anyway:
  - per ship, add `Σ F/v_e` and `Σ F·v_e/η` into two accumulators (about 2 ns per thruster);
  - the 10 Hz ledger turns them into propellant and AM debits, clamped at the stock, and keeps a per-thruster 1 s average of `P_γ` for the 1 Hz dose pass;
  - an empty tank sets `Starved`, and thrust is 0 from the next tick. That is at most 100 ms of overdraw, deterministic on the server.

### Antimatter weapons (decision)

All AM weapons are radiation weapons first: armour is shielding mass, and the shooter needs shielding too.

- **Torpedo warheads:**
  - A casing with its own micro-trap holding 1 g (S), 5 g (M) or 20 g (L), with a proximity or contact trigger. It is carried by missiles; missile flight belongs to a weapons doc.
  - **Detonation is an AM breach:** a hull brush of `8 m · m_g^(1/3)` (8, 14 and 22 m), plus the dose pulse.
  - For 1 g, 5 Gy (lethal) is reached out to about 890 m unshielded, about 137 m behind 1 m of composite, and about 21 m behind 2 m. A torpedo kills crew in thin hulls well before it holes them.
  - It has a 2 km containment signature, and the AG charge's fail-safe: a dud after 5 min without trap power. It counts toward the mines-and-charges group caps.
- **Annihilation lance (beam):**
  - It fires 1 mg/s of AM, 1 GW at the target, at full effect to 2 km, falling off as `(2 km / d)²` out to 10 km.
  - **At the target:** hull ablation at 2e10 J/m³ (0.05 m³/s, a 0.25 m-radius bore advancing about 0.25 m/s), plus prompt dose from the impact point (`f_rad` 0.1). Rooms within 5 m of the bore behind 1 m of remaining hull take about 0.75 Gy/s.
  - **At the shooter:** 2 % muzzle loss annihilates in the housing, which makes 10 MW of gamma. The standard housing has 3,000 kg/m² (about 12 t); a crude one has 600 (about 2 t). A crew room 10 m away behind 500 kg/m² of bulkheads takes 1.3e-4 Gy/s with the standard housing (5 Gy after about 11 h of fire), or 0.05 Gy/s with the crude one (lethal after about 100 s of fire).
- **Anti-graviton charges** stay as specified below. They are the anti-capital weapon (gradient damage); AM weapons are the anti-crew weapon.
- **Drive runaways are not a designed weapon, but the override is allowed** (decision D12): a scuttle or a visible 10 s kamikaze, bounded by the casing cap on its far field and by the near-field rules above.

## Anti-graviton charges (bombs)

**What a charge is:** a casing with an EM charge (any purity ≥ 0.15, crude preferred), a small trap holding `AmPerEm · c` of AM, and a trigger.
- Arming opens the trap into the EM: the crude form of the warp reaction with no bubble geometry.
- Yield `Y = min(c_EM, m_AM / AmPerEm)` in kg of true content, capped by the casing's `MaxYieldKg` (data; the largest casing is 100 kg). Leftover AM annihilates as a breach.
- **Example:** 1 kg of content (4 kg of crude at q 0.25) plus 5 g of AM.
- **Fail-safe:** a casing whose trap battery falls to 5 min dumps its AM through a micro-annihilator into space and becomes a dud. Mines without power therefore expire harmlessly after an hour. Hits still breach and detonate.
- **Drive runaways share this machinery:** an `AgPulse` with source `Runaway` and yield capped at the same 100 kg, using this effect table, these limits and this resolution order.

### Effects

| Effect | Radius | Magnitude | Duration |
| --- | --- | --- | --- |
| **Gravity pulse** (`PulseKick`) | `R = 1 km · Y^(1/3)` | `Δv = 40 m/s · Y^(1/3) · (1 − d/R)²`, outward, capped at 100 m/s and by the per-body kick budget | one tick |
| **Shear damage** | same | `s = |∂Δv/∂d| · L_hull`; damage brushes ∝ `s − ShearLimit` (data, default 4 m/s) | one tick |
| **Warp disruption** | `R_w = 20 · R` | an `InterdictionField` with no emitter and a fixed `End` | `20 s · Y^(1/3)`, ≤ 120 s |
| **Stability drain** | `R_s = 5 · R` from each mouth's centre; the whole tunnel when the centre is in one | `BombK · Y · (1 − d/R_s)²` per mouth in range (summed for a pair with both mouths in range); `BombK · Y` inside a tunnel. `BombK = 1 GJ` per kg | rung down over `PulseDrainTicks` (300, 5 s) |
| **Catcher knock-down** | `R` | Catchers whose host is in range restart `SpinUp` | n/a |

- **The pulse is gravity:** every mass gets the same Δv.
  - Ships are kicked whole, and their proper-acceleration history is reset, as for the flush. **Interiors feel nothing but the gradient.**
  - The gradient peaks at the centre at `2V/R ≈ 0.08 s⁻¹`, whatever the yield, up to the Δv cap at Y ≈ 16 kg.
  - So **big hulls take damage near the centre and fighters don't:** a 100 m hull sees 8 m/s at the centre, over the 4 m/s limit within about 0.5 R. The charge is an anti-capital weapon.
  - Near the ground, kicked bodies (landed ships, characters) hit terrain as ordinary impacts. That is intended weapon behaviour.
  - Anchored grids are not kicked; their anchor pays the impulse (see Station anchor). Shear damage still applies to them.
- **No terrain edits** from a pulse. It is gravitational, not explosive.

### Warp disruption

- **Detection reuses interdiction:** the 32-segment capsule test, then a forced exit at `max(t_enter, now + N)` with 3 s of warning. Entries detected after `TCommit` are ignored.
- **Spool refused:** a spool cannot start inside an active `R_w`. A spool already running is cancelled only before `T0 − 30`. The lock-in is unchanged.
  - So one fleet member's runaway cancels the whole fleet's spool if it bursts before `T0 − 30`, which it always does for an off-spec mix.
- **Stations' interdiction ban does not stop a detonation** (an explosion is not an activation). Gravity dampers do: a damper covering a body voids the warp field for paths inside its radius.
- **Charges cannot be armed in `Warp` or `Throat` placement.** The bubble would collapse it. The same applies to a drive override. In a tunnel a charge can be armed, but the wormhole strikes it at once (see Graviton projection inside tunnels).
- Cross-shard warps are not affected (v1), as for interdiction.

### Wormhole disruption: stability drain (decision A; replaces the stall)

`TClose` is now the paid **maximum** (wormholes.md revision 3, Stability). A bomb fight is an energy fight: pulses drain the reserve, holders feed it, and a zero collapses the wormhole early. The revision 2 stall, its quarter-of-`OpenTicks` cap and its immunity window are **removed**.
- **Before `TCommit − 30`:** unchanged. A generator host inside `R` counts as **disabled**, so its charge cancels (reservations, additives included, go back to its tanks).
  - The catcher knock-down changes which candidates are eligible at commit. That is legal before commit, and is counterplay against interdictors.
- **Committed (Forming), Open, and a tunnel's collapse:** a **drain** `D` per wormhole, fixed in tick step 3 at the pulse's resolution, in the step-3 order (see Resolution and determinism):
  - each mouth whose centre is within `R_s = 5R` of the pulse centre takes `BombK · Y · (1 − d/R_s)²`, with `Y` the true yield (after the casing cap) and `d` to the mouth centre;
  - a pulse whose centre is **inside a tunnel frame** drains `BombK · Y`, once, at its arc position `s`, whatever its distance to the mouths: the point-blank value, so the drain is continuous across the disc (a charge 1 m outside a mouth and 1 m inside drain the same);
  - a powered gravity damper covering a mouth at `TDet` blocks that mouth's whole drain (partially if it runs short, as for kicks). Nothing blocks the in-tunnel drain: a damper in a tunnel is struck;
  - **ring-down:** `D` is not removed in one tick. It becomes a drain slope of `floor(D / 300)` kJ per tick for `PulseDrainTicks` (300, 5 s), with the remainder at `TDet`. Feeds keep counting while it runs, so holders answer a pulse with power, which is what decision A's "power through" needs: against a one-tick drain, feed could never help. Ring-down slopes are not capped (destabilisers are; see Stabilisers), so a big enough pulse still wins;
  - drains are integers in kJ, so server and clients agree on the band and the zero tick. `AgDetonation` lists each drained wormhole and its `D`; clients add the ring-down slope themselves (its end tick is `TDet + 300`), and wormholes.md's coalesced `StabilityState` carries the result.
- **At zero** (end of step 3 of `Tz`, which may fall anywhere in a ring-down): admissions stop, feeds stop counting, remaining ring-downs are dropped, and if `Tz + 60 < TClose` the wormhole collapses at `Tcs = Tz + 60` (cause `Pulse` when a ring-down was running, by the largest active ring-down slope). An instant pair flushes both ways; a tunnel starts its collapse flow from that pulse's `s` (inside the tunnel) or from the end at its drained mouth (outside). Wormholes.md owns all of that.
- **Nothing else moves.** Flow, `Φ`, X and the flush rules are unchanged; a pulse that does not empty the reserve only lowers the band (an Unstable tunnel twists, a Critical instant pair discharges at every crossing). Natural pairs are drained the same way and relax back toward their seeded level afterwards.
- **The survival rule:** a wormhole at `Ψ₀` with net feed `P_net` (feeds minus destabilisers, each capped at `FeedCapW = EsMax / 5 s`) survives a lone pulse of drain `D` iff `D < Ψ₀ · EsMax + P_net · 5 s`. So:
  - **unsupported** (full, unfed): any `D ≥ EsMax` collapses it;
  - **prepared** (overcharged to 1.25, fed at the cap): it survives up to `2.25 · EsMax`, and a steady bombing that averages less than the cap (`EsMax / 5 s`) never empties it;
  - **pinned** (fed at the cap, destabilisers also at the cap): `P_net = 0`, so only the overcharge stands between it and a pulse above `1.25 · EsMax`.
- **Examples** (r = 100 m, `EsMax` 10 GJ, cap 2 GW; r = 150 m holds 22.5 GJ):
  - a 4 kg crude charge (1 kg yield) at `0.5 R_s` from a mouth drains 0.25 GJ (2.5 %);
  - a 10 kg-yield charge (40 kg of crude, 50 g of AM; about 15 reference units) at point blank empties a full, unfed r = 100 m reserve at the end of its ring-down;
  - a prepared r = 100 m wormhole survives 22.5 kg of yield at point blank (about 34 units), or a 100 kg casing detonated more than 12 km from the mouth (`R_s` is 23 km for 100 kg); two destabiliser kits pinning it cut that to 12.5 kg;
  - the same 10 kg charge armed inside that wormhole's tunnel drains 10 GJ at the bomber's own position: the point-blank figure, with no damper able to help;
  - a 100 kg casing (or a full runaway far field) at point blank drains 100 GJ, outside or inside: it empties any unsupported player wormhole under r ≈ 316 m and any prepared one under r ≈ 210 m. An unsupported r = 400 m capital wormhole (160 GJ) needs two casings together; a prepared one (360 GJ) four.
- **Why this is no longer a griefing win.** The revision 2 objection was that 4 kg of crude could undo 100+ EM of throat-grade investment. Now:
  - the drain scales with yield and falls off over `R_s`, so cheap charges only nibble; collapsing a wormhole takes a big charge delivered close, or many at once, against the defenders' kicks and dampers;
  - holders out-feed small drains during their ring-down (one kit refills 20 % of an r = 100 m reserve a second), overcharge buys a 25 % buffer before a known attack, and dampers over the mouths block outside drains entirely where a station carries them;
  - a collapse costs the owner a quarter of X to **re-establish** within 60 s, keeping the paid life, rather than the whole opening;
  - in a tunnel the bomber is inside the bore and is caught in its own pulse and collapse flow.
- **Cost ratio (to tune; wormholes.md open question 19):** a point-blank 10 kg-yield charge (about 15 units) against an unfed r = 100 m, 1 AU tunnel (119 EM of throat grade, about 360 units plus AM) is about 1 : 24, and the re-establish (30 EM, about 90 units) about 1 : 6. Against the same wormhole prepared, about 34 units (1 : 11, and 1 : 2.7 against the re-establish), delivered within a few km of a mouth or carried into the bore past the defenders.

### Interaction with the suppression field

- **Detonations add no suppression term.**
  - An earlier draft added a hard Occupancy "aftermath" for 30 s.
  - At commit it would push a paid B out of a gate's catch slot for the price of 4 kg of crude, an interdiction with no catcher, power or capture cost.
  - Repeated every 30 s, it would close a gate to all incoming traffic.
  - The stability drain already covers the disruption, and it never moves a paid end.
- **Suppression does not block detonations.** Charges work inside atmospheres, near mouths and at gates. Dampers are the defence.

### Resolution and determinism

- **Arming:** 5 s (`ArmTicks = 300`). An armed charge:
  - is a **physics anchor** with `A = max(2 km, R + 200 m)`, so every body in range has a Jolt body at detonation (the same guarantee as `A_m` for the flush);
  - is a loud contact (an "AG armed" signature within 50 km).
  - A runaway onset is a physics anchor with the same radius, using `Y_p`, and shows as a `RunawaySignature`.
- **Mines:**
  - Mines sit **dormant** (frozen, not anchors). A 10 Hz staggered proximity check runs against nearby entities, about 100 ns per mine.
  - When a contact enters a mine's trigger radius, the mine arms for 5 s.
  - Each group may hold at most 32 dormant or armed mines per SOI.
- **Detonation** is a server event at tick `TDet`, resolved in tick step 3:
  - **Order:** flush → black-hole collapse → pulses due this tick (charges and runaways) in `(TDet, PulseId)` order, each applying its kick and shear and starting its stability ring-downs (remainder and first tick now, the rest in step 2c of the next 299 ticks) → AM breaches due this tick in `(tick, TrapId)` order → anchor failures. Wormholes.md's zero test runs at the end of step 3, after all of these.
  - **Cascades advance one hop per tick:**
    - a pulse or burst brush that breaks a trap schedules its breach for the next tick;
    - a breach that chains or sets off a charge schedules that pulse for the next tick;
    - a brush that crosses a drive's breach threshold starts its onset on that tick, under the runaway rules.
    - There are no same-tick cycles.
  - The pulse applies to simulated roots in every bubble that overlaps `R`, and to rails entities by element update (with a pose-cache refresh). A pulse inside a tunnel kicks only that tunnel's bubbles (the tunnel frame has no parent, so nothing outside overlaps it).
  - **Cross-shard:** a pulse's drain on a wormhole owned by another shard is forwarded as a drain event and lands one hand-off late, in `(tick, source)` order, as wormholes.md specifies for every drain.
  - Skipped: `Warp` and `Throat` placements, mouth frames and wormhole frames, anchored grids (their anchor pays), and anything inside a powered damper's radius.
- `AgDetonation` (about 48 B, plus 12 B per drained wormhole: the pulse, source, drained wormholes with amounts, and damper coverage) is reliable to the interest of the centre, out to `R_w`. Clients apply the kick at `TDet`, and snapshots correct anything they got wrong.

### Griefing limits

- **Kick budget (shared `PulseKick`):**
  - Each body has a budget of 100 m/s, refilling at 100 m/s per second, that all pulses, bursts, shocks and flushes draw from. It is integer (mm/s), so clients predict it bit-exactly.
  - A per-tick cap would let a chain of charges on consecutive ticks stack to any speed.
  - `V_flush` keeps wormholes.md's own 80 m/s cap. A stacked pair's summed flush (up to 160 m/s before) is now bounded by the budget. This stays deterministic, and clients predict it from the same rule.
  - The shared `PulseKick` keeps each source's skip list (the flush skips wormhole frames, static grids and the mouth host's own compound).
- **Stability drain** (replaces the stall cap and immunity): bounded by the yield law and its falloff, the casing cap (100 GJ per pulse, inside a tunnel or out), dampers over the mouths, and whatever the holders feed during each pulse's 5 s ring-down. There is no immunity window: a defended wormhole is protected by power, not by a rule.
- **Warp fields:** at most 4 active `R_w` fields per group per SOI, runaways included. Extra detonations still kick and drain, but add no warp field.
- **Tunnels:** mines can't be deployed in a tunnel frame, and an armed charge there is struck at once, so a tunnel can't be mined in advance; a bomber inside must ride its own pulse and collapse flow.
- **Casing cap:** 100 kg of yield per charge (`R` ≈ 4.6 km, `R_w` ≈ 93 km). The same cap applies to a runaway's far field; its near-field brush is bounded by the drive's line cap (see Runaway burst).
- **Gravity dampers** protect what is inside their radius. Stations defend with equipment, not rules.
- **Mines:** 32 per group per SOI, and they expire as duds without power.
- **Carrying charges is dangerous:** when a charge's trap is hit or breaches with EM inside, the charge detonates at yield.
- **Hot drives at stations:** stations can refuse docking to primed recirculating drives by their chamber signature, and a docked drive can't prime or override (collar lock), so a ship can't park a runaway against a hull.
- **Anchored stations as kinetic weapons:** the drop footprint check, the 60 s public release countdown and the refusal of owner starvation (see Station anchor).

## Pirates and improvised kit (decision)

Pirates use what they can get. Nothing here is special-cased: it is the same data and rules with worse parameters.

- **Crude refineries:**
  - `Crude still T1c` and `Crude recrystalliser T2c` (refining table). They need no licence, are built from T0/T1 parts, and can be mounted on ships.
  - Poor yield (Y 0.65–0.70, λ 0.08) and **inconsistent purity**: every output stack is unassayed, with a spread of 15–20 %.
  - **Consequence:**
    - Slurry drives just risk a failed spool (an over-rich or weak slurry aborts with wear).
    - Recirculating drives without an inline assayer risk an off-spec runaway whenever the pilot raises `q_mix` above `q_lim · (1 − Spread)` (0.336 for T2c output), which a big charge against a small agent loop pushes them to do.
    - Unassayed feed also voids the low-grade immunity: stock shown at 0.41 may truly be 0.48–0.51.
  - **Pirate practice the rules reward:** blend many unassayed stacks before burning them (their true errors average out in a content-weighted blend, though the shown spread doesn't shrink), keep the default `q_mix`, run Cold, or steal an assayer.
- **Mixed fleets:**
  - Fleet warp already takes the most conservative profile (min `V0`, `K`, `VCap`). Kinds don't change the profile; each member pays its own fuel plan by its own kind.
  - **Spool sync** waits for the slowest member, including the 30 s priming of a Cold recirculating member.
  - **One member's runaway** sends its alarm to the leader. If it bursts before `T0 − 30` (an off-spec burst always does), its warp field cancels the whole fleet's spool, and its pulse hits a formation packed within 2 km. Fleets therefore prefer assayed fuel, auto-purge, or keeping their dodgy recirculating ships out of the formation.
  - A slurry member is heavier per jump but can never be that member.
- **Multi-drive ships (several warp drive cores on one grid; decision D14, replaces the revision 2 selector):** their bubbles **support each other**. Every participating drive spools together and contributes to one bubble, whose quality is the average of theirs weighted by the power each provides.
  - **Participation** (warp.md "Multiple drives" owns the bubble rules; this doc follows them): every drive on the root grid, and on docked ships whose collar carries power and control wiring, that is wired to the same Engage signal, powered, out of cooldown, `Integrity` ≥ 0.5, Hot or primable within the spool sync, and whose fuel plan passes. A drive the wiring leaves unpowered or unengaged (the owner's "Off") simply doesn't join and doesn't block. Rewiring or switching is allowed only while no drive is spooling, in cruise or committed; plots made before a change are discarded.
  - **Provided power:** `P_i` = the bus power its wiring actually delivers (W), and `f_i = clamp(P_i / PRated_i, 0, 1)` with `PRated` per class in data (the old `BubbleW` ratios kept as its scale, D 1 : C 4 : B 12 : A 40; absolute watts are warp.md's to set). A drive with `f_i < 0.5` doesn't join. Weights `w_i = min(P_i, PRated_i)` (D14: "averaged by provided power"; a drive draws at most its rating, so over-wiring power to one drive can't buy it more than its rated share); averages are `Σ w·X / Σ w` (warp.md). At most 8 drives join, the 8 with the largest `w`.
  - **Combined bubble** (warp.md's formulas):
    - quality (`V0`, `K`, `VReal`, `TReal`) is the `w`-weighted average of the participants' values, and `VCap = Σ w_i · f_i · VCap_i / Σ w_i`: a small drive helping a big one lowers its quality a little, a big one helping a small one raises it, an under-powered one drags the cap down;
    - capacity sums: the plot needs `M ≤ Σ f_i · MassCap_i`, so two drives can carry a ship neither could alone;
    - spool is the longest participant's. **Cooldown** (warp.md owns it): each drive starts its own `CooldownTicks`, and the root ship cannot spool again until `ShipWarpCooldownEnd`, the latest cooldown end among its last warp's participants, stored per root grid (anti-chaining; this doc's revision 2 fix, now warp.md's rule).
  - **Fuel:** each participant pays its share of the jump by its own kind and class: `C_i = EmMul_i · EmPerLn_i · max((w_i / Σ w) · M, MassMin_i) · ℓ`, through its own mixer, line and AM. Mixed kinds are allowed; each mixes at its own mix tick.
  - **Runaway:** any participant's runaway cancels the shared spool before `T0 − 30` (its warp field and the bubble both go). After the lock-in it defers like a single drive's, and in cruise it is a hard cancel of the whole warp (warp.md cancel table). A participant's weak or over-rich mix aborts the whole spool, charge rules as for one drive.
  - **Damage:** each drive takes damage separately through its own housings. A drive disabled mid-spool before the lock-in aborts the spool (charges kept per the weak-bubble rule). After the lock-in, losing a drive (damage, power, eject) only removes its `f_i · MassCap_i` of support: the warp continues while the remaining support still carries `M`, otherwise it is a hard cancel (warp.md). An unpowered recirculating drive that is Hot still holds its prime and can still run away.
    - So the typical pirate fit is the salvaged recirculating drive kept Cold and switched out, with the slurry drive Hot as the escape drive, or both wired in for a heavier hull than either carries.
    - A runaway's near-field brush (≥ 10 m) usually wrecks the other drive too, if it is close; ejecting the core takes it out of the bubble.
  - **Signatures:** `WarpDepart` carries the participating `DriveId`s with their kinds and weights, so observers see that a ship jumped partly on slurry. A Hot drive that didn't join still shows its chamber signature.
  - **Fleets** are unchanged: fleet warp still takes the most conservative member profile, each member's profile being its own combined bubble.
- **Salvage:** drives recovered from wrecks keep their `Integrity` and options, but faction-keyed options (auto-purge, inline assay) stop working off-key (see Warp fuel). Pirates' recirculating drives are therefore usually the dangerous kind.

## EM crystal stability

- **Below 0.97 (crude to throat),** EM is stable. It reacts only with AM.
- **Crystal (≥ 0.97) is metastable, hence "impossible":**
  - unless it sits in a powered **stasis case**, purity decays at 0.01 per day, mass kept, content lost;
  - shock (a hit, breach, pulse gradient, burst, tunnel discharge or collapse ejection reaching its room) costs 0.02 per event. Stasis cases block shock too.
- **Decay stops at a floor (decision H).** `DecayFloor = 0.96` (data): crystal never decays, and is never shocked, below it. The floor is T3's cap, so a decayed stack is ordinary throat-grade stock, worth about half a crystal at reference prices (3 against 6 per kg of content), and one T4 pass regrows it.
  - A stack is **metastable** while its purity is above the floor and it was crystal when last stamped. It keeps decaying after it drops below 0.97 (shown as Throat, "decaying") until it reaches the floor, where the flag clears and it is stable for good.
  - Seeds and lenses need q ≥ 0.97 at the tick of use, so a stack that has left the band can't seed or be sealed into a lens until it is regrown.
  - Crystal geodes in the ground do not decay (their stamp is the mining tick).
- **Decay is lazy:** `c(t) = m · max(DecayFloor, q0 − k·(t − Stamp))`, evaluated on access. Shock is `q ← max(DecayFloor, q − 0.02)` at the event.
  - Merging, splitting, and any change of a case's stasis state (power lost or restored, a stack moved in or out) settle the affected stacks to that tick and stamp them. The ledger reports stasis power changes as events.
  - **There is no per-tick cost.**
- **Crafted lenses don't decay.** Crafting seals the crystal.
- **The Seed lattice additive** is crystal ground to fines, not sealed, so it decays too: its potency falls 0.02 a day toward a 0.45 floor outside stasis, by the same lazy rule on `ContentMg`.
- **It makes crystal a logistics good:** you need stasis cases on the haul home from the black hole. The floor bounds the loss: a lost stasis case on a long haul costs half the crystal's value, never all of it.

## Progression and economy

| Stage | Power | Thrust | Travel | EM / AM |
| --- | --- | --- | --- | --- |
| T0 (first hours) | Solar, batteries, fission | Chemical, jet, ion | None, or slurry D warp once fuelled | Planet ore → concentrator ×2 → crude → slurry blender. AM cells bought from the system's faction foundry |
| T1 | Fusion | Chemical plus AM thermal | Slurry D/C warp, moons first | Crude/fuel from T1; crude stills (no licence); first `AmFactory` S on fission (about 0.9 g a day) |
| T2 | Fusion plus small cores | Plasma | C/B warp; recirculating drives where a faction sells them | Asteroid belts (about 15× purity), T2 field grade; plating, inertial dampeners, tractors (pull without AM, push with it); `AmFactory` M; assayers; first station anchors (S/M) |
| T3 | Cores (GW) | Heavy plasma, heavy AM thermal | Light/heavy wormhole generators (tunnel class; instant as the premium option), catchers, gates; battleship stabiliser kits; recirculating B/A with auto-purge and inline assay | T3 throat grade; additive labs (crude labs earlier, for pirates); black hole H1 and H2; `AmFactory` L; AM torpedoes and lances; large anchored stations |
| T4 | Capital cores | Heavy plasma | Capital generators, natural redirects, support-ship stabiliser projectors | Black hole H3, first seeds, T4 crystal, Seed lattice additive; `CollapseInducer` (a coalition project); the rare remnant rush |

**Rarity knobs** (`data/deposits/*.json`, `data/celestials/*.json`, `data/physics/exotic.json`):
- lens density 5 per 10⁶ km²; planet q median 0.0006;
- EM-asteroid fraction 2 % (30 % near a black hole); asteroid q median 0.01, 1 % ore fill;
- black-hole slots 1 per system; life 5–20 years (D21); rebirth 14 days; `MaxRemnants` 8; `CollapseWork` 2,000 kg of throat content (10 kg of AM), `InduceRateKgPerH` 20, `CollapseRelax` 2 % an hour;
- remnant R 20–60 km, surface q 0.08;
- concentrator and drill throughput (the EM faucet);
- `AmProductionJ` (2 TJ per gram) and installed power (the AM faucet); foundry output per system;
- `SlurryMarketWeight` (0.8: fuel grade slightly easier to find than slurry);
- crude refinery spread (0.15–0.20) and the faction availability of recirculating drives and their options;
- `QMax` (0.995), `DecayFloor` (0.96), and additive potencies, recipes and faction availability.

Tune so that planets and asteroids supply every grade below crystal for the whole game (D22), a remnant is a rare local rush rather than the moment the economy changes, and AM stays below the price at which the heavy manufactured-wormhole invariant breaks (about 0.7 per gram at reference EM prices; capital breaks at about 1.85). Travel prices keep D23's order: natural crossings cheapest, warp cheap for close specific trips, manufactured wormholes the fast premium.

## Data (all content in `data/`, formula families in code)

```json
// data/physics/exotic.json
{ "amPerEm": 0.005, "amEnergyJPerKg": 1.0e15, "amProductionJPerKg": 2.0e15,
  "bands": [ ["Ore",0], ["Crude",0.15], ["Fuel",0.40], ["Field",0.60], ["Throat",0.85], ["Crystal",0.97] ],
  "mergeBucket": 0.005, "qMax": 0.995,
  "crystal": { "decayPerDay": 0.01, "floor": 0.96, "shockLoss": 0.02 },
  "additives": { "maxKinds": 2, "maxMassFraction": 0.10, "fullActiveFraction": 0.045, "potencyBucket": 0.05,
                 "labPotency": 0.90, "crudeLabPotency": 0.60 },
  "trap": { "holdWPerG": 10, "bufferS": 120, "signatureRangeM": 2000 },
  "breach": { "radiusMPerCbrtG": 8, "radiusCap": 120, "craterCap": 30, "chainFraction": 0.25, "fRad": 0.5 },
  "radiation": { "kappaM2PerKg": 1.0e-4, "sigma0Annihilation": 400, "sigma0Flare": 200,
                 "doseRecoveryGyPerDay": 0.05, "thresholdsGy": [0.5, 2.0, 5.0], "lethalDelayS": 60,
                 "designLimitGyS": 1.0e-4, "receiverFloorGyS": 1.0e-6, "amLimitRadiusM": 20000,
                 "lifetimeBandsGy": [2.0, 5.0, 10.0], "antiradRecoveryGyPerDay": 0.5, "antiradHours": 6,
                 "contamination": { "breachMinGy": 1.0, "activationGy": 50, "halfLifeDays": { "HullActivation": 3, "BreachFallout": 20 } } },
  "componentDamage": { "overlapGain": 4.0, "chamberRating": 8, "ejectMountRating": 4 },
  "graviton": { "wPerN": 1.0 },
  "dampener": { "emPerNs": 1.0e-11, "wPerN": 1.0, "standbyW": 1.0e4, "rampS": 0.5 },
  "multiDrive": { "pRatedScale": { "D": 1, "C": 4, "B": 12, "A": 40 }, "joinMinPowerFraction": 0.5, "shipCooldownIsMaxOfParticipants": true },
  "runaway": { "mixTicks": 60, "delayTicks": 180, "purgeTicks": 120, "purgeMinIntegrity": 0.25,
               "breachIntegrity": 0.5, "ejectTicks": 30, "ejectDvMs": { "D": 20, "C": 20, "B": 40, "A": 40 }, "ejectStandard": true, "overrideArmTicks": 600,
               "nearRadiusMPerCbrtKg": 10, "nearRadiusCap": 120, "pulseCapKg": 100,
               "lockInDeferral": true, "signatureRangeM": 50000, "hotWearPerTol": 0.05, "overRichWear": 0.10,
               "autoQMixFromSpread": true },
  "anchor": { "emPerNs": 1.0e-12, "minPurity": 0.60, "zoneEnterM": 2000, "zoneLeaveM": 2600, "anyGrid": true,
              "spinUpTicks": 600, "engagePosTolM": 5, "engageMaxVelMs": 5, "engageMaxOmega": 0.05, "overloadGraceTicks": 1800, "forceOverloadGraceTicks": 3600,
              "controlBufferS": 10, "releaseCountdownTicks": 3600, "warnHours": [24, 6, 1, 0.1667, 0.0167],
              "dropRadiusMPerCbrtTJ": 100, "crawlMaxMs": 0.05, "crawlMaxVertMs": 0.02, "crawlSegmentS": 3600,
              "requirePodsForImpactFootprint": true, "burnUpMassKg": 5000 },
  "market": { "slurryWeight": 0.8 },
  "kickBudget": { "capMs": 100, "refillMsPerS": 100 },
  "agPulse": { "rKmPerCbrtKg": 1.0, "vPerCbrtKg": 40, "vCap": 100, "warpMul": 20, "warpS": 20, "warpCapS": 120,
               "drainRadiusMul": 5, "bombJPerKg": 1.0e9, "tunnelDrainMul": 1, "drainRingDownTicks": 300,
               "shearLimit": 4, "warpFieldsPerGroupPerSoi": 4, "minesPerGroupPerSoi": 32, "failSafeS": 300 } }

// data/recipes/em_recrystallise_t2.json
{ "id": "em.recrystallise.t2", "kind": "Enrich", "station": "fc.recrystalliser.t2",
  "input": { "family": "em", "minPurity": 0.30 }, "alpha": 4.0, "yield": 0.80, "loss": 0.03, "purityCap": 0.85,
  "throughputKgPerHour": 20, "powerW": 5.0e6, "spreadOut": 0.0, "consumes": [ { "item": "solvent.cryo", "perKgInput": 0.5 } ] }

// data/recipes/em_crude_still_t1c.json   (pirate kit: no licence, ship-mountable)
{ "id": "em.crude_still.t1c", "kind": "Enrich", "station": "fc.crude_still", "licence": null,
  "input": { "family": "em", "minPurity": 0.10 }, "alpha": 2.0, "yield": 0.70, "loss": 0.08, "purityCap": 0.55,
  "throughputKgPerHour": 40, "powerW": 3.0e5, "spreadOut": 0.15, "consumes": [] }

// data/recipes/em_slurry_blend.json
{ "id": "em.slurry.blend", "kind": "Blend", "station": "fc.slurry_blender",
  "inputs": [ { "family": "em", "minPurity": 0.17, "maxPurity": 0.40 }, { "item": "agent.mix" } ],
  "agentMinFraction": 0.40, "targetPurity": 0.20, "output": "em.slurry",
  "throughputKgPerHour": 2000, "powerW": 2.0e5, "feePerKg": 0.05 }

// data/items/agent_mix.json
{ "id": "agent.mix", "family": "fluid", "densityKgM3": 1400, "refCostPerKg": 0.02,
  "recipe": { "station": "fc.chem_plant", "inputs": [ { "item": "ice", "kg": 0.7 }, { "item": "rock.flux", "kg": 0.3 } ] } }

// data/recipes/weapon_agcharge.json
{ "id": "weapon.agcharge.1kg", "kind": "Fixed", "station": "fc.assembler", "timeS": 120,
  "inputs": [ { "family": "em", "minPurity": 0.15, "contentKg": 1.0 }, { "item": "am.cell", "amG": 5 }, { "item": "part.ag_casing.s", "count": 1 } ],
  "outputs": [ { "item": "weapon.agcharge", "attributes": { "yieldKg": 1.0 } } ] }

// data/drives/warp_c_slurry.json   (warp.md's file, FuelPerLn replaced)
{ "id": "drive.warp.c.slurry", "class": "C", "kind": "Slurry", "v0": 5000, "k": 0.3, "vCap": 1.0e9,
  "spoolTicks": 300, "cooldownTicks": 1800, "massMin": 0, "massCap": 5.0e6, "emPerLn": 1.0e-6, "emMul": 1.5,
  "mixLo": 0.10, "mixHi": 0.25, "agentMinFraction": 0.40, "fuelSocketAccepts": "em.slurry", "costRef": 800 }

// data/drives/warp_c_recirc.json
{ "id": "drive.warp.c.recirc", "class": "C", "kind": "Recirculating", "v0": 5000, "k": 0.3, "vCap": 1.0e9,
  "spoolTicks": 300, "cooldownTicks": 1800, "massMin": 0, "massCap": 5.0e6, "emPerLn": 1.0e-6, "emMul": 1.0,
  "mixLo": 0.25, "mixHi": 0.40, "mixTarget": 0.35, "feedLo": 0.40, "feedMax": 0.85, "tolerance": 0.05,
  "agentRecovery": 0.95, "loopAgentKg": 80, "primeKg": 1.0, "lineCapKg": 75, "primeTicks": 1800,
  "separatorW": 2.0e6, "costRef": 2400,
  "options": { "inlineAssay": { "cost": 480, "factionKeyed": true }, "autoPurge": { "cost": 240, "factionKeyed": true },
               "qualitySeparator": { "cost": 400, "agentRecovery": 0.98 } },
  "coreEject": "standard", "pRatedScale": 4 }

// data/em_additives/binder.json   (wormholes.md reads "effect" and "tradeOff"; this doc owns "item" and "recipe")
{ "id": "add.binder", "item": { "family": "additive", "densityKgM3": 2200, "refCostPerKg": 4.0, "metastable": null },
  "effect":   { "maxOpenTicksMul": 1.5 },
  "tradeOff": { "mS": 0.8 },
  "recipe": { "station": "fc.additive_lab", "crude": true, "kind": "Fixed", "timeSPerKg": 1800, "powerW": 5.0e6,
              "inputs": [ { "family": "em", "minPurity": 0.40, "contentKg": 0.5 }, { "item": "metal.rare", "kg": 0.3 } ] } }

// data/em_additives/lattice.json
{ "id": "add.lattice", "item": { "family": "additive", "densityKgM3": 3100, "refCostPerKg": 12.0,
                                 "metastable": { "decayPerDay": 0.02, "floorPotency": 0.45 } },
  "effect":   { "mS": 1.5 },
  "tradeOff": { "mX": 1.2, "mL": 1.25 },
  "recipe": { "station": "fc.additive_lab", "crude": false, "kind": "Fixed", "timeSPerKg": 3600, "powerW": 5.0e6,
              "inputs": [ { "family": "em", "minPurity": 0.97, "contentKg": 0.1 }, { "family": "em", "minPurity": 0.85, "contentKg": 0.7 } ] } }

// data/stabilisers/battleship_kit.json   (wormholes.md reads the feed fields; this doc owns power class and crafting)
{ "id": "stab.kit", "feedW": 2.0e9, "rangeM": 5000, "efficiency": 1.0, "massKg": 8.0e5, "tier": 3,
  "powerClassFeed": "Flight", "powerClassDrain": "Charging", "gravitonProjection": { "Feed": "Internal", "Drain": "External" },
  "craft": { "lens": { "family": "em", "minPurity": 0.85, "contentKg": 5 } } }

// data/components/function-cores/tractor_beam.json   (excerpt: the tunnel classification every gravity core carries)
{ "id": "fc.tractor", "minPurity": 0.60, "gravitonProjection": "External",
  "modes": { "Pull": { "source": "Graviton", "amPerEm": 0 }, "Push": { "source": "AntiGraviton", "requiresAm": true } } }

// data/components/function-cores/inertial_dampener_m.json
{ "id": "fc.inertial_dampener.m", "aMaxG": 6, "massMaxKg": 5.0e5, "radiusM": 40, "minPurity": 0.60,
  "source": "Graviton", "powerClass": "Life", "gravitonProjection": "Internal", "sockets": ["Fuel", "Power"] }

// data/components/function-cores/collapse_inducer.json
{ "id": "fc.collapse_inducer", "tier": 4, "massKg": 2.0e6, "minPurity": 0.85, "rateKgPerH": 20, "powerW": 2.0e9,
  "craft": { "lens": { "family": "em", "minPurity": 0.97, "contentKg": 5 } }, "gravitonProjection": "External", "signatureRangeM": 5.0e7 }

// data/components/function-cores/station_anchor_m.json   (D25: any grid)
{ "id": "fc.station_anchor.m", "stationOnly": false, "holdMaxN": 2.0e8, "massKg": 60000,
  "minPurity": 0.60, "controlW": 5.0e5, "powerClass": "Containment", "sockets": ["Fuel", "Antimatter", "Power"] }

// data/components/function-cores/am_factory_m.json
{ "id": "fc.am_factory.m", "stationOnly": true, "powerW": 2.0e8, "powerClass": "Industry", "idleFraction": 0.01,
  "feed": [ { "item": "propellant.hydrogen", "kgPerG": 0.001 } ], "leakGammaFraction": 1.0e-4, "sockets": ["Antimatter", "Fuel", "Power"] }

// data/components/function-cores/thruster_am_thermal_l.json
{ "id": "fc.thruster.am_thermal.l", "fMaxN": 5.0e5, "veMs": 9000, "eta": 0.5, "fRad": 0.30,
  "shadow": { "sigmaKgM2": 4000, "areaM2": 0.5, "halfAngleDeg": 30 } }

// data/components/function-cores/weapon_am_lance.json
{ "id": "fc.weapon.am_lance", "amKgPerS": 1.0e-6, "rangeFullM": 2000, "rangeMaxM": 10000,
  "ablationJPerM3": 2.0e10, "fRadTarget": 0.1, "muzzleLoss": 0.02, "fRadMuzzle": 0.5,
  "housing": { "sigmaKgM2": 3000, "massKg": 12000 } }

// data/components/function-cores/warhead_am_s.json
{ "id": "fc.warhead.am.s", "amG": 1, "trigger": ["Proximity", "Contact"], "failSafeS": 300 }

// data/materials/voxel/exotic_ore.json   (derived at generation, never placeable)
{ "id": "ExoticOre", "densityKgM3": 2800, "hardness": 0.7, "placeable": false,
  "yields": { "family": "em", "purityFrom": "deposit" }, "tint": "#6B3FA0" }

// data/deposits/planet_em.json
{ "lensPerMKm2": 5, "depthM": [30, 1500], "halfAxesM": [10, 80], "veinFill": 0.05,
  "qLogNormal": { "median": 0.0006, "min": 0.0002, "max": 0.002 }, "depthGainPerKm": 1.0, "cueDepthM": 200, "cueRadiusM": 150 }

// data/celestials/blackhole.json
{ "mu": [1.0e13, 4.0e13], "killRadiusM": 2000, "disc": [2.0e5, 2.0e6], "jetHalfAngleDeg": 8, "jetLengthM": 1.0e9,
  "doseDiscGyS": 0.5, "doseJetGyS": 2.0, "doseRefM": 1.0e6,
  "lifeDays": [1825, 7300], "rebirthDays": 14,
  "induction": { "collapseWorkKg": 2000, "relaxPerHour": 0.02, "countdownHours": 24, "progressEventFraction": 0.25 }, "forecastDays": 7, "birthForecastDays": 7, "maxRemnants": 8,
  "shock": { "radiusM": 5.0e7, "dv": 100 },
  "tidalBubble": { "accelCap": 20, "minRadiusM": 50 }, "integratedOriginGradient": 1.0e-3,
  "remnant": { "radiusM": [2.0e4, 6.0e4], "gravityG": [0.15, 0.4], "reliefM": 2000, "yieldDensityKgM3": 3000,
               "qSurface": 0.08, "qGain": 0.80, "qExponent": 2.0, "geodeQ": [0.97, 0.99], "geodeInnerFraction": 0.2 } }
```

Also needed:
- `data/components/function-cores/*.json` for every core named here (drives of both kinds and every class, anchors S/M/L, factories S/M/L, blender, assayers, crude cores, thrusters, warheads, lance);
- `data/drives/*.json` for every class and kind;
- `data/stations/foundry.json` (the seeded faction foundry);
- `data/factions/*.json` with the drive kinds and options each faction builds;
- `data/ship-materials` gains `TidalLimit` and `ShearLimit`;
- `data/materials/voxel` gains the `placeable` flag and the three remnant materials;
- `data/loot/*.json` (salvaged drives keep options, keyed options inert);
- `data/em_additives/*.json` for all seven families, and `data/recipes/add_*.json` if the recipes are split out;
- `data/stabilisers/*.json` (kit and projector);
- a `gravitonProjection` field on every function core listed under Graviton projection inside tunnels;
- `data/factions/*.json` gains the additive families each faction makes and sells;
- round 5: `fc.inertial_dampener` S/M/L, `fc.collapse_inducer`, tractor `modes`, `PRated` per drive class (warp.md sets the watts), the chamber and eject-mount housing ratings, `med.antirad` and the `MedBay` treatment items, contamination half-lives, `EntryLimit` in ship materials, and escape-pod components (ship-hulls.md).

## C# data structures (zero alloc, server authoritative)

```csharp
namespace BrutalGame.Resources;

public readonly record struct ItemId(ushort Value);

[StructLayout(LayoutKind.Sequential)]
public struct ItemStack                       // 32 B
{
    public ItemId Item; public ushort Bucket;   // merge bucket (derived, cached; includes the assayed bit)
    public byte Flags;                          // InStasis, Assayed, ...
    public byte SpreadQ8;                       // public bound on |NomErr|; 0 = assayed
    public short NomErrQ15;                     // nominal/true − 1; server only, never sent
    public long MassMg;
    public long ContentMg;                      // TRUE EM-bearing part; purity = ContentMg / MassMg. Additives: active mass (potency). 0 for other items
    public long Stamp;                          // metastable settle tick (crystal, seed lattice); 0 = n/a or at the floor
}

public static class EmMath                     // integer only; no Math.Exp/Log
{
    public static ushort PurityQ16(in ItemStack s);
    public static ushort NominalPurityQ16(in ItemStack s);            // what the owner sees
    public static void Blend(ReadOnlySpan<ItemStack> parts, out ItemStack result);   // sums true and nominal, recomputes NomErr, true-content-weighted Spread (rounded up)
    public static ushort Enrich(ushort qIn, int alphaQ16, ushort cap);   // requires qIn < cap; guarded near q -> 1
    public static bool EnrichBalance(in ItemStack input, in EnrichRecipe r, ulong serverSeed, long batchId, out ItemStack product, out ItemStack residue);
    public static long CrystalContentAt(in ItemStack s, long tick, in CrystalRules rules);   // max(floor, q0 − k·dt); also additive potency
    public static void Shock(ref ItemStack s, long tick, in CrystalRules rules);              // settle, then q ← max(floor, q − loss)
    public static void Assay(ref ItemStack s);                         // NomErr = 0, Spread = 0
    // Wormhole charge blend: X content of EM plus ≤ 2 additive stacks. Checks kinds, Σ mass ≤ 10 % of X, and the purity gate
    // X ≥ MinPurity · (m_EM + Σ m_add) in integer mg, on TRUE content (server, charge start) or nominal (plan). Writes effect_i in q16.
    public static bool AdditiveBlend(long xContentMg, long emMassMg, ReadOnlySpan<ItemStack> additives, ushort minPurity,
                                     in AdditiveRules rules, bool useTrue, Span<ushort> effectQ16);
}

public struct CrystalRules { public int DecayPerDayQ16, ShockLossQ16; public ushort FloorQ16, BandQ16; }   // lattice: its own floor and rate
public struct AdditiveRules { public byte MaxKinds; public ushort MaxMassFracQ16, FullActiveQ16; }
public struct AdditiveMix { public ushort A0, A1; public float F0, F1; }   // as wormholes.md: item ids and effect fractions, fixed at charge start
public enum GravitonProjection : byte { None, Internal, External }       // per function core and mode (data); read by tunnel retaliation

public struct EnrichRecipe { public ushort Station, MinIn, Cap; public int AlphaQ16, YieldQ16, LossQ16; public int MgPerSecond;
                             public long PowerW; public int SolventMgPerKg, AmNgPerKg; public ushort SeedMinQ; public int SeedFracQ16, SeedUseQ16;
                             public byte SpreadOutQ8; }
public struct Batch { public int Station, Recipe; public long StartTick, EndTick, StarvedTicks, StarvedApplied, BatchId; }  // min-heap by EndTick, lazy re-push

public struct AntimatterTrap { public int Component; public byte Room; public long AmNg, CapacityNg, HoldW, BufferJ, BufferMaxJ; public ushort Integrity; }

public struct AmFactory { public int Component, OutTrap; public long PowerW, BankedJ; }   // 10 Hz: ng += BankedJ / 2000, keep remainder

public enum PowerClass : byte { Containment, Life, Flight, Charging, Industry }
public sealed class PowerNet                   // SoA per grid, preallocated
{
    public long[] GenMaxW, GenOutW; public byte[] GenKind;        // Solar, Fission, Fusion, Annihilation
    public long[] DrawW; public PowerClass[] DrawClass; public long[] GrantedW;
    public long[] StoreJ, StoreMaxJ, StoreRateW;
    public void Solve(long tick);              // 10 Hz, staggered by grid id; emits stasis and anchor-control power-change events
}

public struct ShipLedger                        // per root grid, written by the force pass, settled at 10 Hz
{
    public double PropMassFlow, PlasmaPowerOverEta;  // accumulators, cleared at settle
    public long AmDebitNg, PropDebitMg; public byte Starved;
}

// ---- Warp drives (data in warp.md's WarpDrive, extended) ----
public enum DriveKind : byte { Slurry, Recirculating }
[Flags] public enum DriveOptions : byte { None = 0, InlineAssay = 1, AutoPurge = 2, AutoEject = 4, QualitySeparator = 8 }   // core eject is standard (D13); AutoEject is the owner flag
public enum LineState : byte { Cold, Priming, Primed, Charged, Runaway, Purging, Purged, Ejected, Destroyed }
public enum RunawayCause : byte { None, OffSpec, DiluterBreach, ChamberRupture, Override }

public unsafe struct WarpFuelPlan               // sent to the crew at spool start
{
    public long EmContentMg, EmMassMg, AmNg, AgentLoopMg, AgentLossMg, MassBurntMg, BilledMassKg;
    public ushort QFeedNom, QMix, SpreadQ16; public DriveKind Kind; public byte Draws; public fixed int Stack[8];
}
public struct WarpFuelTruth { public long TrueContentMg; public ushort QLineTrue, QFeedTrue; public RunawayCause Outcome; public byte Failure; } // server only

public struct DriveLine                         // per recirculating drive core, server; SoA in DriveTable
{
    public int Component, Grid; public LineState State; public RunawayCause Cause; public DriveOptions Options;
    public ushort Integrity, ChamberIntegrity, EjectIntegrity;   // q16 per housing (core rating 1, chamber 8, eject mount 4)
    public long ContentMg, NominalMg, MassMg;   // line inventory: true content, nominal content, total incl. agent
    public long ChamberAmNg;                    // = AmPerEm · ContentMg; a trap (signature, hold power)
    public ushort QFeedTrue;                    // for the breach rule
    public long StateEndTick;                   // prime end, burst tick, purge end, arm end
    public bool Hot;                            // owner standby setting
    public bool Deferred;                       // burst deferred past the lock-in to T_exit + 181 (T_exit + 1 = TDrop, TDrop' after a cancel, or the throat exit)
    public bool PurgeQueued, EjectQueued;       // D17: commanded in cruise, executed on the first real-space tick
}
// D14 combined bubble: warp.md's WarpDriveSet (Part[8], Weight[8] = min(P_i, PRated_i), Fraction[8] = f_i, Effective, SupportKg)
// replaces this doc's former DriveGroup; the anti-chaining ship cooldown is warp.md's per-root-grid ShipWarpCooldownEnd.
public struct DampenerState { public int Component, Grid; public long MassInKg; public int AMaxMms2; public long MassMaxKg; public byte Starved; }   // m_in updated on events
// Tractor modes: tractor-beams.md's TractorPolarity (Pull, Push) and TractorDrive {Graviton, Reaction, Overdrive} (D28: no AM = Graviton, pull only)

public struct KickBudget { public int UsedMmS; public long LastTick; }   // per root body; integer mm/s, refills 100 m/s per second

public enum PulseSource : byte { Charge, Runaway, Chain }
public struct AgPulse
{
    public int Id, Owner; public PulseSource Source; public FrameId Frame; public double3 Centre;    // int64 1/1024 m on the wire
    public float YieldKg, Radius, VMax, WarpRadius, DrainRadius, NearRadius;                          // NearRadius: runaway brush
    public long Tick, WarpEnd;
    public long DrainKJ;                        // BombK · Y in kJ (integer); ×1 in a tunnel, × falloff per mouth outside; rung down over drainRingDownTicks
    public ushort Tunnel; public float TunnelS; // 0xFFFF = not in a tunnel; else its arc position, the collapse point if it empties the reserve
}
public static class PulseKick                   // shared by CollapseFlush, AG pulses, runaways, black-hole collapse
{
    public static void Apply(FrameId frame, double3 centre, double radius, double vMax, PulseSkip skip,
                             BubbleManager bubbles, RailsTable rails, Span<KickBudget> budgets, AnchorTable anchors);
}

// ---- Radiation ----
public struct RadSource { public int Grid, Component; public float3 Local, ShadowAxis; public float ShadowCos, ShadowSigma, GammaW, CullRadius; } // GammaW: 1 s average; CullRadius: where dose falls below the receiver floor
public sealed class InternalShieldTable { public float[] Atten; public int Sources, Rooms; }  // κ/(4πd²)·exp(−σ/σ0), baked with the hull
public struct DoseState { public float Gy, LifetimeGy; public long Tick, AntiradEndTick; public byte Chronic; }   // per character; acute recovers lazily, lifetime never does (D24)
public enum ContaminationKind : byte { HullActivation, BreachFallout }
public struct Contamination { public int Grid; public ushort Room; public ChunkCoord Chunk; public ContaminationKind Kind; public float ActivityGyS; public long Tick; }   // decays A·2^(−Δt/T½), lazily; dropped below the receiver floor

namespace BrutalGame.Frames;
public enum CelestialKind : byte { Star, Planet, Moon, GasGiant, BlackHole, Remnant }   // canonical list owned by frames-and-zones (planets.md adds GasGiant)
public struct BlackHole
{
    public int Celestial; public FrameId PreallocSurface; public byte Incarnation;
    public double Mu, KillRadius, InfallRadius, DiscInner, DiscOuter, JetHalfAngle, JetLength; public double3 SpinAxis;
    public long BirthForecastTick, BirthTick, ForecastTick, CollapseTick;   // CollapseTick server-only until the forecast
    public long InducedWorkMg, InduceSettledTick, InduceFillTick;           // D21 deliberate collapse: relaxes lazily, fill is a scheduled event
    public ulong Seed; public RemnantParams Remnant;
}
public struct RemnantParams { public double Radius, SurfaceGravity, DayTicks; public ulong TerrainSeed; public float QSurface, QGain, QExponent; }
public struct MinorBody { public AsteroidRef Ref; public FrameId Frame; public KeplerOrbit Rails; public float Radius; public int LastAnchorTick; }
public readonly record struct AsteroidRef(ushort Belt, uint Cell, ushort Index);
public struct AsteroidBelt { public int Parent; public double AMin, AMax, IncSpread, EccMax; public int Bands; public float Density, EmFraction; public ulong Seed; }

public enum AnchorState : byte { SpinUp, Holding, Crawling, Overload, ForceOverload, Releasing, Released }   // ForceOverload: public AnchorOverload alarm (D27)
public enum DropKind : byte { Impact, Orbit, Escape }
public struct DropFootprint { public DropKind Kind; public double3 ImpactPos; public long FallTicks; public double MaxEnergyJ; public float RadiusM; } // solved at engage and on core change
public struct StationAnchor                     // SoA in AnchorTable (256 on a server)
{
    public int Grid, Celestial; public FrameId Surface;
    public double3 Pos; public doubleQuat Rot;  // fixed in BodySurface (int64 1/1024 m persisted, C13)
    public double3 AHold;                       // ω×(ω×r) + μ r/|r|³ at the centre of mass, constant for the pose
    public long BilledMassKg, HoldMaxN, MaxRatedMassKg;   // HoldMaxN = Σ core ratings × Integrity
    public long EmRateNgPerS;                   // EmPerNs · m · |AHold|, integer, persisted; recomputed on mass change only
    public long ReserveEmNg, ReserveAmNg, SettledTick;    // lazy settlement
    public long EmptyTick, NextWarnTick;        // scheduled events, recomputed on rate or reserve change
    public DropFootprint Drop;
    public AnchorState State; public byte WarnLevel;
    public long OverloadEndTick, ReleaseTick;
    public double3 CrawlTarget; public long CrawlSegEndTick; public float CrawlSpeedMs;   // D25 crawl: one straight segment ≤ 1 h at a time
}
// BubbleAnchor gains Station (origin fixed at the station; re-snapped by whole 32 m steps only while crawling); GridFlags gains Anchored (any grid class).

namespace BrutalGame.Terrain;
public struct DepositLens { public double3 Dir; public float Depth; public float3 HalfAxes; public uint Rotation; public ushort QBase; public uint Seed; }
public sealed class DepositIndex { public DepositLens[] Lenses; /* hash: LOD-10 cell -> lens range */ public int Query(in ChunkCoord c, Span<int> hits); }
```

## Networking

| Event (reliable) | To | Size |
| --- | --- | --- |
| `DepositRecord` | The scanner's group / interest of an exposing edit / within 2 km of a cue | about 40 B |
| `AsteroidComposition` | The scanner's group / on exposure | about 16 B |
| `AgArmed` / `AgDetonation` (with source, and each drained wormhole with its amount) | Interest of the charge (50 km) / interest of the centre to `R_w`, plus the stability audience of each drained wormhole | about 24 / 48 B + 12 B per drained wormhole |
| `AmBreach` | Interest of the trap | about 32 B |
| `DriveAlarm` (state, cause, burst tick, line content) | Crew of the carried subtree; fleet leader | about 16 B |
| `RunawaySignature` | Interest of the ship (50 km) | about 24 B |
| `AnchorWarning` (level, time-to-empty) | Owner, station party, owners of docked ships; persisted | about 24 B |
| `AnchorFootprint` (kind, impact point, energy, radius) | Interest of the station and of the footprint; persisted with the station | about 32 B |
| `AnchorFailing` / `AnchorReleased` (tick, state, impact point; the release is a frame-change event) | Interest of the station and of its footprint | about 32 / 48 B |
| `BlackHoleForecast`, `BlackHoleCountdown` | Every client in the system, persisted | about 24 B |
| `BlackHoleCollapse` (tick, kick, new μ, remnant params and seed) | Every client in the system | about 64 B |
| `BlackHoleBirthForecast`, `BlackHoleBirth` | Every client in the system, persisted | about 48 B each |
| `BlackHoleInduction` (work fraction, contributing groups) | Every client in the system once past 25 %, on each 5 % step; persisted | about 32 B |
| `AnchorCrawl` (target, segment end tick, speed) | Interest of the grid and of its footprint, on each segment | about 40 B |
| `Contamination` (room or chunk, kind, activity) | Interest of the grid or chunk, on create and decon | about 24 B |

- **Inventories, batches, ledgers, drive lines and anchors are server state.** Owners get deltas on change; there is no per-tick traffic.
- **Nominal only:** clients receive nominal content and spread, never `NomErr` or true content, and never `WarpFuelTruth`. An off-spec outcome first reaches the crew at the mix tick, as a `DriveAlarm` or a spool abort.
- **Additive mixes** go to the crew in the plan and quote; third parties see only their effects (wormholes.md decides what commit reveals). Stabiliser state is wormholes.md's `StabiliserState`.
- **Thrust starvation** reaches the pilot as a flag in the existing ship snapshot (1 bit). A character's `DoseGy` rides in its own snapshot (2 B, owner only).

## Per-tick cost (estimates; measure with ImGui profiler rows)

| Item | Cost |
| --- | --- |
| Thrust accounting in the force pass | about 2 ns per thruster |
| Ledger and `PowerNet` (10 Hz, staggered) | about 1–3 µs per grid; 200 grids ≈ 10 µs a tick on average |
| Batches | Event-driven: a heap pop per completion |
| Crystal decay | 0 (lazy; settles on stasis power events) |
| Deposit lens lookup in chunk jobs | about 50 ns per chunk; +0.05 ms in ore chunks (worker) |
| Mining yield | about 20–100 µs per subtract op (worker) |
| Asteroid lookup / instantiated rails | about 5 µs per interest move / about 100 ns per body |
| Black-hole dose, heat, tidal stress (1 Hz) | about 50 ns per entity in range |
| Gravity-gradient torque | about 20 ns per body where `2μ/d³ > 1e-3` |
| Integrated bubble origins near a black hole | about 30 ns per bubble in the gradient zone |
| Rails kill-sphere and impact-wake scheduling | about 50 ns per element change |
| Kick budget | about 5 ns per kicked body, per event |
| Mine proximity (10 Hz, staggered) | about 100 ns per dormant mine |
| AM factories (10 Hz ledger) | about 20 ns each |
| Drive lines | event-driven (spool, mix tick, damage, purge); 0 per tick |
| Damage-to-breach checks | about 50 ns per component housing a damage brush reaches, per brush |
| Runaway burst | one `AgPulse` plus one near-field brush, per event |
| Station anchors | 0 per tick: lazy settlement, with warnings, empty and release as scheduled events. Rate recompute about 50 ns on mass change; drop-footprint solve about 1 µs on engage or core change. The station's own `PowerNet` runs in the ledger like any grid's |
| Anchored-zone membership | about 100 ns per entity near an anchored station, inside the existing 10 Hz checks |
| Onboard dose (1 Hz, staggered) | about 2 ns per (AM source, crewed room) pair: 16 sources × 64 rooms ≈ 2 µs per AM-burning ship per second |
| External dose (1 Hz, staggered) | about 50 ns per (source, receiver) pair above the 1e-6 Gy/s floor. Receivers come from the spatial hash inside each source's `CullRadius` (about 330 km for a heavy AM thermal at full), so a busy system needs the measurement |
| `InternalShield` bake | about 10 µs per (source, room) pair at hull bake, on a worker |
| Anti-graviton pulse | about 1 µs per affected body, plus about 2 µs per (warp, field) pair, plus about 50 ns per mouth tested for a drain (spatial hash), per event |
| Additive blend check | about 1 µs at plan and at charge start; 0 per tick |
| Stabiliser draws | one bus draw each in the existing ledger; 0 extra per tick |
| `gravitonProjection` lookups for tunnel retaliation | a byte read when a device in a tunnel frame changes state (event), plus the 60-tick re-strike (wormholes.md's cost) |
| Black-hole collapse or birth | about 2 ms once per event (10k element re-derives); can be a job |
| Anchor release | one transfer pass over the station's bubble (about 5 µs per body), once per event |
| Client black-hole visuals (lensing, disc) | about 0.1–0.3 ms GPU while on screen |
| Inertial dampener | about 2 ns per dampener in the force pass; `m_in` on events |
| Multi-drive plot | one mixer evaluation per participating drive (about 1 µs each), at plot and spool start |
| Anchor crawl | zero per tick unattended (`Frozen`, pose linear in the tick); one `MoveKinematic` per tick only while the crawling grid is in a bubble; segment boundaries are events |
| Contamination | 0 until a record exists; then about 2 ns per (record, nearby occupant) in the 1 Hz dose pass |
| Black-hole induction | 0 per tick: lazy relaxation, fill as a scheduled event |
| Falling station entry heating | about 1 µs per falling grid per tick, during a drop only |

Profiler rows: Resources, PowerNet, Batches, Mining yield, Deposits, Asteroids, BH hazards, BH origins, Pulses, Breaches, Drives, Anchors, Dose, Factories.

## Changes to other docs

**warp.md**
- **`WarpDrive`:**
  - replace `FuelPerLn` with `EmPerLn`, `EmMul`, `MassMin` and `Kind`;
  - add `MixLo`, `MixHi`, `FeedLo`, `FeedMax`, `Tolerance`, `AgentMinFrac`, `AgentRecovery`, `LoopAgentKg`, `PrimeKg`, `LineCapKg`, `PrimeTicks` and `SeparatorW`;
  - `AmPerEm` is global.
- **Fuel line:** two drive kinds (slurry burnt whole; recirculating with diluter and agent recovery), keeping the ln form, billed at `max(M, MassMin)`.
- **Validation step 1** uses `WarpFuelPlan`: EM content, AM, agent, loop and line capacity. "Out of cooldown" means the ship's cooldown, not the selected drive's.
- **Spool:**
  - a mix tick at spool start + 60, where weak or over-rich mixes abort the spool and off-spec recirculating mixes start a runaway;
  - Cold recirculating drives must prime for 30 s before spool;
  - `SpoolTicks ≥ 270` is enforced at load;
  - any runaway burst that would fall at or after `T0 − 30` is deferred to `T_exit + 181` (`T_exit + 1` = `TDrop`, or `TDrop'` after a cancel, or the throat exit: the first real-space tick, not T1), with prime yield. The lock-in is unchanged. A purge commanded in cruise is queued to `T_exit + 1`; an eject runs its 30-tick sequence in cruise and separates at `max(T_exit + 1, command + 30)`, unbraked (D17). *(Applied in warp.md.)*
  - **Override mid-warp:** warp.md's "only an override or sabotage can start one mid-warp" should read "only sabotage or interior damage" *(applied in warp.md)*. This doc keeps the override refused in `Warp` and `Throat` placement: a full-line override armed in cruise would arrive at the destination already armed, skipping the 10 s public arm in real space that D12's kamikaze bound relies on.
- **Explicit locks** (docking collar, landing clamp) also refuse priming and the drive override.
- **Anti-graviton fields:** spool is refused inside an active anti-graviton warp field (charges and runaways), and cancelled by one only before `T0 − 30`.
  - These fields join interdiction detection. A station's interdiction ban does not stop them; dampers do.
- **Fleet warp:** spool sync includes Cold priming; members' `DriveAlarm`s go to the leader; a member's runaway field cancels the fleet's spool before `T0 − 30`.
- **Multiple drives (D14):** warp.md's `WarpDriveSet` replaces the selector and "only one spools", on warp.md's "Multiple drives" rules: participating drives spool together into one bubble, quality (`V0`, `K`, `VReal`, `TReal`) averaged by delivered power `P_i`, weights `w_i = min(P_i, PRated_i)`, `VCap = Σ w_i f_i VCap_i / Σ w_i`, support `Σ f_i · MassCap_i`, at most 8 drives (largest `w`), spool the longest participant's, each drive paying its `w_i / Σ w` share by its own kind. **Cooldown and PRated (applied in warp.md):** each drive starts its own cooldown and the root grid stores `ShipWarpCooldownEnd` = the latest participant end (closes the revision 2 cooldown-chaining exploit: jump on drive A, unwire it, jump on drive B); `PRated` per class lives in `data/drives` at D:C:B:A = 1:4:12:40, base watts a warp.md knob. `WarpDepart` carries every participating `DriveId`, kind and weight.
- **Core eject is standard** on recirculating drives (D13), so warp.md's drive data drops the eject-mount option.
- **Targets:**
  - anchored stations: the band rule gives `BodySurface` inside the anchored zone; `MatchTarget` gives `U = 0`; the arrival ring is the hemisphere facing the approach. No floor applies (D16): a low or in-atmosphere arrival at a low station is legal, with warp.md's pressure wave, fines and blame.
  - black holes: `RouteShell(BH) = 1.1 × DiscOuter`, and warp exits inside it are refused (a hazard exclusion, not an atmosphere floor; warp.md 149/307 carry it).
- Charges can't be armed, and drive overrides can't start, in `Warp` or `Throat` placement.
- Speed and cost table, plus `data/drives` per class and kind.

**wormholes.md**
- Answers Q4 (EM source). "EM" means kg of content from stock at or above `MinPurity`.
- `MinPurity` per generator class (capital up to r = 400 m); crystal lens for capital generators and redirect catchers.
- Every EM debit carries `AmPerEm` of AM: the quote, the generator's reservation, catcher establish and capture (and candidate eligibility), `Φ_gate`, and `X_redirect`. An owner's cancel refunds AM. A forced cancel returns both reservations to the generator's tanks.
- **Travel economics (D23):** replace "warp cheaper, wormholes costlier" everywhere with "natural wormholes cheapest, manufactured wormholes fastest, warp the cheap way somewhere specific and relatively close". The manufactured-wormhole invariant is restated per generator class, against the largest ship that fits at `ρ_ref`, using the recirculating kind on fuel grade and including AM. The test logs the AM price at which each ratio reaches 1 (heavy binds at about 0.7 per gram). Owners' fees on natural pairs must stay below a ship's warp cost to the same place for "natural cheapest" to hold.
- Power comes from `PowerNet`. The charge must be full by `TCommit − 31`, with 1.25× catch-up draw, or it cancels as disabled.
- ~~Stall rule~~ (revision 2) is **replaced by the pulse drain** (revision 3; wormholes.md revision 3 already carries it): `BombK · Y · (1 − d/R_s)²` per mouth within `R_s = 5R`, `BombK · Y` inside a tunnel, rung down over 300 ticks, blocked at a mouth a powered damper covers; a zero collapses early at `Tz + 60`. Answers this doc's old open question 2.
- **Made in wormholes.md by the revision 3 review** (cross-doc consistency): the pulse-drain row (tunnel value 1×, ring-down), the destabiliser row (capped at `FeedCapW`), the drain order (ring-downs as timed slopes), the stability examples (prepared survival, pinning), the power-through bullet, the re-establish's "original X", the in-tunnel charge line, the early collapse point for ring-downs, open question 19 and its test line.
- A pulse at the host before `TCommit − 30` cancels the charge as disabled (reservations and additives return to its tanks). Catcher spin-up restarts inside `R`.
- **Quote AM** includes the `EsMax` term in `E` when cores supply it; additives carry no AM. The re-establish draws `0.25 · X`, its AM and 0.25 of the additives from the generator's own tanks.
- **Additives:** items, potency, crafting, the 2-kind and 10 % mass limits, and the purity gate on true content are here; effects stay in wormholes.md. A blend that fails the gate on true content refuses the charge start with nothing spent and no cooldown.
- **Stabilisers:** power only (Feed at class 2, Drain at class 3), crafted with throat-grade lenses (the projector also a crystal lens); proposed answer to wormholes.md open question 18.
- **Tunnel retaliation:** every function core declares `gravitonProjection` (None, Internal, External, per mode). Mines can't be deployed in a tunnel frame.
- **Suppression:**
  - new Natural terms: black-hole disc and jets;
  - the black hole's body-core Occupancy radius is `InfallRadius + r`, not `R + MouthFloor + r`;
  - no aftermath term.
- Rails mouths are exempt from `PulseKick` and from a parent's μ change: they keep their announced elements until they despawn (at most `TEnd`).
- The `CollapseFlush` kick moves into the shared `PulseKick`, keeping its skip list, which now includes anchored grids (their anchor pays). `V_flush` keeps its 80 m/s cap, and the summed kick is bounded by the per-body budget (stacked pairs: 160 → 100 m/s).
- **Flare dose becomes the shared `RadiationDose`:**
  - `σ0` per spectrum (flares 200, annihilation 400 kg/m²);
  - `ShieldTable` is reused for every external source;
  - `RadiationShield` counts against annihilation too;
  - "ships in warp or held in a throat are immune" applies to all dose. Runaway bursts are deferred out of `Throat` placement like out of warp;
  - dose is transient and lingering (D24): lifetime dose, chronic conditions and contamination records apply to flare and black-hole dose as well.
- The warp interdiction emitter stays power-only (unchanged).
- Q14: fees may be throat-grade EM.

**tractor-beams.md** (added by the round 5 review; that doc carries its own consumption and modes)
- **D28:** drop the Pumped mode (`PumpedFrac` 0.25, `PumpMWPerMN` 100) and the `pumped` field of `TractorState`. Without AM a beam runs **Pull** on graviton emission at full rating (`s = −1` only; a flip to push is refused with no AM in the emitter's traps); Push and Hold assist need the reaction. Bus power in pull mode is `BaseMW + 1 MW per km of R + GravitonWPerN · F_emit`; heat in pull mode is `0.2 · GravitonWPerN · F_emit` (tractor-beams.md's to set). *(Applied in tractor-beams.md, r6.)*
- Lock-Hold's spring-damper both pulls and pushes, so without AM it is a pull-only tether: it limits range and reels in, but cannot stop a target closing. It still blocks warp spool under the `m · 0.5 m/s²` rule. *(r6: tractor-beams.md owns modes; drive kinds `Graviton` (pull only), `Reaction`, `Overdrive`.)*
- The worked rates table gains a pull row (M towing a 300 t ship at 0.5 m/s²: 54 g/h of content, no AM, 2.15 MW).

**planets.md**
- `ExoticOre` and the remnant materials are **derived, never stored and never placeable**. Snapshots store generator material, and Add brushes place only `Placeable` materials.
- `DepositSeed` lenses in step 4, plus `DepositIndex`.
- Mining yield in the chunk job, with off-ring chunks generated on a worker.
- `PlanetParams` gains `DepositSet`, `InteriorModel`, and `DigDepth = Radius` for remnants. `Magnetosphere` is dropped: there are no belt scoops now that AM is industrial only.
- Remnant material by depth fraction, with yield density separate from gravity.
- Minor-body generator (3D SDF) for asteroids.
- `EditStore` keyed by body or `AsteroidRef`.
- `DayTicks` and surface gravity set every anchor's synchronous radius and cost (see the worked numbers).
- Re-entry heating for falling grids (Sutton-Graves, as for scoop lips) and fragmentation, so dropped stations mostly burn up (D25). Contamination records per terrain chunk for AM breach craters (D24); contaminated rock carries an item flag.
- The planet deposits are the main EM supply (D22).

**ship-hulls.md**
- **New function cores:**
  - warp drives per class and kind: slurry; recirculating with diluter, separator, agent loop, a separately rated reaction chamber (×8) and a standard core-eject mount (rated ×4); options inline assayer, auto-purge and quality separator;
  - `AntimatterTrap`, AM cell, `CellRack`, `AnnihilationCore`;
  - fusion, fission, solar, capacitor, battery;
  - AM thermal, plasma, chemical, ion and jet thrusters, each AM device with a shadow shield (directional σ, real mass);
  - `GravityPlating` (per room, floor gravity only), `InertialDampener` S/M/L (`a_max`, `MassMax`, covered rooms; D9), `TractorBeam` (pull without AM, push and hold with AM; D28), `TidalCompensator`, `GravityDamper`, `StasisCase`;
  - `CollapseInducer` (T4, D21), `MedBay` (T3 station core, D24), escape pods (D25);
  - concentrator, recrystallisers T1–T4, crude still T1c, crude recrystalliser T2c (ship-mountable), slurry blender, assayer (station and portable);
  - station-only cores: `AmFactory` S/M/L. `StationAnchor` S/M/L is mountable on any grid (D25);
  - `MagneticScoop` (EM dust only), `HorizonHarvester`, `AgCharge` casings, AM warheads, `AmLance`;
  - `Stabiliser` (battleship kit, support projector), `AdditiveLab` (station) and `AdditiveLab.Crude` (ship-mountable).
- Every function core carries `gravitonProjection` per mode; the build tool's Rad view gains a tunnel overlay showing housings within a projector's strike brush.
  - `FlareCollector` is removed.
- `SocketType.Antimatter`, `SocketType.Agent` (the closed agent loop) and `SocketType.Additive` (wormhole generators, two). `Fuel` sockets carry dry EM or slurry; a slurry drive's sockets accept only slurry.
- **Grid class `Station`** (orbital, anchored or surface base): it gates station-only cores. **Any** anchored grid has its thrusters and warp drive locked while anchored (the anchor's crawl inputs move it) and unlocked from `AnchorFailing`. The build tool shows a station's thrust-to-weight at its anchor pose, warns when escape-pod berths are fewer than crew berths, and refuses anchoring over an atmosphere with an `Impact` footprint without them (D25).
- **Shared component damage rule:** `Integrity −= min(1, 4 · V_overlap / V_housing) / Rating` per damage brush, in damage order, with `Rating` per housing (1 by default; recirculating chamber 8; eject mount 4). Thresholds per core (drive core 0.5 / 0.25, chamber 0; traps 0; anchors scale `HoldMaxN`).
- `EntryLimit` (heat flux, dynamic pressure) in ship materials for falling grids (D25).
- Contamination records per room (D24), fed by the `InternalShield` march.
- Breach damage brush on every grid in reach, and the same-room chain rule (next tick). Runaway near-field brush. Jettison and core eject through the existing detach rule.
- **`InternalShield` bake** per (AM source, room), next to `ShieldTable`. The build tool's Rad view uses it; the build tool also shows anchor drop footprints.
- `TidalLimit` and `ShearLimit` in ship materials.
- Plated area from room volume.
- Containment signatures for traps, cells and hot drive chambers.

**frames-and-zones.md**
- `CelestialKind`, `BlackHole`, `Remnant`, `MinorBody` (non-rotating, rails, no SOI) and preallocated remnant surface frames up to `MaxRemnants`.
- `BubbleAnchor.MinorBody` (origin rails fixed to the asteroid). *(Superseded: rings-and-asteroids.md proposes lane and field frames with `BubbleAnchor.Source` instead; frames-and-zones owns the enum.)*
- **Station anchors:**
  - `GridFlags.Anchored` (any grid class, D25; never over a minor body): a grid parented to `BodySurface` at any altitude (or to a Lagrange frame, frames' extension), with a constant local pose (piecewise linear while crawling), skipped by membership checks, static in Jolt (kinematic while crawling in a bubble), `Frozen` when unattended, crawling or not;
  - **`BubbleAnchor.Station`:** a static bubble in `BodySurface`, origin fixed at the station (32 m snapped), never rebased or refit. It uses Surface-bubble gravity, centrifugal and Coriolis, allowed above `RotatingTop` inside an anchored zone. Merges go into it (two station bubbles share the older origin); clusters that split off inside the zone stay in `BodySurface`;
  - **enter/leave table**, new row "Anchored zone": enter within `r_station + 2 km`, leave beyond `+ 2.6 km`, capped by the 4 km hard radius; entry by predicted closest approach (R7). Inside, the band rule returns `BodySurface` whatever the altitude, for bubble members only; unsimulated entities in the zone go to `Rails` in `BodyInertial` and are never frozen there unless docked or landed. Leaving above `1.3 × RotatingTop` transfers to `BodyInertial`;
  - **release:** the station turns dynamic with inertial velocity `ω × r`. Above the band the bubble converts to FreeFall in `BodyInertial` (merge transfer budget, ≤ 128 a tick). An unattended station goes to `Rails` with an impact-wake event at `TerrainMax + 2 km`;
  - anchored grids are in the `PulseKick` skip list; the anchor pays `EmPerNs · m · Δv`, and contact impulses and tractor forces on the static station are debited the same way.
- μ change at collapse re-derives rails elements, except mouths. Kill-sphere and impact-wake events for rails entities.
- Near compact bodies:
  - tidal bubble cap;
  - integrated FreeFall origins where `2μ/d³ > 1e-3`;
  - gravity-gradient torque and tidal stress;
  - tidal bound in `MaxAccel`.
- `Gravity` gains the interior model `μ·r/R³` below the surface for deep-dig bodies.
- Shared `PulseKick` (simulated bodies by scene, rails by elements, pose-cache refresh) with the per-body integer kick budget.
- Interior gravity (decided, D9; answers Q1): plating tops floor gravity up to its setting; a separate inertial dampener cancels the manoeuvre acceleration `a_man` (frames' term, rest acceleration excluded) for everything in its bubble up to `a_max` and `MassMax`, with leak-through. On anchored grids the rest acceleration is the constant `a_hold`, felt as floor gravity and never billed to a dampener. Frames' "(2–8 g, `F_cap`)" mention should read exotic-matter's 3/6/10 g S/M/L, which ship-design's catalog plans already evaluate to. *(Applied, r6: frames and ship-design now use `a_d`, `a_max`, `MassMax`; `F_cap`, `a_eff` retired.)*
- **Lagrange anchors:** frames-and-zones' "Lagrange frames and anchors" extends the anchor to `FrameKind.Lagrange` targets (orbit-averaged billing, orbit-maximum overload, worst-phase footprint); this doc's Station anchor section now points to it.
- Tick order:
  - step 3: flush → black-hole collapse or birth → anti-graviton pulses and runaway bursts (starting their stability ring-downs, which continue in wormholes.md's step 2c) → AM breaches → anchor failures → wormholes.md's zero test. Cascades go to the next tick; release transfers run in the next tick's step 3;
  - step 5: thrust accumulators, compensator `f_comp`, integrated origins;
  - a 10 Hz ledger and `PowerNet` slot next to membership (factories included; anchors are event-driven), and a 1 Hz dose pass.

**README.md**
- Q13 is already struck. Strike wormholes Q4 (decided here).
- Q2 (planet rotation) already describes the station anchor; point it at this doc's Station anchor section.
- Q3 is answered by D9: plating for gravity plus a separate inertial dampener (EM and energy, rated).
- Wherever README says "warp cheaper, wormholes costlier", use D23's order: natural cheapest, manufactured fastest, warp for close specific trips. *(Applied in README's Travel row, r6.)*
- Q9 (huge grids) now also covers anchored stations over about 1.4 km in radius.
- The documents table: exotic-matter.md is no longer "(Pending.)". Add drive kinds, runaways, industrial AM, radiation, pirates, anchors, additives and stabilisers to its description.
- Add the open questions below. Old questions 2 (bombs against wormholes) and 3 (perishable crystal) are answered by the revision 3 decisions; 4–13 by round 5 (D9, D12–D14, D20–D25).

## Milestones

| When | Scope | Exit check |
| --- | --- | --- |
| M2 | `ExoticOre` (derived), deposit lenses, `DepositIndex`, placeable flag | Lens chunks within +0.05 ms; no ore in client meshes or snapshots for unknown lenses; refill-and-remine yields nothing |
| M5 | `ItemStack` (with nominal error), `EmMath`, traps, tanks, `PowerNet`, ledger, function-core data, the shared component damage rule | Ledger ≤ 3 µs per grid; breach brush and chain tested |
| M6 | Thrusters (chemical, AM thermal, plasma, jet) with mass-flow accounting | Debits match the integrated ṁ within 0.1 % over 10 min |
| M7 | Slurry warp (EM + agent + AM), concentrator, T1/T2, slurry blender, bought AM cells, `AmFactory` S | Mixer deterministic; a jump debits exactly the plan |
| D1 (after M7) | Recirculating drives: line states, diluter and agent loop, mix tick, runaway triggers, purge, eject, auto-purge, lock-in deferral; crude refineries and assay; multi-drive group (D14); chamber rating and standard core eject (D13) | Every row of the trigger matrix reproduced; purge started at onset always prevents the burst; lock-in never broken; slurry drives never burst; default `q_mix` never runs away on unassayed feed |
| R1 (with M8 rooms) | `RadiationDose` shared with flares, `InternalShield` bake, shadow shields, crew dose (acute and lifetime), anti-radiation medicine, contamination records, station AM-thrust limit | Room dose within 1 % of the analytic point-source case; Rad view matches the 1 Hz pass |
| A1 (after M7) | Asteroid belts, minor bodies, anti-graviton charges and dampers | Lookup ≤ 5 µs; kick budget holds; pulse drains match the law in kJ on server and client; a damper over a mouth blocks its drain |
| S1 (after M8) | Station anchor: `GridFlags.Anchored` on any grid, position-and-velocity activation, `BubbleAnchor.Station`, anchored zone, consumption, overload, reserve warnings, drop footprint, release, crawl, thruster unlock, escape-pod rule; re-entry burn-up and fragmentation for falling grids | Debit within 0.1 % of `EmPerNs · m · |a_hold|`; zero at the equatorial synchronous radius; release continuous in inertial state; a 100 km release lands at the predicted tick ± 1 s and point; an unattended anchored station costs 0 per tick |
| W1/W2 | Grades, AM quote and debits, pulse drain (with wormholes' milestones) | Invariant tests pass, including AM, at tunnel prices |
| W3 (with wormholes' stability milestone) | Stabiliser power and crafting, re-establish debits, `gravitonProjection` data, tunnel hazards for EM and AM hardware | A struck charge detonates on the next tick; plating and compensators are never struck; re-establish debits exactly 0.25 X, its AM and 0.25 of the additives |
| X1 (after W3) | Additive items, labs (station and crude), potency, blend check, seed lattice decay | Gate holds on true content; effects match wormholes.md's factors; a failed gate spends nothing |
| C1 | AM weapons (warheads, lance), `AmFactory` M/L | Warhead dose matches the breach formula; lance muzzle dose matches the housing σ |
| B1 (after M8) | Black hole: celestial, hazards, tidal bubbles and integrated origins, torque and stress, compensator, H1–H3, T3/T4, crystal decay | A compensated harvester holds 20 km for 10 min; an uncompensated one breaks up; two bodies at rest at the origin drift < 1 m in 10 s |
| B2 | Collapse and birth events, remnant, debris arc, rebirth; `CollapseInducer` and induction | Event tick ≤ 2 ms (or a job); remnant terrain identical on clients; mouths unmoved |

## Files

- **`src/Resources/` (no Vulkan):** `Items.cs`, `ItemStack.cs`, `EmMath.cs` (blend, enrich, assay), `Recipes.cs` (Fixed, Enrich, Blend), `BatchRunner.cs`, `PowerNet.cs`, `ShipLedger.cs`, `AntimatterTraps.cs`, `AmFactories.cs`, `WarpFuelMixer.cs` (both kinds, truth), `DriveLines.cs` (line states, mix tick, purge, eject), `Runaway.cs` (triggers, deferral, burst), uses warp's `WarpDriveSet.cs` (D14 combined bubble), `Dampeners.cs` (D9), `Medicine.cs` (D24), `Additives.cs` (items, potency, blend check), `StabiliserPower.cs` (feed and drain draws; the component itself is wormholes.md's `Stabilisers.cs`).
- **`src/Frames/`:** `BlackHoles.cs`, `Remnants.cs`, `AsteroidBelts.cs`, `MinorBodies.cs`, `TidalZone.cs` (bubble cap, integrated origins, kill-sphere and impact-wake events), `StationAnchors.cs` (hold acceleration, rates, lazy settlement, warnings, drop footprint, anchored zone, activation, crawl), `CollapseInduction.cs` (D21).
- **`src/Physics/`:** `PulseKick.cs` (with `KickBudget`), `AgCharges.cs` (with stability drains), `GravitonProjection.cs` (classification lookups for tunnel retaliation), `TidalStress.cs` (stress plus gradient torque), `Breach.cs`, `ComponentDamage.cs` (shared Integrity rule), `AnchorRelease.cs`, `RadiationDose.cs` (shared with flares; lifetime dose), `Contamination.cs` (D24), `EntryHeating.cs` (falling grids, D25), `AmWeapons.cs`.
- **`src/Terrain/Voxels/`:** `Deposits.cs`, `MinorBodyGenerator.cs`, `RemnantMaterials.cs`. **`src/Terrain.Runtime/`:** `MiningYield.cs`.
- **`src/Ships/`:** `InternalShieldBake.cs` (next to `ShieldBake.cs`). **`src/Ships/Functions/`:** one file per function core above.
- **`src/Net/ResourceEvents.cs`.**
- **Client:** `RefineryPanel.cs`, `AdditivePanel.cs` (mix, gate headroom, blended factors), `FuelMixPanel.cs` (kind, `q_mix`, spread warnings, Hot/Cold), `DrivePanel.cs` (alarm, purge, eject, selector), `AnchorPanel.cs` (reserve, warnings, footprint), `PowerPanel.cs`, `RadView.cs` (build-tool overlay), `BlackHoleView.cs` with `lensing.glsl` and `accretion_disc.glsl`.
- **`data/`:** as listed above.

## Tests

- **EmMath:**
  - a blend conserves true and nominal content exactly in mg;
  - `Enrich` mass balance is exact in mg;
  - the residue's purity is below the input's;
  - inputs at or above `QCap` are refused;
  - caps hold;
  - auto-merge never moves content across a gate, and never merges assayed with unassayed;
  - a blend's `|NomErr|` never exceeds its `Spread`;
  - assay zeroes the error and shows the true purity;
  - crystal decay is the same evaluated lazily or at every tick, including stasis power toggles;
  - decay and shock never take a stack below `DecayFloor`; a stack that leaves the Crystal band keeps decaying to the floor and then stays stable;
  - no recipe or source exceeds `QMax`, and the loader refuses data that would.
- **Additives:**
  - the gate holds on true content: a blend just under `MinPurity` on true content refuses the charge start, spends nothing and starts no cooldown, even when it passed on nominal content;
  - more than 2 kinds or more than 10 % of X by mass is refused;
  - effects scale linearly to `FullActive` and stop there; trade-offs scale the same way and multiply when they share a factor;
  - EM consumed in an additive recipe never appears as EM content in the product or the blend;
  - seed lattice potency decays to its floor exactly as crystal does, lazily;
  - additives are reserved, spent, refunded and re-drawn at 0.25 exactly with X.
- **Stabilisers:**
  - feeds draw at class 2 and drains at class 3; curtailment follows the classes;
  - a stabiliser holds no EM or AM, and its destruction breaches nothing.
- **Mixer:**
  - a slurry plan never leaves `[MixLo, MixHi]` nominally;
  - a slurry drive's sockets refuse dry EM;
  - a recirculating plan keeps the feed in `[FeedLo, FeedMax]`, adds crude only while the blend stays ≥ `FeedLo`, and never exceeds `LoopAgentKg` or `LineCapKg − Prime`;
  - the interlock refuses feed above `FeedMax`;
  - the default `q_mix` is `min(MixTarget, MixHi · (1 − Spread))`, and at it no fuzzed unassayed feed runs hot;
  - deterministic with tied purities;
  - bills `max(M, MassMin)`;
  - `m_burnt` matches the formula for both kinds.
- **Warp:**
  - the debit equals the plan (true content, agent loss, AM);
  - an abort refunds nothing;
  - fleet members pay their own by their own kind;
  - a Cold drive can't spool before priming ends;
  - a docked drive can't prime or override.
- **Runaway:**
  - every trigger row fires on the predicted tick, and no other case does;
  - the true line purity equals `q_mix / (1 + e)` for fuzzed unassayed feed;
  - true `q_feed ≤ q_lim` never runs away under any damage;
  - slurry drives never burst (fuzzed damage and off-spec slurry);
  - a purge started at onset with `Integrity ≥ 0.25` always prevents the burst;
  - eject moves the near-field brush off the ship;
  - an off-spec burst always lands before `T0 − 30`;
  - a breach onset in the last 210 ticks, or a chamber rupture in the last 31, defers to `T_exit + 181` with prime yield (`T_exit + 1` = the first real-space tick, `TDrop` or `TDrop'`), including when the warp is aborted, interdicted or hard-cancelled by the breach itself (warp.md), and the ship lands where its commit announced;
  - D17: a purge commanded in cruise starts on the first real-space tick and beats the burst; an eject commanded in cruise separates at `max(T_exit + 1, command + 30)` and is 60 m (D, C) or 120 m (B, A) clear at the burst; a runaway started in cruise by interior damage hard-cancels the warp and bursts with prime yield at `T_exit + 181`, after an eject commanded in cruise has separated by at least 60 m; the override is refused in cruise;
  - the override is refused with feed at or below `q_lim`, in `Warp` or `Throat` placement, and while locked;
  - the damage-to-breach rule gives identical `Integrity` on replay;
  - the burst's pulse yield is capped at 100 kg, and its warp field counts toward the 4-per-SOI limit;
  - multi-drive (D14): participants spool together; the combined `V0`, `K` equal the `w`-weighted averages (`w_i = min(P_i, PRated_i)`) and `VCap = Σ w_i f_i VCap_i / Σ w_i`; a drive with `f_i < 0.5` doesn't join; at most 8 join (largest `w`); `M ≤ Σ f_i · MassCap_i` is accepted and anything above is refused; after the lock-in, losing one drive continues the warp while the remaining support carries `M` and hard-cancels otherwise; each drive debits exactly its share by its own kind; one participant's runaway or bad mix aborts the shared spool before `T0 − 30`; no toggle mid-spool; the root grid's `ShipWarpCooldownEnd` is the latest participant cooldown end, so chaining drive A then B is refused until A's cooldown ends;
  - chamber rating (D13): a brush that takes the core to 0 takes the chamber down by an eighth; a chamber rupture needs at least eight such hits;
  - core eject: works at any core `Integrity` (own housing), off-key, and mid-warp (commanded in cruise, separating at `max(T_exit + 1, command + 30)`); the near-field brush misses the ship for every C-class line and, at the B/A eject speed, every A-class line.
- **Wormholes:**
  - per generator class, the cheapest wormhole costs more than warping the largest reference ship that fits (recirculating kind, with AM);
  - fleet, dense-ship and AM-price break-evens are logged (heavy about 0.7, capital about 1.85 per gram);
  - an owner's cancel refunds AM and X;
  - a forced cancel returns both to the tanks;
  - a catcher without AM is not a candidate;
  - a re-establish debits `0.25 · X` and its AM from the generator's own tanks, and is refused if they are short.
- **PowerNet:**
  - curtails by class;
  - containment (including anchor control) is never curtailed;
  - a charge short at `TCommit − 31` cancels;
  - catch-up recovers a short shortfall.
- **AM production:**
  - grams produced equal `∫P dt / AmProductionJ` exactly in ng with the remainder kept;
  - the round trip through a core returns 20 % (no free energy);
  - full traps stop production;
  - factories refuse to mount on non-Station grids.
- **Traps:**
  - the buffer empties to a breach on the tick predicted;
  - a jettisoned trap keeps its buffer;
  - the same-room chain fires on the next tick, and a different-room one doesn't.
- **Radiation:**
  - onboard dose per room equals `Σ GammaW · att` from the bake;
  - `InternalShield` matches a brute-force march;
  - the shadow cone applies only inside its half-angle;
  - external dose uses the `ShieldTable` direction;
  - the off-cone point-source figures match the formula (0.27 Gy/s at 100 m for a light plasma drive at full);
  - crew thresholds and lazy recovery match per-tick evaluation;
  - lifetime dose only grows; medicine raises acute recovery while it acts and never lowers the lifetime dose; chronic bands apply at their thresholds and the `MedBay` treatment drops one band;
  - a breach creates contamination records only in rooms its pulse reaches above 1 Gy; activity decays lazily to the same value as per-tick evaluation and is dropped below the receiver floor;
  - the station AM-thrust limit clamps non-permitted ships only, inside `AmLimitRadius`.
- **Station anchor:**
  - the debit equals `EmPerNs · m · |a_hold|` within 0.1 % over a day, lazily settled or not;
  - `|a_hold| = 0` at the equatorial synchronous radius and > 0 off the equator;
  - the rate changes only on mass change;
  - overload refuses docking and fails after the grace;
  - warnings fire at each threshold of time-to-empty, as scheduled events;
  - owner actions that would starve a holding anchor are refused; release takes the 60 s countdown;
  - engaging is refused when the drop footprint meets a non-consenting station's zone;
  - release gives inertial velocity exactly `ω × r` and no proper-acceleration spike;
  - a 100 km release impacts at the Kepler-predicted tick ± 1 s and the published impact point;
  - a 3,000 km release enters orbit, and a 10,000 km release escapes;
  - an entity crossing the anchored zone keeps its inertial state to 1 ulp;
  - a ship left hovering in the zone with no anchor goes to rails and falls;
  - an unattended anchored station costs 0 per tick and still falls and impacts when released;
  - `PulseKick` never moves an anchored grid and debits its anchor; rams and tractors debit it too;
  - any grid class can anchor; engagement is refused beyond `EngagePosTolM` or `EngageMaxVelMs`, and a residual below the limit is debited as an impulse; anchoring over an asteroid is refused;
  - a crawl moves at most `CrawlMaxMs`, its debit equals the per-segment rates, it stops at a segment whose footprint would cross a non-consenting zone, and an unattended crawling grid stays `Frozen` (no body) yet arrives at the same tick and pose as an attended one;
  - thrusters unlock at `AnchorFailing`; engagement with an `Impact` footprint over an atmosphere is refused without pod berths for the crew berths;
  - a station released high into a thick atmosphere sheds most of its mass as burnt-up debris, and a low release over an airless body arrives whole; the footprint check never credits burn-up.
- **Inertial dampener and plating:**
  - plating's debit depends on the supplied top-up `g_top` only (zero for a 1 g plate on a 1 g landed ship);
  - the dampener's debit equals `EmPerNsDamp · m_in · a_d` and its draw `DampWPerN · m_in · a_d` within 0.1 % over a burn, and nothing but standby when coasting, landed or anchored (rest acceleration is not billed);
  - overlapping dampeners: a room takes the highest `a_max`; four S cores over one room still leak above 3 g; a docked ship's own dampener idles while its host's covers it;
  - above `a_max` or `MassMax` the excess leaks through exactly; starved, `a_d` ramps to 0 over 0.5 s;
  - the dampener never draws AM and is never struck in a tunnel.
- **Tractors:** with no AM in its traps a tractor's force on the target always points at the emitter; push and hold are refused; with AM they work and debit `AmPerEm`.
- **Deliberate collapse:** induced work relaxes lazily exactly as per-tick evaluation; the fill schedules `CollapseTick = fill + 24 h` (never later than the natural tick); progress events go out from 25 %; attribution splits by delivered work.
- **Pulses:**
  - Δv is identical across masses;
  - interiors log no spike;
  - the kick budget caps chains over consecutive ticks;
  - each mouth's drain equals `BombK · Y · (1 − d/R_s)²` in integer kJ, identical on server and client; inside a tunnel it is `BombK · Y` at the pulse's `s`;
  - the ring-down removes exactly `D` over 300 ticks on server and client; feeds count during it; an unfed full r = 100 m reserve empties under a 10 kg point-blank pulse and a prepared one survives 22.5 kg; destabiliser drain stops at `FeedCapW`;
  - a second re-establish after a second collapse costs 0.25 of the original X;
  - a drain that empties the reserve collapses the wormhole at `Tz + 60` (only when that is before `TClose`), with the tunnel collapse point at the pulse's `s` or the drained mouth's end;
  - a pulse that leaves the reserve above zero changes only the band: `TClose`, flow and X are unchanged;
  - spool lock-in holds;
  - catcher spin-up restarts;
  - dampers void kicks and warp fields inside their radius, and block drains at mouths they cover;
  - a mine without power becomes a dud.
- **Tunnels:**
  - every `External` device in a tunnel frame is struck on the tick it activates; `Internal` and `None` devices never are;
  - an armed charge in a tunnel is struck at once and detonates on the next tick;
  - deploying a mine in a tunnel frame is refused;
  - a trap offline for the longest discharge outage (10 s) never breaches from the outage alone;
  - a stasis case offline settles its crystal for exactly the outage.
- **Deposits:**
  - lenses identical across runs;
  - no lens geometry reaches a client without a record (meshes or snapshots);
  - yield equals the integral of `q(p)·mass`;
  - placed rock never yields.
- **Asteroids:**
  - the rigid-band lookup finds a body at ±1 tick after 1e9 ticks;
  - epicycles stay within one neighbouring cell;
  - edits persist across re-instantiation;
  - a `MinorBody` bubble never refits off the asteroid.
- **Black hole:**
  - gradient torque aligns a rod radially;
  - tidal stress damages above the limit only;
  - the compensator at `f_comp = 1` holds;
  - an integrated origin keeps co-moving bodies within 1 m over 10 s at 20 km;
  - the tidal bubble cap splits bodies before Jolt's velocity clamp;
  - a rails entity with periapsis inside `KillRadius` is destroyed at the predicted tick.
- **Collapse and birth:**
  - infall destroys only inside `InfallRadius`;
  - rails orbits re-derive with continuous state;
  - rails mouths are unmoved;
  - interior gravity is finite at the remnant's centre;
  - remnant parameters are identical on server and client;
  - late joiners get the forecasts.

## Open questions for the user

Numbers are kept so in-text references stay valid. Questions 2–13 are answered and removed: 2 and 3 by revision 3 (decisions A and H); 4 and 13 (AM energy and production scale) by D20; 5 (plating) by D9; 6 (warp economy of scale) by D23; 7 (remnant abundance) by D22; 8 (deliberate runaway) by D12; 9 (runaway severity) by D13; 10 (radiation persistence) by D24; 11 (anchor scope) by D25; 12 (multiple drives) by D14.

1. **Black-hole placement** (life is answered by D21: very long, with deliberate collapse; time does not advance on an empty server):
   - Should every system's slot be filled, and should the Dark Gate's far end be intra- or inter-system?
   - Should later incarnations get their own natural gate?
   - After `MaxRemnants` (8) collapses, should the slot go dormant (proposed), or should rebirth continue with old remnants kept? (With lives of years and costly forced collapses, this may never be reached.)
14. **Decay floor and cap values:** the floor is 0.96 (T3's cap, so decayed crystal is throat grade and one T4 pass regrows it) and `QMax` is 0.995. Is that the floor you meant, or should it sit lower (for example the Throat band's bottom, 0.85) so neglect costs more?
15. **What purity buys inside the Crystal band:** in v1 only margin (time and shocks before leaving the band) and price. Should a crafted lens or a seed's grade also scale with purity (for example a better lens raising a generator's `MaxRadius` a little), making the top of the band a quality tier?
16. **Additive scope:**
    - Generators only (proposed), or also catchers, gates and redirects (for example masking for interdictors, or a gate's flow bias)?
    - Mixed at the generator only (proposed), or a tradeable pre-doped EM item, which needs a per-stack composition and breaks the 32 B stack?
    - Are the seven families and their recipes (residue, rare and heavy metals, disc inclusions, AM, crystal fines) the right inputs, and should pirates' crude lab make more than binder, flux and catalyst?
17. **Stabiliser cost:** power only (proposed, same as wormholes.md open question 18), crafted with throat-grade lenses and a crystal lens for support projectors. Or should feeding also burn field-grade EM plus AM like other gravity devices, making a long hold cost fuel rather than only installed power?
18. **Bombs in tunnels:** an armed charge in a tunnel is struck at once and detonates a tick later, draining its point-blank value (`BombK · Y`, rung down over 5 s) at the bomber's own position. A 10 kg yield collapses an unfed r = 100 m tunnel; a prepared one needs 22.5 kg. Owners cannot refuse entry, so the bore is where a bomb is surest to land. Is a suicide bomb in the bore an acceptable tactic (proposed: yes, the bomber rides its own pulse and collapse), or should arming be refused in a tunnel frame entirely, or should owners get an admission filter?
19. **Dampers for fleets:** gravity dampers are station cores, so a fleet in the field powers through with feed and overcharge only, and dampers protect station gates completely against outside drains. Should a heavy ship-mountable damper exist (for example battleship-only, 1 km radius), or is "stations block bombs, fleets out-feed them" the intended split?
20. **Plating on gravitons (new, round 5):** D28 makes graviton emission (EM and power, no AM) a pull-only mode, and D9 puts the dampener on it. Plating only ever pulls toward the floor, so it could run AM-free too (proposed: yes, `AmPerEm` 0 for plating). Keep plating on the full reaction instead?
21. **Deliberate collapse cost (new, round 5):** `CollapseWork` 2,000 kg of throat content plus 10 kg of AM (about 100 inducer-hours, a coalition's AM stockpile), a 24 h public countdown after the budget fills, and the inducers' owners attributed for the damage. Is that the scale you want, or should forcing a collapse be cheaper (a faction project) or dearer (practically a server event)?

All costs are estimates. Nothing was built or run, and no repo files were changed.

## Review notes

**User decisions**
- **All nine decisions are present.** Two needed tightening to stay faithful.
  - "Only the most advanced harvest directly": H1 and H2 needed only shields and a tractor. Every black-hole tier now needs T3+ cores; H3 (crystal) is T4.
  - "Harvested in its entirety" holds through `DigDepth = Radius`, and needed the interior gravity fix below.
- **Plating:** "settles the gravity-plating question" became a proposed answer. Q3 is still the user's open question.

**Contradictions with the other docs**
- **Disc trap was impossible.** Wormholes.md's Occupancy body core is `R + MouthFloor + r` (named `WarpFloor` at the time), and a 2,200 km black-hole floor (warp's `RouteShell(BH)`) would cover the whole disc. Interdict catchers can't waive Occupancy, so "a captured end can open in the disc" could never happen. The black hole's core radius is now `InfallRadius + r` (64 km). That also keeps mouths out of the future remnant's volume.
- **Collapse moved announced mouths.** The shock kick and the μ re-derive would move rails mouths, but wormholes.md says B is never moved after announcement. Mouths and wormhole frames are now exempt and keep their elements until `TClose`.
- **Kick cap broken by its own shock.** The black-hole shock was 150 m/s against the shared 100 m/s cap; it is now 100 m/s.
- **Kick cap reworded.** "The flush cap rises to 100" was wrong: `V_flush` keeps its 80 m/s cap, and only the summed kick is bounded.
- **Kick budget.** A per-tick cap let charges on consecutive ticks stack without limit, so it became a per-body budget that refills over 1 s.
- **Stall could void a whole wormhole.** A 60 s stall plus 2× immunity left a 30 s wormhole with nothing, an early collapse in all but name. The stall is now capped at 0.25 · `OpenTicks`.
- **Stall vs the throat hold.** The stall now spells out the hold, `ThroatHoldMax` and `MouthQueue` interactions.
- **Aftermath term removed.** It was a hard Occupancy term evaluated at commit, which let a 4 kg crude charge push a paid wormhole out of a gate's catch slot. It was a cheap interdiction with no catcher, and a sustained gate denial.
- **Interdiction emitter kept power-only.** README and wormholes.md decided warp interdiction costs power only; the draft silently made it consume EM.
- **Charge starvation made deterministic.** "Any tick short" cancelled the charge, and force-curtailing life support in the lock-in window was a hack. Now the charge must be full by `TCommit − 31`, with catch-up draw and a cancel otherwise.
- **AM on every EM debit.** Catchers, `Φ_gate` and natural redirects spent EM without AM, contradicting "every gravity device runs on the reaction".
- **Forced cancel defined.** Wormholes.md never said what happens to reserved EM when a charge is cancelled by disable or destruction. The reservations now return to the generator's tanks.
- **Capital generator size.** Capped at r ≤ 200 m, against wormholes.md's 400 m maximum; corrected to 400 m.
- **Graviton lens bootstrap.** The crystal row listed a crystal lens for the horizon harvester, while H3 is the only pre-remnant crystal source and its own table said throat-grade. That was a bootstrap deadlock; it is throat-grade now.
- **Dark Gate MOID.** A co-orbital orbit with the same elements has MOID 0 and fails natural-mouth validity. It now has an inclination and eccentricity offset, and is kept out of the jet cones.
- **Warp time to the black hole.** "≥ 20 AU, about 75 min" fixed to 20–30 AU, 50–75 min (C class).

**Physics and precision near the black hole**
- **"No bubble size cap is needed" was wrong.**
  - At 20 km the gradient is 5 s⁻²: bodies 500 m apart diverge at 2.5 km/s².
  - That breaks the free-fall rebase rule (it would fire every tick) and the small-relative-velocity premise, and hits Jolt's 500 m/s velocity clamp.
  - Added a tidal bubble cap (one ship per bubble near the horizon) and the tidal bound in `MaxAccel`.
- **Integer `PeriodTicks` rails don't satisfy Kepler's third law exactly.** At a 240-tick period the rounding gives up to ~200 m/s² of spurious acceleration against `g` = 50,000 m/s². Bubble origins in the gradient zone are now integrated in double with the same `g()`.
- **Rails entities could pass through the kill sphere unnoticed.** Destruction is now scheduled from the periapsis when elements change.
- **Remnant interior gravity.** The point-mass `μ/r²` diverges at the centre of a body dug to its core; a uniform-sphere interior model was added.
- **`MinorBody` bubble anchor.** A FreeFall bubble at an asteroid would refit its origin away from the static chunks.
- **Escape figures.** "13 km/s from the disc edge" was really the escape Δv from 20 km. The descent from the disc edge is about 16 km/s.
- **Gradient-torque threshold** raised to 1e-3 s⁻², so remnants (1.5e-4 at the surface) don't pay for it.

**Exploits**
- **EM duplication.** "Purity is a function of the lens" plus Add brushes would let "mine ore, refill with rock, mine again" print EM, and the same on remnants. Ore and remnant materials are now derived only from generator material and are never placeable.
- **Snapshot leak.** Ore stored in chunk snapshots leaked hidden ore to joining clients. Snapshots now store generator material, and ore is re-derived at load.
- **Antimatter as a weapon.**
  - Hold power of 10 kW/g made a 10 g cell need a 720 kg battery; it is now 10 W/g.
  - The breach brush (a 10 g cell made a 65 m hole) is now mainly radiation with a smaller blast, and hits every grid in reach.
  - Traps have a scannable containment signature for station screening.
- **Bomb griefing:**
  - mines had no cap and would self-detonate when their battery died; they now have a per-group cap and expire as duds;
  - casings have a yield cap;
  - dampers protect whatever is inside their radius, not only pulses centred inside it, so a charge just outside 5 km can't stall a station's gate;
  - cascades go one hop per tick.
- **Black-hole events.**
  - Rebirth could spawn a black hole on top of a station, so births are now forecast 7 days ahead with MOID clearance.
  - The infall radius was secret (it depended on the remnant's size); it is now a public 64 km, so camping the collapse is fair.
- **Warp drive overspec.** Lower `EmPerLn` on big drives needed `MassMin`, or small ships would fit A drives for cheap fuel.
- **Inventory fragmentation.** Every mining op made a new stack. Stacks now store content, so auto-merge buckets aligned to every gate can never cost value.

**Arithmetic and economy**
- **Asteroid content was off by about 1,000×.** A 100 m EM asteroid at 10 % fill and q 0.01 holds about 1,500 t of content, not 1.3 t. Fill is now 1 %, giving about 150 t.
- **Disc debris was too rich.** As stated it held thousands of tonnes of field-grade EM per chunk, so it is now rock with 1–3 % EM inclusions.
- **Remnant yield.** The remnant used the gravity-implied density (2.6e5 kg/m³), which would yield 260 t per voxel. Yield density is now separate (3,000 kg/m³), and the surface purity was lowered from 0.35 to 0.08, so the remnant doesn't instantly make all warp fuel free.
- **Throughput.** Stated explicitly that concentrator and drill throughput, not deposit size, is the faucet.
- **The restated "cheaper" invariant held only for r = 25 m.** Wormhole cost grows with r², while the mass of a ship that fits grows with ρ³. With one `EmPerLn` for every class, any ship over about 6,000 t that fits a heavy or capital mouth finds a wormhole cheaper than its own warp, breaking README's "warp cheaper, wormholes costlier". Fixed with per-class `EmPerLn` plus `MassMin`, and an invariant checked per generator class at a reference density (new open question 7).
- **Enrich could lower purity.** For `q_in ≥ QCap` it gave a purity drop and a residue purer than 1; such inputs are now refused, and the residue-below-input test now provably holds.
- **Smaller fixes:**
  - fighter propellant 2.6 t → 3.3 t;
  - shear-damage radius 0.4 R → 0.5 R;
  - asteroid purity "×10" → about 15×;
  - dose units given;
  - `ItemStack` is now 32 B with content;
  - `WarpFuelPlan` needs `unsafe` for its fixed buffer.

**Perf**
- **No new all-entity per-tick cost.** Added rows for:
  - integrated origins (per bubble, gradient zone only);
  - kill-sphere scheduling (on element change);
  - the kick budget (per kicked body, per event);
  - mine proximity checks (10 Hz, per mine);
  - birth events.
- Crystal decay stays lazy; stasis power changes arrive as ledger events.

## Revision 2 notes

What changed against the first draft, decision by decision. The Review notes above are kept as written for the first draft, so their question numbers refer to the old list.

**Decision 1: runaway burst**
- New section "Runaway burst":
  - the line states (Cold, Primed, Charged and the rest);
  - the purity limit `q_lim = MixHi · (1 + Tol)`;
  - four triggers: off-spec mix, diluter breach, chamber rupture, and the owner override;
  - a deterministic damage-to-breach rule, the same component `Integrity` rule now used by traps and anchors;
  - the 3 s alarm with purge, auto-purge, core eject and Cold running;
  - the near-field brush `10 m · Y^(1/3)`, plus a far field through `PulseKick` as an `AgPulse` with source `Runaway`, capped at the 100 kg casing yield.
- **Slurry drives and low-grade feed are immune by rule:** a breach with true `q_feed ≤ q_lim` only disables the drive.
- **Lock-in kept:** off-spec bursts always land before `T0 − 30` (`SpoolTicks ≥ 270` is enforced). Bursts that would fall in the lock-in window are deferred to after arrival with prime yield.
- The first draft's "interlock refuses, no override in v1" became "interlock refuses; the override is the only bypass" (open question 8).
- The reaction makes anti-gravitons, not gamma, so a burst has no dose pulse of its own.

**Decision 2: industrial antimatter**
- The "Sources (proposal)" table and old open question 1 are gone.
- AM is produced only by `AmFactory` S/M/L (station-only) and seeded faction foundries, at 2 TJ per gram (lowered from the draft's 10 TJ so an early fission plant makes about a gram a day). The round trip is 20 %.
- Belt scoops, flare collectors and black-hole jet AM are removed. H1 jet skimming now yields EM dust only (1–4 kg/h). `PlanetParams.Magnetosphere` and the `FlareCollector` core are dropped.
- New "Radiation from annihilation" section:
  - one dose formula with `κ` and `σ0 = 400 kg/m²` for annihilation;
  - `ShieldTable` reused for external sources, and a new baked `InternalShield` for onboard sources;
  - shadow shields as real mass;
  - crew dose thresholds and recovery, a design limit and the Rad view;
  - a station AM-thrust limit.
  - Flares keep `σ0 = 200` on the shared path.
- **Thrusters:** AM thermal light and heavy added as the conventional AM engine. Every AM device has `f_rad` and a shadow shield. Dose examples show standard against crude shielding.
- **New "Antimatter weapons":** torpedo warheads (breach model) and the annihilation lance (muzzle loss irradiates a crude shooter).
- The breach dose formula is now defined (`κ · f_rad · E / 4πd²`).

**Decision 3: drive kinds**
- Warp fuel is rewritten around **class × kind**:
  - slurry (EmMul 1.5, all agent consumed, slurry 0.10–0.25, no line, no runaway);
  - recirculating (EmMul 1.0, feed 0.40–0.85, line 0.25–0.40, R 0.95, prime, line cap and loop per class).
- The old per-class purity windows (D 0.15–0.30 … A 0.40–0.75) and the content-0 "carrier" are replaced by the kind windows and the `agent.mix` item. Slurry is an EM-family item.
- New tables:
  - kinds;
  - classes, with prime, line cap, loop and capex;
  - cost per jump for both kinds;
  - capex against opex at reference prices (recirculating about 35 % cheaper to run, 3–5× lighter, about 3× the capex; break-even about 28 jumps for a 5,000 t hauler and about 460 for a 300 t ship);
  - failure modes by kind.
- "Higher grade slightly more available" is a market knob (`SlurryMarketWeight` 0.8). "Faction decision" is `data/factions` plus faction-keyed options.
- The wormhole "cheaper" invariant now uses the recirculating kind and includes AM. The test logs the AM price at which each ratio reaches 1 (heavy binds at about 0.7 per gram; see Revision 2 review notes).

**Decision 4: pirates**
- New "Pirates and improvised kit" section:
  - crude refinery cores (T1c, T2c; no licence, ship-mountable, unassayed output with 15–20 % spread);
  - mixed fleets (spool sync with priming, alarms to the leader, one runaway cancels the fleet's spool);
  - dual-drive ships (selector, 10 s idle-only switching, no double spool, auto-failover when idle, per-drive damage, the kind in `WarpDepart`);
  - salvage (faction-keyed options inert off-key).
- Supporting model:
  - `ItemStack` keeps 32 B by splitting `Flags` into `Flags`, `SpreadQ8` and `NomErrQ15`;
  - stacks carry a hidden nominal error and a public spread;
  - assay (station and portable), and merge rules that keep assayed and unassayed apart;
  - devices debit true content.

**Decision 5: station anchor**
- New "Station anchor" section:
  - `a_hold = ω×(ω×r) + μr/|r|³`, rate `EmPerNs · m · |a_hold|` (1e-12 kg/N·s, field grade, plus AM);
  - worked numbers for a 1 g, 1000 km planet with a 6 h day at 10, 50, 100, 500 and 1,000 km, the synchronous radius (3,876 km) and 10,000 km, with fall times and impact speeds on release, plus latitude and 0.27 g notes;
  - founding, capacity and overload, reserve and warnings, failure and release, and impulses paid instead of kicked.
- **Frames changes:** `GridFlags.Anchored`, `BubbleAnchor.Station` (static, never rebased), the anchored-zone row, release transfers and impact-wake events, and the `PulseKick` skip.
- The anchor joins the gravity-device table, the containment power class, the tick order (after AM breaches), networking events, per-tick costs, data, C# (`StationAnchor`, `AnchorTable`), milestone S1 and tests.

**Other edits**
- The Recommendation and decisions sections are rewritten to cover the five decisions.
- Bucket edges are now derived by the loader from data.
- The Progression table is updated: AM bought, then made; slurry early, recirculating later; anchors from T2.
- Data, C#, networking, per-tick costs, changes to other docs (warp, wormholes, planets, ship-hulls, frames-and-zones, README), milestones (D1, R1, S1, C1 added), files and tests are all extended.
- **Open questions:** the old question 1 (antimatter source) is removed and the rest renumbered: old 2–8 are now 1–7, and in-text references are updated. New questions 8–13: deliberate runaway, counterplay severity, radiation persistence, anchor scope, dual drives, AM production rate.

All costs are estimates. Nothing was built or run, and no repo files were changed.

## Revision 2 review notes

A skeptical pass over Revision 2 against warp.md, wormholes.md, frames-and-zones.md, review.md and README.md. Every worked number was recomputed (the anchor table by integrating the release trajectories). Corrections are already applied in the text above; this lists what changed and why.

**The five decisions: present and faithful**
- **1, runaway:** present. Pure EM dumped into a drive (the override, or off-spec stock) and damage (diluter breach, chamber rupture) both trigger it. Slurry drives can't run away: dry EM is refused by their sockets, and any slurry, however purely blended, is only over-rich and aborts. Tightened: the "low-grade recirculating drives are immune" extension holds only for **true** purity ≤ 0.42, so only for assayed feed (see the arithmetic fix below).
- **2, industrial AM:** present and faithful: station-only factories, weapons, conventional AM thermal thrusters, shielding mass, crude use irradiating crew and surroundings through the flares' `ShieldTable` plus a baked internal path.
- **3, drive classes:** present and faithful. Recirculating is **cheaper to run** (about 35 % per jump, recomputed: 6.1 against 9.6, and 101 against 160) and **costlier to set up** (3× capex), slurry burns its agent and has no dilution stage and no runaway, higher grade is slightly more available (`SlurryMarketWeight`), and kind is a faction choice.
- **4, pirates:** present: crude refineries raising runaway risk, mixed fleets, dual-drive selector with damage rules and engagement rules.
- **5, station anchor:** present and faithful. The grid stays in `BodySurface` at any altitude, the bubble is static, the rate grows with mass × hold acceleration and is zero at the equatorial synchronous radius, failure drops it with its inertial velocity, warnings and a reserve exist, and approaching ships join its frame.

**Physics and arithmetic errors fixed**
- **Off-spec formula was inverted.** With `nominal = true · (1 + e)`, the diluter dosing by nominal content gives `q_line = q_mix / (1 + e)`, not `q_mix · (1 + e)`. Stock that **understates** its purity is the dangerous one. Consequences: at the default `q_mix` 0.35, T2c feed (20 % spread) can run away (worst case 0.4375 > 0.42), not "sit on the edge"; the UI rule `q_mix ≤ MixHi / (1 + Spread)` was wrong. Fixed with a default `q_mix = min(MixTarget, MixHi · (1 − Spread))` that can't even run hot; raising it (to fit a big charge in the loop) is now the risk the decision asks for.
- **Off-cone dose was 10× too high.** `κ · P_γ / 4πd²` for a light plasma drive at full is 0.27 Gy/s at 100 m, not 2.7 (and 2.7e-3 at 1 km, 2.7e-5 at 10 km). "5 Gy in a few seconds at 100 m" became about 20 s.
- **Capital invariant row used the first draft's price.** At fuel grade (1.2) a 78,000 t A-class ship costs 14.1 per ℓ, not 17.6 (19.9 with AM); ratios are 2.1× / 1.8×. The AM price that breaks the invariant is set by the **heavy** class at about 0.7 per gram (capital about 1.85), not 1.4 by the capital class. Fleet break-evens with AM added (about 9 and 19 ships).
- **Anchor worked numbers all check** (rates, fall times, impact speeds, escape at 10,000 km, r_s, 0.27 g case, latitude). Small fixes: the vertical climb to 100 km is about 1.35 km/s; "over the pole of the synchronous radius" reworded (it is the 45° point at that radius); one M factory needs "just over four", not four, to feed a 35 g/day station; the hovering comparison now includes its 170,000 t a day of propellant. Added the release regimes (impact below about 2,670 km altitude, orbit to r_s and beyond, escape above about 5,140 km) and the impact-point drift.
- **`a_hold` wording.** "Relative to a natural co-rotating orbit" was ambiguous; it is the non-gravitational acceleration needed to co-rotate (centripetal minus gravity), evaluated at the centre of mass.

**Contradictions fixed**
- **Deferral used T1.** warp.md allows abort and interdiction exits mid-warp, so a deferred burst is now at `T_exit + 181`. Chamber ruptures in the lock-in window ("next tick") would have destroyed a locked-in drive; they now defer the same way. Throat placement defers too (immune like warp). Purge and eject are refused while intangible and open at `T_exit + 1`.
- **Dual drives bypassed cooldown.** warp.md checks "out of cooldown" per drive, so a 10 s switch beat a 30 s cooldown. Cooldown is now per ship.
- **Rails in a rotating frame.** frames-and-zones.md has rails only as Kepler elements in an inertial frame, and freezes parked things in `BodySurface`. A ship hovering in the anchored zone with nobody near would have frozen there for free. The zone now sets the frame only for bubble members; unsimulated entities go to `Rails` in `BodyInertial` and fall.
- **Zone vs bubble limits.** `r_station + 2.6 km` can exceed the 4 km hard radius, and clusters splitting off inside the zone were sent to `BodyInertial` while the zone said `BodySurface`. The zone is capped (stations up to about 1.4 km radius; larger is README Q9), and splits inside the zone stay in `BodySurface`. Two station bubbles merging, and zone entry at km/s (R7 prediction), are specified.
- **Release budget.** "Arrival-style ≤ 128" mixed warp.md's arrival (64) and merge (128) budgets; it uses the merge budget.
- **"Costs nothing per tick" vs a 20 ns ledger row.** Anchors are now lazily settled with scheduled warning, empty and failure events.

**Exploits closed**
- **Runaway as a suicide bomb.** "Adds no reach beyond a 100 kg charge" was false: charges have no near-field brush, a runaway does (up to 77 m). The bound is restated honestly, and docked drives can't prime or override (the warp.md collar lock), so the docked-bomb case is gone; what is left is a visible 10 s kamikaze (open question 8). The override also requires feed above `q_lim`.
- **Anchor drop onto a base.** A 10,000 t station released at 100 km is about 2 kt, landing within 3 km of the point below. Added: a public drop footprint checked at engage against other parties' zones (using the maximum rated mass, re-checked on core changes), a 60 s public release countdown broadcast to the footprint, and refusal of owner actions that would starve the anchor (draining the reserve, cutting control power), which were 10 s or instant back doors.
- **Anchored station immunity.** Rams and tractors against the static body moved nothing and cost nothing; they are now debited and count toward overload. Pulse shear still damages anchored hulls.
- **AM radiation griefing.** A heavy AM thermal hovering at a 5 km exclusion edge gives unshielded EVA crew 5 Gy in about 19 min. The station AM-thrust limit now has its own radius (default 20 km), and dose from another party's source is attributed to it.
- **Dual-drive abuse.** Cooldown chaining closed (above); a Hot unselected drive still shows its signature.

**Determinism**
- The kick budget was a float that clients "predict from the same rule"; it is now integer mm/s.
- The anchor rate is computed once in double on the server and persisted as integer ng/s, so saves and replays never recompute it; settlement and event ticks are integer.
- Merged `Spread` is defined as the true-content-weighted mean, rounded up by the server, which is the weighting that actually bounds the merged error.

**Perf**
- Anchors: 0 per tick (lazy), about 1 µs per footprint solve on engage or core change.
- External dose needs a per-source cull radius: a heavy AM thermal at full stays above the 1e-6 Gy/s floor out to about 330 km, so receivers come from the spatial hash and the cost must be measured on a busy server.

All costs are estimates. Nothing was built or run, and no repo files were changed.
## Revision 3 notes

What changed against revision 2, decision by decision (wormholes.md revision 3 carries decisions A–H on its side). The Review notes, Revision 2 notes and Revision 2 review notes above are kept as written; the stall they describe is superseded. Open questions are no longer renumbered: answered ones are struck through in place, so every earlier in-text reference still points at the right question.

**H, crystals and additives**
- **Decay floor:** `DecayFloor = 0.96` (data) bounds both decay and shock. It equals T3's cap, so decayed crystal is throat-grade stock and one T4 pass regrows it. A stack keeps decaying after it leaves the Crystal band until it reaches the floor, then is stable. Lost stasis costs about half the crystal's value, never all of it.
- **Purity cap:** `QMax = 0.995`, global. The loader refuses recipes and sources above it. The Crystal band is 0.97–0.995, and inside it purity buys margin (time and shocks before leaving the band) and price, not device stats (open question 15).
- **Additives:** a new "Wormhole additives" section. Seven families (binder, dilatant, chiral dopant, inverted dopant, damping flux, catalyst, seed lattice) matching wormholes.md's effects, each with its wormhole trade-off and its own recipe at a T3 additive lab, plus a crude ship-mountable lab for three families at lower potency. Additive stacks reuse `ItemStack` with `ContentMg` as active mass (potency, capped below 1). Mixed at the generator at charge start (no pre-doped EM item), at most 2 kinds and 10 % of X by mass, and the generator's 0.85 gate applies on true content with additives as impurity: a full dose needs EM at 0.929 or better. EM consumed by a recipe never reappears as content. Seed lattice is metastable like crystal.

**A–C, the stability fight from the exotic-matter side**
- **Pulses drain stability; the stall is removed.** "Wormhole disruption: the invariant is kept" became "Wormhole disruption: stability drain": `BombK · Y · (1 − d/R_s)²` per mouth, `2 · BombK · Y` inside a tunnel (the review made it 1× and rung down over 5 s; see Revision 3 review notes), dampers block it at covered mouths, a zero collapses early. The stall row, stall limits, immunity, "why not early collapse", the stall data keys and `AgPulse.StallRadius/StallEnd` are gone; `DrainRadius`, `DrainKJ` and the tunnel position replace them. Runaway far fields drain the same way. Worked examples, a cost ratio (about 1 : 24 against an unfed 1 AU tunnel, 1 : 6 against its re-establish) and the griefing bound are restated.
- **Stabilisers:** power only (Feed at class 2, Drain at class 3), outside the reaction like the interdiction emitter; AM only through cores (5 mg/s for a 2 GW kit). Crafted with sealed throat-grade lenses; the support projector also needs a crystal lens, making it T4. They hold no EM or AM.
- **Re-establish:** `0.25 · X`, its AM and 0.25 of the additives, drawn from the generator's own tanks, plus `E_re`. Fleets that expect a bombing carry a quarter-charge spare.
- **Throughput drain:** paid back in power, so about 2.5 mg of AM per GJ through cores (6.5 mg for a 78,000 t crossing). It shapes the band, not the economy.
- **Quote:** `E` now includes `EsMax`, X carries the class factor; examples updated for tunnel and instant. The per-class "cheaper" table prices tunnel class and is unchanged.

**F, graviton projection in tunnels**
- New section with a `gravitonProjection` field (None, Internal, External, per mode) on every function core, and the classification of every device here: tractors, dampers, armed charges, heavy scoops, the harvester, Drain stabilisers, catchers and emitters are struck; plating, dampening, compensators, stasis cases, Feed stabilisers and generators feeding are not; drives, traps, AM thrusters and AM weapons are not gravitational.
- Deploying mines in a tunnel is refused; an armed charge there is struck and detonates on the next tick.
- New "Tunnel hazards for EM and AM hardware": what an offline outage does to traps, stasis cases, drive cores and plating; Cold running advised through unstable bores.

**Other edits**
- Recommendation, decisions, purity table, power classes, device table, runaway effects, bomb effects table, resolution order, networking, data (`qMax`, `additives`, drain keys, additive and stabiliser files), C# (`CrystalRules`, `AdditiveRules`, `AdditiveMix`, `GravitonProjection`, `EmMath.Shock` and `AdditiveBlend`), per-tick costs, progression, rarity knobs, changes to other docs, milestones (W3, X1), files and tests updated.
- **Open questions:** 2 (bombs against wormholes) and 3 (perishable crystal) answered and struck. New: 14 floor and cap values, 15 what purity buys in the Crystal band, 16 additive scope, 17 stabiliser cost, 18 bombs in tunnels.

**Problems found while integrating, and fixes**
- **Additives could smuggle EM past the gate** if a recipe's EM input stayed counted as content in the product. Recipe EM is consumed; additive `ContentMg` is active mass and never counts as EM.
- **Unassayed throat stock** could pass the additive gate on nominal content and fail it on true. The server re-checks at charge start and refuses with nothing spent and no cooldown, the same "plan on nominal, act on true" rule as every other gate.
- **The decay floor sits below the Crystal band,** so "crystal" stacks could sit at 0.965 forever. They are shown as Throat, can't seed or make lenses, and decay continues to the floor, where the stack becomes stable.
- **A damper in a tunnel** was both a defence (it blocks drains at mouths) and a projector. It blocks only at the mouths it covers in normal space; inside a tunnel it is struck and blocks nothing.
- **Mines in tunnels** would have become one-tick proximity fuses after the first strike; deploying them there is refused.
- **Hot recirculating lines in tunnels:** wall discharges are damage brushes, and a burst inside drains up to 200 GJ, more than any player wormhole holds (100 GJ after the review). Documented, with a Cold-running prompt before unstable bores.
- **Re-establish with empty tanks:** the re-establish is a charge start and draws only from the generator's own sockets; stated, and tested.
- **Rails mouths at a black-hole collapse** kept their elements "until `TClose`", but a tunnel's mouths now live to `TEnd`. They keep them until they despawn.

**Unchanged on purpose:** the anti-graviton stoichiometry, warp fuel and both drive kinds, runaway triggers and the lock-in, industrial AM and radiation, station anchors, pirates' kit, black holes and remnants, and every earlier user decision not superseded above.

All costs are estimates. Nothing was built or run, and no repo files were changed.

## Revision 3 review notes

A skeptical pass over decisions A–C, F and H, checked against wormholes-r3 (the revision 3 wormhole doc). Fixes are made in place above; the wormhole doc was edited only where both docs state the same rule (listed under "Changes to other docs", wormholes.md).

**Decisions, checked:**
- **A, energy fight:** present (pulses drain, zero collapses early, paid maximum, re-establish at 0.25 X with its AM and additives). "Power through" was not real against a single pulse; fixed below.
- **B, holders:** present (battleship kit 2 GW / 5 km / η 1, support projector 1.5 GW / 50 km / η 0.7, power only, lenses as capital). Same numbers as wormholes.md. Destabilisers could beat any number of feeders on small wormholes; fixed below.
- **C, natural level and throughput:** present and consistent (`e_m` 33 J/kg, 2.6 GJ for 78,000 t, refilled at 2.5 mg of AM per GJ through cores). Additive trade-offs move the mass-drain bounds; now stated.
- **F, graviton projection:** present and consistent with wormholes.md's trigger and exempt lists (plating, dampening, compensators internal; tractors, dampers, armed charges, Drain stabilisers, catchers, emitters external; Feed stabilisers and feeding generators exempt as proposed).
- **H, crystals and additives:** present. Floor 0.96 (= T3 cap, so decayed crystal is throat grade), `QMax` 0.995, seven additive families matching wormholes.md's seven effects one to one, with identical trade-off factors (`m_X`, `m_E`, `m_S`, `m_C`, `m_L`, `e_m`, `VCrossMax`, `Ψ_s`). Gate arithmetic checked: 0.929 for a full 10 % dose, 0.888 for 5 %, 0.865 for 2 %.

**Units and rates, checked:** the reserve is integer kJ in both docs; `EsMax = 10 GJ · r̂² · m_S`; feed cap `EsMax / 5 s` (2 GW at r = 100 m); kit and projector `FeedW` match; AM per GJ (2.5 mg at η 0.4, 1 TJ/g) gives 5 mg/s per 2 GW kit; quote and re-establish AM (152 g, 380 g, 0.6 kg; 149 g and 24.9 GJ) recomputed and correct.

**Contradictions and exploits fixed:**
- **Power through was impossible against one pulse.** A pulse drained its whole energy in one tick, so feeders could never answer it: the only defence was the 25 % overcharge, and a 100 kg casing (about 150 units) collapsed even a prepared r = 100 m wormhole from up to 15 km away. Pulse drains now **ring down** over 300 ticks (a timed slope, exact in integer kJ, no new traffic: clients add it from `AgDetonation`). Feeds count during it. A wormhole survives a lone pulse iff `D < Ψ₀ · EsMax + P_net · 5 s`: an unsupported one (full, unfed) falls to `EsMax` (10 kg at point blank for r = 100 m, unchanged), a prepared one (1.25 and fed at the cap) holds to `2.25 · EsMax` (22.5 kg). A big enough bomb still collapses an unsupported wormhole up to r ≈ 316 m with one casing, and larger ones with several together.
- **In-tunnel drain was doubled.** A charge 1 m inside the disc drained twice what one 1 m outside drained, the tunnel already removes the damper defence, and owners cannot refuse entry, so the bore was strictly the best place to bomb (10 kg collapsed even a prepared r = 100 m tunnel). It is now the point-blank value, `BombK · Y` (`tunnelDrainMul` 1), and the 100 kg cap is 100 GJ everywhere. Open question 18 reworded; admission filtering offered as an option.
- **Destabilisers outran any feed on small wormholes.** The feed cap scales with `r²` but a kit's drain did not, so one Drain kit (1 GW) beat unlimited feeders below r ≈ 71 m, and one Drain projector below r ≈ 51 m: the smaller ships' wormholes that decision B's support ships exist to hold. Destabiliser drain now shares `FeedCapW`: at the cap it **pins** a wormhole (net zero) and a bomb finishes it, so breaking a held wormhole takes combined arms. At r = 100 m the old "two Drain kits cancel one Feed kit" still holds; a third no longer wins.
- **Fleets cannot carry dampers.** The gravity damper is a station core, yet both docs listed dampers in a fleet's power-through kit. Reworded to "where a station carries them"; whether a ship damper should exist is new open question 19.
- **Re-establish chains got cheaper each time** if "0.25 X" was read against the previous re-establish. `X` is now always the original plan's; the re-establish blend re-checks the purity gate on true content, and a Dilatant mix keeps its boosted `MaxRadius` for the eligibility check.
- **Additives silently moved the mass-drain bounds** ("one reserve per 300 s of flux", "40 % per entry"): binder plus damping flux gives about 160 s and 74 %. Stated as the intended price; entry refusal and the feed cap (about 30× the worst mass rate) still bound it. `m_S` is the only additive path into the energy fight, and it scales the feed cap, bomb thresholds and `E_re` together.

**Left for wormholes.md (not edited, its own structures):** the `Stability` struct needs a per-wormhole ring-down pool (end tick, slope, source `s`), and `CollapseCause.Pulse` should be chosen by the largest active ring-down at `Tz` (the collapse-point rule was edited; the enum is unchanged).

**Not changed:** `BombK`, `EsK`, the feed cap, overcharge, the strike cap (1 %/s), the decay floor and cap, additive recipes and potencies, and every number not named above. All costs are estimates; nothing was built or run, and no repo files were changed.

## Revision (round 5) notes

Applies the user's round 5 answers D9, D12, D13, D14, D20, D21, D22, D23, D24, D25 and D28 (decisions.md). They win over any older text in this doc, including the historical notes above.

- **D9, inertial dampener:** plating and dampening are split. Plating now bills only `|g_plate|` and gives floor gravity. The new `InertialDampener` S/M/L core keeps everything inside its bubble (crew, loose bodies, cargo, covered docked craft) moving with the ship, on graviton emission: `EmPerNsDamp` 1e-11 kg/(N·s) of field-grade content plus 1 W per N of bus power, billed on `m_in · a_d` only while accelerating, rated by `a_max` (3/6/10 g) and `MassMax` (50/500/5,000 t), with leak-through above either and a 0.5 s ramp-down when starved. Interior gravity (README Q3, frames Q1) is now decided. Updated: recommendation, bands, devices table, tunnel table, data, structs, ship-hulls and frames changes, tests.
- **D12, override:** the "pure dump" override is kept as a scuttle or kamikaze, with every safeguard (owner only, feed above `q_lim`, never docked, locked or in `Warp`/`Throat`, 10 s public arm, casing cap, line cap).
- **D13, runaway severity:** the shared damage rule gains a per-housing `Rating`. A recirculating drive has a core housing (rating 1: diluter breach 0.5, valve 0.25), a reaction chamber rated ×8 (rupture at 0) and an eject mount rated ×4. **Core eject is standard** on every recirculating drive (no longer a +240 option; capex unchanged at 2,400), ejects the entire core, works at any core Integrity and off-key, at 20 m/s (D, C) or 40 m/s (B, A) so the near-field brush misses the ship for every line; an auto-eject owner flag is added. `DriveOptions.EjectMount` became `AutoEject`.
- **D14, multiple drives** (superseded by Round 5 review notes; r6: `DriveGroup` folded into warp.md's `WarpDriveSet`): the revision 2 selector ("only one spools") is replaced by `DriveGroup`: participating drives spool together into one bubble, quality averaged by `BubbleW · Integrity`, capacity `Σ MassCap` (the round 5 review aligned these with warp.md: delivered power, `Σ f_i · MassCap_i`), each drive paying its own share by its own kind, any participant's runaway or bad mix aborting the shared spool before `T0 − 30`.
- **D20:** AM energy and production scale unchanged; old open questions 4 and 13 removed.
- **D21, black-hole lifetime:** natural life 5–20 years (was 60–180 days). New **Deliberate collapse** section: the T4 `CollapseInducer` (crystal lens, works from the harvesting band) pours throat-grade EM and AM into a `CollapseWork` budget (2,000 kg content, 10 kg AM, 20 kg/h per inducer, relaxing 2 %/h when unfed); a full budget schedules the collapse 24 h later with public progress events from 25 %; contributors are attributed for the damage. Data, structs, networking, milestone B2 and tests added.
- **D22, EM supply:** the remnant-flood framing is removed. All existing EM is recycled ancient EM in planets and asteroids, which are the main supply for the whole game; remnants are rare, late, local rushes. Recommendation, planet deposits, asteroids, remnant economy, progression row T4 and tuning target rewritten; old open question 7 removed.
- **D23, travel economics:** "warp cheaper, wormholes costlier" is replaced everywhere in this doc by natural cheapest (no EM or AM per crossing), warp the cheap way somewhere specific and close, manufactured fastest and dearest. The per-class invariant is kept as the manufactured-versus-warp half; the fleet break-even is reframed; the warp.md, wormholes.md and README change lists carry the restatement; old open question 6 removed.
- **D24, radiation:** transient plus lingering. Characters gain a lifetime dose with three chronic bands cured only by an expensive `MedBay` treatment; `med.antirad` treats acute exposure (suppresses sickness, faster recovery) without touching lifetime dose. Contamination records (hull activation 3-day half-life, breach fallout 20-day) per room and per terrain chunk, lazily decaying, with decon and contaminated rock stacks. Old open question 10 removed.
- **D25, anchors:** any grid can mount an anchor (`stationOnly` false); activation needs the set position (5 m) and mostly the velocity (≤ 5 m/s residual, paid as an impulse); anchored grids crawl over days (0.05 m/s along, 0.02 m/s vertical, 1 h segments, footprint re-checked, kinematic while crawling); no anchoring over asteroids (tractors hold those); a dropped station is a rare disaster with thrusters unlocking at `AnchorFailing`, an escape-pod rule for impact footprints over atmospheres, re-entry burn-up and fragmentation (proposed Sutton-Graves heating, `BurnUpMassKg` 5 t) and hard ground impact; footprint stays conservative. Old open question 11 removed.
- **D28, tractors:** without AM a tractor emits gravitons only and can **only pull**; push and hold need the AM reaction. A new "Graviton emission" mode (EM and power, `AmPerEm` 0) is defined under The anti-graviton reaction.
- **Open questions:** 2–13 removed as answered (numbers kept so references stay valid); question 1 trimmed to placement only; new questions 20 (plating on gravitons) and 21 (deliberate collapse cost).
- **Not changed here (owned elsewhere):** D17 wiring, D18 warp beacon and real-space tail, D15/D16 warp path and exit rules (warp.md); D26/D27 tractor consent and sieges; D10/D11 flight and hull editing (ship-hulls.md, ship-design-and-construction.md).

## Round 5 review notes

A skeptical pass over the round 5 edits (D9, D12, D13, D14, D20–D25, D28), checked against decisions.md and the round 5 copies of warp.md, frames-and-zones.md, ship-design-and-construction.md and ship-hulls.md, plus the repo's tractor-beams.md. Fixes are made in place above; where the text above and the "Revision (round 5) notes" differ, these notes and the text win.

**Decisions, checked:**
- **D9:** present (plating for gravity, a separate rated dampener on EM and energy, leak-through above the rating). Aligned with frames-and-zones: the dampener cancels the manoeuvre term `a_man` and never bills rest acceleration, so the old "a dampener cancels `a_hold` all the time" is gone; plating bills only the top-up it supplies. Added full-rating bills (an L core at 10 g on 5,000 t is 490 MW, cores only) and how it behaves on a bus shortfall.
- **D12, D13:** present. Chamber ×8, eject mount ×4, eject standard and wired (plus ship-design's mechanical pull handle, so cut wires can't stop an engineer). Eject distances re-checked: 20 m/s × 2.5 s = 50 m against `r_n` ≤ 43 m for a 75 kg C line; 40 m/s gives 100 m against 77 m for 450 kg.
- **D14:** present but it disagreed with warp.md's "Multiple drives" (owner of the bubble): weight by `BubbleW · Integrity` instead of delivered power, `Σ MassCap` instead of `Σ f_i · MassCap_i`, "after the lock-in the bubble holds" instead of the support row, Off/On toggles instead of wiring. Now follows warp.md; `P_i` is capped at its rating.
- **D20–D24:** present. D21's lifetime is 5–20 years, the inducer's AM figure re-checked (10 kg ≈ 116 days of an L factory, 11.6 of the foundry); a leftover "once a quarter" collapse cost line fixed. D22 and D23 leave no blanket "warp cheaper" text. D24's model is sound; contaminated stacks now feed one aggregate record per room.
- **D25:** present. Added the Lagrange target (frames-and-zones' extension) and the asteroid-contact engage refusal; unattended crawls are now `Frozen` with a pose linear in the tick, and crawl segments must clear terrain at plan time.
- **D28:** present. Anchors stay on the full reaction at every pose (a pull can't supply the poleward part); "holds an asteroid" now says tow, with a rigid hold needing AM.

**Contradictions and exploits fixed:**
- **D16 leftovers:** "Exits must still clear `WarpFloor`" for anchored stations and "`WarpFloor` still applies" in the warp.md change list contradicted "no forced floor". Removed; low exits are legal with warp.md's pressure wave and fines. The black hole keeps an exit exclusion (renamed `RouteShell(BH)`), stated as a hazard rule, not an atmosphere floor.
- **D17/D18 leftovers:** "the crew couldn't act during warp", "purge and eject are refused in warp", "damage cannot happen during Cruise" and `T_exit` = T1 predate the real-space segment and free crew. Now: purge and eject can be commanded in cruise and execute on the first real-space tick; interior damage can happen in cruise; `T_exit + 1` is `TDrop` (or `TDrop'`).
- **Mid-warp eject never worked under warp.md's timing.** A breach in cruise hard-cancels, so `TDrop'` is 180 ticks after onset, the same tick as the burst; warp.md held the burst to that tick, and the core separated on it, 0 m from the ship. Bursts held through cruise now resolve at `T_exit + 181`, giving a commanded eject 60–120 m.
- **Graviton power figure:** "the energy annihilation would otherwise supply" was off by about 500× against this doc's own 1 TJ per gram. `GravitonWPerN` is now a stated knob, with tractor-beams.md's old pumped figure as the fallback if pull proves too cheap.
- **Dampener stacking:** four S cores over one room no longer make a 12 g dampener; a docked ship's own dampener idles under its host's.

**Left for other docs (not edited here):**
- **warp.md** *(all applied in warp.md, r6)*: adopt a ship cooldown equal to the longest cooldown among the last warp's participants (per-drive cooldowns reopen revision 2's cooldown chaining); change "only an override or sabotage can start one mid-warp" to "only sabotage or interior damage" (the override stays refused in `Warp` placement, so a full line can't arrive pre-armed); replace "held to `max(burst tick, TDrop')`" with `T_exit + 181`; add the black-hole exit exclusion; set `PRated` watts per class.
- **tractor-beams.md:** drop Pumped mode for D28 pull on graviton emission (new entry under Changes to other docs). *(Applied, r6.)*
- **frames-and-zones.md:** its "(2–8 g, `F_cap`)" dampener mention should read 3/6/10 g S/M/L. *(Applied, r6: frames now cites `a_d`, `a_max`, `MassMax`.)*

**Noted, not changed:** an ejected Hot core shot to rupture is an improvised mine. It is bounded by the prime (1–20 kg) and the casing cap, so it is no worse than a legal charge. With a chamber rating of 8, a no-warning rupture needs sustained, focused fire, which is the intent of D13.

All costs are estimates. Nothing was built or run, and no repo files were changed.

## Consistency pass (r6) notes

- **Linked drives (D14):** weights are `w_i = min(P_i, PRated_i)`, averages `Σw·X/Σw`, `VCap` divided by `Σw`, fuel split by `w_i/Σw`, at most 8 drives join. The ship cooldown is warp.md's per-root-grid `ShipWarpCooldownEnd`. `DriveGroup` is folded into warp.md's `WarpDriveSet` (struct, `src/` list). Warp.md cooldown and `PRated` asks are marked applied, and the Round 5 D14 note is marked superseded.
- **Runaway in cruise:** a deferred burst is at `T_exit + 181`. The rebuttal of warp's old `max(burst, TDrop')` is deleted. The override is refused in `Warp` and `Throat`. Purge is queued to `T_exit + 1`. Eject runs its 30 ticks in cruise and separates at `max(T_exit+1, command+30)`, giving 60 m (D, C) or 120 m (B, A). Warp.md asks are marked applied.
- **Floors:** `WarpFloor` becomes wormholes' `MouthFloor`. A black hole's body core is `InfallRadius + r`. The "warp.md doesn't cover black holes" note now cites warp.md 149/307.
- **Dampener:** `a_d = min(|a_man|, a_max) · min(1, MassMax/m_in)`, also limited by delivered power, EM and `Limit`. `F_cap`, `a_eff` and "2–8 g" are retired. The tidal compensator is this doc's device. Frames and ship-design asks are marked applied.
- **Enums:** `CelestialKind` adds `GasGiant`. Exotic's `TractorMode` is replaced by tractor-beams' `TractorPolarity`/`TractorDrive`.
- **Tractors (D28):** pull is billed on emitted force, with bus power `BaseMW + 1 MW/km·R + GravitonWPerN·F_emit` and heat `0.2·GravitonWPerN·F_emit`. The AM row is renamed "push, or fixed-offset hold (Hold assist)". Without AM, Lock-Hold is a pull-only tether that still blocks warp spool. D26 (tractor consent) and D33 (miner liability) need no edits here: tractor-beams.md and rings-and-asteroids.md own them.
- **Station anchor (D27):** force overload from tractors and rams drains the reserve first. It then raises a public `AnchorOverload` alarm with a grace window (`ForceOverloadGraceTicks`, proposed 60 s; `AnchorState.ForceOverload`). Failure needs both control feeds cut. Capture at the `CommandCore` transfers anchor authority, and cutting bus power never cuts the anchor's containment feeds.
- **Systems:** "single-system v1" is replaced by per-system black-hole slots. There is no cross-system warp, and the Dark Gate's far end may be in this or another system. Open Q1 is reworded.
- **Names:** `decisions-r5.md` becomes `decisions.md`.
