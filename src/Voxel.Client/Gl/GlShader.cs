using Silk.NET.OpenGL;

namespace Voxel.Client.Gl;

/// <summary>Compiled+linked GLSL program with cached uniform locations.</summary>
public sealed class GlShader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _program;
    private readonly Dictionary<string, int> _uniforms = new();

    public GlShader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        uint vs = Compile(ShaderType.VertexShader, vertexSource);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSource);
        _program = gl.CreateProgram();
        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        gl.LinkProgram(_program);
        gl.GetProgram(_program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            throw new InvalidOperationException($"shader link failed: {gl.GetProgramInfoLog(_program)}");
        }
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
    }

    private uint Compile(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            throw new InvalidOperationException($"{type} compile failed: {_gl.GetShaderInfoLog(shader)}");
        }
        return shader;
    }

    public void Use() => _gl.UseProgram(_program);

    private int Location(string name)
    {
        if (!_uniforms.TryGetValue(name, out int loc))
        {
            loc = _gl.GetUniformLocation(_program, name);
            _uniforms[name] = loc;
        }
        return loc;
    }

    public void SetMatrix(string name, float[] columnMajor) =>
        _gl.UniformMatrix4(Location(name), 1, transpose: false, in columnMajor[0]);

    public void SetVec2(string name, float x, float y) => _gl.Uniform2(Location(name), x, y);

    public void SetVec3(string name, float x, float y, float z) => _gl.Uniform3(Location(name), x, y, z);

    public void SetFloat4(string name, float x, float y, float z, float w) => _gl.Uniform4(Location(name), x, y, z, w);

    public void SetFloat(string name, float v) => _gl.Uniform1(Location(name), v);

    public void SetInt(string name, int v) => _gl.Uniform1(Location(name), v);

    public void Dispose() => _gl.DeleteProgram(_program);
}
