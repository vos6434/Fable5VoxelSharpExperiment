using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Voxel.Shared;

namespace Voxel.Server;

public sealed partial class PhysicsWorld
{
    private const float EntityReach = 6f;
    private const int MaxContraptionBlocks = 1000;

    public readonly record struct EntityRaycastHit(uint EntityId, int LocalX, int LocalY, int LocalZ, int Nx, int Ny, int Nz, float Distance);

    /// <summary>Voxel raycast against all entity block grids; returns the nearest hit within reach.</summary>
    public bool TryRaycastEntity(Vector3 eye, Vector3 rayDir, Func<ushort, bool> isTargetable, out EntityRaycastHit hit)
    {
        hit = default;
        float best = float.MaxValue;
        bool found = false;

        foreach (var entity in _entities.Values)
        {
            var body = _sim.Bodies[entity.Body];
            var pos = body.Pose.Position;
            var rot = body.Pose.Orientation;
            var localEye = EntityGridRaycast.WorldToLocal(eye, pos, rot, entity.Pivot);
            var localDir = EntityGridRaycast.LocalDirection(rayDir, rot);

            var localHit = EntityGridRaycast.CastLocal(
                localEye.X, localEye.Y, localEye.Z,
                localDir.X, localDir.Y, localDir.Z,
                EntityReach,
                entity.DimX, entity.DimY, entity.DimZ,
                entity.Blocks, isTargetable);
            if (localHit is null) continue;

            float dist = (float)localHit.Value.Distance;
            if (dist >= best) continue;
            best = dist;
            hit = new EntityRaycastHit(entity.Id, localHit.Value.LocalX, localHit.Value.LocalY, localHit.Value.LocalZ,
                localHit.Value.Nx, localHit.Value.Ny, localHit.Value.Nz, dist);
            found = true;
        }
        return found;
    }

    /// <summary>Place or break a block in an entity grid; rebuilds the physics compound. Returns false if entity was removed.</summary>
    public bool TryEditEntityBlock(uint entityId, int lx, int ly, int lz, ushort blockId, Func<ushort, bool> isSolidBlock, out bool entityRemoved)
    {
        entityRemoved = false;
        if (!_entities.TryGetValue(entityId, out var entity)) return false;

        if (blockId == 0)
        {
            if (!InGrid(entity, lx, ly, lz)) return false;
            if (entity.Blocks[Index(entity, lx, ly, lz)] == 0) return false;
        }
        else if (!isSolidBlock(blockId)) return false;

        var body = _sim.Bodies[entity.Body];
        var pos = body.Pose.Position;
        var rot = body.Pose.Orientation;
        var velocity = body.Velocity;
        var pivot = entity.Pivot;

        if (!TryFindAnchor(entity, pos, rot, pivot, out int anchorX, out int anchorY, out int anchorZ, out Vector3 anchorWorld))
            return false;

        if (!TryBuildEditedGrid(entity, lx, ly, lz, blockId, anchorX, anchorY, anchorZ,
                out ushort[] newBlocks, out int newDimX, out int newDimY, out int newDimZ,
                out int newAnchorX, out int newAnchorY, out int newAnchorZ))
            return false;

        int solidCount = CountSolid(newBlocks);
        if (solidCount == 0)
        {
            Remove(entityId);
            entityRemoved = true;
            return true;
        }

        entity.DimX = (ushort)newDimX;
        entity.DimY = (ushort)newDimY;
        entity.DimZ = (ushort)newDimZ;
        entity.Blocks = newBlocks;

        var (compound, inertia, center) = BuildContraptionCompound(newDimX, newDimY, newDimZ, newBlocks);
        Vector3 newLocalAnchor = new(newAnchorX + 0.5f, newAnchorY + 0.5f, newAnchorZ + 0.5f);
        Vector3 newPos = anchorWorld - Vector3.Transform(newLocalAnchor - center, rot);

        ReleaseGrabsOnEntity(entityId);
        UntrackBody(entity);
        _sim.Bodies.Remove(entity.Body);

        var handle = _sim.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(newPos, rot), inertia,
            new CollidableDescription(compound, 0.1f, ContinuousDetection.Continuous(1e-3f, 1e-3f)),
            new BodyActivityDescription(0.01f)));
        var newBody = _sim.Bodies[handle];
        newBody.Velocity = velocity;
        newBody.Awake = true;

        entity.Body = handle;
        entity.Pivot = center;
        TrackBody(entity);
        return true;
    }

    private static int Index(Entity entity, int x, int y, int z) => (y * entity.DimZ + z) * entity.DimX + x;

    private static bool InGrid(Entity entity, int x, int y, int z) =>
        x >= 0 && y >= 0 && z >= 0 && x < entity.DimX && y < entity.DimY && z < entity.DimZ;

    private static int CountSolid(ushort[] blocks)
    {
        int count = 0;
        foreach (ushort id in blocks) if (id != 0) count++;
        return count;
    }

    private static bool TryBuildEditedGrid(
        Entity entity, int lx, int ly, int lz, ushort blockId,
        int anchorX, int anchorY, int anchorZ,
        out ushort[] newBlocks, out int newDimX, out int newDimY, out int newDimZ,
        out int newAnchorX, out int newAnchorY, out int newAnchorZ)
    {
        newBlocks = [];
        newDimX = newDimY = newDimZ = 0;
        newAnchorX = newAnchorY = newAnchorZ = 0;

        int padNegX = lx < 0 ? -lx : 0;
        int padNegY = ly < 0 ? -ly : 0;
        int padNegZ = lz < 0 ? -lz : 0;
        int padPosX = lx >= entity.DimX ? lx - entity.DimX + 1 : 0;
        int padPosY = ly >= entity.DimY ? ly - entity.DimY + 1 : 0;
        int padPosZ = lz >= entity.DimZ ? lz - entity.DimZ + 1 : 0;

        newDimX = entity.DimX + padNegX + padPosX;
        newDimY = entity.DimY + padNegY + padPosY;
        newDimZ = entity.DimZ + padNegZ + padPosZ;
        if ((long)newDimX * newDimY * newDimZ > MaxContraptionBlocks * 4) return false;

        newBlocks = new ushort[newDimX * newDimY * newDimZ];
        for (int y = 0; y < entity.DimY; y++)
        for (int z = 0; z < entity.DimZ; z++)
        for (int x = 0; x < entity.DimX; x++)
        {
            ushort id = entity.Blocks[Index(entity, x, y, z)];
            if (id == 0) continue;
            newBlocks[((y + padNegY) * newDimZ + (z + padNegZ)) * newDimX + (x + padNegX)] = id;
        }

        int nlx = lx + padNegX;
        int nly = ly + padNegY;
        int nlz = lz + padNegZ;
        int idx = (nly * newDimZ + nlz) * newDimX + nlx;

        if (blockId == 0)
        {
            if (newBlocks[idx] == 0) return false;
            newBlocks[idx] = 0;
        }
        else
        {
            if (newBlocks[idx] != 0) return false;
            newBlocks[idx] = blockId;
            if (CountSolid(newBlocks) > MaxContraptionBlocks) return false;
        }

        newAnchorX = anchorX + padNegX;
        newAnchorY = anchorY + padNegY;
        newAnchorZ = anchorZ + padNegZ;
        return true;
    }

    private static bool TryFindAnchor(
        Entity entity, Vector3 pos, Quaternion rot, Vector3 pivot,
        out int ax, out int ay, out int az, out Vector3 anchorWorld)
    {
        ax = ay = az = 0;
        anchorWorld = default;
        for (int y = 0; y < entity.DimY; y++)
        for (int z = 0; z < entity.DimZ; z++)
        for (int x = 0; x < entity.DimX; x++)
        {
            if (entity.Blocks[Index(entity, x, y, z)] == 0) continue;
            ax = x; ay = y; az = z;
            anchorWorld = EntityGridRaycast.LocalPointToWorld(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), pos, rot, pivot);
            return true;
        }
        return false;
    }
}
