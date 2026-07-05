# Plan 03 — Physics contraptions, glue item, physics gun

**Size:** XL · **Depends on:** 01 (tick step) · **Unblocks:** LOD contraption tier (04), map markers (05)

## Goal

Create-mod-inspired physics: glue blocks together and they detach from the
grid as a rigid-body **contraption** that tumbles, stacks, and collides.
A Garry's Mod / HL2-style **physics gun** grabs contraptions, carries them,
and throws them. Server-authoritative, multiplayer-correct.

## Locked decisions

- **BepuPhysics v2** (pure C#, no native deps; compound colliders,
  constraints, sleeping, CCD — everything this feature needs).
- **Cap ~1,000 blocks** per contraption (server-enforced at glue time).
- Two new data-driven items: **glue** and **physics_gun**
  (`data/items/*.json` + icons; behavior bound by string id in code — the
  JSON stays the source of stats/name/icon).

## Design

### Simulation (server)

- Bepu `Simulation` stepped inside the tick loop (01): `Timestep(50ms)` per
  tick; timescale changes just change tick cadence — physics stays
  deterministic per tick. Pause/step debugging works for free.
- **Contraption body:** glued block set → local grid (min-corner-relative
  `ushort[]`, same encoding as chunk payloads) → collider = **greedy box
  merge** over the solid mask (reuses the greedy-mesher rectangle logic in
  3D) → Bepu `Compound` of boxes. Mass = block count × per-block base mass
  (later: per-block JSON `mass`). Cap keeps compounds small (typically
  10–100 boxes after merging).
- **World collision:** voxel terrain as static colliders, materialized
  lazily — for each chunk overlapped by an awake body's AABB (+1 margin),
  build merged static boxes (cache; invalidate on edit); remove statics when
  no awake body is near. Fast-moving bodies get CCD enabled.
- **Sleeping:** Bepu's built-in; sleeping contraptions cost ~nothing and are
  the persistence checkpoint.

### Glue workflow

1. RMB with glue on a block → server marks it glued (broadcast for overlay).
2. More RMBs extend the marked set (must stay face-adjacent to the set).
3. Shift+RMB (or RMB on an already-marked block) → **activate**: server
   flood-fills the marked set, validates (≤1,000 blocks, no bedrock/liquids),
   removes the blocks from the world (normal `BlockUpdate`s), spawns the
   contraption entity at the same pose.
4. Client shows marked blocks with the existing line-cube highlight (yellow).

### Disassembly (back to blocks)

- Physics-gun alt-action on a *resting* contraption: snap rotation to the
  nearest 90°, snap position to the grid; if every destination cell is air →
  write blocks back, despawn entity; else refuse (red flash). (Milestone 7.)

### Physics gun

- RMB: ray from eye (existing DDA misses entities, so also ray-test Bepu) →
  hit contraption within 6 blocks → server creates a **servo constraint**
  pulling the grabbed point to a target 4 blocks ahead of the player's eye,
  updated every tick from the player's reported look; damped so it swings
  naturally (the HL2 feel).
- Scroll: hold distance 2–8 blocks. LMB while holding: release + impulse
  (throw). RMB again: gentle release. One holder per contraption (server
  arbitrates); holder shown to others via a taut line (stretch goal).

### Protocol additions

- `EntitySpawn` — id, kind, local-grid dims + palette blob (deflated),
  transform (pos f64×3, quat f32×4).
- `EntityState` — batched per broadcast tick: id, pos f32×3, quat f32×4,
  linear velocity f32×3 (client extrapolation). 10 Hz like player moves;
  sleeping entities stop appearing (client keeps last pose).
- `EntityDespawn` — id, optional "became blocks" flag.
- `GlueMark` / `GlueState` — marked-set overlay sync.
- Client interaction reuses `SetBlock`-style intents: `UseItem` message
  (item id, target block/entity, action bits) — becomes the generic
  server-side item-behavior entry point (future tools reuse it).

### Client rendering

- Contraption grid meshed by the **existing greedy mesher** (it already
  takes a raw `ushort[]`); drawn with a full model matrix — the chunk shader
  gains `uModel` (chunks pass translation-only; contraptions pass TRS).
  Interpolate pos/quat between the last two `EntityState`s (~100 ms behind),
  extrapolate briefly by velocity if starved.
- Contraptions render into the shadow pass (02) once both features exist.

### Persistence & world-format migrations

- `entities` table: id, kind, blocks blob, transform, velocities, asleep
  flag. Saved on change-of-sleep-state + shutdown; loaded (asleep) on boot.
- This is the first schema change since launch, so it ships with a small
  **migration runner**: ordered `Migration` steps keyed off the existing
  `formatVersion` meta value, applied transactionally on world open
  (v1→v2 = create `entities`). Plans 04 (LOD cache) and 07 (block-state
  bits) then add migrations instead of ad-hoc version checks — one
  mechanism, built once, at the moment it's first needed.

## Milestones

1. **Bepu bootstrap** — simulation in the tick loop; a debug 1-block body
   spawned by console command, synced via the new entity messages, rendered
   and interpolated client-side. (Protocol + render path proven end-to-end.)
   **DONE (2026-07-05).** BepuPhysics 2.4 `Simulation` stepped once per world
   tick (physics + entity mutations run only on the tick thread; console/net
   requests enqueue actions). Protocol v4: EntitySpawn (deflated grid +
   pose) / EntityState (batched pos/quat/vel at 10 Hz) / EntityDespawn, all
   round-trip tested. `spawn [x y z]` console command drops a stone box onto
   a temporary raised static floor (M2 replaces it with voxel colliders).
   Client `EntityRenderer` meshes the grid (simple per-block cube mesher —
   the greedy mesher is 16³-only), draws with a `uModel` TRS matrix through
   the chunk shader (lit by the world, no self-occlusion), and interpolates
   pose between states. Verified: box spawned at y=20 renders at its settled
   y≈13 (so streaming + interpolation, not just the spawn pose). Joining
   players get cached spawn payloads. 59 tests.
2. **Terrain collision** — the debug body lands on and rolls down real
   terrain; static-collider cache with edit invalidation.
   **DONE (2026-07-05).** Lazy per-chunk voxel colliders: each tick,
   chunks within 1 of an awake body get greedy-merged box statics (solid
   `Collision.Solid` voxels → maximal boxes via +X/+Y/+Z runs), cached and
   dropped when no awake body is near; block edits enqueue a chunk
   invalidation onto the tick thread. Chunk blocks are fetched via a
   world-lock-guarded clone so the tick thread reads stable data. The temp
   M1 floor is gone; dynamic bodies get continuous collision detection so a
   fast fall can't tunnel thin terrain. Verified: a box dropped from y=20
   lands and rests on the grass surface (colliders build as it falls).
3. **Glue → contraption** — mark/flood/validate/spawn; a glued tower falls
   over as one object. Blocks vanish from the grid correctly for all clients.
   **DONE (2026-07-05).** `glue` data item + protocol v5: `UseItem`
   (mark/activate/clear) and `GlueMarks` overlay; EntitySpawn gained a pivot
   (center of mass in grid-local coords). Server tracks a per-player marked
   set (face-adjacency + glueable = breakable solid enforced, ≤1000);
   activate flood-removes the blocks (BlockUpdates for all clients, collider
   invalidation) and spawns a dynamic Bepu **Compound** of greedy-merged
   boxes (mass ∝ block count). Client: glue in hand → RMB mark / Shift+RMB
   activate / LMB clear, yellow mark overlay; the contraption renders through
   the entity path (uModel = T(pos)·R(quat)·T(−pivot)). `contraption`
   console command builds+glues a test wall. Verified: a 15-block 3×5×1 wall
   glued mid-air fell as one rigid body and toppled flat on the terrain;
   source blocks gone. 61 tests.
4. **Physics gun** — grab/carry/scroll/throw with the servo constraint;
   two players fighting over one crate resolves cleanly (single holder).
   **DONE (2026-07-06).** Protocol v6: `GunGrab` / `GunRelease` / `GunThrow` /
   `GunSetDistance` on `UseItem`, `GunHold` sync to holder. Server Bepu
   raycast (6-block reach) → `OneBodyLinearServo` + angular servo (Grabber
   pattern); hold distance 2–8 blocks (scroll while holding); throw impulse
   on LMB; one holder per entity enforced. Client: `physics_gun` in hand →
   RMB grab/gentle release, LMB throw, scroll adjusts distance. 64 tests.
5. **Persistence + migration runner** — formatVersion-keyed migrations
   (v1→v2 creates `entities`); contraptions survive restart, wake on
   interaction.
6. **Caps & hardening** — 1,000-block cap UX, CCD for throws, contraption-
   vs-contraption stacking stress test (10× 100-block crates).
7. **Disassemble** — snap-back-to-blocks flow.

## Verification

- Each milestone: scripted scenario + `--screenshot` (e.g. glued tower mid-
  tumble), plus a browser-bot-style witness where protocol correctness
  matters (entity messages received, block removals broadcast).
- Physics determinism smoke test: same seed scenario stepped twice server-
  side → identical final transforms (Bepu is deterministic per-machine).
- Perf: 10 awake 100-block contraptions ≤ 2 ms/tick server-side.

## Risks & open questions

- **Static collider churn** when contraptions travel far/fast — bounded by
  AABB margin + cache; worst case is a thrown crate streaming colliders.
- **Grab feel** is tuning-heavy (spring stiffness/damping vs 100 ms input
  latency) — client-side smoothing of the *rendered* held pose can mask it.
- Water: contraptions currently ignore liquids (no buoyancy) — v1 accepts
  this; note for a future buoyancy pass.
- Open: should glue be consumable (stack count decrements)? Default: yes,
  1 per marked block — trivially changeable in item JSON later.
