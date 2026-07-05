# Feature roadmap

Seven planned features, ordered by dependency and payoff. Each has its own
plan document; each plan is broken into runnable, testable milestones (the
same discipline used for the original build and the port).

| # | Plan | Size | Depends on |
|---|------|------|------------|
| 1 | [Tick system & timescale](01-tick-system.md) ✅ SHIPPED | S | — |
| 2 | [Colored lighting, shadows, day/night](02-lighting-shadows-day-night.md) ✅ SHIPPED | XL | 1 (time of day) |
| 3 | [Physics contraptions, glue & physics gun](03-physics-contraptions.md) | XL | 1 (simulation step) |
| 4 | [Chunk & contraption LOD](04-lod.md) | L | 3 (contraption half) |
| 5 | [Minimap & world map](05-maps.md) | M | 3 (contraption markers) |
| 6 | [Volumetric fog](06-volumetric-fog.md) | M | 2 (shadow map, sun) |
| 7 | [Blockbench bbmodel support](07-bbmodel-support.md) | L | — (last by request) |

## Why this order

1. **Ticks first** — it's small, and both flagship features stand on it:
   day/night needs a world clock, physics needs a fixed simulation step.
   Retrofitting deterministic time under running systems later is far worse.
2. **Lighting second** — the largest *visual* payoff, and the shadow-map
   infrastructure it builds is a hard prerequisite for volumetric light
   shafts (6). Day/night makes the world feel alive before gameplay deepens.
3. **Contraptions third** — the flagship *gameplay* feature. It defines the
   entity sync protocol that LOD (4) and maps (5) both consume.
4. **LOD fourth** — multiplies the value of everything visual, and the
   contraption sync-rate scaling it introduces is what map markers reuse.
5. **Maps fifth** — quality-of-life; consumes entity states (3) and benefits
   from the tick clock (1) for update throttling.
6. **Volumetric fog sixth** — polish tier; sits directly on the lighting
   pipeline (2) and the post-process framebuffer it introduces.
7. **bbmodel last** — per request; it's independent, so it can slot anywhere
   if priorities shift.

## Locked decisions (from planning Q&A, 2026-07-05)

- Physics: **BepuPhysics v2** (pure C#, compound colliders, constraints)
- Ticks: **20 TPS fixed + adjustable timescale** (pause / step / speed)
- Lighting: **voxel ray-traced shadows for ALL lights** (sun, moon, and
  block lights — user upgraded the requirement 2026-07-05): DDA shadow rays
  through a GPU occupancy bitmap, clustered block lights with a shadowed-
  lights cap per cluster. Perf-gated; fallback tier = CSM sun + flood-fill
  block light. VSM rejected.
- Day length: **20 minutes** default, server-configurable
- Map: **omniscient** (shows everything the server has generated)
- LOD reach: **~32 chunks (512 blocks)** via two LOD rings
- Contraption cap: **~1,000 blocks**
- Fog: **half-res raymarched volumetrics with shadow-map light shafts**

## Cross-cutting rules

- Everything stays **data-driven**: new items (glue, physics gun), light
  colors, map colors, models — all land in `/data` with schema docs updated.
- Everything stays **server-authoritative**: physics, time, map state.
- **The web version is dropped** (decision 2026-07-05): D:\Fable5Voxel is an
  archive, no longer a verification tool or compatibility constraint. The
  golden test data stays as a frozen regression pin; if shared math ever
  changes deliberately, goldens are re-baselined from the C# implementation.
- The wire protocol still carries a **version byte in Hello/Welcome**
  (added in plan 01) — not for the web client, but so a stale published
  native client fails with a clear "client outdated" message instead of
  undefined behavior.
- Every milestone ends runnable, with the screenshot/log verification
  workflow used throughout the port.

## Deferred backlog (explicitly postponed 2026-07-05 — core tech first)

- **Survival core** — player gravity/collision/walking, hold-to-mine with
  the existing JSON hardness/tool/drop rules, item pickup.
- **Sound system** — OpenAL, data-driven sound sets (block JSON `sounds`
  keys are already in place), contraption impact audio.
- **Worldgen v3** — trees, ores, cross-chunk structures.
- **Multiplayer QoL** — chat, server config file (seed/port/day length),
  join password.
- **Powered contraption components** — bearings/motors/pistons (Create-mod
  soul). Plan 03 keeps the door open: contraption architecture must allow
  per-block behaviors and multi-body constraints (Bepu motors/hinges), even
  though v1 ships single rigid bodies only.
