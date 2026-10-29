# Water screen-space reflections

Every water surface (the ocean, the breaker lips, voxel water) reflects the opaque scene where the reflected ray meets it on screen, and the sky where it doesn't. One trace in `shadeWater` (`water_shading.glsl` → `water_ssr.glsl`), so all water reflects the same way. Visual only: nothing reads it back.

## Frame

- **Two passes** (`Renderer`): the opaque pass (terrain) clears colour and depth and leaves both in TRANSFER_SRC; the water pass loads them and draws the ocean, voxel water, rain and the UI, then presents. The passes are compatible (same attachments), so every pipeline is still made against `Renderer.RenderPass` and drawn in either.
- **The copy** (`ScreenReflections.Record`, between `EndOpaquePass` and `BeginWaterPass`, GPU scope *Reflection copy*, 0.011 ms): the swapchain image and the depth are copied into a colour image and a D32 image the water samples. So the trace sees the terrain only, never water, and the water can keep testing and writing depth as before. The copies follow the swapchain's size; a resize waits for the device and rewrites the sets.
- **Bindings**: set 1 of the ocean's and voxel water's pipeline layouts (`ScreenReflections.SetLayout`): binding 0 the uniforms (view-projection, screen size, pixels per step, on, longest ray, most steps, near plane), 1 the scene colour (linear, clamped), 2 the scene depth (nearest: depth filtered across a silhouette is a surface that isn't there). A shader opts in with `#define WATER_SSR_SET 1` before including `water_shading.glsl`; without it the water reflects the sky only (rain and spray shaders don't opt in).

## The trace (`ssrTrace`)

- The reflected ray (kept above the horizon, as the sky reflection is) runs from the fragment in **screen space**: both ends projected, the far end where the ray reaches its longest length (`MaxDistance`, 1000 m) or comes within 1.5 × the near plane; the segment clipped to the screen, so no step is spent off it. The stored depth (reverse-Z infinite: near / view depth, 0 for a cleared pixel) and the screen position are both linear along it, so the ray's depth anywhere on it is exact.
- **The march**: even steps in screen space, a step every `Stride` px (8) where the whole reflection shows and sparser as less of it does: the step count is the ray's length on screen × the share that survives (Fresnel × (1 − foam) × (1 − the rain and slope blur)) / `Stride`, rounded up to a multiple of four, at least 4 and at most `Steps` (12). A far ray spans few pixels, and a faint or blurred reflection hides what a coarse march misses, so neither pays for steps it can't show. The steps start at a per-pixel offset, four depth fetches a pass tested together (one fetch a step and a test before the next cost twice as much at the lake). A step that has gone behind the scene's depth, having been in front of it (within 3 % of its depth) a step before, is the crossing; the first in the batch that passes the ground test is refined by 5 bisections.
- **Ground under the water is not a hit**: a beach under a film or a pond's bed is behind the surface the ray left from; the scene point along that pixel's view ray must stand at the water's height or above.
- **Colour back to light**: the scene's colour is the display colour (tonemapped, gamma, fogged in display space). Its fog over the camera-to-hit distance is taken off, then gamma and the ACES fit undone (the fit's quadratic solved, the input capped at 0.98), so the reflection is mixed in linear light before the water's own tonemap and fog, as the sky's is.
- **Weight** (how much of the sky the hit replaces): faded over the last 6 % of the screen at the edges, over the last fifth of the ray's length, where the hit was mostly fog (its un-fogging would amplify noise), and by the air between the water and the hit (exp(−travel × fog density), toward the sky the water would show there). Rain and slope roughness then blur the result toward the sky's average as they blurred the sky alone.
- **Not traced** where the reflection's share (Fresnel × (1 − foam)) is under `SSR_MIN_SHARE` (0.025; 0.02 is Fresnel head on), faded in over 0.025–0.05, and **not under water** (the underside mirrors the water's own glow, as before).

## Controls

- Terrain panel (F5), *Water reflections*: on/off, most steps, pixels per step, longest ray.
- `--no-ssr` starts with it off; `--bench-ssr [s]` alternates it on/off in 2 s blocks like `--bench-fog`, then closes. Off skips the copy as well as the trace.

## Measured (2026-10-28, Release, RTX 4090, 1600×900, `--wind 14 --bench-ssr 12`, on against off, medians)

| View | Version | Frame | Delta | Water scope delta |
| --- | --- | --- | --- | --- |
| Start camera (sea) | one fetch a step | 0.271 / 0.214 | +0.056 | Ocean surface +0.059 (0.093 → 0.153) |
| Start camera (sea) | batched | 0.266 / 0.213 | +0.053 | Ocean surface +0.058 |
| Lake, `--cam -240 100 0 0 -12` | one fetch a step | 0.239 / 0.163 | +0.077 | Voxel water +0.056 (0.049 → 0.106) |
| Lake | batched | 0.205 / 0.162 | +0.043 | Voxel water +0.027 |
| Start camera (sea) | Hi-Z, GPU shared with a game | 0.336 / 0.277 | +0.059 | Ocean surface +0.047 (0.126 → 0.173); *Reflection copy* 0.035 |
| Lake | Hi-Z, GPU shared with a game | 0.407 / 0.289 | +0.119 | Voxel water +0.084 (0.091 → 0.175); *Reflection copy* 0.047 |

| Start camera (sea) | batched, after the Hi-Z revert | 0.263 / 0.213 | +0.051 | Ocean surface +0.057; *Reflection copy* 0.011 |
| Lake | batched, after the Hi-Z revert | 0.205 / 0.163 | +0.043 | Voxel water +0.028; *Reflection copy* 0.011 |
| Start camera (sea) | steps by what shows (4 px) | 0.244 / 0.211 | +0.033 | Ocean surface +0.039; *Reflection copy* 0.011 |
| Lake | steps by what shows (4 px) | 0.203 / 0.162 | +0.042 | Voxel water +0.027; *Reflection copy* 0.011 |

- *Reflection copy* was 0.011 ms in every run of the plain copy.

**Hi-Z against the batched march, idle GPU** (one build with the pyramid; the march swapped in through the bin's shader copy, reading the pyramid's level 0 at 24 steps; run A B A B, both runs of each within 0.003 ms):

| View | Batched march | Hi-Z walk |
| --- | --- | --- |
| Start camera (sea), ocean surface delta | +0.050 | +0.047 / +0.048 |
| Lake, voxel water delta | +0.0225 | +0.0553 |
| *Reflection copy* (colour copy + pyramid) | 0.032 | 0.032 |

- **Hi-Z lost, and was reverted.** At the sea it is no cheaper than the march (the sea's rays mostly meet the island rather than open sky, so there is little empty space to skip), at the lake it costs 2.5 × (the crater wall is a few dozen pixels out: the march finds it in a batch or two, the walk climbs and descends one dependent fetch at a time), and the pyramid's build costs 0.032 ms against the 0.011 of the depth copy it replaced. With the plain copy back, the batched march measures +0.051 ms of frame at the sea and +0.043 at the lake (table above).
- **Look** (captures on and off, 2026-10-28): at the lake the crater's far rim shows in the water along the far shore, the strongest change on screen; elsewhere the lake still shows the sky. At the sea the changes are lost in the wave motion between the two runs; a same-time A/B is still to take.

## Next

- **Halved for the frame budget** (2026-10-29): `Steps` 24 → 12 and `Stride` 4 → 8 px. At the lake (`--bench 12 --bench-ssr --cam 178 76 0 180 -10`, RTX 4090, one run each) the reflections cost +0.036 ms against +0.063 (*Voxel water* 0.107 against 0.134 on, 0.072 off; frame 0.248 against 0.276). Thin features fall between steps more often and more rays reach the sky; not yet judged in motion.

- Steps by what shows took the sea from +0.057 to +0.039 ms (4 steps everywhere was +0.011); the lake's rays end at the near crater wall, so it barely changed. Not yet judged in motion against fixed steps: if crests flicker where the step count changes, raise the floor or smooth `shown`.

## Known gaps

- **Water doesn't reflect water**: the copy is taken before any water, so a lake doesn't show the ocean or a waterfall in it.
- **Off-screen and hidden things** fall back to the sky: anything behind the camera, past the screen's edge, or behind a nearer hill.
- **Thin features can be stepped over**: 12 steps across up to the whole screen leave gaps a post or a thin ridge fits in.
- **No temporal filtering**: rippled water scatters the rays per pixel; the noise is the ripples', fixed per pixel, but it is not smoothed.
