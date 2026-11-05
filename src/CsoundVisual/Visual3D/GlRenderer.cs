using System.Numerics;
using Silk.NET.OpenGLES;
using CsoundVisual.Localization;

namespace CsoundVisual.Visual3D;

/// <summary>
/// Minimal immediate-style renderer for the 3D views: scenes add coloured lines, triangles and
/// points each frame, and <see cref="Flush"/> uploads them in one dynamic vertex buffer and
/// draws them with a single shader.
/// </summary>
/// <remarks>
/// <para>
/// Works on OpenGL ES 3.0 (ANGLE over Direct3D 11, Avalonia's default on Windows) and on desktop
/// OpenGL 3.3: only the shader header differs. Vertex layout: position (3 floats) + RGBA
/// colour (4 floats), 28 bytes. Triangles are drawn first, then lines and points, with alpha
/// blending and depth testing.
/// </para>
/// <para>
/// Matrices use System.Numerics (row vectors). Uploading them untransposed to a column-vector
/// GLSL shader is equivalent, so <c>viewProjection</c> is passed as-is.
/// </para>
/// <para>
/// <b>Threading and lifetime.</b> Every member must run with Avalonia's GL context current, i.e.
/// only from inside <see cref="GlView"/>'s <c>OnOpenGlInit</c> / <c>OnOpenGlRender</c> /
/// <c>OnOpenGlDeinit</c> callbacks. Created and disposed by <see cref="GlView"/>; scenes only receive it
/// as a drawing surface. GL state that is not program/buffer related (viewport, blending,
/// depth test, clear) is set by <see cref="GlView"/>, not here.
/// </para>
/// <para>
/// <b>Memory.</b> The CPU-side vertex lists are growable arrays that are reused between frames
/// (doubling on overflow, never shrinking), so a steady scene allocates nothing per frame. Each
/// primitive kind is uploaded with <c>glBufferData(DYNAMIC_DRAW)</c>, which lets the driver
/// orphan the previous storage instead of stalling on the GPU.
/// </para>
/// </remarks>
public sealed unsafe class GlRenderer : IDisposable
{
    /// <summary>Floats per vertex: x, y, z, r, g, b, a (28 bytes).</summary>
    const int Stride = 7;

    /// <summary>GL function table bound to Avalonia's context.</summary>
    readonly GL gl;

    /// <summary>Linked shader program, vertex array object and the single dynamic vertex buffer.</summary>
    readonly uint program, vao, vbo;

    /// <summary>Location of the <c>uMvp</c> uniform (view × projection; geometry is in world space).</summary>
    readonly int mvpLocation;

    /// <summary>Vertex lists for the current frame, <see cref="Stride"/> floats per vertex.</summary>
    float[] lines = new float[4096], tris = new float[4096], points = new float[1024];

    /// <summary>Vertices (not primitives) queued in each list since the last <see cref="Flush"/>.</summary>
    int lineCount, triCount, pointCount;

    /// <summary>Compiles the shaders and creates the vertex buffer.</summary>
    /// <param name="gl">Silk.NET GL bound to Avalonia's context.</param>
    /// <param name="isEs">True for OpenGL ES (ANGLE), false for desktop OpenGL.</param>
    /// <exception cref="InvalidOperationException">A shader failed to compile or link (message includes the driver log).</exception>
    public GlRenderer(GL gl, bool isEs)
    {
        // GLSL ES needs an explicit default float precision in the fragment shader; desktop GLSL
        // 3.30 has none, so only the header differs between the two dialects.
        this.gl = gl;
        string header = isEs ? "#version 300 es\nprecision mediump float;\n" : "#version 330 core\n";
        string vs = header + """
            in vec3 aPos;
            in vec4 aColor;
            uniform mat4 uMvp;
            out vec4 vColor;
            void main() {
                gl_Position = uMvp * vec4(aPos, 1.0);
                gl_PointSize = 7.0;
                vColor = aColor;
            }
            """;
        string fs = header + """
            in vec4 vColor;
            out vec4 fragColor;
            void main() { fragColor = vColor; }
            """;
        program = gl.CreateProgram();
        uint v = Compile(ShaderType.VertexShader, vs), f = Compile(ShaderType.FragmentShader, fs);
        gl.AttachShader(program, v);
        gl.AttachShader(program, f);
        // Fixed attribute slots (bound before linking) so the VAO set-up below can use 0 and 1.
        gl.BindAttribLocation(program, 0, "aPos");
        gl.BindAttribLocation(program, 1, "aColor");
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
            throw new InvalidOperationException(Loc.S("No se pudo enlazar el shader: ", "Could not link the shader: ") + gl.GetProgramInfoLog(program));
        gl.DeleteShader(v);
        gl.DeleteShader(f);
        mvpLocation = gl.GetUniformLocation(program, "uMvp");

        // The VAO records the interleaved layout once: attribute 0 = 3 floats at byte 0,
        // attribute 1 = 4 floats at byte 12, stride 28 bytes.
        vao = gl.GenVertexArray();
        vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Stride * 4, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, Stride * 4, (void*)12);
        gl.EnableVertexAttribArray(1);
        gl.BindVertexArray(0);
    }

    /// <summary>Compiles one shader stage; throws with the driver's info log on failure.</summary>
    uint Compile(ShaderType type, string source)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, source);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException(Loc.S($"No se pudo compilar el shader ({type}): ", $"Could not compile the shader ({type}): ") + gl.GetShaderInfoLog(s));
        return s;
    }

    /// <summary>Appends one vertex (position + RGBA colour, components 0…1) to a list, doubling it when full.</summary>
    static void Put(ref float[] buffer, ref int count, Vector3 p, Vector4 c)
    {
        if ((count + 1) * Stride > buffer.Length)
            Array.Resize(ref buffer, buffer.Length * 2);
        int i = count * Stride;
        buffer[i] = p.X; buffer[i + 1] = p.Y; buffer[i + 2] = p.Z;
        buffer[i + 3] = c.X; buffer[i + 4] = c.Y; buffer[i + 5] = c.Z; buffer[i + 6] = c.W;
        count++;
    }

    /// <summary>Adds a line segment.</summary>
    public void Line(Vector3 a, Vector3 b, Vector4 color) => Line(a, b, color, color);

    /// <summary>Adds a line segment with a colour gradient.</summary>
    public void Line(Vector3 a, Vector3 b, Vector4 colorA, Vector4 colorB)
    {
        Put(ref lines, ref lineCount, a, colorA);
        Put(ref lines, ref lineCount, b, colorB);
    }

    /// <summary>Adds a triangle with per-vertex colours.</summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector4 ca, Vector4 cb, Vector4 cc)
    {
        Put(ref tris, ref triCount, a, ca);
        Put(ref tris, ref triCount, b, cb);
        Put(ref tris, ref triCount, c, cc);
    }

    /// <summary>Adds a quad (two triangles) with per-corner colours.</summary>
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 ca, Vector4 cb, Vector4 cc, Vector4 cd)
    {
        Triangle(a, b, c, ca, cb, cc);
        Triangle(a, c, d, ca, cc, cd);
    }

    /// <summary>Adds a point sprite (7 pixels, size fixed in the vertex shader).</summary>
    public void Point(Vector3 p, Vector4 color) => Put(ref points, ref pointCount, p, color);

    /// <summary>Adds a UV sphere made of triangles.</summary>
    /// <param name="centre">Centre in world units.</param>
    /// <param name="radius">Radius in world units.</param>
    /// <param name="color">Base colour; each vertex is darkened by a fixed Lambert term (no lighting in the shader).</param>
    /// <param name="slices">Divisions around the vertical axis (longitude).</param>
    /// <param name="stacks">Divisions from pole to pole (latitude); t runs from −π/2 to +π/2.</param>
    public void Sphere(Vector3 centre, float radius, Vector4 color, int slices = 18, int stacks = 12)
    {
        for (int i = 0; i < stacks; i++)
        {
            float t0 = MathF.PI * i / stacks - MathF.PI / 2, t1 = MathF.PI * (i + 1) / stacks - MathF.PI / 2;
            for (int j = 0; j < slices; j++)
            {
                float p0 = 2 * MathF.PI * j / slices, p1 = 2 * MathF.PI * (j + 1) / slices;
                Vector3 P(float t, float p) => centre + radius * new Vector3(MathF.Cos(t) * MathF.Cos(p), MathF.Sin(t), MathF.Cos(t) * MathF.Sin(p));
                // Simple lighting from above-front so the sphere reads as 3D.
                Vector4 Shade(float t, float p)
                {
                    var n = Vector3.Normalize(P(t, p) - centre);
                    float l = 0.45f + 0.55f * MathF.Max(0, Vector3.Dot(n, Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.6f))));
                    return new Vector4(color.X * l, color.Y * l, color.Z * l, color.W);
                }
                Quad(P(t0, p0), P(t1, p0), P(t1, p1), P(t0, p1), Shade(t0, p0), Shade(t1, p0), Shade(t1, p1), Shade(t0, p1));
            }
        }
    }

    /// <summary>Uploads and draws everything added since the last flush.</summary>
    /// <param name="viewProjection">World → clip matrix (System.Numerics, row-vector convention).</param>
    /// <remarks>Opaque surfaces first so lines and points drawn on top of them depth-test against
    /// the terrain/sphere; the lists are then emptied for the next frame.</remarks>
    public void Flush(Matrix4x4 viewProjection)
    {
        gl.UseProgram(program);
        float* m = (float*)&viewProjection;
        gl.UniformMatrix4(mvpLocation, 1, false, m);
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        Draw(tris, triCount, PrimitiveType.Triangles);
        Draw(lines, lineCount, PrimitiveType.Lines);
        Draw(points, pointCount, PrimitiveType.Points);
        gl.BindVertexArray(0);
        lineCount = triCount = pointCount = 0;
    }

    /// <summary>Uploads one vertex list into the shared buffer and draws it (<paramref name="count"/> = vertices).</summary>
    void Draw(float[] data, int count, PrimitiveType type)
    {
        if (count == 0) return;
        fixed (float* p = data)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(count * Stride * 4), p, BufferUsageARB.DynamicDraw);
        gl.DrawArrays(type, 0, (uint)count);
    }

    /// <inheritdoc/>
    /// <remarks>Call with the context current (<c>OnOpenGlDeinit</c>).</remarks>
    public void Dispose()
    {
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        gl.DeleteProgram(program);
    }
}
