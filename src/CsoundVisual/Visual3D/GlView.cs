using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.OpenGLES;
using CsoundVisual.Theming;
using CsoundVisual.Localization;

namespace CsoundVisual.Visual3D;

/// <summary>A 3D scene drawn by a <see cref="GlView"/>.</summary>
/// <remarks>
/// Implementations live in <c>Scenes.cs</c> (<see cref="WaterfallScene"/>, <see cref="XyzPadScene"/>,
/// <see cref="SpatialScene"/>). A scene is pure geometry plus input handling: it never touches GL
/// directly and never talks to Csound; it reads the audio through <see cref="AudioFeed"/> and
/// reports user gestures through its own events, which the 3D window turns into channel values.
/// World space is Y-up and centred on the origin; each scene fits within about ±2 units so the
/// default camera (<see cref="GlView"/>, distance 5.6) frames it.
/// <see cref="Render"/>/<see cref="AddLabels"/> are called during drawing, the input members from
/// pointer events; none of them may block.
/// </remarks>
public interface IScene3D
{
    /// <summary>Name shown in the scene picker.</summary>
    string Name { get; }

    /// <summary>One-paragraph help shown under the picker (and as tooltip).</summary>
    string Help { get; }

    /// <summary>True when left-drag is used by the scene (e.g. to move a control); orbiting is then on the right button.</summary>
    bool Interactive { get; }

    /// <summary>Adds the frame's geometry. <paramref name="time"/> is seconds since the view started.</summary>
    void Render(GlRenderer renderer, double time);

    /// <summary>Left-drag by (dx, dy) normalised to the view size (≈ −1…1 for a full sweep).</summary>
    void Drag(float dx, float dy, bool shift);

    /// <summary>
    /// Left-drag with the camera context, so a scene can move things relative to what the user
    /// sees (the default ignores the camera and calls <see cref="Drag(float, float, bool)"/>).
    /// </summary>
    void Drag(in DragContext drag) => Drag(drag.Dx, drag.Dy, drag.Shift);

    /// <summary>Mouse wheel while hovering (positive = away / up).</summary>
    void Wheel(float delta);

    /// <summary>
    /// Adds the scene's text labels (axis numbers, units, names) in world coordinates. They are
    /// drawn by <see cref="LabelOverlay"/> on top of the GPU image, projected with the same camera.
    /// </summary>
    void AddLabels(List<Label3D> labels) { }
}

/// <summary>
/// One step of a left-drag: the pointer motion normalised to the view size, the Shift state,
/// the camera yaw, and the world-space ray under the pointer (for picking on a plane).
/// </summary>
/// <param name="Dx">Horizontal motion (view widths, right = positive).</param>
/// <param name="Dy">Vertical motion (view heights, down = positive).</param>
/// <param name="Shift">True while Shift is held.</param>
/// <param name="Yaw">Camera yaw (radians); the camera sits at (sin yaw, ·, cos yaw).</param>
/// <param name="RayOrigin">A point of the ray under the pointer.</param>
/// <param name="RayDirection">Normalised direction of that ray (away from the camera).</param>
public readonly record struct DragContext(float Dx, float Dy, bool Shift, float Yaw, Vector3 RayOrigin, Vector3 RayDirection)
{
    /// <summary>
    /// Where the pointer ray meets the horizontal plane at height <paramref name="y"/>, or
    /// <c>null</c> when the ray is (almost) parallel to it or the plane is behind the camera.
    /// </summary>
    public Vector3? HitHorizontalPlane(float y)
    {
        // Ray P(t) = O + t·D meets the plane Y = y at t = (y − O.y) / D.y; t ≤ 0 is behind the origin.
        if (MathF.Abs(RayDirection.Y) < 1e-4f) return null;
        float t = (y - RayOrigin.Y) / RayDirection.Y;
        return t > 0 ? RayOrigin + t * RayDirection : null;
    }
}

/// <summary>A text label anchored at a 3D point.</summary>
/// <param name="Position">World position of the anchor (the text is centred on it).</param>
/// <param name="Text">Text to show.</param>
/// <param name="Strong">True for titles (brighter, bold); false for tick numbers.</param>
public readonly record struct Label3D(Vector3 Position, string Text, bool Strong = false);

/// <summary>
/// Hosts an <see cref="IScene3D"/> in an Avalonia <see cref="OpenGlControlBase"/>: GPU rendering
/// with Silk.NET over Avalonia's context (OpenGL ES 3 via ANGLE on Windows), an orbit camera
/// (right-drag or left-drag in non-interactive scenes), wheel zoom, and continuous redraw.
/// </summary>
/// <remarks>
/// If the context cannot be created or the shaders fail, <see cref="Failed"/> is raised once and
/// the view stays blank; the hosting window then shows a message instead of crashing. Context
/// loss (<see cref="OnOpenGlLost"/>) is reported the same way.
/// <para><b>Camera.</b> An orbit camera looking at the origin from spherical coordinates
/// (<see cref="Yaw"/>, <see cref="Pitch"/>, <see cref="Distance"/>), Y up, 45° vertical field of
/// view, near/far planes 0.05/100 world units. Built with System.Numerics (right-handed, row vectors).</para>
/// <para><b>Frame loop.</b> Each <c>OnOpenGlRender</c> sets the GL state, lets the scene add its
/// geometry to <see cref="GlRenderer"/>, flushes it and immediately requests the next frame
/// (<c>RequestNextFrameRendering</c>), so the view redraws continuously at the compositor's rate
/// while visible. Hosted by <c>Views/Visual3DWindow.cs</c> together with a <see cref="LabelOverlay"/>.</para>
/// </remarks>
public sealed class GlView : OpenGlControlBase, ICustomHitTest
{
    /// <summary>Initial camera, restored by a double-click.</summary>
    const float HomeYaw = 0.6f, HomePitch = 0.5f, HomeDistance = 5.6f;

    /// <summary>Silk.NET function table, bound to Avalonia's context in <see cref="OnOpenGlInit"/>.</summary>
    GL? gl;

    /// <summary>The renderer; null before init, after deinit or when init failed.</summary>
    GlRenderer? renderer;

    /// <summary>Set once by <see cref="Fail"/>; stops rendering and keeps <see cref="Failed"/> from firing twice.</summary>
    bool failed;

    /// <summary>Time base passed to <see cref="IScene3D.Render"/> and used for the FPS counter.</summary>
    readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>Pointer position of the previous move while a button is held (DIP); null when no drag is active.</summary>
    Point? lastPointer;

    /// <summary>True when the current drag rotates the camera instead of being passed to the scene.</summary>
    bool orbiting;

    /// <summary>Scene to draw.</summary>
    public IScene3D? Scene { get; set; }

    /// <summary>Camera yaw around the vertical axis (radians).</summary>
    public float Yaw { get; set; } = HomeYaw;

    /// <summary>Camera pitch (radians, clamped to ±1.45).</summary>
    public float Pitch { get; set; } = HomePitch;

    /// <summary>Camera distance from the target (world units; the wheel keeps it within 1.5…20).</summary>
    public float Distance { get; set; } = HomeDistance;

    /// <summary>
    /// Makes the whole control receive the mouse.
    /// </summary>
    /// <remarks>
    /// BUG FIXED HERE: the GL image is drawn in a child composition visual, so the control itself
    /// has no drawn content and Avalonia's hit test went straight through it. Pointer events never
    /// arrived: the camera could not be rotated and the XYZ pad / panner could not be dragged.
    /// </remarks>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Puts the camera back where it started.</summary>
    public void ResetCamera()
    {
        Yaw = HomeYaw;
        Pitch = HomePitch;
        Distance = HomeDistance;
    }

    /// <summary>
    /// The camera's view × projection matrix for an image of the given size. Used by the GPU pass
    /// and by <see cref="LabelOverlay"/>, so labels sit exactly on the geometry.
    /// </summary>
    /// <param name="width">Image width (pixels or DIP: only the aspect ratio matters).</param>
    /// <param name="height">Image height, same unit as <paramref name="width"/>.</param>
    public Matrix4x4 ViewProjection(double width, double height)
    {
        // Spherical → Cartesian: pitch lifts the eye above the XZ plane, yaw turns it around Y
        // (yaw 0 = on the +Z axis looking towards −Z).
        var eye = new Vector3(
            Distance * MathF.Cos(Pitch) * MathF.Sin(Yaw),
            Distance * MathF.Sin(Pitch),
            Distance * MathF.Cos(Pitch) * MathF.Cos(Yaw));
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, (float)(width / Math.Max(1, height)), 0.05f, 100f);
        return view * projection; // row vectors: v · view · projection
    }

    /// <summary>Raised (once) when OpenGL is unavailable or failed, with a readable reason.</summary>
    public event Action<string>? Failed;

    /// <summary>Frames rendered since <see cref="frameCountStart"/>.</summary>
    int framesThisSecond;

    /// <summary><see cref="clock"/> time (s) at which the current one-second FPS window began.</summary>
    double frameCountStart;

    /// <summary>Frames the GPU drew during the last second.</summary>
    public int Fps { get; private set; }

    /// <summary>Description of the active context ("OpenGL ES 3.0 (ANGLE)…"), once initialised.</summary>
    public string? ContextInfo { get; private set; }

    /// <inheritdoc/>
    /// <remarks>Binds Silk.NET to Avalonia's context (function pointers resolved through Avalonia's
    /// <c>GetProcAddress</c>, so no separate GL loader or window is needed) and builds the renderer.</remarks>
    protected override void OnOpenGlInit(GlInterface glInterface)
    {
        try
        {
            gl = GL.GetApi(glInterface.GetProcAddress);
            bool es = GlVersion.Type == GlProfileType.OpenGLES;
            renderer = new GlRenderer(gl, es);
            ContextInfo = $"{(es ? "OpenGL ES" : "OpenGL")} {GlVersion.Major}.{GlVersion.Minor} · {gl.GetStringS(StringName.Renderer)}";
        }
        catch (Exception ex)
        {
            Fail(Loc.S("No se pudo iniciar OpenGL: ", "Could not start OpenGL: ") + ex.Message);
        }
    }

    /// <inheritdoc/>
    protected override void OnOpenGlDeinit(GlInterface glInterface)
    {
        renderer?.Dispose();
        renderer = null;
        gl = null;
    }

    /// <inheritdoc/>
    protected override void OnOpenGlLost()
    {
        base.OnOpenGlLost();
        Fail(Loc.S("Se perdió el contexto de la GPU (¿cambió el driver o la pantalla?). Cierra y vuelve a abrir la vista 3D.", "The GPU context was lost (did the driver or display change?). Close and reopen the 3D view."));
    }

    /// <summary>Enters the failed state and raises <see cref="Failed"/> the first time only.</summary>
    void Fail(string reason)
    {
        if (failed) return;
        failed = true;
        Failed?.Invoke(reason);
    }

    /// <inheritdoc/>
    protected override void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (failed || gl is null || renderer is null)
            return;
        try
        {
            // Bounds are in DIP; the framebuffer is in physical pixels (DPI scaling, e.g. 1.5 at 150 %).
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            int w = Math.Max(1, (int)(Bounds.Width * scaling)), h = Math.Max(1, (int)(Bounds.Height * scaling));
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            gl.Viewport(0, 0, (uint)w, (uint)h);
            var clear = AppTheme.Vector("BgDeep");
            gl.ClearColor(clear.X, clear.Y, clear.Z, 1f);
            gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
            // State is set every frame: Avalonia shares the context and may leave other state behind.
            // Lequal lets a line drawn exactly on a surface (grid on the terrain) pass the depth test.
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Lequal);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            double now = clock.Elapsed.TotalSeconds;
            framesThisSecond++;
            if (now - frameCountStart >= 1)
            {
                Fps = framesThisSecond;
                framesThisSecond = 0;
                frameCountStart = now;
            }
            Scene?.Render(renderer, now);
            renderer.Flush(ViewProjection(w, h));
        }
        catch (Exception ex)
        {
            Fail(Loc.S("Error de render 3D: ", "3D render error: ") + ex.Message);
        }
        RequestNextFrameRendering(); // continuous animation (also after a failure; drawing is skipped then)
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Chooses what the drag does: right/middle button always orbit; the left button orbits only
    /// in non-interactive scenes (otherwise it is passed to the scene). A left double-click in a
    /// non-interactive scene resets the camera. The pointer is captured so the drag continues
    /// outside the control.
    /// </remarks>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (e.ClickCount == 2 && props.IsLeftButtonPressed && Scene is not { Interactive: true })
        {
            ResetCamera();
            e.Handled = true;
            return;
        }
        orbiting = props.IsRightButtonPressed || props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && Scene is not { Interactive: true });
        lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (lastPointer is not { } last)
            return;
        var p = e.GetPosition(this);
        float dx = (float)((p.X - last.X) / Math.Max(1, Bounds.Width));
        float dy = (float)((p.Y - last.Y) / Math.Max(1, Bounds.Height));
        lastPointer = p;
        if (orbiting)
        {
            // A full-width drag turns the camera by 4 rad; pitch stops short of ±π/2 so the
            // look-at matrix never degenerates (eye parallel to the up vector).
            Yaw -= dx * 4f;
            Pitch = Math.Clamp(Pitch + dy * 3f, -1.45f, 1.45f);
        }
        else if (Scene is not null)
        {
            var (origin, direction) = PointerRay(p);
            Scene.Drag(new DragContext(dx, dy, e.KeyModifiers.HasFlag(KeyModifiers.Shift), Yaw, origin, direction));
        }
    }

    /// <summary>World-space ray through view point <paramref name="p"/> (inverse of the camera matrix).</summary>
    (Vector3 Origin, Vector3 Direction) PointerRay(Point p)
    {
        double w = Math.Max(1, Bounds.Width), h = Math.Max(1, Bounds.Height);
        Matrix4x4.Invert(ViewProjection(w, h), out var inverse);
        // View point → normalised device coordinates (−1…1, Y up). Unprojecting the same NDC point
        // at depth 0 (near plane) and 1 (far plane) gives two world points on the pointer ray.
        float x = (float)(2 * p.X / w - 1), y = (float)(1 - 2 * p.Y / h);
        static Vector3 Unproject(Vector4 v, Matrix4x4 m)
        {
            var r = Vector4.Transform(v, m);
            return new Vector3(r.X, r.Y, r.Z) / r.W;
        }
        var near = Unproject(new Vector4(x, y, 0, 1), inverse);
        var far = Unproject(new Vector4(x, y, 1, 1), inverse);
        return (near, Vector3.Normalize(far - near));
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        lastPointer = null;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc/>
    /// <remarks>Zooms (±10 % per notch) in non-interactive scenes or with Ctrl; otherwise the
    /// wheel belongs to the scene (e.g. the pad's Z axis).</remarks>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || Scene is not { Interactive: true })
            Distance = Math.Clamp(Distance * (e.Delta.Y > 0 ? 0.9f : 1.1f), 1.5f, 20f);
        else
            Scene.Wheel((float)e.Delta.Y);
        e.Handled = true;
    }
}

/// <summary>
/// Transparent layer over a <see cref="GlView"/> that draws the scene's <see cref="Label3D"/>s
/// (axis numbers, units) with Avalonia text, projected with the view's camera. It ignores the
/// mouse so dragging still reaches the GL view underneath. Call <see cref="Visual.InvalidateVisual"/>
/// every frame (the 3D window does it from its 16 ms timer, ≈ 60 Hz).
/// </summary>
/// <remarks>
/// The theme brushes are <see cref="AppTheme"/>'s stable per-role instances, recoloured in place
/// when the theme changes, so caching them in static fields keeps following the theme.
/// </remarks>
public sealed class LabelOverlay : Control
{
    /// <summary>Brush of tick numbers.</summary>
    static readonly IBrush Tick = AppTheme.Brush("Text");
    /// <summary>Brush of titles (<see cref="Label3D.Strong"/>).</summary>
    static readonly IBrush Title = AppTheme.Brush("AccentHi");
    /// <summary>Semi-transparent background plate (role <c>BgDeep</c> with alpha 0xB0) that keeps text readable over the geometry.</summary>
    static readonly IBrush Plate = AppTheme.Brush("BgDeep_B0");
    /// <summary>Typeface of tick numbers.</summary>
    static readonly Typeface Face = new("Segoe UI");
    /// <summary>Typeface of titles.</summary>
    static readonly Typeface Bold = new("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
    /// <summary>The view whose scene and camera are labelled.</summary>
    readonly GlView view;
    /// <summary>Scratch list refilled by the scene on every render (reused to avoid per-frame allocation of the list).</summary>
    readonly List<Label3D> labels = [];

    /// <summary>Creates the overlay for <paramref name="view"/>.</summary>
    public LabelOverlay(GlView view)
    {
        this.view = view;
        IsHitTestVisible = false;
        ClipToBounds = true; // labels of points that leave the view must not spill over the panels
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (view.Scene is null || !view.IsVisible) return;
        labels.Clear();
        view.Scene.AddLabels(labels);
        // The overlay covers the GL view exactly, so the same camera with this size's aspect ratio
        // projects each anchor onto the same spot as the GPU geometry (here in DIP).
        double w = Bounds.Width, h = Bounds.Height;
        var vp = view.ViewProjection(w, h);
        foreach (var label in labels)
        {
            var clip = Vector4.Transform(new Vector4(label.Position, 1), vp);
            if (clip.W <= 0.01f) continue; // behind the camera
            // Perspective divide → NDC, then NDC → overlay coordinates (Y flipped: screen Y grows down).
            double x = (clip.X / clip.W + 1) / 2 * w, y = (1 - clip.Y / clip.W) / 2 * h;
            if (x < -40 || y < -20 || x > w + 40 || y > h + 20) continue; // off screen
            var text = new FormattedText(label.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                label.Strong ? Bold : Face, label.Strong ? 13 : 11, label.Strong ? Title : Tick);
            var origin = new Point(x - text.Width / 2, y - text.Height / 2);
            context.DrawRectangle(Plate, null, new Rect(origin.X - 3, origin.Y - 1, text.Width + 6, text.Height + 2), 3, 3);
            context.DrawText(text, origin);
        }
    }
}
