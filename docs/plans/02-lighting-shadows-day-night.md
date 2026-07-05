# Plan 02 — Colored lighting, real-time voxel shadows, day/night cycle

**Size:** XL · **Depends on:** 01 (world clock) · **Unblocks:** volumetric fog (06)

## Goal

Three intertwined systems:

1. **Colored block light with real-time shadows** — glowstone glows warm,
   lava orange; torches and lamps cast crisp shadows that move when the
   light source moves; colors defined per block in `/data` JSON.
2. **Real-time pixelated sun/moon shadows** that rotate as the sun and moon
   cross the sky.
3. **Day/night cycle** — 20-minute default (configurable), sky gradient,
   sun/moon rendering, ambient light curve.

## Locked decisions

- **Block lights must cast real-time shadows too** (user decision,
  2026-07-05). That rules the architecture:
  - **Shadow maps** — fine for one sun (CSM), impossible for "player places
    50 torches" (6 depth renders per point light). Rejected as primary.
  - **VSM/EVSM** — soft, light-bleeding; fights the pixel aesthetic. Rejected.
  - **Flood-fill only** — no directionality, no shadows. Demoted to fallback.
  - **Voxel-raymarched shadows (chosen primary)** — our world is already a
    voxel grid, so shadow rays can DDA through a GPU **occupancy bitmap**
    (1 bit/voxel). One shared structure answers shadow rays for *every*
    light — sun, moon, and block lights alike, Teardown-style. Crisp
    per-voxel shadows are the native output, which is exactly the requested
    look.
- **Fallback tier** (settings + perf gate): CSM sun shadows + classic
  flood-fill block light — fully specified below, implemented only if the
  primary misses the perf gate on the dev GPU.
- Day length: 24,000 ticks (20 min) default; `dayLengthTicks` via
  `TimeSync` (01).

## Design — primary architecture (voxel ray-lit)

### A. GPU occupancy world

- **Occupancy bitmap:** 1 bit per voxel, packed per chunk (16³ = 512 bytes),
  stored in a 3D texture / SSBO region covering the camera ± ~96 blocks
  (light radius + sun ray reach), remapped as the player moves (toroidal
  addressing so movement is cheap).
- **Coarse level:** 1 bit per chunk-brick occupancy ("any solid inside") for
  two-level DDA — long sun rays skip empty chunks in single steps.
- **Updates:** on chunk load and on every block edit, re-upload that chunk's
  512-byte slab (trivial bandwidth). Translucent blocks (water, ice, glass,
  leaves) count as *non-occluding* v1 (light passes; noted simplification).

### B. Light sources & clustering

- **Emitter registry:** per loaded chunk, the client extracts emitter list
  (position, RGB color from JSON `lightColor`, intensity 0–15) on
  load/edit — same data the fallback flood-fill would use.
- **Clustered shading:** world-space cluster grid (8³ blocks per cluster)
  around the camera; CPU builds per-cluster light lists each time chunks or
  emitters change (not per frame). Fragment shader: loop the cluster's
  lights — **cap 8 strongest per cluster** — for each: linear falloff to
  radius 15 × color × **DDA shadow ray** (≤ 30 fine steps, early-out on
  first hit). Lights beyond the cap contribute unshadowed falloff (they
  glow, no distinct shadow) so torch-spam degrades gracefully.
- **Sun/moon:** one long ray per fragment using the two-level DDA (coarse
  chunk skips, fine steps near geometry); pixelated by nature — occupancy
  *is* the voxel grid. Moon = opposite azimuth, ~25% cool intensity.
- **Sky/ambient:** skylight via cheap column openness (computed with the
  emitter scan, stored per voxel column) × time-of-day curve; vertex-baked
  voxel AO (corner darkening from occupancy) keeps unlit areas readable.
- **Composite:** `albedo × (Σ shadowedBlockLights + skylight × skyColor(t) ×
  sunRay × max(N·L, 0) + ambientFloor)`.
- **Surface normals (added 2026-07-05):** the original formula had no N·L
  term, which made sun-averted faces fully lit and forced a large ray-origin
  bias that ate contact shadows and leaked light. The chunk shader now
  reconstructs the face normal from screen-space derivatives
  (`cross(dFdx(worldPos), dFdy(worldPos))`, camera-oriented — exact and
  constant on flat voxel faces, so the pixel look is preserved). Shadow rays
  start 0.01 blocks along the normal (landing in the air voxel in front of
  the face); fragments with N·L ≤ 0 skip the ray entirely. Block-light rays
  (M5) should reuse the same normal and bias.

### C. Known limitations (v1, documented)

- Moving **contraptions (03)** are not in the occupancy bitmap: they receive
  light (sampled at their position) but don't block rays while moving.
  Stretch: voxelize awake contraptions into a dynamic overlay bitmap.
- Shadow reach = occupancy region (~96 blocks); beyond that, distance fog
  and the LOD rings (04) take over with sky-only shading.
- Emissive *surfaces* light from block centers (point approximation), fine
  at pixel-art scale.

## Design — fallback tier (only if perf gate fails)

- **CSM sun shadows:** 2 texel-snapped orthographic cascades (¼-block texels
  near, 1-block far), hard single-tap sampling for the pixelated look,
  depth-only chunk pass.
- **Flood-fill colored block light:** per chunk `ushort[4096]` packed
  `R:4 G:4 B:4 Sky:4`; BFS per channel (−1/block, opaque absorbs); computed
  on the mesher pool per chunk + neighbors; per-vertex sampling with
  4-neighbor smoothing. Block lights occlude but do **not** cast
  directional shadows in this tier.

## Day/night cycle (both tiers)

- Sun direction from `WorldTimeTicks`: 360°/day azimuth on a ~30° tilted
  axis (shadows never vanish at noon).
- Sky gradient keyed on sun elevation (day → dusk orange → night), fog color
  follows horizon (hell override unchanged); sun/moon textured quads from
  `data/environment/`; stars stretch goal.
- Ambient intensity curve (1.0 noon → ~0.15 midnight).

## Data / schema changes

- Block JSON: optional `"lightColor": "#RRGGBB"` (default white; intensity
  stays `lightEmission`). Parsed + validated in `Voxel.Shared`
  (`BlockRegistry`); the web repo's `data-format.md` still needs the field
  documented when the web client adopts it.
- `data/environment/sun.png`, `moon.png` (gen-textures placeholders).

## Milestones

0. **Render pipeline groundwork** (pulled forward from plan 06) — offscreen
   scene rendering (color + depth targets, composite pass = the `PostChain`
   skeleton) and **GPU pass timers** shown in the HUD. Rationale: milestone
   3's perf gate needs *measured* pass timings to be a real gate, and the
   documented plan-B (half-res lighting + bilateral upsample) needs the
   offscreen pipeline anyway. Plan 06 then inherits both for free.
1. **Clock → sky** — sun position + sky gradient + sun/moon quads from world
   time; `tick rate 10` timelapse.
2. **Occupancy infra** — bitmap region + per-chunk upload + edit updates;
   debug view (shader mode rendering occupancy directly).
3. **Sun ray shadows** — two-level DDA; **PERF GATE** (measured via M0
   timers): ≥ 60 fps at current view distance on the dev GPU, else pivot to
   fallback tier (CSM first).
   **DONE (2026-07-05): GATE PASSED.** Single-level DDA (96 steps) through a
   176^3 R8 occupancy volume costs 0.4-0.95 ms world-pass GPU time at 166 fps
   (VSync-capped). CSM fallback NOT needed. Shadows verified rotating with
   the sun (morning/afternoon slopes flip). Two-level coarse DDA deferred as
   a future optimization — not required to hit the gate.
   **FIXED (2026-07-05):** first version looked flat/wrong: no N·L term
   (see composite note above), a 1.2-block sun-ward ray bias that erased
   contact shadows and produced lit islands inside shadows, and the volume
   was fully wiped and re-uploaded on every chunk crossing instead of the
   planned toroidal remap (shadows blinked out while moving). All three
   corrected: derivative normals + N·L + 0.01-block normal bias, and true
   toroidal addressing (slab = chunk mod region; only the newly-entered rim
   re-uploads). World pass now 1.2-1.65 ms — still under the 4 ms budget.
4. **Emitters + clusters** — registry, cluster lists, unshadowed colored
   point lights (lightColor JSON live here).
   **DONE (2026-07-05).** `LightVolume` scans loaded chunks for emitters
   (only blocks *exposed* to a non-opaque neighbor count, so buried lava
   oceans don't flood the registry) and distributes them into an 8^3-block
   cluster grid covering the occupancy region. GPU side is one RGBA32F 3D
   texture, 9 depth slices per cluster column: 8 light slots (position +
   color5:5:5/intensity4 packed as an exact-integer float — GL 3.3 has no
   SSBOs and NaN-pattern bit packing through float textures is unsafe) plus
   one **overflow slice**: evicted lights fold into an unshadowed per-cluster
   color as planned. Rebuilds are change-driven (edit/stream/recenter),
   debounced 0.2 s, and follow the occupancy volume's origin so recenters
   stay atomic. `lightColor` parsed in `Voxel.Shared` (registry tests);
   glowstone #FFD9A0, lava #FF7A2A. Emissive blocks render fullbright via a
   +4.0 flag on the baked vertex brightness.
5. **Block-light shadow rays** — capped shadowed lights per cluster; the
   money shot: a pillar between a torch and a wall casts a moving shadow
   when the torch is re-placed.
   **DONE (2026-07-05).** Per-light DDA (≤ 30 steps) through the same
   occupancy volume, using the derivative normal + 0.01-block bias from M3
   and N·L per light (kills back-face leakage). The ray terminates when it
   reaches the emitter's own voxel — emitting blocks are solid in the
   occupancy volume and would otherwise shadow themselves. Verified at
   midnight: glowstone column throws a warm pool with crisp dark shadows
   behind adjacent dirt blocks.
6. **Skylight + AO** — column openness, vertex AO, ambient curve; caves go
   properly dark at night.
   **DONE (2026-07-05).** Composite reworked to
   `albedo × shade × (ambientFloor + skyAmbient × skyVis + dirLight × N·L ×
   shadowRay + Σ blockLights)` where `shade` = face brightness × vertex AO.
   *Deviation:* column openness is a straight-up occupancy ray per fragment
   (≤ 96 vertical texel steps, early-out on ceilings, skipped entirely when
   sky ambient is ~0 e.g. hell) instead of the planned CPU per-column data —
   reuses the existing volume, exact within the region, zero new CPU state.
   Vertex AO is classic 3-neighbor corner occlusion baked in the greedy
   mesher; the merge key now includes the 4 corner AO levels so rectangles
   merge only when AO matches. Corner samples that would cross two chunk
   boundaries read as air (pool ships face neighbors only) — invisible seam
   in practice.
7. **Night polish** — moon shadows, dusk grade, hell interplay (hell has no
   sun — ambient red floor).
   **DONE (2026-07-05).** `SkyState` now exposes the shadow-casting
   directional light: sun while up, else moon at the opposite azimuth with a
   cool ~25% intensity; both fade through the horizon so dusk is lit by the
   (already orange-graded) ambient alone. Hell: sky ambient and directional
   light fade out with `hellT`, ambient floor lerps to a dim red — lava
   emitters (via M4/M5) carry the scene. Verified: moonlit surface with
   crisp moon shadows at 00:00, dusk grade at 17:36, hell at
   (0, −140, 0) reads dim red with orange lava light pools.
8. **Perf/quality tiers** — settings: shadowed-light cap (0/2/4/8), region
   size; fallback tier implemented here only if gate 3 failed.
   **DONE (2026-07-05).** `settings.json`: `shadowedLightCap` (0/2/4/8,
   default 8, cycles live from the F3 debug menu, persisted) and
   `shadowRegionRadius` (chunks, 4–6, default 5, applied at startup to both
   occupancy and light volumes). Gate 3 passed, so the CSM/flood-fill
   fallback tier was never built. Measured world-pass GPU time with
   everything on: 0.9–2.0 ms at 1080p (noon close-up with block lights
   ~1.9 ms, hell ~0.4 ms since sun/sky terms skip) — inside the 4 ms budget.

## Verification

- Screenshot matrix at forced times: noon/dusk/midnight × surface/cave/hell;
  torch-behind-pillar shadow shot; glowstone vs lava color contrast;
  sun-shadow rotation timelapse (N screenshots across a fast day).
- Frame-time budget: lighting ≤ 4 ms at 1080p on the dev GPU (measured in
  the HUD pass timings from 06's groundwork or a temporary GL timer).
- Edit correctness: place/break glowstone → occupancy + emitter lists update
  within one frame; shadows move accordingly.

## Risks & open questions

- **Perf is the big bet** — hence the explicit gate at milestone 3 and the
  fully-specified fallback. Fill-rate scales with resolution; half-res
  lighting + bilateral upsample is the documented plan-B before full pivot.
- Cluster rebuild cost in emitter-dense areas — lists rebuild on change
  only, and caves cap naturally by loaded radius.
- Torch-spam beyond the per-cluster cap loses *distinct* shadows (graceful,
  but should be communicated — maybe a debug overlay showing shadowed vs
  unshadowed emitters).
- Open: should glass tint rays (colored shadows through stained glass
  later)? Occupancy is 1-bit v1; a per-voxel RGB filter layer is a known
  future extension.
