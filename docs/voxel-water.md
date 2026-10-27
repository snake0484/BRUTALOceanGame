# Voxel water

Water lives in the voxel grid. Voxels say **where** water is. The swell and FFT (`water-architecture.md`, layers A/B) say **how the open-sea surface moves**. This replaces the 2D bathymetry flood fill that used to decide where the ocean is drawn.

## Model

- **Per-voxel fill**: each voxel stores `Water`, a byte 0–255 giving the fraction of the voxel's volume that holds water. It sits beside `Density` and `Material` in `VoxelChunk`, padded apron included.
- **Solids hold no water.** A voxel with density < 0 always has `Water = 0`.
- **Sea level** is world y = 0, set in `TerrainParams.SeaLevel`. A voxel's sample point is its integer world position, so the voxel at y = −1 is the top sea layer.
- **Sea reservoir**: water connected to the open sea at or below sea level is not simulated cell by cell. It is held at sea level, with an infinite source and sink. Only water that leaves that state (pits, caves, lakes above sea level, spills) needs the flow sim.

## Generation (v1, implemented)

- Every non-solid voxel with world y < sea level gets `Water = 255`. So does every non-solid voxel of the crater lake: at or under its top layer, within its radius (`TerrainGenerator.FillLake`, "Lakes" below). Everything else gets 0.
- **Assumption:** generated terrain has no enclosed air below sea level, so all generated sub-sea water is open sea. The generator only adds overhang noise on land above +3 m, so this holds today. If a generator change ever creates sealed cavities, it must leave them dry.
- Water is generated per chunk with its apron, like density. Neighbouring chunks therefore agree on shared boundaries.

## Edits (v1, implemented)

- **Dig** removes solid and leaves water untouched. Carved former solid held no water, so a cavity dug into land or the seabed starts dry. Open water the brush overlaps keeps its water, so digging never cuts holes in the sea. A dig into a body of still water opens a disc of free water around it ("Bodies of water").
- **Place** makes voxels solid, and solids hold no water.
- **Fill** (`VoxelWaterSystem.Fill(centre, radius, top)`) fills every open cell inside a sphere and at or below `top` with water: a pond, a flooded room. It creates the blocks it touches and lands at the start of the first step each is ready in. Scripted scenes use it; it is not a per-frame call.
- Only a dig that reaches below sea level starts a sea zone. A zone over a hillside dig handed the pond to the zone sim, and the voxel mesh skips zone cells, so the pond vanished.
- Edits stay brush ops replayed after generation (`terrain-design.md`), so water edits replay deterministically too.
- **Known v1 gap:** a channel dug from the sea into a pit stays dry, and so does a cavity dug in the seabed, until the flow sim exists. The ocean surface still draws over seabed digs, because the column's top sea voxel still holds water.
- **Sea discs claim only the sea's run-up:** inside a sea zone disc, open cells over the sea's top layer were all tagged `SeaZone` (the zone's surface can stand above sea level in a surge), and the voxel mesher skips such columns, so a tunnel or stream coming down to the shore lost its water from the air where the disc's columns began. The disc now claims only `WaterBlock.ZoneRunUp` (4) layers over the sea; higher cells are the voxels' own and are drawn (2026-10-26).

## Bodies of water (implemented 2026-10-27)

Still water is held by **bodies** (`WaterBodies`, `src/Terrain/Water/WaterBodies.cs`), not simulated: the sea, the generator's crater lake, and any pool the sim leaves at rest. A body has one surface level, an area and a ledger. The lake is not a special case in the sim: it is the generator's one body record (`TerrainGenerator.Bodies`, built from `LakeInfo`), held and drained like any pool a player fills.

- **Held cells:** `WaterFlags.Reservoir` with the body's id in `WaterBlock.Body` (a `ushort` per cell, halo planes copied with the flags). A held cell sits at `WaterBodies.Fill(y, level)`: the part of the cell under the surface, and over full the pressure of the water above (`Compression` per metre), the vertical equilibrium of both flow models. It is never stepped; the flow passes reset it to its fill each step and book the difference (`WaterBlock.AddExchange`, gathered per body per step; the SIMD automaton gathers the levels with `Avx2.GatherVector256`). The sea is body 1 (level = its top layer's top, −0.5 m with sea level 0), infinite area, so it never moves.
- **Ledger and level:** `WaterBodies.Exchange` books what held cells gave or took; a `LevelStep` (1 cm) over the body's area moves the level (`SettleLevels`, once a tick), never under the body's bottom, and every loaded block holding it refills its held cells (`RefillHeld`), meshes again, and wakes if it holds free water or a rim the level now spills over (`WaterBlock.Spills`). So a small drain lowers the whole body uniformly at no cost, blocks loaded or not.
- **Joining, as a block sleeps** (`WaterBlock.JoinBodies`, main thread, halos fresh): free water connected to a body's held cells that stands **at the body's level** joins it: a cell with a free surface within `JoinTolerance` (5 cm) of the level, or a full cell wholly under it. Dry cells never join, and flowing water is never asleep, so a tunnel out of a lake drains it instead of the lake flooding the hillside outside to its level, which the old flood through every open cell under the surface did (`JoinLake`, gone). Columns in a live disc of the body are left alone. What the body paid to take the cells in (fill − water, about nothing at the level) and the surface cells it gained are booked (`Took`).
- **Born bodies** (`WaterBlock.BornBodies`, after joining): free water left that stands level, at least 4 surface cells with surfaces within twice the tolerance of one another, becomes a body of its own: level their mean, area their count here, centre their mean, bottom its lowest floor. Neighbouring blocks' water at that level joins it when they next settle, adding their surface. So a pond filled by `Fill`, or a pit the sea or a lake left full, costs nothing once still, and a later dig into it opens a disc like any body.
- **Discs** (`VoxelWaterSystem` `Disc`): a dig whose sphere reaches a body's held cells in a loaded block (`TouchedBody`) adds a disc, brush radius + `SeaZoneMargin` (6 m), keyed by that body; the sea keeps its zone sim instead. Every block under it (the body's bottom to its level) is read again with it (`WaterActivationJob` passes the discs; `InitFrom` / `RefreshFrom` mark the columns). Inside, the body's held water is **free** at the fill it was held at (`FreeFill`: in the pipe model a part-full cell on a bed takes the fill whose surface on that bed is the level, or the shore's half-cells slumped into the body or drew it in across seams), so it loads at rest; the dig starts dry and the free water pours in with momentum, the held cells beyond the disc refilling it and booking what they give. Nothing joins at a refresh any more.
  - **Settle:** once every block under the disc has been asleep for `DiscSettleSeconds` (3 s) the disc is dropped and those blocks, still asleep, join by level again (`Settle`): the hole's water is the body's and the blocks cost nothing. With the generator now filling the lake's bank nooks (`LakeShoreCells` 8 m), nothing seeps for minutes any more.
  - **Growth:** each step the body's exchange in the blocks under a disc is its flow (`MeasureDiscs`, smoothed over 2 s; a block's exchange counts once, for the nearest disc over it). Flow over `GrowFlux` (0.05 m³/s per metre of the disc's edge) grows it by `GrowStep` (4 m) every `GrowCooldown` (0.5 s), up to `MaxDiscRadius` (24 m), the new ring's blocks read again: a breach that runs a stream simulates a widening area with real currents toward it; a hole that only fills stops growing as its flow dies. A disc frees the body to its full depth, so unlike a sea zone (a heightfield) it costs 3D cells: 24 m over 10 m of water is about 18,000 cells and 18 awake blocks at 4 ms a step; the 48 m first tried was 95,000 cells and 36 blocks.
  - **Which dig opens a disc:** a dig above the sea is tested once its blocks have finished regenerating (`TestDigs`): the body whose held cells its sphere reaches gets the disc. Testing at the dig read stale cells, and a geometric fallback to the generator's record made a disc for every dig of a tunnel running in rock under the lake bed and freed a hundred metres of lake.
  - **Outlets:** a disc whose flow stays at least `MinOutletRate` (0.02 m³/s) and within `OutletSteadiness` (30 %) of its average for `OutletSeconds` (5 s) records that rate and the level. With its blocks evicted it is kept, not dropped, and drains the body at the recorded rate every tick (`DrainOutlet`), halving for every metre the level has fallen past the first since it was recorded (a weir slackens as its head falls), until it is next to nothing or the level meets the bottom. When its blocks come back the disc is re-read (its cut replayed, its water free) and the real flow measures it again. So a persistent waterfall keeps draining a lake it cannot see, at its own small rate.
- **Rain** (`RainOnBodies`): every body gains the rain intensity over its centre (`RainAt`, the weather map; `--rain` overrides it) × `WaterBodies.RainRate` (15 mm/h at 1) × its area each tick. The lake's 92,400 m² at a 5 mm/h rain takes in 0.13 m³/s; its level rises until a rim cell is under it, that block wakes (`Spills`) and the spill runs, so a lake with rain and a breach finds the level where outflow matches the rain by itself.
- **Generated bodies:** `TerrainGenerator.Bodies(params)` lists the lake as `WaterBodyInfo` (centre, fill radius, level, bottom, area = the shore's circle). The system registers each as a body at start and asks for its blocks within reach of the camera (`RequestGenerated`); `InitFrom` holds generated water above the sea for the record whose fill disc covers its column, and generated water no record claims is free (it settles and is born a body). A block straight from the generator with no edit or disc starts asleep (`WaterActivationJob.AtRest`).
- **Calm sleep:** a block whose fills change under `CalmDelta` (1e-3) a step and whose faces move under `CalmFlux` (0.1 m³/s) for `CalmSeconds` (3 s) sleeps like a quiet one and settles. The quiet rule alone (1e-4 a step) never fired for a pond at rest sloshing at millimetres, or for a pit's free water trickling 0.01 m³/s against held cells, so neither was ever born or joined and their blocks ran for whole scenes. A stream over a tenth of a cubic metre a second is never calm and keeps running; a disc ends on calm too.
- **Cost:** a body id per cell (78 KB a block, 40 MB for the pool) and a 16 KB exchange table per block touched only for the bodies present. Steps are unchanged except the held lanes' gather. Joins and births run once, on the main thread, as a block sleeps.
- **Not yet:** the pipe model's pressure ringing in a large deep free region against a held boundary (above), and the roughness and drift cap that hold a full tunnel's flow under its physical value; an outlet's blocks still step while loaded (0.5–1 ms a step near a waterfall; freezing the stream's last state as a drawn mesh would make it free); bodies never retire (ids are never reused, 4,096 of them); a body that drains apart into two pools is one body until both sleep and the lower is born anew; rain on free water; run-off from the catchment into bodies; evaporation.
- **Scenes and measurements** (`--lake-test hole|tunnel --water-pipes`, 2026-10-27): `hole` digs a 3 m hole at the water's edge once the lake's blocks are in; `tunnel` digs a 3 m tunnel from the crater's outer slope (where the ground is 30 m under the lake, 244 m off at z 0) up through the rim and breaches the lake last, one dig a frame. Both log the lake's level, the discs and outlets, the bodies, and what the lake gave (`WaterBodies.Body.Given`, m³ and m³/s) every half second; `tunnel` adds the water along the tunnel. The lake is 92,400 m² (about 1.1 million m³): a centimetre of level is 924 m³.
  - **Hole:** 4 blocks awake at 0.5 ms a step, pouring 4.9 m³/s for 20 s (86 m³); the pour then slackens to a trickle that keeps the pit's free water awake, the disc ends on calm at +44 s, the pit joins the lake and every block sleeps (`bodies 1`: the pit is the lake's, not a twin of it).
  - **Tunnel:** 6 discs (the digs within the lake's fill disc), 5.7 m³/s steady, the mouth 0.5 m deep: critical flow through a 6 m mouth half a metre deep, what a 3 m breach at a shoreline about a metre deep passes. The 23 m³/s measured before bodies was inflated: the old join turned tunnel cells under the lake's level into held cells at the lake's full pressure, a small case of the flood that drowned the hillside. The lake drops a centimetre every 160 s.
  - **Pond** (`--pond-test --water-pipes`): the bowl is breached 6 s after it is filled, while its 50 m³ still slosh over the calm floor, so it drains as free water as before; the pool its run-off leaves below the breach is born a body at +40 s. A pond left alone is born within a few seconds of going calm (`StillPondIsBornABodyAndDrainsUniformly`).
  - **Deep breach** (`--lake-test deep --water-pipes`: the tunnel comes up through the lake bed 80 m inside the shore, under about 8 m of water, and falls at 45° to the exit's height): the disc grows to its 24 m cap in seconds; the lake gives 300 m³/s as the tunnel fills, then about 65 m³/s net with the deep free region ringing at ±200 m³/s (fills of 3–4 in free cells 8 m down, far over the hydrostatic 1.2: the pipe model's pressure in a large sealed-off free region against a held boundary rings, as the lidded pit did); the tunnel runs 3–6 m deep and its front advances about 1.3 m/s; the level falls 70.000 → 69.960 in 60 s. Physically a 6 m tunnel under 20 m of head would pass several hundred m³/s; the Manning roughness 0.1 on every bed face (rock cuts run 0.035) and the 10 m/s drift cap hold it to a fifth of that. So a deep cut drains the lake about ten times faster than a shoreline cut, still only 4 cm a minute here.
  - **Rain** showed up unasked: with the weather over the crater the lake took in 0.03–0.04 m³/s (about 1.5 mm/h over its area) through both runs.
- **Tests:** `LakeLoadsAtRest`, `PitDugIntoTheLakeFillsAndJoinsIt`, `PocketTheLakeCannotReachStaysDry`, `LakeDiscPoursIntoAPitAndRejoins` (pipes); `PitBesideTheLakeFillsAndJoinsIt`, `PitAcrossABlockSeamSettles`, `ReservoirFillsPitGradually`, `InitFromMarksSeaAsReservoirAndDugSeabedAsDry` (automaton). The automaton's pressure rate (an eighth) now applies when either cell of a pair is full: a pit filling from held cells hovers about full, and with the quarter on those mixed pairs its cells flipped between 0.3 and 3.4 every step for good, which the old instant join had hidden.

## Wind on voxel water (implemented 2026-10-26, seams fixed 2026-10-27)

Voxel water (ponds, the lake, flooded pits, cave pools) is still unless something moves it: the flow sim sleeps, and the shader draws no motion of its own. The wind puts short waves on it where it can reach, the way it limits the ocean's wind sea by fetch and cover (`ocean-gpu-contract.md`, "Wind sea").

- **Reach, GPU** (`windReach` in `voxel_water.frag`): per fragment, a march of 8 steps of 2 m upwind over the cover map's column tops (`rainCoverTop`, `rain_cover.glsl`, the 128 m window around the camera). A top standing over the water's surface is a wall: reach = min over walls of distance / (6 × height, height at most 6 m), so a 1 m bank shelters 6 m and a cliff its whole lee. The first top at or over the water's level is the shore: reach × min(1, fetch / 6 m). Past the window the wind is open. Multiplied by the cover map's exposure (roofs and caves, fading over 10 m).
  - **Before** the reach was walked per column in the mesher with a 1-cell halo and stored as 8 bytes a vertex. The two blocks sharing a seam clipped their walks differently, so a seam corner had two ripple amplitudes, and with the ripples moving every seam read as a flashing line, a cross where four blocks met. World-space data agrees from every block; the vertex is 28 bytes again.
- **Wind:** the renderer takes the local weather wind (`RainRenderer.Wind`, the 10 m wind at the camera) in its frame uniforms. The fragment shader raises four short deep-water waves along the wind, 0.45–2.4 m long, spread ±30°, each coming in over a band of wind speed (the 0.45 m one from 1.5 to 4 m/s, the 2.4 m one from 4 to 10), travelling at √(g/k). Reach 0 gives a flat normal: glass. The three fixed sine ripples that used to run on every voxel surface are gone.
- **Not yet:** the ripples are normals only (no displacement, no foam, no FFT); the march is 2 m coarse and 16 m long (plenty for ripples, nothing for the chop a 300 m lake would raise at 15 m/s); fetch beyond the window is taken as open. Still voxel water shaded like the ocean (the plan's item 5) would replace this with the ocean's cascades under the same fetch rules.

## Ocean surface mask (v1, implemented)

- A 2 m column is **sea** when its voxel at world y = −1 holds water (fill > half). Only chunk layer Y = −1 contributes.
- The bathymetry grid stores this flag per cell, and unknown cells count as sea. The GPU depth map writes `NotSea` for non-sea columns, and the ocean shader discards there.
- So an inland pit is dry: its y = −1 voxels are air with no water.
- **Under an overhang:** the column still counts as sea when its y = −1 voxel holds water. The surface is drawn under the overhang, which is correct.
  - The depth under it is the **bed**: each leaf also records, per column, the topmost surface at or below the y = −1 voxel (`ChunkJob.ColumnBeds`). The ground the sea sees (`BathymetryGrid.Ground`) is the bed where it lies under sea level and the column's top otherwise, so land rising through sea level (beaches, hills) keeps its own surface.
  - The depth map, the shore reach and the sea-zone sim (`SampleGround`) all read the ground. The column's top is kept for what needs the roof: rain cover and camera clearance.
  - Before, depth came from the top alone. A roof, an arch or a rock placed over the sea made the column read as land above the water, so the ocean was discarded there (thickness < 0) and the zone sim under an edit went dry. The water under it vanished and the seabed showed through (`--roof-test`).
- **Water at other levels** (lakes above the sea, flooded pits, caves) is not part of the ocean surface. It will be drawn from a voxel-water mesh (see Rendering) and never from the swell.

## Flow simulation v1 (implemented)

v1 uses a **compressible cell automaton** in place of the momentum model below. It is mass-exact, stable, and job-friendly, and water visibly falls, spreads and fills. Momentum (sloshing, overshoot) comes later with the pipe model.

### Blocks (`src/Terrain/Water/`, namespace `BrutalGame.Terrain.Water`, pure CPU)

- A `WaterBlock` covers one LOD-0 chunk (32³) with a 1-cell halo, using the same `ChunkConstants.Index` layout. Each cell holds:
  - `float[] Water`: fill. 1 = full; slightly above 1 is allowed under pressure (compression).
  - `float[] Next`: the next step's fill.
  - `float[] OutScale`: the outflow limiter.
  - `byte[] Flags`: bit 0 = Solid, bit 1 = Reservoir (open sea: held at 1, infinite source).
- **Halo:**
  - Interior cells are owned by the block.
  - The orchestrator copies neighbour blocks' border cells into the halo before each pass: Water before pass 1, OutScale before pass 2, Flags when activated or refreshed.
  - Faces with no active neighbour get Solid halo flags. Water stays in the region; edges wake neighbours.
- `InitFrom(VoxelChunk chunk)`: the chunk has been generated and had ops replayed at LOD 0. Set Solid where density < 0. Set Reservoir where the chunk's byte water ≥ 128 and the cell is not solid (generated sea that no edit carved). Water = 1 for reservoir cells, else 0.
- `RefreshFrom(VoxelChunk chunk)` after an edit: recompute flags, keep water in non-solid cells, set water to 0 in solid cells, and set water to 1 in reservoir cells.

### Pair transfer, computed identically from the previous state by both cells

- **Vertical** (a above b): `total = Wa + Wb`. `lower = total <= 1 ? total : (total < 2 + C ? (1 + total·C) / (1 + C) : (total + C) / 2)` with C = `Compression` (0.02). `f = (lower − Wb) · VerticalRate` (0.5). Positive f flows down; negative f is pressure pushing up.
- **Horizontal** (a → b): if Wa > Wb and a is supported, `f = (Wa − Wb) · HorizontalRate` (0.25). Supported means the cell below is solid or holds ≥ 0.99. The mirror case gives a negative f. Otherwise f = 0.
- Pairs that touch a solid cell transfer nothing.

### Step (two passes; the orchestrator runs one job per awake block per pass, with a barrier between)

1. **`ComputeOutflowScale`:** for each interior cell, sum its positive outgoing transfers over the 6 neighbours. `OutScale = min(1, W / sum)`, or 1 if sum = 0.
2. **`ApplyFlows`:** for each interior cell, `Next = W + Σ_n actual(n→c) − Σ_n actual(c→n)`, where `actual = desired · OutScale[source]`. Then reservoir cells are set to 1, and anything below 1e-5 snaps to 0. Track `MaxDelta` and whether any border cell changed by more than 1e-4 (`BorderActive`), so the orchestrator can wake neighbours.
3. **Swap** Water and Next.

Mass is conserved exactly except at reservoir cells (the sea is a source and sink) and the 1e-5 snap.

### Sleep and wake

- A block with `MaxDelta < 1e-4` for 60 consecutive steps sleeps.
- It wakes on an edit, when a neighbour's `BorderActive` is set, or when a neighbour activates.
- Activation creates blocks around an edit (the affected LOD-0 chunks plus a one-chunk ring). It also creates the neighbour across any face where water reaches a border cell and the halo isn't solid.
- **Rate:** `StepsPerSecond` = 30, run inside the fixed tick.

### Not in v1

Coupling the bathymetry and sea mask to sim water, buoyancy against sim water, swell-driven nearshore cells, and persistence of sim water across block eviction (blocks are evicted beyond a radius and their water is lost).

## Water surface rendering (v2, 2026-10-27)

### Mesher (`src/Terrain/Water/WaterSurfaceMesher.cs`, pure CPU)

- Works per block, per interior column (x, z).
- **Top cell:** the topmost interior cell with W > 0.02 whose cell above holds less than 0.02 or is solid.
- **Skip the column** if that cell is the sea's (held by body 1) or a sea zone's; the ocean draws open sea. Every other body and all free water is drawn here.
- **Surface height** = (y − 0.5 + min(W, 1)) metres plus the bed's rise under a film, chunk-local. A cell's volume spans [y − 0.5, y + 0.5].
- **Thickness** = surface − (top of the solid below the water column), for depth tint and alpha.
- **Flow and foam** (pipe model only): the top cell's surface speed from its four side faces' fluxes over the water they move (m/s, world x and z; a halo column from the one face it shares with the interior), and how churned the water is: water landing from above (the cell over it falling, full at 6 m/s) and fast shallow flow (Froude past 1.2, half a point per unit, at most 0.6). The automaton gives neither.
- **Corners:** each corner vertex takes the average height, thickness, flow and foam of the wet columns among the 4 around it. One quad per wet column.
- **Normals** come from the corner-height gradient.
- **Output:** a `WaterVertex` (Vector3 Position, Vector3 Normal, float Thickness, Vector2 Flow, float Foam; 40 bytes) and uint indices in a reusable `WaterMesh`. No allocation after warm-up.

### Renderer (`src/Terrain.Runtime/Rendering/VoxelWaterRenderer.cs`)

- **Per frame:** `BeginFrame`, then `Add(ChunkCoord, WaterMesh)` for each block with water. Meshes are copied into this frame's host-visible vertex and index ring.
- **Draw** comes after terrain and the ocean: alpha-blended, depth test on, depth write off; push constant = chunk origin; frame uniforms carry the ocean's lighting and water colours (`SceneLighting.WaterDeep`, `WaterScatter`), the rain, and the local wind.
- **Shader** (`voxel_water.vert/.frag`), shaded **as the ocean is**: `shadeWater` and the foam lace live in `water_shading.glsl`, shared with `ocean.frag` and `ocean_lip.frag` (moved out of `ocean_shading.glsl`, which keeps only the FFT partials), so every water in the world has the same seabed-through-shallows body, turquoise scatter, sky reflection, GGX sun, foam, tonemap and fog.
  - **Normals:** the mesh's, plus wind ripples where the wind reaches ("Wind on voxel water"), plus **stream ripples** where the water runs: a noise slope slid along the flow by the flow map's two phases (`flowPhases`), stretched along the flow, so a stream's surface streaks and moves at its speed. Each ripple fades by the pixel's footprint against its wavelength (`resolved`), not by distance alone: by distance alone the sines aliased into moiré bands across the lake seen at a grazing angle.
  - **Foam:** `flowingLace` at coverage 0.75 × the vertex foam, streaming with the flow and churning as fast as the water is churned, plus the rain's flashes.
  - **Rain** rings and flashes as the ocean's, none under cover; rings finer than a pixel roughen the surface (`gRainRough`).
  - **Alpha** rises from about 0.35 when thin to about 0.9 when thick, to 1 at grazing angles and in the fog; foam keeps thin sheets visible but fades out with them.
- **Spray** (`VoxelWaterSystem.FindSplashes`, pipe model, once a tick after the steps): the zones' splashes, then every wet held-up cell of an awake block that water lands in from above at `PourSplashSpeed` (4 m/s, a fall of 0.8 m) or faster with at least `PourSplashDischarge` (0.3 m³/s) over its metre, the landing cell at least `PourPoolFill` (0.9) full, a pool rather than a film (a pour: impact speed the fall's, capped at 12 m/s as the zone sim caps its own, since the pipe model's fall speed under a cascade runs to 30 m/s and the spray launched at that threw droplets 20 m from a hole; direction the falling water's drift), and every one whose water arrives at `WallSplashSpeed` (0.4 m/s) or faster against a solid side standing a cell over the surface (a wall). They go to the ocean renderer with the zones' (`VoxelWaterSystem.Splashes`), which throws its spray there (`ocean_spray_splash.comp`). Without the pool test every stair of a run-off sheet counted as a pour (a falling cell fed sideways builds its fall speed past free fall) and the whole beach stood in mist.
- **Seen in game** (`--pond-test` and `--lake-test tunnel`, `--water-pipes`, 2026-10-27): the breach stream froths at its foot with spray thrown over the pond's lip, the sheet down the beach shows the sand through it with lace where it runs fast, the lake's hole pours with lace and spray. Before, voxel water was a flat tint with sky reflection and three fixed sine ripples, no foam and no spray.

## v1 as built: deviations and measurements

- **Reservoir pressure:** reservoir cells hold `1 + Compression·depth` below the sea top, not 1. At rest the compression rule fills deep water past 1, so a sea held at exactly 1 would pull water back out of pits. With the fix, pits level with the sea.
- **Mass-safe snap:** a cell that would keep less than `SnapEpsilon` sends everything, and horizontal transfers below `SnapEpsilon` are dropped. Mass drift is now about 1e-6 per step (float rounding).
- **Seams:** horizontal pairs across an X/Z block face at y = 0 always count as supported. The transfer is still bit-identical from both sides, which a test checks against a single-block run.
- **Clamped blocks never step:** a block that is all sea or solid never steps. A sleeping neighbour isn't woken across a face that is all sea or solid. This took 28 awake blocks down to about 8.
- **Sea zones: shallow-water sim (`ZoneFlowSim`).** Each edit adds a disc (brush radius + `SeaZoneMargin`, 6 m). The sea inside the discs no longer uses the cell automaton. It runs as a 1 m heightfield **virtual-pipe** model:
  - **State:** the bed comes from the terrain (bathymetry, bilinear), plus water depth and 4 outflow fluxes per cell. Water carries momentum and fills at wave speed. The model is stable: no jitter.
  - **Depth-aware pipes:** each face's pipe cross-section is the water over the higher of its two beds (hydrostatic reconstruction): f += dt·g·h_face·Δη.
    - Deep water conducts as deep water, with waves at √(g·h), and a film over a ledge only spills what stands on it.
    - Friction is a light background damping (`FluxDampingRate` 0.2/s) plus implicit quadratic bed drag (`BedDrag` 0.01, deceleration Cd·u²/h). Thin films slow; deep pools glide.
    - Before this, every pipe conducted like 1 m of water, with 0.996 damping per substep (about 1/s). A 14 m dig fed from the sea held a surface sloping 6.5 m across 18 m of 5–8 m deep water, drawn as a lid hanging over the hole. It now fills from the bottom in about 5 s, level to within about 0.3 m (`--seaside-test`).
  - **Edits:** new sea cells start as the open sea stands there now: the swell's height and its orbital flow (`SeaState`, sampled on a 4 m lattice and interpolated), over the pre-edit bed. When the dig lands, the bed drops under the water and it flows in. The bed is re-read whenever the bathymetry is rebuilt. (They used to start flat at sea level and still, which flattened every wave in a new disc.)
  - **Sea boundary:** cells just outside the zone that are open sea are held at the **swell height** each tick (`SeaSurface` = `OceanSwell.SampleHeight`), so real waves run into the zone and the hole.
  - **Velocity** comes from the face fluxes, with depth ≥ 0.1 m, capped at min(8, 2·√(g·h)) m/s so thin run-up films can't read as fast flow.
  - **Edge fade:** the sim surface blends into the swell over the last `ZoneBlendWidth` (5 m) inside a disc (B channel = weight). Foam spills `FoamSpill` (6 m) past the edge onto the open sea, fading.
  - **Hand-over in time:** a disc's weight also fades in over `ZoneFadeSeconds` (1.5 s) when it is added, and out over the same when it goes back to the ocean (`BathymetryGrid.AdvanceZoneFades`), the sim running it until it has faded out. The depth channel of a zone cell is the open sea's blended to the sim's by the same weight, so depth, surface and shading hand over together. Before, a disc switched the drawn surface from the swell to the sim in one tick.
  - **Growth (violent flow):** every `GrowCooldown` (0.5 s) the sim picks at most one edge cell to grow at and adds a `GrowRadius` (8 m) disc there. Triggers:
    - sustained inflow from the open sea above `GrowFlux` (1.5 m²/s per metre of edge): the net exchange through the sea edge averaged twice over the wave period (`WavePeriod`, the swell's peak period). A wave flows in and back out within a period while a pour keeps flowing in; read raw, every crest of a 1 m swell (about 3 m²/s) grew the zone, so it grew two discs a second for as long as waves arrived;
    - a surge standing `GrowSurge` (0.3 m) above both the local swell and the ground past the edge. Plain sea level against a low beach is the rest state and does not count.
    - Growth stays within `GrowReach` (16 m) of an edit disc and stops at `MaxCells` (12,000, bounding box). Terrain placed in the way raises the bed past the edge and so stops the surge trigger.
  - **Remerge:** a grown disc that is calm for `RemergeTime` (3 s) returns to the ocean. Calm means: surface within 0.25 m of the swell, speed < 1.5 m/s, little foam in deep cells, no sustained inflow. Beach wash shallower than 0.3 m is ignored. It fades out (above), then its blocks are re-clamped and the mask re-stamped. Edit discs stay.
  - **The once-a-second pop (fixed 2026-10-26):** in `--seaside-test` the sea around the dig popped about once a second. A debug view tinting zone weight red showed the zone growing in round lobes every 0.5 s until it covered the view, each lobe switching the water inside it from swell to a flat sim in a tick. Frame-to-frame difference over open sea near the camera, 80 ms apart: spikes to 20.0 against a median of 3.0 before; after (seeding, fades and sustained inflow), none over a 20 s burst covering the pour (max 5.0, median 3.1). Growth now waits for the pour (none for 7 s), adds about 9 discs while the hole fills, stops at 11–12 and hands discs back once calm; before, 30 discs by t+14 s and still growing.
  - **Cost:** seeding samples the swell about 10–25 times a growth, 0.03–0.08 ms. Its first samples took 13 ms while tiered compilation had `OceanSwell.SampleOf` in its slow early tiers (0.39 ms for 72 samples with tiering off); it is now compiled fully optimised and warmed while loading. Growth ticks still peak at 5–10 ms with or without seeding: `Resize` rebuilds the whole domain per disc. `--cave-test pool` now holds its 60 edit discs (3,400 cells) at 1.1–1.4 ms a tick, where wave-driven growth took it to 85 discs and 7,500 cells at 2–6 ms.
  - **Foam:**
    - **Injected** where the water surface falls by more than 0.5 m into a cell (a pour, scaled by fall height and inflow), where a front crashes (surface slope over `BreakSlope` 0.35 into wet water), and where flow is fast with depth behind it (`ChurnDepth` 0.5 m). Thin wash running up a beach stays clear.
    - **Advected** semi-Lagrangian along the velocity field, so foam streaks follow the pour and swirl in the pool.
    - **Settles** slowly (`FoamDecay` 0.1/s).
  - **Rendering:**
    - The bathymetry map is RGBA16F: R = depth (NotSea or −4 when dry), G = absolute sim surface, B = zone flag, A = foam.
    - In the zone, the ocean vertex shader replaces the analytic swell with the sim surface (swell already arrives physically) and keeps the FFT detail. Normals come from the sim surface gradient.
    - Shallow water is translucent everywhere (thickness alpha), and pits use a capped, slightly turbid tint.
  - **Sea caves and walls (implemented, checked in game 2026-10-25 with `--cave-test cave|pool|cove`, which digs one site on the coast east of the start camera and logs its fetch, exposure, zone cost and wall splashes):**
    - **Cave** (a tunnel at sea level through a rock mound, `--wind 14`): inside, exposure 0 and the water is glassy, frame-to-frame change over it 0.3–0.5 against about 4.5 for the chop seen through the mouth. The water meets the walls flat. Ripples fade out over the first few metres past the mouth.
    - **Pool** (a pit fed by an L-shaped channel, a berm upwind): fetch 4 m at its west edge and 16 m at its east edge (6 m at both since the 8 m fetch map, `ocean-gpu-contract.md`, "Wind sea"). Ripples only, with the inflow's foam, while the sea beyond the berm keeps its chop. A/B against the old dry-cell rule in the same shot: the old rule drew a tongue of water up the far wall, the new one stops flat; motion over the pool is the same (frame-to-frame 1.0–1.5 in both).
    - **Cove** (a bay open to the west, an onshore wind): open-sea fetch, full chop, as it should.
    - **Wall spray:** while the cove floods, 15–30 splashes a tick at 2–4 m/s into its back wall; a debug run drawing them red lined that wall. In the cave at wind 20 with a 2.5 m swell the water only reaches 0.5–1 m/s into its walls, so the spray rises centimetres and barely shows.
    - **Zone sim cost** (`--seaside-test`, old against new, two runs each): median 1.23 and 1.26 ms against 1.25 and 1.28 ms a tick, within noise. (About 5,200 cells cost 1.25 ms in both, twice the 0.65 ms measured earlier.) `--cave-test pool` makes 85 discs and 7,500 cells and costs 2–6 ms a tick: `Publish` and `InAnyDisc` loop over every disc per cell.
    - **Foam that didn't move (fixed, below):** zone foam was advected on the CPU, but its lace was drawn in rest coordinates with no flow in zones (the breaker flow is zeroed there), so on the glassy water of a cave or pool it read as painted on.
    - **Dry cells next to walls:** a dry 2 m cell stores its (discarded) surface at its ground − 1 m, but never above the lowest wet neighbour among its 8 (`PublishDry`). A cave wall's ground is the hill top over it (the column is solid through sea level, so `Ground` is the top). The interpolated surface used to climb from the water to 1 m under the hill top, and the part not yet faded by thickness drew as streaky sheets up the walls. Now it runs flat into the wall and is hidden by it.
    - **Wind sea limited by fetch and cover:** the FFT cascades are wind sea, so they keep only what the wind can raise over the water upwind (`ocean-gpu-contract.md`, "Wind sea"). An enclosed pool or flooded pit keeps ripples, a cove short chop. Under a roof they fade out within 10 m of open water, with their whitecaps. A sea cave's water moves only as the sim moves it, and the sim's own damping settles it unless the swell keeps driving it through the mouth.
    - **Splashes** (`ZoneFlowSim.Agitate`, once per tick after the substeps; up to 512 go to the ocean renderer as `WaterSplash`, binding 12, and `ocean_spray_splash.comp` throws spray from them into the breaker spray ring):
      - **Wall:** a wet cell (≥ `SplashDepth` 0.15 m) whose velocity into a side is over `SplashSpeed` (0.4 m/s), where that side's bed stands `WallHeight` (0.5 m) over the surface: water that can only go up the wall. A beach it would run up instead. 30 droplets/s per metre of wall per m/s over 0.4 m/s, launched up the wall at 1.5–3.6× the speed and back off it.
      - **Pour:** water arriving in a wet cell from a neighbour whose surface stands `PourDrop` (0.5 m) or more above it, with at least `PourDischarge` (0.05 m²/s per metre of lip): a waterfall's foot. Impact speed √(2 g fall), up to 12 m/s. 10 particles/s per m²/s of discharge per m/s of fall, at most 4 per splash a frame: a crown of droplets thrown up and out, and a quarter of them mist that hangs over the plunge pool.
      - Particles now carry their own motion (`SprayParticle.motion`: drag, share of gravity, growth, opacity). Droplets keep the breaker spray's (1.2/s, falling, growing 0.6 m/s, opaque). Mist drags 3/s, feels a tenth of gravity, grows 0.25 m/s and is a third as opaque. Spray under cover has no sun.
    - **Turbulence** (`ZoneFlowSim.Agitate`): a per-cell 0..1 field of how churned the water is. It is raised by pours (by fall and discharge), wall slams and supercritical flow (the foam's Froude rule), at `TurbulenceBuild` 2/s at full churn. It is carried with the flow (semi-Lagrangian), spread by its eddies (`TurbulenceSpread` 6 m²/s) and dies out at `TurbulenceDecay` 0.08/s (1/e in 12 s), so a plunge pool stays rough for a while after the pour stops.
    - **Zone flow on the GPU** (`PackFlow`, `OceanRenderer.WriteZoneFlow`, binding 13): each tick the sim's own 1 m cells go up as half (surface drift x, z) and half (turbulence), up to 32,768 cells (bigger goes up empty). The drift is the sim's depth-averaged velocity plus a plunge pool's **boil**: churned water wells up where it is most turbulent and spreads over the surface, `BoilSpeed` 8 m/s per unit of turbulence per metre down its gradient, capped at 3 m/s. The depth average alone is nearly still in a deep pool, so foam sat where it landed.
    - **What the ocean draws with it** (`oceanFinish`): in zones the surface's flow (`vFlow`) is the drift plus **eddies**, a slowly turning swirl of up to 1.2 m/s in fully turbulent water (4 m across). The ripples and all the foam lace are flow-mapped by it. Turbulence raises each cascade's share to at least 0.2, 0.8, 1.2 of the open sea's (`TURBULENCE_SHARE`, wind or not, cover or not), and adds lace over the water (`TURBULENT_FOAM` 0.45 coverage) that churns as fast as the water is turbulent.
    - **Checked in game** (`--cave-test pool`, the channel pouring into the pit): up to 82 pour splashes a tick at about 7 m/s, turbulence 1.0 at the foot. A debug view colouring zone water by drift (red) and turbulence (green) showed the whole pool turbulent while the channel runs fast. Turbulence fell from 1.0 to 0.58, 0.30, 0.14 and 0.08 over the 25 s after the pour slackened. Foam in the pool now drifts: matching foam masks between frames 0.3 s apart, the best shift is 12–15 px (zero before the boil and eddies).
    - **Cost:** `Agitate` plus `PackFlow` 0.03 ms a tick at 3,400 cells, 0.095 ms at 7,300. GPU (`--surf-test` bench, no zone in view): *Ocean surface* +0.001 ms, *Ocean patch* +0.001 ms; the spray update is unchanged at 48 bytes a particle.
  - **3D automaton:** zone cells are clamped sea tagged `SeaZone`. The automaton sleeps there and only handles water outside the zones.
  - **Measured:** about 0.15 ms per tick for the seaside hole before growth (~1,600 cells, 4 substeps).
    - With growth, the hole run settles at 24 discs and 4,756 cells within about 12 s, then stays there.
    - Zone cost is about 0.65 ms per tick in Release.
    - The 13 m pit is still rising slowly at 60 s (−0.64 m), so the grown discs have not yet gone calm enough to remerge.
- **Rate:** `StepsPerSecond` = 30, run inside the fixed tick.

### Not in v1

Coupling the bathymetry and sea mask to sim water, buoyancy against sim water, swell-driven nearshore cells, and persistence of sim water across block eviction (blocks are evicted beyond a radius and their water is lost).

## v1 as built: deviations and measurements

- **Reservoir pressure:** reservoir cells hold `1 + Compression·depth` below the sea top, not 1. At rest the compression rule fills deep water past 1, so a sea held at exactly 1 would pull water back out of pits. With the fix, pits level with the sea.
- **Mass-safe snap:** a cell that would keep less than `SnapEpsilon` sends everything, and horizontal transfers below `SnapEpsilon` are dropped. Mass drift is now about 1e-6 per step (float rounding).
- **Seams:** horizontal pairs across an X/Z block face at y = 0 always count as supported. The transfer is still bit-identical from both sides, which a test checks against a single-block run.
- **Clamped blocks never step:** a block that is all sea or solid never steps. A sleeping neighbour isn't woken across a face that is all sea or solid. This took 28 awake blocks down to about 8.
- **Sea zone around edits:** each edit adds a disc (radius = brush radius + `SeaZoneMargin`, 6 m).
  - Inside the disc, open cells are **unclamped and tagged `SeaZone`**: sea becomes ordinary simulated water at sea pressure, so it drains into the hole. Clamped sea outside the disc is the source that refills it.
  - **Rendering is one surface:** the ocean renderer draws the zone with the same swell, FFT and foam, displaced by the sim's **level offset** (sim level − rest level, where rest is sea level − 0.5 voxel). The bathymetry map is RG16F: R = water depth (NotSea where dry), G = level offset.
  - **Dry zone cells** store an offset just below their ground, so the interpolated surface dives under the terrain rather than standing up as a wall. (Superseded: dry cells now store a surface no higher than their wet neighbours, see "Sea caves and walls".)
  - The voxel-water mesher skips `SeaZone` columns. It only draws sim water that has spread outside every disc.
  - **Physics contract (for buoyancy, not wired yet):** surface = swell (damped by local depth) + sim level offset. CPU and GPU see the same thing.
  - Level offsets are rebuilt every sim tick and uploaded every tick, only the rectangle of cells that changed. The depth map keeps the previous tick in a second layer, and the ocean shaders blend the two by the tick alpha, so the 60 Hz sim moves smoothly at any frame rate.
  - **Bathymetry uploads (`BathymetryTexture`):**
    - Only what changed goes up: the regions the grid re-merged (`ChangedRegions`, trimmed to cells that really differ), plus the zone cells that moved this tick.
    - When the grid re-centres, the texture's contents move on the GPU, copied into a scratch image and back at the offset. Only the strips that came in are filled.
    - Layer 1 differs from layer 0 only on zone cells, so terrain regions are filled once and copied into both layers. The CPU mirrors track zone cells only.
    - The shoreline reach (below-sea-level dry cells near the sea) moves with the grid, and is re-flooded only around changed regions: flood the region grown by 8 cells, keep it grown by 4.
    - Before, every bathymetry rebuild re-uploaded the whole map: 4–9 ms of per-cell half-float fills and a whole-grid flood. A parallel fill was tried and dropped: the job workers' wake-up latency (around the 15.6 ms timer tick) made it slower, 7–14 ms.
- **Rate:** 240 steps/s by default (`--water-rate`), with at most 8 substeps per tick and remeshing once per tick.
  - The automaton spreads diffusively, so filling time grows with distance squared.
  - Measured in Release: about 0.3–0.9 ms per step with 8–26 awake blocks.
  - The seaside hole (r = 9 m) visibly fills and the local sea drops within about 15 s.
- **Edit hitches (fixed):** digging next to the sea used to drop frames to 84–183 ms. Measured with per-section timing on `--seaside-test`, the causes and fixes were:
  - **Worker wake-up:** waiting on job batches from the tick cost 10–70 ms spikes while other schedulers were busy. The work itself was tiny (16 awake blocks).
    - Passes with up to `InlineBlocks` (24) awake blocks now run inline on the main thread.
    - Surface meshing is asynchronous: double-buffered meshes on their own scheduler, swapped on a later tick. Blocks being meshed are not stepped, refreshed or evicted.
  - **Catch-up spiral:** automaton steps are budgeted at `StepBudgetMs` (2 ms) per tick, and the backlog is dropped, so the clock no longer spirals.
  - **Scheduler priorities:** terrain streaming, water activation and the coastal build run below normal priority; coastal uses a quarter of the cores. Water steps run above normal.
  - **Bathymetry:** rebuilds are partial.
    - Re-meshed leaves dirty only the columns that changed (edits remesh coarse leaves too, up to 512 m wide).
    - New or retired leaves dirty their footprint; up to 16 regions are kept, then folded into a bounding box.
    - Leaves with no surface or sea layer are skipped.
    - A full rebuild dropped from about 10 to about 5 ms, and an edit's rebuild to about 0.1 ms.
  - **Place edits:** only digs open sea zones now; placing material displaces zone water instead of lifting it.
  - **After:** the edit frame is about 15 ms, the worst tick after it about 10 ms (once), and then ticks run 4–5 ms (water step about 2 ms inline plus zone about 1 ms).
- **Shoreline:** the bathymetry map's R channel is now signed (sea level − ground), negative on dry land. Pits cut off from the sea store `Pit` (−1000) and are always discarded.
  - The ocean fragment shader fades by real water thickness, surface height + R, instead of the static depth.
  - So the water's edge is where the moving surface meets the beach, with shore foam on the thin swash.
  - Before, dry columns were a flat "not sea" flag. The surface was cut at a fixed mask line, and from a low side view it ended in mid-air above the sand.
  - The edge outline still follows the 2 m bathymetry grid, so it can look slightly faceted up close. It is anti-aliased by pixel coverage, and the swash takes the beach slope over 12 m so its edge no longer steps per cell (`docs/breaking-waves.md`, thirty-fifth pass).
  - **Shore reach:** the voxel sea flag reads the sea-level voxel layer (−1 to 0 m). Where the ground sits just under the surface, that voxel is mostly solid, so the shoreline band read as dry and was discarded as a pit. That left stepped holes, seen in a low side view as dark seabed through the water.
    - A below-sea-level column now counts as sea when it is within 4 cells (8 m) of open sea through other below-sea-level columns. This is a breadth-first search in `BathymetryTexture`, rerun only when the bathymetry version changes.
    - Pits further inland stay discarded.
  - **Pit rule:** a dry column only counts as a pit if its ground lies deeper than one voxel of the leaf its sea flag came from (`SeaVoxel(lod)`). Ground inside the sea-level voxel can't hold a sea voxel at all.
  - **Tops:** each 2 m cell's ground height comes from the finest leaf with a surface in that column (max within that LOD). Previously it was the max over all leaves, so a coarse leaf stacked above could override the real seabed.
  - **Flat beaches:** the test beach shelves from −0.94 to 0 m over 16 m, and a raycast confirmed the bathymetry matches the ground. Two changes make that shelf read as water:
    - Open water is opaque by 15 cm of thickness (it was 50 cm). Sea zones keep the old soft fade.
    - Shallow water's body colour fades to the sand seen through the water column (terrain.frag's albedo and absorption) below 2.5 m of thickness.
    - Before, that shelf looked like bare sand with a dark band at its outer edge.
- **Known gaps:**
  - Edit discs never remerge. The pit's water would vanish, because edits don't add sea voxels yet.
  - Growth discs overlap heavily (24 discs over ~5k cells). Merging them into a mask would be cheaper.
  - The zone sim is a heightfield: overhangs and caves inside a disc are not represented.
  - Water leaving a zone into the 3D automaton isn't exchanged in mass yet: zone cells act as clamped sea for it.

## Flow simulation v2: 3D virtual pipes (the default; `--water-automaton` for v1)

The automaton has no momentum: water spreads diffusively, a fill's time grows with the distance squared (the seaside pit was still rising at 60 s), and nothing sloshes, overshoots or runs. v2 replaces it with the virtual pipe model the sea-zone sim uses, in 3D on the same blocks, so voxel water pours, fills and runs with momentum wherever it is: a breached pond, a lake on a cliff, a flooded cave, later a hull (`WaterPipes`, `src/Terrain/Water/WaterPipes.cs`). It was behind `--water-pipes` until judged in game; since 2026-10-27 it is the default.

**Status (2026-10-27, branch `voxel-water-3d`):** built and unit-tested (`WaterPipeTests`, at 240 and at 60 steps a second), judged in game against the automaton with `--pond-test` and the lake scenes. It settles, pours and conserves mass; its breach drains at about weir pace, its run-off runs down the hillside as one steady stream, and only it gives the renderer flow, foam and spray. **It is the default**; `--water-automaton` runs the old automaton.

### State (per `WaterBlock`, the automaton's layout)

- `Water` fill per cell, as now (1 = full). Over 1 is pressure (below).
- A signed flux per face, m³/s, positive from the lower-index cell to the higher: `FluxX[i]` between i and i + 1, `FluxY[i]` between i and the cell above, `FluxZ[i]` between i and i + `PaddedArea`. The flux is the momentum: it persists from step to step and only the head difference, drag and the mass limit change it.
- `FallSpeed` per cell (m/s, down negative): how fast its water falls, carried with the water from cell to cell; 0 for water resting on a floor.
- `DriftX`, `DriftZ` per cell (m/s): the sideways speed of falling water, carried with it like the fall speed; 0 for water held up, whose sideways momentum lives on its faces.
- `Bed` per cell (both models, `sbyte`, 1/254 m): for an open cell standing on solid, how far the terrain's smooth surface lies above the cell's voxel floor (y − 0.5), −0.5..0.5 m. It is read from the density's zero crossing between the two samples in `ReadSolids`. `NotBed` elsewhere.
- A block's faces on its −X, −Y and −Z sides are held in the halo cells' slots, its +X, +Y, +Z faces in its border cells'. A face shared by two blocks is held by both and computed identically by both, as the automaton's transfers are, so no fluxes are exchanged per step. The fall speeds and drifts of the neighbours' border cells are copied into the side and top halo planes each step (`WaterHalo.CopyFalling`); the beds go with the flags (`WaterHalo.CopyFlags`).

### Head

- A cell's head (m, relative to its centre) is its free surface while it has room, `W − 0.5`, and over full the pressure its overfill stands for, `0.5 + (W − 1) / κ` with κ = `WaterFlow.Compression` (0.02 per metre of water above). That is the automaton's and the reservoirs' equilibrium (`WaterBlock.ReservoirFill`), so a column at rest has the same fills in both models and pits level with the sea in both.
- The head is a function of the cell alone, so pressure needs no column scan: it travels through full cells as a weakly compressible pressure wave at √(g / κ) ≈ 22 m/s, which is what makes communicating vessels, a pit filled from below through a pipe, and a breach at the foot of a dam spurt.
- **Sideways, water stands on the terrain, not on the voxels** (`SideHead`). The voxels turn a slope into flat terraces and one-cell stairs. A film on a terrace felt no slope, only its own depth, and dammed behind each stair until it was deep enough to spill. Two corrections:
  - On a bed, the head gains the bed's rise, faded out as the cell fills: `head + Bed · (1 − min(W, 1))`. A film feels the slope the stairs stand for; full cells and the layers over them keep their heads, so a lake's bottom layer is not pushed around by its bed.
  - A cell with room over a bed cell with room is falling and has no pressure of its own. It reads as the surface it lands on one layer down: `head(below) + rise(below) − 1 + W`. A film then runs over a stair's edge. Over a deeper drop (a cliff, a pit) a falling cell keeps its own head, so a brink is not driven by the drop's height.

### Step (fixed dt = 1 / `StepsPerSecond`)

1. **Fluxes** (pass 1, per awake block, needs fresh `Water` halos and the fall speeds above): each face touching the interior, once.
   - Sideways: `f ← f · drag + dt · g · A · Δhead`, `Δhead = head(lo) − head(hi)` (sideways heads, above). The face area A is the upwind cell's water, at most 1, and 0 unless the upwind cell is supported (solid or ≥ 0.99 full below), the automaton's rule: water in free fall has no pressure to spread with. Faces in the bottom layer across an X or Z block face count as supported, as in the automaton (their "below" is an edge halo cell no block carries), and stand on a bed by the cells' `Bed`.
   - Landing: water falling into either cell from above (fall rate `−v · min(W, 1)` of the cell over it) brings its drift's momentum onto the face it moves toward, only into water held up. Without it a sheet lost all its speed at each stair, the landing cell had to fill nearly full before its own outflow got going, and every stair pooled and let go a bore. Rows are checked for falling water first, so the cost is paid only under it. A face across a block face in the top row has edge halo cells over it, so it takes none.
   - Vertically, over a full cell (≥ 0.99), the same with `Δhead = head(lo) − 1 − head(hi)` and A = 1: pressure, rising and sinking water.
   - Vertically, over a cell with room, the water above is falling into space and crosses at its own fall speed: `f = FallSpeed(hi) · min(W(hi), 1)`. A falling speck too small to move at that speed in a step drops whole, or it would hang in the air.
   - Drag: the background `DampingRate` (0.2/s, the zone sim's) on every sideways face, and over a bed (the upwind cell's floor is solid) implicit Manning friction, deceleration g n² u² / h^⁴⁄₃ with `BedRoughness` n = 0.1, natural ground under overland flow. With the zone sim's sand-bed drag (Cd 0.01) a sheet on a 20 % slope ran at Froude 4 and its roll waves grew down the stairs until it emptied between surges; at n = 0.06 they still grew past 15 m. Under more water the layer is the bottom of a deeper flow, so the friction blends to `DeepBedDrag` (Cd 0.01) as the cell above fills: braking only the bottom layer as a 1 m film let the layers over it race ahead, and a lidded pit filling from the sea sloshed for 80 s. The cube root is a bit trick and one Newton step (`MathF.Cbrt` cost 48 µs on a busy block). Vertical faces over full water damp at `PressureDampingRate` (6/s): water is incompressible, so the overfill that stands for its pressure should settle, not ring. At 0.2/s a 3 m pond rang at about 4 Hz and never slept. At 20/s the dam break's surge lost a quarter of its height (4.27 → 3.10 m); 6/s keeps 3.75 m. Damping sideways faces carrying overfilled water the same way flattened the dam break to 1.08 m, so they keep 0.2/s.
   - Faces touching a solid, and faces between two dry cells, hold no flux. So a row that is dry with its four neighbouring rows is skipped, as in the automaton.
   - Then each interior cell's outflow limit `OutScale`, the automaton's rule: a cell that would keep less than `SnapEpsilon` sends all it has.
2. **Apply** (pass 2, needs fresh `OutScale` halos):
   - Each face is scaled once by its source cell's limit and stored back scaled: the limited flux is the momentum carried on.
   - A face moves water only when it would move at least `SnapEpsilon` in the step; below that its momentum stays on the face and builds until it does. Zeroing it instead left a 12 cm head dead zone between cells: a settled pool stayed 9 cm uneven across 8 m.
   - `W += dt · (Σ in − Σ out)`. Reservoir cells are held at their fill (the sea as source and sink), solids at 0. There is no snap: a speck left behind can't spread (its transfers are under the minimum) and drops whole when it falls, while snapping specks lost 0.01 m³ of 1872 over 10 s.
   - Fall speed: a cell held up by full water falls as fast as its floor carries it (so a column that starts to fall keeps its speed); a cell with room under it mixes the water it kept and the water that fell in from above by volume and gains g dt; at most 30 m/s. A pipe model carries flow across a face but not from one face to the next, so without this every cell restarts the fall from rest (27 m in about 12 s, against 2.3 s of free fall). Carrying the face flux instead of the speed drained a falling blob down a cell-a-step tube into the floor in 0.43 s.
   - Drift: a falling cell mixes the drift of the water it kept, the water that fell in and the water that flowed in sideways (at that face's flux over its source's water), by volume, at most 10 m/s; water held up has none. Dry rows keep stale drifts: they are read only under falling water, whose fall speeds are cleared.
3. **Swap**, as the automaton.

Mass is exact as in the automaton (each face is one value both cells read). Stable for √(g / κ) dt ≲ 0.58 m (the 3D pressure wave): 1 / 240 s gives 0.09, 1 / 60 s 0.37. The pipe model runs at 60 steps a second by default (`--water-rate` overrides it); the automaton keeps 240.

### Sleep and wake

- A block sleeps after `QuietStepsToSleep` steps with its fill changes under `SleepDelta` **and** no face moving more than `SleepDelta` of a cell in a step (the moved flux × dt): a steady stream through a block changes no fill and must not sleep, while momentum too small to move water lingers on faces over head differences of a tenth of a millimetre. Going to sleep zeroes its fluxes and fall speeds. A fixed 1e-4 m³/s instead kept the `--pond-test` pond awake for over a minute on millimetre sloshing.
- An awake block wakes a sleeping neighbour it shares water with, but no longer resets its quiet count: only the neighbour's own changes do. Resetting it, two quiet blocks sharing a pond took turns waking each other and neither slept (in both models).
- Meshing skips a block with a refresh queued. An awake block remeshed every few ticks, and its refresh, which waits for the mesh job, could be starved for good.
- The tests settle by the same rule as the runtime at its 60 steps a second (fill and moved flux under 6e-3 a second, for a second). They used to take 60 steps at 240 a second under 1e-4 a step, a quarter of the time at four times the rate, and caught a 1.5 cm seiche at its turning point.
- A block that steps for the first time after sleeping, activating or refreshing takes the fluxes of the faces it shares with blocks that stepped the step before, and zeroes those it shares with blocks that didn't (they hold none), so the two copies agree from then on (`WaterHalo.SyncFluxes`).

### Measured (unit tests, Release, 2026-10-26)

| Test | Pipe model | Automaton |
| --- | --- | --- |
| A blob falling 27 m: half of it on the floor | 2.33 s (free fall 2.35 s; 2.37 s at 60 steps/s) | no fall speed |
| U-tube legs | 4.979 / 4.967 m after 62 s | 4.982 / 4.964 m after 15,000 steps |
| Pit beside the sea, mean fill after 40 s | 1.070, far top cell 1.000 | levels after 8,000 steps |
| Dam break, peak at the far wall | 4.23 m over a settled 2.67 m | monotone |
| 32 m³ falling onto an 8 × 8 floor | level to 0.5003–0.5004, asleep after 55 s | |
| 10 × 10 × 3 m pond: quiet, then 10 s through a 2 m breach | quiet after 2.6 s; 214 of 300 m³ left | |
| Breach over a broad crest 4 m wide, 1 m of head | 4.83 m³/s, 73 % of smooth-crest weir flow (6.64) | |
| Sheet fed 0.3 m deep down a smooth 20 % slope (voxel stairs) | front 24 m down at 12.8 s; depth steady to 1 % | |
| Seam against a single block | bit for bit | bit for bit |
| Step, empty block | 9 µs | 5.4 µs |
| Step, busy block (dam break) | about 525 µs (scalar; 387 µs before the bed, landing and friction work) | 65 µs (AVX2) |

A busy block costs about 8 times the automaton's step, but needs a quarter of the steps: per second about 2 times.

Run-off over slopes 0.1–0.5 and springs 0.1–0.6 m deep (30 s, at 60 and 240 steps a second, n = 0.1): the depth swings under 10 % once the front has passed, except for torrents, a 0.6 m spring on 35–50 % slopes (59–95 %). Steep, deep flows do form roll waves, and without momentum advection (below) the model forms them more readily than water does. At n = 0.06 most of the sweep surged.

### In game: `--pond-test` (2026-10-26)

`--pond-test` (`src/Game/PondTest.cs`) digs a 7 m bowl into the hillside above the west beach (lowest ground from 7 m up on a gentle slope; this seed: (−198, 10.9, 0)), fills it with `Fill` to 0.8 m under its lowest rim (y 4.78, 50 m³) at t+4 s, then at t+10 s cuts a channel from inside the downhill rim until it opens onto lower ground (4 cuts here). The cut's lowest point is 1.5 m under the water line, but voxelised its floor is the cell under it: about 1 m of head over a channel about 3 cells wide. The camera looks back up the run-off path from over the beach. It logs the voxel sim every 0.5 s with the water depth down that path. Add `--water-pipes` for the pipe model and `--hide-ui` for clean captures. The pond is above sea level, so no sea zone starts and the voxel model alone moves its water.

| | Pipe model, with beds, landing and Manning friction | Pipe model, first run | Automaton (240 steps/s) |
| --- | --- | --- | --- |
| Pond at rest, before the breach | awake for the full 6 s on millimetre sloshing | the same | asleep within 1 s |
| Pond centre depth after the breach (2.02 m to start) | 1.89, 1.75, 1.63, 1.58 m at +1.5, 4.5, 7.5, 10.5 s; 1.44 m at +25 s | 1.86, 1.63, 1.37, 1.26 m; 1.10 m at +30 s | 1.51, 1.14, 1.04, 1.01 m; 1.00 m and asleep at +14.5 s |
| Run-off | one steady stream, 0.1–0.2 m deep down the path; 28 m at +25 s | surges at 1–2 m/s that emptied between them; 28 m at +19.5 s; patches and holes | a thin sheet to 32 m within 1.5 s, into the sea soon after |
| Run-off region, frames changing a pixel by more than 40 of 255 | 0.01–0.05 % a frame while it runs | 0.17–1.67 % | |
| Blocks / awake / step | 32 / 2–3 / 0.13–0.45 ms | 32 / 2–5 / 0.09–0.31 ms | 32 → 40 / up to 12 / 0.19–0.50 ms |

Judged in motion (200 ms bursts, `--hide-ui`): the pipe model reads as water with weight. The breach forms a stream that keeps running onto the beach as one smooth tongue, with no holes or scattered patches. The breach was never slow. Weir flow over about 1 m of head and a 3 m gap is a few m³/s. The pipe model passes 73 % of a smooth crest's weir flow (`BreachPassesWeirFlow`), the bed's roughness costing the rest. The first run passed about a smooth crest's. The automaton drained the pond to the channel's floor in a third of the time, with nothing but its transfer rate to limit it: the pond slumps out in one sheet and is gone. The first run's run-off broke into patches for two reasons. Its slope existed only as one-cell drops between flat terraces, so each terrace had to refill before it spilled. And each film was drawn on its voxel floor, under the smooth terrain wherever the slope rose above that floor. The mesher now draws a film on the true bed too (`WaterBlock.BedRise`, both models).

### Not yet

- **No momentum advection:** a face's flux changes only by head, drag and landing water, not by the momentum its neighbours carry in, d(q² / h) / dx. Shallow water without that term forms roll waves at about a third of the Froude number real water does, hence the n = 0.1 roughness. Per-layer advection was tried, with each cell's W u² differenced across the face: it halved the weir flow (2.96 against 6.68 m³/s) and slowed the pond's drain. Water rising out of a deep reservoir's lower layers carries no sideways momentum up with it, and the layer model has no term that would carry it, so the top layer was braked as it sped up onto the crest.
- **Jets:** falling water carries its drift and hands it on when it lands, but does not move sideways while it falls: a jet leaving a ledge still falls straight down rather than flying out.
- **One roughness:** n = 0.1 everywhere. Rock-cut channels run near 0.035, sand 0.02, grass 0.1–0.4: a bed's roughness should come from its voxel material.
- **Sideways-fed falling cells:** a falling cell fed sideways never empties, so its fall speed builds to the 30 m/s cap and its water drops through it at once. Over a stair that helps (the brink passes the sheet), but the water under a waterfall's lip falls faster than free fall.
- **Light seiche damping:** a pond at rest sloshes at millimetres for several seconds before it sleeps (only the 0.2/s background damping acts on it; the friction is quadratic).
- **SIMD:** the in-place sweep reads faces and fall speeds the cells before it updated, so lanes need a different order.
- **Specks:** without a snap, a speck stranded on a floor keeps its row (and the rows around it) from being skipped while its block is awake.
- The sea-zone coupling, persistence across eviction, and the settle rule from the plan below.
- **Memory:** each block carries six more padded float arrays (943 KB), allocated with the system's 512 pooled blocks only when `--water-pipes` is on: about 480 MB more. Pooling them for awake blocks only would remove it. `Bed` adds 39 KB a block in both models (20 MB).

### Plan after v2

1. Sea-zone bed from the 1 m voxel columns (the zone sim already runs on 1 m cells but reads the 2 m grid).
2. Sim water persisted across eviction: a sparse, compressed per-chunk record of fills that generation and ops can't reproduce, reloaded on activation, with a per-chunk mass ledger for the float ↔ byte round trip.
3. Settle: done for bodies ("Bodies of water"): water at a body's level joins it as its block sleeps, and level free water becomes a body. The sea still needs its own: connected water at sea level returning to the ocean.
4. Zone ↔ voxel exchange at disc edges, column for column.
5. Still voxel water shaded like the ocean, so a pool doesn't change look when it settles.

### Coupling

- **Terrain edits** change solids, which zeroes water and wakes the chunk and its neighbours.
- **Bathymetry and sea mask** are rebuilt from the chunk's water when it sleeps, or every few ticks while awake.
- **Buoyancy and queries** go through `OceanSurface`:
  - In a sea column, the height is swell (A + B).
  - Elsewhere, the height is the voxel water surface: the top filled voxel plus its fill fraction. Velocity comes from the face fluxes.
- **Swell to voxel water:** in the nearshore band (Layer C), sea cells take their fill level from the swell height each tick, not a flat sea level. That gives run-up and flow around islands. This is where Layer C becomes the voxel flow sim.
- **Vehicle flooding (later):** the same cell model runs in the vehicle's frame on the vehicle's own small voxel grid.

## Rendering (design)

- **Open sea:** the existing ocean renderer, masked by sea columns.
- **Other water:** surface nets on a water density field derived from the fill levels, on jobs like terrain meshing. It is shaded with the same water material and depth absorption, without swell, and with flow-direction normals scrolling for rivers and waterfalls.
- **Seams:** where sea meets voxel-water meshes, the shoreline band uses the voxel mesh. The ocean clipmap fades its shore damping (already present) so the two meet without a visible step.

## Storage cost

- v1 adds 1 byte per voxel: 39 KB per padded chunk.
- It only lives in pooled job buffers, since chunks are regenerated from generation + ops and never stored.
- Persistent water state (the flow sim's output) will be stored per chunk only when it differs from what generation + ops would produce. That keeps memory proportional to simulated water.
