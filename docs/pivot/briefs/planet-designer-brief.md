# Planet designer: slice 1 brief

Decisions: D83 (authored world, fixed seeds), D78 (designer with brushes), D30 (fixed shores), D82 (licences). Builds on planet kinds slice 1 (`planet-kinds-brief.md`). Numbers are estimates until measured.

## 1. Three layers, three owners

| Layer | Owner | Where | Changes when |
| --- | --- | --- | --- |
| Generated | seed + kind | `data/planets/kinds/*.json` + fixed `kindSeed` | new kind id (never in place) |
| Authored | us, in the designer | `data/planets/<body>/` | a content version we ship |
| Player edits | players | the save's `EditStore` op log (`EditOp.Author`) | play |

- The designer never writes `EditOp`s. Everything it makes is function data (maps, sites, markers), so it shows at every LOD; edits only show close up, so a sculpt-as-edits tool would vanish from orbit.
- Every op the evaluator runs carries its origin: kind layers, then authored ops (flagged `Origin.Authored` in the program, ids from `body.json`), then player edits replayed on top. A content upgrade replaces only the authored layer and never touches the save's edit log (section 5).

## 2. Data model and files

```
data/planets/<body>/
  body.json          id, kind, kindSeed, version, maps[], sites[], markers[]
  maps/<name>.bmap   sparse tiled cube-sphere map
```

`body.json` (canonical order, so diffs stay readable):
```json
{ "id": "home", "schema": 1, "kind": "temperate-continental", "kindSeed": 1234, "version": 3,
  "maps":    [ { "name": "uplift", "res": 512, "bind": { "layer": "mountains", "mode": "amplitude", "range": [0, 2] } },
               { "name": "shape",  "res": 512, "bind": { "mode": "height", "range": [-600, 600] } } ],
  "sites":   [ { "id": "port-a", "type": "flatten", "pos": [x, y, z], "coreM": 250, "blendM": 150, "heightM": 42.5 } ],
  "markers": [ { "id": "newport", "type": "city", "name": "Newport", "site": "port-a" } ] }
```

**Maps.** Per face a vertex grid of (res+1)² u16 values; edge vertices are shared by the two faces (the brush writes both copies), so values are continuous across cube edges. A point picks its face from its position alone (largest axis, fixed tie order), never from the chunk, so every chunk and peer reads the same texels. Stored as 32² tiles; only tiles that differ from the default are written. File: magic `BMAP`, version, res, default, tile count, then (face, tx, ty, 33² u16) per tile; raw, no compression in slice 1 (a fully painted 512 map is 3 MB). res 512 on a 1,000 km body is about 3 km per texel: broad weights and reshaping, not detail.

Bind modes (decoded value `m` = lerp over `range`, the default code decodes to exactly the neutral value):
- `amplitude`: the named kind layer's output times `m` (neutral 1). Lets us raise or flatten mountain belts, crater fields, canyons.
- `gate`: multiplies the layer's gate (neutral 1, mask in [0, 1] so the compiler's mask rule holds).
- `height`: adds `m` metres to `h` before `seaClamp` (neutral 0). Broad reshaping: a plateau, a basin, a ridge.
- `biome` (later): u8 index per texel, nearest, read by surface rules and decoration. Needs materials (§5 of planet-kinds.md, not built).

**Sites** reshape ground at metre scale. `pos` is the planet-frame point as int64 at 1/1024 m (persistent-position rule); the evaluator uses its direction. `heightM` is explicit (the tool fills it from `Height` at the centre when placed, then we tune it), so a site does not drift if the kind's numbers move. Slice 1 type `flatten`, disc: `h += w(d) (target - h)`, `w` = 1 inside the core, smoothstep to 0 at core + blend, chord distance, exact 0 outside. Later types: `terrace` (several levels), `basin`, `ramp`, rounded rectangle with yaw.

**Markers** are not terrain: (id, type, name, site, tags). The game reads them for the nav HUD, spawn and points of interest; they feed `LandingSites` when it exists.

## 3. Compiler and evaluator

- `AuthoredLayer.Load(dir)` (headless, `src/Terrain/Planet/Authored/`), then `PlanetKindCompiler.Compile(kind, params, authored)`. A body with no folder, or an empty layer, compiles to the same program as today: no op added, same `ContentHash`. Test: bit-identical heights, chunk hashes and impostor bytes.
- New ops: `Map` (bilinear sample into a channel, coarse grid: texels are ≥ 1 km, far above the 256-voxel coarse spacing) and `Sites` (per point, after `seaClamp`). `amplitude` routes the layer into a temp channel, then `AddMul` by the map channel into its destination; `gate` multiplies into the existing gate channel; `height` adds before `seaClamp`.
- Determinism: bilinear and smoothstep use add, mul, div, sqrt only; SIMD row path mirrors scalar without FMA, as the other ops.
- **Bounds.** Each map keeps a min/max pyramid over tiles. A cap maps to one uv rectangle per face it touches (padded by one texel); the map interval is the min/max of the covered pyramid cells, which holds because bilinear stays in the hull of its four corners. Then existing interval rules (4-corner product, sum). Sites: if the cap can reach the support, `[min(lo, target), max(hi, target)]`; inside the core, exactly `target`. Both hold for every LOD.
- **Per chunk** the site list is culled by chord reach (`StampList`, ≤ 64; usually 0 or 1). Points outside every site cost one compare.
- **Cost estimate:** map on the coarse grid about 1 ns per point amortised, `AddMul` about 0.5 ns, a site in reach about 5 ns. Budget stays 150 ns per point; measured per LOD as the kinds bench does.
- **D30.** The compiler rejects a site whose target puts ground across sea + `ShoreProtectHeight` inside its disc, and a `height` map whose range can, unless the body is flagged unshipped. The tool shows the sea-mask difference against the last shipped version, so shores stay fixed once we ship.

## 4. Every level of detail

The authored ops are part of the program, so everything that evaluates the program sees them with no extra path: chunk lattices at every LOD, `Height`, `Bounds`/`Classify`, `IsSea`, shore depth, and the impostor bake (`Height` at LOD 12 / 10). Band limits: maps are smooth by construction; sites are evaluated at every LOD (sub-voxel sites alias a little at far LODs, harmless). Markers draw as HUD points at any range.

## 5. Content versioning and upgrades

- `version` is a counter the tool bumps on save; `AuthoredHash` = XxHash64 of the canonical bytes (body.json + map tiles). It folds into `ProgramHash`, which already joins `GeneratorVersion` in the handshake: a client on another content version is refused.
- A save records per body (`id`, `version`, `AuthoredHash`). Loading a save made on an older version: the new authored layer is used, the player's edit log is replayed unchanged on top (ops are world-space digs and fills, so they still land where they were made). Nothing of the player's is rewritten.
- Overlap check on load: authored items carry `"since": version`; any newer site or changed map tile whose region holds player edits is listed in the log (and later in-game). What happens there is an open question (Q4).

## 6. The tool

- **Recommended:** a standalone app `BRUTALPlanetDesigner` (`src/PlanetDesigner` headless model + commands, `src/PlanetDesigner.App` exe, `runPlanetDesigner.bat`). It renders with `Terrain.Runtime` (`TerrainWorld`, `TerrainRenderer`, `ImpostorTexture`) and reuses `Designer.Ui` style and `OrbitCamera` where they fit, without restructuring them. Not in `GameApp` (water work in flight there), not inside `BRUTALDesigner` (owned by the ship designer).
- **Camera:** orbit the body from 3 R to the ground (zoom by altitude), fly mode near the surface. Picking: ray against the analytic `Height`.
- **Brushes:** map paint (radius in km, strength, falloff; raise, lower, smooth, set), site tool (place, drag, core and blend handles, set or pick height), marker tool (place, name, attach to site).
- **Live update:** on stroke end, recompile (cheap), invalidate chunks whose bounds meet the stroke's cap, rebake the touched impostor faces (256² × 6 at 0.3 µs is about 0.12 s).
- **Undo:** own stack; a map stroke stores the tiles it changed before the stroke, a site or marker change stores the item before and after; one group per drag.
- **Save:** writes `body.json` and changed `.bmap` files, bumps `version`, shows the hash and the D30 shore check.

## 7. Slice 1 scope (adjusted after answers)

(a) format, loader, compiler support, empty layer bit-exact. (b) one `amplitude` or `height` map and `flatten` sites end to end, tests: bounds hold (10⁴ samples per LOD), determinism, scalar == SIMD, seams on a site across a cube edge, ns per point per LOD. (c) the tool: view, paint, place and move a site, undo, save. (d) the game loads `data/planets/<body>/` and shows it orbit to ground. One commit each.

## 8. Answers (2026-11-27, D84, D85)

1. Settlements: site + marker now. Buildings later, from a structure/city editor like the ship editor.
2. Tool: standalone `BRUTALPlanetDesigner`.
3. Bodies: the test system with fixed seeds in `data/systems/test.json`; planet `home`, moon `home-moon`.
4. Upgrade overlaps: the authored site wins. On loading a save made on an older version, player edits inside the core of a site whose `since` is newer than the save are dropped; everywhere else the edit log replays unchanged.
5. Slice 1 channel: a `height` map (our call; `amplitude` follows).
