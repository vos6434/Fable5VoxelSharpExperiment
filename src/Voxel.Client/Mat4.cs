namespace Voxel.Client;

/// <summary>
/// Minimal column-major 4x4 matrix helpers for OpenGL (clip z in [-1, 1]).
/// Kept tiny and explicit to avoid row/column-convention confusion with
/// System.Numerics. Data layout uploads directly with transpose = false.
/// </summary>
public static class Mat4
{
    /// <summary>Column-major multiply: result = a * b (a applied last).</summary>
    public static float[] Multiply(float[] a, float[] b)
    {
        var r = new float[16];
        for (int col = 0; col < 4; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                float sum = 0;
                for (int k = 0; k < 4; k++) sum += a[k * 4 + row] * b[col * 4 + k];
                r[col * 4 + row] = sum;
            }
        }
        return r;
    }

    public static float[] PerspectiveGl(float fovYRadians, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovYRadians / 2f);
        var m = new float[16];
        m[0] = f / aspect;
        m[5] = f;
        m[10] = (far + near) / (near - far);
        m[11] = -1f;
        m[14] = 2f * far * near / (near - far);
        return m;
    }

    /// <summary>View matrix from eye position and yaw/pitch (web camera convention: yaw about Y, pitch about X).</summary>
    public static float[] View(float ex, float ey, float ez, float yaw, float pitch)
    {
        // Camera basis (world space).
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        // forward = rotate (0,0,-1) by pitch then yaw
        float fx = -sy * cp, fy = sp, fz = -cy * cp;
        // right = rotate (1,0,0) by yaw
        float rx = cy, ry = 0, rz = -sy;
        // up = right x forward... use cross(forward, right) = up
        float ux = fy * rz - fz * ry;
        float uy = fz * rx - fx * rz;
        float uz = fx * ry - fy * rx;
        // up should point "up": cross(right, forward) — fix sign
        ux = -ux; uy = -uy; uz = -uz;

        var m = new float[16];
        m[0] = rx; m[4] = ry; m[8] = rz;
        m[1] = ux; m[5] = uy; m[9] = uz;
        m[2] = -fx; m[6] = -fy; m[10] = -fz;
        m[12] = -(rx * ex + ry * ey + rz * ez);
        m[13] = -(ux * ex + uy * ey + uz * ez);
        m[14] = -(-fx * ex + -fy * ey + -fz * ez);
        m[15] = 1f;
        return m;
    }
}
