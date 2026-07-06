using System.Numerics;
using BepuPhysics;
using Voxel.Shared;

namespace Voxel.Server;

public sealed partial class PhysicsWorld
{
    private const float Gravity = 20f;
    private const float WaterDrag = 4f;
    private const float VerticalWaterDrag = 8f;
    /// <summary>Average wet fraction at equilibrium; lower = more hull above the waterline.</summary>
    private const float RelativeBlockDensity = 0.35f;
    private const float UprightStrength = 10f;
    private const float UprightDamp = 5f;
    private const int WaterScanMinY = WorldGen.SeaLevel - 48;

    private byte[] _water = [];
    private readonly Dictionary<(int X, int Z), float> _surfaceScratch = new();

    public void SetWaterTable(byte[] waterTable) => _water = waterTable;

    private void ApplyBuoyancy(float dt)
    {
        if (_getChunk is null || _water.Length == 0) return;

        _surfaceScratch.Clear();
        foreach (var entity in _entities.Values)
        {
            var body = _sim.Bodies[entity.Body];
            // Let calm floating bodies sleep — a sleeping body keeps its pose, so it stays
            // afloat without us re-applying lift every tick. Bepu wakes it on any contact/grab.
            if (!body.Awake) continue;
            if (!TryApplyBuoyancy(entity, body, dt, out float submerged)) continue;

            ApplyUprightTorque(body, submerged, dt);

            float horizDamp = 1f / (1f + WaterDrag * submerged * dt);
            float vertDamp = 1f / (1f + VerticalWaterDrag * submerged * dt);
            var vel = body.Velocity;
            vel.Linear = new Vector3(vel.Linear.X * horizDamp, vel.Linear.Y * vertDamp, vel.Linear.Z * horizDamp);
            body.Velocity = vel;
            body.Velocity.Angular *= MathF.Sqrt(horizDamp);
        }
    }

    /// <summary>
    /// Net lift from displaced volume vs. weight. Displacement includes both solid blocks and
    /// the air enclosed above a hull floor (a boat floats on its trapped air, not just its
    /// planks), while weight is proportional to the solid-block count only. Impulse is scaled by
    /// body mass and applied at the submerged-volume centroid for righting torque when tilted.
    /// </summary>
    private bool TryApplyBuoyancy(Entity entity, BodyReference body, float dt, out float submergedRatio)
    {
        submergedRatio = 0f;
        int dimX = entity.DimX, dimY = entity.DimY, dimZ = entity.DimZ;
        var pivot = entity.Pivot;
        var rot = body.Pose.Orientation;
        var pos = body.Pose.Position;

        float submergedSum = 0f;
        int solidBlocks = 0;
        Vector3 weighted = Vector3.Zero;
        float weightTotal = 0f;
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            // Scan the column bottom-up: air only displaces once a solid floor sits below it,
            // so an open-bottomed hull floods (no lift) but a hull with a floor traps air.
            bool solidBelow = false;
            for (int y = 0; y < dimY; y++)
            {
                bool solid = entity.Blocks[(y * dimZ + z) * dimX + x] != 0;
                if (!solid && !solidBelow) continue;
                var local = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - pivot;
                var world = Vector3.Transform(local, rot) + pos;
                float sub = BlockSubmergence(world);
                submergedSum += sub;
                if (solid) { solidBlocks++; solidBelow = true; }
                if (sub <= 0f) continue;
                weighted += world * sub;
                weightTotal += sub;
            }
        }

        if (solidBlocks == 0 || submergedSum <= 0f) return false;
        submergedRatio = submergedSum / solidBlocks;

        float mass = 1f / body.LocalInertia.InverseMass;
        float netUp = Gravity * (submergedRatio - RelativeBlockDensity + 1f) * dt;
        Vector3 buoyancyCenter = weighted / weightTotal;
        body.ApplyImpulse(Vector3.UnitY * (netUp * mass), buoyancyCenter - pos);
        return true;
    }

    /// <summary>
    /// Critically-damped self-righting: drives the grid's +Y back to world up. Applied
    /// directly to angular velocity (not as an inertia-scaled impulse) so a tall, top-heavy
    /// mast can't overpower it and leave the boat leaning at a tilted equilibrium.
    /// </summary>
    private static void ApplyUprightTorque(BodyReference body, float submerged, float dt)
    {
        Vector3 bodyUp = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
        Vector3 axis = Vector3.Cross(bodyUp, Vector3.UnitY);
        float sin = axis.Length();
        if (sin < 1e-4f) return;
        axis /= sin;
        // Full tilt angle (0..π) so the correction keeps growing past 90° instead of fading.
        float angle = MathF.Atan2(sin, bodyUp.Y);
        float tiltRate = Vector3.Dot(body.Velocity.Angular, axis);
        float wet = MathF.Min(1f, submerged * 3f); // only right a hull that's actually in the water
        float accel = (UprightStrength * angle - UprightDamp * tiltRate) * wet;
        body.Velocity.Angular += axis * (accel * dt);
    }

    /// <summary>Fraction of a 1³ block below the local water-air surface.</summary>
    private float BlockSubmergence(Vector3 worldCenter)
    {
        int wx = (int)MathF.Floor(worldCenter.X);
        int wz = (int)MathF.Floor(worldCenter.Z);
        if (!TryGetWaterSurface(wx, wz, out float surface)) return 0f;

        float blockBottom = worldCenter.Y - 0.5f;
        float blockTop = worldCenter.Y + 0.5f;
        if (blockBottom >= surface) return 0f;
        return Math.Clamp(MathF.Min(blockTop, surface) - blockBottom, 0f, 1f);
    }

    private bool TryGetWaterSurface(int wx, int wz, out float surfaceY)
    {
        var key = (wx, wz);
        if (_surfaceScratch.TryGetValue(key, out surfaceY)) return !float.IsNegativeInfinity(surfaceY);

        surfaceY = float.NegativeInfinity;
        for (int y = WorldGen.SeaLevel; y >= WaterScanMinY; y--)
        {
            if (!IsWaterBlock(wx, y, wz)) continue;
            surfaceY = y + 1f;
            break;
        }

        _surfaceScratch[key] = surfaceY;
        return !float.IsNegativeInfinity(surfaceY);
    }

    private bool IsWaterBlock(int wx, int wy, int wz)
    {
        // Single-block read (no chunk clone) — the water-surface scan touches up
        // to ~48 blocks per column, so cloning a chunk per block would churn the GC.
        ushort id = _getBlock is not null ? _getBlock(wx, wy, wz) : (ushort)0;
        return id != 0 && id < _water.Length && _water[id] != 0;
    }
}
