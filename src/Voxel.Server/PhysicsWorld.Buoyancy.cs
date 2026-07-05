using System.Numerics;
using BepuPhysics;
using Voxel.Shared;

namespace Voxel.Server;

public sealed partial class PhysicsWorld
{
    private const float Gravity = 20f;
    private const float WaterDrag = 2.5f;
    /// <summary>Contraption density relative to water; lower = rides higher on the surface.</summary>
    private const float RelativeBlockDensity = 0.55f;
    /// <summary>Extra torque that levels the grid's local +Y toward world up while floating.</summary>
    private const float UprightStrength = 18f;
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
            if (!TryApplyDistributedBuoyancy(entity, body, dt, out float submerged)) continue;

            body.Awake = true;
            ApplyUprightTorque(body, submerged, dt);

            float damp = 1f / (1f + WaterDrag * submerged * dt);
            var vel = body.Velocity;
            vel.Linear = new Vector3(vel.Linear.X * damp, vel.Linear.Y, vel.Linear.Z * damp);
            body.Velocity = vel;
            body.Velocity.Angular *= MathF.Sqrt(damp);
        }
    }

    /// <summary>
    /// Per-block buoyancy at each submerged voxel center (creates a righting torque when
    /// tilted) instead of a single upward shove at the center of mass.
    /// </summary>
    private bool TryApplyDistributedBuoyancy(Entity entity, BodyReference body, float dt, out float submergedRatio)
    {
        submergedRatio = 0f;
        int dimX = entity.DimX, dimY = entity.DimY, dimZ = entity.DimZ;
        var pivot = entity.Pivot;
        var rot = body.Pose.Orientation;
        var pos = body.Pose.Position;

        float submergedSum = 0f;
        int solidBlocks = 0;
        for (int y = 0; y < dimY; y++)
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            if (entity.Blocks[(y * dimZ + z) * dimX + x] == 0) continue;
            solidBlocks++;
            var local = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - pivot;
            var world = Vector3.Transform(local, rot) + pos;
            float sub = BlockSubmergence(world);
            if (sub <= 0f) continue;

            submergedSum += sub;
            float impulseMag = Gravity * (sub - RelativeBlockDensity + 1f) * dt;
            body.ApplyImpulse(Vector3.UnitY * impulseMag, world - pos);
        }

        if (solidBlocks == 0 || submergedSum <= 0f) return false;
        submergedRatio = submergedSum / solidBlocks;
        return true;
    }

    /// <summary>Grid-local +Y is "up" for glued contraptions; rotate it toward world up in water.</summary>
    private static void ApplyUprightTorque(BodyReference body, float submerged, float dt)
    {
        Vector3 bodyUp = Vector3.Transform(Vector3.UnitY, body.Pose.Orientation);
        Vector3 axis = Vector3.Cross(bodyUp, Vector3.UnitY);
        float sin = axis.Length();
        if (sin < 1e-4f) return;
        axis /= sin;
        body.ApplyAngularImpulse(axis * (UprightStrength * submerged * sin * dt));
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
