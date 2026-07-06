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
    private const float HeightSpring = 6f;
    private const float HeightDamp = 4f;
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
            if (!TryApplyBuoyancy(entity, body, dt, out float submerged)) continue;

            body.Awake = true;
            ApplyHeightSpring(entity, body, submerged, dt);
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
    /// Net lift from average wet fraction across all solid blocks (dry upper hull no longer
    /// sinks the whole contraption). Impulse is scaled by body mass and applied at the
    /// submerged-volume centroid for righting torque when tilted.
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
        for (int y = 0; y < dimY; y++)
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            if (entity.Blocks[(y * dimZ + z) * dimX + x] == 0) continue;
            solidBlocks++;
            var local = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - pivot;
            var world = Vector3.Transform(local, rot) + pos;
            float sub = BlockSubmergence(world);
            submergedSum += sub;
            if (sub <= 0f) continue;
            weighted += world * sub;
            weightTotal += sub;
        }

        if (solidBlocks == 0 || submergedSum <= 0f) return false;
        submergedRatio = submergedSum / solidBlocks;

        float mass = 1f / body.LocalInertia.InverseMass;
        float netUp = Gravity * (submergedRatio - RelativeBlockDensity + 1f) * dt;
        Vector3 buoyancyCenter = weighted / weightTotal;
        body.ApplyImpulse(Vector3.UnitY * (netUp * mass), buoyancyCenter - pos);
        return true;
    }

    /// <summary>Gentle draft spring using solid-block vertical extent (ignores hollow air).</summary>
    private void ApplyHeightSpring(Entity entity, BodyReference body, float submerged, float dt)
    {
        var pos = body.Pose.Position;
        if (!TryGetWaterSurface((int)MathF.Floor(pos.X), (int)MathF.Floor(pos.Z), out float surface)) return;
        if (!TryGetSolidVerticalExtent(entity, body, out float solidMinY, out float solidMaxY)) return;

        float hullHeight = MathF.Max(0.5f, solidMaxY - solidMinY);
        float targetMinY = surface - hullHeight * RelativeBlockDensity;
        float error = targetMinY - solidMinY;
        float vy = body.Velocity.Linear.Y;
        body.Velocity.Linear += Vector3.UnitY * ((HeightSpring * error - HeightDamp * vy) * submerged * dt);
    }

    private static bool TryGetSolidVerticalExtent(Entity entity, BodyReference body, out float minY, out float maxY)
    {
        minY = float.MaxValue;
        maxY = float.MinValue;
        var pivot = entity.Pivot;
        var rot = body.Pose.Orientation;
        var pos = body.Pose.Position;
        int dimX = entity.DimX, dimY = entity.DimY, dimZ = entity.DimZ;

        for (int y = 0; y < dimY; y++)
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            if (entity.Blocks[(y * dimZ + z) * dimX + x] == 0) continue;
            var local = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - pivot;
            float wy = (Vector3.Transform(local, rot) + pos).Y;
            minY = MathF.Min(minY, wy - 0.5f);
            maxY = MathF.Max(maxY, wy + 0.5f);
        }

        return minY < maxY;
    }

    /// <summary>PD torque: level grid +Y toward world up without overshooting.</summary>
    private static void ApplyUprightTorque(BodyReference body, float submerged, float dt)
    {
        Vector3 bodyUp = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
        Vector3 axis = Vector3.Cross(bodyUp, Vector3.UnitY);
        float sin = axis.Length();
        if (sin < 1e-4f) return;
        axis /= sin;
        float tiltDamp = Vector3.Dot(body.Velocity.Angular, axis);
        float torque = UprightStrength * sin - UprightDamp * tiltDamp;
        body.ApplyAngularImpulse(axis * (torque * submerged * dt));
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
        ushort id = GetWorldBlock(wx, wy, wz);
        return id != 0 && id < _water.Length && _water[id] != 0;
    }

    private ushort GetWorldBlock(int wx, int wy, int wz)
    {
        ushort[]? chunk = _getChunk!(
            Coords.WorldToChunk(wx), Coords.WorldToChunk(wy), Coords.WorldToChunk(wz));
        if (chunk is null) return 0;
        return chunk[ChunkData.Index(
            Coords.WorldToLocal(wx), Coords.WorldToLocal(wy), Coords.WorldToLocal(wz))];
    }
}
