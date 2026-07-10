using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// Greedy mesher with cross-chunk face culling — logic-identical port of the
/// web client's greedy.ts, extended with baked vertex AO (plan 02 M6). For
/// each of the 6 face directions it sweeps slices, builds a visibility mask,
/// and merges equal cells into maximal rectangles. UVs are world-anchored
/// (one unit per block) and wrap inside the texture-array layer, so merged
/// quads tile seamlessly.
///
/// AO: classic 3-neighbor corner occlusion sampled in the layer the face
/// looks into, baked into the per-vertex brightness. The merge key includes
/// the cell's 4 corner AO levels so greedy rectangles stay correct (cells
/// only merge when their corner AO matches). Corner samples that would cross
/// two chunk boundaries at once read as air (the pool only ships the 6 face
/// neighbors) — a subtle AO seam on chunk corners, invisible in practice.
///
/// Output is interleaved vertex data: position(3) uv(2) meta(2), where meta
/// is (atlas layer, face brightness × AO). Light-emitting blocks get +4.0
/// added to meta.y — the fragment shader renders those faces fullbright.
/// </summary>
public sealed class MeshPass
{
    public required float[] Vertices { get; init; }   // stride 7
    public required uint[] Indices { get; init; }

    public int IndexCount => Indices.Length;
}

public sealed record MeshResult(MeshPass? Solid, MeshPass? LiquidSurface, MeshPass? Translucent);

public static class GreedyMesher
{
    // Per-face brightness, FACE_DIRS order (px, nx, py, ny, pz, nz):
    // classic voxel shading — top brightest, bottom darkest.
    private static readonly float[] FaceBrightness = [0.6f, 0.6f, 1.0f, 0.5f, 0.8f, 0.8f];

    // Brightness multiplier per corner AO level (0 = fully occluded corner).
    private static readonly float[] AoFactor = [0.55f, 0.72f, 0.86f, 1.0f];

    private readonly record struct Sweep(int D, int Dir, int FaceIndex, int U, int V, bool Flip);

    private static readonly Sweep[] Sweeps =
    [
        new(0, 1, 0, 2, 1, true),
        new(0, -1, 1, 2, 1, false),
        new(1, 1, 2, 0, 2, true),
        new(1, -1, 3, 0, 2, false),
        new(2, 1, 4, 0, 1, false),
        new(2, -1, 5, 0, 1, true),
    ];

    private sealed class PassBuilder
    {
        public readonly List<float> Vertices = new();
        public readonly List<uint> Indices = new();

        public void Quad(
            Span<float> c0, Span<float> c1, Span<float> c2, Span<float> c3,
            float u0, float v0, float u1, float v1,
            float layer, Span<float> brightness, bool flip)
        {
            uint baseIndex = (uint)(Vertices.Count / 7);
            AddVertex(c0, u0, v0, layer, brightness[0]);
            AddVertex(c1, u1, v0, layer, brightness[1]);
            AddVertex(c2, u1, v1, layer, brightness[2]);
            AddVertex(c3, u0, v1, layer, brightness[3]);
            if (flip)
            {
                Indices.AddRange([baseIndex, baseIndex + 2, baseIndex + 1, baseIndex, baseIndex + 3, baseIndex + 2]);
            }
            else
            {
                Indices.AddRange([baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3]);
            }
        }

        private void AddVertex(Span<float> pos, float u, float v, float layer, float brightness)
        {
            Vertices.AddRange([pos[0], pos[1], pos[2], u, v, layer, brightness]);
        }

        public MeshPass? Build() => Indices.Count == 0 ? null : new MeshPass
        {
            Vertices = [.. Vertices],
            Indices = [.. Indices],
        };
    }

    /// <param name="neighbors">Neighbor chunk contents in FACE_DIRS order (px,nx,py,ny,pz,nz); null = treat as air.</param>
    /// <param name="cellsPerAxis">Grid edge in cells (16 for chunks and LOD sections alike, plan 04 v2).</param>
    /// <param name="cellSize">World blocks per cell (2^level). Geometry scales; UVs stay block-anchored.</param>
    public static MeshResult Mesh(
        int cx, int cy, int cz,
        ushort[] blocks,
        ushort[]?[] neighbors,
        byte[] opaque,
        ushort[] renderTable,
        byte[] translucentMask,
        byte[] flatAoMask,
        byte[] emissiveMask,
        int cellsPerAxis = Constants.ChunkSize,
        int cellSize = 1)
    {
        int S = cellsPerAxis;
        int ox = cx * Constants.ChunkSize, oy = cy * Constants.ChunkSize, oz = cz * Constants.ChunkSize;
        ReadOnlySpan<int> chunkOrigin = stackalloc int[] { ox, oy, oz };
        ushort[]? nPx = neighbors[0], nNx = neighbors[1], nPy = neighbors[2];
        ushort[]? nNy = neighbors[3], nPz = neighbors[4], nNz = neighbors[5];

        int Idx(int x, int y, int z) => (y * S + z) * S + x;

        // Voxel lookup that reaches one cell into neighbor chunks along one
        // axis. AO corner samples can step outside on two axes at once; the
        // pool only has face neighbors, so those read as air.
        ushort Voxel(int x, int y, int z)
        {
            int outside = (x < 0 || x >= S ? 1 : 0) + (y < 0 || y >= S ? 1 : 0) + (z < 0 || z >= S ? 1 : 0);
            if (outside > 1) return 0;
            if (x >= S) return nPx?[Idx(x - S, y, z)] ?? 0;
            if (x < 0) return nNx?[Idx(x + S, y, z)] ?? 0;
            if (y >= S) return nPy?[Idx(x, y - S, z)] ?? 0;
            if (y < 0) return nNy?[Idx(x, y + S, z)] ?? 0;
            if (z >= S) return nPz?[Idx(x, y, z - S)] ?? 0;
            if (z < 0) return nNz?[Idx(x, y, z + S)] ?? 0;
            return blocks[Idx(x, y, z)];
        }

        var solid = new PassBuilder();
        var liquidSurface = new PassBuilder();
        var translucent = new PassBuilder();
        // Merge key per cell: block id (16 bits) | packed corner AO (8 bits).
        var mask = new uint[S * S];
        var apos = new int[3]; // scratch for CornerAo (locals can't capture spans)
        Span<int> pos = stackalloc int[3];
        Span<float> c0 = stackalloc float[3];
        Span<float> c1 = stackalloc float[3];
        Span<float> c2 = stackalloc float[3];
        Span<float> c3 = stackalloc float[3];
        Span<float> brightness4 = stackalloc float[4];

        foreach (var sweep in Sweeps)
        {
            (int d, int dir, int faceIndex, int u, int v, bool flip) =
                (sweep.D, sweep.Dir, sweep.FaceIndex, sweep.U, sweep.V, sweep.Flip);
            float brightness = FaceBrightness[faceIndex];

            // One corner AO level (0..3) from the two edge neighbors + the
            // diagonal, all sampled in the layer the face looks into.
            int CornerAo(int mu, int mv, int slice, int su, int sv)
            {
                apos[d] = slice + dir;
                apos[u] = mu + su; apos[v] = mv;
                bool side1 = opaque[Voxel(apos[0], apos[1], apos[2])] == 1;
                apos[u] = mu; apos[v] = mv + sv;
                bool side2 = opaque[Voxel(apos[0], apos[1], apos[2])] == 1;
                apos[u] = mu + su; apos[v] = mv + sv;
                bool corner = opaque[Voxel(apos[0], apos[1], apos[2])] == 1;
                if (side1 && side2) return 0;
                return 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
            }

            for (int slice = 0; slice < S; slice++)
            {
                // Build the visibility mask for this slice. A face is hidden when
                // the block behind it is opaque, or is the same non-opaque block.
                bool anyVisible = false;
                for (int mv = 0; mv < S; mv++)
                {
                    for (int mu = 0; mu < S; mu++)
                    {
                        pos[d] = slice; pos[u] = mu; pos[v] = mv;
                        ushort id = blocks[Idx(pos[0], pos[1], pos[2])];
                        uint cell = 0;
                        if (id != 0)
                        {
                            pos[d] = slice + dir;
                            ushort nb = Voxel(pos[0], pos[1], pos[2]);
                            if (opaque[nb] != 1 && nb != id)
                            {
                                int ao00 = CornerAo(mu, mv, slice, -1, -1);
                                int ao10 = CornerAo(mu, mv, slice, +1, -1);
                                int ao11 = CornerAo(mu, mv, slice, +1, +1);
                                int ao01 = CornerAo(mu, mv, slice, -1, +1);
                                cell = id | (uint)((ao00 | ao10 << 2 | ao11 << 4 | ao01 << 6) << 16);
                            }
                        }
                        mask[mv * S + mu] = cell;
                        if (cell != 0) anyVisible = true;
                    }
                }
                if (!anyVisible) continue;

                // Merge mask cells into maximal rectangles.
                for (int mv = 0; mv < S; mv++)
                {
                    for (int mu = 0; mu < S; mu++)
                    {
                        uint cell = mask[mv * S + mu];
                        if (cell == 0) continue;
                        ushort id = (ushort)(cell & 0xFFFF);
                        bool isTranslucent = translucentMask[id] == 1;
                        bool flatAo = flatAoMask[id] == 1;
                        bool mergeById = isTranslucent || flatAo;

                        int w = 1;
                        while (mu + w < S && (mergeById ? (mask[mv * S + mu + w] & 0xFFFF) == id : mask[mv * S + mu + w] == cell)) w++;
                        int h = 1;
                        while (mv + h < S)
                        {
                            bool rowMatches = true;
                            for (int k = 0; k < w; k++)
                            {
                                uint other = mask[(mv + h) * S + mu + k];
                                if (mergeById ? (other & 0xFFFF) != id : other != cell) { rowMatches = false; break; }
                            }
                            if (!rowMatches) break;
                            h++;
                        }
                        for (int dv = 0; dv < h; dv++)
                        {
                            for (int du = 0; du < w; du++) mask[(mv + dv) * S + mu + du] = 0;
                        }

                        // Quad corners: origin + extents along u/v axes, scaled to
                        // world blocks; the face plane sits at slice+1 for positive
                        // directions, slice for negative.
                        int plane = slice + (dir > 0 ? 1 : 0);
                        SetCorner(c0, d, plane * cellSize, u, mu * cellSize, v, mv * cellSize);
                        SetCorner(c1, d, plane * cellSize, u, (mu + w) * cellSize, v, mv * cellSize);
                        SetCorner(c2, d, plane * cellSize, u, (mu + w) * cellSize, v, (mv + h) * cellSize);
                        SetCorner(c3, d, plane * cellSize, u, mu * cellSize, v, (mv + h) * cellSize);

                        // Skip baked AO on ice/water — corner samples differ at chunk
                        // edges and produced visible grid seams.
                        uint ao = cell >> 16;
                        float emissive = emissiveMask[id] == 1 ? 4f : 0f;
                        if (flatAo)
                        {
                            brightness4[0] = brightness4[1] = brightness4[2] = brightness4[3] = brightness + emissive;
                        }
                        else
                        {
                            brightness4[0] = brightness * AoFactor[ao & 3] + emissive;
                            brightness4[1] = brightness * AoFactor[(ao >> 2) & 3] + emissive;
                            brightness4[2] = brightness * AoFactor[(ao >> 4) & 3] + emissive;
                            brightness4[3] = brightness * AoFactor[(ao >> 6) & 3] + emissive;
                        }

                        // World-anchored UVs (one unit per block, LOD cells span
                        // cellSize units) so atlas repeat tiles continuously across
                        // chunk boundaries and rings keep the same texel density.
                        float u0 = chunkOrigin[u] + mu * cellSize;
                        float u1 = chunkOrigin[u] + (mu + w) * cellSize;
                        float v0 = chunkOrigin[v] + mv * cellSize;
                        float v1 = chunkOrigin[v] + (mv + h) * cellSize;

                        // NOTE: no chunk-border overlap nudge here. Overlapping
                        // liquid-surface quads from adjacent chunks double-blend
                        // (a darker grid on water). Adjacent greedy quads share an
                        // exact integer edge, so the rasterizer fill rule covers it
                        // once — no gap, no overlap.

                        var target = isTranslucent switch
                        {
                            true when flatAo && faceIndex is 2 or 3 => liquidSurface,
                            true => translucent,
                            _ => solid,
                        };
                        target.Quad(
                            c0, c1, c2, c3,
                            u0, v0, u1, v1,
                            renderTable[id * 6 + faceIndex], brightness4, flip);
                    }
                }
            }
        }

        return new MeshResult(solid.Build(), liquidSurface.Build(), translucent.Build());
    }

    private static void SetCorner(Span<float> c, int d, int dCoord, int u, int uCoord, int v, int vCoord)
    {
        c[d] = dCoord;
        c[u] = uCoord;
        c[v] = vCoord;
    }
}
