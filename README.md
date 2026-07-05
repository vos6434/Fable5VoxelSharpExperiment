# Fable5VoxelSharp

Native C# port of the Fable5Voxel web game: OpenGL client (Silk.NET), C#
authoritative server, shared simulation library. Multiplayer, infinite cubic-
chunk world with 3D biomes (surface climates, cave biomes, a hell layer),
fully data-driven blocks/items/GUIs.

The `/data` content (JSON + 16x16 PNG) is shared verbatim with the web
version (D:\Fable5Voxel), and the wire protocol + SQLite world format are
byte-compatible — browser and native clients can play on the same server.

## Run (dev)

```sh
dotnet run --project src/Voxel.Server    # ws://0.0.0.0:8081, world in worlds/main.db
dotnet run --project src/Voxel.Client    # connects to ws://localhost:8081
```

Client flags: `--server ws://host:8081`, `--pos x y z`, `--look yaw pitch`,
`--screenshot out.png --frames N` (automated verification).

## Controls

- Click to capture the mouse; **Esc** releases (pause menu)
- **WASD** move, **Space/Shift** up/down, **Ctrl** sprint
- **LMB** break, **RMB** place, **1–9/0** + mouse wheel select hotbar slot
- **E** opens/closes the inventory + creative menu
- Hold **Ctrl** (with GUIs open) to reveal window title bars: drag to move,
  **R** button resets a window's position
- Pause menu: unlock hotbar dragging / reset hotbar position

## Layout

- `src/Voxel.Shared` — chunk data, seeded simplex noise, worldgen v2,
  registries, binary protocol. Bit-for-bit identical to the TypeScript
  implementation (golden tests in `tests/`, reference data regenerated with
  `npx tsx tools/dump-golden.ts` in the web repo).
- `src/Voxel.Server` — Kestrel WebSocket game server + SQLite world store.
- `src/Voxel.Client` — Silk.NET/OpenGL 3.3 client: threaded greedy meshing,
  3D-radius chunk streaming, texture-array atlas, data-driven GUI with
  sprite batch + baked font.
- `data/` — blocks/items/gui content; `shaders/` — GLSL, loaded at runtime.

## Package for friends

```sh
powershell -File tools/publish.ps1
```

Produces self-contained `dist/server` and `dist/client` folders (no .NET
install needed). Ship the client folder; run the server on the host machine
and pass `--server ws://<host-ip>:8081` to remote clients.

## Test

```sh
dotnet test
```
