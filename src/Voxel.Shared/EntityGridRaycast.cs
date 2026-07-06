using System.Numerics;

namespace Voxel.Shared;

/// <summary>Raycast against a contraption's local block grid (after transforming the ray).</summary>
public static class EntityGridRaycast
{
    public readonly record struct Hit(int LocalX, int LocalY, int LocalZ, int Nx, int Ny, int Nz, double Distance);

    public static Hit? CastLocal(
        double ox, double oy, double oz,
        double dx, double dy, double dz,
        double maxDistance,
        int dimX, int dimY, int dimZ,
        ushort[] blocks,
        Func<ushort, bool> isTargetable)
    {
        var hit = Raycast.Cast(ox, oy, oz, dx, dy, dz, maxDistance, (x, y, z) =>
        {
            if (x < 0 || y < 0 || z < 0 || x >= dimX || y >= dimY || z >= dimZ) return false;
            return isTargetable(blocks[(y * dimZ + z) * dimX + x]);
        });
        return hit is null ? null : new Hit(hit.Value.X, hit.Value.Y, hit.Value.Z, hit.Value.Nx, hit.Value.Ny, hit.Value.Nz, hit.Value.Distance);
    }

    public static Vector3 WorldToLocal(Vector3 world, Vector3 bodyPos, Quaternion rot, Vector3 pivot)
        => Vector3.Transform(world - bodyPos, Quaternion.Conjugate(rot)) + pivot;

    public static Vector3 LocalDirection(Vector3 worldDir, Quaternion rot)
    {
        var local = Vector3.Transform(worldDir, Quaternion.Conjugate(rot));
        float len = local.Length();
        return len > 1e-6f ? local / len : local;
    }

    public static Vector3 LocalPointToWorld(Vector3 local, Vector3 bodyPos, Quaternion rot, Vector3 pivot)
        => bodyPos + Vector3.Transform(local - pivot, rot);
}
