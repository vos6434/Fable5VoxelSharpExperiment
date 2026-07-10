# Plan 04 — LOD for chunks and contraptions (v2)

**Size:** L · **Depends on:** 03 (contraption tier) · **Unblocks:** better maps context (05)

> **v2 revamp (2026-07-10).** The v1 plan (majority-vote downsampling, two
> fixed per-chunk rings, vertical band, skirts + exact stitching) produced
> visible ring seams, erased surface blocks at distance, and ~1300 draws.
> This revision is modeled on the two Minecraft LOD mods that solve the
> blend-and-looks problem convincingly:
> - **Voxy** (github.com/MCRcortex/voxy) — octree mip pyramid of 32³-voxel
>   sections; mip picks the *most opaque* child voxel, tie toward the top;
>   real block textures on packed quads; parent sections render until all
>   children are ready (hole-free hierarchical refinement).
> - **Distant Horizons** (gitlab.com/distant-horizons-team/distant-horizons)
>   — per-column RLE data points in 64×64 sections; coarser levels
>   *subsample* (never vote); merged quads per section; **no skirts** —
>   seams vanish under generous overdraw (LODs render underneath/behind
>   nearer terrain) plus fog; SQLite store with lazy upward propagation.

## Goal

See ≥ 32 chunks (512 blocks) into the world at high FPS — architecture
extends to 64+ by adding one level per doubling. Full detail near the
player; each LOD level doubles cell size and covers double the radius;
distance-scaled sync for physics contraptions.

## Locked decisions (v2)

- **Sections, not chunks.** LOD level ℓ ≥ 1 is stored/streamed/meshed as
  cubic *sections* of 16³ cells with cell size 2^ℓ blocks, covering 2^ℓ
  chunks per axis (section coords = chunk coords >> ℓ). Every level's
  payload is the same shape as a chunk (16³ u16, deflated) — the wire
  format, DB schema, and mesher are level-invariant. One mesh per section
  keeps draws roughly constant per level.
- **Levels 1..3 initially** (2×, 4×, 8× cells; 32→64 chunk reach), pyramid
  built level-from-level-below server-side; adding level 4 later is config,
  not code.
- **Voxy mip rule, not majority vote:** a cell takes the *most opaque* of
  its 8 child cells, ties broken toward the topmost corner. Surfaces keep
  their block identity (grass stays grass, snow stays snow); coarse terrain
  is always ≥ the fine terrain it covers (no sunken seams, no erased
  floors). Air only where all 8 children are air.
- **No vertical band.** All generated cy participate; all-air sections cost
  a zero-length cached blob and an empty wire payload.
- **Overdraw instead of stitching (DH-style).** No skirts, no cross-ring
  neighbor culling, no exact seams. Each level's ring *overlaps* the finer
  region below it by ~25% of the finer radius; coarser geometry draws
  depth-biased behind finer geometry and only shows where finer coverage is
  absent. Because the mip rounds *up* toward solid, the coarse copy under
  fine terrain never pokes through in silhouette-relevant ways.
- **Real textures, world-anchored UVs** (we already have this — our blocks
  are plain atlas cubes, so we get Voxy's "blocks still look like blocks"
  for free, no model bakery needed).
- **Server-side pyramid with DB cache + lazy invalidation** (kept from v1;
  schema gains nothing new, rows are just section-keyed per level).

## Design

### Data & serving

- **Downsample:** `ChunkLod` mips 2×2×2 → 1 with the Voxy rule. Opacity
  ranking: opaque solid > translucent non-air (water/ice/glass) > air; among
  equals the topmost child (then lowest id) wins deterministically. Level 1
  sections mip from 8 source chunks; level ℓ from 8 level ℓ−1 sections —
  building the pyramid touches each level once, not the raw chunks 8^ℓ times.
- **DB:** existing `lods` table, rows keyed (level, sx, sy, sz), deflated
  16³ u16 cells, zero-length = all air. `lodAlgoVersion` meta wipe stays.
- **Invalidation:** a block edit at chunk (cx,cy,cz) deletes ancestor rows
  (ℓ, cx>>ℓ, cy>>ℓ, cz>>ℓ) for ℓ = 1..maxLevel and evicts the memory cache;
  rebuilt lazily on next request (DH's propagation, simplified to delete).
- **Protocol:** unchanged from v9 — `ChunkRequest(level, coords)` where
  coords are section coords for level ≥ 1; `ChunkData` echoes the level.
  Payloads are level-invariant in size (~1–2 KB deflated per section).

### Client streaming

- Per-level request rings, nearest-section-first: level ℓ covers section
  distance up to R·2^ℓ… i.e. chunk radius [0.75·R·2^(ℓ−1), R·2^ℓ] with the
  inner 25% overlapping the finer level (overdraw region). LOD0 keeps
  absolute priority; level ℓ streams only when every finer level's cursor
  is exhausted (leftover budget, shared in-flight cap).
- **Hole-free refinement (Voxy-style, CPU):** a section renders while any
  of its footprint lacks a finer rendered replacement. Concretely: render
  level ℓ section unless all 8 child footprints are covered (child section
  meshed, or — for ℓ=1 — the 8 underlying chunks all have Done LOD0
  meshes). Coarse-under-fine overlap is expected and harmless (depth bias);
  the rule only exists so far terrain never blinks out.
- **Unload** with hysteresis both outward and inward per level (moved-away
  overlap sections shed instead of accumulating under the fine region).

### Meshing & rendering

- Greedy mesher already runs on any (cellsPerAxis, cellSize) grid; sections
  mesh as 16³ grids with same-level neighbor sections for face culling
  (missing neighbor = air; the resulting interior walls sit in the overlap
  region, hidden behind finer terrain). Baked AO stays (cheap, looks good).
  Skirt emission is deleted.
- Draw order per pass: fine → coarse with increasing polygon offset
  (LOD0 none, level ℓ offset ∝ ℓ), so finer always wins the depth test.
  Liquid-surface pass mirrors solids (depth writes make overlap safe);
  translucent pass renders only the finest available section per footprint
  (no depth writes → double-blend otherwise).
- **Budget target:** < 600 draws total at 32-chunk reach: LOD0 ≈ 300–400
  visible chunk meshes + ~16³-section rings ≈ 50–150 section meshes per
  level. Stretch (post-M4): pack per-level section quads into one shared
  buffer and issue one multi-draw per level, Voxy-style.
- **Fog & sky:** fog completes just inside the outermost level's edge; fog
  color must be sampled from the same gradient the sky shader uses at the
  horizon so fully-fogged LODs melt into the sky instead of silhouetting
  (grey-blob bug from v1 M2).
- **Lighting at distance:** skylight-only (occupancy/light volumes don't
  reach LOD rings; chunk.frag falls back to open-sky shading out there —
  shipped in v1 M2, keep). Distant colored light is imperceptible.

### Contraption LOD (03 interplay — unchanged from v1)

- Near (LOD0): full 10 Hz sync + full mesh. Mid: 1 Hz updates, client
  interpolates. Far: frozen transform, ≥250-block contraptions swap to a
  2× downsampled mesh. Per-client interest buckets server-side.

## Shipped groundwork (v1, kept)

- Protocol v9 level byte; `lods` table (format v3) + `lodAlgoVersion` wipe;
  offline mode for verification; greedy mesher parameterized by grid/cell
  size; per-level streaming with leftover-budget priority; polygon-offset
  LOD passes; chunk.frag skyVisibility fixes (outside-region and
  below-region handling). The v1 per-chunk LOD1 ring, majority vote,
  vertical band, skirts, and underlap-shell special cases get replaced.

## Milestones

1. ~~Protocol + server downsampling~~ **DONE (v1, 2026-07-09)** — revisit:
   swap vote → Voxy mip (**M2v2**).
2. **Sections + mip rule** — server builds the level 1..3 pyramid from
   section mips (level-from-level-below); `ChunkLod` implements the
   opacity/topmost rule; unit tests pin mip semantics (grass-top survival,
   water/translucent ranking, determinism); protocol serves section coords.
   Client streams + renders level 1 sections only (16-chunk reach) with
   overdraw blending; the v1 chunk-ring path is deleted.

   **DONE (2026-07-10).** ChunkLod.MipSections (opacity → topmost corner),
   WorldStore recursive pyramid (lods rows section-keyed per level,
   ancestors invalidated at cx>>ℓ), client generic per-level section rings
   (uniform 3..9 request shell in section units, mesh ≤ 8), coverage-based
   hole-free refinement (section draws while any of its 8 child footprints
   lacks a Done finer stage; translucent only when zero are Done), skirts
   and the v1 underlap/band special cases deleted. Verified at spawn:
   ocean + land vistas seam-free, snow/grass tops survive to the horizon,
   ~400 draws (v1: ~1300), 60 fps. Known leftover: fog-vs-sky grey
   silhouettes at the far edge (M6).
3. **Levels 2–3 + refinement** — full 32→64 chunk reach; hole-free
   hierarchical swap rule; draw/tri budget measured at spawn, mountains,
   ocean; fog/sky horizon match.
4. **Edit invalidation end-to-end** — break a mountain top; ancestors
   regenerate; client re-requests affected sections (server pushes a
   section-dirty notice or client polls on BlockUpdate outside LOD0).
5. **Contraption tiers** — sync-rate scaling + far-freeze (unchanged).
6. **Tuning pass** — pop masking, budgets, hell; stretch: shared quad
   buffer + one multi-draw per level.

## Verification

- Screenshot panoramas (spawn, mountain top, ocean, high altitude) at each
  milestone; draws/tris/FPS in HUD; a "LOD level tint" debug view to check
  ring placement and refinement (both mods ship one — invaluable).
- Bandwidth: initial join payload target < 8 MB at 32-chunk reach;
  steady-state while flying.
- Correctness: mip unit tests; a golden test that the level-1 mip of a
  known chunk matches a hand-computed fixture.

## Risks & open questions

- Most-opaque mip inflates terrain slightly (bushes → solid blobs at 8×);
  acceptable — both reference mods do exactly this and it reads fine.
- Level ≥ 1 sections mesh with same-level neighbors; at level ring edges
  the outermost sections wait for neighbors (data radius = render radius
  + 1 section, as today).
- Water at 8× cells: translucent ranking keeps oceans; shorelines wobble
  by up to 8 blocks at the farthest ring — hidden by fog distance.
- Contraption LOD interplay unchanged and still speculative until M5.
