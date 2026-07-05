using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// Greedy mesher with cross-chunk face culling — logic-identical port of the
/// web client's greedy.ts. For each of the 6 face directions it sweeps
/// slices, builds a visibility mask, and merges equal cells into maximal
/// rectangles. UVs are world-anchored (one unit per block) and wrap inside
/// the texture-array layer, so merged quads tile seamlessly.
///
/// Output is interleaved vertex data: position(3) uv(2) meta(2), where meta
/// is (atlas layer, face brightness).
/// </summary>
public sealed class MeshPass
{
    public required float[] Vertices { get; init; }   // stride 7
    public required uint[] Indices { get; init; }

    public int IndexCount => Indices.Length;
}

public sealed record MeshResult(MeshPass? Solid, MeshPass? Translucent);

public static class GreedyMesher
{
    private const int S = Constants.ChunkSize;

    // Per-face brightness, FACE_DIRS order (px, nx, py, ny, pz, nz):
    // classic voxel shading — top brightest, bottom darkest.
    private static readonly float[] FaceBrightness = [0.6f, 0.6f, 1.0f, 0.5f, 0.8f, 0.8f];

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
            float layer, float brightness, bool flip)
        {
            uint baseIndex = (uint)(Vertices.Count / 7);
            AddVertex(c0, u0, v0, layer, brightness);
            AddVertex(c1, u1, v0, layer, brightness);
            AddVertex(c2, u1, v1, layer, brightness);
            AddVertex(c3, u0, v1, layer, brightness);
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
    public static MeshResult Mesh(
        ushort[] blocks,
        ushort[]?[] neighbors,
        byte[] opaque,
        ushort[] renderTable,
        byte[] translucentMask)
    {
        ushort[]? nPx = neighbors[0], nNx = neighbors[1], nPy = neighbors[2];
        ushort[]? nNy = neighbors[3], nPz = neighbors[4], nNz = neighbors[5];

        // Voxel lookup that reaches one block into neighbor chunks along one axis.
        ushort Voxel(int x, int y, int z)
        {
            if (x >= S) return nPx?[ChunkData.Index(x - S, y, z)] ?? 0;
            if (x < 0) return nNx?[ChunkData.Index(x + S, y, z)] ?? 0;
            if (y >= S) return nPy?[ChunkData.Index(x, y - S, z)] ?? 0;
            if (y < 0) return nNy?[ChunkData.Index(x, y + S, z)] ?? 0;
            if (z >= S) return nPz?[ChunkData.Index(x, y, z - S)] ?? 0;
            if (z < 0) return nNz?[ChunkData.Index(x, y, z + S)] ?? 0;
            return blocks[ChunkData.Index(x, y, z)];
        }

        var solid = new PassBuilder();
        var translucent = new PassBuilder();
        var mask = new ushort[S * S];
        Span<int> pos = stackalloc int[3];
        Span<float> c0 = stackalloc float[3];
        Span<float> c1 = stackalloc float[3];
        Span<float> c2 = stackalloc float[3];
        Span<float> c3 = stackalloc float[3];

        foreach (var sweep in Sweeps)
        {
            (int d, int dir, int faceIndex, int u, int v, bool flip) =
                (sweep.D, sweep.Dir, sweep.FaceIndex, sweep.U, sweep.V, sweep.Flip);
            float brightness = FaceBrightness[faceIndex];

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
                        ushort id = blocks[ChunkData.Index(pos[0], pos[1], pos[2])];
                        ushort visible = 0;
                        if (id != 0)
                        {
                            pos[d] = slice + dir;
                            ushort nb = Voxel(pos[0], pos[1], pos[2]);
                            if (opaque[nb] != 1 && nb != id) visible = id;
                        }
                        mask[mv * S + mu] = visible;
                        if (visible != 0) anyVisible = true;
                    }
                }
                if (!anyVisible) continue;

                // Merge mask cells into maximal rectangles.
                for (int mv = 0; mv < S; mv++)
                {
                    for (int mu = 0; mu < S; mu++)
                    {
                        ushort id = mask[mv * S + mu];
                        if (id == 0) continue;

                        int w = 1;
                        while (mu + w < S && mask[mv * S + mu + w] == id) w++;
                        int h = 1;
                        while (mv + h < S)
                        {
                            bool rowMatches = true;
                            for (int k = 0; k < w; k++)
                            {
                                if (mask[(mv + h) * S + mu + k] != id) { rowMatches = false; break; }
                            }
                            if (!rowMatches) break;
                            h++;
                        }
                        for (int dv = 0; dv < h; dv++)
                        {
                            for (int du = 0; du < w; du++) mask[(mv + dv) * S + mu + du] = 0;
                        }

                        // Quad corners: origin + extents along u/v axes; the face
                        // plane sits at slice+1 for positive directions, slice for negative.
                        int plane = slice + (dir > 0 ? 1 : 0);
                        SetCorner(c0, d, plane, u, mu, v, mv);
                        SetCorner(c1, d, plane, u, mu + w, v, mv);
                        SetCorner(c2, d, plane, u, mu + w, v, mv + h);
                        SetCorner(c3, d, plane, u, mu, v, mv + h);

                        var target = translucentMask[id] == 1 ? translucent : solid;
                        target.Quad(
                            c0, c1, c2, c3,
                            mu, mv, mu + w, mv + h,
                            renderTable[id * 6 + faceIndex], brightness, flip);
                    }
                }
            }
        }

        return new MeshResult(solid.Build(), translucent.Build());
    }

    private static void SetCorner(Span<float> c, int d, int dCoord, int u, int uCoord, int v, int vCoord)
    {
        c[d] = dCoord;
        c[u] = uCoord;
        c[v] = vCoord;
    }
}
