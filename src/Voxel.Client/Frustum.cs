namespace Voxel.Client;

/// <summary>
/// View-frustum culling (Gribb-Hartmann plane extraction from a column-major
/// view-projection matrix). Chunks are tested as bounding spheres — the same
/// scheme three.js applied for the web client.
/// </summary>
public static class Frustum
{
    /// <summary>Extracts 6 planes (a,b,c,d each) into a 24-float buffer.</summary>
    public static void Extract(float[] m, float[] planes)
    {
        // Row i of the matrix, from column-major storage.
        static void Row(float[] m, int i, out float x, out float y, out float z, out float w)
        {
            x = m[0 * 4 + i]; y = m[1 * 4 + i]; z = m[2 * 4 + i]; w = m[3 * 4 + i];
        }
        Row(m, 0, out float r0x, out float r0y, out float r0z, out float r0w);
        Row(m, 1, out float r1x, out float r1y, out float r1z, out float r1w);
        Row(m, 2, out float r2x, out float r2y, out float r2z, out float r2w);
        Row(m, 3, out float r3x, out float r3y, out float r3z, out float r3w);

        Set(planes, 0, r3x + r0x, r3y + r0y, r3z + r0z, r3w + r0w); // left
        Set(planes, 1, r3x - r0x, r3y - r0y, r3z - r0z, r3w - r0w); // right
        Set(planes, 2, r3x + r1x, r3y + r1y, r3z + r1z, r3w + r1w); // bottom
        Set(planes, 3, r3x - r1x, r3y - r1y, r3z - r1z, r3w - r1w); // top
        Set(planes, 4, r3x + r2x, r3y + r2y, r3z + r2z, r3w + r2w); // near
        Set(planes, 5, r3x - r2x, r3y - r2y, r3z - r2z, r3w - r2w); // far
    }

    private static void Set(float[] planes, int index, float a, float b, float c, float d)
    {
        // Normalize so plane distances are in world units (needed for radius tests).
        float len = MathF.Sqrt(a * a + b * b + c * c);
        if (len > 0)
        {
            a /= len; b /= len; c /= len; d /= len;
        }
        planes[index * 4] = a;
        planes[index * 4 + 1] = b;
        planes[index * 4 + 2] = c;
        planes[index * 4 + 3] = d;
    }

    public static bool SphereVisible(float[] planes, float x, float y, float z, float radius)
    {
        for (int p = 0; p < 6; p++)
        {
            float dist = planes[p * 4] * x + planes[p * 4 + 1] * y + planes[p * 4 + 2] * z + planes[p * 4 + 3];
            if (dist < -radius) return false;
        }
        return true;
    }
}
