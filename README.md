# Fable5VoxelSharp

Native C# port of the Fable5Voxel web game (D:\Fable5Voxel): Vulkan client,
C# authoritative server, shared simulation library. The `/data` content
(blocks/items/GUI: JSON + 16x16 PNG) is copied verbatim from the web version
and stays wire/format-compatible with it — the browser client can connect to
this server and vice versa during the port.

## Layout

- `src/Voxel.Shared` — chunk data, seeded simplex noise, worldgen v2,
  block/item registries, binary protocol. **Bit-for-bit identical** to the
  TypeScript implementation, enforced by golden tests
  (`tests/Voxel.Shared.Tests/golden/golden.json`, regenerated with
  `npx tsx tools/dump-golden.ts` in the web repo).
- `src/Voxel.Server` — WebSocket game server + SQLite persistence (port phase 2).
- `src/Voxel.Client` — Vulkan client via Silk.NET (port phase 3+).

## Build & test

```sh
dotnet test
```

Requires .NET 10.
