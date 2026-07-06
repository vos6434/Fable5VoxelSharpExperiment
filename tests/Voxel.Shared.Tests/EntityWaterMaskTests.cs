using Voxel.Client;

namespace Voxel.Shared.Tests;

/// <summary>
/// The water lid masks world water seen through an open deck. It only covers columns the hull
/// truly encloses in the XZ footprint — an open-topped ring counts, but a ring with a gap (open
/// to the sea) and a solid-floored hull (water already hidden by the floor) do not.
/// </summary>
public class EntityWaterMaskTests
{
    private const ushort P = 2;

    /// <summary>dx x dy x dz grid; builder sets the blocks.</summary>
    private static ushort[] Grid(int dx, int dy, int dz, Action<int, int, int, ushort[]> fill)
    {
        var b = new ushort[dx * dy * dz];
        fill(dx, dy, dz, b);
        return b;
    }

    [Fact]
    public void Open_ring_hull_encloses_its_interior()
    {
        const int dx = 5, dy = 1, dz = 5;
        var blocks = Grid(dx, dy, dz, (x, y, z, b) =>
        {
            for (int zz = 0; zz < dz; zz++)
            for (int xx = 0; xx < dx; xx++)
                if (xx == 0 || xx == dx - 1 || zz == 0 || zz == dz - 1)
                    b[(0 * dz + zz) * dx + xx] = P;
        });

        var result = EntityRenderer.ComputeInteriorColumns(blocks, dx, dy, dz);
        Assert.NotNull(result);
        var (interior, deckY) = result.Value;

        // The 3x3 inside the ring is enclosed; the ring itself and outside are not.
        Assert.True(interior[2 * dx + 2], "center should be interior");
        Assert.False(interior[0], "corner wall is not interior");
        Assert.Equal(9, interior.Count(v => v));
        Assert.Equal(1, deckY); // one above the 1-tall wall (top y=0)
    }

    [Fact]
    public void Ring_with_a_gap_encloses_nothing()
    {
        const int dx = 5, dy = 1, dz = 5;
        var blocks = Grid(dx, dy, dz, (x, y, z, b) =>
        {
            for (int zz = 0; zz < dz; zz++)
            for (int xx = 0; xx < dx; xx++)
                if (xx == 0 || xx == dx - 1 || zz == 0 || zz == dz - 1)
                    b[(0 * dz + zz) * dx + xx] = P;
            b[(0 * dz + 0) * dx + 2] = 0; // punch a hole in the wall -> interior leaks to the sea
        });

        Assert.Null(EntityRenderer.ComputeInteriorColumns(blocks, dx, dy, dz));
    }

    [Fact]
    public void Solid_floored_hull_has_no_open_interior()
    {
        const int dx = 4, dy = 2, dz = 4;
        // Full floor at y=0 (every column has a block), walls at y=1: no empty column exists.
        var blocks = Grid(dx, dy, dz, (x, y, z, b) =>
        {
            for (int zz = 0; zz < dz; zz++)
            for (int xx = 0; xx < dx; xx++)
                b[(0 * dz + zz) * dx + xx] = P;
        });

        Assert.Null(EntityRenderer.ComputeInteriorColumns(blocks, dx, dy, dz));
    }

    [Fact]
    public void Deck_height_ignores_a_tall_mast()
    {
        const int dx = 5, dy = 8, dz = 5;
        var blocks = Grid(dx, dy, dz, (x, y, z, b) =>
        {
            // 1-tall ring wall.
            for (int zz = 0; zz < dz; zz++)
            for (int xx = 0; xx < dx; xx++)
                if (xx == 0 || xx == dx - 1 || zz == 0 || zz == dz - 1)
                    b[(0 * dz + zz) * dx + xx] = P;
            // Tall mast in the middle (a wall column bordering the interior).
            for (int yy = 0; yy < dy; yy++)
                b[(yy * dz + 2) * dx + 2] = P;
        });

        var result = EntityRenderer.ComputeInteriorColumns(blocks, dx, dy, dz);
        Assert.NotNull(result);
        // Lid rides on the short ring (top y=0), not the mast (top y=7).
        Assert.Equal(1, result.Value.DeckY);
    }
}
