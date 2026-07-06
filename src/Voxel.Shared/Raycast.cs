namespace Voxel.Shared;

public readonly record struct RaycastHit(int X, int Y, int Z, int Nx, int Ny, int Nz, double Distance);

/// <summary>
/// Voxel DDA (Amanatides &amp; Woo): steps the ray cell-by-cell so targeting
/// is exact at block resolution.
/// </summary>
public static class Raycast
{
    public static RaycastHit? Cast(
        double ox, double oy, double oz,
        double dx, double dy, double dz,
        double maxDistance,
        Func<int, int, int, bool> isTargetable)
    {
        int x = (int)Math.Floor(ox);
        int y = (int)Math.Floor(oy);
        int z = (int)Math.Floor(oz);

        int stepX = dx > 0 ? 1 : -1;
        int stepY = dy > 0 ? 1 : -1;
        int stepZ = dz > 0 ? 1 : -1;

        double tDeltaX = dx != 0 ? Math.Abs(1 / dx) : double.PositiveInfinity;
        double tDeltaY = dy != 0 ? Math.Abs(1 / dy) : double.PositiveInfinity;
        double tDeltaZ = dz != 0 ? Math.Abs(1 / dz) : double.PositiveInfinity;

        static double Bound(double p, int cell, int step) => step > 0 ? cell + 1 - p : p - cell;
        double tMaxX = dx != 0 ? Bound(ox, x, stepX) * tDeltaX : double.PositiveInfinity;
        double tMaxY = dy != 0 ? Bound(oy, y, stepY) * tDeltaY : double.PositiveInfinity;
        double tMaxZ = dz != 0 ? Bound(oz, z, stepZ) * tDeltaZ : double.PositiveInfinity;

        int nx = 0, ny = 0, nz = 0;
        double t = 0;

        while (t <= maxDistance)
        {
            if (t > 0 && isTargetable(x, y, z))
            {
                return new RaycastHit(x, y, z, nx, ny, nz, t);
            }
            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                t = tMaxX; tMaxX += tDeltaX; x += stepX;
                nx = -stepX; ny = 0; nz = 0;
            }
            else if (tMaxY < tMaxZ)
            {
                t = tMaxY; tMaxY += tDeltaY; y += stepY;
                nx = 0; ny = -stepY; nz = 0;
            }
            else
            {
                t = tMaxZ; tMaxZ += tDeltaZ; z += stepZ;
                nx = 0; ny = 0; nz = -stepZ;
            }
        }
        return null;
    }
}
