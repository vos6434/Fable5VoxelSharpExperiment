namespace Voxel.Shared;

/// <summary>
/// Worldgen v2, ported statement-for-statement from the web version:
/// continents + 3D biomes. Biome is a function of (x, y, z) — climate noise
/// picks surface biomes with per-block dither, a 3D field picks cave biomes
/// underground, and a noise-warped depth boundary gates the hell layer.
/// Bit-identical to the TypeScript implementation (golden-tested); keep
/// expression order intact when touching this file.
/// </summary>

public enum SurfaceBiome { Ocean, FrozenOcean, Beach, Desert, SnowyPlains, Grassland }

public enum CaveBiome { StoneCaves, LushCaves, DarkCaves }

public static class BiomeNames
{
    public static string Name(this SurfaceBiome b) => b switch
    {
        SurfaceBiome.Ocean => "ocean",
        SurfaceBiome.FrozenOcean => "frozen_ocean",
        SurfaceBiome.Beach => "beach",
        SurfaceBiome.Desert => "desert",
        SurfaceBiome.SnowyPlains => "snowy_plains",
        _ => "grassland",
    };

    public static string Name(this CaveBiome b) => b switch
    {
        CaveBiome.LushCaves => "lush_caves",
        CaveBiome.DarkCaves => "dark_caves",
        _ => "stone_caves",
    };
}

public sealed record GeneratedChunk(ChunkData Chunk, bool Empty);

public sealed class WorldGen
{
    public const int SeaLevel = 0;
    public const int DefaultSeed = 1337;
    /// <summary>Bump when generation logic changes; persisted worlds refuse to mix versions.</summary>
    public const int GeneratorVersion = 2;

    /// <summary>Mean depth of the hell boundary; warped ±24 blocks by 2D noise.</summary>
    private const int HellDepth = -96;
    /// <summary>Lava fills hell caverns this far below the local boundary.</summary>
    private const int HellLavaOffset = 56;
    /// <summary>Thickness of the dithered stone→hellstone transition band.</summary>
    private const int HellBlend = 8;
    /// <summary>Mountains above this height are snow-capped regardless of climate.</summary>
    private const int AlpineHeight = 36;

    public int Seed { get; }

    // 2D fields
    private readonly Simplex2 _continents;
    private readonly Simplex2 _hills;
    private readonly Simplex2 _detail;
    private readonly Simplex2 _temperature;
    private readonly Simplex2 _humidity;
    private readonly Simplex2 _climateJitter;
    private readonly Simplex2 _hellWarp;
    // 3D fields
    private readonly Simplex3 _caveA;
    private readonly Simplex3 _caveB;
    private readonly Simplex3 _caveBiomeField;
    private readonly Simplex3 _hellDensity;
    private readonly Simplex3 _hellDetail;
    private readonly Simplex3 _sparkle;
    private readonly Simplex3 _blend;

    private readonly ushort _stone;
    private readonly ushort _dirt;
    private readonly ushort _grass;
    private readonly ushort _sand;
    private readonly ushort _gravel;
    private readonly ushort _water;
    private readonly ushort _snow;
    private readonly ushort _ice;
    private readonly ushort _sandstone;
    private readonly ushort _slate;
    private readonly ushort _mossyStone;
    private readonly ushort _hellstone;
    private readonly ushort _glowstone;
    private readonly ushort _lava;

    public WorldGen(int seed, BlockRegistry blocks)
    {
        Seed = seed;
        _continents = new Simplex2(seed ^ unchecked((int)0x9e3779b9));
        _hills = new Simplex2(seed + 1);
        _detail = new Simplex2(seed + 2);
        _temperature = new Simplex2(seed + 3);
        _humidity = new Simplex2(seed + 4);
        _climateJitter = new Simplex2(seed + 5);
        _hellWarp = new Simplex2(seed + 6);
        _caveA = new Simplex3(seed + 10);
        _caveB = new Simplex3(seed + 11);
        _caveBiomeField = new Simplex3(seed + 12);
        _hellDensity = new Simplex3(seed + 13);
        _hellDetail = new Simplex3(seed + 14);
        _sparkle = new Simplex3(seed + 15);
        _blend = new Simplex3(seed + 16);

        _stone = blocks.Resolve("stone");
        _dirt = blocks.Resolve("dirt");
        _grass = blocks.Resolve("grass");
        _sand = blocks.Resolve("sand");
        _gravel = blocks.Resolve("gravel");
        _water = blocks.Resolve("water");
        _snow = blocks.Resolve("snow");
        _ice = blocks.Resolve("ice");
        _sandstone = blocks.Resolve("sandstone");
        _slate = blocks.Resolve("slate");
        _mossyStone = blocks.Resolve("mossy_stone");
        _hellstone = blocks.Resolve("hellstone");
        _glowstone = blocks.Resolve("glowstone");
        _lava = blocks.Resolve("lava");
    }

    public int SurfaceHeight(double x, double z)
    {
        double c = _continents.Fbm(x / 700, z / 700, 4);
        double @base = c * 28;
        double hillAmp = 6 + 20 * Math.Max(0, c + 0.2);
        double hills = _hills.Fbm(x / 130, z / 130, 4) * hillAmp;
        double detail = _detail.Fbm(x / 26, z / 26, 3) * 3;
        return (int)JsMath.Round(@base + hills + detail);
    }

    /// <summary>Local depth of the hell boundary (2D noise-warped, never flat).</summary>
    public double HellBoundary(double x, double z)
    {
        return HellDepth + _hellWarp.Fbm(x / 300, z / 300, 2) * 24;
    }

    public SurfaceBiome GetSurfaceBiome(double x, double z, int h)
    {
        // Per-block jitter dithers climate borders instead of hard-cutting them.
        double dither = _climateJitter.Noise(x / 28, z / 28) * 0.08;
        double temp = _temperature.Fbm(x / 900, z / 900, 3) + dither;
        double humid = _humidity.Fbm(x / 800, z / 800, 3) - dither;

        if (h <= SeaLevel - 2) return temp < -0.35 ? SurfaceBiome.FrozenOcean : SurfaceBiome.Ocean;
        if (h <= SeaLevel + 1) return SurfaceBiome.Beach;
        if (h >= AlpineHeight || temp < -0.35) return SurfaceBiome.SnowyPlains;
        if (temp > 0.35 && humid < 0) return SurfaceBiome.Desert;
        return SurfaceBiome.Grassland;
    }

    public CaveBiome GetCaveBiome(double x, double y, double z)
    {
        double v = _caveBiomeField.Noise(x / 140, y / 140, z / 140);
        if (v > 0.3) return CaveBiome.LushCaves;
        if (v < -0.3) return CaveBiome.DarkCaves;
        return CaveBiome.StoneCaves;
    }

    /// <summary>Biome as a function of all three coordinates — used by the HUD and (later) gameplay.</summary>
    public string BiomeAt(double x, double y, double z)
    {
        if (y <= HellBoundary(x, z)) return "hell";
        int h = SurfaceHeight(x, z);
        if (y < h - 8) return GetCaveBiome(x, y, z).Name();
        return GetSurfaceBiome(x, z, h).Name();
    }

    /// <summary>
    /// Nearest comfortably-dry column to the origin: spiral-scan in 16-block
    /// steps for ground a few blocks above sea level. Deterministic, so every
    /// process using a given seed agrees on the spawn point.
    /// </summary>
    public (int X, int Y, int Z) FindSpawn()
    {
        ReadOnlySpan<(int Ox, int Oz)> area = [(24, 0), (-24, 0), (0, 24), (0, -24), (24, 24), (-24, -24)];
        for (int ring = 0; ring <= 64; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            {
                for (int dz = -ring; dz <= ring; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                    int x = dx * 16;
                    int z = dz * 16;
                    int h = SurfaceHeight(x, z);
                    if (h < SeaLevel + 3) continue;
                    // Reject tiny islands: the surrounding area must be dry too.
                    bool allDry = true;
                    foreach (var (ox, oz) in area)
                    {
                        if (SurfaceHeight(x + ox, z + oz) < SeaLevel + 2) { allDry = false; break; }
                    }
                    if (allDry) return (x, h, z);
                }
            }
        }
        return (0, SurfaceHeight(0, 0), 0);
    }

    private ushort SurfaceTop(SurfaceBiome biome, int h) => biome switch
    {
        SurfaceBiome.Grassland => _grass,
        SurfaceBiome.Desert or SurfaceBiome.Beach => _sand,
        SurfaceBiome.SnowyPlains => _snow,
        _ => h <= SeaLevel - 7 ? _gravel : _sand, // ocean / frozen_ocean
    };

    private ushort SubSurface(SurfaceBiome biome, int h) => biome switch
    {
        SurfaceBiome.Grassland or SurfaceBiome.SnowyPlains => _dirt,
        SurfaceBiome.Desert => _sandstone,
        SurfaceBiome.Beach => _sand,
        _ => h <= SeaLevel - 7 ? _gravel : _sand, // ocean / frozen_ocean
    };

    public GeneratedChunk Generate(int cx, int cy, int cz)
    {
        var chunk = new ChunkData();
        int x0 = cx * Constants.ChunkSize;
        int y0 = cy * Constants.ChunkSize;
        int z0 = cz * Constants.ChunkSize;

        // Column pass: heights, biomes, hell boundary.
        var heights = new int[Constants.ChunkSize * Constants.ChunkSize];
        var hellBs = new double[Constants.ChunkSize * Constants.ChunkSize];
        var biomes = new SurfaceBiome[Constants.ChunkSize * Constants.ChunkSize];
        double maxH = double.NegativeInfinity;
        for (int lx = 0; lx < Constants.ChunkSize; lx++)
        {
            for (int lz = 0; lz < Constants.ChunkSize; lz++)
            {
                int i = lz * Constants.ChunkSize + lx;
                int h = SurfaceHeight(x0 + lx, z0 + lz);
                heights[i] = h;
                hellBs[i] = HellBoundary(x0 + lx, z0 + lz);
                biomes[i] = GetSurfaceBiome(x0 + lx, z0 + lz, h);
                if (h > maxH) maxH = h;
            }
        }

        // Entirely above both terrain and sea: pure air (hell is far below).
        if (y0 > Math.Max(maxH, SeaLevel))
        {
            return new GeneratedChunk(chunk, Empty: true);
        }

        int count = 0;
        for (int lx = 0; lx < Constants.ChunkSize; lx++)
        {
            for (int lz = 0; lz < Constants.ChunkSize; lz++)
            {
                int i = lz * Constants.ChunkSize + lx;
                int h = heights[i];
                double hellB = hellBs[i];
                SurfaceBiome biome = biomes[i];
                int wx = x0 + lx;
                int wz = z0 + lz;

                for (int ly = 0; ly < Constants.ChunkSize; ly++)
                {
                    int wy = y0 + ly;
                    ushort id = BlockAt(wx, wy, wz, h, hellB, biome);
                    if (id != 0)
                    {
                        chunk.Set(lx, ly, lz, id);
                        count++;
                    }
                }
            }
        }
        return new GeneratedChunk(chunk, Empty: count == 0);
    }

    private ushort BlockAt(int wx, int wy, int wz, int h, double hellB, SurfaceBiome biome)
    {
        // ---- Hell zone ----
        if (wy <= hellB)
        {
            double density =
                _hellDensity.Noise(wx / 90.0, wy / 90.0, wz / 90.0) +
                0.35 * _hellDetail.Noise(wx / 33.0, wy / 33.0, wz / 33.0);
            if (density > -0.15)
            {
                return _sparkle.Noise(wx / 13.0, wy / 13.0, wz / 13.0) > 0.72
                    ? _glowstone
                    : _hellstone;
            }
            return wy <= hellB - HellLavaOffset ? _lava : (ushort)0;
        }

        // ---- Above ground / water ----
        if (wy > h)
        {
            if (wy > SeaLevel) return 0;
            if (biome == SurfaceBiome.FrozenOcean && wy == SeaLevel) return _ice;
            return _water;
        }

        // ---- Carved tunnel caves (two intersecting 3D noise sheets) ----
        if (wy < h - 5 && wy > hellB + 2)
        {
            if (Math.Abs(_caveA.Noise(wx / 60.0, wy / 60.0, wz / 60.0)) < 0.12 &&
                Math.Abs(_caveB.Noise(wx / 60.0, wy / 60.0, wz / 60.0)) < 0.12)
            {
                return 0;
            }
        }

        // ---- Surface strata ----
        int depth = h - wy;
        if (depth == 0) return SurfaceTop(biome, h);
        if (depth <= 3) return SubSurface(biome, h);

        // ---- Deep stone: hell transition band, then 3D cave biomes ----
        if (wy <= hellB + HellBlend)
        {
            double t = (hellB + HellBlend - wy) / HellBlend;
            if ((_blend.Noise(wx / 15.0, wy / 15.0, wz / 15.0) + 1) / 2 < t) return _hellstone;
            return _stone;
        }
        if (wy < h - 8)
        {
            CaveBiome cave = GetCaveBiome(wx, wy, wz);
            if (cave == CaveBiome.LushCaves) return _mossyStone;
            if (cave == CaveBiome.DarkCaves) return _slate;
        }
        return _stone;
    }
}
