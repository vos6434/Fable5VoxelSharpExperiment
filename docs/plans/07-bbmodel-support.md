# Plan 07 — Blockbench .bbmodel support

**Size:** L · **Depends on:** nothing hard · **Ordered last by request**

## Goal

First-class [Blockbench](https://blockbench.net) support: drop a `.bbmodel`
file into `/data`, reference it from a block's JSON, and the game renders
that block as the custom model — no converters, no code. Entities using
bbmodel rigs (bones + animations) are the explicit future follow-on; this
plan lays the file-format foundation they'll reuse.

## Format notes (what we consume)

A `.bbmodel` is a single JSON file containing:

- `resolution` — texture-space width/height the UVs are authored against.
- `elements[]` — cuboids: `from`/`to` (model space, MC-style 0–16 per
  block), optional `rotation` (origin + axis + angle, 22.5° steps),
  per-face `uv` + texture reference + optional cullface hints.
- `textures[]` — embedded as base64 PNG data URIs (self-contained files).
- `outliner` (bone tree) and `animations[]` — parsed and validated but
  **unused in v1** (entity groundwork).

## Design

### Loading & registry

- `data/models/*.bbmodel`; block JSON gains optional `"model":
  "lantern.bbmodel"`. Validation at load: unknown model file, malformed
  elements, missing textures → the usual loud `DataException` with file/field.
- Parser lives in `Voxel.Shared` (server needs model *bounds* for collision
  even though it never renders); mesh building is client-side.
- Model textures are **not** 16×16 tiles, so they can't join the block
  texture array: a second **model atlas** (2D, rectangle-packed at load,
  arbitrary sizes) feeds a dedicated UV space. Nearest-filtered like
  everything else.

### Rendering

- Blocks with models are excluded from the greedy mesher's cube passes
  (they set `transparency: cutout` semantics implicitly — neighbors behind
  them still render).
- Per chunk, a **decor mesh**: all model-block instances baked into one
  vertex buffer (element cuboids transformed by their rotations + block
  position). Drawn in the cutout pass with the model atlas bound; per-vertex
  light sampled from the host voxel (02 integration).
- Rotation variants (facing north/east/...) via a `"rotatable": true` flag —
  placement uses player facing; stored in the block's upper state bits
  (introduces 4 block-state bits — the one storage-format change here,
  gated behind a world `formatVersion` bump).

### Collision & targeting

- Default: collision/raycast box = union AABB of the model's elements
  (computed by the shared parser). Optional explicit
  `"collisionBoxes": [[x0,y0,z0,x1,y1,z1], ...]` in block JSON for finer
  shapes (fence-post feel). DDA targeting keeps using full-cell selection
  v1 (highlight stays a full cube; refinement is a stretch).

### Item icons

- Slot rendering: model blocks can't use the generic cube renderer, so bake
  an icon at load — render the model once to a small offscreen texture at
  the standard isometric angle, cache per block. (Until that milestone,
  fall back to the model's first texture as a flat icon.)

### Entities (explicit non-goal now, groundwork noted)

- The parser keeps `outliner` bones and `animations` in its object model.
  When the entity system arrives, rigs animate by bone-transform sampling of
  the same loaded models — file format work done exactly once.

## Milestones

1. **Parser + tests** — load a real Blockbench-authored sample (checked into
   `data/models/`), assert elements/UVs/textures round-trip; validation
   failures covered.
2. **Model atlas** — packed at startup alongside the block atlas; debug dump
   of the packed atlas as PNG for inspection.
3. **Decor mesh rendering** — a `lantern` test block renders its model
   in-world (screenshot); performance sanity with a 1,000-lantern field.
4. **Schema + docs** — `model` / `rotatable` / `collisionBoxes` in
   `data-format.md`; the drop-in walkthrough updated.
5. **Collision boxes** — AABB + explicit boxes respected by server collision
   (03's static collider path) and targeting.
6. **Rotation variants** — block-state bits + placement facing (format
   version bump).
7. **Icon baking** — offscreen isometric render into slot icons.

## Verification

- A model authored in actual Blockbench (not hand-written JSON) is the
  acceptance fixture — export → drop → restart → screenshot in-world and in
  the creative menu.
- Golden-style parser test pinning one fixture's parsed structure.
- Perf: decor-mesh field screenshot with draw/tri counts in HUD.

## Risks & open questions

- Block-state bits touch the world format — the migration story (format
  version gate) must land with milestone 6, not after.
- Oversized models (elements outside the 0–16 block bounds) break culling
  assumptions — clamp with a validation warning, allow up to 1.5 blocks
  overhang (Blockbench display convention), document it.
- Contraptions carrying model blocks: decor meshing must also run for
  contraption grids (03 integration) — planned as part of milestone 3.
- Open: transparency sorting for translucent model faces (glass panes in
  models) — v1 restricts models to opaque/cutout textures, documented.
