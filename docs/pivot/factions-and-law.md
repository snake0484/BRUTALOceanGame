# Factions and law

Status: design only. Nothing has been built or run, and no repo files were changed. Written 2026-11-26 against decisions.md (D9-D74), README.md, review.md, frames-and-zones.md, warp.md, wormholes.md, exotic-matter.md, planets.md, ship-hulls.md, ship-design-and-construction.md, tractor-beams.md, rings-and-asteroids.md, ship-atmosphere.md and ai-ships-and-crew.md. Every number is a knob in `data/` and every cost is an estimate to measure (`--law-bench`, profiler rows below). decisions.md wins over everything here, then review.md's rulings. Where this doc changes another doc, the change is listed under "Changes to other docs". Ticks are 60 Hz; "played" time is `TickIndex` time, which stops while the server is empty.

## Recommendation in one paragraph

Law is a **consumer of events, never a per-tick system and never a physics rule**. Every system that attributes harm already writes one shared, server-only `IncidentRecord`; this doc takes ownership of that record and turns it into a **case** in the **jurisdiction** where it happened: a fine, restitution to the victim, a standing change with the factions that learn of it, and **heat** (crime status) that decays with the offender's own played time. Jurisdiction is a lookup (station zone, then surface region, then body, then system claim, else lawless), resolved only when an event needs it. A crime needs **evidence** to become a case: a logged input, a transponder, a sensor track or a surviving victim. Enforcement is real hardware: patrol and garrison hulls from the AI traffic tables, docking refusal through one permission hook, impound at the dock. Factions are data: holdings, a relations matrix, a law code, a ship catalogue their fleets fly (D68), gated tech and military rights. Land is owned or licensed (D74): digging, tunnelling and building check a claim table once per stroke. Cities and capital worlds carry **region shields** that absorb warp-exit blasts and bombardment and push warp exits out of their shell (D58). Wars between factions run as a **strategic step once per played hour** over the real hull and station rows, mean-reverting so the map moves a station at a time over tens of played hours (D54). Stations change hands by one **siege and capture rule** (D27) that replaces the interim knobs, and stolen hulls follow **title**, not possession.

## User decisions this doc implements

- **D57:** a factions and law design (fines, crime status, stolen hulls, sieges, land). This doc.
- **D54:** factions fight wars outside the players' view, but things are mostly stable. → "Wars off-screen".
- **D58:** warp exits used as a weapon are definitely illegal; planetary or city shielding may prevent the damage. → "Planetary and city shields", offence `WarpAttack`.
- **D68:** no fleet discount; fleets generally use their faction's ships. → "Ships and catalogue".
- **D72:** other parties may heat, wedge or shoot a rock with people in its caves; injuring or killing players or NPCs that way is illegal. → "Offences", `PersonHarm`.
- **D74:** tunnelling vehicles and cave drills are allowed but regulated; factions control most planets, so you need to own the land or otherwise have approval. → "Land and mining rights".
- Consumed, not changed: D16 (no exit floor; blast, fines, blame), D18 (beacon, military switch, cut wire fined), D26 (tractor without consent is a weapon), D27 (sieges; capture preferred), D33 (legal-mining debris is the miner's liability), D34-D36 (station shields, real debris, deliberate shatter damage is hostile), D25 (dropped station is a rare disaster), D30/D32 (shores unminable, edits permanent), D43-D51 (AI ships and crew, surrender, scannable crew), D55 (three shield kinds), D63 (emergency shields do not stop bodies), and "game time does not advance while the server is empty".

## Ground rules

1. **Physics never depends on law.** Law reads what happened. It never makes a shot miss, a rock vanish or damage go away. The only game rules that gate actions are the ones other docs already own (protected-zone tractor zeroing, the rock-working ban inside `ProtectRadius`, exclusion pushes, the shore clamp) plus two added here, both physical in the fiction and both server-validated like the shore clamp: the settlement dig clamp and the region shield.
2. **Nothing per tick.** Every rule below runs on an event (an incident flush, a dock request, an edit stroke, a scheduled heap event) or is settled lazily from `(value, tick)`.
3. **Attribution is the other docs' job; consequence is this doc's.** Who the actor is, what class the act was (Hostile, Reckless, Accidental) and who is liable come from the writer (rings' tracing, warp's corridor rules, tractor consent, the `Logged` inputs). This doc never re-derives intent.
4. **No mint.** Every payout (bounty, restitution, salvage award, ransom, prize) is paid from money that was collected or escrowed. Faction treasuries are finite numbers.
5. **Same rules for players and AI.** An AI ship's actor is its owner (ai-ships-and-crew.md "Liability"). NPC owners hold standing, heat and fines exactly as players do.

## 1. Factions

### Identity

One id type names every party that can own, act or be liable. It replaces `PlayerId` in `IncidentRecord` and every tracing field (ai-ships-and-crew.md already requires this).

```
OwnerId (uint): 4-bit kind | 28-bit index
  kind = None, Player, Organisation, Faction, Company, Authority
```

- **Player:** a person. **Organisation:** a player group (wormholes.md's "owner group" and "group"; tractor-beams.md's `EmitterOwnerGroup`). **Faction:** an NPC power or a chartered player faction. **Company:** an NPC owner under a faction (a hauler line, a mining firm, a pirate clan). **Authority:** a planetary or station authority, which is a law-giving arm of a faction with its own overrides.
- `FactionOf(owner)` is one array read: a player's enlistment, an organisation's charter, a company's or authority's parent, or 0 (none).
- **Party** stays the session-level grouping other docs use ("own and party bodies"). A party is not an owner and holds no standing.

### Faction table

At most **32 factions** per world (`FactionId` 0 = none, 1-31), so every per-faction table is a fixed array. Proposed content for a start system and its neighbours: 4 major factions, 2-3 minor ones, 2 outlaw clans. All of it is `data/factions/*.json` (the directory exotic-matter.md already uses for drive kinds, options and additive families).

| Field | Meaning |
| --- | --- |
| `kind` | `Major` (holds territory, gives law, fields a navy), `Minor` (holds a few stations, borrows a major's law code or its own), `Outlaw` (no law code; its ports are lawless; never signs anything) |
| `lawCode` | which `data/law/codes/*.json` its jurisdictions use |
| `baseline` | its row of the baseline relations matrix |
| `catalogue` | ship classes its yards build and its fleets fly (D68), plus `foreignShare` |
| `access` | part templates, drive kinds, options and additive families it builds and sells, each with the standing band that unlocks it |
| `military` | which templates are `MilitaryOnly` (the beacon switch, warp.md) and what a commission costs |
| `capital` | its capital station or world: never siegeable, never flips |
| `treasury` | starting credits; grows by taxes, fees and fines, shrinks by bounties, awards, mission pay and ransom |
| `key` | wormholes.md's signature key and epoch; rotated by the faction's key holder |

### Territory: holdings

Territory is a list of **holdings** (cap 1,024 a world, 24 B each), generated from the seed at world generation and persisted. A holding binds a place to a faction, an authority and a **security tier**.

| Holding kind | Covers | Typical tier |
| --- | --- | --- |
| `System` | everything inside a star's SOI that no closer holding covers | Frontier or Lawless |
| `Body` | a planet's or moon's SOI, its rings and its surface by default | Patrolled |
| `Region` | cells of a body's surface region grid (below), up to `RegionCeilingM` = 100 km | Secure near cities, Patrolled or Frontier elsewhere |
| `Band` | a ring band or a belt sector (rings-and-asteroids.md lane or field ids) | Patrolled near stations, Frontier otherwise |
| `Zone` | a station's `ProtectRadius` (5 km), a settlement footprint, a declared approach lane or gate queue | Secure |

- **Security tiers:** `Secure` (3), `Patrolled` (2), `Frontier` (1), `Lawless` (0). The tier sets what counts as evidence, how fast a response comes and whether surveys run. It is data on the holding and changes only by strategic events.
- **Factions control most planets (D74).** Generation holds at least 75 % of planets and major moons in settled systems. Belts and outer bodies are mostly Frontier or unheld. Unheld space is Lawless.
- **Surface region grid:** each held body carries a cube-sphere grid of 6 × 16 × 16 cells (1,536 bytes; ~90 km cells on a 1,000 km planet). Each byte indexes the body's region table (≤ 255 rows: authority, tier, land policy). Lookup is a face pick and two multiplies, ~20 ns.
- **Station zones** take their owner's faction and law code. A station owned by an independent organisation in Lawless space has house rules only (its `StationPolicy`), no law code.

### Jurisdiction lookup

`Jurisdictions.Resolve(frame, position, zoneHint)` returns `(holding, authority, faction, tier)`: zone (callers that know the zone pass it; `IncidentRecord.ZoneId` already carries it) → surface region if under `RegionCeilingM` → band → body → system → Lawless. ~50-100 ns, called only on events. The holdings table and region grids are world data sent to clients at join (≤ 24 KB plus 1.5 KB a body), so the HUD shows "Secure: Meridian Authority" with no server traffic.

### Relations

A 32 × 32 `sbyte` matrix (1 KB), symmetric, −100 to +100, with a derived state per pair.

| Value | State | Effect |
| --- | --- | --- |
| ≤ −60 and declared | `War` | members and assets are lawful targets for each other (acts of war, below); stations embargo each other; sieges may be declared |
| −59 to −30 | `Hostile` | gates and docks refused to each other's members; no extradition; privateers tolerated |
| −29 to +29 | `Neutral` | open trade at full tax |
| +30 to +59 | `Friendly` | each honours the other's registry (stolen hulls), warrants at one level lower, and commissions (military beacon rights) |
| ≥ +60 | `Allied` | as Friendly, plus shared wanted lists at full level and mutual defence at fronts |

Relations change only in the strategic step (section 5) and on declared events. Nothing reads the matrix per tick.

### Standing

Standing is what a faction thinks of one owner: a `short`, −1000 to +1000, per (owner, faction). It is slow, earned and never decays by itself.

| Band | Standing | What it gates |
| --- | --- | --- |
| `Enemy` | ≤ −600 | treated as a member of a faction at war: refused everywhere, engaged by patrols in the faction's space |
| `Unwelcome` | −599 to −200 | no docking at Secure stations, no services, managed gates refuse |
| `Neutral` | −199 to +199 | docking, trade at full tax, premade ships and tier 0-1 parts, prospecting permits |
| `Trusted` | +200 to +599 | tier 2 gated templates (recirculating drive parts, gated additive families), mining and tunnelling permits, leases, half tax |
| `Honoured` | ≥ +600 | freeholds in Secure regions, shield licences, a commission (military parts) and a letter of marque if enlisted |

- **A ship is judged by its owner, a person by themselves.** A ship owned by an organisation reads the organisation's standing and heat; a character walking into a station reads their own. So a clean pilot flying a notorious organisation's hull is refused the dock, and a notorious passenger on a clean ship is refused at the airlock.
- **How it moves:** cases (the offence table below), restitution paid (restores half the loss of an Accidental or Reckless case), missions, delivered bounties and prisoners, rescue and salvage returns, and tax paid (+1 per 1,000 credits, at most +5 a played day).
- **Propagation:** a change `Δ` with faction F also moves standing with every other faction G by `Δ · relation(F, G) / 200`: half to F's allies, the opposite sign to F's enemies. **Atrocities** (below) propagate to every Major faction at the stated flat amount whatever the relations.
- **Organisations:** an offence by a member acting on an organisation's ship or station applies fully to the member and at 25 % to the organisation. Organisation standing gates what the organisation owns (docks for its ships, its claims, its charter).

### Membership, organisations and charters

- **Enlistment:** a player may enlist with one faction (standing ≥ Neutral). Members get the faction's wormhole key (wormholes.md), the `Faction` gate policy, and its war: enemies' members are lawful targets and so are they.
- **Organisation:** up to 128 members, one leader, officers with rights (treasury, claims, policy, declare). Cap 256 organisations a world.
- **Charter:** an organisation may charter under one faction (leader's standing ≥ Trusted). Its members count as enlisted, its stations fly that faction's law code and join its fronts, and it may declare sieges under that faction's wars. An independent organisation is keyless (wormholes.md) and can only declare sieges in Lawless space.
- **Player factions are not in v1** (open question 1): chartering covers keys, law and sieges without a second relations layer.

### Ships and catalogue (D68)

- **Each faction has a catalogue:** the ship classes in `data/shipyards/*.json` and `data/traffic/shipclasses.json` gain a `faction` field. A faction's yards stock and refit only its own catalogue, and its plan licences cover only its own plans.
- **Fleets fly their faction's ships.** Every NPC route, patrol, garrison and navy row of a faction draws its `ShipClass` from that faction's catalogue. `foreignShare` (default 0.05, Outlaw clans 0.5) allows a few prize or bought hulls so a pirate clan's fleet looks like what it has stolen. Silhouette tells you whose ship it is before the transponder does.
- **No fleet discount.** Price is per unit at any quantity, for NPC and player buyers alike (ship-design-and-construction.md already holds this for every route to a ship). A chartered organisation gets catalogue **access**, not a lower price.

### Tech access and military rights

- **Access lives on part templates** (ship-design's `access {tier, faction}`). This doc defines what satisfies it: `Public`; `Standing(band)` with any faction that lists the template; a `ProductionLicence` item for the template, sold by that faction at Trusted; or `Commission` for `MilitaryOnly` templates.
- **Recirculating drives** (exotic-matter.md): the diluter and separator templates are `Standing(Trusted)` with a faction whose data lists the drive kind. Faction-keyed options (auto-purge, inline assay) keep exotic-matter's rule: they stop working off-key, so a stolen or salvaged drive is the dangerous kind.
- **Crude kit needs no licence** (exotic-matter.md) and stays that way.
- **Commission:** a licence item for an enlisted member at Honoured, or a chartered organisation's officer. It allows buying and producing `MilitaryOnly` templates, including the military warp drive with the Beacon switch.
- **Military beacon rights:** `MilitaryOff` is lawful only where the jurisdiction's authority recognises the commission: the issuing faction, any faction Friendly or better with it, and Lawless space. Anywhere else a silent arrival is the `UnbeaconedArrival` offence (the same fine as a cut wire), and between factions at war it is an act of war. Liability for what a silent arrival hits is unchanged (warp.md).
- **Letter of marque:** issued to an enlisted member at Honoured while the faction is at war. It makes hulls and cargo taken from that enemy lawful prizes (section 6).

## 2. Law

### The incident record (owned here from now on)

rings-and-asteroids.md defined the record "until a law doc exists". It moves to `src/Sim/Law/Incidents.cs` unchanged in meaning, with these changes:

- ids are `OwnerId`; `ActorHull` is added (the grid whose weapon, emitter, drive or tool was used). Its title-holder is the **principal**, looked up at the flush: an organisation whose member acted on its ship, or an AI ship's owner;
- `Jurisdiction`, `Evidence`, `PersonsHurt`, `PersonsKilled` and `ValueMilli` (damage value, below) are added; the record grows from ~56 B to 64 B;
- new kinds are appended (existing values keep their order): `WeaponFire`, `Boarding`, `PersonHarm`, `SurrenderViolation`, `Perfidy`, `Theft`, `Hijack`, `IllegalDig`, `ClaimJump`, `IllegalShield`, `ImpoundBreak`, `TransponderOff`, `RadiationDose`, `ForcedCollapse`;
- `IncidentClass.Fined` is removed: settlement is a case state, not a class. Classes are `Hostile`, `Reckless`, `Accidental`.

Writers keep their own rules: 1 s accumulation per (actor, victim, source), the 1 Hz flush, `HostileMinJ`, the came-to-the-hazard cap, D33 `Liable`. AI actors are their owners. A record whose actor is unknown (transponder off, no logged input) has `Actor = None` and only `ActorHull`, and becomes a hull case (below).

**Damage value** is what the liable party owes the victim: the victim grid's repair quote for the brushes and components the event damaged (ship-design's `CostQuote`, evaluated once at flush, ~2 µs), plus a station shield's refill energy at the local power price, plus goods destroyed at reference price. Where no quote exists it falls back to `EnergyJ × 0.5 credits/MJ`.

### Evidence

A crime nobody can prove makes no case. The writer or the flush sets these flags; the jurisdiction's code says which are enough.

| Flag | Set when |
| --- | --- |
| `ZoneLog` | the event, actor or victim was inside a protected zone, a settlement footprint or within 2 km of a powered claim beacon |
| `Logged` | a `Logged` input names the actor (beacon, transponder, anchor `Release`, a `CommandCore` `Override`, ship-design-and-construction.md) |
| `Beacon` | a warp beacon or hazard beacon carried the actor's id for this event |
| `Transponder` | the actor's transponder was on and a lawful grid with powered comms was within `WitnessRangeM` (200 km, warp's `BeaconRange`) in the same SOI |
| `Sensor` | a lawful third party held a sensor track on the actor at the event tick (combat-and-sensors.md's `SensorSystem.HasTrack`: a lawful third party's observer group held the actor at Q2 or better on that tick. It replaces the interim rule of 20 km with line of sight) |
| `Victim` | the victim grid, or any of its crew, still had powered comms `ReportDelayS` (10 s) after the event |
| `Survey` | a land or band survey found it later (section 3) |

- **Secure:** any flag opens a case. **Patrolled:** any flag except `Victim` alone against an unknown hull. **Frontier:** `ZoneLog`, `Logged`, `Sensor` or `Survey`. **Lawless:** no case, ever.
- **The victim's side still remembers.** With `Victim`, `Sensor` or `Transponder` set, standing with the victim's faction moves by the offence's amount even in Lawless space. Silence the ship before it reports, with nobody watching, and nothing moves: that is what piracy in the dark is for.
- **Cut wires:** cutting a `Logged` line names the cutter. Cutting an ordinary status-return cable (data is one-way, D71) blinds a console, trips nothing and is not evidence by itself.
- **Hull cases:** with an unknown actor the case attaches to the hull's persistent id. The first docking scan, or the first time its transponder is read by a lawful grid, moves the case to its owner.

### Offences

One table per law code (`data/law/codes/*.json`). The default code is below. Fine is the base, in credits (a premade 15 m Wren is about 5,000); **restitution** (damage value to the victim) is always added on top and goes to the victim, not the authority. Standing is with the authority's faction. Heat is in points.

| Offence | Raised from | Fine | Standing | Heat |
| --- | --- | --- | --- | --- |
| Liable accident | any `Accidental` record with a `Liable` party (D33: legal-mining debris on a station or in its zone, even after a defence push) | restitution only | 0 | 0 |
| Nuisance blast | `WarpBlast` at ≥ 0.07 bar on property, nobody hurt (D16) | 100 | −5 | 10 |
| Transponder off | `TransponderOff` in Secure space | 100 | −5 | 10 |
| Unbeaconed arrival | `BeaconTamper` by the ship's own side; `MilitaryOff` where the commission is not recognised (D18) | 250, plus full liability for the arrival | −10 | 20 |
| Reckless damage | any `Reckless` record | 10 % of restitution, at least 50 | −10 | 20 |
| Illegal dig | `IllegalDig`: mining, tunnelling or building without the right (D74) | 200 + 2 × material value | −15 | 30 |
| Claim jumping | `ClaimJump`: working another party's claimed rock or land | 200 + 2 × material value | −15 | 30 |
| Illegal shield | `IllegalShield`: a region shield raised without a licence | 1,000 per played hour raised | −20 | 50 |
| Theft | `Theft`: cargo, jetsam or parts with a live owner | 2 × goods value | −20 | 50 |
| Weapon use | `TractorLock` without consent (D26, including a gravity-shield lobe pushing another party's ship that is not on a collision course), weapons fire that hits nothing of value, jamming another party's ship (both combat-and-sensors.md) | 500 | −30 | 100 |
| Hostile damage | any `Hostile` record on property: `WeaponFire`, `Thrown`, `Shatter`, `Detonation`, `AmBlast`, `Kicked`, `BeamPushed`, `TractorTear`, `WarpCollision`, `WarpBrakeCut`, a hostile `BeaconTamper` (shooting out someone's beacon), `ForcedCollapse` | 1,000 + restitution again (so 2 × in all) | −60 | 200 |
| Resisting | fire on a patrol after its hail; `ImpoundBreak` | 1,000 | −60 | +200 |
| Boarding, hijack | `Boarding` without a granted `DockRequest`; `Hijack` (taking a hull by force or by killing its watch) | 2,000 (+ hull value if not returned) | −80 | 300 |
| Injury | `PersonHarm`, `RadiationDose`: a player or NPC hurt | 1,000 + medical cost | −80 | 200 |
| Killing | `PersonHarm` with a death, per person | 5,000 + 5,000 to the victim's owner | −200 | 500 |
| Perfidy | `Perfidy`: hostile action within 60 s of surrendering | 5,000 | −150 | 400 |
| **Atrocity:** killing the surrendered | `SurrenderViolation`: harming a surrendered or captive person, or firing on a surrendered ship | 20,000 | −400, and −200 with every Major faction | 1,000 |
| **Atrocity:** warp attack | `WarpBlast` aimed at people (below, D58) | 20,000 + restitution | −400, and −200 with every Major faction | 1,000 |
| **Atrocity:** station drop | `StationDrop` caused by an attack or an owner's deliberate release over a footprint | 100,000 + restitution | to −1000, and −300 with every Major faction | 5,000 |
| Covert capture | `StationCapture` with no lawful siege or writ | 50,000 | −600 | 2,000 |

**Specific rulings** (each closes a hook in another doc):

- **Warp exits as a weapon (D58).** A `WarpBlast` is the `WarpAttack` atrocity when the pilot dropped below `QuietAltitude` and the predicted 0.35 bar ring (people injured) at commit covered a settlement footprint, a station or claim structure of another party, or a person outside the actor's party. The preview draws that ring with its warning (warp.md's footprint gains it beside 0.07, 0.5 and 1.5 bar), so the pilot knew. It is an atrocity **whether or not anyone was hurt** and whether or not a shield absorbed it (a shielded city was still attacked, as with station shields). A drop below `QuietAltitude` whose 0.35 bar ring touched nobody is a Nuisance blast or Reckless damage by result. An interdictor that forces a drop carries the offence instead of the pilot (warp.md).
- **Beacon tampering (D18).** Fine and blame go to whoever the `Logged` input names. The ship's own side: Unbeaconed arrival plus all liability. A hostile who destroyed the beacon: Hostile damage on them, and the arrival is judged as an ordinary silent one, unfined. The `Tampered` flag a docking scan reads is the open case.
- **People in caves (D72).** Heating, wedging or shooting a rock with people in its caves stays physically allowed. If a person is hurt, the class decides: **Hostile** when the people were in the rock before the actor's first site or fracture event on it (occupancy is public: the hazard beacon and any scan show it, D48), **Reckless** at most when they came to the hazard afterwards (rings' existing cap). Injury and Killing apply on top of the class. In Lawless space there is no case, but standing with the victim's faction moves if anyone reported it.
- **Debris liability (D33, D36).** Unchanged: liability is separate from class. The miner pays restitution for station, shield and in-zone damage in every class; class decides the fine, standing and heat on top. Deliberate shatter damage is Hostile damage.
- **Venting people (ship-atmosphere.md's question).** Venting **hostile boarders** off your own ship is lawful: self-defence (below) covers anyone aboard under a live `Boarding` record. Venting guests, passengers, your own crew, prisoners or the surrendered is Injury or Killing, and for the last two an atrocity. Crews are in normal clothes (D64), so after the 15 s klaxon a vented room kills within seconds anyone not hooded or suited: a venting that kills is Killing per person, not Injury. ship-atmosphere.md writes these as `PersonHarm` with the air event (vent, override, blow, barrier drop, shield inhibit) as `SourceEvent`; there are no air-event kinds.
- **The warp bubble (D66; warp.md's `WarpShell` proposal).** A person pushed or vented past the bubble wall in cruise is lost: `PersonHarm` with a death, judged exactly as venting at rest. Riding another party's hull through a warp and cutting in is `Boarding` like any other uninvited entry.
- **Tractors (D26).** A lock or sweep without consent is Weapon use even with no damage; a tear is Hostile damage. Consent is tractor-beams.md's flag and is the only defence.
- **Harming crew and crowds.** ai-ships-and-crew.md writes a Hostile record for any crew or crowd member hurt at any level. Here that is Injury or Killing per person.
- **Black-hole collapse, dose, anchor drops** (exotic-matter.md's "attribution hook"): each becomes an `IncidentRecord` (`ForcedCollapse`, `RadiationDose`, `StationDrop`) with the shares exotic-matter computes as co-actors.

### Dispositions: when a record is not a crime

Evaluated once when the case would open.

- **Self, party, permitted:** writers already write no record.
- **Self-defence (`Justified`):** the victim of this record has a live Hostile record against the actor, the actor's party or the zone the actor owns within the last `DefenceWindowS` (300 s); or is at heat level Fugitive or worse in this jurisdiction. No fine, standing or heat. Restitution is not owed. It never covers an atrocity.
- **Act of war (`ActOfWar`):** actor and victim are members or assets of factions at `War`, or parties to an Open siege on opposite sides. No fine or heat anywhere; standing moves only with the enemy. Atrocities are never acts of war: they apply in full.
- **Lawful force:** an authority's own patrols, garrison and commissioned members acting on a `ResponseOrder`, and anyone acting on a Fugitive or Outlaw inside that jurisdiction.
- **No evidence:** no case. The record stays in the server log for the profiler and for later evidence (a docking scan can still resolve a hull case).

### Cases, fines and restitution

- A **case** is the settled view of one or more records: defendant (or hull), authority, offence, fine, restitution and payee, heat, standing change, due time. Records for the same (defendant, authority, offence, victim owner) within 10 min merge into one case. Pool of 4,096 (64 B each); settled cases are evicted oldest first.
- **Paying:** from the law panel anywhere in that authority's jurisdiction with comms, at any of its stations, or through a fixer at a lawless port for +50 %. Restitution is collected first and paid to the victim as it comes in.
- **Due:** `FineDueS` = 2 h of the defendant's own played time. Unpaid past due, heat is raised to at least 100 (Wanted for non-payment) and stops decaying.
- **Cannot pay:** the wallet never goes negative. The remainder is a **debt** row: half of every sale to an NPC market and every award is garnished until it clears, restitution first.
- **Courts are a function, not a place.** There are no trials, judges, juries or jail timers. The one appeal is automatic: when later evidence changes attribution (a hull case resolves to a different owner, a `Logged` input names a hostile), the case is re-settled and refunds are paid.

### Crime status: heat

Heat is a `ushort` per (owner, faction): how badly that faction's authorities want you now. It is separate from standing, which is how much they like you.

| Heat | Level | In that faction's jurisdictions |
| --- | --- | --- |
| 0 | Clean | nothing |
| 1-99 | Cited | a docking scan demands payment before services |
| 100-399 | Wanted | docking only to pay or surrender; patrols hail and order `Yield`; listed on the zone beacon |
| 400-999 | Fugitive | patrols disable and board; an authority bounty is posted; anyone may lawfully disable or capture you there |
| ≥ 1,000 | Outlaw | kill or capture; station defences engage in zones; any force against you there is lawful |

- **Decay:** 20 points per hour of the **offender's own played time** (an organisation: while any member is online; an NPC owner: world played time), so waiting out a warrant offline does nothing. It is settled lazily from `(heat, playedStamp)`; a player's played time is `accumulated + (now − onlineSince)`, so nothing counts per tick. Decay pauses for 10 min after any new case and while a fine is overdue. A killing takes 25 played hours to cool; a station drop, 250.
- **Sharing:** Allied factions mirror heat at full level, Friendly ones at one level lower. Others ignore it. Heat does not exist in Lawless space.
- **Surrendering** to a patrol or a station (D49) halves heat at once and clears the rest when the case is paid.
- **Listing replaces rings' interim `HostileListS`:** the zone beacon names everyone at Wanted or above to everyone in the zone.

### Enforcement

Enforcement is ordinary AI behaviour (ai-ships-and-crew.md's `patrol` and `station_security`) fed by one event.

- **`ResponseOrder`** `{jurisdiction, target owner or hull, level, last known frame and position, tick}` is created when a case opens or a target is sighted. At most one per target per jurisdiction and 8 active per jurisdiction. It is re-planned on events (new case, sighting, a patrol lost, target docked), never polled.
- **Who comes** is real: the nearest patrol hull on a route through that holding, then the station garrison. Hulls come up through the AI levels, so a response takes travel time and can be exhausted.

| Tier | Wanted | Fugitive | Outlaw |
| --- | --- | --- | --- |
| Secure | nearest patrol dispatched; hail, `Yield`, scan | two patrols and the garrison; disable and board | everything in reach; station turrets fire in the zone |
| Patrolled | a patrol if one route passes within warp reach | dispatched | as Secure |
| Frontier | only on encounter and at stations | on encounter | dispatched from the nearest station |
| Lawless | none | none | bounty hunters only |

- **Escalation ladder** for every response: hail and demand → order `Yield` → disable (drive, then helm) → board and arrest → destroy only at Outlaw, or when fired on. AI never fires on a surrendered ship.
- **Losing the trail:** a target that warps away (the departure side learns only a direction) keeps its case. The order holds the last known position for 30 min, then falls back to docking scans: "the response moves to wherever the player next asks to dock" (ai-ships-and-crew.md).
- **Docking refusal and services** go through the one permission hook (section 6).
- **Arrest** (after `Yield`, boarding or a surrender): the patrol collects fine and restitution from the wallet, then seizes cargo at buyback value, then impounds the hull. No jail. The person is released at the nearest station of the authority.
- **Impound:** the hull is clamped at the authority's dock with the `Impounded` flag. It is released on payment plus 5 % of hull value per played day (capped at 50 %), and forfeited after 7 played days to the faction's yard stock as a real used hull. Cutting it loose is possible and is Resisting.
- **Equipment seizure:** a third Illegal dig or Claim jumping case within 24 played hours impounds the drill or tunnelling vehicle used.

### Bounties

- **Authority bounty:** posted automatically at Fugitive, `clamp(0.5 × owed, 500, 50,000)`. It pays **only what the authority recovers**: on a live delivery the target's wallet, cargo and hull are seized up to what is owed and the hunter gets the bounty out of that; on a witnessed kill the wallet is debited up to what is owed and the hunter gets at most half the bounty. If nothing can be recovered the hunter gets a 100 credit finding fee, once per target per played day. So a criminal and a friend cannot farm the treasury.
- **Player bounty:** a victim with a witnessed Hostile case against someone in the last 7 played days may post one from their own money (escrowed, 10 % fee to the treasury). It can be claimed anywhere, Lawless space included. It is a transfer, not a mint.
- **Excluded claimants:** the target, and anyone in the target's party or organisation now or in the last 24 played hours.
- **Collecting is lawful only where the target is Fugitive or worse, or in Lawless space.** Elsewhere the hunter commits the crime too.

## 3. Land and mining rights (D74)

### Land policy

Every surface region cell has a **land policy** from its authority.

| Policy | Hand tools (EVA drill, sampling) | Powered mining, cave drills, tunnelling vehicles | Building |
| --- | --- | --- | --- |
| `Open` (frontier land of a held body) | free | permit | permit |
| `Licensed` (most held land) | prospecting permit (free at Neutral, 10 m³ a played day per region) | permit, lease or freehold | permit, lease or freehold |
| `Reserved` (settlement footprints, parks, military ground) | refused by the dig clamp | refused by the dig clamp | authority only |
| `Wild` (unheld bodies) | free | free | free |

- **Tunnelling vehicles and cave drills are regulated equipment.** Their component templates carry `regulated: true`. Buying them is open at Neutral; a docking scan lists them; **using** them on a held body or in a held band needs a right that includes `Tunnel`. On Wild bodies and in Lawless belts they are free.
- Limits that are not law stay where they are: shores and seabeds are unminable (D30), heat stops digging near 1.7 km (D29), edits are permanent (D32).

### Rights

A **claim** binds a holder to a patch of ground or rock with a set of rights. Cap 4,096 per body, 8 per owner per body; 36 B each.

| Kind | What it is | Lasts |
| --- | --- | --- |
| `Freehold` | owned land, bought from the authority (Honoured in Secure regions, Trusted elsewhere). Circle of 50-1,000 m radius on the surface, from 1 km above it down to the dig floor | permanent; land tax per played day; lapses to the authority after 30 played days unpaid |
| `Lease` | rented land, same shape | while rent is paid |
| `Permit` | an activity licence over a region cell or a corridor: `Prospect`, `Mine` (quota in m³ per played day, depth limit), `Tunnel` (start, heading cone, length), `Build` (footprint) | 1-30 played days |
| `Approval` | a holder's grant to another owner on its own freehold or lease: rights and expiry, up to 8 per claim | as granted |

- **Rights bits:** `Dig`, `Tunnel`, `Build`, `Anchor`, `ExtractEm`, `Shield`, `Sublet`. `Anchor` is the "surface claim" exotic-matter.md's drop-footprint check already asks about: a station may anchor over a claim only with its holder's `Anchor` right or approval.
- **"Own the land or otherwise have approval"** is therefore exactly: the op lies inside a claim whose holder is you, your organisation, or someone who gave you an `Approval` with the needed bit; or inside a `Permit` of yours; or the land is Open or Wild and the tool is one that policy lets run free.
- **A freehold is not a wall.** Someone else digging in your freehold is not refused: it is Claim jumping, with `ZoneLog` evidence if your claim beacon is powered. Bases are defended by walls, turrets and law, not by making terrain indestructible. Settlement ground is the one exception, below.
- **Tunnels cross borders:** a tunnel permit is a corridor. Leaving it, or passing within 2 km of a settlement footprint or under another party's freehold without approval, is an Illegal dig for the volume outside.

### The check (server, once per stroke)

Every terrain edit is already validated on the server (planets.md: materials, the shore mask). The land check is added to the same step:

- `LandRights.Check(owner, body, direction, depth, toolClass)` → `Allowed`, `Illegal` or `Refused`. It reads the region cell, then a sparse table of claims keyed on a ~4 km cube-sphere cell (open addressing, preallocated). ~50 ns. The result is cached on the tool for 1 s per (cell, claim), so a drill held down checks once a second, not once an op.
- `Refused` is returned only for `Reserved` ground, through the **dig clamp**: the settlement footprints of a chunk are one more source for planets.md's per-chunk protected-column mask, and removal by tools is clamped to the generated density exactly as on shores. Impacts, bombs and blasts (the Huge edit tier) are not clamped: shields stop those, or nothing does.
- `Illegal` ops run. The server adds the removed volume and material value to an `IllegalVolume` counter per (owner, region cell or claim), 12 B, and writes an `IllegalDig` or `ClaimJump` record at the 1 Hz flush **if evidence exists** then.
- **Surveys:** unwitnessed counters wait. Each Licensed cell is surveyed by a heap event every 6 played hours: Secure and Patrolled cells always find the counter; Frontier cells find it on a seeded 1-in-4 draw; Wild and Lawless never. A found counter becomes a case with `Survey` evidence. So a quiet illegal tunnel in the backcountry is a gamble, and one under a city suburb is a certainty.
- Mined material carries no provenance. Once it is in a hold it is just ore (see Rejected).

### Rings, belts and asteroids

- **Band permit:** in a held band a `Mine` permit covers any unclaimed rock in it, sold at the authority's stations for a period. No permit is needed in Frontier-tier bands for hand sampling, or anywhere unheld.
- **Rock claim:** opening a site, or planting a powered claim beacon, on a body claims it for that owner while the site or beacon is live and 30 min after (cap 4 per owner, pool 1,024). It makes that owner the lawful miner: someone else working the rock in a jurisdiction is Claim jumping. It stops nothing physically (D72: others may still heat, wedge or shoot it), and presence in a cave is not a claim.
- **Cave drills and tunnelling vehicles on asteroids** need a band permit with `Tunnel` in a held band, and obey rings' damage-zone rule like hand drills.
- **The check in a band:** `LandRights.Check` has a band form for asteroid tools (owner, band id, the rock's claim, `toolClass` = `CaveDrill` or hand tool), with the same 1 s cache on the tool. An op without the right still runs and is counted toward `IllegalDig` or `ClaimJump`, as on land.
- **Protected zones** keep rings' physical ban on working rock inside `ProtectRadius`; no permit overrides it.
- **Replenishment** (D73: modest events such as asteroid captures) adds bodies to a band under the band's existing holding and permits. A newly captured rock is unclaimed.
- **Evidence** follows the band's tier: Secure and Patrolled bands are surveyed like land; Frontier only when witnessed.

## 4. Planetary and city shields (D58)

A **region shield** is the station shield's big sibling: a lazy energy pool behind a shell, which lets slow things through and makes fast things pay.

### Who has one

| Class | `E_max` | `P_r` | Shell | Who |
| --- | --- | --- | --- | --- |
| Town | 10 TJ | 5 GW | dome, radius ≤ 3 km | settlements of tier Town; players and organisations on a freehold with a `Shield` licence (Honoured), as a `RegionShield` core on a surface station grid |
| City | 100 TJ | 20 GW | dome, radius ≤ 10 km | authority only |
| Planetary | 5 PJ | 200 GW | whole body, at the top of the atmosphere | authority only, one per Major faction, on its capital world |

Most settlements have none: villages and frontier towns rely on law alone. Shield state is public (HUD, like station defence): class, pool, raised or down.

### What it stops

- **Warp-exit pressure waves.** For a blast of energy `W` centred at distance `R` from a dome of radius `r_d`, the dome intercepts `f = ½ · (1 − sqrt(1 − (r_d/R)²))` of it (the solid angle it fills), so `E_inc = f · W`. The pool absorbs `a = min(E_inc, E_s)`, and receivers inside the dome get the blast scaled by `1 − a / E_inc`. Receivers outside are unaffected. One evaluation per blast per shield in reach (~0.2 µs), inside warp.md's existing `WarpBlast` resolution. A 2,000 t ship dropping at 250 m beside a town offers about 2 TJ: a Town shield takes five before it is empty and refills one in 400 s. A 20,000 t ship offers 20 TJ and half of it gets through. That is D34's rule at city scale: a big enough hit, or sustained fire, gets through.
- **Bombardment.** Any body crossing the shell inward with a normal speed above `PassSpeedMs` (100 m/s) offers `½ · m · (v_n² − v_pass²)`: thrown and shattered rock, dropped-station debris, missiles, and a ship on a brake-cut overrun. Absorb and leak work as in `StationShield`: the pool pays, the body keeps `v_n' = sqrt(v_pass² + 2 · (E − a) / m)`. A 10,000 t station released from 100 km (8.8 TJ) is just inside a Town pool.
- **Weapons fire:** combat-and-sensors.md (section 3, "Region shields") routes rounds, shells, bolts and missiles through the same absorb call, and beams offer their power for each 0.1 s sample; what the pool cannot take carries on.
- **Not stopped:** anything crossing at or below `PassSpeedMs` (ships landing, aircraft, people, cargo, weather and air), and anything going **outward**. This matches D63 at ship scale: the shield does not stop bodies, it stops projectiles.

### How it meets the warp exit rules

- **D16 stands: there is still no exit floor.** Outside a raised shield a warp may drop anywhere clear of terrain, with the blast and its consequences.
- **A raised shield is an exclusion, like a grid's.** Its shell is added to warp.md's exclusion test: an exit point or corridor inside a raised region shield is pushed out to the shell plus ship radius plus 300 m at commit, as a non-permitting grid's exclusion sphere does. The client can check it (shield state is public), so the plot shows it as "a visible exclusion zone". The authority's traffic control can permit arrivals on published lanes or above its `QuietAltitude`, the conditional permission warp.md already has.
- **The push is physical, not legal.** A drained, unpowered or destroyed shield excludes nothing. So this answers warp.md's open question 6 without a no-drop law: ground is protected exactly as far as someone paid for a shield.
- **An overrun path** (brake lost) that meets a raised shell is a projectile crossing it: absorbed or leaked like any body. warp.md's overrun rule treats the shell as it treats terrain when checking what a brake failure could hit.
- **Beacons** for atmospheric drops already go to the planetary authority; a shielded settlement in the predicted footprint gets the beacon too.

### Use and abuse

- **Licence:** raising a region shield on a held body needs the `Shield` right from the authority. An unlicensed one is the Illegal shield offence for as long as it is up: it blocks arrival lanes and the authority's own reach. On Wild bodies anyone may.
- **It cannot trap or strike.** Only inward crossings above `PassSpeedMs` are touched. A body straddling the shell when it is raised is ignored until it has left the shell. Nothing is ever pushed.
- **It cannot be raised over someone else's land:** the dome's footprint must lie inside claims the owner holds or has `Shield` approval for, checked at raise like the anchor's drop footprint.
- **It does not stop the law.** Patrols cross slowly like everyone else, so a wanted player gains nothing by sitting under one.
- **Players get Town class only**, and it draws real bus power at 50 % efficiency like a station shield. Planetary shells are data-only.
- **Attacking a shielded city is still the atrocity.** Absorbed energy counts as damage for the record.
- **Detection cost:** coasting and rails bodies are tested once when their path is set (rings' scheduled-arrival test, extended to region shells, ~1 µs per raised shield on that body). Simulated bodies are tested per tick only in bubbles flagged as touching a raised shell and only when faster than `PassSpeedMs` (~5 ns each).

## 5. Wars off-screen (D54)

### What is simulated

A **strategic step** runs once per played hour (216,000 ticks) as one event on the scheduled-event heap. It is a pure function of `(strategic state, step index, world seed, the real traffic and station rows)`. With no players interfering, the whole history follows from the seed and the played time. Game time stops while the server is empty, so the war does too; nothing catches up on resume.

- **Relations drift:** every 4 steps each pair moves 1 toward its `baseline`. Friction pushes it down: incidents between the two factions' owners since the last step (−1 to −5 each, at most −10 a step) and a seeded border incident (1 in 64 a step, −5).
- **War** is declared when a pair reaches −60, neither is under truce and fewer than `MaxWars` (2) are running. Otherwise the value holds at −59.
- **Fronts:** a war opens a front for each theatre where the two hold adjacent holdings (same SOI, within warp reach, or joined by a natural wormhole). At most 16 fronts. A front has one number, **pressure**, −100 to +100.
- **Strength** of a side in a theatre is counted from the real rows: the military value of its live patrol, escort and navy hulls on routes in the theatre (`TrafficShips` rows not `DestroyedUntilTick`) plus the defence value of its stations there. Both values are combat-and-sensors.md's `CombatRating.MilitaryValue`, computed once per plan hash and scaled by a damaged hull's `hp`. ≤ ~1,024 rows, ~5 µs.
- **Pressure update** (integers): `p += clamp(16 · (S_a − S_b) / max(S_a + S_b, 1), −8, +8) + noise(−3..+3) − p / 16`. With even strength it wanders within about ±12. It settles near 60 only with a sustained 60:40 advantage and near 85 only at about 2:1.

### What pressure does

| Pressure (toward the weaker side) | Event | Effect in the world |
| --- | --- | --- |
| ≥ 30 | Skirmishing | patrol levels rise on both sides' routes in the theatre; loss draws use the war rate; escort and patrol contracts appear |
| ≥ 60 | **Blockade** of the weaker side's most exposed Frontier-tagged station | its routes are suspended (AI doc's existing rule), stock falls and prices rise; blockade-running, relief and convoy contracts appear |
| ≥ 85 for 12 steps | **NPC siege** of that station | a real declared siege (section 6) by real hull rows on station-keeping legs; evacuation traffic; strike and defence contracts |
| siege held at ≥ 60 for 18 of the next 24 steps | **Capture** | ownership flips (`StationCapture`), routes regenerate for the new owner, the holding and its zone change faction |
| otherwise | Siege lifted | routes resume |

- **Armistice:** each step at war adds 1 weariness to both sides, each lost hull 1, each lost station 25. At a combined 100, or after 200 steps, the war ends: relations reset to −30, fronts close, captured stations stay captured, and a truce of 100 steps holds.

### Why the map does not churn

- Only stations tagged `Frontier` (at most a quarter of a faction's stations) can flip off-screen. Capitals and core stations never do.
- A station that changed hands cannot flip again off-screen for 100 played hours.
- At most 1 off-screen capture per 24 played hours per star system and 2 NPC sieges at once.
- No faction can fall below 60 % of its generated holdings through off-screen events.
- The fastest possible off-screen flip, with one side at maximum advantage throughout, is ~17 steps to reach 85, 12 held, then 24 of siege: **about 53 played hours**. Typical is one flip per system per 100-200 played hours.

### How players perturb it

Nothing is scripted around players; they change the rows the step reads.

- **Destroying or capturing hulls** lowers a side's strength until the hull is replaced (`ReplaceTicks`, and only if the owner's yard holds the bill of materials).
- **Supplying a yard or a besieged station** restores it: replacement hulls and station defence need real stock.
- **War contracts:** a completed contract adds a strength-equivalent bonus for 6 steps, capped at 25 % of the side's strength.
- **Members' offences** are friction between their factions.
- **Capturing a station for real** (section 6) is a fact the step simply sees. Player captures are not limited by the anti-churn caps, except that capitals cannot be besieged.
- **Bounded:** the pressure clamp of ±8 a step applies to player-caused change too, so no group can swing a front in an hour.

### When players are near

- NPC blockade and siege fleets are ordinary `TrafficShips` rows. With a player within promotion range they become real ships under ai-ships-and-crew.md's levels and caps, and fight for real. Hulls lost in a real fight are lost in the rows.
- Far from players, fights between the rows stay statistical: the AI doc's per-route loss draw, at the war rate for routes in a front's theatre (D50: far ships are data unless they get into a fight).
- **An NPC capture deadline does not teleport a station out from under players.** While a defending player is in the `CommandCore` room, the flip is deferred and the besieger's boarding party is promoted (the AI doc's scripted-need rule) to contest it. The siege goes on step by step until the room is taken or pressure falls below 60.

### What players see

- `WorldNews` events (war declared, blockade, siege, capture, armistice): reliable, to everyone, ~16 B each, a few a played day.
- Mission boards: at most 4 contracts per front per step, generated from the front's state.
- Traffic: patrol density, convoys with escorts, suspended routes, refugees. All of it is route data changed on the step, through fields the AI doc already has (`PatrolLevel`, route suspension, `lossRate`).

## 6. Stations

### Ownership and the permission hook

- A station's owner is the `OwnerId` holding its `CommandCore` (ship-design-and-construction.md). Linked grids follow the core's grid.
- **`StationPolicy`** (owner-set data on the core's Data port): docking (`Public`, `Faction`, `Organisation`, `Private`), minimum standing, heat level refused, arrival lanes and `QuietAltitude` conditions, weapons-safe rule, interdiction ban, fees and tax, loiter rule (armed loitering inside the exclusion zone flagged hostile, warp.md).
- **One hook:** `ZonePermission.Check(zone, owner, action)` (~30 ns, on request only) is the "station permission hook" warp.md, exotic-matter.md, tractor-beams.md, rings-and-asteroids.md and the AI doc's `DockRequest` all call. It reads the policy, the owner's standing band and heat with the station's faction, siege state, and hull flags (`Stolen`, `Impounded`, override-capable, `Tampered`). Actions: `Dock`, `Service`, `WarpArrive`, `TractorAssist`, `WorkDebris`, `Interdict`, `Anchor`.

### Protected zones

Unchanged in mechanics (rings-and-asteroids.md, tractor-beams.md, warp.md): `ProtectRadius` 5 km, no rock-working inside, tractor delivery to other parties zeroed, exclusion push for unpermitted arrivals, shields and debris defence. This doc adds only that a zone is a `Zone` holding at Secure tier under its owner's law code, with `ZoneLog` evidence for everything in it.

### Sieges and capture (D27)

This replaces the interim rules in rings-and-asteroids.md and tractor-beams.md and the two free knobs `CaptureTicks` and `ForceOverloadGraceTicks`. Both now come from one row per station class in `data/law/siege.json`, driven by one state machine.

| Station class | `NoticeS` | `CaptureS` | `EvacGraceS` |
| --- | --- | --- | --- |
| S (outpost) | 10 min | 5 min | 60 s |
| M | 20 min | 10 min | 120 s |
| L | 30 min | 20 min | 300 s |

**Who may declare.** A faction, through its strategic step or a chartered organisation's officer, against a station of a faction it is at `War` with. An authority, by **writ**, against a station in its jurisdiction whose owner is Outlaw with it. Anyone, against a station in Lawless space. Never against a capital. One siege at a time per declaring organisation; 12 played hours before the same side may re-declare on the same station.

**States.** `Declared → Open → Captured | Lifted | Expired`.

- **Declared:** public and logged (`SiegeState` to the station's interest, both factions and the news). Nothing is lifted for `NoticeS`. For a station owned by a player organisation, notice counts only played time with a member of that organisation online, up to 24 played hours in total (open question 2).
- **Open:** for besiegers only, tractor-delivery zeroing toward the station and its zone is lifted, and so are zone courtesies. Besiegers and defenders are parties to an act of war. **Kept:** the rock-working and weapon ban on asteroids inside `ProtectRadius`, shields and debris defence, warp's exclusion push (besiegers still drop 5 km out), the interdiction ban, and incident records. Neutral parties keep full protection and have safe conduct to leave.
- **Lifted:** the besieger withdraws, or has no ship or character within 2 × `ProtectRadius` for 30 min. **Expired:** after 6 played hours Open.

**Capture: one clock.** A capture is an `Override` held at the `CommandCore`'s local panel. It fills a progress value from 0 to 1.

- Rate while an attacker holds the panel and no defender contests: `1 / (CaptureS · (1 + G))` per second, where `G` is the fraction of the station's staff and garrison still alive and not surrendered (from the crew manifest's alive mask). A fully manned L station takes 40 min; one whose crew has surrendered, 20.
- **Contested** (a defender alive, free and inside the core room's `GridVolume`): paused. **Unattended:** drains at the same rate.
- **Completing it** transfers ownership, policy, anchor `Release` and crawl authority, gate policies, the stock, the shield and `StationDefence`. Catchers re-key to the captor's faction (or to noise). Dock bookings are void; third parties' docked ships have 10 min of safe conduct. The zone's holding changes faction. A `StationCapture` record is written.
- **Striking colours:** the owner, or the on-watch controller with the owner's standing order, may surrender the station at any time. Transfer is immediate, and the staff become surrendered persons (below).
- **Counter-siege:** the previous owner may besiege with no notice for 2 played hours after losing it.
- **Without a siege** the panel works the same and the clock is the same (it is hardware), but nothing is lifted, so the attackers got in by stealth or by being let in. Completing it is the Covert capture offence under the owner faction's law, and that faction answers with a writ.

**Destruction stays possible but hard.** The physical barriers are the other docs': two independent anchor feeds beyond a 10 s buffer, an owner-authorised `Release` with a 60 s countdown, reserve drain before failure, and the public `AnchorOverload` alarm.

- The alarm's grace is `EvacGraceS` for the station's class, the time its crew needs to reach pods, and no longer a separate knob. Outside an Open siege other parties can only reach force overload where the zone permits them, so the grace is an accident margin.
- **While a capture is in progress or the station has struck its colours, besiegers who keep overloading the anchor are the actors of the drop.** A `StationDrop` is the heaviest atrocity whoever is at war with whom. Factions want the station; AI besiegers never overload an anchor.

### Stolen hulls: title, not possession

This replaces the AI doc's interim rule (refuse dock, refit and buyback everywhere).

- **Title.** Every hull has a registered owner (`Title`) with its owner's faction. Buying, building and lawful transfer move title. Flying a hull does not.
- **Reported stolen.** A hull taken without transfer is `Stolen` once its owner's side learns of it: evidence on the `Hijack` or `Theft` record, or an AI hull missing its next schedule event by 30 min (`Missing`, which becomes `Stolen` the first time a lawful grid reads its transponder or a docking scan reads its hull id). Until then it is just a ship.

| Where you bring it | What happens |
| --- | --- |
| A station of the title's faction, or of a faction Friendly or better with it | docking is permitted so the hull can be seized: impounded and returned, and a Hijack or Theft case opens against the holder if there is evidence of who took it. No refit, no buyback. **Returning it voluntarily** pays a 10 % salvage award and standing instead |
| A Neutral or Hostile faction's station | docking allowed; no refit, no buyback, no plan `Modify` licence. Stripping it yields parts at salvage condition (ship-design's rule) |
| A station of a faction at `War` with the title's faction | **prize court:** with a letter of marque, title passes for a 20 % prize fee; without one the yard buys it at 0.25 × premade price |
| A lawless port | a **fence** buys it at ≤ 0.2 × premade price out of the port's finite daily fence budget, or **re-registers** it to you for 25 % of the premade price and 2 played hours. A re-registered hull is clean everywhere except with the original faction, which remembers the hull id |

- **No mint:** NPC buyback (0.6) never applies to a stolen hull; the fence and prize prices sit below what its stripped parts fetch, and the hull's replacement was debited from its owner's yard stock (AI doc).
- **Faction-keyed options** on its drives stop working off-key (exotic-matter.md) wherever it goes.
- **Player hulls** follow the same table, with the owner's faction (or organisation, if unenlisted) as the registry. There is no insurance (section 7).

### Salvage rights

- **Derelict is not abandoned.** A hull with no qualified crew (AI doc's `Derelict`) still belongs to its owner. Boarding it is Boarding and flying it off is Hijack, unless you are **rendering assistance**: answering its `Distress` gives implied consent to dock, board and tow.
- **Awards:** returning a derelict to its owner's station pays 10 % of hull and cargo value from the owner (an NPC owner pays from its faction treasury). Rescuing crew or pods pays a flat award per person and standing.
- **Abandoned** (lawful salvage, title passes to whoever takes it): the owner declares it; or the hull is a wreck (no intact `CommandCore` or flight computer); or the owner failed to recover it by 3 × `RecoverTicks`.
- **Jetsam:** crates and loose parts keep their source owner for 30 min. Taking them in that window is Theft if witnessed, unless the owner is Outlaw there, the crates were jettisoned on purpose, or you are that owner. After that they are anyone's. Whoever destroyed the ship never gains a right to its cargo by doing so.

### Surrender and prisoners (D49)

- **A person surrenders** by the surrender action: weapons dropped, hands up. **A ship surrenders** by `Yield` or striking colours: spool cancelled, weapons safe, shields down, transponder on, no thrust beyond station-keeping, and a broadcast that sets `Victim` and `Transponder` evidence for anything done to it afterwards. **A station** strikes colours as above.
- **Protection:** harming the surrendered is the atrocity, in every jurisdiction and with every Major faction, for players and AI alike. AI crews surrender by behaviour data (`Yield` thresholds) and AI never harms the surrendered: pirates take the cargo and leave.
- **Perfidy:** hostile action within 60 s of surrendering voids the protection and is its own offence.
- **Captives:** a surrendered person held by a captor is a `Captive` (pool of 256): restrained, cannot use weapons or consoles, must be kept in breathable air (ship-atmosphere.md) and fed like crew. Outcomes:
  - **deliver** to an authority that wants them (the bounty or an arrest fee), or to their own faction (a rescue award);
  - **ransom:** NPC captives have a price by role in data, paid by their owner from its treasury; a player captive may accept a price;
  - **release** at any dock or in a pod.
  - Recruiting captives is not in v1.
- **A player cannot be held for ever:** after 10 min a captive player may choose to be released at the nearest lawful station, leaving their carried kit with the captor (open question 4).
- **Captured station staff** are surrendered persons of the new owner: released at the next dock call, ransomed, or kept on as paid staff if they were NPC employees of a company that accepts the new owner.

## 7. Economy hooks

Kept minimal: every row is a sink or a transfer, and only the treasury pays out.

| Hook | Rule |
| --- | --- |
| **Sales tax** | 2-8 % by faction on NPC market trades at its stations, to its treasury. Half at Trusted, none at Honoured |
| **Docking fee** | by size class per played hour after a free hour, set in `StationPolicy` |
| **Gate fees** | in throat-grade EM grams, as wormholes.md has it. A managed gate's fee table is keyed on standing band: members and Honoured at base, Trusted × 1.25, Neutral × 1.5, Unwelcome refused. Natural pairs stay free of any game-charged fee (D23) |
| **Land** | freehold price by region tier and area; land tax and rent per played day; permit fees by quota |
| **Fines, impound fees, prize and re-registration fees** | to the authority's faction treasury (a lawless port keeps its own) |
| **Treasury out** | bounties (only what is recovered), mission and contract pay, salvage and rescue awards, ransoms. Offers shrink as a treasury runs low; it never goes negative |
| **Markets by faction** | a faction's stations sell its catalogue and the templates it lists, to the bands that unlock them. Prices follow stock (AI doc); standing changes access and tax, never the price itself |
| **Embargo and blockade** | at `War`, each side's stations refuse the other's members. A neutral trader running a blockade breaks no law of its own, but the blockader may order `Yield` and seize war cargo; refusing is resisting an act of war, not a crime elsewhere |
| **Insurance** | none in v1. Restitution from the liable party is the only compensation. The `Policy` hook is reserved |

## Data definitions

```jsonc
// data/factions/meridian.json
{ "id": "meridian", "kind": "Major", "lawCode": "civil", "capital": "station.meridian_prime",
  "baseline": { "kestrel_league": 20, "outer_compact": -35, "red_wake": -80 },
  "catalogue": { "shipClasses": ["wren", "hauler_5kt", "cutter_mk2"], "foreignShare": 0.05 },
  "access": [ { "template": "part.diluter", "band": "Trusted" }, { "template": "part.separator", "band": "Trusted" },
              { "family": "add.lattice", "band": "Honoured" } ],
  "military": { "templates": ["part.warp_controller.mil"], "commissionBand": "Honoured", "commissionFee": 5000 },
  "tax": 0.04, "treasury": 2000000, "frontierShare": 0.25, "minHoldings": 0.6 }

// data/law/codes/civil.json   (one row per offence; unspecified offences inherit data/law/codes/default.json)
{ "id": "civil", "evidence": { "Secure": "any", "Patrolled": "anyButLoneVictimOnHull", "Frontier": ["ZoneLog", "Logged", "Sensor", "Survey"] },
  "offences": { "NuisanceBlast": { "fine": 100, "standing": -5, "heat": 10 },
                "Killing": { "fine": 5000, "toVictim": 5000, "standing": -200, "heat": 500 },
                "WarpAttack": { "fine": 20000, "standing": -400, "allMajors": -200, "heat": 1000, "atrocity": true } },
  "heat": { "decayPerPlayedHour": 20, "pauseAfterCaseS": 600, "fineDueS": 7200, "levels": [1, 100, 400, 1000] },
  "defenceWindowS": 300, "reportDelayS": 10, "witnessRangeM": 200000 }

// data/law/siege.json
{ "classes": { "S": { "noticeS": 600, "captureS": 300, "evacGraceS": 60 }, "M": { "noticeS": 1200, "captureS": 600, "evacGraceS": 120 },
               "L": { "noticeS": 1800, "captureS": 1200, "evacGraceS": 300 } },
  "openMaxS": 21600, "lapseS": 1800, "redeclareS": 43200, "counterSiegeS": 7200, "ownerOfflineNoticeMaxS": 86400, "safeConductS": 600 }

// data/law/land.json
{ "surveyPeriodS": 21600, "frontierFindChance": 0.25, "prospectM3PerDay": 10, "settlementKeepOutM": 2000,
  "claims": { "maxPerOwnerPerBody": 8, "freeholdRadiusM": [50, 1000], "rockClaimsPerOwner": 4, "rockClaimLingerS": 1800 } }

// data/law/region-shields.json
{ "passSpeedMs": 100, "classes": { "Town": { "eMaxJ": 1e13, "prW": 5e9, "maxRadiusM": 3000, "playerBuildable": true },
                                   "City": { "eMaxJ": 1e14, "prW": 2e10, "maxRadiusM": 10000 },
                                   "Planetary": { "eMaxJ": 5e15, "prW": 2e11, "wholeBody": true } } }

// data/law/strategic.json
{ "stepS": 3600, "maxWars": 2, "maxFronts": 16, "pressureClamp": 8, "noise": 3, "restoreDiv": 16,
  "skirmish": 30, "blockade": 60, "siege": 85, "siegeHoldSteps": 12, "npcSiegeSteps": 24, "npcSiegeNeed": 18,
  "flipCooldownSteps": 100, "maxFlipsPerDay": 1, "maxNpcSieges": 2, "truceSteps": 100, "warMaxSteps": 200, "contractBonusCap": 0.25 }

// data/law/hulls.json
{ "missingAfterS": 1800, "salvageAward": 0.10, "prizeFee": 0.20, "prizeBuy": 0.25, "fenceBuy": 0.20, "reRegisterFee": 0.25, "reRegisterS": 7200,
  "impoundPerDay": 0.05, "impoundCap": 0.5, "forfeitDays": 7, "jetsamS": 1800, "captiveOptOutS": 600 }
```

Settlements are world data generated with the planet (`Settlement {body, direction, radiusM, tier, authority, shieldClass}`); this doc needs only their footprints. What a settlement contains is a missing system.

## C# structures (zero alloc; `src/Sim/Law`, `src/Sim/Factions`, no Vulkan)

All tables are preallocated at world load. Nothing below allocates after that.

```csharp
namespace BrutalGame.Sim.Law;

public enum OwnerKind : byte { None, Player, Organisation, Faction, Company, Authority }
public readonly record struct OwnerId(uint Value)                 // 4-bit kind | 28-bit index; shared by every doc
{ public OwnerKind Kind => (OwnerKind)(Value >> 28); public int Index => (int)(Value & 0x0FFFFFFF); }
public readonly record struct FactionId(byte Value);              // 0 = none, 1..31 (wormholes.md's ushort narrows to this)

public enum SecurityTier : byte { Lawless, Frontier, Patrolled, Secure }
public enum HoldingKind : byte { System, Body, Region, Band, Zone }
public struct Holding { public uint Place; public OwnerId Authority; public FactionId Faction; public SecurityTier Tier;
                        public HoldingKind Kind; public byte LandPolicy, Flags; public ushort LawCode; public long SinceTick; }   // 24 B, cap 1024
public readonly struct Jurisdiction { public readonly ushort Holding; public readonly OwnerId Authority; public readonly FactionId Faction; public readonly SecurityTier Tier; }

[Flags] public enum Evidence : byte { None = 0, ZoneLog = 1, Logged = 2, Beacon = 4, Transponder = 8, Sensor = 16, Victim = 32, Survey = 64 }
public enum IncidentClass : byte { Hostile, Reckless, Accidental }
public enum IncidentKind : byte
{
    Shatter, AmBlast, Detonation, Thrown, BeamPushed, Kicked, TractorLock, TractorTear,
    WarpBlast, WarpCollision, WarpBrakeCut, BeaconTamper, StationCapture, StationDrop, Other,     // existing order kept
    WeaponFire, Boarding, PersonHarm, SurrenderViolation, Perfidy, Theft, Hijack,
    IllegalDig, ClaimJump, IllegalShield, ImpoundBreak, TransponderOff, RadiationDose, ForcedCollapse
}
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct IncidentRecord                                      // 64 B, server only; the one shared record
{
    public long Tick;
    public OwnerId Actor, CoActor1, CoActor2, CoActor3, VictimOwner, Liable;
    public EntityId ActorHull, VictimEntity; public uint ZoneId, SourceEvent;
    public float EnergyJ; public uint ValueMilli;                 // damage value, credits / 1000
    public ushort Jurisdiction; public IncidentKind Kind; public IncidentClass Class; public Evidence Evidence;
    public byte PersonsHurt, PersonsKilled, Pad;
}

public enum Offence : byte { LiableAccident, NuisanceBlast, TransponderOff, UnbeaconedArrival, RecklessDamage, IllegalDig, ClaimJump, IllegalShield,
                             Theft, WeaponUse, HostileDamage, Resisting, Boarding, Injury, Killing, Perfidy, SurrenderViolation, WarpAttack, StationDrop, CovertCapture }
public enum CaseState : byte { Open, Paid, Debt, Justified, ActOfWar, Resettled }
public struct LawCase                                             // 64 B, pool 4096
{
    public uint Id; public OwnerId Defendant, Payee; public EntityId Hull; public ushort Jurisdiction; public FactionId Authority;
    public Offence Offence; public CaseState State; public Evidence Evidence; public short Standing; public ushort Heat;
    public long FineMilli, RestitutionMilli, OpenedTick; public uint DuePlayed; public uint MergeKey;
}

public sealed class OwnerLaw                                      // SoA, index = owner slot (open-addressed by OwnerId), cap 8192; ~140 B a row
{
    public OwnerId[] Owner; public FactionId[] Enlisted;
    public short[] Standing;                                      // 32 per row
    public ushort[] Heat;                                         // 32 per row, value at HeatStamp
    public uint[] HeatStamp, HeatHoldUntil;                       // owner's played seconds; lazy decay
    public long[] PlayedAccum, OnlineSinceTick, DebtMilli;
}
public static class Standings
{
    public static int Effective(OwnerLaw t, int slot, FactionId f, long tick);        // settles heat lazily, ~30 ns
    public static void Apply(OwnerLaw t, in RelationMatrix r, int slot, FactionId f, int standing, int heat, bool atrocity);
}
public sealed class RelationMatrix { public sbyte[] Value; public byte[] State; public long[] SinceTick; }   // 32 x 32

public enum ClaimKind : byte { Freehold, Lease, Permit, Approval, Rock, BandPermit }
[Flags] public enum Rights : byte { Dig = 1, Tunnel = 2, Build = 4, Anchor = 8, ExtractEm = 16, Shield = 32, Sublet = 64 }
public struct Claim { public OwnerId Holder; public uint Place; public uint DirOct; public float RadiusM; public ClaimKind Kind; public Rights Rights;
                      public ushort Parent; public uint QuotaM3; public long ExpiryTick; public uint PaidUntilPlayedDay; }   // 36 B, cap 4096 per body
public enum LandResult : byte { Allowed, Illegal, Refused }
public static class LandRights
{
    public static LandResult Check(in LandTables t, OwnerId owner, int body, in float3 dir, float depthM, byte toolClass, ref RightsCache cache, long tick);
}
public struct RightsCache { public uint Cell; public ushort Claim; public LandResult Result; public long UntilTick; }   // on the tool, 16 B
public struct IllegalVolume { public OwnerId Owner; public uint Place; public float M3; public float ValueCr; }         // 16 B, pool 1024

public struct RegionShield { public int Body; public float3 Dir; public float RadiusM; public double EnergyJ0; public long Tick0;
                             public float MaxJ, RateW; public OwnerId Owner; public byte Class, Flags; }                 // lazy pool, cap 64
public enum SiegePhase : byte { None, Declared, Open, Captured, Lifted, Expired }
public struct Siege { public EntityId Station; public FactionId Besieger, Defender; public OwnerId Declarer; public SiegePhase Phase;
                      public long DeclaredTick, OpenTick; public uint NoticeLeftS; public float Progress; public long ProgressTick; public sbyte Rate; }   // pool 16
public struct Front { public FactionId A, B; public ushort Theatre; public sbyte Pressure; public byte HeldSteps, SiegeSteps, SiegeGood;
                      public EntityId Target; public ushort WearinessA, WearinessB; }                                    // pool 16
public struct ResponseOrder { public ushort Jurisdiction; public OwnerId Target; public EntityId Hull; public byte Level; public FrameId Frame;
                              public double3 LastKnown; public long Tick; }                                              // pool 64
public struct Bounty { public OwnerId Target, Issuer; public long AmountMilli, ExpiryTick; public byte Kind; }           // pool 512
public struct Captive { public EntityId Person; public OwnerId Captor, Owner; public long SinceTick; public uint RansomCr; }   // pool 256
[Flags] public enum HullLaw : byte { None = 0, Missing = 1, Stolen = 2, Impounded = 4, ReRegistered = 8, Prize = 16, Abandoned = 32, Surrendered = 64 }
public struct HullTitle { public OwnerId Title, Holder; public OwnerId OriginalTitle; public HullLaw Flags; public long SinceTick; }   // per grid entity

public static class Jurisdictions { public static Jurisdiction Resolve(in LawWorld w, FrameId frame, in double3 pos, uint zoneHint); }
public static class ZonePermission { public static bool Check(in LawWorld w, uint zone, OwnerId owner, EntityId hull, ZoneAction action, out RefuseReason why); }
public static class Cases { public static void Flush(LawWorld w, ReadOnlySpan<IncidentRecord> flushed, long tick); }    // 1 Hz, from the incident flush
public static class Strategic { public static void Step(LawWorld w, TrafficShips ships, ReadOnlySpan<TrafficRoute> routes, long stepIndex, ulong seed); }
```

## Networking

Everything is a reliable, tick-stamped event. There is no per-tick traffic and no client authority. Clients hold only public state.

| Message | When | To | Size |
| --- | --- | --- | --- |
| Holdings table, region grids, faction table, relations | join; deltas on strategic events | everyone | ≤ 24 KB + 1.5 KB a body at join |
| `StandingChanged {faction, value, heat}` | a case settles | that owner (and the organisation's officers) | 8 B |
| `CaseNotice {id, offence, fine, restitution, due, state}` | opened, merged, settled | defendant, victim's owner, zone owner | 24 B (replaces sending the 56 B record) |
| `WantedList` | zone or jurisdiction entry; deltas | clients in that zone's interest | 8 B an entry, ≤ 64 |
| `SiegeState {station, phase, tick, progress, rate}` | phase change, contest change | station interest, both factions, news | 20 B; clients extrapolate progress |
| `RegionShieldState {id, pool, rate, tick}` | raise, drop, hit | the body's interest | 20 B; lazy like `ShieldState` |
| `ClaimRows` | on opening the land overlay, on change | requester, within 50 km | 36 B a claim |
| `LandVerdict {cell, result}` | the tool's cached check changes | the tool's user | 6 B |
| `BountyPosted` / `BountyPaid`, `HullFlag`, `CaptiveState` | on change | interest of the subject | 12-16 B |
| `WorldNews` | strategic events | everyone | 16 B |

Evidence flags, hull cases, unwitnessed `IllegalVolume` counters and the strategic state's internals never leave the server: a player learns they were seen when the case arrives.

## Per-tick cost

**Nothing in this doc runs per tick.** One exception is bounded and local: the fast-body test in bubbles touching a raised region shield.

| Work | When | Cost (estimate) |
| --- | --- | --- |
| Record → case (jurisdiction, disposition, merge, standing, heat) | the 1 Hz incident flush, per flushed record | ~1 µs; damage quote ~2 µs when a grid was damaged |
| `Jurisdictions.Resolve` | per event | 50-100 ns |
| `ZonePermission.Check` | per dock, warp commit, lock, service request | ~30 ns |
| `LandRights.Check` | once a second per active tool per cell | ~50 ns |
| Heat decay, fines due, impound fees, rent | lazy, on read; due dates as heap events | ~30 ns a read |
| Survey | heap event per Licensed cell with a counter, every 6 played hours | ~0.5 µs |
| Response dispatch | per case opened at Wanted or above, per sighting | ~5 µs (nearest patrol over ≤ 64 routes) |
| Siege progress | on contest change; completion is a heap event | ~0.2 µs |
| Region shield: blast | per `WarpBlast` per shield in reach | ~0.2 µs |
| Region shield: bodies | path set (coasting) ~1 µs per shield; simulated bodies ~5 ns each per tick, only in bubbles touching a raised shell and only above 100 m/s | ≤ ~1 µs a tick in such a bubble |
| Strategic step | once per played hour | ~20 µs |

Memory: `OwnerLaw` ~1.1 MB at 8,192 owners; holdings 24 KB; claims ~150 KB a body at the cap; cases 256 KB; everything else under 64 KB. Profiler rows (`LawPanel`, `FactionPanel`): records flushed by class, cases opened by offence and disposition, land checks and cache hits, illegal m³ pending, response orders active, sieges, shield hits, strategic step µs, front pressures, treasuries.

## Files

- `src/Sim/Law/Incidents.cs` (moved from rings' proposed `src/Sim/Incidents.cs`), `Cases.cs`, `LawCodes.cs`, `Evidence.cs`, `Heat.cs`, `Enforcement.cs` (`ResponseOrder`, arrest, impound), `Bounties.cs`, `Captives.cs`, `HullTitles.cs`, `Salvage.cs`.
- `src/Sim/Law/Jurisdictions.cs`, `Holdings.cs`, `LandRights.cs`, `Claims.cs`, `Surveys.cs`, `RegionShields.cs`, `Sieges.cs`, `ZonePermission.cs`.
- `src/Sim/Factions/Factions.cs`, `Relations.cs`, `Standings.cs`, `Organisations.cs`, `Access.cs`, `Strategic.cs`, `Treasury.cs`, `WorldNews.cs`.
- `src/Debug/LawPanel.cs`, `FactionPanel.cs`. `--law-bench` and `--strategic-sim <hours>` headless modes.
- `data/factions/*.json`, `data/law/codes/*.json`, `data/law/{siege,land,region-shields,strategic,hulls}.json`.

## Tests

- **Records:** every existing writer's test still passes with `OwnerId`; an AI hauler's debris on a station makes a Liable accident case against its company (D33); a record with no evidence makes no case and one with `Victim` moves only the victim faction's standing.
- **Offences:** a 250 m drop beside a test settlement is `WarpAttack` with and without a shield; the same drop over empty ground is a Nuisance blast; a hostile-shot beacon fines the shooter, not the ship; a caver present before the first site event makes Hostile, one who arrived after makes at most Reckless (D72); venting a boarder is Justified, venting a captive is an atrocity.
- **Dispositions:** self-defence inside 300 s; act of war between enlisted members; neither excuses an atrocity.
- **Heat:** decays only with the offender's played time; does not decay offline or while the server is empty; overdue fine pins it at 100; surrender halves it.
- **No mint:** over 1,000 scripted crime, bounty, fence, prize and salvage cycles, total credits never exceed starting credits plus goods sold at buyback; a broke outlaw's bounty pays at most the finding fee.
- **Land:** `Check` returns the right verdict on, inside, across and under claim edges; the settlement clamp matches the shore clamp bit for bit; an unwitnessed illegal tunnel becomes a case at the next survey in Patrolled land and never in Wild land; cache invalidates on a claim change.
- **Region shield:** absorb and leak match the formula for 2 TJ and 20 TJ offers; slow bodies and outward bodies are never touched; an exit plotted inside a raised dome is pushed to the shell and one plotted inside a dropped dome is not.
- **Sieges:** capture time equals `CaptureS · (1 + G)`; contest pauses and absence drains; striking colours transfers at once; a capital refuses a declaration; overload grace equals `EvacGraceS`.
- **Stolen hulls:** each row of the table; NPC buyback never pays for a `Stolen` hull (the AI doc's test, kept); re-registration clears the flag everywhere but the original faction.
- **Strategic:** two headless runs of 2,000 played hours from one seed are identical; with no players no faction falls below its floor, flips per system stay ≤ 1 per 24 h and the mean is within the stated band; a scripted 2:1 strength cut flips a Frontier station in no less than 53 h; pausing the clock pauses everything.
- **Zero allocations** in the flush, the land check, the permission hook and the strategic step.

## Milestones

| | Depends on | Scope | Exit check |
| --- | --- | --- | --- |
| **F0** | M4 (network, save) | `OwnerId`, factions, organisations, standing, the moved `IncidentRecord`, persistence | records and standing round-trip a save; zero allocations |
| **F1** | M7 (warp), rings revision 4 | holdings, jurisdictions, cases, fines, restitution, heat, `ZonePermission`, `LawPanel` | a scripted warp blast, tractor lock and mining accident each produce the expected case; flush cost per record measured |
| **F2** | M3 (planet digging) | region grid, claims, permits, `LandRights.Check`, settlement clamp, surveys, rock claims | land check ns per stroke measured; an illegal tunnel is found at survey |
| **F3** | AI doc A5, combat and sensors docs | evidence from sensors, response orders, arrest, impound, bounties, surrender and captives, hull titles, salvage | a wanted player is hailed, stopped and fined by real patrol hulls; a stolen hauler is seized at its owner's dock and re-registered at a lawless one |
| **F4** | exotic-matter anchors, station shields | sieges and capture, region shields, warp exclusion by shell | two groups run a full siege of an M station; a Town shield takes five 2 TJ drops |
| **F5** | AI doc A5 economy | strategic step, fronts, blockades, NPC sieges, news, war contracts, treasuries | `--strategic-sim 2000` meets the stability bounds twice identically; a played session at a front shows the blockade in prices and traffic |

## Changes to other docs

- **rings-and-asteroids.md**
  - "Hostile acts and the incident record": the record, its kinds and classes are owned here. Replace the struct and enums with a pointer; ids become `OwnerId` (also `SourceOwner`, `LastBeamOwner`, site `Owner`, `Shooter`, `ThrownTag.Owner`, `HeatContributor`); drop `IncidentClass.Fined`. Keep tracing, classification, accumulation and the flush.
  - "Consumers": delete the minimal station response (`HostileListS` 1 h listing, the 10 min Reckless listing). Listing is heat level Wanted or above; docking refusal is `ZonePermission`.
  - "Sieges (D27, interim)": replace with a pointer to section 6. Delete `CaptureTicks`.
  - "Cave miners get no special protection" and open question 3: closed by D72. Keep the physics; add that hurting people in caves is Injury or Killing here, Hostile when they were there first.
  - Open question 11: closed by D74. Cave drills and tunnelling vehicles are `regulated` components that need a `Tunnel` right in held bands.
  - Add rock claims and band permits by reference; the hazard beacon carries an `Occupied` flag for rocks with people in their caves.
  - `src/Sim/Incidents.cs` in Files becomes `src/Sim/Law/Incidents.cs`. `IncidentRecord` message (56 B) is replaced by `CaseNotice`.
- **warp.md**
  - "Low and atmospheric exits": "Fines are the jurisdiction's" → the schedule is this doc's offence table; add `WarpAttack` (D58) and that a region shield scales the blast for receivers inside it. The preview and the beacon footprint gain the 0.35 bar ring.
  - "Choosing the exit point", exclusion zones: add raised region shields as exclusion volumes for E and the corridor; the overrun rule treats a raised shell like terrain.
  - "Arrival beacon": `MilitaryOff` is lawful only where the commission is recognised, else Unbeaconed arrival; "the planetary authority of the region" is the `Region` holding's authority; a shielded settlement in the footprint receives the beacon.
  - `WarpExclusion` gains a region form (`body, direction, radius`). Open questions 4 and 6 are closed (schedule here; shields, not no-drop law).
- **tractor-beams.md**
  - "Sieges (D27; proposed...)": replace with a pointer to section 6; keep what is lifted and kept.
  - "the same standing, fines and faction consequences as firing on it": cite the Weapon use and Hostile damage rows. "unless the zone permits it" is `ZonePermission.Check(.., TractorAssist | WorkDebris)`.
- **exotic-matter.md**
  - Station anchor, force overload: `ForceOverloadGraceTicks` is removed; the grace is `SiegeRules.EvacGraceS` by station class. Capture text points to section 6.
  - "another party's ... surface claim" is a `Claim` with the `Anchor` right.
  - The dose, collapse and drop "attribution hook" writes `IncidentRecord`s (`RadiationDose`, `ForcedCollapse`, `StationDrop`).
  - `data/factions/*.json` schema is owned here; "faction access" is section 1's access rule; `"licence"` on recipes is a `ProductionLicence`.
- **wormholes.md**
  - `FactionId`, "group" and "owner group" are defined here (`FactionId` narrows to a byte; a group is an `Organisation`; a party is not an owner). The key holder is a faction officer.
  - `GatePolicy.Faction` reads enlistment and charters. Fee tables may key on standing band. Redirect contests pool per `FactionId` as now.
- **ship-design-and-construction.md**
  - `CommandCoreStats` comment and the Command core paragraph: `CaptureTicks` → the siege rule here; the `Override` hold reports to `Sieges`.
  - `access {tier, faction}` is satisfied as in section 1; `MilitaryOnly` needs a `Commission`. `OwnerId` is the shared type here.
  - Grids gain `HullTitle`; buyback, refit and `Modify` licences refuse `Stolen` hulls; a docking scan reads `HullLaw` flags, open cases, override capability and regulated equipment.
  - Tunnelling and cave-drill templates carry `regulated: true`. Catalogue rows gain `faction` (D68).
- **ship-atmosphere.md**
  - "Whether venting boarders off your own ship is lawful is the law doc's call": lawful against hostile boarders, Injury or Killing otherwise, an atrocity against the surrendered or captive. Captives must be kept in breathable air.
- **planets.md**
  - "Edits, storage and network": server validation gains `LandRights.Check`; `Reserved` settlement footprints feed the per-chunk protected-column mask (tools only, not the Huge tier).
  - Add the surface region grid (6 × 16 × 16 bytes a body) and settlement footprints to generated world data.
- **frames-and-zones.md**
  - "Sieges and capture (D27, owned by ... a future factions/law doc)": point here. "claim checks per segment" for crawling anchors are `Claim` checks. The tick-keyed list gains heat, dues, rent, surveys and the strategic step.
- **ship-hulls.md**
  - Edit validation "permissions" means the grid's `HullTitle` holder, its organisation's rights, or build-site rules.
- **ai-ships-and-crew.md** (being revised elsewhere; these are the hooks it needs)
  - "Stolen hulls" interim rule and open question 8: replaced by section 6's title table.
  - "Off-screen conflict" and open question 4: the statistical loss draw stays for routes; faction war is section 5's strategic step over the same rows (D54). "No AI fleet battles are simulated off-screen in v1" becomes "fleet battles off-screen are the strategic step's strength count".
  - Open question 3 (surrender and prisoners): closed by D49 and section 6.
  - `station_security` and `patrol` act on `ResponseOrder`; behaviours' `lawful` flag means "has a law code"; AI never harms the surrendered, never overloads an anchor, never makes a warp attack.
  - `data/traffic/shipclasses.json` rows gain `faction`; routes draw classes from their owner's catalogue (D68). `OwnerId` is the type defined here.
- **README.md, review.md**
  - Add this doc to the docs table. Missing systems: remove "factions, law and fines" and "siege and capture beyond the interim rules"; add "settlements and NPC population on planets", "death, medical and respawn" and "missions and contracts".
- **combat-and-sensors.md** (D56; written the same day)
  - It supplies what this doc asked for: `HasTrack` for `Sensor` evidence, comms jamming against `ReportDelayS`, station turrets for the Outlaw response, weapons through the `StationShield` and `RegionShield` absorb calls, and `MilitaryValue` for the strategic step. It writes `WeaponFire` and `TransponderOff` records and sets the surrendered flag; it defines no offence.

**r7 consistency pass (2026-11-26).** The list above was applied in the staged docs: rings-and-asteroids.md (record struct and enums replaced by a pointer, `OwnerId`, no `Fined`, the minimal station response and `CaptureTicks` removed, `src/Sim/Law/Incidents.cs`, `CaseNotice`), tractor-beams.md, exotic-matter.md (`EvacGraceS`, `Claim`, the three record kinds), wormholes.md (`FactionId` as a byte, organisations), ship-design-and-construction.md (capture clock, `HullTitle`, `Commission`, `regulated`), ship-atmosphere.md (the venting ruling), planets.md (`LandRights.Check`, settlement footprints, the region grid), frames-and-zones.md, ship-hulls.md, warp.md (done in its round 6) and README.md. Added here from other docs' asks: the bubble-wall and hull-rider rulings (warp.md), the band form of the land check (rings-and-asteroids.md), `PersonHarm` for air events (ship-atmosphere.md) and the cut-wire evidence note (ship-design-and-construction.md). ai-ships-and-crew.md is outside the staged set: its items are still asks. review.md gains a pointer only.

## Rejected

- **Law as physics** (no-fire zones, undiggable freeholds, legal no-drop volumes): it turns rules into invulnerability and removes the point of evidence, patrols and shields. Only settlement ground and a raised shield gate anything.
- **Per-tick wanted scans or reputation auras:** every consequence here is an event at a dock, a hail or a flush.
- **Jail timers and trials:** time away from play is not a penalty worth building. Money, access and being hunted are.
- **One global reputation number:** it cannot express being a hero to one side of a war.
- **Provenance on goods** (stolen ore, contraband stacks): per-stack tags fight stack merging and the refinery maths for little play. Hulls carry title; goods do not, beyond the 30 min jetsam window.
- **Borders that move continuously:** holdings change only when a station changes hands.
- **Simulating battles off-screen:** the strategic step counts real rows instead; fights are real only where a player can see them.
- **Insurance in v1:** it needs fraud rules (self-destruction for payout) that restitution does not.

## Open questions for the user

1. **Player factions.** v1 lets player organisations charter under an NPC faction (keys, law, sieges under that faction's wars) or stay independent and lawless. Do you want organisations to be able to found their own faction, with their own relations, law code and wars?
2. **Offline owners.** Siege notice on a player organisation's station counts only time with a member online, up to 24 played hours, then opens regardless. Right balance, or should player stations be capturable only with a defender online?
3. **Settlement ground.** City and town footprints are physically undiggable by tools (the shore clamp), because edits are permanent and settlements cannot defend themselves. Everything else is diggable and merely illegal. Agree, or should even city ground be diggable and policed?
4. **Captive players** may opt out after 10 min, leaving their kit. Too short, too long, or should a captive always be able to leave at once?
5. **Faction count and names** are content: 4 major, 2-3 minor and 2 outlaw clans around the start system is the proposed shape.
