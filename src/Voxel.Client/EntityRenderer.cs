using Silk.NET.OpenGL;
using Voxel.Client.Gl;
using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// Physics entities (contraptions / debug bodies, plan 03): each carries a
/// small block grid meshed once on spawn, drawn with a full model matrix, and
/// interpolated between the last two server EntityStates (~100 ms behind) with
/// brief velocity extrapolation if states are starved. Entities are lit by the
/// world but don't occlude light rays while moving (v1).
/// </summary>
public sealed class EntityRenderer : IDisposable
{
    public readonly record struct EntityRaycastHit(
        uint EntityId, int LocalX, int LocalY, int LocalZ, int Nx, int Ny, int Nz, double Distance);

    private sealed class Entity
    {
        public required uint Id;
        public required int DimX, DimY, DimZ;
        public required float PivotX, PivotY, PivotZ;
        public required ushort[] Blocks;
        public required ChunkMesh Mesh;
        /// <summary>Hull-interior footprint as grid-local rects (water cut-outs); null when nothing encloses.</summary>
        public List<(int X0, int Z0, int W, int D)>? HullRects;
        /// <summary>Top of the water cut-out (one above the hull rim).</summary>
        public int DeckY;
        public float X, Y, Z;
        public float Qx, Qy, Qz, Qw = 1;
        public float Tx, Ty, Tz;
        public float TQx, TQy, TQz, TQw = 1;
        public float Vx, Vy, Vz;
        public bool Seen;
    }

    private readonly GL _gl;
    private readonly ClientData _data;
    private readonly Dictionary<uint, Entity> _entities = new();

    public EntityRenderer(GL gl, ClientData data)
    {
        _gl = gl;
        _data = data;
    }

    public int Count => _entities.Count;

    public void Handle(ServerEvent evt)
    {
        switch (evt)
        {
            case ServerEvent.EntitySpawned s:
                Upsert(s);
                break;
            case ServerEvent.EntityStates states:
                foreach (var st in states.States)
                {
                    if (!_entities.TryGetValue(st.Id, out var e)) continue;
                    e.Tx = st.X; e.Ty = st.Y; e.Tz = st.Z;
                    e.TQx = st.Qx; e.TQy = st.Qy; e.TQz = st.Qz; e.TQw = st.Qw;
                    e.Vx = st.Vx; e.Vy = st.Vy; e.Vz = st.Vz;
                    if (!e.Seen)
                    {
                        e.Seen = true;
                        e.X = e.Tx; e.Y = e.Ty; e.Z = e.Tz;
                        e.Qx = e.TQx; e.Qy = e.TQy; e.Qz = e.TQz; e.Qw = e.TQw;
                    }
                }
                break;
            case ServerEvent.EntityDespawned d:
                if (_entities.Remove(d.Id, out var gone)) gone.Mesh.Dispose();
                break;
        }
    }

    private void Upsert(ServerEvent.EntitySpawned s)
    {
        if (_entities.Remove(s.Id, out var existing)) existing.Mesh.Dispose();

        var pass = BuildMesh(s.Blocks, s.DimX, s.DimY, s.DimZ);
        if (pass is null) return;
        var hull = ComputeInteriorColumns(s.Blocks, s.DimX, s.DimY, s.DimZ);
        _entities[s.Id] = new Entity
        {
            Id = s.Id,
            DimX = s.DimX, DimY = s.DimY, DimZ = s.DimZ,
            PivotX = s.PivotX, PivotY = s.PivotY, PivotZ = s.PivotZ,
            Blocks = s.Blocks,
            Mesh = new ChunkMesh(_gl, pass),
            HullRects = hull is { } h ? DecomposeRects(h.Interior, s.DimX, s.DimZ) : null,
            DeckY = hull?.DeckY ?? 0,
            X = (float)s.X, Y = (float)s.Y, Z = (float)s.Z,
            Qx = s.Qx, Qy = s.Qy, Qz = s.Qz, Qw = s.Qw,
            Tx = (float)s.X, Ty = (float)s.Y, Tz = (float)s.Z,
            TQx = s.Qx, TQy = s.Qy, TQz = s.Qz, TQw = s.Qw,
            Seen = true,
        };
    }

    public void Update(float dt)
    {
        float alpha = 1f - MathF.Exp(-dt * 14f);
        foreach (var e in _entities.Values)
        {
            e.X += (e.Tx - e.X) * alpha;
            e.Y += (e.Ty - e.Y) * alpha;
            e.Z += (e.Tz - e.Z) * alpha;
            e.Qx += (e.TQx - e.Qx) * alpha;
            e.Qy += (e.TQy - e.Qy) * alpha;
            e.Qz += (e.TQz - e.Qz) * alpha;
            e.Qw += (e.TQw - e.Qw) * alpha;
            float len = MathF.Sqrt(e.Qx * e.Qx + e.Qy * e.Qy + e.Qz * e.Qz + e.Qw * e.Qw);
            if (len > 1e-6f) { e.Qx /= len; e.Qy /= len; e.Qz /= len; e.Qw /= len; }
        }
    }

    public float[] GetModelMatrix(uint id)
    {
        if (!_entities.TryGetValue(id, out var e)) return Mat4.Identity();
        return ModelMatrix(e);
    }

    /// <summary>Nearest voxel hit on any entity grid within reach.</summary>
    public EntityRaycastHit? Raycast(
        double ox, double oy, double oz, double dx, double dy, double dz, double maxDistance)
    {
        EntityRaycastHit? best = null;
        foreach (var e in _entities.Values)
        {
            var localEye = EntityGridRaycast.WorldToLocal(
                new System.Numerics.Vector3((float)ox, (float)oy, (float)oz),
                new System.Numerics.Vector3(e.X, e.Y, e.Z),
                new System.Numerics.Quaternion(e.Qx, e.Qy, e.Qz, e.Qw),
                new System.Numerics.Vector3(e.PivotX, e.PivotY, e.PivotZ));
            var localDir = EntityGridRaycast.LocalDirection(
                new System.Numerics.Vector3((float)dx, (float)dy, (float)dz),
                new System.Numerics.Quaternion(e.Qx, e.Qy, e.Qz, e.Qw));

            var hit = EntityGridRaycast.CastLocal(
                localEye.X, localEye.Y, localEye.Z,
                localDir.X, localDir.Y, localDir.Z,
                maxDistance,
                e.DimX, e.DimY, e.DimZ,
                e.Blocks, IsTargetable);
            if (hit is null) continue;
            if (best is not null && hit.Value.Distance >= best.Value.Distance) continue;
            best = new EntityRaycastHit(e.Id, hit.Value.LocalX, hit.Value.LocalY, hit.Value.LocalZ,
                hit.Value.Nx, hit.Value.Ny, hit.Value.Nz, hit.Value.Distance);
        }
        return best;
    }

    public bool IsEmptyCell(uint entityId, int lx, int ly, int lz)
    {
        if (!_entities.TryGetValue(entityId, out var e)) return false;
        if (lx < 0 || ly < 0 || lz < 0 || lx >= e.DimX || ly >= e.DimY || lz >= e.DimZ) return true;
        return e.Blocks[(ly * e.DimZ + lz) * e.DimX + lx] == 0;
    }

    private bool IsTargetable(ushort id) =>
        id != 0 && _data.Blocks.Get(id).Collision != Collision.Liquid;

    public void Render(GlShader shader)
    {
        shader.SetVec3("uChunkOrigin", 0, 0, 0);
        foreach (var e in _entities.Values)
        {
            shader.SetMatrix("uModel", ModelMatrix(e));
            e.Mesh.Draw();
        }
    }

    /// <summary>One water cut-out box: world→grid-local transform + local rect bounds.</summary>
    public readonly record struct WaterCutout(
        float[] Inv, float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ);

    /// <summary>
    /// Hull-interior rects of the nearest floating hulls, as cut-out boxes for the liquid
    /// shader (world sea is discarded inside them so a boat reads hollow from any camera).
    /// Nearest hulls claim the budget first. The box starts exactly at the hull bottom (no
    /// padding below), and only near-level hulls carve at all: a boat tilted by the physics
    /// gun sweeps its box through the surface at a shallow angle, honestly removing a long
    /// swath of sea over the dipped end — which reads as a seabed-deep hole "under" the
    /// boat. A settled floating boat is upright (buoyancy rights it), so it always carves.
    /// </summary>
    public List<WaterCutout> WaterCutouts(float camX, float camY, float camZ, int maxBoxes)
    {
        var hulls = new List<(float Dist, Entity E)>();
        foreach (var e in _entities.Values)
        {
            if (e.HullRects is null || e.HullRects.Count == 0 || e.DeckY <= 0) continue;
            // Y component of the grid's rotated up axis; < ~18° of tilt required.
            float upY = 1f - 2f * (e.Qx * e.Qx + e.Qz * e.Qz);
            if (upY < 0.95f) continue;
            float dx = e.X - camX, dy = e.Y - camY, dz = e.Z - camZ;
            hulls.Add((dx * dx + dy * dy + dz * dz, e));
        }
        hulls.Sort((a, b) => a.Dist.CompareTo(b.Dist));

        var result = new List<WaterCutout>();
        foreach (var (_, e) in hulls)
        {
            if (result.Count >= maxBoxes) break;
            // Inverse of T(pos)·R(q)·T(-pivot): T(pivot)·R(conj q)·T(-pos).
            var inv = Mat4.Multiply(
                Mat4.Multiply(Mat4.Translation(e.PivotX, e.PivotY, e.PivotZ),
                              Mat4.FromQuaternion(-e.Qx, -e.Qy, -e.Qz, e.Qw)),
                Mat4.Translation(-e.X, -e.Y, -e.Z));
            foreach (var (x0, z0, w, d) in e.HullRects!)
            {
                if (result.Count >= maxBoxes) break;
                result.Add(new WaterCutout(inv, x0, 0f, z0, x0 + w, e.DeckY, z0 + d));
            }
        }
        return result;
    }

    private static float[] ModelMatrix(Entity e) =>
        Mat4.Multiply(
            Mat4.Multiply(Mat4.Translation(e.X, e.Y, e.Z), Mat4.FromQuaternion(e.Qx, e.Qy, e.Qz, e.Qw)),
            Mat4.Translation(-e.PivotX, -e.PivotY, -e.PivotZ));

    private MeshPass? BuildMesh(ushort[] blocks, int dimX, int dimY, int dimZ)
    {
        (int dx, int dy, int dz)[] normals =
            [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];
        float[] faceBrightness = [0.6f, 0.6f, 1.0f, 0.5f, 0.8f, 0.8f];
        float[][][] faceCorners =
        [
            [[1,0,0],[1,1,0],[1,1,1],[1,0,1]],
            [[0,0,1],[0,1,1],[0,1,0],[0,0,0]],
            [[0,1,1],[1,1,1],[1,1,0],[0,1,0]],
            [[0,0,0],[1,0,0],[1,0,1],[0,0,1]],
            [[0,0,1],[1,0,1],[1,1,1],[0,1,1]],
            [[1,0,0],[0,0,0],[0,1,0],[1,1,0]],
        ];
        float[][] faceUv = [[0, 0], [1, 0], [1, 1], [0, 1]];

        int Index(int x, int y, int z) => (y * dimZ + z) * dimX + x;
        ushort At(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= dimX || y >= dimY || z >= dimZ) return 0;
            return blocks[Index(x, y, z)];
        }

        var verts = new List<float>();
        var indices = new List<uint>();
        for (int y = 0; y < dimY; y++)
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            ushort id = At(x, y, z);
            if (id == 0) continue;
            bool emissive = _data.EmissiveMask[id] == 1;
            for (int f = 0; f < 6; f++)
            {
                var (nx, ny, nz) = normals[f];
                if (At(x + nx, y + ny, z + nz) != 0) continue;
                int layer = _data.RenderTable[id * 6 + f];
                float brightness = faceBrightness[f] + (emissive ? 4f : 0f);
                uint baseIndex = (uint)(verts.Count / 7);
                for (int c = 0; c < 4; c++)
                {
                    var corner = faceCorners[f][c];
                    verts.AddRange([x + corner[0], y + corner[1], z + corner[2],
                        faceUv[c][0], faceUv[c][1], layer, brightness]);
                }
                indices.AddRange([baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3]);
            }
        }
        if (indices.Count == 0) return null;
        return new MeshPass { Vertices = [.. verts], Indices = [.. indices] };
    }

    /// <summary>
    /// Greedy rectangle cover of the interior-column mask: each rect extends its first
    /// unclaimed cell right along X, then forward along Z while the full row matches.
    /// Boat hulls are near-convex, so this yields a handful of rects.
    /// </summary>
    public static List<(int X0, int Z0, int W, int D)> DecomposeRects(bool[] mask, int dimX, int dimZ)
    {
        var claimed = new bool[dimX * dimZ];
        var rects = new List<(int, int, int, int)>();
        bool Free(int x, int z) => mask[z * dimX + x] && !claimed[z * dimX + x];

        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            if (!Free(x, z)) continue;
            int w = 1;
            while (x + w < dimX && Free(x + w, z)) w++;
            int d = 1;
            bool RowFree(int zz) { for (int i = 0; i < w; i++) if (!Free(x + i, zz)) return false; return true; }
            while (z + d < dimZ && RowFree(z + d)) d++;
            for (int j = 0; j < d; j++)
            for (int i = 0; i < w; i++)
                claimed[(z + j) * dimX + (x + i)] = true;
            rects.Add((x, z, w, d));
        }
        return rects;
    }

    /// <summary>Chebyshev radius for the morphological close — seals hull gaps/notches up to ~2R wide.</summary>
    private const int HullCloseRadius = 2;

    /// <summary>
    /// Footprint columns to plug against the world water. A column that holds any block is part of
    /// the hull outline; this outline is morphologically closed (dilate then erode) to seal narrow
    /// gaps and concave notches, and the open sea is flood-filled inward from the (padded) border.
    /// Every column the sea can't reach — hull walls, the floor, and the enclosed cockpit alike —
    /// is masked, because a boat with a floor has world water sitting in the cockpit *above* that
    /// floor and every interior column then holds a block. The plug rises to the tallest block so
    /// it always clears the interior waterline. Null when nothing is enclosed (open water only).
    /// </summary>
    public static (bool[] Interior, int DeckY)? ComputeInteriorColumns(ushort[] blocks, int dimX, int dimY, int dimZ)
    {
        int n = dimX * dimZ;
        var wall = new bool[n];
        var top = new int[n];
        int maxTop = -1;
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            int t = TopSolidY(blocks, dimX, dimY, dimZ, x, z);
            top[z * dimX + x] = t;
            if (t < 0) continue;
            wall[z * dimX + x] = true;
            if (t > maxTop) maxTop = t;
        }
        if (maxTop < 0) return null;

        // Work on a padded footprint so the hull never touches the grid edge: the sea can then
        // flood cleanly all the way around it and morphology has no border ambiguity.
        int m = HullCloseRadius + 1;
        int px = dimX + 2 * m, pz = dimZ + 2 * m, pn = px * pz;
        var padWall = new bool[pn];
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
            if (wall[z * dimX + x]) padWall[(z + m) * px + (x + m)] = true;

        bool[] closed = Close(padWall, px, pz, HullCloseRadius);

        // Flood-fill the open sea inward from the padded border (all empty) around the hull.
        var sea = new bool[pn];
        var queue = new Queue<int>();
        void Seed(int x, int z) { int c = z * px + x; if (!closed[c] && !sea[c]) { sea[c] = true; queue.Enqueue(c); } }
        for (int x = 0; x < px; x++) { Seed(x, 0); Seed(x, pz - 1); }
        for (int z = 0; z < pz; z++) { Seed(0, z); Seed(px - 1, z); }
        int[] ox = [1, -1, 0, 0], oz = [0, 0, 1, -1];
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int cx = c % px, cz = c / px;
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + ox[k], nz = cz + oz[k];
                if (nx >= 0 && nz >= 0 && nx < px && nz < pz) Seed(nx, nz);
            }
        }

        // Every footprint column the sea can't reach is part of the boat -> mask it. Map back.
        // Deck height comes from the *most common* height of the outer hull rim (masked columns
        // touching the sea): the plug tops out at the gunwale, and a tall mast or a sail plane
        // whose end grazes the rim is outvoted instead of pushing the mask above the hull.
        var interior = new bool[n];
        bool any = false;
        var rimTops = new Dictionary<int, int>();
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            if (sea[(z + m) * px + (x + m)]) continue;
            interior[z * dimX + x] = true;
            any = true;

            bool onRim = sea[(z + m) * px + (x + m - 1)] || sea[(z + m) * px + (x + m + 1)]
                      || sea[(z + m - 1) * px + (x + m)] || sea[(z + m + 1) * px + (x + m)];
            if (onRim) rimTops[top[z * dimX + x]] = rimTops.GetValueOrDefault(top[z * dimX + x]) + 1;
        }
        if (!any) return null;

        int deckTop = maxTop, bestCount = 0;
        foreach (var (t, count) in rimTops)
            if (count > bestCount || (count == bestCount && t > deckTop)) { deckTop = t; bestCount = count; }

        return (interior, deckTop + 1);
    }

    /// <summary>Morphological close (dilate then erode) by a Chebyshev radius, sealing gaps up to ~2R wide.</summary>
    private static bool[] Close(bool[] src, int dimX, int dimZ, int r)
    {
        int n = dimX * dimZ;
        var dil = new bool[n];
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            if (!src[z * dimX + x]) continue;
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int nx = x + dx, nz = z + dz;
                if (nx >= 0 && nz >= 0 && nx < dimX && nz < dimZ) dil[nz * dimX + nx] = true;
            }
        }
        var outv = new bool[n];
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            bool all = true;
            for (int dz = -r; dz <= r && all; dz++)
            for (int dx = -r; dx <= r && all; dx++)
            {
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= dimX || nz >= dimZ || !dil[nz * dimX + nx]) all = false;
            }
            outv[z * dimX + x] = all;
        }
        return outv;
    }

    private static int TopSolidY(ushort[] blocks, int dimX, int dimY, int dimZ, int x, int z)
    {
        for (int y = dimY - 1; y >= 0; y--)
            if (blocks[(y * dimZ + z) * dimX + x] != 0) return y;
        return -1;
    }

    public void Dispose()
    {
        foreach (var e in _entities.Values) e.Mesh.Dispose();
        _entities.Clear();
    }
}
