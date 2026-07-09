using Voxel.Client;

namespace Voxel.Shared.Tests;

/// <summary>
/// The water mask plugs the world sea out of a boat's footprint. It covers every column the hull
/// encloses — walls, floor, and open cockpit — because a floored boat has world water sitting in
/// the cockpit above the floor. Open ocean around the hull must stay unmasked.
/// </summary>
public class EntityWaterMaskTests
{
    private const ushort P = 2;

    private static ushort[] Grid(int dx, int dy, int dz, Action<int, int, int, ushort[]> fill)
    {
        var b = new ushort[dx * dy * dz];
        fill(dx, dy, dz, b);
        return b;
    }

    /// <summary>A 5x5 boat (floor + 2-high perimeter walls) centred in a 7x7 grid with a sea margin.</summary>
    private static ushort[] FlooredBoat(int dx = 7, int dy = 4, int dz = 7)
    {
        return Grid(dx, dy, dz, (_, _, _, b) =>
        {
            int I(int x, int y, int z) => (y * dz + z) * dx + x;
            for (int z = 1; z <= 5; z++)
            for (int x = 1; x <= 5; x++)
            {
                b[I(x, 0, z)] = P; // floor
                if (x == 1 || x == 5 || z == 1 || z == 5)
                    for (int y = 1; y <= 2; y++) b[I(x, y, z)] = P; // walls
            }
        });
    }

    [Fact]
    public void Floored_cockpit_and_hull_are_masked()
    {
        var result = EntityRenderer.ComputeInteriorColumns(FlooredBoat(), 7, 4, 7);
        Assert.NotNull(result);
        var (mask, deckY, floorAir) = result.Value;

        Assert.True(mask[3 * 7 + 3], "cockpit centre (floor below, water above) must be masked");
        Assert.True(mask[1 * 7 + 1], "hull wall column must be masked");
        Assert.Equal(3, deckY);          // one above the top wall course (y=2)
        Assert.Equal(1, floorAir[3 * 7 + 3]); // cockpit air starts above the floor block (y=0)
        Assert.Equal(3, floorAir[1 * 7 + 1]); // wall column is solid to the rim (carves nothing)
    }

    [Fact]
    public void Open_ocean_around_the_hull_is_not_masked()
    {
        var result = EntityRenderer.ComputeInteriorColumns(FlooredBoat(), 7, 4, 7);
        Assert.NotNull(result);
        var (mask, _, _) = result.Value;

        Assert.False(mask[0 * 7 + 0], "corner outside the hull is open sea");
        Assert.False(mask[3 * 7 + 0], "cell beside the hull is open sea");
    }

    [Fact]
    public void Concave_notch_in_the_hull_is_still_masked()
    {
        // A 1-wide step cut into a wall — morphological close seals the channel so the pocket masks.
        const int dx = 9, dy = 1, dz = 9;
        var blocks = Grid(dx, dy, dz, (_, _, _, b) =>
        {
            int I(int x, int z) => (0 * dz + z) * dx + x;
            for (int z = 1; z <= 7; z++)
            for (int x = 1; x <= 7; x++)
                if (x == 1 || x == 7 || z == 1 || z == 7) b[I(x, z)] = P;
            b[I(4, 1)] = 0;      // notch: remove one wall cell
            b[I(4, 2)] = P;      // ...and step it inward
        });

        var result = EntityRenderer.ComputeInteriorColumns(blocks, dx, dy, dz);
        Assert.NotNull(result);
        var (mask, _, _) = result.Value;
        Assert.True(mask[4 * dx + 4], "hull interior masked");
        Assert.True(mask[1 * dx + 4], "the concave notch pocket masked, not left as water");
    }

    [Fact]
    public void Empty_grid_masks_nothing()
    {
        Assert.Null(EntityRenderer.ComputeInteriorColumns(new ushort[3 * 3 * 3], 3, 3, 3));
    }

    [Fact]
    public void Rect_decomposition_covers_the_mask_exactly()
    {
        // L-shaped mask: 4x2 block plus a 2x2 tail — greedy cover, no overlap, full coverage.
        const int dx = 6, dz = 4;
        var mask = new bool[dx * dz];
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 4; x++) mask[z * dx + x] = true;
        for (int z = 2; z < 4; z++)
        for (int x = 2; x < 4; x++) mask[z * dx + x] = true;

        var rects = EntityRenderer.DecomposeRects(mask, new int[dx * dz], deckY: 1, dx, dz);

        var covered = new bool[dx * dz];
        foreach (var (x0, z0, w, d, _) in rects)
            for (int z = z0; z < z0 + d; z++)
            for (int x = x0; x < x0 + w; x++)
            {
                Assert.False(covered[z * dx + x], $"cell ({x},{z}) covered twice");
                covered[z * dx + x] = true;
            }
        Assert.Equal(mask, covered);
    }

    [Fact]
    public void Stepped_hull_carves_each_floor_level_from_its_own_height()
    {
        // A stepped V-hull cross-section: keel row at floorAir 1, a step at 2, walls at deck.
        const int dx = 5, dz = 1, deckY = 4;
        bool[] mask = [true, true, true, true, true];
        int[] floorAir = [4, 2, 1, 2, 4]; // walls | step | keel | step | walls

        var rects = EntityRenderer.DecomposeRects(mask, floorAir, deckY, dx, dz);

        Assert.Equal(3, rects.Count); // keel rect + two step rects; wall columns carve nothing
        Assert.Contains((2, 0, 1, 1, 1), rects);  // keel: carve from y=1
        Assert.Contains((1, 0, 1, 1, 2), rects);  // left step: carve from y=2
        Assert.Contains((3, 0, 1, 1, 2), rects);  // right step: carve from y=2
    }

    [Fact]
    public void Deck_tracks_the_hull_rim_not_the_mast()
    {
        // Floor + 2-high walls + a tall central mast: the plug tops out at the gunwale (y=2),
        // not the mast (y=8), so the mask doesn't tower above the hull.
        const int dx = 7, dy = 10, dz = 7;
        var blocks = Grid(dx, dy, dz, (_, _, _, b) =>
        {
            int I(int x, int y, int z) => (y * dz + z) * dx + x;
            for (int z = 1; z <= 5; z++)
            for (int x = 1; x <= 5; x++)
            {
                b[I(x, 0, z)] = P; // floor
                if (x == 1 || x == 5 || z == 1 || z == 5)
                    for (int y = 1; y <= 2; y++) b[I(x, y, z)] = P; // walls
            }
            for (int y = 1; y <= 8; y++) b[I(3, y, 3)] = P; // tall mast (interior)
        });

        var result = EntityRenderer.ComputeInteriorColumns(blocks, dx, dy, dz);
        Assert.NotNull(result);
        Assert.Equal(3, result.Value.DeckY); // one above the wall top (y=2), ignoring the mast
    }
}
