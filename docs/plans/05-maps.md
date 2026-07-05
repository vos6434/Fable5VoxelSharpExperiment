# Plan 05 — Minimap & world map

**Size:** M · **Depends on:** 03 (contraption markers) · **Benefits from:** 01 (throttling), 04 (context)

## Goal

A corner **minimap** during play and a full **world map** window (pan/zoom),
both showing terrain from above plus live markers: yourself, other players,
and physics contraptions.

## Locked decisions

- **Omniscient map:** everything the server has ever generated is visible —
  no fog-of-war bookkeeping.
- Map surfaces are **server-computed summaries** (clients never need distant
  chunk data just to draw the map).

## Design

### Server: surface summaries

- Per chunk *column* (cx, cz), a 16×16 tile grid; per tile:
  `top block id (u16) + height (i16) + water depth (u8)` = 5 bytes → 1,280
  bytes/column, deflated in a new `maptiles` table.
- Produced when any chunk in a column is first generated; updated
  incrementally on `SetBlock` (only when the edit is at/above the recorded
  surface, or exposes a new surface by breaking it).
- **Backfill migration:** on first boot with the feature, scan the existing
  `chunks` table top-down per column to seed summaries (one-time,
  progress-logged).

### Protocol

- `MapTilesRequest` (client→server): list of (cx, cz).
- `MapTilesData` (server→client): coords + summary blobs.
- `MapTileUpdate` (server→client): pushed for dirty columns, throttled to
  ≤1/column/second (tick-based, 01), only to clients that have requested
  that column this session.
- Markers need **no new messages**: players come from `PlayerMoves`,
  contraptions from `EntityState` (03) — the map layer just projects them.

### Client: color derivation (data-driven)

- Tile color = average of the block's *top face* texture (computed once at
  atlas load — no new assets), with an optional `"mapColor": "#RRGGBB"`
  override in block JSON for blocks whose texture average reads wrong.
- **Relief shading:** MC-style — compare each tile's height to its
  north neighbor: brighter when higher, darker when lower (cheap, reads
  beautifully). Water: blue tinted darker with depth.

### Client: minimap (HUD)

- A GL texture (256² covering ~24 chunks) updated incrementally as tile data
  arrives; drawn via `UiBatch` in a corner frame (position/size in
  `settings.json`, draggable in the unlock mode like the hotbar).
- North-locked; player arrow rotates with yaw. Remote players = colored
  dots (their entity hue); contraptions = small orange squares.
- Zoom toggle (1 block/px ↔ 2 blocks/px) on a keybind (`M` tap vs `N`?
  final binds decided at implementation; `M` opens the world map).

### Client: world map window

- Opens like a GUI window (Esc/`M` closes) but is its own full-screen-ish
  view: drag to pan, wheel to zoom (0.5–8 blocks/px), lazily requesting
  visible columns from the server; tiles cached client-side for the session.
- Same marker set; contraption markers clickable → shows block count +
  distance (stretch).
- Coordinates readout under the cursor.

## Milestones

1. **Server summaries + backfill** — table populated for the existing world;
   verified by dumping one column and cross-checking heights vs worldgen.
2. **Protocol + tile cache** — client fetches and stores tiles (log-level
   verification).
3. **Minimap** — terrain + relief shading + player arrow; screenshot vs the
   actual terrain from above.
4. **World map window** — pan/zoom, lazy loading, coordinates readout.
5. **Markers** — remote players and contraptions live on both views
   (witness bot + a spawned contraption as test subjects).
6. **Edit updates** — break/place at the surface; both map views update
   within a second.

## Verification

- Screenshot of minimap next to a top-down flight over the same area —
  colors/relief should visibly correspond.
- Marker correctness with the browser-bot witness at a known position.
- Backfill timing on the current ~9k-chunk world (target < 10 s one-time).

## Risks & open questions

- Top-block-only summaries show the *surface*; underground bases are
  invisible by design. (A cave-mode minimap is a possible future toggle —
  out of scope.)
- Map memory at extreme zoom-out: cap the world-map tile cache (LRU) —
  columns re-request cheaply.
- Open: marker for the *local* player's death/spawn points? Waypoints?
  Deferred until requested.
