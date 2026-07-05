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
    private sealed class Entity
    {
        public required int DimX, DimY, DimZ;
        public required ChunkMesh Mesh;
        // Rendered (smoothed) pose.
        public float X, Y, Z;
        public float Qx, Qy, Qz, Qw = 1;
        // Target pose from the latest state + velocity for extrapolation.
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
                Spawn(s);
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

    private void Spawn(ServerEvent.EntitySpawned s)
    {
        if (_entities.ContainsKey(s.Id)) return;
        var pass = BuildMesh(s.Blocks, s.DimX, s.DimY, s.DimZ);
        if (pass is null) return; // all air — nothing to draw
        _entities[s.Id] = new Entity
        {
            DimX = s.DimX, DimY = s.DimY, DimZ = s.DimZ,
            Mesh = new ChunkMesh(_gl, pass),
            X = (float)s.X, Y = (float)s.Y, Z = (float)s.Z,
            Qx = s.Qx, Qy = s.Qy, Qz = s.Qz, Qw = s.Qw,
            Tx = (float)s.X, Ty = (float)s.Y, Tz = (float)s.Z,
            TQx = s.Qx, TQy = s.Qy, TQz = s.Qz, TQw = s.Qw,
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

    /// <summary>Draws all entities with the chunk shader; caller has set the shared lighting uniforms.</summary>
    public void Render(GlShader shader)
    {
        shader.SetVec3("uChunkOrigin", 0, 0, 0);
        foreach (var e in _entities.Values)
        {
            // model = T(pos) * R(quat) * T(-halfDims): center the grid on the body.
            float[] model = Mat4.Multiply(
                Mat4.Multiply(Mat4.Translation(e.X, e.Y, e.Z), Mat4.FromQuaternion(e.Qx, e.Qy, e.Qz, e.Qw)),
                Mat4.Translation(-e.DimX / 2f, -e.DimY / 2f, -e.DimZ / 2f));
            shader.SetMatrix("uModel", model);
            e.Mesh.Draw();
        }
    }

    // Simple per-block cube mesh (chunk vertex format: pos3 uv2 meta2). Faces
    // hidden against solid same-grid neighbors; grid edges are exposed to air.
    private MeshPass? BuildMesh(ushort[] blocks, int dimX, int dimY, int dimZ)
    {
        // FACE_DIRS order px,nx,py,ny,pz,nz.
        (int dx, int dy, int dz)[] normals =
            [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];
        float[] faceBrightness = [0.6f, 0.6f, 1.0f, 0.5f, 0.8f, 0.8f];
        // 4 CCW corners per face (unit cube), matching the greedy mesher winding.
        float[][][] faceCorners =
        [
            [[1,0,0],[1,1,0],[1,1,1],[1,0,1]], // px
            [[0,0,1],[0,1,1],[0,1,0],[0,0,0]], // nx
            [[0,1,1],[1,1,1],[1,1,0],[0,1,0]], // py
            [[0,0,0],[1,0,0],[1,0,1],[0,0,1]], // ny
            [[0,0,1],[1,0,1],[1,1,1],[0,1,1]], // pz
            [[1,0,0],[0,0,0],[0,1,0],[1,1,0]], // nz
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
                if (At(x + nx, y + ny, z + nz) != 0) continue; // hidden by neighbor
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

    public void Dispose()
    {
        foreach (var e in _entities.Values) e.Mesh.Dispose();
        _entities.Clear();
    }
}
