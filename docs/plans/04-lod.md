# Plan 04 — LOD for chunks and contraptions

**Size:** L · **Depends on:** 03 (contraption tier) · **Unblocks:** better maps context (05)

## Goal

See ~32 chunks (512 blocks) into the world at high FPS: full detail near the
player, two rings of progressively coarser terrain beyond, and distance-
scaled sync/rendering for physics contraptions.

## Locked decisions

- Target reach **~32 chunks (512 blocks)**:
  - **LOD0** — full detail, radius 8 chunks (up from 6).
  - **LOD1** — 2× downsampled (16³ → 8³ cells of 2-block size), to 16 chunks.
  - **LOD2** — 4× downsampled, to 32 chunks.
- Server-side downsampling with DB caching (clients stay thin; edits
  invalidate).

## Design

### Data & serving

- **Downsample:** 2×2×2 (then 4×4×4) majority vote of non-air blocks; water
  counts as air unless the cell is majority-water (keeps oceans intact,
  drops shoreline slivers). Produced from stored/generated chunks on demand.
- **Vertical bound:** LOD columns cover a fixed band (coarse-y equivalents of
  world y −64…+80) — distant hell/deep stone is invisible anyway; keeps
  memory linear in radius².
- **Caching:** `lods` table keyed (level, cx, cy, cz) with deflated blobs;
  edit to any source chunk marks its LOD ancestors dirty (regenerated
  lazily on next request).
- **Protocol:** `ChunkRequest` gains a `level` byte (0 = today's behavior,
  wire-compatible extension); `ChunkData` echoes it. LOD payloads are 8× /
  64× smaller than full chunks.

### Client streaming & rendering

- Three concentric request spheres (nearest-first within each ring; LOD0
  keeps absolute priority — coarse rings only stream on leftover budget).
- **Meshing:** the greedy mesher runs unchanged on 8³/4³ grids with a block
  scale factor; UVs stay world-anchored so textures tile at world scale
  (distant terrain keeps the same texel density look).
- **Draw-call budget:** coarse meshes merged 2×2×2 coarse-chunks per mesh →
  LOD1 ≈ LOD0 draw count, LOD2 much lower; target < 600 draws total with
  frustum culling (P7) applying to all rings.
- **Seams:** ring boundaries get **skirts** (downward-extruded edge quads,
  Distant Horizons-style) instead of exact stitching — cheap and invisible
  from grazing angles. Inner-ring geometry always wins the depth test
  (drawn after, slight depth bias on coarse rings).
- **Transitions:** a chunk crossing a ring boundary swaps mesh only when the
  replacement is ready (no holes); fog now starts inside LOD2
  (near = 384, far = 512) so pops hide in haze. Hell fog override unchanged.
- **Lighting at distance (02 interplay):** LOD rings skip voxel light
  (skylight-only shading + sun shadow term from cascade 2 or none) —
  distant colored light is imperceptible; keeps relight cost bounded.

### Contraption LOD (03 interplay)

- **Near (inside LOD0):** full 10 Hz sync + full mesh (today's behavior).
- **Mid (LOD1 ring):** server drops that client's entity updates to 1 Hz;
  client interpolates over the longer window; mesh unchanged (contraptions
  are small relative to terrain).
- **Far (LOD2 ring):** transform frozen at last-known (asleep-looking),
  no per-tick sync; ≥ 250-block contraptions swap to a 2× downsampled mesh.
- Per-client interest management lives server-side (distance bucket per
  entity per client, reevaluated on player chunk crossings).

## Milestones

1. **Protocol + server downsampling** — request LOD1 blobs, verified by a
   unit test (majority-vote correctness) and a hexdump-scale sanity check.
2. **LOD1 ring rendered** — 16-chunk vistas; skirts; screenshot comparison
   with a full-detail reference render of the same area.
3. **LOD2 + merging** — full 32-chunk reach; draw/tri budget measured.
4. **Edit invalidation** — break a mountain top; the distant view updates
   within seconds of the LOD cache refresh.
5. **Contraption tiers** — sync-rate scaling + far-freeze; verified with a
   witness client parked far away counting messages.
6. **Tuning pass** — fog placement, pop masking, budget targets met at
   spawn, mountains, and hell.

## Verification

- `--screenshot` panoramas at spawn and from a mountain top; before/after
  FPS + draws/tris in the HUD line.
- Bandwidth: initial join payload measured (target < 8 MB for full 32-chunk
  reveal); steady-state traffic while flying.
- Memory: client mesh memory reported in stats HUD.

## Risks & open questions

- Majority-vote downsampling can erase thin features (trees later, pillars)
  — acceptable v1; a "prefer-solid" bias flag is a one-line experiment.
- Skirt color mismatch at biome dithering boundaries — likely invisible in
  fog; revisit if screenshots say otherwise.
- LOD relighting is deliberately skipped — when 02 ships after 04 in
  calendar terms this is moot (order says 02 first anyway).
- Open: should LOD2 render underground at all? (Plan: no — coarse rings cull
  chunks fully below the heightmap band; caves stay LOD0-only.)
