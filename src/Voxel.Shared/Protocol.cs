using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxel.Shared;

/// <summary>
/// Binary WebSocket protocol, byte-compatible with the web version: one type
/// byte followed by a type-specific body — little-endian numbers, JSON bodies
/// for low-rate control messages, raw-deflate compressed block arrays for
/// chunk payloads. Golden tests pin the exact byte layout.
/// </summary>
public enum Msg : byte
{
    // client → server
    Hello = 1,        // JSON { name, protocolVersion }
    ChunkRequest = 2, // u16 count, count x (i32 cx, i32 cy, i32 cz)
    Move = 3,         // f64 x,y,z, f32 yaw, f32 pitch
    SetBlock = 4,     // i32 x,y,z, u16 blockId (0 = break)
    TimeControl = 5,  // i64 setTick (-1 = no change), f32 timescale (<0 = no change)
    UseItem = 6,      // u8 action, i32 x,y,z (block coords or gun hold distance in centi-blocks)
    // server → client
    Welcome = 10,     // JSON { playerId, seed, spawn, palette, protocolVersion }
    ChunkData = 11,   // i32 cx,cy,cz, u8 empty, [deflate-raw u16 LE blocks]
    BlockUpdate = 12, // i32 x,y,z, u16 blockId
    PlayerJoin = 13,  // JSON { id, name }
    PlayerLeave = 14, // JSON { id }
    PlayerMoves = 15, // u16 count, count x (u16 id, f32 x,y,z,yaw,pitch)
    TimeSync = 16,    // i64 worldTick, f32 timescale, i32 dayLengthTicks
    // Physics entities (plan 03): contraptions and debug bodies.
    EntitySpawn = 17,   // u32 id, u8 kind, u16 dimX,dimY,dimZ, f64 x,y,z, f32 qx,qy,qz,qw, f32 pivotX,Y,Z, [deflate-raw u16 blocks]
    EntityState = 18,   // u16 count, count x (u32 id, f32 x,y,z, f32 qx,qy,qz,qw, f32 vx,vy,vz)
    EntityDespawn = 19, // u32 id, u8 becameBlocks
    GlueMarks = 20,     // u16 count (0–2), count x (i32 x,y,z) — selection corners 1..2
    GunHold = 21,       // u32 entityId (0 = not holding) — physics gun grab sync
}

public sealed class WelcomePayload
{
    [JsonPropertyName("playerId")] public required int PlayerId { get; init; }
    [JsonPropertyName("seed")] public required int Seed { get; init; }
    [JsonPropertyName("spawn")] public required SpawnPoint Spawn { get; init; }
    /// <summary>Server's block palette (stringIds by numericId); must match the client's.</summary>
    [JsonPropertyName("palette")] public required string[] Palette { get; init; }
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; init; }
}

public sealed class SpawnPoint
{
    [JsonPropertyName("x")] public required double X { get; init; }
    [JsonPropertyName("y")] public required double Y { get; init; }
    [JsonPropertyName("z")] public required double Z { get; init; }
}

public sealed class PlayerJoinPayload
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
}

public sealed class PlayerLeavePayload
{
    [JsonPropertyName("id")] public required int Id { get; init; }
}

public sealed class HelloPayload
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    /// <summary>Optional so pre-versioning clients decode cleanly (and get rejected with a clear reason).</summary>
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; init; }
}

public readonly record struct PlayerMove(ushort Id, float X, float Y, float Z, float Yaw, float Pitch);

public readonly record struct MovePayload(double X, double Y, double Z, float Yaw, float Pitch);

public readonly record struct BlockChange(int X, int Y, int Z, ushort BlockId);

public readonly record struct ChunkDataHeader(int Cx, int Cy, int Cz, bool Empty, ReadOnlyMemory<byte> Payload);

public static class Protocol
{
    public const int Port = 8081;

    /// <summary>
    /// Bumped on every wire-format change. Mismatched clients are rejected at
    /// Hello with a clear close reason instead of decoding garbage.
    /// History: 1 = launch (implicit), 2 = version handshake + TimeSync,
    /// 3 = TimeControl (debug menu time slider / pause),
    /// 4 = physics entities (spawn/state/despawn),
    /// 5 = UseItem + glue marks + EntitySpawn pivot (contraptions),
    /// 6 = physics gun (grab actions + GunHold sync).
    /// </summary>
    public const int Version = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Msg TypeOf(ReadOnlySpan<byte> message) => (Msg)message[0];

    // ---- JSON messages ------------------------------------------------------

    public static byte[] EncodeJson<T>(Msg type, T payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var outBuf = new byte[1 + body.Length];
        outBuf[0] = (byte)type;
        body.CopyTo(outBuf, 1);
        return outBuf;
    }

    public static T DecodeJson<T>(ReadOnlySpan<byte> message)
    {
        return JsonSerializer.Deserialize<T>(message[1..], JsonOptions)
            ?? throw new InvalidDataException("null JSON payload");
    }

    // ---- ChunkRequest -------------------------------------------------------

    public static byte[] EncodeChunkRequest(IReadOnlyList<(int Cx, int Cy, int Cz)> coords)
    {
        var outBuf = new byte[3 + coords.Count * 12];
        outBuf[0] = (byte)Msg.ChunkRequest;
        BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(1), (ushort)coords.Count);
        for (int i = 0; i < coords.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(3 + i * 12), coords[i].Cx);
            BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(7 + i * 12), coords[i].Cy);
            BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(11 + i * 12), coords[i].Cz);
        }
        return outBuf;
    }

    public static List<(int Cx, int Cy, int Cz)> DecodeChunkRequest(ReadOnlySpan<byte> message)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(message[1..]);
        var coords = new List<(int, int, int)>(count);
        for (int i = 0; i < count; i++)
        {
            coords.Add((
                BinaryPrimitives.ReadInt32LittleEndian(message[(3 + i * 12)..]),
                BinaryPrimitives.ReadInt32LittleEndian(message[(7 + i * 12)..]),
                BinaryPrimitives.ReadInt32LittleEndian(message[(11 + i * 12)..])));
        }
        return coords;
    }

    // ---- ChunkData ----------------------------------------------------------

    public static byte[] EncodeChunkData(int cx, int cy, int cz, byte[]? compressed)
    {
        int payloadLength = compressed?.Length ?? 0;
        var outBuf = new byte[14 + payloadLength];
        outBuf[0] = (byte)Msg.ChunkData;
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(1), cx);
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(5), cy);
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(9), cz);
        outBuf[13] = (byte)(compressed is null ? 1 : 0); // 1 = empty chunk
        compressed?.CopyTo(outBuf, 14);
        return outBuf;
    }

    public static ChunkDataHeader DecodeChunkData(ReadOnlyMemory<byte> message)
    {
        var span = message.Span;
        return new ChunkDataHeader(
            BinaryPrimitives.ReadInt32LittleEndian(span[1..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[5..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[9..]),
            span[13] == 1,
            message[14..]);
    }

    // ---- Move ---------------------------------------------------------------

    public static byte[] EncodeMove(double x, double y, double z, float yaw, float pitch)
    {
        var outBuf = new byte[33];
        outBuf[0] = (byte)Msg.Move;
        BinaryPrimitives.WriteDoubleLittleEndian(outBuf.AsSpan(1), x);
        BinaryPrimitives.WriteDoubleLittleEndian(outBuf.AsSpan(9), y);
        BinaryPrimitives.WriteDoubleLittleEndian(outBuf.AsSpan(17), z);
        BinaryPrimitives.WriteSingleLittleEndian(outBuf.AsSpan(25), yaw);
        BinaryPrimitives.WriteSingleLittleEndian(outBuf.AsSpan(29), pitch);
        return outBuf;
    }

    public static MovePayload DecodeMove(ReadOnlySpan<byte> message)
    {
        return new MovePayload(
            BinaryPrimitives.ReadDoubleLittleEndian(message[1..]),
            BinaryPrimitives.ReadDoubleLittleEndian(message[9..]),
            BinaryPrimitives.ReadDoubleLittleEndian(message[17..]),
            BinaryPrimitives.ReadSingleLittleEndian(message[25..]),
            BinaryPrimitives.ReadSingleLittleEndian(message[29..]));
    }

    // ---- SetBlock / BlockUpdate (same layout, different type byte) -----------

    public static byte[] EncodeBlockChange(Msg type, int x, int y, int z, ushort blockId)
    {
        if (type != Msg.SetBlock && type != Msg.BlockUpdate)
        {
            throw new ArgumentException($"not a block-change message type: {type}", nameof(type));
        }
        var outBuf = new byte[15];
        outBuf[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(1), x);
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(5), y);
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(9), z);
        BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(13), blockId);
        return outBuf;
    }

    public static BlockChange DecodeBlockChange(ReadOnlySpan<byte> message)
    {
        return new BlockChange(
            BinaryPrimitives.ReadInt32LittleEndian(message[1..]),
            BinaryPrimitives.ReadInt32LittleEndian(message[5..]),
            BinaryPrimitives.ReadInt32LittleEndian(message[9..]),
            BinaryPrimitives.ReadUInt16LittleEndian(message[13..]));
    }

    // ---- Physics entities (plan 03) -------------------------------------------

    public readonly record struct EntitySpawnData(
        uint Id, byte Kind, ushort DimX, ushort DimY, ushort DimZ,
        double X, double Y, double Z, float Qx, float Qy, float Qz, float Qw,
        float PivotX, float PivotY, float PivotZ,
        ReadOnlyMemory<byte> CompressedBlocks);

    private const int EntitySpawnHeader = 64; // 1+4+1+6+24+16+12

    public static byte[] EncodeEntitySpawn(
        uint id, byte kind, ushort dimX, ushort dimY, ushort dimZ,
        double x, double y, double z, float qx, float qy, float qz, float qw,
        float pivotX, float pivotY, float pivotZ, byte[] compressedBlocks)
    {
        var outBuf = new byte[EntitySpawnHeader + compressedBlocks.Length];
        var s = outBuf.AsSpan();
        s[0] = (byte)Msg.EntitySpawn;
        BinaryPrimitives.WriteUInt32LittleEndian(s[1..], id);
        s[5] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], dimX);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], dimY);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], dimZ);
        BinaryPrimitives.WriteDoubleLittleEndian(s[12..], x);
        BinaryPrimitives.WriteDoubleLittleEndian(s[20..], y);
        BinaryPrimitives.WriteDoubleLittleEndian(s[28..], z);
        BinaryPrimitives.WriteSingleLittleEndian(s[36..], qx);
        BinaryPrimitives.WriteSingleLittleEndian(s[40..], qy);
        BinaryPrimitives.WriteSingleLittleEndian(s[44..], qz);
        BinaryPrimitives.WriteSingleLittleEndian(s[48..], qw);
        BinaryPrimitives.WriteSingleLittleEndian(s[52..], pivotX);
        BinaryPrimitives.WriteSingleLittleEndian(s[56..], pivotY);
        BinaryPrimitives.WriteSingleLittleEndian(s[60..], pivotZ);
        compressedBlocks.CopyTo(outBuf, EntitySpawnHeader);
        return outBuf;
    }

    public static EntitySpawnData DecodeEntitySpawn(ReadOnlyMemory<byte> message)
    {
        var s = message.Span;
        return new EntitySpawnData(
            BinaryPrimitives.ReadUInt32LittleEndian(s[1..]),
            s[5],
            BinaryPrimitives.ReadUInt16LittleEndian(s[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(s[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
            BinaryPrimitives.ReadDoubleLittleEndian(s[12..]),
            BinaryPrimitives.ReadDoubleLittleEndian(s[20..]),
            BinaryPrimitives.ReadDoubleLittleEndian(s[28..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[36..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[40..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[44..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[48..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[52..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[56..]),
            BinaryPrimitives.ReadSingleLittleEndian(s[60..]),
            message[EntitySpawnHeader..]);
    }

    // ---- UseItem / glue (plan 03 M3) ------------------------------------------

    public enum ItemAction : byte
    {
        GlueMark = 0, GlueActivate = 1, GlueClear = 2,
        GunGrab = 3, GunRelease = 4, GunThrow = 5,
        /// <summary>z = hold distance in centi-blocks (200–800 → 2.0–8.0 blocks).</summary>
        GunSetDistance = 6,
    }

    /// <summary>Sync the marking player's box selection (0, 1, or 2 corners).</summary>
    public static byte[] EncodeGlueSelection((int X, int Y, int Z)? corner1, (int X, int Y, int Z)? corner2)
    {
        if (corner1 is null) return EncodeGlueMarks([]);
        if (corner2 is null) return EncodeGlueMarks([corner1.Value]);
        return EncodeGlueMarks([corner1.Value, corner2.Value]);
    }

    public static ((int X, int Y, int Z)? Corner1, (int X, int Y, int Z)? Corner2) DecodeGlueSelection(ReadOnlySpan<byte> message)
    {
        var marks = DecodeGlueMarks(message);
        return marks.Length switch
        {
            0 => (null, null),
            1 => (marks[0], null),
            _ => (marks[0], marks[1]),
        };
    }

    public static byte[] EncodeGunHold(uint entityId)
    {
        var outBuf = new byte[5];
        outBuf[0] = (byte)Msg.GunHold;
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(1), entityId);
        return outBuf;
    }

    public static uint DecodeGunHold(ReadOnlySpan<byte> message)
        => BinaryPrimitives.ReadUInt32LittleEndian(message[1..]);

    public static byte[] EncodeUseItem(ItemAction action, int x, int y, int z)
    {
        var outBuf = new byte[14];
        outBuf[0] = (byte)Msg.UseItem;
        outBuf[1] = (byte)action;
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(2), x);
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(6), y);
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(10), z);
        return outBuf;
    }

    public static (ItemAction Action, int X, int Y, int Z) DecodeUseItem(ReadOnlySpan<byte> message)
        => ((ItemAction)message[1],
            BinaryPrimitives.ReadInt32LittleEndian(message[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(message[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(message[10..]));

    public static byte[] EncodeGlueMarks(IReadOnlyList<(int X, int Y, int Z)> marks)
    {
        var outBuf = new byte[3 + marks.Count * 12];
        outBuf[0] = (byte)Msg.GlueMarks;
        BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(1), (ushort)marks.Count);
        for (int i = 0; i < marks.Count; i++)
        {
            int o = 3 + i * 12;
            BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(o), marks[i].X);
            BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(o + 4), marks[i].Y);
            BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(o + 8), marks[i].Z);
        }
        return outBuf;
    }

    public static (int X, int Y, int Z)[] DecodeGlueMarks(ReadOnlySpan<byte> message)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(message[1..]);
        var marks = new (int, int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int o = 3 + i * 12;
            marks[i] = (
                BinaryPrimitives.ReadInt32LittleEndian(message[o..]),
                BinaryPrimitives.ReadInt32LittleEndian(message[(o + 4)..]),
                BinaryPrimitives.ReadInt32LittleEndian(message[(o + 8)..]));
        }
        return marks;
    }

    public readonly record struct EntityState(
        uint Id, float X, float Y, float Z, float Qx, float Qy, float Qz, float Qw, float Vx, float Vy, float Vz);

    public static byte[] EncodeEntityStates(IReadOnlyList<EntityState> entities)
    {
        var outBuf = new byte[3 + entities.Count * 44];
        outBuf[0] = (byte)Msg.EntityState;
        BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(1), (ushort)entities.Count);
        for (int i = 0; i < entities.Count; i++)
        {
            int o = 3 + i * 44;
            var e = entities[i];
            var s = outBuf.AsSpan(o);
            BinaryPrimitives.WriteUInt32LittleEndian(s, e.Id);
            BinaryPrimitives.WriteSingleLittleEndian(s[4..], e.X);
            BinaryPrimitives.WriteSingleLittleEndian(s[8..], e.Y);
            BinaryPrimitives.WriteSingleLittleEndian(s[12..], e.Z);
            BinaryPrimitives.WriteSingleLittleEndian(s[16..], e.Qx);
            BinaryPrimitives.WriteSingleLittleEndian(s[20..], e.Qy);
            BinaryPrimitives.WriteSingleLittleEndian(s[24..], e.Qz);
            BinaryPrimitives.WriteSingleLittleEndian(s[28..], e.Qw);
            BinaryPrimitives.WriteSingleLittleEndian(s[32..], e.Vx);
            BinaryPrimitives.WriteSingleLittleEndian(s[36..], e.Vy);
            BinaryPrimitives.WriteSingleLittleEndian(s[40..], e.Vz);
        }
        return outBuf;
    }

    public static EntityState[] DecodeEntityStates(ReadOnlySpan<byte> message)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(message[1..]);
        var entities = new EntityState[count];
        for (int i = 0; i < count; i++)
        {
            int o = 3 + i * 44;
            entities[i] = new EntityState(
                BinaryPrimitives.ReadUInt32LittleEndian(message[o..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 4)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 8)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 12)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 16)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 20)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 24)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 28)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 32)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 36)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 40)..]));
        }
        return entities;
    }

    public static byte[] EncodeEntityDespawn(uint id, bool becameBlocks)
    {
        var outBuf = new byte[6];
        outBuf[0] = (byte)Msg.EntityDespawn;
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(1), id);
        outBuf[5] = (byte)(becameBlocks ? 1 : 0);
        return outBuf;
    }

    public static (uint Id, bool BecameBlocks) DecodeEntityDespawn(ReadOnlySpan<byte> message)
        => (BinaryPrimitives.ReadUInt32LittleEndian(message[1..]), message[5] == 1);

    // ---- TimeSync -------------------------------------------------------------

    public static byte[] EncodeTimeSync(long worldTick, float timescale, int dayLengthTicks)
    {
        var outBuf = new byte[17];
        outBuf[0] = (byte)Msg.TimeSync;
        BinaryPrimitives.WriteInt64LittleEndian(outBuf.AsSpan(1), worldTick);
        BinaryPrimitives.WriteSingleLittleEndian(outBuf.AsSpan(9), timescale);
        BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(13), dayLengthTicks);
        return outBuf;
    }

    public static (long WorldTick, float Timescale, int DayLengthTicks) DecodeTimeSync(ReadOnlySpan<byte> message)
    {
        return (
            BinaryPrimitives.ReadInt64LittleEndian(message[1..]),
            BinaryPrimitives.ReadSingleLittleEndian(message[9..]),
            BinaryPrimitives.ReadInt32LittleEndian(message[13..]));
    }

    // ---- TimeControl (client → server: debug menu) ------------------------------

    public static byte[] EncodeTimeControl(long setTick, float timescale)
    {
        var outBuf = new byte[13];
        outBuf[0] = (byte)Msg.TimeControl;
        BinaryPrimitives.WriteInt64LittleEndian(outBuf.AsSpan(1), setTick);
        BinaryPrimitives.WriteSingleLittleEndian(outBuf.AsSpan(9), timescale);
        return outBuf;
    }

    public static (long SetTick, float Timescale) DecodeTimeControl(ReadOnlySpan<byte> message)
    {
        return (
            BinaryPrimitives.ReadInt64LittleEndian(message[1..]),
            BinaryPrimitives.ReadSingleLittleEndian(message[9..]));
    }

    // ---- PlayerMoves ----------------------------------------------------------

    public static byte[] EncodePlayerMoves(IReadOnlyList<PlayerMove> players)
    {
        var outBuf = new byte[3 + players.Count * 22];
        outBuf[0] = (byte)Msg.PlayerMoves;
        BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(1), (ushort)players.Count);
        for (int i = 0; i < players.Count; i++)
        {
            int o = 3 + i * 22;
            PlayerMove p = players[i];
            BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(o), p.Id);
            BinaryPrimitives.WriteSingleLittleEndian(outBuf.AsSpan(o + 2), p.X);
            BinaryPrimitives.WriteSingleLittleEndian(outBuf.AsSpan(o + 6), p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(outBuf.AsSpan(o + 10), p.Z);
            BinaryPrimitives.WriteSingleLittleEndian(outBuf.AsSpan(o + 14), p.Yaw);
            BinaryPrimitives.WriteSingleLittleEndian(outBuf.AsSpan(o + 18), p.Pitch);
        }
        return outBuf;
    }

    public static PlayerMove[] DecodePlayerMoves(ReadOnlySpan<byte> message)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(message[1..]);
        var players = new PlayerMove[count];
        for (int i = 0; i < count; i++)
        {
            int o = 3 + i * 22;
            players[i] = new PlayerMove(
                BinaryPrimitives.ReadUInt16LittleEndian(message[o..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 2)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 6)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 10)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 14)..]),
                BinaryPrimitives.ReadSingleLittleEndian(message[(o + 18)..]));
        }
        return players;
    }
}
