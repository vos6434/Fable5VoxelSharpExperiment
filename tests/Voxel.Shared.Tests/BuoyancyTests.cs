using System.Numerics;
using Voxel.Server;
using Voxel.Shared;

namespace Voxel.Shared.Tests;

/// <summary>
/// Headless buoyancy/stability checks: a boat dropped into a flat ocean must float with its
/// deck above the waterline and stay upright even when it carries a tall, heavy mast and sail.
/// Water fills y &lt;= SeaLevel (0), so the surface plane sits at y = 1.
/// </summary>
public class BuoyancyTests
{
    private const ushort Water = 1;
    private const ushort Plank = 2;
    private const float Dt = 1f / 20f;
    private const float Surface = WorldGen.SeaLevel + 1; // top of the water column

    private static PhysicsWorld NewOceanWorld()
    {
        var world = new PhysicsWorld();
        // Open ocean: no terrain colliders (getChunk null); getBlock reports water below/at
        // sea level and air above so the buoyancy scan finds a surface at y = 1.
        world.SetTerrainSource(
            getChunk: (_, _, _) => null,
            getBlock: (_, y, _) => y <= WorldGen.SeaLevel ? Water : (ushort)0,
            collidesTable: [0, 0, 0]);
        world.SetWaterTable([0, 1, 0]); // id 1 (Water) is liquid
        return world;
    }

    /// <summary>
    /// A proper boat: a 3-deep hull (plank floor + two wall layers around an open, air-filled
    /// interior) carrying a tall central mast and a wide raised sail — deliberately top-heavy,
    /// like a player-built sailboat.
    /// </summary>
    private static (int DimX, int DimY, int DimZ, ushort[] Blocks) BuildSailboat()
    {
        const int dx = 5, dy = 10, dz = 5;
        var blocks = new ushort[dx * dy * dz];
        int I(int x, int y, int z) => (y * dz + z) * dx + x;

        // y=0: solid plank floor (traps the buoyant air in the hull above it).
        for (int z = 0; z < dz; z++)
        for (int x = 0; x < dx; x++)
            blocks[I(x, 0, z)] = Plank;

        // y=1..2: perimeter walls (open interior = trapped air, deck top at y=3).
        for (int y = 1; y <= 2; y++)
        for (int z = 0; z < dz; z++)
        for (int x = 0; x < dx; x++)
            if (x == 0 || x == dx - 1 || z == 0 || z == dz - 1)
                blocks[I(x, y, z)] = Plank;

        // Tall central mast.
        for (int y = 3; y <= 8; y++)
            blocks[I(2, y, 2)] = Plank;

        // Wide raised sail panel (top-heavy mass).
        for (int y = 6; y <= 8; y++)
        for (int x = 0; x < dx; x++)
            blocks[I(x, y, 2)] = Plank;

        return (dx, dy, dz, blocks);
    }

    private static Vector3 Up(Quaternion rot) => Vector3.Transform(Vector3.UnitY, rot);

    /// <summary>World-space Y of the highest hull block whose column has no block above it (the deck).</summary>
    private static (float MinSolidY, float MaxSolidY) SolidExtent(
        PhysicsWorld.Entity e, Vector3 pos, Quaternion rot)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < e.DimY; y++)
        for (int z = 0; z < e.DimZ; z++)
        for (int x = 0; x < e.DimX; x++)
        {
            if (e.Blocks[(y * e.DimZ + z) * e.DimX + x] == 0) continue;
            var local = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - e.Pivot;
            float wy = (Vector3.Transform(local, rot) + pos).Y;
            min = MathF.Min(min, wy - 0.5f);
            max = MathF.Max(max, wy + 0.5f);
        }
        return (min, max);
    }

    private static PhysicsWorld.Entity SettleBoat(PhysicsWorld world, int settleTicks = 400)
    {
        var (dx, dy, dz, blocks) = BuildSailboat();
        var entity = world.SpawnContraption(0, 3, 0, dx, dy, dz, blocks);
        for (int i = 0; i < settleTicks; i++) world.Step(Dt);
        return entity;
    }

    [Fact]
    public void Sailboat_floats_deck_above_water()
    {
        using var world = NewOceanWorld();
        var entity = SettleBoat(world);

        var (pos, rot, _) = world.GetState(entity);
        Assert.True(Up(rot).Y > 0.98f, $"boat not level: up.Y = {Up(rot).Y:0.###}");

        // The hull is 3 blocks deep; the deck must ride above the waterline (some freeboard).
        var (minSolid, _) = SolidExtent(entity, pos, rot);
        float deckTop = minSolid + 3f; // floor + two wall layers
        Assert.True(deckTop > Surface,
            $"deck sits underwater: deckTop = {deckTop:0.##}, surface = {Surface:0.##}");
        Assert.True(minSolid < Surface, "hull bottom should be submerged (it is floating, not flying)");
    }

    [Fact]
    public void Sailboat_rights_itself_after_a_roll()
    {
        using var world = NewOceanWorld();
        var entity = SettleBoat(world);

        // A hard shove — like a wave or a shoulder-check — must not capsize it.
        world.Nudge(entity, new Vector3(0, 0, 2.0f));
        for (int i = 0; i < 400; i++) world.Step(Dt);

        var (_, rot, _) = world.GetState(entity);
        Assert.True(Up(rot).Y > 0.98f, $"boat failed to right itself: up.Y = {Up(rot).Y:0.###}");
    }

    [Fact]
    public void Sailboat_settles_to_rest()
    {
        using var world = NewOceanWorld();
        var entity = SettleBoat(world, settleTicks: 600);

        var (_, _, vel) = world.GetState(entity);
        Assert.True(vel.Length() < 0.05f, $"boat never came to rest: |v| = {vel.Length():0.###}");
    }
}
