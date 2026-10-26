# How waves form, what makes them surfable, and what that means for our ocean

Research notes behind the water design (`water-architecture.md`, `breaking-waves.md`): the physics, the numbers worth matching, and where our model stands against them.

## 1. Wind makes waves

**Starting from flat water.**
- Turbulent pressure fluctuations in the wind push on the surface. Where they move at the speed of a possible wave, they resonate with it, and ripples grow linearly in time (Phillips, 1957).
- Once waves exist, the airflow over them becomes unstable at the height where the wind speed equals the wave's speed (the critical layer). The pressure there falls out of phase with the crest and feeds the wave, so growth becomes exponential (Miles, 1957).
- Phillips dominates the first moments and Miles the rest; laboratory work has since measured the critical-layer growth directly (Carpenter et al., 2022).

**What limits a sea.** Three things set how big and long a wind sea gets:
- **Wind speed.**
- **Fetch:** the distance of open water the wind blows over.
- **Duration:** how long it has blown.

Growth laws are written in dimensionless fetch g·X/U². Peak frequency, the spectrum's level (α ≈ 0.076·X̃^−0.22) and its peakedness all scale with it. The JONSWAP spectrum (Hasselmann et al., 1973) has a mean peak enhancement γ = 3.3 and widths σ = 0.07 below the peak and 0.09 above. A sea that has had enough fetch and time stops growing: it is fully developed for that wind.

**The short waves saturate.** Their slopes hardly grow with wind (Cox & Munk measured mean-square slope rising only linearly with wind speed). What grows strongly with wind is breaking of the larger wind-sea waves, which is why whitecaps (section 5) cannot be read off small-scale surface compression alone.

## 2. Wind sea and swell

**Swell is wind sea that has left its storm.**
- Wave speed rises with period: deep-water group speed is c_g = g·T / 4π, about 0.78 T m/s. So waves sort themselves as they travel, and the long-period ones arrive first.
- What reaches a distant coast is a narrow band of long, smooth, organised waves.
- Period is the usual label, with blurry cutoffs: **windswell** under about 10 s (choppy, local), **groundswell** about 10–20 s (from a far storm). A storm holding strong winds over a long fetch for a day or two makes the best of it.

**Sets** are wave groups.
- A narrow band of frequencies beats into groups as neighbouring frequencies drift in and out of phase, and two swells of nearly the same period arriving together make strong sets.
- The narrower the band, the longer and more distinct the groups, so long-period swell has longer waits between bigger sets.
- "Every seventh wave" is folklore: the number of waves in a group is random.

## 3. Waves reaching the shore

- **Shoaling:** as depth falls below about half a wavelength, waves slow, shorten and grow taller.
- **Refraction:** crests bend to follow the depth contours, so long-period swell feels the bottom earlier and is bent and focused more by reefs, points and canyons.
- **Depth-limited breaking:** a wave breaks when its height reaches about 0.78 of the water depth (the McCowan limit).

**Breaker type** follows the surf similarity (Iribarren) number ξ₀ = tan β / √(H₀/L₀): bottom slope against the deep-water wave's steepness.

| Breaker | ξ₀ (deep water) | ξ_b (at the break) | What it looks like |
| --- | --- | --- | --- |
| Spilling | < 0.5 | < 0.4 | Whitewater tumbles down the face; no tube |
| Plunging | 0.5–3.3 | 0.4–2.0 | Lip throws forward and lands, leaving a tube |
| Collapsing / surging | > 3.3 | > 2.0 | Face collapses or surges up the beach without breaking |

So steep bottoms and long, low swell plunge, while gentle slopes and short, steep wind sea spill. The thresholds are a guide with real scatter, and two definitions of ξ (deep water versus at the break) are both in use.

## 4. What makes a wave surfable

**Peel angle** is the angle between the crest and the path the break point moves along: the most important factor (Walker, 1974; Hutt et al., 2001).
- **Too small** (below about 27–30°) and the wave closes out: the break runs along the crest faster than a surfer can, or the whole crest breaks at once.
- **Around 30–70°** it peels: the curl moves steadily along the crest, with an open face ahead of it to ride.
- **Peel rate:** the break point moves along the crest at V = C_b / sin α (C_b = wave speed at breaking), so smaller angles peel faster. Walker estimated top board speeds of about 12 m/s from aerial photographs.
- Bigger waves and better surfers tolerate smaller angles.

**Bathymetry sets the peel.**
- Point breaks, reefs and angled sandbars put the breaking depth at an angle to the incoming crests, so each crest starts breaking at one end and peels along.
- A straight beach under straight crests closes out.
- At a headland such as Rincon, refraction around the point controls the quality, so the same spot surfs differently with swell direction.

**Swell quality:**
- Long-period groundswell gives clean, powerful, well-spaced sets.
- Short-period windswell arrives disorganised and choppy.

**Wind:**
- **Offshore wind** delays breaking, so waves break later, in shallower water, and steeper and plunging. It also grooms the face.
- **Onshore wind** makes waves break earlier, flattens them and widens the surf zone.

Laboratory studies and simulations agree on the delay; whether offshore wind makes the tube hollower was not measured in what I found.

## 5. Whitecaps and foam

**How much foam:** the measured share of the sea covered by whitecaps is W = 3.84×10⁻⁶ · U^3.41, with U the wind speed at 10 m in m/s (Monahan & O'Muircheartaigh, 1980).

| Wind (m/s) | 8 | 10 | 16 | 20 | 25 | 30 |
| --- | --- | --- | --- | --- | --- | --- |
| Whitecap coverage | 0.5% | 1% | 5% | 10% | 22% | 42% |

It varies with sea temperature, fetch and sea state; it overestimates at high latitudes (Salisbury et al., 2014), and the exponent rises with sea temperature. But it is the standard reference.

**What it looks like by Beaufort force:**

| Force | Wind | Sea surface |
| --- | --- | --- |
| 7 | 13.9–17.1 m/s | Foam from breaking waves begins to be blown in streaks along the wind |
| 8 | 17.2–20.7 m/s | Well-marked streaks |
| 9 | 20.8–24.4 m/s | Dense streaks, spray reduces visibility |
| 10 | 24.5–28.4 m/s | Large patches in heavy streaks; the surface takes on a white appearance |
| 12 | 32.7+ m/s | Air full of foam and spray; sea completely white |

So a white sea is right only in a storm.

**How long foam lasts:**
- Whitecaps have an active stage (the breaking crest, about 0.4–5 s) and a residual stage (the foam left behind, which can make up most of the white area).
- Individual foam patches decay in 0.2–10 s, with effective (area-weighted) decay times of 1.4–4.8 s (Callaghan et al., 2012).

## 6. Where our ocean stands

**Done:**
- **Spectrum:** JONSWAP, parameterised by wind speed, fetch and duration, with depth-aware dispersion (`OceanSwell`, `Jonswap`).
- **Coastal effects:** shoaling, refraction and sheltering through the coastal maps (layer B).
- **Breaking:** depth-limited on the surf carrier, at the McCowan 0.78 (`WAVE_BREAKING_GAMMA`).
- **Groundswell:** a long-period band independent of the local wind, with its own height, period and direction (`SwellParams.SwellHeight`, `SwellPeriod`, `SwellDirection`; `--swell H T dirDeg`; Ocean panel sliders). `WaveSet.BuildGroundswell` draws it as a Gaussian in ω (σ = `SwellBandwidth` × ω_p, default 5%, sampled to ±3σ) with narrow cos-2s spreading (s = 40), scaled to give exactly the requested Hs.
  - The game starts with a 1.2 m, 13 s swell travelling toward +X. `SwellParams` defaults to none, so tests and other callers are unchanged.
  - Once the swell's height is at least half the wind sea's, it becomes the dominant band. The surf carrier, the coastal maps and the sets then follow its period and direction instead of the wind's, so light wind over a groundswell gives clean, long-period surf.
  - Cost (`--wind 14 --bench 12 --surf-test`, Release, 8 bands × 3 directions = 24 more components): frame 0.187 → 0.200 ms, ocean surface pass 0.082 → 0.093 ms. At 12 × 3 it was 0.204 ms and 0.096 ms, for no visible gain.
- **Sets:** a group envelope with height ×(1 ± 0.7) and a set every 1 / (2 × bandwidth) waves of the dominant band, within 4–20. That is every 8 waves for a wind sea and every 10 for a 5%-wide groundswell; a 3% swell sets every 17.
- **The break's progress:** a break has memory. It throws once the face stands up and lands within 0.35 wavelengths, so the curl is a pocket that peels along the crest (`breaking-waves.md`, ninth pass).
- **Whitecaps:** they now follow the Monahan curve (`ocean-gpu-contract.md`, Foam tuning). Before, they jumped from almost none to a white haze over the whole sea above about 25 m/s, as reported. Each frame the GPU seeds foam on the measured share of the surface, from the J histogram of the two larger FFT cascades, drawn as lace patches.

  | Wind (m/s) | 10 | 16 | 20 | 25 | 30 |
  | --- | --- | --- | --- | --- | --- |
  | Measured | 0.8% | 4.6% | 10.4% | 24.8% | 47.9% |
  | W(U) | 1.0% | 4.9% | 10.5% | 22.5% | 41.8% |

**Biggest gaps for surfing, roughly in order of payoff:**
1. **Peel angle in the test bar.** `--surf-test`'s bar runs at about 19° to the crests, below the 27–30° at which waves close out, so it peels too fast and reads as a tube. A bar at about 40–50° to the incoming crests would peel at about 1.3–1.5 C_b.
   - In the open world, peel comes from the terrain: a world generator that makes points, reefs and angled bars makes surf spots.
2. **Breaker type from the Iribarren number** (done, `breaking-waves.md` tenth pass): ξ_b = tan β / √(H / L₀) at each crest point, from the bed slope a quarter wavelength either side and the carrier's unbroken height, picks spilling (ξ < 0.35: whitewater down a steep front, no lip) through plunging (ξ > 0.65: the lip is thrown). The island's 1:50 beach spilled its set waves (ξ ≈ 0.4) and gently plunged the small ones (ξ ≈ 0.7); the surf-test bar's face plunges. Steepened to 1:35 on 2026-10-25 (`TerrainParams.BeachSlope`), so the sets sit at ξ ≈ 0.57, mostly plunging, and the small waves at ξ ≈ 1.0.
3. **Wind relative to the shore.** Offshore wind should delay breaking and steepen it (a higher breaking ratio, more pitch); onshore wind should break earlier and flatter, with more whitecapping in the surf. The wind field is global today; this needs its direction against the local shore normal.
4. **Whitecaps where waves break.** Whitecaps are now the right amount but placed by FFT compression alone, so they don't favour the crests of the bigger waves. Weighting the seeding by the swell's local crest steepness would put them where real breaking is. Foam should also streak along the wind from force 7 (14 m/s) up.

## Sources

- Monahan & O'Muircheartaigh (1980), whitecap coverage: [University of Galway repository](https://researchrepository.universityofgalway.ie/entities/publication/955c2fc7-e4f6-4a44-ab15-7e7073732378); context and limitations: [Goddijn-Murphy et al. 2011](https://data-ww3.ifremer.fr/BIB/Goddijn-Murphy_etal_JPO2011.pdf), [AMS abstract](https://ams.confex.com/ams/95Annual/webprogram/Paper260446.html), [Oceanologia](https://ecp.iopan.pl/oceanologia/OC_26/OC_26_19-22.pdf)
- Foam decay and whitecap stages: [Callaghan et al. 2012](https://data-ww3.ifremer.fr/BIB/Callaghan_etal_JGR2012.pdf), [Remote Sensing 2021](https://doaj.org/article/a53f540f7deb493d85d2421efbf1dfa5)
- Beaufort scale: [Wikipedia](https://en.wikipedia.org/wiki/Beaufort_scale), [MIDSIS/REMPEC](https://midsis.rempec.org/en/tools-guides/beaufort-scale), [Royal Meteorological Society](https://rmets.org/metmatters/beaufort-wind-scale)
- Wave generation: [Miles–Phillips mechanism](https://en.wikipedia.org/wiki/Miles-Phillips_mechanism), [Carpenter, Buckley & Veron 2022](https://par.nsf.gov/servlets/purl/10389423)
- Fetch-limited growth and JONSWAP: [Young & Verhagen 1996](https://data-ww3.ifremer.fr/BIB/Young_Verhagen_CE1996b.pdf), [ICCE paper](https://icce-ojs-tamu.tdl.org/icce/article/download/3056/2721/13086), [PlanetCalc](https://planetcalc.com/4442)
- Swell, period and sets: [Rapture Camps](https://www.rapturecamps.com/learn-to-surf/wave-knowledge/swell-basics), [Hawaiian South Shore](https://www.hawaiiansouthshore.com/blogs/hawaiian-south-shore-surfing-blog/swells-102-ground-swell-versus-windswell), [Surfer Today on windswell](https://www.surfertoday.com/surfing/what-is-a-wind-swell), [Surfer Today on sets](https://www.surfertoday.com/surfing/why-do-waves-come-in-sets), [Live Science on sets of seven](https://www.livescience.com/33624-waves-ocean-sets-seven.html)
- Breaker types: [Iribarren number](https://en.wikipedia.org/wiki/Iribarren_number), [SPH breaker-type table](https://arxiv.org/pdf/1810.08357), [breaker types on a 1:10 slope](https://www.doi.org/10.3390/JMSE8040296)
- Peel angle and surfability: [Swellnet, Coastal Creationism](https://swellnet.com/news/coastal-creationism/2015/10/19/coastal-creationism-part-1-basic-reef-shapes), [NOAA report](https://repository.library.noaa.gov/view/noaa/36614/noaa_36614_DS1.pdf), [University of Waikato thesis](https://researchcommons.waikato.ac.nz/bitstreams/f7cc9054-485c-4262-9902-a8817523d636/download)
- Wind and breaking: [lab study of on- and offshore wind](https://hal-univ-pau.archives-ouvertes.fr/hal-02966441), [wind-induced shoaling wave shape](https://arxiv.org/pdf/2110.05519), [JFM, cross-shore wind and overturning wave shape](https://www.cambridge.org/core/journals/journal-of-fluid-mechanics/article/crossshore-windinduced-changes-to-fieldscale-overturning-wave-shape/4236FD21F9A9EA5AFCF6ED8966B93596)
