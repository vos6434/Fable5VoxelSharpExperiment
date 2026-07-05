using Silk.NET.OpenGL;

namespace Voxel.Client.Gl;

/// <summary>Position-only mesh for the flat-color shader (lines or triangles).</summary>
public sealed class ColorMesh : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly PrimitiveType _mode;
    private readonly int _indexCount;

    public unsafe ColorMesh(GL gl, float[] positions, uint[] indices, PrimitiveType mode)
    {
        _gl = gl;
        _mode = mode;
        _indexCount = indices.Length;

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, positions, BufferUsageARB.StaticDraw);
        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, indices, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
    }

    /// <summary>Unit cube [0,1]^3 as triangles (remote player bodies).</summary>
    public static ColorMesh UnitCubeTriangles(GL gl)
    {
        float[] p =
        [
            0,0,0, 1,0,0, 1,1,0, 0,1,0, // z=0
            0,0,1, 1,0,1, 1,1,1, 0,1,1, // z=1
        ];
        uint[] i =
        [
            0,2,1, 0,3,2,   // -z
            4,5,6, 4,6,7,   // +z
            0,1,5, 0,5,4,   // -y
            3,6,2, 3,7,6,   // +y
            0,4,7, 0,7,3,   // -x
            1,2,6, 1,6,5,   // +x
        ];
        return new ColorMesh(gl, p, i, PrimitiveType.Triangles);
    }

    /// <summary>Unit cube [0,1]^3 as line edges (block target highlight).</summary>
    public static ColorMesh UnitCubeLines(GL gl)
    {
        float[] p =
        [
            0,0,0, 1,0,0, 1,1,0, 0,1,0,
            0,0,1, 1,0,1, 1,1,1, 0,1,1,
        ];
        uint[] i =
        [
            0,1, 1,2, 2,3, 3,0,
            4,5, 5,6, 6,7, 7,4,
            0,4, 1,5, 2,6, 3,7,
        ];
        return new ColorMesh(gl, p, i, PrimitiveType.Lines);
    }

    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(_mode, (uint)_indexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}
