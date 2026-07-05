namespace Voxel.Shared;

/// <summary>
/// Seeded 2D and 3D simplex noise (Gustavson's reference algorithm) plus
/// fractal Brownian motion. This is a statement-for-statement port of the
/// TypeScript original: the arithmetic uses only +, *, floor and 32-bit
/// integer ops, so results are bit-identical across languages — verified by
/// the golden tests. Do not reorder expressions.
/// </summary>
internal sealed class Mulberry32
{
    private uint _a;

    public Mulberry32(int seed)
    {
        _a = unchecked((uint)seed); // JS: seed >>> 0
    }

    public double Next()
    {
        unchecked
        {
            _a += 0x6d2b79f5u;
            uint t = (_a ^ (_a >> 15)) * (1u | _a);          // Math.imul(a ^ (a >>> 15), 1 | a)
            t = (t + ((t ^ (t >> 7)) * (61u | t))) ^ t;      // (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
            return (t ^ (t >> 14)) / 4294967296.0;           // ((t ^ (t >>> 14)) >>> 0) / 2^32
        }
    }
}

public sealed class Simplex2
{
    private static readonly double F2 = 0.5 * (Math.Sqrt(3) - 1);
    private static readonly double G2 = (3 - Math.Sqrt(3)) / 6;

    // 8 gradient directions, (x, y) pairs.
    private static readonly double[] Grad2 =
    [
        1, 1, -1, 1, 1, -1, -1, -1,
        1, 0, -1, 0, 0, 1, 0, -1,
    ];

    private readonly byte[] _perm = new byte[512];

    public Simplex2(int seed)
    {
        var p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;
        var rand = new Mulberry32(seed);
        for (int i = 255; i > 0; i--)
        {
            int j = (int)Math.Floor(rand.Next() * (i + 1));
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    /// <summary>Single octave, output in [-1, 1].</summary>
    public double Noise(double xin, double yin)
    {
        byte[] perm = _perm;
        double s = (xin + yin) * F2;
        int i = (int)Math.Floor(xin + s);
        int j = (int)Math.Floor(yin + s);
        double t = (i + j) * G2;
        double x0 = xin - (i - t);
        double y0 = yin - (j - t);

        int i1 = x0 > y0 ? 1 : 0;
        int j1 = x0 > y0 ? 0 : 1;

        double x1 = x0 - i1 + G2;
        double y1 = y0 - j1 + G2;
        double x2 = x0 - 1 + 2 * G2;
        double y2 = y0 - 1 + 2 * G2;

        int ii = i & 255;
        int jj = j & 255;

        double n = 0;
        double a = 0.5 - x0 * x0 - y0 * y0;
        if (a > 0)
        {
            int g = (perm[ii + perm[jj]] & 7) * 2;
            a *= a;
            n += a * a * (Grad2[g] * x0 + Grad2[g + 1] * y0);
        }
        a = 0.5 - x1 * x1 - y1 * y1;
        if (a > 0)
        {
            int g = (perm[ii + i1 + perm[jj + j1]] & 7) * 2;
            a *= a;
            n += a * a * (Grad2[g] * x1 + Grad2[g + 1] * y1);
        }
        a = 0.5 - x2 * x2 - y2 * y2;
        if (a > 0)
        {
            int g = (perm[ii + 1 + perm[jj + 1]] & 7) * 2;
            a *= a;
            n += a * a * (Grad2[g] * x2 + Grad2[g + 1] * y2);
        }
        return 70 * n;
    }

    /// <summary>Fractal Brownian motion: octaves layers, each doubling frequency and halving amplitude. Output in [-1, 1].</summary>
    public double Fbm(double x, double y, int octaves)
    {
        double sum = 0;
        double amp = 1;
        double freq = 1;
        double norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += Noise(x * freq, y * freq) * amp;
            norm += amp;
            amp *= 0.5;
            freq *= 2;
        }
        return sum / norm;
    }
}

public sealed class Simplex3
{
    private const double F3 = 1.0 / 3;
    private const double G3 = 1.0 / 6;

    // 12 gradient directions (edge midpoints of a cube), (x, y, z) triples.
    private static readonly double[] Grad3 =
    [
        1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1, 0,
        1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, -1,
        0, 1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1,
    ];

    private readonly byte[] _perm = new byte[512];

    public Simplex3(int seed)
    {
        var p = new byte[256];
        for (int i = 0; i < 256; i++) p[i] = (byte)i;
        var rand = new Mulberry32(seed);
        for (int i = 255; i > 0; i--)
        {
            int j = (int)Math.Floor(rand.Next() * (i + 1));
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    /// <summary>Single octave, output in [-1, 1].</summary>
    public double Noise(double xin, double yin, double zin)
    {
        byte[] perm = _perm;
        double s = (xin + yin + zin) * F3;
        int i = (int)Math.Floor(xin + s);
        int j = (int)Math.Floor(yin + s);
        int k = (int)Math.Floor(zin + s);
        double t = (i + j + k) * G3;
        double x0 = xin - (i - t);
        double y0 = yin - (j - t);
        double z0 = zin - (k - t);

        // Rank the coordinates to pick the simplex traversal order.
        int i1 = 0, j1 = 0, k1 = 0;
        int i2 = 0, j2 = 0, k2 = 0;
        if (x0 >= y0)
        {
            if (y0 >= z0) { i1 = 1; i2 = 1; j2 = 1; }
            else if (x0 >= z0) { i1 = 1; i2 = 1; k2 = 1; }
            else { k1 = 1; i2 = 1; k2 = 1; }
        }
        else
        {
            if (y0 < z0) { k1 = 1; j2 = 1; k2 = 1; }
            else if (x0 < z0) { j1 = 1; j2 = 1; k2 = 1; }
            else { j1 = 1; i2 = 1; j2 = 1; }
        }

        double x1 = x0 - i1 + G3, y1 = y0 - j1 + G3, z1 = z0 - k1 + G3;
        double x2 = x0 - i2 + 2 * G3, y2 = y0 - j2 + 2 * G3, z2 = z0 - k2 + 2 * G3;
        double x3 = x0 - 1 + 3 * G3, y3 = y0 - 1 + 3 * G3, z3 = z0 - 1 + 3 * G3;

        int ii = i & 255, jj = j & 255, kk = k & 255;
        double n = 0;

        double a = 0.6 - x0 * x0 - y0 * y0 - z0 * z0;
        if (a > 0)
        {
            int g = (perm[ii + perm[jj + perm[kk]]] % 12) * 3;
            a *= a;
            n += a * a * (Grad3[g] * x0 + Grad3[g + 1] * y0 + Grad3[g + 2] * z0);
        }
        a = 0.6 - x1 * x1 - y1 * y1 - z1 * z1;
        if (a > 0)
        {
            int g = (perm[ii + i1 + perm[jj + j1 + perm[kk + k1]]] % 12) * 3;
            a *= a;
            n += a * a * (Grad3[g] * x1 + Grad3[g + 1] * y1 + Grad3[g + 2] * z1);
        }
        a = 0.6 - x2 * x2 - y2 * y2 - z2 * z2;
        if (a > 0)
        {
            int g = (perm[ii + i2 + perm[jj + j2 + perm[kk + k2]]] % 12) * 3;
            a *= a;
            n += a * a * (Grad3[g] * x2 + Grad3[g + 1] * y2 + Grad3[g + 2] * z2);
        }
        a = 0.6 - x3 * x3 - y3 * y3 - z3 * z3;
        if (a > 0)
        {
            int g = (perm[ii + 1 + perm[jj + 1 + perm[kk + 1]]] % 12) * 3;
            a *= a;
            n += a * a * (Grad3[g] * x3 + Grad3[g + 1] * y3 + Grad3[g + 2] * z3);
        }
        return 32 * n;
    }
}
