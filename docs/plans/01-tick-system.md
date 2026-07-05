# Plan 01 — Tick system & timescale

**Size:** S · **Depends on:** nothing · **Unblocks:** day/night (02), physics (03), map throttling (05)

## Goal

Give the game a Minecraft-style heartbeat: the server simulates the world in
discrete ticks at a fixed rate, with a persistent world clock and an
adjustable timescale (speed up, slow down, pause, single-step). Every future
time-dependent system (sun position, physics, redstone-like machinery,
crop growth...) hangs off this one clock.

## Locked decisions

- **20 TPS** fixed base rate (1 tick = 50 ms of game time).
- **Timescale multiplier** `0.0–10.0` (0 = paused) plus single-step,
  controlled from the server console; default 1.0.
- World time (total ticks elapsed) **persists** in the world database.

## Design

### Server

- Replace ad-hoc `PeriodicTimer`s in `GameServer` with one **tick loop**:
  a fixed-timestep accumulator (`Stopwatch`-driven) that calls
  `Tick(worldTick)` at `20 * timescale` Hz. Sleep between ticks; catch up at
  most 5 ticks per frame to avoid spirals after stalls.
- `WorldClock` class: `long WorldTick`, `float Timescale`, tick event.
  `worldTime` stored in the existing `meta` table (updated every ~100 ticks
  and on shutdown — losing <5s of clock on a crash is acceptable).
- Existing systems move onto ticks: player-move broadcast every 2 ticks
  (10 Hz, unchanged rate), stats heartbeat every 1200 ticks.
- **Console commands** (server stdin): `tick rate <mult>`, `tick pause`,
  `tick step [n]`, `tick status`. (Chat/admin protocol commands can come
  later; stdin is enough to start.)

### Protocol

- New message `TimeSync` (server→client): `i64 worldTick, f32 timescale,
  i32 dayLengthTicks`. Sent on join, on any timescale change, and every 100
  ticks as a drift guard.

### Client

- `ClientClock`: reconstructs continuous world time as
  `syncTick + elapsedRealSeconds * 20 * timescale`, clamped/eased when a new
  `TimeSync` disagrees (no visible snapping). Exposes `WorldTimeTicks`
  (fractional) — consumed later by the sun position (02) and entity
  interpolation (03).

## Data / schema changes

- `meta` table: new key `worldTime` (backward compatible — absent key means 0).
- Protocol: one new message type. Web client ignores unknown types today
  (default case) — verify, since this is the first server→web message it
  won't understand.

## Milestones

1. **Tick loop** — accumulator loop in the server, move-broadcast and
   heartbeat migrated onto it; TPS counter in the stats log.
2. **World clock persistence** — `worldTime` survives restart (observable in
   stats log).
3. **TimeSync + client clock** — client HUD shows world time and day count.
4. **Console control** — `tick pause` freezes the HUD clock on all clients;
   `tick step` advances exactly one tick; `tick rate 5` visibly speeds it up.

## Verification

- Unit tests: accumulator produces exactly N ticks for simulated elapsed
  time, including timescale changes mid-run; client clock reconstruction
  converges after a TimeSync correction.
- Manual/scripted: two clients see the same world time; pause/step/rate via
  console observed in HUD; restart preserves the clock.

## Risks & open questions

- Tick loop and WebSocket handlers now share state → all world mutations
  should funnel through the tick thread (the existing `_worldLock` remains
  as a guard until 03 forces a cleaner single-threaded-sim model).
- Timescale > 1 with heavy physics (later) may not hold rate — the loop
  should report "running behind" rather than silently stretching ticks.
