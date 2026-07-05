# Plan 06 — Volumetric fog for 3D biomes

**Size:** M · **Depends on:** 02 (sun, shadow map) · **Replaces:** the current analytic distance fog

## Goal

Fog with *presence*: light shafts streaming through gaps at sunrise, thick
red haze pooling in hell caverns, thin cold air over snowfields — density
and color driven by the same 3D biome system that shapes the world.

## Locked decisions

- **Half-resolution raymarched volumetrics with shadow-map light shafts**
  (froxels rejected as overkill for the art style; pure analytic fog kept
  only as the low-spec fallback toggle).

## Design

### Rendering pipeline change (the enabler)

- The scene currently renders straight to the default framebuffer. Step one
  is an **offscreen HDR-ish pass**: scene → color + depth textures →
  post-process chain → composite to screen. This is deliberately built as a
  small generic `PostChain` (fog today; bloom/SSAO possible later).

### The fog pass

- Half-res target. Per pixel: reconstruct the view ray, march from camera to
  the scene depth (max ~24 steps, exponential step distribution — near
  steps finer where shafts matter).
- Per step accumulate `density(worldPos) * (ambient + sunColor * shadow)`,
  where `shadow` samples the CSM from plan 02 — occluded segments stay dark
  → **god rays** fall out naturally as the sun moves.
- **Blue-noise jitter** per pixel de-bands the march; a depth-aware
  (bilateral) upsample composites the half-res result without halos around
  geometry edges.

### Biome-driven density (the 3D-biome hook)

- Density field = `base(biomeParams) * heightFalloff(y) * hellBoost`:
  - **Biome params** per surface/cave biome: color tint, base density,
    height falloff — a small table keyed by the existing biome enums
    (constants in `Voxel.Shared` first; optionally promoted to
    `/data/biomes/*.json` later — noted as a stretch since biomes themselves
    aren't data-driven yet).
  - Sampled **CPU-side** on a coarse grid around the camera (e.g. 8×4×8
    samples over ±96 blocks, refreshed on chunk crossings) and uploaded as a
    tiny 3D texture — the shader interpolates, so walking from grassland
    into a snowfield *gradually* changes the air. Full per-pixel biome
    evaluation on GPU is rejected (the noise stack is too heavy).
  - **Hell:** below the hell boundary the field blends to dense warm red —
    replacing today's screen-space hell fog hack with true depth-aware
    murk; glowing distance haze in big caverns.
- The old analytic distance fog folds into this pass (far-distance
  extinction term) so there is exactly one fog system; LOD ring fade (04)
  keys off the same extinction curve.

### Settings

- `settings.json`: fog quality off / analytic / volumetric (step count
  scales); default volumetric.

## Milestones

1. **PostChain** — offscreen scene + trivial passthrough composite; zero
   visual change, screenshot-diff verified; HUD reports pass timings.
2. **Analytic fog in post** — current fog reproduced in the new pass
   (deletes the in-shader fog uniforms); hell override behavior preserved.
3. **Raymarch + shafts** — homogeneous fog with CSM sampling; timelapse
   screenshots show shafts rotating with the sun through terrain gaps.
4. **Quality pass** — blue-noise jitter, bilateral upsample, step tuning;
   half-res artifacts eliminated in screenshot review.
5. **Biome density field** — coarse-grid sampler + 3D texture; hell descent
   becomes the showcase (screenshot series surface → cave → hell).
6. **Perf + fallback** — ≤ 1.5 ms at 1080p half-res on the dev GPU;
   settings toggle wired.

## Verification

- Screenshot matrix: dawn shafts in a forested/overhang area, noon clear,
  hell cavern, snowfield vs desert contrast (density difference visible).
- Perf timings in the HUD stats line per pass.
- A/B: analytic vs volumetric toggle screenshots from identical camera poses
  (`--pos`/`--look` make this reproducible).

## Risks & open questions

- Shadow-map reuse means fog quality inherits shadow-map range limits —
  shafts fade beyond cascade 2; acceptable (distant fog is extinction-
  dominated anyway).
- Temporal shimmer under motion with jittered marching — if visible, add a
  cheap temporal blend (previous-frame reprojection) as milestone 4.5.
- Water: underwater volumetrics are a natural extension (blue absorption) —
  out of scope v1, noted.
- Open: should weather (rain/storm density spikes) hook in here later? The
  density field API is designed to accept a global modifier for it.
