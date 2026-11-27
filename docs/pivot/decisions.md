# User decisions, round 5 (2026-11-25)

Answers to the consolidated open-question list. Do not re-litigate. Where these conflict with any doc, these win (then review.md's rulings, then the owning doc).

D26-D28 and D33 were applied across the docs in the r6 consistency pass. D34-D37 record the station and asteroid decisions given with rings-and-asteroids.md revision 4, so every user decision lives in one list.

Already recorded earlier in this round: 1 g on 1000 km planets; realistic planet spacing (warp ~3.7 min/AU); natural wormholes lead to other star systems (several Solar roots); ~8 players listen server, 32-64 dedicated; huge structures as several linked grids / bubbles; bodies on rails with spheres of influence, stations may sit where no orbit is possible (e.g. Lagrange points) via station anchors; game time does NOT advance while a server is empty; go straight to the sphere (island retired).

## Ships and flight
- D9 Interior gravity: plating gives gravity; a SEPARATE **inertial dampener** keeps everything inside its bubble moving with the ship (same speed) at the cost of energy, using exotic matter + energy, up to a limit (beyond its rating, occupants/cargo feel the excess acceleration).
- D10 Flight model: BOTH pure 6-DOF with flight assist AND aerodynamic lift in atmosphere. Revised 2026-11-27 (D85): lift comes from shape, not from marked wings.
- D11 Hull detail: FULL MESH EDITING of hulls (vertex/edge/face modelling), and hulls must still be cuttable (boolean cuts, interiors cut away, damage holes) "and whatnot". Reconcile with the CSG brush blueprint: e.g. a closed-mesh brush type authored by mesh editing, combined with CSG add/subtract; keep compact, deterministic, server-validated.
- D12 Deliberate runaway override (scuttle/kamikaze): YES.
- D13 Runaway severity: the reaction chamber is very highly rated; the ENTIRE CORE can be ejected (core ejection is the main counterplay).
- D14 Multiple warp drives on one ship: their bubbles SUPPORT EACH OTHER; the resulting quality is an average weighted by provided power etc. (not "only one spools").
- D17 Mid-warp: crew move freely and interact with anything. Engineers can shut down the warp, and many things can cancel it. Components have WIRING (power/data/control), so what can cancel or control the drive depends on the ship's wiring setup (designer feature; cutting wires is a gameplay action).
  - Narrowed by D71: wiring has two media, power and data. There is no separate control medium.

## Warp
- D15 Warp path: a SMOOTH path from start to end that avoids planets (server computes the curve; no manual via points required).
- D16 Exit floor: NO forced floor. Exiting warp in low atmosphere causes a massive pressure wave that can kill wildlife and possibly people; you may get FINES / blame for damage.
- D18 Warp safety beacon: a warp transmits its ARRIVAL LOCATION as a safety feature. It can be disabled on military ships, or the wire can be cut (fine / blamed for any resulting damage). The LAST PART of a warp is in real space: the ship is a large ship-shaped projectile (it can collide). This replaces warp.md's "intangible all the way, exit announced 3 s before" privacy model.

## Wormholes and economy
- D19 Wormhole detail questions: accept the docs' proposals.
- D23 Travel economics: NATURAL wormholes are the CHEAPEST; MANUFACTURED wormholes are the FASTEST; warp is for going somewhere specific, relatively close by, cheaply. (Replaces "warp cheaper, wormholes costlier" as a blanket invariant.)

## Exotic matter, antimatter, black holes
- D20 Antimatter energy/production scale: fine for now.
- D21 Black holes naturally last a very long time, but equipment can deliberately cause them to collapse sooner.
- D22 Remnant abundance: NO system flood. All existing exotic matter is assumed to have been recycled into planets and asteroids (like uranium from earlier stars). Remnants are rare, late events, not the main supply.
- D24 Radiation: both transient and lingering. Short-term exposure can be treated with medicine; long-term exposure causes health effects requiring expensive treatment.

## Stations, tractors, PvP
- D25 Station anchors:
  - Usable on ANY grid, but activation requires reaching the required position and (mostly) the required velocity first.
  - Anchored stations can MOVE, slowly over days, via anchor inputs (a slow crawl control).
  - Anchors cannot attach to asteroids; use tractor beams for that.
  - A dropped station is a serious, very uncommon disaster: it mostly burns up in the atmosphere or hits the ground hard. Stations should have thrusters (in case they need to fly) and escape pods.
- D26 Tractor beams are considered a WEAPON unless requested. E.g. you are expected to dock your own ship, but a station may tractor you in to dock if you ask for it (consent flag).
  - Applied: a lock or sweep on another party's ship, grid or character without consent writes an `IncidentRecord` of kind `TractorLock`. Consent is a flag on a docking, tow or landing request, for one emitter owner group; it lapses on release, docking, a mouth crossing, or 120 s without a lock. Own and party bodies need none; unowned wrecks and asteroids are not ships. Anti-escape, pull-into-wormhole and combat locks are always hostile; consent never exempts a tunnel strike. Owner: tractor-beams.md (Multiplayer, Permissions).
- D27 Station sieges: YES; factions would prefer to CAPTURE stations rather than destroy them (capture mechanics matter).
  - Round 6: factions-and-law.md "Sieges and capture" now owns the rule and replaces the interim knobs below (`CaptureTicks` became a capture clock per station class; the overload grace is `EvacGraceS`).
  - Applied (interim until a factions/law doc): a declared siege is public; it lifts only tractor-delivery zeroing toward besiegers and zone courtesies, and keeps the asteroid ban inside `ProtectRadius`, shields, debris defence, warp's exclusion push, the interdiction ban and incident records. Destruction stays possible but hard (two anchor feeds, 60 s owner `Release`, `AnchorOverload` alarm with a grace window, `StationDrop` incident). Capture: an `Override` held at the station's `CommandCore` for `CaptureTicks` with no defender contesting transfers ownership, anchor authority, gate and catcher keys (`StationCapture`). Owners: rings-and-asteroids.md (Sieges), tractor-beams.md, exotic-matter.md (anchor), ship-design-and-construction.md (`CommandCore`).
- D28 Tractors without antimatter can only emit gravitons, so they can only PULL (push needs anti-gravitons, i.e. AM). Replaces the "25 % pumped mode".
  - Applied: drive kinds `Graviton` (no AM, Pull only, full rating, 1 W/N bus power), `Reaction` (AM, Pull or Push), `Overdrive` (AM). Without AM, Push, flips and Hold assist are refused and Hold is a pull-only tether. Station debris defence assigns AM-fed emitters only; docking assist without AM is a pull-only tether. Owner: tractor-beams.md.
- D34 Station SHIELDS against explosions and debris or shrapnel impacts: a capacity and a recharge rate. A big enough hit, or sustained fire, gets through. Replaces "asteroid debris does no hull damage inside protected zones". (rings-and-asteroids.md revision 4, `StationShield`.)
- D35 Debris that drifts into a protected zone stays REAL. Stations push it back out with tractor beams or mechanical arms, as far as their installed hardware allows; undefended outposts near mining have a real problem. (rings-and-asteroids.md revision 4, `StationDefence`; AM-fed emitters only under D28.)
- D36 Shattering an asteroid so that it deliberately causes damage is a HOSTILE ACT. Damage traced to a shatter or a thrown rock is attributed to the event's owner and feeds reputation, faction standing, crime status and station response. (One shared server-only `IncidentRecord`, defined in rings-and-asteroids.md until a law doc exists, used by warp.md and tractor-beams.md too. Round 6: factions-and-law.md is that law doc and owns the record.)

## Terrain and planets
- D29 Relief -8 km..+9 km and digging 2 km down: fine. Digging too deep brings extreme heat that damages components until it becomes impractical.
- D30 Shores and seabeds are UNMINABLE (so no dug holes below sea level at the shore; no flooding question).
- D31 Weather: port the pressure-system weather to planets.
- D32 Planet edits are PERMANENT.

## Asteroids
- D33 Fragments from legal mining near a station: ALWAYS the MINER's liability.
  - Applied: liability (who pays) is separate from class (intent). Debris of legal mining (made outside every `ProtectRadius`) that damages a station, its shield or anything inside its protected zone is the event actor's liability in every class, including debris redirected by station defence (`IncidentRecord.Liable`). Owner: rings-and-asteroids.md.
- D37 A shatter at the threshold breaks the WHOLE BODY: a crack through it, a lot of dust (a local fog cloud that cuts visibility and sensors for a while), and large fragments flung in different directions. Below the threshold a hit makes a crater and cracks. (rings-and-asteroids.md revision 4, `DustCloud`.)

## Ship atmospheres (2026-11-26)
- D38 Ships have atmospheres inside.
- D39 Hull damage can leak atmosphere.
- D40 Airlocks are needed.
- D41 More advanced ships can use AIR BARRIERS: a small particle shield across an opening that keeps air in the room; it needs constant power.
- D42 EMERGENCY SHIELDS may exist that stop breaches from leaking. (Design: ship-atmosphere.md.)

## AI ships and crew (2026-11-26)
- D43 The game has AI ships.
- D44 AI crew are not fully simulated until needed.
- D45 The pilot is a placeholder for a ship-flying AI.
- D46 Players see the crew of other ships and stations passing the windows, even as a simpler render. (Design: ai-ships-and-crew.md.)
- D47 Player ships can have AI crew.
- D48 Crew positions can be scanned.
- D49 People can surrender.
- D50 Prepare for 200 hulls actually rendered at once. Far ships are simplified further and become data unless they get into a fight.
- D51 Max crew is 100 per ship. If needed, crew on other floors or outside the player's room are simplified.

## Round 6 (2026-11-26)
- D52 Building: sessions and agents doing implementation work may build and test.
- D53 The pilot DOES matter to an AI ship. Pilots differ: flying styles and/or a skill level.
- D54 Factions fight wars outside the players' view, but things are mostly stable.
- D55 Ships get shields too, of three kinds: PARTICLE shields, ENERGY shields and GRAVITY shields.
- D56 A combat and sensors design is wanted (weapon stats, sensor kinds and ranges).
- D57 A factions and law design is wanted (fines, crime status, stolen hulls, sieges, land).
- D58 Warp exits used as a weapon: planetary or city shielding may prevent the damage; doing it is definitely illegal.
- D59 Hull damage has all three forms: holes, dents and deformation.
- D60 No sea-water flooding of ships. Terrain around water is solid.
- D61 Air barriers have no size cap.
- D62 Emergency shield hold time depends on breach size; the shield feeds from a battery.
- D63 Emergency shields do NOT stop bodies.
- D64 EVA suits are restrictive, so crews are not in them by default; suit lockers sit around airlocks, hangars and crew rooms for changing.
- D65 Air is used at real rates.
- D66 Warp space is treated as real space (a holed ship is not sealed by warp; breaches vent).
- D67 No weld minigame. Welding takes a set time; unskilled welders make poor-quality welds.
- D68 No fleet discount. Fleets generally use their faction's ships.
- D69 The standalone designer is bundled with the game.
- D70 Construction time of about 70 minutes solo for a 15 m ship with basic tools is the right order of magnitude.
- D71 Wiring has two media: power and data. A data cable can be split to all inputs and outputs but is one-way.
- D72 Other parties may heat, wedge or shoot a rock with people in its caves; injuring or killing players or NPCs that way is illegal.
- D73 Replenishment events are modest (nothing dramatic): things like asteroid captures.
- D74 Tunnelling vehicles and cave drills are allowed but regulated. Factions control most planets, so you need to own the land or otherwise have approval.

Round 6 designs: D55, D56 and D59 in combat-and-sensors.md; D54, D57, D58, D68, D72 and D74 in factions-and-law.md; D60-D66 in ship-atmosphere.md; D59 (storage) and D71 (sockets) in ship-hulls.md; D64, D67-D71 in ship-design-and-construction.md; D58, D66 and D71 in warp.md; D72-D74 in rings-and-asteroids.md; D53 in ai-ships-and-crew.md. The r7 consistency pass applied them across the other docs.

## Planets and decoration (2026-11-26)
- D75 Planets are far too flat. Different KINDS of planet generation are needed; bodies never touch, so each can have its own generator. (Design: planet-kinds.md.)
- D76 Full 3D terrain features must be possible (caves, arches, overhangs).
- D77 Terrain decoration (rocks, trees and so on) is generated too. (Design: terrain-decoration.md.)
- D78 A planet designer tool may be needed: brushes that paint biomes and weight modifiers onto a planet. (Not designed yet. Likely shape: painted low-resolution cube-sphere maps per body that the planet kind's layers read as masks and weights, authored in the designer app shell.)
- D79 Bring back shore waves (waves rolling in and breaking). Shorelines are fixed (D30), so coastal maps and break lines can be baked once per coast region from the planet function and never rebuilt; adapt the breaker and coastal code from the `island-final` tag. After M2b.
- D79a Confirmed: shore waves means breaking waves.
- D80 Volumetric clouds. Each planet has a cloud map that evolves over time. (Not designed yet. Likely shape: coverage and cloud type from the sphere weather library's pressure systems, deterministic from seed + tick so every client agrees; a cloud layer on the planet impostor from orbit, raymarched volumetrics at reduced resolution near the ground, cloud shadows on terrain and sea.)
- D81 Use open-source textures for the ground. (CC0 sources preferred, e.g. ambientCG and Poly Haven; keep a licence record per texture. Likely shape: triplanar texture arrays in KTX2, one slot per terrain material.)
- D82 Asset licensing: the game will be sold, so only use assets whose licence allows shipping in a commercial product (CC0, or equally permissive with terms we can meet). Nothing "personal use", "non-commercial", "editorial" or share-alike that would bind the game. Keep a licence record per asset. Applies to textures, models, fonts, audio and code.
- D83 The world is authored, not random. Each body starts from a seed fixed by us (the player cannot enter one); that gives the generated layout, which we then change by hand and save as part of the shipped world: cities, mining outposts, reshaped land. (Likely shape: a per-body authored layer shipped in `data/`, read by every peer: painted biome and weight maps (D78), site stamps that flatten or shape ground, and placed settlements. It is part of the planet function, so it shows at every level of detail and costs no bandwidth; player edits stay a separate layer on top. Consequences: the main menu's seed box goes; the planet designer (D78) is the tool that writes this layer; a change to the authored world is a content version that saves must record.)
- D85 Body lift from shape (2026-11-27). Every exposed face of a ship's hull lifts and drags by its angle to the airflow, so a hull gives body lift and a wing is just a thin part; no wing marking, no airfoil picks. Control surfaces are small moving parts on hinges or rotors (the joints' sub-hulls), which turn their faces. Docked vehicles act as one: on docking or undocking the combined shape's air loads are baked again. Vehicles stored inside another (in a hangar or bay) do not count.
- D84 Sea wind per sea tile (weather wiring, 2026-11-27). Each 64 km `SeaTiles` tile takes the sphere weather's mean wind over its area every 60 s, quantized to 1 m/s and 10°, and has its own swell; a tile's sea is a pure function of seed, tile and tick (the surf carrier's phase in closed form, its crests up to ~1.5π/45 rad/s off during a 45 s blend). Each GPU slot draws its own tile's waves. Planet weather systems are 4× the island's (storms 45-65 km) so a storm reaches a tile's sea.
