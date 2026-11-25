# User decisions, round 5 (2026-11-25)

Answers to the consolidated open-question list. Do not re-litigate. Where these conflict with any doc, these win (then review.md's rulings, then the owning doc).

D26-D28 and D33 were applied across the docs in the r6 consistency pass. D34-D37 record the station and asteroid decisions given with rings-and-asteroids.md revision 4, so every user decision lives in one list.

Already recorded earlier in this round: 1 g on 1000 km planets; realistic planet spacing (warp ~3.7 min/AU); natural wormholes lead to other star systems (several Solar roots); ~8 players listen server, 32-64 dedicated; huge structures as several linked grids / bubbles; bodies on rails with spheres of influence, stations may sit where no orbit is possible (e.g. Lagrange points) via station anchors; game time does NOT advance while a server is empty; go straight to the sphere (island retired).

## Ships and flight
- D9 Interior gravity: plating gives gravity; a SEPARATE **inertial dampener** keeps everything inside its bubble moving with the ship (same speed) at the cost of energy, using exotic matter + energy, up to a limit (beyond its rating, occupants/cargo feel the excess acceleration).
- D10 Flight model: BOTH pure 6-DOF with flight assist AND aerodynamic lift from wing parts in atmosphere.
- D11 Hull detail: FULL MESH EDITING of hulls (vertex/edge/face modelling), and hulls must still be cuttable (boolean cuts, interiors cut away, damage holes) "and whatnot". Reconcile with the CSG brush blueprint: e.g. a closed-mesh brush type authored by mesh editing, combined with CSG add/subtract; keep compact, deterministic, server-validated.
- D12 Deliberate runaway override (scuttle/kamikaze): YES.
- D13 Runaway severity: the reaction chamber is very highly rated; the ENTIRE CORE can be ejected (core ejection is the main counterplay).
- D14 Multiple warp drives on one ship: their bubbles SUPPORT EACH OTHER; the resulting quality is an average weighted by provided power etc. (not "only one spools").
- D17 Mid-warp: crew move freely and interact with anything. Engineers can shut down the warp, and many things can cancel it. Components have WIRING (power/data/control), so what can cancel or control the drive depends on the ship's wiring setup (designer feature; cutting wires is a gameplay action).

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
  - Applied (interim until a factions/law doc): a declared siege is public; it lifts only tractor-delivery zeroing toward besiegers and zone courtesies, and keeps the asteroid ban inside `ProtectRadius`, shields, debris defence, warp's exclusion push, the interdiction ban and incident records. Destruction stays possible but hard (two anchor feeds, 60 s owner `Release`, `AnchorOverload` alarm with a grace window, `StationDrop` incident). Capture: an `Override` held at the station's `CommandCore` for `CaptureTicks` with no defender contesting transfers ownership, anchor authority, gate and catcher keys (`StationCapture`). Owners: rings-and-asteroids.md (Sieges), tractor-beams.md, exotic-matter.md (anchor), ship-design-and-construction.md (`CommandCore`).
- D28 Tractors without antimatter can only emit gravitons, so they can only PULL (push needs anti-gravitons, i.e. AM). Replaces the "25 % pumped mode".
  - Applied: drive kinds `Graviton` (no AM, Pull only, full rating, 1 W/N bus power), `Reaction` (AM, Pull or Push), `Overdrive` (AM). Without AM, Push, flips and Hold assist are refused and Hold is a pull-only tether. Station debris defence assigns AM-fed emitters only; docking assist without AM is a pull-only tether. Owner: tractor-beams.md.
- D34 Station SHIELDS against explosions and debris or shrapnel impacts: a capacity and a recharge rate. A big enough hit, or sustained fire, gets through. Replaces "asteroid debris does no hull damage inside protected zones". (rings-and-asteroids.md revision 4, `StationShield`.)
- D35 Debris that drifts into a protected zone stays REAL. Stations push it back out with tractor beams or mechanical arms, as far as their installed hardware allows; undefended outposts near mining have a real problem. (rings-and-asteroids.md revision 4, `StationDefence`; AM-fed emitters only under D28.)
- D36 Shattering an asteroid so that it deliberately causes damage is a HOSTILE ACT. Damage traced to a shatter or a thrown rock is attributed to the event's owner and feeds reputation, faction standing, crime status and station response. (One shared server-only `IncidentRecord`, defined in rings-and-asteroids.md until a law doc exists, used by warp.md and tractor-beams.md too.)

## Terrain and planets
- D29 Relief -8 km..+9 km and digging 2 km down: fine. Digging too deep brings extreme heat that damages components until it becomes impractical.
- D30 Shores and seabeds are UNMINABLE (so no dug holes below sea level at the shore; no flooding question).
- D31 Weather: port the pressure-system weather to planets.
- D32 Planet edits are PERMANENT.

## Asteroids
- D33 Fragments from legal mining near a station: ALWAYS the MINER's liability.
  - Applied: liability (who pays) is separate from class (intent). Debris of legal mining (made outside every `ProtectRadius`) that damages a station, its shield or anything inside its protected zone is the event actor's liability in every class, including debris redirected by station defence (`IncidentRecord.Liable`). Owner: rings-and-asteroids.md.
- D37 A shatter at the threshold breaks the WHOLE BODY: a crack through it, a lot of dust (a local fog cloud that cuts visibility and sensors for a while), and large fragments flung in different directions. Below the threshold a hit makes a crater and cracks. (rings-and-asteroids.md revision 4, `DustCloud`.)
