# Plan 02 — Colored lighting, pixelated shadows, day/night cycle

**Size:** L · **Depends on:** 01 (world clock) · **Unblocks:** volumetric fog (06)

## Goal

Three intertwined systems:

1. **Colored voxel block light** — glowstone glows warm, lava orange, ice-blue
   crystals possible; colors defined per block in `/data` JSON.
2. **Real-time pixelated sun/moon shadows** that rotate as the sun and moon
   cross the sky.
3. **Day/night cycle** — 20-minute default (configurable), sky gradient,
   sun/moon rendering, ambient light curve.

## Locked decisions

- Real-time shadows required. **Evaluation of the candidates:**
  - **CSM (cascaded shadow maps)** — the proven baseline. 2–3 orthographic
    cascades following the camera, depth-only render of chunk meshes.
    "Pixelated" comes for free with the right tuning: snap the light-space
    origin to shadow-texel increments (kills crawl), size texels to a fixed
    world size (¼ block), and sample with a single hard tap (no PCF).
    Works at any view distance via cascades. **Chosen baseline.**
  - **VSM/EVSM** — filtered, *soft* shadows with light-bleeding artifacts;
    softness directly fights the crisp pixel aesthetic. **Rejected.**
  - **SDF / voxel-raymarched shadows** — normally SDF shadows need a signed
    distance field baked from meshes, but *our world is already voxels*: a
    shadow ray can DDA through a GPU occupancy bitmap (1 bit/voxel, 3D
    texture brick per chunk region) and produce perfectly crisp per-voxel
    shadows in any light direction, Teardown-style. Genuinely attractive
    here; the cost is GPU voxel-data plumbing (upload on chunk load/edit)
    and per-pixel march cost. **Timeboxed spike (milestone 7)** behind a
    settings flag — if it looks better and holds 60+ fps, it can replace CSM
    for the sun; CSM remains the fallback.
- Colored **block light is voxel flood-fill**, not shadow-mapped (dozens of
  emitters underground would melt any shadow-map approach).
- Day length: 24,000 ticks (20 min) default; `dayLengthTicks` comes from the
  server via `TimeSync` (01).

## Design

### A. Voxel colored light (block light + skylight)

- **Storage:** per chunk, `ushort[4096]` packed `R:4 G:4 B:4 Sky:4`
  (0–15 per channel, exactly MC-style ranges but per-color).
- **Block JSON:** new optional `"lightColor": "#RRGGBB"` alongside the
  existing `lightEmission` (intensity 0–15). Default white. Docs updated in
  `data-format.md`; loaders in `Voxel.Shared` parse + validate.
- **Propagation:** BFS flood per channel from emitters; −1 per block stepped;
  opaque blocks absorb; translucent (water/ice) attenuate an extra step.
  **Skylight:** top-down column fill (full sky value descends until a
  non-transparent block, then floods like block light).
- **Where computed:** client-side, on the mesher worker pool, when a chunk
  and its neighbors have data (same gating as meshing); edits relight the
  affected chunk + neighbors before remeshing. Deterministic, so no sync
  needed. *Known limit:* skylight at the top of the loaded radius assumes
  open sky — acceptable, revisit with LOD (04) heightmap hints.
- **Meshing/shader:** mesher samples the light of the air voxel each face
  faces into, per vertex (average of the 4 adjacent voxels for smooth
  lighting — also gives soft AO-ish corners). Vertex format grows by one
  RGBA (block-light RGB + skylight). Fragment:
  `albedo * (blockLight + skylight * skyColor(time) * shadow)`.

### B. Sun/moon shadows (CSM baseline)

- Depth-only pipeline: render solid chunk meshes into 2 cascades
  (near ~64 blocks at ¼-block texels, far ~192 blocks at 1-block texels),
  2048² each. Texel-snapped orthographic frusta.
- Chunk fragment shader samples the cascade (hard single tap → chunky edges),
  biased in normal + light direction to avoid acne on flat voxel faces.
- Light source = sun by day, moon by night (opposite azimuth, ~25%
  intensity, cooler color); blend to shadowless during dawn/dusk handover.
- Contraptions (03) render into the shadow pass too once they exist.

### C. Day/night cycle

- Sun direction from `WorldTimeTicks`: azimuth sweeps 360°/day on a tilted
  axis (fixed tilt ~30° so shadows always have some length).
- Sky: gradient (zenith/horizon) keyed on sun elevation — day blue → orange
  dusk → dark night; existing fog color follows the horizon color (replaces
  the current constant sky blue; hell override still wins underground).
- Sun and moon: textured quads (data-driven: `data/environment/sun.png`,
  `moon.png`); stars at night (cheap static point sprite sheet) — stretch.
- Ambient curve: skylight intensity multiplier over the day (1.0 noon →
  ~0.15 midnight) applied in the fragment shader — MC-style global darkening
  without touching stored light values.

## Milestones

1. **Clock → sky** — sun position + sky gradient + sun/moon quads driven by
   world time (no shadows yet). `tick rate 10` makes a timelapse.
2. **CSM pass** — single cascade, hard shadows rotating with the sun.
3. **Cascades + pixel tuning** — second cascade, texel snapping, ¼-block
   shadow texels; screenshot matrix at dawn/noon/dusk.
4. **Colored block light** — JSON `lightColor`, RGB flood + skylight,
   mesher/vertex/shader integration; relight-on-edit.
5. **Smooth lighting** — 4-sample vertex smoothing (the AO-ish look).
6. **Night polish** — moon shadows, ambient curve, dusk color grade.
7. **Voxel-raymarch spike (timeboxed)** — occupancy brickmap + DDA shadow
   ray for the sun; compare vs CSM on looks + frame time; keep the winner
   behind a settings toggle.

## Verification

- Screenshot matrix via `--screenshot` at forced times (`tick` commands):
  noon/dusk/midnight, surface/cave/hell; glowstone-vs-lava color contrast
  shot; shadow rotation timelapse (N screenshots across a fast day).
- Perf: frame time budget ≤ +2 ms for CSM at current view distance.
- Relight correctness: place/break glowstone, light updates within one remesh.

## Risks & open questions

- Shadow acne vs peter-panning on axis-aligned voxel faces — mitigated by
  slope-scaled depth bias; needs tuning time.
- Relight cost on edits in caves (large flood volumes) — bounded by light
  radius 15; worst case remains local.
- Vertex format change touches mesher, worker protocol, and both chunk
  shader paths — coordinate with contraption meshing (03) to do it once.
- Open: should placed *contraption* blocks emit light while moving? (v1: no.)
