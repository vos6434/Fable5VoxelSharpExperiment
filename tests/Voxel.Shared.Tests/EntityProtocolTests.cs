using Voxel.Shared;

namespace Voxel.Shared.Tests;

public class EntityProtocolTests
{
    [Fact]
    public void EntitySpawn_round_trips()
    {
        byte[] blocks = [1, 2, 3, 4];
        byte[] encoded = Protocol.EncodeEntitySpawn(
            42, 0, 2, 1, 2, -16.5, 20.0, -32.25, 0.1f, 0.2f, 0.3f, 0.9f, blocks);
        Assert.Equal(Msg.EntitySpawn, Protocol.TypeOf(encoded));

        var d = Protocol.DecodeEntitySpawn(encoded);
        Assert.Equal(42u, d.Id);
        Assert.Equal(0, d.Kind);
        Assert.Equal((ushort)2, d.DimX);
        Assert.Equal((ushort)1, d.DimY);
        Assert.Equal((ushort)2, d.DimZ);
        Assert.Equal(-16.5, d.X);
        Assert.Equal(20.0, d.Y);
        Assert.Equal(-32.25, d.Z);
        Assert.Equal(0.9f, d.Qw);
        Assert.Equal(blocks, d.CompressedBlocks.ToArray());
    }

    [Fact]
    public void EntityStates_round_trip()
    {
        Protocol.EntityState[] states =
        [
            new(1, 1.5f, 2.5f, -3.5f, 0f, 0f, 0f, 1f, 0.1f, -0.2f, 0.3f),
            new(65540, -1000.25f, 13f, 4096.75f, 0.5f, 0.5f, 0.5f, 0.5f, 0f, -20f, 0f),
        ];
        byte[] encoded = Protocol.EncodeEntityStates(states);
        Assert.Equal(Msg.EntityState, Protocol.TypeOf(encoded));

        var decoded = Protocol.DecodeEntityStates(encoded);
        Assert.Equal(states, decoded);
    }

    [Fact]
    public void EntityDespawn_round_trips()
    {
        byte[] encoded = Protocol.EncodeEntityDespawn(7, becameBlocks: true);
        Assert.Equal(Msg.EntityDespawn, Protocol.TypeOf(encoded));
        var (id, became) = Protocol.DecodeEntityDespawn(encoded);
        Assert.Equal(7u, id);
        Assert.True(became);
    }
}
