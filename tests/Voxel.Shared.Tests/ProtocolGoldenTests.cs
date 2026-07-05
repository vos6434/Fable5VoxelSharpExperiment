using Voxel.Shared;

namespace Voxel.Shared.Tests;

public class ProtocolGoldenTests
{
    [Fact]
    public void ChunkRequest_bytes_match_typescript()
    {
        byte[] encoded = Protocol.EncodeChunkRequest([(1, -2, 3), (-100000, 200000, -300000), (0, 0, 0)]);
        Assert.Equal(Golden.Instance.Protocol["chunkRequest"], encoded);

        var decoded = Protocol.DecodeChunkRequest(Golden.Instance.Protocol["chunkRequest"]);
        Assert.Equal([(1, -2, 3), (-100000, 200000, -300000), (0, 0, 0)], decoded);
    }

    [Fact]
    public void Move_bytes_match_typescript()
    {
        byte[] encoded = Protocol.EncodeMove(123.456789, -64.25, 987654.321, 1.5707963f, -0.25f);
        Assert.Equal(Golden.Instance.Protocol["move"], encoded);

        var decoded = Protocol.DecodeMove(Golden.Instance.Protocol["move"]);
        Assert.Equal(123.456789, decoded.X);
        Assert.Equal(-64.25, decoded.Y);
        Assert.Equal(987654.321, decoded.Z);
        Assert.Equal(1.5707963f, decoded.Yaw);
        Assert.Equal(-0.25f, decoded.Pitch);
    }

    [Fact]
    public void BlockChange_bytes_match_typescript()
    {
        Assert.Equal(
            Golden.Instance.Protocol["setBlock"],
            Protocol.EncodeBlockChange(Msg.SetBlock, -5, 129, 7, 42));
        Assert.Equal(
            Golden.Instance.Protocol["blockUpdate"],
            Protocol.EncodeBlockChange(Msg.BlockUpdate, 1, 2, 3, 0));

        var decoded = Protocol.DecodeBlockChange(Golden.Instance.Protocol["setBlock"]);
        Assert.Equal(new BlockChange(-5, 129, 7, 42), decoded);
    }

    [Fact]
    public void PlayerMoves_bytes_match_typescript()
    {
        PlayerMove[] moves =
        [
            new(1, 1.5f, 2.5f, -3.5f, 0.5f, -0.5f),
            new(65535, -1000.25f, 0f, 4096.75f, -3.14159f, 1.5f),
        ];
        Assert.Equal(Golden.Instance.Protocol["playerMoves"], Protocol.EncodePlayerMoves(moves));
        Assert.Equal(moves, Protocol.DecodePlayerMoves(Golden.Instance.Protocol["playerMoves"]));
    }

    [Fact]
    public void TimeControl_round_trips()
    {
        byte[] encoded = Protocol.EncodeTimeControl(123456789L, 2.5f);
        Assert.Equal(Msg.TimeControl, Protocol.TypeOf(encoded));
        Assert.Equal(13, encoded.Length);
        Assert.Equal((123456789L, 2.5f), Protocol.DecodeTimeControl(encoded));

        // Sentinels: -1 tick = no change, negative timescale = no change.
        Assert.Equal((-1L, -1f), Protocol.DecodeTimeControl(Protocol.EncodeTimeControl(-1, -1f)));
    }

    [Fact]
    public void Json_messages_round_trip()
    {
        var welcome = new WelcomePayload
        {
            PlayerId = 7,
            Seed = 1337,
            Spawn = new SpawnPoint { X = -16, Y = 30, Z = -32 },
            Palette = ["air", "stone"],
        };
        byte[] encoded = Protocol.EncodeJson(Msg.Welcome, welcome);
        Assert.Equal(Msg.Welcome, Protocol.TypeOf(encoded));
        var decoded = Protocol.DecodeJson<WelcomePayload>(encoded);
        Assert.Equal(welcome.PlayerId, decoded.PlayerId);
        Assert.Equal(welcome.Seed, decoded.Seed);
        Assert.Equal(welcome.Spawn.Y, decoded.Spawn.Y);
        Assert.Equal(welcome.Palette, decoded.Palette);

        // Wire-shape check against a hand-written JS-style payload.
        byte[] jsStyle = [(byte)Msg.PlayerJoin, .. "{\"id\":3,\"name\":\"steve\"}"u8];
        var join = Protocol.DecodeJson<PlayerJoinPayload>(jsStyle);
        Assert.Equal(3, join.Id);
        Assert.Equal("steve", join.Name);
    }
}
