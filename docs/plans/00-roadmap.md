# Feature roadmap

Seven planned features, ordered by dependency and payoff. Each has its own
plan document; each plan is broken into runnable, testable milestones (the
same discipline used for the original build and the port).

| # | Plan | Size | Depends on |
|---|------|------|------------|
| 1 | [Tick system & timescale](01-tick-system.md) | S | — |
| 2 | [Colored lighting, shadows, day/night](02-lighting-shadows-day-night.md) | L | 1 (time of day) |
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
- Lighting: **real-time sun shadows** — CSM baseline, texel-snapped for the
  pixelated look; timeboxed voxel-raymarch (SDF-style) spike as an upgrade;
  VSM rejected (soft edges fight the aesthetic). Voxel **RGB colored block
  light** regardless.
- Day length: **20 minutes** default, server-configurable
- Map: **omniscient** (shows everything the server has generated)
- LOD reach: **~32 chunks (512 blocks)** via two LOD rings
- Contraption cap: **~1,000 blocks**
- Fog: **half-res raymarched volumetrics with shadow-map light shafts**

## Cross-cutting rules

- Everything stays **data-driven**: new items (glue, physics gun), light
  colors, map colors, models — all land in `/data` with schema docs updated.
- Everything stays **server-authoritative**: physics, time, map state.
- The wire protocol grows (new message types are listed per plan); the web
  client is legacy — it will lose compatibility once entity messages ship,
  which is accepted.
- Every milestone ends runnable, with the screenshot/log verification
  workflow used throughout the port.
