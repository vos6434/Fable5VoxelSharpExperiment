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
        /// <summary>Depth-only "lid" over enclosed hull columns; hides world water seen through an open deck.</summary>
        public ChunkMesh? WaterMask;
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
                if (_entities.Remove(d.Id, out var gone)) { gone.Mesh.Dispose(); gone.WaterMask?.Dispose(); }
                break;
        }
    }

    private void Upsert(ServerEvent.EntitySpawned s)
    {
        if (_entities.Remove(s.Id, out var existing)) { existing.Mesh.Dispose(); existing.WaterMask?.Dispose(); }

        var pass = BuildMesh(s.Blocks, s.DimX, s.DimY, s.DimZ);
        if (pass is null) return;
        var maskPass = BuildWaterMask(s.Blocks, s.DimX, s.DimY, s.DimZ);
        _entities[s.Id] = new Entity
        {
            Id = s.Id,
            DimX = s.DimX, DimY = s.DimY, DimZ = s.DimZ,
            PivotX = s.PivotX, PivotY = s.PivotY, PivotZ = s.PivotZ,
            Blocks = s.Blocks,
            Mesh = new ChunkMesh(_gl, pass),
            WaterMask = maskPass is null ? null : new ChunkMesh(_gl, maskPass),
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

    /// <summary>
    /// Draws the enclosed-hull lids. The caller masks color writes so only depth lands,
    /// occluding the world-water surface that would otherwise show through an open deck.
    /// </summary>
    public void RenderWaterMask(GlShader shader)
    {
        shader.SetVec3("uChunkOrigin", 0, 0, 0);
        foreach (var e in _entities.Values)
        {
            if (e.WaterMask is null) continue;
            shader.SetMatrix("uModel", ModelMatrix(e));
            e.WaterMask.Draw();
        }
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

    /// <summary>Horizontal lid quads over each enclosed hull column, at deck height (local space).</summary>
    private static MeshPass? BuildWaterMask(ushort[] blocks, int dimX, int dimY, int dimZ)
    {
        if (ComputeInteriorColumns(blocks, dimX, dimY, dimZ) is not var (interior, deckY)) return null;

        var verts = new List<float>();
        var indices = new List<uint>();
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            if (!interior[z * dimX + x]) continue;
            uint b = (uint)(verts.Count / 7);
            // pos(3), uv(2), layer, brightness — only position matters (color is masked off).
            void V(int px, int pz) => verts.AddRange([px, deckY, pz, 0f, 0f, 0f, 1f]);
            V(x, z); V(x + 1, z); V(x + 1, z + 1); V(x, z + 1);
            indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        if (indices.Count == 0) return null;
        return new MeshPass { Vertices = [.. verts], Indices = [.. indices] };
    }

    /// <summary>
    /// Enclosed hull columns for the water lid: a 2D flood-fill over the footprint (a column is a
    /// "wall" if it holds any block) marks columns reachable from the grid edge as open sea; the
    /// unreached empty columns are the hull interior. Deck height is one above the shortest wall
    /// bordering that interior, so a tall mast doesn't lift the lid. Null when nothing is enclosed.
    /// </summary>
    public static (bool[] Interior, int DeckY)? ComputeInteriorColumns(ushort[] blocks, int dimX, int dimY, int dimZ)
    {
        int n = dimX * dimZ;
        var wall = new bool[n];
        var top = new int[n];
        Array.Fill(top, -1);
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        for (int y = 0; y < dimY; y++)
            if (blocks[(y * dimZ + z) * dimX + x] != 0) { wall[z * dimX + x] = true; top[z * dimX + x] = y; }

        var exterior = new bool[n];
        var queue = new Queue<int>();
        void Seed(int x, int z) { int c = z * dimX + x; if (!wall[c] && !exterior[c]) { exterior[c] = true; queue.Enqueue(c); } }
        for (int x = 0; x < dimX; x++) { Seed(x, 0); Seed(x, dimZ - 1); }
        for (int z = 0; z < dimZ; z++) { Seed(0, z); Seed(dimX - 1, z); }

        int[] ox = [1, -1, 0, 0], oz = [0, 0, 1, -1];
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int cx = c % dimX, cz = c / dimX;
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + ox[k], nz = cz + oz[k];
                if (nx < 0 || nz < 0 || nx >= dimX || nz >= dimZ) continue;
                Seed(nx, nz);
            }
        }

        var interior = new bool[n];
        bool any = false;
        int deckTop = int.MaxValue;
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            int c = z * dimX + x;
            if (wall[c] || exterior[c]) continue;
            interior[c] = true;
            any = true;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + ox[k], nz = z + oz[k];
                if (nx < 0 || nz < 0 || nx >= dimX || nz >= dimZ) continue;
                int nc = nz * dimX + nx;
                if (wall[nc] && top[nc] >= 0) deckTop = Math.Min(deckTop, top[nc]);
            }
        }
        if (!any) return null;
        return (interior, deckTop == int.MaxValue ? dimY : deckTop + 1);
    }

    public void Dispose()
    {
        foreach (var e in _entities.Values) { e.Mesh.Dispose(); e.WaterMask?.Dispose(); }
        _entities.Clear();
    }
}
